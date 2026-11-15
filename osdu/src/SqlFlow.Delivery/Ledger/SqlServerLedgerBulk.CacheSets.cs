using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>One round of a render-context sweep: the sets and entries it removed, and the last set it looked at.</summary>
internal readonly record struct CacheSetSweepRound(int Sets, int Entries, long? Last);

/// <summary>
/// One round of the sweep's pass over the values: the values it removed, the last value it looked at, and whether it was
/// let in (false when renders kept the values' locks past the wait, so the values it found unheld are left for the next
/// pass).
/// </summary>
internal readonly record struct CacheSetValueSweepRound(int Values, long? Last, bool LetIn);

/// <summary>How a mint of a set ended: the set written, or why none was.</summary>
internal enum CacheSetMintOutcome
{
    /// <summary>The set was written, holding every value it was handed.</summary>
    Written = 0,

    /// <summary>The lock that orders the writers of new values was not given within the wait.</summary>
    ValuesBusy = 1,

    /// <summary>The lock that keeps the sweep off the values a set is being written against was not given within the wait.</summary>
    SweepBusy = 2,

    /// <summary>A sweep removed values the set holds between their writing and the set's, each time it was tried.</summary>
    ValuesRemoved = 3,

    /// <summary>The database read other values from what it was handed than the render counted; nothing was written.</summary>
    ValuesMisread = 4,
}

/// <summary>
/// The statements that write and remove the render context (<see cref="CacheSetKeeping"/>): a set with the values it
/// holds, each kept once however many sets hold it (<c>osdu.CacheSetValue</c>), and the sweep's rounds.
/// </summary>
/// <remarks>
/// Three writers meet on the values, and two application locks order them, both owned by a transaction and so released
/// when it ends however it ends:
/// <list type="bullet">
/// <item><see cref="ValueAddLock"/>, exclusive, around the insert of the values a set holds that are not kept yet, so
/// two renders minting sets that share a new value never insert it twice, and never wait on each other's rows in two
/// orders.</item>
/// <item><see cref="ValueUseLock"/>, shared, around the insert of a set and its entries, which reads the values'
/// numbers; and exclusive, with <see cref="ValueAddLock"/>, around the sweep's delete of the values no set holds. While a
/// set is being written no value can be removed, and while values are removed no set is being written, so an entry never
/// names a value that is gone. Sets are written beside each other.</item>
/// </list>
/// A mint takes the two locks one after the other, never both, and the sweep takes them in one order, so they cannot
/// deadlock. A value the sweep removed between a mint's two steps is noticed (the set's entries come out fewer than the
/// values it was handed), nothing of the set is kept, and the mint writes the values again.
/// </remarks>
internal static partial class SqlServerLedgerBulk
{
    /// <summary>The application lock that orders the writers of new values.</summary>
    internal const string ValueAddLock = "osdu.CacheSetValue.add";

    /// <summary>The application lock a set's write shares and the sweep's delete of unheld values takes alone.</summary>
    internal const string ValueUseLock = "osdu.CacheSetValue.use";

    /// <summary>How long a mint waits for either lock: each is held for one short statement.</summary>
    private const int MintLockWaitMs = 60_000;

    /// <summary>How long a round of the pass over the values waits to be let in before it leaves its values to the next pass.</summary>
    private const int ValueSweepLockWaitMs = 5_000;

