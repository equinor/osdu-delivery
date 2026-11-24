using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ids a ledger expects that an inventory's read did not list, when more are expected than one build asks storage for
/// (maxMissingChecks): each build asks for the ones never asked first and then the ones asked longest ago, so successive builds
/// take turns through every one; an id this build did not ask for keeps what storage answered when it last was asked, and one
/// never asked says so rather than that no ledger expects it.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class InventoryMissingCheckTests : IDisposable
{
    private const string Partition = TestLedgers.Partition;
    private const string Kind = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string Estate = "delivery-sp@example.com";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _inventoryFlow = FlowId.Of("welllog-inventory", Partition);
    private readonly Guid _deliveryFlow = FlowId.Of("welllog-delivery");

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose() => _db.Dispose();

    private static string Id(string key) => $"{Partition}:work-product-component--WellLog:{key}";

    private static InventoryScanRow Row(string key)
        => new(Id(key), Kind, 5, Estate, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), Estate, new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc));

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
        return (ledger, await ledger.Inventories.RegisterAsync(_inventoryFlow, "welllog-inventory", "WellLogs", Kind, null, "search", "latest"));
    }

    private async Task BuildAsync(OsduLedger ledger, InventoryState inventory, params InventoryScanRow[] rows)
    {
        var run = await ledger.Inventories.StartRunAsync(_inventoryFlow, inventory.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "tests", "search", Now);
        await ledger.Inventories.AppendScanAsync(_inventoryFlow, run, rows);
        await ledger.Inventories.MergeAsync(_inventoryFlow, inventory.InventoryId, run, Now);
    }

    /// <summary>A record of the delivery flow's ledger delivered at version 5 under <paramref name="key"/>'s id.</summary>
    private async Task DeliveredAsync(OsduLedger ledger, string key)
    {
        var deliveryKey = DeliveryKey.Derive("missing-check-tests", [key]);
        await ledger.UpsertPendingAsync(_deliveryFlow, [new RecordState
        {
            DeliveryKey = deliveryKey, FlowId = _deliveryFlow, SourceKey = key, MappingName = "WellLog", TargetId = Id(key),
            LastSubmissionId = Guid.NewGuid(), PendingDocumentRef = "0:0:10", PendingRenderContext = "{}", PendingMetadataHash = "mh", PendingMetadata = true,
        }]);
        await using var db = _db.CreateDbContext();
        await db.DeliveryRecords
            .Where(r => r.FlowId == _deliveryFlow && r.DeliveryKey == deliveryKey.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "delivered").SetProperty(r => r.TargetVersion, (long?)5).SetProperty(r => r.ClaimedTargetId, Id(key)));
    }

    /// <summary>One reconcile's bounded check, as a build makes it: at most <paramref name="max"/> candidates, none of which storage holds.</summary>
    private async Task<IReadOnlyList<string>> CheckAsync(OsduLedger ledger, InventoryState inventory, int max)
    {
        var candidates = await ledger.Inventories.CandidatesAsync(_inventoryFlow, inventory.InventoryId, null, max);
        await ledger.Inventories.RecordChecksAsync(_inventoryFlow, inventory.InventoryId, candidates.Select(c => new InventoryCheck(c, false, null)).ToList(), Now);
        return candidates.Select(c => c.TargetId).ToList();
    }

    private async Task<Dictionary<string, (string Finding, string? Detail, DateTime FindingUtc, DateTime? CheckedUtc)>> RecordsAsync(InventoryState inventory)
    {
        await using var db = _db.CreateDbContext();
        return await db.DeliveryInventoryRecords.AsNoTracking()
            .Where(r => r.InventoryId == inventory.InventoryId)
            .ToDictionaryAsync(r => r.TargetId, r => (r.Finding, r.Detail, r.FindingUtc, r.CheckedUtc), StringComparer.Ordinal);
    }

    [Fact]
    public async Task Builds_bounded_by_max_missing_checks_take_turns_through_every_id_a_ledger_expects()
    {
        var (ledger, inventory) = await InventoryAsync();
        var keys = new[] { "k1", "k2", "k3", "k4", "k5" };
        foreach (var key in keys)
        {
            await DeliveredAsync(ledger, key);
        }

        await BuildAsync(ledger, inventory, [.. keys.Select(Row), Row("unwanted")]);

        // The next read lists none of them: five ids a ledger expects, and one no ledger does.
        _clock.Advance(TimeSpan.FromHours(1));
        await BuildAsync(ledger, inventory);

        var first = await CheckAsync(ledger, inventory, 2);
        Assert.Equal([Id("k1"), Id("k2")], first);
        var afterFirst = await RecordsAsync(inventory);
        Assert.Equal((InventoryFindings.Missing, (DateTime?)Now), (afterFirst[Id("k1")].Finding, afterFirst[Id("k1")].CheckedUtc));
        Assert.Equal(
            (InventoryFindings.Gone, "a ledger expects it, and this build did not ask storage for it: more ids were expected than maxMissingChecks, and a later build asks", (DateTime?)null),
            (afterFirst[Id("k3")].Finding, afterFirst[Id("k3")].Detail, afterFirst[Id("k3")].CheckedUtc));
        Assert.Equal((InventoryFindings.Gone, "no ledger expects it"), (afterFirst[Id("unwanted")].Finding, afterFirst[Id("unwanted")].Detail));

        // The next build asks for the ones never asked; the ones asked keep what storage answered, from when it first did.
        var foundMissing = Now;
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal([Id("k3"), Id("k4")], await CheckAsync(ledger, inventory, 2));
        var afterSecond = await RecordsAsync(inventory);
        Assert.Equal((InventoryFindings.Missing, foundMissing, (DateTime?)foundMissing), (afterSecond[Id("k1")].Finding, afterSecond[Id("k1")].FindingUtc, afterSecond[Id("k1")].CheckedUtc));
        Assert.Equal((InventoryFindings.Missing, (DateTime?)Now), (afterSecond[Id("k4")].Finding, afterSecond[Id("k4")].CheckedUtc));
        Assert.Equal(InventoryFindings.Gone, afterSecond[Id("k5")].Finding);

        // Then the last one never asked, and the one asked longest ago; and round again, every id asked in its turn.
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal([Id("k5"), Id("k1")], await CheckAsync(ledger, inventory, 2));
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal([Id("k2"), Id("k3")], await CheckAsync(ledger, inventory, 2));
        var settled = await RecordsAsync(inventory);
        Assert.All(keys, key => Assert.Equal(InventoryFindings.Missing, settled[Id(key)].Finding));
        Assert.Equal(foundMissing, settled[Id("k1")].FindingUtc);
        Assert.Equal(Now, settled[Id("k1")].CheckedUtc!.Value.AddHours(1));
    }

    [Fact]
    public async Task With_no_id_asked_of_storage_every_id_no_longer_listed_is_gone_saying_whether_a_ledger_expects_it()
    {
        var (ledger, inventory) = await InventoryAsync();
        await DeliveredAsync(ledger, "kept");
        await BuildAsync(ledger, inventory, Row("kept"), Row("unwanted"));
        _clock.Advance(TimeSpan.FromHours(1));
        await BuildAsync(ledger, inventory);
        await CheckAsync(ledger, inventory, 1);
        Assert.Equal(InventoryFindings.Missing, (await RecordsAsync(inventory))[Id("kept")].Finding);

        // maxMissingChecks: 0 asks storage for nothing (the ledger hands no candidate), and an earlier check does not stand.
        Assert.Empty(await ledger.Inventories.CandidatesAsync(_inventoryFlow, inventory.InventoryId, null, 0));
        await ledger.Inventories.RecordChecksAsync(_inventoryFlow, inventory.InventoryId, [], Now);

        var records = await RecordsAsync(inventory);
        Assert.Equal(
            (InventoryFindings.Gone, "a ledger expects it, and the flow asks storage for no id (maxMissingChecks: 0), so whether it is missing is not known"),
            (records[Id("kept")].Finding, records[Id("kept")].Detail));
        Assert.Equal((InventoryFindings.Gone, "no ledger expects it"), (records[Id("unwanted")].Finding, records[Id("unwanted")].Detail));
    }
}
