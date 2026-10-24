using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Finding the duplicate records of an inventory (osdu/docs/reference/flow/inventory-duplicates.md): the check's own table,
// made afresh by its download and written a page at a time, each record the search returned matched with the inventory's own
// rows by the OSDU id, which both keep in a binary collation so they compare exactly as OSDU compares them; the ids that hold
// equal values at a key grouped from the table in SQL, as often as the key changes; how each group came out; and the copies
// picked to remove made a selection, every statement in one transaction.
internal static partial class SqlServerLedgerBulk
{
    /// <summary>The hash a property a record holds no value at stands as in a key: no SHA-256 is all zeros.</summary>
    private static readonly byte[] AbsentHash = new byte[InventoryDuplicateTable.HashLength];

    /// <summary>How many copies one statement fills the values of, after a grouping.</summary>
    private const int ValuesChunk = 5000;

    /// <summary>
    /// Makes a check's table afresh, outside any transaction: what an attempt before this one wrote is dropped, and the table is
    /// made through SQLFlow's schema evolution with a column for each property's values and one for their hash.
    /// The check's row records the table and the properties it holds.
    /// </summary>
    /// <exception cref="DeliveryException">The table cannot be made: the database user may not create it.</exception>
    public static async Task PrepareInventoryDuplicateTableAsync(
        OsduDbContext db, short partitionId, long checkId, IReadOnlyList<DuplicateColumn> attributes, CancellationToken ct)
    {
        var table = InventoryDuplicateTable.NameOf(checkId);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await ExecuteAsync(connection, null, $"DROP TABLE IF EXISTS {RunTimeTables.Qualified(table)};", ct).ConfigureAwait(false);
            await RunTimeTables.EvolveAsync(connection, table, InventoryDuplicateTable.Desired(attributes), InventoryDuplicateTable.KeyColumns, ct).ConfigureAwait(false);
            await using var record = Command(
                connection,
                null,
                """
                UPDATE [osdu].[InventoryDuplicateCheck] SET [TableName] = @table, [AttributesJson] = @attributes
                WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;
                """,
                slice: null);
            AddDuplicateParameters(record, partitionId, checkId);
            record.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, DeliveryModel.MaxRunTimeTableNameLength) { Value = table });
            record.Parameters.Add(new SqlParameter("@attributes", SqlDbType.NVarChar, -1) { Value = InventoryDuplicateTable.ToJson(attributes) });
            await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlException or SqlFlowException)
        {
            throw new DeliveryException(
                $"The duplicate check's table {DeliveryModel.SchemaName}.{table} could not be made, so nothing was downloaded: {ex.Message.Trim()} The database user the module connects as needs CREATE TABLE in the database and ALTER on the osdu schema.",
                ex);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Drops a check's table, if it has one: its download failed, or a sweep retired the check.</summary>
    public static async Task DeleteInventoryDuplicateTableAsync(OsduDbContext db, short partitionId, long checkId, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            await ExecuteAsync(connection, null, $"DROP TABLE IF EXISTS {RunTimeTables.Qualified(InventoryDuplicateTable.NameOf(checkId))};", ct).ConfigureAwait(false);
            await using var record = Command(
                connection,
                null,
                "UPDATE [osdu].[InventoryDuplicateCheck] SET [TableName] = NULL WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;",
                slice: null);
            AddDuplicateParameters(record, partitionId, checkId);
            await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The temporary table a page of a download is staged in: the OSDU id, the version, and each property's columns, as the check's table names them.</summary>
    private static string RowsStageSql(IReadOnlyList<DuplicateColumn> attributes)
    {
        var sql = new StringBuilder("""
            CREATE TABLE #DupRows (
                [TargetId] nvarchar(500) COLLATE Latin1_General_100_BIN2 NOT NULL,
                [Version] bigint NULL
            """);
        foreach (var attribute in attributes)
        {
            sql.Append(",\n    ").Append(RunTimeTables.Quoted(attribute.Column)).Append(" nvarchar(max) NULL");
            sql.Append(",\n    ").Append(RunTimeTables.Quoted(InventoryDuplicateTable.HashColumn(attribute.Column))).Append(" binary(32) NULL");
        }

        return sql.Append(");").ToString();
    }

    /// <summary>
    /// The statements that keep a staged page in the check's table: each record the inventory serves a row of, once (the highest
    /// version a repeat names), unless the table holds the row already; the records the inventory serves no row of counted, as are
    /// the rows read at another version than it listed; and the check's progress.
    /// </summary>
    private static string RowsSql(string table, IReadOnlyList<DuplicateColumn> attributes)
    {
        var target = RunTimeTables.Qualified(table);
        var columns = attributes
            .SelectMany(a => new[] { a.Column, InventoryDuplicateTable.HashColumn(a.Column) })
            .Select(RunTimeTables.Quoted)
            .ToList();
        var listed = columns.Count == 0 ? string.Empty : ", " + string.Join(", ", columns);
        var staged = columns.Count == 0 ? string.Empty : ", " + string.Join(", ", columns.Select(c => "s." + c));
        return $"""
            DECLARE @written bigint, @changed bigint, @outside bigint;

            WITH [k] AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY [TargetId] ORDER BY [Version] DESC) AS [Repeat]
                FROM #DupRows)
            SELECT r.[InventoryRecordId] AS [RecordId], r.[Version] AS [Listed], k.*
            INTO #Served
            FROM [k] AS k
            INNER JOIN [osdu].[InventoryRecord] AS r
                ON r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = k.[TargetId]
            WHERE k.[Repeat] = 1 AND r.[GoneUtc] IS NULL AND r.[FirstSeenUtc] IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM {target} AS d WHERE d.[{InventoryDuplicateTable.RecordColumn}] = r.[InventoryRecordId]);

            INSERT INTO {target} ([{InventoryDuplicateTable.RecordColumn}], [{InventoryDuplicateTable.TargetColumn}], [{InventoryDuplicateTable.VersionColumn}]{listed})
            SELECT s.[RecordId], s.[TargetId], s.[Version]{staged}
            FROM #Served AS s;
            SET @written = @@ROWCOUNT;

            SELECT @changed = COUNT_BIG(*) FROM #Served AS s
            WHERE s.[Version] IS NOT NULL AND s.[Listed] IS NOT NULL AND s.[Version] <> s.[Listed];

            SELECT @outside = COUNT_BIG(DISTINCT k.[TargetId]) FROM #DupRows AS k
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[InventoryRecord] AS r
                WHERE r.[PartitionId] = @partitionId AND r.[InventoryId] = @inventoryId AND r.[TargetId] = k.[TargetId]
                    AND r.[GoneUtc] IS NULL AND r.[FirstSeenUtc] IS NOT NULL);

            UPDATE [osdu].[InventoryDuplicateCheck] SET
                [Changed] = [Changed] + @changed, [Outside] = [Outside] + @outside,
                [Checked] = [Checked] + @checked, [Requests] = [Requests] + @requests
            WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;

            SELECT @written, @outside, @changed;
            DROP TABLE #Served;
            DROP TABLE #DupRows;
            """;
    }

    // A page that returned nothing still moves the check on.
    private const string DuplicateProgressSql = """
        UPDATE [osdu].[InventoryDuplicateCheck] SET [Checked] = [Checked] + @checked, [Requests] = [Requests] + @requests
        WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;
        """;

    /// <summary>
    /// The statements that group a check's table by a key, over whatever it was grouped by before, under an update lock on the
    /// check so two groupings, or a grouping and a pick, never interleave: each row's key the hash of its key properties' hashes
    /// in the key's order (a property it holds no value at standing as <see cref="AbsentHash"/>); a row holding no value at any
    /// of them passed over; the rows whose key another shares numbered into groups, the largest first and then by the first id of
    /// each; a group too large to be one record written more than once left for review with why, its copies unread; and the
    /// check's counts, its key and who set it. The groups wait to be picked.
    /// </summary>
    private static string GroupSql(string table, IReadOnlyList<string> hashColumns)
    {
        var target = RunTimeTables.Qualified(table);
        var key = string.Join(" + ", hashColumns.Select(c => $"ISNULL(d.{RunTimeTables.Quoted(c)}, @absent)"));
        var held = string.Join(" OR ", hashColumns.Select(c => $"d.{RunTimeTables.Quoted(c)} IS NOT NULL"));
        return $"""
            DECLARE @status nvarchar(16), @phase nvarchar(16), @groups bigint, @copies bigint, @broad bigint, @keyed bigint, @rows bigint, @done int;

            SELECT @status = c.[Status], @phase = c.[Phase]
            FROM [osdu].[InventoryDuplicateCheck] AS c WITH (UPDLOCK, HOLDLOCK)
            WHERE c.[PartitionId] = @partitionId AND c.[InventoryDuplicateCheckId] = @checkId;
            IF @status IS NULL THROW 51000, N'The partition holds no such duplicate check.', 1;
            IF @status IN (N'failed', N'expired') THROW 51000, N'The check holds no download to group: it failed, or a sweep retired it.', 1;
            IF @phase = N'copies' AND @status IN (N'queued', N'running') THROW 51000, N'The check is picking the copies of its groups; group it again once the pick ends.', 1;

            SET @done = @batch;
            WHILE @done = @batch
            BEGIN
                DELETE TOP (@batch) FROM [osdu].[InventoryDuplicateMember]
                WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;
                SET @done = @@ROWCOUNT;
            END

            SET @done = @batch;
            WHILE @done = @batch
            BEGIN
                DELETE TOP (@batch) FROM [osdu].[InventoryDuplicateGroup]
                WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;
                SET @done = @@ROWCOUNT;
            END

            SELECT d.[{InventoryDuplicateTable.RecordColumn}] AS [Id], d.[{InventoryDuplicateTable.VersionColumn}] AS [Version],
                CAST(HASHBYTES('SHA2_256', {key}) AS binary(32)) AS [KeyHash]
            INTO #Keyed
            FROM {target} AS d
            WHERE {held};
            SET @keyed = @@ROWCOUNT;
            SELECT @rows = COUNT_BIG(*) FROM {target};

            SELECT k.[KeyHash], COUNT_BIG(*) AS [Copies], MIN(k.[Id]) AS [First]
            INTO #Shared
            FROM #Keyed AS k
            GROUP BY k.[KeyHash]
            HAVING COUNT_BIG(*) > 1;

            SELECT CAST(ROW_NUMBER() OVER (ORDER BY s.[Copies] DESC, s.[First]) AS int) AS [GroupNumber], s.[KeyHash], s.[Copies]
            INTO #Numbered
            FROM #Shared AS s;
            CREATE UNIQUE CLUSTERED INDEX [IX_Numbered] ON #Numbered ([KeyHash]);

            INSERT INTO [osdu].[InventoryDuplicateGroup] ([PartitionId], [InventoryDuplicateCheckId], [GroupNumber], [KeyHash], [Copies], [ValuesJson], [Outcome], [Reason])
            SELECT @partitionId, @checkId, n.[GroupNumber], n.[KeyHash], CAST(n.[Copies] AS int), NULL,
                CASE WHEN n.[Copies] > @maxCopies THEN N'review' ELSE N'pending' END,
                CASE WHEN n.[Copies] > @maxCopies
                    THEN CONCAT(n.[Copies], N' copies share the key, more than the ', @maxCopies,
                        N' a group is picked from: a key this broad is not one record written more than once, so add properties to it')
                    ELSE NULL END
            FROM #Numbered AS n;
            SET @groups = @@ROWCOUNT;

            INSERT INTO [osdu].[InventoryDuplicateMember] ([PartitionId], [InventoryDuplicateCheckId], [InventoryRecordId], [KeyHash], [GroupNumber], [IndexedVersion], [ValuesJson], [Role], [Reason])
            SELECT @partitionId, @checkId, k.[Id], k.[KeyHash], n.[GroupNumber], k.[Version], NULL,
                CASE WHEN n.[Copies] > @maxCopies THEN N'review' ELSE NULL END,
                CASE WHEN n.[Copies] > @maxCopies THEN N'its group is too broad to pick from, so its copies are not read' ELSE NULL END
            FROM #Keyed AS k
            INNER JOIN #Numbered AS n ON n.[KeyHash] = k.[KeyHash];
            SET @copies = @@ROWCOUNT;

            SELECT @broad = COUNT_BIG(CASE WHEN n.[Copies] > @maxCopies THEN 1 END) FROM #Numbered AS n;

            UPDATE [osdu].[InventoryDuplicateCheck] SET
                [KeyJson] = @keyJson, [KeyHash] = @keyHash, [KeyedBy] = @actor, [KeyedUtc] = @now,
                [Keyed] = @keyed, [Unkeyed] = @rows - @keyed, [Groups] = @groups, [Copies] = @copies,
                [GroupsJudged] = @broad, [Review] = @broad, [Decided] = 0, [ToRemove] = 0,
                [SelectionId] = NULL, [ChosenUtc] = NULL, [PickedUtc] = NULL
            WHERE [PartitionId] = @partitionId AND [InventoryDuplicateCheckId] = @checkId;

            SELECT @groups, @copies, @broad, @keyed, @rows - @keyed;
            DROP TABLE #Numbered;
            DROP TABLE #Shared;
            DROP TABLE #Keyed;
            """;
    }

    /// <summary>A chunk of a check's copies with what each holds at the properties named, in the inventory's order, after <c>@after</c>.</summary>
    private static string CopyValuesSql(string table, IReadOnlyList<DuplicateColumn> attributes)
    {
        var columns = attributes.Count == 0 ? string.Empty : ", " + string.Join(", ", attributes.Select(a => "d." + RunTimeTables.Quoted(a.Column)));
        return $"""
            SELECT TOP (@chunk) m.[InventoryRecordId]{columns}
            FROM [osdu].[InventoryDuplicateMember] AS m
            INNER JOIN {RunTimeTables.Qualified(table)} AS d ON d.[{InventoryDuplicateTable.RecordColumn}] = m.[InventoryRecordId]
            WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId AND m.[InventoryRecordId] > @after
            ORDER BY m.[InventoryRecordId];
            """;
    }

    private const string CopyValuesStageSql = """
        CREATE TABLE #CopyValues ([InventoryRecordId] bigint NOT NULL PRIMARY KEY, [ValuesJson] nvarchar(4000) NULL);
        """;

    // A chunk of copies given what they hold at the key, and every group the values of its first copy.
    private const string CopyValuesWriteSql = """
        UPDATE m SET [ValuesJson] = v.[ValuesJson]
        FROM [osdu].[InventoryDuplicateMember] AS m
        INNER JOIN #CopyValues AS v ON v.[InventoryRecordId] = m.[InventoryRecordId]
        WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId;
        DROP TABLE #CopyValues;
        """;

    private const string GroupValuesSql = """
        UPDATE g SET [ValuesJson] = (
            SELECT TOP (1) m.[ValuesJson] FROM [osdu].[InventoryDuplicateMember] AS m
            WHERE m.[PartitionId] = g.[PartitionId] AND m.[InventoryDuplicateCheckId] = g.[InventoryDuplicateCheckId] AND m.[GroupNumber] = g.[GroupNumber]
            ORDER BY m.[InventoryRecordId])
        FROM [osdu].[InventoryDuplicateGroup] AS g
        WHERE g.[PartitionId] = @partitionId AND g.[InventoryDuplicateCheckId] = @checkId;
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
    // statement records a pick (every group pending before it) and an operator's choice (a group decided before it). The groups
    // are read under an update lock first, so two choices of one group write one after the other. A group the check does not
    // hold, or a copy outside its group, is refused rather than written: a key set since formed other groups.
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
        IF @known <> @judged THROW 51000, N'The judgement names a group the duplicate check does not hold: its key was set again since.', 1;

        IF EXISTS (
            SELECT 1 FROM #JudgedCopies AS c
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[InventoryDuplicateMember] AS m
                WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId
                    AND m.[InventoryRecordId] = c.[InventoryRecordId] AND m.[GroupNumber] = c.[GroupNumber]))
            THROW 51000, N'The judgement names a copy its group does not hold: the check''s key was set again since.', 1;

        IF EXISTS (
            SELECT 1 FROM #JudgedGroups AS j
            WHERE (SELECT COUNT_BIG(*) FROM [osdu].[InventoryDuplicateMember] AS m
                   WHERE m.[PartitionId] = @partitionId AND m.[InventoryDuplicateCheckId] = @checkId AND m.[GroupNumber] = j.[GroupNumber])
                <> (SELECT COUNT_BIG(*) FROM #JudgedCopies AS c WHERE c.[GroupNumber] = j.[GroupNumber]))
            THROW 51000, N'The judgement does not name every copy of its group: the check''s key was set again since.', 1;

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
    // storage still holds, and the key the groups were formed by, which the check's may not stay. Only the groups whose copy to
    // keep is decided give any. The check is read under an update lock, so a choice or a grouping written at the same time comes
    // before or after the selection, never half way through it.
    private const string DuplicateSelectionSql = """
        DECLARE @status nvarchar(16), @build bigint, @inventoryId int, @key nvarchar(max), @selectionId bigint, @matched bigint, @changed bigint;

        SELECT @status = c.[Status], @build = c.[BuildRunId], @inventoryId = c.[InventoryId], @key = c.[KeyJson]
        FROM [osdu].[InventoryDuplicateCheck] AS c WITH (UPDLOCK, HOLDLOCK)
        WHERE c.[PartitionId] = @partitionId AND c.[InventoryDuplicateCheckId] = @checkId;
        IF @status IS NULL THROW 51000, N'The partition holds no such duplicate check.', 1;
        IF @status <> N'completed' THROW 51000, N'Only a check whose download completed, and which is not picking, says which copies to remove.', 1;

        INSERT INTO [osdu].[InventorySelection] ([PartitionId], [InventoryId], [RunId], [Actor], [ConditionsJson], [ConditionsKey], [FiltersJson], [Status],
                [Query], [Direction], [NotesJson], [ColumnsJson], [BuildRunId], [Candidates], [ToCheck], [Checked], [Matched], [Outside], [Changed],
                [Requests], [CreatedUtc], [StartedUtc], [CompletedUtc], [Error], [DuplicateCheckId], [DuplicateKeyJson])
        SELECT @partitionId, @inventoryId, c.[RunId], @actor, N'[]', @conditionsKey, @filtersJson, N'completed',
            NULL, NULL, NULL, @columnsJson, @build, c.[Candidates], 0, 0, 0, 0, 0,
            0, @now, @now, @now, NULL, @checkId, @key
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
    /// Keeps a downloaded page in the check's table, in one transaction: each record the search returned that the inventory serves
    /// a row of, once, with the version the index holds and what it holds at each property; how many records the inventory holds
    /// no row of, and how many rows are at another version than it listed; and the check's progress.
    /// </summary>
    public static Task<InventoryDuplicateRowsWrite> AddInventoryDuplicateRowsAsync(
        OsduDbContext db, short partitionId, int inventoryId, long checkId, IReadOnlyList<DuplicateColumn> attributes, IReadOnlyList<InventoryDuplicateRow> rows,
        long checkedCount, long requests, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            if (rows.Count == 0)
            {
                await using var progress = Command(connection, transaction, DuplicateProgressSql, slice: null);
                AddDuplicateParameters(progress, partitionId, checkId);
                AddProgressParameters(progress, checkedCount, requests);
                await progress.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return new InventoryDuplicateRowsWrite(0, 0, 0);
            }

            await ExecuteAsync(connection, transaction, RowsStageSql(attributes), ct).ConfigureAwait(false);
            using (var table = new DataTable())
            {
                table.Columns.Add("TargetId", typeof(string));
                table.Columns.Add("Version", typeof(long));
                foreach (var attribute in attributes)
                {
                    table.Columns.Add(attribute.Column, typeof(string));
                    table.Columns.Add(InventoryDuplicateTable.HashColumn(attribute.Column), typeof(byte[]));
                }

                foreach (var row in rows)
                {
                    var values = new object[2 + (attributes.Count * 2)];
                    values[0] = row.TargetId;
                    values[1] = Value(row.Version);
                    for (var i = 0; i < attributes.Count; i++)
                    {
                        var held = row.Values[i];
                        values[2 + (i * 2)] = Value(held?.ValuesJson);
                        values[3 + (i * 2)] = held?.Hash is { } hash ? hash : DBNull.Value;
                    }

                    table.Rows.Add(values);
                }

                await BulkCopyAsync(connection, transaction, "#DupRows", table, ct).ConfigureAwait(false);
            }

            await using var command = Command(connection, transaction, RowsSql(InventoryDuplicateTable.NameOf(checkId), attributes), slice: null);
            AddDuplicateParameters(command, partitionId, checkId);
            command.Parameters.Add(new SqlParameter("@inventoryId", SqlDbType.Int) { Value = inventoryId });
            AddProgressParameters(command, checkedCount, requests);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException("A write of the duplicate check's download answered nothing.");
            }

            return new InventoryDuplicateRowsWrite(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }, ct);

    /// <summary>
    /// Groups a check's table by a key, over whatever it was grouped by before, in one transaction (<see cref="GroupSql"/>), then
    /// gives each copy what it holds at the key's properties, fitted as a selection's row keeps values, and then at the check's
    /// other properties (<paramref name="others"/>) as room allows, so a group shows how its copies differ outside the key; a
    /// chunk at a time; and each group the values of its first copy. Every property of the key is one the check downloaded.
    /// </summary>
    public static Task<InventoryDuplicateGrouping> GroupInventoryDuplicatesAsync(
        OsduDbContext db, short partitionId, long checkId, IReadOnlyList<DuplicateColumn> keyed, IReadOnlyList<DuplicateColumn> others, IReadOnlyList<string> hashColumns,
        InventoryDuplicateKeying keying, int maxCopies, int batch, CancellationToken ct)
        => InTransactionAsync(db, async (connection, transaction) =>
        {
            var table = InventoryDuplicateTable.NameOf(checkId);
            InventoryDuplicateGrouping grouping;
            await using (var command = Command(connection, transaction, GroupSql(table, hashColumns), slice: null))
            {
                AddDuplicateParameters(command, partitionId, checkId);
                command.Parameters.Add(new SqlParameter("@absent", SqlDbType.Binary, InventoryDuplicateTable.HashLength) { Value = AbsentHash });
                command.Parameters.Add(new SqlParameter("@maxCopies", SqlDbType.Int) { Value = maxCopies });
                command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = batch });
                command.Parameters.Add(new SqlParameter("@keyJson", SqlDbType.NVarChar, -1) { Value = keying.KeyJson });
                command.Parameters.Add(new SqlParameter("@keyHash", SqlDbType.VarChar, DeliveryModel.SelectionKeyLength) { Value = keying.KeyHash });
                command.Parameters.Add(new SqlParameter("@actor", SqlDbType.NVarChar, 200) { Value = Truncate(keying.Actor, 200)! });
                command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = keying.NowUtc });
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    throw new DeliveryException("The grouping of the duplicate check's table answered nothing.");
                }

                grouping = new InventoryDuplicateGrouping(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
            }

            var read = keyed.Concat(others).ToList();
            long after = 0;
            while (true)
            {
                var chunk = new List<(long Id, string? Values)>();
                await using (var command = Command(connection, transaction, CopyValuesSql(table, read), slice: null))
                {
                    AddDuplicateParameters(command, partitionId, checkId);
                    command.Parameters.Add(new SqlParameter("@chunk", SqlDbType.Int) { Value = ValuesChunk });
                    command.Parameters.Add(new SqlParameter("@after", SqlDbType.BigInt) { Value = after });
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var held = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                        var rest = new List<(string Path, List<string> Values)>();
                        for (var i = 0; i < read.Count; i++)
                        {
                            if (!reader.IsDBNull(i + 1) && ValuesOf(reader.GetString(i + 1)) is { Count: > 0 } values)
                            {
                                if (i < keyed.Count)
                                {
                                    held[read[i].Path] = values;
                                }
                                else
                                {
                                    rest.Add((read[i].Path, values));
                                }
                            }
                        }

                        chunk.Add((reader.GetInt64(0), CopyValues(held, rest)));
                    }
                }

                if (chunk.Count == 0)
                {
                    break;
                }

                after = chunk[^1].Id;
                await ExecuteAsync(connection, transaction, CopyValuesStageSql, ct).ConfigureAwait(false);
                using (var values = new DataTable())
                {
                    values.Columns.Add("InventoryRecordId", typeof(long));
                    values.Columns.Add("ValuesJson", typeof(string));
                    foreach (var (id, json) in chunk)
                    {
                        values.Rows.Add(id, Value(json));
                    }

                    await BulkCopyAsync(connection, transaction, "#CopyValues", values, ct).ConfigureAwait(false);
                }

                await using var write = Command(connection, transaction, CopyValuesWriteSql, slice: null);
                AddDuplicateParameters(write, partitionId, checkId);
                await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (chunk.Count < ValuesChunk)
                {
                    break;
                }
            }

            await using (var groups = Command(connection, transaction, GroupValuesSql, slice: null))
            {
                AddDuplicateParameters(groups, partitionId, checkId);
                await groups.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return grouping;
        }, ct);

    /// <summary>
    /// What a copy's row keeps of it: its values at the key fitted as a selection's row keeps values (<see cref="InventorySelectionValues.Fit"/>),
    /// then each other property the check downloaded, whole, in the check's order, while the row still fits; a property that does
    /// not fit is left out, and the next is tried.
    /// </summary>
    private static string? CopyValues(Dictionary<string, List<string>> keyed, IReadOnlyList<(string Path, List<string> Values)> rest)
    {
        var json = InventorySelectionValues.Fit(keyed);
        foreach (var (path, values) in rest)
        {
            keyed[path] = values;
            var wider = System.Text.Json.JsonSerializer.Serialize(keyed);
            if (wider.Length <= DeliveryModel.MaxSelectionValuesLength)
            {
                json = wider;
            }
            else
            {
                keyed.Remove(path);
            }
        }

        return json;
    }

    /// <summary>The values a property's column of a check's table holds: a JSON list of text, as the download wrote it; none for anything else.</summary>
    private static List<string>? ValuesOf(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string?>>(json)?.OfType<string>().ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            // A column a person changed by hand into something else says nothing a copy's row can show.
            return null;
        }
    }

    /// <summary>
    /// Writes how the groups came out over what they held before, in one transaction, and moves the check's counts by the
    /// difference: a pick's rounds (<see cref="DuplicateChoice.None"/>), an operator's choice of the copy a group keeps,
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
    /// check read, each id beside the copy its group keeps, the key its groups were formed by kept with it. Returns the selection's number.
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
