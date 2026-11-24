using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

// The values a lookup's findBy lines compare, each after the node's modifiers, and why the columns they compare gave
// nothing, as a cache source's and a search source's holds say it.
internal static partial class EntryValues
{
    /// <summary>
    /// The values one findBy line compares, in its order, each after the node's modifiers and trimmed. <paramref name="values"/>
    /// is null when any of them is empty, so the line cannot decide, and the column that gave nothing is added to
    /// <paramref name="emptied"/>. On a line of several comparisons a value naming an OSDU record by its id is compared as the
    /// id it names, so <c>dev:master-data--Well:1:</c> meets the record <c>dev:master-data--Well:1</c>. False when a modifier
    /// held the record, which ends the lookup there.
    /// </summary>
    private static bool TryLineValues(
        MappingEntry entry, FindBy line, ReferenceType type, SourceRow root, SourceRow? item, MappingRenderer renderer, List<string> holds, List<CacheUsage> usages,
        RenderTrail searched, List<EmptiedValue> emptied, out IReadOnlyList<(FindBy Find, string Value)>? values)
    {
        values = null;
        var read = new List<(FindBy Find, string Value)>(line.Comparisons.Count);
        foreach (var find in line.Comparisons)
        {
            string? value;
            object? raw = null;
            if (find.Literal is not null)
            {
                // A quoted text reads the mapping's parameters, as every literal does: '{$param.dataPartition}:...'.
                value = MappingEntry.ExpandParameters(find.Literal, renderer.ParameterValue);
            }
            else
            {
                raw = Read(find.Column!, root, item);
                if (!TryModify(entry, raw, root, item, renderer, holds, usages, searched, out var modified))
                {
                    return false;
                }

                value = modified is bool flag ? (flag ? "true" : "false") : SourceRow.Stringify(modified);
            }

            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (find.Column is { } empty)
                {
                    emptied.Add(Emptied(empty, raw));
                }

                return true;
            }

            if (line.IsComposite && !type.IsLookup && type.MeansRecordId(find.Field) && CachedReferences.Parse(value) is { } named)
            {
                value = named.Id;
            }

            read.Add((find, value));
        }

        values = read;
        return true;
    }

    /// <summary>
    /// A column a findBy line compares that gave nothing to compare: what the row held there when the node's modifiers turned
    /// it into nothing, or null when the row held nothing.
    /// </summary>
    private readonly record struct EmptiedValue(DatasetColumn Column, string? Held);

    private static EmptiedValue Emptied(DatasetColumn column, object? raw)
        => new(column, IsEmpty(raw) ? null : SourceRow.Stringify(raw)?.Trim());

    /// <summary>
    /// Why the columns a node's findBy lines compare gave nothing, as a hold says it: <c>dataset.unit is empty</c>, or, for a
    /// value the node's modifiers turned into nothing, <c>dataset.unit is 'NONE', which its modifiers (replace) turn into no
    /// value</c>, as a <c>$from</c> node says it.
    /// </summary>
    private static string EmptiedText(IReadOnlyList<EmptiedValue> emptied, MappingEntry entry)
    {
        var blank = emptied.Where(e => e.Held is null).Select(e => e.Column.ToString()).Distinct(StringComparer.Ordinal).ToList();
        var modified = emptied.Where(e => e.Held is not null)
            .GroupBy(e => e.Column.ToString(), StringComparer.Ordinal)
            .Select(g => $"{g.Key} is '{Clip(g.First().Held!)}'")
            .ToList();
        var parts = new List<string>();
        if (blank.Count > 0)
        {
            parts.Add($"{string.Join(", ", blank)} is empty");
        }

        if (modified.Count > 0)
        {
            parts.Add($"{string.Join(", ", modified)}, which its modifiers ({ModifierText(entry.Modifiers)}) turn into no value");
        }

        return parts.Count == 0 ? "every value its findBy lines compare is empty" : string.Join(", and ", parts);
    }

    /// <summary>The source columns a cache or search node's findBy lines compare, every comparison of every line.</summary>
    private static IReadOnlyList<DatasetColumn> FindColumns(MappingEntry entry)
        => entry.FindBy.SelectMany(f => f.Comparisons).Select(f => f.Column).OfType<DatasetColumn>().Distinct().ToList();
}
