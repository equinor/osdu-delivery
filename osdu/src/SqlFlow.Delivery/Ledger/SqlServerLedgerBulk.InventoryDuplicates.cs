using System.Data;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Finding the duplicate records of an inventory (osdu/docs/reference/flow/inventory-duplicates.md): the keys
// one page of a check read, matched with the inventory's own rows by the OSDU id, which both keep in a binary collation so
// they compare exactly as OSDU compares them; the ids that share a key numbered into groups; how each group came out; and the
// copies picked to remove made a selection, every statement in one transaction.
internal static partial class SqlServerLedgerBulk
{
    private const string DuplicateKeysStageSql = """
        CREATE TABLE #Keys (
            [TargetId] nvarchar(500) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Version] bigint NULL,
            [KeyHash] binary(32) NULL,
            [ValuesJson] nvarchar(4000) NULL);
        """;

    // The records the search returned that the inventory serves a row of, each once (the highest version a repeat names), kept
    // with their key unless the check holds the row already; the rows that hold no key counted, as are the records the
    // inventory serves no row of and the rows read at another version than it listed; and the check's counts added to.
    private const string DuplicateKeysSql = """
        DECLARE @keyed bigint, @unkeyed bigint, @changed bigint, @outside bigint;

        WITH [k] AS (
            SELECT [TargetId], [Version], [KeyHash], [ValuesJson],
                ROW_NUMBER() OVER (PARTITION BY [TargetId] ORDER BY [Version] DESC) AS [n]
            FROM #Keys)
        SELECT r.[InventoryRecordId], k.[Version], k.[KeyHash], k.[ValuesJson], r.[Version] AS [Listed]
        INTO #Served
        FROM [k] AS k
        INNER JOIN [osdu].[InventoryRecord] AS r
            ON r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = k.[TargetId]
        WHERE k.[n] = 1 AND r.[GoneUtc] IS NULL AND r.[FirstSeenUtc] IS NOT NULL
            AND NOT EXISTS (
                SELECT 1 FROM [osdu].[InventoryDuplicateMember] AS m
                WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId AND m.[InventoryRecordId] = r.[InventoryRecordId]);

        INSERT INTO [osdu].[InventoryDuplicateMember] ([PartitionId], [InventoryDuplicateCheckId], [InventoryRecordId], [KeyHash], [IndexedVersion], [ValuesJson])
        SELECT @partitionId, @checkId, s.[InventoryRecordId], s.[KeyHash], s.[Version], s.[ValuesJson]
        FROM #Served AS s
        WHERE s.[KeyHash] IS NOT NULL;
        SET @keyed = @@ROWCOUNT;

        SELECT @unkeyed = COUNT_BIG(*) FROM #Served AS s WHERE s.[KeyHash] IS NULL;

        SELECT @changed = COUNT_BIG(*) FROM #Served AS s
        WHERE s.[Version] IS NOT NULL AND s.[Listed] IS NOT NULL AND s.[Version] <> s.[Listed];

        SELECT @outside = COUNT_BIG(DISTINCT k.[TargetId]) FROM #Keys AS k
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[InventoryRecord] AS r
            WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = k.[TargetId]
                AND r.[GoneUtc] IS NULL AND r.[FirstSeenUtc] IS NOT NULL);

        UPDATE [osdu].[InventoryDuplicateCheck] SET
            [Keyed] = [Keyed] + @keyed, [Unkeyed] = [Unkeyed] + @unkeyed, [Changed] = [Changed] + @changed, [Outside] = [Outside] + @outside,
            [Checked] = [Checked] + @checked, [Requests] = [Requests] + @requests
        WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;

        SELECT @keyed, @unkeyed, @outside, @changed;
        DROP TABLE #Served;
        DROP TABLE #Keys;
        """;

    // A page that returned nothing still moves the check on.
    private const string DuplicateProgressSql = """
        UPDATE [osdu].[InventoryDuplicateCheck] SET [Checked] = [Checked] + @checked, [Requests] = [Requests] + @requests
        WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;
        """;

