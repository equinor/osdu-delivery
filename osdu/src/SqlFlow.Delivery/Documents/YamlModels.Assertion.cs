// The YAML shapes of an assertion flow document (osdu/docs/reference/flow/assertion.md): its source, defaults,
// queries and tests, and each test's assertions.
// As in YamlModels.cs, every property must be listed: unknown keys are rejected.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720
using YamlDotNet.Serialization;

namespace SqlFlow.Delivery.Documents;

internal sealed class AssertionYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    /// <summary>The set the flow's tests are grouped under; left out, its batch.</summary>
    public string? Set { get; set; }

    /// <summary>What people read the flow as.</summary>
    public string? Title { get; set; }

    /// <summary>What the flow's assertions are about, name to value; every test takes them.</summary>
    public Dictionary<string, string>? Dimensions { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

    // The flow's operation code, which its runs' correlation id says what ran with (telemetry.operation).
    public TelemetryYaml? Telemetry { get; set; }

    public Dictionary<string, FlowParameterYaml>? Parameters { get; set; }

    /// <summary>The queries the tests compare with by name: each the database it runs against and its one SELECT.</summary>
    public Dictionary<string, AssertionQueryYaml?>? Queries { get; set; }

    public AssertionSourceYaml? Source { get; set; }

    /// <summary>The partitions the flow tests, each by its data-partition-id.</summary>
    public List<string>? Partitions { get; set; }

    public AssertionDefaultsYaml? Defaults { get; set; }

    public string? FailRunOn { get; set; }

    public List<AssertionTestYaml>? Tests { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public RunParallelismYaml? Parallelism { get; set; }
}

/// <summary>A query a test compares with: the database it runs against (a reference), its one SELECT, and how long it may run.</summary>
internal sealed class AssertionQueryYaml
{
    public string? Connection { get; set; }

    public string? Sql { get; set; }

    public int? TimeoutSeconds { get; set; }
}

internal sealed class AssertionSourceYaml
{
    public string? Endpoint { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? QueryPath { get; set; }

    public string? SearchPath { get; set; }

    /// <summary>cursor (the default) or createTime: how a read of every record a search matches pages through them.</summary>
    public string? Paging { get; set; }

    public string? RecordQueryPath { get; set; }

    public string? LegalPath { get; set; }

    public string? DdmsRoot { get; set; }
}

internal sealed class AssertionDefaultsYaml
{
    public int? MaxRecords { get; set; }

    public int? Examples { get; set; }

    public string? Read { get; set; }

    public int? IndexSettleSeconds { get; set; }
}

internal sealed class AssertionTestYaml
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>What people read the test as.</summary>
    public string? Title { get; set; }

    /// <summary>What the test is about, name to value, over its flow's.</summary>
    public Dictionary<string, string>? Dimensions { get; set; }

    public List<string>? Tags { get; set; }

    public List<string>? Partitions { get; set; }

    public string? Severity { get; set; }

    public string? Kind { get; set; }

    public string? Template { get; set; }

    public string? Query { get; set; }

    public List<string>? Ids { get; set; }

    // The search's spatialFilter as the document writes it: the mapper checks its shape (a field and exactly one filter).
    public object? Spatial { get; set; }

    public List<AssertionSortYaml>? Sort { get; set; }

    public string? Read { get; set; }

    public int? MaxRecords { get; set; }

    public bool? Sample { get; set; }

    public int? IndexSettleSeconds { get; set; }

    public AssertionBulkYaml? Bulk { get; set; }

    // Why the test is disabled: it stays in the flow and its reports, skipped with this reason.
    public string? Disabled { get; set; }

    // When the values the test expects were stated, and the last day they hold, or how long after asOf.
    public string? AsOf { get; set; }

    public string? ValidUntil { get; set; }

    public string? ValidFor { get; set; }

    // The date property a record holds on or before asOf to be among the test's records.
    public string? AsOfField { get; set; }

    public List<AssertionItemYaml>? Assert { get; set; }
}

internal sealed class AssertionSortYaml
{
    public string? Field { get; set; }

    public string? Order { get; set; }
}

internal sealed class AssertionBulkYaml
{
    public List<string>? Columns { get; set; }

    public long? MaxRows { get; set; }
}

/// <summary>
/// A condition on a value: what an assertion on a field or a column asks, and what each <c>where</c> condition selects the
/// records (or rows) it looks at by. A scalar operand keeps the type YAML reads it as, so <c>equals: 5</c> compares a number
/// and <c>equals: "5"</c> text.
/// </summary>
internal class AssertionConditionYaml
{
    public string? Field { get; set; }

    public string? Column { get; set; }

    [YamlMember(Alias = "equals")]
    public object? EqualTo { get; set; }

    public object? NotEquals { get; set; }

    public List<object>? In { get; set; }

    public List<object>? NotIn { get; set; }

    public object? AtLeast { get; set; }

    public object? AtMost { get; set; }

    public object? GreaterThan { get; set; }

    public object? LessThan { get; set; }

    public List<object>? Between { get; set; }

    public string? Matches { get; set; }

    public string? NotMatches { get; set; }

    public string? StartsWith { get; set; }

    public string? EndsWith { get; set; }

    public object? Contains { get; set; }

    public object? NotContains { get; set; }

    public bool? Exists { get; set; }

    public bool? Empty { get; set; }

    public string? Type { get; set; }

    // A number, or a comparison of the length ({ atLeast: 1, atMost: 40 }).
    public object? Length { get; set; }

    // true (the reference names a record that exists), or the entity type it has to point at (master-data--Well).
    public object? Resolves { get; set; }

    public bool? IgnoreCase { get; set; }

    public double? Tolerance { get; set; }
}

/// <summary>One assertion of a test: exactly one of its subject keys (count, field, column, aggregate, ...) says what it is.</summary>
internal sealed class AssertionItemYaml : AssertionConditionYaml
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Severity { get; set; }

    // Why the assertion is disabled: it is reported as skipped with this reason.
    public string? Disabled { get; set; }

    // When what the assertion expects was stated, and the last day it holds, or how long after asOf; its test's when it says neither.
    public string? AsOf { get; set; }

    public string? ValidUntil { get; set; }

    public string? ValidFor { get; set; }

    // A number (equals), or a comparison ({ atLeast: 1 }).
    public object? Count { get; set; }

    public string? Aggregate { get; set; }

    public List<string>? Unique { get; set; }

    public string? GroupBy { get; set; }

    // Each group's key with its count: a number (equals), or a comparison.
    public Dictionary<string, object>? Groups { get; set; }

    public List<string>? Absent { get; set; }

    public string? Mode { get; set; }

    public object? GroupCount { get; set; }

    public AssertionRecordSetYaml? RecordSet { get; set; }

    public AssertionKnownValuesYaml? KnownValues { get; set; }

    // The values an aggregate leaves out, each a scalar as a condition's operand is.
    public List<object>? Except { get; set; }

    // How far a count or an aggregate may miss its comparison and only warn: a distance (5) or a percentage (10%).
    public object? WarnWithin { get; set; }

    public bool? Conforms { get; set; }

    // The mapping (Name@version) whose record-stage assertions every record read is held to.
    public string? Mapping { get; set; }

    public bool? Indexed { get; set; }

    public string? Legal { get; set; }

    public string? Delivered { get; set; }

    public string? Interface { get; set; }

    public bool? Exact { get; set; }

    // Whether a delivered assertion starts from the rows its delivery flow stages, rather than from its ledger's delivered records.
    public bool? Staged { get; set; }

    // The severity of a staged row the delivery flow's ledger holds back and says why.
    public string? Explained { get; set; }

    public object? RowCount { get; set; }

    public AssertionColumnsYaml? Columns { get; set; }

    public string? Monotonic { get; set; }

    public string? For { get; set; }

    // A field assertion's quantifier over a record's values (all or any); a delivered assertion's delivery flow parameter values.
    public object? Values { get; set; }

    public bool? Optional { get; set; }

    public List<AssertionConditionYaml>? Where { get; set; }
}

