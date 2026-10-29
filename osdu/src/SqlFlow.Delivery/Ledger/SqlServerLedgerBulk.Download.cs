using System.Data;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// The downloads of download flows (osdu/docs/reference/flow/download.md, What a run records): a run's listing staged in
// osdu.DownloadScan, merged into osdu.DownloadRecord once whole, and what each write came to recorded on its row, in set-based
// statements, each a seek on an index of the download's rows.
internal static partial class SqlServerLedgerBulk
{
    // The merge of one complete listing: the ids it listed once each (the latest version a repeat listed), served again,
    // changed, new, and the ids it did not list marked gone, never deleted, since their files stay. A new id takes its own
    // name unless an id of the download holds it already, whatever its case, or an id listed in the same run that sorts
    // before it would take it; then the name its hash makes distinct. The names are compared in the column's collation,
    // which folds case as the folders of Windows and macOS do. Two merges of one download go one after the other.
    private const string DownloadMergeSql = """
        DECLARE @lock int;
        EXEC @lock = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 600000;
        IF @lock < 0 THROW 51000, N'Another run of the download held it for ten minutes; this run merged nothing.', 1;

        SELECT s.[TargetId], s.[Kind], s.[Version], s.[PlainName], s.[DistinctName]
        INTO #Scan
        FROM (
            SELECT x.[TargetId], x.[Kind], x.[Version], x.[PlainName], x.[DistinctName],
                ROW_NUMBER() OVER (PARTITION BY x.[TargetId] ORDER BY x.[Version] DESC, x.[ScanId] DESC) AS [Rank]
            FROM [osdu].[DownloadScan] AS x
            WHERE x.[PartitionId] = @partitionId AND x.[DownloadRunId] = @runId) AS s
        WHERE s.[Rank] = 1;
        CREATE UNIQUE CLUSTERED INDEX [IX_Scan] ON #Scan ([TargetId]);

        DECLARE @returned bigint, @changed bigint, @added bigint, @gone bigint;

        UPDATE r SET [Kind] = s.[Kind], [Version] = s.[Version], [GoneUtc] = NULL, [ChangedUtc] = @now
        FROM [osdu].[DownloadRecord] AS r
        INNER JOIN #Scan AS s ON r.[PartitionId] = @partitionId AND r.[DownloadId] = @downloadId AND r.[TargetId] = s.[TargetId]
        WHERE r.[GoneUtc] IS NOT NULL;
        SET @returned = @@ROWCOUNT;

        UPDATE r SET [Kind] = s.[Kind], [Version] = s.[Version], [ChangedUtc] = @now
        FROM [osdu].[DownloadRecord] AS r
        INNER JOIN #Scan AS s ON r.[PartitionId] = @partitionId AND r.[DownloadId] = @downloadId AND r.[TargetId] = s.[TargetId]
        WHERE EXISTS (SELECT r.[Kind], r.[Version] EXCEPT SELECT s.[Kind], s.[Version]);
        SET @changed = @@ROWCOUNT;

        INSERT INTO [osdu].[DownloadRecord] ([PartitionId], [DownloadId], [TargetId], [Kind], [Version], [FileName], [FirstSeenUtc], [ChangedUtc])
        SELECT @partitionId, @downloadId, n.[TargetId], n.[Kind], n.[Version],
            CASE
                WHEN n.[PlainName] IS NOT NULL AND n.[NameRank] = 1 AND NOT EXISTS (
                    SELECT 1 FROM [osdu].[DownloadRecord] AS o
                    WHERE o.[PartitionId] = @partitionId AND o.[DownloadId] = @downloadId AND o.[FileName] = n.[PlainName])
                THEN n.[PlainName]
                ELSE n.[DistinctName]
            END,
            @now, @now
        FROM (
            SELECT s.[TargetId], s.[Kind], s.[Version], s.[PlainName], s.[DistinctName],
                ROW_NUMBER() OVER (PARTITION BY s.[PlainName] ORDER BY s.[TargetId]) AS [NameRank]
            FROM #Scan AS s
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[DownloadRecord] AS r
                WHERE r.[PartitionId] = @partitionId AND r.[DownloadId] = @downloadId AND r.[TargetId] = s.[TargetId])) AS n;
        SET @added = @@ROWCOUNT;

        UPDATE r SET [GoneUtc] = @now
        FROM [osdu].[DownloadRecord] AS r
        WHERE r.[PartitionId] = @partitionId AND r.[DownloadId] = @downloadId AND r.[GoneUtc] IS NULL
            AND NOT EXISTS (SELECT 1 FROM #Scan AS s WHERE s.[TargetId] = r.[TargetId]);
        SET @gone = @@ROWCOUNT;

        DELETE FROM [osdu].[DownloadScan] WHERE [PartitionId] = @partitionId AND [DownloadRunId] = @runId;
        SELECT (SELECT COUNT_BIG(*) FROM #Scan), @added, @changed, @gone, @returned;
        DROP TABLE #Scan;
        """;

