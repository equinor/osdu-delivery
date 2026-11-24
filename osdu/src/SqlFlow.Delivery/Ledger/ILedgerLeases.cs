using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>How a try ended for one claimed record: what the worker appends under its lease, applied to the record later.</summary>
public sealed record RecordCompletion
{
    public required DeliveryKey DeliveryKey { get; init; }

    public required RecordStatus Status { get; init; }

    public required AttemptRecord Attempt { get; init; }

    /// <summary>When delivered: promote the pending document, hashes, context and origin to current.</summary>
    public bool Promote { get; init; }

    /// <summary>
    /// Nothing reached the target. A promotion settles work the final hash check found OSDU already holding: the pending
    /// state becomes current, and the delivery and verification times stay as they were. Any other completion hands the try
    /// back uncharged (the claim counted it against the retry budget): newer work waiting for an earlier unit's undo.
    /// </summary>
    public bool NothingSent { get; init; }

    /// <summary>
    /// The pending document could not be read (its work batch was pruned, truncated or rewritten): the record drops it and
    /// is asked to be planned again from its row, not blocked, since nothing about its data is wrong and a release would only
    /// send the missing document again.
    /// </summary>
    public bool Replan { get; init; }

    public long? TargetVersion { get; init; }

    public string? TargetId { get; init; }

    /// <summary>
    /// For a promotion: the paths of the document the try sent (<see cref="Planning.RenderPaths.EncodeText"/>), which the record
    /// keeps as those of the document OSDU holds; null for a try that sent no document, or whose work carried none.
    /// </summary>
    public string? RenderPaths { get; init; }

    /// <summary>For a promotion: the estate commit the work's render context was planned at, which the record keeps with it.</summary>
    public string? RenderCommit { get; init; }

    public DateTime? NextAttemptUtc { get; init; }

    public string? Error { get; init; }

    /// <summary>The target state to merge into the record (the returned identifiers), when the try changed it.</summary>
    public string? TargetStateJson { get; init; }

    /// <summary>The step progress to keep on the record for the next try (null clears it).</summary>
    public string? PendingStepJson { get; init; }

    /// <summary>
    /// The record's try count as the claim of this try left it. A completion that reaches the record after its lease ran
    /// out and the record was handed back is applied only while the count is still this one, so a later claim, release,
    /// removal or delivery is never overwritten by an older try. Null applies it whatever the count.
    /// </summary>
    public int? ClaimedAttemptCount { get; init; }

    /// <summary>
    /// The pending work this try carried out, as it stood when the record was claimed. A newer version of the record
    /// can be queued while the try is in flight; the completion then promotes what it actually delivered, leaves the
    /// newer work pending for the next pass, and keeps its step progress out of the newer work. Null for a
    /// completion that carried no document.
    /// </summary>
    public ClaimedWork? Claimed { get; init; }

    /// <summary>What the check of the try's document came to, which the record keeps; null for a try that checked none.</summary>
    public RecordValidation? Validation { get; init; }

    /// <summary>
    /// The unit of work the try belonged to, when it began one (osdu/docs/reference/concepts/artifacts-and-undo.md, How the
    /// states move): a delivered completion makes the ids the unit minted live and deletes what stood only for its
    /// progress; a held or failed one makes its artifacts due.
    /// </summary>
    public Guid? UnitId { get; init; }

    /// <summary>The OSDU ids an earlier delivery minted that this one made obsolete: the ledger marks them superseded.</summary>
    public IReadOnlyList<string> Superseded { get; init; } = [];

    /// <summary>
    /// For a delivered try that sent the record's document: the access the document went out under
    /// (osdu/docs/reference/flow/mapping-restricted-access.md), which the record keeps as the access OSDU holds it under, restricted
    /// until complete or its own. Null for any other completion (a try that sent a payload alone, found OSDU holding the work
    /// already, was retried, held or failed), which leaves the record's access as it was: OSDU's changes only when a document
    /// is sent.
    /// </summary>
    public DocumentAccess? Access { get; init; }
}

