using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The statements of a ledger move (<see cref="LedgerMoves"/>): what it checks before it begins, the records of the new
/// identity that give way to the old ledger's, the link of every moved record to the move, the rows of each table moved a
/// slice at a time, and the end that drops the old ledger from the directory. Each statement touches at most a slice of
/// rows and commits on its own, so no statement takes a lock on a whole table and a move of any size never holds the
/// ledger tables for longer than a slice; a move that stops is taken up where it stopped, since every statement only
/// reaches rows the move has not reached yet.
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    // Checked before a move begins: a run working on either ledger (a lease alive, or a submission a run holds, which only a
    // session that works on it does: SubmissionHold), records of the new identity that claimed an OSDU id under
    // a key the old ledger holds a record of too (one key would be two records of one ledger, each with an id of its own),
    // and the two keys a move cannot settle by itself, an artifact slot or a reversal both ledgers hold. Records of the new
    // identity that claimed ids under keys of their own are records the renamed pipeline delivered, and stay beside the moved.
    private const string MoveCheckSql = """
        SET NOCOUNT ON;
        SELECT
            CAST(CASE
                WHEN EXISTS (
                    SELECT 1 FROM [osdu].[Lease]
                    WHERE [PartitionId] = @partition AND [FlowId] IN (@from, @to) AND [ExpiresUtc] > @now) THEN 1
                WHEN EXISTS (
                    SELECT 1 FROM [osdu].[Submission] AS s
                    WHERE s.[PartitionId] = @partition AND s.[FlowId] IN (@from, @to) AND s.[Status] IN (N'received', N'planned', N'running')
                      AND APPLOCK_TEST(N'public', N'SqlFlow.Delivery.Submission:' + LOWER(CONVERT(nvarchar(36), s.[SubmissionId])), N'Exclusive', N'Session') = 0) THEN 1
                ELSE 0 END AS bit),
            (SELECT COUNT_BIG(*) FROM [osdu].[Record] AS r
             WHERE r.[PartitionId] = @partition AND r.[FlowId] = @to AND r.[ClaimedTargetId] IS NOT NULL
               AND EXISTS (
                   SELECT 1 FROM [osdu].[Record] AS o
                   WHERE o.[PartitionId] = @partition AND o.[FlowId] = @from AND o.[DeliveryKey] = r.[DeliveryKey])),
            (SELECT COUNT_BIG(*) FROM [osdu].[Artifact] AS n
             WHERE n.[PartitionId] = @partition AND n.[FlowId] = @to
               AND EXISTS (
                   SELECT 1 FROM [osdu].[Artifact] AS o
                   WHERE o.[PartitionId] = @partition AND o.[FlowId] = @from AND o.[DeliveryKey] = n.[DeliveryKey]
                     AND o.[UnitId] = n.[UnitId] AND o.[Slot] = n.[Slot]))
            + (SELECT COUNT_BIG(*) FROM [osdu].[Reversal] AS n
               WHERE n.[PartitionId] = @partition AND n.[FlowId] = @to
                 AND EXISTS (
                     SELECT 1 FROM [osdu].[Reversal] AS o
                     WHERE o.[PartitionId] = @partition AND o.[FlowId] = @from AND o.[SourceKind] = n.[SourceKind] AND o.[SourceId] = n.[SourceId]));
        """;

    // The new identity's records the old ledger holds too, which claimed no OSDU id and so wrote nothing to OSDU (a renamed
    // pipeline held them because the old ledger claimed their ids): their search entries and rows go, a slice at a time, and
    // the old records take their place. Their attempts stay, and read as part of the moved record's history.
    private const string SupersedeIdentitiesSql = """
        DELETE TOP (@slice) n FROM [osdu].[RecordIdentity] AS n
        WHERE n.[PartitionId] = @partition AND n.[FlowId] = @to
          AND EXISTS (
              SELECT 1 FROM [osdu].[Record] AS o
              WHERE o.[PartitionId] = @partition AND o.[FlowId] = @from AND o.[DeliveryKey] = n.[DeliveryKey]);
        """;

    private const string SupersedeRecordsSql = """
        SET NOCOUNT ON;
        DELETE TOP (@slice) n FROM [osdu].[Record] AS n
        WHERE n.[PartitionId] = @partition AND n.[FlowId] = @to AND n.[ClaimedTargetId] IS NULL
          AND EXISTS (
              SELECT 1 FROM [osdu].[Record] AS o
              WHERE o.[PartitionId] = @partition AND o.[FlowId] = @from AND o.[DeliveryKey] = n.[DeliveryKey]);
        DECLARE @gone int = @@ROWCOUNT;
        IF @gone > 0
            UPDATE [osdu].[Activity]
            SET [ParametersJson] = JSON_MODIFY([ParametersJson], '$.superseded', ISNULL(CAST(JSON_VALUE([ParametersJson], '$.superseded') AS bigint), 0) + @gone)
            WHERE [PartitionId] = @partition AND [ActivityId] = @activity;
        SELECT @gone;
        """;

    // A deletion a removal flow found, or a watermark of a scope, both ledgers hold: the old ledger's is kept. Its watermark is
    // the older, so the next run reads every row changed since the old name last read, and its deletion is the one its records
    // were taken out under.
    private const string SupersedeRemovedKeysSql = """
        DELETE TOP (@slice) n FROM [osdu].[RemovedKey] AS n
        WHERE n.[PartitionId] = @partition AND n.[FlowId] = @to
          AND EXISTS (
              SELECT 1 FROM [osdu].[RemovedKey] AS o
              WHERE o.[PartitionId] = @partition AND o.[FlowId] = @from AND o.[DeliveryKey] = n.[DeliveryKey]);
        """;

    private const string SupersedeWatermarksSql = """
        DELETE n FROM [osdu].[SourceWatermark] AS n
        WHERE n.[PartitionId] = @partition AND n.[FlowId] = @to
          AND EXISTS (
              SELECT 1 FROM [osdu].[SourceWatermark] AS o
              WHERE o.[PartitionId] = @partition AND o.[FlowId] = @from AND o.[Scope] = n.[Scope]);
        """;

    // Every record of the old ledger is linked to the move's activity before it moves (the link moves with it), so each
    // record's history says it came with its ledger, and the move counts what it moved from the links. Walked in key order
    // from the last key linked, so a slice never reads the records linked before it.
    private const string LinkMovedRecordsSql = """
        SET NOCOUNT ON;
        DECLARE @linked TABLE ([DeliveryKey] uniqueidentifier NOT NULL);
        INSERT INTO [osdu].[ActivityRecord] ([PartitionId], [FlowId], [DeliveryKey], [ActivityId])
        OUTPUT inserted.[DeliveryKey] INTO @linked
        SELECT TOP (@slice) @partition, @from, r.[DeliveryKey], @activity
        FROM [osdu].[Record] AS r
        WHERE r.[PartitionId] = @partition AND r.[FlowId] = @from AND (@after IS NULL OR r.[DeliveryKey] > @after)
          AND NOT EXISTS (
              SELECT 1 FROM [osdu].[ActivityRecord] AS a
              WHERE a.[PartitionId] = @partition AND a.[FlowId] = @from AND a.[DeliveryKey] = r.[DeliveryKey] AND a.[ActivityId] = @activity)
        ORDER BY r.[DeliveryKey];
        SELECT COUNT(*), MAX([DeliveryKey]) FROM @linked;
        """;

    // The end: the old ledger leaves the directory once none of its records is left, and the move's activity is closed with
    // what it moved, counted from the links its records carry.
    private const string MoveCountsSql = """
        SET NOCOUNT ON;
        SELECT
            (SELECT COUNT_BIG(*) FROM [osdu].[ActivityRecord] WHERE [PartitionId] = @partition AND [FlowId] = @to AND [ActivityId] = @activity),
            (SELECT COUNT_BIG(*) FROM [osdu].[Record] WHERE [PartitionId] = @partition AND [FlowId] = @from);
        """;

    private const string FinishMoveSql = """
        DELETE FROM [osdu].[Ledger] WHERE [PartitionId] = @partition AND [FlowId] = @from;
        UPDATE [osdu].[Activity]
        SET [Outcome] = @outcome, [CompletedUtc] = @now, [Summary] = @summary
        WHERE [PartitionId] = @partition AND [ActivityId] = @activity AND [Outcome] = N'running';
        """;

    private const string FailMoveSql = """
        UPDATE [osdu].[Activity]
        SET [Outcome] = N'failed', [CompletedUtc] = @now, [Summary] = @summary
        WHERE [PartitionId] = @partition AND [ActivityId] = @activity AND [Outcome] = N'running';
        """;

    private const string StartMoveSql = """
        UPDATE [osdu].[Activity]
        SET [ParametersJson] = JSON_MODIFY([ParametersJson], '$.started', CAST(1 AS bit))
        WHERE [PartitionId] = @partition AND [ActivityId] = @activity;
        """;

    // A move works under an exclusive lock on the identity it moves into, held by its session: a second mover, and a run of
    // the renamed pipeline about to read its ledger, wait for it or leave it.
    private const string LockMoveSql = """
        SET NOCOUNT ON;
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = @timeout;
        SELECT CAST(@granted AS int);
        """;

    /// <summary>What a move checks before it begins.</summary>
    /// <param name="Working">A run works on either ledger: a worker holds a lease on its records, or a run holds a submission of it.</param>
    /// <param name="Claimed">Records of the new identity that claimed an OSDU id under a key the old ledger holds too.</param>
    /// <param name="Colliding">Artifact slots and reversals both ledgers hold.</param>
    public readonly record struct MoveCheck(bool Working, long Claimed, long Colliding);

    /// <summary>Takes the move lock on <paramref name="resource"/> on <paramref name="db"/>'s connection, which it leaves open; below 0 not granted.</summary>
    public static Task<int> LockMoveAsync(OsduDbContext db, string resource, int timeoutMs, CancellationToken ct)
        => LockAsync(db, LockMoveSql, resource, timeoutMs, ct);

    /// <summary>The statement that lets go of the move lock on <paramref name="resource"/>, run on the connection that took it.</summary>
    public static SqlCommand ReleaseMoveLock(SqlConnection connection, string resource) => ReleaseSubmission(connection, resource);

    public static async Task<MoveCheck> CheckMoveAsync(OsduDbContext db, short partition, long activity, Guid from, Guid to, DateTime now, CancellationToken ct)
    {
        await using var command = await MoveCommandAsync(db, MoveCheckSql, partition, activity, from, to, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return new MoveCheck(reader.GetBoolean(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    public static async Task StartMoveAsync(OsduDbContext db, short partition, long activity, Guid from, Guid to, CancellationToken ct)
    {
        await using var command = await MoveCommandAsync(db, StartMoveSql, partition, activity, from, to, ct).ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes a slice of the search entries of the new identity's records that give way; returns how many it removed.</summary>
    public static Task<int> SupersedeIdentitiesAsync(OsduDbContext db, short partition, long activity, Guid from, Guid to, int slice, CancellationToken ct)
        => SliceAsync(db, SupersedeIdentitiesSql, partition, activity, from, to, slice, ct);

    /// <summary>Removes a slice of the new identity's records that give way, counting them on the move; returns how many.</summary>
    public static async Task<int> SupersedeRecordsAsync(OsduDbContext db, short partition, long activity, Guid from, Guid to, int slice, CancellationToken ct)
    {
        await using var command = await MoveCommandAsync(db, SupersedeRecordsSql, partition, activity, from, to, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@slice", SqlDbType.Int) { Value = slice });
        await using var transaction = (SqlTransaction)await command.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        command.Transaction = transaction;
        var gone = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return gone;
    }

    public static Task<int> SupersedeRemovedKeysAsync(OsduDbContext db, short partition, long activity, Guid from, Guid to, int slice, CancellationToken ct)
        => SliceAsync(db, SupersedeRemovedKeysSql, partition, activity, from, to, slice, ct);

    public static async Task SupersedeWatermarksAsync(OsduDbContext db, short partition, long activity, Guid from, Guid to, CancellationToken ct)
    {
        await using var command = await MoveCommandAsync(db, SupersedeWatermarksSql, partition, activity, from, to, ct).ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Links a slice of the old ledger's records to the move, after <paramref name="after"/> in key order; returns how many
    /// it linked and the last key, to go on from.
    /// </summary>
    public static async Task<(int Linked, Guid? Last)> LinkMovedRecordsAsync(
        OsduDbContext db, short partition, long activity, Guid from, Guid to, Guid? after, int slice, CancellationToken ct)
    {
        await using var command = await MoveCommandAsync(db, LinkMovedRecordsSql, partition, activity, from, to, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@slice", SqlDbType.Int) { Value = slice });
        command.Parameters.Add(new SqlParameter("@after", SqlDbType.UniqueIdentifier) { Value = after is { } key ? key : DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    /// <summary>Moves a slice of <paramref name="table"/>'s rows of the old ledger (a table of <see cref="LedgerMoves.KeyedTables"/>); returns how many.</summary>
    public static Task<int> MoveSliceAsync(OsduDbContext db, string table, short partition, long activity, Guid from, Guid to, int slice, CancellationToken ct)
        => SliceAsync(db, MoveStatement(table, LedgerMoves.KeyedTables, "FlowId"), partition, activity, from, to, slice, ct);

    /// <summary>Points a slice of <paramref name="table"/>'s rows naming the old ledger at the new one (a table of <see cref="LedgerMoves.NamingTables"/>); returns how many.</summary>
    public static Task<int> RenameSliceAsync(OsduDbContext db, string table, short partition, long activity, Guid from, Guid to, int slice, CancellationToken ct)
        => SliceAsync(db, MoveStatement(table, LedgerMoves.NamingTables, "LedgerFlowId"), partition, activity, from, to, slice, ct);

    /// <summary>The records the move linked, and the old ledger's records not moved yet.</summary>
    public static async Task<(long Moved, long Left)> MoveCountsAsync(OsduDbContext db, short partition, long activity, Guid from, Guid to, CancellationToken ct)
    {
        await using var command = await MoveCommandAsync(db, MoveCountsSql, partition, activity, from, to, ct).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>Drops the old ledger from the directory and closes the move with <paramref name="outcome"/> and <paramref name="summary"/>, in one transaction.</summary>
    public static async Task FinishMoveAsync(
        OsduDbContext db, short partition, long activity, Guid from, Guid to, string outcome, string summary, DateTime now, CancellationToken ct)
    {
        await using var command = await MoveCommandAsync(db, FinishMoveSql, partition, activity, from, to, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@outcome", SqlDbType.NVarChar, 16) { Value = outcome });
        command.Parameters.Add(new SqlParameter("@summary", SqlDbType.NVarChar, 2000) { Value = Clip(summary) });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        await using var transaction = (SqlTransaction)await command.Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Closes a move that will not be made, saying why.</summary>
    public static async Task FailMoveAsync(OsduDbContext db, short partition, long activity, Guid from, Guid to, string summary, DateTime now, CancellationToken ct)
    {
        await using var command = await MoveCommandAsync(db, FailMoveSql, partition, activity, from, to, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@summary", SqlDbType.NVarChar, 2000) { Value = Clip(summary) });
        command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The statement that moves a slice of <paramref name="table"/>, which must be one of <paramref name="tables"/>: no other name reaches the SQL.</summary>
    private static string MoveStatement(string table, IReadOnlyList<string> tables, string column)
    {
        if (!tables.Contains(table, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{table}' is not a table a ledger move reaches.", nameof(table));
        }

        return $"UPDATE TOP (@slice) [osdu].[{table}] SET [{column}] = @to WHERE [PartitionId] = @partition AND [{column}] = @from;";
    }

    private static async Task<int> SliceAsync(
        OsduDbContext db, string sql, short partition, long activity, Guid from, Guid to, int slice, CancellationToken ct)
    {
        await using var command = await MoveCommandAsync(db, sql, partition, activity, from, to, ct).ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@slice", SqlDbType.Int) { Value = slice });
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<SqlCommand> MoveCommandAsync(OsduDbContext db, string sql, short partition, long activity, Guid from, Guid to, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var command = Command((SqlConnection)db.Database.GetDbConnection(), null, sql, slice: null);
        command.Parameters.Add(new SqlParameter("@partition", SqlDbType.SmallInt) { Value = partition });
        command.Parameters.Add(new SqlParameter("@activity", SqlDbType.BigInt) { Value = activity });
        command.Parameters.Add(new SqlParameter("@from", SqlDbType.UniqueIdentifier) { Value = new SqlGuid(from) });
        command.Parameters.Add(new SqlParameter("@to", SqlDbType.UniqueIdentifier) { Value = new SqlGuid(to) });
        return command;
    }

    private static string Clip(string text) => text.Length <= 2000 ? text : text[..2000];
}
