using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Ledger;

// The missing documents and their takeover (osdu/docs/reference/concepts/renaming-a-pipeline.md, Missing documents;
// LedgerBindings): each ledger a takeover hands over is asked in a transaction of its own and carried out at once, as a move.
public sealed partial class OsduLedger
{
    public Task<IReadOnlyList<OrphanedDocument>> ListOrphanedDocumentsAsync(string? partition, CancellationToken ct = default)
        => ReadAsync(db => LedgerBindings.ListAsync(db, partition, ct), ct);

    public async Task<IReadOnlyList<LedgerMoveResult>> TakeOverLedgersAsync(LedgerTakeoverRequest request, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var steps = await ReadAsync(db => LedgerBindings.PlanAsync(db, request, ct), ct).ConfigureAwait(false);
        var results = new List<LedgerMoveResult>(steps.Count);
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (step.To is not { } to)
            {
                results.Add(new LedgerMoveResult(0, step.Partition, step.Ledger, Guid.Empty, LedgerBindings.Skipped, 0, 0, step.Skipped ?? "this ledger stays orphaned"));
                continue;
            }

            long activityId;
            try
            {
                activityId = await AskTakeoverAsync(step, actor, ct).ConfigureAwait(false);
            }
            catch (DeliveryException ex)
            {
                // One ledger that cannot be handed over leaves the others to go: each is a move of its own.
                results.Add(new LedgerMoveResult(0, step.Partition, step.Ledger, to.LedgerFlowId, LedgerMoves.Failed, 0, 0, ex.Message));
                continue;
            }

            results.Add(await CompleteMoveAsync(step.PartitionId, activityId, wait: false, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>
    /// Asks the move of one ledger a takeover hands over, in a transaction that holds the lock every ask holds from before it
    /// checks the ledger is still orphaned, so two takeovers of one document, or a takeover and a sync, never both ask it.
    /// </summary>
    private Task<long> AskTakeoverAsync(LedgerTakeoverStep step, string actor, CancellationToken ct)
        => RetryDeadlockAsync(
            async () =>
            {
                await using var db = Open();
                var strategy = db.Database.CreateExecutionStrategy();
                return await strategy.ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                    var activityId = await LedgerBindings.RequestAsync(db, step, actor, Now, ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return activityId;
                }).ConfigureAwait(false);
            },
            ct);
}
