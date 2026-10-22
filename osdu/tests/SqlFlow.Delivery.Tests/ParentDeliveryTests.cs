using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A custom log delivered beside the canonical one, end to end (osdu/docs/reference/flow/mapping-parent.md): the sample
/// estate's logs rendered with a custom template, a full copy of WellLog 1.4.0 under an entity type of its own with a
/// property the canonical one lacks, linked to WellLog 1.4.0, through a storage flow whose mapping takes each record's DDMS
/// references from its parent, the WellLog record of the same minted value, read from the platform as the record renders.
/// The canonical flow delivers the parents through the same ledger, each run as the executor makes it, over the sample
/// estate, the module's database on SQL Server, a fake protocol, and a platform that holds the parents a test gives it.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class ParentDeliveryTests : IDisposable
{
    private const string WellLogKind = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string CustomKind = "welldb:wks:work-product-component--CustomLog:1.0.0";
    private const string CustomFlowName = "welldb-customlog-03-delivery";

    /// <summary>The log whose parent the platform does not hold when the custom flow first renders it.</summary>
    private const int Orphan = 1;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    /// <summary>Where each flow's last run read through: its next run reads the rows the ingestion tables stamped after it.</summary>
    private readonly Dictionary<string, DateTime> _readThrough = new(StringComparer.Ordinal);

    /// <summary>The WellLog records the platform holds, by OSDU id, as the parents a custom record reads.</summary>
    private readonly Dictionary<string, JsonObject> _platform = new(StringComparer.Ordinal);

    private readonly SchemaSnapshot _custom;

    public ParentDeliveryTests()
    {
        _custom = CustomTemplate();
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "mappings")).FullName, "CustomLog@1.0.0.yaml"), CustomMapping(_custom.Version));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        _db.Dispose();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The sample WellLog template copied under the custom kind, linked to WellLog 1.4.0, with the pass type the well database
    /// records for a log, which the canonical template has no property for.
    /// </summary>
    private static SchemaSnapshot CustomTemplate()
    {
        var root = (JsonObject)Samples.SampleTemplate(WellLogKind).Root.DeepClone();
        root["$id"] = "https://schema.example.org/json/work-product-component/CustomLog.1.0.0.json";
        root["x-osdu-schema-source"] = CustomKind;
        root[TemplateParent.Key] = new JsonArray(new JsonObject { ["name"] = "WellLog", ["kind"] = WellLogKind });
        var log = root["properties"]!["data"]!["allOf"]!.AsArray().OfType<JsonObject>()
            .Single(part => part["properties"] is JsonObject properties && properties.ContainsKey("WellboreID"));
        log["properties"]!.AsObject()["LogPassType"] = new JsonObject { ["type"] = "string", ["description"] = "The pass type the well database records for the log." };
        return new SchemaSnapshot(CustomKind, root, new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>The sample WellLog mapping as the custom template's: the same dataset and key, the pass type, and the DDMS references from the parent.</summary>
    private static string CustomMapping(string version)
    {
        var text = File.ReadAllText(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml")).ReplaceLineEndings("\n");
        text = Replace(text, "\nname: WellLog\nversion: 1.4.0\n", "\nname: CustomLog\nversion: 1.0.0\n");
        text = Replace(text, $"  kind: {WellLogKind}\n  version: 26a3c3441882db4f\n", $"  kind: {CustomKind}\n  version: {version}\n");
        return Replace(text, "\n  data:\n", """

              data:
                LogPassType: { $from: log_pass_type }
                DDMSDatasets: { $parent: data.DDMSDatasets }
                ExtensionProperties: { $parent: data.ExtensionProperties }

            """);
    }

    private static string Replace(string text, string from, string to)
    {
        Assert.Equal(1, text.Split(from).Length - 1);
        return text.Replace(from, to, StringComparison.Ordinal);
    }

    private static string ParentId(int index) => TargetId.Compose(Samples.SamplePartition, "work-product-component--WellLog", SampleEstate.Key(index));

    private static string CustomId(int index) => TargetId.Compose(Samples.SamplePartition, "work-product-component--CustomLog", SampleEstate.Key(index));

    /// <summary>The WellLog record of log <paramref name="index"/> as the platform holds it once the wellbore DDMS wrote its bulk data, at <paramref name="version"/>.</summary>
    private void Holds(int index, long version, string dataset = "bulk-1")
    {
        var id = ParentId(index);
        _platform[id] = new JsonObject
        {
            ["id"] = id,
            ["kind"] = WellLogKind,
            ["version"] = version,
            ["acl"] = new JsonObject { ["owners"] = new JsonArray("owners@dev"), ["viewers"] = new JsonArray("viewers@dev") },
            ["legal"] = new JsonObject { ["legaltags"] = new JsonArray("dev-tag"), ["otherRelevantDataCountries"] = new JsonArray("US") },
            ["data"] = new JsonObject
            {
                ["Name"] = "parent " + index,
                ["DDMSDatasets"] = new JsonArray($"urn:wdms-1:uuid:{dataset}-{index}"),
                ["ExtensionProperties"] = new JsonObject
                {
                    ["wdms"] = new JsonObject { ["bulkURI"] = $"urn:wdms-1:uuid:{dataset}-{index}", ["sessionId"] = index },
                },
            },
        };
    }

    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> CanonicalAsync(MemoryIngestionTables tables)
        => await RuntimeAsync(tables, Samples.LocalFlow(_root));

    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> CustomAsync(MemoryIngestionTables tables)
    {
        var flow = Samples.LocalFlow(_root);
        return await RuntimeAsync(tables, flow with
        {
            Name = CustomFlowName,
            Source = flow.Source with
            {
                Payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal),
                Work = Path.Combine(_root, "custom-work"),
            },
            Render = flow.Render with { Mapping = "CustomLog@1.0.0", MappingsDirectory = Path.Combine(_root, "mappings") },
            Target = flow.Target with { Protocol = DeliveryProtocol.Storage, ProtocolOptions = new ProtocolOptions() },
        });
    }

    /// <summary>A runtime of <paramref name="flow"/> over the platform as it holds the parents now: each run reads them afresh.</summary>
    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> RuntimeAsync(MemoryIngestionTables tables, FlowDefinition flow)
    {
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var templates = new MemoryTemplates(Samples.TemplateKinds().Select(Samples.SampleTemplate)).Add(_custom);
        var searches = FixedRecordSearchFactory.SampleWellbores(new Dictionary<string, JsonObject>(_platform, StringComparer.Ordinal));
        var engine = Samples.Engine(ledger, _clock, templates: templates, sources: tables, searches: searches) with { Protocols = new FakeProtocolFactory(protocol) };
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        return (runtime, protocol, ledger);
    }

    /// <summary>
    /// One deliver run as the executor makes it: the records asked to be planned again, then the rows stamped since the flow's
    /// last run. Returns the second part's result; what the first sent is read off the protocol and the ledger.
    /// </summary>
    private async Task<RunResult> DeliverAsync(FlowRuntime runtime)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var selection = SourceSelection.Incremental(_readThrough.TryGetValue(runtime.Flow.Name, out var through) ? through : null);
        runtime.Selection = selection;
        runtime.RunId = Guid.CreateVersion7();
        await DeliveryExecutor.DeliverRequestedAsync(runtime, selection, DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
        runtime.Selection = selection;
        var run = await runtime.RunAsync(force: false);
        _readThrough[runtime.Flow.Name] = Now;
        return run;
    }

    private static DeliveryWork Sent(FakeProtocol protocol, int index) => Assert.Single(protocol.Deliveries, d => d.TargetId == CustomId(index));

    private static JsonObject Data(DeliveryWork work) => work.Document["data"]!.AsObject();

    [Fact]
    public async Task A_custom_log_takes_its_parents_DDMS_references_waits_for_a_missing_parent_and_follows_a_changed_one()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var logs = SampleEstate.Logs().Count;
        Assert.True(logs > Orphan + 1, "The sample estate holds too few logs for this scenario.");
        for (var i = 0; i < logs; i++)
        {
            if (i != Orphan)
            {
                Holds(i, 1_700_000_000_000_000 + i);
            }
        }

        // 1. The custom flow renders every log: each record takes its DDMS references from the WellLog record of the same
        //    minted value, and the one whose parent the platform does not hold is held, its parent named, nothing sent.
        var (custom, sent, ledger) = await CustomAsync(tables);
        var first = await DeliverAsync(custom);
        Assert.Equal(logs - 1, first.Work.Delivered);
        for (var i = 0; i < logs; i++)
        {
            if (i == Orphan)
            {
                Assert.DoesNotContain(sent.Deliveries, d => d.TargetId == CustomId(i));
                continue;
            }

            var data = Data(Sent(sent, i));
            Assert.Equal(_platform[ParentId(i)]["data"]!["DDMSDatasets"]!.ToJsonString(), data["DDMSDatasets"]!.ToJsonString());
            Assert.Equal(_platform[ParentId(i)]["data"]!["ExtensionProperties"]!.ToJsonString(), data["ExtensionProperties"]!.ToJsonString());
            Assert.Equal(SampleEstate.Logs()[i].LogPassType, data["LogPassType"]!.GetValue<string>());

            // Only the sections the mapping names are taken: the parent's own name stays the parent's.
            Assert.NotEqual("parent " + i, data["Name"]?.GetValue<string>());
            var record = (await ledger.GetRecordAsync(custom.Flow.Id, SampleEstate.Key(i)))!;
            Assert.Equal((RecordStatus.Delivered, ParentId(i), (long?)(1_700_000_000_000_000 + i)), (record.Status, record.ParentId, record.ParentVersion));
            var attempt = Assert.Single(await ledger.ListAttemptsAsync(custom.Flow.Id, SampleEstate.Key(i), 10));
            Assert.Equal(1_700_000_000_000_000 + i, attempt.ParentVersion);
        }

        var orphan = (await ledger.GetRecordAsync(custom.Flow.Id, SampleEstate.Key(Orphan)))!;
        Assert.Equal((RecordStatus.Held, true, ParentId(Orphan)), (orphan.Status, orphan.Blocked, orphan.ParentId));
        Assert.Null(orphan.PendingDocumentRef);
        Assert.Contains($"the record's parent {ParentId(Orphan)} ({WellLogKind}) is not in OSDU", orphan.LastError, StringComparison.Ordinal);

        // A run over rows that did not change sends nothing: the orphan stays held until its parent lands.
        sent.Deliveries.Clear();
        Assert.Equal(0, (await DeliverAsync(custom)).Work.Delivered);
        Assert.Empty(sent.Deliveries);

        // 2. The canonical flow delivers the logs through the same ledger. Each landing releases the custom record held for
        //    it under an intervention the trail shows, and asks the ones rendered from it to render again.
        var (canonical, canonicalSent, _) = await CanonicalAsync(tables);
        Assert.Equal(logs, (await DeliverAsync(canonical)).Work.Delivered);
        Assert.Contains(canonicalSent.Deliveries, d => d.TargetId == ParentId(Orphan));
        orphan = (await ledger.GetRecordAsync(custom.Flow.Id, SampleEstate.Key(Orphan)))!;
        Assert.False(orphan.Blocked);
        Assert.NotNull(orphan.PlanRequestedUtc);
        var release = Assert.Single(
            await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = custom.Flow.Id, DeliveryKey = SampleEstate.Key(Orphan).Value }),
            a => a.Actor == OsduLedger.ParentActor);
        Assert.Equal("release", release.Kind);
        Assert.All(Enumerable.Range(0, logs), i => Assert.NotNull(ledger.GetRecordAsync(custom.Flow.Id, SampleEstate.Key(i)).GetAwaiter().GetResult()!.PlanRequestedUtc));

        // 3. The platform now holds the orphan's parent. The custom flow's next run renders the records asked for again: the
        //    orphan goes out with its parent's references, and every other one, rendered from a parent whose sections did not
        //    change, renders as it was and is not sent again.
        Holds(Orphan, 1_700_000_000_000_100);
        (custom, sent, ledger) = await CustomAsync(tables);
        await DeliverAsync(custom);
        Assert.Single(sent.Deliveries);
        Assert.Equal($"urn:wdms-1:uuid:bulk-1-{Orphan}", Data(Sent(sent, Orphan))["DDMSDatasets"]![0]!.GetValue<string>());
        orphan = (await ledger.GetRecordAsync(custom.Flow.Id, SampleEstate.Key(Orphan)))!;
        Assert.Equal((RecordStatus.Delivered, (long?)1_700_000_000_000_100), (orphan.Status, orphan.ParentVersion));
        Assert.All(Enumerable.Range(0, logs), i => Assert.Null(ledger.GetRecordAsync(custom.Flow.Id, SampleEstate.Key(i)).GetAwaiter().GetResult()!.PlanRequestedUtc));

        // 4. The wellbore DDMS writes log 0's bulk data again, and the canonical flow delivers it again: the custom record
        //    renders from the parent's new references and goes out with them, recording the version it read.
        Holds(0, 1_700_000_000_000_200, dataset: "bulk-2");
        var row = tables.Records[0];
        Assert.Equal(SampleEstate.Logs()[0].LogId, row.Row["log_id"]?.ToString());
        _clock.Advance(TimeSpan.FromMinutes(1));
        row.Row["log_version"] = "2";
        row.UpdatedUtc = Now;
        (canonical, _, _) = await CanonicalAsync(tables);
        Assert.Equal(1, (await DeliverAsync(canonical)).Work.Delivered);
        Assert.NotNull((await ledger.GetRecordAsync(custom.Flow.Id, SampleEstate.Key(0)))!.PlanRequestedUtc);

        // The custom flow's own read is moved past the corrected row, so what renders its record again is the parent's
        // landing alone.
        (custom, sent, ledger) = await CustomAsync(tables);
        _readThrough[CustomFlowName] = Now;
        await DeliverAsync(custom);
        Assert.Single(sent.Deliveries);
        var changed = Data(Sent(sent, 0));
        Assert.Equal("urn:wdms-1:uuid:bulk-2-0", changed["DDMSDatasets"]![0]!.GetValue<string>());
        Assert.Equal("urn:wdms-1:uuid:bulk-2-0", changed["ExtensionProperties"]!["wdms"]!["bulkURI"]!.GetValue<string>());
        var updated = (await ledger.GetRecordAsync(custom.Flow.Id, SampleEstate.Key(0)))!;
        Assert.Equal((RecordStatus.Delivered, (long?)1_700_000_000_000_200, (DateTime?)null), (updated.Status, updated.ParentVersion, updated.PlanRequestedUtc));
        Assert.Equal(1_700_000_000_000_200, (await ledger.ListAttemptsAsync(custom.Flow.Id, SampleEstate.Key(0), 10))[0].ParentVersion);
    }
}
