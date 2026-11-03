using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Restoring an inventory's gone ids (osdu/docs/reference/flow/inventory-removals.md, Restoring gone ids): the ids a restore may
// bring back, each with what the inventory holds of it, what the ledgers of the partition hold of it now and the finding the
// reconcile's own rule gives it once OSDU serves it again, read a round at a time; and what a round came to, written with the
// inventory rows it serves again in one transaction.
internal static partial class SqlServerLedgerBulk
{
    // The inventory's gone ids (or, for a restore that names its ids, those ids whatever they are now), in order after the last
    // one read: every one is a row the inventory holds.
    private const string InventoryRestoreRowsSql = """
        SELECT TOP (@limit) x.[InventoryRecordId] AS [Order], x.[InventoryRecordId], x.[TargetId], x.[Version], x.[CreateUser],
            CAST(1 AS bit) AS [Held], x.[Finding]
        FROM [osdu].[InventoryRecord] AS x
        WHERE x.[PartitionId] = @partitionId AND x.[InventoryId] = @inventoryId AND x.[InventoryRecordId] > @after AND {{named}}
        ORDER BY x.[InventoryRecordId]
        """;

    // The ids a soft-delete removal removed, in the order it reached them after the last one read, each with the inventory's
    // row of it when it still holds one: a removal of gone ids may have taken the row since, and a later build may have listed
    // the id again as a new one, so the row is found by the id rather than by the number the removal kept.
    private const string InventoryRestoreRemovedSql = """
        SELECT TOP (@limit) i.[InventoryRemovalItemId] AS [Order], COALESCE(x.[InventoryRecordId], i.[InventoryRecordId]) AS [InventoryRecordId],
            i.[TargetId], COALESCE(x.[Version], i.[Version]) AS [Version], x.[CreateUser],
            CAST(CASE WHEN x.[InventoryRecordId] IS NULL THEN 0 ELSE 1 END AS bit) AS [Held], x.[Finding]
        FROM [osdu].[InventoryRemovalItem] AS i
        LEFT JOIN [osdu].[InventoryRecord] AS x
            ON x.[PartitionId] = @partitionId AND x.[InventoryId] = @inventoryId AND x.[TargetId] = i.[TargetId]
        WHERE i.[PartitionId] = @partitionId AND i.[InventoryRemovalId] = @removalId AND i.[Outcome] = N'removed' AND i.[InventoryRemovalItemId] > @after
        ORDER BY i.[InventoryRemovalItemId]
        """;

    // A round's candidates (r, the rows above), each with what the ledgers hold of it now and the finding it takes once served.
    private const string InventoryRestoreCandidatesSql = """
        SELECT r.[Order], r.[InventoryRecordId], r.[TargetId], r.[Version], r.[CreateUser], r.[Held], r.[Finding],
            {{finding}} AS [Current],
            COALESCE(rec.[FlowId], art.[FlowId], pur.[FlowId]) AS [LedgerFlowId],
            COALESCE(rec.[DeliveryKey], art.[DeliveryKey], pur.[DeliveryKey]) AS [DeliveryKey],
            rec.[Status] AS [LedgerStatus], art.[ArtifactId], art.[State] AS [ArtifactState]
        FROM ({{rows}}) AS r
        {{ledger}}
        ORDER BY r.[Order];
        """;

    // A round's outcomes kept, the ids it brought back served again in the inventory at once with the finding the reconcile's
    // rule gives them, so its counts say what OSDU serves without another build, and the restore's tallies added to. Only a row
    // still gone is served again: one a build listed since keeps what that build and its reconcile made of it. The rows are
    // found from the round's own ids, each a seek on the inventory's id, however many gone ids the inventory holds.
    private const string InventoryRestoredSql = """
        INSERT INTO [osdu].[InventoryRemovalItem] ([PartitionId], [InventoryRemovalId], [InventoryRecordId], [TargetId], [Version], [Finding],
                [Outcome], [Reason], [LedgerFlowId], [DeliveryKey], [RecordedUtc])
        SELECT @partitionId, @removalId, x.[InventoryRecordId], x.[TargetId], x.[Version], x.[Finding], x.[Outcome], x.[Reason],
            x.[LedgerFlowId], x.[DeliveryKey], @now
        FROM #Removed AS x;

        SELECT r.[InventoryRecordId], {{finding}} AS [Finding],
            COALESCE(rec.[FlowId], art.[FlowId], pur.[FlowId]) AS [LedgerFlowId],
            COALESCE(rec.[DeliveryKey], art.[DeliveryKey], pur.[DeliveryKey]) AS [DeliveryKey],
            rec.[Status] AS [LedgerStatus], rec.[TargetVersion] AS [LedgerVersion], art.[ArtifactId], art.[State] AS [ArtifactState]
        INTO #Served
        FROM #Removed AS x
        INNER JOIN [osdu].[InventoryRecord] AS r
            ON r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = x.[TargetId]
        {{ledger}}
        WHERE x.[Outcome] = N'restored' AND r.[Finding] = N'gone';

        UPDATE r SET
            [GoneUtc] = NULL, [FirstSeenUtc] = COALESCE(r.[FirstSeenUtc], @now), [ChangedUtc] = @now, [FindingUtc] = @now,
            [Finding] = s.[Finding], [LedgerFlowId] = s.[LedgerFlowId], [DeliveryKey] = s.[DeliveryKey], [LedgerStatus] = s.[LedgerStatus],
            [LedgerVersion] = s.[LedgerVersion], [ArtifactId] = s.[ArtifactId], [ArtifactState] = s.[ArtifactState], [Detail] = @restoredDetail
        FROM [osdu].[InventoryRecord] AS r
        INNER JOIN #Served AS s ON r.[PartitionId] = @partitionId AND r.[InventoryRecordId] = s.[InventoryRecordId];

        UPDATE [osdu].[InventoryRemoval] SET
            [Restored] = [Restored] + @restored, [Gone] = [Gone] + @gone, [Skipped] = [Skipped] + @skipped, [Failed] = [Failed] + @failed
        WHERE [PartitionId] = @partitionId AND [InventoryRemovalId] = @removalId;
        DROP TABLE #Served;
        DROP TABLE #Removed;
        """;

