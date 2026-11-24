using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Record status vocabulary (osdu/docs/reference/concepts/record-lifecycle.md, Record states). <c>Held</c>, <c>Failed</c>
/// and <c>Reverted</c> are terminal until an operator releases the record or the source changes. <c>Deleted</c> is not
/// blocked: the record follows its source again.
/// </summary>
public enum RecordStatus
{
    Pending,
    Delivering,
    Delivered,
    Held,
    Failed,

    /// <summary>
    /// Removed from OSDU by an operator, and not blocked: the record follows its source again, so the next run that
    /// reads its row (a full read, or the row changed under an incremental one) sends it again, unless the ingestion
    /// table marks the row deleted.
    /// </summary>
    Deleted,

    /// <summary>
    /// Put back by a reversal to the version OSDU held before the run that was reversed
    /// (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission): OSDU holds that version
    /// again, as a new one, and the record is blocked from redelivery while the source is unchanged. A release makes it
    /// delivered again.
    /// </summary>
    Reverted,

    /// <summary>
    /// Holds a rendered document that refers to a record another record of the ledger holds and has not delivered, and
    /// goes back to pending when that one lands (osdu/docs/reference/concepts/record-lifecycle.md, Waiting records).
    /// Waiting is not a try: it charges nothing, and no operator is needed.
    /// </summary>
    Waiting,
}

public enum SubmissionStatus
{
    Received,
    Planned,
    Running,
    Completed,
    Failed,
}

public enum AttemptOutcome
{
    Delivered,
    Skipped,
    Failed,
    Held,

    /// <summary>The record was removed from OSDU, reversibly or by a purge of everything.</summary>
    Deleted,

    /// <summary>The record's earlier versions were purged; the record itself is still delivered and live.</summary>
    HistoryPurged,

    /// <summary>
    /// An earlier version was written back as a new version: by a reversal (phase <see cref="AttemptPhases.Reverse"/>), the
    /// version OSDU held before the run it reversed; by an operator (phase <see cref="AttemptPhases.RestorePrevious"/>), the
    /// version before the latest.
    /// </summary>
    Restored,

    /// <summary>
    /// What an aborted delivery left in OSDU was undone (phase <see cref="AttemptPhases.Undo"/>;
    /// osdu/docs/reference/concepts/artifacts-and-undo.md, How the ledger and the GUI show it): its result names each
    /// artifact and what became of it. The record's own state is what the delivery left it in.
    /// </summary>
    Undone,

    /// <summary>
    /// The record's ACL in OSDU was replaced with a registered restriction's groups (phase <see cref="AttemptPhases.Restrict"/>;
    /// osdu/docs/reference/concepts/access-restriction.md): its result names the restriction, the ACL set and the ACL replaced.
    /// The record keeps its status and its version; no run delivers it until the restriction is lifted.
    /// </summary>
    Restricted,

    /// <summary>
    /// The restriction was lifted (phase <see cref="AttemptPhases.LiftRestriction"/>): the ACL OSDU held before it was written
    /// back, or nothing was asked of OSDU because it no longer held the record. The record follows its source again.
    /// </summary>
    Unrestricted,
}

/// <summary>The phases of the attempts that record a decision not to send, beside the delivery phases the worker reports.</summary>
public static class AttemptPhases
{
    /// <summary>An operator replaced the record's ACL in OSDU with a registered restriction's groups (outcome restricted).</summary>
    public const string Restrict = "restrict";

    /// <summary>An operator lifted the record's restriction, giving it back the ACL it had before (outcome unrestricted).</summary>
    public const string LiftRestriction = "lift-restriction";

    /// <summary>A skipped attempt: the source carried a version older than the one delivered or queued.</summary>
    public const string Stale = "stale";

    /// <summary>
    /// The record's newer work was not sent: what an earlier delivery of it left in OSDU is not undone yet. Skipped and not
    /// charged while the undo has tries left, held once it has none (osdu/docs/reference/concepts/record-lifecycle.md,
    /// Waiting for an undo).
    /// </summary>
    public const string UndoWait = "undo-wait";

    /// <summary>A skipped attempt: the final hash check found OSDU already holding the queued document and payload.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>
    /// A skipped attempt the intake writes: the ingestion table changed the row, and the document it renders is the one
    /// OSDU holds or the one already queued, so nothing is sent. It records the change, with the row's new origin.
    /// </summary>
    public const string Identical = "identical";

    /// <summary>A held attempt: the ingestion table marked the record row deleted, and a deleted row is never delivered.</summary>
    public const string SourceDeleted = "source-deleted";

    /// <summary>
    /// A held attempt: the document queued for the record was not there to read where the run that had it reads its work
    /// from (another node wrote its work batch, or the batch was pruned or rewritten), so nothing was sent. The record is
    /// not blocked: it dropped what it had queued and asks to be planned again from its row
    /// (osdu/docs/reference/concepts/submissions.md, Work queued where a run cannot read it).
    /// </summary>
    public const string DocumentMissing = "document-missing";

    /// <summary>
    /// A skipped attempt a sync writes: the record's row is not in the ingestion table, or not in the scope the record was
    /// planned under. The record keeps its status: what OSDU holds is removed only by a removal someone asks for.
    /// </summary>
    public const string SourceMissing = "source-missing";

