using System.Globalization;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// What a reversal puts back (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission):
/// one run, with the submissions it coordinated and what it delivered itself, or one submission, whichever runs drained
/// it.
/// </summary>
public sealed record ReversalSource
{
    /// <summary>A run: what was delivered under the submissions it coordinated, and what it delivered itself.</summary>
    public const string RunKind = "run";

    /// <summary>A submission: what its batch delivered, whichever run drained it.</summary>
    public const string SubmissionKind = "submission";

    private ReversalSource(string kind, Guid id)
    {
        Kind = kind;
        Id = id;
    }

    /// <summary><see cref="RunKind"/> or <see cref="SubmissionKind"/>.</summary>
    public string Kind { get; }

    /// <summary>The run's or the submission's id.</summary>
    public Guid Id { get; }

    /// <summary>Whether the source is a run.</summary>
    public bool IsRun => Kind == RunKind;

    public static ReversalSource Run(Guid runId)
        => runId == Guid.Empty ? throw new DeliveryException("A reversal of a run names the run.") : new ReversalSource(RunKind, runId);

    public static ReversalSource Submission(Guid submissionId)
        => submissionId == Guid.Empty ? throw new DeliveryException("A reversal of a submission names the submission.") : new ReversalSource(SubmissionKind, submissionId);

    /// <summary>A source as the ledger stores it; throws for a kind that is neither.</summary>
    public static ReversalSource Of(string kind, Guid id) => kind switch
    {
        RunKind => Run(id),
        SubmissionKind => Submission(id),
        _ => throw new DeliveryException($"'{kind}' is not what a reversal reverses: a run or a submission."),
    };

    /// <summary>How a message names the source: "run 0193..." or "submission 0193...".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Kind} {Id:D}");
}

/// <summary>The states of a reversal.</summary>
public static class ReversalStatuses
{
    /// <summary>Listing what its source delivered.</summary>
    public const string Capturing = "capturing";

    /// <summary>Putting the records back.</summary>
    public const string Reversing = "reversing";

    /// <summary>Every record it lists is settled: done, passed over or failed.</summary>
    public const string Completed = "completed";

    /// <summary>The latest run stopped on a failure; asking again resumes it.</summary>
    public const string Failed = "failed";

    /// <summary>The latest run was cancelled; asking again resumes it.</summary>
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [Capturing, Reversing, Completed, Failed, Cancelled];
}

/// <summary>The states of one record of a reversal.</summary>
public static class ReversalItemStates
{
    /// <summary>Not taken yet.</summary>
    public const string Pending = "pending";

    /// <summary>
    /// A write to OSDU is under way for it. A run that finds an item still sending (its run stopped mid-write) asks OSDU
    /// whether the write landed before it writes again.
    /// </summary>
    public const string Sending = "sending";

    /// <summary>Restored or removed: OSDU holds what it held before the source.</summary>
    public const string Done = "done";

    /// <summary>Passed over, saying why (<see cref="ReversalOutcomes"/>).</summary>
    public const string Skipped = "skipped";

    /// <summary>OSDU or the route refused or failed; taken again when the reversal is asked again.</summary>
    public const string Failed = "failed";

    public static IReadOnlyList<string> All { get; } = [Pending, Sending, Done, Skipped, Failed];
}

/// <summary>What came of one record of a reversal.</summary>
public static class ReversalOutcomes
{
    /// <summary>The version OSDU held before the source was written back, as a new version.</summary>
    public const string Restored = "restored";

    /// <summary>The source created the record, and it was removed again, reversibly.</summary>
    public const string Removed = "removed";

    /// <summary>The source created the record, and OSDU had already lost it: the ledger is settled as removed.</summary>
    public const string AlreadyGone = "already-gone";

    /// <summary>The ledger holds another version than the one the source left: something came after it.</summary>
    public const string Superseded = "superseded";

    /// <summary>The source left OSDU at the version it held before (it wrote nothing new of the record): nothing to put back.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>OSDU's latest version is not the one the source left: something outside this flow wrote it since.</summary>
    public const string ChangedInOsdu = "changed-in-osdu";

