using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>An activity listing: filter and page.</summary>
public sealed record ActivityQuery
{
    /// <summary>The partition whose audit trail is read, by its data-partition-id; null reads every partition's.</summary>
    public string? Partition { get; init; }

    public Guid? FlowId { get; init; }

    /// <summary>
    /// The activities about one record: those made for it alone and, with <see cref="FlowId"/>, those made for many records
    /// that reached it (a release of a whole flow or of a problem's records), which name it among the records they changed.
    /// </summary>
    public Guid? DeliveryKey { get; init; }

    public Guid? SubmissionId { get; init; }

    public Guid? RunId { get; init; }

    public string? Kind { get; init; }

    public string? Actor { get; init; }

    public string? Outcome { get; init; }

    /// <summary>False leaves out the runs that changed nothing, true reads only them, and null reads every activity.</summary>
    public bool? Idle { get; init; }

    public DateTime? SinceUtc { get; init; }

    public DateTime? UntilUtc { get; init; }

    public int Max { get; init; } = 100;

    public int Offset { get; init; }
}

/// <summary>The statuses of a retrieval run.</summary>
public static class RetrievalStatus
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>One retrieval run as the ledger holds it: the window it covered, where its files went, and its outcome.</summary>
public sealed record RetrievalState
{
    public long RetrievalId { get; init; }

    public required Guid FlowId { get; init; }

    public required string FlowName { get; init; }

    public Guid? RunId { get; init; }

    public string Actor { get; init; } = "unknown";

    /// <summary>The kinds the run covered, comma separated.</summary>
    public required string Kinds { get; init; }

    /// <summary>The query as it ran, window included.</summary>
    public string? Query { get; init; }

    public string? WindowField { get; init; }

    public DateTime? WindowFrom { get; init; }

    /// <summary>The upper bound of the window: the next run's lower bound once this one is done.</summary>
    public DateTime? WindowTo { get; init; }

    public required string Location { get; init; }

    public string? ManifestLocation { get; init; }

    public string Status { get; init; } = RetrievalStatus.Running;

    public long Records { get; init; }

    public int Files { get; init; }

    /// <summary>Uncompressed bytes written.</summary>
    public long Bytes { get; init; }

    public DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// One operator or scheduler action, persisted for the audit trail: who did what, when, with which inputs, and
/// what came of it. Record-level history lives in attempts; this is the history of runs and interventions.
/// </summary>
public sealed record ActivityRecord
{
    public long ActivityId { get; init; }

    public required Guid FlowId { get; init; }

    /// <summary>
    /// The OSDU partition the row belongs to, as the ledger's directory names it: filled when the ledger reads the row, and
    /// null for a ledger not yet placed in a partition. A write takes the partition of its ledger identity, never this.
    /// </summary>
    public string? Partition { get; init; }


    public required string FlowName { get; init; }

    /// <summary>
    /// What was done. The operations of a run: deliver (a replan records one too), intake, drain, verify, sync, undo, reverse,
    /// delete-ledger. The interventions: release, redeliver, rerender, delete, restore-previous, purge. And probe (the target
    /// probe), remove-dimension and inventory-remove. A plan run records none: it changes nothing.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Who acted. For a run, its requester as SQLFlow records it: the token's subject for a run a person queued,
    /// schedule:&lt;name&gt; for one a schedule fired, cli:&lt;user&gt;@&lt;machine&gt; for a direct CLI run, unknown
    /// when none was recorded. For an intervention: user:&lt;subject&gt; through the control plane,
    /// cli:&lt;user&gt;@&lt;machine&gt; from the command line. service:schedule for the scheduled target probe.
    /// </summary>
    public required string Actor { get; init; }

    public required DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    /// <summary>running, completed, failed, cancelled.</summary>
    public string Outcome { get; init; } = "running";

    public string? ParametersJson { get; init; }

    public Guid? SubmissionId { get; init; }

    /// <summary>Set when the action targeted one record.</summary>
    public Guid? DeliveryKey { get; init; }

    /// <summary>
    /// The platform run the activity ran in, when it ran in one: every operation of a run, an intervention a run's payload
    /// asked for (a redelivery or bringing records up to date before a deliver), an inventory removal. Null for an
    /// intervention through the control plane, the GUI or the command line, which no run carries. A plan run records no
    /// activity, so no activity names one.
    /// </summary>
    public Guid? RunId { get; init; }

