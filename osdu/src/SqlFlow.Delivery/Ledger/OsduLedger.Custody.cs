using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

// What a removal or a restore did in OSDU, written on the records it reached
// (osdu/docs/reference/concepts/removal-and-reversal.md, Removing records from OSDU; Restoring the previous version): a
// record taken out of OSDU is marked removed, with what OSDU held of it forgotten, and a record written back to its
// version before the latest is marked reverted at that version.
public sealed partial class OsduLedger
{
    /// <summary>
    /// Whether a record is held or failed at a row of its own: its pending columns name the version it was held at, which is
    /// the latest row the ledger read for it, and the one a write back that blocks it again leaves it at.
    /// </summary>
    private static bool HeldAtItsRow(DeliveryRecord entity)
        => (entity.Status == HeldStatus || entity.Status == FailedStatus)
            && (entity.PendingSourceFingerprint is not null || entity.PendingSourceModifiedUtc is not null);

    public async Task MarkRemovedAsync(
        Guid flowId, IReadOnlyList<DeliveryKey> keys, RemovalScope scope, string worker, DateTime nowUtc, string? correlationId = null, bool deliverAgain = true,
        IReadOnlyDictionary<DeliveryKey, SourceRemovalNote>? sources = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        if (keys.Count == 0)
        {
            return;
        }

        if (sources is not null && (scope != RemovalScope.Record || deliverAgain || keys.Any(k => !sources.ContainsKey(k))))
        {
            throw new DeliveryException(
                "A record its source no longer holds is taken out of OSDU reversibly (the record scope), each with why, and kept out by its deletion rather than asked to be planned again.");
        }

        var then = deliverAgain ? "the flow's next run reads its row again and delivers it" : "the next run that reads its row delivers it again";
        var note = scope switch
        {
            RemovalScope.Record => $"removed from OSDU (reversible) by {worker}; {then}",
            RemovalScope.History => $"earlier versions purged from OSDU by {worker}; the latest version is still live",
            RemovalScope.Everything => $"purged from OSDU (the record and every version) by {worker}; {then}",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);

        // A removal of many records settles in chunks: one query for the rows and one save for the whole chunk,
        // rather than a round trip per record.
        foreach (var chunk in keys.Chunk(ChunkSize))
        {
            await using var db = Open();
            var ids = chunk.Select(k => k.Value).Distinct().ToList();
            var entities = await db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && ids.Contains(r.DeliveryKey)).ToListAsync(ct).ConfigureAwait(false);
            if (entities.Count != ids.Count)
            {
                var missing = ids.Except(entities.Select(e => e.DeliveryKey)).First();
                throw new DeliveryException($"Record {missing} is not in the ledger of flow {flowId:D}.");
            }

            foreach (var entity in entities)
            {
                if (sources is not null)
                {
                    var source = sources[new DeliveryKey(entity.DeliveryKey)];
                    var why = source.Describe(inOsdu: true);
                    db.DeliveryAttempts.Add(SourceRemovalAttempt(entity, worker, AttemptOutcome.Deleted, source, why, correlationId, nowUtc));
                    Retire(entity, why, nowUtc, deliverAgain: false);
                    if (source.ActivityId is { } activity)
                    {
                        db.DeliveryActivityRecords.Add(new DeliveryActivityRecord { PartitionId = partition, FlowId = flowId, DeliveryKey = entity.DeliveryKey, ActivityId = activity });
                    }

                    continue;
                }

                MarkRemoved(db, entity, scope, worker, note, nowUtc, correlationId, deliverAgain);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // After the commit, never inside it: the records linking one of these as their child render again and leave the
            // link out, since OSDU no longer holds the child (osdu/docs/reference/flow/mapping-child.md, When a child is taken
            // out). A history purge leaves the record live, so its links stand.
            if (scope != RemovalScope.History)
            {
                await PlanParentsOfAsync(partition, [.. entities.Select(e => e.TargetId).OfType<string>()], nowUtc, ct).ConfigureAwait(false);
            }
        }
    }

