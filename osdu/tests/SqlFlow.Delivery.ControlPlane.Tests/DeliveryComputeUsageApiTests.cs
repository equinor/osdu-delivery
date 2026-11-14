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
using SqlFlow.Delivery.ComputeUsage;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Maintenance;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Tests;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Compute usage through the API (osdu/docs/reference/concepts/compute-usage.md): the control plane's own node is
/// measured, a period's total, days and shares are an admin's to read, the work behind a share is listed a page at a
/// time, and a request the store refuses says why.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryComputeUsageApiTests
{
    [Fact]
    public async Task An_admin_reads_a_periods_usage_its_shares_and_the_work_behind_them()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var repo = "cp-usage-" + suffix;
        var store = new ComputeUsageStore(() => SampleEstate.Context(cs));

        // A period nothing else writes in, so the sums are this test's alone whatever the shared database holds.
        var day = new DateTime(2001, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var work = new[]
        {
            Work(repo, day, minutes: 60, "wells", "wells-delivery"),
            Work(repo, day.AddHours(1), minutes: 30, "wells", "wells-delivery"),
            Work(repo, day.AddDays(1), minutes: 120, "logs", "logs-delivery"),
        };

        try
        {
            await store.RecordAsync(work);

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();

            // The control plane's own node is measured: its line to the dispatcher is the metered one, and the recorder runs.
            Assert.IsType<MeteredNodeTransport>(factory.Services.GetRequiredService<INodeTransport>());
            Assert.Contains(factory.Services.GetServices<IHostedService>(), s => s is ComputeUsageRecorder);

            var admin = await TokenAsync(client, "usage-admin-" + suffix, ["admin"]);
            var reader = await TokenAsync(client, "usage-reader-" + suffix, ["read", "operate"]);

            using (var forbidden = await SendAsync(client, reader, "/api/v1/delivery/compute-usage?from=2001-03-01&to=2001-03-02"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }

            using (var forbidden = await SendAsync(client, reader, "/api/v1/delivery/compute-usage/work?from=2001-03-01&to=2001-03-02"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }

            var summary = await JsonAsync(client, admin, "/api/v1/delivery/compute-usage?from=2001-03-01&to=2001-03-02");
            Assert.Equal("2001-03-01", summary.GetProperty("from").GetString());
            Assert.Equal("2001-03-02", summary.GetProperty("to").GetString());
            var totals = summary.GetProperty("totals");
            Assert.Equal(3, totals.GetProperty("work").GetInt64());
            Assert.Equal(210 * 60_000L, totals.GetProperty("wallClockMs").GetInt64());
            Assert.Equal(3.5 * 8 / 6, totals.GetProperty("computeUnits").GetDouble(), 9);
            Assert.Equal(["2001-03-01", "2001-03-02"], summary.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("day").GetString()));
            var shares = summary.GetProperty("shares").EnumerateArray().ToList();
            Assert.Equal(["logs-delivery", "wells-delivery"], shares.Select(s => s.GetProperty("flow").GetString()));
            Assert.Equal(("", repo, "wells", "run", 2L),
                (shares[1].GetProperty("team").GetString(), shares[1].GetProperty("repo").GetString(), shares[1].GetProperty("project").GetString(),
                    shares[1].GetProperty("kind").GetString(), shares[1].GetProperty("work").GetInt64()));
            Assert.True(summary.GetProperty("workKeptFromUtc").GetDateTime() <= day);

            // The work behind the wells share, a page at a time.
            var path = $"/api/v1/delivery/compute-usage/work?from=2001-03-01&to=2001-03-02&team=&repo={repo}&project=wells&take=1";
            var first = await JsonAsync(client, admin, path);
            var row = Assert.Single(first.GetProperty("rows").EnumerateArray());
            Assert.Equal(work[1].WorkId, row.GetProperty("workId").GetGuid());
            Assert.Equal(("node-1", "run", "wells-delivery", "user:ada", 1_800_000L, 8, 1, 6, "completed"),
                (row.GetProperty("node").GetString(), row.GetProperty("kind").GetString(), row.GetProperty("flow").GetString(),
                    row.GetProperty("requestedBy").GetString(), row.GetProperty("wallClockMs").GetInt64(), row.GetProperty("cores").GetInt32(),
                    row.GetProperty("slots").GetInt32(), row.GetProperty("nodeSlots").GetInt32(), row.GetProperty("outcome").GetString()));
            var next = first.GetProperty("next").GetString();
            Assert.NotNull(next);
            var second = await JsonAsync(client, admin, path + "&after=" + Uri.EscapeDataString(next));
            Assert.Equal(work[0].WorkId, Assert.Single(second.GetProperty("rows").EnumerateArray()).GetProperty("workId").GetGuid());
            Assert.Equal(JsonValueKind.Null, second.GetProperty("next").ValueKind);

            // With no period named, the last 30 days ending today.
            var recent = await JsonAsync(client, admin, "/api/v1/delivery/compute-usage");
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            Assert.Contains(recent.GetProperty("to").GetString(), new[] { Day(today), Day(today.AddDays(-1)) });
            Assert.Equal(29, ParseDay(recent.GetProperty("to").GetString()!).DayNumber - ParseDay(recent.GetProperty("from").GetString()!).DayNumber);

            // What the store refuses is said as it says it.
            Assert.Equal(
                (HttpStatusCode.BadRequest, "'from' is '1 March'; it is a day written yyyy-MM-dd."),
                await ProblemAsync(client, admin, "/api/v1/delivery/compute-usage?from=1%20March"));
            Assert.Equal(
                (HttpStatusCode.BadRequest, "The period ends (2001-03-01) before it starts (2001-03-02)."),
                await ProblemAsync(client, admin, "/api/v1/delivery/compute-usage?from=2001-03-02&to=2001-03-01"));
            Assert.Equal(
                (HttpStatusCode.BadRequest, "A page of work holds 1 to 500 rows; 501 was asked for."),
                await ProblemAsync(client, admin, "/api/v1/delivery/compute-usage/work?from=2001-03-01&to=2001-03-02&take=501"));

            // The folders that name a team, each with its repository's name while the catalog holds it.
            var repoId = Guid.NewGuid();
            var gone = Guid.NewGuid();
            await using (var catalog = CatalogDatabase.Create(cs))
            {
                catalog.Repos.Add(new CatalogRepo { Id = repoId, Name = repo, FirstSeenUtc = day, LastSyncUtc = day });
                await catalog.SaveChangesAsync();
            }

            await using (var osdu = SampleEstate.Context(cs))
            {
                osdu.DeliveryRepositoryTeams.AddRange(
                    new DeliveryRepositoryTeam { RepoId = repoId, Folder = "wells", Team = "Wells " + suffix, UpdatedUtc = day },
                    new DeliveryRepositoryTeam { RepoId = repoId, Folder = string.Empty, Team = "Estate " + suffix, UpdatedUtc = day },
                    new DeliveryRepositoryTeam { RepoId = gone, Folder = string.Empty, Team = "Gone " + suffix, UpdatedUtc = day });
                await osdu.SaveChangesAsync();
            }

            try
            {
                using (var forbidden = await SendAsync(client, reader, "/api/v1/delivery/compute-usage/teams"))
                {
                    Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
                }

                var teams = (await JsonAsync(client, admin, "/api/v1/delivery/compute-usage/teams")).EnumerateArray()
                    .Where(t => t.GetProperty("team").GetString()!.EndsWith(suffix, StringComparison.Ordinal))
                    .Select(t => (Repo: t.GetProperty("repo").GetString(), Folder: t.GetProperty("folder").GetString(), Team: t.GetProperty("team").GetString()))
                    .ToList();
                Assert.Equal(
                    [(null, "", "Gone " + suffix), (repo, "", "Estate " + suffix), (repo, "wells", "Wells " + suffix)],
                    teams);
            }
            finally
            {
                await using var osdu = SampleEstate.Context(cs);
                await osdu.DeliveryRepositoryTeams.Where(t => t.RepoId == repoId || t.RepoId == gone).ExecuteDeleteAsync();
                await using var catalog = CatalogDatabase.Create(cs);
                await catalog.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }

            // The Maintenance page's cleanup of the node work folds it into its days: the first day's two pieces of work
            // leave the list, and the period's usage reads as it did.
            var contributor = new OsduStorageContributor(factory.Services);
            var area = contributor.Areas.Single(a => a.Id == OsduStorageContributor.ComputeWorkArea);
            var cutoff = new DateTime(2001, 3, 2, 0, 0, 0, DateTimeKind.Utc);
            Assert.Equal(2, await contributor.CountAsync(area, cutoff, CancellationToken.None));
            Assert.Equal(2, await contributor.CleanAsync(area, cutoff, CancellationToken.None));
            Assert.Equal(0, await contributor.CountAsync(area, cutoff, CancellationToken.None));
            var folded = await JsonAsync(client, admin, "/api/v1/delivery/compute-usage?from=2001-03-01&to=2001-03-02");
            Assert.Equal(3, folded.GetProperty("totals").GetProperty("work").GetInt64());
            Assert.Equal(210 * 60_000L, folded.GetProperty("totals").GetProperty("wallClockMs").GetInt64());
            Assert.Equal(
                ["logs-delivery", "wells-delivery"],
                folded.GetProperty("shares").EnumerateArray().Select(s => s.GetProperty("flow").GetString()));
            var left = await JsonAsync(client, admin, $"/api/v1/delivery/compute-usage/work?from=2001-03-01&to=2001-03-02&repo={repo}");
            Assert.Equal(work[2].WorkId, Assert.Single(left.GetProperty("rows").EnumerateArray()).GetProperty("workId").GetGuid());
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryComputeUsages.Where(u => u.Repo == repo).ExecuteDeleteAsync();
            await osdu.DeliveryComputeUsageDays.Where(d => d.Repo == repo).ExecuteDeleteAsync();
        }
    }

    private static ComputeWork Work(string repo, DateTime endedUtc, int minutes, string project, string flow)
        => new(
            Guid.CreateVersion7(), "node-1", ComputeWorkKinds.Run, Guid.NewGuid(), 1, null, repo, project, project, flow, "user:ada",
            endedUtc.AddMinutes(-minutes), endedUtc, minutes * 60_000L, 8, 1, 6, ComputeOutcomes.Completed);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDay(string day) => DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<(HttpStatusCode Status, string Detail)> ProblemAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, path);
        var text = await response.Content.ReadAsStringAsync();
        var detail = JsonDocument.Parse(text).RootElement.TryGetProperty("detail", out var said) ? said.GetString() ?? string.Empty : string.Empty;
        return (response.StatusCode, detail);
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

    private static async Task<string> TokenAsync(HttpClient client, string subject, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, subject, scopes.ToArray()));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
