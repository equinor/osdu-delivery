using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Order;
using SqlFlow.Delivery.Ordering;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>A built release of OSDU's lineage: the release and commit it was read from, who built it and when, and what it holds.</summary>
public sealed record DeliveryOrderReleaseDto(
    string Release, string Commit, DateTime? PublishedUtc, DateTime BuiltUtc, string BuiltBy, int Kinds, int Relationships, IReadOnlyList<SchemaLineageFailure> Failures);

/// <summary>A build of OSDU's lineage: the release to build, the newest when none is named.</summary>
public sealed record DeliveryOrderBuildRequest(string? Release);

/// <summary>A kind of an entity type in a release, with its status there.</summary>
public sealed record DeliveryOrderKindDto(string Kind, string Version, string? Status);

/// <summary>
/// An entity type of a release's lineage, or a group a relationship names without an entity type (<c>Kinds</c> empty): how
/// many entity types it refers to and is referred to by.
/// </summary>
public sealed record DeliveryOrderEntityDto(string EntityType, string Group, IReadOnlyList<DeliveryOrderKindDto> Kinds, int RefersTo, int ReferredBy);

/// <summary>Every entity type a built release holds.</summary>
public sealed record DeliveryOrderEntitiesDto(DeliveryOrderReleaseDto Release, IReadOnlyList<DeliveryOrderEntityDto> Entities);

/// <summary>Records of <c>From</c> refer to records of <c>To</c> through each of <c>Properties</c>.</summary>
public sealed record DeliveryOrderGraphEdgeDto(string From, string To, IReadOnlyList<string> Properties);

/// <summary>
/// The part of a release's lineage around one entity type: what it refers to and what refers to it, as far as
/// <c>Depth</c> steps, with <c>Truncated</c> when the part was cut at <see cref="DeliveryOrderEndpoints.MaxGraphNodes"/> entity types.
/// </summary>
public sealed record DeliveryOrderGraphDto(
    DeliveryOrderReleaseDto Release, string Entity, int Depth, string Direction, IReadOnlyList<DeliveryOrderEntityDto> Nodes, IReadOnlyList<DeliveryOrderGraphEdgeDto> Edges, bool Truncated);

/// <summary>A delivery of a partition's order: its repository, flow and interface, the kind it writes, and whether its relationships are known.</summary>
public sealed record DeliveryOrderDeliveryDto(string? Repository, string Flow, string? Interface, string Kind, string EntityType, int Level, bool Known);

/// <summary>A delivery and the property of its mapping that makes a reference.</summary>
public sealed record DeliveryOrderReasonDto(string? Repository, string Flow, string? Interface, string Property, bool Searched);

/// <summary>A reference the order keeps: records of <c>From</c> wait for records of <c>To</c>.</summary>
public sealed record DeliveryOrderEdgeDto(string From, string To, IReadOnlyList<DeliveryOrderReasonDto> Because);

/// <summary>A reference the order cut, the rule that cut it, and why.</summary>
public sealed record DeliveryOrderCutDto(string From, string To, string Reason, string Why);

/// <summary>The order of one partition, every repository's deliveries together: its levels, deliveries, references, cuts and warnings.</summary>
public sealed record DeliveryOrderPartitionDto(
    string Partition,
    IReadOnlyList<IReadOnlyList<string>> Levels,
    IReadOnlyList<DeliveryOrderDeliveryDto> Deliveries,
    IReadOnlyList<DeliveryOrderEdgeDto> Edges,
    IReadOnlyList<DeliveryOrderCutDto> Cuts,
    IReadOnlyList<string> SelfReferences,
    IReadOnlyList<string> Warnings);

/// <summary>The order of every partition the synced deliveries reach, and what the order could not read.</summary>
public sealed record DeliveryOrderPartitionsDto(IReadOnlyList<DeliveryOrderPartitionDto> Partitions, IReadOnlyList<string> Problems);

