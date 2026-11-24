namespace SqlFlow.ControlPlane.Api;

/// <summary>A flow as the path walk knows it: its identity and its execution wave.</summary>
internal sealed record StreamPathFlow(Guid Id, string Name, string Kind, int Wave);

/// <summary>
/// The lineage the path is walked over: what each flow reads and writes, and the one object each flow is said to write
/// (the board's target), all keyed by lineage object key. Only the flows in <see cref="Flows"/> take part; an edge of a
/// flow outside it (one that left the estate) is ignored.
/// </summary>
internal sealed class StreamPathGraph
{
    public required IReadOnlyDictionary<Guid, StreamPathFlow> Flows { get; init; }

    public required IReadOnlyDictionary<Guid, List<string>> Reads { get; init; }

    public required IReadOnlyDictionary<Guid, List<string>> Writes { get; init; }

    public required IReadOnlyDictionary<Guid, string> Targets { get; init; }
}

/// <summary>Which side of the selected flow a node was reached from.</summary>
internal static class StreamPathRoles
{
    public const string Selected = "selected";

    public const string Upstream = "upstream";

    public const string Downstream = "downstream";
}

/// <summary>
/// What a node stands for without being drawn, so nothing is left off silently: the flows and objects the walk did
/// not draw, named, and counted on the node they were cut from.
/// </summary>
internal static class StreamPathOmissions
{
    /// <summary>Flows that write what this flow reads, beyond the ones drawn.</summary>
    public const string Producers = "producers";

    /// <summary>Objects this flow reads that are not on the path: another flow's output a downstream flow also reads,
    /// or an external input beyond the ones drawn.</summary>
    public const string Inputs = "inputs";

    /// <summary>Objects this flow writes that are not on the path (a view over its table, a staging copy).</summary>
    public const string Outputs = "outputs";

    /// <summary>Flows that read this object and are not drawn: ones that write nothing (a check, a report), and
    /// ones beyond the number drawn.</summary>
    public const string Readers = "readers";

    /// <summary>Flows further upstream than the walk goes.</summary>
    public const string Upstream = "upstream";

    /// <summary>Flows further downstream than the walk goes.</summary>
    public const string Downstream = "downstream";
}

/// <summary>One node the walk drew: a flow, or an object (a table, a view, a file location, a dataset).</summary>
internal sealed class StreamPathWalkNode
{
    public required string Id { get; init; }

    public Guid? FlowId { get; init; }

    public string? ObjectKey { get; init; }

    public required string Role { get; init; }

    /// <summary>An object nothing in the estate writes: where data enters it (a file location, an endpoint).</summary>
    public bool External { get; init; }

    /// <summary>What was left off, by <see cref="StreamPathOmissions"/> kind, each a sorted list of names.</summary>
    public SortedDictionary<string, SortedSet<string>> Omitted { get; } = new(StringComparer.Ordinal);
}

/// <summary>The walk's answer: the nodes in the order they were reached, the edges between them, and whether the selected
/// flow has any lineage at all (a flow whose lineage was never computed is a path of one).</summary>
internal sealed record StreamPathWalkResult(
    IReadOnlyList<StreamPathWalkNode> Nodes,
    IReadOnlyList<(string From, string To)> Edges,
    bool LineageKnown);

/// <summary>
/// The path data takes through one flow: every flow upstream of it back to where the data enters the estate, and every
/// flow downstream of it that carries the data further, with the object each hand-over goes through.
///
/// <para>
/// It is the lineage trace of one flow, cut down to what follows the data. Upstream, everything the flow reads is walked
/// back: the flows that write it and, where nothing does, the external input itself (the file location a pre-ingestion
/// flow lands, an endpoint). Downstream, only the flows that WRITE something are followed, because a flow that only reads
/// (a check, a report) ends the data's journey; those are counted on the object they read, by name. A downstream flow's
/// other inputs are counted on it rather than walked, since walking them would draw every source that meets this one
/// further down instead of this one's path.
/// </para>
///
/// <para>
/// Every hand-over between two flows is drawn through ONE object: the producer's own target when the reader reads it,
/// otherwise the first shared object by key. A flow that writes a table and a view over it, read through the view, is
/// still one line rather than two parallel ones; what it writes besides is counted on it. Fans are bounded
/// (<see cref="MaxBranches"/> flows per fan, <see cref="MaxExternalInputs"/> external inputs per flow) and so is the
/// depth (<see cref="MaxDepth"/> flows each way); what is cut is counted on the node it was cut from, never dropped.
/// </para>
/// </summary>
internal static class StreamPathWalk
{
    /// <summary>Flows followed each way from the selected one. A pre-ingestion, ingestion, delivery chain is three; past
    /// six the drawing is a lineage graph's job.</summary>
    public const int MaxDepth = 6;

    /// <summary>Flows drawn side by side where the data fans in or out.</summary>
    public const int MaxBranches = 4;

    /// <summary>External inputs drawn per flow (file locations, endpoints, datasets nothing here writes).</summary>
    public const int MaxExternalInputs = 3;

