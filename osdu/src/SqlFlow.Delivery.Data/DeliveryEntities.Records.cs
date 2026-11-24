namespace SqlFlow.Delivery.Data;

// The records of the ledger (osdu/docs/reference/concepts/ledger.md, The tables): each record's current state, its
// append-only attempts, the values it is found by, what is kept of a record deleted from the ledger, and what its
// deliveries created in OSDU.

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

    /// <summary>
    /// The estate commit the run that planned <see cref="RenderContext"/> executed, when it was pinned to one: what a delivery's
    /// reason quotes beside the mapping fingerprint the record's document was rendered with
    /// (osdu/docs/reference/concepts/delivery-reasons.md). Written with the render context,
    /// cleared with it; null when that run knew no commit.
    /// </summary>
    public string? RenderCommit { get; set; }

    /// <summary>
    /// The paths of the document OSDU holds of the record, each with a 64-bit hash of its value, in the compact binary form
    /// <c>RenderPaths.Encode</c> writes (about 25 bytes a path; an array is one path): what the next delivery compares its
    /// render with to name the paths it changes, without the ledger keeping a value. Written when a try that sends the
    /// document lands, or when a plan finds a record whose paths were never kept rendering exactly what OSDU holds; cleared
    /// when OSDU no longer holds that version (a removal, a reversal, a step back). Null for a record delivered before.
    /// </summary>
    public byte[]? RenderPaths { get; set; }

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
    /// The OSDU id this record claimed for its flow when it first queued a document, or when an operator adopted the record
    /// OSDU held there (<see cref="OccupiedTargetId"/>), kept for good afterwards. One OSDU record belongs to one flow: no
    /// other flow's record can claim the same id. A record that was only ever held, and never adopted, has claimed nothing.
    /// </summary>
    public string? ClaimedTargetId { get; set; }

    /// <summary>
    /// When an operator gave the record a new OSDU id because OSDU lost the one it claimed (an inventory's repair): from then
    /// on the record is written under <see cref="ClaimedTargetId"/> rather than the id its mapping makes from its key. Null
    /// for a record whose id is the one its mapping makes.
    /// </summary>
    public DateTime? TargetIdAssignedUtc { get; set; }

    /// <summary>
    /// While the record is held because OSDU already holds a record at the OSDU id its mapping makes from its key, and no
    /// record of any ledger claimed that id (another system wrote it, or a ledger this database does not hold): that id.
    /// An operator's adoption makes the record claim it (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a
    /// record OSDU already holds). Null for any other record, and once the record claims an id or is held for another reason.
    /// </summary>
    public string? OccupiedTargetId { get; set; }

    /// <summary>The version OSDU held at <see cref="OccupiedTargetId"/> when the record was held for it; null with it.</summary>
    public long? OccupiedTargetVersion { get; set; }

    /// <summary>
    /// When an operator asked the record to adopt the record OSDU holds at the OSDU id its key makes: the next run that asks
    /// OSDU about the id and finds a record there claims it, at the version OSDU holds then, rather than holding the record
    /// (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already holds). Cleared once the record
    /// claims an id. Null for any other record.
    /// </summary>
    public DateTime? AdoptRequestedUtc { get; set; }

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

    /// <summary>
    /// The rules the record's last hold at its render was rendered under: the hash of its render context without the cache
    /// version (the mapping, its fingerprint, the template, the parameters, the enrichments and the pinned system
    /// properties). A plan that reads the record's row under other rules renders the record again while it is held keeping
    /// no document, so its error is the one the rules it renders with now give
    /// (osdu/docs/reference/concepts/record-lifecycle.md, A record held at its render). Null for a record never held at its
    /// render, and for one held before the ledger kept it, whose rules are not known; a record no longer held at its render
    /// is not read by it.
    /// </summary>
    public string? HeldRulesHash { get; set; }

    /// <summary>
    /// The record's parent (osdu/docs/reference/flow/mapping-parent.md): the OSDU id, without a version, of the record of the
    /// kind its template is linked to whose sections its render reads; null for a record whose mapping reads no parent. Set
    /// when work is queued or the record is held, so a parent that lands asks the records that read it to be planned again.
    /// </summary>
    public string? ParentId { get; set; }

    /// <summary>The version of the parent the document OSDU holds was rendered from; null when it read none.</summary>
    public long? ParentVersion { get; set; }

    /// <summary>The version of the parent the pending work was rendered from; null when it read none.</summary>
    public long? PendingParentVersion { get; set; }

    /// <summary>
    /// For a record whose render looked for its children (osdu/docs/reference/flow/mapping-child.md): the unique segment their
    /// OSDU ids share with the id the record's key gives, everything after the entity type; null for a record whose mapping
    /// links none. Set as the render that looked is recorded (work queued, a hold, a render that sent nothing), so a child
    /// that lands asks the records that link it to be planned again.
    /// </summary>
    public string? ChildLinkKey { get; set; }

    /// <summary>
    /// The steps of the flow's <c>enrich:</c> that held and were set aside on the record's last render
    /// (osdu/docs/reference/flow/enrich-holds.md), comma-separated in the order they render: the record went ahead with what
    /// each added to its last delivery. Null when none was. Set as the render is recorded (work queued, a hold, a render that
    /// sent nothing), so the records a step holds in are found through their index.
    /// </summary>
    public string? HeldSteps { get; set; }

    /// <summary>
    /// The registered access restriction (<see cref="DeliveryAccessRestriction"/>) whose groups replaced the record's ACL in
    /// OSDU (osdu/docs/reference/concepts/access-restriction.md), or null for a record whose access is its own. Set before the
    /// ACL is patched, so a restriction whose answer was lost still keeps the record out of every run; while it is set, no
    /// run delivers the record, no release sends it, and the ledger never forgets it, until the restriction is lifted.
    /// </summary>
    public int? RestrictionId { get; set; }

    /// <summary>
    /// When OSDU confirmed the record's ACL was replaced with the restriction's groups; null while the restriction is asked
    /// and OSDU's answer is not known (a restriction asked again, or a lift, settles it), and for a record not restricted.
    /// </summary>
    public DateTime? RestrictedUtc { get; set; }

    /// <summary>
    /// The ACL OSDU held for the record before it was restricted, as <c>{"viewers":[...],"owners":[...]}</c>: what lifting the
    /// restriction writes back. Kept from the first time the restriction was asked, whatever OSDU answered then.
    /// </summary>
    public string? RestrictedFromAcl { get; set; }

    /// <summary>
    /// The registered access restriction (<see cref="DeliveryAccessRestriction"/>) whose groups OSDU holds the record under,
    /// restricted until complete, because values its mapping marks <c>$restrictWhenMissing</c> were missing from the document
    /// OSDU holds (osdu/docs/reference/flow/mapping-restricted-access.md); null while OSDU holds it with the access its mapping
    /// gives. Written when a try that sent the document lands, from the access the document went out under, and only then:
    /// what OSDU holds changes only when a document is sent. Unlike <see cref="RestrictionId"/> it holds nothing off: every run
    /// plans the record as any other, and the delivery of a document with every such value gives it its own access back.
    /// </summary>
    public int? IncompleteRestrictionId { get; set; }

    /// <summary>
    /// When OSDU first held the record restricted until complete, kept through the deliveries that kept it so; null while OSDU
    /// holds it with the access its mapping gives.
    /// </summary>
    public DateTime? IncompleteSinceUtc { get; set; }

    /// <summary>
    /// What the document OSDU holds goes without, each value on a line of its own as a hold would say it
    /// (<c>osdu.tags['dra.securityClassification']: dataset.Lra_securityClassification is empty</c>); null while the record is
    /// not restricted until complete. At most <see cref="DeliveryModel.MaxIncompleteMissingLength"/> characters, values past it
    /// counted on a last line.
    /// </summary>
    public string? IncompleteMissing { get; set; }

    /// <summary>
    /// The issue signature of <see cref="IncompleteMissing"/>, made as <see cref="ProblemHash"/> is made of an error: records
    /// restricted for the same values share it, whatever row each read. Null while the record is not restricted until complete.
    /// </summary>
    public long? IncompleteHash { get; set; }

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

    /// <summary>
    /// For a delivered try: the inputs whose move made the record render and be sent, as the flags of <c>TriggerKinds</c>
    /// (osdu/docs/reference/concepts/delivery-reasons.md), so a submission's deliveries are counted
    /// by them without reading their results. The reason itself is the result's <c>reason</c>. Null for every other attempt,
    /// and for a delivery made before the ledger kept reasons.
    /// </summary>
    public int? Triggers { get; set; }

    /// <summary>The work batch the try belonged to, when it ran from one.</summary>
    public int? WorkBatch { get; set; }

    /// <summary>The ingestion file the attempt's document was built from; kept here because the record's own origin
    /// columns move on with later versions.</summary>
    public string? SourceFileName { get; set; }

    /// <summary>
    /// The version of the record's parent the attempt's document was rendered from (osdu/docs/reference/flow/mapping-parent.md);
    /// null when its mapping reads no parent, or the platform held none.
    /// </summary>
    public long? ParentVersion { get; set; }

    /// <summary>The row of that file.</summary>
    public long? SourceRowNumber { get; set; }

    /// <summary>When the ingestion table last updated that row.</summary>
    public DateTime? SourceUpdatedUtc { get; set; }

    /// <summary>When the ingestion table marked that row deleted, for the hold of a deleted row; null otherwise.</summary>
    public DateTime? SourceDeletedUtc { get; set; }
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

    /// <summary>Where the token came from: identity, key, label, osdu, file or added, as the ledger's identity kinds name them.</summary>
    public string Kind { get; set; } = string.Empty;
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
