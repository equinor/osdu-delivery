using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The <c>bulk:</c> of a step of <c>enrich:</c> as the loader reads it (osdu/docs/reference/flow/enrich-bulk.md): the record's
/// DDMS bulk data, the flow's payload set <c>bulk</c> read from the step's one row, on the ddms route alone, with no payload of
/// the record's own and from one step; and what could never send it, refused when the document loads.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepBulkDocumentTests
{
    private static string Head(string protocol = "ddms", string payloads = "", string options = "    ddmsRoot: /api/os-wellbore-ddms\n") => $$"""
        flowType: delivery
        name: demo
        source:
          connection: ${env:OSDU_DATA_DB}
          record:
            object: OsduData.silver.WellLog
            key: [source_project, log_id]
        {{payloads}}  work: work
        render:
          mapping: WellLog@1.4.0
          parameters: { dataPartition: dev }
        target:
          endpoint: https://example.org/api
          headers: { data-partition-id: dev }
          protocol: {{protocol}}
          protocolOptions:
        {{options}}
        """;

    private const string Curves = "  curves:\n    dataset:\n      object: OsduData.silver.WellLogCurves\n      join: { source_project: source_project, log_id: log_id }\n";

    private const string Bulk = "    bulk:\n      root: ../data/curves\n      locationColumn: curve_folder\n      pattern: \"*.parquet\"\n      hashColumn: curves_hash\n";

    private static FlowDefinition Parsed(string enrich, string? head = null) => new DeliveryDocumentLoader().ParseFlow((head ?? Head()) + "enrich:\n" + enrich, "flows/demo.yaml");

    private static FlowValidationException Refused(string enrich, string? head = null) => Assert.Throws<FlowValidationException>(() => Parsed(enrich, head));

    [Fact]
    public void A_steps_bulk_is_the_flows_bulk_payload_read_from_the_steps_row()
    {
        var flow = Parsed(Curves + Bulk);

        var payload = Assert.Single(flow.Source.Payloads);
        Assert.Equal(PayloadParts.Bulk, payload.Key);
        Assert.Equal(("curves", "../data/curves", "curve_folder", "*.parquet", "curves_hash"), (payload.Value.Step, payload.Value.Root, payload.Value.LocationColumn, payload.Value.Pattern, payload.Value.HashColumn));
        Assert.Equal(payload.Value, Assert.Single(flow.Enrich!).Bulk);
        Assert.Equal(PayloadParts.Bulk, PayloadParts.Streamed(flow));
    }

    [Fact]
    public void A_step_supplying_bulk_may_render_a_fragment_beside_it()
    {
        var step = Assert.Single(Parsed(Curves + Bulk + "    mapping: WellLogCurveNote@1.0.0\n").Enrich!);

        Assert.Equal("WellLogCurveNote@1.0.0", step.Mapping);
        Assert.NotNull(step.Bulk);
    }

    [Fact]
    public void Bulk_on_a_route_that_sends_no_ddms_bulk_data_of_a_steps_is_refused()
    {
        var refused = Refused(Curves + Bulk, Head(protocol: "storage", options: "    batchSize: 10\n"));

        Assert.Contains("enrich.curves.bulk is the record's DDMS bulk data, which the ddms route sends", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bulk_beside_a_payload_the_record_names_itself_is_refused()
    {
        var payloads = "  payloads:\n    curves:\n      root: ../data/curves\n      locationColumn: curve_folder\n      hashColumn: curves_hash\n";
        var refused = Refused(Curves + Bulk, Head(payloads: payloads));

        Assert.Contains("source.payloads.curves names its payload already", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bulk_needs_the_steps_rows_and_one_of_them()
    {
        Assert.Contains("the step declares no dataset", Refused("  curves:\n" + Bulk).Message, StringComparison.Ordinal);
        Assert.Contains("a step supplying bulk data takes one row per record", Refused(Curves + "    rows: many\n" + Bulk + "    mapping: X@1.0.0\n").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_steps_supplying_bulk_are_refused()
    {
        var second = Curves.Replace("curves", "more", StringComparison.Ordinal).Replace("OsduData.silver.WellLogMore", "OsduData.silver.WellLogMore", StringComparison.Ordinal);
        var refused = Refused(Curves + Bulk + second + Bulk);

        Assert.Contains("steps curves, more each supply bulk data", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_option_naming_another_set_is_refused()
    {
        var refused = Refused(Curves + Bulk, Head(options: "    ddmsRoot: /api/os-wellbore-ddms\n    payload: curves\n"));

        Assert.Contains("target.protocolOptions.payload names 'curves'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bulk_names_its_root_and_its_folder_column()
    {
        Assert.Contains("enrich.curves.bulk.root", Refused(Curves + "    bulk:\n      locationColumn: curve_folder\n      hashColumn: h\n").Message, StringComparison.Ordinal);
        Assert.Contains("enrich.curves.bulk.locationColumn", Refused(Curves + "    bulk:\n      root: r\n      hashColumn: h\n").Message, StringComparison.Ordinal);
    }
}
