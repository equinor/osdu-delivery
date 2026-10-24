using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// The moves of a renamed pipeline's ledger (osdu/docs/reference/concepts/renaming-a-pipeline.md; LedgerMoves): asked by
// the repository sync, carried out here a slice at a time, by the control plane right after the sync and by the renamed
// pipeline's run before it reads its ledger, whichever comes first.
public sealed partial class OsduLedger
{
    /// <summary>
    /// How long a run of the renamed pipeline waits for a move another process is making into its ledger: long enough for a
    /// ledger of millions of records, a slice at a time.
    /// </summary>
    private const int MoveWaitMilliseconds = 15 * 60 * 1000;

    public async Task<IReadOnlyList<LedgerMoveResult>> CompleteLedgerMovesAsync(Guid? into, CancellationToken ct = default)
    {
        var pending = await PendingMovesAsync(into, ct).ConfigureAwait(false);
        var results = new List<LedgerMoveResult>(pending.Count);
        foreach (var (partition, activityId) in pending)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await CompleteMoveAsync(partition, activityId, wait: into is not null, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// The moves asked and not done, oldest first: into <paramref name="into"/> alone, found in its partition, or every
    /// one, a partition at a time, each one seek of the activities of a kind.
    /// </summary>
    private async Task<IReadOnlyList<(short Partition, long ActivityId)>> PendingMovesAsync(Guid? into, CancellationToken ct)
    {
        IReadOnlyList<short> partitions;
        if (into is { } flowId)
        {
            if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } kept || kept == Unassigned)
            {
                return [];
            }

            partitions = [kept];
        }
        else
        {
            partitions = await ReadAsync(db => db.DeliveryLedgerPartitions.AsNoTracking().Select(p => p.PartitionId).ToListAsync(ct), ct).ConfigureAwait(false);
        }

        var pending = new List<(short, long)>();
        foreach (var partition in partitions)
        {
            var ids = await ReadAsync(
                db => db.DeliveryActivities.AsNoTracking()
                    .Where(a => a.PartitionId == partition && a.Kind == LedgerMoves.Kind && a.Outcome == LedgerMoves.Running
                        && (into == null || a.FlowId == into))
                    .OrderBy(a => a.ActivityId)
                    .Select(a => a.ActivityId)
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            pending.AddRange(ids.Select(id => (partition, id)));
        }

        return pending;
    }

    /// <summary>
    /// Carries out one move under its lock: checks it before it begins, then supersedes the new identity's records the old
    /// ledger holds too, links every old record to the move, moves each table's rows and closes the move. Work in flight on
    /// either ledger leaves the move waiting: for the control plane it is taken up on its next pass, and a run of the renamed
    /// pipeline (<paramref name="wait"/>) is refused, so it never reads half a ledger.
    /// </summary>
    private async Task<LedgerMoveResult> CompleteMoveAsync(short partition, long activityId, bool wait, CancellationToken ct)
    {
        var name = await PartitionNameAsync(partition, ct).ConfigureAwait(false) ?? string.Empty;
        await using var db = Open();
        var activity = await db.DeliveryActivities.AsNoTracking()
            .FirstOrDefaultAsync(a => a.PartitionId == partition && a.ActivityId == activityId, ct).ConfigureAwait(false);
        var asked = LedgerMoves.Read(activity?.ParametersJson);
        if (activity is null || asked is null)
        {
            return new LedgerMoveResult(activityId, name, Guid.Empty, Guid.Empty, "failed", 0, 0, $"Activity {activityId} names no ledger move.");
        }

        var resource = "SqlFlow.Delivery.LedgerMove:" + asked.To.ToString("D");
        var granted = await SqlServerLedgerBulk.LockMoveAsync(db, resource, wait ? MoveWaitMilliseconds : 0, ct).ConfigureAwait(false);
        if (granted < 0)
        {
            if (wait)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The ledger of '{asked.FromFlow}' is being moved to '{asked.ToFlow}', which replaced it, and the move did not finish within {MoveWaitMilliseconds / 60_000} minutes (sp_getapplock answered {granted}), so nothing ran: a run reads its ledger whole or not at all. Run again once the move is done; the audit trail shows it as activity {activityId}."));
            }

            return Result(activityId, name, asked, LedgerMoves.Running, 0, 0, "another process is moving this ledger");
        }

        try
        {
            return await MoveUnderLockAsync(db, partition, name, activityId, wait, ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseMoveLockAsync(db, resource).ConfigureAwait(false);
        }
    }

    private async Task<LedgerMoveResult> MoveUnderLockAsync(OsduDbContext db, short partition, string name, long activityId, bool wait, CancellationToken ct)
    {
        // Read again under the lock: another process may have finished it meanwhile.
        var activity = await db.DeliveryActivities.AsNoTracking()
            .FirstOrDefaultAsync(a => a.PartitionId == partition && a.ActivityId == activityId, ct).ConfigureAwait(false);
        if (activity is null || LedgerMoves.Read(activity.ParametersJson) is not { } move)
        {
            return new LedgerMoveResult(activityId, name, Guid.Empty, Guid.Empty, "failed", 0, 0, $"Activity {activityId} names no ledger move.");
        }

        if (activity.Outcome != LedgerMoves.Running)
        {
            return Result(activityId, name, move, activity.Outcome, 0, move.Superseded, activity.Summary ?? activity.Outcome);
        }

        var now = Now;
        var (from, to) = (move.From, move.To);

        // A lease that ran out is settled first, so the records it held are no lease's and move with the rest.
        await RecoverExpiredLeasesAsync(from, now, ct).ConfigureAwait(false);
        await RecoverExpiredLeasesAsync(to, now, ct).ConfigureAwait(false);
        var check = await RetryDeadlockAsync(() => SqlServerLedgerBulk.CheckMoveAsync(db, partition, activityId, from, to, now, ct), ct).ConfigureAwait(false);
        if (check.Working)
        {
            var waiting = $"a run is working on '{move.FromFlow}' or '{move.ToFlow}' (a lease on its records, or a submission it plans or sends), so the move waits for that work to end";
            if (wait)
            {
                throw new DeliveryException(
                    $"The ledger of '{move.FromFlow}' moves to '{move.ToFlow}', which replaced it, and {waiting}; nothing ran, since a run reads its ledger whole or not at all. Run again once that work has ended.");
            }

            return Result(activityId, name, move, LedgerMoves.Running, 0, move.Superseded, waiting);
        }

        if (!move.Started && (check.Claimed > 0 || check.Colliding > 0))
        {
            var refused = check.Claimed > 0
                ? string.Create(CultureInfo.InvariantCulture, $"not moved: {check.Claimed} record(s) of '{move.ToFlow}' claimed OSDU ids under keys '{move.FromFlow}' holds records of too, each with an id of its own, and one key is one record of a ledger. Keep the old ledger with ledger: {move.FromLedger} in the flow's document, or leave the two apart.")
                : string.Create(CultureInfo.InvariantCulture, $"not moved: {check.Colliding} artifact or reversal row(s) of '{move.ToFlow}' are the same as rows of '{move.FromFlow}', which one ledger cannot hold twice.");
            await SqlServerLedgerBulk.FailMoveAsync(db, partition, activityId, from, to, refused, now, ct).ConfigureAwait(false);
            return Result(activityId, name, move, "failed", 0, 0, refused);
        }

        await SqlServerLedgerBulk.StartMoveAsync(db, partition, activityId, from, to, ct).ConfigureAwait(false);
        await RepeatAsync(() => SqlServerLedgerBulk.SupersedeIdentitiesAsync(db, partition, activityId, from, to, WriteSlice, ct), ct).ConfigureAwait(false);
        await RepeatAsync(() => SqlServerLedgerBulk.SupersedeRecordsAsync(db, partition, activityId, from, to, WriteSlice, ct), ct).ConfigureAwait(false);
        await RepeatAsync(() => SqlServerLedgerBulk.SupersedeRemovedKeysAsync(db, partition, activityId, from, to, WriteSlice, ct), ct).ConfigureAwait(false);
        await RetryDeadlockAsync(async () => { await SqlServerLedgerBulk.SupersedeWatermarksAsync(db, partition, activityId, from, to, ct).ConfigureAwait(false); return 0; }, ct).ConfigureAwait(false);

        Guid? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var cursor = after;
            var (linked, last) = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.LinkMovedRecordsAsync(db, partition, activityId, from, to, cursor, WriteSlice, ct), ct).ConfigureAwait(false);
            if (linked < WriteSlice || last is null)
            {
                break;
            }

            after = last;
        }