    /// <summary>
    /// What a reversal did to a record (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a
    /// submission): restored (outcome restored), removed (outcome deleted), passed over (outcome skipped) or failed, each
    /// saying why in its result.
    /// </summary>
    public const string Reverse = "reverse";

    /// <summary>
    /// An operator took the record's latest version out of being current
    /// (osdu/docs/reference/concepts/removal-and-reversal.md, Restoring the previous version): the version before it was
    /// written back as a new version (outcome restored), naming both in its result.
    /// </summary>
    public const string RestorePrevious = "restore-previous";

    /// <summary>
    /// An undo of what an aborted delivery left in OSDU (outcome undone;
    /// osdu/docs/reference/concepts/artifacts-and-undo.md, How the ledger and the GUI show it): its result names why the
    /// delivery was undone and each artifact with what became of it, and its error what could not be undone yet.
    /// </summary>
    public const string Undo = "undo";

    /// <summary>
    /// An operator gave the record a new OSDU id because OSDU lost the one it claimed
    /// (osdu/docs/reference/flow/inventory-removals.md, Repairing missing ids): a skipped attempt, since nothing was sent, whose
    /// result names both ids and why. The next deliver run writes the record under the new id.
    /// </summary>
    public const string ReassignId = "reassign-id";

    /// <summary>
    /// A run adopted, as an operator asked, the record OSDU already held at the id the record's mapping makes from its key,
    /// which no record of a ledger had claimed (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU
    /// already holds): a skipped attempt, since adopting sends nothing, whose result names the id and the version OSDU held
    /// there. The same run writes the record to that id.
    /// </summary>
    public const string Adopt = "adopt";

    /// <summary>
    /// A removal flow took the record out because its source no longer holds it (osdu/docs/reference/flow/removal.md): outcome
    /// deleted when OSDU held it, skipped when OSDU did not (its queued work dropped). The result names the removal flow, the
    /// run, why the source no longer holds the key and the file and row that said so.
    /// </summary>
    public const string SourceRemoved = "source-removed";
}

/// <summary>
/// What a verify found of a record in OSDU (osdu/docs/reference/flow/delivery.md, verify). A target answers the first four;
/// the verify pass records the last two of a record the ledger marks removed from OSDU, whose expected answer is that OSDU
/// holds nothing at its id.
/// </summary>
public enum VerifyOutcome
{
    /// <summary>OSDU holds the record at the version the ledger holds.</summary>
    Match,

    /// <summary>OSDU holds the record at another version, or with other content.</summary>
    Drifted,

    /// <summary>OSDU holds no record at the id: never written, removed, or hidden from the flow's credentials.</summary>
    Missing,

    /// <summary>The target could not be read.</summary>
    Error,

    /// <summary>A record the ledger marks removed, and OSDU holds no record at its id: the removal stands.</summary>
    Gone,

    /// <summary>A record the ledger marks removed, and OSDU still holds a record at its id: the removal did not take, or it was written again.</summary>
    Served,
}

/// <summary>Where the version of a record came from: the ingestion table's file and row, and when the table last updated the row.</summary>
public readonly record struct RecordOrigin(string? FileName, long? RowNumber, DateTime? UpdatedUtc)
{
    public static RecordOrigin None { get; } = new(null, null, null);
}

/// <summary>
/// One plan of a flow over its ingestion tables (osdu/docs/reference/concepts/submissions.md, The submission): an
/// incremental window, a full read, or a set of record keys. Its id is the idempotency key.
/// </summary>
public sealed record SubmissionState
{
    public required Guid SubmissionId { get; init; }

    public required Guid FlowId { get; init; }

    /// <summary>
    /// The OSDU partition the row belongs to, as the ledger's directory names it: filled when the ledger reads the row, and
    /// null for a ledger not yet placed in a partition. A write takes the partition of its ledger identity, never this.
    /// </summary>
    public string? Partition { get; init; }


    public required string FlowName { get; init; }

    public required string MappingReference { get; init; }

    public required string RenderContext { get; init; }

    public string ParametersJson { get; init; } = "{}";

    /// <summary>How many candidate records the plan estimated when it opened the source.</summary>
    public long RecordCount { get; init; }

    public SubmissionStatus Status { get; init; } = SubmissionStatus.Received;

    /// <summary>
    /// The work location the intake wrote its batches under (osdu/docs/reference/concepts/submissions.md, Work batches).
    /// </summary>
    public string? WorkLocation { get; init; }

    /// <summary>How many work batches the intake wrote.</summary>
    public int BatchCount { get; init; }

    /// <summary>How many key slices the intake was cut into for its fan-out; 1 for a plan that ran on one node.</summary>
    public int Slices { get; init; }

    public DateTime ReceivedUtc { get; init; }

    public DateTime? StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public long Planned { get; init; }

    public long SkippedUnchanged { get; init; }

    /// <summary>
    /// Records a cache change waiting for approval held back: rendered, ready, and not sent until an operator
    /// approves or rejects the change. They are not unchanged, and a run that reports them as such hides the fact
    /// that a decision is what the estate is waiting on.
    /// </summary>
    public long AwaitingApproval { get; init; }

