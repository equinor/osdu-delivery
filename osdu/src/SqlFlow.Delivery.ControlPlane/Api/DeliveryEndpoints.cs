using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A delivery flow's record counts by state, drift, throughput, and its last submission: the flow's dashboard card. For one
/// interface it carries the interface and its ledger identity; for a source as a whole it adds its interfaces up, carries
/// no single ledger identity (<see cref="Guid.Empty"/>) and no interface. A flow that names its partitions keeps a ledger
/// per partition: counted in one partition, <c>Partition</c> names it; counted as a whole, every partition's ledgers are
/// added up the way a source's interfaces are, and <c>Partitions</c> lists the partitions the counts cover. Both are null
/// for a flow that names none. A flow whose partition is its data-partition-id header is counted in the partition its
/// ledger is kept under, which <c>HeaderPartition</c> names as the ledger's directory holds it (null until it has run), so
/// the workbench's partition filters it as it filters the rest; a request never names it, since such a flow takes none.
/// <c>Blocked</c> counts the records blocked until their row moves or someone releases them, and <c>Stranded</c> the held or
/// failed ones nothing will move (what a release answered by a plan that left the record parked used to leave): together,
/// what a release of every blocked record reaches.
/// </summary>
public sealed record DeliveryFlowStatsDto(
    Guid PipelineId, string FlowName, Guid FlowId, long Total, long Pending, long Delivering, long Delivered, long Held, long Failed,
    long Deleted, long Drifted, long DeliveredLast24h, DateTime? LastDeliveredUtc, DateTime? LastVerifiedUtc, long Submissions,
    DeliverySubmissionDto? LastSubmission, string? Interface = null, int Interfaces = 1, long Waiting = 0, string? Partition = null,
    IReadOnlyList<string>? Partitions = null, string? HeaderPartition = null, IReadOnlyList<string>? RedeliverParts = null, long Reverted = 0,
    long Blocked = 0, long Stranded = 0, long Adoptable = 0);

/// <summary>
/// One interface of a delivery flow (osdu/docs/reference/flow/interfaces.md): its ledger identity, how it is delivered
/// and why, the mapping and kind it delivers, the record table it reads, what it waits for, and its record counts. A
/// flow in the single form lists one, with no name.
/// <para><c>After</c> is what the document declares; <c>Wave</c>, <c>WaitsFor</c> and <c>NotWaitedFor</c> are the order a
/// run takes, from <c>after:</c> and the relationships the mappings fill. When that order cannot be worked out (a mapping
/// or template the catalog does not hold, interfaces that wait for each other), <c>OrderProblem</c> says why and the
/// order shown is the one <c>after:</c> alone gives.</para>
/// <para><c>Partition</c> is the partition the interface is described in, for a flow that names its partitions: each
/// partition keeps a ledger of its own (osdu/docs/reference/concepts/partitions.md, A ledger per partition). Null for a
/// flow that names none.</para>
/// </summary>
public sealed record DeliveryInterfaceDto(
    string? Interface, Guid FlowId, string Ledger, string Route, string? RouteReason, string Mapping, string? Kind, string RecordObject,
    IReadOnlyList<string> After, DeliveryFlowStatsDto Stats,
    int Wave = 1, IReadOnlyList<DeliveryInterfaceWaitDto>? WaitsFor = null, IReadOnlyList<DeliveryInterfaceWaitDto>? NotWaitedFor = null,
    string? OrderProblem = null, IReadOnlyList<DeliveryParameterDto>? Parameters = null, IReadOnlyList<string>? KeyColumns = null,
    string? Partition = null);

/// <summary>
/// A parameter the flow declares, whose value fills its record scope: its name, whether a value is required, its default,
/// and the record table's column <c>source.record.scope</c> binds it to (null for a parameter the scope does not read, such
/// as one naming the work location), whose values a page can offer for it.
/// </summary>
public sealed record DeliveryParameterDto(string Name, bool Required, string? Default, string? Description, string? ScopeColumn = null);

/// <summary>
/// A preview of one record of a flow: the record's key, or none for the scope's first record, and the flow parameter values
/// the scope is read with (the declared defaults fill what is not given).
/// </summary>
public sealed record DeliveryPreviewRequest(string? Key, [property: System.Text.Json.Serialization.JsonConverter(typeof(SqlFlow.Core.Runs.RunValuesJsonConverter))] IReadOnlyDictionary<string, string>? Values);

/// <summary>
/// A read of one OSDU record through a flow's route and credentials, by its id (a version or a trailing colon is dropped),
/// at its latest version or at the one <c>Version</c> names. A record's own read names no id: the ledger's record says which.
/// </summary>
public sealed record DeliveryReadRequest(string? TargetId, long? Version = null);

/// <summary>One interface another waits for, or does not wait for, with where that comes from (<c>after</c> or <c>schema</c>) and why.</summary>
public sealed record DeliveryInterfaceWaitDto(string Interface, string Origin, string Why);

/// <summary>
/// One plan of a flow over its ingestion tables as the ledger received it, and what became of it: which selection it
/// read (<c>Kind</c>, the window and what else bounded it), where the records came from (<c>SourceConnection</c> as the
/// flow declares it, <c>SourceObject</c>), the run that carried it, and the partition whose ledger holds it.
/// </summary>
public sealed record DeliverySubmissionDto(
    Guid SubmissionId, Guid FlowId, string FlowName, string MappingReference, string RenderContext,
    string ParametersJson, long RecordCount, string Status, DateTime ReceivedUtc, DateTime? StartedUtc, DateTime? CompletedUtc,
    long Planned, long SkippedUnchanged, long AwaitingApproval, long SkippedStale, long UnchangedAtPush, long Blocked, long Delivered, long Held, long Failed, string? Error,
    string? WorkLocation, int BatchCount, int Slices,
    string Kind = SubmissionKinds.Incremental, long Untracked = 0,
    string SourceConnection = "", string SourceObject = "", DateTime? WindowFromUtc = null, DateTime? WindowToUtc = null,
    JsonElement? SourceWindow = null, Guid? RunId = null, long Waiting = 0, string? Partition = null);

/// <summary>One retrieval run of a retrieval flow: the window it covered, where its files went, and its outcome.</summary>
public sealed record DeliveryRetrievalDto(
    long RetrievalId, Guid FlowId, string FlowName, Guid? RunId, string Actor, string Kinds, string? Query, string? WindowField,
    DateTime? WindowFrom, DateTime? WindowTo, string Location, string? ManifestLocation, string Status, long Records, int Files, long Bytes,
    DateTime StartedUtc, DateTime? CompletedUtc, string? Error);

/// <summary>One work batch of a submission: a file of rendered documents and how far its drain got.</summary>
public sealed record DeliveryWorkBatchDto(
    Guid SubmissionId, int Index, string Location, int RecordCount, string Status, string? LeaseOwner, DateTime? LeaseExpiresUtc, Guid? RunId,
    DateTime CreatedUtc, DateTime? StartedUtc, DateTime? CompletedUtc, long Delivered, long Held, long Failed, long Retrying, string? Error,
    long Waiting = 0);

/// <summary>
/// The current state of one deliverable: what OSDU holds for it, what is pending, and why it is where it is. A record of a
/// linked template that reads its parent names it (<c>ParentId</c>) and the version of it its delivered and its queued
/// document were rendered from (osdu/docs/reference/flow/mapping-parent.md).
/// </summary>
public sealed record DeliveryRecordDto(
    Guid DeliveryKey, Guid FlowId, string SourceKey, string? Label, string MappingName, string? RenderContext,
    string? SourceFingerprint, DateTime? SourceModifiedUtc, string? MetadataHash, string? PayloadHash, DateTime? PayloadModifiedUtc, string? TargetId, long? TargetVersion, string Status,
    DateTime? LastDeliveredUtc, DateTime? LastVerifiedUtc, string? LastVerifyOutcome, string? LeaseOwner, DateTime? LeaseExpiresUtc,
    Guid? LastSubmissionId, int AttemptCount, DateTime? NextAttemptUtc, string? LastError, bool HasPendingDocument,
    bool PendingMetadata, bool PendingPayload, string? PendingPayloadLocation, bool Blocked, DateTime CreatedUtc, DateTime UpdatedUtc,
    string? PendingDocumentRef, int? WorkBatch, JsonElement? TargetState, JsonElement? PendingSteps,
    string? SourceFileName, long? SourceRowNumber, DateTime? SourceUpdatedUtc,
    string? PendingSourceFileName, long? PendingSourceRowNumber, DateTime? PendingSourceUpdatedUtc,
    string? SourceKeyJson, DateTime? PlanRequestedUtc,
    string? WaitingFor = null, IReadOnlyList<DeliveryRecordReferenceDto>? References = null, DateTime? SourceInsertedUtc = null,
    string? Partition = null, string? Issue = null, string? ParentId = null, long? ParentVersion = null, long? PendingParentVersion = null,
    string? OccupiedTargetId = null, long? OccupiedTargetVersion = null, bool Adoptable = false, DateTime? AdoptRequestedUtc = null);

/// <summary>An OSDU id a record's pending document refers to, and the property of the record holding it.</summary>
public sealed record DeliveryRecordReferenceDto(string Id, string Property);

/// <summary>A record of the ledger another record waits for, or that waits for it: where it is and how it stands.</summary>
public sealed record DeliveryRecordLinkDto(
    Guid FlowId, Guid DeliveryKey, Guid? PipelineId, string? FlowName, string? Interface, string SourceKey, string? Label, string? TargetId, string Status,
    string? Partition = null);

/// <summary>
/// A flow the record lookup can be narrowed to: the ledger identity its records carry (<c>FlowId</c>, what the lookup's
/// <c>flowId</c> takes), the pipeline and interface (null for the single form) it is named by, and the partition its
/// ledger is kept under. Only an identity that holds records is offered.
/// </summary>
public sealed record DeliveryRecordFlowDto(Guid FlowId, Guid PipelineId, string FlowName, string? Interface, string? Partition = null);

/// <summary>
/// A flow the audit trail can be narrowed to: the ledger identity its activities carry (<c>FlowId</c>, what the trail's
/// <c>flowId</c> takes), the flow and interface (null for the single form) the ledger's directory names it by, the kind of
/// ledger it is (a delivery flow, a dimension), and the partition it is kept under. Only an identity with activity is offered.
/// </summary>
public sealed record DeliveryActivityFlowDto(Guid FlowId, string FlowName, string Kind, string? Interface, string? Partition);

/// <summary>
/// A record with the pipeline (and, for a source, the interface) it belongs to. The pending document itself lives in the
/// submission's work batches on storage, which the nodes read; its reference and batch are on the record. A waiting record
/// names the record it waits for (<c>WaitsOn</c>), and every record lists the records waiting for it (<c>WaitedOnBy</c>,
/// the first <see cref="DeliveryEndpoints.MaxWaitersShown"/>). A record reading its parent names the record of the ledger
/// holding the parent's id (<c>Parent</c>, null when none of the partition's flows delivers it), and every record lists the
/// records reading it as their parent (<c>ReadBy</c>, the first <see cref="DeliveryEndpoints.MaxWaitersShown"/>): the same
/// minted id in the other namespace, one click away. <c>KeyColumns</c> names the parts of the record's key
/// tuple (<c>SourceKeyJson</c>): its interface's <c>source.record.key</c> as the catalog's copy of the flow declares it
/// now, null when the catalog holds no readable copy of that flow or interface. <c>Partition</c> is the partition a
/// request about the record's flow names, for a flow that names or follows its partitions; null for one whose partition is
/// its header's, which takes none. The partition the record's ledger is kept under, either way, is the record's own.
/// </summary>
public sealed record DeliveryRecordDetailDto(
    DeliveryRecordDto Record, Guid? PipelineId, Guid? RepoId, string? FlowName, string? Interface = null,
    DeliveryRecordLinkDto? WaitsOn = null, IReadOnlyList<DeliveryRecordLinkDto>? WaitedOnBy = null,
    IReadOnlyList<string>? KeyColumns = null, string? Partition = null,
    DeliveryRecordLinkDto? Parent = null, IReadOnlyList<DeliveryRecordLinkDto>? ReadBy = null, DeliveryRemovedKeyDto? RemovedKey = null);

/// <summary>One delivery try, as the append-only history holds it: its outcome, and every step with what the target returned.</summary>
public sealed record DeliveryAttemptDto(
    long AttemptId, Guid DeliveryKey, Guid? SubmissionId, Guid? RunId, string Worker, DateTime StartedUtc, DateTime CompletedUtc,
    string Outcome, string Phase, string? MetadataHash, string? PayloadHash, long? TargetVersion, string? Error, JsonElement? Result, int? WorkBatch,
    string? SourceFileName, long? SourceRowNumber, DateTime? SourceUpdatedUtc, DateTime? SourceDeletedUtc = null, long? ParentVersion = null);

/// <summary>
/// One entry of the audit trail: who did what, when, with which inputs, and how it ended, and the partition whose ledger it
/// was done to. <c>Idle</c> marks a run that completed having changed nothing. One entry read on its own also names the
/// pipeline of its ledger, the interface of it, and the partition a request about the flow names
/// (<c>NamedPartition</c>: null for a flow whose partition is its header's), so what it did can be acted on from it (a run
/// reversed); a listing leaves them out.
/// </summary>
public sealed record DeliveryActivityDto(
    long ActivityId, Guid FlowId, string FlowName, string Kind, string Actor, DateTime StartedUtc, DateTime? CompletedUtc,
    string Outcome, string? ParametersJson, Guid? SubmissionId, Guid? DeliveryKey, Guid? RunId, string? Summary, string? Log,
    string? Partition = null, bool Idle = false, Guid? PipelineId = null, string? Interface = null, string? NamedPartition = null);

/// <summary>A submission with the pipeline, interface and partition that planned it, and the runs that carried it.</summary>
public sealed record DeliverySubmissionDetailDto(
    DeliverySubmissionDto Submission, Guid? PipelineId, IReadOnlyList<Guid> RunIds, string? Interface = null, string? Partition = null);

/// <summary>A mapping document as the sync found it in a repository.</summary>
/// <remarks>
/// <c>TemplateVersion</c> is the template version the mapping fills, and <c>TemplatePath</c> the template folder file the
/// last sync settled it from (osdu/docs/reference/concepts/templates.md, Template folders); empty when no folder on the
/// mapping's path holds it.
/// </remarks>
public sealed record DeliveryMappingDto(
    Guid Id, Guid RepoId, string Reference, string Name, string Version, string Kind, string RelativePath, string ContentHash,
    string Status, string? Message, JsonElement Summary, DateTime FirstSeenUtc, DateTime LastSeenUtc, string TemplateVersion, string TemplatePath);

/// <summary>A mapping document with its text.</summary>
public sealed record DeliveryMappingDetailDto(DeliveryMappingDto Mapping, string Yaml);

/// <summary>One cache flow's declaration of a type: the kind it searches, its query, and what a change does in its declaration.</summary>
/// <summary>
/// One cache flow's declaration of a type: where it takes the records from (<c>Origin</c>: osdu, table or dictionary), for an
/// OSDU type the kind and query it searches, for a table type the connection reference and table it reads and the key column,
/// and for a dictionary type the document's file and the name of its key.
/// </summary>
public sealed record DeliveryCacheTypeSourceDto(
    string Flow, string Origin, string? Kind, string? Query, string OnChange, string? Connection = null, string? SourceObject = null, string? KeyField = null,
    string? DictionaryPath = null);

/// <summary>One path the cache keeps for a type: the path, the name it is cached under, and the cache flows that declare it.</summary>
public sealed record DeliveryCacheFieldDto(string Path, string As, IReadOnlyList<string> Flows);

/// <summary>
/// A type of a partition's cache: the union of what its cache flows declare (each flow's kind and query, and every path any of
/// them keeps), what a change does (it waits for approval when any flow asks for that), and how many records the current
/// version holds.
/// </summary>
public sealed record DeliveryCacheTypeDto(
    string Name, string EntityType, IReadOnlyList<DeliveryCacheTypeSourceDto> Sources, IReadOnlyList<DeliveryCacheFieldDto> Fields, string OnChange, long Items,
    string Origin = CacheOrigins.OsduText, string? Key = null);

/// <summary>A schedule that refreshes a cache flow: its cadence, or that it fires behind other schedules.</summary>
public sealed record DeliveryCacheScheduleDto(Guid Id, string Name, string? Cron, int? IntervalSeconds, bool Chained);

/// <summary>
/// A cache flow that fills a partition's cache: the repository and file that define it (the file is where what it caches is
/// changed), its pipeline, the OSDU endpoint reference its OSDU types are searched on and the connection reference its table
/// types are read from (each null when it declares none of those), the schedules that refresh it, the types it declares for
/// the partition, the partitions it names (<c>partitions</c>): every partition it builds a cache for, which a refresh of it
/// names, or empty for a flow whose partition is its header's, which a refresh names none for; and the retention it declares
/// (<c>retentionDays</c>).
/// </summary>
public sealed record DeliveryCacheFlowDto(
    string Name, Guid RepoId, string RepoName, string RelativePath, Guid? PipelineId, string? Endpoint, IReadOnlyList<DeliveryCacheScheduleDto> Schedules,
    IReadOnlyList<string> Types, string? Connection = null, IReadOnlyList<string>? Partitions = null, int RetentionDays = CacheRetention.DefaultDays);

/// <summary>
/// The cache of one OSDU partition: every cache flow that fills it, the types it holds as those flows together declare them,
/// its current version with the flow and run that wrote it, how many versions it has, and its retention: how many days the
/// records of a replaced version are kept, the longest any of its cache flows declares. Every delivery flow that delivers to
/// the partition reads it.
/// </summary>
public sealed record DeliveryCacheDto(
    string Scope, IReadOnlyList<DeliveryCacheFlowDto> Flows, IReadOnlyList<DeliveryCacheTypeDto> Types, DeliveryCacheVersionDto? Current, int Versions,
    int RetentionDays = CacheRetention.DefaultDays);

/// <summary>
/// One cache change and what happens about it: the partition, the cached record and path that moved, the value before and
/// after, how many delivered manifest rows it reaches, how many are marked for redelivery (<c>Processed</c>), and how many,
/// of which flows, are still built from the old value (<c>Waiting</c>, <c>WaitingFlows</c>): the flows yet to run.
/// </summary>
public sealed record DeliveryUpdateTagDto(
    long TagId, string Kind, string Scope, string TypeName, string ItemId, string Path, string Change, string? OldValue, string? NewValue,
    string? FromVersion, string ToVersion, string Mode, string Status, string Summary, long AffectedRecords, long Processed,
    long Remaining, long Waiting, IReadOnlyList<DeliveryUpdateTagFlowDto> WaitingFlows, DateTime DetectedUtc, DateTime? DecidedUtc,
    string? DecidedBy, DateTime? StartedUtc, DateTime? CompletedUtc);

/// <summary>The records of one flow a cache change still waits for; the pipeline is null for a ledger no synced flow holds.</summary>
public sealed record DeliveryUpdateTagFlowDto(Guid FlowId, Guid? PipelineId, string? FlowName, long Records);

/// <summary>A decision on a set of tags: approve lets the next run carry the update, reject leaves OSDU as it is.</summary>
public sealed record DeliveryTagDecisionRequest(IReadOnlyList<long> TagIds, bool Approve);

public sealed record DeliveryTagDecisionResult(int Decided, bool Approved);

/// <summary>One cached value a record was built from, for its history page: the partition, the cached record and path, and what it held.</summary>
public sealed record DeliveryCacheUseDto(string Scope, string TypeName, string ItemId, string Path, string Kind, string Value);

/// <summary>
/// One thing delivered records of a partition were built without, and how many were: a value no cached record answered to
/// (<c>unlisted</c>), a key no row was listed under (<c>listed</c>), an id written without its record (<c>unverified</c>),
/// or a path that held nothing (<c>empty</c>). The refresh that brings it tags and redelivers them.
/// </summary>
public sealed record DeliveryCacheGapDto(string TypeName, string Path, string Kind, string Key, string Value, long Records);

/// <summary>One cached record: its OSDU id and the values captured at the declared paths, as one version of its partition's cache holds it.</summary>
public sealed record DeliveryCachedItemDto(
    long ItemId, string Scope, string Version, string TypeName, string EntityType, string RecordId, JsonElement Fields);

/// <summary>
/// One type a cache version holds, how many records of it, and for a lookup table the name its key is kept under; with the
/// type's own content hash, how it compares with the version before (<c>added</c>, <c>changed</c> or <c>unchanged</c>), and
/// the version its content dates from. The version moves when anything in the partition's cache does; these move only when
/// the type does. The three are null for a version written before types were hashed, and <c>since</c> when it cannot be told.
/// </summary>
public sealed record DeliveryCacheVersionTypeDto(
    string Name, string EntityType, long Items, string? Key = null, string? Hash = null, string? Change = null, string? Since = null);

/// <summary>
/// One of the partition's system properties as a cache version holds it: a setting of the platform for the partition,
/// reported by one of its services (<c>indexer</c> or <c>search</c>), which is neither reference nor master data.
/// <c>State</c> is <c>Enabled</c>, <c>Disabled</c> or <c>Unknown</c>; <c>Detail</c> says why when it is unknown.
/// </summary>
public sealed record DeliveryCacheSystemPropertyDto(string Service, string Name, string State, string? Source, string? Detail);

/// <summary>
/// One version of a partition's cache: when it was captured, whether it is the version deliveries render against, the version
/// that was current before it, the cache flow and the run that wrote it and who asked (null run for an import from files),
/// where the content came from, what it holds, the partition's system properties the capture found, and when the cache's
/// retention pruned its records and who did (null while they are kept; a pruned version can no longer be read or compared).
/// </summary>
public sealed record DeliveryCacheVersionDto(
    string Scope, string Version, int Sequence, DateTime CapturedUtc, bool Current, string? PreviousVersion, string Flow, Guid? RunId, string CapturedBy,
    string Origin, long Items, IReadOnlyList<DeliveryCacheVersionTypeDto> Types, IReadOnlyList<DeliveryCacheSystemPropertyDto> SystemProperties,
    DateTime? PrunedUtc = null, string? PrunedBy = null);

