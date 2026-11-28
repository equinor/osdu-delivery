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
    Html,
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
/// <param name="Check">Where it stands, and what the move means for it.</param>
/// <param name="Problem">Why it could not be checked, for <see cref="MappingMoveState.Unchecked"/>.</param>
public sealed record TemplateMoveMapping(
    string Repository, string Reference, string Path, string PinnedKind, string? PinnedVersion, MappingMoveCheck Check, string? Problem = null);

/// <summary>
/// How much a change between two versions of a kind weighs for the synced mappings of the kind, read against what their
/// documents write; declared heaviest first.
/// </summary>
public enum TemplateChangeWeight
{
    /// <summary>
    /// It asks something of a mapping that writes what it changes: the mapping has to change for it (it breaks what the
    /// mapping writes, or requires what the mapping may leave out), or it deprecates what the mapping writes.
    /// </summary>
    Significant,

    /// <summary>It touches what a mapping writes and asks nothing of it: how it reads, a rule relaxed, more kinds it may point to.</summary>
    Minor,

    /// <summary>No synced mapping writes what it changes: it matters only to a mapping that starts to use it.</summary>
    Unused,
}

/// <summary>A change between the two versions, weighed against the synced mappings, with those that use what it changes (<see cref="MappingMoveCheck.Uses"/>).</summary>
/// <param name="Change">The change.</param>
/// <param name="UsedBy">The mappings that use what it changes, as the report names them; empty for a change no mapping uses.</param>
/// <param name="Weight">How much it weighs for them.</param>
public sealed record TemplateMoveChange(TemplateVariableChange Change, IReadOnlyList<string> UsedBy, TemplateChangeWeight Weight)
{
    /// <summary>Whether a synced mapping uses what the change changes.</summary>
    public bool Used => UsedBy.Count > 0;
}

