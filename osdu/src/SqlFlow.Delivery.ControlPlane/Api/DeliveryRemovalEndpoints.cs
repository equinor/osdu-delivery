using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>What a removal run read, found and came to.</summary>
public sealed record DeliveryRemovalTallyDto(
    long RowsRead, long SourceKeys, long ListedKeys, long IngestionKeys, long Compared, long Detected, long AwaitingApproval, long Approved,
    long Removed, long AlreadyGone, long Settled, long KeptOut, long Referenced, long Shared, long Busy, long Failed, long CameBack, long Lifted);

/// <summary>One run of one removal of a removal flow, as the ledger of the delivery flow it wrote to keeps it.</summary>
public sealed record DeliveryRemovalRunDto(
    string Partition, long RemovalRunId, string FlowName, string Entry, Guid LedgerFlowId, string DeliveryFlow, Guid? RunId, string Operation, string Actor,
    string Status, DateTime StartedUtc, DateTime? CompletedUtc, DateTime? SourceReadUtc, DeliveryRemovalTallyDto Tally, int GuardPercent, long? GuardRecords,
    string? ApprovedBy, long? ActivityId, string? Error);

/// <summary>
/// One of a delivery ledger's deletions: a key a source removed, why and as of when, the file and row that said so, how far
/// taking it out has come, which removal found it, took it out and approved it, and the status of the ledger's record of the
/// key (null when the ledger holds none: a key only the record table held, which no run delivered).
/// </summary>
public sealed record DeliveryRemovedKeyDto(
    Guid DeliveryKey, string? SourceKeyJson, string Reason, DateTime RemovedUtc, string? FileName, long? RowNumber, string State, string? Outcome, string? Detail,
    int Tries, string RemovalFlow, string Entry, long DetectedRunId, long? DoneRunId, Guid? RunId, string? ApprovedBy, DateTime? ApprovedUtc, DateTime CreatedUtc,
    DateTime? DoneUtc, string? RecordStatus);

/// <summary>A page of a delivery ledger's deletions, the key to ask for the next page with, and how many there are in each state.</summary>
public sealed record DeliveryRemovedKeysPageDto(IReadOnlyList<DeliveryRemovedKeyDto> Keys, Guid? Next, IReadOnlyDictionary<string, long> Counts);

/// <summary>One column a removal reads a key column from: the source column, the key column it fills, and the expression over it.</summary>
public sealed record DeliveryRemovalColumnDto(string Name, string? As, string? Expr);

/// <summary>
/// Where a removal reads keys from, as its document declares it: <c>files</c> read by a SQLFlow reader of <c>Type</c> at
/// <c>Where</c>, or a <c>table</c> read through the source system <c>Type</c> names, <c>Where</c> naming the object and
/// <c>Filter</c> the rows; the columns the record key is read from (none reads each key column by its name), and for a source
/// of removed keys the column holding the moment. The connection is left out.
/// </summary>
public sealed record DeliveryRemovalSourceDto(
    string Reads, string Type, string? Where, string? Filter, IReadOnlyList<DeliveryRemovalColumnDto> Columns, DeliveryRemovalColumnDto? RemovedAt);

/// <summary>
/// One removal of a removal flow as its page shows it: the delivery flow (and interface) whose ledger it writes to, the delivery
/// flow's parameter values it compares under, what it reads, that ledger's identity in the partition (null while the delivery
/// flow has no ledger there), the deletions waiting for this removal's approval, the ledger's deletions by state, and the
/// removal's last run.
/// </summary>
public sealed record DeliveryRemovalEntryDto(
    string Name, string Flow, string? Interface, [property: System.Text.Json.Serialization.JsonConverter(typeof(SqlFlow.Core.Runs.RunValuesJsonConverter))] IReadOnlyDictionary<string, string> Values, bool KeepReferenced, DeliveryRemovalSourceDto? Keys,
    DeliveryRemovalSourceDto? Removed, Guid? DeliveryPipelineId, Guid? LedgerFlowId, long AwaitingApproval, IReadOnlyDictionary<string, long> Deletions,
    DeliveryRemovalRunDto? LastRun);