        // The record's own row moves last, so a record shows under its new pipeline once everything it carries is there.
        foreach (var table in LedgerMoves.KeyedTables.Where(t => t != "Record").Append("Record"))
        {
            await RepeatAsync(() => SqlServerLedgerBulk.MoveSliceAsync(db, table, partition, activityId, from, to, WriteSlice, ct), ct).ConfigureAwait(false);
        }

        foreach (var table in LedgerMoves.NamingTables)
        {
            await RepeatAsync(() => SqlServerLedgerBulk.RenameSliceAsync(db, table, partition, activityId, from, to, WriteSlice, ct), ct).ConfigureAwait(false);
        }

        var (moved, left) = await RetryDeadlockAsync(() => SqlServerLedgerBulk.MoveCountsAsync(db, partition, activityId, from, to, ct), ct).ConfigureAwait(false);
        var superseded = LedgerMoves.Read((await db.DeliveryActivities.AsNoTracking()
            .Where(a => a.PartitionId == partition && a.ActivityId == activityId)
            .Select(a => a.ParametersJson)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)))?.Superseded ?? 0;
        if (left > 0)
        {
            // Rows a run wrote under the old identity while the move ran: the move is taken up again and reaches them.
            return Result(activityId, name, move, LedgerMoves.Running, moved, superseded, string.Create(CultureInfo.InvariantCulture, $"{left} record(s) of '{move.FromFlow}' are still to move"));
        }

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"moved {moved} record(s) of '{move.FromFlow}' to '{move.ToFlow}', which replaced it in its repository, with their history, their OSDU ids and the reading position")
            + (superseded > 0
                ? string.Create(CultureInfo.InvariantCulture, $"; {superseded} record(s) '{move.ToFlow}' had held, which claimed no OSDU id, gave way to them")
                : string.Empty);
        await SqlServerLedgerBulk.FinishMoveAsync(db, partition, activityId, from, to, "completed", summary, Now, ct).ConfigureAwait(false);
        _ledgerPartitions.TryRemove(from, out _);
        return Result(activityId, name, move, "completed", moved, superseded, summary);
    }

    /// <summary>Runs a slice statement until it reaches fewer rows than a slice: every row it is meant for is done.</summary>
    private async Task RepeatAsync(Func<Task<int>> slice, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await RetryDeadlockAsync(slice, ct).ConfigureAwait(false) < WriteSlice)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Lets go of the move lock on <paramref name="db"/>'s connection. A connection that cannot say so is cleared from the
    /// pool, which ends its session and the lock with it, rather than handed to the next caller holding it.
    /// </summary>
    private static async Task ReleaseMoveLockAsync(OsduDbContext db, string resource)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var command = SqlServerLedgerBulk.ReleaseMoveLock(connection, resource);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            SqlConnection.ClearPool(connection);
        }
    }

    private static LedgerMoveResult Result(long activityId, string partition, LedgerMoveParameters move, string outcome, long records, long superseded, string summary)
        => new(activityId, partition, move.From, move.To, outcome, records, superseded, summary);
}
