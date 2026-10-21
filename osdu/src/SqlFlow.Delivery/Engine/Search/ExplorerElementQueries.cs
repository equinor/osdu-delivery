using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>A value inside a section, by the path the record inspector names it by, and what the record holds there.</summary>
public sealed record ExplorerElementLeaf(string Path, JsonElement? Value);

/// <summary>What the explorer asks the queries of: an element of a record of a kind, by the path the record inspector names it by.</summary>
public sealed record ExplorerElementRequest
{
    /// <summary>The longest path asked about.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>The longest value a query is written for: far past the 256 characters the index keeps of a value whole.</summary>
    public const int MaxValueLength = 4096;

    /// <summary>The most values of a section one query compares: past a few dozen, a query finds what its first values find.</summary>
    public const int MaxLeaves = 48;

    /// <summary>The record's kind (a kind pattern is read as the kinds it matches).</summary>
    public required string Kind { get; init; }

    /// <summary>The element's path in the record, a list's items by their place: <c>data.GeoContexts[1].GeoPoliticalEntityID</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Whether the element is a section (an object, a list, an item of a list) rather than a value.</summary>
    public bool Section { get; init; }

    /// <summary>The value the record holds there, for a value: a text, a number, a boolean, or null.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>For a section, the values it holds at any depth, each by its own path; the query finds the records holding them all.</summary>
    public IReadOnlyList<ExplorerElementLeaf>? Values { get; init; }

    /// <summary>Why the request cannot be read; null when it can.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Kind))
        {
            return "Name the kind of the record the element is in.";
        }

        if (ExplorerKinds.Problem(Kind.Trim()) is { } wrong)
        {
            return wrong;
        }

        if (string.IsNullOrWhiteSpace(Path))
        {
            return "Name the element's path in the record, such as data.FacilityName.";
        }

        if (Path.Length > MaxPathLength || Values?.Any(v => v?.Path is not { Length: > 0 and <= MaxPathLength }) == true)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Every path is a path of at most {MaxPathLength:N0} characters.");
        }

        if (Values is { Count: > MaxLeaves })
        {
            return $"A section's query compares at most {MaxLeaves} of its values; send its first {MaxLeaves}.";
        }

        if (Values is { Count: > 0 } && !Section)
        {
            return "Only a section holds values of its own; a value is sent as its value.";
        }

        return Value is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } || Values?.Any(v => v.Value is { ValueKind: JsonValueKind.Object or JsonValueKind.Array }) == true
            ? "A value is a text, a number, a boolean or null; an object or a list is a section."
            : null;
    }
}

/// <summary>A query that finds records by an element, and what it finds, in words.</summary>
/// <param name="Purpose">
/// <c>exact</c> (exactly this value, or every value a section holds, a nested list's item by item), <c>words</c> (the words
/// of a text, their case aside) or <c>exists</c> (any value there).
/// </param>
/// <param name="Query">The Lucene query, as the search service's <c>query</c> takes it.</param>
/// <param name="Says">What the query finds, in words.</param>
public sealed record ExplorerElementQuery(string Purpose, string Query, string Says);

/// <summary>How the index holds a value: its path, how it is kept, and the nested or flattened list it sits in.</summary>
public sealed record ExplorerElementField(string Path, string Index, string? NestedPath, string? FlattenedPath);

/// <summary>
/// The queries that find records by an element of a record, the exact one first; how the index holds it, in words; the
/// template read (null where no synced repository holds one); why a query is a guess, where the template could not say how a value is
/// indexed and it is written from the value; why no query can be written, where none can; and notes.
/// </summary>
public sealed record ExplorerElementAnswer(
    string Kind, string Path, BlueprintTemplate? Template, ExplorerElementField? Field, string? Reading,
    IReadOnlyList<ExplorerElementQuery> Queries, string? Guess, string? Problem, IReadOnlyList<string> Notes);