    private const string DownloadWritesStageSql = """
        CREATE TABLE #Writes (
            [DownloadRecordId] bigint NOT NULL PRIMARY KEY,
            [Version] bigint NULL,
            [Bytes] bigint NULL,
            [Sha256] binary(32) NULL,
            [Problem] nvarchar(1000) NULL);
        """;

    // What each write came to: a file written holds the version storage returned, and clears an earlier problem; a write that
    // did not happen keeps the file as it was and says why.
    private const string DownloadWritesSql = """
        UPDATE r SET
            [WrittenVersion] = w.[Version], [WrittenUtc] = @now, [WrittenRunId] = @runId, [Bytes] = w.[Bytes], [Sha256] = w.[Sha256],
            [Problem] = NULL, [ProblemUtc] = NULL
        FROM [osdu].[DownloadRecord] AS r
        INNER JOIN #Writes AS w ON r.[PartitionId] = @partitionId AND r.[DownloadRecordId] = w.[DownloadRecordId]
        WHERE w.[Problem] IS NULL;

        UPDATE r SET [Problem] = w.[Problem], [ProblemUtc] = @now
        FROM [osdu].[DownloadRecord] AS r
        INNER JOIN #Writes AS w ON r.[PartitionId] = @partitionId AND r.[DownloadRecordId] = w.[DownloadRecordId]
        WHERE w.[Problem] IS NOT NULL;
        DROP TABLE #Writes;
        """;

    /// <summary>Stages one chunk of a run's listing, in its own transaction: nothing of the download changes until the merge.</summary>
    public static Task<int> AppendDownloadScanAsync(OsduDbContext db, short partitionId, long runId, IReadOnlyList<DownloadScanRow> rows, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            using var table = new DataTable();
            table.Columns.Add("PartitionId", typeof(short));
            table.Columns.Add("DownloadRunId", typeof(long));
            table.Columns.Add("TargetId", typeof(string));
            table.Columns.Add("Kind", typeof(string));
            table.Columns.Add("Version", typeof(long));
            table.Columns.Add("PlainName", typeof(string));
            table.Columns.Add("DistinctName", typeof(string));
            foreach (var row in rows)
            {
                table.Rows.Add(
                    partitionId, runId, row.TargetId, Value(Truncate(row.Kind, DeliveryModel.MaxInventoryKindLength)), Value(row.Version),
                    Value(row.PlainName), row.DistinctName);
            }

            await BulkCopyAsync(connection, transaction, "[osdu].[DownloadScan]", table, ct).ConfigureAwait(false);
            return rows.Count;
        }, ct);

    /// <summary>Merges a run's whole listing into its download, in one transaction held against every other merge of the download.</summary>
    public static Task<DownloadMerge> MergeDownloadAsync(OsduDbContext db, short partitionId, int downloadId, long runId, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, DownloadMergeSql, slice: null);
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = $"osdu-download-{partitionId}-{downloadId}" });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@downloadId", SqlDbType.Int) { Value = downloadId });
            command.Parameters.Add(new SqlParameter("@runId", SqlDbType.BigInt) { Value = runId });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("The merge of the download's listing answered nothing.");
            }

            return new DownloadMerge(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        }, ct);

    /// <summary>Records what each write of a run came to, in one transaction.</summary>
    public static Task<int> RecordDownloadWritesAsync(OsduDbContext db, short partitionId, long runId, IReadOnlyList<DownloadWrite> writes, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, DownloadWritesStageSql, ct).ConfigureAwait(false);
            using (var table = new DataTable())
            {
                table.Columns.Add("DownloadRecordId", typeof(long));
                table.Columns.Add("Version", typeof(long));
                table.Columns.Add("Bytes", typeof(long));
                table.Columns.Add("Sha256", typeof(byte[]));
                table.Columns.Add("Problem", typeof(string));
                foreach (var write in writes.GroupBy(w => w.DownloadRecordId).Select(g => g.Last()))
                {
                    table.Rows.Add(
                        write.DownloadRecordId, Value(write.Version), Value(write.Bytes), write.Sha256 is { } hash ? hash : DBNull.Value,
                        Value(Truncate(write.Problem, DeliveryModel.MaxDownloadProblemLength)));
                }

                await BulkCopyAsync(connection, transaction, "#Writes", table, ct).ConfigureAwait(false);
            }

            await using var command = Command(connection, transaction, DownloadWritesSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@runId", SqlDbType.BigInt) { Value = runId });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
}
