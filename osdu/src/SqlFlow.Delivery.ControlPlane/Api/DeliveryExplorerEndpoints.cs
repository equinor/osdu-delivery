using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.SearchTerms;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// How the explorer reaches a partition: the partition, whether its own connection can be made (the platform and client its
/// configuration names, <see cref="PartitionConnection"/>) and where it reaches, or why it cannot.
/// </summary>
/// <param name="Partition">The partition asked about, as the registry spells it; null when no partition was named and none is the default.</param>
/// <param name="Available">True when the partition's connection can be made.</param>
/// <param name="Endpoint">
/// The platform the connection reaches: the configured base URL, or the control plane's environment's, without the
/// credentials, query or fragment a URL can carry; a configured reference as written. Null when it is not available.
/// </param>
/// <param name="Reason">Why the connection cannot be made; null when it can.</param>
public sealed record DeliveryExplorerConnectionDto(string? Partition, bool Available, string? Endpoint, string? Reason);

/// <summary>A condition a page of the explorer narrows to, as the GUI sends it.</summary>
/// <param name="Path">The property's path from the record root.</param>
/// <param name="Index">How the platform indexes it: text (the default), keyword, number, boolean or date.</param>
/// <param name="Value">The value compared: the whole value (is, isNot), the words (contains), the start (startsWith), or a range's lower bound.</param>
/// <param name="Condition">is (the default), isNot, anyOf, contains, startsWith, range, exists or missing.</param>
/// <param name="Values">The values anyOf compares.</param>
/// <param name="To">A range's upper bound, left out of it.</param>
/// <param name="Nested">The nested array the property sits in, which the query reaches through.</param>
/// <param name="Term">
/// A search term (osdu/docs/reference/concepts/search-terms.md) in place of the property: the condition names a column
/// of a source system, its values the source's own, and the control plane turns it into the condition on the record the
/// term's route fills.
/// </param>
public sealed record DeliveryExplorerFilterDto(
    string? Path, string? Index, string? Value, string? Condition = null, IReadOnlyList<string>? Values = null, string? To = null, string? Nested = null,
    Guid? Term = null);

/// <summary>A property the explorer groups records by.</summary>
/// <param name="Path">The property's path from the record root.</param>
/// <param name="Index">How the platform indexes it: text (the default), keyword, number, boolean or date.</param>
/// <param name="Nested">The nested array the property sits in, which the grouping reaches through.</param>
public sealed record DeliveryExplorerFieldDto(string? Path, string? Index, string? Nested = null);

/// <summary>
/// What the explorer asks of OSDU: a text (read as an id, the start of one, or words; or a Lucene query with
/// <paramref name="Lucene"/>), or the id whose mentions are listed; the kind; the values narrowed to; the order; the page;
/// and the property to group by.
/// </summary>
/// <param name="Text">What the reader typed; null for every record.</param>
/// <param name="Lucene">Whether the text is a Lucene query, sent as written.</param>
/// <param name="Mentions">The id whose mentions are listed instead of a text.</param>
/// <param name="Kind">The kind, wildcards per segment; null for every kind.</param>
/// <param name="Filters">The property values every record of the page holds.</param>
/// <param name="Sort">relevance (the default), modified or created.</param>
/// <param name="Offset">Where the page starts; 0 when left out.</param>
/// <param name="Limit">How many records the page holds; 100 when left out.</param>
/// <param name="Facet">The property whose distinct values the records are grouped by.</param>
/// <param name="Columns">The properties whose values each record carries, as columns beside it.</param>
public sealed record DeliveryExplorerSearchRequest(
    string? Text = null,
    bool Lucene = false,
    string? Mentions = null,
    string? Kind = null,
    IReadOnlyList<DeliveryExplorerFilterDto>? Filters = null,
    string? Sort = null,
    int? Offset = null,
    int? Limit = null,
    DeliveryExplorerFieldDto? Facet = null,
    IReadOnlyList<string>? Columns = null);

/// <summary>The kind whose properties the explorer offers to group and narrow by.</summary>
public sealed record DeliveryExplorerFieldsRequest(string? Kind);

