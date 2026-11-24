// The YAML shapes of a delivery flow document (osdu/docs/reference/flow/delivery.md): the document and its
// interfaces, and the source, render, change, cache, parameters, parallelism, verify and failWhen blocks they
// declare.
// As in YamlModels.cs, every property must be listed: unknown keys are rejected.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720

namespace SqlFlow.Delivery.Documents;

internal sealed class FlowYaml
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

    public Dictionary<string, DeliveryParameterYaml>? Parameters { get; set; }

    public FlowSourceYaml? Source { get; set; }

    public FlowRenderYaml? Render { get; set; }

    public FlowChangeYaml? Change { get; set; }

    public FlowTargetYaml? Target { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public FlowParallelismYaml? Parallelism { get; set; }

    public FlowVerifyYaml? Verify { get; set; }

    public FailWhenYaml? FailWhen { get; set; }

    /// <summary>What the pipeline brings up to date in its partition's cache before and after it runs.</summary>
    public FlowCacheYaml? Cache { get; set; }

    /// <summary>The source's interfaces, by name, in document order; null for the single form.</summary>
    public OrderedDictionary<string, InterfaceYaml?>? Interfaces { get; set; }

    // The partitions the flow may deliver to: each a name (dev), or a name with its settings ({ name: dev, keepLedger: true }).
    public List<object>? Partitions { get; set; }

    // For a flow that leaves its partitions to the registry: the partition that keeps the ledger it kept before (keepLedger: dev).
    public string? KeepLedger { get; set; }

    // For the single form: the earlier flow whose ledger this one keeps instead of its own (ledger: <old name>), as a renamed flow does.
    public string? Ledger { get; set; }

    /// <summary>
    /// Where enrichments were declared before the mapping declared them: read only to refuse it, saying where they go now
    /// (osdu/docs/reference/flow/enrich.md).
    /// </summary>
    public object? Enrich { get; set; }

    /// <summary>
    /// The OSDU workflows a delivery runs over an enrichment's records once they landed, by the name of the enrichment of the
    /// flow's mapping, in document order; null when none.
    /// </summary>
    public OrderedDictionary<string, WorkflowYaml?>? Workflows { get; set; }
}

/// <summary>The files a child dataset's rows name (osdu/docs/reference/flow/enrich-records.md): where they are, in each row's columns.</summary>
internal sealed class DatasetFilesYaml
{
    public string? Root { get; set; }

    public string? LocationColumn { get; set; }

    public string? Pattern { get; set; }

    public string? HashColumn { get; set; }

    public string? ChunkCountColumn { get; set; }

    public string? ContentType { get; set; }
}

/// <summary>
/// One interface of a source: its record table, its mapping and what its records carry, and the overrides of the
/// source's shared blocks. Everything else (the connection, the work location, the target) is the source's.
/// </summary>
internal sealed class InterfaceYaml
{
    public string? Description { get; set; }

    public string? Ledger { get; set; }

    public FlowSourceTableYaml? Record { get; set; }

    public Dictionary<string, FlowSourceDatasetYaml>? Datasets { get; set; }

    public FlowPayloadYaml? Files { get; set; }

    public FlowPayloadYaml? Bulk { get; set; }

    public string? LastModified { get; set; }

    public FlowSystemColumnsYaml? SystemColumns { get; set; }

    public FlowIncrementalYaml? Incremental { get; set; }

    public string? Mapping { get; set; }

    public string? Route { get; set; }

    public ProtocolOptionsYaml? ProtocolOptions { get; set; }

    public List<string>? After { get; set; }

    public FailWhenYaml? FailWhen { get; set; }

    /// <summary>The interface's own cache steps, which replace the source's.</summary>
    public FlowCacheYaml? Cache { get; set; }

    public InterfaceRenderYaml? Render { get; set; }

    public FlowChangeYaml? Change { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public FlowParallelismYaml? Parallelism { get; set; }

    public FlowVerifyYaml? Verify { get; set; }

    /// <summary>How the workflow route delivers the interface.</summary>
    public WorkflowYaml? Workflow { get; set; }

    /// <summary>The source's <c>target.validation</c>, with this interface's keys laid over it.</summary>
    public ValidationYaml? Validation { get; set; }

    /// <summary>Where an interface's enrichments were declared before its mapping declared them: read only to refuse it.</summary>
    public object? Enrich { get; set; }

    /// <summary>The OSDU workflows a delivery runs over the records of the enrichments of the interface's mapping; null when none.</summary>
    public OrderedDictionary<string, WorkflowYaml?>? Workflows { get; set; }
}

/// <summary>
/// A pipeline's cache steps: each <c>auto</c>, or a list of the cache types and entity types it brings up to date. A plain
/// object, since a step is either a word or a list.
/// </summary>
internal sealed class FlowCacheYaml
{
    public object? Before { get; set; }

    public object? After { get; set; }
}

/// <summary>What an interface may override of the source's render block: the mapping is the interface's own key.</summary>
internal sealed class InterfaceRenderYaml
{
    public string? CacheVersion { get; set; }

    public Dictionary<string, string>? Parameters { get; set; }
}

internal sealed class FailWhenYaml
{
    public double? FailedPercent { get; set; }

    public int? MinRecords { get; set; }

    public int? ConsecutiveFailures { get; set; }

    public int? OutageFailures { get; set; }
}

/// <summary>A delivery flow's parameter, whose values may be looked up in a column of its record table.</summary>
internal sealed class DeliveryParameterYaml : FlowParameterYaml
{
    public FlowParameterLookupYaml? Lookup { get; set; }
}

internal sealed class FlowSourceYaml
{
    public string? Connection { get; set; }

    public FlowSourceTableYaml? Record { get; set; }

    public Dictionary<string, FlowSourceDatasetYaml>? Datasets { get; set; }

