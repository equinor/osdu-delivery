using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The file route as a unit of work (osdu/docs/reference/concepts/artifacts-and-undo.md, file): every registration
/// declared by where its file landed before its call and completed with the dataset the service minted, the record's
/// write declared before it goes when the unit registered files for it, a registration whose answer was lost found
/// through the index and taken over, the extra datasets a lost answer let a later try register removed, the datasets of
/// an earlier delivery named superseded on a payload change, and the undo of every failure point: the record taken back
/// first (removed, given back its version, or gone when its write never landed), then every dataset registered for each
/// landing path removed reversibly through storage, the datasets waiting with a record that cannot be taken back yet, a
/// call that fails answered failed for its own items alone, and a second undo that changes nothing.
/// </summary>
public sealed class AtomicFileRouteTests
{
    private const string LogId = "dev:work-product-component--WellLog:atomic-1";
    private const string LogKind = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string Minted = "dev:dataset--File.Generic:minted-";
    private const string Metadata = "/api/file/v2/files/metadata";
    private const string StorageRecords = "/api/storage/v2/records";
    private const string Search = "/api/search/v2/query";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private sealed class Rig : IDisposable
    {
        public Rig(ProtocolOptions? options = null)
        {
            Platform = new FakeOsduPlatform();
            FileFamilyFaults.IndexLive(Platform);
            Faults = new FileFamilyFaults(Platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, Faults, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            Protocol = new OsduFileProtocol(Client, options ?? new ProtocolOptions { DatasetIndexWaitSeconds = 0, WorkflowPollSeconds = 1 });
        }

        public FakeOsduPlatform Platform { get; }

        public FileFamilyFaults Faults { get; }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public OsduFileProtocol Protocol { get; }

        public FileFamilyLedger Ledger() => new(() => Platform.Calls.Count);

        public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work) => (await Protocol.DeliverBatchAsync([work]))[0];

        public long VersionOf(string id) => Platform.Records[id]["version"]!.GetValue<long>();

        public void Dispose()
        {
            Runtime.Dispose();
            Faults.Dispose();
        }
    }

