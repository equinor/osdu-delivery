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
    string Test, string Kind, string? Description, string Standing, string? Why, long? AssertionRunId, bool Changed, TestResult? Result);

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
/// disable with why, and the tests no run has judged yet. Its HTML is a document to put in front of people who have not seen
/// the flows: a verdict first, then the figures, the flows at a glance, what needs attention with what was expected and what
/// was found, and the rest. It renders as Markdown and JSON as well, and as JUnit XML for a CI server, whose cases a run's
/// report writes. The API and the CLI read and render a set through this one class.
/// </summary>
public sealed record AssertionSetReport(string Set, string? Partition, IReadOnlyList<AssertionSetFlow> Flows, DateTime GeneratedUtc)
{
    /// <summary>The longest line a flow's first issue is told in on the flows at a glance.</summary>
    private const int IssueLength = 220;

    /// <summary>The failing records a check lists in the HTML report; the rest are in its run's report.</summary>
    private const int ShownExamples = 10;

    /// <summary>The assertions a flow's line names in a list before saying how many more there are.</summary>
    private const int ShownNames = 12;

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
                .Select(t => TestOf(t, ofFlow is not null && ofFlow.TryGetValue(t.Name, out var hit) ? hit : null))
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

    public string Render(ReportFormat format) => format switch
    {
        ReportFormat.Markdown => ToMarkdown(),
        ReportFormat.Html => ToHtml(),
        ReportFormat.JUnit => ToJUnit(),
        _ => ToJson(),
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
                    ["kind"] = t.Kind,
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

    /// <summary>The report in Markdown, in the order the HTML tells it: the verdict, the figures, the flows, what needs attention, the rest.</summary>
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
    /// The report as one HTML page that needs nothing else, a dashboard to put in front of people who have not seen the
    /// flows: styles inline, light and dark, printing on A4. It opens on the set's verdict and its figures, then one line per
    /// flow, worst first, with where it stands and the first thing that is wrong in it; a click opens a flow on its
    /// assertions: each that failed, could not be checked or warned with what its checks expected and found and the records
    /// that fail them, and the rest one line each, a disabled one with the reason its document gives. Closed, the page is the
    /// overview; every status wears a glyph and a word, never colour alone.
    /// </summary>
    public string ToHtml()
    {
        var ordered = Ordered();
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>").Append(E(Set)).Append(" assertion report</title>").AppendLine();
        html.AppendLine("<style>").AppendLine(Styles).AppendLine("</style></head><body><main>");

        html.Append("<header class=\"top\"><p class=\"eyebrow\">Assertion report &middot; OSDU Delivery</p>")
            .Append("<h1>").Append(E(Set)).Append("</h1>")
            .Append(CultureInfo.InvariantCulture,
                $"<p class=\"meta\">Partition <b>{E(Partition ?? "?")}</b> &middot; {Counted(Flows.Count, "flow")} &middot; {Counted(Tests.Count(), "assertion")} &middot; as of {E(Day(GeneratedUtc))}</p></header>")
            .AppendLine();
        html.Append(CultureInfo.InvariantCulture, $"<section class=\"verdict {Status}\">{Icon(Status, "big")}<p>{E(Verdict())}</p></section>").AppendLine();

        WriteFigures(html);
        WriteFlows(html, ordered);
        WriteLegend(html);
        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    /// <summary>The figures: the pass rate, the count of each standing, and the whole split as one bar with its legend.</summary>
    private void WriteFigures(StringBuilder html)
    {
        var total = Tests.Count();
        html.Append("<section class=\"figures\"><div class=\"hero\">")
            .Append("<span class=\"label\">Pass rate</span>")
            .Append("<span class=\"value\">").Append(E(Rate())).Append("</span>")
            .Append(CultureInfo.InvariantCulture, $"<span class=\"caption\">{(Evaluated == 0 ? "no assertion checked yet" : $"{Count(TestOutcomes.Passed)} of the {Counted(Evaluated, "assertion")} checked pass")}</span>")
            .Append("</div><div class=\"tiles\">");
        foreach (var standing in Parts)
        {
            var count = Count(standing);
            html.Append(CultureInfo.InvariantCulture,
                $"<div class=\"tile{(count == 0 ? " zero" : string.Empty)}\"><span class=\"label\">{Icon(standing)}{E(SetStandings.Word(standing))}</span><span class=\"value\">{count}</span></div>");
        }

        html.Append("</div>");
        if (total > 0)
        {
            html.Append(CultureInfo.InvariantCulture, $"<div class=\"split\" role=\"img\" aria-label=\"{E(SplitText(Tests.ToList()))}\">");
            foreach (var standing in Parts.Where(s => Count(s) > 0))
            {
                var share = 100.0 * Count(standing) / total;
                html.Append(CultureInfo.InvariantCulture,
                    $"<span class=\"part {standing}\" style=\"flex-grow:{Count(standing)}\" title=\"{E(SetStandings.Word(standing))}: {Count(standing)} ({share:0}%)\">{(share >= 7 ? share.ToString("0", CultureInfo.InvariantCulture) + "%" : string.Empty)}</span>");
            }

            html.Append("</div><ul class=\"legend\">");
            foreach (var standing in Parts.Where(s => Count(s) > 0))
            {
                html.Append(CultureInfo.InvariantCulture, $"<li><span class=\"swatch {standing}\"></span>{E(SetStandings.Word(standing))}</li>");
            }

            html.Append("</ul>");
        }

        html.AppendLine("</section>");
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

    /// <summary>
    /// One row per flow, worst first, each a fold: closed, where the flow stands, what it is for, its share of passing
    /// assertions and the first thing that is wrong in it; open, its assertions, worst first.
    /// </summary>
    private void WriteFlows(StringBuilder html, IReadOnlyList<AssertionSetFlow> ordered)
    {
        html.Append("<section><h2>Flows</h2><p class=\"lead\">Worst first. Open a flow to see its assertions, what each check expected and found, and the records that fail it.</p>")
            .Append("<div class=\"flows\"><div class=\"fhead\"><span>Flow</span><span>Status</span><span>Assertions</span><span>Needs attention</span></div>");
        foreach (var flow in ordered)
        {
            var standing = FlowStanding(flow);
            html.Append(CultureInfo.InvariantCulture, $"<details class=\"flow {standing}\"><summary><span class=\"cell\"><span class=\"name\">{E(flow.Flow)}</span>");
            if (flow.Description is { } description)
            {
                html.Append("<span class=\"sub\">").Append(E(description)).Append("</span>");
            }

            html.Append(CultureInfo.InvariantCulture, $"</span><span class=\"cell\"><span class=\"chip\">{Icon(standing)}{E(FlowWord(flow))}</span></span><span class=\"cell\">");
            if (flow.Tests.Count > 0)
            {
                html.Append(CultureInfo.InvariantCulture, $"<span class=\"thin\" role=\"img\" aria-label=\"{E(SplitText(flow.Tests))}\">");
                foreach (var part in Parts)
                {
                    var count = flow.Tests.Count(t => t.Standing == part);
                    if (count > 0)
                    {
                        html.Append(CultureInfo.InvariantCulture, $"<span class=\"part {part}\" style=\"flex-grow:{count}\" title=\"{E(SetStandings.Word(part))}: {count}\"></span>");
                    }
                }

                html.Append("</span>");
            }

            html.Append("<span class=\"count\">").Append(E(FlowPassed(flow))).Append("</span></span>");
            if (FirstIssue(flow) is { } issue)
            {
                html.Append("<span class=\"cell issue\">");
                if (issue.Test is { } test)
                {
                    html.Append("<b>").Append(E(test)).Append("</b> ");
                }

                html.Append(E(issue.Text)).Append("</span>");
            }
            else
            {
                html.Append("<span class=\"cell issue none\">")
                    .Append(flow.Tests.Any(t => t.Standing == TestOutcomes.Passed) ? "Nothing: every assertion checked passes." : "Nothing checked yet.")
                    .Append("</span>");
            }

            html.Append("</summary><div class=\"body\">");
            if (flow.Problem is { } problem)
            {
                html.Append("<p class=\"alert\">").Append(E(problem)).Append("</p>");
            }

            foreach (var t in flow.Tests)
            {
                if (NeedsLook(t) && t.Result is { } result)
                {
                    AssertionReport.WriteResultHtml(html, result, string.Create(CultureInfo.InvariantCulture, $"checked {E(Day(result.CompletedUtc))} &middot; report {t.AssertionRunId}"));
                    continue;
                }

                var text = t.Standing == TestOutcomes.Passed && t.Result is { } passed
                    ? (passed.Assertions.Count == 1 ? "its check passes" : string.Create(CultureInfo.InvariantCulture, $"all {passed.Assertions.Count} checks pass"))
                    : Unjudged(t);
                html.Append(CultureInfo.InvariantCulture,
                    $"<div class=\"line\">{Icon(t.Standing)}<span class=\"word\">{E(SetStandings.Word(t.Standing))}</span><b>{E(t.Test)}</b><span>{E(text)}</span></div>");
            }

            html.AppendLine("</div></details>");
        }

        html.AppendLine("</div></section>");
    }

    private void WriteLegend(StringBuilder html)
    {
        html.Append("<footer><h2>How to read this report</h2><dl>");
        foreach (var (standing, meaning) in new[]
                 {
                     (TestOutcomes.Passed, "Every check of error or warning severity holds in the assertion's latest run."),
                     (TestOutcomes.Failed, "A check of error severity does not hold: OSDU holds something other than what the flow states."),
                     (TestOutcomes.Warned, "Only checks of warning severity do not hold, or the values a check expects need restating."),
                     (TestOutcomes.Errored, "The assertion could not find out: a query OSDU refused, a service that did not answer, or an assertion that does not fit the schema of its type."),
                     (SetStandings.Disabled, "Switched off in the flow's document, with the reason given there."),
                     (SetStandings.NotRun, "No run has checked the assertion as its flow states it now."),
                 })
        {
            html.Append(CultureInfo.InvariantCulture, $"<div><dt>{Icon(standing)}{E(SetStandings.Word(standing))}</dt><dd>{E(meaning)}</dd></div>");
        }

        html.Append(CultureInfo.InvariantCulture,
            $"</dl><p>Each assertion is reported by its latest result in partition {E(Partition ?? "?")}, of the assertion as its flow states it now; an earlier result is not shown. The pass rate is the share of the assertions checked that pass. Generated {E(Day(GeneratedUtc))} by OSDU Delivery, powered by SQLFlow.</p></footer>");
    }

    /// <summary>The standings a split is drawn in, in this order: what holds, then what does not, then what is not known.</summary>
    private static readonly string[] Parts =
        [TestOutcomes.Passed, TestOutcomes.Warned, TestOutcomes.Failed, TestOutcomes.Errored, SetStandings.NotRun, SetStandings.Disabled];

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

    /// <summary>How a check stands as its card shows it: one that does not hold weighs its severity; a skipped one was not judged.</summary>
    private static string CheckStanding(AssertionOutcome check) => AssertionReport.Standing(check) switch
    {
        "noted" => "noted",
        TestOutcomes.Skipped => SetStandings.Disabled,
        var standing => standing,
    };

    /// <summary>A split in words, as the bar's accessible name says it: "3 passed, 1 failed".</summary>
    private static string SplitText(IReadOnlyCollection<AssertionSetTest> tests)
        => string.Join(", ", Parts.Select(p => (p, n: tests.Count(t => t.Standing == p))).Where(x => x.n > 0)
            .Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.n} {SetStandings.Word(x.p).ToLowerInvariant()}")));

    /// <summary>A kind as a reader names it: its entity type and version, <c>WellLog 1.4.0</c>.</summary>
    private static string KindName(string kind)
    {
        var parts = kind.Split(':');
        if (parts.Length < 4)
        {
            return kind;
        }

        var entity = parts[2];
        var at = entity.IndexOf("--", StringComparison.Ordinal);
        return $"{(at >= 0 ? entity[(at + 2)..] : entity)} {parts[3]}";
    }

    /// <summary>Why a test has no result to report: its document disables it, it changed since its last result, or no run has judged it.</summary>
    private string Unjudged(AssertionSetTest test)
        => test.Why ?? (test.AssertionRunId is { } report
            ? string.Create(CultureInfo.InvariantCulture, $"Report {report} holds its result, whose detail could not be read.")
            : $"No run has checked it in {Partition ?? "this partition"} yet.");

    /// <summary>The tests of one standing in Markdown, grouped as the HTML groups them.</summary>
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

    private static string E(string text) => AssertionReport.E(text);

    /// <summary>A standing's glyph, drawn inline so the page needs nothing else; its word always stands beside it.</summary>
    private static string Icon(string standing, string size = "")
    {
        var shape = standing switch
        {
            TestOutcomes.Passed => "<circle cx=\"8\" cy=\"8\" r=\"6.5\"/><path d=\"M5.2 8.3l1.9 1.9 3.8-4.1\"/>",
            TestOutcomes.Failed => "<circle cx=\"8\" cy=\"8\" r=\"6.5\"/><path d=\"M5.7 5.7l4.6 4.6M10.3 5.7l-4.6 4.6\"/>",
            TestOutcomes.Warned => "<path d=\"M8 1.9l6.5 11.6h-13z\"/><path d=\"M8 6.4v3.1M8 11.6v.1\"/>",
            TestOutcomes.Errored => "<path d=\"M5.4 1.5h5.2l3.9 3.9v5.2l-3.9 3.9H5.4l-3.9-3.9V5.4z\"/><path d=\"M8 4.9v3.6M8 11v.1\"/>",
            SetStandings.Disabled => "<circle cx=\"8\" cy=\"8\" r=\"6.5\"/><path d=\"M3.5 12.5l9-9\"/>",
            "noted" => "<circle cx=\"8\" cy=\"8\" r=\"6.5\"/><path d=\"M8 7.3v4M8 4.9v.1\"/>",
            _ => "<circle cx=\"8\" cy=\"8\" r=\"6.5\" stroke-dasharray=\"2.4 2.1\"/>",
        };
        return $"<svg class=\"i {standing} {size}\" viewBox=\"0 0 16 16\" aria-hidden=\"true\">{shape}</svg>";
    }

    /// <summary>
    /// The page's styles: one surface and one ink per theme, the status colours only on glyphs, bars and accents (never as a
    /// fill behind text), figures in the same sans as the text, and an A4 print that keeps each card whole.
    /// </summary>
    private const string Styles = """
        :root { color-scheme:light dark; --bg:#f6f7f9; --card:#ffffff; --ink:#16191d; --ink2:#3d4550; --muted:#646d78; --line:#e2e5e9; --chip:#f0f2f4;
          --good:#0ca30c; --warning:#d99a0f; --serious:#e0703f; --critical:#d03b3b; --off:#9aa1a9; --none:#c9ced4; --info:#2f6fd0; }
        @media (prefers-color-scheme: dark) { :root { --bg:#111417; --card:#1a1e23; --ink:#eceff2; --ink2:#c3c9d0; --muted:#98a1ab; --line:#2b3138; --chip:#232930;
          --warning:#fab219; --serious:#ec835a; --off:#7d858e; --none:#48505a; --info:#6ea2f0; } }
        * { box-sizing:border-box; }
        body { margin:0; background:var(--bg); color:var(--ink); font:14px/1.55 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
        main { max-width:1120px; margin:0 auto; padding:40px 24px 56px; }
        h1 { margin:6px 0 4px; font-size:34px; line-height:1.15; letter-spacing:-.01em; overflow-wrap:anywhere; }
        h2 { display:flex; align-items:center; gap:8px; margin:44px 0 6px; font-size:19px; } h2 .n { color:var(--muted); font-weight:500; }
        h3 { margin:0; font-size:15px; overflow-wrap:anywhere; }
        .eyebrow { margin:0; color:var(--muted); text-transform:uppercase; letter-spacing:.08em; font-size:11.5px; font-weight:600; }
        .meta, .lead, .sub { color:var(--muted); } .meta { margin:0; } .lead { margin:0 0 14px; max-width:78ch; }
        .sub { display:block; font-size:12.5px; line-height:1.45; margin-top:2px; }
        code { font-family:ui-monospace, "Cascadia Mono", Consolas, monospace; font-size:12px; overflow-wrap:anywhere; }
        .i { width:16px; height:16px; flex:none; fill:none; stroke:var(--off); stroke-width:1.7; stroke-linecap:round; stroke-linejoin:round; vertical-align:-3px; }
        .i.big { width:30px; height:30px; } .i.passed { stroke:var(--good); } .i.failed { stroke:var(--critical); } .i.warned { stroke:var(--warning); }
        .i.errored { stroke:var(--serious); } .i.noted { stroke:var(--info); } .i.notRun { stroke:var(--muted); }
        .verdict { display:flex; align-items:center; gap:14px; margin:24px 0 0; padding:16px 20px; background:var(--card); border:1px solid var(--line); border-left:5px solid var(--off); border-radius:12px; }
        .verdict p { margin:0; font-size:18px; font-weight:600; line-height:1.4; }
        .verdict.passed { border-left-color:var(--good); } .verdict.failed { border-left-color:var(--critical); } .verdict.warned { border-left-color:var(--warning); } .verdict.errored { border-left-color:var(--serious); }
        .figures { display:grid; grid-template-columns:minmax(200px, 260px) 1fr; gap:14px; margin-top:16px; }
        .hero, .tile { background:var(--card); border:1px solid var(--line); border-radius:12px; }
        .hero { display:flex; flex-direction:column; justify-content:center; padding:18px 22px; }
        .hero .value { font-size:56px; font-weight:650; line-height:1.05; letter-spacing:-.02em; } .hero .caption { color:var(--muted); font-size:13px; }
        .label { display:flex; align-items:center; gap:6px; color:var(--ink2); font-size:13px; font-weight:500; }
        .tiles { display:grid; grid-template-columns:repeat(3, minmax(0, 1fr)); gap:10px; }
        .tile { display:flex; flex-direction:column; gap:4px; padding:12px 16px; } .tile .value { font-size:28px; font-weight:650; line-height:1.1; }
        .tile.zero .value, .tile.zero .label { color:var(--muted); } .tile.zero .i { opacity:.55; }
        .split { grid-column:1 / -1; display:flex; gap:2px; height:28px; } .split .part { min-width:4px; border-radius:4px; display:flex; align-items:center; padding:0 8px; font-size:12px; font-weight:600; color:#fff; }
        .part.passed { background:var(--good); } .part.failed { background:var(--critical); } .part.warned { background:var(--warning); color:#1b1b1b; } .part.errored { background:var(--serious); color:#1b1b1b; }
        .part.disabled { background:var(--off); } .part.notRun { background:transparent; border:1.5px dashed var(--none); color:var(--muted); }
        .legend { grid-column:1 / -1; display:flex; flex-wrap:wrap; gap:4px 18px; margin:0; padding:0; list-style:none; color:var(--ink2); font-size:12.5px; }
        .legend li { display:flex; align-items:center; gap:6px; } .swatch { width:10px; height:10px; border-radius:3px; }
        .swatch.passed { background:var(--good); } .swatch.failed { background:var(--critical); } .swatch.warned { background:var(--warning); } .swatch.errored { background:var(--serious); }
        .swatch.disabled { background:var(--off); } .swatch.notRun { border:1.5px dashed var(--none); }
        table { width:100%; border-collapse:collapse; } th, td { text-align:left; vertical-align:top; padding:10px 12px; border-top:1px solid var(--line); overflow-wrap:anywhere; }
        thead th { border-top:none; color:var(--muted); font-size:12px; font-weight:600; text-transform:uppercase; letter-spacing:.04em; }
        .name { font-weight:600; } td.fit, th.fit { white-space:nowrap; width:1%; }
        .flows { background:var(--card); border:1px solid var(--line); border-radius:12px; overflow:hidden; }
        .fhead, .flow > summary { display:grid; grid-template-columns:minmax(0, 31%) 11rem 9.5rem minmax(0, 1fr); gap:12px; padding:10px 14px 10px 34px; }
        .fhead { color:var(--muted); font-size:12px; font-weight:600; text-transform:uppercase; letter-spacing:.04em; }
        .flow { border-top:1px solid var(--line); } .flow > summary { position:relative; cursor:pointer; list-style:none; color:var(--ink); font-size:14px; } .flow > summary::-webkit-details-marker { display:none; }
        .flow > summary::before { content:""; position:absolute; left:14px; top:16px; width:6px; height:6px; border-right:1.6px solid var(--muted); border-bottom:1.6px solid var(--muted); transform:rotate(-45deg); transition:transform .15s; }
        .flow[open] > summary::before { transform:rotate(45deg); } .flow > summary:hover { background:var(--chip); } .flow[open] > summary { background:var(--chip); }
        .cell { min-width:0; } .issue { color:var(--ink2); font-size:13px; } .issue.none { color:var(--muted); } .issue b { color:var(--ink); font-weight:600; }
        .flow .body { padding:4px 14px 14px 34px; } .line { display:flex; align-items:baseline; gap:8px; padding:7px 0; border-top:1px solid var(--line); font-size:13px; }
        .line:first-child { border-top:none; } .line .i { align-self:center; } .line .word { color:var(--muted); min-width:7.5rem; } .line span:last-child { color:var(--muted); }
        .test { background:var(--card); border:1px solid var(--line); border-left:4px solid var(--off); border-radius:10px; padding:12px 14px; margin:10px 0; break-inside:avoid; }
        .test.failed { border-left-color:var(--critical); } .test.warned { border-left-color:var(--warning); } .test.errored { border-left-color:var(--serious); }
        .test .head { display:flex; justify-content:space-between; align-items:center; gap:12px; } .test h3 { font-size:14.5px; }
        .badge { display:inline-flex; align-items:center; gap:6px; white-space:nowrap; padding:2px 9px; border-radius:999px; background:var(--chip); color:var(--ink); font-size:12px; font-weight:500; }
        .badge::before { content:""; width:7px; height:7px; border-radius:50%; background:var(--off); }
        .badge.passed::before { background:var(--good); } .badge.failed::before { background:var(--critical); } .badge.warned::before { background:var(--warning); }
        .badge.errored::before { background:var(--serious); } .badge.noted::before { background:var(--info); }
        td.why, td small { color:var(--muted); } .how { margin-top:10px; } .how summary { font-size:12px; } .thin { display:flex; gap:2px; height:8px; margin:6px 0 6px; } .thin .part { min-width:3px; border-radius:4px; padding:0; }
        .count { color:var(--muted); font-size:12.5px; font-variant-numeric:tabular-nums; }
        .chip { display:inline-flex; align-items:center; gap:6px; padding:3px 10px 3px 8px; border-radius:999px; background:var(--chip); color:var(--ink); font-size:12.5px; font-weight:500; white-space:nowrap; }
        .desc { margin:8px 0 0; color:var(--ink2); } .facts { margin:6px 0 0; color:var(--muted); font-size:12.5px; }
        .alert { margin:10px 0 0; padding:10px 12px; border:1px solid var(--serious); border-radius:8px; color:var(--ink); }
        .note { margin:8px 0 0; color:var(--muted); font-size:13px; }
        .checks { margin-top:12px; font-size:13px; } .checks th:first-child, .checks td:first-child { width:28%; } .checks td:nth-child(3) { width:24%; } .checks td:nth-child(4) { min-width:6rem; } .checks td:nth-child(5) { width:30%; }
        .examples td { padding-top:0; border-top:none; } details summary { cursor:pointer; color:var(--info); font-size:12.5px; }
        details table { margin-top:6px; font-size:12.5px; } details th, details td { padding:6px 8px; }
        footer { margin-top:52px; padding-top:8px; border-top:1px solid var(--line); color:var(--muted); font-size:12.5px; }
        footer h2 { font-size:15px; margin:18px 0 10px; color:var(--ink); } footer dl { display:grid; grid-template-columns:repeat(2, minmax(0, 1fr)); gap:10px 28px; margin:0 0 16px; }
        footer dt { display:flex; align-items:center; gap:6px; color:var(--ink); font-weight:600; } footer dd { margin:2px 0 0 22px; }
        @media (max-width:760px) { .figures { grid-template-columns:1fr; } .tiles { grid-template-columns:repeat(2, minmax(0, 1fr)); } footer dl { grid-template-columns:1fr; }
          .fhead { display:none; } .flow > summary { grid-template-columns:1fr auto; } .flow .issue { grid-column:1 / -1; } }
        @media print { :root { color-scheme:light; --bg:#fff; --card:#fff; --ink:#111; --ink2:#333; --muted:#5d646d; --line:#d9dde2; --chip:#f1f3f5; }
          @page { size:A4; margin:14mm; } main { padding:0; max-width:none; } h2 { break-after:avoid; } tr, .test, .tile, .hero, .verdict { break-inside:avoid; }
          * { -webkit-print-color-adjust:exact; print-color-adjust:exact; } }
        """;
}
