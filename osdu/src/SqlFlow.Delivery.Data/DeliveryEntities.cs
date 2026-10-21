using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace SqlFlow.Delivery.Data;

// The delivery ledger (osdu/docs/reference/concepts/ledger.md), in the osdu schema: submissions, records and
// append-only attempts, the audit trail of interventions, the source watermarks, and the read model of the mapping and
// cache documents the sync found in the repositories. Statuses are stored as short strings so the tables read without a
// decoder ring. Everything the GUI lists is index-backed. No foreign key or navigation reaches SQLFlow's catalog: a
// run, a group or a repository is referenced by id only.

/// <summary>
/// One plan of a flow over its ingestion tables: an incremental window, a full read, or a set of record keys. Its id is
/// the idempotency key, and every record it planned points back at it.
/// </summary>
public sealed class DeliverySubmission
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid SubmissionId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string MappingReference { get; set; } = string.Empty;

    public string RenderContext { get; set; } = string.Empty;

    public string ParametersJson { get; set; } = "{}";

    public long RecordCount { get; set; }

    /// <summary>The work location the intake wrote its batches under.</summary>
    public string? WorkLocation { get; set; }

    /// <summary>How many work batches the intake wrote.</summary>
    public int BatchCount { get; set; }

    /// <summary>How many key slices the intake was cut into for its fan-out; 1 for a plan that ran on one node.</summary>
    public int Slices { get; set; }

    public string Status { get; set; } = "received";

    public DateTime ReceivedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public long Planned { get; set; }

    public long SkippedUnchanged { get; set; }

    /// <summary>Records a cache change waiting for approval held back; rendered and ready, not sent until it is decided.</summary>
    public long AwaitingApproval { get; set; }

    /// <summary>Records the source carried in a version older than the one delivered or queued; skipped, never sent.</summary>
    public long SkippedStale { get; set; }

    /// <summary>Records whose queued document was, when its turn came, what OSDU already held; nothing was sent.</summary>
    public long UnchangedAtPush { get; set; }

    public long Blocked { get; set; }

    public long Delivered { get; set; }

    public long Held { get; set; }

    public long Failed { get; set; }

    /// <summary>Records of the submission still waiting, when it closed, for a record they refer to that has not landed.</summary>
    public long Waiting { get; set; }

    /// <summary>Records without a derivable delivery key: not planned, not delivered.</summary>
    public long Untracked { get; set; }

    public string? Error { get; set; }

    /// <summary>incremental (a window of changed rows), full (every row up to a bound) or keys (named record keys).</summary>
    public string Kind { get; set; } = "incremental";

    /// <summary>The source connection reference as the flow declares it; never a resolved value.</summary>
    public string SourceConnection { get; set; } = string.Empty;

    /// <summary>The record table's three-part name.</summary>
    public string SourceObject { get; set; } = string.Empty;

    /// <summary>The lower bound (exclusive) of the change window planned; null for a plan without a window.</summary>
    public DateTime? WindowFromUtc { get; set; }

    /// <summary>The upper bound (inclusive) of the change window planned; set for incremental and full plans.</summary>
    public DateTime? WindowToUtc { get; set; }

    /// <summary>What else bounded the read, as JSON: dataset objects, overlap, scope values, slice boundaries, key count
    /// and, for a keys plan, the digest of the keys.</summary>
    public string? SourceWindowJson { get; set; }

    /// <summary>The platform run that coordinated the plan.</summary>
    public Guid? RunId { get; set; }
}

/// <summary>
/// The current state of one deliverable, keyed by the flow that delivers it and its deterministic delivery key. The same
/// source row read by two flows is two records, each with its own state and history.
/// </summary>
public sealed class DeliveryRecord
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    public string SourceKey { get; set; } = string.Empty;

    /// <summary>The record's key tuple as a JSON array of strings, in the flow's key order: what a key-scoped read uses.</summary>
    public string? SourceKeyJson { get; set; }

    public string? Label { get; set; }

    public string MappingName { get; set; } = string.Empty;

    public string? RenderContext { get; set; }

    /// <summary>The ingestion fingerprint of the rows OSDU's document was built from.</summary>
    public string? SourceFingerprint { get; set; }

    /// <summary>When the source row OSDU's document was built from last changed (the flow's source.lastModified).</summary>
    public DateTime? SourceModifiedUtc { get; set; }

    /// <summary>The ingestion file the version OSDU holds came from.</summary>
    public string? SourceFileName { get; set; }

    /// <summary>The row of that file.</summary>
    public long? SourceRowNumber { get; set; }

    /// <summary>When the ingestion table last updated the row the version OSDU holds came from.</summary>
    public DateTime? SourceUpdatedUtc { get; set; }

    /// <summary>
    /// When the ingestion table first inserted the record's row, as the last plan that read it saw it: the row's arrival,
    /// which later changes never move. Null when the table does not carry it.
    /// </summary>
    public DateTime? SourceInsertedUtc { get; set; }

    public string? MetadataHash { get; set; }

    public string? PayloadHash { get; set; }

    /// <summary>The newest modified time among the chunk files OSDU's payload was delivered from.</summary>
    public DateTime? PayloadModifiedUtc { get; set; }

    public string? TargetId { get; set; }

    /// <summary>
    /// The OSDU id this record claimed for its flow when it first queued a document, kept for good afterwards. One OSDU
    /// record belongs to one flow: no other flow's record can claim the same id. A record that was only ever held has
    /// claimed nothing.
    /// </summary>
    public string? ClaimedTargetId { get; set; }

    /// <summary>
    /// When an operator gave the record a new OSDU id because OSDU lost the one it claimed (an inventory's repair): from then
    /// on the record is written under <see cref="ClaimedTargetId"/> rather than the id its mapping makes from its key. Null
    /// for a record whose id is the one its mapping makes.
    /// </summary>
    public DateTime? TargetIdAssignedUtc { get; set; }

    public long? TargetVersion { get; set; }

    public string Status { get; set; } = "pending";

    public DateTime? LastDeliveredUtc { get; set; }

    public DateTime? LastVerifiedUtc { get; set; }

    public string? LastVerifyOutcome { get; set; }

    /// <summary>
    /// The token of the lease the record is being delivered under (<see cref="DeliveryLease"/>), set once when a worker
    /// claims it and cleared when the lease applies its outcome or hands it back. The lease holds the expiry.
    /// </summary>
    public string? LeaseOwner { get; set; }

    public Guid? LastSubmissionId { get; set; }

    public int AttemptCount { get; set; }

    public DateTime? NextAttemptUtc { get; set; }

    public string? LastError { get; set; }

    /// <summary>Where the pending document sits in the submission's work batches (batch:offset:length).</summary>
    public string? PendingDocumentRef { get; set; }

    /// <summary>The work batch the pending document was written in.</summary>
    public int? WorkBatch { get; set; }

    /// <summary>The identifiers the target returned for what it holds now (a JSON object merged step by step).</summary>
    public string? TargetStateJson { get; set; }

    /// <summary>The completed steps of the pending delivery and what they returned (a JSON object keyed by step).</summary>
    public string? PendingStepJson { get; set; }

    /// <summary>
    /// The cache values this record was built from, as the id of the set it shares with every other record that
    /// read the same values (<see cref="DeliveryCacheSet"/>). One column rather than a row per dependency: at
    /// estate scale the records number in the hundreds of millions and the distinct sets in the thousands.
    /// </summary>
    public long? CacheSetId { get; set; }

    /// <summary>
    /// While the record is held, what its held render read out of the cache, the ids it looked for and did not find among
    /// them, as a set id like <see cref="CacheSetId"/>: a cache version that brings in one of those ids releases the record
    /// to be planned again (osdu/docs/reference/concepts/partition-cache.md, A record held for a reference waits for it).
    /// Kept apart from <see cref="CacheSetId"/>, which says what the document OSDU holds was built from. A record that is no
    /// longer held is not read by it.
    /// </summary>
    public long? HeldCacheSetId { get; set; }

    public string? PendingRenderContext { get; set; }

    public string? PendingSourceFingerprint { get; set; }

    public DateTime? PendingSourceModifiedUtc { get; set; }

    /// <summary>The ingestion file the queued version came from.</summary>
    public string? PendingSourceFileName { get; set; }

    /// <summary>The row of that file.</summary>
    public long? PendingSourceRowNumber { get; set; }

    /// <summary>When the ingestion table last updated the row the queued version came from.</summary>
    public DateTime? PendingSourceUpdatedUtc { get; set; }

    public string? PendingMetadataHash { get; set; }

    public string? PendingPayloadHash { get; set; }

    public DateTime? PendingPayloadModifiedUtc { get; set; }

    public string? PendingPayloadLocation { get; set; }

    public bool PendingMetadata { get; set; }

    public bool PendingPayload { get; set; }

    /// <summary>
    /// The OSDU ids the pending document refers to through the properties its template declares relationships for, each
    /// with the property that holds it, as a JSON array (osdu/docs/reference/flow/interfaces.md, Records that wait for
    /// records). Written with the pending document and cleared with it, so only the work still to be sent keeps them.
    /// </summary>
    public string? PendingReferences { get; set; }

    public bool Blocked { get; set; }

    /// <summary>
    /// While the record is blocked, held or failed: the problem that keeps it so, as the hash of its last error with every
    /// part that names the record replaced (a value, an id, a moment, a number), which every record refused for the same
    /// reason shares. Null for any other record, so the index that groups blocked records by problem holds them alone.
    /// </summary>
    public long? ProblemHash { get; set; }

    /// <summary>
    /// What the last check of the record's document against its schema came to, when the gate before a record is sent
    /// checked it: <c>valid</c>, <c>invalid</c> or <c>unverified</c>. Null until the gate has checked a document of the
    /// record. A flow's records are counted by it (osdu/docs/reference/concepts/preflight.md, Validation before a record is
    /// sent).
    /// </summary>
    public string? ValidationOutcome { get; set; }

    /// <summary>How many problems that check found.</summary>
    public long? ValidationProblems { get; set; }

    /// <summary>When that check was made.</summary>
    public DateTime? ValidatedUtc { get; set; }

    /// <summary>
    /// How many judgements of the mapping's assertions failed on the document that check read (held, reported or left out;
    /// osdu/docs/reference/flow/mapping-assertions.md). Null when its mapping states none, or no check has reached the
    /// record. A flow's records failing an assertion are counted and listed by it.
    /// </summary>
    public long? AssertionFailures { get; set; }

    /// <summary>
    /// The metadata hash of the pending document an operator's release accepted as it is: the gate sends that document
    /// whatever its verdict says. A document rendered differently has another hash and is judged again.
    /// </summary>
    public string? AcceptedMetadataHash { get; set; }

    /// <summary>
    /// While the record is waiting: the OSDU id of the record it waits for, which another record of the ledger holds and
    /// has not delivered. The record goes back to pending when that one lands.
    /// </summary>
    public string? WaitingFor { get; set; }

    /// <summary>When the ledger asked for the record to be planned again (a redeliver, a release, a cache rollout); null
    /// once a plan took it.</summary>
    public DateTime? PlanRequestedUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>One delivery try, append-only: the record's history.</summary>
public sealed class DeliveryAttempt
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long AttemptId { get; set; }

    /// <summary>The flow of the record the try belongs to; with the delivery key, the record's identity.</summary>
    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    public Guid? SubmissionId { get; set; }

    /// <summary>The platform run the attempt happened in, when it did (operator actions carry none).</summary>
    public Guid? RunId { get; set; }

    public string Worker { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime CompletedUtc { get; set; }

    public string Outcome { get; set; } = string.Empty;

    public string Phase { get; set; } = string.Empty;

    public string? MetadataHash { get; set; }

    public string? PayloadHash { get; set; }

    public long? TargetVersion { get; set; }

    public string? Error { get; set; }

    /// <summary>The steps of the try and what the target answered, as JSON.</summary>
    public string? ResultJson { get; set; }

    /// <summary>The work batch the try belonged to, when it ran from one.</summary>
    public int? WorkBatch { get; set; }

    /// <summary>The ingestion file the attempt's document was built from; kept here because the record's own origin
    /// columns move on with later versions.</summary>
    public string? SourceFileName { get; set; }

    /// <summary>The row of that file.</summary>
    public long? SourceRowNumber { get; set; }

    /// <summary>When the ingestion table last updated that row.</summary>
    public DateTime? SourceUpdatedUtc { get; set; }

    /// <summary>When the ingestion table marked that row deleted, for the hold of a deleted row; null otherwise.</summary>
    public DateTime? SourceDeletedUtc { get; set; }
}

/// <summary>One work batch of a submission: a file of rendered documents, claimed and drained as one unit.</summary>
public sealed class DeliveryWorkBatch
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid SubmissionId { get; set; }

    public int Index { get; set; }

    public Guid FlowId { get; set; }

    public string Location { get; set; } = string.Empty;

    public int RecordCount { get; set; }

    /// <summary>queued, running, done, failed.</summary>
    public string Status { get; set; } = "queued";

    /// <summary>The token of the lease a worker drains the batch under (<see cref="DeliveryLease"/>), while it runs.</summary>
    public string? LeaseOwner { get; set; }

    public Guid? RunId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public long Delivered { get; set; }

    public long Held { get; set; }

    public long Failed { get; set; }

    public long Retrying { get; set; }

    /// <summary>Records of the batch its claim found waiting for a record they refer to, and did not send.</summary>
    public long Waiting { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// A worker's hold on work: one row per claim, a work batch or a group of records due for a retry. The worker renews
/// this one row while it delivers, however many records the claim holds, and the records carry its token. A lease that
/// ran out belongs to nobody alive, and the next claim of its flow recovers it: it applies what the worker appended and
/// hands the rest of the work back.
/// </summary>
public sealed class DeliveryLease
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    /// <summary>The claim's token: the worker's name and a fresh id.</summary>
    public string Token { get; set; } = string.Empty;

    public Guid FlowId { get; set; }

    /// <summary>The submission the claim was made for: the batch's, or the one a retry claim named.</summary>
    public Guid? SubmissionId { get; set; }

    /// <summary>The work batch the lease drains, or null for a group of records due for a retry.</summary>
    public int? WorkBatch { get; set; }

    /// <summary>Who holds the lease: the worker that claimed it, or the one recovering it.</summary>
    public string Owner { get; set; } = string.Empty;

    public Guid? RunId { get; set; }

    public DateTime AcquiredUtc { get; set; }

    public DateTime ExpiresUtc { get; set; }

    /// <summary>
    /// The tries the claiming worker's flow gives a record (<c>reliability.retry.attempts</c>), so whoever recovers the lease
    /// after it ran out fails a record that used them up with its worker stopping mid-attempt every time, rather than handing
    /// it back for ever; 0 for a lease claimed without one.
    /// </summary>
    public int MaxAttempts { get; set; }

    /// <summary>
    /// True for a lease a removal holds records under while it takes them out of OSDU, so no worker claims one of them
    /// meanwhile. Nothing is sent under it: when it ends or runs out, its records are let go as they are, with no try
    /// charged and no attempt written.
    /// </summary>
    public bool ForRemoval { get; set; }
}

/// <summary>
/// One value a record is findable by: a wellbore id, a well name, a key value, the OSDU id or its trailing part, a word
/// of the label, or the ingestion file it came from. The token is folded to upper case, so a lookup compares folded
/// terms and one index answers "starts with" for every identifier an operator may hold, across every flow. Rows belong
/// to their record: they are rewritten whenever its identity changes, and deleted with it.
/// </summary>
public sealed class DeliveryRecordIdentity
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The value folded to upper case: what a lookup seeks.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The value as it was read, for showing beside a hit.</summary>
    public string Display { get; set; } = string.Empty;

    /// <summary>Where the token came from: identity, key, label, osdu or file, as the ledger's identity kinds name them.</summary>
    public string Kind { get; set; } = string.Empty;
}

/// <summary>
/// What a worker appended while it delivered under a lease, not yet applied to the record: a step that completed, or the
/// outcome of a try. Rows are only ever added. The lease applies them to their records when the worker checkpoints or
/// closes it, or when the lease is recovered, and deletes them in the same transaction, so each is applied once. The
/// try's permanent history is its <see cref="DeliveryAttempt"/>, written with the outcome.
/// </summary>
public sealed class DeliveryRecordEvent
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long EventId { get; set; }

    public string LeaseToken { get; set; } = string.Empty;

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>step or completion.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>When the step completed or the try ended.</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>A step: every step of the pending work completed so far and what each returned (a JSON object keyed by step).</summary>
    public string? StepJson { get; set; }

    /// <summary>A completion: the status the try settles the record in.</summary>
    public string? Status { get; set; }

    public bool Promote { get; set; }

    public bool NothingSent { get; set; }

    /// <summary>A completion whose pending document could not be read: the record drops it and is asked to be planned again.</summary>
    public bool Replan { get; set; }

    public DateTime? NextAttemptUtc { get; set; }

    public string? Error { get; set; }

    /// <summary>A completion that holds or fails the record: the problem its error names, which the record keeps while it is blocked.</summary>
    public long? ProblemHash { get; set; }

    /// <summary>A completion of a try that checked its document: the outcome the record keeps; null leaves the record's as it was.</summary>
    public string? ValidationOutcome { get; set; }

    /// <summary>How many problems that check found.</summary>
    public long? ValidationProblems { get; set; }

    /// <summary>When that check was made.</summary>
    public DateTime? ValidatedUtc { get; set; }

    /// <summary>How many judgements of the mapping's assertions failed on the document that check read; null when its mapping states none.</summary>
    public long? AssertionFailures { get; set; }

    public string? TargetId { get; set; }

    public long? TargetVersion { get; set; }

    /// <summary>The target state the record holds after the try, when the try changed it.</summary>
    public string? TargetStateJson { get; set; }

    /// <summary>The step progress the record keeps for its next try; null clears it.</summary>
    public string? PendingStepJson { get; set; }

    // The pending work the try carried, as it stood when the record was claimed: a step belongs to it, and a completion
    // promotes it when newer work was queued behind the try. A null document reference means the try carried none.

    public Guid? ClaimSubmissionId { get; set; }

    public string? ClaimDocumentRef { get; set; }

    /// <summary>
    /// A completion: the record's try count as the claim of the try left it. A completion that reaches a record no lease
    /// holds (its own ran out and the record was handed back) is applied only while the count is still that one: a later
    /// claim, release, removal or delivery moved it, and what that did is newer than the try.
    /// </summary>
    public int? ClaimAttemptCount { get; set; }

    public string? ClaimRenderContext { get; set; }

    public string? ClaimSourceFingerprint { get; set; }

    public DateTime? ClaimSourceModifiedUtc { get; set; }

    public string? ClaimSourceFileName { get; set; }

    public long? ClaimSourceRowNumber { get; set; }

    public DateTime? ClaimSourceUpdatedUtc { get; set; }

    public string? ClaimMetadataHash { get; set; }

    public string? ClaimPayloadHash { get; set; }

    public DateTime? ClaimPayloadModifiedUtc { get; set; }

    public bool ClaimMetadata { get; set; }

    public bool ClaimPayload { get; set; }
}

/// <summary>
/// The watermark of one flow scope (the parameter set): the upper bound of the last whole-scope plan that completed, so
/// the next incremental plan reads the rows the ingestion tables changed after it.
/// </summary>
public sealed class DeliverySourceWatermark
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid FlowId { get; set; }

    public string Scope { get; set; } = string.Empty;

    /// <summary>The upper bound of the change window the last completed whole-scope plan covered.</summary>
    public DateTime UpdatedThroughUtc { get; set; }

    /// <summary>The submission whose completion wrote the watermark.</summary>
    public Guid SubmissionId { get; set; }

    /// <summary>
    /// The render context the last plan of this scope used. The whole-run gate compares it, so a cache, mapping or
    /// schema version that moved re-renders the scope even when no source row changed.
    /// </summary>
    public string? ContextHash { get; set; }

    public DateTime RecordedUtc { get; set; }
}

/// <summary>The audit trail of runs and interventions: who did what, when, with which inputs, and the outcome.</summary>
public sealed class DeliveryActivity
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ActivityId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string Outcome { get; set; } = "running";

    public string? ParametersJson { get; set; }

    public Guid? SubmissionId { get; set; }

    public Guid? DeliveryKey { get; set; }

    /// <summary>
    /// The platform run the activity ran in, when it ran in one: every operation of a run, an intervention a run's payload
    /// asked for, an inventory removal. Null for an intervention through the control plane, the GUI or the command line. A
    /// plan run records no activity.
    /// </summary>
    public Guid? RunId { get; set; }

    public string? Summary { get; set; }

    public string? Log { get; set; }

    /// <summary>
    /// A run that completed having changed nothing: it planned, held, blocked and sent no record, and left none waiting.
    /// The schedule fired and found nothing new. The audit trail leaves these out unless asked for them, and says how many
    /// it left out; an intervention is never idle.
    /// </summary>
    public bool Idle { get; set; }
}

/// <summary>
/// One record an intervention changed, written in the statement that changed it. An intervention made for one record names
/// it on its <see cref="DeliveryActivity"/>; one made for many (a release of a whole flow, or of every record a problem
/// keeps blocked) names each record here, so a record's history holds every request that reached it, with who asked and
/// when, however many records the request reached.
/// </summary>
public sealed class DeliveryActivityRecord
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The intervention, as the audit trail numbers it.</summary>
    public long ActivityId { get; set; }
}

