using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A set's report (osdu/docs/reference/flow/assertion-sets.md): every test by where it stands now (disabled whatever an
/// earlier run found, not run when its last result judged an earlier version of it), the set's verdict, and the report as
/// a dashboard in HTML, as Markdown, JSON and JUnit XML. Each check reads once: what it expected, what it found and why.
/// </summary>
public sealed class AssertionSetReportTests
{
    private const string Flow = """
        flowType: assertion
        name: welldb-welllog-assertion-well-a
        description: Proof values of well A.
        set: welldb
        partitions: [dev]
        source: { endpoint: http://localhost }
        tests:
          - name: welldb-logs
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

    private static AssertionSetReport Report() => new(
        "welldb",
        "dev",
        [
            new AssertionSetFlow(
                Definition.Name, Definition.Description, null, null,
                [
                    AssertionSetReport.TestOf(TestNamed("welldb-logs"), Latest("welldb-logs", TestOutcomes.Warned, result: Result("welldb-logs", TestOutcomes.Warned, Warning()))),
                    AssertionSetReport.TestOf(TestNamed("archive-logs"), Latest("archive-logs", TestOutcomes.Failed)),
                    AssertionSetReport.TestOf(TestNamed("key-numbers"), Latest("key-numbers", TestOutcomes.Failed, hash: "an-earlier-version")),
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

    [Fact]
    public void The_html_is_a_dashboard_of_the_flows_each_opening_on_its_assertions()
    {
        var html = Report().ToHtml();
        Assert.Contains("<title>welldb assertion report</title>", html);
        Assert.Contains("Holds, with 1 warning in 1 flow: no assertion fails; 1 disabled, 1 not yet checked.", html);
        Assert.Contains("<details class=\"flow warned\"><summary>", html);
        // The flow's first issue says what its worst check expected and found, each once.
        Assert.Contains("<b>welldb-logs</b> 5191 wellbore(s): expected equals 5191, found 5190", html);
        // The check reads once: the comparison, its qualifiers, what it found and why.
        Assert.Contains("<td>equals 5191<br><small>warns within 10% &middot; stated as of 2026-10-27</small></td><td>5190</td><td class=\"why\">1 below 5191 (0.02%), within the warning band of 10%, so it warns.</td>", html);
        Assert.Contains("No flow of this estate delivers the archive logs to OSDU yet.", html);
        Assert.Contains("Changed since report 162", html);
        Assert.Contains("Not checked here", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\u2014', html);
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
