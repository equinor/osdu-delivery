using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// The audit trail (osdu/docs/reference/concepts/ledger.md, osdu.Activity and osdu.ActivityRecord): what retention
// prunes of attempts and activity logs, the retrievals of retrieval flows, and the activities, started, completed, read
// and listed narrowed to a flow, a submission, a run, a kind, an actor, an outcome or a time.
public sealed partial class OsduLedger
{
    public async Task<IReadOnlySet<Guid>> FlowsWithActivitiesAsync(IReadOnlyCollection<Guid> flowIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flowIds);
        if (flowIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // An EXISTS per identity, as for the records: a partition's trail is mostly scheduled runs, and a DISTINCT over it
        // would read all of them to learn which flows ran.
        var active = await ReadAsync(db => SqlServerLedgerBulk.FlowsWithActivitiesAsync(db, flowIds, ct), ct).ConfigureAwait(false);
        return active.ToHashSet();
    }

    /// <summary>
    /// The most attempts one prune statement removes (4,000; tests lower it). Each statement is its own short transaction,
    /// so pruning an estate's history never holds a long lock on the attempt table, which every drain appends to, or writes
    /// one huge log record; and it stays under the 5,000 row locks at which SQL Server would lock the whole table instead.
    /// </summary>
    internal int PruneBatch { get; init; } = 4_000;

