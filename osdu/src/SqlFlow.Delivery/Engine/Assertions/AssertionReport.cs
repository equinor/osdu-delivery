using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>The formats a report is rendered in.</summary>
public enum ReportFormat
{
    Json,
    Markdown,
    Html,
    JUnit,
}

/// <summary>
/// The full report of one run of an assertion flow (osdu/docs/reference/flow/assertion.md, The report): the run, and
/// every test's result as the ledger keeps it, rendered as JSON for tools, Markdown for a pull request or a wiki, JUnit
/// XML for a CI server, or a self-contained HTML page to read, print or send. The API and the CLI render through this
/// one class, so a report reads the same wherever it is taken from.
/// </summary>
public sealed record AssertionReport(AssertionRunState Run, IReadOnlyList<TestResult> Results, DateTime GeneratedUtc)
{
    /// <summary>The report's format by the name the API and the CLI take (json, md, markdown, html, junit, xml).</summary>
    public static ReportFormat? FormatOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "json" => ReportFormat.Json,
        "md" or "markdown" => ReportFormat.Markdown,
        "html" => ReportFormat.Html,
        "junit" or "xml" => ReportFormat.JUnit,
        _ => null,
    };

    /// <summary>The media type and file extension of a format.</summary>
    public static (string MediaType, string Extension) Describe(ReportFormat format) => format switch
    {
        ReportFormat.Markdown => ("text/markdown; charset=utf-8", "md"),
        ReportFormat.Html => ("text/html; charset=utf-8", "html"),
        ReportFormat.JUnit => ("application/xml; charset=utf-8", "xml"),
        _ => ("application/json; charset=utf-8", "json"),
    };

    /// <summary>The file name a report downloads as: the flow, the partition and the run.</summary>
    public string FileName(ReportFormat format)
    {
        var safe = new string(Run.FlowName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return string.Create(CultureInfo.InvariantCulture, $"{safe}-{Run.Partition ?? "partition"}-report-{Run.AssertionRunId}.{Describe(format).Extension}");
    }

    public string Render(ReportFormat format) => format switch
    {
        ReportFormat.Markdown => ToMarkdown(),
        ReportFormat.Html => ToHtml(),
        ReportFormat.JUnit => ToJUnit(),
        _ => ToJson(),
    };

    /// <summary>The tests in the order a report lists them: by kind, then the ones that did not pass first, then by name.</summary>
    public IReadOnlyList<IGrouping<string, TestResult>> ByKind() => Results
        .OrderBy(r => Rank(r.Outcome))
        .ThenBy(r => r.Test, StringComparer.OrdinalIgnoreCase)
        .GroupBy(r => r.Kind, StringComparer.OrdinalIgnoreCase)
        .OrderBy(g => g.Min(r => Rank(r.Outcome)))
        .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The share of the evaluated tests that passed, in percent; null when none was evaluated.</summary>
    public double? PassRate
    {
        get
        {
            var evaluated = Run.Counts.Tests - Run.Counts.Skipped;
            return evaluated <= 0 ? null : Math.Round(100.0 * Run.Counts.Passed / evaluated, 1);
        }
    }

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["flow"] = Run.FlowName,
            ["partition"] = Run.Partition,
            ["assertionRunId"] = Run.AssertionRunId,
            ["runId"] = Run.RunId?.ToString("D"),
            ["actor"] = Run.Actor,
            ["status"] = Run.Status,
            ["startedUtc"] = Run.StartedUtc,
            ["completedUtc"] = Run.CompletedUtc,
            ["error"] = Run.Error,
            ["selection"] = Run.Selection is null ? null : JsonNode.Parse(Run.Selection),
            ["counts"] = new JsonObject
            {
                ["tests"] = Run.Counts.Tests,
                ["passed"] = Run.Counts.Passed,
                ["failed"] = Run.Counts.Failed,
                ["warned"] = Run.Counts.Warned,
                ["errored"] = Run.Counts.Errored,
                ["skipped"] = Run.Counts.Skipped,
            },
            ["passRate"] = PassRate,
            ["generatedUtc"] = GeneratedUtc,
            ["results"] = JsonSerializer.SerializeToNode(Results, TestResults.Json),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public string ToMarkdown()
    {
        var md = new StringBuilder();
        md.Append("# ").Append(Run.FlowName).Append(" in ").Append(Run.Partition ?? "its partition").AppendLine();
        md.AppendLine();
        md.Append(CultureInfo.InvariantCulture, $"Report {Run.AssertionRunId}, **{Run.Status}**. Started {Stamp(Run.StartedUtc)}")
            .Append(Run.CompletedUtc is { } done ? $", completed {Stamp(done)}" : ", still running")
            .Append(CultureInfo.InvariantCulture, $", by {Cell(Run.Actor)}.").AppendLine();
        md.AppendLine();
        md.AppendLine("| Tests | Passed | Failed | Warned | Errored | Skipped | Pass rate |");
        md.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        md.Append(CultureInfo.InvariantCulture,
            $"| {Run.Counts.Tests} | {Run.Counts.Passed} | {Run.Counts.Failed} | {Run.Counts.Warned} | {Run.Counts.Errored} | {Run.Counts.Skipped} | {(PassRate is { } rate ? rate.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "n/a")} |")
            .AppendLine();
        if (Run.Error is { } error)
        {
            md.AppendLine().Append("> ").AppendLine(Cell(error));
        }

        foreach (var group in ByKind())
        {
            md.AppendLine().Append("## ").AppendLine(group.Key);
            foreach (var result in group)
            {
                WriteResultMarkdown(md, result, $"{result.Test}: {result.Outcome.ToUpperInvariant()}");
            }
        }

        md.AppendLine().Append(CultureInfo.InvariantCulture, $"Generated {Stamp(GeneratedUtc)} by OSDU Delivery.").AppendLine();
        return md.ToString();
    }

    /// <summary>
    /// The report as JUnit XML, which CI servers read: a suite per kind, a case per test. A failed test is a failure, an
    /// errored one an error, a skipped one skipped; a warned test passes, with its warnings in its output.
    /// </summary>
    public string ToJUnit()
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using var buffer = new StringWriterUtf8();
        using (var xml = XmlWriter.Create(buffer, settings))
        {
            xml.WriteStartElement("testsuites");
            xml.WriteAttributeString("name", $"{Run.FlowName}@{Run.Partition}");
            WriteCounts(xml, Results);
            xml.WriteAttributeString("time", Seconds(Results.Sum(r => r.DurationMs)));
            xml.WriteAttributeString("timestamp", Stamp(Run.StartedUtc));
            foreach (var group in ByKind())
            {
                xml.WriteStartElement("testsuite");
                xml.WriteAttributeString("name", group.Key);
                WriteCounts(xml, group.ToList());
                xml.WriteAttributeString("time", Seconds(group.Sum(r => r.DurationMs)));
                xml.WriteStartElement("properties");
                foreach (var (name, value) in new[]
                         {
                             ("flow", Run.FlowName), ("partition", Run.Partition ?? string.Empty),
                             ("report", Run.AssertionRunId.ToString(CultureInfo.InvariantCulture)), ("run", Run.RunId?.ToString("D") ?? string.Empty),
                         })
                {
                    xml.WriteStartElement("property");
                    xml.WriteAttributeString("name", name);
                    xml.WriteAttributeString("value", value);
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
                foreach (var result in group)
                {
                    WriteTestCase(xml, $"{Run.FlowName}.{group.Key}", result);
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
        }

        return buffer.ToString();
    }

    /// <summary>The report as one HTML page that needs nothing else: styles inline, light and dark, readable printed.</summary>
    public string ToHtml()
    {
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.Append("<title>").Append(E(Run.FlowName)).Append(" test report</title>").AppendLine();
        html.AppendLine("<style>").AppendLine(Styles).AppendLine("</style></head><body><main>");
        var rate = PassRate;
        var degrees = rate is { } r ? (int)Math.Round(r * 3.6) : 0;
        html.Append(CultureInfo.InvariantCulture,
            $"<header><div><p class=\"eyebrow\">Test report {Run.AssertionRunId} &middot; OSDU Delivery</p><h1>{E(Run.FlowName)}</h1>");
        html.Append(CultureInfo.InvariantCulture,
            $"<p class=\"sub\">Partition <b>{E(Run.Partition ?? "?")}</b> &middot; started {E(Stamp(Run.StartedUtc))}{(Run.CompletedUtc is { } done ? " &middot; completed " + E(Stamp(done)) : " &middot; still running")} &middot; by {E(Run.Actor)}</p></div>");
        html.Append(CultureInfo.InvariantCulture,
            $"<div class=\"ring\" style=\"--pass:{degrees}deg\"><span>{(rate is { } shown ? shown.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "n/a")}</span><small>passed</small></div></header>");
        html.AppendLine("<section class=\"tiles\">");
        foreach (var (label, count, tone) in new[]
                 {
                     ("Tests", Run.Counts.Tests, "neutral"), ("Passed", Run.Counts.Passed, "passed"), ("Failed", Run.Counts.Failed, "failed"),
                     ("Warned", Run.Counts.Warned, "warned"), ("Errored", Run.Counts.Errored, "errored"), ("Skipped", Run.Counts.Skipped, "skipped"),
                 })
        {
            html.Append(CultureInfo.InvariantCulture, $"<div class=\"tile {tone}\"><span>{count}</span><small>{label}</small></div>");
        }

        html.AppendLine("</section>");
        if (Run.Error is { } error)
        {
            html.Append("<p class=\"alert\">").Append(E(error)).AppendLine("</p>");
        }

        foreach (var group in ByKind())
        {
            html.Append("<section class=\"kind\"><h2>").Append(E(group.Key)).Append("</h2>");
            foreach (var result in group)
            {
                WriteResultHtml(html, result, null);
            }

            html.AppendLine("</section>");
        }

        html.Append("<footer>Generated ").Append(E(Stamp(GeneratedUtc))).AppendLine(" by OSDU Delivery, powered by SQLFlow.</footer>");
        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    /// <summary>
    /// One test's result in Markdown, under a heading of its own: what it is for, how many records it matched, why it could
    /// not be judged, then one line per check with its result, what it expected, what it found and why, and the failing
    /// records of each check that did not pass. A run's report and a set's write a result through this one method.
    /// </summary>
    internal static void WriteResultMarkdown(StringBuilder md, TestResult result, string heading)
    {
        md.AppendLine().Append("### ").AppendLine(heading);
        if (result.Description is { } description)
        {
            md.AppendLine().AppendLine(description);
        }

        md.AppendLine();
        md.Append(CultureInfo.InvariantCulture, $"Matched {Number(result.Matched)} record(s)")
            .Append(result.Sampled ? $", a sample of {Number(result.Evaluated)} read" : string.Empty)
            .Append(result.Query is { } query ? $"; query `{query.Replace("`", "'", StringComparison.Ordinal)}`" : string.Empty)
            .AppendLine(".");
        if (result.Error is { } testError)
        {
            md.AppendLine().Append("> ").AppendLine(Cell(testError));
        }

        foreach (var line in result.Problems.Concat(result.Notes))
        {
            md.Append("- ").AppendLine(Cell(line));
        }

        if (result.Assertions.Count == 0)
        {
            return;
        }

        md.AppendLine();
        md.AppendLine("| Check | Result | Expected | Found | Why |");
        md.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var a in result.Assertions)
        {
            var view = CheckView.Of(a);
            var expected = view.Qualifiers.Count == 0 ? view.Expected : $"{view.Expected} ({string.Join(", ", view.Qualifiers)})";
            md.Append(CultureInfo.InvariantCulture, $"| {Cell(a.Label)} | {CheckWord(Standing(a))} | {Cell(expected)} | {Cell(view.Found)} | {Cell(view.Why ?? string.Empty)} |").AppendLine();
        }

        foreach (var a in result.Assertions.Where(a => a.Examples.Count > 0 && a.Outcome != TestOutcomes.Passed))
        {
            md.AppendLine().Append("Records failing ").Append(Cell(a.Label)).AppendLine(":").AppendLine();
            md.AppendLine("| Record | Value | Why |");
            md.AppendLine("| --- | --- | --- |");
            foreach (var example in a.Examples)
            {
                md.Append(CultureInfo.InvariantCulture, $"| {Cell(example.Id ?? string.Empty)} | {Cell(example.Value ?? string.Empty)} | {Cell(example.Reason)} |").AppendLine();
            }
        }
    }

    /// <summary>
    /// One test's result as an HTML card: its name and result, what it is for, how many records it matched, why it could not
    /// be judged, then one line per check with its result, what it expected (the qualifiers under it), what it found and why,
    /// each fact once, the failing records a click away; the query, the template and the time it took are under "How it was
    /// checked". <paramref name="facts"/>, when given, is a line of encoded HTML under the heading (a set's report says which
    /// run the result is from).
    /// </summary>
    internal static void WriteResultHtml(StringBuilder html, TestResult result, string? facts)
    {
        html.Append(CultureInfo.InvariantCulture,
            $"<article class=\"test {result.Outcome}\"><div class=\"head\"><h3>{E(result.Test)}</h3><span class=\"badge {result.Outcome}\">{E(CheckWord(result.Outcome))}</span></div>");
        if (facts is not null)
        {
            html.Append("<p class=\"facts\">").Append(facts).Append("</p>");
        }

        if (result.Description is { } description)
        {
            html.Append("<p class=\"desc\">").Append(E(description)).Append("</p>");
        }

        html.Append(CultureInfo.InvariantCulture,
            $"<p class=\"facts\"><b>{Number(result.Matched)}</b> record(s) matched{(result.Sampled ? $", a sample of <b>{Number(result.Evaluated)}</b> read" : string.Empty)}</p>");
        if (result.Error is { } testError)
        {
            html.Append("<p class=\"alert\">").Append(E(testError)).Append("</p>");
        }

        foreach (var line in result.Problems.Concat(result.Notes))
        {
            html.Append("<p class=\"note\">").Append(E(line)).Append("</p>");
        }

        if (result.Assertions.Count > 0)
        {
            html.Append("<table class=\"checks\"><thead><tr><th>Check</th><th class=\"fit\">Result</th><th>Expected</th><th>Found</th><th>Why</th></tr></thead><tbody>");
            foreach (var a in result.Assertions)
            {
                var standing = Standing(a);
                var view = CheckView.Of(a);
                html.Append(CultureInfo.InvariantCulture,
                    $"<tr><td>{E(a.Label)}</td><td class=\"fit\"><span class=\"badge {standing}\">{E(CheckWord(standing))}</span></td><td>{E(view.Expected)}");
                if (view.Qualifiers.Count > 0)
                {
                    html.Append("<br><small>").Append(string.Join(" &middot; ", view.Qualifiers.Select(E))).Append("</small>");
                }

                html.Append("</td><td>").Append(E(view.Found)).Append("</td><td class=\"why\">").Append(E(view.Why ?? string.Empty)).Append("</td></tr>");
                if (a.Examples.Count > 0 && a.Outcome != TestOutcomes.Passed)
                {
                    html.Append(CultureInfo.InvariantCulture,
                        $"<tr class=\"examples\"><td colspan=\"5\"><details><summary>{a.Examples.Count} record(s) that fail it{(a.ExamplesTrimmed ? ", cut back" : string.Empty)}</summary><table><tbody>");
                    foreach (var example in a.Examples)
                    {
                        html.Append("<tr><td><code>").Append(E(example.Id ?? string.Empty)).Append("</code></td><td>").Append(E(example.Value ?? string.Empty))
                            .Append("</td><td>").Append(E(example.Reason)).Append("</td></tr>");
                    }

                    html.Append("</tbody></table></details></td></tr>");
                }
            }

            html.Append("</tbody></table>");
        }

        html.Append("<details class=\"how\"><summary>How it was checked</summary><p class=\"facts\">")
            .Append(E(result.Kind));
        if (result.Query is { } query)
        {
            html.Append(" &middot; query <code>").Append(E(query)).Append("</code>");
        }

        if (result.Template is { } template)
        {
            html.Append(" &middot; template <code>").Append(E(template)).Append("</code>");
        }

        html.Append(CultureInfo.InvariantCulture, $" &middot; read {Number(result.Evaluated)} &middot; {result.DurationMs} ms</p></details>");
        html.AppendLine("</article>");
    }

    /// <summary>A test's or a check's result as a reader reads it: "Warning" for warned, "Could not be checked" for errored.</summary>
    internal static string CheckWord(string standing) => standing switch
    {
        TestOutcomes.Passed => "Passed",
        TestOutcomes.Failed => "Failed",
        TestOutcomes.Warned => "Warning",
        TestOutcomes.Errored => "Could not be checked",
        "noted" => "Note",
        _ => "Not checked",
    };

    /// <summary>
    /// One test's result as a JUnit test case: a failed test is a failure, an errored one an error, a skipped one skipped; a
    /// warned test passes, with its warnings in its output.
    /// </summary>
    internal static void WriteTestCase(XmlWriter xml, string className, TestResult result)
    {
        xml.WriteStartElement("testcase");
        xml.WriteAttributeString("classname", className);
        xml.WriteAttributeString("name", result.Test);
        xml.WriteAttributeString("time", Seconds(result.DurationMs));
        var detail = Detail(result);
        switch (result.Outcome)
        {
            case TestOutcomes.Failed:
                xml.WriteStartElement("failure");
                xml.WriteAttributeString("message", First(result));
                xml.WriteAttributeString("type", "assertion");
                xml.WriteString(detail);
                xml.WriteEndElement();
                break;
            case TestOutcomes.Errored:
                xml.WriteStartElement("error");
                xml.WriteAttributeString("message", result.Error ?? First(result));
                xml.WriteAttributeString("type", "evaluation");
                xml.WriteString(detail);
                xml.WriteEndElement();
                break;
            case TestOutcomes.Skipped:
                xml.WriteStartElement("skipped");
                xml.WriteAttributeString("message", result.Error ?? "not run");
                xml.WriteEndElement();
                break;
            default:
                if (result.Outcome == TestOutcomes.Warned || result.Notes.Count > 0)
                {
                    xml.WriteElementString("system-out", detail);
                }

                break;
        }

        xml.WriteEndElement();
    }

    /// <summary>How an assertion stands in its test: one that does not hold weighs what its severity says (a warning warns, an info notes).</summary>
    internal static string Standing(AssertionOutcome assertion)
        => assertion.Outcome != TestOutcomes.Failed ? assertion.Outcome : assertion.Severity switch
        {
            "warning" => TestOutcomes.Warned,
            "info" => "noted",
            _ => TestOutcomes.Failed,
        };

    internal const string Styles = """
        :root { color-scheme:light dark; --bg:#f7f8fa; --card:#ffffff; --ink:#1b1f24; --muted:#5b6470; --line:#e3e6ea; --passed:#1f8f55; --failed:#c62f3b; --warned:#b7791f; --errored:#7a3fc2; --skipped:#8a929c; --neutral:#2c5fb3; --noted:#2c5fb3; }
        @media (prefers-color-scheme: dark) { :root { --bg:#111418; --card:#191d22; --ink:#e7eaee; --muted:#9aa3ad; --line:#2a3037; } }
        * { box-sizing:border-box; }
        body { margin:0; background:var(--bg); color:var(--ink); font:14px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width:1100px; margin:0 auto; padding:32px 16px 48px; }
        header { display:flex; justify-content:space-between; gap:24px; align-items:center; }
        h1 { margin:4px 0; font-size:26px; } h2 { font-size:15px; margin:32px 0 12px; color:var(--muted); font-family:ui-monospace, monospace; }
        h3 { margin:0; font-size:15px; } .eyebrow { margin:0; color:var(--muted); text-transform:uppercase; letter-spacing:.06em; font-size:11px; }
        .sub, .facts { color:var(--muted); margin:4px 0; } code { font-family:ui-monospace, monospace; font-size:12px; overflow-wrap:anywhere; }
        .ring { width:120px; height:120px; border-radius:50%; background:conic-gradient(var(--passed) var(--pass), var(--line) 0); display:flex; flex-direction:column; align-items:center; justify-content:center; flex:none; position:relative; }
        .ring::after { content:""; position:absolute; inset:14px; border-radius:50%; background:var(--bg); }
        .ring span, .ring small { position:relative; z-index:1; } .ring span { font-size:22px; font-weight:700; line-height:1.1; } .ring small { color:var(--muted); }
        .tiles { display:grid; grid-template-columns:repeat(auto-fit, minmax(120px, 1fr)); gap:10px; margin:24px 0; }
        .tile { background:var(--card); border:1px solid var(--line); border-top:3px solid var(--neutral); border-radius:10px; padding:12px; }
        .tile span { display:block; font-size:24px; font-weight:700; } .tile small { color:var(--muted); }
        .tile.passed { border-top-color:var(--passed); } .tile.failed { border-top-color:var(--failed); } .tile.warned { border-top-color:var(--warned); }
        .tile.errored { border-top-color:var(--errored); } .tile.skipped, .tile.disabled { border-top-color:var(--skipped); } .tile.notRun { border-top:3px dashed var(--skipped); }
        .test { background:var(--card); border:1px solid var(--line); border-left:4px solid var(--skipped); border-radius:10px; padding:14px 16px; margin:10px 0; break-inside:avoid; }
        .test.passed { border-left-color:var(--passed); } .test.failed { border-left-color:var(--failed); } .test.warned { border-left-color:var(--warned); } .test.errored { border-left-color:var(--errored); }
        .head { display:flex; justify-content:space-between; align-items:center; gap:12px; } .head h3 { min-width:0; overflow-wrap:anywhere; }
        .badge { display:inline-block; white-space:nowrap; font-size:11px; font-weight:600; padding:2px 8px; border-radius:999px; color:#fff; background:var(--skipped); text-transform:uppercase; letter-spacing:.04em; }
        .badge.passed { background:var(--passed); } .badge.failed { background:var(--failed); } .badge.warned { background:var(--warned); } .badge.errored { background:var(--errored); } .badge.noted { background:var(--noted); }
        .badge.notRun { background:transparent; color:var(--muted); border:1px dashed var(--skipped); } .test.notRun { border-left-style:dashed; }
        table { width:100%; border-collapse:collapse; margin-top:10px; font-size:13px; } th, td { text-align:left; padding:6px 8px; border-top:1px solid var(--line); vertical-align:top; overflow-wrap:anywhere; }
        th { color:var(--muted); font-weight:600; font-size:12px; } th.fit, td.fit { white-space:nowrap; } .examples td { padding:0 8px 8px; border-top:none; } details summary { cursor:pointer; color:var(--muted); }
        .alert { border:1px solid var(--failed); color:var(--failed); border-radius:8px; padding:8px 12px; } .note { color:var(--muted); margin:4px 0; font-size:13px; }
        .desc { margin:6px 0 0; } .checks td:nth-child(4) { min-width:6rem; } td.why { color:var(--muted); } td small { color:var(--muted); } .how { margin-top:10px; } .how summary { font-size:12px; }
        footer { margin-top:32px; color:var(--muted); font-size:12px; }
        @media print { body { background:#fff; } .test, .tile { break-inside:avoid; } details { display:block; } details > * { display:block; } }
        @media (max-width:640px) { header { flex-direction:column; align-items:flex-start; } table { display:block; overflow-x:auto; } th, td { overflow-wrap:break-word; } }
        """;

    internal static int Rank(string outcome) => outcome switch
    {
        TestOutcomes.Failed => 0,
        TestOutcomes.Errored => 1,
        TestOutcomes.Warned => 2,
        TestOutcomes.Passed => 3,
        _ => 4,
    };

    internal static void WriteCounts(XmlWriter xml, IReadOnlyCollection<TestResult> results)
    {
        xml.WriteAttributeString("tests", results.Count.ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("failures", results.Count(r => r.Outcome == TestOutcomes.Failed).ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("errors", results.Count(r => r.Outcome == TestOutcomes.Errored).ToString(CultureInfo.InvariantCulture));
        xml.WriteAttributeString("skipped", results.Count(r => r.Outcome == TestOutcomes.Skipped).ToString(CultureInfo.InvariantCulture));
    }

    private static string First(TestResult result)
    {
        var first = result.Assertions.FirstOrDefault(a => a.Outcome is TestOutcomes.Failed or TestOutcomes.Errored);
        return first is null ? result.Error ?? result.Outcome : $"{first.Label}: {first.Message ?? first.Actual}";
    }

    private static string Detail(TestResult result)
    {
        var text = new StringBuilder();
        foreach (var line in result.Problems.Concat(result.Notes))
        {
            text.AppendLine(line);
        }

        foreach (var a in result.Assertions.Where(a => a.Outcome != TestOutcomes.Passed))
        {
            text.Append(CultureInfo.InvariantCulture, $"[{a.Severity}] {a.Label}: {a.Outcome}; expected {a.Expected}; actual {a.Actual ?? "n/a"}").AppendLine();
            if (a.Message is { } message)
            {
                text.Append("  ").AppendLine(message);
            }

            foreach (var example in a.Examples.Take(10))
            {
                text.Append("  ").Append(example.Id ?? "-").Append(": ").Append(example.Value ?? string.Empty).Append(" (").Append(example.Reason).AppendLine(")");
            }
        }

        return text.ToString();
    }

    internal static string Number(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "n/a";

    internal static string Seconds(long milliseconds) => (milliseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);

    internal static string Stamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    internal static string E(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Text as a Markdown table cell holds it: pipes escaped, one line.</summary>
    internal static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>A string writer that says UTF-8, so the XML declaration does.</summary>
    internal sealed class StringWriterUtf8 : StringWriter
    {
        public StringWriterUtf8()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
