using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// A flow's records checked against what is there: what a sync found of their rows in the ingestion tables
// (osdu/docs/reference/concepts/removal-and-reversal.md, Syncing the ledger with the source), and the drift pass, which
// compares delivered records with what OSDU holds and checks removed ones are gone from it.
public sealed partial class OsduLedger
{
    public async Task<SourceSyncApplied> ApplySourceSyncAsync(Guid flowId, IReadOnlyList<SourceSyncFinding> findings, Guid? runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var now = Now;
        var arrivals = 0;
        var plans = 0;
        var notFound = 0;
        if (findings.Count == 0)
        {
            return new SourceSyncApplied(arrivals, plans, notFound);
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        foreach (var chunk in findings.Chunk(ChunkSize))
        {
            var keys = chunk.Select(f => f.DeliveryKey.Value).ToArray();
            var entities = await db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && keys.Contains(r.DeliveryKey)).ToDictionaryAsync(r => r.DeliveryKey, ct).ConfigureAwait(false);
            foreach (var finding in chunk)
            {
                if (!entities.TryGetValue(finding.DeliveryKey.Value, out var entity))
                {
                    throw new DeliveryException($"Record {finding.DeliveryKey} is not in the ledger, but a sync only ever reads the rows of records the ledger holds.");
                }

                // The arrival is a fact of the row, not a decision about the record: it is written as read.
                if (finding.InsertedUtc is { } inserted && entity.SourceInsertedUtc != inserted)
                {
                    entity.SourceInsertedUtc = inserted;
                    arrivals++;
                }

                // A change the ledger never saw is decided by a plan, as every change is: the flow's next run reads the
                // row by its key. The record's state moves, so the record says when.
                if (finding.RequestPlan && entity.PlanRequestedUtc is null)
                {
                    entity.PlanRequestedUtc = now;
                    entity.UpdatedUtc = now;
                    plans++;
                }

                if (finding.NotFound is { } reason)
                {
                    db.DeliveryAttempts.Add(new DeliveryAttempt
                    {
                        PartitionId = partition,
                        FlowId = flowId,
                        DeliveryKey = entity.DeliveryKey,
                        RunId = runId,
                        Worker = "sync",
                        StartedUtc = now,
                        CompletedUtc = now,
                        Outcome = StatusText.Of(AttemptOutcome.Skipped),
                        Phase = AttemptPhases.SourceMissing,
                        ResultJson = AttemptResult.WithDetail(null, Truncate(reason, 2000)),
                        SourceFileName = Truncate(finding.Origin.FileName, DeliveryModel.MaxSourceFileNameLength),
                        SourceRowNumber = finding.Origin.RowNumber,
                        SourceUpdatedUtc = finding.Origin.UpdatedUtc,
                    });
                    notFound++;
                }

                // A row the table no longer holds cannot be planned: a request to plan the record again is answered, and a
                // held or failed record a release left unblocked is blocked again, so it moves when its row is back (a row
                // the ingestion table lands again is stamped anew) or when someone releases it.
                if (finding.Gone)
                {
                    if (entity.PlanRequestedUtc is not null)
                    {
                        entity.PlanRequestedUtc = null;
                        entity.UpdatedUtc = now;
                    }

                    if (!entity.Blocked && (entity.Status == HeldStatus || entity.Status == FailedStatus) && entity.PendingDocumentRef is null)
                    {
                        entity.Blocked = true;
                        entity.LastError = "the record's row is gone from the ingestion table (deleted from it, or its key changed), so no run can plan it; "
                            + "it is planned again when its row is back, or released";
                        entity.ProblemHash = ProblemSignature.Of(entity.LastError);
                        entity.UpdatedUtc = now;
                    }
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new SourceSyncApplied(arrivals, plans, notFound);
    }

    public async Task<IReadOnlyList<RecordState>> ListForVerifyAsync(Guid flowId, DateTime? verifiedBeforeUtc, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var delivered = StatusText.Of(RecordStatus.Delivered);
        var deleted = StatusText.Of(RecordStatus.Deleted);
        var rows = await ReadAsync(
            db =>
            {
                // Delivered records are compared with what OSDU holds; removed ones are checked to be gone from it.
                var query = db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && (r.Status == delivered || r.Status == deleted) && r.TargetId != null);
                if (verifiedBeforeUtc is { } before)
                {
                    query = query.Where(r => r.LastVerifiedUtc == null || r.LastVerifiedUtc < before);
                }

                return query.OrderBy(r => r.LastVerifiedUtc).Take(Math.Clamp(max, 1, 10_000)).ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task RecordVerifyAsync(
        Guid flowId, DeliveryKey key, VerifyOutcome outcome, long? observedVersion, DateTime nowUtc, bool requeue, long? activityId = null, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var entity = await db.DeliveryRecords.FirstOrDefaultAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.DeliveryKey == key.Value, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Record {key} is not in the ledger of flow {flowId:D}.");
        if (requeue && outcome is VerifyOutcome.Drifted or VerifyOutcome.Missing)
        {
            // Reconciling is a redelivery of all of the record, marked by the statement every redelivery marks with: it
            // forgets what OSDU holds and the source version it was planned under, and asks the flow's next run to plan the
            // record again, which reads it by key whatever its incremental window holds. Only a record OSDU holds by the
            // ledger's account is marked; one a run took up since the pass read it is that run's.
            var observed = observedVersion?.ToString(CultureInfo.InvariantCulture) ?? "none";
            var expected = entity.TargetVersion?.ToString(CultureInfo.InvariantCulture) ?? "none";
            var note = $"verify: {StatusText.Of(outcome)} (observed version {observed}, expected {expected}); redelivery requested, the flow's next deliver run sends it again";
            await RedeliverKeysAsync(db, partition, flowId, [key.Value], RedeliverScope.All, marker: null, deliveredOnly: true, activityId, note, nowUtc, ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            entity = await db.DeliveryRecords.FirstAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.DeliveryKey == key.Value, ct).ConfigureAwait(false);
        }

        entity.LastVerifiedUtc = nowUtc;
        entity.LastVerifyOutcome = StatusText.Of(outcome);
        entity.UpdatedUtc = nowUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
