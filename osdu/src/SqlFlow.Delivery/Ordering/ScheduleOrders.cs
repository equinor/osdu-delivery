using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Order;

namespace SqlFlow.Delivery.Ordering;

/// <summary>
/// A schedule as the order names it: its id, its repository (by id and name) and its name, with what decides when it
/// fires: its cadence (a cron in a time zone, an interval, or neither for a schedule that fires after others), whether it
/// is enabled and not paused, and its last and next fires.
/// </summary>
public sealed record ScheduleName(Guid Id, Guid RepoId, string Repository, string Name)
{
    public string? Cron { get; init; }

    public int? IntervalSeconds { get; init; }

    public string Timezone { get; init; } = "UTC";

    public bool Enabled { get; init; } = true;

    public bool Paused { get; init; }

    public DateTime? LastFireUtc { get; init; }

    public DateTime? NextFireUtc { get; init; }

    /// <summary>Whether the schedule fires at all: enabled, and not paused.</summary>
    public bool Fires => Enabled && !Paused;
}

/// <summary>A flow a schedule runs, as the order needs it: the schedule, and the flow's repository and name.</summary>
public sealed record ScheduleMemberFlow(Guid ScheduleId, Guid RepoId, string FlowName);

/// <summary>
/// Why one schedule runs best after another: a flow the later schedule runs (and the interface of it) delivers
/// <see cref="EntityType"/> records that refer to <see cref="RefersTo"/> records, which a flow of the earlier schedule
/// delivers, in each of <see cref="Partitions"/>.
/// </summary>
public sealed record ScheduleReason(
    string Flow,
    string? Interface,
    string EntityType,
    string RefersTo,
    Guid ProviderRepoId,
    string ProviderRepository,
    string ProviderFlow,
    string? ProviderInterface,
    IReadOnlyList<string> Partitions);

/// <summary>How the fires of a schedule and of one that runs best before it fall.</summary>
public enum ScheduleTiming
{
    /// <summary>It fires after the earlier one: it names it in <c>after:</c>, directly or through the schedules it chains behind.</summary>
    Chained,

    /// <summary>The earlier one fires next, before it does.</summary>
    FiresFirst,

    /// <summary>It fires next, before the earlier one does: its flows run before the records they refer to are delivered.</summary>
    FiresAfter,

    /// <summary>The earlier one does not fire: it is disabled or paused.</summary>
    NotFiring,

    /// <summary>One of them has no next fire to compare: it fires after other schedules, or has not been planned.</summary>
    Unknown,
}

/// <summary>
/// A schedule that runs best before another, with every reason, and how their fires fall (<see cref="Timing"/>).
/// <see cref="SameRepository"/> says whether the later schedule could chain behind it with <c>after:</c>, which names
/// schedules of its own repository only.
/// </summary>
public sealed record ScheduleFirst(ScheduleName Schedule, bool SameRepository, ScheduleTiming Timing, IReadOnlyList<ScheduleReason> Because)
{
    /// <summary>Whether the later schedule already fires after it.</summary>
    public bool Chained => Timing == ScheduleTiming.Chained;
}

/// <summary>
/// What the OSDU order says of one schedule (osdu/docs/reference/concepts/osdu-order.md, Which schedules run before which): the schedules whose
/// flows deliver records its flows' records refer to, which run best before it, and the deliveries it refers to that no
/// schedule runs.
/// </summary>
public sealed record ScheduleOrder(ScheduleName Schedule, IReadOnlyList<ScheduleFirst> RunFirst, IReadOnlyList<ScheduleReason> Unscheduled)
{
    /// <summary>The schedules it runs best after whose fires fall the wrong way or do not happen.</summary>
    public IEnumerable<ScheduleFirst> OutOfOrder => RunFirst.Where(f => f.Timing is ScheduleTiming.FiresAfter or ScheduleTiming.NotFiring);
}

/// <summary>
/// Every schedule that runs a delivery, which schedules run best before which and how their fires fall, and the schedules
/// that each run best before another of them, which no timing can satisfy.
/// </summary>
public sealed record ScheduleOverview(IReadOnlyList<ScheduleOrder> Schedules, IReadOnlyList<IReadOnlyList<ScheduleName>> Circles);

