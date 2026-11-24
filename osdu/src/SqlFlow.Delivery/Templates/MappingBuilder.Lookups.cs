using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Templates;

// What a lookup and a find all lack (osdu/docs/reference/flow/mapping-lookups.md,
// osdu/docs/reference/flow/mapping-lookups-composite.md, osdu/docs/reference/flow/mapping-lookups-find-all.md): a
// lookup's name, the cached type its record is found in and its findBy lines, each comparison of a line of several,
// and the cached field a find all's rows are found by and the value it is compared with.
public static partial class MappingBuilder
{
    /// <summary>The issue target that names a lookup, so the page can open the lookup an issue is about.</summary>
    public static string LookupTarget(string name) => "lookups." + name;

    /// <summary>
    /// What a lookup lacks: a name of its own, the cached type its record is found in, findBy lines comparing columns of the
    /// dataset's own row (the row a lookup reads wherever it is read), and modifiers that change what those lines compare.
    /// </summary>
    private static void LookupIssues(MappingDraftLookup lookup, HashSet<string> names, Action<string, string?> error)
    {
        var name = lookup.Name?.Trim() ?? string.Empty;
        var target = LookupTarget(name);
        var at = $"Lookup '{name}'";
        if (!ColumnName().IsMatch(name))
        {
            error($"Name each lookup with letters, digits, underscores and hyphens; entries read it as {MappingMapper.LookupKey}: <name>.<field>.", target);
            return;
        }

        if (!names.Add(name))
        {
            error($"{at} is declared twice; give each lookup a name of its own.", target);
        }

        if (!ColumnName().IsMatch(lookup.CacheType?.Trim() ?? string.Empty))
        {
            error($"{at}: choose the cached type its record is found in, such as Wellbore.", target);
        }

        if (lookup.FindBy.Count == 0)
        {
            error($"{at}: say how a row finds its record with at least one findBy line, such as FacilityName = wellbore_uwi.", target);
        }

        foreach (var find in lookup.FindBy.SelectMany(line => CompositeIssues(line, at, target, error)))
        {
            if (!FieldPath().IsMatch(find.Field ?? string.Empty) || find.Field!.StartsWith(Marker, StringComparison.Ordinal))
            {
                error($"{at}: a findBy line needs the cached field it compares.", target);
            }
            else if (string.IsNullOrWhiteSpace(find.Literal) && !ColumnName().IsMatch(find.Column?.Trim() ?? string.Empty))
            {
                error(DatasetColumnPattern().IsMatch(find.Column?.Trim() ?? string.Empty)
                    ? $"{at}: findBy {find.Field} compares {find.Column!.Trim()}, a column of a child dataset; a lookup reads the dataset's own row, so it compares a column of that row."
                    : $"{at}: findBy {find.Field} needs the dataset column, or a fixed text, it must equal.", target);
            }
        }

        foreach (var modifier in lookup.Modifiers)
        {
            if (modifier.Kind is "id" or "ref")
            {
                error($"{at}: the {modifier.Kind} modifier builds an id to write, and a lookup finds a record in the cache; the record's id is read with {MappingMapper.LookupKey}: {name}.id.", target);
            }
            else
            {
                ModifierIssue(modifier, null, at, error);
            }
        }

        if (lookup.Modifiers.Count > 0 && lookup.FindBy.Count > 0 && lookup.FindBy.SelectMany(f => f.Comparisons).All(f => !string.IsNullOrWhiteSpace(f.Literal)))
        {
            error($"{at}: modifiers change the dataset value a findBy line compares, and every line compares a fixed text; remove the modifiers.", target);
        }
    }

