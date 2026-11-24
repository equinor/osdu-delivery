using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// The lookup tables a source's tables join (osdu/docs/reference/flow/delivery-lookups.md): each table's lookups
// as the flow writes them, and the checks of their names, objects, joins and columns.
internal static partial class FlowMapper
{
    /// <summary>A table's <c>lookups</c> as the flow writes them, each name and column trimmed.</summary>
    private static IReadOnlyDictionary<string, FlowSourceLookup> MapLookups(Dictionary<string, FlowSourceLookupYaml>? declared, string at, string source)
    {
        var lookups = new Dictionary<string, FlowSourceLookup>(StringComparer.Ordinal);
        foreach (var (name, lookup) in declared ?? [])
        {
            var where = $"{at}.{name.Trim()}";
            var written = lookup ?? throw new FlowValidationException($"{source}: {where} declares nothing; give its object, join and columns.");
            lookups[name.Trim()] = new FlowSourceLookup
            {
                Object = Require(written.Object, where + ".object", source),
                Join = MapLookupJoin(written.Join, where + ".join", source),
                Columns = (written.Columns ?? []).Select(c => c?.Trim() ?? string.Empty).ToList(),
            };
        }

        return lookups;
    }

    /// <summary>
    /// A lookup's <c>join</c> as the flow writes it, each lookup column to the joining column it matches, trimmed and in the
    /// order written. A lookup column named twice, whatever its case, is refused: SQL Server names a column whatever its case,
    /// so the two would be one column matched twice.
    /// </summary>
    private static IReadOnlyDictionary<string, string> MapLookupJoin(Dictionary<string, string>? declared, string at, string source)
    {
        if (declared is null)
        {
            throw Missing(at, source);
        }

        var join = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, on) in declared)
        {
            if (!join.TryAdd(key?.Trim() ?? string.Empty, on?.Trim() ?? string.Empty))
            {
                throw new FlowValidationException($"{source}: {at} matches lookup column '{key?.Trim()}' twice once case is ignored; match each column of the key once.");
            }
        }

        return join;
    }

    /// <summary>
    /// A table's <c>lookups</c> (osdu/docs/reference/flow/delivery-lookups.md): each named by letters, digits and '_' (the prefix of
    /// the columns it adds), at most once whatever its case, with a three-part object, a join of at least one lookup column to a
    /// joining column, and at least one column, each once. Whether the lookup table is in the joining table's database, keyed
    /// uniquely by the join's columns and holds the columns is settled when a run opens the source, against the tables themselves.
    /// </summary>
    private static void CheckLookups(IReadOnlyDictionary<string, FlowSourceLookup> lookups, string at, string source)
    {
        if (lookups.Count > FlowSourceLookup.MaxPerTable)
        {
            throw new FlowValidationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{source}: {at} joins {lookups.Count} lookup tables; a table joins at most {FlowSourceLookup.MaxPerTable}, each a join every read of it makes."));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lookup) in lookups)
        {
            var where = $"{at}.{name}";
            if (!LookupName().IsMatch(name))
            {
                throw new FlowValidationException(
                    $"{source}: {at} names a lookup '{name}'; a lookup's name is letters, digits and '_', starting with a letter, since it prefixes the columns it adds ({name}_<column>).");
            }

            if (!seen.Add(name))
            {
                throw new FlowValidationException($"{source}: {at} names lookup {name} twice once case is ignored; the columns each adds would share their names.");
            }

            CheckObject(lookup.Object, where + ".object", source);
            if (lookup.Join.Count == 0)
            {
                throw new FlowValidationException(
                    $"{source}: {where}.join matches no column; match each column of {lookup.Object}'s key with the column of the joining table it is found by, such as join: {{ source_mnemonic: curve_mnemonic }}.");
            }

            if (lookup.Join.Count > FlowSourceLookup.MaxJoinColumns)
            {
                throw new FlowValidationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{source}: {where}.join matches {lookup.Join.Count} columns; a lookup matches at most {FlowSourceLookup.MaxJoinColumns}, the most key columns a SQL Server index holds."));
            }

            foreach (var (key, on) in lookup.Join)
            {
                CheckColumn(key, where + ".join", source);
                CheckColumn(on, $"{where}.join.{key}", source);
            }

            if (lookup.Columns.Count == 0)
            {
                throw new FlowValidationException(
                    $"{source}: {where}.columns lists no column; list the columns of {lookup.Object} each row is given, such as columns: [curve_type, unit].");
            }

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in lookup.Columns)
            {
                CheckColumn(column, where + ".columns", source);
                if (!columns.Add(column))
                {
                    throw new FlowValidationException($"{source}: {where}.columns lists '{column}' twice.");
                }

                if (!LookupColumnName().IsMatch($"{name}_{column}"))
                {
                    throw new FlowValidationException(
                        $"{source}: {where}.columns lists '{column}', and a row gives it as {name}_{column}, which a mapping cannot name: a column a mapping reads is letters, digits, '_' and '-'.");
                }
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,99}$")]
    private static partial System.Text.RegularExpressions.Regex LookupName();

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
    private static partial System.Text.RegularExpressions.Regex LookupColumnName();
}
