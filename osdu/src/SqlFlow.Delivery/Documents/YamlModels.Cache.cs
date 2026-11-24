// The YAML shapes of a cache flow document (osdu/docs/reference/flow/cache.md): its source and the types it
// caches.
// As in YamlModels.cs, every property must be listed: unknown keys are rejected.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720

namespace SqlFlow.Delivery.Documents;

internal sealed class CacheYaml
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

    public CacheSourceYaml? Source { get; set; }

    public bool? MakeCurrent { get; set; }

    public string? OnChange { get; set; }

    /// <summary>How many days the partition's cache keeps the records of a version after a newer one replaced it.</summary>
    public int? RetentionDays { get; set; }

    public List<CachedTypeYaml>? Types { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    /// <summary>The partitions the flow builds a cache for, each by its data-partition-id.</summary>
    public List<string>? Partitions { get; set; }
}

internal sealed class CacheSourceYaml
{
    public string? Endpoint { get; set; }

    /// <summary>The database the flow's table types are read from, declared as a delivery flow's source.connection is.</summary>
    public string? Connection { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>cursor (the default) or createTime: how a read of every record a search matches pages through them.</summary>
    public string? Paging { get; set; }
}

internal sealed class CachedTypeYaml
{
    public string? Name { get; set; }

    public string? EntityType { get; set; }

    public string? Kind { get; set; }

    public string? Query { get; set; }

    public string? OnChange { get; set; }

    // A path ("data.Code") or a mapping of path and as ({ path: data.NameAlias.AliasName, as: Alias }).
    public List<object>? Fields { get; set; }

    /// <summary>For an OSDU type: read each record the search finds whole from storage, so a path the index does not hold is kept.</summary>
    public bool? FetchRecords { get; set; }

    /// <summary>
    /// For an OSDU type: values read as a dimension's attributes are, by name, from the cached record and through the records
    /// its references name: a path, a list of paths, or settings, exactly as a dimension's <c>attributes</c> are written.
    /// </summary>
    public Dictionary<string, object?>? Attributes { get; set; }

    /// <summary>For an OSDU type with attributes: the one kind each entity type's records are looked up in, as a dimension's <c>lookupKinds</c>.</summary>
    public List<string>? LookupKinds { get; set; }

    /// <summary>The dictionary document the type holds, in place of a kind: a lookup table kept in the repository.</summary>
    public string? Dictionary { get; set; }

    /// <summary>The ingestion table the type reads, in place of a kind: a lookup table SQLFlow's own flows load.</summary>
    public string? Table { get; set; }

    /// <summary>For a table type: the column each row is keyed by.</summary>
    public string? Key { get; set; }

    /// <summary>For a table type: the most rows its capture takes, 1,000,000 when left out; a table holding more refuses the capture.</summary>
    public int? MaxRows { get; set; }

    /// <summary>The dimension the type holds the members of, in place of a kind: a lookup table a dimension flow builds.</summary>
    public string? Dimension { get; set; }

    /// <summary>For a dimension type: the dimension flow that declares the dimension.</summary>
    public string? DimensionFlow { get; set; }

    /// <summary>The partitions of the flow this type is built for; every partition of the flow when left out.</summary>
    public List<string>? Partitions { get; set; }
}
