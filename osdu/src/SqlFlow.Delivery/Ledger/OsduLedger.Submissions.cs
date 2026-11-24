using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Submissions and their work batches (osdu/docs/reference/concepts/submissions.md, The submission; Work batches): a
// submission registered by the intake and updated as its run goes, read back by its id and listed, with what its
// deliveries were sent for and the settled submissions that still have work due; and the work batches a submission's
// records are queued in, added, listed and counted.
public sealed partial class OsduLedger
{
    public async Task<SubmissionState?> GetSubmissionAsync(Guid submissionId, CancellationToken ct = default)
    {
        var entity = await ReadAsync(db => db.DeliverySubmissions.FirstOrDefaultAsync(s => s.SubmissionId == submissionId, ct), ct).ConfigureAwait(false);
        return entity is null ? null : await NamedAsync(entity, ct).ConfigureAwait(false);
    }

    /// <summary>A submission as the ledger holds it, naming its partition.</summary>
    private async Task<SubmissionState> NamedAsync(DeliverySubmission entity, CancellationToken ct)
        => ToState(entity) with { Partition = await PartitionNameAsync(entity.PartitionId, ct).ConfigureAwait(false) };

    public async Task<(SubmissionState Submission, bool Created)> RegisterSubmissionAsync(SubmissionState submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var partition = await WritePartitionAsync(submission.FlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var existing = await db.DeliverySubmissions.AsNoTracking().FirstOrDefaultAsync(s => s.SubmissionId == submission.SubmissionId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return (await NamedAsync(existing, ct).ConfigureAwait(false), false);
        }

        var entity = new DeliverySubmission { PartitionId = partition };
        Apply(entity, submission with { ReceivedUtc = submission.ReceivedUtc == default ? Now : submission.ReceivedUtc });
        db.DeliverySubmissions.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Lost a race with another intake of the same id: idempotent by construction.
            var raced = await db.DeliverySubmissions.AsNoTracking().FirstAsync(s => s.SubmissionId == submission.SubmissionId, ct).ConfigureAwait(false);
            return (await NamedAsync(raced, ct).ConfigureAwait(false), false);
        }

        return (await NamedAsync(entity, ct).ConfigureAwait(false), true);
    }

    public async Task UpdateSubmissionAsync(SubmissionState submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await using var db = Open();
        var entity = await db.DeliverySubmissions.FirstOrDefaultAsync(s => s.SubmissionId == submission.SubmissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submission.SubmissionId} is not in the ledger.");
        Apply(entity, submission);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubmissionState>> ListSubmissionsAsync(Guid? flowId, int max, string? partition = null, CancellationToken ct = default)
    {
        // Each partition's newest through its own range of (PartitionId, FlowId, ReceivedUtc) or (PartitionId, ReceivedUtc),
        // merged when the listing covers several.
        var take = Math.Clamp(max, 1, 1000);
        var partitions = await ReadPartitionsAsync(flowId, partition, ct).ConfigureAwait(false);
        var list = new List<DeliverySubmission>();
        foreach (var within in partitions)
        {
            list.AddRange(await ReadAsync(
                db =>
                {
                    var query = db.DeliverySubmissions.Where(s => s.PartitionId == within);
                    if (flowId is { } flow)
                    {
                        query = query.Where(s => s.FlowId == flow);
                    }

                    return query.OrderByDescending(s => s.ReceivedUtc).Take(take).ToListAsync(ct);
                },
                ct).ConfigureAwait(false));
        }

        if (partitions.Count > 1)
        {
            list = list.OrderByDescending(s => s.ReceivedUtc).Take(take).ToList();
        }

        var named = new List<SubmissionState>(list.Count);
        foreach (var row in list)
        {
            named.Add(await NamedAsync(row, ct).ConfigureAwait(false));
        }

        return named;
    }

    public async Task<SubmissionTriggerCounts> CountDeliveredTriggersAsync(Guid submissionId, CancellationToken ct = default)
    {
        var delivered = StatusText.Of(AttemptOutcome.Delivered);
        var total = await CountAttemptsAsync(submissionId, AttemptOutcome.Delivered, null, ct).ConfigureAwait(false);
        return await CountTriggersAsync(db => db.DeliveryAttempts.AsNoTracking().Where(a => a.SubmissionId == submissionId && a.Outcome == delivered), total, ct)
            .ConfigureAwait(false);
    }

    public async Task<SubmissionTriggerCounts> CountRunDeliveredTriggersAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runIds);
        List<Guid?> runs = [.. runIds.Distinct().Select(r => (Guid?)r)];
        if (runs.Count == 0)
        {
            return new SubmissionTriggerCounts(0, 0, []);
        }