/// <summary>
/// What changed in a partition's cache between two versions: counts per type and a page of the records that differ. The
/// counts follow the type and search filters but not the change filter.
/// </summary>
public sealed record DeliveryCacheDiffDto(
    string Scope, string FromVersion, string ToVersion, long Changed, long Added, long Removed, IReadOnlyList<DeliveryCacheDiffTypeDto> Types,
    PagedResult<DeliveryCacheDiffItemDto> Items);

/// <summary>How many records of one cached type changed, arrived and left between the two versions.</summary>
public sealed record DeliveryCacheDiffTypeDto(string TypeName, long Changed, long Added, long Removed);

/// <summary>
/// One cached record that differs between the two versions: changed, added or removed, the captured values on each side
/// (null on the side that does not hold it), and the captured names whose value moved.
/// </summary>
public sealed record DeliveryCacheDiffItemDto(
    string TypeName, string EntityType, string RecordId, string Change, JsonElement? Before, JsonElement? After, IReadOnlyList<string> ChangedFields);

/// <summary>
/// One version in a cache's history: the version captured before it, how many records it changed, added and removed against
/// that one, and which types it moved. A type that only rode along with another's change is not listed. The counts stay when
/// the cache's retention prunes either version's records; <c>BeforePruned</c> says the version before it is pruned, so the
/// records that differ can no longer be listed.
/// </summary>
public sealed record DeliveryCacheHistoryEntryDto(
    DeliveryCacheVersionDto Version, string? Before, long Changed, long Added, long Removed, IReadOnlyList<DeliveryCacheHistoryTypeDto> Types,
    bool BeforePruned = false);

/// <summary>
/// One type a version moved: <c>added</c>, <c>changed</c> or <c>removed</c>, with how many of its records changed, arrived and
/// left. Such a version is a version of the type: <c>hash</c> is the type's content hash in it (null when the version removed
/// the type, or was written before types were hashed) and <c>items</c> how many records of the type it holds.
/// </summary>
public sealed record DeliveryCacheHistoryTypeDto(string Name, string Change, long Changed, long Added, long Removed, string? Hash, long Items);

/// <summary>A run was queued for a record-scoped operation (redeliver, verify).</summary>
public sealed record DeliveryRunAccepted(Guid RunId, string Status);

/// <summary>
/// A compute task was queued for a node: an operation that runs long enough to be watched rather than waited on (a removal,
/// a value check). A probe, a read-back, a preview and a read of the ingestion tables run in process instead.
/// </summary>
public sealed record ComputeTaskAccepted(Guid TaskId, string Status);

/// <summary>
/// A release of a flow's blocked records: those <c>Keys</c> names, or every one. <c>Run</c> also queues a deliver run of the
/// flow under the parameter values its last submission ran with, which sends what the release queued and plans the rest.
/// </summary>
public sealed record DeliveryReleaseRequest(IReadOnlyList<Guid>? Keys, bool Run = false, string? Pool = null);

/// <summary>How many records a release released, and the run it queued when asked to.</summary>
public sealed record DeliveryReleaseResult(int Released, Guid? RunId = null);

/// <summary>
/// A lift of the removals of a flow's records (osdu/docs/reference/flow/removal.md, Delivering a record again): the records
/// <c>Keys</c> names, at most 1000. <c>Run</c> also queues a deliver run of the flow under the parameter values its last
/// submission ran with, which delivers them again at once.
/// </summary>
public sealed record DeliveryLiftRequest(IReadOnlyList<Guid>? Keys, bool Run = false, string? Pool = null);

/// <summary>How many records' removals a lift lifted (their keys no longer kept out), and the deliver run it queued when asked to.</summary>
public sealed record DeliveryLiftResult(int Lifted, Guid? RunId = null);

/// <summary>
/// A release of one record. <c>Run</c> also queues a deliver run scoped to it, read under the parameter values it was last
/// planned with, so the record is tried again at once: how an operator checks, on one record, that an issue's cause is
/// fixed before releasing every record the issue keeps blocked.
/// </summary>
public sealed record DeliveryRecordReleaseRequest(bool Run = false, string? Pool = null);

/// <summary>
/// An adoption asked of one record held because OSDU already holds a record at the id its key makes. <c>Run</c> also queues
/// a deliver run scoped to it, as a record's release does, which takes the record over at once.
/// </summary>
public sealed record DeliveryRecordAdoptRequest(bool Run = false, string? Pool = null);

/// <summary>
/// An adoption asked of a flow's records held because OSDU already holds a record at the id each one's key makes: those
/// <c>Keys</c> names, every one <c>Filter</c> matches (resolved when it is asked for, and refused when it no longer matches
/// the <c>Expected</c> count the operator was shown), or, with neither, every such record of the flow, of every interface
/// when the request names none. <c>Confirm</c> is the partition the ledgers are kept in, typed back. A record held for
/// another reason is left. <c>Run</c> also queues a deliver run of the flow, which takes the records over.
/// </summary>
public sealed record DeliveryFlowAdoptRequest(
    IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected, string? Confirm, bool Run = false, string? Pool = null);

/// <summary>
/// What an adoption asked: how many records it reached (named, matched, or held for an id OSDU holds), how many it asked
/// to adopt, and the deliver run it queued, which takes them over, when asked to.
/// </summary>
public sealed record DeliveryAdoptResult(int Selected, int Asked, Guid? RunId);

/// <summary>
/// Redeliver: <c>scope</c> is the part to send again, <c>all</c> by default, <c>record</c>, or <c>files</c> or <c>bulk</c>
/// as the record's route sends them (<c>metadata</c> and <c>payload</c> name the same parts); <c>run</c> queues the deliver
/// run that sends it.
/// </summary>
public sealed record DeliveryRedeliverRequest(string? Scope = null, bool Run = true, string? Pool = null);

public sealed record DeliveryRedeliverResult(int Marked, Guid? RunId);

/// <summary>
/// Where a flow's records actually live: the endpoint and data partition every removal in the GUI names before it
/// runs, with the exact call each scope makes. The endpoint is reported as the flow declares it, secret references
/// and all, because that reference is what identifies the environment; no credential or header value is exposed.
/// <c>Ddms</c> says, for a flow on the ddms route, which collection of which DDMS its records go to, and
/// <c>RecordMethod</c> which method the record scope calls <c>RecordPath</c> with. <c>PreviousPath</c> is the calls the
/// previous scope makes (the version before the latest read, then written back as a new version), and
/// <c>PreviousRefusal</c> why the route cannot write an earlier version back, null when it can.
/// </summary>
public sealed record DeliveryTargetDto(
    Guid PipelineId, string FlowName, string Endpoint, string? DataPartition, string Protocol, string AuthType,
    string RecordPath, string HistoryPath, string EverythingPath, string? Interface = null, string? Ddms = null, string RecordMethod = "POST",
    string PreviousPath = "", string? PreviousRefusal = null);

/// <summary>
/// The listing a removal is aimed at, the same filter the records list is built from. <c>SubmissionId</c> names the
/// records a submission last planned; <c>DeliveredBy</c> names the records a submission delivered, which stay its
/// however many submissions touch them afterwards: the set "the batch we ran" means.
/// </summary>
/// <summary>
/// A record listing's filter, as a listing, a removal and a sync name it. <c>Issue</c> keeps the blocked records one
/// issue keeps blocked: the sixteen characters the flow's issues listing gives it.
/// </summary>
public sealed record DeliveryRecordFilterDto(
    string? Status, string? Search, string? Mode, Guid? SubmissionId, Guid? RunId, bool Drifted = false, Guid? DeliveredBy = null, string? Issue = null);

/// <summary>
/// A removal of one or many records. <c>scope</c> is record, previous, history or everything. The records are named either
/// by <c>keys</c> or by <c>filter</c> (every record the listing matches), never both. <c>expected</c> is the count
/// the operator was shown: when it no longer matches what the filter resolves to, the removal is refused rather
/// than run against a set that changed underneath them. <c>purgeLedger</c>, with record or everything, also deletes each
/// record OSDU answered for from the ledger, keeping one line of it. <c>pool</c> names the pool the remove run is queued on.
/// </summary>
public sealed record DeliveryRemovalRequest(
    string? Scope, IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected, bool PurgeLedger = false, string? Pool = null);

/// <summary>
/// Which of a flow's records a sync reads: those <c>Keys</c> names, every one <c>Filter</c> matches (resolved to keys when the
/// sync is queued, and refused when it no longer matches the <c>Expected</c> count the operator was shown), or, with neither,
/// every record of the flow.
/// </summary>
public sealed record DeliverySyncRequest(IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected);

/// <summary>
/// A redelivery of many of a flow's records: those <c>Keys</c> names, every one <c>Filter</c> matches (resolved when it is
/// asked for, and refused when it no longer matches the <c>Expected</c> count the operator was shown), or, with neither,
/// every record the flow has delivered. <c>Scope</c> is the part to send again, as the flow's route sends it (all when it
/// names none). The records are marked under the caller's name, and with <c>Run</c> a deliver run is queued that plans
/// and sends them; without it the flow's next run does.
/// </summary>
public sealed record DeliveryFlowRedeliverRequest(
    string? Scope, IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected, bool Run = true, string? Pool = null);

/// <summary>
/// A request to bring many of a flow's records up to date: rendered again under the rules of now and sent only where they
/// render differently. The records are named as a redelivery of many names them (<see cref="DeliveryFlowRedeliverRequest"/>):
/// by <c>Keys</c>, by <c>Filter</c> with the <c>Expected</c> count, or with neither every record the flow has delivered.
/// </summary>
public sealed record DeliveryRerenderRequest(IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected, bool Run = true, string? Pool = null);

/// <summary>How many records were asked to be brought up to date, and the deliver run that brings them, when one was queued.</summary>
public sealed record DeliveryRerenderResult(int Marked, Guid? RunId);

/// <summary>
/// The plan run that says what bringing a selection up to date would send: the run to watch, how many delivered records of
/// the selection it checks (at most <see cref="DeliveryRerender.PreviewRecords"/>), and how many the selection holds, with
/// whether that count stopped at the listing's bound.
/// </summary>
public sealed record DeliveryRerenderPreviewAccepted(Guid RunId, int Checked, long Selected, bool SelectedCapped);

/// <summary>The bounds of bringing records up to date from the API.</summary>
public static class DeliveryRerender
{
    /// <summary>The most records a preview checks: a plan run is scoped to at most this many records.</summary>
    public const int PreviewRecords = DeliveryRunPayload.MaxRecordKeys;
}

/// <summary>
/// A removal was queued as a remove run of the flow (osdu/docs/reference/concepts/removal-and-reversal.md, Removing records
/// from OSDU): the run to watch, how many records it will act on, and whether it deletes them from the ledger too.
/// </summary>
public sealed record DeliveryRemovalAccepted(Guid RunId, string Status, string Scope, int Records, bool PurgeLedger = false);

/// <summary>
/// Records already removed from OSDU to delete from the ledger: those <c>Keys</c> names, every one <c>Filter</c> matches (refused
/// when it no longer matches the <c>Expected</c> count the operator was shown), or, with neither, every record the ledger marks
/// deleted.
/// </summary>
public sealed record DeliveryLedgerPurgeRequest(IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected);

/// <summary>What deleting from the ledger did: records asked about, deleted, and left as they were (not removed from OSDU, or busy).</summary>
public sealed record DeliveryLedgerPurgeResult(int Selected, int Purged, int Left, string Summary);

/// <summary>
/// What the ledger keeps of a record deleted from it after it was removed from OSDU. <c>PlanRequestedUtc</c> is set while the
/// flow's next run is still to read its row again and deliver it as a new record.
/// </summary>
public sealed record DeliveryPurgedRecordDto(
    Guid FlowId, Guid DeliveryKey, string SourceKey, string? Label, string? TargetId, long? LastVersion, int Attempts, long? ActivityId,
    string PurgedBy, DateTime PurgedUtc, DateTime? PlanRequestedUtc = null);

/// <summary>
/// What a removal would act on, for the confirmation the operator sees before asking for it. <c>Removed</c> counts the
/// records of the selection the ledger marks removed from OSDU already: those deleting from the ledger alone reaches.
/// </summary>
public sealed record DeliveryRemovalPreview(
    string Scope, int Records, int InOsdu, int NeverDelivered, bool Capped, DeliveryTargetDto Target, int Removed = 0);

/// <summary>
/// How a record is addressed in the API and the GUI: the ledger's flow id and the delivery key, together. A delivery key
/// alone names one record per flow that reads the row, and the flow id (not the pipeline) is what outlives a flow's
/// removal from its repository, so a record's history stays reachable.
/// </summary>
public static class DeliveryRecordRoutes
{
    /// <summary>The record's path segment: <c>{flowId}/{deliveryKey}</c>.</summary>
    public static string Path(Guid flowId, Guid deliveryKey) => $"{flowId:D}/{deliveryKey:D}";
}

/// <summary>
/// The delivery ledger's API: what each flow delivered (records, their history, their submissions), the audit trail
/// of runs and interventions, the mappings and snapshots the repositories hold, and the interventions themselves
/// (release, redeliver, verify, read back, delete). Reads are answered from the catalog's indexed ledger tables;
/// interventions either act on the ledger directly under the caller's name, or queue a run or a compute task for a node.
/// What a person waits on (a probe, a read-back of a record from OSDU, a preview, a read of the ingestion tables) runs in
/// this process through <see cref="DirectOperationRunner"/>, under the flow's own credentials resolved with the central
/// configuration, exactly as a node would run it.
/// </summary>
public static class DeliveryEndpoints
{
    private const int MaxAttempts = 500;

    /// <summary>Runs one submission's page lists: the run that registered it and everything that has worked on it since.</summary>
    private const int MaxSubmissionRuns = 100;

    /// <summary>Cached type declarations the cache listing reads at once, across every partition.</summary>
    private const int MaxCacheDefinitions = 5000;

    public static RouteGroupBuilder MapDeliveryReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");

        // The central configuration the control plane supplies to the runs it queues.
        DeliveryConfigEndpoints.Map(delivery);

        // The partitions the catalog knows, which every OSDU page is read in.
        DeliveryPartitionEndpoints.Map(delivery);

        // Where each cached type of a partition comes from, who reads it, and what its last refreshes did.
        DeliveryCacheStreams.Map(delivery);

