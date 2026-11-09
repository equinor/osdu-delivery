using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.SearchTerms;

/// <summary>A cached type a mapping's lookups read, as a cache flow of the repository declares it.</summary>
/// <param name="Name">The type's name (<c>Wellbore</c>).</param>
/// <param name="Origin">Where its rows come from: <c>osdu</c>, <c>table</c> or <c>dictionary</c>.</param>
/// <param name="Kind">For an OSDU type, the kind, or kind pattern, it captures; null for any other.</param>
/// <param name="Fields">The fields it captures, each by its path in the record and the name a mapping reads it by.</param>
public sealed record SearchCacheType(string Name, string Origin, string? Kind, IReadOnlyList<ReferenceFieldSpec> Fields)
{
    /// <summary>The origin of a type whose records are OSDU's own, captured by searching the platform.</summary>
    public const string OsduOrigin = "osdu";

    /// <summary>
    /// The path in the record of the field a mapping reads by <paramref name="field"/> (<c>FacilityName</c>,
    /// <c>NameAliases.AliasName</c>): the captured field of that name, or one captured whole that it reaches into; <c>id</c>
    /// for the record id where the type captures no field of that name, as the render reads it
    /// (<see cref="ReferenceType.MeansRecordId"/>); null for a field the type does not capture.
    /// </summary>
    public string? PathOf(string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        var name = ReferenceField.Normalize(field);
        if (Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) is { } captured)
        {
            // An attribute is read through the records the cached record references, so no path of the record holds it.
            return captured.IsAttribute ? null : captured.Path;
        }

        if (ReferenceField.IsId(field))
        {
            return "id";
        }

        // A field captured whole holds the paths inside it (a wellbore's GeoContexts holds GeoContexts.FieldID).
        var whole = Fields
            .Where(f => !f.IsAttribute && name.StartsWith(f.Name + ".", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Name.Length)
            .FirstOrDefault();
        return whole is null ? null : whole.Path + name[whole.Name.Length..];
    }
}

/// <summary>
/// Compiles the pipelines into search terms (osdu/docs/reference/concepts/search-terms.md): for every column of a table
/// a delivery flow reads, each place the column reaches in the records its mapping renders, and how a value of the
/// column becomes the value written there. A term is the table's column, whichever flows read the table and whichever
/// mappings render it: two flows rendering one table under two versions of a schema give one term, its routes read by
/// both. Nothing is read but the flows' tables, their mappings and the cached types the lookups name: the routes say
/// what a search would ask, and the explorer asks it of the platform.
/// </summary>
/// <remarks>
/// <para>
/// A column written as it stands is a <see cref="SearchRouteKind.Copy"/>; one written through modifiers is a
/// <see cref="SearchRouteKind.Steps"/>, which a search carries by putting the typed value through the same modifiers the
/// render does. A column a lookup or a search finds a record by is a <see cref="SearchRouteKind.Lookup"/> or a
/// <see cref="SearchRouteKind.Search"/>: the record found is what is written, so a search finds those records first. A
/// column of the dataset's key is a <see cref="SearchRouteKind.Key"/>: the record's id is made from it.
/// </para>
/// <para>
/// A route no typed value can be carried by says why (<see cref="SearchRoute.Problem"/>): a value computed by an
/// expression, translated through a cached table, or built into an id from other columns or from a flow's parameter.
/// The access list and the legal block are not routes: they say who may read a record, not what it holds.
/// </para>
/// </remarks>
public static class SearchTermCompiler
{
    /// <summary>The parameter every mapping is given the partition by, which a search knows: the partition it asks.</summary>
    private const string PartitionParameter = RenderContext.DataPartitionParameter;

    /// <summary>The roots of the record whose values a search compares: its content and its tags.</summary>
    private static readonly HashSet<string> SearchedRoots = new(StringComparer.Ordinal) { "data", "tags" };