/// <summary>A schedule as the overview shows it: what it is, and what decides when it fires.</summary>
public sealed record DeliveryOrderScheduleDto(
    Guid Id, Guid RepoId, string Repository, string Name, string? Cron, int? IntervalSeconds, string Timezone, bool Fires, DateTime? LastFireUtc, DateTime? NextFireUtc);

/// <summary>Why a schedule runs best after another: a flow of it refers to records a flow of the other delivers.</summary>
public sealed record DeliveryOrderScheduleReasonDto(
    string Flow, string? Interface, string EntityType, string RefersTo, string ProviderRepository, string ProviderFlow, string? ProviderInterface, IReadOnlyList<string> Partitions);

/// <summary>A schedule that runs best before another, how their fires fall, and why.</summary>
public sealed record DeliveryOrderScheduleFirstDto(DeliveryOrderScheduleDto Schedule, bool SameRepository, string Timing, IReadOnlyList<DeliveryOrderScheduleReasonDto> Because);

/// <summary>One schedule of the overview: the schedules it runs best after, and the deliveries it refers to that no schedule runs.</summary>
public sealed record DeliveryOrderScheduleOrderDto(
    DeliveryOrderScheduleDto Schedule, IReadOnlyList<DeliveryOrderScheduleFirstDto> RunFirst, IReadOnlyList<DeliveryOrderScheduleReasonDto> Unscheduled, bool OutOfOrder);

/// <summary>Every schedule that runs a delivery, which runs best before which, and the schedules that refer to each other in a circle.</summary>
public sealed record DeliveryOrderSchedulesDto(IReadOnlyList<DeliveryOrderScheduleOrderDto> Schedules, IReadOnlyList<IReadOnlyList<DeliveryOrderScheduleDto>> Circles);

/// <summary>
/// The OSDU order (osdu/docs/reference/concepts/osdu-order.md): OSDU's lineage by release, built from the OSDU data
/// definitions and read back entity by entity; the order of each partition with every repository's deliveries together;
/// and which schedules run best before which, with how their fires fall. Reading is any signed-in user's; building a
/// release reads the data definitions and writes the module database, which is an operate action.
/// </summary>
public static class DeliveryOrderEndpoints
{
    /// <summary>The most entity types a part of a release's lineage holds.</summary>
    public const int MaxGraphNodes = 150;

    /// <summary>The furthest a part of a release's lineage reaches from its entity type.</summary>
    public const int MaxGraphDepth = 4;

