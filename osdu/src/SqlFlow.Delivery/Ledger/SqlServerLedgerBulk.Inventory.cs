using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// The inventories of inventory flows (osdu/docs/reference/flow/inventory-builds.md, The tables an inventory keeps): a build's
// read staged in osdu.InventoryScan, merged into osdu.InventoryRecord once whole, and every id compared with the ledgers
// of the partition in set-based statements, each a seek on an index of the table it joins (osdu.Record by its claimed
// id, osdu.Artifact and osdu.PurgedRecord by OSDU id).
internal static partial class SqlServerLedgerBulk
{
    // The merge of one complete read: the ids it listed once each (the latest version a repeat listed), served again, changed,
    // new, and the ids it did not list marked gone, never deleted. Two merges of one inventory go one after the other.
    private const string InventoryMergeSql = """
        DECLARE @lock int;
        EXEC @lock = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 600000;
        IF @lock < 0 THROW 51000, N'Another build of the inventory held it for ten minutes; this build merged nothing.', 1;

        SELECT s.[TargetId], s.[Kind], s.[Version], s.[CreateUser], s.[CreateTime], s.[ModifyUser], s.[ModifyTime]
        INTO #Scan
        FROM (
            SELECT x.*, ROW_NUMBER() OVER (PARTITION BY x.[TargetId] ORDER BY x.[Version] DESC, x.[ScanId] DESC) AS [Rank]
            FROM [osdu].[InventoryScan] AS x
            WHERE x.[PartitionId] = @partitionId AND x.[InventoryRunId] = @runId) AS s
        WHERE s.[Rank] = 1;
        CREATE UNIQUE CLUSTERED INDEX [IX_Scan] ON #Scan ([TargetId]);

        DECLARE @returned bigint, @changed bigint, @added bigint, @gone bigint;

        UPDATE r SET
            [Kind] = s.[Kind], [Version] = s.[Version], [CreateUser] = s.[CreateUser], [CreateTime] = s.[CreateTime],
            [ModifyUser] = s.[ModifyUser], [ModifyTime] = s.[ModifyTime],
            [FirstSeenUtc] = COALESCE(r.[FirstSeenUtc], @now), [GoneUtc] = NULL, [ChangedUtc] = @now
        FROM [osdu].[InventoryRecord] AS r
        INNER JOIN #Scan AS s ON r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = s.[TargetId]
        WHERE r.[GoneUtc] IS NOT NULL OR r.[FirstSeenUtc] IS NULL;
        SET @returned = @@ROWCOUNT;

        UPDATE r SET
            [Kind] = s.[Kind], [Version] = s.[Version], [CreateUser] = s.[CreateUser], [CreateTime] = s.[CreateTime],
            [ModifyUser] = s.[ModifyUser], [ModifyTime] = s.[ModifyTime], [ChangedUtc] = @now
        FROM [osdu].[InventoryRecord] AS r
        INNER JOIN #Scan AS s ON r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = s.[TargetId]
        WHERE EXISTS (
            SELECT r.[Kind], r.[Version], r.[CreateUser], r.[CreateTime], r.[ModifyUser], r.[ModifyTime]
            EXCEPT
            SELECT s.[Kind], s.[Version], s.[CreateUser], s.[CreateTime], s.[ModifyUser], s.[ModifyTime]);
        SET @changed = @@ROWCOUNT;

        INSERT INTO [osdu].[InventoryRecord] ([PartitionId], [InventoryId], [TargetId], [Kind], [Version], [CreateUser], [CreateTime],
                [ModifyUser], [ModifyTime], [FirstSeenUtc], [ChangedUtc], [Finding], [FindingUtc])
        SELECT @partitionId, @inventoryId, s.[TargetId], s.[Kind], s.[Version], s.[CreateUser], s.[CreateTime],
                s.[ModifyUser], s.[ModifyTime], @now, @now, N'unreconciled', @now
        FROM #Scan AS s
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[InventoryRecord] AS r
            WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = s.[TargetId]);
        SET @added = @@ROWCOUNT;

        UPDATE r SET [GoneUtc] = @now
        FROM [osdu].[InventoryRecord] AS r
        WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[GoneUtc] IS NULL AND r.[FirstSeenUtc] IS NOT NULL
            AND NOT EXISTS (SELECT 1 FROM #Scan AS s WHERE s.[TargetId] = r.[TargetId]);
        SET @gone = @@ROWCOUNT;

        DELETE FROM [osdu].[InventoryScan] WHERE [PartitionId] = @partitionId AND [InventoryRunId] = @runId;
        SELECT (SELECT COUNT_BIG(*) FROM #Scan), @added, @changed, @gone, @returned;
        DROP TABLE #Scan;
        """;

