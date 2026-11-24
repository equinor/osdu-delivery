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
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Many records redelivered from the API (osdu/docs/reference/concepts/removal-and-reversal.md, Redelivering records):
/// brought up to date, which renders them again and sends only what renders differently, or sent again whatever their
/// hashes say; named by keys, by a filter with the count the operator was shown, or as every delivered record; and the
/// plan run that says beforehand what bringing them up to date would send. Every record asked names the request in its
/// history.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryRedeliverApiTests
{
    [Fact]
    public async Task Many_records_are_brought_up_to_date_or_sent_again_by_keys_by_filter_or_all_of_them()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-redeliver-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-redeliver-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var flowId = FlowId.Of(flowName);
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.Wellbore, key: [facility_name] }
              work: ../.work/redeliver
            render:
              mapping: Wellbore@1.0.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: storage
              headers: { data-partition-id: dev }
            """;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-redeliver-" + suffix, RemoteUrl = "https://example/cp-redeliver.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
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
        await ledger.RegisterAsync(flowId, TestLedgers.Partition, flowName);
        var delivered = Enumerable.Range(1, 3).Select(i => DeliveryKey.Derive("redeliver", [$"W-{suffix}-{i}"]).Value).ToList();
        var held = DeliveryKey.Derive("redeliver", [$"H-{suffix}"]).Value;
        await using (var osdu = SampleEstate.Context(cs))
        {
            var partition = await osdu.DeliveryLedgers.AsNoTracking().Where(l => l.FlowId == flowId).Select(l => l.PartitionId).SingleAsync();
            foreach (var (key, i) in delivered.Select((k, i) => (k, i)))
            {
                osdu.DeliveryRecords.Add(new DeliveryRecord
                {
                    PartitionId = partition, FlowId = flowId, DeliveryKey = key, SourceKey = $"W-{suffix}-{i}", SourceKeyJson = $"[\"W-{suffix}-{i}\"]",
                    MappingName = "Wellbore", Status = "delivered", TargetId = $"dev:master-data--Wellbore:{key:N}", TargetVersion = 1,
                    MetadataHash = "hash-" + i, SourceFingerprint = "fingerprint-" + i, SourceModifiedUtc = now, CreatedUtc = now, UpdatedUtc = now,
                });
            }

            osdu.DeliveryRecords.Add(new DeliveryRecord
            {
                PartitionId = partition, FlowId = flowId, DeliveryKey = held, SourceKey = $"H-{suffix}", SourceKeyJson = $"[\"H-{suffix}\"]",
                MappingName = "Wellbore", Status = "held", SourceFingerprint = "fingerprint-held", CreatedUtc = now, UpdatedUtc = now,
            });
            await osdu.SaveChangesAsync();
        }

        async Task<DeliveryRecord> RecordAsync(Guid key)
        {
            await using var osdu = SampleEstate.Context(cs);
            return await osdu.DeliveryRecords.AsNoTracking().SingleAsync(r => r.FlowId == flowId && r.DeliveryKey == key);
        }

        async Task<CatalogRun> RunAsync(Guid runId)
        {
            await using var db = CatalogDatabase.Create(cs);
            return await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
        }

        async Task<int> RunCountAsync()
        {
            await using var db = CatalogDatabase.Create(cs);
            return await db.Runs.AsNoTracking().CountAsync(r => r.PipelineId == pipelineId);
        }

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var flow = $"/api/v1/delivery/flows/{pipelineId:D}";

            // The card of the ledger names the parts its route can send again: a storage route sends the record alone.
            var stats = await JsonAsync(client, token, HttpMethod.Get, $"{flow}/stats");
            Assert.Equal(["all", "record", "metadata"], stats.GetProperty("redeliverParts").EnumerateArray().Select(p => p.GetString()!).ToList());

            // Brought up to date by keys, without a run: the record OSDU holds keeps its hashes and the business date of its
            // row, and forgets the fingerprint it was planned under; the held one is left as it is, and the request is on the
            // record's history.
            var asked = await JsonAsync(client, token, HttpMethod.Post, $"{flow}/rerender", Keys(false, delivered[0], held));
            Assert.Equal((1, JsonValueKind.Null), (asked.GetProperty("marked").GetInt32(), asked.GetProperty("runId").ValueKind));
            var first = await RecordAsync(delivered[0]);
            Assert.Equal(("hash-0", (string?)null, (DateTime?)now), (first.MetadataHash, first.SourceFingerprint, first.SourceModifiedUtc));
            Assert.NotNull(first.PlanRequestedUtc);
            var untouched = await RecordAsync(held);
            Assert.Equal(("fingerprint-held", (DateTime?)null), (untouched.SourceFingerprint, untouched.PlanRequestedUtc));
            var history = await JsonAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/records/{flowId:D}/{delivered[0]:D}/activities");
            Assert.Equal("rerender", Assert.Single(history.EnumerateArray().ToList()).GetProperty("kind").GetString());

            // Named either way, never both; a filter whose count moved since it was shown is refused, asking nothing.
            await StatusAsync(client, token, HttpMethod.Post, $"{flow}/rerender", HttpStatusCode.BadRequest,
                $$$"""{"keys":["{{{delivered[1]:D}}}"],"filter":{"status":"delivered"}}""");
            await StatusAsync(client, token, HttpMethod.Post, $"{flow}/rerender", HttpStatusCode.Conflict, """{"filter":{"status":"delivered"},"expected":99}""");
            Assert.Null((await RecordAsync(delivered[1])).PlanRequestedUtc);

            // By the filter shown, with a run: the records asked here, and a plain deliver run queued that plans them.
            var filtered = await JsonAsync(client, token, HttpMethod.Post, $"{flow}/rerender", """{"filter":{"status":"delivered"},"expected":3}""", HttpStatusCode.Accepted);
            Assert.Equal(3, filtered.GetProperty("marked").GetInt32());
            var plain = await RunAsync(filtered.GetProperty("runId").GetGuid());
            Assert.Equal((pipelineId, "deliver"), (plain.PipelineId, plain.Operation));
            Assert.DoesNotContain("rerender", plain.Payload ?? string.Empty, StringComparison.Ordinal);

            // A request that names no records asks nothing and queues nothing: every record is asked for by name, and all is
            // not asked beside keys or a filter.
            var runsBefore = await RunCountAsync();
            foreach (var route in new[] { "rerender", "rerender/preview", "redeliver" })
            {
                await StatusAsync(client, token, HttpMethod.Post, $"{flow}/{route}", HttpStatusCode.BadRequest, "{}");
                await StatusAsync(client, token, HttpMethod.Post, $"{flow}/{route}", HttpStatusCode.BadRequest, """{"filter":{"status":"delivered"},"expected":3,"all":true}""");
            }

            Assert.Equal(runsBefore, await RunCountAsync());

            // Every delivered record: asked of a deliver run, whose node asks and plans them in the run that brings them.
            var all = await JsonAsync(client, token, HttpMethod.Post, $"{flow}/rerender", """{"all":true}""", HttpStatusCode.Accepted);
            Assert.Equal(0, all.GetProperty("marked").GetInt32());
            Assert.Contains("\"rerender\":true", (await RunAsync(all.GetProperty("runId").GetGuid())).Payload, StringComparison.Ordinal);

            // What it would send, asked of a plan run over the delivered records of the selection, sending nothing.
            var preview = await JsonAsync(client, token, HttpMethod.Post, $"{flow}/rerender/preview", Keys(true, delivered[1], delivered[2], held), HttpStatusCode.Accepted);
            Assert.Equal((2, 3L, false), (preview.GetProperty("checked").GetInt32(), preview.GetProperty("selected").GetInt64(), preview.GetProperty("selectedCapped").GetBoolean()));
            var plan = await RunAsync(preview.GetProperty("runId").GetGuid());
            Assert.Equal("plan", plan.Operation);
            Assert.Contains("\"rerender\":true", plan.Payload, StringComparison.Ordinal);
            Assert.Contains(delivered[2].ToString("D"), plan.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(held.ToString("D"), plan.Payload, StringComparison.Ordinal);
            await StatusAsync(client, token, HttpMethod.Post, $"{flow}/rerender/preview", HttpStatusCode.Conflict, Keys(true, held));
            var whole = await JsonAsync(client, token, HttpMethod.Post, $"{flow}/rerender/preview", """{"all":true}""", HttpStatusCode.Accepted);
            Assert.Equal((3, 3L), (whole.GetProperty("checked").GetInt32(), whole.GetProperty("selected").GetInt64()));

            // Sent again whatever the hashes say: a part the route does not send is refused, and the record forgets its
            // delivered document so the next plan sends it.
            await StatusAsync(client, token, HttpMethod.Post, $"{flow}/redeliver", HttpStatusCode.BadRequest,
                $$"""{"keys":["{{delivered[2]:D}}"],"scope":"files"}""");
            var again = await JsonAsync(client, token, HttpMethod.Post, $"{flow}/redeliver", $$"""{"keys":["{{delivered[2]:D}}"],"scope":"record","run":false}""");
            Assert.Equal(1, again.GetProperty("marked").GetInt32());
            Assert.Null((await RecordAsync(delivered[2])).MetadataHash);
            var everyOne = await JsonAsync(client, token, HttpMethod.Post, $"{flow}/redeliver", """{"all":true}""", HttpStatusCode.Accepted);
            Assert.Contains("\"redeliver\":\"all\"", (await RunAsync(everyOne.GetProperty("runId").GetGuid())).Payload, StringComparison.Ordinal);
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryActivityRecords.Where(l => l.FlowId == flowId).ExecuteDeleteAsync();
                await osdu.DeliveryActivities.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
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
    }

    private static string Keys(bool run, params Guid[] keys)
        => $$"""{"keys":[{{string.Join(",", keys.Select(k => $"\"{k:D}\""))}}],"run":{{(run ? "true" : "false")}}}""";

    private static async Task<JsonElement> JsonAsync(
        HttpClient client, string token, HttpMethod method, string path, string? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await SendAsync(client, token, method, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{method} {path} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task StatusAsync(HttpClient client, string token, HttpMethod method, string path, HttpStatusCode expected, string? body = null)
    {
        using var response = await SendAsync(client, token, method, path, body);
        Assert.True(response.StatusCode == expected, $"{method} {path} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, string? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

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