/// <summary>
/// Which schedules run best before which, from the OSDU order of each partition with every repository's deliveries
/// together, and whether their fires fall that way. Nothing here orders a run: a schedule fires on its own clock or behind
/// the schedules it names in <c>after:</c>, and its flows run in their repository's waves, which run every flow after the
/// flows of the same schedule it refers to. Across schedules this is what to know when timing or chaining them.
/// </summary>
public static class ScheduleOrders
{
    /// <summary>
    /// Reads the catalog and the module database and answers for every schedule that runs a delivery: the deliveries from
    /// the module's interfaces, the schedules and the flows they run (active, and run by a fire) from the catalog. The two
    /// may be in different databases; nothing here writes. A mapping's template is read from its file, in the folder
    /// <paramref name="folders"/> finds its repository in.
    /// </summary>
    public static async Task<ScheduleOverview> ReadAsync(
        CatalogDbContext catalog, OsduDbContext module, Catalog.IRepositoryFolders folders, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(module);
        var read = await OrderCatalog.ReadAsync(module, folders, null, ct).ConfigureAwait(false);
        var repositories = await catalog.Repos.AsNoTracking()
            .Select(r => new { r.Id, r.Name })
            .ToDictionaryAsync(r => r.Id, r => r.Name, ct)
            .ConfigureAwait(false);
        var rows = await catalog.Schedules.AsNoTracking()
            .Select(s => new { s.Id, s.RepoId, s.Name, s.Cron, s.IntervalSeconds, s.Timezone, s.Enabled, s.Paused, s.LastFireUtc, s.NextFireUtc })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var members = await (
                from member in catalog.ScheduleMembers.AsNoTracking()
                join pipeline in catalog.Pipelines.AsNoTracking() on member.PipelineId equals pipeline.Id
                where pipeline.Active && pipeline.ExecutionMode == PipelineExecutionModes.Auto
                select new ScheduleMemberFlow(member.ScheduleId, pipeline.RepoId, pipeline.Name))
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var parents = await catalog.ScheduleParents.AsNoTracking()
            .Select(p => new { p.ScheduleId, p.ParentName })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var schedules = rows
            .Select(s => new ScheduleName(s.Id, s.RepoId, repositories.GetValueOrDefault(s.RepoId, s.RepoId.ToString("D")), s.Name)
            {
                Cron = s.Cron,
                IntervalSeconds = s.IntervalSeconds,
                Timezone = s.Timezone,
                Enabled = s.Enabled,
                Paused = s.Paused,
                LastFireUtc = s.LastFireUtc is { } last ? DateTime.SpecifyKind(last, DateTimeKind.Utc) : null,
                NextFireUtc = s.NextFireUtc is { } next ? DateTime.SpecifyKind(next, DateTimeKind.Utc) : null,
            })
            .ToList();
        return Of(RepositoryOrder.Plan(read, repositories), schedules, members, parents.Select(p => (p.ScheduleId, p.ParentName)).ToList());
    }

    /// <summary>
    /// The answer for every schedule that runs a delivery of <paramref name="plans"/>, from the plans, the schedules, the
    /// flows each runs, and the schedules each chains behind by name (within its repository).
    /// </summary>
    public static ScheduleOverview Of(
        IReadOnlyList<OrderPlan> plans,
        IReadOnlyList<ScheduleName> schedules,
        IReadOnlyList<ScheduleMemberFlow> members,
        IReadOnlyList<(Guid ScheduleId, string ParentName)> parents)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(parents);
        var byId = schedules.ToDictionary(s => s.Id);
        var running = new Dictionary<(Guid RepoId, string Flow), List<Guid>>(FlowComparer.Instance);
        foreach (var member in members.Where(m => byId.ContainsKey(m.ScheduleId)))
        {
            if (!running.TryGetValue((member.RepoId, member.FlowName), out var list))
            {
                list = [];
                running[(member.RepoId, member.FlowName)] = list;
            }

            if (!list.Contains(member.ScheduleId))
            {
                list.Add(member.ScheduleId);
            }
        }

