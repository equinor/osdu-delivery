using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Restoring an inventory's gone ids (osdu/docs/reference/flow/inventory-removals.md, Restoring gone ids), over the module's database
/// on SQL Server and a platform that keeps a soft-deleted record as storage does: a restore is refused before anything is asked
/// unless the flow declares a removal, the partition is confirmed and what the operator was shown holds; every id is checked
/// again just before it is brought back, and what a ledger knows, what this estate did not write, what storage purged or serves
/// already, and what it withholds is kept with why; what passes is served again at the version it had and in the inventory at
/// once; what one removal removed comes back from the removal, even after its ids were removed from the inventory; and OSDU
/// refusing a whole round for want of permission stops the restore there.
/// </summary>
public sealed partial class InventoryRemovalTests
{
    [Fact]
    public async Task What_a_removal_soft_deleted_is_restored_in_osdu_at_the_version_it_had_and_served_again_in_the_inventory_at_once()
    {
        foreach (var key in new[] { "a", "b", "c" })
        {
            Add(key);
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var removed = await runner.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 3), Partition, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal(3L, removed.Removed);
        Assert.Equal(3L, (await ledger.InventoryCountsAsync(Partition, inventory.InventoryId)).Of(InventoryFindings.Gone));
        var run = Guid.NewGuid();

        var outcome = await runner.RestoreAsync("WellLogs", new InventoryRestoreRequest { Expected = 3 }, Partition, run, Operator, CancellationToken.None);

        Assert.Equal((3L, 0L, 0L, 0L, (string?)null), (outcome.Restored, outcome.Gone, outcome.Skipped, outcome.Failed, outcome.Error));
        Assert.Equal((DeliveryOperations.Restore, InventoryRemovals.Restore, (long?)null), (outcome.Operation, outcome.Scope, outcome.RestoresRemovalId));
        Assert.Equal([Id("a"), Id("b"), Id("c")], _platform.Restored.Order(StringComparer.Ordinal));
        Assert.Equal(3, _search.Records.Count);
        Assert.Equal(3L, Record("a")["version"]!.GetValue<long>());

        // Served again in the inventory at once, with the finding the reconcile's rule gives them: orphans, as before the removal.
        var counts = await ledger.InventoryCountsAsync(Partition, inventory.InventoryId);
        Assert.Equal((3L, 0L), (counts.Of(InventoryFindings.Orphan), counts.Of(InventoryFindings.Gone)));
        var row = Assert.Single(await ledger.LookupInventoryRecordsAsync(Partition, Id("a")));
        Assert.Null(row.GoneUtc);
        Assert.Contains($"restore {outcome.InventoryRemovalId}", row.Detail, StringComparison.Ordinal);

        var restore = (await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10))[0];
        Assert.Equal(
            (outcome.InventoryRemovalId, InventoryRemovals.Restore, InventoryFindings.Gone, InventoryRunStatus.Completed, 3L, 3L, 0L, run, Operator, (long?)null),
            (restore.InventoryRemovalId, restore.Scope, restore.Finding, restore.Status, restore.Requested, restore.Restored, restore.Removed, restore.RunId!.Value, restore.Actor, restore.RestoresRemovalId));
        var items = await ItemsAsync(ledger, restore.InventoryRemovalId);
        Assert.Equal(3, items.Count);
        Assert.All(items.Values, i => Assert.Equal((InventoryRemovals.Restored, InventoryFindings.Gone, 3L), (i.Outcome, i.Finding, i.Version!.Value)));

        // An id's history answers both: the removal that took it out of OSDU, and the restore that brought it back.
        Assert.Equal([InventoryRemovals.Restored, InventoryRemovals.Removed], (await ledger.LookupInventoryRemovalItemsAsync(Partition, Id("a"))).Select(i => i.Outcome));
        await using (var db = _db.CreateDbContext())
        {
            var activity = await db.DeliveryActivities.SingleAsync(a => a.Kind == InventoryRemovals.RestoreActivityKind);
            Assert.Equal((restore.ActivityId, "completed", Operator, (Guid?)run), ((long?)activity.ActivityId, activity.Outcome, activity.Actor, activity.RunId));
            Assert.Contains("3 restored, 0 gone for good, 0 skipped, 0 failed", activity.Summary, StringComparison.Ordinal);
        }

