using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Known values as a document writes them: the full paths and values a row is located by, its proof values (a value, or a
/// condition), paths through the records a reference names and the records naming one, aggregates of a path's values, what
/// is disabled, and everything the loader refuses, each with a message naming it.
/// </summary>
public sealed class KnownValuesDocumentTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";

    private static readonly DeliveryDocumentLoader Loader = new();

    private static AssertionFlowDefinition Parse(string yaml) => Loader.ParseAssertion(yaml, "flow.yaml");

    private static string Flow(string assertion, string testKeys = "") => $"""
        flowType: assertion
        name: qc
        partitions: [dev]
        source: {"{"} endpoint: http://localhost {"}"}
        tests:
          - name: curves
            kind: "{WellLog}"
        {Indent(testKeys, 4)}
            assert:
        {Indent(assertion, 6)}
        """;

    private static string Indent(string text, int spaces)
        => string.Join("\n", text.Split('\n').Where(l => l.Trim().Length > 0).Select(l => new string(' ', spaces) + l.TrimEnd('\r')));

    private static string Refusal(string yaml) => Assert.Throws<FlowValidationException>(() => Parse(yaml)).Message;

    private static KnownValuesAssertion Known(string assertion) => (KnownValuesAssertion)Parse(Flow(assertion)).Tests[0].Assertions[0];

    [Fact]
    public void A_row_is_located_by_full_paths_and_holds_its_proof_values_with_what_every_row_shares()
    {
        var known = Known("""
            - name: the curves of A/1-F-11
              knownValues:
                tolerance: 0.001
                locateBy:
                  '[data.WellboreID, data.FacilityName]': A/1-F-11
                  tags.SourceProject: NORTH
                  data.Name: COMPOSITE
                check:
                  data.SamplingInterval: 0.1524
                rows:
                  - locateBy: { data.Curves.CurveID: DT }
                    check:
                      data.Curves.CurveUnit: "{partition}:reference-data--UnitOfMeasure:us%2Fft:"
                      data.Curves.TopDepth: { equals: 1250.4, tolerance: 0.0005 }
                      data.Curves.BaseDepth: { between: [2079, 2080] }
                      data.LogSource: { equals: OPERATOR, disabled: "the source's log source is not delivered yet" }
                  - locateBy: { data.Curves.CurveID: GR }
                    disabled: GR is checked by another flow
            """);

        Assert.Equal("the curves of A/1-F-11", known.Label);
        Assert.Equal(2, known.Rows.Count);
        var dt = known.Rows[0];
        Assert.Null(dt.Disabled);
        Assert.Equal(["[data.WellboreID, data.FacilityName]", "tags.SourceProject", "data.Name", "data.Curves.CurveID"], dt.Locate.Select(l => l.Column.Path));
        Assert.Equal("A/1-F-11 / NORTH / COMPOSITE / DT", dt.Label);

        var wellbore = dt.Locate[0].Column;
        Assert.Equal([new KnownStep(KnownStepVia.Record, "data.WellboreID"), new KnownStep(KnownStepVia.Parent, "data.FacilityName")], wellbore.Steps);

        Assert.Equal(["data.SamplingInterval", "data.Curves.CurveUnit", "data.Curves.TopDepth", "data.Curves.BaseDepth", "data.LogSource"], dt.Checks.Select(c => c.Column.Path));
        Assert.Equal(0.001, dt.Checks[0].Condition.Tolerance);
        Assert.Equal(ValueOperator.EqualTo, dt.Checks[1].Condition.Operator);
        Assert.Equal("{partition}:reference-data--UnitOfMeasure:us%2Fft:", dt.Checks[1].Condition.Operands[0].Text);
        Assert.Equal(0.0005, dt.Checks[2].Condition.Tolerance);
        Assert.Equal(ValueOperator.Between, dt.Checks[3].Condition.Operator);
        Assert.Equal("the source's log source is not delivered yet", dt.Checks[4].Disabled);

        Assert.Equal("GR is checked by another flow", known.Rows[1].Disabled);
        Assert.Contains("each of 1 row(s) finds one record and holds its 4 proof value(s)", known.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_reach_the_records_a_reference_names_and_the_records_naming_one()
    {
        var known = (KnownValuesAssertion)Parse(Flow("""
            - knownValues:
                locateBy:
                  data.FacilityName: A/1-F-11
                check:
                  '[data.WellID, data.FacilityName]': A/1-F
                  '[work-product-component--WellLog[data.WellboreID], data.Name]': { equals: COMPOSITE, values: any }
                  "[work-product-component--WellLog[data.WellboreID], data.Curves.CurveID]": { aggregate: count, except: [MD], atLeast: 20 }
                  '[data.WellID, master-data--Wellbore[data.WellID], data.FacilityName]': { contains: NO, values: any }
                  "[ data.WellID , 'data.SpudDate' ]": { atLeast: 2001-01-01 }
                  '[work-product-component--WellLog[data.WellboreID], data.WellboreID, data.FacilityName]': A/1-F-11
                  '[master-data--Wellbore[data.WellID], work-product-component--WellLog[data.WellboreID], data.Name]': { equals: COMPOSITE, values: any }
            """).Replace(WellLog, "osdu:wks:master-data--Wellbore:1.3.0", StringComparison.Ordinal)).Tests[0].Assertions[0];

        var checks = known.Rows.Single().Checks;
        Assert.Equal([new KnownStep(KnownStepVia.Record, "data.WellID"), new KnownStep(KnownStepVia.Parent, "data.FacilityName")], checks[0].Column.Steps);
        Assert.Equal(
            [new KnownStep(KnownStepVia.Children, "data.Name", "work-product-component--WellLog", "data.WellboreID")],
            checks[1].Column.Steps);
        Assert.True(checks[1].Column.AnyValue);
        Assert.Equal(AggregateFunction.Count, checks[2].Column.Aggregate);
        Assert.Equal("MD", Assert.Single(checks[2].Column.Except).Text);
        Assert.Equal("count([work-product-component--WellLog[data.WellboreID], data.Curves.CurveID])", checks[2].Column.Label);
        Assert.Equal(ValueOperator.AtLeast, checks[2].Condition.Operator);
        Assert.Equal(
            [
                new KnownStep(KnownStepVia.Record, "data.WellID"),
                new KnownStep(KnownStepVia.Parent, null),
                new KnownStep(KnownStepVia.Children, "data.FacilityName", "master-data--Wellbore", "data.WellID"),
            ],
            checks[3].Column.Steps);
        Assert.Equal("[data.WellID, master-data--Wellbore[data.WellID], data.FacilityName]", checks[3].Column.Path);

        // An entry may be quoted, as a dimension's list writes a path with a filter; the path is written back plainly.
        Assert.Equal("[data.WellID, data.SpudDate]", checks[4].Column.Path);
        Assert.Equal([new KnownStep(KnownStepVia.Record, "data.WellID"), new KnownStep(KnownStepVia.Parent, "data.SpudDate")], checks[4].Column.Steps);

        // From the records naming one, a reference leads on: the wellbore of each of its logs.
        Assert.Equal(
            [
                new KnownStep(KnownStepVia.Children, "data.WellboreID", "work-product-component--WellLog", "data.WellboreID"),
                new KnownStep(KnownStepVia.Parent, "data.FacilityName"),
            ],
            checks[5].Column.Steps);

        // And so do the records naming those: the logs of every wellbore naming the same well.
        Assert.Equal(
            [
                new KnownStep(KnownStepVia.Children, null, "master-data--Wellbore", "data.WellID"),
                new KnownStep(KnownStepVia.Children, "data.Name", "work-product-component--WellLog", "data.WellboreID"),
            ],
            checks[6].Column.Steps);
    }

    [Fact]
    public void Known_values_without_rows_are_the_one_row_the_assertion_lists()
    {
        var known = Known("""
            - knownValues:
                locateBy: { '[data.WellboreID, data.FacilityName]': A/1-F-11, data.Name: COMPOSITE }
                check: { data.SamplingStart: 204.6, data.IsRegular: true, data.CompanyID: ~ }
            """);
        var row = Assert.Single(known.Rows);
        Assert.Equal(2, row.Locate.Count);
        Assert.Equal(ValueOperator.Exists, row.Checks[2].Condition.Operator);
        Assert.False(row.Checks[2].Condition.Flag);
        Assert.Equal(ExpectedValueKind.Boolean, row.Checks[1].Condition.Operands[0].Kind);
    }

    [Fact]
    public void A_test_or_an_assertion_is_disabled_with_why()
    {
        var flow = Parse(Flow("""
            - count: { atLeast: 1 }
              disabled: the partition is being reloaded
            - knownValues: { locateBy: { data.Name: COMPOSITE } }
            """, "disabled: \"OSDU holds no composite logs of this wellbore yet\""));
        var test = flow.Tests[0];
        Assert.Equal("OSDU holds no composite logs of this wellbore yet", test.Disabled);
        Assert.Equal("the partition is being reloaded", test.Assertions[0].Disabled);
        Assert.Null(test.Assertions[1].Disabled);
    }

    [Fact]
    public void An_aggregate_leaves_out_the_values_it_lists()
    {
        var flow = Parse(Flow("""
            - aggregate: count
              field: data.Curves.CurveID
              except: [MD, MD]
              equals: 7842
            """));
        var aggregate = (AggregateAssertion)flow.Tests[0].Assertions[0];
        Assert.Equal("MD", Assert.Single(aggregate.Except).Text);
        Assert.Equal("count(data.Curves.CurveID) equals 7842, leaving out \"MD\"", aggregate.Label);
    }

    [Theory]
    [InlineData("- knownValues: { check: { data.Name: X } }", "is located by nothing")]
    [InlineData("- knownValues: { locateBy: { data.Name: ~ } }", "gives no value")]
    [InlineData("- knownValues: { locateBy: { data.Name: { equals: X, startsWith: Y } } }", "has 'startsWith'; a row is located by values its record equals")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, rows: [{ locateBy: { data.Name: Y } }] }", "is located by data.Name, which the assertion lists for every row already")]
    [InlineData("- knownValues: { rows: [{ locateBy: { data.Name: X } }, { locateBy: { data.Name: X } }] }", "is located as rows[0] is (X)")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, check: { data.Name: X } }", "is located by data.Name and checks it as well")]
    [InlineData("- knownValues: { locateBy: { 'data.Curves[0].CurveID': DT } }", "names an item of a list by its position")]
    [InlineData("- knownValues: { locateBy: { '[data.WellboreID, ]': X } }", "has an empty entry")]
    [InlineData("- knownValues: { locateBy: { '[data.WellboreID, data.FacilityName': X } }", "opens a list of paths it does not close")]
    [InlineData("- knownValues: { locateBy: { \"[data.WellboreID, 'data.FacilityName]\": X } }", "has a bracket or a quote that does not pair up")]
    [InlineData("- knownValues: { locateBy: { '[data.WellboreID], data.FacilityName]': X } }", "has a bracket or a quote that does not pair up")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, rows: [{ locateBy: { '[data.Name]': Y } }] }", "is located by data.Name, which the assertion lists for every row already")]
    [InlineData("- knownValues: { locateBy: { '[a.b, data.X]': Y } }", "is not a property of an OSDU record")]
    [InlineData("- knownValues: { locateBy: { 'data.WellboreID->data.FacilityName': Y } }", "is not a path into a record")]
    [InlineData("- knownValues: { locateBy: { '[data.A, data.B, data.C, data.D, data.E]': X } }", "lists 5 paths; a list reads through at most 4")]
    [InlineData("- knownValues: { locateBy: { '[data.WellID, master-data--Wellbore[data.WellID]]': X } }", "reads nothing of them; end the list with the path read in them")]
    [InlineData("- knownValues: { locateBy: { 'master-data--Wellbore[data.WellID].data.Name': X } }", "steps to the records naming one outside a list of paths")]
    [InlineData("- knownValues: { locateBy: { '[master-data--Wellbore[data.WellID].data.Name]': X } }", "is not a path into a record")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, check: { data.A: { near: 1 } } }", "has 'near'; a proof value is a value, or a condition")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, check: { data.A: { equals: 1, atLeast: 0 } } }", "names equals and atLeast; a condition has one operator")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, check: { data.A: { resolves: true } } }", "has 'resolves'")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, check: { data.A: { aggregate: count, startsWith: a } } }", "so it takes one of equals")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, check: { data.A: { aggregate: count, equals: many } } }", "which is a number; give a number")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, check: { data.A: { aggregate: missing, equals: 0 } } }", "hold its count to 0 instead")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, check: { data.A: { aggregate: count, values: any, equals: 1 } } }", "an aggregate is taken of them all")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, check: { data.A: { except: [MD], equals: 1 } } }", "the proof value aggregates nothing")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, check: { data.A: { equals: 1, disabled: '' } } }", "says why it is disabled")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, rows: [{ locateBy: { data.A: 1 }, sorted: true }] }", "invalid YAML")]
    [InlineData("- knownValues: { locateBy: { data.Name: X }, notFound: fatal }", "'fatal' is not one of")]
    [InlineData("- aggregate: count\n  field: data.A\n  except: []\n  equals: 1", "lists between 1 and 1000 values the aggregate leaves out")]
    [InlineData("- aggregate: count\n  field: data.A\n  except: [~]\n  equals: 1", "an aggregate already leaves out what has no value")]
    public void Known_values_that_do_not_say_one_thing_clearly_are_refused(string assertion, string expected)
        => Assert.Contains(expected, Refusal(Flow(assertion)), StringComparison.Ordinal);

    [Fact]
    public void Known_values_find_records_by_their_values_not_by_ids()
        => Assert.Contains(
            "finds its records by the values its rows give, and the test names its records by id",
            Refusal(Flow("- knownValues: { locateBy: { data.Name: X } }", "ids: [dev:work-product-component--WellLog:1]")),
            StringComparison.Ordinal);

    [Fact]
    public void Values_are_stated_as_of_a_day_and_hold_until_one()
    {
        var test = Parse(Flow("""
            - aggregate: distinct
              field: data.WellboreID
              equals: 62
              asOf: 2026-10-13T08:00:00Z
              validUntil: 2026-12-31T23:59:59Z
            - count: { atLeast: 1 }
            """, "asOf: 2026-10-13\nvalidUntil: 2026-11-13")).Tests[0];
        Assert.Equal(("2026-10-13", "2026-11-13"), (test.AsOf, test.ValidUntil));
        Assert.Equal(("2026-10-13T08:00:00Z", "2026-12-31T23:59:59Z"), (test.Assertions[0].AsOf, test.Assertions[0].ValidUntil));
        Assert.Null(test.Assertions[1].ValidUntil);

        // A date holds to the end of its day; past it, what was expected needs restating, and says why. An assertion's own
        // dates take over from its test's, so the test needs new proof values whole only once every assertion does.
        var lastDay = new DateTimeOffset(2026, 11, 13, 23, 0, 0, TimeSpan.Zero);
        var dayAfter = new DateTimeOffset(2026, 11, 14, 0, 0, 1, TimeSpan.Zero);
        Assert.Null(AssertionActivity.Stale(test.Assertions[1], test, lastDay));
        Assert.Equal(
            "needs new proof values: stated as of 2026-10-13 and valid until 2026-11-13, and the run is past that; restate them and move asOf",
            AssertionActivity.Stale(test.Assertions[1], test, dayAfter));
        Assert.Null(AssertionActivity.Stale(test.Assertions[0], test, dayAfter));
        Assert.False(AssertionActivity.Stale(test, dayAfter));
        Assert.Equal(
            "needs new proof values: stated as of 2026-10-13T08:00:00Z and valid until 2026-12-31T23:59:59Z, and the run is past that; restate them and move asOf",
            AssertionActivity.Stale(test.Assertions[0], test, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(AssertionActivity.Stale(test, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Values_hold_for_a_period_after_the_day_they_were_stated()
    {
        var test = Parse(Flow("- count: { atLeast: 1 }", "asOf: 2026-10-13\nvalidFor: 90d")).Tests[0];
        Assert.Equal("90d", test.ValidFor);
        Assert.Equal(new DateTimeOffset(2027, 1, 11, 0, 0, 0, TimeSpan.Zero).AddDays(1).AddTicks(-1), AssertionActivity.Last(test.AsOf, test.ValidUntil, test.ValidFor));
        Assert.Equal("stated as of 2026-10-13 and valid until 2027-01-11 (90d after asOf)", AssertionActivity.Window(test.AsOf, test.ValidUntil, test.ValidFor));
        Assert.Equal(
            new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero).AddMonths(6),
            AssertionActivity.Last("2026-10-13T08:00:00Z", null, "6 months"));
    }

    [Theory]
    [InlineData("90d", 90, 'd')]
    [InlineData("12 weeks", 12, 'w')]
    [InlineData("6m", 6, 'm')]
    [InlineData("1 Year", 1, 'y')]
    [InlineData("P90D", 90, 'd')]
    [InlineData("P2W", 2, 'w')]
    public void A_period_is_a_whole_number_of_days_weeks_months_or_years(string text, int count, char unit)
    {
        Assert.True(AssertionActivity.TryPeriod(text, out var n, out var u));
        Assert.Equal((count, unit), (n, u));
    }

    [Theory]
    [InlineData("0d")]
    [InlineData("90")]
    [InlineData("3h")]
    [InlineData("1.5y")]
    [InlineData("dweeks")]
    public void A_period_that_is_not_one_is_refused(string text) => Assert.False(AssertionActivity.TryPeriod(text, out _, out _));

    [Fact]
    public void A_test_compares_the_records_its_asOfField_dates_on_or_before_its_asOf()
    {
        var test = Parse(Flow("- count: 62", "query: 'data.Name:\"COMPOSITE\"'\nasOf: 2026-10-13\nasOfField: createTime")).Tests[0];
        Assert.Equal("createTime", test.AsOfField);
        Assert.Equal("(data.Name:\"COMPOSITE\") AND (createTime:[* TO \"2026-10-14T00:00:00.000Z\"})", Engine.Assertions.AssertionQueries.Of(test));
        var instant = Parse(Flow("- count: 62", "asOf: 2026-10-13T08:00:00Z\nasOfField: modifyTime")).Tests[0];
        Assert.Equal("modifyTime:[* TO \"2026-10-13T08:00:00.001Z\"}", Engine.Assertions.AssertionQueries.Of(instant));
    }

    [Theory]
    [InlineData("asOf: 13/10/2026", "is not an ISO 8601 date")]
    [InlineData("asOf: 2026-10-13\nvalidUntil: 2026-10-12", "comes before asOf 2026-10-13")]
    [InlineData("validFor: 90d", "counts from the day the values were stated, and no asOf says which")]
    [InlineData("asOf: 2026-10-13\nvalidFor: 90d\nvalidUntil: 2026-12-31", "gives validUntil and validFor")]
    [InlineData("asOf: 2026-10-13\nvalidFor: 3 hours", "is not a period")]
    [InlineData("asOfField: createTime", "no asOf says which")]
    [InlineData("asOf: 2026-10-13\nasOfField: tags.Loaded", "is not a date every record carries")]
    [InlineData("asOf: 2026-10-13\nasOfField: createTime\nids: [dev:work-product-component--WellLog:1]", "the test names its records by id")]
    public void A_day_that_is_no_date_or_an_end_before_the_start_is_refused(string testKeys, string expected)
        => Assert.Contains(expected, Refusal(Flow("- count: 1", testKeys)), StringComparison.Ordinal);

    [Fact]
    public void A_disabled_test_says_why()
        => Assert.Contains("says why it is disabled", Refusal(Flow("- count: 1", "disabled: \"  \"")), StringComparison.Ordinal);
}