    /// <summary>
    /// Records the source carried in a version older than the one already delivered or queued: skipped and never sent,
    /// each with an attempt saying which version it was and which one stands.
    /// </summary>
    public long SkippedStale { get; init; }

    /// <summary>Records whose queued document was, when the worker came to send it, what OSDU already held: nothing was sent.</summary>
    public long UnchangedAtPush { get; init; }

    /// <summary>Records held, failed, deleted or reverted earlier whose source has not changed; they need a release.</summary>
    public long Blocked { get; init; }

    public long Delivered { get; init; }

    public long Held { get; init; }

    public long Failed { get; init; }

    /// <summary>Records of the submission still waiting, when it closed, for a record they refer to that has not landed; they go out once it does.</summary>
    public long Waiting { get; init; }

    /// <summary>Records without a derivable delivery key: not planned, not delivered.</summary>
    public long Untracked { get; init; }

    public string? Error { get; init; }

    /// <summary>One of <see cref="SubmissionKinds"/>.</summary>
    public string Kind { get; init; } = SubmissionKinds.Incremental;

    /// <summary>The source connection reference as the flow declared it; never a resolved value.</summary>
    public string SourceConnection { get; init; } = string.Empty;

    /// <summary>The record table's three-part name.</summary>
    public string SourceObject { get; init; } = string.Empty;

    /// <summary>The lower bound (exclusive) of the change window planned; null for a plan without one.</summary>
    public DateTime? WindowFromUtc { get; init; }

    /// <summary>The upper bound (inclusive) of the change window planned; set for incremental and full plans.</summary>
    public DateTime? WindowToUtc { get; init; }

    /// <summary>What else bounded the read, as JSON (<see cref="Source.SourceWindowDescription"/>).</summary>
    public string? SourceWindowJson { get; init; }

    /// <summary>The platform run that coordinated the plan.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Whether the plan covered the whole scope, so its completion may move the scope's watermark.</summary>
    public bool CoversScope => Kind is SubmissionKinds.Incremental or SubmissionKinds.Full;
}

/// <summary>What a submission is: which selection of the ingestion tables it planned.</summary>
public static class SubmissionKinds
{
    /// <summary>The rows the tables changed in a window after the scope's watermark.</summary>
    public const string Incremental = "incremental";

    /// <summary>Every row of the scope, up to a bound: a first plan, or a replan.</summary>
    public const string Full = "full";

    /// <summary>Named record keys: a record-scoped run, or the records the ledger asked to plan again.</summary>
    public const string Keys = "keys";

    public static IReadOnlyList<string> All { get; } = [Incremental, Full, Keys];
}

/// <summary>
/// The current state of one deliverable (osdu/docs/reference/concepts/ledger.md, <c>osdu.Record</c>: the current state of
/// one deliverable). A record is one flow's: its identity is the flow and the delivery key together, so two flows reading
/// the same source row keep two records with separate histories.
/// </summary>
public sealed record RecordState
{
    public required DeliveryKey DeliveryKey { get; init; }

    public required Guid FlowId { get; init; }

    /// <summary>
    /// The OSDU partition the row belongs to, as the ledger's directory names it: filled when the ledger reads the row, and
    /// null for a ledger not yet placed in a partition. A write takes the partition of its ledger identity, never this.
    /// </summary>
    public string? Partition { get; init; }


    public required string SourceKey { get; init; }

    /// <summary>The record's key tuple as a JSON array of strings, in the flow's key order: what a key-scoped read uses.</summary>
    public string? SourceKeyJson { get; init; }

    /// <summary>Human-readable label from the mapping's identity.label template (for search and display only).</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The values of the columns the mapping declares as identities (<c>dataset.identity</c>): a wellbore id, a well
    /// name, whatever an operator holds when they come looking. Search only, never part of the record.
    /// </summary>
    public IReadOnlyList<string> Identities { get; init; } = [];

    /// <summary>
    /// The OSDU ids of the records the mapping's enrichments add to this one (osdu/docs/reference/flow/enrich-records.md), as
    /// its last render made them: what finds the record it belongs to when an operator holds one of them. Search only.
    /// </summary>
    public IReadOnlyList<string> AddedRecords { get; init; } = [];

    public required string MappingName { get; init; }

    /// <summary>The render context of the last delivered document, canonical JSON.</summary>
    public string? RenderContext { get; init; }

    /// <summary>
    /// The estate commit the run that planned <see cref="RenderContext"/> executed, when it knew it: what a delivery's reason
    /// quotes beside the mapping fingerprint the record's document was rendered with. Null when that run was not pinned to a
    /// commit, and for a context written before the ledger kept it.
    /// </summary>
    public string? RenderCommit { get; init; }

    /// <summary>
    /// The paths of the document OSDU holds of the record and a hash of each value (<see cref="Planning.RenderPaths.EncodeText"/>):
    /// what a delivery compares its render with to name the paths it changes. Text, so a state compares equal to another read
    /// of the same record. Null for a record delivered before the ledger kept them, and once OSDU no longer holds the version
    /// they describe (a removal, a reversal, a step back).
    /// </summary>
    public string? RenderPaths { get; init; }

