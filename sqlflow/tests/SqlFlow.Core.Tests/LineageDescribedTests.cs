using System.Text.Json;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Lineage descriptions (flowType: lin) through the real service over estates on disk. The first test is the contract
/// the kind exists under: the lineage SQLFlow computes is the same whether an estate holds descriptions or not. The
/// rest pin how a description is placed beside that lineage, and that one that cannot be placed in full is placed in
/// part with a finding saying what to write.
/// </summary>
public sealed class LineageDescribedTests
{
    private const string LandOrders = """
        name: land-orders
        source:
          type: csv
          location: landing/orders
        target:
          connection: ${env:SQLFLOW_CONN_DWH}
          schema: pre
          table: Orders
        """;

    private const string Curate = """
        flowType: lin
        name: orders-u1-curate-lin
        system: databricks
        reads:
          - { system: erp, name: sales.orders, as: src, origin: true }
          - { system: erp, name: sales.currencies, as: fx }
        writes:
          - name: curated.orders
            columns:
              id: src.order_no
              amount_eur: { from: [src.amount, fx.rate], note: amount * rate }
        """;

    private const string Publish = """
        flowType: lin
        name: orders-u2-publish-lin
        system: databricks
        reads:
          - { name: Curated.Orders, as: orders }
        writes:
          - feeds: land-orders
            columns:
              order_id: orders.id
              amount: orders.amount_eur
              loaded_at:
        """;

    private static LineageEstateHarness Estate() => new LineageEstateHarness()
        .Flow("orders/land-orders.yaml", LandOrders)
        .Flow("orders/load-orders.yaml", LineageEstateHarness.Ingestion("load-orders", "DW.pre.Orders", "DW.dbo.Orders", sourceRef: "${env:SQLFLOW_CONN_DWH}"))
        .Flow("orders/watch-orders.yaml", LineageEstateHarness.HealthCheck("watch-orders", "DW.dbo.Orders", "OrderDate"));

    /// <summary>The report as a reader that wants only the computed lineage sees it.</summary>
    private static string Computed(LineageReport report)
        => JsonSerializer.Serialize(report with { Described = LineageDescribed.Empty, GeneratedAtUtc = default });

