using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A run found by what an operator holds, as the API and the search serve it: the correlation id copied from a platform's log
/// or a bare run id. A part of a split delivery leads to its whole execution, and an id no run has says what that means.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryRunLookupApiTests
{
    private const string FlowName = "wells-wellbore-03-header-delivery-run-lookup";

    [Fact]
    public async Task A_correlation_id_from_a_log_leads_to_the_whole_execution_from_any_of_its_runs()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowId = FlowId.Of(FlowName);
        var pipeline = Guid.NewGuid();
        var root = Guid.CreateVersion7();
        var parts = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        var alone = Guid.CreateVersion7();
        var when = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
        var runs = new[] { root, parts[0], parts[1], alone };
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId);

        try
        {
            await using (var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs)))
            {
                catalog.Runs.AddRange(
                    Run(root, pipeline, when, null, null, null),
                    Run(parts[1], pipeline, when.AddMinutes(1), root, 2, 2),
                    Run(parts[0], pipeline, when.AddMinutes(1), root, 1, 2),
                    Run(alone, pipeline, when.AddHours(1), null, null, null));
                await catalog.SaveChangesAsync();
            }

            var submission = (await ledger.RegisterSubmissionAsync(new SubmissionState
            {
                SubmissionId = Guid.CreateVersion7(),
                FlowId = flowId,
                FlowName = FlowName,
                MappingReference = "Wellbore@1.0.0",
                RenderContext = "{}",
                Status = SubmissionStatus.Completed,
                Kind = SubmissionKinds.Incremental,
                SourceConnection = "${env:OSDU_DATA_DB}",
                SourceObject = "OsduData.arc.Wellbore",
                RunId = root,
                ReceivedUtc = when,
            })).Submission.SubmissionId;

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // A part's value, as a platform's log shows it: the part, then the coordinating run and the parts in order.
            var found = await ReadAsync(client, token, $"/api/v1/delivery/runs/lookup?id=osdu-delivery-wl-dlv-{parts[1]:D}");
            Assert.Equal((parts[1], "wl", "dlv"), (found.GetProperty("id").GetGuid(), found.GetProperty("repository").GetString(), found.GetProperty("operation").GetString()));
            Assert.Equal(parts[1], found.GetProperty("run").GetProperty("runId").GetGuid());
            Assert.Equal(
                [root, parts[0], parts[1]],
                found.GetProperty("execution").EnumerateArray().Select(r => r.GetProperty("runId").GetGuid()));
            Assert.Equal(submission, Assert.Single(found.GetProperty("submissions").EnumerateArray().ToList()).GetProperty("submissionId").GetGuid());

            // The coordinating run's bare id reads the same execution, and a run that was not split is one row.
            var byRoot = await ReadAsync(client, token, $"/api/v1/delivery/runs/lookup?id={root:D}");
            Assert.Equal(3, byRoot.GetProperty("execution").GetArrayLength());
            var single = await ReadAsync(client, token, $"/api/v1/delivery/runs/lookup?id={alone:D}");
            Assert.Equal(alone, Assert.Single(single.GetProperty("execution").EnumerateArray().ToList()).GetProperty("runId").GetGuid());
            Assert.Equal(0, single.GetProperty("submissions").GetArrayLength());

            // An explorer read's value holds an id no run has: the answer says what it is, rather than failing.
            var read = Guid.NewGuid();
            var none = await ReadAsync(client, token, $"/api/v1/delivery/runs/lookup?id=osdu-delivery-explore-{read:D}");
            Assert.Equal(JsonValueKind.Null, none.GetProperty("run").ValueKind);
            Assert.Equal("explore", none.GetProperty("operation").GetString());
            Assert.Equal(DeliveryRunLookup.NoRun, none.GetProperty("reason").GetString());

            using var noId = await GetAsync(client, token, "/api/v1/delivery/runs/lookup?id=welldb-delivery");
            Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);

            // The search lists the execution, each hit opening its run, and marks the one it was asked for.
            var hits = await ReadAsync(client, token, $"/api/v1/search/categories/runs?q=osdu-delivery-wl-dlv-{parts[0]:D}");
            Assert.Equal(3, hits.GetProperty("total").GetInt64());
            var items = hits.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal($"/runs/{root:D}", items[0].GetProperty("route").GetString());
            Assert.Equal(
                [parts[0]],
                items.Where(i => i.GetProperty("data").GetProperty("asked").GetBoolean()).Select(i => i.GetProperty("data").GetProperty("run").GetProperty("runId").GetGuid()));

            // The combined search carries the category, says when no run has the id, and has nothing for any other search.
            var all = await ReadAsync(client, token, $"/api/v1/search/all?q=osdu-delivery-explore-{read:D}");
            var noRun = Assert.Single(all.GetProperty("runs").GetProperty("items").EnumerateArray().ToList());
            Assert.Equal(JsonValueKind.Null, noRun.GetProperty("route").ValueKind);
            Assert.Equal(DeliveryRunLookup.NoRun, noRun.GetProperty("data").GetProperty("reason").GetString());
            var other = await ReadAsync(client, token, "/api/v1/search/all?q=wellbore");
            Assert.Equal(0, other.GetProperty("runs").GetProperty("total").GetInt64());
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliverySubmissions.Where(s => s.FlowId == flowId).ExecuteDeleteAsync();
            await using var catalog = new CatalogDbContext(CatalogDatabase.BuildOptions(cs));
            await catalog.Runs.Where(r => runs.Contains(r.RunId)).ExecuteDeleteAsync();
        }
    }

    private static CatalogRun Run(Guid runId, Guid pipelineId, DateTime at, Guid? root, int? slot, int? count) => new()
    {
        RunId = runId,
        PipelineId = pipelineId,
        FlowName = FlowName,
        FlowKind = "delivery",
        Status = RunStatuses.Succeeded,
        Success = true,
        WrittenUtc = at,
        DurationSeconds = 12.5,
        FanOutRoot = root,
        FanOutSlot = slot,
        FanOutCount = count,
    };

    private static ControlPlaneAppFactory Factory(string cs)
        => new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

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

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, string token, string path)
    {
        using var response = await GetAsync(client, token, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }
}
