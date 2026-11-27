using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Templates;

/// <summary>The formats a report on moving to another version of a kind is rendered in.</summary>
public enum TemplateMoveReportFormat
{
    Json,
    Markdown,
}

/// <summary>One side of a move: a version of a kind as a release of the OSDU data definitions publishes it.</summary>
/// <param name="Kind">The kind, with its schema version.</param>
/// <param name="Release">The release of the data definitions it is read from.</param>
/// <param name="Path">Its schema file in the release.</param>
/// <param name="WebUrl">That file on community.opengroup.org.</param>
/// <param name="Status">What the release says of it (<c>PUBLISHED</c>, <c>DEVELOPMENT</c>); null when it does not say.</param>
/// <param name="TemplateVersion">The content version of the template its file holds once bundled.</param>
public sealed record TemplateMoveSide(string Kind, string Release, string Path, Uri WebUrl, string? Status, string TemplateVersion);

/// <summary>A shared schema the two versions refer to whose published text differs: its name without the version, and its file on each side (null where a version does not refer to it).</summary>
public sealed record TemplateMoveSharedSchema(string Name, string? FromPath, string? ToPath, Uri? FromWebUrl, Uri? ToWebUrl);

/// <summary>A mapping of a synced repository that fills the kind, set against the newer version (<see cref="MappingMoves"/>).</summary>
/// <param name="Repository">The repository it is synced from.</param>
/// <param name="Reference">The mapping, <c>name@version</c>.</param>
/// <param name="Path">Its document in the repository.</param>
/// <param name="PinnedKind">The kind, with its schema version, of the template it pins.</param>
/// <param name="PinnedVersion">The content version of that template; null when it is not known.</param>
/// <param name="Check">Where it stands, and what it has to change.</param>
/// <param name="Problem">Why it could not be checked, for <see cref="MappingMoveState.Unchecked"/>.</param>
public sealed record TemplateMoveMapping(
    string Repository, string Reference, string Path, string PinnedKind, string? PinnedVersion, MappingMoveCheck Check, string? Problem = null);

