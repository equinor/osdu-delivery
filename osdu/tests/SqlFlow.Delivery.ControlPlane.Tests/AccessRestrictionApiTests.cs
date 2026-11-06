using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The access restriction registry as the control plane serves it (osdu/docs/reference/concepts/access-restriction.md, The
/// registry): read by anyone signed in, kept by administrators, refusing what storage would not take in an ACL, a name taken
/// in its partition and a partition the registry does not hold, and saying what a restriction is for without touching its
/// groups.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class AccessRestrictionApiTests
{
    private const string Hold = "data.restricted.owners@dev.dataservices.energy";

    [Fact]
    public async Task Administrators_keep_the_registry_and_anyone_signed_in_reads_it()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var partition = "ar" + Guid.NewGuid().ToString("N")[..10];
        await using (var osdu = SampleEstate.Context(cs))
        {
            var now = DateTime.UtcNow;
            osdu.DeliveryPartitions.Add(new DeliveryPartition { Name = partition, CreatedBy = "tests", UpdatedBy = "tests", CreatedUtc = now, UpdatedUtc = now });
            await osdu.SaveChangesAsync();
        }

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var operate = await TokenAsync(client, ["read", "operate"]);
            var admin = await TokenAsync(client, ["admin"]);
            var hold = new DeliveryAccessRestrictionAddRequest(partition, "legal-hold", "Records under legal review", [Hold], [Hold]);

            using (var refused = await SendAsync(client, operate, HttpMethod.Post, "/api/v1/delivery/access-restrictions", hold))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            }

            int id;
            using (var added = await SendAsync(client, admin, HttpMethod.Post, "/api/v1/delivery/access-restrictions", hold))
            {
                var body = await added.Content.ReadAsStringAsync();
                Assert.True(added.StatusCode == HttpStatusCode.Created, body);
                var row = JsonDocument.Parse(body).RootElement;
                id = row.GetProperty("restrictionId").GetInt32();
                Assert.Equal((partition, "legal-hold", 0L), (row.GetProperty("partition").GetString(), row.GetProperty("name").GetString(), row.GetProperty("records").GetInt64()));
                Assert.Equal([Hold], row.GetProperty("owners").EnumerateArray().Select(o => o.GetString()));
            }

            using (var twice = await SendAsync(client, admin, HttpMethod.Post, "/api/v1/delivery/access-restrictions", hold with { Name = "LEGAL-HOLD" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);
                Assert.Contains("already", await twice.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var notGroup = await SendAsync(client, admin, HttpMethod.Post, "/api/v1/delivery/access-restrictions", hold with { Name = "other", Owners = ["users.data.root@dev.dataservices.energy"] }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, notGroup.StatusCode);
                Assert.Contains("not a data group", await notGroup.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            using (var nowhere = await SendAsync(client, admin, HttpMethod.Post, "/api/v1/delivery/access-restrictions", hold with { Partition = partition + "x" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, nowhere.StatusCode);
                Assert.Contains("is not registered", await nowhere.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            var listed = await JsonAsync(client, operate, $"/api/v1/delivery/access-restrictions?partition={partition}");
            Assert.Equal(["legal-hold"], listed.EnumerateArray().Select(r => r.GetProperty("name").GetString()));

            using (var described = await SendAsync(client, admin, HttpMethod.Put, $"/api/v1/delivery/access-restrictions/{id}", new DeliveryAccessRestrictionDescribeRequest("Kept while counsel reviews")))
            {
                var body = await described.Content.ReadAsStringAsync();
                Assert.True(described.StatusCode == HttpStatusCode.OK, body);
                var row = JsonDocument.Parse(body).RootElement;
                Assert.Equal("Kept while counsel reviews", row.GetProperty("description").GetString());
                Assert.Equal([Hold], row.GetProperty("viewers").EnumerateArray().Select(o => o.GetString()));
            }

            using (var removed = await SendAsync(client, admin, HttpMethod.Delete, $"/api/v1/delivery/access-restrictions/{id}"))
            {
                Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            }

            using (var gone = await SendAsync(client, admin, HttpMethod.Delete, $"/api/v1/delivery/access-restrictions/{id}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            }

            Assert.Empty((await JsonAsync(client, operate, $"/api/v1/delivery/access-restrictions?partition={partition}")).EnumerateArray());
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryAccessRestrictions.Where(a => a.Partition.StartsWith(partition)).ExecuteDeleteAsync();
            await osdu.DeliveryPartitions.Where(p => p.Name == partition).ExecuteDeleteAsync();
        }
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes.ToArray()));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
