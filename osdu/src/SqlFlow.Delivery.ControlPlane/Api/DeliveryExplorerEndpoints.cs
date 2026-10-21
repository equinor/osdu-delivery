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
/// How the explorer reaches a partition: the partition, whether anything reaches it, and through what (the delivery flow whose
/// OSDU connection it borrows, the endpoint that flow names, as written, and the route it delivers by), or why nothing does.
/// </summary>
/// <param name="Partition">The partition asked about; null when no partition was named and none is the default.</param>
/// <param name="Available">True when a connection reaches the partition.</param>
/// <param name="Through">The flow (and interface) whose connection the explorer reads through; null when none reaches it.</param>
/// <param name="Endpoint">The endpoint that flow names, as its document writes it (a reference stays a reference).</param>
/// <param name="Route">The route the flow delivers by, which says nothing of how the explorer reads: always search and storage.</param>
/// <param name="Reason">Why no connection reaches the partition; null when one does.</param>
public sealed record DeliveryExplorerConnectionDto(string? Partition, bool Available, string? Through, string? Endpoint, string? Route, string? Reason);

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
/// OSDU's own search and storage services. It shows what OSDU holds and nothing the delivery system keeps; the delivery
/// system lends it only the way in: each read (<see cref="ExploreOperation"/>) runs in this process, as the request's
/// answer, through the OSDU connection of a delivery flow that reaches the partition, which the engine keeps open
/// between reads. The partition is the one the request names, else the workbench's, else the registry's default.
/// </summary>
/// <remarks>
/// The connection is picked, not chosen by the reader: of the delivery flows that reach the partition, one whose route
/// delivers through the platform's own services (any but dspdm and etp, whose endpoints are not the platform's), the storage
/// route first since its endpoint is the platform root by definition, then by flow and interface name, so the same partition
/// is always read the same way. Reading what OSDU holds with a flow's credentials is an operate action, as reading a record
/// back is; which connection a partition is read through is anyone's to see.
/// </remarks>
public static partial class DeliveryExplorerEndpoints
{
    /// <summary>The routes the explorer reads through, best first; a route not listed (dspdm, etp) has no platform endpoint.</summary>
    private static readonly IReadOnlyList<DeliveryProtocol> Routes =
    [
        DeliveryProtocol.Storage,
        DeliveryProtocol.Manifest,
        DeliveryProtocol.File,
        DeliveryProtocol.Dataset,
        DeliveryProtocol.Workflow,
        DeliveryProtocol.FileAndDdms,
        DeliveryProtocol.ManifestAndDdms,
        DeliveryProtocol.Ddms,
    ];

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
        [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, HttpRequest request,
        CancellationToken ct)
    {
        var found = await ConnectAsync(db, documents, partitions, ledger, WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false);
        return TypedResults.Ok(found.Flow is { } flow
            ? new DeliveryExplorerConnectionDto(found.Partition, true, flow.Flow.Label, flow.Flow.Target.Endpoint, DeliveryProtocols.Name(flow.Flow.Target.Protocol), null)
            : new DeliveryExplorerConnectionDto(found.Partition, false, null, null, null, found.Reason));
    }

    /// <summary>The kinds of the records a search finds across every kind, each with its count.</summary>
    private static Task<Results<ContentHttpResult, ProblemHttpResult>> TypesAsync(
        DeliveryExplorerSearchRequest? body, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user,
        OsduDbContext osdu, TimeProvider clock, CancellationToken ct)
        => QueueSearchAsync(
            ExploreOperation.TypesAction, body, partition, db, documents, partitions, ledger, config, direct, loggers, request, user,
            new SearchTermDirectory(osdu, documents, clock), ct);

    /// <summary>One page of the records a search finds.</summary>
    private static Task<Results<ContentHttpResult, ProblemHttpResult>> SearchAsync(
        DeliveryExplorerSearchRequest? body, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user,
        OsduDbContext osdu, TimeProvider clock, CancellationToken ct)
        => QueueSearchAsync(
            ExploreOperation.SearchAction, body, partition, db, documents, partitions, ledger, config, direct, loggers, request, user,
            new SearchTermDirectory(osdu, documents, clock), ct);

    /// <summary>The properties the records of a kind hold: the record's own, those its schema declares, and those its records hold beyond them.</summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> FieldsAsync(
        DeliveryExplorerFieldsRequest? body, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
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

        return await QueueAsync(
            ExploreOperation.FieldsAction, new Dictionary<string, string>(StringComparer.Ordinal) { [ExploreOperation.KindArgument] = kind },
            partition, db, documents, partitions, ledger, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }

