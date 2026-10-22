using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>The type whose referring types the explorer asks for, and whether the partition's schemas are read again first.</summary>
/// <param name="Type">A type (<c>reference-data--UnitOfMeasure</c>), or a kind naming one, whose version does not matter.</param>
/// <param name="Refresh">True to start a pass that reads the partition's schemas again: those added since, and those in development.</param>
public sealed record DeliveryExplorerReferencesRequest(string? Type, bool Refresh = false);

public static partial class DeliveryExplorerEndpoints
{
    private static void MapReferenceEndpoints(RouteGroupBuilder delivery)
        => delivery.MapPost("/explorer/referenced-by", ReferencedByAsync).WithName("ExploreDeliveryOsduReferencedBy");

    /// <summary>
    /// The types whose records name records of a type, as the partition's Schema service declares them
    /// (osdu/docs/reference/concepts/explorer.md, Referenced by), answered from the schemas read once and kept. The
    /// first ask of a partition starts the pass that reads them, and answers with where it stands until it ends.
    /// </summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ReferencedByAsync(
        DeliveryExplorerReferencesRequest? body, [FromQuery] string? partition, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct,
        ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (ExplorerReferences.EntityTypeOf(body?.Type) is not { } entityType)
        {
            return DeliveryEndpoints.Invalid(string.IsNullOrWhiteSpace(body?.Type)
                ? "Name the type whose referring types to list."
                : ExplorerReferences.TypeProblem(body.Type));
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { [ExploreOperation.TypeArgument] = entityType };
        if (body!.Refresh)
        {
            arguments[ExploreOperation.RefreshArgument] = "true";
        }

        return await ExploreAsync(ExploreOperation.ReferencedByAction, arguments, partition, partitions, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }
}
