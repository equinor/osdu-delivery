using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The path one stream's data takes: back to where it enters the estate, forward through every flow that carries it on,
/// each hand-over drawn through one object, and whatever is not drawn counted on the node it was cut from.
/// </summary>
public sealed class StreamPathWalkTests
{
    private const string Files = "file|||shop/orders";
    private const string PreTable = "srv|db|pre|orders";
    private const string PreView = "srv|db|pre|v_orders";
    private const string ArcTable = "srv|db|arc|orders";
    private const string Export = "file|||exports/orders";

    /// <summary>A small estate: files landed by a pre flow (a table and a view over it), keyed by an ingestion flow
    /// that reads both, exported by a flow that also reads a second archive table, and checked by a flow that writes
    /// nothing.</summary>
    private sealed class Estate
    {
        public Dictionary<Guid, StreamPathFlow> Flows { get; } = [];

        public Dictionary<Guid, List<string>> Reads { get; } = [];

        public Dictionary<Guid, List<string>> Writes { get; } = [];

        public Dictionary<Guid, string> Targets { get; } = [];

        public Guid Flow(string name, string kind, int wave, string[] reads, string[] writes, string? target = null)
        {
            var id = Guid.NewGuid();
            Flows[id] = new StreamPathFlow(id, name, kind, wave);
            Reads[id] = [.. reads];
            Writes[id] = [.. writes];
            if ((target ?? writes.FirstOrDefault()) is { } t)
            {
                Targets[id] = t;
            }

            return id;
        }

        public StreamPathWalkResult Walk(Guid selected) => StreamPathWalk.Walk(
            new StreamPathGraph { Flows = Flows, Reads = Reads, Writes = Writes, Targets = Targets }, selected);
    }

    private static string F(Guid id) => StreamPathWalk.FlowNodeId(id);

    private static string O(string key) => StreamPathWalk.ObjectNodeId(key);

    private static StreamPathWalkNode Node(StreamPathWalkResult result, string id)
        => Assert.Single(result.Nodes, n => n.Id == id);

    [Fact]
    public void Following_an_ingestion_flow_draws_where_its_data_came_from_and_where_it_goes()
    {
        var estate = new Estate();
        // The pre flow reads its own table back (a merge into it): that is its output, not an input.
        var pre = estate.Flow("orders_pre", "file", 0, [Files, PreTable], [PreTable, PreView], PreTable);
        var ing = estate.Flow("orders_ing", "ing", 1, [PreView, PreTable], [ArcTable]);
        var exp = estate.Flow("orders_exp", "exp", 2, [ArcTable], [Export]);

        var result = estate.Walk(ing);

        Assert.True(result.LineageKnown);
        Assert.Equal(StreamPathRoles.Selected, Node(result, F(ing)).Role);
        Assert.Equal(StreamPathRoles.Upstream, Node(result, F(pre)).Role);
        Assert.True(Node(result, O(Files)).External);
        Assert.Equal(StreamPathRoles.Downstream, Node(result, F(exp)).Role);

        // One hand-over from the pre flow, through its table, though the ingestion flow reads the view as well.
        Assert.Equal(
            new HashSet<(string, string)>
            {
                (O(Files), F(pre)),
                (F(pre), O(PreTable)),
                (O(PreTable), F(ing)),
                (F(ing), O(ArcTable)),
                (O(ArcTable), F(exp)),
                (F(exp), O(Export)),
            },
            result.Edges.ToHashSet());
        Assert.DoesNotContain(result.Nodes, n => n.ObjectKey == PreView);
        Assert.Empty(Node(result, F(ing)).Omitted);
    }

    [Fact]
    public void A_flow_that_writes_nothing_ends_the_journey_and_is_named_on_the_object_it_reads()
    {
        var estate = new Estate();
        var ing = estate.Flow("orders_ing", "ing", 1, [PreTable], [ArcTable]);
        estate.Flow("orders_check", "hc", 2, [ArcTable], []);
        estate.Flow("orders_report", "rpt", 2, [ArcTable], []);

        var result = estate.Walk(ing);

        Assert.DoesNotContain(result.Nodes, n => n.FlowId is not null && n.FlowId != ing);
        var readers = Node(result, O(ArcTable)).Omitted[StreamPathOmissions.Readers];
        Assert.Equal(new[] { "orders_check", "orders_report" }, readers);
    }