    /// <summary>OSDU no longer holds a record the source updated: it was removed outside this flow.</summary>
    public const string MissingInOsdu = "missing-in-osdu";

    /// <summary>OSDU no longer holds the version to put back (its history was purged).</summary>
    public const string VersionMissing = "version-missing";

    /// <summary>Work is queued or in flight for the record; taken again when the reversal is asked again.</summary>
    public const string Busy = "busy";

    /// <summary>
    /// The record's access in OSDU is restricted (osdu/docs/reference/concepts/access-restriction.md), and nothing is written
    /// to it while it is; taken again when the reversal is asked again, once the restriction is lifted.
    /// </summary>
    public const string Restricted = "restricted";

    /// <summary>The record never claimed the OSDU id, so the flow wrote nothing to OSDU under it.</summary>
    public const string NotClaimed = "not-claimed";

    /// <summary>The ledger holds no record of the flow under the key.</summary>
    public const string NotInLedger = "not-in-ledger";

    /// <summary>The route cannot do what the record needs.</summary>
    public const string NotReversible = "not-reversible";

    /// <summary>OSDU or the route refused or failed.</summary>
    public const string Failed = "failed";

    public static IReadOnlyList<string> All { get; } =
        [Restored, Removed, AlreadyGone, Superseded, Unchanged, ChangedInOsdu, MissingInOsdu, VersionMissing, Busy, Restricted, NotClaimed, NotInLedger, NotReversible, Failed];

    /// <summary>The outcomes after which OSDU holds what it held before the source.</summary>
    public static bool Reversed(string? outcome) => outcome is Restored or Removed or AlreadyGone;
}

/// <summary>What OSDU held of a record before the source delivered it.</summary>
public static class ReversalPriors
{
    /// <summary>A version, which a restore writes back.</summary>
    public const string Version = "version";

    /// <summary>Nothing: the source created the record, or created it again after a removal; a removal puts that back.</summary>
    public const string None = "none";

    /// <summary>The ledger no longer says (the attempts before were pruned): OSDU's version list decides.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// One reversal as the ledger holds it (osdu/docs/reference/concepts/ledger.md, <c>osdu.Reversal</c> and
/// <c>osdu.ReversalItem</c>).
/// </summary>
public sealed record ReversalState
{
    public required long ReversalId { get; init; }

    public required Guid FlowId { get; init; }

    /// <summary>The partition the ledger is kept under, as the directory names it.</summary>
    public string? Partition { get; init; }

    public required string FlowName { get; init; }

    public required ReversalSource Source { get; init; }

    /// <summary>The submissions the source covers.</summary>
    public IReadOnlyList<Guid> Submissions { get; init; } = [];

    /// <summary>One of <see cref="ReversalStatuses"/>.</summary>
    public required string Status { get; init; }

    public required string RequestedBy { get; init; }

    public DateTime RequestedUtc { get; init; }

    /// <summary>When every record the source delivered was listed; null while the listing is not finished.</summary>
    public DateTime? CapturedUtc { get; init; }

    public DateTime? StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public Guid? LastRunId { get; init; }

    public string? Error { get; init; }
}

/// <summary>One record of a reversal: what its source delivered of it, what OSDU held before, and what came of it.</summary>
public sealed record ReversalItemState
{
    public required DeliveryKey DeliveryKey { get; init; }

    /// <summary>The OSDU id the record claimed when the reversal listed it.</summary>
    public string? TargetId { get; init; }

    public long FirstAttemptId { get; init; }

    /// <summary>The version the source's first delivery of the record wrote.</summary>
    public long? FirstVersion { get; init; }

    /// <summary>The version the source left.</summary>
    public long? RunVersion { get; init; }

    /// <summary>One of <see cref="ReversalPriors"/>.</summary>
    public required string Prior { get; init; }

    public long? PriorVersion { get; init; }

    public long? PriorAttemptId { get; init; }