/// <summary>
/// The Lucene query that finds the records holding exactly an element of a record the explorer shows
/// (osdu/docs/reference/concepts/explorer.md, A record): a value, a list of values, an object, a nested list's item or
/// a whole list of them. How each value is indexed is read from the template of the record's kind a synced repository holds, as every
/// search the module writes reads it (<see cref="SearchFields"/>): text by its keyword sub-field, a keyword, a number,
/// a boolean or a date, a value of a nested list inside <c>nested(...)</c> with the other values of its item. Where the
/// template cannot say, the query is written from the value and said to be a guess, so there is always a query to see.
/// Each is written by <see cref="OsduQuery"/>, as the module's own lookups and filters are. Nothing is read from OSDU.
/// </summary>
public static partial class ExplorerElementQueries
{
    /// <summary>The queries of <paramref name="request"/>'s element, its kind's template read from <paramref name="templates"/>.</summary>
    /// <exception cref="DeliveryException">The request cannot be read.</exception>
    public static async Task<ExplorerElementAnswer> DescribeAsync(ExplorerElementRequest request, ITemplateSource templates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(templates);
        if (request.Problem() is { } problem)
        {
            throw new DeliveryException(problem);
        }

        var kind = request.Kind.Trim();
        var notes = new List<string>();
        var (matching, _) = await DimensionBlueprints.MatchingAsync(kind, templates, document: null, ct).ConfigureAwait(false);
        var match = matching.Count > 0 ? matching[0] : null;
        if (match is null && ExplorerKinds.EntityTypeOf(kind) is { } type)
        {
            // Another version of the type describes the property as near as any template the repositories hold does, and says so.
            var (ofType, _) = await DimensionBlueprints.MatchingAsync($"*:*:{type}:*", templates, document: null, ct).ConfigureAwait(false);
            match = ofType.Count > 0 ? ofType[0] : null;
            if (match is not null)
            {
                notes.Add($"No synced repository holds a template of {kind}, so it is read by {match.Template.Kind}, the newest of its type they hold.");
            }
        }

        return Describe(request, match?.Schema, notes);
    }

    /// <summary>The queries of <paramref name="request"/>'s element by <paramref name="schema"/>, or by its values where no repository holds the template.</summary>
    public static ExplorerElementAnswer Describe(ExplorerElementRequest request, SchemaSnapshot? schema, IReadOnlyList<string>? notesSoFar = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = request.Kind.Trim();
        var notes = new List<string>(notesSoFar ?? []);
        var template = schema is null ? null : new BlueprintTemplate(schema.Kind, schema.Version);
        if (ElementPath.Parse(request.Path) is not { } element)
        {
            return new(kind, request.Path, template, null, null, [],
                null, $"'{request.Path}' is not the path of an element of a record, such as data.FacilityName or data.GeoContexts[1].GeoTypeID.", notes);
        }

        return request.Section || element.EndsInItem && request.Value is null
            ? DescribeSection(kind, element, request.Values ?? [], schema, template, notes)
            : DescribeValue(kind, element, request.Value, schema, template, notes);
    }

    /// <summary>A value: exactly it; its words, for a text; and whether a record holds one, where a query can ask that.</summary>
    private static ExplorerElementAnswer DescribeValue(
        string kind, ElementPath element, JsonElement? json, SchemaSnapshot? schema, BlueprintTemplate? template, List<string> notes)
    {
        var path = element.Plain;
        var name = element.Names[^1];
        var value = ValueText(json);
        if (value is { Length: > ExplorerElementRequest.MaxValueLength })
        {
            return new(kind, path, template, null, null, [], null, string.Create(CultureInfo.InvariantCulture,
                $"This value is {value.Length:N0} characters, and a query is written for a value of at most {ExplorerElementRequest.MaxValueLength:N0}: the index keeps at most 256 characters of a value whole."), notes);
        }

        var (field, repeats, guess) = FieldOf(kind, schema, element, json);
        var described = new ExplorerElementField(field.Path, IndexName(field.Index), field.NestedPath, FlattenedOf(schema, path));
        var queries = new List<ExplorerElementQuery>();
        var held = value is null ? null : Clipped(value);
        if (value is null)
        {
            notes.Add("This record holds null here, which the index holds no value of; only whether a record holds one can be asked.");
        }
        else
        {
            try
            {
                var says = repeats
                    ? $"The records holding {held} as one of their {name} values, the whole value as written."
                    : $"The records whose {name} is exactly {held}, the whole value as written.";
                queries.Add(new ExplorerElementQuery("exact", OsduQuery.Equal(field, value).Text, says));
            }
            catch (OsduQueryException ex)
            {
                notes.Add($"No query finds this whole value: {Sentence(ex.Message)}");
            }

            // A record's id is found by its whole value; its words (the parts of an id) find nothing the whole value does not.
            if (field.Index == OsduFieldIndex.Text && !TargetId.IsRecordReference(value.Trim()))
            {
                try
                {
                    var phrase = OsduQuery.Phrase(field.QueryPath, value);
                    var words = field.NestedPath is { } nestedPath ? OsduQuery.Nested(nestedPath, phrase) : phrase;
                    queries.Add(new ExplorerElementQuery("words", words.Text, $"The records whose {name} holds the words {held}, in this order and in any case: a value that contains them answers as well."));
                }
                catch (OsduQueryException ex)
                {
                    notes.Add($"No query finds these words: {Sentence(ex.Message)}");
                }
            }
        }

        if (field.NestedPath is null)
        {
            queries.Add(new ExplorerElementQuery("exists", $"_exists_:{field.Path}", $"The records that hold any {name}."));
        }

        return new ExplorerElementAnswer(kind, path, template, described, Reading(described, field), queries, guess,
            queries.Count == 0 ? "No query can be written for this value." : null, notes);
    }