    // The ids that share their key with another, numbered into groups (the largest first, then by the first id of each); the ids
    // whose key no other holds let go a batch at a time, so a check of millions never holds one long lock on the table; a group
    // too large to be one record written more than once left for review with why, its copies unread; and the check's counts.
    private const string DuplicateGroupSql = """
        DECLARE @groups bigint, @copies bigint, @broad bigint, @done int;

        SELECT m.[KeyHash], COUNT_BIG(*) AS [Copies], MIN(m.[InventoryRecordId]) AS [First]
        INTO #Shared
        FROM [osdu].[InventoryDuplicateMember] AS m
        WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId
        GROUP BY m.[KeyHash];

        SELECT CAST(ROW_NUMBER() OVER (ORDER BY s.[Copies] DESC, s.[First]) AS int) AS [GroupNumber], s.[KeyHash], s.[Copies], s.[First]
        INTO #Numbered
        FROM #Shared AS s
        WHERE s.[Copies] > 1;
        CREATE UNIQUE CLUSTERED INDEX [IX_Numbered] ON #Numbered ([KeyHash]);

        SET @done = @batch;
        WHILE @done = @batch
        BEGIN
            DELETE TOP (@batch) m
            FROM [osdu].[InventoryDuplicateMember] AS m
            WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId
                AND NOT EXISTS (SELECT 1 FROM #Numbered AS n WHERE n.[KeyHash] = m.[KeyHash]);
            SET @done = @@ROWCOUNT;
        END

        INSERT INTO [osdu].[InventoryDuplicateGroup] ([PartitionId], [InventoryDuplicateCheckId], [GroupNumber], [KeyHash], [Copies], [ValuesJson], [Outcome], [Reason])
        SELECT @partitionId, @checkId, n.[GroupNumber], n.[KeyHash], CAST(n.[Copies] AS int), f.[ValuesJson],
            CASE WHEN n.[Copies] > @maxCopies THEN N'review' ELSE N'pending' END,
            CASE WHEN n.[Copies] > @maxCopies
                THEN CONCAT(n.[Copies], N' copies share the key, more than the ', @maxCopies,
                    N' a group is picked from: a key this broad is not one record written more than once, so add properties to it and check again')
                ELSE NULL END
        FROM #Numbered AS n
        INNER JOIN [osdu].[InventoryDuplicateMember] AS f
            ON f.[PartitionId] = @partitionId AND f.[InventoryDuplicateCheckId] = @checkId AND f.[InventoryRecordId] = n.[First];
        SET @groups = @@ROWCOUNT;

        SET @done = @batch;
        WHILE @done = @batch
        BEGIN
            UPDATE TOP (@batch) m SET
                [GroupNumber] = n.[GroupNumber],
                [Role] = CASE WHEN n.[Copies] > @maxCopies THEN N'review' ELSE NULL END,
                [Reason] = CASE WHEN n.[Copies] > @maxCopies THEN N'its group is too broad to pick from, so its copies were not read' ELSE NULL END
            FROM [osdu].[InventoryDuplicateMember] AS m
            INNER JOIN #Numbered AS n ON n.[KeyHash] = m.[KeyHash]
            WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId AND m.[GroupNumber] IS NULL;
            SET @done = @@ROWCOUNT;
        END

        SELECT @copies = COALESCE(SUM(n.[Copies]), 0), @broad = COUNT_BIG(CASE WHEN n.[Copies] > @maxCopies THEN 1 END) FROM #Numbered AS n;

        UPDATE [osdu].[InventoryDuplicateCheck] SET
            [Groups] = @groups, [Copies] = @copies, [GroupsJudged] = @broad, [Review] = @broad, [Decided] = 0, [ToRemove] = 0, [Phase] = N'copies'
        WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;

        SELECT @groups, @copies, @broad;
        DROP TABLE #Numbered;
        DROP TABLE #Shared;
        """;