    private static DeliveryWork Work(IPayloadSource? files, bool metadata = true, bool payload = true, long? existing = null, IReadOnlyDictionary<string, string>? state = null, string id = LogId)
        => new()
        {
            Key = DeliveryKey.Derive("atomic-file-route", [id]),
            TargetId = id,
            Document = FakeOsduPlatform.Record(id, LogKind),
            DeliverMetadata = metadata,
            DeliverPayload = payload,
            Payload = files,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    private static MemoryFiles Files(int count)
        => new(Enumerable.Range(0, count).Select(i => ("run_" + i.ToString(CultureInfo.InvariantCulture) + ".las", "~A " + i.ToString(CultureInfo.InvariantCulture))).ToArray());

    private static string Landing(int n) => "/staging/landing/l-" + n.ToString(CultureInfo.InvariantCulture);

    private static string Dataset(int n) => Minted + n.ToString(CultureInfo.InvariantCulture);

    private static string Delete(string id) => StorageRecords + "/" + id + ":delete";

    /// <summary>The slot the route keeps the registration of file <paramref name="index"/> landed at <paramref name="source"/> under.</summary>
    private static string SlotOf(int index, string source) => FileUploads.DatasetSlot(new UploadedFile(index, "file", 1, source, null));

    private static int IndexOf(FakeOsduPlatform platform, HttpMethod method, string path, string? body = null, int from = 0)
    {
        var at = platform.Calls.Skip(from).ToList()
            .FindIndex(c => c.Method == method && Uri.UnescapeDataString(c.Uri.AbsolutePath) == path && (body is null || (c.Body?.Contains(body, StringComparison.Ordinal) ?? false)));
        return at < 0 ? at : at + from;
    }

    private static List<string> Since(FakeOsduPlatform platform, int from)
        => platform.Calls.Skip(from).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.AbsolutePath)).ToList();

    private static string[] DatasetsOf(JsonObject record)
        => record["data"]!["Datasets"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    /// <summary>A record storage holds already, naming <paramref name="datasets"/>, as an earlier delivery left it; its version.</summary>
    private static long Earlier(FakeOsduPlatform platform, params string[] datasets)
    {
        foreach (var dataset in datasets)
        {
            platform.Put(FakeOsduPlatform.Record(dataset, "osdu:wks:dataset--File.Generic:1.0.0"));
        }

        var earlier = FakeOsduPlatform.Record(LogId, LogKind);
        earlier["data"]!["Datasets"] = new JsonArray(datasets.Select(d => (JsonNode?)JsonValue.Create(d + ":")).ToArray());
        return platform.Put(earlier);
    }

    private static Dictionary<string, string> StateNaming(params string[] datasets)
        => new(StringComparer.Ordinal) { [FileUploads.DatasetIdsValue] = string.Join(",", datasets) };

    [Fact]
    public async Task Each_registration_is_declared_by_where_its_file_landed_before_its_call_and_completed_with_the_dataset_minted_for_it()
    {
        using var rig = new Rig();
        var ledger = rig.Ledger();
        var outcome = await rig.DeliverAsync(ledger.Track(Work(Files(2))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(
            ["upload-0", "upload-1", "register-0", "register-0", "register-1", "register-1", OsduFileProtocol.RecordIntentStep, "records", "records"],
            ledger.Reports.Select(r => r.Step));

        for (var i = 0; i < 2; i++)
        {
            // Both files land first (l-1, l-2), then each is registered (minted-3, minted-4).
            var source = Landing(i + 1);
            var slot = SlotOf(i, source);
            Assert.Matches("^file:" + i.ToString(CultureInfo.InvariantCulture) + ":[0-9a-f]{24}$", slot);
            var post = IndexOf(rig.Platform, HttpMethod.Post, Metadata, source);
            Assert.True(post >= 0, "no registration names " + source);

            // The intent is in the ledger before the call that mints the id goes; the id after its answer came.
            var intent = Assert.Single(ledger.Reports, r => r.Artifacts.Any(a => a.Slot == slot && a.Status == ArtifactStatus.Intent));
            var created = Assert.Single(ledger.Reports, r => r.Artifacts.Any(a => a.Slot == slot && a.Status == ArtifactStatus.Pending));
            Assert.True(intent.CallsBefore <= post, $"the intent of {slot} was reported after its registration was sent");
            Assert.True(created.CallsBefore > post, $"{slot} was reported created before its registration answered");
            Assert.Equal(FileUploads.RegisteringState, intent.Report.Returned["state"]);

            var declared = Assert.Single(intent.Artifacts);
            Assert.Equal(ArtifactRoles.Dataset, declared.Role);
            Assert.Null(declared.TargetId);
            Assert.Equal(source, declared.Locator);
            var minted = Assert.Single(created.Artifacts);
            Assert.Equal(ArtifactRoles.Dataset, minted.Role);
            Assert.Equal(Dataset(i + 3), minted.TargetId);
            Assert.Equal(source, minted.Locator);
            Assert.Null(minted.Version);
            Assert.Null(minted.PriorVersion);

            // As the ledger keeps it: one row, the intent completed by its id.
            var row = ledger.Slot(slot);
            Assert.Equal(ArtifactStatus.Pending, row.Status);
            Assert.Equal(Dataset(i + 3), row.TargetId);
            Assert.Equal(source, row.Locator);
        }

        // The record names the new datasets from its write on, so the write is declared before it goes and reported once it
        // answered, at the version it landed.
        var write = IndexOf(rig.Platform, HttpMethod.Put, StorageRecords);
        var declaredRecord = Assert.Single(ledger.Reports, r => r.Step == OsduFileProtocol.RecordIntentStep);
        Assert.True(declaredRecord.CallsBefore <= write, "the record's write was declared after it was sent");
        Assert.True(declaredRecord.CallsBefore > IndexOf(rig.Platform, HttpMethod.Post, Metadata, Landing(2)));
        var recordIntent = Assert.Single(declaredRecord.Artifacts);
        Assert.Equal(TargetArtifact.RecordSlot, recordIntent.Slot);
        Assert.Equal(ArtifactRoles.Record, recordIntent.Role);
        Assert.Equal(LogId, recordIntent.TargetId);
        Assert.Null(recordIntent.Version);
        Assert.Null(recordIntent.PriorVersion);
        Assert.Equal(ArtifactStatus.Intent, recordIntent.Status);
        var landed = Assert.Single(ledger.Reports, r => r.Step == "records" && r.Artifacts.Count > 0);
        Assert.True(landed.CallsBefore > write);
        var record = ledger.Slot(TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Pending, record.Status);
        Assert.Equal(ArtifactRoles.Record, record.Role);
        Assert.Equal(rig.VersionOf(LogId), record.Version);

        Assert.Equal(3, ledger.Rows.Count);
        Assert.Empty(outcome.Superseded);
        Assert.Equal([Dataset(3) + ":", Dataset(4) + ":"], DatasetsOf(rig.Platform.Records[LogId]));

        // The record itself goes when the unit commits; the datasets it minted stay, live.
        ledger.Commit();
        Assert.Equal(2, ledger.Rows.Count);
        Assert.All(ledger.Rows, r => Assert.Equal(ArtifactStatus.Live, r.Status));
    }

    [Fact]
    public async Task An_update_declares_its_record_write_as_a_version_over_the_one_the_ledger_held()
    {
        using var rig = new Rig();
        const string old = "dev:dataset--File.Generic:old";
        var version = Earlier(rig.Platform, old);
        var ledger = rig.Ledger();
        var outcome = await rig.DeliverAsync(ledger.Track(Work(Files(1), existing: version, state: StateNaming(old))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        var declared = Assert.Single(Assert.Single(ledger.Reports, r => r.Step == OsduFileProtocol.RecordIntentStep).Artifacts);
        Assert.Equal(ArtifactRoles.Version, declared.Role);
        Assert.Equal(version, declared.PriorVersion);
        Assert.Null(declared.Version);
        var record = ledger.Slot(TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactRoles.Version, record.Role);
        Assert.Equal(version, record.PriorVersion);
        Assert.Equal(rig.VersionOf(LogId), record.Version);
        Assert.Equal([old], outcome.Superseded);
    }

    [Fact]
    public async Task A_record_write_storage_refused_is_gone_at_the_undo_and_the_datasets_are_removed_after_it()
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(2)));
        var outcome = await rig.DeliverAsync(work);

        Assert.False(outcome.Succeeded);
        Assert.Contains("400", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LogId, rig.Platform.Records.Keys);
        Assert.Equal(ArtifactStatus.Intent, ledger.Slot(TargetArtifact.RecordSlot).Status);

        var undo = ledger.Undo(work);
        Assert.Equal(3, undo.Items.Count);
        var mark = rig.Platform.Calls.Count;
        var results = await rig.Protocol.UndoAsync([undo]);

        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        var record = FileFamilyLedger.For(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Gone, record.Outcome);
        Assert.Contains("OSDU no longer holds the record", record.Note, StringComparison.Ordinal);
        Assert.All(results.Where(r => r.Item.Artifact.Role == ArtifactRoles.Dataset), r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Contains(Dataset(3), FileFamilyLedger.For(results, SlotOf(0, Landing(1))).Note, StringComparison.Ordinal);
        Assert.Contains(Dataset(3), rig.Platform.Removed);
        Assert.Contains(Dataset(4), rig.Platform.Removed);

        // The record is asked about first; then each landing path is looked up, and every dataset found there removed in one
        // bulk call. The files stay in the landing zone, which no call removes.
        Assert.Equal(
            [
                "GET " + StorageRecords + "/" + LogId,
                "POST " + Search, "POST " + Search, "POST " + StorageRecords + "/delete",
            ],
            Since(rig.Platform, mark));
        Assert.Equal([Dataset(3), Dataset(4)], JsonNode.Parse(rig.Platform.Calls[^1].Body!)!.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal));
        Assert.True(rig.Platform.Staged.ContainsKey(Landing(1)));
        Assert.True(rig.Platform.Staged.ContainsKey(Landing(2)));
    }

    [Theory]
    [InlineData(false, FileFamilyLoss.Transport)]
    [InlineData(false, FileFamilyLoss.Timeout)]
    [InlineData(true, FileFamilyLoss.ServerError)]
    public async Task A_record_write_that_landed_without_its_answer_is_taken_back_first_and_its_new_datasets_after_it(bool update, FileFamilyLoss loss)
    {
        using var rig = new Rig();
        const string old = "dev:dataset--File.Generic:old";
        long? prior = update ? Earlier(rig.Platform, old) : null;
        rig.Faults.LoseAnswer(HttpMethod.Put, StorageRecords, loss);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(1), existing: prior, state: update ? StateNaming(old) : null));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        // The write landed: OSDU serves the record naming the new dataset, and the ledger holds the write's intent.
        var minted = Dataset(2);
        Assert.Equal([minted + ":"], DatasetsOf(rig.Platform.Records[LogId]));
        var row = ledger.Slot(TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Intent, row.Status);
        Assert.Null(row.Version);

        var mark = rig.Platform.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        var record = FileFamilyLedger.For(results, TargetArtifact.RecordSlot);
        var dataset = FileFamilyLedger.For(results, SlotOf(0, Landing(1)));
        Assert.Equal(ArtifactStatus.Removed, dataset.Outcome);
        Assert.Contains(minted, rig.Platform.Removed);
        if (update)
        {
            Assert.Equal(ArtifactStatus.Restored, record.Outcome);
            Assert.Contains($"version {prior!.Value.ToString(CultureInfo.InvariantCulture)} written back", record.Note, StringComparison.Ordinal);
            Assert.Equal([old + ":"], DatasetsOf(rig.Platform.Records[LogId]));
            Assert.DoesNotContain(old, rig.Platform.Removed);
            Assert.DoesNotContain(LogId, rig.Platform.Removed);
        }
        else
        {
            Assert.Equal(ArtifactStatus.Removed, record.Outcome);
            Assert.Contains(LogId, rig.Platform.Removed);
        }

        // The record goes back before the dataset it names is removed.
        var recordCall = update ? IndexOf(rig.Platform, HttpMethod.Put, StorageRecords, from: mark) : IndexOf(rig.Platform, HttpMethod.Post, Delete(LogId), from: mark);
        var datasetCall = IndexOf(rig.Platform, HttpMethod.Post, Delete(minted), from: mark);
        Assert.True(recordCall >= 0 && datasetCall > recordCall, "a dataset was removed before the record that names it");
    }

    [Fact]
    public async Task Datasets_wait_with_a_record_that_cannot_be_taken_back_yet_and_go_with_it_on_the_next_undo()
    {
        using var rig = new Rig();
        rig.Faults.LoseAnswer(HttpMethod.Put, StorageRecords);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(2)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        rig.Faults.Refuse(HttpMethod.Post, Delete(LogId), HttpStatusCode.ServiceUnavailable);
        var undo = ledger.Undo(work);
        var waiting = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], waiting);
        Assert.Equal(ArtifactStatus.Failed, FileFamilyLedger.For(waiting, TargetArtifact.RecordSlot).Outcome);
        Assert.All(waiting.Where(r => r.Item.Artifact.Role == ArtifactRoles.Dataset), r =>
        {
            Assert.Equal(ArtifactStatus.Failed, r.Outcome);
            Assert.StartsWith("the record could not be taken back yet", r.Note, StringComparison.Ordinal);
        });
        Assert.Empty(rig.Platform.Removed);

        ledger.Settle(waiting);
        var retry = ledger.Undo(work);
        Assert.Equal(3, retry.Items.Count);
        var settled = await rig.Protocol.UndoAsync([retry]);
        Assert.All(settled, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Contains(LogId, rig.Platform.Removed);
        Assert.Contains(Dataset(3), rig.Platform.Removed);
        Assert.Contains(Dataset(4), rig.Platform.Removed);
    }

    [Fact]
    public async Task Newer_work_keeps_the_record_and_the_datasets_of_the_abandoned_unit_are_still_removed()
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(1)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        var undo = ledger.Undo(work, UndoReason.Abandoned, keepRecord: true);
        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        Assert.Equal(ArtifactStatus.Superseded, FileFamilyLedger.For(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal(ArtifactStatus.Removed, FileFamilyLedger.For(results, SlotOf(0, Landing(1))).Outcome);
        Assert.Contains(Dataset(2), rig.Platform.Removed);
    }

    [Theory]
    [InlineData(FileFamilyLoss.Transport)]
    [InlineData(FileFamilyLoss.ServerError)]
    [InlineData(FileFamilyLoss.Timeout)]
    public async Task A_registration_whose_answer_was_lost_is_found_by_where_its_file_landed_and_removed(FileFamilyLoss loss)
    {
        using var rig = new Rig();
        rig.Faults.LoseAnswer(HttpMethod.Post, Metadata, loss);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(1)));
        var outcome = await rig.DeliverAsync(work);

        Assert.False(outcome.Succeeded);
        Assert.IsAssignableFrom<DeliveryException>(outcome.Failure);

        // The service minted the dataset; the ledger knows only where its file landed, and the record was never written.
        Assert.Contains(Dataset(2), rig.Platform.Records.Keys);
        var slot = SlotOf(0, Landing(1));
        var row = Assert.Single(ledger.Rows);
        Assert.Equal(slot, row.Slot);
        Assert.Equal(ArtifactStatus.Intent, row.Status);
        Assert.Null(row.TargetId);
        Assert.Equal(Landing(1), row.Locator);
        Assert.DoesNotContain(rig.Platform.Calls, c => c.Method == HttpMethod.Put && c.Uri.AbsolutePath == StorageRecords);

        var undo = ledger.Undo(work);
        var mark = rig.Platform.Calls.Count;
        var result = Assert.Single(await rig.Protocol.UndoAsync([undo]));
        Assert.Equal(ArtifactStatus.Removed, result.Outcome);
        Assert.Contains("registered as " + Dataset(2), result.Note, StringComparison.Ordinal);
        Assert.Contains(Dataset(2), rig.Platform.Removed);
        Assert.Equal(["POST " + Search, "POST " + Delete(Dataset(2))], Since(rig.Platform, mark));
        var query = JsonNode.Parse(rig.Platform.Calls[mark].Body!)!.AsObject();
        Assert.Contains("\"" + Landing(1) + "\"", query["query"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("osdu:wks:dataset--File.Generic:1.0.0", query["kind"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_registration_the_service_refused_fails_the_record_unwritten_and_its_intent_is_gone_at_the_undo(HttpStatusCode status)
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Post, Metadata, status);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(1)));
        var outcome = await rig.DeliverAsync(work);

        Assert.False(outcome.Succeeded);
        var refused = Assert.IsType<OsduStatusException>(outcome.Failure);
        Assert.Equal((int)status, refused.StatusCode);
        Assert.DoesNotContain(rig.Platform.Calls, c => c.Uri.AbsolutePath == StorageRecords);
        Assert.DoesNotContain(rig.Platform.Records.Keys, k => k.StartsWith(Minted, StringComparison.Ordinal));
        Assert.Equal(ArtifactStatus.Intent, Assert.Single(ledger.Rows).Status);

        var mark = rig.Platform.Calls.Count;
        var result = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));
        Assert.Equal(ArtifactStatus.Gone, result.Outcome);
        Assert.Contains("did not land", result.Note, StringComparison.Ordinal);
        Assert.Equal(["POST " + Search], Since(rig.Platform, mark));
        Assert.Empty(rig.Platform.Removed);
    }

    [Fact]
    public async Task A_resumed_try_takes_over_the_registration_whose_answer_was_lost_without_uploading_or_registering_again()
    {
        using var rig = new Rig();
        rig.Faults.LoseAnswer(HttpMethod.Post, Metadata);
        var ledger = rig.Ledger();
        Assert.False((await rig.DeliverAsync(ledger.Track(Work(Files(1))))).Succeeded);

        var mark = rig.Platform.Calls.Count;
        var resumed = await rig.DeliverAsync(ledger.Resume(Work(Files(1))));
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Equal(Dataset(2), resumed.Returned[FileUploads.DatasetIdsValue]);
        Assert.Equal(["POST " + Search, "PUT " + StorageRecords], Since(rig.Platform, mark));
        Assert.Equal(["upload-0", "register-0", "records"], resumed.Steps.Select(s => s.Name));
        Assert.True(resumed.Steps[0].Resumed);
        Assert.True(resumed.Steps[1].Resumed);

        // The take-over completes the intent's one row with the dataset it found.
        var adopted = Assert.Single(ledger.Reports, r => r.Step == "register-0" && r.Report.Returned.ContainsKey("adopted"));
        var artifact = Assert.Single(adopted.Artifacts);
        Assert.Equal(ArtifactStatus.Pending, artifact.Status);
        Assert.Equal(Dataset(2), artifact.TargetId);
        var row = ledger.Slot(SlotOf(0, Landing(1)));
        Assert.Equal(ArtifactStatus.Pending, row.Status);
        Assert.Equal(Dataset(2), row.TargetId);
        Assert.Equal(Landing(1), row.Locator);
        Assert.Equal(ArtifactStatus.Pending, ledger.Slot(TargetArtifact.RecordSlot).Status);
        Assert.Equal([Dataset(2) + ":"], DatasetsOf(rig.Platform.Records[LogId]));
    }

    [Fact]
    public async Task A_resumed_try_sends_nothing_an_earlier_try_uploaded_and_registered_and_declares_only_the_record_again()
    {
        using var rig = new Rig();
        var ledger = rig.Ledger();
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.ServiceUnavailable);
        var work = ledger.Track(Work(Files(2)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var reported = ledger.Reports.Count;

        var mark = rig.Platform.Calls.Count;
        var resumed = await rig.DeliverAsync(ledger.Resume(Work(Files(2))));
        Assert.True(resumed.Succeeded, resumed.Failure?.Message);
        Assert.Equal(["PUT " + StorageRecords], Since(rig.Platform, mark));
        Assert.All(resumed.Steps.Where(s => s.Name != "records"), s => Assert.True(s.Resumed, s.Name));

        // Only the record's write is declared again, under its one slot; no dataset is reported again.
        Assert.Equal([OsduFileProtocol.RecordIntentStep, "records", "records"], ledger.Reports.Skip(reported).Select(r => r.Step));
        Assert.All(ledger.Reports.Skip(reported).SelectMany(r => r.Artifacts), a => Assert.Equal(TargetArtifact.RecordSlot, a.Slot));
        Assert.Equal(3, ledger.Rows.Count);
        Assert.Equal(ArtifactStatus.Pending, ledger.Slot(TargetArtifact.RecordSlot).Status);
        Assert.Equal($"{Dataset(3)},{Dataset(4)}", resumed.Returned[FileUploads.DatasetIdsValue]);
    }

    [Fact]
    public async Task Datasets_a_lost_answer_let_a_later_try_register_twice_are_kept_once_and_the_rest_removed_by_the_route()
    {
        using var rig = new Rig();
        rig.Faults.LoseAnswer(HttpMethod.Post, Metadata, times: 2);
        var ledger = rig.Ledger();
        Assert.False((await rig.DeliverAsync(ledger.Track(Work(Files(1))))).Succeeded);

        // The index has not listed the first registration yet, so the second try registers the same landed file again, and
        // loses that answer too.
        rig.Platform.Search = (_, _) => [];
        Assert.False((await rig.DeliverAsync(ledger.Resume(Work(Files(1))))).Succeeded);
        Assert.Contains(Dataset(2), rig.Platform.Records.Keys);
        Assert.Contains(Dataset(3), rig.Platform.Records.Keys);

        // The third try finds both: it keeps the first in id order and removes the other reversibly itself.
        FileFamilyFaults.IndexLive(rig.Platform);
        var third = await rig.DeliverAsync(ledger.Resume(Work(Files(1))));
        Assert.True(third.Succeeded, third.Failure?.Message);
        Assert.Equal(Dataset(2), third.Returned[FileUploads.DatasetIdsValue]);
        Assert.Equal([Dataset(2) + ":"], DatasetsOf(rig.Platform.Records[LogId]));
        Assert.Contains(Dataset(3), rig.Platform.Removed);
        Assert.DoesNotContain(Dataset(2), rig.Platform.Removed);
        Assert.Equal(Dataset(3), third.Steps.Single(s => s.Name == "register-0").Returned["removedDuplicates"]);

        var slot = SlotOf(0, Landing(1));
        var kept = ledger.Slot(slot);
        Assert.Equal(ArtifactStatus.Pending, kept.Status);
        Assert.Equal(Dataset(2), kept.TargetId);
        var extra = ledger.Slot(slot + ":" + Dataset(3));
        Assert.Equal(ArtifactStatus.Removed, extra.Status);
        Assert.Equal(Dataset(3), extra.TargetId);
        Assert.Equal(Landing(1), extra.Locator);
        Assert.Contains("a second registration of run_0.las", extra.Note, StringComparison.Ordinal);

        // A dataset the route removed itself is no longer the undo's: what is left is the dataset kept and the record.
        var open = ledger.Undo(Work(Files(1))).Items.Select(i => i.Artifact.Slot).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { slot, TargetArtifact.RecordSlot }.Order(StringComparer.Ordinal), open);
    }

    [Fact]
    public async Task An_extra_registration_whose_removal_failed_is_due_after_its_unit_commits_and_the_sweep_removes_it_by_id()
    {
        using var rig = new Rig();
        rig.Faults.LoseAnswer(HttpMethod.Post, Metadata, times: 2);
        var ledger = rig.Ledger();
        Assert.False((await rig.DeliverAsync(ledger.Track(Work(Files(1))))).Succeeded);
        rig.Platform.Search = (_, _) => [];
        Assert.False((await rig.DeliverAsync(ledger.Resume(Work(Files(1))))).Succeeded);
        FileFamilyFaults.IndexLive(rig.Platform);
        rig.Faults.Refuse(HttpMethod.Post, Delete(Dataset(3)), HttpStatusCode.InternalServerError);

        var third = await rig.DeliverAsync(ledger.Resume(Work(Files(1))));
        Assert.True(third.Succeeded, third.Failure?.Message);
        Assert.DoesNotContain(Dataset(3), rig.Platform.Removed);
        var extraSlot = SlotOf(0, Landing(1)) + ":" + Dataset(3);
        var extra = ledger.Slot(extraSlot);
        Assert.Equal(ArtifactStatus.Intent, extra.Status);
        Assert.Equal(Dataset(3), extra.TargetId);
        Assert.Null(extra.Locator);
        Assert.Contains("could not be removed yet", extra.Note, StringComparison.Ordinal);

        // The record committed naming the dataset it kept. The duplicate nothing names does not go live with it: the unit's end
        // leaves it due, as it does an intent the delivery completed without.
        ledger.Commit();
        Assert.Equal(ArtifactStatus.Live, ledger.Slot(SlotOf(0, Landing(1))).Status);
        Assert.Equal(ArtifactStatus.Due, ledger.Slot(extraSlot).Status);

        // The sweep removes it by its id alone: a search by its landing path would find the dataset the record keeps.
        var sweep = ledger.Undo(Work(Files(1)));
        Assert.Equal(extraSlot, Assert.Single(sweep.Items).Artifact.Slot);
        var mark = rig.Platform.Calls.Count;
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(await rig.Protocol.UndoAsync([sweep])).Outcome);
        Assert.Equal(["POST " + Delete(Dataset(3))], Since(rig.Platform, mark));
        Assert.Contains(Dataset(3), rig.Platform.Removed);
        Assert.DoesNotContain(Dataset(2), rig.Platform.Removed);
    }

    [Fact]
    public async Task The_datasets_of_many_records_are_removed_in_one_storage_call_and_each_record_answered_for_its_own()
    {
        // What the committed deliveries of three records minted, as a removal hands them over once the records are out of
        // OSDU: two known by their id alone, and one by where its file landed, whose registration the index lists.
        using var rig = new Rig();
        for (var n = 1; n <= 4; n++)
        {
            var dataset = FakeOsduPlatform.Record(Dataset(n), "osdu:wks:dataset--File.Generic:1.0.0");
            dataset["data"]!["DatasetProperties"] = new JsonObject { ["FileSourceInfo"] = new JsonObject { ["FileSource"] = Landing(n) } };
            rig.Platform.Put(dataset);
        }

        var started = DateTime.UtcNow;
        UndoWork Minted(int record, params TargetArtifact[] artifacts) => new()
        {
            Key = DeliveryKey.Derive("atomic-file-route", [record.ToString(CultureInfo.InvariantCulture)]),
            TargetId = LogId + record.ToString(CultureInfo.InvariantCulture),
            Reason = UndoReason.Removed,
            Items = artifacts.Select((a, i) => new UndoItem((record * 10) + i, a, Guid.NewGuid(), started)).ToList(),
        };
        TargetArtifact Known(int n) => new() { Slot = "dataset:" + n.ToString(CultureInfo.InvariantCulture), Role = ArtifactRoles.Dataset, TargetId = Dataset(n), Status = ArtifactStatus.Live };
        var works = new[]
        {
            Minted(1, Known(1), Known(2)),
            Minted(2, Known(4)),
            Minted(3, new TargetArtifact { Slot = "dataset:0", Role = ArtifactRoles.Dataset, Locator = Landing(3), Status = ArtifactStatus.Live }),
        };

        var mark = rig.Platform.Calls.Count;
        var results = await rig.Protocol.UndoAsync(works);

        // The index is asked once for the path, and every dataset of the three records goes in one bulk removal.
        Assert.Equal(["POST " + Search, "POST " + StorageRecords + "/delete"], Since(rig.Platform, mark));
        var sent = JsonNode.Parse(rig.Platform.Calls[^1].Body!)!.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal);
        Assert.Equal([Dataset(1), Dataset(2), Dataset(3), Dataset(4)], sent);
        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Equal(works.SelectMany(w => w.Items).Select(i => i.ArtifactId).Order(), results.Select(r => r.Item.ArtifactId).Order());
        Assert.Contains("registered as " + Dataset(3), results.Single(r => r.Item.Artifact.Locator == Landing(3)).Note, StringComparison.Ordinal);
        Assert.Equal([Dataset(1), Dataset(2), Dataset(3), Dataset(4)], rig.Platform.Removed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_registration_sent_again_after_the_index_listed_nothing_is_undone_with_the_one_whose_answer_was_lost()
    {
        using var rig = new Rig();
        rig.Faults.LoseAnswer(HttpMethod.Post, Metadata);
        var ledger = rig.Ledger();
        Assert.False((await rig.DeliverAsync(ledger.Track(Work(Files(1))))).Succeeded);

        // The index has not caught up, so the next try registers the landed file again; then storage refuses the record.
        rig.Platform.Search = (_, _) => [];
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var work = ledger.Resume(Work(Files(1)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        Assert.Contains(Dataset(2), rig.Platform.Records.Keys);
        Assert.Contains(Dataset(3), rig.Platform.Records.Keys);
        Assert.Equal(Dataset(3), ledger.Slot(SlotOf(0, Landing(1))).TargetId);

        // By the undo the index lists both registrations of the one landed file. Both are the unit's, and nothing names either.
        FileFamilyFaults.IndexLive(rig.Platform);
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        var dataset = FileFamilyLedger.For(results, SlotOf(0, Landing(1)));
        Assert.Equal(ArtifactStatus.Removed, dataset.Outcome);
        Assert.Contains($"registered as {Dataset(2)}, {Dataset(3)}", dataset.Note, StringComparison.Ordinal);
        Assert.Contains(Dataset(3), rig.Platform.Removed);
        Assert.Contains(Dataset(2), rig.Platform.Removed);
    }

    [Fact]
    public async Task A_payload_change_names_the_datasets_of_the_earlier_delivery_superseded_and_a_change_to_the_record_alone_none()
    {
        using var rig = new Rig();
        const string oldA = "dev:dataset--File.Generic:old-a";
        const string oldB = "dev:dataset--File.Generic:old-b";
        var version = Earlier(rig.Platform, oldA, oldB);

        var changed = await rig.DeliverAsync(rig.Ledger().Track(Work(Files(1), existing: version, state: StateNaming(oldA, oldB))));
        Assert.True(changed.Succeeded, changed.Failure?.Message);
        Assert.Equal([oldA, oldB], changed.Superseded);
        Assert.Equal(Dataset(2), changed.Returned[FileUploads.DatasetIdsValue]);

        // A change to the record alone registers nothing, so its one write is not declared: nothing of the unit stands before it.
        var ledger = rig.Ledger();
        var renamed = await rig.DeliverAsync(ledger.Track(Work(null, payload: false, existing: changed.TargetVersion, state: changed.Returned)));
        Assert.True(renamed.Succeeded, renamed.Failure?.Message);
        Assert.Empty(renamed.Superseded);
        Assert.Empty(ledger.Rows);
        Assert.DoesNotContain(ledger.Reports, r => r.Step == OsduFileProtocol.RecordIntentStep);
        Assert.Equal([Dataset(2) + ":"], DatasetsOf(rig.Platform.Records[LogId]));

        // A payload change that does not complete supersedes nothing: the record still names the earlier datasets.
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var refused = await rig.DeliverAsync(rig.Ledger().Track(Work(Files(1), existing: renamed.TargetVersion, state: renamed.Returned)));
        Assert.False(refused.Succeeded);
        Assert.Empty(refused.Superseded);
    }

    [Fact]
    public async Task An_update_storage_refuses_keeps_the_record_on_the_datasets_of_its_earlier_delivery()
    {
        using var rig = new Rig();
        const string old = "dev:dataset--File.Generic:old";
        var version = Earlier(rig.Platform, old);

        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(1), existing: version, state: StateNaming(old)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        var mark = rig.Platform.Calls.Count;
        var undo = ledger.Undo(work);
        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);

        // The write never landed: OSDU's latest version is still the one it would have replaced, so nothing is written back.
        var record = FileFamilyLedger.For(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Gone, record.Outcome);
        Assert.Contains($"OSDU's latest version is still {version.ToString(CultureInfo.InvariantCulture)}", record.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Platform.Calls.Skip(mark), c => c.Method == HttpMethod.Put && c.Uri.AbsolutePath == StorageRecords);
        Assert.Equal(ArtifactStatus.Removed, FileFamilyLedger.For(results, SlotOf(0, Landing(1))).Outcome);
        Assert.Contains(Dataset(2), rig.Platform.Removed);
        Assert.DoesNotContain(old, rig.Platform.Removed);
        Assert.Equal(version, rig.VersionOf(LogId));
        Assert.Equal([old + ":"], DatasetsOf(rig.Platform.Records[LogId]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_second_undo_of_what_the_first_took_back_answers_gone_and_changes_nothing(bool lostAnswer)
    {
        using var rig = new Rig();
        if (lostAnswer)
        {
            rig.Faults.LoseAnswer(HttpMethod.Post, Metadata);
        }
        else
        {
            rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        }

        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(1)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var undo = ledger.Undo(work);
        var slot = SlotOf(0, Landing(1));
        Assert.Equal(ArtifactStatus.Removed, FileFamilyLedger.For(await rig.Protocol.UndoAsync([undo]), slot).Outcome);
        var removed = rig.Platform.Removed.ToHashSet(StringComparer.Ordinal);
        var records = rig.Platform.Records.Count;

        var again = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], again);
        Assert.All(again, r => Assert.Equal(ArtifactStatus.Gone, r.Outcome));
        Assert.Equal(removed, rig.Platform.Removed);
        Assert.Equal(records, rig.Platform.Records.Count);
    }

    [Fact]
    public async Task A_second_undo_of_several_datasets_settles_each_again_and_changes_nothing()
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(3)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var undo = ledger.Undo(work);
        var first = await rig.Protocol.UndoAsync([undo]);
        Assert.All(first.Where(r => r.Item.Artifact.Role == ArtifactRoles.Dataset), r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        var removed = rig.Platform.Removed.ToHashSet(StringComparer.Ordinal);

        // Each dataset is looked up by its landing path, which the index no longer lists, and removed by its id in one bulk call,
        // which marks a dataset storage already soft-deleted deleted again (osdu/specs/core/INTEGRATION.md, D2): each is
        // settled, none fails, and storage holds what it held.
        var mark = rig.Platform.Calls.Count;
        var again = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], again);
        Assert.All(again, r => Assert.Contains(r.Outcome, new[] { ArtifactStatus.Removed, ArtifactStatus.Gone }));
        Assert.Equal(1, Since(rig.Platform, mark).Count(c => c == "POST " + StorageRecords + "/delete"));
        Assert.Equal(removed, rig.Platform.Removed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_undo_call_that_fails_answers_failed_for_exactly_its_items_while_the_others_still_settle(bool searchFails)
    {
        using var rig = new Rig();

        // Two registrations land and answer; the third lands and loses its answer, so the record is never written.
        rig.Faults.Lose(FileFamilyFaults.Nth(HttpMethod.Post, Metadata, 3), FileFamilyLoss.Transport);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(3)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var undo = ledger.Undo(work);
        Assert.Equal(3, undo.Items.Count);
        var intentSlot = SlotOf(2, Landing(3));
        var failingSlot = searchFails ? intentSlot : SlotOf(0, Landing(1));
        Assert.Null(ledger.Slot(intentSlot).TargetId);

        if (searchFails)
        {
            // The landing paths are looked up in order: the third search is the lost registration's.
            rig.Faults.Refuse(FileFamilyFaults.Nth(HttpMethod.Post, Search, 3), HttpStatusCode.ServiceUnavailable);
        }
        else
        {
            // The datasets go in one bulk removal: that is the call that fails.
            rig.Faults.Refuse(HttpMethod.Post, StorageRecords + "/delete", HttpStatusCode.ServiceUnavailable);
        }

        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        var failed = FileFamilyLedger.For(results, failingSlot);
        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        if (searchFails)
        {
            Assert.All(results.Where(r => r.Item.Artifact.Slot != failingSlot), r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
            Assert.Contains("the index could not be asked which dataset " + Landing(3) + " became", failed.Note, StringComparison.Ordinal);
            Assert.DoesNotContain(Dataset(6), rig.Platform.Removed);
            Assert.Contains(Dataset(4), rig.Platform.Removed);
            Assert.Contains(Dataset(5), rig.Platform.Removed);
        }
        else
        {
            // Every dataset the failed call carried is failed with what storage said, and nothing was removed; the sweep tries
            // each again.
            Assert.All(results, r => Assert.Equal(ArtifactStatus.Failed, r.Outcome));
            Assert.All(results, r => Assert.Contains("503", r.Note, StringComparison.Ordinal));
            Assert.Empty(rig.Platform.Removed);
        }
    }

    [Fact]
    public async Task One_dataset_whose_soft_delete_storage_refuses_fails_alone_and_the_intent_beside_it_is_removed()
    {
        using var rig = new Rig();
        rig.Faults.Lose(FileFamilyFaults.Nth(HttpMethod.Post, Metadata, 2), FileFamilyLoss.ServerError);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(2)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var undo = ledger.Undo(work);
        Assert.Equal(2, undo.Items.Count);

        // Storage keeps the one dataset it will not remove for the caller and names it in the bulk call's 207; the route asks
        // about that one alone, and storage refuses it with 403.
        rig.Platform.RefusedDeletes.Add(Dataset(3));
        var results = await rig.Protocol.UndoAsync([undo]);
        FileFamilyLedger.AssertAnsweredOnce([undo], results);
        var refused = FileFamilyLedger.For(results, SlotOf(0, Landing(1)));
        Assert.Equal(ArtifactStatus.Failed, refused.Outcome);
        Assert.Contains("403", refused.Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, FileFamilyLedger.For(results, SlotOf(1, Landing(2))).Outcome);
        Assert.DoesNotContain(Dataset(3), rig.Platform.Removed);
        Assert.Contains(Dataset(4), rig.Platform.Removed);

        // Settled as the ledger settles it, the next undo takes only what failed, and removes it once storage takes it.
        rig.Platform.RefusedDeletes.Clear();
        ledger.Settle(results);
        var retry = ledger.Undo(work);
        var item = Assert.Single(retry.Items);
        Assert.Equal(Dataset(3), item.Artifact.TargetId);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(await rig.Protocol.UndoAsync([retry])).Outcome);
        Assert.Contains(Dataset(3), rig.Platform.Removed);
    }

    [Fact]
    public async Task An_intent_the_index_finds_under_two_datasets_is_removed_whole_or_failed_whole()
    {
        using var rig = new Rig();
        var ledger = rig.Ledger();
        rig.Faults.Refuse(HttpMethod.Post, Metadata, HttpStatusCode.BadGateway);
        var work = ledger.Track(Work(Files(1)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        // Two registrations of the one landed file reached the service (an earlier answer lost, the file registered again).
        foreach (var n in new[] { 7, 8 })
        {
            var twin = FakeOsduPlatform.Record(Dataset(n), "osdu:wks:dataset--File.Generic:1.0.0");
            twin["data"]!["DatasetProperties"] = new JsonObject { ["FileSourceInfo"] = new JsonObject { ["FileSource"] = Landing(1) } };
            rig.Platform.Put(twin);
        }

        var undo = ledger.Undo(work);
        rig.Faults.Refuse(HttpMethod.Post, StorageRecords + "/delete", HttpStatusCode.ServiceUnavailable);
        var failed = Assert.Single(await rig.Protocol.UndoAsync([undo]));
        Assert.Equal(ArtifactStatus.Failed, failed.Outcome);
        Assert.Contains($"registered as {Dataset(7)}, {Dataset(8)}", failed.Note, StringComparison.Ordinal);
        Assert.Empty(rig.Platform.Removed);

        var removed = Assert.Single(await rig.Protocol.UndoAsync([undo]));
        Assert.Equal(ArtifactStatus.Removed, removed.Outcome);
        Assert.Contains($"registered as {Dataset(7)}, {Dataset(8)}, removed (reversible)", removed.Note, StringComparison.Ordinal);
        Assert.Contains(Dataset(7), rig.Platform.Removed);
        Assert.Contains(Dataset(8), rig.Platform.Removed);
    }

    [Fact]
    public async Task A_bulk_soft_delete_storage_partly_refuses_counts_what_it_deleted_as_removed()
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(1)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        // A second registration of the landed file is listed beside the one the ledger names, which was purged meanwhile. The
        // bulk soft delete of the two deletes the live one and answers 207 naming the other (osdu/specs/core/INTEGRATION.md, D2:
        // "the others are still deleted").
        var twin = FakeOsduPlatform.Record(Dataset(9), "osdu:wks:dataset--File.Generic:1.0.0");
        twin["data"]!["DatasetProperties"] = new JsonObject { ["FileSourceInfo"] = new JsonObject { ["FileSource"] = Landing(1) } };
        rig.Platform.Put(twin);
        rig.Platform.Records.Remove(Dataset(2));
        rig.Faults.Answer(FileFamilyFaults.On(HttpMethod.Post, StorageRecords + "/delete"), async (request, ct) =>
        {
            var ids = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            var missing = ids.Where(id => !rig.Platform.Records.ContainsKey(id) || rig.Platform.Removed.Contains(id)).ToList();
            foreach (var id in ids.Except(missing))
            {
                rig.Platform.Removed.Add(id);
            }

            var body = new JsonArray(missing.Select(id => (JsonNode?)new JsonObject { ["notDeletedRecordId"] = id, ["message"] = $"Record with id '{id}' not found" }).ToArray());
            return new HttpResponseMessage(HttpStatusCode.MultiStatus) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        });

        var mark = rig.Platform.Calls.Count;
        var results = await rig.Protocol.UndoAsync([ledger.Undo(work)]);
        var dataset = FileFamilyLedger.For(results, SlotOf(0, Landing(1)));
        Assert.Equal(ArtifactStatus.Removed, dataset.Outcome);
        Assert.Contains($"registered as {Dataset(9)}, {Dataset(2)}, removed (reversible)", dataset.Note, StringComparison.Ordinal);
        Assert.Contains(Dataset(9), rig.Platform.Removed);

        // Only the id the answer named is asked about again.
        Assert.Equal(1, Since(rig.Platform, mark).Count(c => c == "POST " + Delete(Dataset(2))));
        Assert.DoesNotContain("POST " + Delete(Dataset(9)), Since(rig.Platform, mark));
    }

    [Fact]
    public async Task The_undo_answers_every_item_of_every_record_once_and_keeps_what_the_file_route_never_makes()
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Files(1)));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var first = ledger.Undo(work);

        var unit = Guid.NewGuid();
        var started = DateTime.UtcNow;
        var other = new UndoWork
        {
            Key = DeliveryKey.Derive("atomic-file-route", ["other"]),
            TargetId = "dev:work-product-component--WellLog:other",
            Reason = UndoReason.Failed,
            Items =
            [
                new UndoItem(1_000_001, TargetArtifact.Created("session", ArtifactRoles.Session, "session-1"), unit, started),
                new UndoItem(1_000_002, TargetArtifact.RecordWritten("dev:work-product-component--WellLog:other", 5, null), unit, started),
                new UndoItem(1_000_003, new TargetArtifact { Slot = "file:0:unfindable", Role = ArtifactRoles.Dataset, Status = ArtifactStatus.Intent }, unit, started),
                new UndoItem(1_000_004, TargetArtifact.Created("file:1:never", ArtifactRoles.Dataset, "dev:dataset--File.Generic:never-held"), unit, started),
            ],
        };

        var results = await rig.Protocol.UndoAsync([first, other]);
        FileFamilyLedger.AssertAnsweredOnce([first, other], results);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(results, r => r.Item.UnitId == ledger.Unit.Id && r.Item.Artifact.Role == ArtifactRoles.Dataset).Outcome);
        Assert.Equal(ArtifactStatus.Gone, Assert.Single(results, r => r.Item.UnitId == ledger.Unit.Id && r.Item.Artifact.Role == ArtifactRoles.Record).Outcome);
        var byId = results.Where(r => r.Item.UnitId == unit).ToDictionary(r => r.Item.ArtifactId);
        Assert.Equal(ArtifactStatus.Kept, byId[1_000_001].Outcome);
        Assert.Equal("the file route creates nothing of this kind", byId[1_000_001].Note);
        Assert.Equal(ArtifactStatus.Gone, byId[1_000_002].Outcome);
        Assert.Contains("OSDU no longer holds the record", byId[1_000_002].Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Kept, byId[1_000_003].Outcome);
        Assert.Contains("names neither the dataset it made nor where its file landed", byId[1_000_003].Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Gone, byId[1_000_004].Outcome);
        Assert.DoesNotContain("dev:work-product-component--WellLog:other", rig.Platform.Removed);
    }

    [Fact]
    public async Task A_record_held_before_anything_is_sent_reports_nothing_and_leaves_nothing_to_undo()
    {
        using var rig = new Rig();
        var ledger = rig.Ledger();
        var outcome = await rig.DeliverAsync(ledger.Track(Work(new MemoryFiles(("run_0.las", "~A"), ("empty.las", "")))));

        Assert.False(outcome.Succeeded);
        Assert.IsType<RecordHeldException>(outcome.Failure);
        Assert.Empty(rig.Platform.Calls);
        Assert.Empty(ledger.Reports);
        Assert.Empty(ledger.Rows);
    }
}

