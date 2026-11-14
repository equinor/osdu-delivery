using System.Text;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Engine.Dimensions;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// A cached type as the explorer's cache builder holds it while a person picks it
/// (osdu/docs/reference/concepts/explorer-cache-builder.md): every part of a <c>types</c> item searched on OSDU, each as
/// typed or picked and none of them checked yet. The builder writes it as YAML (<see cref="CacheTypeBuilder.ToYaml"/>)
/// and the document loader reads that YAML back, so what the builder offers is exactly what a cache flow would declare.
/// </summary>
public sealed record CacheTypeDraft
{
    /// <summary>The name a mapping reads the type by; blank takes the one the kind gives (<c>Wellbore</c>).</summary>
    public string? Name { get; init; }

    /// <summary>The kind whose records are cached, wildcards allowed per segment.</summary>
    public string? Kind { get; init; }

    /// <summary>A query narrowing the records cached; null caches every record of the kind.</summary>
    public string? Query { get; init; }

    /// <summary>The paths of each record the cache keeps.</summary>
    public IReadOnlyList<CacheTypeDraftField>? Fields { get; init; }

    /// <summary>The values read through the records each cached record references, each under its name.</summary>
    public IReadOnlyList<CacheTypeDraftAttributeSpec>? Attributes { get; init; }

    /// <summary>Whether each record is read whole from storage, for a path the search index does not hold.</summary>
    public bool? FetchRecords { get; init; }

    /// <summary>What a changed value does to the records built from it: <c>approve</c>, or blank for the flow's own.</summary>
    public string? OnChange { get; init; }
}

/// <summary>A field of a draft: the path of the record kept, and the name it is kept under when not the one the path gives.</summary>
public sealed record CacheTypeDraftField(string? Path, string? As);

/// <summary>An attribute of a draft: its name, and the paths it is read through, from the cached record on.</summary>
public sealed record CacheTypeDraftAttributeSpec(string? Name, IReadOnlyList<string>? Steps);

/// <summary>
/// Writes a cached type the builder holds as the item a cache flow lists under <c>types</c>, in the documented style, and
/// reads what the document loader says of it back into issues the builder can point at. As a dimension's builder does
/// (<see cref="DimensionBuilder"/>): the YAML is the builder's whole output, written by the rules the loader reads by and
/// checked by reading it back through the loader (<see cref="Document"/>) rather than by a second set of rules.
/// </summary>
public static partial class CacheTypeBuilder
{
    /// <summary>The flow a draft is read back in to be checked; it reaches nothing, since nothing reads it but the loader.</summary>
    public const string CheckFlowName = "cache-builder";

    /// <summary>Where the loader says a draft's document comes from, at the start of every message it writes about it.</summary>
    public const string CheckSource = DimensionBuilder.CheckSource;

    /// <summary>The indentation of the item under <c>types</c>, as the documentation writes it.</summary>
    private const string Item = "  - ";

    /// <summary>The indentation of the item's keys.</summary>
    private const string Key = "    ";

    /// <summary>The indentation of what a key of the item holds.</summary>
    private const string Inner = "      ";

    /// <summary>
    /// The draft as the item a cache flow lists under <c>types</c>, indented as it sits there, ending with a line break. Every
    /// part present is written, in the order the documentation writes them, and a part left blank is left out; the kind is
    /// written even when blank, so the loader names what is missing; a value is quoted wherever YAML would not read it back
    /// as written.
    /// </summary>
    public static string ToYaml(CacheTypeDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var yaml = new StringBuilder();
        void Line(string text) => yaml.Append(text).Append('\n');

        Line(Item + "kind: " + DimensionBuilder.Scalar(Trimmed(draft.Kind) ?? string.Empty));
        Optional(Line, "name", draft.Name);
        Optional(Line, "query", draft.Query);

        var fields = (draft.Fields ?? []).Where(f => Trimmed(f?.Path) is not null).ToList();
        if (fields.Count > 0)
        {
            Line(Key + "fields:");
            foreach (var field in fields)
            {
                var path = Trimmed(field.Path)!;
                Line(Trimmed(field.As) is { } named
                    ? $"{Inner}- {{ path: {DimensionBuilder.FlowScalar(path)}, as: {DimensionBuilder.FlowScalar(named)} }}"
                    : Inner + "- " + DimensionBuilder.Scalar(path));
            }
        }

        if (draft.FetchRecords == true)
        {
            Line(Key + "fetchRecords: true");
        }

        var attributes = (draft.Attributes ?? []).Where(a => a is not null).ToList();
        if (attributes.Count > 0)
        {
            Line(Key + "attributes:");
            foreach (var attribute in attributes)
            {
                var name = DimensionBuilder.Scalar(Trimmed(attribute.Name) ?? string.Empty);
                var steps = (attribute.Steps ?? []).Select(Trimmed).Where(s => s is not null).Select(s => s!).ToList();
                Line(steps.Count == 1
                    ? Inner + name + ": " + DimensionBuilder.Scalar(steps[0])
                    : Inner + name + ": [" + string.Join(", ", steps.Select(DimensionBuilder.FlowScalar)) + "]");
            }
        }

        Optional(Line, "onChange", draft.OnChange);
        return yaml.ToString();
    }

