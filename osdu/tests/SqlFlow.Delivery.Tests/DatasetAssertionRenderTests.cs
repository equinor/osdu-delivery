using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a render judges a mapping's rules on the rows it renders from (osdu/docs/reference/flow/mapping-dataset-assertions.md):
/// a column of the record's row or of each child row, the child rows counted or compared, rows selected by where, and what
/// each failure is named, carries and does.
/// </summary>
public sealed class DatasetAssertionRenderTests
{
    [Fact]
    public void A_rule_on_a_column_of_the_record_row_holds_the_record_it_fails_and_names_the_column()
    {
        const string Rules = """
            - name: acl-read-whole
              column: AclUnreadable
              empty: true
            """;

        var met = Render(Rules, Row(("AclUnreadable", null)));
        Assert.Equal((1, 0), (met.Assertions!.Checked, met.Assertions.Failed));
        Assert.False(met.Assertions.Holds);

        var failed = Render(Rules, Row(("AclUnreadable", "acl.viewers")));
        var findings = failed.Assertions!;
        Assert.Equal((1, 1, 1), (findings.Checked, findings.Failed, findings.Held));
        Assert.True(findings.Holds);
        var failure = Assert.Single(findings.Failures);
        Assert.Equal(("dataset.AclUnreadable", "dataset.AclUnreadable", "acl-read-whole", "incoming", "hold", "acl.viewers"),
            (failure.At, failure.Path, failure.Assertion, failure.Stage, failure.OnFail, failure.Value));
        Assert.False(failed.IsHeld);
        Assert.Equal("thing-1", failed.Document["data"]!["Name"]!.GetValue<string>());
    }

    [Fact]
    public void A_rule_on_a_column_of_the_child_rows_judges_each_row_and_names_the_row_that_fails()
    {
        var result = Render("""
            - rows: curves
              column: unit
              exists: true
            """, Row(), ("curves", [Curve("MD", "M"), Curve("GR", null), Curve("RHOB", "G/C3")]));

        var findings = result.Assertions!;
        Assert.Equal((3, 1), (findings.Checked, findings.Failed));
        var failure = Assert.Single(findings.Failures);
        Assert.Equal(("dataset.curves[].unit", "dataset.curves[1].unit"), (failure.At, failure.Path));
    }

    [Fact]
    public void Where_on_the_child_rows_selects_the_rows_a_rule_reads()
    {
        var result = Render("""
            - rows: curves
              column: unit
              exists: true
              where:
                - column: curve_id
                  notEquals: MD
            """, Row(), ("curves", [Curve("MD", null), Curve("GR", "GAPI")]));

        Assert.Equal((1, 0), (result.Assertions!.Checked, result.Assertions.Failed));
    }

    [Fact]
    public void Where_on_the_record_row_decides_whether_the_rule_is_the_records_to_meet()
    {
        const string Rules = """
            - rows: curves
              count: { atLeast: 2 }
              where:
                - column: $dataset.kind
                  equals: LOG
            """;
        (string, List<SourceRow>) curves = ("curves", [Curve("MD", "M")]);

        var other = Render(Rules, Row(("kind", "TRAJECTORY")), curves);
        Assert.Equal((0, 0), (other.Assertions!.Checked, other.Assertions.Failed));

        var log = Render(Rules, Row(("kind", "LOG")), curves);
        Assert.Equal((1, 1), (log.Assertions!.Checked, log.Assertions.Failed));
    }

    [Fact]
    public void A_rule_that_one_child_row_has_to_meet_is_judged_once()
    {
        const string Rules = """
            - rows: curves
              column: curve_id
              equals: MD
              values: any
              onFail: report
            """;

        var met = Render(Rules, Row(), ("curves", [Curve("GR", "GAPI"), Curve("MD", "M")]));
        Assert.Equal((1, 0), (met.Assertions!.Checked, met.Assertions.Failed));

        var failed = Render(Rules, Row(), ("curves", [Curve("GR", "GAPI"), Curve("RHOB", "G/C3")]));
        var findings = failed.Assertions!;
        Assert.Equal((1, 1, 1, 0), (findings.Checked, findings.Failed, findings.Reported, findings.Held));
        Assert.False(findings.Holds);
        var failure = Assert.Single(findings.Failures);
        Assert.Equal(("dataset.curves[].curve_id", "dataset.curves[].curve_id", "report", "GR"), (failure.At, failure.Path, failure.OnFail, failure.Value));
        Assert.StartsWith("none of its 2 rows meets it; one ", failure.Message, StringComparison.Ordinal);

        var none = Render(Rules, Row());
        Assert.Equal((0, 0), (none.Assertions!.Checked, none.Assertions.Failed));
    }