    // What the ledgers of the partition hold of one id: the record that claims it, the latest artifact a delivery recorded for it
    // and whether that artifact's record is still in its ledger, and the latest record a ledger purged under it.
    private const string InventoryLedgerApply = """
        OUTER APPLY (
            SELECT TOP (1) x.[FlowId], x.[DeliveryKey], x.[Status], x.[TargetVersion]
            FROM [osdu].[Record] AS x
            WHERE x.[ClaimedTargetId] = r.[TargetId] AND x.[PartitionId] = @partitionId) AS rec
        OUTER APPLY (
            SELECT TOP (1) a.[ArtifactId], a.[State], a.[FlowId], a.[DeliveryKey],
                CASE WHEN EXISTS (
                    SELECT 1 FROM [osdu].[Record] AS y
                    WHERE y.[PartitionId] = @partitionId AND y.[FlowId] = a.[FlowId] AND y.[DeliveryKey] = a.[DeliveryKey])
                THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS [Held]
            FROM [osdu].[Artifact] AS a
            WHERE a.[PartitionId] = @partitionId AND a.[TargetId] = r.[TargetId]
            ORDER BY a.[ArtifactId] DESC) AS art
        OUTER APPLY (
            SELECT TOP (1) p.[FlowId], p.[DeliveryKey]
            FROM [osdu].[PurgedRecord] AS p
            WHERE p.[PartitionId] = @partitionId AND p.[TargetId] = r.[TargetId]
            ORDER BY p.[PurgedRecordId] DESC) AS pur
        """;

    // The finding of an id OSDU serves (r, an inventory row), from what the ledgers hold of it (InventoryLedgerApply) and the
    // identities this estate writes as (@owners): the one rule a reconcile sets findings by, and a removal checks them again by.
    private const string InventoryFindingCase = """
            CASE
                WHEN rec.[DeliveryKey] IS NOT NULL THEN CASE
                    WHEN rec.[Status] = N'deleted' THEN N'stale'
                    WHEN rec.[TargetVersion] IS NULL THEN N'unconfirmed'
                    WHEN r.[Version] IS NULL OR rec.[TargetVersion] = r.[Version] THEN N'tracked'
                    ELSE N'drifted' END
                WHEN art.[ArtifactId] IS NOT NULL THEN CASE
                    WHEN art.[Held] = 0 THEN N'forgotten'
                    WHEN art.[State] IN (N'due', N'failed') THEN N'undoing'
                    WHEN art.[State] IN (N'intent', N'pending') THEN N'unconfirmed'
                    WHEN art.[State] IN (N'live', N'restored') THEN N'tracked'
                    WHEN art.[State] = N'superseded' THEN N'superseded'
                    ELSE N'stale' END
                WHEN pur.[DeliveryKey] IS NOT NULL THEN N'forgotten'
                WHEN r.[CreateUser] IS NOT NULL AND r.[CreateUser] IN (SELECT o.[value] FROM OPENJSON(@owners) AS o) THEN N'orphan'
                ELSE N'foreign'
            END
        """;

    // Whether a ledger expects the id (r, an inventory row) to be served, from what the ledgers hold of it (InventoryLedgerApply):
    // its record delivered (or reverted) at a version, or, claimed by no record, a live minted id whose record its ledger holds.
    // Written as a CASE so it is true or false, never unknown where no record or artifact is held, and NOT of it holds where it does not.
    private const string InventoryExpected = """
        (CASE WHEN (rec.[DeliveryKey] IS NOT NULL AND rec.[TargetVersion] IS NOT NULL AND rec.[Status] IN (N'delivered', N'reverted'))
            OR (rec.[DeliveryKey] IS NULL AND art.[State] = N'live' AND art.[Held] = 1) THEN 1 ELSE 0 END = 1)
        """;

