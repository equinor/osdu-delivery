using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// Where a flow's records actually live: the endpoint and data partition every removal in the GUI names before it
/// runs, with the exact call each scope makes. The endpoint is reported as the flow declares it, secret references
/// and all, because that reference is what identifies the environment; no credential or header value is exposed.
/// <c>Ddms</c> says, for a flow on the ddms route, which collection of which DDMS its records go to, and
/// <c>RecordMethod</c> which method the record scope calls <c>RecordPath</c> with. <c>PreviousPath</c> is the calls the
/// previous scope makes (the version before the latest read, then written back as a new version), and
/// <c>PreviousRefusal</c> why the route cannot write an earlier version back, null when it can. <c>RestrictPath</c> is the
/// call a restriction of the records' access makes, and a lift (osdu/docs/reference/concepts/access-restriction.md), and
/// <c>RestrictRefusal</c> why the route cannot restrict them, null when it can.
/// </summary>
public sealed record DeliveryTargetDto(
    Guid PipelineId, string FlowName, string Endpoint, string? DataPartition, string Protocol, string AuthType,
    string RecordPath, string HistoryPath, string EverythingPath, string? Interface = null, string? Ddms = null, string RecordMethod = "POST",
    string PreviousPath = "", string? PreviousRefusal = null, string RestrictPath = "", string? RestrictRefusal = null);

/// <summary>
/// A removal of one or many records. <c>scope</c> is record, previous, history or everything, or restrict or unrestrict their
/// access (osdu/docs/reference/concepts/access-restriction.md), restrict with the registered <c>restriction</c> to restrict
/// them under, by name. The records are named either
/// by <c>keys</c> or by <c>filter</c> (every record the listing matches), never both. <c>expected</c> is the count
/// the operator was shown: when it no longer matches what the filter resolves to, the removal is refused rather
/// than run against a set that changed underneath them. <c>purgeLedger</c>, with record or everything, also deletes each
/// record OSDU answered for from the ledger, keeping one line of it. <c>pool</c> names the pool the remove run is queued on.
/// </summary>
public sealed record DeliveryRemovalRequest(
    string? Scope, IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected, bool PurgeLedger = false, string? Pool = null, string? Restriction = null);

/// <summary>
/// A removal was queued as a remove run of the flow (osdu/docs/reference/concepts/removal-and-reversal.md, Removing records
/// from OSDU): the run to watch, how many records it will act on, and whether it deletes them from the ledger too.
/// </summary>
public sealed record DeliveryRemovalAccepted(Guid RunId, string Status, string Scope, int Records, bool PurgeLedger = false, string? Restriction = null);

/// <summary>
/// What a removal would act on, for the confirmation the operator sees before asking for it. <c>Removed</c> counts the
/// records of the selection the ledger marks removed from OSDU already: those deleting from the ledger alone reaches.
/// <c>Restricted</c> counts those whose access is restricted: those a lift reaches, and a restriction passes over.
/// </summary>
public sealed record DeliveryRemovalPreview(
    string Scope, int Records, int InOsdu, int NeverDelivered, bool Capped, DeliveryTargetDto Target, int Removed = 0, int Restricted = 0);