/// <summary>What a check of a record's document came to, as the record keeps it: the outcome, the problems, and when.</summary>
/// <param name="Outcome">What the check against the schema came to (<see cref="Validation.ValidationOutcomes"/>).</param>
/// <param name="Problems">How many problems it found.</param>
/// <param name="CheckedUtc">When it was made.</param>
/// <param name="AssertionFailures">
/// How many judgements of the mapping's assertions failed on the document (osdu/docs/reference/flow/mapping-assertions.md),
/// or null when its mapping states none.
/// </param>
public sealed record RecordValidation(string Outcome, long Problems, DateTime CheckedUtc, long? AssertionFailures = null);

/// <summary>
/// What a claimed record's pending work was: the submission and document reference that identify it (a reference is
/// only unique within its submission's work batches), and the values a delivery of it establishes.
/// </summary>
public sealed record ClaimedWork(
    Guid? SubmissionId,
    string DocumentRef,
    string? RenderContext,
    string? SourceFingerprint,
    DateTime? SourceModifiedUtc,
    string? MetadataHash,
    string? PayloadHash,
    DateTime? PayloadModifiedUtc,
    bool Metadata,
    bool Payload,
    RecordOrigin Origin = default,
    long? ParentVersion = null)
{
    /// <summary>The claimed work of a record, or null when it holds no pending document.</summary>
    public static ClaimedWork? Of(RecordState record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.PendingDocumentRef is not { } reference
            ? null
            : new ClaimedWork(
                record.LastSubmissionId, reference, record.PendingRenderContext, record.PendingSourceFingerprint, record.PendingSourceModifiedUtc,
                record.PendingMetadataHash, record.PendingPayloadHash, record.PendingPayloadModifiedUtc, record.PendingMetadata, record.PendingPayload,
                record.PendingOrigin, record.PendingParentVersion);
    }
}

/// <summary>
/// A worker's hold on work (osdu/docs/reference/concepts/submissions.md, Leases and claims): a work batch, or a group of
/// records due for a retry. It is one row however many records it holds, and the worker renews only that row. The records
/// carry its token.
/// </summary>
public sealed record LeaseState
{
    public required string Token { get; init; }

    public required Guid FlowId { get; init; }

    /// <summary>The submission the claim was made for: the batch's, or the one a retry claim named.</summary>
    public Guid? SubmissionId { get; init; }

    /// <summary>The work batch the lease drains, or null for records due for a retry.</summary>
    public int? WorkBatch { get; init; }

    /// <summary>Who holds the lease: the worker that claimed it, or the one recovering it.</summary>
    public required string Owner { get; init; }

    public Guid? RunId { get; init; }

    public DateTime AcquiredUtc { get; init; }

    public DateTime ExpiresUtc { get; init; }
}

/// <summary>
/// A claimed batch, the lease it is drained under, and the pending records the lease holds; beside them, the records of
/// the batch the claim found waiting for a record they refer to.
/// </summary>
public sealed record ClaimedWorkBatch(WorkBatchState Batch, LeaseState Lease, IReadOnlyList<RecordState> Records)
{
    public IReadOnlyList<WaitingRecord> Waiting { get; init; } = [];
}

/// <summary>
/// Records due for a retry, claimed together under one lease; no lease when nothing was due. Beside them, the due records
/// the claim found waiting for a record they refer to, which it did not take.
/// </summary>
public sealed record ClaimedRecords(LeaseState? Lease, IReadOnlyList<RecordState> Records)
{
    public static ClaimedRecords None { get; } = new(null, []);

    public IReadOnlyList<WaitingRecord> Waiting { get; init; } = [];
}

/// <summary>
/// The records a removal holds while it takes them out of OSDU (<see cref="ILedgerLeases.HoldForRemovalAsync"/>), under the
/// lease <paramref name="Token"/> names; no token when it holds nothing (no record named, or a ledger no run registered).
/// </summary>
public sealed record RemovalHold(string? Token, IReadOnlySet<DeliveryKey> Held)
{
    public static RemovalHold None { get; } = new(null, new HashSet<DeliveryKey>());
}