    // The values arrive as a JSON array of arrays (ordinal, type, item, path, kind, the value's hash in hex, the value's
    // text), each already cut to its column by the caller: OPENJSON cuts a longer text silently. A table variable takes
    // the database's collation, which is what the values' columns compare by; the item is compared exactly, as its column is.
    private const string MintCacheSetSql = """
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        DECLARE @use TABLE (
            [Ordinal] int NOT NULL PRIMARY KEY,
            [TypeName] nvarchar(200) NOT NULL,
            [ItemId] nvarchar(512) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Path] nvarchar(400) NOT NULL,
            [Kind] nvarchar(16) NOT NULL,
            [ValueHash] binary(32) NOT NULL,
            [ValueText] nvarchar(400) NOT NULL);
        DECLARE @count int;
        DECLARE @attempt int = 0;
        DECLARE @locked int;
        DECLARE @setId bigint = NULL;
        DECLARE @outcome int = NULL;

        INSERT INTO @use ([Ordinal], [TypeName], [ItemId], [Path], [Kind], [ValueHash], [ValueText])
        SELECT j.[Ordinal], j.[TypeName], j.[ItemId], j.[Path], j.[Kind], CONVERT(binary(32), j.[ValueHash], 2), j.[ValueText]
        FROM OPENJSON(@entries) WITH (
            [Ordinal] int N'strict $[0]',
            [TypeName] nvarchar(200) N'strict $[1]',
            [ItemId] nvarchar(512) N'strict $[2]',
            [Path] nvarchar(400) N'strict $[3]',
            [Kind] nvarchar(16) N'strict $[4]',
            [ValueHash] char(64) N'strict $[5]',
            [ValueText] nvarchar(400) N'strict $[6]') AS j;
        SET @count = @@ROWCOUNT;

        -- A set holding other values than the render read would answer for records it was not built from: nothing is
        -- written unless the values read are the values handed over.
        IF @count <> @expected
            SET @outcome = 4;

        WHILE @outcome IS NULL
        BEGIN
            SET @attempt += 1;

            -- The values the set holds that are not kept yet, written under the lock that orders their writers.
            IF EXISTS (
                SELECT 1 FROM @use AS u
                WHERE NOT EXISTS (
                    SELECT 1 FROM [osdu].[CacheSetValue] AS v WITH (FORCESEEK)
                    WHERE v.[Scope] = @scope AND v.[TypeName] = u.[TypeName] AND v.[ItemId] = u.[ItemId] AND v.[Path] = u.[Path]
                      AND v.[Kind] = u.[Kind] AND v.[ValueHash] = u.[ValueHash]))
            BEGIN
                BEGIN TRANSACTION;
                EXEC @locked = sys.sp_getapplock @Resource = @addLock, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @lockWait;
                IF @locked < 0
                BEGIN
                    ROLLBACK TRANSACTION;
                    SET @outcome = 1;
                    BREAK;
                END;

                INSERT INTO [osdu].[CacheSetValue] ([Scope], [TypeName], [ItemId], [Path], [Kind], [ValueHash], [ValueText])
                SELECT @scope, u.[TypeName], u.[ItemId], u.[Path], u.[Kind], u.[ValueHash], u.[ValueText]
                FROM @use AS u
                WHERE NOT EXISTS (
                    SELECT 1 FROM [osdu].[CacheSetValue] AS v WITH (FORCESEEK)
                    WHERE v.[Scope] = @scope AND v.[TypeName] = u.[TypeName] AND v.[ItemId] = u.[ItemId] AND v.[Path] = u.[Path]
                      AND v.[Kind] = u.[Kind] AND v.[ValueHash] = u.[ValueHash])
                ORDER BY u.[Ordinal];
                COMMIT TRANSACTION;
            END;

            -- The set and its entries in one transaction, so no run reads a set without what it holds, under the lock that
            -- keeps the sweep off the values they name.
            BEGIN TRANSACTION;
            EXEC @locked = sys.sp_getapplock @Resource = @useLock, @LockMode = N'Shared', @LockOwner = N'Transaction', @LockTimeout = @lockWait;
            IF @locked < 0
            BEGIN
                ROLLBACK TRANSACTION;
                SET @outcome = 2;
                BREAK;
            END;

            INSERT INTO [osdu].[CacheSet] ([SetHash], [EntryCount], [Gated], [FirstSeenUtc], [LastSeenUtc])
            VALUES (@hash, @count, 0, @now, @now);
            SET @setId = SCOPE_IDENTITY();

            INSERT INTO [osdu].[CacheSetEntry] ([SetId], [ValueId])
            SELECT @setId, v.[ValueId]
            FROM @use AS u
            INNER JOIN [osdu].[CacheSetValue] AS v WITH (FORCESEEK)
                ON v.[Scope] = @scope AND v.[TypeName] = u.[TypeName] AND v.[ItemId] = u.[ItemId] AND v.[Path] = u.[Path]
               AND v.[Kind] = u.[Kind] AND v.[ValueHash] = u.[ValueHash]
            ORDER BY u.[Ordinal];

            IF @@ROWCOUNT = @count
            BEGIN
                COMMIT TRANSACTION;
                SET @outcome = 0;
            END
            ELSE
            BEGIN
                -- A sweep removed a value between the two steps: nothing of the set is kept, and its values are written again.
                ROLLBACK TRANSACTION;
                SET @setId = NULL;
                IF @attempt >= 3
                    SET @outcome = 3;
            END;
        END;

        SELECT @outcome AS [Outcome], @setId AS [SetId], @count AS [Values];
        """;

