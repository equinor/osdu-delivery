using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A pipeline's cache steps on SQL Server (osdu/docs/reference/concepts/partition-cache.md, Pipelines bring the cache up to
/// date): what a delivery adds stays beside what it added before and survives a cache flow's capture that does not find it,
/// until a refresh finds OSDU's storage no longer holds it; what a run finds OSDU no longer holds leaves whoever held it; the
/// ledger names what a run changed; the after step reads it back into the types it names, and only those; the before step
/// captures each declaring flow's records as that flow's own; and a run's steps are refused, or do not fail it, as they should.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CacheStepsStoreTests : IDisposable
{
    private const string Scope = "dev";
    private const string LogKind = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string UnitKind = "osdu:wks:reference-data--UnitOfMeasure:1.0.0";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task What_a_delivery_adds_stays_until_a_refresh_finds_storage_no_longer_holds_it()
    {
        var store = _db.Caches();
        var capture = new CacheCapture(null, "tests", "tests");
        const string Delivery = "delivery:welldb-unit-delivery";

        await store.MergeAsync(Scope, "reference-cache", [Units(("m", "metre"), ("ft", "foot"))], capture, T(0));
        await store.MergeAsync(Scope, Delivery, [Units(("s", "second"))], capture with { Adds = true }, T(1));
        var added = await store.MergeAsync(Scope, Delivery, [Units(("kg", "kilogram"))], capture with { Adds = true }, T(2));

        // A delivery's later records go beside its earlier ones; it holds both.
        Assert.Equal(["ft", "kg", "m", "s"], Ids(added.Snapshot));
        Assert.Equal(["kg", "s"], await HeldAsync(Delivery));
        Assert.Equal(["kg", "s"], (await store.HeldByDeliveriesAsync(Scope, "UnitOfMeasure", ["ft", "kg", "m", "s", "x"])).Order(StringComparer.Ordinal));

        // A cache flow's capture that finds only the metre takes out the foot, which only it held, and keeps what the delivery holds.
        var refreshed = await store.MergeAsync(Scope, "reference-cache", [Units(("m", "metre"))], capture, T(3));
        Assert.Equal(["kg", "m", "s"], Ids(refreshed.Snapshot));

        // Told storage no longer holds the second, the next capture takes it out; the kilogram, which storage holds, stays.
        var gone = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase) { ["UnitOfMeasure"] = new HashSet<string> { "s" } };
        var cleaned = await store.MergeAsync(Scope, "reference-cache", [Units(("m", "metre"))], capture with { Gone = gone }, T(4));
        Assert.Equal(["kg", "m"], Ids(cleaned.Snapshot));
        Assert.Equal(["kg"], await HeldAsync(Delivery));
    }

    [Fact]
    public async Task A_refresh_asks_storage_only_about_what_deliveries_hold_and_it_did_not_find()
    {
        var store = _db.Caches();
        var capture = new CacheCapture(null, "tests", "tests");
        await store.MergeAsync(Scope, "reference-cache", [Units(("m", "metre"), ("ft", "foot"))], capture, T(0));
        await store.MergeAsync(Scope, "delivery:welldb-unit-delivery", [Units(("kg", "kilogram"), ("s", "second"))], capture with { Adds = true }, T(1));

        using var platform = new FakeOsduPlatform();
        platform.Put(FakeOsduPlatform.Record("kg", "osdu:wks:reference-data--UnitOfMeasure:1.0.0"));
        using var rig = new StorageRig(platform);

        // The capture found the metre; the foot was only the cache flow's, so storage is not asked about it.
        var gone = await CacheRefresher.GoneAsync(store, Scope, rig.Storage, [Units(("m", "metre"))], NullLogger.Instance, CancellationToken.None);

        Assert.Equal(["s"], Assert.Single(gone).Value.Order(StringComparer.Ordinal));
        var asked = platform.Calls.Where(r => r.Uri.AbsolutePath.EndsWith("/query/records", StringComparison.Ordinal)).ToList();
        var body = JsonNode.Parse(Assert.Single(asked).Body!)!["records"]!.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal);
        Assert.Equal(["kg", "s"], body);
    }

    [Fact]
    public async Task What_OSDU_no_longer_holds_leaves_the_type_whoever_held_it()
    {
        var store = _db.Caches();
        var capture = new CacheCapture(null, "tests", "tests");
        const string Delivery = "delivery:welldb-unit-delivery";
        await store.MergeAsync(Scope, "reference-cache", [Units(("m", "metre"), ("ft", "foot"))], capture, T(0));
        await store.MergeAsync(Scope, Delivery, [Units(("s", "second"))], capture with { Adds = true }, T(1));

        // A run found the foot (a cache flow's) and the second (a delivery's) gone from OSDU: both leave, with every hold.
        IReadOnlySet<string> gone = new HashSet<string>(StringComparer.Ordinal) { "ft", "s" };
        var removed = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase) { ["UnitOfMeasure"] = gone };
        var written = await store.MergeAsync(Scope, Delivery, [Units()], capture with { Adds = true, Removed = removed }, T(2));

        Assert.Equal(["m"], Ids(written.Snapshot));
        Assert.Empty(await HeldAsync(Delivery));
        Assert.Equal(["m"], await HeldAsync("reference-cache"));
    }

    [Fact]
    public async Task The_after_step_reads_back_what_the_run_changed_and_takes_out_what_OSDU_no_longer_holds()
    {
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        // The run renders against the sample cache, as every engine suite does; the step writes into the partition's cache on SQL Server.
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);

        await _db.DeclareCacheAsync(Scope, "welldb-logs-cache", new ReferenceTypeSpec
        {
            Name = "WellLog",
            EntityType = "work-product-component--WellLog",
            Kind = "osdu:wks:work-product-component--WellLog:*",
            Fields = [new ReferenceFieldSpec("data.Name")],
        });
        await _db.DeclareCacheAsync(Scope, "welldb-wellbore-cache", new ReferenceTypeSpec
        {
            Name = "Wellbore",
            EntityType = "master-data--Wellbore",
            Kind = "osdu:wks:master-data--Wellbore:*",
            Fields = [new ReferenceFieldSpec("data.FacilityName")],
        });

        var started = _clock.GetUtcNow().UtcDateTime;
        _clock.Advance(TimeSpan.FromMinutes(1));
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        var worker = new DeliveryWorker(ledger, engine.Payloads, engine.Stores, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
        var work = await worker.DrainAsync(intake.Submission.SubmissionId);
        await runtime.Intake.CompleteAsync(intake.Submission.SubmissionId, runtime.Flow.Id);
        Assert.True(work.Delivered > 1);

        // The ledger names what the run changed in OSDU, and nothing changed before it.
        var changed = await ledger.ChangedSinceAsync(runtime.Flow.Id, started, 1_000);
        Assert.Equal(work.Delivered, changed.Count);
        Assert.Empty(await ledger.ChangedSinceAsync(runtime.Flow.Id, _clock.GetUtcNow().UtcDateTime.AddMinutes(1), 1_000));

        // OSDU holds every record but one, which a cleanup took meanwhile.
        using var platform = new FakeOsduPlatform();
        foreach (var id in changed.Skip(1))
        {
            platform.Put(FakeOsduPlatform.Record(id, LogKind, new JsonObject { ["Name"] = $"log {id}" }));
        }

        using var rig = new StorageRig(platform);
        var caches = _db.Caches();
        var declaration = await caches.DeclarationAsync(Scope);
        var keeper = new CacheKeeper(engine with { Cache = caches }, NullLogger.Instance);
        var after = await keeper.AfterAsync(
            Scope, [declaration.Step("WellLog")!], runtime.Flow.Id, runtime.Flow.LedgerName, started, _ => Task.FromResult(rig.Client),
            OsduRecordProtocol.DefaultVerifyBatchPath, Guid.NewGuid(), "tests", null, false, CancellationToken.None);

        Assert.NotNull(after);
        Assert.Equal(["WellLog"], after.Types);
        Assert.Equal((changed.Count, changed.Count - 1, 1, true), (after.Records, after.Read, after.Removed, after.Written));
        var cached = await caches.LoadAsync(Scope, after.Version!);
        var logs = cached!.Type("WellLog")!;
        Assert.Equal(changed.Count - 1, logs.Items.Count);
        Assert.All(logs.Items, item => Assert.Equal($"log {item.Id}", item.Fields["Name"].Text));
        Assert.Null(cached.Type("Wellbore"));
        Assert.Equal(changed.Count - 1, (await HeldAsync(CacheHolders.Delivery(runtime.Flow.LedgerName), "WellLog")).Count);

        // A step naming no type does nothing, and does not read storage.
        Assert.Null(await keeper.AfterAsync(
            Scope, [], runtime.Flow.Id, runtime.Flow.LedgerName, started, _ => throw new InvalidOperationException("storage is not read"),
            OsduRecordProtocol.DefaultVerifyBatchPath, null, "tests", null, false, CancellationToken.None));
    }

    [Fact]
    public async Task The_before_step_captures_each_declaring_flow_s_records_as_that_flow_s_own()
    {
        var caches = _db.Caches();
        await _db.DeclareCacheAsync(Scope, "reference-cache", new ReferenceTypeSpec
        {
            Name = "UnitOfMeasure",
            EntityType = "reference-data--UnitOfMeasure",
            Kind = "osdu:wks:reference-data--UnitOfMeasure:*",
            Fields = [new ReferenceFieldSpec("data.Code")],
        });

        using var platform = new FakeOsduPlatform();
        platform.Put(FakeOsduPlatform.Record("dev:reference-data--UnitOfMeasure:m", UnitKind, new JsonObject { ["Code"] = "m" }));
        platform.Put(FakeOsduPlatform.Record("dev:reference-data--UnitOfMeasure:ft", UnitKind, new JsonObject { ["Code"] = "ft" }));

        // The fake search answers the units' kind with their ids, as the search index holds them.
        platform.Search = (kind, _) => kind.Contains("reference-data--UnitOfMeasure", StringComparison.Ordinal)
            ? ["dev:reference-data--UnitOfMeasure:m", "dev:reference-data--UnitOfMeasure:ft"]
            : [];
        var declaration = await caches.DeclarationAsync(Scope);
        var keeper = new CacheKeeper(Samples.Engine(_db.Ledger(_clock), _clock, cache: caches), NullLogger.Instance);

        var before = await keeper.BeforeAsync(
            Scope, [declaration.Step("UnitOfMeasure")!], "welldb-wells", token => Connect(platform, token),
            OsduRecordProtocol.DefaultVerifyBatchPath, Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((2, true), (before.Records, before.Written));
        var cached = await caches.LoadAsync(Scope, before.Version!);
        Assert.Equal(["dev:reference-data--UnitOfMeasure:ft", "dev:reference-data--UnitOfMeasure:m"], cached!.Type("UnitOfMeasure")!.Items.Select(i => i.Id).Order(StringComparer.Ordinal));
        Assert.Equal(2, (await HeldAsync("reference-cache")).Count);

        // Taken again with nothing new in OSDU, the step writes no version.
        var again = await keeper.BeforeAsync(
            Scope, [declaration.Step("UnitOfMeasure")!], "welldb-wells", token => Connect(platform, token),
            OsduRecordProtocol.DefaultVerifyBatchPath, Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.False(again.Written);
        Assert.Equal(before.Version, again.Version);
    }

    [Fact]
    public async Task A_before_step_on_a_type_only_its_cache_flow_s_parameters_can_search_is_refused()
    {
        var caches = _db.Caches();
        await _db.DeclareCacheAsync(Scope, "reference-cache", new ReferenceTypeSpec
        {
            Name = "UnitOfMeasure",
            EntityType = "reference-data--UnitOfMeasure",
            Kind = "osdu:wks:reference-data--UnitOfMeasure:*",
            Query = "data.Source:\"{source}\"",
            Fields = [new ReferenceFieldSpec("data.Code")],
        });
        var declaration = await caches.DeclarationAsync(Scope);
        var keeper = new CacheKeeper(Samples.Engine(_db.Ledger(_clock), _clock, cache: caches), NullLogger.Instance);

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => keeper.BeforeAsync(
            Scope, [declaration.Step("UnitOfMeasure")!], "welldb-wells", _ => throw new InvalidOperationException("OSDU is not searched"),
            OsduRecordProtocol.DefaultVerifyBatchPath, null, "tests", CancellationToken.None));
        Assert.Contains("takes the flow's parameters", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deliver_run_ends_with_its_after_step_and_a_step_that_cannot_reach_storage_does_not_fail_it()
    {
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var caches = _db.Caches();
        await Samples.ImportSampleCacheAsync(caches);
        await _db.DeclareCacheAsync(Scope, "welldb-logs-cache", new ReferenceTypeSpec
        {
            Name = "WellLog",
            EntityType = "work-product-component--WellLog",
            Kind = "osdu:wks:work-product-component--WellLog:*",
            Fields = [new ReferenceFieldSpec("data.Name")],
        });
        var engine = Samples.Engine(ledger, _clock, sources: tables, cache: caches) with { Protocols = new FakeProtocolFactory(new FakeProtocol()) };
        var flow = Samples.LocalFlow(_root) with { Cache = new Model.FlowCacheSteps { After = new Model.CacheStepEntries { Named = ["WellLog"] } } };
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);

        // The sample flow's target is a placeholder no one answers on: the records land through the fake protocol, and the
        // step that reads them back cannot reach storage. The run stands, and says what the step could not do.
        var run = await runtime.RunAsync(force: false);
        Assert.True(run.Work.Delivered > 0);
        var after = Assert.IsType<CacheStepOutcome>(run.CacheAfter);
        Assert.Equal((CacheStepOutcome.AfterStep, Scope), (after.Step, after.Scope));
        Assert.Contains("A refresh of the cache flows brings the records in", after.Skipped, StringComparison.Ordinal);
        Assert.Null(run.CacheBefore);

        // A run that sends nothing has nothing to put into the cache.
        _clock.Advance(TimeSpan.FromHours(1));
        var idle = await runtime.RunAsync(force: false);
        Assert.Equal(0, idle.Work.Delivered);
        Assert.Null(idle.CacheAfter);
    }

    [Fact]
    public async Task A_run_naming_a_type_the_partition_s_cache_does_not_hold_stops_before_it_reads_anything()
    {
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var caches = _db.Caches();
        await Samples.ImportSampleCacheAsync(caches);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables, cache: caches) with { Protocols = new FakeProtocolFactory(protocol) };
        var flow = Samples.LocalFlow(_root) with { Cache = new Model.FlowCacheSteps { After = new Model.CacheStepEntries { Named = ["NoSuchType"] } } };
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => runtime.RunAsync(force: false));
        Assert.Contains("names 'NoSuchType'", refused.Message, StringComparison.Ordinal);
        Assert.Empty(protocol.Deliveries);
    }

    private static Task<OsduConnection> Connect(FakeOsduPlatform platform, CancellationToken ct)
        => OsduConnection.CreateAsync(
            FakeOsduPlatform.Endpoint,
            new Model.TargetAuth { Type = Model.TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = Scope },
            new Model.FlowReliability { Retry = new Model.FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new Core.Secrets.SecretResolver([new Core.Secrets.EnvSecretProvider()]),
            handler: platform,
            allowLoopback: true,
            ct: ct);

    private static DateTimeOffset T(int hours) => new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero).AddHours(hours);

    private static ReferenceType Units(params (string Id, string Code)[] items)
        => new("UnitOfMeasure", "reference-data--UnitOfMeasure", items.Select(i => ReferenceItem.FromText(i.Id, new Dictionary<string, string> { ["Code"] = i.Code })));

    private static List<string> Ids(ReferenceSnapshot snapshot)
        => [.. snapshot.Type("UnitOfMeasure")!.Items.Select(i => i.Id).Order(StringComparer.Ordinal)];

    private async Task<List<string>> HeldAsync(string holder, string type = "UnitOfMeasure")
    {
        await using var db = _db.CreateDbContext();
        return await db.DeliveryCacheMembers.Where(m => m.Scope == Scope && m.TypeName == type && m.FlowName == holder)
            .Select(m => m.RecordId).OrderBy(id => id).ToListAsync();
    }

    /// <summary>The storage service of the fake platform, as a delivery's connection reaches it.</summary>
    private sealed class StorageRig : IDisposable
    {
        private readonly Http.HttpRuntime _runtime;

        public StorageRig(FakeOsduPlatform platform)
        {
            _runtime = new Http.HttpRuntime(
                new Model.FlowReliability { Retry = new Model.FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new Core.Secrets.SecretResolver([new Core.Secrets.EnvSecretProvider()]), TimeProvider.System, platform, allowLoopback: true);
            Client = new OsduHttpClient(
                _runtime, FakeOsduPlatform.Endpoint, new Model.TargetAuth { Type = Model.TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = Scope });
            Storage = new StorageRecords(Client, OsduRecordProtocol.DefaultVerifyBatchPath);
        }

        public OsduHttpClient Client { get; }

        public StorageRecords Storage { get; }

        public void Dispose() => _runtime.Dispose();
    }
}
