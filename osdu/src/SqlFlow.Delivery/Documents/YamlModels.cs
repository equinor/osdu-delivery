// Mutable YAML shapes. They exist only so YamlDotNet can populate them; the loader maps them to the immutable model
// records in SqlFlow.Delivery.Model and validates as it goes. Every property must be listed: unknown keys are rejected.
// The platform envelope keys (name, batch, description, schedule, mode, lifecycle) are listed too: the platform
// parses them itself, the delivery loader only has to let them pass.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720
using YamlDotNet.Serialization;

namespace SqlFlow.Delivery.Documents;

internal sealed class DocumentProbeYaml
{
    public string? FlowType { get; set; }

    public string? DocumentType { get; set; }
}

internal sealed class FlowYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

    public Dictionary<string, FlowParameterYaml>? Parameters { get; set; }

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

/// <summary>The workflow route's declaration (<c>interfaces.&lt;name&gt;.workflow</c>, or <c>target.workflow</c> in the single form).</summary>
internal sealed class WorkflowYaml
{
    /// <summary>How the record is written first: dataset (registered with its files) or storage.</summary>
    public string? Anchor { get; set; }

    public List<WorkflowStageYaml>? Stages { get; set; }

    /// <summary>The payload sets registered as the workflow's inputs, by name, in document order.</summary>
    public OrderedDictionary<string, WorkflowInputYaml?>? Inputs { get; set; }

    public WorkflowResultsYaml? Results { get; set; }

    /// <summary>changed (the default), created or requested.</summary>
    public string? RunWhen { get; set; }

    /// <summary>The secrets a context names with {secret:name}, as references.</summary>
    public Dictionary<string, string>? Secrets { get; set; }

    /// <summary>The tag key the route writes on the record with its anchor tag.</summary>
    public string? AnchorTag { get; set; }
}

internal sealed class WorkflowStageYaml
{
    public string? Workflow { get; set; }

    public string? Contract { get; set; }

    /// <summary>The execution context: any YAML, with placeholders in its strings.</summary>
    public Dictionary<string, object?>? Context { get; set; }

    public int? TimeoutMinutes { get; set; }

    public int? PollSeconds { get; set; }

    public OrderedDictionary<string, WorkflowOutputYaml?>? Outputs { get; set; }
}

internal sealed class WorkflowInputYaml
{
    public string? Root { get; set; }

    public string? LocationColumn { get; set; }

    public string? Pattern { get; set; }

    public string? HashColumn { get; set; }

    public string? ChunkCountColumn { get; set; }

    public string? DatasetKind { get; set; }

    public bool? Optional { get; set; }
}

internal sealed class WorkflowOutputYaml
{
    public string? Value { get; set; }

    public WorkflowXComYaml? Xcom { get; set; }
}

internal sealed class WorkflowXComYaml
{
    public string? Task { get; set; }

    public string? Key { get; set; }

    public string? Match { get; set; }
}

internal sealed class WorkflowResultsYaml
{
    /// <summary>True when the run writes the record itself, which is read back.</summary>
    public bool? Anchor { get; set; }

    public string? Ids { get; set; }

    public WorkflowArtefactYaml? Artefact { get; set; }

    public WorkflowSearchYaml? Search { get; set; }

    public string? Manifest { get; set; }

    public WorkflowXComYaml? Xcom { get; set; }

    public int? Minimum { get; set; }

    public int? WaitSeconds { get; set; }

    public int? Keep { get; set; }

    public bool? Remove { get; set; }
}

internal sealed class WorkflowArtefactYaml
{
    public string? Role { get; set; }

    public string? Kind { get; set; }
}

internal sealed class WorkflowSearchYaml
{
    public string? Kind { get; set; }

    public string? Query { get; set; }
}

/// <summary>The Airflow instance behind the target's Workflow service (<c>target.airflow</c>).</summary>
internal sealed class AirflowYaml
{
    public string? Endpoint { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? ApiVersion { get; set; }
}

/// <summary>The External Data Services deployment behind the target (<c>target.eds</c>).</summary>
internal sealed class EdsYaml
{
    /// <summary>Whether the records that configure EDS are checked before they are sent. Default true.</summary>
    public bool? Checks { get; set; }

    /// <summary>Whether proxy datasets are retrieved through eds-dms, which needs every registry entry's DatasetURL. Default true.</summary>
    public bool? Retrieval { get; set; }