/// <summary>
/// A record deleted from the ledger after it was removed from OSDU
/// (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting removed records from the ledger): its attempts,
/// search entries and row went, and this line is what the ledger keeps of it, so who deleted which record, with which
/// OSDU id and last version, is still answered from the ledger. Rows are only ever added, and the one thing that changes
/// on a line is its request to read the record's row again, answered by the run that reads it; the intervention that
/// deleted it names it under <see cref="DeliveryActivityRecord"/> as well.
/// </summary>
public sealed class DeliveryPurgedRecord
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long PurgedRecordId { get; set; }

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The record's key in its source, as the ledger held it.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>
    /// The record's key tuple as the ledger held it (a JSON array of strings, in the flow's key order): what the run that reads
    /// its row again reads it by. Null for a record that carried none.
    /// </summary>
    public string? SourceKeyJson { get; set; }

    /// <summary>
    /// When the deletion asked for the record's row to be read again (osdu/docs/reference/concepts/removal-and-reversal.md,
    /// Deleting removed records from the ledger): the next run of the flow reads it by <see cref="SourceKeyJson"/> and delivers
    /// it as a new record. Null once a run answered it (its row planned, or gone from the table), when a later deletion of the
    /// same record took it over, for a record a reversal keeps out, for one with no key to read it by, and for every line
    /// deleting the whole ledger wrote, since its next run reads every row.
    /// </summary>
    public DateTime? PlanRequestedUtc { get; set; }

    public string? Label { get; set; }

    /// <summary>The OSDU id the record was delivered and removed under; null for a record that never had one.</summary>
    public string? TargetId { get; set; }

    /// <summary>The last OSDU version an attempt of the record named, before it was removed.</summary>
    public long? LastVersion { get; set; }

    /// <summary>How many attempts were deleted with it.</summary>
    public int Attempts { get; set; }

    /// <summary>The intervention that deleted it, as the audit trail numbers it.</summary>
    public long? ActivityId { get; set; }

    public string PurgedBy { get; set; } = string.Empty;

    public DateTime PurgedUtc { get; set; }
}