/// <summary>What a route reported, and how many calls the platform had taken when the report came.</summary>
internal sealed record FileFamilyReport(StepReport Report, int CallsBefore)
{
    public string Step => Report.Step;

    public IReadOnlyList<TargetArtifact> Artifacts => Report.Artifacts;
}

/// <summary>
/// The worker's and the ledger's side of one record's unit of work, as the file, dataset, manifest and composed route tests
/// need it (osdu/docs/reference/concepts/artifacts-and-undo.md, Slot rules): every step a route reports, in order, with how
/// many calls the platform had taken when it came, so a test tells an intent from the call it goes before; the completed
/// steps a resumed try is given, kept as the worker keeps them (a step reported again replaces its values); and the
/// artifacts upserted by slot as the ledger upserts them (<c>SqlServerLedgerBulk.ArtifactUpsertSql</c>): the id, locator,
/// version and note a later report gives win, the version a write replaced is the one first reported, a slot first reported
/// as the record the unit created stays so, a slot the route settled itself (removed, kept or gone) reopens when the route
/// reports it again, and a slot an undo settled never changes. The unit's end (<c>UnitEndSql</c>) and an undo's settlements
/// (<c>ArtifactSettleSql</c>) move the rows as the ledger moves them.
/// </summary>
internal sealed class FileFamilyLedger
{
    private static long _nextId;

