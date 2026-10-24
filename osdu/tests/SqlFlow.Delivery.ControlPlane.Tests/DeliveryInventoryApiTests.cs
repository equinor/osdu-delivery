using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The inventories of inventory flows as the control plane serves them (osdu/docs/reference/concepts/api.md, Assertions,
/// dimensions and inventories): the inventories of a partition with what their last reconcile raised and their newest
/// run, an inventory flow's inventories (declared, kept and no longer declared), one inventory with its counts by finding,
/// owners and last runs, its ids of one finding a page at a time, its runs, a lookup of an OSDU id across the partition's
/// inventories, and the CSV export. The inventory is built and reconciled through the ledger as a run writes it, against a
/// delivery ledger of the same partition, so what these tests hold the API to is what a real build leaves; the tests never
/// reach an OSDU.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryInventoryApiTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:*";
    private const string LogFile = "osdu:wks:dataset--File.Generic:*";
    private const string Estate = "delivery-sp@contoso.com";
    private const string Stranger = "someone@elsewhere.com";

    [Fact]
    public async Task An_inventory_flow_s_inventories_counts_records_runs_lookups_and_exports_are_served_in_its_partition()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-inventory-" + suffix;
        var partition = "iv" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-inventories-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var otherPipelineId = CatalogIdentity.Pipeline(repoId, flowName + "-ingestion");
        var yaml = $$"""
            flowType: inventory
            name: {{flowName}}
            description: Every well log and log file the partition holds.
            partitions: [{{partition}}]
            owners: [{{Estate}}]
            source:
              endpoint: http://localhost
            inventories:
              - name: WellLogs
                description: Every well log, whatever its kind's version.
                kind: "{{WellLog}}"
              - name: LogFiles
                kind: "{{LogFile}}"
            """;
        var flow = new DeliveryDocumentLoader().ParseInventory(yaml, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var deliveryFlow = FlowId.Of("api-inventory-delivery-" + suffix);
        var deliveryName = "api-inventory-delivery-" + suffix;
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        string Id(string key) => $"{partition}:work-product-component--WellLog:{key}";

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-inventories-" + suffix, RemoteUrl = "https://example/cp-inventories.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, InventoryFlowDefinition.FlowTypeName, yaml, now));
            db.Pipelines.Add(Pipeline(otherPipelineId, repoId, flowName + "-ingestion", "ingestion", "flowType: ingestion", now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Inventory, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });
            await ledger.RegisterAsync(deliveryFlow, partition, deliveryName);

            // The delivery ledger delivered "tracked" at the version OSDU serves, and "drifted" at an older one than it serves.
            var trackedKey = await ClaimAsync(cs, ledger, deliveryFlow, Id("tracked"), "tracked", "delivered", 10);
            await ClaimAsync(cs, ledger, deliveryFlow, Id("drifted"), "drifted", "delivered", 10);

            // A build of WellLogs lists five ids: the two the ledger knows, two no ledger knows that the estate created, and one
            // another identity created. It reconciles with the owner the flow names and completes.
            var wellLogs = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "WellLogs", WellLog, null, "search", "latest");
            var build = await ledger.StartInventoryRunAsync(flow.LedgerId, wellLogs.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "inventory api tests", "search", now.AddHours(-2));
            await ledger.AppendInventoryScanAsync(flow.LedgerId, build,
            [
                Row(Id("tracked"), 10, Estate), Row(Id("drifted"), 12, Estate), Row(Id("ours-1"), 1, Estate), Row(Id("ours-2"), 1, Estate), Row(Id("theirs"), 1, Stranger),
            ]);
            var merge = await ledger.MergeInventoryAsync(flow.LedgerId, wellLogs.InventoryId, build, now.AddHours(-2));
            await ledger.ReconcileInventoryAsync(flow.LedgerId, wellLogs.InventoryId, [Estate], now.AddHours(-2));
            var counts = await ledger.InventoryCountsAsync(flow.LedgerId, wellLogs.InventoryId);
            var owners = JsonSerializer.Serialize(new { source = "declared", owners = new[] { new { identity = Estate, records = 2 } } });
            await ledger.CompleteInventoryRunAsync(
                flow.LedgerId, build,
                new InventoryRunState
                {
                    InventoryRunId = build, InventoryId = wellLogs.InventoryId, Operation = InventoryRunStatus.Build, Actor = "inventory api tests",
                    Status = InventoryRunStatus.Completed, StartedUtc = now.AddHours(-2), ReadMode = "search", Listed = merge.Listed, Added = merge.Added,
                    Pages = 1, Requests = 2, FindingsJson = JsonSerializer.Serialize(counts.ByFinding), OwnersJson = owners,
                },
                "declared", now.AddHours(-2).AddMinutes(1));

            // A newer build is under way: listings show it as the newest run, and the counts the last reconcile wrote beside it.
            var running = await ledger.StartInventoryRunAsync(flow.LedgerId, wellLogs.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "inventory api tests", "search", now.AddMinutes(-5));

            // LogFiles is registered and never built; Retired is kept from a declaration the flow no longer makes.
            var logFiles = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "LogFiles", LogFile, null, "search", "latest");
            var retired = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "Retired", "osdu:wks:master-data--Wellbore:*", null, "search", "all");

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var root = "/api/v1/delivery/inventories";
            var inventoryPath = $"{root}/{partition}/{wellLogs.InventoryId.ToString(CultureInfo.InvariantCulture)}";

            // The partition's inventories, by flow and name; what holds no value is left out.
            var list = await JsonAsync(client, token, $"{root}?partition={partition}");
            Assert.Equal(partition, list.GetProperty("partition").GetString());
            var listed = list.GetProperty("inventories").EnumerateArray().ToList();
            Assert.Equal(["LogFiles", "Retired", "WellLogs"], listed.Select(i => i.GetProperty("name").GetString()));
            var summary = listed[2];
            Assert.Equal((wellLogs.InventoryId, flow.LedgerId, flowName), (summary.GetProperty("inventoryId").GetInt32(), summary.GetProperty("ledgerId").GetGuid(), summary.GetProperty("flowName").GetString()));
            Assert.Equal(3L, summary.GetProperty("raised").GetInt64());
            var reconciled = summary.GetProperty("reconciled").EnumerateArray().ToDictionary(c => c.GetProperty("finding").GetString()!, c => c.GetProperty("count").GetInt64());
            Assert.Equal(InventoryFindings.All.Count, reconciled.Count);
            Assert.Equal((2L, 1L, 1L, 1L, 0L), (reconciled["orphan"], reconciled["drifted"], reconciled["tracked"], reconciled["foreign"], reconciled["missing"]));
            Assert.Equal((running, "running", "build"), (summary.GetProperty("latest").GetProperty("inventoryRunId").GetInt64(), summary.GetProperty("latest").GetProperty("status").GetString(), summary.GetProperty("latest").GetProperty("operation").GetString()));
            Assert.Equal(build, summary.GetProperty("lastReconcileRunId").GetInt64());
            var never = listed[0];
            Assert.False(never.TryGetProperty("reconciled", out _));
            Assert.False(never.TryGetProperty("latest", out _));
            Assert.False(never.TryGetProperty("lastBuiltUtc", out _));
            Assert.False(never.TryGetProperty("query", out _));

            // The workbench's partition stands for one the request does not name; a name that is no partition is refused.
            using (var byHeader = await SendAsync(client, token, root, partition))
            {
                var text = await byHeader.Content.ReadAsStringAsync();
                Assert.True(byHeader.StatusCode == HttpStatusCode.OK, text);
                Assert.Equal(3, JsonDocument.Parse(text).RootElement.GetProperty("inventories").GetArrayLength());
            }

            await ProblemAsync(client, token, $"{root}?partition=no%20such!", HttpStatusCode.BadRequest, "is not a partition");
            Assert.Empty((await JsonAsync(client, token, $"{root}?partition=none{suffix}")).GetProperty("inventories").EnumerateArray());

            // One inventory: its counts read from its rows, every finding with its zeros, its owners and its last runs.
            var detail = await JsonAsync(client, token, inventoryPath);
            Assert.Equal((pipelineId, repoId, true), (detail.GetProperty("pipelineId").GetGuid(), detail.GetProperty("repoId").GetGuid(), detail.GetProperty("declared").GetBoolean()));
            Assert.Equal("Every well log, whatever its kind's version.", detail.GetProperty("description").GetString());
            Assert.Equal((5L, 3L), (detail.GetProperty("ids").GetInt64(), detail.GetProperty("raised").GetInt64()));
            var detailCounts = detail.GetProperty("counts").EnumerateArray().ToList();
            Assert.Equal(InventoryFindings.All, detailCounts.Select(c => c.GetProperty("finding").GetString()));
            Assert.True(detailCounts.Single(c => c.GetProperty("finding").GetString() == "orphan").GetProperty("raised").GetBoolean());
            Assert.False(detailCounts.Single(c => c.GetProperty("finding").GetString() == "foreign").GetProperty("raised").GetBoolean());
            Assert.Equal("declared", detail.GetProperty("owners").GetProperty("source").GetString());
            var owner = Assert.Single(detail.GetProperty("owners").GetProperty("identities").EnumerateArray().ToList());
            Assert.Equal((Estate, 2L), (owner.GetProperty("identity").GetString(), owner.GetProperty("records").GetInt64()));
            var lastBuild = detail.GetProperty("lastBuild");
            Assert.Equal((build, "completed", 5L, 5L), (lastBuild.GetProperty("inventoryRunId").GetInt64(), lastBuild.GetProperty("status").GetString(), lastBuild.GetProperty("listed").GetInt64(), lastBuild.GetProperty("added").GetInt64()));
            Assert.Equal(["orphan", "drifted", "foreign", "tracked"], lastBuild.GetProperty("findings").EnumerateArray().Select(c => c.GetProperty("finding").GetString()));
            Assert.Equal(build, detail.GetProperty("lastReconcile").GetProperty("inventoryRunId").GetInt64());
            Assert.Equal(running, detail.GetProperty("inventory").GetProperty("latest").GetProperty("inventoryRunId").GetInt64());
            await ProblemAsync(client, token, $"{root}/{partition}/2147480000", HttpStatusCode.NotFound, "No inventory 2147480000");
            await ProblemAsync(client, token, $"{root}/none{suffix}/{wellLogs.InventoryId}", HttpStatusCode.NotFound, "No inventory");

            // Its orphans a page at a time: the first page names where the next starts, the last names none.
            var first = await JsonAsync(client, token, $"{inventoryPath}/records?finding=orphan&limit=1");
            var firstItem = Assert.Single(first.GetProperty("items").EnumerateArray().ToList());
            Assert.Equal("orphan", firstItem.GetProperty("finding").GetString());
            var next = first.GetProperty("next").GetInt64();
            Assert.Equal(firstItem.GetProperty("inventoryRecordId").GetInt64(), next);
            var second = await JsonAsync(client, token, $"{inventoryPath}/records?finding=orphan&limit=1&after={next}");
            var secondItem = Assert.Single(second.GetProperty("items").EnumerateArray().ToList());
            Assert.True(secondItem.GetProperty("inventoryRecordId").GetInt64() > next);
            Assert.Equal(
                [Id("ours-1"), Id("ours-2")],
                new[] { firstItem.GetProperty("targetId").GetString()!, secondItem.GetProperty("targetId").GetString()! }.Order(StringComparer.Ordinal));
            Assert.False(second.TryGetProperty("next", out _));
            var all = await JsonAsync(client, token, $"{inventoryPath}/records?limit=1000");
            Assert.Equal(5, all.GetProperty("items").GetArrayLength());
            Assert.False(all.TryGetProperty("next", out _));

            // A tracked id carries what the ledger holds of it: the ledger by identity and name, the record, its status and version.
            var tracked = Assert.Single((await JsonAsync(client, token, $"{inventoryPath}/records?finding=tracked")).GetProperty("items").EnumerateArray().ToList());
            Assert.Equal((deliveryFlow, deliveryName, trackedKey.Value), (tracked.GetProperty("ledgerFlowId").GetGuid(), tracked.GetProperty("ledger").GetString(), tracked.GetProperty("deliveryKey").GetGuid()));
            Assert.Equal(("delivered", 10L, 10L), (tracked.GetProperty("ledgerStatus").GetString(), tracked.GetProperty("ledgerVersion").GetInt64(), tracked.GetProperty("version").GetInt64()));
            var drifted = Assert.Single((await JsonAsync(client, token, $"{inventoryPath}/records?finding=drifted")).GetProperty("items").EnumerateArray().ToList());
            Assert.Equal("the ledger holds version 10, OSDU serves version 12", drifted.GetProperty("detail").GetString());
            var foreign = Assert.Single((await JsonAsync(client, token, $"{inventoryPath}/records?finding=foreign")).GetProperty("items").EnumerateArray().ToList());
            Assert.False(foreign.TryGetProperty("ledgerFlowId", out _));
            Assert.Equal(Stranger, foreign.GetProperty("createUser").GetString());

            await ProblemAsync(client, token, $"{inventoryPath}/records?finding=lost", HttpStatusCode.BadRequest, "is not a finding");
            await ProblemAsync(client, token, $"{inventoryPath}/records?limit=0", HttpStatusCode.BadRequest, "limit");
            await ProblemAsync(client, token, $"{inventoryPath}/records?limit={DeliveryInventoryEndpoints.MaxPage + 1}", HttpStatusCode.BadRequest, "limit");
            await ProblemAsync(client, token, $"{inventoryPath}/records?after=-1", HttpStatusCode.BadRequest, "after");
            await ProblemAsync(client, token, $"{root}/{partition}/2147480000/records", HttpStatusCode.NotFound, "No inventory");

            // Its runs, newest first: the build under way, then the one that completed with what it found and the owners it used.
            var runs = (await JsonAsync(client, token, $"{inventoryPath}/runs")).EnumerateArray().ToList();
            Assert.Equal([running, build], runs.Select(r => r.GetProperty("inventoryRunId").GetInt64()));
            Assert.False(runs[0].TryGetProperty("findings", out _));
            Assert.Equal((3L, 2), (runs[1].GetProperty("raised").GetInt64(), runs[1].GetProperty("requests").GetInt32()));
            Assert.Equal("declared", runs[1].GetProperty("owners").GetProperty("source").GetString());
            Assert.Single((await JsonAsync(client, token, $"{inventoryPath}/runs?limit=1")).EnumerateArray());
            await ProblemAsync(client, token, $"{inventoryPath}/runs?limit={DeliveryInventoryEndpoints.MaxRuns + 1}", HttpStatusCode.BadRequest, "limit");

            // A lookup by OSDU id across the partition's inventories, in the partition named or the workbench's.
            var lookup = await JsonAsync(client, token, $"{root}/lookup?partition={partition}&id={Uri.EscapeDataString(Id("tracked"))}");
            var hit = Assert.Single(lookup.GetProperty("hits").EnumerateArray().ToList());
            Assert.Equal(("WellLogs", "tracked"), (hit.GetProperty("inventory").GetProperty("name").GetString(), hit.GetProperty("record").GetProperty("finding").GetString()));
            Assert.Equal(deliveryName, hit.GetProperty("record").GetProperty("ledger").GetString());
            using (var byHeader = await SendAsync(client, token, $"{root}/lookup?id={Uri.EscapeDataString("  " + Id("theirs") + " ")}", partition))
            {
                var text = await byHeader.Content.ReadAsStringAsync();
                Assert.True(byHeader.StatusCode == HttpStatusCode.OK, text);
                var looked = JsonDocument.Parse(text).RootElement;
                Assert.Equal(Id("theirs"), looked.GetProperty("targetId").GetString());
                Assert.Equal("foreign", Assert.Single(looked.GetProperty("hits").EnumerateArray().ToList()).GetProperty("record").GetProperty("finding").GetString());
            }

            Assert.Empty((await JsonAsync(client, token, $"{root}/lookup?partition={partition}&id=nowhere")).GetProperty("hits").EnumerateArray());
            await ProblemAsync(client, token, $"{root}/lookup?id=anything", HttpStatusCode.BadRequest, "Name the partition");
            await ProblemAsync(client, token, $"{root}/lookup?partition={partition}", HttpStatusCode.BadRequest, "Name the OSDU id");

            // The export: a finding's ids as CSV under a header row, or every id, named after the flow, partition and inventory.
            using (var csv = await SendAsync(client, token, $"{inventoryPath}/export?finding=orphan"))
            {
                var body = await csv.Content.ReadAsStringAsync();
                Assert.True(csv.StatusCode == HttpStatusCode.OK, body);
                Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
                var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.StartsWith("inventory_record_id,id,kind,version,finding", lines[0], StringComparison.Ordinal);
                Assert.Equal(3, lines.Length);
                Assert.All(lines.Skip(1), line => Assert.Contains(",orphan,", line, StringComparison.Ordinal));
                var file = csv.Content.Headers.ContentDisposition?.FileNameStar ?? csv.Content.Headers.ContentDisposition?.FileName ?? string.Empty;
                Assert.Equal($"{flowName}-{partition}-WellLogs-orphan.csv", file.Trim('"'));
            }

            using (var csv = await SendAsync(client, token, $"{inventoryPath}/export"))
            {
                var lines = (await csv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(6, lines.Length);
                Assert.Contains(lines, line => line.Contains(deliveryName, StringComparison.Ordinal) && line.Contains(trackedKey.Value.ToString("D"), StringComparison.Ordinal));
            }

            await ProblemAsync(client, token, $"{inventoryPath}/export?finding=lost", HttpStatusCode.BadRequest, "is not a finding");
            await ProblemAsync(client, token, $"{root}/{partition}/2147480000/export", HttpStatusCode.NotFound, "No inventory");

            // The flow's inventories in its partition: those it declares, in its order, then the one it no longer declares.
            var board = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/inventories?partition={partition}");
            Assert.Equal((partition, true, flow.LedgerId), (board.GetProperty("partition").GetString(), board.GetProperty("readsPartition").GetBoolean(), board.GetProperty("ledgerId").GetGuid()));
            Assert.Equal(("search", Estate), (board.GetProperty("read").GetString(), Assert.Single(board.GetProperty("owners").EnumerateArray().ToList()).GetString()));
            Assert.False(board.TryGetProperty("problem", out _));
            var entries = board.GetProperty("inventories").EnumerateArray().ToList();
            Assert.Equal(["WellLogs", "LogFiles", "Retired"], entries.Select(e => e.GetProperty("name").GetString()));
            Assert.Equal([true, true, false], entries.Select(e => e.GetProperty("declared").GetBoolean()));
            Assert.Equal(wellLogs.InventoryId, entries[0].GetProperty("inventory").GetProperty("inventoryId").GetInt32());
            Assert.Equal(3L, entries[0].GetProperty("inventory").GetProperty("raised").GetInt64());
            Assert.Equal(logFiles.InventoryId, entries[1].GetProperty("inventory").GetProperty("inventoryId").GetInt32());
            Assert.Equal((retired.InventoryId, "all"), (entries[2].GetProperty("inventory").GetProperty("inventoryId").GetInt32(), entries[2].GetProperty("versions").GetString()));

            // In a partition the flow does not read, it says why, with what it declares; another kind and an unknown pipeline are refused.
            var elsewhere = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/inventories?partition=other{suffix}");
            Assert.False(elsewhere.GetProperty("readsPartition").GetBoolean());
            Assert.Contains("does not read partition", elsewhere.GetProperty("problem").GetString(), StringComparison.Ordinal);
            Assert.All(elsewhere.GetProperty("inventories").EnumerateArray(), e => Assert.False(e.TryGetProperty("inventory", out _)));
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{otherPipelineId}/inventories", HttpStatusCode.Conflict, "not an inventory flow");
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{Guid.NewGuid()}/inventories", HttpStatusCode.NotFound, "No pipeline");
        }
        finally
        {
            await CleanUpAsync(cs, repoId, partition, flow.LedgerId, deliveryFlow);
        }
    }

    [Fact]
    public async Task A_removal_is_queued_as_a_run_of_the_flow_only_when_the_flow_allows_it_the_partition_is_confirmed_and_the_count_holds()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-removal-" + suffix;
        var partition = "rm" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-removals-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var yaml = $$"""
            flowType: inventory
            name: {{flowName}}
            partitions: [{{partition}}]
            owners: [{{Estate}}]
            source:
              endpoint: http://localhost
            removal: { findings: [orphan] }
            inventories:
              - name: WellLogs
                kind: "{{WellLog}}"
            """;
        var flow = new DeliveryDocumentLoader().ParseInventory(yaml, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var deliveryFlow = FlowId.Of("api-removal-delivery-" + suffix);
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        string Id(string key) => $"{partition}:work-product-component--WellLog:{key}";

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-removals-" + suffix, RemoteUrl = "https://example/cp-removals.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, InventoryFlowDefinition.FlowTypeName, yaml, now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Inventory, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });
            await ledger.RegisterAsync(deliveryFlow, partition, "api-removal-delivery-" + suffix);

            // Two ids no ledger knows that the estate created (orphans), one another identity created (foreign), and one OSDU no
            // longer serves (gone).
            var wellLogs = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "WellLogs", WellLog, null, "search", "latest");
            var build = await ledger.StartInventoryRunAsync(flow.LedgerId, wellLogs.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "inventory api tests", "search", now.AddHours(-1));
            await ledger.AppendInventoryScanAsync(flow.LedgerId, build, [Row(Id("ours-1"), 1, Estate), Row(Id("ours-2"), 1, Estate), Row(Id("theirs"), 1, Stranger), Row(Id("vanished"), 1, Estate)]);
            var merge = await ledger.MergeInventoryAsync(flow.LedgerId, wellLogs.InventoryId, build, now.AddHours(-1));
            await ledger.ReconcileInventoryAsync(flow.LedgerId, wellLogs.InventoryId, [Estate], now.AddHours(-1));
            await using (var osdu = SampleEstate.Context(cs))
            {
                var vanished = Id("vanished");
                await osdu.DeliveryInventoryRecords
                    .Where(r => r.InventoryId == wellLogs.InventoryId && r.TargetId == vanished)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Finding, InventoryFindings.Gone).SetProperty(r => r.GoneUtc, now.AddMinutes(-30)));
            }

            var counts = await ledger.InventoryCountsAsync(flow.LedgerId, wellLogs.InventoryId);
            await ledger.CompleteInventoryRunAsync(
                flow.LedgerId, build,
                new InventoryRunState
                {
                    InventoryRunId = build, InventoryId = wellLogs.InventoryId, Operation = InventoryRunStatus.Build, Actor = "inventory api tests",
                    Status = InventoryRunStatus.Completed, StartedUtc = now.AddHours(-1), ReadMode = "search", Listed = merge.Listed, Added = merge.Added,
                    FindingsJson = JsonSerializer.Serialize(counts.ByFinding),
                    OwnersJson = JsonSerializer.Serialize(new { source = "declared", owners = new[] { new { identity = Estate, records = 0 } } }),
                },
                "declared", now.AddHours(-1).AddMinutes(1));

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var reader = await TokenAsync(client);
            var operate = await TokenAsync(client, ["read", "operate"]);
            var inventoryPath = $"/api/v1/delivery/inventories/{partition}/{wellLogs.InventoryId.ToString(CultureInfo.InvariantCulture)}";
            var removals = inventoryPath + "/removals";

            // The inventory says what its flow lets an operator remove, and where the removal goes.
            var removal = (await JsonAsync(client, reader, inventoryPath)).GetProperty("removal");
            Assert.Equal(["orphan"], removal.GetProperty("findings").EnumerateArray().Select(f => f.GetString()!));
            Assert.Equal((false, "http://localhost"), (removal.GetProperty("purge").GetBoolean(), removal.GetProperty("endpoint").GetString()));

            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 2 }, HttpStatusCode.BadRequest, "Confirmation required");
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 2, confirm = "prod" }, HttpStatusCode.BadRequest, "is kept in partition");
            await RefusedAsync(client, operate, removals, new { finding = "foreign", scope = "record", expected = 1, confirm = partition }, HttpStatusCode.BadRequest, "not a finding an inventory removes");
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 2, ids = new[] { Id("ours-1") }, confirm = partition }, HttpStatusCode.BadRequest, "as many as expected says");
            await RefusedAsync(client, operate, removals, new { finding = "stale", scope = "record", expected = 1, confirm = partition }, HttpStatusCode.Conflict, "allows removing orphan ids, not stale ones");
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "everything", expected = 2, confirm = partition }, HttpStatusCode.Conflict, "soft deletes only");
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 3, confirm = partition }, HttpStatusCode.Conflict, "holds 2 orphan id(s) now");
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 2, ids = new[] { Id("ours-1"), Id("theirs") }, confirm = partition }, HttpStatusCode.Conflict, "not a served orphan id");
            await RefusedAsync(client, operate, removals, new { finding = "gone", scope = "record", expected = 1, confirm = partition }, HttpStatusCode.BadRequest, "nothing of it in OSDU to remove");
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "inventory", expected = 2, confirm = partition }, HttpStatusCode.BadRequest, "the next build would list it again");
            await RefusedAsync(client, operate, removals, new { finding = "gone", scope = "inventory", expected = 1, ids = new[] { Id("ours-1") }, confirm = partition }, HttpStatusCode.Conflict, "not a gone id");

            // Every orphan, as many as the operator was shown, in the partition they typed: a run of the flow is queued.
            Guid runId;
            using (var accepted = await PostAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 2, confirm = partition.ToUpperInvariant() }))
            {
                var body = await accepted.Content.ReadAsStringAsync();
                Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, body);
                var answer = JsonDocument.Parse(body).RootElement;
                runId = answer.GetProperty("runId").GetGuid();
                Assert.Equal((partition, "WellLogs", "orphan", "record", 2L), (answer.GetProperty("partition").GetString(), answer.GetProperty("inventory").GetString(), answer.GetProperty("finding").GetString(), answer.GetProperty("scope").GetString(), answer.GetProperty("expected").GetInt64()));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.SingleAsync(r => r.RunId == runId);
                Assert.Equal((pipelineId, "remove", "queued"), (run.PipelineId, run.Operation, run.Status));
                var payload = global::SqlFlow.Delivery.Engine.DeliveryRunPayload.Parse(run.Payload);
                Assert.Equal(["WellLogs"], payload.Inventories);
                Assert.Equal((partition, "orphan", 2L, false), (payload.Confirm, payload.Removal!.Finding, payload.Removal.Expected, payload.Removal.NamesIds));
                Assert.Contains(partition, run.ValuesJson, StringComparison.Ordinal);
            }

            // While that run is queued, the flow is busy: a second removal waits.
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 2, confirm = partition }, HttpStatusCode.Conflict, "is queued or running");
            Assert.Empty((await JsonAsync(client, reader, removals)).EnumerateArray());
            Assert.Empty((await JsonAsync(client, reader, $"/api/v1/delivery/inventories/{partition}/removals/1/items")).GetProperty("items").EnumerateArray());
            await ProblemAsync(client, reader, $"/api/v1/delivery/inventories/{partition}/removals/2147480000", HttpStatusCode.NotFound, "No inventory removal");
            await ProblemAsync(client, reader, $"/api/v1/delivery/inventories/{partition}/removals/1/items?outcome=lost", HttpStatusCode.BadRequest, "not what a removal comes to");

            // Gone ids go from the inventory alone, so a flow whose removal names only orphans removes them all the same.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.RunId == runId).ExecuteDeleteAsync();
            }

            using (var accepted = await PostAsync(client, operate, removals, new { finding = "gone", scope = "inventory", expected = 1, confirm = partition }))
            {
                var body = await accepted.Content.ReadAsStringAsync();
                Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, body);
                runId = JsonDocument.Parse(body).RootElement.GetProperty("runId").GetGuid();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var payload = global::SqlFlow.Delivery.Engine.DeliveryRunPayload.Parse((await db.Runs.SingleAsync(r => r.RunId == runId)).Payload);
                Assert.Equal(("gone", "inventory", 1L), (payload.Removal!.Finding, payload.Removal.Scope, payload.Removal.Expected));
            }
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.PipelineId == pipelineId).ExecuteDeleteAsync();
            }

            await CleanUpAsync(cs, repoId, partition, flow.LedgerId, deliveryFlow);
        }
    }

    [Fact]
    public async Task A_selection_is_matched_by_a_run_of_the_flow_read_back_with_its_counts_and_ids_and_a_removal_takes_its_ids_while_it_holds()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-selection-" + suffix;
        var partition = "sl" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-selections-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var yaml = $$"""
            flowType: inventory
            name: {{flowName}}
            partitions: [{{partition}}]
            owners: [{{Estate}}]
            source:
              endpoint: http://localhost
            removal: { findings: [orphan] }
            inventories:
              - name: WellLogs
                kind: "{{WellLog}}"
            """;
        var flow = new DeliveryDocumentLoader().ParseInventory(yaml, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var deliveryFlow = FlowId.Of("api-selection-delivery-" + suffix);
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        string Id(string key) => $"{partition}:work-product-component--WellLog:{key}";

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-selections-" + suffix, RemoteUrl = "https://example/cp-selections.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, InventoryFlowDefinition.FlowTypeName, yaml, now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Inventory, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });
            await ledger.RegisterAsync(deliveryFlow, partition, "api-selection-delivery-" + suffix);

            // Three ids no ledger knows that the estate created (orphans), and one another identity created (foreign).
            var wellLogs = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "WellLogs", WellLog, null, "search", "latest");
            async Task<long> BuildAsync(DateTime at)
            {
                var run = await ledger.StartInventoryRunAsync(flow.LedgerId, wellLogs.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "inventory api tests", "search", at);
                await ledger.AppendInventoryScanAsync(flow.LedgerId, run, [Row(Id("ours-1"), 1, Estate), Row(Id("ours-2"), 1, Estate), Row(Id("ours-3"), 1, Estate), Row(Id("theirs"), 1, Stranger)]);
                var merged = await ledger.MergeInventoryAsync(flow.LedgerId, wellLogs.InventoryId, run, at);
                await ledger.ReconcileInventoryAsync(flow.LedgerId, wellLogs.InventoryId, [Estate], at);
                var found = await ledger.InventoryCountsAsync(flow.LedgerId, wellLogs.InventoryId);
                await ledger.CompleteInventoryRunAsync(
                    flow.LedgerId, run,
                    new InventoryRunState
                    {
                        InventoryRunId = run, InventoryId = wellLogs.InventoryId, Operation = InventoryRunStatus.Build, Actor = "inventory api tests",
                        Status = InventoryRunStatus.Completed, StartedUtc = at, ReadMode = "search", Listed = merged.Listed, Added = merged.Added,
                        FindingsJson = JsonSerializer.Serialize(found.ByFinding),
                        OwnersJson = JsonSerializer.Serialize(new { source = "declared", owners = new[] { new { identity = Estate, records = 0 } } }),
                    },
                    "declared", at.AddMinutes(1));
                return run;
            }

            var firstBuild = await BuildAsync(now.AddHours(-1));

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var reader = await TokenAsync(client);
            var operate = await TokenAsync(client, ["read", "operate"]);
            var inventoryPath = $"/api/v1/delivery/inventories/{partition}/{wellLogs.InventoryId.ToString(CultureInfo.InvariantCulture)}";
            var selections = inventoryPath + "/selections";
            var facility = new { path = "data.FacilityName", index = "text", value = "Wellbore A-1" };

            await RefusedAsync(client, operate, selections, new { filters = Array.Empty<object>() }, HttpStatusCode.BadRequest, "Conditions required");
            await RefusedAsync(client, operate, selections, new { filters = new[] { new { path = "data.FacilityName", index = "text", value = "x", condition = "near" } } }, HttpStatusCode.BadRequest, "'near' is not a condition");
            await RefusedAsync(client, operate, selections, new { filters = new[] { new { path = "data.FacilityName", index = "text", condition = "is" } } }, HttpStatusCode.BadRequest, "Invalid condition");

            // A match is a run of the flow, queued as the caller, its selection opened with the conditions as written.
            long selectionId;
            Guid matchRun;
            using (var accepted = await PostAsync(client, operate, selections, new { filters = new[] { facility } }))
            {
                var body = await accepted.Content.ReadAsStringAsync();
                Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, body);
                var answer = JsonDocument.Parse(body).RootElement;
                selectionId = answer.GetProperty("selectionId").GetInt64();
                matchRun = answer.GetProperty("runId").GetGuid();
                Assert.Equal((InventorySelections.Queued, false), (answer.GetProperty("status").GetString(), answer.GetProperty("stale").GetBoolean()));
                var condition = Assert.Single(answer.GetProperty("conditions").EnumerateArray());
                Assert.Equal(("data.FacilityName", "text", "is", "Wellbore A-1"), (condition.GetProperty("path").GetString(), condition.GetProperty("index").GetString(), condition.GetProperty("condition").GetString(), condition.GetProperty("value").GetString()));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.SingleAsync(r => r.RunId == matchRun);
                Assert.Equal((pipelineId, global::SqlFlow.Delivery.Engine.DeliveryOperations.Match, "queued"), (run.PipelineId, run.Operation, run.Status));
                var payload = global::SqlFlow.Delivery.Engine.DeliveryRunPayload.Parse(run.Payload);
                Assert.Equal(["WellLogs"], payload.Inventories);
                Assert.Equal(selectionId, payload.SelectionId);
                Assert.Contains(partition, run.ValuesJson, StringComparison.Ordinal);
            }

            // The same conditions, however they are spelled, find the match made of them; refresh reads OSDU again.
            using (var again = await PostAsync(client, operate, selections, new { filters = new[] { new { path = " data.FacilityName ", index = "TEXT", value = "Wellbore A-1", condition = "IS" } } }))
            {
                var body = await again.Content.ReadAsStringAsync();
                Assert.True(again.StatusCode == HttpStatusCode.OK, body);
                Assert.Equal(selectionId, JsonDocument.Parse(body).RootElement.GetProperty("selectionId").GetInt64());
            }

            long refreshed;
            Guid refreshRun;
            using (var fresh = await PostAsync(client, operate, selections, new { filters = new[] { facility }, refresh = true }))
            {
                var body = await fresh.Content.ReadAsStringAsync();
                Assert.True(fresh.StatusCode == HttpStatusCode.Accepted, body);
                var answer = JsonDocument.Parse(body).RootElement;
                refreshed = answer.GetProperty("selectionId").GetInt64();
                refreshRun = answer.GetProperty("runId").GetGuid();
                Assert.NotEqual(selectionId, refreshed);
            }

            // The match run fills the selection: two ids of the inventory hold the condition, and one record of the kind it holds no row of.
            Assert.NotNull(await ledger.StartInventorySelectionAsync(flow.LedgerId, wellLogs.InventoryId, selectionId, matchRun, firstBuild, now));
            await ledger.PlanInventorySelectionAsync(flow.LedgerId, selectionId, 4, 3, InventorySelections.ByConditions, "data.FacilityName.keyword:\"Wellbore A-1\"", 1);
            var write = await ledger.AddInventorySelectionItemsAsync(flow.LedgerId, wellLogs.InventoryId, selectionId,
            [
                new InventorySelectionHit(Id("ours-1"), 2, """{"data.FacilityName":["Wellbore A-1"]}"""),
                new InventorySelectionHit(Id("theirs"), 1, """{"data.FacilityName":["Wellbore A-1"]}"""),
                new InventorySelectionHit(Id("since"), 1, """{"data.FacilityName":["Wellbore A-1"]}"""),
            ], 3, 2);
            Assert.Equal(new InventorySelectionWrite(2, 1, 1), write);
            await ledger.CompleteInventorySelectionAsync(
                flow.LedgerId, selectionId, new InventorySelectionEnd(InventorySelections.Completed, null, null, [], ["data.FacilityName"], null), now);

            var selection = await JsonAsync(client, reader, $"{selections}/{selectionId}");
            Assert.Equal((InventorySelections.Completed, InventorySelections.ByConditions, 2L, 1L, 1L, 3L), (
                selection.GetProperty("status").GetString(), selection.GetProperty("direction").GetString(), selection.GetProperty("matched").GetInt64(),
                selection.GetProperty("outside").GetInt64(), selection.GetProperty("changed").GetInt64(), selection.GetProperty("requests").GetInt64()));
            Assert.Equal(["data.FacilityName"], selection.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!));
            var counted = selection.GetProperty("counts").EnumerateArray().ToDictionary(c => c.GetProperty("finding").GetString()!, c => c.GetProperty("count").GetInt64());
            Assert.Equal((1L, 1L, 0L), (counted["orphan"], counted["foreign"], counted["tracked"]));

            // The selection's ids of one finding, each with what the match read of it; and the CSV of every one of them.
            var page = await JsonAsync(client, reader, $"{inventoryPath}/records?selection={selectionId}&finding=orphan");
            var item = Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal((Id("ours-1"), 2L, 1L), (item.GetProperty("targetId").GetString(), item.GetProperty("matchedVersion").GetInt64(), item.GetProperty("version").GetInt64()));
            Assert.Equal(["Wellbore A-1"], item.GetProperty("values").GetProperty("data.FacilityName").EnumerateArray().Select(v => v.GetString()!));
            using (var csv = await SendAsync(client, reader, $"{inventoryPath}/export?selection={selectionId}"))
            {
                var text = await csv.Content.ReadAsStringAsync();
                Assert.True(csv.StatusCode == HttpStatusCode.OK, text);
                var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.EndsWith(",matched_version,data.FacilityName", lines[0], StringComparison.Ordinal);
                Assert.Equal(3, lines.Length);
                Assert.Contains($"selection-{selectionId.ToString(CultureInfo.InvariantCulture)}-all.csv", csv.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
            }

            await ProblemAsync(client, reader, $"{selections}/2147480000", HttpStatusCode.NotFound, "No selection 2147480000");
            await ProblemAsync(client, reader, $"{inventoryPath}/records?selection=2147480000", HttpStatusCode.NotFound, "No selection 2147480000");

            // A removal takes the selection's ids of the finding, as many as the operator was shown, once the flow is idle.
            var removals = inventoryPath + "/removals";
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.RunId == matchRun).ExecuteDeleteAsync();
            }

            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 1, selection = selectionId, confirm = partition }, HttpStatusCode.Conflict, "is queued or running");
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.RunId == refreshRun).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Cancelled));
            }

            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 3, selection = selectionId, confirm = partition }, HttpStatusCode.Conflict, $"Selection {selectionId} of inventory 'WellLogs' holds 1 orphan id(s) now");
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 1, ids = new[] { Id("ours-1") }, selection = selectionId, confirm = partition }, HttpStatusCode.BadRequest, "not both");
            await RefusedAsync(client, operate, removals, new { finding = "gone", scope = "inventory", expected = 1, selection = selectionId, confirm = partition }, HttpStatusCode.BadRequest, "Not from a selection");
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 1, selection = 2147480000L, confirm = partition }, HttpStatusCode.NotFound, "No selection 2147480000");
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 1, selection = refreshed, confirm = partition }, HttpStatusCode.Conflict, "only a match that read OSDU whole");

            Guid removalRun;
            using (var accepted = await PostAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 1, selection = selectionId, confirm = partition }))
            {
                var body = await accepted.Content.ReadAsStringAsync();
                Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, body);
                var answer = JsonDocument.Parse(body).RootElement;
                removalRun = answer.GetProperty("runId").GetGuid();
                Assert.Equal(selectionId, answer.GetProperty("selection").GetInt64());
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var payload = global::SqlFlow.Delivery.Engine.DeliveryRunPayload.Parse((await db.Runs.SingleAsync(r => r.RunId == removalRun)).Payload);
                Assert.Equal(("orphan", 1L, selectionId, false), (payload.Removal!.Finding, payload.Removal.Expected, payload.Removal.Selection!.Value, payload.Removal.NamesIds));
                await db.Runs.Where(r => r.RunId == removalRun).ExecuteDeleteAsync();
            }

            // A removal that took the selection's ids names it, with the conditions that made it.
            await ledger.StartInventoryRemovalAsync(
                flow.LedgerId, new InventoryRemovalStart(wellLogs.InventoryId, removalRun, "inventory api tests", "orphan", "record", false, 1, null, now, selectionId));
            var listed = Assert.Single((await JsonAsync(client, reader, removals)).EnumerateArray());
            var named = listed.GetProperty("selection");
            Assert.Equal((selectionId, "data.FacilityName", 2L), (named.GetProperty("selectionId").GetInt64(), named.GetProperty("conditions")[0].GetProperty("path").GetString(), named.GetProperty("matched").GetInt64()));

            // The cancelled run's selection is closed as failed when it is read, saying how its run ended.
            var cancelled = await JsonAsync(client, reader, $"{selections}/{refreshed}");
            Assert.Equal(InventorySelections.Failed, cancelled.GetProperty("status").GetString());
            Assert.Contains("ended cancelled before the match did", cancelled.GetProperty("error").GetString(), StringComparison.Ordinal);

            // A build since the match leaves the selection stale: it says so, and a removal of what it holds waits for a new match.
            await BuildAsync(now.AddMinutes(-10));
            Assert.True((await JsonAsync(client, reader, $"{selections}/{selectionId}")).GetProperty("stale").GetBoolean());
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 1, selection = selectionId, confirm = partition }, HttpStatusCode.Conflict, "was built again since selection");

            // The same conditions asked of the new build are a new match.
            using (var rebuilt = await PostAsync(client, operate, selections, new { filters = new[] { facility } }))
            {
                var body = await rebuilt.Content.ReadAsStringAsync();
                Assert.True(rebuilt.StatusCode == HttpStatusCode.Accepted, body);
                Assert.NotEqual(selectionId, JsonDocument.Parse(body).RootElement.GetProperty("selectionId").GetInt64());
            }
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.PipelineId == pipelineId).ExecuteDeleteAsync();
            }

            await CleanUpAsync(cs, repoId, partition, flow.LedgerId, deliveryFlow);
        }
    }

    [Fact]
    public async Task A_duplicate_check_is_run_by_the_flow_read_back_with_its_groups_and_an_operator_s_choice_and_its_selection_are_written_as_the_caller()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-duplicates-" + suffix;
        var partition = "du" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-duplicates-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var yaml = $$"""
            flowType: inventory
            name: {{flowName}}
            partitions: [{{partition}}]
            owners: [{{Estate}}]
            source:
              endpoint: http://localhost
            removal: { findings: [orphan] }
            inventories:
              - name: WellLogs
                kind: "{{WellLog}}"
            """;
        var flow = new DeliveryDocumentLoader().ParseInventory(yaml, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var deliveryFlow = FlowId.Of("api-duplicates-delivery-" + suffix);
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        string Id(string key) => $"{partition}:work-product-component--WellLog:{key}";
        static byte[] Key(string text) => System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-duplicates-" + suffix, RemoteUrl = "https://example/cp-duplicates.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, InventoryFlowDefinition.FlowTypeName, yaml, now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Inventory, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });
            await ledger.RegisterAsync(deliveryFlow, partition, "api-duplicates-delivery-" + suffix);

            // Three ids no ledger knows that the estate created (orphans), and one another identity created (foreign).
            var wellLogs = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "WellLogs", WellLog, null, "search", "latest");
            async Task<long> BuildAsync(DateTime at)
            {
                var run = await ledger.StartInventoryRunAsync(flow.LedgerId, wellLogs.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "inventory api tests", "search", at);
                await ledger.AppendInventoryScanAsync(flow.LedgerId, run, [Row(Id("ours-1"), 1, Estate), Row(Id("ours-2"), 1, Estate), Row(Id("ours-3"), 1, Estate), Row(Id("theirs"), 1, Stranger)]);
                var merged = await ledger.MergeInventoryAsync(flow.LedgerId, wellLogs.InventoryId, run, at);
                await ledger.ReconcileInventoryAsync(flow.LedgerId, wellLogs.InventoryId, [Estate], at);
                var found = await ledger.InventoryCountsAsync(flow.LedgerId, wellLogs.InventoryId);
                await ledger.CompleteInventoryRunAsync(
                    flow.LedgerId, run,
                    new InventoryRunState
                    {
                        InventoryRunId = run, InventoryId = wellLogs.InventoryId, Operation = InventoryRunStatus.Build, Actor = "inventory api tests",
                        Status = InventoryRunStatus.Completed, StartedUtc = at, ReadMode = "search", Listed = merged.Listed, Added = merged.Added,
                        FindingsJson = JsonSerializer.Serialize(found.ByFinding),
                        OwnersJson = JsonSerializer.Serialize(new { source = "declared", owners = new[] { new { identity = Estate, records = 0 } } }),
                    },
                    "declared", at.AddMinutes(1));
                return run;
            }

            var firstBuild = await BuildAsync(now.AddHours(-1));

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var reader = await TokenAsync(client);
            var operate = await TokenAsync(client, ["read", "operate"]);
            var inventoryPath = $"/api/v1/delivery/inventories/{partition}/{wellLogs.InventoryId.ToString(CultureInfo.InvariantCulture)}";
            var duplicates = inventoryPath + "/duplicates";
            var key = new[] { new { path = "data.WellboreID" }, new { path = "data.Name" } };

            await RefusedAsync(client, operate, duplicates, new { key = Array.Empty<object>() }, HttpStatusCode.BadRequest, "Name at least one property");
            await RefusedAsync(client, operate, duplicates, new { key = new[] { new { path = "id" } } }, HttpStatusCode.BadRequest, "no two records share it");
            await RefusedAsync(client, operate, "/api/v1/delivery/inventories/" + partition + "/2147480000/duplicates", new { key }, HttpStatusCode.NotFound, "No inventory 2147480000");

            // A check is a run of the flow, queued as the caller, opened with the key in its one spelling and downloading its properties.
            long checkId;
            Guid checkRun;
            using (var accepted = await PostAsync(client, operate, duplicates, new { key }))
            {
                var body = await accepted.Content.ReadAsStringAsync();
                Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, body);
                var answer = JsonDocument.Parse(body).RootElement;
                checkId = answer.GetProperty("checkId").GetInt64();
                checkRun = answer.GetProperty("runId").GetGuid();
                Assert.Equal((InventoryDuplicates.Queued, false), (answer.GetProperty("status").GetString(), answer.GetProperty("stale").GetBoolean()));
                Assert.Equal(["data.WellboreID", "data.Name"], answer.GetProperty("key").EnumerateArray().Select(k => k.GetProperty("path").GetString()!));
                Assert.Equal(["data.WellboreID", "data.Name"], answer.GetProperty("attributes").EnumerateArray().Select(a => a.GetString()!));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.SingleAsync(r => r.RunId == checkRun);
                Assert.Equal((pipelineId, global::SqlFlow.Delivery.Engine.DeliveryOperations.Duplicates, "queued"), (run.PipelineId, run.Operation, run.Status));
                var payload = global::SqlFlow.Delivery.Engine.DeliveryRunPayload.Parse(run.Payload);
                Assert.Equal(["WellLogs"], payload.Inventories);
                Assert.Equal(checkId, payload.DuplicateCheckId);
                Assert.Contains(partition, run.ValuesJson, StringComparison.Ordinal);
            }

            // The same key, however it is spelled, finds the check made of it; another key of its properties waits for its download.
            using (var again = await PostAsync(client, operate, duplicates, new { key = new[] { new { path = " data.WellboreID " }, new { path = "data.Name" } } }))
            {
                var body = await again.Content.ReadAsStringAsync();
                Assert.True(again.StatusCode == HttpStatusCode.OK, body);
                Assert.Equal(checkId, JsonDocument.Parse(body).RootElement.GetProperty("checkId").GetInt64());
            }

            await RefusedAsync(client, operate, duplicates, new { key = new[] { new { path = "data.Name" } } }, HttpStatusCode.Conflict, "is downloading these properties");
            Assert.Equal(checkId, Assert.Single((await JsonAsync(client, reader, duplicates)).EnumerateArray()).GetProperty("checkId").GetInt64());
            await RefusedAsync(client, operate, $"{duplicates}/{checkId}/selection", new { }, HttpStatusCode.Conflict, "is queued: only a check that downloaded every id");

            // The run downloads the properties into the check's table and groups it: two groups of two, waiting to be picked.
            Assert.NotNull(await ledger.StartInventoryDuplicateCheckAsync(flow.LedgerId, wellLogs.InventoryId, checkId, checkRun, firstBuild, now));
            await ledger.PlanInventoryDuplicateCheckAsync(flow.LedgerId, checkId, 4, 0);
            var attributes = InventoryDuplicateTable.AttributesOf(["data.WellboreID", "data.Name"]);
            await ledger.PrepareInventoryDuplicateTableAsync(flow.LedgerId, checkId, attributes);
            static InventoryDuplicateValue Value(string value) => new($"[\"{value}\"]", Key(value));
            var write = await ledger.AddInventoryDuplicateRowsAsync(flow.LedgerId, wellLogs.InventoryId, checkId, attributes,
            [
                new InventoryDuplicateRow(Id("ours-1"), 1, [Value("wb-1"), Value("GR")]),
                new InventoryDuplicateRow(Id("ours-2"), 2, [Value("wb-1"), Value("GR")]),
                new InventoryDuplicateRow(Id("ours-3"), 1, [Value("wb-2"), Value("DT")]),
                new InventoryDuplicateRow(Id("theirs"), 1, [Value("wb-2"), Value("DT")]),
                new InventoryDuplicateRow(Id("since"), 1, [Value("wb-2"), Value("DT")]),
            ], 5, 1);
            Assert.Equal(new InventoryDuplicateRowsWrite(4, 1, 1), write);
            var logKey = InventoryDuplicateKey.Of(["data.WellboreID", "data.Name"]).Key!;
            Assert.Equal(
                new InventoryDuplicateGrouping(2, 4, 0, 4, 0),
                await ledger.GroupInventoryDuplicatesAsync(partition, checkId, new InventoryDuplicateKeying(logKey.ToJson(), logKey.Hash(), logKey.Paths, "inventory api tests", now)));
            await ledger.CompleteInventoryDuplicateCheckAsync(flow.LedgerId, checkId, new InventoryDuplicateCheckEnd(InventoryDuplicates.Completed, ["a note"], null), now);

            var downloaded = await JsonAsync(client, reader, $"{duplicates}/{checkId}");
            Assert.Equal((InventoryDuplicates.Completed, 2L, 4L, 2L, 4L, $"osdu.dup_{checkId.ToString(CultureInfo.InvariantCulture)}"), (
                downloaded.GetProperty("status").GetString(), downloaded.GetProperty("groups").GetInt64(), downloaded.GetProperty("copies").GetInt64(),
                downloaded.GetProperty("pendingGroups").GetInt64(), downloaded.GetProperty("pendingCopies").GetInt64(), downloaded.GetProperty("table").GetString()));
            Assert.Equal([2L, 0L, 0L, 0L], downloaded.GetProperty("sizes").EnumerateArray().Select(s => s.GetProperty("groups").GetInt64()));
            await RefusedAsync(client, operate, $"{duplicates}/{checkId}/groups/2/keep", new { inventoryRecordId = (long?)null }, HttpStatusCode.Conflict, "pick the copies of the check's groups first");

            // A key of what the check downloaded groups it again at once, no run queued: by the wellbore alone, then by both again.
            int runs;
            await using (var db = CatalogDatabase.Create(cs))
            {
                runs = await db.Runs.CountAsync(r => r.PipelineId == pipelineId);
            }

            using (var regrouped = await PostAsync(client, operate, duplicates, new { key = new[] { new { path = "data.WellboreID" } } }))
            {
                var body = await regrouped.Content.ReadAsStringAsync();
                Assert.True(regrouped.StatusCode == HttpStatusCode.OK, body);
                var answer = JsonDocument.Parse(body).RootElement;
                Assert.Equal((checkId, 2L, 4L), (answer.GetProperty("checkId").GetInt64(), answer.GetProperty("groups").GetInt64(), answer.GetProperty("copies").GetInt64()));
                Assert.Equal(["data.WellboreID"], answer.GetProperty("key").EnumerateArray().Select(k => k.GetProperty("path").GetString()!));
                Assert.False(string.IsNullOrEmpty(answer.GetProperty("keyedBy").GetString()));
            }

            using (var back = await PostAsync(client, operate, duplicates, new { key }))
            {
                Assert.Equal(HttpStatusCode.OK, back.StatusCode);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(runs, await db.Runs.CountAsync(r => r.PipelineId == pipelineId));
            }

            // A pick is a run of the flow too, asked for on its own; it reads the copies and picks the copy each group keeps.
            Guid pickRun;
            using (var asked = await PostAsync(client, operate, $"{duplicates}/{checkId}/pick", new { }))
            {
                var body = await asked.Content.ReadAsStringAsync();
                Assert.True(asked.StatusCode == HttpStatusCode.Accepted, body);
                var answer = JsonDocument.Parse(body).RootElement;
                Assert.Equal((InventoryDuplicates.Queued, InventoryDuplicates.ReadingCopies), (answer.GetProperty("status").GetString(), answer.GetProperty("phase").GetString()));
                pickRun = answer.GetProperty("runId").GetGuid();
                Assert.NotEqual(checkRun, pickRun);
            }

            await RefusedAsync(client, operate, $"{duplicates}/{checkId}/pick", new { }, HttpStatusCode.Conflict, "is picking the copies of its groups");
            Assert.NotNull(await ledger.StartInventoryDuplicatePickAsync(flow.LedgerId, checkId, pickRun, now));
            var toJudge = await ledger.InventoryDuplicateGroupsToJudgeAsync(flow.LedgerId, checkId, 0, 10);
            var figures = new Dictionary<string, int>(StringComparer.Ordinal) { [Id("ours-1")] = 9, [Id("ours-2")] = 3, [Id("ours-3")] = 5, [Id("theirs")] = 5 };
            var orphans = new HashSet<string>(StringComparer.Ordinal) { InventoryFindings.Orphan };
            var judged = toJudge.Select(g => InventoryDuplicatePicker.Judge(
                g.GroupNumber, g.Copies.Select(c => new DuplicateCopy(c.InventoryRecordId, c.TargetId, c.Finding, InventoryDuplicates.Held, 1, figures[c.TargetId], 0)).ToList(), orphans, null)).ToList();
            await ledger.WriteInventoryDuplicateJudgementsAsync(flow.LedgerId, checkId, judged, 3, now);
            await ledger.CompleteInventoryDuplicatePickAsync(flow.LedgerId, checkId, new InventoryDuplicatePickEnd(["a note"], null), now);
            await RefusedAsync(client, operate, $"{duplicates}/{checkId}/pick", new { }, HttpStatusCode.Conflict, "holds no group waiting to be picked");

            var check = await JsonAsync(client, reader, $"{duplicates}/{checkId}");
            Assert.Equal((InventoryDuplicates.Completed, 2L, 4L, 1L, 1L, 1L, 4L), (
                check.GetProperty("status").GetString(), check.GetProperty("groups").GetInt64(), check.GetProperty("copies").GetInt64(), check.GetProperty("decided").GetInt64(),
                check.GetProperty("review").GetInt64(), check.GetProperty("toRemove").GetInt64(), check.GetProperty("requests").GetInt64()));
            Assert.Equal((1L, 1L, false), (check.GetProperty("outside").GetInt64(), check.GetProperty("changed").GetInt64(), check.GetProperty("selectionCurrent").GetBoolean()));
            var outcomes = check.GetProperty("outcomes").EnumerateArray().ToDictionary(o => o.GetProperty("outcome").GetString()!, o => o.GetProperty("groups").GetInt64());
            Assert.Equal((1L, 1L, 0L), (outcomes[InventoryDuplicates.Review], outcomes[InventoryDuplicates.Picked], outcomes[InventoryDuplicates.Chosen]));
            Assert.Equal(["a note"], check.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!));

            // The groups, the largest first, each with its copies; one outcome's groups alone; and a group's copies a page at a time.
            var groups = (await JsonAsync(client, reader, $"{duplicates}/{checkId}/groups")).GetProperty("items").EnumerateArray().ToList();
            Assert.Equal([(1, InventoryDuplicates.Picked), (2, InventoryDuplicates.Review)], groups.Select(g => (g.GetProperty("groupNumber").GetInt32(), g.GetProperty("outcome").GetString()!)));
            var picked = groups[0].GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("record").GetProperty("targetId").GetString()!);
            Assert.Equal((InventoryDuplicates.Keep, 9, InventoryDuplicates.Remove, 3), (
                picked[Id("ours-1")].GetProperty("role").GetString(), picked[Id("ours-1")].GetProperty("attributes").GetInt32(),
                picked[Id("ours-2")].GetProperty("role").GetString(), picked[Id("ours-2")].GetProperty("attributes").GetInt32()));
            Assert.Equal(["GR"], picked[Id("ours-2")].GetProperty("values").GetProperty("data.Name").EnumerateArray().Select(v => v.GetString()!));
            Assert.Equal((2L, "orphan"), (picked[Id("ours-2")].GetProperty("indexedVersion").GetInt64(), picked[Id("ours-2")].GetProperty("record").GetProperty("finding").GetString()));
            var review = Assert.Single((await JsonAsync(client, reader, $"{duplicates}/{checkId}/groups?outcome=review")).GetProperty("items").EnumerateArray());
            Assert.Equal(2, review.GetProperty("groupNumber").GetInt32());
            await ProblemAsync(client, reader, $"{duplicates}/{checkId}/groups?outcome=bogus", HttpStatusCode.BadRequest, "'bogus' is not how a group of duplicates comes out");
            var copies = await JsonAsync(client, reader, $"{duplicates}/{checkId}/groups/2/copies?limit=1");
            Assert.Equal(1, copies.GetProperty("items").GetArrayLength());
            Assert.True(copies.TryGetProperty("next", out _));
            await ProblemAsync(client, reader, $"{duplicates}/{checkId}/groups/9/copies", HttpStatusCode.NotFound, "holds no group 9");
            await ProblemAsync(client, reader, $"{duplicates}/2147480000", HttpStatusCode.NotFound, "No duplicate check 2147480000");

            // An operator chooses the copy the tie keeps: another identity's, so the orphan beside it is picked to remove.
            var theirs = review.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("record").GetProperty("targetId").GetString() == Id("theirs"));
            var keep = $"{duplicates}/{checkId}/groups/2/keep";
            await RefusedAsync(client, operate, keep, new { inventoryRecordId = 2147480000L }, HttpStatusCode.BadRequest, "is not one of the copies of group 2");
            using (var chose = await PostAsync(client, operate, keep, new { inventoryRecordId = theirs.GetProperty("record").GetProperty("inventoryRecordId").GetInt64() }))
            {
                var body = await chose.Content.ReadAsStringAsync();
                Assert.True(chose.StatusCode == HttpStatusCode.OK, body);
                var group = JsonDocument.Parse(body).RootElement;
                Assert.Equal((InventoryDuplicates.Chosen, false), (group.GetProperty("outcome").GetString(), string.IsNullOrEmpty(group.GetProperty("chosenBy").GetString())));
                var roles = group.GetProperty("members").EnumerateArray().ToDictionary(m => m.GetProperty("record").GetProperty("targetId").GetString()!, m => m.GetProperty("role").GetString()!);
                Assert.Equal((InventoryDuplicates.Keep, InventoryDuplicates.Remove), (roles[Id("theirs")], roles[Id("ours-3")]));
            }

            Assert.Equal((2L, 0L, 2L), (
                (await JsonAsync(client, reader, $"{duplicates}/{checkId}")).GetProperty("decided").GetInt64(),
                (await JsonAsync(client, reader, $"{duplicates}/{checkId}")).GetProperty("review").GetInt64(),
                (await JsonAsync(client, reader, $"{duplicates}/{checkId}")).GetProperty("toRemove").GetInt64()));

            // The picks made a selection, which names the check and its key; asked again with nothing chosen since, it is the same one.
            long selectionId;
            using (var made = await PostAsync(client, operate, $"{duplicates}/{checkId}/selection", new { }))
            {
                var body = await made.Content.ReadAsStringAsync();
                Assert.True(made.StatusCode == HttpStatusCode.Created, body);
                var selection = JsonDocument.Parse(body).RootElement;
                selectionId = selection.GetProperty("selectionId").GetInt64();
                Assert.Equal((InventorySelections.Completed, 2L, checkId), (selection.GetProperty("status").GetString(), selection.GetProperty("matched").GetInt64(), selection.GetProperty("duplicateCheckId").GetInt64()));
                Assert.Equal("data.WellboreID", selection.GetProperty("duplicateKey")[0].GetProperty("path").GetString());
                Assert.Empty(selection.GetProperty("conditions").EnumerateArray());
                Assert.Equal(2L, selection.GetProperty("counts").EnumerateArray().Single(c => c.GetProperty("finding").GetString() == "orphan").GetProperty("count").GetInt64());
            }

            using (var same = await PostAsync(client, operate, $"{duplicates}/{checkId}/selection", new { }))
            {
                var body = await same.Content.ReadAsStringAsync();
                Assert.True(same.StatusCode == HttpStatusCode.OK, body);
                Assert.Equal(selectionId, JsonDocument.Parse(body).RootElement.GetProperty("selectionId").GetInt64());
            }

            Assert.True((await JsonAsync(client, reader, $"{duplicates}/{checkId}")).GetProperty("selectionCurrent").GetBoolean());
            var selected = await JsonAsync(client, reader, $"{inventoryPath}/records?selection={selectionId}");
            Assert.Equal([Id("ours-2"), Id("ours-3")], selected.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("targetId").GetString()!).Order(StringComparer.Ordinal));

            // A choice taken back leaves the tie for review again, and the selection made before it no longer stands.
            using (var cleared = await PostAsync(client, operate, keep, new { inventoryRecordId = (long?)null }))
            {
                var body = await cleared.Content.ReadAsStringAsync();
                Assert.True(cleared.StatusCode == HttpStatusCode.OK, body);
                Assert.Equal(InventoryDuplicates.Review, JsonDocument.Parse(body).RootElement.GetProperty("outcome").GetString());
            }

            Assert.False((await JsonAsync(client, reader, $"{duplicates}/{checkId}")).GetProperty("selectionCurrent").GetBoolean());
            long remade;
            using (var made = await PostAsync(client, operate, $"{duplicates}/{checkId}/selection", new { }))
            {
                var body = await made.Content.ReadAsStringAsync();
                Assert.True(made.StatusCode == HttpStatusCode.Created, body);
                remade = JsonDocument.Parse(body).RootElement.GetProperty("selectionId").GetInt64();
                Assert.NotEqual(selectionId, remade);
                Assert.Equal(1L, JsonDocument.Parse(body).RootElement.GetProperty("matched").GetInt64());
            }

            // Every copy of every group as CSV, with the key's values at its end.
            using (var csv = await SendAsync(client, reader, $"{duplicates}/{checkId}/export"))
            {
                var text = await csv.Content.ReadAsStringAsync();
                Assert.True(csv.StatusCode == HttpStatusCode.OK, text);
                var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Assert.StartsWith("group_number,group_copies,group_outcome,", lines[0], StringComparison.Ordinal);
                Assert.EndsWith(",data.WellboreID,data.Name", lines[0], StringComparison.Ordinal);
                Assert.Equal(5, lines.Length);
                Assert.Contains($"duplicates-{checkId.ToString(CultureInfo.InvariantCulture)}.csv", csv.Content.Headers.ContentDisposition?.ToString(), StringComparison.Ordinal);
            }

            // A removal takes the selection's ids once the flow is idle, and names the check that picked them.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.PipelineId == pipelineId).ExecuteDeleteAsync();
            }

            var removals = inventoryPath + "/removals";
            Guid removalRun;
            using (var accepted = await PostAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 1, selection = remade, confirm = partition }))
            {
                var body = await accepted.Content.ReadAsStringAsync();
                Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, body);
                removalRun = JsonDocument.Parse(body).RootElement.GetProperty("runId").GetGuid();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.RunId == removalRun).ExecuteDeleteAsync();
            }

            await ledger.StartInventoryRemovalAsync(
                flow.LedgerId, new InventoryRemovalStart(wellLogs.InventoryId, removalRun, "inventory api tests", "orphan", "record", false, 1, null, now, remade));
            var named = Assert.Single((await JsonAsync(client, reader, removals)).EnumerateArray()).GetProperty("selection");
            Assert.Equal((remade, checkId, "data.Name"), (named.GetProperty("selectionId").GetInt64(), named.GetProperty("duplicateCheckId").GetInt64(), named.GetProperty("duplicateKey")[1].GetProperty("path").GetString()));

            // A build since the check leaves it stale: no choice or selection is written of it, and a removal of its picks waits for a new check.
            await BuildAsync(now.AddMinutes(-10));
            Assert.True((await JsonAsync(client, reader, $"{duplicates}/{checkId}")).GetProperty("stale").GetBoolean());
            await RefusedAsync(client, operate, keep, new { inventoryRecordId = theirs.GetProperty("record").GetProperty("inventoryRecordId").GetInt64() }, HttpStatusCode.Conflict, "was built again since duplicate check");
            await RefusedAsync(client, operate, $"{duplicates}/{checkId}/selection", new { }, HttpStatusCode.Conflict, "was built again since duplicate check");
            await RefusedAsync(client, operate, removals, new { finding = "orphan", scope = "record", expected = 1, selection = remade, confirm = partition }, HttpStatusCode.Conflict, $"since duplicate check {checkId} picked the copies");
            using (var rebuilt = await PostAsync(client, operate, duplicates, new { key }))
            {
                var body = await rebuilt.Content.ReadAsStringAsync();
                Assert.True(rebuilt.StatusCode == HttpStatusCode.Accepted, body);
                Assert.NotEqual(checkId, JsonDocument.Parse(body).RootElement.GetProperty("checkId").GetInt64());
            }
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.PipelineId == pipelineId).ExecuteDeleteAsync();
            }

            await CleanUpAsync(cs, repoId, partition, flow.LedgerId, deliveryFlow);
        }
    }

    [Fact]
    public async Task Missing_ids_are_delivered_again_through_their_flows_under_their_own_ids_or_under_new_ones_and_what_cannot_be_is_said()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var inventoryName = "api-repair-" + suffix;
        var deliveryName = "api-repair-delivery-" + suffix;
        var partition = "rp" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-repairs-" + suffix);
        var inventoryPipeline = CatalogIdentity.Pipeline(repoId, inventoryName);
        var deliveryPipeline = CatalogIdentity.Pipeline(repoId, deliveryName);
        var inventoryYaml = $$"""
            flowType: inventory
            name: {{inventoryName}}
            partitions: [{{partition}}]
            owners: [{{Estate}}]
            source:
              endpoint: http://localhost
            inventories:
              - name: Wellbores
                kind: "*:*:master-data--Wellbore:*"
            """;
        var deliveryYaml = $$"""
            flowType: delivery
            name: {{deliveryName}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.Wellbore, key: [facility_name] }
              work: ../.work/repair
            render:
              mapping: Wellbore@1.0.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: storage
              headers: { data-partition-id: {{partition}} }
            """;
        var flow = new DeliveryDocumentLoader().ParseInventory(inventoryYaml, "flows/" + inventoryName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var deliveryFlow = FlowId.Of(deliveryName);
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        string Id(Guid key) => $"{partition}:master-data--Wellbore:{key:N}";

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-repairs-" + suffix, RemoteUrl = "https://example/cp-repairs.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(inventoryPipeline, repoId, inventoryName, InventoryFlowDefinition.FlowTypeName, inventoryYaml, now));
            db.Pipelines.Add(Pipeline(deliveryPipeline, repoId, deliveryName, "delivery", deliveryYaml, now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        var lost = Enumerable.Range(1, 2).Select(i => DeliveryKey.Derive("repair", [$"W-{suffix}-{i}"]).Value).ToList();
        var held = DeliveryKey.Derive("repair", [$"H-{suffix}"]).Value;
        var minted = DeliveryKey.Derive("repair", [$"M-{suffix}"]).Value;
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Inventory, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });
            await ledger.RegisterAsync(deliveryFlow, partition, deliveryName);

            // Two records the delivery ledger holds as delivered and one held since the inventory was reconciled.
            short ledgerPartition;
            await using (var osdu = SampleEstate.Context(cs))
            {
                ledgerPartition = await osdu.DeliveryLedgers.AsNoTracking().Where(l => l.FlowId == deliveryFlow).Select(l => l.PartitionId).SingleAsync();
                foreach (var (key, i) in lost.Select((k, i) => (k, i)))
                {
                    osdu.DeliveryRecords.Add(new DeliveryRecord
                    {
                        PartitionId = ledgerPartition, FlowId = deliveryFlow, DeliveryKey = key, SourceKey = $"W-{suffix}-{i}", SourceKeyJson = $"[\"W-{suffix}-{i}\"]",
                        MappingName = "Wellbore", Status = "delivered", TargetId = Id(key), ClaimedTargetId = Id(key), TargetVersion = 1,
                        MetadataHash = "hash-" + i, SourceFingerprint = "fingerprint-" + i, SourceModifiedUtc = now, CreatedUtc = now, UpdatedUtc = now,
                    });
                }

                osdu.DeliveryRecords.Add(new DeliveryRecord
                {
                    PartitionId = ledgerPartition, FlowId = deliveryFlow, DeliveryKey = held, SourceKey = $"H-{suffix}", SourceKeyJson = $"[\"H-{suffix}\"]",
                    MappingName = "Wellbore", Status = "held", TargetId = Id(held), ClaimedTargetId = Id(held), CreatedUtc = now, UpdatedUtc = now,
                });
                await osdu.SaveChangesAsync();
            }

            // The inventory found five ids missing: the two delivered records, the held one, a dataset a delivery minted, and one
            // whose record no ledger holds any more.
            var wellbores = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "Wellbores", "*:*:master-data--Wellbore:*", null, "search", "latest");
            var build = await ledger.StartInventoryRunAsync(flow.LedgerId, wellbores.InventoryId, InventoryRunStatus.Build, Guid.NewGuid(), "inventory api tests", "search", now.AddHours(-1));
            await using (var osdu = SampleEstate.Context(cs))
            {
                var inventoryPartition = await osdu.DeliveryInventories.AsNoTracking().Where(i => i.InventoryId == wellbores.InventoryId).Select(i => i.PartitionId).SingleAsync();
                DeliveryInventoryRecord Missing(string targetId, Guid? ledgerFlow, Guid? key, string? status, long? artifact) => new()
                {
                    PartitionId = inventoryPartition, InventoryId = wellbores.InventoryId, TargetId = targetId, Finding = InventoryFindings.Missing, FindingUtc = now,
                    CheckedUtc = now, LedgerFlowId = ledgerFlow, DeliveryKey = key, LedgerStatus = status, ArtifactId = artifact,
                    Detail = "a ledger expects it, and storage does not hold it",
                };
                osdu.DeliveryInventoryRecords.AddRange(
                    Missing(Id(lost[0]), deliveryFlow, lost[0], "delivered", null),
                    Missing(Id(lost[1]), deliveryFlow, lost[1], "delivered", null),
                    Missing(Id(held), deliveryFlow, held, "delivered", null),
                    Missing($"{partition}:dataset--File.Generic:{minted:N}", deliveryFlow, minted, null, 77),
                    Missing($"{partition}:master-data--Wellbore:forgotten-{suffix}", null, null, null, null));
                await osdu.SaveChangesAsync();
            }

            var counts = await ledger.InventoryCountsAsync(flow.LedgerId, wellbores.InventoryId);
            await ledger.CompleteInventoryRunAsync(
                flow.LedgerId, build,
                new InventoryRunState
                {
                    InventoryRunId = build, InventoryId = wellbores.InventoryId, Operation = InventoryRunStatus.Build, Actor = "inventory api tests",
                    Status = InventoryRunStatus.Completed, StartedUtc = now.AddHours(-1), ReadMode = "search", FindingsJson = JsonSerializer.Serialize(counts.ByFinding),
                    OwnersJson = JsonSerializer.Serialize(new { source = "declared", owners = new[] { new { identity = Estate, records = 0 } } }),
                },
                "declared", now.AddHours(-1).AddMinutes(1));

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var operate = await TokenAsync(client, ["read", "operate"]);
            var repairs = $"/api/v1/delivery/inventories/{partition}/{wellbores.InventoryId.ToString(CultureInfo.InvariantCulture)}/repairs";

            await RefusedAsync(client, operate, repairs, new { }, HttpStatusCode.BadRequest, "Count required");
            await RefusedAsync(client, operate, repairs, new { expected = 4 }, HttpStatusCode.Conflict, "holds 5 missing id(s) now");
            await RefusedAsync(client, operate, repairs, new { expected = 1, ids = new[] { $"{partition}:master-data--Wellbore:never-{suffix}" } }, HttpStatusCode.Conflict, "not a missing id");
            await RefusedAsync(client, operate, repairs, new { expected = 5, newIds = true }, HttpStatusCode.BadRequest, "Confirmation required");
            await RefusedAsync(client, operate, repairs, new { expected = 5, newIds = true, confirm = "prod" }, HttpStatusCode.BadRequest, "is kept in partition");

            async Task<DeliveryRecord> RecordAsync(Guid key)
            {
                await using var osdu = SampleEstate.Context(cs);
                return await osdu.DeliveryRecords.AsNoTracking().SingleAsync(r => r.FlowId == deliveryFlow && r.DeliveryKey == key);
            }

            // Under their own ids: the delivered records are marked lost and a deliver run of their flow is queued; the held
            // record, the minted dataset and the id no ledger holds are left, each with why.
            JsonElement answer;
            using (var response = await PostAsync(client, operate, repairs, new { expected = 5 }))
            {
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.Accepted, body);
                answer = JsonDocument.Parse(body).RootElement.Clone();
            }

            Assert.Equal((5L, 2, false), (answer.GetProperty("asked").GetInt64(), answer.GetProperty("marked").GetInt32(), answer.GetProperty("newIds").GetBoolean()));
            var repaired = Assert.Single(answer.GetProperty("flows").EnumerateArray());
            Assert.Equal((deliveryName, 3, 2), (repaired.GetProperty("flow").GetString(), repaired.GetProperty("asked").GetInt32(), repaired.GetProperty("marked").GetInt32()));
            var runId = repaired.GetProperty("runId").GetGuid();
            var reasons = answer.GetProperty("skipped").EnumerateArray().ToDictionary(s => s.GetProperty("reason").GetString()!, s => s.GetProperty("count").GetInt32());
            Assert.Equal(3, reasons.Count);
            Assert.Equal(1, reasons.Single(r => r.Key.Contains("not delivered in its ledger now", StringComparison.Ordinal)).Value);
            Assert.Equal(1, reasons.Single(r => r.Key.Contains("a delivery minted", StringComparison.Ordinal)).Value);
            Assert.Equal(1, reasons.Single(r => r.Key.Contains("no ledger record names it", StringComparison.Ordinal)).Value);
            foreach (var key in lost)
            {
                var record = await RecordAsync(key);
                Assert.Equal(("delivered", "missing", Id(key)), (record.Status, record.LastVerifyOutcome, record.ClaimedTargetId));
                Assert.NotNull(record.PlanRequestedUtc);
            }

            Assert.Null((await RecordAsync(held)).LastVerifyOutcome);
            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                Assert.Equal((deliveryPipeline, "deliver", "queued"), (run.PipelineId, run.Operation, run.Status));
                await db.Runs.Where(r => r.RunId == runId).ExecuteDeleteAsync();
            }

            await using (var osdu = SampleEstate.Context(cs))
            {
                var repair = await osdu.DeliveryActivities.AsNoTracking().SingleAsync(a => a.FlowId == flow.LedgerId && a.Kind == InventoryRepairs.ActivityKind);
                Assert.Equal(("completed", answer.GetProperty("activityId").GetInt64()), (repair.Outcome, repair.ActivityId));
                Assert.Contains("2 of 5 missing id(s)", repair.Summary, StringComparison.Ordinal);
                Assert.True(await osdu.DeliveryActivities.AnyAsync(a => a.FlowId == deliveryFlow && a.Kind == "redeliver"));
            }

            // Under new ids, with the partition typed back: each delivered record claims a new id, kept with the one OSDU lost.
            using (var response = await PostAsync(client, operate, repairs, new { expected = 2, ids = new[] { Id(lost[0]), Id(lost[1]) }, newIds = true, confirm = partition }))
            {
                var body = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.Accepted, body);
                Assert.Equal(2, JsonDocument.Parse(body).RootElement.GetProperty("marked").GetInt32());
            }

            foreach (var key in lost)
            {
                var record = await RecordAsync(key);
                Assert.Equal(TargetId.Reassigned(Id(key), new DeliveryKey(key)), record.ClaimedTargetId);
                Assert.NotNull(record.TargetIdAssignedUtc);
                Assert.Null(record.TargetVersion);
            }
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.PipelineId == inventoryPipeline || r.PipelineId == deliveryPipeline).ExecuteDeleteAsync();
            }

            await CleanUpAsync(cs, repoId, partition, flow.LedgerId, deliveryFlow);
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task RefusedAsync(HttpClient client, string token, string path, object body, HttpStatusCode status, string says)
    {
        using var response = await PostAsync(client, token, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"POST {path} answered {(int)response.StatusCode}, not {(int)status}: {text}");
        Assert.Contains(says, text, StringComparison.Ordinal);
    }

    private static InventoryScanRow Row(string id, long version, string creator)
        => new(id, "osdu:wks:work-product-component--WellLog:1.4.0", version, creator, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), creator,
            new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc));

    /// <summary>A record of the delivery ledger claiming <paramref name="targetId"/>, in <paramref name="status"/> at <paramref name="version"/>.</summary>
    private static async Task<DeliveryKey> ClaimAsync(string cs, OsduLedger ledger, Guid flowId, string targetId, string sourceKey, string status, long? version)
    {
        var key = DeliveryKey.Derive("inventory-api-tests", [targetId]);
        await ledger.UpsertPendingAsync(flowId, [new RecordState
        {
            DeliveryKey = key, FlowId = flowId, SourceKey = sourceKey, MappingName = "WellLog", TargetId = targetId, LastSubmissionId = Guid.NewGuid(),
            PendingDocumentRef = "0:0:10", PendingRenderContext = "{}", PendingMetadataHash = "mh", PendingMetadata = true,
        }]);
        await using var osdu = SampleEstate.Context(cs);
        await osdu.DeliveryRecords
            .Where(r => r.FlowId == flowId && r.DeliveryKey == key.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status).SetProperty(r => r.TargetVersion, version).SetProperty(r => r.ClaimedTargetId, targetId));
        return key;
    }

    private static async Task CleanUpAsync(string cs, Guid repoId, string partition, Guid inventoryFlow, Guid deliveryFlow)
    {
        await using (var osdu = SampleEstate.Context(cs))
        {
            var inventories = await osdu.DeliveryInventories.Where(i => i.FlowId == inventoryFlow).Select(i => i.InventoryId).ToListAsync();
            var runs = await osdu.DeliveryInventoryRuns.Where(r => inventories.Contains(r.InventoryId)).Select(r => r.InventoryRunId).ToListAsync();
            var records = osdu.DeliveryInventoryRecords.Where(r => inventories.Contains(r.InventoryId)).Select(r => r.InventoryRecordId);
            var removals = osdu.DeliveryInventoryRemovals.Where(r => inventories.Contains(r.InventoryId)).Select(r => r.InventoryRemovalId);
            var selections = osdu.DeliveryInventorySelections.Where(s => inventories.Contains(s.InventoryId)).Select(s => s.InventorySelectionId);
            var checks = osdu.DeliveryInventoryDuplicateChecks.Where(c => inventories.Contains(c.InventoryId)).Select(c => c.InventoryDuplicateCheckId);
            foreach (var check in await checks.ToListAsync())
            {
                // A check's table is made at run time, so the test that made it drops it as a sweep would; its name is a number's.
                var drop = "DROP TABLE IF EXISTS [osdu].[" + InventoryDuplicateTable.NameOf(check) + "];";
                await osdu.Database.ExecuteSqlRawAsync(drop);
            }

            await osdu.DeliveryInventoryDuplicateMembers.Where(m => checks.Contains(m.InventoryDuplicateCheckId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventoryDuplicateGroups.Where(g => checks.Contains(g.InventoryDuplicateCheckId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventoryDuplicateChecks.Where(c => inventories.Contains(c.InventoryId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventorySelectionItems.Where(i => selections.Contains(i.InventorySelectionId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventorySelections.Where(s => inventories.Contains(s.InventoryId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventoryRemovalItems.Where(i => removals.Contains(i.InventoryRemovalId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventoryRemovals.Where(r => inventories.Contains(r.InventoryId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventoryScans.Where(s => runs.Contains(s.InventoryRunId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventoryVersions.Where(v => records.Contains(v.InventoryRecordId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventoryRecords.Where(r => inventories.Contains(r.InventoryId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventoryRuns.Where(r => inventories.Contains(r.InventoryId)).ExecuteDeleteAsync();
            await osdu.DeliveryInventories.Where(i => i.FlowId == inventoryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryActivityRecords.Where(a => a.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryActivities.Where(a => a.FlowId == inventoryFlow || a.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryAttempts.Where(a => a.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryRecordEvents.Where(e => e.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => i.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryLedgers.Where(l => l.FlowId == inventoryFlow || l.FlowId == deliveryFlow).ExecuteDeleteAsync();
            await osdu.DeliveryLedgerPartitions.Where(p => p.Name == partition).ExecuteDeleteAsync();
        }

        await using (var db = CatalogDatabase.Create(cs))
        {
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, string kind, string yaml, DateTime now) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = kind,
        RelativePath = "flows/" + name + ".yaml",
        ContentHash = new string('0', 64),
        Yaml = yaml,
        DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"{{kind}}"}"""),
        Active = true,
        Wave = 0,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

    [Fact]
    public async Task The_duplicate_check_sweep_closes_checks_whose_runs_ended_and_retires_those_past_their_week_with_their_tables()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var partition = "sw" + suffix;
        var flowName = "api-sweep-" + suffix;
        var flow = new DeliveryDocumentLoader().ParseInventory($$"""
            flowType: inventory
            name: {{flowName}}
            partitions: [{{partition}}]
            owners: [{{Estate}}]
            source:
              endpoint: http://localhost
            inventories:
              - name: WellLogs
                kind: "{{WellLog}}"
            """, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var deliveryFlow = FlowId.Of("api-sweep-delivery-" + suffix);
        var now = DateTime.UtcNow;
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Inventory, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });
            var wellLogs = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "WellLogs", WellLog, null, "search", "latest");
            var files = await ledger.RegisterInventoryAsync(flow.LedgerId, flow.Name, "LogFiles", LogFile, null, "search", "latest");
            var key = InventoryDuplicateKey.Of(["data.Name"]).Key!;
            var attributes = InventoryDuplicateTable.AttributesOf(key.Paths);

            // A check of each kind, each with its table: one past its week (of another inventory, since a download completed since
            // lets go of an inventory's earlier tables), one whose run the catalog no longer holds, one recent.
            async Task<long> CheckAsync(int inventoryId, DateTime created, bool complete)
            {
                var checkId = await ledger.CreateInventoryDuplicateCheckAsync(
                    partition, new InventoryDuplicateCheckStart(inventoryId, "inventory api tests", key.ToJson(), key.Hash(), attributes, null, created));
                Assert.NotNull(await ledger.StartInventoryDuplicateCheckAsync(flow.LedgerId, inventoryId, checkId, Guid.NewGuid(), null, created));
                await ledger.PrepareInventoryDuplicateTableAsync(flow.LedgerId, checkId, attributes);
                if (complete)
                {
                    await ledger.CompleteInventoryDuplicateCheckAsync(flow.LedgerId, checkId, new InventoryDuplicateCheckEnd(InventoryDuplicates.Completed, [], null), created);
                }

                return checkId;
            }

            var old = await CheckAsync(files.InventoryId, now - InventoryDuplicates.KeptFor - TimeSpan.FromHours(2), complete: true);
            var stuck = await CheckAsync(wellLogs.InventoryId, now, complete: false);
            var recent = await CheckAsync(wellLogs.InventoryId, now, complete: true);
            Assert.True(await TableExistsAsync(cs, old) && await TableExistsAsync(cs, stuck) && await TableExistsAsync(cs, recent));

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var sweep = factory.Services.GetServices<IHostedService>().OfType<InventoryDuplicateSweepService>().Single();
            var pass = await sweep.SweepAsync(CancellationToken.None);

            Assert.True(pass is { Settled: >= 1, Deleted: >= 1 }, $"the pass closed {pass.Settled} and deleted {pass.Deleted}");
            Assert.Null(await ledger.GetInventoryDuplicateCheckAsync(partition, old));
            Assert.False(await TableExistsAsync(cs, old));
            var closed = await ledger.GetInventoryDuplicateCheckAsync(partition, stuck);
            Assert.Equal((InventoryDuplicates.Failed, (string?)null), (closed?.Status, closed?.TableName));
            Assert.Contains("is no longer in the catalog", closed!.Error, StringComparison.Ordinal);
            Assert.False(await TableExistsAsync(cs, stuck));
            Assert.Equal(InventoryDuplicates.Completed, (await ledger.GetInventoryDuplicateCheckAsync(partition, recent))?.Status);
            Assert.True(await TableExistsAsync(cs, recent));
        }
        finally
        {
            await CleanUpAsync(cs, Guid.NewGuid(), partition, flow.LedgerId, deliveryFlow);
        }
    }

    /// <summary>Whether the module's database holds a duplicate check's table.</summary>
    private static async Task<bool> TableExistsAsync(string cs, long checkId)
    {
        await using var osdu = SampleEstate.Context(cs);
        await osdu.Database.OpenConnectionAsync();
        await using var command = osdu.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT OBJECT_ID(N'osdu." + InventoryDuplicateTable.NameOf(checkId) + "', N'U');";
        return await command.ExecuteScalarAsync() is not (null or DBNull);
    }

    private static ControlPlaneAppFactory Factory(string cs) => new ControlPlaneAppFactory()
        .WithCatalog(cs)
        .WithModules(new DeliveryControlPlaneModule())
        .WithSetting("ControlPlane:Worker:Enabled", "false")
        .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    private static async Task ProblemAsync(HttpClient client, string token, string path, HttpStatusCode status, string says)
    {
        using var response = await SendAsync(client, token, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"GET {path} answered {(int)response.StatusCode}, not {(int)status}: {body}");
        Assert.Contains(says, body, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>A GET as the workbench sends it: with the partition picked in its title bar, when one is.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path, string? workbenchPartition = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (workbenchPartition is not null)
        {
            request.Headers.Add(WorkbenchPartition.Header, workbenchPartition);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client, string[]? scopes = null)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes ?? ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