/// <summary>
/// The explorer (osdu/docs/reference/concepts/explorer.md): a browser of what an OSDU partition holds, read live from
/// OSDU's own search, storage and schema services. It shows what OSDU holds and nothing the delivery system keeps, and needs
/// no flow: each read (<see cref="ExploreOperation"/>) runs in this process, as the request's answer, through the
/// partition's own connection (<see cref="PartitionConnection"/>: the platform and client the control plane's configuration
/// names for the partition, or its environment), which the engine keeps open between reads. The partition is the one the
/// request names, else the workbench's, else the registry's default, and it is a registered one.
/// </summary>
/// <remarks>
/// Reading what OSDU holds with the partition's credentials is an operate action, as reading a record back is; where a
/// partition is read is anyone's to see. A record page's check of the record it shows is the one read made through a flow:
/// the record's own, as the page reads the record.
/// </remarks>
public static partial class DeliveryExplorerEndpoints
{
    public static RouteGroupBuilder MapDeliveryExplorerReadEndpoints(this RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/explorer/connection", GetConnectionAsync).WithName("GetDeliveryExplorerConnection");
        return delivery;
    }

    public static RouteGroupBuilder MapDeliveryExplorerOperateEndpoints(this RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/explorer/types", TypesAsync).WithName("ExploreDeliveryOsduTypes");
        delivery.MapPost("/explorer/search", SearchAsync).WithName("ExploreDeliveryOsduRecords");
        delivery.MapPost("/explorer/fields", FieldsAsync).WithName("ExploreDeliveryOsduFields");
        delivery.MapPost("/explorer/read", ReadAsync).WithName("ExploreDeliveryOsduRecord");
        MapElementQueryEndpoints(delivery);
        MapDimensionBuilderEndpoints(delivery);
        MapValidateEndpoints(delivery);
        MapReferenceEndpoints(delivery);
        return delivery;
    }

    private static async Task<Ok<DeliveryExplorerConnectionDto>> GetConnectionAsync(
        [FromQuery] string? partition, IPartitionRegistry partitions, DeliveryConfigStore config, HttpRequest request, CancellationToken ct)
    {
        var found = await ConnectAsync(partitions, config, WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false);
        return TypedResults.Ok(found.Configured is { } configured
            ? new DeliveryExplorerConnectionDto(found.Partition, true, PartitionConnection.EndpointShown(configured, Environment.GetEnvironmentVariable), null)
            : new DeliveryExplorerConnectionDto(found.Partition, false, null, found.Reason));
    }

    /// <summary>The kinds of the records a search finds across every kind, each with its count.</summary>
    private static Task<Results<ContentHttpResult, ProblemHttpResult>> TypesAsync(
        DeliveryExplorerSearchRequest? body, [FromQuery] string? partition, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user,
        OsduDbContext osdu, TimeProvider clock, IRepositoryFolders folders, CancellationToken ct)
        => ExploreSearchAsync(
            ExploreOperation.TypesAction, body, partition, partitions, config, direct, loggers, request, user,
            new SearchTermDirectory(osdu, documents, clock, folders), ct);

    /// <summary>One page of the records a search finds.</summary>
    private static Task<Results<ContentHttpResult, ProblemHttpResult>> SearchAsync(
        DeliveryExplorerSearchRequest? body, [FromQuery] string? partition, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user,
        OsduDbContext osdu, TimeProvider clock, IRepositoryFolders folders, CancellationToken ct)
        => ExploreSearchAsync(
            ExploreOperation.SearchAction, body, partition, partitions, config, direct, loggers, request, user,
            new SearchTermDirectory(osdu, documents, clock, folders), ct);

    /// <summary>The properties the records of a kind hold: the record's own, those its schema declares, and those its records hold beyond them.</summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> FieldsAsync(
        DeliveryExplorerFieldsRequest? body, [FromQuery] string? partition, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct,
        ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var kind = body?.Kind?.Trim();
        if (string.IsNullOrEmpty(kind))
        {
            return DeliveryEndpoints.Invalid("Name the kind whose properties to read.");
        }

