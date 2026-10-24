using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Removing what an inventory found (osdu/docs/reference/flow/inventory-removals.md, What a removal does), over the module's database
/// on SQL Server and a platform that serves search, storage's reads and its removals: a removal is refused before anything is
/// read unless the flow allows it, the partition is confirmed and the inventory holds what the operator was shown; every id
/// is checked again just before it goes and what moved is skipped; what passes is soft deleted 500 ids a request, or purged
/// one at a time; every id's outcome is kept, the ids removed are gone from the inventory at once, a stale record's ledger
/// records its removal, and the removal is an activity of the audit trail; OSDU refusing a whole chunk for want of permission
/// stops it there.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class InventoryRemovalTests : IDisposable
{
    private const string Partition = "dev";
    private const string Kind = "osdu:wks:work-product-component--WellLog:1.0.0";
    private const string Estate = "delivery-sp@contoso.com";
    private const string Operator = "user:operator";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _search = new();
    private readonly StoragePlatform _platform;
    private readonly Guid _deliveryFlow = FlowId.Of("welllog-delivery");

    public InventoryRemovalTests()
    {
        _platform = new StoragePlatform(_search);
    }

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    private static string Id(string key) => $"{Partition}:work-product-component--WellLog:{key}";

    private static string Flow(string removal = "removal: { findings: [orphan] }") => $$"""
        flowType: inventory
        name: welllog-inventory
        partitions: [dev]
        source:
          endpoint: http://localhost
        owners: [{{Estate}}]
        {{removal}}
        inventories:
          - name: WellLogs
            kind: "{{Kind}}"
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 2 }
        """;

    private JsonObject Add(string key, long version = 3, string creator = Estate)
    {
        var record = _search.Add(Id(key), Kind, new JsonObject { ["Name"] = key });
        record["version"] = version;
        record["createUser"] = creator;
        record["createTime"] = "2026-09-01T08:00:00.000Z";
        record["modifyUser"] = creator;
        record["modifyTime"] = "2026-09-02T08:00:00.000Z";
        return record;
    }

    private JsonObject Record(string key) => _search.Records.First(r => r["id"]!.GetValue<string>() == Id(key));

    /// <summary>A runner of the flow over the platform, its ledger, and the inventory built once.</summary>
    private async Task<(InventoryRunner Runner, OsduLedger Ledger, InventoryState Inventory)> BuiltAsync(string yaml)
    {
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterAsync(_deliveryFlow);
        var runner = Runner(yaml, ledger);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        return (runner, ledger, Assert.Single(await ledger.ListInventoriesAsync(Partition)));
    }

    private InventoryRunner Runner(string yaml, OsduLedger ledger)
    {
        var flow = new DeliveryDocumentLoader().ParseInventory(yaml, "flows/inventory.yaml").ForPartition(Partition);
        return new InventoryRunner(Samples.Engine(ledger, _clock), flow, new Dictionary<string, string>(), Samples.Logger<InventoryRunner>(), _platform, allowLoopback: true);
    }

    private async Task<DeliveryKey> ClaimAsync(OsduLedger ledger, string key, string status, long? version)
    {
        var deliveryKey = DeliveryKey.Derive("inventory-removals", [key]);
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

    private static InventoryRemovalRequest Every(string finding, long expected, string scope = InventoryRemovals.SoftDelete)
        => new() { Finding = finding, Scope = scope, Expected = expected };

    /// <summary>Every id a removal reached, with its outcome.</summary>
    private static async Task<Dictionary<string, InventoryRemovalItemState>> ItemsAsync(OsduLedger ledger, long removalId)
    {
        var all = new Dictionary<string, InventoryRemovalItemState>(StringComparer.Ordinal);
        long? after = null;
        while (true)
        {
            var page = await ledger.ListInventoryRemovalItemsAsync(Partition, removalId, null, after, 500);
            if (page.Count == 0)
            {
                return all;
            }

            foreach (var item in page)
            {
                all.Add(item.TargetId, item);
            }

            after = page[^1].InventoryRemovalItemId;
        }
    }

    [Fact]
    public async Task Every_orphan_is_soft_deleted_500_ids_a_request_each_id_kept_with_its_outcome_and_gone_from_the_inventory_at_once()
    {
        for (var i = 0; i < 1203; i++)
        {
            Add("orphan-" + i.ToString("D4", CultureInfo.InvariantCulture));
        }

        Add("delivered");
        Add("stranger", creator: "someone@elsewhere.com");
        var ledger0 = _db.Ledger(_clock);
        await ledger0.RegisterAsync(_deliveryFlow);
        await ClaimAsync(ledger0, "delivered", "delivered", 3);
        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        Assert.Equal(1203L, (await ledger.InventoryCountsAsync(Partition, inventory.InventoryId)).Of(InventoryFindings.Orphan));
        var run = Guid.NewGuid();

        var outcome = await runner.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 1203), Partition, run, Operator, CancellationToken.None);

        Assert.Equal((1203L, 0L, 0L, 0L, (string?)null), (outcome.Removed, outcome.Gone, outcome.Skipped, outcome.Failed, outcome.Error));
        Assert.Equal((3, 0), (_platform.BulkDeletes, _platform.SingleDeletes));
        Assert.Equal(1203, _platform.SoftDeleted.Count);
        Assert.Equal(2, _search.Records.Count);
        var counts = await ledger.InventoryCountsAsync(Partition, inventory.InventoryId);
        Assert.Equal((0L, 1203L, 1L, 1L), (counts.Of(InventoryFindings.Orphan), counts.Of(InventoryFindings.Gone), counts.Of(InventoryFindings.Tracked), counts.Of(InventoryFindings.Foreign)));

        var removal = Assert.Single(await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10));
        Assert.Equal((outcome.InventoryRemovalId, InventoryRunStatus.Completed, 1203L, 1203L, run, Operator), (removal.InventoryRemovalId, removal.Status, removal.Requested, removal.Removed, removal.RunId!.Value, removal.Actor));
        var items = await ItemsAsync(ledger, removal.InventoryRemovalId);
        Assert.Equal(1203, items.Count);
        Assert.All(items.Values, i => Assert.Equal((InventoryRemovals.Removed, InventoryFindings.Orphan, 3L), (i.Outcome, i.Finding, i.Version!.Value)));
        var row = (await ledger.LookupInventoryRecordsAsync(Partition, Id("orphan-0000"))).Single();
        Assert.Equal(InventoryFindings.Gone, row.Finding);
        Assert.NotNull(row.GoneUtc);
        Assert.Contains($"removal {removal.InventoryRemovalId}", row.Detail, StringComparison.Ordinal);
        Assert.Equal(InventoryRemovals.Removed, Assert.Single(await ledger.LookupInventoryRemovalItemsAsync(Partition, Id("orphan-0000"))).Outcome);

        // The removal is an activity of the audit trail, under the inventory flow's ledger, closed with what it came to.
        await using var db = _db.CreateDbContext();
        var activity = await db.DeliveryActivities.SingleAsync(a => a.Kind == InventoryRemovals.ActivityKind);
        Assert.Equal((removal.ActivityId, "completed", Operator, (Guid?)run), ((long?)activity.ActivityId, activity.Outcome, activity.Actor, activity.RunId));
        Assert.Contains("1203 removed", activity.Summary, StringComparison.Ordinal);

        // A second removal finds the orphans gone, so the inventory holds none to remove.
        var again = await Assert.ThrowsAsync<DeliveryException>(() => runner.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 1203), Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("holds 0 orphan id(s) now", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_id_is_checked_again_just_before_it_goes_and_what_moved_since_the_reconcile_is_left_in_osdu()
    {
        foreach (var key in new[] { "plain", "claimed", "changed", "vanished", "rewritten" })
        {
            Add(key);
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        await ClaimAsync(ledger, "claimed", "delivered", 3);
        Record("changed")["version"] = 4;
        _search.Records.Remove(Record("vanished"));
        Record("rewritten")["createUser"] = "someone@elsewhere.com";

        var outcome = await runner.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 5), Partition, Guid.NewGuid(), Operator, CancellationToken.None);

        Assert.Equal((1L, 1L, 3L, 0L), (outcome.Removed, outcome.Gone, outcome.Skipped, outcome.Failed));
        Assert.Equal([Id("plain")], _platform.SoftDeleted);
        var items = await ItemsAsync(ledger, outcome.InventoryRemovalId);
        Assert.Equal(InventoryRemovals.Removed, items[Id("plain")].Outcome);
        Assert.Equal(InventoryRemovals.Gone, items[Id("vanished")].Outcome);
        Assert.Equal(InventoryRemovals.Skipped, items[Id("claimed")].Outcome);
        Assert.Contains("ledgers make it tracked", items[Id("claimed")].Reason, StringComparison.Ordinal);
        Assert.Equal(InventoryRemovals.Skipped, items[Id("changed")].Outcome);
        Assert.Contains("OSDU serves version 4", items[Id("changed")].Reason, StringComparison.Ordinal);
        Assert.Equal(InventoryRemovals.Skipped, items[Id("rewritten")].Outcome);
        Assert.Contains("someone@elsewhere.com", items[Id("rewritten")].Reason, StringComparison.Ordinal);

        // What was skipped is still the inventory's to look at; what was removed or found gone is gone from it.
        var rows = (await ledger.ListInventoryRecordsAsync(Partition, inventory.InventoryId, null, null, 50)).ToDictionary(r => r.TargetId, StringComparer.Ordinal);
        Assert.Equal(InventoryFindings.Orphan, rows[Id("changed")].Finding);
        Assert.Null(rows[Id("changed")].GoneUtc);
        Assert.Equal(InventoryFindings.Gone, rows[Id("vanished")].Finding);
        Assert.Equal(InventoryFindings.Gone, rows[Id("plain")].Finding);
    }

    [Fact]
    public async Task A_removal_is_refused_before_anything_is_read_unless_the_flow_allows_it_the_partition_is_confirmed_and_the_count_holds()
    {
        Add("a");
        Add("b");
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterAsync(_deliveryFlow);
        var stale = await ClaimAsync(ledger, "b", "deleted", 3);
        var readOnly = Runner(Flow(removal: string.Empty), ledger);
        await readOnly.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var allowing = Runner(Flow(), ledger);

        var none = await Assert.ThrowsAsync<DeliveryException>(() => readOnly.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 1), Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("allows no removal", none.Message, StringComparison.Ordinal);
        var finding = await Assert.ThrowsAsync<DeliveryException>(() => allowing.RemoveAsync("WellLogs", Every(InventoryFindings.Stale, 1), Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("allows removing orphan ids, not stale ones", finding.Message, StringComparison.Ordinal);
        var purge = await Assert.ThrowsAsync<DeliveryException>(() => allowing.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 1, InventoryRemovals.Purge), Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("soft deletes only", purge.Message, StringComparison.Ordinal);
        var partition = await Assert.ThrowsAsync<DeliveryException>(() => allowing.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 1), "prod", Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("confirms partition 'prod'", partition.Message, StringComparison.Ordinal);
        var count = await Assert.ThrowsAsync<DeliveryException>(() => allowing.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 2), Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("holds 1 orphan id(s) now, and the removal was asked for 2", count.Message, StringComparison.Ordinal);
        var unknown = await Assert.ThrowsAsync<DeliveryException>(() => allowing.RemoveAsync(
            "WellLogs", new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 1, Ids = [Id("never-listed")] },
            Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("holds no row of", unknown.Message, StringComparison.Ordinal);

        Assert.Equal((0, 0), (_platform.BulkDeletes, _platform.SingleDeletes));
        var inventory = Assert.Single(await ledger.ListInventoriesAsync(Partition));
        Assert.Empty(await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10));
        await using var db = _db.CreateDbContext();
        Assert.False(await db.DeliveryActivities.AnyAsync(a => a.Kind == InventoryRemovals.ActivityKind));
        Assert.Equal("deleted", (await db.DeliveryRecords.SingleAsync(r => r.DeliveryKey == stale.Value)).Status);
    }

    [Fact]
    public async Task OSDU_refusing_a_whole_chunk_for_want_of_permission_stops_the_removal_there_keeping_what_it_recorded()
    {
        for (var i = 0; i < 700; i++)
        {
            Add("orphan-" + i.ToString("D3", CultureInfo.InvariantCulture));
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        _platform.RemovalsRefused = HttpStatusCode.Forbidden;

        var stopped = await Assert.ThrowsAsync<InventoryRemovalFailedException>(
            () => runner.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 700), Partition, Guid.NewGuid(), Operator, CancellationToken.None));

        // One request for the first chunk, and none after it: the same refusal is not asked again for every id.
        Assert.Equal((1, 0), (_platform.BulkDeletes, _platform.SingleDeletes));
        Assert.Equal((0L, 500L), (stopped.Outcome.Removed, stopped.Outcome.Failed));
        Assert.Contains("owner of the records", stopped.Outcome.Error, StringComparison.Ordinal);
        var removal = Assert.Single(await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10));
        Assert.Equal((InventoryRunStatus.Failed, 500L), (removal.Status, removal.Failed));
        Assert.Contains("owner of the records", removal.Error, StringComparison.Ordinal);
        var items = await ItemsAsync(ledger, removal.InventoryRemovalId);
        Assert.Equal(500, items.Count);
        Assert.All(items.Values, i => Assert.Equal(InventoryRemovals.Failed, i.Outcome));
        Assert.Equal(700L, (await ledger.InventoryCountsAsync(Partition, inventory.InventoryId)).Of(InventoryFindings.Orphan));
        await using var db = _db.CreateDbContext();
        Assert.Equal("failed", (await db.DeliveryActivities.SingleAsync(a => a.Kind == InventoryRemovals.ActivityKind)).Outcome);
    }

    [Fact]
    public async Task A_partial_answer_asks_again_only_for_the_ids_it_did_not_delete_and_the_rest_are_removed()
    {
        foreach (var key in new[] { "a", "b", "c" })
        {
            Add(key);
        }

        var (runner, ledger, _) = await BuiltAsync(Flow());
        _platform.Undeletable.Add(Id("b"));

        var outcome = await runner.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 3), Partition, Guid.NewGuid(), Operator, CancellationToken.None);

        // The 207 deleted a and c: they are removed, never "already gone", and only b, the id it named, is asked about again.
        Assert.Equal((2L, 0L, 1L, (string?)null), (outcome.Removed, outcome.Gone, outcome.Failed, outcome.Error));
        Assert.Equal((1, 1), (_platform.BulkDeletes, _platform.SingleDeletes));
        Assert.Equal([Id("a"), Id("c")], _platform.SoftDeleted.Order(StringComparer.Ordinal));
        var items = await ItemsAsync(ledger, outcome.InventoryRemovalId);
        Assert.Equal((InventoryRemovals.Removed, InventoryRemovals.Removed), (items[Id("a")].Outcome, items[Id("c")].Outcome));
        Assert.Equal(InventoryRemovals.Failed, items[Id("b")].Outcome);
        Assert.Contains("OSDU refused it (403): the flow's source credentials are no owner of this record (its acl.owners)", items[Id("b")].Reason, StringComparison.Ordinal);
        Assert.StartsWith($"removal {outcome.InventoryRemovalId}, 3 orphan id(s) of 'WellLogs' in 'dev', soft deleted (reversible): 2 removed, 0 already gone, 0 skipped, 1 failed", outcome.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_purge_goes_one_id_at_a_time_and_a_stale_records_ledger_records_its_removal_with_who_asked()
    {
        Add("stale");
        Add("orphan");
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterAsync(_deliveryFlow);
        var key = await ClaimAsync(ledger, "stale", "deleted", 3);
        var runner = Runner(Flow(removal: "removal: { findings: [orphan, stale], purge: true }"), ledger);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var outcome = await runner.RemoveAsync("WellLogs", Every(InventoryFindings.Stale, 1, InventoryRemovals.Purge), Partition, Guid.NewGuid(), Operator, CancellationToken.None);

        Assert.Equal((1L, 0L), (outcome.Removed, outcome.Failed));
        Assert.Equal([Id("stale")], _platform.Purged);
        Assert.Equal((0, 1), (_platform.BulkDeletes, _platform.SingleDeletes));
        var item = Assert.Single((await ItemsAsync(ledger, outcome.InventoryRemovalId)).Values);
        Assert.Equal((InventoryFindings.Stale, _deliveryFlow, key.Value), (item.Finding, item.LedgerFlowId!.Value, item.DeliveryKey!.Value));
        await using var db = _db.CreateDbContext();
        var attempt = await db.DeliveryAttempts.Where(a => a.FlowId == _deliveryFlow && a.DeliveryKey == key.Value).OrderByDescending(a => a.AttemptId).FirstAsync();
        Assert.Equal(("delete", Operator), (attempt.Phase, attempt.Worker));
        Assert.Contains("purged from OSDU", attempt.ResultJson, StringComparison.Ordinal);
        Assert.Contains($"inventory removal {outcome.InventoryRemovalId}", attempt.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gone_ids_are_removed_from_the_inventory_alone_a_round_at_a_time_whatever_the_flow_declares_and_one_a_ledger_expects_stays()
    {
        for (var i = 0; i < 2105; i++)
        {
            Add("gone-" + i.ToString("D4", CultureInfo.InvariantCulture));
        }

        Add("expected");
        Add("served");
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterAsync(_deliveryFlow);
        await ClaimAsync(ledger, "expected", "delivered", 3);

        // A flow that declares no removal, and asks storage for no id, so the id a ledger expects stays gone once it is not listed.
        var runner = Runner(Flow(removal: "maxMissingChecks: 0"), ledger);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        _search.Records.RemoveAll(r => r["id"]!.GetValue<string>() != Id("served"));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var inventory = Assert.Single(await ledger.ListInventoriesAsync(Partition));
        Assert.Equal(2106L, (await ledger.InventoryCountsAsync(Partition, inventory.InventoryId)).Of(InventoryFindings.Gone));
        await using (var db = _db.CreateDbContext())
        {
            var row = await db.DeliveryInventoryRecords.SingleAsync(r => r.TargetId == Id("gone-0000"));
            db.DeliveryInventoryVersions.AddRange(
                new DeliveryInventoryVersion { PartitionId = row.PartitionId, InventoryRecordId = row.InventoryRecordId, Version = 2 },
                new DeliveryInventoryVersion { PartitionId = row.PartitionId, InventoryRecordId = row.InventoryRecordId, Version = 3 });
            await db.SaveChangesAsync();
        }

        var asked = (_search.Calls.Count, _platform.Listings, _platform.HeaderReads, _platform.RecordReads, _platform.BulkDeletes, _platform.SingleDeletes);
        var run = Guid.NewGuid();

        var outcome = await runner.RemoveAsync("WellLogs", Every(InventoryFindings.Gone, 2106, InventoryRemovals.InventoryOnly), Partition, run, Operator, CancellationToken.None);

        Assert.Equal((2105L, 0L, 1L, 0L, (string?)null), (outcome.Removed, outcome.Gone, outcome.Skipped, outcome.Failed, outcome.Error));
        Assert.Equal(asked, (_search.Calls.Count, _platform.Listings, _platform.HeaderReads, _platform.RecordReads, _platform.BulkDeletes, _platform.SingleDeletes));
        var counts = await ledger.InventoryCountsAsync(Partition, inventory.InventoryId);
        Assert.Equal((1L, 1L), (counts.Of(InventoryFindings.Gone), counts.Of(InventoryFindings.Orphan)));
        Assert.Empty(await ledger.LookupInventoryRecordsAsync(Partition, Id("gone-0000")));
        await using (var db = _db.CreateDbContext())
        {
            Assert.False(await db.DeliveryInventoryVersions.AnyAsync());
        }

        var removal = Assert.Single(await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10));
        Assert.Equal(
            (InventoryFindings.Gone, InventoryRemovals.InventoryOnly, InventoryRunStatus.Completed, 2106L, 2105L, 1L, run, Operator),
            (removal.Finding, removal.Scope, removal.Status, removal.Requested, removal.Removed, removal.Skipped, removal.RunId!.Value, removal.Actor));

        // Only the id it kept is listed with the removal, with why: an id it removed keeps no row, which is what it removed.
        var item = Assert.Single((await ItemsAsync(ledger, removal.InventoryRemovalId)).Values);
        Assert.Equal((Id("expected"), InventoryRemovals.Skipped, InventoryFindings.Gone, _deliveryFlow), (item.TargetId, item.Outcome, item.Finding, item.LedgerFlowId!.Value));
        Assert.Contains("a ledger expects it", item.Reason, StringComparison.Ordinal);
        await using (var db = _db.CreateDbContext())
        {
            var activity = await db.DeliveryActivities.SingleAsync(a => a.Kind == InventoryRemovals.ActivityKind);
            Assert.Equal(("completed", Operator, (Guid?)run), (activity.Outcome, activity.Actor, activity.RunId));
            Assert.Contains("removed from the inventory alone (nothing asked of OSDU): 2105 removed", activity.Summary, StringComparison.Ordinal);
        }

        // An id OSDU serves again is listed again by the next build, as any new id is.
        Add("gone-0000");
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(InventoryFindings.Orphan, Assert.Single(await ledger.LookupInventoryRecordsAsync(Partition, Id("gone-0000"))).Finding);
    }

    [Fact]
    public async Task Gone_ids_go_only_from_the_inventory_served_ids_only_from_osdu_and_a_picked_id_that_is_not_gone_stays()
    {
        foreach (var key in new[] { "a", "b", "c" })
        {
            Add(key);
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        _search.Records.Remove(Record("a"));
        _search.Records.Remove(Record("b"));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var fromOsdu = await Assert.ThrowsAsync<DeliveryException>(() => runner.RemoveAsync("WellLogs", Every(InventoryFindings.Gone, 2), Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("there is nothing of it in OSDU to remove", fromOsdu.Message, StringComparison.Ordinal);
        var fromInventory = await Assert.ThrowsAsync<DeliveryException>(
            () => runner.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 1, InventoryRemovals.InventoryOnly), Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("the next build would list it again", fromInventory.Message, StringComparison.Ordinal);
        var count = await Assert.ThrowsAsync<DeliveryException>(
            () => runner.RemoveAsync("WellLogs", Every(InventoryFindings.Gone, 3, InventoryRemovals.InventoryOnly), Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("holds 2 gone id(s) now, and the removal was asked for 3", count.Message, StringComparison.Ordinal);
        Assert.Empty(await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10));

        var picked = new InventoryRemovalRequest { Finding = InventoryFindings.Gone, Scope = InventoryRemovals.InventoryOnly, Expected = 2, Ids = [Id("a"), Id("c")] };
        var outcome = await runner.RemoveAsync("WellLogs", picked, Partition, Guid.NewGuid(), Operator, CancellationToken.None);

        Assert.Equal((1L, 1L), (outcome.Removed, outcome.Skipped));
        Assert.Empty(_platform.SoftDeleted);
        var item = Assert.Single((await ItemsAsync(ledger, outcome.InventoryRemovalId)).Values);
        Assert.Equal((Id("c"), InventoryRemovals.Skipped), (item.TargetId, item.Outcome));
        Assert.Contains("finds it orphan now, not gone", item.Reason, StringComparison.Ordinal);
        var rows = (await ledger.ListInventoryRecordsAsync(Partition, inventory.InventoryId, null, null, 50)).ToDictionary(r => r.TargetId, StringComparer.Ordinal);
        Assert.Equal([Id("b"), Id("c")], rows.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((InventoryFindings.Gone, InventoryFindings.Orphan), (rows[Id("b")].Finding, rows[Id("c")].Finding));
        Assert.True(Assert.Single(await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10)).NamesIds);
    }

    [Fact]
    public async Task A_removal_naming_its_ids_removes_those_and_skips_one_a_later_reconcile_found_otherwise()
    {
        foreach (var key in new[] { "a", "b", "c" })
        {
            Add(key);
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        await ClaimAsync(ledger, "b", "delivered", 3);
        await runner.ReconcileAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var picked = new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 2, Ids = [Id("a"), Id("b")] };

        var outcome = await runner.RemoveAsync("WellLogs", picked, Partition, Guid.NewGuid(), Operator, CancellationToken.None);

        Assert.Equal((1L, 1L), (outcome.Removed, outcome.Skipped));
        Assert.Equal([Id("a")], _platform.SoftDeleted);
        var items = await ItemsAsync(ledger, outcome.InventoryRemovalId);
        Assert.Contains("finds it tracked now", items[Id("b")].Reason, StringComparison.Ordinal);
        Assert.False(items.ContainsKey(Id("c")));
        var removal = Assert.Single(await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10));
        Assert.True(removal.NamesIds);
    }
}
