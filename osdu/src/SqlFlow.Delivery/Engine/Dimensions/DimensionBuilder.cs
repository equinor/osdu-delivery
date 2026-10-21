using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// A dimension as the explorer's dimension builder holds it while a person picks it
/// (osdu/docs/reference/concepts/explorer.md, Building a dimension): every part of a <c>dimensions</c> item, each as
/// typed or picked and none of them checked yet. The builder writes it as YAML (<see cref="DimensionBuilder.ToYaml"/>)
/// and the document loader reads that YAML back, so what the builder offers is exactly what a flow would declare.
/// </summary>
public sealed record DimensionDraft
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The kind whose records are read, wildcards allowed per segment.</summary>
    public string? Kind { get; init; }

    /// <summary>A query narrowing the records read; null reads every record of the kind.</summary>
    public string? Query { get; init; }

    /// <summary>The key: the path of the records whose distinct values the dimension holds.</summary>
    public string? Path { get; init; }

    /// <summary>Where each key's value is read, through the records it names; empty for a key that is its own value.</summary>
    public IReadOnlyList<string>? Label { get; init; }

    /// <summary>The value of a key whose label or attribute is not read.</summary>
    public string? Unlabelled { get; init; }

    public IReadOnlyList<DimensionDraftAttributeSpec>? Attributes { get; init; }

    public IReadOnlyList<DimensionDraftCleanStep>? Clean { get; init; }

    /// <summary>The name of the table's key column, when not the one the path gives.</summary>
    public string? KeyColumn { get; init; }

    /// <summary>The name of the table's value column, when not the one the label or the name gives.</summary>
    public string? ValueColumn { get; init; }

    public bool? CountRecords { get; init; }

    public long? MaxValues { get; init; }
}

/// <summary>An attribute of a draft: its name, and the paths it is read through from the record a key names, or the path of the dimension's own records it collects.</summary>
public sealed record DimensionDraftAttributeSpec(string? Name, IReadOnlyList<string>? Steps, string? Collect);

/// <summary>A clean step of a draft: its name (<c>trim</c>, <c>upper</c>, <c>replace</c>, ...), and a <c>replace</c> step's pattern and replacement.</summary>
public sealed record DimensionDraftCleanStep(string? Step, string? Pattern, string? With);

/// <summary>Something the builder found about a draft: an error keeps the dimension from loading or building; a warning does not.</summary>
/// <param name="Severity"><c>error</c> or <c>warning</c>.</param>
/// <param name="Message">What is wrong and what to do about it.</param>
/// <param name="Target">The part of the dimension it is about, as the YAML's spans name them (<c>path</c>, <c>attributes.Country</c>); null for the whole.</param>
/// <param name="Code">What kind of issue it is, where a page offers something to do about it (<see cref="TemplateCode"/>, <see cref="NameCode"/>); null otherwise.</param>
public sealed record DimensionDraftIssue(string Severity, string Message, string? Target, string? Code = null)
{
    public const string Error = "error";
    public const string Warning = "warning";

    /// <summary>No synced repository holds a template: the Templates page downloads its file from the OSDU data definitions.</summary>
    public const string TemplateCode = "template";

    /// <summary>Another flow's dimension writes the table this one would.</summary>
    public const string NameCode = "name";
}

/// <summary>
/// Writes a dimension the builder holds as the item a flow lists under <c>dimensions</c>, in the documented style, and reads
/// what the document loader says of it back into issues the builder can point at. The YAML is the builder's whole output: a
/// person copies it into a dimension flow, so it is written by the same rules the loader reads by, and checked by reading it
/// back through the loader (<see cref="Document"/>) rather than by a second set of rules.
/// </summary>
public static partial class DimensionBuilder
{
    /// <summary>The flow a draft is read back in to be checked; it reaches nothing, since nothing reads it but the loader.</summary>
    public const string CheckFlowName = "dimension-builder";

    /// <summary>Where the loader says a draft's document comes from, at the start of every message it writes about it.</summary>
    public const string CheckSource = "builder";

