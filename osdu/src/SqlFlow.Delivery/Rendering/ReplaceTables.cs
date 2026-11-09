using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Rendering;

/// <summary>What looking a value up in a replace table found.</summary>
internal enum ReplaceLookupKind
{
    /// <summary>The table lists the value: <see cref="ReplaceLookup.Value"/> is what it becomes, null for no value.</summary>
    Found,

    /// <summary>The table does not list the value; the replace's <c>otherwise</c> decides.</summary>
    NotListed,

    /// <summary>Several rows answer to the value and they give different values, so none of them is taken.</summary>
    Ambiguous,
}

/// <summary>
/// The outcome of one replace lookup: what the value becomes, and the rows that decided it, so a replace reading a cached
/// table can record every row a render depended on.
/// </summary>
internal readonly record struct ReplaceLookup(ReplaceLookupKind Kind, ReferenceValue? Value, IReadOnlyList<ReferenceItem> Rows)
{
    public static ReplaceLookup NotListed { get; } = new(ReplaceLookupKind.NotListed, null, []);
}

/// <summary>The fields a replace reading a cached table matches the incoming value on, and takes its replacement from.</summary>
internal readonly record struct ReplaceFields(string Match, string Field);

/// <summary>
/// The one way a replace looks a value up, whether its table is written in the mapping or read from the cache. A table is a
/// <see cref="ReferenceType"/> and a value is matched by the cache's own rules (<see cref="ReferenceType.Find(string, string, bool)"/>): an exact
/// match wins, case is ignored only when that finds one row, and a value several rows answer to selects none of them,
/// unless every one of those rows gives the same value, in which case which of them was meant makes no difference.
/// </summary>
internal static class ReplaceTables
{
    /// <summary>The field an inline table's incoming values are held under.</summary>
    public const string KeyField = "key";

    /// <summary>The field an inline table's replacements are held under; a pair replacing with no value holds none.</summary>
    public const string ValueField = "value";

    // An inline table is built once per parsed modifier, not per row: one table serves every render of a mapping, and a
    // table no longer referenced by any mapping goes with it.
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<string, string?>, ReferenceType> Inline = [];

    /// <summary>The lookup table of a replace written in the mapping: one row per pair, keyed by the incoming value.</summary>
    public static ReferenceType Of(IReadOnlyDictionary<string, string?> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        return Inline.GetValue(replacements, Build);
    }

    /// <summary>
    /// Looks <paramref name="value"/> up in <paramref name="table"/>: matched on <paramref name="matchField"/>, and replaced
    /// by what the matched row holds at <paramref name="valueField"/>.
    /// </summary>
    public static ReplaceLookup Find(ReferenceType table, string matchField, string valueField, string value)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(matchField);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueField);
        ArgumentNullException.ThrowIfNull(value);
        var found = table.Find(matchField, value);
        if (found.Item is { } row)
        {
            return new ReplaceLookup(ReplaceLookupKind.Found, table.Value(row, valueField), [row]);
        }

        if (!found.IsCaseAmbiguous)
        {
            return ReplaceLookup.NotListed;
        }

        var values = found.CaseVariants.Select(candidate => table.Value(candidate, valueField)).ToList();
        var distinct = values.Select(Canonical).Distinct(StringComparer.Ordinal).Count();
        return distinct == 1
            ? new ReplaceLookup(ReplaceLookupKind.Found, values[0], found.CaseVariants)
            : new ReplaceLookup(ReplaceLookupKind.Ambiguous, null, found.CaseVariants);
    }

    /// <summary>
    /// The fields a replace reading <paramref name="type"/> matches on and replaces by: those it names, or those the table
    /// settles. A lookup table matches on its key and replaces by the one field it holds beside its key (<c>value</c>, for
    /// a dictionary of pairs, and for a table whose rows give no value at all). A type of OSDU records has no key and many
    /// fields, so a replace reading one names both. Null, with the reason, when a field cannot be settled.
    /// </summary>
    public static ReplaceFields? Fields(CachedReplaceTable table, ReferenceType type, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(type);
        problem = null;
        var match = table.Match ?? type.Key;
        if (match is null)
        {
            problem = $"$cache.{type.Name} holds OSDU records ({type.EntityType}), which have no key for a replace to match on; name the field the incoming value is compared with, such as match: Code";
            return null;
        }

        var field = table.Field;
        if (field is null)
        {
            if (!type.IsLookup)
            {
                problem = $"$cache.{type.Name} holds OSDU records ({type.EntityType}); name the field that replaces the value, such as field: id";
                return null;
            }

            var beside = type.FieldNames.Where(name => !name.Equals(type.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (beside.Count > 1)
            {
                problem = $"lookup table {type.Name} holds {beside.Count} fields beside its key {type.Key} ({string.Join(", ", beside)}); name the one that replaces the value, such as field: {beside[0]}";
                return null;
            }

            field = beside.Count == 1 ? beside[0] : ValueField;
        }

        return new ReplaceFields(ReferenceField.Normalize(match), ReferenceField.Normalize(field));
    }

    /// <summary>
    /// The one text a cached value gives a replace: a scalar, or the only value of a set. Null when it holds several
    /// values or an object, which a replace cannot turn one value into.
    /// </summary>
    public static string? SingleText(ReferenceValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Node switch
        {
            JsonValue => value.Text,
            JsonArray { Count: 1 } single when single[0] is JsonValue => value.Text,
            _ => null,
        };
    }

    /// <summary>A value as text two rows can be compared by; no value compares as itself and never equals a text.</summary>
    private static string Canonical(ReferenceValue? value) => value is null ? "\u0000none" : CanonicalJson.ToString(value.Node);

    private static ReferenceType Build(IReadOnlyDictionary<string, string?> replacements)
    {
        var rows = new List<ReferenceItem>(replacements.Count);
        foreach (var (from, to) in replacements)
        {
            var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { [KeyField] = ReferenceValue.Of(from) };
            if (to is not null)
            {
                fields[ValueField] = ReferenceValue.Of(to);
            }

            rows.Add(new ReferenceItem(from, fields));
        }

        return new ReferenceType("replace", ReferenceType.LookupEntityType("replace"), rows, KeyField);
    }
}
