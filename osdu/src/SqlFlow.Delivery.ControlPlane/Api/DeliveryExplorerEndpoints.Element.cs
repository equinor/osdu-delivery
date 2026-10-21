using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Search;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>What the explorer asks the queries of: an element of a record, by the path the record inspector names it by, and its value.</summary>
/// <param name="Kind">The record's kind.</param>
/// <param name="Path">The element's path in the record, a list's items by their place: <c>data.GeoContexts[1].GeoTypeID</c>.</param>
/// <param name="Section">Whether the element is a section (an object, a list, an item of a list) rather than a value.</param>
/// <param name="Value">The value the record holds there, for a value: a text, a number, a boolean, or null.</param>
/// <param name="Values">For a section, the values it holds at any depth, each by its own path, at most 48.</param>
public sealed record DeliveryExplorerElementRequest(
    string? Kind, string? Path, bool? Section = null, JsonElement? Value = null, IReadOnlyList<DeliveryExplorerElementLeaf?>? Values = null);

/// <summary>A value inside a section: its path, as the record inspector names it, and what the record holds there.</summary>
public sealed record DeliveryExplorerElementLeaf(string? Path, JsonElement? Value = null);

public static partial class DeliveryExplorerEndpoints
{
    private static void MapElementQueryEndpoints(RouteGroupBuilder delivery)
        => delivery.MapPost("/explorer/element-queries", ElementQueriesAsync).WithName("ExploreDeliveryOsduElementQueries");

    /// <summary>
    /// The Lucene query that finds the records holding exactly an element of a record
    /// (osdu/docs/reference/concepts/explorer.md, A record): a value, a list, an object or a nested list's item,
    /// written by how the template of the record's kind has the platform index each value it holds, or from the
    /// values where the template cannot say. Read from the templates the synced repositories hold, from their files, alone;
    /// nothing is asked of OSDU.
    /// </summary>
    private static async Task<Results<Ok<ExplorerElementAnswer>, ProblemHttpResult>> ElementQueriesAsync(
        DeliveryExplorerElementRequest? body, OsduDbContext osdu, IRepositoryFolders folders, CancellationToken ct)
    {
        if (body is null)
        {
            return DeliveryEndpoints.Invalid("Send the element's kind, path and value.");
        }

        var request = new ExplorerElementRequest
        {
            Kind = body.Kind?.Trim() ?? string.Empty,
            Path = body.Path?.Trim() ?? string.Empty,
            Section = body.Section ?? false,
            Value = body.Value is { ValueKind: JsonValueKind.Undefined } ? null : body.Value,
            Values = body.Values?
                .Select(v => new ExplorerElementLeaf(v?.Path?.Trim() ?? string.Empty, v?.Value is { ValueKind: JsonValueKind.Undefined } ? null : v?.Value))
                .ToList(),
        };
        if (request.Problem() is { } problem)
        {
            return DeliveryEndpoints.Invalid(problem);
        }

        var templates = await RepositoryTemplates.ReadAsync(osdu, folders, ct).ConfigureAwait(false);
        return TypedResults.Ok(await ExplorerElementQueries.DescribeAsync(request, templates, ct).ConfigureAwait(false));
    }
}
