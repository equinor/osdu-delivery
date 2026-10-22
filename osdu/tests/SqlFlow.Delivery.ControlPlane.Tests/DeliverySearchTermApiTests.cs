using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.SearchTerms;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The search terms through the API (osdu/docs/reference/concepts/search-terms.md): the terms the sync extracted from
/// the sample estate's mapping listed for a kind, refined by an author and by no one else, reset; and a search naming a
/// term, which reaches the explorer as the condition on the record the term's route fills, in the partition read. Gated
/// on a reachable database.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliverySearchTermApiTests
{
    private const string WellLog = "work-product-component--WellLog";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>A term of the log's own table, as the sample flow reads it.</summary>
    private static Guid IdOf(string column) => SearchTermKey.Of("OsduData.silver.WellLog", column).Id;

    [Fact]
    public async Task Terms_are_listed_for_a_kind_refined_by_an_author_and_searched_through_their_route()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var partition = "t" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-search-terms-" + suffix);
        var flow = "search-terms-" + suffix;
        var now = DateTime.UtcNow;
        var sample = Path.Combine(AppContext.BaseDirectory, "samples", SampleEstate.SourceFolder);

        // The sample estate synced into the module, as a repository sync writes it; a delivery flow reading the log's table
        // with the log's mapping, which the terms are written from again when the control plane starts; and the partition
        // read, registered with its own connection.
        await using (var osdu = SampleEstate.Context(cs))
        {
            await new DeliveryCatalogSync(new DeliveryDocumentLoader()).ReconcileAsync(osdu, repoId, sample, now, new List<string>(), CancellationToken.None);
            await osdu.DeliverySearchTermRefinements.Where(r => r.TermId == IdOf("wellbore_uwi") || r.TermId == IdOf("log_run") || r.TermId == IdOf("index_type")).ExecuteDeleteAsync();
        }

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo { Id = repoId, Name = "cp-search-terms-" + suffix, RemoteUrl = "https://example/search-terms.git", RootPath = sample, FirstSeenUtc = now, LastSyncUtc = now });
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = CatalogIdentity.Pipeline(repoId, flow),
                RepoId = repoId,
                Name = flow,
                Kind = "delivery",
                RelativePath = "flows/" + flow + ".yaml",
                ContentHash = new string('0', 64),
                Yaml = $$"""
                    flowType: delivery
                    name: {{flow}}
                    source:
                      connection: ${env:OSDU_DATA_DB}
                      record: { object: OsduData.silver.WellLog, key: [source_project, log_id] }
                      work: ../.work/search-terms
                    render:
                      mapping: WellLog@1.4.0
                    target:
                      endpoint: ${env:OSDU_URL}
                      protocol: storage
                    """,
                DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{flow}}","flowKind":"delivery"}"""),
                Active = true,
                Wave = 0,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            await db.SaveChangesAsync();
        }

        var operations = new RecordedOperations();
        try
        {
            await TestPartitions.RegisterAsync(cs, partition);
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithServices(services => services.AddSingleton(operations.Registry()));
            using var client = factory.CreateClient();
            var reader = await TokenAsync(client, "read", "operate");
            var author = await TokenAsync(client, "read", "author");

            // Listed for a kind of one type, by the column's name; a kind of every type has none.
            var listed = await ReadAsync<DeliverySearchTermsDto>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/search-terms?kind=*:*:work-product-component--WellLog:*"));
            Assert.Equal(WellLog, listed.EntityType);
            var wellbore = Assert.Single(listed.Terms, t => t.Id == IdOf("wellbore_uwi"));
            Assert.Equal(("WellLog.wellbore_uwi", $"{WellLog}|osdu.data.WellboreID|Search", "search"), (wellbore.Name, wellbore.Route, wellbore.Routes[0].Kind));
            Assert.Equal(("OsduData.silver.WellLog", "WellLog", "wellbore_uwi"), (wellbore.Source, wellbore.Table, wellbore.Column));
            Assert.Empty((await ReadAsync<DeliverySearchTermsDto>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/search-terms?kind=*:*:*:*"))).Terms);
            var types = await ReadAsync<List<DeliverySearchTermTypeDto>>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/search-terms/entity-types"));
            Assert.Contains(types, t => t.EntityType == WellLog && t.Terms > 10);

            // Refining is an author action, which every signed-in user may take and no anonymous caller may; an unknown term is
            // not found, and a name two terms would share is refused.
            using (var anonymous = await client.PutAsJsonAsync(new Uri($"/api/v1/delivery/search-terms/{IdOf("wellbore_uwi")}", UriKind.Relative), new { name = "Wellbore name" }, Web))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }

            using (var unknown = await SendAsync(client, author, HttpMethod.Put, $"/api/v1/delivery/search-terms/{Guid.NewGuid()}", new { name = "Nothing" }))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }

            var renamed = await ReadAsync<SearchTermView>(await SendAsync(client, author, HttpMethod.Put, $"/api/v1/delivery/search-terms/{IdOf("wellbore_uwi")}", new { name = "Wellbore name", note = "The UWI the well database files the log under." }));
            Assert.Equal(("Wellbore name", true), (renamed.Name, renamed.Renamed));
            using (var taken = await SendAsync(client, author, HttpMethod.Put, $"/api/v1/delivery/search-terms/{IdOf("log_run")}", new { name = "wellbore NAME" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, taken.StatusCode);
                Assert.Contains("is the search term WellLog.wellbore_uwi's already", await taken.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // A search naming a term reaches the explorer as the condition on what the term's route fills, in the partition read.
            var searched = await RanAsync(client, reader, $"/api/v1/delivery/explorer/search?partition={partition}", operations, new
            {
                kind = "*:*:work-product-component--WellLog:*",
                filters = new object[]
                {
                    new { term = IdOf("wellbore_uwi"), condition = "is", value = "WB D/4-A-30" },
                    new { term = IdOf("index_type"), condition = "anyOf", values = new[] { "DEPTH" } },
                },
            });
            var search = ExplorerSearch.Parse(searched.Argument(ExploreOperation.SearchArgument));
            Assert.Equal(("data.WellboreID", "Wellbore name", "osdu:wks:master-data--Wellbore:*"), (search.Filters[0].Path, search.Filters[0].Via!.Term, search.Filters[0].Via!.Kind));
            Assert.Equal(["" + partition + ":reference-data--WellLogSamplingDomainType:Depth:"], search.Filters[1].Values!);

            // A term deleted from the search, and a value its route cannot carry, are refused before anything is read.
            using (var excluded = await SendAsync(client, author, HttpMethod.Put, $"/api/v1/delivery/search-terms/{IdOf("index_type")}", new { excluded = true }))
            {
                Assert.Equal(HttpStatusCode.OK, excluded.StatusCode);
            }

            await RefusedAsync(client, reader, $"/api/v1/delivery/explorer/search?partition={partition}", new { filters = new[] { new { term = IdOf("index_type"), condition = "is", value = "DEPTH" } } }, "is deleted from the search terms");
            await RefusedAsync(client, reader, $"/api/v1/delivery/explorer/search?partition={partition}", new { filters = new[] { new { term = IdOf("index_min"), condition = "is", value = "deep" } } }, "cannot be searched for");

            // Reset: the term is searched as its mapping gives it again; a term with nothing made of it has nothing to reset.
            using (var reset = await SendAsync(client, author, HttpMethod.Delete, $"/api/v1/delivery/search-terms/{IdOf("wellbore_uwi")}/refinement"))
            {
                Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
            }

            using (var again = await SendAsync(client, author, HttpMethod.Delete, $"/api/v1/delivery/search-terms/{IdOf("wellbore_uwi")}/refinement"))
            {
                Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
            }

            using (var included = await SendAsync(client, author, HttpMethod.Delete, $"/api/v1/delivery/search-terms/{IdOf("index_type")}/refinement"))
            {
                Assert.Equal(HttpStatusCode.NoContent, included.StatusCode);
            }

            var plain = await ReadAsync<SearchTermView>(await SendAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/search-terms/{IdOf("wellbore_uwi")}"));
            Assert.Equal(("WellLog.wellbore_uwi", false), (plain.Name, plain.Renamed));

            // Several deleted in one request: each term a pipeline reads leaves the search, listed as deleted, and an id that is no
            // term is reported missing; restored in one request, they are offered again. Nothing named is refused.
            using (var anonymous = await client.PostAsJsonAsync(new Uri("/api/v1/delivery/search-terms/delete", UriKind.Relative), new { terms = new[] { IdOf("log_run") } }, Web))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }

            var nothing = Guid.NewGuid();
            var deletion = await ReadAsync<SearchTermDeletion>(await SendAsync(
                client, author, HttpMethod.Post, "/api/v1/delivery/search-terms/delete", new { terms = new[] { IdOf("log_run"), IdOf("index_type"), nothing }, entityType = WellLog }));
            Assert.Equal([IdOf("log_run"), IdOf("index_type")], deletion.Deleted);
            Assert.Equal([nothing], deletion.Missing);
            var deleted = await ReadAsync<DeliverySearchTermsDto>(await SendAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/search-terms?entityType={WellLog}"));
            Assert.True(Assert.Single(deleted.Terms, t => t.Id == IdOf("log_run")).Excluded);
            await RefusedAsync(client, reader, $"/api/v1/delivery/explorer/search?partition={partition}", new { filters = new[] { new { term = IdOf("index_type"), condition = "is", value = "DEPTH" } } }, "is deleted from the search terms");

            var restoration = await ReadAsync<SearchTermRestoration>(await SendAsync(
                client, author, HttpMethod.Post, "/api/v1/delivery/search-terms/restore", new { terms = new[] { IdOf("log_run"), IdOf("index_type") } }));
            Assert.Equal([IdOf("log_run"), IdOf("index_type")], restoration.Restored);
            var offered = await ReadAsync<DeliverySearchTermsDto>(await SendAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/search-terms?entityType={WellLog}"));
            Assert.False(Assert.Single(offered.Terms, t => t.Id == IdOf("index_type")).Excluded);
            using (var none = await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/search-terms/delete", new { terms = Array.Empty<Guid>() }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
            }
        }
        finally
        {
            await TestPartitions.ForgetAsync(cs, partition);
            await using var db = CatalogDatabase.Create(cs);
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliverySearchTerms.Where(t => t.RepoId == repoId).ExecuteDeleteAsync();
            await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
            await osdu.DeliveryMappings.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            await osdu.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    private static async Task<SqlFlow.Core.Compute.ComputeTaskPayload> RanAsync(HttpClient client, string token, string path, RecordedOperations operations, object body)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST {path} answered {(int)response.StatusCode}: {text}");
        return operations.Last();
    }

    private static async Task RefusedAsync(HttpClient client, string token, string path, object body, string why)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"POST {path} answered {(int)response.StatusCode}: {text}");
        Assert.Contains(why, text, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Web);
        }

        return await client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.StatusCode}: {text}");
            var value = JsonSerializer.Deserialize<T>(text, Web);
            Assert.NotNull(value);
            return value;
        }
    }

    private static async Task<string> TokenAsync(HttpClient client, params string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/auth/token", UriKind.Relative), new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
