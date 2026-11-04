using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A set's report (osdu/docs/reference/flow/assertion-sets.md): every test by where it stands now (disabled whatever an
/// earlier run found, not run when its last result judged an earlier version of it), the set's verdict, every test's
/// dimensions, and the report as Markdown, JSON and JUnit XML. Each check reads once: what it expected, what it found and why.
/// </summary>
public sealed class AssertionSetReportTests
{
    private const string Flow = """
        flowType: assertion
        name: welldb-welllog-assertion-well-a
        description: Proof values of well A.
        title: Proof values of well A
        set: welldb
        dimensions: { Country: Norway, Source: Archive }
        partitions: [dev]
        source: { endpoint: http://localhost }
        tests:
          - name: welldb-logs
            title: Logs of well A
            dimensions: { Well: A }
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            assert:
              - count: { atLeast: 1 }
          - name: archive-logs
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            disabled: No flow of this estate delivers the archive logs to OSDU yet.
            assert:
              - count: { atLeast: 1 }
          - name: key-numbers
            kind: osdu:wks:work-product-component--WellLog:1.4.0
            assert:
              - count: { atLeast: 1 }
        """;

    private static readonly DateTime At = new(2026, 11, 3, 14, 5, 0, DateTimeKind.Utc);

    private static readonly AssertionFlowDefinition Definition = new DeliveryDocumentLoader().ParseAssertion(Flow, "flow.yaml").ForPartition("dev");

    private static AssertionTest TestNamed(string name) => Definition.Test(name)!;

    private static AssertionOutcome Warning() => new()
    {
        Index = 0,
        Label = "5191 wellbore(s)",
        Type = "aggregate",
        Severity = "warning",
        Outcome = TestOutcomes.Failed,
        Expected = "equals 5191 (warning within 10%) (stated as of 2026-10-27)",
        Actual = "distinct(data.WellboreID) is 5190",
        Message = "distinct(data.WellboreID) is 5190; expected equals 5191 (warning within 10%); 1 below 5191 (0.02%), within the warning band of 10%, so it warns.",
        Value = 5190,
    };

    private static TestResult Result(string test, string outcome, params AssertionOutcome[] checks) => new()
    {
        Test = test,
        Kind = "osdu:wks:work-product-component--WellLog:1.4.0",
        Outcome = outcome,
        Matched = 11358,
        Evaluated = 11358,
        DefinitionHash = TestNamed(test).DefinitionHash,
        Assertions = checks,
        StartedUtc = At.AddMinutes(-4),
        CompletedUtc = At.AddMinutes(-3),
    };

    private static AssertionResultState Latest(string test, string outcome, string? hash = null, TestResult? result = null) => new()
    {
        AssertionRunId = 162,
        FlowId = Definition.LedgerId,
        TestName = test,
        Kind = "osdu:wks:work-product-component--WellLog:1.4.0",
        Outcome = outcome,
        DefinitionHash = hash ?? TestNamed(test).DefinitionHash,
        Detail = TestResults.Serialize(result ?? Result(test, outcome)),
    };

    /// <summary>A test as a set's report reads it from the ledger: where it stands, with its title and dimensions.</summary>
    private static AssertionSetTest Of(string test, AssertionResultState latest)
        => AssertionSetReport.TestOf(TestNamed(test), latest) with { Title = TestNamed(test).Title, Dimensions = AssertionDimensions.Of(Definition, TestNamed(test)) };

    private static AssertionSetReport Report() => new(
        "welldb",
        "dev",
        [
            new AssertionSetFlow(
                Definition.Name, Definition.Description, null, null,
                [
                    Of("welldb-logs", Latest("welldb-logs", TestOutcomes.Warned, result: Result("welldb-logs", TestOutcomes.Warned, Warning()))),
                    Of("archive-logs", Latest("archive-logs", TestOutcomes.Failed)),
                    Of("key-numbers", Latest("key-numbers", TestOutcomes.Failed, hash: "an-earlier-version")),
                ]),
            new AssertionSetFlow("welldb-units-04-assertion", null, null, "Assertion flow 'welldb-units-04-assertion' tests prod, not dev.", []),
        ],
        At);

    [Fact]
    public void A_test_stands_as_its_document_states_it_now_whatever_an_earlier_run_found()
    {
        var disabled = AssertionSetReport.TestOf(TestNamed("archive-logs"), Latest("archive-logs", TestOutcomes.Failed));
        Assert.Equal(SetStandings.Disabled, disabled.Standing);
        Assert.Equal("No flow of this estate delivers the archive logs to OSDU yet.", disabled.Why);
        Assert.Null(disabled.Result);

        var changed = AssertionSetReport.TestOf(TestNamed("key-numbers"), Latest("key-numbers", TestOutcomes.Failed, hash: "an-earlier-version"));
        Assert.Equal(SetStandings.NotRun, changed.Standing);
        Assert.True(changed.Changed);
        Assert.Null(changed.Result);
        Assert.Equal("Changed since report 162, which checked an earlier version of it; no run has checked this version yet.", changed.Why);

        var judged = AssertionSetReport.TestOf(TestNamed("welldb-logs"), Latest("welldb-logs", TestOutcomes.Passed));
        Assert.Equal(TestOutcomes.Passed, judged.Standing);
        Assert.Equal(162, judged.AssertionRunId);
        Assert.NotNull(judged.Result);

        Assert.Equal(SetStandings.NotRun, AssertionSetReport.TestOf(TestNamed("welldb-logs"), null).Standing);
    }