    // The findings of the ids OSDU serves, set only where they changed.
    private const string InventoryReconcileSql = """
        SELECT r.[InventoryRecordId],
            COALESCE(rec.[FlowId], art.[FlowId], pur.[FlowId]) AS [LedgerFlowId],
            COALESCE(rec.[DeliveryKey], art.[DeliveryKey], pur.[DeliveryKey]) AS [DeliveryKey],
            rec.[Status] AS [LedgerStatus], rec.[TargetVersion] AS [LedgerVersion], art.[ArtifactId], art.[State] AS [ArtifactState],
            {{finding}} AS [Finding],
            CASE
                WHEN rec.[DeliveryKey] IS NOT NULL AND rec.[Status] = N'deleted' THEN N'the ledger marks the record removed, and OSDU still serves it'
                WHEN rec.[DeliveryKey] IS NOT NULL AND rec.[TargetVersion] IS NULL THEN CONCAT(N'the ledger''s record is ', rec.[Status], N' and confirmed no delivery: a write that landed without its answer')
                WHEN rec.[DeliveryKey] IS NOT NULL AND r.[Version] IS NOT NULL AND rec.[TargetVersion] <> r.[Version] THEN CONCAT(N'the ledger holds version ', rec.[TargetVersion], N', OSDU serves version ', r.[Version])
                WHEN rec.[DeliveryKey] IS NULL AND art.[ArtifactId] IS NOT NULL AND art.[Held] = 0 THEN N'a delivery of a record its ledger no longer holds minted it'
                WHEN rec.[DeliveryKey] IS NULL AND art.[ArtifactId] IS NOT NULL AND art.[State] IN (N'due', N'failed') THEN CONCAT(N'what an unfinished delivery left: its undo is ', art.[State])
                WHEN rec.[DeliveryKey] IS NULL AND art.[ArtifactId] IS NOT NULL AND art.[State] IN (N'removed', N'gone', N'kept') THEN CONCAT(N'the ledger''s artifact is ', art.[State], N', and OSDU still serves it')
                WHEN rec.[DeliveryKey] IS NULL AND art.[ArtifactId] IS NULL AND pur.[DeliveryKey] IS NOT NULL THEN N'the record was purged from its ledger, and OSDU still serves it'
                ELSE NULL
            END AS [Detail]
        INTO #Found
        FROM [osdu].[InventoryRecord] AS r
        {{ledger}}
        WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[GoneUtc] IS NULL AND r.[FirstSeenUtc] IS NOT NULL;

        UPDATE r SET
            [FindingUtc] = CASE WHEN r.[Finding] = f.[Finding] THEN r.[FindingUtc] ELSE @now END,
            [Finding] = f.[Finding], [LedgerFlowId] = f.[LedgerFlowId], [DeliveryKey] = f.[DeliveryKey], [LedgerStatus] = f.[LedgerStatus],
            [LedgerVersion] = f.[LedgerVersion], [ArtifactId] = f.[ArtifactId], [ArtifactState] = f.[ArtifactState], [Detail] = f.[Detail]
        FROM [osdu].[InventoryRecord] AS r
        INNER JOIN #Found AS f ON r.[PartitionId] = @partitionId AND r.[InventoryRecordId] = f.[InventoryRecordId]
        WHERE EXISTS (
            SELECT r.[Finding], r.[LedgerFlowId], r.[DeliveryKey], r.[LedgerStatus], r.[LedgerVersion], r.[ArtifactId], r.[ArtifactState], r.[Detail]
            EXCEPT
            SELECT f.[Finding], f.[LedgerFlowId], f.[DeliveryKey], f.[LedgerStatus], f.[LedgerVersion], f.[ArtifactId], f.[ArtifactState], f.[Detail]);
        DROP TABLE #Found;
        """;

    // The identities that created the ids of an inventory a ledger claims, with how many each created.
    private const string InventoryOwnersSql = """
        SELECT TOP (@max) r.[CreateUser], COUNT_BIG(*) AS [Records]
        FROM [osdu].[InventoryRecord] AS r
        WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[GoneUtc] IS NULL AND r.[CreateUser] IS NOT NULL
            AND (EXISTS (SELECT 1 FROM [osdu].[Record] AS x WHERE x.[ClaimedTargetId] = r.[TargetId] AND x.[PartitionId] = @partitionId)
                OR EXISTS (SELECT 1 FROM [osdu].[Artifact] AS a WHERE a.[PartitionId] = @partitionId AND a.[TargetId] = r.[TargetId]))
        GROUP BY r.[CreateUser]
        ORDER BY COUNT_BIG(*) DESC, r.[CreateUser];
        """;

