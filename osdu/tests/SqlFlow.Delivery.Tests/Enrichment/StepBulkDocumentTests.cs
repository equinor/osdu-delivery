using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An enrichment naming the record's bulk data (<c>bulk: true</c>, osdu/docs/reference/flow/enrich-bulk.md), bound to the files
/// its dataset's rows name: the record's DDMS bulk data, the flow's payload set <c>bulk</c> read from the enrichment's one row,
/// on the ddms route alone, with no payload of the record's own and from one enrichment; and what could never send it, refused
/// when a run binds the flow to its mapping.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepBulkDocumentTests
{
    private const string Bulk = "curves:\n  rows: one\n  bulk: true\n";

    private const string CurveFiles = "      files:\n        root: ../data/curves\n        locationColumn: curve_folder\n        pattern: \"*.parquet\"\n        hashColumn: curves_hash\n";

    private static readonly MappingDefinition Note = Fragment("data:\n  Symbol: { $from: note }", name: "WellLogCurveNote");

    private static string Head(string protocol = "ddms", string payloads = "", string options = "    ddmsRoot: /api/os-wellbore-ddms\n", string datasets = "curves")
    {
        var bound = string.Concat(datasets.Split(',', StringSplitOptions.TrimEntries)
            .Select(name => $"    {name}:\n      object: OsduData.silver.WellLog{name}\n      join: {{ source_project: source_project, log_id: log_id }}\n" + CurveFiles));
        return """
            flowType: delivery
            name: demo
            source:
              connection: ${env:OSDU_DATA_DB}
              record:
                object: OsduData.silver.WellLog
                key: [source_project, log_id]
              datasets:

            """ + bound + payloads + $$"""
              work: work
            render:
              mapping: WellLog@1.4.0
              parameters: { dataPartition: dev }
            target:
              endpoint: https://example.org/api
              headers: { data-partition-id: dev }
              protocol: {{protocol}}
              protocolOptions:

            """ + options;
    }

    private static FlowDefinition Parsed(string enrich, string? head = null)
        => Bound(new DeliveryDocumentLoader().ParseFlow(head ?? Head(), "flows/demo.yaml"), Owner(enrich: enrich), Note);

    private static FlowValidationException Refused(string enrich, string? head = null) => Assert.Throws<FlowValidationException>(() => Parsed(enrich, head));

    [Fact]
    public void An_enrichments_bulk_is_the_flows_bulk_payload_read_from_its_row()
    {
        var flow = Parsed(Bulk);

        var payload = Assert.Single(flow.Source.Payloads);
        Assert.Equal(PayloadParts.Bulk, payload.Key);
        Assert.Equal(("curves", "../data/curves", "curve_folder", "*.parquet", "curves_hash"), (payload.Value.Step, payload.Value.Root, payload.Value.LocationColumn, payload.Value.Pattern, payload.Value.HashColumn));
        Assert.Equal(payload.Value, Assert.Single(flow.Enrich!).Bulk);
        Assert.Equal(PayloadParts.Bulk, PayloadParts.Streamed(flow));
    }

    [Fact]
    public void An_enrichment_supplying_bulk_may_add_paths_beside_it()
    {
        var step = Assert.Single(Parsed(Bulk + "  mapping: WellLogCurveNote@1.0.0\n").Enrich!);

        Assert.Equal("WellLogCurveNote@1.0.0", step.Mapping);
        Assert.NotNull(step.Bulk);
    }

    [Fact]
    public void Bulk_on_a_route_that_sends_no_ddms_bulk_data_of_an_enrichments_is_refused()
    {
        var refused = Refused(Bulk, Head(protocol: "storage", options: "    batchSize: 10\n"));

        Assert.Contains("enrich.curves of the mapping Thing@1.0.0 names the record's DDMS bulk data, which the ddms route sends", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bulk_beside_a_payload_the_record_names_itself_is_refused()
    {
        var payloads = "  payloads:\n    curves:\n      root: ../data/curves\n      locationColumn: curve_folder\n      hashColumn: curves_hash\n";
        var refused = Refused(Bulk, Head(payloads: payloads));

        Assert.Contains("source.payloads.curves names its payload already", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bulk_needs_the_files_of_the_enrichments_one_row()
    {
        var noFiles = Head().Replace(CurveFiles, string.Empty, StringComparison.Ordinal);

        Assert.Contains("names the record's bulk data by its row (bulk: true), and the flow binds no files for curves", Refused(Bulk, noFiles).Message, StringComparison.Ordinal);
        Assert.Contains("one supplying bulk data takes one row per record", Assert.Throws<FlowValidationException>(() => Owner(enrich: "curves:\n  rows: many\n  bulk: true\n")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_enrichments_supplying_bulk_are_refused()
    {
        var refused = Refused(Bulk + "more:\n  rows: one\n  bulk: true\n", Head(datasets: "curves, more"));

        Assert.Contains("the enrichments curves, more of the mapping Thing@1.0.0 each name the record's bulk data", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_option_naming_the_bulk_data_an_enrichment_supplies_passes_and_one_naming_another_set_is_refused()
    {
        var named = Parsed(Bulk, Head(options: "    ddmsRoot: /api/os-wellbore-ddms\n    payload: bulk\n"));
        Assert.Equal("curves", named.Source.Payloads[PayloadParts.Bulk].Step);

        var other = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(Head(options: "    ddmsRoot: /api/os-wellbore-ddms\n    payload: curves\n"), "flows/demo.yaml"));
        Assert.Contains("target.protocolOptions.payload 'curves' is not declared under source.payloads", other.Message, StringComparison.Ordinal);

        var none = Assert.Throws<FlowValidationException>(() => Parsed("curves:\n  rows: one\n  mapping: WellLogCurveNote@1.0.0\n", Head(options: "    ddmsRoot: /api/os-wellbore-ddms\n    payload: bulk\n")));
        Assert.Contains("target.protocolOptions.payload names 'bulk', and the flow declares no payload set of that name, nor does any enrichment of the mapping Thing@1.0.0 name the record's bulk data", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bulk_needs_the_column_holding_its_hash_unless_the_flow_compares_the_files_modified_times()
    {
        var noHash = Head().Replace("        hashColumn: curves_hash\n", string.Empty, StringComparison.Ordinal);

        Assert.Contains(
            "enrich.curves of the mapping Thing@1.0.0 names the record's bulk data by its row (bulk: true), and the flow decides payload changes by content hash, so source.datasets.curves.files.hashColumn must name the column holding it",
            Refused(Bulk, noHash).Message,
            StringComparison.Ordinal);

        var byTime = Parsed(Bulk, noHash + "change:\n  payloadDetect: lastModified\n");
        Assert.Null(byTime.Source.Payloads[PayloadParts.Bulk].HashColumn);

        // The loader cannot tell a ddms flow sending no bulk data from one an enrichment supplies, so binding refuses it.
        var noFiles = Head().Replace(CurveFiles, string.Empty, StringComparison.Ordinal) + "change:\n  payloadDetect: lastModified\n";
        Assert.Contains(
            "change.payloadDetect is lastModified, and the flow declares no payload set, nor does any enrichment of the mapping Thing@1.0.0 name the record's bulk data (bulk: true)",
            Refused("curves:\n  rows: one\n  mapping: WellLogCurveNote@1.0.0\n", noFiles).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_files_bulk_data_is_read_from_name_their_root_and_their_folder_column()
    {
        var noRoot = Head().Replace("        root: ../data/curves\n", string.Empty, StringComparison.Ordinal);
        var noColumn = Head().Replace("        locationColumn: curve_folder\n", string.Empty, StringComparison.Ordinal);

        Assert.Contains("source.datasets.curves.files.root", Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(noRoot, "flows/demo.yaml")).Message, StringComparison.Ordinal);
        Assert.Contains("source.datasets.curves.files.locationColumn", Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseFlow(noColumn, "flows/demo.yaml")).Message, StringComparison.Ordinal);
    }
}
