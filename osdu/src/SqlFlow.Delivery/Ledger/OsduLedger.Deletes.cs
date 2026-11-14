using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Deleting what a missing document left behind (osdu/docs/reference/concepts/renaming-a-pipeline.md, Deleting what a missing
// document left behind; LedgerDeletes): each orphaned ledger is checked for what only it knows of OSDU, asked in a
// transaction of its own, and deleted at once in committed slices, under the lock a move into the ledger takes. A deletion
// that stops, or that waits for work in flight, stays running, and the control plane's pass takes it up again.
public sealed partial class OsduLedger
{
    /// <summary>The table whose rows each keep a table of their own, made at run time and dropped with them.</summary>
    private const string DuplicateCheckTable = "InventoryDuplicateCheck";

    public async Task<IReadOnlyList<LedgerDeleteStep>> PreviewOrphanedLedgerDeleteAsync(LedgerDeleteRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var steps = await ReadAsync(db => LedgerBindings.PlanDeleteAsync(db, request, ct), ct).ConfigureAwait(false);
        var previewed = new List<LedgerDeleteStep>(steps.Count);
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            var refusal = await DeleteRefusalAsync(step.PartitionId, step.Partition, step.Ledger, step.Kind, step.FlowName, ct).ConfigureAwait(false);
            previewed.Add(step with { Refusal = refusal?.Detail, RefusedFor = refusal?.Reason });
        }

        return previewed;
    }

    public async Task<IReadOnlyList<LedgerDeleteResult>> DeleteOrphanedLedgersAsync(LedgerDeleteRequest request, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var steps = await PreviewOrphanedLedgerDeleteAsync(request, ct).ConfigureAwait(false);
        var results = new List<LedgerDeleteResult>(steps.Count);
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (step.Refusal is { } refusal)
            {
                results.Add(new LedgerDeleteResult(0, step.Partition, step.Ledger, step.Interface, LedgerMoves.Failed, step.Rows, refusal));
                continue;
            }

            long activityId;
            try
            {
                activityId = await AskDeleteAsync(step, actor, ct).ConfigureAwait(false);
            }
            catch (DeliveryException ex)
            {
                // One ledger that cannot be deleted leaves the others to go: each is a deletion of its own.
                results.Add(new LedgerDeleteResult(0, step.Partition, step.Ledger, step.Interface, LedgerMoves.Failed, step.Rows, ex.Message));
                continue;
            }

            results.Add(await CompleteDeleteAsync(step.PartitionId, activityId, ct).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<IReadOnlyList<LedgerDeleteResult>> CompleteLedgerDeletesAsync(CancellationToken ct = default)
    {
        // The deletions asked and not done, oldest first, a partition at a time: each one seek of the activities of a kind.
        var partitions = await ReadAsync(db => db.DeliveryLedgerPartitions.AsNoTracking().Select(p => p.PartitionId).ToListAsync(ct), ct).ConfigureAwait(false);
        var results = new List<LedgerDeleteResult>();
        foreach (var partition in partitions)
        {
            var pending = await ReadAsync(
                db => db.DeliveryActivities.AsNoTracking()
                    .Where(a => a.PartitionId == partition && a.Kind == LedgerDeletes.Kind && a.Outcome == LedgerMoves.Running)
                    .OrderBy(a => a.ActivityId)
                    .Select(a => a.ActivityId)
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            foreach (var activityId in pending)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await CompleteDeleteAsync(partition, activityId, ct).ConfigureAwait(false));
            }
        }

        return results;
    }

    /// <summary>
    /// Asks the deletion of one orphaned ledger, in a transaction that holds the lock every ask for a move holds from before it
    /// checks the ledger is still orphaned, so a deletion and a takeover of one ledger, or a deletion and a sync's move, are
    /// never both asked.
    /// </summary>
    private Task<long> AskDeleteAsync(LedgerDeleteStep step, string actor, CancellationToken ct)
        => RetryDeadlockAsync(
            async () =>
            {
                await using var db = Open();
                var strategy = db.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                    var activityId = await LedgerBindings.RequestDeleteAsync(db, step, actor, Now, ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return activityId;
                }).ConfigureAwait(false);
            },
            ct);

    /// <summary>
    /// Why the orphaned ledger <paramref name="ledger"/> cannot be deleted now, or null when it can: a deletion removes
    /// history, never what only this ledger knows of OSDU or what something declared still reads. Read, nothing written.
    /// </summary>
    private async Task<LedgerDeleteRefusal?> DeleteRefusalAsync(short partition, string partitionName, Guid ledger, string kind, string flow, CancellationToken ct)
    {
        switch (kind)
        {
            case LedgerKinds.Delivery:
                var blocks = await ReadAsync(db => SqlServerLedgerBulk.DeleteBlocksAsync(db, partition, ledger, Now, ct), ct).ConfigureAwait(false);
                if (blocks.InOsdu > 0)
                {
                    return Refused(
                        $"OSDU may still hold {blocks.InOsdu} of its records",
                        $"{blocks.InOsdu} record(s) of '{flow}' in partition '{partitionName}' claimed an OSDU id and are not marked removed, so OSDU may still hold them and the ledger is not deleted: it is the one place that accounts for them. Have a declared flow take the ledger over; or declare the flow again, sync its repository and delete its ledger from its Delivery tab, which removes its records from OSDU first.");
                }

                if (blocks.Restricted > 0)
                {
                    return Refused(
                        $"{blocks.Restricted} of its records are restricted in OSDU",
                        $"{blocks.Restricted} record(s) of '{flow}' in partition '{partitionName}' have their access restricted in OSDU, so the ledger is not deleted: deleting it would forget the restrictions. Have a declared flow take the ledger over and lift them there.");
                }

                return blocks.ToUndo > 0
                    ? Refused(
                        $"{blocks.ToUndo} item(s) it left in OSDU are still to undo",
                        $"{blocks.ToUndo} item(s) the unfinished deliveries of '{flow}' left in OSDU in partition '{partitionName}' are still to undo, so the ledger is not deleted: it is the one place that names them. Have a declared flow take the ledger over, whose runs undo them.")
                    : null;

            case LedgerKinds.Inventory:
                var removals = await ReadAsync(db => SqlServerLedgerBulk.InventoryRemovalsAsync(db, partition, ledger, ct), ct).ConfigureAwait(false);
                return removals > 0
                    ? Refused(
                        $"it holds {removals} removal(s) from OSDU",
                        $"The inventories of '{flow}' in partition '{partitionName}' hold {removals} removal(s), the record of what each took out of OSDU and what a restore brings back, so the ledger is not deleted. Have a declared inventory flow take it over.")
                    : null;

            case LedgerKinds.Dimension:
                return await DimensionDeleteRefusalAsync(ledger, flow, ct).ConfigureAwait(false);

            default:
                return LedgerDeletes.Trees.ContainsKey(kind)
                    ? null
                    : new LedgerDeleteRefusal($"no deletion knows a {kind} ledger", $"'{kind}' is no kind of ledger a deletion knows, so nothing is deleted.");
        }
    }

    private static LedgerDeleteRefusal Refused(FormattableString reason, FormattableString detail)
        => new(reason.ToString(CultureInfo.InvariantCulture), detail.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Why the dimensions of an orphaned ledger cannot go: a cache flow of the partition captures one, or a view of another
    /// flow reads one's table (<see cref="Engine.Dimensions.DimensionRemoval"/> refuses a single dimension for the same).
    /// The missing flow's own views are no reason: they go with its last ledger.
    /// </summary>
    private async Task<LedgerDeleteRefusal?> DimensionDeleteRefusalAsync(Guid ledger, string flow, CancellationToken ct)
    {
        var own = (await ListDimensionViewsAsync(flow, ct).ConfigureAwait(false)).Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dimension in await ListDimensionsAsync(null, ledger, ct).ConfigureAwait(false))
        {
            var captures = await DimensionCapturesAsync(dimension.DimensionId, ct).ConfigureAwait(false);
            if (captures.Count > 0)
            {
                return new LedgerDeleteRefusal(
                    $"a cache flow captures dimension {dimension.Name}",
                    $"Dimension {dimension.Name} of '{flow}' is captured by {string.Join(", ", captures.Select(c => $"type {c.Type} of cache flow {c.CacheFlow}"))} in partition {captures[0].Partition}, so the ledger is not deleted: a refresh of that cache would read a dimension that is gone. Remove the type from the cache flow and sync the repository first, or have a declared dimension flow take the ledger over.");
            }

            if (dimension.TableName is { } table)
            {
                var reading = (await DimensionViewsReadingAsync(table, ct).ConfigureAwait(false)).Where(v => !own.Contains(v)).ToList();
                if (reading.Count > 0)
                {
                    return new LedgerDeleteRefusal(
                        $"another flow's view reads dimension {dimension.Name}",
                        $"Dimension {dimension.Name} of '{flow}' is read by view{(reading.Count == 1 ? string.Empty : "s")} {string.Join(", ", reading)} of another flow, so the ledger is not deleted: the view would read a table that is gone. Take the dimension out of the view's joins and build its flow first, or have a declared dimension flow take the ledger over.");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Carries out one deletion under the lock a move into its ledger takes, so no move and no second deletion works on the
    /// ledger meanwhile: checks it again, deletes what the ledger holds and closes the deletion. Another process deleting it,
    /// and work in flight on a delivery ledger, leave it running for the control plane's pass.
    /// </summary>
    /// <param name="partition">The partition the ledger is kept in.</param>
    /// <param name="activityId">The deletion's activity.</param>
    /// <param name="ct">Stops the deletion between two slices; it is taken up again where it stopped.</param>
    private async Task<LedgerDeleteResult> CompleteDeleteAsync(short partition, long activityId, CancellationToken ct)
    {
        var name = await PartitionNameAsync(partition, ct).ConfigureAwait(false) ?? string.Empty;
        await using var db = Open();
        var activity = await db.DeliveryActivities.AsNoTracking()
            .FirstOrDefaultAsync(a => a.PartitionId == partition && a.ActivityId == activityId, ct).ConfigureAwait(false);
        if (activity is null || LedgerDeletes.Read(activity.ParametersJson) is not { } asked)
        {
            return new LedgerDeleteResult(activityId, name, Guid.Empty, string.Empty, LedgerMoves.Failed, 0, $"Activity {activityId} names no ledger deletion.");
        }

        var resource = "SqlFlow.Delivery.LedgerMove:" + asked.Ledger.ToString("D");
        if (await SqlServerLedgerBulk.LockMoveAsync(db, resource, 0, ct).ConfigureAwait(false) < 0)
        {
            return Result(activityId, name, asked, LedgerMoves.Running, "another process is deleting this ledger");
        }

        try
        {
            return await DeleteUnderLockAsync(db, partition, name, activityId, ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseMoveLockAsync(db, resource).ConfigureAwait(false);
        }
    }

    private async Task<LedgerDeleteResult> DeleteUnderLockAsync(OsduDbContext db, short partition, string name, long activityId, CancellationToken ct)
    {
        // Read again under the lock: another process may have finished it meanwhile.
        var activity = await db.DeliveryActivities.AsNoTracking()
            .FirstOrDefaultAsync(a => a.PartitionId == partition && a.ActivityId == activityId, ct).ConfigureAwait(false);
        if (activity is null || LedgerDeletes.Read(activity.ParametersJson) is not { } asked)
        {
            return new LedgerDeleteResult(activityId, name, Guid.Empty, string.Empty, LedgerMoves.Failed, 0, $"Activity {activityId} names no ledger deletion.");
        }

        if (activity.Outcome != LedgerMoves.Running)
        {
            return Result(activityId, name, asked, activity.Outcome, activity.Summary ?? activity.Outcome);
        }

        try
        {
            // A repository may declare the flow again while its ledger is being deleted: what is left is then its flow's.
            if (!await ReadAsync(read => LedgerBindings.UnkeptAsync(read, partition, asked.Ledger, ct), ct).ConfigureAwait(false))
            {
                return await RefuseDeleteAsync(
                    db, partition, name, activityId, asked,
                    $"not deleted whole: a repository declares '{asked.Flow}' again, or a move takes its ledger, so what the ledger still holds stays with it.").ConfigureAwait(false);
            }

            if (await DeleteRefusalAsync(partition, name, asked.Ledger, asked.LedgerKind, asked.Flow, ct).ConfigureAwait(false) is { } refusal)
            {
                return await RefuseDeleteAsync(db, partition, name, activityId, asked, refusal.Detail).ConfigureAwait(false);
            }

            string summary;
            if (asked.LedgerKind == LedgerKinds.Delivery)
            {
                // A lease that ran out is settled first, so the records it held are no lease's and go with the rest.
                await RecoverExpiredLeasesAsync(asked.Ledger, Now, ct).ConfigureAwait(false);
                var blocks = await RetryDeadlockAsync(() => SqlServerLedgerBulk.DeleteBlocksAsync(db, partition, asked.Ledger, Now, ct), ct).ConfigureAwait(false);
                if (blocks.Working)
                {
                    return Result(
                        activityId, name, asked, LedgerMoves.Running,
                        $"a run is still working on '{asked.Flow}' (a lease on its records, or a submission it plans or sends), so the deletion waits for that work to end");
                }

                var deleted = await DeleteLedgerAsync(asked.Ledger, activity.Actor, activityId, Now, ct).ConfigureAwait(false);
                summary = string.Create(
                    CultureInfo.InvariantCulture,
                    $"deleted the orphaned ledger of '{asked.Flow}', which no repository declares: {deleted.Records} record(s), none of which OSDU holds, each kept as one line, with their attempts, and its submissions, watermarks and the rest of its runs");
            }
            else
            {
                var kind = LedgerMoves.OtherKinds[asked.LedgerKind];
                if (asked.LedgerKind == LedgerKinds.Dimension)
                {
                    await DeleteDimensionsAsync(asked, ct).ConfigureAwait(false);
                }
                else
                {
                    await DeleteTablesAsync(db, LedgerDeletes.Trees[asked.LedgerKind], partition, "FlowId", asked.Ledger, wide: true, ct).ConfigureAwait(false);
                }

                var left = await RetryDeadlockAsync(() => SqlServerLedgerBulk.KindRowsLeftAsync(db, kind, partition, asked.Ledger, ct), ct).ConfigureAwait(false);
                if (left > 0)
                {
                    // Rows a run wrote under the ledger while the deletion ran: it is taken up again and reaches them.
                    return Result(
                        activityId, name, asked, LedgerMoves.Running,
                        string.Create(CultureInfo.InvariantCulture, $"{left} of the {kind.Noun} of '{asked.Flow}' are still to delete"));
                }

                summary = string.Create(
                    CultureInfo.InvariantCulture,
                    $"deleted the orphaned ledger of '{asked.Flow}', which no repository declares: {asked.Rows} {kind.Noun} with everything kept under them");
            }

            await SqlServerLedgerBulk.CloseDeleteAsync(db, partition, activityId, LedgerMoves.Completed, summary, Now, ct).ConfigureAwait(false);
            return Result(activityId, name, asked, LedgerMoves.Completed, summary);
        }
        catch (DeliveryException ex)
        {
            return await RefuseDeleteAsync(db, partition, name, activityId, asked, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recorded before it is thrown on, so a deletion that cannot be made is not taken up again by every pass, its
            // ledger is listed as orphaned again, and the audit trail says what stopped it.
            await SqlServerLedgerBulk.CloseDeleteAsync(
                db, partition, activityId, LedgerMoves.Failed, "not deleted whole: " + SecretHygiene.RedactedMessage(ex), Now, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Deletes the dimensions of an orphaned ledger, each as a dimension its flow no longer declares is removed
    /// (<see cref="ILedger.RemoveDimensionAsync"/>): everything kept of it in the partition, and its table with the last
    /// partition writing it. The missing flow's views, which read tables every partition's builds wrote, go first when this
    /// is the last of the flow's ledgers that holds a dimension.
    /// </summary>
    private async Task DeleteDimensionsAsync(LedgerDeleteParameters asked, CancellationToken ct)
    {
        var holding = await ReadAsync(
            async db =>
            {
                var others = await db.DeliveryLedgers.AsNoTracking()
                    .Where(l => l.Kind == LedgerKinds.Dimension && l.FlowName == asked.Flow && l.FlowId != asked.Ledger)
                    .Select(l => l.FlowId)
                    .ToListAsync(ct).ConfigureAwait(false);
                return await LedgerMoves.HoldingAsync(db, LedgerKinds.Dimension, others, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        if (holding.Count == 0)
        {
            foreach (var view in await ListDimensionViewsAsync(asked.Flow, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                await RemoveDimensionViewAsync(view.Name, ct).ConfigureAwait(false);
            }
        }

        foreach (var dimension in await ListDimensionsAsync(null, asked.Ledger, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            await RemoveDimensionAsync(dimension.DimensionId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the rows of <paramref name="tables"/> whose <paramref name="column"/> names <paramref name="parent"/> (the
    /// ledger's identity, or the key of a row of the table above), a slice at a time: for a table other tables name, the rows
    /// that name each row go first, so nothing ever names a row that is gone.
    /// </summary>
    private async Task DeleteTablesAsync(
        OsduDbContext db, IReadOnlyList<LedgerDeleteTable> tables, short partition, string column, object parent, bool wide, CancellationToken ct)
    {
        foreach (var table in tables)
        {
            if (table.Key is not { } key || table.Children.Count == 0)
            {
                await RepeatAsync(() => SqlServerLedgerBulk.DeleteSliceAsync(db, table.Table, column, parent, wide, partition, WriteSlice, ct), ct).ConfigureAwait(false);
                continue;
            }

            if (table.Paged)
            {
                await RepeatAsync(() => SqlServerLedgerBulk.DeletePageAsync(db, table, column, parent, wide, partition, WriteSlice, ct), ct).ConfigureAwait(false);
                continue;
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var keys = await RetryDeadlockAsync(
                    () => SqlServerLedgerBulk.DeleteKeysAsync(db, table.Table, key, column, parent, wide, partition, WriteSlice, ct), ct).ConfigureAwait(false);
                if (keys.Count == 0)
                {
                    break;
                }

                foreach (var row in keys)
                {
                    await DeleteTablesAsync(db, table.Children, partition, key, row, table.WideKey, ct).ConfigureAwait(false);
                    if (table.Table == DuplicateCheckTable)
                    {
                        await SqlServerLedgerBulk.DeleteInventoryDuplicateTableAsync(db, partition, row, ct).ConfigureAwait(false);
                    }
                }

                await RetryDeadlockAsync(() => SqlServerLedgerBulk.DeleteRowsAsync(db, table.Table, key, keys, table.WideKey, partition, ct), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Closes a deletion that will not be made, saying why; its ledger is listed as orphaned again.</summary>
    private async Task<LedgerDeleteResult> RefuseDeleteAsync(OsduDbContext db, short partition, string name, long activityId, LedgerDeleteParameters asked, string why)
    {
        await SqlServerLedgerBulk.CloseDeleteAsync(db, partition, activityId, LedgerMoves.Failed, why, Now, CancellationToken.None).ConfigureAwait(false);
        return Result(activityId, name, asked, LedgerMoves.Failed, why);
    }

    private static LedgerDeleteResult Result(long activityId, string partition, LedgerDeleteParameters asked, string outcome, string summary)
        => new(activityId, partition, asked.Ledger, asked.Interface, outcome, asked.Rows, summary);
}
