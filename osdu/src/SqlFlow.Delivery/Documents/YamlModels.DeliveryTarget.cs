// The YAML shapes of a delivery flow's target block (osdu/docs/reference/flow/delivery.md, target): the
// validation gate, the DDMSs, the External Data Services, the Reservoir DDMS (ETP), the Production DDMS core
// service (DSPDM) and the options the routes read.
// As in YamlModels.cs, every property must be listed: unknown keys are rejected.
#pragma warning disable CA1812, CA2227, CA1002, CA1034, CA1720

namespace SqlFlow.Delivery.Documents;

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

    public int? UploadBlockMiB { get; set; }

    public int? UploadConcurrency { get; set; }

    public bool? UploadServerCopy { get; set; }

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