/// <summary>
/// One thing a delivery created in OSDU, or set out to create (osdu/docs/reference/concepts/artifacts-and-undo.md, What an
/// artifact is): a dataset it registered, the record it wrote before a later call, a bulk session it opened, rows it
/// posted. Written in the transaction that writes the step that created it, so the ledger names it before the delivery goes
/// on; an intent is written before a call whose id the service chooses, with what finds the object when the answer is lost,
/// and completed with the id. A unit of work that commits makes the ids it minted live and deletes what stood only for its
/// progress; one that aborts is undone, and each artifact says how: removed, restored, gone, or kept with why.
/// </summary>
public sealed class DeliveryArtifact
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ArtifactId { get; set; }

    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The unit of work (one delivery of the record's pending work, across its tries) that created it.</summary>
    public Guid UnitId { get; set; }

    /// <summary>When that unit began: what an undo compares OSDU's creation time with before it removes a record.</summary>
    public DateTime UnitStartedUtc { get; set; }

    /// <summary>The route's name for it, unique within its unit: <c>record</c>, <c>dataset:0</c>, <c>content:Kr</c>, <c>session</c>.</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>What it is: record, version, dataset, content, output, dataspace, session, lock, rows, points, objects or run.</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>The OSDU id, or the key the target gave it; null for an intent whose call has not answered.</summary>
    public string? TargetId { get; set; }

    /// <summary>What finds it when its id is not known, or what else names it (a file's landing-zone path, a session's record).</summary>
    public string? Locator { get; set; }

    /// <summary>The version the unit wrote, when it wrote one.</summary>
    public long? Version { get; set; }

    /// <summary>The version the unit's write replaced, for a version of a record that existed: what an undo writes back.</summary>
    public long? PriorVersion { get; set; }

    /// <summary>intent, pending, live, superseded, due, removed, restored, gone, kept or failed.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Why it was kept, or why its undo failed, redacted.</summary>
    public string? Note { get; set; }

    /// <summary>How many undos of it were tried.</summary>
    public int UndoAttempts { get; set; }

    /// <summary>When an undo that failed is tried again; null when none is due.</summary>
    public DateTime? NextUndoUtc { get; set; }

    /// <summary>The submission whose work the unit delivered.</summary>
    public Guid? SubmissionId { get; set; }

    /// <summary>The platform run the artifact was created in.</summary>
    public Guid? CreatedRunId { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>When its unit's undo settled it (removed, restored, gone or kept); null while it is not settled by one.</summary>
    public DateTime? SettledUtc { get; set; }

    /// <summary>The platform run that settled it.</summary>
    public Guid? SettledRunId { get; set; }

    /// <summary>Who settled it: the worker, the sweep, or the operator whose removal undid it.</summary>
    public string? SettledBy { get; set; }
}

/// <summary>
/// One inventory of an inventory flow in a partition (osdu/docs/reference/flow/inventory.md, The tables an inventory
/// keeps): what it reads, the owners its last reconcile used and how it knew them, and its last build and reconcile. Its
/// counts by finding are read from its records.
/// </summary>
public sealed class DeliveryInventory
{
    public short PartitionId { get; set; }

    public int InventoryId { get; set; }

    /// <summary>The ledger identity of the inventory flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    /// <summary>The inventory's name in its flow.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The kind it reads, as the flow declares it (wildcards allowed).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The query that narrows a search read; null for every record of the kind.</summary>
    public string? Query { get; set; }

    /// <summary>search or storage.</summary>
    public string ReadMode { get; set; } = string.Empty;

    /// <summary>latest or all.</summary>
    public string Versions { get; set; } = string.Empty;

    /// <summary>The identities the last reconcile took as this estate's, each with how many records it created that a ledger claims (JSON).</summary>
    public string? OwnersJson { get; set; }

    /// <summary>declared (the flow names them), inferred (from the records a ledger claims), or none.</summary>
    public string? OwnersSource { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>The last build that completed, and when.</summary>
    public long? LastBuildRunId { get; set; }

    public DateTime? LastBuiltUtc { get; set; }

    /// <summary>The last reconcile that completed (a build's or one of its own), and when.</summary>
    public long? LastReconcileRunId { get; set; }

    public DateTime? LastReconciledUtc { get; set; }
}

/// <summary>
/// One build or reconcile of an inventory: the platform run and who asked, how it read OSDU, what it found and changed, and its
/// counts by finding as it left them.
/// </summary>
public sealed class DeliveryInventoryRun
{
    public short PartitionId { get; set; }

    public long InventoryRunId { get; set; }

    public int InventoryId { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>build or reconcile.</summary>
    public string Operation { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    /// <summary>running, completed or failed.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    /// <summary>search or storage.</summary>
    public string ReadMode { get; set; } = string.Empty;

    /// <summary>The ids the read listed, repeats left out.</summary>
    public long Listed { get; set; }

    /// <summary>The pages and requests the read took.</summary>
    public int Pages { get; set; }

    public long Requests { get; set; }

    /// <summary>What the merge changed: ids new to the inventory, ids whose version or headers moved, ids no longer served, and ids served again.</summary>
    public long Added { get; set; }

    public long Changed { get; set; }

    public long Gone { get; set; }

    public long Returned { get; set; }

    /// <summary>The ids a ledger expects that the reconcile read from storage to tell missing from merely unlisted.</summary>
    public long MissingChecked { get; set; }

    /// <summary>The counts by finding the reconcile left (JSON).</summary>
    public string? FindingsJson { get; set; }

    /// <summary>The owners the reconcile used, each with its count, and how it knew them (JSON).</summary>
    public string? OwnersJson { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One id an inventory holds, or a ledger of its partition expects: its kind, version and system properties as OSDU served
/// them, when it was first listed, last changed and found gone, and what the ledgers hold of it (the finding).
/// </summary>
public sealed class DeliveryInventoryRecord
{
    public short PartitionId { get; set; }

    public long InventoryRecordId { get; set; }

    public int InventoryId { get; set; }

    /// <summary>The OSDU id, compared exactly.</summary>
    public string TargetId { get; set; } = string.Empty;

    public string? Kind { get; set; }

    public long? Version { get; set; }

    public string? CreateUser { get; set; }

    public DateTime? CreateTime { get; set; }

    public string? ModifyUser { get; set; }

    public DateTime? ModifyTime { get; set; }

    /// <summary>When a build first listed it; null for an id a ledger expects that OSDU never served while the inventory read it.</summary>
    public DateTime? FirstSeenUtc { get; set; }

    /// <summary>When a build last saw its version or system properties change.</summary>
    public DateTime? ChangedUtc { get; set; }

    /// <summary>When a complete build first did not list it; null while OSDU serves it.</summary>
    public DateTime? GoneUtc { get; set; }

    /// <summary>The latest version its versions were read at (<c>versions: all</c>), so a rebuild reads versions only for what moved.</summary>
    public long? VersionsAt { get; set; }

    /// <summary>
    /// When a reconcile last asked storage for it, a ledger expecting it and the read not listing it; null when none has. The
    /// ids a ledger expects are asked for least recently asked first, so builds bounded by <c>maxMissingChecks</c> take turns
    /// through every one of them.
    /// </summary>
    public DateTime? CheckedUtc { get; set; }

    /// <summary>What the ledgers hold of it: tracked, drifted, unconfirmed, stale, superseded, undoing, forgotten, orphan, foreign, missing, unlisted, gone or unreconciled.</summary>
    public string Finding { get; set; } = string.Empty;

    /// <summary>When its finding became what it is.</summary>
    public DateTime FindingUtc { get; set; }

    /// <summary>The ledger and record that claim it, with the record's status and the version it holds, when one does.</summary>
    public Guid? LedgerFlowId { get; set; }

    public Guid? DeliveryKey { get; set; }

    public string? LedgerStatus { get; set; }

    public long? LedgerVersion { get; set; }

    /// <summary>The artifact a delivery recorded for it, with its state, when the ledgers know it as one.</summary>
    public long? ArtifactId { get; set; }

    public string? ArtifactState { get; set; }

    /// <summary>What the finding rests on, in a line.</summary>
    public string? Detail { get; set; }
}

/// <summary>One version storage keeps of a record an inventory holds (<c>versions: all</c>).</summary>
public sealed class DeliveryInventoryVersion
{
    public short PartitionId { get; set; }

    public long InventoryRecordId { get; set; }

    public long Version { get; set; }
}

/// <summary>
/// One removal of an inventory's ids an operator asked for (osdu/docs/reference/flow/inventory.md, What a removal does):
/// the run it was, who asked, the finding and how much of each record it removes, how many ids the operator was shown, and
/// what it came to. Every id it reached is a <see cref="DeliveryInventoryRemovalItem"/>.
/// </summary>
public sealed class DeliveryInventoryRemoval
{
    public short PartitionId { get; set; }

    public long InventoryRemovalId { get; set; }

    public int InventoryId { get; set; }

    /// <summary>The platform run the removal ran as.</summary>
    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>The finding whose ids it removes.</summary>
    public string Finding { get; set; } = string.Empty;

    /// <summary>record (a soft delete) or everything (a purge).</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>True when the removal named its ids; false when it took every id of the finding.</summary>
    public bool NamesIds { get; set; }

    /// <summary>How many ids the operator was shown, which the removal held the inventory to before it removed anything.</summary>
    public long Requested { get; set; }

    /// <summary>running, completed or failed.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public long Removed { get; set; }

    public long Gone { get; set; }

    public long Skipped { get; set; }

    public long Failed { get; set; }

    /// <summary>Why the removal stopped, redacted; null for one that completed.</summary>
    public string? Error { get; set; }

    /// <summary>The audit trail's activity of the removal.</summary>
    public long? ActivityId { get; set; }

    /// <summary>
    /// The selection the removal took its ids from (<see cref="DeliveryInventorySelection"/>): the ids of the finding that held
    /// what an operator's conditions asked in OSDU. Null for a removal that named its ids or took every id of the finding.
    /// </summary>
    public long? InventorySelectionId { get; set; }
}

/// <summary>
/// The ids of an inventory that hold what an operator's conditions ask in OSDU (osdu/docs/reference/flow/inventory.md,
/// Selecting ids by what OSDU holds): the conditions as the operator wrote them and as the search was asked them, how the
/// match read OSDU and what it found, and the build of the inventory it was matched against. Every id it found is a
/// <see cref="DeliveryInventorySelectionItem"/>. A match runs as a run of the inventory flow; a removal may take its ids.
/// </summary>
public sealed class DeliveryInventorySelection
{
    public short PartitionId { get; set; }

    public long InventorySelectionId { get; set; }

    public int InventoryId { get; set; }

    /// <summary>The platform run the match ran as; null until it is queued.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Who asked for the match.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>The conditions as the operator wrote them, a search term by its id (JSON), as the page shows them again.</summary>
    public string ConditionsJson { get; set; } = string.Empty;

    /// <summary>
    /// A hash of <see cref="ConditionsJson"/>, by which the same conditions asked again of the same build find the match
    /// already made rather than reading OSDU again.
    /// </summary>
    public string ConditionsKey { get; set; } = string.Empty;

    /// <summary>
    /// The conditions as the search is asked them: an explorer search of the inventory's kind (JSON), each search term carried
    /// through its mapping to the property its route fills.
    /// </summary>
    public string FiltersJson { get; set; } = string.Empty;

    /// <summary>queued, running, completed, failed or expired.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The Lucene query the conditions came to, as the match sent it; null until the match runs.</summary>
    public string? Query { get; set; }

    /// <summary>
    /// How the match read OSDU: <c>conditions</c> (every record the conditions find in the inventory's kind, joined with the
    /// inventory's ids) or <c>ids</c> (the inventory's ids in chunks, each asked with the conditions); null until it decides.
    /// </summary>
    public string? Direction { get; set; }

    /// <summary>What the reader should know of how the conditions were read (JSON list): a source column read through other records, a value that found nothing.</summary>
    public string? NotesJson { get; set; }

    /// <summary>The properties whose values each id's row carries, those the conditions ask (JSON list).</summary>
    public string? ColumnsJson { get; set; }

    /// <summary>The inventory's last build when the match read OSDU; a later build makes the match stale.</summary>
    public long? BuildRunId { get; set; }

    /// <summary>The ids the inventory serves that the match judged.</summary>
    public long Candidates { get; set; }

    /// <summary>How much the match has to read, and has read: the records the conditions find, or the inventory's ids.</summary>
    public long ToCheck { get; set; }

    public long Checked { get; set; }

    /// <summary>The ids of the inventory that hold what the conditions ask.</summary>
    public long Matched { get; set; }

    /// <summary>Records the conditions find in the inventory's kind that the inventory serves no row of: created since its last build, or outside its query.</summary>
    public long Outside { get; set; }

    /// <summary>Ids matched at another version than the inventory listed: changed since its last build, which a removal leaves for the next build.</summary>
    public long Changed { get; set; }

    /// <summary>The search requests the match sent.</summary>
    public long Requests { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    /// <summary>Why the match failed, redacted; null for one that did not.</summary>
    public string? Error { get; set; }
}

/// <summary>One id of an inventory a match found holding what its conditions ask: the version the index holds and the values it holds there.</summary>
public sealed class DeliveryInventorySelectionItem
{
    public short PartitionId { get; set; }

    public long InventorySelectionId { get; set; }

    public long InventoryRecordId { get; set; }

    /// <summary>The version the search index held of it when the match read it.</summary>
    public long? MatchedVersion { get; set; }

    /// <summary>What it held at each property the conditions ask, property to values (JSON), cut to the column.</summary>
    public string? ValuesJson { get; set; }
}

/// <summary>What one removal did to one id: removed, already gone, skipped or failed, at the version it found, and why.</summary>
public sealed class DeliveryInventoryRemovalItem
{
    public short PartitionId { get; set; }

    public long InventoryRemovalItemId { get; set; }

    public long InventoryRemovalId { get; set; }

    public long InventoryRecordId { get; set; }

    /// <summary>The OSDU id, compared exactly.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>The version the inventory listed it at.</summary>
    public long? Version { get; set; }

    /// <summary>The finding it had when the removal reached it.</summary>
    public string Finding { get; set; } = string.Empty;

    /// <summary>removed, gone, skipped or failed.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Why, in a line: what was asked of OSDU, what moved, or what OSDU answered (redacted).</summary>
    public string? Reason { get; set; }

    /// <summary>The ledger and record that held it, for a stale or forgotten id.</summary>
    public Guid? LedgerFlowId { get; set; }

    public Guid? DeliveryKey { get; set; }

    public DateTime RecordedUtc { get; set; }
}

/// <summary>One id a build listed, staged until the build's read is whole and it is merged into the inventory.</summary>
public sealed class DeliveryInventoryScan
{
    public short PartitionId { get; set; }

    public long ScanId { get; set; }

    public long InventoryRunId { get; set; }

    public string TargetId { get; set; } = string.Empty;

    public string? Kind { get; set; }

    public long? Version { get; set; }

    public string? CreateUser { get; set; }

    public DateTime? CreateTime { get; set; }

    public string? ModifyUser { get; set; }

    public DateTime? ModifyTime { get; set; }
}

/// <summary>
/// One reversal: what one run or one submission put into OSDU, put back record by record as OSDU held it before
/// (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission). There is one per source of a
/// ledger; asking again resumes it. Its counts are read from its items, never kept here.
/// </summary>
public sealed class DeliveryReversal
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ReversalId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    /// <summary>run or submission.</summary>
    public string SourceKind { get; set; } = string.Empty;

    /// <summary>The run or the submission reversed.</summary>
    public Guid SourceId { get; set; }

    /// <summary>The submissions the source covers, as a JSON array of ids, fixed when the reversal was opened.</summary>
    public string SubmissionsJson { get; set; } = "[]";

    /// <summary>capturing, reversing, completed, failed or cancelled.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Who asked for the reversal first.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    public DateTime RequestedUtc { get; set; }

    /// <summary>When every record the source delivered was listed as an item; null while the listing is not finished.</summary>
    public DateTime? CapturedUtc { get; set; }

    /// <summary>When the latest run working on it started.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>When the latest run working on it ended.</summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>The latest run that worked on it.</summary>
    public Guid? LastRunId { get; set; }

    /// <summary>Why the latest run stopped, redacted; null when it did not fail.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// One record a reversal reaches: what its source delivered of it, what OSDU held before, and what the reversal did. Written
/// when the reversal lists its source, and settled in the transaction that changes the record.
/// </summary>
public sealed class DeliveryReversalItem
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ReversalId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The OSDU id the record claimed when the reversal listed it; null for a record that claimed none.</summary>
    public string? TargetId { get; set; }

    /// <summary>The source's first delivered attempt of the record.</summary>
    public long FirstAttemptId { get; set; }

    /// <summary>The version that attempt wrote.</summary>
    public long? FirstVersion { get; set; }

    /// <summary>The version the source's last delivered attempt of the record left.</summary>
    public long? RunVersion { get; set; }

    /// <summary>What OSDU held before the source: version, none or unknown.</summary>
    public string Prior { get; set; } = string.Empty;

    /// <summary>With <see cref="Prior"/> version: the version OSDU held.</summary>
    public long? PriorVersion { get; set; }

    /// <summary>The attempt that delivered or restored the version OSDU held before, when the ledger still has it: its hashes and origin are what the record gets back.</summary>
    public long? PriorAttemptId { get; set; }

    /// <summary>pending, sending, done, skipped or failed.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>What came of it once settled: restored, removed, already-gone, or why it was passed over or failed.</summary>
    public string? Outcome { get; set; }

    /// <summary>The reason, redacted.</summary>
    public string? Detail { get; set; }

    /// <summary>The version a restore put back.</summary>
    public long? RestoredVersion { get; set; }

    /// <summary>The version OSDU gave the restored record.</summary>
    public long? NewVersion { get; set; }

    /// <summary>The reverse run that settled it, or marked it sending.</summary>
    public Guid? RunId { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// A search term a repository's pipelines give (osdu/docs/reference/concepts/search-terms.md) for one entity type: a
/// column of a source table the repository's active delivery flows read, with every route by which it reaches the
/// records of that type they render. A term is the table's column, whichever flows read the table and whichever
/// mappings render it; a row is the term in one repository and one entity type. The sync writes the rows again whenever
/// it runs, from the flows and the mappings they pin; what a person makes of a term is kept apart, in
/// <see cref="DeliverySearchTermRefinement"/>, by the term's identity, so a sync never undoes it.
/// </summary>
public sealed class DeliverySearchTerm
{
    /// <summary>The longest table, entity type and column a term names.</summary>
    public const int MaxSourceLength = 400;

    public const int MaxEntityTypeLength = 200;

    public const int MaxColumnLength = 128;

    /// <summary>Stable id: derived from the repo id, the entity type and the term's key.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The term's identity, the same in every repository, entity type and host: derived from its key alone.</summary>
    public Guid TermId { get; set; }

    /// <summary>The term's key as text: <c>osdudata.arc.welllog/wellbore_uwi</c>.</summary>
    public string TermKey { get; set; } = string.Empty;

    /// <summary>The table the column is read from, its parts without brackets (<c>OsduData.arc.WellLog</c>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The entity type the routes fill (<c>work-product-component--WellLog</c>).</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The column as the mappings name it.</summary>
    public string Column { get; set; } = string.Empty;

    /// <summary>The routes by which the column reaches the records of the entity type, as JSON (<c>SearchRoute</c>).</summary>
    public string RoutesJson { get; set; } = "[]";

    /// <summary>The active delivery flows whose mappings read the column, as a JSON list of names.</summary>
    public string FlowsJson { get; set; } = "[]";

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// What a person made of a search term (osdu/docs/reference/concepts/search-terms.md): the name it is searched by,
/// whether it is left out of the search, which of its routes it is searched through, and a note on it, with who changed
/// it last and when. Kept by the term's identity, apart from the terms a sync writes, so it holds across every sync and
/// every mapping version that keeps the column; one whose term no mapping gives any longer stays until it is removed,
/// and applies again if the term comes back.
/// </summary>
public sealed class DeliverySearchTermRefinement
{
    /// <summary>The longest name a term is given, and the longest note on it.</summary>
    public const int MaxNameLength = 100;

    public const int MaxNoteLength = 1000;

    /// <summary>The longest route id: a template variable and how the value reaches it.</summary>
    public const int MaxRouteLength = 1100;

    /// <summary>The term refined (<see cref="DeliverySearchTerm.TermId"/>).</summary>
    public Guid TermId { get; set; }

    /// <summary>The term's key when it was refined, which names it while no mapping gives it.</summary>
    public string TermKey { get; set; } = string.Empty;

    /// <summary>The entity type the term searches, within which no two terms share a name.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The name the term is searched by; null to keep the column's own.</summary>
    public string? Name { get; set; }

    /// <summary>True when the term is left out of the search.</summary>
    public bool Excluded { get; set; }

    /// <summary>The route the term is searched through (<c>SearchRoute.Id</c>); null for the one the explorer prefers.</summary>
    public string? Route { get; set; }

    /// <summary>A note on the term, shown beside it wherever it is offered; null for none.</summary>
    public string? Note { get; set; }

    /// <summary>Who changed the refinement last.</summary>
    public string UpdatedBy { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>A mapping document as the sync found it in a repository: the read model behind the mappings page.</summary>
public sealed class DeliveryMapping
{
    /// <summary>Stable id: derived from the repo id and the mapping reference.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>Name@version.</summary>
    public string Reference { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    /// <summary>The OSDU kind of the template the mapping fills.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The template version the mapping fills: the one it names, or the one the template folders on its path settle a kind
    /// named alone to. Kept for a document that fails to load when it names a version; empty when there is none.
    /// </summary>
    public string TemplateVersion { get; set; } = string.Empty;

    /// <summary>
    /// The file of the repository the template was settled from, in the template folder on the mapping's path that holds it
    /// (osdu/docs/reference/concepts/templates.md, Template folders); empty when no folder on its path holds it.
    /// </summary>
    public string TemplatePath { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The document text, secret-redacted, as it was when synced.</summary>
    public string Yaml { get; set; } = string.Empty;

    /// <summary>A parsed summary (template, dataset system, key and label, child datasets, entry and fixture counts, envelope), for listings.</summary>
    public string SummaryJson { get; set; } = "{}";

    /// <summary>valid or invalid.</summary>
    public string Status { get; set; } = "valid";

    public string? Message { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// One interface of a delivery flow document, as the repository sync found it (osdu/docs/reference/flow/interfaces.md, The
/// interface read model): the pipeline it belongs to, the ledger identity its records, submissions and statistics are kept
/// under, and what it delivers and how. The API and the GUI find the pipeline of a ledger identity here, and the interfaces
/// of a pipeline, without parsing a document. A document in the single form has one row, whose interface name is empty. A
/// document that names its partitions has one row per interface and partition, each with the partition's ledger
/// (osdu/docs/reference/concepts/partitions.md, A ledger per partition). The row of an interface the repository no longer
/// declares is kept, inactive, so the records it delivered still lead to their flow.
/// </summary>
public sealed class DeliveryInterface
{
    /// <summary>The longest interface name.</summary>
    public const int MaxInterfaceLength = 64;

    /// <summary>
    /// Stable id: derived from the repository, the flow's name and the interface's name, and for a flow that names its
    /// partitions the partition's name as well.
    /// </summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The flow's name: its pipeline in the repository.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The interface's name; empty for a document in the single form.</summary>
    public string Interface { get; set; } = string.Empty;

    /// <summary>The partition whose ledger the row describes; empty for a flow that names no partitions.</summary>
    public string Partition { get; set; } = string.Empty;

    /// <summary>
    /// The partition the interface's records reach, as the OSDU order groups deliveries (osdu/docs/reference/concepts/
    /// osdu-order.md, The order of a partition): the one its <c>target.headers</c> name, as the document writes it, or <c>*</c>
    /// for a source that leaves its partitions to the registry while none is registered. Empty when the headers name
    /// none the order can read, which leaves the interface out of the order, as lineage leaves it out.
    /// </summary>
    public string TargetPartition { get; set; } = string.Empty;

    /// <summary>Where the interface is in its document, from 0.</summary>
    public int Ordinal { get; set; }

    /// <summary>The ledger identity (the flow id every ledger row of the interface carries).</summary>
    public Guid LedgerFlowId { get; set; }

    /// <summary>The name the ledger identity is derived from: the flow, <c>flow/interface</c>, or the ledger it adopts.</summary>
    public string LedgerName { get; set; } = string.Empty;

    /// <summary>The route the interface is delivered by: storage, file, dataset, manifest, ddms, fileAndDdms, manifestAndDdms or workflow.</summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>Why it goes by that route; null for the single form, whose document names its protocol.</summary>
    public string? RouteReason { get; set; }

    /// <summary>The mapping it pins (Name@version).</summary>
    public string MappingReference { get; set; } = string.Empty;

    /// <summary>
    /// The version of the partition's cache it renders against, as its <c>render.cacheVersion</c> says: <c>current</c>, or
    /// the label of the version it pins. The cache's retention keeps the records of every version an active interface pins.
    /// Null for a row the sync wrote before it recorded this: until the next sync records it, the retention of the partitions
    /// the interface may read prunes nothing, since it cannot tell which versions are pinned.
    /// </summary>
    public string? CacheVersion { get; set; }

    /// <summary>The OSDU kind the mapping fills, when the repository holds a valid mapping of that reference; empty otherwise.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The record table it reads.</summary>
    public string RecordObject { get; set; } = string.Empty;

    /// <summary>The interfaces of the document it waits for (<c>after:</c>), as a JSON array of names.</summary>
    public string AfterJson { get; set; } = "[]";

    public string RelativePath { get; set; } = string.Empty;

    /// <summary>False once the repository no longer declares the interface.</summary>
    public bool Active { get; set; } = true;

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// A saved template, the library (osdu/docs/reference/concepts/templates.md): the OSDU schema of one kind, captured from the
/// OSDU data definitions or imported from a file, by a person, on the Templates page or the command line. A version is
/// identified by the kind and the hash of its schema, and never changes. The explorer, the mapping builder and the
/// assistant read the library; a repository's documents read their templates from their template folders, and the
/// repositories never write here.
/// </summary>
public sealed class DeliveryTemplate
{
    /// <summary>Stable id: derived from the kind and the version.</summary>
    public Guid Id { get; set; }

    /// <summary>The OSDU kind the schema describes (osdu:wks:work-product-component--WellLog:1.4.0).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The content version: the hash prefix of the canonical bundled schema.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The bundled JSON Schema, every reference resolved into its definitions.</summary>
    public string SchemaJson { get; set; } = string.Empty;

    /// <summary>Where the schema came from: the flow and endpoint reference it was captured through, or the imported file.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>Who captured or imported it.</summary>
    public string CapturedBy { get; set; } = string.Empty;

    public DateTime CapturedUtc { get; set; }
}

/// <summary>
/// A file of a template folder of a repository, as the last sync found it (osdu/docs/reference/concepts/templates.md,
/// Template folders): which template it holds with a copy of its schema, or why it holds none. A template folder is a
/// directory named <c>templates</c>, and a document reads those on its path; the control plane's pages settle a document's
/// template from these rows by the same rule a run settles it by from the files, and read its schema from the copy here,
/// since they have no working copy. The copy is the repository's own: the library of saved templates
/// (<see cref="DeliveryTemplate"/>) never holds it. A file the repository no longer holds loses its row.
/// </summary>
public sealed class DeliveryRepositoryTemplate
{
    /// <summary>The longest path of a template file, or of its folder, in its repository.</summary>
    public const int MaxPathLength = 400;

    /// <summary>The longest reason a file holds no template.</summary>
    public const int MaxProblemLength = 2000;

    /// <summary>Stable id: derived from the repo id and the file's path.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The file's path in the repository, with forward slashes (Welllog/templates/osdu_wks_work-product-component--WellLog_1.4.0.json).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The template folder the file is in, its path in the repository (Welllog/templates).</summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>The kind the file's schema declares (x-osdu-schema-source); empty when the file holds no template.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The template version the file holds, the hash of its canonical schema; empty when it holds no template.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Why the file holds no template; null when it holds one.</summary>
    public string? Problem { get; set; }

    /// <summary>
    /// The schema the file holds as the sync read it, compact JSON whose canonical form hashes to <see cref="Version"/>; null
    /// when the file holds no template.
    /// </summary>
    public string? SchemaJson { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// A cached OSDU type as one cache flow (<c>flowType: cache</c>) declares it: which records it captures into its partition's
/// cache and which paths of each it keeps. The cache flow's YAML in the repository is the only place what is cached is
/// defined; the sync copies the declaration here, so a refresh knows every path the partition keeps for a type and the GUI
/// can show which files fill a cache. Several flows may declare the same type for one partition: the cache holds their union.
/// </summary>
public sealed class DeliveryCacheDefinition
{
    /// <summary>Stable id: derived from the repo id, the cache flow name and the cached type name.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The cache flow that declares the type.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>
    /// The partition whose cache the flow fills: one of the partitions it names (<c>partitions</c>), or for a flow that names
    /// none, its <c>source.headers.data-partition-id</c> resolved.
    /// </summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// True when the flow works in partitions (osdu/docs/reference/flow/cache.md, Which partitions a flow fills): it names
    /// the partitions it builds a cache for, or leaves them to the registry. It then has a row per type and partition it
    /// serves, and a refresh of it names the partition it builds. False for a flow whose partition is its header's.
    /// </summary>
    public bool DeclaresPartitions { get; set; }

    /// <summary>
    /// Where the type's records come from: <c>osdu</c> (searched on the platform), <c>table</c> (an ingestion table) or
    /// <c>dictionary</c> (a dictionary document in the repository). Rows written before origins existed are osdu.
    /// </summary>
    public string Origin { get; set; } = "osdu";

    /// <summary>For an OSDU type: the endpoint the flow searches, as declared (a reference, never a resolved value).</summary>
    public string? Endpoint { get; set; }

    /// <summary>For a table type: the flow's source connection, as declared (a reference, never a resolved value).</summary>
    public string? Connection { get; set; }

    /// <summary>For a table type: the three-part name of the ingestion table read.</summary>
    public string? SourceObject { get; set; }

    /// <summary>For a table or dictionary type: the name each row's key is kept under.</summary>
    public string? KeyField { get; set; }

    /// <summary>For a dictionary type: the dictionary document's file, relative to the repository root.</summary>
    public string? DictionaryPath { get; set; }

    /// <summary>The cache flow's file, relative to the repository root.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>The short name mappings use (UnitOfMeasure).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The OSDU entity type (reference-data--UnitOfMeasure), or lookup--&lt;Name&gt; for a table or dictionary type.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>For an OSDU type: the search kind the capture sweeps.</summary>
    public string? Kind { get; set; }

    /// <summary>For an OSDU type: the search query narrowing the capture.</summary>
    public string? Query { get; set; }

    /// <summary>
    /// What the cache keeps of each record as JSON: <c>[{ "path": "data.Code", "as": "Code" }]</c>, the path a column for a
    /// table type, and for a dictionary type the fields its document names.
    /// </summary>
    public string FieldsJson { get; set; } = "[]";

    /// <summary>approve or auto: what a changed cached value of this type does to the records already built from it.</summary>
    public string OnChange { get; set; } = "auto";

    /// <summary>
    /// How many days the partition's cache keeps the records of a version after a newer one replaced it, as the flow declares
    /// it (<c>retentionDays</c>, 7 when it declares none). The partition keeps the longest any of its cache flows asks for.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// One version of a partition's cache: the whole cache as one merge left it, written by the cache flow whose capture moved
/// it. A capture that changes what the cache holds writes the next version, which becomes current, and one that changes
/// nothing writes none, because a new version moves the render context of every record built against the cache. A version's
/// content never changes once written. Its records are kept while it is current, while it is the version the current one
/// replaced, while a delivery flow pins it, and for the partition's retention after a newer version replaced it; then a
/// refresh prunes them (<see cref="PrunedUtc"/>). The row itself is kept for as long as the ledger exists: a delivered
/// record's render context names the version, and the row says who captured it, when, from where and what it changed.
/// </summary>
public sealed class DeliveryCacheVersion
{
    /// <summary>Stable id: derived from the partition and the version label.</summary>
    public Guid Id { get; set; }

    /// <summary>The partition whose cache the version belongs to.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cache flow whose capture, or import, wrote the version.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The label, minted from the capture instant (20260910T165153Z), with the sequence appended when two captures share a second.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The version's place in the partition's history, 1 for the first; the ranges of <see cref="DeliveryCacheItem"/> count in it.</summary>
    public int Sequence { get; set; }

    public DateTime CapturedUtc { get; set; }

    /// <summary>Hash of the whole content, checked on every load so a version altered after it was written is refused.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The version that was current when this one was written, which the capture was merged onto; null for the first.</summary>
    public string? PreviousVersion { get; set; }

    /// <summary>Whether this is the newest version, the one deliveries render against unless a flow pins another.</summary>
    public bool Current { get; set; }

    /// <summary>The platform run that captured the version; null for one imported from files.</summary>
    public Guid? RunId { get; set; }

    /// <summary>
    /// Who asked for it: the capturing run's requester as SQLFlow records it (the token's subject for a run a person queued,
    /// schedule:&lt;name&gt; for one a schedule fired, cli:&lt;user&gt;@&lt;machine&gt; for a direct CLI run), or
    /// cli:&lt;user&gt;@&lt;machine&gt; for an import from the command line.
    /// </summary>
    public string CapturedBy { get; set; } = string.Empty;

    /// <summary>Where the content came from: the OSDU endpoint reference a capture searched, or the directory an import read.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>The types the version holds, by name, each with its entity type and record count: <c>[{ "name", "entityType", "items" }]</c>.</summary>
    public string TypesJson { get; set; } = "[]";

    /// <summary>
    /// The partition's system properties the capture found, kept apart from the cached records because they describe the
    /// platform rather than any record: <c>[{ "service", "name", "state", "source", "detail" }]</c>. Empty for a version
    /// written before captures recorded them, or by an import onto a cache no capture had asked about.
    /// </summary>
    public string SystemPropertiesJson { get; set; } = "[]";

    /// <summary>How many cached records the version holds across its types.</summary>
    public long Items { get; set; }

    /// <summary>
    /// When the cache's retention removed the version's records; null while they are kept. A pruned version can no longer be
    /// read, compared or rendered against, and its row still describes it.
    /// </summary>
    public DateTime? PrunedUtc { get; set; }

    /// <summary>
    /// Who pruned the version's records: the requester of the refresh that applied the retention (a person, or
    /// schedule:&lt;name&gt;), the account of the import, or the operator who purged the cache's history. Null while they are
    /// kept, and for a version pruned before this was recorded.
    /// </summary>
    public string? PrunedBy { get; set; }

    /// <summary>
    /// What the version changed against the one before it, per type, as counted from its records before a retention removed
    /// any of them: <c>[{ "type", "changed", "added", "removed" }]</c>, listing the types that moved. Null while the history
    /// can still count them from the records.
    /// </summary>
    public string? ChangesJson { get; set; }
}

/// <summary>
/// One cached record as a run of consecutive versions of its partition's cache held it: the OSDU id and the values captured
/// at the declared paths, valid from the version at <see cref="FromSequence"/> up to, and not including, the one at
/// <see cref="ToSequence"/>, which is null while the newest version still holds it unchanged. A record is stored once per
/// partition however many cache flows capture it, and a merge writes rows only for the records that changed, arrived or
/// left, so keeping every version costs rows in proportion to what moved rather than to the size of the cache times the
/// number of captures.
/// </summary>
public sealed class DeliveryCacheItem
{
    public long ItemId { get; set; }

    /// <summary>The partition whose cache holds the record.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cached type's short name (UnitOfMeasure).</summary>
    public string TypeName { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    /// <summary>The OSDU record id, without a version.</summary>
    public string RecordId { get; set; } = string.Empty;

    /// <summary>The captured values as JSON, in whatever shape the paths yielded, names in ordinal order.</summary>
    public string FieldsJson { get; set; } = "{}";

    /// <summary>Every scalar the item holds, newline separated: what a search over cached values matches on.</summary>
    public string Terms { get; set; } = string.Empty;

    /// <summary>The sequence of the first version that holds the record with these values.</summary>
    public int FromSequence { get; set; }

    /// <summary>The sequence of the first version that no longer holds it so; null while the newest version still does.</summary>
    public int? ToSequence { get; set; }
}

/// <summary>
/// OSDU's lineage as one release of the OSDU data definitions declares it (osdu/docs/reference/concepts/osdu-order.md,
/// OSDU's lineage by release): which release and commit it was read from, when and by whom it was built, and how much it
/// holds. What each kind refers to is in <see cref="SchemaLineageKind"/>. A release is built once and kept; building it
/// again replaces its kinds.
/// </summary>
public sealed class SchemaLineage
{
    /// <summary>The longest release name a lineage keeps.</summary>
    public const int MaxReleaseLength = 100;

    public Guid Id { get; set; }

    /// <summary>The release's tag (<c>v0.29.0</c>).</summary>
    public string Release { get; set; } = string.Empty;

    /// <summary>The commit the release's tag names.</summary>
    public string Commit { get; set; } = string.Empty;

    /// <summary>When the release was published, when the repository says.</summary>
    public DateTime? PublishedUtc { get; set; }

    public DateTime BuiltUtc { get; set; }

    /// <summary>Who built it: a user, or the command line's local account.</summary>
    public string BuiltBy { get; set; } = string.Empty;

    /// <summary>How many kinds it holds.</summary>
    public int Kinds { get; set; }

    /// <summary>How many properties of those kinds refer to other records.</summary>
    public int Relationships { get; set; }

    /// <summary>The kinds the release lists that could not be read, each with why: a JSON array of <c>{ "kind", "reason" }</c>.</summary>
    public string FailuresJson { get; set; } = "[]";
}

/// <summary>
/// One kind of a release's lineage (<see cref="SchemaLineage"/>): its entity type and version, its status in the release, and
/// every property of its schema that refers to other records, with the entity types each names: a JSON array of
/// <c>{ "property", "targets": [ ... ] }</c>, as the OSDU order reads a schema.
/// </summary>
public sealed class SchemaLineageKind
{
    public Guid LineageId { get; set; }

    /// <summary>The kind (<c>osdu:wks:master-data--Wellbore:1.3.0</c>).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The entity type it is of (<c>master-data--Wellbore</c>).</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>The kind's version (<c>1.3.0</c>).</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The kind's status in the release (<c>PUBLISHED</c>, <c>DEVELOPMENT</c>), when the release says.</summary>
    public string? Status { get; set; }

    /// <summary>The properties that refer to other records, each with the entity types its relationship names.</summary>
    public string RelationshipsJson { get; set; } = "[]";
}

/// <summary>
/// That a cache flow's last capture of a type held a record. It is what lets several flows share one partition's cache: a
/// record a flow no longer finds leaves the cache only when no other flow's capture still holds it. Current state, not
/// history; what each version held is in <see cref="DeliveryCacheItem"/>.
/// </summary>
public sealed class DeliveryCacheMember
{
    /// <summary>The partition whose cache holds the record.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cached type's short name (UnitOfMeasure).</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>The OSDU record id, without a version.</summary>
    public string RecordId { get; set; } = string.Empty;

    /// <summary>The cache flow whose last capture of the type held the record.</summary>
    public string FlowName { get; set; } = string.Empty;
}

/// <summary>
/// One distinct combination of cached values that records were built from: the set of (type, cached record, path,
/// value) a render consumed. Records share sets heavily (every log that resolved metres and the same wellbore
/// shares one), so the estate's dependency trail is a few thousand sets rather than a row per record per value.
/// A record points at its set; a cache change finds the sets that hold the changed value and, through them, the
/// records to update.
/// </summary>
public sealed class DeliveryCacheSet
{
    public long SetId { get; set; }

    /// <summary>Content hash of the set's entries: the identity a render computes without a round trip.</summary>
    public string SetHash { get; set; } = string.Empty;

    public int EntryCount { get; set; }

    /// <summary>
    /// Set while a change to one of these values is tagged and unapproved. The planner reads the gated sets once
    /// per run and skips their records, so holding records back never means writing to them.
    /// </summary>
    public bool Gated { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>One cached value inside a set: what was read, and what it read at the time.</summary>
public sealed class DeliveryCacheSetEntry
{
    public long SetId { get; set; }

    /// <summary>The partition whose cache the value was read from: the partition the delivery flow delivers to.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cached type's short name (UnitOfMeasure).</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>The cached record's OSDU id.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>What the mapping read: <c>id</c>, <c>Name</c>, <c>NameAlias.AliasName</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// How the render used it: match (the value the source resolved by), value (a value written into the document), empty
    /// (a path that held no value), unlisted (a key a lookup table listed no row under), listed (the rows a $findAll found
    /// by a key), unverified (an id written as an unverified reference) or missing (an id the cache did not hold).
    /// </summary>
    public string Kind { get; set; } = "value";

    public string ValueHash { get; set; } = string.Empty;

    /// <summary>The value as it was consumed, truncated for display.</summary>
    public string ValueText { get; set; } = string.Empty;
}

/// <summary>
/// One change to the cache that delivered records were built from, and what happens about it. A tag is written per
/// change, not per record: a corrected unit name is one decision covering every record that read it, with the count
/// of what it affects. Under <c>approve</c> the affected sets are gated until someone decides; under <c>auto</c> it
/// is approved as written. Either way the update is rolled out in batches from <see cref="Cursor"/>, so a change
/// touching millions of records drains at a controlled rate instead of flooding the estate.
/// </summary>
public sealed class DeliveryUpdateTag
{
    public long TagId { get; set; }

    /// <summary>What caused the tag; <c>cache</c> today.</summary>
    public string Kind { get; set; } = "cache";

    /// <summary>The partition whose cache the change was found in.</summary>
    public string Scope { get; set; } = string.Empty;

    public string TypeName { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// changed (the value moved, or a path that gave none gives one), removed (the cached record is gone), unmatched (what it
    /// matched by is gone), listed (a lookup table now lists a key records looked up and found no row under, or a type now
    /// holds a record by a value records found none by), relisted (the rows a key finds are no longer the ones records read)
    /// or found (the cache now holds a record records reference as an unverified id).
    /// </summary>
    public string Change { get; set; } = "changed";

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string? FromVersion { get; set; }

    public string ToVersion { get; set; } = string.Empty;

    /// <summary>auto or approve, as the cache declared for this type when the tag was written.</summary>
    public string Mode { get; set; } = "approve";

    /// <summary>pending, approved, rejected, rolling or applied.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>The cache sets holding the changed value, comma separated; the records are found through them.</summary>
    public string SetIds { get; set; } = string.Empty;

    /// <summary>How many delivered records were built from the old value when the change was found.</summary>
    public long AffectedRecords { get; set; }

    /// <summary>How many of them the rollout has marked for redelivery so far.</summary>
    public long Processed { get; set; }

    /// <summary>
    /// Where the rollout got to, in delivery-key order and then flow order, so a pass resumes rather than restarts: the
    /// delivery key of the last record marked.
    /// </summary>
    public Guid? Cursor { get; set; }

    /// <summary>The flow of the last record marked; with <see cref="Cursor"/>, where the rollout got to.</summary>
    public Guid? CursorFlowId { get; set; }

    public DateTime DetectedUtc { get; set; }

    public DateTime? DecidedUtc { get; set; }

    public string? DecidedBy { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }
}

/// <summary>One retrieval run: the window it covered, where its files went, and its outcome.</summary>
public sealed class DeliveryRetrieval
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long RetrievalId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>The kinds the run covered, comma separated.</summary>
    public string Kinds { get; set; } = string.Empty;

    /// <summary>The query as it ran, window included.</summary>
    public string? Query { get; set; }

    public string? WindowField { get; set; }

    public DateTime? WindowFrom { get; set; }

    public DateTime? WindowTo { get; set; }

    public string Location { get; set; } = string.Empty;

    public string? ManifestLocation { get; set; }

    public string Status { get; set; } = "running";

    public long Records { get; set; }

    public int Files { get; set; }

    /// <summary>Uncompressed bytes written.</summary>
    public long Bytes { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One run of an assertion flow's tests in one partition (osdu/docs/reference/flow/assertion.md, The report): the tests it
/// ran, how they came out, and who asked. The results of each test are rows of <see cref="DeliveryAssertionResult"/>.
/// </summary>
public sealed class DeliveryAssertionRun
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long AssertionRunId { get; set; }

    /// <summary>The ledger identity of the flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    /// <summary>The platform run the tests ran in.</summary>
    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>The tests the run was asked for, as JSON (an object of tests and tags); null when it ran every test.</summary>
    public string? Selection { get; set; }

    /// <summary>running, passed, failed, errored or cancelled.</summary>
    public string Status { get; set; } = "running";

    /// <summary>The tests the run took up, those skipped included.</summary>
    public int Tests { get; set; }

    public int Passed { get; set; }

    public int Failed { get; set; }

    public int Warned { get; set; }

    public int Errored { get; set; }

    public int Skipped { get; set; }

    /// <summary>A hash of the tests the run ran, as their definitions stood: two runs of the same tests hash the same.</summary>
    public string? DefinitionsHash { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One test's result in one run of an assertion flow: its outcome, what it read and matched, and every assertion with what
/// it expected, what it found and the records that failed it (<see cref="Detail"/>, JSON).
/// </summary>
public sealed class DeliveryAssertionResult
{
    /// <summary>The partition the row belongs to: the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ResultId { get; set; }

    public long AssertionRunId { get; set; }

    /// <summary>The ledger identity of the flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string TestName { get; set; } = string.Empty;

    /// <summary>The OSDU kind the test reads.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>passed, failed, warned, errored or skipped.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The heaviest severity among the assertions that failed; null when none did.</summary>
    public string? Severity { get; set; }

    /// <summary>The records the test matched (the index's count, or the ids storage holds); null when it did not get as far.</summary>
    public long? Matched { get; set; }

    /// <summary>The records the test read and held its assertions to.</summary>
    public long? Evaluated { get; set; }

    /// <summary>True when more records matched than the test reads, and it evaluated the first ones as a sample.</summary>
    public bool Sampled { get; set; }

    public int Assertions { get; set; }

    public int FailedAssertions { get; set; }

    /// <summary>The hash of the test's definition when it ran, so a result says whether the test has changed since.</summary>
    public string DefinitionHash { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public string? Error { get; set; }

    /// <summary>The whole result as JSON: every assertion, what it expected and found, and its failing examples.</summary>
    public string Detail { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime CompletedUtc { get; set; }
}

/// <summary>
/// One dimension of a dimension flow in one partition (osdu/docs/reference/concepts/dimension-tables.md): the declaration
/// its last build read with, the field it asked the index for, and how many members and originals it holds now. Its
/// members, originals, builds and changes are rows of <see cref="DeliveryDimensionMember"/>,
/// <see cref="DeliveryDimensionValue"/>, <see cref="DeliveryDimensionRun"/> and <see cref="DeliveryDimensionChange"/>,
/// keyed by <see cref="DimensionId"/>.
/// </summary>
public sealed class DeliveryDimension
{
    /// <summary>The longest dimension name, as a run's payload and a page name it.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The longest path a dimension reads, which a query writes unquoted.</summary>
    public const int MaxPathLength = 512;

    /// <summary>The longest field the index is asked for: a keyword sub-field inside the service's nested form.</summary>
    public const int MaxAggregateByLength = 1100;

    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The ledger identity of the dimension flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The kind the dimension reads, wildcards allowed per segment.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The query narrowing the records read, as the last build ran it; null for every record of the kind.</summary>
    public string? Query { get; set; }

    public string Path { get; set; } = string.Empty;

    /// <summary>How the index stores the field: text, keyword, number, boolean or date. Null until a build has read the templates.</summary>
    public string? FieldIndex { get; set; }

    /// <summary>The nested array the field sits in, or null.</summary>
    public string? NestedPath { get; set; }

    /// <summary>The field as the search's aggregateBy names it.</summary>
    public string? AggregateBy { get; set; }

    /// <summary>Whether one record can hold the field more than once, so members' counts need not be of records.</summary>
    public bool Repeats { get; set; }

    /// <summary>The clean steps, as JSON.</summary>
    public string CleanJson { get; set; } = "[]";

    /// <summary>
    /// Where each key's label is read, as a JSON array of paths: the record the key names, then each record a path before
    /// it refers to. Null when keys are their own values.
    /// </summary>
    public string? LabelJson { get; set; }

    /// <summary>
    /// The attributes each key is read with, as a JSON array of <c>{ "name", "steps", "collect" }</c>: the paths each is read
    /// from the record the key names, or the path of the dimension's own records whose values it collects. Null when
    /// the dimension reads none.
    /// </summary>
    public string? AttributesJson { get; set; }

    /// <summary>
    /// The objects of a nested array each key's records hold, as JSON <c>{ "path", "fields": [{ "name", "path", "keep" }] }</c>:
    /// a row each in the dimension's table, its fields kept as rows of <see cref="DeliveryDimensionElement"/>. Null when the
    /// dimension reads none.
    /// </summary>
    public string? ElementsJson { get; set; }

    /// <summary>
    /// What the last build that settled the field read of each collected attribute, as a JSON array: the attribute's name and
    /// path, how the index stores its field, and the value records holding none of its values were given. The texts its
    /// values stand for are rows of <see cref="DeliveryDimensionCollectedText"/>. Null when the dimension collects nothing.
    /// </summary>
    public string? CollectedJson { get; set; }

    /// <summary>The hash of the dimension's declaration as the last build read it.</summary>
    public string DefinitionHash { get; set; } = string.Empty;

    /// <summary>
    /// The dimension's table in this schema (<c>dim_&lt;dimension&gt;</c>), as the last build wrote it: the
    /// dimension as one table, a row per key and value it collects and a column per attribute. Builds make the table and
    /// widen it as the flow declares more; it is no table of the model, and no migration touches it. Null until a build
    /// has written it.
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>
    /// The column of the dimension's table that holds each key, as the table has it now: named after the property the
    /// dimension's path ends with (with <c>Key</c> at its end where the value's column has that name) unless its document
    /// names it, and <c>key</c> in a table made before dimensions named their columns, which the next build renames. Null
    /// until the table has been made ready.
    /// </summary>
    public string? KeyColumn { get; set; }

    /// <summary>
    /// The column of the dimension's table that holds each key's value, as the table has it now: named after the property
    /// the dimension's label ends with (or after the dimension, when it reads no label) unless its document names it, and
    /// <c>value</c> in a table made before dimensions named their columns. Null until the table has been made ready.
    /// </summary>
    public string? ValueColumn { get; set; }

    /// <summary>The members the dimension holds now.</summary>
    public long Members { get; set; }

    /// <summary>The originals the dimension holds now, those under no member included.</summary>
    public long Originals { get; set; }

    /// <summary>The build that last wrote or read the dimension.</summary>
    public long? LastRunId { get; set; }

    public DateTime? LastBuiltUtc { get; set; }

    /// <summary>
    /// The last build that read the dimension in full and kept the records each key's label and attributes were read
    /// through (<see cref="DeliveryDimensionKeyRecord"/>): what an incremental load reads on from. Null until one has.
    /// </summary>
    public long? LastFullRunId { get; set; }

    /// <summary>When <see cref="LastFullRunId"/> completed.</summary>
    public DateTime? LastFullBuiltUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One build of one dimension: the declaration and the field it read with, how it read (aggregations, ranges, scans), what it
/// found and how complete that is, and what it changed.
/// </summary>
public sealed class DeliveryDimensionRun
{
    /// <summary>The longest unique key a load keeps records by: a few property paths.</summary>
    public const int MaxRecordKeyLength = 1600;

    public short PartitionId { get; set; }

    public long DimensionRunId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The ledger identity of the dimension flow in the partition.</summary>
    public Guid FlowId { get; set; }

    /// <summary>The platform run the build ran in.</summary>
    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>running, completed, failed or cancelled.</summary>
    public string Status { get; set; } = "running";

    /// <summary>
    /// How the run read its dimension: <c>full</c>, every key the index holds, or <c>incremental</c>, the keys of the
    /// records that changed in its window and of the records their labels and attributes were read through.
    /// </summary>
    public string Mode { get; set; } = "full";

    /// <summary>Where an incremental load's window began; null for a full load, and for a window open below.</summary>
    public DateTime? WindowFrom { get; set; }

    /// <summary>
    /// Up to when a completed run has read what changed, its start less the flow's lag: the next incremental load reads on
    /// from the latest of them. Null for a run that has not completed, and for one made before runs kept it.
    /// </summary>
    public DateTime? WindowTo { get; set; }

    /// <summary>The records an incremental load found changed in its window; null for a full load.</summary>
    public long? ChangedRecords { get; set; }

    /// <summary>The keys an incremental load read again; null for a full load.</summary>
    public long? TouchedKeys { get; set; }

    /// <summary>
    /// The unique key a load kept the keys each record holds by (<see cref="DeliveryDimensionRecord"/>): the flow's
    /// <c>incremental.keyColumns</c>, comma-separated. Null for a load that kept none, which no incremental load reads on from.
    /// </summary>
    public string? RecordKey { get; set; }

    public string DefinitionHash { get; set; } = string.Empty;

    /// <summary>The query as the build ran it, its tokens substituted.</summary>
    public string? Query { get; set; }

    public string? AggregateBy { get; set; }

    /// <summary>The kinds the pattern matched and the template each was read against, as JSON.</summary>
    public string? Templates { get; set; }

    /// <summary>The records the query matched; null when that could not be counted.</summary>
    public long? Records { get; set; }

    /// <summary>The records the exact field holds a value for; null when that could not be counted.</summary>
    public long? WithValue { get; set; }

    /// <summary>The records (or objects) whose value is null.</summary>
    public long Nulls { get; set; }

    /// <summary>The records holding only text values longer than the exact field keeps; null when not counted.</summary>
    public long? TooLong { get; set; }

    /// <summary>Values the index holds that are not of the field's type.</summary>
    public long Unreadable { get; set; }

    public long Members { get; set; }

    public long Originals { get; set; }

    /// <summary>Originals under no member: cleaned to nothing, to a clean value too long, or dropped by a map.</summary>
    public long LeftOut { get; set; }

    /// <summary>Originals no query can carry, which their members' filters leave out.</summary>
    public long Unfilterable { get; set; }

    public long MembersAdded { get; set; }

    public long MembersRemoved { get; set; }

    public long MembersRestored { get; set; }

    public long OriginalsAdded { get; set; }

    public long OriginalsRemoved { get; set; }

    /// <summary>Originals now under another member than before, or none where they had one.</summary>
    public long OriginalsMoved { get; set; }

    public long OriginalsRestored { get; set; }

    public int Aggregations { get; set; }

    /// <summary>Ranges an aggregation answered whole.</summary>
    public int Slices { get; set; }

    public int Splits { get; set; }

    public int ScannedSlices { get; set; }

    public int ScanPages { get; set; }

    public long ScannedUnits { get; set; }

    /// <summary>Counts of members' records the build asked the search for.</summary>
    public int CountQueries { get; set; }

    /// <summary>Keys whose label the build read from the record they name.</summary>
    public long Labelled { get; set; }

    /// <summary>Keys the build could not label: a key that names no record, a record the search does not hold, or one without the path.</summary>
    public long Unlabelled { get; set; }

    /// <summary>Searches the build asked to read labels.</summary>
    public int LabelQueries { get; set; }

    /// <summary>What the build had to say, as a JSON array of lines.</summary>
    public string? Notes { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One member of a dimension: a clean value, with how many records hold any of its originals, and the search filter that
/// finds them. A member a build no longer finds is marked removed and kept, so its id stays its own if it comes back. A build
/// writes a row only when something of it changed, so every member the dimension holds now was seen by its last build.
/// </summary>
public sealed class DeliveryDimensionMember
{
    public short PartitionId { get; set; }

    public long MemberId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The clean value, trimmed, compared exactly.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>The records holding any of its originals (or the sum of its originals' counts when <see cref="RecordsExact"/> is false).</summary>
    public long Records { get; set; }

    public bool RecordsExact { get; set; }

    /// <summary>The originals under it.</summary>
    public int Originals { get; set; }

    /// <summary>Its originals no query can carry, which the filter leaves out.</summary>
    public int Unfilterable { get; set; }

    /// <summary>The search filter finding every record holding a filterable original, when it fits one query; null otherwise.</summary>
    public string? Filter { get; set; }

    /// <summary>How many queries the filter takes: 1 when <see cref="Filter"/> holds it, more when its originals pass one query's clauses, 0 with none filterable.</summary>
    public int FilterParts { get; set; }

    /// <summary>The build that first found it. One the dimension holds now was found by the dimension's last build.</summary>
    public long FirstSeenRunId { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    /// <summary>The build that no longer found it, or null while it is there.</summary>
    public long? RemovedRunId { get; set; }

    public DateTime? RemovedUtc { get; set; }
}

/// <summary>
/// One original of a dimension: a value exactly as the index holds it, the member it belongs to or why it belongs to none, and
/// how many records (or objects of a nested array) hold it.
/// </summary>
public sealed class DeliveryDimensionValue
{
    /// <summary>The longest original kept; a longer one is counted and left out by the build.</summary>
    public const int MaxOriginalLength = 1024;

    /// <summary>The longest label kept; a longer one is cut, with a note.</summary>
    public const int MaxLabelLength = 1024;

    /// <summary>The longest filter of one original: the original quoted and escaped, inside the field's nested form.</summary>
    public const int MaxFilterLength = 4000;

    public short PartitionId { get; set; }

    public long ValueId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The value exactly as the index holds it.</summary>
    public string Original { get; set; } = string.Empty;

    /// <summary>SHA-256 of the original's UTF-8 bytes: what an original is unique by, exactly, however long it is.</summary>
    public byte[] OriginalHash { get; set; } = [];

    /// <summary>The member it belongs to, or null when cleaning left it out of every member.</summary>
    public long? MemberId { get; set; }

    /// <summary>Why it belongs to no member: empty, tooLong, dropped or failed; null when it has a member.</summary>
    public string? LeftOut { get; set; }

    /// <summary>What cleaning had to say about it.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// The label the dimension read for it from the record it names (<c>Wellbore A-1</c> for a wellbore's id), before
    /// cleaning; null for a dimension that reads no label, or when the build could not read one.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>The id of the record the label was read from: the record the original names, or the last one a label's steps reached.</summary>
    public string? LabelFrom { get; set; }

    /// <summary>The search filter finding the records holding it, exactly as the index holds it; null when no query can carry it.</summary>
    public string? Filter { get; set; }

    public long Count { get; set; }

    /// <summary>Whether a query can carry it, so its member's filter finds its records.</summary>
    public bool Filterable { get; set; }

    /// <summary>The build that first found it. One the dimension holds now was found by the dimension's last build.</summary>
    public long FirstSeenRunId { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    /// <summary>The build that no longer found it, or null while it is there.</summary>
    public long? RemovedRunId { get; set; }

    public DateTime? RemovedUtc { get; set; }

    /// <summary>The build since which it belongs to its member (or to none).</summary>
    public long MemberSinceRunId { get; set; }
}

/// <summary>
/// One value of one attribute of one original (a key). An attribute read through the record the key names holds one value
/// a key, with the record it was read from; a collected attribute holds every value the key's own records hold, each with how
/// many of them hold it. A row is rewritten when a build reads it otherwise, and removed when a build finds the key
/// without it; a key no build finds any more keeps what its last build read.
/// </summary>
public sealed class DeliveryDimensionAttributeValue
{
    /// <summary>The longest attribute name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest attribute value kept; a longer one is cut by the build, with a note.</summary>
    public const int MaxValueLength = 256;

    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long AttributeValueId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The original the attribute is of (<see cref="DeliveryDimensionValue.ValueId"/>).</summary>
    public long ValueId { get; set; }

    /// <summary>The attribute, by its number (<see cref="DeliveryDimensionAttributeName.AttributeId"/>).</summary>
    public int AttributeId { get; set; }

    /// <summary>The value read, trimmed, compared exactly.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Where it was read: the id of the record the original names, or of the last one the attribute's steps reached; for a
    /// collected attribute, the text the key's records hold, as the index holds it (the first, ordinally, when several are shown
    /// as one value). Null for the value a key holding none is given.
    /// </summary>
    public string? ValueFrom { get; set; }

    /// <summary>
    /// For a collected attribute, how many of the key's records hold the value; null for an attribute read through the record the
    /// key names, which is every record of the key's.
    /// </summary>
    public long? Records { get; set; }
}

/// <summary>
/// One field of one object of a nested array a key's records hold (a curve of a well log): the key, the object's place among
/// the key's (from 1, in the order of the records' ids and then of the objects in each record), the field, and the value
/// read. An object holding none of its fields is one row of its first field with no value, so it is still a row of the
/// dimension's table. A build rewrites the rows of the keys it found; a key no build finds any more keeps what its last
/// build read.
/// </summary>
public sealed class DeliveryDimensionElement
{
    /// <summary>The longest value of a field kept; a longer one is cut by the build, with a note. No index holds it, so it is longer than an attribute's.</summary>
    public const int MaxValueLength = 4000;

    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long ElementValueId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The original the object belongs to (<see cref="DeliveryDimensionValue.ValueId"/>).</summary>
    public long ValueId { get; set; }

    /// <summary>The object's place among the original's, from 1.</summary>
    public int Seq { get; set; }

    /// <summary>The field, by its number (<see cref="DeliveryDimensionAttributeName.AttributeId"/>), as an attribute is named.</summary>
    public int AttributeId { get; set; }

    /// <summary>The value read, as the field keeps it, compared exactly; null for an object holding none of its fields.</summary>
    public string? Value { get; set; }
}

/// <summary>
/// A view a dimension flow declares (osdu/docs/reference/flow/dimension-views.md), as its builds last wrote it: what the
/// module made in its schema, so a build writes only views it recorded and drops only those its flow no longer declares. A
/// view spans every partition its tables hold, so it belongs to no ledger partition; its checks do.
/// </summary>
public sealed class DeliveryDimensionView
{
    /// <summary>The longest name a view takes, as the document gives it.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest description of a view.</summary>
    public const int MaxDescriptionLength = 1000;

    public int ViewId { get; set; }

    /// <summary>The view's name as the document gives it, unique ignoring case among the views of the database.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The view's name in the module's schema (<c>v_dim_&lt;name&gt;</c>).</summary>
    public string ViewName { get; set; } = string.Empty;

    /// <summary>The dimension flow that declares the view, by its name.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The ledger of the build that last wrote the view, which a removal of the view is recorded under.</summary>
    public Guid LedgerId { get; set; }

    public string? Description { get; set; }

    /// <summary>The view as its document declares it: its from dimension, its joins and its columns, as JSON.</summary>
    public string DeclarationJson { get; set; } = string.Empty;

    /// <summary>The statement the view was last written with.</summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of <see cref="Sql"/>, hex: a build that would write the same statement writes nothing. Null when the view is
    /// to be written again: a build renamed a column of a table it reads and dropped it.
    /// </summary>
    public string? SqlHash { get; set; }

    /// <summary>The dimension tables the view reads, its from table first, as a JSON list of names.</summary>
    public string TablesJson { get; set; } = string.Empty;

    /// <summary>The view's columns with their types, expressions and descriptions, as JSON.</summary>
    public string ColumnsJson { get; set; } = string.Empty;

    /// <summary>Why the view is not in the database now, when it is not: what dropped it, and what writes it again.</summary>
    public string? Note { get; set; }

    /// <summary>The platform run whose build last wrote the view.</summary>
    public Guid? WrittenRunId { get; set; }

    /// <summary>Who started that run.</summary>
    public string WrittenBy { get; set; } = string.Empty;

    public DateTime WrittenUtc { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// What a build's check of a view found in one partition (osdu/docs/reference/flow/dimension-views.md): the rows, what each
/// join found, and the values each conversion could not read, with examples; or why the view could not be read. A view
/// keeps its newest checks of each partition.
/// </summary>
public sealed class DeliveryDimensionViewCheck
{
    /// <summary>The most checks a view keeps of each partition.</summary>
    public const int Kept = 50;

    public short PartitionId { get; set; }

    /// <summary>The row's own number.</summary>
    public long CheckId { get; set; }

    /// <summary>The view checked (<see cref="DeliveryDimensionView.ViewId"/>).</summary>
    public int ViewId { get; set; }

    /// <summary>The platform run whose build checked it.</summary>
    public Guid? RunId { get; set; }

    /// <summary><c>passed</c>, or <c>failed</c> when the view could not be read.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The rows the view holds of the partition.</summary>
    public long Rows { get; set; }

    /// <summary>What each join found: the rows that found their row, those whose value found none, and some of those values, as JSON.</summary>
    public string? JoinsJson { get; set; }

    /// <summary>The values each column holds, and the values each converted column could not read with examples, as JSON.</summary>
    public string? ColumnsJson { get; set; }

    /// <summary>Why the view could not be read, redacted.</summary>
    public string? Error { get; set; }

    public DateTime CheckedUtc { get; set; }

    /// <summary>How long the check read the view.</summary>
    public int DurationMs { get; set; }
}

/// <summary>
/// One text a dimension's records hold at a collected attribute's path, as the last build that settled the field read it:
/// the text exactly as the index holds it, the value it is shown as (several texts shown alike are one value), and the
/// records holding it. A search picking a collected value asks for every text shown as it; a pick of the value for what is
/// not read asks for the records holding none of them. A build replaces the dimension's rows with what it read.
/// </summary>
public sealed class DeliveryDimensionCollectedText
{
    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long TextId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The collected attribute, by its number (<see cref="DeliveryDimensionAttributeName.AttributeId"/>).</summary>
    public int AttributeId { get; set; }

    /// <summary>SHA-256 of the text's UTF-8 bytes, which the row is unique by, since a text may be longer than a key holds.</summary>
    public byte[] TextHash { get; set; } = [];

    /// <summary>The text exactly as the index holds it, compared exactly.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The value it is shown as, compared exactly.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>The records holding it.</summary>
    public long Records { get; set; }
}

/// <summary>
/// An attribute of a dimension, under the number its values are kept and joined by: its name as the dimension declares
/// it, whether it is collected from the dimension's own records, and its place among the attributes the dimension declares
/// now, which is the order of its columns as the dimension's table is read. An attribute the dimension no longer
/// declares keeps its number and has no place.
/// </summary>
public sealed class DeliveryDimensionAttributeName
{
    public short PartitionId { get; set; }

    public int AttributeId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The attribute's name, as the dimension declares it, compared exactly.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Its place among the attributes the dimension declares now, from 1. Null for an attribute the dimension no longer declares.</summary>
    public short? Ordinal { get; set; }

    /// <summary>Whether its values are collected from the dimension's own records, so a key holds several.</summary>
    public bool Collected { get; set; }
}

/// <summary>
/// A record one key's label and attributes were read through: the record the key names and every record a step reached or
/// looked for, found or not. A build rewrites them for every key it reads; an incremental load finds the keys to read
/// again because a record changed by looking the record up here.
/// </summary>
public sealed class DeliveryDimensionKeyRecord
{
    /// <summary>The longest entity type a record id names.</summary>
    public const int MaxEntityTypeLength = 256;

    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long KeyRecordId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>The original read through the record (<see cref="DeliveryDimensionValue.ValueId"/>).</summary>
    public long ValueId { get; set; }

    /// <summary>SHA-256 of the record's id without its version, as UTF-8: what a changed record is looked up by.</summary>
    public byte[] RecordHash { get; set; } = [];

    /// <summary>The entity type the id names (<c>master-data--Wellbore</c>): the kinds an incremental load asks the search what changed in.</summary>
    public string EntityType { get; set; } = string.Empty;
}

/// <summary>
/// A key one record of a dimension held when a load last read the record, the record named by its unique key (the flow's
/// <c>incremental.keyColumns</c>): what an incremental load reads again when the record changes, besides the keys it holds
/// now. A full load of a flow that loads incrementally rewrites them all; an incremental load rewrites those of the records
/// it found changed. A record that left the index keeps its rows until the next full load.
/// </summary>
public sealed class DeliveryDimensionRecord
{
    public short PartitionId { get; set; }

    /// <summary>The row's own number, which the table is stored in the order of.</summary>
    public long DimensionRecordId { get; set; }

    public int DimensionId { get; set; }

    /// <summary>SHA-256 of the record's unique key, its columns' values joined, as UTF-8.</summary>
    public byte[] RecordHash { get; set; } = [];

    /// <summary>The original the record held (<see cref="DeliveryDimensionValue.ValueId"/>).</summary>
    public long ValueId { get; set; }
}

/// <summary>
/// A change a build made to one original: it arrived (after the dimension's first build), left, came back, or moved from one
/// member to another. The first build's originals are its arrivals, told by their first build.
/// </summary>
public sealed class DeliveryDimensionChange
{
    public short PartitionId { get; set; }

    public long ChangeId { get; set; }

    public int DimensionId { get; set; }

    public long DimensionRunId { get; set; }

    public long ValueId { get; set; }

    /// <summary>added, removed, moved or restored.</summary>
    public string Change { get; set; } = string.Empty;

    public long? FromMemberId { get; set; }

    public long? ToMemberId { get; set; }

    public DateTime ChangedUtc { get; set; }
}

/// <summary>
/// The single row that says which version of the osdu schema a database holds: the module version and the last migration
/// applied, when and by whom, and the oldest SQLFlow catalog migration this schema works with. Written by the module
/// database's migrate step in the same connection as the migrations, and read by every host at startup.
/// </summary>
public sealed class OsduSchemaVersion
{
    /// <summary>Always 1: the table holds exactly one row.</summary>
    public int Id { get; set; } = 1;

    public string ModuleVersion { get; set; } = string.Empty;

    public string LastMigration { get; set; } = string.Empty;

    public DateTime AppliedUtc { get; set; }

    /// <summary>The host and actor that ran the migrate.</summary>
    public string AppliedBy { get; set; } = string.Empty;

    /// <summary>The oldest SQLFlow catalog migration the schema needs beside it.</summary>
    public string MinimumCatalogMigration { get; set; } = string.Empty;
}

/// <summary>
/// What a central configuration property may be called and hold, and how many of them one run carries. The bounds are
/// here rather than at each caller so the store, the API, the CLI and the run payload all refuse the same things.
/// </summary>
public static partial class DeliveryConfigNames
{
    /// <summary>The longest accepted property name, which is an environment variable name.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest accepted value. A value is an identifier, a URL or a reference, never a document.</summary>
    public const int MaxValueLength = 1000;

    /// <summary>The longest accepted description of what a property is for: the width of its column.</summary>
    public const int MaxDescriptionLength = 400;

    /// <summary>The most properties one run carries, which bounds what a queued run writes into its payload.</summary>
    public const int MaxPerRun = 64;

    /// <summary>Whether <paramref name="name"/> can name a property: an environment variable name, which is what a flow spells it as.</summary>
    public static bool IsName(string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && NamePattern().IsMatch(name);

    /// <summary>
    /// Whether <paramref name="value"/> can be held as a property: non-empty, within the bound and free of the control
    /// characters that would make a value unprintable in a log line or a run payload.
    /// </summary>
    public static bool IsValue(string? value)
        => !string.IsNullOrEmpty(value) && value.Length <= MaxValueLength && !value.Any(char.IsControl);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();
}

/// <summary>
/// A partition the ledger keeps rows under, and the number that stands for it in every ledger key
/// (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and partitions). A partition's name is a data-partition-id
/// of up to 200 characters; carried in every key of the record table it would push the source file index past SQL
/// Server's 1700-byte limit and widen every index of the largest tables, so the ledger keys by this number and names
/// the partition here. A row is added the first time a ledger of the partition is registered, and is never renumbered
/// or removed: every ledger row names its partition by it.
/// </summary>
public sealed class DeliveryLedgerPartition
{
    /// <summary>The number every ledger key starts with. <see cref="DeliveryModel.UnassignedPartition"/> (0) has no row.</summary>
    public short PartitionId { get; set; }

    /// <summary>The data-partition-id, as runs name it; compared regardless of case, so a partition has one number.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One ledger: the rows of one flow (or interface of a source) in one partition, under one ledger identity
/// (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and partitions). The engine registers a ledger when a run
/// binds its flow, before it writes a row, with the partition the run delivers to: the one the flow names, the
/// registry's, or the one its <c>data-partition-id</c> header resolves to. From then on the ledger belongs to that
/// partition. Every ledger row carries the partition in its key, written from here, so which partition a record, a
/// submission or an audit entry belongs to is known from the ledger alone.
/// </summary>
/// <remarks>
/// A ledger the upgrade to partition keys could not place (no interface named its partition and its records carry no OSDU
/// id, or ids of more than one partition) is <see cref="DeliveryModel.UnassignedPartition"/> until its next run adopts it,
/// which refuses a ledger holding records delivered to another partition than the run's.
/// </remarks>
public sealed class DeliveryLedger
{
    /// <summary>The widest flow name a ledger keeps.</summary>
    public const int MaxFlowNameLength = 200;

    /// <summary>The partition the ledger belongs to, or <see cref="DeliveryModel.UnassignedPartition"/>.</summary>
    public short PartitionId { get; set; }

    /// <summary>The ledger identity every row of the ledger carries.</summary>
    public Guid FlowId { get; set; }

    /// <summary>delivery, retrieval, assertion, dimension or inventory: the kind of flow whose ledger this is.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The flow (for a source with interfaces, the source) the ledger belongs to.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The interface of a source, or empty for a flow in the single form.</summary>
    public string Interface { get; set; } = string.Empty;

    /// <summary>The ledger's name, as pages show it: <c>&lt;ledger&gt;@&lt;partition&gt;</c> for a partition keeping a ledger of its own.</summary>
    public string LedgerName { get; set; } = string.Empty;

    public DateTime RegisteredUtc { get; set; }
}

/// <summary>
/// An OSDU partition registered with the catalog (osdu/docs/reference/concepts/partitions.md, The partition registry): the
/// partitions every run, cache and ledger of the module is keyed by. A flow that names no partitions serves every
/// registered one, so the same documents deploy to every environment; a run that names none runs in the one marked the
/// default. A flow that names its partitions serves those of them that are registered.
/// </summary>
/// <remarks>
/// Removing a partition deletes nothing kept under it: its caches, ledgers and runs stay as they are, and no run can target
/// it until it is registered again. At most one partition is the default, which a filtered unique index keeps.
/// </remarks>
public sealed class DeliveryPartition
{
    /// <summary>The widest description a partition keeps.</summary>
    public const int MaxDescriptionLength = 400;

    /// <summary>The data-partition-id, as runs name it and caches and ledgers are keyed by it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the partition is for (the environment, the platform), in the words of whoever registered it.</summary>
    public string? Description { get; set; }

    /// <summary>True for the one partition a run that names none runs in.</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// True while the control plane's configuration (<c>Osdu:Partitions</c>) names the partition: the control plane
    /// registered it from there, and only the configuration describes it, makes it the default or takes it out. A partition
    /// the configuration stops naming stays registered with this cleared.
    /// </summary>
    public bool IsConfigured { get; set; }

    public DateTime CreatedUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>
/// One property of the central configuration: a value the control plane supplies to the runs it queues, so a flow that
/// names <c>${env:NAME}</c> resolves it from here rather than from whatever the node that picks the run up happens to
/// hold. A property is set once for the whole control plane (<see cref="RepoId"/> null) and may be set again for one
/// repository, which is an estate: the repository's value wins for the flows that repository holds. Either may also be set
/// for one OSDU partition (<see cref="Partition"/>), and a run of a flow bound to that partition resolves with it first.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Value"/> is a non-secret value (an entitlements group, a legal tag, a base URL) or a
/// <c>${env:NAME}</c> or <c>${keyvault:vault/secret}</c> reference, which travels unresolved and is resolved on the node.
/// A literal secret here is a defect: this row, and the run payload it is carried in, are ordinary catalog content.
/// </para>
/// <para>
/// For a run bound to partition P the value of a name is taken from the first scope that sets it: the repository's for P,
/// the control plane's for P, the repository's, the control plane's (osdu/docs/reference/concepts/environment-variables.md,
/// Where a reference resolves from). A value set for a partition always wins over one that is not, so an estate-wide
/// endpoint never sends a run of one partition to another's platform.
/// </para>
/// </remarks>
public sealed class DeliveryConfigProperty
{
    public Guid Id { get; set; }

    /// <summary>The repository whose flows this value applies to, or null for the control plane's own value.</summary>
    public Guid? RepoId { get; set; }

    /// <summary>
    /// The OSDU partition whose runs this value applies to, by its data-partition-id, or null for a value that applies
    /// whatever the partition.
    /// </summary>
    public string? Partition { get; set; }

    /// <summary>The reference name a flow spells as <c>${env:NAME}</c>, held as written.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The value, or a reference the node resolves.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>What this property is for, shown wherever it is listed.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// True while the control plane's configuration (<c>Osdu:Partitions:&lt;partition&gt;:Properties</c>) sets this value: the
    /// control plane wrote it from there, and only the configuration changes or removes it. A value the configuration stops
    /// setting stays with this cleared.
    /// </summary>
    public bool IsConfigured { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Who set it last.</summary>
    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>The EF model of the delivery ledger, in the <c>osdu</c> schema.</summary>
public static class DeliveryModel
{
    public const string SchemaName = "osdu";

    /// <summary>
    /// The partition number of a ledger the upgrade to partition keys could not place, until its next run adopts it
    /// (<see cref="DeliveryLedger"/>). No <see cref="DeliveryLedgerPartition"/> row carries it.
    /// </summary>
    public const short UnassignedPartition = 0;

    /// <summary>The widest data-partition-id, as every table that names a partition keeps it.</summary>
    public const int MaxPartitionLength = 200;

    /// <summary>The longest search term key: its table and its column
    /// (osdu/docs/reference/concepts/search-terms.md).</summary>
    public const int SearchTermKeyLength = 600;

    /// <summary>
    /// The collation of the columns that key on an OSDU record id. OSDU ids are case-sensitive:
    /// <c>...UnitOfMeasure:ft</c> (the foot) and <c>...UnitOfMeasure:fT</c> (the femtotesla) are two records, and SQL
    /// Server's default collation folds case, which would make them one key.
    /// </summary>
    public const string OsduIdCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// The collation a search of text the ledger keeps exactly asks it in: case folded, so a person finds a clean value or an
    /// original by the spelling they know, whatever its case.
    /// </summary>
    public const string SearchCollation = "Latin1_General_100_CI_AS";

    /// <summary>
    /// The longest ingestion file name a record's origin holds. It keeps the (flow, file, row) index key under SQL Server's
    /// 1700-byte limit; a longer file name is refused when the source is opened, naming the file and this limit.
    /// </summary>
    public const int MaxSourceFileNameLength = 800;

    /// <summary>
    /// The longest OSDU id a record holds, as its id, its claim and the id it waits for. A render that gives a longer id holds
    /// the record, naming this limit, rather than storing an id cut short.
    /// </summary>
    public const int MaxTargetIdLength = 500;

    /// <summary>The longest lease token, and the longest worker name a lease records as its owner.</summary>
    public const int MaxLeaseTokenLength = 200;

    /// <summary>The longest slot a route names an artifact by within its unit.</summary>
    public const int MaxArtifactSlotLength = 200;

    /// <summary>The longest locator an artifact keeps (a landing-zone path, a list of row keys in runs).</summary>
    public const int MaxArtifactLocatorLength = 1000;

    /// <summary>The longest note an artifact keeps of why it was kept or why its undo failed.</summary>
    public const int MaxArtifactNoteLength = 1000;

    /// <summary>
    /// The filtered index of the artifacts still open (an intent, a pending one of an open or abandoned unit, one due or failed
    /// to undo), named because the sweep reaches it by name and repeats its filter.
    /// </summary>
    public const string OpenArtifactIndex = "IX_Artifact_Open";

    /// <summary>The longest kind an inventory reads or a record of it carries.</summary>
    public const int MaxInventoryKindLength = 300;

    /// <summary>The longest query an inventory narrows a search with.</summary>
    public const int MaxInventoryQueryLength = 4000;

    /// <summary>The longest identity an inventory keeps as a record's creator or last modifier.</summary>
    public const int MaxInventoryUserLength = 256;

    /// <summary>The longest line an inventory record keeps of what its finding rests on.</summary>
    public const int MaxInventoryDetailLength = 1000;

    /// <summary>The longest text a selection keeps of the values an id holds at the properties its conditions ask.</summary>
    public const int MaxSelectionValuesLength = 4000;

    /// <summary>The length of a selection's conditions key: a SHA-256 in hex.</summary>
    public const int SelectionKeyLength = 64;

    /// <summary>
    /// The longest identity token, and the longest display value beside it: 200 characters keep the
    /// (token, flow, key) primary key well under SQL Server's 1700-byte limit for a nonclustered index key, and a
    /// longer value is still found by its start. The ledger's <c>RecordIdentityLimits.MaxTokenLength</c> is this.
    /// </summary>
    public const int MaxIdentityTokenLength = 200;

    /// <summary>
    /// The filtered index of the blocked records not sorted into a problem yet, named because a statement reaches it by
    /// name and repeats its filter: the problem backfill's.
    /// </summary>
    public const string UnsortedProblemIndex = "IX_Record_Unsorted";

    /// <summary>
    /// The filtered index of the parked records (held, failed, removed or reverted) by whether they are blocked, named because
    /// a statement reaches it by name and repeats its filter: a flow's statistics count its blocked records, and its held ones
    /// nothing moves, from it.
    /// </summary>
    public const string ParkedIndex = "IX_Record_Parked";

    /// <param name="modelBuilder">The model being built.</param>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // The ledger's directory: the partitions it keys by, and the ledger of every flow in one of them.
        modelBuilder.Entity<DeliveryLedgerPartition>(e =>
        {
            e.ToTable("LedgerPartition", SchemaName);
            e.HasKey(p => p.PartitionId);
            e.Property(p => p.PartitionId).ValueGeneratedOnAdd();
            e.Property(p => p.Name).HasMaxLength(MaxPartitionLength).IsRequired();
            // A partition has one number whatever the case it is written in, as the registry compares names.
            e.HasIndex(p => p.Name).IsUnique();
        });

        modelBuilder.Entity<DeliveryLedger>(e =>
        {
            e.ToTable("Ledger", SchemaName);
            // A partition's ledgers together, as every ledger table keeps its rows.
            e.HasKey(l => new { l.PartitionId, l.FlowId });
            e.Property(l => l.Kind).HasMaxLength(16).IsRequired();
            e.Property(l => l.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(l => l.Interface).HasMaxLength(DeliveryInterface.MaxInterfaceLength).IsRequired();
            e.Property(l => l.LedgerName).HasMaxLength(200).IsRequired();
            // A ledger belongs to one partition: its identity is unique on its own, and every read of it starts here.
            e.HasIndex(l => l.FlowId).IsUnique();
        });

        // Every ledger table keys by partition first, so one partition's rows are kept together and every read of a
        // flow, a partition or a listing seeks a range of one partition. A row found by an id unique on its own (a
        // submission, an activity, a retrieval, a lease, a work batch) keeps that id unique in an index of its own, for
        // the reads that hold nothing else.
        modelBuilder.Entity<DeliverySubmission>(e =>
        {
            e.ToTable("Submission", SchemaName);
            e.HasKey(s => new { s.PartitionId, s.SubmissionId });
            e.Property(s => s.FlowName).HasMaxLength(200).IsRequired();
            e.Property(s => s.MappingReference).HasMaxLength(200).IsRequired();
            e.Property(s => s.RenderContext).IsRequired();
            e.Property(s => s.WorkLocation).HasMaxLength(2000);
            e.Property(s => s.ParametersJson).IsRequired();
            e.Property(s => s.Status).HasMaxLength(16).IsRequired();
            e.Property(s => s.Error).HasMaxLength(4000);
            e.Property(s => s.Kind).HasMaxLength(16).IsRequired();
            e.Property(s => s.SourceConnection).HasMaxLength(400).IsRequired();
            e.Property(s => s.SourceObject).HasMaxLength(400).IsRequired();
            // A submission named by its id alone: a run's payload, a link, a record's last submission.
            e.HasIndex(s => s.SubmissionId).IsUnique();
            e.HasIndex(s => new { s.PartitionId, s.FlowId, s.ReceivedUtc });
            e.HasIndex(s => new { s.PartitionId, s.FlowId, s.Status });
            // The flow's submission listing filtered by kind.
            e.HasIndex(s => new { s.PartitionId, s.FlowId, s.Kind, s.ReceivedUtc });
            // The submissions of a partition, most recent first, across its flows.
            e.HasIndex(s => new { s.PartitionId, s.ReceivedUtc });
            // A run page links the run to the plan it coordinated.
            e.HasIndex(s => s.RunId);
        });

        modelBuilder.Entity<DeliveryRecord>(e =>
        {
            e.ToTable("Record", SchemaName);
            // A record is one flow's in one partition: two flows reading the same source row keep two records, and so does
            // one flow delivering it to two partitions, under the same delivery key. A flow's key-ordered walks (a
            // removal's key list, a key-scoped plan) read the key in order within the partition.
            e.HasKey(r => new { r.PartitionId, r.FlowId, r.DeliveryKey });
            e.Property(r => r.SourceKey).HasMaxLength(400).IsRequired();
            e.Property(r => r.SourceKeyJson).HasMaxLength(2000);
            e.Property(r => r.Label).HasMaxLength(400);
            e.Property(r => r.MappingName).HasMaxLength(200).IsRequired();
            e.Property(r => r.SourceFingerprint).HasMaxLength(200);
            e.Property(r => r.SourceFileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(r => r.MetadataHash).HasMaxLength(64);
            e.Property(r => r.PayloadHash).HasMaxLength(64);
            e.Property(r => r.TargetId).HasMaxLength(MaxTargetIdLength);
            OptionalOsduId(e.Property(r => r.ClaimedTargetId)).HasMaxLength(MaxTargetIdLength);
            OptionalOsduId(e.Property(r => r.WaitingFor)).HasMaxLength(MaxTargetIdLength);
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.LastVerifyOutcome).HasMaxLength(16);
            e.Property(r => r.LeaseOwner).HasMaxLength(200);
            e.Property(r => r.LastError).HasMaxLength(2000);
            e.Property(r => r.PendingSourceFingerprint).HasMaxLength(200);
            e.Property(r => r.PendingSourceFileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(r => r.PendingMetadataHash).HasMaxLength(64);
            e.Property(r => r.PendingPayloadHash).HasMaxLength(64);
            e.Property(r => r.PendingPayloadLocation).HasMaxLength(2000);
            e.Property(r => r.PendingDocumentRef).HasMaxLength(64);
            e.Property(r => r.ValidationOutcome).HasMaxLength(16);
            e.Property(r => r.AcceptedMetadataHash).HasMaxLength(64);

            // Worker and intake paths. The worker's reads (the claim, what is due next, the settled submissions with due
            // work) are answered from these two alone, for a flow and for one submission of it.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Status, r.NextAttemptUtc })
                .IncludeProperties(r => new { r.LastSubmissionId, r.UpdatedUtc, r.PendingDocumentRef });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastSubmissionId, r.Status, r.NextAttemptUtc })
                .IncludeProperties(r => r.UpdatedUtc);
            // The rollout walks one set's records in key order, then flow order; a set is one partition's, and the filtered
            // index keeps untagged records out of it.
            e.HasIndex(r => new { r.CacheSetId, r.DeliveryKey, r.FlowId }).HasFilter("[CacheSetId] IS NOT NULL");
            // A cache version that brings in what held records missed finds them by the set their held render read.
            e.HasIndex(r => new { r.HeldCacheSetId, r.Status }).HasFilter("[HeldCacheSetId] IS NOT NULL");
            // One OSDU record, one flow: the database refuses a second flow's claim on an id, whatever races the intakes
            // run. An id names its partition, so this stays unique across every partition.
            e.HasIndex(r => r.ClaimedTargetId).IsUnique().HasFilter("[ClaimedTargetId] IS NOT NULL");
            e.HasIndex(r => new { r.LastSubmissionId, r.WorkBatch });
            // The records a lease holds: the ones a checkpoint or a close hands back, and the ones a submission waits on.
            e.HasIndex(r => r.LeaseOwner);
            // A submission's records, most recent first, read in index order however many the submission holds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastSubmissionId, r.UpdatedUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastVerifiedUtc });

            // GUI: one flow's prefix search and recency listings, all answered from an index.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Label });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.SourceKey });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.TargetId });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.UpdatedUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Status, r.UpdatedUtc });

            // The parked records (held, failed, removed or reverted) by whether they are blocked: what a flow's statistics
            // count as blocked, and as held with nothing to move them (unblocked, unasked, with no document), each a seek of
            // a range that holds the parked records alone, however many records the ledger holds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Blocked, r.Status }, ParkedIndex)
                .IncludeProperties(r => new { r.PlanRequestedUtc, r.PendingDocumentRef })
                .HasFilter("[Status] IN (N'held', N'failed', N'deleted', N'reverted')");
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastDeliveredUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.LastVerifyOutcome });

            // The lookup across the flows of a partition (the search box, read in the partition the workbench works in): a
            // delivery key, an OSDU id, a source key, a label prefix or an ingestion file name answers from these.
            e.HasIndex(r => new { r.PartitionId, r.DeliveryKey });
            e.HasIndex(r => new { r.PartitionId, r.TargetId });
            e.HasIndex(r => new { r.PartitionId, r.SourceKey });
            e.HasIndex(r => new { r.PartitionId, r.Label });
            e.HasIndex(r => new { r.PartitionId, r.SourceFileName });

            // The Records page with nothing typed: the most recently updated records across the flows of a partition, and
            // the most recent of one custody state, read from the end of an index instead of by ordering the whole ledger.
            e.HasIndex(r => new { r.PartitionId, r.UpdatedUtc });
            e.HasIndex(r => new { r.PartitionId, r.Status, r.UpdatedUtc });

            // Which records came from this file, inside a flow, in milliseconds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.SourceFileName, r.SourceRowNumber });

            // The records the ledger asked to be planned again, paged by the planner each run: the filter keeps the
            // index as small as the backlog.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.PlanRequestedUtc }).HasFilter("[PlanRequestedUtc] IS NOT NULL");

            // The records waiting for an id, released when the record holding that id lands in the same partition: the
            // filter keeps the index as small as what is waiting, so the release every settle runs costs a seek.
            e.HasIndex(r => new { r.PartitionId, r.WaitingFor }).HasFilter("[WaitingFor] IS NOT NULL");

            // The blocked records by the problem that keeps them so: a flow's problems counted with their held and failed
            // records, their newest and oldest, and their files; a problem's records listed newest first and released. The
            // filter keeps the index as small as what is blocked, however many records the ledger holds.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.ProblemHash, r.UpdatedUtc })
                .IncludeProperties(r => new { r.Status, r.PendingSourceFileName })
                .HasFilter("[ProblemHash] IS NOT NULL");

            // The blocked records not sorted into a problem yet: held or failed before the ledger kept problems, or by a
            // completion an older build appended. The control plane's backfill reads and sorts them from here, so the index
            // is empty but for that backlog. A query reaches it only by repeating the filter, which the backfill names.
            e.HasIndex(r => new { r.PartitionId, r.FlowId }, UnsortedProblemIndex)
                .HasFilter("[ProblemHash] IS NULL AND [Blocked]=(1) AND ([Status] IN (N'held', N'failed'))");

            // A flow's records by what the last check of their documents came to: counted for the flow's page, and the
            // invalid or unverified ones listed. The filter leaves out the records no check has reached.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.ValidationOutcome, r.UpdatedUtc })
                .HasFilter("[ValidationOutcome] IS NOT NULL");

            // A flow's records whose last checked document failed an assertion of its mapping: counted for the flow's page and
            // listed. The filter leaves out the records that met every assertion, or whose mapping states none.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.AssertionFailures, r.UpdatedUtc })
                .HasFilter("[AssertionFailures]>(0)");
        });

        modelBuilder.Entity<DeliveryRecordIdentity>(e =>
        {
            e.ToTable("RecordIdentity", SchemaName);
            // One row per token per record: the partition, then the token, so the row is written and read by what it
            // identifies within the partition the lookup reads.
            e.HasKey(i => new { i.PartitionId, i.Token, i.FlowId, i.DeliveryKey });
            e.Property(i => i.Token).HasMaxLength(MaxIdentityTokenLength).IsRequired();
            e.Property(i => i.Display).HasMaxLength(MaxIdentityTokenLength).IsRequired();
            e.Property(i => i.Kind).HasMaxLength(16).IsRequired();

            // The lookup: any identifier an operator holds, as a prefix, across the flows of a partition. The primary key
            // answers it, and the record it names is read by joining to the record's own key.
            //
            // The other direction: a record's own tokens, to rewrite or delete them when its identity changes.
            e.HasIndex(i => new { i.PartitionId, i.FlowId, i.DeliveryKey });

            // The same lookup narrowed to one flow (the Records page's flow filter): its candidates are that flow's own
            // tokens, so a prefix many other flows share cannot use up the candidate bound before this flow is reached.
            e.HasIndex(i => new { i.PartitionId, i.FlowId, i.Token });
        });

        modelBuilder.Entity<DeliveryAttempt>(e =>
        {
            e.ToTable("Attempt", SchemaName);
            // Append-only: the ever-increasing id within each partition, so every drain appends at the end of its range.
            e.HasKey(a => new { a.PartitionId, a.AttemptId });
            e.Property(a => a.AttemptId).ValueGeneratedOnAdd();
            e.Property(a => a.Worker).HasMaxLength(200).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
            e.Property(a => a.Phase).HasMaxLength(32).IsRequired();
            e.Property(a => a.MetadataHash).HasMaxLength(64);
            e.Property(a => a.PayloadHash).HasMaxLength(64);
            e.Property(a => a.Error).HasMaxLength(2000);
            e.Property(a => a.SourceFileName).HasMaxLength(MaxSourceFileNameLength);
            // A record's timeline, and the later attempt pruning looks for beside each one it removes.
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.DeliveryKey, a.StartedUtc });
            // Pruning by age, across every partition: retention is the estate's, not a partition's.
            e.HasIndex(a => a.StartedUtc);
            // A submission's attempts, and the records they settled by outcome, counted when the submission closes: the
            // count reads this index alone, however many attempts a full plan wrote.
            e.HasIndex(a => new { a.SubmissionId, a.Outcome, a.Phase }).IncludeProperties(a => a.DeliveryKey);
            // A run's records: the listing's run filter seeks the run and joins on the record's key without reading the attempt.
            e.HasIndex(a => new { a.RunId, a.PartitionId, a.FlowId, a.DeliveryKey });
        });

        modelBuilder.Entity<DeliveryWorkBatch>(e =>
        {
            e.ToTable("WorkBatch", SchemaName);
            e.HasKey(b => new { b.PartitionId, b.SubmissionId, b.Index });
            e.Property(b => b.Location).HasMaxLength(2000).IsRequired();
            e.Property(b => b.Status).HasMaxLength(16).IsRequired();
            e.Property(b => b.LeaseOwner).HasMaxLength(200);
            e.Property(b => b.Error).HasMaxLength(2000);
            // The claim: the oldest queued batch of a flow (or a submission).
            e.HasIndex(b => new { b.PartitionId, b.FlowId, b.Status, b.CreatedUtc });
            // A batch named by its submission and place, and a submission's batches in order, counted by status: the batch's
            // identity is unique on its own.
            e.HasIndex(b => new { b.SubmissionId, b.Index }).IsUnique().IncludeProperties(b => b.Status);
        });

        modelBuilder.Entity<DeliveryLease>(e =>
        {
            e.ToTable("Lease", SchemaName);
            e.HasKey(l => new { l.PartitionId, l.Token });
            e.Property(l => l.Token).HasMaxLength(MaxLeaseTokenLength);
            e.Property(l => l.Owner).HasMaxLength(MaxLeaseTokenLength).IsRequired();
            // A lease named by its token: a renewal, a checkpoint, a close, a record's holder.
            e.HasIndex(l => l.Token).IsUnique();
            // The sweep: a flow's leases that ran out. A submission's leases: what its run waits on.
            e.HasIndex(l => new { l.PartitionId, l.FlowId, l.ExpiresUtc });
            e.HasIndex(l => new { l.SubmissionId, l.ExpiresUtc });
        });

        modelBuilder.Entity<DeliveryRecordEvent>(e =>
        {
            e.ToTable("RecordEvent", SchemaName);
            // Clustered on an ever-increasing id within each partition: every worker only appends, at the end of the
            // partition's range.
            e.HasKey(v => new { v.PartitionId, v.EventId });
            e.Property(v => v.EventId).ValueGeneratedOnAdd();
            e.Property(v => v.LeaseToken).HasMaxLength(MaxLeaseTokenLength).IsRequired();
            e.Property(v => v.Kind).HasMaxLength(16).IsRequired();
            e.Property(v => v.Status).HasMaxLength(16);
            e.Property(v => v.Error).HasMaxLength(2000);
            e.Property(v => v.ValidationOutcome).HasMaxLength(16);
            e.Property(v => v.TargetId).HasMaxLength(500);
            e.Property(v => v.ClaimDocumentRef).HasMaxLength(64);
            e.Property(v => v.ClaimSourceFingerprint).HasMaxLength(200);
            e.Property(v => v.ClaimSourceFileName).HasMaxLength(MaxSourceFileNameLength);
            e.Property(v => v.ClaimMetadataHash).HasMaxLength(64);
            e.Property(v => v.ClaimPayloadHash).HasMaxLength(64);
            // What a lease applies: its events, record by record, the latest last.
            e.HasIndex(v => new { v.LeaseToken, v.FlowId, v.DeliveryKey, v.EventId });
            // A flow's recovery: its events old enough that only a lease that is gone can have left them.
            e.HasIndex(v => new { v.PartitionId, v.FlowId, v.AtUtc }).IncludeProperties(v => v.LeaseToken);
        });

        modelBuilder.Entity<DeliverySourceWatermark>(e =>
        {
            e.ToTable("SourceWatermark", SchemaName);
            e.HasKey(w => new { w.PartitionId, w.FlowId, w.Scope });
            e.Property(w => w.Scope).HasMaxLength(400);
            e.Property(w => w.ContextHash).HasMaxLength(64);
        });

        modelBuilder.Entity<DeliveryActivity>(e =>
        {
            e.ToTable("Activity", SchemaName);
            e.HasKey(a => new { a.PartitionId, a.ActivityId });
            e.Property(a => a.ActivityId).ValueGeneratedOnAdd();
            e.Property(a => a.FlowName).HasMaxLength(200).IsRequired();
            e.Property(a => a.Kind).HasMaxLength(32).IsRequired();
            e.Property(a => a.Actor).HasMaxLength(200).IsRequired();
            e.Property(a => a.Outcome).HasMaxLength(16).IsRequired();
            e.Property(a => a.Summary).HasMaxLength(2000);
            // An entry named by its id: the audit trail's detail, and the run that completes it.
            e.HasIndex(a => a.ActivityId).IsUnique();
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.StartedUtc });
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.DeliveryKey, a.StartedUtc });
            e.HasIndex(a => a.SubmissionId);
            e.HasIndex(a => a.RunId);
            // The audit trail of a partition, newest first, and filtered by action or by actor.
            e.HasIndex(a => new { a.PartitionId, a.Kind, a.StartedUtc });
            e.HasIndex(a => new { a.PartitionId, a.Actor, a.StartedUtc });
            e.HasIndex(a => new { a.PartitionId, a.StartedUtc });
            // The trail as it opens, without the idle runs, and the count of those it leaves out: a partition whose schedules
            // fire every hour holds mostly idle runs, so both are a seek rather than a walk past them.
            e.HasIndex(a => new { a.PartitionId, a.Idle, a.StartedUtc });
        });

        modelBuilder.Entity<DeliveryActivityRecord>(e =>
        {
            e.ToTable("ActivityRecord", SchemaName);
            // A record's requests are one seek of its key, in the order the trail numbered them.
            e.HasKey(a => new { a.PartitionId, a.FlowId, a.DeliveryKey, a.ActivityId });
        });

        modelBuilder.Entity<DeliveryPurgedRecord>(e =>
        {
            e.ToTable("PurgedRecord", SchemaName);
            e.HasKey(p => new { p.PartitionId, p.PurgedRecordId });
            e.Property(p => p.PurgedRecordId).ValueGeneratedOnAdd();
            e.Property(p => p.SourceKey).HasMaxLength(400).IsRequired();
            e.Property(p => p.SourceKeyJson).HasMaxLength(2000);
            e.Property(p => p.Label).HasMaxLength(400);
            OptionalOsduId(e.Property(p => p.TargetId)).HasMaxLength(500);
            e.Property(p => p.PurgedBy).HasMaxLength(200).IsRequired();
            // The deleted records whose rows wait to be read again, paged by each run beside the records asked to be planned
            // again, in the same order: the filter keeps the index as small as what waits.
            e.HasIndex(p => new { p.PartitionId, p.FlowId, p.PlanRequestedUtc, p.DeliveryKey })
                .IncludeProperties(p => p.SourceKeyJson)
                .HasFilter("[PlanRequestedUtc] IS NOT NULL");
            // A record's page asks by its key what became of a record the ledger no longer holds, and a lookup by OSDU id finds
            // which record went under it.
            e.HasIndex(p => new { p.PartitionId, p.FlowId, p.DeliveryKey });
            e.HasIndex(p => new { p.PartitionId, p.TargetId }).HasFilter("[TargetId] IS NOT NULL");
            // What a ledger deleted lately, by when: an assertion run asks it before it judges what the search index lists, which
            // can still list a record a removal took out of OSDU moments before.
            e.HasIndex(p => new { p.PartitionId, p.FlowId, p.PurgedUtc }).IncludeProperties(p => p.TargetId);
        });

        modelBuilder.Entity<DeliveryArtifact>(e =>
        {
            e.ToTable("Artifact", SchemaName);
            e.HasKey(a => new { a.PartitionId, a.ArtifactId });
            e.Property(a => a.ArtifactId).ValueGeneratedOnAdd();
            e.Property(a => a.Slot).HasMaxLength(MaxArtifactSlotLength).IsRequired();
            e.Property(a => a.Role).HasMaxLength(16).IsRequired();
            OptionalOsduId(e.Property(a => a.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(a => a.Locator).HasMaxLength(MaxArtifactLocatorLength);
            e.Property(a => a.State).HasMaxLength(16).IsRequired();
            e.Property(a => a.Note).HasMaxLength(MaxArtifactNoteLength);
            e.Property(a => a.SettledBy).HasMaxLength(200);
            // One row per slot of a unit: an intent completed by its id, and a step reported again by a resumed try, update it.
            // Its prefix is a record's artifacts, one seek for the record's page and for a claim.
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.DeliveryKey, a.UnitId, a.Slot }).IsUnique();
            // Every id a delivery minted, by OSDU id: what the inventory joins with what OSDU serves, and a lookup by id.
            e.HasIndex(a => new { a.PartitionId, a.TargetId })
                .HasFilter("[TargetId] IS NOT NULL")
                .IncludeProperties(a => new { a.State, a.Role, a.FlowId, a.DeliveryKey });
            // What is still to undo, and what an abandoned unit left: the sweep reads only these, and the flow's counts and its
            // records with an undo to finish are read from it alone, however many an outage left open.
            e.HasIndex(a => new { a.PartitionId, a.FlowId, a.State, a.NextUndoUtc })
                .HasDatabaseName(OpenArtifactIndex)
                .HasFilter("[State] IN (N'intent', N'pending', N'due', N'failed')")
                .IncludeProperties(a => new { a.DeliveryKey, a.UnitId, a.UndoAttempts, a.CreatedUtc });
        });

        modelBuilder.Entity<DeliveryInventory>(e =>
        {
            e.ToTable("Inventory", SchemaName);
            e.HasKey(i => new { i.PartitionId, i.InventoryId });
            e.Property(i => i.InventoryId).ValueGeneratedOnAdd();
            e.Property(i => i.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(i => i.Name).HasMaxLength(200).IsRequired();
            e.Property(i => i.Kind).HasMaxLength(MaxInventoryKindLength).IsRequired();
            e.Property(i => i.Query).HasMaxLength(MaxInventoryQueryLength);
            e.Property(i => i.ReadMode).HasMaxLength(16).IsRequired();
            e.Property(i => i.Versions).HasMaxLength(16).IsRequired();
            e.Property(i => i.OwnersSource).HasMaxLength(16);
            // One row per inventory of a flow's ledger in the partition: a build finds its own by flow and name.
            e.HasIndex(i => new { i.PartitionId, i.FlowId, i.Name }).IsUnique();
        });

        modelBuilder.Entity<DeliveryInventoryRun>(e =>
        {
            e.ToTable("InventoryRun", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.InventoryRunId });
            e.Property(r => r.InventoryRunId).ValueGeneratedOnAdd();
            e.Property(r => r.Operation).HasMaxLength(16).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.ReadMode).HasMaxLength(16).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            // An inventory's runs, newest first: its page and its next build's look at the last ones.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.InventoryRunId });
        });

        modelBuilder.Entity<DeliveryInventoryRecord>(e =>
        {
            e.ToTable("InventoryRecord", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.InventoryRecordId });
            e.Property(r => r.InventoryRecordId).ValueGeneratedOnAdd();
            OsduId(e.Property(r => r.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(r => r.Kind).HasMaxLength(MaxInventoryKindLength);
            e.Property(r => r.CreateUser).HasMaxLength(MaxInventoryUserLength);
            e.Property(r => r.ModifyUser).HasMaxLength(MaxInventoryUserLength);
            e.Property(r => r.Finding).HasMaxLength(16).IsRequired();
            e.Property(r => r.LedgerStatus).HasMaxLength(16);
            e.Property(r => r.ArtifactState).HasMaxLength(16);
            e.Property(r => r.Detail).HasMaxLength(MaxInventoryDetailLength);
            // One row per id of an inventory: the merge seeks it by id.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.TargetId }).IsUnique();
            // An inventory's records of one finding, in order: the report's grid pages through them, and its counts are read here.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.Finding, r.InventoryRecordId });
            // An inventory's records in order, whatever their finding: the report's grid of every id and the export page through
            // them, seeking the inventory rather than walking every inventory of the partition.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.InventoryRecordId });
            // An OSDU id across every inventory of the partition: a lookup by id answers what each inventory found of it.
            e.HasIndex(r => new { r.PartitionId, r.TargetId }).IncludeProperties(r => new { r.InventoryId, r.Finding, r.GoneUtc });
        });

        modelBuilder.Entity<DeliveryInventoryVersion>(e =>
        {
            e.ToTable("InventoryVersion", SchemaName);
            e.HasKey(v => new { v.PartitionId, v.InventoryRecordId, v.Version });
        });

        modelBuilder.Entity<DeliveryInventoryRemoval>(e =>
        {
            e.ToTable("InventoryRemoval", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.InventoryRemovalId });
            e.Property(r => r.InventoryRemovalId).ValueGeneratedOnAdd();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Finding).HasMaxLength(16).IsRequired();
            e.Property(r => r.Scope).HasMaxLength(16).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            // An inventory's removals, newest first: its page lists them, and a removal closes the earlier ones its process left open.
            e.HasIndex(r => new { r.PartitionId, r.InventoryId, r.InventoryRemovalId });
        });

        modelBuilder.Entity<DeliveryInventoryRemovalItem>(e =>
        {
            e.ToTable("InventoryRemovalItem", SchemaName);
            e.HasKey(i => new { i.PartitionId, i.InventoryRemovalItemId });
            e.Property(i => i.InventoryRemovalItemId).ValueGeneratedOnAdd();
            OsduId(e.Property(i => i.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(i => i.Finding).HasMaxLength(16).IsRequired();
            e.Property(i => i.Outcome).HasMaxLength(16).IsRequired();
            e.Property(i => i.Reason).HasMaxLength(1000);
            // A removal's ids in order, and those of one outcome in order: its page pages through either.
            e.HasIndex(i => new { i.PartitionId, i.InventoryRemovalId, i.InventoryRemovalItemId });
            e.HasIndex(i => new { i.PartitionId, i.InventoryRemovalId, i.Outcome, i.InventoryRemovalItemId });
            // An OSDU id across every removal of the partition: a lookup by id answers what was removed of it, by whom and when.
            e.HasIndex(i => new { i.PartitionId, i.TargetId }).IncludeProperties(i => new { i.InventoryRemovalId, i.Outcome });
        });

        modelBuilder.Entity<DeliveryInventorySelection>(e =>
        {
            e.ToTable("InventorySelection", SchemaName);
            e.HasKey(s => new { s.PartitionId, s.InventorySelectionId });
            e.Property(s => s.InventorySelectionId).ValueGeneratedOnAdd();
            e.Property(s => s.Actor).HasMaxLength(200).IsRequired();
            e.Property(s => s.ConditionsJson).IsRequired();
            e.Property(s => s.ConditionsKey).HasMaxLength(SelectionKeyLength).IsUnicode(false).IsRequired();
            e.Property(s => s.FiltersJson).IsRequired();
            e.Property(s => s.Status).HasMaxLength(16).IsRequired();
            e.Property(s => s.Direction).HasMaxLength(16);
            e.Property(s => s.ColumnsJson).HasMaxLength(4000);
            e.Property(s => s.Error).HasMaxLength(4000);
            // The same conditions asked again of an inventory find the newest match made of them.
            e.HasIndex(s => new { s.PartitionId, s.InventoryId, s.ConditionsKey, s.InventorySelectionId });
            // The matches a sweep retires, oldest first.
            e.HasIndex(s => new { s.PartitionId, s.CreatedUtc });
        });

        modelBuilder.Entity<DeliveryInventorySelectionItem>(e =>
        {
            e.ToTable("InventorySelectionItem", SchemaName);
            // A selection's ids in the inventory's own order: the grid pages through them, and a removal joins them by row.
            e.HasKey(i => new { i.PartitionId, i.InventorySelectionId, i.InventoryRecordId });
            e.Property(i => i.ValuesJson).HasMaxLength(MaxSelectionValuesLength);
        });

        modelBuilder.Entity<DeliveryInventoryScan>(e =>
        {
            e.ToTable("InventoryScan", SchemaName);
            e.HasKey(s => new { s.PartitionId, s.ScanId });
            e.Property(s => s.ScanId).ValueGeneratedOnAdd();
            OsduId(e.Property(s => s.TargetId)).HasMaxLength(MaxTargetIdLength);
            e.Property(s => s.Kind).HasMaxLength(MaxInventoryKindLength);
            e.Property(s => s.CreateUser).HasMaxLength(MaxInventoryUserLength);
            e.Property(s => s.ModifyUser).HasMaxLength(MaxInventoryUserLength);
            // A build's ids by id: its merge reads them in this order, and a build's leftovers are deleted by run.
            e.HasIndex(s => new { s.PartitionId, s.InventoryRunId, s.TargetId });
        });

        modelBuilder.Entity<DeliveryReversal>(e =>
        {
            e.ToTable("Reversal", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.ReversalId });
            e.Property(r => r.ReversalId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(200).IsRequired();
            e.Property(r => r.SourceKind).HasMaxLength(16).IsRequired();
            e.Property(r => r.SubmissionsJson).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.RequestedBy).HasMaxLength(200).IsRequired();
            e.Property(r => r.Error).HasMaxLength(2000);
            // A reversal named by its id alone: a run's payload, a link.
            e.HasIndex(r => r.ReversalId).IsUnique();
            // One reversal per source of a ledger: asking again resumes it rather than opening a second one.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.SourceKind, r.SourceId }).IsUnique();
            // A ledger's reversals, newest first.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.RequestedUtc });
        });

        modelBuilder.Entity<DeliveryReversalItem>(e =>
        {
            e.ToTable("ReversalItem", SchemaName);
            // A reversal's records together, in key order: what its listing adds to and its settlement walks.
            e.HasKey(i => new { i.PartitionId, i.ReversalId, i.DeliveryKey });
            OptionalOsduId(e.Property(i => i.TargetId)).HasMaxLength(500);
            e.Property(i => i.Prior).HasMaxLength(16).IsRequired();
            e.Property(i => i.State).HasMaxLength(16).IsRequired();
            e.Property(i => i.Outcome).HasMaxLength(24);
            e.Property(i => i.Detail).HasMaxLength(2000);
            // The next page of what is still to do, in key order, and the counts by state and outcome, read from this alone.
            e.HasIndex(i => new { i.PartitionId, i.ReversalId, i.State, i.DeliveryKey }).IncludeProperties(i => i.Outcome);
            // A reversal's records by what came of them, a page at a time.
            e.HasIndex(i => new { i.PartitionId, i.ReversalId, i.Outcome, i.DeliveryKey });
        });

        modelBuilder.Entity<DeliveryRetrieval>(e =>
        {
            e.ToTable("Retrieval", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.RetrievalId });
            e.Property(r => r.RetrievalId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(200).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Kinds).HasMaxLength(4000).IsRequired();
            e.Property(r => r.WindowField).HasMaxLength(200);
            e.Property(r => r.Location).HasMaxLength(2000).IsRequired();
            e.Property(r => r.ManifestLocation).HasMaxLength(2000);
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            // A retrieval named by its id: the run that completes it.
            e.HasIndex(r => r.RetrievalId).IsUnique();
            // The flow's listing, the watermark chain (the last done run), and the run's row.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.StartedUtc });
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.Status, r.StartedUtc });
            e.HasIndex(r => r.RunId);
        });

        modelBuilder.Entity<DeliveryAssertionRun>(e =>
        {
            e.ToTable("AssertionRun", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.AssertionRunId });
            e.Property(r => r.AssertionRunId).ValueGeneratedOnAdd();
            e.Property(r => r.FlowName).HasMaxLength(200).IsRequired();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.DefinitionsHash).HasMaxLength(64);
            e.Property(r => r.Error).HasMaxLength(4000);
            // A run named by its id: its report, and the results that close it.
            e.HasIndex(r => r.AssertionRunId).IsUnique();
            // The flow's runs in a partition, newest first; and the platform run's row.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.StartedUtc });
            e.HasIndex(r => r.RunId);
        });

        modelBuilder.Entity<DeliveryAssertionResult>(e =>
        {
            e.ToTable("AssertionResult", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.ResultId });
            e.Property(r => r.ResultId).ValueGeneratedOnAdd();
            e.Property(r => r.TestName).HasMaxLength(100).IsRequired();
            e.Property(r => r.Kind).HasMaxLength(400).IsRequired();
            e.Property(r => r.Outcome).HasMaxLength(16).IsRequired();
            e.Property(r => r.Severity).HasMaxLength(16);
            e.Property(r => r.DefinitionHash).HasMaxLength(32).IsRequired();
            e.Property(r => r.Error).HasMaxLength(4000);
            e.Property(r => r.Detail).IsRequired();
            e.HasIndex(r => r.ResultId).IsUnique();
            // A run's report: its results in the order they were recorded.
            e.HasIndex(r => new { r.PartitionId, r.AssertionRunId });
            // One test's history, newest run first, and the latest result of each test of a flow: one seek per test.
            e.HasIndex(r => new { r.PartitionId, r.FlowId, r.TestName, r.AssertionRunId });
        });

        modelBuilder.Entity<DeliveryDimension>(e =>
        {
            e.ToTable("Dimension", SchemaName);
            e.HasKey(d => new { d.PartitionId, d.DimensionId });
            e.Property(d => d.DimensionId).ValueGeneratedOnAdd();
            e.Property(d => d.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(d => d.Name).HasMaxLength(DeliveryDimension.MaxNameLength).IsRequired();
            e.Property(d => d.Description).HasMaxLength(4000);
            e.Property(d => d.Kind).HasMaxLength(400).IsRequired();
            e.Property(d => d.Query).HasMaxLength(4000);
            e.Property(d => d.Path).HasMaxLength(DeliveryDimension.MaxPathLength).IsRequired();
            e.Property(d => d.FieldIndex).HasMaxLength(16);
            e.Property(d => d.NestedPath).HasMaxLength(DeliveryDimension.MaxPathLength);
            e.Property(d => d.AggregateBy).HasMaxLength(DeliveryDimension.MaxAggregateByLength);
            e.Property(d => d.CleanJson).IsRequired();
            e.Property(d => d.DefinitionHash).HasMaxLength(16).IsRequired();
            e.Property(d => d.TableName).HasMaxLength(128);
            e.Property(d => d.KeyColumn).HasMaxLength(128);
            e.Property(d => d.ValueColumn).HasMaxLength(128);
            // A dimension named by its id: every member, original, build and change of it names it so.
            e.HasIndex(d => d.DimensionId).IsUnique();
            // A flow's dimension in a partition, by its name: what a build registers and a page finds.
            e.HasIndex(d => new { d.PartitionId, d.FlowId, d.Name }).IsUnique();
            // The dimensions writing one table, one a partition: what a build asks before it writes and a removal before it drops.
            e.HasIndex(d => d.TableName);
        });

        modelBuilder.Entity<DeliveryDimensionRun>(e =>
        {
            e.ToTable("DimensionRun", SchemaName);
            e.HasKey(r => new { r.PartitionId, r.DimensionRunId });
            e.Property(r => r.DimensionRunId).ValueGeneratedOnAdd();
            e.Property(r => r.Actor).HasMaxLength(200).IsRequired();
            e.Property(r => r.Status).HasMaxLength(16).IsRequired();
            e.Property(r => r.Mode).HasMaxLength(16).IsRequired();
            e.Property(r => r.RecordKey).HasMaxLength(DeliveryDimensionRun.MaxRecordKeyLength);
            e.Property(r => r.DefinitionHash).HasMaxLength(16).IsRequired();
            e.Property(r => r.Query).HasMaxLength(4000);
            e.Property(r => r.AggregateBy).HasMaxLength(DeliveryDimension.MaxAggregateByLength);
            e.Property(r => r.Error).HasMaxLength(4000);
            e.HasIndex(r => r.DimensionRunId).IsUnique();
            // A dimension's builds, newest first; and the platform run's builds.
            e.HasIndex(r => new { r.PartitionId, r.DimensionId, r.StartedUtc });
            e.HasIndex(r => r.RunId);
        });

        modelBuilder.Entity<DeliveryDimensionMember>(e =>
        {
            e.ToTable("DimensionMember", SchemaName);
            e.HasKey(m => new { m.PartitionId, m.MemberId });
            e.Property(m => m.MemberId).ValueGeneratedOnAdd();
            // A clean value is compared exactly, as a filter an application stored compares it.
            ExactText(e.Property(m => m.Value)).HasMaxLength(256).IsRequired();
            e.HasIndex(m => m.MemberId).IsUnique();
            // A member is a clean value of one dimension, once: what a build matches its members by.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.Value }).IsUnique();
            // The members a dimension holds now, and those it held, each read as one range.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.RemovedRunId });
            // The members most records hold first, a page at a time.
            e.HasIndex(m => new { m.PartitionId, m.DimensionId, m.Records, m.Value }).IsDescending(false, false, true, false);
        });

        modelBuilder.Entity<DeliveryDimensionValue>(e =>
        {
            e.ToTable("DimensionValue", SchemaName);
            e.HasKey(v => new { v.PartitionId, v.ValueId });
            e.Property(v => v.ValueId).ValueGeneratedOnAdd();
            ExactText(e.Property(v => v.Original)).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength).IsRequired();
            e.Property(v => v.OriginalHash).HasMaxLength(32).IsFixedLength().IsRequired();
            e.Property(v => v.LeftOut).HasMaxLength(16);
            e.Property(v => v.Note).HasMaxLength(400);
            e.Property(v => v.Label).HasMaxLength(DeliveryDimensionValue.MaxLabelLength);
            e.Property(v => v.LabelFrom).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength);
            e.Property(v => v.Filter).HasMaxLength(DeliveryDimensionValue.MaxFilterLength);
            e.HasIndex(v => v.ValueId).IsUnique();
            // An original is one value of one dimension, once, compared by its hash so no length or collation blurs two.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.OriginalHash }).IsUnique();
            // A member's originals.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.MemberId });
            // The originals most records hold first, a page at a time.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.Count, v.ValueId }).IsDescending(false, false, true, false);
            // A dimension's keys in the order they arrived, a page at a time, as one range of this dimension alone.
            e.HasIndex(v => new { v.PartitionId, v.DimensionId, v.ValueId });
        });

        modelBuilder.Entity<DeliveryDimensionAttributeValue>(e =>
        {
            e.ToTable("DimensionAttribute", SchemaName);
            e.HasKey(a => new { a.PartitionId, a.AttributeValueId });
            e.Property(a => a.AttributeValueId).ValueGeneratedOnAdd();
            ExactText(e.Property(a => a.Value)).HasMaxLength(DeliveryDimensionAttributeValue.MaxValueLength).IsRequired();
            e.Property(a => a.ValueFrom).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength);
            e.HasIndex(a => a.AttributeValueId).IsUnique();
            // A key's attributes, read with it a page at a time: one row per value, so a collected attribute holds several,
            // and a key holds a value of an attribute once.
            e.HasIndex(a => new { a.PartitionId, a.DimensionId, a.ValueId, a.AttributeId, a.Value }).IsUnique();
            // The keys an attribute value holds (Country is US), and an attribute's values: one seek either way.
            e.HasIndex(a => new { a.PartitionId, a.DimensionId, a.AttributeId, a.Value });
        });

        modelBuilder.Entity<DeliveryDimensionElement>(e =>
        {
            e.ToTable("DimensionElement", SchemaName);
            e.HasKey(x => new { x.PartitionId, x.ElementValueId });
            e.Property(x => x.ElementValueId).ValueGeneratedOnAdd();
            OptionalOsduId(e.Property(x => x.Value)).HasMaxLength(DeliveryDimensionElement.MaxValueLength);
            e.HasIndex(x => x.ElementValueId).IsUnique();
            // A key's objects in their order, each field once: what a build matches its rows by and its table is laid out from.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.ValueId, x.Seq, x.AttributeId }).IsUnique();
        });

        modelBuilder.Entity<DeliveryDimensionView>(e =>
        {
            e.ToTable("DimensionView", SchemaName);
            e.HasKey(v => v.ViewId);
            e.Property(v => v.Name).HasMaxLength(DeliveryDimensionView.MaxNameLength).IsRequired();
            e.Property(v => v.ViewName).HasMaxLength(128).IsRequired();
            e.Property(v => v.FlowName).HasMaxLength(DeliveryLedger.MaxFlowNameLength).IsRequired();
            e.Property(v => v.Description).HasMaxLength(DeliveryDimensionView.MaxDescriptionLength);
            e.Property(v => v.DeclarationJson).IsRequired();
            e.Property(v => v.Sql).IsRequired();
            e.Property(v => v.SqlHash).HasMaxLength(64);
            e.Property(v => v.TablesJson).IsRequired();
            e.Property(v => v.ColumnsJson).IsRequired();
            e.Property(v => v.Note).HasMaxLength(1000);
            e.Property(v => v.WrittenBy).HasMaxLength(200).IsRequired();
            // A view by its name, once in the database: what a build finds its record by, and what refuses a second flow's.
            e.HasIndex(v => v.Name).IsUnique();
            e.HasIndex(v => v.ViewName).IsUnique();
            // A flow's views: what a build drops the undeclared ones of.
            e.HasIndex(v => v.FlowName);
        });

        modelBuilder.Entity<DeliveryDimensionViewCheck>(e =>
        {
            e.ToTable("DimensionViewCheck", SchemaName);
            e.HasKey(c => new { c.PartitionId, c.CheckId });
            e.Property(c => c.CheckId).ValueGeneratedOnAdd();
            e.Property(c => c.Status).HasMaxLength(16).IsRequired();
            e.Property(c => c.Error).HasMaxLength(4000);
            e.HasIndex(c => c.CheckId).IsUnique();
            // A view's checks of a partition, newest first: its page, and what a build prunes past the newest it keeps.
            e.HasIndex(c => new { c.ViewId, c.PartitionId, c.CheckedUtc });
        });

        modelBuilder.Entity<DeliveryDimensionAttributeName>(e =>
        {
            e.ToTable("DimensionAttributeName", SchemaName);
            e.HasKey(n => new { n.PartitionId, n.AttributeId });
            e.Property(n => n.AttributeId).ValueGeneratedOnAdd();
            ExactText(e.Property(n => n.Name)).HasMaxLength(DeliveryDimensionAttributeValue.MaxNameLength).IsRequired();
            e.HasIndex(n => n.AttributeId).IsUnique();
            // A dimension's attribute by its name, once: what a build finds the number by.
            e.HasIndex(n => new { n.PartitionId, n.DimensionId, n.Name }).IsUnique();
        });

        modelBuilder.Entity<DeliveryDimensionCollectedText>(e =>
        {
            e.ToTable("DimensionCollectedText", SchemaName);
            e.HasKey(t => new { t.PartitionId, t.TextId });
            e.Property(t => t.TextId).ValueGeneratedOnAdd();
            e.Property(t => t.TextHash).HasMaxLength(32).IsFixedLength().IsRequired();
            ExactText(e.Property(t => t.Text)).HasMaxLength(DeliveryDimensionValue.MaxOriginalLength).IsRequired();
            ExactText(e.Property(t => t.Value)).HasMaxLength(DeliveryDimensionAttributeValue.MaxValueLength).IsRequired();
            e.HasIndex(t => t.TextId).IsUnique();
            // A text of a collected attribute, once, by its hash, since a text may be longer than a key holds.
            e.HasIndex(t => new { t.PartitionId, t.DimensionId, t.AttributeId, t.TextHash }).IsUnique();
            // The texts of the values a search picks: one seek.
            e.HasIndex(t => new { t.PartitionId, t.DimensionId, t.AttributeId, t.Value });
        });

        modelBuilder.Entity<DeliveryDimensionKeyRecord>(e =>
        {
            e.ToTable("DimensionKeyRecord", SchemaName);
            e.HasKey(x => new { x.PartitionId, x.KeyRecordId });
            e.Property(x => x.KeyRecordId).ValueGeneratedOnAdd();
            e.Property(x => x.RecordHash).HasMaxLength(32).IsFixedLength().IsRequired();
            ExactText(e.Property(x => x.EntityType)).HasMaxLength(DeliveryDimensionKeyRecord.MaxEntityTypeLength).IsRequired();
            e.HasIndex(x => x.KeyRecordId).IsUnique();
            // A key's records, once each: what a write rewrites them by.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.ValueId, x.RecordHash }).IsUnique();
            // The keys read through a record that changed: one seek a record.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.RecordHash });
            // The entity types a dimension's keys were read through, which an update asks the search what changed in.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.EntityType });
        });

        modelBuilder.Entity<DeliveryDimensionRecord>(e =>
        {
            e.ToTable("DimensionRecord", SchemaName);
            e.HasKey(x => new { x.PartitionId, x.DimensionRecordId });
            e.Property(x => x.DimensionRecordId).ValueGeneratedOnAdd();
            e.Property(x => x.RecordHash).HasMaxLength(32).IsFixedLength().IsRequired();
            e.HasIndex(x => x.DimensionRecordId).IsUnique();
            // A record's keys, once each: what a load rewrites them by and an incremental load reads them by.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.RecordHash, x.ValueId }).IsUnique();
            // The records of a key: what goes with a key a load removes.
            e.HasIndex(x => new { x.PartitionId, x.DimensionId, x.ValueId });
        });

        modelBuilder.Entity<DeliveryDimensionChange>(e =>
        {
            e.ToTable("DimensionChange", SchemaName);
            e.HasKey(c => new { c.PartitionId, c.ChangeId });
            e.Property(c => c.ChangeId).ValueGeneratedOnAdd();
            e.Property(c => c.Change).HasMaxLength(16).IsRequired();
            e.HasIndex(c => c.ChangeId).IsUnique();
            // An original's history, and a build's changes.
            e.HasIndex(c => new { c.PartitionId, c.DimensionId, c.ValueId, c.ChangeId });
            e.HasIndex(c => new { c.PartitionId, c.DimensionRunId });
        });

        modelBuilder.Entity<DeliveryMapping>(e =>
        {
            e.ToTable("Mapping", SchemaName);
            e.HasKey(m => m.Id);
            e.Property(m => m.Reference).HasMaxLength(200).IsRequired();
            e.Property(m => m.Name).HasMaxLength(150).IsRequired();
            e.Property(m => m.Version).HasMaxLength(50).IsRequired();
            e.Property(m => m.Kind).HasMaxLength(200).IsRequired();
            e.Property(m => m.TemplateVersion).HasMaxLength(64).IsRequired();
            e.Property(m => m.TemplatePath).HasMaxLength(DeliveryRepositoryTemplate.MaxPathLength).IsRequired().HasDefaultValue(string.Empty);
            e.Property(m => m.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(m => m.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(m => m.Yaml).IsRequired();
            e.Property(m => m.SummaryJson).IsRequired();
            e.Property(m => m.Status).HasMaxLength(16).IsRequired();
            e.Property(m => m.Message).HasMaxLength(4000);
            e.HasIndex(m => new { m.RepoId, m.Reference }).IsUnique();
            e.HasIndex(m => new { m.RepoId, m.Kind });
            // A template delete asks which mappings pin the version.
            e.HasIndex(m => new { m.Kind, m.TemplateVersion });
        });

        modelBuilder.Entity<DeliverySearchTerm>(e =>
        {
            e.ToTable("SearchTerm", SchemaName);
            e.HasKey(t => t.Id);
            e.Property(t => t.TermKey).HasMaxLength(SearchTermKeyLength).IsRequired();
            e.Property(t => t.Source).HasMaxLength(DeliverySearchTerm.MaxSourceLength).IsRequired();
            e.Property(t => t.EntityType).HasMaxLength(DeliverySearchTerm.MaxEntityTypeLength).IsRequired();
            e.Property(t => t.Column).HasMaxLength(DeliverySearchTerm.MaxColumnLength).IsRequired();
            e.Property(t => t.RoutesJson).IsRequired();
            e.Property(t => t.FlowsJson).IsRequired();
            e.HasIndex(t => new { t.RepoId, t.TermId, t.EntityType }).IsUnique();
            // The search terms page and the explorer list the terms of one entity type, from every repository.
            e.HasIndex(t => new { t.EntityType, t.TermId });
        });

        modelBuilder.Entity<DeliverySearchTermRefinement>(e =>
        {
            e.ToTable("SearchTermRefinement", SchemaName);
            e.HasKey(r => r.TermId);
            e.Property(r => r.TermKey).HasMaxLength(SearchTermKeyLength).IsRequired();
            e.Property(r => r.EntityType).HasMaxLength(DeliverySearchTerm.MaxEntityTypeLength).IsRequired();
            e.Property(r => r.Name).HasMaxLength(DeliverySearchTermRefinement.MaxNameLength);
            e.Property(r => r.Route).HasMaxLength(DeliverySearchTermRefinement.MaxRouteLength);
            e.Property(r => r.Note).HasMaxLength(DeliverySearchTermRefinement.MaxNoteLength);
            e.Property(r => r.UpdatedBy).HasMaxLength(200).IsRequired();
            // A name is the term's within its entity type: a rename checks it is free there.
            e.HasIndex(r => new { r.EntityType, r.Name });
        });

        modelBuilder.Entity<DeliveryInterface>(e =>
        {
            e.ToTable("Interface", SchemaName);
            e.HasKey(i => i.Id);
            e.Property(i => i.FlowName).HasMaxLength(400).IsRequired();
            e.Property(i => i.Interface).HasMaxLength(DeliveryInterface.MaxInterfaceLength).IsRequired();
            e.Property(i => i.LedgerName).HasMaxLength(200).IsRequired();
            e.Property(i => i.Route).HasMaxLength(32).IsRequired();
            e.Property(i => i.RouteReason).HasMaxLength(1000);
            e.Property(i => i.MappingReference).HasMaxLength(200).IsRequired();
            e.Property(i => i.CacheVersion).HasMaxLength(64);
            e.Property(i => i.Kind).HasMaxLength(200).IsRequired();
            e.Property(i => i.RecordObject).HasMaxLength(400).IsRequired();
            e.Property(i => i.AfterJson).HasMaxLength(4000).IsRequired();
            e.Property(i => i.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(i => i.Partition).HasMaxLength(200).IsRequired();
            e.Property(i => i.TargetPartition).HasMaxLength(200).IsRequired().HasDefaultValue(string.Empty);
            e.HasIndex(i => new { i.RepoId, i.FlowName, i.Interface, i.Partition }).IsUnique();
            // A record's page, a submission's page and the record search find the pipeline behind a ledger identity.
            e.HasIndex(i => i.LedgerFlowId);
        });

        modelBuilder.Entity<DeliveryTemplate>(e =>
        {
            e.ToTable("Template", SchemaName);
            e.HasKey(t => t.Id);
            e.Property(t => t.Kind).HasMaxLength(200).IsRequired();
            e.Property(t => t.Version).HasMaxLength(64).IsRequired();
            e.Property(t => t.SchemaJson).IsRequired();
            e.Property(t => t.Origin).HasMaxLength(1000).IsRequired();
            e.Property(t => t.CapturedBy).HasMaxLength(200).IsRequired();
            e.HasIndex(t => new { t.Kind, t.Version }).IsUnique();
        });

        modelBuilder.Entity<DeliveryRepositoryTemplate>(e =>
        {
            e.ToTable("RepositoryTemplate", SchemaName);
            e.HasKey(t => t.Id);
            e.Property(t => t.Path).HasMaxLength(DeliveryRepositoryTemplate.MaxPathLength).IsRequired();
            e.Property(t => t.Folder).HasMaxLength(DeliveryRepositoryTemplate.MaxPathLength).IsRequired();
            e.Property(t => t.Kind).HasMaxLength(200).IsRequired();
            e.Property(t => t.Version).HasMaxLength(64).IsRequired();
            e.Property(t => t.Problem).HasMaxLength(DeliveryRepositoryTemplate.MaxProblemLength);
            e.Property(t => t.SchemaJson);
            // A sync reconciles one repository's files, and a page reads them, and the copy of the one it settles on, to
            // settle that repository's documents.
            e.HasIndex(t => new { t.RepoId, t.Path }).IsUnique();
        });

        modelBuilder.Entity<DeliveryCacheVersion>(e =>
        {
            e.ToTable("CacheVersion", SchemaName);
            // Stored in partition order (the clustered index below), so everything a refresh reads or writes of its
            // partition's versions is a range seek of that partition, and refreshes of different partitions never touch,
            // and so never wait on, each other's rows.
            e.HasKey(v => v.Id).IsClustered(false);
            e.Property(v => v.Scope).HasMaxLength(200).IsRequired();
            e.Property(v => v.FlowName).HasMaxLength(200).IsRequired();
            e.Property(v => v.Version).HasMaxLength(64).IsRequired();
            e.Property(v => v.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(v => v.PreviousVersion).HasMaxLength(64);
            e.Property(v => v.CapturedBy).HasMaxLength(200).IsRequired();
            e.Property(v => v.Origin).HasMaxLength(1000).IsRequired();
            e.Property(v => v.TypesJson).IsRequired();
            e.Property(v => v.SystemPropertiesJson).IsRequired().HasDefaultValue("[]");
            e.Property(v => v.PrunedBy).HasMaxLength(200);
            e.HasIndex(v => new { v.Scope, v.Version }).IsUnique();
            // The sequence is what a concurrent second write of the same partition collides on, so two captures can never
            // both claim the next version. It is the table's clustered key: a partition's versions in order.
            e.HasIndex(v => new { v.Scope, v.Sequence }).IsUnique().IsClustered();
            e.HasIndex(v => v.RunId);
        });

        modelBuilder.Entity<DeliveryCacheItem>(e =>
        {
            e.ToTable("CacheItem", SchemaName);
            // Stored in partition order (the clustered index below) for the same reason as the versions: a refresh reads
            // and closes only its own partition's rows, whatever plan the table's size leads to.
            e.HasKey(i => i.ItemId).IsClustered(false);
            e.HasIndex(i => new { i.Scope, i.ItemId }).IsUnique().IsClustered();
            e.Property(i => i.Scope).HasMaxLength(200).IsRequired();
            e.Property(i => i.TypeName).HasMaxLength(200).IsRequired();
            e.Property(i => i.EntityType).HasMaxLength(200).IsRequired();
            OsduId(e.Property(i => i.RecordId)).HasMaxLength(512).IsRequired();
            e.Property(i => i.FieldsJson).IsRequired();
            e.Property(i => i.Terms).IsRequired();
            // A record holds one range per distinct content: the type listing is its prefix, so this index answers both a
            // version's listing of a type and one record's history.
            e.HasIndex(i => new { i.Scope, i.TypeName, i.RecordId, i.FromSequence }).IsUnique();
            // The rows the newest version holds (ToSequence null), which a merge compares its result against, and the closed
            // ranges a retention removes.
            e.HasIndex(i => new { i.Scope, i.ToSequence });
            // The rows each version began: what a version changed is counted from them, by the history and by a retention
            // recording a version's changes before it removes the version's records.
            e.HasIndex(i => new { i.Scope, i.FromSequence }).IncludeProperties(i => new { i.TypeName, i.RecordId });
        });

        modelBuilder.Entity<SchemaLineage>(e =>
        {
            e.ToTable("SchemaLineage", SchemaName);
            e.HasKey(l => l.Id);
            e.Property(l => l.Release).HasMaxLength(SchemaLineage.MaxReleaseLength).IsRequired();
            e.Property(l => l.Commit).HasMaxLength(64).IsRequired();
            e.Property(l => l.BuiltBy).HasMaxLength(200).IsRequired();
            e.Property(l => l.FailuresJson).IsRequired();
            // A release is built once: building it again replaces its kinds under the same row.
            e.HasIndex(l => l.Release).IsUnique();
        });

        modelBuilder.Entity<SchemaLineageKind>(e =>
        {
            e.ToTable("SchemaLineageKind", SchemaName);
            e.HasKey(k => new { k.LineageId, k.Kind });
            e.Property(k => k.Kind).HasMaxLength(400).IsRequired();
            e.Property(k => k.EntityType).HasMaxLength(256).IsRequired();
            e.Property(k => k.Version).HasMaxLength(64).IsRequired();
            e.Property(k => k.Status).HasMaxLength(32);
            e.Property(k => k.RelationshipsJson).IsRequired();
            // The lineage page reads a release's kinds by entity type; the OSDU order looks a kind up across releases.
            e.HasIndex(k => new { k.LineageId, k.EntityType });
            e.HasIndex(k => k.Kind);
        });

        modelBuilder.Entity<DeliveryCacheMember>(e =>
        {
            e.ToTable("CacheMember", SchemaName);
            e.Property(m => m.Scope).HasMaxLength(200).IsRequired();
            e.Property(m => m.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(m => m.RecordId)).HasMaxLength(512).IsRequired();
            e.Property(m => m.FlowName).HasMaxLength(200).IsRequired();
            // A merge reads who holds each record of the captured types, and replaces one flow's rows of a type.
            e.HasKey(m => new { m.Scope, m.TypeName, m.RecordId, m.FlowName });
            e.HasIndex(m => new { m.Scope, m.FlowName, m.TypeName });
        });

        modelBuilder.Entity<DeliveryCacheSet>(e =>
        {
            e.ToTable("CacheSet", SchemaName);
            e.HasKey(c => c.SetId);
            e.Property(c => c.SetHash).HasMaxLength(64).IsRequired();
            e.HasIndex(c => c.SetHash).IsUnique();
            // The planner reads this every run: a filtered index keeps it to the handful of gated sets.
            e.HasIndex(c => c.Gated).HasFilter("[Gated] = 1");
        });

        modelBuilder.Entity<DeliveryCacheSetEntry>(e =>
        {
            e.ToTable("CacheSetEntry", SchemaName);
            e.Property(c => c.Scope).HasMaxLength(200).IsRequired();
            e.Property(c => c.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(c => c.ItemId)).HasMaxLength(512).IsRequired();
            e.Property(c => c.Path).HasMaxLength(400).IsRequired();
            e.Property(c => c.Kind).HasMaxLength(16).IsRequired();
            e.Property(c => c.ValueHash).HasMaxLength(64).IsRequired();
            e.Property(c => c.ValueText).HasMaxLength(400).IsRequired();
            e.HasKey(c => new { c.SetId, c.Scope, c.TypeName, c.ItemId, c.Path, c.Kind });
            // The impact query: which sets hold this cached value of this partition's cache.
            e.HasIndex(c => new { c.Scope, c.TypeName, c.ItemId });
        });

        modelBuilder.Entity<DeliveryPartition>(e =>
        {
            e.ToTable("Partition", SchemaName);
            e.HasKey(p => p.Name);
            e.Property(p => p.Name).HasMaxLength(200).IsRequired();
            e.Property(p => p.Description).HasMaxLength(DeliveryPartition.MaxDescriptionLength);
            e.Property(p => p.CreatedBy).HasMaxLength(200).IsRequired();
            e.Property(p => p.UpdatedBy).HasMaxLength(200).IsRequired();
            // At most one partition is the default.
            e.HasIndex(p => p.IsDefault).IsUnique().HasFilter("[IsDefault] = 1");
        });

        modelBuilder.Entity<DeliveryConfigProperty>(e =>
        {
            e.ToTable("ConfigProperty", SchemaName);
            e.HasKey(c => c.Id);
            e.Property(c => c.Name).HasMaxLength(DeliveryConfigNames.MaxNameLength).IsRequired();
            e.Property(c => c.Value).HasMaxLength(DeliveryConfigNames.MaxValueLength).IsRequired();
            e.Property(c => c.Description).HasMaxLength(DeliveryConfigNames.MaxDescriptionLength);
            e.Property(c => c.UpdatedBy).HasMaxLength(200).IsRequired();
            e.Property(c => c.Partition).HasMaxLength(200);
            // One value per name per scope: the control plane's own value is the row with no repository, and a value for
            // no particular partition the row with no partition. The filter is cleared because EF excludes nulls from a
            // unique index over nullable columns by default, which would leave those rows unconstrained; SQL Server treats
            // nulls as equal, so one index covers every scope.
            e.HasIndex(c => new { c.RepoId, c.Partition, c.Name }).IsUnique().HasFilter(null);
            e.HasIndex(c => c.Name);
        });

        modelBuilder.Entity<DeliveryUpdateTag>(e =>
        {
            e.ToTable("UpdateTag", SchemaName);
            e.HasKey(t => t.TagId);
            e.Property(t => t.Kind).HasMaxLength(16).IsRequired();
            e.Property(t => t.Scope).HasMaxLength(200).IsRequired();
            e.Property(t => t.TypeName).HasMaxLength(200).IsRequired();
            OsduId(e.Property(t => t.ItemId)).HasMaxLength(512).IsRequired();
            e.Property(t => t.Path).HasMaxLength(400).IsRequired();
            e.Property(t => t.Change).HasMaxLength(16).IsRequired();
            e.Property(t => t.OldValue).HasMaxLength(400);
            e.Property(t => t.NewValue).HasMaxLength(400);
            e.Property(t => t.FromVersion).HasMaxLength(64);
            e.Property(t => t.ToVersion).HasMaxLength(64).IsRequired();
            e.Property(t => t.Mode).HasMaxLength(16).IsRequired();
            e.Property(t => t.Status).HasMaxLength(16).IsRequired();
            e.Property(t => t.DecidedBy).HasMaxLength(200);
            e.Property(t => t.SetIds).IsRequired();
            // The rollout queue, across every partition.
            e.HasIndex(t => t.Status);
            e.HasIndex(t => new { t.Scope, t.TypeName, t.ItemId, t.Path, t.Status });
            // A partition's changes of one status, newest first: the tag id the table is keyed by orders them.
            e.HasIndex(t => new { t.Scope, t.Status });
        });

        modelBuilder.Entity<DeliveryCacheDefinition>(e =>
        {
            e.ToTable("CacheDefinition", SchemaName);
            e.HasKey(c => c.Id);
            e.Property(c => c.FlowName).HasMaxLength(200).IsRequired();
            e.Property(c => c.Scope).HasMaxLength(200).IsRequired();
            e.Property(c => c.Origin).HasMaxLength(16).IsRequired().HasDefaultValue("osdu");
            e.Property(c => c.Endpoint).HasMaxLength(1000);
            e.Property(c => c.Connection).HasMaxLength(1000);
            e.Property(c => c.SourceObject).HasMaxLength(400);
            e.Property(c => c.KeyField).HasMaxLength(128);
            e.Property(c => c.DictionaryPath).HasMaxLength(1000);
            e.Property(c => c.RelativePath).HasMaxLength(1000).IsRequired();
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
            e.Property(c => c.EntityType).HasMaxLength(200).IsRequired();
            e.Property(c => c.Kind).HasMaxLength(400);
            e.Property(c => c.Query).HasMaxLength(4000);
            e.Property(c => c.FieldsJson).IsRequired();
            e.Property(c => c.OnChange).HasMaxLength(16).IsRequired();
            e.Property(c => c.RetentionDays).HasDefaultValue(7);
            // A cache flow that names its partitions declares each of its types once per partition it builds a cache for.
            e.HasIndex(c => new { c.RepoId, c.FlowName, c.Name, c.Scope }).IsUnique();
            // A refresh reads every declaration of its partition; the GUI lists a partition's types and the flows filling them.
            e.HasIndex(c => new { c.Scope, c.Name });
            e.HasIndex(c => c.FlowName);
        });

        modelBuilder.Entity<OsduSchemaVersion>(e =>
        {
            e.ToTable("SchemaVersion", SchemaName, t => t.HasCheckConstraint("CK_SchemaVersion_SingleRow", "[Id] = 1"));
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).ValueGeneratedNever();
            e.Property(v => v.ModuleVersion).HasMaxLength(32).IsRequired();
            e.Property(v => v.LastMigration).HasMaxLength(150).IsRequired();
            e.Property(v => v.AppliedBy).HasMaxLength(200).IsRequired();
            e.Property(v => v.MinimumCatalogMigration).HasMaxLength(150).IsRequired();
        });
    }

    /// <summary>
    /// A column keyed on an OSDU record id: compared exactly by the database (<see cref="OsduIdCollation"/>) and exactly by
    /// the change tracker. Without the ordinal comparer, a context tracking the foot and
    /// the femtotesla side by side (two memberships of one cache flow, say) takes them for one key and refuses the second.
    /// </summary>
    private static PropertyBuilder<string> OsduId(PropertyBuilder<string> property)
    {
        property.Metadata.SetValueComparer(new ValueComparer<string>(
            (left, right) => string.Equals(left, right, StringComparison.Ordinal),
            value => StringComparer.Ordinal.GetHashCode(value),
            value => value));
        return property.UseCollation(OsduIdCollation);
    }

    /// <summary>
    /// A column of text a dimension compares exactly: by the database (<see cref="OsduIdCollation"/>), where the default
    /// collation would take GR and gr for one member, and by the change tracker.
    /// </summary>
    private static PropertyBuilder<string> ExactText(PropertyBuilder<string> property) => OsduId(property);

    /// <summary>A nullable column keyed on an OSDU record id, compared exactly as <see cref="OsduId"/> compares a required one.</summary>
    private static PropertyBuilder<string?> OptionalOsduId(PropertyBuilder<string?> property)
    {
        property.Metadata.SetValueComparer(new ValueComparer<string?>(
            (left, right) => string.Equals(left, right, StringComparison.Ordinal),
            value => value == null ? 0 : StringComparer.Ordinal.GetHashCode(value),
            value => value));
        return property.UseCollation(OsduIdCollation);
    }
}