    // The sets are picked first and judged again as they are deleted, under the delete's own locks: a render that marked a
    // picked set used in between (EnsureCacheSetAsync) keeps it, and a set the delete took is one no render can hand out,
    // since a render marks a set before it hands it out and finds a removed one gone. The set's row goes before its entries
    // and in the same transaction, so a set is never seen without what it holds. The values its entries named stay for the
    // pass over the values.
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

        -- A round removes at most @entries entries, and always its first set, however many that one holds; the sets it
        -- leaves lie past the cursor, so the next round takes them.
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
            FROM @gone AS g
            INNER JOIN [osdu].[CacheSetEntry] AS e WITH (FORCESEEK) ON e.[SetId] = g.[SetId];
            SET @entriesGone = @@ROWCOUNT;

            COMMIT TRANSACTION;
        END;

        SELECT @setsGone AS [Sets], @entriesGone AS [Entries], (SELECT MAX([SetId]) FROM @pick) AS [Last];
        """;

    // A round looks at the next values past the cursor, in key order, and removes those no set holds. It asks first
    // without a lock, which is all most rounds do; the delete judges each value again under both locks, when no set is
    // being written and no value added, so every entry a set holds is there to be seen.
    private const string SweepCacheSetValuesSql = """
        SET NOCOUNT ON;
        SET XACT_ABORT ON;
        DECLARE @pick TABLE ([ValueId] bigint NOT NULL PRIMARY KEY);
        DECLARE @valuesGone int = 0;
        DECLARE @locked int = 0;

        INSERT INTO @pick ([ValueId])
        SELECT TOP (@values) v.[ValueId]
        FROM [osdu].[CacheSetValue] AS v
        WHERE v.[ValueId] > @after
        ORDER BY v.[ValueId];

        IF EXISTS (
            SELECT 1 FROM @pick AS p
            WHERE NOT EXISTS (SELECT 1 FROM [osdu].[CacheSetEntry] AS e WITH (FORCESEEK) WHERE e.[ValueId] = p.[ValueId]))
        BEGIN
            BEGIN TRANSACTION;
            EXEC @locked = sys.sp_getapplock @Resource = @addLock, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @lockWait;
            IF @locked >= 0
                EXEC @locked = sys.sp_getapplock @Resource = @useLock, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @lockWait;

            IF @locked >= 0
            BEGIN
                DELETE v
                FROM @pick AS p
                INNER JOIN [osdu].[CacheSetValue] AS v WITH (FORCESEEK) ON v.[ValueId] = p.[ValueId]
                WHERE NOT EXISTS (SELECT 1 FROM [osdu].[CacheSetEntry] AS e WITH (FORCESEEK, READCOMMITTEDLOCK) WHERE e.[ValueId] = v.[ValueId]);
                SET @valuesGone = @@ROWCOUNT;
            END;

            -- Ending the transaction releases the locks; one that took neither, or one of them, changed nothing.
            COMMIT TRANSACTION;
        END;

