using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An inventory's tables on SQL Server (osdu/docs/reference/flow/inventory-builds.md, The tables an inventory keeps): a complete
/// read merged into the inventory, adding, changing and marking gone, and a read never merged changing nothing; every id
/// OSDU serves compared with the ledgers of the partition (the records that claim ids, the artifacts deliveries recorded,
/// the records purged from a ledger) into its finding; the ids a ledger expects that the read did not list told missing,
/// unlisted or gone; the owners inferred from what a ledger claims; versions read only for what moved; and the reads a
/// report pages through.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class InventoryLedgerTests : IDisposable
{
    private const string Partition = TestLedgers.Partition;
    private const string Kind = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string Estate = "delivery-sp@contoso.com";
    private const string Stranger = "someone@elsewhere.com";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _inventoryFlow = FlowId.Of("welllog-inventory", Partition);
    private readonly Guid _deliveryFlow = FlowId.Of("welllog-delivery");

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose() => _db.Dispose();

    private static string Id(string key) => $"{Partition}:work-product-component--WellLog:{key}";

    private static InventoryScanRow Row(string key, long version, string creator = Estate, string? kind = Kind)
        => new(Id(key), kind, version, creator, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), creator, new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc));

    private async Task<(OsduLedger Ledger, InventoryState Inventory)> InventoryAsync()
    {
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterLedgerAsync(new LedgerEntry
        {
            FlowId = _inventoryFlow,
            Partition = Partition,
            Kind = LedgerKinds.Inventory,
            FlowName = "welllog-inventory",
            LedgerName = "welllog-inventory@" + Partition,
        });
        await ledger.RegisterAsync(_deliveryFlow);
        var inventory = await ledger.RegisterInventoryAsync(_inventoryFlow, "welllog-inventory", "WellLogs", Kind, null, "search", "latest");
        return (ledger, inventory);
    }

    /// <summary>One build's read staged and merged, as a build does it.</summary>
    private async Task<InventoryMerge> BuildAsync(OsduLedger ledger, InventoryState inventory, params InventoryScanRow[] rows)
    {
        var run = await ledger.StartInventoryRunAsync(_inventoryFlow, inventory.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "tests", "search", Now);
        await ledger.AppendInventoryScanAsync(_inventoryFlow, run, rows);
        return await ledger.MergeInventoryAsync(_inventoryFlow, inventory.InventoryId, run, Now);
    }

    private static async Task<Dictionary<string, InventoryRecordState>> RecordsAsync(OsduLedger ledger, InventoryState inventory)
    {
        var all = new List<InventoryRecordState>();
        long? after = null;
        while (true)
        {
            var page = await ledger.ListInventoryRecordsAsync(Partition, inventory.InventoryId, null, after, 2);
            if (page.Count == 0)
            {
                return all.ToDictionary(r => r.TargetId, StringComparer.Ordinal);
            }

            all.AddRange(page);
            after = page[^1].InventoryRecordId;
        }
    }

    /// <summary>A record of the delivery flow's ledger claiming <paramref name="key"/>'s id, in <paramref name="status"/> at <paramref name="version"/>.</summary>
    private async Task<DeliveryKey> ClaimAsync(OsduLedger ledger, string key, string status, long? version)
    {
        var deliveryKey = DeliveryKey.Derive("inventory-tests", [key]);
        await ledger.UpsertPendingAsync(_deliveryFlow, [new RecordState
        {
            DeliveryKey = deliveryKey, FlowId = _deliveryFlow, SourceKey = key, MappingName = "WellLog", TargetId = Id(key),
            LastSubmissionId = Guid.NewGuid(), PendingDocumentRef = "0:0:10", PendingRenderContext = "{}", PendingMetadataHash = "mh", PendingMetadata = true,
        }]);
        await using var db = _db.CreateDbContext();
        await db.DeliveryRecords
            .Where(r => r.FlowId == _deliveryFlow && r.DeliveryKey == deliveryKey.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status).SetProperty(r => r.TargetVersion, version).SetProperty(r => r.ClaimedTargetId, Id(key)));
        return deliveryKey;
    }

    private async Task ArtifactAsync(string targetId, string state, string role = "dataset", DeliveryKey? key = null)
    {
        await using var db = _db.CreateDbContext();
        var partition = await db.DeliveryLedgers.Where(l => l.FlowId == _deliveryFlow).Select(l => l.PartitionId).SingleAsync();
        db.DeliveryArtifacts.Add(new DeliveryArtifact
        {
            PartitionId = partition, FlowId = _deliveryFlow, DeliveryKey = (key ?? DeliveryKey.Derive("inventory-tests", ["artifact", targetId])).Value,
            UnitId = Guid.NewGuid(), UnitStartedUtc = Now, Slot = "dataset:" + targetId.GetHashCode(StringComparison.Ordinal), Role = role, TargetId = targetId,
            State = state, CreatedUtc = Now, UpdatedUtc = Now,
        });
        await db.SaveChangesAsync();
    }

    private async Task PurgedAsync(string targetId)
    {
        await using var db = _db.CreateDbContext();
        var partition = await db.DeliveryLedgers.Where(l => l.FlowId == _deliveryFlow).Select(l => l.PartitionId).SingleAsync();
        db.DeliveryPurgedRecords.Add(new DeliveryPurgedRecord
        {
            PartitionId = partition, FlowId = _deliveryFlow, DeliveryKey = Guid.NewGuid(), SourceKey = "purged", TargetId = targetId, LastVersion = 3,
            PurgedBy = "tests", PurgedUtc = Now,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_complete_read_is_merged_adding_changing_and_marking_gone_and_ids_served_again_come_back()
    {
        var (ledger, inventory) = await InventoryAsync();

        var first = await BuildAsync(ledger, inventory, Row("a", 1), Row("b", 1), Row("c", 1));
        Assert.Equal(new InventoryMerge(3, 3, 0, 0, 0), first);
        var built = await RecordsAsync(ledger, inventory);
        Assert.All(built.Values, r => Assert.Equal((InventoryFindings.Unreconciled, Now, (DateTime?)null), (r.Finding, r.FirstSeenUtc!.Value, r.GoneUtc)));

        // a moves, b goes, c stays as it was, d is new.
        _clock.Advance(TimeSpan.FromHours(1));
        var second = await BuildAsync(ledger, inventory, Row("a", 2), Row("c", 1), Row("d", 1));
        Assert.Equal(new InventoryMerge(3, 1, 1, 1, 0), second);
        var records = await RecordsAsync(ledger, inventory);
        Assert.Equal((2L, Now), (records[Id("a")].Version!.Value, records[Id("a")].ChangedUtc!.Value));
        Assert.Equal(Now, records[Id("b")].GoneUtc);
        Assert.Equal(Now.AddHours(-1), records[Id("c")].ChangedUtc);
        Assert.Equal(Now, records[Id("d")].FirstSeenUtc);

        // b is served again, and keeps when it was first listed.
        _clock.Advance(TimeSpan.FromHours(1));
        var third = await BuildAsync(ledger, inventory, Row("a", 2), Row("b", 4), Row("c", 1), Row("d", 1));
        Assert.Equal(new InventoryMerge(4, 0, 0, 0, 1), third);
        var back = (await RecordsAsync(ledger, inventory))[Id("b")];
        Assert.Equal((null, 4L, Now.AddHours(-2)), (back.GoneUtc, back.Version!.Value, back.FirstSeenUtc!.Value));
    }

    [Fact]
    public async Task A_read_listing_an_id_twice_keeps_its_latest_version_and_a_read_never_merged_changes_nothing()
    {
        var (ledger, inventory) = await InventoryAsync();
        Assert.Equal(new InventoryMerge(1, 1, 0, 0, 0), await BuildAsync(ledger, inventory, Row("a", 3), Row("a", 7), Row("a", 5)));
        Assert.Equal(7L, (await RecordsAsync(ledger, inventory))[Id("a")].Version);

        // A build stopped after staging its read: the next one closes it, deletes what it staged, and the inventory is as it was.
        var stopped = await ledger.StartInventoryRunAsync(_inventoryFlow, inventory.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "tests", "search", Now);
        await ledger.AppendInventoryScanAsync(_inventoryFlow, stopped, [Row("x", 1)]);
        Assert.Single(await RecordsAsync(ledger, inventory));

        _clock.Advance(TimeSpan.FromMinutes(5));
        var next = await ledger.StartInventoryRunAsync(_inventoryFlow, inventory.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "tests", "search", Now);
        await using (var db = _db.CreateDbContext())
        {
            Assert.Equal(0, await db.DeliveryInventoryScans.CountAsync(s => s.InventoryRunId == stopped));
        }

        var runs = await ledger.ListInventoryRunsAsync(Partition, inventory.InventoryId, 10);
        var closed = runs.Single(r => r.InventoryRunId == stopped);
        Assert.Equal((InventoryRunStatus.Failed, Now), (closed.Status, closed.CompletedUtc!.Value));
        Assert.Contains("stopped before it did", closed.Error, StringComparison.Ordinal);
        Assert.Equal(InventoryRunStatus.Running, runs.Single(r => r.InventoryRunId == next).Status);
    }

    [Fact]
    public async Task Every_id_OSDU_serves_gets_the_finding_the_ledgers_of_the_partition_give_it()
    {
        var (ledger, inventory) = await InventoryAsync();
        await ClaimAsync(ledger, "tracked", "delivered", 10);
        await ClaimAsync(ledger, "drifted", "delivered", 10);
        await ClaimAsync(ledger, "unconfirmed", "held", null);
        await ClaimAsync(ledger, "removed", "deleted", 10);
        await ArtifactAsync(Id("minted"), "live");
        await ArtifactAsync(Id("replaced"), "superseded");
        await ArtifactAsync(Id("undoing"), "failed");
        await ArtifactAsync(Id("inflight"), "pending");
        await ArtifactAsync(Id("undone"), "removed");
        await ArtifactAsync(Id("orphaned-artifact"), "live", key: DeliveryKey.Derive("inventory-tests", ["no such record"]));
        await PurgedAsync(Id("purged"));

        // The artifacts above belong to records the ledger holds, except the one keyed by a record it never held.
        foreach (var key in new[] { "minted", "replaced", "undoing", "inflight", "undone" })
        {
            await ClaimAsync(ledger, "holder-" + key, "delivered", 1);
        }

        await using (var db = _db.CreateDbContext())
        {
            foreach (var key in new[] { "minted", "replaced", "undoing", "inflight", "undone" })
            {
                var holder = DeliveryKey.Derive("inventory-tests", ["holder-" + key]).Value;
                await db.DeliveryArtifacts.Where(a => a.TargetId == Id(key)).ExecuteUpdateAsync(s => s.SetProperty(a => a.DeliveryKey, holder));
            }
        }

        await BuildAsync(
            ledger, inventory,
            Row("tracked", 10), Row("drifted", 12), Row("unconfirmed", 1), Row("removed", 10), Row("minted", 1), Row("replaced", 1), Row("undoing", 1),
            Row("inflight", 1), Row("undone", 1), Row("orphaned-artifact", 1), Row("purged", 3), Row("ours", 1), Row("theirs", 1, Stranger));
        await ledger.ReconcileInventoryAsync(_inventoryFlow, inventory.InventoryId, [Estate], Now);

        var records = await RecordsAsync(ledger, inventory);
        string Finding(string key) => records[Id(key)].Finding;
        Assert.Equal(InventoryFindings.Tracked, Finding("tracked"));
        Assert.Equal(InventoryFindings.Drifted, Finding("drifted"));
        Assert.Equal("the ledger holds version 10, OSDU serves version 12", records[Id("drifted")].Detail);
        Assert.Equal(InventoryFindings.Unconfirmed, Finding("unconfirmed"));
        Assert.Equal(InventoryFindings.Stale, Finding("removed"));
        Assert.Equal(InventoryFindings.Tracked, Finding("minted"));
        Assert.Equal(InventoryFindings.Superseded, Finding("replaced"));
        Assert.Equal(InventoryFindings.Undoing, Finding("undoing"));
        Assert.Equal(InventoryFindings.Unconfirmed, Finding("inflight"));
        Assert.Equal(InventoryFindings.Stale, Finding("undone"));
        Assert.Equal(InventoryFindings.Forgotten, Finding("orphaned-artifact"));
        Assert.Equal(InventoryFindings.Forgotten, Finding("purged"));
        Assert.Equal(InventoryFindings.Orphan, Finding("ours"));
        Assert.Equal(InventoryFindings.Foreign, Finding("theirs"));

        // What the finding rests on is kept with it: the ledger, the record, its status and version, the artifact.
        var drifted = records[Id("drifted")];
        Assert.Equal((_deliveryFlow, "delivered", 10L), (drifted.LedgerFlowId!.Value, drifted.LedgerStatus, drifted.LedgerVersion!.Value));
        Assert.Equal("failed", records[Id("undoing")].ArtifactState);

        // A second reconcile that finds the same keeps when each finding was first set.
        var at = records[Id("ours")].FindingUtc;
        _clock.Advance(TimeSpan.FromHours(1));
        await ledger.ReconcileInventoryAsync(_inventoryFlow, inventory.InventoryId, [Estate], Now);
        Assert.Equal(at, (await RecordsAsync(ledger, inventory))[Id("ours")].FindingUtc);

        // An owner no longer named makes its ids foreign, from now.
        await ledger.ReconcileInventoryAsync(_inventoryFlow, inventory.InventoryId, [], Now);
        var foreign = (await RecordsAsync(ledger, inventory))[Id("ours")];
        Assert.Equal((InventoryFindings.Foreign, Now), (foreign.Finding, foreign.FindingUtc));

        var counts = await ledger.InventoryCountsAsync(_inventoryFlow, inventory.InventoryId);
        Assert.Equal(13L, counts.ByFinding.Values.Sum());
        Assert.Equal(2L, counts.Of(InventoryFindings.Foreign));
    }

    [Fact]
    public async Task Ids_a_ledger_expects_that_the_read_did_not_list_are_read_from_storage_into_missing_or_unlisted_and_the_rest_are_gone()
    {
        var (ledger, inventory) = await InventoryAsync();
        await ClaimAsync(ledger, "lost", "delivered", 5);
        await ClaimAsync(ledger, "lagging", "delivered", 5);
        await ClaimAsync(ledger, "never-listed", "delivered", 5);
        await ClaimAsync(ledger, "pending-only", "pending", null);
        await ArtifactAsync(Id("minted-lost"), "live", key: await ClaimAsync(ledger, "minted-holder", "pending", null));
        await BuildAsync(ledger, inventory, Row("lost", 5), Row("lagging", 5), Row("unwanted", 1), Row("minted-lost", 1));

        // The next read lists none of them.
        _clock.Advance(TimeSpan.FromHours(1));
        await BuildAsync(ledger, inventory);
        await ledger.ReconcileInventoryAsync(_inventoryFlow, inventory.InventoryId, [Estate], Now);

        // Narrowed: only the rows the inventory listed before, delivered records and live minted ids, are candidates.
        var narrowed = await ledger.InventoryCandidatesAsync(_inventoryFlow, inventory.InventoryId, null, 100);
        Assert.Equal([Id("lagging"), Id("lost"), Id("minted-lost")], narrowed.Select(c => c.TargetId).Order(StringComparer.Ordinal));
        Assert.All(narrowed, c => Assert.True(c.Known));

        // Covering the type whole: the ledgers' delivered records of the type it never listed too, after the known ones.
        var covering = await ledger.InventoryCandidatesAsync(_inventoryFlow, inventory.InventoryId, $"{Partition}:work-product-component--WellLog:", 100);
        Assert.Equal(4, covering.Count);
        Assert.Equal((Id("never-listed"), false), (covering[^1].TargetId, covering[^1].Known));
        Assert.Equal(2, (await ledger.InventoryCandidatesAsync(_inventoryFlow, inventory.InventoryId, $"{Partition}:work-product-component--WellLog:", 2)).Count);

        // Storage holds lagging and never-listed, and not lost or minted-lost.
        var checks = covering.Select(c => new InventoryCheck(c, c.TargetId == Id("lagging") || c.TargetId == Id("never-listed"), c.TargetId == Id("never-listed") ? Row("never-listed", 5) : null)).ToList();
        await ledger.RecordInventoryChecksAsync(_inventoryFlow, inventory.InventoryId, checks, Now);

        var records = await RecordsAsync(ledger, inventory);
        Assert.Equal(InventoryFindings.Missing, records[Id("lost")].Finding);
        Assert.Equal(InventoryFindings.Missing, records[Id("minted-lost")].Finding);
        Assert.Equal(InventoryFindings.Unlisted, records[Id("lagging")].Finding);
        var never = records[Id("never-listed")];
        Assert.Equal((InventoryFindings.Unlisted, (DateTime?)null, 5L), (never.Finding, never.FirstSeenUtc, never.Version!.Value));
        Assert.Equal(InventoryFindings.Gone, records[Id("unwanted")].Finding);
        Assert.DoesNotContain(Id("pending-only"), records.Keys);
    }

    [Fact]
    public async Task Owners_are_the_identities_that_created_the_ids_a_ledger_claims_counted_by_what_each_created()
    {
        var (ledger, inventory) = await InventoryAsync();
        await ClaimAsync(ledger, "a", "delivered", 1);
        await ClaimAsync(ledger, "b", "delivered", 1);
        await ClaimAsync(ledger, "c", "delivered", 1);
        await ArtifactAsync(Id("d"), "live");
        await BuildAsync(ledger, inventory, Row("a", 1), Row("b", 1), Row("c", 1, "migration@contoso.com"), Row("d", 1), Row("e", 1, Stranger));

        var owners = await ledger.InventoryOwnersAsync(_inventoryFlow, inventory.InventoryId);

        Assert.Equal([new InventoryOwner(Estate, 3), new InventoryOwner("migration@contoso.com", 1)], owners);
    }

    [Fact]
    public async Task Versions_are_read_only_for_records_that_are_new_or_whose_latest_version_moved()
    {
        var (ledger, inventory) = await InventoryAsync();
        await BuildAsync(ledger, inventory, Row("a", 2), Row("b", 1));
        var due = await ledger.InventoryVersionsDueAsync(_inventoryFlow, inventory.InventoryId, 10);
        Assert.Equal([Id("a"), Id("b")], due.Select(d => d.TargetId));

        await ledger.WriteInventoryVersionsAsync(_inventoryFlow, due.Select(d => new InventoryVersionsRead(d.InventoryRecordId, d.Version, d.TargetId == Id("a") ? [1, 2] : [1])).ToList());
        Assert.Empty(await ledger.InventoryVersionsDueAsync(_inventoryFlow, inventory.InventoryId, 10));

        await BuildAsync(ledger, inventory, Row("a", 3), Row("b", 1));
        var moved = Assert.Single(await ledger.InventoryVersionsDueAsync(_inventoryFlow, inventory.InventoryId, 10));
        Assert.Equal((Id("a"), 3L), (moved.TargetId, moved.Version));
        await ledger.WriteInventoryVersionsAsync(_inventoryFlow, [new InventoryVersionsRead(moved.InventoryRecordId, 3, [1, 2, 3])]);
        await using var db = _db.CreateDbContext();
        Assert.Equal([1L, 2L, 3L], await db.DeliveryInventoryVersions.Where(v => v.InventoryRecordId == moved.InventoryRecordId).OrderBy(v => v.Version).Select(v => v.Version).ToListAsync());
    }

    [Fact]
    public async Task A_completed_run_becomes_its_inventorys_last_and_the_report_reads_list_counts_pages_runs_and_lookups()
    {
        var (ledger, inventory) = await InventoryAsync();
        await ClaimAsync(ledger, "a", "delivered", 1);
        var run = await ledger.StartInventoryRunAsync(_inventoryFlow, inventory.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "gui:tahir", "search", Now);
        await ledger.AppendInventoryScanAsync(_inventoryFlow, run, [Row("a", 1), Row("b", 1), Row("c", 1, Stranger)]);
        var merge = await ledger.MergeInventoryAsync(_inventoryFlow, inventory.InventoryId, run, Now);
        await ledger.ReconcileInventoryAsync(_inventoryFlow, inventory.InventoryId, [Estate], Now);
        var counts = await ledger.InventoryCountsAsync(_inventoryFlow, inventory.InventoryId);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await ledger.CompleteInventoryRunAsync(
            _inventoryFlow, run,
            new InventoryRunState
            {
                InventoryRunId = run, InventoryId = inventory.InventoryId, Operation = InventoryRunStatus.Build, Actor = "gui:tahir", Status = InventoryRunStatus.Completed,
                StartedUtc = Now, ReadMode = "search", Listed = merge.Listed, Added = merge.Added, Pages = 1, Requests = 1,
                FindingsJson = JsonSerializer.Serialize(counts.ByFinding), OwnersJson = "{\"source\":\"declared\"}",
            },
            "declared", Now);

        var listed = Assert.Single(await ledger.ListInventoriesAsync(Partition));
        Assert.Equal((run, Now, run, "declared"), (listed.LastBuildRunId!.Value, listed.LastBuiltUtc!.Value, listed.LastReconcileRunId!.Value, listed.OwnersSource));
        Assert.Equal(Partition, listed.Partition);
        Assert.Empty(await ledger.ListInventoriesAsync("prod"));
        Assert.Equal(inventory.InventoryId, (await ledger.GetInventoryAsync(Partition, inventory.InventoryId))!.InventoryId);
        Assert.Null(await ledger.GetInventoryAsync(Partition, inventory.InventoryId + 1000));

        var byPartition = await ledger.InventoryCountsAsync(Partition, inventory.InventoryId);
        Assert.Equal((1L, 1L, 1L), (byPartition.Of(InventoryFindings.Tracked), byPartition.Of(InventoryFindings.Orphan), byPartition.Of(InventoryFindings.Foreign)));
        Assert.Equal(1L, byPartition.Raised);

        var orphans = Assert.Single(await ledger.ListInventoryRecordsAsync(Partition, inventory.InventoryId, InventoryFindings.Orphan, null, 10));
        Assert.Equal(Id("b"), orphans.TargetId);
        var refused = await Assert.ThrowsAsync<DeliveryException>(() => ledger.ListInventoryRecordsAsync(Partition, inventory.InventoryId, "lost", null, 10));
        Assert.Contains("is not a finding", refused.Message, StringComparison.Ordinal);

        var looked = Assert.Single(await ledger.LookupInventoryRecordsAsync(Partition, Id("a")));
        Assert.Equal((InventoryFindings.Tracked, _deliveryFlow), (looked.Finding, looked.LedgerFlowId!.Value));
        Assert.Empty(await ledger.LookupInventoryRecordsAsync(Partition, Id("nowhere")));

        var done = Assert.Single(await ledger.ListInventoryRunsAsync(Partition, inventory.InventoryId, 5));
        Assert.Equal((InventoryRunStatus.Completed, 3L, 3L), (done.Status, done.Listed, done.Added));
        Assert.Contains("\"tracked\":1", done.FindingsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registering_an_inventory_again_keeps_its_rows_and_takes_what_the_flow_declares_now()
    {
        var (ledger, inventory) = await InventoryAsync();
        await BuildAsync(ledger, inventory, Row("a", 1));

        var again = await ledger.RegisterInventoryAsync(_inventoryFlow, "welllog-inventory", "WellLogs", "osdu:wks:work-product-component--WellLog:*", "data.Name:A*", "search", "all");

        Assert.Equal(inventory.InventoryId, again.InventoryId);
        Assert.Equal(("osdu:wks:work-product-component--WellLog:*", "data.Name:A*", "all"), (again.Kind, again.Query, again.Versions));
        Assert.Single(await RecordsAsync(ledger, again));
    }
}
