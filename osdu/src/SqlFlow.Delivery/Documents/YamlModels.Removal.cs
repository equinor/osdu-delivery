// The YAML shapes of a removal flow document (osdu/docs/reference/flow/removal.md).
// As in YamlModels.cs, every property must be listed: unknown keys are rejected.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720

namespace SqlFlow.Delivery.Documents;

internal sealed class RemovalFlowYaml
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

    /// <summary>The partitions the flow removes in, each by its data-partition-id.</summary>
    public List<string>? Partitions { get; set; }

    /// <summary>When a run stops before removing anything, for an operator to approve.</summary>
    public RemovalGuardYaml? Guard { get; set; }

    public List<RemovalEntryYaml>? Removals { get; set; }
}

internal sealed class RemovalGuardYaml
{
    /// <summary>The share, 0 to 100, of the keys a removal compares it may take out without an approval.</summary>
    public int? MaxPercent { get; set; }

    /// <summary>How many keys one removal may take out without an approval.</summary>
    public long? MaxRecords { get; set; }
}

internal sealed class RemovalEntryYaml
{
    public string? Name { get; set; }

    /// <summary>The delivery flow whose records the removal takes out, by name.</summary>
    public string? Flow { get; set; }

    /// <summary>The interface of that flow, for a source with interfaces.</summary>
    public string? Interface { get; set; }

    /// <summary>The values of the delivery flow's parameters the comparison is scoped by, each one value or a list.</summary>
    public Dictionary<string, RunValueYaml?>? Values { get; set; }

    /// <summary>Whether a record other records still name stays in OSDU until they are gone (the default).</summary>
    public bool? KeepReferenced { get; set; }

    /// <summary>The source's current, complete key set.</summary>
    public RemovalKeySourceYaml? Keys { get; set; }

    /// <summary>A source listing the keys the source removed.</summary>
    public RemovalKeySourceYaml? Removed { get; set; }
}

internal sealed class RemovalKeySourceYaml
{
    /// <summary>A SQLFlow source reader: csv, xls, xlsx, json, jsonl, ndjson, xml, parquet, prq, duckdb, delta.</summary>
    public string? Type { get; set; }

    public string? Location { get; set; }

    public Dictionary<string, string?>? Options { get; set; }

    /// <summary>The connection of a source database, a reference.</summary>
    public string? Connection { get; set; }

    /// <summary>The source system: mssql (the default), azdb, mysql, postgres or oracle.</summary>
    public string? Provider { get; set; }

    /// <summary>The table or view, a three-part name.</summary>
    public string? Object { get; set; }

    /// <summary>A predicate narrowing the rows read, with its own leading AND.</summary>
    public string? Filter { get; set; }

    public List<RemovalKeyColumnYaml>? Columns { get; set; }

    /// <summary>The column holding the moment a key was removed, for a source of removed keys.</summary>
    public RemovalKeyColumnYaml? RemovedAt { get; set; }
}

internal sealed class RemovalKeyColumnYaml
{
    public string? Name { get; set; }

    public string? As { get; set; }

    public string? Expr { get; set; }
}
