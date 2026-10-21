using System.Text.Json.Nodes;
using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Order;
using SqlFlow.Delivery.Ordering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Lineage takes the OSDU order (osdu/docs/reference/concepts/osdu-order.md): a delivery flow is ordered after every flow
/// delivering an entity type its records refer to, however its mapping writes the reference (a column holding the id, a
/// literal, a search), cycles are cut the way the order cuts them and never reach the fallback wave, a cache flow that both
/// reads what a delivery writes and is read by it is ordered after it, and flows that leave their partitions to the
/// registry are ordered against the flows of the partitions the estate names. Each estate is written to a folder of its
/// own, with schemas made for the case (the relationships a test needs, nothing else), and scanned as a sync scans it,
/// with the templates held in memory.
/// </summary>
public sealed class OrderLineageTests : IDisposable
{
    private static readonly DateTime Utc = OrderEstate.Utc;

    private readonly string _root;
    private readonly OrderEstate.MemoryTemplates _templates = new();
    private readonly OrderEstate _estate;

    public OrderLineageTests()
    {
        _root = Samples.NewTempDirectory();
        _estate = new OrderEstate(_root, _templates);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Deliveries_referring_to_each_other_by_ids_written_from_columns_run_in_the_order_their_records_refer_in()
    {
        Schema("master-data--Well");
        Schema("master-data--Wellbore", ("WellID", "master-data--Well"));
        Schema("work-product-component--WellLog", ("WellboreID", "master-data--Wellbore"));
        Delivery("wells", "master-data--Well");
        Delivery("wellbores", "master-data--Wellbore", columns: ["WellID"]);
        Delivery("logs", "work-product-component--WellLog", columns: ["WellboreID"]);

        var report = Scan();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "wells") < WaveOf(report, "wellbores"));
        Assert.True(WaveOf(report, "wellbores") < WaveOf(report, "logs"));

