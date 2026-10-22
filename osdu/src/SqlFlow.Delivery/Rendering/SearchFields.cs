using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// The template of the kind a mapping's template is linked to (<see cref="TemplateParent"/>), settled from the template
/// folders on the mapping's path as a kind named alone is: what the mapping's <c>$parent</c> paths are checked against
/// (osdu/docs/reference/flow/mapping-parent.md). <paramref name="Schema"/> is null when it cannot be read, and
/// <paramref name="Problem"/> says why.
/// </summary>
public sealed record ResolvedParent(TemplateParent Link, SchemaSnapshot? Schema, string? Problem);

/// <summary>One search a mapping declares, with how each property its findBy lines compare is indexed.</summary>
/// <param name="Declaration">The search as the mapping declares it.</param>
/// <param name="Fields">Each property compared, by the path the mapping writes, as the searched kind's schema indexes it.</param>
public sealed record ResolvedSearch(MappingSearch Declaration, IReadOnlyDictionary<string, OsduField> Fields);

/// <summary>
/// A mapping's searches resolved against the schemas they pin: for every property a findBy line compares, how the
/// platform indexes it, and so how a query asks for it. What cannot be asked is listed rather than thrown, so a
/// preflight reports every problem at once.
/// </summary>
public sealed class ResolvedSearches
{
    private ResolvedSearches(IReadOnlyDictionary<string, ResolvedSearch> searches, IReadOnlyList<string> problems)
    {
        Searches = searches;
        Problems = problems;
    }

    /// <summary>A mapping that searches nothing.</summary>
    public static ResolvedSearches None { get; } = new(new Dictionary<string, ResolvedSearch>(StringComparer.Ordinal), []);

    /// <summary>The searches that resolved, by the name entries write.</summary>
    public IReadOnlyDictionary<string, ResolvedSearch> Searches { get; }

    /// <summary>Why a search or a property it compares cannot be asked for, one line each, naming the mapping's own words.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>
    /// The template of the kind the mapping's template is linked to, for a mapping that reads its records' parent; null for
    /// one that reads none, or whose template is linked to no parent (which the preflight refuses).
    /// </summary>
    public ResolvedParent? Parent { get; private init; }

    /// <summary>These searches with <paramref name="parent"/>, the parent's template, beside them.</summary>
    public ResolvedSearches WithParent(ResolvedParent parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return new ResolvedSearches(Searches, Problems) { Parent = parent };
    }

    /// <summary>
    /// Resolves <paramref name="mapping"/>'s searches against the schemas in <paramref name="schemas"/>, which holds the
    /// template each search settled on, or lacks it, with why in <c>missing</c>, when it could not be read.
    /// </summary>
    public static ResolvedSearches Resolve(
        MappingDefinition mapping,
        IReadOnlyDictionary<TemplateReference, SchemaSnapshot> schemas,
        IReadOnlyDictionary<TemplateReference, string>? missing = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schemas);
        if (mapping.Searches.Count == 0)
        {
            return None;
        }

        var where = mapping.SourcePath ?? mapping.Reference;
        var problems = new List<string>();
        var resolved = new Dictionary<string, ResolvedSearch>(StringComparer.Ordinal);
        foreach (var (name, search) in mapping.Searches.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!schemas.TryGetValue(search.Schema, out var schema))
            {
                problems.Add(missing is not null && missing.TryGetValue(search.Schema, out var reason)
                    ? $"{where}: search '{name}' cannot read the template it pins, {search.Schema}: {reason}"
                    : $"{where}: search '{name}' cannot read the template it pins, {search.Schema}.");
                continue;
            }

            var fields = new Dictionary<string, OsduField>(StringComparer.Ordinal);
            var compared = mapping.Entries
                .SelectMany(e => e.ValueNodes)
                .Where(e => e.Source?.Kind == MappingSourceKind.Search && string.Equals(e.Source.CacheType, name, StringComparison.Ordinal))
                .SelectMany(e => e.FindBy.Select(f => (Entry: e, f.Field)));
            foreach (var (entry, path) in compared)
            {
                if (fields.ContainsKey(path))
                {
                    continue;
                }

                var (field, problem) = SearchFields.Classify(schema, path);
                if (field is null)
                {
                    problems.Add($"{where}: {entry.Where} compares search.{name}.{path}, which cannot be searched: {problem}");
                    continue;
                }

                fields[path] = field;
            }