    /// <summary>
    /// A section: the records holding every value it holds, each compared exactly, the values of a nested list's item together
    /// inside one <c>nested(...)</c> so they must sit in the same item; and whether a record holds the section, where a query
    /// can ask that (not of a nested list, whose items a top-level query does not see).
    /// </summary>
    private static ExplorerElementAnswer DescribeSection(
        string kind, ElementPath element, IReadOnlyList<ExplorerElementLeaf> leaves, SchemaSnapshot? schema, BlueprintTemplate? template, List<string> notes)
    {
        var path = element.Plain;
        var name = element.Names[^1];
        if (path == SearchFields.DataRoot)
        {
            return new(kind, path, template, null, null, [], null, "Every record holds data: search one of its values, or one of its sections, rather than data itself.", notes);
        }

        // How the index holds the section itself: the nested or flattened list it is or sits in, by the template.
        string? nested = null;
        string? flattened = null;
        string? sectionGuess = null;
        if (SearchFields.IsDataPath(path) && schema is not null)
        {
            var shape = SearchFields.Section(schema, path);
            if (shape.Problem is { } problem)
            {
                sectionGuess = Sentence(problem);
            }

            (nested, flattened) = (shape.NestedPath, shape.FlattenedPath);
        }
        else if (SearchFields.IsDataPath(path))
        {
            sectionGuess = $"No template a synced repository holds of {kind} says how {path} is indexed, so a list in it is read as a plain object; the platform may index it nested, flattened or not at all.";
        }

        // Every value, compared exactly: those outside any nested list each alone, those of a nested list's item together.
        var top = new List<OsduQuery>();
        var items = new Dictionary<string, (string List, List<(OsduField Field, string Value)> Comparisons)>(StringComparer.Ordinal);
        var guesses = new List<string>();
        var skipped = 0;
        foreach (var leaf in leaves)
        {
            var value = ValueText(leaf.Value);
            if (ElementPath.Parse(leaf.Path) is not { } at || !Within(at, element) || value is null || value.Length > ExplorerElementRequest.MaxValueLength)
            {
                skipped++;
                continue;
            }

            var (field, _, guess) = FieldOf(kind, schema, at, leaf.Value);
            if (guess is not null && !guesses.Contains(guess, StringComparer.Ordinal))
            {
                guesses.Add(guess);
            }

            try
            {
                if (field.NestedPath is { } list)
                {
                    _ = OsduQuery.Equal(field, value);
                    var item = $"{list}[{at.ItemOf(list)?.ToString(CultureInfo.InvariantCulture) ?? "?"}]";
                    if (!items.TryGetValue(item, out var compared))
                    {
                        compared = (list, []);
                        items[item] = compared;
                    }

                    compared.Comparisons.Add((field, value));
                }
                else
                {
                    top.Add(OsduQuery.Equal(field, value));
                }
            }
            catch (OsduQueryException ex)
            {
                skipped++;
                notes.Add($"{at.Plain} is left out of the query: {Sentence(ex.Message)}");
            }
        }

        var parts = new List<OsduQuery>(top);
        foreach (var (item, (list, comparisons)) in items)
        {
            try
            {
                parts.Add(OsduQuery.NestedAll(list, comparisons));
            }
            catch (OsduQueryException ex)
            {
                notes.Add($"{item} is left out of the query: {Sentence(ex.Message)}");
            }
        }

        if (skipped > 0)
        {
            notes.Add(skipped == 1 ? "One value held null or could not be compared, and is left out." : $"{skipped} values held null or could not be compared, and are left out.");
        }

        var queries = new List<ExplorerElementQuery>();
        var count = parts.Count;
        if (count > 0)
        {
            var what = element.EndsInItem ? $"this item of {path}" : $"this {name}";
            var together = items.Count > 0 ? ", the values of each item of a nested list together in one item" : string.Empty;
            queries.Add(new ExplorerElementQuery(
                "exact",
                (count == 1 ? parts[0] : OsduQuery.All([.. parts])).Text,
                $"The records holding every value of {what}, each exactly as written{together}. A record may hold more besides."));
        }

        if (nested is null && sectionGuess is null)
        {
            queries.Add(new ExplorerElementQuery("exists", $"_exists_:{path}", $"The records that hold {name}, whatever it holds."));
        }
        else if (nested is not null)
        {
            notes.Add($"{path} is {(nested == path ? "a nested list" : $"inside the nested list {nested}")}: whether a record holds it is not a question a search answers, since the index keeps its items apart from the record.");
        }

        if (sectionGuess is not null)
        {
            guesses.Insert(0, sectionGuess);
        }

        var reading = nested is not null
            ? $"{path} is {(nested == path ? "a nested list" : $"inside the nested list {nested}")}: the index keeps each item apart, so the values of one item are compared together inside nested({nested}, (...))."
            : flattened is not null
                ? $"{path} is in the flattened list {flattened}: the index keeps its values as keywords under their dotted paths, not item by item."
                : $"{path} is a section of the record: each value it holds is compared on its own path.";
        return new ExplorerElementAnswer(kind, path, template, new ExplorerElementField(path, "section", nested, flattened), reading, queries,
            guesses.Count == 0 ? null : string.Join(" ", guesses.Take(2)),
            queries.Count == 0 ? "This section holds no value a query can compare." : null, notes);
    }

