using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The partition cache's retention as the store applies it on SQL Server (osdu/docs/reference/concepts/partition-cache.md,
/// Retention): the records of the versions a partition no longer needs are pruned, each pruned version's row stays with what
/// it changed, so the history reads the same after; a pinned version is kept; nothing is pruned while a delivery flow's pin
/// is not recorded; the longest retention of a partition's flows applies; a pass cut short is finished by the next; merges
/// running beside a retention lose nothing a kept version holds; and every refresh and import applies it.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CacheRetentionStoreTests : IDisposable
{
    private const string Scope = "dev";
    private const string Flow = "welldb-osdu-00-reference-cache";

    private static readonly DateTimeOffset Start = new(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);
    private static readonly CacheCapture Capture = new(null, "tests", "seeded");

    private readonly OsduTestDatabase _catalog = new();

    public void Dispose() => _catalog.Dispose();

    [Fact]
    public async Task A_retention_prunes_the_versions_the_partition_no_longer_needs_and_the_history_reads_the_same()
    {
        var labels = new List<string>
        {
            await CaptureAsync(0, Uom("m", "metre"), Uom("ft", "foot"), Wellbore("A")),
            await CaptureAsync(1, Uom("m", "Metre"), Uom("ft", "foot"), Wellbore("A")),
            await CaptureAsync(2, Uom("m", "Metre"), Uom("ft", "foot"), Uom("km", "kilometre"), Wellbore("A")),
            await CaptureAsync(3, Uom("m", "Metre"), Uom("km", "kilometre"), Wellbore("A")),
            await CaptureAsync(4, Uom("m", "metre"), Uom("km", "kilometre"), Wellbore("A")),
            await CaptureAsync(5, Uom("m", "metre"), Uom("km", "kilometre"), Uom("cm", "centimetre"), Wellbore("A")),
        };
        var before = await HistoryAsync(null);
        var unitsBefore = await HistoryAsync("UnitOfMeasure");
        Assert.Equal(7, await RowCountAsync());

        // Eight days in, a retention of two days keeps the current version and the one it replaced; the four before them
        // were replaced more than two days ago.
        var now = Start.AddDays(8);
        var outcome = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 2, now, "tests"));

        Assert.Equal((2, 2, 3L), (outcome.RetentionDays, outcome.Kept, outcome.RowsRemoved));
        Assert.Equal(labels.Take(4), outcome.Pruned);
        Assert.Null(outcome.Failure);
        Assert.Null(outcome.Deferred);

        // The rows left are those a kept version holds: the metre, the kilometre, the centimetre and the wellbore.
        await using (var db = _catalog.CreateDbContext())
        {
            var rows = await db.DeliveryCacheItems.AsNoTracking().Where(i => i.Scope == Scope).ToListAsync();
            Assert.Equal(4, rows.Count);
            Assert.All(rows, row => Assert.True(row.FromSequence <= 6 && (row.ToSequence is null || row.ToSequence > 5), $"{row.RecordId} [{row.FromSequence}, {row.ToSequence}) is held by no kept version"));
        }

        // Every version is still listed, the pruned ones saying when; the history counts every change as it did.
        var versions = await _catalog.Caches().ListVersionsAsync(Scope);
        Assert.Equal(6, versions.Count);
        Assert.All(versions.Where(v => v.Sequence <= 4), v => Assert.Equal(now.UtcDateTime, v.PrunedUtc));
        Assert.All(versions.Where(v => v.Sequence > 4), v => Assert.Null(v.PrunedUtc));
        var after = await HistoryAsync(null);
        Assert.Equal(Describe(before), Describe(after));
        Assert.Equal(Describe(unitsBefore), Describe(await HistoryAsync("UnitOfMeasure")));
        // The records that differ can be listed for the current version alone: every other version's predecessor is pruned.
        Assert.Equal([false, true, true, true, true, false], after.OrderBy(h => h.Version.Sequence).Select(h => h.BeforePruned));

        // The kept versions read and check as they did; a pruned one is refused, read or compared.
        var fresh = _catalog.Caches();
        Assert.NotNull(await fresh.LoadAsync(Scope, labels[4]));
        Assert.NotNull(await fresh.LoadAsync(Scope, labels[5]));
        var gone = await Assert.ThrowsAsync<CacheVersionPrunedException>(() => fresh.LoadAsync(Scope, labels[2]));
        Assert.Contains($"Version {labels[2]} of the cache of partition '{Scope}' was pruned at 2026-09-09 06:00:00Z", gone.Message, StringComparison.Ordinal);
        await using (var db = _catalog.CreateDbContext())
        {
            await Assert.ThrowsAsync<CacheVersionPrunedException>(() => CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope, labels[1])));
            var latest = await CacheVersions.CompareAsync(db, new CacheComparisonQuery(Scope, labels[4]));
            Assert.Equal((0L, 1L, 0L), (latest!.Changed, latest.Added, latest.Removed));
        }

        // A second pass finds nothing left to prune.
        var again = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 2, now.AddHours(1), "tests"));
        Assert.Equal((2, 0L), (again.Kept, again.RowsRemoved));
        Assert.Empty(again.Pruned);
        Assert.Equal(Describe(before), Describe(await HistoryAsync(null)));
    }

    [Fact]
    public async Task A_record_that_left_and_came_back_keeps_one_row_once_the_version_without_it_is_pruned()
    {
        // The foot leaves with the second version and comes back unchanged with the third: the cache holds two rows of
        // the same content, one for the first version and one from the third on.
        await CaptureAsync(0, Uom("ft", "foot"), Uom("m", "metre"));
        await CaptureAsync(1, Uom("m", "metre"));
        await CaptureAsync(2, Uom("ft", "foot"), Uom("m", "metre"));
        await CaptureAsync(3, Uom("ft", "foot"), Uom("m", "metre"), Uom("km", "kilometre"));
        Assert.Equal(2, await RowCountAsync(UomId("ft")));

        var outcome = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 1, Start.AddDays(10), "tests"));

        Assert.Equal(2, outcome.Pruned.Count);
        Assert.Equal(1, await RowCountAsync(UomId("ft")));
        Assert.Equal(3, await RowCountAsync());
    }

    [Fact]
    public async Task A_version_a_delivery_flow_pins_keeps_its_records_until_no_flow_pins_it()
    {
        var labels = new List<string>();
        for (var day = 0; day < 5; day++)
        {
            labels.Add(await CaptureAsync(day, Uom("m", "metre-" + day)));
        }

        var pin = await InterfaceAsync(labels[1]);
        // A flow pinning a version of another partition's cache keeps nothing here; nor does a flow no longer declared.
        await InterfaceAsync(labels[2], partition: "prod");
        await InterfaceAsync(labels[0], active: false);

        var outcome = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 1, Start.AddDays(30), "tests"));
        Assert.Equal([labels[0], labels[2]], outcome.Pruned);
        Assert.Equal(3, outcome.Kept);
        Assert.NotNull(await _catalog.Caches().LoadAsync(Scope, labels[1]));

        // The pin goes: the next pass prunes the version.
        await using (var db = _catalog.CreateDbContext())
        {
            await db.DeliveryInterfaces.Where(i => i.Id == pin).ExecuteUpdateAsync(set => set.SetProperty(i => i.CacheVersion, FlowRender.CurrentCacheVersion));
        }

        var unpinned = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 1, Start.AddDays(30), "tests"));
        Assert.Equal([labels[1]], unpinned.Pruned);
        await Assert.ThrowsAsync<CacheVersionPrunedException>(() => _catalog.Caches().LoadAsync(Scope, labels[1]));
    }

    [Fact]
    public async Task Nothing_is_pruned_while_a_delivery_flow_s_pinned_version_is_not_recorded()
    {
        for (var day = 0; day < 4; day++)
        {
            await CaptureAsync(day, Uom("m", "metre-" + day));
        }

        // A flow whose partition is its header's, synced before pins were recorded: it may read this partition.
        var unrecorded = await InterfaceAsync(null, partition: string.Empty);
        var waiting = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 1, Start.AddDays(30), "tests"));
        Assert.Empty(waiting.Pruned);
        Assert.Equal((0L, 4), (waiting.RowsRemoved, waiting.Kept));
        // It names the flow it waits on, and what records its pin.
        Assert.Contains("delivery flow welllogs-", waiting.Deferred, StringComparison.Ordinal);
        Assert.Contains("the control plane records them when it starts, and a sync of the repository does too", waiting.Deferred, StringComparison.Ordinal);

        // The sync records it: the pass after prunes.
        await using (var db = _catalog.CreateDbContext())
        {
            await db.DeliveryInterfaces.Where(i => i.Id == unrecorded).ExecuteUpdateAsync(set => set.SetProperty(i => i.CacheVersion, FlowRender.CurrentCacheVersion));
        }

        var applied = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 1, Start.AddDays(30), "tests"));
        Assert.Null(applied.Deferred);
        Assert.Equal(2, applied.Pruned.Count);
    }

    [Fact]
    public async Task A_refresh_keeps_only_the_newest_versions_the_partition_keeps()
    {
        var labels = new List<string>();
        for (var day = 0; day < 6; day++)
        {
            labels.Add(await CaptureAsync(day, Uom("m", "metre-" + day), Uom("km", "kilometre")));
        }

        // Every version was replaced within the flow's thirty days. A partition nobody set keeps three versions, the
        // current one counted, and a partition that is not registered is no different.
        var store = _catalog.Caches();
        Assert.Equal(CacheRetention.DefaultVersions, await store.VersionsKeptAsync(Scope));
        var three = await store.ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 30, Start.AddDays(6), "schedule:nightly"));
        Assert.Equal((3, 3, 30), (three.VersionsKept, three.Kept, three.RetentionDays));
        Assert.Equal(labels.Take(3), three.Pruned);
        // The kilometre is one row every version shares; the metre of each of the three versions kept stays.
        Assert.Equal(4, await RowCountAsync());

        // An admin keeps two: the next refresh prunes one more. Keeping more brings back nothing pruned.
        await RegisterAsync(CacheRetention.MinVersions);
        Assert.Equal(CacheRetention.MinVersions, await store.VersionsKeptAsync(Scope));
        var two = await store.ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 30, Start.AddDays(6), "schedule:nightly"));
        Assert.Equal([labels[3]], two.Pruned);
        Assert.Equal(2, two.Kept);

        await RegisterAsync(10);
        var ten = await store.ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 30, Start.AddDays(6), "schedule:nightly"));
        Assert.Equal((10, 2), (ten.VersionsKept, ten.Kept));
        Assert.Empty(ten.Pruned);
        await Assert.ThrowsAsync<CacheVersionPrunedException>(() => store.LoadAsync(Scope, labels[3]));
    }

    [Fact]
    public async Task A_purge_may_name_the_versions_it_keeps_and_a_row_edited_by_hand_still_keeps_two()
    {
        var labels = new List<string>();
        for (var day = 0; day < 6; day++)
        {
            labels.Add(await CaptureAsync(day, Uom("m", "metre-" + day)));
        }

        await RegisterAsync(CacheRetention.MaxVersions);
        var store = _catalog.Caches();
        var preview = await store.ApplyRetentionAsync(CacheRetentionRequest.Purge(Scope, 30, Start.AddDays(6), "tahir", dryRun: true, versionsKept: 4));
        Assert.Equal((4, 4), (preview.VersionsKept, preview.Kept));
        Assert.Equal(labels.Take(2), preview.Pruned);
        Assert.DoesNotContain(await store.ListVersionsAsync(Scope), v => v.Pruned);

        await using (var db = _catalog.CreateDbContext())
        {
            await db.DeliveryPartitions.Where(p => p.Name == Scope).ExecuteUpdateAsync(set => set.SetProperty(p => p.CacheVersionsKept, 0));
        }

        Assert.Equal(CacheRetention.MinVersions, await store.VersionsKeptAsync(Scope));
        var applied = await store.ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 30, Start.AddDays(6), "tests"));
        Assert.Equal(labels.Take(4), applied.Pruned);
    }

    [Fact]
    public async Task The_partition_keeps_the_longest_retention_any_of_its_cache_flows_declares()
    {
        // The partition keeps every version, so the days alone decide.
        await RegisterAsync(CacheRetention.MaxVersions);
        for (var day = 0; day < 6; day++)
        {
            await CaptureAsync(day, Uom("m", "metre-" + day));
        }

        // Another project's cache flow of the partition asks for thirty days.
        await _catalog.DeclareCacheAsync(Scope, "project-b-cache", UnitType());
        await using (var db = _catalog.CreateDbContext())
        {
            await db.DeliveryCacheDefinitions.Where(d => d.FlowName == "project-b-cache").ExecuteUpdateAsync(set => set.SetProperty(d => d.RetentionDays, 30));
        }

        var kept = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 1, Start.AddDays(20), "tests"));
        Assert.Equal(30, kept.RetentionDays);
        Assert.Empty(kept.Pruned);

        // Past thirty days, the same pass prunes.
        var pruned = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 1, Start.AddDays(40), "tests"));
        Assert.Equal(4, pruned.Pruned.Count);
    }

    [Fact]
    public async Task An_operator_purge_keeps_exactly_the_days_it_names_and_its_dry_run_says_what_it_would_do()
    {
        // The partition keeps every version, so the days alone decide.
        await RegisterAsync(CacheRetention.MaxVersions);
        var labels = new List<string>();
        for (var day = 0; day < 6; day++)
        {
            labels.Add(await CaptureAsync(day, Uom("m", "metre-" + day), Uom("km", "kilometre")));
        }

        // Another project's cache flow asks for thirty days, which every refresh keeps and an operator's purge does not.
        await _catalog.DeclareCacheAsync(Scope, "project-b-cache", UnitType());
        await using (var db = _catalog.CreateDbContext())
        {
            await db.DeliveryCacheDefinitions.Where(d => d.FlowName == "project-b-cache").ExecuteUpdateAsync(set => set.SetProperty(d => d.RetentionDays, 30));
        }

        var before = await HistoryAsync(null);
        var now = Start.AddDays(6);
        Assert.Empty((await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 1, now, "tests"))).Pruned);

        // The dry run names what goes and counts the rows, and changes nothing.
        var preview = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.Purge(Scope, 0, now, "tahir", dryRun: true));
        Assert.True(preview.DryRun);
        Assert.Equal((0, 2, 4L), (preview.RetentionDays, preview.Kept, preview.RowsRemoved));
        Assert.Equal(labels.Take(4), preview.Pruned);
        Assert.Equal(7, await RowCountAsync());
        Assert.DoesNotContain(await _catalog.Caches().ListVersionsAsync(Scope), v => v.Pruned);

        // The purge does exactly that, and every version it prunes says who pruned it and when.
        var purged = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.Purge(Scope, 0, now, "tahir", dryRun: false));
        Assert.False(purged.DryRun);
        Assert.Equal(preview.Pruned, purged.Pruned);
        Assert.Equal(preview.RowsRemoved, purged.RowsRemoved);
        Assert.Equal(3, await RowCountAsync());
        var versions = await _catalog.Caches().ListVersionsAsync(Scope);
        Assert.All(versions.Where(v => v.Sequence <= 4), v => Assert.Equal((now.UtcDateTime, "tahir"), (v.PrunedUtc!.Value, v.PrunedBy)));
        Assert.All(versions.Where(v => v.Sequence > 4), v => Assert.Null(v.PrunedBy));
        Assert.Equal(Describe(before), Describe(await HistoryAsync(null)));
        var gone = await Assert.ThrowsAsync<CacheVersionPrunedException>(() => _catalog.Caches().LoadAsync(Scope, labels[0]));
        Assert.Contains("by tahir", gone.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pass_cut_short_after_it_marked_a_version_pruned_is_finished_by_the_next()
    {
        for (var day = 0; day < 4; day++)
        {
            await CaptureAsync(day, Uom("m", "metre-" + day));
        }

        var before = await HistoryAsync(null);

        // As a pass leaves things when it stops after recording the changes and marking the version, before any row goes.
        await using (var db = _catalog.CreateDbContext())
        {
            var counted = await CacheVersions.CountChangesAsync(db, Scope, [1, 2]);
            foreach (var (sequence, changes) in counted)
            {
                var json = CacheVersions.ChangesJson(changes);
                await db.DeliveryCacheVersions.Where(v => v.Scope == Scope && v.Sequence == sequence).ExecuteUpdateAsync(set => set.SetProperty(v => v.ChangesJson, json));
            }

            var at = Start.AddDays(10).UtcDateTime;
            await db.DeliveryCacheVersions.Where(v => v.Scope == Scope && v.Sequence == 1).ExecuteUpdateAsync(set => set.SetProperty(v => v.PrunedUtc, at));
        }

        Assert.Equal(4, await RowCountAsync());

        // Three days in, nothing new is due; the rows only the marked version held still go.
        var finished = await _catalog.Caches().ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 7, Start.AddDays(3), "tests"));
        Assert.Empty(finished.Pruned);
        Assert.Equal(1L, finished.RowsRemoved);
        Assert.Equal(Describe(before), Describe(await HistoryAsync(null)));
    }

    [Fact]
    public async Task Merges_beside_a_retention_lose_nothing_a_kept_version_holds()
    {
        var store = _catalog.Caches();
        await CaptureAsync(0, Uom("m", "metre-0"), Uom("km", "kilometre"));
        for (var hour = 1; hour <= 12; hour++)
        {
            var types = Types(Uom("m", "metre-" + hour), Uom("km", "kilometre"), Uom("x" + hour, "unit " + hour));
            var merge = store.MergeAsync(Scope, Flow, types, Capture, Start.AddHours(hour));
            var retain = store.ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 1, Start.AddDays(30), "tests"));
            await Task.WhenAll(merge, retain);
            Assert.True((await merge).Written);
        }

        await store.ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Scope, Flow, 1, Start.AddDays(30), "tests"));

        // Every version that keeps its records reads whole and matches its hash, read afresh; every pruned version has
        // its changes recorded.
        var fresh = _catalog.Caches();
        var versions = await fresh.ListVersionsAsync(Scope);
        Assert.Equal(13, versions.Count);
        Assert.Equal([12, 13], versions.Where(v => !v.Pruned).Select(v => v.Sequence).Order());
        foreach (var version in versions.Where(v => !v.Pruned))
        {
            var loaded = await fresh.LoadAsync(Scope, version.Version);
            Assert.Equal(version.Items, loaded!.Types.Sum(t => (long)t.Items.Count));
        }

        // Each capture changed the metre and brought a unit of its own, and each after the first let go of the one before.
        await using var db = _catalog.CreateDbContext();
        Assert.False(await db.DeliveryCacheVersions.AnyAsync(v => v.Scope == Scope && v.PrunedUtc != null && v.ChangesJson == null));
        var history = await CacheVersions.HistoryAsync(db, Scope, type: null);
        Assert.Equal(new CacheChangeCounts(1, 1, 0), history.Single(h => h.Version.Sequence == 2).Changes);
        Assert.All(history.Where(h => h.Version.Sequence > 2), h => Assert.Equal(new CacheChangeCounts(1, 1, 1), h.Changes));
    }

    [Fact]
    public async Task Every_write_through_the_builder_applies_the_retention_and_a_retention_that_fails_leaves_the_write_standing()
    {
        var clock = new TestClock(Start);
        var builder = new SnapshotBuilder(_catalog.Caches(), Scope, Flow, clock, Samples.Logger<SnapshotBuilder>(), retentionDays: 1);
        for (var day = 0; day < 4; day++)
        {
            var write = await builder.WriteAsync(Types(Uom("m", "metre-" + day)), Capture, []);
            Assert.True(write.Written);
            Assert.NotNull(write.Retention);
            Assert.Equal(1, write.Retention.RetentionDays);
            clock.Advance(TimeSpan.FromDays(1));
        }

        // The fourth write pruned the first version, replaced two days before it. A write that finds nothing new still weighs
        // the history: a day later, the second version is due too.
        var unchanged = await builder.WriteAsync(Types(Uom("m", "metre-3")), Capture, []);
        Assert.False(unchanged.Written);
        Assert.Equal(CacheVersionLabel.Mint(Start.AddDays(1)), Assert.Single(unchanged.Retention!.Pruned));
        var pruned = (await _catalog.Caches().ListVersionsAsync(Scope)).Where(v => v.Pruned).ToList();
        Assert.Equal(2, pruned.Count);
        // Each records who asked for the write that pruned it.
        Assert.All(pruned, v => Assert.Equal(Capture.CapturedBy, v.PrunedBy));

        // The store fails the retention with a message that names a secret: the write stands, and the failure is reported
        // without it.
        var failing = new SnapshotBuilder(new FailingRetention(_catalog.Caches()), Scope, Flow, clock, Samples.Logger<SnapshotBuilder>());
        var stands = await failing.WriteAsync(Types(Uom("m", "metre-4")), Capture, []);
        Assert.True(stands.Written);
        Assert.NotNull(stands.Retention!.Failure);
        Assert.Contains("the cache database answered", stands.Retention.Failure, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", stands.Retention.Failure, StringComparison.Ordinal);
        Assert.Equal(stands.Snapshot.Version, await _catalog.Caches().CurrentVersionAsync(Scope));

        // A cancelled retention cancels the write's caller.
        var cancelled = new SnapshotBuilder(new FailingRetention(_catalog.Caches(), cancel: true), Scope, Flow, clock, Samples.Logger<SnapshotBuilder>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WriteAsync(Types(Uom("m", "metre-5")), Capture, []));
    }

    [Fact]
    public async Task A_delivery_flow_pinning_a_pruned_version_is_told_what_happened_and_what_to_do()
    {
        var store = _catalog.Caches();
        await Samples.ImportSampleCacheAsync(store);
        var lookups = CacheVersionLabel.Mint(Samples.SampleCacheCaptured.AddMinutes(-1));
        var outcome = await store.ApplyRetentionAsync(CacheRetentionRequest.ForFlow(Samples.SampleCacheScope, Flow, 1, Samples.SampleCacheCaptured.AddDays(30), "tests"));
        Assert.Equal([lookups], outcome.Pruned);

        var engine = Samples.Engine(ledger: null, cache: store);
        var flow = Samples.LocalFlow(Samples.NewTempDirectory());
        var values = new Dictionary<string, string> { ["logSource"] = "COMPOSITE" };
        var refused = await Assert.ThrowsAsync<FlowValidationException>(
            () => FlowRuntime.CreateAsync(engine, flow with { Render = flow.Render with { CacheVersion = lookups } }, values));
        Assert.Contains(
            $"render.cacheVersion pins version {lookups} of the cache of partition '{Samples.SampleCacheScope}', whose records the cache's retention pruned",
            refused.Message, StringComparison.Ordinal);
        Assert.Contains("remove render.cacheVersion to render against the current version", refused.Message, StringComparison.Ordinal);
        Assert.IsType<CacheVersionPrunedException>(refused.InnerException);

        // The current version still renders.
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, values);
        Assert.Equal(CacheVersionLabel.Mint(Samples.SampleCacheCaptured), runtime.Mapping.Context.CacheVersion);
    }

    /// <summary>Merges one capture of every type <paramref name="items"/> hold, captured <paramref name="day"/> days after <see cref="Start"/>.</summary>
    /// <summary>
    /// Registers the partition, when it is not yet, and keeps <paramref name="versions"/> versions of its cache, through the
    /// registry, as an admin does.
    /// </summary>
    private async Task RegisterAsync(int versions)
    {
        var registry = new DeliveryPartitionRegistry(_catalog.CreateDbContext);
        var now = Start.UtcDateTime;
        if ((await registry.ReadAsync()).Find(Scope) is null)
        {
            await registry.AddAsync(Scope, null, makeDefault: true, "tests", now);
        }

        await registry.KeepCacheVersionsAsync(Scope, versions, "tahir", now);
    }

    private async Task<string> CaptureAsync(int day, params Item[] items)
    {
        var write = await _catalog.Caches().MergeAsync(Scope, Flow, Types(items), Capture, Start.AddDays(day));
        Assert.True(write.Written);
        return write.Snapshot.Version;
    }

    private static List<ReferenceType> Types(params Item[] items)
        => items.GroupBy(i => (i.Type, i.EntityType)).Select(g => new ReferenceType(g.Key.Type, g.Key.EntityType, g.Select(i => i.Record))).ToList();

    private async Task<IReadOnlyList<CacheHistoryEntry>> HistoryAsync(string? type)
    {
        await using var db = _catalog.CreateDbContext();
        return await CacheVersions.HistoryAsync(db, Scope, type);
    }

    /// <summary>What a history says of each version, without when its records were pruned.</summary>
    private static List<string> Describe(IReadOnlyList<CacheHistoryEntry> history)
        => history
            .Select(h => $"{h.Version.Version} after {h.Before}: {h.Changes}; "
                + string.Join(", ", h.Types.Select(t => $"{t.TypeName} {t.Change} {t.Counts} {t.Hash} {t.Items}")))
            .ToList();

    private async Task<int> RowCountAsync(string? recordId = null)
    {
        await using var db = _catalog.CreateDbContext();
        return await db.DeliveryCacheItems.CountAsync(i => i.Scope == Scope && (recordId == null || i.RecordId == recordId));
    }

    /// <summary>An active delivery interface rendering against <paramref name="version"/>, as the sync records it; returns its id.</summary>
    private async Task<Guid> InterfaceAsync(string? version, string partition = Scope, bool active = true)
    {
        await using var db = _catalog.CreateDbContext();
        var now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var name = "welllogs-" + Guid.NewGuid().ToString("N")[..8];
        var row = new DeliveryInterface
        {
            Id = Guid.NewGuid(),
            RepoId = Guid.NewGuid(),
            FlowName = name,
            Interface = string.Empty,
            Partition = partition,
            LedgerFlowId = Guid.NewGuid(),
            LedgerName = name,
            Route = "storage",
            MappingReference = "WellLog@1.4.0",
            CacheVersion = version,
            RecordObject = "ods.welllogs",
            RelativePath = "flows/" + name + ".yaml",
            Active = active,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        db.DeliveryInterfaces.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static ReferenceTypeSpec UnitType() => new()
    {
        Name = "UnitOfMeasure",
        EntityType = "reference-data--UnitOfMeasure",
        Kind = "osdu:wks:reference-data--UnitOfMeasure:1.0.0",
        Fields = [new ReferenceFieldSpec("data.Code", "Code"), new ReferenceFieldSpec("data.Name", "Name")],
    };

    private static string UomId(string code) => "test:reference-data--UnitOfMeasure:" + code;

    private static Item Uom(string code, string name)
        => new("UnitOfMeasure", "reference-data--UnitOfMeasure", ReferenceItem.FromText(UomId(code), new Dictionary<string, string> { ["Code"] = code, ["Name"] = name }));

    private static Item Wellbore(string key)
        => new("Wellbore", "master-data--Wellbore", ReferenceItem.FromText("test:master-data--Wellbore:" + key, new Dictionary<string, string> { ["FacilityName"] = "Wellbore 1/1-" + key }));

    private sealed record Item(string Type, string EntityType, ReferenceItem Record);

    /// <summary>The module's cache store, whose retention fails: with a message naming a secret, or cancelled.</summary>
    private sealed class FailingRetention(ICacheStore inner, bool cancel = false) : ICacheStore
    {
        public Task<string?> CurrentVersionAsync(string scope, CancellationToken ct = default) => inner.CurrentVersionAsync(scope, ct);

        public Task<IReadOnlyList<CacheVersionInfo>> CurrentVersionsAsync(CancellationToken ct = default) => inner.CurrentVersionsAsync(ct);

        public Task<ReferenceSnapshot?> LoadAsync(string scope, string version, CancellationToken ct = default) => inner.LoadAsync(scope, version, ct);

        public Task<IReadOnlyList<CacheVersionInfo>> ListVersionsAsync(string scope, CancellationToken ct = default) => inner.ListVersionsAsync(scope, ct);

        public Task<CacheVersionInfo?> VersionAsync(string scope, string? version, CancellationToken ct = default) => inner.VersionAsync(scope, version, ct);

        public Task<CacheDeclaration> DeclarationAsync(string scope, CancellationToken ct = default) => inner.DeclarationAsync(scope, ct);

        public Task<IReadOnlySet<string>> HeldByDeliveriesAsync(string scope, string typeName, IReadOnlyCollection<string> ids, CancellationToken ct = default)
            => inner.HeldByDeliveriesAsync(scope, typeName, ids, ct);

        public Task<CacheWrite> MergeAsync(
            string scope, string flowName, IReadOnlyList<ReferenceType> captured, CacheCapture capture, DateTimeOffset capturedUtc,
            IReadOnlyList<SystemPropertyReading>? readings = null, CancellationToken ct = default)
            => inner.MergeAsync(scope, flowName, captured, capture, capturedUtc, readings, ct);

        public Task<Source.ProofRows> ReadProofAsync(CacheProofQuery query, CancellationToken ct = default) => inner.ReadProofAsync(query, ct);

        public Task<int> VersionsKeptAsync(string scope, CancellationToken ct = default) => inner.VersionsKeptAsync(scope, ct);

        public Task<CacheRetentionOutcome> ApplyRetentionAsync(CacheRetentionRequest request, CancellationToken ct = default)
            => cancel
                ? throw new OperationCanceledException("The refresh was cancelled.")
                : throw new InvalidOperationException("the cache database answered: Login failed for 'svc' with password=hunter2.");
    }
}
