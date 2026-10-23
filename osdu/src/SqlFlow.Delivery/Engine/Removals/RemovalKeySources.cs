using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using SqlFlow.Core;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Removals;

/// <summary>
/// One key column a removal reads, resolved against the delivery flow's record key: the source column, the record key column it
/// fills (in the record key's order), the type that column holds in the record table, and the expression over the raw value.
/// </summary>
public sealed record ResolvedKeyColumn(string Source, string Target, string SqlType, string? Expression);

/// <summary>
/// Reads the raw keys of a removal's key sources (osdu/docs/reference/flow/removal.md, How a key is read), through SQLFlow's own
/// readers and nothing of the removal's: files of any format SQLFlow reads through its registered source readers, with their
/// schema found as they are read (columns unioned across files) and the provenance columns they add, and tables of a source
/// database through SQLFlow's connection resolution, connection factory and SQL dialect. A read never runs a reader's file
/// lifecycle (no copy, archive or delete) and writes nothing.
/// </summary>
public sealed class RemovalKeySources
{
    /// <summary>The provenance column of the file a row came from, as SQLFlow's file readers add it.</summary>
    public const string FileNameColumn = "FileName_DW";

    /// <summary>The provenance column of the row's position in its file.</summary>
    public const string RowNumberColumn = "RowNumber_DW";

    /// <summary>The provenance column of the file's date: what a removed key read from a file is removed as of, unless the source names a column.</summary>
    public const string FileDateColumn = RemovalMapper.FileDateColumn;

    private readonly IReadOnlyList<ISourceReader> _readers;
    private readonly IConnectionResolver _connections;
    private readonly IConnectionFactory _factory;
    private readonly SourceProviderRegistry _providers;
    private readonly ISecretResolver _secrets;

    public RemovalKeySources(
        IEnumerable<ISourceReader> readers, IConnectionResolver connections, IConnectionFactory factory, SourceProviderRegistry providers, ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(secrets);
        _readers = readers.ToList();
        _connections = connections;
        _factory = factory;
        _providers = providers;
        _secrets = secrets;
    }

    /// <summary>
    /// Every row of <paramref name="source"/>: its raw key values in the record key's order (<paramref name="columns"/>), its
    /// removal moment when <paramref name="removedSide"/>, and the file and row it was read from.
    /// </summary>
    /// <param name="source">The key source.</param>
    /// <param name="columns">The key columns it is read through, in the record key's order.</param>
    /// <param name="removedSide">Whether the source lists removed keys, so each row carries the moment its key was removed.</param>
    /// <param name="flowFile">The removal flow's file, which a relative location is anchored to.</param>
    /// <param name="values">The removal flow's parameter values, which a location's <c>{name}</c> tokens take.</param>
    /// <param name="flowName">The removal flow, for messages and the source connection's application name.</param>
    /// <param name="ct">Stops the read.</param>
    /// <exception cref="DeliveryException">The source cannot be read, or lacks a column the removal names; the message names the source.</exception>
    public IAsyncEnumerable<RawRemovalKey> ReadAsync(
        RemovalKeySource source, IReadOnlyList<ResolvedKeyColumn> columns, bool removedSide, string? flowFile, IReadOnlyDictionary<string, string> values, string flowName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        if (columns.Count == 0)
        {
            throw new DeliveryException($"{source.Path}: a key source is read through at least one key column.");
        }

        return source.Reader is { } reader
            ? ReadFilesAsync(source, reader, columns, removedSide, flowFile, values, ct)
            : ReadTableAsync(source, source.Table!, columns, removedSide, flowName, ct);
    }

