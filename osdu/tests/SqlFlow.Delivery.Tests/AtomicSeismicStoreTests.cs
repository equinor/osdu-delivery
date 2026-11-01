using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Seismic Store's part of the units of work (osdu/docs/reference/concepts/artifacts-and-undo.md, Seismic Store),
/// against the fake of the service, its stores and Storage: a registration names the dataset, the lock and the record
/// it carries before it goes and settles them by its answer, the record Seismic Store wrote is reported before any
/// object lands, opening a dataset names the read-only flag once lifted and the lock before it is asked for, a close
/// or patch that carries the record names it first, a close settles the lock and the flag, and a try held while it
/// holds the lock releases it itself under its own id; then the undo after each failure point (a registration another
/// writer holds or whose answer is lost, an upload, a close that fails or loses its answer, a lock refused or lost, a
/// release that fails), which deletes the dataset the unit registered with its objects (not one taken over from before
/// the unit, not on gc, not when the provider cannot be told), releases only a lock held under the unit's id and sets
/// the read-only flag again, removes or writes back the record, minds a record OSDU held before the unit began, leaves
/// the dataset and the record to newer work, does no harm a second time, and answers every artifact once, a failure
/// for its own artifact alone, the record waiting with what it names.
/// </summary>
public sealed class AtomicSeismicStoreTests
{
    private const string LineId = "dev:dataset--FileCollection.SEGY:line-001";
    private const string SecondId = "dev:dataset--FileCollection.SEGY:line-002";
    private const string LineKind = "osdu:wks:dataset--FileCollection.SEGY:1.1.0";
    private const string SdPath = "sd://dev/seismic/surveys/north/line-001";
    private const string SecondPath = "sd://dev/seismic/surveys/north/line-002";
    private const string Base = FakeOsduPlatform.SeismicRoot + "/dataset/tenant/dev/subproject/seismic/dataset/line-001";
    private const string Records = "/api/storage/v2/records";
    private const string Elsewhere = "WanotherWriterOfTheDataset";
    private const int MiB = 1024 * 1024;

    private static readonly DeliveryUnit Unit = new(Guid.Parse("0192aa00-0000-7000-8000-00000000535e"), new DateTime(2026, 9, 17, 11, 58, 0, DateTimeKind.Utc));