        // The next build lists them as any id OSDU serves.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal((3L, 0L), ((await ledger.InventoryCountsAsync(Partition, inventory.InventoryId)).Of(InventoryFindings.Orphan), (await ledger.InventoryCountsAsync(Partition, inventory.InventoryId)).Of(InventoryFindings.Gone)));
    }

    [Fact]
    public async Task Every_gone_id_is_checked_again_before_it_is_restored_and_what_cannot_come_back_is_kept_with_why()
    {
        foreach (var key in new[] { "plain", "purged", "claimed", "stranger", "again", "withheld" })
        {
            Add(key, creator: key == "stranger" ? "someone@elsewhere.com" : Estate);
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        foreach (var key in new[] { "plain", "claimed", "stranger", "again", "withheld" })
        {
            _platform.SoftDelete(Id(key));
        }

        _platform.Purge(Id("purged"));
        await ClaimAsync(ledger, "claimed", "deleted", 3);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(6L, (await ledger.InventoryCountsAsync(Partition, inventory.InventoryId)).Of(InventoryFindings.Gone));

        // Brought back by another tool since the inventory found it gone, and withheld by storage over its legal tags.
        _platform.Undelete(Id("again"));
        _platform.Withheld.Add(Id("withheld"));

        var outcome = await runner.RestoreAsync("WellLogs", new InventoryRestoreRequest { Expected = 6 }, Partition, Guid.NewGuid(), Operator, CancellationToken.None);

        Assert.Equal((1L, 1L, 3L, 1L, (string?)null), (outcome.Restored, outcome.Gone, outcome.Skipped, outcome.Failed, outcome.Error));
        // Only what passed the checks was asked of storage: a ledger's record and an id another identity created were not.
        Assert.Equal(4, _platform.Restores);
        Assert.Equal([Id("plain")], _platform.Restored);
        var items = await ItemsAsync(ledger, outcome.InventoryRemovalId);
        Assert.Equal(InventoryRemovals.Restored, items[Id("plain")].Outcome);
        Assert.Equal(InventoryRemovals.Gone, items[Id("purged")].Outcome);
        Assert.Contains("it was purged, or never written", items[Id("purged")].Reason, StringComparison.Ordinal);
        Assert.Equal((InventoryRemovals.Skipped, (Guid?)_deliveryFlow), (items[Id("claimed")].Outcome, items[Id("claimed")].LedgerFlowId));
        Assert.Contains("a ledger holds its record (deleted)", items[Id("claimed")].Reason, StringComparison.Ordinal);
        Assert.Equal(InventoryRemovals.Skipped, items[Id("stranger")].Outcome);
        Assert.Contains("someone@elsewhere.com created it, not an identity this estate writes as", items[Id("stranger")].Reason, StringComparison.Ordinal);
        Assert.Equal(InventoryRemovals.Skipped, items[Id("again")].Outcome);
        Assert.Contains("OSDU serves it already", items[Id("again")].Reason, StringComparison.Ordinal);
        Assert.Equal(InventoryRemovals.Failed, items[Id("withheld")].Outcome);
        Assert.Contains("(409): storage withholds it while its legal tags are incompliant", items[Id("withheld")].Reason, StringComparison.Ordinal);

        // Only the id brought back is served again in the inventory; the rest stay gone until a build finds otherwise.
        var rows = (await ledger.ListInventoryRecordsAsync(Partition, inventory.InventoryId, null, null, 50)).ToDictionary(r => r.TargetId, StringComparer.Ordinal);
        Assert.Equal(InventoryFindings.Orphan, rows[Id("plain")].Finding);
        Assert.All(new[] { "purged", "claimed", "stranger", "again", "withheld" }, key => Assert.Equal(InventoryFindings.Gone, rows[Id(key)].Finding));
        Assert.StartsWith(
            $"restore {outcome.InventoryRemovalId}, 6 gone id(s) of 'WellLogs' in 'dev', restored in OSDU (the soft delete undone): 1 restored, 1 gone for good, 3 skipped, 1 failed",
            outcome.Describe(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restore_is_refused_before_anything_is_asked_unless_the_flow_declares_removal_the_partition_is_confirmed_and_what_was_shown_holds()
    {
        foreach (var key in new[] { "a", "b", "c" })
        {
            Add(key);
        }

        var ledger = _db.Ledger(_clock);
        await ledger.RegisterAsync(_deliveryFlow);
        var readOnly = Runner(Flow(removal: string.Empty), ledger);
        await readOnly.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var allowing = Runner(Flow(), ledger);
        _platform.SoftDelete(Id("a"));
        _platform.SoftDelete(Id("b"));
        await allowing.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        // A removal from the inventory alone, which asked nothing of OSDU: there is nothing of it to bring back.
        var forgot = await allowing.RemoveAsync(
            "WellLogs", new InventoryRemovalRequest { Finding = InventoryFindings.Gone, Scope = InventoryRemovals.InventoryOnly, Expected = 1, Ids = [Id("c")] },
            Partition, Guid.NewGuid(), Operator, CancellationToken.None);

        async Task<string> RefusedAsync(InventoryRunner runner, InventoryRestoreRequest request, string confirm = Partition)
            => (await Assert.ThrowsAsync<DeliveryException>(() => runner.RestoreAsync("WellLogs", request, confirm, Guid.NewGuid(), Operator, CancellationToken.None))).Message;

        Assert.Contains("allows no restore: its document declares no 'removal'", await RefusedAsync(readOnly, new() { Expected = 2 }), StringComparison.Ordinal);
        Assert.Contains("confirms partition 'prod'", await RefusedAsync(allowing, new() { Expected = 2 }, "prod"), StringComparison.Ordinal);
        Assert.Contains("holds 2 gone id(s) now, and the restore was asked for 3", await RefusedAsync(allowing, new() { Expected = 3 }), StringComparison.Ordinal);
        Assert.Contains("holds no row of", await RefusedAsync(allowing, new() { Expected = 1, Ids = [Id("never-listed")] }), StringComparison.Ordinal);
        Assert.Contains("not both", await RefusedAsync(allowing, new() { Expected = 1, Ids = [Id("a")], Removal = forgot.InventoryRemovalId }), StringComparison.Ordinal);
        Assert.Contains("holds no removal 999", await RefusedAsync(allowing, new() { Expected = 1, Removal = 999 }), StringComparison.Ordinal);
        Assert.Contains(
            "only what a soft delete removed can be brought back",
            await RefusedAsync(allowing, new() { Expected = 1, Removal = forgot.InventoryRemovalId }),
            StringComparison.Ordinal);

        Assert.Equal(0, _platform.Restores);
        var inventory = Assert.Single(await ledger.ListInventoriesAsync(Partition));
        Assert.DoesNotContain(await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10), r => r.Scope == InventoryRemovals.Restore);
        await using var db = _db.CreateDbContext();
        Assert.False(await db.DeliveryActivities.AnyAsync(a => a.Kind == InventoryRemovals.RestoreActivityKind));
    }

    [Fact]
    public async Task What_a_removal_removed_is_restored_from_the_removal_even_after_its_ids_were_removed_from_the_inventory()
    {
        foreach (var key in new[] { "a", "b", "elsewhere" })
        {
            Add(key);
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var removal = await runner.RemoveAsync(
            "WellLogs", new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 2, Ids = [Id("a"), Id("b")] },
            Partition, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal(2L, removal.Removed);

        // An id gone some other way is no part of the removal, and the inventory forgets every gone id.
        _platform.SoftDelete(Id("elsewhere"));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var forgotten = await runner.RemoveAsync("WellLogs", Every(InventoryFindings.Gone, 3, InventoryRemovals.InventoryOnly), Partition, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal(3L, forgotten.Removed);
        Assert.Empty(await ledger.LookupInventoryRecordsAsync(Partition, Id("a")));

        var outcome = await runner.RestoreAsync(
            "WellLogs", new InventoryRestoreRequest { Expected = 2, Removal = removal.InventoryRemovalId }, Partition, Guid.NewGuid(), Operator, CancellationToken.None);

        Assert.Equal((2L, 0L, 0L, (long?)removal.InventoryRemovalId), (outcome.Restored, outcome.Skipped, outcome.Failed, outcome.RestoresRemovalId));
        Assert.Equal([Id("a"), Id("b")], _platform.Restored.Order(StringComparer.Ordinal));
        Assert.Contains($"2 gone id(s) of 'WellLogs' in 'dev' that removal {removal.InventoryRemovalId} removed", outcome.Describe(), StringComparison.Ordinal);
        var restore = (await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10))[0];
        Assert.Equal((InventoryRemovals.Restore, (long?)removal.InventoryRemovalId, false, 2L), (restore.Scope, restore.RestoresRemovalId, restore.NamesIds, restore.Restored));

        // The inventory held no row of them to serve again: the next build lists them again, as new ids.
        Assert.Empty(await ledger.LookupInventoryRecordsAsync(Partition, Id("a")));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(InventoryFindings.Orphan, Assert.Single(await ledger.LookupInventoryRecordsAsync(Partition, Id("a"))).Finding);

        // Asked again, the inventory finds the removal's ids served, so nothing more is asked of storage.
        var again = await runner.RestoreAsync(
            "WellLogs", new InventoryRestoreRequest { Expected = 2, Removal = removal.InventoryRemovalId }, Partition, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal((0L, 2L), (again.Restored, again.Skipped));
        Assert.Equal(2, _platform.Restores);
        Assert.Contains("the inventory finds it orphan now: OSDU serves it", (await ItemsAsync(ledger, again.InventoryRemovalId))[Id("a")].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OSDU_refusing_every_id_of_a_round_for_want_of_permission_stops_the_restore_there_keeping_what_it_recorded()
    {
        for (var i = 0; i < 150; i++)
        {
            Add("orphan-" + i.ToString("D3", CultureInfo.InvariantCulture));
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        await runner.RemoveAsync("WellLogs", Every(InventoryFindings.Orphan, 150), Partition, Guid.NewGuid(), Operator, CancellationToken.None);
        _platform.RemovalsRefused = HttpStatusCode.Forbidden;

        var stopped = await Assert.ThrowsAsync<InventoryRemovalFailedException>(
            () => runner.RestoreAsync("WellLogs", new InventoryRestoreRequest { Expected = 150 }, Partition, Guid.NewGuid(), Operator, CancellationToken.None));

        // One round asked, one id a request, and none after it: the same refusal is not asked again for every id.
        Assert.Equal(InventoryRemovals.RestoreChunk, _platform.Restores);
        Assert.Equal((0L, (long)InventoryRemovals.RestoreChunk), (stopped.Outcome.Restored, stopped.Outcome.Failed));
        Assert.Contains("owner of the records", stopped.Outcome.Error, StringComparison.Ordinal);
        var restore = (await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10))[0];
        Assert.Equal((InventoryRemovals.Restore, InventoryRunStatus.Failed, (long)InventoryRemovals.RestoreChunk), (restore.Scope, restore.Status, restore.Failed));
        Assert.All((await ItemsAsync(ledger, restore.InventoryRemovalId)).Values, i => Assert.Contains("(403)", i.Reason, StringComparison.Ordinal));
        Assert.Equal(150L, (await ledger.InventoryCountsAsync(Partition, inventory.InventoryId)).Of(InventoryFindings.Gone));
        await using var db = _db.CreateDbContext();
        Assert.Equal("failed", (await db.DeliveryActivities.SingleAsync(a => a.Kind == InventoryRemovals.RestoreActivityKind)).Outcome);
    }
}
