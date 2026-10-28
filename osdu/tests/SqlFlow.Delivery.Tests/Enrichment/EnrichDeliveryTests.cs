using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A delivery flow with two <c>enrich:</c> steps, end to end (osdu/docs/reference/flow/enrich.md, One change, one write): the
/// sample well logs delivered through storage, a step adding each log's LAS file name from a table of its own, and a step
/// after it copying that name into a note of the record. Every run is made as the executor makes it, over the sample estate,
/// the module's database on SQL Server and a fake protocol counting what is sent. A change to the log's row, to a step's row,
/// or a step's row arriving or leaving, sends the record once with the whole chain in it; a change that renders the same
/// record sends nothing; and a run with nothing changed sends nothing.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class EnrichDeliveryTests : IDisposable
{
    private const string WellLogKind = "osdu:wks:work-product-component--WellLog:1.4.0";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly string _mappings;
    private DateTime? _readThrough;

    public EnrichDeliveryTests()
    {
        _mappings = Directory.CreateDirectory(Path.Combine(_root, "mappings")).FullName;
        File.Copy(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"), Path.Combine(_mappings, "WellLog@1.4.0.yaml"));
        var version = Samples.SampleTemplate(WellLogKind).Version;
        File.WriteAllText(Path.Combine(_mappings, "WellLogFile@1.0.0.yaml"), $$"""
            documentType: mapping
            name: WellLogFile
            version: 1.0.0
            template:
              kind: {{WellLogKind}}
              version: {{version}}
            fragment: true
            record:
              tags:
                LasFile: { $from: las_file }
              data:
                ExtensionProperties:
                  WellDb:
                    LasFile: { $from: las_file }
            """);
        File.WriteAllText(Path.Combine(_mappings, "WellLogNote@1.0.0.yaml"), $$"""
            documentType: mapping
            name: WellLogNote
            version: 1.0.0
            template:
              kind: {{WellLogKind}}
              version: {{version}}
            fragment: true
            record:
              data:
                ExtensionProperties:
                  WellDb:
                    Note: { $base: data.ExtensionProperties.WellDb.LasFile, $modifiers: [upper], $required: false }
            """);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        _db.Dispose();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private FlowDefinition Flow()
    {
        var flow = Samples.LocalFlow(_root);
        var datasets = new Dictionary<string, FlowSourceDataset>(flow.Source.Datasets, StringComparer.Ordinal)
        {
            ["lasfile"] = new FlowSourceDataset
            {
                Object = "OsduData.silver.WellLogFile",
                Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["source_project"] = "source_project", ["log_id"] = "log_id" },
            },
        };
        return flow with
        {
            Source = flow.Source with
            {
                Datasets = datasets,
                Payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal),
            },
            Render = flow.Render with { MappingsDirectory = _mappings },
            Target = flow.Target with { Protocol = DeliveryProtocol.Storage, ProtocolOptions = new ProtocolOptions() },
            Enrich =
            [
                new EnrichStep { Name = "lasfile", Mapping = "WellLogFile@1.0.0", ReadsRows = true },
                new EnrichStep { Name = "note", Mapping = "WellLogNote@1.0.0", After = ["lasfile"] },
            ],
        };
    }

    /// <summary>One deliver run of the flow as the executor makes it, with a fresh protocol counting what it sends.</summary>
    private async Task<(FakeProtocol Sent, OsduLedger Ledger, Guid FlowId, RunResult Run)> DeliverAsync(MemoryIngestionTables tables)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var templates = new MemoryTemplates(Samples.TemplateKinds().Select(Samples.SampleTemplate));
        var engine = Samples.Engine(ledger, _clock, templates: templates, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        var runtime = await FlowRuntime.CreateAsync(engine, Flow(), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);

        var selection = SourceSelection.Incremental(_readThrough);
        runtime.Selection = selection;
        runtime.RunId = Guid.CreateVersion7();
        await DeliveryExecutor.DeliverRequestedAsync(runtime, selection, DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
        runtime.Selection = selection;
        var run = await runtime.RunAsync(force: false);
        _readThrough = Now;
        return (protocol, ledger, runtime.Flow.Id, run);
    }

    /// <summary>Gives a log its LAS file row, changes it or takes it away (null), stamped now as the ingestion tables stamp it.</summary>
    private void LasFile(MemoryRecord record, string? lasFile)
    {
        // A change lands after the last run read through, as one made between two runs does.
        _clock.Advance(TimeSpan.FromSeconds(1));
        record.Datasets.Remove("lasfile");
        if (lasFile is not null)
        {
            record.AddChild("lasfile", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["source_project"] = record.Row["source_project"],
                ["log_id"] = record.Row["log_id"],
                ["las_file"] = lasFile,
            });
        }

        record.DatasetUpdatedUtc["lasfile"] = Now;
    }

    private static JsonObject Sent(FakeProtocol sent, int index) => Assert.Single(sent.Deliveries, d => d.Key == SampleEstate.Key(index)).Document;

    private static string? WellDb(JsonObject document, string property)
        => document["data"]?["ExtensionProperties"]?["WellDb"]?[property]?.GetValue<string>();

    [Fact]
    public async Task One_change_anywhere_in_the_chain_sends_the_record_once_with_every_step_and_no_change_sends_nothing()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        Assert.True(tables.Records.Count >= 3, "The sample estate holds too few logs for this scenario.");
        LasFile(tables.Records[0], "a.las");
        LasFile(tables.Records[1], "b.las");

        // 1. The first run sends every log once, each with what its steps add, and a log without a LAS file without them.
        var (sent, ledger, flowId, run) = await DeliverAsync(tables);
        Assert.Equal(tables.Records.Count, run.Work.Delivered);
        Assert.Equal(tables.Records.Count, sent.Deliveries.Count);
        var first = Sent(sent, 0);
        Assert.Equal(("a.las", "A.LAS", "a.las"), (WellDb(first, "LasFile"), WellDb(first, "Note"), first["tags"]!["LasFile"]!.GetValue<string>()));
        Assert.Null(Sent(sent, 2)["data"]!["ExtensionProperties"]);
        var record = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!;
        Assert.Contains("\"enrich\":[", record.RenderContext, StringComparison.Ordinal);

        // 2. Nothing changed: nothing is sent.
        (sent, _, _, _) = await DeliverAsync(tables);
        Assert.Empty(sent.Deliveries);

        // 3. Only the first step's row of one log changed: that log is sent once, and the step after it follows in the same write.
        LasFile(tables.Records[0], "a2.las");
        (sent, _, _, _) = await DeliverAsync(tables);
        var changed = Assert.Single(sent.Deliveries);
        Assert.Equal((SampleEstate.Key(0), "a2.las", "A2.LAS"), (changed.Key, WellDb(changed.Document, "LasFile"), WellDb(changed.Document, "Note")));

        // 4. A step's row written again with the same values renders the same record: nothing is sent.
        LasFile(tables.Records[1], "b.las");
        (sent, _, _, _) = await DeliverAsync(tables);
        Assert.Empty(sent.Deliveries);

        // 5. A step's row arriving for a log that had none sends that log once.
        LasFile(tables.Records[2], "c.las");
        (sent, _, _, _) = await DeliverAsync(tables);
        Assert.Equal("C.LAS", WellDb(Sent(sent, 2), "Note"));
        Assert.Single(sent.Deliveries);

        // 6. The log's own row changed: the record is sent once, its steps still in it.
        _clock.Advance(TimeSpan.FromSeconds(1));
        SampleEstate.Change(tables.Records[0], "log_run", "RUN-CHANGED", Now);
        (sent, _, _, _) = await DeliverAsync(tables);
        var owner = Assert.Single(sent.Deliveries);
        Assert.Equal(("RUN-CHANGED", "a2.las"), (owner.Document["data"]!["LogRun"]!.GetValue<string>(), WellDb(owner.Document, "LasFile")));

        // 7. A step's row gone: the record is sent once, without what the step added.
        LasFile(tables.Records[2], null);
        (sent, _, _, _) = await DeliverAsync(tables);
        var removed = Assert.Single(sent.Deliveries);
        Assert.Equal(SampleEstate.Key(2), removed.Key);
        Assert.Null(removed.Document["data"]!["ExtensionProperties"]);
        Assert.Null(removed.Document["tags"]!["LasFile"]);
    }
}