    public async Task<int> PruneAttemptsAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PruneBatch, 1);
        var pruned = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = Open();
            // The old attempts are read in start order from the start index, and the later one is a seek of the record's own
            // timeline.
            var deleted = await PrunableAttempts(db, olderThanUtc)
                .OrderBy(a => a.StartedUtc)
                .Take(PruneBatch)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            pruned += deleted;
            if (deleted < PruneBatch)
            {
                return pruned;
            }
        }
    }

    public async Task<long> CountPrunableAttemptsAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        return await PrunableAttempts(db, olderThanUtc).LongCountAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The attempts a prune at <paramref name="olderThanUtc"/> takes, defined once for the prune and its count: an attempt goes
    /// only when a later attempt of the same flow's record exists, so a record's last outcome is always explainable.
    /// </summary>
    private static IQueryable<DeliveryAttempt> PrunableAttempts(OsduDbContext db, DateTime olderThanUtc)
        => db.DeliveryAttempts.Where(a => a.StartedUtc < olderThanUtc
            && db.DeliveryAttempts.Any(b => b.PartitionId == a.PartitionId && b.FlowId == a.FlowId && b.DeliveryKey == a.DeliveryKey && b.AttemptId > a.AttemptId));

    /// <summary>
    /// The most activities one statement clears the log of. Each batch is its own short statement, so clearing years of logs
    /// never holds a long lock on the table every run appends an activity to, and never takes enough row locks for SQL Server
    /// to lock the whole table instead.
    /// </summary>
    internal int ActivityLogBatch { get; init; } = 1_000;

    public async Task<int> ClearActivityLogsAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ActivityLogBatch, 1);
        var cleared = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = Open();
            var batch = await ClearableActivityLogs(db, olderThanUtc)
                .OrderBy(a => a.StartedUtc)
                .Select(a => a.ActivityId)
                .Take(ActivityLogBatch)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (batch.Count == 0)
            {
                return cleared;
            }

            cleared += await db.DeliveryActivities
                .Where(a => batch.Contains(a.ActivityId))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Log, (string?)null), ct)
                .ConfigureAwait(false);
            if (batch.Count < ActivityLogBatch)
            {
                return cleared;
            }
        }
    }

    public async Task<long> CountActivityLogsAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        return await ClearableActivityLogs(db, olderThanUtc).LongCountAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The activities whose log a clear at <paramref name="olderThanUtc"/> takes: finished, started before the cut-off,
    /// and still holding a log.</summary>
    private static IQueryable<DeliveryActivity> ClearableActivityLogs(OsduDbContext db, DateTime olderThanUtc)
        => db.DeliveryActivities.Where(a => a.StartedUtc < olderThanUtc && a.CompletedUtc != null && a.Log != null);

    // ---- Retrievals ----------------------------------------------------------------------------------------------

    public async Task<RetrievalState> StartRetrievalAsync(RetrievalState retrieval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(retrieval);
        var partition = await WritePartitionAsync(retrieval.FlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var entity = new DeliveryRetrieval
        {
            PartitionId = partition,
            FlowId = retrieval.FlowId,
            FlowName = retrieval.FlowName,
            RunId = retrieval.RunId,
            Actor = retrieval.Actor,
            Kinds = retrieval.Kinds,
            Query = retrieval.Query,
            WindowField = retrieval.WindowField,
            WindowFrom = retrieval.WindowFrom,
            WindowTo = retrieval.WindowTo,
            Location = retrieval.Location,
            ManifestLocation = retrieval.ManifestLocation,
            Status = retrieval.Status,
            Records = retrieval.Records,
            Files = retrieval.Files,
            Bytes = retrieval.Bytes,
            StartedUtc = retrieval.StartedUtc,
            CompletedUtc = retrieval.CompletedUtc,
            Error = Truncate(retrieval.Error, 4000),
        };
        db.DeliveryRetrievals.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return retrieval with { RetrievalId = entity.RetrievalId };
    }

    public async Task CompleteRetrievalAsync(long retrievalId, string status, long records, int files, long bytes, string? manifestLocation, string? failure, DateTime completedUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        await using var db = Open();
        var entity = await db.DeliveryRetrievals.FirstOrDefaultAsync(r => r.RetrievalId == retrievalId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Retrieval {retrievalId} is not in the ledger.");
        entity.Status = status;
        entity.Records = records;
        entity.Files = files;
        entity.Bytes = bytes;
        entity.ManifestLocation = manifestLocation ?? entity.ManifestLocation;
        entity.Error = Truncate(failure, 4000);
        entity.CompletedUtc = completedUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<RetrievalState?> LastRetrievalAsync(Guid flowId, string status, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var entity = await ReadAsync(
            db => db.DeliveryRetrievals
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && r.Status == status)
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.RetrievalId)
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
        return entity is null ? null : ToState(entity);
    }

    public async Task<IReadOnlyList<RetrievalState>> ListRetrievalsAsync(Guid flowId, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var rows = await ReadAsync(
            db => db.DeliveryRetrievals
                .Where(r => r.PartitionId == partition && r.FlowId == flowId)
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.RetrievalId)
                .Take(Math.Clamp(max, 1, 1000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    private static RetrievalState ToState(DeliveryRetrieval r) => new()
    {
        RetrievalId = r.RetrievalId,
        FlowId = r.FlowId,
        FlowName = r.FlowName,
        RunId = r.RunId,
        Actor = r.Actor,
        Kinds = r.Kinds,
        Query = r.Query,
        WindowField = r.WindowField,
        WindowFrom = r.WindowFrom is { } from ? DateTime.SpecifyKind(from, DateTimeKind.Utc) : null,
        WindowTo = r.WindowTo is { } to ? DateTime.SpecifyKind(to, DateTimeKind.Utc) : null,
        Location = r.Location,
        ManifestLocation = r.ManifestLocation,
        Status = r.Status,
        Records = r.Records,
        Files = r.Files,
        Bytes = r.Bytes,
        StartedUtc = DateTime.SpecifyKind(r.StartedUtc, DateTimeKind.Utc),
        CompletedUtc = r.CompletedUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
        Error = r.Error,
    };

    public async Task<ActivityRecord> StartActivityAsync(ActivityRecord activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var partition = await WritePartitionAsync(activity.FlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var entity = new DeliveryActivity
        {
            PartitionId = partition,
            FlowId = activity.FlowId,
            FlowName = activity.FlowName,
            Kind = activity.Kind,
            Actor = Truncate(activity.Actor, 200)!,
            StartedUtc = activity.StartedUtc == default ? Now : activity.StartedUtc,
            CompletedUtc = activity.CompletedUtc,
            Outcome = activity.Outcome,
            ParametersJson = activity.ParametersJson,
            SubmissionId = activity.SubmissionId,
            DeliveryKey = activity.DeliveryKey,
            RunId = activity.RunId,
            Summary = Truncate(activity.Summary, 2000),
            Log = Truncate(activity.Log, MaxLogLength),
        };
        db.DeliveryActivities.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToRecord(entity) with { Partition = await PartitionNameAsync(partition, ct).ConfigureAwait(false) };
    }

    public async Task CompleteActivityAsync(long activityId, string outcome, string? summary, string? log, DateTime completedUtc, Guid? submissionId = null, bool idle = false, CancellationToken ct = default)
    {
        await using var db = Open();
        var entity = await db.DeliveryActivities.FirstOrDefaultAsync(a => a.ActivityId == activityId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Activity {activityId} is not in the ledger.");
        entity.Outcome = outcome;
        entity.SubmissionId ??= submissionId;
        entity.Summary = Truncate(summary, 2000);
        entity.Log = Truncate(log, MaxLogLength);
        entity.CompletedUtc = completedUtc;
        // A failed or cancelled run is never idle: the trail always shows it.
        entity.Idle = idle && outcome == "completed";
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<ActivityRecord?> GetActivityAsync(long activityId, CancellationToken ct = default)
    {
        var entity = await ReadAsync(db => db.DeliveryActivities.FirstOrDefaultAsync(a => a.ActivityId == activityId, ct), ct).ConfigureAwait(false);
        return entity is null ? null : ToRecord(entity) with { Partition = await PartitionNameAsync(entity.PartitionId, ct).ConfigureAwait(false) };
    }

    public async Task<IReadOnlyList<ActivityRecord>> ListActivitiesAsync(ActivityQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await ActivityPartitionAsync(query, ct).ConfigureAwait(false) is not { } scope)
        {
            return [];
        }

        var skip = Math.Max(0, query.Offset);
        var take = Math.Clamp(query.Max, 1, 1000);
        List<DeliveryActivity> list;
        if (scope.PartitionId is null && query.SubmissionId is null && query.RunId is null)
        {
            // Every partition's trail: each partition's newest through its own range of (PartitionId, StartedUtc), merged,
            // rather than one sort of every activity kept. One narrowed to a submission or a run seeks that id's index.
            var merged = new List<DeliveryActivity>();
            foreach (var partition in await EveryPartitionIdAsync(ct).ConfigureAwait(false))
            {
                merged.AddRange(await ActivityPageAsync(query, partition, 0, skip + take, ct).ConfigureAwait(false));
            }

            list = merged.OrderByDescending(a => a.StartedUtc).ThenByDescending(a => a.ActivityId).Skip(skip).Take(take).ToList();
        }
        else
        {
            list = await ActivityPageAsync(query, scope.PartitionId, skip, take, ct).ConfigureAwait(false);
        }

        var names = await PartitionNamesAsync(list.Select(a => a.PartitionId), ct).ConfigureAwait(false);
        return list.Select(a => ToRecord(a) with { Partition = names[a.PartitionId] }).ToList();
    }

    /// <summary>One page of activities <paramref name="query"/> selects in <paramref name="partition"/> (every one for null), newest first, without their logs.</summary>
    private Task<List<DeliveryActivity>> ActivityPageAsync(ActivityQuery query, short? partition, int skip, int take, CancellationToken ct)
        => ReadAsync(
            db => FilterActivities(db, query, partition)
                .OrderByDescending(a => a.StartedUtc)
                .ThenByDescending(a => a.ActivityId)
                .Skip(skip)
                .Take(take)
                .Select(a => new DeliveryActivity
                {
                    PartitionId = a.PartitionId,
                    ActivityId = a.ActivityId,
                    FlowId = a.FlowId,
                    FlowName = a.FlowName,
                    Kind = a.Kind,
                    Actor = a.Actor,
                    StartedUtc = a.StartedUtc,
                    CompletedUtc = a.CompletedUtc,
                    Outcome = a.Outcome,
                    ParametersJson = a.ParametersJson,
                    SubmissionId = a.SubmissionId,
                    DeliveryKey = a.DeliveryKey,
                    RunId = a.RunId,
                    Summary = a.Summary,
                    // The log is fetched per activity, not in listings.
                    Log = null,
                    Idle = a.Idle,
                })
                .ToListAsync(ct),
            ct);

    public async Task<int> CountActivitiesAsync(ActivityQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await ActivityPartitionAsync(query, ct).ConfigureAwait(false) is not { } scope)
        {
            return 0;
        }

        return await ReadAsync(db => FilterActivities(db, query, scope.PartitionId).CountAsync(ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The partition an activity listing reads: its ledger's, when it names a ledger identity; the one it names; or every
    /// partition (a null number). Null when the ledger or the partition holds no activity to read, or when it names both
    /// and the ledger belongs to another partition.
    /// </summary>
    private async Task<ActivityScope?> ActivityPartitionAsync(ActivityQuery query, CancellationToken ct)
    {
        if (query.FlowId is { } flowId)
        {
            var own = await PartitionOfAsync(flowId, ct).ConfigureAwait(false);
            return own is not null && (string.IsNullOrWhiteSpace(query.Partition) || await PartitionIdOfAsync(query.Partition, ct).ConfigureAwait(false) == own)
                ? new ActivityScope(own)
                : null;
        }

        if (!string.IsNullOrWhiteSpace(query.Partition))
        {
            return await PartitionIdOfAsync(query.Partition, ct).ConfigureAwait(false) is { } named ? new ActivityScope(named) : null;
        }

        return new ActivityScope(null);
    }

    /// <summary>The partition an activity listing reads, or null for every partition.</summary>
    private sealed record ActivityScope(short? PartitionId);

    private static IQueryable<DeliveryActivity> FilterActivities(OsduDbContext db, ActivityQuery query, short? partition)
    {
        var rows = db.DeliveryActivities.AsQueryable();
        if (partition is { } within)
        {
            rows = rows.Where(a => a.PartitionId == within);
        }

        if (query.FlowId is { } flowId)
        {
            rows = rows.Where(a => a.FlowId == flowId);
        }

        if (query.DeliveryKey is { } key)
        {
            if (partition is { } ledgerPartition && query.FlowId is { } ledger)
            {
                // A record's requests: one made for the record alone names it on its own row, and one made for many
                // records names it among the records it reached, a seek of the record's key there and of each activity's
                // id here. The two never meet, so the record's requests are both together, each once.
                var reached = db.DeliveryActivityRecords
                    .Where(l => l.PartitionId == ledgerPartition && l.FlowId == ledger && l.DeliveryKey == key)
                    .Select(l => l.ActivityId);
                rows = rows.Where(a => a.DeliveryKey == key)
                    .Concat(rows.Where(a => (a.DeliveryKey == null || a.DeliveryKey != key) && reached.Contains(a.ActivityId)));
            }
            else
            {
                rows = rows.Where(a => a.DeliveryKey == key);
            }
        }

        if (query.SubmissionId is { } submissionId)
        {
            rows = rows.Where(a => a.SubmissionId == submissionId);
        }

        if (query.RunId is { } runId)
        {
            rows = rows.Where(a => a.RunId == runId);
        }

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            rows = rows.Where(a => a.Kind == query.Kind);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            var actor = query.Actor.Trim();
            rows = rows.Where(a => a.Actor.StartsWith(actor));
        }

        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            rows = rows.Where(a => a.Outcome == query.Outcome);
        }

        if (query.Idle is { } idle)
        {
            rows = rows.Where(a => a.Idle == idle);
        }

        if (query.SinceUtc is { } since)
        {
            rows = rows.Where(a => a.StartedUtc >= since);
        }

        if (query.UntilUtc is { } until)
        {
            rows = rows.Where(a => a.StartedUtc < until);
        }

        return rows;
    }

    private static ActivityRecord ToRecord(DeliveryActivity a) => new()
    {
        ActivityId = a.ActivityId,
        FlowId = a.FlowId,
        FlowName = a.FlowName,
        Kind = a.Kind,
        Actor = a.Actor,
        StartedUtc = a.StartedUtc,
        CompletedUtc = a.CompletedUtc,
        Outcome = a.Outcome,
        ParametersJson = a.ParametersJson,
        SubmissionId = a.SubmissionId,
        DeliveryKey = a.DeliveryKey,
        RunId = a.RunId,
        Summary = a.Summary,
        Log = a.Log,
        Idle = a.Idle,
    };
}
