using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Query;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using SqlFlow.SqlServer.Query;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>The rows a query answered for known values: its columns, in order, and each row's values as a test compares them.</summary>
/// <param name="Columns">The columns, as the query names them.</param>
/// <param name="Rows">Each row's values, in column order.</param>
public sealed record StagedRows(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<ExpectedValue>> Rows);

/// <summary>
/// Answers the queries a run's tests compare with (osdu/docs/reference/flow/staged-proof-values.md): each against the database
/// its connection names, resolved on the node that runs the flow, through SQLFlow's read-only query read (one SELECT, parsed,
/// in a transaction that is always rolled back), its tokens bound as values. A query asked by several assertions or tests is
/// run once per run, with the same values. Every value comes back as a test compares it: a number as a number, text as text,
/// a moment in ISO 8601, and SQL NULL as null.
/// </summary>
public sealed class StagedQueries
{
    private readonly ISecretResolver _secrets;
    private readonly IReadOnlyDictionary<string, string> _values;
    private readonly string _flowName;
    private readonly ConcurrentDictionary<string, Lazy<Task<ExpectedValue>>> _answers = new(StringComparer.Ordinal);

    /// <param name="secrets">Resolves a query's connection reference on this node.</param>
    /// <param name="values">The values the queries' tokens take: the flow's parameters and the partition the run tests.</param>
    /// <param name="flowName">The flow, which the database sessions are named after.</param>
    public StagedQueries(ISecretResolver secrets, IReadOnlyDictionary<string, string> values, string flowName)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        _secrets = secrets;
        _values = values;
        _flowName = flowName;
    }

    /// <summary>The value <paramref name="query"/> answers: exactly one row of one column, with where it came from.</summary>
    /// <exception cref="DeliveryException">The query could not run, or answered no row, several rows or several columns.</exception>
    public Task<ExpectedValue> ValueAsync(StagedQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var read = StagedSql.Read(query, _values);
        var key = Key(query.Connection, read);
        return _answers.GetOrAdd(key, _ => new Lazy<Task<ExpectedValue>>(() => AskAsync(query, read, ct))).Value;
    }

    /// <summary>The rows <paramref name="query"/> answers, at most <paramref name="maxRows"/>.</summary>
    /// <exception cref="DeliveryException">The query could not run, answered more rows than <paramref name="maxRows"/>, or names a column twice.</exception>
    public async Task<StagedRows> RowsAsync(StagedQuery query, int maxRows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        var read = StagedSql.Read(query, _values);
        IReadOnlyList<string> columns = [];
        var rows = new List<IReadOnlyList<ExpectedValue>>();
        var over = false;
        await RunAsync(query, read, (names, record) =>
        {
            if (rows.Count == 0)
            {
                columns = names.Select(c => c.Name).ToList();
                var twice = columns.GroupBy(c => c, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (twice is not null || columns.Any(string.IsNullOrWhiteSpace))
                {
                    throw new DeliveryException(twice is not null
                        ? $"{query} answers the column '{twice.Key}' twice; give each column a name of its own (AS name), so a proof value names one."
                        : $"{query} answers a column with no name; name each column (AS name), so a proof value can name it.");
                }
            }

            if (rows.Count == maxRows)
            {
                over = true;
                return false;
            }

            rows.Add(Values(record));
            return true;
        }, ct).ConfigureAwait(false);

        if (over)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"{query} answers more than {maxRows} rows, more than these known values judge, so none was judged: narrow its WHERE, or raise maxRows (at most {KnownValuesAssertion.MaxQueryRows})."));
        }

        return new StagedRows(columns, rows);
    }

    private async Task<ExpectedValue> AskAsync(StagedQuery query, ReadOnlyRead read, CancellationToken ct)
    {
        var count = 0;
        var width = 0;
        ExpectedValue? value = null;
        await RunAsync(query, read, (columns, record) =>
        {
            width = columns.Count;
            if (++count == 1 && width == 1)
            {
                value = Values(record)[0];
            }

            return count < 2;
        }, ct).ConfigureAwait(false);

        if (count != 1 || width != 1)
        {
            throw new DeliveryException(count switch
            {
                0 => $"{query} answered no row; a value compared with is one row of one column, so give it an aggregate (COUNT, MAX) or a WHERE that finds one row.",
                1 => string.Create(CultureInfo.InvariantCulture, $"{query} answered {width} columns; a value compared with is one row of one column, so select the one value."),
                _ => $"{query} answered more than one row; a value compared with is one row of one column, so give it an aggregate (COUNT, MAX) or a WHERE that finds one row.",
            });
        }

        return value!.From(query.ToString());
    }

    private async Task RunAsync(StagedQuery query, ReadOnlyRead read, Func<IReadOnlyList<QueryColumn>, IDataRecord, bool> row, CancellationToken ct)
    {
        string connection;
        try
        {
            connection = IngestionConnection.Canonicalize(await _secrets.ResolveAsync(query.Connection, ct).ConfigureAwait(false), _flowName);
        }
        catch (Exception ex) when (ex is SqlFlowException or InvalidOperationException or ArgumentException)
        {
            throw new DeliveryException($"{query}: its connection reference could not be resolved on this node: {SecretHygiene.RedactedMessage(ex.Message)}", ex);
        }

        try
        {
            await SqlServerQueryRunner.ReadAsync(connection, read, row, ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw new DeliveryException($"{query} failed (SQL error {ex.Number}): {SecretHygiene.RedactedMessage(ex.Message)}", ex);
        }
        catch (SqlFlowException ex) when (ex is not DeliveryException)
        {
            throw new DeliveryException($"{query} is not a query a test may run: {ex.Message}", ex);
        }
    }

    /// <summary>The key a query is run once under: its database, its statement and the values it binds.</summary>
    private static string Key(string connection, ReadOnlyRead read)
        => connection + "\u001f" + read.Sql + "\u001f" + string.Join("\u001f", read.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    private static List<ExpectedValue> Values(IDataRecord record)
    {
        var values = new List<ExpectedValue>(record.FieldCount);
        for (var i = 0; i < record.FieldCount; i++)
        {
            values.Add(record.IsDBNull(i) ? ExpectedValue.Null : Value(record.GetValue(i)));
        }

        return values;
    }

    /// <summary>
    /// A value as a test compares it: a whole or decimal number as the number it is, written without the trailing zeros a
    /// column's scale adds (1250.4000 is 1250.4); a float as the double it is; text as text; a moment in ISO 8601, a moment
    /// without an offset read as UTC; true or false; a GUID in its 36-character form; and bytes as hexadecimal text.
    /// </summary>
    internal static ExpectedValue Value(object value) => value switch
    {
        string text => ExpectedValue.OfText(text),
        bool flag => ExpectedValue.OfBoolean(flag),
        byte or sbyte or short or ushort or int or uint or long or ulong
            => ExpectedValue.OfNumber(Convert.ToDouble(value, CultureInfo.InvariantCulture), Convert.ToString(value, CultureInfo.InvariantCulture)!),
        decimal number => ExpectedValue.OfNumber((double)number, number.ToString("0.############################", CultureInfo.InvariantCulture)),
        double number when double.IsFinite(number) => ExpectedValue.OfNumber(number),
        float number when float.IsFinite(number) => ExpectedValue.OfNumber(number),
        double or float => ExpectedValue.OfText(Convert.ToString(value, CultureInfo.InvariantCulture)!),
        DateTime moment => ExpectedValue.OfText(DateTime.SpecifyKind(moment, moment.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : moment.Kind)
            .ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture)),
        DateTimeOffset moment => ExpectedValue.OfText(moment.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture)),
        DateOnly day => ExpectedValue.OfText(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeSpan time => ExpectedValue.OfText(time.ToString("c", CultureInfo.InvariantCulture)),
        Guid guid => ExpectedValue.OfText(guid.ToString("D", CultureInfo.InvariantCulture)),
        byte[] bytes => ExpectedValue.OfText("0x" + Convert.ToHexString(bytes)),
        IFormattable formattable => ExpectedValue.OfText(formattable.ToString(null, CultureInfo.InvariantCulture)),
        _ => ExpectedValue.OfText(value.ToString() ?? string.Empty),
    };
}
