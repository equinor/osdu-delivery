using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;
using SqlFlow.SqlServer.Schema;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Making a dimension's table ready where it is kept (osdu/docs/reference/flow/dimension-table.md): its schema, its
/// key's and value's columns settled under the names the dimensions writing it recorded, its rows' times, its indexes
/// and its lock, and a table a view reads made ready for the view.
/// </summary>
internal static partial class SqlServerDimensionStore
{
    /// <summary>
    /// What preparing a dimension's table came to: the table as it is now (its key's and its value's columns under the
    /// names they were settled at), the statements that write its rows, whether its schema had to change, and the time the
    /// write stamps the rows it adds and rewrites with (<see cref="DimensionColumnNames.InsertedColumn"/>,
    /// <see cref="DimensionColumnNames.UpdatedColumn"/>).
    /// </summary>
    private sealed record PreparedTable(DimensionTableSpec Table, string ApplySql, bool Changed, DateTime Stamp);

    // What the table's key and value columns are named now: what the dimensions writing the table recorded (the rows
    // that name the table first, then this dimension's own, which a build that never wrote may hold alone), and the
    // columns the table has.
    private const string ColumnsSql = """
        SELECT TOP (1) x.[KeyColumn], x.[ValueColumn]
        FROM (
            SELECT d.[KeyColumn], d.[ValueColumn], 0 AS [Rank]
            FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
            WHERE d.[TableName] = @name AND d.[KeyColumn] IS NOT NULL AND d.[ValueColumn] IS NOT NULL
            UNION ALL
            SELECT d.[KeyColumn], d.[ValueColumn], 1
            FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
            WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d AND d.[KeyColumn] IS NOT NULL AND d.[ValueColumn] IS NOT NULL) AS x
        ORDER BY x.[Rank];

        """;

    // The names recorded on every dimension writing the table (one a partition) and on this one, compared exactly, so
    // a name that changed only in case is recorded too: each by a seek of its own, so no other dimension's row is read.
    private const string RecordColumnsSql = """
        UPDATE d SET d.[KeyColumn] = @key, d.[ValueColumn] = @value
        FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
        WHERE d.[TableName] = @name
          AND (ISNULL(d.[KeyColumn], N'') COLLATE Latin1_General_100_BIN2 <> @key OR ISNULL(d.[ValueColumn], N'') COLLATE Latin1_General_100_BIN2 <> @value);
        UPDATE d SET d.[KeyColumn] = @key, d.[ValueColumn] = @value
        FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
        WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d
          AND (ISNULL(d.[KeyColumn], N'') COLLATE Latin1_General_100_BIN2 <> @key OR ISNULL(d.[ValueColumn], N'') COLLATE Latin1_General_100_BIN2 <> @value);
        """;

    /// <summary>The names a column passes through when two renames would cross (the key's and the value's swapped, or a name changed only in case).</summary>
    private static string Passing(string role) => "osdu_renaming_" + role;

    /// <summary>
    /// Settles what the table's key and value columns are named, before its schema is brought to the declaration
    /// (osdu/docs/reference/flow/dimension-table.md, The dimension's table). A build (<paramref name="rename"/>) names them
    /// as its dimension declares: where the table holds them under other names (the dimension's path or label changed,
    /// its document names them otherwise, or the table was made before dimensions named their columns and holds
    /// <c>key</c> and <c>value</c>), each is renamed where it is, so the rows, their numbers and the indexes stay. A
    /// reader that makes a missing table ready does not know the declaration, so it keeps the names the table has. The
    /// names are recorded on every dimension writing the table, in the transaction that renames, so a reader never
    /// names a column the table does not have.
    /// </summary>
    /// <returns>The table with its columns as they are named now, and whether a column was renamed.</returns>
    /// <exception cref="DeliveryException">A name is taken by another column of the table, a rename failed, or another build held the table too long.</exception>
    private static async Task<(DimensionTableSpec Table, bool Renamed)> SettleColumnsAsync(
        SqlConnection connection, DimensionTableDatabase home, short partitionId, int dimensionId, DimensionTableSpec declared, bool rename, CancellationToken ct)
    {
        var shown = home.Target.Shown(declared.Name);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                // One table is written by a dimension in each partition it is built in, so its columns are settled by one at a time.
                await TableLockAsync(connection, transaction, declared.Name, shown, LockTimeoutMs, "settled its columns", ct).ConfigureAwait(false);

                (string Key, string Value)? recorded = null;
                await using (var read = Command(connection, transaction, ColumnsSql))
                {
                    AddColumnParameters(read, partitionId, dimensionId, declared.Name);
                    await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        recorded = (reader.GetString(0), reader.GetString(1));
                    }
                }

