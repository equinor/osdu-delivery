using System.Globalization;
using System.Net;
using System.Text;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Templates;

/// <summary>
/// The report as one HTML page that needs nothing else: styles inline and shared with the assertion report's page, light and
/// dark, readable printed, and inert (its content security policy allows no script and nothing fetched). It says what the
/// Markdown says, laid out to be read: what the move means first, each mapping as a card with its findings in tables, and
/// the changes no synced mapping uses folded away, because they weigh less.
/// </summary>
public sealed partial record TemplateMoveReport
{
    /// <summary>Marks where a value set as code starts inside a sentence, until the sentence is encoded (<see cref="Rich"/>).</summary>
    private const char CodeOpen = '\u0001';

    /// <summary>Marks where a value set as code ends.</summary>
    private const char CodeClose = '\u0002';

    /// <summary>Marks where words set in bold start inside a sentence, until the sentence is encoded.</summary>
    private const char StrongOpen = '\u0003';

    /// <summary>Marks where words set in bold end.</summary>
    private const char StrongClose = '\u0004';

    private const string HtmlStyles = """
        h2 { font-family:inherit; color:var(--ink); font-size:17px; margin:36px 0 8px; } h2 .count, h4 .count { color:var(--muted); font-weight:500; }
        h4 { margin:18px 0 6px; font-size:13px; } .lead { font-size:16px; margin:16px 0 4px; }
        .sides table { margin-top:16px; background:var(--card); border:1px solid var(--line); border-radius:10px; overflow:hidden; } .sides th:first-child { width:12rem; }
        .tile.quiet { border-top-color:var(--line); } .tile.quiet span { color:var(--muted); }
        .mapping { background:var(--card); border:1px solid var(--line); border-left:4px solid var(--skipped); border-radius:10px; padding:14px 16px; margin:12px 0; }
        .mapping.failed { border-left-color:var(--failed); } .mapping.warned { border-left-color:var(--warned); } .mapping.passed { border-left-color:var(--passed); } .mapping.errored { border-left-color:var(--errored); }
        .mapping h3 code { font-size:14px; } .mapping table { margin-top:4px; } h4.failed { color:var(--failed); } h4.warned { color:var(--warned); } h4.neutral { color:var(--neutral); }
        ul.changes { list-style:none; padding:0; margin:8px 0; } ul.changes > li { border-left:3px solid var(--line); padding:4px 10px; margin:6px 0; overflow-wrap:anywhere; }
        ul.changes > li.Breaking { border-left-color:var(--failed); } ul.changes > li.Additive { border-left-color:var(--neutral); } ul.changes > li.Wording { border-left-color:var(--skipped); }
        ul.changes ul { margin:4px 0 0; padding-left:18px; color:var(--muted); } .used { color:var(--muted); } li code, td code, p code { overflow-wrap:anywhere; }
        details.weight { margin-top:32px; } details.weight > summary { font-size:17px; font-weight:600; color:var(--ink); }
        details.weight.significant > summary { color:var(--warned); } details.weight.unused li { color:var(--muted); } ol.steps li { margin:4px 0; } .lead strong { font-weight:700; }
        table.findings { table-layout:fixed; } table.findings col.prop { width:30%; } table.findings col.line { width:3.5rem; } table.findings col.reads { width:34%; }
        code.path { overflow-wrap:normal; word-break:normal; }
        """;