    private static SeismicStoreSettings Settings(string? provider = null, bool readOnly = false) => new()
    {
        Subproject = "seismic",
        Folder = "surveys/north",
        Provider = provider is null ? null : Enum.Parse<DdmsProvider>(provider, ignoreCase: true),
        ObjectStore = provider == "gc" ? FakeOsduPlatform.GcsEndpoint : null,
        ChunkMiB = 1,
        ReadOnly = readOnly,
    };

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform, SeismicStoreSettings? settings = null)
        {
            Platform = platform;
            Hook = new ShapeCallHook(platform);
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), Hook, allowLoopback: true);
            var client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            var options = new ProtocolOptions { WorkflowPollSeconds = 1, DatasetIndexWaitSeconds = 0 };
            var seismic = new DdmsService("seismic", FakeOsduPlatform.SeismicRoot, DdmsShape.SeismicStoreV3, DdmsCatalog.SeismicStoreCollections)
            {
                SeismicStore = settings ?? Settings(),
            };
            var flow = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.Ddms, Ddms = [seismic], ProtocolOptions = options }, "seismic");
            Protocol = new OsduDdmsProtocol(client, options, NullLogger.Instance, time: new ProductionTimeSeriesRouteTests.SteppingClock(), routing: DdmsRouting.Of(flow));
            Ledger = new ShapeArtifactLedger(Unit, () => Hook.Count);
        }

        public FakeOsduPlatform Platform { get; }

        public ShapeCallHook Hook { get; }

        public HttpRuntime Runtime { get; }

        public OsduDdmsProtocol Protocol { get; }

        /// <summary>What the worker's ledger holds of the record's unit.</summary>
        public ShapeArtifactLedger Ledger { get; }

        public void Dispose()
        {
            Runtime.Dispose();
            Hook.Dispose();
        }
    }

    private static JsonObject Line(string id = LineId, string name = "Line 001") => FakeOsduPlatform.Record(id, LineKind, new JsonObject
    {
        ["Name"] = name,
        ["DatasetProperties"] = new JsonObject
        {
            ["FileCollectionPath"] = "sd://placeholder/",
            ["FileSourceInfos"] = new JsonArray(new JsonObject { ["FileSource"] = "line.sgy" }),
        },
    });

    /// <summary>Deterministic bytes that differ from offset to offset.</summary>
    private static byte[] Bytes(int length, int seed = 7)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static RafsRouteTests.NamedFiles File(int length = 1000) => new(("line.sgy", Bytes(length)));

    private static DeliveryKey KeyOf(string id) => DeliveryKey.Derive("seismic", [id]);

    /// <summary>One try of the record's pending work; a ledger makes it a try of that unit, resuming what earlier tries reported.</summary>
    private static DeliveryWork Work(
        JsonObject document, ShapeArtifactLedger? ledger, IPayloadSource? files = null, bool metadata = true, long? existing = null, IReadOnlyDictionary<string, string>? state = null)
    {
        var id = document["id"]!.GetValue<string>();
        return new DeliveryWork
        {
            Key = KeyOf(id),
            TargetId = id,
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = files is not null,
            Payload = files,
            ExistingVersion = existing,
            TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
            CompletedSteps = ledger is null
                ? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
                : new Dictionary<string, IReadOnlyDictionary<string, string>>(ledger.Completed, StringComparer.Ordinal),
            StepCompleted = ledger is null ? null : ledger.Listen,
            Unit = ledger?.Unit,
        };
    }

    private static UndoWork Undo(
        ShapeArtifactLedger ledger, string id = LineId, UndoReason reason = UndoReason.Failed, bool keepRecord = false, IReadOnlyDictionary<string, string>? state = null)
        => ledger.Undo(KeyOf(id), id, reason, keepRecord, state);

    private static async Task<IReadOnlyList<UndoResult>> UndoAsync(Rig rig, UndoWork work)
    {
        var results = await rig.Protocol.UndoAsync([work]);
        UndoAnswers.EachOnce([work], results);
        return results;
    }

    /// <summary>The record as a first delivery wrote it, its dataset closed, its files written; what an update starts from.</summary>
    private static async Task<DeliveryOutcome> DeliveredAsync(Rig rig, int length = 1000)
    {
        var first = await rig.Protocol.DeliverAsync(Work(Line(), null, File(length)));
        Assert.True(first.Succeeded, first.Failure?.Message);
        Assert.Empty(rig.Platform.SeismicLocks);
        return first;
    }

    private static string Location(FakeOsduPlatform platform, string path = SdPath)
        => platform.SeismicDatasets[path]["gcsurl"]!.GetValue<string>().Replace("$$", "/", StringComparison.Ordinal);

    private static bool ReadOnly(FakeOsduPlatform platform) => platform.SeismicDatasets[SdPath]["readonly"]!.GetValue<bool>();

    private static Func<HttpRequestMessage, bool> Close => ShapeCallHook.CallWith(HttpMethod.Patch, "close=");

    private static Func<HttpRequestMessage, bool> Registration => ShapeCallHook.Call(HttpMethod.Post, "/dataset/tenant/");

    [Fact]
    public async Task A_registration_that_took_the_lock_reports_the_dataset_and_the_lock_and_the_record_before_any_object_lands()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);

        var outcome = await rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File()));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal($"POST {Base}?path=surveys/north", rig.Hook.Seen[0]);

        // The lock id goes first; every id the registration makes is named before it goes, and settled by its answer, before the
        // credentials are asked for.
        Assert.Equal(["lock", "register-intent", "register", "metadata", "upload", "close"], rig.Ledger.Reports.Select(r => r.Report.Step));
        Assert.Equal([0, 0, 1, 1], rig.Ledger.Reports.Take(4).Select(r => r.Calls));
        Assert.Empty(rig.Ledger.Last("lock").Report.Artifacts);
        var lockId = rig.Ledger.Completed["lock"]["lockId"];
        var intents = rig.Ledger.Last("register-intent").Report.Artifacts;
        Assert.Equal(
            [
                ("seismic-dataset", ArtifactRoles.Objects, (string?)SdPath, (string?)null),
                ("lock", ArtifactRoles.Lock, SdPath, lockId),
                ("record:register", ArtifactRoles.Record, LineId, null),
            ],
            intents.Select(a => (a.Slot, a.Role, a.TargetId, a.Locator)));
        Assert.All(intents, a => Assert.Equal(ArtifactStatus.Intent, a.Status));

        var dataset = rig.Ledger["seismic-dataset"];
        Assert.Equal((ArtifactRoles.Objects, SdPath, Location(platform), ArtifactStatus.Pending), (dataset.Role, dataset.TargetId, dataset.Locator, dataset.State));
        var carried = rig.Ledger["record:register"];
        Assert.Equal((ArtifactStatus.Gone, "the registration answered; the record slot names the record it wrote"), (carried.State, carried.Note));
        var record = rig.Ledger[TargetArtifact.RecordSlot];
        Assert.Equal((ArtifactRoles.Record, LineId, (long?)null, (long?)null, ArtifactStatus.Pending), (record.Role, record.TargetId, record.Version, record.PriorVersion, record.State));

        // The close released the lock, which the ledger then holds as removed by the route.
        var (close, closedAt) = rig.Ledger.Last("close");
        Assert.Equal($"PATCH {Base}?path=surveys/north&close={lockId}", rig.Hook.Seen[closedAt - 1]);
        Assert.Equal(ArtifactStatus.Removed, Assert.Single(close.Artifacts).Status);
        var held = rig.Ledger["lock"];
        Assert.Equal((ArtifactRoles.Lock, SdPath, lockId, ArtifactStatus.Removed, "released by the close"), (held.Role, held.TargetId, held.Locator, held.State, held.Note));
        Assert.Equal(["seismic-dataset", "lock", "record:register", TargetArtifact.RecordSlot], rig.Ledger.Rows.Select(r => r.Slot));
        Assert.Equal(["seismic-dataset", TargetArtifact.RecordSlot], rig.Ledger.Open().Select(i => i.Artifact.Slot));
    }

    [Fact]
    public async Task An_update_of_a_read_only_dataset_reports_the_lock_and_the_lifted_flag_and_the_close_settles_both()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, Settings(readOnly: true));
        var first = await DeliveredAsync(rig);
        Assert.True(ReadOnly(platform));

        var calls = rig.Hook.Count;
        var outcome = await rig.Protocol.DeliverAsync(Work(Line(name: "renamed"), rig.Ledger, File(2000), existing: first.TargetVersion, state: first.Returned));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal([$"PATCH {Base}?path=surveys/north", $"PUT {Base}/lock?path=surveys/north&openmode=write"], rig.Hook.Since(calls).Take(2));

        // The flag is named once it is lifted, the lock before it is asked for and again once taken; the record before the close
        // that carries it, and again after.
        Assert.Equal(
            ["lock", "open-readonly", "open-intent", "open", "upload", "metadata-intent", "close", "metadata"],
            rig.Ledger.Reports.Select(r => r.Report.Step));
        Assert.Equal([calls + 1, calls + 1, calls + 2], new[] { "open-readonly", "open-intent", "open" }.Select(s => rig.Ledger.Last(s).Calls));
        var lockId = rig.Ledger.Completed["lock"]["lockId"];
        Assert.Equal(("readonly", ArtifactStatus.Pending), (Assert.Single(rig.Ledger.Last("open-readonly").Report.Artifacts).Slot, rig.Ledger.Last("open-readonly").Report.Artifacts[0].Status));
        var intent = Assert.Single(rig.Ledger.Last("open-intent").Report.Artifacts);
        Assert.Equal(("lock", ArtifactStatus.Intent, (string?)SdPath, (string?)lockId), (intent.Slot, intent.Status, intent.TargetId, intent.Locator));
        Assert.Equal(["lock"], rig.Ledger.Last("open").Report.Artifacts.Select(a => a.Slot));
        var (carried, carriedAt) = rig.Ledger.Last("metadata-intent");
        Assert.Equal((TargetArtifact.RecordSlot, ArtifactStatus.Intent, ArtifactRoles.Version), (carried.Artifacts.Single().Slot, carried.Artifacts.Single().Status, carried.Artifacts.Single().Role));
        Assert.Equal($"PATCH {Base}?path=surveys/north&close={lockId}", rig.Hook.Seen[carriedAt]);
        Assert.Equal((ArtifactStatus.Removed, lockId, "released by the close"), (rig.Ledger["lock"].State, rig.Ledger["lock"].Locator, rig.Ledger["lock"].Note));
        var flag = rig.Ledger["readonly"];
        Assert.Equal((ArtifactRoles.Lock, SdPath, "readonly", ArtifactStatus.Removed, "set again by the close"), (flag.Role, flag.TargetId, flag.Locator, flag.State, flag.Note));
        var record = rig.Ledger[TargetArtifact.RecordSlot];
        Assert.Equal((ArtifactRoles.Version, (long?)null, first.TargetVersion), (record.Role, record.Version, record.PriorVersion));
        Assert.True(ReadOnly(platform));
    }

    [Fact]
    public async Task A_failed_upload_leaves_the_lock_held_and_the_undo_deletes_the_dataset_with_its_objects_and_removes_the_record()
    {
        var platform = new FakeOsduPlatform();
        platform.FailBlock(3);
        using var rig = new Rig(platform);

        var failed = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File(3 * MiB))));
        Assert.Equal(500, failed.StatusCode);
        var lockId = rig.Ledger.Completed["lock"]["lockId"];
        var location = Location(platform);

        // A try that ends other than held keeps the lock, which the unit names until its undo.
        Assert.Equal(lockId, platform.SeismicLocks[SdPath]);
        Assert.True(platform.SeismicObjects.ContainsKey(location + "/0"));
        Assert.Equal(["seismic-dataset", "lock", TargetArtifact.RecordSlot], rig.Ledger.Open().Select(i => i.Artifact.Slot));

        var calls = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(
            (ArtifactStatus.Removed, $"{SdPath} deleted with its files and its lock (Seismic Store has no reversible delete)"),
            (UndoAnswers.Of(results, "seismic-dataset").Outcome, UndoAnswers.Of(results, "seismic-dataset").Note));
        Assert.Equal(
            (ArtifactStatus.Gone, $"{SdPath} was deleted, and its lock and flags with it"),
            (UndoAnswers.Of(results, "lock").Outcome, UndoAnswers.Of(results, "lock").Note));
        Assert.Equal(
            (ArtifactStatus.Removed, $"{LineId}: removed from OSDU (reversible)"),
            (UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Note));
        Assert.Equal(
            [$"GET {FakeOsduPlatform.SeismicRoot}/svcstatus", $"DELETE {Base}?path=surveys/north", $"GET {Records}/{LineId}", $"POST {Records}/{LineId}:delete"],
            rig.Hook.Since(calls));

        Assert.Empty(platform.SeismicDatasets);
        Assert.Empty(platform.SeismicLocks);
        Assert.DoesNotContain(platform.SeismicObjects.Keys, k => k.StartsWith(location, StringComparison.Ordinal));
        Assert.Contains(LineId, platform.Removed);
    }

    [Fact]
    public async Task A_resumed_try_reopens_the_dataset_under_the_same_slot_and_the_ledger_keeps_one_row_for_its_lock()
    {
        var platform = new FakeOsduPlatform();
        platform.FailBlock(3);
        using var rig = new Rig(platform);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File(3 * MiB))));

        var reports = rig.Ledger.Reports.Count;
        var outcome = await rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File(3 * MiB)));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        // The resumed try opens the dataset it registered under the lock it holds, and its close releases it.
        Assert.Equal(["open-intent", "open", "upload", "close"], rig.Ledger.Reports.Skip(reports).Select(r => r.Report.Step));
        Assert.Equal(["lock"], rig.Ledger.Last("open").Report.Artifacts.Select(a => a.Slot));
        Assert.Equal(["seismic-dataset", "lock", "record:register", TargetArtifact.RecordSlot], rig.Ledger.Rows.Select(r => r.Slot));
        Assert.Equal((ArtifactStatus.Removed, "released by the close"), (rig.Ledger["lock"].State, rig.Ledger["lock"].Note));
        Assert.Equal(ArtifactRoles.Record, rig.Ledger[TargetArtifact.RecordSlot].Role);
        Assert.Empty(platform.SeismicLocks);
    }

    [Fact]
    public async Task A_close_that_fails_leaves_the_lock_and_the_undo_of_an_update_releases_it_and_sets_the_flag_again()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, Settings(readOnly: true));
        var first = await DeliveredAsync(rig);
        rig.Hook.Answer(Close, HttpStatusCode.InternalServerError);

        var failed = await Assert.ThrowsAsync<OsduStatusException>(
            () => rig.Protocol.DeliverAsync(Work(Line(name: "renamed"), rig.Ledger, File(2000), existing: first.TargetVersion, state: first.Returned)));
        Assert.Equal(500, failed.StatusCode);
        Assert.Equal(rig.Ledger.Completed["lock"]["lockId"], platform.SeismicLocks[SdPath]);
        Assert.False(ReadOnly(platform));

        // The record goes with the close, so it was named before the close, which failed without writing it.
        Assert.Equal(["readonly", "lock", TargetArtifact.RecordSlot], rig.Ledger.Open().Select(i => i.Artifact.Slot));
        Assert.Equal(ArtifactStatus.Intent, rig.Ledger[TargetArtifact.RecordSlot].State);
        Assert.Equal("Line 001", platform.Records[LineId]["data"]!["Name"]!.GetValue<string>());

        var lockId = rig.Ledger.Completed["lock"]["lockId"];
        var calls = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger, state: first.Returned));
        Assert.Equal(
            (ArtifactStatus.Removed, $"the write lock on {SdPath} released"),
            (UndoAnswers.Of(results, "lock").Outcome, UndoAnswers.Of(results, "lock").Note));
        Assert.Equal(
            (ArtifactStatus.Removed, $"{SdPath} read-only again"),
            (UndoAnswers.Of(results, "readonly").Outcome, UndoAnswers.Of(results, "readonly").Note));
        var record = UndoAnswers.Of(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Gone, record.Outcome);
        Assert.EndsWith("the one the delivery's write would have replaced, so the write did not land", record.Note, StringComparison.Ordinal);

        // The lock is released under the delivery's own id alone, with an empty patch, before the read-only flag is set again.
        Assert.Equal(
            [$"PATCH {Base}?path=surveys/north&close={lockId}", $"PATCH {Base}?path=surveys/north", $"GET {Records}/versions/{LineId}"],
            rig.Hook.Since(calls));
        Assert.Equal(["{}", "{\"readonly\":true}"], platform.Calls.Where(c => c.Method == HttpMethod.Patch).TakeLast(2).Select(c => c.Body));
        Assert.Empty(platform.SeismicLocks);
        Assert.True(ReadOnly(platform));
        Assert.True(platform.SeismicDatasets.ContainsKey(SdPath));
        Assert.Equal(first.TargetVersion, platform.Records[LineId]["version"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_try_held_after_it_opened_the_dataset_releases_the_lock_itself_and_the_undo_puts_the_read_only_flag_back()
    {
        var platform = new FakeOsduPlatform();
        using var first = new Rig(platform, Settings(readOnly: true));
        var delivered = await DeliveredAsync(first);

        // The flow now says the deployment runs on anthos without naming its store: held once the dataset is open.
        using var rig = new Rig(platform, Settings(readOnly: true) with { Provider = DdmsProvider.Anthos });
        var held = await Assert.ThrowsAsync<RecordHeldException>(
            () => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File(2000), metadata: false, existing: delivered.TargetVersion, state: delivered.Returned)));
        Assert.Contains("runs on anthos and issues a key triple for an S3 store it does not name", held.Message, StringComparison.Ordinal);

        // The release closes the dataset under the delivery's own lock id, with an empty patch.
        Assert.Equal(["lock", "open-readonly", "open-intent", "open", "unlock"], rig.Ledger.Reports.Select(r => r.Report.Step));
        Assert.Equal($"PATCH {Base}?path=surveys/north&close={rig.Ledger.Completed["lock"]["lockId"]}", rig.Hook.Seen[rig.Ledger.Last("unlock").Calls - 1]);
        Assert.Equal("{}", platform.Calls.Last(c => c.Method == HttpMethod.Patch).Body);
        Assert.Equal((ArtifactStatus.Removed, "released when the delivery was held"), (rig.Ledger["lock"].State, rig.Ledger["lock"].Note));
        Assert.Empty(platform.SeismicLocks);
        Assert.False(ReadOnly(platform));

        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Held, state: delivered.Returned));
        Assert.Equal((ArtifactStatus.Removed, $"{SdPath} read-only again"), (Assert.Single(results).Outcome, results[0].Note));
        Assert.True(ReadOnly(platform));
        Assert.Empty(platform.SeismicLocks);
    }

    [Fact]
    public async Task A_lock_release_that_fails_when_the_try_is_held_leaves_the_lock_for_the_undo()
    {
        var platform = new FakeOsduPlatform();
        using var first = new Rig(platform, Settings(readOnly: true));
        var delivered = await DeliveredAsync(first);

        using var rig = new Rig(platform, Settings(readOnly: true) with { Provider = DdmsProvider.Anthos });
        rig.Hook.Answer(Close, HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<RecordHeldException>(
            () => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File(2000), metadata: false, existing: delivered.TargetVersion, state: delivered.Returned)));

        // The release failed after the hold: the lock is still the unit's, and still held.
        Assert.Equal(["lock", "open-readonly", "open-intent", "open"], rig.Ledger.Reports.Select(r => r.Report.Step));
        Assert.Equal(["readonly", "lock"], rig.Ledger.Open().Select(i => i.Artifact.Slot));
        Assert.Equal(rig.Ledger.Completed["lock"]["lockId"], platform.SeismicLocks[SdPath]);

        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Held, state: delivered.Returned));
        Assert.All(results, r => Assert.Equal(ArtifactStatus.Removed, r.Outcome));
        Assert.Empty(platform.SeismicLocks);
        Assert.True(ReadOnly(platform));
    }

    [Fact]
    public async Task On_gc_the_undo_keeps_the_dataset_with_its_files_and_still_releases_its_lock()
    {
        var platform = new FakeOsduPlatform { SeismicProvider = "gc" };
        using var rig = new Rig(platform, Settings("gc"));
        rig.Hook.Answer(Close, HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File())));
        var location = Location(platform);
        Assert.Contains(platform.SeismicObjects.Keys, k => k.StartsWith(location, StringComparison.Ordinal));

        var calls = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger));
        var dataset = UndoAnswers.Of(results, "seismic-dataset");
        Assert.Equal(ArtifactStatus.Kept, dataset.Outcome);
        Assert.Equal(
            $"Seismic Store on gc deletes the files of every dataset in a subproject when one dataset is deleted, so {SdPath}, which the unfinished delivery registered, is left with its files",
            dataset.Note);
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "lock").Outcome);
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal(
            [$"PATCH {Base}?path=surveys/north&close={rig.Ledger.Completed["lock"]["lockId"]}", $"GET {Records}/{LineId}", $"POST {Records}/{LineId}:delete"],
            rig.Hook.Since(calls));
        Assert.True(platform.SeismicDatasets.ContainsKey(SdPath));
        Assert.Contains(platform.SeismicObjects.Keys, k => k.StartsWith(location, StringComparison.Ordinal));
        Assert.Empty(platform.SeismicLocks);
    }

    [Fact]
    public async Task A_dataset_on_a_deployment_whose_provider_cannot_be_told_is_not_deleted_and_answers_failed_alone()
    {
        var platform = new FakeOsduPlatform { SeismicProvider = "oracle" };
        using var rig = new Rig(platform);
        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File())));
        Assert.Contains("names its provider 'oracle'", held.Message, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, rig.Ledger["lock"].State);
        Assert.Equal(["seismic-dataset", TargetArtifact.RecordSlot], rig.Ledger.Open().Select(i => i.Artifact.Slot));

        // Deleting a dataset on gc takes its subproject's files, so a delete waits until the deployment is known, and the record
        // that names the dataset waits with it.
        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Held));
        var dataset = UndoAnswers.Of(results, "seismic-dataset");
        Assert.Equal(ArtifactStatus.Failed, dataset.Outcome);
        Assert.Contains("names its provider 'oracle', which tells the route no object store to upload to", dataset.Note, StringComparison.Ordinal);
        var record = UndoAnswers.Of(results, TargetArtifact.RecordSlot);
        Assert.Equal(ArtifactStatus.Failed, record.Outcome);
        Assert.StartsWith("1 item(s) the delivery made beside the record could not be undone yet (", record.Note, StringComparison.Ordinal);
        Assert.EndsWith("the record is taken back with them, on the next undo", record.Note, StringComparison.Ordinal);
        Assert.True(platform.SeismicDatasets.ContainsKey(SdPath));
        Assert.DoesNotContain(LineId, platform.Removed);
        Assert.DoesNotContain(rig.Hook.Seen, c => c.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Newer_work_takes_the_dataset_over_so_the_undo_leaves_it_and_the_record_and_releases_the_lock()
    {
        var platform = new FakeOsduPlatform();
        platform.FailBlock(3);
        using var rig = new Rig(platform);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File(3 * MiB))));

        var calls = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Abandoned, keepRecord: true));
        Assert.Equal(
            (ArtifactStatus.Superseded, $"the record's newer work takes {SdPath} over, so it is left as it is"),
            (UndoAnswers.Of(results, "seismic-dataset").Outcome, UndoAnswers.Of(results, "seismic-dataset").Note));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "lock").Outcome);
        Assert.Equal(ArtifactStatus.Superseded, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Equal([$"PATCH {Base}?path=surveys/north&close={rig.Ledger.Completed["lock"]["lockId"]}"], rig.Hook.Since(calls));
        Assert.True(platform.SeismicDatasets.ContainsKey(SdPath));
        Assert.Empty(platform.SeismicLocks);
        Assert.DoesNotContain(LineId, platform.Removed);
    }

    [Fact]
    public async Task A_second_undo_does_no_harm_and_never_throws()
    {
        var platform = new FakeOsduPlatform();
        platform.FailBlock(3);
        using var rig = new Rig(platform);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File(3 * MiB))));
        var work = Undo(rig.Ledger);
        await UndoAsync(rig, work);

        // Seismic Store answers the delete of a dataset it no longer holds as it answers any other (osdu/specs/seismic-ddms/INTEGRATION.md section 6.3).
        var again = await UndoAsync(rig, work);
        Assert.Contains(UndoAnswers.Of(again, "seismic-dataset").Outcome, new[] { ArtifactStatus.Removed, ArtifactStatus.Gone });
        Assert.Equal(ArtifactStatus.Gone, UndoAnswers.Of(again, "lock").Outcome);
        Assert.Equal((ArtifactStatus.Gone, $"{LineId}: OSDU no longer holds the record"), (UndoAnswers.Of(again, TargetArtifact.RecordSlot).Outcome, UndoAnswers.Of(again, TargetArtifact.RecordSlot).Note));
        Assert.Empty(platform.SeismicDatasets);
        Assert.Empty(platform.SeismicLocks);
        Assert.Contains(LineId, platform.Removed);
    }

    [Fact]
    public async Task A_dataset_taken_over_from_a_delivery_the_ledger_lost_is_not_the_units_and_its_undo_only_releases_the_lock()
    {
        // A first delivery landed and its outcome was lost: the dataset holds the record, and the ledger knows nothing of it.
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        await DeliveredAsync(rig);
        var location = Location(platform);
        rig.Hook.Answer(Close, HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(name: "renamed"), rig.Ledger, File(2000))));
        Assert.Equal(SeismicStoreShape.TookExisting, rig.Ledger.Completed["register"][SeismicStoreShape.TookValue]);
        Assert.Equal(
            (ArtifactStatus.Gone, "taken over: the dataset held this record before this delivery, so it is not this delivery's to delete"),
            (rig.Ledger["seismic-dataset"].State, rig.Ledger["seismic-dataset"].Note));
        Assert.Equal(
            (ArtifactStatus.Gone, "the registration answered that the dataset exists; it wrote no record"),
            (rig.Ledger["record:register"].State, rig.Ledger["record:register"].Note));
        Assert.Equal(["lock"], rig.Ledger.Last("open").Report.Artifacts.Select(a => a.Slot));
        Assert.Equal(["lock", TargetArtifact.RecordSlot], rig.Ledger.Open().Select(i => i.Artifact.Slot));

        // The record an earlier delivery wrote is older than the unit, and the close that would have written it again never landed.
        platform.Records[LineId]["createTime"] = Unit.StartedUtc.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "lock").Outcome);
        Assert.Equal(ArtifactStatus.Kept, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome);
        Assert.DoesNotContain(rig.Hook.Seen, c => c.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.True(platform.SeismicDatasets.ContainsKey(SdPath));
        Assert.Equal(location, Location(platform));
        Assert.Empty(platform.SeismicLocks);
        Assert.Equal("Line 001", platform.Records[LineId]["data"]!["Name"]!.GetValue<string>());
        Assert.DoesNotContain(LineId, platform.Removed);
    }

    [Fact]
    public async Task A_registration_another_writer_holds_reports_nothing_and_leaves_that_writers_lock()
    {
        var platform = new FakeOsduPlatform();
        platform.SeismicLocks[SdPath] = Elsewhere;
        using var rig = new Rig(platform);

        var busy = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File())));
        Assert.StartsWith($"Seismic Store keeps {SdPath} locked for another writer", busy.Message, StringComparison.Ordinal);

        // The registration made nothing: what it named is settled as gone, and only the lock it asked for stays named.
        Assert.Equal(["lock", "register-intent", "register-refused"], rig.Ledger.Reports.Select(r => r.Report.Step));
        const string Why = "the registration answered that another writer holds the dataset's lock; it made nothing";
        Assert.Equal((ArtifactStatus.Gone, Why), (rig.Ledger["seismic-dataset"].State, rig.Ledger["seismic-dataset"].Note));
        Assert.Equal((ArtifactStatus.Gone, Why), (rig.Ledger["record:register"].State, rig.Ledger["record:register"].Note));
        Assert.Equal(["lock"], rig.Ledger.Open().Select(i => i.Artifact.Slot));

        // The undo releases a lock held under the delivery's id alone, which the other writer's is not.
        var calls = rig.Hook.Count;
        var results = await UndoAsync(rig, Undo(rig.Ledger));
        var held = Assert.Single(results);
        Assert.Equal(ArtifactStatus.Gone, held.Outcome);
        Assert.StartsWith($"Seismic Store holds no lock on {SdPath} under this delivery's id", held.Note, StringComparison.Ordinal);
        Assert.Equal([$"PATCH {Base}?path=surveys/north&close={rig.Ledger.Completed["lock"]["lockId"]}"], rig.Hook.Since(calls));
        Assert.Equal(Elsewhere, platform.SeismicLocks[SdPath]);
        Assert.Empty(platform.SeismicDatasets);
    }

    [Fact]
    public async Task A_dataset_of_another_record_is_never_the_units_and_its_undo_deletes_nothing()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        await DeliveredAsync(rig);
        platform.SeismicDatasets[SdPath]["seismicmeta_guid"] = "dev:dataset--FileCollection.SEGY:someone-else";

        var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File())));
        Assert.StartsWith($"the dataset {SdPath} already exists and belongs to dev:dataset--FileCollection.SEGY:someone-else", held.Message, StringComparison.Ordinal);
        Assert.Equal(["lock", "register-intent", "register-refused"], rig.Ledger.Reports.Select(r => r.Report.Step));
        Assert.Equal(ArtifactStatus.Gone, rig.Ledger["seismic-dataset"].State);
        Assert.StartsWith("the dataset exists and belongs to dev:dataset--FileCollection.SEGY:someone-else", rig.Ledger["seismic-dataset"].Note, StringComparison.Ordinal);
        Assert.Equal(["lock"], rig.Ledger.Open().Select(i => i.Artifact.Slot));

        var location = Location(platform);
        var results = await UndoAsync(rig, Undo(rig.Ledger, reason: UndoReason.Held));
        Assert.NotEqual(ArtifactStatus.Failed, Assert.Single(results).Outcome);
        Assert.DoesNotContain(rig.Hook.Seen, c => c.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.Equal("dev:dataset--FileCollection.SEGY:someone-else", platform.SeismicDatasets[SdPath]["seismicmeta_guid"]!.GetValue<string>());
        Assert.Contains(platform.SeismicObjects.Keys, k => k.StartsWith(location, StringComparison.Ordinal));
        Assert.DoesNotContain(LineId, platform.Removed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_record_change_alone_is_reported_as_a_version_and_its_undo_writes_the_earlier_one_back_only_when_it_landed(bool seismicWritesStorage)
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var first = await DeliveredAsync(rig);
        platform.SeismicSkipsStorage = !seismicWritesStorage;

        // The patch goes; the read of the version it made fails, before a record Seismic Store did not write is written through Storage.
        rig.Hook.Answer(ShapeCallHook.Call(HttpMethod.Get, Records + "/" + LineId), HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(name: "renamed"), rig.Ledger, existing: first.TargetVersion, state: first.Returned)));
        var record = rig.Ledger[TargetArtifact.RecordSlot];
        Assert.Equal((ArtifactRoles.Version, (long?)null, first.TargetVersion), (record.Role, record.Version, record.PriorVersion));
        Assert.DoesNotContain(rig.Hook.Seen, c => c.Contains("unlock", StringComparison.Ordinal) || c.Contains("/lock", StringComparison.Ordinal));

        var result = Assert.Single(await UndoAsync(rig, Undo(rig.Ledger, state: first.Returned)));
        var name = platform.Records[LineId]["data"]!["Name"]!.GetValue<string>();
        if (seismicWritesStorage)
        {
            Assert.Equal(ArtifactStatus.Restored, result.Outcome);
            Assert.StartsWith($"{LineId}: version {first.TargetVersion!.Value.ToString(CultureInfo.InvariantCulture)} written back as version ", result.Note, StringComparison.Ordinal);
            Assert.Equal("Line 001", name);
        }
        else
        {
            // Seismic Store left the record as it was: nothing landed, so nothing is written again.
            Assert.Equal(ArtifactStatus.Gone, result.Outcome);
            Assert.EndsWith("the one the delivery's write would have replaced, so the write did not land", result.Note, StringComparison.Ordinal);
            Assert.Equal((first.TargetVersion, "Line 001"), ((long?)platform.Records[LineId]["version"]!.GetValue<long>(), name));
        }
    }

    [Theory]
    [InlineData(-5_000_000, ArtifactStatus.Kept)]
    [InlineData(-4, ArtifactStatus.Removed)]
    public async Task A_record_osdu_created_before_the_unit_began_is_kept_since_seismic_store_never_says_which_version_it_wrote(int minutesFromStart, ArtifactStatus expected)
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.Answer(Close, HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File())));
        platform.Records[LineId]["createTime"] = Unit.StartedUtc.AddMinutes(minutesFromStart).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "seismic-dataset").Outcome);
        var record = UndoAnswers.Of(results, TargetArtifact.RecordSlot);
        Assert.Equal(expected, record.Outcome);
        if (expected == ArtifactStatus.Kept)
        {
            Assert.StartsWith($"{LineId}: OSDU created the record at ", record.Note, StringComparison.Ordinal);
            Assert.EndsWith("which version it held before is not known; nothing was put back", record.Note, StringComparison.Ordinal);
            Assert.DoesNotContain(LineId, platform.Removed);
        }
        else
        {
            Assert.Contains(LineId, platform.Removed);
        }
    }

    [Fact]
    public async Task A_failed_undo_call_answers_failed_for_its_own_artifact_alone_and_the_next_undo_finishes()
    {
        // An update whose close failed: the release is refused once.
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, Settings(readOnly: true));
        var first = await DeliveredAsync(rig);
        rig.Hook.Answer(Close, HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<OsduStatusException>(
            () => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File(2000), metadata: false, existing: first.TargetVersion, state: first.Returned)));

        rig.Hook.Answer(Close, HttpStatusCode.InternalServerError);
        var refused = await UndoAsync(rig, Undo(rig.Ledger, state: first.Returned));
        Assert.Equal(ArtifactStatus.Failed, UndoAnswers.Of(refused, "lock").Outcome);
        Assert.Contains("HTTP 500", UndoAnswers.Of(refused, "lock").Note, StringComparison.Ordinal);
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(refused, "readonly").Outcome);
        rig.Ledger.Settle(refused);

        var retried = await UndoAsync(rig, Undo(rig.Ledger, state: first.Returned));
        Assert.Equal(("lock", ArtifactStatus.Removed), (Assert.Single(retried).Item.Artifact.Slot, retried[0].Outcome));
        Assert.Empty(platform.SeismicLocks);
        Assert.True(ReadOnly(platform));

        // A create whose upload failed: the dataset's delete is refused once, the lock is released instead, and the record that
        // names the dataset waits with it.
        var created = new FakeOsduPlatform();
        created.FailBlock(3);
        using var second = new Rig(created);
        await Assert.ThrowsAsync<OsduStatusException>(() => second.Protocol.DeliverAsync(Work(Line(), second.Ledger, File(3 * MiB))));
        second.Hook.Answer(ShapeCallHook.Call(HttpMethod.Delete, "/dataset/tenant/"), HttpStatusCode.InternalServerError);
        var deleteRefused = await UndoAsync(second, Undo(second.Ledger));
        Assert.Equal(ArtifactStatus.Failed, UndoAnswers.Of(deleteRefused, "seismic-dataset").Outcome);
        Assert.Equal((ArtifactStatus.Removed, $"the write lock on {SdPath} released"), (UndoAnswers.Of(deleteRefused, "lock").Outcome, UndoAnswers.Of(deleteRefused, "lock").Note));
        Assert.Equal(ArtifactStatus.Failed, UndoAnswers.Of(deleteRefused, TargetArtifact.RecordSlot).Outcome);
        Assert.True(created.SeismicDatasets.ContainsKey(SdPath));
        Assert.Empty(created.SeismicLocks);
        Assert.DoesNotContain(LineId, created.Removed);
        second.Ledger.Settle(deleteRefused);

        var deleted = await UndoAsync(second, Undo(second.Ledger));
        Assert.Equal(
            [("seismic-dataset", ArtifactStatus.Removed), (TargetArtifact.RecordSlot, ArtifactStatus.Removed)],
            deleted.Select(r => (r.Item.Artifact.Slot, r.Outcome)));
        Assert.Empty(created.SeismicDatasets);
        Assert.Contains(LineId, created.Removed);
    }

    [Fact]
    public async Task An_undo_of_several_records_answers_every_artifact_of_each_once()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var other = new ShapeArtifactLedger(new DeliveryUnit(Guid.Parse("0192aa00-0000-7000-8000-00000000535f"), Unit.StartedUtc), () => rig.Hook.Count);
        rig.Hook.Answer(Close, HttpStatusCode.InternalServerError, times: 2);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File())));
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(SecondId, "Line 002"), other, File())));

        // An artifact of a kind the shape never makes is answered too.
        var strange = new UndoItem(9101, TargetArtifact.Created("points:1", ArtifactRoles.Points, LineId, locator: "OIL=1"), Unit.Id, Unit.StartedUtc);
        var first = Undo(rig.Ledger);
        var works = new[] { first with { Items = [.. first.Items, strange] }, Undo(other, SecondId) };
        var results = await rig.Protocol.UndoAsync(works);
        UndoAnswers.EachOnce(works, results);
        Assert.Equal(
            (ArtifactStatus.Kept, "the Seismic Store shape makes nothing of this kind beside a record"),
            (Assert.Single(results, r => r.Item.ArtifactId == 9101).Outcome, results.Single(r => r.Item.ArtifactId == 9101).Note));
        Assert.Equal(6, results.Count(r => r.Outcome is ArtifactStatus.Removed or ArtifactStatus.Gone));
        Assert.Empty(platform.SeismicDatasets);
        Assert.Empty(platform.SeismicLocks);
        Assert.Contains(LineId, platform.Removed);
        Assert.Contains(SecondId, platform.Removed);
        Assert.False(platform.SeismicDatasets.ContainsKey(SecondPath));
    }

    [Fact]
    public async Task An_undo_releases_only_its_own_lock_and_never_one_another_writer_took_since()
    {
        // The unit opened the dataset; by its close the lock had lapsed and another writer held the dataset.
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var first = await DeliveredAsync(rig);
        rig.Hook.Before(Close, () => platform.SeismicLocks[SdPath] = Elsewhere);
        var refused = await Assert.ThrowsAsync<DeliveryException>(
            () => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File(2000), metadata: false, existing: first.TargetVersion, state: first.Returned)));
        Assert.StartsWith($"Seismic Store refused to close {SdPath} under this delivery's lock", refused.Message, StringComparison.Ordinal);
        Assert.Equal(["lock"], rig.Ledger.Open().Select(i => i.Artifact.Slot));

        var results = await UndoAsync(rig, Undo(rig.Ledger, state: first.Returned));

        // The unit's lock is no longer held; the other writer's is not the unit's to release.
        Assert.Equal(Elsewhere, platform.SeismicLocks.GetValueOrDefault(SdPath));
        Assert.Equal(ArtifactStatus.Gone, Assert.Single(results).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_only_flag_lifted_before_its_lock_was_answered_is_the_units_and_its_undo_sets_it_again(bool lockAnswerLost)
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, Settings(readOnly: true));
        var first = await DeliveredAsync(rig);
        if (lockAnswerLost)
        {
            rig.Hook.LoseAnswer(ShapeCallHook.Call(HttpMethod.Put, "/lock"));
        }
        else
        {
            platform.SeismicLocks[SdPath] = Elsewhere;
        }

        var failed = await Assert.ThrowsAsync<DeliveryException>(
            () => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File(2000), metadata: false, existing: first.TargetVersion, state: first.Returned)));
        Assert.Contains(lockAnswerLost ? "HTTP transport failure calling PUT" : "locked for another reader or writer", failed.Message, StringComparison.Ordinal);

        // The flag was lifted before the lock was asked for, and the lock, when its answer was lost, was taken.
        Assert.False(ReadOnly(platform));
        Assert.Equal(lockAnswerLost ? rig.Ledger.Completed["lock"]["lockId"] : Elsewhere, platform.SeismicLocks[SdPath]);

        await UndoAsync(rig, Undo(rig.Ledger, state: first.Returned));
        Assert.True(ReadOnly(platform), "the read-only flag the unit lifted is set again by its undo");
        Assert.Equal(lockAnswerLost ? null : Elsewhere, platform.SeismicLocks.GetValueOrDefault(SdPath));
    }

    [Fact]
    public async Task A_registration_whose_answer_was_lost_is_undone_all_the_same()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.LoseAnswer(Registration);

        var lost = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File())));
        Assert.StartsWith("HTTP transport failure calling POST", lost.Message, StringComparison.Ordinal);

        // Seismic Store registered the dataset under the unit's lock and wrote the record through Storage.
        Assert.True(platform.SeismicDatasets.ContainsKey(SdPath));
        Assert.Equal(rig.Ledger.Completed["lock"]["lockId"], platform.SeismicLocks[SdPath]);
        Assert.True(platform.Records.ContainsKey(LineId));

        await UndoAsync(rig, Undo(rig.Ledger));
        Assert.False(platform.SeismicDatasets.ContainsKey(SdPath), "the dataset the lost registration made is the unit's to delete");
        Assert.Empty(platform.SeismicLocks);
        Assert.Contains(LineId, platform.Removed);
    }

    [Fact]
    public async Task A_dataset_an_earlier_try_registered_without_hearing_back_stays_the_units_when_a_later_try_takes_it_over()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        rig.Hook.LoseAnswer(Registration);
        await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File())));

        // The lock lapsed before the next try of the unit, which meets the dataset as one that exists and holds the record.
        platform.SeismicLocks.Remove(SdPath);
        rig.Hook.Answer(Close, HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Line(), rig.Ledger, File())));
        Assert.Equal(SeismicStoreShape.TookExisting, rig.Ledger.Completed["register"][SeismicStoreShape.TookValue]);
        var dataset = rig.Ledger["seismic-dataset"];
        Assert.Equal(
            (ArtifactStatus.Pending, "registered by an earlier try of this delivery whose answer was lost, and taken over"),
            (dataset.State, dataset.Note));
        Assert.Equal(ArtifactStatus.Gone, rig.Ledger["record:register"].State);
        Assert.Equal(["seismic-dataset", "lock", TargetArtifact.RecordSlot], rig.Ledger.Open().Select(i => i.Artifact.Slot));

        var results = await UndoAsync(rig, Undo(rig.Ledger));
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, "seismic-dataset").Outcome);
        Assert.Equal(ArtifactStatus.Gone, UndoAnswers.Of(results, "lock").Outcome);
        Assert.Equal(ArtifactStatus.Removed, UndoAnswers.Of(results, TargetArtifact.RecordSlot).Outcome);
        Assert.Empty(platform.SeismicDatasets);
        Assert.Empty(platform.SeismicLocks);
        Assert.Contains(LineId, platform.Removed);
    }

    [Fact]
    public async Task A_close_whose_answer_was_lost_after_it_wrote_the_record_has_the_record_written_back_by_the_undo()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var first = await DeliveredAsync(rig);
        rig.Hook.LoseAnswer(Close);

        var lost = await Assert.ThrowsAsync<DeliveryException>(
            () => rig.Protocol.DeliverAsync(Work(Line(name: "renamed"), rig.Ledger, File(2000), existing: first.TargetVersion, state: first.Returned)));
        Assert.StartsWith("HTTP transport failure calling PATCH", lost.Message, StringComparison.Ordinal);

        // The close released the lock and wrote the record Seismic Store was given with it.
        Assert.Empty(platform.SeismicLocks);
        Assert.Equal("renamed", platform.Records[LineId]["data"]!["Name"]!.GetValue<string>());

        await UndoAsync(rig, Undo(rig.Ledger, state: first.Returned));
        Assert.Equal("Line 001", platform.Records[LineId]["data"]!["Name"]!.GetValue<string>());
    }
}