        // How the explorer reaches each partition of OSDU.
        delivery.MapDeliveryExplorerReadEndpoints();
        delivery.MapGet("/flows/{pipelineId:guid}/stats", GetStatsAsync).WithName("GetDeliveryFlowStats");
        delivery.MapGet("/flows/{pipelineId:guid}/interfaces", ListInterfacesAsync).WithName("ListDeliveryInterfaces");
        delivery.MapGet("/flows/{pipelineId:guid}/records", ListRecordsAsync).WithName("ListDeliveryRecords");
        delivery.MapGet("/records", LookupRecordsAsync).WithName("LookupDeliveryRecords");
        delivery.MapGet("/records/flows", ListRecordFlowsAsync).WithName("ListDeliveryRecordFlows");
        delivery.MapGet("/flows/{pipelineId:guid}/target", GetTargetAsync).WithName("GetDeliveryTarget");
        delivery.MapGet("/flows/{pipelineId:guid}/submissions", ListSubmissionsAsync).WithName("ListDeliverySubmissions");
        delivery.MapGet("/flows/{pipelineId:guid}/retrievals", ListRetrievalsAsync).WithName("ListDeliveryRetrievals");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}", GetRecordAsync).WithName("GetDeliveryRecord");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/attempts", ListRecordAttemptsAsync).WithName("ListDeliveryRecordAttempts");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/chain", GetRecordChainAsync).WithName("GetDeliveryRecordChain");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/activities", ListRecordActivitiesAsync).WithName("ListDeliveryRecordActivities");
        delivery.MapGet("/submissions/{submissionId:guid}", GetSubmissionAsync).WithName("GetDeliverySubmission");
        delivery.MapGet("/submissions/{submissionId:guid}/attempts", ListSubmissionAttemptsAsync).WithName("ListDeliverySubmissionAttempts");
        delivery.MapGet("/submissions/{submissionId:guid}/batches", ListSubmissionBatchesAsync).WithName("ListDeliverySubmissionBatches");
        delivery.MapGet("/activities", ListActivitiesAsync).WithName("ListDeliveryActivities");
        delivery.MapGet("/activities/flows", ListActivityFlowsAsync).WithName("ListDeliveryActivityFlows");
        delivery.MapGet("/activities/{activityId:long}", GetActivityAsync).WithName("GetDeliveryActivity");
        delivery.MapGet("/mappings", ListMappingsAsync).WithName("ListDeliveryMappings");
        delivery.MapGet("/mappings/{mappingId:guid}", GetMappingAsync).WithName("GetDeliveryMapping");
        DeliveryValueCheckEndpoints.MapReads(delivery);

        // What keeps a flow's records blocked, grouped by issue, and one issue with the files its records came from.
        DeliveryIssueEndpoints.MapReads(delivery);

        // The reversals of runs and submissions, each with its records by outcome.
        DeliveryReversalEndpoints.MapReads(delivery);

        // The report of assertion flows: boards, runs, history and the report of a run in every format.
        DeliveryAssertionEndpoints.MapReads(delivery);

        // The dimensions of dimension flows: boards, members and originals, builds, the change log, filters and exports.
        DeliveryDimensionEndpoints.MapReads(delivery);

        // The views dimension flows declare over their tables: every view, a flow's, and one with its checks, SQL and YAML.
        DeliveryDimensionViewEndpoints.MapReads(delivery);

        // The inventories of inventory flows: what OSDU serves set against the ledgers, by finding, a page at a time, and exports.
        DeliveryInventoryEndpoints.MapReads(delivery);

        // Removal flows: a flow's removals in a partition, their runs, and the deletions a delivery ledger holds.
        DeliveryRemovalEndpoints.MapReads(delivery);

        // The missing documents: flows no repository declares any more whose ledgers hold anything, and who can take them over.
        DeliveryOrphanedLedgerEndpoints.MapReads(delivery);
        delivery.MapGet("/caches", ListCachesAsync).WithName("ListDeliveryCaches");
        delivery.MapGet("/cache/items", ListCachedItemsAsync).WithName("ListDeliveryCachedItems");
        delivery.MapGet("/cache/versions", ListCacheVersionsAsync).WithName("ListDeliveryCacheVersions");
        delivery.MapGet("/cache/diff", CompareCacheVersionsAsync).WithName("CompareDeliveryCacheVersions");
        delivery.MapGet("/cache/history", ListCacheHistoryAsync).WithName("ListDeliveryCacheHistory");
        delivery.MapGet("/cache/tags", ListUpdateTagsAsync).WithName("ListDeliveryUpdateTags");
        delivery.MapGet("/cache/gaps", ListCacheGapsAsync).WithName("ListDeliveryCacheGaps");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/cache", ListRecordCacheUsesAsync).WithName("ListDeliveryRecordCacheUses");

        // What deliveries created in OSDU: a record's artifacts, and a flow's open undos with the records that hold them.
        DeliveryArtifactEndpoints.MapReads(delivery);
        return group;
    }

    public static RouteGroupBuilder MapDeliveryWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");

        // The explorer's reads of what OSDU holds, each run in this process through a flow's connection, as a record's read-back is.
        delivery.MapDeliveryExplorerOperateEndpoints();

        // The records ride in the body, so the route reads a larger body than the default and no larger than that.
        delivery.MapPost("/flows/{pipelineId:guid}/release", ReleaseFlowAsync).WithName("ReleaseDeliveryFlowRecords");
        delivery.MapPost("/flows/{pipelineId:guid}/redeliver", RedeliverFlowAsync).WithName("RedeliverDeliveryFlowRecords");
        delivery.MapPost("/flows/{pipelineId:guid}/adopt", AdoptFlowAsync).WithName("AdoptDeliveryFlowRecords");
        delivery.MapPost("/flows/{pipelineId:guid}/rerender", RerenderFlowAsync).WithName("BringDeliveryFlowRecordsUpToDate");
        delivery.MapPost("/flows/{pipelineId:guid}/rerender/preview", PreviewRerenderAsync).WithName("PreviewBringingDeliveryFlowRecordsUpToDate");
        delivery.MapPost("/flows/{pipelineId:guid}/probe", ProbeAsync).WithName("ProbeDeliveryTarget");
        delivery.MapPost("/cache/tags/decide", DecideUpdateTagsAsync).WithName("DecideDeliveryUpdateTags");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/release", ReleaseRecordAsync).WithName("ReleaseDeliveryRecord");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/adopt", AdoptRecordAsync).WithName("AdoptDeliveryRecord");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/redeliver", RedeliverAsync).WithName("RedeliverDeliveryRecord");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/verify", VerifyRecordAsync).WithName("VerifyDeliveryRecord");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/sync", SyncRecordAsync).WithName("SyncDeliveryRecordWithSource");
        delivery.MapPost("/flows/{pipelineId:guid}/sync", SyncFlowAsync).WithName("SyncDeliveryFlowWithSource");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/read", ReadRecordAsync).WithName("ReadDeliveryRecordBack");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/source", ReadSourceAsync).WithName("ReadDeliveryRecordSource");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/preview", PreviewRecordAsync).WithName("PreviewDeliveryRecord");
        delivery.MapPost("/flows/{pipelineId:guid}/preview", PreviewAsync).WithName("PreviewDeliveryFlowRecord");
        delivery.MapPost("/flows/{pipelineId:guid}/scope-values", ScopeValuesAsync).WithName("ListDeliveryFlowScopeValues");
        DeliveryValueCheckEndpoints.MapWrites(delivery);
        DeliveryIssueEndpoints.MapWrites(delivery);

        // What reversing a run or a submission would reach, and the reverse run that puts OSDU back as it was before it.
        DeliveryReversalEndpoints.MapWrites(delivery);
        delivery.MapPost("/flows/{pipelineId:guid}/osdu/read", ReadTargetAsync).WithName("ReadDeliveryOsduRecord");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/delete", DeleteRecordAsync).WithName("DeleteDeliveryRecord");
        delivery.MapPost("/flows/{pipelineId:guid}/records/remove", RemoveRecordsAsync).WithName("RemoveDeliveryRecords");
        delivery.MapPost("/flows/{pipelineId:guid}/records/remove/preview", PreviewRemovalAsync).WithName("PreviewDeliveryRemoval");
        delivery.MapPost("/flows/{pipelineId:guid}/records/purge", PurgeRecordsAsync).WithName("PurgeDeliveryRecordsFromLedger");
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/purge", PurgeRecordAsync).WithName("PurgeDeliveryRecordFromLedger");

        // Deleting a flow's whole ledger, every interface's, through a run of the pipeline that removes its records from OSDU first.
        DeliveryLedgerEndpoints.MapWrites(delivery);

        // A missing document's ledgers taken over by a declared flow, each moved at once, as the person who asked.
        DeliveryOrphanedLedgerEndpoints.MapWrites(delivery);
        DeliveryInventoryEndpoints.MapWrites(delivery);

        // The approval of what a removal flow's guard held, and an operator's lift of a deletion, which delivers its record again.
        DeliveryRemovalEndpoints.MapWrites(delivery);
        delivery.MapPost("/records/{flowId:guid}/{key:guid}/lift-removal", LiftRemovalAsync).WithName("LiftDeliveryRecordRemoval");
        delivery.MapPost("/flows/{pipelineId:guid}/lift-removal", LiftFlowRemovalsAsync).WithName("LiftDeliveryFlowRemovals");

        // Purging a partition cache's history: its preview, and the purge itself, an admin's.
        DeliveryCachePruneEndpoints.MapWrites(delivery);
        DeliveryDimensionEndpoints.MapWrites(delivery);
        DeliveryDimensionViewEndpoints.MapWrites(delivery);
        return group;
    }

    // ---- Reads ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A flow's dashboard card: one interface's counts when the request names it (or the flow has one), and the sum of
    /// every interface's otherwise, which is what a source as a whole holds. A flow that works in partitions is counted in
    /// the partition the request names, or, when it names none, as a whole: the ledgers of every partition it serves added
    /// up, as a source's interfaces are.
    /// </summary>
    private static async Task<Results<Ok<DeliveryFlowStatsDto>, ProblemHttpResult>> GetStatsAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents,
        IPartitionRegistry partitions, ILedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var (unbound, problem) = await ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (unbound is null)
        {
            return problem!;
        }

        var whole = unbound.Source.Partitioned && string.IsNullOrWhiteSpace(partition);
        var registry = whole && unbound.Source.FollowsRegistry
            ? await partitions.ReadAsync(ct).ConfigureAwait(false)
            : await RegistryForAsync(partitions, unbound.Source, partition, ct).ConfigureAwait(false);
        var source = unbound;
        if (!whole)
        {
            (source, var unpartitioned) = await BindKeptAsync(ledger, unbound, partition, registry, ct).ConfigureAwait(false);
            if (unpartitioned is not null)
            {
                return unpartitioned;
            }
        }

        IReadOnlyList<FlowDefinition> flows;
        if (interfaceName is null)
        {
            flows = whole ? source.Source.EveryLedger(registry).ToList() : source.Source.Interfaces;
        }
        else if (Pick(source.Source, interfaceName, out var named) is { } unknown)
        {
            return unknown;
        }
        else
        {
            flows = whole
                ? source.Source.EveryLedger(registry).Where(f => string.Equals(f.Interface, named!.Interface, StringComparison.Ordinal)).ToList()
                : [named!];
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var stats = new List<(FlowDefinition Flow, FlowStats Stats)>(flows.Count);
        foreach (var flow in flows)
        {
            stats.Add((flow, await ledger.StatsAsync(flow.Id, now, ct).ConfigureAwait(false)));
        }

        return TypedResults.Ok(StatsDto(source.Pipeline, stats, await HeaderPartitionAsync(ledger, flows, ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// The partition the ledgers of <paramref name="flows"/> are kept under, for flows whose partition is their data-partition-id
    /// header, as the ledger's directory holds it; null for flows bound to a partition, and while none of them has run.
    /// </summary>
    private static async Task<string?> HeaderPartitionAsync(ILedger ledger, IEnumerable<FlowDefinition> flows, CancellationToken ct)
    {
        foreach (var flow in flows)
        {
            if (flow.Partitioned)
            {
                return null;
            }

            if (await ledger.GetLedgerAsync(flow.Id, ct).ConfigureAwait(false) is { Partition: { } kept })
            {
                return kept;
            }
        }

        return null;
    }

    /// <summary>
    /// A flow's interfaces in document order, each with how it is delivered, the order a run takes it in and its counts. A
    /// flow that works in partitions lists them in the partition the request names, or, when it names none, in every
    /// partition it serves (the ones it names, in its order, or every registered one), each row naming its partition: the
    /// one listing that says which partitions a flow delivers to and how each stands.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryInterfaceDto>>, ProblemHttpResult>> ListInterfacesAsync(
        Guid pipelineId, [FromQuery] string? partition, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        EngineContext engine, ILedger ledger, TimeProvider clock, CancellationToken ct)
    {
        var (unbound, problem) = await ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (unbound is null)
        {
            return problem!;
        }

        var whole = unbound.Source.Partitioned && string.IsNullOrWhiteSpace(partition);
        var registry = whole && unbound.Source.FollowsRegistry
            ? await partitions.ReadAsync(ct).ConfigureAwait(false)
            : await RegistryForAsync(partitions, unbound.Source, partition, ct).ConfigureAwait(false);
        var source = unbound;
        if (!whole)
        {
            (source, var unpartitioned) = await BindKeptAsync(ledger, unbound, partition, registry, ct).ConfigureAwait(false);
            if (unpartitioned is not null)
            {
                return unpartitioned;
            }
        }

        // The kind each mapping fills is what the repository sync read; a mapping it could not read has none yet. The
        // interfaces are described once per partition, so each partition listed has its own rows.
        var listedPartitions = whole ? source.Source.Served(registry).Select(p => (string?)p).ToList() : [source.Source.Partition];
        var described = new List<DeliveryInterface>();
        foreach (var named in listedPartitions)
        {
            described.AddRange(await DeliveryInterfaceCatalog.OfFlowAsync(osdu, source.Pipeline.RepoId, source.Pipeline.Name, named, ct).ConfigureAwait(false));
        }

        var (order, orderProblem) = await OrderAsync(osdu, documents, engine, source, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow().UtcDateTime;
        var listed = whole ? source.Source.EveryLedger(registry).ToList() : source.Source.Interfaces;
        var result = new List<DeliveryInterfaceDto>(listed.Count);
        foreach (var flow in listed)
        {
            var stats = await ledger.StatsAsync(flow.Id, now, ct).ConfigureAwait(false);
            var kind = described.FirstOrDefault(d => d.LedgerFlowId == flow.Id)?.Kind;
            var name = flow.Interface ?? string.Empty;
            result.Add(new DeliveryInterfaceDto(
                flow.Interface, flow.Id, flow.LedgerName, DeliveryProtocols.Name(flow.Target.Protocol), flow.RouteReason, flow.Render.Mapping,
                string.IsNullOrEmpty(kind) ? null : kind, flow.Source.Record.Object, flow.After,
                StatsDto(source.Pipeline, [(flow, stats)], await HeaderPartitionAsync(ledger, [flow], ct).ConfigureAwait(false)),
                order.WaveOf(name),
                order.WaitsFor(name).Select(d => Wait(d, d.DependsOn)).ToList(),
                order.NotWaitedFor.Where(d => string.Equals(d.Interface, name, StringComparison.OrdinalIgnoreCase)).Select(d => Wait(d, d.DependsOn)).ToList(),
                orderProblem,
                flow.Parameters.Select(p => new DeliveryParameterDto(
                    p.Key, p.Value.Required, p.Value.Default, p.Value.Description,
                    flow.Source.Record.Scope.FirstOrDefault(s => string.Equals(s.Value, p.Key, StringComparison.Ordinal)).Key)).ToList(),
                flow.Source.Record.Key,
                flow.Partition));
        }

        return TypedResults.Ok<IReadOnlyList<DeliveryInterfaceDto>>(result);
    }

    private static DeliveryInterfaceWaitDto Wait(InterfaceDependency dependency, string other)
        => new(other, dependency.Origin == DependencyOrigin.After ? "after" : "schema", dependency.Why);

    /// <summary>
    /// The order a run takes the source's interfaces in, worked out as the run's preflight works it out: from <c>after:</c>
    /// and the relationships the mappings fill, read from the repository's synced mappings and the templates the template
    /// folders its last sync recorded on each mapping's path hold. When a mapping or template is missing, or the interfaces
    /// wait for each other, the order <c>after:</c> alone gives is returned with the reason.
    /// </summary>
    private static async Task<(InterfaceOrderPlan Order, string? Problem)> OrderAsync(
        OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine, SourceContext source, CancellationToken ct)
    {
        var names = source.Source.Interfaces.Select(f => f.Interface ?? string.Empty).ToList();
        var declared = InterfaceOrder.Declared(source.Source);
        if (names.Count < 2)
        {
            return (InterfaceOrder.Plan(names, declared, []), null);
        }

        var references = source.Source.Interfaces.Select(f => f.Render.Mapping).Distinct(StringComparer.Ordinal).ToList();
        var rows = await osdu.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == source.Pipeline.RepoId && references.Contains(m.Reference) && m.Status == "valid")
            .Select(m => new { m.Reference, m.Yaml, m.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);
        var schemas = new List<InterfaceSchema>(names.Count);
        Catalog.SyncedTemplates? folders = null;
        foreach (var flow in source.Source.Interfaces)
        {
            var row = rows.FirstOrDefault(r => r.Reference == flow.Render.Mapping);
            if (row is null)
            {
                return (InterfaceOrder.Plan(names, declared, []), $"Mapping {flow.Render.Mapping} of interface '{flow.Interface}' is not among the repository's valid mappings, so only after: orders the interfaces.");
            }

            try
            {
                folders ??= await Catalog.SyncedTemplates.ReadAsync(osdu, engine.RepositoryFolders, source.Pipeline.RepoId, ct).ConfigureAwait(false);
                var mapping = documents.ParseMapping(row.Yaml, row.RelativePath, folders.For(row.RelativePath));
                var resolved = await folders.ResolveAsync(mapping.TemplateDocument, mapping.Template.Kind, mapping.Template.Version, ct).ConfigureAwait(false);
                if (resolved.Schema is not { } schema)
                {
                    return (InterfaceOrder.Plan(names, declared, []), $"Only after: orders the interfaces, since the template of mapping {mapping.Reference} of interface '{flow.Interface}' cannot be read: {resolved.Problem}");
                }

                schemas.Add(InterfaceSchemas.Describe(flow.Interface ?? string.Empty, mapping, OsduTemplate.From(schema)));
            }
            catch (FlowValidationException ex)
            {
                return (InterfaceOrder.Plan(names, declared, []), $"Mapping {flow.Render.Mapping} of interface '{flow.Interface}' could not be read ({ex.Message}), so only after: orders the interfaces.");
            }
        }

        try
        {
            return (InterfaceOrder.Plan(names, declared, schemas), null);
        }
        catch (DeliveryException ex)
        {
            return (InterfaceOrder.Plan(names, declared, []), ex.Message + " Until then a run refuses to start, and only after: orders the interfaces here.");
        }
    }

    /// <summary>
    /// The dashboard card of one ledger, or of several added up: a source's interfaces, a flow's partitions, or both. It
    /// names the interface and the partition when every ledger counted shares one, and lists the partitions it covers.
    /// </summary>
    private static DeliveryFlowStatsDto StatsDto(CatalogPipeline pipeline, IReadOnlyList<(FlowDefinition Flow, FlowStats Stats)> stats, string? headerPartition)
    {
        var one = stats.Count == 1 ? stats[0].Flow : null;
        var interfaces = stats.Select(s => s.Flow.Interface ?? string.Empty).Distinct(StringComparer.Ordinal).ToList();
        var partitions = stats.Select(s => s.Flow.Partition).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var last = stats.Select(s => s.Stats.LastSubmission).OfType<SubmissionState>().OrderByDescending(s => s.ReceivedUtc).FirstOrDefault();
        return new DeliveryFlowStatsDto(
            pipeline.Id, pipeline.Name, one?.Id ?? Guid.Empty,
            stats.Sum(s => s.Stats.Total), stats.Sum(s => s.Stats.Pending), stats.Sum(s => s.Stats.Delivering), stats.Sum(s => s.Stats.Delivered),
            stats.Sum(s => s.Stats.Held), stats.Sum(s => s.Stats.Failed), stats.Sum(s => s.Stats.Deleted), stats.Sum(s => s.Stats.Drifted),
            stats.Sum(s => s.Stats.DeliveredLast24h), stats.Max(s => s.Stats.LastDeliveredUtc), stats.Max(s => s.Stats.LastVerifiedUtc),
            stats.Sum(s => s.Stats.Submissions), last is null ? null : ToDto(last),
            interfaces.Count == 1 && stats.Count > 0 ? stats[0].Flow.Interface : null,
            interfaces.Count,
            stats.Sum(s => s.Stats.Waiting),
            partitions.Count == 1 ? partitions[0] : null,
            partitions.Count > 0 ? partitions : null,
            headerPartition,
            one is null ? null : RedeliverScopes.For(one),
            stats.Sum(s => s.Stats.Reverted),
            stats.Sum(s => s.Stats.Blocked),
            stats.Sum(s => s.Stats.Stranded),
            stats.Sum(s => s.Stats.Adoptable));
    }

    private static async Task<Results<Ok<PagedResult<DeliveryRecordDto>>, ProblemHttpResult>> ListRecordsAsync(
        Guid pipelineId, string? search, string? mode, string? status, Guid? submissionId, Guid? runId, bool? drifted, Guid? deliveredBy, string? issue, int? page, int? pageSize,
        [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        var (flow, unresolved) = await ResolveKeptAsync(db, documents, partitions, ledger, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return unresolved!;
        }

        var (query, invalid) = BuildQuery(new DeliveryRecordFilterDto(status, search, mode, submissionId, runId, drifted == true, deliveredBy, issue));
        if (query is null)
        {
            return invalid!;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var offset = (long)(p - 1) * size;
        if (offset >= RecordListing.CountLimit)
        {
            return TooBroad(
                $"A record listing pages through its first {RecordListing.CountLimit} records, and page {p} of {size} starts past them. " +
                "Narrow the filter (a status, a submission, a run or a search) to reach the records beyond.");
        }

        query = query with { Offset = (int)offset, Max = size };
        try
        {
            var items = await ledger.ListAsync(flow.FlowId, query, ct).ConfigureAwait(false);
            var total = await ledger.CountAsync(flow.FlowId, query, RecordListing.CountLimit + 1, ct).ConfigureAwait(false);
            return TypedResults.Ok(new PagedResult<DeliveryRecordDto>(
                items.Select(ToDto).ToList(), p, size, Math.Min(total.Count, RecordListing.CountLimit), TotalCapped: !total.Exact));
        }
        catch (RecordQueryTooBroadException ex)
        {
            return TooBroad(ex.Message);
        }
    }

    /// <summary>The listing filter of a request, or the problem to answer with when it names something unknown.</summary>
    private static (RecordQuery? Query, ProblemHttpResult? Problem) BuildQuery(DeliveryRecordFilterDto filter)
    {
        RecordStatus? status = null;
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            if (!Enum.TryParse<RecordStatus>(filter.Status, ignoreCase: true, out var parsed))
            {
                return (null, TypedResults.Problem(
                    detail: $"status must be one of {string.Join(", ", Enum.GetNames<RecordStatus>().Select(n => n.ToLowerInvariant()))}.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"));
            }

            status = parsed;
        }

        long? issue = null;
        if (!string.IsNullOrWhiteSpace(filter.Issue))
        {
            if (!ProblemSignature.TryParse(filter.Issue.Trim().ToLowerInvariant(), out var parsed))
            {
                return (null, TypedResults.Problem(
                    detail: $"issue '{filter.Issue}' is not an issue: an issue is the {ProblemSignature.TextLength} characters the flow's issues listing gives it.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"));
            }

            issue = parsed;
        }

        return (new RecordQuery
        {
            Status = status,
            Problem = issue,
            Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim(),
            Mode = string.Equals(filter.Mode, "contains", StringComparison.OrdinalIgnoreCase) ? SearchMode.Contains : SearchMode.Prefix,
            SubmissionId = filter.SubmissionId,
            DeliveredBySubmissionId = filter.DeliveredBy,
            RunId = filter.RunId,
            Drifted = filter.Drifted,
        }, null);
    }

    /// <summary>
    /// A record by what an operator holds, across every flow: the Records page's lookup. A delivery key lands on the
    /// record of every flow reading that row; anything else is a prefix over the OSDU id, the source key, the label and
    /// the ingestion file name, narrowed to one custody state and to one flow's ledger identity (<paramref name="flowId"/>,
    /// one of <see cref="ListRecordFlowsAsync"/>) when asked. With no term it is the ledger's recency listing instead: the
    /// records the delivery system last took in or sent, newest first, which is what the page shows before anything is
    /// typed. Both are indexed reads, so they answer in milliseconds at production volume and reach no further than the
    /// candidate bound; a page past that bound is empty rather than a scan, and the listing has to be narrowed instead.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DeliveryRecordHitDto>>, ProblemHttpResult>> LookupRecordsAsync(
        string? search, string? status, Guid? flowId, [FromQuery] string? partition, int? page, int? pageSize, CatalogDbContext db, OsduDbContext osdu, ILedger ledger,
        HttpRequest request, CancellationToken ct)
    {
        // The partition the page reads: the one the request names, else the workbench's.
        partition = WorkbenchPartition.Named(partition, request);
        var term = search?.Trim() is { Length: > 0 } typed ? typed : null;
        var (query, invalid) = BuildQuery(new DeliveryRecordFilterDto(status, null, null, null, null));
        if (query is null)
        {
            return invalid!;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var wanted = (long)p * size;
        var take = (int)Math.Min(wanted, RecordListing.LookupCandidateLimit);
        var skip = (p - 1) * size;
        var found = skip >= RecordListing.LookupCandidateLimit
            ? []
            : term is null
                ? await ledger.ListRecentAsync(take, query.Status, flowId, partition, ct).ConfigureAwait(false)
                : await ledger.LookupAsync(term, take, query.Status, flowId, partition, ct).ConfigureAwait(false);
        var items = found.Skip(skip).Take(size).ToList();

        // Fewer records than asked for means neither the recency index nor an identity index ran into its bound, so that count is exact.
        var total = found.Count < take
            ? new BoundedCount(found.Count, Exact: true)
            : term is null
                ? await ledger.CountRecentAsync(RecordListing.LookupCandidateLimit, query.Status, flowId, partition, ct).ConfigureAwait(false)
                : await ledger.CountLookupAsync(term, RecordListing.LookupCandidateLimit, query.Status, flowId, partition, ct).ConfigureAwait(false);

        var hits = await DeliveryRecordHits.DescribeAsync(db, osdu, items, ct, term).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryRecordHitDto>(hits, p, size, total.Count, TotalCapped: !total.Exact));
    }

    /// <summary>
    /// The flows the Records page can be narrowed to: every ledger identity the synced repositories name that holds at
    /// least one record, with the pipeline and interface a hit of it is named by, found the way a hit finds them, so the
    /// choice reads as the Flow column does. A source that delivers several interfaces is one choice per interface,
    /// because its records are kept per interface and never summed; an interface that has delivered nothing yet is not a
    /// choice, since narrowing to it could only show an empty page. A ledger no synced pipeline holds any more is not a
    /// choice either; its records still appear under every flow, as "no longer synced". Ordered by flow, then interface.
    /// With <paramref name="partition"/> (or the workbench's), the ledgers of that partition alone, as the ledger's
    /// directory keeps them.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryRecordFlowDto>>> ListRecordFlowsAsync(
        [FromQuery] string? partition, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, HttpRequest request, CancellationToken ct)
    {
        partition = WorkbenchPartition.Named(partition, request);
        var named = (await ledger.ListLedgersAsync(partition, ct).ConfigureAwait(false))
            .Where(l => l.Kind == LedgerKinds.Delivery)
            .Select(l => l.FlowId)
            .ToList();
        var holding = await ledger.FlowsWithRecordsAsync(named, ct).ConfigureAwait(false);
        var ledgers = named.Where(holding.Contains).ToList();
        var found = await DeliveryPipelines.ForLedgersAsync(db, osdu, ledgers, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryRecordFlowDto>>(found
            .Select(f => new DeliveryRecordFlowDto(f.Key, f.Value.Pipeline.Id, f.Value.Pipeline.Name, NamedInterface(f.Value), KeptPartition(f.Value)))
            .OrderBy(f => f.FlowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Interface ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Partition ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static async Task<Results<Ok<DeliveryTargetDto>, ProblemHttpResult>> GetTargetAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, IPartitionRegistry partitions, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        return flow is null ? problem! : TypedResults.Ok(await ToTargetDtoAsync(osdu, flow, ct).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliverySubmissionDto>>, ProblemHttpResult>> ListSubmissionsAsync(
        Guid pipelineId, int? max, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        var (flow, problem) = await ResolveKeptAsync(db, documents, partitions, ledger, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var submissions = await ledger.ListSubmissionsAsync(flow.FlowId, Math.Clamp(max ?? 100, 1, 1000), ct: ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliverySubmissionDto>>(submissions.Select(ToDto).ToList());
    }

    /// <summary>A retrieval flow's runs, newest first. The flow id derives from the pipeline's name, as the executor derives it.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryRetrievalDto>>, ProblemHttpResult>> ListRetrievalsAsync(
        Guid pipelineId, int? max, CatalogDbContext db, ILedger ledger, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return NotFound("pipeline", pipelineId);
        }

        if (!string.Equals(pipeline.Kind, RetrievalDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(
                detail: $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a retrieval flow.",
                statusCode: StatusCodes.Status409Conflict, title: "Not a retrieval flow");
        }

        var rows = await ledger.ListRetrievalsAsync(FlowId.Of(pipeline.Name), Math.Clamp(max ?? 100, 1, 1000), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryRetrievalDto>>(rows.Select(r => new DeliveryRetrievalDto(
            r.RetrievalId, r.FlowId, r.FlowName, r.RunId, r.Actor, r.Kinds, r.Query, r.WindowField, r.WindowFrom, r.WindowTo, r.Location, r.ManifestLocation,
            r.Status, r.Records, r.Files, r.Bytes, r.StartedUtc, r.CompletedUtc, r.Error)).ToList());
    }

    private static async Task<Results<Ok<DeliveryRecordDetailDto>, ProblemHttpResult>> GetRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return await ledger.FindPurgedAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false) is { } purged
                ? RecordPurged(purged)
                : RecordNotFound(flowId, key);
        }

        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, record.FlowId, ct).ConfigureAwait(false);
        DeliveryRecordLinkDto? waitsOn = null;
        var holders = record is { Status: RecordStatus.Waiting, WaitingFor: { } waitingFor }
            ? await ledger.ListHoldersAsync(record.FlowId, waitingFor, 1, ct).ConfigureAwait(false)
            : [];
        if (holders.Count > 0)
        {
            waitsOn = await LinkAsync(db, osdu, holders[0], ct).ConfigureAwait(false);
        }

        var waitedOnBy = new List<DeliveryRecordLinkDto>();
        if (record.TargetId is { } targetId)
        {
            foreach (var waiter in await ledger.ListWaitingForAsync(record.FlowId, targetId, MaxWaitersShown, ct).ConfigureAwait(false))
            {
                waitedOnBy.Add(await LinkAsync(db, osdu, waiter, ct).ConfigureAwait(false));
            }
        }

        // The record and its parent are the same minted id in two namespaces: each page links to the other.
        DeliveryRecordLinkDto? parent = null;
        if (record.ParentId is { } parentId
            && (await ledger.ListHoldersAsync(record.FlowId, parentId, 1, ct).ConfigureAwait(false)) is [var holder, ..])
        {
            parent = await LinkAsync(db, osdu, holder, ct).ConfigureAwait(false);
        }

        var readBy = new List<DeliveryRecordLinkDto>();
        if (record.TargetId is { } id)
        {
            foreach (var child in await ledger.ListChildrenOfAsync(record.FlowId, id, MaxWaitersShown, ct).ConfigureAwait(false))
            {
                readBy.Add(await LinkAsync(db, osdu, child, ct).ConfigureAwait(false));
            }
        }

        // The ledger's deletion of the record's key, when a removal flow found its source no longer holds it.
        var removedKey = await ledger.GetRemovedKeyAsync(record.FlowId, record.DeliveryKey, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryRecordDetailDto(
            ToDto(record), found?.Pipeline.Id, found?.Pipeline.RepoId, found?.Pipeline.Name, NamedInterface(found), waitsOn, waitedOnBy,
            KeyColumnsOf(documents, found), NamedPartition(found), parent, readBy, removedKey is null ? null : DeliveryRemovalEndpoints.ToDto(removedKey)));
    }

    /// <summary>
    /// The key columns a ledger identity's interface declares (<c>source.record.key</c>), in the order its records' key
    /// tuples hold their parts; null when the catalog holds no pipeline for it, its copy does not parse, or it no longer
    /// declares the interface.
    /// </summary>
    private static IReadOnlyList<string>? KeyColumnsOf(DeliveryDocumentLoader documents, LedgerPipeline? found)
    {
        if (found is null || Parse(documents, found.Pipeline).Source is not { } source)
        {
            return null;
        }

        return Pick(source.Source, NamedInterface(found), out var flow) is null ? flow!.Source.Record.Key : null;
    }

    /// <summary>How many of the records waiting for one record its page lists.</summary>
    public const int MaxWaitersShown = 50;

    /// <summary>A record as another record's page links to it: its flow, pipeline and interface, and how it stands.</summary>
    private static async Task<DeliveryRecordLinkDto> LinkAsync(CatalogDbContext db, OsduDbContext osdu, RecordState record, CancellationToken ct)
    {
        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, record.FlowId, ct).ConfigureAwait(false);
        return new DeliveryRecordLinkDto(
            record.FlowId, record.DeliveryKey.Value, found?.Pipeline.Id, found?.Pipeline.Name, NamedInterface(found),
            record.SourceKey, record.Label, record.TargetId, record.Status.ToString().ToLowerInvariant(), record.Partition ?? KeptPartition(found));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryAttemptDto>>, ProblemHttpResult>> ListRecordAttemptsAsync(
        Guid flowId, Guid key, int? max, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return RecordNotFound(flowId, key);
        }

        var attempts = await ledger.ListAttemptsAsync(flowId, record.DeliveryKey, Math.Clamp(max ?? 100, 1, MaxAttempts), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryAttemptDto>>(attempts.Select(ToDto).ToList());
    }

    /// <summary>
    /// The record's row through its ingestion table: when it arrived, and every change of it the ledger recorded, each
    /// with the ingestion run that wrote it and the landing that brought its file in, as the platform recorded them. A
    /// run that reloaded the row without changing it is not a change and is not named. The delivery half of the record's
    /// history is its attempts and activities, which the page reads beside it.
    /// </summary>
    private static async Task<Results<Ok<DeliveryRecordChainDto>, ProblemHttpResult>> GetRecordChainAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        return record is null
            ? RecordNotFound(flowId, key)
            : TypedResults.Ok(await RecordChain.OfAsync(
                db, ledger, record, await SourceTableAsync(osdu, record.FlowId, ct).ConfigureAwait(false), ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The ingestion table a flow reads its records from, which is what names the runs that wrote a row's changes. The
    /// sync records it per interface, so it is one read and it survives a document that stopped parsing; a ledger no
    /// synced interface names any more costs the ingestion runs and nothing else, and the changes and their landings
    /// still answer. The declared interface wins over one left behind by an older sync.
    /// </summary>
    private static async Task<string?> SourceTableAsync(OsduDbContext osdu, Guid ledgerFlowId, CancellationToken ct)
        => await osdu.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.LedgerFlowId == ledgerFlowId && i.RecordObject != "")
            .OrderByDescending(i => i.Active)
            .ThenByDescending(i => i.LastSeenUtc)
            .Select(i => i.RecordObject)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private static async Task<Results<Ok<IReadOnlyList<DeliveryActivityDto>>, ProblemHttpResult>> ListRecordActivitiesAsync(
        Guid flowId, Guid key, int? max, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return RecordNotFound(flowId, key);
        }

        var activities = await ledger.ListActivitiesAsync(
            new ActivityQuery { FlowId = flowId, DeliveryKey = key, Max = Math.Clamp(max ?? 100, 1, MaxAttempts) }, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryActivityDto>>(activities.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<DeliverySubmissionDetailDto>, ProblemHttpResult>> GetSubmissionAsync(
        Guid submissionId, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission is null)
        {
            return NotFound("submission", submissionId);
        }

        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, submission.FlowId, ct).ConfigureAwait(false);
        var runIds = await SubmissionRunsAsync(db, submissionId, submission.RunId, found?.Pipeline.Id, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliverySubmissionDetailDto(ToDto(submission), found?.Pipeline.Id, runIds, NamedInterface(found), NamedPartition(found)));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryAttemptDto>>, ProblemHttpResult>> ListSubmissionAttemptsAsync(
        Guid submissionId, int? max, ILedger ledger, CancellationToken ct)
    {
        var submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission is null)
        {
            return NotFound("submission", submissionId);
        }

        var attempts = await ledger.ListAttemptsForSubmissionAsync(submissionId, Math.Clamp(max ?? 500, 1, 5000), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryAttemptDto>>(attempts.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<PagedResult<DeliveryWorkBatchDto>>, ProblemHttpResult>> ListSubmissionBatchesAsync(
        Guid submissionId, string? status, int? page, int? pageSize, ILedger ledger, CancellationToken ct)
    {
        var submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission is null)
        {
            return NotFound("submission", submissionId);
        }

        WorkBatchStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<WorkBatchStatus>(status, ignoreCase: true, out var parsed))
            {
                return TypedResults.Problem(
                    detail: $"status must be one of {string.Join(", ", Enum.GetNames<WorkBatchStatus>().Select(n => n.ToLowerInvariant()))}.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
            }

            statusFilter = parsed;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var items = await ledger.ListWorkBatchesAsync(submissionId, size, (p - 1) * size, ct).ConfigureAwait(false);
        if (statusFilter is { } wanted)
        {
            items = items.Where(b => b.Status == wanted).ToList();
        }

        var total = await ledger.CountWorkBatchesAsync(submissionId, statusFilter, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryWorkBatchDto>(items.Select(ToDto).ToList(), p, size, (int)Math.Min(total, int.MaxValue)));
    }

    /// <summary>
    /// The audit trail, newest first. <paramref name="idle"/> false leaves out the runs that changed nothing, true lists only
    /// them (its total is how many the trail left out), and leaving it out lists every activity. One flow's trail is named by
    /// its pipeline (with the interface and partition a flow's own pages name), or by the ledger identity
    /// (<paramref name="flowId"/>, one of <see cref="ListActivityFlowsAsync"/>) the trail's flow choice carries; never both.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DeliveryActivityDto>>, ProblemHttpResult>> ListActivitiesAsync(
        Guid? pipelineId, Guid? flowId, Guid? submissionId, Guid? runId, string? kind, string? actor, string? outcome, bool? idle, DateTime? since, DateTime? until,
        int? page, int? pageSize, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents,
        IPartitionRegistry partitions, ILedger ledger, HttpRequest request, CancellationToken ct)
    {
        if (pipelineId is not null && flowId is not null)
        {
            return Invalid("Name the flow whose trail to read by its pipeline ('pipelineId') or by its ledger identity ('flowId'), not both.");
        }

        // One flow's trail named by its pipeline is read in the partition the request names, as every read of one flow is:
        // the flow's own for a flow whose partition is its header's. The trail across flows, and the trail of a ledger
        // identity, are the partition's the request names, else the workbench's, else every partition's; a ledger kept in
        // another partition than that reads empty, as the Records page's lookup of one does.
        var ledgerId = flowId;
        if (pipelineId is { } pid)
        {
            var (flow, problem) = await ResolveKeptAsync(db, documents, partitions, ledger, pid, interfaceName, partition, ct).ConfigureAwait(false);
            if (flow is null)
            {
                return problem!;
            }

            ledgerId = flow.FlowId;
        }
        else
        {
            partition = WorkbenchPartition.Named(partition, request);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = new ActivityQuery
        {
            Partition = pipelineId is null ? partition : null,
            FlowId = ledgerId,
            SubmissionId = submissionId,
            RunId = runId,
            Kind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant(),
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(),
            Outcome = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim().ToLowerInvariant(),
            Idle = idle,
            SinceUtc = since is { } s ? DateTime.SpecifyKind(s.ToUniversalTime(), DateTimeKind.Utc) : null,
            UntilUtc = until is { } u ? DateTime.SpecifyKind(u.ToUniversalTime(), DateTimeKind.Utc) : null,
            Offset = (p - 1) * size,
            Max = size,
        };
        var items = await ledger.ListActivitiesAsync(query, ct).ConfigureAwait(false);
        var total = await ledger.CountActivitiesAsync(query, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryActivityDto>(items.Select(ToDto).ToList(), p, size, total));
    }

    /// <summary>
    /// The flows the audit trail can be narrowed to: every ledger identity of the partition the request names, else the
    /// workbench's, else every partition, that has at least one activity, named by the ledger's directory as its activities
    /// name it (the flow, and the interface of a source that delivers several). A ledger with no activity is not a choice,
    /// since narrowing to it could only show an empty trail. Unlike the Records page's choices, a ledger no synced pipeline
    /// holds any more stays one: the trail keeps what was done to a flow after the flow is gone. Ordered by flow, then
    /// interface, then partition.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryActivityFlowDto>>> ListActivityFlowsAsync(
        [FromQuery] string? partition, ILedger ledger, HttpRequest request, CancellationToken ct)
    {
        partition = WorkbenchPartition.Named(partition, request);
        var ledgers = await ledger.ListLedgersAsync(partition, ct).ConfigureAwait(false);
        var active = await ledger.FlowsWithActivitiesAsync(ledgers.Select(l => l.FlowId).ToList(), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryActivityFlowDto>>(ledgers
            .Where(l => active.Contains(l.FlowId))
            .Select(l => new DeliveryActivityFlowDto(l.FlowId, l.FlowName, l.Kind, l.Interface.Length == 0 ? null : l.Interface, l.Partition))
            .OrderBy(f => f.FlowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Interface ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Partition ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static async Task<Results<Ok<DeliveryActivityDto>, ProblemHttpResult>> GetActivityAsync(
        long activityId, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var activity = await ledger.GetActivityAsync(activityId, ct).ConfigureAwait(false);
        if (activity is null)
        {
            return TypedResults.Problem(detail: $"No activity '{activityId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        // The pipeline and interface of the ledger, as a submission names them, so the entry can be acted on where it is read.
        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, activity.FlowId, ct).ConfigureAwait(false);
        return TypedResults.Ok(ToDto(activity) with { PipelineId = found?.Pipeline.Id, Interface = NamedInterface(found), NamedPartition = NamedPartition(found) });
    }

    private static async Task<Ok<IReadOnlyList<DeliveryMappingDto>>> ListMappingsAsync(Guid? repoId, string? status, OsduDbContext osdu, CancellationToken ct)
    {
        var query = osdu.DeliveryMappings.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(m => m.RepoId == r);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(m => m.Status == s);
        }

        var rows = await query.OrderBy(m => m.Name).ThenBy(m => m.Version).Take(1000).ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryMappingDto>>(rows.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<DeliveryMappingDetailDto>, ProblemHttpResult>> GetMappingAsync(Guid mappingId, OsduDbContext osdu, CancellationToken ct)
    {
        var row = await osdu.DeliveryMappings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mappingId, ct).ConfigureAwait(false);
        return row is null ? NotFound("mapping", mappingId) : TypedResults.Ok(new DeliveryMappingDetailDto(ToDto(row), row.Yaml));
    }

    /// <summary>
    /// Every partition's cache: the cache flows that fill it (the repository and the file of each, which is where what it
    /// caches is changed, its pipeline and the schedules that refresh it), the types it holds as those flows together declare
    /// them with what the current version holds of each, and the current version with the flow and run that wrote it. With
    /// <paramref name="repoId"/>, the caches the repository's cache flows fill. Read-only: a cache is defined in YAML and
    /// filled by runs.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryCacheDto>>> ListCachesAsync(Guid? repoId, CatalogDbContext db, OsduDbContext osdu, CancellationToken ct)
    {
        var definitions = await osdu.DeliveryCacheDefinitions.AsNoTracking()
            .OrderBy(c => c.Scope).ThenBy(c => c.Name).ThenBy(c => c.FlowName)
            .Take(MaxCacheDefinitions)
            .ToListAsync(ct).ConfigureAwait(false);
        if (repoId is { } r)
        {
            var filled = definitions.Where(d => d.RepoId == r).Select(d => d.Scope).ToHashSet(StringComparer.Ordinal);
            definitions = definitions.Where(d => filled.Contains(d.Scope)).ToList();
        }

        if (definitions.Count == 0)
        {
            return TypedResults.Ok<IReadOnlyList<DeliveryCacheDto>>([]);
        }

        var scopes = definitions.Select(d => d.Scope).Distinct().ToList();
        var flowNames = definitions.Select(d => d.FlowName).Distinct().ToList();
        var repoIds = definitions.Select(d => d.RepoId).Distinct().ToList();
        var repoNames = await RepoNamesAsync(db, repoIds, ct).ConfigureAwait(false);
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => repoIds.Contains(p.RepoId) && p.Kind == CacheDefinition.FlowTypeName && flowNames.Contains(p.Name))
            .Select(p => new { p.Id, p.RepoId, p.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var pipelineIds = pipelines.Select(p => p.Id).ToList();
        var memberships = await db.ScheduleMembers.AsNoTracking()
            .Where(m => pipelineIds.Contains(m.PipelineId))
            .Select(m => new { m.PipelineId, m.ScheduleId })
            .ToListAsync(ct).ConfigureAwait(false);
        var scheduleIds = memberships.Select(m => m.ScheduleId).Distinct().ToList();
        var schedules = await db.Schedules.AsNoTracking()
            .Where(s => scheduleIds.Contains(s.Id))
            .Select(s => new DeliveryCacheScheduleDto(s.Id, s.Name, s.Cron, s.IntervalSeconds, s.Parents.Any()))
            .ToListAsync(ct).ConfigureAwait(false);
        var currentRows = await osdu.DeliveryCacheVersions.AsNoTracking()
            .Where(v => scopes.Contains(v.Scope) && v.Current)
            .ToListAsync(ct).ConfigureAwait(false);
        var versionCounts = await osdu.DeliveryCacheVersions.AsNoTracking()
            .Where(v => scopes.Contains(v.Scope))
            .GroupBy(v => v.Scope)
            .Select(g => new { Scope = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Scope, g => g.Count, StringComparer.Ordinal, ct).ConfigureAwait(false);

        var caches = definitions
            .GroupBy(d => d.Scope, StringComparer.Ordinal)
            .Select(group =>
            {
                var scope = group.Key;
                var current = currentRows.FirstOrDefault(v => v.Scope == scope) is { } row ? OsduCacheStore.Info(row) : null;
                var held = current?.Types.ToDictionary(t => t.Name, t => t.Items, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var keys = current?.Types.Where(t => t.Key is not null).ToDictionary(t => t.Name, t => t.Key, StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                var flows = group
                    .GroupBy(d => (d.RepoId, d.FlowName))
                    .Select(declared =>
                    {
                        var first = declared.First();
                        var pipeline = pipelines.FirstOrDefault(p => p.RepoId == first.RepoId && p.Name == first.FlowName);
                        var refreshedBy = pipeline is null
                            ? []
                            : memberships
                                .Where(m => m.PipelineId == pipeline.Id)
                                .Join(schedules, m => m.ScheduleId, s => s.Id, (_, s) => s)
                                .OrderBy(s => s.Name, StringComparer.Ordinal)
                                .ToList();
                        // A flow that names its partitions has rows under each of them; every one it builds is listed.
                        var partitions = declared.Any(d => d.DeclaresPartitions)
                            ? definitions.Where(d => d.RepoId == first.RepoId && d.FlowName == first.FlowName).Select(d => d.Scope)
                                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList()
                            : [];
                        return new DeliveryCacheFlowDto(
                            first.FlowName, first.RepoId, repoNames.GetValueOrDefault(first.RepoId, string.Empty), first.RelativePath, pipeline?.Id,
                            declared.Select(d => d.Endpoint).FirstOrDefault(e => e is not null), refreshedBy,
                            declared.Select(d => d.Name).Order(StringComparer.Ordinal).ToList(),
                            declared.Select(d => d.Connection).FirstOrDefault(c => c is not null),
                            partitions,
                            CacheRetention.OfPartition(declared.Select(d => d.RetentionDays)));
                    })
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .ToList();

                var types = group
                    .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(declared =>
                    {
                        var ordered = declared.OrderBy(d => d.FlowName, StringComparer.Ordinal).ToList();
                        var fields = new List<(string Path, string As, List<string> Flows)>();
                        foreach (var declaration in ordered)
                        {
                            foreach (var field in OsduCacheStore.ParseFields(declaration.FieldsJson, declaration.FlowName, declaration.Name))
                            {
                                var known = fields.FindIndex(f => f.As.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
                                if (known < 0)
                                {
                                    fields.Add((field.Path, field.Name, [declaration.FlowName]));
                                }
                                else if (!fields[known].Flows.Contains(declaration.FlowName))
                                {
                                    fields[known].Flows.Add(declaration.FlowName);
                                }
                            }
                        }

                        var first = ordered[0];
                        var onChange = ordered.Any(d => d.OnChange.Equals("approve", StringComparison.OrdinalIgnoreCase)) ? "approve" : "auto";
                        return new DeliveryCacheTypeDto(
                            first.Name, first.EntityType,
                            ordered.Select(d => new DeliveryCacheTypeSourceDto(
                                d.FlowName, d.Origin, d.Kind, d.Query, d.OnChange, d.Connection, d.SourceObject, d.KeyField, d.DictionaryPath)).ToList(),
                            fields.Select(f => new DeliveryCacheFieldDto(f.Path, f.As, f.Flows)).ToList(),
                            onChange, held.GetValueOrDefault(first.Name), first.Origin, keys.GetValueOrDefault(first.Name) ?? first.KeyField);
                    })
                    .OrderBy(t => t.Name, StringComparer.Ordinal)
                    .ToList();

                return new DeliveryCacheDto(
                    scope, flows, types, current is null ? null : ToVersionDto(current), versionCounts.GetValueOrDefault(scope),
                    CacheRetention.OfPartition(group.Select(d => d.RetentionDays)));
            })
            .ToList();
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheDto>>(caches);
    }

    /// <summary>
    /// The cached records of one partition's cache, filtered by type and searched over every value they hold, so an operator
    /// can answer "is this unit cached, and under which id". The listing reads exactly one version: <paramref name="version"/>
    /// names it, and without one it is the current version, the one delivery flows render against.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DeliveryCachedItemDto>>, ProblemHttpResult>> ListCachedItemsAsync(
        string? scope, string? type, string? search, string? version, int? page, int? pageSize, OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var partition = scope.Trim();
        var resolved = await CacheVersions.ResolveAsync(osdu, partition, version, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return string.IsNullOrWhiteSpace(version)
                ? TypedResults.Ok(new PagedResult<DeliveryCachedItemDto>([], p, size, 0))
                : UnknownCacheVersion(partition, version.Trim());
        }

        try
        {
            CacheVersions.ThrowIfPruned(resolved);
        }
        catch (CacheVersionPrunedException pruned)
        {
            return PrunedCacheVersion(pruned);
        }

        var query = CacheVersions.ItemsAt(osdu, partition, resolved.Sequence);
        if (!string.IsNullOrWhiteSpace(type))
        {
            var t = type.Trim();
            query = query.Where(i => i.TypeName == t);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(i => i.RecordId.Contains(term) || i.Terms.Contains(term));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderBy(i => i.TypeName).ThenBy(i => i.RecordId)
            .Skip((p - 1) * size).Take(size)
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryCachedItemDto>(
            items.Select(i => new DeliveryCachedItemDto(i.ItemId, partition, resolved.Version, i.TypeName, i.EntityType, i.RecordId, ParseJson(i.FieldsJson))).ToList(),
            p, size, total));
    }

    /// <summary>The versions of one partition's cache, newest first, each with the flow and run that wrote it: what the version picker offers.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryCacheVersionDto>>, ProblemHttpResult>> ListCacheVersionsAsync(
        string? scope, OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        var versions = await CacheVersions.ListAsync(osdu, scope.Trim(), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheVersionDto>>(versions.Select(ToVersionDto).ToList());
    }

    /// <summary>
    /// One partition cache's history, newest first: each version with the version written before it and how many records it
    /// changed, added and removed. Naming a <paramref name="type"/> narrows the counts to that type, so the versions that
    /// changed it can be told apart.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryCacheHistoryEntryDto>>, ProblemHttpResult>> ListCacheHistoryAsync(
        string? scope, string? type, OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        var history = await CacheVersions.HistoryAsync(osdu, scope.Trim(), string.IsNullOrWhiteSpace(type) ? null : type.Trim(), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheHistoryEntryDto>>(history
            .Select(h => new DeliveryCacheHistoryEntryDto(
                ToVersionDto(h.Version), h.Before, h.Changes.Changed, h.Changes.Added, h.Changes.Removed,
                h.Types.Select(t => new DeliveryCacheHistoryTypeDto(t.TypeName, t.Change, t.Counts.Changed, t.Counts.Added, t.Counts.Removed, t.Hash, t.Items)).ToList(),
                h.BeforePruned))
            .ToList());
    }

    private static DeliveryCacheVersionDto ToVersionDto(CacheVersionInfo version)
        => new(
            version.Scope, version.Version, version.Sequence, version.CapturedUtc, version.Current, version.PreviousVersion, version.FlowName, version.RunId,
            version.CapturedBy, version.Origin, version.Items,
            version.Types.Select(t => new DeliveryCacheVersionTypeDto(
                t.Name, t.EntityType, t.Items, t.Key, t.Hash, t.Change is { } change ? CacheTypeChanges.Text(change) : null, t.Since)).ToList(),
            version.SystemProperties.Select(p => new DeliveryCacheSystemPropertyDto(p.Service, p.Name, p.State.ToString(), p.Source, p.Detail)).ToList(),
            version.PrunedUtc,
            version.PrunedBy);

    private static ProblemHttpResult NoCacheNamed()
        => TypedResults.Problem(
            title: "No partition", detail: "Name the partition whose cache to read with 'scope': its data-partition-id.", statusCode: StatusCodes.Status400BadRequest);

    private static ProblemHttpResult UnknownCacheVersion(string scope, string version)
        => TypedResults.Problem(title: "Unknown version", detail: $"The cache of partition '{scope}' holds no version '{version}'.", statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// A version whose records the cache's retention pruned: gone for good (410), which a client tells apart from a version
    /// the cache never held (404). The version is still listed, with what it was.
    /// </summary>
    private static ProblemHttpResult PrunedCacheVersion(CacheVersionPrunedException pruned)
        => TypedResults.Problem(title: "Version pruned", detail: pruned.Message, statusCode: StatusCodes.Status410Gone);

    /// <summary>The names of the repositories a cache answer mentions, by id.</summary>
    private static async Task<Dictionary<Guid, string>> RepoNamesAsync(CatalogDbContext db, IEnumerable<Guid> repoIds, CancellationToken ct)
    {
        var ids = repoIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await db.Repos.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What changed in one partition's cache between two versions: per type, the records whose captured values moved, the
    /// records the later version added and the ones it no longer holds, a page at a time. <paramref name="to"/> defaults to
    /// the current version.
    /// </summary>
    private static async Task<Results<Ok<DeliveryCacheDiffDto>, ProblemHttpResult>> CompareCacheVersionsAsync(
        string? scope, string? from, string? to, string? type, string? change, string? search, int? page, int? pageSize,
        OsduDbContext osdu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return NoCacheNamed();
        }

        if (string.IsNullOrWhiteSpace(from))
        {
            return TypedResults.Problem(
                title: "No version", detail: "Name the earlier cache version to compare with 'from'.", statusCode: StatusCodes.Status400BadRequest);
        }

        CacheItemChange? kind = null;
        if (!string.IsNullOrWhiteSpace(change))
        {
            var text = change.Trim();
            if (!text.All(char.IsLetter) || !Enum.TryParse<CacheItemChange>(text, ignoreCase: true, out var parsed))
            {
                return TypedResults.Problem(
                    title: "Unknown change", detail: $"'change' is changed, added or removed, not '{text}'.", statusCode: StatusCodes.Status400BadRequest);
            }

            kind = parsed;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = new CacheComparisonQuery(scope.Trim(), from.Trim())
        {
            ToVersion = string.IsNullOrWhiteSpace(to) ? null : to.Trim(),
            Type = string.IsNullOrWhiteSpace(type) ? null : type.Trim(),
            Change = kind,
            Search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            Skip = (int)Math.Min(int.MaxValue, (long)(p - 1) * size),
            Take = size,
        };
        CacheComparison? diff;
        try
        {
            diff = await CacheVersions.CompareAsync(osdu, query, ct).ConfigureAwait(false);
        }
        catch (CacheVersionPrunedException pruned)
        {
            return PrunedCacheVersion(pruned);
        }

        if (diff is null)
        {
            var named = query.ToVersion is null ? $"'{query.FromVersion}' (or has no current version)" : $"'{query.FromVersion}' or '{query.ToVersion}'";
            return TypedResults.Problem(
                title: "Unknown version", detail: $"The cache of partition '{query.Scope}' holds no version {named}.", statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(new DeliveryCacheDiffDto(
            diff.Scope, diff.FromVersion, diff.ToVersion, diff.Changed, diff.Added, diff.Removed,
            diff.Types.Select(t => new DeliveryCacheDiffTypeDto(t.TypeName, t.Changed, t.Added, t.Removed)).ToList(),
            new PagedResult<DeliveryCacheDiffItemDto>(
                diff.Items.Select(i => new DeliveryCacheDiffItemDto(
                    i.TypeName, i.EntityType, i.RecordId, i.Change.ToString().ToLowerInvariant(),
                    i.BeforeJson is null ? null : ParseJson(i.BeforeJson),
                    i.AfterJson is null ? null : ParseJson(i.AfterJson),
                    i.ChangedFields)).ToList(),
                p, size, diff.Total)));
    }

    /// <summary>
    /// The cache changes delivered records were built from: one row per change with what it reaches, filtered by
    /// status (pending, approved, rolling, rejected, applied) and, with <c>scope</c>, narrowed to the changes found in one
    /// partition's cache.
    /// </summary>
    private static async Task<Ok<PagedResult<DeliveryUpdateTagDto>>> ListUpdateTagsAsync(
        string? status, string? scope, int? page, int? pageSize, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var tags = await ledger.ListTagsAsync(status, size, (p - 1) * size, scope, ct).ConfigureAwait(false);
        var total = await ledger.CountTagsAsync(status, scope, ct).ConfigureAwait(false);
        var pipelines = await DeliveryPipelines.ForLedgersAsync(db, osdu, tags.SelectMany(t => t.WaitingByFlow.Keys).Distinct().ToList(), ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryUpdateTagDto>(tags.Select(t => ToDto(t, pipelines)).ToList(), p, size, total));
    }

    /// <summary>
    /// What delivered records of one partition were built without, most records first, a page at a time: the flag for records
    /// that went out without a value the cache did not hold yet (a wellbore, the access group of a field), each filled by the
    /// refresh that brings it. <c>type</c> narrows to one cached type; <c>empty</c> adds the paths read that held nothing.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DeliveryCacheGapDto>>, ProblemHttpResult>> ListCacheGapsAsync(
        string? scope, string? type, bool? empty, int? page, int? pageSize, ILedger ledger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return TypedResults.Problem(title: "No partition", detail: "Name the partition whose cache the gaps are read in, as scope.", statusCode: StatusCodes.Status400BadRequest);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var gaps = await ledger.ListCacheGapsAsync(scope.Trim(), string.IsNullOrWhiteSpace(type) ? null : type.Trim(), empty ?? false, size, (p - 1) * size, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryCacheGapDto>(
            gaps.Items.Select(g => new DeliveryCacheGapDto(g.TypeName, g.Path, g.Kind.ToString().ToLowerInvariant(), g.Key, g.Value, g.Records)).ToList(),
            p, size, gaps.Total));
    }

    /// <summary>Approves or rejects tags. Approving releases the records so the next run carries the new document.</summary>
    private static async Task<Results<Ok<DeliveryTagDecisionResult>, ProblemHttpResult>> DecideUpdateTagsAsync(
        DeliveryTagDecisionRequest request, ILedger ledger, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null || request.TagIds.Count == 0)
        {
            return TypedResults.Problem(title: "No tags", detail: "Name at least one tag to decide.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.TagIds.Count > 1000)
        {
            return TypedResults.Problem(title: "Too many tags", detail: "At most 1000 tags can be decided in one call.", statusCode: StatusCodes.Status400BadRequest);
        }

        var decided = await ledger.DecideTagsAsync(request.TagIds, request.Approve, RequestActor.Of(user) ?? "unknown", clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryTagDecisionResult(decided, request.Approve));
    }

    /// <summary>What one record read out of the cache when it was rendered, through the set it shares.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryCacheUseDto>>, ProblemHttpResult>> ListRecordCacheUsesAsync(
        Guid flowId, Guid key, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return RecordNotFound(flowId, key);
        }

        if (record.CacheSetId is not { } setId)
        {
            return TypedResults.Ok<IReadOnlyList<DeliveryCacheUseDto>>([]);
        }

        var uses = await ledger.ListCacheSetAsync(setId, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryCacheUseDto>>(uses
            .Select(u => new DeliveryCacheUseDto(u.Scope, u.TypeName, u.ItemId, u.Path, u.Kind.ToString().ToLowerInvariant(), u.ValueText))
            .ToList());
    }

    // ---- Interventions -------------------------------------------------------------------------------------------

    private static async Task<Results<Ok<DeliveryReleaseResult>, Accepted<DeliveryReleaseResult>, ProblemHttpResult>> ReleaseFlowAsync(
        Guid pipelineId, DeliveryReleaseRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var keys = request?.Keys is { Count: > 0 } k ? k.Select(g => new DeliveryKey(g)).ToList() : null;
        using var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var released = await runtime.ReleaseAsync(keys, ct).ConfigureAwait(false);
        if (request is not { Run: true })
        {
            return TypedResults.Ok(new DeliveryReleaseResult(released));
        }

        // The run reads under the parameter values the flow's last submission ran with, as a scheduled run of it would.
        var latest = await ledger.ListSubmissionsAsync(flow.FlowId, 1, null, ct).ConfigureAwait(false);
        var runId = await EnqueueRunAsync(db, dispatcher, flow, DeliverRun(flow, latest.Count > 0 ? latest[0].ParametersJson : null), request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryReleaseResult(released, runId));
    }

    /// <summary>
    /// Redelivers many of a flow's records: sends again what the scope names whatever their hashes say. Named records (by
    /// keys or by filter) are marked here under the caller's name and a deliver run is queued that sends them, as a release
    /// of many is; every delivered record is asked of a deliver run, whose node marks and sends them in one recorded run.
    /// The run reads under the parameter values the flow's last submission ran with, as a scheduled run of it would.
    /// </summary>
    private static async Task<Results<Ok<DeliveryRedeliverResult>, Accepted<DeliveryRedeliverResult>, ProblemHttpResult>> RedeliverFlowAsync(
        Guid pipelineId, DeliveryFlowRedeliverRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var part = string.IsNullOrWhiteSpace(request?.Scope) ? RedeliverScopes.All : request.Scope.Trim().ToLowerInvariant();
        RedeliverSelection selection;
        try
        {
            selection = RedeliverScopes.Of(part, flow.Flow);
        }
        catch (DeliveryException ex)
        {
            return Invalid($"scope: {ex.Message}");
        }

        var (keys, unselected) = await SelectedAsync(flow, request?.Keys, request?.Filter, request?.Expected, Intervention.Redelivery, ledger, ct).ConfigureAwait(false);
        if (unselected is not null)
        {
            return unselected;
        }

        var latest = await ledger.ListSubmissionsAsync(flow.FlowId, 1, null, ct).ConfigureAwait(false);
        var values = latest.Count > 0 ? latest[0].ParametersJson : null;
        if (keys is null && request is not { Run: false })
        {
            // Every delivered record: the node marks them in the run that sends them, however many the flow holds.
            var all = await EnqueueRunAsync(db, dispatcher, flow, new RunParameters
            {
                Operation = DeliveryOperations.Deliver,
                Values = SubmissionValues(values),
                Payload = new DeliveryRunPayload { Redeliver = part, Interface = flow.Flow.Interface }.ToJson(),
            }, request?.Pool, user, ct).ConfigureAwait(false);
            return TypedResults.Accepted($"/api/v1/runs/{all}", new DeliveryRedeliverResult(0, all));
        }

        using var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var marked = await runtime.RedeliverAsync(keys, selection, ct).ConfigureAwait(false);
        if (request is { Run: false })
        {
            return TypedResults.Ok(new DeliveryRedeliverResult(marked, null));
        }

        var runId = await EnqueueRunAsync(db, dispatcher, flow, DeliverRun(flow, values), request?.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRedeliverResult(marked, runId));
    }

    /// <summary>
    /// Brings many of a flow's records up to date: renders them again under the rules of now and sends only a part that
    /// renders differently, since their delivered hashes stay (osdu/docs/reference/concepts/removal-and-reversal.md,
    /// Redelivering records). Named records are asked here under the caller's name and a deliver run is queued that
    /// plans them; every delivered record is asked of a deliver run, whose node asks and plans them in one recorded
    /// run. A record OSDU does not hold is left as it is.
    /// </summary>
    private static async Task<Results<Ok<DeliveryRerenderResult>, Accepted<DeliveryRerenderResult>, ProblemHttpResult>> RerenderFlowAsync(
        Guid pipelineId, DeliveryRerenderRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var (keys, unselected) = await SelectedAsync(flow, request?.Keys, request?.Filter, request?.Expected, Intervention.Rerender, ledger, ct).ConfigureAwait(false);
        if (unselected is not null)
        {
            return unselected;
        }

        var latest = await ledger.ListSubmissionsAsync(flow.FlowId, 1, null, ct).ConfigureAwait(false);
        var values = latest.Count > 0 ? latest[0].ParametersJson : null;
        if (keys is null && request is not { Run: false })
        {
            var all = await EnqueueRunAsync(db, dispatcher, flow, new RunParameters
            {
                Operation = DeliveryOperations.Deliver,
                Values = SubmissionValues(values),
                Payload = new DeliveryRunPayload { Rerender = true, Interface = flow.Flow.Interface }.ToJson(),
            }, request?.Pool, user, ct).ConfigureAwait(false);
            return TypedResults.Accepted($"/api/v1/runs/{all}", new DeliveryRerenderResult(0, all));
        }

        using var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var marked = await runtime.BringUpToDateAsync(keys, ct).ConfigureAwait(false);
        if (request is { Run: false })
        {
            return TypedResults.Ok(new DeliveryRerenderResult(marked, null));
        }

        var runId = await EnqueueRunAsync(db, dispatcher, flow, DeliverRun(flow, values), request?.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRerenderResult(marked, runId));
    }

    /// <summary>
    /// Says what bringing a selection up to date would send, sending nothing: a plan run over the selection's delivered
    /// records (at most <see cref="DeliveryRerender.PreviewRecords"/> of them), each rendered under the rules of now and
    /// decided by its hashes. The run's outcome counts what would go (the record, its payload) and names the first records.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRerenderPreviewAccepted>, ProblemHttpResult>> PreviewRerenderAsync(
        Guid pipelineId, DeliveryRerenderRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, IRunDispatcher dispatcher, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        long selected;
        bool capped;
        IReadOnlyList<DeliveryKey> candidates;
        if (request?.Keys is not { Count: > 0 } && request?.Filter is null)
        {
            // Every record the flow has delivered: the ledger's own count of them, however many, and the first of them in the
            // listing's order to stand for the rest.
            var (query, invalid) = BuildQuery(new DeliveryRecordFilterDto(RecordStatus.Delivered.ToString().ToLowerInvariant(), null, null, null, null));
            if (query is null)
            {
                return invalid!;
            }

            var stats = await ledger.StatsAsync(flow.FlowId, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            (selected, capped) = (stats.Delivered, false);
            candidates = await ledger.ListKeysAsync(flow.FlowId, query, DeliveryRerender.PreviewRecords, ct).ConfigureAwait(false);
        }
        else
        {
            var (keys, unselected) = await SelectedAsync(flow, request.Keys, request.Filter, request.Expected, Intervention.Rerender, ledger, ct).ConfigureAwait(false);
            if (unselected is not null)
            {
                return unselected;
            }

            (selected, capped) = (keys!.Count, false);
            candidates = keys.Take(DeliveryRerender.PreviewRecords).ToList();
        }

        // Only a record OSDU holds is brought up to date, and a plan reads a record by the key tuple the ledger stored.
        var records = await ledger.GetRecordsAsync(flow.FlowId, candidates, ct).ConfigureAwait(false);
        var checkable = candidates
            .Where(k => records.TryGetValue(k, out var r) && r.Status == RecordStatus.Delivered && r.TargetId is not null && r.SourceKeyJson is not null)
            .Select(k => k.Value)
            .ToList();
        if (checkable.Count == 0)
        {
            return TypedResults.Problem(
                detail: "None of the selected records is delivered with the source key it was built from, so there is nothing to bring up to date. A held, failed, removed or reverted record is released, or redelivered, instead.",
                statusCode: StatusCodes.Status409Conflict, title: "Nothing to check");
        }

        var latest = await ledger.ListSubmissionsAsync(flow.FlowId, 1, null, ct).ConfigureAwait(false);
        var runId = await EnqueueRunAsync(db, dispatcher, flow, new RunParameters
        {
            Operation = DeliveryOperations.Plan,
            Values = SubmissionValues(latest.Count > 0 ? latest[0].ParametersJson : null),
            Payload = new DeliveryRunPayload { RecordKeys = checkable, Rerender = true, Interface = flow.Flow.Interface }.ToJson(),
        }, null, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRerenderPreviewAccepted(runId, checkable.Count, selected, capped));
    }

    /// <summary>What a many-record intervention is called in what it answers.</summary>
    private sealed record Intervention(string Noun, string Verb)
    {
        public static Intervention Redelivery { get; } = new("A redelivery", "redeliver");

        public static Intervention Rerender { get; } = new("A request to bring records up to date", "bring up to date");

        public static Intervention Purge { get; } = new("Deleting from the ledger", "delete from the ledger");

        public static Intervention Adoption { get; } = new("An adoption", "adopt");
    }

    /// <summary>
    /// Asks, as the caller, a flow's records held because OSDU already holds a record at the id each one's key makes to adopt
    /// it, the partition typed back first (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already
    /// holds): the records the request names by keys, every one its filter matches, or, with neither, every such record of
    /// the interface it names, or of every interface of the source when it names none. A record held for another reason is
    /// left, and the answer counts both. With <c>run</c>, a deliver run is queued (of the whole source for an adoption of every
    /// interface), which takes the records over.
    /// </summary>
    private static async Task<Results<Ok<DeliveryAdoptResult>, Accepted<DeliveryAdoptResult>, ProblemHttpResult>> AdoptFlowAsync(
        Guid pipelineId, DeliveryFlowAdoptRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db,
        DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine, DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var selects = request?.Keys is { Count: > 0 } || request?.Filter is not null;
        IReadOnlyList<FlowContext> ledgers;
        if (selects || interfaceName is not null)
        {
            var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
            if (flow is null)
            {
                return problem!;
            }

            ledgers = [flow];
        }
        else
        {
            var (unbound, problem) = await ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
            if (unbound is null)
            {
                return problem!;
            }

            if (Bind(unbound, partition, await RegistryForAsync(partitions, unbound.Source, partition, ct).ConfigureAwait(false), out var bound) is { } unbindable)
            {
                return unbindable;
            }

            ledgers = [.. bound.Source.Interfaces.Select(f => new FlowContext(bound.Pipeline, bound.Source, f))];
        }

        List<DeliveryKey>? keys = null;
        if (selects)
        {
            var (selected, unselected) = await SelectedAsync(ledgers[0], request?.Keys, request?.Filter, request?.Expected, Intervention.Adoption, ledger, ct).ConfigureAwait(false);
            if (unselected is not null)
            {
                return unselected;
            }

            keys = selected;
        }

        // The partition the ledgers are kept in, which the operator types back: one for every interface of a source bound to it.
        string? kept = null;
        foreach (var flow in ledgers)
        {
            kept ??= (await ledger.GetLedgerAsync(flow.FlowId, ct).ConfigureAwait(false))?.Partition is { Length: > 0 } named ? named : null;
        }

        var label = ledgers.Count == 1 ? ledgers[0].Flow.Label : ledgers[0].Pipeline.Name;
        if (kept is null)
        {
            return TypedResults.Problem(
                detail: $"{label} keeps no records in a partition yet, so there is nothing to adopt.",
                statusCode: StatusCodes.Status409Conflict, title: "Nothing to adopt");
        }

        var what = keys is null ? $"Adopting every record of {label} held for an id OSDU holds" : $"Adopting {keys.Count} record(s) of {label}";
        if (AdoptionUnconfirmed(request?.Confirm, kept, what, label) is { } unconfirmed)
        {
            return unconfirmed;
        }

        var asked = 0;
        foreach (var flow in ledgers)
        {
            using var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
            runtime.Actor = RequestActor.Label(user);
            asked += (keys is null ? await runtime.AdoptEveryAsync(ct).ConfigureAwait(false) : await runtime.AdoptAsync(keys, ct).ConfigureAwait(false)).Count;
        }

        var selectedCount = keys?.Count ?? asked;
        if (request is not { Run: true } || asked == 0)
        {
            return TypedResults.Ok(new DeliveryAdoptResult(selectedCount, asked, null));
        }

        // The run reads under the parameter values the flow's last submission ran with, as a release's run does; an adoption of
        // every interface runs the whole source, so each interface's records are taken over in the order its waves give.
        var latest = await ledger.ListSubmissionsAsync(ledgers[0].FlowId, 1, null, ct).ConfigureAwait(false);
        var values = latest.Count > 0 ? latest[0].ParametersJson : null;
        var run = ledgers.Count == 1
            ? DeliverRun(ledgers[0], values)
            : new RunParameters { Operation = DeliveryOperations.Deliver, Values = SubmissionValues(values), Payload = new DeliveryRunPayload().ToJson() };
        var runId = await EnqueueRunAsync(db, dispatcher, ledgers[0], run, request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryAdoptResult(selectedCount, asked, runId));
    }

    /// <summary>
    /// Why an adoption of many records is refused before anything is adopted: no partition typed back, or another one than
    /// <paramref name="kept"/>, the partition the flow's ledger is kept in (case ignored); null when it is confirmed.
    /// <paramref name="what"/> names what would be adopted, as the refusal says it.
    /// </summary>
    internal static ProblemHttpResult? AdoptionUnconfirmed(string? confirm, string kept, string what, string flowLabel)
    {
        if (confirm?.Trim() is not { Length: > 0 } typed)
        {
            return TypedResults.Problem(
                detail: $"{what} takes over every record OSDU holds at their ids in partition {kept}, which no ledger owned, and the next run writes them there. Type the partition back as confirm.",
                statusCode: StatusCodes.Status400BadRequest, title: "Confirmation required");
        }

        return string.Equals(typed, kept, StringComparison.OrdinalIgnoreCase)
            ? null
            : TypedResults.Problem(
                detail: $"The adoption confirms partition '{typed}', and the records of {flowLabel} are kept in '{kept}'. Nothing was adopted.",
                statusCode: StatusCodes.Status400BadRequest, title: "Confirmation does not match");
    }

    /// <summary>
    /// Deletes records already removed from OSDU from the ledger, in this process and asking nothing of OSDU
    /// (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting removed records from the ledger): those
    /// <c>keys</c> names, every one <c>filter</c> matches (refused when it no longer matches the <c>expected</c> count
    /// the operator was shown), or, with neither, every record the ledger marks deleted. A record OSDU may still hold
    /// is never deleted; it is counted as left.
    /// </summary>
    private static async Task<Results<Ok<DeliveryLedgerPurgeResult>, ProblemHttpResult>> PurgeRecordsAsync(
        Guid pipelineId, DeliveryLedgerPurgeRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db,
        DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine, DeliveryConfigStore config, ILedger ledger, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var (keys, unselected) = await SelectedAsync(flow, request?.Keys, request?.Filter, request?.Expected, Intervention.Purge, ledger, ct).ConfigureAwait(false);
        if (unselected is not null)
        {
            return unselected;
        }

        using var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var summary = await runtime.PurgeFromLedgerAsync(keys, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryLedgerPurgeResult(summary.Selected, summary.Purged, summary.Left, summary.Describe()));
    }

    /// <summary>Deletes one record already removed from OSDU from the ledger; refused with 409 for a record OSDU may still hold.</summary>
    private static async Task<Results<Ok<DeliveryLedgerPurgeResult>, ProblemHttpResult>> PurgeRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine, DeliveryConfigStore config, ILedger ledger,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        if (record.Status != RecordStatus.Deleted)
        {
            return TypedResults.Problem(
                detail: $"The record is {record.Status.ToString().ToLowerInvariant()}, so OSDU may still hold it: only a record removed from OSDU is deleted from the ledger. Remove it from OSDU first.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Not removed from OSDU");
        }

        if ((await ledger.OpenArtifactsAsync(flow.Flow.Id, [new DeliveryKey(key)], ct).ConfigureAwait(false)).Count is > 0 and var open)
        {
            return TypedResults.Problem(
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"An unfinished delivery of the record left {open} item(s) in OSDU that its undo has not taken back yet; the undo reaches them through the record, so it stays in the ledger. Run the flow's undo, then ask again."),
                statusCode: StatusCodes.Status409Conflict,
                title: "Undo unfinished");
        }

        using var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var summary = await runtime.PurgeFromLedgerAsync([new DeliveryKey(key)], ct).ConfigureAwait(false);
        if (summary.Purged == 0)
        {
            return TypedResults.Problem(
                detail: "Work for the record started after it was read, so it was left as it was. Ask again once it has settled.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Record busy");
        }

        return TypedResults.Ok(new DeliveryLedgerPurgeResult(summary.Selected, summary.Purged, summary.Left, summary.Describe()));
    }

    /// <summary>
    /// The records a many-record intervention names: those <paramref name="keys"/> lists, or every one <paramref name="filter"/>
    /// matches, refused when it no longer matches the <paramref name="expected"/> count the operator was shown, at most
    /// <see cref="RemovalLimits.MaxSelection"/> either way. With neither, null keys: every record the intervention reaches.
    /// </summary>
    private static async Task<(List<DeliveryKey>? Keys, ProblemHttpResult? Problem)> SelectedAsync(
        FlowContext flow, IReadOnlyList<Guid>? keys, DeliveryRecordFilterDto? filter, int? expected, Intervention intervention, ILedger ledger, CancellationToken ct)
    {
        if (keys is { Count: > 0 })
        {
            if (filter is not null)
            {
                return (null, Invalid($"{intervention.Noun} names its records either by keys or by filter, not both."));
            }

            var named = keys.Distinct().Select(k => new DeliveryKey(k)).ToList();
            if (named.Count > RemovalLimits.MaxSelection)
            {
                return (null, TypedResults.Problem(
                    detail: $"{intervention.Noun} names at most {RemovalLimits.MaxSelection} records; {named.Count} were selected. Ask it of every record of the flow, or narrow the selection.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Too many records"));
            }

            return (named, null);
        }

        if (filter is null)
        {
            return (null, null);
        }

        var (query, invalid) = BuildQuery(filter);
        if (query is null)
        {
            return (null, invalid!);
        }

        BoundedCount matched;
        try
        {
            matched = await ledger.CountAsync(flow.FlowId, query, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false);
        }
        catch (RecordQueryTooBroadException ex)
        {
            return (null, TooBroad(ex.Message));
        }

        if (!matched.Exact || matched.Count > RemovalLimits.MaxSelection)
        {
            return (null, TypedResults.Problem(
                detail: $"The filter matches more than {RemovalLimits.MaxSelection} records as far as the listing counts; ask it of every record of the flow, or narrow the filter.",
                statusCode: StatusCodes.Status409Conflict, title: "Too many records"));
        }

        if (matched.Count == 0)
        {
            return (null, TypedResults.Problem(
                detail: $"The filter matches no records, so there is nothing to {intervention.Verb}.",
                statusCode: StatusCodes.Status409Conflict, title: "Nothing selected"));
        }

        if (expected is { } shown && shown != matched.Count)
        {
            return (null, TypedResults.Problem(
                detail: $"The filter matched {shown} records when it was shown and matches {matched.Count} now. Nothing was asked; check the list and ask again.",
                statusCode: StatusCodes.Status409Conflict, title: "The selection changed"));
        }

        return ([.. await ledger.ListKeysAsync(flow.FlowId, query, RemovalLimits.MaxSelection, ct).ConfigureAwait(false)], null);
    }

    /// <summary>
    /// A deliver run of <paramref name="flow"/>'s interface under the parameter values <paramref name="parametersJson"/> names
    /// (a submission's): what a release asks for when it asks for a run. The run plans every record the release asked to be
    /// planned again, a pass at a time, and sends every record it queued.
    /// </summary>
    internal static RunParameters DeliverRun(FlowContext flow, string? parametersJson) => new()
    {
        Operation = DeliveryOperations.Deliver,
        Values = SubmissionValues(parametersJson),
        Payload = new DeliveryRunPayload { Interface = flow.Flow.Interface }.ToJson(),
    };

    private static async Task<Results<Ok<DeliveryReleaseResult>, Accepted<DeliveryReleaseResult>, ProblemHttpResult>> ReleaseRecordAsync(
        Guid flowId, Guid key, DeliveryRecordReleaseRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        using var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var released = await runtime.ReleaseAsync([record.DeliveryKey], ct).ConfigureAwait(false);
        if (request is not { Run: true })
        {
            return TypedResults.Ok(new DeliveryReleaseResult(released));
        }

        // The release is recorded under the caller's name here; the run reads the record by the key tuple the ledger stored,
        // under the parameter values its last plan ran with, and sends what it renders now.
        var last = record.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Deliver,
            Values = SubmissionValues(last?.ParametersJson),
            Payload = new DeliveryRunPayload { RecordKeys = [key], Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryReleaseResult(released, runId));
    }

    /// <summary>
    /// Lifts, as the caller, the ledger's deletion of one record's key (osdu/docs/reference/flow/removal.md, Delivering a record
    /// again): the key is no longer kept out, and the record is asked to be planned again, so the flow's next deliver run reads
    /// its row and delivers it under its id. With <c>run</c>, a deliver run scoped to the record is queued, which does it at
    /// once. A record whose key the ledger keeps out for no removal answers 409.
    /// </summary>
    private static async Task<Results<Ok<DeliveryLiftResult>, Accepted<DeliveryLiftResult>, ProblemHttpResult>> LiftRemovalAsync(
        Guid flowId, Guid key, DeliveryRecordReleaseRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        using var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        if ((await runtime.LiftRemovalsAsync([record.DeliveryKey], ct).ConfigureAwait(false)).Count == 0)
        {
            return TypedResults.Problem(
                detail: "The ledger keeps this record's key out for no removal: no removal flow found its source no longer holds it.",
                statusCode: StatusCodes.Status409Conflict, title: "Not removed by its source");
        }

        if (request is not { Run: true })
        {
            return TypedResults.Ok(new DeliveryLiftResult(1));
        }

        var last = record.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Deliver,
            Values = SubmissionValues(last?.ParametersJson),
            Payload = new DeliveryRunPayload { RecordKeys = [key], Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryLiftResult(1, runId));
    }

    /// <summary>
    /// Lifts, as the caller, the ledger's deletions of the records a request names, at most 1000 (osdu/docs/reference/flow/removal.md,
    /// Delivering a record again), in one activity naming every record lifted; a key the ledger keeps out for no removal is
    /// passed over and not counted. With <c>run</c>, a deliver run of the flow is queued under the parameter values its last
    /// submission ran with, which delivers them again at once. Refused with 400 for a request that names no key or too many.
    /// </summary>
    private static async Task<Results<Ok<DeliveryLiftResult>, Accepted<DeliveryLiftResult>, ProblemHttpResult>> LiftFlowRemovalsAsync(
        Guid pipelineId, DeliveryLiftRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db,
        DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine, DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher,
        ClaimsPrincipal user, CancellationToken ct)
    {
        if (request?.Keys is not { Count: > 0 } named || named.Count > FlowRuntime.MaxLift)
        {
            return Invalid(string.Create(CultureInfo.InvariantCulture, $"keys: name from 1 to {FlowRuntime.MaxLift} records whose removal to lift."));
        }

        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        using var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var lifted = await runtime.LiftRemovalsAsync(named.Distinct().Select(g => new DeliveryKey(g)).ToList(), ct).ConfigureAwait(false);
        if (request is not { Run: true } || lifted.Count == 0)
        {
            return TypedResults.Ok(new DeliveryLiftResult(lifted.Count));
        }

        var latest = await ledger.ListSubmissionsAsync(flow.FlowId, 1, null, ct).ConfigureAwait(false);
        var runId = await EnqueueRunAsync(db, dispatcher, flow, DeliverRun(flow, latest.Count > 0 ? latest[0].ParametersJson : null), request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryLiftResult(lifted.Count, runId));
    }

    /// <summary>
    /// Asks, as the caller, one record held because OSDU already holds a record at the id its key makes to adopt that record
    /// (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already holds): the flow's next deliver run
    /// takes it over at the version OSDU holds then. With <c>run</c>, a deliver run scoped to the record is queued, which does
    /// it at once. A record that is not held so is refused, saying why.
    /// </summary>
    private static async Task<Results<Ok<DeliveryAdoptResult>, Accepted<DeliveryAdoptResult>, ProblemHttpResult>> AdoptRecordAsync(
        Guid flowId, Guid key, DeliveryRecordAdoptRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        if (!OccupiedHolds.Adoptable(record))
        {
            var why = record switch
            {
                { ClaimedTargetId: { } claimed } => $"it claimed {claimed} already, and a record keeps the OSDU id it claimed",
                { AdoptRequestedUtc: not null, Blocked: false } => "its adoption is asked already, and the flow's next run takes it over",
                { AdoptRequestedUtc: not null } =>
                    "its adoption is asked already, and a run held it since for another reason (its error says which); release it, and the run after takes it over",
                { LeaseOwner: not null } => "a delivery holds it now",
                _ => "it is not held because OSDU already holds a record at the OSDU id its key makes",
            };
            return TypedResults.Problem(
                detail: $"Record {record.SourceKey} of {flow.Flow.Label} has nothing to adopt: {why}. An adoption takes over the record OSDU holds at the id a held record's key makes.",
                statusCode: StatusCodes.Status409Conflict, title: "Nothing to adopt");
        }

        using var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var asked = await runtime.AdoptAsync([record.DeliveryKey], ct).ConfigureAwait(false);
        if (asked.Count == 0)
        {
            return TypedResults.Problem(
                detail: $"Record {record.SourceKey} of {flow.Flow.Label} moved on since it was shown (a run planned it, or a delivery holds it), so no adoption was asked. Look at the record again.",
                statusCode: StatusCodes.Status409Conflict, title: "Not asked");
        }

        if (request is not { Run: true })
        {
            return TypedResults.Ok(new DeliveryAdoptResult(1, 1, null));
        }

        // The run reads the record by the key tuple the ledger stored, under the parameter values its last plan ran with, as
        // a record's release with a run does, and takes it over.
        var last = record.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Deliver,
            Values = SubmissionValues(last?.ParametersJson),
            Payload = new DeliveryRunPayload { RecordKeys = [key], Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryAdoptResult(1, 1, runId));
    }

    private static async Task<Results<Ok<DeliveryRedeliverResult>, Accepted<DeliveryRedeliverResult>, ProblemHttpResult>> RedeliverAsync(
        Guid flowId, Guid key, DeliveryRedeliverRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine, DeliveryConfigStore config,
        ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        // The part to send again, named by what it is on the record's route (record, files, bulk), or all of it.
        var part = string.IsNullOrWhiteSpace(request?.Scope) ? RedeliverScopes.All : request.Scope.Trim().ToLowerInvariant();
        RedeliverSelection scope;
        try
        {
            scope = RedeliverScopes.Of(part, flow.Flow);
        }
        catch (DeliveryException ex)
        {
            return TypedResults.Problem(detail: $"scope: {ex.Message}", statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // With a run, the node marks and re-sends in one recorded run (the mark carries the run id and the scope);
        // without one, the mark is made here under the caller's name and the flow's next run sends the record. The run
        // reads the record by the key tuple the ledger stored, under the parameter values its last plan ran with, so a
        // flow whose scope is a parameter reads the row in the record's own scope rather than in the flow's defaults.
        if (request?.Run ?? true)
        {
            var last = record.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
            var parameters = new RunParameters
            {
                Operation = DeliveryOperations.Deliver,
                Values = SubmissionValues(last?.ParametersJson),
                Payload = new DeliveryRunPayload { RecordKeys = [key], Redeliver = part, Interface = flow.Flow.Interface }.ToJson(),
            };
            var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, request?.Pool, user, ct).ConfigureAwait(false);
            return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRedeliverResult(1, runId));
        }

        using var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var marked = await runtime.RedeliverAsync([record.DeliveryKey], scope, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryRedeliverResult(marked, null));
    }

    /// <summary>
    /// The engine an intervention runs on in this process: its references resolve from the central configuration first, in
    /// the layer of the flow's partition, exactly as the flow's runs resolve them on a node. So the partition the flow's
    /// ledger is registered in here is the one its runs deliver to (osdu/docs/reference/concepts/ledger.md, Ledgers,
    /// flows and partitions).
    /// </summary>
    internal static async Task<EngineContext> ConfiguredAsync(EngineContext engine, DeliveryConfigStore config, FlowContext flow, CancellationToken ct)
    {
        var configuration = await config.ConfigurationAsync(flow.Pipeline.RepoId, ct).ConfigureAwait(false);
        return engine.WithSuppliedReferences(configuration.For(flow.Flow.Partition));
    }

    /// <summary>
    /// The record's rows as the ingestion tables hold them now (osdu/docs/reference/concepts/api.md, Reading OSDU and the
    /// source), read in this process (<see cref="DirectOperationRunner"/>), which opens the flow's source with the flow's
    /// own connection reference and reads the record by the key tuple the ledger stored. Nothing is planned and nothing is
    /// delivered; the answer carries the record row with its system columns, its child datasets and the origin file and row
    /// the ingestion tables record.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ReadSourceAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["deliveryKey"] = key.ToString("D") };
        if (record.LastSubmissionId is { } lastId
            && await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) is { ParametersJson.Length: > 2 } last)
        {
            // The scope predicate of the read is the one the record was planned under, so a flow whose scope is a
            // parameter reads the row in the record's own scope rather than in the flow's declared defaults.
            arguments["values"] = last.ParametersJson;
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, ReadSourceRowOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>The flow parameter values a submission was opened with; none when it recorded none or is unknown.</summary>
    internal static IReadOnlyDictionary<string, string> SubmissionValues(string? parametersJson)
    {
        try
        {
            return RunParameters.ValuesFromJson(parametersJson);
        }
        catch (SqlFlowException ex)
        {
            throw new DeliveryException($"The submission's recorded parameters are not valid: {ex.Message}", ex);
        }
    }

    private static async Task<Results<Accepted<DeliveryRunAccepted>, ProblemHttpResult>> VerifyRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Verify,
            Payload = new DeliveryRunPayload { RecordKeys = [key], Force = true, Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, null, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRunAccepted(runId, RunStatuses.Queued));
    }

    /// <summary>
    /// Consolidates the ledger with the ingestion tables for one record: a sync run on a node reads the record's row by the
    /// key the ledger stored, in the scope it was planned under, records what the ledger lacks of it, asks for it to be
    /// planned by the flow's next run when its row changed unseen, and puts a row that is gone on its history. It renders
    /// nothing and sends nothing to OSDU; the run and its activity say what it found.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRunAccepted>, ProblemHttpResult>> SyncRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Sync,
            Payload = new DeliveryRunPayload { RecordKeys = [key], Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, null, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRunAccepted(runId, RunStatuses.Queued));
    }

    /// <summary>
    /// The same for records of one interface (the flow's, in the single form): the ones the request names by key, every one
    /// its filter matches, or every record of the interface. A sync of every record pages the ledger in key order and
    /// consolidates each page with the rows the ingestion tables hold. Rows the ledger has no record of are a plan's to
    /// find, since a row's delivery key comes from its mapping.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRunAccepted>, ProblemHttpResult>> SyncFlowAsync(
        Guid pipelineId, DeliverySyncRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var keys = new List<Guid>();
        if (request?.Keys is { Count: > 0 } named)
        {
            if (request.Filter is not null)
            {
                return TypedResults.Problem(
                    detail: "A sync names its records either by keys or by filter, not both.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
            }

            keys.AddRange(named.Distinct());
            if (keys.Count > SourceSync.MaxNamedRecords)
            {
                return TypedResults.Problem(
                    detail: $"A sync names at most {SourceSync.MaxNamedRecords} records; {keys.Count} were selected. Sync every record of the flow instead.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Too many records");
            }
        }
        else if (request?.Filter is { } filter)
        {
            var (query, invalid) = BuildQuery(filter);
            if (query is null)
            {
                return invalid!;
            }

            BoundedCount matched;
            try
            {
                matched = await ledger.CountAsync(flow.FlowId, query, SourceSync.MaxNamedRecords + 1, ct).ConfigureAwait(false);
            }
            catch (RecordQueryTooBroadException ex)
            {
                return TooBroad(ex.Message);
            }

            if (!matched.Exact || matched.Count > SourceSync.MaxNamedRecords)
            {
                return TypedResults.Problem(
                    detail: $"The filter matches more than {SourceSync.MaxNamedRecords} records as far as the listing counts; sync every record of the flow, or narrow the filter.",
                    statusCode: StatusCodes.Status409Conflict, title: "Too many records");
            }

            if (matched.Count == 0)
            {
                return TypedResults.Problem(
                    detail: "The filter matches no records, so there is nothing to sync.",
                    statusCode: StatusCodes.Status409Conflict, title: "Nothing selected");
            }

            if (request.Expected is { } expected && expected != matched.Count)
            {
                return TypedResults.Problem(
                    detail: $"The filter matched {expected} records when it was shown and matches {matched.Count} now. Nothing was queued; check the list and ask again.",
                    statusCode: StatusCodes.Status409Conflict, title: "The selection changed");
            }

            keys.AddRange((await ledger.ListKeysAsync(flow.FlowId, query, SourceSync.MaxNamedRecords, ct).ConfigureAwait(false)).Select(k => k.Value));
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Sync,
            Payload = new DeliveryRunPayload { RecordKeys = keys, Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, null, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRunAccepted(runId, RunStatuses.Queued));
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ReadRecordAsync(
        Guid flowId, Guid key, DeliveryReadRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        if (InvalidVersion(request) is { } invalid)
        {
            return invalid;
        }

        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["deliveryKey"] = key.ToString("D") };
        WithVersion(arguments, request);
        return await DirectOperationRunner.RunAsync(db, config, direct, flow, ReadRecordOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>A version to read at is a positive whole number; the storage service numbers versions from one.</summary>
    internal static ProblemHttpResult? InvalidVersion(DeliveryReadRequest? request)
        => request?.Version is { } version && version <= 0 ? Invalid($"{version.ToString(CultureInfo.InvariantCulture)} is not a record version: a positive whole number.") : null;

    internal static void WithVersion(Dictionary<string, string> arguments, DeliveryReadRequest? request)
    {
        if (request?.Version is { } version)
        {
            arguments["version"] = version.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// One record of a flow rendered in this process (<see cref="DirectOperationRunner"/>) as a delivery would render it, with
    /// nothing sent: the scope's first record, or the one the request's key names. The key and the parameter values are
    /// checked before the preview runs, so a request the preview would refuse is a 400 rather than a preview that fails;
    /// whether the key names a row is the preview's to find, in the flow's own ingestion tables, and is an answer rather
    /// than a failure.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> PreviewAsync(
        Guid pipelineId, DeliveryPreviewRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request?.Key?.Trim() is { Length: > 0 } key)
        {
            if (key.Length > ComputeTaskPayload.MaxArgumentLength)
            {
                return Invalid($"The key is {key.Length} characters long; a key a preview reads is at most {ComputeTaskPayload.MaxArgumentLength}.");
            }

            if (key.Any(char.IsControl))
            {
                return Invalid("The key holds a control character (a tab, a line break); name one record, on one line.");
            }

            arguments["key"] = key;
        }

        var values = request?.Values ?? new Dictionary<string, string>(StringComparer.Ordinal);
        if (ParameterProblem(flow.Flow, values) is { } refused)
        {
            return Invalid(refused);
        }

        if (values.Count > 0)
        {
            var json = RunParameters.ValuesToJson(values)!;
            if (json.Length > ComputeTaskPayload.MaxArgumentLength)
            {
                return Invalid($"The parameter values are {json.Length} characters as JSON; a preview carries at most {ComputeTaskPayload.MaxArgumentLength}.");
            }

            arguments["values"] = json;
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, PreviewRecordOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The values each parameter of an interface's scope predicate can take, read in this process
    /// (<see cref="DirectOperationRunner"/>) from the column <c>source.record.scope</c> binds it to in the flow's own record
    /// table: what a page offers for a scope's value rather than having it typed. Nothing is written.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ScopeValuesAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents,
        IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, ScopeValuesOperation.OperationName, new Dictionary<string, string>(StringComparer.Ordinal), user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Why the parameter values cannot read the flow's scope, or null when they can: a name the flow does not declare, or a
    /// required parameter without a default that is given no value.
    /// </summary>
    internal static string? ParameterProblem(FlowDefinition flow, IReadOnlyDictionary<string, string> values)
    {
        var undeclared = values.Keys.Where(name => !flow.Parameters.ContainsKey(name)).ToList();
        if (undeclared.Count > 0)
        {
            var declared = flow.Parameters.Count == 0 ? "it declares none" : $"it declares {string.Join(", ", flow.Parameters.Keys)}";
            return $"The flow declares no parameter {string.Join(", ", undeclared.Select(n => $"'{n}'"))}; {declared}.";
        }

        var missing = flow.Parameters
            .Where(p => p.Value.Required && p.Value.Default is null && (!values.TryGetValue(p.Key, out var given) || string.IsNullOrWhiteSpace(given)))
            .Select(p => p.Key)
            .ToList();
        return missing.Count == 0 ? null : $"The flow's scope needs a value for {string.Join(", ", missing)}, which it declares required and gives no default.";
    }

    /// <summary>
    /// The record a record page shows, rendered in this process (<see cref="DirectOperationRunner"/>) from its current source
    /// row as a delivery would render it now, with nothing sent: what the page compares with what OSDU holds. The row is read
    /// in the scope the record was last planned under, as the source row read reads it.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> PreviewRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["key"] = key.ToString("D") };
        if (record.LastSubmissionId is { } lastId
            && await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) is { ParametersJson.Length: > 2 } last)
        {
            if (last.ParametersJson.Length > ComputeTaskPayload.MaxArgumentLength)
            {
                return TypedResults.Problem(
                    detail: $"The scope the record was last planned under is {last.ParametersJson.Length} characters as JSON, more than the {ComputeTaskPayload.MaxArgumentLength} an operation's argument carries; preview it from the flow's Preview tab with its values.",
                    statusCode: StatusCodes.Status409Conflict, title: "Scope too large to preview");
            }

            arguments["values"] = last.ParametersJson;
        }

        return await DirectOperationRunner.RunAsync(db, config, direct, flow, PreviewRecordOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>The longest OSDU id a read takes; storage ids are far shorter, and a longer text is not one.</summary>
    private const int MaxTargetIdLength = 1024;

    /// <summary>
    /// One OSDU record read in this process (<see cref="DirectOperationRunner"/>) through a flow's route and credentials, by
    /// its id: a record a document refers to, which the ledger may never have delivered. The id may carry a version or the
    /// trailing colon of a reference; the read is of the record, at its latest version. Nothing is written.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ReadTargetAsync(
        Guid pipelineId, DeliveryReadRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        if (TargetProblem(request, out var asked) is { } invalid)
        {
            return invalid;
        }

        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["targetId"] = TargetId.WithoutVersion(asked) };
        WithVersion(arguments, request);
        return await DirectOperationRunner.RunAsync(db, config, direct, flow, ReadRecordOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Why a read by OSDU id cannot be asked (no id, one too long, text that is no record id, a version that is no version),
    /// or null with the id as asked in <paramref name="asked"/>. Every read of a record by its id checks it this way.
    /// </summary>
    internal static ProblemHttpResult? TargetProblem(DeliveryReadRequest? request, out string asked)
    {
        asked = request?.TargetId?.Trim() ?? string.Empty;
        if (asked.Length == 0)
        {
            return Invalid("Name the OSDU id to read.");
        }

        if (asked.Length > MaxTargetIdLength)
        {
            return Invalid($"The id is {asked.Length} characters long; an OSDU id is at most {MaxTargetIdLength}.");
        }

        if (!TargetId.IsRecordReference(asked) || asked.Any(char.IsControl))
        {
            return Invalid($"'{asked}' is not an OSDU record id: a partition, an entity type such as master-data--Wellbore, and a unique part, separated by colons.");
        }

        return InvalidVersion(request);
    }

    internal static ProblemHttpResult Invalid(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");

    /// <summary>
    /// One record's removal. It is the many-record path with a selection of one, so a single delete and a bulk
    /// delete are the same remove run, the same ledger writes and the same result shape.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRemovalAccepted>, ProblemHttpResult>> DeleteRecordAsync(
        Guid flowId, Guid key, DeliveryRemovalRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, IRunDispatcher dispatcher,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        if (!RemovalScopes.TryParse(request?.Scope, out var scope))
        {
            return BadScope(request?.Scope);
        }

        var purgeLedger = request?.PurgeLedger == true;
        if (PurgeRefused(scope, purgeLedger) is { } refused)
        {
            return refused;
        }

        return await EnqueueRemovalAsync(db, dispatcher, flow, scope, purgeLedger, [key], null, 1, request?.Pool, user, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A removal of the records an operator selected, or of every record their listing matches. The filter form
    /// resolves on the node at the moment the removal runs; what is checked here is that the count the operator was
    /// shown is still the count the filter yields, so a set that changed underneath them stops the removal instead
    /// of quietly widening it.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRemovalAccepted>, ProblemHttpResult>> RemoveRecordsAsync(
        Guid pipelineId, DeliveryRemovalRequest request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger,
        IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        if (request is null || !RemovalScopes.TryParse(request.Scope, out var scope))
        {
            return BadScope(request?.Scope);
        }

        if (request.Keys is { Count: > 0 })
        {
            if (request.Filter is not null)
            {
                return TypedResults.Problem(
                    detail: "A removal names its records either by keys or by filter, not both.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
            }

            if (request.Keys.Count > RemovalLimits.MaxSelection)
            {
                return TypedResults.Problem(
                    detail: $"A removal takes at most {RemovalLimits.MaxSelection} records at a time; {request.Keys.Count} were selected.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Too many records");
            }

            return await EnqueueRemovalAsync(db, dispatcher, flow, scope, request.PurgeLedger, request.Keys, null, request.Keys.Count, request.Pool, user, ct).ConfigureAwait(false);
        }

        if (request.Filter is null)
        {
            return TypedResults.Problem(
                detail: "A removal needs either keys or a filter; the request carries neither.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var (query, invalid) = BuildQuery(request.Filter);
        if (query is null)
        {
            return invalid!;
        }

        BoundedCount matched;
        try
        {
            matched = await ledger.CountAsync(flow.FlowId, query, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false);
        }
        catch (RecordQueryTooBroadException ex)
        {
            return TooBroad(ex.Message);
        }

        if (!matched.Exact || matched.Count > RemovalLimits.MaxSelection)
        {
            return TypedResults.Problem(
                detail: $"The filter matches more than {RemovalLimits.MaxSelection} records as far as the listing counts; a removal takes at most {RemovalLimits.MaxSelection} at a time. Narrow the filter and remove in parts.",
                statusCode: StatusCodes.Status409Conflict, title: "Too many records");
        }

        if (matched.Count == 0)
        {
            return TypedResults.Problem(
                detail: "The filter matches no records, so there is nothing to remove.",
                statusCode: StatusCodes.Status409Conflict, title: "Nothing selected");
        }

        if (request.Expected is { } expected && expected != matched.Count)
        {
            return TypedResults.Problem(
                detail: $"The filter matched {expected} records when it was shown and matches {matched.Count} now. Nothing was removed; check the list and confirm again.",
                statusCode: StatusCodes.Status409Conflict, title: "The selection changed");
        }

        return await EnqueueRemovalAsync(db, dispatcher, flow, scope, request.PurgeLedger, [], query, matched.Count, request.Pool, user, ct).ConfigureAwait(false);
    }

    /// <summary>What a removal would take away, and from where: the confirmation's contents, computed not guessed.</summary>
    private static async Task<Results<Ok<DeliveryRemovalPreview>, ProblemHttpResult>> PreviewRemovalAsync(
        Guid pipelineId, DeliveryRemovalRequest request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        if (request is null || !RemovalScopes.TryParse(request.Scope, out var scope))
        {
            return BadScope(request?.Scope);
        }

        // A record is given its OSDU id when it is planned, so an id proves nothing about what OSDU holds. What the
        // operator needs to know is how many of the selection were ever actually delivered.
        int records;
        int neverDelivered;
        int removed;
        bool capped;
        if (request.Keys is { Count: > 0 })
        {
            var found = await ledger.GetRecordsAsync(flow.FlowId, request.Keys.Select(k => new DeliveryKey(k)), ct).ConfigureAwait(false);
            records = request.Keys.Count;
            neverDelivered = records - found.Values.Count(r => r.LastDeliveredUtc is not null);
            removed = found.Values.Count(r => r.Status == RecordStatus.Deleted);
            capped = records > RemovalLimits.MaxSelection;
        }
        else
        {
            if (request.Filter is null)
            {
                return TypedResults.Problem(
                    detail: "A removal preview needs either keys or a filter; the request carries neither.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
            }

            var (query, invalid) = BuildQuery(request.Filter);
            if (query is null)
            {
                return invalid!;
            }

            try
            {
                // Counted as far as one removal takes and one more, so a selection larger than a removal is named as such.
                var counted = await ledger.CountAsync(flow.FlowId, query, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false);
                var never = await ledger.CountAsync(flow.FlowId, query with { EverDelivered = false }, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false);
                capped = !counted.Exact || counted.Count > RemovalLimits.MaxSelection;
                records = Math.Min(counted.Count, RemovalLimits.MaxSelection);
                neverDelivered = Math.Min(never.Count, records);

                // A listing of another state holds no removed record; any other is counted for its removed ones.
                removed = query.Status is null or RecordStatus.Deleted
                    ? Math.Min((await ledger.CountAsync(flow.FlowId, query with { Status = RecordStatus.Deleted }, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false)).Count, records)
                    : 0;
            }
            catch (RecordQueryTooBroadException ex)
            {
                return TooBroad(ex.Message);
            }
        }

        return TypedResults.Ok(new DeliveryRemovalPreview(
            RemovalScopes.Wire(scope), records, Math.Max(0, records - neverDelivered), neverDelivered, capped, await ToTargetDtoAsync(osdu, flow, ct).ConfigureAwait(false), removed));
    }

    /// <summary>A record listing outside the ledger's bounds (<see cref="RecordListing"/>): the detail says how to narrow it.</summary>
    private static ProblemHttpResult TooBroad(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Listing too broad");

    private static ProblemHttpResult BadScope(string? scope)
        => TypedResults.Problem(
            detail: $"'{scope ?? "(none)"}' is not a removal scope. Use record (reversible), previous (the version before the latest written back as current, reversible), history (earlier versions only) or everything (the record and every version).",
            statusCode: StatusCodes.Status400BadRequest, title: "Invalid removal scope");

    /// <summary>A removal that deletes from the ledger what it does not take out of OSDU: refused, since only a record OSDU no longer holds may go.</summary>
    private static ProblemHttpResult? PurgeRefused(RemovalChoice scope, bool purgeLedger)
        => purgeLedger && scope is not (RemovalChoice.Record or RemovalChoice.Everything)
            ? TypedResults.Problem(
                detail: $"A record is deleted from the ledger only with a removal that takes it out of OSDU (record or everything), not with {RemovalScopes.Wire(scope)}.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid removal")
            : null;

    /// <summary>
    /// Queues the remove run of <paramref name="flow"/>'s interface (osdu/docs/reference/concepts/removal-and-reversal.md,
    /// Removing records from OSDU) under the caller's name: a run of the pipeline, so it is listed with the flow's runs, never
    /// runs beside another run of the pipeline, and carries its records in its payload, by <paramref name="keys"/> or by the
    /// listing <paramref name="filter"/> the run resolves when it removes them. <paramref name="records"/> is how many the
    /// request names, or the filter matched when it was counted.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRemovalAccepted>, ProblemHttpResult>> EnqueueRemovalAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, FlowContext flow, RemovalChoice scope, bool purgeLedger, IReadOnlyList<Guid> keys, RecordQuery? filter,
        int records, string? pool, ClaimsPrincipal user, CancellationToken ct)
    {
        if (PurgeRefused(scope, purgeLedger) is { } refused)
        {
            return refused;
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Remove,
            Payload = new DeliveryRunPayload
            {
                Interface = flow.Flow.Interface,
                RecordKeys = keys,
                Filter = filter,
                Scope = scope,
                PurgeLedger = purgeLedger,
            }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted(
            $"/api/v1/runs/{runId}",
            new DeliveryRemovalAccepted(runId, RunStatuses.Queued, RemovalScopes.Wire(scope), records, purgeLedger));
    }

    /// <summary>
    /// The flow's target as the GUI names it before a removal. The data partition is read from the flow's headers,
    /// which is where OSDU takes it; no other header is reported, since a header can carry a credential reference. On the
    /// ddms route the endpoints and the collection are those serving the kind the flow's mapping renders, as the
    /// repository sync read it.
    /// </summary>
    internal static async Task<DeliveryTargetDto> ToTargetDtoAsync(OsduDbContext osdu, FlowContext flow, CancellationToken ct)
    {
        var target = flow.Flow.Target;
        target.Headers.TryGetValue("data-partition-id", out var partition);
        string? kind = null;
        string? ddms = null;
        if (DeliveryProtocols.ReachesDdms(target.Protocol))
        {
            kind = await MappingKindAsync(osdu, flow, ct).ConfigureAwait(false);
            ddms = DdmsDescription(flow.Flow, kind);
        }

        var paths = RemovalEndpoints.Of(flow.Flow, string.IsNullOrEmpty(kind) ? null : kind);
        var route = SqlFlow.Delivery.Engine.Reversals.ReversalRoute.Of(flow.Flow, string.IsNullOrEmpty(kind) ? null : kind);
        return new DeliveryTargetDto(
            flow.Pipeline.Id, flow.Pipeline.Name, target.Endpoint, partition, DeliveryProtocols.Name(target.Protocol),
            target.Auth.Type.ToString(), paths.Record, paths.History, paths.Everything, flow.Flow.Interface, ddms, paths.RecordMethod,
            route.Restore, route.RestoreRefusal);
    }

    /// <summary>The kind the flow's mapping renders, as the repository sync read it; null while the sync has read no valid mapping of that reference.</summary>
    internal static Task<string?> MappingKindAsync(OsduDbContext osdu, FlowContext flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(flow);
        var reference = flow.Flow.Render.Mapping;
        return osdu.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == flow.Pipeline.RepoId && m.Reference == reference && m.Status == "valid")
            .Select(m => m.Kind)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Where a ddms-route flow's records go, as a sentence: the collection and the DDMS serving the kind its mapping renders.</summary>
    private static string DdmsDescription(FlowDefinition flow, string? kind)
        => string.IsNullOrEmpty(kind)
            ? "The DDMS collection is chosen by the kind the flow's mapping renders, which the repository sync has not read yet."
            : DdmsRouting.Of(flow).Explain(kind);

    /// <summary>
    /// The operator's "Probe target", asked from the GUI, the API or the MCP tool: one interface's target probed in this
    /// process and recorded in the audit trail under the caller (<see cref="TargetProbes.RunAsync"/>, the path the scheduled
    /// probe takes too), answering with what the probe found.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ProbeAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, DeliveryConfigStore config, DirectOperations direct, EngineContext engine, TimeProvider clock, ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        return await TargetProbes.RunAsync(db, ledger, config, direct, engine, clock, loggers, flow, RequestActor.Label(user), ct).ConfigureAwait(false);
    }

    // ---- Plumbing ------------------------------------------------------------------------------------------------

    /// <summary>A delivery pipeline, the source its catalog copy declares, and the interface of it a request acts on.</summary>
    internal sealed record FlowContext(CatalogPipeline Pipeline, SourceDefinition Source, FlowDefinition Flow)
    {
        public Guid FlowId => Flow.Id;
    }

    /// <summary>A delivery pipeline and the source its catalog copy declares.</summary>
    internal sealed record SourceContext(CatalogPipeline Pipeline, SourceDefinition Source);

    /// <summary>
    /// The delivery pipeline, and the interface of it the request names, or the problem to answer with. A flow in the single
    /// form, and a source of one interface, need no name; a source of several has to be told which.
    /// </summary>
    internal static async Task<(FlowContext? Flow, ProblemHttpResult? Problem)> ResolveAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, Guid pipelineId, string? interfaceName, string? partition,
        CancellationToken ct)
    {
        var (source, problem) = await ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        return source is null
            ? (null, problem)
            : Select(source, interfaceName, partition, await RegistryForAsync(partitions, source.Source, partition, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// <see cref="ResolveAsync"/> for a request that reads what a ledger kept: a partition since taken out of the registry
    /// still binds a flow that follows the registry while the ledger's directory keeps a ledger of the flow in it
    /// (<see cref="BindKeptAsync"/>), so that partition's records, submissions and audit trail stay readable.
    /// </summary>
    internal static async Task<(FlowContext? Flow, ProblemHttpResult? Problem)> ResolveKeptAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, Guid pipelineId, string? interfaceName, string? partition,
        CancellationToken ct)
    {
        var (unbound, problem) = await ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (unbound is null)
        {
            return (null, problem);
        }

        var registry = await RegistryForAsync(partitions, unbound.Source, partition, ct).ConfigureAwait(false);
        var (source, unpartitioned) = await BindKeptAsync(ledger, unbound, partition, registry, ct).ConfigureAwait(false);
        return unpartitioned is not null ? (null, unpartitioned) : Select(source, interfaceName, source.Source.Partition, registry: null);
    }

    /// <summary>
    /// <see cref="Bind"/> for a request that reads what a ledger kept (osdu/docs/reference/concepts/partitions.md, In the
    /// GUI and the API). A partition taken out of the registry leaves its ledgers in place, and the directory still names
    /// them: a flow that follows the registry is bound to such a partition when the directory keeps a ledger of one of the
    /// flow's interfaces in it, so what was delivered there can still be read. Nothing is queued in it this way; a run
    /// settles its partition from the registry, and asks for the partition to be registered again.
    /// </summary>
    internal static async Task<(SourceContext Bound, ProblemHttpResult? Problem)> BindKeptAsync(
        ILedger ledger, SourceContext unbound, string? partition, RegisteredPartitions registry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (Bind(unbound, partition, registry, out var bound) is not { } problem)
        {
            return (bound, null);
        }

        if (await KeptPartitionAsync(ledger, unbound.Source, partition, registry, ct).ConfigureAwait(false) is not { } kept)
        {
            return (unbound, problem);
        }

        return Bind(unbound, kept, registry: null, out bound) is { } unbindable ? (unbound, unbindable) : (bound, null);
    }

    /// <summary>
    /// The partition <paramref name="partition"/> names, as the ledger's directory spells it, when it is no longer registered
    /// and the directory keeps a ledger of one of <paramref name="source"/>'s interfaces in it; null otherwise.
    /// </summary>
    private static async Task<string?> KeptPartitionAsync(ILedger ledger, SourceDefinition source, string? partition, RegisteredPartitions registry, CancellationToken ct)
    {
        var name = partition?.Trim();
        if (!source.FollowsRegistry || string.IsNullOrEmpty(name) || !CacheScope.IsPartitionId(name) || registry.Find(name) is not null)
        {
            return null;
        }

        foreach (var candidate in source.ForPartition(name).Interfaces)
        {
            if (await ledger.GetLedgerAsync(candidate.Id, ct).ConfigureAwait(false) is { Partition: { } kept } && string.Equals(kept, name, StringComparison.OrdinalIgnoreCase))
            {
                return kept;
            }
        }

        return null;
    }

    /// <summary>
    /// The registry, read when settling <paramref name="partition"/> for <paramref name="source"/> needs it
    /// (<see cref="SourceDefinition.NeedsRegistry"/>), and none otherwise: a partition the flow hard-codes is settled from
    /// its document alone.
    /// </summary>
    internal static async Task<RegisteredPartitions> RegistryForAsync(IPartitionRegistry partitions, SourceDefinition source, string? partition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(source);
        return source.NeedsRegistry(partition) ? await partitions.ReadAsync(ct).ConfigureAwait(false) : RegisteredPartitions.None;
    }

    /// <summary>The delivery pipeline and its parsed source, or the problem to answer with.</summary>
    internal static async Task<(SourceContext? Source, ProblemHttpResult? Problem)> ResolveSourceAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, Guid pipelineId, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return (null, NotFound("pipeline", pipelineId));
        }

        return Parse(documents, pipeline);
    }

    internal static (SourceContext? Source, ProblemHttpResult? Problem) Parse(DeliveryDocumentLoader documents, CatalogPipeline pipeline)
    {
        if (!string.Equals(pipeline.Kind, FlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return (null, TypedResults.Problem(
                detail: $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a delivery flow.",
                statusCode: StatusCodes.Status409Conflict, title: "Not a delivery flow"));
        }

        try
        {
            return (new SourceContext(pipeline, documents.ParseSource(pipeline.Yaml, pipeline.RelativePath)), null);
        }
        catch (FlowValidationException ex)
        {
            return (null, TypedResults.Problem(
                detail: $"The catalog's copy of '{pipeline.Name}' does not parse: {ex.Message} Re-sync the repository.",
                statusCode: StatusCodes.Status409Conflict, title: "Flow document invalid"));
        }
    }

    /// <summary>
    /// The source bound to the partition a request names (osdu/docs/reference/concepts/partitions.md, In the GUI and the
    /// API), settled as a run settles it (<see cref="SourceDefinition.Resolve"/>): the one named, or when none is, the
    /// registry's default among the flow's partitions, else its only one; refused for a flow whose partition is its
    /// header's. With no <paramref name="registry"/>, the partition is one a ledger already keeps, and the flow is bound to
    /// it as it is, so a record of a partition since taken out of the registry can still be read and acted on. Null when
    /// bound, otherwise the problem to answer with.
    /// </summary>
    internal static ProblemHttpResult? Bind(SourceContext source, string? partition, RegisteredPartitions? registry, out SourceContext bound)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            bound = source with { Source = registry is null ? source.Source.ForPartition(partition) : source.Source.Resolve(partition, registry) };
            return null;
        }
        catch (DeliveryException ex)
        {
            bound = source;
            return TypedResults.Problem(
                detail: ex.Message + (source.Source.Partitioned ? " Name it with ?partition=." : string.Empty),
                statusCode: StatusCodes.Status400BadRequest,
                title: source.Source.Partitioned && string.IsNullOrWhiteSpace(partition) ? "Partition required" : "No such partition");
        }
    }

    /// <summary>
    /// The interface of a source a request names, bound to the partition it names (<see cref="Bind"/>), as a flow context, or
    /// the problem to answer with.
    /// </summary>
    internal static (FlowContext? Flow, ProblemHttpResult? Problem) Select(SourceContext unbound, string? interfaceName, string? partition, RegisteredPartitions? registry)
    {
        if (Bind(unbound, partition, registry, out var source) is { } unpartitioned)
        {
            return (null, unpartitioned);
        }

        if (interfaceName is null && source.Source.Interfaces.Count > 1)
        {
            return (null, TypedResults.Problem(
                detail: $"Pipeline '{source.Pipeline.Name}' delivers {source.Source.Interfaces.Count} interfaces ({string.Join(", ", source.Source.Names)}); name the one this request is about with ?interface=.",
                statusCode: StatusCodes.Status400BadRequest, title: "Interface required"));
        }

        return Pick(source.Source, interfaceName, out var flow) is { } problem
            ? (null, problem)
            : (new FlowContext(source.Pipeline, source.Source, flow!), null);
    }

    /// <summary>The interface <paramref name="interfaceName"/> names (the only one when it names none), or the problem to answer with.</summary>
    private static ProblemHttpResult? Pick(SourceDefinition source, string? interfaceName, out FlowDefinition? flow)
    {
        try
        {
            flow = source.Interface(interfaceName);
            return null;
        }
        catch (DeliveryException ex)
        {
            flow = null;
            return TypedResults.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound, title: "No such interface");
        }
    }

    /// <summary>The interface a ledger identity's pipeline names, or null for a flow in the single form.</summary>
    internal static string? NamedInterface(LedgerPipeline? found) => found is { Interface.Length: > 0 } ? found.Interface : null;

    /// <summary>
    /// The partition a request about a ledger's flow names: the ledger's, for a flow that names or follows its partitions;
    /// null for one whose partition is its header's, which a request cannot name.
    /// </summary>
    private static string? NamedPartition(LedgerPipeline? found) => found is { Bound: true, Partition.Length: > 0 } ? found.Partition : null;

    /// <summary>The partition a ledger is kept under, whichever way its flow names it; null for a ledger not yet placed.</summary>
    private static string? KeptPartition(LedgerPipeline? found) => found is { Partition.Length: > 0 } ? found.Partition : null;

    /// <summary>
    /// One flow's record and the pipeline behind it: the delivery flow whose name yields the flow id. An intervention acts
    /// on that flow's record only, never on another flow's record of the same source row.
    /// </summary>
    private static async Task<(FlowContext? Flow, RecordState? Record, ProblemHttpResult? Problem)> ResolveForRecordAsync(
        CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, Guid flowId, Guid key, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return (null, null, RecordNotFound(flowId, key));
        }

        var (flow, problem) = await ResolveForLedgerAsync(db, osdu, documents, record.FlowId, "The record's flow", ct).ConfigureAwait(false);
        return flow is null ? (null, null, problem) : (flow, record, null);
    }

    /// <summary>
    /// The flow that keeps ledger <paramref name="ledgerId"/>, bound as its runs bind it: the pipeline declaring it, the
    /// interface the ledger is kept under, and the partition it delivers to; or the problem to answer with, naming the flow
    /// as <paramref name="whose"/> says.
    /// </summary>
    internal static async Task<(FlowContext? Flow, ProblemHttpResult? Problem)> ResolveForLedgerAsync(
        CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, Guid ledgerId, string whose, CancellationToken ct)
    {
        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, ledgerId, ct).ConfigureAwait(false);
        if (found is null)
        {
            return (null, TypedResults.Problem(
                detail: $"{whose} is no longer in any synced repository, so nothing can act on it. Restore the flow document and sync.",
                statusCode: StatusCodes.Status409Conflict, title: "Flow not in catalog"));
        }

        var (source, problem) = Parse(documents, found.Pipeline);
        if (source is null)
        {
            return (null, problem);
        }

        // The ledger's partition binds a flow that works in partitions; a flow whose partition is its header's is bound to
        // none, its ledger's partition being the one its header resolved to.
        var partition = source.Source.Partitioned && found.Partition.Length > 0 ? found.Partition : null;
        return Select(source, found.Interface.Length == 0 ? null : found.Interface, partition, registry: null);
    }

    /// <summary>
    /// The runs that worked on one submission, newest first: the run that registered it, and every run whose kind
    /// arguments name it (a re-run, a drain, a fan-out member). A run carries the submission in its payload rather than in
    /// a column of its own, so the payload is what finds them. That match is scoped to the submission's own flow, which is
    /// the only flow those runs belong to; without the scope the search reads every run the estate has ever recorded to
    /// answer one submission's page.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> SubmissionRunsAsync(
        CatalogDbContext db, Guid submissionId, Guid? runId, Guid? pipelineId, CancellationToken ct)
    {
        var named = submissionId.ToString("D");
        return await db.Runs.AsNoTracking()
            .Where(r => (runId != null && r.RunId == runId)
                || (pipelineId != null && r.PipelineId == pipelineId && r.Payload != null && r.Payload.Contains(named)))
            .OrderByDescending(r => r.EnqueuedUtc)
            .Select(r => r.RunId)
            .Take(MaxSubmissionRuns)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues a run of <paramref name="flow"/>. A flow bound to a partition is run in it: the run is told the partition of the
    /// ledger the request was about, since one that names none would settle to the registry's default and act on another
    /// partition's ledger.
    /// </summary>
    internal static Task<Guid> EnqueueRunAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, FlowContext flow, RunParameters parameters, string? pool, ClaimsPrincipal user, CancellationToken ct)
    {
        var bound = flow.Flow.Partition is { } partition
            ? parameters with { Values = new Dictionary<string, string>(parameters.Values, StringComparer.Ordinal) { [PartitionNames.RunValue] = partition } }
            : parameters;
        return dispatcher.EnqueueAsync(
            db,
            new RunEnqueueRequest(
                flow.Pipeline.RepoId, flow.Pipeline.Name, flow.Pipeline.Kind, string.IsNullOrWhiteSpace(pool) ? null : pool.Trim(), null, bound,
                RequestedBy: RequestActor.Of(user)),
            ct);
    }

    /// <summary>A failure as it may be stored and shown: the message with every resolved secret redacted out of it.</summary>
    internal static string Redacted(Exception ex) => SecretHygiene.RedactedMessage(ex);

    /// <summary>
    /// Queues an operation for a node (a removal, a value check) under the caller's name: the flow file's location rides
    /// along, every credential stays a reference the node resolves.
    /// </summary>
    internal static async Task<Results<Accepted<ComputeTaskAccepted>, ProblemHttpResult>> EnqueueOperationAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, FlowContext flow, string operation, IReadOnlyDictionary<string, string> arguments,
        ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var requestedBy = RequestActor.Of(user);
        var (payload, problem) = await OperationPayloadAsync(db, flow, operation, arguments, RequestActor.Label(user), ct).ConfigureAwait(false);
        if (payload is null)
        {
            return problem!;
        }

        var taskId = await dispatcher.EnqueueComputeTaskAsync(
            db,
            new ComputeTaskEnqueueRequest(operation, flow.Pipeline.Name, FlowDefinition.FlowTypeName, payload.ToJson(), RequestedBy: requestedBy),
            ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/compute/tasks/{taskId}", new ComputeTaskAccepted(taskId, RunStatuses.Queued));
    }

    /// <summary>
    /// What an operation of <paramref name="flow"/> is given, wherever it runs: the flow file's location as the catalog knows
    /// it, who asked, the repository whose central configuration it resolves with, and the interface and partition it acts
    /// through, beside its own <paramref name="arguments"/>. An operation queued for a node (a value check, a removal) and one
    /// run in this process (<see cref="DirectOperationRunner"/>) are given the same, so they act alike.
    /// </summary>
    internal static async Task<(ComputeTaskPayload? Payload, ProblemHttpResult? Problem)> OperationPayloadAsync(
        CatalogDbContext db, FlowContext flow, string operation, IReadOnlyDictionary<string, string> arguments, string actor, CancellationToken ct)
    {
        var rootPath = await db.Repos.AsNoTracking().Where(r => r.Id == flow.Pipeline.RepoId).Select(r => r.RootPath).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return (null, TypedResults.Problem(
                detail: "The flow's repository has no synced root path, so the flow file cannot be located for this operation.",
                statusCode: StatusCodes.Status409Conflict, title: "Repository not materialized"));
        }

        var taskArguments = new Dictionary<string, string>(arguments, StringComparer.Ordinal)
        {
            ["repoRoot"] = rootPath,
            ["relativePath"] = flow.Pipeline.RelativePath,
            ["actor"] = actor,
            // The repository whose central configuration the operation resolves its references with, as a run does.
            [ConfiguredRunDispatcher.RepoArgument] = flow.Pipeline.RepoId.ToString("D"),
        };
        if (flow.Flow.Interface is { } interfaceName)
        {
            // The operation acts through this interface of the source, and through no other.
            taskArguments["interface"] = interfaceName;
        }

        if (flow.Flow.Partition is { } partition)
        {
            // And in this partition of a flow that names its partitions: its target, its ledger and its configuration.
            taskArguments[DeliveryOperation.PartitionArgument] = partition;
        }

        var payload = new ComputeTaskPayload
        {
            Operation = operation,
            SourceRef = flow.Pipeline.Name,
            Arguments = taskArguments,
        };

        // The operation is one of the module's own, so the payload is checked as a registered operation's: the platform's
        // own list of operations does not name it.
        payload.Validate([operation]);
        return (payload, null);
    }

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(detail: $"No {resource} '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult RecordNotFound(Guid flowId, Guid key)
        => TypedResults.Problem(detail: $"No record '{key}' in the ledger of flow '{flowId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    /// <summary>
    /// A record deleted from the ledger after it was removed from OSDU: not found, saying who deleted it and when, with the
    /// line the ledger keeps of it under <c>purged</c>.
    /// </summary>
    private static ProblemHttpResult RecordPurged(PurgedRecordState purged)
    {
        var dto = new DeliveryPurgedRecordDto(
            purged.FlowId, purged.DeliveryKey.Value, purged.SourceKey, purged.Label, purged.TargetId, purged.LastVersion, purged.Attempts, purged.ActivityId,
            purged.PurgedBy, purged.PurgedUtc, purged.PlanRequestedUtc);
        var osdu = purged.TargetId is { } id
            ? string.Create(CultureInfo.InvariantCulture, $" (OSDU id {id}{(purged.LastVersion is { } v ? $", last version {v}" : string.Empty)})")
            : string.Empty;
        var next = purged.PlanRequestedUtc is null ? string.Empty : " The flow's next run reads its row again and delivers it as a new record.";
        return TypedResults.Problem(
            detail: string.Create(
                CultureInfo.InvariantCulture,
                $"Record {purged.Label ?? purged.SourceKey}{osdu} was deleted from the ledger by {purged.PurgedBy} at {purged.PurgedUtc:u}, after it was removed from OSDU; its {purged.Attempts} attempt(s) went with it.{next}"),
            statusCode: StatusCodes.Status404NotFound,
            title: "Deleted from the ledger",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["purged"] = dto });
    }

    private static DeliverySubmissionDto ToDto(SubmissionState s) => new(
        s.SubmissionId, s.FlowId, s.FlowName, s.MappingReference, s.RenderContext, s.ParametersJson, s.RecordCount,
        s.Status.ToString().ToLowerInvariant(), s.ReceivedUtc, s.StartedUtc, s.CompletedUtc, s.Planned, s.SkippedUnchanged, s.AwaitingApproval, s.SkippedStale, s.UnchangedAtPush, s.Blocked,
        s.Delivered, s.Held, s.Failed, s.Error, s.WorkLocation, s.BatchCount, s.Slices,
        s.Kind, s.Untracked, s.SourceConnection, s.SourceObject, s.WindowFromUtc, s.WindowToUtc,
        ParseJsonOrNull(s.SourceWindowJson), s.RunId, s.Waiting, s.Partition);

    private static DeliveryWorkBatchDto ToDto(WorkBatchState b) => new(
        b.SubmissionId, b.Index, b.Location, b.RecordCount, b.Status.ToString().ToLowerInvariant(), b.LeaseOwner, b.LeaseExpiresUtc, b.RunId,
        b.CreatedUtc, b.StartedUtc, b.CompletedUtc, b.Delivered, b.Held, b.Failed, b.Retrying, b.Error, b.Waiting);

    internal static DeliveryRecordDto ToDto(RecordState r) => new(
        r.DeliveryKey.Value, r.FlowId, r.SourceKey, r.Label, r.MappingName, r.RenderContext, r.SourceFingerprint, r.SourceModifiedUtc, r.MetadataHash, r.PayloadHash, r.PayloadModifiedUtc,
        r.TargetId, r.TargetVersion, r.Status.ToString().ToLowerInvariant(), r.LastDeliveredUtc, r.LastVerifiedUtc,
        r.LastVerifyOutcome?.ToString().ToLowerInvariant(), r.LeaseOwner, r.LeaseExpiresUtc, r.LastSubmissionId, r.AttemptCount, r.NextAttemptUtc,
        r.LastError, r.PendingDocumentRef is not null, r.PendingMetadata, r.PendingPayload, r.PendingPayloadLocation, r.Blocked, r.CreatedUtc, r.UpdatedUtc,
        r.PendingDocumentRef, r.WorkBatch, ParseJsonOrNull(r.TargetStateJson), ParseJsonOrNull(r.PendingStepJson),
        // Where the record came from. Traceability is the product: a delivered record says which ingestion file and row
        // it was built from, and a record with work waiting says which file and row that work will be built from.
        r.SourceFileName, r.SourceRowNumber, r.SourceUpdatedUtc,
        r.PendingSourceFileName, r.PendingSourceRowNumber, r.PendingSourceUpdatedUtc,
        r.SourceKeyJson, r.PlanRequestedUtc,
        // What the pending document refers to, and, while the record waits, the record it waits for.
        r.WaitingFor, r.PendingReferences.Select(p => new DeliveryRecordReferenceDto(p.Id, p.Property)).ToList(),
        // When the row first reached the ingestion table, which later changes never move.
        r.SourceInsertedUtc,
        // The partition the record's ledger is kept under, whichever way its flow names it.
        r.Partition,
        // While it is blocked, held or failed: the issue keeping it so, which every record refused for the same reason shares.
        r.ProblemHash is { } problem ? ProblemSignature.Format(problem) : null,
        // The parent a record of a linked template reads, and the versions of it its documents were rendered from.
        r.ParentId, r.ParentVersion, r.PendingParentVersion,
        // While it is held because OSDU already holds a record at the id its key makes: that id and version when the hold kept
        // them, whether an adoption may be asked of it, and when one was.
        r.OccupiedTargetId, r.OccupiedTargetVersion, OccupiedHolds.Adoptable(r), r.AdoptRequestedUtc);

    private static DeliveryAttemptDto ToDto(AttemptRecord a) => new(
        a.AttemptId, a.DeliveryKey.Value, a.SubmissionId, a.RunId, a.Worker, a.StartedUtc, a.CompletedUtc, a.Outcome.ToString().ToLowerInvariant(),
        a.Phase, a.MetadataHash, a.PayloadHash, a.TargetVersion, a.Error, ParseJsonOrNull(a.ResultJson), a.WorkBatch,
        // The origin of the document this try sent, which is what makes a past attempt reconstructible from the ledger
        // alone even after the record has moved on to a newer row.
        a.SourceFileName, a.SourceRowNumber, a.SourceUpdatedUtc, a.SourceDeletedUtc,
        // For a record reading its parent: the version of the parent the document this try sent was rendered from.
        a.ParentVersion);

    private static DeliveryActivityDto ToDto(ActivityRecord a) => new(
        a.ActivityId, a.FlowId, a.FlowName, a.Kind, a.Actor, a.StartedUtc, a.CompletedUtc, a.Outcome, a.ParametersJson, a.SubmissionId,
        a.DeliveryKey, a.RunId, a.Summary, a.Log, a.Partition, a.Idle);

    private static DeliveryMappingDto ToDto(DeliveryMapping m) => new(
        m.Id, m.RepoId, m.Reference, m.Name, m.Version, m.Kind, m.RelativePath, m.ContentHash, m.Status, m.Message, ParseJson(m.SummaryJson),
        m.FirstSeenUtc, m.LastSeenUtc, m.TemplateVersion, m.TemplatePath);

    private static DeliveryUpdateTagDto ToDto(UpdateTag t, IReadOnlyDictionary<Guid, LedgerPipeline> pipelines) => new(
        t.TagId, t.Kind, t.Scope, t.TypeName, t.ItemId, t.Path, t.Change, t.OldValue, t.NewValue, t.FromVersion, t.ToVersion, t.Mode,
        t.Status, t.Describe(), t.AffectedRecords, t.Processed, t.Remaining, t.Waiting,
        t.WaitingByFlow
            .Select(f => pipelines.TryGetValue(f.Key, out var found)
                ? new DeliveryUpdateTagFlowDto(f.Key, found.Pipeline.Id, NamedFlow(found), f.Value)
                : new DeliveryUpdateTagFlowDto(f.Key, null, null, f.Value))
            .OrderByDescending(f => f.Records)
            .ThenBy(f => f.FlowName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        t.DetectedUtc, t.DecidedUtc, t.DecidedBy, t.StartedUtc, t.CompletedUtc);

    /// <summary>A flow as a change names it: the pipeline, and the interface when the ledger is one of several.</summary>
    private static string NamedFlow(LedgerPipeline found)
        => NamedInterface(found) is { } name ? $"{found.Pipeline.Name} ({name})" : found.Pipeline.Name;

    private static JsonElement? ParseJsonOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement ParseJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }
}