    private readonly Func<int> _calls;
    private readonly object _gate = new();
    private readonly List<Row> _rows = [];
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _completed = new(StringComparer.Ordinal);

    public FileFamilyLedger(Func<int> calls, DateTime? startedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(calls);
        _calls = calls;
        Unit = new DeliveryUnit(Guid.NewGuid(), DateTime.SpecifyKind(startedUtc ?? DateTime.UtcNow, DateTimeKind.Utc));
    }

    public DeliveryUnit Unit { get; }

    public List<FileFamilyReport> Reports { get; } = [];

    /// <summary>The artifacts as the ledger holds them, in the order their slots were first reported, each with its state.</summary>
    public IReadOnlyList<TargetArtifact> Rows
    {
        get
        {
            lock (_gate)
            {
                return _rows.Select(r => r.ToArtifact()).ToList();
            }
        }
    }

    /// <summary>The work as the worker hands it to the route: under this unit, its steps reported here.</summary>
    public DeliveryWork Track(DeliveryWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return work with { Unit = Unit, StepCompleted = CaptureAsync };
    }

    /// <summary>A later try of the same pending work: the same unit, with every step reported so far as completed.</summary>
    public DeliveryWork Resume(DeliveryWork work)
    {
        lock (_gate)
        {
            return Track(work) with { CompletedSteps = new Dictionary<string, IReadOnlyDictionary<string, string>>(_completed, StringComparer.Ordinal) };
        }
    }