    /// <summary>The eds-dms build the partition runs: corePlus, azure or gc.</summary>
    public string? Build { get; set; }
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

internal sealed class FlowParameterYaml
{
    public bool Required { get; set; }

    public string? Default { get; set; }

    public string? Description { get; set; }
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
}

internal sealed class FlowSourceDatasetYaml
{
    public string? Object { get; set; }

    public Dictionary<string, string>? Join { get; set; }

    public List<string>? OrderBy { get; set; }

    public int? MaxRowsPerRecord { get; set; }
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

internal sealed class FlowTargetYaml
{
    public string? Endpoint { get; set; }

    public TargetAuthYaml? Auth { get; set; }

    public Dictionary<string, string>? Headers { get; set; }

    public string? Protocol { get; set; }

    public ProtocolOptionsYaml? ProtocolOptions { get; set; }

    /// <summary>The DDMSs the flow delivers to, by the names the flow gives them, in document order.</summary>
    public OrderedDictionary<string, DdmsYaml?>? Ddms { get; set; }

    /// <summary>The workflow route's declaration of a document in the single form.</summary>
    public WorkflowYaml? Workflow { get; set; }

    public AirflowYaml? Airflow { get; set; }

    /// <summary>The External Data Services deployment behind the target, and how the records that configure it are checked.</summary>
    public EdsYaml? Eds { get; set; }

    /// <summary>The Production DDMS core service the dspdm route writes business object rows to.</summary>
    public DspdmYaml? Dspdm { get; set; }

    /// <summary>The Reservoir DDMS the etp route writes Energistics objects to.</summary>
    public EtpYaml? Etp { get; set; }

    /// <summary>What a record's references are checked against before it is sent: none (the ledger alone) or storage.</summary>
    public string? VerifyReferences { get; set; }

    /// <summary>What the gate before a record is sent does with the record's verdict.</summary>
    public ValidationYaml? Validation { get; set; }
}

/// <summary>
/// What the gate before a record is sent does with the record's verdict (<c>target.validation</c>, or an interface's
/// <c>validation</c> laid over it key by key).
/// </summary>
internal sealed class ValidationYaml
{
    /// <summary>report (the default): a record that breaks its schema is sent and its verdict recorded; enforce: it is held.</summary>
    public string? Mode { get; set; }

    /// <summary>send (the default): a record some part of which could not be checked is sent; hold: it is held.</summary>
    public string? Unverified { get; set; }
}

/// <summary>The Reservoir DDMS behind the target (<c>target.etp</c>).</summary>
internal sealed class EtpYaml
{
    /// <summary>Where the ETP WebSocket answers under the endpoint; the OSDU deployment's path by default.</summary>
    public string? Path { get; set; }

    /// <summary>The dataspace records go into when their document names none (<c>project/study</c>).</summary>
    public string? Dataspace { get; set; }

    /// <summary>Objects per message, which is also the batch the worker hands the route. Default 100.</summary>
    public int? ObjectsPerMessage { get; set; }

    /// <summary>The largest message this side sends or accepts, before the server's own maximum narrows it.</summary>
    public long? MaxMessageBytes { get; set; }

    /// <summary>The most bytes of one array a delivery reads into memory. Default 256 MiB.</summary>
    public long? MaxArrayBytes { get; set; }

    /// <summary>Whether a delivery leaves the dataspace locked, unlocking it before it writes. Default false.</summary>
    public bool? Lock { get; set; }
}

/// <summary>The Production DDMS core service behind the target (<c>target.dspdm</c>).</summary>
internal sealed class DspdmYaml
{
    /// <summary>Where DSPDM sits under the endpoint (<c>/api/dspdm/v1</c>); left out when the endpoint is DSPDM.</summary>
    public string? Root { get; set; }

    /// <summary>The time zone every request names: GMT+hh:mm. Default GMT+00:00.</summary>
    public string? Timezone { get; set; }

    /// <summary>The business objects the flow's kinds are rows of, by entity type.</summary>
    public OrderedDictionary<string, DspdmBusinessObjectYaml?>? BusinessObjects { get; set; }

