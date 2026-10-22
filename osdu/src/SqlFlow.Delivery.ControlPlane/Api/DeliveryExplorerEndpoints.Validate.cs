using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.SearchTerms;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>A record the explorer checks against its schema.</summary>
/// <param name="TargetId">The record's id.</param>
/// <param name="Version">The version to check; its latest when left out.</param>
/// <param name="Schema"><c>osdu</c> (the default): what the partition's Schema service holds for its kind; <c>template</c>: a template a synced repository holds, read from its file.</param>
/// <param name="TemplateVersion">With <c>template</c>, the template version to check against, among those the synced repositories hold; the one the most recently synced repository holds when left out.</param>
/// <param name="Mapping">
/// The synced mapping (its id, as the Mappings page lists it) whose assertions are judged on the record
/// (osdu/docs/reference/flow/mapping-assertions.md); none when left out, except on a record page, whose check judges the
/// assertions of the record's own flow's mapping.
/// </param>
public sealed record DeliveryExplorerValidateRequest(string? TargetId, long? Version = null, string? Schema = null, string? TemplateVersion = null, Guid? Mapping = null);

/// <summary>The records a search finds, checked against their schemas: the search as the explorer sends it, how many records at most, and the schema.</summary>
/// <param name="Search">The search, as <c>/explorer/search</c> takes it; its page and grouping are not used.</param>
/// <param name="Max">The most records read and checked, 1 to 1,000; 1,000 when left out.</param>
/// <param name="Schema"><c>osdu</c> (the default) or <c>template</c>, each kind's template as the most recently synced repository holding it holds it.</param>
/// <param name="Mapping">The synced mapping whose assertions are judged on the records, by its id; none when left out.</param>
public sealed record DeliveryExplorerValidateListRequest(DeliveryExplorerSearchRequest? Search = null, int? Max = null, string? Schema = null, Guid? Mapping = null);

public static partial class DeliveryExplorerEndpoints
{
    private static void MapValidateEndpoints(RouteGroupBuilder delivery)
    {
        delivery.MapPost("/explorer/validate", ValidateAsync).WithName("ExploreDeliveryOsduValidate");
        delivery.MapPost("/explorer/validate-list", ValidateListAsync).WithName("ExploreDeliveryOsduValidateList");
        // A record page's check, beside the read by id a record page makes (/flows/{pipelineId}/osdu/read).
        delivery.MapPost("/flows/{pipelineId:guid}/osdu/validate", ValidateTargetAsync).WithName("ValidateDeliveryOsduRecord");
    }

    /// <summary>
    /// One record OSDU holds checked against the schema of its kind (osdu/docs/reference/concepts/explorer.md,
    /// Validate): the Schema service's, or a template a synced repository holds; the records it refers to are looked up in storage. Reads
    /// OSDU through the partition's own connection, as every explorer read does.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ValidateAsync(
        DeliveryExplorerValidateRequest? body, [FromQuery] string? partition, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct,
        ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (ValidateArguments(body, out var arguments) is { } invalid)
        {
            return invalid;
        }