    /// <summary>The ingestion fingerprint of the rows the delivered document was built from.</summary>
    public string? SourceFingerprint { get; init; }

    /// <summary>The business version of the row the delivered document was built from (the flow's source.lastModified).</summary>
    public DateTime? SourceModifiedUtc { get; init; }

    /// <summary>The ingestion file the version OSDU holds came from.</summary>
    public string? SourceFileName { get; init; }

    /// <summary>The row of that file.</summary>
    public long? SourceRowNumber { get; init; }

    /// <summary>When the ingestion table last updated that row.</summary>
    public DateTime? SourceUpdatedUtc { get; init; }

    /// <summary>
    /// When the ingestion table first inserted the record's row (SQLFlow's <c>InsertedDate_DW</c>), as the last plan that
    /// read the row saw it: the row's arrival, which later changes never move. Null when the table does not carry it, or
    /// when no plan has read the row since the ledger began keeping it.
    /// </summary>
    public DateTime? SourceInsertedUtc { get; init; }

    public string? MetadataHash { get; init; }

    public string? PayloadHash { get; init; }

    /// <summary>The newest modified time among the payload files the delivered payload was sent from.</summary>
    public DateTime? PayloadModifiedUtc { get; init; }

    public string? TargetId { get; init; }

    /// <summary>
    /// The OSDU id the record claimed for its flow when it first queued a document, or when an operator adopted the record
    /// OSDU held there; null for a record that was only ever held. Only a claimed id is this flow's to write, read back or
    /// remove. Written by the ledger, never by a caller.
    /// </summary>
    public string? ClaimedTargetId { get; init; }

    /// <summary>
    /// When an operator gave the record a new OSDU id because OSDU lost the one it claimed: the record is then written under
    /// <see cref="ClaimedTargetId"/>, not the id its mapping makes from its key. Null for a record whose id is its mapping's.
    /// </summary>
    public DateTime? TargetIdAssignedUtc { get; init; }

    /// <summary>
    /// While the record is held because OSDU already holds a record at the OSDU id its mapping makes from its key that no
    /// record of any ledger claimed: that id, as the hold found it (<see cref="ILedgerRecords.RequestAdoptionAsync"/> asks such a
    /// record to adopt the record there). The intake sets it on the hold; the ledger clears it when the record claims an id
    /// or is held for another reason. A record held so before the hold kept the id has none, and is told by its hold's
    /// message (<see cref="OccupiedHolds"/>).
    /// </summary>
    public string? OccupiedTargetId { get; init; }

    /// <summary>The version OSDU held at <see cref="OccupiedTargetId"/> when the record was held for it.</summary>
    public long? OccupiedTargetVersion { get; init; }

    /// <summary>
    /// When an operator asked the record to adopt the record OSDU holds at the id its key makes; the next run that finds one
    /// there claims it rather than holding the record. Null otherwise, and once the record claims an id.
    /// </summary>
    public DateTime? AdoptRequestedUtc { get; init; }

    /// <summary>
    /// The registered access restriction whose groups replaced the record's ACL in OSDU (osdu/docs/reference/concepts/access-restriction.md),
    /// or null for a record whose access is its own. While it is set no run delivers the record, no release sends it and the
    /// ledger never forgets it; only lifting the restriction gives the record its access back.
    /// </summary>
    public int? RestrictionId { get; init; }

    /// <summary>When OSDU confirmed the restriction; null while it is asked and OSDU's answer is not known.</summary>
    public DateTime? RestrictedUtc { get; init; }

    /// <summary>The ACL OSDU held before the restriction, as JSON, which lifting it writes back.</summary>
    public string? RestrictedFromAcl { get; init; }

    /// <summary>Whether the record's access in OSDU is restricted, or asked to be: the ledger keeps it out of every run until it is lifted.</summary>
    public bool Restricted => RestrictionId is not null;

    /// <summary>
    /// The registered access restriction whose groups OSDU holds the record under, restricted until complete, because values its
    /// mapping marks <c>$restrictWhenMissing</c> were missing from the document OSDU holds
    /// (osdu/docs/reference/flow/mapping-restricted-access.md); null while OSDU holds it with the access its mapping gives. It holds
    /// nothing off, unlike <see cref="RestrictionId"/>: every run plans the record as any other, and the delivery of a document
    /// with every such value gives the record its own access back. Written by the ledger when a try that sent the document lands,
    /// never by a caller.
    /// </summary>
    public int? IncompleteRestrictionId { get; init; }

    /// <summary>When OSDU first held the record restricted until complete, kept through the deliveries that kept it so; null while it is not.</summary>
    public DateTime? IncompleteSinceUtc { get; init; }

    /// <summary>What the document OSDU holds goes without (<see cref="DocumentAccess.MissingText"/>), one value to a line; null while not restricted until complete.</summary>
    public string? IncompleteMissing { get; init; }

    /// <summary>The issue signature of <see cref="IncompleteMissing"/>: records restricted for the same values share it. Null while not restricted until complete.</summary>
    public long? IncompleteHash { get; init; }

    /// <summary>Whether OSDU holds the record restricted until complete (<see cref="IncompleteRestrictionId"/>).</summary>
    public bool RestrictedUntilComplete => IncompleteRestrictionId is not null;

    public long? TargetVersion { get; init; }