    /// <summary>The indentation of the item under <c>dimensions</c>, as the documentation writes it.</summary>
    private const string Item = "  - ";

    /// <summary>The indentation of the item's keys.</summary>
    private const string Key = "    ";

    /// <summary>The indentation of what a key of the item holds.</summary>
    private const string Inner = "      ";

    /// <summary>The indentation of what an attribute holds.</summary>
    private const string Deeper = "        ";

    /// <summary>The longest text the builder takes for any one part of a draft: far over what any part may hold, so the loader is the one to say what is too long.</summary>
    public const int MaxTextLength = 4096;

    /// <summary>The most entries the builder takes in a list of a draft: more than the loader allows, so the loader says how many it takes.</summary>
    public const int MaxListLength = 64;

    /// <summary>
    /// The draft as the item a dimension flow lists under <c>dimensions</c>, indented as it sits there, ending with a line
    /// break. Every part present is written, in the order the documentation writes them, a part left blank is left out, and
    /// a name left blank is written empty so the loader names what is missing; a value is quoted wherever YAML would not
    /// read it back as written.
    /// </summary>
    public static string ToYaml(DimensionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var yaml = new StringBuilder();
        void Line(string text) => yaml.Append(text).Append('\n');

        Line(Item + "name: " + Scalar(Trimmed(draft.Name) ?? string.Empty));
        Optional(Line, Key, "description", draft.Description);
        Optional(Line, Key, "kind", draft.Kind);
        Optional(Line, Key, "query", draft.Query);
        Optional(Line, Key, "path", draft.Path);
        Steps(Line, Key, "label", Present(draft.Label), Inner);
        Optional(Line, Key, "unlabelled", draft.Unlabelled);

        var attributes = (draft.Attributes ?? []).Where(a => a is not null).ToList();
        if (attributes.Count > 0)
        {
            Line(Key + "attributes:");
            foreach (var attribute in attributes)
            {
                var name = Scalar(Trimmed(attribute.Name) ?? string.Empty);
                if (Trimmed(attribute.Collect) is { } collect)
                {
                    Line(Inner + name + ": { collect: " + FlowScalar(collect) + " }");
                }
                else
                {
                    Steps(Line, Inner, name, Present(attribute.Steps), Deeper, always: true);
                }
            }
        }

        var keyColumn = Trimmed(draft.KeyColumn);
        var valueColumn = Trimmed(draft.ValueColumn);
        if (keyColumn is not null || valueColumn is not null)
        {
            var named = new List<string>(2);
            if (keyColumn is not null)
            {
                named.Add("key: " + FlowScalar(keyColumn));
            }

            if (valueColumn is not null)
            {
                named.Add("value: " + FlowScalar(valueColumn));
            }

            Line(Key + "columns: { " + string.Join(", ", named) + " }");
        }

        var clean = (draft.Clean ?? []).Where(c => Trimmed(c?.Step) is not null).ToList();
        if (clean.Count > 0)
        {
            Line(Key + "clean:");
            foreach (var step in clean)
            {
                var name = Trimmed(step.Step)!;
                Line(name == "replace"
                    ? $"{Inner}- replace: {{ pattern: {Quoted(step.Pattern ?? string.Empty)}, with: {Quoted(step.With ?? string.Empty)} }}"
                    : Inner + "- " + Scalar(name));
            }
        }

        if (draft.CountRecords == true)
        {
            Line(Key + "countRecords: true");
        }

        if (draft.MaxValues is { } maxValues)
        {
            Line(Key + "maxValues: " + maxValues.ToString(CultureInfo.InvariantCulture));
        }

        return yaml.ToString();
    }

    /// <summary>
    /// A dimension flow document listing <paramref name="item"/> and nothing else of note, which the loader reads to check the
    /// item exactly as a flow's own would be: its source names an endpoint nobody reaches, since the document is only read.
    /// </summary>
    public static string Document(string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"flowType: dimension\nname: {CheckFlowName}\nsource:\n  endpoint: https://osdu.invalid\ndimensions:\n{item}";
    }

