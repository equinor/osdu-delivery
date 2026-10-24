using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Removing what an inventory found (osdu/docs/reference/flow/inventory-removals.md, What a removal does): the ids a removal may
// act on, each with the finding the inventory recorded and the finding the reconcile's own rule gives it now, read a
// chunk at a time; and what a chunk came to, written with the inventory rows it changed in one transaction.
internal static partial class SqlServerLedgerBulk
{
    // The ids of one finding the inventory holds as served (or, for a removal that names its ids, those ids whatever they are
    // now), in order after the last one read, each with what the ledgers hold of it now and the finding that gives it.
    // A removal that takes its ids from a selection reads the finding's served ids the selection holds.
    private const string InventoryRemovalCandidatesSql = """
        SELECT TOP (@limit) r.[InventoryRecordId], r.[TargetId], r.[Version], r.[CreateUser],
            CASE WHEN r.[GoneUtc] IS NOT NULL OR r.[FirstSeenUtc] IS NULL THEN N'gone' ELSE r.[Finding] END AS [Recorded],
            {{finding}} AS [Current],
            COALESCE(rec.[FlowId], art.[FlowId], pur.[FlowId]) AS [LedgerFlowId],
            COALESCE(rec.[DeliveryKey], art.[DeliveryKey], pur.[DeliveryKey]) AS [DeliveryKey],
            rec.[Status] AS [LedgerStatus], art.[ArtifactId], art.[State] AS [ArtifactState]
        FROM [osdu].[InventoryRecord] AS r
        {{ledger}}
        WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[InventoryRecordId] > @after AND {{named}}
        ORDER BY r.[InventoryRecordId];
        """;

    private const string InventoryRemovedStageSql = """
        CREATE TABLE #Removed (
            [InventoryRecordId] bigint NOT NULL PRIMARY KEY,
            [TargetId] nvarchar(500) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Version] bigint NULL,
            [Finding] nvarchar(16) NOT NULL,
            [Outcome] nvarchar(16) NOT NULL,
            [Reason] nvarchar(1000) NULL,
            [LedgerFlowId] uniqueidentifier NULL,
            [DeliveryKey] uniqueidentifier NULL);
        """;

    // A chunk's outcomes kept, the ids it took out of OSDU (or found OSDU no longer served) marked gone in the inventory at once,
    // so its counts say what is left without another build, and the removal's tallies added to.
    private const string InventoryRemovedSql = """
        INSERT INTO [osdu].[InventoryRemovalItem] ([PartitionId], [InventoryRemovalId], [InventoryRecordId], [TargetId], [Version], [Finding],
                [Outcome], [Reason], [LedgerFlowId], [DeliveryKey], [RecordedUtc])
        SELECT @partitionId, @removalId, x.[InventoryRecordId], x.[TargetId], x.[Version], x.[Finding], x.[Outcome], x.[Reason],
            x.[LedgerFlowId], x.[DeliveryKey], @now
        FROM #Removed AS x;

        UPDATE r SET
            [GoneUtc] = COALESCE(r.[GoneUtc], @now),
            [FindingUtc] = CASE WHEN r.[Finding] = N'gone' THEN r.[FindingUtc] ELSE @now END,
            [Finding] = N'gone',
            [Detail] = CASE WHEN x.[Outcome] = N'removed' THEN @removedDetail ELSE @goneDetail END
        FROM [osdu].[InventoryRecord] AS r
        INNER JOIN #Removed AS x ON r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[InventoryRecordId] = x.[InventoryRecordId]
        WHERE x.[Outcome] IN (N'removed', N'gone');

        UPDATE [osdu].[InventoryRemoval] SET
            [Removed] = [Removed] + @removed, [Gone] = [Gone] + @gone, [Skipped] = [Skipped] + @skipped, [Failed] = [Failed] + @failed
        WHERE [PartitionId] = @partitionId AND [InventoryRemovalId] = @removalId;
        DROP TABLE #Removed;
        """;

