using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The value check as the API serves it: the interfaces of the flows that render with a mapping, which is what a mapping's
/// page checks its values against, a check of one interface's rows queued for a node with the variables, the scope's
/// values, the row budget and the example records it asks for, and the read of the values the scope's parameters can take,
/// which the control plane answers itself. Every request a node would refuse is answered as a 400 before anything is queued.
/// Nothing here reaches an OSDU: the check's tasks are queued and read back from the catalog, not run, and the read of the
/// scope's values is stood in for (<see cref="RecordedOperations"/>).
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryValueCheckApiTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_mapping_lists_the_flows_rendering_with_it_and_a_check_of_one_is_queued_with_what_it_asks_and_bad_asks_are_refused()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-values-" + suffix;
        var retiredName = "api-values-retired-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-values-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var logsLedger = FlowId.Of(flowName + "/welllogs");
        var mappingId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            parameters:
              logSource: { required: true, description: The well database's log source a run reads }
              project: { default: PROJECT_A }
            source:
              connection: ${env:OSDU_DATA_DB}
              work: ../.work/{logSource}
            render:
              parameters:
                dataPartition: dev
            target:
              endpoint: ${env:OSDU_URL}
              headers: { data-partition-id: dev }
              protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
            interfaces:
              wellbores:
                record: { object: OsduData.silver.Wellbore, key: [facility_name] }
                mapping: Wellbore@1.0.0
              welllogs:
                record: { object: OsduData.silver.WellLog, key: [source_project, log_id], scope: { log_source: logSource } }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: payload_hash }
                mapping: WellLog@1.4.0
            """;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-values-" + suffix, RemoteUrl = "https://example/cp-values.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = pipelineId,
                RepoId = repoId,
                Name = flowName,
                Kind = "delivery",
                RelativePath = "flows/" + flowName + ".yaml",
                ContentHash = new string('0', 64),
                Yaml = yaml,
                DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{flowName}}","flowKind":"delivery"}"""),
                Active = true,
                Wave = 0,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            await db.SaveChangesAsync();
        }

        await using (var osdu = SampleEstate.Context(cs))
        {
            osdu.DeliveryMappings.Add(new DeliveryMapping
            {
                Id = mappingId,
                RepoId = repoId,
                Reference = "WellLog@1.4.0",
                Name = "WellLog",
                Version = "1.4.0",
                Kind = "osdu:wks:work-product-component--WellLog:1.4.0",
                TemplateVersion = "26a3c3441882db4f",
                RelativePath = "mappings/WellLog@1.4.0.yaml",
                ContentHash = new string('1', 64),
                Yaml = "documentType: mapping",
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            osdu.DeliveryInterfaces.AddRange(
                Interface(repoId, flowName, "wellbores", 0, "Wellbore@1.0.0", "OsduData.silver.Wellbore", FlowId.Of(flowName + "/wellbores"), active: true, now),
                Interface(repoId, flowName, "welllogs", 1, "WellLog@1.4.0", "OsduData.silver.WellLog", logsLedger, active: true, now),
                // A flow the repository no longer declares renders nothing, and one the catalog holds no pipeline of has no rows to read.
                Interface(repoId, retiredName, "welllogs", 0, "WellLog@1.4.0", "OsduData.silver.WellLog", FlowId.Of(retiredName + "/welllogs"), active: false, now),
                Interface(repoId, "no-pipeline-" + suffix, string.Empty, 0, "WellLog@1.4.0", "OsduData.silver.WellLog", FlowId.Of("no-pipeline-" + suffix), active: true, now));
            await osdu.SaveChangesAsync();
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
            var token = await TokenAsync(client);

            // The one interface that renders with the mapping, is declared and has a pipeline.
            using (var flows = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/mappings/{mappingId:D}/flows"))
            {
                var text = await flows.Content.ReadAsStringAsync();
                Assert.True(flows.StatusCode == HttpStatusCode.OK, text);
                var only = Assert.Single(JsonDocument.Parse(text).RootElement.EnumerateArray().ToList());
                Assert.Equal(pipelineId, only.GetProperty("pipelineId").GetGuid());
                Assert.Equal(flowName, only.GetProperty("flow").GetString());
                Assert.Equal("welllogs", only.GetProperty("interface").GetString());
                Assert.Equal(JsonValueKind.Null, only.GetProperty("partition").ValueKind);
                Assert.Equal(logsLedger, only.GetProperty("ledgerFlowId").GetGuid());
                Assert.Equal("OsduData.silver.WellLog", only.GetProperty("recordObject").GetString());
                Assert.Equal("ddms", only.GetProperty("route").GetString());
            }

            using (var unknown = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/mappings/{Guid.NewGuid():D}/flows"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }

            // The flow's parameters say which column of the record table each scopes by, so a page offers that column's
            // values for it; a parameter the scope does not read (the work location's) is typed.
            using (var interfaces = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/flows/{pipelineId:D}/interfaces"))
            {
                Assert.Equal(HttpStatusCode.OK, interfaces.StatusCode);
                var logs = JsonDocument.Parse(await interfaces.Content.ReadAsStringAsync()).RootElement.EnumerateArray()
                    .Single(i => i.GetProperty("interface").GetString() == "welllogs");
                var parameters = logs.GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p);
                Assert.Equal("log_source", parameters["logSource"].GetProperty("scopeColumn").GetString());
                Assert.Equal(JsonValueKind.Null, parameters["project"].GetProperty("scopeColumn").ValueKind);
            }

            // A check of named variables, with the scope's values, a row budget and a page of examples.
            var path = $"/api/v1/delivery/flows/{pipelineId:D}/check-values?interface=welllogs";
            var values = new Dictionary<string, string> { ["logSource"] = "COMPOSITE" };
            var queued = await QueuedAsync(client, token, path, new
            {
                targets = new[] { " osdu.data.WellboreID ", "osdu.data.Curves[].CurveUnit", "osdu.data.WellboreID" },
                values,
                maxRows = 25000,
                samples = 100,
                skipSamples = 200,
                mapping = "WellLog@1.4.0",
            });
            Assert.Equal("delivery-check-values", queued.Operation);
            Assert.Equal(flowName, queued.SourceRef);
            Assert.Equal("welllogs", queued.Argument("interface"));
            Assert.Equal(["osdu.data.WellboreID", "osdu.data.Curves[].CurveUnit"], JsonSerializer.Deserialize<string[]>(queued.Argument("targets")!)!);
            Assert.Equal("COMPOSITE", JsonDocument.Parse(queued.Argument("values")!).RootElement.GetProperty("logSource").GetString());
            Assert.Equal("25000", queued.Argument("maxRows"));
            Assert.Equal("100", queued.Argument("samples"));
            Assert.Equal("200", queued.Argument("skipSamples"));
            Assert.Equal("WellLog@1.4.0", queued.Argument("mapping"));

            // A check of every variable over the whole scope names none and reads every row.
            var whole = await QueuedAsync(client, token, path, new { values, maxRows = 0 });
            Assert.Null(whole.Argument("targets"));
            Assert.Equal("0", whole.Argument("maxRows"));
            Assert.Null(whole.Argument("samples"));

            // An export of an attribute's rows is asked for as a check is, and queued for a node the same way.
            var export = await QueuedAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/check-values/export?interface=welllogs", new { targets = new[] { "osdu.data.WellboreID" }, values, maxRows = 1000, mapping = "WellLog@1.4.0" });
            Assert.Equal("delivery-export-value-issues", export.Operation);
            Assert.Equal(flowName, export.SourceRef);
            Assert.Equal("welllogs", export.Argument("interface"));
            Assert.Equal(["osdu.data.WellboreID"], JsonSerializer.Deserialize<string[]>(export.Argument("targets")!)!);
            Assert.Equal("1000", export.Argument("maxRows"));
            Assert.Equal("WellLog@1.4.0", export.Argument("mapping"));
            await RefusedAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/check-values/export?interface=welllogs", new { }, "needs a value for logSource");

            // What a node would refuse is refused here, before anything is queued.
            await RefusedAsync(client, token, path, new { targets = new[] { "data.Name" }, values }, "is not a variable a check can name");
            await RefusedAsync(client, token, path, new { targets = new[] { "osdu.data.Curves[]" }, values }, "is not a variable a check can name");
            await RefusedAsync(client, token, path, new { targets = Enumerable.Range(0, 201).Select(i => $"osdu.data.P{i}").ToArray(), values }, "at most 200 variables");
            await RefusedAsync(client, token, path, new { targets = Enumerable.Range(0, 150).Select(i => $"osdu.data.{new string('P', 30)}{i}").ToArray(), values }, "Check fewer at a time");
            await RefusedAsync(client, token, path, new { }, "needs a value for logSource");
            await RefusedAsync(client, token, path, new { values, maxRows = -1 }, "or 0 for the whole scope");
            await RefusedAsync(client, token, path, new { values, samples = 501 }, "between 0 and 500 example records");
            await RefusedAsync(client, token, path, new { values, skipSamples = -1 }, "a count from 0");
            await RefusedAsync(client, token, path, new { values, mapping = "WellLog@9.9.9" }, "renders with mapping WellLog@1.4.0, not WellLog@9.9.9");
            await RefusedAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/check-values", new { values }, "name the one this request is about with ?interface=");

            // What a scope's parameters can be set to is read by the control plane itself, at once, from the columns the flow
            // binds them to; it needs no value itself, since it is what a value is picked from, and nothing is queued for it.
            using (var answered = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/delivery/flows/{pipelineId:D}/scope-values?interface=welllogs"))
            {
                Assert.True(answered.StatusCode == HttpStatusCode.OK, await answered.Content.ReadAsStringAsync());
            }

            var scopeValues = operations.Last();
            Assert.Equal("delivery-scope-values", scopeValues.Operation);
            Assert.Equal(flowName, scopeValues.SourceRef);
            Assert.Equal("welllogs", scopeValues.Argument("interface"));
            Assert.Null(scopeValues.Argument("values"));
            await RefusedAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/scope-values", new { }, "name the one this request is about with ?interface=");
            Assert.Empty(operations.Given);
            await using var catalog = CatalogDatabase.Create(cs);
            Assert.DoesNotContain(await catalog.ComputeTasks.AsNoTracking().Where(t => t.SourceRef == flowName).Select(t => t.Operation).ToListAsync(), o => o == "delivery-scope-values");
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryMappings.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.ComputeTasks.Where(t => t.SourceRef == flowName).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }
        }

        async Task<ComputeTaskPayload> QueuedAsync(HttpClient client, string token, string path, object? body)
        {
            using var response = await SendAsync(client, token, HttpMethod.Post, path, body);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"POST {path} answered {(int)response.StatusCode}: {text}");
            var taskId = JsonDocument.Parse(text).RootElement.GetProperty("taskId").GetGuid();
            await using var db = CatalogDatabase.Create(cs);
            var task = await db.ComputeTasks.AsNoTracking().SingleAsync(t => t.TaskId == taskId);
            return JsonSerializer.Deserialize<ComputeTaskPayload>(task.ArgumentsJson, WebJson)!;
        }
    }

    private static DeliveryInterface Interface(
        Guid repoId, string flowName, string name, int ordinal, string mapping, string recordObject, Guid ledger, bool active, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        RepoId = repoId,
        FlowName = flowName,
        Interface = name,
        Ordinal = ordinal,
        LedgerFlowId = ledger,
        LedgerName = name.Length == 0 ? flowName : $"{flowName}/{name}",
        Route = "ddms",
        MappingReference = mapping,
        RecordObject = recordObject,
        RelativePath = "flows/" + flowName + ".yaml",
        Active = active,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

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
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
