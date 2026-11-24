using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Removing a dimension (osdu/docs/reference/concepts/dimension-tables.md): its rows, table by table and a batch at a
/// time under its write lock, then the dimension's own row, and its table with the last partition writing it.
/// </summary>
internal static partial class SqlServerDimensionStore
{
    // A dimension's rows, table by table, a batch at a time, each batch its own statement so no one transaction holds millions
    // of rows; under the dimension's write lock, held by the session, so a build's write waits or comes first. The dimension's
    // own row goes last: a removal that stops part way leaves it listed, and removing it again finishes the work.
    private const string RemoveSql = """
        SET NOCOUNT ON;
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = @timeout;
        IF @granted < 0
        BEGIN
            SELECT CAST(@granted AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint),
                CAST(NULL AS sysname), CAST(NULL AS sysname), CAST(0 AS bit);
            RETURN;
        END;

        BEGIN TRY
            DECLARE @n bigint, @texts bigint = 0, @attributes bigint = 0, @changes bigint = 0, @keys bigint = 0, @values bigint = 0, @builds bigint = 0;
            DECLARE @table sysname = (SELECT d.[TableName] FROM [osdu].[Dimension] AS d WITH (FORCESEEK) WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d);
            DECLARE @dropped sysname = NULL;

            -- The dimension's rows in its own table, which every partition the flow builds it in writes to, when the table is
            -- kept here; the removal took them from a table kept elsewhere before it began.
            IF @tableHere = 1 AND @table IS NOT NULL AND OBJECT_ID(N'[osdu].' + QUOTENAME(@table), N'U') IS NOT NULL
            BEGIN
                DECLARE @rows nvarchar(max) = N'WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM [osdu].' + QUOTENAME(@table)
                    + N' WHERE [partition] = @partition; IF @@ROWCOUNT < @batch BREAK; END;';
                EXEC sys.sp_executesql @rows, N'@batch int, @partition nvarchar(256)', @batch = @batch, @partition = @partitionName;
            END;

            WHILE 1 = 1 BEGIN DELETE TOP (@batch) c FROM [osdu].[DimensionCollectedText] AS c WITH (FORCESEEK) WHERE c.[PartitionId] = @p AND c.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @texts += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) a FROM [osdu].[DimensionAttribute] AS a WITH (FORCESEEK) WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @attributes += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) e FROM [osdu].[DimensionElement] AS e WITH (FORCESEEK) WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @attributes += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) k FROM [osdu].[DimensionKeyRecord] AS k WITH (FORCESEEK) WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @attributes += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) r FROM [osdu].[DimensionRecord] AS r WITH (FORCESEEK) WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @attributes += @n; IF @n < @batch BREAK; END;

            -- What a build of the dimension staged and left: a build its process stopped before it ended, or one this removal stops.

        """ + SqlServerDimensionStage.DiscardEverySql + """

            WHILE 1 = 1 BEGIN DELETE TOP (@batch) c FROM [osdu].[DimensionChange] AS c WITH (FORCESEEK) WHERE c.[PartitionId] = @p AND c.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @changes += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) v FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK) WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @keys += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) m FROM [osdu].[DimensionMember] AS m WITH (FORCESEEK) WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @values += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) r FROM [osdu].[DimensionRun] AS r WITH (FORCESEEK) WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @builds += @n; IF @n < @batch BREAK; END;
            DELETE n FROM [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK) WHERE n.[PartitionId] = @p AND n.[DimensionId] = @d;
            DELETE d FROM [osdu].[Dimension] AS d WITH (FORCESEEK) WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d;
            SET @n = @@ROWCOUNT;

            -- The table holds every partition the flow builds the dimension in: it goes with the last of them, here, or where
            -- it is kept once this answers.
            DECLARE @last bit = CASE WHEN @table IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [osdu].[Dimension] AS d WITH (FORCESEEK) WHERE d.[TableName] = @table) THEN 1 ELSE 0 END;
            IF @tableHere = 1 AND @last = 1
            BEGIN
                DECLARE @drop nvarchar(400) = N'DROP TABLE IF EXISTS [osdu].' + QUOTENAME(@table) + N';';
                EXEC sys.sp_executesql @drop;
                SET @dropped = @table;
            END;

            EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';
            SELECT @n, @values, @keys, @builds, @changes, @attributes, @texts, @dropped, @table, @last;
        END TRY
        BEGIN CATCH
            EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';
            THROW;
        END CATCH;
        """;

