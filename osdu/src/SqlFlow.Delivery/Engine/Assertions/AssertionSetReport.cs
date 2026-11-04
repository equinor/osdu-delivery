using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>Where a test of a set stands in the set's report: a test outcome, or one of these two.</summary>
public static class SetStandings
{
    /// <summary>Its document disables it (the test, or every assertion of it), so no run judges it.</summary>
    public const string Disabled = "disabled";

    /// <summary>No run has judged it, as the document states it now, in the partition.</summary>
    public const string NotRun = "notRun";

    /// <summary>The order a report lists standings in: what needs a look first, then what is not known yet, then what passed.</summary>
    public static int Rank(string standing) => standing switch
    {
        TestOutcomes.Failed => 0,
        TestOutcomes.Errored => 1,
        TestOutcomes.Warned => 2,
        NotRun => 3,
        TestOutcomes.Passed => 4,
        Disabled => 5,
        _ => 6,
    };

    /// <summary>A standing as a reader of a report reads it, whoever they are: "Could not be checked" for an errored test.</summary>
    public static string Word(string standing) => standing switch
    {
        TestOutcomes.Passed => "Passed",
        TestOutcomes.Failed => "Failed",
        TestOutcomes.Warned => "Warning",
        TestOutcomes.Errored => "Could not be checked",
        Disabled => "Disabled",
        NotRun => "Not yet checked",
        _ => "Skipped",
    };
}

/// <summary>
/// One test of a set as its report shows it: where it stands, why (its document's reason for disabling it, or that it changed
/// since its last result), and its latest result whole, with the report it is from, when that result judged the test as it is
/// now. <c>Changed</c> is true when the test's definition has changed since its last result, which then stands as not run.
/// </summary>
public sealed record AssertionSetTest(
    string Test, string Kind, string? Description, string Standing, string? Why, long? AssertionRunId, bool Changed, TestResult? Result)
{
    /// <summary>What people read the test as; null when it names none.</summary>
    public string? Title { get; init; }

    /// <summary>Every dimension of the test: Set, Type and Check, then the ones its documents name.</summary>
    public IReadOnlyList<AssertionDimension> Dimensions { get; init; } = [];
}

/// <summary>
/// One flow of a set as its report shows it: what it is for, its last run in the partition, what keeps it from being judged
/// there (a document that does not parse, a partition it does not test), and its tests that run in the partition.
/// </summary>
public sealed record AssertionSetFlow(string Flow, string? Description, AssertionRunState? LastRun, string? Problem, IReadOnlyList<AssertionSetTest> Tests);

/// <summary>
/// A flow of a set as the caller found it: bound to the partition the report describes (with the partition its tests are
/// judged in), or with the problem that keeps it from being read there.
/// </summary>
public sealed record AssertionSetMember(string Flow, AssertionFlowDefinition? Bound, string? TestPartition, string? Problem);

/// <summary>
/// The report of an assertion set (osdu/docs/reference/flow/assertion-sets.md): where every test of every flow in the set
/// stands now in one partition, each by its latest result of the test as its document states it, the tests the documents
/// disable with why, and the tests no run has judged yet. It renders as Markdown for a pull request or a wiki, as JSON (each
/// test with its title and dimensions), and as JUnit XML for a CI server, whose cases a run's report writes; the page to
/// present is the set's dashboard, which the GUI renders and downloads.
/// </summary>
public sealed record AssertionSetReport(string Set, string? Partition, IReadOnlyList<AssertionSetFlow> Flows, DateTime GeneratedUtc)
{
    /// <summary>The longest line a flow's first issue is told in on the flows at a glance.</summary>
    private const int IssueLength = 220;

