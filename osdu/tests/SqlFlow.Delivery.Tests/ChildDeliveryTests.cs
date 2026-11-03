using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A canonical log and its custom log linked both ways, end to end (osdu/docs/reference/flow/mapping-child.md): the sample
/// estate's WellLog mapping links each log's custom record (<c>$child</c>, in <c>ExtensionProperties</c>, as a canonical
/// template carries it), and the custom mapping reads its parent and names it. The canonical flow delivers first and links
/// nothing, since no custom record is there yet; the custom records land and ask the canonical ones to render again; the
/// canonical flow's next run sends each with the link, its document only, its bulk data unchanged; and the custom records,
/// asked again by their parents' landing, render the same and send nothing. A custom record taken out of OSDU through the
/// ledger takes the link away on the canonical flow's next run, and gives it back once delivered again; one taken out by
/// another system is seen only by a render, which the preview says. Over the sample estate, the module's database on SQL
/// Server, a fake protocol, and a platform holding what each flow landed.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class ChildDeliveryTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly Dictionary<string, DateTime> _readThrough = new(StringComparer.Ordinal);

    /// <summary>The WellLog records the platform holds, by OSDU id: the parents the custom records read.</summary>
    private readonly Dictionary<string, JsonObject> _parents = new(StringComparer.Ordinal);

    /// <summary>The custom records the platform holds, by OSDU id: the children the WellLog records link.</summary>
    private readonly Dictionary<string, JsonObject> _children = new(StringComparer.Ordinal);

    private readonly SchemaSnapshot _custom = ParentDeliveryTests.CustomTemplate();

    public ChildDeliveryTests()
    {
        var mappings = Directory.CreateDirectory(Path.Combine(_root, "mappings")).FullName;
        File.WriteAllText(Path.Combine(mappings, "CustomLog@1.0.0.yaml"), ParentDeliveryTests.CustomMapping(_custom.Version));
        var canonical = File.ReadAllText(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml")).ReplaceLineEndings("\n");
        File.WriteAllText(Path.Combine(mappings, "WellLog@1.4.0.yaml"), ParentDeliveryTests.Replace(canonical, "\n  data:\n", $$"""

              data:
                ExtensionProperties:
                  WellDb:
                    CustomLogID: { $child: {{ParentDeliveryTests.CustomKind}} }

            """));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        _db.Dispose();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static string ParentId(int index) => TargetId.Compose(Samples.SamplePartition, "work-product-component--WellLog", SampleEstate.Key(index));

    private static string CustomId(int index) => TargetId.Compose(Samples.SamplePartition, "work-product-component--CustomLog", SampleEstate.Key(index));

    /// <summary>What the platform holds once a WellLog record landed: the DDMS references its custom record reads.</summary>
    private void HoldsParent(int index, long version)
    {
        _parents[ParentId(index)] = new JsonObject
        {
            ["id"] = ParentId(index),
            ["kind"] = ParentDeliveryTests.WellLogKind,
            ["version"] = version,
            ["data"] = new JsonObject
            {
                ["DDMSDatasets"] = new JsonArray($"urn:wdms-1:uuid:bulk-{index}"),
                ["ExtensionProperties"] = new JsonObject { ["wdms"] = new JsonObject { ["bulkURI"] = $"urn:wdms-1:uuid:bulk-{index}" } },
            },
        };
    }

    /// <summary>What the platform holds once a custom record landed.</summary>
    private void HoldsChild(int index) => _children[CustomId(index)] = new JsonObject
    {
        ["id"] = CustomId(index),
        ["kind"] = ParentDeliveryTests.CustomKind,
        ["version"] = 1_700_000_000_000_500 + index,
    };

    /// <summary>
    /// The sample WellLog flow over the mapping that links its children. It carries over from OSDU only the DDMS's dataset
    /// lists, not <c>ExtensionProperties</c>, which the wellbore DDMS route carries its own key in by itself: a flow carrying
    /// the whole object over would put back what OSDU holds in place of the link, which the route check refuses.
    /// </summary>
    private Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> CanonicalAsync(MemoryIngestionTables tables)
    {
        var flow = Samples.LocalFlow(_root);
        return RuntimeAsync(tables, flow with
        {
            Render = flow.Render with { MappingsDirectory = Path.Combine(_root, "mappings") },
            Target = flow.Target with { ProtocolOptions = flow.Target.ProtocolOptions with { PreserveDataKeys = ["Datasets", "DDMSDatasets"] } },
        });
    }

    private Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> CustomAsync(MemoryIngestionTables tables)
    {
        var flow = Samples.LocalFlow(_root);
        return RuntimeAsync(tables, flow with
        {
            Name = ParentDeliveryTests.CustomFlowName,
            Source = flow.Source with
            {
                Payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal),
                Work = Path.Combine(_root, "custom-work"),
            },
            Render = flow.Render with { Mapping = "CustomLog@1.0.0", MappingsDirectory = Path.Combine(_root, "mappings") },
            Target = flow.Target with { Protocol = DeliveryProtocol.Storage, ProtocolOptions = new ProtocolOptions() },
        });
    }

    /// <summary>A runtime of <paramref name="flow"/> over the platform as it holds the records now: each run reads them afresh.</summary>
    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> RuntimeAsync(MemoryIngestionTables tables, FlowDefinition flow)
    {
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var templates = new MemoryTemplates(Samples.TemplateKinds().Select(Samples.SampleTemplate)).Add(_custom);
        var searches = FixedRecordSearchFactory.SampleWellbores(
            new Dictionary<string, JsonObject>(_parents, StringComparer.Ordinal), new Dictionary<string, JsonObject>(_children, StringComparer.Ordinal));
        var engine = Samples.Engine(ledger, _clock, templates: templates, sources: tables, searches: searches) with { Protocols = new FakeProtocolFactory(protocol) };
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        return (runtime, protocol, ledger);
    }

    /// <summary>One deliver run as the executor makes it: the records asked to be planned again, then the rows stamped since the flow's last run.</summary>
    private async Task DeliverAsync(FlowRuntime runtime)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var selection = SourceSelection.Incremental(_readThrough.TryGetValue(runtime.Flow.Name, out var through) ? through : null);
        runtime.Selection = selection;
        runtime.RunId = Guid.CreateVersion7();
        await DeliveryExecutor.DeliverRequestedAsync(runtime, selection, DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
        runtime.Selection = selection;
        await runtime.RunAsync(force: false);
        _readThrough[runtime.Flow.Name] = Now;
    }

    private static JsonNode? Link(DeliveryWork work) => work.Document["data"]?["ExtensionProperties"]?["WellDb"]?["CustomLogID"];

    [Fact]
    public async Task A_canonical_log_is_delivered_without_its_custom_log_and_linked_to_it_on_the_run_after_it_lands()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var logs = SampleEstate.Logs().Count;
        Assert.True(logs > 1, "The sample estate holds too few logs for this scenario.");

        // 1. The canonical flow delivers every log first, curve values and all. No custom record is there yet, so each goes
        //    out without the link, never held for it, and the ledger keeps what its children's ids will share.
        var (canonical, canonicalSent, ledger) = await CanonicalAsync(tables);
        await DeliverAsync(canonical);
        Assert.Equal(logs, canonicalSent.Deliveries.Count);
        Assert.All(canonicalSent.Deliveries, work => Assert.Null(Link(work)));
        for (var i = 0; i < logs; i++)
        {
            var record = (await ledger.GetRecordAsync(canonical.Flow.Id, SampleEstate.Key(i)))!;
            Assert.Equal((RecordStatus.Delivered, TargetId.Unique(CustomId(i))), (record.Status, record.ChildLinkKey));
            HoldsParent(i, 1_700_000_000_000_000 + i);
        }

        // 2. The custom flow delivers every log, each reading its parent. Each landing asks the WellLog record linking it to
        //    render again; nothing about it is sent yet.
        var (custom, customSent, _) = await CustomAsync(tables);
        await DeliverAsync(custom);
        Assert.Equal(logs, customSent.Deliveries.Count);
        for (var i = 0; i < logs; i++)
        {
            Assert.NotNull((await ledger.GetRecordAsync(canonical.Flow.Id, SampleEstate.Key(i)))!.PlanRequestedUtc);
            HoldsChild(i);
        }

        // 3. The canonical flow's next run, its rows unchanged: every record asked for renders with the link, which changes
        //    its document, so it is sent, the document alone: its curve values are what OSDU holds and are not sent again.
        (canonical, canonicalSent, _) = await CanonicalAsync(tables);
        await DeliverAsync(canonical);
        Assert.Equal(logs, canonicalSent.Deliveries.Count);
        for (var i = 0; i < logs; i++)
        {
            var work = Assert.Single(canonicalSent.Deliveries, d => d.TargetId == ParentId(i));
            Assert.Equal(CustomId(i) + ":", Link(work)!.GetValue<string>());
            Assert.Equal((true, false), (work.DeliverMetadata, work.DeliverPayload));
            var record = (await ledger.GetRecordAsync(canonical.Flow.Id, SampleEstate.Key(i)))!;
            Assert.Equal((RecordStatus.Delivered, (DateTime?)null), (record.Status, record.PlanRequestedUtc));

            // The parent landing again is what the custom record reads; its sections did not change.
            HoldsParent(i, 1_700_000_000_000_100 + i);
        }

        // 4. The custom records, asked again by their parents' landing, render what OSDU holds and send nothing; and the two
        //    flows have settled: neither run sends anything more.
        (custom, customSent, _) = await CustomAsync(tables);
        await DeliverAsync(custom);
        Assert.Empty(customSent.Deliveries);
        (canonical, canonicalSent, _) = await CanonicalAsync(tables);
        await DeliverAsync(canonical);
        Assert.Empty(canonicalSent.Deliveries);
        for (var i = 0; i < logs; i++)
        {
            Assert.Null((await ledger.GetRecordAsync(canonical.Flow.Id, SampleEstate.Key(i)))!.PlanRequestedUtc);
            Assert.Null((await ledger.GetRecordAsync(custom.Flow.Id, SampleEstate.Key(i)))!.PlanRequestedUtc);
        }
    }

    /// <summary>
    /// The state the end-to-end test settles in: every log delivered by both flows, each canonical log linking its custom log,
    /// and neither flow sending anything more.
    /// </summary>
    private async Task<MemoryIngestionTables> LinkedAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var logs = SampleEstate.Logs().Count;
        var (canonical, _, _) = await CanonicalAsync(tables);
        await DeliverAsync(canonical);
        for (var i = 0; i < logs; i++)
        {
            HoldsParent(i, 1_700_000_000_000_000 + i);
        }

        var (custom, _, _) = await CustomAsync(tables);
        await DeliverAsync(custom);
        for (var i = 0; i < logs; i++)
        {
            HoldsChild(i);
        }

        (canonical, var sent, _) = await CanonicalAsync(tables);
        await DeliverAsync(canonical);
        Assert.Equal(logs, sent.Deliveries.Count);
        for (var i = 0; i < logs; i++)
        {
            HoldsParent(i, 1_700_000_000_000_100 + i);
        }

        (custom, sent, _) = await CustomAsync(tables);
        await DeliverAsync(custom);
        Assert.Empty(sent.Deliveries);
        return tables;
    }

    [Fact]
    public async Task A_custom_log_taken_out_of_osdu_takes_the_link_away_and_gives_it_back_once_delivered_again()
    {
        var tables = await LinkedAsync();

        // An operator removes the first log's custom record from OSDU.
        var (custom, customSent, ledger) = await CustomAsync(tables);
        _children.Remove(CustomId(0));
        await ledger.MarkRemovedAsync(custom.Flow.Id, [SampleEstate.Key(0)], RemovalScope.Record, "user:operator", Now);

        // The canonical flow's next run sends that log alone, without the link: its document only, its curve values unchanged.
        var (canonical, canonicalSent, _) = await CanonicalAsync(tables);
        await DeliverAsync(canonical);
        var unlinked = Assert.Single(canonicalSent.Deliveries);
        Assert.Equal(ParentId(0), unlinked.TargetId);
        Assert.Null(Link(unlinked));
        Assert.Equal((true, false), (unlinked.DeliverMetadata, unlinked.DeliverPayload));
        HoldsParent(0, 1_700_000_000_000_200);

        // An operator's removal delivers the record again on its flow's next run, and its landing asks the canonical log again.
        (custom, customSent, _) = await CustomAsync(tables);
        await DeliverAsync(custom);
        Assert.Equal(CustomId(0), Assert.Single(customSent.Deliveries).TargetId);
        HoldsChild(0);
        Assert.NotNull((await ledger.GetRecordAsync(canonical.Flow.Id, SampleEstate.Key(0)))!.PlanRequestedUtc);

        // The canonical flow links it again, and both flows have settled.
        (canonical, canonicalSent, _) = await CanonicalAsync(tables);
        await DeliverAsync(canonical);
        var linked = Assert.Single(canonicalSent.Deliveries);
        Assert.Equal(CustomId(0) + ":", Link(linked)!.GetValue<string>());
        HoldsParent(0, 1_700_000_000_000_300);
        (custom, customSent, _) = await CustomAsync(tables);
        await DeliverAsync(custom);
        Assert.Empty(customSent.Deliveries);
        (canonical, canonicalSent, _) = await CanonicalAsync(tables);
        await DeliverAsync(canonical);
        Assert.Empty(canonicalSent.Deliveries);
    }

    [Fact]
    public async Task A_custom_log_taken_out_by_another_system_is_seen_by_a_render_and_the_preview_says_the_next_run_would_not()
    {
        var tables = await LinkedAsync();

        // Another system takes the first log's custom record out of OSDU; nothing tells the ledger.
        _children.Remove(CustomId(0));
        var (canonical, canonicalSent, _) = await CanonicalAsync(tables);

        // Rendered now, the log has no link, so it differs from the document delivered; its row is unchanged, so the next run
        // skips it without rendering it, and the preview says so.
        var preview = await new RecordPreviewer(canonical).PreviewAsync(PreviewKey(0));
        Assert.Null(preview.Document!.Rendered!["data"]?["ExtensionProperties"]?["WellDb"]?["CustomLogID"]);
        Assert.Equal(("skip", "fingerprint", (bool?)false), (preview.Decision!.Action, preview.Decision.SkipTier, preview.Decision.Ledger!.SameDocument));
        Assert.Contains("redeliver it to send this document", preview.Decision.Reason, StringComparison.Ordinal);

        // A log whose render is the document delivered says nothing more.
        var same = await new RecordPreviewer(canonical).PreviewAsync(PreviewKey(1));
        Assert.Equal(("skip", (bool?)true), (same.Decision!.Action, same.Decision.Ledger!.SameDocument));
        Assert.DoesNotContain("redeliver", same.Decision.Reason, StringComparison.Ordinal);

        await DeliverAsync(canonical);
        Assert.Empty(canonicalSent.Deliveries);
    }

    /// <summary>The key a preview names log <paramref name="index"/> by: its key's parts as a JSON array.</summary>
    private static string PreviewKey(int index)
        => System.Text.Json.JsonSerializer.Serialize(new[] { SampleEstate.Logs()[index].SourceProject, SampleEstate.Logs()[index].LogId });
}
