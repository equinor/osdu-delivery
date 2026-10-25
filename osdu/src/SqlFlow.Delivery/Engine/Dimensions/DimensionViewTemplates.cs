using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>What the templates the flow reads say of one join of a view: the entity types the joined column names, and whether the dimension joined reads one of them.</summary>
/// <param name="Alias">The join's alias.</param>
/// <param name="On">The column joined on, as the document writes it.</param>
/// <param name="To">The dimension joined.</param>
/// <param name="Names">The entity types the column's value names, as the template of its records declares it (<c>x-osdu-relationship</c>, or an id's pattern); none when no template says.</param>
/// <param name="Reads">The entity type the joined dimension's kind names; null for a kind of any type, or a dimension not keyed by id.</param>
/// <param name="Verdict"><c>agrees</c>, <c>differs</c>, or <c>unchecked</c> when no template says.</param>
/// <param name="Note">The verdict as a sentence.</param>
public sealed record DimensionViewJoinVerdict(string Alias, string On, string To, IReadOnlyList<string> Names, string? Reads, string Verdict, string Note);

/// <summary>A join a view could make: the column, the dimension, the alias, and why it is offered.</summary>
public sealed record DimensionViewJoinSuggestion(string On, string To, string As, string Note);

/// <summary>
/// The templates the flow reads as a view's joins are checked and suggested by (osdu/docs/reference/flow/dimension-views.md,
/// What the templates say of a view's joins): a join is declared, never inferred, and the template of the records a
/// joined column is read from says which records the column names, so a join to a dimension of another entity type is
/// marked, and the joins a view could make are offered. Nothing here decides what a build writes: the document alone
/// does.
/// </summary>
public static class DimensionViewTemplates
{
    /// <summary>A join the template agrees with.</summary>
    public const string Agrees = "agrees";

    /// <summary>A join to a dimension of another entity type than the template names: it finds nothing.</summary>
    public const string Differs = "differs";

    /// <summary>A join no template says anything of.</summary>
    public const string Unchecked = "unchecked";

    /// <summary>
    /// What the templates <paramref name="flow"/> reads say of each join of <paramref name="view"/>: those of the template
    /// folders on the flow's path, read from <paramref name="templates"/>.
    /// </summary>
    public static async Task<IReadOnlyList<DimensionViewJoinVerdict>> CheckAsync(
        DimensionFlowDefinition flow, DimensionViewSpec view, ITemplateSource templates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(templates);
        var reader = new Reader(templates, flow.SourcePath);
        var read = new Dictionary<string, DimensionSpec>(StringComparer.OrdinalIgnoreCase);
        if (flow.Dimension(view.From) is { } from)
        {
            read[ViewScope.FromAlias] = from;
        }

        var verdicts = new List<DimensionViewJoinVerdict>(view.Joins.Count);
        foreach (var join in view.Joins)
        {
            if (join.View)
            {
                verdicts.Add(new DimensionViewJoinVerdict(join.As, join.On, join.To, [], null, Unchecked,
                    $"The join reads view {join.To}, grouped by the key it is joined on; the templates describe records, not views, so its own joins are checked on its page."));
                continue;
            }

            var to = flow.Dimension(join.To);
            var parts = join.On.Split('.');
            var (alias, column) = parts.Length == 1 ? (ViewScope.FromAlias, parts[0]) : (parts[0], parts[1]);
            if (to is null || !read.TryGetValue(alias, out var holder))
            {
                verdicts.Add(new DimensionViewJoinVerdict(join.As, join.On, join.To, [], null, Unchecked, "The join is not one of the flow's dimensions as its document declares them now."));
                continue;
            }

            verdicts.Add(await VerdictAsync(reader, join.As, join.On, holder, column, to, ct).ConfigureAwait(false));
            read[join.As] = to;
        }

        return verdicts;
    }

