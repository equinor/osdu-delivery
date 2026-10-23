using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Engine;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// Removal flows (osdu/docs/reference/flow/removal.md): converting a source's raw keys the way SQL Server converts text, staging
// them by delivery key, comparing them with the ledger's records and its deletions in one set-based pass, and recording
// what taking each deletion out came to. Every statement keys by the partition first and then the run or the ledger flow, so it
// seeks rather than scans, and none reaches beyond the delivery ledger it names.
internal static partial class SqlServerLedgerBulk
{
    /// <summary>How many rows one statement deletes when a run's staged keys or a superseded run's candidates are cleared.</summary>
    private const int ClearSlice = 50_000;

    /// <summary>The collation an OSDU id is compared exactly under.</summary>
    private const string Exact = "Latin1_General_100_BIN2";

    /// <summary>
    /// Converts a batch of raw key values (osdu/docs/reference/flow/removal.md, How a key is read): the raw values into a
    /// session table of text, then each key column's expression over its value and <c>TRY_CONVERT</c> to the record table's
    /// type, and the removal moment to a UTC instant, read back in order. A value that does not convert comes back null.
    /// </summary>
    public static async Task<IReadOnlyList<ConvertedRemovalKey>> ConvertRemovalKeysAsync(
        OsduDbContext db, RemovalKeyConversion conversion, IReadOnlyList<RawRemovalKey> rows, CancellationToken ct)
    {
        var columns = conversion.Columns.Count;
        var table = new DataTable("#RemovalRaw") { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add("Seq", typeof(int));
        for (var i = 0; i < columns; i++)
        {
            table.Columns.Add(RawColumn(i), typeof(string));
        }

        if (conversion.Moment is not null)
        {
            table.Columns.Add("M", typeof(string));
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Values.Count != columns)
            {
                throw new DeliveryException(
                    string.Create(CultureInfo.InvariantCulture, $"A key row holds {row.Values.Count} value(s), and the record key has {columns} column(s)."));
            }

            var values = new object?[table.Columns.Count];
            values[0] = r;
            for (var i = 0; i < columns; i++)
            {
                values[i + 1] = (object?)row.Values[i] ?? DBNull.Value;
            }

            if (conversion.Moment is not null)
            {
                values[columns + 1] = (object?)row.Moment ?? DBNull.Value;
            }

            table.Rows.Add(values);
        }

        var create = new StringBuilder("CREATE TABLE #RemovalRaw ([Seq] int NOT NULL PRIMARY KEY");
        for (var i = 0; i < columns; i++)
        {
            create.Append(", [").Append(RawColumn(i)).Append("] nvarchar(max) NULL");
        }

        if (conversion.Moment is not null)
        {
            create.Append(", [M] nvarchar(max) NULL");
        }

        create.Append(");");
        var select = new StringBuilder("SELECT [Seq]");
        for (var i = 0; i < columns; i++)
        {
            var target = conversion.Columns[i];
            var value = Over(target.Expression, $"[{RawColumn(i)}]");
            select.Append(", TRY_CONVERT(").Append(ConversionType(target.SqlType)).Append(", ").Append(value).Append(") AS [").Append(RawColumn(i)).Append(']');
        }

        if (conversion.Moment is { } moment)
        {
            // A moment written with an offset is read at it; one written without is read as UTC, as every moment the module reads is.
            select.Append(", CONVERT(datetime2(7), SWITCHOFFSET(TRY_CONVERT(datetimeoffset(7), ").Append(Over(moment.Expression, "[M]")).Append("), '+00:00')) AS [M]");
        }

        select.Append(" FROM #RemovalRaw ORDER BY [Seq];");
        return await InTransactionAsync(db, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, create.ToString(), ct).ConfigureAwait(false);
            try
            {
                await BulkCopyAsync(connection, transaction, "#RemovalRaw", table, ct).ConfigureAwait(false);
                var converted = new List<ConvertedRemovalKey>(rows.Count);
                await using var command = Command(connection, transaction, select.ToString(), slice: null);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var values = new object?[columns];
                    for (var i = 0; i < columns; i++)
                    {
                        values[i] = reader.IsDBNull(i + 1) ? null : reader.GetValue(i + 1);
                    }

                    DateTime? at = conversion.Moment is not null && !reader.IsDBNull(columns + 1)
                        ? DateTime.SpecifyKind(reader.GetDateTime(columns + 1), DateTimeKind.Utc)
                        : null;
                    converted.Add(new ConvertedRemovalKey(values, at));
                }

                return converted;
            }
            finally
            {
                await ExecuteAsync(connection, transaction, "DROP TABLE IF EXISTS #RemovalRaw;", CancellationToken.None).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stages derived keys of one side of a run, each key once: a key the side holds already keeps the later removal moment (the
    /// removed side) or the later change (the record table's side), with the file and row that carried it. Returns how many keys
    /// the batch added to the side.
    /// </summary>
    public static async Task<long> StageRemovalKeysAsync(
        OsduDbContext db, short partitionId, long removalRunId, byte side, IReadOnlyList<StagedRemovalKey> keys, CancellationToken ct)
    {
        var table = new DataTable("#RemovalStage") { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add("DeliveryKey", typeof(Guid));
        table.Columns.Add("SourceKeyJson", typeof(string));
        table.Columns.Add("UpdatedUtc", typeof(DateTime));
        table.Columns.Add("RemovedUtc", typeof(DateTime));
        table.Columns.Add("FileName", typeof(string));
        table.Columns.Add("RowNumber", typeof(long));
        foreach (var key in keys)
        {
            table.Rows.Add(
                key.Key.Value,
                Value(key.SourceKeyJson),
                key.UpdatedUtc is { } updated ? updated : DBNull.Value,
                key.RemovedUtc is { } removed ? removed : DBNull.Value,
                Value(Truncated(key.FileName, DeliveryModel.MaxSourceFileNameLength)),
                key.RowNumber is { } row ? row : DBNull.Value);
        }

        const string create = """
            CREATE TABLE #RemovalStage (
                [DeliveryKey] uniqueidentifier NOT NULL,
                [SourceKeyJson] nvarchar(2000) NULL,
                [UpdatedUtc] datetime2 NULL,
                [RemovedUtc] datetime2 NULL,
                [FileName] nvarchar(800) NULL,
                [RowNumber] bigint NULL);
            """;
        const string merge = """
            DECLARE @added TABLE ([Action] nvarchar(10) NOT NULL);
            WITH n AS (
                SELECT [DeliveryKey], [SourceKeyJson], [UpdatedUtc], [RemovedUtc], [FileName], [RowNumber],
                       ROW_NUMBER() OVER (PARTITION BY [DeliveryKey] ORDER BY [RemovedUtc] DESC, [UpdatedUtc] DESC, [RowNumber]) AS [Rank]
                FROM #RemovalStage)
            MERGE [osdu].[RemovalKey] WITH (HOLDLOCK) AS t
            USING (SELECT * FROM n WHERE [Rank] = 1) AS n
               ON t.[PartitionId] = @partitionId AND t.[RemovalRunId] = @run AND t.[Side] = @side AND t.[DeliveryKey] = n.[DeliveryKey]
            WHEN MATCHED AND (ISNULL(n.[RemovedUtc], '0001-01-01') > ISNULL(t.[RemovedUtc], '0001-01-01')
                           OR ISNULL(n.[UpdatedUtc], '0001-01-01') > ISNULL(t.[UpdatedUtc], '0001-01-01')) THEN
                UPDATE SET [SourceKeyJson] = n.[SourceKeyJson], [UpdatedUtc] = n.[UpdatedUtc], [RemovedUtc] = n.[RemovedUtc],
                           [FileName] = n.[FileName], [RowNumber] = n.[RowNumber]
            WHEN NOT MATCHED THEN
                INSERT ([PartitionId], [RemovalRunId], [Side], [DeliveryKey], [SourceKeyJson], [UpdatedUtc], [RemovedUtc], [FileName], [RowNumber])
                VALUES (@partitionId, @run, @side, n.[DeliveryKey], n.[SourceKeyJson], n.[UpdatedUtc], n.[RemovedUtc], n.[FileName], n.[RowNumber])
            OUTPUT $action INTO @added;
            SELECT COUNT_BIG(*) FROM @added WHERE [Action] = N'INSERT';
            DROP TABLE #RemovalStage;
            """;
        return await InTransactionAsync(db, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, create, ct).ConfigureAwait(false);
            await BulkCopyAsync(connection, transaction, "#RemovalStage", table, ct).ConfigureAwait(false);
            await using var command = Command(connection, transaction, merge, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@run", SqlDbType.BigInt) { Value = removalRunId });
            command.Parameters.Add(new SqlParameter("@side", SqlDbType.TinyInt) { Value = side });
            var added = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt64(added, CultureInfo.InvariantCulture);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The detection (osdu/docs/reference/flow/removal.md, What a run finds), in one transaction: the ledger's records of the
    /// flow in the scope, and the record table's keys besides, against the source's current keys and its removed keys. A key not
    /// in the current keys, or listed as removed, is found removed unless its row arrived after the moment its removal stands for
    /// (it came back). Deletions whose key the source holds again, or whose record was delivered again since they were found, are
    /// lifted; a deletion found again keeps the later moment; and what the ledger holds no deletion of yet becomes one, pending,
    /// or waiting for approval when the guard holds what the run found. A plan counts all of it and writes nothing.
    /// </summary>
    public static Task<RemovalDetection> DetectRemovalsAsync(
        OsduDbContext db, short partitionId, Guid flowId, long removalRunId, RemovalComparison comparison, CancellationToken ct)
    {
        const string sql = """
            SET NOCOUNT ON;
            CREATE TABLE #RemovalLedger (
                [DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY,
                [Status] nvarchar(16) NOT NULL,
                [SourceKeyJson] nvarchar(2000) NULL,
                [Moved] datetime2 NULL);

            INSERT INTO #RemovalLedger ([DeliveryKey], [Status], [SourceKeyJson], [Moved])
            SELECT r.[DeliveryKey], r.[Status], r.[SourceKeyJson],
                   CASE WHEN r.[PendingSourceUpdatedUtc] IS NOT NULL AND (r.[SourceUpdatedUtc] IS NULL OR r.[PendingSourceUpdatedUtc] > r.[SourceUpdatedUtc])
                        THEN r.[PendingSourceUpdatedUtc] ELSE r.[SourceUpdatedUtc] END
            FROM [osdu].[Record] r
            WHERE r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId
              AND (@scope IS NULL OR EXISTS (
                    SELECT 1 FROM [osdu].[Submission] s
                    WHERE s.[PartitionId] = r.[PartitionId] AND s.[SubmissionId] = r.[LastSubmissionId]
                      AND NOT EXISTS (
                          SELECT 1 FROM OPENJSON(@scope) v
                          WHERE ISNULL(JSON_VALUE(s.[ParametersJson], N'$."' + v.[key] + N'"'), NCHAR(1)) COLLATE Latin1_General_100_BIN2
                                <> v.[value] COLLATE Latin1_General_100_BIN2)));

            DECLARE @compared bigint =
                (SELECT COUNT_BIG(*) FROM #RemovalLedger WHERE [Status] <> N'deleted')
              + (SELECT COUNT_BIG(*) FROM [osdu].[RemovalKey] i
                 WHERE i.[PartitionId] = @partitionId AND i.[RemovalRunId] = @run AND i.[Side] = 3
                   AND NOT EXISTS (SELECT 1 FROM #RemovalLedger l WHERE l.[DeliveryKey] = i.[DeliveryKey]));

            CREATE TABLE #RemovalKnown (
                [DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY,
                [SourceKeyJson] nvarchar(2000) NULL,
                [Moved] datetime2 NULL);

            INSERT INTO #RemovalKnown ([DeliveryKey], [SourceKeyJson], [Moved])
            SELECT l.[DeliveryKey], COALESCE(i.[SourceKeyJson], l.[SourceKeyJson]),
                   CASE WHEN i.[UpdatedUtc] IS NOT NULL AND (l.[Moved] IS NULL OR i.[UpdatedUtc] > l.[Moved]) THEN i.[UpdatedUtc] ELSE l.[Moved] END
            FROM #RemovalLedger l
            LEFT JOIN [osdu].[RemovalKey] i
                   ON i.[PartitionId] = @partitionId AND i.[RemovalRunId] = @run AND i.[Side] = 3 AND i.[DeliveryKey] = l.[DeliveryKey];

            INSERT INTO #RemovalKnown ([DeliveryKey], [SourceKeyJson], [Moved])
            SELECT i.[DeliveryKey], i.[SourceKeyJson], i.[UpdatedUtc]
            FROM [osdu].[RemovalKey] i
            WHERE i.[PartitionId] = @partitionId AND i.[RemovalRunId] = @run AND i.[Side] = 3
              AND NOT EXISTS (SELECT 1 FROM #RemovalLedger l WHERE l.[DeliveryKey] = i.[DeliveryKey]);

            CREATE TABLE #RemovalFound (
                [DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY,
                [Reason] nvarchar(16) NOT NULL,
                [RemovedUtc] datetime2 NOT NULL,
                [FileName] nvarchar(800) NULL,
                [RowNumber] bigint NULL,
                [SourceKeyJson] nvarchar(2000) NULL,
                [Moved] datetime2 NULL);

            INSERT INTO #RemovalFound ([DeliveryKey], [Reason], [RemovedUtc], [FileName], [RowNumber], [SourceKeyJson], [Moved])
            SELECT k.[DeliveryKey],
                   CASE WHEN d.[DeliveryKey] IS NOT NULL THEN N'listed' ELSE N'absent' END,
                   CASE WHEN d.[DeliveryKey] IS NOT NULL THEN d.[RemovedUtc] ELSE @sourceRead END,
                   d.[FileName], d.[RowNumber], k.[SourceKeyJson], k.[Moved]
            FROM #RemovalKnown k
            LEFT JOIN [osdu].[RemovalKey] d
                   ON @readsRemoved = 1 AND d.[PartitionId] = @partitionId AND d.[RemovalRunId] = @run AND d.[Side] = 2 AND d.[DeliveryKey] = k.[DeliveryKey]
            LEFT JOIN [osdu].[RemovalKey] s
                   ON @readsSource = 1 AND s.[PartitionId] = @partitionId AND s.[RemovalRunId] = @run AND s.[Side] = 1 AND s.[DeliveryKey] = k.[DeliveryKey]
            WHERE (@readsSource = 1 AND s.[DeliveryKey] IS NULL)
               OR (d.[DeliveryKey] IS NOT NULL AND d.[RemovedUtc] IS NOT NULL
                   AND NOT (@readsSource = 1 AND s.[DeliveryKey] IS NOT NULL AND @sourceRead >= d.[RemovedUtc]));

            DELETE FROM #RemovalFound
            WHERE ([Reason] = N'absent' AND [Moved] > DATEADD(SECOND, -@margin, @sourceRead))
               OR ([Reason] = N'listed' AND [Moved] > [RemovedUtc]);
            DECLARE @cameBack bigint = @@ROWCOUNT;

            CREATE TABLE #RemovalLifted ([DeliveryKey] uniqueidentifier NOT NULL PRIMARY KEY, [State] nvarchar(24) NOT NULL);
            IF @readsSource = 1
                INSERT INTO #RemovalLifted ([DeliveryKey], [State])
                SELECT t.[DeliveryKey], t.[State]
                FROM [osdu].[RemovedKey] t
                WHERE t.[PartitionId] = @partitionId AND t.[FlowId] = @flowId AND t.[RemovedUtc] < @sourceRead
                  AND EXISTS (SELECT 1 FROM [osdu].[RemovalKey] s
                              WHERE s.[PartitionId] = @partitionId AND s.[RemovalRunId] = @run AND s.[Side] = 1 AND s.[DeliveryKey] = t.[DeliveryKey]);

            INSERT INTO #RemovalLifted ([DeliveryKey], [State])
            SELECT t.[DeliveryKey], t.[State]
            FROM [osdu].[RemovedKey] t
            INNER JOIN [osdu].[Record] r ON r.[PartitionId] = t.[PartitionId] AND r.[FlowId] = t.[FlowId] AND r.[DeliveryKey] = t.[DeliveryKey]
            WHERE t.[PartitionId] = @partitionId AND t.[FlowId] = @flowId AND r.[Status] = N'delivered' AND r.[LastDeliveredUtc] > t.[CreatedUtc]
              AND NOT EXISTS (SELECT 1 FROM #RemovalLifted x WHERE x.[DeliveryKey] = t.[DeliveryKey]);
            DECLARE @lifted bigint = (SELECT COUNT_BIG(*) FROM #RemovalLifted);

            IF @write = 1
            BEGIN
                DELETE t FROM [osdu].[RemovedKey] t
                INNER JOIN #RemovalLifted x ON t.[PartitionId] = @partitionId AND t.[FlowId] = @flowId AND t.[DeliveryKey] = x.[DeliveryKey];

                -- A record a deletion took out comes back with its key: the flow's next run reads its row and delivers it again.
                UPDATE r SET r.[PlanRequestedUtc] = @now, r.[UpdatedUtc] = @now
                FROM [osdu].[Record] r
                INNER JOIN #RemovalLifted x ON r.[PartitionId] = @partitionId AND r.[FlowId] = @flowId AND r.[DeliveryKey] = x.[DeliveryKey]
                WHERE x.[State] = N'removed' AND r.[Status] = N'deleted' AND r.[PlanRequestedUtc] IS NULL;

                UPDATE t SET t.[RemovedUtc] = f.[RemovedUtc], t.[Reason] = f.[Reason], t.[FileName] = f.[FileName], t.[RowNumber] = f.[RowNumber], t.[UpdatedUtc] = @now
                FROM [osdu].[RemovedKey] t
                INNER JOIN #RemovalFound f ON t.[PartitionId] = @partitionId AND t.[FlowId] = @flowId AND t.[DeliveryKey] = f.[DeliveryKey]
                WHERE f.[RemovedUtc] > t.[RemovedUtc];
            END

            DELETE f FROM #RemovalFound f
            WHERE EXISTS (SELECT 1 FROM [osdu].[RemovedKey] t WHERE t.[PartitionId] = @partitionId AND t.[FlowId] = @flowId AND t.[DeliveryKey] = f.[DeliveryKey])
              AND NOT EXISTS (SELECT 1 FROM #RemovalLifted x WHERE x.[DeliveryKey] = f.[DeliveryKey]);

            DECLARE @detected bigint = (SELECT COUNT_BIG(*) FROM #RemovalFound);
            DECLARE @sourceKeys bigint = CASE WHEN @readsSource = 1
                THEN (SELECT COUNT_BIG(*) FROM [osdu].[RemovalKey] s WHERE s.[PartitionId] = @partitionId AND s.[RemovalRunId] = @run AND s.[Side] = 1)
                ELSE NULL END;
            DECLARE @guarded bit = CASE
                WHEN @detected = 0 THEN 0
                WHEN @guardRecords IS NOT NULL AND @detected > @guardRecords THEN 1
                WHEN @compared > 0 AND @detected * 100 > CAST(@guardPercent AS bigint) * @compared THEN 1
                WHEN @readsSource = 1 AND @sourceKeys = 0 THEN 1
                ELSE 0 END;

            IF @write = 1
                INSERT INTO [osdu].[RemovedKey]
                    ([PartitionId], [FlowId], [DeliveryKey], [SourceKeyJson], [Reason], [RemovedUtc], [FileName], [RowNumber], [State], [Outcome], [Detail], [Tries],
                     [RemovalFlow], [Entry], [DetectedRunId], [DoneRunId], [RunId], [ApprovedBy], [ApprovedUtc], [CreatedUtc], [DoneUtc], [UpdatedUtc])
                SELECT @partitionId, @flowId, f.[DeliveryKey], f.[SourceKeyJson], f.[Reason], f.[RemovedUtc], f.[FileName], f.[RowNumber],
                       CASE WHEN @guarded = 1 THEN N'awaiting-approval' ELSE N'pending' END, NULL, NULL, 0,
                       @removalFlow, @entry, @run, NULL, @runId, NULL, NULL, @now, NULL, @now
                FROM #RemovalFound f;

            DECLARE @pending bigint = (SELECT COUNT_BIG(*) FROM [osdu].[RemovedKey] t
                WHERE t.[PartitionId] = @partitionId AND t.[FlowId] = @flowId AND t.[State] = N'pending'
                  AND (@write = 1 OR NOT EXISTS (SELECT 1 FROM #RemovalLifted x WHERE x.[DeliveryKey] = t.[DeliveryKey])))
                + CASE WHEN @write = 0 AND @guarded = 0 THEN @detected ELSE 0 END;
            DECLARE @awaiting bigint = (SELECT COUNT_BIG(*) FROM [osdu].[RemovedKey] t
                WHERE t.[PartitionId] = @partitionId AND t.[FlowId] = @flowId AND t.[State] = N'awaiting-approval'
                  AND (@write = 1 OR NOT EXISTS (SELECT 1 FROM #RemovalLifted x WHERE x.[DeliveryKey] = t.[DeliveryKey])))
                + CASE WHEN @write = 0 AND @guarded = 1 THEN @detected ELSE 0 END;

            SELECT @compared, @detected, @guarded, @cameBack, @lifted, @pending, @awaiting;

            DROP TABLE #RemovalLifted;
            DROP TABLE #RemovalFound;
            DROP TABLE #RemovalKnown;
            DROP TABLE #RemovalLedger;
            """;
        return InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, sql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
            command.Parameters.Add(new SqlParameter("@run", SqlDbType.BigInt) { Value = removalRunId });
            command.Parameters.Add(new SqlParameter("@readsSource", SqlDbType.Bit) { Value = comparison.ReadsSource });
            command.Parameters.Add(new SqlParameter("@readsRemoved", SqlDbType.Bit) { Value = comparison.ReadsRemoved });
            command.Parameters.Add(new SqlParameter("@sourceRead", SqlDbType.DateTime2) { Value = comparison.SourceReadUtc is { } read ? read : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@margin", SqlDbType.Int) { Value = (int)SourceRemovals.ClockMargin.TotalSeconds });
            command.Parameters.Add(new SqlParameter("@scope", SqlDbType.NVarChar, -1) { Value = comparison.Scope.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(comparison.Scope) });
            command.Parameters.Add(new SqlParameter("@write", SqlDbType.Bit) { Value = comparison.Write });
            command.Parameters.Add(new SqlParameter("@removalFlow", SqlDbType.NVarChar, DeliveryLedger.MaxFlowNameLength) { Value = comparison.RemovalFlow });
            command.Parameters.Add(new SqlParameter("@entry", SqlDbType.NVarChar, DeliveryModel.MaxRemovalNameLength) { Value = comparison.Entry });
            command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = comparison.RunId is { } runId ? runId : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@guardPercent", SqlDbType.Int) { Value = comparison.GuardPercent });
            command.Parameters.Add(new SqlParameter("@guardRecords", SqlDbType.BigInt) { Value = comparison.GuardRecords is { } records ? records : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = comparison.NowUtc });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("The removal's comparison returned no counts; nothing of it was kept.");
            }

            return new RemovalDetection
            {
                Compared = reader.GetInt64(0),
                Detected = reader.GetInt64(1),
                Guarded = reader.GetBoolean(2),
                CameBack = reader.GetInt64(3),
                Lifted = reader.GetInt64(4),
                Pending = reader.GetInt64(5),
                AwaitingApproval = reader.GetInt64(6),
            };
        }, ct);
    }

    /// <summary>
    /// Records what tries to take out deletions came to, one statement per slice of them: a finishing outcome makes the deletion
    /// removed under the run; any other leaves it pending with the outcome and why. Every try is counted.
    /// </summary>
    public static async Task SettleRemovalsAsync(
        OsduDbContext db, short partitionId, Guid flowId, long removalRunId, Guid? runId, IReadOnlyList<RemovalKeyOutcome> outcomes, DateTime nowUtc, CancellationToken ct)
    {
        const string sql = """
            UPDATE t SET
                t.[Outcome] = n.[Outcome],
                t.[Detail] = n.[Detail],
                t.[Tries] = t.[Tries] + 1,
                t.[RunId] = @runId,
                t.[State] = CASE WHEN n.[Outcome] IN (N'removed', N'already-gone', N'settled', N'kept-out') THEN N'removed' ELSE t.[State] END,
                t.[DoneRunId] = CASE WHEN n.[Outcome] IN (N'removed', N'already-gone', N'settled', N'kept-out') THEN @run ELSE t.[DoneRunId] END,
                t.[DoneUtc] = CASE WHEN n.[Outcome] IN (N'removed', N'already-gone', N'settled', N'kept-out') THEN @now ELSE t.[DoneUtc] END,
                t.[UpdatedUtc] = @now
            FROM [osdu].[RemovedKey] t
            INNER JOIN OPENJSON(@outcomes) WITH ([DeliveryKey] uniqueidentifier '$.k', [Outcome] nvarchar(24) '$.o', [Detail] nvarchar(1000) '$.d') n
                    ON t.[PartitionId] = @partitionId AND t.[FlowId] = @flowId AND t.[DeliveryKey] = n.[DeliveryKey]
            WHERE t.[State] = N'pending';
            """;
        foreach (var slice in outcomes.Chunk(1000))
        {
            var json = JsonSerializer.Serialize(slice.Select(o => new { k = o.Key.Value, o = o.Outcome, d = SourceRemovals.Detail(o.Detail) }));
            await InTransactionAsync(db, async (connection, transaction) =>
            {
                await using var command = Command(connection, transaction, sql, slice: null);
                command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
                command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
                command.Parameters.Add(new SqlParameter("@run", SqlDbType.BigInt) { Value = removalRunId });
                command.Parameters.Add(new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId is { } id ? id : DBNull.Value });
                command.Parameters.Add(new SqlParameter("@outcomes", SqlDbType.NVarChar, -1) { Value = json });
                command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = nowUtc });
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The ids among <paramref name="ids"/> a record of another ledger of the partition holds live (claimed or not, and not
    /// removed): a removal leaves such an id in OSDU, since taking it out would take it from under that ledger.
    /// </summary>
    public static async Task<IReadOnlySet<string>> SharedIdsAsync(OsduDbContext db, short partitionId, Guid flowId, IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        const string sql = """
            SELECT DISTINCT n.[Id]
            FROM OPENJSON(@ids) WITH ([Id] nvarchar(500) '$') n
            WHERE EXISTS (
                SELECT 1 FROM [osdu].[Record] r
                WHERE r.[PartitionId] = @partitionId AND r.[TargetId] = n.[Id] AND r.[FlowId] <> @flowId AND r.[Status] <> N'deleted'
                  AND r.[TargetId] COLLATE Latin1_General_100_BIN2 = n.[Id] COLLATE Latin1_General_100_BIN2);
            """;
        var shared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slice in ids.Distinct(StringComparer.Ordinal).Chunk(1000))
        {
            await InTransactionAsync(db, async (connection, transaction) =>
            {
                await using var command = Command(connection, transaction, sql, slice: null);
                command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
                command.Parameters.Add(new SqlParameter("@flowId", SqlDbType.UniqueIdentifier) { Value = flowId });
                command.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(slice) });
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    shared.Add(reader.GetString(0));
                }

                return shared.Count;
            }, ct).ConfigureAwait(false);
        }

        return shared;
    }

    /// <summary>Closes a run and clears what it staged, a slice at a time so the log never holds a whole run's keys.</summary>
    public static async Task CompleteRemovalRunAsync(
        OsduDbContext db, short partitionId, long removalRunId, string status, string? error, long? activityId, DateTime nowUtc, CancellationToken ct)
    {
        const string close = """
            UPDATE [osdu].[RemovalRun]
               SET [Status] = @status, [CompletedUtc] = @now, [Error] = @error, [ActivityId] = COALESCE(@activityId, [ActivityId])
             WHERE [PartitionId] = @partitionId AND [RemovalRunId] = @run;
            SELECT @@ROWCOUNT;
            """;
        var updated = await InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, close, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@run", SqlDbType.BigInt) { Value = removalRunId });
            command.Parameters.Add(new SqlParameter("@status", SqlDbType.NVarChar, 16) { Value = status });
            command.Parameters.Add(new SqlParameter("@error", SqlDbType.NVarChar, 4000) { Value = error is null ? DBNull.Value : Truncated(error, 4000) });
            command.Parameters.Add(new SqlParameter("@activityId", SqlDbType.BigInt) { Value = activityId is { } id ? id : DBNull.Value });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = nowUtc });
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }, ct).ConfigureAwait(false);
        if (updated == 0)
        {
            throw new DeliveryException($"Removal run {removalRunId} is not in the ledger.");
        }

        await ClearAsync(db, "[osdu].[RemovalKey]", partitionId, removalRunId, ct).ConfigureAwait(false);
    }

    /// <summary>Clears the keys staged by runs of the same removal its process left open: what <c>StartRemovalRunAsync</c> closed failed.</summary>
    public static async Task ClearRemovalStagesAsync(OsduDbContext db, short partitionId, IReadOnlyList<long> removalRunIds, CancellationToken ct)
    {
        foreach (var run in removalRunIds)
        {
            await ClearAsync(db, "[osdu].[RemovalKey]", partitionId, run, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes a run's rows of <paramref name="table"/>, <see cref="ClearSlice"/> at a time, each slice a statement of its own.</summary>
    private static async Task ClearAsync(OsduDbContext db, string table, short partitionId, long removalRunId, CancellationToken ct)
    {
        var sql = $"DELETE TOP ({ClearSlice.ToString(CultureInfo.InvariantCulture)}) FROM {table} WHERE [PartitionId] = @partitionId AND [RemovalRunId] = @run;";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var deleted = await InTransactionAsync(db, async (connection, transaction) =>
            {
                await using var command = Command(connection, transaction, sql, slice: null);
                command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
                command.Parameters.Add(new SqlParameter("@run", SqlDbType.BigInt) { Value = removalRunId });
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (deleted < ClearSlice)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The SQL Server type a raw value converts to for a key column of type <paramref name="sqlType"/>: the column's own type,
    /// except that text converts to <c>nvarchar(max)</c>, so a value is never cut to the column's width and matched to a
    /// shorter key.
    /// </summary>
    private static string ConversionType(string sqlType)
    {
        var type = sqlType.Trim().ToLowerInvariant();
        return type.StartsWith("varchar", StringComparison.Ordinal) || type.StartsWith("nvarchar", StringComparison.Ordinal)
            || type.StartsWith("char", StringComparison.Ordinal) || type.StartsWith("nchar", StringComparison.Ordinal)
            || type is "text" or "ntext" or "sysname"
            ? "nvarchar(max)"
            : sqlType;
    }

    /// <summary>The expression over a raw column: the column itself, or the expression with <c>@ColName</c> standing for it.</summary>
    private static string Over(string? expression, string column)
        => string.IsNullOrWhiteSpace(expression) ? column : $"({ColumnTransformExpression.Substitute(expression, column)})";

    private static string RawColumn(int index) => "K" + index.ToString(CultureInfo.InvariantCulture);

    private static string? Truncated(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}
