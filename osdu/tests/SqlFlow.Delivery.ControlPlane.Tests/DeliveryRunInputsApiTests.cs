using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A delivery flow's run inputs through SQLFlow's run-input routes (osdu/docs/reference/flow/parameters.md): what the run form
/// is given (the parameters, with their choices and lookups, the operations that resolve them, Records at most with the
/// document's value), a parameter's values looked up through the operation the Preview tab lists a scope's values with, in
/// the interface the parameter is looked up through and the partition the form names, and what a run with the form's inputs
/// would take and what would refuse it. The lookup is stood in for (<see cref="RecordedOperations"/>), so nothing here
/// reads the ingestion tables or reaches an OSDU, and nothing is queued.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryRunInputsApiTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_delivery_flows_run_form_is_given_its_inputs_their_values_and_what_would_refuse_a_run()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-inputs-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-inputs-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            parameters:
              logSource: { required: true, description: The well database's log source a run reads }
              project: { default: PROJECT_A, choices: [PROJECT_A, PROJECT_B] }
              wellbore: { lookup: { column: facility_name, interface: wellbores } }
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
                record: { object: OsduData.silver.WellLog, key: [source_project, log_id], scope: { log_source: logSource }, maxRecords: 100 }
                bulk: { root: ../data/curves, locationColumn: curve_folder, hashColumn: payload_hash }
                mapping: WellLog@1.4.0
            """;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-inputs-" + suffix, RemoteUrl = "https://example/cp-inputs.git",
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

            // What the run form is given, read from the flow's document by its kind.
            using (var described = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/pipelines/{pipelineId:D}/parameters"))
            {
                var text = await described.Content.ReadAsStringAsync();
                Assert.True(described.StatusCode == HttpStatusCode.OK, text);
                var inputs = JsonDocument.Parse(text).RootElement.GetProperty("inputs");
                var parameters = inputs.GetProperty("parameters").EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!, p => p);
                Assert.Equal(["logSource", "project", "wellbore"], parameters.Keys);
                Assert.True(parameters["logSource"].GetProperty("takesSeveral").GetBoolean());
                Assert.Equal(
                    "the values of column log_source of OsduData.silver.WellLog, which interfaces.welllogs.record.scope binds it to",
                    parameters["logSource"].GetProperty("lookup").GetString());
                Assert.Equal(["PROJECT_A", "PROJECT_B"], parameters["project"].GetProperty("choices").EnumerateArray().Select(c => c.GetString()));
                Assert.Equal("PROJECT_A", parameters["project"].GetProperty("default").GetString());
                Assert.Equal("the values of column facility_name of OsduData.silver.Wellbore", parameters["wellbore"].GetProperty("lookup").GetString());
                Assert.Equal(["deliver", "plan", "intake", "replan"], inputs.GetProperty("parameterOperations").EnumerateArray().Select(o => o.GetString()));
                var setting = Assert.Single(inputs.GetProperty("settings").EnumerateArray().ToList());
                Assert.Equal(("maxRecords", "WholeNumber"), (setting.GetProperty("key").GetString(), setting.GetProperty("input").GetString()));
                Assert.Equal("wellbores none, welllogs 100", setting.GetProperty("documentValue").GetString());
                Assert.Empty(inputs.GetProperty("kindValues").EnumerateArray());
            }

            // A scope parameter's values are the column its scope binds, read through its interface by the operation the
            // Preview tab lists a scope's values with, narrowed to what is typed.
            operations.Answer = payload => JsonSerializer.Serialize(new ScopeValuesAnswer(
                flowName, payload.Argument("interface"), "OsduData.silver.WellLog",
                [new SqlFlow.Delivery.Source.ScopeParameterValues(payload.Argument("parameter")!, "log_source", [new("COMPOSITE", 42), new("WIRELINE", 7)], More: true)],
                now), WebJson);
            using (var logSources = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/pipelines/{pipelineId:D}/parameters/logSource/values", new { search = "o" }))
            {
                var text = await logSources.Content.ReadAsStringAsync();
                Assert.True(logSources.StatusCode == HttpStatusCode.OK, text);
                var answer = JsonDocument.Parse(text).RootElement;
                Assert.Equal("column log_source of OsduData.silver.WellLog", answer.GetProperty("source").GetString());
                Assert.Equal([("COMPOSITE", 42L), ("WIRELINE", 7L)], answer.GetProperty("values").EnumerateArray().Select(v => (v.GetProperty("value").GetString(), v.GetProperty("count").GetInt64())));
                Assert.True(answer.GetProperty("more").GetBoolean());
            }

            var asked = operations.Last();
            Assert.Equal((ScopeValuesOperation.OperationName, flowName), (asked.Operation, asked.SourceRef));
            Assert.Equal(("welllogs", "logSource", "o"), (asked.Argument("interface"), asked.Argument("parameter"), asked.Argument("search")));
            Assert.StartsWith("user:", asked.Argument("actor"), StringComparison.Ordinal);

            // A parameter its lookup places in another interface is read through that interface.
            using (var lookedUp = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/pipelines/{pipelineId:D}/parameters/wellbore/values", new { }))
            {
                Assert.Equal(HttpStatusCode.OK, lookedUp.StatusCode);
            }

            var wellbores = operations.Last();
            Assert.Equal(("wellbores", "wellbore", null), (wellbores.Argument("interface"), wellbores.Argument("parameter"), wellbores.Argument("search")));

            // A parameter's choices are the document's own list, and no operation reads them.
            using (var choices = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/pipelines/{pipelineId:D}/parameters/project/values", new { search = "b" }))
            {
                var answer = JsonDocument.Parse(await choices.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal(["PROJECT_B"], answer.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("value").GetString()));
            }

            Assert.Empty(operations.Given);

            // A source that cannot be read answers why, with what it quotes of a secret redacted.
            operations.Answer = _ => throw new SqlFlowException("the ingestion database refused Server=db;Password=hunter2;Encrypt=True.");
            using (var failed = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/pipelines/{pipelineId:D}/parameters/logSource/values", new { }))
            {
                var text = await failed.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
                Assert.Contains("The values of parameter 'logSource' could not be read: the ingestion database refused Server=db;Password=[redacted];", text, StringComparison.Ordinal);
                Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
            }

            _ = operations.Last();

            // What would refuse a run: a plan resolves the parameters, a verify takes none, and the payload is the kind's to check.
            Assert.Equal(
                ["parameter 'logSource' is required. Supply it with --set logSource=value or the run's values."],
                await ProblemsAsync(client, token, pipelineId, new { operation = "plan" }));
            Assert.Empty(await ProblemsAsync(client, token, pipelineId, new { operation = "verify" }));
            Assert.Equal(
                ["parameter 'project' is given 'PROJECT_C', which is not one of its choices: PROJECT_A, PROJECT_B."],
                await ProblemsAsync(client, token, pipelineId, new { operation = "deliver", values = new Dictionary<string, object> { ["logSource"] = new[] { "COMPOSITE", "WIRELINE" }, ["project"] = "PROJECT_C" } }));
            Assert.Equal(
                ["parameter 'project' is given 2 values, and takes one: only a parameter source.record.scope binds (logSource) takes several."],
                await ProblemsAsync(client, token, pipelineId, new { operation = "plan", values = new Dictionary<string, object> { ["logSource"] = "A", ["project"] = new[] { "PROJECT_A", "PROJECT_B" } } }));
            Assert.Equal(
                ["payload maxRecords is 5000; a run reads the first 1 to 1,000 records of its scope, or 0 reads the scope whole."],
                await ProblemsAsync(client, token, pipelineId, new { operation = "deliver", values = new { logSource = "A" }, payload = new { maxRecords = 5000 } }));
            Assert.Empty(await ProblemsAsync(client, token, pipelineId, new { operation = "replan", values = new { logSource = "A" }, payload = new { maxRecords = 0 } }));
            Assert.Equal(
                ["payload maxRecords does not apply to the verify operation: only a deliver, plan, intake or replan run reads the first records of its scope."],
                await ProblemsAsync(client, token, pipelineId, new { operation = "verify", payload = new { maxRecords = 5 } }));

            Assert.Empty(operations.Given);
            await using var catalog = CatalogDatabase.Create(cs);
            Assert.Empty(await catalog.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync());
            Assert.Empty(await catalog.ComputeTasks.AsNoTracking().Where(t => t.SourceRef == flowName).ToListAsync());
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static async Task<IReadOnlyList<string?>> ProblemsAsync(HttpClient client, string token, Guid pipelineId, object body)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, $"/api/v1/pipelines/{pipelineId:D}/parameters/explain", body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList();
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