    /// <summary>
    /// The joins a view whose rows are <paramref name="from"/>'s could make: each column of it holding a key, joined to the
    /// dimension of the flow keyed by what it holds, one row a key, of the entity type the template names; then the same of
    /// each dimension so joined, through it. A column no template says the records of is offered nothing: a record's id
    /// names a record of any type, and only the template tells which.
    /// </summary>
    public static async Task<IReadOnlyList<DimensionViewJoinSuggestion>> SuggestAsync(
        DimensionFlowDefinition flow, string from, ITemplateSource templates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(templates);
        var root = flow.Dimension(from) ?? throw new DeliveryException($"Dimension flow '{flow.Name}' has no dimension named '{from}'.");
        var reader = new Reader(templates, flow.SourcePath);
        var suggestions = new List<DimensionViewJoinSuggestion>();
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var level = new List<(string Alias, DimensionSpec Dimension)> { (ViewScope.FromAlias, root) };
        for (var depth = 0; depth < 2 && level.Count > 0; depth++)
        {
            var next = new List<(string, DimensionSpec)>();
            foreach (var (alias, holder) in level)
            {
                foreach (var column in Keyed(holder))
                {
                    var on = alias.Length == 0 ? column : $"{alias}.{column}";
                    foreach (var to in flow.Dimensions.Where(d => !ReferenceEquals(d, holder) && !DimensionViews.RowsPerKey(d)))
                    {
                        var (key, _) = DimensionViews.KeyOf(holder, column);
                        if (key is null || DimensionViews.Meets(key, DimensionViews.KeyOf(to)) is not null)
                        {
                            continue;
                        }

                        var verdict = await VerdictAsync(reader, to.Name, on, holder, column, to, ct).ConfigureAwait(false);
                        var samePath = key is DimensionJoinKey.AtPath;
                        if (!samePath && verdict.Verdict != Agrees)
                        {
                            continue;
                        }

                        var name = UniqueAlias(to.Name, aliases);
                        suggestions.Add(new DimensionViewJoinSuggestion(on, to.Name, name, samePath ? $"{on} holds the text {to.Name} is keyed by." : verdict.Note));
                        next.Add((name, to));
                    }
                }
            }

            level = next;
        }

        return suggestions;
    }

    /// <summary>The columns of a dimension's table that hold a key, as the document names them.</summary>
    private static IEnumerable<string> Keyed(DimensionSpec dimension)
    {
        yield return dimension.KeyColumn;
        foreach (var attribute in dimension.Attributes.Where(a => a.Keep != DimensionValueKeep.Value))
        {
            yield return attribute.Name;
        }

        foreach (var field in (dimension.Elements?.Fields ?? []).Where(f => f.Keep != DimensionValueKeep.Value))
        {
            yield return field.Name;
        }
    }