    // The ids a ledger expects that the read did not list: rows of the inventory no longer (or never) listed whose record is
    // delivered or whose minted id is live, and, for an inventory that covers its entity type whole, the ledgers' delivered
    // records and live minted ids of the type the inventory holds no row of. They come least recently asked of storage first
    // (never asked, then the longest ago), so builds that each ask for @max of them take turns through every one.
    private const string InventoryCandidatesSql = """
        SELECT TOP (@max) u.[TargetId], u.[Known], u.[LedgerFlowId], u.[DeliveryKey], u.[LedgerStatus], u.[LedgerVersion], u.[ArtifactId], u.[ArtifactState]
        FROM (
            SELECT r.[TargetId], CAST(1 AS bit) AS [Known],
                COALESCE(rec.[FlowId], art.[FlowId]) AS [LedgerFlowId], COALESCE(rec.[DeliveryKey], art.[DeliveryKey]) AS [DeliveryKey],
                rec.[Status] AS [LedgerStatus], rec.[TargetVersion] AS [LedgerVersion], art.[ArtifactId], art.[State] AS [ArtifactState],
                r.[CheckedUtc]
            FROM [osdu].[InventoryRecord] AS r
            {{ledger}}
            WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND (r.[GoneUtc] IS NOT NULL OR r.[FirstSeenUtc] IS NULL)
                AND {{expected}}
            UNION ALL
            SELECT x.[ClaimedTargetId], CAST(0 AS bit), x.[FlowId], x.[DeliveryKey], x.[Status], x.[TargetVersion], NULL, NULL, NULL
            FROM [osdu].[Record] AS x
            WHERE @coversType = 1 AND x.[PartitionId] = @partitionId AND x.[ClaimedTargetId] LIKE @prefix ESCAPE N'\'
                AND x.[TargetVersion] IS NOT NULL AND x.[Status] IN (N'delivered', N'reverted')
                AND NOT EXISTS (SELECT 1 FROM [osdu].[InventoryRecord] AS r WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = x.[ClaimedTargetId])
            UNION ALL
            SELECT a.[TargetId], CAST(0 AS bit), a.[FlowId], a.[DeliveryKey], NULL, NULL, a.[ArtifactId], a.[State], NULL
            FROM [osdu].[Artifact] AS a
            WHERE @coversType = 1 AND a.[PartitionId] = @partitionId AND a.[TargetId] LIKE @prefix ESCAPE N'\' AND a.[State] = N'live'
                AND NOT EXISTS (SELECT 1 FROM [osdu].[Record] AS x WHERE x.[ClaimedTargetId] = a.[TargetId])
                AND NOT EXISTS (SELECT 1 FROM [osdu].[InventoryRecord] AS r WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = a.[TargetId])
        ) AS u
        ORDER BY CASE WHEN u.[CheckedUtc] IS NULL THEN 0 ELSE 1 END, u.[CheckedUtc], u.[Known] DESC, u.[TargetId];
        """;

    private const string InventoryCheckedStageSql = """
        CREATE TABLE #Checked (
            [TargetId] nvarchar(500) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            [Held] bit NOT NULL,
            [LedgerFlowId] uniqueidentifier NULL, [DeliveryKey] uniqueidentifier NULL, [LedgerStatus] nvarchar(16) NULL, [LedgerVersion] bigint NULL,
            [ArtifactId] bigint NULL, [ArtifactState] nvarchar(16) NULL,
            [Kind] nvarchar(300) NULL, [Version] bigint NULL, [CreateUser] nvarchar(256) NULL, [CreateTime] datetime2 NULL,
            [ModifyUser] nvarchar(256) NULL, [ModifyTime] datetime2 NULL);
        """;

