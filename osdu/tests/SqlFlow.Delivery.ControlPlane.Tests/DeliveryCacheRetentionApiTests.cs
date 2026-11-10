using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The partition cache's retention as the control plane serves it (osdu/docs/reference/concepts/partition-cache.md,
/// Retention): a pruned version stays listed with when it was pruned and its counts, its records and a comparison with it
/// answer 410 Gone, and the cache listing says the retention each cache flow declares and the one the partition keeps,
/// where each type's values are read from, and how many rows a table type's capture takes.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryCacheRetentionApiTests
{
    private const string Flow = "cp-retention-cache";

    private static readonly DateTimeOffset Start = new(2026, 9, 1, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_pruned_version_is_listed_with_its_counts_and_its_records_are_gone()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var scope = "cp-ret-" + Guid.NewGuid().ToString("N")[..8];
        var store = new OsduCacheStore(() => SampleEstate.Context(cs));

        try
        {
            var labels = new List<string>();
            for (var day = 0; day < 4; day++)
            {
                var write = await store.MergeAsync(
                    scope, Flow,
                    [new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", [ReferenceItem.FromText(scope + ":reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Name"] = "metre-" + day })])],
                    new CacheCapture(null, "tests", "seeded"), Start.AddDays(day));
                labels.Add(write.Snapshot.Version);
            }

            var repoId = Guid.NewGuid();
            await using (var db = SampleEstate.Context(cs))
            {
                // Two lookup tables beside it: one taking the rows a table type takes by default, one naming its own maxRows.
                foreach (var (name, maxRows) in new[] { ("UnitAlias", (int?)null), ("CurveAlias", 250_000) })
                {
                    db.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
                    {
                        Id = Guid.NewGuid(),
                        RepoId = repoId,
                        FlowName = Flow,
                        Scope = scope,
                        Origin = CacheOrigins.TableText,
                        Connection = "${env:OSDU_DATA_DB}",
                        SourceObject = "OsduData.silver." + name,
                        KeyField = "source",
                        RelativePath = "cache/" + Flow + ".yaml",
                        Name = name,
                        EntityType = ReferenceType.LookupEntityType(name),
                        FieldsJson = "[{\"path\":\"target\",\"as\":\"target\"}]",
                        MaxRows = maxRows,
                        RetentionDays = 2,
                        FirstSeenUtc = Start.UtcDateTime,
                        LastSeenUtc = Start.UtcDateTime,
                    });
                }

                db.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
                {
                    Id = Guid.NewGuid(),
                    RepoId = repoId,
                    FlowName = Flow,
                    Scope = scope,
                    Endpoint = "https://osdu.example.test",
                    RelativePath = "cache/" + Flow + ".yaml",
                    Name = "UnitOfMeasure",
                    EntityType = "reference-data--UnitOfMeasure",
                    Kind = "osdu:wks:reference-data--UnitOfMeasure:1.0.0",
                    Query = "*",
                    FieldsJson = "[{\"path\":\"data.Name\",\"as\":\"Name\"}]",
                    FetchRecords = true,
                    RetentionDays = 2,
                    FirstSeenUtc = Start.UtcDateTime,
                    LastSeenUtc = Start.UtcDateTime,
                });
                await db.SaveChangesAsync();
            }

            // Ten days in, the current version and the one it replaced keep their records; the first two are pruned.
            var outcome = await store.ApplyRetentionAsync(CacheRetentionRequest.ForFlow(scope, Flow, 2, Start.AddDays(10), "tests"));
            Assert.True(
                outcome.Deferred is null,
                $"The retention waited ({outcome.Deferred}): an interrupted earlier run left an active interface row whose pin is not recorded in the test database. Running the module suite empties it.");
            Assert.Equal(labels.Take(2), outcome.Pruned);

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client, ["read"]);

            var versions = (await JsonAsync(client, token, $"/api/v1/delivery/cache/versions?scope={scope}")).EnumerateArray().ToList();
            Assert.Equal(4, versions.Count);
            foreach (var version in versions)
            {
                var pruned = labels.Take(2).Contains(version.GetProperty("version").GetString());
                Assert.Equal(pruned ? JsonValueKind.String : JsonValueKind.Null, version.GetProperty("prunedUtc").ValueKind);
            }

            // The history keeps every version's counts; the version after the last pruned one says its predecessor is gone.
            var history = (await JsonAsync(client, token, $"/api/v1/delivery/cache/history?scope={scope}")).EnumerateArray().ToList();
            Assert.Equal(4, history.Count);
            Assert.All(history.Where(h => h.GetProperty("before").ValueKind == JsonValueKind.String), h => Assert.Equal(1, h.GetProperty("changed").GetInt64()));
            Assert.Equal(
                [false, true, true, false],
                history.OrderBy(h => h.GetProperty("version").GetProperty("sequence").GetInt32()).Select(h => h.GetProperty("beforePruned").GetBoolean()));

            using (var items = await SendAsync(client, token, $"/api/v1/delivery/cache/items?scope={scope}&version={labels[0]}"))
            {
                Assert.Equal(HttpStatusCode.Gone, items.StatusCode);
                Assert.Contains($"Version {labels[0]} of the cache of partition '{scope}' was pruned", await items.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var diff = await SendAsync(client, token, $"/api/v1/delivery/cache/diff?scope={scope}&from={labels[1]}"))
            {
                Assert.Equal(HttpStatusCode.Gone, diff.StatusCode);
            }

            // A version the cache never held is still not found, and the kept ones still read and compare.
            using (var unknown = await SendAsync(client, token, $"/api/v1/delivery/cache/items?scope={scope}&version=19990101T000000Z"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }

            var kept = await JsonAsync(client, token, $"/api/v1/delivery/cache/items?scope={scope}&version={labels[2]}");
            Assert.Equal(1, kept.GetProperty("total").GetInt32());
            var compared = await JsonAsync(client, token, $"/api/v1/delivery/cache/diff?scope={scope}&from={labels[2]}");
            Assert.Equal(1, compared.GetProperty("changed").GetInt64());

            // The cache listing says the retention the flow declares and the one the partition keeps, and that the type's
            // records are read whole from storage, by the type and by the flow asking for it.
            var cache = Assert.Single((await JsonAsync(client, token, "/api/v1/delivery/caches")).EnumerateArray(), c => c.GetProperty("scope").GetString() == scope);
            Assert.Equal(2, cache.GetProperty("retentionDays").GetInt32());
            Assert.Equal(2, Assert.Single(cache.GetProperty("flows").EnumerateArray()).GetProperty("retentionDays").GetInt32());
            var types = cache.GetProperty("types").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t);
            Assert.Equal(["CurveAlias", "UnitAlias", "UnitOfMeasure"], types.Keys.Order(StringComparer.Ordinal));
            var type = types["UnitOfMeasure"];
            Assert.True(type.GetProperty("fetchRecords").GetBoolean());
            var units = Assert.Single(type.GetProperty("sources").EnumerateArray());
            Assert.True(units.GetProperty("fetchRecords").GetBoolean());

            // A table type's source says the most rows its capture takes: what its flow names, or the default; others say none.
            Assert.False(units.TryGetProperty("maxRows", out var none) && none.ValueKind != JsonValueKind.Null);
            Assert.Equal(1_000_000, Assert.Single(types["UnitAlias"].GetProperty("sources").EnumerateArray()).GetProperty("maxRows").GetInt32());
            Assert.Equal(250_000, Assert.Single(types["CurveAlias"].GetProperty("sources").EnumerateArray()).GetProperty("maxRows").GetInt32());
        }
        finally
        {
            await using var db = SampleEstate.Context(cs);
            await db.DeliveryCacheItems.Where(i => i.Scope == scope).ExecuteDeleteAsync();
            await db.DeliveryCacheMembers.Where(m => m.Scope == scope).ExecuteDeleteAsync();
            await db.DeliveryCacheVersions.Where(v => v.Scope == scope).ExecuteDeleteAsync();
            await db.DeliveryCacheDefinitions.Where(d => d.Scope == scope).ExecuteDeleteAsync();
        }
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

    [Fact]
    public async Task An_admin_purges_the_history_after_a_preview_an_operator_may_see()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var scope = "cp-purge-" + Guid.NewGuid().ToString("N")[..8];
        var store = new OsduCacheStore(() => SampleEstate.Context(cs));

        try
        {
            var labels = new List<string>();
            for (var day = 0; day < 4; day++)
            {
                var write = await store.MergeAsync(
                    scope, Flow,
                    [new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", [ReferenceItem.FromText(scope + ":reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Name"] = "metre-" + day })])],
                    new CacheCapture(null, "tests", "seeded"), Start.AddDays(day));
                labels.Add(write.Snapshot.Version);
            }

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var operate = await TokenAsync(client, ["read", "operate"]);
            var admin = await TokenAsync(client, ["admin"]);
            var body = new DeliveryCachePruneRequest(scope, 0);

            // An operator sees what keeping no days would prune, and nothing is pruned by looking.
            using (var preview = await PostAsync(client, operate, "/api/v1/delivery/cache/prune/preview", body))
            {
                var text = await preview.Content.ReadAsStringAsync();
                Assert.True(preview.StatusCode == HttpStatusCode.OK, text);
                var result = JsonDocument.Parse(text).RootElement;
                Assert.True(result.GetProperty("preview").GetBoolean());
                Assert.Equal(labels.Take(2), result.GetProperty("pruned").EnumerateArray().Select(v => v.GetString()));
                Assert.StartsWith("Pruning, keeping only the current version", result.GetProperty("summary").GetString(), StringComparison.Ordinal);
            }

            Assert.DoesNotContain(await store.ListVersionsAsync(scope), v => v.Pruned);

            // The purge itself is an admin's.
            using (var refused = await PostAsync(client, operate, "/api/v1/delivery/cache/prune", body))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }

            using (var purged = await PostAsync(client, admin, "/api/v1/delivery/cache/prune", body))
            {
                var text = await purged.Content.ReadAsStringAsync();
                Assert.True(purged.StatusCode == HttpStatusCode.OK, text);
                var result = JsonDocument.Parse(text).RootElement;
                Assert.False(result.GetProperty("preview").GetBoolean());
                Assert.Equal(2, result.GetProperty("pruned").GetArrayLength());
                Assert.StartsWith("Pruned the records of 2 version(s)", result.GetProperty("summary").GetString(), StringComparison.Ordinal);
            }

            // Each pruned version says who pruned it, as the versions listing answers it.
            var versions = (await JsonAsync(client, operate, $"/api/v1/delivery/cache/versions?scope={scope}")).EnumerateArray().ToList();
            var pruned = versions.Where(v => v.GetProperty("prunedUtc").ValueKind == JsonValueKind.String).ToList();
            Assert.Equal(2, pruned.Count);
            Assert.All(pruned, v => Assert.False(string.IsNullOrWhiteSpace(v.GetProperty("prunedBy").GetString())));

            // Days out of range, and a partition with no cache, are refused.
            using (var negative = await PostAsync(client, admin, "/api/v1/delivery/cache/prune/preview", new DeliveryCachePruneRequest(scope, -1)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
            }

            using (var nowhere = await PostAsync(client, admin, "/api/v1/delivery/cache/prune/preview", new DeliveryCachePruneRequest("cp-purge-nowhere", 3)))
            {
                Assert.Equal(HttpStatusCode.NotFound, nowhere.StatusCode);
            }
        }
        finally
        {
            await using var db = SampleEstate.Context(cs);
            await db.DeliveryCacheItems.Where(i => i.Scope == scope).ExecuteDeleteAsync();
            await db.DeliveryCacheMembers.Where(m => m.Scope == scope).ExecuteDeleteAsync();
            await db.DeliveryCacheVersions.Where(v => v.Scope == scope).ExecuteDeleteAsync();
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