    private const string DuplicateJudgementStageSql = """
        CREATE TABLE #JudgedGroups (
            [GroupNumber] int NOT NULL PRIMARY KEY,
            [Outcome] nvarchar(16) NOT NULL,
            [Reason] nvarchar(1000) NULL,
            [Kept] bigint NULL);
        CREATE TABLE #JudgedCopies (
            [InventoryRecordId] bigint NOT NULL PRIMARY KEY,
            [GroupNumber] int NOT NULL,
            [StorageState] nvarchar(16) NOT NULL,
            [StoredVersion] bigint NULL,
            [Attributes] int NULL,
            [MentionedBy] bigint NULL,
            [Role] nvarchar(16) NOT NULL,
            [Reason] nvarchar(1000) NULL);
        """;

    // How the groups came out, written over what they held before: the check's counts move by the difference, so the same
    // statement records the copies pass (every group pending before it) and an operator's choice (a group decided before it).
    // The groups are read under an update lock first, so two choices of one group write one after the other. A group the check
    // does not hold, or a copy outside its group, is refused rather than written.
    private const string DuplicateJudgementSql = """
        DECLARE @wasPending bigint, @wasDecided bigint, @wasReview bigint, @wasRemove bigint;
        DECLARE @isDecided bigint, @isReview bigint, @isRemove bigint, @judged bigint, @known bigint;

        SELECT @known = COUNT_BIG(*),
            @wasPending = COUNT_BIG(CASE WHEN g.[Outcome] = N'pending' THEN 1 END),
            @wasDecided = COUNT_BIG(CASE WHEN g.[Outcome] IN (N'picked', N'chosen') THEN 1 END),
            @wasReview = COUNT_BIG(CASE WHEN g.[Outcome] = N'review' THEN 1 END)
        FROM [osdu].[InventoryDuplicateGroup] AS g WITH (UPDLOCK, HOLDLOCK)
        INNER JOIN #JudgedGroups AS j ON j.[GroupNumber] = g.[GroupNumber]
        WHERE g.[PartitionId] = @partitionId AND g.[InventoryDuplicateCheckId] = @checkId;

        SELECT @judged = COUNT_BIG(*) FROM #JudgedGroups;
        IF @known <> @judged THROW 51000, N'The judgement names a group the duplicate check does not hold.', 1;

        IF EXISTS (
            SELECT 1 FROM #JudgedCopies AS c
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[InventoryDuplicateMember] AS m
                WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId
                    AND m.[InventoryRecordId] = c.[InventoryRecordId] AND m.[GroupNumber] = c.[GroupNumber]))
            THROW 51000, N'The judgement names a copy its group does not hold.', 1;

        SELECT @wasRemove = COUNT_BIG(*)
        FROM [osdu].[InventoryDuplicateMember] AS m
        INNER JOIN #JudgedGroups AS j ON j.[GroupNumber] = m.[GroupNumber]
        WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId AND m.[Role] = N'remove';

        UPDATE m SET
            [StorageState] = c.[StorageState], [StoredVersion] = c.[StoredVersion], [Attributes] = c.[Attributes],
            [MentionedBy] = c.[MentionedBy], [Role] = c.[Role], [Reason] = c.[Reason]
        FROM [osdu].[InventoryDuplicateMember] AS m
        INNER JOIN #JudgedCopies AS c ON c.[InventoryRecordId] = m.[InventoryRecordId]
        WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId;

        UPDATE g SET
            [Outcome] = j.[Outcome], [Reason] = j.[Reason], [KeptInventoryRecordId] = j.[Kept],
            [ChosenBy] = CASE @choice WHEN 1 THEN @chosenBy WHEN 2 THEN NULL ELSE g.[ChosenBy] END,
            [ChosenUtc] = CASE @choice WHEN 1 THEN @now WHEN 2 THEN NULL ELSE g.[ChosenUtc] END
        FROM [osdu].[InventoryDuplicateGroup] AS g
        INNER JOIN #JudgedGroups AS j ON j.[GroupNumber] = g.[GroupNumber]
        WHERE g.[PartitionId] = @partitionId AND g.[InventoryDuplicateCheckId] = @checkId;

        SELECT @isDecided = COUNT_BIG(CASE WHEN j.[Outcome] IN (N'picked', N'chosen') THEN 1 END),
            @isReview = COUNT_BIG(CASE WHEN j.[Outcome] = N'review' THEN 1 END)
        FROM #JudgedGroups AS j;
        SELECT @isRemove = COUNT_BIG(*) FROM #JudgedCopies AS c WHERE c.[Role] = N'remove';

        UPDATE [osdu].[InventoryDuplicateCheck] SET
            [GroupsJudged] = [GroupsJudged] + @wasPending,
            [Decided] = [Decided] - @wasDecided + @isDecided,
            [Review] = [Review] - @wasReview + @isReview,
            [ToRemove] = [ToRemove] - @wasRemove + @isRemove,
            [Requests] = [Requests] + @requests,
            [ChosenUtc] = CASE WHEN @choice IN (1, 2) THEN @now ELSE [ChosenUtc] END
        WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;

        DROP TABLE #JudgedCopies;
        DROP TABLE #JudgedGroups;
        """;