    private static string Key(LineageReport report, string flow, LineageRelation relation, string nameContains)
        => report.Edges
            .Where(e => string.Equals(e.Flow, flow, StringComparison.OrdinalIgnoreCase) && e.Relation == relation && e.ViaModule is null)
            .Select(e => e.ObjectKey)
            .Distinct()
            .Single(k => k.Contains(nameContains, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task TheComputedLineage_IsTheSame_WithAndWithoutDescriptions()
    {
        using var estate = Estate();
        var without = await estate.ComputeAsync();

        estate
            .Flow("lineage/curate.yaml", Curate)
            .Flow("lineage/publish.yaml", Publish)
            .Flow("lineage/consume.yaml", """
                flowType: lin
                name: orders-d1-report-lin
                system: fabric
                reads:
                  - { fedBy: load-orders }
                writes:
                  - { name: sales_lakehouse.orders_report }
                """)
            .Flow("lineage/broken.yaml", """
                flowType: lin
                name: orders-x-broken-lin
                system: databricks
                writes:
                  - { feeds: no-such-flow }
                """);
        var with = await estate.ComputeAsync();

        // Flows, objects, edges, relationships, dependencies, waves, cycles and warnings: byte for byte.
        Assert.Equal(Computed(without), Computed(with));
        Assert.Equal(LineageEstateHarness.Waves(without), LineageEstateHarness.Waves(with));
        Assert.DoesNotContain(with.Flows, f => f.Kind == LineageDescriptionFlow.FlowType);
        Assert.DoesNotContain(with.Edges, e => LineageRelations.IsDescribed(e.Relation));
        Assert.DoesNotContain(with.Objects, o => LineageDescribedObject.IsDescribedKey(o.Key));
        Assert.DoesNotContain(with.ExecutionPlan.Waves.SelectMany(w => w.Flows), f => f.EndsWith("-lin", StringComparison.Ordinal));

        // And the descriptions are all there, beside it.
        Assert.True(without.Described.IsEmpty);
        Assert.Equal(
            ["orders-d1-report-lin", "orders-u1-curate-lin", "orders-u2-publish-lin", "orders-x-broken-lin"],
            with.Described.Steps.Select(s => s.Name));
        Assert.Equal(LineageReport.CurrentSchemaVersion, with.SchemaVersion);
    }

    [Fact]
    public async Task ADescription_MeetsTheEstate_OnTheObjectTheNamedFlowDeclares()
    {
        using var estate = Estate()
            .Flow("lineage/curate.yaml", Curate)
            .Flow("lineage/publish.yaml", Publish);

        var report = await estate.ComputeAsync();
        var described = report.Described;

        Assert.Empty(described.Warnings);
        var landingFile = Key(report, "land-orders", LineageRelation.Reads, "landing/orders");
        var publish = described.Steps.Single(s => s.Name == "orders-u2-publish-lin");
        var fed = Assert.Single(publish.Writes);
        Assert.Equal(landingFile, fed.ObjectKey);
        Assert.Equal("land-orders", fed.ViaFlow);

        // Two descriptions meet on the object both name, whatever case each spells it in, and it is one object.
        const string curated = "described:databricks|||curated.orders";
        Assert.Equal(curated, Assert.Single(publish.Reads).ObjectKey);
        Assert.Equal(curated, Assert.Single(described.Steps.Single(s => s.Name == "orders-u1-curate-lin").Writes).ObjectKey);
        Assert.Equal(
            ["described:databricks|||curated.orders", "described:erp|||sales.currencies", "described:erp|||sales.orders"],
            described.Objects.Select(o => o.Key));
        Assert.Equal("curated.orders", described.Objects[0].Name);

        Assert.Equal(
            [
                ("orders-u1-curate-lin", LineageRelation.DescribedReads, "described:erp|||sales.currencies"),
                ("orders-u1-curate-lin", LineageRelation.DescribedReads, "described:erp|||sales.orders"),
                ("orders-u1-curate-lin", LineageRelation.DescribedWrites, curated),
                ("orders-u2-publish-lin", LineageRelation.DescribedReads, curated),
                ("orders-u2-publish-lin", LineageRelation.DescribedWrites, landingFile),
            ],
            described.Edges.Select(e => (e.Flow!, e.Relation, e.ObjectKey)));
        Assert.All(described.Edges, e => Assert.Equal(LineageTier.Declared, e.Tier));
    }

    [Fact]
    public async Task WhereADescribedChainEnds_IsAFact_NotAFault()
    {
        using var estate = Estate()
            .Flow("lineage/curate.yaml", Curate)
            .Flow("lineage/publish.yaml", Publish);

        var described = (await estate.ComputeAsync()).Described;

        // Written by a description: not an end. Read and marked: the origin. Read and unmarked: simply not described.
        Assert.Equal(LineageDescribedBoundary.None, described.Objects.Single(o => o.Name == "curated.orders").Boundary);
        Assert.Equal(LineageDescribedBoundary.Origin, described.Objects.Single(o => o.Name == "sales.orders").Boundary);
        Assert.Equal(LineageDescribedBoundary.Open, described.Objects.Single(o => o.Name == "sales.currencies").Boundary);
        Assert.Empty(described.Warnings);
    }

    [Fact]
    public async Task ColumnMappings_NameTheObjectsOnBothSides()
    {
        using var estate = Estate()
            .Flow("lineage/curate.yaml", Curate)
            .Flow("lineage/publish.yaml", Publish);

        var report = await estate.ComputeAsync();
        var landingFile = Key(report, "land-orders", LineageRelation.Reads, "landing/orders");

        var publish = report.Described.Columns.Where(c => c.Flow == "orders-u2-publish-lin").ToList();
        Assert.Equal([("order_id", 1), ("amount", 2), ("loaded_at", 3)], publish.Select(c => (c.TargetColumn, c.Ordinal)));
        Assert.All(publish, c => Assert.Equal(landingFile, c.TargetObjectKey));
        Assert.Equal(("described:databricks|||curated.orders", "id"), (publish[0].Sources.Single().ObjectKey, publish[0].Sources.Single().Column));
        Assert.Empty(publish[2].Sources);

        var curate = report.Described.Columns.Single(c => c.Flow == "orders-u1-curate-lin" && c.TargetColumn == "amount_eur");
        Assert.Equal(
            [("described:erp|||sales.orders", "amount"), ("described:erp|||sales.currencies", "rate")],
            curate.Sources.Select(s => (s.ObjectKey, s.Column)));
        Assert.Equal("amount * rate", curate.Note);
    }

    [Fact]
    public async Task AFlowThatWritesSeveralObjects_IsNarrowedWithAt()
    {
        // A file flow that types its columns declares the table it loads and the typed view over it.
        using var estate = Estate()
            .Flow("orders/land-typed.yaml", """
                name: land-typed
                source:
                  type: csv
                  location: landing/typed
                target:
                  connection: ${env:SQLFLOW_CONN_DWH}
                  schema: pre
                  table: Orders
                transform:
                  inferTypes: true
                """)
            .Flow("lineage/ambiguous.yaml", """
                flowType: lin
                name: orders-d1-ambiguous-lin
                system: fabric
                reads:
                  - { fedBy: land-typed }
                writes:
                  - { name: lakehouse.orders_copy }
                """)
            .Flow("lineage/narrowed.yaml", """
                flowType: lin
                name: orders-d2-narrowed-lin
                system: fabric
                reads:
                  - { fedBy: land-typed, at: Orders, as: pre }
                writes:
                  - { name: lakehouse.orders_copy, columns: { id: pre.id } }
                """)
            .Flow("lineage/nowhere.yaml", """
                flowType: lin
                name: orders-d3-nowhere-lin
                system: fabric
                reads:
                  - { fedBy: land-typed, at: Customers, as: pre }
                writes:
                  - { name: lakehouse.customers_copy, columns: { id: pre.id, made: } }
                """);

        var report = await estate.ComputeAsync();
        var described = report.Described;
        var written = report.Edges
            .Where(e => e.Flow == "land-typed" && e.ViaModule is null && e.Relation is LineageRelation.Writes or LineageRelation.Creates)
            .Select(e => e.ObjectKey).Distinct().ToList();
        Assert.True(written.Count > 1, "the fixture needs a flow that writes more than one object");

        Assert.Empty(described.Steps.Single(s => s.Name == "orders-d1-ambiguous-lin").Reads);
        Assert.Contains(described.Warnings, w => w.Contains("orders-d1-ambiguous-lin", StringComparison.Ordinal)
            && w.Contains($"that flow writes {written.Count} objects", StringComparison.Ordinal)
            && w.Contains("Add 'at' with a part of the name of the one meant.", StringComparison.Ordinal));

        // A whole name wins over a part: 'Orders' is the table, not also the view named after it.
        var narrowed = Assert.Single(described.Steps.Single(s => s.Name == "orders-d2-narrowed-lin").Reads);
        Assert.Equal("Orders", report.Objects.Single(o => o.Key == narrowed.ObjectKey).Name);
        Assert.Equal("land-typed", narrowed.ViaFlow);

        // A source whose read could not be placed is left out of the mapping; the column and the step stay.
        Assert.Contains(described.Warnings, w => w.Contains("orders-d3-nowhere-lin", StringComparison.Ordinal)
            && w.Contains("at 'Customers', which matches none of the objects", StringComparison.Ordinal));
        Assert.Contains(described.Warnings, w => w.Contains("maps columns from 'pre', whose reads entry could not be placed", StringComparison.Ordinal));
        var nowhere = described.Columns.Where(c => c.Flow == "orders-d3-nowhere-lin").ToList();
        Assert.Equal(["id", "made"], nowhere.Select(c => c.TargetColumn));
        Assert.All(nowhere, c => Assert.Empty(c.Sources));
    }

    [Fact]
    public async Task AConnectionThatCannotBePlaced_IsLeftOut_AndTheStepStays()
    {
        using var estate = Estate()
            .Flow("lineage/curate.yaml", Curate)
            .Flow("lineage/scm.yaml", """
                flowType: scm
                name: snapshot-dwh
                connections:
                  dwh: ${env:SQLFLOW_CONN_DWH}
                source: { server: dwh }
                repository: { url: https://example.com/acme/schema.git }
                """)
            .Flow("lineage/odd.yaml", """
                flowType: lin
                name: orders-x-odd-lin
                system: databricks
                reads:
                  - { fedBy: load-orderz }
                  - { fedBy: orders-u1-curate-lin }
                  - { fedBy: snapshot-dwh }
                  - { name: curated.orders }
                writes:
                  - { feeds: land-orders }
                  - { feeds: LAND-ORDERS, at: landing }
                """);

        var report = await estate.ComputeAsync();
        var described = report.Described;
        var odd = described.Steps.Single(s => s.Name == "orders-x-odd-lin");

        Assert.Equal("described:databricks|||curated.orders", Assert.Single(odd.Reads).ObjectKey);
        Assert.Single(odd.Writes);
        Assert.Contains(described.Warnings, w => w.Contains("names flow 'load-orderz' with 'fedBy', which this repository does not declare", StringComparison.Ordinal));
        Assert.Contains(described.Warnings, w => w.Contains("names 'orders-u1-curate-lin' with 'fedBy', which is itself a lineage description", StringComparison.Ordinal));
        if (report.Warnings.All(w => !w.Contains("lineage/scm.yaml", StringComparison.Ordinal)))
        {
            Assert.Contains(described.Warnings, w => w.Contains("names flow 'snapshot-dwh' with 'fedBy', which takes no part in lineage", StringComparison.Ordinal));
        }

        Assert.Contains(described.Warnings, w => w.Contains("writes entry 2 is the same object as an earlier writes entry", StringComparison.Ordinal));
        Assert.All(described.Warnings, w => Assert.StartsWith("lineage/odd.yaml: lineage description 'orders-x-odd-lin'", w, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANameTakenTwice_OrByAFlowThatRuns_IsKeptOnce()
    {
        using var estate = Estate()
            .Flow("lineage/a-curate.yaml", Curate)
            .Flow("lineage/b-curate.yaml", Curate.Replace("system: databricks", "system: fabric", StringComparison.Ordinal))
            .Flow("lineage/clash.yaml", """
                flowType: lin
                name: LOAD-ORDERS
                system: databricks
                writes:
                  - { name: somewhere }
                """);

        var described = (await estate.ComputeAsync()).Described;

        var kept = Assert.Single(described.Steps);
        Assert.Equal(("orders-u1-curate-lin", "lineage/a-curate.yaml", "databricks"), (kept.Name, kept.File, kept.System));
        Assert.Contains(described.Warnings, w => w.Contains("is declared by 2 documents (lineage/a-curate.yaml, lineage/b-curate.yaml); the first is used", StringComparison.Ordinal));
        Assert.Contains(described.Warnings, w => w.StartsWith("lineage/clash.yaml: lineage description 'LOAD-ORDERS' has the name of the ing flow in orders/load-orders.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADescriptionNamedLikeAFlow_AndReadBeforeIt_NeverStandsInThatFlowsPlace()
    {
        // The estate with a run on record for the loading flow, so the observed tier has something to lose.
        using var estate = Estate()
            .Run("load-orders", new DateTime(2026, 6, 10, 6, 0, 0, DateTimeKind.Utc),
                ("source.select", "SELECT * FROM [DW].[pre].[Orders];"),
                ("upsert.insert", "INSERT INTO [DW].[dbo].[Orders] SELECT * FROM [DW].[pre].[Orders];"),
                ("postprocess", "INSERT INTO [DW].[audit].[LoadLog] (At) SELECT GETUTCDATE();"));
        var without = await estate.ComputeAsync();
        Assert.Contains(without.Edges, e => e.Tier == LineageTier.Observed);

        // Documents read before every flow (their folder sorts first): one with the name of a flow that runs, in
        // another case, and one name declared twice. The first document in path order becomes the pipeline of a name,
        // so without a rule of its own the description would be the flow.
        estate
            .Flow("_first/clash.yaml", """
                flowType: lin
                name: LOAD-ORDERS
                system: databricks
                writes:
                  - { name: somewhere }
                """)
            .Flow("_first/twice-a.yaml", Curate)
            .Flow("_first/twice-b.yaml", Curate);
        var with = await estate.ComputeAsync();

        // The computed lineage, its observed edges and its warnings are what they were.
        Assert.Equal(Computed(without), Computed(with));
        Assert.Contains(with.Described.Warnings, w => w.Contains("has the name of the ing flow in orders/load-orders.yaml", StringComparison.Ordinal));
        Assert.Contains(with.Described.Warnings, w => w.Contains("is declared by 2 documents", StringComparison.Ordinal));

        // And the flow set a catalog sync makes its pipelines from holds the flow under its name, never the description.
        var collected = new FlowSetCollector(YamlDocumentLoader.CreateDefault()).Collect(estate.Root);
        var named = Assert.Single(collected.Flows, f => string.Equals(f.Node.Name, "load-orders", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ing", named.Node.Kind);
        Assert.False(named.IsLineageDescription);
        Assert.Equal("_first/twice-a.yaml", Assert.Single(collected.Flows, f => f.IsLineageDescription).Node.File);
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("their facts merge", StringComparison.Ordinal));
        Assert.Equal(3, collected.Descriptions.Count); // every description is still handed to the described lineage
    }

    [Fact]
    public async Task AnAliasOfAnObjectReadTwice_StillNamesIt_SoItsColumnsKeepTheirSource()
    {
        using var estate = Estate()
            .Flow("lineage/twice.yaml", """
                flowType: lin
                name: orders-d2-twice-lin
                system: fabric
                reads:
                  - { fedBy: load-orders, as: a }
                  - { fedBy: load-orders, at: Orders, as: b }
                writes:
                  - name: lake.orders_copy
                    columns:
                      id: a.Id
                      day: b.OrderDate
                """);

        var described = (await estate.ComputeAsync()).Described;

        Assert.Contains(described.Warnings, w => w.Contains("is the same object as an earlier reads entry", StringComparison.Ordinal));
        Assert.DoesNotContain(described.Warnings, w => w.Contains("could not be placed", StringComparison.Ordinal));
        Assert.Single(described.Edges, e => e.Relation == LineageRelation.DescribedReads);
        Assert.Equal(["day", "id"], described.Columns.Select(c => c.TargetColumn).Order(StringComparer.Ordinal));
        Assert.All(described.Columns, c => Assert.Single(c.Sources));
    }

    [Fact]
    public async Task ADescriptionThatDoesNotLoad_IsABrokenFlow_AndBlindsNothing()
    {
        using var estate = Estate()
            .Flow("lineage/curate.yaml", Curate)
            .Flow("lineage/typo.yaml", """
                flowType: lin
                name: orders-u9-typo-lin
                system: databricks
                wirtes:
                  - { feeds: land-orders }
                """);

        var report = await estate.ComputeAsync();

        Assert.Equal(["orders-u1-curate-lin"], report.Described.Steps.Select(s => s.Name));
        Assert.Equal([["land-orders"], ["load-orders"], ["watch-orders"]], LineageEstateHarness.Waves(report));
    }

    [Fact]
    public void Rekey_FollowsAnObjectToItsAdoptedIdentity()
    {
        var described = new LineageDescribed
        {
            Steps =
            [
                new LineageDescribedStep
                {
                    Name = "s", File = "s.yaml", System = "databricks",
                    Reads = [new LineageDescribedEndpoint { ObjectKey = "weak", ObjectName = "T" }],
                    Writes = [new LineageDescribedEndpoint { ObjectKey = "described:databricks|||x", ObjectName = "x" }],
                },
            ],
            Edges =
            [
                new LineageEdge { Flow = "s", Relation = LineageRelation.DescribedReads, ObjectKey = "weak", Tier = LineageTier.Declared },
                new LineageEdge { Flow = "s", Relation = LineageRelation.DescribedReads, ObjectKey = "strong", Tier = LineageTier.Declared },
            ],
            Columns =
            [
                new LineageColumnMapping
                {
                    Flow = "s", TargetObjectKey = "described:databricks|||x", TargetObjectName = "x", TargetColumn = "c", Ordinal = 1,
                    Sources = [new LineageColumnSource { ObjectKey = "weak", ObjectName = "T", Column = "c" }],
                },
            ],
        };

        var rekeyed = described.Rekey(key => key == "weak" ? "strong" : key);

        Assert.Equal("strong", rekeyed.Steps[0].Reads[0].ObjectKey);
        Assert.Equal("described:databricks|||x", rekeyed.Steps[0].Writes[0].ObjectKey);
        Assert.Equal("strong", Assert.Single(rekeyed.Edges).ObjectKey);
        Assert.Equal("strong", rekeyed.Columns[0].Sources[0].ObjectKey);
        Assert.Same(LineageDescribed.Empty, LineageDescribed.Empty.Rekey(key => key + "!"));
    }
}
