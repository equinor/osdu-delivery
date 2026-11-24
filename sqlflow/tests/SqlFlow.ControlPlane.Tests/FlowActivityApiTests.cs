using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What flows moved, day by day, through the API the lineage graph draws its data from: each flow's runs totalled per
/// day (rows read and written, the files taken in, the counts a result reports), and one day's fires with their runs.
/// The grouping and the totals are EF queries whose translation only a real database proves. Removes its own rows; the
/// assembly runs serially (see AssemblyInfo).
/// </summary>
[Trait("Category", "Integration")]
public sealed class FlowActivityApiTests
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Each_flows_runs_are_totalled_per_day_and_a_days_fires_hold_the_runs_behind_its_totals()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("fa_" + suffix);
        var today = DateTime.UtcNow.Date;
        var yesterday = today.AddDays(-1);

        var pre = Pipeline(repoId, $"fa_{suffix}_orders_pre", "file", 0, today);
        var ing = Pipeline(repoId, $"fa_{suffix}_orders_ing", "ing", 1, today);
        var exp = Pipeline(repoId, $"fa_{suffix}_orders_exp", "exp", 2, today);
        var idle = Pipeline(repoId, $"fa_{suffix}_orders_idle", "ing", 2, today);
        var other = Pipeline(repoId, $"fa_{suffix}_other_ing", "ing", 1, today);

        var morning = Guid.NewGuid();
        var evening = Guid.NewGuid();
        var morningPre = Guid.NewGuid();
        var eveningExp = Guid.NewGuid();
        var loneIng = Guid.NewGuid();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId, Name = "fa_repo_" + suffix, FirstSeenUtc = today, LastSyncUtc = today,
                });
                db.Pipelines.AddRange(pre, ing, exp, idle, other);

                // Yesterday the schedule fired twice. Each fire ran the three flows in wave order; the evening's
                // export split itself over two parts, and the evening's ingestion failed.
                foreach (var (group, at, preRun, expRun, ingFails) in new[]
                {
                    (morning, yesterday.AddHours(4), morningPre, Guid.NewGuid(), false),
                    (evening, yesterday.AddHours(18), Guid.NewGuid(), eveningExp, true),
                })
                {
                    db.Runs.Add(Run(preRun, repoId, pre, group, at, loaded: 15, inserted: 15));
                    var ingRun = Run(Guid.NewGuid(), repoId, ing, group, at.AddMinutes(1), loaded: 15, inserted: 3, updated: 2);
                    if (ingFails)
                    {
                        ingRun.Status = RunStatuses.Failed;
                        ingRun.Success = false;
                        ingRun.RowsInserted = ingRun.RowsUpdated = ingRun.RowsDeleted = null;
                        ingRun.RowsLoaded = null;
                    }

                    db.Runs.Add(ingRun);
                    var exportRun = Run(expRun, repoId, exp, group, at.AddMinutes(2), loaded: 5);
                    exportRun.ResultJson = """{"operation":"export","written":5,"failed":1}""";
                    db.Runs.Add(exportRun);
                    db.RunFiles.AddRange(
                        new CatalogRunFile { RunId = preRun, RepoId = repoId, Name = "orders_1.csv", Rows = 10, Columns = 4, SizeBytes = 100, Modified = at },
                        new CatalogRunFile { RunId = preRun, RepoId = repoId, Name = "orders_2.csv", Rows = 5, Columns = 4, SizeBytes = 50, Modified = at.AddHours(1) });
                }

                for (var slot = 1; slot <= 2; slot++)
                {
                    var part = Run(Guid.NewGuid(), repoId, exp, evening, yesterday.AddHours(18).AddMinutes(3), loaded: 2);
                    part.FanOutRoot = eveningExp;
                    part.FanOutSlot = slot;
                    part.FanOutCount = 2;
                    part.ResultJson = """{"written":2}""";
                    db.Runs.Add(part);
                }

                // Today: the ingestion flow run on its own, which read fifteen rows and changed none.
                db.Runs.Add(Run(loneIng, repoId, ing, null, today.AddHours(1), loaded: 15, inserted: 0));

                // A flow the request does not name, and a run before the window: neither is counted.
                db.Runs.Add(Run(Guid.NewGuid(), repoId, other, null, today.AddHours(3), loaded: 99, inserted: 99));
                db.Runs.Add(Run(Guid.NewGuid(), repoId, pre, null, today.AddDays(-40), loaded: 7, inserted: 7));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);
            var named = string.Join("&", new[] { pre, ing, exp, idle }.Select(p => $"pipelineId={p.Id:D}"));
            var activity = await GetJsonAsync<FlowActivityDto>(client, token, $"/api/v1/datastreams/activity?days=30&{named}");

            Assert.Equal(30, activity.WindowDays);
            Assert.Equal(today.AddDays(-29), activity.FromUtc);
            // A flow with no run in the window has no entry, and neither has a flow that was not named.
            Assert.Equal(new[] { pre.Id, ing.Id, exp.Id }.Order(), activity.Flows.Select(f => f.PipelineId).Order());

            // The pre-ingestion flow: one day, two runs, with the files of both totalled.
            var landed = Assert.Single(Flow(activity, pre).Days);
            Assert.Equal(28, landed.Day);
            Assert.Equal(2, landed.Runs);
            Assert.Equal(0, landed.Failed);
            Assert.Equal(30, landed.RowsRead);
            Assert.True(landed.Breakdown);
            Assert.Equal(30, landed.RowsInserted);
            Assert.Equal(4, landed.Files);
            Assert.Equal(30, landed.FileRows);
            Assert.Equal(300, landed.FileBytes);
            Assert.Equal(60, landed.Seconds);
            Assert.Null(landed.Counts);

            // The ingestion flow: yesterday one run changed five rows and one failed; today one read and changed nothing.
            var keyed = Flow(activity, ing).Days;
            Assert.Equal(new[] { 28, 29 }, keyed.Select(d => d.Day));
            Assert.Equal(2, keyed[0].Runs);
            Assert.Equal(1, keyed[0].Failed);
            Assert.Equal(15, keyed[0].RowsRead);
            Assert.Equal(3, keyed[0].RowsInserted);
            Assert.Equal(2, keyed[0].RowsUpdated);
            Assert.Equal(1, keyed[1].Runs);
            Assert.True(keyed[1].Breakdown);
            Assert.Equal(15, keyed[1].RowsRead);
            Assert.Equal(0, keyed[1].RowsInserted + keyed[1].RowsUpdated + keyed[1].RowsDeleted);

            // The export reports only a row count, so it has no breakdown; its results' counts are summed over its two
            // runs, and its parts are part of their root, counted in neither the runs nor the sums.
            var exported = Assert.Single(Flow(activity, exp).Days);
            Assert.Equal(2, exported.Runs);
            Assert.False(exported.Breakdown);
            Assert.Equal(10, exported.RowsRead);
            Assert.NotNull(exported.Counts);
            Assert.Equal(10, exported.Counts["written"]);
            Assert.Equal(2, exported.Counts["failed"]);
            Assert.DoesNotContain("operation", exported.Counts.Keys);

            // Yesterday's fires, newest first, each with its runs in execution order.
            var day = yesterday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var fires = await GetJsonAsync<FlowFiresDto>(client, token, $"/api/v1/datastreams/activity/fires?day={day}&{named}");
            Assert.Equal(yesterday, fires.DayUtc);
            Assert.Equal(2, fires.TotalFires);
            Assert.Equal(new[] { evening.ToString("D"), morning.ToString("D") }, fires.Fires.Select(f => f.Key));

            var first = fires.Fires[1];
            Assert.Equal(morning, first.GroupId);
            Assert.Equal(new[] { pre.Name, ing.Name, exp.Name }, first.Runs.Select(r => r.Run.FlowName));
            Assert.Equal(morningPre, first.Runs[0].Run.RunId);
            var took = first.Runs[0].Files;
            Assert.NotNull(took);
            Assert.Equal(2, took.Files);
            Assert.Equal(15, took.Rows);
            Assert.Equal(150, took.Bytes);
            Assert.Equal(new DateTimeOffset(yesterday.AddHours(5), TimeSpan.Zero), took.NewestModified);
            Assert.Null(first.Runs[1].Files);

            // The evening's export split into two parts, which are its parts and not runs of the fire.
            var split = fires.Fires[0].Runs[2];
            Assert.Equal(eveningExp, split.Run.RunId);
            Assert.Equal(2, split.Parts);
            Assert.NotNull(split.Run.ResultCounts);
            Assert.Equal(1, split.Run.ResultCounts["failed"]);
            Assert.Equal(RunStatuses.Failed, fires.Fires[0].Runs[1].Run.Status);

            // Today's fire: a run started on its own is a fire of one, keyed by its run id.
            var now = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var alone = await GetJsonAsync<FlowFiresDto>(client, token, $"/api/v1/datastreams/activity/fires?day={now}&{named}");
            var lone = Assert.Single(alone.Fires);
            Assert.Equal(loneIng.ToString("D"), lone.Key);
            Assert.Null(lone.GroupId);
            Assert.Equal(1, alone.TotalFires);

            using var unnamed = await SendAsync(client, token, "/api/v1/datastreams/activity?days=30");
            Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
            using var tooWide = await SendAsync(client, token, $"/api/v1/datastreams/activity?days=999&{named}");
            Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);
            using var noDay = await SendAsync(client, token, $"/api/v1/datastreams/activity/fires?{named}");
            Assert.Equal(HttpStatusCode.BadRequest, noDay.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static FlowActivityFlowDto Flow(FlowActivityDto activity, CatalogPipeline flow) =>
        Assert.Single(activity.Flows, f => f.PipelineId == flow.Id);

    private static CatalogPipeline Pipeline(Guid repoId, string name, string kind, int wave, DateTime today) => new()
    {
        Id = CatalogIdentity.Pipeline(repoId, name),
        RepoId = repoId,
        Name = name,
        Kind = kind,
        RelativePath = "flows/" + name + ".yaml",
        ContentHash = new string('0', 64),
        Yaml = "name: " + name + "\n",
        DefinitionJson = "{}",
        Active = true,
        Wave = wave,
        FirstSeenUtc = today,
        LastSeenUtc = today,
    };

    private static CatalogRun Run(
        Guid runId, Guid repoId, CatalogPipeline flow, Guid? group, DateTime at,
        long loaded, long? inserted = null, long? updated = null) => new()
    {
        RunId = runId,
        PipelineId = flow.Id,
        RepoId = repoId,
        FlowName = flow.Name,
        FlowKind = flow.Kind,
        Success = true,
        Status = RunStatuses.Succeeded,
        GroupId = group,
        StartUtc = at,
        WrittenUtc = at.AddSeconds(30),
        DurationSeconds = 30,
        RowsLoaded = loaded,
        RowsInserted = inserted,
        RowsUpdated = updated,
        RowsDeleted = inserted is null ? null : 0,
    };

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string url)
    {
        using var response = await SendAsync(client, token, url);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