    // The copies a completed check picked to remove made a selection, as a completed match would have filled one: each id with
    // the version the index held and the values it holds at the key, beside the copy its group keeps, which a removal checks
    // storage still holds. Only the groups whose copy to keep is decided give any. The check is read under an update lock, so
    // a choice written at the same time comes before or after the selection, never half way through it.
    private const string DuplicateSelectionSql = """
        DECLARE @status nvarchar(16), @build bigint, @inventoryId int, @selectionId bigint, @matched bigint, @changed bigint;

        SELECT @status = c.[Status], @build = c.[BuildRunId], @inventoryId = c.[InventoryId]
        FROM [osdu].[InventoryDuplicateCheck] AS c WITH (UPDLOCK, HOLDLOCK)
        WHERE c.[PartitionId] = @partitionId AND c.[InventoryDuplicateCheckId] = @checkId;
        IF @status IS NULL THROW 51000, N'The partition holds no such duplicate check.', 1;
        IF @status <> N'completed' THROW 51000, N'Only a completed duplicate check says which copies to remove.', 1;

        INSERT INTO [osdu].[InventorySelection] ([PartitionId], [InventoryId], [RunId], [Actor], [ConditionsJson], [ConditionsKey], [FiltersJson], [Status],
                [Query], [Direction], [NotesJson], [ColumnsJson], [BuildRunId], [Candidates], [ToCheck], [Checked], [Matched], [Outside], [Changed],
                [Requests], [CreatedUtc], [StartedUtc], [CompletedUtc], [Error], [DuplicateCheckId])
        SELECT @partitionId, @inventoryId, c.[RunId], @actor, N'[]', @conditionsKey, @filtersJson, N'completed',
            NULL, NULL, NULL, @columnsJson, @build, c.[Candidates], 0, 0, 0, 0, 0,
            0, @now, @now, @now, NULL, @checkId
        FROM [osdu].[InventoryDuplicateCheck] AS c
        WHERE c.[PartitionId] = @partitionId AND c.[InventoryDuplicateCheckId] = @checkId;
        SET @selectionId = CAST(SCOPE_IDENTITY() AS bigint);

        INSERT INTO [osdu].[InventorySelectionItem] ([PartitionId], [InventorySelectionId], [InventoryRecordId], [MatchedVersion], [ValuesJson], [KeptInventoryRecordId])
        SELECT @partitionId, @selectionId, m.[InventoryRecordId], m.[IndexedVersion], m.[ValuesJson], g.[KeptInventoryRecordId]
        FROM [osdu].[InventoryDuplicateMember] AS m
        INNER JOIN [osdu].[InventoryDuplicateGroup] AS g
            ON g.[PartitionId] = @partitionId AND g.[InventoryDuplicateCheckId] = @checkId AND g.[GroupNumber] = m.[GroupNumber]
        WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId AND m.[Role] = N'remove'
            AND g.[Outcome] IN (N'picked', N'chosen') AND g.[KeptInventoryRecordId] IS NOT NULL;
        SET @matched = @@ROWCOUNT;

        SELECT @changed = COUNT_BIG(*)
        FROM [osdu].[InventorySelectionItem] AS i
        INNER JOIN [osdu].[InventoryRecord] AS r ON r.[PartitionId] = @partitionId AND r.[InventoryRecordId] = i.[InventoryRecordId]
        WHERE i.[PartitionId] = @partitionId AND i.[InventorySelectionId] = @selectionId
            AND i.[MatchedVersion] IS NOT NULL AND r.[Version] IS NOT NULL AND i.[MatchedVersion] <> r.[Version];

        UPDATE [osdu].[InventorySelection] SET [ToCheck] = @matched, [Checked] = @matched, [Matched] = @matched, [Changed] = @changed
        WHERE [PartitionId] = @partitionId AND [InventorySelectionId] = @selectionId;

        UPDATE [osdu].[InventoryDuplicateCheck] SET [SelectionId] = @selectionId
        WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;

        SELECT @selectionId;
        """;