    /// <summary>
    /// Reads a set's report from the ledger: each bound flow's last run and each of its tests' latest result, whole, read in
    /// one query across the flows; a flow with a problem keeps it and lists no test.
    /// </summary>
    public static async Task<AssertionSetReport> ReadAsync(
        string set, string? partition, IReadOnlyList<AssertionSetMember> members, ILedger ledger, DateTime generatedUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(set);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(ledger);
        var ledgerIds = members.Where(m => m.Bound is not null).Select(m => m.Bound!.LedgerId).Distinct().ToList();
        var latest = (await ledger.LatestAssertionResultsAsync(ledgerIds, withDetail: true, ct).ConfigureAwait(false))
            .GroupBy(r => r.FlowId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.TestName, StringComparer.OrdinalIgnoreCase));
        var flows = new List<AssertionSetFlow>(members.Count);
        foreach (var member in members)
        {
            if (member.Bound is not { } bound)
            {
                flows.Add(new AssertionSetFlow(member.Flow, null, null, member.Problem ?? "The flow could not be read.", []));
                continue;
            }

            var runs = await ledger.ListAssertionRunsAsync(bound.LedgerId, 1, ct).ConfigureAwait(false);
            var ofFlow = latest.TryGetValue(bound.LedgerId, out var found) ? found : null;
            var tests = bound.Tests
                .Where(t => t.RunsIn(member.TestPartition))
                .Select(t => TestOf(t, ofFlow is not null && ofFlow.TryGetValue(t.Name, out var hit) ? hit : null) with
                {
                    Title = t.Title,
                    Dimensions = AssertionDimensions.Of(bound, t),
                })
                .ToList();
            flows.Add(new AssertionSetFlow(bound.Name, bound.Description, runs.Count > 0 ? runs[0] : null, member.Problem, tests));
        }

