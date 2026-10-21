using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.SearchTerms;

/// <summary>A condition asked of a search term, as a person gives it: what the column must hold, in the source's own values.</summary>
public sealed record SearchTermCondition(ExplorerCondition Condition, string? Value = null, IReadOnlyList<string>? Values = null, string? To = null);

/// <summary>
/// How the platform indexes what a route compares: the record's property it fills; for a lookup or a search, the
/// properties of the records found it matches on and the one it reads of them; for a key, where the record holds each of
/// the key's other columns. Or why a search cannot compare it, such as a property inside an object the schema leaves open,
/// which the platform does not index.
/// </summary>
public sealed record SearchRouteFields(
    OsduField? Target,
    IReadOnlyList<OsduField>? Match = null,
    OsduField? Read = null,
    IReadOnlyList<ExplorerViaColumn>? KeyColumns = null,
    string? Problem = null)
{
    public static SearchRouteFields Refused(string problem) => new(null, Problem: problem);
}

/// <summary>
/// Turns a condition on a search term into the condition the explorer asks of the records
/// (osdu/docs/reference/concepts/search-terms.md): on the property the term's route fills, its values put through the
/// mapping as a render puts them (<see cref="SearchTermValues"/>), and for a route through other records, the way the
/// node reads those first (<see cref="ExplorerVia"/>). Which conditions a route takes follows from how its property is
/// indexed and what its steps keep of a value.
/// </summary>
public static class SearchTermResolver
{
    /// <summary>The order routes are preferred in when a person has picked none: the plainest way the column reaches the record first.</summary>
    private static int Rank(SearchRouteKind kind) => kind switch
    {
        SearchRouteKind.Copy => 0,
        SearchRouteKind.Steps => 1,
        SearchRouteKind.Search or SearchRouteKind.Lookup => 2,
        SearchRouteKind.Key => 3,
        _ => 9,
    };

