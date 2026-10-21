using System.Text;

namespace SqlFlow.Delivery.Order;

/// <summary>
/// One delivery of a partition, as the order sees it: a flow (or one interface of a source) of a repository writing
/// records of one kind, with the properties its mapping fills. <see cref="Filled"/> null means the mapping is not known,
/// and the delivery is taken to refer to everything its schema allows.
/// </summary>
public sealed record OrderDelivery
{
    /// <summary>The partition the delivery writes to.</summary>
    public required string Partition { get; init; }

    /// <summary>The repository the flow is synced from; empty for the folder a command scans.</summary>
    public string Repository { get; init; } = string.Empty;

    /// <summary>The repository's name as messages give it, when it is known; <see cref="Repository"/> identifies it either way.</summary>
    public string? RepositoryName { get; init; }

    /// <summary>The flow's name.</summary>
    public required string Flow { get; init; }

    /// <summary>The interface of a source the delivery is, or null for a flow of the single form.</summary>
    public string? Interface { get; init; }

    /// <summary>The kind the delivery's mapping fills.</summary>
    public required string Kind { get; init; }

    /// <summary>The template paths the mapping fills, or null when the mapping is not known.</summary>
    public IReadOnlyCollection<string>? Filled { get; init; }

    /// <summary>The template paths the mapping fills by a search on the platform.</summary>
    public IReadOnlyCollection<string> Searched { get; init; } = [];

    /// <summary>The entity type the delivery writes.</summary>
    public string EntityType => OsduEntity.OfKind(Kind) ?? string.Empty;

    /// <summary>The flow, with the interface when there is one, as messages name it: <c>flow</c> or <c>flow/interface</c>.</summary>
    public string Label => Interface is { Length: > 0 } name ? $"{Flow}/{name}" : Flow;

    /// <summary>The label, with the repository when there is one: <c>repository: flow/interface</c>.</summary>
    public string FullLabel => RepositoryName is { Length: > 0 } name ? $"{name}: {Label}" : Repository.Length > 0 ? $"{Repository}: {Label}" : Label;

    internal (string Repository, string Flow, string Interface) Identity => (Repository.ToUpperInvariant(), Flow.ToUpperInvariant(), (Interface ?? string.Empty).ToUpperInvariant());
}

/// <summary>Why one entity type refers to another: the delivery and the property of its mapping that holds the reference.</summary>
public sealed record OrderReason(OrderDelivery Delivery, string Property, bool Searched);

/// <summary>
/// A reference between two entity types of a partition: records of <see cref="From"/> refer to records of <see cref="To"/>,
/// so records of <see cref="To"/> go first. The reasons name every delivery and property that makes the reference.
/// </summary>
public sealed record OrderEdge(string From, string To, IReadOnlyList<OrderReason> Because)
{
    /// <summary>Whether any of the references is filled by a search, which lineage orders by the searched kind already.</summary>
    public bool Searched => Because.Any(b => b.Searched);

    public override string ToString() => $"{From} -> {To}";
}

/// <summary>Why a reference was not made part of the order.</summary>
public enum OrderCutReason
{
    /// <summary>A reference from an earlier OSDU group to a later one, inside a cycle: it points back, and resolves once the other side is delivered.</summary>
    BackReference,

    /// <summary>A reference no search backs, inside a cycle of one group whose other side a search backs: the search's order wins.</summary>
    GivesWayToSearch,

    /// <summary>A reference in a cycle of one group that nothing resolves: no order is taken, and the records wait for each other one by one.</summary>
    Circular,
}

/// <summary>A reference the order leaves out, with the rule that left it out.</summary>
public sealed record OrderCut(OrderEdge Edge, OrderCutReason Reason, string Why);

/// <summary>
/// The order records must reach one partition in: the entity types its deliveries write, the references between them the
/// order keeps (acyclic), the ones it cut and why, the types that refer to themselves, and the deliveries whose references
/// could not be read. Built by <see cref="OsduOrder.Plan"/>.
/// </summary>
public sealed class OrderPlan
{
    private readonly HashSet<(string From, string To)> _kept;
    private readonly Dictionary<(string Repository, string Flow, string Interface), IReadOnlySet<string>> _waits;
    private readonly Dictionary<string, int> _levels;

