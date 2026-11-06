using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
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

    [Fact]
    public async Task The_dialog_asks_whether_the_flow_can_lift_each_of_its_partitions_restrictions_and_a_route_that_cannot_restrict_is_refused_before_queueing()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var partition = "ac" + suffix;
        var flowName = "api-access-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-access-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.silver.WellLog, key: [source_project, log_id] }
              work: ../.work
            render:
              mapping: WellLog@1.4.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: ddms
              headers: { data-partition-id: {{partition}} }
              protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
            """;

        int legal, quarantine;
        await using (var osdu = SampleEstate.Context(cs))
        {
            osdu.DeliveryPartitions.Add(new DeliveryPartition { Name = partition, CreatedBy = "tests", UpdatedBy = "tests", CreatedUtc = now, UpdatedUtc = now });
            await osdu.SaveChangesAsync();
            var groups = AccessRestrictionState.GroupsJson([Hold]);
            var first = new DeliveryAccessRestriction { Partition = partition, Name = "quarantine", Viewers = groups, Owners = groups, CreatedUtc = now, CreatedBy = "tests", UpdatedUtc = now, UpdatedBy = "tests" };
            var second = new DeliveryAccessRestriction { Partition = partition, Name = "legal-hold", Viewers = groups, Owners = groups, CreatedUtc = now, CreatedBy = "tests", UpdatedUtc = now, UpdatedBy = "tests" };
            osdu.DeliveryAccessRestrictions.AddRange(first, second);
            await osdu.SaveChangesAsync();
            (legal, quarantine) = (second.RestrictionId, first.RestrictionId);
        }

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo { Id = repoId, Name = "cp-access-" + suffix, RemoteUrl = "https://example/cp-access.git", RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now });
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = pipelineId, RepoId = repoId, Name = flowName, Kind = "delivery", RelativePath = "flows/" + flowName + ".yaml",
                ContentHash = new string('0', 64), Yaml = yaml, DefinitionJson = $$"""{"name":"{{flowName}}","flowKind":"delivery"}""",
                Active = true, Wave = 0, FirstSeenUtc = now, LastSeenUtc = now,
            });
            await db.SaveChangesAsync();
        }

        var operations = new RecordedOperations();
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithServices(services => services.AddSingleton(operations.Registry()));
            using var client = factory.CreateClient();
            var operate = await TokenAsync(client, ["read", "operate"]);

            // The check is run in this process for the partition's restrictions, by name, under the flow's own credentials.
            using (var checkedOnes = await SendAsync(client, operate, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/access-check"))
            {
                var body = await checkedOnes.Content.ReadAsStringAsync();
                Assert.True(checkedOnes.StatusCode == HttpStatusCode.OK, body);
            }

            var given = operations.Last();
            Assert.Equal((CheckAccessRestrictionsOperation.OperationName, flowName), (given.Operation, given.SourceRef));
            Assert.Equal($"{legal},{quarantine}", given.Argument(CheckAccessRestrictionsOperation.RestrictionsArgument));

            // A restriction the route cannot make (the collection is not known while no mapping of the flow is synced) is refused
            // before a run is queued, in the words the dialog shows.
            using (var unrestricted = await SendAsync(
                client, operate, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/records/remove",
                new DeliveryRemovalRequest("restrict", [Guid.NewGuid()], null, null, Restriction: "legal-hold")))
            {
                var body = await unrestricted.Content.ReadAsStringAsync();
                Assert.True(unrestricted.StatusCode == HttpStatusCode.BadRequest, body);
                Assert.Contains("cannot restrict its records' access", body, StringComparison.Ordinal);
                Assert.Contains("Nothing was queued", body, StringComparison.Ordinal);
            }

            // A partition that registers none is answered with none, and nothing is asked of OSDU.
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryAccessRestrictions.Where(a => a.Partition == partition).ExecuteDeleteAsync();
            }

            var none = await JsonAsync(client, operate, $"/api/v1/delivery/flows/{pipelineId:D}/access-check", HttpMethod.Post);
            Assert.Empty(none.GetProperty("restrictions").EnumerateArray());
            Assert.Empty(operations.Given);
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryAccessRestrictions.Where(a => a.Partition == partition).ExecuteDeleteAsync();
                await osdu.DeliveryPartitions.Where(p => p.Name == partition).ExecuteDeleteAsync();
            }

            await using var db = CatalogDatabase.Create(cs);
            await db.Pipelines.Where(p => p.Id == pipelineId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path, HttpMethod? method = null)
    {
        using var response = await SendAsync(client, token, method ?? HttpMethod.Get, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method ?? HttpMethod.Get} {path} answered {(int)response.StatusCode}: {body}");
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
