using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>Whether the partition's schemas are listed again first.</summary>
public sealed record DeliveryExplorerSchemasRequest(bool Refresh = false);

/// <summary>The kind whose schema is read, <c>authority:source:entityType:major.minor.patch</c>.</summary>
public sealed record DeliveryExplorerSchemaRequest(string? Kind);

public static partial class DeliveryExplorerEndpoints
{
    private static void MapSchemaEndpoints(RouteGroupBuilder delivery)
    {
        delivery.MapPost("/explorer/schemas", SchemasAsync).WithName("ExploreDeliveryOsduSchemas");
        delivery.MapPost("/explorer/schema", SchemaAsync).WithName("ExploreDeliveryOsduSchema");
    }

    /// <summary>
    /// The kinds the partition's Schema service lists, every status and scope, the partition's own and the platform's, whether
    /// or not any record is of them (osdu/docs/reference/concepts/templates.md, Browse OSDU), from the listing the control
    /// plane keeps of them. The first ask of a partition starts the listing, and answers with where it stands until it ends.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> SchemasAsync(
        DeliveryExplorerSchemasRequest? body, [FromQuery] string? partition, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct,
        ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (body?.Refresh == true)
        {
            arguments[ExploreOperation.RefreshArgument] = "true";
        }

        return await ExploreAsync(ExploreOperation.SchemasAction, arguments, partition, partitions, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }

    /// <summary>One kind's schema as the partition's Schema service holds it, bundled, as the file a repository's template folder keeps.</summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> SchemaAsync(
        DeliveryExplorerSchemaRequest? body, [FromQuery] string? partition, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct,
        ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var kind = body?.Kind?.Trim();
        if (ExplorerSchemaFile.KindProblem(kind) is { } wrong)
        {
            return DeliveryEndpoints.Invalid(string.IsNullOrEmpty(kind) ? "Name the kind whose schema to read." : wrong);
        }

        return await ExploreAsync(
            ExploreOperation.SchemaAction, new Dictionary<string, string>(StringComparer.Ordinal) { [ExploreOperation.KindArgument] = kind! },
            partition, partitions, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }
}