        return new AssertionSetReport(set, partition, flows, generatedUtc);
    }

    /// <summary>
    /// Where a test stands in a set, as its document states it now: disabled when the document says so, whatever an earlier
    /// run found; else by its latest result when that result judged the test as it is now; else not run. A result of an
    /// earlier version of the test is history and says nothing of this one.
    /// </summary>
    public static AssertionSetTest TestOf(AssertionTest test, AssertionResultState? latest)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (AssertionActivity.Disabled(test) is { } off)
        {
            return new AssertionSetTest(test.Name, test.Kind, test.Description, SetStandings.Disabled, test.Disabled ?? off, null, false, null);
        }

        if (latest is null)
        {
            return new AssertionSetTest(test.Name, test.Kind, test.Description, SetStandings.NotRun, null, null, false, null);
        }

        return string.Equals(latest.DefinitionHash, test.DefinitionHash, StringComparison.Ordinal)
            ? new AssertionSetTest(test.Name, test.Kind, test.Description, latest.Outcome, null, latest.AssertionRunId, false, TestResults.Deserialize(latest.Detail))
            : new AssertionSetTest(
                test.Name, test.Kind, test.Description, SetStandings.NotRun,
                string.Create(CultureInfo.InvariantCulture, $"Changed since report {latest.AssertionRunId}, which checked an earlier version of it; no run has checked this version yet."),
                null, true, null);
    }

    /// <summary>Every test of the set that runs in the partition.</summary>
    public IEnumerable<AssertionSetTest> Tests => Flows.SelectMany(f => f.Tests);

    /// <summary>How many tests of the set stand at <paramref name="standing"/>.</summary>
    public int Count(string standing) => Tests.Count(t => t.Standing == standing);

    /// <summary>The tests a run judged, of which the pass rate is a share: every test that is neither disabled, skipped nor unrun.</summary>
    public int Evaluated => Tests.Count(t => t.Standing is TestOutcomes.Passed or TestOutcomes.Failed or TestOutcomes.Warned or TestOutcomes.Errored);

    /// <summary>The share of the evaluated tests that passed, in percent; null when none was evaluated.</summary>
    public double? PassRate => Evaluated == 0 ? null : Math.Round(100.0 * Count(TestOutcomes.Passed) / Evaluated, 1);

    /// <summary>
    /// How the set stands as a whole: failed when a test failed, else errored, else warned; passed when every evaluated test
    /// passed; not run when no test has been judged.
    /// </summary>
    public string Status
        => Count(TestOutcomes.Failed) > 0 ? TestOutcomes.Failed
            : Count(TestOutcomes.Errored) > 0 ? TestOutcomes.Errored
            : Count(TestOutcomes.Warned) > 0 ? TestOutcomes.Warned
            : Count(TestOutcomes.Passed) > 0 ? TestOutcomes.Passed
            : SetStandings.NotRun;

    /// <summary>
    /// The set's verdict in one sentence, what a report opens with: how many tests need a look and in how many flows, or that
    /// every judged test passes.
    /// </summary>
    public string Verdict()
    {
        var total = Tests.Count();
        int FlowsWith(string standing) => Flows.Count(f => f.Tests.Any(t => t.Standing == standing));
        var also = new[]
        {
            Count(SetStandings.Disabled) > 0 ? string.Create(CultureInfo.InvariantCulture, $"{Count(SetStandings.Disabled)} disabled") : null,
            Count(SetStandings.NotRun) > 0 ? string.Create(CultureInfo.InvariantCulture, $"{Count(SetStandings.NotRun)} not yet checked") : null,
        }.Where(p => p is not null).ToList();
        var tail = also.Count == 0 ? "." : $"; {string.Join(", ", also)}.";
        return Status switch
        {
            TestOutcomes.Failed => string.Create(CultureInfo.InvariantCulture,
                $"Needs attention: {Count(TestOutcomes.Failed)} of {Counted(total, "assertion")} fail, in {FlowsWith(TestOutcomes.Failed)} of {Counted(Flows.Count, "flow")}{tail}"),
            TestOutcomes.Errored => string.Create(CultureInfo.InvariantCulture,
                $"Needs attention: {Counted(Count(TestOutcomes.Errored), "assertion")} could not be checked, in {Counted(FlowsWith(TestOutcomes.Errored), "flow")}{tail}"),
            TestOutcomes.Warned => string.Create(CultureInfo.InvariantCulture,
                $"Holds, with {Counted(Count(TestOutcomes.Warned), "warning")} in {Counted(FlowsWith(TestOutcomes.Warned), "flow")}: no assertion fails{tail}"),
            TestOutcomes.Passed => string.Create(CultureInfo.InvariantCulture,
                $"Holds: {(Evaluated == 1 ? "the one assertion checked passes" : $"all {Evaluated} assertions checked pass")}{tail}"),
            _ => $"Not yet checked: no run has checked the assertions of this set in {Partition ?? "this partition"}{tail}",
        };
    }

    /// <summary>The flows in the order the report lists them: the one whose worst test needs a look most first, then by name.</summary>
    public IReadOnlyList<AssertionSetFlow> Ordered() => Flows
        .Select(f => f with { Tests = f.Tests.Select((t, i) => (t, i)).OrderBy(x => SetStandings.Rank(x.t.Standing)).ThenBy(x => x.i).Select(x => x.t).ToList() })
        .OrderBy(FlowRank)
        .ThenBy(f => f.Flow, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The file name a set's report downloads as: the set and the partition.</summary>
    public string FileName(ReportFormat format)
    {
        var safe = new string(Set.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return string.Create(CultureInfo.InvariantCulture, $"{safe}-{Partition ?? "partition"}-assertion-report.{AssertionReport.Describe(format).Extension}");
    }

    /// <summary>The report in a format a set's report takes: JSON, Markdown or JUnit XML. Its dashboard is the GUI's to render.</summary>
    public string Render(ReportFormat format) => format switch
    {
        ReportFormat.Markdown => ToMarkdown(),
        ReportFormat.JUnit => ToJUnit(),
        ReportFormat.Json => ToJson(),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "A set's report renders as JSON, Markdown or JUnit XML; its dashboard downloads as a page from the GUI."),
    };

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["set"] = Set,
            ["partition"] = Partition,
            ["status"] = Status,
            ["verdict"] = Verdict(),
            ["counts"] = Counts(),
            ["passRate"] = PassRate,
            ["generatedUtc"] = GeneratedUtc,
            ["flows"] = new JsonArray(Ordered().Select(f => (JsonNode)new JsonObject
            {
                ["flow"] = f.Flow,
                ["description"] = f.Description,
                ["problem"] = f.Problem,
                ["lastRun"] = f.LastRun is not { } run ? null : new JsonObject
                {
                    ["assertionRunId"] = run.AssertionRunId,
                    ["status"] = run.Status,
                    ["startedUtc"] = run.StartedUtc,
                    ["completedUtc"] = run.CompletedUtc,
                },
                ["tests"] = new JsonArray(f.Tests.Select(t => (JsonNode)new JsonObject
                {
                    ["test"] = t.Test,
                    ["title"] = t.Title,
                    ["kind"] = t.Kind,
                    ["dimensions"] = new JsonObject(t.Dimensions.Select(d => KeyValuePair.Create(d.Name, (JsonNode?)d.Value))),
                    ["standing"] = t.Standing,
                    ["why"] = t.Why,
                    ["assertionRunId"] = t.AssertionRunId,
                    ["changed"] = t.Changed,
                    ["result"] = t.Result is null ? null : JsonSerializer.SerializeToNode(t.Result, TestResults.Json),
                }).ToArray()),
            }).ToArray()),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The report in Markdown: the verdict, the figures, the flows at a glance, what needs attention, and the rest.</summary>
    public string ToMarkdown()
    {
        var ordered = Ordered();
        var md = new StringBuilder();
        md.Append("# ").Append(Set).AppendLine(": assertion report");
        md.AppendLine();
        md.Append(CultureInfo.InvariantCulture,
            $"Partition {Partition ?? "?"}, {Counted(Flows.Count, "flow")}, {Counted(Tests.Count(), "assertion")}; as of {Day(GeneratedUtc)}.").AppendLine();
        md.AppendLine().Append("**").Append(AssertionReport.Cell(Verdict())).AppendLine("**");
        md.AppendLine();
        md.AppendLine("| Pass rate | Passed | Failed | Warning | Could not be checked | Disabled | Not yet checked |");
        md.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        md.Append(CultureInfo.InvariantCulture,
            $"| {Rate()} | {Count(TestOutcomes.Passed)} | {Count(TestOutcomes.Failed)} | {Count(TestOutcomes.Warned)} | {Count(TestOutcomes.Errored)} | {Count(SetStandings.Disabled)} | {Count(SetStandings.NotRun)} |")
            .AppendLine();

        md.AppendLine().AppendLine("## Flows at a glance").AppendLine();
        md.AppendLine("| Flow | Status | Passed | Needs attention |");
        md.AppendLine("| --- | --- | ---: | --- |");
        foreach (var flow in ordered)
        {
            md.Append(CultureInfo.InvariantCulture,
                $"| {AssertionReport.Cell(flow.Flow)} | {FlowWord(flow)} | {FlowPassed(flow)} | {AssertionReport.Cell(IssueText(flow))} |").AppendLine();
        }

        var attention = ordered.Where(f => f.Tests.Any(NeedsLook)).ToList();
        if (attention.Count > 0)
        {
            md.AppendLine().AppendLine("## What needs attention");
            foreach (var flow in attention)
            {
                md.AppendLine().Append("### ").AppendLine(flow.Flow);
                if (flow.Description is { } description)
                {
                    md.AppendLine().AppendLine(description);
                }

                foreach (var test in flow.Tests.Where(NeedsLook))
                {
                    if (test.Result is { } result)
                    {
                        AssertionReport.WriteResultMarkdown(md, result, string.Create(CultureInfo.InvariantCulture,
                            $"{test.Test}: {SetStandings.Word(test.Standing)} (report {test.AssertionRunId})"));
                    }
                    else
                    {
                        md.AppendLine().Append("#### ").Append(test.Test).Append(": ").AppendLine(SetStandings.Word(test.Standing));
                        md.AppendLine().AppendLine(AssertionReport.Cell(Unjudged(test)));
                    }
                }
            }
        }

        WriteMarkdownGroups(md, "Disabled", "Why it is disabled", ordered, SetStandings.Disabled);
        WriteMarkdownGroups(md, "Not yet checked", "Why", ordered, SetStandings.NotRun);
        WriteMarkdownGroups(md, "Passing", "Checks", ordered, TestOutcomes.Passed);
        md.AppendLine().Append(CultureInfo.InvariantCulture,
            $"Each assertion is reported by its latest result in {Partition ?? "its partition"}, of the assertion as its flow states it now. Generated {Day(GeneratedUtc)} by OSDU Delivery.").AppendLine();
        return md.ToString();
    }

    /// <summary>
    /// The report as JUnit XML: a suite per flow, a case per test. A disabled test is skipped with its reason, and so is one no
    /// run has checked as it is now; every other case is written as a run's report writes it.
    /// </summary>
    public string ToJUnit()
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using var buffer = new AssertionReport.StringWriterUtf8();
        using (var xml = XmlWriter.Create(buffer, settings))
        {
            xml.WriteStartElement("testsuites");
            xml.WriteAttributeString("name", $"{Set}@{Partition}");
            WriteCounts(xml, Tests.ToList());
            xml.WriteAttributeString("time", AssertionReport.Seconds(Tests.Sum(t => t.Result?.DurationMs ?? 0)));
            xml.WriteAttributeString("timestamp", AssertionReport.Stamp(GeneratedUtc));
            foreach (var flow in Ordered())
            {
                xml.WriteStartElement("testsuite");
                xml.WriteAttributeString("name", flow.Flow);
                WriteCounts(xml, flow.Tests);
                xml.WriteAttributeString("time", AssertionReport.Seconds(flow.Tests.Sum(t => t.Result?.DurationMs ?? 0)));
                xml.WriteStartElement("properties");
                foreach (var (name, value) in new[]
                         {
                             ("set", Set), ("flow", flow.Flow), ("partition", Partition ?? string.Empty),
                             ("lastReport", flow.LastRun?.AssertionRunId.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                             ("problem", flow.Problem ?? string.Empty),
                         })
                {
                    xml.WriteStartElement("property");
                    xml.WriteAttributeString("name", name);
                    xml.WriteAttributeString("value", value);
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
                foreach (var test in flow.Tests)
                {
                    if (test.Result is { } result)
                    {
                        AssertionReport.WriteTestCase(xml, $"{Set}.{flow.Flow}", result);
                        continue;
                    }

                    xml.WriteStartElement("testcase");
                    xml.WriteAttributeString("classname", $"{Set}.{flow.Flow}");
                    xml.WriteAttributeString("name", test.Test);
                    xml.WriteAttributeString("time", "0");
                    xml.WriteStartElement("skipped");
                    xml.WriteAttributeString("message", Unjudged(test));
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        return buffer.ToString();
    }

    /// <summary>
    /// The tests of one standing in groups, in the order the report lists their flows: disabled tests by the reason their
    /// documents give, so a reason shared by many reads once; any other by flow, so a flow of many reads as one line.
    /// </summary>
    private List<(string Key, List<(string Flow, AssertionSetTest Test)> Rows)> Grouped(IReadOnlyList<AssertionSetFlow> ordered, string standing)
        => ordered
            .SelectMany(f => f.Tests.Where(t => t.Standing == standing).Select(t => (f.Flow, Test: t)))
            .GroupBy(r => standing == SetStandings.Disabled ? Unjudged(r.Test) : r.Flow, StringComparer.Ordinal)
            .Select(g => (g.Key, g.ToList()))
            .ToList();

    /// <summary>What one flow's group of tests says beside its names: why they are not yet checked, or how many checks pass.</summary>
    private string GroupText(string standing, IReadOnlyCollection<AssertionSetTest> tests)
    {
        if (standing == TestOutcomes.Passed)
        {
            var checks = tests.Sum(t => t.Result?.Assertions.Count ?? 0);
            return checks == 1 ? "its check passes" : string.Create(CultureInfo.InvariantCulture, $"all {checks} checks pass");
        }

        if (tests.Count == 1)
        {
            return Unjudged(tests.First());
        }

        var changed = tests.Count(t => t.Changed);
        var never = tests.Count - changed;
        return string.Join("; ", new[]
        {
            changed > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{(changed == tests.Count ? "All changed" : $"{changed} changed")} since their last result, which checked an earlier version")
                : null,
            never > 0 ? (never == tests.Count ? $"No run has checked them in {Partition ?? "this partition"} yet" : string.Create(CultureInfo.InvariantCulture, $"{never} never run")) : null,
        }.Where(p => p is not null)) + ".";
    }

    private static bool NeedsLook(AssertionSetTest test) => test.Standing is TestOutcomes.Failed or TestOutcomes.Errored or TestOutcomes.Warned;

    private static int FlowRank(AssertionSetFlow flow)
        => flow.Problem is not null && flow.Tests.Count == 0 ? 1 : flow.Tests.Count == 0 ? 7 : flow.Tests.Min(t => SetStandings.Rank(t.Standing));

    /// <summary>How a flow stands: its worst test's standing, or not checked when it has none to judge here.</summary>
    private static string FlowStanding(AssertionSetFlow flow)
        => flow.Tests.Count == 0 ? (flow.Problem is not null ? TestOutcomes.Errored : SetStandings.NotRun) : flow.Tests.MinBy(t => SetStandings.Rank(t.Standing))!.Standing;

    private static string FlowWord(AssertionSetFlow flow)
        => flow.Tests.Count == 0 ? (flow.Problem is not null ? "Not checked here" : "No assertion") : SetStandings.Word(FlowStanding(flow));

    private static string FlowPassed(AssertionSetFlow flow)
    {
        var judged = flow.Tests.Count(t => t.Standing is TestOutcomes.Passed or TestOutcomes.Failed or TestOutcomes.Warned or TestOutcomes.Errored);
        return judged == 0
            ? flow.Tests.Count == 0 ? "-" : "none checked"
            : string.Create(CultureInfo.InvariantCulture, $"{flow.Tests.Count(t => t.Standing == TestOutcomes.Passed)} of {judged} pass");
    }

    /// <summary>
    /// The first thing a flow's reader should know is wrong, in one line: its worst test (null for a problem of the flow's own)
    /// and that test's first check that did not hold, with what it expected and found, or why it could not be checked, or the
    /// problem that keeps the flow from being checked; null when nothing is wrong.
    /// </summary>
    private static (string? Test, string Text)? FirstIssue(AssertionSetFlow flow)
    {
        if (flow.Problem is { } problem && flow.Tests.Count == 0)
        {
            return (null, Clip(problem));
        }

        var test = flow.Tests.FirstOrDefault(NeedsLook);
        if (test is null)
        {
            return null;
        }

        var others = flow.Tests.Count(NeedsLook) - 1;
        var more = others > 0 ? string.Create(CultureInfo.InvariantCulture, $" (and {Counted(others, "more assertion")})") : string.Empty;
        if (test.Result is not { } result)
        {
            return (test.Test, Clip($"{SetStandings.Word(test.Standing).ToLowerInvariant()}{more}."));
        }

        var check = result.Assertions.FirstOrDefault(a => AssertionReport.Standing(a) is TestOutcomes.Failed or TestOutcomes.Errored)
            ?? result.Assertions.FirstOrDefault(a => AssertionReport.Standing(a) == TestOutcomes.Warned);
        if (check is null)
        {
            return (test.Test, Clip($"{result.Error ?? (result.Problems.Count > 0 ? result.Problems[0] : null) ?? SetStandings.Word(test.Standing).ToLowerInvariant()}{more}"));
        }

        var view = CheckView.Of(check);
        var found = check.Actual is not null ? $", found {view.Found}" : check.Message is { } message ? $": {message}" : string.Empty;
        return (test.Test, Clip($"{check.Label}: expected {view.Expected}{found}{more}"));
    }

    /// <summary>A flow's first issue as one line of text, its test first: what Markdown's table cell holds.</summary>
    private static string IssueText(AssertionSetFlow flow)
        => FirstIssue(flow) is not { } issue ? string.Empty : issue.Test is null ? issue.Text : $"{issue.Test}: {issue.Text}";

    private static string Clip(string text) => text.Length <= IssueLength ? text : text[..(IssueLength - 3)] + "...";

    /// <summary>Why a test has no result to report: its document disables it, it changed since its last result, or no run has judged it.</summary>
    private string Unjudged(AssertionSetTest test)
        => test.Why ?? (test.AssertionRunId is { } report
            ? string.Create(CultureInfo.InvariantCulture, $"Report {report} holds its result, whose detail could not be read.")
            : $"No run has checked it in {Partition ?? "this partition"} yet.");

    /// <summary>The tests of one standing in Markdown: disabled ones under each reason, the others a line per flow.</summary>
    private void WriteMarkdownGroups(StringBuilder md, string title, string column, IReadOnlyList<AssertionSetFlow> ordered, string standing)
    {
        var groups = Grouped(ordered, standing);
        if (groups.Count == 0)
        {
            return;
        }

        md.AppendLine().Append("## ").AppendLine(title).AppendLine();
        if (standing == SetStandings.Disabled)
        {
            md.AppendLine("| Why it is disabled | Assertions |");
            md.AppendLine("| --- | --- |");
            foreach (var (reason, rows) in groups)
            {
                md.Append(CultureInfo.InvariantCulture,
                    $"| {AssertionReport.Cell(reason)} | {AssertionReport.Cell(string.Join(", ", rows.Select(r => $"{r.Test.Test} ({r.Flow})")))} |").AppendLine();
            }

            return;
        }

        md.Append("| Flow | Assertions | ").Append(column).AppendLine(" |");
        md.AppendLine("| --- | --- | --- |");
        foreach (var (flow, rows) in groups)
        {
            md.Append(CultureInfo.InvariantCulture,
                $"| {AssertionReport.Cell(flow)} | {AssertionReport.Cell(string.Join(", ", rows.Select(r => r.Test.Test)))} | {AssertionReport.Cell(GroupText(standing, rows.Select(r => r.Test).ToList()))} |").AppendLine();
        }
    }

    private JsonObject Counts() => new()
    {
        ["tests"] = Tests.Count(),
        ["passed"] = Count(TestOutcomes.Passed),
        ["failed"] = Count(TestOutcomes.Failed),
        ["warned"] = Count(TestOutcomes.Warned),
        ["errored"] = Count(TestOutcomes.Errored),
        ["disabled"] = Count(SetStandings.Disabled),
        ["notRun"] = Count(SetStandings.NotRun),
    };

    private string Rate() => PassRate is { } rate ? rate.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "n/a";

    private static string Counted(int count, string noun) => string.Create(CultureInfo.InvariantCulture, $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}");

    /// <summary>An instant as a reader reads it: <c>3 November 2026, 14:05 UTC</c>.</summary>
    private static string Day(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("d MMMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static void WriteCounts(XmlWriter xml, IReadOnlyCollection<AssertionSetTest> tests)
    {
        xml.WriteAttributeString("tests", tests.Count.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("failures", tests.Count(t => t.Standing == TestOutcomes.Failed).ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("errors", tests.Count(t => t.Standing == TestOutcomes.Errored).ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("skipped", tests.Count(t => t.Standing is SetStandings.Disabled or SetStandings.NotRun or TestOutcomes.Skipped).ToString(CultureInfo.InvariantCulture));
    }


}
