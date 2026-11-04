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
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The staged rows a check of an assertion run read, as T-SQL over its delivery flow's tables
/// (osdu/docs/reference/flow/staged-accounting.md, The rows in the source): the request names the run, the test and the check,
/// never a statement or a key, and the control plane writes the query from what the result kept (the delivery flow, the
/// values its scope was read under, the keys of the rows the check listed) through the operation a mapping's source query is
/// written by, which a stand-in records here (<see cref="RecordedOperations"/>). The run is written to the ledger as a run
/// writes it; nothing reaches an OSDU or a source table.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryAssertionSourceSqlApiTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";

    [Fact]
    public async Task A_staged_check_s_rows_are_written_as_a_query_of_its_delivery_flow_s_tables_and_bad_asks_are_refused()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var partition = "sq" + suffix;
        var deliveryName = "api-staged-delivery-" + suffix;
        var assertionName = "api-staged-assertion-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-staged-sql-" + suffix);
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var delivery = $$"""
            flowType: delivery
            name: {{deliveryName}}
            partitions: [{{partition}}]
            parameters:
              logSource: { required: true, description: The well database's log source a run reads }
            source:
              connection: ${env:OSDU_DATA_DB}
              work: ../.work/{logSource}
            target:
              endpoint: ${env:OSDU_URL}
              protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }
            interfaces:
              welllogs:
                record: { object: OsduData.silver.WellLog, key: [source_project, log_id], scope: { log_source: logSource } }
                mapping: WellLog@1.4.0
            """;
        var yaml = $$"""
            flowType: assertion
            name: {{assertionName}}
            partitions: [{{partition}}]
            source:
              endpoint: http://localhost
            tests:
              - name: logs-staged
                kind: {{WellLog}}
                assert:
                  - delivered: {{deliveryName}}
                    interface: welllogs
                    staged: true
                    values: { logSource: COMPOSITE }
            """;
        var flow = new DeliveryDocumentLoader().ParseAssertion(yaml, "flows/" + assertionName + ".yaml").ForRun(partition, RegisteredPartitions.None);

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-staged-sql-" + suffix, RemoteUrl = "https://example/cp-staged-sql.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(CatalogIdentity.Pipeline(repoId, deliveryName), repoId, deliveryName, "delivery", delivery, now));
            db.Pipelines.Add(Pipeline(CatalogIdentity.Pipeline(repoId, assertionName), repoId, assertionName, "assertion", yaml, now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        var operations = new RecordedOperations();
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Assertion, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });
            var scope = new StagedScope(deliveryName, "welllogs", new Dictionary<string, string>(StringComparer.Ordinal) { ["logSource"] = "COMPOSITE" });
            var runId = await RecordRunAsync(ledger, flow, now,
                Check(0, scope,
                    new AssertionExample("dev:work-product-component--WellLog:a", "PROJECT_A | 7", "is staged, and the ledger holds no record of it", ["PROJECT_A", "7"]),
                    new AssertionExample("dev:work-product-component--WellLog:b", "PROJECT_A | 8", "is held", ["PROJECT_A", "8"]),
                    new AssertionExample("dev:work-product-component--WellLog:a", "PROJECT_A | 7", "is staged again", ["PROJECT_A", "7"]),
                    new AssertionExample("dev:work-product-component--WellLog:stray", null, "is found by the test's query, and no row accounts for it")),
                Check(1, null),
                Check(2, scope with { Flow = "not-a-flow-" + suffix }, new AssertionExample(null, null, "is staged", ["PROJECT_A", "9"])),
                Check(3, scope with { Values = new Dictionary<string, string>(StringComparer.Ordinal) }, new AssertionExample(null, null, "is staged", ["PROJECT_A", "9"])),
                Check(4, scope, new AssertionExample("dev:work-product-component--WellLog:stray", null, "is found by the test's query, and no row accounts for it")));

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithServices(services => services.AddSingleton(operations.Registry()));
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var path = string.Create(CultureInfo.InvariantCulture, $"/api/v1/delivery/assertion-runs/{runId}/source-sql");

            // The rows the check listed, each once, in the order it listed them, named by their keys; a record no row accounts
            // for has no key, and is left out. Every attribute the mapping reads, under the values the run read the rows under.
            await AnsweredAsync(client, token, path, new { test = "logs-staged", assertion = 0 });
            var listed = operations.Last();
            Assert.Equal("delivery-source-sql", listed.Operation);
            Assert.Equal(deliveryName, listed.SourceRef);
            Assert.Equal("welllogs", listed.Argument("interface"));
            Assert.Equal("COMPOSITE", JsonDocument.Parse(listed.Argument("values")!).RootElement.GetProperty("logSource").GetString());
            Assert.Equal("[[\"PROJECT_A\",\"7\"],[\"PROJECT_A\",\"8\"]]", listed.Argument("keys"));
            Assert.Null(listed.Argument("target"));
            Assert.Null(listed.Argument("maxRows"));

            // Every row of the scope it read: no key, every row unless the request bounds them, and the test named ignoring case.
            await AnsweredAsync(client, token, path, new { test = "LOGS-STAGED", assertion = 0, rows = "all" });
            var every = operations.Last();
            Assert.Null(every.Argument("keys"));
            Assert.Equal("0", every.Argument("maxRows"));
            await AnsweredAsync(client, token, path, new { test = "logs-staged", assertion = 0, rows = "all", maxRows = 500 });
            Assert.Equal("500", operations.Last().Argument("maxRows"));

            // What cannot be asked is refused, and nothing runs.
            await ProblemAsync(client, token, path, new { }, HttpStatusCode.BadRequest, "names its test (test) and the check");
            await ProblemAsync(client, token, path, new { test = "logs-staged" }, HttpStatusCode.BadRequest, "names its test (test) and the check");
            await ProblemAsync(client, token, path, new { test = "logs-staged", assertion = 0, rows = "some" }, HttpStatusCode.BadRequest, "not 'some'");
            await ProblemAsync(client, token, path, new { test = "logs-staged", assertion = 0, rows = "all", maxRows = -1 }, HttpStatusCode.BadRequest, "or 0 for all of them");
            await ProblemAsync(client, token, "/api/v1/delivery/assertion-runs/9223372036854775806/source-sql", new { test = "logs-staged", assertion = 0 }, HttpStatusCode.NotFound, "No assertion run");
            await ProblemAsync(client, token, path, new { test = "other", assertion = 0 }, HttpStatusCode.NotFound, "holds no result of a test 'other'");
            await ProblemAsync(client, token, path, new { test = "logs-staged", assertion = 7 }, HttpStatusCode.NotFound, "has no check 7");

            // A check that reads OSDU alone has no staged rows; one whose flow is gone, or no longer takes the values the run
            // read it under, cannot be queried as it is now; and one that listed no row by its key can still be asked for all.
            await ProblemAsync(client, token, path, new { test = "logs-staged", assertion = 1 }, HttpStatusCode.Conflict, "reads no delivery flow's staged rows");
            await ProblemAsync(client, token, path, new { test = "logs-staged", assertion = 2 }, HttpStatusCode.NotFound, $"Delivery flow 'not-a-flow-{suffix}', whose rows the check read, is not an active flow");
            await ProblemAsync(client, token, path, new { test = "logs-staged", assertion = 3 }, HttpStatusCode.Conflict, "needs a value for logSource");
            await ProblemAsync(client, token, path, new { test = "logs-staged", assertion = 4 }, HttpStatusCode.Conflict, "lists no staged row by its key");
            // One operation ran since the last read, so none of the refusals ran anything.
            await AnsweredAsync(client, token, path, new { test = "logs-staged", assertion = 4, rows = "all" });
            Assert.Null(operations.Last().Argument("keys"));
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryAssertionResults.Where(r => r.FlowId == flow.LedgerId).ExecuteDeleteAsync();
                await osdu.DeliveryAssertionRuns.Where(r => r.FlowId == flow.LedgerId).ExecuteDeleteAsync();
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

    /// <summary>A check of the test's result: a staged delivered assertion reading <paramref name="scope"/>, or one reading OSDU alone with none.</summary>
    private static AssertionOutcome Check(int index, StagedScope? scope, params AssertionExample[] examples) => new()
    {
        Index = index,
        Label = scope is null ? "count " + index.ToString(CultureInfo.InvariantCulture) : "every staged row is in OSDU " + index.ToString(CultureInfo.InvariantCulture),
        Type = scope is null ? "count" : "delivered",
        Severity = "error",
        Outcome = examples.Length == 0 ? TestOutcomes.Passed : TestOutcomes.Failed,
        Expected = "what the flow declares",
        Actual = examples.Length == 0 ? "as declared" : "not as declared",
        Checked = 12,
        Failing = examples.Length,
        Examples = examples,
        Staged = scope,
    };

    /// <summary>Writes a run of the one test to the ledger as the runner does: opened, its result recorded, then closed.</summary>
    private static async Task<long> RecordRunAsync(OsduLedger ledger, AssertionFlowDefinition flow, DateTime startedUtc, params AssertionOutcome[] checks)
    {
        var test = flow.Test("logs-staged")!;
        var result = new TestResult
        {
            Test = test.Name,
            Kind = test.Kind,
            Outcome = TestOutcomes.Failed,
            Severity = "error",
            Matched = 12,
            Evaluated = 12,
            DefinitionHash = test.DefinitionHash,
            DurationMs = 1500,
            Assertions = checks,
            StartedUtc = startedUtc.AddSeconds(-2),
            CompletedUtc = startedUtc,
        };
        var run = await ledger.StartAssertionRunAsync(new AssertionRunState
        {
            FlowId = flow.LedgerId,
            FlowName = flow.Name,
            RunId = Guid.NewGuid(),
            Actor = "assertion source sql tests",
            Status = AssertionRunStatus.Running,
            Counts = AssertionCounts.None with { Tests = 1 },
            DefinitionsHash = new string('a', 32),
            StartedUtc = startedUtc,
        });
        await ledger.RecordAssertionResultAsync(new AssertionResultState
        {
            AssertionRunId = run.AssertionRunId,
            FlowId = flow.LedgerId,
            TestName = result.Test,
            Kind = result.Kind,
            Outcome = result.Outcome,
            Severity = result.Severity,
            Matched = result.Matched,
            Evaluated = result.Evaluated,
            Assertions = checks.Length,
            FailedAssertions = checks.Count(c => c.Outcome == TestOutcomes.Failed),
            DefinitionHash = result.DefinitionHash,
            DurationMs = result.DurationMs,
            Detail = TestResults.Serialize(result),
            StartedUtc = result.StartedUtc,
            CompletedUtc = result.CompletedUtc,
        });
        await ledger.CompleteAssertionRunAsync(run.AssertionRunId, AssertionRunStatus.Failed, AssertionCounts.Of([TestOutcomes.Failed]), "a test did not pass", startedUtc.AddMinutes(1));
        return run.AssertionRunId;
    }

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, string kind, string yaml, DateTime now) => new()
    {
        Id = id,
        RepoId = repoId,
        Name = name,
        Kind = kind,
        RelativePath = "flows/" + name + ".yaml",
        ContentHash = new string('0', 64),
        Yaml = yaml,
        DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"{{kind}}"}"""),
        Active = true,
        Wave = 0,
        FirstSeenUtc = now,
        LastSeenUtc = now,
    };

    private static async Task AnsweredAsync(HttpClient client, string token, string path, object body)
    {
        using var response = await SendAsync(client, token, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST {path} answered {(int)response.StatusCode}: {text}");
    }

    private static async Task ProblemAsync(HttpClient client, string token, string path, object body, HttpStatusCode status, string says)
    {
        using var response = await SendAsync(client, token, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"POST {path} answered {(int)response.StatusCode}, not {(int)status}: {text}");
        Assert.Contains(says, text, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