    /// <summary>
    /// The route a term is searched by. Of the routes that can be searched, those that fill <paramref name="kind"/> when one
    /// kind is in view and any of them does: the route of the mapping version that renders that kind, where versions write
    /// the column differently. Of those, the one <paramref name="picked"/> names, else the plainest (a copy before steps,
    /// before a lookup, before a key; the record's content before its tags; a value of its own before one inside a list);
    /// null when none can be searched.
    /// </summary>
    public static SearchRoute? Preferred(IReadOnlyList<SearchRoute> routes, Func<SearchRoute, bool> usable, string? picked, string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(usable);
        var candidates = routes.Where(usable).ToList();
        var shown = kind?.Trim();
        if (!string.IsNullOrEmpty(shown) && !shown.Contains('*', StringComparison.Ordinal) && candidates.Any(r => r.Fills(shown)))
        {
            candidates = candidates.Where(r => r.Fills(shown)).ToList();
        }

        if (picked is not null && candidates.FirstOrDefault(r => string.Equals(r.Id, picked, StringComparison.Ordinal)) is { } chosen)
        {
            return chosen;
        }

        return candidates
            .OrderBy(r => Rank(r.Kind))
            .ThenBy(r => r.Path.StartsWith("data.", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(r => r.Target.Contains("[]", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(r => r.Path.Length)
            .ThenBy(r => r.Target, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// How the platform indexes what <paramref name="route"/> compares: its property in <paramref name="template"/>, the
    /// template the mapping pins; for a lookup or a search, the properties of <paramref name="found"/>, the schema of the
    /// records found. A route whose property the schemas do not say how to index is refused with why.
    /// </summary>
    public static SearchRouteFields Classify(SearchRoute route, SchemaSnapshot? template, SchemaSnapshot? found)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Problem is { } problem)
        {
            return SearchRouteFields.Refused(problem);
        }

        if (route.Kind == SearchRouteKind.Key)
        {
            var columns = new List<ExplorerViaColumn>();
            foreach (var (column, path) in route.Key?.Others ?? new Dictionary<string, string>())
            {
                var (field, why) = FieldOf(template, path);
                if (field is null)
                {
                    return SearchRouteFields.Refused($"the key's column {column} is held at {path}, which {why}");
                }

                columns.Add(new ExplorerViaColumn { Column = column, Path = field.Path, Index = field.Index });
            }

            return new SearchRouteFields(OsduField.Keyword("id"), KeyColumns: columns);
        }

        var (target, targetProblem) = FieldOf(template, route.Path);
        if (target is null)
        {
            return SearchRouteFields.Refused($"{route.Path} {targetProblem}");
        }

        if (route.Find is not { } find)
        {
            return new SearchRouteFields(target);
        }

        if (found is null)
        {
            return SearchRouteFields.Refused($"no template the mapping reads of {find.Kind} says how its records are indexed; put one in a template folder on the mapping's path to search through {find.Name}");
        }

        var match = new List<OsduField>(find.Lines.Count);
        foreach (var line in find.Lines)
        {
            var (field, why) = FieldOf(found, line.Field);
            if (field is null)
            {
                return SearchRouteFields.Refused($"{find.Name}'s {line.Field} {why}");
            }

            match.Add(field);
        }

        if (find.ReadsId)
        {
            return new SearchRouteFields(target, match, OsduField.Keyword("id"));
        }

        var (read, readProblem) = FieldOf(found, find.Read);
        return read is null ? SearchRouteFields.Refused($"{find.Name}'s {find.Read} {readProblem}") : new SearchRouteFields(target, match, read);
    }

    /// <summary>
    /// The conditions a route takes, the likeliest first: by how its property is indexed and what its steps keep of a value.
    /// A copy, or steps that keep a value's text or order, takes what its property takes; steps that make another value of
    /// it take whole values; a lookup or a search takes the conditions its records are found by; a key takes whole values.
    /// Whether a property holds a value is asked of the property, outside a nested list.
    /// </summary>
    public static IReadOnlyList<ExplorerCondition> Conditions(SearchRoute route, SearchRouteFields fields)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Target is not { } target)
        {
            return [];
        }

        IReadOnlyList<ExplorerCondition> whole = [ExplorerCondition.Is, ExplorerCondition.IsNot, ExplorerCondition.AnyOf, ExplorerCondition.NoneOf];
        IReadOnlyList<ExplorerCondition> presence = target.NestedPath is null && route.Kind != SearchRouteKind.Key ? [ExplorerCondition.Exists, ExplorerCondition.Missing] : [];
        switch (route.Kind)
        {
            case SearchRouteKind.Key:
                return whole;
            case SearchRouteKind.Lookup or SearchRouteKind.Search:
                var match = fields.Match ?? [];
                var words = match.All(f => f.Index == OsduFieldIndex.Text) ? [ExplorerCondition.Contains] : Array.Empty<ExplorerCondition>();
                var start = match.All(f => f.Index is OsduFieldIndex.Text or OsduFieldIndex.Keyword && f.NestedPath is null) ? [ExplorerCondition.StartsWith] : Array.Empty<ExplorerCondition>();
                return [ExplorerCondition.Is, .. words, .. start, ExplorerCondition.IsNot, ExplorerCondition.AnyOf, ExplorerCondition.NoneOf, .. presence];
        }

        var keeps = route.Kind == SearchRouteKind.Copy ? null : (SearchRouteKeeps?)route.Keeps;
        switch (target.Index)
        {
            case OsduFieldIndex.Text when keeps is null or SearchRouteKeeps.Text:
                return [ExplorerCondition.Contains, .. whole, .. target.NestedPath is null ? [ExplorerCondition.StartsWith] : Array.Empty<ExplorerCondition>(), .. presence];
            case OsduFieldIndex.Keyword when keeps is null or SearchRouteKeeps.Text:
                return [.. whole, .. target.NestedPath is null ? [ExplorerCondition.StartsWith] : Array.Empty<ExplorerCondition>(), .. presence];
            case OsduFieldIndex.Number or OsduFieldIndex.Date when keeps is null or SearchRouteKeeps.Order:
                return [ExplorerCondition.Range, .. whole, .. presence];
            case OsduFieldIndex.Boolean:
                return [ExplorerCondition.Is, .. presence];
            default:
                return [.. whole, .. presence];
        }
    }

    /// <summary>
    /// The condition the explorer asks for <paramref name="asked"/> on the term <paramref name="name"/>
    /// (<paramref name="key"/>), searched by <paramref name="route"/>, every value put through <paramref name="values"/>.
    /// </summary>
    /// <exception cref="DeliveryException">The route cannot be searched, takes no such condition, or a value cannot be carried; the message says why.</exception>
    public static ExplorerFilter Resolve(string name, SearchTermKey key, SearchRoute route, SearchRouteFields fields, SearchTermCondition asked, SearchTermValues values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(values);
        if (fields.Target is not { } target)
        {
            throw new DeliveryException($"{name} cannot be searched: {fields.Problem}.");
        }

        if (!Conditions(route, fields).Contains(asked.Condition))
        {
            throw new DeliveryException($"{name} is searched through {route.Path} ({Spoken(route.Kind)}), which takes {string.Join(", ", Conditions(route, fields))}, not {asked.Condition}.");
        }

        var plain = new ExplorerFilter { Path = target.Path, Index = target.Index, Nested = target.NestedPath, Condition = asked.Condition };
        if (asked.Condition is ExplorerCondition.Exists or ExplorerCondition.Missing)
        {
            return plain;
        }

        string Carried(string value)
        {
            var carried = values.Translate(key, route, value);
            return carried.Value ?? throw new DeliveryException($"{name} '{value}' cannot be searched for: {carried.Problem}.");
        }

        IReadOnlyList<string>? listed = asked.Values?.Select(Carried).Distinct(StringComparer.Ordinal).ToList();
        var single = asked.Value is null ? null : Carried(asked.Value);
        var to = string.IsNullOrEmpty(asked.To) ? null : Carried(asked.To);
        var condition = plain with { Value = single, Values = listed, To = to };
        return route.Kind switch
        {
            SearchRouteKind.Key => condition with
            {
                Via = new ExplorerVia
                {
                    Term = name,
                    Key = new ExplorerViaKey
                    {
                        Systems = route.Key!.Systems,
                        EntityType = route.EntityType,
                        Columns = route.Key.Columns,
                        Given = key.Column,
                        FromKey = route.Key.FromKey,
                        Others = fields.KeyColumns ?? [],
                    },
                },
            },
            SearchRouteKind.Lookup or SearchRouteKind.Search => condition with
            {
                Via = new ExplorerVia
                {
                    Term = name,
                    Kind = route.Find!.Kind,
                    Match = fields.Match!.Select(f => new ExplorerField { Path = f.Path, Index = f.Index, Nested = f.NestedPath }).ToList(),
                    Read = new ExplorerField { Path = fields.Read!.Path, Index = fields.Read.Index, Nested = fields.Read.NestedPath },
                },
            },
            _ => condition,
        };
    }

    /// <summary>How a route reaches the record, as a message says it.</summary>
    public static string Spoken(SearchRouteKind kind) => kind switch
    {
        SearchRouteKind.Copy => "copied as it is",
        SearchRouteKind.Steps => "through the mapping's steps",
        SearchRouteKind.Lookup => "through the record a lookup finds",
        SearchRouteKind.Search => "through the record a search finds",
        SearchRouteKind.Key => "as the record's id",
        _ => "through an expression",
    };

    /// <summary>
    /// How the platform indexes <paramref name="path"/> of a record: the record's own properties and its tags alike for
    /// every kind, its content as <paramref name="schema"/> declares it; or why it holds nothing a query compares there.
    /// </summary>
    private static (OsduField? Field, string? Problem) FieldOf(SchemaSnapshot? schema, string path)
    {
        if (!SearchFields.IsDataPath(path))
        {
            var shape = SearchFields.RecordProperty(path);
            return (shape.Field, shape.Problem);
        }

        if (schema is null)
        {
            return (null, "is described by no template the mapping reads, which says how it is indexed");
        }

        var typed = SearchFields.ClassifyValue(schema, path);
        return typed.Field is { } field ? (field, null) : (null, "is not indexed so that a query reaches it: " + typed.Problem);
    }
}
