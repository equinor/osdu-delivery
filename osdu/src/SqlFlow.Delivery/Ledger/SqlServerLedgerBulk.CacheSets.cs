using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>One round of a render-context sweep: the sets and values it removed, and the last set it looked at.</summary>
internal readonly record struct CacheSetSweepRound(int Sets, int Entries, long? Last);

/// <summary>
/// The statement of a render-context sweep (<see cref="CacheSetKeeping"/>). A round looks at the next sets past a cursor,
/// in key order, and removes those no record points at and no render has used since the cut-off, each with its values, in
/// one short transaction.
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    // The sets are picked first and judged again as they are deleted, under the delete's own locks: a render that marked a
    // picked set used in between (EnsureCacheSetAsync) keeps it, and a set the delete took is one no render can hand out,
    // since a render marks a set before it hands it out and finds a removed one gone. The set's row goes before its values
    // and in the same transaction, so a set is never seen without the values it stands for.
    private const string SweepCacheSetsSql = """
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        DECLARE @pick TABLE ([SetId] bigint NOT NULL PRIMARY KEY, [EntryCount] int NOT NULL);
        DECLARE @gone TABLE ([SetId] bigint NOT NULL PRIMARY KEY);
        DECLARE @setsGone int = 0;
        DECLARE @entriesGone int = 0;

        INSERT INTO @pick ([SetId], [EntryCount])
        SELECT TOP (@sets) s.[SetId], s.[EntryCount]
        FROM [osdu].[CacheSet] AS s
        WHERE s.[SetId] > @after
          AND s.[LastSeenUtc] < @unusedSince
          AND NOT EXISTS (SELECT 1 FROM [osdu].[Record] AS r WHERE r.[CacheSetId] = s.[SetId])
          AND NOT EXISTS (SELECT 1 FROM [osdu].[Record] AS r WHERE r.[HeldCacheSetId] = s.[SetId])
        ORDER BY s.[SetId];

        -- A round removes at most @entries values, and always its first set, however many that one holds; the sets it leaves
        -- lie past the cursor, so the next round takes them.
        DELETE p
        FROM @pick AS p
        WHERE p.[SetId] > (SELECT MIN(f.[SetId]) FROM @pick AS f)
          AND (SELECT SUM(CAST(q.[EntryCount] AS bigint)) FROM @pick AS q WHERE q.[SetId] <= p.[SetId]) > @entries;

        IF EXISTS (SELECT 1 FROM @pick)
        BEGIN
            BEGIN TRANSACTION;

            DELETE s
            OUTPUT deleted.[SetId] INTO @gone ([SetId])
            FROM [osdu].[CacheSet] AS s
            INNER JOIN @pick AS p ON p.[SetId] = s.[SetId]
            WHERE s.[LastSeenUtc] < @unusedSince
              AND NOT EXISTS (SELECT 1 FROM [osdu].[Record] AS r WHERE r.[CacheSetId] = s.[SetId])
              AND NOT EXISTS (SELECT 1 FROM [osdu].[Record] AS r WHERE r.[HeldCacheSetId] = s.[SetId]);
            SET @setsGone = @@ROWCOUNT;

            DELETE e
            FROM [osdu].[CacheSetEntry] AS e
            INNER JOIN @gone AS g ON g.[SetId] = e.[SetId];
            SET @entriesGone = @@ROWCOUNT;

            COMMIT TRANSACTION;
        END;

        SELECT @setsGone AS [Sets], @entriesGone AS [Entries], (SELECT MAX([SetId]) FROM @pick) AS [Last];
        """;

    /// <summary>
    /// Runs one round of the sweep past the set <paramref name="after"/>: at most <paramref name="sets"/> sets looked at and
    /// <paramref name="entries"/> values removed. <see cref="CacheSetSweepRound.Last"/> is null when no set past the cursor
    /// was left to look at.
    /// </summary>
    public static async Task<CacheSetSweepRound> SweepCacheSetsAsync(
        OsduDbContext db, DateTime unusedSinceUtc, long after, int sets, int entries, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, SweepCacheSetsSql, slice: null);
            command.Parameters.Add(new SqlParameter("@unusedSince", SqlDbType.DateTime2) { Value = unusedSinceUtc });
            command.Parameters.Add(new SqlParameter("@after", SqlDbType.BigInt) { Value = after });
            command.Parameters.Add(new SqlParameter("@sets", SqlDbType.Int) { Value = sets });
            command.Parameters.Add(new SqlParameter("@entries", SqlDbType.BigInt) { Value = (long)entries });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("A sweep of the render context's sets answered no row; the statement always answers one.");
            }

            return new CacheSetSweepRound(reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