    [Fact]
    public void A_count_rule_counts_the_records_child_rows_none_given_counting_none()
    {
        const string Rules = """
            - name: index-and-one-curve
              rows: curves
              count: { atLeast: 2 }
            """;

        Assert.Equal(0, Render(Rules, Row(), ("curves", [Curve("MD", "M"), Curve("GR", "GAPI")])).Assertions!.Failed);

        var one = Assert.Single(Render(Rules, Row(), ("curves", [Curve("MD", "M")])).Assertions!.Failures);
        Assert.Equal(("dataset.curves", "dataset.curves", "index-and-one-curve", "1"), (one.At, one.Path, one.Assertion, one.Value));
        Assert.Equal("the record has 1 row of curves, and the rule asks for count atLeast 2", one.Message);

        var none = Assert.Single(Render(Rules, Row()).Assertions!.Failures);
        Assert.Equal("0", none.Value);
        Assert.StartsWith("the record has 0 rows of curves,", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unique_rule_names_each_set_of_rows_sharing_values_and_compares_no_row_lacking_one()
    {
        var result = Render("""
            - rows: curves
              unique: [curve_id]
            """, Row(), ("curves", [Curve("GR", "GAPI"), Curve("MD", "M"), Curve("GR", "API"), Curve(null, "M"), Curve(null, "FT"), Curve("MD", "FT")]));

        var findings = result.Assertions!;
        Assert.Equal((1, 2), (findings.Checked, findings.Failed));
        Assert.Equal(
            [("dataset.curves[0]", "curve_id 'GR'", "rows 0 and 2 of curves share it, and the rule asks that no two rows do"),
             ("dataset.curves[1]", "curve_id 'MD'", "rows 1 and 5 of curves share it, and the rule asks that no two rows do")],
            findings.Failures.Select(f => (f.Path, f.Value, f.Message)));
        Assert.All(findings.Failures, f => Assert.Equal("dataset.curves", f.At));
    }

    [Fact]
    public void A_unique_rule_over_two_columns_compares_their_values_together()
    {
        var result = Render("""
            - rows: curves
              unique: [curve_id, unit]
            """, Row(), ("curves", [Curve("GR", "GAPI"), Curve("GR", "API"), Curve("GR", "GAPI")]));

        var failure = Assert.Single(result.Assertions!.Failures);
        Assert.Equal(("dataset.curves[0]", "curve_id 'GR', unit 'GAPI'"), (failure.Path, failure.Value));
    }

    [Fact]
    public void Rules_on_the_rows_and_property_assertions_are_judged_together_on_one_log()
    {
        var result = Render("""
            - column: AclUnreadable
              empty: true
            """, Row(("AclUnreadable", "x"), ("count", "312")), data: """
            Count:
              $from: count
              $assert:
                - atMost: 250
            """);

        var findings = result.Assertions!;
        Assert.Equal((2, 2), (findings.Checked, findings.Failed));
        Assert.Equal(["dataset.AclUnreadable", "data.Count"], findings.Failures.Select(f => f.At));
    }

    [Fact]
    public void A_rule_on_the_rows_judged_once_a_run_is_not_judged_by_a_render()
    {
        var result = Render("""
            - per: run
              unique: [name]
            """, Row());

        Assert.Null(result.Assertions);
    }

    [Fact]
    public void An_inspection_of_every_entry_judges_the_rules_on_the_rows_as_a_render_does()
    {
        var mapping = DatasetAssertionLoaderTests.Mapping("""
            - column: AclUnreadable
              empty: true
            """);
        var renderer = new MappingRenderer(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context());
        var record = new SourceRecord { Row = SourceRow.FromStrings(Row(("AclUnreadable", "x"))) };

        Assert.Equal("dataset.AclUnreadable", Assert.Single(renderer.Inspect(record, EntrySelection.All).Assertions!.Failures).At);

        // An inspection of some entries judges theirs alone: the rules on the rows are the record's as a whole.
        var some = renderer.Inspect(record, EntrySelection.Of([TemplatePath.TryParse("osdu.data.Name", out var name, out _) ? name! : throw new InvalidOperationException()]));
        Assert.True(some.Assertions is null || some.Assertions.Failed == 0);
    }

    private static RenderResult Render(string rules, Dictionary<string, string?> row, params (string Child, List<SourceRow> Rows)[] children)
        => Render(rules, row, string.Empty, children);

    private static RenderResult Render(string rules, Dictionary<string, string?> row, string data, params (string Child, List<SourceRow> Rows)[] children)
    {
        var mapping = DatasetAssertionLoaderTests.Mapping(rules, data);
        var renderer = new MappingRenderer(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context());
        var scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (child, rows) in children)
        {
            scopes[child] = rows;
        }

        return renderer.Render(new SourceRecord { Row = SourceRow.FromStrings(row), Scopes = scopes });
    }

    private static Dictionary<string, string?> Row(params (string Column, string? Value)[] values)
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "thing-1", ["depth"] = "12.5" };
        foreach (var (column, value) in values)
        {
            row[column] = value;
        }

        return row;
    }

    private static SourceRow Curve(string? id, string? unit)
        => SourceRow.FromStrings(new Dictionary<string, string?> { ["curve_id"] = id, ["unit"] = unit });
}