    /// <summary>The artifact in <paramref name="slot"/>, as the ledger holds it.</summary>
    public TargetArtifact Slot(string slot)
    {
        lock (_gate)
        {
            return Assert.Single(_rows, r => r.Slot == slot).ToArtifact();
        }
    }

    /// <summary>What the undo of this unit takes, as the ledger hands it over: every artifact still open, in the order created.</summary>
    public UndoWork Undo(DeliveryWork work, UndoReason reason = UndoReason.Held, bool keepRecord = false)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            return new UndoWork
            {
                Key = work.Key,
                TargetId = work.TargetId,
                TargetState = work.TargetState,
                CommittedVersion = work.ExistingVersion,
                Reason = reason,
                KeepRecord = keepRecord,
                Items = _rows.Where(r => ArtifactStatuses.IsOpen(r.State)).Select(r => r.ToItem(Unit)).ToList(),
            };
        }
    }

    /// <summary>A delivered unit's end: an intent never completed is due, a minted id kept after commit is live, the rest goes.</summary>
    public void Commit()
    {
        lock (_gate)
        {
            foreach (var row in _rows.Where(r => r.State == ArtifactStatus.Intent))
            {
                row.State = ArtifactStatus.Due;
            }

            foreach (var row in _rows.Where(r => r.State == ArtifactStatus.Pending && ArtifactRoles.KeptAfterCommit(r.Role)))
            {
                row.State = ArtifactStatus.Live;
            }

            _rows.RemoveAll(r => r.State is ArtifactStatus.Pending or ArtifactStatus.Removed && !ArtifactRoles.KeptAfterCommit(r.Role));
        }
    }

    /// <summary>An aborted unit's end: every artifact still an intent or pending is due.</summary>
    public void Abort()
    {
        lock (_gate)
        {
            foreach (var row in _rows.Where(r => r.State is ArtifactStatus.Intent or ArtifactStatus.Pending))
            {
                row.State = ArtifactStatus.Due;
            }
        }
    }

    /// <summary>An undo's settlements, written only over artifacts still open, as the ledger writes them.</summary>
    public void Settle(IEnumerable<UndoResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        lock (_gate)
        {
            foreach (var result in results)
            {
                if (_rows.FirstOrDefault(r => r.Id == result.Item.ArtifactId) is { } row && ArtifactStatuses.IsOpen(row.State))
                {
                    row.State = result.Outcome;
                    row.Note = result.Note;
                    row.SettledByRoute = false;
                }
            }
        }
    }

    /// <summary>Asserts that <paramref name="results"/> answer every item of <paramref name="works"/> exactly once.</summary>
    public static void AssertAnsweredOnce(IReadOnlyList<UndoWork> works, IReadOnlyList<UndoResult> results)
    {
        ArgumentNullException.ThrowIfNull(works);
        ArgumentNullException.ThrowIfNull(results);
        Assert.Equal(works.SelectMany(w => w.Items).Select(i => i.ArtifactId).Order(), results.Select(r => r.Item.ArtifactId).Order());
        Assert.All(results, r => Assert.True(ArtifactStatuses.IsUndoOutcome(r.Outcome), $"{r.Item.Artifact.Slot} was answered {r.Outcome}, which is not an undo's outcome"));
    }

    /// <summary>The one result of the artifact in <paramref name="slot"/>.</summary>
    public static UndoResult For(IReadOnlyList<UndoResult> results, string slot) => Assert.Single(results, r => r.Item.Artifact.Slot == slot);

    private Task CaptureAsync(StepReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            Reports.Add(new FileFamilyReport(report, _calls()));
            _completed[report.Step] = new Dictionary<string, string>(report.Returned, StringComparer.Ordinal);
            foreach (var artifact in report.Artifacts)
            {
                Upsert(artifact);
            }
        }

        return Task.CompletedTask;
    }

    private void Upsert(TargetArtifact artifact)
    {
        var row = _rows.FirstOrDefault(r => r.Slot == artifact.Slot);
        if (row is null)
        {
            _rows.Add(new Row(Interlocked.Increment(ref _nextId), artifact));
            return;
        }

        // Open rows take the report, and so does a row the route settled itself (removed, kept or gone), which a later report
        // reopens; a row an undo settled never changes again.
        if (row.State is not (ArtifactStatus.Intent or ArtifactStatus.Pending) && !row.SettledByRoute)
        {
            return;
        }

        var stillCreated = row.Role == ArtifactRoles.Record && artifact.Role == ArtifactRoles.Version;
        row.PriorVersion = stillCreated ? row.PriorVersion : row.PriorVersion ?? artifact.PriorVersion;
        row.Role = stillCreated ? row.Role : artifact.Role;
        row.TargetId = artifact.TargetId ?? row.TargetId;
        row.Locator = artifact.Locator ?? row.Locator;
        row.Version = artifact.Version ?? row.Version;
        row.State = artifact.Status;
        row.Note = artifact.Note ?? row.Note;
        row.SettledByRoute = SettledByRoute(artifact.Status);
    }

    /// <summary>The states a route settles an artifact in itself, which the ledger marks settled by the route.</summary>
    private static bool SettledByRoute(ArtifactStatus status) => status is ArtifactStatus.Removed or ArtifactStatus.Kept or ArtifactStatus.Gone;

    private sealed class Row(long id, TargetArtifact first)
    {
        public long Id { get; } = id;

        public string Slot { get; } = first.Slot;

        public string Role { get; set; } = first.Role;

        public string? TargetId { get; set; } = first.TargetId;

        public string? Locator { get; set; } = first.Locator;

        public long? Version { get; set; } = first.Version;

        public long? PriorVersion { get; set; } = first.PriorVersion;

        public ArtifactStatus State { get; set; } = first.Status;

        public string? Note { get; set; } = first.Note;

        /// <summary>Whether the route settled the artifact itself (SettledBy 'route'), so a later report of its slot reopens it.</summary>
        public bool SettledByRoute { get; set; } = SettledByRoute(first.Status);

        public TargetArtifact ToArtifact() => new()
        {
            Slot = Slot,
            Role = Role,
            TargetId = TargetId,
            Locator = Locator,
            Version = Version,
            PriorVersion = PriorVersion,
            Status = State,
            Note = Note,
        };

        /// <summary>The artifact as the ledger hands it to an undo (<c>LedgerArtifact.ToItem</c>).</summary>
        public UndoItem ToItem(DeliveryUnit unit) => new(
            Id,
            ToArtifact() with { Status = State == ArtifactStatus.Intent ? ArtifactStatus.Intent : ArtifactStatus.Pending },
            unit.Id,
            unit.StartedUtc);
    }
}

