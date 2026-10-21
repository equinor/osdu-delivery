using Xunit;
using static SqlFlow.Delivery.Order.Tests.OrderTestKit;

namespace SqlFlow.Delivery.Order.Tests;

public sealed class OsduOrderTests
{
    [Fact]
    public void A_chain_of_references_orders_its_types_level_by_level()
    {
        var plan = Plan(
            Deliver("logs", WellLog, ["osdu.data.WellboreID"]),
            Deliver("wellbores", Wellbore, ["osdu.data.WellID"]),
            Deliver("wells", Well, []));

        Assert.Equal("Wellbore>Well WellLog>Wellbore", Edges(plan));
        Assert.Equal("Well | Wellbore | WellLog", Levels(plan));
        Assert.True(plan.Keeps("master-data--Wellbore", "master-data--Well"));
        Assert.False(plan.Keeps("master-data--Well", "master-data--Wellbore"));
        Assert.Equal(["master-data--Wellbore"], plan.WaitsFor(plan.Deliveries[0]));
        Assert.Empty(plan.WaitsFor(plan.Deliveries[2]));
        Assert.Empty(plan.Cuts);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Only_the_properties_a_mapping_fills_refer_and_a_mapping_not_known_refers_to_all_its_schema_allows()
    {
        var filled = Plan(
            Deliver("logs", WellLog, ["osdu.data.WellboreID", "osdu.data.Name"]),
            Deliver("wellbores", Wellbore, []),
            Deliver("companies", Organisation, []));
        Assert.Equal("WellLog>Wellbore", Edges(filled));

        var unknown = Plan(
            Deliver("logs", WellLog, null),
            Deliver("wellbores", Wellbore, []),
            Deliver("companies", Organisation, []));
        Assert.Equal("WellLog>Organisation WellLog>Wellbore", Edges(unknown));
    }

    [Fact]
    public void A_reference_to_a_type_no_delivery_of_the_partition_writes_orders_nothing()
    {
        var plan = Plan(Deliver("logs", WellLog, ["osdu.data.WellboreID", "osdu.data.Curves[].CurveUnit"]));
        Assert.Empty(plan.Edges);
        Assert.Equal(["work-product-component--WellLog"], plan.Types);
        Assert.Empty(plan.WaitsFor(plan.Deliveries[0]));
        Assert.True(plan.Delivers("work-product-component--WellLog"));
        Assert.False(plan.Delivers("master-data--Wellbore"));
        Assert.Equal(-1, plan.LevelOf("master-data--Wellbore"));
    }

    [Fact]
    public void A_type_referring_to_its_own_type_is_left_to_record_waits_and_makes_no_cycle()
    {
        var plan = Plan(Deliver("wellbores", Wellbore, ["osdu.data.ParentWellboreID", "osdu.data.WellID"]), Deliver("wells", Well, []));
        Assert.Equal("Wellbore>Well", Edges(plan));
        var self = Assert.Single(plan.SelfReferences);
        Assert.Equal(("master-data--Wellbore", "osdu.data.ParentWellboreID"), (self.From, Assert.Single(self.Because).Property));
        Assert.Empty(plan.Cuts);
    }

    [Fact]
    public void A_cycle_across_groups_cuts_the_reference_from_the_earlier_group_to_the_later_one()
    {
        // A wellbore names its definitive trajectory, and the trajectory names its wellbore: the wellbores go first.
        var plan = Plan(
            Deliver("wellbores", Wellbore, ["osdu.data.DefinitiveTrajectoryID"]),
            Deliver("trajectories", Trajectory, ["osdu.data.WellboreID"]));

        Assert.Equal("WellboreTrajectory>Wellbore", Edges(plan));
        var cut = Assert.Single(plan.Cuts);
        Assert.Equal((OrderCutReason.BackReference, "master-data--Wellbore", "work-product-component--WellboreTrajectory"), (cut.Reason, cut.Edge.From, cut.Edge.To));
        Assert.Contains("a later group", cut.Why, StringComparison.Ordinal);
        Assert.Empty(plan.Warnings);
        Assert.Equal("Wellbore | WellboreTrajectory", Levels(plan));
    }

    [Fact]
    public void A_reference_from_an_earlier_group_to_a_later_one_outside_any_cycle_is_kept()
    {
        var graph = Graph(Schema("osdu:wks:reference-data--Catalogue:1.0.0", ("osdu.data.OwnerID", ["master-data--Organisation"])));
        var plan = OsduOrder.Plan("dev", [Deliver("catalogue", "osdu:wks:reference-data--Catalogue:1.0.0", ["osdu.data.OwnerID"]), Deliver("companies", Organisation, [])], graph);
        Assert.Equal("Catalogue>Organisation", Edges(plan));
        Assert.Empty(plan.Cuts);
    }

    [Fact]
    public void In_a_cycle_of_one_group_the_reference_no_search_fills_gives_way_to_the_one_a_search_fills()
    {
        var a = "osdu:wks:work-product-component--A:1.0.0";
        var b = "osdu:wks:work-product-component--B:1.0.0";
        var graph = Graph(Schema(a, ("osdu.data.BID", ["work-product-component--B"])), Schema(b, ("osdu.data.AID", ["work-product-component--A"])));
        var plan = OsduOrder.Plan("dev", [Deliver("a", a, ["osdu.data.BID"], searched: ["osdu.data.BID"]), Deliver("b", b, ["osdu.data.AID"])], graph);

        Assert.Equal("A>B", Edges(plan));
        var cut = Assert.Single(plan.Cuts);
        Assert.Equal((OrderCutReason.GivesWayToSearch, "work-product-component--B"), (cut.Reason, cut.Edge.From));
        Assert.Empty(plan.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_cycle_of_one_group_nothing_resolves_is_not_ordered_and_says_so(bool bothSearched)
    {
        var a = "osdu:wks:work-product-component--A:1.0.0";
        var b = "osdu:wks:work-product-component--B:1.0.0";
        var graph = Graph(Schema(a, ("osdu.data.BID", ["work-product-component--B"])), Schema(b, ("osdu.data.AID", ["work-product-component--A"])));
        var plan = OsduOrder.Plan(
            "dev",
            [Deliver("a", a, ["osdu.data.BID"], searched: bothSearched ? ["osdu.data.BID"] : null), Deliver("b", b, ["osdu.data.AID"], searched: bothSearched ? ["osdu.data.AID"] : null)],
            graph);

        Assert.Empty(plan.Edges);
        Assert.Equal(2, plan.Cuts.Count);
        Assert.All(plan.Cuts, c => Assert.Equal(OrderCutReason.Circular, c.Reason));
        Assert.Contains("refer to each other in a circle", Assert.Single(plan.Warnings), StringComparison.Ordinal);
        Assert.Equal("A,B", Levels(plan));
        Assert.Empty(plan.WaitsFor(plan.Deliveries[0]));
    }

    [Fact]
    public void A_cycle_through_three_groups_cuts_only_the_back_reference_that_closes_it()
    {
        var r = "osdu:wks:reference-data--R:1.0.0";
        var m = "osdu:wks:master-data--M:1.0.0";
        var w = "osdu:wks:work-product-component--W:1.0.0";
        var graph = Graph(
            Schema(r, ("osdu.data.MID", ["master-data--M"])),
            Schema(m, ("osdu.data.WID", ["work-product-component--W"])),
            Schema(w, ("osdu.data.RID", ["reference-data--R"]), ("osdu.data.MID", ["master-data--M"])));
        var plan = OsduOrder.Plan("dev", [Deliver("r", r, null), Deliver("m", m, null), Deliver("w", w, null)], graph);

        // Both cycles (M <-> W, and R -> M -> W -> R) go through M -> W, master data referring to a work product
        // component: cutting it leaves R -> M, reference data referring to master data, in no cycle, so it is kept.
        Assert.Equal("R>M W>M W>R", Edges(plan));
        var cut = Assert.Single(plan.Cuts);
        Assert.Equal((OrderCutReason.BackReference, "master-data--M -> work-product-component--W"), (cut.Reason, cut.Edge.ToString()));
        Assert.Equal("M | R | W", Levels(plan));
    }

    [Fact]
    public void The_order_is_the_same_whatever_order_the_deliveries_come_in()
    {
        var a = "osdu:wks:work-product-component--A:1.0.0";
        var b = "osdu:wks:work-product-component--B:1.0.0";
        var c = "osdu:wks:work-product-component--C:1.0.0";
        var graph = Graph(
            Schema(a, ("osdu.data.BID", ["work-product-component--B"]), ("osdu.data.WellboreID", ["master-data--Wellbore"])),
            Schema(b, ("osdu.data.CID", ["work-product-component--C"])),
            Schema(c, ("osdu.data.AID", ["work-product-component--A"])));
        OrderDelivery[] deliveries =
        [
            Deliver("a", a, null), Deliver("b", b, null), Deliver("c", c, null),
            Deliver("wellbores", Wellbore, ["osdu.data.DefinitiveTrajectoryID", "osdu.data.WellID"]),
            Deliver("trajectories", Trajectory, null), Deliver("wells", Well, null),
        ];
        var expected = OsduOrder.Plan("dev", deliveries, graph).Describe();
        var random = new Random(20261020);
        for (var i = 0; i < 25; i++)
        {
            var shuffled = deliveries.OrderBy(_ => random.Next()).ToArray();
            Assert.Equal(expected, OsduOrder.Plan("dev", shuffled, graph).Describe());
        }
    }

    [Fact]
    public void A_relationship_naming_a_group_refers_to_every_type_of_the_group_the_partition_delivers()
    {
        var plan = Plan(Deliver("logs", WellLog, ["osdu.data.Datasets[]"]), Deliver("files", FileKind, []));
        Assert.Equal("WellLog>File.Generic", Edges(plan));
    }

    [Fact]
    public void Each_partition_has_an_order_of_its_own()
    {
        var plans = OsduOrder.PlanAll(
            [
                Deliver("logs", WellLog, ["osdu.data.WellboreID"], partition: "dev"),
                Deliver("wellbores", Wellbore, [], partition: "dev"),
                Deliver("logs", WellLog, ["osdu.data.WellboreID"], partition: "test"),
            ],
            Graph());

        Assert.Equal(["dev", "test"], plans.Select(p => p.Partition));
        Assert.Equal("WellLog>Wellbore", Edges(plans[0]));
        Assert.Empty(plans[1].Edges);
        Assert.Single(OsduOrder.Plan("TEST", plans.SelectMany(p => p.Deliveries), Graph()).Deliveries);
    }

    [Fact]
    public void Two_deliveries_of_one_type_wait_for_what_each_one_refers_to()
    {
        var plan = Plan(
            Deliver("logs-a", WellLog, ["osdu.data.WellboreID"]),
            Deliver("logs-b", WellLog, ["osdu.data.Curves[].CurveUnit"]),
            Deliver("wellbores", Wellbore, []),
            Deliver("units", Unit, []));

        Assert.Equal(["master-data--Wellbore"], plan.WaitsFor(plan.Deliveries[0]));
        Assert.Equal(["reference-data--UnitOfMeasure"], plan.WaitsFor(plan.Deliveries[1]));
        Assert.Equal(2, plan.DeliveriesOf("work-product-component--WellLog").Count);
    }

    [Fact]
    public void A_delivery_whose_schema_is_not_known_is_a_type_of_the_order_that_refers_to_nothing_with_a_warning()
    {
        var plan = Plan(Deliver("rigs", "osdu:wks:master-data--Rig:1.0.0", null), Deliver("logs", WellLog, ["osdu.data.WellboreID"]));
        Assert.Equal(["master-data--Rig", "work-product-component--WellLog"], plan.Types);
        Assert.Equal("rigs", Assert.Single(plan.Unknown).Flow);
        Assert.Contains("no template folder holds its template, and no built release", Assert.Single(plan.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_delivery_given_twice_counts_once_and_one_that_writes_no_kind_is_refused()
    {
        var plan = Plan(Deliver("logs", WellLog, []), Deliver("LOGS", WellLog, []), Deliver("logs", WellLog, [], @interface: "b"));
        Assert.Equal(2, plan.Deliveries.Count);
        Assert.Throws<ArgumentException>(() => Plan(Deliver("logs", "WellLog", [])));
        Assert.Throws<ArgumentException>(() => OsduOrder.Plan(" ", [], Graph()));
    }

    [Fact]
    public void Kinds_of_different_authorities_and_versions_are_one_entity_type()
    {
        var graph = Graph(Schema("acme:custom:master-data--Wellbore:9.0.0", ("osdu.data.WellID", ["master-data--Well"])));
        var plan = OsduOrder.Plan("dev", [Deliver("acme", "acme:custom:master-data--Wellbore:9.0.0", ["osdu.data.WellID"]), Deliver("wells", Well, []), Deliver("logs", WellLog, ["osdu.data.WellboreID"])], graph);
        Assert.Equal("Wellbore>Well WellLog>Wellbore", Edges(plan));
    }

    [Fact]
    public void A_diamond_puts_each_type_one_level_above_the_highest_type_it_waits_for()
    {
        var plan = Plan(
            Deliver("logs", WellLog, ["osdu.data.WellboreID", "osdu.data.Curves[].CurveUnit"]),
            Deliver("wellbores", Wellbore, ["osdu.data.WellID"]),
            Deliver("wells", Well, ["osdu.data.VerticalMeasurements[].VerticalMeasurementUnitOfMeasureID"]),
            Deliver("units", Unit, ["osdu.data.UnitQuantityID"]),
            Deliver("quantities", Quantity, []));

        Assert.Equal("UnitQuantity | UnitOfMeasure | Well | Wellbore | WellLog", Levels(plan));
        Assert.Equal(4, plan.LevelOf("work-product-component--WellLog"));
    }

    [Fact]
    public void A_long_chain_is_ordered_without_exhausting_the_stack()
    {
        const int Length = 3000;
        var nodes = Enumerable.Range(0, Length).Select(i => Schema(
            $"osdu:wks:reference-data--T{i:D5}:1.0.0",
            i == 0 ? [] : [("osdu.data.PreviousID", new[] { $"reference-data--T{i - 1:D5}" })]));
        var graph = OsduSchemaGraph.Of(nodes);
        var plan = OsduOrder.Plan("dev", Enumerable.Range(0, Length).Select(i => Deliver($"t{i}", $"osdu:wks:reference-data--T{i:D5}:1.0.0", null)), graph);

        Assert.Equal(Length - 1, plan.LevelOf($"reference-data--T{Length - 1:D5}"));
        Assert.Equal(Length, plan.Levels.Count);
    }

    [Fact]
    public void A_long_cycle_is_found_and_cut_without_exhausting_the_stack()
    {
        const int Length = 2000;
        var nodes = Enumerable.Range(0, Length).Select(i => Schema(
            $"osdu:wks:work-product-component--C{i:D5}:1.0.0",
            [("osdu.data.NextID", new[] { $"work-product-component--C{(i + 1) % Length:D5}" })]));
        var plan = OsduOrder.Plan("dev", Enumerable.Range(0, Length).Select(i => Deliver($"c{i}", $"osdu:wks:work-product-component--C{i:D5}:1.0.0", null)), OsduSchemaGraph.Of(nodes));

        Assert.Empty(plan.Edges);
        Assert.Equal(Length, plan.Cuts.Count);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void Two_cycles_sharing_a_type_are_each_cut_and_what_is_left_is_acyclic()
    {
        var a = "osdu:wks:work-product-component--A:1.0.0";
        var b = "osdu:wks:work-product-component--B:1.0.0";
        var c = "osdu:wks:work-product-component--C:1.0.0";
        var graph = Graph(
            Schema(a, ("osdu.data.BID", ["work-product-component--B"]), ("osdu.data.CID", ["work-product-component--C"])),
            Schema(b, ("osdu.data.AID", ["work-product-component--A"])),
            Schema(c, ("osdu.data.AID", ["work-product-component--A"])));
        var plan = OsduOrder.Plan(
            "dev",
            [Deliver("a", a, null, searched: ["osdu.data.BID"]), Deliver("b", b, null), Deliver("c", c, null, searched: ["osdu.data.AID"])],
            graph);

        // A -> B is searched, so B -> A gives way; C -> A is searched, so A -> C gives way.
        Assert.Equal("A>B C>A", Edges(plan));
        Assert.All(plan.Cuts, cut => Assert.Equal(OrderCutReason.GivesWayToSearch, cut.Reason));
        Assert.Equal("B | A | C", Levels(plan));
    }

    [Fact]
    public void The_description_names_the_levels_the_references_the_cuts_and_the_warnings()
    {
        var plan = Plan(
            Deliver("wellbores", Wellbore, ["osdu.data.DefinitiveTrajectoryID", "osdu.data.ParentWellboreID"], repository: "estate"),
            Deliver("trajectories", Trajectory, ["osdu.data.WellboreID"], repository: "estate", searched: ["osdu.data.WellboreID"]),
            Deliver("rigs", "osdu:wks:master-data--Rig:1.0.0", null));
        var text = plan.Describe();

        Assert.Contains("level 1: master-data--Rig, master-data--Wellbore", text, StringComparison.Ordinal);
        Assert.Contains("work-product-component--WellboreTrajectory waits for master-data--Wellbore: estate: trajectories osdu.data.WellboreID (searched)", text, StringComparison.Ordinal);
        Assert.Contains("not ordered: master-data--Wellbore -> work-product-component--WellboreTrajectory", text, StringComparison.Ordinal);
        Assert.Contains("refers to its own type", text, StringComparison.Ordinal);
        Assert.Contains("WARN  rigs writes", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_delivery_to_every_partition_is_part_of_the_order_of_each_partition_another_delivery_names()
    {
        var plans = OsduOrder.PlanPartitions(
            [
                Deliver("wells", Well, null, partition: OsduOrder.EveryPartition),
                Deliver("wellbores", Wellbore, ["osdu.data.WellID"], partition: "dev"),
                Deliver("test-wellbores", Wellbore, ["osdu.data.WellID"], partition: "test"),
            ],
            Graph());

        Assert.Equal(["*", "dev", "test"], plans.Select(p => p.Partition));
        foreach (var named in plans.Where(p => p.Partition != OsduOrder.EveryPartition))
        {
            // The wells are delivered to each named partition, so each partition's wellbores wait for them there.
            Assert.Contains(named.Deliveries, d => d.Flow == "wells" && d.Partition == named.Partition);
            Assert.Equal("Wellbore>Well", Edges(named));
        }

        // The partition standing for every registered one holds what is delivered to it, and nothing named elsewhere.
        Assert.Equal(["wells"], plans[0].Deliveries.Select(d => d.Flow));
    }

    [Fact]
    public void Deliveries_to_every_partition_alone_are_ordered_in_that_partition_and_copied_nowhere()
    {
        var plans = OsduOrder.PlanPartitions(
            [
                Deliver("wells", Well, null, partition: OsduOrder.EveryPartition),
                Deliver("wellbores", Wellbore, ["osdu.data.WellID"], partition: OsduOrder.EveryPartition),
            ],
            Graph());

        var plan = Assert.Single(plans);
        Assert.Equal((OsduOrder.EveryPartition, "Wellbore>Well"), (plan.Partition, Edges(plan)));
    }

    [Fact]
    public void Partitions_named_in_different_cases_are_one_partition()
    {
        var plans = OsduOrder.PlanPartitions(
            [
                Deliver("wells", Well, null, partition: "Dev"),
                Deliver("wellbores", Wellbore, ["osdu.data.WellID"], partition: "dev"),
                Deliver("more-wells", Well, null, partition: OsduOrder.EveryPartition),
            ],
            Graph());

        Assert.Equal(2, plans.Count);
        var dev = plans.Single(p => p.Partition != OsduOrder.EveryPartition);
        Assert.Equal(["more-wells", "wellbores", "wells"], dev.Deliveries.Select(d => d.Flow).Order(StringComparer.Ordinal));
    }
}