/// <summary>
/// The report on moving from one version of a kind to another (osdu/docs/reference/concepts/template-versions.md, The
/// report), read against the documents of the synced mappings of the kind: which version is which, what the move means for
/// each mapping (what it has to change, what it writes that is deprecated or changed without breaking it, what it may now
/// fill), every change between the versions with the mappings that use what it changes, the changes no mapping uses after
/// them as weighing less, the shared schemas that differ, and how to move. Rendered as Markdown to paste into a ticket, a pull
/// request or a wiki, as a self-contained HTML page to read, print or send, or as JSON for a tool. The API renders through
/// this one class, so the report reads the same wherever it is taken from.
/// </summary>
public sealed partial record TemplateMoveReport(
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

    /// <summary>The findings of a mapping, by kind, as a report heads them, in the order a report lists them.</summary>
    private static readonly MappingFindingKind[] FindingKinds = [MappingFindingKind.MustChange, MappingFindingKind.Deprecated, MappingFindingKind.Changed, MappingFindingKind.MayFill];

    /// <summary>The weights of a change, heaviest first, as a report lists them.</summary>
    private static readonly TemplateChangeWeight[] Weights = [TemplateChangeWeight.Significant, TemplateChangeWeight.Minor, TemplateChangeWeight.Unused];

    /// <summary>Indented, and on line feeds whatever the host, so a report is the same file wherever it is rendered.</summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n" };

    /// <summary>The report's format by the name the API takes (json, md, markdown, html); null for any other.</summary>
    public static TemplateMoveReportFormat? FormatOf(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "json" => TemplateMoveReportFormat.Json,
        "md" or "markdown" => TemplateMoveReportFormat.Markdown,
        "html" => TemplateMoveReportFormat.Html,
        _ => null,
    };

    /// <summary>The media type and file extension of a format.</summary>
    public static (string MediaType, string Extension) Describe(TemplateMoveReportFormat format) => format switch
    {
        TemplateMoveReportFormat.Markdown => ("text/markdown; charset=utf-8", "md"),
        TemplateMoveReportFormat.Html => ("text/html; charset=utf-8", "html"),
        _ => ("application/json; charset=utf-8", "json"),
    };

    /// <summary>The file name a report downloads as: the entity, and each side's schema version and release.</summary>
    public string FileName(TemplateMoveReportFormat format)
    {
        var name = string.Join('-', EntityName(To.Kind), SchemaVersion(From.Kind), From.Release, "to", SchemaVersion(To.Kind), To.Release, "report");
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        return $"{safe}.{Describe(format).Extension}";
    }

    public string Render(TemplateMoveReportFormat format) => format switch
    {
        TemplateMoveReportFormat.Markdown => ToMarkdown(),
        TemplateMoveReportFormat.Html => ToHtml(),
        _ => ToJson(),
    };

    /// <summary>The synced mappings in the order the report lists them: what needs changes first, then by repository and name.</summary>
    public IReadOnlyList<TemplateMoveMapping> OrderedMappings() => Mappings
        .OrderBy(m => m.Check.State)
        .ThenBy(m => m.Repository, StringComparer.OrdinalIgnoreCase)
        .ThenBy(m => m.Reference, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// The mappings the changes are weighed against: those set against the newer version from another one. A mapping that
    /// already pins the newer version has nothing to move, and one that could not be checked says nothing of what it writes.
    /// </summary>
    public IReadOnlyList<TemplateMoveMapping> Weighing() => OrderedMappings()
        .Where(m => m.Check.State is MappingMoveState.NeedsChanges or MappingMoveState.Ready)
        .ToList();

    /// <summary>
    /// Every change between the versions, in the comparison's order, weighed against the mappings set against the newer
    /// version (<see cref="Weighing"/>): significant when it asks something of one that uses what it changes, minor when it
    /// touches what one writes and asks nothing, unused when none uses it. With no mapping to weigh against, every change is
    /// unused.
    /// </summary>
    public IReadOnlyList<TemplateMoveChange> WeighedChanges()
    {
        var weighing = Weighing();
        return Comparison.Changes.Select(change =>
        {
            var users = weighing.Where(m => m.Check.Uses(change)).ToList();
            var weight = users.Count == 0
                ? TemplateChangeWeight.Unused
                : users.Any(m => Asks(m.Check, change)) ? TemplateChangeWeight.Significant : TemplateChangeWeight.Minor;
            return new TemplateMoveChange(change, users.Select(NameOf).ToList(), weight);
        }).ToList();
    }

    /// <summary>
    /// Whether the move asks something of a mapping about a change: what the mapping has to change at the changed variable or
    /// inside it (a removed object takes what it held with it), or the variable's deprecation by this move.
    /// </summary>
    private static bool Asks(MappingMoveCheck check, TemplateVariableChange change) => check.Findings.Any(finding => finding.Kind switch
    {
        MappingFindingKind.MustChange => TemplatePath.IsWithin(change.Path, finding.Path),
        MappingFindingKind.Deprecated => !finding.AlreadyDeprecated && string.Equals(finding.Path, change.Path, StringComparison.Ordinal),
        _ => false,
    });

    /// <summary>A mapping as the report names it: its reference, and its repository where another repository holds a mapping of the same reference.</summary>
    public string NameOf(TemplateMoveMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var shared = Mappings.Count(m => string.Equals(m.Reference, mapping.Reference, StringComparison.Ordinal)) > 1;
        return shared ? $"{mapping.Reference} in {mapping.Repository}" : mapping.Reference;
    }

    public string ToJson()
    {
        var weighing = Weighing();
        var weighed = WeighedChanges();
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
                ["weighedMappings"] = weighing.Count,
                ["significant"] = weighing.Count == 0 ? null : (JsonNode)weighed.Count(c => c.Weight == TemplateChangeWeight.Significant),
                ["minor"] = weighing.Count == 0 ? null : (JsonNode)weighed.Count(c => c.Weight == TemplateChangeWeight.Minor),
                ["unused"] = weighing.Count == 0 ? null : (JsonNode)weighed.Count(c => c.Weight == TemplateChangeWeight.Unused),
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
                ["writes"] = mapping.Check.Writes.Count,
                ["counts"] = new JsonObject(FindingKinds.Select(kind =>
                    KeyValuePair.Create(JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()), (JsonNode?)mapping.Check.Count(kind)))),
                ["findings"] = new JsonArray(mapping.Check.Findings.Select(finding => (JsonNode)new JsonObject
                {
                    ["path"] = finding.Path,
                    ["kind"] = finding.Kind.ToString(),
                    ["line"] = finding.Write?.Line,
                    ["reads"] = finding.Write?.Reads,
                    ["change"] = finding.Change?.Change.ToString(),
                    ["fields"] = Fields(finding.Fields),
                    ["requirement"] = finding.Requirement,
                    ["deprecation"] = finding.Deprecation,
                    ["alreadyDeprecated"] = finding.Kind == MappingFindingKind.Deprecated || finding.Deprecation is not null ? (JsonNode)finding.AlreadyDeprecated : null,
                    ["note"] = FindingText(finding, Plain, Plain),
                }).ToArray()),
            }).ToArray()),
            ["changes"] = new JsonArray(weighed.Select(entry => (JsonNode)new JsonObject
            {
                ["path"] = entry.Change.Path,
                ["change"] = entry.Change.Change.ToString(),
                ["impact"] = entry.Change.Impact.ToString(),
                ["role"] = entry.Change.Role.ToString(),
                ["deprecates"] = entry.Change.Fields.Any(TemplateChangeAdvice.Deprecates),
                ["weight"] = weighing.Count == 0 ? null : entry.Weight.ToString(),
                ["usedBy"] = weighing.Count == 0 ? null : new JsonArray(entry.UsedBy.Select(name => (JsonNode)JsonValue.Create(name)).ToArray()),
                ["summary"] = TemplateChangeAdvice.Summary(entry.Change, Plain),
                ["action"] = TemplateChangeAdvice.Action(entry.Change),
                ["fields"] = Fields(entry.Change.Fields),
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
        md.AppendLine(MappingVerdict(Code, text => $"**{text}**"));
        md.AppendLine();
        md.AppendLine(Verdict(markdown: true));

        WriteMappings(md, to);

        var weighed = WeighedChanges();
        if (Weighing().Count == 0)
        {
            foreach (var impact in Impacts)
            {
                var changes = weighed.Where(c => c.Change.Impact == impact).ToList();
                if (changes.Count > 0)
                {
                    md.AppendLine().Append(CultureInfo.InvariantCulture, $"## {impact} changes ({changes.Count})").AppendLine();
                    md.AppendLine().AppendLine(Hint(impact)).AppendLine();
                    changes.ForEach(change => WriteChange(md, change, detailed: true));
                }
            }
        }
        else
        {
            foreach (var weight in Weights)
            {
                WriteWeighed(md, weighed.Where(c => c.Weight == weight).ToList(), weight);
            }
        }

        md.AppendLine().AppendLine("## Shared schemas").AppendLine();
        md.AppendLine(SharedSchemasText());
        if (SharedSchemas.Count > 0)
        {
            md.AppendLine();
            foreach (var file in SharedSchemas)
            {
                md.Append("- ").Append(Code(file.Name)).Append(": ")
                    .Append(file.FromPath is null ? "not referred to" : Link(file.FromPath, file.FromWebUrl))
                    .Append(" to ")
                    .Append(file.ToPath is null ? "not referred to" : Link(file.ToPath, file.ToWebUrl))
                    .AppendLine();
            }
        }

        if (Comparison.Changes.Any(c => c.Impact != TemplateChangeImpact.Wording) || Weighing().Any(m => m.Check.Findings.Count > 0))
        {
            md.AppendLine().Append("## Moving a mapping to ").AppendLine(to).AppendLine();
            var steps = MovingSteps(Code);
            for (var i = 0; i < steps.Count; i++)
            {
                md.Append(CultureInfo.InvariantCulture, $"{i + 1}. ").AppendLine(steps[i]);
            }
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

    /// <summary>
    /// What the move means for the synced mappings, first: how many there are and where each stands, and how much of the
    /// comparison touches what they write. A kind no mapping is weighed against says the changes count in full.
    /// </summary>
    private string MappingVerdict(Func<string, string> quote, Func<string, string> strong)
    {
        var to = SchemaVersion(To.Kind);
        if (Mappings.Count == 0)
        {
            return $"No synced repository holds a mapping of {quote(Stem(To.Kind))}, so no change is weighed against what a mapping writes: each counts in full.";
        }

        var verdict = new StringBuilder(Tally(to)).Append(". ");
        var weighing = Weighing();
        if (weighing.Count == 0)
        {
            return verdict.Append("None moves from another version, so no change is weighed against what a mapping writes: each counts in full.").ToString();
        }

        var weighed = WeighedChanges();
        var significant = weighed.Count(c => c.Weight == TemplateChangeWeight.Significant);
        var minor = weighed.Count(c => c.Weight == TemplateChangeWeight.Minor);
        var unused = weighed.Count - significant - minor;
        var what = weighing.Count == 1
            ? $"what {quote(NameOf(weighing[0]))} writes"
            : $"what the {weighing.Count} mappings moving to {to} write";
        if (weighed.Count == 0)
        {
            verdict.Append("Nothing changed between the versions.");
        }
        else if (significant + minor == 0)
        {
            verdict.Append(CultureInfo.InvariantCulture,
                $"Read against {what}, {strong($"none of the {Counted(weighed.Count, "change")} is significant")}: none touches {(weighing.Count == 1 ? "it" : "them")}, so each matters only to a mapping that starts to use what it changes.");
        }
        else
        {
            var one = weighing.Count == 1;
            var mustChange = weighed.Count(c => c.Weight == TemplateChangeWeight.Significant && weighing.Any(m => m.Check.Findings.Any(f => f.Kind == MappingFindingKind.MustChange
                && TemplatePath.IsWithin(c.Change.Path, f.Path))));
            var deprecating = significant - mustChange;
            var why = new List<string>();
            if (mustChange > 0)
            {
                why.Add($"{mustChange} ask{(mustChange == 1 ? "s" : string.Empty)} a change of {(one ? "it" : "a mapping")}");
            }

            if (deprecating > 0)
            {
                why.Add($"{deprecating} deprecate{(deprecating == 1 ? "s" : string.Empty)} what {(one ? "it writes" : "they write")}");
            }

            var rest = new List<string>();
            if (minor > 0)
            {
                rest.Add($"{minor} {(minor == 1 ? "is" : "are")} minor, touching what {(one ? "it writes" : "they write")} without asking anything of {(one ? "it" : "them")}");
            }

            if (unused > 0)
            {
                rest.Add($"{unused} touch{(unused == 1 ? "es" : string.Empty)} nothing a synced mapping uses");
            }

            verdict.Append(CultureInfo.InvariantCulture, $"Read against {what}, ")
                .Append(significant == 0
                    ? strong($"none of the {Counted(weighed.Count, "change")} is significant")
                    : strong($"{significant} of the {Counted(weighed.Count, "change")} {(significant == 1 ? "is" : "are")} significant"))
                .Append(why.Count == 0 ? string.Empty : $" ({string.Join(", ", why)})")
                .Append(rest.Count == 0 ? string.Empty : "; " + string.Join("; ", rest))
                .Append('.');
        }
        var notChecked = Mappings.Count(m => m.Check.State == MappingMoveState.Unchecked);
        if (notChecked > 0)
        {
            verdict.Append(CultureInfo.InvariantCulture,
                $" {Counted(notChecked, "mapping")} could not be checked, so what {(notChecked == 1 ? "it writes is" : "they write is")} not weighed.");
        }

        return verdict.ToString();
    }

    /// <summary>How many synced mappings there are, and how many stand where.</summary>
    private string Tally(string to)
    {
        var states = new (MappingMoveState State, string One, string Many)[]
        {
            (MappingMoveState.NeedsChanges, "needs changes", "need changes"),
            (MappingMoveState.Unchecked, "could not be checked", "could not be checked"),
            (MappingMoveState.Ready, "moves as it is", "move as they are"),
            (MappingMoveState.Current, $"already pins {to}", $"already pin {to}"),
        };
        var tally = states
            .Select(s => (s, Count: Mappings.Count(m => m.Check.State == s.State)))
            .Where(s => s.Count > 0)
            .Select(s => $"{s.Count} {(s.Count == 1 ? s.s.One : s.s.Many)}");
        return $"{Counted(Mappings.Count, "synced mapping")} of the kind: {string.Join(", ", tally)}";
    }

    /// <summary>The comparison summed up: no change, or how many changes of each impact, and what that asks of a mapping.</summary>
    private string Verdict(bool markdown)
    {
        var from = SchemaVersion(From.Kind);
        var to = SchemaVersion(To.Kind);
        string Strong(string text) => markdown ? $"**{text}**" : text;
        if (Comparison.Changes.Count == 0)
        {
            var why = SameFile
                ? "The two versions are the same schema, file for file."
                : OnlyIdentifiersDiffer
                    ? "The files differ only in the version identifiers each one carries; the schema is the same."
                    : "No template variable differs. The files differ in what a template does not hold, such as annotations.";
            return $"{Strong("No change.")} {why} A mapping of {from} fills {to} as it is.";
        }

        var counts = string.Join(", ", Impacts.Select(impact => $"{Comparison.Count(impact)} {impact.ToString().ToLowerInvariant()}"));
        var verdict = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Between the two versions: {Strong(counts)}; {Counted(Comparison.Unchanged, "variable")} unchanged. ")
            .Append(Comparison.Count(TemplateChangeImpact.Breaking) > 0
                ? Weighing().Count == 0
                    ? $"A mapping written for {from} needs a look at the breaking changes before it moves to {to}."
                    : "A breaking change matters to a mapping that writes what it changes; the synced mappings are read against each below."
                : $"Nothing a mapping written for {from} relies on changed in a way that breaks it.");
        if (SameFile && SharedSchemas.Count > 0)
        {
            verdict.Append(CultureInfo.InvariantCulture,
                $" The kind's own file is the same in both; these changes come from the {Counted(SharedSchemas.Count, "shared schema")} it refers to that changed.");
        }

        return verdict.ToString();
    }

    private void WriteMappings(StringBuilder md, string to)
    {
        if (Mappings.Count == 0)
        {
            return;
        }

        md.AppendLine().AppendLine("## Synced mappings of this kind").AppendLine();
        md.Append("Each is read against what its document writes, from the template it pins, which can be another version than ")
            .Append(SchemaVersion(From.Kind)).Append(": what it has to change (what it writes that breaks, or what ").Append(to)
            .AppendLine(" requires that it would leave empty or may leave out, by the delivery gate's rule), what it writes that is deprecated or changed without breaking it, and what it may now fill. A change to nothing it writes means nothing for it. The checks that read the cache are left to the gate.");

        foreach (var mapping in OrderedMappings())
        {
            md.AppendLine().Append("### ").Append(Code(NameOf(mapping))).Append(": ").AppendLine(StateWords(mapping.Check.State, to)).AppendLine();
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
                case MappingMoveState.Ready or MappingMoveState.NeedsChanges:
                    md.AppendLine().AppendLine(Gist(mapping.Check, to));
                    foreach (var kind in FindingKinds)
                    {
                        var findings = mapping.Check.Findings.Where(f => f.Kind == kind).ToList();
                        if (findings.Count == 0)
                        {
                            continue;
                        }

                        md.AppendLine().Append(CultureInfo.InvariantCulture, $"#### {KindHeading(kind, to)} ({findings.Count})").AppendLine().AppendLine();
                        if (kind == MappingFindingKind.MayFill)
                        {
                            findings.ForEach(f => md.Append("- ").Append(Code(f.Path)).Append(": ").Append(FindingText(f, Code, Prose)).AppendLine());
                            continue;
                        }

                        md.Append("| Property | Line | The mapping writes it from | ").Append(kind == MappingFindingKind.Deprecated ? "What to use instead" : "What changed").AppendLine(" |");
                        md.AppendLine("| --- | --- | --- | --- |");
                        foreach (var finding in findings)
                        {
                            md.Append("| ").Append(Cell(Code(finding.Path)))
                                .Append(" | ").Append(finding.Write?.Line is { } line ? line.ToString(CultureInfo.InvariantCulture) : string.Empty)
                                .Append(" | ").Append(finding.Write is { } write ? Cell(Code(write.Reads)) : "what it writes inside it")
                                .Append(" | ").Append(Cell(FindingText(finding, Code, Prose)))
                                .AppendLine(" |");
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>The changes of one weight, each impact under a heading of its own.</summary>
    private static void WriteWeighed(StringBuilder md, List<TemplateMoveChange> changes, TemplateChangeWeight weight)
    {
        if (changes.Count == 0)
        {
            return;
        }

        md.AppendLine().Append(CultureInfo.InvariantCulture, $"## {WeightHeading(weight)} ({changes.Count})").AppendLine();
        md.AppendLine().AppendLine(WeightHint(weight)).AppendLine();

        // Weighed, a change is listed by what it means for the mappings; the impact the schema gives it is said on its line.
        changes.OrderByDescending(c => c.Change.Impact).ToList().ForEach(change => WriteChange(md, change, detailed: false, tagged: true));
    }

    /// <summary>
    /// A change and what it asks of a mapping, with the mappings that use it. Detailed, a changed variable says each field
    /// that changed on a line of its own; otherwise the fields are named on its line.
    /// </summary>
    private static void WriteChange(StringBuilder md, TemplateMoveChange entry, bool detailed, bool tagged = false)
    {
        var change = entry.Change;
        md.Append("- ").Append(Code(change.Path)).Append(": ").Append(TemplateChangeAdvice.Summary(change, Code))
            .Append(tagged ? $" ({ImpactTag(change.Impact)})" : string.Empty).Append('.');
        if (entry.Used)
        {
            md.Append(" Used by ").Append(string.Join(", ", entry.UsedBy.Select(Code))).Append('.');
        }

        if (TemplateChangeAdvice.Action(change) is { } action)
        {
            md.Append(' ').AppendLine(action);
            return;
        }

        md.AppendLine();
        if (!detailed)
        {
            return;
        }

        foreach (var field in change.Fields.OrderByDescending(f => f.Impact))
        {
            md.Append("  - ").Append(TemplateChangeAdvice.Note(field, Code));
            if (TemplateChangeAdvice.Deprecates(field) && TemplateDeprecation.Guidance(field.After) is { } guidance)
            {
                md.Append(" It says: ").Append(Prose(guidance));
            }

            md.AppendLine();
        }
    }

    /// <summary>
    /// What the move means for a mapping set against the newer version, in a sentence: how many of the variables it writes
    /// stay the same and how many changed, by what that means for it, and how many new ones it may fill.
    /// </summary>
    private static string Gist(MappingMoveCheck check, string to)
    {
        var touched = check.Findings.Where(f => f.Kind != MappingFindingKind.MayFill).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var same = check.Writes.Count(path => !touched.Contains(path));
        var parts = new List<string>();
        void Part(MappingFindingKind kind, string words)
        {
            if (check.Count(kind) is var count and > 0)
            {
                parts.Add($"{count} {words}");
            }
        }

        Part(MappingFindingKind.MustChange, "to change");
        Part(MappingFindingKind.Deprecated, $"deprecated in {to}");
        Part(MappingFindingKind.Changed, "changed without breaking it");
        var gist = new StringBuilder().Append(CultureInfo.InvariantCulture, $"It writes {Counted(check.Writes.Count, "property", "properties")}: ")
            .Append(parts.Count == 0 ? $"{to} changes none of them" : string.Join(", ", parts))
            .Append(CultureInfo.InvariantCulture, $"; {same} {(same == 1 ? "is" : "are")} the same.");
        if (check.Count(MappingFindingKind.MayFill) is var mayFill and > 0)
        {
            gist.Append(CultureInfo.InvariantCulture, $" {to} adds {Counted(mayFill, "property", "properties")} inside what it writes that it may fill.");
        }

        return gist.ToString();
    }

    /// <summary>
    /// What a finding says: a removed variable and what to do, each breaking field's note, the requirement as the gate words
    /// it, what replaces a deprecated variable, or what a new variable is; values through <paramref name="quote"/>, and the
    /// gate's and the schema's words through <paramref name="text"/>.
    /// </summary>
    private static string FindingText(MappingMoveFinding finding, Func<string, string> quote, Func<string, string> text)
    {
        if (finding.Requirement is { } requirement)
        {
            return text(requirement);
        }

        var change = finding.Change;
        switch (finding.Kind)
        {
            case MappingFindingKind.Deprecated:
                var instead = finding.Deprecation is { } guidance ? text(guidance) : "Its description marks it deprecated without saying what replaces it.";
                return finding.AlreadyDeprecated ? $"{instead} (Already deprecated in the version it pins.)" : instead;
            case MappingFindingKind.MayFill:
                return change is null ? string.Empty : $"{TemplateChangeAdvice.Summary(change, quote)}.";
        }

        if (change is null)
        {
            return string.Empty;
        }

        var said = change.Change == TemplateVariableChangeKind.Removed
            ? $"{TemplateChangeAdvice.Summary(change, quote)}. {TemplateChangeAdvice.Action(change)}"
            : string.Join(" ", finding.Fields.Select(field => finding.Kind == MappingFindingKind.Changed && field.Field == TemplateChangeAdvice.RequiredField && field.After == TemplateChangeAdvice.RequiredField
                ? "It is now required, and the mapping fills it on every row."
                : TemplateChangeAdvice.Note(field, quote)));
        return finding.Deprecation is { } replaced ? $"{said} It is also deprecated: {text(replaced)}" : said;
    }

    /// <summary>The shared schemas the kind refers to, summed up.</summary>
    private string SharedSchemasText() => SharedSchemas.Count == 0
        ? $"Every shared schema the kind refers to is the same in both ({SameSharedSchemas})."
        : $"{Counted(SharedSchemas.Count, "shared schema")} the kind refers to {(SharedSchemas.Count == 1 ? "differs" : "differ")}; {SameSharedSchemas} {(SameSharedSchemas == 1 ? "is" : "are")} the same. A kind's own file can be the same in two releases while a schema it refers to changed, and that is where its template's changes then come from.";

    /// <summary>How to move a mapping to the newer version, step by step, with commands through <paramref name="quote"/>.</summary>
    private List<string> MovingSteps(Func<string, string> quote)
    {
        var to = SchemaVersion(To.Kind);
        return
        [
            $"Download the {to} file of release {To.Release} into the template folder the mapping reads: Templates, Browse OSDU, Download, or {quote($"sqlflow template capture --kind {To.Kind} --release {To.Release} --out <folder>/<file>.json")}.",
            "Fix what this report lists under the mapping, and plan to move off what it writes that is deprecated. A mapping that names its template version is moved by naming the new one (give it a new mapping version when both are to run side by side); one that names the kind alone moves with the file.",
            $"Check it with {quote("sqlflow check")}, which runs every check of the delivery gate, those that read the cache included.",
            $"Run the flow's {quote("plan")} operation to see which records would be delivered again, before delivering.",
        ];
    }

    private static string StateWords(MappingMoveState state, string to) => state switch
    {
        MappingMoveState.NeedsChanges => "needs changes",
        MappingMoveState.Unchecked => "could not be checked",
        MappingMoveState.Ready => "moves as it is",
        _ => $"already pins {to}",
    };

    /// <summary>The impact the schema gives a change, as a change listed by its weight is tagged with.</summary>
    private static string ImpactTag(TemplateChangeImpact impact) => $"{impact.ToString().ToLowerInvariant()} in the schema";

    private static string WeightHeading(TemplateChangeWeight weight) => weight switch
    {
        TemplateChangeWeight.Significant => "Significant changes",
        TemplateChangeWeight.Minor => "Minor changes",
        _ => "Changes no synced mapping uses",
    };

    private static string WeightHint(TemplateChangeWeight weight) => weight switch
    {
        TemplateChangeWeight.Significant => "Each asks something of a synced mapping that writes what it changes: the mapping has to change for it, or what it writes is deprecated. What it asks is said under the mapping above.",
        TemplateChangeWeight.Minor => "Each touches what a synced mapping writes and asks nothing of it: how it reads, a rule it no longer has to keep, more kinds it may point to.",
        _ => "No synced mapping writes what these change, so they have the least impact: each matters only to a mapping that starts to use what it changes, or to one not synced here.",
    };

    private static string KindHeading(MappingFindingKind kind, string to) => kind switch
    {
        MappingFindingKind.MustChange => "To change",
        MappingFindingKind.Deprecated => $"Deprecated in {to}",
        MappingFindingKind.Changed => "Changed without breaking it",
        _ => "It may now fill",
    };

    private static string Hint(TemplateChangeImpact impact) => impact switch
    {
        TemplateChangeImpact.Breaking => "A mapping written for the older version can stop rendering, or render a record the newer version does not accept.",
        TemplateChangeImpact.Additive => "Something a mapping may now use; nothing a mapping of the older version relies on changed.",
        _ => "Only a title or a description reads differently; a description that now starts with DEPRECATED marks its property deprecated.",
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

    private static string Counted(int count, string noun) => Counted(count, noun, noun + "s");

    private static string Counted(int count, string one, string many) => string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");

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

    /// <summary>The gate's or a schema's words in Markdown: the spans a schema sets in backticks as code, the rest as text.</summary>
    private static string Prose(string value) => Spans(value, Code, Text);

    /// <summary>
    /// Prose with the code spans it writes in backticks (<c>Use the `SamplingStart` property.</c>) set through
    /// <paramref name="quote"/> and the rest through <paramref name="text"/>; prose with an unpaired backtick is text throughout.
    /// </summary>
    private static string Spans(string prose, Func<string, string> quote, Func<string, string> text)
    {
        var parts = prose.Split('`');
        if (parts.Length % 2 == 0)
        {
            return text(prose);
        }

        var spans = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            spans.Append(i % 2 == 0 ? text(parts[i]) : parts[i].Length == 0 ? string.Empty : quote(parts[i]));
        }

        return spans.ToString();
    }

    /// <summary>A table cell: its pipes escaped, so the row keeps its columns.</summary>
    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);

    private static string OneLine(string value) => value.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}