/// <summary>
/// Removing a flow's records from OSDU (osdu/docs/reference/concepts/removal-and-reversal.md), or restricting their
/// access (osdu/docs/reference/concepts/access-restriction.md): the flow's target, which every removal names before it
/// runs, what a removal would act on, the remove run it queues, and which registered restrictions the flow can use.
/// </summary>
public static partial class DeliveryEndpoints
{
    private static async Task<Results<Ok<DeliveryTargetDto>, ProblemHttpResult>> GetTargetAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, IPartitionRegistry partitions, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        return flow is null ? problem! : TypedResults.Ok(await ToTargetDtoAsync(osdu, flow, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// One record's removal. It is the many-record path with a selection of one, so a single delete and a bulk
    /// delete are the same remove run, the same ledger writes and the same result shape.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRemovalAccepted>, ProblemHttpResult>> DeleteRecordAsync(
        Guid flowId, Guid key, DeliveryRemovalRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedgerRecords ledger, IRunDispatcher dispatcher,
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

        return await EnqueueRemovalAsync(db, osdu, dispatcher, flow, scope, purgeLedger, request?.Restriction, [key], null, 1, request?.Pool, user, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A removal of the records an operator selected, or of every record their listing matches. The filter form
    /// resolves on the node at the moment the removal runs; what is checked here is that the count the operator was
    /// shown is still the count the filter yields, so a set that changed underneath them stops the removal instead
    /// of quietly widening it.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRemovalAccepted>, ProblemHttpResult>> RemoveRecordsAsync(
        Guid pipelineId, DeliveryRemovalRequest request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedgerQueries ledger,
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

            return await EnqueueRemovalAsync(db, osdu, dispatcher, flow, scope, request.PurgeLedger, request.Restriction, request.Keys, null, request.Keys.Count, request.Pool, user, ct).ConfigureAwait(false);
        }

        if (request.Filter is null)
        {
            return TypedResults.Problem(
                detail: "A removal needs either keys or a filter; the request carries neither.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var (query, invalid) = await BuildQueryAsync(db, request.Filter, ct).ConfigureAwait(false);
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

        return await EnqueueRemovalAsync(db, osdu, dispatcher, flow, scope, request.PurgeLedger, request.Restriction, [], query, matched.Count, request.Pool, user, ct).ConfigureAwait(false);
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
        int restricted;
        bool capped;
        if (request.Keys is { Count: > 0 })
        {
            var found = await ledger.GetRecordsAsync(flow.FlowId, request.Keys.Select(k => new DeliveryKey(k)), ct).ConfigureAwait(false);
            records = request.Keys.Count;
            neverDelivered = records - found.Values.Count(r => r.LastDeliveredUtc is not null);
            removed = found.Values.Count(r => r.Status == RecordStatus.Deleted);
            restricted = found.Values.Count(r => r.Restricted);
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

            var (query, invalid) = await BuildQueryAsync(db, request.Filter, ct).ConfigureAwait(false);
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
                restricted = Math.Min((await ledger.CountAsync(flow.FlowId, query with { Restricted = true }, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false)).Count, records);
            }
            catch (RecordQueryTooBroadException ex)
            {
                return TooBroad(ex.Message);
            }
        }

        return TypedResults.Ok(new DeliveryRemovalPreview(
            RemovalScopes.Wire(scope), records, Math.Max(0, records - neverDelivered), neverDelivered, capped, await ToTargetDtoAsync(osdu, flow, ct).ConfigureAwait(false), removed, restricted));
    }

    private static ProblemHttpResult BadScope(string? scope)
        => TypedResults.Problem(
            detail: $"'{scope ?? "(none)"}' is not a removal scope. Use record (reversible), previous (the version before the latest written back as current, reversible), history (earlier versions only), everything (the record and every version), restrict (its access, under a registered restriction, reversible) or unrestrict (its access given back).",
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
        CatalogDbContext db, OsduDbContext osdu, IRunDispatcher dispatcher, FlowContext flow, RemovalChoice scope, bool purgeLedger, string? restriction,
        IReadOnlyList<Guid> keys, RecordQuery? filter, int records, string? pool, ClaimsPrincipal user, CancellationToken ct)
    {
        if (PurgeRefused(scope, purgeLedger) is { } refused)
        {
            return refused;
        }

        restriction = string.IsNullOrWhiteSpace(restriction) ? null : restriction.Trim();
        if (await RestrictionRefusedAsync(osdu, flow, scope, restriction, ct).ConfigureAwait(false) is { } unrestricted)
        {
            return unrestricted;
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
                Restriction = restriction,
            }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted(
            $"/api/v1/runs/{runId}",
            new DeliveryRemovalAccepted(runId, RunStatuses.Queued, RemovalScopes.Wire(scope), records, purgeLedger, restriction));
    }

    /// <summary>
    /// A restriction that names no registered restriction, or one that is not the flow's partition's, or a removal of another
    /// scope that names one: refused before a run is queued (osdu/docs/reference/concepts/access-restriction.md). The run checks
    /// it again on the node, with whether the flow's identity could lift it.
    /// </summary>
    private static async Task<ProblemHttpResult?> RestrictionRefusedAsync(OsduDbContext osdu, FlowContext flow, RemovalChoice scope, string? restriction, CancellationToken ct)
    {
        if (scope != RemovalChoice.Restrict)
        {
            return restriction is null
                ? null
                : TypedResults.Problem(
                    detail: $"Only a removal of scope restrict names an access restriction, not one of {RemovalScopes.Wire(scope)}.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid removal");
        }

        if (restriction is null)
        {
            return TypedResults.Problem(
                detail: "A restriction names the registered access restriction whose groups the records' ACL becomes (restriction).",
                statusCode: StatusCodes.Status400BadRequest, title: "No restriction named");
        }

        // What the flow's route, and what it runs beside it, cannot restrict is refused here, as the removal dialog refuses it.
        var kind = DeliveryProtocols.ReachesDdms(flow.Flow.Target.Protocol) ? await MappingKindAsync(osdu, flow, ct).ConfigureAwait(false) : null;
        if (SqlFlow.Delivery.Engine.Restrictions.RestrictionRoute.Of(flow.Flow, string.IsNullOrEmpty(kind) ? null : kind).Refusal is { } cannot)
        {
            return TypedResults.Problem(
                detail: $"'{flow.Flow.Label}' cannot restrict its records' access: {cannot}. Nothing was queued.",
                statusCode: StatusCodes.Status400BadRequest, title: "Cannot restrict");
        }

        // A flow bound to a partition names it; one whose partition is its header's names it there, unless as a reference the
        // node resolves, in which case the node finds the restriction in the partition it runs in.
        var partition = DeliveryDestination.BoundPartition(flow.Flow);
        if (partition is null || partition.Contains("${", StringComparison.Ordinal))
        {
            return null;
        }

        var registered = await osdu.DeliveryAccessRestrictions.AsNoTracking()
            .AnyAsync(a => a.Partition == partition && a.Name == restriction, ct).ConfigureAwait(false);
        return registered
            ? null
            : TypedResults.Problem(
                detail: $"No access restriction named '{restriction}' is registered for partition '{partition}'. Register it under Access restrictions, or name one that is.",
                statusCode: StatusCodes.Status400BadRequest, title: "Unknown access restriction");
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
        var restriction = SqlFlow.Delivery.Engine.Restrictions.RestrictionRoute.Of(flow.Flow, string.IsNullOrEmpty(kind) ? null : kind);
        return new DeliveryTargetDto(
            flow.Pipeline.Id, flow.Pipeline.Name, target.Endpoint, partition, DeliveryProtocols.Name(target.Protocol),
            target.Auth.Type.ToString(), paths.Record, paths.History, paths.Everything, flow.Flow.Interface, ddms, paths.RecordMethod,
            route.Restore, route.RestoreRefusal, restriction.Call, restriction.Refusal);
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
    /// Whether the flow can restrict its records under each access restriction registered for its partition, and lift it
    /// again (osdu/docs/reference/concepts/access-restriction.md, Checked before it is queued): run in this process under the
    /// flow's own credentials (<see cref="CheckAccessRestrictionsOperation"/>), asking OSDU for the flow's own groups alone, so
    /// the removal dialog refuses what the run would refuse, in its words. A partition that registers none is answered with
    /// none, asking OSDU nothing.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> CheckAccessRestrictionsAsync(
        Guid pipelineId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, OsduDbContext osdu,
        DeliveryDocumentLoader documents, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var bound = DeliveryDestination.BoundPartition(flow.Flow);
        if (string.IsNullOrWhiteSpace(bound) || bound.Contains("${", StringComparison.Ordinal))
        {
            return TypedResults.Problem(
                detail: $"'{flow.Flow.Label}' names its partition as a reference a run resolves, so which access restrictions it can use is known once a run settles it.",
                statusCode: StatusCodes.Status422UnprocessableEntity, title: "Partition not known");
        }

        var ids = await osdu.DeliveryAccessRestrictions.AsNoTracking()
            .Where(a => a.Partition == bound)
            .OrderBy(a => a.Name)
            .Select(a => a.RestrictionId)
            .Take(CheckAccessRestrictionsOperation.MaxRestrictions)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return TypedResults.Content(
                JsonSerializer.Serialize(new { flow = flow.Flow.Label, restrictions = Array.Empty<object>() }, JsonSerializerOptions.Web),
                "application/json");
        }

        return await DirectOperationRunner.RunAsync(
            db, config, direct, flow, CheckAccessRestrictionsOperation.OperationName,
            new Dictionary<string, string>(StringComparer.Ordinal) { [CheckAccessRestrictionsOperation.RestrictionsArgument] = string.Join(',', ids) },
            user, loggers, ct).ConfigureAwait(false);
    }
}