            resolved[name] = new ResolvedSearch(search, fields);
        }

        return new ResolvedSearches(resolved, problems);
    }

    /// <summary>How the search <paramref name="name"/> compares <paramref name="path"/>, when both resolved.</summary>
    public bool TryField(string name, string path, out ResolvedSearch? search, out OsduField? field)
    {
        field = null;
        return Searches.TryGetValue(name, out search) && search.Fields.TryGetValue(path, out field);
    }
}

/// <summary>
/// How the platform indexes a property of a kind: a property of <c>data</c> read from that kind's schema the way the indexer
/// reads it (<c>PropertiesProcessor</c>, <c>TypeMapper</c>, <c>SchemaConverterPropertiesConfig</c>), and a property of the
/// record itself from the indexer's own mapping of it (<c>TypeMapper.metaAttributeIndexerType</c>), so a query asks for what
/// was actually stored.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// A string is indexed as text with a <c>keyword</c> sub-field, unless its pattern starts <c>^srn</c>, which makes it a
/// link and a bare keyword, or its format is <c>date-time</c>, <c>date</c> or <c>time</c>, which makes it a date, or
/// <c>int32</c>, <c>integer</c> or <c>int64</c>, which makes it a number. Any other format is indexed as text. An OSDU id
/// reference is text: its pattern does not start <c>^srn</c>. A number, an integer and a boolean are indexed as what they
/// are.
/// </description></item>
/// <item><description>
/// A list of values is typed by its items' pattern and type alone; the items' format is not read.
/// </description></item>
/// <item><description>
/// An object's properties are indexed under dotted paths. An array of objects is indexed by its
/// <c>x-osdu-indexing</c> hint: <c>nested</c> keeps each object whole for a nested query, <c>flattened</c> stores every
/// value inside it as a keyword, and with no hint the array is mapped as an object whose properties are not indexed at
/// all, since every index is created with <c>dynamic: false</c>.
/// </description></item>
/// </list>
/// </remarks>
public static class SearchFields
{
    /// <summary>What the indexer reads at the start of a pattern to type a string as a link.</summary>
    private const string LinkPatternPrefix = "^srn";

    private const string IndexingHint = "x-osdu-indexing";

    /// <summary>The root of a record's own data, whose properties the kind's schema describes.</summary>
    public const string DataRoot = "data";

    /// <summary>
    /// The record's own properties the indexer maps, with how (<c>TypeMapper.metaAttributeIndexerType</c> and the ACL, legal,
    /// ancestry and index status mappings beside it) and whether a record holds several: <c>authority</c> and <c>source</c>
    /// are constant keywords, <c>version</c> a long, the two times dates, and the rest keywords. <c>tags</c> is a flattened
    /// map, reached one tag at a time (<see cref="TagsRoot"/>).
    /// </summary>
    private static readonly Dictionary<string, (OsduFieldIndex Index, bool Repeats)> RecordProperties = new(StringComparer.Ordinal)
    {
        ["id"] = (OsduFieldIndex.Keyword, false),
        ["kind"] = (OsduFieldIndex.Keyword, false),
        ["type"] = (OsduFieldIndex.Keyword, false),
        ["namespace"] = (OsduFieldIndex.Keyword, false),
        ["authority"] = (OsduFieldIndex.Keyword, false),
        ["source"] = (OsduFieldIndex.Keyword, false),
        ["version"] = (OsduFieldIndex.Number, false),
        ["createUser"] = (OsduFieldIndex.Keyword, false),
        ["modifyUser"] = (OsduFieldIndex.Keyword, false),
        ["createTime"] = (OsduFieldIndex.Date, false),
        ["modifyTime"] = (OsduFieldIndex.Date, false),
        ["collaborationId"] = (OsduFieldIndex.Keyword, false),
        ["acl.viewers"] = (OsduFieldIndex.Keyword, true),
        ["acl.owners"] = (OsduFieldIndex.Keyword, true),
        ["legal.legaltags"] = (OsduFieldIndex.Keyword, true),
        ["legal.otherRelevantDataCountries"] = (OsduFieldIndex.Keyword, true),
        ["legal.status"] = (OsduFieldIndex.Keyword, false),
        ["ancestry.parents"] = (OsduFieldIndex.Keyword, true),
        ["index.statusCode"] = (OsduFieldIndex.Number, false),
    };