    /// <summary>One record as the storage service holds it, at its latest or at one version, with its version list.</summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ReadAsync(
        DeliveryReadRequest? body, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        if (DeliveryEndpoints.TargetProblem(body, out var asked) is { } invalid)
        {
            return invalid;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { ["targetId"] = TargetId.WithoutVersion(asked) };
        DeliveryEndpoints.WithVersion(arguments, body);
        return await QueueAsync(ExploreOperation.ReadAction, arguments, partition, db, documents, partitions, ledger, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> QueueSearchAsync(
        string action, DeliveryExplorerSearchRequest? body, string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user,
        SearchTermDirectory directory, CancellationToken ct)
    {
        var asked = body ?? new DeliveryExplorerSearchRequest();
        var (terms, failure) = await TermsOfAsync(asked, partition, db, documents, partitions, ledger, request, directory, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        var (search, invalid) = SearchOf(asked, terms);
        if (search is null)
        {
            return DeliveryEndpoints.Invalid(invalid!);
        }

        return await QueueAsync(
            action, new Dictionary<string, string>(StringComparer.Ordinal) { [ExploreOperation.SearchArgument] = search.ToJson() },
            partition, db, documents, partitions, ledger, config, direct, loggers, request, user, ct).ConfigureAwait(false);
    }

    /// <summary>Runs one of the explorer's reads in this process, through the connection that reaches the partition, and answers with it.</summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> QueueAsync(
        string action, Dictionary<string, string> arguments, string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers, HttpRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var found = await ConnectAsync(db, documents, partitions, ledger, WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false);
        if (found.Flow is not { } flow)
        {
            return TypedResults.Problem(detail: found.Reason, statusCode: StatusCodes.Status409Conflict, title: "No connection to the partition");
        }

        arguments[ExploreOperation.ActionArgument] = action;
        return await DirectOperationRunner.RunAsync(db, config, direct, flow, ExploreOperation.OperationName, arguments, user, loggers, ct).ConfigureAwait(false);
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
    /// The delivery flow whose OSDU connection reaches <paramref name="asked"/> (the registry's default when none is named), as
    /// the remarks on <see cref="DeliveryExplorerEndpoints"/> describe; or, when none does, why. A flow that hard-codes its
    /// partition header reaches the partition the header names, or the partition its ledger is kept in when the header is a
    /// reference the control plane does not resolve.
    /// </summary>
    internal static async Task<(DeliveryEndpoints.FlowContext? Flow, string? Partition, string? Reason)> ConnectAsync(
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, string? asked, CancellationToken ct)
    {
        var registry = await partitions.ReadAsync(ct).ConfigureAwait(false);
        var partition = string.IsNullOrWhiteSpace(asked) ? registry.Default : asked.Trim();
        if (partition is not null && !CacheScope.IsPartitionId(partition))
        {
            return (null, null, $"'{partition}' is not a partition id.");
        }

        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == FlowDefinition.FlowTypeName && p.Active)
            .ToListAsync(ct).ConfigureAwait(false);
        IReadOnlySet<Guid>? kept = null;
        (DeliveryEndpoints.SourceContext Source, FlowDefinition Flow, string? Partition, int Rank)? best = null;
        foreach (var pipeline in pipelines.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (source, _) = DeliveryEndpoints.Parse(documents, pipeline);
            if (source is null)
            {
                // A flow whose catalog copy does not parse reaches nothing; the flow's own pages say why.
                continue;
            }

            string? bound = null;
            if (source.Source.Partitioned)
            {
                bound = partition is null ? null : source.Source.Served(registry).FirstOrDefault(p => string.Equals(p, partition, StringComparison.OrdinalIgnoreCase));
                if (bound is null)
                {
                    continue;
                }
            }

            foreach (var flow in source.Source.Interfaces)
            {
                var rank = Rank(flow.Target.Protocol);
                if (rank < 0 || (best is { } current && rank >= current.Rank))
                {
                    continue;
                }

                if (!source.Source.Partitioned && partition is not null && !HeaderNames(flow, partition))
                {
                    kept ??= (await ledger.ListLedgersAsync(partition, ct).ConfigureAwait(false)).Select(l => l.FlowId).ToHashSet();
                    if (!kept.Contains(flow.Id))
                    {
                        continue;
                    }
                }

                best = (source, flow, bound, rank);
            }
        }

        if (best is not { } chosen)
        {
            return (null, partition, partition is null
                ? "No partition is named and none is the default. Pick one in the title bar."
                : $"No delivery flow reaches partition '{partition}', so the explorer has no connection to it. A flow that delivers there through the platform's own services (any route but dspdm and etp) lends the explorer its connection.");
        }

        var (picked, problem) = DeliveryEndpoints.Select(chosen.Source, chosen.Flow.Interface, chosen.Partition, registry);
        return picked is null
            ? (null, partition, problem?.ProblemDetails.Detail ?? "The flow that reaches the partition could not be bound to it.")
            : (picked, partition ?? chosen.Partition, null);
    }

    /// <summary>Where a route stands among those the explorer reads through, best first; -1 for one it cannot read through.</summary>
    private static int Rank(DeliveryProtocol protocol)
    {
        for (var i = 0; i < Routes.Count; i++)
        {
            if (Routes[i] == protocol)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether the flow's partition header names <paramref name="partition"/> as written, not as a reference to resolve.</summary>
    private static bool HeaderNames(FlowDefinition flow, string partition)
    {
        var declared = flow.Target.Headers.FirstOrDefault(h => h.Key.Equals(CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        return declared is not null && !declared.Contains("${", StringComparison.Ordinal) && string.Equals(declared.Trim(), partition, StringComparison.OrdinalIgnoreCase);
    }
}