    /// <summary>One of <see cref="ReversalItemStates"/>.</summary>
    public required string State { get; init; }

    /// <summary>One of <see cref="ReversalOutcomes"/>, once settled.</summary>
    public string? Outcome { get; init; }

    public string? Detail { get; init; }

    public long? RestoredVersion { get; init; }

    public long? NewVersion { get; init; }

    public Guid? RunId { get; init; }

    public DateTime UpdatedUtc { get; init; }
}

/// <summary>A reversal's records counted by state and by outcome, read from its items.</summary>
public sealed record ReversalCounts(long Records, IReadOnlyDictionary<string, long> States, IReadOnlyDictionary<string, long> Outcomes)
{
    public static ReversalCounts Empty { get; } = new(0, new Dictionary<string, long>(StringComparer.Ordinal), new Dictionary<string, long>(StringComparer.Ordinal));

    public long State(string state) => States.TryGetValue(state, out var n) ? n : 0;

    public long Outcome(string outcome) => Outcomes.TryGetValue(outcome, out var n) ? n : 0;

    /// <summary>Records not settled yet: pending, or left sending by a run that stopped.</summary>
    public long Open => State(ReversalItemStates.Pending) + State(ReversalItemStates.Sending);

    /// <summary>
    /// Records the next run of the reversal takes: those not settled yet, those that failed, and those passed over as busy or
    /// restricted. None means asking again would change nothing.
    /// </summary>
    public long ToTake => Open + State(ReversalItemStates.Failed) + Outcome(ReversalOutcomes.Busy) + Outcome(ReversalOutcomes.Restricted);

    /// <summary>The counts that are not zero, as the audit trail shows a reversal: "1,200 restored, 300 removed, 4 superseded".</summary>
    public override string ToString()
    {
        var parts = ReversalOutcomes.All.Where(o => Outcome(o) > 0).Select(o => string.Create(CultureInfo.InvariantCulture, $"{Outcome(o):N0} {o}")).ToList();
        if (Open > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Open:N0} not done yet"));
        }

        return parts.Count == 0 ? "nothing to reverse" : string.Join(", ", parts);
    }
}

/// <summary>What listing a reversal's source added, page by page.</summary>
public sealed record ReversalCapture(long Added, long Records);

/// <summary>
/// What a reversal did to one record, as the ledger settles it with the record: the item's state and outcome, and for a
/// restore or a removal what the record becomes.
/// </summary>
public sealed record ReversalSettlement
{
    public required DeliveryKey DeliveryKey { get; init; }

    /// <summary><see cref="ReversalItemStates.Done"/>, <see cref="ReversalItemStates.Skipped"/> or <see cref="ReversalItemStates.Failed"/>.</summary>
    public required string State { get; init; }

    /// <summary>One of <see cref="ReversalOutcomes"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Why, in words; redacted before it is stored.</summary>
    public string? Detail { get; init; }

    /// <summary>With <see cref="ReversalOutcomes.Restored"/>: the version written back.</summary>
    public long? RestoredVersion { get; init; }

    /// <summary>With <see cref="ReversalOutcomes.Restored"/>: the version OSDU gave it.</summary>
    public long? NewVersion { get; init; }

    /// <summary>With <see cref="ReversalOutcomes.Restored"/>: the target state the record holds afterwards.</summary>
    public string? TargetStateJson { get; init; }

    /// <summary>The correlation id the record's OSDU calls carried.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>A settlement that puts the record back in OSDU, which the ledger record follows.</summary>
    public bool ChangesRecord => State == ReversalItemStates.Done && ReversalOutcomes.Reversed(Outcome);

    public static ReversalSettlement Restored(DeliveryKey key, long restored, long newVersion, string? targetStateJson, string detail, string? correlationId)
        => new() { DeliveryKey = key, State = ReversalItemStates.Done, Outcome = ReversalOutcomes.Restored, RestoredVersion = restored, NewVersion = newVersion, TargetStateJson = targetStateJson, Detail = detail, CorrelationId = correlationId };

