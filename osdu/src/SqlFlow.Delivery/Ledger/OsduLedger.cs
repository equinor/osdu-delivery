using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The ledger over the module database's <c>osdu</c> schema (<see cref="OsduDbContext"/>). A claim is one lease row
/// (osdu/docs/reference/concepts/submissions.md, Leases and claims): the worker takes a work batch, or a group of
/// records due for a retry, under a new token in one compare-and-swap, the records it holds carry the token, and a
/// crashed worker's lease simply runs out and is recovered by the next claim of its flow. While it delivers, a worker
/// renews its one row and appends what it learns; the lease applies that to the records a slice at a time. A read takes
/// no transaction of its own and does not rely on
/// snapshot isolation: one that needs a record and its lease asks for both in one statement, and one chosen as a
/// deadlock victim is read again (see ReadAsync). Every query the GUI issues is index-backed (see
/// <see cref="DeliveryModel"/>). Each operation opens its own
/// context from the factory, so the ledger is safe to share across the worker's bounded concurrency. The ledger runs on
/// SQL Server alone, and the volume writes (staging pending records, appending and applying a lease's events) go through
/// a bulk copy and set-based statements (<see cref="SqlServerLedgerBulk"/>).
/// </summary>
public sealed partial class OsduLedger : LedgerStore, ILedger
{
    /// <summary>A change whose records are all marked, waiting for the flows that build them to render them again.</summary>
    private const string Delivering = "delivering";

    private const string DeletedStatus = "deleted";

    private const string RevertedStatus = "reverted";

    private const string HeldStatus = "held";

    private const string FailedStatus = "failed";

    /// <summary>
    /// Cache sets this ledger has already resolved, by hash, each with when it was last checked against the database. Records
    /// that read the same values share a set, so this spares a query per record of a run; an entry older than
    /// <see cref="CacheSetKeeping.RecheckAfter"/> is checked again, so a set the sweep removed is never handed out.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, KnownCacheSet> _cacheSets = new(StringComparer.Ordinal);

    private const int MaxLogLength = 200_000;

    private const int ChunkSize = 500;

    /// <summary>
    /// Records whose identity tokens are rewritten in one statement round. It is smaller than a record chunk because
    /// each record carries up to <see cref="RecordIdentityLimits.MaxPerRecord"/> rows of its own.
    /// </summary>
    private const int IdentityChunk = 100;

    /// <summary>The most records a contains search reads (<see cref="RecordListing.ContainsScanLimit"/>); tests lower it.</summary>
    internal int ContainsScanLimit { get; init; } = RecordListing.ContainsScanLimit;

    /// <summary>
    /// The most records one statement writes when a write can reach more (1,000; tests lower it): a staging slice, a
    /// batch's lease, a release or redelivery. SQL Server turns the row locks of a statement holding 5,000 of them on one
    /// index into a lock on the whole table, which would stop every other node and flow writing records while it ran; a
    /// record's write takes a lock in each index it changes, and at most two there.
    /// </summary>
    internal int WriteSlice { get; init; } = 1_000;

    /// <param name="factory">Makes a context on the module's database.</param>
    /// <param name="time">The clock; the system's unless given.</param>
    /// <param name="secrets">
    /// Resolves the connection a dimension flow's <c>target.connection</c> names, so the ledger reaches a table kept in another
    /// database; null on a host that resolves none, where such a table cannot be reached.
    /// </param>
    public OsduLedger(Func<OsduDbContext> factory, TimeProvider? time = null, SqlFlow.Core.Secrets.ISecretResolver? secrets = null)
        : this(new LedgerDatabase(factory, time ?? TimeProvider.System), secrets)
    {
    }

    /// <summary>The ledger over <paramref name="database"/>, with the one directory of partitions it shares with every store it makes.</summary>
    private OsduLedger(LedgerDatabase database, SqlFlow.Core.Secrets.ISecretResolver? secrets)
        : base(database, new LedgerDirectory(database))
    {
        Downloads = new DownloadStore(Database, Partitions);
        Assertions = new AssertionStore(Database, Partitions);
        Inventories = new InventoryStore(Database, Partitions);
        Dimensions = new DimensionStore(Database, Partitions, secrets);
    }

    public DownloadStore Downloads { get; }

    public AssertionStore Assertions { get; }

    public InventoryStore Inventories { get; }

    public DimensionStore Dimensions { get; }

