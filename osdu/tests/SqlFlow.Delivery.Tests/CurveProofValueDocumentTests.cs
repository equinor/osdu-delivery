using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Curve proof values as known values write them (osdu/docs/reference/flow/known-values.md, Curve proof values): each curve
/// mapped to its measures, the proof value of each with what the assertion shares, the curve the rows are indexed by, the
/// definition hash a flow without them keeps, and everything the loader refuses, each with a message naming it.
/// </summary>
public sealed class CurveProofValueDocumentTests
{
    private static readonly DeliveryDocumentLoader Loader = new();

    private const string WithoutCurves = """
        flowType: assertion
        name: qc
        partitions: [dev]
        source: { endpoint: http://localhost }
        tests:
          - name: curves
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            bulk: { maxRows: 50000 }
            assert:
              - name: the curves of A/1-F-11
                knownValues:
                  tolerance: 0.001
                  warnWithin: 1%
                  notFound: warning
                  locateBy:
                    '[data.WellboreID, data.FacilityName]': A/1-F-11
                    data.Name: COMPOSITE
                  check:
                    data.SamplingInterval: 0.1524
                  rows:
                    - locateBy: { data.Curves.CurveID: DT }
                      check:
                        data.Curves.TopDepth: { equals: 1250.4, tolerance: 0.0005 }
                    - locateBy: { data.Curves.CurveID: GR }
                      disabled: GR is not delivered yet
        """;

    private static AssertionFlowDefinition Parse(string yaml) => Loader.ParseAssertion(yaml, "flow.yaml");

    private static string Flow(string knownValues, string testKeys = "bulk: { maxRows: 50000 }") => $"""
        flowType: assertion
        name: qc
        partitions: [dev]
        source: {"{"} endpoint: http://localhost {"}"}
        tests:
          - name: curves
            kind: osdu:wks:work-product-component--WellLog:1.4.0
        {Indent(testKeys, 4)}
            assert:
              - name: the curve data of the COMPOSITE log
                knownValues:
        {Indent(knownValues, 10)}
        """;

    private static string Indent(string text, int spaces)
        => string.Join("\n", text.Split('\n').Where(l => l.Trim().Length > 0).Select(l => new string(' ', spaces) + l.TrimEnd('\r')));

    private static KnownValuesAssertion Known(string knownValues) => (KnownValuesAssertion)Parse(Flow(knownValues)).Tests[0].Assertions[0];

    private static string Refusal(string knownValues, string testKeys = "bulk: { maxRows: 50000 }")
        => Assert.Throws<FlowValidationException>(() => Parse(Flow(knownValues, testKeys))).Message;

    [Fact]
    public void Known_values_without_curve_proof_values_keep_the_definition_hash_they_had()
    {
        // The hash main gave this test before curve proof values existed: a flow without them keeps every result it has.
        Assert.Equal("710005d81787199c", Parse(WithoutCurves).Tests[0].DefinitionHash);
    }

    [Fact]
    public void Each_curve_maps_its_measures_to_proof_values_with_what_the_assertion_shares()
    {
        var known = Known("""
            tolerance: 0.001
            warnWithin: 2%
            locateBy: { data.Name: COMPOSITE }
            curves:
              MD: { count: 5120 }
            rows:
              - curves:
                  GR: { max: 150.2, avg: { between: [70, 80], tolerance: 0.5 }, missing: { atMost: 10 }, first: 1000 }
                  DT: { step: { equals: 0.1524, tolerance: 0.0001, warnWithin: 0.01 }, sum: { equals: 5, disabled: restated next release } }
            """);
        var row = Assert.Single(known.Rows);
        Assert.Equal(
            ["MD count", "GR max", "GR avg", "GR missing", "GR first", "DT step", "DT sum"],
            row.CurveChecks.Select(c => c.Label));

        var count = row.CurveChecks[0];
        Assert.Equal((ValueOperator.EqualTo, "5120"), (count.Condition.Operator, count.Condition.Operands[0].Text));
        Assert.Equal(0.001, count.Condition.Tolerance);
        Assert.Equal(new WarningBand(2, Percent: true), count.WarnWithin);

        var max = row.CurveChecks[1];
        Assert.Equal(0.001, max.Condition.Tolerance);
        Assert.Equal("equals 150.2 (tolerance 0.001)", max.Condition.ToString());

        var avg = row.CurveChecks[2];
        Assert.Equal((ValueOperator.Between, 0.5), (avg.Condition.Operator, avg.Condition.Tolerance));
        Assert.Equal(ValueOperator.AtMost, row.CurveChecks[3].Condition.Operator);

        var step = row.CurveChecks[5];
        Assert.Equal(new WarningBand(0.01, Percent: false), step.WarnWithin);
        Assert.Equal("restated next release", row.CurveChecks[6].Disabled);
        Assert.Null(known.CurveIndex);
        Assert.Equal("each of 1 row(s) finds one record and holds its 0 proof value(s) and 6 curve proof value(s)", known.Expected);
    }

