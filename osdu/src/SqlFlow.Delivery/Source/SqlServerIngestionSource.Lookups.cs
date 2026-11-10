using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Source;

/// <summary>
/// The lookup tables a flow joins into its reads (<c>lookups</c>, osdu/docs/reference/flow/delivery-lookups.md), checked against
/// the tables themselves as the source's layout is built: each lookup table in the database of the table that joins it (a
/// statement joins nothing across databases, which Azure SQL Database does not run), keyed uniquely by the columns its join
/// matches, each comparable with the joining column it matches, holding the columns the flow asks for and an update column a
/// window can read, and giving each row columns whose names no other column of the row has.
/// </summary>
public sealed partial class SqlServerIngestionSource
{
    /// <summary>
    /// The lookups <paramref name="declared"/> for the table <paramref name="joining"/>, whose columns are
    /// <paramref name="columns"/>, as the SQL builders join them, and the columns each gives a row under its prefixed name.
    /// </summary>
    private async Task<(List<IngestionLookup> Lookups, Dictionary<string, SourceColumn> Given)> LookupsAsync(
        SqlConnection connection,
        SourceObjectName joining,
        IReadOnlyDictionary<string, SourceColumn> columns,
        IReadOnlyDictionary<string, FlowSourceLookup> declared,
        string at,
        string where,
        CancellationToken ct)
    {
        var lookups = new List<IngestionLookup>(declared.Count);
        var given = new Dictionary<string, SourceColumn>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, lookup) in declared)
        {
            var path = $"{at}.{name}";
            var table = SourceObjectName.Parse(lookup.Object);
            if (!string.Equals(table.Database, joining.Database, StringComparison.OrdinalIgnoreCase))
            {
                throw new FlowValidationException(
                    $"{where}: {path}.object names {table}, in database {table.Database}, and {joining} is in {joining.Database}. A lookup table is joined in the statement that reads the rows, and no statement reads across two databases (Azure SQL Database runs none); load the lookup table into {joining.Database}.");
            }

            var held = await ReadColumnsAsync(connection, table, $"{path}.object", ct).ConfigureAwait(false);
            var pairs = new List<(SourceColumn Key, SourceColumn On)>(lookup.Join.Count);
            foreach (var (keyName, onName) in lookup.Join)
            {
                var key = Column(held, table, keyName, $"{where}: {path}.join names column '{keyName}'");
                var on = Column(columns, joining, onName, $"{where}: {path}.join.{keyName} names column '{onName}'");
                if (Incomparable(key, on) is { } mismatch)
                {
                    throw new FlowValidationException(
                        $"{where}: {path}.join matches {joining}'s column '{on.Name}' ({on.SqlType}) with column '{key.Name}' of {table} ({key.SqlType}): {mismatch}.");
                }

                pairs.Add((key, on));
            }

            var keys = pairs.Select(p => p.Key.Name).ToList();
            var probe = await ProbeKeyAsync(connection, table, keys, ct).ConfigureAwait(false);
            if (!probe.Unique)
            {
                throw new FlowValidationException(Unkeyed(where, path, table, keys));
            }

            var match = new List<IngestionLookupMatch>(pairs.Count);
            foreach (var (key, on) in pairs)
            {
                string? collation = null;
                if (key.IsText)
                {
                    collation = probe.Collations.GetValueOrDefault(key.Name) is { } named && CollationName().IsMatch(named)
                        ? named
                        : throw new DeliveryException(
                            $"Flow '{_flow.Label}': the source names no collation a statement can name for column '{key.Name}' of {table} ('{probe.Collations.GetValueOrDefault(key.Name)}').");
                }

                match.Add(new IngestionLookupMatch(key.Name, on.Name, collation));
            }

            var updated = Optional(held, _flow.Source.SystemColumns.Updated);
            if (updated is null || !updated.IsMoment)
            {
                throw new FlowValidationException(
                    $"{where}: {path}.object names {table}, which holds no {_flow.Source.SystemColumns.Updated} date and time column: a run reads the records whose lookup row changed by it, so without one a corrected lookup value would never reach the records already delivered. Load the table with a SQLFlow ingestion flow, which stamps it.");
            }

            var deleted = Optional(held, _flow.Source.SystemColumns.Deleted);
            var added = new List<KeyValuePair<string, string>>(lookup.Columns.Count + 1);
            void Give(SourceColumn column)
            {
                var named = $"{name}_{column.Name}";
                if (columns.ContainsKey(named) || !given.TryAdd(named, column with { Name = named, Nullable = true }))
                {
                    throw new FlowValidationException(
                        $"{where}: {path} gives each row column '{named}', which the row already has, from {joining} or another of its lookups; rename the lookup, so the columns it gives are named apart.");
                }

                added.Add(new KeyValuePair<string, string>(column.Name, named));
            }

            foreach (var column in lookup.Columns)
            {
                Give(Column(held, table, column, $"{where}: {path}.columns names column '{column}'"));
            }

            // The lookup row's update moment is given too, whether the flow lists it or not: a record's fingerprint reads it, so a
            // record whose lookup row moved is rendered again.
            if (!added.Any(a => string.Equals(a.Key, updated.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Give(updated);
            }

            lookups.Add(new IngestionLookup(name, table, match, added, updated.Name, $"{name}_{updated.Name}", deleted?.Name));
        }

        return (lookups, given);
    }

    /// <summary>
    /// Why the joining column cannot be compared with the lookup key as the join compares it, or null when it can: two text
    /// columns (compared under the key's collation), two whole-number columns, or two columns of one type. Any other pair would
    /// convert one side for every row, which finds no index and can fail on a value that does not convert.
    /// </summary>
    private static string? Incomparable(SourceColumn key, SourceColumn on)
    {
        if (!key.Comparable)
        {
            return "a key has to be a type an index orders (text with a length, a number, a date, a uniqueidentifier)";
        }

        if (!on.Comparable)
        {
            return "the joining column has to be a type an index orders (text with a length, a number, a date, a uniqueidentifier)";
        }

        if (key.IsText && on.IsText)
        {
            return null;
        }

        if (key.IsInteger && on.IsInteger)
        {
            return null;
        }

        return string.Equals(key.TypeName, on.TypeName, StringComparison.OrdinalIgnoreCase)
            ? null
            : "the two are compared as they are, so they have to be two text columns, two whole numbers, or of one type";
    }

    /// <summary>
    /// The refusal of a lookup whose join's columns <paramref name="keys"/> no unique index keeps to one row, with the index
    /// that would: the ingestion flow's <c>load.keyColumns</c> creates it, and the statement creates it on a table already loaded.
    /// </summary>
    private static string Unkeyed(string where, string path, SourceObjectName table, IReadOnlyList<string> keys)
    {
        var index = "UX_" + table.Name + "_" + string.Join("_", keys);
        if (index.Length > 128)
        {
            index = index[..128];
        }

        var named = keys.Count == 1
            ? $"column '{keys[0]}' of {table}, which no unique index without a filter keeps to one row per value"
            : $"columns {string.Join(", ", keys.Select(k => $"'{k}'"))} of {table}, which no unique index without a filter, on all of them or on some, keeps to one row per combination of values";
        return $"{where}: {path}.join matches {named}, so the join could give a row two lookup rows and read it twice. "
            + $"The ingestion flow that loads the table creates one from load.keyColumns: [{string.Join(", ", keys)}]; an existing table needs it once: "
            + $"CREATE UNIQUE INDEX {SourceObjectName.Quote(index)} ON {table.Quoted} ({string.Join(", ", keys.Select(SourceObjectName.Quote))});";
    }

    /// <summary>
    /// Whether a unique index without a filter, on the columns <paramref name="keys"/> or on some of them, keeps the lookup table
    /// to one row per combination of their values, and each column's collation (null for one that holds no text).
    /// </summary>
    private async Task<(bool Unique, Dictionary<string, string?> Collations)> ProbeKeyAsync(
        SqlConnection connection, SourceObjectName table, IReadOnlyList<string> keys, CancellationToken ct)
    {
        await using var command = Command(connection, IngestionSql.LookupKeyProbe(table));
        command.Parameters.Add(new SqlParameter(IngestionSql.ObjectParameter, SqlDbType.NVarChar, 386) { Value = table.ToString() });
        command.Parameters.Add(new SqlParameter(IngestionSql.KeyNamesParameter, SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(keys) });
        await using var reader = await ExecuteReaderAsync(command, $"check the key of lookup table {table}", ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new DeliveryException($"Flow '{_flow.Label}': the source did not say whether {table} keeps columns {string.Join(", ", keys)} unique.");
        }

        var unique = reader.GetInt32(0) == 1;
        if (!await reader.NextResultAsync(ct).ConfigureAwait(false))
        {
            throw new DeliveryException($"Flow '{_flow.Label}': the source did not name the collation of columns {string.Join(", ", keys)} of {table}.");
        }

        var collations = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            collations[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return (unique, collations);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CollationName();
}
