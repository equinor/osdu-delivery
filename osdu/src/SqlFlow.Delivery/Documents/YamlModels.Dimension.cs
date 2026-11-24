// The YAML shapes of a dimension flow document (osdu/docs/reference/flow/dimension.md): its source, target,
// views and dimensions, and its parallelism.
// As in YamlModels.cs, every property must be listed: unknown keys are rejected.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// A dimension flow's <c>parallelism</c> block: what one build run works on at once, written <see cref="RecordsAtOnce"/> as
/// on a delivery flow, and the most nodes a build is spread over.
/// </summary>
internal sealed class DimensionParallelismYaml
{
    public int? RecordsAtOnce { get; set; }

    public int? Nodes { get; set; }

    public long? MinRecordsPerNode { get; set; }

    /// <summary>Renamed <see cref="RecordsAtOnce"/>; refused here.</summary>
    public int? Concurrency { get; set; }
}

internal sealed class DimensionFlowYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

    // The flow's operation code, which its runs' correlation id says what ran with (telemetry.operation).
    public TelemetryYaml? Telemetry { get; set; }

    public Dictionary<string, FlowParameterYaml>? Parameters { get; set; }

    public DimensionSourceYaml? Source { get; set; }

    /// <summary>The partitions the flow builds dimensions in, each by its data-partition-id.</summary>
    public List<string>? Partitions { get; set; }

    public List<DimensionYaml>? Dimensions { get; set; }

    /// <summary>Where the flow's tables and views are written; left out, the module's database.</summary>
    public DimensionTargetYaml? Target { get; set; }

    /// <summary>The views over the flow's dimension tables, each joining them at the grain of one.</summary>
    public List<DimensionViewYaml>? Views { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public DimensionParallelismYaml? Parallelism { get; set; }

    /// <summary>How a build loads the dimensions after their first full load; left out, every build loads them in full.</summary>
    public DimensionIncrementalYaml? Incremental { get; set; }
}

internal sealed class DimensionIncrementalYaml
{
    /// <summary>The properties that together identify one record: its unique key, <c>[id]</c> for an OSDU record.</summary>
    public List<string>? KeyColumns { get; set; }

    /// <summary>When a record last changed: the first of these it holds. Left out, <c>[modifyTime, createTime]</c>.</summary>
    public List<string>? DateColumns { get; set; }

    /// <summary>How far behind now a window ends, in minutes.</summary>
    public int? LagMinutes { get; set; }

    /// <summary>The age of a dimension's last full load, in hours, past which a build loads it in full.</summary>
    public int? FullLoadAfterHours { get; set; }

    /// <summary>Every build loads in full.</summary>
    public bool? FullLoad { get; set; }

    /// <summary>A build loads in full when a record the last full load read has left the index or no longer matches the query.</summary>
    public bool? FullLoadWhenRecordsLeave { get; set; }
}

internal sealed class DimensionTargetYaml
{
    /// <summary>A ${env:...} or ${keyvault:...} reference, or a connection string whose password is one; left out, the module's.</summary>
    public string? Connection { get; set; }

    /// <summary>A database on the connection's server; left out, the connection's own.</summary>
    public string? Database { get; set; }

    /// <summary>The schema the tables and views go to; left out, <c>osdu</c>.</summary>
    public string? Schema { get; set; }
}

internal sealed class DimensionViewYaml
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>The dimension whose rows the view's rows are.</summary>
    public string? From { get; set; }

    public List<DimensionViewJoinYaml>? Join { get; set; }

    // Each column by its name: an expression, or { expression, dataType, description }; read by the mapper.
    public Dictionary<string, object?>? Columns { get; set; }

    /// <summary>The condition a row of the view is kept by, over the same columns its expressions read.</summary>
    public string? Where { get; set; }

    // The columns the view groups its rows by: one, or a list; read by the mapper.
    public object? GroupBy { get; set; }
}

internal sealed class DimensionViewJoinYaml
{
    /// <summary>The column joined on: a column of the from dimension, bare, or alias.column of an earlier join.</summary>
    public string? On { get; set; }

    /// <summary>The dimension joined to.</summary>
    public string? To { get; set; }

    /// <summary>The view of the flow joined to, declared before this one and grouping by the key joined on; in place of to.</summary>
    public string? View { get; set; }

    /// <summary>The alias its columns are read by; the dimension's name when left out.</summary>
    public string? As { get; set; }
}

internal sealed class DimensionSourceYaml
{
    public string? Endpoint { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? QueryPath { get; set; }

    public string? SearchPath { get; set; }

    /// <summary>cursor (the default) or createTime: how a read of every record a search matches pages through them.</summary>
    public string? Paging { get; set; }

    /// <summary>How many groups the platform's aggregation returns: its AGGREGATION_SIZE, 1000 unless its operators changed it.</summary>
    public int? AggregationSize { get; set; }
}

internal sealed class DimensionYaml
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Kind { get; set; }

    public string? Query { get; set; }

    public string? Path { get; set; }

    // A path of the record each key names, or a list of them, each but the last reading a reference; read by the mapper.
    public object? Label { get; set; }

    // The value of a key whose label is not read ("Not specified").
    public string? Unlabelled { get; set; }

    // Each attribute by its name, read as a label is: a path, or a list of paths; read by the mapper.
    public Dictionary<string, object?>? Attributes { get; set; }

    // The objects of a nested array of each record, a row each with a column per field.
    public DimensionElementsYaml? Elements { get; set; }

    // The names of the table's key and value columns, when not the ones the path and the label give.
    public DimensionColumnsYaml? Columns { get; set; }

    // Each step a name ("trim") or a one-key map ({ replace: { pattern, with } }, { map: CurveAliases }), read by the mapper.
    public List<object>? Clean { get; set; }

    public bool? CountRecords { get; set; }

    public long? MaxValues { get; set; }

    /// <summary>The partitions of the flow this dimension is built in; every partition of the flow when left out.</summary>
    public List<string>? Partitions { get; set; }

    /// <summary>The kind the records of an entity type are looked up in by labels and attributes, one per entity type.</summary>
    public List<string>? LookupKinds { get; set; }
}

internal sealed class DimensionElementsYaml
{
    /// <summary>The nested array whose objects are rows, as a path from the record's root (data.Curves).</summary>
    public string? Path { get; set; }

    /// <summary>What a null in the array is: skip (no element, the default) or keep (an element of every field null).</summary>
    public string? Nulls { get; set; }

    // Each field by its column: a path inside the object, or { path, keep }; read by the mapper.
    public Dictionary<string, object?>? Fields { get; set; }
}

internal sealed class DimensionColumnsYaml
{
    /// <summary>The name of the column that holds each key; the property the path ends with when left out, with Key at its end beside a value's column of that name.</summary>
    public string? Key { get; set; }

    /// <summary>The name of the column that holds each key's value; the property the label ends with, or the dimension's name, when left out.</summary>
    public string? Value { get; set; }
}
