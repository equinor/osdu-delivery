using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The reason a test run's report keeps on its row in the ledger, which <c>sqlflow assertions list</c> prints under the run:
/// a run its tests failed says how many failed and the severity that failed them, beside the tests that errored or warned;
/// a run that passed keeps none.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class AssertionRunReasonTests : IDisposable
{
    private const string Wellbore = Samples.WellboreKind;

    private const string Flow = $$"""
        flowType: assertion
        name: welldb-wellbore-04-assertion
        partitions: [dev]
        source:
          endpoint: http://localhost
        defaults: { examples: 5, indexSettleSeconds: 0 }
        reliability: { concurrency: 2, retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        tests:
          - name: counted
            kind: {{Wellbore}}
            assert:
              - count: 3
          - name: also-counted
            kind: {{Wellbore}}
            assert:
              - count: { atLeast: 5 }
              - { field: data.FacilityName, exists: true, severity: warning }
          - name: names
            kind: {{Wellbore}}
            assert:
              - { field: data.FacilityName, startsWith: "Wellbore B", severity: warning }
          - name: present
            kind: {{Wellbore}}
            assert:
              - count: 2
        """;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeAssertionPlatform _platform = new();

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    /// <summary>A flow whose tests either pass or cannot be evaluated: one names a field its kind does not have.</summary>
    private static string Erroring(string endpoint = "http://localhost", string misspelledPartitions = "") => $$"""
        flowType: assertion
        name: welldb-wellbore-05-assertion
        partitions: [dev, test]
        source:
          endpoint: {{endpoint}}
        defaults: { indexSettleSeconds: 0 }
        reliability: { concurrency: 1, retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        tests:
          - name: present
            {{(misspelledPartitions.Length > 0 ? "partitions: [test]" : string.Empty)}}
            kind: {{Wellbore}}
            assert:
              - count: 2
          - name: misspelled
            {{misspelledPartitions}}
            kind: {{Wellbore}}
            assert:
              - { field: data.FacilityNam, exists: true }
        """;

    private async Task<(AssertionRunner Runner, OsduLedger Ledger)> RunnerAsync(string yaml = Flow)
    {
        _platform.Add("dev:master-data--Wellbore:WB-0001", Wellbore, new JsonObject { ["FacilityName"] = "Wellbore A-1" });
        _platform.Add("dev:master-data--Wellbore:WB-0002", Wellbore, new JsonObject { ["FacilityName"] = "Wellbore A-2" });
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseAssertion(yaml, "flow.yaml").ForPartition("dev");
        return (new AssertionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<AssertionRunner>(), _platform, allowLoopback: true), ledger);
    }

    /// <summary>How many tests the run recorded a result for, whatever it came to.</summary>
    private static int Recorded(AssertionRunState run) => run.Counts.Passed + run.Counts.Failed + run.Counts.Warned + run.Counts.Errored + run.Counts.Skipped;

    [Fact]
    public async Task A_run_whose_tests_errored_completes_with_how_many_errored_and_which_and_records_every_test()
    {
        var (runner, ledger) = await RunnerAsync(Erroring());

        var outcome = await runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None);

        var row = (await ledger.GetAssertionRunAsync(outcome.AssertionRunId))!;
        Assert.Equal(AssertionRunStatus.Errored, row.Status);
        Assert.Equal("2 of 2 test(s) evaluated, 1 passed; 1 errored (misspelled)", row.Error);
        Assert.Equal(row.Counts.Tests, Recorded(row));
    }

    [Fact]
    public async Task A_run_that_stops_part_way_keeps_why_and_leaves_tests_unrecorded_which_tells_it_from_a_run_whose_tests_errored()
    {
        // The endpoint names a variable this node does not set: the run cannot reach OSDU, so it stops before judging a test.
        var (runner, ledger) = await RunnerAsync(Erroring("${env:OSDU_DELIVERY_TESTS_UNSET_ENDPOINT}"));

        await Assert.ThrowsAnyAsync<Exception>(() => runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None));

        var row = Assert.Single(await ledger.ListAssertionRunsAsync(new DeliveryDocumentLoader().ParseAssertion(Erroring(), "flow.yaml").ForPartition("dev").LedgerId, 10));
        Assert.Equal(AssertionRunStatus.Errored, row.Status);
        Assert.Contains("OSDU_DELIVERY_TESTS_UNSET_ENDPOINT", row.Error, StringComparison.Ordinal);
        Assert.True(Recorded(row) < row.Counts.Tests, $"a run that stopped recorded {Recorded(row)} of its {row.Counts.Tests} tests");
    }

    [Fact]
    public async Task A_run_with_no_test_to_judge_in_its_partition_asks_OSDU_nothing_and_completes()
    {
        // Every test runs only in 'test'; the endpoint could not be reached from here, and is never asked.
        var (runner, ledger) = await RunnerAsync(Erroring("${env:OSDU_DELIVERY_TESTS_UNSET_ENDPOINT}", "partitions: [test]"));

        var outcome = await runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None);

        var row = (await ledger.GetAssertionRunAsync(outcome.AssertionRunId))!;
        Assert.Equal((AssertionRunStatus.Passed, (string?)null, 2), (row.Status, row.Error, row.Counts.Skipped));
        Assert.Equal(row.Counts.Tests, Recorded(row));
        Assert.Empty(_platform.Calls);
    }

    [Fact]
    public async Task A_run_its_tests_failed_keeps_how_many_failed_and_the_severity_that_failed_them_and_a_run_that_passed_keeps_no_reason()
    {
        var (runner, ledger) = await RunnerAsync();

        var failed = await runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal(AssertionRunStatus.Failed, failed.Status);
        var row = (await ledger.GetAssertionRunAsync(failed.AssertionRunId))!;
        Assert.Equal(AssertionRunStatus.Failed, row.Status);
        Assert.Equal("4 of 4 test(s) evaluated, 1 passed; 2 failed on an assertion of severity error (also-counted, counted); 1 warned (names)", row.Error);

        // The platform run's error says the same after the flow and partition, so the report and the run never disagree.
        Assert.Equal($"assertion flow 'welldb-wellbore-04-assertion' in partition 'dev': {row.Error}.", failed.Describe());
        Assert.Equal(row.Error, Assert.Single(await ledger.ListAssertionRunsAsync(row.FlowId, 10)).Error);

        // A run whose tests only warned passed, and keeps no reason.
        _clock.Advance(TimeSpan.FromMinutes(1));
        var warned = await runner.TestAsync(["names", "present"], [], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(AssertionRunStatus.Passed, warned.Status);
        Assert.Null((await ledger.GetAssertionRunAsync(warned.AssertionRunId))!.Error);
    }
}
