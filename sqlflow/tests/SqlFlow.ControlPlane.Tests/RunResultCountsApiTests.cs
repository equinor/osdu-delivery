using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The runs list carries the counts each run's result reports when it is asked for them (<c>resultCounts=true</c>), so a
/// flow kind whose runs count something other than rows can show its own figures on a list of its runs; without the ask,
/// and in a group's member list, it carries none. Against the real shadow catalog; seeded rows are removed in a finally.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunResultCountsApiTests
{
    [SkippableFact]
    public async Task The_runs_list_carries_each_runs_result_counts_only_when_asked()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_result_counts_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "cp_result_counts_flow_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var anchor = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        var groupId = Guid.NewGuid();
        var counted = Guid.CreateVersion7();
        var textOnly = Guid.CreateVersion7();
        var noResult = Guid.CreateVersion7();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId,
                    Name = repoName,
                    RemoteUrl = null,
                    RootPath = "/tmp/" + repoName,
                    FirstSeenUtc = anchor,
                    LastSyncUtc = anchor,
                });
                db.Pipelines.Add(SeedPipeline(pipelineId, repoId, flowName, anchor));
                db.Runs.Add(SeedRun(
                    counted, pipelineId, repoId, flowName, anchor, groupId,
                    """{"operation":"deliver","delivered":9,"held":2,"failed":1,"seconds":0.25,"submission":{"delivered":40}}"""));
                db.Runs.Add(SeedRun(textOnly, pipelineId, repoId, flowName, anchor.AddMinutes(-1), groupId, """{"operation":"plan"}"""));
                db.Runs.Add(SeedRun(noResult, pipelineId, repoId, flowName, anchor.AddMinutes(-2), groupId, null));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            // Asked for: the whole numbers at the top of each result, an empty set for a result that has none, and null
            // for a run that recorded no result.
            var asked = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?pipelineId={pipelineId}&resultCounts=true&pageSize=200");
            Assert.Equal(3, asked.Items.Count);
            var counts = asked.Items.Single(r => r.RunId == counted).ResultCounts;
            Assert.NotNull(counts);
            Assert.Equal(new Dictionary<string, long> { ["delivered"] = 9, ["held"] = 2, ["failed"] = 1 }, counts);
            var none = asked.Items.Single(r => r.RunId == textOnly).ResultCounts;
            Assert.NotNull(none);
            Assert.Empty(none);
            Assert.Null(asked.Items.Single(r => r.RunId == noResult).ResultCounts);

            // The counts keep the names the result gives them on the wire, as a client reads them.
            using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/runs?runId={counted}&resultCounts=true", UriKind.Relative)))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                Assert.Contains("\"resultCounts\":{\"delivered\":9,\"held\":2,\"failed\":1}", body, StringComparison.Ordinal);
            }

            // Not asked for, and in a group's member list: no counts, whatever the result holds.
            var plain = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?pipelineId={pipelineId}&pageSize=200");
            Assert.Equal(3, plain.Items.Count);
            Assert.All(plain.Items, r => Assert.Null(r.ResultCounts));
            var declined = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?pipelineId={pipelineId}&resultCounts=false&pageSize=200");
            Assert.All(declined.Items, r => Assert.Null(r.ResultCounts));
            var members = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?groupId={groupId}&pageSize=200");
            Assert.Equal(3, members.Items.Count);
            Assert.All(members.Items, r => Assert.Null(r.ResultCounts));

            // An empty page asked for counts is an empty page.
            var empty = await GetJsonAsync<PagedResult<RunSummaryDto>>(
                client, token, $"/api/v1/runs?runId={Guid.NewGuid()}&resultCounts=true");
            Assert.Empty(empty.Items);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.PipelineId == pipelineId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.Id == pipelineId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static CatalogPipeline SeedPipeline(Guid id, Guid repoId, string name, DateTime now)
        => new()
        {
            Id = id,
            RepoId = repoId,
            Name = name,
            Kind = "file",
            Batch = null,
            RelativePath = $"flows/{name}.flow.yaml",
            ContentHash = "0000000000000000000000000000000000000000000000000000000000000000",
            Yaml = $"name: {name}\nflowType: file\n",
            DefinitionJson = $$"""{"name":"{{name}}","flowType":"file"}""",
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };

    private static CatalogRun SeedRun(
        Guid runId, Guid pipelineId, Guid repoId, string flowName, DateTime writtenUtc, Guid groupId, string? resultJson)
        => new()
        {
            RunId = runId,
            PipelineId = pipelineId,
            RepoId = repoId,
            FlowName = flowName,
            FlowKind = "file",
            Success = true,
            Status = RunStatuses.Succeeded,
            Error = null,
            SchemaVersion = 1,
            WrittenUtc = writtenUtc,
            DurationSeconds = 0.5,
            RowsLoaded = 3,
            GroupId = groupId,
            ResultJson = resultJson,
        };

    private static async Task<string> IssueReadTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
        return token.AccessToken;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