    /// <summary>Names listed per omission; the count is always the whole number.</summary>
    public const int MaxNamesListed = 50;

    public static string FlowNodeId(Guid flowId) => "flow:" + flowId.ToString("D");

    public static string ObjectNodeId(string key) => "object:" + key;

    public static StreamPathWalkResult Walk(StreamPathGraph graph, Guid selected)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (!graph.Flows.ContainsKey(selected))
        {
            throw new ArgumentException($"The flow {selected} is not in the graph the path is walked over.", nameof(selected));
        }

        return new Walker(graph, selected).Run();
    }

    private sealed class Walker
    {
        private readonly StreamPathGraph _graph;
        private readonly Guid _selected;
        private readonly Dictionary<string, List<Guid>> _producers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Guid>> _readers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StreamPathWalkNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<StreamPathWalkNode> _order = [];
        private readonly HashSet<string> _edgeSet = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string From, string To)> _edges = [];

        public Walker(StreamPathGraph graph, Guid selected)
        {
            _graph = graph;
            _selected = selected;
            foreach (var flow in graph.Flows.Keys)
            {
                foreach (var key in WritesOf(flow))
                {
                    Add(_producers, key, flow);
                }

                foreach (var key in ReadsOf(flow))
                {
                    Add(_readers, key, flow);
                }
            }
        }

        public StreamPathWalkResult Run()
        {
            Flow(_selected, StreamPathRoles.Selected);
            WalkUpstream();
            WalkDownstream();
            CountUnwalkedInputsAndOutputs();

            var known = ReadsOf(_selected).Count > 0 || WritesOf(_selected).Count > 0;
            return new StreamPathWalkResult(_order, _edges, known);
        }

        private void WalkUpstream()
        {
            var visited = new HashSet<Guid> { _selected };
            var queue = new Queue<(Guid Flow, int Depth)>();
            queue.Enqueue((_selected, 0));
            while (queue.Count > 0)
            {
                var (flow, depth) = queue.Dequeue();
                var reads = ReadsOf(flow);
                var producers = reads
                    .SelectMany(key => _producers.GetValueOrDefault(key) ?? [])
                    .Where(producer => producer != flow)
                    .Distinct()
                    .OrderBy(producer => _graph.Flows[producer].Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // An input nothing else writes is where the data enters: unless the flow writes it itself, which is a
                // flow reading its own output back (a merge into its table), not an input.
                var writes = WritesOf(flow);
                var externals = reads
                    .Where(key => !writes.Contains(key, StringComparer.OrdinalIgnoreCase)
                        && !(_producers.GetValueOrDefault(key) ?? []).Any(producer => producer != flow))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToList();

                var node = FlowNodeId(flow);
                if (depth >= MaxDepth)
                {
                    Omit(node, StreamPathOmissions.Upstream, producers.Select(NameOf));
                    Omit(node, StreamPathOmissions.Inputs, externals);
                    continue;
                }

                foreach (var producer in producers.Take(MaxBranches))
                {
                    var handOver = HandOver(producer, flow);
                    Flow(producer, StreamPathRoles.Upstream);
                    Object(handOver, StreamPathRoles.Upstream, external: false);
                    Edge(FlowNodeId(producer), ObjectNodeId(handOver));
                    Edge(ObjectNodeId(handOver), node);
                    if (visited.Add(producer))
                    {
                        queue.Enqueue((producer, depth + 1));
                    }
                }

                Omit(node, StreamPathOmissions.Producers, producers.Skip(MaxBranches).Select(NameOf));

                foreach (var key in externals.Take(MaxExternalInputs))
                {
                    Object(key, StreamPathRoles.Upstream, external: true);
                    Edge(ObjectNodeId(key), node);
                }

                Omit(node, StreamPathOmissions.Inputs, externals.Skip(MaxExternalInputs));
            }
        }

        private void WalkDownstream()
        {
            var visited = new HashSet<Guid> { _selected };
            var queue = new Queue<(Guid Flow, int Depth)>();
            queue.Enqueue((_selected, 0));
            while (queue.Count > 0)
            {
                var (flow, depth) = queue.Dequeue();
                var node = FlowNodeId(flow);

                // The flow's own target is drawn whether or not anything reads it: it is where this flow's data lands,
                // and on the last flow of a path it is the end of the journey.
                var target = _graph.Targets.GetValueOrDefault(flow);
                if (target is not null)
                {
                    Object(target, StreamPathRoles.Downstream, external: false);
                    Edge(node, ObjectNodeId(target));
                }

                var readers = WritesOf(flow)
                    .SelectMany(key => _readers.GetValueOrDefault(key) ?? [])
                    .Where(reader => reader != flow)
                    .Distinct()
                    .OrderBy(reader => _graph.Flows[reader].Wave)
                    .ThenBy(reader => _graph.Flows[reader].Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Readers that write nothing end the journey here; they are named on the object they read, or on the
                // flow when it has no target to hang them on.
                var carriers = readers.Where(reader => WritesOf(reader).Count > 0).ToList();
                var endpoints = readers.Where(reader => WritesOf(reader).Count == 0).ToList();
                var countOn = target is null ? node : ObjectNodeId(target);
                Omit(countOn, StreamPathOmissions.Readers, endpoints.Select(NameOf));

                if (depth >= MaxDepth)
                {
                    Omit(node, StreamPathOmissions.Downstream, carriers.Select(NameOf));
                    continue;
                }

                foreach (var reader in carriers.Take(MaxBranches))
                {
                    var handOver = HandOver(flow, reader);
                    Object(handOver, StreamPathRoles.Downstream, external: false);
                    Edge(node, ObjectNodeId(handOver));
                    Flow(reader, StreamPathRoles.Downstream);
                    Edge(ObjectNodeId(handOver), FlowNodeId(reader));
                    if (visited.Add(reader))
                    {
                        queue.Enqueue((reader, depth + 1));
                    }
                }

                Omit(countOn, StreamPathOmissions.Readers, carriers.Skip(MaxBranches).Select(NameOf));
            }
        }

        /// <summary>
        /// After both walks: the inputs of the selected and downstream flows that no drawn edge brings in, and the
        /// outputs of those flows that no drawn edge takes out. Upstream flows are left alone; what else they write is
        /// not on this path's way.
        /// </summary>
        private void CountUnwalkedInputsAndOutputs()
        {
            foreach (var node in _order.Where(n => n.FlowId is not null && n.Role != StreamPathRoles.Upstream))
            {
                var flow = node.FlowId!.Value;
                var writes = WritesOf(flow);
                var drawnIn = _edges.Where(e => e.To == node.Id).Select(e => e.From).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var drawnOut = _edges.Where(e => e.From == node.Id).Select(e => e.To).ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (node.Role != StreamPathRoles.Selected)
                {
                    Omit(node.Id, StreamPathOmissions.Inputs, ReadsOf(flow)
                        .Where(key => !drawnIn.Contains(ObjectNodeId(key))
                            && !writes.Contains(key, StringComparer.OrdinalIgnoreCase)));
                }

                Omit(node.Id, StreamPathOmissions.Outputs, writes.Where(key => !drawnOut.Contains(ObjectNodeId(key))));
            }
        }

        /// <summary>The one object a hand-over from <paramref name="producer"/> to <paramref name="reader"/> is drawn
        /// through: the producer's target when the reader reads it, otherwise the first shared object by key.</summary>
        private string HandOver(Guid producer, Guid reader)
        {
            var reads = ReadsOf(reader);
            var target = _graph.Targets.GetValueOrDefault(producer);
            if (target is not null && reads.Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                return target;
            }

            return WritesOf(producer)
                .Where(key => reads.Contains(key, StringComparer.OrdinalIgnoreCase))
                .OrderBy(key => key, StringComparer.Ordinal)
                .First();
        }

        private void Flow(Guid flow, string role)
        {
            var id = FlowNodeId(flow);
            if (!_nodes.ContainsKey(id))
            {
                Remember(new StreamPathWalkNode { Id = id, FlowId = flow, Role = role });
            }
        }

        private void Object(string key, string role, bool external)
        {
            var id = ObjectNodeId(key);
            if (!_nodes.ContainsKey(id))
            {
                Remember(new StreamPathWalkNode { Id = id, ObjectKey = key, Role = role, External = external });
            }
        }

        private void Remember(StreamPathWalkNode node)
        {
            _nodes[node.Id] = node;
            _order.Add(node);
        }

        /// <summary>An edge between two drawn nodes, by the ids the nodes were drawn with, so a key spelled in another
        /// case by one edge of the lineage still names the same node.</summary>
        private void Edge(string from, string to)
        {
            var source = _nodes[from].Id;
            var target = _nodes[to].Id;
            if (_edgeSet.Add(source + "" + target))
            {
                _edges.Add((source, target));
            }
        }

        private void Omit(string nodeId, string what, IEnumerable<string> names)
        {
            var listed = names.ToList();
            if (listed.Count == 0)
            {
                return;
            }

            var node = _nodes[nodeId];
            if (!node.Omitted.TryGetValue(what, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                node.Omitted[what] = set;
            }

            set.UnionWith(listed);
        }

        private string NameOf(Guid flow) => _graph.Flows[flow].Name;

        private List<string> ReadsOf(Guid flow) => _graph.Flows.ContainsKey(flow)
            ? _graph.Reads.GetValueOrDefault(flow) ?? []
            : [];

        private List<string> WritesOf(Guid flow) => _graph.Flows.ContainsKey(flow)
            ? _graph.Writes.GetValueOrDefault(flow) ?? []
            : [];

        private static void Add(Dictionary<string, List<Guid>> index, string key, Guid flow)
        {
            if (!index.TryGetValue(key, out var flows))
            {
                flows = [];
                index[key] = flows;
            }

            if (!flows.Contains(flow))
            {
                flows.Add(flow);
            }
        }
    }
}
