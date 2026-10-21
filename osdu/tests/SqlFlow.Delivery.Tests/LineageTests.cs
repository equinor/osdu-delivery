using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the module's flows contribute to SQLFlow's lineage (osdu/docs/reference/concepts/lineage.md): a delivery flow
/// reads its ingestion tables, its payload files and the mapping it pins, and writes the OSDU type its mapping fills;
/// the mapping is a node of its own, which reads the cache types it names, the cache types holding what its ids are
/// checked against, and the kinds it searches; a cache flow reads OSDU types and writes partition cache types; a
/// retrieval flow reads OSDU types and lands files; a dimension flow reads OSDU types and the dictionaries it cleans values
/// through, and writes its tables; an inventory flow reads OSDU types only. The sample estate is copied to a folder of
/// its own, scanned with the module's kinds registered, and its waves ordered from the files the pre flows read to the
/// OSDU types the delivery flows write and the flows that read them back. A mapping that cannot be read, or that lies outside the checkout,
/// costs a flow its OSDU nodes with a warning and nothing else; a template lineage cannot read costs it the cache types
/// only that template tells of.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class LineageTests : IDisposable
{
    private const string Platform = "${env:OSDU_URL}";

    /// <summary>
    /// The partition the sample estate and the fixture documents name: their flows name it under <c>partitions</c> (the
    /// retrieval in its header), and lineage keys a partition as it is written.
    /// </summary>
    private const string Partition = "dev";

    private static readonly DateTime Utc = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The folder the sample source occupies in the repository: a repository is laid out one folder per source.</summary>
    private const string Source = "welldb";

    /// <summary>The checkout the flows are scanned in. Everything the source holds is under <see cref="Source"/> in it.</summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), "osdu-lineage-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>
    /// The welldb estate as the repository holds it, with the fixture documents (a wellbore chain, a source of several
    /// interfaces, a retrieval) beside it, so one scan orders flows of every kind the module adds; its template folder holds
    /// the sample's templates and the fixture mappings' own, which every mapping of the estate reads.
    /// </summary>
    public LineageTests()
    {
        Directory.CreateDirectory(_root);
        foreach (var folder in new[] { "flows", "reference", "cache", "dictionaries", "mappings", "templates", "data" })
        {
            Copy(Path.Combine(Samples.Source, folder), Path.Combine(_root, Source, folder));
        }

        File.Copy(Path.Combine(Samples.Source, "schedules.yaml"), Path.Combine(_root, Source, "schedules.yaml"));
        foreach (var folder in new[] { "flows", "mappings" })
        {
            Copy(Path.Combine(Samples.FixtureDocuments, folder), Path.Combine(_root, Source, folder), overwrite: true);
        }

        Copy(Samples.FixtureTemplateFiles, Path.Combine(_root, Source, "templates"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Copy(string from, string to, bool overwrite = false)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    /// <summary>A path inside the source's folder, named the way the source holds it (<c>flows/x.yaml</c>).</summary>
    private string PathOf(string relative) => Path.Combine(_root, Source, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The same path as the checkout records it, which is what lineage names a node and keys a warning by.</summary>
    private static string InRepo(string relative) => Source + "/" + relative;

    /// <summary>
    /// The loader every host registers: its delivery kind reads the templates the mappings fill from the template folders on
    /// their paths in the checkout, unless <paramref name="templates"/> says otherwise.
    /// </summary>
    private static YamlDocumentLoader Loader(MappingTemplateSource? templates = null)
    {
        var documents = new DeliveryDocumentLoader();
        return YamlDocumentLoader.CreateDefault(
            [
                new DeliveryFlowKind(documents, templates ?? new MappingTemplateSource()),
                new CacheFlowKind(documents), new RetrievalFlowKind(documents), new AssertionFlowKind(documents),
                new DimensionFlowKind(documents), new InventoryFlowKind(documents),
            ]);
    }

    /// <summary>
    /// A document described as a scan describes it: with every registered document of the checkout at hand, in path
    /// order, which is where a mapping's lineage finds the cache types holding what its ids are checked against.
    /// </summary>
    private RegisteredFlowLineage Describe(string relative, YamlDocumentLoader? loader = null)
    {
        loader ??= Loader();
        var path = PathOf(relative);
        var document = Assert.IsAssignableFrom<RegisteredFlowDocument>(loader.LoadFile(path));
        var estate = new List<RegisteredEstateDocument>();
        foreach (var file in Directory.EnumerateFiles(_root, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            try
            {
                if (loader.LoadFile(file) is RegisteredFlowDocument registered)
                {
                    estate.Add(new RegisteredEstateDocument(file, registered));
                }
            }
            catch (SqlFlowException)
            {
                // Not a flow document (a mapping, a schedule library), exactly as the scan passes over it.
            }
        }

        return document.DescribeLineage(new RegisteredLineageContext(path, _root) { Estate = estate });
    }

    private (CollectionResult Collected, LineageReport Report) Scan(YamlDocumentLoader? loader = null)
    {
        var collected = new FlowSetCollector(loader ?? Loader()).Collect(_root);
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("skipped", StringComparison.Ordinal));
        return (collected, LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc));
    }

    /// <summary>The node of a mapping filed in the source's mappings folder, as the sample partition and platform render it.</summary>
    private static string MappingKey(string reference)
        => NodeKey.For(ServerIdentity.Dataset(OsduLineage.MappingSystem, Platform), Partition, InRepo("mappings"), reference);

    /// <summary>What the one mapping a flow reads is derived from, in the order lineage declares it.</summary>
    private static string Sources(RegisteredFlowLineage lineage)
        => string.Join(", ", Assert.Single(lineage.Derivations).From.Select(d => $"{d.System}/{d.Namespace}/{d.Group}/{d.Name}"));

    private static string TypeKey(string kind)
        => NodeKey.For(ServerIdentity.Dataset(OsduLineage.TypeSystem, Platform), Partition, OsduKind.Group(kind), kind);

    private static string CacheKey(string name)
        => NodeKey.For(ServerIdentity.Dataset(OsduLineage.CacheSystem, null), Partition, OsduLineage.CacheGroup, name);

    private static string Datasets(RegisteredFlowLineage lineage, LineageRelation relation)
        => string.Join(", ", lineage.Datasets.Where(d => d.Relation == relation).Select(d => $"{d.System}/{d.Namespace}/{d.Group}/{d.Name}"));

    /// <summary>The OSDU types a flow writes, without the cache types its cache steps write.</summary>
    private static string TypeWrites(RegisteredFlowLineage lineage)
        => string.Join(", ", lineage.Datasets.Where(d => d.Relation == LineageRelation.Writes && d.System != "osdu-cache").Select(d => $"{d.System}/{d.Namespace}/{d.Group}/{d.Name}"));

    private static int WaveOf(LineageReport report, string flow)
    {
        var wave = report.ExecutionPlan.Waves.FirstOrDefault(w => w.Flows.Contains(flow, StringComparer.OrdinalIgnoreCase));
        Assert.True(wave is not null, $"'{flow}' is in no wave.");
        return wave!.Wave;
    }

    /// <summary>Replaces text in a copied document, whose line endings are read as LF whatever the checkout wrote.</summary>
    private void Rewrite(string relative, string from, string to)
    {
        var path = PathOf(relative);
        var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(from, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    [Fact]
    public void A_source_declares_every_interface_read_and_every_interface_write()
    {
        // One document, two interfaces: the graph has to carry each interface's own table and its own OSDU type, so a
        // source is ordered after everything any of its interfaces reads, and shows as writing everything they write
        // (osdu/docs/reference/concepts/lineage.md, A source with interfaces).
        var lineage = Describe("flows/wells-source-03-interfaces-delivery.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal(
            [
                "OsduData.silver.Wellbore", "OsduData.silver.WellboreAlias", "OsduData.silver.WellLog",
                "OsduData.silver.WellLogCurve", "OsduData.silver.WellboreTrajectory", "OsduData.silver.WellboreTrajectoryStation",
                "OsduData.silver.Document",
            ],
            lineage.Objects.Select(o => $"{o.Database}.{o.Schema}.{o.Name}").ToList());
        Assert.All(lineage.Objects, o => Assert.Equal(LineageRelation.Reads, o.Relation));
        var writes = Datasets(lineage, LineageRelation.Writes);
        Assert.Contains(Samples.WellboreKind, writes, StringComparison.Ordinal);
        Assert.Contains(Samples.WellLogKind, writes, StringComparison.Ordinal);
        Assert.Contains("work-product-component--Document", writes, StringComparison.Ordinal);

        Assert.Contains("work-product-component--WellboreTrajectory", writes, StringComparison.Ordinal);

        // The documents interface streams its files and the surveys their stations, so the source reads both folders.
        Assert.Contains(lineage.Files, f => f.Location == "../data/document-files" && f.Relation == LineageRelation.Reads);
        Assert.Contains(lineage.Files, f => f.Location == "../data/stations" && f.Relation == LineageRelation.Reads);

        // And the estate orders it after the ingestion flows that fill both of those tables.
        var (_, report) = Scan();
        Assert.True(WaveOf(report, "wells-wellbore-02-header-ing") < WaveOf(report, "wells-source-03-interfaces-delivery"));
        Assert.True(WaveOf(report, "welldb-welllog-02-header-ing") < WaveOf(report, "wells-source-03-interfaces-delivery"));
    }

    [Fact]
    public void A_delivery_flow_reads_its_tables_payload_files_and_mapping_and_writes_its_mappings_type()
    {
        var lineage = Describe("flows/welldb-welllog-03-header-delivery.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal(
            ["OsduData.silver.WellLog", "OsduData.silver.WellLogCurve"],
            lineage.Objects.Select(o => $"{o.Database}.{o.Schema}.{o.Name}").ToList());
        Assert.All(lineage.Objects, o => Assert.Equal(LineageRelation.Reads, o.Relation));
        var payload = Assert.Single(lineage.Files);
        Assert.Equal((LineageRelation.Reads, "../data/curves", "chunk_*.parquet"), (payload.Relation, payload.Location, payload.FilePattern));

        // The flow itself reads no cache type and searches no kind: its mapping does, and the flow reads the mapping. Its
        // before step writes the lookup tables it reads again before it renders (cache.before), so the flows reading them
        // run after it.
        Assert.Equal(
            ["CurveDictionary", "DepthUnitAlias", "UnitAlias"],
            lineage.Datasets.Where(d => d.System == "osdu-cache").Select(d => d.Relation == LineageRelation.Writes ? d.Name : $"read {d.Name}").Order(StringComparer.Ordinal));
        var written = Assert.Single(lineage.Datasets, d => d.System != "osdu-cache");
        Assert.Equal(
            (LineageRelation.Writes, "osdu-type", "dev", "work-product-component", "osdu:wks:work-product-component--WellLog:1.4.0"),
            (written.Relation, written.System, written.Namespace, written.Group, written.Name));
        Assert.Equal((Platform, (char?)':'), (written.Instance, written.Separator));

        // The mapping is a node of the partition and platform the flow renders it for, grouped under the folder it is
        // filed in and named by its reference.
        var mapping = Assert.Single(lineage.Derivations).Dataset;
        Assert.Equal(
            (LineageRelation.Reads, "osdu-mapping", Platform, "dev", InRepo("mappings"), "WellLog@1.4.0"),
            (mapping.Relation, mapping.System, mapping.Instance, mapping.Namespace, mapping.Group, mapping.Name));

        // What the mapping reads. First what it names: the unit tables, the curve dictionary and the partition's units
        // (which a unit is found among when the table does not translate it). Then what its ids are checked against: the
        // types of the partition's cache holding the entity type each ref and id builds, which the mapping never names.
        // Then the wellbores it searches the platform for, so whatever delivers wellbores there is ordered before the flow.
        // Last every entity type the OSDU order says its records wait for, as every kind of it, so the flows delivering
        // those types are ordered before it however the mapping fills the reference.
        Assert.Equal(
            "osdu-cache/dev/cache/CurveDictionary, osdu-cache/dev/cache/DepthUnitAlias, "
            + "osdu-cache/dev/cache/UnitAlias, osdu-cache/dev/cache/UnitOfMeasure, "
            + "osdu-cache/dev/cache/LogCurveBusinessValue, osdu-cache/dev/cache/LogCurveFamily, "
            + "osdu-cache/dev/cache/LogCurveMainFamily, osdu-cache/dev/cache/LogCurveType, osdu-cache/dev/cache/LogType, "
            + "osdu-cache/dev/cache/VerticalMeasurementType, osdu-cache/dev/cache/WellLogSamplingDomainType, "
            + "osdu-type/dev/master-data/osdu:wks:master-data--Wellbore:*, " + OrderedByTheWellLogMapping,
            Sources(lineage));
        var sources = lineage.Derivations[0].From;
        Assert.All(sources, d => Assert.Equal(LineageRelation.Reads, d.Relation));
        Assert.All(sources.Where(d => d.System == "osdu-cache"), d => Assert.Null(d.Instance));
        Assert.All(sources.Where(d => d.System == "osdu-type"), d => Assert.Equal(Platform, d.Instance));
    }

    [Fact]
    public void A_record_only_delivery_flow_reads_no_payload_files()
    {
        var lineage = Describe("flows/wells-wellbore-03-header-delivery.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Empty(lineage.Files);
        Assert.Equal("osdu-type/dev/master-data/osdu:wks:master-data--Wellbore:1.3.0", Datasets(lineage, LineageRelation.Writes));
    }

    [Fact]
    public void A_file_protocol_flow_also_writes_the_dataset_kind_it_registers_files_as()
    {
        Rewrite("flows/welldb-welllog-03-header-delivery.yaml", "protocol: ddms", "protocol: file");
        Rewrite("flows/welldb-welllog-03-header-delivery.yaml", "    sessionThresholdChunks: 1\n", "    datasetKind: osdu:wks:dataset--File.Generic:1.0.0\n");

        // The file route reaches its services under the endpoint, so the DDMS's own root goes with the DDMS route.
        Rewrite("flows/welldb-welllog-03-header-delivery.yaml", "    ddmsRoot: /api/os-wellbore-ddms\n", string.Empty);

        var lineage = Describe("flows/welldb-welllog-03-header-delivery.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal(
            "osdu-type/dev/work-product-component/osdu:wks:work-product-component--WellLog:1.4.0, osdu-type/dev/dataset/osdu:wks:dataset--File.Generic:1.0.0",
            TypeWrites(lineage));
    }

    [Fact]
    public void A_cache_flow_reads_its_kinds_and_writes_its_partitions_cache_types()
    {
        Directory.CreateDirectory(Path.Combine(_root, Source, "cache"));
        File.WriteAllText(PathOf("cache/multi-type-cache.yaml"), """
            flowType: cache
            name: multi-type-cache
            source:
              endpoint: ${env:OSDU_URL}
              headers:
                data-partition-id: ${env:OSDU_DATA_PARTITION}
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                name: UnitOfMeasure
                fields: [data.Code, data.Name]
              - kind: "osdu:wks:reference-data--LogCurveBusinessValue:*"
                name: LogCurveBusinessValue
                fields: [data.Code, data.Name]
            """);

        var lineage = Describe("cache/multi-type-cache.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Empty(lineage.Objects);
        Assert.Empty(lineage.Files);
        Assert.Equal(
            "osdu-type/${env:OSDU_DATA_PARTITION}/reference-data/osdu:wks:reference-data--UnitOfMeasure:*, "
            + "osdu-type/${env:OSDU_DATA_PARTITION}/reference-data/osdu:wks:reference-data--LogCurveBusinessValue:*",
            Datasets(lineage, LineageRelation.Reads));
        Assert.Equal(
            "osdu-cache/${env:OSDU_DATA_PARTITION}/cache/UnitOfMeasure, osdu-cache/${env:OSDU_DATA_PARTITION}/cache/LogCurveBusinessValue",
            Datasets(lineage, LineageRelation.Writes));
    }

    [Fact]
    public void A_retrieval_flow_reads_its_kinds_and_lands_record_files_and_a_manifest()
    {
        var lineage = Describe("flows/wells-osdu-04-metadata-retrieval.yaml");

        Assert.Equal(
            "osdu-type/dev/reference-data/osdu:wks:reference-data--UnitOfMeasure:*, osdu-type/dev/reference-data/osdu:wks:reference-data--LogCurveBusinessValue:*, "
            + "osdu-type/dev/reference-data/osdu:wks:reference-data--VerticalMeasurementType:*, osdu-type/dev/reference-data/osdu:wks:reference-data--TrajectoryStationPropertyType:*, "
            + "osdu-type/dev/master-data/osdu:wks:master-data--Wellbore:*",
            Datasets(lineage, LineageRelation.Reads));
        Assert.Empty(Datasets(lineage, LineageRelation.Writes));
        Assert.Equal(
            ["samples/wells/out/metadata|part-*.jsonl", "samples/wells/out/metadata|manifest.json"],
            lineage.Files.Select(f => $"{f.Location}|{f.FilePattern}").ToList());
        Assert.All(lineage.Files, f => Assert.Equal(LineageRelation.Writes, f.Relation));

        // The runner resolves this relative location against its working directory, not the flow file: lineage says so.
        var warning = Assert.Single(lineage.Warnings);
        Assert.Contains("target.location 'samples/wells/out/metadata' is a relative path", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gzipped_retrieval_to_a_storage_uri_lands_gzipped_files_without_a_warning()
    {
        Rewrite("flows/wells-osdu-04-metadata-retrieval.yaml", "location: samples/wells/out/metadata",
            "location: https://lake.dfs.core.windows.net/raw/osdu/{date}\n  compression: gzip");

        var lineage = Describe("flows/wells-osdu-04-metadata-retrieval.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal("part-*.jsonl.gz", lineage.Files[0].FilePattern);
    }

    [Fact]
    public void The_sample_estate_orders_files_pre_ing_delivery_cache_and_retrieval_flows()
    {
        var (collected, report) = Scan();

        Assert.Empty(report.ExecutionPlan.Unordered);
        Assert.True(WaveOf(report, "welldb-welllog-01-header-pre") < WaveOf(report, "welldb-welllog-02-header-ing"));
        Assert.True(WaveOf(report, "welldb-welllog-02-header-ing") < WaveOf(report, "welldb-welllog-03-header-delivery"));
        Assert.True(WaveOf(report, "welldb-welllog-02-curves-ing") < WaveOf(report, "welldb-welllog-03-header-delivery"));
        Assert.True(WaveOf(report, "wells-wellbore-02-header-ing") < WaveOf(report, "wells-wellbore-03-header-delivery"));

        // The well log flow searches for the wellbores the wellbore flow delivers, and renders its units against the cache.
        Assert.True(WaveOf(report, "wells-wellbore-03-header-delivery") < WaveOf(report, "welldb-welllog-03-header-delivery"));
        Assert.True(WaveOf(report, "welldb-lookups-00-cache") < WaveOf(report, "welldb-welllog-03-header-delivery"));
        Assert.True(WaveOf(report, "wells-wellbore-03-header-delivery") < WaveOf(report, "wells-osdu-04-metadata-retrieval"));

        // The sample fills an empty partition in the order its records refer to each other, each step after what it reads:
        // the reference data, the unit quantities among it, which puts what it delivers into the cache as it runs, the wells
        // checked against it, the wellbores that search for them, and the logs that search for the wellbores.
        string[] order =
        [
            "welldb-ref-03-delivery", "welldb-well-03-delivery", "welldb-wellbore-03-delivery", "welldb-welllog-03-header-delivery",
        ];
        Assert.All(order.Zip(order.Skip(1)), step => Assert.True(
            WaveOf(report, step.First) < WaveOf(report, step.Second), $"{step.First} runs before {step.Second}"));

        // The reference source writes the cache types its records go into, so the nightly refresh of the same types runs
        // after it, and the wells, which read them, run after both.
        Assert.True(WaveOf(report, "welldb-ref-03-delivery") < WaveOf(report, "osdu-reference-00-cache"));
        Assert.Contains(report.Edges, e => e.Flow == "welldb-ref-03-delivery" && e.Relation == LineageRelation.Writes && e.ObjectKey == CacheKey("UnitQuantity"));

        // The assertion flow reads the kind the well log delivery writes, so its tests run once the logs have landed.
        Assert.True(WaveOf(report, "welldb-welllog-03-header-delivery") < WaveOf(report, "welldb-welllog-04-header-assertion"));
        Assert.Contains(report.Edges, e => e.Flow == "welldb-welllog-04-header-assertion" && e.Relation == LineageRelation.Reads && e.ObjectKey == TypeKey(Samples.WellLogKind));
        Assert.DoesNotContain(report.Edges, e => e.Flow == "welldb-welllog-04-header-assertion" && e.Relation == LineageRelation.Writes);

        // The surveys and the tops land and are keyed by chains of their own, and the source delivering them searches for
        // the wellbores they were run in and refers to the station property types the reference source delivers.
        foreach (var (pre, ing) in new[]
        {
            ("welldb-survey-01-header-pre", "welldb-survey-02-header-ing"),
            ("welldb-survey-01-properties-pre", "welldb-survey-02-properties-ing"),
            ("welldb-markerset-01-header-pre", "welldb-markerset-02-header-ing"),
            ("welldb-markerset-01-markers-pre", "welldb-markerset-02-markers-ing"),
        })
        {
            Assert.True(WaveOf(report, pre) < WaveOf(report, ing), $"{pre} runs before {ing}");
            Assert.True(WaveOf(report, ing) < WaveOf(report, "welldb-survey-03-delivery"), $"{ing} runs before the survey source");
        }

        Assert.True(WaveOf(report, "welldb-wellbore-03-delivery") < WaveOf(report, "welldb-survey-03-delivery"));
        Assert.True(WaveOf(report, "welldb-ref-03-delivery") < WaveOf(report, "welldb-survey-03-delivery"));
        Assert.Contains(report.Edges, e => e.Flow == "welldb-ref-03-delivery" && e.Relation == LineageRelation.Writes && e.ObjectKey == CacheKey("TrajectoryStationPropertyType"));
        Assert.Contains(report.Edges, e => e.Flow == "welldb-survey-03-delivery" && e.Relation == LineageRelation.Writes && e.ObjectKey == TypeKey("osdu:wks:work-product-component--WellboreTrajectory:1.3.0"));
        Assert.Contains(report.Edges, e => e.Flow == "welldb-survey-03-delivery" && e.Relation == LineageRelation.Writes && e.ObjectKey == TypeKey("osdu:wks:work-product-component--WellboreMarkerSet:1.2.1"));

        // What reads OSDU back runs once what it reads has landed: the wells are tested, the wellbores read back into the
        // lake, the dimensions built over the logs, and the inventory taken of every kind the estate delivers.
        foreach (var (writer, reader) in new[]
        {
            ("welldb-well-03-delivery", "welldb-well-04-assertion"),
            ("welldb-wellbore-03-delivery", "welldb-wellbore-04-retrieval"),
            ("welldb-welllog-03-header-delivery", "welldb-welllog-05-dimensions"),
            ("welldb-well-03-delivery", "welldb-osdu-06-inventory"),
            ("welldb-wellbore-03-delivery", "welldb-osdu-06-inventory"),
            ("welldb-welllog-03-header-delivery", "welldb-osdu-06-inventory"),
        })
        {
            Assert.True(WaveOf(report, writer) < WaveOf(report, reader), $"{writer} runs before {reader}");
        }

        Assert.DoesNotContain(report.Edges, e => e.Flow == "welldb-well-04-assertion" && e.Relation == LineageRelation.Writes);
        Assert.DoesNotContain(report.Edges, e => e.Flow == "welldb-osdu-06-inventory" && e.Relation == LineageRelation.Writes);
        Assert.Contains(report.Edges, e => e.Flow == "welldb-welllog-05-dimensions" && e.Relation == LineageRelation.Reads && e.ObjectKey == TypeKey(Samples.WellLogKind));
        Assert.Contains(report.Edges, e => e.Flow == "welldb-welllog-05-dimensions" && e.Relation == LineageRelation.Writes);
        Assert.Contains(report.Edges, e => e.Flow == "welldb-welllog-05-dimensions" && e.Relation == LineageRelation.Reads
            && report.Objects.Any(o => o.Key == e.ObjectKey && o.Name == InRepo("dictionaries/curve-aliases.yaml")));

        // Every file node is where the flows read and land it: relative to the checkout (the dictionary the dimensions clean
        // their mnemonics through among them), or the lake folder the wellbores are read back into, named by its account and
        // container.
        var files = report.Objects.Where(o => o.Kind == LineageNodeKind.File).Select(o => o.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            [
                "az://welldbstorage/lake/osdu/wellbores",
                InRepo("cache/data/curve-dictionary"), InRepo("cache/data/depth-unit-alias"), InRepo("cache/data/unit-alias"),
                InRepo("data/curves"), InRepo("data/curves-meta"), InRepo("data/document-files"),
                InRepo("data/marker"), InRepo("data/marker-set"), InRepo("data/stations"),
                InRepo("data/survey"), InRepo("data/survey-properties"), InRepo("data/well"), InRepo("data/wellbore"),
                InRepo("data/wellbore-aliases"), InRepo("data/welllog"), InRepo("dictionaries/curve-aliases.yaml"),
                InRepo("flows/samples/wells/out/metadata"),
                InRepo("reference/data/log-curve-business-value"), InRepo("reference/data/log-curve-family"),
                InRepo("reference/data/log-curve-main-family"), InRepo("reference/data/log-curve-type"),
                InRepo("reference/data/log-type"), InRepo("reference/data/technical-assurance-type"),
                InRepo("reference/data/trajectory-station-property-type"),
                InRepo("reference/data/unit-of-measure"), InRepo("reference/data/unit-quantity"),
                InRepo("reference/data/vertical-measurement-type"), InRepo("reference/data/well-log-sampling-domain-type"),
            ],
            files);

        // The lookups cache flow's tables are loaded from the files in cache/data by the estate's own flows, each file landed
        // and keyed by a pre and an ing flow of its own, so it runs after them and writes each table into the cache.
        foreach (var (pre, ing, table) in new[]
        {
            ("welldb-unitalias-01-pre", "welldb-unitalias-02-ing", "UnitAlias"),
            ("welldb-depthunitalias-01-pre", "welldb-depthunitalias-02-ing", "DepthUnitAlias"),
            ("welldb-curvedictionary-01-pre", "welldb-curvedictionary-02-ing", "CurveDictionary"),
        })
        {
            Assert.True(WaveOf(report, pre) < WaveOf(report, ing), $"{pre} runs before {ing}");
            Assert.True(WaveOf(report, ing) < WaveOf(report, "welldb-lookups-00-cache"), $"{ing} runs before the lookups cache flow");
            Assert.Contains(report.Edges, e => e.Flow == "welldb-lookups-00-cache" && e.Relation == LineageRelation.Writes && e.ObjectKey == CacheKey(table));
        }

        // One node per exact OSDU type written, one per pattern read, and one per cache type, each captioned by its system.
        var types = report.Objects.Where(o => o.Kind == LineageNodeKind.Dataset).ToDictionary(o => o.Key, StringComparer.Ordinal);
        Assert.Contains(TypeKey(Samples.WellLogKind), types.Keys);
        Assert.Contains(TypeKey(Samples.WellboreKind), types.Keys);
        Assert.Contains(TypeKey("osdu:wks:master-data--Wellbore:*"), types.Keys);

        // Wellbores are searched for, never captured, so the cache holds no type for them.
        Assert.DoesNotContain(CacheKey("Wellbore"), types.Keys);
        Assert.Equal("osdu-type", ServerIdentity.DatasetSystem(TypeKey(Samples.WellLogKind)));

        Assert.Contains(report.Edges, e => e.Flow == "welldb-welllog-03-header-delivery" && e.Relation == LineageRelation.Writes && e.ObjectKey == TypeKey(Samples.WellLogKind));

        // The flow reads its mapping, and through it what the mapping reads: a lookup table it names, and the wellbores
        // its search finds, bound from the pattern to the kind the wellbore flow writes.
        var wellLog = MappingKey("WellLog@1.4.0");
        Assert.Contains(report.Edges, e => e.Flow == "welldb-welllog-03-header-delivery" && e.ViaModule is null && e.Relation == LineageRelation.Reads && e.ObjectKey == wellLog);
        Assert.Contains(report.Edges, e => e.Flow is null && e.ViaModule == wellLog && e.Relation == LineageRelation.Reads && e.ObjectKey == CacheKey("UnitAlias"));
        Assert.Contains(report.Edges, e => e.Flow is null && e.ViaModule == wellLog && e.Relation == LineageRelation.Reads && e.ObjectKey == TypeKey(Samples.WellboreKind));
        Assert.Contains(report.Edges, e => e.Flow == "welldb-welllog-03-header-delivery" && e.ViaModule == wellLog && e.Relation == LineageRelation.Reads && e.ObjectKey == CacheKey("UnitAlias"));
        Assert.Contains(report.Edges, e => e.Flow == "welldb-welllog-03-header-delivery" && e.ViaModule == wellLog && e.Relation == LineageRelation.Reads && e.ObjectKey == TypeKey(Samples.WellboreKind));
        Assert.DoesNotContain(report.Edges, e => e.Flow == "welldb-welllog-03-header-delivery" && e.ViaModule is null && e.Relation == LineageRelation.Reads && e.ObjectKey == CacheKey("UnitAlias"));

        // Its before step reads the lookup tables again before it renders, so the flow writes them as well.
        Assert.Contains(report.Edges, e => e.Flow == "welldb-welllog-03-header-delivery" && e.ViaModule is null && e.Relation == LineageRelation.Writes && e.ObjectKey == CacheKey("UnitAlias"));

        // The reference cache flow fills the types the mapping's ids are checked against, so it runs before the delivery.
        Assert.True(WaveOf(report, "osdu-reference-00-cache") < WaveOf(report, "welldb-welllog-03-header-delivery"));
        Assert.Contains(report.Edges, e => e.Flow is null && e.ViaModule == wellLog && e.ObjectKey == CacheKey("LogCurveFamily"));
        Assert.Contains(report.Edges, e => e.Flow is null && e.ViaModule == wellLog && e.ObjectKey == CacheKey("LogType"));

        // One node per mapping: the source's well log interface pins the mapping the sample flow pins, for the same
        // partition of the same platform, and both read the one node. Every mapping of the estate is one, captioned by its
        // system.
        Assert.Contains(report.Edges, e => e.Flow == "wells-source-03-interfaces-delivery" && e.ViaModule is null && e.Relation == LineageRelation.Reads && e.ObjectKey == wellLog);
        Assert.Equal(
            [
                "Document@1.0.0", "LogCurveBusinessValue@1.0.0", "LogCurveFamily@1.1.0", "LogCurveMainFamily@1.0.0", "LogCurveType@1.1.0",
                "LogType@1.0.0", "TechnicalAssuranceType@1.0.0", "TrajectoryStationPropertyType@1.0.0", "UnitOfMeasure@1.0.0",
                "UnitQuantity@1.0.0", "VerticalMeasurementType@1.0.3", "Well@1.2.0", "WellLog@1.4.0", "WellLogSamplingDomainType@1.0.0",
                "Wellbore@1.0.0", "Wellbore@1.3.0", "WellboreMarkerSet@1.2.1", "WellboreTrajectory@1.0.0", "WellboreTrajectory@1.3.0",
            ],
            types.Values.Where(o => ServerIdentity.DatasetSystem(o.Key) == OsduLineage.MappingSystem).Select(o => o.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Contains(report.Edges, e => e.Flow == "wells-osdu-04-metadata-retrieval" && e.Relation == LineageRelation.Reads && e.ObjectKey == TypeKey(Samples.WellboreKind));

        // The only thing the scan has to say about the module's flows is where the retrieval's files may land.
        Assert.Single(collected.Warnings, w => w.Contains("flow '", StringComparison.Ordinal) && w.Contains("lineage", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_mapping_costs_the_flow_its_osdu_nodes_with_a_warning_and_nothing_else()
    {
        File.Delete(PathOf("mappings/WellLog@1.4.0.yaml"));

        var lineage = Describe("flows/welldb-welllog-03-header-delivery.yaml");

        var warning = Assert.Single(lineage.Warnings);
        Assert.Equal(
            "delivery flow 'welldb-welllog-03-header-delivery' shows no OSDU type in lineage: mapping 'WellLog@1.4.0' could not be read (Mapping 'WellLog@1.4.0' was not found under '"
            + InRepo("mappings")
            + "'. Expected one of: WellLog@1.4.0.yaml, WellLog@1.4.0.yml, 1.4.0.yaml, 1.4.0.yml.).",
            warning);
        Assert.Empty(lineage.Datasets);
        Assert.Empty(lineage.Derivations);
        Assert.Equal(2, lineage.Objects.Count);
        Assert.Single(lineage.Files);

        var (collected, report) = Scan();
        Assert.Contains(collected.Warnings, w => w == InRepo("flows/welldb-welllog-03-header-delivery.yaml") + ": " + warning);
        Assert.True(WaveOf(report, "welldb-welllog-02-header-ing") < WaveOf(report, "welldb-welllog-03-header-delivery"));
    }

    [Fact]
    public void An_invalid_mapping_is_a_warning_naming_the_file_relative_to_the_checkout()
    {
        File.WriteAllText(PathOf("mappings/WellLog@1.4.0.yaml"), "documentType: mapping\nname: WellLog\nversion: [\n");

        var lineage = Describe("flows/welldb-welllog-03-header-delivery.yaml");

        var warning = Assert.Single(lineage.Warnings);
        Assert.StartsWith(
            "delivery flow 'welldb-welllog-03-header-delivery' shows no OSDU type in lineage: mapping 'WellLog@1.4.0' could not be read ("
            + InRepo("mappings/WellLog@1.4.0.yaml"),
            warning,
            StringComparison.Ordinal);
        Assert.DoesNotContain(_root, warning, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(lineage.Datasets);
    }

    [Fact]
    public void A_mapping_filed_under_another_name_is_a_warning()
    {
        Rewrite("mappings/WellLog@1.4.0.yaml", "version: 1.4.0", "version: 1.4.1");

        var warning = Assert.Single(Describe("flows/welldb-welllog-03-header-delivery.yaml").Warnings);

        Assert.Contains(InRepo("mappings/WellLog@1.4.0.yaml") + ": declares 'WellLog@1.4.1' but is filed as 'WellLog@1.4.0'", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mappings_directory_outside_the_checkout_is_never_read()
    {
        // Three steps up from the flow's folder (<checkout>/wells/flows) is past the checkout itself.
        Rewrite("flows/welldb-welllog-03-header-delivery.yaml", "  mapping: WellLog@1.4.0\n", "  mapping: WellLog@1.4.0\n  mappings: ../../../outside/mappings\n");

        var warning = Assert.Single(Describe("flows/welldb-welllog-03-header-delivery.yaml").Warnings);

        Assert.Equal(
            "delivery flow 'welldb-welllog-03-header-delivery' shows no OSDU type in lineage: render.mappings '../../../outside/mappings' lies outside the repository, so mapping 'WellLog@1.4.0' is not read.",
            warning);
    }

    [Fact]
    public void The_search_for_a_mappings_directory_stops_at_the_checkout()
    {
        // The checkout is a folder of its own inside a folder holding a mappings directory the flow must not reach.
        var parent = Path.Combine(_root, "outer");
        var checkout = Path.Combine(parent, "repo");
        Copy(PathOf("flows"), Path.Combine(checkout, "flows"));
        Copy(PathOf("mappings"), Path.Combine(parent, "mappings"));
        var path = Path.Combine(checkout, "flows", "welldb-welllog-03-header-delivery.yaml");
        var document = Assert.IsAssignableFrom<RegisteredFlowDocument>(Loader().LoadFile(path));

        var lineage = document.DescribeLineage(new RegisteredLineageContext(path, checkout));

        var warning = Assert.Single(lineage.Warnings);
        Assert.Contains("was not found under 'flows/mappings'", warning, StringComparison.Ordinal);
        Assert.Empty(lineage.Datasets);
    }

    [Fact]
    public void A_mapping_reference_naming_a_path_is_refused_before_any_file_is_read()
    {
        Rewrite("flows/welldb-welllog-03-header-delivery.yaml", "mapping: WellLog@1.4.0", "mapping: ../mappings/WellLog@1.4.0");

        var warning = Assert.Single(Describe("flows/welldb-welllog-03-header-delivery.yaml").Warnings);
        Assert.Contains("Mapping reference '../mappings/WellLog@1.4.0' names a path", warning, StringComparison.Ordinal);

        var catalog = new MappingCatalog(PathOf("mappings"), new DeliveryDocumentLoader());
        Assert.Throws<FlowValidationException>(() => catalog.Load("..\\mappings\\WellLog@1.4.0"));
        Assert.Throws<FlowValidationException>(() => catalog.Load("WellLog@../1.4.0"));
        Assert.Equal("WellLog@1.4.0", catalog.Load("WellLog@1.4.0").Reference);
    }

    [Fact]
    public void A_flow_whose_partition_names_no_cache_keeps_its_tables_and_files_and_says_why_it_has_no_osdu_nodes()
    {
        // The flow in the header form, its partition written as text that is no partition id.
        Rewrite("flows/welldb-welllog-03-header-delivery.yaml", "partitions:\n  - name: dev\n    keepLedger: true\n", string.Empty);
        Rewrite("flows/welldb-welllog-03-header-delivery.yaml", "  protocol: ddms\n", "  headers:\n    data-partition-id: \"open des\"\n  protocol: ddms\n");

        var lineage = Describe("flows/welldb-welllog-03-header-delivery.yaml");

        var warning = Assert.Single(lineage.Warnings);
        Assert.StartsWith(
            "delivery flow 'welldb-welllog-03-header-delivery' shows no OSDU node in lineage: target.headers: data-partition-id 'open des' is neither a partition id",
            warning,
            StringComparison.Ordinal);
        Assert.Empty(lineage.Datasets);
        Assert.Equal(2, lineage.Objects.Count);
        Assert.Single(lineage.Files);
    }

    [Fact]
    public void A_partition_reference_stays_its_text_and_a_literal_endpoint_is_identified_by_hash()
    {
        Directory.CreateDirectory(Path.Combine(_root, Source, "cache"));
        File.WriteAllText(PathOf("cache/searched-cache.yaml"), """
            flowType: cache
            name: searched-cache
            source:
              endpoint: https://osdu.example.com
              headers:
                data-partition-id: ${env:OSDU_PARTITION}
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                name: UnitOfMeasure
                fields: [data.Code, data.Name]
            """);

        var lineage = Describe("cache/searched-cache.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.All(lineage.Datasets, d => Assert.Equal("${env:OSDU_PARTITION}", d.Namespace));
        var (collected, report) = Scan();
        Assert.Contains(collected.Facts, f => f.Flow == "searched-cache" && f.ServerRef.StartsWith("dataset:osdu-type:inline:", StringComparison.Ordinal));
        Assert.DoesNotContain(collected.Facts, f => f.ServerRef.Contains("example.com", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Objects, o => o.Key.Contains("example.com", StringComparison.Ordinal));
    }

    [Fact]
    public void An_osdu_type_the_graph_cannot_hold_is_left_out_with_a_warning()
    {
        var warnings = new List<string>();

        Assert.Null(OsduLineage.Type(LineageRelation.Writes, Platform, "dev", "osdu:wks:master-data--Wellbore:*", "flow 'x'", "a write", warnings));
        Assert.Null(OsduLineage.Type(LineageRelation.Reads, Platform, "dev", "not a kind", "flow 'x'", "a read", warnings));
        Assert.Null(OsduLineage.Type(LineageRelation.Reads, new string('e', DeclaredDataset.MaxInstanceLength + 1), "dev", Samples.WellboreKind, "flow 'x'", "a read", warnings));
        Assert.Null(OsduLineage.Type(
            LineageRelation.Reads, Platform, "dev", "osdu:wks:master-data--" + new string('W', DeclaredDataset.MaxPartLength) + ":1.0.0", "flow 'x'", "a read", warnings));
        Assert.Null(OsduLineage.CacheType(LineageRelation.Reads, "dev", "Well|bore", "flow 'x'", warnings));
        Assert.Null(OsduLineage.CacheType(LineageRelation.Reads, "dev", " ", "flow 'x'", warnings));
        Assert.Null(OsduLineage.Mapping(Platform, "dev", "welldb/map|pings", "WellLog@1.4.0", "flow 'x'", warnings));
        Assert.Null(OsduLineage.Mapping(Platform, "dev", "welldb/mappings", "WellLog@*", "flow 'x'", warnings));
        Assert.Null(OsduLineage.Mapping(Platform, "dev", " ", "WellLog@1.4.0", "flow 'x'", warnings));
        Assert.Null(OsduLineage.Mapping(new string('e', DeclaredDataset.MaxInstanceLength + 1), "dev", "welldb/mappings", "WellLog@1.4.0", "flow 'x'", warnings));
        Assert.Null(OsduLineage.Mapping(Platform, "dev", "welldb/mappings", new string('W', DeclaredDataset.MaxPartLength + 1), "flow 'x'", warnings));

        Assert.Equal(11, warnings.Count);
        var mapping = OsduLineage.Mapping($" {Platform} ", "dev", " welldb/mappings ", " WellLog@1.4.0 ", "flow 'x'", warnings)!;
        Assert.Equal((LineageRelation.Reads, OsduLineage.MappingSystem, Platform, "dev", "welldb/mappings", "WellLog@1.4.0"), (mapping.Relation, mapping.System, mapping.Instance, mapping.Namespace, mapping.Group, mapping.Name));
        Assert.Null(mapping.Separator);
        Assert.DoesNotContain(warnings, w => w.Contains(new string('e', 100), StringComparison.Ordinal));
        Assert.Equal("osdu:wks:master-data--Wellbore:1.3.0",
            OsduLineage.Type(LineageRelation.Writes, Platform, "dev", Samples.WellboreKind, "flow 'x'", "a write", warnings)!.Name);
    }

    /// <summary>A source of templates that fails every read with <paramref name="failure"/>, as one that cannot be read does.</summary>
    private sealed class ThrowingTemplates(string failure) : ITemplateSource
    {
        public Task<TemplateResolution> ResolveAsync(string? document, string kind, string? version, CancellationToken ct = default)
            => throw new InvalidOperationException(failure);

        public Task<IReadOnlyList<TemplateInfo>> ListForAsync(string? document, CancellationToken ct = default)
            => throw new InvalidOperationException(failure);
    }

    [Theory]
    [InlineData("osdu:wks:master-data--Wellbore:1.3.0", "master-data")]
    [InlineData("osdu:wks:reference-data--UnitOfMeasure:*", "reference-data")]
    [InlineData("osdu:wks:work-product-component--WellLog:1.4.0", "work-product-component")]
    [InlineData("osdu:wks:Manifest:1.0.0", "Manifest")]
    [InlineData("osdu:wks:*:*", "*")]
    [InlineData("osdu:wks:*-data--Wellbore:*", "*")]
    [InlineData("osdu:wks:master-data--*:*", "master-data")]
    public void A_kind_is_grouped_by_its_entity_types_prefix(string kind, string group)
        => Assert.Equal(group, OsduKind.Group(kind));

    /// <summary>The cache types the well log mapping names: its unit tables, its curve dictionary and the partition's units.</summary>
    private const string NamedByTheWellLogMapping =
        "osdu-cache/dev/cache/CurveDictionary, osdu-cache/dev/cache/DepthUnitAlias, osdu-cache/dev/cache/UnitAlias, osdu-cache/dev/cache/UnitOfMeasure";

    private const string WellboresSearched = "osdu-type/dev/master-data/osdu:wks:master-data--Wellbore:*";

    /// <summary>
    /// The entity types the OSDU order says the well log mapping waits for in the sample estate: every type a property it
    /// fills refers to (the literal technical assurance among them) that a flow of the estate delivers to the partition.
    /// </summary>
    private const string OrderedByTheWellLogMapping =
        "osdu-type/dev/master-data/*:*:master-data--Wellbore:*, "
        + "osdu-type/dev/reference-data/*:*:reference-data--LogCurveBusinessValue:*, osdu-type/dev/reference-data/*:*:reference-data--LogCurveFamily:*, "
        + "osdu-type/dev/reference-data/*:*:reference-data--LogCurveMainFamily:*, osdu-type/dev/reference-data/*:*:reference-data--LogCurveType:*, "
        + "osdu-type/dev/reference-data/*:*:reference-data--LogType:*, osdu-type/dev/reference-data/*:*:reference-data--TechnicalAssuranceType:*, "
        + "osdu-type/dev/reference-data/*:*:reference-data--UnitOfMeasure:*, osdu-type/dev/reference-data/*:*:reference-data--VerticalMeasurementType:*, "
        + "osdu-type/dev/reference-data/*:*:reference-data--WellLogSamplingDomainType:*";

    [Fact]
    public void A_mapping_reads_every_cache_type_of_its_partition_holding_what_its_ids_are_checked_against()
    {
        // Another cache flow of the estate holds the partition's log types under a name of its own. It also holds a type
        // of an entity the mapping builds no id of, a lookup table, and a curve type built for another partition only:
        // none of those is where a render looks an id of the mapping up in this partition.
        File.WriteAllText(PathOf("cache/extra-cache.yaml"), """
            flowType: cache
            name: extra-cache
            partitions: [dev, test]
            source:
              endpoint: ${env:OSDU_URL}
              connection: ${env:OSDU_DATA_DB}
              auth:
                type: none
            types:
              - kind: "osdu:wks:reference-data--LogType:*"
                name: LogKinds
                fields: [data.Code]
              - kind: "osdu:wks:reference-data--AliasNameType:*"
                name: AliasNameType
                fields: [data.Code]
              - kind: "osdu:wks:reference-data--LogCurveType:*"
                name: CurveTypesOfTest
                partitions: [test]
                fields: [data.Code]
              - table: OsduData.silver.LogTypes
                name: LogTypeTable
                key: code
                fields: [name]
            """);

        var lineage = Describe("flows/welldb-welllog-03-header-delivery.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal(
            NamedByTheWellLogMapping
            + ", osdu-cache/dev/cache/LogCurveBusinessValue, osdu-cache/dev/cache/LogCurveFamily, osdu-cache/dev/cache/LogCurveMainFamily"
            + ", osdu-cache/dev/cache/LogCurveType, osdu-cache/dev/cache/LogKinds, osdu-cache/dev/cache/LogType"
            + ", osdu-cache/dev/cache/VerticalMeasurementType, osdu-cache/dev/cache/WellLogSamplingDomainType, " + WellboresSearched
            + ", " + OrderedByTheWellLogMapping,
            Sources(lineage));
    }

    [Fact]
    public void What_a_mapping_is_checked_against_is_read_off_its_modifiers_and_its_template()
    {
        var mapping = new DeliveryDocumentLoader().LoadMapping(PathOf("mappings/WellLog@1.4.0.yaml"));

        // With the template: the entity type every ref takes from the variable it fills, beside the ones the ids write.
        var read = MappingCacheReads.Of(mapping, Samples.SampleTemplate(Samples.WellLogKind));
        Assert.Equal(["CurveDictionary", "DepthUnitAlias", "UnitAlias", "UnitOfMeasure"], read.Types);
        Assert.Equal(
            [
                "reference-data--LogCurveBusinessValue", "reference-data--LogCurveFamily", "reference-data--LogCurveMainFamily",
                "reference-data--LogCurveType", "reference-data--LogType", "reference-data--UnitOfMeasure",
                "reference-data--VerticalMeasurementType", "reference-data--WellLogSamplingDomainType",
            ],
            read.EntityTypes);
        Assert.Empty(read.Untold);

        // Without it: the ids' entity types are text in the mapping; each ref's is the template's to tell, once per variable.
        var alone = MappingCacheReads.Of(mapping, schema: null);
        Assert.Equal(read.Types, alone.Types);
        Assert.Equal(["reference-data--LogCurveFamily", "reference-data--LogCurveMainFamily", "reference-data--LogCurveType"], alone.EntityTypes);
        Assert.Equal(
            [
                "osdu.data.SamplingDomainTypeID", "osdu.data.WellLogTypeID", "osdu.data.VerticalMeasurement.VerticalMeasurementUnitOfMeasureID",
                "osdu.data.VerticalMeasurement.VerticalMeasurementTypeID", "osdu.data.Curves[].CurveUnit", "osdu.data.Curves[].DepthUnit",
                "osdu.data.Curves[].LogCurveBusinessValueID",
            ],
            alone.Untold);

        // A ref that writes its entity type in full needs no template to be known.
        Rewrite("mappings/WellLog@1.4.0.yaml", "        - replace: { INTERPRETED: Interpreted }\n        - ref\n", "        - replace: { INTERPRETED: Interpreted }\n        - ref: reference-data--LogType\n");
        var written = MappingCacheReads.Of(new DeliveryDocumentLoader().LoadMapping(PathOf("mappings/WellLog@1.4.0.yaml")), schema: null);
        Assert.Contains("reference-data--LogType", written.EntityTypes);
        Assert.DoesNotContain("osdu.data.WellLogTypeID", written.Untold);
    }

    [Fact]
    public void Without_its_template_a_mapping_shows_what_the_documents_tell_and_says_what_it_left_out()
    {
        // The template folder on the mapping's path no longer holds the template the mapping names.
        File.Delete(PathOf("templates/osdu_wks_work-product-component--WellLog_1.4.0.json"));
        var lineage = Describe("flows/welldb-welllog-03-header-delivery.yaml");

        // The three curve classes are ids whose entity type the mapping writes; what the refs are checked against is not known.
        Assert.Equal(
            NamedByTheWellLogMapping
            + ", osdu-cache/dev/cache/LogCurveFamily, osdu-cache/dev/cache/LogCurveMainFamily, osdu-cache/dev/cache/LogCurveType, " + WellboresSearched,
            Sources(lineage));
        Assert.Equal(2, lineage.Warnings.Count);
        Assert.StartsWith(
            "delivery flow 'welldb-welllog-03-header-delivery' shows fewer cache types in lineage than its mapping reads: mapping 'WellLog@1.4.0' builds a reference "
            + "with ref at osdu.data.SamplingDomainTypeID, osdu.data.WellLogTypeID, osdu.data.VerticalMeasurement.VerticalMeasurementUnitOfMeasureID, "
            + "osdu.data.VerticalMeasurement.VerticalMeasurementTypeID, osdu.data.Curves[].CurveUnit and 2 more, and the entity type of each is told by its template "
            + "(osdu:wks:work-product-component--WellLog:1.4.0 version 26a3c3441882db4f), so the cache types those references are checked against are left out. "
            + "Its template is not read: no template folder on the path of welldb/mappings/WellLog@1.4.0.yaml holds template "
            + "osdu:wks:work-product-component--WellLog:1.4.0 version 26a3c3441882db4f. It reads welldb/templates. Put the template's file in one of them",
            lineage.Warnings[0],
            StringComparison.Ordinal);
        Assert.Contains("is not ordered by what its records refer to", lineage.Warnings[1], StringComparison.Ordinal);

        // The flow keeps everything else, and the estate still orders it: after its tables, its lookup tables and the wellbores.
        var (collected, report) = Scan(Loader());
        Assert.Contains(collected.Warnings, w => w.StartsWith(InRepo("flows/welldb-welllog-03-header-delivery.yaml") + ": delivery flow", StringComparison.Ordinal));
        Assert.True(WaveOf(report, "welldb-lookups-00-cache") < WaveOf(report, "welldb-welllog-03-header-delivery"));
        Assert.True(WaveOf(report, "wells-wellbore-03-header-delivery") < WaveOf(report, "welldb-welllog-03-header-delivery"));
    }

    [Fact]
    public void A_template_the_folders_hold_in_another_version_or_that_cannot_be_read_is_said_and_never_thrown()
    {
        // The folder holds the kind in another version than the mapping names: the warning says which, and the OSDU order
        // says it cannot tell what the records refer to.
        Rewrite("mappings/WellLog@1.4.0.yaml", "  version: 26a3c3441882db4f\n", "  version: 0123456789abcdef\n");
        var other = Describe("flows/welldb-welllog-03-header-delivery.yaml");
        Assert.Equal(2, other.Warnings.Count);
        Assert.Contains(
            "Its template is not read: no template folder on the path of welldb/mappings/WellLog@1.4.0.yaml holds template "
            + "osdu:wks:work-product-component--WellLog:1.4.0 version 0123456789abcdef. On its path welldb/templates/"
            + "osdu_wks_work-product-component--WellLog_1.4.0.json holds version 26a3c3441882db4f.",
            other.Warnings[0],
            StringComparison.Ordinal);
        Assert.StartsWith(
            "delivery flow 'welldb-welllog-03-header-delivery' is not ordered by what its records refer to: mapping 'WellLog@1.4.0' fills "
            + "osdu:wks:work-product-component--WellLog:1.4.0 version 0123456789abcdef, and without that template the OSDU order cannot tell "
            + "which of the partition's deliveries it waits for. Its template is not read: no template folder",
            other.Warnings[1],
            StringComparison.Ordinal);

        // A source that fails: the reason is said without what it may carry, and the scan goes on ordering the flow.
        Rewrite("mappings/WellLog@1.4.0.yaml", "  version: 0123456789abcdef\n", "  version: 26a3c3441882db4f\n");
        var failing = new ThrowingTemplates("the login for 'osdu' failed; Password=hunter2");
        var (collected, report) = Scan(Loader(new MappingTemplateSource(failing)));
        var told = collected.Warnings.Where(w => w.Contains("Its template is not read: it could not be read (InvalidOperationException: ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(told);
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("hunter2", StringComparison.Ordinal));
        Assert.True(WaveOf(report, "welldb-welllog-02-header-ing") < WaveOf(report, "welldb-welllog-03-header-delivery"));

        // A source that answers is read.
        var mapping = new DeliveryDocumentLoader().LoadMapping(PathOf("mappings/WellLog@1.4.0.yaml"));
        var read = new MappingTemplateSource().Read(mapping, _root);
        Assert.Equal((MappingTemplateOutcome.Found, "26a3c3441882db4f"), (read.Outcome, read.Schema!.Version));
        Assert.Null(read.Missing);
    }

    [Fact]
    public void A_mapping_that_cannot_be_a_node_leaves_what_it_reads_with_the_flow()
    {
        // The mappings folder's name alone makes the node's group longer than a node part may be.
        var folder = new string('m', DeclaredDataset.MaxPartLength - Source.Length);
        Copy(PathOf("mappings"), PathOf(folder));
        Rewrite("flows/welldb-welllog-03-header-delivery.yaml", "  mapping: WellLog@1.4.0\n", $"  mapping: WellLog@1.4.0\n  mappings: ../{folder}\n");

        var lineage = Describe("flows/welldb-welllog-03-header-delivery.yaml");

        Assert.Empty(lineage.Derivations);
        Assert.Equal(
            "delivery flow 'welldb-welllog-03-header-delivery' shows no node for mapping 'WellLog@1.4.0': its partition, group or name is longer than 256 characters, or its identity longer than 900.",
            Assert.Single(lineage.Warnings));
        Assert.Equal("osdu-type/dev/work-product-component/osdu:wks:work-product-component--WellLog:1.4.0", TypeWrites(lineage));
        var reads = Datasets(lineage, LineageRelation.Reads);
        Assert.StartsWith(NamedByTheWellLogMapping, reads, StringComparison.Ordinal);
        Assert.Contains("osdu-cache/dev/cache/LogType", reads, StringComparison.Ordinal);
        Assert.Contains(WellboresSearched, reads, StringComparison.Ordinal);
        Assert.EndsWith(OrderedByTheWellLogMapping, reads, StringComparison.Ordinal);

        // The flow is still ordered after everything its mapping reads.
        var (_, report) = Scan();
        Assert.True(WaveOf(report, "osdu-reference-00-cache") < WaveOf(report, "welldb-welllog-03-header-delivery"));
        Assert.True(WaveOf(report, "wells-wellbore-03-header-delivery") < WaveOf(report, "welldb-welllog-03-header-delivery"));
    }

    [Fact]
    public void A_mapping_rendered_for_two_partitions_is_a_node_in_each_reading_that_partitions_cache()
    {
        Rewrite("flows/welldb-welllog-03-header-delivery.yaml", "partitions:\n  - name: dev\n", "partitions:\n  - name: test\n  - name: dev\n");

        var lineage = Describe("flows/welldb-welllog-03-header-delivery.yaml");

        Assert.Empty(lineage.Warnings);
        Assert.Equal(["test", "dev"], lineage.Derivations.Select(d => d.Dataset.Namespace).ToList());
        var test = lineage.Derivations[0];
        Assert.All(test.From, source => Assert.Equal("test", source.Namespace));

        // No cache flow of the estate fills the test partition's reference data, so there the mapping reads only what it names.
        Assert.Equal(
            ["CurveDictionary", "DepthUnitAlias", "UnitAlias", "UnitOfMeasure"],
            test.From.Where(d => d.System == OsduLineage.CacheSystem).Select(d => d.Name).ToList());
        Assert.Contains(lineage.Derivations[1].From, d => d.Name == "LogType" && d.Namespace == "dev");
    }

    [Fact]
    public void A_mapping_in_the_checkouts_own_mappings_folder_is_grouped_under_the_root()
    {
        // A repository that is one source: its flows and its mappings sit at the top, and the checkout is that folder.
        var checkout = Path.Combine(_root, Source);
        var path = PathOf("flows/wells-wellbore-03-header-delivery.yaml");
        var document = Assert.IsAssignableFrom<RegisteredFlowDocument>(Loader().LoadFile(path));

        var lineage = document.DescribeLineage(new RegisteredLineageContext(path, checkout));

        Assert.Empty(lineage.Warnings);
        var mapping = Assert.Single(lineage.Derivations).Dataset;
        Assert.Equal(("mappings", "Wellbore@1.0.0"), (mapping.Group, mapping.Name));

        // The same flow naming the checkout itself as its mappings folder, with the mapping filed there.
        var top = Path.Combine(checkout, "flows", "at-the-root.yaml");
        var text = File.ReadAllText(path).ReplaceLineEndings("\n");
        Assert.Contains("  mapping: Wellbore@1.0.0\n", text, StringComparison.Ordinal);
        File.WriteAllText(top, text.Replace("  mapping: Wellbore@1.0.0\n", "  mapping: Wellbore@1.0.0\n  mappings: ..\n", StringComparison.Ordinal));
        File.Copy(PathOf("mappings/Wellbore@1.0.0.yaml"), Path.Combine(checkout, "Wellbore@1.0.0.yaml"));
        var rooted = Assert.IsAssignableFrom<RegisteredFlowDocument>(Loader().LoadFile(top)).DescribeLineage(new RegisteredLineageContext(top, checkout));
        Assert.Equal(OsduLineage.RootGroup, Assert.Single(rooted.Derivations).Dataset.Group);
    }

    [Fact]
    public async Task A_graph_stored_before_mappings_were_nodes_is_computed_again_by_the_next_sync_and_then_stands()
    {
        using var database = new OsduTestDatabase();
        var repo = "lineage-upgrade-" + Guid.NewGuid().ToString("N")[..10];
        var repoId = FlowIdentity.FromName(repo);
        var sync = new CatalogSync(Loader(), [new DeliveryCatalogSync(new DeliveryDocumentLoader())]);
        var mappings = ServerIdentity.DatasetPrefix + OsduLineage.MappingSystem + ":";

        async Task<CatalogSyncResult> SyncAsync(DateTime nowUtc)
        {
            await using var db = database.CreateCatalogContext();
            return await sync.SyncAsync(db, _root, repo, null, nowUtc);
        }

        async Task<bool> LacksMappingsAsync()
        {
            await using var db = database.CreateCatalogContext();
            return await DeliveryCatalogSync.StoredGraphLacksMappingsAsync(db, repoId, CancellationToken.None);
        }

        // The edges that are a mapping's: the reads of its node, what the node reads, and what a flow inherits through it.
        async Task<int> MappingEdgesAsync()
        {
            await using var db = database.CreateCatalogContext();
            return await db.LineageEdges.CountAsync(e => e.RepoId == repoId
                && (e.ObjectKey.StartsWith(mappings) || (e.ViaModule != null && e.ViaModule.StartsWith(mappings))));
        }

        var touched = new List<string>();
        try
        {
            // A repository nothing is stored of yet has nothing to lack.
            Assert.False(await LacksMappingsAsync());

            var first = await SyncAsync(Utc);
            Assert.True(first.LineageEdges > 0);
            var drawn = await MappingEdgesAsync();
            Assert.True(drawn > 0);
            Assert.False(await LacksMappingsAsync());

            // Nothing changed, so the stored graph is the graph and no lineage is computed.
            Assert.Equal(0, (await SyncAsync(Utc.AddMinutes(5))).LineageEdges);

            // The graph as a build from before mappings were nodes stored it: the same flows writing the same OSDU types,
            // and nothing of a mapping. No document differs from what the catalog holds.
            await using (var db = database.CreateCatalogContext())
            {
                touched = await db.LineageEdges.Where(e => e.RepoId == repoId).Select(e => e.ObjectKey).Distinct().ToListAsync();
                await db.LineageEdges
                    .Where(e => e.RepoId == repoId && (e.ObjectKey.StartsWith(mappings) || (e.ViaModule != null && e.ViaModule.StartsWith(mappings))))
                    .ExecuteDeleteAsync();
            }

            Assert.Equal(0, await MappingEdgesAsync());
            Assert.True(await LacksMappingsAsync());

            // The next sync, the one a restarted host runs on its own, computes the lineage again and stores the mappings.
            var healed = await SyncAsync(Utc.AddMinutes(10));
            Assert.Equal(first.LineageEdges, healed.LineageEdges);
            Assert.Equal(drawn, await MappingEdgesAsync());
            Assert.False(await LacksMappingsAsync());

            // And then it stands: the question is asked of the stored graph, so once answered it is not asked into a loop.
            Assert.Equal(0, (await SyncAsync(Utc.AddMinutes(15))).LineageEdges);

            // A flow that is not active, and a flow of another kind writing a type, say nothing of a mapping.
            await using (var db = database.CreateCatalogContext())
            {
                await db.LineageEdges
                    .Where(e => e.RepoId == repoId && (e.ObjectKey.StartsWith(mappings) || (e.ViaModule != null && e.ViaModule.StartsWith(mappings))))
                    .ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == repoId && p.Kind == FlowDefinition.FlowTypeName)
                    .ExecuteUpdateAsync(set => set.SetProperty(p => p.Active, false));
            }

            Assert.False(await LacksMappingsAsync());
        }
        finally
        {
            await using var db = database.CreateCatalogContext();
            await RepoStore.DeleteAsync(db, repoId, CancellationToken.None);

            // The nodes this repository's edges named are global rows; the ones no other repository names go with it.
            var kept = await db.LineageEdges.Where(e => touched.Contains(e.ObjectKey)).Select(e => e.ObjectKey).Distinct().ToListAsync();
            var mine = touched.Except(kept, StringComparer.Ordinal).ToList();
            await db.ObjectColumns.Where(c => mine.Contains(c.ObjectKey)).ExecuteDeleteAsync();
            await db.Objects.Where(o => mine.Contains(o.Key)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task A_changed_template_folder_is_a_lineage_input_change_until_it_is_synced_and_a_library_save_is_not()
    {
        using var catalog = new OsduTestDatabase();
        var sync = new DeliveryCatalogSync(new DeliveryDocumentLoader());
        var repoId = Guid.NewGuid();

        async Task ReconcileAsync(DateTime nowUtc)
        {
            await using var db = catalog.CreateDbContext();
            await sync.ReconcileAsync(db, repoId, _root, nowUtc, new List<string>(), CancellationToken.None);
        }

        async Task<bool> ChangedAsync(Guid? repo = null)
        {
            await using var db = catalog.CreateDbContext();
            return await DeliveryCatalogSync.TemplateFoldersChangedAsync(db, repo ?? repoId, _root, CancellationToken.None);
        }

        // Nothing recorded yet: every file of the folder is new. Once synced, nothing has changed.
        Assert.True(await ChangedAsync());
        await ReconcileAsync(Utc);
        Assert.False(await ChangedAsync());

        // A file that now holds another version is a change, until the sync records it.
        var file = PathOf("templates/osdu_wks_work-product-component--WellLog_1.4.0.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"description\":", "\"$comment\": \"changed\", \"description\":", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(file, Utc.AddHours(2));
        Assert.True(await ChangedAsync());
        await ReconcileAsync(Utc.AddHours(2));
        Assert.False(await ChangedAsync());

        // A file that holds no template, and a file removed, are changes too.
        File.WriteAllText(PathOf("templates/notes.json"), "not json");
        Assert.True(await ChangedAsync());
        await ReconcileAsync(Utc.AddHours(3));
        Assert.False(await ChangedAsync());
        File.Delete(PathOf("templates/notes.json"));
        Assert.True(await ChangedAsync());
        await ReconcileAsync(Utc.AddHours(4));
        Assert.False(await ChangedAsync());

        // Another repository has recorded nothing of these folders.
        Assert.True(await ChangedAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_mapping_change_is_a_lineage_input_change_and_a_second_declaration_is_not()
    {
        using var catalog = new OsduTestDatabase();
        var sync = new DeliveryCatalogSync(new DeliveryDocumentLoader());
        var repoId = Guid.NewGuid();

        async Task ReconcileAsync()
        {
            await using var db = catalog.CreateDbContext();
            await sync.ReconcileAsync(db, repoId, _root, Utc, new List<string>(), CancellationToken.None);
        }

        async Task<bool> ChangedAsync()
        {
            await using var db = catalog.CreateDbContext();
            return await sync.MappingsChangedAsync(db, repoId, _root, CancellationToken.None);
        }

        // Nothing reconciled yet: the mappings on disk are new.
        Assert.True(await ChangedAsync());
        await ReconcileAsync();
        Assert.False(await ChangedAsync());

        // A second file declaring a reference already on record is left out by the reconciliation, so it changes nothing.
        Directory.CreateDirectory(PathOf("mappings/copies"));
        File.Copy(PathOf("mappings/WellLog@1.4.0.yaml"), PathOf("mappings/copies/WellLog.yaml"));
        Assert.False(await ChangedAsync());
        Directory.Delete(PathOf("mappings/copies"), recursive: true);

        // An edit, a new mapping and a removed one each are.
        Rewrite("mappings/WellLog@1.4.0.yaml", "Well logs of the well database (one record per log)", "Well logs of the well database, one record per log");
        Assert.True(await ChangedAsync());
        await ReconcileAsync();
        Assert.False(await ChangedAsync());

        File.Copy(PathOf("mappings/Wellbore@1.0.0.yaml"), PathOf("mappings/Wellbore@1.0.1.yaml"));
        Rewrite("mappings/Wellbore@1.0.1.yaml", "version: 1.0.0", "version: 1.0.1");
        Assert.True(await ChangedAsync());
        await ReconcileAsync();
        Assert.False(await ChangedAsync());

        File.Delete(PathOf("mappings/Wellbore@1.0.1.yaml"));
        Assert.True(await ChangedAsync());
        await ReconcileAsync();
        Assert.False(await ChangedAsync());

        // A broken mapping the rows do not hold as it is, is a change; once reconciled as invalid, it is not.
        File.WriteAllText(PathOf("mappings/Broken@1.0.0.yaml"), "documentType: mapping\nname: [\n");
        Assert.True(await ChangedAsync());
        await ReconcileAsync();
        Assert.False(await ChangedAsync());
    }
}
