// The YAML shapes of a retrieval flow document (osdu/docs/reference/flow/retrieval.md).
// As in YamlModels.cs, every property must be listed: unknown keys are rejected.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720

namespace SqlFlow.Delivery.Documents;

internal sealed class RetrievalYaml
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

    public RetrievalSourceYaml? Source { get; set; }

    public RetrievalTargetYaml? Target { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public RunParallelismYaml? Parallelism { get; set; }
}

internal sealed class RetrievalSourceYaml
{
    public string? Endpoint { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? Kind { get; set; }

    public List<string>? Kinds { get; set; }

    public string? Query { get; set; }

    public List<string>? ReturnedFields { get; set; }

    public int? PageSize { get; set; }

    public string? SearchPath { get; set; }

    /// <summary>cursor (the default) or createTime: how a read of every record a search matches pages through them.</summary>
    public string? Paging { get; set; }

    public string? QueryPath { get; set; }

    public RetrievalIncrementalYaml? Incremental { get; set; }

    public bool? FetchRecords { get; set; }

    public string? RecordQueryPath { get; set; }

    public int? FetchParallelism { get; set; }

    public string? ProbePath { get; set; }
}

internal sealed class RetrievalIncrementalYaml
{
    public string? Field { get; set; }

    public string? Since { get; set; }

    public int? LagMinutes { get; set; }
}

internal sealed class RetrievalTargetYaml
{
    public string? Location { get; set; }

    public string? Format { get; set; }

    public string? Compression { get; set; }

    public long? RollRecords { get; set; }

    public string? Manifest { get; set; }
}
