namespace SqlFlow.Delivery.Ledger;

/// <summary>One cache change and what happens about it, covering every record built from the value that moved.</summary>
/// <summary>
/// The records a submission delivered (<see cref="Delivered"/>), how many under each kind of trigger (<see cref="ByTrigger"/>,
/// in <see cref="Planning.ReasonKinds.All"/>'s order, kinds no record names left out), and how many were delivered before the
/// ledger kept reasons (<see cref="NotRecorded"/>).
/// </summary>
public sealed record SubmissionTriggerCounts(long Delivered, long NotRecorded, IReadOnlyList<(string Kind, long Records)> ByTrigger);

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds submissions and their work batches: registered, updated,
/// held, taken over and listed. A caller that uses only this part takes it rather than the whole ledger;
/// <see cref="OsduLedger"/> implements every part.
/// </summary>
public interface ILedgerSubmissions
{
    Task<SubmissionState?> GetSubmissionAsync(Guid submissionId, CancellationToken ct = default);

    /// <summary>Registers a submission. Returns the existing one when the id was seen before (idempotent).</summary>
    Task<(SubmissionState Submission, bool Created)> RegisterSubmissionAsync(SubmissionState submission, CancellationToken ct = default);

    Task UpdateSubmissionAsync(SubmissionState submission, CancellationToken ct = default);

    /// <summary>
    /// A flow's submissions, newest first, or when <paramref name="flowId"/> is null, those of every flow of
    /// <paramref name="partition"/>, or of every partition when that is null too.
    /// </summary>
    Task<IReadOnlyList<SubmissionState>> ListSubmissionsAsync(Guid? flowId, int max, string? partition = null, CancellationToken ct = default);

    /// <summary>
    /// The records a submission delivered, counted by what made them render and be sent (each delivered attempt's triggers,
    /// <c>osdu.Attempt.Triggers</c>; osdu/docs/reference/concepts/delivery-reasons.md): a record
    /// counts once under each kind its reason names, so the counts may add up to more than the records delivered. One read of
    /// the submission's delivered attempts through their index.
    /// </summary>
    Task<SubmissionTriggerCounts> CountDeliveredTriggersAsync(Guid submissionId, CancellationToken ct = default);