    /// <summary>The number of lines <see cref="Document"/> writes before the item.</summary>
    public const int DocumentHeaderLines = 5;

    /// <summary>
    /// What is missing from a draft before anything else can be said of it: the dimension's name, the kind it reads and its
    /// key. A draft missing one of them is not read further, since the loader stops at the first.
    /// </summary>
    public static IReadOnlyList<DimensionDraftIssue> Incomplete(DimensionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var issues = new List<DimensionDraftIssue>(3);
        if (Trimmed(draft.Name) is null)
        {
            issues.Add(new DimensionDraftIssue(DimensionDraftIssue.Error, "Name the dimension: its name names its table (osdu.dim_<name>) and every page that shows it.", "name"));
        }

        if (Trimmed(draft.Kind) is null)
        {
            issues.Add(new DimensionDraftIssue(DimensionDraftIssue.Error, "Pick the kind of the records the dimension reads.", "kind"));
        }

        if (Trimmed(draft.Path) is null)
        {
            issues.Add(new DimensionDraftIssue(
                DimensionDraftIssue.Error, "Pick the key: the value of each record whose distinct values the dimension holds, such as data.WellboreID. The values marked with a key name other records, as a key most often does.", "path"));
        }

        return issues;
    }

    /// <summary>
    /// What the loader said of a draft's document, as an issue of the builder: its words without the document's name and the
    /// item's place in it, which the builder has no use for, pointed at the part of the dimension it is about.
    /// </summary>
    public static DimensionDraftIssue FromLoader(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var text = message.Trim();
        var prefix = CheckSource + ": ";
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            text = text[prefix.Length..];
        }

        // "'dimensions[0].kind' is required.": the key alone is named.
        var required = RequiredKey().Match(text);
        if (required.Success)
        {
            text = $"{required.Groups["key"].Value} is required.";
        }

        // "dimensions[0] 'Wellbore': attributes.X ..." and "dimensions[0].name ...": the item's place goes, what it says stays.
        var placed = ItemPlace().Match(text);
        if (placed.Success)
        {
            text = text[placed.Length..].TrimStart(':', ' ', '.');
        }

