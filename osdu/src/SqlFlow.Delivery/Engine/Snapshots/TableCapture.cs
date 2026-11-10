using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// Reads a table type out of its ingestion table: the key column and the columns the type keeps, every row the table holds
/// that its ingestion flow has not marked deleted. The connection is the flow's <c>source.connection</c>, opened the way a
/// delivery flow opens its own, and every value is kept as the text the delivery reader gives it, so a value read from a
/// cached table and the same value read from a dataset are the same text; a column the type decodes as JSON
/// (<see cref="CacheColumnDecode.Json"/>) is kept as the JSON object its text holds (<see cref="JsonColumnValue"/>). A key
/// the cache could not hold, or a decoded value that holds no JSON object, refuses the capture, naming the rows: nothing
/// of a table is captured unless all of it can be.
/// </summary>
internal static class TableCapture
{
    /// <summary>
    /// How long one statement of a capture may wait on the database. The client applies it to each call that reads the result,
    /// not to the whole read, so a table of millions of rows streams within it.
    /// </summary>
    private const int CommandTimeoutSeconds = 120;

    /// <summary>How many of the keys that refuse a capture its message names.</summary>
    private const int NamedKeys = 5;

    /// <summary>Captures <paramref name="type"/> from its table into a lookup table of the partition's cache.</summary>
    public static async Task<ReferenceType> CaptureAsync(CacheDefinition flow, ReferenceTypeSpec type, ISecretResolver secrets, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        var table = SourceObjectName.Parse(type.Table!);
        await using var connection = await OpenAsync(flow, secrets, ct).ConfigureAwait(false);
        var layout = await LayoutAsync(connection, flow, type, table, ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        // One row past the limit says the table is over it without counting the whole table first.
        var limit = type.RowLimit;
        command.CommandText = $"SELECT TOP ({((long)limit + 1).ToString(CultureInfo.InvariantCulture)}) {layout.Select} FROM {table.Quoted}{layout.Where};";

        var rows = new List<ReferenceItem>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var problems = new List<string>();
        var duplicates = new List<string>();
        var undecoded = new List<string>();
        var undecodedCount = 0;
        var rowNumber = 0;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rowNumber++;
                if (rowNumber > limit)
                {
                    throw new DeliveryException(TooManyRows(flow, type));
                }

                var raw = SourceRow.Stringify(SourceValues.Normalize(reader.GetValue(0)));
                var key = raw?.Trim();
                if (LookupKeys.Problem(key) is { } problem)
                {
                    if (problems.Count < NamedKeys)
                    {
                        problems.Add($"row {rowNumber.ToString(CultureInfo.InvariantCulture)}: the key {problem}");
                    }

                    continue;
                }

                if (seen.TryGetValue(key!, out var first))
                {
                    if (duplicates.Count < NamedKeys)
                    {
                        duplicates.Add($"'{key}' (rows {first.ToString(CultureInfo.InvariantCulture)} and {rowNumber.ToString(CultureInfo.InvariantCulture)})");
                    }

                    continue;
                }

                seen[key!] = rowNumber;
                var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { [type.Key!] = ReferenceValue.Of(key!) };
                for (var i = 0; i < type.Fields.Count; i++)
                {
                    if (SourceRow.Stringify(SourceValues.Normalize(reader.GetValue(i + 1))) is not { } value)
                    {
                        continue;
                    }

                    var field = type.Fields[i];
                    if (field.Decode != CacheColumnDecode.Json)
                    {
                        fields[field.Name] = ReferenceValue.Of(value);
                    }
                    else if (JsonColumnValue.Decode(value, out var undecodable) is { } decoded)
                    {
                        fields[field.Name] = ReferenceValue.From(decoded);
                    }
                    else
                    {
                        undecodedCount++;
                        if (undecoded.Count < NamedKeys)
                        {
                            var kept = string.Equals(field.Path, field.Name, StringComparison.Ordinal) ? string.Empty : $" (kept as {field.Name})";
                            undecoded.Add($"row '{key}', column '{field.Path}'{kept}: the value {undecodable}");
                        }
                    }
                }

                rows.Add(new ReferenceItem(key!, fields));
            }
        }
        catch (SqlException ex)
        {
            throw Failure(flow, table, ex, "read the rows");
        }

        if (problems.Count > 0 || duplicates.Count > 0)
        {
            var reasons = new StringBuilder();
            if (problems.Count > 0)
            {
                reasons.Append(string.Join("; ", problems));
            }

            if (duplicates.Count > 0)
            {
                reasons.Append(reasons.Length > 0 ? "; " : string.Empty)
                    .Append("keys held by more than one row once trimmed: ").Append(string.Join(", ", duplicates));
            }

            throw new DeliveryException(
                $"Cache flow '{flow.Name}': table {table} has rows type {type.Name} could not be keyed by {type.Key}, so nothing was captured: {reasons}. A lookup row is found by its key, so every row needs one of its own.");
        }

        if (undecodedCount > 0)
        {
            var named = undecodedCount > undecoded.Count
                ? $"the first {undecoded.Count.ToString(CultureInfo.InvariantCulture)}: "
                : string.Empty;
            throw new DeliveryException(
                $"Cache flow '{flow.Name}': table {table} has {undecodedCount.ToString(CultureInfo.InvariantCulture)} {(undecodedCount == 1 ? "value" : "values")} type {type.Name} decodes as JSON that {(undecodedCount == 1 ? "holds" : "hold")} no JSON object, so nothing was captured: {named}{string.Join("; ", undecoded)}. A column read with decode: json holds the text of one JSON object in each row, or NULL where a row has none.");
        }