        // Without the templates nothing tells what the ids point to, and the three are not ordered at all.
        _templates.Clear();
        var blind = Scan();
        Assert.Equal(WaveOf(blind, "wells"), WaveOf(blind, "logs"));
    }

    [Fact]
    public void A_reference_written_as_a_literal_orders_the_delivery_after_the_one_delivering_what_it_names()
    {
        Schema("reference-data--TechnicalAssuranceType");
        Schema("work-product-component--WellLog", ("TechnicalAssuranceTypeID", "reference-data--TechnicalAssuranceType"));
        Delivery("assurance-types", "reference-data--TechnicalAssuranceType");
        Delivery("logs", "work-product-component--WellLog", literals: [("TechnicalAssuranceTypeID", "\"{$param.dataPartition}:reference-data--TechnicalAssuranceType:Unevaluated:\"")]);

        var report = Scan();
        Assert.True(WaveOf(report, "assurance-types") < WaveOf(report, "logs"));
    }

    [Fact]
    public void A_reference_to_a_type_nothing_in_the_partition_delivers_reads_nothing_and_orders_nothing()
    {
        Schema("work-product-component--WellLog", ("WellboreID", "master-data--Wellbore"));
        Delivery("logs", "work-product-component--WellLog", columns: ["WellboreID"]);

        var lineage = Describe("flows/logs.yaml");
        Assert.DoesNotContain(Reads(lineage), r => r.Contains("*:*:", StringComparison.Ordinal));
        Assert.Empty(lineage.Warnings);
    }

    [Fact]
    public void A_reference_to_the_records_own_type_reads_nothing_and_makes_no_cycle()
    {
        Schema("master-data--Wellbore", ("ParentWellboreID", "master-data--Wellbore"));
        Delivery("wellbores", "master-data--Wellbore", columns: ["ParentWellboreID"]);
        Delivery("more-wellbores", "master-data--Wellbore", columns: ["ParentWellboreID"]);

        var report = Scan();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.Equal(WaveOf(report, "wellbores"), WaveOf(report, "more-wellbores"));
    }

    [Fact]
    public void Types_of_one_group_referring_to_each_other_are_not_ordered_say_so_and_never_reach_the_fallback_wave()
    {
        Schema("work-product-component--A", ("BID", "work-product-component--B"));
        Schema("work-product-component--B", ("AID", "work-product-component--A"));
        Delivery("a", "work-product-component--A", columns: ["BID"]);
        Delivery("b", "work-product-component--B", columns: ["AID"]);

        var (collected, report) = ScanWithWarnings();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.Equal(WaveOf(report, "a"), WaveOf(report, "b"));
        Assert.Equal(2, collected.Warnings.Count(w => w.Contains("is not ordered against every delivery it refers to", StringComparison.Ordinal)));
    }

    [Fact]
    public void In_a_cycle_across_groups_the_later_group_waits_for_the_earlier_one()
    {
        // A wellbore names its definitive trajectory and the trajectory names its wellbore: the wellbores go first.
        Schema("master-data--Wellbore", ("DefinitiveTrajectoryID", "work-product-component--WellboreTrajectory"));
        Schema("work-product-component--WellboreTrajectory", ("WellboreID", "master-data--Wellbore"));
        Delivery("wellbores", "master-data--Wellbore", columns: ["DefinitiveTrajectoryID"]);
        Delivery("trajectories", "work-product-component--WellboreTrajectory", columns: ["WellboreID"]);

        var (collected, report) = ScanWithWarnings();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "wellbores") < WaveOf(report, "trajectories"));
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("not ordered", StringComparison.Ordinal));
    }

    [Fact]
    public void In_a_cycle_of_one_group_a_searched_reference_wins_over_one_written_from_a_column()
    {
        Schema("work-product-component--A", ("BID", "work-product-component--B"));
        Schema("work-product-component--B", ("AID", "work-product-component--A"));
        Delivery("a", "work-product-component--A", searched: "BID");
        Delivery("b", "work-product-component--B", columns: ["AID"]);

        var report = Scan();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "b") < WaveOf(report, "a"));
    }

    [Fact]
    public void Reference_data_referring_to_reference_data_one_cache_captures_runs_before_the_cache_and_the_cache_before_what_reads_it()
    {
        // One source delivers the unit quantities and the units, which refer to their quantity and check it against the
        // cache; one cache flow captures both; the wells refer to a unit. Before the order, the source and the cache each
        // read what the other writes and lineage ordered neither, so the cache could capture before the units existed.
        Schema("reference-data--UnitQuantity");
        Schema("reference-data--UnitOfMeasure", ("UnitQuantityID", "reference-data--UnitQuantity"));
        Schema("master-data--Well", ("ElevationUnitID", "reference-data--UnitOfMeasure"));
        Source("reference", ("quantities", "reference-data--UnitQuantity", null), ("units", "reference-data--UnitOfMeasure", "UnitQuantityID"));
        Cache("reference-cache", "reference-data--UnitQuantity", "reference-data--UnitOfMeasure");
        Delivery("wells", "master-data--Well", refChecked: ("ElevationUnitID", "reference-data--UnitOfMeasure"));

        var report = Scan();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "reference") < WaveOf(report, "reference-cache"));
        Assert.True(WaveOf(report, "reference-cache") < WaveOf(report, "wells"));
    }

    [Fact]
    public void Separate_reference_flows_one_cache_captures_are_ordered_by_what_they_refer_to_and_the_cache_runs_after_both()
    {
        Schema("reference-data--UnitQuantity");
        Schema("reference-data--UnitOfMeasure", ("UnitQuantityID", "reference-data--UnitQuantity"));
        Schema("master-data--Well", ("ElevationUnitID", "reference-data--UnitOfMeasure"));
        Delivery("quantities", "reference-data--UnitQuantity");
        Delivery("units", "reference-data--UnitOfMeasure", refChecked: ("UnitQuantityID", "reference-data--UnitQuantity"));
        Cache("reference-cache", "reference-data--UnitQuantity", "reference-data--UnitOfMeasure");
        Delivery("wells", "master-data--Well", refChecked: ("ElevationUnitID", "reference-data--UnitOfMeasure"));

        var report = Scan();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "quantities") < WaveOf(report, "units"));
        Assert.True(WaveOf(report, "units") < WaveOf(report, "reference-cache"));
        Assert.True(WaveOf(report, "reference-cache") < WaveOf(report, "wells"));
    }

    [Fact]
    public void A_cache_type_another_cache_flow_fills_without_capturing_what_the_delivery_writes_is_still_read()
    {
        // The quantities have a cache of their own: reading it makes no cycle, so the units still wait for it.
        Schema("reference-data--UnitQuantity");
        Schema("reference-data--UnitOfMeasure", ("UnitQuantityID", "reference-data--UnitQuantity"));
        Delivery("quantities", "reference-data--UnitQuantity");
        Cache("quantity-cache", "reference-data--UnitQuantity");
        Delivery("units", "reference-data--UnitOfMeasure", refChecked: ("UnitQuantityID", "reference-data--UnitQuantity"));

        var report = Scan();
        Assert.True(WaveOf(report, "quantities") < WaveOf(report, "quantity-cache"));
        Assert.True(WaveOf(report, "quantity-cache") < WaveOf(report, "units"));
    }

    [Fact]
    public void A_flow_serving_every_partition_waits_for_what_a_named_partition_s_flow_delivers_and_the_other_way_round()
    {
        Schema("reference-data--LogType");
        Schema("master-data--Wellbore");
        Schema("work-product-component--WellLog", ("LogTypeID", "reference-data--LogType"), ("WellboreID", "master-data--Wellbore"));
        Schema("work-product-component--WellboreMarkerSet", ("WellboreID", "master-data--Wellbore"));
        Delivery("log-types", "reference-data--LogType");
        Delivery("logs", "work-product-component--WellLog", columns: ["LogTypeID", "WellboreID"], partition: null);
        Delivery("wellbores", "master-data--Wellbore", partition: null);
        Delivery("markers", "work-product-component--WellboreMarkerSet", columns: ["WellboreID"]);

        var report = Scan();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "log-types") < WaveOf(report, "logs"));
        Assert.True(WaveOf(report, "wellbores") < WaveOf(report, "logs"));
        Assert.True(WaveOf(report, "wellbores") < WaveOf(report, "markers"));
    }

    [Fact]
    public void A_cache_flow_serving_every_partition_runs_after_a_named_partition_s_delivery_of_what_it_captures()
    {
        Schema("reference-data--LogType");
        Delivery("log-types", "reference-data--LogType");
        CacheIn(null, "reference-cache", "reference-data--LogType");

        var report = Scan();
        Assert.True(WaveOf(report, "log-types") < WaveOf(report, "reference-cache"));
    }

    [Fact]
    public void Partitions_are_ordered_apart()
    {
        Schema("master-data--Wellbore");
        Schema("work-product-component--WellLog", ("WellboreID", "master-data--Wellbore"));
        Delivery("wellbores", "master-data--Wellbore", partition: "test");
        Delivery("logs", "work-product-component--WellLog", columns: ["WellboreID"]);

        var lineage = Describe("flows/logs.yaml");
        Assert.DoesNotContain(Reads(lineage), r => r.Contains("*:*:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_delivery_whose_template_is_not_saved_says_it_is_not_ordered_and_the_others_still_are()
    {
        Schema("master-data--Well");
        Schema("master-data--Wellbore", ("WellID", "master-data--Well"));
        Delivery("wells", "master-data--Well");
        Delivery("wellbores", "master-data--Wellbore", columns: ["WellID"]);
        Schema("work-product-component--WellLog", ("WellboreID", "master-data--Wellbore"));
        Delivery("logs", "work-product-component--WellLog", columns: ["WellboreID"]);
        _templates.Forget("work-product-component--WellLog");

        var (collected, report) = ScanWithWarnings();
        Assert.True(WaveOf(report, "wells") < WaveOf(report, "wellbores"));
        Assert.Contains(collected.Warnings, w => w.Contains("delivery flow 'logs' is not ordered by what its records refer to", StringComparison.Ordinal)
            && w.Contains("Its template is not read: template osdu:wks:work-product-component--WellLog:1.0.0 version", StringComparison.Ordinal)
            && w.Contains("is not saved", StringComparison.Ordinal));
    }

    [Fact]
    public void Interfaces_of_one_source_read_their_own_types_without_waiting_for_themselves()
    {
        Schema("master-data--Well");
        Schema("master-data--Wellbore", ("WellID", "master-data--Well"));
        Source("master", ("wells", "master-data--Well", null), ("wellbores", "master-data--Wellbore", "WellID"));

        var report = Scan();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.DoesNotContain(report.Edges, e => e.Flow == "master" && e.Relation == LineageRelation.Reads && e.ObjectKey.Contains("master-data--well:1.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanning_an_estate_twice_orders_it_the_same_way()
    {
        Schema("reference-data--UnitQuantity");
        Schema("reference-data--UnitOfMeasure", ("UnitQuantityID", "reference-data--UnitQuantity"));
        Schema("master-data--Well", ("ElevationUnitID", "reference-data--UnitOfMeasure"));
        Schema("master-data--Wellbore", ("WellID", "master-data--Well"), ("DefinitiveTrajectoryID", "work-product-component--WellboreTrajectory"));
        Schema("work-product-component--WellboreTrajectory", ("WellboreID", "master-data--Wellbore"));
        Source("reference", ("quantities", "reference-data--UnitQuantity", null), ("units", "reference-data--UnitOfMeasure", "UnitQuantityID"));
        Cache("reference-cache", "reference-data--UnitQuantity", "reference-data--UnitOfMeasure");
        Delivery("wells", "master-data--Well", columns: ["ElevationUnitID"]);
        Delivery("wellbores", "master-data--Wellbore", columns: ["WellID", "DefinitiveTrajectoryID"]);
        Delivery("trajectories", "work-product-component--WellboreTrajectory", columns: ["WellboreID"]);

        static string Waves(LineageReport report) => string.Join(" | ", report.ExecutionPlan.Waves.Select(w => string.Join(",", w.Flows.Order(StringComparer.Ordinal))));
        var first = Waves(Scan());
        Assert.Equal(first, Waves(Scan()));
        Assert.Equal("reference | reference-cache,wells | wellbores | trajectories", first);
    }

    [Fact]
    public void A_kind_whose_template_is_not_saved_is_ordered_by_the_relationships_a_built_release_of_OSDU_s_lineage_holds()
    {
        Schema("master-data--Well");
        Schema("master-data--Wellbore", ("WellID", "master-data--Well"));
        Delivery("wells", "master-data--Well");
        Delivery("wellbores", "master-data--Wellbore", columns: ["WellID"]);
        _templates.Forget("master-data--Wellbore");
        var released = new SchemaNode(
            "osdu:wks:master-data--Wellbore:1.0.0", "master-data--Wellbore", [new SchemaRelationship("osdu.data.WellID", ["master-data--Well"])]);

        Assert.DoesNotContain(Reads(Describe("flows/wellbores.yaml")), r => r.Contains("*:*:", StringComparison.Ordinal));
        var lineage = Describe("flows/wellbores.yaml", new FixedCatalog(released));
        Assert.Contains($"{Partition}/*:*:master-data--Well:*", Reads(lineage));
        Assert.True(WaveOf(Scan(new FixedCatalog(released)), "wells") < WaveOf(Scan(new FixedCatalog(released)), "wellbores"));
    }

    [Fact]
    public void A_catalog_that_does_not_know_the_kind_leaves_the_delivery_unordered_and_says_why()
    {
        Schema("master-data--Well");
        Schema("master-data--Wellbore", ("WellID", "master-data--Well"));
        Delivery("wells", "master-data--Well");
        Delivery("wellbores", "master-data--Wellbore", columns: ["WellID"]);
        _templates.Forget("master-data--Wellbore");

        var lineage = Describe("flows/wellbores.yaml", new FixedCatalog(null));
        Assert.DoesNotContain(Reads(lineage), r => r.Contains("*:*:", StringComparison.Ordinal));
        Assert.Contains(lineage.Warnings, w => w.Contains("is not ordered by what its records refer to", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pipeline_writes_the_cache_types_its_after_step_names_so_their_readers_run_after_it()
    {
        Schema("reference-data--UnitOfMeasure");
        Schema("master-data--Well", ("ElevationUnitID", "reference-data--UnitOfMeasure"));
        Delivery("units", "reference-data--UnitOfMeasure", cache: "{ after: [UnitOfMeasure] }");
        Cache("reference-cache", "reference-data--UnitOfMeasure");
        Delivery("wells", "master-data--Well", refChecked: ("ElevationUnitID", "reference-data--UnitOfMeasure"));

        Assert.Contains(CacheWrites("flows/units.yaml"), d => d.Name == "UnitOfMeasure" && d.Namespace == Partition);
        Assert.Empty(CacheWrites("flows/wells.yaml"));
        var report = Scan();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "units") < WaveOf(report, "reference-cache"));
        Assert.True(WaveOf(report, "reference-cache") < WaveOf(report, "wells"));

        // By entity type, and by auto, the step names the same type; with no step, the cache flow writes it alone.
        Delivery("units", "reference-data--UnitOfMeasure", cache: "{ after: [reference-data--UnitOfMeasure] }");
        Assert.Contains(CacheWrites("flows/units.yaml"), d => d.Name == "UnitOfMeasure");
        Delivery("units", "reference-data--UnitOfMeasure", cache: "{ after: auto }");
        Assert.Contains(CacheWrites("flows/units.yaml"), d => d.Name == "UnitOfMeasure");
        Delivery("units", "reference-data--UnitOfMeasure");
        Assert.Empty(CacheWrites("flows/units.yaml"));
    }

    [Fact]
    public void A_pipeline_whose_before_step_searches_a_type_runs_after_the_flows_delivering_its_kind()
    {
        // The wells need the units current before they render: their before step searches the units' kind, so the flow
        // delivering the units runs first, whatever cache flow captures them.
        Schema("reference-data--UnitOfMeasure");
        Schema("master-data--Well", ("ElevationUnitID", "reference-data--UnitOfMeasure"));
        Delivery("units", "reference-data--UnitOfMeasure");
        Cache("reference-cache", "reference-data--UnitOfMeasure");
        Delivery("wells", "master-data--Well", refChecked: ("ElevationUnitID", "reference-data--UnitOfMeasure"), cache: "{ before: auto }");

        var lineage = Describe("flows/wells.yaml");
        Assert.Contains(lineage.Datasets, d => d.Relation == LineageRelation.Writes && d.System == OsduLineage.CacheSystem && d.Name == "UnitOfMeasure");
        Assert.Contains($"{Partition}/osdu:wks:reference-data--UnitOfMeasure:*", Reads(lineage));
        var report = Scan();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "units") < WaveOf(report, "wells"));
    }

    [Fact]
    public void A_pipeline_that_checks_references_against_a_type_its_after_step_writes_is_still_ordered()
    {
        // A wellbore refers to its parent wellbore: the pipeline reads the type it writes, which is no cycle with another
        // flow, so the estate is ordered and the cache flow still runs after the pipeline.
        Schema("master-data--Wellbore", ("ParentWellboreID", "master-data--Wellbore"));
        Delivery("wellbores", "master-data--Wellbore", refChecked: ("ParentWellboreID", "master-data--Wellbore"), cache: "{ after: [Wellbore] }");
        Cache("wellbore-cache", "master-data--Wellbore");

        Assert.Contains(CacheWrites("flows/wellbores.yaml"), d => d.Name == "Wellbore");
        var report = Scan();
        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "wellbores") < WaveOf(report, "wellbore-cache"));
    }

    [Fact]
    public void A_pipeline_to_every_partition_writes_the_types_a_named_partition_s_cache_flow_declares()
    {
        Schema("reference-data--LogType");
        Delivery("log-types", "reference-data--LogType", partition: null, cache: "{ after: auto }");
        Cache("reference-cache", "reference-data--LogType");

        Assert.Contains(CacheWrites("flows/log-types.yaml"), d => d.Name == "LogType" && d.Namespace == Partition);
    }

    // ---------------------------------------------------------------------------------------------------------------- estate

    private const string Partition = OrderEstate.Partition;

    private void Schema(string entityType, params (string Property, string Target)[] references) => _estate.Schema(entityType, references);

    private void Delivery(
        string name,
        string entityType,
        string[]? columns = null,
        string? partition = Partition,
        string? searched = null,
        (string Property, string Target)? refChecked = null,
        (string Property, string Value)[]? literals = null,
        string? cache = null)
        => _estate.Delivery(name, entityType, columns, partition, searched, refChecked, literals, cache);

    private void Source(string name, params (string Interface, string EntityType, string? Column)[] interfaces) => _estate.Source(name, interfaces);

    private void Cache(string name, params string[] entityTypes) => _estate.Cache(name, entityTypes);

    private void CacheIn(string? partition, string name, params string[] entityTypes) => _estate.CacheIn(partition, name, entityTypes);

    // ---------------------------------------------------------------------------------------------------------------- scans

    private YamlDocumentLoader Loader(IOrderCatalog? catalog = null)
    {
        var documents = new DeliveryDocumentLoader();
        return YamlDocumentLoader.CreateDefault(
            [new DeliveryFlowKind(documents, new MappingTemplateSource(_templates), catalog is null ? null : () => catalog), new CacheFlowKind(documents)]);
    }

    private LineageReport Scan(IOrderCatalog? catalog = null) => ScanWithWarnings(catalog).Report;

    private (CollectionResult Collected, LineageReport Report) ScanWithWarnings(IOrderCatalog? catalog = null)
    {
        var collected = new FlowSetCollector(Loader(catalog)).Collect(_root);
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("skipped", StringComparison.Ordinal) || w.Contains("is required", StringComparison.Ordinal));
        return (collected, LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc));
    }

    private RegisteredFlowLineage Describe(string relative, IOrderCatalog? catalog = null)
    {
        var loader = Loader(catalog);
        var path = Path.Combine(_root, relative);
        var estate = Directory.EnumerateFiles(Path.Combine(_root, "flows"), "*.yaml")
            .Concat(Directory.Exists(Path.Combine(_root, "cache")) ? Directory.EnumerateFiles(Path.Combine(_root, "cache"), "*.yaml") : [])
            .Order(StringComparer.Ordinal)
            .Select(f => new RegisteredEstateDocument(f, (RegisteredFlowDocument)loader.LoadFile(f)))
            .ToList();
        var document = Assert.IsAssignableFrom<RegisteredFlowDocument>(loader.LoadFile(path));
        return document.DescribeLineage(new RegisteredLineageContext(path, _root) { Estate = estate });
    }

    private IReadOnlyList<DeclaredDataset> CacheWrites(string relative)
        => [.. Describe(relative).Datasets.Where(d => d.Relation == LineageRelation.Writes && d.System == OsduLineage.CacheSystem)];

    private static IEnumerable<string> Reads(RegisteredFlowLineage lineage)
        => lineage.Derivations.SelectMany(d => d.From).Concat(lineage.Datasets).Where(d => d.Relation == LineageRelation.Reads).Select(d => $"{d.Namespace}/{d.Name}");

    private static int WaveOf(LineageReport report, string flow)
    {
        var wave = report.ExecutionPlan.Waves.FirstOrDefault(w => w.Flows.Contains(flow, StringComparer.OrdinalIgnoreCase));
        Assert.True(wave is not null, $"'{flow}' is in no wave.");
        return wave!.Wave;
    }

    /// <summary>A catalog that knows one released kind, or none.</summary>
    private sealed class FixedCatalog(SchemaNode? released) : IOrderCatalog
    {
        public SchemaNode? Schema(string kind) => released is not null && string.Equals(released.Kind, kind, StringComparison.Ordinal) ? released : null;
    }
}
