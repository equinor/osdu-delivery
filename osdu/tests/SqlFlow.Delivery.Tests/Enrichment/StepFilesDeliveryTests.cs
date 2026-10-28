using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A delivery flow whose mapping's enrichment registers each log's LAS file as a dataset, linked from the log with <c>$link</c>,
/// end to end (osdu/docs/reference/flow/enrich-records.md):
/// every run made as the executor makes it, over the sample estate, the module's database on SQL Server, the
/// flow's own route counting the records it writes, and a fake platform serving the Dataset service. The first run registers
/// each log's dataset and writes the log naming it; new files register a new version of the same dataset and leave the log as
/// OSDU holds it; a change to the log writes it once and registers nothing; nothing changed sends nothing; and a log whose LAS
/// file row goes is written once without the link, the dataset left live. On a Wellbore DDMS flow that carries the log's
/// dataset list over from the record OSDU holds, an update keeps what another system listed there and adds or takes off the
/// step's link alone; and the pipeline's cache after step reads the datasets a run's steps registered back into a type of
/// their kind.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class StepFilesDeliveryTests : IDisposable
{
    private const string Register = "/api/dataset/v1/registerDataset";

    /// <summary>A Wellbore DDMS flow's options carrying the log's dataset lists over from the record OSDU holds, as the well log estate's delivery does.</summary>
    private static readonly ProtocolOptions PreservingDdms = new() { DdmsRoot = FakeOsduPlatform.DdmsRoot, PreserveDataKeys = ["Datasets", "DDMSDatasets"] };

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly string _las;
    private readonly string _mappings;
    private readonly FakeOsduPlatform _platform = new();
    private readonly HttpRuntime _http;
    private DateTime? _readThrough;

    public StepFilesDeliveryTests()
    {
        _las = Directory.CreateDirectory(Path.Combine(_root, "las")).FullName;
        _mappings = Directory.CreateDirectory(Path.Combine(_root, "mappings")).FullName;
        EnrichmentSuite.WriteWellLogMapping(_mappings, "lasfile:\n  rows: one\n  mapping: WellLogLasDataset@1.0.0\n", data: "Datasets: [{ $link: lasfile }]");
        EnrichmentSuite.WriteEnrichmentMapping(_mappings, "WellLogLasDataset", EnrichmentSuite.DatasetKind, "tags:\n  LasFolder: { $from: las_folder }\n");
        _http = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), TimeProvider.System, _platform, allowLoopback: true);
    }

    public void Dispose()
    {
        _http.Dispose();
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
                Files = new FlowPayload { Root = _las, LocationColumn = "las_folder", Pattern = "*.las", HashColumn = "las_hash", ContentType = "text/plain" },
            },
        };
        return flow with
        {
            Source = flow.Source with { Datasets = datasets, Payloads = new Dictionary<string, FlowPayload>(StringComparer.Ordinal) },
            Render = flow.Render with { MappingsDirectory = _mappings },
            Target = flow.Target with { Protocol = DeliveryProtocol.Storage, ProtocolOptions = new ProtocolOptions() },
        };
    }

    /// <summary>The flow over the Wellbore DDMS, carrying the log's dataset lists over from the record OSDU holds.</summary>
    private FlowDefinition PreservingDdmsFlow()
    {
        var flow = Flow();
        return flow with { Target = flow.Target with { Protocol = DeliveryProtocol.Ddms, ProtocolOptions = PreservingDdms } };
    }

    /// <summary>One deliver run as the executor makes it: the flow's own route counting what it writes, wrapped as a flow with step files is.</summary>
    private async Task<(FakeProtocol Written, OsduLedger Ledger, Guid FlowId, RunResult Run)> DeliverAsync(MemoryIngestionTables tables)
    {
        var written = new FakeProtocol();
        var (ledger, flowId, run, _) = await RunAsync(tables, Flow(), _ => written);
        return (written, ledger, flowId, run);
    }

    /// <summary>
    /// One deliver run of <paramref name="flow"/> as the executor makes it, the flow's own route made by <paramref name="inner"/>
    /// over the platform and wrapped as a flow adding records is; with the flow as the run bound it to its mapping's enrichments.
    /// </summary>
    private async Task<(OsduLedger Ledger, Guid FlowId, RunResult Run, FlowDefinition Bound)> RunAsync(MemoryIngestionTables tables, FlowDefinition flow, Func<OsduHttpClient, IDeliveryProtocol> inner)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var ledger = _db.Ledger(_clock);
        var client = Client();
        var options = new ProtocolOptions { UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob" } };
        var route = new EnrichPartsProtocol(inner(client), client, options);
        var templates = new MemoryTemplates(Samples.TemplateKinds().Select(Samples.SampleTemplate));
        var engine = Samples.Engine(ledger, _clock, templates: templates, sources: tables) with { Protocols = new FakeProtocolFactory(route) };
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);

        var selection = SourceSelection.Incremental(_readThrough);
        runtime.Selection = selection;
        runtime.RunId = Guid.CreateVersion7();
        await DeliveryExecutor.DeliverRequestedAsync(runtime, selection, DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
        runtime.Selection = selection;
        var run = await runtime.RunAsync(force: false);
        _readThrough = Now;
        return (ledger, runtime.Flow.Id, run, runtime.Flow);
    }

    /// <summary>The platform as the flow reaches it, in partition dev.</summary>
    private OsduHttpClient Client() => new(
        _http, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });

    /// <summary>The flow's own route over the Wellbore DDMS, carrying the log's dataset lists over.</summary>
    private static IDeliveryProtocol Ddms(OsduHttpClient client) => new OsduDdmsProtocol(client, PreservingDdms, NullLogger.Instance);

    /// <summary>Gives a log its LAS file, in a folder of its own with the content given, or takes its row away (null).</summary>
    private void LasFile(MemoryRecord record, string? content)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        record.Datasets.Remove("lasfile");
        if (content is not null)
        {
            var folder = $"{record.Row["source_project"]}-{record.Row["log_id"]}";
            var path = Directory.CreateDirectory(Path.Combine(_las, folder)).FullName;
            File.WriteAllText(Path.Combine(path, "log.las"), content);
            record.AddChild("lasfile", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["source_project"] = record.Row["source_project"],
                ["log_id"] = record.Row["log_id"],
                ["las_folder"] = folder,
                ["las_hash"] = Hashing.ContentHash.Of(content),
            });
        }

        record.DatasetUpdatedUtc["lasfile"] = Now;
    }

    private int Registrations(int from = 0)
        => _platform.Calls.Skip(from).Count(c => c.Method == HttpMethod.Put && Uri.UnescapeDataString(c.Uri.AbsolutePath) == Register);

    private static string DatasetOf(string recordId) => WorkflowValues.DerivedDatasetId(recordId, "dataset--File.Generic", "enrich.lasfile");

    private static List<string> Links(JsonObject document)
        => document["data"]?["Datasets"] is JsonArray links ? links.Select(l => l!.GetValue<string>()).ToList() : [];

    [Fact]
    public async Task A_logs_LAS_file_is_registered_under_one_id_for_good_and_new_files_never_write_the_log_again()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        Assert.True(tables.Records.Count >= 3, "The sample estate holds too few logs for this scenario.");
        LasFile(tables.Records[0], "~Version Information A");
        LasFile(tables.Records[1], "~Version Information B");

        // 1. Every log is written once; each with a LAS file names its dataset, registered before it under the id its own makes.
        var (written, ledger, flowId, run) = await DeliverAsync(tables);
        Assert.Equal(tables.Records.Count, run.Work.Delivered);
        Assert.Equal(tables.Records.Count, written.Deliveries.Count);
        var first = Assert.Single(written.Deliveries, d => d.Key == SampleEstate.Key(0));
        var dataset = DatasetOf(first.TargetId);
        Assert.Equal([dataset + ":"], Links(first.Document));
        Assert.Empty(Links(Assert.Single(written.Deliveries, d => d.Key == SampleEstate.Key(2)).Document));
        Assert.Equal("log.las", _platform.Records[dataset]["data"]!["Name"]!.GetValue<string>());
        Assert.NotNull(_platform.Records[dataset]["tags"]!["LasFolder"]);
        Assert.Equal(first.Document["acl"]!.ToJsonString(), _platform.Records[dataset]["acl"]!.ToJsonString());
        Assert.Equal(2, Registrations());
        var record = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!;
        var kept = JsonMerge.ToValues(record.TargetStateJson);
        Assert.Equal([dataset], EnrichParts.Records(kept));
        Assert.Equal(Hashing.ContentHash.Of("~Version Information A"), kept[EnrichParts.FilesStateKey(dataset)]);

        // 2. Nothing changed: nothing is sent, and nothing registered.
        var mark = _platform.Calls.Count;
        (written, _, _, run) = await DeliverAsync(tables);
        Assert.Empty(written.Deliveries);
        Assert.Equal(0, Registrations(mark));

        // 3. New files for one log: its dataset is registered again under the same id, and the log is not written.
        LasFile(tables.Records[0], "~Version Information A, corrected");
        mark = _platform.Calls.Count;
        (written, ledger, _, run) = await DeliverAsync(tables);
        Assert.Empty(written.Deliveries);
        Assert.Equal(1, Registrations(mark));
        Assert.Equal(2, _platform.History[dataset].Count);
        Assert.Equal(1, run.Work.Delivered);
        record = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!;
        Assert.Equal(RecordStatus.Delivered, record.Status);
        Assert.Equal(Hashing.ContentHash.Of("~Version Information A, corrected"), JsonMerge.ToValues(record.TargetStateJson)[EnrichParts.FilesStateKey(dataset)]);

        // 4. The same files again: nothing is sent.
        mark = _platform.Calls.Count;
        (written, _, _, _) = await DeliverAsync(tables);
        Assert.Empty(written.Deliveries);
        Assert.Equal(0, Registrations(mark));

        // 5. The log itself changed: it is written once, naming the same dataset, and nothing is registered.
        _clock.Advance(TimeSpan.FromSeconds(1));
        SampleEstate.Change(tables.Records[0], "log_run", "RUN-CHANGED", Now);
        mark = _platform.Calls.Count;
        (written, _, _, _) = await DeliverAsync(tables);
        var changed = Assert.Single(written.Deliveries);
        Assert.Equal([dataset + ":"], Links(changed.Document));
        Assert.Equal(0, Registrations(mark));

        // 6. The log's LAS file row goes: the log is written once without the link, and the dataset stays live.
        LasFile(tables.Records[0], null);
        (written, ledger, _, _) = await DeliverAsync(tables);
        var removed = Assert.Single(written.Deliveries);
        Assert.Empty(Links(removed.Document));
        Assert.DoesNotContain(dataset, _platform.Removed);
        record = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!;
        Assert.Empty(EnrichParts.Records(JsonMerge.ToValues(record.TargetStateJson)));
    }

    [Fact]
    public async Task The_ledger_traces_each_dataset_a_log_adds_to_the_log_its_files_and_every_write_made_of_it()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        LasFile(tables.Records[0], "~Version Information A");

        // 1. The first delivery registers the log's dataset: the ledger lists it under the log, linked, with its files and one write.
        var (_, ledger, flowId, _) = await DeliverAsync(tables);
        var (record, enrichment) = await EnrichmentAsync(ledger, flowId, 0);
        var dataset = DatasetOf(record.TargetId!);
        Assert.Equal("lasfile", enrichment.Name);
        Assert.Equal(["records"], enrichment.Adds);
        Assert.Equal("WellLogLasDataset@1.0.0", enrichment.Mapping);
        Assert.Equal((EnrichmentSuite.DatasetKind, "dataset"), (enrichment.RecordKind, enrichment.WrittenThrough));
        Assert.False(enrichment.SetAside);
        Assert.Equal(1, enrichment.Rows!.Count);
        var added = Assert.Single(enrichment.Records);
        Assert.Equal((dataset, true), (added.Id, added.Linked));
        Assert.Equal(Hashing.ContentHash.Of("~Version Information A"), added.FilesHash);
        var created = Assert.Single(added.Writes);
        Assert.Equal(("delivered", null), (created.Outcome, created.PriorVersion));

        // A lookup by the dataset's id finds the log it was added to.
        Assert.Equal(SampleEstate.Key(0), Assert.Single(await ledger.LookupAsync(dataset, 10, flowId: flowId)).DeliveryKey);

        // A log without a LAS file lists the enrichment with nothing added.
        Assert.Empty((await EnrichmentAsync(ledger, flowId, 2)).Enrichment.Records);

        // 2. New files register the dataset again: both writes are listed, the newest first, the new one replacing the version
        // the first landed as, and the files hash is the new one.
        LasFile(tables.Records[0], "~Version Information A, corrected");
        (_, ledger, _, _) = await DeliverAsync(tables);
        added = Assert.Single((await EnrichmentAsync(ledger, flowId, 0)).Enrichment.Records);
        Assert.Equal(2, added.Writes.Count);
        Assert.True(added.Writes[0].AtUtc >= added.Writes[1].AtUtc);
        Assert.Equal((created.AttemptId, null), (added.Writes[1].AttemptId, added.Writes[1].PriorVersion));
        Assert.NotNull(added.Writes[0].PriorVersion);
        Assert.Equal(Hashing.ContentHash.Of("~Version Information A, corrected"), added.FilesHash);

        // 3. The log's LAS file row goes: the dataset stays listed with its writes, no longer linked.
        LasFile(tables.Records[0], null);
        (_, ledger, _, _) = await DeliverAsync(tables);
        added = Assert.Single((await EnrichmentAsync(ledger, flowId, 0)).Enrichment.Records);
        Assert.Equal((dataset, false, 2), (added.Id, added.Linked, added.Writes.Count));
    }

    /// <summary>The log at <paramref name="index"/> of the sample estate as the ledger holds it, and its one enrichment as the ledger traces it.</summary>
    private static async Task<(RecordState Record, RecordEnrichment Enrichment)> EnrichmentAsync(OsduLedger ledger, Guid flowId, int index)
    {
        var record = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(index)))!;
        var attempts = await ledger.ListAttemptsAsync(flowId, SampleEstate.Key(index), RecordEnrichments.MaxAttempts);
        return (record, Assert.Single(RecordEnrichments.Of(record, attempts)));
    }

    [Fact]
    public async Task On_a_DDMS_flow_preserving_its_dataset_list_an_update_keeps_what_OSDU_lists_and_adds_or_takes_off_the_steps_link_alone()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        LasFile(tables.Records[0], "~Version Information A");

        // 1. The log is created through the DDMS naming its dataset: OSDU holds no list of it to carry over yet.
        var (ledger, flowId, run, _) = await RunAsync(tables, PreservingDdmsFlow(), Ddms);
        Assert.Equal(tables.Records.Count, run.Work.Delivered);
        var logId = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!.TargetId!;
        var link = EnrichParts.Link(DatasetOf(logId));
        Assert.Equal([link], Links(_platform.Records[logId]));

        // 2. Another system lists a dataset of its own on the log, ahead of the step's, and on a log the step links nothing in.
        const string foreign = "dev:dataset--File.Generic:from-elsewhere:";
        _platform.Records[logId]["data"]!["Datasets"] = new JsonArray(foreign, link);
        var otherId = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(1)))!.TargetId!;
        _platform.Records[otherId]["data"]!["Datasets"] = new JsonArray(foreign);

        // 3. Both logs change: each update keeps the list as OSDU holds it, the step's link once.
        _clock.Advance(TimeSpan.FromSeconds(1));
        SampleEstate.Change(tables.Records[0], "log_run", "RUN-CHANGED", Now);
        SampleEstate.Change(tables.Records[1], "log_run", "RUN-CHANGED", Now);
        var mark = _platform.Calls.Count;
        (_, _, run, _) = await RunAsync(tables, PreservingDdmsFlow(), Ddms);
        Assert.Equal(2, run.Work.Delivered);
        Assert.Equal(0, Registrations(mark));
        Assert.Equal([foreign, link], Links(_platform.Records[logId]));
        Assert.Equal([foreign], Links(_platform.Records[otherId]));

        // 4. The log's LAS file row goes: the update takes the step's link off and keeps the other system's, the dataset left live.
        LasFile(tables.Records[0], null);
        (_, _, run, _) = await RunAsync(tables, PreservingDdmsFlow(), Ddms);
        Assert.Equal(1, run.Work.Delivered);
        Assert.Equal([foreign], Links(_platform.Records[logId]));
        Assert.DoesNotContain(DatasetOf(logId), _platform.Removed);
    }

    [Fact]
    public async Task The_cache_after_step_reads_the_datasets_a_runs_steps_registered_back_into_a_type_of_their_kind()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        LasFile(tables.Records[0], "~Version Information A");
        var started = Now;
        var (ledger, flowId, _, flow) = await RunAsync(tables, PreservingDdmsFlow(), Ddms);
        var logId = TargetId.WithoutVersion((await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!.TargetId!).TrimEnd(':');
        var dataset = DatasetOf(logId);

        // The ledger names the dataset beside the logs the run changed when asked for what their target state lists.
        Assert.Contains(dataset, await ledger.ChangedSinceAsync(flowId, started, 1_000, EnrichParts.RegisteredValue));
        Assert.DoesNotContain(dataset, await ledger.ChangedSinceAsync(flowId, started, 1_000));

        await _db.DeclareCacheAsync("dev", "welldb-logs-cache", new ReferenceTypeSpec
        {
            Name = "WellLog",
            EntityType = "work-product-component--WellLog",
            Kind = "osdu:wks:work-product-component--WellLog:*",
            Fields = [new ReferenceFieldSpec("data.Name")],
        });
        await _db.DeclareCacheAsync("dev", "welldb-datasets-cache", new ReferenceTypeSpec
        {
            Name = "LasFile",
            EntityType = "dataset--File.Generic",
            Kind = "osdu:wks:dataset--File.Generic:*",
            Fields = [new ReferenceFieldSpec("data.Name")],
        });
        var caches = _db.Caches();
        var plan = CacheStepPlan.Resolve(
            flow.Label, new FlowCacheSteps { After = new CacheStepEntries { Auto = true } }, await caches.DeclarationAsync("dev"),
            Samples.WellLogKind, new MappingCacheReads([], [], []), EnrichRecords.KindsOf(flow));
        Assert.Equal(["LasFile", "WellLog"], plan.After.Select(t => t.TypeName).Order(StringComparer.Ordinal));
        Assert.True(plan.TakesStepDatasets);

        var keeper = new CacheKeeper(Samples.Engine(ledger, _clock) with { Cache = caches }, NullLogger.Instance);
        var after = await keeper.AfterAsync(
            "dev", plan.After, flowId, flow.LedgerName, started, _ => Task.FromResult(Client()),
            OsduRecordProtocol.DefaultVerifyBatchPath, Guid.NewGuid(), "tests", null, plan.TakesStepDatasets, CancellationToken.None);

        // Each record went into the type of its own kind: the LAS file's dataset among the datasets, the logs among the logs.
        var cached = (await caches.LoadAsync("dev", after!.Version!))!;
        var las = Assert.Single(cached.Type("LasFile")!.Items);
        Assert.Equal((dataset, "log.las"), (las.Id, las.Fields["Name"].Text));
        var logs = cached.Type("WellLog")!.Items.Select(i => i.Id).ToList();
        Assert.Equal(tables.Records.Count, logs.Count);
        Assert.Contains(logId, logs);
        Assert.DoesNotContain(dataset, logs);
    }

    [Fact]
    public async Task A_redelivery_of_a_log_writes_it_and_registers_its_dataset_again()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        LasFile(tables.Records[0], "~Version Information A");
        var (_, ledger, flowId, _) = await DeliverAsync(tables);

        await ledger.ForceRedeliverAsync(flowId, [SampleEstate.Key(0)], RedeliverScope.All, Now);
        var mark = _platform.Calls.Count;
        var (written, _, _, _) = await DeliverAsync(tables);

        Assert.Equal(SampleEstate.Key(0), Assert.Single(written.Deliveries).Key);
        Assert.Equal(1, Registrations(mark));
    }

    /// <summary>
    /// The flow over a mapping whose enrichment <c>note</c> adds a Document record to each log from a row of its own, written
    /// through storage, and links it from the log's extension properties.
    /// </summary>
    private FlowDefinition NotesFlow()
    {
        const string documentKind = "osdu:wks:work-product-component--Document:1.0.0";
        var mappings = Directory.CreateDirectory(Path.Combine(_root, "notes-mappings")).FullName;
        EnrichmentSuite.WriteWellLogMapping(mappings, "note:\n  rows: one\n  mapping: WellLogNote@1.0.0\n", data: "ExtensionProperties:\n  WellDb:\n    NoteID: { $link: note }");
        EnrichmentSuite.WriteEnrichmentMapping(mappings, "WellLogNote", documentKind, "data:\n  Name: { $from: note }\n  Description: { $base: data.Name }\n");
        var flow = Flow();
        var datasets = flow.Source.Datasets.Where(d => d.Key != "lasfile").ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
        datasets["note"] = new FlowSourceDataset
        {
            Object = "OsduData.silver.WellLogNote",
            Join = new Dictionary<string, string>(StringComparer.Ordinal) { ["source_project"] = "source_project", ["log_id"] = "log_id" },
        };
        return flow with { Source = flow.Source with { Datasets = datasets }, Render = flow.Render with { MappingsDirectory = mappings } };
    }

    /// <summary>Gives a log its note row, or takes it away (null).</summary>
    private void Note(MemoryRecord record, string? note)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        record.Datasets.Remove("note");
        if (note is not null)
        {
            record.AddChild("note", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["source_project"] = record.Row["source_project"],
                ["log_id"] = record.Row["log_id"],
                ["note"] = note,
            });
        }

        record.DatasetUpdatedUtc["note"] = Now;
    }

    private int StorageWrites(string id, int from = 0)
        => _platform.Calls.Skip(from).Count(c => c.Method == HttpMethod.Put && Uri.UnescapeDataString(c.Uri.AbsolutePath) == "/api/storage/v2/records"
            && c.Body is { } body && body.Contains('"' + id + '"', StringComparison.Ordinal));

    [Fact]
    public async Task A_record_of_another_kind_is_written_through_storage_and_again_only_when_it_changes()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        Note(tables.Records[0], "first note");

        // 1. The log goes once, linking the Document its note row renders, written through storage under the id its own makes.
        var written = new FakeProtocol();
        var (ledger, flowId, run, bound) = await RunAsync(tables, NotesFlow(), _ => written);
        Assert.Equal(tables.Records.Count, run.Work.Delivered);
        var log = Assert.Single(written.Deliveries, d => d.Key == SampleEstate.Key(0));
        var note = WorkflowValues.DerivedDatasetId(log.TargetId, "work-product-component--Document", "enrich.note");
        Assert.Equal(note + ":", log.Document["data"]!["ExtensionProperties"]!["WellDb"]!["NoteID"]!.GetValue<string>());
        var stored = _platform.Records[note];
        Assert.Equal(("osdu:wks:work-product-component--Document:1.0.0", "first note"), (stored["kind"]!.GetValue<string>(), stored["data"]!["Name"]!.GetValue<string>()));
        Assert.Equal(log.Document["legal"]!.ToJsonString(), stored["legal"]!.ToJsonString());
        Assert.Equal(0, Registrations());
        Assert.False(Assert.Single(bound.Enrich!).Records!.RegistersFiles);
        var record = (await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!;
        Assert.Equal([note], EnrichParts.Records(JsonMerge.ToValues(record.TargetStateJson)));

        // 2. Nothing changed: nothing is written.
        var mark = _platform.Calls.Count;
        written = new FakeProtocol();
        (_, _, run, _) = await RunAsync(tables, NotesFlow(), _ => written);
        Assert.Empty(written.Deliveries);
        Assert.Equal(0, StorageWrites(note, mark));

        // 3. The note changed: the Document gets a new version, and the log, which links it already, is not written.
        Note(tables.Records[0], "second note");
        mark = _platform.Calls.Count;
        written = new FakeProtocol();
        (_, _, run, _) = await RunAsync(tables, NotesFlow(), _ => written);
        Assert.Empty(written.Deliveries);
        Assert.Equal(1, StorageWrites(note, mark));
        Assert.Equal(2, _platform.History[note].Count);
        Assert.Equal("second note", _platform.Records[note]["data"]!["Name"]!.GetValue<string>());

        // 4. The note row goes: the log is written once without the link, and the Document stays live.
        Note(tables.Records[0], null);
        written = new FakeProtocol();
        (ledger, _, _, _) = await RunAsync(tables, NotesFlow(), _ => written);
        Assert.Null(Assert.Single(written.Deliveries).Document["data"]!["ExtensionProperties"]);
        Assert.DoesNotContain(note, _platform.Removed);
        Assert.Empty(EnrichParts.Records(JsonMerge.ToValues((await ledger.GetRecordAsync(flowId, SampleEstate.Key(0)))!.TargetStateJson)));
    }
}