    /// <summary>What happens to a row found by a record's key that the record did not write: hold (default) or update.</summary>
    public string? ExistingRows { get; set; }
}

/// <summary>One business object under <c>target.dspdm.businessObjects</c>.</summary>
internal sealed class DspdmBusinessObjectYaml
{
    /// <summary>Its name in DSPDM (<c>WELL TEST</c>); the entity type's when left out.</summary>
    public string? Name { get; set; }

    /// <summary>The attributes a row is found again by: one of its unique constraints.</summary>
    public List<string>? Key { get; set; }
}

/// <summary>One DDMS a flow declares under <c>target.ddms</c>.</summary>
internal sealed class DdmsYaml
{
    /// <summary>Where the DDMS is under the endpoint (<c>/api/os-wellbore-ddms</c>); null when the endpoint is the DDMS.</summary>
    public string? Root { get; set; }

    /// <summary>Its call pattern (<c>wellboreDdmsV3</c>, the default, <c>wellDeliveryV1</c>, <c>rafsV2</c>, <c>productionTimeSeriesV1</c>, <c>seismicStoreV3</c> or <c>reservoirManagement</c>).</summary>
    public string? Shape { get; set; }

    /// <summary>The id the DDMS is registered under in the Register service, to look up what the flow does not declare.</summary>
    public string? Register { get; set; }

    /// <summary>The entity types it serves, each with the collection it serves it under; the shape's own when left out.</summary>
    public OrderedDictionary<string, DdmsCollectionYaml?>? Collections { get; set; }

    /// <summary>Well Delivery DDMS: whether the deployment copies every entity into Storage. Default true.</summary>
    public bool? Mirror { get; set; }

    /// <summary>Well Delivery DDMS and Seismic Store: the provider the deployment runs on (azure, aws, gc, anthos, ibm, as the shape takes them).</summary>
    public string? Provider { get; set; }

    /// <summary>Well Delivery DDMS: the writes one process sends to the deployment at a time. Default 1.</summary>
    public int? Concurrency { get; set; }

    /// <summary>Production DDMS historian: where its query service is under the endpoint. Default <c>/api/pddms/query/v1</c>.</summary>
    public string? QueryRoot { get; set; }

    /// <summary>
    /// Production DDMS historian: how long a delivery reads accepted points back for, in seconds (default 60; 0 does not read
    /// them back). Reservoir Management DDMS: how long a delivery with rows waits for the service to take its record in.
    /// </summary>
    public int? SettleSeconds { get; set; }

    /// <summary>Production DDMS historian and Reservoir Management DDMS: the pause between two asks, in seconds. Default 5.</summary>
    public int? PollSeconds { get; set; }

    /// <summary>Production DDMS historian: the largest request body points are sent in, in bytes. Default 8000000.</summary>
    public long? MaxRequestBytes { get; set; }

    /// <summary>Seismic Store: the tenant the datasets are registered under. Default: the flow's <c>data-partition-id</c>.</summary>
    public string? Tenant { get; set; }

    /// <summary>Seismic Store: the subproject the datasets are registered in. Required.</summary>
    public string? Subproject { get; set; }

    /// <summary>Seismic Store: the folder under the subproject the datasets are registered in (<c>seismic/raw</c>). Default: the subproject's root.</summary>
    public string? Folder { get; set; }

    /// <summary>Seismic Store: the object store endpoint on anthos and IBM, or another Google Cloud Storage endpoint.</summary>
    public string? ObjectStore { get; set; }

    /// <summary>Seismic Store: the region S3 requests are signed for. Default us-east-1.</summary>
    public string? Region { get; set; }

    /// <summary>Seismic Store: the size in MiB of each object a single file is cut into on Azure (0 keeps it whole), and of each block or part. Default 32.</summary>
    public int? ChunkMiB { get; set; }

    /// <summary>Seismic Store: whether a delivered dataset is closed read-only. Default false.</summary>
    public bool? ReadOnly { get; set; }
}

/// <summary>One collection of a declared DDMS.</summary>
internal sealed class DdmsCollectionYaml
{
    /// <summary>The path segment the collection is served under (<c>welllogs</c>).</summary>
    public string? Path { get; set; }

    /// <summary>Whether the collection stores bulk data beside its records. Default false.</summary>
    public bool? Bulk { get; set; }

    /// <summary>What the bulk data's columns are checked against: unchecked (the default), curveIds or trajectoryStations.</summary>
    public string? Columns { get; set; }

