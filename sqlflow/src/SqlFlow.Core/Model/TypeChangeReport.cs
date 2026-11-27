using System.Text.Json.Serialization;

namespace SqlFlow.Core.Model;

/// <summary>
/// The three tiers every change to the type of a column that already exists falls into. A change not listed in
/// <see cref="Widening"/> or <see cref="CheckedConversion"/> is <see cref="Manual"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TypeChangeTier>))]
public enum TypeChangeTier
{
    /// <summary>Within a family, to a type that holds every value of the old one. Applied automatically: in place when
    /// SQL Server records it in metadata, by the safe rewrite (with <c>schema.allowTableRewrite</c>) otherwise.</summary>
    Widening = 1,

    /// <summary>A conversion whose success depends on the data (text to a typed column, a typed column to text, a
    /// shorter text, an integer to a decimal wide enough). Applied only with <c>schema.convertTypes</c>, after every
    /// stored value is shown to convert without loss.</summary>
    CheckedConversion = 2,

    /// <summary>Refused. The engine names the change and the manual steps; it never applies part of it.</summary>
    Manual = 3,
}

/// <summary>What became of a type change in one run (or one plan).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TypeChangeStatus>))]
public enum TypeChangeStatus
{
    /// <summary>The change was applied; the column holds the new type.</summary>
    Applied,

    /// <summary>The change needs an opt-in the flow has not given; the table is untouched.</summary>
    Pending,

    /// <summary>Stored values do not convert without loss; the change was refused and the table is untouched.</summary>
    Refused,

    /// <summary>Tier 3: the engine does not apply it; the manual steps say how.</summary>
    Manual,

    /// <summary>A narrowing the engine leaves alone: the column keeps its wider type, which holds every source value.</summary>
    KeptWider,

    /// <summary>A plan's verdict: the change would be applied by a run (the checks passed).</summary>
    Planned,

    /// <summary>A rewrite an earlier run started was finished.</summary>
    Resumed,

    /// <summary>A rewrite an earlier run started was rolled back, leaving the column as it was.</summary>
    RolledBack,
}

/// <summary>A stored value that does not convert, with how many rows hold it.</summary>
public sealed record TypeChangeValueSample
{
    /// <summary>The value as text, cut to <see cref="MaxLength"/> characters.</summary>
    public required string Value { get; init; }

    public required long Rows { get; init; }

    public const int MaxLength = 100;
}

/// <summary>A conversion to check for loss over the values a table holds: a column, its type, and the type it is to
/// become.</summary>
public sealed record LossCheck(string Column, string FromType, string ToType);

/// <summary>What a <see cref="LossCheck"/> found: the rows, the values that do not convert without loss, and a
/// sample of them.</summary>
public sealed record LossFinding(string Column, long Rows, long Failing, IReadOnlyList<TypeChangeValueSample> Sample);

/// <summary>
/// What a run (or a plan) did about one change to the type of a column that already exists: where it is, the types,
/// its tier, its outcome, the table's row count, and for a refusal the number of values that do not convert and a
/// sample of them. A file flow reports the changes of its typed view (<see cref="ViewLayer"/>), an ingestion flow those
/// of its target (<see cref="TargetLayer"/>). Serialized into run.json under <c>result.typeChanges</c>, from where the
/// catalog keeps each column's type history.
/// </summary>
public sealed record TypeChangeReport
{
    public const string TargetLayer = "target";
    public const string ViewLayer = "view";

    /// <summary>The flow layer the change is in: <see cref="TargetLayer"/> or <see cref="ViewLayer"/>.</summary>
    public required string Layer { get; init; }

    /// <summary>The qualified table or view.</summary>
    public required string Table { get; init; }

    public required string Column { get; init; }

    public required string FromType { get; init; }

    public required string ToType { get; init; }

    public required TypeChangeTier Tier { get; init; }

    public required TypeChangeStatus Status { get; init; }

    /// <summary>True when the change rewrites every row (rather than being recorded in metadata only).</summary>
    public bool RewritesTable { get; init; }

    /// <summary>True for a key, hash, identity or primary-key column.</summary>
    public bool IsKey { get; init; }

    /// <summary>The indexes that key or include the column.</summary>
    public IReadOnlyList<string> Indexes { get; init; } = [];

    /// <summary>The objects that reference the column and stop the engine changing it.</summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>The flow setting the change needs, or null when it applies without one.</summary>
    public string? RequiresOptIn { get; init; }

    /// <summary>The rows the table held when the change was considered; null when not counted.</summary>
    public long? Rows { get; init; }

    /// <summary>For a refusal, the values that do not convert without loss.</summary>
    public long? FailingValues { get; init; }

    public IReadOnlyList<TypeChangeValueSample> Sample { get; init; } = [];

    /// <summary>The full explanation: why, what the engine did, and what to do next.</summary>
    public required string Message { get; init; }

    public IReadOnlyList<string> ManualSteps { get; init; } = [];

    /// <summary>True when the outcome needs an operator's attention: it stays a warning of every run until resolved.</summary>
    [JsonIgnore]
    public bool NeedsAttention => StatusNeedsAttention(Status);

    /// <summary>Whether an outcome needs an operator's attention (pending an opt-in, refused, manual, rolled back): the
    /// one rule the engine's warnings and every reader of a recorded outcome follow.</summary>
    public static bool StatusNeedsAttention(TypeChangeStatus status)
        => status is TypeChangeStatus.Pending or TypeChangeStatus.Refused or TypeChangeStatus.Manual
            or TypeChangeStatus.RolledBack;

    /// <summary>True when the run reports the change as a warning: it needs attention, or it changed the type every
    /// consumer of the column reads (applied or finished), which no consumer asked for. A kept-wider narrowing and a
    /// plan's verdict are informational.</summary>
    [JsonIgnore]
    public bool IsWarning => NeedsAttention || Status is TypeChangeStatus.Applied or TypeChangeStatus.Resumed;

    /// <summary>The sentence an applied change ends with: what it means for the column's consumers.</summary>
    public const string Downstream =
        "Every consumer of the column now reads the new type: one that renders it (a delivery, a report) renders it differently " +
        "the next time it renders a row, and a row converted here keeps its UpdatedDate_DW, so a consumer that selects rows by it " +
        "re-renders the converted rows only when they next change or when it re-renders its whole scope.";
}
