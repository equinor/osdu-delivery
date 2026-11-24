using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// A build's write (osdu/docs/reference/concepts/dimension-tables.md): the merge of what the build staged, in one
/// transaction under the dimension's write lock and tried again from the same staging after a deadlock, then the table
/// step that brings the dimension's table to what the merge laid out, after leaving nothing where a moved target kept
/// it before (osdu/docs/reference/flow/dimension-target.md, Moving a target).
/// </summary>
internal static partial class SqlServerDimensionStore
{
    /// <summary>
    /// What a write changed, and what the dimension holds after it: the attributes it added, rewrote or dropped among them,
    /// and its originals under no member and those under one that no query can carry.
    /// </summary>
    public sealed record Written(DimensionChangeCounts Changes, long Members, long Originals, long AttributesChanged, long LeftOut, long Unfilterable)
    {
        /// <summary>The fields of keys' elements the write added, rewrote or dropped.</summary>
        public long ElementsChanged { get; init; }
    }

    /// <summary>The server process id of the session <paramref name="connection"/> holds, which the write's progress reports ask the database about.</summary>
    private static async Task<int> SessionOfAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT CAST(@@SPID AS int);", connection) { CommandTimeout = CommandTimeoutSeconds };
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Merges what <paramref name="stage"/>'s build staged into its dimension, as <paramref name="write"/> says, and closes the
    /// build as completed with <paramref name="close"/>'s counts, in one transaction of the module's database; a deadlock
    /// rolls it back and it is written again from the same staging. Then the table step brings the dimension's table, where
    /// the write's target keeps it (opened by <paramref name="open"/>), to what the merge laid out
    /// (osdu/docs/reference/flow/dimension-target.md). The merge is never made twice: a table step that fails leaves the
    /// dimension written and the table behind, and says so, and the next build lays the table out whole.
    /// </summary>
    /// <exception cref="DimensionTableWriteException">The dimension was written and its table was not.</exception>
    public static async Task<Written> WriteAsync(
        OsduDbContext db, string partition, DimensionStage stage, DimensionStagedWrite write, Func<Written, DeliveryDimensionRun, Task> close,
        Func<SqlConnection, DimensionTableTarget, CancellationToken, Task<DimensionTableDatabase>> open, DimensionWriteClock clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(clock);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            clock.Session = await SessionOfAsync(connection, ct).ConfigureAwait(false);
            DimensionTableDatabase? home = null;
            try
            {
                var elementRows = false;
                if (write.Table is { } declared)
                {
                    await LeaveFormerTargetAsync(connection, open, stage.PartitionId, stage.DimensionId, partition, write.TableTarget, ct).ConfigureAwait(false);
                    home = await open(connection, write.TableTarget, ct).ConfigureAwait(false);
                    elementRows = await ElementRowsAsync(home, declared, partition, ct).ConfigureAwait(false);
                }

                var merged = await clock.TimeAsync(
                    DimensionWriteStep.Merge,
                    string.Create(CultureInfo.InvariantCulture,
                        $"{stage.Values} key(s), {stage.Members} value(s), {stage.Attributes} collected value(s) and {stage.Elements} element object(s) staged{(write.Partial ? ", an incremental load's" : string.Empty)}"),
                    async () =>
                    {
                        for (var attempt = 1; ; attempt++)
                        {
                            try
                            {
                                return await MergeOnceAsync(db, connection, partition, stage, write, close, elementRows, clock, ct).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (StoppedBy(ex, ct))
                            {
                                throw new OperationCanceledException(
                                    string.Create(CultureInfo.InvariantCulture, $"The write of dimension {stage.DimensionId} was cancelled with its run, and wrote nothing."), ex, ct);
                            }
                            catch (Exception ex) when (attempt < DeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
                            {
                                db.ChangeTracker.Clear();
                                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 200) * attempt), ct).ConfigureAwait(false);
                            }
                        }
                    },
                    m => string.Create(CultureInfo.InvariantCulture,
                        $"{m.Written.Changes.OriginalsAdded} key(s) arrived, {m.Written.Changes.OriginalsRemoved} left, {m.Written.Changes.OriginalsMoved} moved and {m.Written.Changes.OriginalsRestored} came back; {m.Written.AttributesChanged} attribute value(s) and {m.Written.ElementsChanged} element field(s) added, rewritten or dropped"),
                    ct).ConfigureAwait(false);

                if (write.Table is { } table && home is not null)
                {
                    await clock.TimeAsync(
                        DimensionWriteStep.Table,
                        home.Target.Shown(table.Name) + (merged.Scoped ? ", the rows of the keys the write changed" : ", every row"),
                        () => WriteBuiltTableAsync(connection, home, stage, partition, table, merged, write.CompletedUtc, clock, ct),
                        changed => string.Create(CultureInfo.InvariantCulture, $"{changed} row(s) added, rewritten or deleted"),
                        ct).ConfigureAwait(false);
                }

                return merged.Written;
            }
            finally
            {
                if (home is not null)
                {
                    await home.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    // Where the dimension's table is recorded as kept, and whether another partition of the dimension keeps it there too.
    private const string FormerTargetSql = """
        SELECT d.[TableName], d.[TableConnection], d.[TableDatabase], d.[TableSchema],
            CASE WHEN EXISTS (
                SELECT 1 FROM [osdu].[Dimension] AS o WITH (FORCESEEK)
                WHERE o.[TableName] = d.[TableName] AND NOT (o.[PartitionId] = d.[PartitionId] AND o.[DimensionId] = d.[DimensionId])
                  AND ISNULL(o.[TableConnection], N'') = ISNULL(d.[TableConnection], N'')
                  AND ISNULL(o.[TableDatabase], N'') = ISNULL(d.[TableDatabase], N'')
                  AND ISNULL(o.[TableSchema], N'osdu') = ISNULL(d.[TableSchema], N'osdu')) THEN 1 ELSE 0 END
        FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
        WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d AND d.[TableName] IS NOT NULL;
        """;

    /// <summary>
    /// A dimension whose flow now keeps its table elsewhere (<paramref name="target"/>) leaves nothing where it kept it
    /// before (osdu/docs/reference/flow/dimension-target.md, Moving a target): the table itself when no other partition
    /// keeps the dimension there, else the partition's rows of it, a batch at a time, under the dimension's write lock and
    /// the table's. Done before the merge records the new place, so a place that cannot be reached stops the build with
    /// nothing changed and the next build does it again; the table where it moves is laid out whole.
    /// </summary>
    /// <exception cref="DeliveryException">Where the table was kept could not be opened, or the table could not be taken from it.</exception>
    private static async Task LeaveFormerTargetAsync(
        SqlConnection connection, Func<SqlConnection, DimensionTableTarget, CancellationToken, Task<DimensionTableDatabase>> open, short partitionId, int dimensionId, string partition, DimensionTableTarget target, CancellationToken ct)
    {
        string table;
        DimensionTableTarget former;
        bool shared;
        await using (var read = new SqlCommand(FormerTargetSql, connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            read.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            read.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            table = reader.GetString(0);
            former = DimensionTableTarget.Of(
                await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, ct).ConfigureAwait(false) ? null : reader.GetString(3));
            shared = reader.GetInt32(4) == 1;
        }

        if (former == target)
        {
            return;
        }

        var shown = former.Shown(table);
        try
        {
            await using var there = former.InCatalog ? DimensionTableDatabase.OfCatalog(connection, former) : await open(connection, former, ct).ConfigureAwait(false);
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await LockAsync(connection, transaction, partitionId, dimensionId, ct).ConfigureAwait(false);
                await TableLockAsync(connection, transaction, table, shown, LockTimeoutMs, "while a build moved it to where its flow keeps its tables now", ct).ConfigureAwait(false);
                var qualified = former.Qualified(table);
                await using var leave = new SqlCommand(
                    shared
                        ? $"IF OBJECT_ID(@table, N'U') IS NOT NULL\n    WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM {qualified} WHERE [partition] = @partition; IF @@ROWCOUNT < @batch BREAK; END;"
                        : $"DROP TABLE IF EXISTS {qualified};",
                    there.Connection, there.Joining(transaction)) { CommandTimeout = WriteTimeoutSeconds };
                leave.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = qualified });
                leave.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = RemoveBatch });
                leave.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
                await leave.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (SqlException ex)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture,
                    $"Dimension {dimensionId}'s flow now keeps its table at {target.Shown(table)}, and the table could not be taken from {shown}, where it was kept before, so the build wrote nothing: {SqlFlow.Core.Secrets.SecretHygiene.RedactedMessage(ex.Message)} The next build tries again."),
                ex);
        }
    }

    /// <summary>What a merge wrote, and how the table step is to lay the table out: written anew, or for the merge's keys alone.</summary>
    private readonly record struct Merged(Written Written, bool Rewrite, bool Scoped);

    /// <summary>
    /// The table step of a build: the table made ready where it is kept, then brought to the rows the merge laid out, a
    /// deadlock tried again from the same layout. The rows it adds or rewrites take <paramref name="stamp"/>, the time of the
    /// build's write, which the dimension records as its last build. A failure says the dimension was written and the table
    /// was not.
    /// </summary>
    private static async Task<long> WriteBuiltTableAsync(
        SqlConnection connection, DimensionTableDatabase home, DimensionStage stage, string partition, DimensionTableSpec table, Merged merged, DateTime stamp,
        DimensionWriteClock clock, CancellationToken ct)
    {
        try
        {
            var prepared = await PrepareTableAsync(connection, home, stage.PartitionId, stage.DimensionId, table, rename: true, stamp, ct).ConfigureAwait(false);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await WriteTableAsync(connection, home, stage.PartitionId, stage.DimensionId, partition, prepared, merged.Rewrite, merged.Scoped, stage.DimensionRunId, clock, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < DeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 200) * attempt), ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (StoppedBy(ex, ct))
        {
            throw new OperationCanceledException(
                $"The write of dimension {stage.DimensionId} was cancelled after the dimension was written and before its table {home.Target.Shown(table.Name)} was; the next build lays the table out whole.",
                ex, ct);
        }
        catch (Exception ex) when (ex is DeliveryException or SqlException or SqlFlowException or InvalidOperationException)
        {
            throw new DimensionTableWriteException(
                $"Dimension {stage.DimensionId} was written, but its table {home.Target.Shown(table.Name)} was not: {SqlFlow.Core.Secrets.SecretHygiene.RedactedMessage(ex.Message)} The next build lays the table out whole.",
                ex);
        }
    }

    private static async Task<Merged> MergeOnceAsync(
        OsduDbContext db, SqlConnection connection, string partition, DimensionStage stage, DimensionStagedWrite write,
        Func<Written, DeliveryDimensionRun, Task> close, bool elementRows, DimensionWriteClock clock, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        var transaction = (SqlTransaction)tx.GetDbTransaction();
        await LockAsync(connection, transaction, stage.PartitionId, stage.DimensionId, clock, DimensionWriteStep.Merge, ct).ConfigureAwait(false);
        await CheckStagedAsync(connection, transaction, stage, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, StageSql, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, LayoutTablesSql, ct).ConfigureAwait(false);
        await CopyAsync(connection, transaction, "#DimName", NameColumns, NameTypes, (write.Table?.Columns ?? []).Select(NameRow), null, ct).ConfigureAwait(false);

        // The staging, copied into the write's own tables where the database holds it: no row of the build passes
        // through the process writing it.
        var first = write.Table?.Columns.FirstOrDefault(c => c.Element)?.Name;
        await using (var copy = SqlServerDimensionStage.Command(connection, transaction, SqlServerDimensionStage.CopySql(stage), stage))
        {
            copy.Parameters.Add(new SqlParameter("@elements", SqlDbType.Bit) { Value = write.Table is { HasElements: true } && first is not null });
            await copy.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        Merged merged;
        await using (var command = Command(connection, transaction, MergeSql))
        {
            command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = stage.PartitionId });
            command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = stage.DimensionId });
            command.Parameters.Add(new SqlParameter("@run", SqlDbType.BigInt) { Value = stage.DimensionRunId });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = write.CompletedUtc });
            command.Parameters.Add(new SqlParameter("@fieldIndex", SqlDbType.NVarChar, 16) { Value = (object?)write.Field?.Index ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@nestedPath", SqlDbType.NVarChar, DeliveryDimension.MaxPathLength) { Value = (object?)write.Field?.NestedPath ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@aggregateBy", SqlDbType.NVarChar, DeliveryDimension.MaxAggregateByLength) { Value = (object?)write.Field?.AggregateBy ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@repeats", SqlDbType.Bit) { Value = write.Field?.Repeats ?? false });
            command.Parameters.Add(new SqlParameter("@collected", SqlDbType.NVarChar, -1) { Value = (object?)write.CollectedJson ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@partial", SqlDbType.Bit) { Value = write.Partial });
            command.Parameters.Add(new SqlParameter("@records", SqlDbType.Bit) { Value = write.KeepsRecords });
            command.Parameters.Add(new SqlParameter("@firstElement", SqlDbType.NVarChar, DeliveryDimensionAttributeValue.MaxNameLength) { Value = (object?)first ?? DBNull.Value });
            AddTable(command, write.Table, partition, elementRows, write.TableTarget);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException($"Writing dimension {stage.DimensionId} returned no counts.");
            }

            merged = new Merged(
                new Written(
                    new DimensionChangeCounts(
                        MembersAdded: reader.GetInt64(4), MembersRemoved: reader.GetInt64(5), MembersRestored: reader.GetInt64(6),
                        OriginalsAdded: reader.GetInt64(0), OriginalsRemoved: reader.GetInt64(1), OriginalsMoved: reader.GetInt64(2), OriginalsRestored: reader.GetInt64(3)),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11))
                {
                    ElementsChanged = reader.GetInt64(14),
                },
                reader.GetBoolean(12),
                reader.GetBoolean(13));
        }

        // By its whole key, as every statement of the write reads: a seek of its own row, whatever the table holds.
        var run = await db.DeliveryDimensionRuns
            .FirstOrDefaultAsync(r => r.PartitionId == stage.PartitionId && r.DimensionId == stage.DimensionId && r.DimensionRunId == stage.DimensionRunId, ct)
            .ConfigureAwait(false)
            ?? throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Dimension build {stage.DimensionRunId} is not in the ledger, so it cannot be closed."));
        await close(merged.Written, run).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return merged;
    }

    /// <summary>
    /// Refuses a write whose staging is gone or no longer holds every key and value its build staged: a later build of the
    /// dimension took the build for one its process left, which it does only once a build has run for
    /// <see cref="DimensionStage.AbandonedAfter"/>, and dropped its staging, or the dimension was removed. Read under the
    /// dimension's write lock, which such a drop takes too, so nothing of the staging is dropped while the write copies it.
    /// </summary>
    private static async Task CheckStagedAsync(SqlConnection connection, SqlTransaction transaction, DimensionStage stage, CancellationToken ct)
    {
        await using var command = SqlServerDimensionStage.Command(connection, transaction, SqlServerDimensionStage.CountSql(stage), stage);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        var (values, members) = (reader.GetInt64(0), reader.GetInt64(1));
        if (values < 0 || members < 0)
        {
            throw new DeliveryException(SqlServerDimensionStage.GoneMessage(stage));
        }

        if (values != stage.Values || members != stage.Members)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"The staging of dimension build {stage.DimensionRunId} holds {values} of the {stage.Values} key(s) and {members} of the {stage.Members} value(s) it staged, so it is not what the build read. Nothing was written; build the dimension again."));
        }
    }
}