/// <summary>How a call whose request landed loses its answer.</summary>
public enum FileFamilyLoss
{
    /// <summary>The connection drops before the answer comes.</summary>
    Transport,

    /// <summary>A gateway answers 502 for a request the service behind it took.</summary>
    ServerError,

    /// <summary>The answer does not come before the client stops waiting.</summary>
    Timeout,
}

/// <summary>
/// Sits in front of the fake platform and breaks chosen calls the ways a network and a service do: a call that lands and
/// whose answer is lost (the request reaches the platform, which acts on it, and the client sees a transport failure, a 502
/// or a timeout), a call the service refuses without acting on it, an answer of the test's own, and an answer rewritten on
/// its way back. Each fault takes the calls it matches until its count runs out; every other call goes to the platform.
/// </summary>
internal sealed class FileFamilyFaults : DelegatingHandler
{
    private readonly object _gate = new();
    private readonly List<Fault> _faults = [];

    public FileFamilyFaults(FakeOsduPlatform platform)
        : base(platform)
    {
        Platform = platform;
    }

    private enum FaultKind
    {
        Lose,
        Refuse,
        Answer,
        Rewrite,
    }

    public FakeOsduPlatform Platform { get; }

    /// <summary>Every call a fault took, in order, and what it did to it.</summary>
    public List<string> Broken { get; } = [];