    [Fact]
    public void The_verdict_says_how_the_set_stands_in_one_sentence()
    {
        var report = Report();
        Assert.Equal(TestOutcomes.Warned, report.Status);
        Assert.Equal("Holds, with 1 warning in 1 flow: no assertion fails; 1 disabled, 1 not yet checked.", report.Verdict());
        Assert.Equal(0.0, report.PassRate);
        Assert.Equal(1, report.Evaluated);
    }

    /// <summary>
    /// Every assertion has a Set, a Type and a Check without a word of YAML, then the dimensions its flow and the test name,
    /// the test's over its flow's; a document's own Check takes the place of the one read off the test.
    /// </summary>
    [Fact]
    public void An_assertion_has_set_type_and_check_then_the_dimensions_its_documents_name()
    {
        Assert.Equal(
            [("Set", "welldb"), ("Type", "WellLog 1.4.0"), ("Check", "Counts"), ("Country", "Norway"), ("Source", "Archive"), ("Well", "A")],
            AssertionDimensions.Of(Definition, TestNamed("welldb-logs")).Select(d => (d.Name, d.Value)));
        Assert.Equal("Logs of well A", TestNamed("welldb-logs").Title);
        Assert.Equal("Proof values of well A", Definition.Title);

        var restated = new DeliveryDocumentLoader().ParseAssertion(
            Flow.Replace("dimensions: { Well: A }", "dimensions: { Well: A, Check: Proof values, Source: Interpretation }", StringComparison.Ordinal), "flow.yaml").ForPartition("dev");
        Assert.Equal(
            [("Set", "welldb"), ("Type", "WellLog 1.4.0"), ("Check", "Proof values"), ("Country", "Norway"), ("Source", "Interpretation"), ("Well", "A")],
            AssertionDimensions.Of(restated, restated.Test("welldb-logs")!).Select(d => (d.Name, d.Value)));

        // A title and dimensions say how a test is shown, never what it checks: they leave its definition hash as it was.
        var bare = new DeliveryDocumentLoader().ParseAssertion(
            Flow.Replace("    title: Logs of well A\n", string.Empty, StringComparison.Ordinal).Replace("dimensions: { Well: A }", "dimensions: { Well: B }", StringComparison.Ordinal),
            "flow.yaml").ForPartition("dev");
        Assert.Equal(TestNamed("welldb-logs").DefinitionHash, bare.Test("welldb-logs")!.DefinitionHash);
    }

    [Fact]
    public void The_markdown_json_and_junit_say_the_same()
    {
        var report = Report();

        var markdown = report.ToMarkdown();
        Assert.Contains("# welldb: assertion report", markdown);
        Assert.Contains("| Flow | Status | Passed | Needs attention |", markdown);
        Assert.Contains("| No flow of this estate delivers the archive logs to OSDU yet. | archive-logs (welldb-welllog-assertion-well-a) |", markdown);
        Assert.Contains("| Check | Result | Expected | Found | Why |", markdown);

        var json = JsonNode.Parse(report.ToJson())!;
        Assert.Equal("warned", json["status"]!.GetValue<string>());
        Assert.Equal(1, json["counts"]!["disabled"]!.GetValue<int>());
        Assert.Equal(1, json["counts"]!["notRun"]!.GetValue<int>());
        var tests = json["flows"]!.AsArray().Single(f => f!["flow"]!.GetValue<string>() == Definition.Name)!["tests"]!.AsArray();
        Assert.Equal(["warned", "notRun", "disabled"], tests.Select(t => t!["standing"]!.GetValue<string>()));
        Assert.Equal("Logs of well A", tests[0]!["title"]!.GetValue<string>());
        Assert.Equal("Norway", tests[0]!["dimensions"]!["Country"]!.GetValue<string>());
        Assert.Equal("Counts", tests[0]!["dimensions"]!["Check"]!.GetValue<string>());
        // The page to present is the set's dashboard, which the GUI renders: the report renders no HTML of its own.
        Assert.Throws<ArgumentOutOfRangeException>(() => report.Render(ReportFormat.Html));

        var junit = report.ToJUnit();
        Assert.Contains("<testsuites name=\"welldb@dev\" tests=\"3\" failures=\"0\" errors=\"0\" skipped=\"2\"", junit);
        Assert.Contains("<skipped message=\"No flow of this estate delivers the archive logs to OSDU yet.\" />", junit);
        Assert.Equal("welldb-dev-assertion-report.xml", report.FileName(ReportFormat.JUnit));
        foreach (var text in new[] { markdown, junit })
        {
            Assert.DoesNotContain('\u2014', text);
        }
    }

    [Fact]
    public void A_check_reads_each_fact_once()
    {
        var view = CheckView.Of(Warning());
        Assert.Equal("equals 5191", view.Expected);
        Assert.Equal(["warns within 10%", "stated as of 2026-10-27"], view.Qualifiers);
        Assert.Equal("5190", view.Found);
        Assert.Equal("1 below 5191 (0.02%), within the warning band of 10%, so it warns.", view.Why);

        var passed = CheckView.Of(Warning() with { Outcome = TestOutcomes.Passed, Expected = "equals 104305, leaving out \"MD\"", Value = 104305 });
        Assert.Equal("equals 104305", passed.Expected);
        Assert.Equal(["leaving out \"MD\""], passed.Qualifiers);
        Assert.Null(passed.Why);
    }
}