    /// <summary>The record's tags: a flattened map, each tag a keyword under <c>tags.&lt;name&gt;</c>.</summary>
    public const string TagsRoot = "tags";

    /// <summary>
    /// How <paramref name="schema"/> has the platform index <paramref name="path"/> for an exact comparison of text, or why a
    /// search cannot compare it: text or a keyword, since a lookup compares text.
    /// </summary>
    public static (OsduField? Field, string? Problem) Classify(SchemaSnapshot schema, string path)
    {
        var shape = Walk(schema, path, typed: false);
        return (shape.Field, shape.Problem);
    }

    /// <summary>
    /// How <paramref name="schema"/> has the platform index <paramref name="path"/> for reading its values, which a number, a
    /// boolean and a date may be as well as text and a keyword, and whether one record can hold several of them; or why the
    /// index holds no value there that a query could match exactly.
    /// </summary>
    public static IndexedShape ClassifyValue(SchemaSnapshot schema, string path) => Walk(schema, path, typed: true);

    /// <summary>
    /// How the platform indexes <paramref name="path"/>, a property of the record itself rather than of its data, which the
    /// indexer maps the same way for every kind; or why it holds no value a query could match exactly.
    /// </summary>
    public static IndexedShape RecordProperty(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OsduPath.IsPath(path))
        {
            return IndexedShape.Refused($"'{path}' is not a property path a query can name.");
        }

        if (IsDataPath(path))
        {
            return IndexedShape.Refused($"{path} is a property of the record's data, which the kind's schema describes, not the record itself.");
        }

        if (RecordProperties.TryGetValue(path, out var known))
        {
            return new IndexedShape(OsduField.Of(path, known.Index), known.Repeats, null);
        }

        var segments = path.Split('.');
        if (segments[0] == TagsRoot)
        {
            // A tag's value is a keyword of the flattened map under its own name; the map as a whole, or a path into a tag, is
            // not one value.
            return segments.Length == 2
                ? new IndexedShape(OsduField.Keyword(path), false, null)
                : IndexedShape.Refused(segments.Length == 1
                    ? "tags is a map of named values, not a value; name one tag, such as tags.Source."
                    : $"{path} reaches inside the tag {segments[1]}, whose value is one text; name the tag itself.");
        }