        // Every reason, by the schedule it is a reason for and the schedule that runs best first (none: no schedule runs it).
        var reasons = new Dictionary<(Guid Later, Guid? Earlier), Dictionary<string, ReasonBuilder>>();
        var delivering = new HashSet<Guid>();
        foreach (var plan in plans)
        {
            foreach (var dependent in plan.Deliveries)
            {
                if (!Guid.TryParse(dependent.Repository, out var dependentRepo)
                    || !running.TryGetValue((dependentRepo, dependent.Flow), out var later))
                {
                    continue;
                }

                delivering.UnionWith(later);
                foreach (var type in plan.WaitsFor(dependent))
                {
                    foreach (var provider in plan.DeliveriesOf(type))
                    {
                        if (!Guid.TryParse(provider.Repository, out var providerRepo)
                            || (providerRepo == dependentRepo && string.Equals(provider.Flow, dependent.Flow, StringComparison.OrdinalIgnoreCase)))
                        {
                            // The interfaces of one source are ordered by the source itself.
                            continue;
                        }

                        var earlier = running.GetValueOrDefault((providerRepo, provider.Flow)) ?? [];
                        foreach (var schedule in later)
                        {
                            if (earlier.Contains(schedule))
                            {
                                // Both run in this schedule, in their repository's waves, which run the provider first.
                                continue;
                            }

                            foreach (var first in earlier.Count == 0 ? [(Guid?)null] : earlier.Select(e => (Guid?)e))
                            {
                                Add(reasons, schedule, first, dependent, type, provider, providerRepo, plan.Partition);
                            }
                        }
                    }
                }
            }
        }

        var chains = Chains(schedules, parents);
        var result = new List<ScheduleOrder>();
        foreach (var schedule in schedules.Where(s => delivering.Contains(s.Id)).OrderBy(s => s.Repository, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var behind = chains.GetValueOrDefault(schedule.Id) ?? [];
            var first = reasons.Keys
                .Where(k => k.Later == schedule.Id && k.Earlier is not null)
                .Select(k => byId[k.Earlier!.Value])
                .Select(earlier => new ScheduleFirst(
                    earlier, earlier.RepoId == schedule.RepoId, Timing(schedule, earlier, behind), Built(reasons[(schedule.Id, earlier.Id)])))
                .OrderBy(f => f.Schedule.Repository, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Schedule.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var unscheduled = reasons.TryGetValue((schedule.Id, null), out var none) ? Built(none) : [];
            result.Add(new ScheduleOrder(schedule, first, unscheduled));
        }

        return new ScheduleOverview(result, Circles(result));
    }

    /// <summary>How the fires of <paramref name="later"/> and of <paramref name="earlier"/>, which runs best before it, fall.</summary>
    private static ScheduleTiming Timing(ScheduleName later, ScheduleName earlier, HashSet<Guid> behind)
    {
        if (behind.Contains(earlier.Id))
        {
            return ScheduleTiming.Chained;
        }

        if (!earlier.Fires)
        {
            return ScheduleTiming.NotFiring;
        }

        if (later.NextFireUtc is not { } laterNext || earlier.NextFireUtc is not { } earlierNext)
        {
            return ScheduleTiming.Unknown;
        }

        return earlierNext <= laterNext ? ScheduleTiming.FiresFirst : ScheduleTiming.FiresAfter;
    }

    /// <summary>
    /// The groups of schedules that each run best before another of the group, so some flow of the group always runs before
    /// a record it refers to whatever their timing: two flows the order cut apart do not appear here, since the order keeps
    /// no reference between them. Found by reachability over the run-first relation, in name order.
    /// </summary>
    private static List<IReadOnlyList<ScheduleName>> Circles(IReadOnlyList<ScheduleOrder> orders)
    {
        var after = orders.ToDictionary(o => o.Schedule.Id, o => o.RunFirst.Select(f => f.Schedule.Id).ToList());
        var reach = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var start in after.Keys)
        {
            var seen = new HashSet<Guid>();
            var pending = new Stack<Guid>(after[start]);
            while (pending.Count > 0)
            {
                var next = pending.Pop();
                if (seen.Add(next) && after.TryGetValue(next, out var further))
                {
                    foreach (var id in further)
                    {
                        pending.Push(id);
                    }
                }
            }

            reach[start] = seen;
        }

        var byId = orders.ToDictionary(o => o.Schedule.Id, o => o.Schedule);
        var placed = new HashSet<Guid>();
        var circles = new List<IReadOnlyList<ScheduleName>>();
        foreach (var order in orders)
        {
            var id = order.Schedule.Id;
            if (placed.Contains(id) || !reach[id].Contains(id))
            {
                continue;
            }

            var circle = reach[id].Where(other => reach.TryGetValue(other, out var back) && back.Contains(id)).ToList();
            placed.UnionWith(circle);
            circles.Add([.. circle.Select(c => byId[c]).OrderBy(s => s.Repository, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)]);
        }

        return circles;
    }

