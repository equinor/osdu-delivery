using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>What a purge of a dimension deleted: the keys an earlier build removed, and the values none of its keys held.</summary>
internal readonly record struct DimensionPurge(long Keys, long Values);

/// <summary>
/// A dimension keeps only its latest state (osdu/docs/reference/flow/dimension.md, What a build keeps). A build marks what
/// it no longer finds as removed, which its write needs to take the keys out of the dimension's table in the same
/// transaction, and the next build's write deletes them before it merges: each key with its attributes, elements, the
/// records it was read from and the records that held it, then each value no key holds any more. Every change still names
/// what it was, since a change keeps its key's and values' text.
/// </summary>
internal static partial class SqlServerDimensionStore
{
    /// <summary>The removed keys one round deletes: a well log's curves are a few hundred elements a key.</summary>
    private const int PurgeKeysPerRound = 25;

    /// <summary>The removed values one round deletes, once no removed key is left.</summary>
    private const int PurgeValuesPerRound = 1_000;

    // One round, under the dimension's write lock, so no build of the dimension merges beside it: the next removed keys in
    // key order with everything that names them, or, once none is left, the removed values no key holds.
    private const string PurgeRemovedSql = """
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        DECLARE @gone TABLE ([ValueId] bigint NOT NULL PRIMARY KEY);
        DECLARE @keysGone int = 0;
        DECLARE @valuesGone int = 0;

        INSERT INTO @gone ([ValueId])
        SELECT TOP (@keys) v.[ValueId]
        FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
        WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[RemovedRunId] IS NOT NULL
        ORDER BY v.[ValueId];

        IF EXISTS (SELECT 1 FROM @gone)
        BEGIN
            DELETE e FROM [osdu].[DimensionElement] AS e WITH (FORCESEEK)
            INNER JOIN @gone AS g ON g.[ValueId] = e.[ValueId]
            WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d;

            DELETE a FROM [osdu].[DimensionAttribute] AS a WITH (FORCESEEK)
            INNER JOIN @gone AS g ON g.[ValueId] = a.[ValueId]
            WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d;

            DELETE k FROM [osdu].[DimensionKeyRecord] AS k WITH (FORCESEEK)
            INNER JOIN @gone AS g ON g.[ValueId] = k.[ValueId]
            WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d;

            DELETE r FROM [osdu].[DimensionRecord] AS r WITH (FORCESEEK)
            INNER JOIN @gone AS g ON g.[ValueId] = r.[ValueId]
            WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d;

            DELETE v FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
            INNER JOIN @gone AS g ON g.[ValueId] = v.[ValueId]
            WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d;
            SET @keysGone = @@ROWCOUNT;
        END
        ELSE
        BEGIN
            DELETE TOP (@values) m FROM [osdu].[DimensionMember] AS m WITH (FORCESEEK)
            WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[RemovedRunId] IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
                  WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[MemberId] = m.[MemberId]);
            SET @valuesGone = @@ROWCOUNT;
        END;

        SELECT @keysGone AS [Keys], @valuesGone AS [Values];
        """;

    /// <summary>
    /// Deletes what earlier builds of dimension <paramref name="dimensionId"/> removed, in rounds of a few keys, each its own
    /// short transaction under the dimension's write lock, until nothing removed is left. Safe to interrupt and to repeat: a
    /// round deletes a key and everything naming it together, and the next write takes up what a stopped one left.
    /// </summary>
    public static async Task<DimensionPurge> PurgeRemovedAsync(OsduDbContext db, short partitionId, int dimensionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            long keys = 0;
            long values = 0;
            while (true)
            {
                var round = await PurgeRoundAsync(connection, partitionId, dimensionId, ct).ConfigureAwait(false);
                keys += round.Keys;
                values += round.Values;
                if (round.Keys == 0 && round.Values == 0)
                {
                    return new DimensionPurge(keys, values);
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<DimensionPurge> PurgeRoundAsync(SqlConnection connection, short partitionId, int dimensionId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            try
            {
                await LockAsync(connection, transaction, partitionId, dimensionId, ct).ConfigureAwait(false);
                DimensionPurge round;
                await using (var command = Command(connection, transaction, PurgeRemovedSql))
                {
                    command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
                    command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
                    command.Parameters.Add(new SqlParameter("@keys", SqlDbType.Int) { Value = PurgeKeysPerRound });
                    command.Parameters.Add(new SqlParameter("@values", SqlDbType.Int) { Value = PurgeValuesPerRound });
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                            $"The purge of dimension {dimensionId}'s removed keys answered no row; the statement always answers one."));
                    }

                    round = new DimensionPurge(reader.GetInt32(0), reader.GetInt32(1));
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return round;
            }
            catch (Exception ex) when (attempt < DeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
            {
                // The round rolled back whole; it is made again from the rows still marked removed.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 200) * attempt), ct).ConfigureAwait(false);
            }
        }
    }
}
