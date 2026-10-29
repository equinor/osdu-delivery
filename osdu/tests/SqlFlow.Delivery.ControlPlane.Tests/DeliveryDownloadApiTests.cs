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
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Downloads;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The downloads of download flows as the control plane serves them (osdu/docs/reference/concepts/api.md, Downloads): a download
/// flow's downloads in a partition (declared, kept and no longer declared) with their counts and last run, a download's runs,
/// and its records a page at a time, of one state or found by id or file name. The downloads are written through the ledger
/// as a run writes them, so what these tests hold the API to is what a real run leaves; the tests never reach an OSDU.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryDownloadApiTests
{
    private const string Wellbore = "*:*:master-data--Wellbore:*";

    [Fact]
    public async Task A_download_flows_downloads_runs_and_records_are_served_in_its_partition()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-download-" + suffix;
        var partition = "dl" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-downloads-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var otherPipelineId = CatalogIdentity.Pipeline(repoId, flowName + "-ingestion");
        var yaml = $$"""
            flowType: download
            name: {{flowName}}
            description: Every wellbore the partition holds, a file each.
            partitions: [{{partition}}]
            source:
              endpoint: http://localhost
            target:
              location: abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records
            downloads:
              - name: Wellbores
                description: Every wellbore, whatever its kind's version.
                kind: "{{Wellbore}}"
              - name: Wells
                kind: "*:*:master-data--Well:*"
            """;
        var flow = new DeliveryDocumentLoader().ParseDownload(yaml, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        string Id(string key) => $"{partition}:master-data--Wellbore:{key}";
        const string Folder = "abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records/Wellbores";

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-downloads-" + suffix, RemoteUrl = "https://example/cp-downloads.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, DownloadFlowDefinition.FlowTypeName, yaml, now));
            db.Pipelines.Add(Pipeline(otherPipelineId, repoId, flowName + "-ingestion", "ingestion", "flowType: ingestion", now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Download, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });

            // A run of Wellbores lists four ids, writes three, and storage does not return the fourth; then a later run finds one gone.
            var wellbores = await ledger.RegisterDownloadAsync(flow.LedgerId, flow.Name, "Wellbores", Wellbore, null, "search");
            var first = await ledger.StartDownloadRunAsync(flow.LedgerId, wellbores.DownloadId, Guid.NewGuid(), "download api tests", "search", Folder, null, now.AddHours(-2));
            await ledger.AppendDownloadScanAsync(flow.LedgerId, first, [Row(Id("WB-1"), 10), Row(Id("WB-2"), 10), Row(Id("WB-3"), 10), Row(Id("WB-4"), 10)]);
            var merge = await ledger.MergeDownloadAsync(flow.LedgerId, wellbores.DownloadId, first, now.AddHours(-2));
            Assert.Equal(new DownloadMerge(4, 4, 0, 0, 0), merge);
            var due = await ledger.DownloadsDueAsync(flow.LedgerId, wellbores.DownloadId, every: false, after: 0, max: 100);
            Assert.Equal(4, due.Count);
            await ledger.RecordDownloadWritesAsync(
                flow.LedgerId, first,
                [
                    DownloadWrite.Written(due[0].DownloadRecordId, 10, 120, new byte[32]),
                    DownloadWrite.Written(due[1].DownloadRecordId, 10, 130, new byte[32]),
                    DownloadWrite.Written(due[2].DownloadRecordId, 10, 140, new byte[32]),
                    DownloadWrite.Missed(due[3].DownloadRecordId, "storage did not return it"),
                ],
                now.AddHours(-2));
            await ledger.CompleteDownloadRunAsync(
                flow.LedgerId, first,
                new DownloadRunState
                {
                    Status = DownloadRunStatus.Completed, Location = Folder, Listed = 4, Added = 4, Written = 3, Bytes = 390, Missing = 1, Pages = 1, Requests = 2,
                },
                now.AddHours(-2).AddMinutes(1));

            var second = await ledger.StartDownloadRunAsync(flow.LedgerId, wellbores.DownloadId, Guid.NewGuid(), "download api tests", "search", Folder, null, now.AddHours(-1));
            await ledger.AppendDownloadScanAsync(flow.LedgerId, second, [Row(Id("WB-1"), 11), Row(Id("WB-2"), 10), Row(Id("WB-4"), 10)]);
            Assert.Equal(new DownloadMerge(3, 0, 1, 1, 0), await ledger.MergeDownloadAsync(flow.LedgerId, wellbores.DownloadId, second, now.AddHours(-1)));
            await ledger.CompleteDownloadRunAsync(
                flow.LedgerId, second,
                new DownloadRunState { Status = DownloadRunStatus.Completed, Location = Folder, Listed = 3, Changed = 1, Gone = 1 },
                now.AddHours(-1).AddMinutes(1));

            // Retired is kept from a declaration the flow no longer makes.
            await ledger.RegisterDownloadAsync(flow.LedgerId, flow.Name, "Retired", "osdu:wks:master-data--Field:*", null, "search");

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The flow's downloads in the partition, declared first in its order, then the one it no longer declares.
            var answer = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/downloads?partition={partition}");
            Assert.Equal((partition, true, "search", "abfss://lake@welldbstorage.dfs.core.windows.net/osdu/records"),
                (answer.GetProperty("partition").GetString(), answer.GetProperty("readsPartition").GetBoolean(), answer.GetProperty("read").GetString(), answer.GetProperty("location").GetString()));
            Assert.False(answer.TryGetProperty("problem", out _));
            var entries = answer.GetProperty("downloads").EnumerateArray().ToList();
            Assert.Equal(["Wellbores", "Wells", "Retired"], entries.Select(e => e.GetProperty("name").GetString()));
            Assert.Equal([true, true, false], entries.Select(e => e.GetProperty("declared").GetBoolean()));
            Assert.False(entries[1].TryGetProperty("download", out _));

            var kept = entries[0].GetProperty("download");
            Assert.Equal((wellbores.DownloadId, Folder), (kept.GetProperty("downloadId").GetInt32(), kept.GetProperty("location").GetString()));
            var counts = kept.GetProperty("counts");
            Assert.Equal((1L, 1L, 1L, 1L, 390L),
                (counts.GetProperty("written").GetInt64(), counts.GetProperty("due").GetInt64(), counts.GetProperty("problem").GetInt64(), counts.GetProperty("gone").GetInt64(), counts.GetProperty("bytes").GetInt64()));
            var lastRun = kept.GetProperty("lastRun");
            Assert.Equal((second, 1L), (lastRun.GetProperty("downloadRunId").GetInt64(), lastRun.GetProperty("gone").GetInt64()));

            // The download's runs, the newest first.
            var downloadPath = $"/api/v1/delivery/downloads/{partition}/{wellbores.DownloadId.ToString(CultureInfo.InvariantCulture)}";
            var runs = (await JsonAsync(client, token, downloadPath + "/runs")).EnumerateArray().ToList();
            Assert.Equal([second, first], runs.Select(r => r.GetProperty("downloadRunId").GetInt64()));
            Assert.Equal((3L, 390L, 1L), (runs[1].GetProperty("written").GetInt64(), runs[1].GetProperty("bytes").GetInt64(), runs[1].GetProperty("missing").GetInt64()));

            // Its records a page at a time, of one state, and found by id or file name whatever the case.
            var page = await JsonAsync(client, token, downloadPath + "/records?limit=3");
            Assert.Equal(3, page.GetProperty("items").GetArrayLength());
            var rest = await JsonAsync(client, token, downloadPath + $"/records?limit=3&after={page.GetProperty("next").GetInt64().ToString(CultureInfo.InvariantCulture)}");
            Assert.Equal(1, rest.GetProperty("items").GetArrayLength());
            Assert.False(rest.TryGetProperty("next", out _));

            var states = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DownloadRecordStates.Written] = Id("WB-2"),
                [DownloadRecordStates.Due] = Id("WB-1"),
                [DownloadRecordStates.Problem] = Id("WB-4"),
                [DownloadRecordStates.Gone] = Id("WB-3"),
            };
            foreach (var (state, id) in states)
            {
                var record = Assert.Single((await JsonAsync(client, token, downloadPath + $"/records?state={state}")).GetProperty("items").EnumerateArray());
                Assert.Equal((id, state, DownloadFileNames.Of(id)), (record.GetProperty("targetId").GetString(), record.GetProperty("state").GetString(), record.GetProperty("fileName").GetString()));
            }

            var problem = Assert.Single((await JsonAsync(client, token, downloadPath + "/records?state=problem")).GetProperty("items").EnumerateArray());
            Assert.Equal("storage did not return it", problem.GetProperty("problem").GetString());
            var written = Assert.Single((await JsonAsync(client, token, downloadPath + "/records?state=written")).GetProperty("items").EnumerateArray());
            Assert.Equal((10L, 130L, new string('0', 64)), (written.GetProperty("writtenVersion").GetInt64(), written.GetProperty("bytes").GetInt64(), written.GetProperty("sha256").GetString()));

            var found = (await JsonAsync(client, token, downloadPath + "/records?q=wb-1")).GetProperty("items").EnumerateArray().ToList();
            Assert.Equal([Id("WB-1")], found.Select(f => f.GetProperty("targetId").GetString()));
            var byName = (await JsonAsync(client, token, downloadPath + "/records?q=wellbore~wb-4")).GetProperty("items").EnumerateArray().ToList();
            Assert.Equal([Id("WB-4")], byName.Select(f => f.GetProperty("targetId").GetString()));

            // What a request gets wrong is refused naming it; a download the partition does not keep is not found.
            await ProblemAsync(client, token, downloadPath + "/records?state=lost", HttpStatusCode.BadRequest, "'lost' is not where a downloaded record stands");
            await ProblemAsync(client, token, downloadPath + "/records?limit=0", HttpStatusCode.BadRequest, "limit is how many records a page holds");
            await ProblemAsync(client, token, downloadPath + "/runs?limit=500", HttpStatusCode.BadRequest, "limit is how many runs the history lists");
            await ProblemAsync(client, token, $"/api/v1/delivery/downloads/{partition}/999999/records", HttpStatusCode.NotFound, "keeps no download 999999");
            await ProblemAsync(client, token, "/api/v1/delivery/downloads/bad!partition/1/runs", HttpStatusCode.BadRequest, "is not a partition");

            // In a partition the flow does not read, it says why, with what it declares; another kind and an unknown pipeline are refused.
            var elsewhere = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/downloads?partition=other{suffix}");
            Assert.False(elsewhere.GetProperty("readsPartition").GetBoolean());
            Assert.Contains("does not read partition", elsewhere.GetProperty("problem").GetString(), StringComparison.Ordinal);
            Assert.All(elsewhere.GetProperty("downloads").EnumerateArray(), e => Assert.False(e.TryGetProperty("download", out _)));
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{otherPipelineId}/downloads", HttpStatusCode.Conflict, "not a download flow");
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{Guid.NewGuid()}/downloads", HttpStatusCode.NotFound, "No pipeline");
        }
        finally
        {
            await CleanUpAsync(cs, repoId, partition, flow.LedgerId);
        }
    }

    private static DownloadScanRow Row(string id, long version)
        => new(id, "osdu:wks:master-data--Wellbore:1.0.0", version, DownloadFileNames.Of(id), DownloadFileNames.Distinct(id));

    private static async Task CleanUpAsync(string cs, Guid repoId, string partition, Guid downloadFlow)
    {
        await using (var osdu = SampleEstate.Context(cs))
        {
            var downloads = await osdu.DeliveryDownloads.Where(d => d.FlowId == downloadFlow).Select(d => d.DownloadId).ToListAsync();
            var runs = osdu.DeliveryDownloadRuns.Where(r => downloads.Contains(r.DownloadId)).Select(r => r.DownloadRunId);
            await osdu.DeliveryDownloadScans.Where(s => runs.Contains(s.DownloadRunId)).ExecuteDeleteAsync();
            await osdu.DeliveryDownloadRecords.Where(r => downloads.Contains(r.DownloadId)).ExecuteDeleteAsync();
            await osdu.DeliveryDownloadRuns.Where(r => downloads.Contains(r.DownloadId)).ExecuteDeleteAsync();
            await osdu.DeliveryDownloads.Where(d => d.FlowId == downloadFlow).ExecuteDeleteAsync();
            await osdu.DeliveryActivities.Where(a => a.FlowId == downloadFlow).ExecuteDeleteAsync();
            await osdu.DeliveryLedgers.Where(l => l.FlowId == downloadFlow).ExecuteDeleteAsync();
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
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
