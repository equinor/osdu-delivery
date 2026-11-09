using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What the rules judged once a run (<c>dataset.assert</c> with <c>per: run</c>, osdu/docs/reference/flow/mapping-dataset-assertions.md)
/// make of each record: a count that fails reaches every record, values shared reach the records whose rows share them, and
/// more shared rows than a run names reach every record; each record counts one judgement per rule.
/// </summary>
public sealed class RunRowsFindingsTests
{
    private static readonly IReadOnlyList<DatasetAssertion> Rules = DatasetAssertionLoaderTests.Mapping("""
        - name: few-logs
          per: run
          count: { atMost: 3 }
        - name: one-log-per-name
          per: run
          unique: [log_name]
          onFail: report
        """).Dataset.Assertions;

    private static DatasetAssertion Count => Rules[0];

    private static DatasetAssertion Unique => Rules[1];

    private static SourceRecord Record(string key)
        => new() { Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = key }), SourceKeyJson = KeyTuple.Of(key).Json };

    [Fact]
    public void Rules_that_hold_count_one_judgement_each_and_find_nothing()
    {
        var found = RunRowsFindings.Judge("Thing@1.0.0", [new RunRowsAnswer { Rule = Count, Rows = 3 }, new RunRowsAnswer { Rule = Unique }]);

        Assert.False(found.IsEmpty);
        Assert.Empty(found.Failures);
        var findings = found.Apply(Record("a"), null)!;
        Assert.Equal((2, 0), (findings.Checked, findings.Failed));
    }

    [Fact]
    public void A_count_that_fails_reaches_every_record_with_what_was_counted()
    {
        var found = RunRowsFindings.Judge("Thing@1.0.0", [new RunRowsAnswer { Rule = Count, Rows = 5 }]);

        Assert.Equal("few-logs: the run's scope holds 5 rows of the record table, and the rule asks for count atMost 3; every record of the run is held", Assert.Single(found.Failures));
        foreach (var key in new[] { "a", "b" })
        {
            var failure = Assert.Single(found.Apply(Record(key), null)!.Failures);
            Assert.Equal(("dataset", "dataset", "few-logs", "incoming", "hold", "5"), (failure.At, failure.Path, failure.Assertion, failure.Stage, failure.OnFail, failure.Value));
        }
    }

    [Fact]
    public void Shared_values_reach_the_records_whose_rows_share_them_and_no_other()
    {
        var found = RunRowsFindings.Judge("Thing@1.0.0", [new RunRowsAnswer
        {
            Rule = Unique,
            Shared = [new RunSharedRow(KeyTuple.Of("a"), ["GR"], 2), new RunSharedRow(KeyTuple.Of("b"), ["GR"], 2)],
            SharedRows = 2,
        }]);

        Assert.StartsWith("one-log-per-name: 2 rows of the record table in the run's scope share values of log_name with another row (first log_name 'GR', in 2 rows)",
            Assert.Single(found.Failures), StringComparison.Ordinal);
        var a = Assert.Single(found.Apply(Record("a"), null)!.Failures);
        Assert.Equal(("report", "log_name 'GR'", "2 rows of the record table in the run's scope share it, and the rule asks that no two rows do"), (a.OnFail, a.Value, a.Message));
        var c = found.Apply(Record("c"), null)!;
        Assert.Equal((1, 0), (c.Checked, c.Failed));
    }

    [Fact]
    public void More_shared_rows_than_a_run_names_reach_every_record()
    {
        var found = RunRowsFindings.Judge("Thing@1.0.0", [new RunRowsAnswer
        {
            Rule = Unique,
            Shared = [new RunSharedRow(KeyTuple.Of("a"), ["GR"], 3)],
            SharedRows = 250_000,
        }]);

        var failure = Assert.Single(found.Apply(Record("z"), null)!.Failures);
        Assert.Contains("more than a run names (1), so every record of the run is judged to fail it", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void What_a_run_found_joins_what_the_render_found()
    {
        var found = RunRowsFindings.Judge("Thing@1.0.0", [new RunRowsAnswer { Rule = Count, Rows = 9 }]);
        var log = new AssertionLog("Thing@1.0.0");
        log.Judged();
        var rendered = log.Findings();

        var combined = found.Apply(Record("a"), rendered)!;
        Assert.Equal((2, 1, 1), (combined.Checked, combined.Failed, combined.Held));
        Assert.Same(rendered, RunRowsFindings.None.Apply(Record("a"), rendered));
    }

    [Fact]
    public void An_answer_for_a_rule_judged_as_each_record_renders_is_refused()
    {
        var perRecord = Assert.Single(DatasetAssertionLoaderTests.Mapping("""
            - rows: curves
              count: 2
            """).Dataset.Assertions);

        Assert.Throws<ArgumentException>(() => RunRowsFindings.Judge("Thing@1.0.0", [new RunRowsAnswer { Rule = perRecord }]));
    }
}