    [Fact]
    public void A_curve_index_is_kept_and_a_flow_with_curve_proof_values_hashes_apart_from_one_without()
    {
        var indexed = Known("""
            curveIndex: TIME
            locateBy: { data.Name: TIMELOG }
            curves:
              GR: { first: "2026-01-01T00:01:00Z", min: { atLeast: "2026-01-01T00:00:00Z" } }
            """);
        Assert.Equal("TIME", indexed.CurveIndex);
        Assert.Equal(ExpectedValueKind.Text, indexed.Rows[0].CurveChecks[0].Condition.Operands[0].Kind);

        var plain = Parse(Flow("locateBy: { data.Name: COMPOSITE }")).Tests[0].DefinitionHash;
        var curved = Parse(Flow("locateBy: { data.Name: COMPOSITE }\ncurves: { GR: { count: 5 } }")).Tests[0].DefinitionHash;
        var other = Parse(Flow("locateBy: { data.Name: COMPOSITE }\ncurves: { GR: { count: 6 } }")).Tests[0].DefinitionHash;
        Assert.NotEqual(plain, curved);
        Assert.NotEqual(curved, other);
    }

    [Fact]
    public void A_curve_value_at_a_key_is_checked_in_the_row_whose_index_holds_it()
    {
        var known = Known("""
            tolerance: 0.01
            locateBy: { data.Name: COMPOSITE }
            curves:
              GR: { at: { 1500.1524: 74.2, 1600: { between: [80, 81] }, 1700.0: { equals: 90, disabled: re-logged next run } } }
              TIME: { at: { "2026-01-01T00:01:00Z": 1.5 } }
              LITH: { at: { 1500.1524: SAND } }
            """);
        var checks = known.Rows[0].CurveChecks;
        Assert.Equal(["GR at 1500.1524", "GR at 1600", "GR at 1700", "TIME at 2026-01-01T00:01:00Z", "LITH at 1500.1524"], checks.Select(c => c.Label));
        Assert.All(checks, c => Assert.Equal(CurveMeasure.At, c.Measure));
        Assert.Equal(("1500.1524", 0.01), (checks[0].Key!.Text, checks[0].Condition.Tolerance));
        Assert.Equal(ValueOperator.Between, checks[1].Condition.Operator);
        Assert.Equal("re-logged next run", checks[2].Disabled);
        Assert.Equal(("SAND", ExpectedValueKind.Text), (checks[4].Condition.Operands[0].Text, checks[4].Condition.Operands[0].Kind));
        Assert.Equal(
            CurveKey.Of(ExpectedValue.OfNumber(1500)),
            CurveKey.Of(System.Text.Json.Nodes.JsonValue.Create(1500.0)));
        Assert.Equal(
            CurveKey.Of(ExpectedValue.OfText("2026-01-01T01:01:00+01:00")),
            CurveKey.Of(System.Text.Json.Nodes.JsonValue.Create("2026-01-01T00:01:00Z")));
    }

