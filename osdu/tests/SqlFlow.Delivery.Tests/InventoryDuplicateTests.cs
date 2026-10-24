using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Finding the duplicate records of an inventory (osdu/docs/reference/flow/inventory-duplicates.md), over the
/// module's database on SQL Server and a platform that serves search, storage's reads and its removals: a check reads every
/// record's key through the search, groups the inventory's ids that share it, reads each group's copies from storage and keeps
/// the most complete; a group the rules cannot decide is left for review, and an operator's choice decides it; the copies picked
/// to remove are made a selection, and a removal by it takes them, each only while storage holds the copy its group keeps; a
/// check that stops keeps no group; and checks past their week are retired.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class InventoryDuplicateTests : IDisposable
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

    public InventoryDuplicateTests()
    {
        _platform = new StoragePlatform(_search);
    }

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

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

    private void Add(string key, string data, string creator = Estate, string kind = Kind, string? id = null)
    {
        var record = _search.Add(id ?? Id(key), kind, JsonNode.Parse(data)!.AsObject());
        record["version"] = 3;
        record["createUser"] = creator;
        record["createTime"] = "2026-09-01T08:00:00.000Z";
        record["modifyUser"] = creator;
        record["modifyTime"] = "2026-09-02T08:00:00.000Z";
    }

    /// <summary>The estate the first tests read: four copies of one log, two of another, and logs no other shares a key with.</summary>
    private void AddEstate()
    {
        Add("full", """{"Name":"GR RUN 1","WellboreID":"wb-1","Curves":[{"Mnemonic":"GR"},{"Mnemonic":"DEPT"}],"DDMSDatasets":["urn://wdms-1/uuid:1"],"ExtensionProperties":{"wdms":{"bulkURI":"urn://wdms-1/uuid:1"}}}""");
        Add("thin", """{"Name":"gr run 1 ","WellboreID":"wb-1"}""");
        Add("thin-2", """{"Name":"GR  RUN 1","WellboreID":"wb-1","Curves":[{"Mnemonic":"GR"}]}""");
        Add("foreign", """{"Name":"GR RUN 1","WellboreID":"wb-1"}""", creator: "someone@else.example");
        Add("other", """{"Name":"DT","WellboreID":"wb-1"}""");
        Add("unkeyed", """{"Description":"no key"}""");
        Add("pair-a", """{"Name":"SP","WellboreID":"wb-2","Curves":[{"Mnemonic":"SP"}]}""");
        Add("pair-b", """{"Name":"sp","WellboreID":"wb-2","Curves":[{"Mnemonic":"SP"}]}""");
    }

    private async Task<(InventoryRunner Runner, OsduLedger Ledger, InventoryState Inventory)> BuiltAsync(string yaml, Func<OsduLedger, Task>? claims = null)
    {
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterAsync(_deliveryFlow);
        if (claims is not null)
        {
            await claims(ledger);
        }

        var runner = Runner(yaml, ledger);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        return (runner, ledger, Assert.Single(await ledger.ListInventoriesAsync(Partition)));
    }

    private InventoryRunner Runner(string yaml, OsduLedger ledger)
    {
        var flow = new DeliveryDocumentLoader().ParseInventory(yaml, "flows/inventory.yaml").ForPartition(Partition);
        return new InventoryRunner(Samples.Engine(ledger, _clock), flow, new Dictionary<string, string>(), Samples.Logger<InventoryRunner>(), _platform, allowLoopback: true);
    }

    private async Task ClaimAsync(OsduLedger ledger, string key)
    {
        var deliveryKey = DeliveryKey.Derive("inventory-duplicates", [key]);
        await ledger.UpsertPendingAsync(_deliveryFlow, [new RecordState
        {
            DeliveryKey = deliveryKey, FlowId = _deliveryFlow, SourceKey = key, MappingName = "WellLog", TargetId = Id(key),
            LastSubmissionId = Guid.NewGuid(), PendingDocumentRef = "0:0:10", PendingRenderContext = "{}", PendingMetadataHash = "mh", PendingMetadata = true,
        }]);
        await using var db = _db.CreateDbContext();
        await db.DeliveryRecords
            .Where(r => r.FlowId == _deliveryFlow && r.DeliveryKey == deliveryKey.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "delivered").SetProperty(r => r.TargetVersion, 3L).SetProperty(r => r.ClaimedTargetId, Id(key)));
    }

    /// <summary>A check of the inventory as the control plane opens one: the key checked, in its one spelling and hashed.</summary>
    private async Task<long> CheckAsync(OsduLedger ledger, InventoryState inventory, params (string Path, string? Compare)[] key)
    {
        var (parsed, problem) = InventoryDuplicateKey.Of(key.Select(k => ((string?)k.Path, k.Compare)).ToList());
        Assert.Null(problem);
        return await ledger.CreateInventoryDuplicateCheckAsync(
            Partition, new InventoryDuplicateCheckStart(inventory.InventoryId, Operator, parsed!.ToJson(), parsed.Hash(), inventory.LastBuildRunId, Now));
    }

    private static readonly (string, string?)[] LogKey = [("data.WellboreID", "exact"), ("data.Name", "loose")];

    private static async Task<IReadOnlyList<InventoryDuplicateGroupPage>> GroupsAsync(OsduLedger ledger, long checkId, string? outcome = null)
        => await ledger.ListInventoryDuplicateGroupsAsync(Partition, checkId, outcome, null, 500, InventoryDuplicates.MaxCopiesPicked);

    private static InventoryDuplicateMemberState CopyOf(InventoryDuplicateGroupPage group, string key)
        => group.Members.Single(m => m.Record.TargetId == Id(key));

    /// <summary>An operator's choice as the control plane writes one: the group judged with the copy kept, or judged afresh with none.</summary>
    private async Task ChooseAsync(OsduLedger ledger, long checkId, int groupNumber, string? keep)
    {
        var members = await ledger.ListInventoryDuplicateMembersAsync(Partition, checkId, groupNumber, null, InventoryDuplicates.MaxCopiesPicked);
        var copies = members.Select(m => new DuplicateCopy(
            m.Record.InventoryRecordId, m.Record.TargetId, m.Record.Finding, m.StorageState!, m.StoredVersion, m.Attributes, m.MentionedBy)).ToList();
        var chosen = keep is null ? (long?)null : members.Single(m => m.Record.TargetId == Id(keep)).Record.InventoryRecordId;
        var judgement = InventoryDuplicatePicker.Judge(groupNumber, copies, new HashSet<string>(StringComparer.Ordinal) { InventoryFindings.Orphan }, chosen);
        await ledger.WriteInventoryDuplicateChoiceAsync(Partition, checkId, judgement, keep is null ? DuplicateChoice.Clear : DuplicateChoice.Choose, Operator, Now);
    }

    private async Task<long> SelectAsync(OsduLedger ledger, long checkId)
        => await ledger.CreateInventoryDuplicateSelectionAsync(
            Partition, checkId, Operator, new ExplorerSearch { Kind = Kind }.ToJson(), InventoryDuplicateColumns.Of(["data.WellboreID", "data.Name"], ExplorerSearch.MaxColumns), Now);

    private static async Task<List<InventorySelectionRecord>> SelectedAsync(OsduLedger ledger, long selectionId)
        => [.. await ledger.ListInventorySelectionRecordsAsync(Partition, selectionId, null, null, 1000)];

    [Fact]
    public async Task A_check_groups_the_ids_sharing_a_key_keeps_the_most_complete_copy_and_a_removal_by_its_selection_takes_the_others()
    {
        AddEstate();
        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var checkId = await CheckAsync(ledger, inventory, LogKey);
        var sent = _search.Calls.Count;
        var reads = _platform.RecordReads;

        var outcome = await runner.FindDuplicatesAsync("WellLogs", checkId, Guid.NewGuid(), Operator, CancellationToken.None);

        Assert.Equal((InventoryDuplicates.Completed, (string?)null), (outcome.Status, outcome.Error));
        Assert.Equal((8L, 7L, 1L, 0L), (outcome.Candidates, outcome.Keyed, outcome.Unkeyed, outcome.Outside));
        Assert.Equal((2L, 6L, 1L, 1L, 2L), (outcome.Groups, outcome.Copies, outcome.Decided, outcome.Review, outcome.ToRemove));
        Assert.Equal(1, _platform.RecordReads - reads);
        Assert.Equal(_search.Calls.Count - sent + 1, outcome.Requests);

        // The key is read with only its properties returned; the copies are read whole from storage, once each.
        var keyRead = JsonNode.Parse(_search.Calls.Skip(sent).First(c => c.Body!.Contains("returnedFields", StringComparison.Ordinal)).Body!)!;
        Assert.Equal(["id", "version", "data.WellboreID", "data.Name"], keyRead["returnedFields"]!.AsArray().Select(f => f!.GetValue<string>()));

        var check = await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId);
        Assert.NotNull(check);
        Assert.Equal((InventoryDuplicates.Completed, (string?)null, inventory.LastBuildRunId, 2L), (check.Status, check.Phase, check.BuildRunId, check.GroupsJudged));
        var groups = await GroupsAsync(ledger, checkId);
        Assert.Equal([1, 2], groups.Select(g => g.Group.GroupNumber));

        // The four copies of GR RUN 1, the largest group first: the one holding its bulk data and curves kept.
        var log = groups[0];
        Assert.Equal((4, InventoryDuplicates.Picked), (log.Group.Copies, log.Group.Outcome));
        Assert.Equal(CopyOf(log, "full").Record.InventoryRecordId, log.Group.KeptInventoryRecordId);
        Assert.Equal("it holds the most values: 6", log.Group.Reason);
        Assert.Equal(
            [(Id("foreign"), InventoryDuplicates.Leave, 2), (Id("full"), InventoryDuplicates.Keep, 6), (Id("thin"), InventoryDuplicates.Remove, 2), (Id("thin-2"), InventoryDuplicates.Remove, 3)],
            log.Members.Select(m => (m.Record.TargetId, m.Role!, m.Attributes!.Value)).OrderBy(m => m.TargetId, StringComparer.Ordinal));
        Assert.All(log.Members, m => Assert.Equal((InventoryDuplicates.Held, 3L, 3L, 0L), (m.StorageState!, m.StoredVersion!.Value, m.IndexedVersion!.Value, m.MentionedBy!.Value)));
        Assert.Equal(["gr run 1 "], InventorySelectionValues.Parse(CopyOf(log, "thin").ValuesJson)["data.Name"]);
        Assert.Contains("only its owner can remove it", CopyOf(log, "foreign").Reason, StringComparison.Ordinal);

        // The two copies of SP hold as many values: which to keep is the operator's to choose.
        var pair = groups[1];
        Assert.Equal((2, InventoryDuplicates.Review, (long?)null), (pair.Group.Copies, pair.Group.Outcome, pair.Group.KeptInventoryRecordId));
        Assert.StartsWith("2 copies hold the most values (3 each", pair.Group.Reason, StringComparison.Ordinal);
        Assert.Equal(1L, (await ledger.InventoryDuplicateOutcomesAsync(Partition, checkId))[InventoryDuplicates.Review]);
        Assert.Equal(pair.Group.GroupNumber, Assert.Single(await GroupsAsync(ledger, checkId, InventoryDuplicates.Review)).Group.GroupNumber);

        // The same key asked again of the same build finds this check; of another build, none.
        Assert.Equal(checkId, (await ledger.FindInventoryDuplicateCheckAsync(Partition, inventory.InventoryId, check.KeyHash, inventory.LastBuildRunId))?.InventoryDuplicateCheckId);
        Assert.Null(await ledger.FindInventoryDuplicateCheckAsync(Partition, inventory.InventoryId, check.KeyHash, inventory.LastBuildRunId + 1));

        // The copies picked to remove made a selection: the two thin copies, each beside the copy its group keeps.
        var selectionId = await SelectAsync(ledger, checkId);
        var selection = await ledger.GetInventorySelectionAsync(Partition, selectionId);
        Assert.NotNull(selection);
        Assert.Equal((InventorySelections.Completed, checkId, inventory.LastBuildRunId, 2L, "[]"), (selection.Status, selection.DuplicateCheckId, selection.BuildRunId, selection.Matched, selection.ConditionsJson));
        Assert.Equal("""["data.WellboreID","data.Name"]""", selection.ColumnsJson);
        Assert.Equal([Id("thin"), Id("thin-2")], (await SelectedAsync(ledger, selectionId)).Select(s => s.Record.TargetId).Order(StringComparer.Ordinal));
        Assert.Equal(selectionId, (await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId))!.SelectionId);
        Assert.Null(await ledger.FindInventorySelectionAsync(Partition, inventory.InventoryId, selection.ConditionsKey, inventory.LastBuildRunId));

        // A match never fills a selection a check made.
        var refused = await Assert.ThrowsAsync<DeliveryException>(() => runner.MatchAsync("WellLogs", selectionId, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains($"holds the copies duplicate check {checkId} picked to remove", refused.Message, StringComparison.Ordinal);

        var removal = await runner.RemoveAsync(
            "WellLogs", new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 2, Selection = selectionId },
            Partition, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal((2L, 0L, 0L, 0L), (removal.Removed, removal.Gone, removal.Skipped, removal.Failed));
        Assert.Equal([Id("thin"), Id("thin-2")], _platform.SoftDeleted.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_operator_s_choice_decides_a_group_for_review_and_a_removal_leaves_every_copy_whose_kept_copy_storage_no_longer_holds()
    {
        AddEstate();
        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var checkId = await CheckAsync(ledger, inventory, LogKey);
        await runner.FindDuplicatesAsync("WellLogs", checkId, Guid.NewGuid(), Operator, CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await ChooseAsync(ledger, checkId, 2, keep: "pair-a");
        var pair = (await GroupsAsync(ledger, checkId))[1];
        Assert.Equal((InventoryDuplicates.Chosen, Operator), (pair.Group.Outcome, pair.Group.ChosenBy));
        Assert.Equal(Now, pair.Group.ChosenUtc);
        Assert.Equal((InventoryDuplicates.Keep, InventoryDuplicates.Remove), (CopyOf(pair, "pair-a").Role, CopyOf(pair, "pair-b").Role));
        var chosen = await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId);
        Assert.Equal((2L, 0L, 3L, Now), (chosen!.Decided, chosen.Review, chosen.ToRemove, chosen.ChosenUtc!.Value));

        // A choice taken back leaves the group to the check's own rules, which leave it for review again.
        await ChooseAsync(ledger, checkId, 2, keep: null);
        var cleared = (await GroupsAsync(ledger, checkId))[1];
        Assert.Equal((InventoryDuplicates.Review, (string?)null), (cleared.Group.Outcome, cleared.Group.ChosenBy));
        var back = await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId);
        Assert.Equal((1L, 1L, 2L), (back!.Decided, back.Review, back.ToRemove));

        await ChooseAsync(ledger, checkId, 2, keep: "pair-a");
        var selectionId = await SelectAsync(ledger, checkId);
        Assert.Equal(3, (await SelectedAsync(ledger, selectionId)).Count);

        // Between the selection and the removal the copy the first group keeps went: its copies stay, the pair's goes.
        _platform.DeletedAfterListing.Add(Id("full"));
        var removal = await runner.RemoveAsync(
            "WellLogs", new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 3, Selection = selectionId },
            Partition, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal((1L, 2L), (removal.Removed, removal.Skipped));
        Assert.Equal([Id("pair-b")], _platform.SoftDeleted);
        var record = Assert.Single(await ledger.ListInventoryRemovalsAsync(Partition, inventory.InventoryId, 10));
        var skipped = await ledger.ListInventoryRemovalItemsAsync(Partition, record.InventoryRemovalId, InventoryRemovals.Skipped, null, 10);
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, s => Assert.Equal($"storage no longer holds {Id("full")}, the copy its group keeps, so this copy is left: removing it could leave no copy of the record", s.Reason));

        // A build since the check makes its selection stale, so a removal of what it holds is refused.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var stale = await Assert.ThrowsAsync<DeliveryException>(() => runner.RemoveAsync(
            "WellLogs", new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 2, Selection = selectionId },
            Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains($"holds the picks of duplicate check {checkId}, made against an earlier build", stale.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_group_is_left_for_review_when_the_ledger_s_copy_is_the_less_complete_when_a_copy_to_remove_is_named_and_when_the_key_is_too_broad()
    {
        // The delivered copy holds less than an orphan of it: the delivery leaves out what the orphan holds.
        Add("delivered", """{"Name":"GR","WellboreID":"wb-1"}""");
        Add("richer", """{"Name":"GR","WellboreID":"wb-1","Curves":[{"Mnemonic":"GR"}]}""");

        // An orphan copy another record names: removing it would leave that record pointing at nothing.
        Add("kept", """{"Name":"DT","WellboreID":"wb-2","Curves":[{"Mnemonic":"DT"},{"Mnemonic":"DEPT"}]}""");
        Add("named", """{"Name":"DT","WellboreID":"wb-2"}""");
        Add("trajectory", $$"""{"LogID":"{{Id("named")}}:"}""", kind: "osdu:wks:work-product-component--WellboreTrajectory:1.0.0",
            id: $"{Partition}:work-product-component--WellboreTrajectory:t-1");

        // A key every one of more than a hundred records shares is no record written more than once.
        for (var i = 0; i < InventoryDuplicates.MaxCopiesPicked + 1; i++)
        {
            Add("broad-" + i.ToString("D3", CultureInfo.InvariantCulture), """{"Name":"UNKNOWN","WellboreID":"wb-9"}""");
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow(), claims: ledger => ClaimAsync(ledger, "delivered"));
        var checkId = await CheckAsync(ledger, inventory, LogKey);
        var outcome = await runner.FindDuplicatesAsync("WellLogs", checkId, Guid.NewGuid(), Operator, CancellationToken.None);

        Assert.Equal((3L, 0L, 3L, 0L), (outcome.Groups, outcome.Decided, outcome.Review, outcome.ToRemove));
        Assert.Contains(outcome.Notes, n => n.StartsWith("1 group(s) hold more than 100 copies", StringComparison.Ordinal));
        var groups = await GroupsAsync(ledger, checkId);

        var broad = groups[0];
        Assert.Equal((InventoryDuplicates.MaxCopiesPicked + 1, InventoryDuplicates.Review), (broad.Group.Copies, broad.Group.Outcome));
        Assert.StartsWith("101 copies share the key, more than the 100 a group is picked from", broad.Group.Reason, StringComparison.Ordinal);
        Assert.Empty(broad.Members);
        var unread = await ledger.ListInventoryDuplicateMembersAsync(Partition, checkId, broad.Group.GroupNumber, null, 1000);
        Assert.Equal(101, unread.Count);
        Assert.All(unread, m => Assert.Equal((InventoryDuplicates.ForReview, (string?)null, (int?)null), (m.Role!, m.StorageState, m.Attributes)));

        var delivered = groups.Single(g => g.Members.Any(m => m.Record.TargetId == Id("delivered")));
        Assert.Equal(InventoryFindings.Tracked, CopyOf(delivered, "delivered").Record.Finding);
        Assert.Equal(InventoryDuplicates.Review, delivered.Group.Outcome);
        Assert.StartsWith($"the copy a ledger holds, {Id("delivered")}, holds 2 values and {Id("richer")} holds 3", delivered.Group.Reason, StringComparison.Ordinal);

        var named = groups.Single(g => g.Members.Any(m => m.Record.TargetId == Id("named")));
        Assert.Equal((InventoryDuplicates.Review, 1L, 0L), (named.Group.Outcome, CopyOf(named, "named").MentionedBy!.Value, CopyOf(named, "kept").MentionedBy!.Value));
        Assert.StartsWith($"{Id("named")}, picked to remove, is named by 1 record(s)", named.Group.Reason, StringComparison.Ordinal);
        Assert.Contains($"repoint them to {Id("kept")}", named.Group.Reason, StringComparison.Ordinal);

        // Nothing is picked to remove, so no selection is made of the check.
        var selectionId = await SelectAsync(ledger, checkId);
        Assert.Empty(await SelectedAsync(ledger, selectionId));
    }

    [Fact]
    public async Task A_check_the_search_fails_keeps_no_group_is_made_afresh_when_run_again_and_says_which_property_no_record_holds()
    {
        AddEstate();
        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var checkId = await CheckAsync(ledger, inventory, ("data.WellboreID", "exact"), ("data.Name", "loose"), ("data.NotIndexed", "exact"));

        _search.Fail = body => body is not null && body.Contains("data.NotIndexed", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null;
        var failed = await Assert.ThrowsAsync<InventoryDuplicatesFailedException>(() => runner.FindDuplicatesAsync("WellLogs", checkId, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Equal(InventoryDuplicates.Failed, failed.Outcome.Status);
        var check = await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId);
        Assert.Equal((InventoryDuplicates.Failed, 0L, (string?)null), (check!.Status, check.Groups, check.Phase));
        Assert.False(string.IsNullOrWhiteSpace(check.Error));
        Assert.Empty(await GroupsAsync(ledger, checkId));
        await using (var db = _db.CreateDbContext())
        {
            Assert.False(await db.DeliveryInventoryDuplicateMembers.AnyAsync(m => m.InventoryDuplicateCheckId == checkId));
        }

        _search.Fail = null;
        var again = await runner.FindDuplicatesAsync("WellLogs", checkId, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal((InventoryDuplicates.Completed, 2L), (again.Status, again.Groups));
        Assert.Contains(again.Notes, n => n.StartsWith("No record the search returned held a value at data.NotIndexed", StringComparison.Ordinal));
        Assert.Null((await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId))!.Error);

        // A check of an inventory the flow does not declare, or one the partition does not hold, never starts.
        var orphaned = await CheckAsync(ledger, inventory, LogKey);
        var missing = await Assert.ThrowsAsync<DeliveryException>(() => runner.FindDuplicatesAsync("Wells", orphaned, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("no inventory named 'Wells'", missing.Message, StringComparison.Ordinal);
        Assert.Equal(InventoryDuplicates.Failed, (await ledger.GetInventoryDuplicateCheckAsync(Partition, orphaned))!.Status);
        var unknown = await Assert.ThrowsAsync<DeliveryException>(() => runner.FindDuplicatesAsync("WellLogs", 987654321, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("holds no duplicate check 987654321", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checks_past_their_week_are_retired_and_one_whose_picks_a_removal_took_is_kept_expired()
    {
        AddEstate();
        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var removedBy = await CheckAsync(ledger, inventory, LogKey);
        await runner.FindDuplicatesAsync("WellLogs", removedBy, Guid.NewGuid(), Operator, CancellationToken.None);
        var selectionId = await SelectAsync(ledger, removedBy);
        await runner.RemoveAsync(
            "WellLogs", new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 2, Selection = selectionId },
            Partition, Guid.NewGuid(), Operator, CancellationToken.None);
        var unused = await CheckAsync(ledger, inventory, ("data.Name", "exact"));
        await runner.FindDuplicatesAsync("WellLogs", unused, Guid.NewGuid(), Operator, CancellationToken.None);

        Assert.Equal(new InventoryDuplicateSweep(0, 0), await ledger.SweepInventoryDuplicateChecksAsync(Partition, Now - InventoryDuplicates.KeptFor));

        _clock.Advance(InventoryDuplicates.KeptFor + TimeSpan.FromHours(1));
        var recent = await CheckAsync(ledger, inventory, LogKey);
        var swept = await ledger.SweepInventoryDuplicateChecksAsync(Partition, Now - InventoryDuplicates.KeptFor);

        Assert.Equal(new InventoryDuplicateSweep(1, 1), swept);
        Assert.Null(await ledger.GetInventoryDuplicateCheckAsync(Partition, unused));
        var expired = await ledger.GetInventoryDuplicateCheckAsync(Partition, removedBy);
        Assert.Equal(InventoryDuplicates.Expired, expired?.Status);
        Assert.Empty(await GroupsAsync(ledger, removedBy));
        var again = await Assert.ThrowsAsync<DeliveryException>(() => runner.FindDuplicatesAsync("WellLogs", removedBy, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains($"holds no duplicate check {removedBy}", again.Message, StringComparison.Ordinal);
        Assert.Equal(InventoryDuplicates.Queued, (await ledger.GetInventoryDuplicateCheckAsync(Partition, recent))?.Status);
        await using var db = _db.CreateDbContext();
        Assert.False(await db.DeliveryInventoryDuplicateMembers.AnyAsync(m => m.InventoryDuplicateCheckId == unused || m.InventoryDuplicateCheckId == removedBy));
    }
}
