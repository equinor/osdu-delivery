using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;

namespace SqlFlow.Lineage.Graph;

/// <summary>
/// Places an estate's lineage descriptions (flowType: lin) beside the lineage SQLFlow computed. It runs after the graph
/// is built and only reads it: a description that names a flow of the estate is connected to the object that flow
/// already declares, a description that names an object by system and name gets a described object, and each
/// connection becomes an edge with a described relation. Nothing here feeds back into the graph, so flows, objects,
/// edges, dependencies and waves are the same with or without descriptions.
/// <para>
/// A description that cannot be used in full is used in part: a connection that names an unknown flow, or a flow whose
/// object cannot be told from the description, is left out with a finding that says what to write instead, and the
/// rest of the step stays.
/// </para>
/// </summary>
public static class LineageDescriber
{
    /// <summary>The described lineage of <paramref name="descriptions"/> over <paramref name="report"/>.
    /// <paramref name="flows"/> is every flow the estate scan collected, the descriptions' own among them, which tells a
    /// flow the graph left out from one the estate does not declare.</summary>
    public static LineageDescribed Describe(
        LineageReport report, IReadOnlyList<CollectedDescription> descriptions, IReadOnlyList<CollectedFlow> flows)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(descriptions);
        ArgumentNullException.ThrowIfNull(flows);
        if (descriptions.Count == 0)
        {
            return LineageDescribed.Empty;
        }

        var warnings = new List<string>();
        var estate = new Estate(report, flows);
        var kept = Distinct(descriptions, flows, warnings);

        var objects = new Dictionary<string, LineageDescribedObject>(StringComparer.Ordinal);
        var steps = new List<LineageDescribedStep>(kept.Count);
        var edges = new List<LineageEdge>();
        var columns = new List<LineageColumnMapping>();

        foreach (var (flow, file) in kept)
        {
            var who = $"{file}: lineage description '{flow.Name}'";
            var reads = new List<LineageDescribedEndpoint>();
            var byAlias = new Dictionary<string, LineageDescribedEndpoint>(StringComparer.OrdinalIgnoreCase);
            var readKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < flow.Reads.Count; i++)
            {
                var entry = flow.Reads[i];
                var placed = Place(entry, flow, isRead: true, $"{who} reads entry {i + 1}", estate, objects, warnings);
                if (placed is null)
                {
                    continue;
                }

                if (!readKeys.Add(placed.ObjectKey))
                {
                    warnings.Add($"{who} reads entry {i + 1} is the same object as an earlier reads entry ('{placed.ObjectName}'); it is counted once.");

                    // Its alias still names that object, so a column mapped through it keeps its source.
                    if (placed.Alias is { } again)
                    {
                        byAlias.TryAdd(again, placed);
                    }

                    continue;
                }

                reads.Add(placed);
                if (placed.Alias is { } alias)
                {
                    byAlias[alias] = placed;
                }

                edges.Add(Edge(flow.Name, LineageRelation.DescribedReads, placed.ObjectKey));
            }