    // The ids OSDU no longer serves: gone unless storage was asked, and then missing (storage does not hold it) or unlisted (it
    // does, and the read did not list it). An id a ledger expects that the inventory held no row of gets one, never listed. An
    // id a ledger still expects that this build did not ask for (more were expected than maxMissingChecks) keeps what storage
    // answered when it was last asked; one never asked is gone, saying it was not asked, until a later build asks. With no
    // check at all (@checks = 0) only maxMissingChecks: 0 leaves an expected id unasked, and every id not listed is gone.
    private const string InventoryCheckedSql = """
        UPDATE r SET
            [FindingUtc] = CASE WHEN r.[Finding] = N'gone' THEN r.[FindingUtc] ELSE @now END,
            [Finding] = N'gone', [Detail] = d.[Detail]
        FROM [osdu].[InventoryRecord] AS r
        {{ledger}}
        CROSS APPLY (SELECT CASE
                WHEN NOT {{expected}} THEN N'no ledger expects it'
                WHEN @checks = 0 THEN N'a ledger expects it, and the flow asks storage for no id (maxMissingChecks: 0), so whether it is missing is not known'
                ELSE N'a ledger expects it, and this build did not ask storage for it: more ids were expected than maxMissingChecks, and a later build asks'
            END AS [Detail]) AS d
        WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND (r.[GoneUtc] IS NOT NULL OR r.[FirstSeenUtc] IS NULL)
            AND NOT EXISTS (SELECT 1 FROM #Checked AS c WHERE c.[TargetId] = r.[TargetId])
            AND NOT (@checks > 0 AND r.[CheckedUtc] IS NOT NULL AND r.[Finding] IN (N'missing', N'unlisted') AND {{expected}})
            AND (r.[Finding] <> N'gone' OR r.[Detail] IS NULL OR r.[Detail] <> d.[Detail]);

        UPDATE r SET
            [CheckedUtc] = @now,
            [FindingUtc] = CASE WHEN r.[Finding] = (CASE WHEN c.[Held] = 1 THEN N'unlisted' ELSE N'missing' END) THEN r.[FindingUtc] ELSE @now END,
            [Finding] = CASE WHEN c.[Held] = 1 THEN N'unlisted' ELSE N'missing' END,
            [LedgerFlowId] = c.[LedgerFlowId], [DeliveryKey] = c.[DeliveryKey], [LedgerStatus] = c.[LedgerStatus], [LedgerVersion] = c.[LedgerVersion],
            [ArtifactId] = c.[ArtifactId], [ArtifactState] = c.[ArtifactState],
            [Detail] = CASE WHEN c.[Held] = 1 THEN N'storage holds it, and the read did not list it: the index has not caught up, or it is outside the inventory''s query'
                ELSE N'a ledger expects it, and storage does not hold it' END
        FROM [osdu].[InventoryRecord] AS r
        INNER JOIN #Checked AS c ON r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = c.[TargetId];

        INSERT INTO [osdu].[InventoryRecord] ([PartitionId], [InventoryId], [TargetId], [Kind], [Version], [CreateUser], [CreateTime],
                [ModifyUser], [ModifyTime], [Finding], [FindingUtc], [CheckedUtc], [LedgerFlowId], [DeliveryKey], [LedgerStatus], [LedgerVersion],
                [ArtifactId], [ArtifactState], [Detail])
        SELECT @partitionId, @inventoryId, c.[TargetId], c.[Kind], c.[Version], c.[CreateUser], c.[CreateTime], c.[ModifyUser], c.[ModifyTime],
            CASE WHEN c.[Held] = 1 THEN N'unlisted' ELSE N'missing' END, @now, @now, c.[LedgerFlowId], c.[DeliveryKey], c.[LedgerStatus], c.[LedgerVersion],
            c.[ArtifactId], c.[ArtifactState],
            CASE WHEN c.[Held] = 1 THEN N'storage holds it, and the read never listed it: the index has not caught up with it'
                ELSE N'a ledger expects it, and storage does not hold it' END
        FROM #Checked AS c
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[InventoryRecord] AS r
            WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = c.[TargetId]);
        DROP TABLE #Checked;
        """;

    private const string InventoryVersionsStageSql = """
        CREATE TABLE #Versions ([InventoryRecordId] bigint NOT NULL, [Version] bigint NOT NULL, PRIMARY KEY ([InventoryRecordId], [Version]));
        CREATE TABLE #VersionsAt ([InventoryRecordId] bigint NOT NULL PRIMARY KEY, [At] bigint NOT NULL);
        """;

    private const string InventoryVersionsSql = """
        DELETE v FROM [osdu].[InventoryVersion] AS v
        INNER JOIN #VersionsAt AS t ON v.[PartitionId] = @partitionId AND v.[InventoryRecordId] = t.[InventoryRecordId];
        INSERT INTO [osdu].[InventoryVersion] ([PartitionId], [InventoryRecordId], [Version])
        SELECT @partitionId, v.[InventoryRecordId], v.[Version] FROM #Versions AS v;
        UPDATE r SET [VersionsAt] = t.[At]
        FROM [osdu].[InventoryRecord] AS r
        INNER JOIN #VersionsAt AS t ON r.[PartitionId] = @partitionId AND r.[InventoryRecordId] = t.[InventoryRecordId];
        DROP TABLE #Versions;
        DROP TABLE #VersionsAt;
        """;