    public RecordStatus Status { get; init; } = RecordStatus.Pending;

    public DateTime? LastDeliveredUtc { get; init; }

    public DateTime? LastVerifiedUtc { get; init; }

    public VerifyOutcome? LastVerifyOutcome { get; init; }

    /// <summary>The token of the lease the record is being delivered under, while it is.</summary>
    public string? LeaseOwner { get; init; }

    /// <summary>When that lease runs out unless its worker renews it; the lease holds it, not the record.</summary>
    public DateTime? LeaseExpiresUtc { get; init; }

    public Guid? LastSubmissionId { get; init; }

    /// <summary>The platform run that queued or held the pending work, for the attempt it writes.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Attempts made for the pending work; reset when new work is queued.</summary>
    public int AttemptCount { get; init; }

    public DateTime? NextAttemptUtc { get; init; }

    /// <summary>Redacted message of the last failure, or the hold reason.</summary>
    public string? LastError { get; init; }

    /// <summary>
    /// Where the rendered document waiting to be delivered sits in the submission's work batches
    /// (batch:offset:length, see <see cref="Storage.DocumentRef"/>), and the hashes it will establish.
    /// </summary>
    public string? PendingDocumentRef { get; init; }

    /// <summary>The work batch the pending document was written in.</summary>
    public int? WorkBatch { get; init; }

    /// <summary>
    /// The identifiers the target returned for what it currently holds (record id and version, dataset ids and
    /// file sources, a workflow run id), as a JSON object merged step by step (osdu/docs/reference/concepts/protocols.md,
    /// Steps and returned values).
    /// </summary>
    public string? TargetStateJson { get; init; }

    /// <summary>
    /// The steps of the pending delivery that already completed and what they returned, so a retry resumes after
    /// them instead of repeating an upload (a JSON object keyed by step name).
    /// </summary>
    public string? PendingStepJson { get; init; }

    public string? PendingRenderContext { get; init; }

    /// <summary>The ingestion fingerprint of the pending work, or of the state a held/failed/deleted record was left in.</summary>
    public string? PendingSourceFingerprint { get; init; }

    /// <summary>The business version of the pending work, or of the state a held/failed/deleted record was left in.</summary>
    public DateTime? PendingSourceModifiedUtc { get; init; }

    /// <summary>The ingestion file the queued version came from, or the one a held record was left at.</summary>
    public string? PendingSourceFileName { get; init; }

    /// <summary>The row of that file.</summary>
    public long? PendingSourceRowNumber { get; init; }

    /// <summary>When the ingestion table last updated that row.</summary>
    public DateTime? PendingSourceUpdatedUtc { get; init; }

    /// <summary>
    /// When the ingestion table marked the row the pending state was read from deleted, for a record held because of it:
    /// the hold's attempt records the moment under <see cref="AttemptPhases.SourceDeleted"/>. The attempt keeps it, not
    /// the record.
    /// </summary>
    public DateTime? PendingSourceDeletedUtc { get; init; }

    public string? PendingMetadataHash { get; init; }

    public string? PendingPayloadHash { get; init; }

    /// <summary>The payload watermark of the pending work, when it carries a payload.</summary>
    public DateTime? PendingPayloadModifiedUtc { get; init; }

    /// <summary>Where the pending payload's files are listed from (folder and pattern), or null when no payload is pending.</summary>
    public string? PendingPayloadLocation { get; init; }

    public bool PendingMetadata { get; init; }

    public bool PendingPayload { get; init; }

    /// <summary>
    /// The OSDU ids the pending document refers to through the properties its template declares relationships for,
    /// with the property holding each. A record waits for the ones another record of the ledger holds and has not
    /// delivered.
    /// </summary>
    public IReadOnlyList<RecordReference> PendingReferences { get; init; } = [];

    /// <summary>While the record is <see cref="RecordStatus.Waiting"/>: the OSDU id of the record it waits for.</summary>
    public string? WaitingFor { get; init; }

    /// <summary>
    /// Set when the record was held, failed, deleted or reverted and not released since. A blocked record is planned again
    /// only when its source changes (fingerprint moved) or an operator releases it.
    /// </summary>
    public bool Blocked { get; init; }

    /// <summary>
    /// While the record is blocked, held or failed: the problem that keeps it so (<see cref="ProblemSignature"/>), which
    /// every record refused for the same reason shares; null otherwise, and until the ledger has sorted a record blocked
    /// before it kept problems. Written by the ledger from the record's error, never by a caller.
    /// </summary>
    public long? ProblemHash { get; init; }

    /// <summary>
    /// What the last check of the record's document against its schema came to, written by the gate before a record is
    /// sent (<see cref="Validation.ValidationOutcomes"/>: valid, invalid, unverified); null until a document was checked.
    /// </summary>
    public string? ValidationOutcome { get; init; }

    /// <summary>How many problems that check found.</summary>
    public long? ValidationProblems { get; init; }

    /// <summary>When that check was made.</summary>
    public DateTime? ValidatedUtc { get; init; }

    /// <summary>
    /// How many judgements of the mapping's assertions failed on the document that check read
    /// (osdu/docs/reference/flow/mapping-assertions.md); null when its mapping states none or no document was checked.
    /// </summary>
    public long? AssertionFailures { get; init; }