        var delivered = StatusText.Of(AttemptOutcome.Delivered);
        IQueryable<DeliveryAttempt> Sent(OsduDbContext db) => db.DeliveryAttempts.AsNoTracking().Where(a => runs.Contains(a.RunId) && a.Outcome == delivered);

        // A record a run delivered in two of its ledgers is two records, as a run's own count has it: keys are per flow.
        var total = await ReadAsync(db => Sent(db).Select(a => new { a.FlowId, a.DeliveryKey }).Distinct().LongCountAsync(ct), ct).ConfigureAwait(false);
        return await CountTriggersAsync(Sent, total, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The delivered attempts <paramref name="sent"/> selects, grouped by the set of kinds each names: a few groups however
    /// many records, each counting its distinct records, then spread over the kinds in memory.
    /// </summary>
    private async Task<SubmissionTriggerCounts> CountTriggersAsync(Func<OsduDbContext, IQueryable<DeliveryAttempt>> sent, long total, CancellationToken ct)
    {
        var groups = await ReadAsync(
            db => sent(db)
                .GroupBy(a => a.Triggers)
                .Select(g => new { Triggers = g.Key, Records = g.Select(a => a.DeliveryKey).Distinct().LongCount() })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var byKind = Planning.ReasonKinds.All
            .Select(k => (k.Kind, Records: groups.Where(g => g.Triggers is { } set && (set & (int)k.Flag) != 0).Sum(g => g.Records)))
            .Where(k => k.Records > 0)
            .ToList();
        return new SubmissionTriggerCounts(total, groups.Where(g => g.Triggers is null).Sum(g => g.Records), byKind);
    }

    public async Task<IReadOnlyList<Guid>> ListSettledSubmissionsWithDueWorkAsync(Guid flowId, IReadOnlyCollection<Guid> except, DateTime nowUtc, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(except);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var pending = StatusText.Of(RecordStatus.Pending);
        var completed = StatusText.Of(SubmissionStatus.Completed);
        var failed = StatusText.Of(SubmissionStatus.Failed);
        return await ReadAsync(
            db =>
            {
                var excluded = except.ToArray();
                var settled = db.DeliverySubmissions
                    .Where(s => s.PartitionId == partition && s.FlowId == flowId && (s.Status == completed || s.Status == failed) && !excluded.Contains(s.SubmissionId))
                    .Select(s => s.SubmissionId);
                return db.DeliveryRecords
                    .Where(r => r.PartitionId == partition && r.FlowId == flowId
                        && r.Status == pending
                        && r.PendingDocumentRef != null
                        && (r.NextAttemptUtc == null || r.NextAttemptUtc <= nowUtc)
                        && r.LastSubmissionId != null
                        && settled.Contains(r.LastSubmissionId.Value))
                    .Select(r => r.LastSubmissionId!.Value)
                    .Distinct()
                    .Take(Math.Clamp(max, 1, 100))
                    .ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
    }

    public async Task AddWorkBatchAsync(WorkBatchState batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var partition = await WritePartitionAsync(batch.FlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var exists = await db.DeliveryWorkBatches.AnyAsync(b => b.SubmissionId == batch.SubmissionId && b.Index == batch.Index, ct).ConfigureAwait(false);
        if (exists)
        {
            await db.DeliveryWorkBatches
                .Where(b => b.SubmissionId == batch.SubmissionId && b.Index == batch.Index)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Location, batch.Location)
                    .SetProperty(b => b.RecordCount, batch.RecordCount)
                    .SetProperty(b => b.Status, StatusText.Of(WorkBatchStatus.Queued))
                    .SetProperty(b => b.LeaseOwner, (string?)null)
                    .SetProperty(b => b.CompletedUtc, (DateTime?)null)
                    .SetProperty(b => b.Error, (string?)null), ct)
                .ConfigureAwait(false);
            return;
        }

        db.DeliveryWorkBatches.Add(new DeliveryWorkBatch
        {
            PartitionId = partition,
            SubmissionId = batch.SubmissionId,
            Index = batch.Index,
            FlowId = batch.FlowId,
            Location = Truncate(batch.Location, 2000)!,
            RecordCount = batch.RecordCount,
            Status = StatusText.Of(WorkBatchStatus.Queued),
            CreatedUtc = batch.CreatedUtc == default ? Now : batch.CreatedUtc,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<WorkBatchState>> ListWorkBatchesAsync(Guid submissionId, int max, int offset, CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<WorkBatchState>>(
            async db =>
            {
                // A running batch shows when its lease runs out.
                var rows = await db.DeliveryWorkBatches
                    .Where(b => b.SubmissionId == submissionId)
                    .OrderBy(b => b.Index)
                    .Skip(Math.Max(0, offset))
                    .Take(Math.Clamp(max, 1, 1000))
                    .Select(b => new
                    {
                        Batch = b,
                        Expires = db.DeliveryLeases.Where(l => l.PartitionId == b.PartitionId && l.Token == b.LeaseOwner).Select(l => (DateTime?)l.ExpiresUtc).FirstOrDefault(),
                    })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                return rows.Select(r => ToState(r.Batch) with { LeaseExpiresUtc = r.Expires }).ToList();
            },
            ct);

    public Task<int?> LastWorkBatchIndexAsync(Guid submissionId, int first, int beyond, CancellationToken ct = default)
        => ReadAsync(
            db => db.DeliveryWorkBatches
                .Where(b => b.SubmissionId == submissionId && b.Index >= first && b.Index < beyond)
                .MaxAsync(b => (int?)b.Index, ct),
            ct);

    public async Task<IReadOnlyList<int>> ListQueuedWorkBatchesAsync(Guid flowId, Guid submissionId, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var pending = StatusText.Of(RecordStatus.Pending);
        var delivering = StatusText.Of(RecordStatus.Delivering);
        var waiting = StatusText.Of(RecordStatus.Waiting);

        // The index of a submission's batches holds a record only while it points into one: a record that landed, or was held
        // without its document, names none. So this reads what is left of the submission, however many records it delivered.
        return await ReadAsync(
            db => db.DeliveryRecords
                .Where(r => r.LastSubmissionId == submissionId && r.WorkBatch != null && r.PartitionId == partition && r.FlowId == flowId
                    && r.PendingDocumentRef != null && (r.Status == pending || r.Status == delivering || r.Status == waiting))
                .Select(r => r.WorkBatch!.Value)
                .Distinct()
                .OrderBy(batch => batch)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public Task<long> CountWorkBatchesAsync(Guid submissionId, WorkBatchStatus? status, CancellationToken ct = default)
        => ReadAsync(
            db =>
            {
                var query = db.DeliveryWorkBatches.Where(b => b.SubmissionId == submissionId);
                if (status is { } s)
                {
                    var text = StatusText.Of(s);
                    query = query.Where(b => b.Status == text);
                }

                return query.LongCountAsync(ct);
            },
            ct);

    private static void Apply(DeliverySubmission entity, SubmissionState s)
    {
        entity.SubmissionId = s.SubmissionId;
        entity.FlowId = s.FlowId;
        entity.FlowName = s.FlowName;
        entity.MappingReference = s.MappingReference;
        entity.RenderContext = s.RenderContext;
        entity.WorkLocation = Truncate(s.WorkLocation, 2000);
        entity.BatchCount = s.BatchCount;
        entity.Slices = s.Slices;
        entity.ParametersJson = s.ParametersJson;
        entity.RecordCount = s.RecordCount;
        entity.Status = StatusText.Of(s.Status);
        entity.ReceivedUtc = s.ReceivedUtc;
        entity.StartedUtc = s.StartedUtc;
        entity.CompletedUtc = s.CompletedUtc;
        entity.Planned = s.Planned;
        entity.SkippedUnchanged = s.SkippedUnchanged;
        entity.AwaitingApproval = s.AwaitingApproval;
        entity.SkippedStale = s.SkippedStale;
        entity.UnchangedAtPush = s.UnchangedAtPush;
        entity.Blocked = s.Blocked;
        entity.Delivered = s.Delivered;
        entity.Held = s.Held;
        entity.Failed = s.Failed;
        entity.Waiting = s.Waiting;
        entity.Error = Truncate(s.Error, 4000);
        entity.Kind = SubmissionKinds.All.Contains(s.Kind, StringComparer.Ordinal)
            ? s.Kind
            : throw new DeliveryException($"Submission {s.SubmissionId:D}: '{s.Kind}' is not a submission kind; it is one of {string.Join(", ", SubmissionKinds.All)}.");
        entity.Untracked = s.Untracked;
        entity.SourceConnection = Truncate(s.SourceConnection, 400)!;
        entity.SourceObject = Truncate(s.SourceObject, 400)!;
        entity.WindowFromUtc = s.WindowFromUtc;
        entity.WindowToUtc = s.WindowToUtc;
        entity.SourceWindowJson = s.SourceWindowJson;
        entity.RunId = s.RunId;
    }
}