    /// <summary>
    /// Runs <paramref name="write"/> over every record <paramref name="rows"/> matches: the records' keys are read first,
    /// by a plain read that holds no lock past the row it is on, and then written <see cref="WriteSlice"/> to a statement
    /// (<see cref="WriteByKeyAsync"/>). The keys take 32 bytes a record.
    /// </summary>
    private Task<int> WriteEachAsync(IQueryable<DeliveryRecord> rows, Func<IQueryable<DeliveryRecord>, Task<int>> write, CancellationToken ct)
        => WriteEachAsync(rows, rows, write, ct);

    /// <summary>
    /// As <see cref="WriteEachAsync(IQueryable{DeliveryRecord}, Func{IQueryable{DeliveryRecord}, Task{int}}, CancellationToken)"/>,
    /// with the keys read from <paramref name="find"/>: records that include every one <paramref name="rows"/> matches, and
    /// that one index answers alone, however many other records the table holds. A found record <paramref name="rows"/>
    /// does not match is left alone by its write.
    /// </summary>
    private async Task<int> WriteEachAsync(IQueryable<DeliveryRecord> find, IQueryable<DeliveryRecord> rows, Func<IQueryable<DeliveryRecord>, Task<int>> write, CancellationToken ct)
    {
        var keys = await RetryDeadlockAsync(() => find.Select(r => new RecordKey(r.PartitionId, r.FlowId, r.DeliveryKey)).ToListAsync(ct), ct).ConfigureAwait(false);
        return await WriteByKeyAsync(rows, keys, write, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="write"/> over the records of <paramref name="rows"/> that <paramref name="keys"/> names,
    /// <see cref="WriteSlice"/> to a statement. Each statement names its partition, flow and keys, the record table's key,
    /// which every index of the table ends with, so the database finds each record with one seek and reads, and locks, no
    /// record it does not write. The slices go in the order of the table's key, as their keys do, so this write takes its
    /// records in the order every write of named records takes them (<see cref="SqlServerLedgerBulk.InKeyOrder"/>).
    /// <paramref name="rows"/> still applies, so a record that changed since its key was read is left as it now is.
    /// </summary>
    private async Task<int> WriteByKeyAsync(IQueryable<DeliveryRecord> rows, IEnumerable<RecordKey> keys, Func<IQueryable<DeliveryRecord>, Task<int>> write, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        var written = 0;
        foreach (var ledger in keys.GroupBy(k => (k.PartitionId, k.FlowId)))
        {
            var (partition, flowId) = ledger.Key;
            foreach (var slice in SqlServerLedgerBulk.InKeyOrderOf(ledger.Select(k => k.DeliveryKey)).Chunk(WriteSlice))
            {
                ct.ThrowIfCancellationRequested();
                written += await RetryDeadlockAsync(() => write(rows.Where(r => r.PartitionId == partition && r.FlowId == flowId && slice.Contains(r.DeliveryKey))), ct).ConfigureAwait(false);
            }
        }

        return written;
    }

    /// <summary>A record's key: its partition, its ledger identity and its delivery key.</summary>
    private readonly record struct RecordKey(short PartitionId, Guid FlowId, Guid DeliveryKey);

    /// <summary>
    /// The partitions a read across ledgers covers: the one <paramref name="flowId"/>'s ledger belongs to, the one
    /// <paramref name="partition"/> names, or every partition the ledger keeps rows under, the unassigned included; none
    /// when the ledger or the partition holds nothing. A ledger and a partition together are both filters, so a ledger of
    /// another partition than the one named reads as empty. Each is read through its own range of a partition-first index.
    /// </summary>
    private async Task<IReadOnlyList<short>> ReadPartitionsAsync(Guid? flowId, string? partition, CancellationToken ct)
    {
        if (flowId is { } flow)
        {
            if (await PartitionOfAsync(flow, ct).ConfigureAwait(false) is not { } own)
            {
                return [];
            }

            return string.IsNullOrWhiteSpace(partition) || await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) == own ? [own] : [];
        }

        if (!string.IsNullOrWhiteSpace(partition))
        {
            return await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is { } named ? [named] : [];
        }

        return await EveryPartitionIdAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Every partition number a ledger row can carry: the unassigned one, and each partition the directory numbers.</summary>
    private async Task<IReadOnlyList<short>> EveryPartitionIdAsync(CancellationToken ct)
    {
        var every = await ReadAsync(db => db.DeliveryLedgerPartitions.AsNoTracking().Select(p => p.PartitionId).ToListAsync(ct), ct).ConfigureAwait(false);
        return [Unassigned, .. every.Order()];
    }

    /// <summary>
    /// Marks the named records of one ledger for redelivery of <paramref name="scope"/>, <see cref="WriteSlice"/> to a
    /// statement, each naming the records it marked under <paramref name="activityId"/> when given: the one statement a
    /// redelivery by name, a redelivery of the whole flow and a cache change's rollout all mark records with. With
    /// <paramref name="lost"/>, each is also marked as OSDU no longer holding it, as a verify that finds it missing marks it.
    /// </summary>
    private async Task<int> RedeliverKeysAsync(
        OsduDbContext db, short partition, Guid flowId, IEnumerable<Guid> keys, RedeliverScope scope, string? marker, bool deliveredOnly, long? activityId,
        string note, DateTime nowUtc, CancellationToken ct, bool lost = false)
    {
        var named = scope switch
        {
            RedeliverScope.Metadata => "metadata",
            RedeliverScope.Payload => "payload",
            _ => "all",
        };
        var marked = 0;
        foreach (var slice in keys.Distinct().Chunk(WriteSlice))
        {
            ct.ThrowIfCancellationRequested();
            marked += await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.RedeliverSliceAsync(db, partition, flowId, slice, named, marker, deliveredOnly, activityId, note, nowUtc, ct, lost), ct).ConfigureAwait(false);
        }

        return marked;
    }

    /// <summary>
    /// What taking a record out of OSDU leaves of it in the ledger: marked removed and not blocked, every hash, version and
    /// queued piece of work forgotten, the note as its last word, and, with <paramref name="deliverAgain"/>, asked to be planned
    /// again. The id it claimed and the origin of the row it was last delivered from stay.
    /// </summary>
    private static void Retire(DeliveryRecord entity, string note, DateTime nowUtc, bool deliverAgain)
    {
        entity.Status = StatusText.Of(RecordStatus.Deleted);
        entity.Blocked = false;
        entity.ProblemHash = null;
        if (deliverAgain)
        {
            entity.PlanRequestedUtc = nowUtc;
        }

        entity.PendingSourceFingerprint = null;
        entity.PendingSourceModifiedUtc = null;
        entity.TargetVersion = null;
        entity.TargetStateJson = null;
        entity.MetadataHash = null;
        entity.PayloadHash = null;
        entity.PayloadModifiedUtc = null;
        entity.SourceFingerprint = null;
        entity.SourceModifiedUtc = null;
        entity.RenderPaths = null;
        entity.LastVerifiedUtc = null;
        entity.HeldSteps = null;
        entity.LastVerifyOutcome = null;
        entity.LeaseOwner = null;
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
        entity.LastError = note;
        entity.UpdatedUtc = nowUtc;
    }

    private static DeliveryAttempt ToEntity(short partition, Guid flowId, AttemptRecord attempt) => new()
    {
        PartitionId = partition,
        FlowId = flowId,
        DeliveryKey = attempt.DeliveryKey.Value,
        SubmissionId = attempt.SubmissionId,
        RunId = attempt.RunId,
        Worker = Truncate(attempt.Worker, 200)!,
        StartedUtc = attempt.StartedUtc,
        CompletedUtc = attempt.CompletedUtc,
        Outcome = StatusText.Of(attempt.Outcome),
        Phase = Truncate(attempt.Phase, 32)!,
        MetadataHash = attempt.MetadataHash,
        PayloadHash = attempt.PayloadHash,
        TargetVersion = attempt.TargetVersion,
        Error = Truncate(attempt.Error, 2000),
        ResultJson = attempt.ResultJson,
        Triggers = attempt.Triggers is { } triggers ? (int)triggers : null,
        WorkBatch = attempt.WorkBatch,
        SourceFileName = Truncate(attempt.SourceFileName, DeliveryModel.MaxSourceFileNameLength),
        SourceRowNumber = attempt.SourceRowNumber,
        SourceUpdatedUtc = attempt.SourceUpdatedUtc,
        SourceDeletedUtc = attempt.SourceDeletedUtc,
        ParentVersion = attempt.ParentVersion,
    };

    private static WorkBatchState ToState(DeliveryWorkBatch b) => new()
    {
        SubmissionId = b.SubmissionId,
        FlowId = b.FlowId,
        Index = b.Index,
        Location = b.Location,
        RecordCount = b.RecordCount,
        Status = StatusText.ToWorkBatchStatus(b.Status),
        LeaseOwner = b.LeaseOwner,
        RunId = b.RunId,
        CreatedUtc = b.CreatedUtc,
        StartedUtc = b.StartedUtc,
        CompletedUtc = b.CompletedUtc,
        Delivered = b.Delivered,
        Held = b.Held,
        Failed = b.Failed,
        Retrying = b.Retrying,
        Waiting = b.Waiting,
        Error = b.Error,
    };

    private static SubmissionState ToState(DeliverySubmission e) => new()
    {
        SubmissionId = e.SubmissionId,
        FlowId = e.FlowId,
        FlowName = e.FlowName,
        MappingReference = e.MappingReference,
        RenderContext = e.RenderContext,
        WorkLocation = e.WorkLocation,
        BatchCount = e.BatchCount,
        Slices = e.Slices,
        ParametersJson = e.ParametersJson,
        RecordCount = e.RecordCount,
        Status = StatusText.ToSubmissionStatus(e.Status),
        ReceivedUtc = e.ReceivedUtc,
        StartedUtc = e.StartedUtc,
        CompletedUtc = e.CompletedUtc,
        Planned = e.Planned,
        SkippedUnchanged = e.SkippedUnchanged,
        AwaitingApproval = e.AwaitingApproval,
        SkippedStale = e.SkippedStale,
        UnchangedAtPush = e.UnchangedAtPush,
        Blocked = e.Blocked,
        Delivered = e.Delivered,
        Held = e.Held,
        Failed = e.Failed,
        Waiting = e.Waiting,
        Error = e.Error,
        Kind = e.Kind,
        Untracked = e.Untracked,
        SourceConnection = e.SourceConnection,
        SourceObject = e.SourceObject,
        WindowFromUtc = e.WindowFromUtc is { } from ? DateTime.SpecifyKind(from, DateTimeKind.Utc) : null,
        WindowToUtc = e.WindowToUtc is { } to ? DateTime.SpecifyKind(to, DateTimeKind.Utc) : null,
        SourceWindowJson = e.SourceWindowJson,
        RunId = e.RunId,
    };

    private static RecordState ToState(DeliveryRecord r) => new()
    {
        DeliveryKey = new DeliveryKey(r.DeliveryKey),
        FlowId = r.FlowId,
        SourceKey = r.SourceKey,
        SourceKeyJson = r.SourceKeyJson,
        Label = r.Label,
        MappingName = r.MappingName,
        RenderContext = r.RenderContext,
        RenderCommit = r.RenderCommit,
        RenderPaths = r.RenderPaths is { } paths ? Convert.ToBase64String(paths) : null,
        SourceFingerprint = r.SourceFingerprint,
        SourceModifiedUtc = r.SourceModifiedUtc,
        SourceFileName = r.SourceFileName,
        SourceRowNumber = r.SourceRowNumber,
        SourceUpdatedUtc = r.SourceUpdatedUtc,
        SourceInsertedUtc = r.SourceInsertedUtc,
        MetadataHash = r.MetadataHash,
        PayloadHash = r.PayloadHash,
        PayloadModifiedUtc = r.PayloadModifiedUtc,
        TargetId = r.TargetId,
        ClaimedTargetId = r.ClaimedTargetId,
        TargetIdAssignedUtc = r.TargetIdAssignedUtc is { } assigned ? DateTime.SpecifyKind(assigned, DateTimeKind.Utc) : null,
        OccupiedTargetId = r.OccupiedTargetId,
        OccupiedTargetVersion = r.OccupiedTargetVersion,
        AdoptRequestedUtc = r.AdoptRequestedUtc is { } asked ? DateTime.SpecifyKind(asked, DateTimeKind.Utc) : null,
        RestrictionId = r.RestrictionId,
        RestrictedUtc = r.RestrictedUtc is { } restricted ? DateTime.SpecifyKind(restricted, DateTimeKind.Utc) : null,
        RestrictedFromAcl = r.RestrictedFromAcl,
        IncompleteRestrictionId = r.IncompleteRestrictionId,
        IncompleteSinceUtc = r.IncompleteSinceUtc is { } incomplete ? DateTime.SpecifyKind(incomplete, DateTimeKind.Utc) : null,
        IncompleteMissing = r.IncompleteMissing,
        IncompleteHash = r.IncompleteHash,
        TargetVersion = r.TargetVersion,
        Status = StatusText.ToRecordStatus(r.Status),
        LastDeliveredUtc = r.LastDeliveredUtc,
        LastVerifiedUtc = r.LastVerifiedUtc,
        LastVerifyOutcome = StatusText.ToVerifyOutcome(r.LastVerifyOutcome),
        LeaseOwner = r.LeaseOwner,
        LastSubmissionId = r.LastSubmissionId,
        AttemptCount = r.AttemptCount,
        NextAttemptUtc = r.NextAttemptUtc,
        LastError = r.LastError,
        PendingDocumentRef = r.PendingDocumentRef,
        WorkBatch = r.WorkBatch,
        TargetStateJson = r.TargetStateJson,
        PendingStepJson = r.PendingStepJson,
        PendingRenderContext = r.PendingRenderContext,
        PendingSourceFingerprint = r.PendingSourceFingerprint,
        PendingSourceModifiedUtc = r.PendingSourceModifiedUtc,
        PendingSourceFileName = r.PendingSourceFileName,
        PendingSourceRowNumber = r.PendingSourceRowNumber,
        PendingSourceUpdatedUtc = r.PendingSourceUpdatedUtc,
        PendingMetadataHash = r.PendingMetadataHash,
        PendingPayloadHash = r.PendingPayloadHash,
        PendingPayloadModifiedUtc = r.PendingPayloadModifiedUtc,
        PendingPayloadLocation = r.PendingPayloadLocation,
        PendingMetadata = r.PendingMetadata,
        PendingPayload = r.PendingPayload,
        PendingReferences = RecordReferences.Decode(r.PendingReferences),
        WaitingFor = r.WaitingFor,
        Blocked = r.Blocked,
        ProblemHash = r.ProblemHash,
        ValidationOutcome = r.ValidationOutcome,
        ValidationProblems = r.ValidationProblems,
        ValidatedUtc = r.ValidatedUtc,
        AssertionFailures = r.AssertionFailures,
        AcceptedMetadataHash = r.AcceptedMetadataHash,
        CacheSetId = r.CacheSetId,
        ParentId = r.ParentId,
        ParentVersion = r.ParentVersion,
        PendingParentVersion = r.PendingParentVersion,
        ChildLinkKey = r.ChildLinkKey,
        HeldSteps = r.HeldSteps,
        HeldRulesHash = r.HeldRulesHash,
        PlanRequestedUtc = r.PlanRequestedUtc,
        CreatedUtc = r.CreatedUtc,
        UpdatedUtc = r.UpdatedUtc,
    };
}

/// <summary>The short status strings the ledger tables store, and their enum forms.</summary>
internal static class StatusText
{
    public static string Of(RecordStatus status) => status switch
    {
        RecordStatus.Pending => "pending",
        RecordStatus.Delivering => "delivering",
        RecordStatus.Delivered => "delivered",
        RecordStatus.Held => "held",
        RecordStatus.Failed => "failed",
        RecordStatus.Deleted => "deleted",
        RecordStatus.Reverted => "reverted",
        RecordStatus.Waiting => "waiting",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static RecordStatus ToRecordStatus(string text) => text switch
    {
        "pending" => RecordStatus.Pending,
        "delivering" => RecordStatus.Delivering,
        "delivered" => RecordStatus.Delivered,
        "held" => RecordStatus.Held,
        "failed" => RecordStatus.Failed,
        "deleted" => RecordStatus.Deleted,
        "reverted" => RecordStatus.Reverted,
        "waiting" => RecordStatus.Waiting,
        _ => throw new DeliveryException($"Unknown record status '{text}' in the ledger."),
    };

    public static string Of(SubmissionStatus status) => status.ToString().ToLowerInvariant();

    public static SubmissionStatus ToSubmissionStatus(string text)
        => Enum.TryParse<SubmissionStatus>(text, ignoreCase: true, out var s) ? s : throw new DeliveryException($"Unknown submission status '{text}' in the ledger.");

    public static string Of(AttemptOutcome outcome) => outcome.ToString().ToLowerInvariant();

    public static AttemptOutcome ToAttemptOutcome(string text)
        => Enum.TryParse<AttemptOutcome>(text, ignoreCase: true, out var o) ? o : throw new DeliveryException($"Unknown attempt outcome '{text}' in the ledger.");

    public static string Of(VerifyOutcome outcome) => outcome.ToString().ToLowerInvariant();

    public static VerifyOutcome? ToVerifyOutcome(string? text)
        => text is null ? null : Enum.TryParse<VerifyOutcome>(text, ignoreCase: true, out var o) ? o : null;

    public static string Of(WorkBatchStatus status) => status.ToString().ToLowerInvariant();

    public static WorkBatchStatus ToWorkBatchStatus(string text)
        => Enum.TryParse<WorkBatchStatus>(text, ignoreCase: true, out var s) ? s : throw new DeliveryException($"Unknown work batch status '{text}' in the ledger.");
}