    /// <summary>
    /// How the index holds a value: the record's own properties as the indexer maps them for every kind; data as the kind's
    /// template says. Where neither can say, the value is read as most are (text, a number, a boolean, a date) and why that
    /// is a guess is given.
    /// </summary>
    private static (OsduField Field, bool Repeats, string? Guess) FieldOf(string kind, SchemaSnapshot? schema, ElementPath element, JsonElement? value)
    {
        var path = element.Plain;
        var repeats = element.CrossesObjectList || element.EndsInItem;
        string why;
        if (!SearchFields.IsDataPath(path))
        {
            var own = SearchFields.RecordProperty(path);
            if (own.Field is { } recordField)
            {
                return (recordField, own.Repeats, null);
            }

            why = Sentence(own.Problem!);
        }
        else if (schema is not null)
        {
            var shape = SearchFields.ClassifyValue(schema, path);
            if (shape.Field is { } schemaField)
            {
                return (schemaField, shape.Repeats, null);
            }

            why = Sentence(shape.Problem!);
        }
        else
        {
            why = $"No template a synced repository holds of {kind} says how {path} is indexed.";
            if (element.ObjectList is { } list)
            {
                why += $" {list} is a list of objects, which the platform may index nested, flattened or not at all.";
            }
        }

        return (OsduField.Of(path, Guess(value)), repeats, $"{why} The query is written from the value, as most values are indexed, and may not find what it looks for.");
    }

    /// <summary>How the index holds a value, in words.</summary>
    private static string Reading(ExplorerElementField described, OsduField field)
    {
        var how = field.Index switch
        {
            OsduFieldIndex.Text => $"{field.Path} is text: its whole value is kept in {field.Path}.keyword, which an exact match asks, and its words in {field.Path}, which a phrase asks.",
            OsduFieldIndex.Keyword => $"{field.Path} is a keyword: the whole value, which a match asks as written.",
            OsduFieldIndex.Number => $"{field.Path} is a number, compared as one.",
            OsduFieldIndex.Boolean => $"{field.Path} is a boolean, true or false.",
            _ => $"{field.Path} is a date, compared as the instant it names.",
        };
        if (field.NestedPath is { } nested)
        {
            how += $" It sits in the nested list {nested}, whose items the index keeps apart, so a search reaches it inside nested({nested}, (...)) and names it there as {field.QueryPath}.";
        }
        else if (described.FlattenedPath is { } flattened)
        {
            how += $" It sits in the flattened list {flattened}, whose values the index keeps as keywords under their dotted paths.";
        }

        return how;
    }