        SELECT @valuesGone AS [Values], (SELECT MAX([ValueId]) FROM @pick) AS [Last], CASE WHEN @locked >= 0 THEN 1 ELSE 0 END AS [LetIn];
        """;

    /// <summary>
    /// Writes a set of <paramref name="count"/> values, <paramref name="entriesJson"/> being the values as
    /// <c>MintCacheSetSql</c> reads them, keeping each value that is new, and answers the set's id. A set of the same hash
    /// another run wrote first fails on the hash's unique index, as a duplicate key (2601), which the caller reads the other
    /// run's set on.
    /// </summary>
    /// <exception cref="DeliveryException">A lock was not given within the wait, a sweep kept removing the set's values, or the database read other values than it was handed.</exception>
    public static async Task<long> MintCacheSetAsync(
        OsduDbContext db, string scope, string hash, string entriesJson, int count, DateTime nowUtc, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, MintCacheSetSql, slice: null);
            command.Parameters.Add(new SqlParameter("@scope", SqlDbType.NVarChar, 200) { Value = scope });
            command.Parameters.Add(new SqlParameter("@hash", SqlDbType.NVarChar, 64) { Value = hash });
            command.Parameters.Add(new SqlParameter("@entries", SqlDbType.NVarChar, -1) { Value = entriesJson });
            command.Parameters.Add(new SqlParameter("@expected", SqlDbType.Int) { Value = count });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = nowUtc });
            command.Parameters.Add(new SqlParameter("@addLock", SqlDbType.NVarChar, 255) { Value = ValueAddLock });
            command.Parameters.Add(new SqlParameter("@useLock", SqlDbType.NVarChar, 255) { Value = ValueUseLock });
            command.Parameters.Add(new SqlParameter("@lockWait", SqlDbType.Int) { Value = MintLockWaitMs });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("Writing a set of the render context answered no row; the statement always answers one.");
            }

            var outcome = (CacheSetMintOutcome)reader.GetInt32(0);
            if (outcome == CacheSetMintOutcome.Written)
            {
                return reader.GetInt64(1);
            }

            throw new DeliveryException(outcome switch
            {
                CacheSetMintOutcome.ValuesBusy =>
                    $"A set of {count} cached value(s) of partition '{scope}' was not written: the lock that orders new values of the render context ({ValueAddLock}) was held by another writer for more than {MintLockWaitMs / 1000}s. Nothing was kept; run the delivery again, and if it recurs, look for a session holding the lock (sys.dm_tran_locks, resource type APPLICATION).",
                CacheSetMintOutcome.ValuesMisread =>
                    $"A set of {count} cached value(s) of partition '{scope}' was not written: the database read {reader.GetInt32(2)} value(s) from what it was handed. The values are passed as JSON, so one of them holds a text the database reads otherwise. Nothing was kept.",
                CacheSetMintOutcome.SweepBusy =>
                    $"A set of {count} cached value(s) of partition '{scope}' was not written: the render-context sweep held the values' lock ({ValueUseLock}) for more than {MintLockWaitMs / 1000}s. Nothing was kept; run the delivery again, and if it recurs, look for a session holding the lock (sys.dm_tran_locks, resource type APPLICATION).",
                _ =>
                    $"A set of {count} cached value(s) of partition '{scope}' was not written: the render-context sweep removed values it holds each of 3 times they were written. Nothing was kept; run the delivery again.",
            });
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one round of the sweep past the set <paramref name="after"/>: at most <paramref name="sets"/> sets looked at and
    /// <paramref name="entries"/> entries removed. <see cref="CacheSetSweepRound.Last"/> is null when no set past the cursor
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

    /// <summary>
    /// Runs one round of the sweep's pass over the values past the value <paramref name="after"/>: at most
    /// <paramref name="values"/> values looked at, and those no set holds removed.
    /// <see cref="CacheSetValueSweepRound.Last"/> is null when no value past the cursor was left to look at.
    /// </summary>
    public static async Task<CacheSetValueSweepRound> SweepCacheSetValuesAsync(OsduDbContext db, long after, int values, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await using var command = Command(connection, null, SweepCacheSetValuesSql, slice: null);
            command.Parameters.Add(new SqlParameter("@after", SqlDbType.BigInt) { Value = after });
            command.Parameters.Add(new SqlParameter("@values", SqlDbType.Int) { Value = values });
            command.Parameters.Add(new SqlParameter("@addLock", SqlDbType.NVarChar, 255) { Value = ValueAddLock });
            command.Parameters.Add(new SqlParameter("@useLock", SqlDbType.NVarChar, 255) { Value = ValueUseLock });
            command.Parameters.Add(new SqlParameter("@lockWait", SqlDbType.Int) { Value = ValueSweepLockWaitMs });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("A sweep of the render context's values answered no row; the statement always answers one.");
            }

            return new CacheSetValueSweepRound(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetInt32(2) == 1);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