    internal OrderPlan(
        string partition,
        IReadOnlyList<OrderDelivery> deliveries,
        IReadOnlyList<string> types,
        IReadOnlyList<OrderEdge> edges,
        IReadOnlyList<OrderCut> cuts,
        IReadOnlyList<OrderEdge> selfReferences,
        IReadOnlyList<OrderDelivery> unknown,
        IReadOnlyList<string> warnings,
        Dictionary<(string Repository, string Flow, string Interface), IReadOnlySet<string>> waits,
        Dictionary<string, int> levels)
    {
        Partition = partition;
        Deliveries = deliveries;
        Types = types;
        Edges = edges;
        Cuts = cuts;
        SelfReferences = selfReferences;
        Unknown = unknown;
        Warnings = warnings;
        _waits = waits;
        _levels = levels;
        _kept = edges.Select(e => (Key(e.From), Key(e.To))).ToHashSet();
    }

    public string Partition { get; }

    /// <summary>Every delivery of the partition, in the order given.</summary>
    public IReadOnlyList<OrderDelivery> Deliveries { get; }

    /// <summary>The entity types the partition's deliveries write, in ordinal order.</summary>
    public IReadOnlyList<string> Types { get; }

    /// <summary>The references the order keeps: an acyclic graph, in (from, to) order.</summary>
    public IReadOnlyList<OrderEdge> Edges { get; }

    /// <summary>The references the order cut, in the order they were cut.</summary>
    public IReadOnlyList<OrderCut> Cuts { get; }

    /// <summary>Entity types whose records refer to records of their own type: ordered by record waits, never by the order.</summary>
    public IReadOnlyList<OrderEdge> SelfReferences { get; }

    /// <summary>Deliveries whose kind's schema the graph does not hold: what they refer to is not known.</summary>
    public IReadOnlyList<OrderDelivery> Unknown { get; }

    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The entity types level by level: each level's types refer only to types of earlier levels.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Levels
        => [.. Types.GroupBy(t => _levels[Key(t)]).OrderBy(g => g.Key).Select(g => (IReadOnlyList<string>)[.. g.OrderBy(t => t, StringComparer.Ordinal)])];

    /// <summary>The level of <paramref name="entityType"/>, from 0, or -1 when no delivery of the partition writes it.</summary>
    public int LevelOf(string entityType) => _levels.TryGetValue(Key(entityType), out var level) ? level : -1;

    /// <summary>Whether records of <paramref name="from"/> wait, by the order, for records of <paramref name="to"/>.</summary>
    public bool Keeps(string from, string to) => _kept.Contains((Key(from), Key(to)));

    /// <summary>Whether a delivery of the partition writes <paramref name="entityType"/>.</summary>
    public bool Delivers(string entityType) => _levels.ContainsKey(Key(entityType));

