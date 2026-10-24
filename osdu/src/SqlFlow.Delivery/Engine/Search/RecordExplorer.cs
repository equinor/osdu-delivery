using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>How the explorer orders a page of records.</summary>
public enum ExplorerSort
{
    /// <summary>The order the search service gives: the best match first for a search by text, the index's own otherwise.</summary>
    Relevance,

    /// <summary>The record changed last first (<c>modifyTime</c>, newest first).</summary>
    Modified,

    /// <summary>The record created last first (<c>createTime</c>, newest first).</summary>
    Created,
}

/// <summary>What a condition of the explorer asks of a property.</summary>
public enum ExplorerCondition
{
    /// <summary>The property holds exactly the value, whole (<see cref="OsduQuery.Equal"/>).</summary>
    Is,

    /// <summary>The property does not hold the value: every record but those that do.</summary>
    IsNot,

    /// <summary>The property holds one of the values, each compared whole (<see cref="OsduQuery.AnyOf"/>).</summary>
    AnyOf,

    /// <summary>The property's text holds the words as a phrase, their case aside (<see cref="OsduQuery.Words"/>).</summary>
    Contains,

    /// <summary>The property's whole value starts with the text, case included (<see cref="OsduQuery.Prefix"/>).</summary>
    StartsWith,

    /// <summary>The property holds a value from <c>value</c> (included) up to <c>to</c> (left out); either end may be open (<see cref="OsduQuery.Range"/>).</summary>
    Range,

    /// <summary>The property holds a value (<see cref="OsduQuery.Exists"/>).</summary>
    Exists,

    /// <summary>The property holds no value: every record but those that hold one.</summary>
    Missing,

    /// <summary>The property holds none of the values: every record but those holding one of them.</summary>
    NoneOf,
}

/// <summary>
/// One condition a page of the explorer narrows to: a property of the record, asked the way the platform indexes it, and
/// what it must (or must not) hold. A value picked from the property's groups is the condition <see cref="ExplorerCondition.Is"/>,
/// which finds exactly the records counted under it. Every comparison is written by <see cref="OsduQuery"/>, as the module's
/// own lookups and filters are.
/// </summary>
public sealed record ExplorerFilter
{
    /// <summary>The most values one condition compares at once: far inside the 1,024 clauses the service allows in a query.</summary>
    public const int MaxValues = 50;

    /// <summary>The property's path from the record root: <c>data.FacilityTypeID</c>, <c>legal.legaltags</c>.</summary>
    public required string Path { get; init; }

    /// <summary>How the platform indexes the property.</summary>
    public OsduFieldIndex Index { get; init; } = OsduFieldIndex.Text;

    /// <summary>The nested array the property sits in (<c>data.GeoContexts</c>), which the query reaches through; null for none.</summary>
    public string? Nested { get; init; }

    /// <summary>What the property must hold; <see cref="ExplorerCondition.Is"/> when left out.</summary>
    public ExplorerCondition Condition { get; init; }

    /// <summary>
    /// The value compared: the whole value (<see cref="ExplorerCondition.Is"/>, <see cref="ExplorerCondition.IsNot"/>), the
    /// words, the start, or the lower bound of a range. Not read by <see cref="ExplorerCondition.Exists"/> and
    /// <see cref="ExplorerCondition.Missing"/>.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>The values <see cref="ExplorerCondition.AnyOf"/> compares, at most <see cref="MaxValues"/>.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>The upper bound of a <see cref="ExplorerCondition.Range"/>, left out of it; null for a range open at the top.</summary>
    public string? To { get; init; }

    /// <summary>
    /// For a condition on a search term that reaches the record through other records
    /// (osdu/docs/reference/concepts/search-terms.md): the records its values find, or the record ids its key makes,
    /// which the node reads from the platform before it asks the condition. The condition then compares
    /// <see cref="Path"/> with what was found. Null for a condition asked as it is.
    /// </summary>
    public ExplorerVia? Via { get; init; }

    /// <summary>
    /// Whether the condition keeps the records that do not match its comparison: <see cref="ExplorerCondition.IsNot"/>,
    /// <see cref="ExplorerCondition.NoneOf"/> and <see cref="ExplorerCondition.Missing"/>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Excludes => Condition is ExplorerCondition.IsNot or ExplorerCondition.NoneOf or ExplorerCondition.Missing;

    /// <summary>
    /// The comparison as the search service reads it: what a record must hold to match it, or for a condition that
    /// <see cref="Excludes"/>, what it must not.
    /// </summary>
    /// <exception cref="OsduQueryException">The condition cannot be asked as given; the message says why.</exception>
    public OsduQuery Query()
    {
        if (Via is not null)
        {
            throw new OsduQueryException($"the condition on {Via.Term ?? Path} is read through other records first, so it is asked once they are read.");
        }

        var field = OsduField.Of(Path, Index, Nested);
        return Condition switch
        {
            ExplorerCondition.Is or ExplorerCondition.IsNot => OsduQuery.Equal(field, Required()),
            ExplorerCondition.AnyOf or ExplorerCondition.NoneOf => OsduQuery.AnyOf(field, Listed()),
            ExplorerCondition.Contains => OsduQuery.Words(field, Required()),
            ExplorerCondition.StartsWith => OsduQuery.Prefix(field, Required()),
            ExplorerCondition.Range when Index == OsduFieldIndex.Boolean
                => throw new OsduQueryException($"'{Path}' is indexed as a boolean, which is true or false: ask whether it is one rather than for a range."),
            ExplorerCondition.Range => OsduQuery.Range(field, Bound(Value), Bound(To)),
            ExplorerCondition.Exists or ExplorerCondition.Missing => OsduQuery.Exists(field),
            _ => throw new OsduQueryException($"'{Condition}' is not a condition the explorer asks."),
        };
    }

    /// <summary>
    /// The condition as one clause a page ANDs with the others: its comparison, grouped where it holds several, and preceded
    /// by <c>NOT</c> for a condition that <see cref="Excludes"/>.
    /// </summary>
    /// <exception cref="OsduQueryException">The condition cannot be asked as given.</exception>
    public string Clause()
    {
        var query = Query().Text;
        return Excludes ? $"NOT ({query})" : Condition == ExplorerCondition.AnyOf ? $"({query})" : query;
    }