    /// <summary>
    /// The next ids a restore may bring back, at most <paramref name="limit"/>, after <paramref name="after"/>: the inventory's gone
    /// ids, or with <paramref name="ids"/> those ids whatever they are now, or with <paramref name="removalId"/> the ids that
    /// soft-delete removal removed; each with what the inventory and the ledgers hold of it now, and the finding the reconcile's
    /// rule gives it against them and <paramref name="owners"/> once OSDU serves it again.
    /// </summary>
    public static Task<IReadOnlyList<InventoryRestoreCandidate>> InventoryRestoreCandidatesAsync(
        OsduDbContext db, short partitionId, int inventoryId, IReadOnlyList<string>? ids, long? removalId, IReadOnlyList<string> owners, long after, int limit,
        CancellationToken ct)
        => InTransactionAsync<IReadOnlyList<InventoryRestoreCandidate>>(db, async (connection, transaction) =>
        {
            // The removal's ids, the ids named, or every gone id, written in rather than switched on a parameter, so each round
            // seeks its own index.
            var rows = removalId is not null
                ? InventoryRestoreRemovedSql
                : InventoryRestoreRowsSql.Replace(
                    "{{named}}",
                    ids is null ? "x.[Finding] = N'gone'" : "x.[TargetId] IN (SELECT i.[value] COLLATE Latin1_General_100_BIN2 FROM OPENJSON(@ids) AS i)",
                    StringComparison.Ordinal);
            var sql = InventorySql(InventoryRestoreCandidatesSql.Replace("{{rows}}", rows, StringComparison.Ordinal));
            await using var command = Command(connection, transaction, sql, slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@removalId", SqlDbType.BigInt) { Value = removalId ?? 0 });
            command.Parameters.Add(new SqlParameter("@ids", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(ids ?? []) });
            command.Parameters.Add(new SqlParameter("@owners", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(owners) });
            command.Parameters.Add(new SqlParameter("@after", SqlDbType.BigInt) { Value = after });
            command.Parameters.Add(new SqlParameter("@limit", SqlDbType.Int) { Value = limit });
            var candidates = new List<InventoryRestoreCandidate>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                candidates.Add(new InventoryRestoreCandidate(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetBoolean(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetGuid(8),
                    reader.IsDBNull(9) ? null : reader.GetGuid(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetInt64(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12)));
            }

            return candidates;
        }, ct);

    /// <summary>
    /// Keeps what one round of a restore came to: every id's outcome and reason, the ids brought back served again in the
    /// inventory (<paramref name="restoredDetail"/>) with the finding the reconcile's rule gives them against the ledgers and
    /// <paramref name="owners"/>, and the restore's tallies, in one transaction.
    /// </summary>
    public static Task<int> RecordInventoryRestoreAsync(
        OsduDbContext db, short partitionId, int inventoryId, long removalId, IReadOnlyList<InventoryRemovalItem> items, IReadOnlyList<string> owners, string restoredDetail,
        DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, InventoryRemovedStageSql, ct).ConfigureAwait(false);
            var kept = items.GroupBy(i => i.TargetId, StringComparer.Ordinal).Select(g => g.Last()).ToList();
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

                // The stage is keyed by the inventory row's number; a restore of a removal's ids keeps the number the removal
                // kept for an id the inventory no longer holds, which no other id of the round shares.
                foreach (var item in kept.GroupBy(i => i.InventoryRecordId).Select(g => g.Last()))
                {
                    table.Rows.Add(
                        item.InventoryRecordId, item.TargetId, Value(item.Version), item.Finding, item.Outcome,
                        Value(InventoryRemovals.Reason(item.Reason)), Value(item.LedgerFlowId), Value(item.DeliveryKey));
                }

                await BulkCopyAsync(connection, transaction, "#Removed", table, ct).ConfigureAwait(false);
            }

            var tally = InventoryRemovalTally.None.Add(kept.GroupBy(i => i.InventoryRecordId).Select(g => g.Last()));
            await using var command = Command(connection, transaction, InventorySql(InventoryRestoredSql), slice: null);
            command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            command.Parameters.Add(new SqlParameter("@removalId", SqlDbType.BigInt) { Value = removalId });
            command.Parameters.Add(new SqlParameter("@owners", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(owners) });
            command.Parameters.Add(new SqlParameter("@restoredDetail", SqlDbType.NVarChar, DeliveryModel.MaxInventoryDetailLength) { Value = Cut(restoredDetail) });
            command.Parameters.Add(new SqlParameter("@restored", SqlDbType.BigInt) { Value = tally.Restored });
            command.Parameters.Add(new SqlParameter("@gone", SqlDbType.BigInt) { Value = tally.Gone });
            command.Parameters.Add(new SqlParameter("@skipped", SqlDbType.BigInt) { Value = tally.Skipped });
            command.Parameters.Add(new SqlParameter("@failed", SqlDbType.BigInt) { Value = tally.Failed });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
}
