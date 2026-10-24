using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Selecting an inventory's ids by what OSDU holds (osdu/docs/reference/flow/inventory.md, Selecting ids by what OSDU holds),
/// over the module's database on SQL Server and a platform that serves search, storage's reads and its removals: a match asks
/// the conditions of the search through the flow's own source and keeps the inventory's ids that hold what they ask, matched
/// by the OSDU id, with the version the index holds and the values each holds; it reads whichever way sends fewer searches,
/// counts the records it finds outside the inventory and the ids at another version, and says how many requests it sent; one
/// that stops keeps no id; a removal takes the ids of a finding its selection holds, held to the count the operator was shown
/// and to the build the match was made against; and selections past their week are retired.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class InventoryMatchTests : IDisposable
{
    private const string Partition = "dev";
    private const string Kind = "osdu:wks:work-product-component--WellLog:1.0.0";
    private const string Estate = "delivery-sp@contoso.com";
    private const string Operator = "user:operator";
    private const string FacilityA = "Wellbore A-1";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _search = new();
    private readonly StoragePlatform _platform;
    private readonly Guid _deliveryFlow = FlowId.Of("welllog-delivery");

    public InventoryMatchTests()
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

    private JsonObject Add(string key, string facility, long version = 3, string creator = Estate)
    {
        var record = _search.Add(Id(key), Kind, new JsonObject { ["Name"] = key, ["FacilityName"] = facility });
        record["version"] = version;
        record["createUser"] = creator;
        record["createTime"] = "2026-09-01T08:00:00.000Z";
        record["modifyUser"] = creator;
        record["modifyTime"] = "2026-09-02T08:00:00.000Z";
        return record;
    }

    private JsonObject Record(string key) => _search.Records.First(r => r["id"]!.GetValue<string>() == Id(key));

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

    private async Task ClaimAsync(OsduLedger ledger, string key, string status, long? version)
    {
        var deliveryKey = DeliveryKey.Derive("inventory-match", [key]);
        await ledger.UpsertPendingAsync(_deliveryFlow, [new RecordState
        {
            DeliveryKey = deliveryKey, FlowId = _deliveryFlow, SourceKey = key, MappingName = "WellLog", TargetId = Id(key),
            LastSubmissionId = Guid.NewGuid(), PendingDocumentRef = "0:0:10", PendingRenderContext = "{}", PendingMetadataHash = "mh", PendingMetadata = true,
        }]);
        await using var db = _db.CreateDbContext();
        await db.DeliveryRecords
            .Where(r => r.FlowId == _deliveryFlow && r.DeliveryKey == deliveryKey.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status).SetProperty(r => r.TargetVersion, version).SetProperty(r => r.ClaimedTargetId, Id(key)));
    }

    /// <summary>A selection of the inventory as the control plane opens one: the conditions as written, and as the search is asked them.</summary>
    private async Task<long> SelectAsync(OsduLedger ledger, InventoryState inventory, params ExplorerFilter[] filters)
    {
        var search = new ExplorerSearch { Kind = Kind, Filters = filters };
        Assert.Null(search.Problem());
        var conditions = JsonSerializer.Serialize(filters.Select(f => new { path = f.Path, value = f.Value }));
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(conditions)));
        return await ledger.CreateInventorySelectionAsync(
            Partition, new InventorySelectionStart(inventory.InventoryId, Operator, conditions, key, search.ToJson(), inventory.LastBuildRunId, _clock.GetUtcNow().UtcDateTime));
    }

    private static ExplorerFilter Facility(string value, ExplorerCondition condition = ExplorerCondition.Is)
        => new() { Path = "data.FacilityName", Index = OsduFieldIndex.Text, Condition = condition, Value = value };

    /// <summary>Every id of a selection, with what the match read of it.</summary>
    private static async Task<Dictionary<string, InventorySelectionRecord>> SelectedAsync(OsduLedger ledger, long selectionId, string? finding = null)
    {
        var all = new Dictionary<string, InventorySelectionRecord>(StringComparer.Ordinal);
        long? after = null;
        while (true)
        {
            var page = await ledger.ListInventorySelectionRecordsAsync(Partition, selectionId, finding, after, 500);
            if (page.Count == 0)
            {
                return all;
            }

            foreach (var selected in page)
            {
                all.Add(selected.Record.TargetId, selected);
            }

            after = page[^1].Record.InventoryRecordId;
        }
    }

    [Fact]
    public async Task A_selective_condition_is_read_by_what_it_finds_and_the_inventory_s_ids_that_hold_it_are_kept_with_their_values()
    {
        Add("a-orphan-1", FacilityA);
        Add("a-orphan-2", FacilityA);
        Add("a-tracked", FacilityA);
        Add("a-changed", FacilityA);
        for (var i = 0; i < 40; i++)
        {
            Add("other-" + i.ToString("D2", CultureInfo.InvariantCulture), "Wellbore B-" + i.ToString(CultureInfo.InvariantCulture));
        }

        var ledger0 = _db.Ledger(_clock);
        await ledger0.RegisterAsync(_deliveryFlow);
        await ClaimAsync(ledger0, "a-tracked", "delivered", 3);
        var (runner, ledger, inventory) = await BuiltAsync(Flow());

        // After the build: a record of the kind written since, which the inventory holds no row of, and one of its ids changed.
        Add("a-late", FacilityA);
        Record("a-changed")["version"] = 4;
        var sent = _search.Calls.Count;
        var selectionId = await SelectAsync(ledger, inventory, Facility(FacilityA));

        var outcome = await runner.MatchAsync("WellLogs", selectionId, Guid.NewGuid(), Operator, CancellationToken.None);

        Assert.Equal((InventorySelections.Completed, InventorySelections.ByConditions, (string?)null), (outcome.Status, outcome.Direction, outcome.Error));
        Assert.Equal((44L, 4L, 1L, 1L), (outcome.Candidates, outcome.Matched, outcome.Outside, outcome.Changed));
        Assert.Equal(_search.Calls.Count - sent, outcome.Requests);
        Assert.Equal("data.FacilityName.keyword:\"Wellbore A-1\"", outcome.Query);

        var selection = await ledger.GetInventorySelectionAsync(Partition, selectionId);
        Assert.NotNull(selection);
        Assert.Equal((InventorySelections.Completed, inventory.LastBuildRunId, 4L, 5L, 5L), (selection.Status, selection.BuildRunId, selection.Matched, selection.ToCheck, selection.Checked));
        Assert.Equal("""["data.FacilityName"]""", selection.ColumnsJson);
        Assert.NotNull(selection.CompletedUtc);

        var selected = await SelectedAsync(ledger, selectionId);
        Assert.Equal([Id("a-changed"), Id("a-orphan-1"), Id("a-orphan-2"), Id("a-tracked")], selected.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([FacilityA], InventorySelectionValues.Parse(selected[Id("a-orphan-1")].ValuesJson)["data.FacilityName"]);
        Assert.Equal((4L, 3L), (selected[Id("a-changed")].MatchedVersion!.Value, selected[Id("a-changed")].Record.Version!.Value));

        // The selection's ids by the finding the inventory gives each now, and a page of one finding.
        var counts = await ledger.InventorySelectionCountsAsync(Partition, selectionId);
        Assert.Equal((3L, 1L), (counts.Of(InventoryFindings.Orphan), counts.Of(InventoryFindings.Tracked)));
        Assert.Equal(3, (await SelectedAsync(ledger, selectionId, InventoryFindings.Orphan)).Count);

        // The same conditions asked again of the same build find this match; of another build, none.
        Assert.Equal(selectionId, (await ledger.FindInventorySelectionAsync(Partition, inventory.InventoryId, selection.ConditionsKey, inventory.LastBuildRunId))?.InventorySelectionId);
        Assert.Null(await ledger.FindInventorySelectionAsync(Partition, inventory.InventoryId, selection.ConditionsKey, inventory.LastBuildRunId + 1));
    }

    [Fact]
    public async Task A_condition_that_finds_far_more_records_than_the_inventory_holds_is_asked_of_its_ids_a_chunk_at_a_time()
    {
        for (var i = 0; i < 1200; i++)
        {
            Add("listed-" + i.ToString("D4", CultureInfo.InvariantCulture), i % 3 == 0 ? FacilityA : "Wellbore C");
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow());

        // The kind grew well past the inventory since its build: the condition finds 3,400 records, the inventory serves 1,200 ids.
        for (var i = 0; i < 3000; i++)
        {
            Add("since-" + i.ToString("D4", CultureInfo.InvariantCulture), FacilityA);
        }

        var sent = _search.Calls.Count;
        var selectionId = await SelectAsync(ledger, inventory, Facility(FacilityA));

        var outcome = await runner.MatchAsync("WellLogs", selectionId, Guid.NewGuid(), Operator, CancellationToken.None);

        Assert.Equal((InventorySelections.Completed, InventorySelections.ByIds), (outcome.Status, outcome.Direction));
        Assert.Equal((1200L, 400L, 0L), (outcome.Candidates, outcome.Matched, outcome.Outside));

        // One count, then a search per 500 ids: 500, 500 and 200, each asking the condition of the ids it names.
        var searches = _search.Calls.Skip(sent).ToList();
        Assert.Equal(4, searches.Count);
        Assert.Equal(4L, outcome.Requests);
        var chunks = searches.Skip(1).Select(c => JsonNode.Parse(c.Body!)!["query"]!.GetValue<string>()).ToList();
        Assert.All(chunks, q => Assert.StartsWith("(data.FacilityName.keyword:\"Wellbore A-1\") AND (id:(", q, StringComparison.Ordinal));
        Assert.Equal([500, 500, 200], chunks.Select(q => q.Split(" OR ").Length));
        Assert.Equal(400, (await SelectedAsync(ledger, selectionId)).Count);
    }

    [Fact]
    public async Task A_match_the_search_refuses_fails_its_selection_keeping_no_id_and_a_match_of_no_built_inventory_never_starts()
    {
        for (var i = 0; i < 5; i++)
        {
            Add("a-" + i.ToString(CultureInfo.InvariantCulture), FacilityA);
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var selectionId = await SelectAsync(ledger, inventory, Facility(FacilityA));

        // The read of what the condition finds is refused (the count before it is answered).
        _search.Fail = body => body is not null && body.Contains("\"version\"", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null;
        var failed = await Assert.ThrowsAsync<InventoryMatchFailedException>(() => runner.MatchAsync("WellLogs", selectionId, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Equal(InventorySelections.Failed, failed.Outcome.Status);
        var selection = await ledger.GetInventorySelectionAsync(Partition, selectionId);
        Assert.NotNull(selection);
        Assert.Equal((InventorySelections.Failed, 0L), (selection.Status, selection.Matched));
        Assert.False(string.IsNullOrWhiteSpace(selection.Error));
        Assert.Empty(await SelectedAsync(ledger, selectionId));

        // Asked again, the same selection is matched afresh by the run tried again.
        _search.Fail = null;
        var again = await runner.MatchAsync("WellLogs", selectionId, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal((InventorySelections.Completed, 5L), (again.Status, again.Matched));
        Assert.Null((await ledger.GetInventorySelectionAsync(Partition, selectionId))!.Error);

        // An inventory the flow does not declare holds nothing to match: the selection closes with why.
        var orphaned = await SelectAsync(ledger, inventory, Facility("nothing"));
        var missing = await Assert.ThrowsAsync<DeliveryException>(() => runner.MatchAsync("Wells", orphaned, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("no inventory named 'Wells'", missing.Message, StringComparison.Ordinal);
        Assert.Equal(InventorySelections.Failed, (await ledger.GetInventorySelectionAsync(Partition, orphaned))!.Status);
        var unknown = await Assert.ThrowsAsync<DeliveryException>(() => runner.MatchAsync("WellLogs", 987654321, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("holds no selection 987654321", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_removal_takes_only_the_ids_of_the_finding_its_selection_holds_held_to_the_count_shown_and_to_the_build_matched()
    {
        foreach (var key in new[] { "a-1", "a-2", "a-3" })
        {
            Add(key, FacilityA);
        }

        foreach (var key in new[] { "b-1", "b-2", "b-3" })
        {
            Add(key, "Wellbore B-1");
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var selectionId = await SelectAsync(ledger, inventory, Facility(FacilityA));
        await runner.MatchAsync("WellLogs", selectionId, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal(6L, (await ledger.InventoryCountsAsync(Partition, inventory.InventoryId)).Of(InventoryFindings.Orphan));

        var shownAsTwo = new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 2, Selection = selectionId };
        var count = await Assert.ThrowsAsync<DeliveryException>(() => runner.RemoveAsync("WellLogs", shownAsTwo, Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains($"Selection {selectionId} of inventory 'WellLogs' holds 3 orphan id(s) now, and the removal was asked for 2", count.Message, StringComparison.Ordinal);
        var both = new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 1, Ids = [Id("a-1")], Selection = selectionId };
        Assert.Contains("only when it names no ids", (await Assert.ThrowsAsync<DeliveryException>(() => runner.RemoveAsync("WellLogs", both, Partition, Guid.NewGuid(), Operator, CancellationToken.None))).Message, StringComparison.Ordinal);
        Assert.Empty(_platform.SoftDeleted);

        var run = Guid.NewGuid();
        var outcome = await runner.RemoveAsync("WellLogs", shownAsTwo with { Expected = 3 }, Partition, run, Operator, CancellationToken.None);

        Assert.Equal((3L, 0L, 0L, 0L), (outcome.Removed, outcome.Gone, outcome.Skipped, outcome.Failed));
        Assert.Equal([Id("a-1"), Id("a-2"), Id("a-3")], _platform.SoftDeleted.Order(StringComparer.Ordinal));
        var counts = await ledger.InventoryCountsAsync(Partition, inventory.InventoryId);
        Assert.Equal((3L, 3L), (counts.Of(InventoryFindings.Orphan), counts.Of(InventoryFindings.Gone)));
        var removal = Assert.Single(await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10));
        Assert.Equal((selectionId, false, 3L), (removal.SelectionId, removal.NamesIds, removal.Requested));
        await using (var db = _db.CreateDbContext())
        {
            var activity = await db.DeliveryActivities.SingleAsync(a => a.Kind == InventoryRemovals.ActivityKind);
            Assert.Contains($"\"selection\":{selectionId.ToString(CultureInfo.InvariantCulture)}", activity.ParametersJson, StringComparison.Ordinal);
        }

        // The selection's ids now count under the finding the removal left them with.
        Assert.Equal(3L, (await ledger.InventorySelectionCountsAsync(Partition, selectionId)).Of(InventoryFindings.Gone));

        // A build since the match makes the selection stale, so a removal of what it holds is refused until it is matched again.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var stale = await Assert.ThrowsAsync<DeliveryException>(() => runner.RemoveAsync("WellLogs", shownAsTwo with { Expected = 3 }, Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("was built again since", stale.Message, StringComparison.Ordinal);

        // A selection whose match did not complete holds nothing a removal may take.
        var rebuilt = Assert.Single(await ledger.ListInventoriesAsync(Partition));
        var unmatched = await SelectAsync(ledger, rebuilt, Facility("Wellbore B-1"));
        var queued = await Assert.ThrowsAsync<DeliveryException>(() => runner.RemoveAsync(
            "WellLogs", shownAsTwo with { Expected = 3, Selection = unmatched }, Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("is queued, not completed", queued.Message, StringComparison.Ordinal);
        Assert.Equal(3, _platform.SoftDeleted.Count);
    }

    [Fact]
    public async Task Selections_past_their_week_are_retired_and_one_a_removal_took_its_ids_from_is_kept_expired()
    {
        Add("a-1", FacilityA);
        Add("a-2", FacilityA);
        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var removedBy = await SelectAsync(ledger, inventory, Facility(FacilityA));
        await runner.MatchAsync("WellLogs", removedBy, Guid.NewGuid(), Operator, CancellationToken.None);
        await runner.RemoveAsync(
            "WellLogs", new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 2, Selection = removedBy },
            Partition, Guid.NewGuid(), Operator, CancellationToken.None);
        var unused = await SelectAsync(ledger, inventory, Facility("nothing"));
        await runner.MatchAsync("WellLogs", unused, Guid.NewGuid(), Operator, CancellationToken.None);

        // Nothing is younger than a week yet, so a sweep takes nothing.
        Assert.Equal(new InventorySelectionSweep(0, 0), await ledger.SweepInventorySelectionsAsync(Partition, _clock.GetUtcNow().UtcDateTime - InventorySelections.KeptFor));

        _clock.Advance(InventorySelections.KeptFor + TimeSpan.FromHours(1));
        var recent = await SelectAsync(ledger, inventory, Facility(FacilityA));
        var swept = await ledger.SweepInventorySelectionsAsync(Partition, _clock.GetUtcNow().UtcDateTime - InventorySelections.KeptFor);

        Assert.Equal(new InventorySelectionSweep(1, 1), swept);
        Assert.Null(await ledger.GetInventorySelectionAsync(Partition, unused));
        var expired = await ledger.GetInventorySelectionAsync(Partition, removedBy);
        Assert.Equal(InventorySelections.Expired, expired?.Status);
        Assert.Empty(await SelectedAsync(ledger, removedBy));
        Assert.Equal(InventorySelections.Queued, (await ledger.GetInventorySelectionAsync(Partition, recent))?.Status);
        Assert.Equal(removedBy, Assert.Single(await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10)).SelectionId);
    }
}