/// <summary>
/// A step of a delivery that completed against the target, with every step of the pending work completed so far: what the
/// record's next try resumes after. It belongs to the pending work the record was claimed with (the submission and the
/// document reference), and never reaches newer work queued behind the try.
/// </summary>
public sealed record RecordStep(DeliveryKey DeliveryKey, Guid? SubmissionId, string DocumentRef, string StepJson, DateTime AtUtc)
{
    /// <summary>The unit of work the step belongs to; null for a step reported outside one.</summary>
    public DeliveryUnit? Unit { get; init; }

    /// <summary>What the step created in OSDU, or an intent of what its call will create, written with the step.</summary>
    public IReadOnlyList<TargetArtifact> Artifacts { get; init; } = [];

    /// <summary>The platform run the step happened in.</summary>
    public Guid? RunId { get; init; }
}

/// <summary>What a worker appends under its lease in one write: steps that completed, and tries that ended.</summary>
public sealed record LeaseAppend(IReadOnlyList<RecordStep> Steps, IReadOnlyList<RecordCompletion> Completions)
{
    /// <summary>The undos of aborted units the worker ran under the lease, each written with its attempt after the completions.</summary>
    public IReadOnlyList<RecordUndo> Undos { get; init; } = [];
}

/// <summary>How a lease ends, which decides what happens to the work it did not reach.</summary>
public enum LeaseEnd
{
    /// <summary>The worker went through all of it: the batch is done.</summary>
    Done,

    /// <summary>The worker could not go on (the batch file could not be read): the batch failed.</summary>
    Failed,

    /// <summary>The worker is stopping: the batch is queued again, and the tries it did not finish are not charged.</summary>
    Stopped,

    /// <summary>The lease ran out with nobody renewing it: the batch is queued again, and the interrupted tries count.</summary>
    Expired,
}

/// <summary>How a worker closes its lease, with the batch's counts when it drained one.</summary>
public sealed record LeaseClosing
{
    public required LeaseEnd End { get; init; }

    public long Delivered { get; init; }

    public long Held { get; init; }

    public long Failed { get; init; }

    public long Retrying { get; init; }

    /// <summary>Why the batch failed, redacted.</summary>
    public string? Failure { get; init; }
}

/// <summary>What applying a lease did: the records its appended events settled, and the records it handed back.</summary>
public sealed record LeaseApplied(int Applied, int Released)
{
    public static LeaseApplied None { get; } = new(0, 0);

    public LeaseApplied Add(LeaseApplied other) => new(Applied + other.Applied, Released + other.Released);
}

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds the worker's leases: claiming records and batches,
/// appending what it did, and closing or recovering a lease. A caller that uses only this part takes it rather than the
/// whole ledger; <see cref="OsduLedger"/> implements every part.
/// </summary>
public interface ILedgerLeases
{
    /// <summary>
    /// Claims up to <paramref name="max"/> of the flow's (or submission's) pending records that are due, under one new
    /// lease for <paramref name="owner"/>, after recovering the flow's leases that ran out. A due record that refers to a
    /// record another record of the ledger holds and has not delivered is not claimed but left waiting, as
    /// <paramref name="waits"/> says (every such record when null). The records come back holding the lease;
    /// <see cref="ClaimedRecords.None"/> when nothing was due. The lease keeps <paramref name="attempts"/>, the tries the
    /// flow gives a record, so whoever recovers it after it ran out fails a record that used them up (0 for none).
    /// </summary>
    /// <remarks>
    /// With <paramref name="batches"/> only records queued in those work batches are claimed: a part of a split delivery
    /// claims its own (osdu/docs/reference/concepts/submissions.md, Many nodes).
    /// </remarks>
    Task<ClaimedRecords> ClaimAsync(
        Guid flowId, Guid? submissionId, string owner, int max, TimeSpan lease, DateTime nowUtc, Guid? runId = null, WaitRules? waits = null, int attempts = 0,
        BatchRange? batches = null, CancellationToken ct = default);