    public static RouteGroupBuilder MapDeliveryOrderReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var order = group.MapGroup("/delivery/order").WithTags("Delivery");
        order.MapGet("/releases", ListReleasesAsync).WithName("ListDeliveryOrderReleases");
        order.MapGet("/releases/{release}/entities", ListEntitiesAsync).WithName("ListDeliveryOrderEntities");
        order.MapGet("/releases/{release}/graph", GetGraphAsync).WithName("GetDeliveryOrderGraph");
        order.MapGet("/partitions", ListPartitionsAsync).WithName("ListDeliveryOrderPartitions");
        order.MapGet("/schedules", ListSchedulesAsync).WithName("ListDeliveryOrderSchedules");
        return group;
    }

    public static RouteGroupBuilder MapDeliveryOrderOperateEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var order = group.MapGroup("/delivery/order").WithTags("Delivery");
        order.MapPost("/releases", BuildReleaseAsync).WithName("BuildDeliveryOrderRelease");
        return group;
    }

    private static async Task<Ok<IReadOnlyList<DeliveryOrderReleaseDto>>> ListReleasesAsync(SchemaLineageStore store, CancellationToken ct)
        => TypedResults.Ok<IReadOnlyList<DeliveryOrderReleaseDto>>([.. (await store.ListAsync(ct).ConfigureAwait(false)).Select(Release)]);

    private static async Task<Results<Ok<DeliveryOrderReleaseDto>, ProblemHttpResult>> BuildReleaseAsync(
        DeliveryOrderBuildRequest? request, SchemaLineageBuilder builder, ClaimsPrincipal user, CancellationToken ct)
    {
        try
        {
            var info = await builder.BuildAsync(string.IsNullOrWhiteSpace(request?.Release) ? null : request.Release.Trim(), RequestActor.Label(user), ct)
                .ConfigureAwait(false);
            return TypedResults.Ok(Release(info));
        }
        catch (DataDefinitionsException ex)
        {
            return ex.NotFound
                ? Problem(ex.Message, StatusCodes.Status404NotFound, "Not found")
                : Problem(ex.Message, StatusCodes.Status502BadGateway, "OSDU data definitions unavailable");
        }
        catch (DeliveryException ex)
        {
            return Problem(ex.Message, StatusCodes.Status422UnprocessableEntity, "Not built");
        }
    }

    private static async Task<Results<Ok<DeliveryOrderEntitiesDto>, ProblemHttpResult>> ListEntitiesAsync(
        string release, SchemaLineageStore store, CancellationToken ct)
    {
        var built = await store.LoadAsync(release, ct).ConfigureAwait(false);
        if (built is null)
        {
            return NotBuilt(release);
        }

        var lineage = Lineage.Of(built);
        return TypedResults.Ok(new DeliveryOrderEntitiesDto(Release(built.Info), [.. lineage.Entities.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(lineage.Entity)]));
    }

    private static async Task<Results<Ok<DeliveryOrderGraphDto>, ProblemHttpResult>> GetGraphAsync(
        string release, string? entity, int? depth, string? direction, SchemaLineageStore store, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            return Problem("Name the entity type the graph is drawn around, as group--Entity.", StatusCodes.Status400BadRequest);
        }

        var steps = depth ?? 1;
        if (steps is < 1 or > MaxGraphDepth)
        {
            return Problem($"depth is from 1 to {MaxGraphDepth}.", StatusCodes.Status400BadRequest);
        }

        var way = (direction ?? "both").Trim().ToLowerInvariant();
        if (way is not ("both" or "out" or "in"))
        {
            return Problem("direction is out (what it refers to), in (what refers to it) or both.", StatusCodes.Status400BadRequest);
        }

        var built = await store.LoadAsync(release, ct).ConfigureAwait(false);
        if (built is null)
        {
            return NotBuilt(release);
        }

        var lineage = Lineage.Of(built);
        var start = lineage.Entities.Keys.FirstOrDefault(e => string.Equals(e, entity.Trim(), StringComparison.OrdinalIgnoreCase));
        if (start is null)
        {
            return Problem($"Release {built.Info.Release} holds no entity type '{entity.Trim()}'.", StatusCodes.Status404NotFound, "Not found");
        }

        // Walked breadth first, so the nearest entity types are the ones kept when the part is cut.
        var nodes = new List<string> { start };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var frontier = new List<string> { start };
        var truncated = false;
        for (var step = 0; step < steps && frontier.Count > 0 && !truncated; step++)
        {
            var next = new List<string>();
            foreach (var at in frontier)
            {
                var neighbours = (way is "both" or "out" ? lineage.Out(at) : [])
                    .Concat(way is "both" or "in" ? lineage.In(at) : [])
                    .Order(StringComparer.OrdinalIgnoreCase);
                foreach (var neighbour in neighbours.Where(seen.Add))
                {
                    if (nodes.Count >= MaxGraphNodes)
                    {
                        truncated = true;
                        break;
                    }

                    nodes.Add(neighbour);
                    next.Add(neighbour);
                }

                if (truncated)
                {
                    break;
                }
            }

            frontier = next;
        }

        var kept = new HashSet<string>(nodes, StringComparer.OrdinalIgnoreCase);
        var edges = lineage.Edges
            .Where(e => kept.Contains(e.Key.From) && kept.Contains(e.Key.To))
            .OrderBy(e => e.Key.From, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Key.To, StringComparer.OrdinalIgnoreCase)
            .Select(e => new DeliveryOrderGraphEdgeDto(e.Key.From, e.Key.To, [.. e.Value.Order(StringComparer.Ordinal)]))
            .ToList();
        return TypedResults.Ok(new DeliveryOrderGraphDto(Release(built.Info), start, steps, way, [.. nodes.Select(lineage.Entity)], edges, truncated));
    }

    private static async Task<Ok<DeliveryOrderPartitionsDto>> ListPartitionsAsync(
        OsduDbContext osdu, CatalogDbContext catalog, IRepositoryFolders folders, CancellationToken ct)
    {
        var read = await OrderCatalog.ReadAsync(osdu, folders, null, ct).ConfigureAwait(false);
        var names = await catalog.Repos.AsNoTracking().Select(r => new { r.Id, r.Name }).ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);
        var plans = RepositoryOrder.Plan(read, names);
        return TypedResults.Ok(new DeliveryOrderPartitionsDto([.. plans.Select(Partition)], read.Problems));
    }

    private static async Task<Ok<DeliveryOrderSchedulesDto>> ListSchedulesAsync(
        OsduDbContext osdu, CatalogDbContext catalog, IRepositoryFolders folders, CancellationToken ct)
    {
        var overview = await ScheduleOrders.ReadAsync(catalog, osdu, folders, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryOrderSchedulesDto(
            [.. overview.Schedules.Select(o => new DeliveryOrderScheduleOrderDto(
                Schedule(o.Schedule),
                [.. o.RunFirst.Select(f => new DeliveryOrderScheduleFirstDto(Schedule(f.Schedule), f.SameRepository, Timing(f.Timing), [.. f.Because.Select(Reason)]))],
                [.. o.Unscheduled.Select(Reason)],
                o.OutOfOrder.Any()))],
            [.. overview.Circles.Select(c => (IReadOnlyList<DeliveryOrderScheduleDto>)[.. c.Select(Schedule)])]));
    }

    private static DeliveryOrderPartitionDto Partition(OrderPlan plan)
        => new(
            plan.Partition,
            plan.Levels,
            [.. plan.Deliveries.Select(d => new DeliveryOrderDeliveryDto(
                d.RepositoryName ?? (d.Repository.Length == 0 ? null : d.Repository), d.Flow, d.Interface, d.Kind, d.EntityType, plan.LevelOf(d.EntityType),
                !plan.Unknown.Contains(d)))],
            [.. plan.Edges.Select(e => new DeliveryOrderEdgeDto(e.From, e.To, [.. e.Because.Select(b => new DeliveryOrderReasonDto(
                b.Delivery.RepositoryName ?? (b.Delivery.Repository.Length == 0 ? null : b.Delivery.Repository), b.Delivery.Flow, b.Delivery.Interface, b.Property, b.Searched))]))],
            [.. plan.Cuts.Select(c => new DeliveryOrderCutDto(c.Edge.From, c.Edge.To, c.Reason.ToString(), c.Why))],
            [.. plan.SelfReferences.Select(s => s.From)],
            plan.Warnings);

    private static DeliveryOrderScheduleDto Schedule(ScheduleName s)
        => new(s.Id, s.RepoId, s.Repository, s.Name, s.Cron, s.IntervalSeconds, s.Timezone, s.Fires, s.LastFireUtc, s.NextFireUtc);

    private static DeliveryOrderScheduleReasonDto Reason(ScheduleReason r)
        => new(r.Flow, r.Interface, r.EntityType, r.RefersTo, r.ProviderRepository, r.ProviderFlow, r.ProviderInterface, r.Partitions);

    private static string Timing(ScheduleTiming timing) => timing switch
    {
        ScheduleTiming.Chained => "chained",
        ScheduleTiming.FiresFirst => "firesFirst",
        ScheduleTiming.FiresAfter => "firesAfter",
        ScheduleTiming.NotFiring => "notFiring",
        _ => "unknown",
    };

    private static DeliveryOrderReleaseDto Release(SchemaLineageInfo info)
        => new(info.Release, info.Commit, info.PublishedUtc, info.BuiltUtc, info.BuiltBy, info.Kinds, info.Relationships, info.Failures);

    private static ProblemHttpResult NotBuilt(string release)
        => Problem($"OSDU's lineage of release '{release}' has not been built; build it first.", StatusCodes.Status404NotFound, "Not found");

    private static ProblemHttpResult Problem(string detail, int status, string title = "Bad request")
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);

    /// <summary>
    /// A release's lineage at the level of entity types: each entity type with its kinds, and every reference between two
    /// of them with the properties that make it. A relationship naming a group alone (a dataset of any type) refers to the
    /// group, which stands as a node of its own.
    /// </summary>
    private sealed class Lineage
    {
        public Dictionary<string, List<SchemaLineageEntry>> Entities { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<(string From, string To), SortedSet<string>> Edges { get; } = new(EdgeComparer.Instance);

        private readonly Dictionary<string, HashSet<string>> _out = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _in = new(StringComparer.OrdinalIgnoreCase);

        public static Lineage Of(SchemaLineageGraph built)
        {
            var lineage = new Lineage();
            foreach (var kind in built.Kinds)
            {
                lineage.Node(kind.Node.EntityType).Add(kind);
            }

            foreach (var kind in built.Kinds)
            {
                foreach (var relationship in kind.Node.Relationships)
                {
                    foreach (var target in relationship.Targets)
                    {
                        var to = OsduEntity.IsEntityType(target) ? target : OsduEntity.GroupOf(target);
                        if (string.Equals(to, kind.Node.EntityType, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        lineage.Node(to);
                        if (!lineage.Edges.TryGetValue((kind.Node.EntityType, to), out var properties))
                        {
                            properties = new SortedSet<string>(StringComparer.Ordinal);
                            lineage.Edges[(kind.Node.EntityType, to)] = properties;
                        }

                        properties.Add(relationship.Property);
                        lineage.Link(kind.Node.EntityType, to);
                    }
                }
            }

            return lineage;
        }

        public IEnumerable<string> Out(string entity) => _out.GetValueOrDefault(entity) ?? [];

        public IEnumerable<string> In(string entity) => _in.GetValueOrDefault(entity) ?? [];

        public DeliveryOrderEntityDto Entity(string entity)
            => new(
                entity,
                OsduEntity.GroupOf(entity),
                [.. Entities[entity].OrderBy(k => k.Node.Kind, StringComparer.Ordinal).Select(k => new DeliveryOrderKindDto(k.Node.Kind, k.Version, k.Status))],
                Out(entity).Count(),
                In(entity).Count());

        private List<SchemaLineageEntry> Node(string entity)
        {
            if (!Entities.TryGetValue(entity, out var kinds))
            {
                kinds = [];
                Entities[entity] = kinds;
            }

            return kinds;
        }

        private void Link(string from, string to)
        {
            if (!_out.TryGetValue(from, out var outs))
            {
                outs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _out[from] = outs;
            }

            if (!_in.TryGetValue(to, out var ins))
            {
                ins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _in[to] = ins;
            }

            outs.Add(to);
            ins.Add(from);
        }
    }

    private sealed class EdgeComparer : IEqualityComparer<(string From, string To)>
    {
        public static readonly EdgeComparer Instance = new();

        public bool Equals((string From, string To) x, (string From, string To) y)
            => string.Equals(x.From, y.From, StringComparison.OrdinalIgnoreCase) && string.Equals(x.To, y.To, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string From, string To) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.From), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.To));
    }
}
