using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Removal flows as the control plane serves them (osdu/docs/reference/concepts/api.md, Removal flows): the partition's
/// removal flows, one flow with each removal, its delivery ledger's deletions by state and its last run, the runs, a
/// delivery ledger's deletions a page at a time by key, the record's own deletion on its page, the approval of what a guard
/// held with every refusal it answers, and lifting deletions for one record and for many. The ledger is seeded as a removal
/// run writes it, under a delivery flow's ledger, so what the API is held to is what a run leaves; nothing reaches an OSDU.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Suite", "Removal")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryRemovalApiTests
{
    private const string Partition = TestLedgers.Partition;

    [Fact]
    public async Task A_removal_flow_its_runs_its_deletions_their_approval_and_their_lift_are_served_in_its_partition()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var deliveryName = "api-removal-delivery-" + suffix;
        var removalName = "api-removal-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-removals-" + suffix);
        var deliveryPipeline = CatalogIdentity.Pipeline(repoId, deliveryName);
        var removalPipeline = CatalogIdentity.Pipeline(repoId, removalName);
        var flowId = FlowId.Of(deliveryName);
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var removalYaml = $$"""
            flowType: removal
            name: {{removalName}}
            description: The wellbores the source no longer holds.
            partitions: [{{Partition}}]
            guard: { maxPercent: 10, maxRecords: 500 }
            removals:
              - name: wellbores
                flow: {{deliveryName}}
                keys: { type: csv, location: ../keys/wellbores }
                removed:
                  connection: ${env:WELLDB_SOURCE_DB}
                  object: WellDB.dbo.DeletedWellbore
                  removedAt: { name: deleted_at }
            """;

        await SeedCatalogAsync(cs, repoId, deliveryPipeline, deliveryName, removalPipeline, removalName, removalYaml, now);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        try
        {
            await ledger.RegisterAsync(flowId, Partition, deliveryName);
            await SeedInterfaceAsync(cs, repoId, deliveryName, flowId, now);

            // Two records delivered, one the removal took out: it is deleted, and its key is removed.
            var live = await DeliveredAsync(cs, ledger, flowId, $"WB-{suffix}-live");
            var held = await DeliveredAsync(cs, ledger, flowId, $"WB-{suffix}-held");
            var gone = await DeliveredAsync(cs, ledger, flowId, $"WB-{suffix}-gone");
            await ledger.MarkRemovedAsync(flowId, [gone], RemovalScope.Record, "removal", now, deliverAgain: false);

            // A run whose guard held what it found, and the deletions it left: 3 waiting for approval (the held record's key
            // among them), 2 pending, and 250 removed (the gone record's key among them).
            var run = await ledger.StartRemovalRunAsync(flowId, new RemovalRunStart
            {
                FlowName = removalName, Entry = "wellbores", DeliveryFlow = deliveryName, Operation = DeliveryOperations.Remove,
                Actor = "schedule:welldb-removal", StartedUtc = now.AddMinutes(-10), GuardPercent = 10, GuardRecords = 500,
            });
            await ledger.AddRemovalTallyAsync(flowId, run, new RemovalTally { SourceKeys = 2200, Compared = 2455, Detected = 3, AwaitingApproval = 3 });
            await ledger.CompleteRemovalRunAsync(flowId, run, SourceRemovals.Guarded, "3 key(s) found removed wait for an operator's approval", null, now.AddMinutes(-9));
            var keys = await SeedDeletionsAsync(cs, flowId, removalName, run, held, gone, now);

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var reader = await TokenAsync(client, ["read"]);
            var operate = await TokenAsync(client, ["operate"]);

            // Every removal flow of the partition, as the workbench reads it: this one with its removal's ledger and deletions.
            var all = await ReadAsync(client, reader, "/api/v1/delivery/removals", Partition);
            var listed = Assert.Single(all.EnumerateArray().ToList(), f => f.GetProperty("name").GetString() == removalName);
            Assert.Equal((removalPipeline, Partition, 10, 500L), (listed.GetProperty("pipelineId").GetGuid(), listed.GetProperty("partition").GetString(), listed.GetProperty("guardPercent").GetInt32(), listed.GetProperty("guardRecords").GetInt64()));

            var flow = await JsonAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/removals/flows/{removalPipeline:D}?partition={Partition}");
            Assert.Equal("The wellbores the source no longer holds.", flow.GetProperty("description").GetString());
            var removal = Assert.Single(flow.GetProperty("removals").EnumerateArray().ToList());
            Assert.Equal(("wellbores", deliveryName, flowId, deliveryPipeline, 3L), (
                removal.GetProperty("name").GetString(), removal.GetProperty("flow").GetString(), removal.GetProperty("ledgerFlowId").GetGuid(),
                removal.GetProperty("deliveryPipelineId").GetGuid(), removal.GetProperty("awaitingApproval").GetInt64()));
            var deletions = removal.GetProperty("deletions");
            Assert.Equal((2L, 3L, 250L), (deletions.GetProperty(SourceRemovals.Pending).GetInt64(), deletions.GetProperty(SourceRemovals.AwaitingApproval).GetInt64(), deletions.GetProperty(SourceRemovals.Done).GetInt64()));
            Assert.Equal(("files", "csv", "../keys/wellbores"), (removal.GetProperty("keys").GetProperty("reads").GetString(), removal.GetProperty("keys").GetProperty("type").GetString(), removal.GetProperty("keys").GetProperty("where").GetString()));
            Assert.Equal(("table", "mssql", "deleted_at"), (removal.GetProperty("removed").GetProperty("reads").GetString(), removal.GetProperty("removed").GetProperty("type").GetString(), removal.GetProperty("removed").GetProperty("removedAt").GetProperty("name").GetString()));
            Assert.False(removal.GetProperty("removed").TryGetProperty("connection", out _));
            Assert.Equal((run, SourceRemovals.Guarded, 3L), (removal.GetProperty("lastRun").GetProperty("removalRunId").GetInt64(), removal.GetProperty("lastRun").GetProperty("status").GetString(), removal.GetProperty("lastRun").GetProperty("tally").GetProperty("detected").GetInt64()));

            // A partition the flow does not remove in is answered with the flow as declared and why, not refused.
            var elsewhere = await ReadAsync(client, reader, $"/api/v1/delivery/removals/flows/{removalPipeline:D}", "test");
            Assert.Contains("does not remove in partition 'test'", elsewhere.GetProperty("problem").GetString(), StringComparison.Ordinal);
            Assert.False(Assert.Single(elsewhere.GetProperty("removals").EnumerateArray().ToList()).TryGetProperty("ledgerFlowId", out _));
            await StatusAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/removals/flows/{deliveryPipeline:D}", HttpStatusCode.NotFound);

            // The runs, of the flow in the partition and of the ledger, and one by its number.
            var runs = await JsonAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/removals/flows/{removalPipeline:D}/runs?partition={Partition}&entry=wellbores");
            Assert.Equal(run, Assert.Single(runs.EnumerateArray().ToList()).GetProperty("removalRunId").GetInt64());
            await StatusAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/removals/flows/{removalPipeline:D}/runs?partition={Partition}&entry=nope", HttpStatusCode.NotFound);
            Assert.Equal(run, Assert.Single((await JsonAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/removals/ledgers/{flowId:D}/runs")).EnumerateArray().ToList()).GetProperty("removalRunId").GetInt64());
            var one = await JsonAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/removals/{Partition}/runs/{run.ToString(CultureInfo.InvariantCulture)}");
            Assert.Equal((removalName, "wellbores", 2455L), (one.GetProperty("flowName").GetString(), one.GetProperty("entry").GetString(), one.GetProperty("tally").GetProperty("compared").GetInt64()));
            await StatusAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/removals/{Partition}/runs/987654321", HttpStatusCode.NotFound);
            await StatusAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/removals/not%20a%20partition/runs/1", HttpStatusCode.BadRequest);

            // The deletions a page at a time in key order: every page carries the counts, and the next key until the last.
            var seen = new List<Guid>();
            string? after = null;
            do
            {
                var page = await JsonAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/removals/ledgers/{flowId:D}/keys?limit=100{(after is null ? string.Empty : "&after=" + after)}");
                Assert.Equal(250L, page.GetProperty("counts").GetProperty(SourceRemovals.Done).GetInt64());
                var items = page.GetProperty("keys").EnumerateArray().ToList();
                Assert.InRange(items.Count, 1, 100);
                seen.AddRange(items.Select(k => k.GetProperty("deliveryKey").GetGuid()));
                after = page.TryGetProperty("next", out var next) ? next.GetGuid().ToString("D") : null;
            }
            while (after is not null);
            Assert.Equal(keys.Count, seen.Count);
            Assert.Equal(seen.Count, seen.Distinct().Count());
            Assert.Equal(keys.Select(k => k.Value).ToHashSet(), seen.ToHashSet());

            var pending = await JsonAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/removals/ledgers/{flowId:D}/keys?state={SourceRemovals.Pending}");
            Assert.Equal(2, pending.GetProperty("keys").GetArrayLength());
            Assert.False(pending.TryGetProperty("next", out _));
            var awaiting = (await JsonAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/removals/ledgers/{flowId:D}/keys?state=awaiting-approval")).GetProperty("keys").EnumerateArray().ToList();
            Assert.Contains(awaiting, k => k.GetProperty("deliveryKey").GetGuid() == held.Value && k.GetProperty("recordStatus").GetString() == "delivered");
            Assert.Contains(awaiting, k => !k.TryGetProperty("recordStatus", out _));
            await StatusAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/removals/ledgers/{flowId:D}/keys?state=lost", HttpStatusCode.BadRequest);

            // A record's page says its key was removed, by which flow and why.
            var detail = await JsonAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/records/{flowId:D}/{gone.Value:D}");
            var removedKey = detail.GetProperty("removedKey");
            Assert.Equal((SourceRemovals.Done, SourceRemovals.Listed, removalName, "deleted"), (removedKey.GetProperty("state").GetString(), removedKey.GetProperty("reason").GetString(), removedKey.GetProperty("removalFlow").GetString(), removedKey.GetProperty("recordStatus").GetString()));
            Assert.Equal(JsonValueKind.Null, (await JsonAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/records/{flowId:D}/{live.Value:D}")).GetProperty("removedKey").ValueKind);

            // An approval names the removal, the count shown and the partition; each refusal says what is wrong, and nothing is queued.
            var approve = $"/api/v1/delivery/removals/flows/{removalPipeline:D}/approve";
            await RefusedAsync(client, operate, approve, """{"expected":3,"confirm":"dev"}""", HttpStatusCode.BadRequest, "Removal required");
            await RefusedAsync(client, operate, approve, """{"entry":"wellbores","confirm":"dev"}""", HttpStatusCode.BadRequest, "Count required");
            await RefusedAsync(client, operate, approve, """{"entry":"wellbores","expected":3}""", HttpStatusCode.BadRequest, "Confirmation required");
            await RefusedAsync(client, operate, approve, """{"entry":"wellbores","expected":3,"confirm":"test"}""", HttpStatusCode.Conflict, "does not remove in partition 'test'");
            await RefusedAsync(client, operate, approve, """{"entry":"logs","expected":3,"confirm":"dev"}""", HttpStatusCode.NotFound, "declares no removal named 'logs'");
            await RefusedAsync(client, operate, approve, """{"entry":"wellbores","expected":2,"confirm":"dev"}""", HttpStatusCode.Conflict, "has 3 removed key(s) waiting for approval");

            var accepted = await JsonAsync(client, operate, HttpMethod.Post, approve, """{"entry":"WELLBORES","expected":3,"confirm":"DEV"}""", HttpStatusCode.Accepted);
            var queued = accepted.GetProperty("runId").GetGuid();
            Assert.Equal((Partition, "wellbores", 3L), (accepted.GetProperty("partition").GetString(), accepted.GetProperty("entry").GetString(), accepted.GetProperty("expected").GetInt64()));
            await using (var db = CatalogDatabase.Create(cs))
            {
                var queuedRun = await db.Runs.SingleAsync(r => r.RunId == queued);
                Assert.Equal((removalPipeline, DeliveryOperations.Remove, RunStatuses.Queued), (queuedRun.PipelineId, queuedRun.Operation, queuedRun.Status));
                var payload = DeliveryRunPayload.Parse(queuedRun.Payload);
                Assert.Equal(["wellbores"], payload.Entries);
                Assert.Equal((3L, Partition), (payload.Approve!.Expected, payload.Confirm));
                Assert.Contains(Partition, queuedRun.ValuesJson, StringComparison.Ordinal);
            }

            // While that run is queued the flow is busy: a second approval waits.
            await RefusedAsync(client, operate, approve, """{"entry":"wellbores","expected":3,"confirm":"dev"}""", HttpStatusCode.Conflict, "is queued or running");

            // A record's deletion lifted: it goes, and the record is asked to be planned again; one never removed answers 409.
            var lifted = await JsonAsync(client, operate, HttpMethod.Post, $"/api/v1/delivery/records/{flowId:D}/{gone.Value:D}/lift-removal", "{}");
            Assert.Equal(1, lifted.GetProperty("lifted").GetInt32());
            Assert.NotNull((await ledger.GetRecordAsync(flowId, gone))!.PlanRequestedUtc);
            Assert.Null(await ledger.GetRemovedKeyAsync(flowId, gone));
            await RefusedAsync(client, operate, $"/api/v1/delivery/records/{flowId:D}/{gone.Value:D}/lift-removal", "{}", HttpStatusCode.Conflict, "Not removed by its source");

            // Many lifted at once in one activity, a key the ledger keeps out for no removal passed over; too many is refused.
            var lift = $"/api/v1/delivery/flows/{deliveryPipeline:D}/lift-removal";
            var many = await JsonAsync(client, operate, HttpMethod.Post, lift, JsonSerializer.Serialize(new { keys = new[] { keys[10].Value, keys[11].Value, live.Value } }));
            Assert.Equal(2, many.GetProperty("lifted").GetInt32());
            await RefusedAsync(client, operate, lift, """{"keys":[]}""", HttpStatusCode.BadRequest, "name from 1 to 1000 records");
            await RefusedAsync(client, operate, lift, JsonSerializer.Serialize(new { keys = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()) }), HttpStatusCode.BadRequest, "name from 1 to 1000 records");
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = flowId, Kind = SourceRemovals.LiftActivityKind, Max = 10 }), a => a.ParametersJson!.Contains("\"count\":3", StringComparison.Ordinal));
            Assert.StartsWith("user:", activity.Actor, StringComparison.Ordinal);
        }
        finally
        {
            await CleanAsync(cs, repoId, flowId);
        }
    }

    private static async Task SeedCatalogAsync(
        string cs, Guid repoId, Guid deliveryPipeline, string deliveryName, Guid removalPipeline, string removalName, string removalYaml, DateTime now)
    {
        var deliveryYaml = $$"""
            flowType: delivery
            name: {{deliveryName}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.Wellbore, key: [facility_name] }
              work: ../.work/removals
            render:
              mapping: Wellbore@1.0.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: storage
              headers: { data-partition-id: {{Partition}} }
            """;
        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId, Name = "cp-removals-" + repoId.ToString("N")[..8], RemoteUrl = "https://example/cp-removals.git",
            RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
        });
        db.Pipelines.Add(Pipeline(deliveryPipeline, repoId, deliveryName, "delivery", deliveryYaml, now));
        db.Pipelines.Add(Pipeline(removalPipeline, repoId, removalName, RemovalFlowDefinition.FlowTypeName, removalYaml, now));
        await db.SaveChangesAsync();
    }

    /// <summary>The read model's row of the delivery flow, as a repository sync writes it: what a removal finds its ledger by.</summary>
    private static async Task SeedInterfaceAsync(string cs, Guid repoId, string deliveryName, Guid flowId, DateTime now)
    {
        await using var osdu = SampleEstate.Context(cs);
        osdu.DeliveryInterfaces.Add(new DeliveryInterface
        {
            Id = Guid.NewGuid(), RepoId = repoId, FlowName = deliveryName, Interface = string.Empty, Partition = string.Empty, TargetPartition = Partition,
            Ordinal = 0, LedgerFlowId = flowId, LedgerName = deliveryName, Route = "storage", MappingReference = "Wellbore@1.0.0",
            Kind = "osdu:wks:master-data--Wellbore:1.3.0", RecordObject = "OsduData.arc.Wellbore", RelativePath = "flows/" + deliveryName + ".yaml",
            Active = true, FirstSeenUtc = now, LastSeenUtc = now,
        });
        await osdu.SaveChangesAsync();
    }

    /// <summary>A record of the ledger delivered at an id of its own.</summary>
    private static async Task<DeliveryKey> DeliveredAsync(string cs, OsduLedger ledger, Guid flowId, string sourceKey)
    {
        var key = DeliveryKey.Derive("removal-api-tests", [sourceKey]);
        var targetId = $"{Partition}:master-data--Wellbore:{sourceKey}";
        await ledger.UpsertPendingAsync(flowId, [new RecordState
        {
            DeliveryKey = key, FlowId = flowId, SourceKey = sourceKey, MappingName = "Wellbore", TargetId = targetId, LastSubmissionId = Guid.NewGuid(),
            PendingDocumentRef = "0:0:10", PendingRenderContext = "{}", PendingMetadataHash = "mh", PendingMetadata = true,
        }]);
        await using var osdu = SampleEstate.Context(cs);
        await osdu.DeliveryRecords
            .Where(r => r.FlowId == flowId && r.DeliveryKey == key.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "delivered").SetProperty(r => r.TargetVersion, 7L).SetProperty(r => r.ClaimedTargetId, targetId)
                .SetProperty(r => r.PendingDocumentRef, (string?)null));
        return key;
    }

    /// <summary>
    /// The deletions a removal run leaves, written as its comparison and take-out write them: 3 waiting for approval (the held
    /// record's key first), 2 pending, and 250 removed (the gone record's key first). Returns every key, in no order.
    /// </summary>
    private static async Task<List<DeliveryKey>> SeedDeletionsAsync(string cs, Guid flowId, string removalName, long run, DeliveryKey held, DeliveryKey gone, DateTime now)
    {
        await using var osdu = SampleEstate.Context(cs);
        var partitionId = await osdu.DeliveryLedgers.Where(l => l.FlowId == flowId).Select(l => l.PartitionId).SingleAsync();
        var keys = new List<DeliveryKey>();
        void Add(DeliveryKey key, string state, string reason)
        {
            keys.Add(key);
            var done = state == SourceRemovals.Done;
            osdu.DeliveryRemovedKeys.Add(new DeliveryRemovedKey
            {
                PartitionId = partitionId, FlowId = flowId, DeliveryKey = key.Value, SourceKeyJson = "[\"" + key.Value.ToString("N") + "\"]", Reason = reason,
                RemovedUtc = now.AddHours(-1), FileName = reason == SourceRemovals.Listed ? "WellDB.dbo.DeletedWellbore" : null, RowNumber = reason == SourceRemovals.Listed ? 3 : null,
                State = state, Outcome = done ? SourceRemovals.Removed : null, Tries = done ? 1 : 0, RemovalFlow = removalName, Entry = "wellbores",
                DetectedRunId = run, DoneRunId = done ? run : null, CreatedUtc = now.AddMinutes(-9), DoneUtc = done ? now.AddMinutes(-8) : null, UpdatedUtc = now.AddMinutes(-8),
            });
        }

        Add(held, SourceRemovals.AwaitingApproval, SourceRemovals.Absent);
        Add(DeliveryKey.Derive("removal-api-tests", ["awaiting-1"]), SourceRemovals.AwaitingApproval, SourceRemovals.Absent);
        Add(DeliveryKey.Derive("removal-api-tests", ["awaiting-2"]), SourceRemovals.AwaitingApproval, SourceRemovals.Absent);
        Add(DeliveryKey.Derive("removal-api-tests", ["pending-1"]), SourceRemovals.Pending, SourceRemovals.Absent);
        Add(DeliveryKey.Derive("removal-api-tests", ["pending-2"]), SourceRemovals.Pending, SourceRemovals.Listed);
        Add(gone, SourceRemovals.Done, SourceRemovals.Listed);
        for (var i = 1; i < 250; i++)
        {
            Add(DeliveryKey.Derive("removal-api-tests", ["removed-" + i.ToString(CultureInfo.InvariantCulture)]), SourceRemovals.Done, SourceRemovals.Absent);
        }

        await osdu.SaveChangesAsync();
        return keys;
    }

    private static async Task CleanAsync(string cs, Guid repoId, Guid flowId)
    {
        await using (var osdu = SampleEstate.Context(cs))
        {
            await osdu.DeliveryRemovedKeys.Where(k => k.FlowId == flowId).ExecuteDeleteAsync();
            var runs = osdu.DeliveryRemovalRuns.Where(r => r.LedgerFlowId == flowId).Select(r => r.RemovalRunId);
            await osdu.DeliveryRemovalKeys.Where(k => runs.Contains(k.RemovalRunId)).ExecuteDeleteAsync();
            await osdu.DeliveryRemovalRuns.Where(r => r.LedgerFlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryActivityRecords.Where(l => l.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryActivities.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryAttempts.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => i.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
        }

        await using (var db = CatalogDatabase.Create(cs))
        {
            await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
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

    private static ControlPlaneAppFactory Factory(string cs) => new ControlPlaneAppFactory()
        .WithCatalog(cs)
        .WithModules(new DeliveryControlPlaneModule())
        .WithSetting("ControlPlane:Worker:Enabled", "false")
        .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    private static async Task<JsonElement> JsonAsync(
        HttpClient client, string token, HttpMethod method, string path, string? body = null, HttpStatusCode expected = HttpStatusCode.OK, string? workbenchPartition = null)
    {
        using var response = await SendAsync(client, token, method, path, body, workbenchPartition);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {path} {body} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A GET as the workbench sends it, with the partition picked in its title bar.</summary>
    private static Task<JsonElement> ReadAsync(HttpClient client, string token, string path, string workbenchPartition)
        => JsonAsync(client, token, HttpMethod.Get, path, null, HttpStatusCode.OK, workbenchPartition);

    private static async Task StatusAsync(HttpClient client, string token, HttpMethod method, string path, HttpStatusCode expected, string? body = null)
    {
        using var response = await SendAsync(client, token, method, path, body, null);
        Assert.True(response.StatusCode == expected, $"{method} {path} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task RefusedAsync(HttpClient client, string token, string path, string body, HttpStatusCode status, string says)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, path, body, null);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"POST {path} answered {(int)response.StatusCode}, not {(int)status}: {text}");
        Assert.Contains(says, text, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, string? body, string? workbenchPartition)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (workbenchPartition is not null)
        {
            request.Headers.Add(WorkbenchPartition.Header, workbenchPartition);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client, string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
