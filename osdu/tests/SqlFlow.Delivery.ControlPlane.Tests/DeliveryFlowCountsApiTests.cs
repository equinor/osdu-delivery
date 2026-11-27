using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// Every delivery flow's counts in one call (<c>GET /api/v1/delivery/flows/stats</c>), as the Delivery page and the home
/// page poll them: each flow as a whole, as its own stats call answers with no partition; in the partition asked for when
/// the flow names it among others; and a flow whose document does not parse listed with why, among the rest.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryFlowCountsApiTests
{
    [Fact]
    public async Task Every_flow_is_counted_in_one_call_as_a_whole_and_in_the_partition_asked_for_and_a_flow_that_cannot_be_says_why()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var (dev, test) = ("d" + suffix, "t" + suffix);
        var repoId = FlowIdentity.FromName("repo/cp-flow-counts-" + suffix);
        var (both, onlyTest, broken) = ("counts-both-" + suffix, "counts-test-" + suffix, "counts-broken-" + suffix);
        var (bothId, onlyTestId, brokenId) = (CatalogIdentity.Pipeline(repoId, both), CatalogIdentity.Pipeline(repoId, onlyTest), CatalogIdentity.Pipeline(repoId, broken));
        var (bothDev, bothTest, onlyTestLedger) = (FlowId.Of(both), FlowId.Of(both, test), FlowId.Of(onlyTest, test));
        var now = DateTime.UtcNow;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-flow-counts-" + suffix, RemoteUrl = "https://example/cp-flow-counts.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(repoId, both, Document(both, $"\n  - name: {dev}\n    keepLedger: true\n  - {test}"), now));
            db.Pipelines.Add(Pipeline(repoId, onlyTest, Document(onlyTest, $"\n  - {test}"), now));
            db.Pipelines.Add(Pipeline(repoId, broken, "flowType: delivery\nname: " + broken + "\nsource: [not a mapping\n", now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(bothDev, dev, both);
        await ledger.RegisterAsync(bothTest, test, both, $"{both}@{test}");
        await ledger.RegisterAsync(onlyTestLedger, test, onlyTest, $"{onlyTest}@{test}");
        foreach (var (flowId, pending) in new[] { (bothDev, 1), (bothTest, 2), (onlyTestLedger, 3) })
        {
            await ledger.UpsertPendingAsync(flowId, Enumerable.Range(0, pending).Select(row => new RecordState
            {
                DeliveryKey = new DeliveryKey(Guid.NewGuid()),
                FlowId = flowId,
                SourceKey = string.Create(CultureInfo.InvariantCulture, $"L-{row}"),
                Label = string.Create(CultureInfo.InvariantCulture, $"L-{row}"),
                MappingName = "WellLog",
                Status = RecordStatus.Pending,
                PendingDocumentRef = "1:0:10",
                PendingMetadata = true,
            }).ToList());
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

            // In dev: the flow naming dev among others is counted as a whole and in dev; the flow of test alone is counted as a
            // whole only, which says it delivers elsewhere; the flow that does not parse says why and holds no counts.
            var inDev = Entries(await JsonAsync(client, token, $"/api/v1/delivery/flows/stats?partition={dev}"));
            var bothEntry = inDev[bothId];
            Assert.Equal(3L, bothEntry.GetProperty("stats").GetProperty("pending").GetInt64());
            Assert.Equal([dev, test], Partitions(bothEntry.GetProperty("stats")));
            Assert.Equal((dev, 1L), (bothEntry.GetProperty("inPartition").GetProperty("partition").GetString(), bothEntry.GetProperty("inPartition").GetProperty("pending").GetInt64()));
            Assert.Equal(JsonValueKind.Null, bothEntry.GetProperty("problem").ValueKind);

            var onlyTestEntry = inDev[onlyTestId];
            Assert.Equal((test, 3L), (onlyTestEntry.GetProperty("stats").GetProperty("partition").GetString(), onlyTestEntry.GetProperty("stats").GetProperty("pending").GetInt64()));
            Assert.Equal(JsonValueKind.Null, onlyTestEntry.GetProperty("inPartition").ValueKind);

            var brokenEntry = inDev[brokenId];
            Assert.Equal(JsonValueKind.Null, brokenEntry.GetProperty("stats").ValueKind);
            var problem = brokenEntry.GetProperty("problem");
            Assert.Equal((409, "Flow document invalid"), (problem.GetProperty("status").GetInt32(), problem.GetProperty("title").GetString()));
            Assert.Contains("Re-sync the repository", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

            // Each flow's whole is what its own stats call answers with no partition, figure for figure.
            var single = await JsonAsync(client, token, $"/api/v1/delivery/flows/{bothId:D}/stats");
            Assert.Equal(single.GetRawText(), bothEntry.GetProperty("stats").GetRawText());

            // In test, the flow of test alone is already counted there as a whole; with no partition named, nothing is counted
            // apart from its whole.
            var inTest = Entries(await JsonAsync(client, token, $"/api/v1/delivery/flows/stats?partition={test}"));
            Assert.Equal(2L, inTest[bothId].GetProperty("inPartition").GetProperty("pending").GetInt64());
            Assert.Equal(JsonValueKind.Null, inTest[onlyTestId].GetProperty("inPartition").ValueKind);
            var anywhere = Entries(await JsonAsync(client, token, "/api/v1/delivery/flows/stats"));
            Assert.All([bothId, onlyTestId], id => Assert.Equal(JsonValueKind.Null, anywhere[id].GetProperty("inPartition").ValueKind));
            Assert.Equal(JsonValueKind.Object, anywhere[brokenId].GetProperty("problem").ValueKind);

            // Signed in to read, like every delivery page.
            using var anonymous = await client.GetAsync(new Uri("/api/v1/delivery/flows/stats", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                var ledgers = new[] { bothDev, bothTest, onlyTestLedger };
                await osdu.DeliveryRecords.Where(r => ledgers.Contains(r.FlowId)).ExecuteDeleteAsync();
                await osdu.DeliveryLedgers.Where(l => ledgers.Contains(l.FlowId)).ExecuteDeleteAsync();
                await osdu.DeliveryLedgerPartitions.Where(p => p.Name == dev || p.Name == test).ExecuteDeleteAsync();
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    /// <summary>A delivery flow document naming the partitions <paramref name="partitions"/> lists, as YAML list items.</summary>
    private static string Document(string name, string partitions) => $$"""
        flowType: delivery
        name: {{name}}
        partitions:{{partitions}}
        source:
          connection: ${env:OSDU_DATA_DB}
          record: { object: OsduData.arc.WellLog, key: [log_id] }
          lastModified: update_date
          work: ../.work/flow-counts
        render:
          mapping: WellLog@1.4.0
        target:
          endpoint: ${env:OSDU_URL}
          protocol: storage
        """;

    private static CatalogPipeline Pipeline(Guid repoId, string name, string yaml, DateTime now) => new()
    {
        Id = CatalogIdentity.Pipeline(repoId, name),
        RepoId = repoId,
        Name = name,
        Kind = "delivery",
        RelativePath = "flows/" + name + ".yaml",
        ContentHash = new string('0', 64),
        Yaml = yaml,
        DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"delivery"}"""),
        Active = true,
        Wave = 0,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

    private static Dictionary<Guid, JsonElement> Entries(JsonElement list)
        => list.EnumerateArray().ToDictionary(entry => entry.GetProperty("pipelineId").GetGuid());

    private static string[] Partitions(JsonElement stats)
        => stats.GetProperty("partitions").EnumerateArray().Select(p => p.GetString()!).ToArray();

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