    /// <summary>RAFS content collection: whether it holds several content types, each under its own path segment. Default false.</summary>
    public bool? TypedContent { get; set; }
}

internal sealed class TargetAuthYaml
{
    public string? Type { get; set; }

    public string? SecretRef { get; set; }

    public string? SecondarySecretRef { get; set; }

    public string? HeaderName { get; set; }

    public string? ValuePrefix { get; set; }

    public TokenEndpointYaml? Token { get; set; }
}

internal sealed class TokenEndpointYaml
{
    public string? Url { get; set; }

    public string? DiscoveryUrl { get; set; }

    public Dictionary<string, string>? Body { get; set; }

    public bool BasicAuthClient { get; set; }

    public string? TokenPath { get; set; }

    public string? ApplyPrefix { get; set; }
}

internal sealed class ProtocolOptionsYaml
{
    public string? RecordPath { get; set; }

    public string? RecordMethod { get; set; }

    public string? DataPath { get; set; }

    public string? SessionPath { get; set; }

    public string? SessionDataPath { get; set; }

    public string? SessionCommitPath { get; set; }

    public string? VerifyPath { get; set; }

    public string? DeletePath { get; set; }

    public string? PurgePath { get; set; }

    public string? PurgeVersionsPath { get; set; }

    public string? BulkDeletePath { get; set; }

    public string? ProbePath { get; set; }

    public string? Payload { get; set; }

    public int? SessionThresholdChunks { get; set; }

    public long? MaxChunkValues { get; set; }

    public int? MaxChunkColumns { get; set; }

    public string? PayloadContentType { get; set; }

    public string? FilesContentType { get; set; }

    public string? VersionPath { get; set; }

    public bool? SkipDuplicates { get; set; }

    public string? VerifyBatchPath { get; set; }

    public string? DdmsRoot { get; set; }

    public bool? ValidateLegalTags { get; set; }

    public string? LegalValidatePath { get; set; }

    public List<string>? PreserveDataKeys { get; set; }

    public int? BatchSize { get; set; }

    public string? UploadUrlPath { get; set; }

    public string? FileMetadataPath { get; set; }

    public string? DatasetKind { get; set; }

    public Dictionary<string, string>? UploadHeaders { get; set; }

    public string? DatasetsProperty { get; set; }

    public string? WorkflowName { get; set; }

    public string? WorkflowRunPath { get; set; }

    public string? WorkflowStatusPath { get; set; }

    public int? WorkflowPollSeconds { get; set; }

    public int? WorkflowTimeoutMinutes { get; set; }

    public int? DatasetIndexWaitSeconds { get; set; }

    public string? SearchQueryPath { get; set; }

    public string? ManifestKind { get; set; }

    public string? UploadUrlExpiry { get; set; }

    public string? FileDeletePath { get; set; }

    public string? ManifestSection { get; set; }

    public string? WorkflowAppKey { get; set; }

    public Dictionary<string, string>? WorkflowPayload { get; set; }

    public string? RecordQueryPath { get; set; }

    public string? RegisterPath { get; set; }

    public string? DatasetInstructionsPath { get; set; }

    public string? DatasetRegisterPath { get; set; }

    public string? DatasetRetrievalPath { get; set; }

    public string? DatasetSoftDeletePath { get; set; }

    public string? ManifestByReference { get; set; }

    public int? ManifestInlineLimitKb { get; set; }

    public string? ByReferenceWorkflowName { get; set; }

    public string? WorkflowPath { get; set; }

    public string? ContentSchemaVersion { get; set; }
}

/// <summary>
/// The <c>reliability</c> block: what makes a flow's requests hold. The settings that moved to <c>parallelism</c> are read
/// only so the loader can refuse each with the key it is now written as (<see cref="FlowMapper"/>), rather than with a bare
/// "unknown key".
/// </summary>
internal sealed class FlowReliabilityYaml
{
    public FlowRetryYaml? Retry { get; set; }

    public List<int>? SkipStatusCodes { get; set; }

    public int? TimeoutSeconds { get; set; }

    public double? RateLimitRps { get; set; }

    public bool? VerifyTls { get; set; }

    public List<string>? UrlAllowlist { get; set; }

    public long? MaxResponseBytes { get; set; }

    public long? MaxRequestBodyBytes { get; set; }

    public int? LeaseSeconds { get; set; }

    /// <summary>Moved to <c>parallelism.recordsAtOnce</c>; refused here.</summary>
    public int? Concurrency { get; set; }