    /// <summary>The values the condition compares, as given: the list, else the one value; none for a condition that compares none.</summary>
    internal IReadOnlyList<string> Compared() => Values ?? (Value is null ? [] : [Value]);

    private string Required() => Value ?? throw new OsduQueryException($"'{Path}' is compared with no value; name the value the condition compares.");

    private IReadOnlyList<string> Listed()
    {
        var values = Compared();
        return values.Count > MaxValues
            ? throw new OsduQueryException($"'{Path}' is compared with {values.Count} values; a condition compares at most {MaxValues} at once.")
            : values;
    }

    private static string? Bound(string? bound) => string.IsNullOrEmpty(bound) ? null : bound;
}

/// <summary>A property the explorer groups records by: its path from the record root, how the platform indexes it, and the nested array it sits in.</summary>
public sealed record ExplorerField
{
    public required string Path { get; init; }

    public OsduFieldIndex Index { get; init; } = OsduFieldIndex.Text;

    /// <summary>The nested array the property sits in, which the grouping reaches through; null for none.</summary>
    public string? Nested { get; init; }

    /// <summary>The value the search's <c>aggregateBy</c> names the property by.</summary>
    /// <exception cref="OsduQueryException">The path cannot be written in a query.</exception>
    public string AggregateBy() => OsduField.Of(Path, Index, Nested).AggregateBy;
}

/// <summary>
/// What a reader asks the explorer for: what they typed (read as an id, the start of one, or text; or a Lucene query as
/// written), or the id whose mentions they follow; the kind it is asked of; the property values it narrows to; the order;
/// the page; and the property whose distinct values it groups the records by. The control plane checks it before it queues
/// it, and the node checks it again when it reads it from the task, since the task's arguments cross a trust boundary.
/// </summary>
public sealed record ExplorerSearch
{
    /// <summary>The longest text a reader may type: far past any id or name, and short enough that no query is a payload.</summary>
    public const int MaxTextLength = 2000;

    /// <summary>The most property values a page narrows to at once.</summary>
    public const int MaxFilters = 12;

    /// <summary>The rows a page reads when it names no size.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The most rows one page reads.</summary>
    public const int MaxLimit = 200;

    /// <summary>The longest id whose mentions a reader follows; storage ids are far shorter.</summary>
    public const int MaxIdLength = 1024;

    /// <summary>The longest kind or kind pattern a page is asked of.</summary>
    public const int MaxKindLength = 256;

    /// <summary>The most properties a page shows as columns beside each record.</summary>
    public const int MaxColumns = 8;

    internal static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>What the reader typed; null or blank for every record of the kind.</summary>
    public string? Text { get; init; }

    /// <summary>Whether <see cref="Text"/> is a Lucene query, sent as it was written.</summary>
    public bool Lucene { get; init; }

    /// <summary>
    /// The id whose mentions the page lists instead of a text: every record one of whose values names it, the record itself
    /// left out. Null for a page of a text.
    /// </summary>
    public string? Mentions { get; init; }

    /// <summary>The kind asked, <c>authority:source:entityType:version</c> with wildcards per segment; null for every kind.</summary>
    public string? Kind { get; init; }

    /// <summary>The property values every record of the page holds.</summary>
    public IReadOnlyList<ExplorerFilter> Filters { get; init; } = [];

    public ExplorerSort Sort { get; init; }

    /// <summary>Where the page starts among the records the query matches.</summary>
    public int Offset { get; init; }

    /// <summary>How many records the page holds at most.</summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>The property whose distinct values the records are grouped by, with their counts; null for none.</summary>
    public ExplorerField? Facet { get; init; }

    /// <summary>The properties whose values each record of the page carries (<see cref="ExplorerHit.Values"/>), as columns beside it.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Why the search cannot be asked, in words a reader acts on; null when it can.</summary>
    public string? Problem()
    {
        if (Text is { } text)
        {
            if (text.Length > MaxTextLength)
            {
                return $"The search is {text.Length} characters long; it takes at most {MaxTextLength}.";
            }

            // A Lucene query pasted over several lines keeps its line breaks; nothing else that cannot be printed survives a query.
            if (text.Any(c => char.IsControl(c) && !(Lucene && c is '\r' or '\n' or '\t')))
            {
                return "The search holds a character that cannot be printed; type it again.";
            }
        }

        if (Mentions is { } mentioned)
        {
            if (!string.IsNullOrWhiteSpace(Text))
            {
                return "A page lists either what a text finds or what mentions a record, not both.";
            }

            var id = mentioned.Trim();
            if (id.Length > MaxIdLength || !TargetId.IsRecordReference(id) || id.Any(char.IsControl))
            {
                return $"'{Clip(id)}' is not an OSDU record id: a partition, an entity type such as master-data--Wellbore, and a unique part, separated by colons.";
            }
        }

        if (Kind is { } kind && ExplorerKinds.Problem(kind) is { } wrongKind)
        {
            return wrongKind;
        }

        if (Filters is null)
        {
            return "The values a page narrows to are a list.";
        }

        if (Filters.Count > MaxFilters)
        {
            return $"A page narrows to at most {MaxFilters} values at once; {Filters.Count} were asked.";
        }

        foreach (var filter in Filters)
        {
            if (filter is null || filter.Path is null || (filter.Values is { } values && values.Any(v => v is null)))
            {
                return "Every condition a page narrows to names its property, and every value it compares is a text.";
            }

            if (!Enum.IsDefined(filter.Condition))
            {
                return "A condition is one of is, isNot, anyOf, noneOf, contains, startsWith, range, exists or missing.";
            }

            try
            {
                _ = filter.Via is { } via ? via.Check(filter) : filter.Clause();
            }
            catch (OsduQueryException ex)
            {
                return $"The page cannot narrow to {Describe(filter)}: {ex.Message}";
            }
        }

        if (Columns is null)
        {
            return "The columns a page shows are a list.";
        }

        if (Columns.Count > MaxColumns)
        {
            return $"A page shows at most {MaxColumns} properties as columns; {Columns.Count} were asked.";
        }

        if (Columns.FirstOrDefault(c => !OsduPath.IsPath(c)) is { } column)
        {
            return $"'{Clip(column ?? string.Empty)}' is not a property path a page can show, such as data.FacilityName.";
        }

        if (Facet is { } facet)
        {
            try
            {
                _ = facet.AggregateBy();
            }
            catch (OsduQueryException ex)
            {
                return $"The records cannot be grouped by '{facet.Path}': {ex.Message}";
            }
        }

        if (!Enum.IsDefined(Sort))
        {
            return "Order the records by relevance, by when they last changed, or by when they were created.";
        }

        if (Offset < 0 || Limit < 1 || Limit > MaxLimit)
        {
            return $"A page starts at 0 or later and holds 1 to {MaxLimit} records.";
        }

        return Offset + Limit > OsduSearch.MaxWindow
            ? $"The search service pages through the first {OsduSearch.MaxWindow.ToString("N0", CultureInfo.InvariantCulture)} records a query matches; narrow the search to reach the others."
            : null;
    }