    // One round of a removal of gone ids from the inventory alone, under the lock a build's merge holds, so a round and a merge
    // go one after the other: the next ids it names (every gone id, or the ids picked, whatever they are now) after the last one
    // read, each kept with why where it is not gone now, OSDU serves it again (its last build listed it), or a ledger expects
    // it (a later build asks storage whether it is missing); the rest removed with the versions kept of them, the ids kept
    // recorded as skipped, and the removal's tallies added to. The ids removed keep no row: keeping one would keep what goes.
    private const string InventoryForgetSql = """
        DECLARE @lock int;
        EXEC @lock = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 600000;
        IF @lock < 0 THROW 51000, N'A build of the inventory held it for ten minutes; the removal took nothing more from it.', 1;

        SELECT TOP (@limit) r.[InventoryRecordId], r.[TargetId], r.[Version], r.[Finding],
            CASE
                WHEN r.[Finding] <> N'gone' THEN CONCAT(N'the inventory finds it ', r.[Finding], N' now, not gone, so it stays')
                WHEN r.[GoneUtc] IS NULL AND r.[FirstSeenUtc] IS NOT NULL THEN N'OSDU serves it again: the inventory''s last build listed it, so it stays'
                WHEN {{expected}} THEN N'a ledger expects it, so it stays: a later build asks storage whether it is missing'
                ELSE NULL
            END AS [Kept],
            COALESCE(rec.[FlowId], art.[FlowId], pur.[FlowId]) AS [LedgerFlowId],
            COALESCE(rec.[DeliveryKey], art.[DeliveryKey], pur.[DeliveryKey]) AS [DeliveryKey]
        INTO #Forget
        FROM [osdu].[InventoryRecord] AS r
        {{ledger}}
        WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[InventoryRecordId] > @after AND {{named}}
        ORDER BY r.[InventoryRecordId];

        DECLARE @removed bigint, @skipped bigint;
        DELETE v FROM [osdu].[InventoryVersion] AS v
        INNER JOIN #Forget AS f ON v.[PartitionId] = @partitionId AND v.[InventoryRecordId] = f.[InventoryRecordId]
        WHERE f.[Kept] IS NULL;

        DELETE r FROM [osdu].[InventoryRecord] AS r
        INNER JOIN #Forget AS f ON r.[PartitionId] = @partitionId AND r.[InventoryRecordId] = f.[InventoryRecordId]
        WHERE f.[Kept] IS NULL;
        SET @removed = @@ROWCOUNT;

        INSERT INTO [osdu].[InventoryRemovalItem] ([PartitionId], [InventoryRemovalId], [InventoryRecordId], [TargetId], [Version], [Finding],
                [Outcome], [Reason], [LedgerFlowId], [DeliveryKey], [RecordedUtc])
        SELECT @partitionId, @removalId, f.[InventoryRecordId], f.[TargetId], f.[Version], f.[Finding], N'skipped', f.[Kept],
            f.[LedgerFlowId], f.[DeliveryKey], @now
        FROM #Forget AS f
        WHERE f.[Kept] IS NOT NULL;
        SET @skipped = @@ROWCOUNT;

        UPDATE [osdu].[InventoryRemoval] SET [Removed] = [Removed] + @removed, [Skipped] = [Skipped] + @skipped
        WHERE [PartitionId] = @partitionId AND [InventoryRemovalId] = @removalId;

        SELECT COUNT(*), COALESCE(MAX(f.[InventoryRecordId]), @after), @removed, @skipped FROM #Forget AS f;
        DROP TABLE #Forget;
        """;

