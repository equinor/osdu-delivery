using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// Restricting records' access in OSDU (osdu/docs/reference/concepts/access-restriction.md). A restriction is written in two
// steps around the call that replaces the records' ACL: asked before it, so a call whose answer is lost still leaves the
// record kept out of every run with the ACL OSDU held before it, and confirmed (or withdrawn) once OSDU answered. Each step
// reads its records under repeatable read and writes only those still as the run read them: a record a worker took, or that
// another run restricted, lifted or removed meanwhile, is left as it is and named.
public sealed partial class OsduLedger
{
    public async Task<AccessRestrictionState?> FindAccessRestrictionAsync(string partition, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var row = await ReadAsync(
            db => db.DeliveryAccessRestrictions.AsNoTracking().FirstOrDefaultAsync(a => a.Partition == partition.Trim() && a.Name == name.Trim(), ct), ct).ConfigureAwait(false);
        return row is null ? null : AccessRestrictionState.Of(row);
    }

    public async Task<IReadOnlyDictionary<int, AccessRestrictionState>> AccessRestrictionsAsync(IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return new Dictionary<int, AccessRestrictionState>();
        }

        var asked = ids.Distinct().ToList();
        var rows = await ReadAsync(
            db => db.DeliveryAccessRestrictions.AsNoTracking().Where(a => asked.Contains(a.RestrictionId)).ToListAsync(ct), ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.RestrictionId, AccessRestrictionState.Of);
    }

    public async Task<long> CountRestrictedAsync(Guid flowId, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return 0;
        }

        return await ReadAsync(
            db => db.DeliveryRecords.AsNoTracking().LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.RestrictionId != null, ct), ct).ConfigureAwait(false);
    }

    public async Task<RestrictionWrite> AskRestrictionAsync(
        Guid flowId, AccessRestrictionState restriction, IReadOnlyList<RestrictionAsked> asked, string? holder, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(restriction);
        ArgumentNullException.ThrowIfNull(asked);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var written = new HashSet<DeliveryKey>();
        var moved = new HashSet<DeliveryKey>();
        foreach (var chunk in asked.GroupBy(a => a.Key).Select(g => g.Last()).Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var (w, m) = await RetryDeadlockAsync(() => AskChunkAsync(partition, flowId, restriction, chunk, holder, nowUtc, ct), ct).ConfigureAwait(false);
            written.UnionWith(w);
            moved.UnionWith(m);
        }

        return new RestrictionWrite(written, moved);
    }

    /// <summary>
    /// One chunk of <see cref="AskRestrictionAsync"/>: each record OSDU holds for the flow, no other lease holds and nothing
    /// restricts (or this restriction asked, unanswered) is marked restricted before its ACL is replaced, keeping the ACL it
    /// had (an earlier ask's, when one left it); work queued for it is dropped, since nothing is sent while it is restricted,
    /// and lifting the restriction asks for it to be planned again.
    /// </summary>
    private async Task<(IReadOnlyList<DeliveryKey> Written, IReadOnlyList<DeliveryKey> Moved)> AskChunkAsync(
        short partition, Guid flowId, AccessRestrictionState restriction, IReadOnlyList<RestrictionAsked> chunk, string? holder, DateTime nowUtc, CancellationToken ct)
    {
        var ids = chunk.Select(r => r.Key.Value).ToList();
        var deleted = StatusText.Of(RecordStatus.Deleted);
        var queued = new[] { StatusText.Of(RecordStatus.Pending), StatusText.Of(RecordStatus.Waiting) };
        await using var db = Open();
        return await PooledIsolation.InTransactionAsync(db, System.Data.IsolationLevel.RepeatableRead, async () =>
        {
            // Read under the transaction's lock, so the registry cannot remove the restriction while records take it on, and a
            // removal that won the race leaves nothing to mark.
            if (!await db.DeliveryAccessRestrictions.AnyAsync(a => a.RestrictionId == restriction.RestrictionId, ct).ConfigureAwait(false))
            {
                throw new DeliveryException(
                    $"Access restriction '{restriction.Name}' was removed from the registry while records were being restricted under it, so no more were.");
            }

            var entities = await db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && ids.Contains(r.DeliveryKey))
                .ToDictionaryAsync(r => r.DeliveryKey, ct)
                .ConfigureAwait(false);
            var written = new List<DeliveryKey>();
            var moved = new List<DeliveryKey>();
            foreach (var asked in chunk)
            {
                if (!entities.TryGetValue(asked.Key.Value, out var entity)
                    || entity.Status == deleted
                    || entity.TargetVersion is null
                    || entity.TargetId is null
                    || !string.Equals(entity.ClaimedTargetId, entity.TargetId, StringComparison.Ordinal)
                    || (entity.LeaseOwner is not null && !string.Equals(entity.LeaseOwner, holder, StringComparison.Ordinal))
                    || (entity.RestrictionId is { } already && (already != restriction.RestrictionId || entity.RestrictedUtc is not null)))
                {
                    moved.Add(asked.Key);
                    continue;
                }

                entity.RestrictionId = restriction.RestrictionId;
                entity.RestrictedUtc = null;
                entity.RestrictedFromAcl ??= asked.From.ToJson();
                if (queued.Contains(entity.Status))
                {
                    SetAsideQueuedWork(entity);
                }

                // Nothing is planned while the record is restricted; lifting the restriction asks for it again.
                entity.PlanRequestedUtc = null;
                entity.UpdatedUtc = nowUtc;
                written.Add(asked.Key);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ((IReadOnlyList<DeliveryKey>)written, (IReadOnlyList<DeliveryKey>)moved);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the work queued for a record OSDU holds, which is due or waits for a record it refers to: the record stands
    /// delivered at the version OSDU holds, as it did before the work was queued, with nothing to send. The work is not lost
    /// for good: the record's row renders it again once the record is planned.
    /// </summary>
    private static void SetAsideQueuedWork(DeliveryRecord entity)
    {
        entity.Status = StatusText.Of(RecordStatus.Delivered);
        entity.Blocked = false;
        entity.ProblemHash = null;
        entity.LastError = null;
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
        entity.PendingSourceFingerprint = null;
        entity.PendingSourceModifiedUtc = null;
        entity.PendingSourceFileName = null;
        entity.PendingSourceRowNumber = null;
        entity.PendingSourceUpdatedUtc = null;
        entity.AcceptedMetadataHash = null;
    }

    public async Task<RestrictionWrite> SettleRestrictionAsync(
        Guid flowId, AccessRestrictionState restriction, IReadOnlyList<RestrictionConfirmed> confirmed, IReadOnlyList<DeliveryKey> withdrawn,
        string worker, Guid? runId, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(restriction);
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(withdrawn);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var written = new HashSet<DeliveryKey>();
        var moved = new HashSet<DeliveryKey>();
        var items = confirmed.Select(c => (c.Key, Detail: (string?)c.Detail)).Concat(withdrawn.Select(k => (Key: k, Detail: (string?)null))).ToList();
        foreach (var chunk in items.Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var (w, m) = await RetryDeadlockAsync(() => SettleChunkAsync(partition, flowId, restriction, chunk, worker, runId, nowUtc, correlationId, ct), ct).ConfigureAwait(false);
            written.UnionWith(w);
            moved.UnionWith(m);
        }

        return new RestrictionWrite(written, moved);
    }

    /// <summary>
    /// One chunk of <see cref="SettleRestrictionAsync"/>: a record OSDU confirmed is restricted as of now, with an attempt naming
    /// the restriction, the ACL set and the ACL replaced (an item with a detail); a record whose ACL OSDU kept is no longer
    /// restricted, and is asked to be planned again, since asking dropped the work queued for it (an item with none). Only a
    /// record this restriction still asks of is written.
    /// </summary>
    private async Task<(IReadOnlyList<DeliveryKey> Written, IReadOnlyList<DeliveryKey> Moved)> SettleChunkAsync(
        short partition, Guid flowId, AccessRestrictionState restriction, IReadOnlyList<(DeliveryKey Key, string? Detail)> chunk,
        string worker, Guid? runId, DateTime nowUtc, string? correlationId, CancellationToken ct)
    {
        var ids = chunk.Select(c => c.Key.Value).Distinct().ToList();
        await using var db = Open();
        return await PooledIsolation.InTransactionAsync(db, System.Data.IsolationLevel.RepeatableRead, async () =>
        {
            var entities = await db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && ids.Contains(r.DeliveryKey))
                .ToDictionaryAsync(r => r.DeliveryKey, ct)
                .ConfigureAwait(false);
            var written = new List<DeliveryKey>();
            var moved = new List<DeliveryKey>();
            foreach (var (key, detail) in chunk)
            {
                if (!entities.TryGetValue(key.Value, out var entity) || entity.RestrictionId != restriction.RestrictionId || entity.RestrictedUtc is not null)
                {
                    moved.Add(key);
                    continue;
                }

                if (detail is not null)
                {
                    entity.RestrictedUtc = nowUtc;
                    db.DeliveryAttempts.Add(RestrictionAttempt(
                        entity, worker, runId, nowUtc, AttemptOutcome.Restricted, AttemptPhases.Restrict,
                        AttemptResult.Restriction(correlationId, entity.TargetStateJson, restriction.RestrictionId, restriction.Name, restriction.Acl.ToJson(), entity.RestrictedFromAcl, detail)));
                }
                else
                {
                    entity.RestrictionId = null;
                    entity.RestrictedFromAcl = null;
                    entity.PlanRequestedUtc = nowUtc;
                }

                entity.UpdatedUtc = nowUtc;
                written.Add(key);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ((IReadOnlyList<DeliveryKey>)written, (IReadOnlyList<DeliveryKey>)moved);
        }, ct).ConfigureAwait(false);
    }

    public async Task<RestrictionWrite> LiftRestrictionAsync(
        Guid flowId, IReadOnlyList<RestrictionLifted> lifted, IReadOnlyDictionary<int, AccessRestrictionState> restrictions, string? holder,
        string worker, Guid? runId, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lifted);
        ArgumentNullException.ThrowIfNull(restrictions);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var written = new HashSet<DeliveryKey>();
        var moved = new HashSet<DeliveryKey>();
        foreach (var chunk in lifted.GroupBy(l => l.Key).Select(g => g.Last()).Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var (w, m) = await RetryDeadlockAsync(() => LiftChunkAsync(partition, flowId, chunk, restrictions, holder, worker, runId, nowUtc, correlationId, ct), ct).ConfigureAwait(false);
            written.UnionWith(w);
            moved.UnionWith(m);
        }

        return new RestrictionWrite(written, moved);
    }

    /// <summary>
    /// One chunk of <see cref="LiftRestrictionAsync"/>: each record still restricted and no other lease holds is restricted no
    /// more, with an attempt naming the restriction lifted and the ACL written back, and is asked to be planned again, so it
    /// follows its source from here: the work its restriction dropped, and any change of its row since, is planned by the next
    /// run. A record OSDU no longer held is marked missing and has its hashes forgotten, so that run sends it again.
    /// </summary>
    private async Task<(IReadOnlyList<DeliveryKey> Written, IReadOnlyList<DeliveryKey> Moved)> LiftChunkAsync(
        short partition, Guid flowId, IReadOnlyList<RestrictionLifted> chunk, IReadOnlyDictionary<int, AccessRestrictionState> restrictions, string? holder,
        string worker, Guid? runId, DateTime nowUtc, string? correlationId, CancellationToken ct)
    {
        var ids = chunk.Select(c => c.Key.Value).ToList();
        await using var db = Open();
        return await PooledIsolation.InTransactionAsync(db, System.Data.IsolationLevel.RepeatableRead, async () =>
        {
            var entities = await db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && ids.Contains(r.DeliveryKey))
                .ToDictionaryAsync(r => r.DeliveryKey, ct)
                .ConfigureAwait(false);
            var written = new List<DeliveryKey>();
            var moved = new List<DeliveryKey>();
            foreach (var lift in chunk)
            {
                if (!entities.TryGetValue(lift.Key.Value, out var entity)
                    || entity.RestrictionId is not { } restrictionId
                    || (entity.LeaseOwner is not null && !string.Equals(entity.LeaseOwner, holder, StringComparison.Ordinal)))
                {
                    moved.Add(lift.Key);
                    continue;
                }

                var name = restrictions.TryGetValue(restrictionId, out var restriction) ? restriction.Name : restrictionId.ToString(CultureInfo.InvariantCulture);
                var writtenBack = lift.Lost || entity.Status == StatusText.Of(RecordStatus.Deleted) ? null : entity.RestrictedFromAcl;
                db.DeliveryAttempts.Add(RestrictionAttempt(
                    entity, worker, runId, nowUtc, AttemptOutcome.Unrestricted, AttemptPhases.LiftRestriction,
                    AttemptResult.Restriction(correlationId, entity.TargetStateJson, restrictionId, name, writtenBack, restriction?.Acl.ToJson(), lift.Detail)));
                entity.RestrictionId = null;
                entity.RestrictedUtc = null;
                entity.RestrictedFromAcl = null;
                entity.PlanRequestedUtc = nowUtc;
                if (lift.Lost)
                {
                    // As a verify that finds the record missing marks it: the next run sends it again under its id, bringing a
                    // soft-deleted record back first.
                    entity.LastVerifyOutcome = StatusText.Of(VerifyOutcome.Missing);
                    entity.LastVerifiedUtc = nowUtc;
                    entity.MetadataHash = null;
                    entity.PayloadHash = null;
                    entity.PayloadModifiedUtc = null;
                    entity.SourceFingerprint = null;
                    entity.PendingSourceFingerprint = null;
                }

                entity.UpdatedUtc = nowUtc;
                written.Add(lift.Key);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ((IReadOnlyList<DeliveryKey>)written, (IReadOnlyList<DeliveryKey>)moved);
        }, ct).ConfigureAwait(false);
    }

    private static DeliveryAttempt RestrictionAttempt(
        DeliveryRecord entity, string worker, Guid? runId, DateTime nowUtc, AttemptOutcome outcome, string phase, string resultJson) => new()
    {
        PartitionId = entity.PartitionId,
        FlowId = entity.FlowId,
        DeliveryKey = entity.DeliveryKey,
        SubmissionId = entity.LastSubmissionId,
        RunId = runId,
        Worker = worker,
        StartedUtc = nowUtc,
        CompletedUtc = nowUtc,
        Outcome = StatusText.Of(outcome),
        Phase = phase,
        MetadataHash = entity.MetadataHash,
        PayloadHash = entity.PayloadHash,
        TargetVersion = entity.TargetVersion,
        ResultJson = resultJson,
        SourceFileName = entity.SourceFileName,
        SourceRowNumber = entity.SourceRowNumber,
        SourceUpdatedUtc = entity.SourceUpdatedUtc,
    };
}