    /// <summary>Gone: retries are claimed <c>parallelism.recordsAtOnce</c> at a time; refused here.</summary>
    public int? BatchSize { get; set; }

    /// <summary>Moved to <c>parallelism.recordsPerBatch</c>; refused here.</summary>
    public int? BatchRecords { get; set; }

    /// <summary>Moved to <c>parallelism.nodes</c>; refused here.</summary>
    public int? FanOut { get; set; }

    /// <summary>Moved to <c>parallelism.minRowsPerNode</c>; refused here.</summary>
    public int? FanOutMinRecords { get; set; }

    /// <summary>Moved to <c>parallelism.renderThreads</c>; refused here.</summary>
    public int? RenderParallelism { get; set; }

    /// <summary>Moved to <c>parallelism.interfacesAtOnce</c>; refused here.</summary>
    public int? ParallelInterfaces { get; set; }
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

/// <summary>
/// The <c>parallelism</c> block of an assertion, dimension, inventory or retrieval flow: what one run works on at once,
/// written <see cref="RecordsAtOnce"/> as on a delivery flow.
/// </summary>
internal sealed class RunParallelismYaml
{
    public int? RecordsAtOnce { get; set; }

    /// <summary>Renamed <see cref="RecordsAtOnce"/>; refused here.</summary>
    public int? Concurrency { get; set; }
}

internal sealed class FlowRetryYaml
{
    public int? Attempts { get; set; }

    public string? Backoff { get; set; }

    public int? BaseDelayMs { get; set; }

    public int? MaxDelayMs { get; set; }

    public bool? HonorRetryAfter { get; set; }

    public int? RecordBaseDelayMinutes { get; set; }

    public int? RecordMaxDelayMinutes { get; set; }
}

internal sealed class FlowVerifyYaml
{
    public bool? Reconcile { get; set; }
}

internal sealed class MappingYaml
{
    public string? DocumentType { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public MappingTemplateYaml? Template { get; set; }

    public string? Description { get; set; }

    public MappingDatasetYaml? Dataset { get; set; }

    public Dictionary<string, MappingParameterYaml>? Parameters { get; set; }

    /// <summary>What each <c>search: &lt;name&gt;</c> node searches, by the name nodes write it as.</summary>
    public Dictionary<string, MappingSearchYaml>? Searches { get; set; }

    /// <summary>
    /// The records a row is matched to once and read wherever the record needs them, by the name <c>$lookup</c> reads them
    /// as. Read as the author wrote it and parsed node by node, like the record tree.
    /// </summary>
    public Dictionary<string, object?>? Lookups { get; set; }

    /// <summary>
    /// The record the mapping renders, laid out as the record is: <c>acl</c>, <c>legal</c>, <c>tags</c>, <c>data</c> and
    /// the properties below them, each a literal, a value node or a <c>forEach</c> array
    /// (osdu/docs/reference/flow/mapping.md). Read as the author wrote it and parsed node by node, since its keys are
    /// the template's property names.
    /// </summary>
    public Dictionary<string, object?>? Record { get; set; }
}

/// <summary>
/// One record set a mapping resolves against by searching the platform, rather than out of the partition's cache.
/// A cache is for a closed vocabulary a capture can hold whole; this is for the records a delivery refers to, which
/// are business data and grow without bound.
/// </summary>
internal sealed class MappingSearchYaml
{
    /// <summary>The OSDU kind searched, as a flow writes a kind (<c>osdu:wks:master-data--Wellbore:*</c>).</summary>
    public string? Kind { get; set; }

    /// <summary>The template whose schema says how the kind's properties are indexed, named like the mapping's own.</summary>
    public MappingTemplateYaml? Schema { get; set; }

    /// <summary>What this set is, shown wherever the mapping is listed.</summary>
    public string? Description { get; set; }
}

internal sealed class MappingTemplateYaml
{
    public string? Kind { get; set; }

    public string? Version { get; set; }
}

internal sealed class MappingParameterYaml
{
    public bool Required { get; set; }

    public string? Default { get; set; }

    public string? Description { get; set; }
}

internal sealed class MappingDatasetYaml
{
    public string? System { get; set; }

    public List<string>? Key { get; set; }

    public string? Label { get; set; }

    public List<string>? Identity { get; set; }

