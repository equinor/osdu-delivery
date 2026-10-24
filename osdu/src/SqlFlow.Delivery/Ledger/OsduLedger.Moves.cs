using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// The moves of a ledger to another flow (osdu/docs/reference/concepts/renaming-a-pipeline.md; LedgerMoves): asked by the
// repository sync for a renamed flow, or by a person binding a ledger no declared flow keeps (LedgerBindings), and carried
// out here a slice at a time: by the sync once it committed, by the bind as it is asked, by the control plane's pass for one
// that had to wait, and by a run of the flow before it reads its ledger, whichever comes first.
public sealed partial class OsduLedger
{
    /// <summary>
    /// How long a run of the flow a ledger moves to waits for a move another process is making into its ledger: long enough
    /// for a ledger of millions of records, a slice at a time.
    /// </summary>
    private const int MoveWaitMilliseconds = 15 * 60 * 1000;

    public async Task<IReadOnlyList<LedgerMoveResult>> CompleteLedgerMovesAsync(Guid? into, CancellationToken ct = default)
    {
        var pending = await PendingMovesAsync(into, ct).ConfigureAwait(false);
        return await CompleteAsync(pending, wait: into is not null, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LedgerMoveResult>> CompleteRepositoryMovesAsync(Guid repoId, CancellationToken ct = default)
    {
        var pending = new List<(short, long)>();
        foreach (var (partition, activityId, json) in await PendingMoveRowsAsync(null, ct).ConfigureAwait(false))
        {
            if (LedgerMoves.Read(json) is { } move && move.Repository == repoId && !move.Bound)
            {
                pending.Add((partition, activityId));
            }
        }

        return await CompleteAsync(pending, wait: false, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<LedgerMoveResult>> CompleteAsync(IReadOnlyList<(short Partition, long ActivityId)> pending, bool wait, CancellationToken ct)
    {
        var results = new List<LedgerMoveResult>(pending.Count);
        foreach (var (partition, activityId) in pending)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await CompleteMoveAsync(partition, activityId, wait, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// The moves asked and not done, oldest first: into <paramref name="into"/> alone, found in its partition, or every
    /// one, a partition at a time, each one seek of the activities of a kind.
    /// </summary>
    private async Task<IReadOnlyList<(short Partition, long ActivityId)>> PendingMovesAsync(Guid? into, CancellationToken ct)
        => [.. (await PendingMoveRowsAsync(into, ct).ConfigureAwait(false)).Select(m => (m.Partition, m.ActivityId))];

    /// <summary>The moves asked and not done, as <see cref="PendingMovesAsync"/> finds them, with what each activity records of its move.</summary>
    private async Task<IReadOnlyList<(short Partition, long ActivityId, string? ParametersJson)>> PendingMoveRowsAsync(Guid? into, CancellationToken ct)
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

        var pending = new List<(short, long, string?)>();
        foreach (var partition in partitions)
        {
            var rows = await ReadAsync(
                db => db.DeliveryActivities.AsNoTracking()
                    .Where(a => a.PartitionId == partition && a.Kind == LedgerMoves.Kind && a.Outcome == LedgerMoves.Running
                        && (into == null || a.FlowId == into))
                    .OrderBy(a => a.ActivityId)
                    .Select(a => new { a.ActivityId, a.ParametersJson })
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            pending.AddRange(rows.Select(r => (partition, r.ActivityId, r.ParametersJson)));
        }

        return pending;
    }

    /// <summary>
    /// Carries out one move under its lock: checks it before it begins, then supersedes the new identity's records the old
    /// ledger holds too, links every old record to the move, moves each table's rows and closes the move. Work in flight on
    /// either ledger leaves the move waiting: the control plane takes it up on its next pass, and a run of the flow it moves
    /// to (<paramref name="wait"/>) is refused, so it never reads half a ledger.
    /// </summary>
    /// <param name="partition">The partition both ledgers are kept in.</param>
    /// <param name="activityId">The move's activity.</param>
    /// <param name="wait">True for a run about to read the ledger the move goes to.</param>
    /// <param name="ct">Stops the move between two slices; it is taken up again where it stopped.</param>
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
                    $"The ledger of '{asked.FromFlow}' is being moved to '{asked.ToFlow}', and the move did not finish within {MoveWaitMilliseconds / 60_000} minutes (sp_getapplock answered {granted}), so nothing ran: a run reads its ledger whole or not at all. Run again once the move is done; the audit trail shows it as activity {activityId}."));
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

        if (move.LedgerKind != LedgerKinds.Delivery)
        {
            return await MoveKindUnderLockAsync(db, partition, name, activityId, move, ct).ConfigureAwait(false);
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
                    $"The ledger of '{move.FromFlow}' moves to '{move.ToFlow}', and {waiting}; nothing ran, since a run reads its ledger whole or not at all. Run again once that work has ended.");
            }

            return Result(activityId, name, move, LedgerMoves.Running, 0, move.Superseded, waiting);
        }

        if (!move.Started && (check.Claimed > 0 || check.Colliding > 0))
        {
            var refused = check.Claimed > 0
                ? string.Create(CultureInfo.InvariantCulture, $"not moved: {check.Claimed} record(s) of '{move.ToFlow}' claimed OSDU ids under keys '{move.FromFlow}' holds records of too, each with an id of its own, and one key is one record of a ledger, so the two ledgers stay apart and the old one stays orphaned.")
                : string.Create(CultureInfo.InvariantCulture, $"not moved: {check.Colliding} artifact or reversal row(s) of '{move.ToFlow}' are the same as rows of '{move.FromFlow}', which one ledger cannot hold twice, so the two ledgers stay apart and the old one stays orphaned.");
            await SqlServerLedgerBulk.FailMoveAsync(db, partition, activityId, from, to, refused, now, ct).ConfigureAwait(false);
            return Result(activityId, name, move, LedgerMoves.Failed, 0, 0, refused);
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
            $"moved {moved} record(s) of '{move.FromFlow}' to '{move.ToFlow}', {Why(move)}, with their history, their OSDU ids and the reading position")
            + (superseded > 0
                ? string.Create(CultureInfo.InvariantCulture, $"; {superseded} record(s) '{move.ToFlow}' had held, which claimed no OSDU id, gave way to them")
                : string.Empty);
        await SqlServerLedgerBulk.FinishMoveAsync(db, partition, activityId, from, to, LedgerMoves.Completed, summary, Now, ct).ConfigureAwait(false);
        _ledgerPartitions.TryRemove(from, out _);
        return Result(activityId, name, move, LedgerMoves.Completed, moved, superseded, summary);
    }

    /// <summary>
    /// Carries out the move of a ledger of another kind (<see cref="LedgerMoves.OtherKinds"/>): begins it in one transaction,
    /// which refuses a ledger holding a dimension or an inventory of a name the old one holds too and moves those named rows
    /// first, then moves the rest of each table a slice at a time; a dimension ledger's views follow it to the new name. The
    /// other kinds have no work in flight the move waits for.
    /// </summary>
    private async Task<LedgerMoveResult> MoveKindUnderLockAsync(OsduDbContext db, short partition, string name, long activityId, LedgerMoveParameters move, CancellationToken ct)
    {
        var (from, to) = (move.From, move.To);
        if (!LedgerMoves.OtherKinds.TryGetValue(move.LedgerKind, out var kind))
        {
            var unknown = $"not moved: '{move.LedgerKind}' is no kind of ledger a move knows.";
            await SqlServerLedgerBulk.FailMoveAsync(db, partition, activityId, from, to, unknown, Now, ct).ConfigureAwait(false);
            return Result(activityId, name, move, LedgerMoves.Failed, 0, 0, unknown);
        }

        var moved = move.Moved;
        if (!move.Started)
        {
            var begun = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.BeginKindMoveAsync(db, kind, partition, activityId, from, to, move.ToFlow, ct), ct).ConfigureAwait(false);
            if (begun.Colliding.Count > 0)
            {
                var refused = string.Create(
                    CultureInfo.InvariantCulture,
                    $"not moved: '{move.ToFlow}' holds {kind.Noun} {string.Join(", ", begun.Colliding.Select(n => $"'{n}'"))} of its own that '{move.FromFlow}' holds too, and a flow holds one of each name, so the two ledgers stay apart and the old one stays orphaned.");
                await SqlServerLedgerBulk.FailMoveAsync(db, partition, activityId, from, to, refused, Now, ct).ConfigureAwait(false);
                return Result(activityId, name, move, LedgerMoves.Failed, 0, 0, refused);
            }

            moved = begun.Moved;
        }

        foreach (var table in kind.Tables)
        {
            await RepeatAsync(() => SqlServerLedgerBulk.MoveSliceAsync(db, table, partition, activityId, from, to, WriteSlice, ct), ct).ConfigureAwait(false);
        }

        if (kind.Kind == LedgerKinds.Dimension)
        {
            await RepeatAsync(() => SqlServerLedgerBulk.MoveViewsAsync(db, partition, activityId, from, to, WriteSlice, ct), ct).ConfigureAwait(false);
            await RetryDeadlockAsync(
                async () =>
                {
                    await SqlServerLedgerBulk.RenameViewOwnerAsync(db, partition, activityId, from, to, move.FromFlow, move.ToFlow, ct).ConfigureAwait(false);
                    return 0;
                },
                ct).ConfigureAwait(false);
        }

        var left = await RetryDeadlockAsync(() => SqlServerLedgerBulk.KindRowsLeftAsync(db, kind, partition, from, ct), ct).ConfigureAwait(false);
        if (left > 0)
        {
            return Result(activityId, name, move, LedgerMoves.Running, moved, 0, string.Create(CultureInfo.InvariantCulture, $"{left} of the {kind.Noun} of '{move.FromFlow}' are still to move"));
        }

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"moved {moved} {kind.Noun} of '{move.FromFlow}' to '{move.ToFlow}', {Why(move)}, with their history");
        await SqlServerLedgerBulk.FinishMoveAsync(db, partition, activityId, from, to, LedgerMoves.Completed, summary, Now, ct).ConfigureAwait(false);
        _ledgerPartitions.TryRemove(from, out _);
        return Result(activityId, name, move, LedgerMoves.Completed, moved, 0, summary);
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

    /// <summary>Why the ledger moved, as a move's summary says it.</summary>
    private static string Why(LedgerMoveParameters move)
        => move.Bound ? "to which it was bound by hand" : "the same document under its newest name";

    private static LedgerMoveResult Result(long activityId, string partition, LedgerMoveParameters move, string outcome, long records, long superseded, string summary)
        => new(activityId, partition, move.From, move.To, outcome, records, superseded, summary);
}