    /// <summary>The metadata hash of the pending document an operator's release accepted as it is, whatever its verdict.</summary>
    public string? AcceptedMetadataHash { get; init; }

    /// <summary>Whether the pending document is the one an operator's release accepted.</summary>
    public bool PendingAccepted => PendingMetadataHash is not null && string.Equals(PendingMetadataHash, AcceptedMetadataHash, StringComparison.Ordinal);

    /// <summary>
    /// The cache values this record was built from, as the id of the set it shares with every record that read the
    /// same values. A plan compares it against the gated sets to know whether an unapproved cache change is holding
    /// this record back.
    /// </summary>
    public long? CacheSetId { get; init; }

    /// <summary>
    /// The record's parent (osdu/docs/reference/flow/mapping-parent.md): the OSDU id of the record its render read sections
    /// of, or would have, had the platform held it; null for a record whose mapping reads no parent.
    /// </summary>
    public string? ParentId { get; init; }

    /// <summary>The version of the parent the document OSDU holds was rendered from; null when it read none.</summary>
    public long? ParentVersion { get; init; }

    /// <summary>The version of the parent the pending work was rendered from; null when it read none.</summary>
    public long? PendingParentVersion { get; init; }

    /// <summary>
    /// For a record whose render looked for its children (osdu/docs/reference/flow/mapping-child.md): the unique segment their
    /// OSDU ids share with the id the record's key gives; null for a record whose mapping links none.
    /// </summary>
    public string? ChildLinkKey { get; init; }

    /// <summary>
    /// The steps of the flow's <c>enrich:</c> that held and were set aside on the record's last render
    /// (osdu/docs/reference/flow/enrich-holds.md), comma-separated in the order they render; null when none was.
    /// </summary>
    public string? HeldSteps { get; init; }

    /// <summary>
    /// The rules the record's last hold at its render was rendered under (<see cref="Snapshots.RenderContext.RulesHash"/>): a
    /// plan under other rules renders the record again while it is held keeping no document. Null for a record never held at
    /// its render, and for one held before the ledger kept it (osdu/docs/reference/concepts/record-lifecycle.md, A record held
    /// at its render).
    /// </summary>
    public string? HeldRulesHash { get; init; }

    /// <summary>When the ledger asked for the record to be planned again (a redeliver, a release, a cache rollout, a parent or a child that landed); null once a plan took it.</summary>
    public DateTime? PlanRequestedUtc { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }

    /// <summary>
    /// For a record a plan holds or queues: the ledger row the plan decided it against (<see cref="PlanBasis"/>). Null for
    /// a write that is not a plan's.
    /// </summary>
    public PlanBasis? Basis { get; init; }

    /// <summary>A rendered document is queued for the record (waiting for a record it refers to, or due), or being delivered right now.</summary>
    public bool HasPendingWork => PendingDocumentRef is not null && Status is RecordStatus.Pending or RecordStatus.Delivering or RecordStatus.Waiting;

    /// <summary>The origin of the version OSDU holds.</summary>
    public RecordOrigin Origin => new(SourceFileName, SourceRowNumber, SourceUpdatedUtc);

    /// <summary>The origin of the queued version, or of the one a held record was left at.</summary>
    public RecordOrigin PendingOrigin => new(PendingSourceFileName, PendingSourceRowNumber, PendingSourceUpdatedUtc);
}

/// <summary>
/// One delivery try, append-only (osdu/docs/reference/concepts/ledger.md, <c>osdu.Attempt</c>: every try, and every
/// decision that changed the record).
/// </summary>
public sealed record AttemptRecord
{
    public long AttemptId { get; init; }

    public required DeliveryKey DeliveryKey { get; init; }

    public Guid? SubmissionId { get; init; }

    /// <summary>The platform run the attempt happened in, when it did.</summary>
    public Guid? RunId { get; init; }

