namespace SqlFlow.Delivery.Data;

// The work a flow plans and sends (osdu/docs/reference/concepts/submissions.md): its submissions and their work
// batches, the leases workers claim work under and the events they append under them, and the watermarks of a flow's
// incremental reads.

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

    /// <summary>
    /// A completion of a try that sent the record's document (osdu/docs/reference/flow/mapping-restricted-access.md): the access
    /// the document went out under, in <see cref="IncompleteRestrictionId"/>, <see cref="IncompleteMissing"/> and
    /// <see cref="IncompleteHash"/>, is the access OSDU holds the record under now. False for any other event, which leaves the
    /// record's access as it was.
    /// </summary>
    public bool AccessSent { get; set; }

    /// <summary>With <see cref="AccessSent"/>: the access restriction the sent document went out under, or null for its own access.</summary>
    public int? IncompleteRestrictionId { get; set; }

    /// <summary>With <see cref="AccessSent"/>: what the sent document goes without, as the record keeps it; null for its own access.</summary>
    public string? IncompleteMissing { get; set; }

    /// <summary>With <see cref="AccessSent"/>: the issue signature of <see cref="IncompleteMissing"/>; null for its own access.</summary>
    public long? IncompleteHash { get; set; }

    /// <summary>
    /// A completion that promotes the try's work: the paths of the document it sent (<see cref="DeliveryRecord.RenderPaths"/>),
    /// which the record keeps from now on; null for a try that sent no document, or whose work carried none.
    /// </summary>
    public byte[]? RenderPaths { get; set; }

    /// <summary>A completion that promotes the try's work: the estate commit its render context was planned at, which the record keeps with it.</summary>
    public string? RenderCommit { get; set; }

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

    /// <summary>The version of the record's parent the claimed work was rendered from; null when it read none.</summary>
    public long? ClaimParentVersion { get; set; }

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