internal sealed class AssertionRecordSetYaml
{
    public List<string>? Columns { get; set; }

    public string? Mode { get; set; }

    public List<List<object?>>? Rows { get; set; }
}

/// <summary>
/// Known values: the full paths and values every row is located by (<c>locateBy</c>) and checked in (<c>check</c>), the
/// rows, each adding paths of its own, and the settings the values take when they state none. A value is a scalar, or for a
/// proof value a condition written as a mapping, which the mapper reads from what YAML gives.
/// </summary>
internal sealed class AssertionKnownValuesYaml
{
    public Dictionary<string, object?>? LocateBy { get; set; }

    public Dictionary<string, object?>? Check { get; set; }

    public List<AssertionKnownRowYaml>? Rows { get; set; }

    public double? Tolerance { get; set; }

    // How far a number may miss a proof value and only warn, where the proof value compares a number and says nothing itself.
    public object? WarnWithin { get; set; }

    public bool? IgnoreCase { get; set; }

    public string? NotFound { get; set; }

    // The proof values every row holds its record's bulk data to: each curve, mapped to its measures and the proof value of each.
    public Dictionary<string, object?>? Curves { get; set; }

    // The curve the rows of each record's bulk data are indexed by, where the record names none at data.ReferenceCurveID.
    public string? CurveIndex { get; set; }

    // The query whose rows are the rows, by the name the flow declares it under; or written here, with connection and sql.
    public string? Query { get; set; }

    public string? Connection { get; set; }

    public string? Sql { get; set; }

    public int? TimeoutSeconds { get; set; }

    // The most rows the query may answer.
    public int? MaxRows { get; set; }
}

/// <summary>
/// One row of known values: the full paths and values it is located by and checked in, the proof values of its record's
/// bulk data, and why it is disabled, when it is.
/// </summary>
internal sealed class AssertionKnownRowYaml
{
    public Dictionary<string, object?>? LocateBy { get; set; }

    public Dictionary<string, object?>? Check { get; set; }

    public Dictionary<string, object?>? Curves { get; set; }

    public string? Disabled { get; set; }
}

internal sealed class AssertionColumnsYaml
{
    public List<string>? Includes { get; set; }

    public List<string>? Excludes { get; set; }

    [YamlMember(Alias = "equals")]
    public List<string>? Exactly { get; set; }
}