    /// <summary>The report as one HTML page to read, print or send.</summary>
    public string ToHtml()
    {
        var to = SchemaVersion(To.Kind);
        var weighing = Weighing();
        var weighed = WeighedChanges();
        var html = new StringBuilder();
        html.AppendLine("<!doctype html>");
        html.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
        html.Append("<title>").Append(E(Title())).AppendLine("</title>");
        html.AppendLine("<style>").AppendLine(AssertionReport.Styles).AppendLine(HtmlStyles).AppendLine("</style></head><body><main>");
        html.Append("<header><div><p class=\"eyebrow\">Template version report &middot; OSDU Delivery</p><h1>").Append(E(Title())).Append("</h1>")
            .Append("<p class=\"sub\"><code>").Append(E(Stem(To.Kind))).AppendLine("</code></p></div></header>");
        html.Append("<p class=\"lead\">").Append(Rich(MappingVerdict(Mark, text => StrongOpen + Unmarked(text) + StrongClose))).AppendLine("</p>");
        WriteTiles(html, weighing, weighed);
        html.Append("<p class=\"note\">").Append(E(Verdict(markdown: false))).AppendLine("</p>");

        html.AppendLine("<section class=\"sides\"><table><thead><tr><th></th><th>From</th><th>To</th></tr></thead><tbody>");
        SideRow(html, "Kind", Coded(From.Kind), Coded(To.Kind));
        SideRow(html, "Release", E(From.Release), E(To.Release));
        SideRow(html, "Status in the release", E(From.Status ?? "not given"), E(To.Status ?? "not given"));
        SideRow(html, "Template version", Coded(From.TemplateVersion), Coded(To.TemplateVersion));
        SideRow(html, "Published file", Anchor(From.Path, From.WebUrl), Anchor(To.Path, To.WebUrl));
        html.AppendLine("</tbody></table></section>");

        WriteMappingCards(html, to);

        if (weighing.Count == 0)
        {
            foreach (var impact in Impacts)
            {
                var changes = weighed.Where(c => c.Change.Impact == impact).ToList();
                if (changes.Count > 0)
                {
                    html.Append(CultureInfo.InvariantCulture, $"<h2>{impact} changes <span class=\"count\">{changes.Count}</span></h2>");
                    html.Append("<p class=\"note\">").Append(E(Hint(impact))).AppendLine("</p>");
                    WriteChangeList(html, changes, detailed: true);
                }
            }
        }
        else
        {
            // The significant changes stand open; the minor ones open too, below them; what no mapping uses is folded away.
            foreach (var weight in Weights)
            {
                var group = weighed.Where(c => c.Weight == weight).ToList();
                if (group.Count == 0)
                {
                    continue;
                }

                var tone = weight == TemplateChangeWeight.Significant ? "significant" : weight == TemplateChangeWeight.Minor ? "minor" : "unused";
                html.Append(CultureInfo.InvariantCulture, $"<details class=\"weight {tone}\"{(weight == TemplateChangeWeight.Unused ? string.Empty : " open")}><summary>{E(WeightHeading(weight))} <span class=\"count\">{group.Count}</span></summary>");
                html.Append("<p class=\"note\">").Append(E(WeightHint(weight))).AppendLine("</p>");
                WriteChangeList(html, group.OrderByDescending(c => c.Change.Impact).ToList(), detailed: false, tagged: true);
                html.AppendLine("</details>");
            }
        }

        html.Append("<h2>Shared schemas</h2><p class=\"note\">").Append(E(SharedSchemasText())).AppendLine("</p>");
        if (SharedSchemas.Count > 0)
        {
            html.Append("<ul>");
            foreach (var file in SharedSchemas)
            {
                html.Append("<li>").Append(Coded(file.Name)).Append(": ")
                    .Append(file.FromPath is null ? "not referred to" : Anchor(file.FromPath, file.FromWebUrl))
                    .Append(" to ")
                    .Append(file.ToPath is null ? "not referred to" : Anchor(file.ToPath, file.ToWebUrl))
                    .Append("</li>");
            }

            html.AppendLine("</ul>");
        }

        if (Comparison.Changes.Any(c => c.Impact != TemplateChangeImpact.Wording) || weighing.Any(m => m.Check.Findings.Count > 0))
        {
            html.Append("<h2>Moving a mapping to ").Append(E(to)).Append("</h2><ol class=\"steps\">");
            foreach (var step in MovingSteps(Mark))
            {
                html.Append("<li>").Append(Rich(step)).Append("</li>");
            }

            html.AppendLine("</ol>");
        }

        html.Append("<footer>Generated ").Append(E(Stamp(GeneratedUtc))).AppendLine(" by OSDU Delivery, powered by SQLFlow.</footer>");
        html.AppendLine("</main></body></html>");
        return html.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>
    /// The figures that say what the move means, each quiet at zero: across the mappings weighed, what they have to change,
    /// what they write that is deprecated or changed without breaking them, and the changes none of them uses. With no mapping
    /// to weigh against, the comparison's own counts.
    /// </summary>
    private void WriteTiles(StringBuilder html, IReadOnlyList<TemplateMoveMapping> weighing, IReadOnlyList<TemplateMoveChange> weighed)
    {
        var mustChange = weighing.Sum(m => m.Check.Count(MappingFindingKind.MustChange));
        var tiles = weighing.Count == 0
            ? Impacts.Select(impact => (Label: $"{impact}", Count: Comparison.Count(impact), Tone: impact == TemplateChangeImpact.Breaking ? "failed" : "neutral")).ToList()
            :
            [
                ("Significant changes", weighed.Count(c => c.Weight == TemplateChangeWeight.Significant), mustChange > 0 ? "failed" : "warned"),
                ("To change in a mapping", mustChange, "failed"),
                ("Minor changes", weighed.Count(c => c.Weight == TemplateChangeWeight.Minor), "neutral"),
                // What no mapping uses has the least impact, so it is quiet whatever its count.
                ("Changes no mapping uses", weighed.Count(c => c.Weight == TemplateChangeWeight.Unused), "quiet"),
            ];
        html.Append("<section class=\"tiles\">");
        foreach (var (label, count, tone) in tiles)
        {
            html.Append(CultureInfo.InvariantCulture, $"<div class=\"tile {(count == 0 ? "quiet" : tone)}\"><span>{count}</span><small>{E(label)}</small></div>");
        }

        html.AppendLine("</section>");
    }

    /// <summary>Each synced mapping as a card: where it stands, where it is, what the move means for it, and its findings by kind.</summary>
    private void WriteMappingCards(StringBuilder html, string to)
    {
        if (Mappings.Count == 0)
        {
            return;
        }

        html.AppendLine("<h2>Synced mappings of this kind</h2>");
        html.Append("<p class=\"note\">Each is read against what its document writes, from the template it pins. A change to nothing it writes means nothing for it; the checks that read the cache are left to the delivery gate.</p>");
        foreach (var mapping in OrderedMappings())
        {
            var check = mapping.Check;
            var tone = check.State switch
            {
                MappingMoveState.NeedsChanges => "failed",
                MappingMoveState.Unchecked => "errored",
                MappingMoveState.Ready => check.Count(MappingFindingKind.Deprecated) > 0 ? "warned" : "passed",
                _ => "skipped",
            };
            html.Append(CultureInfo.InvariantCulture, $"<article class=\"mapping {tone}\"><div class=\"head\"><h3>{Coded(NameOf(mapping))}</h3>");
            html.Append(CultureInfo.InvariantCulture, $"<span class=\"badge {tone}\">{E(StateWords(check.State, to))}</span></div>");
            html.Append("<p class=\"facts\">").Append(Coded(mapping.Path)).Append(" in ").Append(E(mapping.Repository)).Append(", pins ").Append(Coded(mapping.PinnedKind));
            if (mapping.PinnedVersion is { Length: > 0 } version)
            {
                html.Append(" (template ").Append(Coded(version)).Append(')');
            }

            html.AppendLine("</p>");
            switch (check.State)
            {
                case MappingMoveState.Unchecked:
                    html.Append("<p class=\"alert\">It could not be checked: ").Append(E(mapping.Problem ?? "the reason was not recorded")).AppendLine("</p>");
                    break;
                case MappingMoveState.Ready or MappingMoveState.NeedsChanges:
                    html.Append("<p class=\"desc\">").Append(E(Gist(check, to))).AppendLine("</p>");
                    foreach (var kind in FindingKinds)
                    {
                        WriteFindings(html, check.Findings.Where(f => f.Kind == kind).ToList(), kind, to);
                    }

                    break;
            }

            html.AppendLine("</article>");
        }
    }

    private static void WriteFindings(StringBuilder html, List<MappingMoveFinding> findings, MappingFindingKind kind, string to)
    {
        if (findings.Count == 0)
        {
            return;
        }

        var tone = kind switch
        {
            MappingFindingKind.MustChange => "failed",
            MappingFindingKind.Deprecated => "warned",
            MappingFindingKind.Changed => "neutral",
            _ => string.Empty,
        };
        html.Append(CultureInfo.InvariantCulture, $"<h4 class=\"{tone}\">{E(KindHeading(kind, to))} <span class=\"count\">{findings.Count}</span></h4>");
        if (kind == MappingFindingKind.MayFill)
        {
            html.Append("<ul>");
            findings.ForEach(f => html.Append("<li>").Append(PathCode(f.Path)).Append(": ").Append(Rich(FindingText(f, Mark, HtmlProse))).Append("</li>"));
            html.AppendLine("</ul>");
            return;
        }

        html.Append("<table class=\"findings\"><colgroup><col class=\"prop\"><col class=\"line\"><col class=\"reads\"><col></colgroup>")
            .Append("<thead><tr><th>Property</th><th>Line</th><th>The mapping writes it from</th><th>")
            .Append(kind == MappingFindingKind.Deprecated ? "What to use instead" : "What changed").Append("</th></tr></thead><tbody>");
        foreach (var finding in findings)
        {
            html.Append("<tr><td>").Append(PathCode(finding.Path)).Append("</td><td>")
                .Append(finding.Write?.Line is { } line ? line.ToString(CultureInfo.InvariantCulture) : string.Empty)
                .Append("</td><td>").Append(finding.Write is { } write ? Coded(write.Reads) : "<small>what it writes inside it</small>")
                .Append("</td><td>").Append(Rich(FindingText(finding, Mark, HtmlProse))).Append("</td></tr>");
        }

        html.AppendLine("</tbody></table>");
    }

    /// <summary>Changes as a list, each with what it asks of a mapping and the mappings that write it; detailed, each changed field on a line of its own.</summary>
    private static void WriteChangeList(StringBuilder html, List<TemplateMoveChange> changes, bool detailed, bool tagged = false)
    {
        html.Append("<ul class=\"changes\">");
        foreach (var entry in changes)
        {
            var change = entry.Change;
            html.Append(CultureInfo.InvariantCulture, $"<li class=\"{change.Impact}\">").Append(PathCode(change.Path)).Append(": ")
                .Append(Rich(TemplateChangeAdvice.Summary(change, Mark)))
                .Append(tagged ? $" <span class=\"used\">({E(ImpactTag(change.Impact))})</span>" : string.Empty).Append('.');
            if (TemplateChangeAdvice.Action(change) is { } action)
            {
                html.Append(' ').Append(E(action));
            }

            if (entry.Used)
            {
                html.Append(" <span class=\"used\">Used by ").Append(string.Join(", ", entry.UsedBy.Select(Coded))).Append(".</span>");
            }

            if (detailed && TemplateChangeAdvice.Action(change) is null)
            {
                html.Append("<ul>");
                foreach (var field in change.Fields.OrderByDescending(f => f.Impact))
                {
                    html.Append("<li>").Append(Rich(TemplateChangeAdvice.Note(field, Mark)));
                    if (TemplateChangeAdvice.Deprecates(field) && TemplateDeprecation.Guidance(field.After) is { } guidance)
                    {
                        html.Append(" It says: ").Append(Rich(HtmlProse(guidance)));
                    }

                    html.Append("</li>");
                }

                html.Append("</ul>");
            }

            html.Append("</li>");
        }

        html.AppendLine("</ul>");
    }

    private static void SideRow(StringBuilder html, string label, string from, string to)
        => html.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(from).Append("</td><td>").Append(to).AppendLine("</td></tr>");

    private static string Anchor(string text, Uri? url)
        => url is null ? Coded(text) : $"<a href=\"{E(url.AbsoluteUri)}\">{E(text)}</a>";

    /// <summary>Text encoded for HTML, without the control characters a page cannot hold (a tab and a line break stay).</summary>
    private static string E(string text) => WebUtility.HtmlEncode(Clean(text));

    /// <summary>Text without control characters other than a tab and a line break, which also takes out every mark <see cref="Rich"/> reads.</summary>
    private static string Clean(string text) => text.Any(IsDropped) ? new string(text.Where(c => !IsDropped(c)).ToArray()) : text;

    /// <summary>Whether a character is a control character a page does not keep: any but a tab and a line break.</summary>
    private static bool IsDropped(char c) => char.IsControl(c) && c is not ('\t' or '\n' or '\r');

    /// <summary>A value as inline code, encoded.</summary>
    private static string Coded(string value) => "<code>" + E(OneLine(value)) + "</code>";

    /// <summary>A template path as code that wraps only after a dot, so a long one breaks between its names and never inside one.</summary>
    private static string PathCode(string path) => "<code class=\"path\">" + E(OneLine(path)).Replace(".", ".<wbr>", StringComparison.Ordinal) + "</code>";

    /// <summary>A value marked to be set as code once the sentence it is written into is encoded.</summary>
    private static string Mark(string value) => CodeOpen + Unmarked(value) + CodeClose;

    /// <summary>The gate's or a schema's words, ready for <see cref="Rich"/>: the spans a schema sets in backticks marked as code.</summary>
    private static string HtmlProse(string value) => Spans(value, Mark, Unmarked);

    /// <summary>A sentence encoded for HTML, with the values <see cref="Mark"/> marked set as code.</summary>
    private static string Rich(string sentence) => WebUtility.HtmlEncode(sentence)
        .Replace(CodeOpen.ToString(), "<code>", StringComparison.Ordinal)
        .Replace(CodeClose.ToString(), "</code>", StringComparison.Ordinal)
        .Replace(StrongOpen.ToString(), "<strong>", StringComparison.Ordinal)
        .Replace(StrongClose.ToString(), "</strong>", StringComparison.Ordinal);

    /// <summary>A value without the marks <see cref="Rich"/> reads, on one line, so no value can open or close code of its own.</summary>
    private static string Unmarked(string value) => Clean(OneLine(value));
}