    private static void MarkRemoved(OsduDbContext db, DeliveryRecord entity, RemovalScope scope, string worker, string note, DateTime nowUtc, string? correlationId, bool deliverAgain)
    {
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            PartitionId = entity.PartitionId,
            FlowId = entity.FlowId,
            DeliveryKey = entity.DeliveryKey,
            SubmissionId = entity.LastSubmissionId,
            Worker = worker,
            StartedUtc = nowUtc,
            CompletedUtc = nowUtc,
            Outcome = StatusText.Of(scope == RemovalScope.History ? AttemptOutcome.HistoryPurged : AttemptOutcome.Deleted),
            Phase = scope == RemovalScope.History ? "purge-history" : "delete",
            TargetVersion = entity.TargetVersion,
            ResultJson = AttemptResult.Removal(correlationId, entity.TargetStateJson, note),
        });

        // A history purge leaves the record live in OSDU at the version the ledger already holds, so its custody
        // state is still true and must not be disturbed: the attempt above is the whole of what happened.
        if (scope == RemovalScope.History)
        {
            entity.UpdatedUtc = nowUtc;
            return;
        }

        // Removed, not blocked: a removal is a clean-up, and the record follows its source again from here. An operator's
        // removal asks for it to be planned again, so the flow's next run reads its row by key, finds nothing delivered and
        // sends it, whether or not the row changed: what the source holds and OSDU does not is delivered again. A row the
        // ingestion table marks deleted is never sent, so a record that should stay out of OSDU is taken out of the source.
        // Released from any block it carried, since nothing of what blocked it is left in OSDU. Removed by an operator
        // rather than kept back by a problem: nothing about it is a problem to fix. The removal that opens deleting the whole
        // ledger, and an inventory's removal of an id the ledger already holds as removed, ask for nothing themselves and
        // leave any request the record carried as it is: the one is followed by a run that reads every row, the other only
        // makes OSDU agree with what the ledger already says, and the operator's removal before it asked for what it asked.
        Retire(entity, note, nowUtc, deliverAgain);
    }

    public async Task<IReadOnlyList<DeliveryKey>> MarkRestoredAsync(
        Guid flowId, IReadOnlyList<PreviousVersionRestored> restored, string worker, Guid? runId, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(restored);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        if (restored.Count == 0)
        {
            return [];
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var moved = new List<DeliveryKey>();
        foreach (var chunk in restored.GroupBy(r => r.Key).Select(g => g.Last()).Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            moved.AddRange(await RetryDeadlockAsync(() => MarkRestoredChunkAsync(partition, flowId, chunk, worker, runId, nowUtc, correlationId, ct), ct).ConfigureAwait(false));
        }

        return moved;
    }

    /// <summary>
    /// One chunk of <see cref="MarkRestoredAsync"/>, in one transaction: the records are read under repeatable read, so the
    /// version and lease each is checked at are the ones it is written at. Returns the records that moved on.
    /// </summary>
    private async Task<IReadOnlyList<DeliveryKey>> MarkRestoredChunkAsync(
        short partition, Guid flowId, IReadOnlyList<PreviousVersionRestored> chunk, string worker, Guid? runId, DateTime nowUtc, string? correlationId, CancellationToken ct)
    {
        var ids = chunk.Select(r => r.Key.Value).ToList();
        var versions = chunk.Select(r => (long?)r.Restored).Distinct().ToList();
        var putBack = new[] { StatusText.Of(AttemptOutcome.Delivered), StatusText.Of(AttemptOutcome.Restored) };
        var busy = new[] { StatusText.Of(RecordStatus.Pending), StatusText.Of(RecordStatus.Delivering), StatusText.Of(RecordStatus.Waiting), StatusText.Of(RecordStatus.Deleted) };
        var reverted = StatusText.Of(RecordStatus.Reverted);
        await using var db = Open();
        // A retried run of this block starts from the database, not from what a rolled back one left tracked, and the
        // connection leaves it at read committed, so the next read on it does not hold repeatable read locks.
        return await PooledIsolation.InTransactionAsync(db, System.Data.IsolationLevel.RepeatableRead, async () =>
        {
            var entities = await db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && ids.Contains(r.DeliveryKey))
                .ToDictionaryAsync(r => r.DeliveryKey, ct)
                .ConfigureAwait(false);

            // The attempt that delivered or restored each version put back says what the record's hashes and origin become.
            var priors = (await db.DeliveryAttempts.AsNoTracking()
                    .Where(a => a.PartitionId == partition && a.FlowId == flowId && ids.Contains(a.DeliveryKey)
                        && putBack.Contains(a.Outcome) && versions.Contains(a.TargetVersion))
                    .Select(a => new { a.DeliveryKey, a.TargetVersion, a.AttemptId, a.MetadataHash, a.PayloadHash, a.SourceFileName, a.SourceRowNumber, a.SourceUpdatedUtc })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .GroupBy(a => (a.DeliveryKey, a.TargetVersion))
                .ToDictionary(g => g.Key, g => g.MaxBy(a => a.AttemptId)!);

            var moved = new List<DeliveryKey>();
            foreach (var r in chunk)
            {
                if (!entities.TryGetValue(r.Key.Value, out var entity)
                    || entity.TargetVersion != r.Replaced || entity.LeaseOwner is not null || busy.Contains(entity.Status))
                {
                    moved.Add(r.Key);
                    continue;
                }

                priors.TryGetValue((entity.DeliveryKey, (long?)r.Restored), out var prior);
                var note = string.Create(
                    CultureInfo.InvariantCulture,
                    $"reverted by {worker}: version {r.Restored}, the one before the latest, written back as version {r.NewVersion}; version {r.Replaced} stays in the record's history. Release the record or change the source to plan it again");
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    PartitionId = entity.PartitionId,
                    FlowId = entity.FlowId,
                    DeliveryKey = entity.DeliveryKey,
                    SubmissionId = entity.LastSubmissionId,
                    RunId = runId,
                    Worker = worker,
                    StartedUtc = nowUtc,
                    CompletedUtc = nowUtc,
                    Outcome = StatusText.Of(AttemptOutcome.Restored),
                    Phase = AttemptPhases.RestorePrevious,
                    MetadataHash = prior?.MetadataHash,
                    PayloadHash = prior?.PayloadHash,
                    TargetVersion = r.NewVersion,
                    ResultJson = AttemptResult.PreviousRestored(correlationId, r.TargetStateJson, r.Replaced, r.Restored, note),
                    SourceFileName = prior?.SourceFileName,
                    SourceRowNumber = prior?.SourceRowNumber,
                    SourceUpdatedUtc = prior?.SourceUpdatedUtc,
                });

                // Blocked at the source version it was left at, as a reversal leaves a record: the next plans pass it over
                // until its row moves or someone releases it. A record an earlier write back already blocked holds that
                // version among its pending columns (its own were cleared), and keeps it; so does a held or failed record,
                // whose pending columns hold the later row it was held at, which is the row it stands at now.
                if (entity.Status != reverted && !HeldAtItsRow(entity))
                {
                    entity.PendingSourceFingerprint = entity.SourceFingerprint;
                    entity.PendingSourceModifiedUtc = entity.SourceModifiedUtc ?? nowUtc;
                    entity.PendingSourceFileName = entity.SourceFileName;
                    entity.PendingSourceRowNumber = entity.SourceRowNumber;
                    entity.PendingSourceUpdatedUtc = entity.SourceUpdatedUtc;
                }

                entity.Status = reverted;
                entity.Blocked = true;
                entity.ProblemHash = null;
                entity.SourceFileName = prior?.SourceFileName;
                entity.SourceRowNumber = prior?.SourceRowNumber;
                entity.SourceUpdatedUtc = prior?.SourceUpdatedUtc;
                entity.MetadataHash = prior?.MetadataHash;
                entity.PayloadHash = prior?.PayloadHash;
                entity.PayloadModifiedUtc = null;
                entity.SourceFingerprint = null;
                entity.SourceModifiedUtc = null;
                entity.RenderContext = null;
                entity.RenderCommit = null;
                entity.RenderPaths = null;
                entity.TargetVersion = r.NewVersion;
                entity.TargetStateJson = r.TargetStateJson;
                entity.LastVerifiedUtc = null;
                entity.LastVerifyOutcome = null;
                entity.NextAttemptUtc = null;
                entity.AttemptCount = 0;
                entity.PendingDocumentRef = null;
                entity.WorkBatch = null;
                entity.PendingStepJson = null;
                entity.PendingReferences = null;
                entity.WaitingFor = null;
                entity.PendingMetadata = false;
                entity.PendingPayload = false;
                entity.PendingPayloadLocation = null;
                entity.PendingPayloadModifiedUtc = null;
                entity.PendingRenderContext = null;
                entity.PendingMetadataHash = null;
                entity.PendingPayloadHash = null;
                entity.PendingParentVersion = null;
                entity.AcceptedMetadataHash = null;
                entity.PlanRequestedUtc = null;
                entity.LastError = Truncate(note, 2000);
                entity.UpdatedUtc = nowUtc;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (IReadOnlyList<DeliveryKey>)moved;
        }, ct).ConfigureAwait(false);
    }
}
