using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Data;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Writes a completed dimension build in one transaction (osdu/docs/reference/concepts/dimension-tables.md): what the
/// build staged as it read (<see cref="DimensionStage"/>) is copied into temporary tables where the database holds it,
/// and set-based statements add what is new, change
/// what changed, mark removed what the build no longer found and bring back what it found again, log every change to an
/// original, rewrite the attributes and the records read through of the originals it found, bring the dimension's own
/// table to what the build found, and close the build with its counts. An incremental load's write is partial: it holds
/// the keys it read again and the values they were and are under, every other key and value stays as it is, and nothing
/// is marked removed but a value left with no key. A reader sees the dimension as one build left it, never half of the
/// next. An application lock per dimension keeps two builds of one dimension from writing at once; a build of another
/// dimension writes beside it, since the tables keep each dimension's rows together (osdu/docs/reference/concepts/database.md,
/// How the tables are keyed) and every statement names its dimension, joins included, so a write reads and locks its own
/// rows alone.
/// </summary>
internal static partial class SqlServerDimensionStore
{
    /// <summary>How long one statement reading a dimension's table, or making it ready, may run.</summary>
    private const int CommandTimeoutSeconds = 900;

    /// <summary>
    /// How long a statement or a copy of a write, or of a removal, may run: as long as its rows take (0). A write grows with
    /// its dimension (one of well log curves stages a row for every field of every curve, tens of millions of them), so
    /// no fixed bound suits every dimension, and one too short fails a build that was only large. As SQLFlow's own loads
    /// are, it is bounded by its run instead, whose cancellation stops the statement running; the wait for another write
    /// of the dimension is bounded by the write lock's own timeout.
    /// </summary>
    private const int WriteTimeoutSeconds = 0;

    /// <summary>How long a write waits for another build of the same dimension to finish writing.</summary>
    private const int LockTimeoutMs = 120_000;

    /// <summary>How many times a write the database chose as a deadlock victim is made again.</summary>
    private const int DeadlockAttempts = 3;

    private const string LockSql = """
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout;
        SELECT @granted;
        """;

    private static string Bracketed(string identifier) => DimensionTables.Quoted(identifier);

    /// <summary>The most rows one statement of a removal deletes.</summary>
    private const int RemoveBatch = 20_000;

    /// <summary>
    /// Whether <paramref name="ex"/> is a statement the run's cancellation stopped, which SqlClient reports as a severe error
    /// on the command: told as a cancellation, the run says its build was cancelled, not that it failed.
    /// </summary>
    private static bool StoppedBy(Exception ex, CancellationToken ct)
        => ct.IsCancellationRequested && ex is SqlException or InvalidOperationException;

    private static Task LockAsync(SqlConnection connection, SqlTransaction transaction, short partitionId, int dimensionId, CancellationToken ct)
        => LockAsync(connection, transaction, partitionId, dimensionId, null, DimensionWriteStep.Merge, ct);

    /// <summary>
    /// Takes dimension <paramref name="dimensionId"/>'s write lock in <paramref name="transaction"/>, waiting up to
    /// <see cref="LockTimeoutMs"/> for another build of it to finish writing; with a <paramref name="clock"/>, a wait is
    /// told to its listener and kept in the build's notes as part of <paramref name="step"/>.
    /// </summary>
    private static async Task LockAsync(
        SqlConnection connection, SqlTransaction transaction, short partitionId, int dimensionId, DimensionWriteClock? clock, DimensionWriteStep step, CancellationToken ct)
    {
        async Task<int> TakeAsync(bool wait)
        {
            await using var command = Command(connection, transaction, LockSql);
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = string.Create(CultureInfo.InvariantCulture, $"osdu-dimension:{partitionId}:{dimensionId}") });
            command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = wait ? LockTimeoutMs : 0 });
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        var granted = clock is null ? await TakeAsync(wait: true).ConfigureAwait(false) : await clock.LockAsync(step, TakeAsync).ConfigureAwait(false);
        if (granted < 0)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture,
                    $"Another build of dimension {dimensionId} held its write lock for more than {LockTimeoutMs / 1000} seconds (sp_getapplock answered {granted}), so this build wrote nothing. Build it again when the other has finished."));
        }
    }

    /// <summary>
    /// The entity type a record id names, its second segment (<c>master-data--Wellbore</c>); null for an id that names no
    /// record, or is longer than a key a dimension keeps.
    /// </summary>
    public static string? KeyRecordType(string recordId)
    {
        ArgumentNullException.ThrowIfNull(recordId);
        if (recordId.Length > DeliveryDimensionValue.MaxKeyLength || recordId.Length == 0 || char.IsWhiteSpace(recordId[0]) || char.IsWhiteSpace(recordId[^1]))
        {
            return null;
        }

        var segments = recordId.Split(':');
        return segments.Length >= 3 && segments[0].Length > 0 && segments[1].Length is > 0 and <= DeliveryDimensionKeyRecord.MaxEntityTypeLength
            ? segments[1]
            : null;
    }

    private static readonly string[] NameColumns = ["Name", "Ordinal", "Collected"];

    private static readonly Type[] NameTypes = [typeof(string), typeof(short), typeof(bool)];

    /// <summary>A declared attribute with its place among them, from 1.</summary>
    private static object?[] NameRow(DimensionTableColumn column, int index) => [column.Name, (short)(index + 1), column.Collected];

    /// <summary>
    /// Copies <paramref name="rows"/> into a temporary table as they are listed, never holding a second copy of them; nothing
    /// is sent when there is none. <paramref name="order"/> names the table's key when the rows come in its order, so the
    /// server appends them as they come instead of sorting them first.
    /// </summary>
    private static async Task CopyAsync(
        SqlConnection connection, SqlTransaction transaction, string table, string[] columns, Type[] types, IEnumerable<object?[]> rows, string[]? order,
        CancellationToken ct)
    {
        using var listed = rows.GetEnumerator();
        if (!listed.MoveNext())
        {
            return;
        }

        // Under a table lock a copy into a staging table is logged by the page, not by the row.
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, transaction)
        {
            DestinationTableName = table,
            BatchSize = 10_000,
            BulkCopyTimeout = WriteTimeoutSeconds,
            EnableStreaming = true,
        };
        foreach (var column in columns)
        {
            bulk.ColumnMappings.Add(column, column);
        }

        foreach (var column in order ?? [])
        {
            bulk.ColumnOrderHints.Add(column, SortOrder.Ascending);
        }

        await using var reader = new StreamingDataReader(columns, types, Rows(listed, ct).GetAsyncEnumerator(ct));
        await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
    }

    /// <summary>The rows <paramref name="listed"/> lists, from the one it stands on.</summary>
    private static async IAsyncEnumerable<object?[]> Rows(IEnumerator<object?[]> listed, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var count = 0;
        do
        {
            if (++count % 50_000 == 0)
            {
                // A long copy lets other work on the thread pool run, and notices a cancellation, now and then.
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            yield return listed.Current;
        }
        while (listed.MoveNext());
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql)
        => new(sql, connection, transaction) { CommandTimeout = WriteTimeoutSeconds };

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