    public Dictionary<string, FlowPayloadYaml>? Payloads { get; set; }

    public string? LastModified { get; set; }

    public FlowSystemColumnsYaml? SystemColumns { get; set; }

    public FlowIncrementalYaml? Incremental { get; set; }

    public string? Work { get; set; }
}

internal sealed class FlowSourceTableYaml
{
    public string? Object { get; set; }

    public List<string>? Key { get; set; }

    public string? PrimaryKey { get; set; }

    public Dictionary<string, string>? Scope { get; set; }

    public string? Filter { get; set; }

    public int? MaxRecords { get; set; }

    /// <summary>The lookup tables joined into every read of the table, by the name that prefixes the columns each adds.</summary>
    public Dictionary<string, FlowSourceLookupYaml>? Lookups { get; set; }
}

internal sealed class FlowSourceDatasetYaml
{
    public string? Object { get; set; }

    public Dictionary<string, string>? Join { get; set; }

    public List<string>? OrderBy { get; set; }

    public int? MaxRowsPerRecord { get; set; }

    public string? Filter { get; set; }

    /// <summary>The files each row names, which an enrichment of the flow's mapping reading these rows registers or sends.</summary>
    public DatasetFilesYaml? Files { get; set; }

    /// <summary>The lookup tables joined into every read of the table, by the name that prefixes the columns each adds.</summary>
    public Dictionary<string, FlowSourceLookupYaml>? Lookups { get; set; }
}

/// <summary>
/// A lookup table joined into the reads of a source table: the table, the columns of its key each matched with a column of the
/// joining table (lookup column to joining column), and the columns it adds.
/// </summary>
internal sealed class FlowSourceLookupYaml
{
    public string? Object { get; set; }

    public Dictionary<string, string>? Join { get; set; }

    public List<string>? Columns { get; set; }
}

internal sealed class FlowPayloadYaml
{
    public string? Root { get; set; }

    public string? LocationColumn { get; set; }

    public string? Pattern { get; set; }

    public string? HashColumn { get; set; }

    public string? ChunkCountColumn { get; set; }
}

/// <summary>A system column a flow names, opts out of with <c>~</c>, or leaves at its default by not naming it.</summary>
internal sealed class FlowSystemColumnsYaml
{
    private string? _updated;
    private string? _fileName;
    private string? _rowNumber;
    private string? _deleted;
    private string? _inserted;

    public string? Updated
    {
        get => _updated;
        set
        {
            _updated = value;
            HasUpdated = true;
        }
    }

    public string? FileName
    {
        get => _fileName;
        set
        {
            _fileName = value;
            HasFileName = true;
        }
    }

    public string? RowNumber
    {
        get => _rowNumber;
        set
        {
            _rowNumber = value;
            HasRowNumber = true;
        }
    }

    public string? Deleted
    {
        get => _deleted;
        set
        {
            _deleted = value;
            HasDeleted = true;
        }
    }

    public string? Inserted
    {
        get => _inserted;
        set
        {
            _inserted = value;
            HasInserted = true;
        }
    }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasUpdated { get; private set; }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasFileName { get; private set; }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasRowNumber { get; private set; }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasDeleted { get; private set; }

    [YamlDotNet.Serialization.YamlIgnore]
    public bool HasInserted { get; private set; }
}

internal sealed class FlowIncrementalYaml
{
    public int? OverlapSeconds { get; set; }

    public int? PageSize { get; set; }

    public string? Isolation { get; set; }

    public int? CommandTimeoutSeconds { get; set; }
}

internal sealed class FlowRenderYaml
{
    public string? Mapping { get; set; }

    public string? Cache { get; set; }

    public string? CacheVersion { get; set; }

    public Dictionary<string, string>? Parameters { get; set; }

    public string? Mappings { get; set; }
}

internal sealed class FlowChangeYaml
{
    public string? Detect { get; set; }

    public string? PayloadDetect { get; set; }

    public string? OnUnchanged { get; set; }

    public bool? UseSourceVersions { get; set; }
}

/// <summary>
/// A delivery flow's <c>parallelism</c> block: the most nodes a delivery is spread over, and the settings a run otherwise
/// works out itself. In a source with interfaces an interface lays its own keys over the source's, except
/// <see cref="InterfacesAtOnce"/>.
/// </summary>
internal sealed class FlowParallelismYaml
{
    public int? Nodes { get; set; }

    public int? MinRowsPerNode { get; set; }

    public int? RecordsAtOnce { get; set; }

    public int? RenderThreads { get; set; }

    public int? RecordsPerBatch { get; set; }

    /// <summary>A source's setting only: how many of its interfaces run at once.</summary>
    public int? InterfacesAtOnce { get; set; }

    /// <summary>Renamed <see cref="RecordsAtOnce"/>; refused here.</summary>
    public int? Concurrency { get; set; }

    /// <summary>Renamed <see cref="Nodes"/>; refused here.</summary>
    public int? FanOut { get; set; }

    /// <summary>Renamed <see cref="MinRowsPerNode"/>; refused here.</summary>
    public int? FanOutMinRecords { get; set; }

    /// <summary>Renamed <see cref="RenderThreads"/>; refused here.</summary>
    public int? Renderers { get; set; }

    /// <summary>Renamed <see cref="RecordsPerBatch"/>; refused here.</summary>
    public int? BatchRecords { get; set; }

    /// <summary>Gone: retries are claimed <see cref="RecordsAtOnce"/> at a time; refused here.</summary>
    public int? BatchSize { get; set; }

    /// <summary>Renamed <see cref="InterfacesAtOnce"/>; refused here.</summary>
    public int? Interfaces { get; set; }
}

internal sealed class FlowVerifyYaml
{
    public bool? Reconcile { get; set; }
}