    public required string Worker { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required DateTime CompletedUtc { get; init; }

    public required AttemptOutcome Outcome { get; init; }

    /// <summary>What the attempt delivered: metadata, payload, both, delete or nothing.</summary>
    public required string Phase { get; init; }

    public string? MetadataHash { get; init; }

    public string? PayloadHash { get; init; }

    public long? TargetVersion { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// What the try did and what the target answered, step by step: a JSON object with a <c>steps</c> array (name,
    /// started and completed times, status, the values returned) and the values the record now carries.
    /// </summary>
    public string? ResultJson { get; init; }

    /// <summary>The work batch the try belonged to, when it ran from one.</summary>
    public int? WorkBatch { get; init; }

    /// <summary>
    /// For a delivered try: the kinds of the inputs that made the record render and be sent (its reason's triggers, which the
    /// result keeps under <c>reason</c>). Null for every other attempt, and for a delivery made before the ledger kept reasons.
    /// </summary>
    public Planning.TriggerKinds? Triggers { get; init; }

    /// <summary>The ingestion file the attempt's document was built from.</summary>
    public string? SourceFileName { get; init; }

    /// <summary>The row of that file.</summary>
    public long? SourceRowNumber { get; init; }

    /// <summary>When the ingestion table last updated that row.</summary>
    public DateTime? SourceUpdatedUtc { get; init; }

    /// <summary>The version of the record's parent the attempt's document was rendered from; null when it read none.</summary>
    public long? ParentVersion { get; init; }

    /// <summary>When the ingestion table marked that row deleted, for the hold of a deleted row; null otherwise.</summary>
    public DateTime? SourceDeletedUtc { get; init; }
}

/// <summary>
/// The ledger row a plan decided a record against: when the row last changed, and the request to plan the record again it
/// carried. A plan's decision is written only while the row is still the one it read: a record released, redelivered,
/// restored or delivered in between is asked to be planned again instead, and only the request the plan read is taken as
/// answered, so an operator's request made while a run plans is never lost.
/// </summary>
/// <param name="UpdatedUtc">When the row the plan read last changed; null when the ledger held no row for the record.</param>
/// <param name="PlanRequestedUtc">The request to plan the record again the row carried; null when it carried none.</param>
public readonly record struct PlanBasis(DateTime? UpdatedUtc, DateTime? PlanRequestedUtc)
{
    /// <summary>The basis of a plan that read <paramref name="state"/> (null when the ledger held no row).</summary>
    public static PlanBasis Of(RecordState? state) => state is null ? default : new(state.UpdatedUtc, state.PlanRequestedUtc);
}

/// <summary>A record the ledger asked to be planned again, with the key tuple a key-scoped read finds it by.</summary>
public sealed record PlanRequestedRecord(DeliveryKey DeliveryKey, string? SourceKeyJson, DateTime RequestedUtc)
{
    /// <summary>
    /// True for a record deleted from the ledger, whose request the line kept of it carries
    /// (<see cref="ILedgerRemovals.PurgeRecordsAsync"/>): a pass that plans its row makes it a new record, and
    /// <see cref="ILedgerRemovals.AnswerPurgedRequestsAsync"/> answers it.
    /// </summary>
    public bool Purged { get; init; }
}

/// <summary>The life of one work batch: queued by the intake, claimed by a drain, done or failed.</summary>
public enum WorkBatchStatus
{
    Queued,
    Running,
    Done,
    Failed,
}

/// <summary>
/// A range of a submission's work batch numbers, from <see cref="First"/> up to, but not including, <see cref="Beyond"/>:
/// the batches a part of a split delivery plans its key slices into, each slice's in a namespace of its own
/// (osdu/docs/reference/concepts/submissions.md, Many nodes). The run claims and waits for the records of these batches
/// alone, whose files are on its own node.
/// </summary>
public readonly record struct BatchRange
{
    public BatchRange(int first, int beyond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(beyond, first);
        First = first;
        Beyond = beyond;
    }

    public int First { get; }

    public int Beyond { get; }

    public bool Contains(int batch) => batch >= First && batch < Beyond;

    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"batches {First} to {Beyond - 1}");
}

/// <summary>
/// One work batch of a submission (osdu/docs/reference/concepts/submissions.md, Work batches): a file of rendered
/// documents and its progress.
/// </summary>
public sealed record WorkBatchState
{
    public required Guid SubmissionId { get; init; }

    public required Guid FlowId { get; init; }

    public required int Index { get; init; }

    public required string Location { get; init; }

    public int RecordCount { get; init; }

    public WorkBatchStatus Status { get; init; } = WorkBatchStatus.Queued;

    /// <summary>The token of the lease the batch is drained under, while it is.</summary>
    public string? LeaseOwner { get; init; }

    /// <summary>When that lease runs out unless its worker renews it.</summary>
    public DateTime? LeaseExpiresUtc { get; init; }

    /// <summary>The platform run that is draining, or drained, the batch.</summary>
    public Guid? RunId { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime? StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public long Delivered { get; init; }

    public long Held { get; init; }

    public long Failed { get; init; }

    /// <summary>Records left pending with a retry time when the batch closed.</summary>
    public long Retrying { get; init; }

    /// <summary>Records the batch's claim found waiting for a record they refer to, and did not send.</summary>
    public long Waiting { get; init; }

    public string? Error { get; init; }
}

/// <summary>How a free-text search is applied. Prefix search uses the indexes and answers in milliseconds.</summary>
public enum SearchMode
{
    Prefix,
    Contains,
}

/// <summary>A record listing: filter, search, sort and page.</summary>
public sealed record RecordQuery
{
    public RecordStatus? Status { get; init; }

    /// <summary>A delivery key, a target id, or a prefix of the label, the source key or the origin file name.</summary>
    public string? Search { get; init; }

    public SearchMode Mode { get; init; } = SearchMode.Prefix;

    /// <summary>
    /// Only records whose last submission is this one: the one that last planned them, whether it delivered them or
    /// found them unchanged. A later submission that touches a record moves it on, so this is "what the submission
    /// left behind", not "what it delivered"; for that, see <see cref="DeliveredBySubmissionId"/>.
    /// </summary>
    public Guid? SubmissionId { get; init; }

    /// <summary>
    /// Only records this submission delivered, resolved through the delivered attempts it wrote. A record keeps that
    /// attempt however many submissions touch it afterwards, so this is the set an operator means by "the batch we
    /// ran": what it put into OSDU, findable and removable after the records have moved on.
    /// </summary>
    public Guid? DeliveredBySubmissionId { get; init; }

