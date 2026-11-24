using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The table step (osdu/docs/reference/flow/dimension-target.md): the rows a write laid out brought to the dimension's
/// table where it is kept, copied to that database's session first when it is another, and the table recorded in the
/// ledger; and a table laid out again from what the ledger holds, without a build.
/// </summary>
internal static partial class SqlServerDimensionStore
{
    // The table laid out again outside a build: the same steps as a build's write, under the dimension's write lock, from
    // the attributes the declaration names, every key's.
    private const string EnsureTableSql = "SET NOCOUNT ON;\nDECLARE @behind bit = 1;\n" + NamesSql + "\n\n" + LayoutSql
        + "\nSELECT ISNULL(@rewrite, CAST(0 AS bit)), ISNULL(@scoped, CAST(0 AS bit));";

    // What the ledger records of the table once the table step has written it: its name, a version raised when its rows
    // changed, its name or where it is kept changed, the build whose rows it holds (the last build, for a write outside one),
    // and where it is kept; and the table the dimension wrote under another name in the same place before, which goes once
    // no dimension names it.
    private const string RecordTableSql = """
        SET NOCOUNT ON;
        DECLARE @tableWas sysname, @sameTarget bit = 0;
        SELECT @tableWas = d.[TableName],
            @sameTarget = CASE WHEN ISNULL(d.[TableConnection], N'') = ISNULL(@tableConnection, N'')
                AND ISNULL(d.[TableDatabase], N'') = ISNULL(@tableDatabase, N'')
                AND ISNULL(d.[TableSchema], N'osdu') = @tableSchema THEN 1 ELSE 0 END
        FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
        WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d;

        UPDATE d SET d.[TableName] = @tableName,
            d.[TableVersion] = d.[TableVersion] + CASE WHEN @changed > 0 OR d.[TableName] IS NULL OR d.[TableName] <> @tableName OR @sameTarget = 0 THEN 1 ELSE 0 END,
            d.[TableRunId] = ISNULL(@runId, d.[LastRunId]),
            d.[TableConnection] = @tableConnection, d.[TableDatabase] = @tableDatabase, d.[TableSchema] = NULLIF(@tableSchema, N'osdu')
        FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
        WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d;

        SELECT CASE WHEN @tableWas IS NOT NULL AND @tableWas <> @tableName AND @sameTarget = 1
            AND NOT EXISTS (SELECT 1 FROM [osdu].[Dimension] AS d WITH (FORCESEEK) WHERE d.[TableName] = @tableWas) THEN @tableWas END;
        """;