                // The columns the table has, where it is kept: in this transaction when that is the module's database.
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var read = Command(home.Connection, home.Joining(transaction), "SELECT c.[name] FROM sys.columns AS c WHERE c.[object_id] = OBJECT_ID(@table, N'U');"))
                {
                    read.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = home.Target.Qualified(declared.Name) });
                    await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        columns.Add(reader.GetString(0));
                    }
                }

                // A table made before dimensions named their columns, which no row says so of, holds them under the two words.
                var was = recorded
                    ?? (columns.Contains(DimensionColumnNames.KeyRole) && columns.Contains(DimensionColumnNames.ValueRole)
                        ? (DimensionColumnNames.KeyRole, DimensionColumnNames.ValueRole)
                        : ((string Key, string Value)?)null);
                var table = !rename && was is { } kept ? declared with { KeyColumn = kept.Key, ValueColumn = kept.Value } : declared;

                var moves = new List<(string Role, string From, string To)>();
                if (rename && was is { } named)
                {
                    foreach (var (role, from, to) in new[] { (DimensionColumnNames.KeyRole, named.Key, table.KeyColumn), (DimensionColumnNames.ValueRole, named.Value, table.ValueColumn) })
                    {
                        if (!string.Equals(from, to, StringComparison.Ordinal) && columns.Contains(from))
                        {
                            moves.Add((role, from, to));
                        }
                    }
                }

                // A name is free when no column holds it, or when the column holding it is itself being renamed away.
                foreach (var (role, from, to) in moves)
                {
                    if (columns.Contains(to) && !moves.Any(m => string.Equals(m.From, to, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new DeliveryException(
                            $"The dimension's table {shown} has a column {to} already (an attribute the dimension declared before, or a column added by hand), so its {role} column {from} cannot be renamed to it, and nothing was written. Drop that column, or name the {role}'s column otherwise in the flow: columns: {{ {role}: <name> }}.");
                    }
                }

                // A view naming a column renamed would fail, or read the other column where two swap names: the views a
                // build made over the table go in the same transaction, and the run's view step writes them again.
                if (moves.Count > 0)
                {
                    var renamed = string.Join(" and ", moves.Select(m => $"{m.From} to {m.To}"));
                    await SqlServerDimensionViewStore.ClearViewsReadingAsync(
                        connection, transaction, home, declared.Name,
                        $"Dropped when a build renamed {renamed} in {shown}; the next build of its flow writes it again.", ct).ConfigureAwait(false);
                }

                var crossing = moves.Any(m => moves.Any(o => string.Equals(o.From, m.To, StringComparison.OrdinalIgnoreCase)));
                if (crossing)
                {
                    foreach (var (role, from, _) in moves)
                    {
                        await RenameColumnAsync(home, home.Joining(transaction), declared.Name, from, Passing(role), ct).ConfigureAwait(false);
                    }
                }

                foreach (var (role, from, to) in moves)
                {
                    await RenameColumnAsync(home, home.Joining(transaction), declared.Name, crossing ? Passing(role) : from, to, ct).ConfigureAwait(false);
                }

                await using (var record = Command(connection, transaction, RecordColumnsSql))
                {
                    AddColumnParameters(record, partitionId, dimensionId, declared.Name);
                    record.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 128) { Value = table.KeyColumn });
                    record.Parameters.Add(new SqlParameter("@value", SqlDbType.NVarChar, 128) { Value = table.ValueColumn });
                    await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return (table, moves.Count > 0);
            }
            catch (SqlException ex)
            {
                throw new DeliveryException(
                    $"The columns of the dimension's table {shown} could not be settled, so the table was not written: {ex.Message.Trim()} The database user the table is written as needs ALTER on the {home.Target.Schema} schema to rename a column; a table changed by hand into something a build cannot write is put right, or dropped so the next run makes it again.",
                    ex);
            }
        }
    }

    private static void AddColumnParameters(SqlCommand command, short partitionId, int dimensionId, string table)
    {
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = table });
        command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
        command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
    }

    /// <summary>Renames one column of a table where it is: its rows and the indexes over it stay, and only its name moves.</summary>
    private static async Task RenameColumnAsync(DimensionTableDatabase home, SqlTransaction? transaction, string table, string from, string to, CancellationToken ct)
    {
        await using var command = Command(home.Connection, transaction, "EXEC sys.sp_rename @objname = @column, @newname = @to, @objtype = N'COLUMN';");
        command.Parameters.Add(new SqlParameter("@column", SqlDbType.NVarChar, 776) { Value = home.Target.Qualified(table) + "." + Bracketed(from) });
        command.Parameters.Add(new SqlParameter("@to", SqlDbType.NVarChar, 128) { Value = to });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes sure the dimension's table is there with its key's and its value's columns under their names and a column
    /// for every attribute it declares, before the write's transaction begins: the two columns are settled first
    /// (<see cref="SettleColumnsAsync"/>), then SQLFlow's schema evolution reads the table as it is, creates it when it is
    /// missing and adds the column of an attribute it does not have, and never drops or narrows one
    /// (osdu/docs/reference/flow/dimension-table.md, The dimension's table). Then the order it keeps its rows in and the
    /// indexes it is read through (<see cref="MakeIndexesAsync"/>), and the statements that write its rows. A table made
    /// before tables kept their rows' times is given the two columns first (<see cref="StampColumnsAsync"/>), every row it
    /// holds taking <paramref name="stamp"/>, the time of the write that follows.
    /// <paramref name="rename"/> is true for a build, which names the two columns as its dimension declares, and false for
    /// a reader, which keeps the names the table has.
    /// </summary>
    /// <exception cref="DeliveryException">The table cannot be made or widened: the database user may not, or the table was changed by hand into something a build cannot write.</exception>
    private static async Task<PreparedTable> PrepareTableAsync(
        SqlConnection connection, DimensionTableDatabase home, short partitionId, int dimensionId, DimensionTableSpec declared, bool rename, DateTime stamp,
        CancellationToken ct)
    {
        await EnsureSchemaAsync(home, ct).ConfigureAwait(false);
        var (table, renamed) = await SettleColumnsAsync(connection, home, partitionId, dimensionId, declared, rename, ct).ConfigureAwait(false);
        try
        {
            var stamped = await StampColumnsAsync(connection, home, table, stamp, ct).ConfigureAwait(false);
            var (plan, changed) = await RunTimeTables.EvolveAsync(
                home.Connection, table.Name, DimensionTables.Desired(table), DimensionTables.KeyColumns, ct, home.Target.Schema).ConfigureAwait(false);
            await MakeIndexesAsync(connection, home, table, ct).ConfigureAwait(false);

            // A column the table has and the declaration does not is one of an attribute the dimension declared before
            // (it is emptied), or one somebody added to the table, which is theirs and is left alone.
            var extra = plan.Drift.Where(d => d.Kind == DriftKind.ExtraTargetColumn).Select(d => d.Column).ToList();
            var retired = new List<string>();
            if (extra.Count > 0)
            {
                await using var known = new SqlCommand(
                    "SELECT n.[Name] FROM [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK) WHERE n.[PartitionId] = @p AND n.[DimensionId] = @d;", connection)
                {
                    CommandTimeout = CommandTimeoutSeconds,
                };
                known.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
                known.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var reader = await known.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        names.Add(reader.GetString(0));
                    }
                }

                retired.AddRange(extra.Where(names.Contains));
            }

            return new PreparedTable(table, DimensionTables.ApplySql(table, retired, home.Target), changed || renamed || stamped, stamp);
        }
        catch (Exception ex) when (ex is SqlException or SqlFlowException)
        {
            throw new DeliveryException(
                $"The dimension's table {home.Target.Shown(table.Name)} could not be made ready, so it was not written: {ex.Message.Trim()} The database user the table is written as needs CREATE TABLE in its database and ALTER on the {home.Target.Schema} schema; a table changed by hand into something a build cannot write is put right, or dropped so the next run makes it again.",
                ex);
        }
    }

    /// <summary>
    /// Makes the target's schema when a database other than the module's does not hold it yet: a flow naming a new schema
    /// there needs no setup first. The module's own schema is the model's, made by its migrations.
    /// </summary>
    /// <exception cref="DeliveryException">The schema is not there and cannot be made.</exception>
    private static async Task EnsureSchemaAsync(DimensionTableDatabase home, CancellationToken ct)
    {
        if (home.InCatalog)
        {
            return;
        }

        try
        {
            await using var command = new SqlCommand(
                "IF SCHEMA_ID(@schema) IS NULL BEGIN DECLARE @make nvarchar(300) = N'CREATE SCHEMA ' + QUOTENAME(@schema) + N';'; EXEC sys.sp_executesql @make; END;",
                home.Connection) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = home.Target.Schema });
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == ObjectExists)
        {
            // Another build made the schema between the look and the make: it is there.
        }
        catch (SqlException ex)
        {
            throw new DeliveryException(
                $"The schema {home.Target.Schema} of the dimension tables' target is not in its database and could not be made: {ex.Message.Trim()} Make it there, or grant the database user the tables are written as CREATE SCHEMA.",
                ex);
        }
    }

    /// <summary>
    /// Gives a table made before tables kept their rows' times the two columns that hold them, every row it holds taking
    /// <paramref name="stamp"/> (<see cref="DimensionTables.StampSql"/>), under the table's lock, so builds of the dimension in
    /// two partitions add them once. A table not there yet is left to schema evolution, which makes it with the columns.
    /// Says whether a column was added.
    /// </summary>
    private static async Task<bool> StampColumnsAsync(SqlConnection connection, DimensionTableDatabase home, DimensionTableSpec table, DateTime stamp, CancellationToken ct)
    {
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // A column added not null on an edition that writes every row for it takes as long as the rows take, as a
            // layout does: the wait for the lock is bounded by the other build's run.
            await TableLockAsync(connection, transaction, table.Name, home.Target.Shown(table.Name), -1, "gave it its rows' times", ct).ConfigureAwait(false);
            int added;
            await using (var stamps = Command(home.Connection, home.Joining(transaction), DimensionTables.StampSql(table, home.Target, stamp)))
            {
                added = Convert.ToInt32(await stamps.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return added > 0;
        }
    }

    /// <summary>
    /// Lays the dimension's table out by partition and makes the indexes it is read through
    /// (<see cref="DimensionTables.IndexSql"/>), under the table's lock, so builds of the dimension in two partitions do it
    /// one at a time and the second finds it done. Laying out a table made before its rows were kept by partition rebuilds
    /// it once, which takes as long as its rows take: the statement is bounded by its run, as a write is, and a build of
    /// another partition waits for it rather than failing.
    /// </summary>
    private static async Task MakeIndexesAsync(SqlConnection connection, DimensionTableDatabase home, DimensionTableSpec table, CancellationToken ct)
    {
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // The lock is the ledger's, held for the transaction; the indexes are made where the table is kept, in that
            // transaction when it is the module's database.
            await TableLockAsync(connection, transaction, table.Name, home.Target.Shown(table.Name), -1, "laid it out", ct).ConfigureAwait(false);
            await using (var indexes = Command(home.Connection, home.Joining(transaction), DimensionTables.IndexSql(table, home.Target)))
            {
                await indexes.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes the lock a dimension's table is changed under for the rest of <paramref name="transaction"/>: one table is
    /// written by the dimension in each partition it is built in, so its columns are settled, and it is laid out, by one
    /// build at a time. <paramref name="timeoutMs"/> bounds the wait (<c>-1</c> waits as long as the other build takes,
    /// which its run bounds); <paramref name="doing"/> says what the other build was doing, for the message.
    /// </summary>
    /// <exception cref="DeliveryException">The lock was not taken.</exception>
    private static async Task TableLockAsync(SqlConnection connection, SqlTransaction transaction, string name, string shown, int timeoutMs, string doing, CancellationToken ct)
    {
        await using var held = Command(connection, transaction, LockSql);
        held.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = "osdu-dimension-table:" + name });
        held.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = timeoutMs });
        var granted = Convert.ToInt32(await held.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (granted < 0)
        {
            var waited = timeoutMs < 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" for more than {timeoutMs / 1000} seconds");
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"Another build held the dimension's table {shown}{waited} while it {doing} (sp_getapplock answered {granted}), so the table was not written. Build it again when the other has finished."));
        }
    }

    /// <summary>
    /// Makes a table a view reads ready for the view (osdu/docs/reference/flow/dimension-views.md): one no build has made
    /// yet is made, empty, as a build makes it, so the view's join finds nothing until its dimension is built; one a build
    /// made is given the index its joins find a key's rows by, and loses the key's hash, when it was made before keys were
    /// found by their text. Its other columns are its builds' to settle: a column the document names that the table does
    /// not have yet fails the view's write, saying so.
    /// </summary>
    /// <exception cref="DeliveryException">The table cannot be made or given the column.</exception>
    internal static async Task EnsureViewTableAsync(SqlConnection connection, DimensionTableDatabase home, DimensionTableSpec table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(table);
        var qualified = home.Target.Qualified(table.Name);
        try
        {
            bool exists;
            await using (var probe = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@table, N'U') IS NULL THEN 0 ELSE 1 END;", home.Connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                probe.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = qualified });
                exists = Convert.ToInt32(await probe.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
            }

            if (!exists)
            {
                await EnsureSchemaAsync(home, ct).ConfigureAwait(false);
                await RunTimeTables.EvolveAsync(home.Connection, table.Name, DimensionTables.Desired(table), DimensionTables.KeyColumns, ct, home.Target.Schema).ConfigureAwait(false);
                await MakeIndexesAsync(connection, home, table, ct).ConfigureAwait(false);
                return;
            }

            await using var index = new SqlCommand(DimensionTables.KeyIndexSql(table.Name, table.KeyColumn, home.Target), home.Connection) { CommandTimeout = CommandTimeoutSeconds };
            await index.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is ObjectExists or ColumnExists or IndexExists)
        {
            // A build made the table, or gave it the column or the index, between the look and the change: it is there.
        }
        catch (Exception ex) when (ex is SqlException or SqlFlowException)
        {
            throw new DeliveryException(
                $"The table {home.Target.Shown(table.Name)} a view reads could not be made ready: {ex.Message.Trim()} The database user the table is written as needs CREATE TABLE in its database and ALTER on the {home.Target.Schema} schema.",
                ex);
        }
    }

    /// <summary>What SQL Server answers a statement making an object that is already there.</summary>
    private const int ObjectExists = 2714;

    /// <summary>What SQL Server answers a statement adding a column a table has already.</summary>
    private const int ColumnExists = 2705;

    /// <summary>What SQL Server answers a statement making an index a table has already.</summary>
    private const int IndexExists = 1913;
}
