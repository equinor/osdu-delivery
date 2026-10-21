using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Known values held to what a fake OSDU platform holds: logs located by the name of the wellbore they belong to, curves by
/// their mnemonic, wellbores by what their logs hold, and every verdict a row comes to, with the searches that found them
/// and the report the run keeps on SQL Server.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class KnownValuesRunTests : IDisposable
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

    private static JsonObject Curve(string id, string unit, double top)
        => new() { ["CurveID"] = id, ["Mnemonic"] = id, ["CurveUnit"] = $"dev:reference-data--UnitOfMeasure:{unit}:", ["TopDepth"] = top };

    private void Log(string id, string wellbore, string name, string project, double start, params JsonObject[] curves)
    {
        var record = _platform.Add($"dev:work-product-component--WellLog:{id}", WellLog, new JsonObject
        {
            ["WellboreID"] = wellbore,
            ["Name"] = name,
            ["SamplingStart"] = start,
            ["LogSource"] = "OPERATOR",
            ["Curves"] = new JsonArray(new[] { Curve("MD", "m", start) }.Concat(curves).Select(c => (JsonNode?)c).ToArray()),
        });
        record["tags"] = new JsonObject { ["SourceProject"] = project };
    }

    /// <summary>
    /// Three wellbores (one whose name starts as another's does), and the logs naming them: a COMPOSITE and a INTERPRETATION log of
    /// A/1-F-11 in NORTH (the second naming its wellbore with a version), one in SOUTH, two COMPOSITE logs of
    /// A/1-F-12 in one project, and one of A/1-F-11 A.
    /// </summary>
    private void Estate()
    {
        _platform.Add("dev:master-data--Wellbore:wb1", Wellbore, new JsonObject { ["FacilityName"] = "A/1-F-11", ["WellID"] = "dev:master-data--Well:W1:" });
        _platform.Add("dev:master-data--Wellbore:wb2", Wellbore, new JsonObject { ["FacilityName"] = "A/1-F-11 A", ["WellID"] = "dev:master-data--Well:W1:" });
        _platform.Add("dev:master-data--Wellbore:wb3", Wellbore, new JsonObject { ["FacilityName"] = "A/1-F-12", ["WellID"] = "dev:master-data--Well:W2:" })
            ["createTime"] = "2026-09-05T10:00:00.000Z";
        Log("comp", "dev:master-data--Wellbore:wb1:", "COMPOSITE", "NORTH", 205.1304, Curve("DT", "us%2Fft", 1023.0612), Curve("GR", "API", 205.1304));
        Log("cpi", "dev:master-data--Wellbore:wb1:1700000000000000", "INTERPRETATION", "NORTH", 1881.8352, Curve("PHIT", "v%2Fv", 1891.7412));
        Log("south", "dev:master-data--Wellbore:wb1:", "COMPOSITE", "SOUTH", 205.1304, Curve("RD", "ohm.m", 216.2556));
        Log("twin1", "dev:master-data--Wellbore:wb3:", "COMPOSITE", "NORTH", 138.0744);
        Log("twin2", "dev:master-data--Wellbore:wb3:", "COMPOSITE", "NORTH", 138.0744);
        Log("sidetrack", "dev:master-data--Wellbore:wb2:", "COMPOSITE", "NORTH", 300);
    }

    private const string Flow = $$"""
        flowType: assertion
        name: qc-a-1-f-11
        partitions: [dev]
        source:
          endpoint: http://localhost
        defaults: { examples: 50, indexSettleSeconds: 0 }
        reliability: { concurrency: 2, retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        tests:
          - name: curves
            kind: {{WellLog}}
            assert:
              - name: the COMPOSITE curves of A/1-F-11
                knownValues:
                  locateBy:
                    '[data.WellboreID, data.FacilityName]': A/1-F-11
                    tags.SourceProject: NORTH
                    data.Name: COMPOSITE
                  check:
                    data.SamplingStart: { equals: 205.13, tolerance: 0.001 }
                  rows:
                    - locateBy: { data.Curves.CurveID: DT }
                      check:
                        data.Curves.CurveUnit: "{partition}:reference-data--UnitOfMeasure:us%2Fft:"
                        data.Curves.TopDepth: 1023.0612
                        data.LogSource: { equals: CONTRACTOR, disabled: the log source is renamed next release }
                    - locateBy: { data.Curves.CurveID: GR }
                      check:
                        data.Curves.CurveUnit: "{partition}:reference-data--UnitOfMeasure:gAPI:"
                    - locateBy: { data.Curves.CurveID: BS }
                    - locateBy: { data.Curves.CurveID: CALI }
                      disabled: CALI is not delivered yet
          - name: logs
            kind: {{WellLog}}
            asOf: 2026-09-01
            assert:
              - name: logs located through their wellbore
                knownValues:
                  rows:
                    - locateBy: { '[data.WellboreID, data.FacilityName]': A/1-F-11, tags.SourceProject: NORTH, data.Name: INTERPRETATION }
                      check:
                        data.Curves.CurveID: { aggregate: count, except: [MD], equals: 1 }
                        '[data.WellboreID, data.WellID]': "{partition}:master-data--Well:W1:"
                    - locateBy: { '[data.WellboreID, data.FacilityName]': A/1-F-12, tags.SourceProject: NORTH, data.Name: COMPOSITE }
              - name: logs OSDU does not hold
                knownValues:
                  notFound: warning
                  rows:
                    - locateBy: { '[data.WellboreID, data.FacilityName]': B/2-X-7, data.Name: COMPOSITE }
                    - locateBy: { '[data.WellboreID, data.FacilityName]': A/1-F-11, data.Name: PRESSURE }
              - name: a check switched off
                disabled: kept for when the PLT logs arrive
                count: 99
          - name: wellbores
            kind: {{Wellbore}}
            assert:
              - name: wellbores and the logs naming them
                knownValues:
                  rows:
                    - locateBy: { data.FacilityName: A/1-F-11 }
                      check:
                        '[work-product-component--WellLog[data.WellboreID], data.Name]': { in: [COMPOSITE, INTERPRETATION] }
                        '[work-product-component--WellLog[data.WellboreID], data.Curves.CurveID]': { aggregate: count, except: [MD], equals: 4 }
                        '[work-product-component--WellLog[data.WellboreID], data.WellboreID, data.FacilityName]': A/1-F-11
                    - locateBy: { '[work-product-component--WellLog[data.WellboreID], tags.SourceProject]': SOUTH }
                      check:
                        data.FacilityName: A/1-F-11
          - name: switched-off
            kind: {{WellLog}}
            disabled: OSDU holds no logs of this field yet
            assert:
              - count: 1
          - name: stale
            kind: {{WellLog}}
            asOf: 2025-10-13
            validUntil: 2025-12-31
            assert:
              - count: 7842
          - name: partly-stale
            kind: {{Wellbore}}
            assert:
              - count: 3
              - aggregate: count
                field: data.FacilityName
                equals: 3
                asOf: 2026-01-01
                validFor: 30d
          - name: as-of
            kind: {{Wellbore}}
            asOf: 2026-09-01
            asOfField: createTime
            assert:
              - count: 2
          - name: within-band
            kind: {{WellLog}}
            assert:
              - name: six logs, stated as seven
                count: 7
                warnWithin: 1
              - name: four curves, stated as 4.2
                aggregate: count
                field: data.Curves.CurveID
                except: [MD]
                equals: 4.2
                warnWithin: 5%
              - name: the COMPOSITE log of A/1-F-11, its numbers a little off
                knownValues:
                  warnWithin: 5%
                  locateBy:
                    '[data.WellboreID, data.FacilityName]': A/1-F-11
                    tags.SourceProject: NORTH
                    data.Name: COMPOSITE
                  check:
                    data.SamplingStart: 200
                    data.LogSource: OPERATOR
                  rows:
                    - locateBy: { data.Curves.CurveID: DT }
                      check: { data.Curves.TopDepth: 1000 }
                    - locateBy: { data.Curves.CurveID: GR }
                      check: { data.Curves.TopDepth: 205.1304 }
          - name: beyond-band
            kind: {{WellLog}}
            assert:
              - count: 9
                warnWithin: 10%
              - knownValues:
                  locateBy:
                    '[data.WellboreID, data.FacilityName]': A/1-F-11
                    tags.SourceProject: NORTH
                    data.Name: COMPOSITE
                  check:
                    data.SamplingStart: { equals: 200, warnWithin: 5% }
                    data.LogSource: CONTRACTOR
          - name: misfit
            kind: {{WellLog}}
            assert:
              - knownValues:
                  locateBy: { '[data.WellboreID, data.FacilityNam]': A/1-F-11 }
                  check: { '[data.WellboreID, data.CurrentOperatorID, data.Name]': A/1-F }
        """;

    private async Task<(AssertionRunner Runner, OsduLedger Ledger)> RunnerAsync()
    {
        var ledger = _db.Ledger(_clock);
        var templates = _db.Templates(_clock);
        await Samples.ImportSampleTemplatesAsync(templates);
        var engine = Samples.Engine(ledger, _clock, templates: templates);
        var flow = new DeliveryDocumentLoader().ParseAssertion(Flow, "flow.yaml").ForPartition("dev");
        return (new AssertionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<AssertionRunner>(), _platform, allowLoopback: true), ledger);
    }

    private async Task<IReadOnlyDictionary<string, TestResult>> RunAsync()
    {
        Estate();
        var (runner, ledger) = await RunnerAsync();
        var outcome = await runner.TestAsync([], [], Guid.NewGuid(), "tests", CancellationToken.None);
        return (await ledger.ListAssertionResultsAsync(outcome.AssertionRunId)).ToDictionary(r => r.TestName, r => TestResults.Deserialize(r.Detail)!);
    }

    [Fact]
    public async Task Curves_are_found_through_their_wellbore_and_each_row_says_whether_it_holds_its_proof_values()
    {
        var results = await RunAsync();
        var curves = Assert.Single(results["curves"].Assertions);
        Assert.Equal(TestOutcomes.Failed, curves.Outcome);
        Assert.Equal(3, curves.Checked);
        Assert.Equal(2, curves.Failing);
        Assert.Contains("3 row(s) with 6 proof value(s): 1 hold their values, 1 differ, 1 not found, 0 found more than once", curves.Actual, StringComparison.Ordinal);
        Assert.Contains("disabled: 1 row(s), 1 proof value(s)", curves.Actual, StringComparison.Ordinal);
        Assert.Contains("differing most in data.Curves.CurveUnit (1)", curves.Actual, StringComparison.Ordinal);

        var gr = Assert.Single(curves.Examples, e => e.Value == "A/1-F-11 / NORTH / COMPOSITE / GR");
        Assert.Equal("dev:work-product-component--WellLog:comp", gr.Id);
        Assert.Contains("of item 2 of data.Curves", gr.Reason, StringComparison.Ordinal);
        Assert.Contains("data.Curves.CurveUnit is \"dev:reference-data--UnitOfMeasure:API:\", expected it equals \"dev:reference-data--UnitOfMeasure:gAPI:\"", gr.Reason, StringComparison.Ordinal);

        var bs = Assert.Single(curves.Examples, e => e.Value == "A/1-F-11 / NORTH / COMPOSITE / BS");
        Assert.Equal("dev:work-product-component--WellLog:comp", bs.Id);
        Assert.Contains("hold no item of data.Curves with data.Curves.CurveID \"BS\"", bs.Reason, StringComparison.Ordinal);

        // The wellbore was found by its exact name among the wellbores, the logs by the reference naming it, whatever its
        // version, and narrowed by the rest of what the rows are located by.
        Assert.Contains(_platform.Calls, c => c.Body?.Contains("\"kind\":\"*:*:master-data--Wellbore:*\"", StringComparison.Ordinal) == true
            && c.Body.Contains("data.FacilityName.keyword:", StringComparison.Ordinal));
        Assert.Contains(_platform.Calls, c => c.Body?.Contains(@"data.WellboreID.keyword:dev\\:master\\-data\\-\\-Wellbore\\:wb1\\:*", StringComparison.Ordinal) == true
            && c.Body.Contains("tags.SourceProject:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_row_found_twice_fails_and_a_row_not_found_says_by_which_path_at_its_own_severity()
    {
        var results = await RunAsync();
        var logs = results["logs"];
        Assert.Equal(TestOutcomes.Failed, logs.Outcome);

        var located = logs.Assertions[0];
        Assert.Equal(TestOutcomes.Failed, located.Outcome);
        Assert.Equal("error", located.Severity);
        Assert.Equal(1, located.Failing);
        var twice = Assert.Single(located.Examples);
        Assert.Equal("A/1-F-12 / NORTH / COMPOSITE", twice.Value);
        Assert.StartsWith("found 2 times: dev:work-product-component--WellLog:twin1 holds its values; dev:work-product-component--WellLog:twin2 holds its values", twice.Reason, StringComparison.Ordinal);

        var missing = logs.Assertions[1];
        Assert.Equal(TestOutcomes.Failed, missing.Outcome);
        Assert.Equal("warning", missing.Severity);
        Assert.Contains(missing.Examples, e => e.Reason == "not found: no master-data--Wellbore record holds data.FacilityName \"B/2-X-7\"");
        Assert.Contains(missing.Examples, e => e.Value == "A/1-F-11 / PRESSURE"
            && e.Reason == "not found: of the 2 records found with [data.WellboreID, data.FacilityName] \"A/1-F-11\", none has data.Name \"PRESSURE\"");

        var off = logs.Assertions[2];
        Assert.Equal(TestOutcomes.Skipped, off.Outcome);
        Assert.Equal("disabled: kept for when the PLT logs arrive", off.Message);

        // What a test's values expect is quoted with the day they were stated.
        Assert.EndsWith("(stated as of 2026-09-01)", located.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wellbore_is_checked_and_found_by_what_the_logs_naming_it_hold()
    {
        var results = await RunAsync();
        var wellbores = Assert.Single(results["wellbores"].Assertions);
        Assert.True(wellbores.Outcome == TestOutcomes.Passed, wellbores.Message + " / " + wellbores.Actual + " / " + string.Join(" | ", wellbores.Examples.Select(e => e.Reason)));
        Assert.Contains("2 row(s) with 4 proof value(s): 2 hold their values", wellbores.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Values_a_little_off_warn_within_their_band_and_fail_beyond_it()
    {
        var results = await RunAsync();
        var near = results["within-band"];
        Assert.Equal(TestOutcomes.Warned, near.Outcome);
        Assert.All(near.Assertions, a => Assert.Equal((TestOutcomes.Failed, "warning"), (a.Outcome, a.Severity)));
        Assert.Equal("equals 7 (warning within 1)", near.Assertions[0].Expected);
        Assert.Contains("1 below 7 (14.29%), within the warning band of 1, so it warns", near.Assertions[0].Message, StringComparison.Ordinal);
        Assert.Contains("0.2 below 4.2 (4.76%), within the warning band of 5%", near.Assertions[1].Message, StringComparison.Ordinal);

        var known = near.Assertions[2];
        Assert.Contains("2 row(s) with 6 proof value(s): 0 hold their values, 2 within the warning band, 0 differ, 0 not found", known.Actual, StringComparison.Ordinal);
        Assert.Equal("2 row(s) differ only within the warning band (warning).", known.Message);
        var dt = Assert.Single(known.Examples, e => e.Value == "A/1-F-11 / NORTH / COMPOSITE / DT");
        Assert.StartsWith("differs within the warning band in 2 proof value(s) of item 1 of data.Curves: ", dt.Reason, StringComparison.Ordinal);
        Assert.Contains("data.SamplingStart is 205.1304, expected it equals 200; 5.1304 above 200 (2.57%), within the warning band of 5%, so it warns", dt.Reason, StringComparison.Ordinal);
        Assert.Contains("data.Curves.TopDepth is 1023.0612, expected it equals 1000; 23.0612 above 1000 (2.31%), within the warning band of 5%", dt.Reason, StringComparison.Ordinal);

        // Beyond the band, or beside a proof value that differs outright, a row and a count fail as ever.
        var far = results["beyond-band"];
        Assert.Equal(TestOutcomes.Failed, far.Outcome);
        Assert.Equal((TestOutcomes.Failed, "error"), (far.Assertions[0].Outcome, far.Assertions[0].Severity));
        var mixed = far.Assertions[1];
        Assert.Equal("error", mixed.Severity);
        Assert.Contains("1 differ", mixed.Actual, StringComparison.Ordinal);
        var row = Assert.Single(mixed.Examples);
        Assert.StartsWith("differs in 2 proof value(s): ", row.Reason, StringComparison.Ordinal);
        Assert.Contains("within the warning band of 5%, so it warns; data.LogSource is \"OPERATOR\", expected it equals \"CONTRACTOR\"", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_test_is_reported_as_skipped_and_a_path_that_does_not_fit_its_templates_as_such()
    {
        var results = await RunAsync();
        var off = results["switched-off"];
        Assert.Equal(TestOutcomes.Skipped, off.Outcome);
        Assert.Equal("The test is disabled: OSDU holds no logs of this field yet", off.Error);

        // A test whose proof values are out of date is not judged, and is flagged: a warning for every assertion it judges.
        var stale = results["stale"];
        Assert.Equal(TestOutcomes.Warned, stale.Outcome);
        Assert.Null(stale.Error);
        var restate = Assert.Single(stale.Assertions);
        Assert.Equal((TestOutcomes.Failed, "warning", "not judged"), (restate.Outcome, restate.Severity, restate.Actual));
        Assert.Equal("needs new proof values: stated as of 2025-10-13 and valid until 2025-12-31, and the run is past that; restate them and move asOf", restate.Message);
        Assert.EndsWith("(stated as of 2025-10-13)", restate.Expected, StringComparison.Ordinal);

        // One assertion out of date flags the test, and the others are judged as ever.
        var partly = results["partly-stale"];
        Assert.Equal(TestOutcomes.Warned, partly.Outcome);
        Assert.Equal(TestOutcomes.Passed, partly.Assertions[0].Outcome);
        Assert.Equal(("warning", "not judged"), (partly.Assertions[1].Severity, partly.Assertions[1].Actual));
        Assert.StartsWith("needs new proof values: stated as of 2026-01-01 and valid until 2026-01-31 (30d after asOf)", partly.Assertions[1].Message, StringComparison.Ordinal);

        // asOfField counts what OSDU had created by the day the values were stated: wb3 came after it.
        var asOf = results["as-of"];
        Assert.Equal(TestOutcomes.Passed, asOf.Outcome);
        Assert.Contains("createTime:[* TO \"2026-09-02T00:00:00.000Z\"}", asOf.Query, StringComparison.Ordinal);

        var misfit = results["misfit"];
        Assert.Equal(TestOutcomes.Errored, misfit.Outcome);
        Assert.Contains(misfit.Problems, p => p.Contains("'data.FacilityNam' is not a property of osdu:wks:master-data--Wellbore:1.3.0", StringComparison.Ordinal)
            && p.Contains("did you mean 'data.FacilityName'", StringComparison.Ordinal));
        Assert.Contains(misfit.Problems, p => p.Contains("no template folder on the flow's path holds a template of master-data--Organisation", StringComparison.Ordinal));
    }
}
