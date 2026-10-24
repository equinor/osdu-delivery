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
/// module's database on SQL Server and a platform that serves search, storage's reads and its removals: a check downloads the
/// properties of its key for every record through the search into its own table, a request per page however many duplicates it
/// finds, and groups the table in SQL by any key of what it downloaded without asking OSDU again; a pick reads the copies of the
/// groups from storage, counts the records naming them a batch at a time, and keeps the most complete; a group the rules cannot
/// decide is left for review, and an operator's choice decides it; the copies picked to remove are made a selection, and a
/// removal by it takes them, each only while storage holds the copy its group keeps; a download that stops keeps no group and
/// no table, a pick that stops keeps its download; and checks past their week are retired with their tables.
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
        Add("thin", """{"Name":"GR RUN 1","WellboreID":"wb-1"}""");
        Add("thin-2", """{"Name":"GR RUN 1","WellboreID":"wb-1","Curves":[{"Mnemonic":"GR"}]}""");
        Add("foreign", """{"Name":"GR RUN 1","WellboreID":"wb-1"}""", creator: "someone@else.example");
        Add("other", """{"Name":"DT","WellboreID":"wb-1"}""");
        Add("unkeyed", """{"Description":"no key"}""");
        Add("pair-a", """{"Name":"SP","WellboreID":"wb-2","Curves":[{"Mnemonic":"SP"}]}""");
        Add("pair-b", """{"Name":"SP","WellboreID":"wb-2","Curves":[{"Mnemonic":"SP"}]}""");
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

    private static InventoryDuplicateKey KeyOf(params string[] key)
    {
        var (parsed, problem) = InventoryDuplicateKey.Of(key);
        Assert.Null(problem);
        return parsed!;
    }

    /// <summary>A check of the inventory as the control plane opens one: the key checked, in its one spelling and hashed, downloading its properties.</summary>
    private async Task<long> CheckAsync(OsduLedger ledger, InventoryState inventory, params string[] key)
    {
        var parsed = KeyOf(key);
        return await ledger.CreateInventoryDuplicateCheckAsync(
            Partition,
            new InventoryDuplicateCheckStart(inventory.InventoryId, Operator, parsed.ToJson(), parsed.Hash(), InventoryDuplicateTable.AttributesOf(parsed.Paths), inventory.LastBuildRunId, Now));
    }

    /// <summary>A key a check's table is grouped by again, as the control plane sets one.</summary>
    private InventoryDuplicateKeying Keying(string actor, params string[] key)
    {
        var parsed = KeyOf(key);
        return new InventoryDuplicateKeying(parsed.ToJson(), parsed.Hash(), parsed.Paths, actor, Now);
    }

    /// <summary>A pick as the control plane asks for one, run by the inventory flow.</summary>
    private static async Task<InventoryDuplicatesOutcome> PickAsync(InventoryRunner runner, OsduLedger ledger, InventoryState inventory, long checkId)
    {
        Assert.True(await ledger.RequestInventoryDuplicatePickAsync(Partition, checkId, inventory.LastBuildRunId));
        return await runner.FindDuplicatesAsync("WellLogs", checkId, Guid.NewGuid(), Operator, CancellationToken.None);
    }

    private static readonly string[] LogKey = ["data.WellboreID", "data.Name"];

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

    /// <summary>The rows of a check's table, and its columns; null when the database holds no such table.</summary>
    private async Task<(long Rows, IReadOnlyList<string> Columns)?> TableAsync(long checkId)
    {
        await using var db = _db.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        var table = InventoryDuplicateTable.NameOf(checkId);
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = $"SELECT CASE WHEN OBJECT_ID(N'osdu.{table}', N'U') IS NULL THEN 0 ELSE 1 END;";
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(), CultureInfo.InvariantCulture) == 0)
            {
                return null;
            }
        }

        long rows;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT_BIG(*) FROM [osdu].[{table}];";
            rows = Convert.ToInt64(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        var columns = new List<string>();
        await using (var listed = connection.CreateCommand())
        {
            listed.CommandText = $"SELECT c.[name] FROM sys.columns AS c WHERE c.[object_id] = OBJECT_ID(N'osdu.{table}') ORDER BY c.[column_id];";
            await using var reader = await listed.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }
        }

        return (rows, columns);
    }

    [Fact]
    public async Task A_check_downloads_its_key_once_groups_it_in_sql_by_any_key_of_what_it_downloaded_and_reads_copies_only_when_picked()
    {
        AddEstate();
        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var checkId = await CheckAsync(ledger, inventory, LogKey);
        var sent = _search.Calls.Count;
        var reads = _platform.RecordReads;

        var downloaded = await runner.FindDuplicatesAsync("WellLogs", checkId, Guid.NewGuid(), Operator, CancellationToken.None);

        // The download sends searches alone, a page at a time, with only the key's properties returned, and reads no copy.
        Assert.Equal((InventoryDuplicates.Completed, (string?)null), (downloaded.Status, downloaded.Error));
        Assert.Equal((8L, 7L, 1L, 0L), (downloaded.Candidates, downloaded.Keyed, downloaded.Unkeyed, downloaded.Outside));
        Assert.Equal((2L, 6L, 0L, 0L, 0L), (downloaded.Groups, downloaded.Copies, downloaded.Decided, downloaded.Review, downloaded.ToRemove));
        Assert.Equal(reads, _platform.RecordReads);
        Assert.Equal(_search.Calls.Count - sent, downloaded.Requests);
        var keyRead = JsonNode.Parse(_search.Calls.Skip(sent).First(c => c.Body!.Contains("returnedFields", StringComparison.Ordinal)).Body!)!;
        Assert.Equal(["id", "version", "data.WellboreID", "data.Name"], keyRead["returnedFields"]!.AsArray().Select(f => f!.GetValue<string>()));

        // The check's table holds a row for each id the search returned, a column for each property's values and one for their hash.
        var (rows, columns) = (await TableAsync(checkId))!.Value;
        Assert.Equal(8L, rows);
        Assert.Equal(
            ["inventory_record_id", "target_id", "version", "WellboreID", "WellboreID__hash", "Name", "Name__hash"],
            columns);
        var check = await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId);
        Assert.NotNull(check);
        Assert.Equal((InventoryDuplicates.Completed, (string?)null, InventoryDuplicateTable.NameOf(checkId), Operator, (DateTime?)null), (check.Status, check.Phase, check.TableName, check.KeyedBy, check.PickedUtc));
        Assert.Equal(["data.WellboreID", "data.Name"], InventoryDuplicateTable.Parse(check.AttributesJson).Select(a => a.Path));

        // Its groups wait to be picked, each copy with what it holds at the key; the larger first.
        var groups = await GroupsAsync(ledger, checkId);
        Assert.Equal([(4, InventoryDuplicates.Pending), (2, InventoryDuplicates.Pending)], groups.Select(g => (g.Group.Copies, g.Group.Outcome)));
        Assert.Equal(["GR RUN 1"], InventorySelectionValues.Parse(CopyOf(groups[0], "thin").ValuesJson)["data.Name"]);
        Assert.Equal(["wb-1"], InventorySelectionValues.Parse(CopyOf(groups[0], "thin").ValuesJson)["data.WellboreID"]);
        Assert.Equal(["GR RUN 1"], InventorySelectionValues.Parse(groups[0].Group.ValuesJson)["data.Name"]);
        Assert.All(groups.SelectMany(g => g.Members), m => Assert.Equal(((string?)null, (int?)null, (string?)null), (m.StorageState, m.Attributes, m.Role)));
        var sizes = await ledger.InventoryDuplicateSizesAsync(Partition, checkId);
        Assert.Equal([(2, (int?)2, 1L, 2L), (3, 10, 1L, 4L), (11, 100, 0L, 0L), (101, null, 0L, 0L)], sizes.Sizes.Select(s => (s.From, s.To, s.Groups, s.Copies)));
        Assert.Equal((2L, 6L), (sizes.PendingGroups, sizes.PendingCopies));

        // Another key of what it downloaded groups the table again in SQL, OSDU asked nothing; a property it did not download is refused.
        sent = _search.Calls.Count;
        _clock.Advance(TimeSpan.FromMinutes(1));
        var byWellbore = await ledger.GroupInventoryDuplicatesAsync(Partition, checkId, Keying("user:second", "data.WellboreID"));
        Assert.Equal((2L, 7L, 0L, 7L, 1L), (byWellbore.Groups, byWellbore.Copies, byWellbore.TooBroad, byWellbore.Keyed, byWellbore.Unkeyed));
        Assert.Equal([5, 2], (await GroupsAsync(ledger, checkId)).Select(g => g.Group.Copies));

        // A copy keeps the other property the check downloaded beside the key's, so a group shows how its copies differ outside it.
        var thinByWellbore = InventorySelectionValues.Parse(CopyOf((await GroupsAsync(ledger, checkId))[0], "thin").ValuesJson);
        Assert.Equal(["wb-1"], thinByWellbore["data.WellboreID"]);
        Assert.Equal(["GR RUN 1"], thinByWellbore["data.Name"]);
        var rekeyed = await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId);
        Assert.Equal(("user:second", (DateTime?)Now, KeyOf("data.WellboreID").ToJson()), (rekeyed!.KeyedBy, rekeyed.KeyedUtc, rekeyed.KeyJson));
        var undownloaded = await Assert.ThrowsAsync<DeliveryException>(() => ledger.GroupInventoryDuplicatesAsync(Partition, checkId, Keying(Operator, "data.Curves.Mnemonic")));
        Assert.Contains("did not download data.Curves.Mnemonic", undownloaded.Message, StringComparison.Ordinal);
        await ledger.GroupInventoryDuplicatesAsync(Partition, checkId, Keying(Operator, LogKey));
        Assert.Equal(sent, _search.Calls.Count);
        Assert.Equal(reads, _platform.RecordReads);

        // A pick reads the copies whole from storage, once each, and asks which records name them in one search for the batch.
        sent = _search.Calls.Count;
        var picked = await PickAsync(runner, ledger, inventory, checkId);
        Assert.Equal((InventoryDuplicates.Completed, (string?)null), (picked.Status, picked.Error));
        Assert.Equal((2L, 6L, 1L, 1L, 2L), (picked.Groups, picked.Copies, picked.Decided, picked.Review, picked.ToRemove));
        Assert.Equal(1, _platform.RecordReads - reads);
        var references = Assert.Single(_search.Calls.Skip(sent));
        Assert.Contains(InventoryDuplicateReferences.EveryKind, references.Body!, StringComparison.Ordinal);
        Assert.All(["full", "thin", "thin-2", "foreign", "pair-a", "pair-b"], key => Assert.Contains(Id(key), references.Body!, StringComparison.Ordinal));
        Assert.False(await ledger.RequestInventoryDuplicatePickAsync(Partition, checkId, inventory.LastBuildRunId));

        check = await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId);
        Assert.Equal((InventoryDuplicates.Completed, (string?)null, (DateTime?)Now, 2L), (check!.Status, check.Phase, check.PickedUtc, check.GroupsJudged));
        groups = await GroupsAsync(ledger, checkId);

        // The four copies of GR RUN 1: the one holding its bulk data and curves kept.
        var log = groups[0];
        Assert.Equal((4, InventoryDuplicates.Picked), (log.Group.Copies, log.Group.Outcome));
        Assert.Equal(CopyOf(log, "full").Record.InventoryRecordId, log.Group.KeptInventoryRecordId);
        Assert.Equal("it holds the most values: 6", log.Group.Reason);
        Assert.Equal(
            [(Id("foreign"), InventoryDuplicates.Leave, 2), (Id("full"), InventoryDuplicates.Keep, 6), (Id("thin"), InventoryDuplicates.Remove, 2), (Id("thin-2"), InventoryDuplicates.Remove, 3)],
            log.Members.Select(m => (m.Record.TargetId, m.Role!, m.Attributes!.Value)).OrderBy(m => m.TargetId, StringComparer.Ordinal));
        Assert.All(log.Members, m => Assert.Equal((InventoryDuplicates.Held, 3L, 3L, 0L), (m.StorageState!, m.StoredVersion!.Value, m.IndexedVersion!.Value, m.MentionedBy!.Value)));
        Assert.Contains("only its owner can remove it", CopyOf(log, "foreign").Reason, StringComparison.Ordinal);

        // The two copies of SP hold as many values: which to keep is the operator's to choose.
        var pair = groups[1];
        Assert.Equal((2, InventoryDuplicates.Review, (long?)null), (pair.Group.Copies, pair.Group.Outcome, pair.Group.KeptInventoryRecordId));
        Assert.StartsWith("2 copies hold the most values (3 each", pair.Group.Reason, StringComparison.Ordinal);

        // The newest check that answers for the build is this one; for another build, none.
        Assert.Equal(checkId, (await ledger.CurrentInventoryDuplicateCheckAsync(Partition, inventory.InventoryId, inventory.LastBuildRunId))?.InventoryDuplicateCheckId);
        Assert.Null(await ledger.CurrentInventoryDuplicateCheckAsync(Partition, inventory.InventoryId, inventory.LastBuildRunId + 1));

        // The copies picked to remove made a selection, which keeps the key they were picked by.
        var selectionId = await SelectAsync(ledger, checkId);
        var selection = await ledger.GetInventorySelectionAsync(Partition, selectionId);
        Assert.NotNull(selection);
        Assert.Equal((InventorySelections.Completed, checkId, inventory.LastBuildRunId, 2L, "[]"), (selection.Status, selection.DuplicateCheckId, selection.BuildRunId, selection.Matched, selection.ConditionsJson));
        Assert.Equal(check.KeyJson, selection.DuplicateKeyJson);
        Assert.Equal([Id("thin"), Id("thin-2")], (await SelectedAsync(ledger, selectionId)).Select(s => s.Record.TargetId).Order(StringComparer.Ordinal));
        Assert.Null(await ledger.FindInventorySelectionAsync(Partition, inventory.InventoryId, selection.ConditionsKey, inventory.LastBuildRunId));

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => runner.MatchAsync("WellLogs", selectionId, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains($"holds the copies duplicate check {checkId} picked to remove", refused.Message, StringComparison.Ordinal);

        var removal = await runner.RemoveAsync(
            "WellLogs", new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 2, Selection = selectionId },
            Partition, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal((2L, 0L, 0L, 0L), (removal.Removed, removal.Gone, removal.Skipped, removal.Failed));
        Assert.Equal([Id("thin"), Id("thin-2")], _platform.SoftDeleted.Order(StringComparer.Ordinal));

        // A key set since forms new groups to pick, and lets go of the selection; the selection still says what chose its ids.
        await ledger.GroupInventoryDuplicatesAsync(Partition, checkId, Keying(Operator, "data.WellboreID"));
        var regrouped = await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId);
        Assert.Equal(((long?)null, (DateTime?)null, 0L, 0L), (regrouped!.SelectionId, regrouped.PickedUtc, regrouped.Decided, regrouped.ToRemove));
        Assert.Equal(check.KeyJson, (await ledger.GetInventorySelectionAsync(Partition, selectionId))!.DuplicateKeyJson);
    }

    [Fact]
    public async Task An_operator_s_choice_decides_a_group_for_review_and_a_removal_leaves_every_copy_whose_kept_copy_storage_no_longer_holds()
    {
        AddEstate();
        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var checkId = await CheckAsync(ledger, inventory, LogKey);
        await runner.FindDuplicatesAsync("WellLogs", checkId, Guid.NewGuid(), Operator, CancellationToken.None);
        await PickAsync(runner, ledger, inventory, checkId);

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

        // A judgement made of groups a key set since re-formed is refused, so it never decides a group it was not made for: the
        // first group holds a fifth copy by the wellbore alone.
        var members = await ledger.ListInventoryDuplicateMembersAsync(Partition, checkId, 1, null, InventoryDuplicates.MaxCopiesPicked);
        var judgement = InventoryDuplicatePicker.Judge(
            1, members.Select(m => new DuplicateCopy(m.Record.InventoryRecordId, m.Record.TargetId, m.Record.Finding, m.StorageState!, m.StoredVersion, m.Attributes, m.MentionedBy)).ToList(),
            new HashSet<string>(StringComparer.Ordinal) { InventoryFindings.Orphan }, members.Single(m => m.Record.TargetId == Id("full")).Record.InventoryRecordId);
        await ledger.GroupInventoryDuplicatesAsync(Partition, checkId, Keying(Operator, "data.WellboreID"));
        var replaced = await Assert.ThrowsAsync<DeliveryException>(() => ledger.WriteInventoryDuplicateChoiceAsync(Partition, checkId, judgement, DuplicateChoice.Choose, Operator, Now));
        Assert.Contains("does not name every copy of its group", replaced.Message, StringComparison.Ordinal);

        // A build since the check makes its selection stale, so a removal of what it holds is refused, and no pick is taken.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var stale = await Assert.ThrowsAsync<DeliveryException>(() => runner.RemoveAsync(
            "WellLogs", new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 2, Selection = selectionId },
            Partition, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains($"holds the picks of duplicate check {checkId}, made against an earlier build", stale.Message, StringComparison.Ordinal);
        var rebuilt = Assert.Single(await ledger.ListInventoriesAsync(Partition));
        Assert.False(await ledger.RequestInventoryDuplicatePickAsync(Partition, checkId, rebuilt.LastBuildRunId));
    }

    [Fact]
    public async Task A_pick_asks_which_records_name_its_copies_a_batch_at_a_time_and_leaves_for_review_a_named_copy_a_ledger_s_thinner_copy_and_a_broad_key()
    {
        // The delivered copy holds less than an orphan of it: the delivery leaves out what the orphan holds.
        Add("delivered", """{"Name":"GR","WellboreID":"wb-1"}""");
        Add("richer", """{"Name":"GR","WellboreID":"wb-1","Curves":[{"Mnemonic":"GR"}]}""");

        // An orphan copy another record names: removing it would leave that record pointing at nothing.
        Add("kept", """{"Name":"DT","WellboreID":"wb-2","Curves":[{"Mnemonic":"DT"},{"Mnemonic":"DEPT"}]}""");
        Add("named", """{"Name":"DT","WellboreID":"wb-2"}""");
        Add("trajectory", $$"""{"LogID":"{{Id("named")}}:"}""", kind: "osdu:wks:work-product-component--WellboreTrajectory:1.0.0",
            id: $"{Partition}:work-product-component--WellboreTrajectory:t-1");

        // A copy its fuller sibling names: the sibling is read by the pick, so it is counted from its record.
        Add("lineage-a", $$"""{"Name":"SP","WellboreID":"wb-3","LineageAssertions":[{"ID":"{{Id("lineage-b")}}:"}]}""");
        Add("lineage-b", """{"Name":"SP","WellboreID":"wb-3"}""");

        // A key every one of more than a hundred records shares is no record written more than once.
        for (var i = 0; i < InventoryDuplicates.MaxCopiesPicked + 1; i++)
        {
            Add("broad-" + i.ToString("D3", CultureInfo.InvariantCulture), """{"Name":"UNKNOWN","WellboreID":"wb-9"}""");
        }

        var (runner, ledger, inventory) = await BuiltAsync(Flow(), claims: ledger => ClaimAsync(ledger, "delivered"));
        var checkId = await CheckAsync(ledger, inventory, LogKey);
        var downloaded = await runner.FindDuplicatesAsync("WellLogs", checkId, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal((4L, 1L), (downloaded.Groups, downloaded.Review));
        Assert.Contains(downloaded.Notes, n => n.StartsWith("1 group(s) hold more than 100 copies", StringComparison.Ordinal));
        Assert.Equal((3L, 6L), ((await ledger.InventoryDuplicateSizesAsync(Partition, checkId)).PendingGroups, (await ledger.InventoryDuplicateSizesAsync(Partition, checkId)).PendingCopies));

        // The six copies of the three groups a pick reads go in one batch; the batch finds the trajectory, so it is split until
        // the copy it names is counted on its own: seven searches, not one a copy.
        var sent = _search.Calls.Count;
        var picked = await PickAsync(runner, ledger, inventory, checkId);
        Assert.Equal(7, _search.Calls.Count - sent);
        Assert.Equal((4L, 0L, 4L, 0L), (picked.Groups, picked.Decided, picked.Review, picked.ToRemove));
        Assert.Contains(downloaded.Notes[0], picked.Notes);
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

        var lineage = groups.Single(g => g.Members.Any(m => m.Record.TargetId == Id("lineage-b")));
        Assert.Equal((InventoryDuplicates.Review, 1L, 0L), (lineage.Group.Outcome, CopyOf(lineage, "lineage-b").MentionedBy!.Value, CopyOf(lineage, "lineage-a").MentionedBy!.Value));

        // Nothing is picked to remove, so no selection is made of the check.
        var selectionId = await SelectAsync(ledger, checkId);
        Assert.Empty(await SelectedAsync(ledger, selectionId));
    }

    [Fact]
    public async Task A_download_that_stops_keeps_no_group_and_no_table_a_pick_that_stops_keeps_the_download_and_both_continue_when_asked_again()
    {
        AddEstate();
        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var checkId = await CheckAsync(ledger, inventory, "data.WellboreID", "data.Name", "data.NotIndexed");

        _search.Fail = body => body is not null && body.Contains("data.NotIndexed", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null;
        var failed = await Assert.ThrowsAsync<InventoryDuplicatesFailedException>(() => runner.FindDuplicatesAsync("WellLogs", checkId, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Equal(InventoryDuplicates.Failed, failed.Outcome.Status);
        var check = await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId);
        Assert.Equal((InventoryDuplicates.Failed, 0L, (string?)null, (string?)null), (check!.Status, check.Groups, check.Phase, check.TableName));
        Assert.False(string.IsNullOrWhiteSpace(check.Error));
        Assert.Null(await TableAsync(checkId));
        Assert.Empty(await GroupsAsync(ledger, checkId));

        _search.Fail = null;
        var again = await runner.FindDuplicatesAsync("WellLogs", checkId, Guid.NewGuid(), Operator, CancellationToken.None);
        Assert.Equal((InventoryDuplicates.Completed, 2L), (again.Status, again.Groups));
        Assert.Contains(again.Notes, n => n.StartsWith("No record the search returned held a value at data.NotIndexed", StringComparison.Ordinal));
        Assert.Null((await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId))!.Error);
        Assert.Equal(8L, (await TableAsync(checkId))!.Value.Rows);

        // A pick the search refuses stops: the check stays completed, its download and groups as they were, and says why.
        _search.Fail = body => body is not null && body.Contains(InventoryDuplicateReferences.EveryKind, StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null;
        var stopped = await Assert.ThrowsAsync<InventoryDuplicatesFailedException>(() => PickAsync(runner, ledger, inventory, checkId));
        Assert.Contains("pick again to read the groups left", stopped.Message, StringComparison.Ordinal);
        var paused = await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId);
        Assert.Equal((InventoryDuplicates.Completed, (string?)null, (DateTime?)null, 2L), (paused!.Status, paused.Phase, paused.PickedUtc, paused.Groups));
        Assert.Contains("pick again to read the groups left", paused.Error, StringComparison.Ordinal);
        Assert.All(await GroupsAsync(ledger, checkId), g => Assert.Equal(InventoryDuplicates.Pending, g.Group.Outcome));
        Assert.Equal(8L, (await TableAsync(checkId))!.Value.Rows);

        _search.Fail = null;
        var resumed = await PickAsync(runner, ledger, inventory, checkId);
        Assert.Equal((InventoryDuplicates.Completed, 1L, 1L), (resumed.Status, resumed.Decided, resumed.Review));
        Assert.Null((await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId))!.Error);

        // A pick whose run never came leaves the check completed with why; a download whose run never came fails it.
        Assert.True(await ledger.GroupInventoryDuplicatesAsync(Partition, checkId, Keying(Operator, LogKey)) is { Groups: 2 });
        Assert.True(await ledger.RequestInventoryDuplicatePickAsync(Partition, checkId, inventory.LastBuildRunId));
        await ledger.FailInventoryDuplicateCheckAsync(Partition, checkId, "its run could not be queued: the pool is gone", Now);
        var unqueued = await ledger.GetInventoryDuplicateCheckAsync(Partition, checkId);
        Assert.Equal((InventoryDuplicates.Completed, (string?)null), (unqueued!.Status, unqueued.Phase));
        Assert.NotNull(await TableAsync(checkId));

        // A check of an inventory the flow does not declare, or one the partition does not hold, never starts.
        var orphaned = await CheckAsync(ledger, inventory, LogKey);
        var missing = await Assert.ThrowsAsync<DeliveryException>(() => runner.FindDuplicatesAsync("Wells", orphaned, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("no inventory named 'Wells'", missing.Message, StringComparison.Ordinal);
        Assert.Equal(InventoryDuplicates.Failed, (await ledger.GetInventoryDuplicateCheckAsync(Partition, orphaned))!.Status);
        var unknown = await Assert.ThrowsAsync<DeliveryException>(() => runner.FindDuplicatesAsync("WellLogs", 987654321, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains("holds no duplicate check 987654321", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_newer_download_lets_go_of_earlier_tables_and_checks_past_their_week_are_retired_one_whose_picks_a_removal_took_kept_expired()
    {
        AddEstate();
        var (runner, ledger, inventory) = await BuiltAsync(Flow());
        var removedBy = await CheckAsync(ledger, inventory, LogKey);
        await runner.FindDuplicatesAsync("WellLogs", removedBy, Guid.NewGuid(), Operator, CancellationToken.None);
        await PickAsync(runner, ledger, inventory, removedBy);
        var selectionId = await SelectAsync(ledger, removedBy);
        await runner.RemoveAsync(
            "WellLogs", new InventoryRemovalRequest { Finding = InventoryFindings.Orphan, Scope = InventoryRemovals.SoftDelete, Expected = 2, Selection = selectionId },
            Partition, Guid.NewGuid(), Operator, CancellationToken.None);
        var unused = await CheckAsync(ledger, inventory, "data.Name");
        await runner.FindDuplicatesAsync("WellLogs", unused, Guid.NewGuid(), Operator, CancellationToken.None);

        // The newer download lets go of the table of the check before it, whose groups stay as they were found.
        Assert.Null(await TableAsync(removedBy));
        Assert.Null((await ledger.GetInventoryDuplicateCheckAsync(Partition, removedBy))!.TableName);
        Assert.NotEmpty(await GroupsAsync(ledger, removedBy));
        Assert.NotNull(await TableAsync(unused));

        Assert.Equal(new InventoryDuplicateSweep(0, 0), await ledger.SweepInventoryDuplicateChecksAsync(Partition, Now - InventoryDuplicates.KeptFor));

        _clock.Advance(InventoryDuplicates.KeptFor + TimeSpan.FromHours(1));
        var recent = await CheckAsync(ledger, inventory, LogKey);
        var swept = await ledger.SweepInventoryDuplicateChecksAsync(Partition, Now - InventoryDuplicates.KeptFor);

        Assert.Equal(new InventoryDuplicateSweep(1, 1), swept);
        Assert.Null(await ledger.GetInventoryDuplicateCheckAsync(Partition, unused));
        Assert.Null(await TableAsync(unused));
        var expired = await ledger.GetInventoryDuplicateCheckAsync(Partition, removedBy);
        Assert.Equal((InventoryDuplicates.Expired, (string?)null), (expired?.Status, expired?.TableName));
        Assert.Null(await TableAsync(removedBy));
        Assert.Empty(await GroupsAsync(ledger, removedBy));
        var again = await Assert.ThrowsAsync<DeliveryException>(() => runner.FindDuplicatesAsync("WellLogs", removedBy, Guid.NewGuid(), Operator, CancellationToken.None));
        Assert.Contains($"holds no duplicate check {removedBy}", again.Message, StringComparison.Ordinal);
        Assert.Equal(InventoryDuplicates.Queued, (await ledger.GetInventoryDuplicateCheckAsync(Partition, recent))?.Status);
        await using var db = _db.CreateDbContext();
        Assert.False(await db.DeliveryInventoryDuplicateMembers.AnyAsync(m => m.InventoryDuplicateCheckId == unused || m.InventoryDuplicateCheckId == removedBy));
    }
}