    /// <summary>What the OSDU id's unique segment is made from: <c>deliveryKey</c> (the default) or <c>key</c>.</summary>
    public string? IdFrom { get; set; }
}

internal sealed class RetrievalYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

    public Dictionary<string, FlowParameterYaml>? Parameters { get; set; }

    public RetrievalSourceYaml? Source { get; set; }

    public RetrievalTargetYaml? Target { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public RunParallelismYaml? Parallelism { get; set; }
}

internal sealed class CacheYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

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

    /// <summary>The dictionary document the type holds, in place of a kind: a lookup table kept in the repository.</summary>
    public string? Dictionary { get; set; }

    /// <summary>The ingestion table the type reads, in place of a kind: a lookup table SQLFlow's own flows load.</summary>
    public string? Table { get; set; }

    /// <summary>For a table type: the column each row is keyed by.</summary>
    public string? Key { get; set; }

    /// <summary>The dimension the type holds the members of, in place of a kind: a lookup table a dimension flow builds.</summary>
    public string? Dimension { get; set; }

    /// <summary>For a dimension type: the dimension flow that declares the dimension.</summary>
    public string? DimensionFlow { get; set; }

    /// <summary>The partitions of the flow this type is built for; every partition of the flow when left out.</summary>
    public List<string>? Partitions { get; set; }
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

internal sealed class AssertionYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

    public Dictionary<string, FlowParameterYaml>? Parameters { get; set; }

    public AssertionSourceYaml? Source { get; set; }

    /// <summary>The partitions the flow tests, each by its data-partition-id.</summary>
    public List<string>? Partitions { get; set; }

    public AssertionDefaultsYaml? Defaults { get; set; }

    public string? FailRunOn { get; set; }

    public List<AssertionTestYaml>? Tests { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public RunParallelismYaml? Parallelism { get; set; }
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

    public object? RowCount { get; set; }

    public AssertionColumnsYaml? Columns { get; set; }

    public string? Monotonic { get; set; }

    public string? For { get; set; }

    public string? Values { get; set; }

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
}

/// <summary>One row of known values: the full paths and values it is located by and checked in, and why it is disabled, when it is.</summary>
internal sealed class AssertionKnownRowYaml
{
    public Dictionary<string, object?>? LocateBy { get; set; }

    public Dictionary<string, object?>? Check { get; set; }

    public string? Disabled { get; set; }
}

internal sealed class AssertionColumnsYaml
{
    public List<string>? Includes { get; set; }

    public List<string>? Excludes { get; set; }

    [YamlMember(Alias = "equals")]
    public List<string>? Exactly { get; set; }
}

internal sealed class InventoryFlowYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

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

internal sealed class RemovalFlowYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

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

internal sealed class DimensionFlowYaml
{
    public string? FlowType { get; set; }

    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Batch { get; set; }

    public object? Schedule { get; set; }

    public object? Mode { get; set; }

    public object? Lifecycle { get; set; }

    public Dictionary<string, FlowParameterYaml>? Parameters { get; set; }

    public DimensionSourceYaml? Source { get; set; }

    /// <summary>The partitions the flow builds dimensions in, each by its data-partition-id.</summary>
    public List<string>? Partitions { get; set; }

    public List<DimensionYaml>? Dimensions { get; set; }

    /// <summary>The module's database as the pipelines reading the flow's tables and views name it.</summary>
    public DimensionTargetYaml? Target { get; set; }

    /// <summary>The views over the flow's dimension tables, each joining them at the grain of one.</summary>
    public List<DimensionViewYaml>? Views { get; set; }

    public FlowReliabilityYaml? Reliability { get; set; }

    public RunParallelismYaml? Parallelism { get; set; }

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
}

internal sealed class DimensionTargetYaml
{
    /// <summary>A ${env:...} or ${keyvault:...} reference, or a connection string whose password is one.</summary>
    public string? Connection { get; set; }
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
}

internal sealed class DimensionViewJoinYaml
{
    /// <summary>The column joined on: a column of the from dimension, bare, or alias.column of an earlier join.</summary>
    public string? On { get; set; }

    /// <summary>The dimension joined to.</summary>
    public string? To { get; set; }

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
}

internal sealed class DimensionElementsYaml
{
    /// <summary>The nested array whose objects are rows, as a path from the record's root (data.Curves).</summary>
    public string? Path { get; set; }

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