    /// <summary>The rows a removal took from each table, and the dimension's own table when the dimension was the last to write it.</summary>
    public sealed record Removed(long Values, long Keys, long Builds, long Changes, long Attributes, long Texts, string? TableDropped);

    /// <summary>
    /// Removes every row of dimension <paramref name="dimensionId"/> in partition <paramref name="partitionId"/> and then the
    /// dimension (<see cref="DimensionStore.RemoveAsync"/>).
    /// </summary>
    /// <exception cref="DeliveryException">Another write of the dimension held its lock past the timeout.</exception>
    /// <remarks>
    /// The dimension's table goes with the last partition writing it, where <paramref name="home"/> keeps it. A table kept in
    /// another database loses the partition's rows before the ledger's are removed, so a removal that fails there leaves the
    /// dimension listed to remove again, and is dropped once the ledger says no dimension names it.
    /// </remarks>
    internal static async Task<Removed> RemoveAsync(OsduDbContext db, DimensionTableDatabase home, short partitionId, int dimensionId, string partition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(home);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            if (!home.InCatalog)
            {
                await RemoveTableRowsAsync(connection, home, partitionId, dimensionId, partition, ct).ConfigureAwait(false);
            }

            await using var command = new SqlCommand(RemoveSql, connection) { CommandTimeout = WriteTimeoutSeconds };
            command.Parameters.Add(new SqlParameter("@tableHere", SqlDbType.Bit) { Value = home.InCatalog });
            command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = RemoveBatch });
            command.Parameters.Add(new SqlParameter("@partitionName", SqlDbType.NVarChar, 256) { Value = partition });
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = string.Create(CultureInfo.InvariantCulture, $"osdu-dimension:{partitionId}:{dimensionId}") });
            command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = LockTimeoutMs });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Removing dimension {dimensionId} returned no counts."));
            }

            if (reader.GetInt64(0) < 0)
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                    $"A build of dimension {dimensionId} held its write lock for more than {LockTimeoutMs / 1000} seconds (sp_getapplock answered {reader.GetInt64(0)}), so nothing was removed. Remove it again when the build has finished."));
            }

            var removed = new Removed(
                reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                await reader.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : reader.GetString(7));
            var table = await reader.IsDBNullAsync(8, ct).ConfigureAwait(false) ? null : reader.GetString(8);
            var last = reader.GetBoolean(9);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (!home.InCatalog && last && table is not null)
            {
                await ExecuteAsync(home.Connection, null, $"DROP TABLE IF EXISTS {home.Target.Qualified(table)};", ct).ConfigureAwait(false);
                removed = removed with { TableDropped = table };
            }

            return removed;
        }
        catch (Exception ex) when (StoppedBy(ex, ct))
        {
            // Each batch is its own statement, so the batches deleted stay deleted and the dimension stays listed.
            throw new OperationCanceledException(
                string.Create(CultureInfo.InvariantCulture, $"Removing dimension {dimensionId} was cancelled part way; removing it again finishes the work."), ex, ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the partition's rows of the dimension's table kept in another database, a batch at a time, each batch its own
    /// statement, under the dimension's write lock held by the ledger's session, as the ledger's removal deletes its own.
    /// </summary>
    private static async Task RemoveTableRowsAsync(
        SqlConnection connection, DimensionTableDatabase home, short partitionId, int dimensionId, string partition, CancellationToken ct)
    {
        string? table;
        await using (var named = new SqlCommand(
            "SELECT d.[TableName] FROM [osdu].[Dimension] AS d WITH (FORCESEEK) WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d;", connection)
            { CommandTimeout = CommandTimeoutSeconds })
        {
            named.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            named.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
            table = await named.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        if (table is null)
        {
            return;
        }

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await LockAsync(connection, transaction, partitionId, dimensionId, ct).ConfigureAwait(false);
            var qualified = home.Target.Qualified(table);
            await using var rows = new SqlCommand(
                $"""
                IF OBJECT_ID(@table, N'U') IS NOT NULL
                    WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM {qualified} WHERE [partition] = @partition; IF @@ROWCOUNT < @batch BREAK; END;
                """,
                home.Connection) { CommandTimeout = WriteTimeoutSeconds };
            rows.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = qualified });
            rows.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = RemoveBatch });
            rows.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
            await rows.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }
}
