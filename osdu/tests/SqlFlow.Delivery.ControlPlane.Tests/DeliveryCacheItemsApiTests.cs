using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The Cache page's listing of one version's records (osdu/docs/reference/concepts/partition-cache.md, Where to see it): a
/// page is picked by row id from the index that orders a type's records, then read whole, so every page of a type, or of
/// every type, holds the records the order puts there, and a search narrows the pages as it narrows the total.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryCacheItemsApiTests
{
    private const string Flow = "cp-items-cache";

    [Fact]
    public async Task Every_page_of_a_version_holds_the_records_the_order_puts_there_and_only_the_version_s()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var scope = "cp-items-" + Guid.NewGuid().ToString("N")[..8];
        var store = new OsduCacheStore(() => SampleEstate.Context(cs));
        var start = new DateTimeOffset(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);

        try
        {
            // Seven wellbores and three units, then a second version in which one wellbore changed and one left: the
            // listing of the second version holds the changed record once and the one that left not at all.
            var first = Wellbores(scope, 1, 7, "well");
            await store.MergeAsync(scope, Flow, [first, Units(scope)], new CacheCapture(null, "tests", "seeded"), start);
            var second = new ReferenceType(
                "Wellbore", "master-data--Wellbore", first.Items.Where(i => !i.Id.EndsWith(":7", StringComparison.Ordinal)).Select(i => i.Id.EndsWith(":3", StringComparison.Ordinal) ? Wellbore(scope, 3, "renamed") : i));
            var current = await store.MergeAsync(scope, Flow, [second, Units(scope)], new CacheCapture(null, "tests", "seeded"), start.AddDays(1));

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client, ["read"]);

            var wellbores = await PagesAsync(client, token, $"/api/v1/delivery/cache/items?scope={scope}&type=Wellbore", 4);
            Assert.Equal(6, wellbores.Total);
            Assert.Equal(Enumerable.Range(1, 6).Select(n => $"{scope}:master-data--Wellbore:{n}"), wellbores.Ids);
            Assert.Equal("renamed 3", wellbores.Names[2]);
            Assert.All(wellbores.Versions, v => Assert.Equal(current.Snapshot.Version, v));

            var all = await PagesAsync(client, token, $"/api/v1/delivery/cache/items?scope={scope}", 4);
            Assert.Equal(9, all.Total);
            Assert.Equal(Enumerable.Repeat("Units", 3).Concat(Enumerable.Repeat("Wellbore", 6)), all.Types);

            var searched = await PagesAsync(client, token, $"/api/v1/delivery/cache/items?scope={scope}&search=renamed", 4);
            Assert.Equal(1, searched.Total);
            Assert.Equal([$"{scope}:master-data--Wellbore:3"], searched.Ids);
        }
        finally
        {
            await using var db = SampleEstate.Context(cs);
            await db.DeliveryCacheItems.Where(i => i.Scope == scope).ExecuteDeleteAsync();
            await db.DeliveryCacheMembers.Where(m => m.Scope == scope).ExecuteDeleteAsync();
            await db.DeliveryCacheVersions.Where(v => v.Scope == scope).ExecuteDeleteAsync();
        }
    }

    private static ReferenceType Wellbores(string scope, int from, int to, string name)
        => new("Wellbore", "master-data--Wellbore", Enumerable.Range(from, to - from + 1).Select(n => Wellbore(scope, n, name)));

    private static ReferenceItem Wellbore(string scope, int number, string name)
        => ReferenceItem.FromText($"{scope}:master-data--Wellbore:{number}", new Dictionary<string, string> { ["FacilityName"] = $"{name} {number}" });

    private static ReferenceType Units(string scope)
        => new("Units", "lookup--Units", new[] { "ft", "m", "s" }.Select(code => ReferenceItem.FromText(code, new Dictionary<string, string> { ["code"] = code, ["partition"] = scope })), "code");

    /// <summary>Every page of a listing, <paramref name="size"/> records each, read until a page comes back short.</summary>
    private static async Task<Listing> PagesAsync(HttpClient client, string token, string path, int size)
    {
        var listing = new Listing();
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(client, token, $"{path}&page={page}&pageSize={size}");
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} page {page} answered {(int)response.StatusCode}: {body}");
            var root = JsonDocument.Parse(body).RootElement;
            listing.Total = root.GetProperty("total").GetInt32();
            var items = root.GetProperty("items").EnumerateArray().ToList();
            foreach (var item in items)
            {
                listing.Ids.Add(item.GetProperty("recordId").GetString()!);
                listing.Types.Add(item.GetProperty("typeName").GetString()!);
                listing.Versions.Add(item.GetProperty("version").GetString()!);
                listing.Names.Add(item.GetProperty("fields").TryGetProperty("FacilityName", out var name) ? name.GetString()! : string.Empty);
            }

            if (items.Count < size)
            {
                return listing;
            }
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
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

    private sealed class Listing
    {
        public int Total { get; set; }

        public List<string> Ids { get; } = [];

        public List<string> Types { get; } = [];

        public List<string> Versions { get; } = [];

        public List<string> Names { get; } = [];
    }
}