        var under = RecordProperties.Keys.Where(k => k.StartsWith(segments[0] + ".", StringComparison.Ordinal)).ToList();
        return IndexedShape.Refused(under.Count > 0
            ? $"{path} is not a value the index holds of the record; under {segments[0]} it holds {string.Join(", ", under)}."
            : $"{path} is not a property the index holds of the record. A record's own values are {string.Join(", ", RecordProperties.Keys.Where(k => !k.Contains('.', StringComparison.Ordinal)))}, the ACL, legal, ancestry and index values under them, and a tag as tags.<name>; its data is under data.");
    }

    /// <summary>Whether <paramref name="path"/> names a property of the record's data, which its kind's schema describes.</summary>
    public static bool IsDataPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.StartsWith(DataRoot + ".", StringComparison.Ordinal);
    }

    /// <summary>The walk both classifications share: down the path's objects and arrays to the leaf, and the leaf's type.</summary>
    private static IndexedShape Walk(SchemaSnapshot schema, string path, bool typed)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OsduPath.IsPath(path))
        {
            return IndexedShape.Refused($"'{path}' is not a property path a query can name.");
        }

        var segments = path.Split('.');
        var (nested, flattened, problem) = Through(schema, segments, segments.Length - 1);
        if (problem is not null)
        {
            return IndexedShape.Refused(problem);
        }

        var leaf = schema.ResolveThroughForms(path);
        if (leaf is null)
        {
            return IndexedShape.Refused($"the schema of {schema.Kind} has no property {path}.");
        }

        var (index, list, leafProblem) = flattened is not null ? FlattenedLeaf(schema, leaf, path) : typed ? TypedLeaf(schema, leaf, path) : Leaf(schema, leaf, path);
        if (index is not { } shape)
        {
            return IndexedShape.Refused(leafProblem!);
        }

        return new IndexedShape(OsduField.Of(path, shape, nested), nested is not null || flattened is not null || list, null);
    }

    /// <summary>
    /// How the platform indexes a section of a record's data at <paramref name="path"/> (an object, or a list of objects or of
    /// values): the nested array it is or sits in, the flattened array it is or sits in, or why no query reaches it (an array
    /// of objects the schema gives no indexing hint, which the platform does not index inside).
    /// </summary>
    public static SectionShape Section(SchemaSnapshot schema, string path)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OsduPath.IsPath(path))
        {
            return new SectionShape(null, null, $"'{path}' is not a property path a query can name.");
        }

        var segments = path.Split('.');
        var (nested, flattened, problem) = Through(schema, segments, segments.Length - 1);
        if (problem is not null)
        {
            return new SectionShape(null, null, problem);
        }

        var section = schema.ResolveThroughForms(path);
        if (section is null)
        {
            return new SectionShape(null, null, $"the schema of {schema.Kind} has no property {path}.");
        }

        if (flattened is not null || section.Type == SchemaType.Object)
        {
            return new SectionShape(nested, flattened, null);
        }

        if (section.Type != SchemaType.Array)
        {
            return new SectionShape(nested, flattened, $"{path} is a {Name(section.Type)} in the schema of {schema.Kind}, a value rather than a section.");
        }

        if (!IsObjectItems(schema, section))
        {
            return new SectionShape(nested, null, null);
        }

        return Hint(section) switch
        {
            "nested" when nested is not null => new SectionShape(null, null,
                $"{path} is a nested array inside the nested array {nested}; a query reaches into one nested array, since the search service rewrites the property names inside a nested query by pattern and a query nested twice does not survive that."),
            "nested" => new SectionShape(path, null, null),
            "flattened" => new SectionShape(nested, path, null),
            _ => new SectionShape(null, null,
                $"{path} is an array of objects the schema of {schema.Kind} gives no x-osdu-indexing hint, and the platform maps such an array as an object whose properties it does not index, so no query reaches inside it."),
        };
    }

    /// <summary>
    /// Down the first <paramref name="count"/> segments of a path in <paramref name="schema"/>: the nested array met, the
    /// flattened array met, or why the path goes where no query reaches.
    /// </summary>
    private static (string? Nested, string? Flattened, string? Problem) Through(SchemaSnapshot schema, string[] segments, int count)
    {
        string? nested = null;
        string? flattened = null;
        for (var i = 1; i <= count; i++)
        {
            var prefix = string.Join('.', segments.Take(i));
            var property = schema.ResolveThroughForms(prefix);
            if (property is null)
            {
                return (null, null, $"the schema of {schema.Kind} has no property {prefix}.");
            }

            if (flattened is not null)
            {
                // Inside a flattened array every value is a keyword under its dotted path, however deep.
                continue;
            }

            if (property.Type == SchemaType.Array)
            {
                if (!IsObjectItems(schema, property))
                {
                    return (null, null, $"{prefix} in the schema of {schema.Kind} is a list of values, which has no properties to compare.");
                }

                switch (Hint(property))
                {
                    case "nested" when nested is not null:
                        return (null, null,
                            $"{prefix} is a nested array inside the nested array {nested}; a query reaches into one nested array, since the search service rewrites the property names inside a nested query by pattern and a query nested twice does not survive that.");
                    case "nested":
                        nested = prefix;
                        break;
                    case "flattened":
                        flattened = prefix;
                        break;
                    default:
                        return (null, null,
                            $"{prefix} is an array of objects the schema of {schema.Kind} gives no x-osdu-indexing hint, and the platform maps such an array as an object whose properties it does not index, so no query reaches inside it.");
                }

                continue;
            }

            if (property.Type != SchemaType.Object)
            {
                return (null, null, $"{prefix} in the schema of {schema.Kind} is a {Name(property.Type)}, which has no properties to compare.");
            }
        }

        return (nested, flattened, null);
    }

    /// <summary>How a property outside any flattened array is indexed for a text comparison, by the indexer's order: pattern, then format, then type.</summary>
    private static (OsduFieldIndex? Index, bool List, string? Problem) Leaf(SchemaSnapshot schema, SchemaProperty leaf, string path)
    {
        var node = leaf.Schema;
        var type = leaf.Type;
        if (type == SchemaType.Array)
        {
            if (leaf.Items is not { } items || IsObjectItems(schema, leaf))
            {
                return (null, true, $"{path} is a list of objects, not a value; compare a property of the objects in it.");
            }

            // A list is typed by its items' pattern, then their type; their format is not read.
            if (IsLinkList(leaf, items))
            {
                return (OsduFieldIndex.Keyword, true, null);
            }

            return SchemaSnapshot.TypeOfNode(items) == SchemaType.String
                ? (OsduFieldIndex.Text, true, null)
                : (null, true, $"{path} is a list of {Name(SchemaSnapshot.TypeOfNode(items))} values, and a search compares text.");
        }

        if (type == SchemaType.Object)
        {
            return (null, false, $"{path} is an object, not a value; compare one of its properties.");
        }

        if (Pattern(node) is { } pattern && pattern.StartsWith(LinkPatternPrefix, StringComparison.Ordinal))
        {
            return (OsduFieldIndex.Keyword, false, null);
        }

        if (leaf.Format is { } format)
        {
            switch (format)
            {
                case "date-time" or "date" or "time":
                    return (null, false, $"{path} is a {format} string, which the platform indexes as a date rather than as text, so a search cannot compare it exactly.");
                case "int32" or "integer" or "int64":
                    return (null, false, $"{path} is a string of format {format}, which the platform indexes as a number, and a search compares text.");
            }
        }

        return type switch
        {
            SchemaType.String => (OsduFieldIndex.Text, false, null),
            SchemaType.Any => (null, false, $"{path} declares no type in the schema, and the platform does not index a property without one."),
            _ => (null, false, $"{path} is a {Name(type)}, and a search compares text."),
        };
    }

    /// <summary>How a property outside any flattened array is indexed for reading its values: as <see cref="Leaf"/> types it, and numbers, booleans and dates as what they are.</summary>
    private static (OsduFieldIndex? Index, bool List, string? Problem) TypedLeaf(SchemaSnapshot schema, SchemaProperty leaf, string path)
    {
        var node = leaf.Schema;
        var type = leaf.Type;
        if (type == SchemaType.Array)
        {
            if (leaf.Items is not { } items || IsObjectItems(schema, leaf))
            {
                return (null, true, $"{path} is a list of objects, not a value; name a property of the objects in it.");
            }

            if (IsLinkList(leaf, items))
            {
                return (OsduFieldIndex.Keyword, true, null);
            }

            var itemType = SchemaSnapshot.TypeOfNode(items);
            return Scalar(itemType) is { } listed
                ? (listed, true, null)
                : (null, true, $"{path} is a list of {Name(itemType)} values, which the platform does not index.");
        }

        if (type == SchemaType.Object)
        {
            return (null, false, $"{path} is an object, not a value; name one of its properties.");
        }

        if (Pattern(node) is { } pattern && pattern.StartsWith(LinkPatternPrefix, StringComparison.Ordinal))
        {
            return (OsduFieldIndex.Keyword, false, null);
        }

        if (type == SchemaType.String && leaf.Format is { } format)
        {
            switch (format)
            {
                case "date-time" or "date" or "time":
                    return (OsduFieldIndex.Date, false, null);
                case "int32" or "integer" or "int64":
                    return (OsduFieldIndex.Number, false, null);
            }
        }

        return Scalar(type) is { } scalar
            ? (scalar, false, null)
            : (null, false, type == SchemaType.Any
                ? $"{path} declares no type in the schema, and the platform does not index a property without one."
                : $"{path} is a {Name(type)}, which the platform does not index as a value.");
    }

    /// <summary>How the indexer stores a scalar of a schema type, or null for one it does not store as a value.</summary>
    private static OsduFieldIndex? Scalar(SchemaType type) => type switch
    {
        SchemaType.String => OsduFieldIndex.Text,
        SchemaType.Number or SchemaType.Integer => OsduFieldIndex.Number,
        SchemaType.Boolean => OsduFieldIndex.Boolean,
        _ => null,
    };

    /// <summary>A value inside a flattened array: every value is a keyword, and only an object or a list of objects has none.</summary>
    private static (OsduFieldIndex? Index, bool List, string? Problem) FlattenedLeaf(SchemaSnapshot schema, SchemaProperty leaf, string path)
        => leaf.Type == SchemaType.Object || (leaf.Type == SchemaType.Array && IsObjectItems(schema, leaf))
            ? (null, true, $"{path} is an object inside a flattened array, not a value; compare one of its properties.")
            : (OsduFieldIndex.Keyword, true, null);

    /// <summary>Whether a list's pattern, or its items', starts <c>^srn</c>, which types it as a list of links.</summary>
    private static bool IsLinkList(SchemaProperty list, JsonObject items)
        => (Pattern(list.Schema) is { } listPattern && listPattern.StartsWith(LinkPatternPrefix, StringComparison.Ordinal))
            || (Pattern(items) is { } itemPattern && itemPattern.StartsWith(LinkPatternPrefix, StringComparison.Ordinal));

    /// <summary>Whether an array's items are objects the indexer walks into: a schema with a reference, branches or properties.</summary>
    /// <summary>Whether an array holds objects: items declaring properties, or of type object, or a choice of forms that are all objects.</summary>
    private static bool IsObjectItems(SchemaSnapshot schema, SchemaProperty array)
        => array.Items is { } items
            && (items["properties"] is JsonObject || schema.TypeWithForms(items) == SchemaType.Object);

    private static string? Hint(SchemaProperty property)
        => property.Schema[IndexingHint] is JsonObject hint && hint["type"] is JsonValue value && value.TryGetValue<string>(out var type)
            ? type
            : null;

    private static string? Pattern(JsonObject node)
        => node["pattern"] is JsonValue value && value.TryGetValue<string>(out var pattern) ? pattern : null;

    private static string Name(SchemaType type) => type switch
    {
        SchemaType.Number => "number",
        SchemaType.Integer => "integer",
        SchemaType.Boolean => "boolean",
        SchemaType.Object => "object",
        SchemaType.Array => "list",
        SchemaType.String => "string",
        _ => "value without a type",
    };
}

/// <summary>
/// How the index holds a property: the field a query asks, and whether one record can hold several values of it (a list, or
/// a property inside an array of objects); or why the index holds no value there a query could match exactly.
/// </summary>
public readonly record struct IndexedShape(OsduField? Field, bool Repeats, string? Problem)
{
    public static IndexedShape Refused(string problem) => new(null, false, problem);
}

/// <summary>
/// How the index holds a section of a record's data: the nested array it is or sits in (a query reaches it inside
/// <c>nested(...)</c>), the flattened array it is or sits in (its values are keywords under their dotted paths), or why no
/// query reaches it.
/// </summary>
public readonly record struct SectionShape(string? NestedPath, string? FlattenedPath, string? Problem);