    private async IAsyncEnumerable<RawRemovalKey> ReadFilesAsync(
        RemovalKeySource source, RemovalReaderSource declared, IReadOnlyList<ResolvedKeyColumn> columns, bool removedSide, string? flowFile,
        IReadOnlyDictionary<string, string> values, [EnumeratorCancellation] CancellationToken ct)
    {
        var reader = _readers.FirstOrDefault(r => r.CanHandle(declared.Type))
            ?? throw new DeliveryException(
                $"{source.Path}.type '{declared.Type}' is not a reader this host has; SQLFlow reads csv, xls, xlsx, json, jsonl, ndjson, xml, parquet, prq, duckdb and delta.");
        var spec = new SourceSpec
        {
            Type = declared.Type,
            Location = await LocationAsync(source, declared.Location, flowFile, values, ct).ConfigureAwait(false),
            Options = await OptionsAsync(source, declared.Options, flowFile, values, ct).ConfigureAwait(false),
        };

        IReadOnlyList<SourceColumn> found;
        try
        {
            found = await reader.GetColumnsAsync(spec, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Readable(ex, ct))
        {
            throw new DeliveryException($"{source.Path}: the {declared.Type} source at '{Shown(spec.Location)}' could not be read: {SecretHygiene.RedactedMessage(ex)}", ex);
        }

        var byName = found.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var selected = new List<SourceColumn>(columns.Count + 3);
        foreach (var column in columns)
        {
            selected.Add(byName.TryGetValue(column.Source, out var match)
                ? match
                : throw new DeliveryException(
                    $"{source.Path}: the {declared.Type} source has no column '{column.Source}', which record key column '{column.Target}' is read from; it has {Listed(found)}."));
        }

        var moment = -1;
        if (removedSide)
        {
            var name = source.RemovedAt?.Name ?? FileDateColumn;
            moment = Index(selected, byName, name)
                ?? throw new DeliveryException(source.RemovedAt is null
                    ? $"{source.Path}: the {declared.Type} source carries no {FileDateColumn} (options.includeFileDate is off), and names no removedAt column: a removed key is removed as of a moment, so name the column holding it."
                    : $"{source.Path}.removedAt: the {declared.Type} source has no column '{name}'; it has {Listed(found)}.");
        }

        var fileName = Index(selected, byName, FileNameColumn) ?? -1;
        var rowNumber = Index(selected, byName, RowNumberColumn) ?? -1;

        SourceReadResult read;
        try
        {
            read = await reader.OpenAsync(spec, selected, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Readable(ex, ct))
        {
            throw new DeliveryException($"{source.Path}: the {declared.Type} source at '{Shown(spec.Location)}' could not be opened: {SecretHygiene.RedactedMessage(ex)}", ex);
        }

        // The reader's file lifecycle (CompleteAsync) is never run: reading keys never copies, archives or deletes a source's files.
        await using var rows = read.Reader;
        while (true)
        {
            bool more;
            try
            {
                more = await rows.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (Readable(ex, ct))
            {
                throw new DeliveryException($"{source.Path}: reading the {declared.Type} source at '{Shown(spec.Location)}' failed: {SecretHygiene.RedactedMessage(ex)}", ex);
            }

            if (!more)
            {
                yield break;
            }

            yield return Row(rows, columns.Count, moment, fileName, rowNumber, rowFallback: null, fileFallback: null);
        }
    }

    private async IAsyncEnumerable<RawRemovalKey> ReadTableAsync(
        RemovalKeySource source, RemovalTableSource table, IReadOnlyList<ResolvedKeyColumn> columns, bool removedSide, string flowName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var dialect = _providers.Dialects.FirstOrDefault(d => d.CanHandle(table.Provider))
            ?? throw new DeliveryException($"{source.Path}.provider: this host reads no {table.Provider} source.");
        ResolvedConnection resolved;
        try
        {
            resolved = await _connections.ResolveAsync(table.Connection, ConnectionRole.Source, table.Provider, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Readable(ex, ct))
        {
            throw new DeliveryException($"{source.Path}.connection could not be resolved on this node: {SecretHygiene.RedactedMessage(ex)}", ex);
        }

        RelationalObject name;
        try
        {
            name = RelationalObject.Parse(table.ObjectName);
        }
        catch (SqlFlowException ex)
        {
            throw new DeliveryException($"{source.Path}.object: {ex.Message}", ex);
        }

        var select = columns.Select(c => dialect.QuoteIdentifier(c.Source)).ToList();
        var moment = -1;
        if (removedSide)
        {
            select.Add(dialect.QuoteIdentifier(source.RemovedAt!.Name));
            moment = select.Count - 1;
        }

        var sql = $"SELECT {string.Join(", ", select)} FROM {dialect.QualifyObject(name)} WHERE 1 = 1 {table.Filter}";
        DbConnection connection;
        try
        {
            connection = await _factory.OpenAsync(resolved, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (Readable(ex, ct))
        {
            throw new DeliveryException($"{source.Path}: removal flow '{flowName}' could not connect to {resolved.RedactedString}: {SecretHygiene.RedactedMessage(ex)}", ex);
        }

        await using (connection.ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = 0;
            DbDataReader rows;
            try
            {
                rows = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (Readable(ex, ct))
            {
                throw new DeliveryException($"{source.Path}: reading {table.ObjectName} failed: {SecretHygiene.RedactedMessage(ex)}", ex);
            }

            await using (rows.ConfigureAwait(false))
            {
                long row = 0;
                while (true)
                {
                    bool more;
                    try
                    {
                        more = await rows.ReadAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (Readable(ex, ct))
                    {
                        throw new DeliveryException($"{source.Path}: reading {table.ObjectName} failed: {SecretHygiene.RedactedMessage(ex)}", ex);
                    }

                    if (!more)
                    {
                        yield break;
                    }

                    row++;
                    yield return Row(rows, columns.Count, moment, fileName: -1, rowNumber: -1, rowFallback: row, fileFallback: table.ObjectName);
                }
            }
        }
    }

    /// <summary>One row's raw values: each key column's, the moment's, and the file and row it came from.</summary>
    private static RawRemovalKey Row(DbDataReader rows, int keys, int moment, int fileName, int rowNumber, long? rowFallback, string? fileFallback)
    {
        var values = new string?[keys];
        for (var i = 0; i < keys; i++)
        {
            values[i] = Raw(rows.IsDBNull(i) ? null : rows.GetValue(i), i);
        }

        return new RawRemovalKey
        {
            Values = values,
            Moment = moment < 0 || rows.IsDBNull(moment) ? null : Raw(rows.GetValue(moment), moment),
            FileName = fileName >= 0 && !rows.IsDBNull(fileName) ? Convert.ToString(rows.GetValue(fileName), CultureInfo.InvariantCulture) : fileFallback,
            RowNumber = rowNumber >= 0 && !rows.IsDBNull(rowNumber) ? Convert.ToInt64(rows.GetValue(rowNumber), CultureInfo.InvariantCulture) : rowFallback,
        };
    }

    /// <summary>
    /// A value a reader returned, as text SQL Server converts back to the value it was: a string as it is, a number in invariant
    /// digits, a moment in ISO 8601 with every digit of its fraction, a GUID in its 36-character form. What SQL Server makes of
    /// the text is the conversion the key goes through, so a typed source (Parquet, a database table) and a text one convert
    /// the same way.
    /// </summary>
    internal static string? Raw(object? value, int column) => value switch
    {
        null or DBNull => null,
        string text => text,
        bool flag => flag ? "1" : "0",
        DateTime moment => moment.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        DateTimeOffset moment => moment.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture),
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        TimeSpan time => time.ToString("c", CultureInfo.InvariantCulture),
        Guid id => id.ToString("D"),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        byte[] => throw new DeliveryException(
            string.Create(CultureInfo.InvariantCulture, $"Key column {column + 1} of the source holds binary data, which no record key is compared by; read the key from a column of text, a number, a moment or a GUID.")),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// The location a reader reads: its <c>{name}</c> tokens filled from the removal flow's values (a value that would climb out
    /// of it, holding a separator or <c>..</c>, is refused), a reference resolved on this node to an absolute path or a URI, and
    /// a relative path anchored to the flow file's folder, as a SQLFlow file flow's is.
    /// </summary>
    private async Task<string?> LocationAsync(RemovalKeySource source, string? declared, string? flowFile, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        if (declared is null)
        {
            return null;
        }

        var location = Substitute(declared, values, $"{source.Path}.location");
        if (SourceLocations.NamesReference(location))
        {
            string resolved;
            try
            {
                resolved = (await _secrets.ResolveAsync(location, ct).ConfigureAwait(false)).Trim();
            }
            catch (Exception ex) when (Readable(ex, ct))
            {
                throw new DeliveryException($"{source.Path}.location '{declared}' names a reference that does not resolve on this node: {SecretHygiene.RedactedMessage(ex)}", ex);
            }

            if (resolved.Length == 0 || (!resolved.Contains("://", StringComparison.Ordinal) && !Path.IsPathRooted(resolved)))
            {
                throw new DeliveryException(
                    $"{source.Path}.location '{declared}' resolves to a relative or empty location; a location that names a reference resolves to an absolute path or a URI, since nothing anchors it to the flow's folder.");
            }

            return resolved;
        }

        if (location.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(location))
        {
            return location;
        }

        var folder = flowFile is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(Path.GetFullPath(flowFile)) ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(folder, location));
    }

    /// <summary>The reader's options, with <c>srcPath</c> resolved as a location is.</summary>
    private async Task<IReadOnlyDictionary<string, string?>> OptionsAsync(
        RemovalKeySource source, IReadOnlyDictionary<string, string?> declared, string? flowFile, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var options = new Dictionary<string, string?>(declared, StringComparer.OrdinalIgnoreCase);
        if (options.TryGetValue(SourceLocations.SrcPathOption, out var path) && path is not null)
        {
            options[SourceLocations.SrcPathOption] = await LocationAsync(source, path, flowFile, values, ct).ConfigureAwait(false);
        }

        return options;
    }

    private static string Substitute(string text, IReadOnlyDictionary<string, string> values, string path)
    {
        foreach (var token in FlowMapper.Tokens(text))
        {
            if (!values.TryGetValue(token, out var value))
            {
                throw new DeliveryException($"{path} uses '{{{token}}}', and this run gives it no value.");
            }

            if (value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal) || value.Contains("..", StringComparison.Ordinal))
            {
                throw new DeliveryException(
                    $"{path} uses '{{{token}}}', and its value '{value}' holds a path separator or '..', which would read outside the location the removal declares.");
            }
        }

        return FlowParameters.Substitute(text, values);
    }

    /// <summary>The index of <paramref name="name"/> in <paramref name="selected"/>, adding it from <paramref name="found"/>; null when the source has no such column.</summary>
    private static int? Index(List<SourceColumn> selected, IReadOnlyDictionary<string, SourceColumn> found, string name)
    {
        var at = selected.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            return at;
        }

        if (!found.TryGetValue(name, out var column))
        {
            return null;
        }

        selected.Add(column);
        return selected.Count - 1;
    }

    private static string Listed(IReadOnlyList<SourceColumn> columns)
    {
        var names = columns.Select(c => c.Name).ToList();
        return names.Count == 0 ? "no columns" : names.Count <= 30 ? string.Join(", ", names) : string.Join(", ", names.Take(30)) + $" and {names.Count - 30} more";
    }

    private static string Shown(string? location) => location is null ? "(options.srcPath)" : location.Length > 200 ? location[..200] + "..." : location;

    /// <summary>A failure of a read that says what went wrong; a cancellation the run asked for propagates as it is.</summary>
    private static bool Readable(Exception ex, CancellationToken ct)
        => !(ex is OperationCanceledException && ct.IsCancellationRequested) && ex is not DeliveryException;
}