    /// <summary>
    /// With <see cref="DeliveredBySubmissionId"/> or <see cref="RunId"/>: only the records it delivered for this kind of
    /// trigger, read from the delivered attempts' <c>Triggers</c> (osdu/docs/reference/concepts/delivery-reasons.md): the
    /// records a submission's or a run's count of one cause stands for. A record counts under every kind its reason names.
    /// </summary>
    public Planning.TriggerKinds? DeliveredFor { get; init; }

    /// <summary>
    /// Only records the given platform run touched, resolved through the attempts that run wrote. Records carry no
    /// run of their own: a record is delivered by many runs over its life, and the attempt is the thing that
    /// belongs to one.
    /// </summary>
    public Guid? RunId { get; init; }

    /// <summary>
    /// The parts <see cref="RunId"/> split its delivery into when it spread one over several nodes
    /// (osdu/docs/reference/concepts/submissions.md, Many nodes). Each part writes its records' attempts under its own run
    /// id and the run that split them delivers no range itself, so the records it touched are those its own attempts and
    /// its parts' name. The ledger keeps no run of its own: the control plane reads the parts from the platform's runs.
    /// Empty for a run that was not split, and for a part, whose records are its own attempts' alone.
    /// </summary>
    public IReadOnlyList<Guid> RunParts { get; init; } = [];

    /// <summary>Only delivered records whose last verify found drift or a missing record.</summary>
    public bool Drifted { get; init; }

    /// <summary>
    /// Only records whose last render set a step of <c>enrich:</c> aside on its hold (osdu/docs/reference/flow/enrich-holds.md),
    /// read from the held-steps index.
    /// </summary>
    public bool StepHeld { get; init; }

    /// <summary>
    /// Only records whose access in OSDU is restricted, or asked to be (osdu/docs/reference/concepts/access-restriction.md),
    /// read from the restricted records' index.
    /// </summary>
    public bool Restricted { get; init; }

    /// <summary>
    /// Only records OSDU holds restricted until complete (osdu/docs/reference/flow/mapping-restricted-access.md), read from the
    /// index that holds them alone.
    /// </summary>
    public bool Incomplete { get; init; }

    /// <summary>
    /// Only the records restricted until complete for one set of missing values: those whose
    /// <see cref="RecordState.IncompleteHash"/> is this one, as <see cref="Problem"/> narrows to one issue. Read from the same index.
    /// </summary>
    public long? Missing { get; init; }

    /// <summary>
    /// Only records whose access in OSDU is restricted either way: by an operator (<see cref="Restricted"/>) or until complete
    /// (<see cref="Incomplete"/>), each record once, read from the two indexes that hold them. What a flow's
    /// <see cref="FlowStats.AccessRestricted"/> counts.
    /// </summary>
    public bool AccessRestricted { get; init; }

    /// <summary>Only the blocked records one problem keeps blocked (<see cref="ProblemSignature"/>), read from the problem index.</summary>
    public long? Problem { get; init; }

    /// <summary>
    /// Filters on whether the ledger has ever recorded a delivery for the record. A record is given its OSDU id
    /// when it is planned, not when it lands, so an id is no evidence that OSDU holds anything: this is what
    /// separates the records a removal will really take from the ones it will find were never there.
    /// </summary>
    public bool? EverDelivered { get; init; }

    public int Max { get; init; } = 100;

    /// <summary>Where the page starts; inside the first <see cref="RecordListing.CountLimit"/> records of the order.</summary>
    public int Offset { get; init; }
}

/// <summary>
/// What bounds a record listing, so that every page, count and search reads a bounded part of the ledger however many
/// records a flow holds.
/// </summary>
public static class RecordListing
{
    /// <summary>
    /// How far a listing counts and how deep it pages. A total up to this is exact and a larger one is a floor; the
    /// records past it are reached by narrowing the filter. It equals the removal selection limit, so a listing's count
    /// is exact whenever the records it matches could be removed together.
    /// </summary>
    public const int CountLimit = RemovalLimits.MaxSelection;

    /// <summary>
    /// The most records a contains search reads. A contains term has no index, so the rest of the filter must leave at
    /// most this many records for it to scan; a broader filter is refused with <see cref="RecordQueryTooBroadException"/>.
    /// </summary>
    public const int ContainsScanLimit = 100_000;

    /// <summary>
    /// Candidates the lookup across every flow takes from each identity index before ordering them, and how far back the
    /// recency listing that answers an empty search box reaches.
    /// </summary>
    public const int LookupCandidateLimit = 1_000;
}

/// <summary>A record listing that cannot be answered inside the bounds of <see cref="RecordListing"/>; the message says how to narrow it.</summary>
public sealed class RecordQueryTooBroadException : DeliveryException
{
    public RecordQueryTooBroadException(string message)
        : base(message)
    {
    }

    public RecordQueryTooBroadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The kinds of flow a ledger belongs to.</summary>
public static class LedgerKinds
{
    public const string Delivery = "delivery";

    public const string Retrieval = "retrieval";

    public const string Assertion = "assertion";

    public const string Dimension = "dimension";

    public const string Inventory = "inventory";

    public const string Download = "download";
}
