using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Source;

/// <summary>
/// The lookup tables a flow joins into its reads (<c>lookups</c>, osdu/docs/reference/flow/delivery-lookups.md), checked against
/// the tables themselves as the source's layout is built: each lookup table in the database of the table that joins it (a
/// statement joins nothing across databases, which Azure SQL Database does not run), keyed by a uniquely indexed column the
/// joining column can be compared with, holding the columns the flow asks for and an update column a window can read, and
/// giving each row columns whose names no other column of the row has.
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
            var key = Column(held, table, lookup.Key, $"{where}: {path}.key names column '{lookup.Key}'");
            var on = Column(columns, joining, lookup.On, $"{where}: {path}.on names column '{lookup.On}'");
            if (Incomparable(key, on) is { } mismatch)
            {
                throw new FlowValidationException($"{where}: {path} matches {joining}'s column '{on.Name}' ({on.SqlType}) with the key '{key.Name}' of {table} ({key.SqlType}): {mismatch}.");
            }

            var probe = await ProbeKeyAsync(connection, table, key.Name, ct).ConfigureAwait(false);
            if (!probe.Unique)
            {
                throw new FlowValidationException(
                    $"{where}: {path}.key names column '{key.Name}' of {table}, which no unique index without a filter keeps to one row per value, so the join could give a row two lookup rows and read it twice. "
                    + $"The ingestion flow that loads the table creates one from load.keyColumns: [{key.Name}]; an existing table needs it once: CREATE UNIQUE INDEX {SourceObjectName.Quote("UX_" + table.Name + "_" + key.Name)} ON {table.Quoted} ({SourceObjectName.Quote(key.Name)});");
            }

            string? collation = null;
            if (key.IsText)
            {
                collation = probe.Collation is { } named && CollationName().IsMatch(named)
                    ? named
                    : throw new DeliveryException($"Flow '{_flow.Label}': the source names no collation a statement can name for the key '{key.Name}' of {table} ('{probe.Collation}').");
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

            lookups.Add(new IngestionLookup(name, table, key.Name, on.Name, collation, added, updated.Name, $"{name}_{updated.Name}", deleted?.Name));
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

    /// <summary>Whether a unique index without a filter keeps the lookup key to one row per value, and the key's collation.</summary>
    private async Task<(bool Unique, string? Collation)> ProbeKeyAsync(SqlConnection connection, SourceObjectName table, string key, CancellationToken ct)
    {
        await using var command = Command(connection, IngestionSql.LookupKeyProbe(table));
        command.Parameters.Add(new SqlParameter(IngestionSql.ObjectParameter, SqlDbType.NVarChar, 386) { Value = table.ToString() });
        command.Parameters.Add(new SqlParameter(IngestionSql.ColumnParameter, SqlDbType.NVarChar, 128) { Value = key });
        await using var reader = await ExecuteReaderAsync(command, $"check the key of lookup table {table}", ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new DeliveryException($"Flow '{_flow.Label}': the source did not say whether {table} keeps column '{key}' unique.");
        }

        return (reader.GetInt32(0) == 1, reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex CollationName();
}