    /// <summary>The deliveries of the partition writing <paramref name="entityType"/>.</summary>
    public IReadOnlyList<OrderDelivery> DeliveriesOf(string entityType)
        => [.. Deliveries.Where(d => string.Equals(d.EntityType, entityType, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// The entity types <paramref name="delivery"/> waits for: the types its own references point to that another delivery
    /// of the partition writes and the order keeps. Empty for a delivery the plan does not hold.
    /// </summary>
    public IReadOnlySet<string> WaitsFor(OrderDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return _waits.TryGetValue(delivery.Identity, out var waits) ? waits : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The plan as <c>sqlflow order</c> prints it: the levels, the references kept and cut, and the warnings.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append("partition ").Append(Partition).Append(": ").Append(Types.Count).Append(" entity type(s) from ").Append(Deliveries.Count).Append(" delivery(ies)\n");
        var levels = Levels;
        for (var i = 0; i < levels.Count; i++)
        {
            text.Append("  level ").Append(i + 1).Append(": ").Append(string.Join(", ", levels[i])).Append('\n');
        }

        foreach (var edge in Edges)
        {
            text.Append("  ").Append(edge.From).Append(" waits for ").Append(edge.To).Append(": ")
                .Append(string.Join("; ", edge.Because.Select(b => $"{b.Delivery.FullLabel} {b.Property}{(b.Searched ? " (searched)" : string.Empty)}").Distinct(StringComparer.Ordinal)))
                .Append('\n');
        }

        foreach (var cut in Cuts)
        {
            text.Append("  not ordered: ").Append(cut.Edge.From).Append(" -> ").Append(cut.Edge.To).Append(": ").Append(cut.Why).Append('\n');
        }

        foreach (var self in SelfReferences)
        {
            text.Append("  ").Append(self.From).Append(" refers to its own type (")
                .Append(string.Join("; ", self.Because.Select(b => $"{b.Delivery.FullLabel} {b.Property}").Distinct(StringComparer.Ordinal)))
                .Append("): its records are ordered by record waits\n");
        }

        foreach (var warning in Warnings)
        {
            text.Append("  WARN  ").Append(warning).Append('\n');
        }

        return text.ToString();
    }

    internal static string Key(string entityType) => entityType.ToUpperInvariant();
}

/// <summary>
/// Builds the order of a partition from its deliveries and OSDU's schemas (osdu/docs/reference/concepts/osdu-order.md):
/// the entity types the deliveries write, a reference between two of them wherever a delivery's mapping fills a property
/// whose schema points from one to the other, and the cycles among them cut by fixed rules, so the result is acyclic and
/// the same whichever repository asks.
/// </summary>
public static class OsduOrder
{
    /// <summary>
    /// The partition a delivery names when it leaves its partitions to the registry: it delivers to every registered
    /// partition, so it is part of the order of each partition the other deliveries name as well as of its own.
    /// </summary>
    public const string EveryPartition = "*";

    /// <summary>
    /// The plan of every partition the deliveries write to, in partition order, with each delivery to
    /// <see cref="EveryPartition"/> also a delivery of every partition another delivery names. Lineage, the repository
    /// sync and the order pages all plan through here, so they see one order.
    /// </summary>
    public static IReadOnlyList<OrderPlan> PlanPartitions(IEnumerable<OrderDelivery> deliveries, OsduSchemaGraph graph)
    {
        ArgumentNullException.ThrowIfNull(deliveries);
        ArgumentNullException.ThrowIfNull(graph);
        var all = deliveries.ToList();
        var every = all.Where(d => string.Equals(d.Partition, EveryPartition, StringComparison.Ordinal)).ToList();
        var named = all.Select(d => d.Partition)
            .Where(p => !string.Equals(p, EveryPartition, StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return PlanAll(all.Concat(named.SelectMany(p => every.Select(d => d with { Partition = p }))), graph);
    }

    /// <summary>The plan of every partition the deliveries write to, in partition order.</summary>
    public static IReadOnlyList<OrderPlan> PlanAll(IEnumerable<OrderDelivery> deliveries, OsduSchemaGraph graph)
    {
        ArgumentNullException.ThrowIfNull(deliveries);
        ArgumentNullException.ThrowIfNull(graph);
        return [.. deliveries
            .GroupBy(d => d.Partition, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => Plan(g.Key, g, graph))];
    }

    /// <summary>
    /// The order of <paramref name="partition"/>, over the deliveries that write to it (others are ignored). A delivery
    /// whose kind is not an OSDU kind is refused; one whose kind's schema the graph does not hold is listed as unknown,
    /// with a warning, and orders nothing.
    /// </summary>
    public static OrderPlan Plan(string partition, IEnumerable<OrderDelivery> deliveries, OsduSchemaGraph graph)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(deliveries);
        ArgumentNullException.ThrowIfNull(graph);
        var mine = new List<OrderDelivery>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var delivery in deliveries)
        {
            ArgumentNullException.ThrowIfNull(delivery);
            if (!string.Equals(delivery.Partition, partition, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (OsduEntity.OfKind(delivery.Kind) is null)
            {
                throw new ArgumentException($"Delivery {delivery.FullLabel} writes '{delivery.Kind}', which is not an OSDU kind.", nameof(deliveries));
            }

            if (seen.Add(delivery.Identity))
            {
                mine.Add(delivery);
            }
        }

        var types = mine.Select(d => d.EntityType).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var canonical = types.ToDictionary(OrderPlan.Key, t => t);
        var warnings = new List<string>();
        var unknown = new List<OrderDelivery>();
        var reasons = new SortedDictionary<(string From, string To), List<OrderReason>>(Comparer<(string, string)>.Create(Compare));
        var self = new SortedDictionary<string, List<OrderReason>>(StringComparer.Ordinal);
        var referred = new Dictionary<(string, string, string), HashSet<string>>();
        foreach (var delivery in mine)
        {
            var relationships = graph.RelationshipsOf(delivery.Kind, delivery.Filled);
            if (relationships is null)
            {
                unknown.Add(delivery);
                warnings.Add($"{delivery.FullLabel} writes {delivery.Kind}, whose schema is not known (no template folder holds its template, and no built release of OSDU's lineage holds it), so what its records refer to is not ordered.");
                referred[delivery.Identity] = [];
                continue;
            }

            var from = canonical[OrderPlan.Key(delivery.EntityType)];
            var searched = new HashSet<string>(delivery.Searched, StringComparer.Ordinal);
            var targetsOfDelivery = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relationship in relationships)
            {
                foreach (var to in types.Where(t => relationship.Targets.Any(target => OsduEntity.Names(target, t))))
                {
                    var reason = new OrderReason(delivery, relationship.Property, searched.Contains(relationship.Property));
                    if (string.Equals(to, from, StringComparison.OrdinalIgnoreCase))
                    {
                        Add(self, from, reason);
                        continue;
                    }

                    targetsOfDelivery.Add(to);
                    if (!reasons.TryGetValue((from, to), out var list))
                    {
                        reasons[(from, to)] = list = [];
                    }

                    list.Add(reason);
                }
            }

            referred[delivery.Identity] = targetsOfDelivery;
        }

        var edges = reasons.Select(r => new OrderEdge(r.Key.From, r.Key.To, r.Value)).ToList();
        var cuts = Cut(types, edges, warnings);
        var kept = edges.Where(e => !cuts.Any(c => ReferenceEquals(c.Edge, e))).ToList();
        var keptKeys = kept.Select(e => (OrderPlan.Key(e.From), OrderPlan.Key(e.To))).ToHashSet();
        var waits = new Dictionary<(string, string, string), IReadOnlySet<string>>();
        foreach (var delivery in mine)
        {
            var own = OrderPlan.Key(delivery.EntityType);
            waits[delivery.Identity] = referred[delivery.Identity]
                .Where(to => keptKeys.Contains((own, OrderPlan.Key(to))))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return new OrderPlan(
            partition,
            mine,
            types,
            kept,
            cuts,
            [.. self.Select(s => new OrderEdge(s.Key, s.Key, s.Value))],
            unknown,
            warnings,
            waits,
            Levels(types, kept));
    }

    /// <summary>
    /// Cuts the cycles of <paramref name="edges"/> one at a time, the first found in ordinal order, until none is left. In a
    /// cycle the references from an earlier group to a later one are cut; failing that, when some references of the cycle
    /// are searched and others not, the ones not searched; failing that, every reference of the cycle, with a warning.
    /// </summary>
    private static List<OrderCut> Cut(IReadOnlyList<string> types, IReadOnlyList<OrderEdge> edges, List<string> warnings)
    {
        var cuts = new List<OrderCut>();
        var live = edges.ToList();
        // Every round cuts at least one reference, so the rounds end.
        for (var round = 0; round <= edges.Count; round++)
        {
            if (FindCycle(types, live) is not { } cycle)
            {
                return cuts;
            }

            var names = string.Join(" -> ", cycle.Select(e => e.From).Append(cycle[0].From));
            var back = cycle.Where(e => OsduEntity.RankOf(e.From) < OsduEntity.RankOf(e.To)).ToList();
            List<(OrderEdge Edge, OrderCutReason Reason, string Why)> cut;
            if (back.Count > 0)
            {
                cut = [.. back.Select(e => (e, OrderCutReason.BackReference,
                    $"{e.From} ({OsduEntity.GroupOf(e.From)}) refers to {e.To} ({OsduEntity.GroupOf(e.To)}), a later group, in the cycle {names}: not waited for, and its records go first"))];
            }
            else if (cycle.Any(e => e.Searched) && cycle.Any(e => !e.Searched))
            {
                cut = [.. cycle.Where(e => !e.Searched).Select(e => (e, OrderCutReason.GivesWayToSearch,
                    $"in the cycle {names}, {e.From} -> {e.To} is filled without a search and gives way to the references a search fills"))];
            }
            else
            {
                warnings.Add($"{string.Join(", ", cycle.Select(e => e.From).Distinct(StringComparer.Ordinal))} refer to each other in a circle ({names}) and nothing says which goes first: their deliveries are not ordered against each other, and a record waits for one it refers to only once its flow has put that record in the ledger.");
                cut = [.. cycle.Select(e => (e, OrderCutReason.Circular, $"part of the cycle {names}, which nothing orders"))];
            }

            foreach (var (edge, reason, why) in cut)
            {
                cuts.Add(new OrderCut(edge, reason, why));
                live.Remove(edge);
            }
        }

        throw new InvalidOperationException("Cutting the cycles of the order did not end; this is a defect of the order.");
    }

    /// <summary>The first cycle of <paramref name="edges"/>, searching from the types in ordinal order, as its references in order; null when there is none.</summary>
    private static List<OrderEdge>? FindCycle(IReadOnlyList<string> types, IReadOnlyList<OrderEdge> edges)
    {
        var outgoing = types.ToDictionary(OrderPlan.Key, _ => new List<OrderEdge>());
        foreach (var edge in edges.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal))
        {
            outgoing[OrderPlan.Key(edge.From)].Add(edge);
        }

        var state = new Dictionary<string, int>();
        foreach (var start in types.Select(OrderPlan.Key))
        {
            if (state.ContainsKey(start))
            {
                continue;
            }

            // An iterative depth-first walk: the path of references taken, and for each type on it the next reference to try.
            var path = new List<OrderEdge>();
            var stack = new Stack<(string Node, int Next)>();
            stack.Push((start, 0));
            state[start] = 1;
            while (stack.Count > 0)
            {
                var (node, next) = stack.Pop();
                var list = outgoing[node];
                if (next >= list.Count)
                {
                    state[node] = 2;
                    if (path.Count > 0)
                    {
                        path.RemoveAt(path.Count - 1);
                    }

                    continue;
                }

                stack.Push((node, next + 1));
                var edge = list[next];
                var to = OrderPlan.Key(edge.To);
                if (state.TryGetValue(to, out var seen))
                {
                    if (seen == 1)
                    {
                        // A reference back to a type on the path: the cycle is that part of the path, closed by this reference.
                        var start2 = path.FindIndex(e => string.Equals(OrderPlan.Key(e.From), to, StringComparison.Ordinal));
                        var cycle = start2 < 0 ? [] : path.Skip(start2).ToList();
                        cycle.Add(edge);
                        return cycle;
                    }

                    continue;
                }

                path.Add(edge);
                state[to] = 1;
                stack.Push((to, 0));
            }
        }

        return null;
    }

    private static Dictionary<string, int> Levels(IReadOnlyList<string> types, IReadOnlyList<OrderEdge> kept)
    {
        var dependsOn = types.ToDictionary(OrderPlan.Key, _ => new List<string>());
        foreach (var edge in kept)
        {
            dependsOn[OrderPlan.Key(edge.From)].Add(OrderPlan.Key(edge.To));
        }

        var levels = new Dictionary<string, int>();
        foreach (var type in types.Select(OrderPlan.Key))
        {
            LevelOf(type, dependsOn, levels);
        }

        return levels;
    }

    private static int LevelOf(string type, Dictionary<string, List<string>> dependsOn, Dictionary<string, int> levels)
    {
        if (levels.TryGetValue(type, out var known))
        {
            return known;
        }

        // The kept references are acyclic, so the walk ends; it is iterative to stay safe on long chains.
        var stack = new Stack<(string Node, bool Expanded)>();
        stack.Push((type, false));
        while (stack.Count > 0)
        {
            var (node, expanded) = stack.Pop();
            if (levels.ContainsKey(node))
            {
                continue;
            }

            var pending = dependsOn[node].Where(d => !levels.ContainsKey(d)).ToList();
            if (!expanded && pending.Count > 0)
            {
                stack.Push((node, true));
                foreach (var dependency in pending)
                {
                    stack.Push((dependency, false));
                }

                continue;
            }

            levels[node] = dependsOn[node].Count == 0 ? 0 : dependsOn[node].Max(d => levels[d]) + 1;
        }

        return levels[type];
    }

    private static void Add(SortedDictionary<string, List<OrderReason>> self, string type, OrderReason reason)
    {
        if (!self.TryGetValue(type, out var list))
        {
            self[type] = list = [];
        }

        list.Add(reason);
    }

    private static int Compare((string From, string To) a, (string From, string To) b)
    {
        var from = string.CompareOrdinal(a.From, b.From);
        return from != 0 ? from : string.CompareOrdinal(a.To, b.To);
    }
}
