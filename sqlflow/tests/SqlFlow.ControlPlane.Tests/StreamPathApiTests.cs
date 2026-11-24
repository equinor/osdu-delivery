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
/// One stream followed end to end through the API: the path read from the catalog's lineage (with the object registry's
/// names, and an edge's name for an object the registry does not hold), each flow's verdict, and the fires through the
/// path, grouped by what started them, with a fan-out's parts folded into their root and a pre-ingestion run's files
/// totalled. The grouping and the totals are EF queries whose translation only a real database proves. Removes its own
/// rows; the assembly runs serially (see AssemblyInfo).
/// </summary>
[Trait("Category", "Integration")]
public sealed class StreamPathApiTests
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task A_stream_is_followed_from_its_files_through_every_flow_with_each_fire_it_ran_in()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("sp_" + suffix);
        var today = DateTime.UtcNow.Date;

        var files = $"file|||sp_{suffix}/orders";
        var preTable = $"srv|db_{suffix}|pre|orders";
        var preView = $"srv|db_{suffix}|pre|v_orders";
        var arcTable = $"srv|db_{suffix}|arc|orders";
        var export = $"file|||sp_{suffix}/exports/orders";

        var pre = Pipeline(repoId, $"sp_{suffix}_orders_pre", "file", 0, today);
        var ing = Pipeline(repoId, $"sp_{suffix}_orders_ing", "ing", 1, today);
        var exp = Pipeline(repoId, $"sp_{suffix}_orders_exp", "exp", 2, today);
        var check = Pipeline(repoId, $"sp_{suffix}_orders_check", "hc", 2, today);

        var olderFire = Guid.NewGuid();
        var newerFire = Guid.NewGuid();
        var newerExp = Guid.NewGuid();
        var loneIng = Guid.NewGuid();
        var newerPre = Guid.NewGuid();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId, Name = "sp_repo_" + suffix, FirstSeenUtc = today, LastSyncUtc = today,
                });
                db.Pipelines.AddRange(pre, ing, exp, check);

                db.Objects.AddRange(
                    Object(preTable, $"db_{suffix}", "pre", "Orders", "Table", today),
                    Object(preView, $"db_{suffix}", "pre", "v_Orders", "View", today),
                    Object(arcTable, $"db_{suffix}", "arc", "Orders", "Table", today));

                db.LineageEdges.AddRange(
                    Edge(repoId, pre, "Reads", files, $"Sp_{suffix}/orders", "Declared"),
                    Edge(repoId, pre, "Writes", preView, "v_Orders", "Declared"),
                    Edge(repoId, pre, "Writes", preTable, "Orders", "Declared"),
                    Edge(repoId, ing, "Reads", preView, "v_Orders", "Declared"),
                    Edge(repoId, ing, "Reads", preTable, "Orders", "Derived"),
                    Edge(repoId, ing, "Writes", arcTable, "Orders", "Declared"),
                    Edge(repoId, exp, "Reads", arcTable, "Orders", "Declared"),
                    Edge(repoId, exp, "Writes", export, $"Sp_{suffix}/exports/orders", "Declared"),
                    Edge(repoId, check, "Reads", arcTable, "Orders", "Declared"));

                // Two schedule fires, two days and one day ago, each running the three flows in wave order; the newer
                // export split itself over two parts. Then a run of the ingestion flow on its own, today.
                foreach (var (group, day, preRun, expRun) in new[]
                {
                    (olderFire, today.AddDays(-2), Guid.NewGuid(), Guid.NewGuid()),
                    (newerFire, today.AddDays(-1), newerPre, newerExp),
                })
                {
                    db.Runs.Add(Run(preRun, repoId, pre, group, day.AddHours(4), loaded: 15, inserted: 15));
                    db.Runs.Add(Run(Guid.NewGuid(), repoId, ing, group, day.AddHours(4).AddMinutes(1), loaded: 15, inserted: 3, updated: 2));
                    var exportRun = Run(expRun, repoId, exp, group, day.AddHours(4).AddMinutes(2), loaded: 5);
                    exportRun.ResultJson = """{"operation":"export","written":5,"failed":1}""";
                    db.Runs.Add(exportRun);
                    db.RunFiles.AddRange(
                        new CatalogRunFile { RunId = preRun, RepoId = repoId, Name = "orders_1.csv", Rows = 10, Columns = 4, SizeBytes = 100, Modified = day },
                        new CatalogRunFile { RunId = preRun, RepoId = repoId, Name = "orders_2.csv", Rows = 5, Columns = 4, SizeBytes = 50, Modified = day.AddHours(1) });
                }

                for (var slot = 1; slot <= 2; slot++)
                {
                    var part = Run(Guid.NewGuid(), repoId, exp, newerFire, today.AddDays(-1).AddHours(4).AddMinutes(3), loaded: 2);
                    part.FanOutRoot = newerExp;
                    part.FanOutSlot = slot;
                    part.FanOutCount = 2;
                    db.Runs.Add(part);
                }

                db.Runs.Add(Run(loneIng, repoId, ing, null, today.AddHours(1), loaded: 15, inserted: 0));
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);
            var path = await GetJsonAsync<StreamPathDto>(client, token, $"/api/v1/datastreams/{ing.Id}/path?days=30");

            Assert.True(path.LineageKnown);
            Assert.Equal(ing.Name, path.FlowName);
            Node(path, $"flow:{ing.Id:D}", "selected");
            Node(path, $"flow:{pre.Id:D}", "upstream");
            Node(path, $"flow:{exp.Id:D}", "downstream");

            // Where the data enters, named by its edge because the registry does not hold it; then the pre table the
            // two flows hand over through, though the ingestion flow reads the view too.
            var origin = Node(path, $"object:{files}", "upstream");
            Assert.True(origin.External);
            Assert.Equal("file", origin.Kind);
            Assert.Equal($"Sp_{suffix}/orders", origin.Name);
            var handOver = Node(path, $"object:{preTable}", "upstream");
            Assert.Equal($"db_{suffix}.pre.Orders", handOver.QualifiedName);
            Assert.Equal("table", handOver.Kind);
            Assert.DoesNotContain(path.Nodes, n => n.ObjectKey == preView);
            Assert.Contains(path.Edges, e => e.From == $"object:{preTable}" && e.To == $"flow:{ing.Id:D}");

            // The check writes nothing, so it ends the journey: named on the table it reads, not drawn.
            var arc = Node(path, $"object:{arcTable}", "downstream");
            var readers = Assert.Single(arc.Omitted, o => o.What == "readers");
            Assert.Equal(new[] { check.Name }, readers.Names);
            Assert.DoesNotContain(path.Nodes, n => n.PipelineId == check.Id);

            // Each flow that ran in the window carries the board's verdict.
            Assert.Equal(
                new[] { exp.Id, ing.Id, pre.Id }.Order(),
                path.Streams.Select(s => s.PipelineId).Order());

            // Three fires, newest first: the lone run, then the two schedule fires.
            Assert.Equal(3, path.TotalFires);
            Assert.Null(path.OlderThanUtc);
            Assert.Equal(new[] { loneIng.ToString("D"), newerFire.ToString("D"), olderFire.ToString("D") }, path.Fires.Select(f => f.Key));
            Assert.Null(path.Fires[0].GroupId);

            var newer = path.Fires[1];
            Assert.Equal(newerFire, newer.GroupId);
            Assert.Equal(new[] { pre.Name, ing.Name, exp.Name }, newer.Runs.Select(r => r.Run.FlowName));

            var landed = newer.Runs[0];
            Assert.Equal(newerPre, landed.Run.RunId);
            Assert.NotNull(landed.Files);
            Assert.Equal(2, landed.Files.Files);
            Assert.Equal(15, landed.Files.Rows);
            Assert.Equal(150, landed.Files.Bytes);
            Assert.Equal(new DateTimeOffset(today.AddDays(-1).AddHours(1), TimeSpan.Zero), landed.Files.NewestModified);

            // The export's two parts are its parts, not runs of the fire, and its own result's counts ride along.
            var exported = newer.Runs[2];
            Assert.Equal(newerExp, exported.Run.RunId);
            Assert.Equal(2, exported.Parts);
            Assert.Null(exported.Files);
            Assert.NotNull(exported.Run.ResultCounts);
            Assert.Equal(1, exported.Run.ResultCounts["failed"]);

            // Paging back: the fires that started before the newest one.
            var before = path.Fires[0].StartedUtc.ToString("O", CultureInfo.InvariantCulture);
            var older = await GetJsonAsync<StreamPathDto>(
                client, token, $"/api/v1/datastreams/{ing.Id}/path?days=30&before={Uri.EscapeDataString(before)}");
            Assert.Equal(new[] { newerFire.ToString("D"), olderFire.ToString("D") }, older.Fires.Select(f => f.Key));
            Assert.Equal(3, older.TotalFires);

            using var missing = await SendAsync(client, token, $"/api/v1/datastreams/{Guid.NewGuid()}/path");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

            using var tooWide = await SendAsync(client, token, $"/api/v1/datastreams/{ing.Id}/path?days=999");
            Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == preTable || o.Key == preView || o.Key == arcTable).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static StreamPathNodeDto Node(StreamPathDto path, string id, string role)
    {
        var node = Assert.Single(path.Nodes, n => n.Id == id);
        Assert.Equal(role, node.Role);
        return node;
    }

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

    private static CatalogObject Object(string key, string database, string schema, string name, string kind, DateTime today) => new()
    {
        Key = key,
        ServerRef = "srv",
        Database = database,
        Schema = schema,
        Name = name,
        Kind = kind,
        FirstSeenUtc = today,
        LastSeenUtc = today,
    };

    private static CatalogLineageEdge Edge(
        Guid repoId, CatalogPipeline flow, string relation, string key, string name, string tier) => new()
    {
        RepoId = repoId,
        Flow = flow.Name,
        PipelineId = flow.Id,
        Relation = relation,
        ObjectKey = key,
        ObjectName = name,
        Tier = tier,
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
