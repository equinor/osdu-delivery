using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A step set aside on its hold, end to end (osdu/docs/reference/flow/enrich-holds.md): the sample well logs delivered through
/// storage with a step adding each log's grade from a table of its own, set aside when it holds. Every run is made as the
/// executor makes it, over the sample estate, the module's database on SQL Server and a fake protocol counting what is sent. A
/// log the step holds in goes ahead with what the step added to its last delivery, nothing for one never delivered, and is
/// listed with the step held, counted and found by the records filter; a hold that changes nothing sends nothing; a change to
/// the log sends it with what was kept; and the step rendering again sends it once and lists it no longer.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepHoldsDeliveryTests : IDisposable
{
    private const string WellLogKind = "osdu:wks:work-product-component--WellLog:1.4.0";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly string _mappings;
    private DateTime? _readThrough;

    public StepHoldsDeliveryTests()
    {
        _mappings = Directory.CreateDirectory(Path.Combine(_root, "mappings")).FullName;
        File.Copy(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"), Path.Combine(_mappings, "WellLog@1.4.0.yaml"));
        File.WriteAllText(Path.Combine(_mappings, "WellLogGrade@1.0.0.yaml"), $$"""
            documentType: mapping
            name: WellLogGrade
            version: 1.0.0
            template:
              kind: {{WellLogKind}}
              version: {{Samples.SampleTemplate(WellLogKind).Version}}
            fragment: true
            record:
              data:
                ExtensionProperties:
                  WellDb:
                    Grade: { $from: grade }
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
            ["grade"] = new FlowSourceDataset
            {
                Object = "OsduData.silver.WellLogGrade",
                Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["source_project"] = "source_project", ["log_id"] = "log_id" },
            },
        };
        return flow with
        {
            Source = flow.Source with { Datasets = datasets, Payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal) },
            Render = flow.Render with { MappingsDirectory = _mappings },
            Target = flow.Target with { Protocol = DeliveryProtocol.Storage, ProtocolOptions = new ProtocolOptions() },
            Enrich = [new EnrichStep { Name = "grade", Mapping = "WellLogGrade@1.0.0", ReadsRows = true, OnHold = EnrichHold.Step }],
        };
    }

    private async Task<(FakeProtocol Sent, OsduLedger Ledger, Guid FlowId)> DeliverAsync(MemoryIngestionTables tables)
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
        await runtime.RunAsync(force: false);
        _readThrough = Now;
        return (protocol, ledger, runtime.Flow.Id);
    }

    /// <summary>Gives a log its grade rows (two of them hold the step, which reads one), stamped as the ingestion tables stamp them.</summary>
    private void Grades(MemoryRecord record, params string[] grades)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        record.Datasets.Remove("grade");
        foreach (var grade in grades)
        {
            record.AddChild("grade", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["source_project"] = record.Row["source_project"],
                ["log_id"] = record.Row["log_id"],
                ["grade"] = grade,
            });
        }

        record.DatasetUpdatedUtc["grade"] = Now;
    }

    private static string? Grade(JsonObject document) => document["data"]?["ExtensionProperties"]?["WellDb"]?["Grade"]?.GetValue<string>();

    private static async Task<(string? Held, long Counted, IReadOnlyList<DeliveryKey> Listed)> HeldAsync(OsduLedger ledger, Guid flowId, int index, DateTime now)
    {
        var record = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(index)))!;
        var stats = await ledger.StatsAsync(flowId, now);
        var listed = await ledger.ListAsync(flowId, new RecordQuery { StepHeld = true });
        return (record.HeldSteps, stats.StepsHeld, listed.Select(r => r.DeliveryKey).ToList());
    }

    [Fact]
    public async Task A_log_the_step_holds_in_goes_ahead_with_what_it_last_added_and_is_listed_until_the_step_renders()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        Assert.True(tables.Records.Count >= 3, "The sample estate holds too few logs for this scenario.");
        Grades(tables.Records[0], "A");
        Grades(tables.Records[1], "B");
        Grades(tables.Records[2], "C1", "C2");

        // 1. Every log goes; the one the step holds in, never delivered, goes without what the step adds, listed with it held.
        var (sent, ledger, flowId) = await DeliverAsync(tables);
        Assert.Equal(tables.Records.Count, sent.Deliveries.Count);
        Assert.Equal("A", Grade(Assert.Single(sent.Deliveries, d => d.Key == SampleEstate.Key(0)).Document));
        Assert.Null(Grade(Assert.Single(sent.Deliveries, d => d.Key == SampleEstate.Key(2)).Document));
        var (held, counted, listed) = await HeldAsync(ledger, flowId, 2, Now);
        Assert.Equal(("grade", 1L), (held, counted));
        Assert.Equal([SampleEstate.Key(2)], listed);
        var first = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!;
        Assert.Null(first.HeldSteps);
        Assert.NotNull(KeptStep.Parse(JsonMerge.ToValues(first.TargetStateJson)[KeptStep.StateKey("grade")]));

        // 2. The step holds in a delivered log: it goes ahead with the grade it last had, which is what OSDU holds, so nothing is sent.
        Grades(tables.Records[0], "A", "A2");
        (sent, ledger, _) = await DeliverAsync(tables);
        Assert.Empty(sent.Deliveries);
        (held, counted, _) = await HeldAsync(ledger, flowId, 0, Now);
        Assert.Equal(("grade", 2L), (held, counted));

        // 3. The log itself changed: it is sent once, with the grade the step last added.
        _clock.Advance(TimeSpan.FromSeconds(1));
        SampleEstate.Change(tables.Records[0], "log_run", "RUN-CHANGED", Now);
        (sent, ledger, _) = await DeliverAsync(tables);
        var changed = Assert.Single(sent.Deliveries);
        Assert.Equal(("RUN-CHANGED", "A"), (changed.Document["data"]!["LogRun"]!.GetValue<string>(), Grade(changed.Document)));
        Assert.Equal("grade", (await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!.HeldSteps);

        // 4. The step renders again with a new grade: the log is sent once with it, and listed no longer.
        Grades(tables.Records[0], "A3");
        (sent, ledger, _) = await DeliverAsync(tables);
        Assert.Equal("A3", Grade(Assert.Single(sent.Deliveries).Document));
        (held, counted, listed) = await HeldAsync(ledger, flowId, 0, Now);
        Assert.Equal((null, 1L), (held, counted));
        Assert.Equal([SampleEstate.Key(2)], listed);

        // 5. The log never delivered with its grade gets it once the step renders for it.
        Grades(tables.Records[2], "C");
        (sent, ledger, _) = await DeliverAsync(tables);
        Assert.Equal("C", Grade(Assert.Single(sent.Deliveries).Document));
        (_, counted, listed) = await HeldAsync(ledger, flowId, 2, Now);
        Assert.Equal(0L, counted);
        Assert.Empty(listed);
    }
}