    /// <summary>
    /// The terms <paramref name="sources"/> give, each with its routes from every pipeline that reads its column into an
    /// entity type: a route read by several mappings (two pipelines, two versions of a mapping) once, as the newest of them
    /// writes it, listing every mapping, kind and source system that reads it.
    /// </summary>
    /// <param name="sources">The pipelines' readings: each mapping a valid document, with the tables its flow reads.</param>
    /// <param name="cacheTypes">The cached types the repository's cache flows declare, by name; null for a type none declares.</param>
    /// <param name="warnings">Where a column read from a dataset its flow does not declare is reported; it gives no route.</param>
    public static IReadOnlyList<CompiledSearchTerm> Compile(IEnumerable<SearchTermSource> sources, Func<string, SearchCacheType?> cacheTypes, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(cacheTypes);
        ArgumentNullException.ThrowIfNull(warnings);

        var routes = new Dictionary<(SearchTermKey Key, string EntityType), Dictionary<string, (SearchRoute Route, string Version)>>();
        foreach (var source in sources.OrderBy(s => s.Mapping.Name, StringComparer.Ordinal).ThenBy(s => s.Mapping.Version, VersionOrder.Instance).ThenBy(s => s.RecordObject, StringComparer.Ordinal))
        {
            var mapping = source.Mapping;
            foreach (var (key, route) in RoutesOf(source, cacheTypes, warnings))
            {
                var at = (key, route.EntityType);
                if (!routes.TryGetValue(at, out var byId))
                {
                    routes[at] = byId = new Dictionary<string, (SearchRoute, string)>(StringComparer.Ordinal);
                }

                if (!byId.TryGetValue(route.Id, out var held))
                {
                    byId[route.Id] = (route, mapping.Version);
                    continue;
                }

                // One route read by several mappings: listed under each, written as the newest version writes it, and within
                // one mapping, a route some alternative carries a value by is kept over one none does. A key's ids are made as
                // every system it is read under makes them.
                var newer = VersionOrder.Instance.Compare(mapping.Version, held.Version) > 0
                    || (string.Equals(mapping.Version, held.Version, StringComparison.Ordinal) && held.Route.Problem is not null && route.Problem is null);
                var kept = newer ? route : held.Route;
                var merged = kept with
                {
                    Mappings = Union(held.Route.Mappings, route.Mappings),
                    Kinds = Union(held.Route.Kinds, route.Kinds),
                    Systems = Union(held.Route.Systems, route.Systems),
                    Key = kept.Key is { } keyOf ? keyOf with { Systems = Union(held.Route.Key?.Systems ?? [], route.Key?.Systems ?? []) } : null,
                };
                byId[route.Id] = (merged, newer ? mapping.Version : held.Version);
            }
        }

        return routes
            .Select(pair => new CompiledSearchTerm(
                pair.Key.Key,
                pair.Key.EntityType,
                pair.Value.Values.Select(v => v.Route).OrderBy(r => r.Target, StringComparer.Ordinal).ThenBy(r => r.Kind).ToList()))
            .OrderBy(t => t.EntityType, StringComparer.Ordinal)
            .ThenBy(t => t.Key.Text, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The values of both lists, each once, in the order they first appear.</summary>
    private static IReadOnlyList<string> Union(IReadOnlyList<string> first, IReadOnlyList<string> second)
        => first.Union(second, StringComparer.Ordinal).ToList();

    /// <summary>Every route a pipeline's reading gives, with the key of the term it belongs to: the table's column.</summary>
    private static IEnumerable<(SearchTermKey Key, SearchRoute Route)> RoutesOf(SearchTermSource source, Func<string, SearchCacheType?> cacheTypes, ICollection<string> warnings)
    {
        var mapping = source.Mapping;
        var copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<(SearchTermKey, SearchRoute)>();
        var unknown = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in mapping.Entries)
        {
            if (entry.IsRepeater || !SearchedRoots.Contains(entry.Target.Root))
            {
                continue;
            }

            var alternatives = entry.Alternatives.Count;
            var place = 0;
            foreach (var node in entry.ValueNodes)
            {
                place++;
                var alternative = alternatives > 0 ? place : (int?)null;
                if (node.Source is null || node.FindAll is not null || !SearchedRoots.Contains(node.Target.Root))
                {
                    continue;
                }

                foreach (var (column, route) in NodeRoutes(mapping, entry, node, alternative, cacheTypes))
                {
                    if (TableOf(source, column.Child) is not { } table)
                    {
                        // Preflight refuses to run a flow whose mapping reads a dataset the flow does not declare; the sync
                        // describes such a flow all the same, so the dataset is reported once and its columns give no term.
                        if (unknown.Add(column.Child!))
                        {
                            warnings.Add($"{mapping.Reference} reads the dataset {column.Child}, which the flow reading {source.RecordObject} does not declare; its columns give no search term.");
                        }

                        continue;
                    }

                    found.Add((SearchTermKey.Of(table, column.Column), route with { Dataset = column.Child }));
                    if (column.Child is null && route.Problem is null && route.Kind == SearchRouteKind.Copy)
                    {
                        copies.TryAdd(column.Column, route.Path);
                    }
                }
            }
        }

        found.AddRange(KeyRoutes(source, copies));
        return found;
    }

    /// <summary>The table a column is read from: the record table for the record's own row, else the child dataset's; null for a dataset the flow does not declare.</summary>
    private static string? TableOf(SearchTermSource source, string? dataset)
        => dataset is null ? source.RecordObject : source.DatasetObjects.GetValueOrDefault(dataset);

    /// <summary>The routes one value node gives, each with the column it carries.</summary>
    private static IEnumerable<(DatasetColumn Column, SearchRoute Route)> NodeRoutes(
        MappingDefinition mapping, MappingEntry entry, MappingEntry node, int? alternative, Func<string, SearchCacheType?> cacheTypes)
    {
        var source = node.Source!;
        var steps = node.Modifiers.Select(m => m.ToString()).ToList();
        SearchRoute Route(SearchRouteKind kind, string? problem) => new()
        {
            EntityType = mapping.EntityType,
            Kinds = [mapping.Template.Kind],
            Systems = [mapping.Dataset.System.Trim().ToLowerInvariant()],
            Target = node.Target.Text,
            Path = node.Target.SchemaPath,
            Kind = kind,
            Mappings = [mapping.Reference],
            Location = node.Location ?? entry.Location,
            Alternative = alternative,
            Steps = steps,
            Keeps = KeepsOf(node.Modifiers),
            When = entry.AppliesWhen?.Text,
            Description = node.Description ?? entry.Description,
            Problem = problem,
        };

        switch (source.Kind)
        {
            case MappingSourceKind.DatasetColumn:
                var column = source.Column!;
                yield return (column, Route(steps.Count == 0 ? SearchRouteKind.Copy : SearchRouteKind.Steps, StepsProblem(node, column)));
                break;

            case MappingSourceKind.Expression:
                foreach (var read in source.Expression!.Columns.Distinct())
                {
                    yield return (read, Route(SearchRouteKind.Expression, $"its value is computed by the expression {source.Expression.Text}, which a search cannot run backwards"));
                }

                break;

            case MappingSourceKind.Search:
                foreach (var (read, lines) in LinesByColumn(node))
                {
                    var search = mapping.Searches.TryGetValue(source.CacheType ?? string.Empty, out var declared) ? declared : null;
                    var find = search is null
                        ? null
                        : new SearchRouteFind(search.Name, search.Kind, lines.Select(l => new SearchRouteLine(l.Field)).ToList(), "id");
                    yield return (read, Route(SearchRouteKind.Search, search is null ? $"the mapping declares no search {source.CacheType}" : StepsProblem(node, read)) with { Find = find });
                }

                break;

            case MappingSourceKind.Cache:
                foreach (var (read, lines) in LinesByColumn(node))
                {
                    var (find, problem) = CacheFind(source, lines, cacheTypes);
                    yield return (read, Route(SearchRouteKind.Lookup, problem ?? StepsProblem(node, read)) with { Find = find });
                }

                break;
        }
    }

    /// <summary>What <paramref name="modifiers"/> keep of a value (<see cref="SearchRoute.Keeps"/>).</summary>
    private static SearchRouteKeeps KeepsOf(IReadOnlyList<Modifier> modifiers)
    {
        if (modifiers.All(m => m.Kind is ModifierKind.Trim or ModifierKind.Upper or ModifierKind.Lower or ModifierKind.Split))
        {
            return SearchRouteKeeps.Text;
        }

        return modifiers.All(m => m.Kind is ModifierKind.Trim or ModifierKind.Number or ModifierKind.Date) ? SearchRouteKeeps.Order : SearchRouteKeeps.Value;
    }

    /// <summary>
    /// The lines of a node's <c>findBy</c> that compare a column, grouped by the column they compare: a line of several
    /// comparisons is listed under each column it compares.
    /// </summary>
    private static IEnumerable<(DatasetColumn Column, IReadOnlyList<FindBy> Lines)> LinesByColumn(MappingEntry node)
        => node.FindBy
            .SelectMany(line => line.Comparisons.Where(c => c.Column is not null).Select(c => (Column: c.Column!, Line: line)))
            .GroupBy(pair => pair.Column)
            .Select(group => (group.Key, (IReadOnlyList<FindBy>)group.Select(pair => pair.Line).Distinct().ToList()));

    /// <summary>
    /// Where a cache node finds its record and what it reads of it, in the record's own paths, from the cached type a cache
    /// flow declares; or why a search cannot follow it: a type no cache flow declares, one whose rows are not OSDU records,
    /// or a field the type does not capture.
    /// </summary>
    private static (SearchRouteFind? Find, string? Problem) CacheFind(MappingSource source, IReadOnlyList<FindBy> lines, Func<string, SearchCacheType?> cacheTypes)
    {
        var typeName = source.CacheType ?? string.Empty;
        if (lines.FirstOrDefault(line => line.IsComposite) is { } together)
        {
            // A record found by several values at once is not found by one of them: a search for one value would name
            // every record that shares it.
            var columns = together.Comparisons.Select(c => c.Column is { } column ? column.ToString() : $"'{c.Literal}'");
            return (null, $"its {typeName} record is found by {string.Join(" and ", columns)} together, which one value cannot stand for");
        }

        if (cacheTypes(typeName) is not { } type)
        {
            return (null, $"the cached type {typeName} is declared by no cache flow of the repository");
        }

        if (!string.Equals(type.Origin, SearchCacheType.OsduOrigin, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(type.Kind))
        {
            return (null, $"the cached type {typeName} holds rows of a {type.Origin}, not records OSDU holds, so a search of the platform cannot find them");
        }

        var fields = new List<SearchRouteLine>(lines.Count);
        foreach (var line in lines)
        {
            if (type.PathOf(line.Field) is not { } path)
            {
                return (null, $"the cached type {typeName} does not capture {line.Field}");
            }

            fields.Add(new SearchRouteLine(path));
        }

        var readField = source.CacheField ?? "id";
        return type.PathOf(readField) is { } read
            ? (new SearchRouteFind(typeName, type.Kind!, fields, read), null)
            : (null, $"the cached type {typeName} does not capture {readField}");
    }

    /// <summary>
    /// Why a typed value cannot be put through <paramref name="node"/>'s modifiers as the render puts a value of
    /// <paramref name="column"/>, or null when it can: a table read from the cache, or an id built from a cached table,
    /// from another column or from a parameter of the flow other than the partition.
    /// </summary>
    private static string? StepsProblem(MappingEntry node, DatasetColumn column)
    {
        foreach (var modifier in node.Modifiers)
        {
            if (modifier.Table is { } table)
            {
                return $"it is translated through the cached table {table.CacheType}, which a search does not read";
            }

            if (modifier.Id is not { } id)
            {
                continue;
            }

            if (id.CacheTypes.FirstOrDefault() is { } cached)
            {
                return $"its id is built from the cached table {cached}, which a search does not read";
            }

            if (id.Parameters.FirstOrDefault(p => !string.Equals(p, PartitionParameter, StringComparison.Ordinal)) is { } parameter)
            {
                return $"its id is built from the flow's parameter {parameter}, which a search is not given";
            }

            if (id.Columns.FirstOrDefault(c => c != column) is { } other)
            {
                return $"its id is built from {other} as well";
            }
        }

        return null;
    }

    /// <summary>
    /// The routes of the dataset's key: each of its columns makes the record's id, alone for a key of one column, and with
    /// the others for a key of several, whose values the record must hold where a search can read them.
    /// </summary>
    private static IEnumerable<(SearchTermKey, SearchRoute)> KeyRoutes(SearchTermSource source, IReadOnlyDictionary<string, string> copies)
    {
        var mapping = source.Mapping;
        var system = mapping.Dataset.System.Trim().ToLowerInvariant();
        var columns = mapping.Dataset.Key;
        if (columns.Count == 0)
        {
            yield break;
        }

        foreach (var column in columns)
        {
            var others = columns.Where(c => !string.Equals(c, column, StringComparison.OrdinalIgnoreCase)).ToList();
            var missing = others.FirstOrDefault(o => !copies.ContainsKey(o));
            var held = others.Count == 0 ? null : others.Where(copies.ContainsKey).ToDictionary(o => o, o => copies[o], StringComparer.Ordinal);
            yield return (
                SearchTermKey.Of(source.RecordObject, column),
                new SearchRoute
                {
                    EntityType = mapping.EntityType,
                    Kinds = [mapping.Template.Kind],
                    Systems = [system],
                    Target = "id",
                    Path = "id",
                    Kind = SearchRouteKind.Key,
                    Mappings = [mapping.Reference],
                    Key = new SearchRouteKey([system], columns, mapping.Dataset.IdFrom == MappingIdSource.Key, held),
                    Problem = missing is null
                        ? null
                        : $"the record's id is made from {string.Join(" and ", columns)} together, and the record does not hold {missing} as it stands, so the id cannot be made from {column} alone",
                });
        }
    }

    /// <summary>Mapping versions in their order: as versions where both read as one (<c>1.10.0</c> after <c>1.9.0</c>), else as text.</summary>
    private sealed class VersionOrder : IComparer<string>
    {
        public static VersionOrder Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (Version.TryParse(x, out var a) && Version.TryParse(y, out var b))
            {
                return a.CompareTo(b);
            }

            return string.Compare(x, y, StringComparison.Ordinal);
        }
    }
}