    /// <summary>
    /// One round of a removal of gone ids from the inventory alone, in one transaction held against the inventory's merge: the
    /// next <paramref name="limit"/> gone ids after <paramref name="after"/> (or of <paramref name="ids"/>, whatever they are now)
    /// removed with their versions, except those that are not gone now, that OSDU serves again, or that a ledger expects, which
    /// are kept and recorded as skipped with why; and the removal's tallies added to.
    /// </summary>
    public static Task<InventoryRemovalRound> RemoveFromInventoryAsync(
        OsduDbContext db, short partitionId, int inventoryId, long removalId, IReadOnlyList<string>? ids, long after, int limit, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            // The named ids or the finding, written in rather than switched on a parameter, so each round seeks its own index.
            var named = ids is null
                ? "r.[Finding] = N'gone'"
                : "r.[TargetId] IN (SELECT i.[value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@ids) AS i)";
            var sql = InventorySql(InventoryForgetSql).Replace("{{named}}", named, StringComparison.Ordinal);
            await using var command = Command(connection, transaction, sql, slice: null);
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = $"osdu-inventory-{partitionId}-{inventoryId}" });
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@removalId", SqlDbType.BigInt) { Value = removalId });
            command.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(ids ?? []) });
            command.Parameters.Add(new SqlParameter("@after", SqlDbType.BigInt) { Value = after });
            command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = limit });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("A round of the removal from the inventory answered nothing.");
            }

            return new InventoryRemovalRound(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }, ct);

    /// <summary>
    /// The next ids a removal may act on, at most <paramref name="limit"/>, after <paramref name="after"/>: those of
    /// <paramref name="finding"/> the inventory holds as served (only those <paramref name="selectionId"/> holds, when it names a
    /// selection), or with <paramref name="ids"/> those ids whatever they are now; each with the finding the reconcile's rule gives
    /// it against the ledgers as they stand and <paramref name="owners"/>.
    /// </summary>
    public static Task<IReadOnlyList<InventoryRemovalCandidate>> InventoryRemovalCandidatesAsync(
        OsduDbContext db, short partitionId, int inventoryId, string finding, IReadOnlyList<string>? ids, long? selectionId, IReadOnlyList<string> owners, long after,
        int limit, CancellationToken ct)
        => InTransactionAsync<IReadOnlyList<InventoryRemovalCandidate>>(db, async (connection, transaction) =>
        {
            // The ids named, the selection's, or every served id of the finding, written in rather than switched on a parameter,
            // so each chunk seeks its own index.
            const string served = "r.[Finding] = @finding AND r.[GoneUtc] IS NULL AND r.[FirstSeenUtc] IS NOT NULL";
            var named = ids is not null
                ? "r.[TargetId] IN (SELECT i.[value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@ids) AS i)"
                : selectionId is not null
                    ? served + " AND EXISTS (SELECT 1 FROM [osdu].[InventorySelectionItem] AS s WHERE s.[PartitionId] = @partitionId AND s.[InventorySelectionId] = @selectionId AND s.[InventoryRecordId] = r.[InventoryRecordId])"
                    : served;
            var sql = InventorySql(InventoryRemovalCandidatesSql).Replace("{{named}}", named, StringComparison.Ordinal);
            await using var command = Command(connection, transaction, sql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@finding", SqlDbType.NVarChar, 16) { Value = finding });
            command.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(ids ?? []) });
            command.Parameters.Add(new SqlParameter("@selectionId", SqlDbType.BigInt) { Value = selectionId ?? 0 });
            command.Parameters.Add(new SqlParameter("@owners", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(owners) });
            command.Parameters.Add(new SqlParameter("@after", SqlDbType.BigInt) { Value = after });
            command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = limit });
            var candidates = new List<InventoryRemovalCandidate>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add(new InventoryRemovalCandidate(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetGuid(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }

            return candidates;
        }, ct);

    /// <summary>
    /// Keeps what one chunk of a removal came to: every id's outcome and reason, the ids removed (or found gone) marked gone in
    /// the inventory with what removed them, and the removal's tallies, in one transaction.
    /// </summary>
    public static Task<int> RecordInventoryRemovalAsync(
        OsduDbContext db, short partitionId, int inventoryId, long removalId, IReadOnlyList<InventoryRemovalItem> items, string removedDetail, string goneDetail, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, InventoryRemovedStageSql, ct).ConfigureAwait(false);
            using (var table = new DataTable())
            {
                foreach (var (name, type) in new (string, Type)[]
                {
                    ("InventoryRecordId", typeof(long)), ("TargetId", typeof(string)), ("Version", typeof(long)), ("Finding", typeof(string)),
                    ("Outcome", typeof(string)), ("Reason", typeof(string)), ("LedgerFlowId", typeof(Guid)), ("DeliveryKey", typeof(Guid)),
                })
                {
                    table.Columns.Add(name, type);
                }

                foreach (var item in items.GroupBy(i => i.InventoryRecordId).Select(g => g.Last()))
                {
                    table.Rows.Add(
                        item.InventoryRecordId, item.TargetId, Value(item.Version), item.Finding, item.Outcome,
                        Value(InventoryRemovals.Reason(item.Reason)), Value(item.LedgerFlowId), Value(item.DeliveryKey));
                }

                await BulkCopyAsync(connection, transaction, "#Removed", table, ct).ConfigureAwait(false);
            }

            var tally = InventoryRemovalTally.None.Add(items.GroupBy(i => i.InventoryRecordId).Select(g => g.Last()));
            await using var command = Command(connection, transaction, InventoryRemovedSql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@removalId", SqlDbType.BigInt) { Value = removalId });
            command.Parameters.Add(new SqlParameter("@removedDetail", SqlDbType.NVarChar, DeliveryModel.MaxInventoryDetailLength) { Value = Cut(removedDetail) });
            command.Parameters.Add(new SqlParameter("@goneDetail", SqlDbType.NVarChar, DeliveryModel.MaxInventoryDetailLength) { Value = Cut(goneDetail) });
            command.Parameters.Add(new SqlParameter("@removed", SqlDbType.BigInt) { Value = tally.Removed });
            command.Parameters.Add(new SqlParameter("@gone", SqlDbType.BigInt) { Value = tally.Gone });
            command.Parameters.Add(new SqlParameter("@skipped", SqlDbType.BigInt) { Value = tally.Skipped });
            command.Parameters.Add(new SqlParameter("@failed", SqlDbType.BigInt) { Value = tally.Failed });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    private static string Cut(string detail) => detail.Length <= DeliveryModel.MaxInventoryDetailLength ? detail : detail[..DeliveryModel.MaxInventoryDetailLength];
}