        return new DimensionDraftIssue(DimensionDraftIssue.Error, text, TargetOf(text));
    }

    /// <summary>
    /// The part of the dimension a loader's message is about, as the YAML's spans name it: the key a message starts with
    /// (<c>path</c>, <c>attributes.Country</c>, <c>columns.key</c>), the columns for a message about a column's name, and
    /// null for a message about the whole.
    /// </summary>
    internal static string? TargetOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = TargetPattern().Match(text);
        if (match.Success)
        {
            var target = match.Groups["target"].Value;
            return target.Contains('[', StringComparison.Ordinal) ? target[..target.IndexOf('[', StringComparison.Ordinal)] : target;
        }

        return text.StartsWith("the key's column", StringComparison.Ordinal) ? "columns.key"
            : text.StartsWith("the value's column", StringComparison.Ordinal) ? "columns.value"
            : null;
    }

    private static void Optional(Action<string> line, string indent, string key, string? value)
    {
        if (Trimmed(value) is { } text)
        {
            line(indent + key + ": " + Scalar(text));
        }
    }

    /// <summary>
    /// A label's or an attribute's paths: one as a value, several as a list, an item a line, which keeps a path with a filter
    /// readable. An attribute is written even with no path, so the loader says it reads nothing.
    /// </summary>
    private static void Steps(Action<string> line, string indent, string key, IReadOnlyList<string> steps, string itemIndent, bool always = false)
    {
        switch (steps.Count)
        {
            case 0 when always:
                line(indent + key + ": []");
                break;
            case 0:
                break;
            case 1:
                line(indent + key + ": " + Scalar(steps[0]));
                break;
            default:
                line(indent + key + ":");
                foreach (var step in steps)
                {
                    line(itemIndent + "- " + Scalar(step));
                }

                break;
        }
    }

    /// <summary>The paths of a list, trimmed, with the blank ones left out.</summary>
    private static List<string> Present(IReadOnlyList<string>? steps)
        => (steps ?? []).Select(Trimmed).Where(s => s is not null).Select(s => s!).ToList();

    /// <summary>
    /// A text as a YAML value outside a flow collection: plain where YAML reads it back as the same text (a name, a kind, a
    /// path, words), which is most of what a dimension holds; in single quotes otherwise, as the documentation quotes a path
    /// with a filter; and in double quotes with its escapes written out when it holds a character single quotes cannot carry.
    /// </summary>
    internal static string Scalar(string value) => Plain(value, flow: false) ? value : Quoted(value);

    /// <summary>A text as a YAML value inside a flow collection (<c>{ collect: ... }</c>), where a comma or a bracket is not plain either.</summary>
    internal static string FlowScalar(string value) => Plain(value, flow: true) ? value : Quoted(value);

    /// <summary>
    /// Whether YAML reads <paramref name="value"/> back as the same text written bare: it starts with a letter, a digit or an
    /// underscore, holds only what a plain scalar may (no ": ", no " #", no trailing colon or space, no control character, no
    /// bracket or star, which a path's filter is quoted for, and in a flow collection no comma), and is not a word or a number
    /// YAML reads as something else.
    /// </summary>
    private static bool Plain(string value, bool flow)
    {
        if (value.Length == 0 || !(char.IsAsciiLetterOrDigit(value[0]) || value[0] == '_') || char.IsWhiteSpace(value[^1]) || value[^1] == ':')
        {
            return false;
        }

        foreach (var c in value)
        {
            var allowed = char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '/' or ':' or '@' or '%' or '+' or '(' or ')' or '=' or '$' or ' '
                || (!flow && c == ',');
            if (!allowed)
            {
                return false;
            }
        }

        return !value.Contains(": ", StringComparison.Ordinal)
            && !value.Contains(" #", StringComparison.Ordinal)
            && !value.Contains("  ", StringComparison.Ordinal)
            && !ReservedWords.Contains(value)
            && !NumberLike().IsMatch(value);
    }

    /// <summary>
    /// A text quoted so YAML reads it back exactly: in single quotes, a quote doubled, when it holds no control character;
    /// otherwise in double quotes with every escape written out.
    /// </summary>
    private static string Quoted(string value)
    {
        if (!value.Any(char.IsControl))
        {
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}"),
                _ => c.ToString(),
            });
        }

        return text.Append('"').ToString();
    }

    /// <summary>The words YAML reads as something other than text, written bare: booleans, nulls and the like, in any case.</summary>
    private static readonly HashSet<string> ReservedWords = new(
        ["true", "false", "yes", "no", "y", "n", "on", "off", "null", "nil", "~", ".inf", ".nan"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A plain value YAML might read as a number: digits with signs, points, exponents, underscores or a base prefix.</summary>
    [GeneratedRegex(@"^(?:[-+]?[0-9][0-9_]*(?:\.[0-9_]*)?(?:[eE][-+]?[0-9]+)?|0[xXoObB][0-9A-Fa-f_]+|[-+]?\.(?:inf|nan))$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLike();

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [GeneratedRegex(@"^dimensions\[\d+\](?: '[^']*')?", RegexOptions.CultureInvariant)]
    private static partial Regex ItemPlace();

    [GeneratedRegex(@"^'dimensions\[\d+\]\.(?<key>[A-Za-z]+)' is required\.$", RegexOptions.CultureInvariant)]
    private static partial Regex RequiredKey();

    [GeneratedRegex(
        @"^(?<target>name|description|kind|query|path|label(?:\[\d+\])?|unlabelled|attributes\.[A-Za-z0-9_]+|columns\.(?:key|value)|clean(?:\[\d+\])?|maxValues|countRecords)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex TargetPattern();
}
