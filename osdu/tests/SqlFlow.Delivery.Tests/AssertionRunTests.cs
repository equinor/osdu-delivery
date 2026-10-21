using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An assertion flow's run: every kind of assertion evaluated against a fake OSDU platform, whose every request is held to
/// the service's pinned contract, and the report the run keeps in the ledger on SQL Server.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class AssertionRunTests : IDisposable
{
    private const string Wellbore = Samples.WellboreKind;

    private const string WellLog = Samples.WellLogKind;

    private const string Flow = $$"""
        flowType: assertion
        name: welldb-04-assertion
        partitions: [dev, test]
        source:
          endpoint: http://localhost
        defaults: { examples: 10 }
        reliability: { concurrency: 3, retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        tests:
          - name: wellbores
            tags: [smoke]
            kind: {{Wellbore}}
            sort: [{ field: data.FacilityName }]
            assert:
              - count: 4
              - { field: data.FacilityName, exists: true }
              - { unique: [data.FacilityName] }
              - { field: data.WellID, resolves: master-data--Well }
              - { aggregate: max, field: data.SequenceNumber, atMost: 4 }
              - { groupBy: kind, groups: { "{{Wellbore}}": 4 }, mode: exact }
              - { groupBy: data.FacilityName, groups: { "A/1-F-12": 2 }, groupCount: 3 }
              - { indexed: true }
              - { legal: valid }
              - { conforms: true, severity: warning }
              - { recordSet: { columns: [data.FacilityName, data.SequenceNumber], mode: includes, rows: [["A/1-F-11", 1]] } }
              - { field: data.SequenceNumber, between: [1, 4] }
              - { delivered: wells-delivery }
          - name: names
            tags: [smoke]
            kind: {{Wellbore}}
            query: 'data.Source:"WellDB"'
            read: index
            assert:
              - count: { atLeast: 1 }
              - { field: data.FacilityName, startsWith: "A/" }
          - name: logs
            tags: [bulk]
            kind: {{WellLog}}
            bulk: { maxRows: 1000 }
            assert:
              - rowCount: { atLeast: 10 }
              - columns: { includes: [MD, GR] }
              - { column: GR, between: [0, 300], severity: warning }
              - { column: GR, between: [0, 500], optional: true }
              - { column: MD, monotonic: increasing, severity: warning }
              - { aggregate: max, column: GR, atMost: 500 }
          - name: by-id
            kind: {{Wellbore}}
            ids: ["{partition}:master-data--Wellbore:w1", "{partition}:master-data--Wellbore:nope"]
            assert:
              - count: 2
          - name: in-test-only
            partitions: [test]
            kind: {{Wellbore}}
            assert:
              - count: 1
          - name: misspelled
            kind: {{Wellbore}}
            assert:
              - { field: data.FacilityNam, exists: true }
          - name: too-many
            kind: {{Wellbore}}
            maxRecords: 2
            assert:
              - count: 4
              - { field: data.FacilityName, exists: true }
          - name: sampled
            kind: {{Wellbore}}
            maxRecords: 2
            sample: true
            assert:
              - { field: data.FacilityName, exists: true }
        """;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeAssertionPlatform _platform = new();

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    private static JsonObject Data(string name, int sequence, string wellId, string source = "WellDB")
        => new() { ["FacilityName"] = name, ["SequenceNumber"] = sequence, ["WellID"] = wellId, ["Source"] = source };

    /// <summary>Four wellbores (one sharing its name, one pointing at a well that is not there, one indexed badly, one without a legal block), their well, and two logs.</summary>
    private void Estate()
    {
        _platform.Add("dev:master-data--Well:W1", "osdu:wks:master-data--Well:1.0.0", new JsonObject { ["FacilityName"] = "A/1-F" });
        _platform.Add("dev:master-data--Wellbore:w1", Wellbore, Data("A/1-F-11", 1, "dev:master-data--Well:W1:"));
        _platform.Add("dev:master-data--Wellbore:w2", Wellbore, Data("A/1-F-12", 2, "dev:master-data--Well:MISSING:"), ["dev-expired"]);
        _platform.Add("dev:master-data--Wellbore:w3", Wellbore, Data("A/1-F-12", 3, "dev:master-data--Well:W1:1234"));
        _platform.Add("dev:master-data--Wellbore:w4", Wellbore, Data("A/1-F-14", 4, "dev:master-data--Well:W1:"), withLegal: false);
        _platform.IndexStatus["dev:master-data--Wellbore:w2"] = 400;
        _platform.InvalidLegalTags["dev-expired"] = "LegalTag has expired";

        _platform.Add("dev:work-product-component--WellLog:l1", WellLog, new JsonObject { ["Name"] = "good" });
        _platform.Add("dev:work-product-component--WellLog:l2", WellLog, new JsonObject { ["Name"] = "bad" });
        _platform.Bulk["dev:work-product-component--WellLog:l1"] = (["MD", "GR"], Enumerable.Range(1, 20).Select(i => new object?[] { 100.0 + i, 50.0 + i }).ToList());
        _platform.Bulk["dev:work-product-component--WellLog:l2"] = (["MD", "GR", "CALI"], Enumerable.Range(1, 20)
            .Select(i => new object?[] { i == 12 ? 90.0 : 100.0 + i, i == 7 ? 400.0 : i == 8 ? null : 60.0, 8.5 }).ToList());
    }

    /// <summary>The ledger of a delivery flow that delivered w1, w2 and w5 in dev: w5 is not in OSDU.</summary>
    private async Task DeliveredAsync(OsduLedger ledger)
    {
        var flowId = FlowId.Of("wells-delivery", "dev");
        await ledger.RegisterLedgerAsync(new LedgerEntry { FlowId = flowId, Partition = "dev", Kind = LedgerKinds.Delivery, FlowName = "wells-delivery", LedgerName = "wells-delivery@dev" });
        var submission = Guid.NewGuid();
        var now = _clock.GetUtcNow().UtcDateTime;
        await ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission, FlowId = flowId, FlowName = "wells-delivery", MappingReference = "Wellbore@1.0.0", RenderContext = "{}",
            RecordCount = 3, Status = SubmissionStatus.Planned, ReceivedUtc = now,
        });
        var records = new[] { "w1", "w2", "w5" }.Select(id => new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", [id]),
            FlowId = flowId,
            SourceKey = id,
            MappingName = "Wellbore",
            TargetId = $"dev:master-data--Wellbore:{id}",
            LastSubmissionId = submission,
            PendingDocumentRef = "0:0:10",
            PendingRenderContext = "{}",
            PendingMetadataHash = "mh",
            PendingMetadata = true,
        }).ToList();
        await ledger.UpsertPendingAsync(flowId, records);
        foreach (var record in (await ledger.ClaimAsync(flowId, submission, "w", 10, TimeSpan.FromMinutes(5), now)).Records)
        {
            await ledger.CompleteAsync(flowId, new RecordCompletion
            {
                DeliveryKey = record.DeliveryKey, Status = RecordStatus.Delivered, Promote = true, TargetId = record.TargetId, TargetVersion = 1,
                Attempt = new AttemptRecord
                {
                    DeliveryKey = record.DeliveryKey, SubmissionId = submission, Worker = "w", StartedUtc = now, CompletedUtc = now,
                    Outcome = AttemptOutcome.Delivered, Phase = "metadata", TargetVersion = 1,
                },
            });
        }
    }

    private async Task<(AssertionRunner Runner, OsduLedger Ledger, AssertionFlowDefinition Flow)> RunnerAsync(string yaml = Flow, string partition = "dev")
    {
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseAssertion(yaml, "flow.yaml").ForPartition(partition);
        return (new AssertionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<AssertionRunner>(), _platform, allowLoopback: true), ledger, flow);
    }

    private static AssertionOutcome Of(TestResult result, string type, int nth = 0) => result.Assertions.Where(a => a.Type == type).ElementAt(nth);

    /// <summary>Tests of the wellbores a delivery flow wrote, of the logs it did not, and one that gives the index no time.</summary>
    private const string Settling = $$"""
        flowType: assertion
        name: settle-assertion
        partitions: [dev]
        source:
          endpoint: http://localhost
        defaults: { indexSettleSeconds: 120 }
        reliability: { concurrency: 2, retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        tests:
          - name: wellbores
            kind: {{Wellbore}}
            assert:
              - { delivered: wells-delivery }
          - name: wellbore-by-id
            kind: {{Wellbore}}
            ids: ["dev:master-data--Wellbore:w1"]
            assert:
              - count: 1
          - name: wellbores-now
            kind: {{Wellbore}}
            indexSettleSeconds: 0
            assert:
              - count: 4
          - name: logs
            kind: {{WellLog}}
            assert:
              - count: 2
        """;

    [Fact]
    public async Task A_test_of_records_delivered_moments_ago_is_skipped_until_the_index_has_had_time_to_list_them()
    {
        Estate();
        var (runner, ledger, _) = await RunnerAsync(Settling);
        await DeliveredAsync(ledger);
        var delivered = _clock.GetUtcNow().UtcDateTime;
        _clock.Advance(TimeSpan.FromSeconds(30));

        var early = await runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None);

        // The two tests of the type just written are skipped, saying what changed and from when a run judges them; the test of
        // another type, and the one that gives the index no time, are judged. Nothing failed, so the run passed.
        Assert.Equal(AssertionRunStatus.Passed, early.Status);
        Assert.Equal((4, 2, 2), (early.Tests, early.Passed, early.Skipped));
        Assert.Contains("every test evaluated passed; 2 skipped (wellbore-by-id, wellbores)", early.Describe(), StringComparison.Ordinal);
        var results = (await ledger.ListAssertionResultsAsync(early.AssertionRunId)).ToDictionary(r => r.TestName, r => TestResults.Deserialize(r.Detail)!);
        foreach (var name in new[] { "wellbores", "wellbore-by-id" })
        {
            var skipped = results[name];
            Assert.Equal(TestOutcomes.Skipped, skipped.Outcome);
            Assert.Contains("The search index may not list every change to master-data--Wellbore yet", skipped.Error, StringComparison.Ordinal);
            Assert.Contains("within the last 120 s (indexSettleSeconds), wells-delivery@dev wrote 3 record(s)", skipped.Error, StringComparison.Ordinal);
            Assert.Contains($"a run from {delivered.AddSeconds(120):yyyy-MM-dd HH:mm:ss}Z judges it", skipped.Error, StringComparison.Ordinal);
            Assert.All(skipped.Assertions, a => Assert.Equal(TestOutcomes.Skipped, a.Outcome));
        }

        Assert.Equal(TestOutcomes.Passed, results["wellbores-now"].Outcome);
        Assert.Equal(TestOutcomes.Passed, results["logs"].Outcome);

        // Once the window has closed, the same tests are judged on what the index lists: w5 was delivered and OSDU does not hold it.
        _clock.Advance(TimeSpan.FromMinutes(2));
        var settled = await runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal(0, settled.Skipped);
        var judged = (await ledger.ListAssertionResultsAsync(settled.AssertionRunId)).ToDictionary(r => r.TestName, r => TestResults.Deserialize(r.Detail)!);
        Assert.Equal(TestOutcomes.Failed, judged["wellbores"].Outcome);
        Assert.Equal("dev:master-data--Wellbore:w5", Assert.Single(Of(judged["wellbores"], "delivered").Examples).Id);
        Assert.Equal(TestOutcomes.Passed, judged["wellbore-by-id"].Outcome);
    }

    [Fact]
    public async Task A_test_that_does_not_fit_its_template_is_reported_as_such_whatever_changed()
    {
        Estate();
        var (runner, ledger, _) = await RunnerAsync($$"""
            flowType: assertion
            name: settle-misfit
            partitions: [dev]
            source:
              endpoint: http://localhost
            tests:
              - name: misspelled
                kind: {{Wellbore}}
                assert:
                  - { field: data.FacilityNam, exists: true }
            """);
        await DeliveredAsync(ledger);

        var outcome = await runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None);

        var result = TestResults.Deserialize(Assert.Single(await ledger.ListAssertionResultsAsync(outcome.AssertionRunId)).Detail)!;
        Assert.Equal(TestOutcomes.Errored, result.Outcome);
        Assert.Contains("did you mean 'data.FacilityName'", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_kind_of_assertion_is_held_to_what_the_platform_holds_and_the_report_is_kept()
    {
        Estate();
        var (runner, ledger, flow) = await RunnerAsync();
        await DeliveredAsync(ledger);

        // The run comes once the search index has had the time its settle window gives it to list what was delivered.
        _clock.Advance(TimeSpan.FromMinutes(10));
        var outcome = await runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal(AssertionRunStatus.Failed, outcome.Status);
        Assert.Equal(8, outcome.Tests);
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(7, outcome.RowsLoaded);
        var results = (await ledger.ListAssertionResultsAsync(outcome.AssertionRunId)).ToDictionary(r => r.TestName, r => TestResults.Deserialize(r.Detail)!);
        Assert.Equal(8, results.Count);

        var wellbores = results["wellbores"];
        Assert.Equal(TestOutcomes.Failed, wellbores.Outcome);
        Assert.Equal("error", wellbores.Severity);
        Assert.Equal(4, wellbores.Matched);
        Assert.Equal(4, wellbores.Evaluated);
        Assert.NotNull(wellbores.Template);
        Assert.Equal(TestOutcomes.Passed, Of(wellbores, "count").Outcome);
        Assert.Equal(TestOutcomes.Passed, Of(wellbores, "field").Outcome);

        var unique = Of(wellbores, "unique");
        Assert.Equal(TestOutcomes.Failed, unique.Outcome);
        Assert.Equal(2, unique.Failing);
        Assert.Contains("dev:master-data--Wellbore:w2", unique.Examples[0].Id);
        Assert.Contains("dev:master-data--Wellbore:w3", unique.Examples[0].Id);

        var resolves = Of(wellbores, "field", 1);
        Assert.Equal(TestOutcomes.Failed, resolves.Outcome);
        Assert.Equal("dev:master-data--Well:MISSING", Assert.Single(resolves.Examples).Value);
        Assert.Equal(1, resolves.Failing);

        Assert.Equal(TestOutcomes.Passed, Of(wellbores, "aggregate").Outcome);
        Assert.Equal(4, Of(wellbores, "aggregate").Value);
        Assert.Equal(TestOutcomes.Passed, Of(wellbores, "groupBy").Outcome);
        Assert.Equal(TestOutcomes.Passed, Of(wellbores, "groupBy", 1).Outcome);
        Assert.Contains(_platform.Calls, c => c.Body?.Contains("\"aggregateBy\":\"data.FacilityName.keyword\"", StringComparison.Ordinal) == true);

        var indexed = Of(wellbores, "indexed");
        Assert.Equal(TestOutcomes.Failed, indexed.Outcome);
        Assert.Equal("dev:master-data--Wellbore:w2", Assert.Single(indexed.Examples).Id);
        Assert.Equal("400", indexed.Examples[0].Value);

        var legal = Of(wellbores, "legal");
        Assert.Equal(TestOutcomes.Failed, legal.Outcome);
        Assert.Contains(legal.Examples, e => e.Value == "dev-expired" && e.Reason.Contains("LegalTag has expired", StringComparison.Ordinal));
        Assert.Contains(legal.Examples, e => e.Id == "dev:master-data--Wellbore:w4" && e.Reason == "carries no legal tag");

        var conforms = Of(wellbores, "conforms");
        Assert.Equal(TestOutcomes.Failed, conforms.Outcome);
        Assert.Equal("warning", conforms.Severity);
        Assert.Contains("legal: the record has no legal, which the schema requires", Assert.Single(conforms.Examples).Reason);

        Assert.Equal(TestOutcomes.Passed, Of(wellbores, "recordSet").Outcome);
        Assert.Equal(TestOutcomes.Passed, Of(wellbores, "field", 2).Outcome);

        var delivered = Of(wellbores, "delivered");
        Assert.Equal(TestOutcomes.Failed, delivered.Outcome);
        var missing = Assert.Single(delivered.Examples);
        Assert.Equal("dev:master-data--Wellbore:w5", missing.Id);
        Assert.Equal("w5", missing.Value);

        Assert.Equal(TestOutcomes.Passed, results["names"].Outcome);

        var logs = results["logs"];
        Assert.Equal(TestOutcomes.Warned, logs.Outcome);
        Assert.Equal(TestOutcomes.Passed, Of(logs, "rowCount").Outcome);
        Assert.Equal(TestOutcomes.Passed, Of(logs, "columns").Outcome);
        var gr = Of(logs, "column");
        Assert.Equal(TestOutcomes.Failed, gr.Outcome);
        Assert.Equal("dev:work-product-component--WellLog:l2", Assert.Single(gr.Examples).Id);
        Assert.Contains("row 7: 400", gr.Examples[0].Value, StringComparison.Ordinal);
        // Row 8's gap comes back as the DDMS's "NaN", which an optional condition passes over as no value.
        Assert.Equal(TestOutcomes.Passed, Of(logs, "column", 1).Outcome);
        var md = Of(logs, "monotonic");
        Assert.Equal(TestOutcomes.Failed, md.Outcome);
        Assert.Contains("is not increasing at row 12", Assert.Single(md.Examples).Reason);
        Assert.Equal(TestOutcomes.Passed, Of(logs, "aggregate").Outcome);

        var byId = results["by-id"];
        Assert.Equal(TestOutcomes.Failed, byId.Outcome);
        Assert.Equal(1, byId.Matched);
        Assert.Equal("dev:master-data--Wellbore:nope", Assert.Single(Of(byId, "count").Examples).Id);
        Assert.Contains(byId.Notes, n => n.Contains("1 of the 2 ids are not in storage", StringComparison.Ordinal));

        Assert.Equal(TestOutcomes.Skipped, results["in-test-only"].Outcome);

        var misspelled = results["misspelled"];
        Assert.Equal(TestOutcomes.Errored, misspelled.Outcome);
        Assert.Contains("did you mean 'data.FacilityName'", Assert.Single(misspelled.Problems));
        Assert.All(misspelled.Assertions, a => Assert.Equal(TestOutcomes.Skipped, a.Outcome));

        var tooMany = results["too-many"];
        Assert.Equal(TestOutcomes.Errored, tooMany.Outcome);
        Assert.Equal(TestOutcomes.Passed, Of(tooMany, "count").Outcome);
        Assert.Contains("more than the 2 this test reads", Of(tooMany, "field").Message);

        var sampled = results["sampled"];
        Assert.Equal(TestOutcomes.Passed, sampled.Outcome);
        Assert.True(sampled.Sampled);
        Assert.Equal(2, sampled.Evaluated);
        Assert.Contains(sampled.Notes, n => n.Contains("the first 2 were evaluated as a sample", StringComparison.Ordinal));

        // The run's row adds the results up, and every request went as the services' contracts say.
        var run = (await ledger.GetAssertionRunAsync(outcome.AssertionRunId))!;
        Assert.Equal("dev", run.Partition);
        Assert.Equal(new AssertionCounts(8, 2, 2, 1, 2, 1), run.Counts);
        Assert.NotNull(run.CompletedUtc);
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search, OsduContracts.Storage, OsduContracts.Legal, OsduContracts.WellboreDdms);
        Assert.Contains(_platform.Calls, c => c.Method == HttpMethod.Delete);
        Assert.DoesNotContain(_platform.Calls, c => c.Body?.Contains("FacilityNam\"", StringComparison.Ordinal) == true);
        Assert.All(_platform.Calls, c => Assert.Equal("dev", c.Headers["data-partition-id"]));
    }

    [Fact]
    public async Task The_report_renders_every_result_in_every_format()
    {
        Estate();
        var (runner, ledger, _) = await RunnerAsync();
        await DeliveredAsync(ledger);

        // The run comes once the search index has had the time its settle window gives it to list what was delivered.
        _clock.Advance(TimeSpan.FromMinutes(10));
        var outcome = await runner.TestAsync([], ["smoke", "bulk"], Guid.NewGuid(), "gui:someone", CancellationToken.None);
        var run = (await ledger.GetAssertionRunAsync(outcome.AssertionRunId))!;
        var results = (await ledger.ListAssertionResultsAsync(run.AssertionRunId)).Select(r => TestResults.Deserialize(r.Detail)!).ToList();
        var report = new AssertionReport(run, results, _clock.GetUtcNow().UtcDateTime);

        var junit = report.Render(ReportFormat.JUnit);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", junit);
        Assert.Contains("<testsuites name=\"welldb-04-assertion@dev\" tests=\"3\" failures=\"1\" errors=\"0\" skipped=\"0\"", junit);
        Assert.Contains("<testcase classname=\"welldb-04-assertion.osdu:wks:master-data--Wellbore:1.3.0\" name=\"wellbores\"", junit);
        Assert.Contains("<failure message=", junit);
        Assert.Contains("<system-out>", junit);

        var html = report.Render(ReportFormat.Html);
        Assert.Contains("<title>welldb-04-assertion test report</title>", html);
        Assert.Contains("dev:master-data--Well:MISSING", html);
        // A warning that fails is badged as a warning, not as a failure.
        Assert.Contains("<td class=\"fit\">warning</td><td class=\"fit\"><span class=\"badge warned\">warned</span>", html);
        Assert.DoesNotContain("<td class=\"fit\">warning</td><td class=\"fit\"><span class=\"badge failed\">", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);

        var markdown = report.Render(ReportFormat.Markdown);
        Assert.Contains("# welldb-04-assertion in dev", markdown);
        Assert.Contains("| Assertion | Severity | Outcome | Expected | Actual |", markdown);

        var json = JsonNode.Parse(report.Render(ReportFormat.Json))!;
        Assert.Equal(3, json["results"]!.AsArray().Count);
        Assert.Equal("""{"tags":["smoke","bulk"]}""", json["selection"]!.ToJsonString());
        Assert.Equal("welldb-04-assertion-dev-report-" + run.AssertionRunId + ".xml", report.FileName(ReportFormat.JUnit));
        foreach (var text in new[] { junit, html, markdown })
        {
            Assert.DoesNotContain('\u2014', text);
        }
    }

    [Fact]
    public async Task A_run_names_its_tests_and_the_board_reads_each_ones_latest_result()
    {
        Estate();
        var (runner, ledger, flow) = await RunnerAsync();
        var first = await runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await runner.TestAsync(["names"], [], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(1, second.Tests);
        Assert.Equal(AssertionRunStatus.Passed, second.Status);

        var latest = (await ledger.LatestAssertionResultsAsync([flow.LedgerId])).ToDictionary(r => r.TestName);
        Assert.Equal(second.AssertionRunId, latest["names"].AssertionRunId);
        Assert.Equal(first.AssertionRunId, latest["wellbores"].AssertionRunId);
        Assert.False(latest.ContainsKey("in-test-only"));
        Assert.Equal(string.Empty, latest["names"].Detail);

        var history = await ledger.AssertionHistoryAsync(flow.LedgerId, "names", 10, withDetail: true);
        Assert.Equal([second.AssertionRunId, first.AssertionRunId], history.Select(h => h.AssertionRunId));
        Assert.NotNull(TestResults.Deserialize(history[0].Detail));
        Assert.Equal(2, (await ledger.ListAssertionRunsAsync(flow.LedgerId, 10)).Count);

        // Pruning takes a run whole once a later result superseded every one of its results: the first run still holds the
        // latest result of wellbores, so it stays, and its report stays whole, the superseded result of names included.
        _clock.Advance(TimeSpan.FromDays(10));
        var pruned = await ledger.PruneAssertionRunsAsync(_clock.GetUtcNow().UtcDateTime);
        Assert.Equal(0, pruned);
        var afterFirst = (await ledger.ListAssertionResultsAsync(first.AssertionRunId)).Select(r => r.TestName).ToList();
        Assert.Contains("names", afterFirst);
        Assert.Contains("wellbores", afterFirst);
        var third = await runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal(2, await ledger.PruneAssertionRunsAsync(_clock.GetUtcNow().UtcDateTime));
        Assert.Equal([third.AssertionRunId], (await ledger.ListAssertionRunsAsync(flow.LedgerId, 10)).Select(r => r.AssertionRunId));
    }

    [Fact]
    public async Task A_failing_service_errors_the_tests_it_stops_and_no_other()
    {
        Estate();
        _platform.Fail = request => request.RequestUri!.AbsolutePath.EndsWith("/ddms/v3/welllogs/dev:work-product-component--WellLog:l2/data", StringComparison.Ordinal)
            ? HttpStatusCode.InternalServerError
            : null;
        var (runner, ledger, _) = await RunnerAsync();
        var outcome = await runner.TestAsync(["logs", "names"], [], Guid.NewGuid(), "tests", CancellationToken.None);
        var results = (await ledger.ListAssertionResultsAsync(outcome.AssertionRunId)).ToDictionary(r => r.TestName, r => TestResults.Deserialize(r.Detail)!);
        Assert.Equal(TestOutcomes.Errored, results["logs"].Outcome);
        Assert.Contains("500", results["logs"].Error);
        Assert.All(results["logs"].Assertions, a => Assert.Equal(TestOutcomes.Errored, a.Outcome));
        Assert.Equal(TestOutcomes.Passed, results["names"].Outcome);
        Assert.Equal(AssertionRunStatus.Errored, outcome.Status);
    }

    [Fact]
    public async Task A_plan_checks_and_counts_every_test_and_records_nothing()
    {
        Estate();
        var (runner, ledger, flow) = await RunnerAsync();
        var plan = await runner.PlanAsync([], [], CancellationToken.None);
        var tests = plan.Tests.ToDictionary(t => t.Test);
        Assert.Equal(4, tests["wellbores"].Matched);
        Assert.Equal(4, tests["wellbores"].WouldRead);
        Assert.Empty(tests["wellbores"].Problems);
        Assert.Equal(2, tests["by-id"].Ids);
        Assert.NotNull(tests["in-test-only"].Skipped);
        Assert.Contains("did you mean", Assert.Single(tests["misspelled"].Problems));
        Assert.Contains("more than the 2 the test reads", tests["too-many"].Error);
        Assert.Empty(await ledger.ListAssertionRunsAsync(flow.LedgerId, 10));
        Assert.All(_platform.Calls, c => Assert.EndsWith("/api/search/v2/query", c.Uri.AbsolutePath));
    }

    [Fact]
    public async Task A_run_fails_as_the_flow_says()
    {
        Estate();
        var (runner, _, flow) = await RunnerAsync();
        var outcome = await runner.TestAsync(["logs"], [], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(1, outcome.Warned);
        Assert.False(AssertionExecutor.FailsRun(flow with { FailRunOn = FailRunOn.Error }, outcome));
        Assert.True(AssertionExecutor.FailsRun(flow with { FailRunOn = FailRunOn.Warning }, outcome));
        Assert.False(AssertionExecutor.FailsRun(flow with { FailRunOn = FailRunOn.Never }, outcome with { Failed = 3, Errored = 1 }));
        Assert.Contains("1 warned (logs)", new AssertionTestsFailedException(outcome).Message);
    }
}