    /// <summary>
    /// A cache flow document listing <paramref name="item"/> and nothing else of note, which the loader reads to check the
    /// item exactly as a flow's own would be: its source names an endpoint nobody reaches, since the document is only read.
    /// </summary>
    public static string Document(string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return $"flowType: cache\nname: {CheckFlowName}\nsource:\n  endpoint: https://osdu.invalid\ntypes:\n{item}";
    }

    /// <summary>
    /// What is missing from a draft before anything else can be said of it: the kind it caches, and something to keep of
    /// each record. A draft missing one of them is not read further, since the loader stops at the first.
    /// </summary>
    public static IReadOnlyList<DimensionDraftIssue> Incomplete(CacheTypeDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var issues = new List<DimensionDraftIssue>(2);
        if (Trimmed(draft.Kind) is null)
        {
            issues.Add(new DimensionDraftIssue(DimensionDraftIssue.Error, "Pick the kind of the records the cache holds.", "kind"));
        }

        var keeps = (draft.Fields ?? []).Any(f => Trimmed(f?.Path) is not null) || (draft.Attributes ?? []).Any(a => a is not null);
        if (!keeps)
        {
            issues.Add(new DimensionDraftIssue(
                DimensionDraftIssue.Error,
                "Pick what the cache keeps of each record: a value a mapping matches a source value against, such as data.Code, or one it writes. A record's id is always kept.",
                "fields"));
        }

        return issues;
    }

    /// <summary>
    /// What the loader said of a draft's document, as an issue of the builder: its words without the document's name and the
    /// item's place in it, which the builder has no use for, pointed at the part of the type it is about.
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

        // "'types[0].onChange' value 'later' is not one of ...": the key alone is named.
        var quoted = QuotedKey().Match(text);
        if (quoted.Success)
        {
            text = quoted.Groups["key"].Value + text[quoted.Length..];
        }

        // "types[0].kind '...' is not ..." and "types[0]: Cached type 'Wellbore' ...": the item's place goes, what it says stays.
        var placed = ItemPlace().Match(text);
        if (placed.Success)
        {
            text = text[placed.Length..].TrimStart(':', ' ', '.');
        }

        return new DimensionDraftIssue(DimensionDraftIssue.Error, text, TargetOf(text));
    }

    /// <summary>
    /// The part of the type a loader's message is about, as the builder's steps name it: the key a message starts with
    /// (<c>kind</c>, <c>fields</c>, <c>attributes.WellName</c>), the fields for a message about two values under one name,
    /// and null for a message about the whole.
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

        if (text.Contains("needs a kind", StringComparison.Ordinal) || text.Contains("needs an entityType", StringComparison.Ordinal))
        {
            return "kind";
        }

        return text.Contains("under the name", StringComparison.Ordinal) || text.Contains("among its fields", StringComparison.Ordinal) ? "fields" : null;
    }

    private static void Optional(Action<string> line, string key, string? value)
    {
        if (Trimmed(value) is { } text)
        {
            line(Key + key + ": " + DimensionBuilder.Scalar(text));
        }
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [GeneratedRegex(@"^types\[\d+\](?: '[^']*')?", RegexOptions.CultureInvariant)]
    private static partial Regex ItemPlace();

    [GeneratedRegex(@"^'types\[\d+\]\.(?<key>[A-Za-z.]+)'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedKey();

    [GeneratedRegex(
        @"^(?<target>name|kind|query|fields(?:\[\d+\])?|attributes\.[A-Za-z0-9_]+|fetchRecords|onChange|lookupKinds|entityType)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex TargetPattern();
}
