using System.Text.Json.Nodes;
using System.Web;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Curve proof values held to the bulk data a fake Wellbore DDMS holds (osdu/docs/reference/flow/known-values.md, Curve proof
/// values): every measure of a log's curves holding, each way one differs, bulk data that cannot be measured, a log of dates,
/// the warning band, and the read asking the DDMS for only the curves the rows need.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CurveProofValueRunTests : IDisposable
{
    private const string Wellbore = Samples.WellboreKind;

    private const string WellLog = Samples.WellLogKind;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeAssertionPlatform _platform = new();

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    /// <summary>A depth of the COMPOSITE log: 1000 m and every 0.1524 m after it, exactly as a source writes them.</summary>
    private static double Depth(int row) => (double)(1000m + (0.1524m * row));

    private void Log(string id, string name, string? referenceCurve, string[] columns, List<object?[]>? rows)
    {
        var data = new JsonObject { ["WellboreID"] = "dev:master-data--Wellbore:wb1:", ["Name"] = name };
        if (referenceCurve is not null)
        {
            data["ReferenceCurveID"] = referenceCurve;
        }

        var recordId = $"dev:work-product-component--WellLog:{id}";
        _platform.Add(recordId, WellLog, data);
        if (rows is not null)
        {
            _platform.Bulk[recordId] = (columns, rows);
        }
    }

    /// <summary>
    /// The wellbore A/1-F-11 and its logs: COMPOSITE (MD every 0.1524 m from 1000 m, GR with a gap at either end, DT from its
    /// fourth row), RUN2 naming no reference curve, NOBULK with no bulk data, BIG holding more rows than the test reads, SHORT
    /// answering fewer rows than it is described as, TIMELOG indexed by time, and DUP whose index holds a depth twice.
    /// </summary>
    private void Estate()
    {
        _platform.Add("dev:master-data--Wellbore:wb1", Wellbore, new JsonObject { ["FacilityName"] = "A/1-F-11" });
        double?[] gr = [null, 50, 60, 70, 80, 90, 100, 110, 120, null];
        double?[] dt = [null, null, null, 200, 210, 220, 230, 240, 250, 260];
        Log("comp", "COMPOSITE", "MD", ["MD", "GR", "DT"], Enumerable.Range(0, 10).Select(i => new object?[] { Depth(i), gr[i], dt[i] }).ToList());
        Log("run2", "RUN2", null, ["MD", "GR"], Enumerable.Range(0, 5).Select(i => new object?[] { Depth(i), 10.0 * (i + 1) }).ToList());
        Log("nobulk", "NOBULK", "MD", [], null);
        Log("big", "BIG", "MD", ["MD", "GR"], Enumerable.Range(0, 60).Select(i => new object?[] { Depth(i), 1.0 }).ToList());
        Log("short", "SHORT", "MD", ["MD", "GR"], Enumerable.Range(0, 10).Select(i => new object?[] { Depth(i), 1.0 }).ToList());
        _platform.DescribedRows["dev:work-product-component--WellLog:short"] = 12;
        Log("time", "TIMELOG", "TIME", ["TIME", "GR"], Enumerable.Range(0, 4)
            .Select(i => new object?[] { $"2026-01-01T00:0{i}:00Z", i == 0 ? null : 1.5 * i }).ToList());
        Log("dup", "DUP", "MD", ["MD", "GR"], [[1.0, 1.0], [2.0, 2.0], [2.0, 3.0], [3.0, 4.0]]);
    }

    private const string Flow = $$"""
        flowType: assertion
        name: curve-proof
        partitions: [dev]
        source:
          endpoint: http://localhost
        defaults: { examples: 50, indexSettleSeconds: 0 }
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 2 }
        tests:
          - name: holding
            kind: {{WellLog}}
            bulk: { maxRows: 50 }
            assert:
              - name: the curves of the COMPOSITE log hold what the source states
                knownValues:
                  locateBy: { '[data.WellboreID, data.FacilityName]': A/1-F-11, data.Name: COMPOSITE }
                  curves:
                    MD: { count: 10, missing: 0, min: 1000, max: 1001.3716, first: 1000, last: 1001.3716, step: 0.1524 }
                    GR: { count: 8, missing: 2, min: 50, max: 120, sum: 680, avg: 85, first: 1000.1524, last: 1001.2192, at: { 1000.1524: 50, 1000.3048: { equals: 60.2, tolerance: 0.5 } } }
                    DT: { count: 7, first: 1000.4572, last: 1001.3716, step: { between: [9, 11] }, at: { 1001.3716: 260 } }
              - name: a log of times, indexed by the curve the assertion names
                knownValues:
                  curveIndex: TIME
                  locateBy: { data.Name: TIMELOG }
                  curves:
                    TIME: { min: "2026-01-01T00:00:00Z", max: "2026-01-01T00:03:00Z" }
                    GR: { count: 3, first: "2026-01-01T00:01:00Z", last: "2026-01-01T00:03:00Z", max: 4.5, at: { "2026-01-01T00:02:00Z": 3 } }
          - name: differing
            kind: {{WellLog}}
            bulk: { maxRows: 50 }
            assert:
              - name: logs whose curves differ from the source
                knownValues:
                  rows:
                    - locateBy: { data.Name: COMPOSITE }
                      curves:
                        GR:
                          max: 125
                          count: 8
                          avg: { equals: 99, disabled: the mean is restated next release }
                          at: { 1000.4572: 75, 1000.1: 1, 2000: 1, 1000: 5 }
                        MD: { step: 0.3048 }
                        CALI: { count: 1 }
                    - locateBy: { data.Name: RUN2 }
                      curves:
                        GR: { first: 1000, count: 5 }
                    - locateBy: { data.Name: NOBULK }
                      curves:
                        GR: { count: 1 }
                    - locateBy: { data.Name: BIG }
                      curves:
                        GR: { count: 60 }
                    - locateBy: { data.Name: SHORT }
                      curves:
                        GR: { count: 12 }
                    - locateBy: { data.Name: DUP }
                      curves:
                        GR: { at: { 2: 2, 3: 4 } }
          - name: within-band
            kind: {{WellLog}}
            bulk: { maxRows: 50 }
            assert:
              - name: the COMPOSITE log's numbers a little off
                knownValues:
                  warnWithin: 2%
                  locateBy: { data.Name: COMPOSITE }
                  curves:
                    GR: { avg: 86, max: 121 }
        """;

    private async Task<IReadOnlyDictionary<string, TestResult>> RunAsync()
    {
        Estate();
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseAssertion(Flow, "flow.yaml").ForPartition("dev");
        var runner = new AssertionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<AssertionRunner>(), _platform, allowLoopback: true);
        var outcome = await runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None);
        return (await ledger.ListAssertionResultsAsync(outcome.AssertionRunId)).ToDictionary(r => r.TestName, r => TestResults.Deserialize(r.Detail)!);
    }

    [Fact]
    public async Task A_log_whose_curves_hold_every_measure_the_source_states_holds_its_row_and_reads_only_the_curves_it_needs()
    {
        var results = await RunAsync();
        var holding = results["holding"];
        Assert.Equal(TestOutcomes.Passed, holding.Outcome);
        var composite = holding.Assertions[0];
        Assert.Equal(TestOutcomes.Passed, composite.Outcome);
        Assert.Contains("1 row(s) with 22 proof value(s): 1 hold their values", composite.Actual, StringComparison.Ordinal);
        Assert.Contains("the curves of 1 record(s) measured", composite.Actual, StringComparison.Ordinal);
        Assert.Equal(TestOutcomes.Passed, holding.Assertions[1].Outcome);

        // Each test reads the COMPOSITE log's curves once, asking for the curves its rows name and the index its first and last
        // values are read at, and nothing else: all three here; GR and MD (CALI is not among the log's curves, so it is not
        // asked for); GR alone.
        var reads = _platform.Calls
            .Where(c => c.Uri.AbsolutePath.EndsWith("WellLog%3Acomp/data", StringComparison.Ordinal) || c.Uri.AbsolutePath.EndsWith("WellLog:comp/data", StringComparison.Ordinal))
            .Select(c => HttpUtility.ParseQueryString(c.Uri.Query))
            .Where(q => q["describe"] is null)
            .Select(q => string.Join(",", q["curves"]!.Split(',').Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["DT,GR,MD", "GR", "GR,MD"], reads);
    }

    [Fact]
    public async Task A_log_differing_from_the_source_says_which_curve_measures_differ_and_why_bulk_data_cannot_be_measured()
    {
        var results = await RunAsync();
        var differing = Assert.Single(results["differing"].Assertions);
        Assert.Equal(TestOutcomes.Failed, differing.Outcome);
        Assert.Equal(6, differing.Failing);
        Assert.Contains("disabled: 0 row(s), 1 proof value(s)", differing.Actual, StringComparison.Ordinal);

        string Reason(string row) => Assert.Single(differing.Examples, e => e.Value == row).Reason;

        var composite = Reason("COMPOSITE");
        Assert.Contains("differs in 7 proof value(s)", composite, StringComparison.Ordinal);
        Assert.Contains("GR max is 120, expected it equals 125", composite, StringComparison.Ordinal);
        Assert.Contains("MD step is 0.1524 in every one of its 9 steps, expected each step equals 0.3048", composite, StringComparison.Ordinal);
        Assert.Contains("CALI is not among the curves of its bulk data (MD, GR, DT)", composite, StringComparison.Ordinal);
        Assert.DoesNotContain("GR count", composite, StringComparison.Ordinal);
        Assert.DoesNotContain("GR avg", composite, StringComparison.Ordinal);

        // A curve's value at a key: the value in the row whose index holds it, as written; where no row holds it, the rows
        // either side, or the index's first and last value; a row holding the key without a value of the curve.
        Assert.Contains("GR at 1000.4572 is 70 (row 4), expected it equals 75", composite, StringComparison.Ordinal);
        Assert.Contains("GR at 1000.1 has no row: the index MD holds no 1000.1 (nearest: 1000 in row 1 and 1000.1524 in row 2); expected it equals 1", composite, StringComparison.Ordinal);
        Assert.Contains("GR at 2000 has no row: the index MD holds no 2000 (it runs from 1000 in row 1 to 1001.3716 in row 10); expected it equals 1", composite, StringComparison.Ordinal);
        Assert.Contains("GR at 1000 has no value (row 1); expected it equals 5", composite, StringComparison.Ordinal);
        var dup = Reason("DUP");
        Assert.Contains("differs in 1 proof value(s): GR at 2 is in 2 rows (2, 3), and a key names one row; expected it equals 2", dup, StringComparison.Ordinal);

        Assert.Contains("GR first cannot be read: the record names no data.ReferenceCurveID", Reason("RUN2"), StringComparison.Ordinal);
        Assert.DoesNotContain("GR count", Reason("RUN2"), StringComparison.Ordinal);
        Assert.Contains("its curves cannot be measured: the DDMS holds no bulk data for the record", Reason("NOBULK"), StringComparison.Ordinal);
        Assert.Contains("its bulk data holds 60 rows, more than the test's bulk.maxRows 50", Reason("BIG"), StringComparison.Ordinal);
        Assert.Contains("the DDMS describes its bulk data as 12 rows and answered 10", Reason("SHORT"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Curve_measures_missing_by_no_more_than_the_warning_band_warn()
    {
        var results = await RunAsync();
        var banded = Assert.Single(results["within-band"].Assertions);
        Assert.Equal(TestOutcomes.Failed, banded.Outcome);
        Assert.Equal("warning", banded.Severity);
        var row = Assert.Single(banded.Examples);
        Assert.Contains("differs within the warning band in 2 proof value(s)", row.Reason, StringComparison.Ordinal);
        Assert.Contains("GR avg is 85, expected it equals 86", row.Reason, StringComparison.Ordinal);
        Assert.Equal(TestOutcomes.Warned, results["within-band"].Outcome);
    }
}
