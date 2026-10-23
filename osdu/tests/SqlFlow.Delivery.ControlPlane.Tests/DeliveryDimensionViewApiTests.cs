using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A dimension flow's views as the control plane serves them (osdu/docs/reference/concepts/api.md, Assertions,
/// dimensions and inventories): every view, a flow's views, one view with its checks, its SQL as written and as
/// declared, and its YAML; and an admin's removal, refused for a view its flow declares, and taking one no flow
/// declares any more. The view is written to the database as a build writes it; the tests never reach an OSDU.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryDimensionViewApiTests
{
    [Fact]
    public async Task A_flow_s_views_are_listed_shown_with_their_sql_checks_and_yaml_and_removed_once_no_flow_declares_them()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-views-" + suffix;
        var partition = "vw" + suffix;
        var log = "ViewLog" + suffix;
        var unit = "ViewUnit" + suffix;
        var viewName = "Curves" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-views-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var dimensions = $$"""
            flowType: dimension
            name: {{flowName}}
            partitions: [{{partition}}]
            source:
              endpoint: http://localhost
            target:
              connection: ${env:SQLFLOW_CATALOG_DB}
            dimensions:
              - name: {{log}}
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: id
                label: data.Name
                columns: { key: WellLogID, value: WellLogName }
                attributes:
                  UnitID: { path: data.UnitID, keep: id }
              - name: {{unit}}
                kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                path: id
                label: data.Code
                columns: { key: UnitID, value: UnitCode }
            """;
        var yaml = dimensions + $$"""

            views:
              - name: {{viewName}}
                description: Every log with its unit.
                from: {{log}}
                join:
                  - { on: UnitID, to: {{unit}}, as: Unit }
                columns:
                  Log: WellLogName
                  Unit: Unit.UnitCode
                  Records: { expression: records, dataType: int }
            """;
        var flow = new DeliveryDocumentLoader().ParseDimension(yaml, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-views-" + suffix, RemoteUrl = "https://example/cp-views.git", RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, yaml, now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Dimension, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });

            // The view written as a build writes it: its tables made empty, since no dimension of this flow was built.
            var written = await ledger.WriteDimensionViewsAsync(new DimensionViewWrite
            {
                Flow = flow.Name,
                FlowLedgerId = flow.LedgerId,
                Partition = partition,
                Views = flow.Views.Select(v => new DimensionViewToWrite(v, DimensionViews.TablesOf(flow, v))).ToList(),
                RunId = Guid.NewGuid(),
                Actor = "view api tests",
                Now = now,
            });
            Assert.Equal((DimensionViewWriteStatus.Written, DimensionViewCheckStatus.Passed), (written.Views[0].Status, written.Views[0].Check!.Status));

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client, ["read"]);
            var admin = await TokenAsync(client, ["read", "operate", "admin"]);

            // Every view, and the flow's: declared, written as declared, its last check passed with no row.
            foreach (var path in new[] { "/api/v1/delivery/dimensions/views", $"/api/v1/delivery/flows/{pipelineId}/dimensions/views" })
            {
                var listed = (await JsonAsync(client, token, path)).EnumerateArray().Single(v => v.GetProperty("name").GetString() == viewName);
                Assert.Equal("v_dim_" + viewName, listed.GetProperty("viewName").GetString());
                Assert.Equal(flowName, listed.GetProperty("flowName").GetString());
                Assert.Equal(pipelineId, listed.GetProperty("pipelineId").GetGuid());
                Assert.True(listed.GetProperty("declared").GetBoolean());
                Assert.True(listed.GetProperty("written").GetBoolean());
                Assert.False(listed.GetProperty("changed").GetBoolean());
                Assert.Equal(log, listed.GetProperty("from").GetString());
                Assert.Equal(["partition", "id", "Log", "Unit", "Records"], listed.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
                Assert.Equal("int", listed.GetProperty("columns")[4].GetProperty("type").GetString());
                Assert.Equal(("passed", 0L), (listed.GetProperty("lastCheck").GetProperty("status").GetString(), listed.GetProperty("lastCheck").GetProperty("rows").GetInt64()));
            }

            // One view: its SQL as written is the one its document declares, its YAML with where each part is written.
            var detail = await JsonAsync(client, token, $"/api/v1/delivery/dimensions/views/{viewName.ToUpperInvariant()}");
            Assert.Equal(detail.GetProperty("declaredSql").GetString(), detail.GetProperty("sql").GetString());
            Assert.StartsWith($"CREATE OR ALTER VIEW [osdu].[v_dim_{viewName}]", detail.GetProperty("sql").GetString(), StringComparison.Ordinal);
            Assert.Single(detail.GetProperty("checks").EnumerateArray());
            var yamlBlock = detail.GetProperty("yaml");
            Assert.Equal($"  - name: {viewName}", yamlBlock.GetProperty("lines")[0].GetString());
            Assert.Contains(yamlBlock.GetProperty("spans").EnumerateArray(), s => s.GetProperty("target").GetString() == "columns.Records.dataType");

            // A view its flow declares is not removed: its next build would write it again.
            using (var declared = await SendAsync(client, admin, HttpMethod.Delete, $"/api/v1/delivery/dimensions/views/{viewName}"))
            {
                Assert.Equal(HttpStatusCode.Conflict, declared.StatusCode);
                Assert.Contains("declares view", await declared.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // An operator may not remove one at all.
            using (var operate = await SendAsync(client, await TokenAsync(client, ["read", "operate"]), HttpMethod.Delete, $"/api/v1/delivery/dimensions/views/{viewName}"))
            {
                Assert.Equal(HttpStatusCode.Forbidden, operate.StatusCode);
            }

            // The flow no longer declares it: it is listed as such, and an admin removes it with its record.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Pipelines.Where(p => p.Id == pipelineId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Yaml, dimensions));
            }

            var orphan = (await JsonAsync(client, token, "/api/v1/delivery/dimensions/views")).EnumerateArray().Single(v => v.GetProperty("name").GetString() == viewName);
            Assert.False(orphan.GetProperty("declared").GetBoolean());
            Assert.True(orphan.GetProperty("recorded").GetBoolean());

            using (var removed = await SendAsync(client, admin, HttpMethod.Delete, $"/api/v1/delivery/dimensions/views/{viewName}"))
            {
                var body = await removed.Content.ReadAsStringAsync();
                Assert.True(removed.StatusCode == HttpStatusCode.OK, body);
                var gone = JsonDocument.Parse(body).RootElement;
                Assert.True(gone.GetProperty("dropped").GetBoolean());
                Assert.Equal(1L, gone.GetProperty("checks").GetInt64());
            }

            using (var missing = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/dimensions/views/{viewName}"))
            {
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }

            await using (var osdu = SampleEstate.Context(cs))
            {
                Assert.Equal(0, await osdu.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM sys.views WHERE [name] = {"v_dim_" + viewName}").SingleAsync());
                Assert.Equal(1, await osdu.DeliveryActivities.CountAsync(a => a.FlowId == flow.LedgerId && a.Kind == DimensionViewRemoval.ActivityKind && a.Outcome == "completed"));
            }
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.Database.ExecuteSqlRawAsync("DROP VIEW IF EXISTS " + DimensionTables.Qualified("v_dim_" + viewName) + ";");
                foreach (var table in new[] { DimensionTables.NameOf(log), DimensionTables.NameOf(unit) })
                {
                    await osdu.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS " + DimensionTables.Qualified(table) + ";");
                }

                var views = await osdu.DeliveryDimensionViews.Where(v => v.FlowName == flowName).Select(v => v.ViewId).ToListAsync();
                await osdu.DeliveryDimensionViewChecks.Where(c => views.Contains(c.ViewId)).ExecuteDeleteAsync();
                await osdu.DeliveryDimensionViews.Where(v => v.FlowName == flowName).ExecuteDeleteAsync();
                await osdu.DeliveryActivities.Where(a => a.FlowId == flow.LedgerId).ExecuteDeleteAsync();
                await osdu.DeliveryLedgers.Where(l => l.FlowId == flow.LedgerId).ExecuteDeleteAsync();
                await osdu.DeliveryLedgerPartitions.Where(p => p.Name == partition).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }
    }

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, string yaml, DateTime now) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = DimensionFlowDefinition.FlowTypeName,
        RelativePath = "flows/" + name + ".yaml",
        ContentHash = new string('0', 64),
        Yaml = yaml,
        DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"dimension"}"""),
        Active = true,
        Wave = 0,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
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