    /// <summary>The search as a task carries it.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, WireOptions);

    /// <summary>The search a task carries, checked as the control plane checked it.</summary>
    /// <exception cref="DeliveryException">The text is not a search, or the search cannot be asked.</exception>
    public static ExplorerSearch Parse(string? json)
    {
        ExplorerSearch? search;
        try
        {
            search = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ExplorerSearch>(json, WireOptions);
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"The task's search is not one the explorer reads: {ex.Message}", ex);
        }

        if (search is null)
        {
            throw new DeliveryException("The task carries no search.");
        }

        return search.Problem() is { } problem ? throw new DeliveryException(problem) : search;
    }

    private static string Clip(string text) => text.Length <= 80 ? text : text[..80] + "...";

    /// <summary>A condition as a message names it: its property, and what it compares.</summary>
    private static string Describe(ExplorerFilter filter)
    {
        var values = filter.Values is { Count: > 0 } listed ? string.Join(", ", listed.Take(3).Select(v => $"'{Clip(v)}'")) + (listed.Count > 3 ? ", ..." : string.Empty) : null;
        return filter.Condition switch
        {
            ExplorerCondition.Exists => $"{filter.Path} holding a value",
            ExplorerCondition.Missing => $"{filter.Path} holding no value",
            ExplorerCondition.AnyOf => $"{filter.Via?.Term ?? filter.Path} one of {values ?? $"'{Clip(filter.Value ?? string.Empty)}'"}",
            ExplorerCondition.NoneOf => $"{filter.Via?.Term ?? filter.Path} none of {values ?? $"'{Clip(filter.Value ?? string.Empty)}'"}",
            ExplorerCondition.Range => $"{filter.Path} from '{Clip(filter.Value ?? "*")}' up to '{Clip(filter.To ?? "*")}'",
            _ => $"{filter.Via?.Term ?? filter.Path} {filter.Condition} '{Clip(filter.Value ?? string.Empty)}'",
        };
    }
}

/// <summary>The kinds the explorer is asked of, as the search service's <c>kind</c> accepts them.</summary>
public static partial class ExplorerKinds
{
    /// <summary>Every kind of every record the partition holds.</summary>
    public const string Any = "*:*:*:*";

    /// <summary>Why <paramref name="kind"/> is not a kind the search accepts; null when it is.</summary>
    public static string? Problem(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return kind.Length <= ExplorerSearch.MaxKindLength && Pattern().IsMatch(kind)
            ? null
            : $"'{(kind.Length <= 80 ? kind : kind[..80] + "...")}' is not a kind: authority:source:entityType:version, a star standing for any part (osdu:wks:master-data--Wellbore:1.*).";
    }

    /// <summary>
    /// The entity type a kind names when it names exactly one (<c>master-data--Wellbore</c> of
    /// <c>*:*:master-data--Wellbore:*</c>), so an id of that type can be written out; null when the kind leaves the type open.
    /// </summary>
    public static string? EntityTypeOf(string? kind)
    {
        if (kind is null)
        {
            return null;
        }

        var parts = kind.Split(':');
        return parts.Length == 4 && parts[2].Length > 0 && !parts[2].Contains('*', StringComparison.Ordinal) ? parts[2] : null;
    }

