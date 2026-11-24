using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A compute task was queued for a node: an operation that runs long enough to be watched rather than waited on (a removal,
/// a value check). A probe, a read-back, a preview and a read of the ingestion tables run in process instead.
/// </summary>
public sealed record ComputeTaskAccepted(Guid TaskId, string Status);

/// <summary>
/// The delivery ledger's API: what each flow delivered (records, their history, their submissions), the audit trail
/// of runs and interventions, the mappings and snapshots the repositories hold, and the interventions themselves
/// (release, redeliver, verify, read back, delete). Reads are answered from the catalog's indexed ledger tables;
/// interventions either act on the ledger directly under the caller's name, or queue a run or a compute task for a node.
/// What a person waits on (a probe, a read-back of a record from OSDU, a preview, a read of the ingestion tables) runs in
/// this process through <see cref="DirectOperationRunner"/>, under the flow's own credentials resolved with the central
/// configuration, exactly as a node would run it.
/// </summary>
public static partial class DeliveryEndpoints
{
    public static RouteGroupBuilder MapDeliveryReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var delivery = group.MapGroup("/delivery").WithTags("Delivery");

        // The central configuration the control plane supplies to the runs it queues.
        DeliveryConfigEndpoints.Map(delivery);

        // The partitions the catalog knows, which every OSDU page is read in.
        DeliveryPartitionEndpoints.Map(delivery);
        DeliveryAccessRestrictionEndpoints.Map(delivery);

        // The compute the nodes spent, and how it divides over repositories, project folders and flows: an admin's.
        DeliveryComputeUsageEndpoints.Map(delivery);

        // Where each cached type of a partition comes from, who reads it, and what its last refreshes did.
        DeliveryCacheStreams.Map(delivery);

        // How the explorer reaches each partition of OSDU.
        delivery.MapDeliveryExplorerReadEndpoints();
        delivery.MapGet("/flows/{pipelineId:guid}/stats", GetStatsAsync).WithName("GetDeliveryFlowStats");
        delivery.MapGet("/flows/{pipelineId:guid}/interfaces", ListInterfacesAsync).WithName("ListDeliveryInterfaces");
        delivery.MapGet("/flows/{pipelineId:guid}/records", ListRecordsAsync).WithName("ListDeliveryRecords");
        delivery.MapGet("/flows/{pipelineId:guid}/records/export", ExportRecordsAsync).WithName("ExportDeliveryRecords");
        delivery.MapGet("/records", LookupRecordsAsync).WithName("LookupDeliveryRecords");
        delivery.MapGet("/records/export", ExportRecordLookupAsync).WithName("ExportDeliveryRecordLookup");
        delivery.MapGet("/records/flows", ListRecordFlowsAsync).WithName("ListDeliveryRecordFlows");
        delivery.MapGet("/flows/{pipelineId:guid}/target", GetTargetAsync).WithName("GetDeliveryTarget");
        delivery.MapGet("/flows/{pipelineId:guid}/submissions", ListSubmissionsAsync).WithName("ListDeliverySubmissions");
        delivery.MapGet("/flows/{pipelineId:guid}/retrievals", ListRetrievalsAsync).WithName("ListDeliveryRetrievals");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}", GetRecordAsync).WithName("GetDeliveryRecord");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/attempts", ListRecordAttemptsAsync).WithName("ListDeliveryRecordAttempts");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/chain", GetRecordChainAsync).WithName("GetDeliveryRecordChain");
        delivery.MapGet("/records/{flowId:guid}/{key:guid}/activities", ListRecordActivitiesAsync).WithName("ListDeliveryRecordActivities");
        delivery.MapGet("/runs/lookup", DeliveryRunLookup.LookupAsync).WithName("LookupDeliveryRun");
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

        // Download flows: a flow's downloads in a partition, their runs, and their records with the file each is written to.
        DeliveryDownloadEndpoints.MapReads(delivery);

        // The missing documents: flows no repository declares any more whose ledgers hold anything, and who can take them over.
        DeliveryOrphanedLedgerEndpoints.MapReads(delivery);
        delivery.MapGet("/caches", ListCachesAsync).WithName("ListDeliveryCaches");
        delivery.MapGet("/cache/items", ListCachedItemsAsync).WithName("ListDeliveryCachedItems");
        delivery.MapGet("/cache/items/sql", WriteCachedItemsSqlAsync).WithName("WriteDeliveryCachedItemsSql");
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

        // The explorer's reads of what OSDU holds, each run in this process through the partition's own connection; none needs a flow.
        delivery.MapDeliveryExplorerOperateEndpoints();

        // The records ride in the body, so the route reads a larger body than the default and no larger than that.
        delivery.MapPost("/flows/{pipelineId:guid}/release", ReleaseFlowAsync).WithName("ReleaseDeliveryFlowRecords");
        delivery.MapPost("/flows/{pipelineId:guid}/redeliver", RedeliverFlowAsync).WithName("RedeliverDeliveryFlowRecords");
        delivery.MapPost("/flows/{pipelineId:guid}/adopt", AdoptFlowAsync).WithName("AdoptDeliveryFlowRecords");
        delivery.MapPost("/flows/{pipelineId:guid}/rerender", RerenderFlowAsync).WithName("BringDeliveryFlowRecordsUpToDate");
        delivery.MapPost("/flows/{pipelineId:guid}/rerender/preview", PreviewRerenderAsync).WithName("PreviewBringingDeliveryFlowRecordsUpToDate");
        delivery.MapPost("/flows/{pipelineId:guid}/probe", ProbeAsync).WithName("ProbeDeliveryTarget");
        delivery.MapPost("/flows/{pipelineId:guid}/access-check", CheckAccessRestrictionsAsync).WithName("CheckDeliveryAccessRestrictions");
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

        // The staged rows a check of an assertion run read, as the T-SQL a mapping's source query is written as.
        DeliveryAssertionEndpoints.MapWrites(delivery);
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
            // The repository whose central configuration the operation resolves its references with, as a run does, and whose
            // synced commit is checked out again where the folder above is not on the host (a redeployed one, a node).
            [DeliveryOperation.RepositoryArgument] = flow.Pipeline.RepoId.ToString("D"),
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

    internal static ProblemHttpResult Invalid(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");

    /// <summary>A record listing outside the ledger's bounds (<see cref="RecordListing"/>): the detail says how to narrow it.</summary>
    private static ProblemHttpResult TooBroad(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Listing too broad");

    /// <summary>
    /// The listing filter of a request that may name a run, with the parts the run split its delivery into when it spread one
    /// over several nodes: each part writes its records' attempts under its own id, so a run's records are its own attempts'
    /// and its parts' (osdu/docs/reference/concepts/submissions.md, Many nodes). The parts are the platform's runs, read here
    /// because the ledger keeps none of its own.
    /// </summary>
    private static async Task<(RecordQuery? Query, ProblemHttpResult? Problem)> BuildQueryAsync(CatalogDbContext db, DeliveryRecordFilterDto filter, CancellationToken ct)
    {
        var (query, invalid) = BuildQuery(filter);
        if (query?.RunId is not { } runId)
        {
            return (query, invalid);
        }

        // A part names the run that split the delivery as its root (a seek of the runs' FanOutRoot index). A run of a source
        // splits each interface it delivers, at most FlowParallelism.MaxNodes parts each, so every part is read: the parts of
        // the other interfaces hold no attempt of this ledger and narrow nothing.
        var parts = await db.Runs.AsNoTracking()
            .Where(r => r.FanOutRoot == runId)
            .Select(r => r.RunId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return (query with { RunParts = parts }, null);
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

        long? missing = null;
        if (!string.IsNullOrWhiteSpace(filter.Missing))
        {
            if (!ProblemSignature.TryParse(filter.Missing.Trim().ToLowerInvariant(), out var parsed))
            {
                return (null, TypedResults.Problem(
                    detail: $"missing '{filter.Missing}' is not a set of missing values: it is the {ProblemSignature.TextLength} characters a record restricted until complete gives as its incompleteGroup.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"));
            }

            missing = parsed;
        }

        Planning.TriggerKinds? deliveredFor = null;
        if (!string.IsNullOrWhiteSpace(filter.Trigger))
        {
            var kind = Planning.ReasonKinds.Flag(filter.Trigger.Trim().ToLowerInvariant());
            if (kind == Planning.TriggerKinds.None)
            {
                return (null, TypedResults.Problem(
                    detail: $"trigger '{filter.Trigger}' is not a kind of trigger: it is one of {string.Join(", ", Planning.ReasonKinds.All.Select(k => k.Kind))}.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"));
            }

            if (filter.DeliveredBy is null && filter.RunId is null)
            {
                return (null, TypedResults.Problem(
                    detail: "trigger narrows what a submission delivered (deliveredBy) or a run delivered (runId): name one of them with it.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request"));
            }

            deliveredFor = kind;
        }

        return (new RecordQuery
        {
            Status = status,
            Problem = issue,
            Search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim(),
            Mode = string.Equals(filter.Mode, "contains", StringComparison.OrdinalIgnoreCase) ? SearchMode.Contains : SearchMode.Prefix,
            SubmissionId = filter.SubmissionId,
            DeliveredBySubmissionId = filter.DeliveredBy,
            DeliveredFor = deliveredFor,
            RunId = filter.RunId,
            Drifted = filter.Drifted,
            StepHeld = filter.StepHeld,
            Restricted = filter.Restricted,
            Incomplete = filter.Incomplete || missing is not null,
            Missing = missing,
            AccessRestricted = filter.AccessRestricted,
        }, null);
    }

    internal static DeliverySubmissionDto ToDto(SubmissionState s) => new(
        s.SubmissionId, s.FlowId, s.FlowName, s.MappingReference, s.RenderContext, s.ParametersJson, s.RecordCount,
        s.Status.ToString().ToLowerInvariant(), s.ReceivedUtc, s.StartedUtc, s.CompletedUtc, s.Planned, s.SkippedUnchanged, s.AwaitingApproval, s.SkippedStale, s.UnchangedAtPush, s.Blocked,
        s.Delivered, s.Held, s.Failed, s.Error, s.WorkLocation, s.BatchCount, s.Slices,
        s.Kind, s.Untracked, s.SourceConnection, s.SourceObject, s.WindowFromUtc, s.WindowToUtc,
        ParseJsonOrNull(s.SourceWindowJson), s.RunId, s.Waiting, s.Partition);

    private static DeliveryAttemptDto ToDto(AttemptRecord a)
    {
        var (result, reason) = AttemptReason(a);
        return new(
            a.AttemptId, a.DeliveryKey.Value, a.SubmissionId, a.RunId, a.Worker, a.StartedUtc, a.CompletedUtc, a.Outcome.ToString().ToLowerInvariant(),
            a.Phase, a.MetadataHash, a.PayloadHash, a.TargetVersion, a.Error, result, a.WorkBatch,
            // The origin of the document this try sent, which is what makes a past attempt reconstructible from the ledger
            // alone even after the record has moved on to a newer row.
            a.SourceFileName, a.SourceRowNumber, a.SourceUpdatedUtc, a.SourceDeletedUtc,
            // For a record reading its parent: the version of the parent the document this try sent was rendered from.
            a.ParentVersion,
            reason);
    }

    /// <summary>
    /// An attempt's result, and the reason it keeps, lifted out of the result so it is read in one place: why a delivered try
    /// was sent, or what moved to render a record found identical to what OSDU holds, marked recorded; for a delivery made
    /// before the ledger kept reasons, that it was not recorded. Null for a try that keeps none and delivered nothing.
    /// </summary>
    internal static (JsonElement? Result, JsonElement? Reason) AttemptReason(AttemptRecord attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var result = ParseJsonOrNull(attempt.ResultJson);
        var node = result is { ValueKind: JsonValueKind.Object } parsed ? System.Text.Json.Nodes.JsonObject.Create(parsed) : null;
        if (node?["reason"] is not System.Text.Json.Nodes.JsonObject kept || SqlFlow.Delivery.Planning.DeliveryReason.FromJson(kept) is null)
        {
            return attempt.Outcome == AttemptOutcome.Delivered
                ? (result, JsonSerializer.SerializeToElement(new { recorded = false, summary = "reason not recorded" }))
                : (result, null);
        }

        node.Remove("reason");
        var reason = (System.Text.Json.Nodes.JsonObject)kept.DeepClone();
        reason["recorded"] = true;
        return (JsonSerializer.SerializeToElement(node), JsonSerializer.SerializeToElement(reason));
    }

    private static DeliveryActivityDto ToDto(ActivityRecord a) => new(
        a.ActivityId, a.FlowId, a.FlowName, a.Kind, a.Actor, a.StartedUtc, a.CompletedUtc, a.Outcome, a.ParametersJson, a.SubmissionId,
        a.DeliveryKey, a.RunId, a.Summary, a.Log, a.Partition, a.Idle);

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