    /// <summary>Stages one chunk of a build's read, in its own transaction: nothing of the inventory changes until the merge.</summary>
    public static Task<int> AppendInventoryScanAsync(OsduDbContext db, short partitionId, long runId, IReadOnlyList<InventoryScanRow> rows, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            using var table = new DataTable();
            table.Columns.Add("PartitionId", typeof(short));
            table.Columns.Add("InventoryRunId", typeof(long));
            table.Columns.Add("TargetId", typeof(string));
            table.Columns.Add("Kind", typeof(string));
            table.Columns.Add("Version", typeof(long));
            table.Columns.Add("CreateUser", typeof(string));
            table.Columns.Add("CreateTime", typeof(DateTime));
            table.Columns.Add("ModifyUser", typeof(string));
            table.Columns.Add("ModifyTime", typeof(DateTime));
            foreach (var row in rows)
            {
                table.Rows.Add(
                    partitionId, runId, row.TargetId, Value(Truncate(row.Kind, DeliveryModel.MaxInventoryKindLength)), Value(row.Version),
                    Value(Truncate(row.CreateUser, DeliveryModel.MaxInventoryUserLength)), Value(row.CreateTime),
                    Value(Truncate(row.ModifyUser, DeliveryModel.MaxInventoryUserLength)), Value(row.ModifyTime));
            }

            await BulkCopyAsync(connection, transaction, "[osdu].[InventoryScan]", table, ct).ConfigureAwait(false);
            return rows.Count;
        }, ct);

    /// <summary>Merges a build's whole read into its inventory, in one transaction held against every other merge of the inventory.</summary>
    public static Task<InventoryMerge> MergeInventoryAsync(OsduDbContext db, short partitionId, int inventoryId, long runId, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, InventoryMergeSql, slice: null);
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = $"osdu-inventory-{partitionId}-{inventoryId}" });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@runId", SqlDbType.BigInt) { Value = runId });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("The merge of the inventory's read answered nothing.");
            }

            return new InventoryMerge(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        }, ct);

    /// <summary>The identities that created ids of the inventory a ledger claims, the most prolific first.</summary>
    public static Task<IReadOnlyList<InventoryOwner>> InventoryOwnersAsync(OsduDbContext db, short partitionId, int inventoryId, int max, CancellationToken ct)
        => InTransactionAsync<IReadOnlyList<InventoryOwner>>(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, InventoryOwnersSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = max });
            var owners = new List<InventoryOwner>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                owners.Add(new InventoryOwner(reader.GetString(0), reader.GetInt64(1)));
            }

            return owners;
        }, ct);

    /// <summary>Sets the finding of every id of the inventory OSDU serves, comparing it with the ledgers of the partition.</summary>
    public static Task<int> ReconcileInventoryAsync(OsduDbContext db, short partitionId, int inventoryId, IReadOnlyList<string> owners, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, InventorySql(InventoryReconcileSql), slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@owners", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(owners) });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// The ids a ledger expects that the inventory's read did not list, at most <paramref name="max"/>, never asked of storage
    /// first and then the longest ago: its own rows no longer listed, and, with <paramref name="prefix"/> (an inventory that
    /// covers its entity type whole), the ledgers' ids of the type it holds no row of.
    /// </summary>
    public static Task<IReadOnlyList<InventoryCandidate>> InventoryCandidatesAsync(OsduDbContext db, short partitionId, int inventoryId, string? prefix, int max, CancellationToken ct)
        => InTransactionAsync<IReadOnlyList<InventoryCandidate>>(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, InventorySql(InventoryCandidatesSql), slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = max });
            command.Parameters.Add(new SqlParameter("@coversType", SqlDbType.Bit) { Value = prefix is not null });
            command.Parameters.Add(new SqlParameter("@prefix", SqlDbType.NVarChar, 600) { Value = prefix is null ? string.Empty : LikePrefix(prefix) });
            var candidates = new List<InventoryCandidate>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add(new InventoryCandidate(
                    reader.GetString(0),
                    reader.GetBoolean(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            return candidates;
        }, ct);

    /// <summary>
    /// Settles the ids OSDU no longer serves (gone), and what storage answered for the ids a ledger expects (missing or
    /// unlisted), in one transaction.
    /// </summary>
    public static Task<int> RecordInventoryChecksAsync(OsduDbContext db, short partitionId, int inventoryId, IReadOnlyList<InventoryCheck> checks, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, InventoryCheckedStageSql, ct).ConfigureAwait(false);
            if (checks.Count > 0)
            {
                using var table = new DataTable();
                foreach (var (name, type) in new (string, Type)[]
                {
                    ("TargetId", typeof(string)), ("Held", typeof(bool)), ("LedgerFlowId", typeof(Guid)), ("DeliveryKey", typeof(Guid)), ("LedgerStatus", typeof(string)),
                    ("LedgerVersion", typeof(long)), ("ArtifactId", typeof(long)), ("ArtifactState", typeof(string)), ("Kind", typeof(string)), ("Version", typeof(long)),
                    ("CreateUser", typeof(string)), ("CreateTime", typeof(DateTime)), ("ModifyUser", typeof(string)), ("ModifyTime", typeof(DateTime)),
                })
                {
                    table.Columns.Add(name, type);
                }

                foreach (var check in checks.GroupBy(c => c.Candidate.TargetId, StringComparer.Ordinal).Select(g => g.First()))
                {
                    var c = check.Candidate;
                    var h = check.Headers;
                    table.Rows.Add(
                        c.TargetId, check.Held, Value(c.LedgerFlowId), Value(c.DeliveryKey), Value(c.LedgerStatus), Value(c.LedgerVersion), Value(c.ArtifactId), Value(c.ArtifactState),
                        Value(Truncate(h?.Kind, DeliveryModel.MaxInventoryKindLength)), Value(h?.Version), Value(Truncate(h?.CreateUser, DeliveryModel.MaxInventoryUserLength)), Value(h?.CreateTime),
                        Value(Truncate(h?.ModifyUser, DeliveryModel.MaxInventoryUserLength)), Value(h?.ModifyTime));
                }

                await BulkCopyAsync(connection, transaction, "#Checked", table, ct).ConfigureAwait(false);
            }

            await using var command = Command(connection, transaction, InventorySql(InventoryCheckedSql), slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@checks", SqlDbType.Int) { Value = checks.Count });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    /// <summary>Replaces the versions kept of each record read, and notes the latest version they were read at.</summary>
    public static Task<int> WriteInventoryVersionsAsync(OsduDbContext db, short partitionId, IReadOnlyList<InventoryVersionsRead> reads, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, InventoryVersionsStageSql, ct).ConfigureAwait(false);
            using (var versions = new DataTable())
            {
                versions.Columns.Add("InventoryRecordId", typeof(long));
                versions.Columns.Add("Version", typeof(long));
                foreach (var read in reads)
                {
                    foreach (var version in read.Versions.Distinct())
                    {
                        versions.Rows.Add(read.InventoryRecordId, version);
                    }
                }

                await BulkCopyAsync(connection, transaction, "#Versions", versions, ct).ConfigureAwait(false);
            }

            using (var at = new DataTable())
            {
                at.Columns.Add("InventoryRecordId", typeof(long));
                at.Columns.Add("At", typeof(long));
                foreach (var read in reads.GroupBy(r => r.InventoryRecordId).Select(g => g.Last()))
                {
                    at.Rows.Add(read.InventoryRecordId, read.At);
                }

                await BulkCopyAsync(connection, transaction, "#VersionsAt", at, ct).ConfigureAwait(false);
            }

            await using var command = Command(connection, transaction, InventoryVersionsSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// An inventory statement with what the ledgers hold of each id, the rule its finding is set by, and whether a ledger
    /// expects it, written in.
    /// </summary>
    private static string InventorySql(string sql)
        => sql.Replace("{{ledger}}", InventoryLedgerApply, StringComparison.Ordinal)
            .Replace("{{finding}}", InventoryFindingCase, StringComparison.Ordinal)
            .Replace("{{expected}}", InventoryExpected, StringComparison.Ordinal);

    /// <summary>A LIKE pattern matching every id that starts with <paramref name="prefix"/>, its wildcards escaped.</summary>
    private static string LikePrefix(string prefix)
        => prefix.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "%";
}