        return await ExploreAsync(ExploreOperation.ValidateAction, arguments, partition, partitions, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One record checked as <see cref="ValidateAsync"/> checks it, read through the route and credentials of the flow named
    /// (its interface and partition as a record page names them) rather than the partition's own connection: a record page's
    /// OSDU tab checks the records it shows, which it reads through their flow. A flow whose route has no platform endpoint
    /// (dspdm, etp) keeps no record in storage to check.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ValidateTargetAsync(
        Guid pipelineId, DeliveryExplorerValidateRequest? body, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct,
        ILoggerFactory loggers, ClaimsPrincipal user, CancellationToken ct)
    {
        if (ValidateArguments(body, out var arguments) is { } invalid)
        {
            return invalid;
        }

        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        if (!KeepsStorageRecords(flow.Flow.Target.Protocol))
        {
            return TypedResults.Problem(
                detail: $"{flow.Flow.Label} delivers by the {DeliveryProtocols.Name(flow.Flow.Target.Protocol)} route, which keeps no record in OSDU's storage service to check against a schema.",
                statusCode: StatusCodes.Status409Conflict,
                title: "No record to check");
        }

        // A record page judges the assertions of the record's own flow's mapping unless the request names another.
        if (!arguments.ContainsKey(ExploreOperation.MappingArgument))
        {
            arguments[ExploreOperation.MappingArgument] = DeliveryCatalogSync.MappingId(flow.Pipeline.RepoId, flow.Flow.Render.Mapping).ToString("D");
        }

        arguments[ExploreOperation.ActionArgument] = ExploreOperation.ValidateAction;
        return await DirectOperationRunner.RunAsync(db, config, direct, flow, ExploreOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The task arguments of a check of one record (its id, version, schema and template version), or why the request
    /// cannot be asked. Every check of one record reads its request this way.
    /// </summary>
    private static ProblemHttpResult? ValidateArguments(DeliveryExplorerValidateRequest? body, out Dictionary<string, string> arguments)
    {
        arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        var read = new DeliveryReadRequest(body?.TargetId, body?.Version);
        if (DeliveryEndpoints.TargetProblem(read, out var asked) is { } invalid)
        {
            return invalid;
        }

        if (SchemaProblem(body!.Schema) is { } schemaProblem)
        {
            return DeliveryEndpoints.Invalid(schemaProblem);
        }

        if (body.TemplateVersion is { } version && (version.Trim().Length is 0 or > 64 || version.Any(char.IsControl)))
        {
            return DeliveryEndpoints.Invalid("Name the template version as the Templates page shows it.");
        }

        arguments["targetId"] = TargetId.WithoutVersion(asked);
        DeliveryEndpoints.WithVersion(arguments, read);
        if (!string.IsNullOrWhiteSpace(body.Schema))
        {
            arguments[ExploreOperation.SchemaArgument] = body.Schema.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(body.TemplateVersion))
        {
            arguments[ExploreOperation.TemplateVersionArgument] = body.TemplateVersion.Trim();
        }

        if (body.Mapping is { } mapping)
        {
            arguments[ExploreOperation.MappingArgument] = mapping.ToString("D");
        }

        return null;
    }

    /// <summary>
    /// The records a search finds, up to 1,000, checked against their schemas and counted by the rules they break, with an
    /// example of each (osdu/docs/reference/concepts/explorer.md, Validate).
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ValidateListAsync(
        DeliveryExplorerValidateListRequest? body, [FromQuery] string? partition, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user,
        OsduDbContext osdu, TimeProvider clock, IRepositoryFolders folders, CancellationToken ct)
    {
        var asked = body ?? new DeliveryExplorerValidateListRequest();
        var searched = (asked.Search ?? new DeliveryExplorerSearchRequest()) with { Offset = null, Limit = null, Facet = null };
        var (terms, failure) = await TermsOfAsync(
            searched, partition, partitions, config, request, new SearchTermDirectory(osdu, documents, clock, folders), ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        var (search, invalid) = SearchOf(searched, terms);
        if (search is null)
        {
            return DeliveryEndpoints.Invalid(invalid!);
        }

        if (asked.Max is { } max && max is < 1 or > ExplorerChecks.MaxRecords)
        {
            return DeliveryEndpoints.Invalid(string.Create(CultureInfo.InvariantCulture, $"A check reads 1 to {ExplorerChecks.MaxRecords} records; {max} is not that."));
        }

        if (SchemaProblem(asked.Schema) is { } schemaProblem)
        {
            return DeliveryEndpoints.Invalid(schemaProblem);
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExploreOperation.SearchArgument] = search.ToJson(),
            [ExploreOperation.MaxArgument] = (asked.Max ?? ExplorerChecks.MaxRecords).ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(asked.Schema))
        {
            arguments[ExploreOperation.SchemaArgument] = asked.Schema.Trim().ToLowerInvariant();
        }

        if (asked.Mapping is { } mapping)
        {
            arguments[ExploreOperation.MappingArgument] = mapping.ToString("D");
        }

        return await ExploreAsync(ExploreOperation.ValidateListAction, arguments, partition, partitions, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }

    /// <summary>Why a request's schema is not one a record is checked against, or null when it is (or is left out).</summary>
    private static string? SchemaProblem(string? schema)
        => string.IsNullOrWhiteSpace(schema) || schema.Trim().ToLowerInvariant() is "osdu" or "template"
            ? null
            : $"'{schema}' is not a schema a record is checked against: osdu (what the Schema service holds) or template (a template a synced repository holds).";
}
