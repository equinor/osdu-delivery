using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Web;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An inventory flow's runs end to end (osdu/docs/reference/flow/inventory.md), over the module's database on SQL Server and
/// a platform that serves the search index's cursor read and storage's own listing, headers, versions and schema listing: a
/// build reads the kind whole, merges and reconciles; a read that fails part way changes nothing; storage's headers route
/// falls back where it is not deployed and a wildcard kind is expanded through the schema service; versions are read only for
/// what moved; the ids a ledger expects are read from storage within the flow's bound; owners are declared or inferred; a
/// reconcile compares the last build with the ledgers as they stand; a plan reads no id.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class InventoryRunTests : IDisposable
{
    private const string Partition = "dev";
    private const string Kind = "osdu:wks:work-product-component--WellLog:1.0.0";
    private const string Estate = "delivery-sp@contoso.com";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _search = new();
    private readonly StoragePlatform _platform;
    private readonly Guid _deliveryFlow = FlowId.Of("welllog-delivery");

    public InventoryRunTests()
    {
        _platform = new StoragePlatform(_search);
    }

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    private static string Id(string key) => $"{Partition}:work-product-component--WellLog:{key}";

    private static string Flow(string read = "search", string kind = Kind, string extra = "", string versions = "latest") => $$"""
        flowType: inventory
        name: welllog-inventory
        partitions: [dev]
        source:
          endpoint: http://localhost
          read: {{read}}
        {{extra}}
        inventories:
          - name: WellLogs
            kind: "{{kind}}"
            versions: {{versions}}
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { concurrency: 2 }
        """;

    private JsonObject Add(string key, long version = 3, string creator = Estate, string kind = Kind)
    {
        var record = _search.Add(Id(key), kind, new JsonObject { ["Name"] = key });
        record["version"] = version;
        record["createUser"] = creator;
        record["createTime"] = "2026-09-01T08:00:00.000Z";
        record["modifyUser"] = creator;
        record["modifyTime"] = "2026-09-02T08:00:00.000Z";
        return record;
    }

    private async Task<(InventoryRunner Runner, OsduLedger Ledger, InventoryFlowDefinition Flow)> RunnerAsync(string yaml, IReadOnlyDictionary<string, string>? values = null)
    {
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterAsync(_deliveryFlow);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseInventory(yaml, "flows/inventory.yaml").ForPartition(Partition);
        var runner = new InventoryRunner(engine, flow, values ?? new Dictionary<string, string>(), Samples.Logger<InventoryRunner>(), _platform, allowLoopback: true);
        return (runner, ledger, flow);
    }

    private async Task ClaimAsync(OsduLedger ledger, string key, string status, long? version)
    {
        var deliveryKey = DeliveryKey.Derive("inventory-runs", [key]);
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

    /// <summary>Every row of the inventory, paged as a report pages them.</summary>
    private static async Task<Dictionary<string, InventoryRecordState>> RecordsAsync(OsduLedger ledger, int inventoryId)
    {
        var all = new Dictionary<string, InventoryRecordState>(StringComparer.Ordinal);
        long? after = null;
        while (true)
        {
            var page = await ledger.ListInventoryRecordsAsync(Partition, inventoryId, null, after, 500);
            if (page.Count == 0)
            {
                return all;
            }

            foreach (var row in page)
            {
                all.Add(row.TargetId, row);
            }

            after = page[^1].InventoryRecordId;
        }
    }

    private static long Of(InventorySummary summary, string finding) => new InventoryCounts(summary.Findings).Of(finding);

    [Fact]
    public async Task A_search_build_reads_the_kind_whole_merges_and_reconciles_every_id_with_the_ledgers()
    {
        for (var i = 0; i < 1203; i++)
        {
            Add("log-" + i.ToString("D4", CultureInfo.InvariantCulture));
        }

        Add("stranger", creator: "someone@elsewhere.com");
        var (runner, ledger, _) = await RunnerAsync(Flow());
        await ClaimAsync(ledger, "log-0000", "delivered", 3);
        await ClaimAsync(ledger, "log-0001", "delivered", 2);

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var summary = Assert.Single(outcome.Inventories);
        Assert.Equal((InventoryRunStatus.Completed, 1204L, 1204L), (summary.Status, summary.Listed, summary.Added));
        Assert.True(summary.Pages >= 2, "a thousand ids a page");
        Assert.Equal(("inferred", Estate), (summary.OwnersSource, Assert.Single(summary.Owners).Identity));
        Assert.Equal((1L, 1L, 1L, 1201L), (Of(summary, InventoryFindings.Tracked), Of(summary, InventoryFindings.Drifted), Of(summary, InventoryFindings.Foreign), Of(summary, InventoryFindings.Orphan)));

        var inventory = Assert.Single(await ledger.ListInventoriesAsync(Partition));
        Assert.Equal((summary.InventoryRunId, "inferred"), (inventory.LastBuildRunId, inventory.OwnersSource));
        var records = await RecordsAsync(ledger, inventory.InventoryId);
        var tracked = records[Id("log-0000")];
        Assert.Equal((Kind, 3L, Estate, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc)), (tracked.Kind, tracked.Version!.Value, tracked.CreateUser, tracked.CreateTime!.Value));

        // The search was asked for the system properties an inventory keeps, and nothing more.
        var asked = JsonNode.Parse(_search.Calls.First(c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal)).Body!)!;
        Assert.Equal(SearchInventoryReader.Fields, asked["returnedFields"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Fact]
    public async Task A_read_that_fails_after_staging_a_chunk_merges_nothing_discards_its_stage_and_fails_the_run_carrying_its_outcome()
    {
        var count = InventoryRunner.StageChunk + 1500;
        for (var i = 0; i < count; i++)
        {
            Add("log-" + i.ToString(CultureInfo.InvariantCulture));
        }

        var (runner, ledger, _) = await RunnerAsync(Flow());
        var built = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);
        var inventory = Assert.Single(await ledger.ListInventoriesAsync(Partition));
        Assert.Equal(built.InventoryRunId, inventory.LastBuildRunId);

        // The next build's cursor pages fail once a whole chunk has been staged, the search's own second read from the first
        // page included, so the read is never whole; meanwhile a record goes, which a merge would mark gone.
        var later = 0;
        _search.Fail = body => body is not null && body.Contains("\"cursor\"", StringComparison.Ordinal) && Interlocked.Increment(ref later) > InventoryRunner.StageChunk / 1000
            ? HttpStatusCode.ServiceUnavailable
            : null;
        _search.Records.RemoveAt(0);
        _clock.Advance(TimeSpan.FromHours(1));

        var failed = await Assert.ThrowsAsync<InventoryRunsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));

        var summary = Assert.Single(failed.Outcome.Inventories);
        Assert.Equal((InventoryRunStatus.Failed, 0, 1), (summary.Status, failed.Outcome.Completed, failed.Outcome.Failed));
        Assert.False(string.IsNullOrWhiteSpace(summary.Error));
        var records = await RecordsAsync(ledger, inventory.InventoryId);
        Assert.Equal(count, records.Count);
        Assert.All(records.Values, r => Assert.Null(r.GoneUtc));
        var run = (await ledger.ListInventoryRunsAsync(Partition, inventory.InventoryId, 5))[0];
        Assert.Equal((summary.InventoryRunId!.Value, InventoryRunStatus.Failed), (run.InventoryRunId, run.Status));
        Assert.Equal(summary.Error, run.Error);
        Assert.Equal(built.InventoryRunId, (await ledger.ListInventoriesAsync(Partition))[0].LastBuildRunId);
        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.DeliveryInventoryScans.CountAsync());
    }

    [Fact]
    public async Task A_storage_build_lists_every_active_record_reads_their_headers_and_leaves_out_one_deleted_between_the_two()
    {
        for (var i = 0; i < 2100; i++)
        {
            Add("log-" + i.ToString(CultureInfo.InvariantCulture));
        }

        _platform.DeletedAfterListing.Add(Id("log-7"));
        var (runner, ledger, _) = await RunnerAsync(Flow(read: "storage"));

        var summary = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);

        Assert.Equal(2099L, summary.Listed);
        Assert.Equal(3, _platform.Listings);
        Assert.Equal(3, _platform.HeaderReads);
        var records = await RecordsAsync(ledger, (await ledger.ListInventoriesAsync(Partition))[0].InventoryId);
        Assert.DoesNotContain(Id("log-7"), records.Keys);
        Assert.Equal(Estate, records[Id("log-0")].ModifyUser);
        Assert.DoesNotContain(_search.Calls, c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Where_the_headers_route_is_not_deployed_records_are_read_a_hundred_at_a_time_and_a_wildcard_kind_is_expanded()
    {
        Add("a", kind: "osdu:wks:work-product-component--WellLog:1.0.0");
        Add("b", kind: "osdu:wks:work-product-component--WellLog:1.4.0");
        Add("other", kind: "osdu:wks:master-data--Wellbore:1.0.0");
        _platform.HeadersDeployed = false;
        var (runner, ledger, _) = await RunnerAsync(Flow(read: "storage", kind: "osdu:wks:work-product-component--WellLog:*"));

        var summary = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);

        Assert.Equal(2L, summary.Listed);
        Assert.Equal(["osdu:wks:work-product-component--WellLog:1.0.0", "osdu:wks:work-product-component--WellLog:1.4.0"], _platform.KindsListed.Order(StringComparer.Ordinal));
        Assert.True(_platform.RecordReads > 0, "records read through POST /query/records");
        Assert.Equal(1, _platform.HeaderReads);
        var records = await RecordsAsync(ledger, (await ledger.ListInventoriesAsync(Partition))[0].InventoryId);
        Assert.Equal(["osdu:wks:work-product-component--WellLog:1.0.0", "osdu:wks:work-product-component--WellLog:1.4.0"], records.Values.Select(r => r.Kind!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Every_version_is_read_for_what_is_new_and_a_rebuild_reads_only_what_moved()
    {
        var a = Add("a", version: 2);
        Add("b", version: 1);
        _platform.Versions[Id("a")] = [1, 2];
        _platform.Versions[Id("b")] = [1];
        var (runner, ledger, _) = await RunnerAsync(Flow(versions: "all"));

        var first = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);
        Assert.Equal((2L, 2), (first.VersionsRead, _platform.VersionReads));

        a["version"] = 3;
        _platform.Versions[Id("a")] = [1, 2, 3];
        var second = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);
        Assert.Equal((1L, 3), (second.VersionsRead, _platform.VersionReads));
        await using var db = _db.CreateDbContext();
        Assert.Equal(4, await db.DeliveryInventoryVersions.CountAsync());
    }

    [Fact]
    public async Task An_inventory_covering_its_type_reads_from_storage_what_a_ledger_expects_and_the_read_did_not_list()
    {
        Add("listed");
        var (runner, ledger, _) = await RunnerAsync(Flow(kind: "*:*:work-product-component--WellLog:*", extra: "owners: [delivery-sp@contoso.com]"));
        await ClaimAsync(ledger, "listed", "delivered", 3);
        await ClaimAsync(ledger, "gone-from-osdu", "delivered", 3);
        await ClaimAsync(ledger, "not-indexed", "delivered", 3);
        _platform.StorageOnly[Id("not-indexed")] = new JsonObject { ["id"] = Id("not-indexed"), ["kind"] = Kind, ["version"] = 3, ["createUser"] = Estate };

        var summary = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);

        Assert.Equal((1L, 1L, 1L, 2L), (Of(summary, InventoryFindings.Tracked), Of(summary, InventoryFindings.Missing), Of(summary, InventoryFindings.Unlisted), summary.MissingChecked));
        Assert.Equal(("declared", 1L), (summary.OwnersSource, Assert.Single(summary.Owners).Records));
        var records = await RecordsAsync(ledger, (await ledger.ListInventoriesAsync(Partition))[0].InventoryId);
        Assert.Null(records[Id("gone-from-osdu")].FirstSeenUtc);
        Assert.Equal(_deliveryFlow, records[Id("not-indexed")].LedgerFlowId);
    }

    [Fact]
    public async Task With_no_missing_checks_allowed_nothing_is_read_from_storage_and_an_id_no_longer_listed_is_gone()
    {
        var gone = Add("leaving");
        Add("staying");
        var (runner, ledger, _) = await RunnerAsync(Flow(extra: "maxMissingChecks: 0"));
        await ClaimAsync(ledger, "leaving", "delivered", 3);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        _search.Records.Remove(gone);

        var summary = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);

        Assert.Equal((0L, 1L, 0), (summary.MissingChecked, summary.Gone, _platform.HeaderReads));
        Assert.Equal(1L, Of(summary, InventoryFindings.Gone));
    }

    [Fact]
    public async Task A_reconcile_compares_the_last_build_with_the_ledgers_as_they_stand_and_refuses_an_inventory_never_built()
    {
        Add("a");
        var (runner, ledger, _) = await RunnerAsync(Flow());
        var never = await Assert.ThrowsAsync<InventoryRunsFailedException>(() => runner.ReconcileAsync([], Guid.NewGuid(), "tests", CancellationToken.None));
        Assert.Contains("never been built", Assert.Single(never.Outcome.Inventories).Error, StringComparison.Ordinal);

        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var searches = _search.Calls.Count;
        await ClaimAsync(ledger, "a", "deleted", 3);

        var reconciled = Assert.Single((await runner.ReconcileAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);

        Assert.Equal((0L, 1L), (reconciled.Listed, Of(reconciled, InventoryFindings.Stale)));
        Assert.Equal(searches, _search.Calls.Count);
        var inventory = (await ledger.ListInventoriesAsync(Partition))[0];
        Assert.Equal(reconciled.InventoryRunId, inventory.LastReconcileRunId);
        Assert.NotEqual(reconciled.InventoryRunId, inventory.LastBuildRunId);
    }

    [Fact]
    public async Task A_plan_counts_what_each_inventory_would_read_and_keeps_nothing()
    {
        Add("a");
        Add("b");
        var (runner, ledger, _) = await RunnerAsync(Flow());

        var plan = await runner.PlanAsync([], CancellationToken.None);

        var inventory = Assert.Single(plan.Inventories);
        Assert.Equal((2L, "search", "latest"), (inventory.Records!.Value, inventory.Read, inventory.Versions));
        Assert.Empty(inventory.Problems);
        Assert.Empty(await ledger.ListInventoriesAsync(Partition));
        var unknown = await Assert.ThrowsAsync<DeliveryException>(() => runner.PlanAsync(["Nope"], CancellationToken.None));
        Assert.Contains("has no inventory named 'Nope'", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_of_a_storage_read_names_the_kinds_a_wildcard_expands_to_as_a_build_reads_them()
    {
        Add("a", kind: "osdu:wks:work-product-component--WellLog:1.0.0");
        Add("b", kind: "osdu:wks:work-product-component--WellLog:1.4.0");
        Add("other", kind: "osdu:wks:master-data--Wellbore:1.0.0");
        var (runner, _, _) = await RunnerAsync(Flow(read: "storage", kind: "osdu:wks:work-product-component--WellLog:*"));

        var inventory = Assert.Single((await runner.PlanAsync([], CancellationToken.None)).Inventories);

        Assert.Equal("storage", inventory.Read);
        Assert.Equal(["osdu:wks:work-product-component--WellLog:1.0.0", "osdu:wks:work-product-component--WellLog:1.4.0"], inventory.Kinds);
        Assert.Equal(0, _platform.Listings);
    }

    [Theory]
    [InlineData("circling", "goes in circles")]
    [InlineData("lost", "no longer knows the cursor")]
    public async Task A_storage_listing_that_circles_or_loses_its_cursor_part_way_is_never_merged(string cursors, string expected)
    {
        for (var i = 0; i < 1500; i++)
        {
            Add("log-" + i.ToString(CultureInfo.InvariantCulture));
        }

        var (runner, ledger, _) = await RunnerAsync(Flow(read: "storage"));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var inventory = Assert.Single(await ledger.ListInventoriesAsync(Partition));
        _search.Records.RemoveAt(0);
        _platform.Cursors = cursors == "circling" ? ListingCursors.Circling : ListingCursors.Lost;

        var failed = await Assert.ThrowsAsync<InventoryRunsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));

        Assert.Contains(expected, Assert.Single(failed.Outcome.Inventories).Error, StringComparison.Ordinal);
        Assert.All((await RecordsAsync(ledger, inventory.InventoryId)).Values, r => Assert.Null(r.GoneUtc));
    }

    [Fact]
    public async Task A_headers_404_naming_the_ids_it_does_not_hold_is_an_answer_so_none_is_served_and_the_route_stays_in_use()
    {
        for (var i = 0; i < 1200; i++)
        {
            Add("log-" + i.ToString(CultureInfo.InvariantCulture));
        }

        _platform.HeadersHoldNone = true;
        var (runner, _, _) = await RunnerAsync(Flow(read: "storage"));

        var summary = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);

        Assert.Equal((0L, 2, 2, 0), (summary.Listed, _platform.Listings, _platform.HeaderReads, _platform.RecordReads));
    }

    [Fact]
    public async Task A_kind_storage_holds_nothing_of_lists_no_id_and_every_id_listed_before_is_gone()
    {
        Add("a");
        Add("b");
        var (runner, _, _) = await RunnerAsync(Flow(read: "storage"));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        _search.Records.Clear();

        var summary = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);

        Assert.Equal((InventoryRunStatus.Completed, 0L, 2L, 2L), (summary.Status, summary.Listed, summary.Gone, Of(summary, InventoryFindings.Gone)));
    }

    [Fact]
    public async Task A_record_gone_before_its_versions_are_read_keeps_none_and_is_not_read_again_until_it_moves()
    {
        Add("a", version: 2);
        Add("b", version: 5);
        _platform.Versions[Id("a")] = [1, 2];
        var (runner, _, _) = await RunnerAsync(Flow(versions: "all"));

        var first = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);
        var second = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);

        Assert.Equal((2L, 0L, 2), (first.VersionsRead, second.VersionsRead, _platform.VersionReads));
        await using var db = _db.CreateDbContext();
        Assert.Equal(2, await db.DeliveryInventoryVersions.CountAsync());
    }

    [Fact]
    public async Task A_query_is_sent_with_the_flows_parameters_and_the_partition_the_run_reads_filled_in()
    {
        var yaml = Flow(extra: "parameters:\n  country: { default: US }")
            .Replace("    versions: latest", "    versions: latest\n    query: 'data.Country:\"{country}\" AND data.Partition:\"{partition}\"'", StringComparison.Ordinal);
        var (runner, _, _) = await RunnerAsync(yaml, new Dictionary<string, string> { ["country"] = "US" });

        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var asked = JsonNode.Parse(_search.Calls.First(c => c.Uri.AbsolutePath.EndsWith("/query_with_cursor", StringComparison.Ordinal)).Body!)!;
        Assert.Equal("data.Country:\"US\" AND data.Partition:\"dev\"", asked["query"]!.GetValue<string>());
        Assert.Equal(Partition, _search.Calls[0].Headers["data-partition-id"]);
    }

    [Fact]
    public async Task A_run_whose_process_stopped_part_way_is_closed_failed_by_the_next_and_its_stage_discarded()
    {
        for (var i = 0; i < InventoryRunner.StageChunk + 1500; i++)
        {
            Add("log-" + i.ToString(CultureInfo.InvariantCulture));
        }

        var (runner, ledger, _) = await RunnerAsync(Flow());
        using var stop = new CancellationTokenSource();
        var later = 0;
        _search.Fail = body =>
        {
            if (body is not null && body.Contains("\"cursor\"", StringComparison.Ordinal) && Interlocked.Increment(ref later) > InventoryRunner.StageChunk / 1000)
            {
                stop.Cancel();
            }

            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", stop.Token));

        var inventory = Assert.Single(await ledger.ListInventoriesAsync(Partition));
        var stopped = Assert.Single(await ledger.ListInventoryRunsAsync(Partition, inventory.InventoryId, 5));
        Assert.Equal(InventoryRunStatus.Running, stopped.Status);
        await using (var db = _db.CreateDbContext())
        {
            Assert.True(await db.DeliveryInventoryScans.AnyAsync(), "a whole chunk was staged before the run stopped");
        }

        _search.Fail = null;
        var next = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Inventories);

        Assert.Equal(InventoryRunStatus.Completed, next.Status);
        var closed = (await ledger.ListInventoryRunsAsync(Partition, inventory.InventoryId, 5)).Single(r => r.InventoryRunId == stopped.InventoryRunId);
        Assert.Equal(InventoryRunStatus.Failed, closed.Status);
        Assert.Contains("ended without finishing", closed.Error, StringComparison.Ordinal);
        await using (var db = _db.CreateDbContext())
        {
            Assert.Equal(0, await db.DeliveryInventoryScans.CountAsync());
        }
    }

    [Fact]
    public async Task One_inventory_failing_leaves_every_other_built_and_a_payload_builds_only_the_inventories_it_names()
    {
        Add("a");
        _search.Add($"{Partition}:master-data--Well:w1", "osdu:wks:master-data--Well:1.0.0", new JsonObject());
        var yaml = Flow().Replace(
            "inventories:\n  - name: WellLogs\n",
            "inventories:\n  - name: Wells\n    kind: \"osdu:wks:master-data--Well:1.0.0\"\n  - name: WellLogs\n",
            StringComparison.Ordinal);
        var (runner, ledger, _) = await RunnerAsync(yaml);

        var only = await runner.BuildAsync(["welllogs"], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(["WellLogs"], only.Inventories.Select(i => i.Inventory));
        Assert.Equal(["WellLogs"], (await ledger.ListInventoriesAsync(Partition)).Select(i => i.Name));

        _search.Fail = body => body is not null && body.Contains("master-data--Well", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null;
        var failed = await Assert.ThrowsAsync<InventoryRunsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));

        Assert.Equal((1, 1), (failed.Outcome.Completed, failed.Outcome.Failed));
        Assert.Equal([("Wells", InventoryRunStatus.Failed), ("WellLogs", InventoryRunStatus.Completed)], failed.Outcome.Inventories.Select(i => (i.Inventory, i.Status)));
        var built = (await ledger.ListInventoriesAsync(Partition)).ToDictionary(i => i.Name, StringComparer.Ordinal);
        Assert.Null(built["Wells"].LastBuildRunId);
        Assert.Equal(failed.Outcome.Inventories[1].InventoryRunId, built["WellLogs"].LastBuildRunId);
    }
}