    /// <summary>
    /// Keeps what one page of a check read, in one transaction: each record the search returned that the inventory serves a row
    /// of, once, with its key, the version the index holds and what it holds at the key; how many such rows hold no key, how many
    /// records the inventory holds no row of, and how many rows are at another version than it listed; and the check's progress.
    /// </summary>
    public static Task<InventoryDuplicateKeyWrite> AddInventoryDuplicateKeysAsync(
        OsduDbContext db, short partitionId, int inventoryId, long checkId, IReadOnlyList<InventoryDuplicateKeyHit> hits, long checkedCount, long requests,
        CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            if (hits.Count == 0)
            {
                await using var progress = Command(connection, transaction, DuplicateProgressSql, slice: null);
                AddDuplicateParameters(progress, partitionId, checkId);
                AddProgressParameters(progress, checkedCount, requests);
                await progress.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return new InventoryDuplicateKeyWrite(0, 0, 0, 0);
            }

            await ExecuteAsync(connection, transaction, DuplicateKeysStageSql, ct).ConfigureAwait(false);
            using (var table = new DataTable())
            {
                table.Columns.Add("TargetId", typeof(string));
                table.Columns.Add("Version", typeof(long));
                table.Columns.Add("KeyHash", typeof(byte[]));
                table.Columns.Add("ValuesJson", typeof(string));
                foreach (var hit in hits)
                {
                    table.Rows.Add(hit.TargetId, Value(hit.Version), hit.KeyHash is null ? DBNull.Value : hit.KeyHash, Value(hit.ValuesJson));
                }

                await BulkCopyAsync(connection, transaction, "#Keys", table, ct).ConfigureAwait(false);
            }

            await using var command = Command(connection, transaction, DuplicateKeysSql, slice: null);
            AddDuplicateParameters(command, partitionId, checkId);
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            AddProgressParameters(command, checkedCount, requests);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("A write of the duplicate check's keys answered nothing.");
            }