/// <summary>
/// A removal flow in one partition: its guard, the parameters its values and locations take, and every removal it declares.
/// <c>Partition</c> is null, and <c>Problem</c> says why, when the partition cannot be settled (the workbench's is not one the
/// flow removes in) or the catalog's copy does not load; the removals are then listed as declared, with nothing of a ledger.
/// </summary>
public sealed record DeliveryRemovalFlowDto(
    Guid PipelineId, Guid RepoId, string Name, string? Description, string? Partition, IReadOnlyList<string> Partitions, int GuardPercent, long? GuardRecords,
    IReadOnlyList<DeliveryParameterDto> Parameters, IReadOnlyList<DeliveryRemovalEntryDto> Removals, string? Problem = null);

/// <summary>
/// An approval of the removed keys a removal's guard held: the removal, how many keys the operator was shown waiting for
/// approval, the partition as the operator typed it to confirm, and optionally the pool the run goes to.
/// </summary>
public sealed record DeliveryRemovalApprovalRequest(string? Entry, long? Expected, string? Confirm, string? Pool = null);

/// <summary>The run an approval queued, which makes the held keys pending and takes them out.</summary>
public sealed record DeliveryRemovalApprovalAccepted(Guid RunId, string Status, string Partition, string Entry, long Expected);

/// <summary>
/// The API of removal flows (osdu/docs/reference/flow/removal.md; osdu/docs/reference/concepts/api.md, Removal flows): a removal
/// flow's page in a partition, its runs, the deletions a delivery ledger holds, and the approval of what a guard held, which
/// queues a remove run that checks it all again on the node.
/// </summary>
public static class DeliveryRemovalEndpoints
{
    /// <summary>The most rows one page of runs or deletions answers.</summary>
    public const int MaxPage = 1000;