    /// <summary>
    /// The parameters a statement that lays out a dimension's table takes: the table (null leaves it as it is), where it is
    /// kept, and whether it holds rows made per element that the declaration no longer reads (<paramref name="elementRows"/>).
    /// </summary>
    private static void AddTable(SqlCommand command, DimensionTableSpec? table, string partition, bool elementRows, DimensionTableTarget target)
    {
        command.Parameters.Add(new SqlParameter("@tableName", SqlDbType.NVarChar, 128) { Value = (object?)table?.Name ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@rowsSql", SqlDbType.NVarChar, -1) { Value = table is null ? DBNull.Value : DimensionTables.RowsSql(table.Columns.Count) });
        command.Parameters.Add(new SqlParameter("@partitionName", SqlDbType.NVarChar, 256) { Value = partition });
        command.Parameters.Add(new SqlParameter("@elements", SqlDbType.Bit) { Value = table?.HasElements ?? false });
        command.Parameters.Add(new SqlParameter("@elementRows", SqlDbType.Bit) { Value = elementRows });
        AddTarget(command, target);
    }

    /// <summary>Where the table is kept, as the ledger records it: the connection's reference, the database and the schema.</summary>
    private static void AddTarget(SqlCommand command, DimensionTableTarget target)
    {
        command.Parameters.Add(new SqlParameter("@tableConnection", SqlDbType.NVarChar, 2000) { Value = (object?)target.Connection ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@tableDatabase", SqlDbType.NVarChar, 128) { Value = (object?)target.Database ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@tableSchema", SqlDbType.NVarChar, 128) { Value = target.Schema });
    }

    /// <summary>
    /// Whether the table holds rows made per element in the partition while the declaration reads no elements: none of
    /// them matches a row the write lays out, so the table's rows of the partition are written anew. A table that is not
    /// there yet, or has no element column, holds none.
    /// </summary>
    private static async Task<bool> ElementRowsAsync(DimensionTableDatabase home, DimensionTableSpec table, string partition, CancellationToken ct)
    {
        if (table.HasElements)
        {
            return false;
        }

        var qualified = home.Target.Qualified(table.Name);
        await using (var column = new SqlCommand("SELECT CASE WHEN COL_LENGTH(@table, @column) IS NULL THEN 0 ELSE 1 END;", home.Connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            column.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = qualified });
            column.Parameters.Add(new SqlParameter("@column", SqlDbType.NVarChar, 128) { Value = DimensionElementsSpec.Column });
            if (Convert.ToInt32(await column.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
            {
                return false;
            }
        }

        await using var rows = new SqlCommand(
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {qualified} AS t WITH (FORCESEEK) WHERE t.[partition] = @partition AND t.{Bracketed(DimensionElementsSpec.Column)} IS NOT NULL) THEN 1 ELSE 0 END;",
            home.Connection) { CommandTimeout = CommandTimeoutSeconds };
        rows.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
        return Convert.ToInt32(await rows.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// The table step of a write (osdu/docs/reference/flow/dimension-target.md): brings the dimension's table, where
    /// <paramref name="home"/> keeps it, to the rows the write laid out in <c>#DimRow</c> (and <c>#DimScope</c>, the keys an
    /// incremental write lays out) on <paramref name="connection"/>'s session, under the dimension's write lock, and records
    /// the table in the ledger: its name, its version, where it is kept, and the build whose rows it holds
    /// (<paramref name="runId"/>; the dimension's last build when null). A table kept in the module's database is written
    /// in the ledger's transaction; one kept elsewhere is given the laid-out rows, written in a transaction of its own
    /// database, and recorded once that commits, so a table write that fails leaves the ledger saying the table is behind,
    /// and the next build lays it out whole. Answers how many of the table's rows it added, rewrote or deleted.
    /// </summary>
    private static async Task<long> WriteTableAsync(
        SqlConnection connection, DimensionTableDatabase home, short partitionId, int dimensionId, string partition, PreparedTable prepared,
        bool rewrite, bool scoped, long? runId, DimensionWriteClock? clock, CancellationToken ct)
    {
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await LockAsync(connection, transaction, partitionId, dimensionId, clock, DimensionWriteStep.Table, ct).ConfigureAwait(false);
            long changed;
            if (home.InCatalog)
            {
                changed = await ApplyAsync(connection, transaction, prepared, partition, rewrite, scoped, ct).ConfigureAwait(false);
            }
            else
            {
                await ExecuteAsync(home.Connection, null, LayoutTablesSql, ct).ConfigureAwait(false);
                await CopyLayoutAsync(connection, transaction, home.Connection, ct).ConfigureAwait(false);
                var remote = (SqlTransaction)await home.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
                await using (remote.ConfigureAwait(false))
                {
                    changed = await ApplyAsync(home.Connection, remote, prepared, partition, rewrite, scoped, ct).ConfigureAwait(false);
                    await remote.CommitAsync(ct).ConfigureAwait(false);
                }

                await ExecuteAsync(home.Connection, null, "DROP TABLE IF EXISTS #DimRow;\nDROP TABLE IF EXISTS #DimScope;", ct).ConfigureAwait(false);
            }

            string? former;
            await using (var record = Command(connection, transaction, RecordTableSql))
            {
                record.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
                record.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
                record.Parameters.Add(new SqlParameter("@tableName", SqlDbType.NVarChar, 128) { Value = prepared.Table.Name });
                record.Parameters.Add(new SqlParameter("@changed", SqlDbType.BigInt) { Value = changed });
                record.Parameters.Add(new SqlParameter("@runId", SqlDbType.BigInt) { Value = (object?)runId ?? DBNull.Value });
                AddTarget(record, home.Target);
                var answered = await record.ExecuteScalarAsync(ct).ConfigureAwait(false);
                former = answered is string name ? name : null;
            }

            // A table the dimension wrote under an earlier name, where it is kept now, goes once no dimension names it, so a
            // rule that names tables otherwise leaves none behind.
            if (former is not null)
            {
                await ExecuteAsync(home.Connection, home.Joining(transaction), $"DROP TABLE IF EXISTS {home.Target.Qualified(former)};", ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "DROP TABLE IF EXISTS #DimRow;\nDROP TABLE IF EXISTS #DimScope;", ct).ConfigureAwait(false);
            return changed;
        }
    }

    /// <summary>
    /// Runs the statements made for the table (<see cref="DimensionTables.ApplySql"/>) over the rows laid out on
    /// <paramref name="connection"/>'s session, stamping the rows they add and rewrite with the prepared table's time, and
    /// answers how many of the table's rows they added, rewrote or deleted.
    /// </summary>
    private static async Task<long> ApplyAsync(
        SqlConnection connection, SqlTransaction transaction, PreparedTable prepared, string partition, bool rewrite, bool scoped, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, prepared.ApplySql);
        command.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
        command.Parameters.Add(new SqlParameter("@stamp", SqlDbType.DateTime) { Value = prepared.Stamp });
        command.Parameters.Add(new SqlParameter("@rewrite", SqlDbType.Bit) { Value = rewrite });
        command.Parameters.Add(new SqlParameter("@scoped", SqlDbType.Bit) { Value = scoped });
        var changed = new SqlParameter("@changed", SqlDbType.BigInt) { Direction = ParameterDirection.InputOutput, Value = 0L };
        command.Parameters.Add(changed);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return changed.Value is long count ? count : Convert.ToInt64(changed.Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Copies the rows a write laid out (<c>#DimScope</c>, then <c>#DimRow</c>) from the ledger's session to the session of
    /// the database the table is kept in, which made its own two tables of the same shape first.
    /// </summary>
    private static async Task CopyLayoutAsync(SqlConnection connection, SqlTransaction transaction, SqlConnection target, CancellationToken ct)
    {
        foreach (var table in new[] { "#DimScope", "#DimRow" })
        {
            await using var read = Command(connection, transaction, $"SELECT * FROM {table};");
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            using var bulk = new SqlBulkCopy(target)
            {
                DestinationTableName = table,
                BulkCopyTimeout = WriteTimeoutSeconds,
                BatchSize = 5_000,
                EnableStreaming = true,
            };
            await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes sure dimension <paramref name="dimensionId"/> has <paramref name="table"/> with the rows the ledger holds of
    /// it, as a build's write does, without a build: for a dimension built before dimensions had a table, or one whose
    /// table was dropped. The table's key and value columns keep the names it has; where it has none yet, they take
    /// <paramref name="table"/>'s. The rows it adds or rewrites take <paramref name="now"/> as their times. Says whether the
    /// table had to be made or widened.
    /// </summary>
    internal static async Task<bool> EnsureTableAsync(
        OsduDbContext db, DimensionTableDatabase home, short partitionId, int dimensionId, string partition, DimensionTableSpec table, DateTime now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(table);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            var prepared = await PrepareTableAsync(connection, home, partitionId, dimensionId, table, rename: false, now, ct).ConfigureAwait(false);
            var elementRows = await ElementRowsAsync(home, prepared.Table, partition, ct).ConfigureAwait(false);
            bool rewrite;
            bool scoped;
            await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false))
            {
                var transaction = (SqlTransaction)tx.GetDbTransaction();
                await LockAsync(connection, transaction, partitionId, dimensionId, ct).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, "DROP TABLE IF EXISTS #DimName;\n" + NameStageSql, ct).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, LayoutTablesSql, ct).ConfigureAwait(false);
                await CopyAsync(connection, transaction, "#DimName", NameColumns, NameTypes, table.Columns.Select(NameRow), null, ct).ConfigureAwait(false);
                await using (var command = Command(connection, transaction, EnsureTableSql))
                {
                    command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
                    command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });

                    // Written again outside a build, the table is laid out whole: no build's keys stand for what changed.
                    command.Parameters.Add(new SqlParameter("@partial", SqlDbType.Bit) { Value = false });
                    AddTable(command, table, partition, elementRows, home.Target);
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Laying out the table of dimension {dimensionId} answered nothing."));
                    }

                    (rewrite, scoped) = (reader.GetBoolean(0), reader.GetBoolean(1));
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            await WriteTableAsync(connection, home, partitionId, dimensionId, partition, prepared, rewrite, scoped, runId: null, clock: null, ct).ConfigureAwait(false);
            return prepared.Changed;
        }
        catch (Exception ex) when (StoppedBy(ex, ct))
        {
            throw new OperationCanceledException(
                string.Create(CultureInfo.InvariantCulture, $"Writing the table of dimension {dimensionId} again was cancelled, and wrote no row."), ex, ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
