// The YAML shapes of an inventory flow document (osdu/docs/reference/flow/inventory.md).
// As in YamlModels.cs, every property must be listed: unknown keys are rejected.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720

namespace SqlFlow.Delivery.Documents;

internal sealed class InventoryFlowYaml
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

    public InventorySourceYaml? Source { get; set; }

    /// <summary>The partitions the flow keeps inventories of, each by its data-partition-id.</summary>
    public List<string>? Partitions { get; set; }

    /// <summary>The identities whose records are this estate's: a record no ledger knows that one of them created is an orphan.</summary>
    public List<string>? Owners { get; set; }

    /// <summary>How many ids a ledger expects that one build reads from storage to tell missing from merely unlisted.</summary>
    public int? MaxMissingChecks { get; set; }

    public List<InventoryYaml>? Inventories { get; set; }

    /// <summary>What an operator may remove from OSDU of what the inventories found; none when left out.</summary>
    public InventoryRemovalYaml? Removal { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public RunParallelismYaml? Parallelism { get; set; }
}

internal sealed class InventoryRemovalYaml
{
    /// <summary>The findings whose ids may be removed: orphan, stale, forgotten.</summary>
    public List<string>? Findings { get; set; }

    /// <summary>Whether a removal may purge rather than soft delete.</summary>
    public bool? Purge { get; set; }
}

internal sealed class InventorySourceYaml
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

    public string? VersionsPath { get; set; }

    public string? SchemaPath { get; set; }

    public string? DeletePath { get; set; }

    public string? BulkDeletePath { get; set; }

    public string? PurgePath { get; set; }
}

internal sealed class InventoryYaml
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Kind { get; set; }

    public string? Query { get; set; }

    /// <summary>latest (the default) or all.</summary>
    public string? Versions { get; set; }
}