    /// <summary>The most removal flows one listing of a partition's answers.</summary>
    public const int MaxFlows = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/removals", ListFlowsAsync).WithName("ListDeliveryRemovalFlows");
        delivery.MapGet("/removals/flows/{pipelineId:guid}", GetFlowAsync).WithName("GetDeliveryRemovalFlow");
        delivery.MapGet("/removals/flows/{pipelineId:guid}/runs", ListRunsAsync).WithName("ListDeliveryRemovalRuns");
        delivery.MapGet("/removals/{partition}/runs/{removalRunId:long}", GetRunAsync).WithName("GetDeliveryRemovalRun");
        delivery.MapGet("/removals/ledgers/{ledgerFlowId:guid}/keys", ListKeysAsync).WithName("ListDeliveryRemovedKeys");
        delivery.MapGet("/removals/ledgers/{ledgerFlowId:guid}/runs", ListLedgerRunsAsync).WithName("ListDeliveryLedgerRemovalRuns");
    }

    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/removals/flows/{pipelineId:guid}/approve", ApproveAsync).WithName("ApproveDeliveryRemovals");
    }

    /// <summary>
    /// Every active removal flow of the catalog in a partition (the one the query names, else the workbench's), by name, at most
    /// <see cref="MaxFlows"/>: each as <see cref="GetFlowAsync"/> answers it, so the Removals page lists every removal and what
    /// waits for approval in one read. A flow that does not remove in the partition is left out; one whose copy does not load is
    /// listed with its problem.
    /// </summary>
    private static async Task<JsonHttpResult<IReadOnlyList<DeliveryRemovalFlowDto>>> ListFlowsAsync(
        string? partition, HttpRequest request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == RemovalFlowDefinition.FlowTypeName && p.Active)
            .OrderBy(p => p.Name)
            .Take(MaxFlows)
            .ToListAsync(ct).ConfigureAwait(false);
        var named = WorkbenchPartition.Named(partition, request);
        var flows = new List<DeliveryRemovalFlowDto>(pipelines.Count);
        foreach (var pipeline in pipelines)
        {
            var flow = await DescribeAsync(pipeline, named, db, osdu, documents, ledger, ct).ConfigureAwait(false);
            if (flow.Partition is not null || flow.Removals.Count == 0)
            {
                flows.Add(flow);
            }
        }

        return TypedResults.Json((IReadOnlyList<DeliveryRemovalFlowDto>)flows, Json);
    }

    /// <summary>
    /// A removal flow in a partition (the one the query names, else the workbench's): what each removal reads and writes to, what
    /// waits for approval, and its last run. A partition the flow does not remove in, or a copy that does not load, is answered
    /// with the flow as declared and the problem, so its page says what is wrong rather than failing.
    /// </summary>
    private static async Task<Results<JsonHttpResult<DeliveryRemovalFlowDto>, ProblemHttpResult>> GetFlowAsync(
        Guid pipelineId, string? partition, HttpRequest request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger,
        CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null || !string.Equals(pipeline.Kind, RemovalFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No removal flow {pipelineId:D} in the catalog.");
        }

        return TypedResults.Json(await DescribeAsync(pipeline, WorkbenchPartition.Named(partition, request), db, osdu, documents, ledger, ct).ConfigureAwait(false), Json);
    }

    /// <summary>A removal flow as its page shows it in <paramref name="partition"/>, or as declared with the problem when it cannot be read there.</summary>
    private static async Task<DeliveryRemovalFlowDto> DescribeAsync(
        CatalogPipeline pipeline, string? partition, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        RemovalFlowDefinition flow;
        try
        {
            flow = documents.ParseRemoval(pipeline.Yaml, pipeline.RelativePath);
        }
        catch (FlowValidationException ex)
        {
            return new DeliveryRemovalFlowDto(pipeline.Id, pipeline.RepoId, pipeline.Name, null, null, [], RemovalGuard.DefaultMaxPercent, null, [], [],
                $"The catalog's copy of the flow does not load: {ex.Message} Fix the document and sync the repository again.");
        }

        var parameters = flow.Parameters.Select(p => new DeliveryParameterDto(p.Key, p.Value.Required, p.Value.Default, p.Value.Description)).ToList();
        var (name, refused) = await PartitionAsync(osdu, flow, partition, ct).ConfigureAwait(false);
        if (name is null)
        {
            var declared = flow.Entries.Select(e => Entry(e, null, null, 0, new Dictionary<string, long>(StringComparer.Ordinal), null)).ToList();
            return new DeliveryRemovalFlowDto(
                pipeline.Id, pipeline.RepoId, flow.Name, flow.Description, null, flow.Partitions, flow.Guard.MaxPercent, flow.Guard.MaxRecords, parameters,
                declared, refused!.ProblemDetails.Detail);
        }

        var entries = new List<DeliveryRemovalEntryDto>(flow.Entries.Count);
        foreach (var entry in flow.Entries)
        {
            var target = await LedgerOfAsync(db, osdu, pipeline.RepoId, entry, name, ct).ConfigureAwait(false);
            var deletions = target.LedgerFlowId is { } ledgerFlowId
                ? await ledger.RemovedKeyCountsAsync(ledgerFlowId, ct).ConfigureAwait(false)
                : new Dictionary<string, long>(StringComparer.Ordinal);
            var awaiting = target.LedgerFlowId is { } id ? await ledger.CountAwaitingApprovalAsync(id, flow.Name, entry.Name, ct).ConfigureAwait(false) : 0;
            var runs = await ledger.ListRemovalRunsAsync(name, flow.Name, entry.Name, 1, ct).ConfigureAwait(false);
            entries.Add(Entry(entry, target.PipelineId, target.LedgerFlowId, awaiting, deletions, runs.Count > 0 ? ToDto(runs[0]) : null));
        }

        return new DeliveryRemovalFlowDto(
            pipeline.Id, pipeline.RepoId, flow.Name, flow.Description, name, flow.Partitions, flow.Guard.MaxPercent, flow.Guard.MaxRecords, parameters, entries);
    }

    /// <summary>
    /// The runs of a removal flow in a partition (the one the query names, else the workbench's), the newest first, of one removal
    /// when <paramref name="entry"/> names it.
    /// </summary>
    private static async Task<Results<JsonHttpResult<IReadOnlyList<DeliveryRemovalRunDto>>, ProblemHttpResult>> ListRunsAsync(
        Guid pipelineId, string? partition, string? entry, int? limit, HttpRequest request, CatalogDbContext db, OsduDbContext osdu,
        DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var (pipeline, flow, problem) = await FlowAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null || flow is null)
        {
            return problem!;
        }

        var (name, refused) = await PartitionAsync(osdu, flow, WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false);
        if (name is null)
        {
            return refused!;
        }

        if (entry is not null && flow.Entry(entry) is null)
        {
            return Problem(StatusCodes.Status404NotFound, "No such removal", $"Removal flow '{flow.Name}' declares no removal named '{entry}'.");
        }

        var runs = await ledger.ListRemovalRunsAsync(name, flow.Name, entry is null ? null : flow.Entry(entry)!.Name, Page(limit, 50), ct).ConfigureAwait(false);
        return TypedResults.Json((IReadOnlyList<DeliveryRemovalRunDto>)runs.Select(ToDto).ToList(), Json);
    }

    private static async Task<Results<JsonHttpResult<DeliveryRemovalRunDto>, ProblemHttpResult>> GetRunAsync(string partition, long removalRunId, ILedger ledger, CancellationToken ct)
    {
        if (PartitionProblem(partition) is { } bad)
        {
            return bad;
        }

        var run = await ledger.GetRemovalRunAsync(partition.Trim(), removalRunId, ct).ConfigureAwait(false);
        return run is null
            ? Problem(StatusCodes.Status404NotFound, "Not found", $"No removal run {removalRunId} in partition '{partition.Trim()}'.")
            : TypedResults.Json(ToDto(run), Json);
    }

    /// <summary>
    /// A page of a delivery ledger's deletions, in key order after <paramref name="after"/>, of one state and of those one removal
    /// run found when the query names them, with how many there are in each state.
    /// </summary>
    private static async Task<Results<JsonHttpResult<DeliveryRemovedKeysPageDto>, ProblemHttpResult>> ListKeysAsync(
        Guid ledgerFlowId, string? state, long? run, Guid? after, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (state is not null && !SourceRemovals.IsState(state))
        {
            return Problem(StatusCodes.Status400BadRequest, "No such state", $"'{state}' is not a state a removed key is in; it is one of {string.Join(", ", SourceRemovals.States)}.");
        }

        var size = Page(limit, 200);
        var keys = await ledger.ListRemovedKeysAsync(ledgerFlowId, state, run, after is { } from ? new DeliveryKey(from) : null, size + 1, ct).ConfigureAwait(false);
        var counts = await ledger.RemovedKeyCountsAsync(ledgerFlowId, ct).ConfigureAwait(false);
        var page = keys.Take(size).Select(ToDto).ToList();
        return TypedResults.Json(new DeliveryRemovedKeysPageDto(page, keys.Count > size ? page[^1].DeliveryKey : null, counts), Json);
    }

    private static async Task<JsonHttpResult<IReadOnlyList<DeliveryRemovalRunDto>>> ListLedgerRunsAsync(Guid ledgerFlowId, int? limit, ILedger ledger, CancellationToken ct)
    {
        var runs = await ledger.ListRemovalRunsOfLedgerAsync(ledgerFlowId, Page(limit, 50), ct).ConfigureAwait(false);
        return TypedResults.Json((IReadOnlyList<DeliveryRemovalRunDto>)runs.Select(ToDto).ToList(), Json);
    }

    /// <summary>
    /// Queues the run that approves the removed keys a removal's guard held (osdu/docs/reference/flow/removal.md, The guard): it
    /// makes them pending, as the caller, when the ledger holds exactly as many waiting as the caller was shown, and takes them out
    /// with the rest of the ledger's pending deletions. Refused with 400 for a request that names no removal, no count, or no
    /// partition to confirm; with 404 for a removal the flow does not declare; with 409 when the delivery flow keeps no ledger in
    /// the partition, another number of keys waits than the caller was shown, or a run of the flow is queued or running. The run
    /// checks the count again on the node before it approves anything.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRemovalApprovalAccepted>, ProblemHttpResult>> ApproveAsync(
        Guid pipelineId, DeliveryRemovalApprovalRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger,
        IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Entry))
        {
            return Problem(StatusCodes.Status400BadRequest, "Removal required", "Name the removal whose held keys you approve, the count you were shown, and the partition to confirm.");
        }

        if (request.Expected is not > 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Count required",
                "Give expected, the number of removed keys you were shown waiting for approval; nothing is approved without it.");
        }

        if (string.IsNullOrWhiteSpace(request.Confirm))
        {
            return Problem(StatusCodes.Status400BadRequest, "Confirmation required", "Type the partition the removal runs in to confirm.");
        }

        var (pipeline, flow, problem) = await FlowAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null || flow is null)
        {
            return problem!;
        }

        var (partition, refused) = await PartitionAsync(osdu, flow, request.Confirm, ct).ConfigureAwait(false);
        if (partition is null)
        {
            return refused!;
        }

        if (flow.Entry(request.Entry) is not { } entry)
        {
            return Problem(StatusCodes.Status404NotFound, "No such removal", $"Removal flow '{flow.Name}' declares no removal named '{request.Entry.Trim()}'.");
        }

        var target = await LedgerOfAsync(db, osdu, pipeline.RepoId, entry, partition, ct).ConfigureAwait(false);
        if (target.LedgerFlowId is not { } ledgerFlowId)
        {
            return Problem(StatusCodes.Status409Conflict, "No ledger",
                $"Delivery flow '{entry.Flow}' keeps no ledger in partition '{partition}' that the catalog knows, so nothing of it waits for approval.");
        }

        var waiting = await ledger.CountAwaitingApprovalAsync(ledgerFlowId, flow.Name, entry.Name, ct).ConfigureAwait(false);
        if (waiting != request.Expected)
        {
            return Problem(StatusCodes.Status409Conflict, "Count changed",
                $"Removal '{entry.Name}' has {waiting} removed key(s) waiting for approval in partition '{partition}' now, and the approval was given for {request.Expected}. Nothing was queued; look at it again and approve again.");
        }

        var busy = await db.Runs.AsNoTracking()
            .Where(r => r.PipelineId == pipeline.Id && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running))
            .Select(r => (Guid?)r.RunId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (busy is { } running)
        {
            return Problem(StatusCodes.Status409Conflict, "Flow busy",
                $"Run {running:D} of '{pipeline.Name}' is queued or running; an approval waits until the flow is idle, so what it approves is what waits. Nothing was queued.");
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Remove,
            Values = new Dictionary<string, string>(StringComparer.Ordinal) { [PartitionNames.RunValue] = partition },
            Payload = new DeliveryRunPayload { Entries = [entry.Name], Approve = new RemovalApproval { Expected = request.Expected.Value }, Confirm = partition }.ToJson(),
        };
        var runId = await dispatcher.EnqueueAsync(
            db,
            new RunEnqueueRequest(
                pipeline.RepoId, pipeline.Name, pipeline.Kind, string.IsNullOrWhiteSpace(request.Pool) ? null : request.Pool.Trim(), null, parameters,
                RequestedBy: RequestActor.Of(user)),
            ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRemovalApprovalAccepted(runId, RunStatuses.Queued, partition, entry.Name, request.Expected.Value));
    }

    /// <summary>The removal flow pipeline <paramref name="pipelineId"/> names, as the catalog's copy parses, or the problem to answer with.</summary>
    private static async Task<(CatalogPipeline? Pipeline, RemovalFlowDefinition? Flow, ProblemHttpResult? Problem)> FlowAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, Guid pipelineId, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null || !string.Equals(pipeline.Kind, RemovalFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, Problem(StatusCodes.Status404NotFound, "Not found", $"No removal flow {pipelineId:D} in the catalog."));
        }

        try
        {
            return (pipeline, documents.ParseRemoval(pipeline.Yaml, pipeline.RelativePath), null);
        }
        catch (FlowValidationException ex)
        {
            return (pipeline, null, Problem(StatusCodes.Status409Conflict, "Not readable",
                $"The catalog's copy of removal flow '{pipeline.Name}' does not load, so its removals cannot be shown or approved: {ex.Message}"));
        }
    }

    /// <summary>
    /// The partition a request about a removal flow names, checked against the flow's: required when the flow names several, the
    /// flow's own when it names one, and a registered one for a flow that names none.
    /// </summary>
    private static async Task<(string? Partition, ProblemHttpResult? Problem)> PartitionAsync(OsduDbContext osdu, RemovalFlowDefinition flow, string? requested, CancellationToken ct)
    {
        var wanted = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (wanted is not null && PartitionProblem(wanted) is { } bad)
        {
            return (null, bad);
        }

        if (flow.DeclaresPartitions)
        {
            if (wanted is null)
            {
                return flow.Partitions.Count == 1
                    ? (flow.Partitions[0], null)
                    : (null, Problem(StatusCodes.Status400BadRequest, "Partition required", $"Removal flow '{flow.Name}' removes in {string.Join(", ", flow.Partitions)}; name the partition."));
            }

            var declared = flow.Partitions.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase));
            return declared is null
                ? (null, Problem(StatusCodes.Status409Conflict, "Not served", $"Removal flow '{flow.Name}' does not remove in partition '{wanted}'; it names {string.Join(", ", flow.Partitions)}."))
                : (declared, null);
        }

        var registry = await DeliveryPartitionRegistry.ReadAsync(osdu, ct).ConfigureAwait(false);
        var name = wanted ?? registry.Default;
        if (name is null)
        {
            return (null, Problem(StatusCodes.Status400BadRequest, "Partition required", $"Removal flow '{flow.Name}' removes in every registered partition, and none is the default; name the partition."));
        }

        return registry.Find(name) is { } found
            ? (found, null)
            : (null, Problem(StatusCodes.Status409Conflict, "Not registered", registry.NotRegistered(name)));
    }

    /// <summary>
    /// The delivery flow a removal names in the removal flow's repository, and the ledger its interface keeps in the partition, as
    /// the catalog's read model describes them; nulls while the catalog knows neither.
    /// </summary>
    private static async Task<(Guid? PipelineId, Guid? LedgerFlowId)> LedgerOfAsync(
        CatalogDbContext db, OsduDbContext osdu, Guid repoId, RemovalEntry entry, string partition, CancellationToken ct)
    {
        var pipelineId = await db.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && p.Name == entry.Flow && p.Kind == FlowDefinition.FlowTypeName)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var wanted = entry.Interface ?? string.Empty;
        var interfaces = await DeliveryInterfaceCatalog.OfFlowAsync(osdu, repoId, entry.Flow, partition, ct).ConfigureAwait(false);
        if (interfaces.Count == 0)
        {
            // A delivery flow whose partition is its header's describes itself under no partition.
            interfaces = await DeliveryInterfaceCatalog.OfFlowAsync(osdu, repoId, entry.Flow, null, ct).ConfigureAwait(false);
        }

        var match = interfaces.FirstOrDefault(i => string.Equals(i.Interface, wanted, StringComparison.OrdinalIgnoreCase));
        return (pipelineId, match?.LedgerFlowId);
    }

    private static DeliveryRemovalEntryDto Entry(
        RemovalEntry entry, Guid? pipelineId, Guid? ledgerFlowId, long awaiting, IReadOnlyDictionary<string, long> deletions, DeliveryRemovalRunDto? last)
        => new(entry.Name, entry.Flow, entry.Interface, entry.Values, entry.KeepReferenced, Source(entry.Keys), Source(entry.Removed), pipelineId, ledgerFlowId,
            awaiting, deletions, last);

    private static DeliveryRemovalSourceDto? Source(RemovalKeySource? source)
    {
        if (source is null)
        {
            return null;
        }

        var columns = source.Columns.Select(Column).ToList();
        var moment = source.RemovedAt is { } at ? Column(at) : null;
        return source.Table is { } table
            ? new DeliveryRemovalSourceDto("table", table.Provider.ToString().ToLowerInvariant(), table.ObjectName, table.Filter, columns, moment)
            : new DeliveryRemovalSourceDto("files", source.Reader!.Type, source.Reader.Location, null, columns, moment);
    }

    private static DeliveryRemovalColumnDto Column(RemovalKeyColumn column) => new(column.Name, column.As, column.Expr);

    internal static DeliveryRemovalRunDto ToDto(RemovalRunState run) => new(
        run.Partition, run.RemovalRunId, run.FlowName, run.Entry, run.LedgerFlowId, run.DeliveryFlow, run.RunId, run.Operation, run.Actor, run.Status,
        run.StartedUtc, run.CompletedUtc, run.SourceReadUtc,
        new DeliveryRemovalTallyDto(
            run.Tally.RowsRead, run.Tally.SourceKeys, run.Tally.ListedKeys, run.Tally.IngestionKeys, run.Tally.Compared, run.Tally.Detected, run.Tally.AwaitingApproval,
            run.Tally.Approved, run.Tally.Removed, run.Tally.AlreadyGone, run.Tally.Settled, run.Tally.KeptOut, run.Tally.Referenced, run.Tally.Shared, run.Tally.Busy,
            run.Tally.Failed, run.Tally.CameBack, run.Tally.Lifted),
        run.GuardPercent, run.GuardRecords, run.ApprovedBy, run.ActivityId, run.Error);

    internal static DeliveryRemovedKeyDto ToDto(RemovedKeyState key) => new(
        key.Key.Value, key.SourceKeyJson, key.Reason, key.RemovedUtc, key.FileName, key.RowNumber, key.State, key.Outcome, key.Detail, key.Tries, key.RemovalFlow,
        key.Entry, key.DetectedRunId, key.DoneRunId, key.RunId, key.ApprovedBy, key.ApprovedUtc, key.CreatedUtc, key.DoneUtc, key.RecordStatus);

    private static int Page(int? limit, int fallback) => Math.Clamp(limit ?? fallback, 1, MaxPage);

    private static ProblemHttpResult? PartitionProblem(string partition)
        => CacheScope.IsPartitionId(partition.Trim())
            ? null
            : Problem(StatusCodes.Status400BadRequest, "No such partition", $"'{partition}' is not a partition: a data-partition-id is letters, digits, underscore, hyphen and dot.");

    private static ProblemHttpResult Problem(int status, string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);
}