    private static void Add(
        Dictionary<(Guid Later, Guid? Earlier), Dictionary<string, ReasonBuilder>> reasons,
        Guid later,
        Guid? earlier,
        OrderDelivery dependent,
        string type,
        OrderDelivery provider,
        Guid providerRepo,
        string partition)
    {
        if (!reasons.TryGetValue((later, earlier), out var byKey))
        {
            byKey = new Dictionary<string, ReasonBuilder>(StringComparer.OrdinalIgnoreCase);
            reasons[(later, earlier)] = byKey;
        }

        var key = string.Join('\u001f', dependent.Flow, dependent.Interface ?? string.Empty, type, providerRepo.ToString("N"), provider.Flow, provider.Interface ?? string.Empty);
        if (!byKey.TryGetValue(key, out var builder))
        {
            builder = new ReasonBuilder(
                dependent.Flow, dependent.Interface, dependent.EntityType, type, providerRepo, provider.RepositoryName ?? provider.Repository, provider.Flow, provider.Interface);
            byKey[key] = builder;
        }

        builder.Partitions.Add(partition);
    }

    private static List<ScheduleReason> Built(Dictionary<string, ReasonBuilder> builders)
        => [.. builders.Values
            .Select(b => new ScheduleReason(
                b.Flow, b.Interface, b.EntityType, b.RefersTo, b.ProviderRepoId, b.ProviderRepository, b.ProviderFlow, b.ProviderInterface,
                [.. b.Partitions.Order(StringComparer.OrdinalIgnoreCase)]))
            .OrderBy(r => r.Flow, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Interface ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.RefersTo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ProviderFlow, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The schedules each schedule fires after, directly or through the ones it chains behind: parents are named within a
    /// repository, and a name nothing in the repository holds chains behind nothing.
    /// </summary>
    private static Dictionary<Guid, HashSet<Guid>> Chains(IReadOnlyList<ScheduleName> schedules, IReadOnlyList<(Guid ScheduleId, string ParentName)> parents)
    {
        var byName = schedules.GroupBy(s => (s.RepoId, Name: s.Name.ToUpperInvariant())).ToDictionary(g => g.Key, g => g.First().Id);
        var repoOf = schedules.ToDictionary(s => s.Id, s => s.RepoId);
        var direct = new Dictionary<Guid, List<Guid>>();
        foreach (var (scheduleId, parentName) in parents)
        {
            if (repoOf.TryGetValue(scheduleId, out var repo) && byName.TryGetValue((repo, parentName.ToUpperInvariant()), out var parent))
            {
                if (!direct.TryGetValue(scheduleId, out var list))
                {
                    list = [];
                    direct[scheduleId] = list;
                }

                list.Add(parent);
            }
        }

        var chains = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var schedule in direct.Keys)
        {
            // Walked without recursion; a chain declared in a circle stops where it meets itself.
            var behind = new HashSet<Guid>();
            var pending = new Stack<Guid>(direct[schedule]);
            while (pending.Count > 0)
            {
                var next = pending.Pop();
                if (next != schedule && behind.Add(next) && direct.TryGetValue(next, out var further))
                {
                    foreach (var parent in further)
                    {
                        pending.Push(parent);
                    }
                }
            }

            chains[schedule] = behind;
        }

        return chains;
    }

    private sealed class ReasonBuilder(
        string flow, string? @interface, string entityType, string refersTo, Guid providerRepoId, string providerRepository, string providerFlow, string? providerInterface)
    {
        public string Flow { get; } = flow;

        public string? Interface { get; } = @interface;

        public string EntityType { get; } = entityType;

        public string RefersTo { get; } = refersTo;

        public Guid ProviderRepoId { get; } = providerRepoId;

        public string ProviderRepository { get; } = providerRepository;

        public string ProviderFlow { get; } = providerFlow;

        public string? ProviderInterface { get; } = providerInterface;

        public HashSet<string> Partitions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Flows as a repository names them: by repository, and by name ignoring case.</summary>
    private sealed class FlowComparer : IEqualityComparer<(Guid RepoId, string Flow)>
    {
        public static readonly FlowComparer Instance = new();

        public bool Equals((Guid RepoId, string Flow) x, (Guid RepoId, string Flow) y)
            => x.RepoId == y.RepoId && string.Equals(x.Flow, y.Flow, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((Guid RepoId, string Flow) obj) => HashCode.Combine(obj.RepoId, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Flow));
    }
}
