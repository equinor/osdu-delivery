using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Taking out of OSDU the records a source no longer holds (osdu/docs/reference/flow/removal.md;
/// osdu/docs/wiki/decisions/0022-removal-flows.md): the sides a removal reads its keys from, why a key counts as removed, the
/// states a removed key goes through in the ledger, what taking it out comes to, and the states of a removal's runs. A removal
/// flow keeps no ledger of its own: its runs and the keys it found removed are kept under the ledger of the delivery flow whose
/// records they are, and the removed keys are that ledger's deletions.
/// </summary>
public static class SourceRemovals
{
    /// <summary>The source's current, complete key set: a key the ledger or the record table holds that it does not is removed.</summary>
    public const byte SourceSide = 1;

    /// <summary>The source's removed keys: a key it lists is removed, as of the moment it lists.</summary>
    public const byte RemovedSide = 2;

    /// <summary>The delivery flow's record table in the removal's scope: what the comparison covers besides the ledger's records.</summary>
    public const byte IngestionSide = 3;

    /// <summary>The key is not in the source's current key set.</summary>
    public const string Absent = "absent";

    /// <summary>The source lists the key as removed.</summary>
    public const string Listed = "listed";

    /// <summary>A removed key the run's guard held: nothing is done with it, and no row of it is kept out, until an operator approves.</summary>
    public const string AwaitingApproval = "awaiting-approval";

    /// <summary>A removed key to take out: the next removal run of its ledger takes it, and no row of it not newer than its moment is delivered.</summary>
    public const string Pending = "pending";

    /// <summary>A removed key taken out of OSDU, settled in the ledger, or kept out: never asked of OSDU again.</summary>
    public const string Done = "removed";

    /// <summary>Every state a removed key is in, in the order a page lists them.</summary>
    public static IReadOnlyList<string> States { get; } = [Pending, AwaitingApproval, Done];

    /// <summary>Taken out of OSDU (reversibly) and marked removed in the ledger.</summary>
    public const string Removed = "removed";

    /// <summary>OSDU no longer held it when the removal reached it; the ledger was settled as for a removal.</summary>
    public const string AlreadyGone = "already-gone";

    /// <summary>The ledger held it and OSDU did not: its queued work was dropped and it was marked removed, nothing asked of OSDU.</summary>
    public const string Settled = "settled";

    /// <summary>Only the record table held it: kept out, so no run delivers it.</summary>
    public const string KeptOut = "kept-out";

    /// <summary>Left in OSDU: other records still name it. The next run tries again.</summary>
    public const string Referenced = "referenced";

    /// <summary>Left in OSDU: another ledger of the partition holds the same id live. The next run tries again.</summary>
    public const string Shared = "shared";

    /// <summary>A worker was sending it, or another removal held it: the next run takes it.</summary>
    public const string Busy = "busy";

    /// <summary>OSDU refused or failed the removal; the detail says what it answered. The next run tries again.</summary>
    public const string Failed = "failed";

    /// <summary>Every outcome a try comes to, in the order a page lists them.</summary>
    public static IReadOnlyList<string> Outcomes { get; } = [Removed, AlreadyGone, Settled, KeptOut, Referenced, Shared, Busy, Failed];

    /// <summary>The outcomes that finish a removed key: it is <see cref="Done"/>, and never asked of OSDU again.</summary>
    public static IReadOnlyList<string> Finishing { get; } = [Removed, AlreadyGone, Settled, KeptOut];

    /// <summary>The run is under way.</summary>
    public const string Running = "running";

    /// <summary>The run read and compared its sources and took out every pending key it could; what it could not (referenced, busy, failed) the next run takes.</summary>
    public const string Completed = "completed";

    /// <summary>The run's guard held the keys it found removed for an operator's approval; the pending ones it took out as any run does.</summary>
    public const string Guarded = "guarded";

    /// <summary>The run stopped on an error; what it recorded stands.</summary>
    public const string Failure = "failed";

    /// <summary>The activity a removal's work is recorded under in the delivery flow's ledger.</summary>
    public const string ActivityKind = "source-remove";

    /// <summary>The activity an operator's lift of a removed key is recorded under: the record is delivered again.</summary>
    public const string LiftActivityKind = "lift-removal";

    /// <summary>
    /// How far before the moment a source's read began a row may have changed and still be taken for a row the source no longer
    /// holds: a row that changed later may be a key the source holds again, so it is not removed. It covers the clocks of the
    /// node reading the source and of the database stamping the row.
    /// </summary>
    public static TimeSpan ClockMargin { get; } = TimeSpan.FromMinutes(5);

    /// <summary>The keys a removal stages, converts and derives in one batch.</summary>
    public const int StageBatch = 20_000;

    /// <summary>The removed keys a removal acts on in one chunk: one bulk soft delete, one removal hold.</summary>
    public const int Chunk = 500;

    /// <summary>How many rows a run that stopped on unreadable keys names.</summary>
    public const int MaxNamedRows = 20;

    /// <summary>The longest detail the ledger keeps of a try's outcome.</summary>
    public const int MaxDetailLength = 1000;

    /// <summary>Whether <paramref name="outcome"/> is one a try comes to.</summary>
    public static bool IsOutcome(string outcome) => Outcomes.Contains(outcome, StringComparer.Ordinal);

    /// <summary>Whether <paramref name="state"/> is one a removed key is in.</summary>
    public static bool IsState(string state) => States.Contains(state, StringComparer.Ordinal);

    /// <summary>A detail as the ledger keeps it: at most <see cref="MaxDetailLength"/> characters.</summary>
    public static string? Detail(string? detail)
        => detail is null ? null : detail.Length <= MaxDetailLength ? detail : detail[..(MaxDetailLength - 3)] + "...";
}

/// <summary>What opens a removal run (<see cref="ILedger.StartRemovalRunAsync"/>).</summary>
public sealed record RemovalRunStart
{
    /// <summary>The removal flow, by name.</summary>
    public required string FlowName { get; init; }

    /// <summary>The removal of the flow, by name.</summary>
    public required string Entry { get; init; }

    /// <summary>The delivery flow as pages name it.</summary>
    public required string DeliveryFlow { get; init; }

    public Guid? RunId { get; init; }

    /// <summary>remove or plan.</summary>
    public required string Operation { get; init; }

    public required string Actor { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required int GuardPercent { get; init; }

    public long? GuardRecords { get; init; }

    /// <summary>Who approved the keys the run makes pending, for a run that carries out an approval; null for none.</summary>
    public string? ApprovedBy { get; init; }
}

/// <summary>A removal run as the ledger keeps it (<c>osdu.RemovalRun</c>).</summary>
public sealed record RemovalRunState
{
    public required string Partition { get; init; }

    public required long RemovalRunId { get; init; }

    public required string FlowName { get; init; }

    public required string Entry { get; init; }

    public required Guid LedgerFlowId { get; init; }

    public required string DeliveryFlow { get; init; }

    public Guid? RunId { get; init; }

    public required string Operation { get; init; }

    public required string Actor { get; init; }

    public required string Status { get; init; }

    public required DateTime StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    public DateTime? SourceReadUtc { get; init; }

    public required RemovalTally Tally { get; init; }

    public required int GuardPercent { get; init; }

    public long? GuardRecords { get; init; }

    public string? ApprovedBy { get; init; }

    public long? ActivityId { get; init; }

    public string? Error { get; init; }
}

/// <summary>What a removal run read, found and came to, as its row keeps the counts.</summary>
public sealed record RemovalTally
{
    public static RemovalTally Empty { get; } = new();

    public long RowsRead { get; init; }

    public long SourceKeys { get; init; }

    public long ListedKeys { get; init; }

    public long IngestionKeys { get; init; }

    public long Compared { get; init; }

    public long Detected { get; init; }

    public long AwaitingApproval { get; init; }

    public long Approved { get; init; }

    public long Removed { get; init; }

    public long AlreadyGone { get; init; }

    public long Settled { get; init; }

    public long KeptOut { get; init; }

    public long Referenced { get; init; }

    public long Shared { get; init; }

    public long Busy { get; init; }

    public long Failed { get; init; }

    public long CameBack { get; init; }

    public long Lifted { get; init; }

    /// <summary>The counts of both, side by side.</summary>
    public RemovalTally Add(RemovalTally other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new RemovalTally
        {
            RowsRead = RowsRead + other.RowsRead,
            SourceKeys = SourceKeys + other.SourceKeys,
            ListedKeys = ListedKeys + other.ListedKeys,
            IngestionKeys = IngestionKeys + other.IngestionKeys,
            Compared = Compared + other.Compared,
            Detected = Detected + other.Detected,
            AwaitingApproval = AwaitingApproval + other.AwaitingApproval,
            Approved = Approved + other.Approved,
            Removed = Removed + other.Removed,
            AlreadyGone = AlreadyGone + other.AlreadyGone,
            Settled = Settled + other.Settled,
            KeptOut = KeptOut + other.KeptOut,
            Referenced = Referenced + other.Referenced,
            Shared = Shared + other.Shared,
            Busy = Busy + other.Busy,
            Failed = Failed + other.Failed,
            CameBack = CameBack + other.CameBack,
            Lifted = Lifted + other.Lifted,
        };
    }

    /// <summary>The tally of a set of tries' outcomes, one count per outcome.</summary>
    public static RemovalTally Of(IEnumerable<string> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        long removed = 0, gone = 0, settled = 0, keptOut = 0, referenced = 0, shared = 0, busy = 0, failed = 0;
        foreach (var outcome in outcomes)
        {
            switch (outcome)
            {
                case SourceRemovals.Removed: removed++; break;
                case SourceRemovals.AlreadyGone: gone++; break;
                case SourceRemovals.Settled: settled++; break;
                case SourceRemovals.KeptOut: keptOut++; break;
                case SourceRemovals.Referenced: referenced++; break;
                case SourceRemovals.Shared: shared++; break;
                case SourceRemovals.Busy: busy++; break;
                case SourceRemovals.Failed: failed++; break;
            }
        }

        return new RemovalTally
        {
            Removed = removed, AlreadyGone = gone, Settled = settled, KeptOut = keptOut, Referenced = referenced, Shared = shared, Busy = busy, Failed = failed,
        };
    }
}

/// <summary>One key a removal stages (<see cref="ILedger.StageRemovalKeysAsync"/>), already derived to its delivery key.</summary>
public sealed record StagedRemovalKey
{
    public required DeliveryKey Key { get; init; }

    /// <summary>The key tuple, a JSON array of strings in the record key's order.</summary>
    public required string SourceKeyJson { get; init; }

    /// <summary>For the record table's side: when its row last changed.</summary>
    public DateTime? UpdatedUtc { get; init; }

    /// <summary>For the removed side: when the source removed the key.</summary>
    public DateTime? RemovedUtc { get; init; }

    public string? FileName { get; init; }

    public long? RowNumber { get; init; }
}

/// <summary>
/// How a batch of raw key values is converted where the ledger is kept (<see cref="ILedger.ConvertRemovalKeysAsync"/>): each
/// key column's expression over its raw value and the record table's type it is converted to, and, for the removed side, the
/// expression over the raw removal moment.
/// </summary>
public sealed record RemovalKeyConversion
{
    /// <summary>The record key's columns in order: the SQL Server type each converts to, and the expression over the raw value (<c>@ColName</c> standing for it), or null for the value itself.</summary>
    public required IReadOnlyList<RemovalKeyTarget> Columns { get; init; }

    /// <summary>Whether a removal moment travels with each row, and the expression over it; null when none does.</summary>
    public RemovalMomentTarget? Moment { get; init; }
}

/// <summary>One key column a raw value converts to: the record table's type, and an expression over the raw value.</summary>
public sealed record RemovalKeyTarget(string Column, string SqlType, string? Expression);

/// <summary>The removal moment a raw value converts to, a UTC instant, and an expression over the raw value.</summary>
public sealed record RemovalMomentTarget(string? Expression);

/// <summary>One row of raw key values a source read, as text, with the file and row it was read from.</summary>
public sealed record RawRemovalKey
{
    /// <summary>The raw value of each key column, in the record key's order; null where the source holds none.</summary>
    public required IReadOnlyList<string?> Values { get; init; }

    /// <summary>The raw removal moment, for the removed side.</summary>
    public string? Moment { get; init; }

    public string? FileName { get; init; }

    public long? RowNumber { get; init; }
}

/// <summary>One row of a batch as SQL Server converted it: each key column's typed value (null where it did not convert) and the removal moment.</summary>
public sealed record ConvertedRemovalKey(IReadOnlyList<object?> Values, DateTime? Moment);

/// <summary>What a removal's comparison asks (<see cref="ILedger.DetectRemovalsAsync"/>).</summary>
public sealed record RemovalComparison
{
    /// <summary>Whether the source's current key set was read: a key it does not hold is removed.</summary>
    public required bool ReadsSource { get; init; }

    /// <summary>Whether a source of removed keys was read.</summary>
    public required bool ReadsRemoved { get; init; }

    /// <summary>When the read of the source's current keys began: the moment absent keys are removed as of.</summary>
    public DateTime? SourceReadUtc { get; init; }

    /// <summary>
    /// The scope the comparison covers: the delivery flow's parameter values its records were planned under, by parameter, which
    /// a record's last submission must carry; empty for a flow whose records carry no scope.
    /// </summary>
    public IReadOnlyDictionary<string, string> Scope { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether the comparison writes what it finds (a remove run) or only counts it (a plan).</summary>
    public required bool Write { get; init; }

    /// <summary>The removal flow, by name: what a key it finds names as the removal that found it.</summary>
    public required string RemovalFlow { get; init; }

    /// <summary>The removal of the flow that compares.</summary>
    public required string Entry { get; init; }

    /// <summary>The platform run of the removal.</summary>
    public Guid? RunId { get; init; }

    /// <summary>The guard's share, 0 to 100, of the compared keys the keys found removed may be without an approval.</summary>
    public required int GuardPercent { get; init; }

    /// <summary>The guard's count the keys found removed may reach without an approval; null for none.</summary>
    public long? GuardRecords { get; init; }

    public required DateTime NowUtc { get; init; }
}

/// <summary>What a removal's comparison found (<see cref="ILedger.DetectRemovalsAsync"/>).</summary>
public sealed record RemovalDetection
{
    /// <summary>Distinct keys compared: the ledger's records in the scope that are not removed, and the record table's keys besides.</summary>
    public required long Compared { get; init; }

    /// <summary>Keys the source removed that the ledger holds no removed key of yet: the rows the comparison added (or, for a plan, would add).</summary>
    public required long Detected { get; init; }

    /// <summary>Whether the guard held them for an operator's approval: there are more than it allows, or the source's current keys read none.</summary>
    public required bool Guarded { get; init; }

    /// <summary>Keys removed as far as their sources say, whose row arrived after the moment of their removal.</summary>
    public required long CameBack { get; init; }

    /// <summary>Removed keys lifted (or, for a plan, that would be): the source holds them again, or their record was delivered again.</summary>
    public required long Lifted { get; init; }

    /// <summary>Removed keys of the ledger pending after the comparison: what taking them out works through.</summary>
    public required long Pending { get; init; }

    /// <summary>Removed keys of the ledger waiting for an operator's approval after the comparison.</summary>
    public required long AwaitingApproval { get; init; }
}

/// <summary>A key a source removed, as the delivery flow's ledger keeps it (<c>osdu.RemovedKey</c>).</summary>
public sealed record RemovedKeyState
{
    public required DeliveryKey Key { get; init; }

    /// <summary>The status of the ledger's record of the key; null when the ledger holds none (a key only the record table held).</summary>
    public string? RecordStatus { get; init; }

    public string? SourceKeyJson { get; init; }

    public required string Reason { get; init; }

    public required DateTime RemovedUtc { get; init; }

    public string? FileName { get; init; }

    public long? RowNumber { get; init; }

    public required string State { get; init; }

    public string? Outcome { get; init; }

    public string? Detail { get; init; }

    public required int Tries { get; init; }

    public required string RemovalFlow { get; init; }

    public required string Entry { get; init; }

    public required long DetectedRunId { get; init; }

    public long? DoneRunId { get; init; }

    public Guid? RunId { get; init; }

    public string? ApprovedBy { get; init; }

    public DateTime? ApprovedUtc { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public DateTime? DoneUtc { get; init; }

    public required DateTime UpdatedUtc { get; init; }
}

/// <summary>What one try to take out a removed key came to (<see cref="ILedger.SettleRemovalsAsync"/>).</summary>
public sealed record RemovalKeyOutcome(DeliveryKey Key, string Outcome, string? Detail);

/// <summary>
/// Why a record a removal took out or settled in the ledger is no longer delivered, as its attempt says it
/// (<see cref="ILedger.MarkRemovedAsync"/>): the removal flow and run, why its source no longer holds it, and the file and row
/// that said so.
/// </summary>
public sealed record SourceRemovalNote
{
    public required string RemovalFlow { get; init; }

    public required string Entry { get; init; }

    public required long RemovalRunId { get; init; }

    public Guid? RunId { get; init; }

    public required string Reason { get; init; }

    public required DateTime RemovedUtc { get; init; }

    public string? FileName { get; init; }

    public long? RowNumber { get; init; }

    /// <summary>The activity the removal is recorded under, which names the record.</summary>
    public long? ActivityId { get; init; }

    /// <summary>This note for one removed key: why its source no longer holds it, as of when, and what said so.</summary>
    public SourceRemovalNote For(RemovedKeyState key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return this with { Reason = key.Reason, RemovedUtc = key.RemovedUtc, FileName = key.FileName, RowNumber = key.RowNumber };
    }

    /// <summary>The attempt's note, in the words a record's history shows.</summary>
    public string Describe(bool inOsdu)
    {
        var why = Reason == SourceRemovals.Listed
            ? $"the source listed the key as removed at {RemovedUtc:yyyy-MM-ddTHH:mm:ssZ}" + (FileName is null ? string.Empty : $" ({FileName}{(RowNumber is { } row ? $", row {row}" : string.Empty)})")
            : $"the source's keys, read at {RemovedUtc:yyyy-MM-ddTHH:mm:ssZ}, no longer held it";
        var what = inOsdu
            ? "taken out of OSDU (reversibly)"
            : "not in OSDU, so nothing was asked of it; its queued work was dropped";
        return $"removed by removal flow '{RemovalFlow}' ({Entry}, run {RemovalRunId}): {why}; {what}. A row of the key newer than that moment is delivered again; 'Deliver again' on the record's page brings it back now.";
    }
}