        logger.LogInformation("Captured {Count} {Type} row(s) from table {Table}, keyed by {Key}.", rows.Count, type.Name, table, type.Key);
        return new ReferenceType(type.Name, type.EntityType, rows.OrderBy(r => r.Id, StringComparer.Ordinal), type.Key);
    }

    /// <summary>How many rows a capture of <paramref name="type"/> would hold now, for a plan: every row its ingestion flow has not marked deleted.</summary>
    public static async Task<long> CountAsync(CacheDefinition flow, ReferenceTypeSpec type, ISecretResolver secrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(secrets);
        var table = SourceObjectName.Parse(type.Table!);
        await using var connection = await OpenAsync(flow, secrets, ct).ConfigureAwait(false);
        var layout = await LayoutAsync(connection, flow, type, table, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = $"SELECT COUNT_BIG(*) FROM {table.Quoted}{layout.Where};";
        try
        {
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        catch (SqlException ex)
        {
            throw Failure(flow, table, ex, "count the rows");
        }
    }

    /// <summary>
    /// Why a table holding more rows than <paramref name="type"/> takes (<see cref="ReferenceTypeSpec.RowLimit"/>) is refused,
    /// and what to change: the type's <c>maxRows</c> when the table really holds that many, or the delivery's joins.
    /// </summary>
    public static string TooManyRows(CacheDefinition flow, ReferenceTypeSpec type)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(type);
        var table = SourceObjectName.Parse(type.Table!);
        var said = type.MaxRows is null
            ? string.Create(CultureInfo.InvariantCulture, $"the {LookupKeys.DefaultTableRows} a table type holds when its maxRows says nothing")
            : "its maxRows";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Cache flow '{flow.Name}': table {table} holds more than {type.RowLimit} rows for type {type.Name}, {said}: every row is held in memory with the cache version a render reads. Raise the type's maxRows (at most {LookupKeys.MaxTableRows}) if the table really holds that many, or join it into the delivery flow's reads instead (source.datasets.<name>.lookups), where SQL Server joins it and nothing holds it in memory. Nothing was captured.");
    }

    private static Task<SqlConnection> OpenAsync(CacheDefinition flow, ISecretResolver secrets, CancellationToken ct)
        => IngestionConnection.OpenAsync(
            flow.Source.Connection ?? throw new DeliveryException($"Cache flow '{flow.Name}' declares a type read from a table and no source.connection to read it over."),
            flow.Name,
            secrets,
            ct);

    /// <summary>
    /// The columns a capture selects, the key first, and the filter that leaves out rows the ingestion flow marked deleted,
    /// checked against what the table holds so a missing column is named with the columns the table has.
    /// </summary>
    private static async Task<(string Select, string Where)> LayoutAsync(
        SqlConnection connection, CacheDefinition flow, ReferenceTypeSpec type, SourceObjectName table, CancellationToken ct)
    {
        IReadOnlyDictionary<string, SourceColumn> columns;
        try
        {
            columns = await IngestionColumns.ReadAsync(connection, table, CommandTimeoutSeconds, ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw Failure(flow, table, ex, "read the columns");
        }

        if (columns.Count == 0)
        {
            throw new DeliveryException(
                $"Cache flow '{flow.Name}': the table {table}, which type {type.Name} reads, was not found on the source database, or the identity this node connects with cannot see it. Check the ingestion flow that loads it has run, and that the node's login is granted SELECT on it.");
        }

        var known = string.Join(", ", columns.Keys.OrderBy(c => c, StringComparer.Ordinal));
        if (!columns.TryGetValue(type.Key!, out var key))
        {
            throw new DeliveryException($"Cache flow '{flow.Name}': type {type.Name} is keyed by column '{type.Key}', which table {table} does not hold. Columns: {known}.");
        }

        if (!key.Comparable)
        {
            throw new DeliveryException(
                $"Cache flow '{flow.Name}': type {type.Name} is keyed by column '{type.Key}' of table {table}, whose type {key.SqlType} cannot be compared as a key. Key the table by a text or number column of a bounded length.");
        }

        var missing = type.Fields.Where(f => !columns.ContainsKey(f.Path)).Select(f => $"'{f.Path}'").ToList();
        if (missing.Count > 0)
        {
            throw new DeliveryException(
                $"Cache flow '{flow.Name}': type {type.Name} keeps {(missing.Count == 1 ? "column" : "columns")} {string.Join(", ", missing)}, which table {table} does not hold. Columns: {known}.");
        }

        var untexted = type.Fields
            .Where(f => f.Decode == CacheColumnDecode.Json && !columns[f.Path].IsText)
            .Select(f => $"'{f.Path}' ({columns[f.Path].SqlType})")
            .ToList();
        if (untexted.Count > 0)
        {
            throw new DeliveryException(
                $"Cache flow '{flow.Name}': type {type.Name} decodes {(untexted.Count == 1 ? "column" : "columns")} {string.Join(", ", untexted)} of table {table} as JSON, and only a text column holds the text of a JSON object: char, varchar, nchar, nvarchar, text, ntext or json. Read the column that holds the object's text, or leave decode out to keep the value as text.");
        }

        var select = string.Join(", ", type.Fields.Select(f => SourceObjectName.Quote(columns[f.Path].Name)).Prepend(SourceObjectName.Quote(key.Name)));
        var where = columns.TryGetValue(FlowSystemColumns.DefaultDeleted, out var deleted)
            ? $" WHERE {SourceObjectName.Quote(deleted.Name)} IS NULL"
            : string.Empty;
        return (select, where);
    }

    private static DeliveryException Failure(CacheDefinition flow, SourceObjectName table, SqlException ex, string what)
        => new($"Cache flow '{flow.Name}': the source database refused to {what} of table {table} (SQL error {ex.Number}): {SecretHygiene.RedactedMessage(ex.Message)}", ex);
}