        if (ExplorerKinds.Problem(kind) is { } wrong)
        {
            return DeliveryEndpoints.Invalid(wrong);
        }

        return await ExploreAsync(
            ExploreOperation.FieldsAction, new Dictionary<string, string>(StringComparer.Ordinal) { [ExploreOperation.KindArgument] = kind },
            partition, partitions, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }

    /// <summary>One record as the storage service holds it, at its latest or at one version, with its version list.</summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ReadAsync(
        DeliveryReadRequest? body, [FromQuery] string? partition, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct,
        ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (DeliveryEndpoints.TargetProblem(body, out var asked) is { } invalid)
        {
            return invalid;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["targetId"] = TargetId.WithoutVersion(asked) };
        DeliveryEndpoints.WithVersion(arguments, body);
        return await ExploreAsync(ExploreOperation.ReadAction, arguments, partition, partitions, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ExploreSearchAsync(
        string action, DeliveryExplorerSearchRequest? body, string? partition, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct,
        ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, SearchTermDirectory directory, CancellationToken ct)
    {
        var asked = body ?? new DeliveryExplorerSearchRequest();
        var (terms, failure) = await TermsOfAsync(asked, partition, partitions, config, request, directory, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        var (search, invalid) = SearchOf(asked, terms);
        if (search is null)
        {
            return DeliveryEndpoints.Invalid(invalid!);
        }

        return await ExploreAsync(
            action, new Dictionary<string, string>(StringComparer.Ordinal) { [ExploreOperation.SearchArgument] = search.ToJson() },
            partition, partitions, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }

    /// <summary>Runs one of the explorer's reads in this process, through the partition's own connection, and answers with it.</summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ExploreAsync(
        string action, Dictionary<string, string> arguments, string? partition, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct,
        ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var found = await ConnectAsync(partitions, config, WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false);
        if (found.Configured is not { } configured)
        {
            return NoConnection(found.Reason);
        }

        arguments[ExploreOperation.ActionArgument] = action;
        return await DirectOperationRunner.RunOnPartitionAsync(direct, found.Partition!, configured, arguments, user, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The search a request asks, checked as the operation checks it again, or why it cannot be asked. A condition naming a
    /// search term is the one <paramref name="terms"/> holds at its place, resolved before (<see cref="TermsOfAsync"/>).
    /// </summary>
    internal static (ExplorerSearch? Search, string? Problem) SearchOf(DeliveryExplorerSearchRequest body, IReadOnlyDictionary<int, ExplorerFilter>? terms = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!TryParse<ExplorerSort>(body.Sort, ExplorerSort.Relevance, out var sort))
        {
            return (null, $"'{body.Sort}' is not an order: relevance, modified or created.");
        }

        var filters = new List<ExplorerFilter>(body.Filters?.Count ?? 0);
        var place = -1;
        foreach (var filter in body.Filters ?? [])
        {
            place++;
            if (filter?.Term is not null)
            {
                if (terms?.GetValueOrDefault(place) is not { } resolved)
                {
                    return (null, "A condition on a search term is resolved before the search is asked.");
                }

                filters.Add(resolved);
                continue;
            }

            if (filter?.Path is not { Length: > 0 } path)
            {
                return (null, "Every condition a page narrows to names its property.");
            }

            if (!TryParse<OsduFieldIndex>(filter.Index, OsduFieldIndex.Text, out var index))
            {
                return (null, $"'{filter.Index}' is not how a property is indexed: text, keyword, number, boolean or date.");
            }

            if (!TryParse<ExplorerCondition>(filter.Condition, ExplorerCondition.Is, out var condition))
            {
                return (null, $"'{filter.Condition}' is not a condition: {ConditionNames}.");
            }

            filters.Add(new ExplorerFilter
            {
                Path = path.Trim(),
                Index = index,
                Nested = string.IsNullOrWhiteSpace(filter.Nested) ? null : filter.Nested.Trim(),
                Condition = condition,
                Value = filter.Value,
                Values = filter.Values,
                To = filter.To,
            });
        }

        ExplorerField? facet = null;
        if (body.Facet is { } grouped)
        {
            if (grouped.Path is not { Length: > 0 } path)
            {
                return (null, "Name the property to group the records by.");
            }

            if (!TryParse<OsduFieldIndex>(grouped.Index, OsduFieldIndex.Text, out var index))
            {
                return (null, $"'{grouped.Index}' is not how a property is indexed: text, keyword, number, boolean or date.");
            }

            facet = new ExplorerField { Path = path.Trim(), Index = index, Nested = string.IsNullOrWhiteSpace(grouped.Nested) ? null : grouped.Nested.Trim() };
        }

        var search = new ExplorerSearch
        {
            Text = string.IsNullOrWhiteSpace(body.Text) ? null : body.Text,
            Lucene = body.Lucene,
            Mentions = string.IsNullOrWhiteSpace(body.Mentions) ? null : body.Mentions.Trim(),
            Kind = string.IsNullOrWhiteSpace(body.Kind) ? null : body.Kind.Trim(),
            Filters = filters,
            Sort = sort,
            Offset = body.Offset ?? 0,
            Limit = body.Limit ?? ExplorerSearch.DefaultLimit,
            Facet = facet,
            Columns = body.Columns?.Select(c => c?.Trim() ?? string.Empty).ToList() ?? [],
        };
        return search.Problem() is { } problem ? (null, problem) : (search, null);
    }

    /// <summary>A name of <typeparamref name="T"/>, any case, or <paramref name="otherwise"/> when none is given; false for a name it does not have.</summary>
    private static bool TryParse<T>(string? text, T otherwise, out T value)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = otherwise;
            return true;
        }

        return Enum.TryParse(text.Trim(), ignoreCase: true, out value) && Enum.IsDefined(value) && !text.Trim().All(char.IsAsciiDigit);
    }

    /// <summary>
    /// The partition <paramref name="asked"/> names (the registry's default when it names none), as the registry spells it,
    /// with the control plane's configuration its connection resolves with (its values for the partition over those for
    /// none, <see cref="PartitionConnection"/>); or why the explorer has no connection to it: no partition named, one not
    /// registered, or one whose configuration and environment leave a value of the connection unset, each named.
    /// </summary>
    internal static async Task<(string? Partition, IReadOnlyDictionary<string, string>? Configured, string? Reason)> ConnectAsync(
        IPartitionRegistry partitions, DeliveryConfigStore config, string? asked, CancellationToken ct)
    {
        var registry = await partitions.ReadAsync(ct).ConfigureAwait(false);
        var named = string.IsNullOrWhiteSpace(asked) ? registry.Default : asked.Trim();
        if (named is null)
        {
            return (null, null, "No partition is named and none is the default. Pick one in the title bar.");
        }

        if (!CacheScope.IsPartitionId(named))
        {
            return (null, null, $"'{named}' is not a partition id.");
        }

        if (registry.Find(named) is not { } partition)
        {
            return (named, null, registry.NotRegistered(named));
        }

        var configured = (await config.ConfigurationAsync(null, ct).ConfigureAwait(false)).For(partition);
        var missing = PartitionConnection.Missing(configured, Environment.GetEnvironmentVariable);
        return missing.Count == 0
            ? (partition, configured, null)
            : (partition, null,
                $"The explorer reads partition '{partition}' through the platform and client its configuration names, and {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not set. "
                + $"Set {(missing.Count == 1 ? "it" : "them")} under {ConfiguredPartitions.SectionPath}:{partition}:Properties in the control plane's configuration, or in its environment.");
    }

    /// <summary>The answer to a read the explorer cannot make, with <paramref name="reason"/>.</summary>
    private static ProblemHttpResult NoConnection(string? reason)
        => TypedResults.Problem(detail: reason, statusCode: StatusCodes.Status409Conflict, title: "No connection to the partition");

    /// <summary>Whether a flow delivering by <paramref name="protocol"/> keeps its records in OSDU's storage service, where a check reads them: every route but dspdm and etp, whose endpoints are not the platform's.</summary>
    private static bool KeepsStorageRecords(DeliveryProtocol protocol) => protocol is not (DeliveryProtocol.Dspdm or DeliveryProtocol.Etp);
}