    /// <summary>
    /// Holds the records of <paramref name="keys"/> no lease holds under one new lease for <paramref name="owner"/>, a
    /// removal's: no worker claims a record it holds, and a plan that queues new work for one keeps it held, so nothing is
    /// sent to OSDU while the removal takes the record out. Nothing about a record changes but its holder. The removal
    /// renews the lease as a worker does and closes it when done (<see cref="CloseLeaseAsync"/>); closed or run out, the
    /// lease lets its records go as they are, with no try charged and no attempt written. A record already held (a worker
    /// is sending it) is not held, and is not among <see cref="RemovalHold.Held"/>.
    /// </summary>
    Task<RemovalHold> HoldForRemovalAsync(
        Guid flowId, IReadOnlyCollection<DeliveryKey> keys, string owner, TimeSpan lease, DateTime nowUtc, Guid? runId = null, CancellationToken ct = default);

    /// <summary>
    /// Extends a lease its worker still holds: one row, however many records it holds. False when the lease is gone or
    /// another worker took it over after it ran out, and the worker must stop sending.
    /// </summary>
    Task<bool> RenewLeaseAsync(string token, TimeSpan lease, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Appends, in one write, the steps that completed and the tries that ended under a lease: each try's attempt, and
    /// the events the lease later applies to the records. A step is written before the delivery that reported it goes on, so
    /// a crash never repeats it. The artifacts a step reports are written in the same transaction
    /// (osdu/docs/reference/concepts/artifacts-and-undo.md, What an artifact is), one row per slot of a unit, so the ledger
    /// names what a delivery created before it goes on; a completion settles its unit's artifacts (live and the progress
    /// rows deleted when it delivered, due when it held or failed, the ids it made obsolete superseded); an undo writes its
    /// attempt and what it settled each artifact as.
    /// </summary>
    Task AppendAsync(Guid flowId, string token, LeaseAppend append, CancellationToken ct = default);

    /// <summary>
    /// Applies what the lease's worker has appended so far to its records, a slice at a time, and deletes each event with
    /// its application: a try's outcome settles the record and hands it back from the lease; a step is kept for the next
    /// try while the record still holds the work it belongs to. A record another lease holds now is left to that lease.
    /// </summary>
    Task<LeaseApplied> CheckpointLeaseAsync(string token, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Ends a lease: applies what its worker appended, hands the records it did not settle back to pending (as
    /// <paramref name="closing"/> says), settles its batch, and deletes it. A lease another worker recovered meanwhile
    /// still has its appended events applied, and nothing else. A removal's lease lets the records it still holds go as
    /// they are, however it ends.
    /// </summary>
    Task<LeaseApplied> CloseLeaseAsync(string token, LeaseClosing closing, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Recovers the flow's leases that ran out before <paramref name="nowUtc"/>: each is taken over by this caller, so two
    /// never recover the same one, and closed as <see cref="LeaseEnd.Expired"/>. Returns the records settled or handed back.
    /// </summary>
    Task<int> RecoverExpiredLeasesAsync(Guid flowId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// When the earliest lease of the flow runs out, or of the submission (its batches, and any lease holding one of its
    /// records), or null when there is none. Expired leases are included, so a lease a stopped worker left behind stays
    /// visible until it is recovered.
    /// </summary>
    Task<DateTime?> NextLeaseExpiryAsync(Guid flowId, Guid? submissionId, CancellationToken ct = default);

    /// <summary>
    /// Claims the oldest queued work batch of the flow, or of one submission, under a new lease for
    /// <paramref name="owner"/>, after recovering the flow's leases that ran out, and has the lease hold the batch's
    /// pending records that are due, except those left waiting as <paramref name="waits"/> says (see
    /// <see cref="ClaimAsync"/>). Null when nothing is claimable. The lease keeps <paramref name="attempts"/> as
    /// <see cref="ClaimAsync"/>'s does. With <paramref name="batches"/> only a batch in that range is claimed.
    /// </summary>
    Task<ClaimedWorkBatch?> ClaimWorkBatchAsync(
        Guid flowId, Guid? submissionId, string owner, TimeSpan lease, DateTime nowUtc, Guid? runId = null, WaitRules? waits = null, int attempts = 0,
        BatchRange? batches = null, CancellationToken ct = default);
}