    public string? Summary { get; init; }

    /// <summary>Captured log lines (capped), for actions run from the service.</summary>
    public string? Log { get; init; }

    /// <summary>
    /// A run that completed having changed nothing: it planned, held, blocked and sent no record, and left none waiting.
    /// Only a completed activity is idle, and an intervention never is.
    /// </summary>
    public bool Idle { get; init; }
}

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds the audit trail of activities and retrievals, and what
/// retention prunes of it. A caller that uses only this part takes it rather than the whole ledger;
/// <see cref="OsduLedger"/> implements every part.
/// </summary>
public interface ILedgerActivities
{
    /// <summary>The ledger identities among <paramref name="flowIds"/> with at least one activity on the audit trail, of any
    /// kind or outcome. Like <see cref="ILedgerQueries.FlowsWithRecordsAsync"/> it asks whether each has one, never how many: one index seek
    /// per identity however long the trail.</summary>
    Task<IReadOnlySet<Guid>> FlowsWithActivitiesAsync(IReadOnlyCollection<Guid> flowIds, CancellationToken ct = default);

    /// <summary>Removes attempts older than the cut-off, keeping the latest attempt of every flow's record.</summary>
    Task<int> PruneAttemptsAsync(DateTime olderThanUtc, CancellationToken ct = default);

    /// <summary>How many attempts <see cref="PruneAttemptsAsync"/> would remove at the cut-off; changes nothing.</summary>
    Task<long> CountPrunableAttemptsAsync(DateTime olderThanUtc, CancellationToken ct = default);

    /// <summary>
    /// Clears the captured log of every activity that started before the cut-off and has finished, and answers how many it
    /// cleared. The activity stays: who did what, when, with which inputs, its outcome and summary. An activity still running
    /// keeps its log, whatever its age, since it is written when the activity completes.
    /// </summary>
    Task<int> ClearActivityLogsAsync(DateTime olderThanUtc, CancellationToken ct = default);

    /// <summary>How many logs <see cref="ClearActivityLogsAsync"/> would clear at the cut-off; changes nothing.</summary>
    Task<long> CountActivityLogsAsync(DateTime olderThanUtc, CancellationToken ct = default);

    /// <summary>Opens the row of a retrieval run and returns it with its id.</summary>
    Task<RetrievalState> StartRetrievalAsync(RetrievalState retrieval, CancellationToken ct = default);

    /// <summary>Closes a retrieval run with its outcome and counts.</summary>
    Task CompleteRetrievalAsync(long retrievalId, string status, long records, int files, long bytes, string? manifestLocation, string? failure, DateTime completedUtc, CancellationToken ct = default);

    /// <summary>The flow's most recent retrieval run in the given status (the watermark chain reads the last done one), or null.</summary>
    Task<RetrievalState?> LastRetrievalAsync(Guid flowId, string status, CancellationToken ct = default);

    /// <summary>The flow's retrieval runs, newest first.</summary>
    Task<IReadOnlyList<RetrievalState>> ListRetrievalsAsync(Guid flowId, int max, CancellationToken ct = default);

    Task<ActivityRecord> StartActivityAsync(ActivityRecord activity, CancellationToken ct = default);

    /// <summary>
    /// Closes an activity with its outcome, summary and captured log, the submission it turned out to work on, and whether
    /// it changed nothing (<see cref="ActivityRecord.Idle"/>), which only a completed activity can be.
    /// </summary>
    Task CompleteActivityAsync(long activityId, string outcome, string? summary, string? log, DateTime completedUtc, Guid? submissionId = null, bool idle = false, CancellationToken ct = default);

    Task<ActivityRecord?> GetActivityAsync(long activityId, CancellationToken ct = default);

    Task<IReadOnlyList<ActivityRecord>> ListActivitiesAsync(ActivityQuery query, CancellationToken ct = default);

    /// <summary>How many activities match a listing, for paging.</summary>
    Task<int> CountActivitiesAsync(ActivityQuery query, CancellationToken ct = default);
}