    /// <summary>
    /// What a find all lacks: the cached field its rows are found by, exactly one value that field is compared with (a
    /// column in the entry's scope, a fixed text, or a field of a declared lookup's record of another type), and fields to
    /// be empty that are named once each and are not the key.
    /// </summary>
    private static void FindAllIssues(
        MappingDraft draft, MappingDraftEntry entry, MappingDraftFindAll findAll, string name, string target, string? scope, Action<string, string?> error)
    {
        var field = findAll.Field?.Trim() ?? string.Empty;
        if (!IsCachedRowField(field))
        {
            error($"{name}: find all needs the cached field the rows are found by, such as FieldIDList.", target);
        }

        var column = findAll.Column?.Trim() ?? string.Empty;
        var lookupPath = findAll.Lookup?.Trim() ?? string.Empty;
        var operands = new[] { column, findAll.Literal ?? string.Empty, lookupPath }.Count(text => !string.IsNullOrWhiteSpace(text));
        if (operands != 1)
        {
            error($"{name}: find all compares {(field.Length == 0 ? "its field" : field)} with one value: a dataset column, a fixed text, or a field of a lookup's record.", target);
        }
        else if (column.Length > 0)
        {
            if (!DatasetColumnPattern().IsMatch(column))
            {
                error($"{name}: find all compares {field} with the dataset column '{column}', which is not a column name.", target);
            }
            else
            {
                ScopeIssue(column, scope, name, error);
            }
        }
        else if (lookupPath.Length > 0)
        {
            var parts = lookupPath.Split('.', 2);
            var lookup = draft.Lookups.FirstOrDefault(l => string.Equals(l.Name?.Trim(), parts[0], StringComparison.Ordinal));
            if (parts.Length < 2 || !FieldPath().IsMatch(parts[1]) || parts[1].StartsWith(Marker, StringComparison.Ordinal))
            {
                error($"{name}: find all reads a field of a lookup's record, named after the lookup, such as wellbore.GeoContexts.FieldID; '{lookupPath}' is not one.", target);
            }
            else if (lookup is null)
            {
                error($"{name}: find all reads lookup '{parts[0]}', which the mapping does not declare.", target);
            }
            else if (string.Equals(lookup.CacheType?.Trim(), entry.CacheType?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                error($"{name}: find all finds {entry.CacheType?.Trim()} rows by a field of the {entry.CacheType?.Trim()} record lookup '{parts[0]}' finds; read that record's field with a lookup input instead.", target);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var empty in findAll.Empty.Select(e => e?.Trim() ?? string.Empty))
        {
            if (!IsCachedRowField(empty))
            {
                error($"{name}: find all names the fields a row must hold nothing under, such as FieldList; '{empty}' is not one.", target);
            }
            else if (!seen.Add(empty))
            {
                error($"{name}: find all asks for {empty} to be empty twice.", target);
            }
            else if (string.Equals(empty, field, StringComparison.OrdinalIgnoreCase))
            {
                error($"{name}: find all finds rows by {field} and asks for {field} to be empty; no row holds both.", target);
            }
        }

        var compared = new HashSet<(string, string)>();
        foreach (var line in findAll.Contains)
        {
            var text = line.Field?.Trim() ?? string.Empty;
            var part = line.Part?.Trim() ?? string.Empty;
            if (!IsCachedRowField(text) || !IsCachedRowField(part))
            {
                error($"{name}: find all compares two fields of the same row, such as GroupName contains CountryName; '{text} contains {part}' does not name two.", target);
            }
            else if (string.Equals(text, part, StringComparison.OrdinalIgnoreCase))
            {
                error($"{name}: find all compares {text} with itself, which every row holding a value meets; name two fields of the row.", target);
            }
            else if (!compared.Add((text.ToUpperInvariant(), part.ToUpperInvariant())))
            {
                error($"{name}: find all asks for {text} to contain {part} twice.", target);
            }
            else if (seen.FirstOrDefault(empty => string.Equals(empty, text, StringComparison.OrdinalIgnoreCase) || string.Equals(empty, part, StringComparison.OrdinalIgnoreCase)) is { } both)
            {
                error($"{name}: find all asks for {both} to be empty and compares it with another field, which a row holding nothing under {both} never meets.", target);
            }
        }

        if (entry.Modifiers.Count > 0 && column.Length == 0)
        {
            error($"{name}: modifiers change the dataset value find all compares, and this one compares none; remove the modifiers.", target);
        }
    }

    /// <summary>A field of a cached row as a find all line names it: a dotted path, never a word of the mapping language.</summary>
    private static bool IsCachedRowField(string field)
        => FieldPath().IsMatch(field) && !field.StartsWith(Marker, StringComparison.Ordinal)
            && !field.StartsWith(MappingSource.CachePrefix + ".", StringComparison.Ordinal);

    /// <summary>A findBy line as the document writes it: the field of the record read, then the column in the entry's scope or a quoted text.</summary>
    /// <summary>
    /// The comparisons of one findBy line of a cache entry or a lookup, each to be checked as a line of one is, after what a
    /// line of several may not do is reported: compare a field twice, which no record meets but by holding both values
    /// under it, or nest comparisons inside a comparison, which the mapping cannot write.
    /// </summary>
    private static IReadOnlyList<MappingDraftFind> CompositeIssues(MappingDraftFind line, string at, string? target, Action<string, string?> error)
    {
        var comparisons = line.Comparisons;
        if (comparisons.Count == 1)
        {
            return comparisons;
        }

        if (comparisons.Skip(1).Any(comparison => comparison.And is { Count: > 0 }))
        {
            error($"{at}: a findBy line compares its fields one after another, joined by and; a comparison holds none of its own.", target);
        }

        var twice = comparisons
            .Select(comparison => comparison.Field?.Trim() ?? string.Empty)
            .Where(field => field.Length > 0)
            .GroupBy(field => field.StartsWith("data.", StringComparison.Ordinal) ? field["data.".Length..] : field, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (twice is not null)
        {
            error($"{at}: findBy compares {twice.Key} twice on one line; a line compares each field once, and a record must meet every comparison of it.", target);
        }

        return comparisons;
    }
}
