using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a mapping's <c>dataset.assert</c> is read (osdu/docs/reference/flow/mapping-dataset-assertions.md): a rule on a
/// column of each row, on how many rows there are, or that no two rows share some values; over the record's row, its rows
/// of a child dataset, or every row of the run's scope; and each place it is refused, with what to write instead.
/// </summary>
public sealed class DatasetAssertionLoaderTests
{
    /// <summary>The test mapping with <paramref name="rules"/> as its <c>dataset.assert</c>.</summary>
    internal static string Document(string rules, string data = "")
    {
        var document = TestSchema.MappingDocument(data).ReplaceLineEndings("\n");
        const string Key = "  key: [name]\n";
        Assert.Contains(Key, document, StringComparison.Ordinal);
        return document.Replace(Key, Key + "  assert:\n" + TestSchema.Indented(rules, 4), StringComparison.Ordinal);
    }

    internal static MappingDefinition Mapping(string rules, string data = "")
        => new DeliveryDocumentLoader().ParseMapping(Document(rules, data), "thing.yaml");

    private static string Refused(string rules) => Assert.Throws<FlowValidationException>(() => Mapping(rules)).Message;

    [Fact]
    public void A_rule_on_a_column_of_the_record_row_is_judged_per_record_and_holds_unless_it_says_otherwise()
    {
        var rule = Assert.Single(Mapping("""
            - name: acl-read-whole
              description: The ACL column reads as a whole.
              column: AclUnreadable
              empty: true
            """).Dataset.Assertions);

        Assert.Equal(DatasetAssertionKind.Value, rule.Kind);
        Assert.Equal(DatasetAssertionPer.Record, rule.Per);
        Assert.Null(rule.Rows);
        Assert.Equal("AclUnreadable", rule.Column);
        Assert.Equal(ValueOperator.Empty, rule.Condition!.Operator);
        Assert.Equal(AssertionAction.Hold, rule.OnFail);
        Assert.Equal("acl-read-whole", rule.Label);
        Assert.True(rule.Named);
        Assert.Equal("The ACL column reads as a whole.", rule.Description);
        Assert.Equal("dataset.assert[0]", rule.Location);
        Assert.Equal("dataset.AclUnreadable", rule.At);
        Assert.Equal("the record's row", rule.RowsRead);
    }

