using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The statements of an orphaned ledger's deletion (<see cref="LedgerDeletes"/>): what refuses it, the rows of each table
/// of the ledger's kind deleted a slice at a time, each slice committed on its own, and the end that closes the deletion's
/// activity. A row goes after the rows that name it, so a deletion that stops leaves nothing that names a row already gone,
/// and is taken up where it stopped, since every statement only reaches rows still there. Every delete seeks its rows
/// through an index led by the partition and the column it finds them by.
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    // What a deletion of a delivery ledger checks before it deletes anything: a run still working on the ledger (a lease
    // alive, or a submission a session holds), which it waits for; and what only this ledger knows of OSDU, which refuses it:
    // a record that claimed an OSDU id and is not marked removed, so OSDU may still hold it; a record whose access in OSDU is
    // restricted; and an item an unfinished delivery left in OSDU that its undo has still to take back.
    private const string DeleteBlocksSql = """
        SET NOCOUNT ON;
        SELECT
            CAST(CASE
                WHEN EXISTS (
                    SELECT 1 FROM [osdu].[Lease]
                    WHERE [PartitionId] = @partition AND [FlowId] = @ledger AND [ExpiresUtc] > @now) THEN 1
                WHEN EXISTS (
                    SELECT 1 FROM [osdu].[Submission] AS s
                    WHERE s.[PartitionId] = @partition AND s.[FlowId] = @ledger AND s.[Status] IN (N'received', N'planned', N'running')
                      AND APPLOCK_TEST(N'public', N'SqlFlow.Delivery.Submission:' + LOWER(CONVERT(nvarchar(36), s.[SubmissionId])), N'Exclusive', N'Session') = 0) THEN 1
                ELSE 0 END AS bit),
            (SELECT COUNT_BIG(*) FROM [osdu].[Record] WITH (READCOMMITTEDLOCK)
             WHERE [PartitionId] = @partition AND [FlowId] = @ledger AND [ClaimedTargetId] IS NOT NULL AND [Status] <> N'deleted'),
            (SELECT COUNT_BIG(*) FROM [osdu].[Record] WITH (READCOMMITTEDLOCK)
             WHERE [PartitionId] = @partition AND [FlowId] = @ledger AND [RestrictionId] IS NOT NULL),
            (SELECT COUNT_BIG(*) FROM [osdu].[Artifact] WITH (READCOMMITTEDLOCK)
             WHERE [PartitionId] = @partition AND [FlowId] = @ledger AND [State] IN (N'intent', N'pending', N'due', N'failed'));
        """;

    // The removals an inventory ledger holds: each took records out of OSDU, and its rows are what a restore brings them
    // back from, so a ledger holding one is never deleted.
    private const string InventoryRemovalsSql = """
        SET NOCOUNT ON;
        SELECT COUNT_BIG(*)
        FROM [osdu].[Inventory] AS i WITH (READCOMMITTEDLOCK)
        INNER JOIN [osdu].[InventoryRemoval] AS r WITH (READCOMMITTEDLOCK) ON r.[PartitionId] = i.[PartitionId] AND r.[InventoryId] = i.[InventoryId]
        WHERE i.[PartitionId] = @partition AND i.[FlowId] = @ledger;
        """;

    private const string CloseDeleteSql = """
        UPDATE [osdu].[Activity]
        SET [Outcome] = @outcome, [CompletedUtc] = @now, [Summary] = @summary
        WHERE [PartitionId] = @partition AND [ActivityId] = @activity AND [Outcome] = N'running';
        """;

    /// <summary>The tables a deletion empties and the columns it finds their rows by: no other name reaches the SQL.</summary>
    private static readonly HashSet<string> DeletedTables = new(
        LedgerDeletes.Trees.Values.SelectMany(LedgerDeletes.Flatten).Select(t => t.Table), StringComparer.Ordinal);

    private static readonly HashSet<string> DeleteColumns = new(
        LedgerDeletes.Trees.Values.SelectMany(LedgerDeletes.Flatten).Where(t => t.Key is not null).Select(t => t.Key!).Append("FlowId"), StringComparer.Ordinal);

    /// <summary>What a deletion of a delivery ledger checks before it deletes anything.</summary>
    /// <param name="Working">A run works on the ledger: a worker holds a lease on its records, or a run holds a submission of it.</param>
    /// <param name="InOsdu">Records that claimed an OSDU id and are not marked removed: OSDU may still hold them.</param>
    /// <param name="Restricted">Records whose access in OSDU is restricted.</param>
    /// <param name="ToUndo">Items unfinished deliveries left in OSDU that an undo has still to take back.</param>
    public readonly record struct DeleteBlocks(bool Working, long InOsdu, long Restricted, long ToUndo);

    public static async Task<DeleteBlocks> DeleteBlocksAsync(OsduDbContext db, short partition, Guid ledger, DateTime now, CancellationToken ct)
    {
        await using var command = await DeleteCommandAsync(db, DeleteBlocksSql, partition, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@ledger", SqlDbType.UniqueIdentifier) { Value = new SqlGuid(ledger) });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return new DeleteBlocks(reader.GetBoolean(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    /// <summary>The removals the inventories of <paramref name="ledger"/> hold.</summary>
    public static async Task<long> InventoryRemovalsAsync(OsduDbContext db, short partition, Guid ledger, CancellationToken ct)
    {
        await using var command = await DeleteCommandAsync(db, InventoryRemovalsSql, partition, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@ledger", SqlDbType.UniqueIdentifier) { Value = new SqlGuid(ledger) });
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Deletes a slice of <paramref name="table"/>'s rows whose <paramref name="column"/> names <paramref name="parent"/> (a
    /// ledger's identity, or the key of a row of the table above it); returns how many.
    /// </summary>
    public static async Task<int> DeleteSliceAsync(
        OsduDbContext db, string table, string column, object parent, bool wide, short partition, int slice, CancellationToken ct)
    {
        await using var command = await DeleteCommandAsync(
            db, $"DELETE TOP (@slice) FROM [osdu].[{DeleteTable(table)}] WHERE [PartitionId] = @partition AND [{DeleteColumn(column)}] = @parent;", partition, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@slice", SqlDbType.Int) { Value = slice });
        command.Parameters.Add(Parent(parent, wide));
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The first keys, in key order, of <paramref name="table"/>'s rows whose <paramref name="column"/> names <paramref name="parent"/>.</summary>
    public static async Task<IReadOnlyList<long>> DeleteKeysAsync(
        OsduDbContext db, string table, string key, string column, object parent, bool wide, short partition, int slice, CancellationToken ct)
    {
        await using var command = await DeleteCommandAsync(
            db,
            $"SELECT TOP (@slice) CAST([{DeleteColumn(key)}] AS bigint) FROM [osdu].[{DeleteTable(table)}] WHERE [PartitionId] = @partition AND [{DeleteColumn(column)}] = @parent ORDER BY [{DeleteColumn(key)}];",
            partition, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@slice", SqlDbType.Int) { Value = slice });
        command.Parameters.Add(Parent(parent, wide));
        var keys = new List<long>(slice);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetInt64(0));
        }

        return keys;
    }

    /// <summary>Deletes the rows of <paramref name="table"/> numbered <paramref name="keys"/>, once nothing names them; returns how many.</summary>
    public static async Task<int> DeleteRowsAsync(
        OsduDbContext db, string table, string key, IReadOnlyList<long> keys, bool wide, short partition, CancellationToken ct)
    {
        await using var command = await DeleteCommandAsync(
            db,
            $"DELETE t FROM [osdu].[{DeleteTable(table)}] AS t WHERE t.[PartitionId] = @partition AND t.[{DeleteColumn(key)}] IN (SELECT CAST(j.[value] AS {(wide ? "bigint" : "int")}) FROM OPENJSON(@keys) AS j);",
            partition, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@keys", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(keys) });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes one page of a paged table's rows whose <paramref name="column"/> names <paramref name="parent"/>, in key
    /// order, in one transaction with the rows of its children that name them; returns how many rows of the table went.
    /// </summary>
    public static Task<int> DeletePageAsync(
        OsduDbContext db, LedgerDeleteTable table, string column, object parent, bool wide, short partition, int slice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Key is not { } key || table.Children.Any(c => c.Children.Count > 0))
        {
            throw new ArgumentException($"'{table.Table}' is paged, so it numbers its rows and the tables that name them name nothing further.", nameof(table));
        }

        var name = DeleteTable(table.Table);
        var sql = new StringBuilder();
        sql.AppendLine("SET NOCOUNT ON;");
        sql.AppendLine("DECLARE @page TABLE ([Key] bigint NOT NULL PRIMARY KEY);");
        sql.AppendLine(CultureInfo.InvariantCulture, $"INSERT INTO @page ([Key]) SELECT TOP (@slice) [{DeleteColumn(key)}] FROM [osdu].[{name}] WITH (UPDLOCK) WHERE [PartitionId] = @partition AND [{DeleteColumn(column)}] = @parent ORDER BY [{DeleteColumn(key)}];");
        foreach (var child in table.Children)
        {
            sql.AppendLine(CultureInfo.InvariantCulture, $"DELETE c FROM [osdu].[{DeleteTable(child.Table)}] AS c INNER JOIN @page AS p ON c.[PartitionId] = @partition AND c.[{DeleteColumn(key)}] = p.[Key];");
        }

        sql.AppendLine(CultureInfo.InvariantCulture, $"DELETE t FROM [osdu].[{name}] AS t INNER JOIN @page AS p ON t.[PartitionId] = @partition AND t.[{DeleteColumn(key)}] = p.[Key];");
        sql.AppendLine("SELECT COUNT(*) FROM @page;");
        var statement = sql.ToString();
        return InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, statement, slice);
            command.Parameters.Add(new SqlParameter("@partition", SqlDbType.SmallInt) { Value = partition });
            command.Parameters.Add(Parent(parent, wide));
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }, ct);
    }

    /// <summary>Closes a deletion with <paramref name="outcome"/> and <paramref name="summary"/>; one closed already stays as it ended.</summary>
    public static async Task CloseDeleteAsync(OsduDbContext db, short partition, long activity, string outcome, string summary, DateTime now, CancellationToken ct)
    {
        await using var command = await DeleteCommandAsync(db, CloseDeleteSql, partition, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@activity", SqlDbType.BigInt) { Value = activity });
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.NVarChar, 16) { Value = outcome });
        command.Parameters.Add(new SqlParameter("@summary", SqlDbType.NVarChar, 2000) { Value = Clip(summary) });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary><paramref name="table"/>, when it is a table a deletion empties.</summary>
    private static string DeleteTable(string table)
        => DeletedTables.Contains(table) ? table : throw new ArgumentException($"'{table}' is not a table a ledger's deletion reaches.", nameof(table));

    /// <summary><paramref name="column"/>, when it is a column a deletion finds rows by.</summary>
    private static string DeleteColumn(string column)
        => DeleteColumns.Contains(column) ? column : throw new ArgumentException($"'{column}' is not a column a ledger's deletion finds rows by.", nameof(column));

    /// <summary>The value rows are found by: a ledger's identity, or the key of the row above, typed as its column is.</summary>
    private static SqlParameter Parent(object parent, bool wide) => parent switch
    {
        Guid ledger => new SqlParameter("@parent", SqlDbType.UniqueIdentifier) { Value = new SqlGuid(ledger) },
        long key when wide => new SqlParameter("@parent", SqlDbType.BigInt) { Value = key },
        long key => new SqlParameter("@parent", SqlDbType.Int) { Value = checked((int)key) },
        _ => throw new ArgumentException("Rows are found by a ledger's identity or by a row's key.", nameof(parent)),
    };

    private static async Task<SqlCommand> DeleteCommandAsync(OsduDbContext db, string sql, short partition, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var command = Command((SqlConnection)db.Database.GetDbConnection(), null, sql, slice: null);
        command.Parameters.Add(new SqlParameter("@partition", SqlDbType.SmallInt) { Value = partition });
        return command;
    }
}
