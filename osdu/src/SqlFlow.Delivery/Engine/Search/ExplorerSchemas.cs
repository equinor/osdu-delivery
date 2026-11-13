using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>One kind the partition's Schema service lists: its id and the parts of it, its status and its scope.</summary>
/// <param name="Kind">The schema id, <c>authority:source:entityType:major.minor.patch</c>.</param>
/// <param name="Authority">The authority (<c>osdu</c>, or the partition's own).</param>
/// <param name="Source">The source (<c>wks</c>).</param>
/// <param name="EntityType">The entity type: <c>master-data--Wellbore</c>, or a partition's own naming no group (<c>Completion</c>).</param>
/// <param name="Version">The version, <c>major.minor.patch</c>.</param>
/// <param name="Status"><c>PUBLISHED</c>, <c>DEVELOPMENT</c> or <c>OBSOLETE</c>.</param>
/// <param name="Scope"><c>INTERNAL</c> (the partition's own) or <c>SHARED</c> (the platform's).</param>
public sealed record ExplorerSchemaKind(string Kind, string Authority, string Source, string EntityType, string Version, string Status, string Scope);

/// <summary>
/// The kinds a partition's Schema service lists, every status and scope, whether or not any record is of them: what the
/// Templates page's Browse OSDU lists for the partition (osdu/docs/reference/concepts/templates.md).
/// </summary>
/// <param name="State"><c>reading</c> (the first listing is under way), <c>ready</c> or <c>failed</c> (see <paramref name="Problem"/>).</param>
/// <param name="ReadUtc">When the listing answered from was made; null before the first.</param>
/// <param name="Listed">How many schemas the service listed, the abstract ones included.</param>
/// <param name="Kinds">The kinds records can be of, in id order.</param>
/// <param name="Problem">Why no listing could be made.</param>
/// <param name="Notes">What leaves the listing short.</param>
public sealed record ExplorerSchemas(string State, DateTimeOffset? ReadUtc, int Listed, IReadOnlyList<ExplorerSchemaKind> Kinds, string? Problem, IReadOnlyList<string> Notes)
{
    /// <summary>The answer for what the index's listing of the kinds holds now.</summary>
    public static ExplorerSchemas Of(SchemaKindsView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var last = view.Last;
        return new ExplorerSchemas(
            view.State,
            last?.ReadUtc,
            last?.Listed ?? 0,
            last is null
                ? []
                : last.Kinds.Values
                    .OrderBy(k => k.Id, StringComparer.Ordinal)
                    .Select(k => new ExplorerSchemaKind(k.Id, k.Authority, k.Source, k.EntityType, k.Version, k.Status, k.Scope))
                    .ToList(),
            last is null ? view.Problem : null,
            last?.Notes ?? []);
    }
}

/// <summary>A kind's schema as the partition's Schema service holds it, bundled, as the file a repository's template folder keeps.</summary>
/// <param name="Kind">The kind.</param>
/// <param name="Status">Its status as the listing the engine keeps says; null where that holds none of it.</param>
/// <param name="Scope">Its scope likewise.</param>
/// <param name="File">The bundled schema as the file's text, as <c>TemplateSources.FileText</c> writes one, naming <paramref name="Kind"/>.</param>
/// <param name="Read">The schema ids read: the kind's, then each it refers to.</param>
/// <param name="Unresolved">The references left as they were, each with why.</param>
/// <param name="Notes">What the file holds other than the service's text: the kind named in place of the one the schema named.</param>
public sealed partial record ExplorerSchemaFile(
    string Kind, string? Status, string? Scope, string File, IReadOnlyList<string> Read, IReadOnlyList<string> Unresolved, IReadOnlyList<string> Notes)
{
    /// <summary>The longest kind read.</summary>
    public const int MaxKindLength = 256;

    /// <summary>The property a template file names the kind it holds in, as every schema of the OSDU data definitions does.</summary>
    private const string SchemaSource = "x-osdu-schema-source";

    /// <summary>
    /// The file a partition's schema of <paramref name="kind"/> downloads as: the bundled schema, naming in
    /// <c>x-osdu-schema-source</c> the kind the Schema service holds it under, the kind its records carry. A schema a
    /// partition registered may name another there, or none: one registered as <c>acme:wells:Completion:1.0.0</c> may
    /// say <c>acme:wells--Completion:1.0.0</c>, which is no kind. A template file is filed by what it names there, so the
    /// file names the registered id, and a note says what the schema named.
    /// </summary>
    public static ExplorerSchemaFile Of(string kind, SchemaListing? listed, SchemaServiceRead read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(read);
        if (read.Schema is null)
        {
            throw new ArgumentException("The read holds no schema.", nameof(read));
        }

        var root = read.Schema.Root.DeepClone().AsObject();
        var notes = new List<string>();
        var named = root[SchemaSource] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (!string.Equals(named, kind, StringComparison.Ordinal))
        {
            root[SchemaSource] = kind;
            notes.Add(string.IsNullOrWhiteSpace(named)
                ? $"The schema names no kind in {SchemaSource}. The file names {kind}, the id the Schema service holds it under, since a template file is filed by the kind it names there."
                : $"The schema names itself '{named}' in {SchemaSource}, while the Schema service holds it as {kind}, the kind its records carry. The file names {kind}, since a template file is filed by the kind it names there.");
        }

        return new ExplorerSchemaFile(kind, listed?.Status, listed?.Scope, DocumentJson.Indented(root), read.Read, read.Unresolved, notes);
    }

    /// <summary>Why <paramref name="kind"/> is not one schema's id, <c>authority:source:entityType:major.minor.patch</c>; null when it is.</summary>
    public static string? KindProblem(string? kind)
        => kind is { Length: > 0 and <= MaxKindLength } && Pattern().IsMatch(kind)
            ? null
            : $"'{(kind is null ? string.Empty : kind.Length <= 80 ? kind : kind[..80] + "...")}' is not a schema's id: authority:source:entityType:major.minor.patch (acme:wells:Completion:1.0.0).";

    [GeneratedRegex(@"^[\w.-]+:[\w.-]+:[\w.-]+:\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