/// <summary>
/// The report on moving from one version of a kind to another (osdu/docs/reference/concepts/template-versions.md, The
/// report): which version is which, the verdict, what each synced mapping of the kind has to change, every breaking,
/// additive and wording change with what it asks of a mapping, the shared schemas that differ, and how to move. Rendered as
/// Markdown to paste into a ticket, a pull request or a wiki, or as JSON for a tool. The API renders through this one class,
/// so the report reads the same wherever it is taken from.
/// </summary>
public sealed record TemplateMoveReport(
    TemplateMoveSide From,
    TemplateMoveSide To,
    bool SameFile,
    bool OnlyIdentifiersDiffer,
    TemplateComparison Comparison,
    int SameSharedSchemas,
    IReadOnlyList<TemplateMoveSharedSchema> SharedSchemas,
    IReadOnlyList<TemplateMoveMapping> Mappings,
    DateTime GeneratedUtc)
{
    private static readonly TemplateChangeImpact[] Impacts = [TemplateChangeImpact.Breaking, TemplateChangeImpact.Additive, TemplateChangeImpact.Wording];

    /// <summary>Indented, and on line feeds whatever the host, so a report is the same file wherever it is rendered.</summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };

    /// <summary>The report's format by the name the API takes (json, md, markdown); null for any other.</summary>
    public static TemplateMoveReportFormat? FormatOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "json" => TemplateMoveReportFormat.Json,
        "md" or "markdown" => TemplateMoveReportFormat.Markdown,
        _ => null,
    };

    /// <summary>The media type and file extension of a format.</summary>
    public static (string MediaType, string Extension) Describe(TemplateMoveReportFormat format) => format == TemplateMoveReportFormat.Markdown
        ? ("text/markdown; charset=utf-8", "md")
        : ("application/json; charset=utf-8", "json");

    /// <summary>The file name a report downloads as: the entity, and each side's schema version and release.</summary>
    public string FileName(TemplateMoveReportFormat format)
    {
        var name = string.Join('-', EntityName(To.Kind), SchemaVersion(From.Kind), From.Release, "to", SchemaVersion(To.Kind), To.Release, "report");
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return $"{safe}.{Describe(format).Extension}";
    }

    public string Render(TemplateMoveReportFormat format) => format == TemplateMoveReportFormat.Markdown ? ToMarkdown() : ToJson();

    /// <summary>The synced mappings in the order the report lists them: what needs changes first, then by repository and name.</summary>
    public IReadOnlyList<TemplateMoveMapping> OrderedMappings() => Mappings
        .OrderBy(m => m.Check.State)
        .ThenBy(m => m.Repository, StringComparer.OrdinalIgnoreCase)
        .ThenBy(m => m.Reference, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string ToJson()
    {
        var root = new JsonObject
        {
            ["kind"] = Stem(To.Kind),
            ["from"] = Side(From),
            ["to"] = Side(To),
            ["sameFile"] = SameFile,
            ["onlyIdentifiersDiffer"] = OnlyIdentifiersDiffer,
            ["sameTemplate"] = string.Equals(From.TemplateVersion, To.TemplateVersion, StringComparison.Ordinal),
            ["counts"] = new JsonObject
            {
                ["breaking"] = Comparison.Count(TemplateChangeImpact.Breaking),
                ["additive"] = Comparison.Count(TemplateChangeImpact.Additive),
                ["wording"] = Comparison.Count(TemplateChangeImpact.Wording),
                ["unchanged"] = Comparison.Unchanged,
            },
            ["mappings"] = new JsonArray(OrderedMappings().Select(mapping => (JsonNode)new JsonObject
            {
                ["repository"] = mapping.Repository,
                ["mapping"] = mapping.Reference,
                ["path"] = mapping.Path,
                ["pinnedKind"] = mapping.PinnedKind,
                ["pinnedVersion"] = mapping.PinnedVersion,
                ["state"] = mapping.Check.State.ToString(),
                ["problem"] = mapping.Problem,
                ["findings"] = new JsonArray(mapping.Check.Findings.Select(finding => (JsonNode)new JsonObject
                {
                    ["path"] = finding.Path,
                    ["change"] = finding.Change?.Change.ToString(),
                    ["fields"] = Fields(finding.Fields),
                    ["requirement"] = finding.Requirement,
                    ["note"] = FindingText(finding, markdown: false),
                }).ToArray()),
            }).ToArray()),
            ["changes"] = new JsonArray(Comparison.Changes.Select(change => (JsonNode)new JsonObject
            {
                ["path"] = change.Path,
                ["change"] = change.Change.ToString(),
                ["impact"] = change.Impact.ToString(),
                ["role"] = change.Role.ToString(),
                ["summary"] = TemplateChangeAdvice.Summary(change, Plain),
                ["action"] = TemplateChangeAdvice.Action(change),
                ["fields"] = Fields(change.Fields),
            }).ToArray()),
            ["sharedSchemas"] = new JsonObject
            {
                ["same"] = SameSharedSchemas,
                ["differ"] = new JsonArray(SharedSchemas.Select(file => (JsonNode)new JsonObject
                {
                    ["name"] = file.Name,
                    ["fromPath"] = file.FromPath,
                    ["toPath"] = file.ToPath,
                    ["fromWebUrl"] = file.FromWebUrl?.AbsoluteUri,
                    ["toWebUrl"] = file.ToWebUrl?.AbsoluteUri,
                }).ToArray()),
            },
            ["generatedUtc"] = Stamp(GeneratedUtc),
        };
        return root.ToJsonString(Indented);

        static JsonObject Side(TemplateMoveSide side) => new()
        {
            ["kind"] = side.Kind,
            ["release"] = side.Release,
            ["path"] = side.Path,
            ["webUrl"] = side.WebUrl.AbsoluteUri,
            ["status"] = side.Status,
            ["templateVersion"] = side.TemplateVersion,
        };

        static JsonArray Fields(IReadOnlyList<TemplateFieldChange> fields) => new(fields.Select(field => (JsonNode)new JsonObject
        {
            ["field"] = field.Field,
            ["before"] = field.Before,
            ["after"] = field.After,
            ["impact"] = field.Impact.ToString(),
            ["note"] = TemplateChangeAdvice.Note(field, Plain),
        }).ToArray());
    }

    public string ToMarkdown()
    {
        var md = new StringBuilder();
        var from = SchemaVersion(From.Kind);
        var to = SchemaVersion(To.Kind);
        md.Append("# ").AppendLine(Title());
        md.AppendLine();
        md.AppendLine("| | From | To |");
        md.AppendLine("| --- | --- | --- |");
        Row(md, "Kind", Code(From.Kind), Code(To.Kind));
        Row(md, "Release", Text(From.Release), Text(To.Release));
        Row(md, "Status in the release", Text(From.Status ?? "not given"), Text(To.Status ?? "not given"));
        Row(md, "Template version", Code(From.TemplateVersion), Code(To.TemplateVersion));
        Row(md, "Published file", Link(From.Path, From.WebUrl), Link(To.Path, To.WebUrl));
        md.AppendLine();
        md.AppendLine(Verdict(from, to));

        WriteMappings(md, to);

        foreach (var impact in Impacts)
        {
            var changes = Comparison.Changes.Where(c => c.Impact == impact).ToList();
            if (changes.Count == 0)
            {
                continue;
            }

            md.AppendLine().Append(CultureInfo.InvariantCulture, $"## {impact} changes ({changes.Count})").AppendLine();
            md.AppendLine().AppendLine(Hint(impact)).AppendLine();
            foreach (var change in changes)
            {
                WriteChange(md, change);
            }
        }

        md.AppendLine().AppendLine("## Shared schemas").AppendLine();
        if (SharedSchemas.Count == 0)
        {
            md.Append(CultureInfo.InvariantCulture, $"Every shared schema the kind refers to is the same in both ({SameSharedSchemas}).").AppendLine();
        }
        else
        {
            md.Append(CultureInfo.InvariantCulture,
                $"{Counted(SharedSchemas.Count, "shared schema")} the kind refers to {(SharedSchemas.Count == 1 ? "differs" : "differ")}; {SameSharedSchemas} {(SameSharedSchemas == 1 ? "is" : "are")} the same. A kind's own file can be the same in two releases while a schema it refers to changed, and that is where its template's changes then come from.")
                .AppendLine().AppendLine();
            foreach (var file in SharedSchemas)
            {
                md.Append("- ").Append(Code(file.Name)).Append(": ")
                    .Append(file.FromPath is null ? "not referred to" : Link(file.FromPath, file.FromWebUrl))
                    .Append(" to ")
                    .Append(file.ToPath is null ? "not referred to" : Link(file.ToPath, file.ToWebUrl))
                    .AppendLine();
            }
        }

        if (Comparison.Changes.Any(c => c.Impact != TemplateChangeImpact.Wording))
        {
            md.AppendLine().Append("## Moving a mapping to ").AppendLine(to).AppendLine();
            md.Append("1. Download the ").Append(to).Append(" file of release ").Append(Text(To.Release))
                .Append(" into the template folder the mapping reads: Templates, Browse OSDU, Download, or ")
                .Append(Code($"sqlflow template capture --kind {To.Kind} --release {To.Release} --out <folder>/<file>.json"))
                .AppendLine(".");
            md.AppendLine("2. Fix what this report lists under the mapping. A mapping that names its template version is moved by naming the new one (give it a new mapping version when both are to run side by side); one that names the kind alone moves with the file.");
            md.Append("3. Check it with ").Append(Code("sqlflow check")).AppendLine(", which runs every check of the delivery gate, those that read the cache included.");
            md.Append("4. Run the flow's ").Append(Code("plan")).AppendLine(" operation to see which records would be delivered again, before delivering.");
        }

        md.AppendLine().Append("Generated ").Append(Stamp(GeneratedUtc)).AppendLine(" by OSDU Delivery.");

        // On line feeds whatever the host, so a report is the same file wherever it is rendered.
        return md.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>What the two sides are, the axis that differs first, so a report is never read as one move when it is another.</summary>
    private string Title()
    {
        var entity = EntityName(To.Kind);
        var from = SchemaVersion(From.Kind);
        var to = SchemaVersion(To.Kind);
        var sameRelease = string.Equals(From.Release, To.Release, StringComparison.Ordinal);
        var sameVersion = string.Equals(from, to, StringComparison.Ordinal);
        return (sameRelease, sameVersion) switch
        {
            (true, true) => $"{entity} {from} of release {From.Release}, set against itself",
            (true, false) => $"Moving {entity} from {from} to {to}",
            (false, true) => $"{entity} {from} as releases {From.Release} and {To.Release} publish it",
            _ => $"Moving {entity} from {from} ({From.Release}) to {to} ({To.Release})",
        };
    }

    private string Verdict(string from, string to)
    {
        if (Comparison.Changes.Count == 0)
        {
            var why = SameFile
                ? "The two versions are the same schema, file for file."
                : OnlyIdentifiersDiffer
                    ? "The files differ only in the version identifiers each one carries; the schema is the same."
                    : "No template variable differs. The files differ in what a template does not hold, such as annotations.";
            return $"**No change.** {why} A mapping of {from} fills {to} as it is.";
        }

        var counts = string.Join(", ", Impacts.Select(impact => $"{Comparison.Count(impact)} {impact.ToString().ToLowerInvariant()}"));
        var verdict = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"**{counts}**; {Counted(Comparison.Unchanged, "variable")} unchanged. ")
            .Append(Comparison.Count(TemplateChangeImpact.Breaking) > 0
                ? $"A mapping written for {from} needs a look at the breaking changes before it moves to {to}."
                : $"Nothing a mapping written for {from} relies on changed.");
        if (SameFile && SharedSchemas.Count > 0)
        {
            verdict.Append(CultureInfo.InvariantCulture,
                $" The kind's own file is the same in both; these changes come from the {Counted(SharedSchemas.Count, "shared schema")} it refers to that changed.");
        }

        return verdict.ToString();
    }

    private void WriteMappings(StringBuilder md, string to)
    {
        md.AppendLine().AppendLine("## Synced mappings of this kind").AppendLine();
        if (Mappings.Count == 0)
        {
            md.Append("No synced repository holds a mapping of ").Append(Code(Stem(To.Kind))).AppendLine(".");
            return;
        }

        var ordered = OrderedMappings();
        var states = new (MappingMoveState State, string Words)[]
        {
            (MappingMoveState.NeedsChanges, "need changes"),
            (MappingMoveState.Unchecked, "could not be checked"),
            (MappingMoveState.Ready, "move as they are"),
            (MappingMoveState.Current, $"already pin {to}"),
        };
        var tally = states
            .Select(s => (s.Words, Count: ordered.Count(m => m.Check.State == s.State)))
            .Where(s => s.Count > 0)
            .Select(s => $"{s.Count} {s.Words}");
        md.Append(CultureInfo.InvariantCulture, $"{Counted(ordered.Count, "mapping")}: {string.Join(", ", tally)}. ")
            .Append("Each is set against ").Append(to).Append(" from the template it pins, which can be another version than ")
            .Append(SchemaVersion(From.Kind))
            .AppendLine(": what it writes that changed in a way that breaks it, and what the newer version requires that it would leave empty or may leave out, by the delivery gate's rule on required properties. The checks that read the cache are left to the gate.");

        foreach (var mapping in ordered)
        {
            md.AppendLine().Append("### ").Append(Code(mapping.Reference)).Append(": ").AppendLine(StateWords(mapping.Check.State, to)).AppendLine();
            md.Append(Code(mapping.Path)).Append(" in ").Append(Text(mapping.Repository)).Append(", pins ").Append(Code(mapping.PinnedKind));
            if (mapping.PinnedVersion is { Length: > 0 } version)
            {
                md.Append(" (template ").Append(Code(version)).Append(')');
            }

            md.AppendLine(".");
            switch (mapping.Check.State)
            {
                case MappingMoveState.Unchecked:
                    md.AppendLine().Append("It could not be checked: ").Append(Text(mapping.Problem ?? "the reason was not recorded")).AppendLine();
                    break;
                case MappingMoveState.Ready:
                    md.AppendLine().Append("Nothing it writes changed in a way that breaks it, and ").Append(to)
                        .AppendLine(" requires nothing of it that the version it pins did not.");
                    break;
                case MappingMoveState.NeedsChanges:
                    md.AppendLine();
                    foreach (var finding in mapping.Check.Findings)
                    {
                        md.Append("- ").Append(Code(finding.Path)).Append(": ").AppendLine(FindingText(finding, markdown: true));
                    }

                    break;
            }
        }
    }

    private static void WriteChange(StringBuilder md, TemplateVariableChange change)
    {
        md.Append("- ").Append(Code(change.Path)).Append(": ").Append(TemplateChangeAdvice.Summary(change, Code)).Append('.');
        if (TemplateChangeAdvice.Action(change) is { } action)
        {
            md.Append(' ').AppendLine(action);
            return;
        }

        md.AppendLine();
        foreach (var field in change.Fields.OrderByDescending(f => f.Impact))
        {
            md.Append("  - ").AppendLine(TemplateChangeAdvice.Note(field, Code));
        }
    }

    /// <summary>
    /// What a finding says: a removed variable and what to do, each breaking field's note, or the requirement as the gate
    /// words it; as Markdown, with values set as code and the gate's words escaped, or as plain text.
    /// </summary>
    private static string FindingText(MappingMoveFinding finding, bool markdown)
    {
        if (finding.Requirement is { } requirement)
        {
            return markdown ? Text(requirement) : requirement;
        }

        Func<string, string> quote = markdown ? Code : Plain;
        var change = finding.Change!;
        return change.Change == TemplateVariableChangeKind.Removed
            ? $"{TemplateChangeAdvice.Summary(change, quote)}. {TemplateChangeAdvice.Action(change)}"
            : string.Join(" ", finding.Fields.Select(field => TemplateChangeAdvice.Note(field, quote)));
    }

    private static string StateWords(MappingMoveState state, string to) => state switch
    {
        MappingMoveState.NeedsChanges => "needs changes",
        MappingMoveState.Unchecked => "could not be checked",
        MappingMoveState.Ready => "moves as it is",
        _ => $"already pins {to}",
    };

    private static string Hint(TemplateChangeImpact impact) => impact switch
    {
        TemplateChangeImpact.Breaking => "A mapping written for the older version can stop rendering, or render a record the newer version does not accept.",
        TemplateChangeImpact.Additive => "Something a mapping may now use; nothing a mapping of the older version relies on changed.",
        _ => "Only a title or a description reads differently.",
    };

    private static void Row(StringBuilder md, string label, string from, string to)
        => md.Append("| ").Append(label).Append(" | ").Append(Cell(from)).Append(" | ").Append(Cell(to)).AppendLine(" |");

    /// <summary>The kind without its version (<c>osdu:wks:master-data--Wellbore</c>).</summary>
    private static string Stem(string kind) => kind.LastIndexOf(':') is var at and > 0 ? kind[..at] : kind;

    /// <summary>The schema version a kind ends with (<c>1.3.0</c>).</summary>
    private static string SchemaVersion(string kind) => kind[(kind.LastIndexOf(':') + 1)..];

    /// <summary>The entity a kind is of (<c>Wellbore</c> for <c>osdu:wks:master-data--Wellbore:1.3.0</c>).</summary>
    private static string EntityName(string kind)
    {
        var parts = kind.Split(':');
        var entityType = parts.Length >= 3 ? parts[2] : kind;
        var at = entityType.LastIndexOf("--", StringComparison.Ordinal);
        return at < 0 ? entityType : entityType[(at + 2)..];
    }

    /// <summary>A value as JSON's text holds it: as it is.</summary>
    private static string Plain(string value) => value;

    private static string Counted(int count, string noun) => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    private static string Stamp(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>A value as inline code, on one line, fenced with more backticks than it holds in a row.</summary>
    private static string Code(string value)
    {
        var line = OneLine(value);
        var longest = 0;
        var run = 0;
        foreach (var c in line)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', longest + 1);
        var pad = line.StartsWith('`') || line.EndsWith('`') ? " " : string.Empty;
        return fence + pad + line + pad + fence;
    }

    /// <summary>Prose as Markdown reads it literally: on one line, with what would start emphasis, code, a link or markup escaped.</summary>
    private static string Text(string value)
    {
        var text = new StringBuilder();
        foreach (var c in OneLine(value))
        {
            if (c is '\\' or '`' or '*' or '[' or ']' or '<' or '>')
            {
                text.Append('\\');
            }

            text.Append(c);
        }

        return text.ToString();
    }

    private static string Link(string text, Uri? url) => url is null ? Code(text) : $"[{Text(text)}]({url.AbsoluteUri})";

    /// <summary>A table cell: its pipes escaped, so the row keeps its columns.</summary>
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    private static string OneLine(string value) => value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}