    /// <summary>
    /// The records the platform runs <paramref name="runIds"/> delivered, counted as <see cref="CountDeliveredTriggersAsync"/>
    /// counts a submission's: what a run (with the parts it split its delivery into) was delivering for. One read of their
    /// delivered attempts through the attempts' run index.
    /// </summary>
    Task<SubmissionTriggerCounts> CountRunDeliveredTriggersAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct = default);

    /// <summary>
    /// The completed or failed submissions of the flow, other than those <paramref name="except"/> names, that still hold
    /// records due for delivery with their rendered documents: records released back to pending after their run was over,
    /// and the records a stopped run handed back untried when it closed its submission as failed. At most
    /// <paramref name="max"/>; a caller sending all of them asks again naming the ones it sent. It reads the flow's pending
    /// records, which a run leaves few of once its own are sent.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListSettledSubmissionsWithDueWorkAsync(Guid flowId, IReadOnlyCollection<Guid> except, DateTime nowUtc, int max, CancellationToken ct = default);

    /// <summary>
    /// The submissions of the flow no run may be working on any more, other than those <paramref name="except"/> names, at
    /// most <paramref name="max"/>: first those left unsettled (received, planned or running) with no lease that has not run
    /// out holding any of their work, the oldest first; then the settled ones still holding due records
    /// (<see cref="ListSettledSubmissionsWithDueWorkAsync"/>). Whether a run still works on one is not decided here: taking
    /// it over is what answers that (<see cref="TakeOverSubmissionAsync"/>). A caller walking all of them asks again naming
    /// the ones it took.
    /// </summary>
    Task<IReadOnlyList<SubmissionState>> ListUnattendedSubmissionsAsync(Guid flowId, IReadOnlyCollection<Guid> except, DateTime nowUtc, int max, CancellationToken ct = default);

    /// <summary>
    /// Whether a lease of the flow that has not run out at <paramref name="nowUtc"/> holds work of the submission: one of its
    /// batches, or one of its records under a lease of the whole flow. Such a lease is a worker sending it now.
    /// </summary>
    Task<bool> IsSubmissionLeasedAsync(Guid flowId, Guid submissionId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// The submissions of the flow that platform run <paramref name="runId"/> registered and that are not settled yet
    /// (received, planned or running), the newest first: what an attempt of the run that was interrupted left, which the
    /// run's next attempt resumes rather than registering another.
    /// </summary>
    Task<IReadOnlyList<SubmissionState>> ListUnsettledSubmissionsOfRunAsync(Guid flowId, Guid runId, CancellationToken ct = default);

    /// <summary>
    /// The submissions platform run <paramref name="runId"/> registered, in any flow and partition, the newest first, as a run
    /// is looked up by its id alone (IX_Submission_RunId).
    /// </summary>
    Task<IReadOnlyList<SubmissionState>> ListSubmissionsOfRunAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Holds submission <paramref name="submissionId"/> for the run working on it, until the hold is disposed: a shared,
    /// session-owned application lock on the module's database, on a connection of its own. Any number of runs hold one
    /// submission at once (a fan-out's coordinator and its members do); a process that stops frees its holds with its
    /// connections. The hold waits a short while for a run taking the submission over to finish taking it, and is refused,
    /// naming the submission, when one does not.
    /// </summary>
    /// <exception cref="DeliveryException">The submission could not be held in time.</exception>
    Task<SubmissionHold> HoldSubmissionAsync(Guid submissionId, CancellationToken ct = default);

    /// <summary>
    /// Takes over submission <paramref name="submissionId"/> when no run holds it any more (<see cref="HoldSubmissionAsync"/>):
    /// the run that registered it, its fan-out members and any run naming it have all ended. The answer is a hold of the
    /// caller's own, as a run working on the submission has; null when a run still holds it, which is then left to that run.
    /// Two runs taking one submission over never both get it while either holds it.
    /// </summary>
    Task<SubmissionHold?> TakeOverSubmissionAsync(Guid submissionId, CancellationToken ct = default);

    /// <summary>Registers a work batch the intake wrote (idempotent on submission and index).</summary>
    Task AddWorkBatchAsync(WorkBatchState batch, CancellationToken ct = default);

    Task<IReadOnlyList<WorkBatchState>> ListWorkBatchesAsync(Guid submissionId, int max, int offset, CancellationToken ct = default);

    /// <summary>
    /// The highest work batch number <paramref name="submissionId"/> has registered from <paramref name="first"/> up to, but
    /// not including, <paramref name="beyond"/>: one share's namespace of batch numbers. Null when it has none there. A plan of a
    /// share that was planned before writes its batches after it, so no batch file a record already points into is
    /// written again.
    /// </summary>
    Task<int?> LastWorkBatchIndexAsync(Guid submissionId, int first, int beyond, CancellationToken ct = default);

    /// <summary>
    /// The numbers of the work batches records of <paramref name="submissionId"/> are still queued in, in order: each a batch
    /// that a record pending, being delivered or waiting for a record it refers to points into. Empty once every record of
    /// the submission is settled. The run that closes a split delivery asks, to find the records its parts left queued in
    /// batch files on their own nodes (osdu/docs/reference/concepts/submissions.md, Work queued where a run cannot read it).
    /// </summary>
    Task<IReadOnlyList<int>> ListQueuedWorkBatchesAsync(Guid flowId, Guid submissionId, CancellationToken ct = default);

    Task<long> CountWorkBatchesAsync(Guid submissionId, WorkBatchStatus? status, CancellationToken ct = default);
}