    [Fact]
    public void A_listed_bulk_read_includes_every_curve_the_proof_values_name()
    {
        var test = Parse(Flow("locateBy: { data.Name: COMPOSITE }\ncurveIndex: MD\ncurves: { GR: { first: 1000 } }")).Tests[0];
        Assert.Equal(["GR", "MD"], test.Bulk!.Columns);
        Assert.Contains(
            "bulk.columns lists the only columns the test reads, and leaves out 'GR' (assert[0])",
            Refusal("locateBy: { data.Name: COMPOSITE }\ncurves: { GR: { count: 5 } }", "bulk: { columns: [MD] }"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "curves: { GR: { count: 5 } }",
        "knownValues holds curve proof values, which are measured over each record's bulk data; give the test a bulk block")]
    [InlineData(
        "curves: { GR: 5 }",
        "knownValues.curves.GR maps the curve's measures to their proof values, such as GR: { count: 5120, max: 150.2 }; a measure is one of count, missing, min, max, sum, avg, first, last, step, at.")]
    [InlineData(
        "curves: { GR: { median: 5 } }",
        "knownValues.curves.GR has 'median', which is not a measure of a curve; a measure is one of count, missing, min, max, sum, avg, first, last, step, at.")]
    [InlineData(
        "curves: { GR: { max: ~ } }",
        "knownValues.curves.GR.max has no value; a curve proof value is the number the measure equals, or a comparison")]
    [InlineData(
        "curves: { GR: { max: [1, 2] } }",
        "knownValues.curves.GR.max is a list; a curve proof value is the number the measure equals")]
    [InlineData(
        "curves: { GR: { max: { matches: '^1' } } }",
        "knownValues.curves.GR.max has 'matches'; a curve proof value is the number the measure equals, or a comparison: one of equals, notEquals, atLeast, atMost, greaterThan, lessThan or between")]
    [InlineData(
        "curves: { GR: { max: { tolerance: 0.1 } } }",
        "knownValues.curves.GR.max names no comparison")]
    [InlineData(
        "curves: { GR: { avg: high } }",
        "knownValues.curves.GR.avg compares GR's avg, which is a number; give a number, not \"high\".")]
    [InlineData(
        "curves: { GR: { avg: \"2026-01-01\" } }",
        "knownValues.curves.GR.avg compares GR's avg, which is a number; give a number, not \"2026-01-01\".")]
    [InlineData(
        "curves: { GR: { max: today } }",
        "knownValues.curves.GR.max compares GR's max, which is a number (or a date, for a curve of dates); give a number or an ISO 8601 date, not \"today\".")]
    [InlineData(
        "curves: { GR: { count: 5.5 } }",
        "knownValues.curves.GR.count compares how many rows GR has a value in, a whole number of zero or more, not 5.5.")]
    [InlineData(
        "curves: { GR: { missing: { atLeast: -1 } } }",
        "knownValues.curves.GR.missing compares how many rows GR has no value in, a whole number of zero or more, not -1.")]
    [InlineData(
        "curves: { MD: { step: { notEquals: 0 } } }",
        "knownValues.curves.MD.step holds every step of MD to it, and notEquals is not a range every step can lie in")]
    [InlineData(
        "curves: { GR: { max: { equals: 5, warnWithin: 0 } } }",
        "knownValues.curves.GR.max.warnWithin is not a warning band")]
    [InlineData(
        "curves: { GR: { max: { equals: 5, disabled: '' } } }",
        "knownValues.curves.GR.max.disabled says why it is disabled")]
    [InlineData(
        "curves: { 'G,R': { max: 5 } }",
        "knownValues.curves.G,R 'G,R' is not a column name: 1 to 256 characters, without commas or control characters.")]
    [InlineData(
        "curveIndex: MD\ncurves: { GR: { max: 5 } }",
        "knownValues.curveIndex names the curve a curve's first and last values are read at, and no row holds a first or last proof value")]
    [InlineData(
        "curves: { GR: { max: 5 } }\nrows:\n  - curves: { GR: { max: 6 } }",
        "knownValues.rows[0] checks GR max, which the assertion lists for every row already; give it in one place.")]
    [InlineData(
        "curves: { GR: { at: 1500 } }",
        "knownValues.curves.GR.at maps keys of the curve the rows are indexed by to GR's value there, such as at: { 1500.1524: 74.2 }; a key is a depth of the curve the rows are indexed by, such as 1500.1524, or a time")]
    [InlineData(
        "curves: { GR: { at: { top: 5 } } }",
        "knownValues.curves.GR.at has the key \"top\"; a key is a depth of the curve the rows are indexed by")]
    [InlineData(
        "curves: { GR: { at: { 1500: 5, 1500.0: 6 } } }",
        "knownValues.curves.GR.at names the key 1500 twice; give each row once.")]
    [InlineData(
        "curves: { GR: { at: { 1500: 5, '1500.00': 6 } } }",
        "knownValues.curves.GR.at names one key twice, as 1500 and 1500.00; give each row once.")]
    [InlineData(
        "curves: { GR: { at: { 1500: ~ } } }",
        "knownValues.curves.GR.at.1500 has no value; a curve proof value is the number the measure equals, or a comparison")]
    public void Curve_proof_values_a_flow_cannot_hold_are_refused_when_it_loads(string curves, string message)
    {
        var testKeys = message.Contains("give the test a bulk block", StringComparison.Ordinal) ? "tags: [qc]" : "bulk: { maxRows: 50000 }";
        Assert.Contains(message, Refusal("locateBy: { data.Name: COMPOSITE }\n" + curves, testKeys), StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_holds_proof_values_of_at_most_a_hundred_curves()
    {
        var curves = string.Join(", ", Enumerable.Range(1, CurveProofValues.MaxCurves + 1).Select(i => $"C{i}: {{ count: 1 }}"));
        Assert.Contains(
            "knownValues holds proof values of 101 curves; a row holds them for at most 100.",
            Refusal("locateBy: { data.Name: COMPOSITE }\ncurves: { " + curves + " }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_curve_checks_its_value_at_a_thousand_keys_at_most()
    {
        var keys = string.Join(", ", Enumerable.Range(1, CurveProofValues.MaxKeys + 1).Select(i => $"{i}: 1"));
        Assert.Contains(
            "knownValues.curves.GR.at names 1001 keys; a curve of a row checks its value at 1000 at most.",
            Refusal("locateBy: { data.Name: COMPOSITE }\ncurves: { GR: { at: { " + keys + " } } }"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1000.1524, 1000.3048, 0.1524)]
    [InlineData(0.1, 0.3, 0.2)]
    [InlineData(-5.5, -5.25, 0.25)]
    [InlineData(1e30, 2e30, 1e30)]
    public void A_step_is_the_difference_between_two_numbers_as_they_are_written(double before, double after, double step)
        => Assert.Equal(step, CurveTally.Difference(before, after));
}