    /// <summary>The flattened list a path of data sits in, by the template; null for none or without a template.</summary>
    private static string? FlattenedOf(SchemaSnapshot? schema, string path)
    {
        if (schema is null || !SearchFields.IsDataPath(path))
        {
            return null;
        }

        var parent = path[..path.LastIndexOf('.')];
        return parent == SearchFields.DataRoot ? null : SearchFields.Section(schema, parent).FlattenedPath;
    }

    /// <summary>Whether a value's path lies inside a section's: the same names first, and the same item of each list on the way.</summary>
    private static bool Within(ElementPath leaf, ElementPath section)
        => leaf.Canonical.StartsWith(section.Canonical, StringComparison.Ordinal)
            && leaf.Canonical.Length > section.Canonical.Length
            && leaf.Canonical[section.Canonical.Length] is '.' or '[';

    /// <summary>How the indexer keeps a value with no template to say: a number, a boolean, a date written as one, or text.</summary>
    private static OsduFieldIndex Guess(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.Number => OsduFieldIndex.Number,
        JsonValueKind.True or JsonValueKind.False => OsduFieldIndex.Boolean,
        JsonValueKind.String when IsoInstant().IsMatch(value.Value.GetString()!) => OsduFieldIndex.Date,
        _ => OsduFieldIndex.Text,
    };

    /// <summary>The value as a query compares it: a text as written, a number's digits, true or false; null for null or none.</summary>
    private static string? ValueText(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString(),
        JsonValueKind.Number => value.Value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static string IndexName(OsduFieldIndex index) => index switch
    {
        OsduFieldIndex.Text => "text",
        OsduFieldIndex.Keyword => "keyword",
        OsduFieldIndex.Number => "number",
        OsduFieldIndex.Boolean => "boolean",
        _ => "date",
    };

    /// <summary>A value as words quote it: in quotes, clipped where it is long.</summary>
    private static string Clipped(string value) => "\"" + (value.Length > 80 ? value[..80] + "..." : value) + "\"";

    /// <summary>A problem as a sentence: its first letter capitalised and a full stop at its end.</summary>
    private static string Sentence(string problem)
    {
        var trimmed = problem.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var capital = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return capital.EndsWith('.') ? capital : capital + ".";
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:?\d{2})$")]
    private static partial Regex IsoInstant();

    /// <summary>
    /// An element's path as the record inspector names it, read: its property names, the item of each list it steps into (by
    /// the number of names before it), and the path written back canonically.
    /// </summary>
    private sealed record ElementPath(
        IReadOnlyList<string> Names, string Plain, string Canonical, IReadOnlyList<(int Names, int Item)> Items, bool CrossesObjectList, string? ObjectList, bool EndsInItem)
    {
        /// <summary>The item of the list at <paramref name="list"/> (a plain path) this path steps into; null when it steps into none there.</summary>
        public int? ItemOf(string list)
        {
            var count = list.Split('.').Length;
            foreach (var (names, item) in Items)
            {
                if (names == count && string.Join('.', Names.Take(count)) == list)
                {
                    return item;
                }
            }

            return null;
        }

        public static ElementPath? Parse(string text)
        {
            var trimmed = text.Trim();
            var names = new List<string>();
            var items = new List<(int, int)>();
            var canonical = new System.Text.StringBuilder(trimmed.Length);
            var crosses = false;
            string? objectList = null;
            var endsInItem = false;
            foreach (Match part in PathPart().Matches(trimmed))
            {
                if (part.Groups["item"].Success)
                {
                    if (names.Count == 0 || !int.TryParse(part.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    {
                        return null;
                    }

                    canonical.Append(part.Value);
                    items.Add((names.Count, index));
                    endsInItem = true;
                    continue;
                }

                if (endsInItem)
                {
                    // A name after an item steps into a list of objects.
                    crosses = true;
                    objectList ??= string.Join('.', names);
                    endsInItem = false;
                }

                if (names.Count > 0)
                {
                    canonical.Append('.');
                }

                canonical.Append(part.Value);
                names.Add(part.Groups["name"].Value);
            }

            // The path is read only when it is exactly its parts: a name, then dotted names and items, nothing else between.
            var plain = string.Join('.', names);
            var written = canonical.ToString();
            return names.Count == 0 || !string.Equals(written, trimmed, StringComparison.Ordinal) || !OsduPath.IsPath(plain)
                ? null
                : new ElementPath(names, plain, written, items, crosses, objectList, endsInItem);
        }
    }

    [GeneratedRegex(@"(?<item>\[(?<index>\d{1,9})\])|(?<name>[A-Za-z0-9_]+)")]
    private static partial Regex PathPart();
}