    public static ReversalSettlement Removed(DeliveryKey key, bool alreadyGone, string detail, string? correlationId)
        => new() { DeliveryKey = key, State = ReversalItemStates.Done, Outcome = alreadyGone ? ReversalOutcomes.AlreadyGone : ReversalOutcomes.Removed, Detail = detail, CorrelationId = correlationId };

    public static ReversalSettlement Skipped(DeliveryKey key, string outcome, string detail, string? correlationId = null)
        => new() { DeliveryKey = key, State = ReversalItemStates.Skipped, Outcome = outcome, Detail = detail, CorrelationId = correlationId };

    public static ReversalSettlement Failed(DeliveryKey key, string detail, string? correlationId)
        => new() { DeliveryKey = key, State = ReversalItemStates.Failed, Outcome = ReversalOutcomes.Failed, Detail = detail, CorrelationId = correlationId };
}

/// <summary>What the ledger made of one page of settlements.</summary>
/// <param name="Settled">Items settled.</param>
/// <param name="Changed">Records the ledger changed with them (restored or removed).</param>
/// <param name="MovedRestores">
/// Restores whose record no longer matched when they were settled (it changed in the ledger meanwhile); each is settled
/// failed, saying what OSDU now holds.
/// </param>
/// <param name="MovedRemovals">Removals settled failed for the same reason.</param>
public sealed record ReversalSettled(int Settled, int Changed, int MovedRestores, int MovedRemovals)
{
    public static ReversalSettled None { get; } = new(0, 0, 0, 0);

    /// <summary>Restores and removals the ledger settled failed because their records moved on.</summary>
    public int Moved => MovedRestores + MovedRemovals;

    public ReversalSettled Add(ReversalSettled other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Settled + other.Settled, Changed + other.Changed, MovedRestores + other.MovedRestores, MovedRemovals + other.MovedRemovals);
    }
}

/// <summary>
/// One record whose version before the latest was written back as its current version
/// (osdu/docs/reference/concepts/removal-and-reversal.md, Restoring the previous version).
/// </summary>
/// <param name="Key">The record.</param>
/// <param name="Replaced">The latest version OSDU held, which the ledger held too: the one taken out of being current.</param>
/// <param name="Restored">The version before it, written back.</param>
/// <param name="NewVersion">The version OSDU gave the write.</param>
/// <param name="TargetStateJson">The target state the record holds now.</param>
public sealed record PreviousVersionRestored(DeliveryKey Key, long Replaced, long Restored, long NewVersion, string? TargetStateJson);

/// <summary>
/// What OSDU held of a record before the write that left the version the ledger holds now, as the ledger tells it
/// (osdu/docs/reference/concepts/removal-and-reversal.md, Restoring the previous version): the version a delivery
/// recorded it replaced, the version a step back recorded it replaced, else the version of the attempt before that
/// write.
/// </summary>
/// <param name="Prior"><see cref="ReversalPriors.Version"/>, <see cref="ReversalPriors.None"/> (the write created the record), or <see cref="ReversalPriors.Unknown"/>.</param>
/// <param name="Version">The version OSDU held before, for <see cref="ReversalPriors.Version"/>.</param>
/// <param name="FirstVersion">
/// The lowest version the write left: one try can write two (a Wellbore DDMS record, then its bulk data, which the try
/// records as the version it left). OSDU's version list decides an unknown prior by the newest version older than it.
/// Null when the ledger no longer holds the attempt that wrote the version (its attempts were pruned): which versions that
/// write left cannot be told, so neither can the one before them.
/// </param>
public sealed record PriorVersion(string Prior, long? Version, long? FirstVersion);

/// <summary>What a preview of a reversal reads of its source.</summary>
/// <param name="Submissions">The submissions the source covers.</param>
/// <param name="Records">The records it delivered, counted.</param>
/// <param name="Sample">The first of those records in key order, each with what the reversal would list for it.</param>
public sealed record ReversalSourceRead(IReadOnlyList<Guid> Submissions, long Records, IReadOnlyList<ReversalItemState> Sample);
