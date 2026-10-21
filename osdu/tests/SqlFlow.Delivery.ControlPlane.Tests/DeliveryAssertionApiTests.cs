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
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The report of assertion flows as the control plane serves it (osdu/docs/reference/flow/assertion.md, The report):
/// the board of every flow and of one, a flow's runs, the matrix of its tests against them, one test's history, a run
/// with every result, and the report of a run in each format. The results are written to the ledger as a run writes
/// them, so what these tests hold the API to is what a real run's report looks like; the tests never reach an OSDU.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryAssertionApiTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";

    /// <summary>A kind no test saves a template for, so a test reading its fields is told how to capture one.</summary>
    private const string Unsaved = "osdu:wks:master-data--AssertionApiProbe:1.0.0";

    [Fact]
    public async Task An_assertion_flow_s_tests_runs_and_reports_are_served_in_its_partition()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-assertion-" + suffix;
        var partition = "at" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-assertions-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var deliveryPipelineId = CatalogIdentity.Pipeline(repoId, flowName + "-delivery");
        var yaml = $$"""
            flowType: assertion
            name: {{flowName}}
            description: What the well logs look like once delivered.
            partitions: [{{partition}}]
            parameters:
              logSource: { default: COMPOSITE, description: The log source tested. }
            source:
              endpoint: http://localhost
            tests:
              - name: log-headers
                description: Each header names its index curve.
                tags: [smoke]
                kind: {{WellLog}}
                query: 'data.Name:"{logSource}"'
                assert:
                  - count: { atLeast: 1 }
                  - field: data.ReferenceCurveID
                    equals: MD
              - name: log-count
                kind: {{WellLog}}
                assert:
                  - count: { atLeast: 1 }
              - name: unsaved-kind
                kind: {{Unsaved}}
                assert:
                  - field: data.Name
                    exists: true
            """;
        var flow = new DeliveryDocumentLoader().ParseAssertion(yaml, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        // The repository holds the templates its tests are checked against in its template folder, as its sync records it.
        await SampleEstate.HoldTemplatesAsync(cs, repoId, now);
        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-assertions-" + suffix, RemoteUrl = "https://example/cp-assertions.git",
                RootPath = SampleEstate.TemplateRoot, FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, "assertion", yaml, now));
            db.Pipelines.Add(Pipeline(deliveryPipelineId, repoId, flowName + "-delivery", "ingestion", "flowType: ingestion", now));
            db.Pipelines.Add(Pipeline(
                CatalogIdentity.Pipeline(repoId, flowName + "-broken"), repoId, flowName + "-broken", "assertion",
                $"flowType: assertion\nname: {flowName}-broken\nsource: {{ endpoint: http://localhost }}\ntests: []", now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        long first = 0;
        long second = 0;
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Assertion, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });

            // The first run ran every test: the header test failed on its field, the count passed, and the test of a kind
            // with no saved template was not evaluated.
            first = await RecordRunAsync(ledger, flow, null, now.AddHours(-2),
            [
                Result(flow, "log-headers", TestOutcomes.Failed, now.AddHours(-2), 1204,
                    Outcome(0, "count", TestOutcomes.Passed, 1204), Outcome(1, "field", TestOutcomes.Failed, 37, "dev:work-product-component--WellLog:a1")),
                Result(flow, "log-count", TestOutcomes.Passed, now.AddHours(-2), 1204, Outcome(0, "count", TestOutcomes.Passed, 1204)),
                Result(flow, "unsaved-kind", TestOutcomes.Errored, now.AddHours(-2), null),
            ]);

            // The second ran the header test alone, and it passed.
            second = await RecordRunAsync(ledger, flow, """{"tests":["log-headers"]}""", now.AddHours(-1),
            [
                Result(flow, "log-headers", TestOutcomes.Passed, now.AddHours(-1), 1210,
                    Outcome(0, "count", TestOutcomes.Passed, 1210), Outcome(1, "field", TestOutcomes.Passed, 0)),
            ]);

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The board of every flow names this one, in the partition asked for, with its parameters and every test.
            var board = await JsonAsync(client, token, $"/api/v1/delivery/assertions?partition={partition}");
            Assert.Equal(partition, board.GetProperty("partition").GetString());
            var listed = board.GetProperty("flows").EnumerateArray().Single(f => f.GetProperty("name").GetString() == flowName);
            Assert.True(listed.GetProperty("testsPartition").GetBoolean());
            Assert.True(listed.GetProperty("parses").GetBoolean());
            Assert.Equal(JsonValueKind.Null, listed.GetProperty("problem").ValueKind);
            Assert.Equal([partition], listed.GetProperty("partitions").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal("logSource", listed.GetProperty("parameters")[0].GetProperty("name").GetString());
            Assert.Equal("COMPOSITE", listed.GetProperty("parameters")[0].GetProperty("default").GetString());
            Assert.Equal(second, listed.GetProperty("lastRun").GetProperty("assertionRunId").GetInt64());

            var tests = listed.GetProperty("tests").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);
            Assert.Equal(["log-headers", "log-count", "unsaved-kind"], tests.Keys);

            // A test's latest result is its newest, and its history strip holds every run that ran it, newest first.
            var headers = tests["log-headers"];
            Assert.Equal(TestOutcomes.Passed, headers.GetProperty("latest").GetProperty("outcome").GetString());
            Assert.Equal(second, headers.GetProperty("latest").GetProperty("assertionRunId").GetInt64());
            Assert.Equal([second, first], headers.GetProperty("history").EnumerateArray().Select(p => p.GetProperty("assertionRunId").GetInt64()));
            Assert.False(headers.GetProperty("changed").GetBoolean());
            // The version of the saved template its fields were checked against.
            Assert.Matches("^[0-9a-f]{16}$", headers.GetProperty("template").GetString());
            Assert.Empty(headers.GetProperty("problems").EnumerateArray());
            Assert.Equal(2, headers.GetProperty("assertions").GetArrayLength());

            // A test the run of the header test alone left out keeps its result from the run before.
            Assert.Equal(first, tests["log-count"].GetProperty("latest").GetProperty("assertionRunId").GetInt64());

            // A test reading fields of a kind whose template is not saved is told so, and how to capture one.
            var problem = Assert.Single(tests["unsaved-kind"].GetProperty("problems").EnumerateArray()).GetString();
            Assert.Contains("sqlflow template capture", problem, StringComparison.Ordinal);

            // A flow whose document does not parse says why, and that it does not parse, which needs a fix.
            var broken = board.GetProperty("flows").EnumerateArray().Single(f => f.GetProperty("name").GetString() == flowName + "-broken");
            Assert.False(broken.GetProperty("parses").GetBoolean());
            Assert.False(broken.GetProperty("testsPartition").GetBoolean());
            Assert.Contains("does not parse", broken.GetProperty("problem").GetString(), StringComparison.Ordinal);
            Assert.Empty(broken.GetProperty("tests").EnumerateArray());

            // Read in a partition it does not test, the flow parses, says which partition it tests, and lists no test.
            var other = await JsonAsync(client, token, $"/api/v1/delivery/assertions?partition=elsewhere{suffix}");
            var elsewhere = other.GetProperty("flows").EnumerateArray().Single(f => f.GetProperty("name").GetString() == flowName);
            Assert.True(elsewhere.GetProperty("parses").GetBoolean());
            Assert.False(elsewhere.GetProperty("testsPartition").GetBoolean());
            Assert.Contains(partition, elsewhere.GetProperty("problem").GetString(), StringComparison.Ordinal);
            Assert.Empty(elsewhere.GetProperty("tests").EnumerateArray());

            // One flow's board is the same flow, alone.
            var own = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/assertions?partition={partition}");
            Assert.Equal(flowName, Assert.Single(own.GetProperty("flows").EnumerateArray()).GetProperty("name").GetString());
            Assert.Equal(3, own.GetProperty("totals").GetProperty("tests").GetInt32());
            Assert.Equal(2, own.GetProperty("totals").GetProperty("passed").GetInt32());
            Assert.Equal(1, own.GetProperty("totals").GetProperty("errored").GetInt32());

            // The runs, newest first, each with its counts and what it was asked to run.
            var runs = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/assertion-runs?partition={partition}");
            Assert.Equal([second, first], runs.EnumerateArray().Select(r => r.GetProperty("assertionRunId").GetInt64()));
            Assert.Equal("""{"tests":["log-headers"]}""", runs[0].GetProperty("selection").GetString());
            Assert.Equal(JsonValueKind.Null, runs[1].GetProperty("selection").ValueKind);
            Assert.Equal(1, runs[1].GetProperty("failed").GetInt32());
            Assert.Equal(partition, runs[0].GetProperty("partition").GetString());

            // The matrix lays every declared test against the runs, with nothing where a run did not run it.
            var matrix = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/assertion-matrix?partition={partition}");
            Assert.Equal([second, first], matrix.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("assertionRunId").GetInt64()));
            var rows = matrix.GetProperty("tests").EnumerateArray().ToDictionary(r => r.GetProperty("test").GetString()!);
            Assert.Equal([TestOutcomes.Passed, TestOutcomes.Failed], rows["log-headers"].GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("outcome").GetString()));
            var count = rows["log-count"].GetProperty("cells").EnumerateArray().ToList();
            Assert.Equal(JsonValueKind.Null, count[0].ValueKind);
            Assert.Equal(TestOutcomes.Passed, count[1].GetProperty("outcome").GetString());

            // One test's history is its whole results, newest first, with what every assertion found.
            var history = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/assertions/log-headers/history?partition={partition}");
            var results = history.GetProperty("results").EnumerateArray().ToList();
            Assert.Equal([TestOutcomes.Passed, TestOutcomes.Failed], results.Select(r => r.GetProperty("outcome").GetString()));
            var failing = results[1].GetProperty("assertions")[1];
            Assert.Equal(37, failing.GetProperty("failing").GetInt64());
            Assert.Equal("dev:work-product-component--WellLog:a1", failing.GetProperty("examples")[0].GetProperty("id").GetString());

            // A run with every result, and the pipeline it belongs to, for the report page's way back.
            var run = await JsonAsync(client, token, $"/api/v1/delivery/assertion-runs/{first}");
            Assert.Equal(pipelineId, run.GetProperty("pipelineId").GetGuid());
            Assert.Equal(TestOutcomes.Failed, run.GetProperty("run").GetProperty("status").GetString());
            Assert.Equal(3, run.GetProperty("results").GetArrayLength());

            // The report renders in every format, as a file named for the flow and the run.
            foreach (var (format, mediaType, marker) in new[]
            {
                ("json", "application/json", "\"log-headers\""),
                ("md", "text/markdown", "log-headers"),
                ("html", "text/html", "log-headers"),
                ("junit", "application/xml", "<testsuites"),
            })
            {
                using var report = await SendAsync(client, token, $"/api/v1/delivery/assertion-runs/{first}/report?format={format}");
                var body = await report.Content.ReadAsStringAsync();
                Assert.True(report.StatusCode == HttpStatusCode.OK, $"format {format} answered {(int)report.StatusCode}: {body}");
                Assert.Equal(mediaType, report.Content.Headers.ContentType?.MediaType);
                Assert.Contains(marker, body, StringComparison.Ordinal);
                Assert.Contains(flowName, report.Content.Headers.ContentDisposition?.FileNameStar ?? report.Content.Headers.ContentDisposition?.FileName ?? string.Empty, StringComparison.Ordinal);
            }

            // What the API cannot answer, it says why: a format it does not render, a run it does not hold, a test name that
            // is not one, a partition the flow does not test, and a pipeline that is not an assertion flow.
            await ProblemAsync(client, token, $"/api/v1/delivery/assertion-runs/{first}/report?format=pdf", HttpStatusCode.BadRequest, "not one of json, md, html, junit");
            await ProblemAsync(client, token, "/api/v1/delivery/assertion-runs/9223372036854775000", HttpStatusCode.NotFound, "No assertion run");
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/assertions/%20bad%20name/history?partition={partition}", HttpStatusCode.BadRequest, "is not a test name");
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/assertion-runs?partition=elsewhere{suffix}", HttpStatusCode.BadRequest, partition);
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{deliveryPipelineId}/assertions", HttpStatusCode.Conflict, "not an assertion flow");
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryAssertionResults.Where(r => r.FlowId == flow.LedgerId).ExecuteDeleteAsync();
                await osdu.DeliveryRepositoryTemplates.Where(t => t.RepoId == repoId).ExecuteDeleteAsync();
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

    /// <summary>
    /// The board says what an assertion's document says now, before any run: why it is disabled, when its proof values
    /// were stated and the last day they hold, whether that day is past, and where its template comes from, a mapping it
    /// names included; a check carries its own.
    /// </summary>
    [Fact]
    public async Task The_board_says_which_assertions_are_disabled_out_of_date_or_name_their_template_by_mapping()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var flowName = "api-assertion-dated-" + suffix;
        var partition = "ad" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-assertions-dated-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var yaml = $$"""
            flowType: assertion
            name: {{flowName}}
            partitions: [{{partition}}]
            source:
              endpoint: http://localhost
            tests:
              - name: switched-off
                kind: {{WellLog}}
                disabled: waiting for the second load
                assert:
                  - count: { atLeast: 1 }
              - name: out-of-date
                kind: {{WellLog}}
                asOf: 2026-01-01
                validFor: 30d
                assert:
                  - count: 7
                  - count: 8
                    disabled: restated after the load
              - name: current
                kind: {{WellLog}}
                template: 26a3c3441882db4f
                asOf: {{today}}
                validFor: 1y
                assert:
                  - field: data.Name
                    exists: true
              - name: by-mapping
                kind: {{WellLog}}
                template: WellLog@9.9.9
                assert:
                  - field: data.Name
                    exists: true
            """;
        var flow = new DeliveryDocumentLoader().ParseAssertion(yaml, "flows/" + flowName + ".yaml").ForRun(partition, RegisteredPartitions.None);
        var now = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-assertions-dated-" + suffix, RemoteUrl = "https://example/cp-assertions-dated.git",
                RootPath = SampleEstate.TemplateRoot, FirstSeenUtc = now, LastSyncUtc = now,
            });
            db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, "assertion", yaml, now));
            await db.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        try
        {
            await ledger.RegisterLedgerAsync(new LedgerEntry
            {
                FlowId = flow.LedgerId, Partition = partition, Kind = LedgerKinds.Assertion, FlowName = flow.Name, LedgerName = flow.LedgerName,
            });

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var own = await JsonAsync(client, token, $"/api/v1/delivery/flows/{pipelineId}/assertions?partition={partition}");
            var tests = Assert.Single(own.GetProperty("flows").EnumerateArray()).GetProperty("tests").EnumerateArray()
                .ToDictionary(test => test.GetProperty("name").GetString()!);

            Assert.Equal("waiting for the second load", tests["switched-off"].GetProperty("disabled").GetString());
            Assert.False(tests["switched-off"].GetProperty("needsNewProofValues").GetBoolean());

            // 30 days after 2026-01-01 is past: the assertion, and the one check it judges, need new proof values.
            var dated = tests["out-of-date"];
            Assert.Equal(("2026-01-01", "2026-01-31"), (dated.GetProperty("asOf").GetString(), dated.GetProperty("validUntil").GetString()));
            Assert.True(dated.GetProperty("needsNewProofValues").GetBoolean());
            var checks = dated.GetProperty("assertions").EnumerateArray().ToList();
            Assert.True(checks[0].GetProperty("needsNewProofValues").GetBoolean());
            Assert.Equal("2026-01-31", checks[0].GetProperty("validUntil").GetString());
            Assert.Equal("restated after the load", checks[1].GetProperty("disabled").GetString());

            // A year from today holds, and the template it names is its own.
            var current = tests["current"];
            Assert.False(current.GetProperty("needsNewProofValues").GetBoolean());
            Assert.Equal(today, current.GetProperty("asOf").GetString());
            Assert.True(current.GetProperty("templatePinned").GetBoolean());
            Assert.Equal(JsonValueKind.Null, current.GetProperty("templateMapping").ValueKind);

            // A template named by a mapping is read from the synced mapping, and one that is not synced says so.
            var byMapping = tests["by-mapping"];
            Assert.Equal("WellLog@9.9.9", byMapping.GetProperty("templateMapping").GetString());
            Assert.False(byMapping.GetProperty("templatePinned").GetBoolean());
            Assert.Contains("no mapping WellLog@9.9.9 is synced", Assert.Single(byMapping.GetProperty("problems").EnumerateArray()).GetString(), StringComparison.Ordinal);
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
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

    /// <summary>Writes a run to the ledger as the runner does: opened, each result recorded, then closed with its counts.</summary>
    private static async Task<long> RecordRunAsync(OsduLedger ledger, AssertionFlowDefinition flow, string? selection, DateTime startedUtc, IReadOnlyList<TestResult> results)
    {
        var run = await ledger.StartAssertionRunAsync(new AssertionRunState
        {
            FlowId = flow.LedgerId,
            FlowName = flow.Name,
            RunId = Guid.NewGuid(),
            Actor = "assertion api tests",
            Selection = selection,
            Status = AssertionRunStatus.Running,
            Counts = AssertionCounts.None with { Tests = results.Count },
            DefinitionsHash = new string('a', 32),
            StartedUtc = startedUtc,
        });
        foreach (var result in results)
        {
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
                Sampled = result.Sampled,
                Assertions = result.Assertions.Count,
                FailedAssertions = result.Assertions.Count(a => a.Outcome is TestOutcomes.Failed or TestOutcomes.Errored),
                DefinitionHash = result.DefinitionHash,
                DurationMs = result.DurationMs,
                Error = result.Error,
                Detail = TestResults.Serialize(result),
                StartedUtc = result.StartedUtc,
                CompletedUtc = result.CompletedUtc,
            });
        }

        var counts = AssertionCounts.Of(results.Select(r => r.Outcome));
        var status = counts.Failed > 0 ? AssertionRunStatus.Failed : counts.Errored > 0 ? AssertionRunStatus.Errored : AssertionRunStatus.Passed;
        await ledger.CompleteAssertionRunAsync(run.AssertionRunId, status, counts, status == AssertionRunStatus.Passed ? null : "a test did not pass", startedUtc.AddMinutes(1));
        return run.AssertionRunId;
    }

    private static TestResult Result(AssertionFlowDefinition flow, string name, string outcome, DateTime completedUtc, long? matched, params AssertionOutcome[] assertions)
    {
        var test = flow.Test(name)!;
        return new TestResult
        {
            Test = test.Name,
            Description = test.Description,
            Kind = test.Kind,
            Tags = test.Tags,
            Outcome = outcome,
            Severity = outcome == TestOutcomes.Failed ? "error" : null,
            Matched = matched,
            Evaluated = matched,
            Query = test.Query,
            DefinitionHash = test.DefinitionHash,
            DurationMs = 1500,
            Error = outcome == TestOutcomes.Errored ? "It does not fit the template of its kind." : null,
            Assertions = assertions,
            StartedUtc = completedUtc.AddSeconds(-2),
            CompletedUtc = completedUtc,
        };
    }

    private static AssertionOutcome Outcome(int index, string type, string outcome, long failing, string? exampleId = null) => new()
    {
        Index = index,
        Label = type + " " + index.ToString(CultureInfo.InvariantCulture),
        Type = type,
        Severity = "error",
        Outcome = outcome,
        Expected = "what the flow declares",
        Actual = outcome == TestOutcomes.Passed ? "as declared" : "not as declared",
        Checked = 1204,
        Failing = outcome == TestOutcomes.Failed ? failing : 0,
        Value = failing,
        Examples = exampleId is null ? [] : [new AssertionExample(exampleId, "\"DEPT\"", "is \"DEPT\", not \"MD\"")],
    };

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

    private static async Task ProblemAsync(HttpClient client, string token, string path, HttpStatusCode status, string says)
    {
        using var response = await SendAsync(client, token, path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"GET {path} answered {(int)response.StatusCode}, not {(int)status}: {body}");
        Assert.Contains(says, body, StringComparison.Ordinal);
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

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