            return new InventoryDuplicateKeyWrite(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
        }, ct);

    /// <summary>
    /// Numbers the ids that share a key into groups, the largest first, and lets go of the ids whose key no other holds; a group
    /// of more than <paramref name="maxCopies"/> copies is left for review, its copies unread. The check moves on to reading the
    /// copies.
    /// </summary>
    public static Task<InventoryDuplicateGrouping> GroupInventoryDuplicatesAsync(OsduDbContext db, short partitionId, long checkId, int maxCopies, int batch, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, DuplicateGroupSql, slice: null);
            AddDuplicateParameters(command, partitionId, checkId);
            command.Parameters.Add(new SqlParameter("@maxCopies", SqlDbType.Int) { Value = maxCopies });
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = batch });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("The grouping of the duplicate check's keys answered nothing.");
            }

            return new InventoryDuplicateGrouping(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }, ct);

    /// <summary>
    /// Writes how the groups came out over what they held before, in one transaction, and moves the check's counts by the
    /// difference: the copies pass's rounds (<see cref="DuplicateChoice.None"/>), an operator's choice of the copy a group keeps,
    /// recorded with who chose (<paramref name="chosenBy"/>) and when, and an operator's choice taken back, which leaves the group
    /// to the check's own pick again.
    /// </summary>
    public static Task<int> WriteInventoryDuplicateJudgementsAsync(
        OsduDbContext db, short partitionId, long checkId, IReadOnlyList<InventoryDuplicateJudgement> judgements, long requests, DuplicateChoice choice,
        string? chosenBy, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await ExecuteAsync(connection, transaction, DuplicateJudgementStageSql, ct).ConfigureAwait(false);
            using (var groups = new DataTable())
            using (var copies = new DataTable())
            {
                groups.Columns.Add("GroupNumber", typeof(int));
                groups.Columns.Add("Outcome", typeof(string));
                groups.Columns.Add("Reason", typeof(string));
                groups.Columns.Add("Kept", typeof(long));
                foreach (var (name, type) in new (string, Type)[]
                {
                    ("InventoryRecordId", typeof(long)), ("GroupNumber", typeof(int)), ("StorageState", typeof(string)), ("StoredVersion", typeof(long)),
                    ("Attributes", typeof(int)), ("MentionedBy", typeof(long)), ("Role", typeof(string)), ("Reason", typeof(string)),
                })
                {
                    copies.Columns.Add(name, type);
                }

                foreach (var judgement in judgements)
                {
                    groups.Rows.Add(judgement.GroupNumber, judgement.Outcome, Value(InventoryDuplicates.Reason(judgement.Reason)), Value(judgement.KeptInventoryRecordId));
                    foreach (var copy in judgement.Copies)
                    {
                        copies.Rows.Add(
                            copy.InventoryRecordId, judgement.GroupNumber, copy.StorageState, Value(copy.StoredVersion), Value(copy.Attributes), Value(copy.MentionedBy),
                            copy.Role, Value(InventoryDuplicates.Reason(copy.Reason)));
                    }
                }

                await BulkCopyAsync(connection, transaction, "#JudgedGroups", groups, ct).ConfigureAwait(false);
                await BulkCopyAsync(connection, transaction, "#JudgedCopies", copies, ct).ConfigureAwait(false);
            }

            await using var command = Command(connection, transaction, DuplicateJudgementSql, slice: null);
            AddDuplicateParameters(command, partitionId, checkId);
            command.Parameters.Add(new SqlParameter("@requests", SqlDbType.BigInt) { Value = requests });
            command.Parameters.Add(new SqlParameter("@choice", SqlDbType.Int) { Value = (int)choice });
            command.Parameters.Add(new SqlParameter("@chosenBy", SqlDbType.NVarChar, 200) { Value = chosenBy is null ? DBNull.Value : Truncate(chosenBy, 200)! });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Makes a selection of the copies a completed check picked to remove, in one transaction: completed against the build the
    /// check read, each id beside the copy its group keeps. Returns the selection's number.
    /// </summary>
    public static Task<long> CreateInventoryDuplicateSelectionAsync(
        OsduDbContext db, short partitionId, long checkId, string actor, string conditionsKey, string filtersJson, string? columnsJson, DateTime now, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var command = Command(connection, transaction, DuplicateSelectionSql, slice: null);
            AddDuplicateParameters(command, partitionId, checkId);
            command.Parameters.Add(new SqlParameter("@actor", SqlDbType.NVarChar, 200) { Value = Truncate(actor, 200)! });
            command.Parameters.Add(new SqlParameter("@conditionsKey", SqlDbType.VarChar, DeliveryModel.SelectionKeyLength) { Value = conditionsKey });
            command.Parameters.Add(new SqlParameter("@filtersJson", SqlDbType.NVarChar, -1) { Value = filtersJson });
            command.Parameters.Add(new SqlParameter("@columnsJson", SqlDbType.NVarChar, 4000) { Value = columnsJson is null ? DBNull.Value : columnsJson });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = now });
            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return result is long id ? id : throw new DeliveryException("Making a selection of the duplicate check's picks answered no selection.");
        }, ct);

    private static void AddDuplicateParameters(SqlCommand command, short partitionId, long checkId)
    {
        command.Parameters.Add(new SqlParameter("@partitionId", SqlDbType.SmallInt) { Value = partitionId });
        command.Parameters.Add(new SqlParameter("@checkId", SqlDbType.BigInt) { Value = checkId });
    }

    private static void AddProgressParameters(SqlCommand command, long checkedCount, long requests)
    {
        command.Parameters.Add(new SqlParameter("@checked", SqlDbType.BigInt) { Value = checkedCount });
        command.Parameters.Add(new SqlParameter("@requests", SqlDbType.BigInt) { Value = requests });
    }
}
