using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A flow that declares interfaces as the API serves it (osdu/docs/reference/flow/interfaces.md, The interface read
/// model; osdu/docs/reference/concepts/api.md): the control plane describes a pipeline synced before the read model of
/// interfaces existed once it starts, lists the pipeline's interfaces with their routes and counts, adds a source's
/// counts up, asks which interface a request is about, and leads a record to its pipeline and interface, the interface
/// travelling with every operation run on it.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryInterfacesApiTests
{
    [Fact]
    public async Task A_source_s_interfaces_are_listed_counted_and_named_on_every_request_about_one()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-source-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-interfaces-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var logsLedger = FlowId.Of(flowName + "/welllogs");
        var key = new DeliveryKey(Guid.NewGuid());
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            parameters:
              logSource: { required: true }
            source:
              connection: ${env:OSDU_DATA_DB}
              work: ../.work/{logSource}
            render:
              parameters:
                dataPartition: dev
            target:
              endpoint: ${env:OSDU_URL}
              headers: { data-partition-id: dev }
              protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
            interfaces:
              wellbores:
                record: { object: OsduData.silver.Wellbore, key: [facility_name] }
                mapping: Wellbore@1.0.0
              welllogs:
                record: { object: OsduData.silver.WellLog, key: [source_project, log_id] }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: payload_hash }
                mapping: WellLog@1.4.0
            """;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-interfaces-" + suffix, RemoteUrl = "https://example/cp-interfaces.git",
                RootPath = SampleEstate.TemplateRoot, FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = pipelineId,
                RepoId = repoId,
                Name = flowName,
                Kind = "delivery",
                RelativePath = "flows/" + flowName + ".yaml",
                ContentHash = new string('0', 64),
                Yaml = yaml,
                DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{flowName}}","flowKind":"delivery"}"""),
                Active = true,
                Wave = 0,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(logsLedger);
        await ledger.UpsertPendingAsync(logsLedger,
        [
            new RecordState
            {
                DeliveryKey = key,
                FlowId = logsLedger,
                SourceKey = "NO_15_9/L-1001",
                Label = "NO_15_9/L-1001",
                MappingName = "WellLog",
                Status = RecordStatus.Pending,
                PendingDocumentRef = "1:0:10",
                PendingMetadata = true,
            },
        ]);

        var operations = new RecordedOperations();
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithServices(services => services.AddSingleton(operations.Registry()));
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The pipeline was synced before interfaces were described; the control plane describes it once it starts.
            await WaitForInterfacesAsync(cs, repoId, 2);

            using (var interfaces = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/interfaces"))
            {
                Assert.Equal(HttpStatusCode.OK, interfaces.StatusCode);
                var items = JsonDocument.Parse(await interfaces.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
                Assert.Equal(["wellbores", "welllogs"], items.Select(i => i.GetProperty("interface").GetString()));
                Assert.Equal("storage", items[0].GetProperty("route").GetString());
                Assert.Equal("ddms", items[1].GetProperty("route").GetString());
                Assert.Equal(logsLedger, items[1].GetProperty("flowId").GetGuid());
                Assert.Equal(flowName + "/welllogs", items[1].GetProperty("ledger").GetString());
                Assert.Empty(items[1].GetProperty("after").EnumerateArray());
                Assert.Equal(1, items[1].GetProperty("stats").GetProperty("pending").GetInt64());
                Assert.Equal(0, items[0].GetProperty("stats").GetProperty("total").GetInt64());
                Assert.Contains("declares bulk", items[1].GetProperty("routeReason").GetString(), StringComparison.Ordinal);

                // The repository's mappings are not synced yet, so nothing but after: orders the interfaces, and the listing says why.
                Assert.All(items, i => Assert.Equal(1, i.GetProperty("wave").GetInt32()));
                Assert.Empty(items[1].GetProperty("waitsFor").EnumerateArray());
                Assert.Contains("Mapping Wellbore@1.0.0 of interface 'wellbores' is not among the repository's valid mappings", items[0].GetProperty("orderProblem").GetString(), StringComparison.Ordinal);
            }

            // Once the mappings are synced with the template folder holding their templates, the well logs wait for the
            // wellbores they refer to.
            await SampleEstate.HoldTemplatesAsync(cs, repoId, now);
            await using (var osdu = SampleEstate.Context(cs))
            {
                var loader = new DeliveryDocumentLoader();
                foreach (var reference in new[] { "Wellbore@1.0.0", "WellLog@1.4.0" })
                {
                    var yamlText = SampleEstate.MappingYaml(reference);
                    var mapping = loader.ParseMapping(yamlText, $"mappings/{reference}.yaml");
                    osdu.DeliveryMappings.Add(new DeliveryMapping
                    {
                        Id = FlowIdentity.FromName($"delivery-mapping/{repoId:N}/{reference}"),
                        RepoId = repoId,
                        Reference = reference,
                        Name = mapping.Name,
                        Version = mapping.Version,
                        Kind = mapping.Kind,
                        TemplateVersion = mapping.Template.Version,
                        RelativePath = $"mappings/{reference}.yaml",
                        ContentHash = new string('0', 64),
                        Yaml = yamlText,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    });
                }

                await osdu.SaveChangesAsync();
            }

            using (var ordered = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/interfaces"))
            {
                Assert.Equal(HttpStatusCode.OK, ordered.StatusCode);
                var items = JsonDocument.Parse(await ordered.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
                Assert.Equal((1, 2), (items[0].GetProperty("wave").GetInt32(), items[1].GetProperty("wave").GetInt32()));
                var wait = Assert.Single(items[1].GetProperty("waitsFor").EnumerateArray().ToList());
                Assert.Equal(("wellbores", "schema"), (wait.GetProperty("interface").GetString(), wait.GetProperty("origin").GetString()));
                Assert.StartsWith("osdu.data.WellboreID refers to master-data--Wellbore, which wellbores delivers", wait.GetProperty("why").GetString(), StringComparison.Ordinal);
                Assert.All(items, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("orderProblem").ValueKind));
            }

            // The source as a whole adds its interfaces up; one interface is asked for by name.
            var total = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/stats");
            Assert.Equal((2, 1L, Guid.Empty), (total.GetProperty("interfaces").GetInt32(), total.GetProperty("total").GetInt64(), total.GetProperty("flowId").GetGuid()));
            Assert.Equal(JsonValueKind.Null, total.GetProperty("interface").ValueKind);
            var logs = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/stats?interface=welllogs");
            Assert.Equal((logsLedger, "welllogs", 1L), (logs.GetProperty("flowId").GetGuid(), logs.GetProperty("interface").GetString(), logs.GetProperty("pending").GetInt64()));

            // A listing of a source's records is about one interface, and says so when it is not told which.
            using (var nameless = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/records"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);
                Assert.Contains("delivers 2 interfaces (wellbores, welllogs); name the one this request is about with ?interface=", await nameless.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var unknown = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/records?interface=cores"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }

            // The audit trail of a source is read the same way: one interface at a time, named the same way.
            using (var namelessActivity = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/activities?pipelineId={pipelineId:D}"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, namelessActivity.StatusCode);
                Assert.Contains("name the one this request is about with ?interface=", await namelessActivity.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var unknownActivity = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/activities?pipelineId={pipelineId:D}&interface=cores"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknownActivity.StatusCode);
            }

            var activities = await JsonAsync(client, token, $"/api/v1/delivery/activities?pipelineId={pipelineId:D}&interface=welllogs");
            Assert.Equal(0, activities.GetProperty("items").GetArrayLength());

            var records = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/records?interface=welllogs");
            Assert.Equal(key.Value, Assert.Single(records.GetProperty("items").EnumerateArray().ToList()).GetProperty("deliveryKey").GetGuid());
            var target = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/target?interface=welllogs");
            Assert.Equal(("welllogs", "ddms"), (target.GetProperty("interface").GetString(), target.GetProperty("protocol").GetString()));

            // The well logs' mapping is synced, so the target names the collection of the kind it renders.
            Assert.Equal(
                "work-product-component--WellLog records go to the welllogs collection of the DDMS 'wellbore' (/api/os-wellbore-ddms).",
                target.GetProperty("ddms").GetString());
            Assert.Equal("/api/os-wellbore-ddms/ddms/v3/welllogs/{id}", target.GetProperty("recordPath").GetString());
            Assert.Equal("DELETE", target.GetProperty("recordMethod").GetString());
            Assert.Equal("/api/os-wellbore-ddms/ddms/v3/welllogs/{id}?purge=true", target.GetProperty("everythingPath").GetString());
            Assert.Equal("/api/storage/v2/records/{id}/versions", target.GetProperty("historyPath").GetString());

            // Restoring the previous version goes past the DDMS to the storage service, which keeps a well log's versions.
            Assert.Equal(
                "GET /api/storage/v2/records/{id}/{version}, then PUT /api/storage/v2/records (the storage service, past the DDMS)",
                target.GetProperty("previousPath").GetString());
            Assert.Equal(JsonValueKind.Null, target.GetProperty("previousRefusal").ValueKind);
            var storageTarget = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/target?interface=wellbores");
            Assert.Equal(JsonValueKind.Null, storageTarget.GetProperty("ddms").ValueKind);
            Assert.Equal("POST", storageTarget.GetProperty("recordMethod").GetString());

            // A record leads to its pipeline and interface, and an operation run on it names the interface it acts through.
            var record = await JsonAsync(client, token, $"/api/v1/delivery/records/{logsLedger:D}/{key.Value:D}");
            Assert.Equal((pipelineId, flowName, "welllogs"), (record.GetProperty("pipelineId").GetGuid(), record.GetProperty("flowName").GetString(), record.GetProperty("interface").GetString()));

            // It names the columns of its key tuple as its own interface declares them, not another interface's of the source.
            Assert.Equal(["source_project", "log_id"], record.GetProperty("keyColumns").EnumerateArray().Select(c => c.GetString()));
            using (var read = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/records/{logsLedger:D}/{key.Value:D}/read"))
            {
                Assert.True(read.StatusCode == HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
                var ran = operations.Last();
                Assert.Equal((flowName, "welllogs"), (ran.SourceRef, ran.Argument("interface")));
            }

            // A record is deleted from the ledger only by a removal that takes it out of OSDU, never with one that leaves it there.
            foreach (var scope in new[] { "history", "previous" })
            {
                using var refused = await SendJsonAsync(
                    client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/records/remove?interface=welllogs",
                    new { scope, keys = new[] { key.Value }, purgeLedger = true });
                var body = await refused.Content.ReadAsStringAsync();
                Assert.True(refused.StatusCode == HttpStatusCode.BadRequest, body);
                Assert.Contains("only with a removal that takes it out of OSDU", body, StringComparison.Ordinal);
            }

            using (var refusedOne = await SendJsonAsync(
                client, token, HttpMethod.Post, $"/api/v1/delivery/records/{logsLedger:D}/{key.Value:D}/delete", new { scope = "history", purgeLedger = true }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, refusedOne.StatusCode);
            }

            Assert.NotNull(await ledger.GetRecordAsync(logsLedger, key));

            // A removal is queued as a remove run of the pipeline, listed with its runs, naming the interface it works in, its
            // records and how much of each goes. The worker is off here, so neither run below changes the record.
            using (var queued = await SendJsonAsync(
                client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/records/remove?interface=welllogs",
                new { scope = "record", keys = new[] { key.Value }, purgeLedger = true }))
            {
                var body = await queued.Content.ReadAsStringAsync();
                Assert.True(queued.StatusCode == HttpStatusCode.Accepted, body);
                var accepted = JsonDocument.Parse(body).RootElement;
                var runId = accepted.GetProperty("runId").GetGuid();
                Assert.Equal(
                    ("record", 1, true),
                    (accepted.GetProperty("scope").GetString(), accepted.GetProperty("records").GetInt32(), accepted.GetProperty("purgeLedger").GetBoolean()));
                Assert.Equal($"/api/v1/runs/{runId:D}", queued.Headers.Location?.OriginalString);
                await using var db = CatalogDatabase.Create(cs);
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                Assert.Equal((DeliveryOperations.Remove, pipelineId, RunStatuses.Queued), (run.Operation, run.PipelineId, run.Status));
                var payload = DeliveryRunPayload.Parse(run.Payload);
                Assert.Equal(("welllogs", RemovalChoice.Record, true), (payload.Interface, payload.Scope, payload.PurgeLedger));
                Assert.Equal([key.Value], payload.RecordKeys);
            }

            // By filter, the listing travels to the run whole, which resolves it when it removes the records.
            using (var byFilter = await SendJsonAsync(
                client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/records/remove?interface=welllogs",
                new { scope = "everything", filter = new { status = "pending" }, expected = 1 }))
            {
                var body = await byFilter.Content.ReadAsStringAsync();
                Assert.True(byFilter.StatusCode == HttpStatusCode.Accepted, body);
                var runId = JsonDocument.Parse(body).RootElement.GetProperty("runId").GetGuid();
                await using var db = CatalogDatabase.Create(cs);
                var payload = DeliveryRunPayload.Parse((await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId)).Payload);
                Assert.Equal((RemovalChoice.Everything, RecordStatus.Pending), (payload.Scope, payload.Filter?.Status));
                Assert.Empty(payload.RecordKeys);
            }

            // A record OSDU may still hold is not deleted from the ledger alone, and the preview counts none of it as removed.
            using (var notRemoved = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/records/{logsLedger:D}/{key.Value:D}/purge"))
            {
                Assert.Equal(HttpStatusCode.Conflict, notRemoved.StatusCode);
                Assert.Equal("Not removed from OSDU", JsonDocument.Parse(await notRemoved.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString());
            }

            var before = await PreviewAsync(client, token, pipelineId, key.Value);
            Assert.Equal((1, 0), (before.GetProperty("records").GetInt32(), before.GetProperty("removed").GetInt32()));

            // Once removed from OSDU, the preview counts it, and deleting it from the ledger alone answers what it did.
            await ledger.MarkRemovedAsync(logsLedger, [key], SqlFlow.Delivery.Protocols.RemovalScope.Record, "user:alice", now);
            Assert.Equal(1, (await PreviewAsync(client, token, pipelineId, key.Value)).GetProperty("removed").GetInt32());
            using (var deleted = await SendJsonAsync(
                client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/records/purge?interface=welllogs", new { keys = new[] { key.Value } }))
            {
                var body = await deleted.Content.ReadAsStringAsync();
                Assert.True(deleted.StatusCode == HttpStatusCode.OK, body);
                var result = JsonDocument.Parse(body).RootElement;
                Assert.Equal((1, 1, 0), (result.GetProperty("selected").GetInt32(), result.GetProperty("purged").GetInt32(), result.GetProperty("left").GetInt32()));
            }

            // A record deleted from the ledger answers who deleted it and when, with the line the ledger keeps of it.
            using (var purged = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/records/{logsLedger:D}/{key.Value:D}"))
            {
                var body = await purged.Content.ReadAsStringAsync();
                Assert.True(purged.StatusCode == HttpStatusCode.NotFound, body);
                var problem = JsonDocument.Parse(body).RootElement;
                Assert.Equal("Deleted from the ledger", problem.GetProperty("title").GetString());
                Assert.Contains("deleted from the ledger by ", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
                Assert.Equal("NO_15_9/L-1001", problem.GetProperty("purged").GetProperty("sourceKey").GetString());
            }

            // A key the ledger never held is plainly not found.
            using (var unknown = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/records/{logsLedger:D}/{Guid.NewGuid():D}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
                Assert.Equal("Not found", JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()).RootElement.GetProperty("title").GetString());
            }
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryPurgedRecords.Where(p => p.FlowId == logsLedger).ExecuteDeleteAsync();
                await osdu.DeliveryAttempts.Where(a => a.FlowId == logsLedger).ExecuteDeleteAsync();
                await osdu.DeliveryRecords.Where(r => r.FlowId == logsLedger).ExecuteDeleteAsync();
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryMappings.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryRepositoryTemplates.Where(t => t.RepoId == repoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.ComputeTasks.Where(t => t.SourceRef == flowName).ExecuteDeleteAsync();
                await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
                await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    private static async Task WaitForInterfacesAsync(string cs, Guid repoId, int expected)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            await using var osdu = SampleEstate.Context(cs);
            if (await osdu.DeliveryInterfaces.CountAsync(i => i.RepoId == repoId) >= expected)
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"The control plane did not describe the {expected} interface(s) of repository {repoId:D} within 30 seconds of starting.");
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> PreviewAsync(HttpClient client, string token, Guid pipelineId, Guid key)
    {
        using var response = await SendJsonAsync(
            client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/records/remove/preview?interface=welllogs", new { scope = "record", keys = new[] { key } });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, string token, HttpMethod method, string path, object body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
