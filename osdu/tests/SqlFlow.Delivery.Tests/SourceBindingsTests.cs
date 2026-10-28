using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A flow checked against the ingestion tables it reads before a row is read, and the one payload set its route streams:
/// the set the flow names, or the only one it declares, so a flow declares its payload once and the check still covers it.
/// </summary>
public sealed class SourceBindingsTests
{
    private static readonly string[] HeaderColumns =
        ["source_project", "log_id", "log_source", "update_date", "curve_folder", "payload_hash", "chunk_count"];

    private static SourceHeader Header(params string[] without) => new()
    {
        Selection = SourceSelection.Full(),
        Window = new SourceWindow(null, new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc)),
        Columns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [SourceDatasets.Record] = HeaderColumns.Except(without).ToHashSet(StringComparer.OrdinalIgnoreCase),
            ["curves"] = new HashSet<string>(["source_project", "log_id", "curve_id", "curve_ordinal"], StringComparer.OrdinalIgnoreCase),
        },
        KeyColumns = [new SourceKeyColumn("source_project", "varchar"), new SourceKeyColumn("log_id", "varchar")],
    };

    [Fact]
    public void A_flow_that_declares_its_payload_once_streams_it_and_has_its_columns_checked_before_a_row_is_read()
    {
        var flow = new DeliveryDocumentLoader().LoadFlow(Samples.Flow);
        var mapping = new MappingCatalog(Samples.Mappings, new DeliveryDocumentLoader()).Load(flow.Render.Mapping);

        // The sample flow names its payload under source.payloads alone, and its route streams that one.
        Assert.Null(flow.Target.ProtocolOptions.Payload);
        Assert.Equal(["curves"], flow.Source.Payloads.Keys);
        Assert.Equal("curves", PayloadParts.Streamed(flow));

        SourceBindings.Check(flow, mapping, Header(), "flow.yaml");

        var folder = Assert.Throws<FlowValidationException>(() => SourceBindings.Check(flow, mapping, Header("curve_folder"), "flow.yaml"));
        Assert.Contains("payload 'curves' takes each record's folder from column 'curve_folder', which the record table", folder.Message, StringComparison.Ordinal);
        var hash = Assert.Throws<FlowValidationException>(() => SourceBindings.Check(flow, mapping, Header("payload_hash"), "flow.yaml"));
        Assert.Contains("payload 'curves' takes its content hash from column 'payload_hash'", hash.Message, StringComparison.Ordinal);
        var count = Assert.Throws<FlowValidationException>(() => SourceBindings.Check(flow, mapping, Header("chunk_count"), "flow.yaml"));
        Assert.Contains("payload 'curves' takes its file count from column 'chunk_count'", count.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_payload_a_route_streams_is_the_one_named_or_the_only_one_declared()
    {
        var flow = new DeliveryDocumentLoader().LoadFlow(Samples.Flow);
        var curves = flow.Source.Payloads["curves"];
        Assert.Equal("curves", PayloadParts.Streamed(flow with { Target = flow.Target with { ProtocolOptions = flow.Target.ProtocolOptions with { Payload = "curves" } } }));

        // Two sets and none named leaves nothing to stream; naming one picks it.
        var two = flow with { Source = flow.Source with { Payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal) { ["curves"] = curves, ["grids"] = curves } } };
        Assert.Null(PayloadParts.Streamed(two));
        Assert.Equal("grids", PayloadParts.Streamed(two with { Target = two.Target with { ProtocolOptions = two.Target.ProtocolOptions with { Payload = "grids" } } }));

        // A route that streams no payload streams none, whatever the source declares.
        Assert.Null(PayloadParts.Streamed(flow with { Target = flow.Target with { Protocol = DeliveryProtocol.Storage } }));
    }

    [Fact]
    public void A_payload_a_step_supplies_has_its_columns_checked_against_the_steps_table()
    {
        var sample = new DeliveryDocumentLoader().LoadFlow(Samples.Flow);
        var mapping = new MappingCatalog(Samples.Mappings, new DeliveryDocumentLoader()).Load(sample.Render.Mapping);
        var bulk = new FlowPayload { Root = "../data/grid", LocationColumn = "grid_folder", HashColumn = "grid_hash", Step = "grid" };
        var datasets = new Dictionary<string, FlowSourceDataset>(sample.Source.Datasets, StringComparer.Ordinal)
        {
            ["grid"] = new FlowSourceDataset
            {
                Object = "OsduData.silver.WellLogGrid",
                Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["source_project"] = "source_project", ["log_id"] = "log_id" },
            },
        };
        var flow = sample with
        {
            Source = sample.Source with { Datasets = datasets, Payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal) { [PayloadParts.Bulk] = bulk } },
            Enrich = [new EnrichStep { Name = "grid", ReadsRows = true, Bulk = bulk }],
        };
        SourceHeader WithGrid(params string[] without)
        {
            var header = Header("curve_folder", "payload_hash");
            var columns = new Dictionary<string, IReadOnlySet<string>>(header.Columns, StringComparer.OrdinalIgnoreCase)
            {
                ["grid"] = new[] { "source_project", "log_id", "grid_folder", "grid_hash" }.Except(without).ToHashSet(StringComparer.OrdinalIgnoreCase),
            };
            return header with { Columns = columns };
        }

        // The record table holds none of the payload's columns, and need not: the step's table names its files.
        SourceBindings.Check(flow, mapping, WithGrid(), "flow.yaml");

        var hash = Assert.Throws<FlowValidationException>(() => SourceBindings.Check(flow, mapping, WithGrid("grid_hash"), "flow.yaml"));
        Assert.Contains("payload 'bulk' takes its content hash from column 'grid_hash', which the table of step 'grid', OsduData.silver.WellLogGrid, does not hold", hash.Message, StringComparison.Ordinal);
    }
}
