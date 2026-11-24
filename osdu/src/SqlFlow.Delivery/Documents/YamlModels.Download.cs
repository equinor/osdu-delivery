// The YAML shapes of a download flow document (osdu/docs/reference/flow/download.md).
// As in YamlModels.cs, every property must be listed: unknown keys are rejected.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720

namespace SqlFlow.Delivery.Documents;

internal sealed class DownloadFlowYaml
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

    public DownloadSourceYaml? Source { get; set; }

    public DownloadTargetYaml? Target { get; set; }

    /// <summary>The partitions the flow downloads from, each by its data-partition-id.</summary>
    public List<string>? Partitions { get; set; }

    public List<DownloadYaml>? Downloads { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public RunParallelismYaml? Parallelism { get; set; }
}

/// <summary>The keys an inventory flow's source takes to list and read records; a download removes nothing, so takes none of the paths a removal sends to.</summary>
internal sealed class DownloadSourceYaml
{
    public string? Endpoint { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>search (the default) or storage.</summary>
    public string? Read { get; set; }

    public string? QueryPath { get; set; }

    public string? SearchPath { get; set; }

    /// <summary>cursor (the default) or createTime: how a read of every record a search matches pages through them.</summary>
    public string? Paging { get; set; }

    public string? RecordQueryPath { get; set; }

    public string? HeadersPath { get; set; }

    public string? SchemaPath { get; set; }
}

internal sealed class DownloadTargetYaml
{
    public string? Location { get; set; }
}

internal sealed class DownloadYaml
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Kind { get; set; }

    public string? Query { get; set; }
}
