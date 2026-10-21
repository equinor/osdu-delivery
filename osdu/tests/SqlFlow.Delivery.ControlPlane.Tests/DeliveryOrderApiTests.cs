using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The OSDU order through the API (osdu/docs/reference/concepts/osdu-order.md): a release of the OSDU data definitions built
/// into OSDU's lineage by a signed-in user, read back entity by entity and drawn around an entity type; the
/// order of a partition with every repository's deliveries; and which schedules run best before which, with how their
/// fires fall. Gated on a reachable database.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryOrderApiTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task An_operator_builds_a_release_s_lineage_and_a_reader_draws_it_around_an_entity_type()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var release = $"v0.{Random.Shared.Next(100, 999)}.{Random.Shared.Next(100, 999)}";
        using var handler = new ReleaseHandler(release);
        using var http = new HttpClient(handler, disposeHandler: false);
        var cache = Path.Combine(Path.GetTempPath(), "sqlflow_order_" + Guid.NewGuid().ToString("N"));
        var definitions = new OsduDataDefinitions(
            () => http, OsduDataDefinitions.DefaultApiUrl, OsduDataDefinitions.DefaultWebUrl, cache, Timeout.InfiniteTimeSpan, TimeSpan.FromMinutes(1), TimeProvider.System);
        try
        {
            await using var factory = Factory(cs).WithServices(services => services.AddSingleton(definitions));
            using var client = factory.CreateClient();
            var reader = await TokenAsync(client, "read");
            var operate = await TokenAsync(client, "read", "operate");

            Assert.DoesNotContain(await ReadAsync<List<DeliveryOrderReleaseDto>>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/order/releases")), r => r.Release == release);
            using (var unbuilt = await SendAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/order/releases/{release}/entities"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unbuilt.StatusCode);
            }

            // Building is a signed-in user's action.
            using (var anonymous = await client.PostAsJsonAsync(new Uri("/api/v1/delivery/order/releases", UriKind.Relative), new DeliveryOrderBuildRequest(release)))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }

            var built = await ReadAsync<DeliveryOrderReleaseDto>(await SendAsync(client, operate, HttpMethod.Post, "/api/v1/delivery/order/releases", new DeliveryOrderBuildRequest(release)));
            Assert.Equal((release, ReleaseHandler.Commit, 3, 3), (built.Release, built.Commit, built.Kinds, built.Relationships));
            Assert.Equal("osdu:wks:master-data--Broken:1.0.0", Assert.Single(built.Failures).Kind);
            Assert.Contains(await ReadAsync<List<DeliveryOrderReleaseDto>>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/order/releases")), r => r.Release == release);

            var entities = await ReadAsync<DeliveryOrderEntitiesDto>(await SendAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/order/releases/{release}/entities"));
            Assert.Equal(["dataset", "master-data--Well", "master-data--Wellbore", "work-product-component--WellLog"], entities.Entities.Select(e => e.EntityType));
            var log = entities.Entities.Single(e => e.EntityType == "work-product-component--WellLog");
            Assert.Equal((2, 0, "PUBLISHED"), (log.RefersTo, log.ReferredBy, Assert.Single(log.Kinds).Status));

            // Around the wellbores: what they refer to and what refers to them, one step out.
            var graph = await ReadAsync<DeliveryOrderGraphDto>(await SendAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/order/releases/{release}/graph?entity=master-data--wellbore"));
            Assert.Equal(["master-data--Wellbore", "master-data--Well", "work-product-component--WellLog"], graph.Nodes.Select(n => n.EntityType));
            Assert.Equal(
                ["master-data--Wellbore>master-data--Well:osdu.data.WellID", "work-product-component--WellLog>master-data--Wellbore:osdu.data.WellboreID"],
                graph.Edges.Select(e => $"{e.From}>{e.To}:{string.Join(",", e.Properties)}"));
            var outward = await ReadAsync<DeliveryOrderGraphDto>(await SendAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/order/releases/{release}/graph?entity=work-product-component--WellLog&depth=2&direction=out"));
            Assert.Equal(["work-product-component--WellLog", "dataset", "master-data--Wellbore", "master-data--Well"], outward.Nodes.Select(n => n.EntityType));
            Assert.False(outward.Truncated);

            foreach (var (url, status) in new[]
            {
                ($"/api/v1/delivery/order/releases/{release}/graph", HttpStatusCode.BadRequest),
                ($"/api/v1/delivery/order/releases/{release}/graph?entity=master-data--Well&depth=9", HttpStatusCode.BadRequest),
                ($"/api/v1/delivery/order/releases/{release}/graph?entity=master-data--Well&direction=up", HttpStatusCode.BadRequest),
                ($"/api/v1/delivery/order/releases/{release}/graph?entity=master-data--Rig", HttpStatusCode.NotFound),
            })
            {
                using var response = await SendAsync(client, reader, HttpMethod.Get, url);
                Assert.Equal(status, response.StatusCode);
            }
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                var ids = await osdu.SchemaLineages.Where(l => l.Release == release).Select(l => l.Id).ToListAsync();
                await osdu.SchemaLineageKinds.Where(k => ids.Contains(k.LineageId)).ExecuteDeleteAsync();
                await osdu.SchemaLineages.Where(l => ids.Contains(l.Id)).ExecuteDeleteAsync();
            }

            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_reader_sees_a_partition_s_order_and_which_schedules_run_best_before_which()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var partition = "o" + suffix;
        var masters = FlowIdentity.FromName("repo/cp-order-masters-" + suffix);
        var estate = FlowIdentity.FromName("repo/cp-order-estate-" + suffix);
        var rootMasters = Samples.NewTempDirectory();
        var rootEstate = Samples.NewTempDirectory();
        var templates = new OrderEstate.MemoryTemplates();
        var mastersEstate = new OrderEstate(rootMasters, templates);
        var estateEstate = new OrderEstate(rootEstate, templates);
        // Days ahead of the clock, so both schedules' next fires are still to come when the control plane reads them: one
        // already due would be fired and timed again by its scheduler, and the two would no longer fire an hour apart.
        var now = DateTime.UtcNow.Date.AddDays(2);
        try
        {
            mastersEstate.Schema($"master-data--Well{suffix}");
            mastersEstate.Schema($"master-data--Wellbore{suffix}", ("WellID", $"master-data--Well{suffix}"));
            mastersEstate.Delivery("wells", $"master-data--Well{suffix}", partition: partition);
            estateEstate.Delivery("wellbores", $"master-data--Wellbore{suffix}", columns: ["WellID"], partition: partition);

            // Each repository holds the templates its mappings fill, which its sync saves.
            mastersEstate.Hold();
            estateEstate.Hold();
            await using (var osdu = SampleEstate.Context(cs))
            {
                var sync = new DeliveryCatalogSync(new DeliveryDocumentLoader());
                await sync.ReconcileAsync(osdu, masters, rootMasters, now, new List<string>(), CancellationToken.None);
                await sync.ReconcileAsync(osdu, estate, rootEstate, now, new List<string>(), CancellationToken.None);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                foreach (var (id, name, flow) in new[] { (masters, "cp-order-masters-" + suffix, "wells"), (estate, "cp-order-estate-" + suffix, "wellbores") })
                {
                    db.Repos.Add(new CatalogRepo { Id = id, Name = name, FirstSeenUtc = now, LastSyncUtc = now });
                    db.Pipelines.Add(new CatalogPipeline
                    {
                        Id = CatalogIdentity.Pipeline(id, flow), RepoId = id, Name = flow, Kind = "delivery", RelativePath = $"flows/{flow}.yaml",
                        ContentHash = new string('0', 64), Active = true, Wave = 1, FirstSeenUtc = now, LastSeenUtc = now,
                    });
                }

                // The masters fire at three, the estate at two: the estate's wellbores go out an hour before the wells they name.
                AddSchedule(db, masters, "masters-" + suffix, "wells", now.AddHours(3));
                AddSchedule(db, estate, "estate-" + suffix, "wellbores", now.AddHours(2));
                await db.SaveChangesAsync();
            }

            await using var factory = Factory(cs);
            using var client = factory.CreateClient();
            var reader = await TokenAsync(client, "read");

            var partitions = await ReadAsync<DeliveryOrderPartitionsDto>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/order/partitions"));
            var plan = partitions.Partitions.Single(p => p.Partition == partition);
            Assert.Equal([[$"master-data--Well{suffix}"], [$"master-data--Wellbore{suffix}"]], plan.Levels);
            var edge = Assert.Single(plan.Edges);
            Assert.Equal(("cp-order-estate-" + suffix, "wellbores", "osdu.data.WellID"), (edge.Because.Single().Repository, edge.Because.Single().Flow, edge.Because.Single().Property));

            var schedules = await ReadAsync<DeliveryOrderSchedulesDto>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/order/schedules"));
            var later = schedules.Schedules.Single(s => s.Schedule.Name == "estate-" + suffix);
            Assert.True(later.OutOfOrder);
            var first = Assert.Single(later.RunFirst);
            Assert.Equal(("masters-" + suffix, false, "firesAfter"), (first.Schedule.Name, first.SameRepository, first.Timing));
            var because = Assert.Single(first.Because);
            Assert.Equal(("wellbores", "wells"), (because.Flow, because.ProviderFlow));
            Assert.Equal([partition], because.Partitions);
            Assert.DoesNotContain(schedules.Circles, c => c.Any(s => s.Name.EndsWith(suffix, StringComparison.Ordinal)));
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await RepoStore.DeleteAsync(db, masters, CancellationToken.None);
                await RepoStore.DeleteAsync(db, estate, CancellationToken.None);
            }

            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == masters || i.RepoId == estate).ExecuteDeleteAsync();
                await osdu.DeliveryMappings.Where(m => m.RepoId == masters || m.RepoId == estate).ExecuteDeleteAsync();
                await osdu.DeliverySearchTerms.Where(t => t.RepoId == masters || t.RepoId == estate).ExecuteDeleteAsync();
                await osdu.DeliveryRepositoryTemplates.Where(t => t.RepoId == masters || t.RepoId == estate).ExecuteDeleteAsync();
            }

            Directory.Delete(rootMasters, recursive: true);
            Directory.Delete(rootEstate, recursive: true);
        }
    }

    private static void AddSchedule(CatalogDbContext db, Guid repoId, string name, string flow, DateTime next)
    {
        var id = Guid.NewGuid();
        db.Schedules.Add(new CatalogSchedule { Id = id, RepoId = repoId, Name = name, Cron = "0 * * * *", NextFireUtc = next, CreatedUtc = next, UpdatedUtc = next });
        db.ScheduleMembers.Add(new CatalogScheduleMember { ScheduleId = id, PipelineId = CatalogIdentity.Pipeline(repoId, flow), RepoId = repoId, FlowName = flow });
    }

    private static ControlPlaneAppFactory Factory(string cs)
        => new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    private static async Task<string> TokenAsync(HttpClient client, params string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/auth/token", UriKind.Relative), new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
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

    /// <summary>
    /// The OSDU data definitions' GitLab API reduced to one release: wells, wellbores naming their well, logs naming their
    /// wellbore and a dataset of any type, and a schema that refers to a shared schema the release does not hold.
    /// </summary>
    private sealed class ReleaseHandler(string release) : HttpMessageHandler
    {
        public const string Commit = "5c1d0f7f1f0e8a3c2b9d4e6f7a8b9c0d1e2f3a4b";

        private static string Kind(string group, string entity) => $"osdu:wks:{group}--{entity}:1.0.0";

        private static string Reference(string group, string? entity)
            => $$"""{ "type": "string", "x-osdu-relationship": [ { "GroupType": "{{group}}"{{(entity is null ? string.Empty : $", \"EntityType\": \"{entity}\"")}} } ] }""";

        private static string Schema(string group, string entity, string data)
            => $$"""
                { "x-osdu-schema-source": "{{Kind(group, entity)}}", "type": "object",
                  "properties": { "id": { "type": "string" }, "data": { "type": "object", "properties": { "Name": { "type": "string" }{{data}} } } } }
                """;

        private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
        {
            ["SchemaStatus.json"] = $$"""{ "{{Kind("master-data", "Well")}}": "PUBLISHED", "{{Kind("master-data", "Wellbore")}}": "PUBLISHED", "{{Kind("work-product-component", "WellLog")}}": "PUBLISHED", "{{Kind("master-data", "Broken")}}": "PUBLISHED" }""",
            ["master-data/Well.1.0.0.json"] = Schema("master-data", "Well", string.Empty),
            ["master-data/Wellbore.1.0.0.json"] = Schema("master-data", "Wellbore", $", \"WellID\": {Reference("master-data", "Well")}"),
            ["work-product-component/WellLog.1.0.0.json"] = Schema(
                "work-product-component", "WellLog", $", \"WellboreID\": {Reference("master-data", "Wellbore")}, \"DatasetID\": {Reference("dataset", null)}"),
            ["master-data/Broken.1.0.0.json"] = $$"""
                { "x-osdu-schema-source": "{{Kind("master-data", "Broken")}}", "type": "object",
                  "properties": { "acl": { "$ref": "../abstract/AbstractMissing.1.0.0.json" }, "data": { "type": "object", "properties": { "Name": { "type": "string" } } } } }
                """,
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/repository/tags", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""[ { "name": "{{release}}", "commit": { "id": "{{Commit}}", "committed_date": "2026-10-17T14:55:57.000+02:00" } } ]""", Encoding.UTF8, "application/json"),
                });
            }

            if (path.EndsWith("/repository/archive.tar.gz", StringComparison.Ordinal) && request.RequestUri.Query.Contains("sha=" + Commit, StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive()) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private byte[] Archive()
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                var top = $"data-definitions-{release}-{Commit}-Generated/";
                foreach (var (file, json) in Files)
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "Generated/" + file) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(json)) });
                }
            }

            return buffer.ToArray();
        }
    }
}