            var writes = new List<LineageDescribedEndpoint>();
            var writeKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < flow.Writes.Count; i++)
            {
                var entry = flow.Writes[i];
                var where = $"{who} writes entry {i + 1}";
                var placed = Place(entry, flow, isRead: false, where, estate, objects, warnings);
                if (placed is null)
                {
                    if (entry.Columns.Count > 0)
                    {
                        warnings.Add($"{where} could not be placed, so its {entry.Columns.Count} column mapping(s) are left out with it.");
                    }

                    continue;
                }

                if (!writeKeys.Add(placed.ObjectKey))
                {
                    warnings.Add($"{where} is the same object as an earlier writes entry ('{placed.ObjectName}'); it is counted once, and its column mappings are left out.");
                    continue;
                }

                writes.Add(placed);
                edges.Add(Edge(flow.Name, LineageRelation.DescribedWrites, placed.ObjectKey));
                columns.AddRange(Columns(flow, entry, placed, byAlias, where, warnings));
            }

            steps.Add(new LineageDescribedStep
            {
                Name = flow.Name,
                File = file,
                System = flow.System,
                Unit = flow.Unit,
                Description = flow.Description,
                Owner = flow.Owner,
                Url = flow.Url,
                Batch = flow.Batch,
                Evidence = flow.Evidence,
                Reads = reads,
                Writes = writes,
            });
        }

        return new LineageDescribed
        {
            Steps = [.. steps.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)],
            Objects = [.. Bounded(objects.Values, steps).OrderBy(o => o.Key, StringComparer.Ordinal)],
            Edges =
            [
                .. edges
                    .OrderBy(e => e.Flow, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Relation)
                    .ThenBy(e => e.ObjectKey, StringComparer.Ordinal),
            ],
            Columns = [.. columns.OrderBy(c => c.Flow, StringComparer.OrdinalIgnoreCase)],
            Warnings = warnings,
        };
    }

    /// <summary>The descriptions to use: one per name, and none that takes the name of a flow that runs. A pipeline is
    /// identified by its name, so two documents under one name cannot both be kept; the first in path order is.</summary>
    private static List<CollectedDescription> Distinct(
        IReadOnlyList<CollectedDescription> descriptions, IReadOnlyList<CollectedFlow> flows, List<string> warnings)
    {
        var running = flows
            .Where(f => !IsDescription(f))
            .GroupBy(f => f.Node.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Node, StringComparer.OrdinalIgnoreCase);

        var kept = new List<CollectedDescription>();
        foreach (var group in descriptions.GroupBy(d => d.Flow.Name, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (running.TryGetValue(first.Flow.Name, out var taken))
            {
                warnings.Add(
                    $"{first.File}: lineage description '{first.Flow.Name}' has the name of the {taken.Kind} flow in {taken.File}; "
                    + "a pipeline is identified by its name, so the description is left out of lineage until it has a name of its own.");
                continue;
            }

            if (group.Count() > 1)
            {
                warnings.Add(
                    $"lineage description '{first.Flow.Name}' is declared by {group.Count()} documents "
                    + $"({string.Join(", ", group.Select(d => d.File))}); the first is used, and the others are left out of lineage.");
            }

            kept.Add(first);
        }

        return kept;
    }

    private static bool IsDescription(CollectedFlow flow) => flow.IsLineageDescription;

    /// <summary>Where an entry of a description lands: the object a flow of the estate declares, or a described object.
    /// Null, with a finding, when it names a flow whose object cannot be told.</summary>
    private static LineageDescribedEndpoint? Place(
        LineageDescriptionEndpoint entry, LineageDescriptionFlow flow, bool isRead, string where, Estate estate,
        Dictionary<string, LineageDescribedObject> objects, List<string> warnings)
    {
        if (!entry.NamesFlow)
        {
            var system = entry.System ?? flow.System;
            var name = entry.Name!;
            var key = NodeKey.For(LineageDescribedObject.ServerRef(system), null, null, name);
            objects.TryAdd(key, new LineageDescribedObject { Key = key, System = system, Name = name });
            return new LineageDescribedEndpoint
            {
                ObjectKey = key,
                ObjectName = objects[key].Name,
                Alias = entry.Alias,
                Note = entry.Note,
                Origin = entry.Origin,
            };
        }

        var named = entry.Flow!;
        var keyword = isRead ? "fedBy" : "feeds";
        var does = isRead ? "writes" : "reads";
        switch (estate.Find(named))
        {
            case FlowStanding.Unknown:
                warnings.Add($"{where} names flow '{named}' with '{keyword}', which this repository does not declare, so the connection is left out. Check the flow's name.");
                return null;
            case FlowStanding.Description:
                warnings.Add($"{where} names '{named}' with '{keyword}', which is itself a lineage description, so the connection is left out. Two descriptions meet on an object: name the same object in one's writes and the other's reads.");
                return null;
            case FlowStanding.OutsideLineage:
                warnings.Add($"{where} names flow '{named}' with '{keyword}', which takes no part in lineage (it moves no data), so the connection is left out.");
                return null;
        }

        var candidates = estate.Objects(named, isRead ? LineageRelation.Writes : LineageRelation.Reads);
        if (candidates.Count == 0)
        {
            warnings.Add($"{where} names flow '{named}' with '{keyword}', but lineage holds nothing that flow {does}, so the connection is left out.");
            return null;
        }

        var matching = entry.At is { } at ? Narrow(candidates, at) : candidates;
        if (matching.Count != 1)
        {
            var listed = string.Join(", ", candidates.Take(8).Select(c => $"'{c.Shown}'")) + (candidates.Count > 8 ? ", ..." : string.Empty);
            warnings.Add(entry.At is null
                ? $"{where} names flow '{named}' with '{keyword}', and that flow {does} {candidates.Count} objects ({listed}), so the connection is left out. Add 'at' with a part of the name of the one meant."
                : matching.Count == 0
                    ? $"{where} names flow '{named}' with '{keyword}' at '{entry.At}', which matches none of the objects that flow {does} ({listed}), so the connection is left out."
                    : $"{where} names flow '{named}' with '{keyword}' at '{entry.At}', which matches {matching.Count} of the objects that flow {does} ({listed}), so the connection is left out. Give 'at' more of the name.");
            return null;
        }

        return new LineageDescribedEndpoint
        {
            ObjectKey = matching[0].Key,
            ObjectName = matching[0].Shown,
            ViaFlow = estate.Name(named),
            Alias = entry.Alias,
            Note = entry.Note,
        };
    }

    /// <summary>The candidates <paramref name="at"/> names: the one whose name it is, or else those whose name or key
    /// holds it. A whole name wins over a part, so <c>Orders</c> means the table and not also a view named after it.</summary>
    private static List<EstateObject> Narrow(List<EstateObject> candidates, string at)
    {
        var exact = candidates
            .Where(c => string.Equals(c.Name, at, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Shown, at, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return exact.Count > 0
            ? exact
            : [.. candidates.Where(c => c.Shown.Contains(at, StringComparison.OrdinalIgnoreCase) || c.Key.Contains(at, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>The column mappings of one written object. A source whose read object could not be placed is left out
    /// with a finding; the mapping keeps the sources that could.</summary>
    private static IEnumerable<LineageColumnMapping> Columns(
        LineageDescriptionFlow flow, LineageDescriptionEndpoint entry, LineageDescribedEndpoint target,
        Dictionary<string, LineageDescribedEndpoint> byAlias, string where, List<string> warnings)
    {
        var ordinal = 0;
        var unplaced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in entry.Columns)
        {
            ordinal++;
            var sources = new List<LineageColumnSource>(column.From.Count);
            foreach (var source in column.From)
            {
                if (!byAlias.TryGetValue(source.Alias, out var read))
                {
                    unplaced.Add(source.Alias);
                    continue;
                }

                sources.Add(new LineageColumnSource { ObjectKey = read.ObjectKey, ObjectName = read.ObjectName, Column = source.Column });
            }

            yield return new LineageColumnMapping
            {
                Flow = flow.Name,
                TargetObjectKey = target.ObjectKey,
                TargetObjectName = target.ObjectName,
                TargetColumn = column.Name,
                Ordinal = ordinal,
                Sources = sources,
                Note = column.Note,
            };
        }

        if (unplaced.Count > 0)
        {
            warnings.Add($"{where} maps columns from {string.Join(", ", unplaced.Select(a => $"'{a}'"))}, whose reads entry could not be placed; those source columns are left out of the mappings.");
        }
    }

    /// <summary>Each described object with where its description ends: an object a step reads that no step writes is the
    /// start of the described chain, marked as the origin when a read says so and left open otherwise.</summary>
    private static IEnumerable<LineageDescribedObject> Bounded(
        IEnumerable<LineageDescribedObject> objects, List<LineageDescribedStep> steps)
    {
        var written = steps.SelectMany(s => s.Writes).Select(w => w.ObjectKey).ToHashSet(StringComparer.Ordinal);
        var read = steps.SelectMany(s => s.Reads).ToLookup(r => r.ObjectKey, StringComparer.Ordinal);
        foreach (var described in objects)
        {
            var readers = read[described.Key].ToList();
            yield return described with
            {
                Boundary = written.Contains(described.Key) || readers.Count == 0
                    ? LineageDescribedBoundary.None
                    : readers.Any(r => r.Origin) ? LineageDescribedBoundary.Origin : LineageDescribedBoundary.Open,
            };
        }
    }

    private static LineageEdge Edge(string flow, LineageRelation relation, string objectKey)
        => new() { Flow = flow, Relation = relation, ObjectKey = objectKey, Tier = LineageTier.Declared };

    private enum FlowStanding
    {
        /// <summary>A flow of the computed graph.</summary>
        InLineage,

        /// <summary>No document of the repository declares it.</summary>
        Unknown,

        /// <summary>A lineage description.</summary>
        Description,

        /// <summary>A flow the graph leaves out because it moves no data (a source-control snapshot, a schema
        /// registration).</summary>
        OutsideLineage,
    }

    /// <summary>One object of the computed graph a flow reads or writes.</summary>
    private sealed record EstateObject(string Key, string Name, string Shown);

    /// <summary>What the describer reads of the computed graph: its flows by name, and each flow's own objects.</summary>
    private sealed class Estate
    {
        private readonly Dictionary<string, string> _graphFlows;
        private readonly Dictionary<string, CollectedFlow> _collected;
        private readonly Dictionary<string, LineageObjectNode> _objects;
        private readonly ILookup<string, LineageEdge> _edges;

        public Estate(LineageReport report, IReadOnlyList<CollectedFlow> flows)
        {
            _graphFlows = report.Flows
                .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            _collected = flows
                .GroupBy(f => f.Node.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _objects = report.Objects
                .GroupBy(o => o.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // A flow's own facts only: what a view or a procedure adds under it (ViaModule) is the module's, and a
            // description that means a base table names the flow loading it.
            _edges = report.Edges
                .Where(e => e.Flow is not null && e.ViaModule is null)
                .ToLookup(e => e.Flow!, StringComparer.OrdinalIgnoreCase);
        }

        public FlowStanding Find(string flow)
        {
            if (_graphFlows.ContainsKey(flow))
            {
                return FlowStanding.InLineage;
            }

            if (!_collected.TryGetValue(flow, out var collected))
            {
                return FlowStanding.Unknown;
            }

            return IsDescription(collected) ? FlowStanding.Description : FlowStanding.OutsideLineage;
        }

        /// <summary>The flow's name as its document spells it.</summary>
        public string Name(string flow) => _graphFlows.GetValueOrDefault(flow, flow);

        /// <summary>
        /// The objects <paramref name="flow"/> reads (<see cref="LineageRelation.Reads"/>) or writes
        /// (<see cref="LineageRelation.Writes"/>, which takes a created object too), in key order. What the flow's
        /// document declares is preferred; a flow known only from what its runs did is read from those.
        /// </summary>
        public List<EstateObject> Objects(string flow, LineageRelation relation)
        {
            var own = _edges[flow]
                .Where(e => relation == LineageRelation.Reads
                    ? e.Relation == LineageRelation.Reads
                    : e.Relation is LineageRelation.Writes or LineageRelation.Creates)
                .ToList();
            var declared = own.Where(e => e.Tier == LineageTier.Declared).ToList();
            return
            [
                .. (declared.Count > 0 ? declared : own)
                    .Select(e => e.ObjectKey)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .Select(k => _objects.TryGetValue(k, out var node)
                        ? new EstateObject(k, node.Name, Shown(node))
                        : new EstateObject(k, k, k)),
            ];
        }

        /// <summary>An object's name as it is shown: qualified by its database and schema when it has them.</summary>
        private static string Shown(LineageObjectNode node)
            => string.Join('.', new[] { node.Database, node.Schema, node.Name }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}
