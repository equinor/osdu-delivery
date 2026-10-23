using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Execution;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What <c>sqlflow validate</c> checks offline of the files a flow names beside it: the mapping a delivery flow's interface
/// pins, the dictionary a cache flow's type holds and the dictionary a dimension's map step cleans through, each found the
/// way a run finds it. Parsing the flow does not look for them, so the estate scan and the catalog sync keep the flow.
/// </summary>
public sealed class OfflineCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("osdu-offline-check-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Delivery(string mapping) => $"""
        flowType: delivery
        name: thing-delivery
        partitions: [dev]
        source:
          connection: ${"{"}env:OSDU_DATA_DB{"}"}
          record: {"{"} object: OsduData.silver.Thing, key: [name] {"}"}
          lastModified: update_date
          work: work/thing
        render:
          mapping: {mapping}
        target:
          endpoint: ${"{"}env:OSDU_URL{"}"}
          protocol: storage
        """.ReplaceLineEndings("\n");

    private const string Interfaces = """
        flowType: delivery
        name: things
        partitions: [dev]
        source:
          connection: ${env:OSDU_DATA_DB}
          work: work/things
        target:
          endpoint: ${env:OSDU_URL}
        interfaces:
          present:
            record: { object: OsduData.silver.Thing, key: [name] }
            lastModified: update_date
            mapping: Thing@1.0.0
          missing:
            record: { object: OsduData.silver.OtherThing, key: [name] }
            lastModified: update_date
            mapping: Thing@2.0.0
        """;

    private const string Cache = """
        flowType: cache
        name: lookups
        source:
          headers: { data-partition-id: dev }
        types:
          - dictionary: UnitAlias
        """;

    private const string Dimension = """
        flowType: dimension
        name: thing-dimensions
        partitions: [dev]
        source:
          endpoint: ${env:OSDU_URL}
        dimensions:
          - name: CurveMnemonic
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.Curves.Mnemonic
            clean: [{ map: CurveAliases }]
        """;

    private static YamlDocumentLoader Documents()
    {
        var loader = new DeliveryDocumentLoader();
        return YamlDocumentLoader.CreateDefault(
            [new DeliveryFlowKind(loader), new CacheFlowKind(loader), new DimensionFlowKind(loader)],
            [new DeliveryFlowKind(loader), new DictionaryDocumentKind(loader)]);
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    private static string Refused(FlowDocument document, string path)
        => Assert.Throws<FlowValidationException>(() => DocumentLoader.CheckOffline(document, path)).Message;

    [Fact]
    public void A_delivery_flow_pinning_a_mapping_that_is_not_there_parses_and_is_refused_offline()
    {
        var flow = Write("flows/thing.yaml", Delivery("Thing@1.0.0"));

        // What the estate scan, the catalog sync and a node read: the flow as it stands.
        var document = Assert.IsType<DeliveryFlowDocument>(Documents().LoadFile(flow));

        var message = Refused(document, flow);
        Assert.StartsWith($"{flow}: render.mapping: Mapping 'Thing@1.0.0' was not found under 'mappings'.", message, StringComparison.Ordinal);
        Assert.Contains("Thing@1.0.0.yaml", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_delivery_flow_whose_mapping_is_in_the_nearest_mappings_folder_above_it_passes()
    {
        var flow = Write("estate/flows/thing/thing.yaml", Delivery("Thing@1.0.0"));
        Write("estate/mappings/Thing@1.0.0.yaml", TestSchema.MappingYaml);

        DocumentLoader.CheckOffline(Documents().LoadFile(flow), flow);
    }

    [Fact]
    public void A_mapping_filed_under_another_version_than_it_declares_is_refused_as_a_run_refuses_it()
    {
        var flow = Write("flows/thing.yaml", Delivery("Thing@1.2.0"));
        Write("mappings/Thing@1.2.0.yaml", TestSchema.MappingYaml);

        var message = Refused(Documents().LoadFile(flow), flow);

        Assert.Contains("render.mapping: ../mappings/Thing@1.2.0.yaml: declares 'Thing@1.0.0' but is filed as 'Thing@1.2.0'", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_interface_is_checked_and_named_by_its_own_key()
    {
        var flow = Write("flows/things.yaml", Interfaces);
        Write("mappings/Thing@1.0.0.yaml", TestSchema.MappingYaml);

        var message = Refused(Documents().LoadFile(flow), flow);

        Assert.Contains("interfaces.missing.mapping: Mapping 'Thing@2.0.0' was not found under '../mappings'.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("interfaces.present.mapping", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cache_flow_holding_a_dictionary_that_is_not_there_is_refused_offline_and_passes_once_it_is()
    {
        var flow = Write("estate/cache/lookups.yaml", Cache);
        var document = Assert.IsType<CacheFlowDocument>(Documents().LoadFile(flow));

        var message = Refused(document, flow);
        Assert.StartsWith($"{flow}: type 'UnitAlias': Dictionary 'UnitAlias' was not found: there is no dictionaries/ directory", message, StringComparison.Ordinal);

        Write("estate/dictionaries/UnitAlias.yaml", "documentType: dictionary\nname: UnitAlias\nentries:\n  M: m\n");
        DocumentLoader.CheckOffline(document, flow);
    }

    [Fact]
    public void A_dimension_cleaning_through_a_dictionary_that_is_not_there_is_refused_offline_and_passes_once_it_is()
    {
        var flow = Write("flows/dimensions.yaml", Dimension);
        Write("dictionaries/Other.yaml", "documentType: dictionary\nname: Other\nentries:\n  A: a\n");
        var document = Assert.IsType<DimensionFlowDocument>(Documents().LoadFile(flow));

        var message = Refused(document, flow);
        Assert.StartsWith($"{flow}: dimension 'CurveMnemonic': Dictionary 'CurveAliases' was not found under '../dictionaries'.", message, StringComparison.Ordinal);

        Write("dictionaries/CurveAliases.yaml", "documentType: dictionary\nname: CurveAliases\nentries:\n  GR: GR\n");
        DocumentLoader.CheckOffline(document, flow);
    }

    [Fact]
    public void A_flow_that_names_no_companion_file_passes()
    {
        var flow = Write("cache/reference.yaml", """
            flowType: cache
            name: reference
            source:
              endpoint: ${env:OSDU_URL}
              headers: { data-partition-id: dev }
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                name: UnitOfMeasure
                fields: [data.Code]
            """);

        DocumentLoader.CheckOffline(Documents().LoadFile(flow), flow);
    }

    [Fact]
    public void An_unknown_document_type_describes_a_mapping_as_a_mapping()
    {
        var refused = Assert.Throws<FlowValidationException>(() => Documents().ParseCompanion("documentType: nope\nname: x\n", "docs/x.yaml"));

        Assert.Contains("'mapping' for how a delivery flow renders the rows of an ingestion table into records of one OSDU kind", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'dictionary' for a lookup table a cache flow holds", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'mapping' for deliver records", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_without_a_flow_type_or_document_type_is_told_every_flow_type_the_module_reads()
    {
        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().Probe("name: x\n", "flows/x.yaml"));

        Assert.Equal(
            "flows/x.yaml: the document declares no 'flowType' (delivery, retrieval, cache, assertion, dimension, inventory, removal) and no 'documentType' (mapping, dictionary).",
            refused.Message);
        Assert.Equal(["delivery", "retrieval", "cache", "assertion", "dimension", "inventory", "removal"], DeliveryDocumentLoader.FlowTypes);
    }
}
