using System.Data;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Selecting an inventory's ids by what OSDU holds (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU holds):
// what the search found for one page or chunk of a match, matched with the inventory's own rows by the OSDU id, which both
// keep in a binary collation so they compare exactly as OSDU compares them.
internal static partial class SqlServerLedgerBulk
{
    private const string SelectionHitsStageSql = """
        CREATE TABLE #Hits (
            [TargetId] nvarchar(500) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Version] bigint NULL,
            [ValuesJson] nvarchar(4000) NULL);
        """;

    // The records the search found that the inventory serves a row of, each once (the highest version a repeat names), kept
    // unless the selection holds the row already; the records it serves no row of are counted, as are the rows matched at
    // another version than the inventory listed; and the match's counts and progress are added to.
    private const string SelectionHitsSql = """
        DECLARE @matched bigint, @changed bigint, @outside bigint;

        WITH [h] AS (
            SELECT [TargetId], [Version], [ValuesJson],
                ROW_NUMBER() OVER (PARTITION BY [TargetId] ORDER BY [Version] DESC) AS [n]
            FROM #Hits)
        SELECT r.[InventoryRecordId], h.[Version], h.[ValuesJson], r.[Version] AS [Listed]
        INTO #Matched
        FROM [h] AS h
        INNER JOIN [osdu].[InventoryRecord] AS r
            ON r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = h.[TargetId]
        WHERE h.[n] = 1 AND r.[GoneUtc] IS NULL AND r.[FirstSeenUtc] IS NOT NULL
            AND NOT EXISTS (
                SELECT 1 FROM [osdu].[InventorySelectionItem] AS s
                WHERE s.[PartitionId] = @partitionId AND s.[InventorySelectionId] = @selectionId AND s.[InventoryRecordId] = r.[InventoryRecordId]);

        INSERT INTO [osdu].[InventorySelectionItem] ([PartitionId], [InventorySelectionId], [InventoryRecordId], [MatchedVersion], [ValuesJson])
        SELECT @partitionId, @selectionId, m.[InventoryRecordId], m.[Version], m.[ValuesJson]
        FROM #Matched AS m;
        SET @matched = @@ROWCOUNT;

        SELECT @changed = COUNT_BIG(*) FROM #Matched AS m
        WHERE m.[Version] IS NOT NULL AND m.[Listed] IS NOT NULL AND m.[Version] <> m.[Listed];

        SELECT @outside = COUNT_BIG(DISTINCT h.[TargetId]) FROM #Hits AS h
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[InventoryRecord] AS r
            WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = h.[TargetId]
                AND r.[GoneUtc] IS NULL AND r.[FirstSeenUtc] IS NOT NULL);

        UPDATE [osdu].[InventorySelection] SET
            [Matched] = [Matched] + @matched, [Changed] = [Changed] + @changed, [Outside] = [Outside] + @outside,
            [Checked] = [Checked] + @checked, [Requests] = [Requests] + @requests
        WHERE [PartitionId] = @partitionId AND [InventorySelectionId] = @selectionId;

        SELECT @matched, @outside, @changed;
        DROP TABLE #Matched;
        DROP TABLE #Hits;
        """;

    // A page that found nothing still moves the match on.
    private const string SelectionProgressSql = """
        UPDATE [osdu].[InventorySelection] SET [Checked] = [Checked] + @checked, [Requests] = [Requests] + @requests
        WHERE [PartitionId] = @partitionId AND [InventorySelectionId] = @selectionId;
        """;

    /// <summary>
    /// Keeps what one page or chunk of a match found, in one transaction: the inventory's served rows the hits name, once each,
    /// with the version the index holds and their values; how many hits name no served row of the inventory, and how many
    /// rows matched at another version than it listed; and the match's progress.
    /// </summary>
    public static Task<InventorySelectionWrite> AddInventorySelectionItemsAsync(
        OsduDbContext db, short partitionId, int inventoryId, long selectionId, IReadOnlyList<InventorySelectionHit> hits, long checkedCount, long requests,
        CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            if (hits.Count == 0)
            {
                await using var progress = Command(connection, transaction, SelectionProgressSql, slice: null);
                AddSelectionParameters(progress, partitionId, inventoryId, selectionId, checkedCount, requests);
                await progress.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return new InventorySelectionWrite(0, 0, 0);
            }

            await ExecuteAsync(connection, transaction, SelectionHitsStageSql, ct).ConfigureAwait(false);
            using (var table = new DataTable())
            {
                table.Columns.Add("TargetId", typeof(string));
                table.Columns.Add("Version", typeof(long));
                table.Columns.Add("ValuesJson", typeof(string));
                foreach (var hit in hits)
                {
                    table.Rows.Add(hit.TargetId, Value(hit.Version), Value(hit.ValuesJson));
                }

                await BulkCopyAsync(connection, transaction, "#Hits", table, ct).ConfigureAwait(false);
            }

            await using var command = Command(connection, transaction, SelectionHitsSql, slice: null);
            AddSelectionParameters(command, partitionId, inventoryId, selectionId, checkedCount, requests);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("A write of the match answered nothing.");
            }

            return new InventorySelectionWrite(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }, ct);

    private static void AddSelectionParameters(SqlCommand command, short partitionId, int inventoryId, long selectionId, long checkedCount, long requests)
    {
        command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
        command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
        command.Parameters.Add(new SqlParameter("@selectionId", SqlDbType.BigInt) { Value = selectionId });
        command.Parameters.Add(new SqlParameter("@checked", SqlDbType.BigInt) { Value = checkedCount });
        command.Parameters.Add(new SqlParameter("@requests", SqlDbType.BigInt) { Value = requests });
    }
}