    /// <summary>The search service's pattern for one kind (openapi search v2, <c>QueryRequest.kind</c>).</summary>
    [GeneratedRegex(@"^[\w.*-]+:[\w.*-]+:[\w.*-]+:[\d.*]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>
/// How the explorer read what a reader typed, and the query it sent for it.
/// </summary>
/// <param name="Reading">
/// <c>everything</c> (nothing typed), <c>lucene</c> (a query as written), <c>id</c> (a whole record id), <c>idPrefix</c> (the
/// start of one), <c>text</c> (words found anywhere in a record) or <c>mentions</c> (the records that name one).
/// </param>
/// <param name="Query">The Lucene query the reading asks, before the page's property values narrow it; null for every record.</param>
/// <param name="Plainer">
/// A query asking the core of the reading alone, for when the service refuses <paramref name="Query"/>; null when there is
/// none plainer.
/// </param>
/// <param name="Dropped">What <paramref name="Plainer"/> leaves out, as a note to the reader says it; null with no plainer query.</param>
public sealed record ExplorerReading(string Reading, string? Query, string? Plainer = null, string? Dropped = null);

/// <summary>
/// One record of a page, as the search index holds it: what it is, which version, its name, and who wrote it when. The name is
/// the record's own (its facility name, its name, its project name, its code or its file's name, the first it has), and
/// <c>NameField</c> says which property it was read from. <c>Values</c> holds what the record holds at each of the page's
/// columns (<see cref="ExplorerSearch.Columns"/>): every value at the path, through any list on the way, the first
/// <see cref="RecordExplorer.MaxColumnValues"/> of them; a column the record holds nothing at is left out, and null when the
/// page shows no columns.
/// </summary>
public sealed record ExplorerHit(
    string Id, string? Kind, long? Version, string? Name, string? NameField, string? CreateTime, string? CreateUser, string? ModifyTime, string? ModifyUser,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Values = null);

/// <summary>One distinct value of the property a page groups by, and how many of its records hold it; a null value is one the service named no key for.</summary>
public sealed record ExplorerBucket(string? Value, long Count);

/// <summary>
/// One page of the explorer: how the text was read and the query sent, the records it matches in all, the page's records, the
/// groups of the property asked for, and what the explorer did that the reader should know (an order or a clause the service
/// would not take, and what was asked instead). A query the service refused is a page with its words and no records.
/// </summary>
public sealed record ExplorerPage(
    string Reading, string? Query, string Kind, long Total, int Offset, IReadOnlyList<ExplorerHit> Hits, string? FacetPath,
    IReadOnlyList<ExplorerBucket>? Facet, IReadOnlyList<string> Notes, string? Refusal);

/// <summary>A kind the partition holds records of, and how many of them the query matches.</summary>
public sealed record ExplorerKindCount(string Kind, long Count);

/// <summary>
/// The kinds the records a query matches are of, each with its count: what the explorer's type list shows. <paramref name="Listed"/>
/// is what the kinds add up to; less than <paramref name="Total"/> when the service named fewer groups than there are.
/// </summary>
public sealed record ExplorerTypes(string Reading, string? Query, long Total, IReadOnlyList<ExplorerKindCount> Kinds, long Listed, IReadOnlyList<string> Notes, string? Refusal);

/// <summary>
/// A property records of a kind hold, which a page searches, narrows to, groups by and shows as a column: its path, how the
/// platform indexes it (<c>text</c>, <c>keyword</c>, <c>number</c>, <c>boolean</c> or <c>date</c>), and the nested array a
/// query reaches it through.
/// </summary>
/// <param name="Path">The property's path from the record root.</param>
/// <param name="Index">How the platform indexes it.</param>
/// <param name="Nested">The nested array it sits in (<c>data.GeoContexts</c>); null for none.</param>
/// <param name="Origin">
/// Where it was found: <c>record</c> (a property of every record), <c>schema</c> (declared by the schema of the kind read),
/// or <c>records</c> (held by the records read and not declared by that schema: a property the index adds, such as one of
/// an index augmentation, one of another version of the kind, or one no schema was read for).
/// </param>
/// <param name="Title">The schema's title for it; null where the schema gives none.</param>
/// <param name="Description">The schema's description of it, its first <see cref="RecordExplorer.MaxDescription"/> characters; null where the schema gives none.</param>
public sealed record ExplorerFieldInfo(string Path, string Index, string? Nested = null, string? Origin = null, string? Title = null, string? Description = null);

/// <summary>
/// The properties a kind's records hold: the record's own first, then those the schema of the kind declares, then those the
/// records read hold beyond them.
/// </summary>
/// <param name="Kind">The kind (or kind pattern) asked.</param>
/// <param name="SampleId">The first record read; null when the kind holds none.</param>
/// <param name="Fields">The properties.</param>
/// <param name="SchemaKind">The kind whose schema was read from the Schema service: the kind asked, or of a pattern, the kind of one type holding the most records; null when none was read.</param>
/// <param name="Sampled">How many records were read for the properties they hold.</param>
/// <param name="Notes">What the reader should know of how the list was made: a schema that could not be read, properties a query does not reach.</param>
public sealed record ExplorerFields(string Kind, string? SampleId, IReadOnlyList<ExplorerFieldInfo> Fields, string? SchemaKind = null, int Sampled = 0, IReadOnlyList<string>? Notes = null);

/// <summary>
/// The explorer's reads of the OSDU search service (openapi search v2, <c>POST /query</c>): a page of the records a reader's
/// text finds in a kind, narrowed to property values, in an order, with a property's groups; the kinds the records a text finds
/// are of, with their counts; and the properties a kind's records hold. Nothing here reads what the delivery system keeps: what
/// is shown is what OSDU holds, as the search index answers for it.
/// </summary>
/// <remarks>
/// <para>
/// Whatever the reader typed is read once (<see cref="Interpret"/>) into a query: a whole id finds that record, the ids that
/// start with it, and the same unique part under another type; the start of an id finds the ids that start with it; text
/// finds the records holding it as a phrase anywhere, and for a single word also the record of the kind in view whose id ends
/// with it. A Lucene query goes as it was written.
/// </para>
/// <para>
/// A query the service refuses (400) is not a failure of the page. An order it will not sort by is dropped, then a clause the
/// reading added beyond its core, each with a note saying so; a query still refused is the page's answer, with the service's
/// words. Anything else that keeps an answer from coming fails the call, since an empty page would say OSDU holds nothing.
/// </para>
/// </remarks>
public sealed partial class RecordExplorer
{
    /// <summary>The search the explorer reads (openapi search v2), as a flow's mapping searches reach it.</summary>
    public const string QueryPath = OsduRecordSearch.QueryPath;

    /// <summary>The properties a record's name is read from, in the order they are tried.</summary>
    public static readonly IReadOnlyList<string> NameFields =
        ["data.FacilityName", "data.Name", "data.ProjectName", "data.Code", "data.DatasetProperties.FileSourceInfo.Name"];

    /// <summary>
    /// The record's own properties every kind's records hold, with how the indexer keeps them: the keywords of the record
    /// itself (<see cref="OsduFieldIndex.Keyword"/>), offered for grouping before any property of the kind's data.
    /// </summary>
    public static readonly IReadOnlyList<ExplorerFieldInfo> EnvelopeFields =
    [
        new("kind", "keyword", Origin: ExplorerFieldCatalog.RecordOrigin),
        new("id", "keyword", Origin: ExplorerFieldCatalog.RecordOrigin),
        new("createUser", "keyword", Origin: ExplorerFieldCatalog.RecordOrigin),
        new("createTime", "date", Origin: ExplorerFieldCatalog.RecordOrigin),
        new("modifyUser", "keyword", Origin: ExplorerFieldCatalog.RecordOrigin),
        new("modifyTime", "date", Origin: ExplorerFieldCatalog.RecordOrigin),
        new("acl.viewers", "keyword", Origin: ExplorerFieldCatalog.RecordOrigin),
        new("acl.owners", "keyword", Origin: ExplorerFieldCatalog.RecordOrigin),
        new("legal.legaltags", "keyword", Origin: ExplorerFieldCatalog.RecordOrigin),
        new("legal.otherRelevantDataCountries", "keyword", Origin: ExplorerFieldCatalog.RecordOrigin),
    ];

    /// <summary>The most properties of a kind's data the explorer offers, however many its schema declares and its records hold.</summary>
    public const int MaxFields = 600;

    /// <summary>How many records the properties of a kind are read from, beside its schema: enough to meet the properties an index augmentation adds.</summary>
    public const int SampleSize = 20;

    /// <summary>The longest description of a property an answer carries; the schema's own may run to paragraphs.</summary>
    public const int MaxDescription = 300;

    /// <summary>The most values of one column a record carries: a list of hundreds shows its first ones.</summary>
    public const int MaxColumnValues = 20;

    /// <summary>The longest value of a column a record carries; a longer one is cut, with an ellipsis.</summary>
    public const int MaxColumnValueLength = 256;

    /// <summary>How deep into a record's data the explorer looks for properties.</summary>
    private const int MaxFieldDepth = 6;

    /// <summary>What a page asks of every hit: the record's identity, its version, its name and who wrote it when.</summary>
    private static readonly IReadOnlyList<string> Shown =
        ["id", "kind", "version", "createTime", "createUser", "modifyTime", "modifyUser", .. NameFields];

    private readonly OsduSearch _search;
    private readonly string? _partition;
    private readonly ILogger _log;

    /// <param name="client">The OSDU the explorer reads, with its credentials and partition.</param>
    /// <param name="partition">The data partition the client reads, which an id written without one is read in; null when unknown.</param>
    /// <param name="log">Where a refused query and what was asked instead are reported.</param>
    public RecordExplorer(OsduHttpClient client, string? partition, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(log);
        _search = new OsduSearch(client, QueryPath, RetrievalSource.DefaultSearchPath, log);
        _partition = string.IsNullOrWhiteSpace(partition) ? null : partition.Trim();
        _log = log;
    }

    /// <summary>
    /// The explorer reading through a search of its caller's own: an inventory's match reads the conditions an operator wrote
    /// in the explorer's terms through its flow's source, with that flow's search paths and paging.
    /// </summary>
    internal RecordExplorer(OsduSearch search, string? partition, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(log);
        _search = search;
        _partition = string.IsNullOrWhiteSpace(partition) ? null : partition.Trim();
        _log = log;
    }

    /// <summary>
    /// The query finding the records that name any of <paramref name="named"/> (ids without their version), other than the
    /// records whose own id is one of <paramref name="excluded"/>: each id a phrase, which finds it however a record writes it
    /// (with a version, or the trailing colon of "the latest"), since the analyser drops the colon and the version is a word of
    /// its own after it. A record's own id is never a mention of itself.
    /// </summary>
    public static string MentionsQuery(IReadOnlyList<string> named, IReadOnlyList<string> excluded)
    {
        ArgumentNullException.ThrowIfNull(named);
        ArgumentNullException.ThrowIfNull(excluded);
        if (named.Count == 0 || excluded.Count == 0)
        {
            throw new ArgumentException("A search for the records naming ids names at least one id, and excludes at least one.");
        }

        static string AnyOf(IReadOnlyList<string> ids)
            => ids.Count == 1 ? LuceneText.Phrase(ids[0]) : "(" + string.Join(" OR ", ids.Select(LuceneText.Phrase)) + ")";

        return $"{AnyOf(named)} AND NOT id:{AnyOf(excluded)}";
    }

    /// <summary>
    /// What a reader's search asks (see the remarks on <see cref="RecordExplorer"/>), in the data partition
    /// <paramref name="partition"/> an id written without one is read in. Pure: the same search reads the same way.
    /// </summary>
    public static ExplorerReading Interpret(ExplorerSearch search, string? partition)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (search.Mentions is { } mentioned)
        {
            // A record names another by its id, mostly with a version or the trailing colon of "the latest": the phrase finds
            // the id whichever, since the analyser drops the colon and the version is a word of its own after it.
            var named = TargetId.WithoutVersion(mentioned.Trim());
            return new ExplorerReading("mentions", MentionsQuery([named], [named]));
        }

        var text = search.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return new ExplorerReading("everything", null);
        }

        if (search.Lucene)
        {
            return new ExplorerReading("lucene", text);
        }

        var phrase = LuceneText.Phrase(text);
        var spaced = text.Any(char.IsWhiteSpace);
        if (!spaced && TargetId.IsRecordReference(text))
        {
            var id = TargetId.WithoutVersion(text);
            var exact = $"id:{LuceneText.Phrase(id)}";
            if (!LuceneText.IsEscapable(id))
            {
                return new ExplorerReading("id", exact);
            }

            // The record, the ids that go on from it (a paste cut short), and the same unique part under another type: the
            // three ways an id a reader holds is near the one OSDU holds.
            var parts = id.Split(':', 3);
            var near = $"{exact} OR id:{LuceneText.Escape(id)}*";
            return new ExplorerReading(
                "id", $"{near} OR id:{LuceneText.Escape(parts[0])}\\:*\\:{LuceneText.Escape(parts[2])}", near, "the same unique part under another type");
        }

        if (!spaced && (text.Contains(':', StringComparison.Ordinal) || text.Contains("--", StringComparison.Ordinal)))
        {
            // The start of an id; one that starts at its type is read in the partition, since every id there starts with it.
            var typed = StartsAtType(text) && partition is not null ? $"{partition}:{text}" : text;
            return LuceneText.IsEscapable(typed)
                ? new ExplorerReading("idPrefix", $"id:{LuceneText.Escape(typed)}*")
                : new ExplorerReading("text", phrase);
        }

        if (!spaced && text.Length >= 3 && partition is not null && LuceneText.IsEscapable(text))
        {
            if (ExplorerKinds.EntityTypeOf(search.Kind) is { } entityType)
            {
                // One word in a kind of one type may be the unique part of an id of it: the record of that type it names,
                // or whose unique part starts with it, beside the records that hold the word.
                var id = $"{partition}:{entityType}:{text}";
                return new ExplorerReading(
                    "text", $"{phrase} OR id:{LuceneText.Phrase(id)} OR id:{LuceneText.Escape(id)}*", phrase, $"the {entityType} whose id ends with the word");
            }

            if (Minted().IsMatch(text))
            {
                // A machine-minted unique part (a GUID, a hash) names a record whatever its type, and nothing else holds it.
                return new ExplorerReading(
                    "text", $"{phrase} OR id:{LuceneText.Escape(partition)}\\:*\\:{LuceneText.Escape(text)}*", phrase, "the records whose id ends with the word");
            }
        }

        return new ExplorerReading("text", phrase);
    }

    /// <summary>Whether the start of an id leaves its partition out: it starts with the entity type (<c>master-data--Wellbore:...</c>).</summary>
    private static bool StartsAtType(string text)
    {
        var first = text.Split(':')[0];
        return first.Contains("--", StringComparison.Ordinal);
    }

    /// <summary>
    /// The query a reading asks with the page's conditions: the reading's own query, and each condition as its clause. The
    /// service refuses a query whose every clause excludes, so conditions that only exclude, with nothing read beside them,
    /// start from every record (<see cref="OsduQuery.EveryRecord"/>).
    /// </summary>
    internal static string? Compose(string? reading, IReadOnlyList<ExplorerFilter> filters)
    {
        var parts = new List<string>(filters.Count + 2);
        var read = !string.IsNullOrWhiteSpace(reading);
        if (read)
        {
            parts.Add(filters.Count == 0 ? reading! : $"({reading})");
        }
        else if (filters.Count > 0 && filters.All(f => f.Excludes))
        {
            parts.Add(OsduQuery.EveryRecord);
        }

        parts.AddRange(filters.Select(f => f.Clause()));
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    /// <summary>One page of what <paramref name="search"/> finds, as the remarks on <see cref="RecordExplorer"/> describe.</summary>
    public async Task<ExplorerPage> SearchAsync(ExplorerSearch search, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (search.Problem() is { } problem)
        {
            throw new DeliveryException(problem);
        }

        var notes = new List<string>();
        search = await ThroughOthersAsync(search, notes, ct).ConfigureAwait(false);
        var reading = Interpret(search, _partition);
        var kind = search.Kind ?? ExplorerKinds.Any;
        var aggregateBy = search.Facet?.AggregateBy();
        var columns = search.Columns.Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyList<string> returned = columns.Count == 0 ? Shown : [.. Shown.Union(columns, StringComparer.Ordinal)];
        var asked = await AskAsync(reading, search.Filters, kind, search.Sort, search.Offset, search.Limit, aggregateBy, returned, notes, ct).ConfigureAwait(false);
        return new ExplorerPage(
            reading.Reading,
            asked.Query,
            kind,
            asked.Answer.Total,
            search.Offset,
            asked.Answer.Hits.Select(hit => Hit(hit, columns)).OfType<ExplorerHit>().ToList(),
            search.Facet?.Path,
            aggregateBy is null || asked.Answer.Refusal is not null ? null : asked.Answer.Buckets.Select(b => new ExplorerBucket(b.Key, b.Count)).ToList(),
            notes,
            asked.Answer.Refusal);
    }

    /// <summary>
    /// The kinds of the records <paramref name="search"/>'s text and property values find across every kind, each with its count:
    /// the search's own kind, order and page do not narrow it, since the list is what a reader picks a kind from.
    /// </summary>
    public async Task<ExplorerTypes> TypesAsync(ExplorerSearch search, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (search.Problem() is { } problem)
        {
            throw new DeliveryException(problem);
        }

        var notes = new List<string>();
        search = await ThroughOthersAsync(search, notes, ct).ConfigureAwait(false);
        var reading = Interpret(search with { Kind = null }, _partition);
        var asked = await AskAsync(reading, search.Filters, ExplorerKinds.Any, ExplorerSort.Relevance, 0, 1, "kind", ["id"], notes, ct).ConfigureAwait(false);
        var kinds = asked.Answer.Buckets
            .Where(b => !string.IsNullOrEmpty(b.Key))
            .Select(b => new ExplorerKindCount(b.Key!, b.Count))
            .OrderBy(k => k.Kind, StringComparer.Ordinal)
            .ToList();
        return new ExplorerTypes(reading.Reading, asked.Query, asked.Answer.Total, kinds, kinds.Sum(k => k.Count), notes, asked.Answer.Refusal);
    }

    /// <summary>
    /// The properties records of <paramref name="kind"/> hold, as the remarks on <see cref="ExplorerFieldCatalog"/> describe:
    /// the record's own, then the values the schema of the kind declares (read from the Schema service through
    /// <paramref name="schemas"/>; of a kind pattern, the schema of the kind of one type holding the most records), then the
    /// values the first <see cref="SampleSize"/> records hold beyond them. A value inside a list of objects is offered only
    /// as the schema has it indexed, since such a list may be nested, which a plain path does not reach; without a schema it
    /// is left out rather than offered and found empty. A schema that cannot be had leaves the records' own values, with a note.
    /// </summary>
    public async Task<ExplorerFields> FieldsAsync(string kind, SchemaServiceReader? schemas, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (ExplorerKinds.Problem(kind) is { } problem)
        {
            throw new DeliveryException(problem);
        }

        var pattern = kind.Contains('*', StringComparison.Ordinal);
        var answer = await _search.PageAsync(new OsduSearchQuery { Kind = kind }, 0, SampleSize, pattern ? "kind" : null, ct).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            throw new DeliveryException($"The search service refused to read the records of {kind}: {refusal}");
        }

        var held = new Dictionary<string, ExplorerFieldCatalog.Held>(StringComparer.Ordinal);
        foreach (var hit in answer.Hits)
        {
            if (hit["data"] is JsonObject data)
            {
                ExplorerFieldCatalog.Collect(data, held, MaxFields, MaxFieldDepth);
            }
        }

        var notes = new List<string>();
        var schemaKind = pattern ? SchemaKindOf(answer.Buckets, notes) : kind;
        var schema = schemas is null || schemaKind is null ? null : await SchemaAsync(schemas, schemaKind, notes, ct).ConfigureAwait(false);
        var content = new List<ExplorerFieldInfo>();
        var offered = new HashSet<string>(StringComparer.Ordinal);
        if (schema is not null)
        {
            var (declared, unreached) = ExplorerFieldCatalog.Declared(schema, MaxFields, MaxDescription);
            content.AddRange(declared);
            offered.UnionWith(declared.Select(f => f.Path));
            if (unreached > 0)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{unreached} {(unreached == 1 ? "value" : "values")} the schema of {schemaKind} declares {(unreached == 1 ? "is" : "are")} left out: the index holds nothing there a query compares (inside a list of objects the schema gives no indexing hint, or a value of no type)."));
            }
        }

        var beyond = new List<ExplorerFieldInfo>();
        foreach (var value in held.Values)
        {
            if (offered.Contains(value.Path) || content.Count + beyond.Count >= MaxFields)
            {
                continue;
            }

            if (schema is not null && SearchFields.ClassifyValue(schema, value.Path).Field is { } field)
            {
                // One of a list's forms the template does not list apart, or a value the walk of the schema met through a choice.
                var (title, description) = ExplorerFieldCatalog.Described(schema, value.Path, MaxDescription);
                content.Add(new ExplorerFieldInfo(value.Path, ExplorerFieldCatalog.IndexName(field.Index), field.NestedPath, ExplorerFieldCatalog.SchemaOrigin, title, description));
            }
            else if (!value.InList)
            {
                beyond.Add(new ExplorerFieldInfo(value.Path, value.Index, Origin: ExplorerFieldCatalog.RecordsOrigin));
            }
        }

        var fields = new List<ExplorerFieldInfo>(EnvelopeFields);
        fields.AddRange(content.OrderBy(f => f.Path, StringComparer.Ordinal));
        fields.AddRange(beyond.OrderBy(f => f.Path, StringComparer.Ordinal));
        var sample = answer.Hits.Count > 0 ? answer.Hits[0] : null;
        return new ExplorerFields(
            kind, sample?["id"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null, fields, schema is null ? null : schemaKind, answer.Hits.Count, notes);
    }

    /// <summary>
    /// The kind whose schema describes the records of a kind pattern: of one type, the kind holding the most of them (the
    /// last of those as many); null for records of several types, which no one schema describes, with a note saying so.
    /// </summary>
    private static string? SchemaKindOf(IReadOnlyList<OsduSearchBucket> kinds, List<string> notes)
    {
        var held = kinds.Where(k => !string.IsNullOrEmpty(k.Key) && k.Count > 0).ToList();
        var types = held.Select(k => ExplorerKinds.EntityTypeOf(k.Key) ?? k.Key!).Distinct(StringComparer.Ordinal).Count();
        if (types > 1)
        {
            notes.Add("The records are of several types, which no one schema describes, so the properties of their content are those the records read hold.");
            return null;
        }

        return held.OrderByDescending(k => k.Count).ThenByDescending(k => k.Key, StringComparer.Ordinal).Select(k => k.Key).FirstOrDefault();
    }

    /// <summary>The schema of <paramref name="kind"/> as the Schema service holds it; null with a note when it cannot be had.</summary>
    private static async Task<SchemaSnapshot?> SchemaAsync(SchemaServiceReader schemas, string kind, List<string> notes, CancellationToken ct)
    {
        SchemaServiceRead read;
        try
        {
            read = await schemas.ReadAsync(kind, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException && !ct.IsCancellationRequested)
        {
            notes.Add($"The schema of {kind} could not be read from the Schema service, so the properties of the content are those the records read hold: {HeaderRedaction.RedactMessage(ex.Message)}");
            return null;
        }

        if (read.Schema is null)
        {
            notes.Add(read.Unresolved.Count > 0
                ? $"The schema of {kind} could not be read from the Schema service, so the properties of the content are those the records read hold: {read.Unresolved[0]}"
                : $"The Schema service holds no schema of {kind}, so the properties of the content are those the records read hold.");
            return null;
        }

        if (read.Unresolved.Count > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{read.Unresolved.Count} {(read.Unresolved.Count == 1 ? "reference" : "references")} of the schema of {kind} could not be followed; the values behind {(read.Unresolved.Count == 1 ? "it" : "them")} are listed only where the records read hold them."));
        }

        return read.Schema;
    }

    /// <summary>
    /// Asks the reading of a page, and when the service refuses it, a plainer question: the same without its order, then the
    /// reading's core alone (with the order, then without). Each step the service made necessary is a note; a question still
    /// refused is answered with the service's words on the first one.
    /// </summary>
    private async Task<(string? Query, OsduSearchAnswer Answer)> AskAsync(
        ExplorerReading reading, IReadOnlyList<ExplorerFilter> filters, string kind, ExplorerSort sort, int offset, int limit, string? aggregateBy,
        IReadOnlyList<string> returned, List<string> notes, CancellationToken ct)
    {
        var query = Compose(reading.Query, filters);
        var answer = await PageAsync(kind, query, sort, offset, limit, aggregateBy, returned, ct).ConfigureAwait(false);
        if (answer.Refusal is not { } refused)
        {
            return (query, answer);
        }

        var tries = new List<(string? Query, ExplorerSort Sort)>();
        if (sort != ExplorerSort.Relevance)
        {
            tries.Add((query, ExplorerSort.Relevance));
        }

        if (reading.Plainer is { } plainer)
        {
            var core = Compose(plainer, filters);
            tries.Add((core, sort));
            if (sort != ExplorerSort.Relevance)
            {
                tries.Add((core, ExplorerSort.Relevance));
            }
        }

        foreach (var (asked, order) in tries)
        {
            var plainerAnswer = await PageAsync(kind, asked, order, offset, limit, aggregateBy, returned, ct).ConfigureAwait(false);
            if (plainerAnswer.Refusal is not null)
            {
                continue;
            }

            if (order != sort)
            {
                notes.Add($"The search service would not order these records by {SortField(sort)}, so they are in its own order.");
            }

            if (!string.Equals(asked, query, StringComparison.Ordinal))
            {
                notes.Add($"The search service would not look for {reading.Dropped ?? "part of this search"}, so it was asked without it.");
            }

            _log.LogWarning("The search service refused {Query} on {Kind} ({Refusal}); asked {Asked} instead.", query, kind, refused, asked);
            notes.Add($"It said: {refused}");
            return (asked, plainerAnswer);
        }

        _log.LogWarning("The search service refused {Query} on {Kind}: {Refusal}", query, kind, refused);
        return (query, answer);
    }

    private Task<OsduSearchAnswer> PageAsync(
        string kind, string? query, ExplorerSort sort, int offset, int limit, string? aggregateBy, IReadOnlyList<string> returned, CancellationToken ct)
        => _search.PageAsync(
            new OsduSearchQuery
            {
                Kind = kind,
                Query = query,
                Sort = sort == ExplorerSort.Relevance ? [] : [(SortField(sort), true)],
                ReturnedFields = returned,
            },
            offset,
            limit,
            aggregateBy,
            ct);

    private static string SortField(ExplorerSort sort) => sort == ExplorerSort.Created ? "createTime" : "modifyTime";

    /// <summary>
    /// A hit as a page lists it, with what it holds at each of <paramref name="columns"/> (none asked: no values); null for a
    /// hit with no id, which names no record a reader could open.
    /// </summary>
    internal static ExplorerHit? Hit(JsonObject hit, IReadOnlyList<string> columns)
    {
        var listed = Hit(hit);
        if (listed is null || columns.Count == 0)
        {
            return listed;
        }

        var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (ValuesAt(hit, column) is { Count: > 0 } held)
            {
                values[column] = held;
            }
        }

        return listed with { Values = values };
    }

    /// <summary>
    /// Every value a hit holds at <paramref name="path"/>, through any list on the way (each of a wellbore's geographic contexts),
    /// whichever way the service projected it: as nested objects, or under a name holding dots itself, as the record's root
    /// carries a projected path (<c>data.Code</c>) and an index augmentation names its properties (<c>Augmented.WellboreName</c>).
    /// The first <see cref="MaxColumnValues"/>, each cut at <see cref="MaxColumnValueLength"/> characters.
    /// </summary>
    internal static IReadOnlyList<string> ValuesAt(JsonObject hit, string path)
    {
        ArgumentNullException.ThrowIfNull(hit);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var found = new List<string>();
        Gather(hit, path.Split('.'), 0, found);
        return found;
    }

    private static void Gather(JsonNode? node, string[] segments, int at, List<string> found)
    {
        if (node is null || found.Count >= MaxColumnValues)
        {
            return;
        }

        if (at == segments.Length)
        {
            IEnumerable<JsonValue> values = node switch { JsonArray list => list.OfType<JsonValue>(), JsonValue one => [one], _ => [] };
            foreach (var value in values)
            {
                if (found.Count < MaxColumnValues && ColumnText(value) is { } text)
                {
                    found.Add(text.Length <= MaxColumnValueLength ? text : string.Concat(text.AsSpan(0, MaxColumnValueLength - 3), "..."));
                }
            }

            return;
        }

        switch (node)
        {
            case JsonArray items:
                foreach (var item in items)
                {
                    Gather(item, segments, at, found);
                }

                break;
            case JsonObject holder:
                // The longest name the object holds first: a name with dots is the whole of the path it spells.
                for (var end = segments.Length; end > at; end--)
                {
                    if (holder.TryGetPropertyValue(string.Join('.', segments[at..end]), out var next))
                    {
                        Gather(next, segments, end, found);
                        return;
                    }
                }

                break;
        }
    }

    /// <summary>A value as a column shows it: a text as it is, a number as written, a boolean as true or false; null for a JSON null.</summary>
    private static string? ColumnText(JsonValue value) => value.GetValueKind() switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.Number => value.ToJsonString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    /// <summary>A hit as a page lists it; null for a hit with no id, which names no record a reader could open.</summary>
    internal static ExplorerHit? Hit(JsonObject hit)
    {
        if (Text(hit, "id") is not { Length: > 0 } id)
        {
            return null;
        }

        string? name = null;
        string? nameField = null;
        foreach (var field in NameFields)
        {
            if (Text(hit, field) is { Length: > 0 } found)
            {
                (name, nameField) = (found, field);
                break;
            }
        }

        var version = Value(hit, "version") is JsonValue v && (v.TryGetValue<long>(out var number)
            || (v.TryGetValue<string>(out var digits) && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number)))
            ? number
            : (long?)null;
        return new ExplorerHit(
            id, Text(hit, "kind"), version, name, nameField, Text(hit, "createTime"), Text(hit, "createUser"), Text(hit, "modifyTime"), Text(hit, "modifyUser"));
    }

    /// <summary>
    /// The value at a dotted path of a hit, whichever way the service projected it: as nested objects (<c>data</c> holding
    /// <c>FacilityName</c>), or under the dotted name itself.
    /// </summary>
    private static JsonNode? Value(JsonObject hit, string path)
    {
        if (hit.TryGetPropertyValue(path, out var flat) && flat is not null)
        {
            return flat;
        }

        JsonNode? node = hit;
        foreach (var part in path.Split('.'))
        {
            node = node is JsonObject inner && inner.TryGetPropertyValue(part, out var next) ? next : null;
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    private static string? Text(JsonObject hit, string path)
        => Value(hit, path) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A unique part a machine minted: hex digits and hyphens, at least eight of them, a digit among them.</summary>
    [GeneratedRegex(@"^(?=[0-9a-fA-F-]*[0-9])[0-9a-fA-F-]{8,}$", RegexOptions.CultureInvariant)]
    private static partial Regex Minted();
}