    [Fact]
    public void A_rule_over_a_child_datasets_rows_takes_its_conditions_and_selects_rows_by_either_row()
    {
        var rule = Assert.Single(Mapping("""
            - rows: curves
              column: curve_unit
              in: [M, FT]
              ignoreCase: true
              values: any
              onFail: report
              where:
                - column: curve_id
                  notEquals: MD
                - column: $dataset.kind
                  equals: LOG
            """).Dataset.Assertions);

        Assert.Equal("curves", rule.Rows);
        Assert.Equal("curve_unit", rule.Column);
        Assert.Equal(ValueOperator.In, rule.Condition!.Operator);
        Assert.True(rule.Condition.IgnoreCase);
        Assert.True(rule.AnyRow);
        Assert.Equal(AssertionAction.Report, rule.OnFail);
        Assert.Equal([new DatasetColumn("curves", "curve_id"), new DatasetColumn(null, "kind")], rule.Where.Select(w => w.Column));
        Assert.Equal("dataset.curves[].curve_unit", rule.At);
        Assert.False(rule.Named);
        Assert.Equal(rule.Expected, rule.Label);
        Assert.StartsWith("curve_unit ", rule.Label, StringComparison.Ordinal);
        Assert.Contains(" in one row of curves where ", rule.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void A_count_and_a_unique_rule_read_a_child_datasets_rows_or_every_row_of_the_run()
    {
        var rules = Mapping("""
            - rows: curves
              count: { atLeast: 2 }
            - rows: curves
              count: 3
            - rows: curves
              unique: [curve_id, run]
            - per: run
              unique: log_name
            - per: run
              count: { between: [1, 500] }
            """).Dataset.Assertions;

        Assert.Equal(5, rules.Count);
        Assert.Equal((DatasetAssertionKind.Count, DatasetAssertionPer.Record, "dataset.curves"), (rules[0].Kind, rules[0].Per, rules[0].At));
        Assert.Equal(ComparisonOperator.AtLeast, Assert.Single(rules[0].Count!.Terms).Operator);
        Assert.Equal(ComparisonOperator.EqualTo, Assert.Single(rules[1].Count!.Terms).Operator);
        Assert.Equal(DatasetAssertionKind.Unique, rules[2].Kind);
        Assert.Equal(["curve_id", "run"], rules[2].Columns);
        Assert.Equal((DatasetAssertionKind.Unique, DatasetAssertionPer.Run, "dataset"), (rules[3].Kind, rules[3].Per, rules[3].At));
        Assert.Equal(["log_name"], rules[3].Columns);
        Assert.Equal("the record table's rows in the run's scope", rules[3].RowsRead);
        Assert.Equal(ComparisonOperator.Between, Assert.Single(rules[4].Count!.Terms).Operator);
        Assert.All(rules, r => Assert.Null(r.Condition));
    }

    [Fact]
    public void A_mapping_without_dataset_assert_has_no_rule_and_its_property_assertions_stay_where_they_are()
    {
        var mapping = TestSchema.Mapping("""
            Count:
              $from: count
              $assert:
                - stage: incoming
                  exists: true
            """);

        Assert.Empty(mapping.Dataset.Assertions);
        Assert.Single(mapping.Assertions());
    }

    [Theory]
    [InlineData("- stage: incoming\n  column: a\n  exists: true", "a rule on the incoming rows judges the rows as the ingestion tables hold them, so it takes no stage")]
    [InlineData("- field: data.Name\n  exists: true", "so it names a column of them, not a field of the record")]
    [InlineData("- $column: a\n  exists: true", "the words of a rule carry no '$', since they are not properties of the record; write column, not $column")]
    [InlineData("- colum: a\n  exists: true", "'colum' is not a word of a rule on the incoming rows. Did you mean 'column'?")]
    [InlineData("- name: x", "says nothing to judge: name a column and one condition")]
    [InlineData("- column: a\n  rows: curves\n  count: 2", "names column and count; a rule judges one of them")]
    [InlineData("- per: run\n  column: a\n  exists: true", "a condition on each row's column is judged as each record renders, so leave out per: run")]
    [InlineData("- per: run\n  rows: curves\n  count: 2", "per: run reads every row of the record table in the run's scope, and rows names a child dataset")]
    [InlineData("- per: run\n  unique: [a]\n  where:\n    - column: b\n      equals: x", "which a where does not narrow")]
    [InlineData("- count: 2", "counts the record's own row of the record table, and a record has one; write rows: <child dataset>")]
    [InlineData("- unique: [a]", "compares the record's own row of the record table, and a record has one")]
    [InlineData("- column: a\n  exists: true\n  values: any", "judges the record's one row of the record table; remove values")]
    [InlineData("- rows: curves\n  count: 2\n  values: any", "judges the rows together; remove values")]
    [InlineData("- column: a\n  exists: true\n  onFail: omit", "onFail: omit leaves a property's value out of the record, and a rule on the incoming rows judges no property")]
    [InlineData("- column: a\n  exists: true\n  onFail: stop", "onFail is 'stop'; write hold (the default")]
    [InlineData("- rows: curves\n  count: { atLeast: 2 }\n  equals: 3", "a count is compared inside it, such as count: { atLeast: 2 }, not with equals beside it")]
    [InlineData("- rows: curves\n  unique: [a]\n  equals: 3", "takes no condition (equals)")]
    [InlineData("- rows: curves\n  count: 2\n  ignoreCase: true", "ignoreCase and tolerance go with a condition on a column's value")]
    [InlineData("- rows: curves\n  unique: [a, A]", "names A more than once")]
    [InlineData("- rows: curves\n  unique: []", "names between 1 and 10 columns")]
    [InlineData("- rows: curves\n  unique: [a, b, c, d, e, f, g, h, i, j, k]", "names between 1 and 10 columns")]
    [InlineData("- rows: curves\n  column: $dataset.kind\n  exists: true", "'$dataset.kind' reads the record's row, and the rule judges the rows of curves")]
    [InlineData("- column: a.b\n  exists: true", "'a.b' is not a column; name a column of the record table as it is")]
    [InlineData("- rows: 'curves x'\n  count: 2", "rows names a child dataset whose rows the rule reads")]
    [InlineData("- rows: curves\n  count: { atLeast: -1 }", "count")]
    [InlineData("- 5", "is a rule on the incoming rows: a column and one condition")]
    [InlineData("- column: a\n  exists: true\n- column: a\n  exists: true", "dataset.assert[1] asserts what dataset.assert[0] asserts")]
    [InlineData("- name: x\n  column: a\n  exists: true\n- name: x\n  column: b\n  exists: true", "dataset.assert[1] is named 'x', as dataset.assert[0] is")]
    public void A_rule_that_cannot_be_judged_as_written_is_refused_saying_what_to_write(string rules, string expected)
        => Assert.Contains(expected, Refused(rules), StringComparison.Ordinal);

    [Fact]
    public void An_empty_list_and_too_many_rules_are_refused()
    {
        var empty = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(
            TestSchema.MappingDocument().ReplaceLineEndings("\n").Replace("  key: [name]\n", "  key: [name]\n  assert: []\n", StringComparison.Ordinal), "thing.yaml")).Message;
        Assert.Contains("dataset.assert lists no rule", empty, StringComparison.Ordinal);

        var many = string.Concat(Enumerable.Range(0, 51).Select(i => $"- column: c{i}\n  exists: true\n"));
        Assert.Contains("lists 51 rules, and a mapping states at most 50", Refused(many), StringComparison.Ordinal);
    }

    [Fact]
    public void A_preflight_with_the_source_columns_names_each_column_or_child_dataset_a_rule_reads_that_the_flow_lacks()
    {
        var mapping = Mapping("""
            - column: AclUnreadable
              empty: true
            - rows: curves
              unique: [curve_id, run]
              where:
                - column: $dataset.kind
                  equals: LOG
            - rows: logs
              count: 1
            - per: run
              unique: [log_name]
            - rows: curves
              column: curve_id
              exists: true
            """);
        var columns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [SourceDatasets.Record] = new HashSet<string>(["name", "depth", "kind"], StringComparer.OrdinalIgnoreCase),
            ["curves"] = new HashSet<string>(["curve_id"], StringComparer.OrdinalIgnoreCase),
        };

        var errors = Preflight.Check(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context(), columns)
            .Where(i => i.Severity == IssueSeverity.Error && i.Message.Contains("dataset.assert[", StringComparison.Ordinal))
            .Select(i => i.Message)
            .ToList();

        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Contains("dataset.assert[0] reads dataset.AclUnreadable, which the record table does not hold", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("dataset.assert[1] reads dataset.curves.run, which child dataset 'curves' does not hold", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("dataset.assert[2] reads the rows of child dataset 'logs', which the flow does not declare under source.datasets", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("dataset.assert[3] reads dataset.log_name, which the record table does not hold", StringComparison.Ordinal));
    }

    [Fact]
    public void A_name_is_one_to_the_longest_label_characters()
        => Assert.Contains(
            "name is what the rule is called wherever what it finds is shown",
            Refused($"- name: {new string('n', DatasetAssertion.MaxLabel + 1)}\n  column: a\n  exists: true"),
            StringComparison.Ordinal);
}
