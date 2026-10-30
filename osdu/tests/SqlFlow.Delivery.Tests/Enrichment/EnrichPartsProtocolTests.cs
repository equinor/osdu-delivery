using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The route of a flow whose mapping's enrichments add records (osdu/docs/reference/flow/enrich-records.md), over the storage
/// route and a fake platform that checks every request against the pinned contracts: a dataset an enrichment adds is stored and
/// registered under its derived id, declared before and checked for retrieval after, before the record is written; a record of
/// another kind is written through storage the same way; new files or a changed record write a new version of the same id and
/// leave the record as OSDU holds it; a changed document writes the record and leaves an unchanged part alone; a failure fails
/// its record alone, unwritten; a retry resumes after what landed; an unfinished delivery is undone at the reversible scope; and
/// removing everything purges what the enrichments added too.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class EnrichPartsProtocolTests
{
    private const string OwnerId = "dev:work-product-component--WellLog:enrich-7";
    private const string OtherId = "dev:work-product-component--WellLog:enrich-8";
    private const string OwnerKind = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string Register = "/api/dataset/v1/registerDataset";
    private const string Instructions = "/api/dataset/v1/storageInstructions";
    private const string Retrieval = "/api/dataset/v1/retrievalInstructions";
    private const string StorageRecords = "/api/storage/v2/records";
    private const string NoteKind = "osdu:wks:work-product-component--Document:1.0.0";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private sealed class Rig : IDisposable
    {
        public Rig(bool registersFiles = true)
        {
            Platform = new FakeOsduPlatform();
            Faults = new FileFamilyFaults(Platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                Secrets, TimeProvider.System, Faults, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            Options = new ProtocolOptions
            {
                PayloadContentType = "application/json",
                UploadHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-ms-blob-type"] = "BlockBlob" },
            };
            Protocol = new EnrichPartsProtocol(new OsduRecordProtocol(Client, Options), Client, Options, registersFiles: registersFiles);
        }

        public FakeOsduPlatform Platform { get; }

        public FileFamilyFaults Faults { get; }

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public ProtocolOptions Options { get; }

        public EnrichPartsProtocol Protocol { get; }

        public FileFamilyLedger Ledger() => new(() => Platform.Calls.Count);

        public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work) => (await Protocol.DeliverBatchAsync([work]))[0];

        public long VersionOf(string id) => Platform.Records[id]["version"]!.GetValue<long>();

        public int Count(HttpMethod method, string path, int from = 0)
            => Platform.Calls.Skip(from).Count(c => c.Method == method && Uri.UnescapeDataString(c.Uri.AbsolutePath) == path);

        public int IndexOf(HttpMethod method, string path)
            => Platform.Calls.FindIndex(c => c.Method == method && Uri.UnescapeDataString(c.Uri.AbsolutePath) == path);

        /// <summary>The storage writes of <paramref name="id"/> since <paramref name="from"/>: what the record's own route sent.</summary>
        public int Writes(string id, int from = 0)
            => Platform.Calls.Skip(from).Count(c => c.Method == HttpMethod.Put && Uri.UnescapeDataString(c.Uri.AbsolutePath) == StorageRecords
                && c.Body is { } body && body.Contains('"' + id + '"', StringComparison.Ordinal));

        public void Dispose()
        {
            Runtime.Dispose();
            Faults.Dispose();
        }
    }

    private static string DatasetId(string owner = OwnerId, string suffix = "enrich.lasfile")
        => WorkflowValues.DerivedDatasetId(owner, "dataset--File.Generic", suffix);

    private static JsonObject Owner(string name = "log 7", string id = OwnerId, params string[] datasets)
        => FakeOsduPlatform.Record(id, OwnerKind, new JsonObject
        {
            ["Name"] = name,
            ["Datasets"] = new JsonArray((datasets.Length == 0 ? [DatasetId(id)] : datasets).Select(d => (JsonNode)JsonValue.Create(d + ":")).ToArray()),
        });

    private static EnrichPart Part(string hash = "h1", string? id = null, string record = "{}")
        => new("lasfile", id ?? DatasetId(), EnrichmentSuite.DatasetKind, "mem://las/*", hash, record, null);

    private static WorkStepPart Step(EnrichPart part, string text = "~Version Information")
        => new(part, new MemoryFiles(("a.las", text)));

    private static string NoteId(string owner = OwnerId) => WorkflowValues.DerivedDatasetId(owner, "work-product-component--Document", "enrich.note");

    /// <summary>A record of another kind than a dataset an enrichment adds, written through storage as it renders: no files.</summary>
    private static WorkStepPart Note(string name = "note of log 7", string? id = null)
        => new(new EnrichPart("note", id ?? NoteId(), NoteKind, string.Empty, string.Empty, new JsonObject { ["data"] = new JsonObject { ["Name"] = name } }.ToJsonString(), null), null);

    private static JsonObject NoteOwner(string name = "log 7")
        => FakeOsduPlatform.Record(OwnerId, OwnerKind, new JsonObject { ["Name"] = name, ["Datasets"] = new JsonArray(NoteId() + ":") });

    private static DeliveryWork Work(
        JsonObject document, IReadOnlyList<WorkStepPart> parts, long? existing = null, IReadOnlyDictionary<string, string>? state = null, bool unchanged = false, bool forced = false)
        => new()
        {
            Key = DeliveryKey.Derive("enrich-parts", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = true,
            DeliverPayload = false,
            StepParts = parts,
            StepDocumentUnchanged = unchanged,
            StepPartsForced = forced,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    /// <summary>The next work of the same record after <paramref name="earlier"/> landed, as the worker hands it over.</summary>
    private static DeliveryWork Next(JsonObject document, IReadOnlyList<WorkStepPart> parts, DeliveryOutcome earlier, bool unchanged, bool forced = false)
        => Work(document, parts, earlier.TargetVersion, earlier.Returned, unchanged, forced);

    private static async Task<DeliveryOutcome> FirstAsync(Rig rig, JsonObject? document = null, EnrichPart? part = null)
    {
        var outcome = await rig.DeliverAsync(Work(document ?? Owner(), [Step(part ?? Part())], forced: true));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        return outcome;
    }

    private static string HashOf(JsonObject document) => Hashing.ContentHash.Of(CanonicalJson.ToString(document));

    [Fact]
    public void The_route_wraps_the_flows_own_and_undoes_what_its_datasets_leave()
    {
        using var rig = new Rig();

        Assert.Equal(DeliveryProtocol.Storage, rig.Protocol.Kind);
        Assert.True(rig.Protocol.Undoes);
        Assert.IsType<OsduRecordProtocol>(rig.Protocol.Inner);
    }

    [Fact]
    public async Task A_first_delivery_registers_the_steps_dataset_under_its_id_before_the_record_that_links_it()
    {
        using var rig = new Rig();
        var ledger = rig.Ledger();
        var outcome = await rig.DeliverAsync(ledger.Track(Work(Owner(), [Step(Part())], forced: true)));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var id = DatasetId();
        var dataset = rig.Platform.Records[id];
        Assert.Equal(EnrichmentSuite.DatasetKind, dataset["kind"]!.GetValue<string>());
        Assert.Equal("a.las", dataset["data"]!["Name"]!.GetValue<string>());
        Assert.NotNull(dataset["data"]!["DatasetProperties"]!["FileSourceInfo"]!["FileSource"]);
        Assert.True(JsonNode.DeepEquals(Owner()["acl"], dataset["acl"]), dataset["acl"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(Owner()["legal"], dataset["legal"]), dataset["legal"]!.ToJsonString());
        Assert.Equal(id + ":", rig.Platform.Records[OwnerId]["data"]!["Datasets"]![0]!.GetValue<string>());

        // The dataset is registered, and found retrievable, before the record naming it is written.
        Assert.True(rig.IndexOf(HttpMethod.Put, Register) < rig.IndexOf(HttpMethod.Put, StorageRecords));
        Assert.True(rig.IndexOf(HttpMethod.Post, Retrieval) < rig.IndexOf(HttpMethod.Put, StorageRecords));
        Assert.Equal(
            [DatasetUploads.StorageStep(EnrichPartsProtocol.PayloadName(0)), EnrichPartsProtocol.IntentStep(0), EnrichPartsProtocol.RegisterStep(0)],
            ledger.Reports.Select(r => r.Step).Take(3));
        var landed = ledger.Slot(EnrichPartsProtocol.SlotOf(EnrichPartsProtocol.DatasetSlot, id));
        Assert.Equal((id, ArtifactRoles.Record), (landed.TargetId, landed.Role));

        Assert.Equal(Part().Hash, outcome.Returned[EnrichParts.StateKey(id)]);
        Assert.Equal("h1", outcome.Returned[EnrichParts.FilesStateKey(id)]);
        Assert.Equal([id], EnrichParts.Records(outcome.Returned));
        Assert.Equal(HashOf(Owner()), outcome.Returned[EnrichParts.DocumentValue]);
        Assert.Equal(rig.VersionOf(OwnerId), outcome.TargetVersion);
        Assert.True(outcome.PayloadDelivered);
    }

    [Fact]
    public async Task New_files_register_a_new_version_of_the_same_dataset_and_leave_the_record_as_OSDU_holds_it()
    {
        using var rig = new Rig();
        var first = await FirstAsync(rig);
        var owner = rig.VersionOf(OwnerId);
        var mark = rig.Platform.Calls.Count;

        var outcome = await rig.DeliverAsync(Next(Owner(), [Step(Part("h2"), "~Version Information 2")], first, unchanged: true));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(1, rig.Count(HttpMethod.Put, Register, mark));
        Assert.Equal(0, rig.Writes(OwnerId, mark));
        Assert.Equal(owner, rig.VersionOf(OwnerId));
        Assert.Equal(2, rig.Platform.History[DatasetId()].Count);
        Assert.Equal((first.TargetVersion, true), (outcome.TargetVersion, outcome.MetadataDelivered));
        Assert.Contains("is not written again", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal("h2", outcome.Returned[EnrichParts.FilesStateKey(DatasetId())]);
        Assert.Empty(outcome.Superseded);
    }

    [Fact]
    public async Task A_changed_document_writes_the_record_and_leaves_an_unchanged_dataset_alone()
    {
        using var rig = new Rig();
        var first = await FirstAsync(rig);
        var mark = rig.Platform.Calls.Count;

        var outcome = await rig.DeliverAsync(Next(Owner("log 7, renamed"), [Step(Part())], first, unchanged: false));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(0, rig.Count(HttpMethod.Put, Register, mark));
        Assert.Equal(0, rig.Count(HttpMethod.Post, Instructions, mark));
        Assert.Equal(1, rig.Writes(OwnerId, mark));
        Assert.Single(rig.Platform.History[DatasetId()]);
        Assert.Equal("log 7, renamed", rig.Platform.Records[OwnerId]["data"]!["Name"]!.GetValue<string>());
        Assert.Equal(HashOf(Owner("log 7, renamed")), outcome.Returned[EnrichParts.DocumentValue]);
        Assert.False(outcome.PayloadDelivered);
    }

    [Fact]
    public async Task A_record_the_plan_found_unchanged_is_still_written_when_its_last_delivery_kept_another_document()
    {
        using var rig = new Rig();
        var first = await FirstAsync(rig);
        var mark = rig.Platform.Calls.Count;
        var kept = new Dictionary<string, string>(first.Returned, StringComparer.Ordinal) { [EnrichParts.DocumentValue] = "another" };

        var outcome = await rig.DeliverAsync(Work(Owner(), [Step(Part("h2"))], first.TargetVersion, kept, unchanged: true));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(1, rig.Writes(OwnerId, mark));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_record_OSDU_lost_or_never_held_is_written_whatever_the_plan_found(bool lost)
    {
        using var rig = new Rig();
        var first = await FirstAsync(rig);
        var mark = rig.Platform.Calls.Count;
        var work = Next(Owner(), [Step(Part("h2"))], first, unchanged: true);
        work = lost ? work with { RestoreFirst = true } : work with { ExistingVersion = null };

        Assert.True((await rig.DeliverAsync(work)).Succeeded);
        Assert.Equal(1, rig.Writes(OwnerId, mark));
    }

    [Fact]
    public async Task A_forced_delivery_registers_every_dataset_again_and_a_redelivery_naming_files_does_too()
    {
        using var rig = new Rig();
        var first = await FirstAsync(rig);
        var mark = rig.Platform.Calls.Count;

        Assert.True((await rig.DeliverAsync(Next(Owner(), [Step(Part())], first, unchanged: true, forced: true))).Succeeded);
        Assert.Equal(1, rig.Count(HttpMethod.Put, Register, mark));
        Assert.Equal(0, rig.Writes(OwnerId, mark));

        mark = rig.Platform.Calls.Count;
        var named = Next(Owner(), [Step(Part())], first, unchanged: true) with { ForcedParts = new HashSet<string>([PayloadParts.Files], StringComparer.Ordinal) };
        Assert.True((await rig.DeliverAsync(named)).Succeeded);
        Assert.Equal(1, rig.Count(HttpMethod.Put, Register, mark));
    }

    [Fact]
    public async Task Nothing_moved_sends_nothing_but_the_record_the_plan_asked_for()
    {
        using var rig = new Rig();
        var first = await FirstAsync(rig);
        var mark = rig.Platform.Calls.Count;

        var outcome = await rig.DeliverAsync(Next(Owner(), [Step(Part())], first, unchanged: true));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(0, rig.Platform.Calls.Count - mark);
        Assert.False(outcome.PayloadDelivered);
    }

    [Fact]
    public async Task A_dataset_whose_files_cannot_be_read_back_fails_its_record_unwritten()
    {
        using var rig = new Rig();
        rig.Platform.Unretrievable.Add(DatasetId());

        var outcome = await rig.DeliverAsync(Work(Owner(), [Step(Part())], forced: true));

        Assert.False(outcome.Succeeded);
        Assert.Contains("answers no retrieval instructions", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OwnerId, rig.Platform.Records.Keys);
    }

    [Fact]
    public async Task A_registration_refused_as_a_whole_is_tried_record_by_record_and_one_refused_alone_fails_alone()
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Put, Register, HttpStatusCode.BadRequest);
        var works = new[]
        {
            Work(Owner(), [Step(Part())], forced: true),
            Work(Owner(id: OtherId), [Step(Part(id: DatasetId(OtherId)))], forced: true),
        };

        var outcomes = await rig.Protocol.DeliverBatchAsync(works);

        // The refused request never reaches the platform; the two that follow it, one per record, do.
        Assert.All(outcomes, o => Assert.True(o.Succeeded, o.Failure?.Message));
        Assert.Single(rig.Faults.Broken);
        Assert.Equal(2, rig.Count(HttpMethod.Put, Register));

        using var refused = new Rig();
        refused.Faults.Refuse(HttpMethod.Put, Register, HttpStatusCode.BadRequest, times: 3);
        var failed = await refused.Protocol.DeliverBatchAsync(works);
        Assert.All(failed, o => Assert.False(o.Succeeded));
        Assert.DoesNotContain(OwnerId, refused.Platform.Records.Keys);
        Assert.DoesNotContain(OtherId, refused.Platform.Records.Keys);
    }

    [Fact]
    public async Task More_datasets_than_one_registration_takes_go_twenty_to_a_request()
    {
        using var rig = new Rig();
        var ids = Enumerable.Range(0, 21).Select(i => DatasetId(suffix: string.Create(CultureInfo.InvariantCulture, $"enrich.lasfile.{i:D2}"))).ToArray();
        var parts = ids.Select(id => Step(Part(id: id))).ToList();

        var outcome = await rig.DeliverAsync(Work(Owner(datasets: ids), parts, forced: true));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(2, rig.Count(HttpMethod.Put, Register));
        Assert.All(ids, id => Assert.Contains(id, rig.Platform.Records.Keys));
        Assert.Equal(ids.Order(StringComparer.Ordinal), EnrichParts.Records(outcome.Returned));
    }

    [Fact]
    public async Task A_retry_resumes_after_the_registration_that_landed_and_writes_the_record()
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.InternalServerError);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Owner(), [Step(Part())], forced: true));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        Assert.Contains(DatasetId(), rig.Platform.Records.Keys);
        var mark = rig.Platform.Calls.Count;

        var retried = await rig.DeliverAsync(ledger.Resume(work));

        Assert.True(retried.Succeeded, retried.Failure?.Message);
        Assert.Equal(0, rig.Count(HttpMethod.Put, Register, mark));
        Assert.Equal(0, rig.Count(HttpMethod.Post, Instructions, mark));
        Assert.Contains(OwnerId, rig.Platform.Records.Keys);
        Assert.Single(rig.Platform.History[DatasetId()]);
    }

    [Fact]
    public async Task An_unfinished_delivery_removes_the_dataset_it_created_through_the_dataset_services_reversible_removal()
    {
        using var rig = new Rig();
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.InternalServerError);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(Owner(), [Step(Part())], forced: true));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);

        var result = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));

        Assert.Equal(ArtifactStatus.Removed, result.Outcome);
        Assert.Contains(DatasetId(), rig.Platform.Removed);
        Assert.Equal(1, rig.Count(HttpMethod.Post, "/api/dataset/v1/metadataRecord/" + DatasetId() + "/softDelete"));
    }

    [Fact]
    public async Task An_unfinished_delivery_of_new_files_gives_the_dataset_back_the_version_before()
    {
        using var rig = new Rig();
        var first = await FirstAsync(rig);
        var before = rig.VersionOf(DatasetId());
        string Source() => rig.Platform.Records[DatasetId()]["data"]!["DatasetProperties"]!["FileSourceInfo"]!["FileSource"]!.GetValue<string>();
        var source = Source();
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.InternalServerError);
        var ledger = rig.Ledger();
        var work = ledger.Track(Next(Owner("renamed"), [Step(Part("h2"), "new")], first, unchanged: false));
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        Assert.NotEqual(before, rig.VersionOf(DatasetId()));
        Assert.NotEqual(source, Source());

        var result = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));

        Assert.Equal(ArtifactStatus.Restored, result.Outcome);
        Assert.Equal(source, Source());
        Assert.DoesNotContain(DatasetId(), rig.Platform.Removed);
    }

    [Fact]
    public async Task Removing_everything_purges_the_datasets_and_a_reversible_removal_leaves_them()
    {
        using var rig = new Rig();
        var first = await FirstAsync(rig);

        var record = await rig.Protocol.DeleteAsync(OwnerId, RemovalScope.Record, first.Returned);
        Assert.True(record.Deleted);
        Assert.Contains(OwnerId, rig.Platform.Removed);
        Assert.DoesNotContain(DatasetId(), rig.Platform.Removed);
        Assert.Contains(DatasetId(), rig.Platform.Records.Keys);

        using var purge = new Rig();
        var purged = await FirstAsync(purge);
        var everything = await purge.Protocol.DeleteAsync(OwnerId, RemovalScope.Everything, purged.Returned);
        Assert.True(everything.Deleted);
        Assert.DoesNotContain(OwnerId, purge.Platform.Records.Keys);
        Assert.DoesNotContain(DatasetId(), purge.Platform.Records.Keys);
        Assert.Contains("1 of the 1 record(s) its enrichments added purged too", everything.Detail, StringComparison.Ordinal);

        var batch = await purge.Protocol.DeleteBatchAsync([new RecordRemoval(DeliveryKey.Derive("x", ["y"]), OwnerId, purged.Returned)], RemovalScope.Everything);
        Assert.True(Assert.Single(batch).Succeeded);
    }

    [Fact]
    public async Task A_dataset_the_record_no_longer_links_stays_live_and_is_marked_superseded()
    {
        using var rig = new Rig();
        var first = await FirstAsync(rig);
        var other = DatasetId(suffix: "enrich.lasfile.2");

        var moved = await rig.DeliverAsync(Next(Owner(datasets: other), [Step(Part(id: other))], first, unchanged: false));
        Assert.True(moved.Succeeded, moved.Failure?.Message);
        Assert.Equal([DatasetId()], moved.Superseded);
        Assert.Equal([other], EnrichParts.Records(moved.Returned));
        Assert.DoesNotContain(DatasetId(), rig.Platform.Removed);

        var none = await rig.DeliverAsync(Work(FakeOsduPlatform.Record(OwnerId, OwnerKind), [], moved.TargetVersion, moved.Returned));
        Assert.True(none.Succeeded, none.Failure?.Message);
        Assert.Equal([other], none.Superseded);
        Assert.Empty(EnrichParts.Records(none.Returned));
    }

    [Fact]
    public async Task A_record_without_an_access_list_holds_before_anything_is_registered()
    {
        using var rig = new Rig();
        var owner = Owner();
        owner.Remove("acl");

        var outcome = await rig.DeliverAsync(Work(owner, [Step(Part())], forced: true));

        Assert.IsType<RecordHeldException>(outcome.Failure);
        Assert.Equal(0, rig.Count(HttpMethod.Put, Register));
    }

    [Fact]
    public async Task The_probe_asks_the_flows_own_services_then_the_dataset_service()
    {
        using var rig = new Rig();

        var probe = await rig.Protocol.ProbeAsync();

        Assert.True(probe.Reachable, probe.Detail);
        Assert.Equal(DatasetService.DefaultProbePath, probe.Path);
    }

    [Fact]
    public async Task The_probe_of_a_flow_registering_no_files_asks_the_flows_own_services_alone()
    {
        using var rig = new Rig(registersFiles: false);

        var probe = await rig.Protocol.ProbeAsync();

        Assert.True(probe.Reachable, probe.Detail);
        Assert.NotEqual(DatasetService.DefaultProbePath, probe.Path);
        Assert.DoesNotContain(rig.Platform.Calls, c => c.Uri.AbsolutePath.Contains("/dataset/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_record_of_another_kind_is_written_through_storage_under_its_id_before_the_record_that_links_it()
    {
        using var rig = new Rig(registersFiles: false);
        var ledger = rig.Ledger();

        var outcome = await rig.DeliverAsync(ledger.Track(Work(NoteOwner(), [Note()], forced: true)));

        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var note = rig.Platform.Records[NoteId()];
        Assert.Equal((NoteKind, "note of log 7"), (note["kind"]!.GetValue<string>(), note["data"]!["Name"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(NoteOwner()["acl"], note["acl"]), note["acl"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(NoteOwner()["legal"], note["legal"]), note["legal"]!.ToJsonString());
        Assert.Equal(0, rig.Count(HttpMethod.Put, Register));
        Assert.Equal(0, rig.Count(HttpMethod.Post, Retrieval));
        Assert.Equal(2, rig.Count(HttpMethod.Put, StorageRecords));
        Assert.True(rig.Writes(NoteId()) == 1 && rig.Platform.Calls.FindIndex(c => c.Body?.Contains('"' + NoteId() + '"', StringComparison.Ordinal) == true)
            < rig.Platform.Calls.FindIndex(c => c.Method == HttpMethod.Put && c.Body?.Contains('"' + OwnerId + '"', StringComparison.Ordinal) == true));
        var landed = ledger.Slot(EnrichPartsProtocol.SlotOf(EnrichPartsProtocol.RecordSlot, NoteId()));
        Assert.Equal((NoteId(), ArtifactRoles.Record), (landed.TargetId, landed.Role));
        Assert.Equal([NoteId()], EnrichParts.Records(outcome.Returned));
        Assert.False(outcome.Returned.ContainsKey(EnrichParts.FilesStateKey(NoteId())));
        Assert.Contains("1 record(s) of its enrichments written", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_of_another_kind_is_written_again_as_a_new_version_only_when_it_changes()
    {
        using var rig = new Rig(registersFiles: false);
        var first = await rig.DeliverAsync(Work(NoteOwner(), [Note()], forced: true));
        Assert.True(first.Succeeded, first.Failure?.Message);
        var owner = rig.VersionOf(OwnerId);

        var mark = rig.Platform.Calls.Count;
        var same = await rig.DeliverAsync(Next(NoteOwner(), [Note()], first, unchanged: true));
        Assert.True(same.Succeeded, same.Failure?.Message);
        Assert.Equal(0, rig.Platform.Calls.Count - mark);

        var changed = await rig.DeliverAsync(Next(NoteOwner(), [Note("note of log 7, corrected")], same, unchanged: true));
        Assert.True(changed.Succeeded, changed.Failure?.Message);
        Assert.Equal(1, rig.Writes(NoteId(), mark));
        Assert.Equal(0, rig.Writes(OwnerId, mark));
        Assert.Equal(owner, rig.VersionOf(OwnerId));
        Assert.Equal(2, rig.Platform.History[NoteId()].Count);
        Assert.Equal("note of log 7, corrected", rig.Platform.Records[NoteId()]["data"]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unfinished_delivery_removes_the_record_it_wrote_through_storage_reversibly()
    {
        using var rig = new Rig(registersFiles: false);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(NoteOwner(), [Note()], forced: true));
        rig.Faults.Refuse(FileFamilyFaults.Nth(HttpMethod.Put, StorageRecords, 2), HttpStatusCode.InternalServerError);
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        Assert.Contains(NoteId(), rig.Platform.Records.Keys);

        var result = Assert.Single(await rig.Protocol.UndoAsync([ledger.Undo(work)]));

        Assert.Equal(ArtifactStatus.Removed, result.Outcome);
        Assert.Contains(NoteId(), rig.Platform.Removed);
        Assert.Equal(1, rig.Count(HttpMethod.Post, StorageRecords + "/" + NoteId() + ":delete"));
        Assert.Equal(0, rig.Count(HttpMethod.Post, "/api/dataset/v1/metadataRecord/" + NoteId() + "/softDelete"));
    }

    [Fact]
    public async Task An_unfinished_delivery_removes_every_record_its_enrichment_added_each_kept_in_a_slot_of_its_own()
    {
        using var rig = new Rig(registersFiles: false);
        var ledger = rig.Ledger();
        var second = NoteId() + "-2";
        var work = ledger.Track(Work(NoteOwner(), [Note(), Note("second note of log 7", second)], forced: true));
        rig.Faults.Refuse(FileFamilyFaults.Nth(HttpMethod.Put, StorageRecords, 3), HttpStatusCode.InternalServerError);
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        Assert.Equal(second, ledger.Slot(EnrichPartsProtocol.SlotOf(EnrichPartsProtocol.RecordSlot, second)).TargetId);

        var results = await rig.Protocol.UndoAsync([ledger.Undo(work)]);

        Assert.Equal([ArtifactStatus.Removed, ArtifactStatus.Removed], results.Select(r => r.Outcome));
        Assert.Contains(NoteId(), rig.Platform.Removed);
        Assert.Contains(second, rig.Platform.Removed);
    }

    [Fact]
    public async Task A_record_a_ledger_holds_in_the_slot_every_added_record_once_shared_is_still_undone()
    {
        using var rig = new Rig(registersFiles: false);
        var ledger = rig.Ledger();
        var work = ledger.Track(Work(NoteOwner(), [Note()], forced: true));
        rig.Faults.Refuse(FileFamilyFaults.Nth(HttpMethod.Put, StorageRecords, 2), HttpStatusCode.InternalServerError);
        Assert.False((await rig.DeliverAsync(work)).Succeeded);
        var undo = ledger.Undo(work);
        var shared = undo with { Items = undo.Items.Select(i => i with { Artifact = i.Artifact with { Slot = EnrichPartsProtocol.RecordSlot } }).ToList() };

        var result = Assert.Single(await rig.Protocol.UndoAsync([shared]));

        Assert.Equal(ArtifactStatus.Removed, result.Outcome);
        Assert.Contains(NoteId(), rig.Platform.Removed);
    }

    [Fact]
    public async Task A_try_resumed_after_a_build_that_declared_its_intent_in_the_shared_slot_completes_that_intent()
    {
        using var rig = new Rig(registersFiles: false);
        var ledger = rig.Ledger();
        var work = Work(NoteOwner(), [Note()], forced: true);

        // What a build before each added record had a slot of its own reported, before its try stopped short of the write.
        var shared = new TargetArtifact { Slot = EnrichPartsProtocol.RecordSlot, Role = ArtifactRoles.Record, TargetId = NoteId(), Status = ArtifactStatus.Intent };
        await ledger.Track(work).ReportStepAsync(
            EnrichPartsProtocol.IntentStep(0), new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = NoteId(), ["step"] = "note" }, [shared], CancellationToken.None);

        var outcome = await rig.DeliverAsync(ledger.Resume(work));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        ledger.Commit();

        // The intent is completed in its slot, so nothing the delivery wrote is left for an undo to remove.
        Assert.Contains(NoteId(), rig.Platform.Records.Keys);
        Assert.Empty(ledger.Undo(work).Items);
    }

    [Fact]
    public async Task Removing_everything_purges_a_record_of_another_kind_the_enrichments_added()
    {
        using var rig = new Rig(registersFiles: false);
        var first = await rig.DeliverAsync(Work(NoteOwner(), [Note()], forced: true));
        Assert.True(first.Succeeded, first.Failure?.Message);

        var everything = await rig.Protocol.DeleteAsync(OwnerId, RemovalScope.Everything, first.Returned);

        Assert.True(everything.Deleted);
        Assert.DoesNotContain(NoteId(), rig.Platform.Records.Keys);
        Assert.Contains("1 of the 1 record(s) its enrichments added purged too", everything.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_storage_refuses_fails_its_enriched_record_alone_unwritten()
    {
        using var rig = new Rig(registersFiles: false);
        rig.Faults.Refuse(HttpMethod.Put, StorageRecords, HttpStatusCode.BadRequest);

        var outcome = await rig.DeliverAsync(Work(NoteOwner(), [Note()], forced: true));

        Assert.False(outcome.Succeeded);
        Assert.Contains($"storage refused {NoteId()}, the record enrichment 'note' adds", outcome.Failure!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OwnerId, rig.Platform.Records.Keys);
    }
}