    private static string UniqueAlias(string name, HashSet<string> taken)
    {
        var alias = name;
        for (var n = 2; !taken.Add(alias); n++)
        {
            alias = name + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return alias;
    }

    private static async Task<DimensionViewJoinVerdict> VerdictAsync(
        Reader reader, string alias, string on, DimensionSpec holder, string column, DimensionSpec to, CancellationToken ct)
    {
        var reads = string.Equals(to.Path, "id", StringComparison.Ordinal) ? OsduKind.EntityType(to.Kind) : null;
        var (key, _) = DimensionViews.KeyOf(holder, column);
        if (key is DimensionJoinKey.AtPath at)
        {
            return new DimensionViewJoinVerdict(alias, on, to.Name, [], reads, Agrees, $"{on} holds the text {at.Path} holds, which {to.Name} is keyed by.");
        }

        if (key is DimensionJoinKey.Id { EntityType: { } own })
        {
            return reads is null
                ? new DimensionViewJoinVerdict(alias, on, to.Name, [own], null, Unchecked, $"{on} holds ids of {own}, and {to.Name} reads a kind of any type.")
                : string.Equals(own, reads, StringComparison.OrdinalIgnoreCase)
                    ? new DimensionViewJoinVerdict(alias, on, to.Name, [own], reads, Agrees, $"{on} and {to.Name} both hold ids of {own}.")
                    : new DimensionViewJoinVerdict(alias, on, to.Name, [own], reads, Differs, $"{on} holds ids of {own}, and {to.Name} reads {reads}: the join finds nothing.");
        }

        var names = await reader.NamesAsync(holder, column, ct).ConfigureAwait(false);
        if (names is null)
        {
            return new DimensionViewJoinVerdict(alias, on, to.Name, [], reads, Unchecked,
                $"No template the flow reads of {holder.Kind} says which records {column} names, so the join is not checked. Put the template in a template folder on the flow's path to check it.");
        }

        if (names.Count == 0)
        {
            return new DimensionViewJoinVerdict(alias, on, to.Name, [], reads, Unchecked, $"The template of {holder.Kind} does not declare {column}'s path a reference to any record.");
        }

        if (reads is null)
        {
            return new DimensionViewJoinVerdict(alias, on, to.Name, names, null, Unchecked, $"{column} names {string.Join(" or ", names)}, and {to.Name} reads a kind of any type.");
        }

        return names.Contains(reads, StringComparer.OrdinalIgnoreCase)
            ? new DimensionViewJoinVerdict(alias, on, to.Name, names, reads, Agrees, $"The template says {column} names {string.Join(" or ", names)}, which {to.Name} reads.")
            : new DimensionViewJoinVerdict(alias, on, to.Name, names, reads, Differs,
                $"The template says {column} names {string.Join(" or ", names)}, and {to.Name} reads {reads}: an id carries its entity type, so the join finds nothing.");
    }

    /// <summary>The template the flow reads of each kind pattern a dimension reads, loaded once.</summary>
    private sealed class Reader(ITemplateSource templates, string? document)
    {
        private readonly Dictionary<string, SchemaSnapshot?> _loaded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The entity types the value of <paramref name="column"/> names, as the template of the kind
        /// <paramref name="dimension"/> reads declares it (the newest version of the kinds its pattern matches that the flow
        /// reads); null when no template describes the records the value is read from. Only a column read from the
        /// dimension's own records is described: a key's path, a field of its elements, and an attribute of a dimension keyed
        /// by its records' own id, read in one step.
        /// </summary>
        public async Task<IReadOnlyList<string>?> NamesAsync(DimensionSpec dimension, string column, CancellationToken ct)
        {
            var path = PathOf(dimension, column);
            if (path is null || DimensionPath.Parse(path).Path is not { } parsed)
            {
                return null;
            }

            if (!_loaded.TryGetValue(dimension.Kind, out var schema))
            {
                var (matching, _) = await DimensionBlueprints.MatchingAsync(dimension.Kind, templates, document, ct).ConfigureAwait(false);
                schema = matching.Count > 0 ? matching[0].Schema : null;
                _loaded[dimension.Kind] = schema;
            }

            return schema is null ? null : SchemaPathReader.Read(schema, parsed).References;
        }

        private static string? PathOf(DimensionSpec dimension, string column)
        {
            if (string.Equals(column, dimension.KeyColumn, StringComparison.OrdinalIgnoreCase))
            {
                return dimension.Path;
            }

            if (dimension.Elements?.Fields.FirstOrDefault(f => string.Equals(f.Name, column, StringComparison.OrdinalIgnoreCase)) is { } field)
            {
                var names = dimension.Elements.Parsed.Segments.Select(s => s.Name).ToList();
                var at = names.Take(names.Count - field.Up);
                return field.Path == DimensionElementsSpec.Self ? string.Join('.', at) : string.Join('.', at.Append(field.Path));
            }

            return dimension.Attribute(column) is { IsCollected: false, Steps.Count: 1 } attribute && string.Equals(dimension.Path, "id", StringComparison.Ordinal)
                ? attribute.Steps[0]
                : dimension.Attribute(column) is { IsCollected: true } collected ? collected.Collect : null;
        }
    }
}