    /// <summary>A request of <paramref name="method"/> whose unescaped path ends with <paramref name="path"/>.</summary>
    public static Func<HttpRequestMessage, bool> On(HttpMethod method, string path)
        => request => request.Method == method && PathOf(request).EndsWith(path, StringComparison.Ordinal);

    /// <summary>The <paramref name="nth"/> request (counted from 1) of <paramref name="method"/> whose path ends with <paramref name="path"/>.</summary>
    public static Func<HttpRequestMessage, bool> Nth(HttpMethod method, string path, int nth)
    {
        var on = On(method, path);
        var seen = 0;
        return request => on(request) && Interlocked.Increment(ref seen) == nth;
    }

    public static string PathOf(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// The search index as the routes read it: a live record is listed by a query that names its id, or the landing-zone path
    /// its file was registered under, in quotes; a soft-deleted record leaves the index.
    /// </summary>
    public static void IndexLive(FakeOsduPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        platform.Search = (_, query) => platform.Records
            .Where(r => query is not null
                && !platform.Removed.Contains(r.Key)
                && (query.Contains('"' + r.Key + '"', StringComparison.Ordinal)
                    || (r.Value["data"]?["DatasetProperties"]?["FileSourceInfo"]?["FileSource"] is JsonValue source
                        && source.TryGetValue<string>(out var path)
                        && query.Contains('"' + path + '"', StringComparison.Ordinal))))
            .Select(r => r.Key)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    public FileFamilyFaults LoseAnswer(HttpMethod method, string path, FileFamilyLoss loss = FileFamilyLoss.Transport, int times = 1)
        => Lose(On(method, path), loss, times);

    public FileFamilyFaults Lose(Func<HttpRequestMessage, bool> match, FileFamilyLoss loss, int times = 1)
        => Add(new Fault(match, FaultKind.Lose, times) { Loss = loss });

    public FileFamilyFaults Refuse(HttpMethod method, string path, HttpStatusCode status, int times = 1)
        => Refuse(On(method, path), status, times);

    public FileFamilyFaults Refuse(Func<HttpRequestMessage, bool> match, HttpStatusCode status, int times = 1)
        => Add(new Fault(match, FaultKind.Refuse, times) { Status = status });

    public FileFamilyFaults Answer(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, int times = int.MaxValue)
        => Add(new Fault(match, FaultKind.Answer, times) { Respond = respond });

    public FileFamilyFaults Rewrite(Func<HttpRequestMessage, bool> match, Action<JsonObject> edit, int times = 1)
        => Add(new Fault(match, FaultKind.Rewrite, times) { Edit = edit });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Fault? fault;
        lock (_gate)
        {
            fault = _faults.FirstOrDefault(f => f.Left > 0 && f.Match(request));
            if (fault is not null)
            {
                fault.Left--;
                Broken.Add($"{fault.Kind} {request.Method} {PathOf(request)}");
            }
        }

        if (fault is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        switch (fault.Kind)
        {
            case FaultKind.Refuse:
                var refusal = new JsonObject { ["code"] = (int)fault.Status, ["reason"] = fault.Status.ToString(), ["message"] = "refused by the test" };
                return new HttpResponseMessage(fault.Status) { Content = new StringContent(refusal.ToJsonString(), Encoding.UTF8, "application/json") };
            case FaultKind.Answer:
                return await fault.Respond!(request, cancellationToken);
            case FaultKind.Rewrite:
                using (var answer = await base.SendAsync(request, cancellationToken))
                {
                    var root = JsonNode.Parse(await answer.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
                    fault.Edit!(root);
                    return new HttpResponseMessage(answer.StatusCode) { Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json") };
                }

            default:
                using (await base.SendAsync(request, cancellationToken))
                {
                    // The platform took the request and acted on it; its answer goes no further.
                }

                return fault.Loss switch
                {
                    FileFamilyLoss.Transport => throw new HttpRequestException("the connection was reset before the answer came"),
                    FileFamilyLoss.Timeout => throw new TaskCanceledException("the answer did not come before the client stopped waiting"),
                    _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("{\"message\":\"upstream answer lost\"}", Encoding.UTF8, "application/json") },
                };
        }
    }

    private FileFamilyFaults Add(Fault fault)
    {
        lock (_gate)
        {
            _faults.Add(fault);
        }

        return this;
    }

    private sealed class Fault(Func<HttpRequestMessage, bool> match, FaultKind kind, int times)
    {
        public Func<HttpRequestMessage, bool> Match { get; } = match;

        public FaultKind Kind { get; } = kind;

        public int Left { get; set; } = times;

        public FileFamilyLoss Loss { get; init; }

        public HttpStatusCode Status { get; init; }

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond { get; init; }

        public Action<JsonObject>? Edit { get; init; }
    }
}