    [Fact]
    public void A_wide_fan_out_draws_the_first_flows_by_wave_and_counts_the_rest_on_the_object()
    {
        var estate = new Estate();
        var ing = estate.Flow("orders_ing", "ing", 1, [PreTable], [ArcTable]);
        var readers = Enumerable.Range(1, StreamPathWalk.MaxBranches + 2)
            .Select(i => estate.Flow($"orders_exp_{i}", "exp", 2 + (i % 2), [ArcTable], [$"file|||exports/{i}"]))
            .ToList();

        var result = estate.Walk(ing);

        var drawn = result.Nodes.Where(n => n.FlowId is { } id && id != ing).ToList();
        Assert.Equal(StreamPathWalk.MaxBranches, drawn.Count);
        Assert.Equal(2, Node(result, O(ArcTable)).Omitted[StreamPathOmissions.Readers].Count);

        // The first by wave, then by name: every wave-2 reader before any wave-3 one.
        var waveOf = estate.Flows.ToDictionary(f => f.Key, f => f.Value.Wave);
        var drawnWaves = drawn.Select(n => waveOf[n.FlowId!.Value]).ToList();
        var cutWaves = readers.Where(r => drawn.All(n => n.FlowId != r)).Select(r => waveOf[r]).ToList();
        Assert.True(drawnWaves.Max() <= cutWaves.Min());
    }

    [Fact]
    public void A_downstream_flow_counts_the_inputs_that_reach_it_from_other_paths()
    {
        var estate = new Estate();
        var ing = estate.Flow("orders_ing", "ing", 1, [PreTable], [ArcTable]);
        var exp = estate.Flow("orders_exp", "exp", 2, [ArcTable, "srv|db|arc|customers", "srv|db|ref|codes"], [Export]);
        estate.Flow("customers_ing", "ing", 1, ["srv|db|pre|customers"], ["srv|db|arc|customers"]);

        var result = estate.Walk(ing);

        Assert.Equal(
            new[] { "srv|db|arc|customers", "srv|db|ref|codes" },
            Node(result, F(exp)).Omitted[StreamPathOmissions.Inputs]);
        Assert.DoesNotContain(result.Nodes, n => n.ObjectKey == "srv|db|arc|customers");
    }

    [Fact]
    public void The_selected_flow_counts_what_it_writes_off_the_path()
    {
        var estate = new Estate();
        var pre = estate.Flow("orders_pre", "file", 0, [Files], [PreTable, PreView], PreTable);

        var result = estate.Walk(pre);

        Assert.Equal(new[] { PreView }, Node(result, F(pre)).Omitted[StreamPathOmissions.Outputs]);
    }

    [Fact]
    public void A_long_chain_stops_at_the_depth_and_names_what_is_further()
    {
        var estate = new Estate();
        var flows = new List<Guid>();
        for (var i = 0; i < StreamPathWalk.MaxDepth + 3; i++)
        {
            flows.Add(estate.Flow($"step_{i}", "sp", i, i == 0 ? [Files] : [$"t{i - 1}"], [$"t{i}"]));
        }

        var result = estate.Walk(flows[0]);

        var drawnFlows = result.Nodes.Count(n => n.FlowId is not null);
        Assert.Equal(StreamPathWalk.MaxDepth + 1, drawnFlows);
        var last = Node(result, F(flows[StreamPathWalk.MaxDepth]));
        Assert.Equal(new[] { $"step_{StreamPathWalk.MaxDepth + 1}" }, last.Omitted[StreamPathOmissions.Downstream]);
    }

    [Fact]
    public void A_flow_without_lineage_is_a_path_of_one()
    {
        var estate = new Estate();
        var lone = estate.Flow("lone", "sp", 0, [], []);

        var result = estate.Walk(lone);

        Assert.False(result.LineageKnown);
        Assert.Equal(F(lone), Assert.Single(result.Nodes).Id);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void A_cycle_between_flows_is_walked_once()
    {
        var estate = new Estate();
        var a = estate.Flow("a", "sp", 0, ["y"], ["x"]);
        var b = estate.Flow("b", "sp", 1, ["x"], ["y"]);

        var result = estate.Walk(a);

        Assert.Equal(2, result.Nodes.Count(n => n.FlowId is not null));
        Assert.Contains((F(b), O("y")), result.Edges);
        Assert.Contains((O("x"), F(b)), result.Edges);
        Assert.Equal(result.Edges.Count, result.Edges.Distinct().Count());
    }

    [Fact]
    public void A_key_spelled_in_another_case_names_the_same_node()
    {
        var estate = new Estate();
        var pre = estate.Flow("orders_pre", "file", 0, [Files], [PreTable]);
        var ing = estate.Flow("orders_ing", "ing", 1, [PreTable.ToUpperInvariant()], [ArcTable]);

        var result = estate.Walk(ing);

        Assert.Single(result.Nodes, n => string.Equals(n.ObjectKey, PreTable, StringComparison.OrdinalIgnoreCase));
        Assert.All(result.Edges, e =>
        {
            Assert.Contains(result.Nodes, n => n.Id == e.From);
            Assert.Contains(result.Nodes, n => n.Id == e.To);
        });
        Assert.Contains(result.Edges, e => e.From == F(pre));
    }
}
