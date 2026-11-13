using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Core;
using SqlFlow.Core.Data;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.Catalog;
using SqlFlow.SqlServer.Schema;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Writes a completed dimension build in one transaction (osdu/docs/reference/concepts/dimension-tables.md): what the
/// build staged as it read (<see cref="DimensionStage"/>) is copied into temporary tables where the database holds it,
/// and set-based statements add what is new, change
/// what changed, mark removed what the build no longer found and bring back what it found again, log every change to an
/// original, rewrite the attributes and the records read through of the originals it found, bring the dimension's own
/// table to what the build found, and close the build with its counts. An incremental load's write is partial: it holds
/// the keys it read again and the values they were and are under, every other key and value stays as it is, and nothing
/// is marked removed but a value left with no key. A reader sees the dimension as one build left it, never half of the
/// next. An application lock per dimension keeps two builds of one dimension from writing at once; a build of another
/// dimension writes beside it, since the tables keep each dimension's rows together (osdu/docs/reference/concepts/database.md,
/// How the tables are keyed) and every statement names its dimension, joins included, so a write reads and locks its own
/// rows alone.
/// </summary>
internal static partial class SqlServerDimensionStore
{
    /// <summary>How long one statement reading a dimension's table, or making it ready, may run.</summary>
    private const int CommandTimeoutSeconds = 900;

    /// <summary>
    /// How long a statement or a copy of a write, or of a removal, may run: as long as its rows take (0). A write grows with
    /// its dimension (one of well log curves stages a row for every field of every curve, tens of millions of them), so
    /// no fixed bound suits every dimension, and one too short fails a build that was only large. As SQLFlow's own loads
    /// are, it is bounded by its run instead, whose cancellation stops the statement running; the wait for another write
    /// of the dimension is bounded by the write lock's own timeout.
    /// </summary>
    private const int WriteTimeoutSeconds = 0;

    /// <summary>How long a write waits for another build of the same dimension to finish writing.</summary>
    private const int LockTimeoutMs = 120_000;

    /// <summary>How many times a write the database chose as a deadlock victim is made again.</summary>
    private const int DeadlockAttempts = 3;

    private const string StageSql = """
        CREATE TABLE #DimValue (
            [Seq] int NOT NULL,
            [Original] nvarchar(1024) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [OriginalHash] binary(32) NOT NULL PRIMARY KEY,
            [CleanValue] nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
            [LeftOut] nvarchar(16) NULL,
            [Note] nvarchar(400) NULL,
            [Label] nvarchar(1024) NULL,
            [LabelFrom] nvarchar(1024) NULL,
            [Filter] nvarchar(4000) NULL,
            [Count] bigint NOT NULL,
            [Filterable] bit NOT NULL,
            [MemberId] bigint NULL);
        CREATE TABLE #DimMember (
            [Value] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            [Records] bigint NOT NULL,
            [RecordsExact] bit NOT NULL,
            [Originals] int NOT NULL,
            [Unfilterable] int NOT NULL,
            [Filter] nvarchar(max) NULL,
            [FilterParts] int NOT NULL);
        CREATE TABLE #DimChange (
            [ValueId] bigint NOT NULL,
            [Change] nvarchar(16) NOT NULL,
            [FromMemberId] bigint NULL,
            [ToMemberId] bigint NULL);
        CREATE TABLE #DimAttr (
            [OriginalHash] binary(32) NOT NULL,
            [Name] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Value] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [ValueFrom] nvarchar(1024) NULL,
            [Records] bigint NULL);
        CREATE TABLE #DimAttrKeyed (
            [ValueId] bigint NOT NULL,
            [AttributeId] int NOT NULL,
            [Value] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [ValueFrom] nvarchar(1024) NULL,
            [Records] bigint NULL,
            PRIMARY KEY ([ValueId], [AttributeId], [Value]));
        CREATE TABLE #DimText (
            [Name] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [TextHash] binary(32) NOT NULL,
            [Text] nvarchar(1024) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Value] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Records] bigint NOT NULL,
            PRIMARY KEY ([Name], [TextHash]));
        CREATE TABLE #DimElem (
            [OriginalHash] binary(32) NOT NULL,
            [Seq] int NOT NULL,
            [Name] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Value] nvarchar(4000) COLLATE Latin1_General_100_BIN2 NULL,
            PRIMARY KEY ([OriginalHash], [Seq], [Name]));
        CREATE TABLE #DimElemKeyed (
            [ValueId] bigint NOT NULL,
            [Seq] int NOT NULL,
            [AttributeId] int NOT NULL,
            [Value] nvarchar(4000) COLLATE Latin1_General_100_BIN2 NULL,
            PRIMARY KEY ([ValueId], [Seq], [AttributeId]));
        CREATE TABLE #DimRead (
            [OriginalHash] binary(32) NOT NULL,
            [RecordHash] binary(32) NOT NULL,
            [EntityType] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PRIMARY KEY ([OriginalHash], [RecordHash]));
        CREATE TABLE #DimReadKeyed (
            [ValueId] bigint NOT NULL,
            [RecordHash] binary(32) NOT NULL,
            [EntityType] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PRIMARY KEY ([ValueId], [RecordHash]));
        CREATE TABLE #DimGone (
            [OriginalHash] binary(32) NOT NULL PRIMARY KEY);
        CREATE TABLE #DimRecRead (
            [RecordHash] binary(32) NOT NULL PRIMARY KEY);
        CREATE TABLE #DimRecHeld (
            [RecordHash] binary(32) NOT NULL,
            [OriginalHash] binary(32) NOT NULL,
            PRIMARY KEY ([RecordHash], [OriginalHash]));
        CREATE TABLE #DimRecKeyed (
            [RecordHash] binary(32) NOT NULL,
            [ValueId] bigint NOT NULL,
            PRIMARY KEY ([RecordHash], [ValueId]));
        """ + "\n" + NameStageSql;

    // The attributes the dimension declares, each with its place among them: what a write that knows the declaration
    // (it carries the table) stages, so every attribute has its number and its place among the dimension's columns.
    private const string NameStageSql = """
        CREATE TABLE #DimName (
            [Name] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            [Ordinal] smallint NULL,
            [Collected] bit NOT NULL,
            [AttributeId] int NULL);
        """;

    private const string LockSql = """
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout;
        SELECT @granted;
        """;

    // The dimension's attributes, each under its number. A write that knows the declaration (it carries the table) gives
    // each declared attribute its place, and takes the place of one the dimension no longer declares; an attribute keeps
    // its number through all of it. The attribute that made the table's rows before the write is remembered first: when
    // the write makes them by another, no row the table holds can be matched, and they are written again.
    private const string NamesSql = """
        DECLARE @collectedWas nvarchar(64) = (
            SELECT TOP (1) n.[Name] FROM [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK)
            WHERE n.[PartitionId] = @p AND n.[DimensionId] = @d AND n.[Ordinal] IS NOT NULL AND n.[Collected] = 1
            ORDER BY n.[Ordinal]);

        INSERT INTO [osdu].[DimensionAttributeName] ([PartitionId], [DimensionId], [Name], [Ordinal], [Collected])
        SELECT @p, @d, s.[Name], NULL, s.[Collected]
        FROM #DimName AS s
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK) WHERE n.[PartitionId] = @p AND n.[DimensionId] = @d AND n.[Name] = s.[Name])
        ORDER BY ISNULL(s.[Ordinal], 32767), s.[Name];

        IF @tableName IS NOT NULL
        BEGIN
            UPDATE n SET n.[Ordinal] = s.[Ordinal], n.[Collected] = ISNULL(s.[Collected], n.[Collected])
            FROM [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK)
            LEFT JOIN #DimName AS s ON s.[Name] = n.[Name]
            WHERE n.[PartitionId] = @p AND n.[DimensionId] = @d
              AND (ISNULL(n.[Ordinal], 0) <> ISNULL(s.[Ordinal], 0) OR n.[Collected] <> ISNULL(s.[Collected], n.[Collected]));
        END;
        """;

    private static string Slots(Func<string, string> each, string between)
        => string.Join(between, Enumerable.Range(1, DimensionTables.Slots).Select(i => each(DimensionTables.Slot(i))));

    // The tables a write lays the dimension's table out in: made on the write's session in a statement of their own before
    // its transaction, so they outlive the merge for the table step that follows it, which reads them on the same session
    // or copies them to the database the table is kept in.
    private static readonly string LayoutTablesSql = $"""
        DROP TABLE IF EXISTS #DimScope;
        CREATE TABLE #DimScope ([ValueId] bigint NOT NULL PRIMARY KEY);
        DROP TABLE IF EXISTS #DimRow;
        CREATE TABLE #DimRow (
            [ValueId] bigint NOT NULL,
            [Part] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Element] int NULL,
            [Key] nvarchar(1024) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Value] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            {Slots(slot => $"[{slot}] nvarchar(4000) COLLATE Latin1_General_100_BIN2 NULL,", "\n    ")}
            [Records] bigint NOT NULL,
            [Filter] nvarchar(4000) COLLATE Latin1_General_100_BIN2 NULL,
            PRIMARY KEY ([ValueId], [Part]));
        """;

    // The dimension laid out as its table holds it (osdu/docs/reference/flow/dimension-table.md, The dimension's table): a
    // row per key the dimension holds now and value it collects, each attribute in the column of its place, read from
    // what the write has just kept and joined by the attributes' numbers (DimensionTables.RowsSql, made for the table's
    // own columns, so a dimension of a few attributes lays out those alone), into #DimRow. An incremental load's write
    // changes the rows of the keys it read again and of those it removed, and no other (its every statement above reads
    // #DimValue and #DimChange alone), so it lays out and compares those keys' rows alone (#DimScope): its table write
    // takes as long as what changed, not as the dimension. A table whose rows are written anew (@rewrite: the attribute
    // that makes its rows changed, or rows made per element are held where the declaration reads none, which the write
    // checked on the table before it began, @elementRows), or a table behind the dimension (@behind: the write before
    // did not reach it, or it moves to another database or schema), is laid out whole.
    private const string RowsSql = """
        DECLARE @collectedId int, @collectedOrdinal smallint, @collectedNow nvarchar(64);
        SELECT TOP (1) @collectedId = n.[AttributeId], @collectedOrdinal = n.[Ordinal], @collectedNow = n.[Name]
        FROM [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK)
        WHERE n.[PartitionId] = @p AND n.[DimensionId] = @d AND n.[Ordinal] IS NOT NULL AND n.[Collected] = 1
        ORDER BY n.[Ordinal];

        DECLARE @rewrite bit = CASE WHEN ISNULL(@collectedWas, N'') <> ISNULL(@collectedNow, N'') OR @elementRows = 1 THEN 1 ELSE 0 END;
        DECLARE @scoped bit = CASE WHEN @partial = 1 AND @rewrite = 0 AND @behind = 0 THEN 1 ELSE 0 END;
        DELETE FROM #DimScope;
        IF @scoped = 1
            INSERT INTO #DimScope ([ValueId])
            SELECT v.[ValueId]
            FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
            INNER JOIN #DimValue AS s ON s.[OriginalHash] = v.[OriginalHash]
            WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d
            UNION
            SELECT c.[ValueId] FROM #DimChange AS c;

        DELETE FROM #DimRow;
        EXEC sys.sp_executesql @rowsSql, N'@p smallint, @d int, @elements bit, @collectedId int, @collectedOrdinal smallint, @scoped bit',
            @p = @p, @d = @d, @elements = @elements, @collectedId = @collectedId, @collectedOrdinal = @collectedOrdinal, @scoped = @scoped;
        """;

    // The dimension's table laid out from what the write kept, when the write carries the table's declaration; the table
    // step after the write's transaction brings the table to it (WriteTableAsync), in the database the table is kept in.
    private const string LayoutSql = """
        IF @tableName IS NOT NULL
        BEGIN

        """ + RowsSql + "\n" + """
        END;
        """;

    // The table laid out again outside a build: the same steps as a build's write, under the dimension's write lock, from
    // the attributes the declaration names, every key's.
    private const string EnsureTableSql = "SET NOCOUNT ON;\nDECLARE @behind bit = 1;\n" + NamesSql + "\n\n" + LayoutSql
        + "\nSELECT ISNULL(@rewrite, CAST(0 AS bit)), ISNULL(@scoped, CAST(0 AS bit));";

    // What the ledger records of the table once the table step has written it: its name, a version raised when its rows
    // changed, its name or where it is kept changed, the build whose rows it holds (the last build, for a write outside one),
    // and where it is kept; and the table the dimension wrote under another name in the same place before, which goes once
    // no dimension names it.
    private const string RecordTableSql = """
        SET NOCOUNT ON;
        DECLARE @tableWas sysname, @sameTarget bit = 0;
        SELECT @tableWas = d.[TableName],
            @sameTarget = CASE WHEN ISNULL(d.[TableConnection], N'') = ISNULL(@tableConnection, N'')
                AND ISNULL(d.[TableDatabase], N'') = ISNULL(@tableDatabase, N'')
                AND ISNULL(d.[TableSchema], N'osdu') = @tableSchema THEN 1 ELSE 0 END
        FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
        WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d;

        UPDATE d SET d.[TableName] = @tableName,
            d.[TableVersion] = d.[TableVersion] + CASE WHEN @changed > 0 OR d.[TableName] IS NULL OR d.[TableName] <> @tableName OR @sameTarget = 0 THEN 1 ELSE 0 END,
            d.[TableRunId] = ISNULL(@runId, d.[LastRunId]),
            d.[TableConnection] = @tableConnection, d.[TableDatabase] = @tableDatabase, d.[TableSchema] = NULLIF(@tableSchema, N'osdu')
        FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
        WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d;

        SELECT CASE WHEN @tableWas IS NOT NULL AND @tableWas <> @tableName AND @sameTarget = 1
            AND NOT EXISTS (SELECT 1 FROM [osdu].[Dimension] AS d WITH (FORCESEEK) WHERE d.[TableName] = @tableWas) THEN @tableWas END;
        """;

    // The whole merge, in the order that keeps every step's reads true: members first, so each original can be given the id
    // of its member; then the originals, whose changes are gathered as they are made; then the log and the counts. A
    // partial write (an incremental load's) removes no original, and of the values only one it left with no original.
    private static readonly string MergeSql = """
        SET NOCOUNT ON;

        -- The table is behind the dimension when the write before did not reach it, or it moves to another database or
        -- schema: it is then laid out whole. Read before the dimension's row names this build.
        DECLARE @behind bit = CASE WHEN EXISTS (
            SELECT 1 FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
            WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d
              AND d.[TableRunId] IS NOT NULL AND d.[TableRunId] = d.[LastRunId] AND d.[TableName] = @tableName
              AND ISNULL(d.[TableConnection], N'') = ISNULL(@tableConnection, N'')
              AND ISNULL(d.[TableDatabase], N'') = ISNULL(@tableDatabase, N'')
              AND ISNULL(d.[TableSchema], N'osdu') = @tableSchema) THEN 0 ELSE 1 END;
        DECLARE @firstBuild bit = CASE WHEN EXISTS (
            SELECT 1 FROM [osdu].[Dimension] AS d WITH (FORCESEEK) WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d AND d.[LastRunId] IS NOT NULL) THEN 0 ELSE 1 END;
        DECLARE @membersAdded bigint, @membersRemoved bigint = 0, @membersRestored bigint;

        SELECT @membersRestored = COUNT_BIG(*)
        FROM [osdu].[DimensionMember] AS m WITH (FORCESEEK) INNER JOIN #DimMember AS s ON m.[Value] = s.[Value]
        WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[RemovedRunId] IS NOT NULL;

        -- A member is rewritten only when something of it changed, so a build that found the same thing writes nothing.
        UPDATE m SET m.[Records] = s.[Records], m.[RecordsExact] = s.[RecordsExact], m.[Originals] = s.[Originals],
            m.[Unfilterable] = s.[Unfilterable], m.[Filter] = s.[Filter], m.[FilterParts] = s.[FilterParts],
            m.[RemovedRunId] = NULL, m.[RemovedUtc] = NULL
        FROM [osdu].[DimensionMember] AS m WITH (FORCESEEK) INNER JOIN #DimMember AS s ON m.[Value] = s.[Value]
        WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d
          AND (m.[RemovedRunId] IS NOT NULL OR m.[Records] <> s.[Records] OR m.[RecordsExact] <> s.[RecordsExact]
               OR m.[Originals] <> s.[Originals] OR m.[Unfilterable] <> s.[Unfilterable] OR m.[FilterParts] <> s.[FilterParts]
               OR ISNULL(m.[Filter], N'') <> ISNULL(s.[Filter], N'') OR (m.[Filter] IS NULL AND s.[Filter] IS NOT NULL)
               OR (m.[Filter] IS NOT NULL AND s.[Filter] IS NULL));

        INSERT INTO [osdu].[DimensionMember] ([PartitionId], [DimensionId], [Value], [Records], [RecordsExact], [Originals], [Unfilterable],
            [Filter], [FilterParts], [FirstSeenRunId], [FirstSeenUtc])
        SELECT @p, @d, s.[Value], s.[Records], s.[RecordsExact], s.[Originals], s.[Unfilterable], s.[Filter], s.[FilterParts], @run, @now
        FROM #DimMember AS s
        WHERE NOT EXISTS (SELECT 1 FROM [osdu].[DimensionMember] AS m WITH (FORCESEEK) WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[Value] = s.[Value]);
        SET @membersAdded = @@ROWCOUNT;

        IF @partial = 0
        BEGIN
            UPDATE m SET m.[RemovedRunId] = @run, m.[RemovedUtc] = @now
            FROM [osdu].[DimensionMember] AS m WITH (FORCESEEK)
            WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[RemovedRunId] IS NULL
              AND NOT EXISTS (SELECT 1 FROM #DimMember AS s WHERE s.[Value] = m.[Value]);
            SET @membersRemoved = @@ROWCOUNT;
        END;

        UPDATE s SET s.[MemberId] = m.[MemberId]
        FROM #DimValue AS s INNER JOIN [osdu].[DimensionMember] AS m WITH (FORCESEEK)
            ON m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[Value] = s.[CleanValue];

        -- An original found again after a build that did not find it is restored; one under another member (or under none
        -- where it had one, or one where it had none) moved.
        INSERT INTO #DimChange ([ValueId], [Change], [FromMemberId], [ToMemberId])
        SELECT v.[ValueId], CASE WHEN v.[RemovedRunId] IS NOT NULL THEN N'restored' ELSE N'moved' END, v.[MemberId], s.[MemberId]
        FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK) INNER JOIN #DimValue AS s ON v.[OriginalHash] = s.[OriginalHash]
        WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d
          AND (v.[RemovedRunId] IS NOT NULL OR ISNULL(v.[MemberId], -1) <> ISNULL(s.[MemberId], -1));

        UPDATE v SET v.[MemberSinceRunId] = CASE WHEN v.[RemovedRunId] IS NOT NULL OR ISNULL(v.[MemberId], -1) <> ISNULL(s.[MemberId], -1)
                THEN @run ELSE v.[MemberSinceRunId] END,
            v.[MemberId] = s.[MemberId], v.[LeftOut] = s.[LeftOut], v.[Note] = s.[Note], v.[Count] = s.[Count], v.[Filterable] = s.[Filterable],
            v.[Label] = s.[Label], v.[LabelFrom] = s.[LabelFrom], v.[Filter] = s.[Filter],
            v.[RemovedRunId] = NULL, v.[RemovedUtc] = NULL
        FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK) INNER JOIN #DimValue AS s ON v.[OriginalHash] = s.[OriginalHash]
        WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d
          AND (v.[RemovedRunId] IS NOT NULL OR ISNULL(v.[MemberId], -1) <> ISNULL(s.[MemberId], -1) OR v.[Count] <> s.[Count]
               OR v.[Filterable] <> s.[Filterable] OR ISNULL(v.[LeftOut], N'') <> ISNULL(s.[LeftOut], N'')
               OR ISNULL(v.[Note], N'') <> ISNULL(s.[Note], N'')
               -- A label compares exactly: a record renamed only in case is a new label.
               OR ISNULL(v.[Label], N'') COLLATE Latin1_General_100_BIN2 <> ISNULL(s.[Label], N'') COLLATE Latin1_General_100_BIN2
               OR (v.[Label] IS NULL AND s.[Label] IS NOT NULL) OR (v.[Label] IS NOT NULL AND s.[Label] IS NULL)
               OR ISNULL(v.[LabelFrom], N'') <> ISNULL(s.[LabelFrom], N'')
               OR ISNULL(v.[Filter], N'') <> ISNULL(s.[Filter], N''));

        INSERT INTO [osdu].[DimensionValue] ([PartitionId], [DimensionId], [Original], [OriginalHash], [MemberId], [LeftOut], [Note], [Count],
            [Filterable], [Label], [LabelFrom], [Filter], [FirstSeenRunId], [FirstSeenUtc], [MemberSinceRunId])
        OUTPUT inserted.[ValueId], N'added', NULL, inserted.[MemberId] INTO #DimChange ([ValueId], [Change], [FromMemberId], [ToMemberId])
        SELECT @p, @d, s.[Original], s.[OriginalHash], s.[MemberId], s.[LeftOut], s.[Note], s.[Count], s.[Filterable], s.[Label], s.[LabelFrom],
            s.[Filter], @run, @now, @run
        FROM #DimValue AS s
        WHERE NOT EXISTS (SELECT 1 FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK) WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[OriginalHash] = s.[OriginalHash])
        -- The new originals take their ids in the order the build listed them, so arrival order within a build is the build's.
        ORDER BY s.[Seq];

        IF @partial = 0
        BEGIN
            UPDATE v SET v.[RemovedRunId] = @run, v.[RemovedUtc] = @now
            OUTPUT inserted.[ValueId], N'removed', deleted.[MemberId], NULL INTO #DimChange ([ValueId], [Change], [FromMemberId], [ToMemberId])
            FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
            WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[RemovedRunId] IS NULL
              AND NOT EXISTS (SELECT 1 FROM #DimValue AS s WHERE s.[OriginalHash] = v.[OriginalHash]);
        END
        ELSE
        BEGIN
            -- The keys an incremental load read again and found held by no record are removed, as a full load removes a key
            -- it no longer finds.
            UPDATE v SET v.[RemovedRunId] = @run, v.[RemovedUtc] = @now
            OUTPUT inserted.[ValueId], N'removed', deleted.[MemberId], NULL INTO #DimChange ([ValueId], [Change], [FromMemberId], [ToMemberId])
            FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
            INNER JOIN #DimGone AS g ON g.[OriginalHash] = v.[OriginalHash]
            WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[RemovedRunId] IS NULL;

            -- A value every original it held left (each read again and now under another, or under none, or removed) holds
            -- nothing; one an original still holds stays, though the write did not hold it.
            UPDATE m SET m.[RemovedRunId] = @run, m.[RemovedUtc] = @now
            FROM [osdu].[DimensionMember] AS m WITH (FORCESEEK)
            WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[RemovedRunId] IS NULL
              AND m.[MemberId] IN (SELECT c.[FromMemberId] FROM #DimChange AS c WHERE c.[FromMemberId] IS NOT NULL)
              AND NOT EXISTS (SELECT 1 FROM #DimMember AS s WHERE s.[Value] = m.[Value])
              AND NOT EXISTS (
                  SELECT 1 FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
                  WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[MemberId] = m.[MemberId] AND v.[RemovedRunId] IS NULL);
            SET @membersRemoved = @@ROWCOUNT;
        END;

        -- Every attribute the build read has its number: an attribute's values are kept and joined by it, never by its
        -- name. A name read that the declaration does not hold (a write that carries none) still gets one.
        INSERT INTO #DimName ([Name], [Ordinal], [Collected])
        SELECT t.[Name], NULL, t.[Collected]
        FROM (SELECT [Name], CAST(MAX(CASE WHEN [Records] IS NULL THEN 0 ELSE 1 END) AS bit) AS [Collected] FROM #DimAttr GROUP BY [Name]
              UNION
              SELECT [Name], CAST(1 AS bit) FROM #DimText WHERE [Name] NOT IN (SELECT [Name] FROM #DimAttr)) AS t
        WHERE NOT EXISTS (SELECT 1 FROM #DimName AS s WHERE s.[Name] = t.[Name]);

        """ + "\n" + NamesSql + "\n\n" + """
        -- The numbers are looked up once, beside the few names, and every row below is joined to them: a row of the
        -- build is never rewritten to carry one.
        UPDATE s SET s.[AttributeId] = n.[AttributeId]
        FROM #DimName AS s INNER JOIN [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK) ON n.[PartitionId] = @p AND n.[DimensionId] = @d AND n.[Name] = s.[Name];

        -- Each attribute value the build read, under the numbers it is kept by: its key's and its attribute's. The rows
        -- kept are then matched to them on those numbers and the value, which name one row on either side. Matched
        -- through the key's hash and the attribute's name instead, the only thing the two sides share directly is the
        -- value, and the server is free to pair them by it: every key holding a value (a country) with every other key
        -- holding it, which is millions of pairs for a few thousand keys.
        INSERT INTO #DimAttrKeyed ([ValueId], [AttributeId], [Value], [ValueFrom], [Records])
        SELECT v.[ValueId], dn.[AttributeId], t.[Value], t.[ValueFrom], t.[Records]
        FROM #DimAttr AS t
        INNER JOIN #DimName AS dn ON dn.[Name] = t.[Name]
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[OriginalHash] = t.[OriginalHash];

        -- The attribute values of every original the build found are what it read: one no longer read is dropped, one read
        -- again rewritten only when where it was read or how many records hold it changed, and a new one added. An original
        -- the build did not find keeps what its last build read.
        DELETE a
        FROM [osdu].[DimensionAttribute] AS a WITH (FORCESEEK)
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = a.[ValueId]
        INNER JOIN #DimValue AS s ON s.[OriginalHash] = v.[OriginalHash]
        WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d
          AND NOT EXISTS (
              SELECT 1 FROM #DimAttrKeyed AS t
              WHERE t.[ValueId] = a.[ValueId] AND t.[AttributeId] = a.[AttributeId] AND t.[Value] = a.[Value]);
        DECLARE @attributesChanged bigint = @@ROWCOUNT;

        -- Where a value was read is compared exactly, byte for byte, as an id is.
        UPDATE a SET a.[ValueFrom] = t.[ValueFrom], a.[Records] = t.[Records]
        FROM [osdu].[DimensionAttribute] AS a WITH (FORCESEEK)
        INNER JOIN #DimAttrKeyed AS t ON t.[ValueId] = a.[ValueId] AND t.[AttributeId] = a.[AttributeId] AND t.[Value] = a.[Value]
        WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d
          AND (ISNULL(a.[ValueFrom], N'') COLLATE Latin1_General_100_BIN2 <> ISNULL(t.[ValueFrom], N'') COLLATE Latin1_General_100_BIN2
               OR (a.[ValueFrom] IS NULL AND t.[ValueFrom] IS NOT NULL)
               OR (a.[ValueFrom] IS NOT NULL AND t.[ValueFrom] IS NULL)
               OR ISNULL(a.[Records], -1) <> ISNULL(t.[Records], -1));
        SET @attributesChanged += @@ROWCOUNT;

        INSERT INTO [osdu].[DimensionAttribute] ([PartitionId], [DimensionId], [ValueId], [AttributeId], [Value], [ValueFrom], [Records])
        SELECT @p, @d, t.[ValueId], t.[AttributeId], t.[Value], t.[ValueFrom], t.[Records]
        FROM #DimAttrKeyed AS t
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[DimensionAttribute] AS a WITH (FORCESEEK)
            WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d AND a.[ValueId] = t.[ValueId] AND a.[AttributeId] = t.[AttributeId] AND a.[Value] = t.[Value])
        ORDER BY t.[ValueId], t.[AttributeId], t.[Value];
        SET @attributesChanged += @@ROWCOUNT;

        -- The elements of every key the build found are what it read, matched on the key, the element's place and the
        -- field: one no longer read is dropped, one read otherwise rewritten, a new one added. A declaration that reads no
        -- elements keeps none of them. A write that carries no declaration leaves them as they are.
        IF @tableName IS NOT NULL
        BEGIN
            INSERT INTO #DimElemKeyed ([ValueId], [Seq], [AttributeId], [Value])
            SELECT v.[ValueId], t.[Seq], dn.[AttributeId], t.[Value]
            FROM #DimElem AS t
            INNER JOIN #DimName AS dn ON dn.[Name] = t.[Name]
            INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[OriginalHash] = t.[OriginalHash];

            IF @elements = 0
                DELETE e FROM [osdu].[DimensionElement] AS e WITH (FORCESEEK) WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d;
            ELSE
            BEGIN
                DELETE e
                FROM [osdu].[DimensionElement] AS e WITH (FORCESEEK)
                INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = e.[ValueId]
                INNER JOIN #DimValue AS s ON s.[OriginalHash] = v.[OriginalHash]
                WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d
                  AND NOT EXISTS (
                      SELECT 1 FROM #DimElemKeyed AS t
                      WHERE t.[ValueId] = e.[ValueId] AND t.[Seq] = e.[Seq] AND t.[AttributeId] = e.[AttributeId]);

                UPDATE e SET e.[Value] = t.[Value]
                FROM [osdu].[DimensionElement] AS e WITH (FORCESEEK)
                INNER JOIN #DimElemKeyed AS t ON t.[ValueId] = e.[ValueId] AND t.[Seq] = e.[Seq] AND t.[AttributeId] = e.[AttributeId]
                WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d
                  AND (ISNULL(e.[Value], N'') COLLATE Latin1_General_100_BIN2 <> ISNULL(t.[Value], N'') COLLATE Latin1_General_100_BIN2
                       OR (e.[Value] IS NULL AND t.[Value] IS NOT NULL)
                       OR (e.[Value] IS NOT NULL AND t.[Value] IS NULL));

                INSERT INTO [osdu].[DimensionElement] ([PartitionId], [DimensionId], [ValueId], [Seq], [AttributeId], [Value])
                SELECT @p, @d, t.[ValueId], t.[Seq], t.[AttributeId], t.[Value]
                FROM #DimElemKeyed AS t
                WHERE NOT EXISTS (
                    SELECT 1 FROM [osdu].[DimensionElement] AS e WITH (FORCESEEK)
                    WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d AND e.[ValueId] = t.[ValueId] AND e.[Seq] = t.[Seq] AND e.[AttributeId] = t.[AttributeId])
                ORDER BY t.[ValueId], t.[Seq], t.[AttributeId];
            END;
        END;

        -- The records each key the write holds was read through are what it read, matched on the key and the record: one no
        -- longer read is dropped and a new one added. A key a load removes keeps none, since no incremental load reads it
        -- again for a record it was read through.
        INSERT INTO #DimReadKeyed ([ValueId], [RecordHash], [EntityType])
        SELECT v.[ValueId], r.[RecordHash], r.[EntityType]
        FROM #DimRead AS r
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[OriginalHash] = r.[OriginalHash];

        DELETE k
        FROM [osdu].[DimensionKeyRecord] AS k WITH (FORCESEEK)
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = k.[ValueId]
        INNER JOIN #DimValue AS s ON s.[OriginalHash] = v.[OriginalHash]
        WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d
          AND NOT EXISTS (SELECT 1 FROM #DimReadKeyed AS t WHERE t.[ValueId] = k.[ValueId] AND t.[RecordHash] = k.[RecordHash]);

        DELETE k
        FROM [osdu].[DimensionKeyRecord] AS k WITH (FORCESEEK)
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = k.[ValueId]
        WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND v.[RemovedRunId] = @run;

        INSERT INTO [osdu].[DimensionKeyRecord] ([PartitionId], [DimensionId], [ValueId], [RecordHash], [EntityType])
        SELECT @p, @d, t.[ValueId], t.[RecordHash], t.[EntityType]
        FROM #DimReadKeyed AS t
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[DimensionKeyRecord] AS k WITH (FORCESEEK)
            WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND k.[ValueId] = t.[ValueId] AND k.[RecordHash] = t.[RecordHash])
        ORDER BY t.[ValueId], t.[RecordHash];

        -- The keys each record held when the load read it, by the record's unique key: a full load's replace every row the
        -- dimension kept, an incremental load's replace those of the records it read. A full load that kept none (its flow
        -- loads in full) leaves none, and a key the load removes keeps none.
        INSERT INTO #DimRecKeyed ([RecordHash], [ValueId])
        SELECT h.[RecordHash], v.[ValueId]
        FROM #DimRecHeld AS h
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[OriginalHash] = h.[OriginalHash];

        IF @records = 1
        BEGIN
            DELETE r
            FROM [osdu].[DimensionRecord] AS r WITH (FORCESEEK)
            WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d
              AND (@partial = 0 OR EXISTS (SELECT 1 FROM #DimRecRead AS s WHERE s.[RecordHash] = r.[RecordHash]))
              AND NOT EXISTS (SELECT 1 FROM #DimRecKeyed AS k WHERE k.[RecordHash] = r.[RecordHash] AND k.[ValueId] = r.[ValueId]);

            INSERT INTO [osdu].[DimensionRecord] ([PartitionId], [DimensionId], [RecordHash], [ValueId])
            SELECT @p, @d, k.[RecordHash], k.[ValueId]
            FROM #DimRecKeyed AS k
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[DimensionRecord] AS r WITH (FORCESEEK)
                WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d AND r.[RecordHash] = k.[RecordHash] AND r.[ValueId] = k.[ValueId])
            ORDER BY k.[RecordHash], k.[ValueId];
        END
        ELSE IF @partial = 0
            DELETE r FROM [osdu].[DimensionRecord] AS r WITH (FORCESEEK) WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d;

        DELETE r
        FROM [osdu].[DimensionRecord] AS r WITH (FORCESEEK)
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = r.[ValueId]
        WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d AND v.[RemovedRunId] = @run;

        -- The texts a full load collected replace those the dimension had, unless it could not settle the field (and so read
        -- none). An incremental load read the texts of its keys' records only, so it adds those the dimension does not hold
        -- yet and leaves the others, and their records, as the last full load counted them.
        IF @fieldIndex IS NOT NULL
        BEGIN
            IF @partial = 0
                DELETE c FROM [osdu].[DimensionCollectedText] AS c WITH (FORCESEEK) WHERE c.[PartitionId] = @p AND c.[DimensionId] = @d;
            INSERT INTO [osdu].[DimensionCollectedText] ([PartitionId], [DimensionId], [AttributeId], [TextHash], [Text], [Value], [Records])
            SELECT @p, @d, dn.[AttributeId], t.[TextHash], t.[Text], t.[Value], t.[Records]
            FROM #DimText AS t INNER JOIN #DimName AS dn ON dn.[Name] = t.[Name]
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[DimensionCollectedText] AS c WITH (FORCESEEK)
                WHERE c.[PartitionId] = @p AND c.[DimensionId] = @d AND c.[AttributeId] = dn.[AttributeId] AND c.[TextHash] = t.[TextHash])
            ORDER BY dn.[AttributeId], t.[Value], t.[Text];
        END;

        -- The first build's originals are its arrivals, told by the build that first found each; a later build logs its own.
        -- Each change keeps the original and its values as they read now, so it still says what it was once the next build
        -- has deleted a removed original and its value.
        INSERT INTO [osdu].[DimensionChange] ([PartitionId], [DimensionId], [DimensionRunId], [ValueId], [Change], [FromMemberId], [ToMemberId],
            [Original], [FromValue], [ToValue], [ChangedUtc])
        SELECT @p, @d, @run, c.[ValueId], c.[Change], c.[FromMemberId], c.[ToMemberId], v.[Original], fm.[Value], tm.[Value], @now
        FROM #DimChange AS c
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = c.[ValueId]
        LEFT JOIN [osdu].[DimensionMember] AS fm WITH (FORCESEEK) ON fm.[PartitionId] = @p AND fm.[DimensionId] = @d AND fm.[MemberId] = c.[FromMemberId]
        LEFT JOIN [osdu].[DimensionMember] AS tm WITH (FORCESEEK) ON tm.[PartitionId] = @p AND tm.[DimensionId] = @d AND tm.[MemberId] = c.[ToMemberId]
        WHERE c.[Change] <> N'added' OR @firstBuild = 0;

        DECLARE @members bigint = (SELECT COUNT_BIG(*) FROM [osdu].[DimensionMember] WITH (FORCESEEK) WHERE [PartitionId] = @p AND [DimensionId] = @d AND [RemovedRunId] IS NULL);
        DECLARE @originals bigint = (SELECT COUNT_BIG(*) FROM [osdu].[DimensionValue] WITH (FORCESEEK) WHERE [PartitionId] = @p AND [DimensionId] = @d AND [RemovedRunId] IS NULL);
        DECLARE @leftOut bigint = (
            SELECT COUNT_BIG(*) FROM [osdu].[DimensionValue] WITH (FORCESEEK)
            WHERE [PartitionId] = @p AND [DimensionId] = @d AND [RemovedRunId] IS NULL AND [MemberId] IS NULL);
        DECLARE @unfilterable bigint = (
            SELECT COUNT_BIG(*) FROM [osdu].[DimensionValue] WITH (FORCESEEK)
            WHERE [PartitionId] = @p AND [DimensionId] = @d AND [RemovedRunId] IS NULL AND [MemberId] IS NOT NULL AND [Filterable] = 0);

        -- A build that could not settle the field (no record for its templates to be read by) leaves the one the dimension had.
        UPDATE d SET
            [FieldIndex] = CASE WHEN @fieldIndex IS NULL THEN [FieldIndex] ELSE @fieldIndex END,
            [NestedPath] = CASE WHEN @fieldIndex IS NULL THEN [NestedPath] ELSE @nestedPath END,
            [AggregateBy] = CASE WHEN @fieldIndex IS NULL THEN [AggregateBy] ELSE @aggregateBy END,
            [Repeats] = CASE WHEN @fieldIndex IS NULL THEN [Repeats] ELSE @repeats END,
            [CollectedJson] = CASE WHEN @fieldIndex IS NULL THEN [CollectedJson] ELSE @collected END,
            [Members] = @members, [Originals] = @originals, [LastRunId] = @run, [LastBuiltUtc] = @now,
            [TableConnection] = CASE WHEN @tableName IS NULL THEN [TableConnection] ELSE @tableConnection END,
            [TableDatabase] = CASE WHEN @tableName IS NULL THEN [TableDatabase] ELSE @tableDatabase END,
            [TableSchema] = CASE WHEN @tableName IS NULL THEN [TableSchema] ELSE NULLIF(@tableSchema, N'osdu') END,
            [LastFullRunId] = CASE WHEN @partial = 0 THEN @run ELSE [LastFullRunId] END,
            [LastFullBuiltUtc] = CASE WHEN @partial = 0 THEN @now ELSE [LastFullBuiltUtc] END
        FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
        WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d;

        """ + "\n\n" + LayoutSql + "\n\n" + """
        SELECT
            (SELECT COUNT_BIG(*) FROM #DimChange WHERE [Change] = N'added'),
            (SELECT COUNT_BIG(*) FROM #DimChange WHERE [Change] = N'removed'),
            (SELECT COUNT_BIG(*) FROM #DimChange WHERE [Change] = N'moved'),
            (SELECT COUNT_BIG(*) FROM #DimChange WHERE [Change] = N'restored'),
            @membersAdded, @membersRemoved, @membersRestored, @members, @originals, @attributesChanged, @leftOut, @unfilterable,
            ISNULL(@rewrite, CAST(0 AS bit)), ISNULL(@scoped, CAST(0 AS bit));
        """;

    // A dimension's rows, table by table, a batch at a time, each batch its own statement so no one transaction holds millions
    // of rows; under the dimension's write lock, held by the session, so a build's write waits or comes first. The dimension's
    // own row goes last: a removal that stops part way leaves it listed, and removing it again finishes the work.
    private const string RemoveSql = """
        SET NOCOUNT ON;
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = @timeout;
        IF @granted < 0
        BEGIN
            SELECT CAST(@granted AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint), CAST(0 AS bigint),
                CAST(NULL AS sysname), CAST(NULL AS sysname), CAST(0 AS bit);
            RETURN;
        END;

        BEGIN TRY
            DECLARE @n bigint, @texts bigint = 0, @attributes bigint = 0, @changes bigint = 0, @keys bigint = 0, @values bigint = 0, @builds bigint = 0;
            DECLARE @table sysname = (SELECT d.[TableName] FROM [osdu].[Dimension] AS d WITH (FORCESEEK) WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d);
            DECLARE @dropped sysname = NULL;

            -- The dimension's rows in its own table, which every partition the flow builds it in writes to, when the table is
            -- kept here; the removal took them from a table kept elsewhere before it began.
            IF @tableHere = 1 AND @table IS NOT NULL AND OBJECT_ID(N'[osdu].' + QUOTENAME(@table), N'U') IS NOT NULL
            BEGIN
                DECLARE @rows nvarchar(max) = N'WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM [osdu].' + QUOTENAME(@table)
                    + N' WHERE [partition] = @partition; IF @@ROWCOUNT < @batch BREAK; END;';
                EXEC sys.sp_executesql @rows, N'@batch int, @partition nvarchar(256)', @batch = @batch, @partition = @partitionName;
            END;

            WHILE 1 = 1 BEGIN DELETE TOP (@batch) c FROM [osdu].[DimensionCollectedText] AS c WITH (FORCESEEK) WHERE c.[PartitionId] = @p AND c.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @texts += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) a FROM [osdu].[DimensionAttribute] AS a WITH (FORCESEEK) WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @attributes += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) e FROM [osdu].[DimensionElement] AS e WITH (FORCESEEK) WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @attributes += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) k FROM [osdu].[DimensionKeyRecord] AS k WITH (FORCESEEK) WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @attributes += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) r FROM [osdu].[DimensionRecord] AS r WITH (FORCESEEK) WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @attributes += @n; IF @n < @batch BREAK; END;

            -- What a build of the dimension staged and left: a build its process stopped before it ended, or one this removal stops.

        """ + SqlServerDimensionStage.DiscardEverySql + """

            WHILE 1 = 1 BEGIN DELETE TOP (@batch) c FROM [osdu].[DimensionChange] AS c WITH (FORCESEEK) WHERE c.[PartitionId] = @p AND c.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @changes += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) v FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK) WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @keys += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) m FROM [osdu].[DimensionMember] AS m WITH (FORCESEEK) WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @values += @n; IF @n < @batch BREAK; END;
            WHILE 1 = 1 BEGIN DELETE TOP (@batch) r FROM [osdu].[DimensionRun] AS r WITH (FORCESEEK) WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d; SET @n = @@ROWCOUNT; SET @builds += @n; IF @n < @batch BREAK; END;
            DELETE n FROM [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK) WHERE n.[PartitionId] = @p AND n.[DimensionId] = @d;
            DELETE d FROM [osdu].[Dimension] AS d WITH (FORCESEEK) WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d;
            SET @n = @@ROWCOUNT;

            -- The table holds every partition the flow builds the dimension in: it goes with the last of them, here, or where
            -- it is kept once this answers.
            DECLARE @last bit = CASE WHEN @table IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [osdu].[Dimension] AS d WITH (FORCESEEK) WHERE d.[TableName] = @table) THEN 1 ELSE 0 END;
            IF @tableHere = 1 AND @last = 1
            BEGIN
                DECLARE @drop nvarchar(400) = N'DROP TABLE IF EXISTS [osdu].' + QUOTENAME(@table) + N';';
                EXEC sys.sp_executesql @drop;
                SET @dropped = @table;
            END;

            EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';
            SELECT @n, @values, @keys, @builds, @changes, @attributes, @texts, @dropped, @table, @last;
        END TRY
        BEGIN CATCH
            EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';
            THROW;
        END CATCH;
        """;

    /// <summary>The rows a removal took from each table, and the dimension's own table when the dimension was the last to write it.</summary>
    public sealed record Removed(long Values, long Keys, long Builds, long Changes, long Attributes, long Texts, string? TableDropped);

    /// <summary>
    /// The attribute columns of table <paramref name="table"/> as dimension <paramref name="dimensionId"/> reads it, where
    /// <paramref name="home"/> keeps it: the attributes the dimension declares now (read from the ledger, on
    /// <paramref name="catalog"/>), in their order, each with a column there.
    /// </summary>
    /// <exception cref="DimensionTableMissingException">The database holds no such table.</exception>
    internal static async Task<IReadOnlyList<string>> TableAttributesAsync(
        SqlConnection catalog, DimensionTableDatabase home, short partitionId, int dimensionId, string table, CancellationToken ct)
    {
        var declared = new List<string>();
        await using (var names = new SqlCommand(
            "SELECT n.[Name] FROM [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK) WHERE n.[PartitionId] = @p AND n.[DimensionId] = @d AND n.[Ordinal] IS NOT NULL ORDER BY n.[Ordinal];",
            catalog) { CommandTimeout = CommandTimeoutSeconds })
        {
            names.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            names.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
            await using var reader = await names.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                declared.Add(reader.GetString(0));
            }
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var read = new SqlCommand(
            """
            SELECT CASE WHEN OBJECT_ID(@table, N'U') IS NULL THEN 0 ELSE 1 END;
            SELECT c.[name] FROM sys.columns AS c WHERE c.[object_id] = OBJECT_ID(@table, N'U');
            """,
            home.Connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            read.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = home.Target.Qualified(table) });
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt32(0) == 0)
            {
                throw new DimensionTableMissingException(
                    $"The table {home.Target.Shown(table)} is not in the database: it was dropped, or never made. Run the flow's pipeline to make it again.");
            }

            await reader.NextResultAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }
        }

        return declared.Where(columns.Contains).ToList();
    }

    /// <summary>The columns of every table a page can be ordered by, beside the key's, the value's and the attributes'.</summary>
    private const string RecordsColumn = "records";

    private const string IdColumn = "id";

    /// <summary>
    /// A column of the table by the name a caller gave it, ignoring case: a column named so in the table first, then the
    /// two words the value's and the key's columns are asked for by whatever the dimension names them; null when the
    /// table has no such column.
    /// </summary>
    private static string? TableColumn(DimensionTableRef table, IReadOnlyList<string> attributes, string name)
    {
        var wanted = name.Trim();
        string[] named = [table.ValueColumn, table.KeyColumn, RecordsColumn, IdColumn, .. attributes];
        return named.FirstOrDefault(c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase))
            ?? (string.Equals(wanted, DimensionColumnNames.ValueRole, StringComparison.OrdinalIgnoreCase) ? table.ValueColumn
                : string.Equals(wanted, DimensionColumnNames.KeyRole, StringComparison.OrdinalIgnoreCase) ? table.KeyColumn
                : null);
    }

    private static string Bracketed(string identifier) => DimensionTables.Quoted(identifier);

    /// <summary>The columns a row ends with.</summary>
    private static readonly string[] EndColumns = ["[records]", "[filter]"];

    /// <summary>The rows a statement over a table reads: the row's number, the key's, the key, the value, each attribute, the records and the filter.</summary>
    private static string TableColumns(DimensionTableRef table, IReadOnlyList<string> attributes, string alias = "")
        => string.Join(", ", new[] { "[id]", "[key_id]", Bracketed(table.KeyColumn), Bracketed(table.ValueColumn) }
            .Concat(attributes.Select(Bracketed)).Concat(EndColumns).Select(c => alias + c));

    /// <summary>What SQL Server answers a statement naming a column the table does not have.</summary>
    private const int InvalidColumnName = 207;

    /// <summary>A read that named a column the table no longer has: a build renamed it between the read's two steps.</summary>
    private static DimensionTableRenamedException Renamed(DimensionTableRef table, DimensionTableTarget target, SqlException ex)
        => new(
            $"The table {target.Shown(table.Name)} was changed while it was read (a build renamed a column of it, or one was dropped by hand): {ex.Message.Trim()} Read it again.",
            ex);

    /// <summary>A row of a table, its columns read in the order the statement names them, as a reader streaming them needs.</summary>
    private static DimensionTableRow TableRow(SqlDataReader reader, int attributes)
    {
        var id = reader.GetInt64(0);
        var keyId = reader.GetInt64(1);
        var key = reader.GetString(2);
        var value = reader.GetString(3);
        var values = new string?[attributes];
        for (var i = 0; i < attributes; i++)
        {
            values[i] = reader.IsDBNull(4 + i) ? null : reader.GetString(4 + i);
        }

        var records = reader.GetInt64(4 + attributes);
        return new DimensionTableRow(id, keyId, key, value, values, records, reader.IsDBNull(5 + attributes) ? null : reader.GetString(5 + attributes));
    }

    /// <summary>
    /// A page of the rows of the table <paramref name="named"/> in partition <paramref name="partition"/>, narrowed and ordered as
    /// <paramref name="query"/> asks, with how many rows it matches in all on a first page.
    /// </summary>
    /// <remarks>
    /// A page is found before it is read: the first statement orders the numbers of the rows the query matches and keeps
    /// the page's, which is all it sorts (a number and the column ordered by, never a key or a filter), and the second
    /// reads those rows by their numbers. Rows in value order come from the index that keeps them so. Text is searched
    /// case-folded and compared exactly, which is several times quicker than a comparison by a language's rules, and an
    /// attribute's value is matched exactly, as the lists that offer it hold it; either reads the partition's rows once,
    /// for the count and the page together.
    /// </remarks>
    /// <exception cref="DimensionTableMissingException">The database holds no such table.</exception>
    /// <exception cref="DimensionTableRenamedException">A build renamed a column of the table while it was read.</exception>
    /// <exception cref="DeliveryException">The query orders or narrows by a column the table does not have.</exception>
    internal static async Task<DimensionTablePage> ReadTableAsync(
        SqlConnection catalog, DimensionTableDatabase home, short partitionId, int dimensionId, DimensionTableRef named, string partition,
        DimensionTableQuery query, int maxPage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(named);
        ArgumentNullException.ThrowIfNull(query);
        var table = named.Name;
        var target = home.Target;
        var connection = home.Connection;
        try
        {
            var attributes = await TableAttributesAsync(catalog, home, partitionId, dimensionId, table, ct).ConfigureAwait(false);
            var where = new List<string> { "[partition] = @partition" };
            var parameters = new List<SqlParameter> { new("@partition", SqlDbType.NVarChar, 256) { Value = partition } };
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                // What is typed is found anywhere in the key, the value or an attribute, ignoring case; its own wildcards are text.
                var like = "%" + query.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
                    .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "%";
                parameters.Add(new SqlParameter("@like", SqlDbType.NVarChar, 600) { Value = like });
                where.Add("(" + string.Join(" OR ", new[] { named.ValueColumn, named.KeyColumn }.Concat(attributes).Select(Bracketed)
                    .Select(c => $"UPPER({c}) COLLATE {DimensionTables.Exact} LIKE UPPER(@like) ESCAPE N'\\'")) + ")");
            }

            var matched = 0;
            foreach (var match in query.Attributes ?? [])
            {
                var column = attributes.FirstOrDefault(a => string.Equals(a, match.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new DeliveryException(
                        $"The table {target.Shown(table)} has no attribute column '{match.Name}'{(attributes.Count == 0 ? "; it has none" : $"; it has {string.Join(", ", attributes)}")}.");
                var names = new List<string>();
                foreach (var value in match.Values.Distinct(StringComparer.Ordinal))
                {
                    var name = string.Create(CultureInfo.InvariantCulture, $"@m{matched++}");
                    parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, DeliveryDimensionElement.MaxValueLength) { Value = value });
                    names.Add(name);
                }

                if (names.Count > 0)
                {
                    where.Add($"{Bracketed(column)} COLLATE {DimensionTables.Exact} IN ({string.Join(", ", names)})");
                }
            }

            var ordered = string.IsNullOrWhiteSpace(query.OrderBy) ? named.ValueColumn : TableColumn(named, attributes, query.OrderBy)
                ?? throw new DeliveryException(
                    $"The table {target.Shown(table)} has no column '{query.OrderBy}' to order by; it has {named.ValueColumn}, {named.KeyColumn}, {RecordsColumn}, {IdColumn}{(attributes.Count == 0 ? string.Empty : ", " + string.Join(", ", attributes))}.");

            // A row's number tells it from every other, so the order ends with it: every page then holds its own rows.
            var direction = query.Descending ? "DESC" : "ASC";
            var byId = string.Equals(ordered, IdColumn, StringComparison.Ordinal);
            var order = byId ? $"[id] {direction}" : $"{Bracketed(ordered)} {direction}, [id]";
            var take = Math.Clamp(query.Limit, 1, maxPage);
            var offset = Math.Max(0, query.Offset);
            var filter = string.Join(" AND ", where);
            var from = target.Qualified(table);

            long? total = null;
            var rows = new List<DimensionTableRow>(take);
            if (where.Count > 1)
            {
                // A search or an attribute's value is found by reading the partition's rows, so they are read once: the
                // numbers of the rows that match are kept with the column they are ordered by, counted, and the page
                // taken from them.
                var by = byId ? string.Empty : $", {Bracketed(ordered)} AS [o]";
                var matchOrder = byId ? $"[id] {direction}" : $"[o] {direction}, [id]";
                await using var command = new SqlCommand(
                    $"""
                    SET NOCOUNT ON;
                    DECLARE @page TABLE ([n] int IDENTITY(1, 1) NOT NULL PRIMARY KEY, [id] bigint NOT NULL);
                    SELECT [id]{by} INTO #match FROM {from} WHERE {filter};
                    DECLARE @total bigint = ROWCOUNT_BIG();
                    INSERT INTO @page ([id])
                    SELECT [id] FROM #match ORDER BY {matchOrder} OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
                    DROP TABLE #match;
                    SELECT @total;
                    SELECT {TableColumns(named, attributes, "t.")}
                    FROM @page AS g INNER JOIN {from} AS t ON t.[id] = g.[id]
                    ORDER BY g.[n];
                    """,
                    connection)
                {
                    CommandTimeout = CommandTimeoutSeconds,
                };
                command.Parameters.AddRange(parameters.ToArray());
                command.Parameters.Add(new SqlParameter("@offset", SqlDbType.Int) { Value = offset });
                command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = take });
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    total = reader.GetInt64(0);
                }

                await reader.NextResultAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    rows.Add(TableRow(reader, attributes.Count));
                }
            }
            else
            {
                // Every row of the partition: counted from the narrow index that keeps them in value order, on a first
                // page, and the page's numbers taken in order, from that index when the order is the value's.
                if (offset == 0)
                {
                    await using var count = new SqlCommand($"SELECT COUNT_BIG(*) FROM {from} WHERE {filter};", connection) { CommandTimeout = CommandTimeoutSeconds };
                    count.Parameters.AddRange(parameters.Select(p => (SqlParameter)((ICloneable)p).Clone()).ToArray());
                    total = Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }

                if (total != 0)
                {
                    await using var command = new SqlCommand(
                        $"""
                        SET NOCOUNT ON;
                        DECLARE @page TABLE ([n] int IDENTITY(1, 1) NOT NULL PRIMARY KEY, [id] bigint NOT NULL);
                        INSERT INTO @page ([id])
                        SELECT [id] FROM {from} WHERE {filter} ORDER BY {order} OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
                        SELECT {TableColumns(named, attributes, "t.")}
                        FROM @page AS g INNER JOIN {from} AS t ON t.[id] = g.[id]
                        ORDER BY g.[n];
                        """,
                        connection)
                    {
                        CommandTimeout = CommandTimeoutSeconds,
                    };
                    command.Parameters.AddRange(parameters.ToArray());
                    command.Parameters.Add(new SqlParameter("@offset", SqlDbType.Int) { Value = offset });
                    command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = take });
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        rows.Add(TableRow(reader, attributes.Count));
                    }
                }
            }

            // A first page knows how many rows there are; a later one says more may follow while it comes back full.
            var more = total is { } all ? offset + rows.Count < all : rows.Count == take;
            return new DimensionTablePage(target.Shown(table), named.KeyColumn, named.ValueColumn, attributes, rows, more, total);
        }
        catch (SqlException ex) when (ex.Number == InvalidColumnName)
        {
            throw Renamed(named, target, ex);
        }
    }

    /// <summary>Every row of the table <paramref name="named"/> in partition <paramref name="partition"/>, by value then row number, one at a time.</summary>
    /// <exception cref="DimensionTableMissingException">The database holds no such table.</exception>
    /// <exception cref="DimensionTableRenamedException">A build renamed a column of the table as the read began.</exception>
    internal static async IAsyncEnumerable<DimensionTableRow> StreamTableAsync(
        SqlConnection catalog, DimensionTableDatabase home, short partitionId, int dimensionId, DimensionTableRef named, string partition,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(named);
        var table = named.Name;
        var connection = home.Connection;
        {
            var attributes = await TableAttributesAsync(catalog, home, partitionId, dimensionId, table, ct).ConfigureAwait(false);

            // The whole table in order is a sort of every row, and a row is declared wide (a key, a filter), so the server
            // would reserve memory for the widest table it can imagine: a tenth of what it may give one query sorts any
            // dimension, past it the sort uses tempdb, and other queries keep their memory.
            await using var command = new SqlCommand(
                $"SELECT {TableColumns(named, attributes)} FROM {home.Target.Qualified(table)} WHERE [partition] = @partition ORDER BY {Bracketed(named.ValueColumn)}, [id] OPTION (MAX_GRANT_PERCENT = 10);",
                connection)
            {
                CommandTimeout = CommandTimeoutSeconds,
            };
            command.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
            SqlDataReader opened;
            try
            {
                opened = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (ex.Number == InvalidColumnName)
            {
                throw Renamed(named, home.Target, ex);
            }

            await using var reader = opened;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                yield return TableRow(reader, attributes.Count);
            }
        }
    }

    /// <summary>
    /// What preparing a dimension's table came to: the table as it is now (its key's and its value's columns under the
    /// names they were settled at), the statements that write its rows, and whether its schema had to change.
    /// </summary>
    private sealed record PreparedTable(DimensionTableSpec Table, string ApplySql, bool Changed);

    // What the table's key and value columns are named now: what the dimensions writing the table recorded (the rows
    // that name the table first, then this dimension's own, which a build that never wrote may hold alone), and the
    // columns the table has.
    private const string ColumnsSql = """
        SELECT TOP (1) x.[KeyColumn], x.[ValueColumn]
        FROM (
            SELECT d.[KeyColumn], d.[ValueColumn], 0 AS [Rank]
            FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
            WHERE d.[TableName] = @name AND d.[KeyColumn] IS NOT NULL AND d.[ValueColumn] IS NOT NULL
            UNION ALL
            SELECT d.[KeyColumn], d.[ValueColumn], 1
            FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
            WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d AND d.[KeyColumn] IS NOT NULL AND d.[ValueColumn] IS NOT NULL) AS x
        ORDER BY x.[Rank];

        """;

    // The names recorded on every dimension writing the table (one a partition) and on this one, compared exactly, so
    // a name that changed only in case is recorded too: each by a seek of its own, so no other dimension's row is read.
    private const string RecordColumnsSql = """
        UPDATE d SET d.[KeyColumn] = @key, d.[ValueColumn] = @value
        FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
        WHERE d.[TableName] = @name
          AND (ISNULL(d.[KeyColumn], N'') COLLATE Latin1_General_100_BIN2 <> @key OR ISNULL(d.[ValueColumn], N'') COLLATE Latin1_General_100_BIN2 <> @value);
        UPDATE d SET d.[KeyColumn] = @key, d.[ValueColumn] = @value
        FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
        WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d
          AND (ISNULL(d.[KeyColumn], N'') COLLATE Latin1_General_100_BIN2 <> @key OR ISNULL(d.[ValueColumn], N'') COLLATE Latin1_General_100_BIN2 <> @value);
        """;

    /// <summary>The names a column passes through when two renames would cross (the key's and the value's swapped, or a name changed only in case).</summary>
    private static string Passing(string role) => "osdu_renaming_" + role;

    /// <summary>
    /// Settles what the table's key and value columns are named, before its schema is brought to the declaration
    /// (osdu/docs/reference/flow/dimension-table.md, The dimension's table). A build (<paramref name="rename"/>) names them
    /// as its dimension declares: where the table holds them under other names (the dimension's path or label changed,
    /// its document names them otherwise, or the table was made before dimensions named their columns and holds
    /// <c>key</c> and <c>value</c>), each is renamed where it is, so the rows, their numbers and the indexes stay. A
    /// reader that makes a missing table ready does not know the declaration, so it keeps the names the table has. The
    /// names are recorded on every dimension writing the table, in the transaction that renames, so a reader never
    /// names a column the table does not have.
    /// </summary>
    /// <returns>The table with its columns as they are named now, and whether a column was renamed.</returns>
    /// <exception cref="DeliveryException">A name is taken by another column of the table, a rename failed, or another build held the table too long.</exception>
    private static async Task<(DimensionTableSpec Table, bool Renamed)> SettleColumnsAsync(
        SqlConnection connection, DimensionTableDatabase home, short partitionId, int dimensionId, DimensionTableSpec declared, bool rename, CancellationToken ct)
    {
        var shown = home.Target.Shown(declared.Name);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                // One table is written by a dimension in each partition it is built in, so its columns are settled by one at a time.
                await TableLockAsync(connection, transaction, declared.Name, shown, LockTimeoutMs, "settled its columns", ct).ConfigureAwait(false);

                (string Key, string Value)? recorded = null;
                await using (var read = Command(connection, transaction, ColumnsSql))
                {
                    AddColumnParameters(read, partitionId, dimensionId, declared.Name);
                    await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        recorded = (reader.GetString(0), reader.GetString(1));
                    }
                }

                // The columns the table has, where it is kept: in this transaction when that is the module's database.
                var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var read = Command(home.Connection, home.Joining(transaction), "SELECT c.[name] FROM sys.columns AS c WHERE c.[object_id] = OBJECT_ID(@table, N'U');"))
                {
                    read.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = home.Target.Qualified(declared.Name) });
                    await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        columns.Add(reader.GetString(0));
                    }
                }

                // A table made before dimensions named their columns, which no row says so of, holds them under the two words.
                var was = recorded
                    ?? (columns.Contains(DimensionColumnNames.KeyRole) && columns.Contains(DimensionColumnNames.ValueRole)
                        ? (DimensionColumnNames.KeyRole, DimensionColumnNames.ValueRole)
                        : ((string Key, string Value)?)null);
                var table = !rename && was is { } kept ? declared with { KeyColumn = kept.Key, ValueColumn = kept.Value } : declared;

                var moves = new List<(string Role, string From, string To)>();
                if (rename && was is { } named)
                {
                    foreach (var (role, from, to) in new[] { (DimensionColumnNames.KeyRole, named.Key, table.KeyColumn), (DimensionColumnNames.ValueRole, named.Value, table.ValueColumn) })
                    {
                        if (!string.Equals(from, to, StringComparison.Ordinal) && columns.Contains(from))
                        {
                            moves.Add((role, from, to));
                        }
                    }
                }

                // A name is free when no column holds it, or when the column holding it is itself being renamed away.
                foreach (var (role, from, to) in moves)
                {
                    if (columns.Contains(to) && !moves.Any(m => string.Equals(m.From, to, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new DeliveryException(
                            $"The dimension's table {shown} has a column {to} already (an attribute the dimension declared before, or a column added by hand), so its {role} column {from} cannot be renamed to it, and nothing was written. Drop that column, or name the {role}'s column otherwise in the flow: columns: {{ {role}: <name> }}.");
                    }
                }

                // A view naming a column renamed would fail, or read the other column where two swap names: the views a
                // build made over the table go in the same transaction, and the run's view step writes them again.
                if (moves.Count > 0)
                {
                    var renamed = string.Join(" and ", moves.Select(m => $"{m.From} to {m.To}"));
                    await SqlServerDimensionViewStore.ClearViewsReadingAsync(
                        connection, transaction, home, declared.Name,
                        $"Dropped when a build renamed {renamed} in {shown}; the next build of its flow writes it again.", ct).ConfigureAwait(false);
                }

                var crossing = moves.Any(m => moves.Any(o => string.Equals(o.From, m.To, StringComparison.OrdinalIgnoreCase)));
                if (crossing)
                {
                    foreach (var (role, from, _) in moves)
                    {
                        await RenameColumnAsync(home, home.Joining(transaction), declared.Name, from, Passing(role), ct).ConfigureAwait(false);
                    }
                }

                foreach (var (role, from, to) in moves)
                {
                    await RenameColumnAsync(home, home.Joining(transaction), declared.Name, crossing ? Passing(role) : from, to, ct).ConfigureAwait(false);
                }

                await using (var record = Command(connection, transaction, RecordColumnsSql))
                {
                    AddColumnParameters(record, partitionId, dimensionId, declared.Name);
                    record.Parameters.Add(new SqlParameter("@key", SqlDbType.NVarChar, 128) { Value = table.KeyColumn });
                    record.Parameters.Add(new SqlParameter("@value", SqlDbType.NVarChar, 128) { Value = table.ValueColumn });
                    await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return (table, moves.Count > 0);
            }
            catch (SqlException ex)
            {
                throw new DeliveryException(
                    $"The columns of the dimension's table {shown} could not be settled, so the table was not written: {ex.Message.Trim()} The database user the table is written as needs ALTER on the {home.Target.Schema} schema to rename a column; a table changed by hand into something a build cannot write is put right, or dropped so the next run makes it again.",
                    ex);
            }
        }
    }

    private static void AddColumnParameters(SqlCommand command, short partitionId, int dimensionId, string table)
    {
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = table });
        command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
        command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
    }

    /// <summary>Renames one column of a table where it is: its rows and the indexes over it stay, and only its name moves.</summary>
    private static async Task RenameColumnAsync(DimensionTableDatabase home, SqlTransaction? transaction, string table, string from, string to, CancellationToken ct)
    {
        await using var command = Command(home.Connection, transaction, "EXEC sys.sp_rename @objname = @column, @newname = @to, @objtype = N'COLUMN';");
        command.Parameters.Add(new SqlParameter("@column", SqlDbType.NVarChar, 776) { Value = home.Target.Qualified(table) + "." + Bracketed(from) });
        command.Parameters.Add(new SqlParameter("@to", SqlDbType.NVarChar, 128) { Value = to });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes sure the dimension's table is there with its key's and its value's columns under their names and a column
    /// for every attribute it declares, before the write's transaction begins: the two columns are settled first
    /// (<see cref="SettleColumnsAsync"/>), then SQLFlow's schema evolution reads the table as it is, creates it when it is
    /// missing and adds the column of an attribute it does not have, and never drops or narrows one
    /// (osdu/docs/reference/flow/dimension-table.md, The dimension's table). Then the order it keeps its rows in and the
    /// indexes it is read through (<see cref="MakeIndexesAsync"/>), and the statements that write its rows.
    /// <paramref name="rename"/> is true for a build, which names the two columns as its dimension declares, and false for
    /// a reader, which keeps the names the table has.
    /// </summary>
    /// <exception cref="DeliveryException">The table cannot be made or widened: the database user may not, or the table was changed by hand into something a build cannot write.</exception>
    private static async Task<PreparedTable> PrepareTableAsync(
        SqlConnection connection, DimensionTableDatabase home, short partitionId, int dimensionId, DimensionTableSpec declared, bool rename, CancellationToken ct)
    {
        await EnsureSchemaAsync(home, ct).ConfigureAwait(false);
        var (table, renamed) = await SettleColumnsAsync(connection, home, partitionId, dimensionId, declared, rename, ct).ConfigureAwait(false);
        try
        {
            var (plan, changed) = await RunTimeTables.EvolveAsync(
                home.Connection, table.Name, DimensionTables.Desired(table), DimensionTables.KeyColumns, ct, home.Target.Schema).ConfigureAwait(false);
            await MakeIndexesAsync(connection, home, table, ct).ConfigureAwait(false);

            // A column the table has and the declaration does not is one of an attribute the dimension declared before
            // (it is emptied), or one somebody added to the table, which is theirs and is left alone.
            var extra = plan.Drift.Where(d => d.Kind == DriftKind.ExtraTargetColumn).Select(d => d.Column).ToList();
            var retired = new List<string>();
            if (extra.Count > 0)
            {
                await using var known = new SqlCommand(
                    "SELECT n.[Name] FROM [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK) WHERE n.[PartitionId] = @p AND n.[DimensionId] = @d;", connection)
                {
                    CommandTimeout = CommandTimeoutSeconds,
                };
                known.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
                known.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var reader = await known.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        names.Add(reader.GetString(0));
                    }
                }

                retired.AddRange(extra.Where(names.Contains));
            }

            return new PreparedTable(table, DimensionTables.ApplySql(table, retired, home.Target), changed || renamed);
        }
        catch (Exception ex) when (ex is SqlException or SqlFlowException)
        {
            throw new DeliveryException(
                $"The dimension's table {home.Target.Shown(table.Name)} could not be made ready, so it was not written: {ex.Message.Trim()} The database user the table is written as needs CREATE TABLE in its database and ALTER on the {home.Target.Schema} schema; a table changed by hand into something a build cannot write is put right, or dropped so the next run makes it again.",
                ex);
        }
    }

    /// <summary>
    /// Makes the target's schema when a database other than the module's does not hold it yet: a flow naming a new schema
    /// there needs no setup first. The module's own schema is the model's, made by its migrations.
    /// </summary>
    /// <exception cref="DeliveryException">The schema is not there and cannot be made.</exception>
    private static async Task EnsureSchemaAsync(DimensionTableDatabase home, CancellationToken ct)
    {
        if (home.InCatalog)
        {
            return;
        }

        try
        {
            await using var command = new SqlCommand(
                "IF SCHEMA_ID(@schema) IS NULL BEGIN DECLARE @make nvarchar(300) = N'CREATE SCHEMA ' + QUOTENAME(@schema) + N';'; EXEC sys.sp_executesql @make; END;",
                home.Connection) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.Add(new SqlParameter("@schema", SqlDbType.NVarChar, 128) { Value = home.Target.Schema });
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == ObjectExists)
        {
            // Another build made the schema between the look and the make: it is there.
        }
        catch (SqlException ex)
        {
            throw new DeliveryException(
                $"The schema {home.Target.Schema} of the dimension tables' target is not in its database and could not be made: {ex.Message.Trim()} Make it there, or grant the database user the tables are written as CREATE SCHEMA.",
                ex);
        }
    }

    /// <summary>
    /// Lays the dimension's table out by partition and makes the indexes it is read through
    /// (<see cref="DimensionTables.IndexSql"/>), under the table's lock, so builds of the dimension in two partitions do it
    /// one at a time and the second finds it done. Laying out a table made before its rows were kept by partition rebuilds
    /// it once, which takes as long as its rows take: the statement is bounded by its run, as a write is, and a build of
    /// another partition waits for it rather than failing.
    /// </summary>
    private static async Task MakeIndexesAsync(SqlConnection connection, DimensionTableDatabase home, DimensionTableSpec table, CancellationToken ct)
    {
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // The lock is the ledger's, held for the transaction; the indexes are made where the table is kept, in that
            // transaction when it is the module's database.
            await TableLockAsync(connection, transaction, table.Name, home.Target.Shown(table.Name), -1, "laid it out", ct).ConfigureAwait(false);
            await using (var indexes = Command(home.Connection, home.Joining(transaction), DimensionTables.IndexSql(table, home.Target)))
            {
                await indexes.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes the lock a dimension's table is changed under for the rest of <paramref name="transaction"/>: one table is
    /// written by the dimension in each partition it is built in, so its columns are settled, and it is laid out, by one
    /// build at a time. <paramref name="timeoutMs"/> bounds the wait (<c>-1</c> waits as long as the other build takes,
    /// which its run bounds); <paramref name="doing"/> says what the other build was doing, for the message.
    /// </summary>
    /// <exception cref="DeliveryException">The lock was not taken.</exception>
    private static async Task TableLockAsync(SqlConnection connection, SqlTransaction transaction, string name, string shown, int timeoutMs, string doing, CancellationToken ct)
    {
        await using var held = Command(connection, transaction, LockSql);
        held.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = "osdu-dimension-table:" + name });
        held.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = timeoutMs });
        var granted = Convert.ToInt32(await held.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (granted < 0)
        {
            var waited = timeoutMs < 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" for more than {timeoutMs / 1000} seconds");
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"Another build held the dimension's table {shown}{waited} while it {doing} (sp_getapplock answered {granted}), so the table was not written. Build it again when the other has finished."));
        }
    }

    /// <summary>
    /// Makes a table a view reads ready for the view (osdu/docs/reference/flow/dimension-views.md): one no build has made
    /// yet is made, empty, as a build makes it, so the view's join finds nothing until its dimension is built; one a build
    /// made is given the key's hash column and its index when it was made before tables kept one. Its other columns are
    /// its builds' to settle: a column the document names that the table does not have yet fails the view's write, saying so.
    /// </summary>
    /// <exception cref="DeliveryException">The table cannot be made or given the column.</exception>
    internal static async Task EnsureViewTableAsync(SqlConnection connection, DimensionTableDatabase home, DimensionTableSpec table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(table);
        var qualified = home.Target.Qualified(table.Name);
        try
        {
            bool exists;
            await using (var probe = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@table, N'U') IS NULL THEN 0 ELSE 1 END;", home.Connection) { CommandTimeout = CommandTimeoutSeconds })
            {
                probe.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = qualified });
                exists = Convert.ToInt32(await probe.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
            }

            if (!exists)
            {
                await EnsureSchemaAsync(home, ct).ConfigureAwait(false);
                await RunTimeTables.EvolveAsync(home.Connection, table.Name, DimensionTables.Desired(table), DimensionTables.KeyColumns, ct, home.Target.Schema).ConfigureAwait(false);
                await MakeIndexesAsync(connection, home, table, ct).ConfigureAwait(false);
                return;
            }

            var literal = qualified.Replace("'", "''", StringComparison.Ordinal);
            await using var column = new SqlCommand(
                $"""
                IF COL_LENGTH(N'{literal}', N'{DimensionTables.KeyHashColumn}') IS NULL
                    ALTER TABLE {qualified} ADD [{DimensionTables.KeyHashColumn}] binary(32) NULL;
                """,
                home.Connection) { CommandTimeout = CommandTimeoutSeconds };
            await column.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await using var index = new SqlCommand(DimensionTables.KeyHashIndexSql(table.Name, home.Target), home.Connection) { CommandTimeout = CommandTimeoutSeconds };
            await index.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is ObjectExists or ColumnExists or IndexExists)
        {
            // A build made the table, or gave it the column or the index, between the look and the change: it is there.
        }
        catch (Exception ex) when (ex is SqlException or SqlFlowException)
        {
            throw new DeliveryException(
                $"The table {home.Target.Shown(table.Name)} a view reads could not be made ready: {ex.Message.Trim()} The database user the table is written as needs CREATE TABLE in its database and ALTER on the {home.Target.Schema} schema.",
                ex);
        }
    }

    /// <summary>What SQL Server answers a statement making an object that is already there.</summary>
    private const int ObjectExists = 2714;

    /// <summary>What SQL Server answers a statement adding a column a table has already.</summary>
    private const int ColumnExists = 2705;

    /// <summary>What SQL Server answers a statement making an index a table has already.</summary>
    private const int IndexExists = 1913;

    /// <summary>
    /// The parameters a statement that lays out a dimension's table takes: the table (null leaves it as it is), where it is
    /// kept, and whether it holds rows made per element that the declaration no longer reads (<paramref name="elementRows"/>).
    /// </summary>
    private static void AddTable(SqlCommand command, DimensionTableSpec? table, string partition, bool elementRows, DimensionTableTarget target)
    {
        command.Parameters.Add(new SqlParameter("@tableName", SqlDbType.NVarChar, 128) { Value = (object?)table?.Name ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@rowsSql", SqlDbType.NVarChar, -1) { Value = table is null ? DBNull.Value : DimensionTables.RowsSql(table.Columns.Count) });
        command.Parameters.Add(new SqlParameter("@partitionName", SqlDbType.NVarChar, 256) { Value = partition });
        command.Parameters.Add(new SqlParameter("@elements", SqlDbType.Bit) { Value = table?.HasElements ?? false });
        command.Parameters.Add(new SqlParameter("@elementRows", SqlDbType.Bit) { Value = elementRows });
        AddTarget(command, target);
    }

    /// <summary>Where the table is kept, as the ledger records it: the connection's reference, the database and the schema.</summary>
    private static void AddTarget(SqlCommand command, DimensionTableTarget target)
    {
        command.Parameters.Add(new SqlParameter("@tableConnection", SqlDbType.NVarChar, 2000) { Value = (object?)target.Connection ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@tableDatabase", SqlDbType.NVarChar, 128) { Value = (object?)target.Database ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@tableSchema", SqlDbType.NVarChar, 128) { Value = target.Schema });
    }

    /// <summary>
    /// Whether the table holds rows made per element in the partition while the declaration reads no elements: none of
    /// them matches a row the write lays out, so the table's rows of the partition are written anew. A table that is not
    /// there yet, or has no element column, holds none.
    /// </summary>
    private static async Task<bool> ElementRowsAsync(DimensionTableDatabase home, DimensionTableSpec table, string partition, CancellationToken ct)
    {
        if (table.HasElements)
        {
            return false;
        }

        var qualified = home.Target.Qualified(table.Name);
        await using (var column = new SqlCommand("SELECT CASE WHEN COL_LENGTH(@table, @column) IS NULL THEN 0 ELSE 1 END;", home.Connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            column.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = qualified });
            column.Parameters.Add(new SqlParameter("@column", SqlDbType.NVarChar, 128) { Value = DimensionElementsSpec.Column });
            if (Convert.ToInt32(await column.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
            {
                return false;
            }
        }

        await using var rows = new SqlCommand(
            $"SELECT CASE WHEN EXISTS (SELECT 1 FROM {qualified} AS t WITH (FORCESEEK) WHERE t.[partition] = @partition AND t.{Bracketed(DimensionElementsSpec.Column)} IS NOT NULL) THEN 1 ELSE 0 END;",
            home.Connection) { CommandTimeout = CommandTimeoutSeconds };
        rows.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
        return Convert.ToInt32(await rows.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// The table step of a write (osdu/docs/reference/flow/dimension-target.md): brings the dimension's table, where
    /// <paramref name="home"/> keeps it, to the rows the write laid out in <c>#DimRow</c> (and <c>#DimScope</c>, the keys an
    /// incremental write lays out) on <paramref name="connection"/>'s session, under the dimension's write lock, and records
    /// the table in the ledger: its name, its version, where it is kept, and the build whose rows it holds
    /// (<paramref name="runId"/>; the dimension's last build when null). A table kept in the module's database is written
    /// in the ledger's transaction; one kept elsewhere is given the laid-out rows, written in a transaction of its own
    /// database, and recorded once that commits, so a table write that fails leaves the ledger saying the table is behind,
    /// and the next build lays it out whole. Answers how many of the table's rows it added, rewrote or deleted.
    /// </summary>
    private static async Task<long> WriteTableAsync(
        SqlConnection connection, DimensionTableDatabase home, short partitionId, int dimensionId, string partition, PreparedTable prepared,
        bool rewrite, bool scoped, long? runId, CancellationToken ct)
    {
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await LockAsync(connection, transaction, partitionId, dimensionId, ct).ConfigureAwait(false);
            long changed;
            if (home.InCatalog)
            {
                changed = await ApplyAsync(connection, transaction, prepared.ApplySql, partition, rewrite, scoped, ct).ConfigureAwait(false);
            }
            else
            {
                await ExecuteAsync(home.Connection, null, LayoutTablesSql, ct).ConfigureAwait(false);
                await CopyLayoutAsync(connection, transaction, home.Connection, ct).ConfigureAwait(false);
                var remote = (SqlTransaction)await home.Connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
                await using (remote.ConfigureAwait(false))
                {
                    changed = await ApplyAsync(home.Connection, remote, prepared.ApplySql, partition, rewrite, scoped, ct).ConfigureAwait(false);
                    await remote.CommitAsync(ct).ConfigureAwait(false);
                }

                await ExecuteAsync(home.Connection, null, "DROP TABLE IF EXISTS #DimRow;\nDROP TABLE IF EXISTS #DimScope;", ct).ConfigureAwait(false);
            }

            string? former;
            await using (var record = Command(connection, transaction, RecordTableSql))
            {
                record.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
                record.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
                record.Parameters.Add(new SqlParameter("@tableName", SqlDbType.NVarChar, 128) { Value = prepared.Table.Name });
                record.Parameters.Add(new SqlParameter("@changed", SqlDbType.BigInt) { Value = changed });
                record.Parameters.Add(new SqlParameter("@runId", SqlDbType.BigInt) { Value = (object?)runId ?? DBNull.Value });
                AddTarget(record, home.Target);
                var answered = await record.ExecuteScalarAsync(ct).ConfigureAwait(false);
                former = answered is string name ? name : null;
            }

            // A table the dimension wrote under an earlier name, where it is kept now, goes once no dimension names it, so a
            // rule that names tables otherwise leaves none behind.
            if (former is not null)
            {
                await ExecuteAsync(home.Connection, home.Joining(transaction), $"DROP TABLE IF EXISTS {home.Target.Qualified(former)};", ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "DROP TABLE IF EXISTS #DimRow;\nDROP TABLE IF EXISTS #DimScope;", ct).ConfigureAwait(false);
            return changed;
        }
    }

    /// <summary>
    /// Runs the statements made for the table (<see cref="DimensionTables.ApplySql"/>) over the rows laid out on
    /// <paramref name="connection"/>'s session, and answers how many of the table's rows they added, rewrote or deleted.
    /// </summary>
    private static async Task<long> ApplyAsync(
        SqlConnection connection, SqlTransaction transaction, string applySql, string partition, bool rewrite, bool scoped, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, applySql);
        command.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
        command.Parameters.Add(new SqlParameter("@rewrite", SqlDbType.Bit) { Value = rewrite });
        command.Parameters.Add(new SqlParameter("@scoped", SqlDbType.Bit) { Value = scoped });
        var changed = new SqlParameter("@changed", SqlDbType.BigInt) { Direction = ParameterDirection.InputOutput, Value = 0L };
        command.Parameters.Add(changed);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return changed.Value is long count ? count : Convert.ToInt64(changed.Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Copies the rows a write laid out (<c>#DimScope</c>, then <c>#DimRow</c>) from the ledger's session to the session of
    /// the database the table is kept in, which made its own two tables of the same shape first.
    /// </summary>
    private static async Task CopyLayoutAsync(SqlConnection connection, SqlTransaction transaction, SqlConnection target, CancellationToken ct)
    {
        foreach (var table in new[] { "#DimScope", "#DimRow" })
        {
            await using var read = Command(connection, transaction, $"SELECT * FROM {table};");
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            using var bulk = new SqlBulkCopy(target)
            {
                DestinationTableName = table,
                BulkCopyTimeout = WriteTimeoutSeconds,
                BatchSize = 5_000,
                EnableStreaming = true,
            };
            await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes sure dimension <paramref name="dimensionId"/> has <paramref name="table"/> with the rows the ledger holds of
    /// it, as a build's write does, without a build: for a dimension built before dimensions had a table, or one whose
    /// table was dropped. The table's key and value columns keep the names it has; where it has none yet, they take
    /// <paramref name="table"/>'s. Says whether the table had to be made or widened.
    /// </summary>
    internal static async Task<bool> EnsureTableAsync(
        OsduDbContext db, DimensionTableDatabase home, short partitionId, int dimensionId, string partition, DimensionTableSpec table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(table);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            var prepared = await PrepareTableAsync(connection, home, partitionId, dimensionId, table, rename: false, ct).ConfigureAwait(false);
            var elementRows = await ElementRowsAsync(home, prepared.Table, partition, ct).ConfigureAwait(false);
            bool rewrite;
            bool scoped;
            await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false))
            {
                var transaction = (SqlTransaction)tx.GetDbTransaction();
                await LockAsync(connection, transaction, partitionId, dimensionId, ct).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, "DROP TABLE IF EXISTS #DimName;\n" + NameStageSql, ct).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction, LayoutTablesSql, ct).ConfigureAwait(false);
                await CopyAsync(connection, transaction, "#DimName", NameColumns, NameTypes, table.Columns.Select(NameRow), null, ct).ConfigureAwait(false);
                await using (var command = Command(connection, transaction, EnsureTableSql))
                {
                    command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
                    command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });

                    // Written again outside a build, the table is laid out whole: no build's keys stand for what changed.
                    command.Parameters.Add(new SqlParameter("@partial", SqlDbType.Bit) { Value = false });
                    AddTable(command, table, partition, elementRows, home.Target);
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Laying out the table of dimension {dimensionId} answered nothing."));
                    }

                    (rewrite, scoped) = (reader.GetBoolean(0), reader.GetBoolean(1));
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            await WriteTableAsync(connection, home, partitionId, dimensionId, partition, prepared, rewrite, scoped, runId: null, ct).ConfigureAwait(false);
            return prepared.Changed;
        }
        catch (Exception ex) when (StoppedBy(ex, ct))
        {
            throw new OperationCanceledException(
                string.Create(CultureInfo.InvariantCulture, $"Writing the table of dimension {dimensionId} again was cancelled, and wrote no row."), ex, ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The most rows one statement of a removal deletes.</summary>
    private const int RemoveBatch = 20_000;

    /// <summary>
    /// Removes every row of dimension <paramref name="dimensionId"/> in partition <paramref name="partitionId"/> and then the
    /// dimension (<see cref="ILedger.RemoveDimensionAsync"/>).
    /// </summary>
    /// <exception cref="DeliveryException">Another write of the dimension held its lock past the timeout.</exception>
    /// <remarks>
    /// The dimension's table goes with the last partition writing it, where <paramref name="home"/> keeps it. A table kept in
    /// another database loses the partition's rows before the ledger's are removed, so a removal that fails there leaves the
    /// dimension listed to remove again, and is dropped once the ledger says no dimension names it.
    /// </remarks>
    internal static async Task<Removed> RemoveAsync(OsduDbContext db, DimensionTableDatabase home, short partitionId, int dimensionId, string partition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(home);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            if (!home.InCatalog)
            {
                await RemoveTableRowsAsync(connection, home, partitionId, dimensionId, partition, ct).ConfigureAwait(false);
            }

            await using var command = new SqlCommand(RemoveSql, connection) { CommandTimeout = WriteTimeoutSeconds };
            command.Parameters.Add(new SqlParameter("@tableHere", SqlDbType.Bit) { Value = home.InCatalog });
            command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = RemoveBatch });
            command.Parameters.Add(new SqlParameter("@partitionName", SqlDbType.NVarChar, 256) { Value = partition });
            command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = string.Create(CultureInfo.InvariantCulture, $"osdu-dimension:{partitionId}:{dimensionId}") });
            command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = LockTimeoutMs });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Removing dimension {dimensionId} returned no counts."));
            }

            if (reader.GetInt64(0) < 0)
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                    $"A build of dimension {dimensionId} held its write lock for more than {LockTimeoutMs / 1000} seconds (sp_getapplock answered {reader.GetInt64(0)}), so nothing was removed. Remove it again when the build has finished."));
            }

            var removed = new Removed(
                reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
                await reader.IsDBNullAsync(7, ct).ConfigureAwait(false) ? null : reader.GetString(7));
            var table = await reader.IsDBNullAsync(8, ct).ConfigureAwait(false) ? null : reader.GetString(8);
            var last = reader.GetBoolean(9);
            await reader.DisposeAsync().ConfigureAwait(false);
            if (!home.InCatalog && last && table is not null)
            {
                await ExecuteAsync(home.Connection, null, $"DROP TABLE IF EXISTS {home.Target.Qualified(table)};", ct).ConfigureAwait(false);
                removed = removed with { TableDropped = table };
            }

            return removed;
        }
        catch (Exception ex) when (StoppedBy(ex, ct))
        {
            // Each batch is its own statement, so the batches deleted stay deleted and the dimension stays listed.
            throw new OperationCanceledException(
                string.Create(CultureInfo.InvariantCulture, $"Removing dimension {dimensionId} was cancelled part way; removing it again finishes the work."), ex, ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes the partition's rows of the dimension's table kept in another database, a batch at a time, each batch its own
    /// statement, under the dimension's write lock held by the ledger's session, as the ledger's removal deletes its own.
    /// </summary>
    private static async Task RemoveTableRowsAsync(
        SqlConnection connection, DimensionTableDatabase home, short partitionId, int dimensionId, string partition, CancellationToken ct)
    {
        string? table;
        await using (var named = new SqlCommand(
            "SELECT d.[TableName] FROM [osdu].[Dimension] AS d WITH (FORCESEEK) WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d;", connection)
            { CommandTimeout = CommandTimeoutSeconds })
        {
            named.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            named.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
            table = await named.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        if (table is null)
        {
            return;
        }

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await LockAsync(connection, transaction, partitionId, dimensionId, ct).ConfigureAwait(false);
            var qualified = home.Target.Qualified(table);
            await using var rows = new SqlCommand(
                $"""
                IF OBJECT_ID(@table, N'U') IS NOT NULL
                    WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM {qualified} WHERE [partition] = @partition; IF @@ROWCOUNT < @batch BREAK; END;
                """,
                home.Connection) { CommandTimeout = WriteTimeoutSeconds };
            rows.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = qualified });
            rows.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = RemoveBatch });
            rows.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
            await rows.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What a write changed, and what the dimension holds after it: the attributes it added, rewrote or dropped among them,
    /// and its originals under no member and those under one that no query can carry.
    /// </summary>
    public sealed record Written(DimensionChangeCounts Changes, long Members, long Originals, long AttributesChanged, long LeftOut, long Unfilterable);

    /// <summary>The hash an original is unique by: SHA-256 of its UTF-8 bytes.</summary>
    public static byte[] HashOf(string original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return SHA256.HashData(Encoding.UTF8.GetBytes(original));
    }

    /// <summary>
    /// The keys dimension <paramref name="dimensionId"/> holds now that were read through one of <paramref name="recordIds"/>,
    /// each looked up by its hash: a seek a record, however many keys the dimension holds. At most a lookup chunk of ids.
    /// </summary>
    public static async Task<List<string>> KeysReadThroughAsync(OsduDbContext db, short partitionId, int dimensionId, IReadOnlyList<string> recordIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(recordIds);
        var hashes = recordIds.Where(id => id.Length <= DeliveryDimensionValue.MaxOriginalLength).Select(HashOf).ToList();
        if (hashes.Count == 0)
        {
            return [];
        }

        var sql = "SELECT DISTINCT v.[Original] FROM [osdu].[DimensionKeyRecord] AS k WITH (FORCESEEK) "
            + "INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = k.[ValueId] "
            + "WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND v.[RemovedRunId] IS NULL AND k.[RecordHash] IN ("
            + string.Join(", ", hashes.Select((_, i) => "@h" + i.ToString(CultureInfo.InvariantCulture))) + ");";
        var keys = new List<string>();
        await LookupAsync(db, partitionId, dimensionId, sql, hashes, reader => keys.Add(reader.GetString(0)), ct).ConfigureAwait(false);
        return keys;
    }

    /// <summary>
    /// The originals of dimension <paramref name="dimensionId"/> named, removed ones included, each with its member's clean
    /// value, each looked up by its hash. At most a lookup chunk of originals.
    /// </summary>
    public static async Task<List<DimensionValueState>> OriginalsAsync(OsduDbContext db, short partitionId, int dimensionId, IReadOnlyList<string> originals, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(originals);
        var hashes = originals.Select(HashOf).ToList();
        if (hashes.Count == 0)
        {
            return [];
        }

        var sql = "SELECT v.[ValueId], v.[Original], v.[MemberId], m.[Value], v.[LeftOut], v.[Note], v.[Label], v.[LabelFrom], v.[Filter], v.[Count], "
            + "v.[Filterable], v.[FirstSeenRunId], v.[FirstSeenUtc], v.[MemberSinceRunId], v.[RemovedRunId], v.[RemovedUtc] "
            + "FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK) "
            + "LEFT JOIN [osdu].[DimensionMember] AS m WITH (FORCESEEK) ON m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[MemberId] = v.[MemberId] "
            + "WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[OriginalHash] IN ("
            + string.Join(", ", hashes.Select((_, i) => "@h" + i.ToString(CultureInfo.InvariantCulture))) + ");";
        var found = new List<DimensionValueState>(originals.Count);
        await LookupAsync(db, partitionId, dimensionId, sql, hashes, reader => found.Add(new DimensionValueState
        {
            ValueId = reader.GetInt64(0),
            DimensionId = dimensionId,
            Original = reader.GetString(1),
            MemberId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            MemberValue = reader.IsDBNull(3) ? null : reader.GetString(3),
            LeftOut = reader.IsDBNull(4) ? null : reader.GetString(4),
            Note = reader.IsDBNull(5) ? null : reader.GetString(5),
            Label = reader.IsDBNull(6) ? null : reader.GetString(6),
            LabelFrom = reader.IsDBNull(7) ? null : reader.GetString(7),
            Filter = reader.IsDBNull(8) ? null : reader.GetString(8),
            Count = reader.GetInt64(9),
            Filterable = reader.GetBoolean(10),
            FirstSeenRunId = reader.GetInt64(11),
            FirstSeenUtc = DateTime.SpecifyKind(reader.GetDateTime(12), DateTimeKind.Utc),
            MemberSinceRunId = reader.GetInt64(13),
            RemovedRunId = reader.IsDBNull(14) ? null : reader.GetInt64(14),
            RemovedUtc = reader.IsDBNull(15) ? null : DateTime.SpecifyKind(reader.GetDateTime(15), DateTimeKind.Utc),
        }), ct).ConfigureAwait(false);
        return found;
    }

    /// <summary>
    /// The keys the records of dimension <paramref name="dimensionId"/> named by <paramref name="recordHashes"/> held when a
    /// load last read them, among those the dimension holds now. At most a lookup chunk of records.
    /// </summary>
    public static async Task<List<string>> RecordKeysAsync(OsduDbContext db, short partitionId, int dimensionId, IReadOnlyList<byte[]> recordHashes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(recordHashes);
        if (recordHashes.Count == 0)
        {
            return [];
        }

        var sql = "SELECT DISTINCT v.[Original] FROM [osdu].[DimensionRecord] AS r WITH (FORCESEEK) "
            + "INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = r.[ValueId] "
            + "WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d AND v.[RemovedRunId] IS NULL AND r.[RecordHash] IN ("
            + string.Join(", ", recordHashes.Select((_, i) => "@h" + i.ToString(CultureInfo.InvariantCulture))) + ");";
        var keys = new List<string>();
        await LookupAsync(db, partitionId, dimensionId, sql, recordHashes, reader => keys.Add(reader.GetString(0)), ct).ConfigureAwait(false);
        return keys;
    }

    /// <summary>Runs a lookup by hashes, <c>@h0</c> onwards, each binary(32), over one dimension, handing each row to <paramref name="row"/>.</summary>
    private static async Task LookupAsync(
        OsduDbContext db, short partitionId, int dimensionId, string sql, IReadOnlyList<byte[]> hashes, Action<SqlDataReader> row, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = new SqlCommand(sql, (SqlConnection)db.Database.GetDbConnection()) { CommandTimeout = CommandTimeoutSeconds };
            command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
            for (var i = 0; i < hashes.Count; i++)
            {
                command.Parameters.Add(new SqlParameter("@h" + i.ToString(CultureInfo.InvariantCulture), SqlDbType.Binary, 32) { Value = hashes[i] });
            }

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                row(reader);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Merges what <paramref name="stage"/>'s build staged into its dimension, as <paramref name="write"/> says, and closes the
    /// build as completed with <paramref name="close"/>'s counts, in one transaction of the module's database; a deadlock
    /// rolls it back and it is written again from the same staging. Then the table step brings the dimension's table, where
    /// the write's target keeps it (opened by <paramref name="open"/>), to what the merge laid out
    /// (osdu/docs/reference/flow/dimension-target.md). The merge is never made twice: a table step that fails leaves the
    /// dimension written and the table behind, and says so, and the next build lays the table out whole.
    /// </summary>
    /// <exception cref="DimensionTableWriteException">The dimension was written and its table was not.</exception>
    public static async Task<Written> WriteAsync(
        OsduDbContext db, string partition, DimensionStage stage, DimensionStagedWrite write, Func<Written, DeliveryDimensionRun, Task> close,
        Func<SqlConnection, DimensionTableTarget, CancellationToken, Task<DimensionTableDatabase>> open, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(open);
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var connection = (SqlConnection)db.Database.GetDbConnection();
            DimensionTableDatabase? home = null;
            try
            {
                var elementRows = false;
                if (write.Table is { } declared)
                {
                    await LeaveFormerTargetAsync(connection, open, stage.PartitionId, stage.DimensionId, partition, write.TableTarget, ct).ConfigureAwait(false);
                    home = await open(connection, write.TableTarget, ct).ConfigureAwait(false);
                    elementRows = await ElementRowsAsync(home, declared, partition, ct).ConfigureAwait(false);
                }

                Merged merged;
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        merged = await MergeOnceAsync(db, connection, partition, stage, write, close, elementRows, ct).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex) when (StoppedBy(ex, ct))
                    {
                        throw new OperationCanceledException(
                            string.Create(CultureInfo.InvariantCulture, $"The write of dimension {stage.DimensionId} was cancelled with its run, and wrote nothing."), ex, ct);
                    }
                    catch (Exception ex) when (attempt < DeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
                    {
                        db.ChangeTracker.Clear();
                        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 200) * attempt), ct).ConfigureAwait(false);
                    }
                }

                if (write.Table is { } table && home is not null)
                {
                    await WriteBuiltTableAsync(connection, home, stage, partition, table, merged, ct).ConfigureAwait(false);
                }

                return merged.Written;
            }
            finally
            {
                if (home is not null)
                {
                    await home.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    // Where the dimension's table is recorded as kept, and whether another partition of the dimension keeps it there too.
    private const string FormerTargetSql = """
        SELECT d.[TableName], d.[TableConnection], d.[TableDatabase], d.[TableSchema],
            CASE WHEN EXISTS (
                SELECT 1 FROM [osdu].[Dimension] AS o WITH (FORCESEEK)
                WHERE o.[TableName] = d.[TableName] AND NOT (o.[PartitionId] = d.[PartitionId] AND o.[DimensionId] = d.[DimensionId])
                  AND ISNULL(o.[TableConnection], N'') = ISNULL(d.[TableConnection], N'')
                  AND ISNULL(o.[TableDatabase], N'') = ISNULL(d.[TableDatabase], N'')
                  AND ISNULL(o.[TableSchema], N'osdu') = ISNULL(d.[TableSchema], N'osdu')) THEN 1 ELSE 0 END
        FROM [osdu].[Dimension] AS d WITH (FORCESEEK)
        WHERE d.[PartitionId] = @p AND d.[DimensionId] = @d AND d.[TableName] IS NOT NULL;
        """;

    /// <summary>
    /// A dimension whose flow now keeps its table elsewhere (<paramref name="target"/>) leaves nothing where it kept it
    /// before (osdu/docs/reference/flow/dimension-target.md, Moving a target): the table itself when no other partition
    /// keeps the dimension there, else the partition's rows of it, a batch at a time, under the dimension's write lock and
    /// the table's. Done before the merge records the new place, so a place that cannot be reached stops the build with
    /// nothing changed and the next build does it again; the table where it moves is laid out whole.
    /// </summary>
    /// <exception cref="DeliveryException">Where the table was kept could not be opened, or the table could not be taken from it.</exception>
    private static async Task LeaveFormerTargetAsync(
        SqlConnection connection, Func<SqlConnection, DimensionTableTarget, CancellationToken, Task<DimensionTableDatabase>> open, short partitionId, int dimensionId, string partition, DimensionTableTarget target, CancellationToken ct)
    {
        string table;
        DimensionTableTarget former;
        bool shared;
        await using (var read = new SqlCommand(FormerTargetSql, connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            read.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            read.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            table = reader.GetString(0);
            former = DimensionTableTarget.Of(
                await reader.IsDBNullAsync(1, ct).ConfigureAwait(false) ? null : reader.GetString(1),
                await reader.IsDBNullAsync(2, ct).ConfigureAwait(false) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, ct).ConfigureAwait(false) ? null : reader.GetString(3));
            shared = reader.GetInt32(4) == 1;
        }

        if (former == target)
        {
            return;
        }

        var shown = former.Shown(table);
        try
        {
            await using var there = former.InCatalog ? DimensionTableDatabase.OfCatalog(connection, former) : await open(connection, former, ct).ConfigureAwait(false);
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await LockAsync(connection, transaction, partitionId, dimensionId, ct).ConfigureAwait(false);
                await TableLockAsync(connection, transaction, table, shown, LockTimeoutMs, "while a build moved it to where its flow keeps its tables now", ct).ConfigureAwait(false);
                var qualified = former.Qualified(table);
                await using var leave = new SqlCommand(
                    shared
                        ? $"IF OBJECT_ID(@table, N'U') IS NOT NULL\n    WHILE 1 = 1 BEGIN DELETE TOP (@batch) FROM {qualified} WHERE [partition] = @partition; IF @@ROWCOUNT < @batch BREAK; END;"
                        : $"DROP TABLE IF EXISTS {qualified};",
                    there.Connection, there.Joining(transaction)) { CommandTimeout = WriteTimeoutSeconds };
                leave.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = qualified });
                leave.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = RemoveBatch });
                leave.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
                await leave.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (SqlException ex)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture,
                    $"Dimension {dimensionId}'s flow now keeps its table at {target.Shown(table)}, and the table could not be taken from {shown}, where it was kept before, so the build wrote nothing: {SqlFlow.Core.Secrets.SecretHygiene.RedactedMessage(ex.Message)} The next build tries again."),
                ex);
        }
    }

    /// <summary>What a merge wrote, and how the table step is to lay the table out: written anew, or for the merge's keys alone.</summary>
    private readonly record struct Merged(Written Written, bool Rewrite, bool Scoped);

    /// <summary>
    /// The table step of a build: the table made ready where it is kept, then brought to the rows the merge laid out, a
    /// deadlock tried again from the same layout. A failure says the dimension was written and the table was not.
    /// </summary>
    private static async Task WriteBuiltTableAsync(
        SqlConnection connection, DimensionTableDatabase home, DimensionStage stage, string partition, DimensionTableSpec table, Merged merged, CancellationToken ct)
    {
        try
        {
            var prepared = await PrepareTableAsync(connection, home, stage.PartitionId, stage.DimensionId, table, rename: true, ct).ConfigureAwait(false);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await WriteTableAsync(connection, home, stage.PartitionId, stage.DimensionId, partition, prepared, merged.Rewrite, merged.Scoped, stage.DimensionRunId, ct)
                        .ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (attempt < DeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 200) * attempt), ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (StoppedBy(ex, ct))
        {
            throw new OperationCanceledException(
                $"The write of dimension {stage.DimensionId} was cancelled after the dimension was written and before its table {home.Target.Shown(table.Name)} was; the next build lays the table out whole.",
                ex, ct);
        }
        catch (Exception ex) when (ex is DeliveryException or SqlException or SqlFlowException or InvalidOperationException)
        {
            throw new DimensionTableWriteException(
                $"Dimension {stage.DimensionId} was written, but its table {home.Target.Shown(table.Name)} was not: {SqlFlow.Core.Secrets.SecretHygiene.RedactedMessage(ex.Message)} The next build lays the table out whole.",
                ex);
        }
    }

    private static async Task<Merged> MergeOnceAsync(
        OsduDbContext db, SqlConnection connection, string partition, DimensionStage stage, DimensionStagedWrite write,
        Func<Written, DeliveryDimensionRun, Task> close, bool elementRows, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        var transaction = (SqlTransaction)tx.GetDbTransaction();
        await LockAsync(connection, transaction, stage.PartitionId, stage.DimensionId, ct).ConfigureAwait(false);
        await CheckStagedAsync(connection, transaction, stage, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, StageSql, ct).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, LayoutTablesSql, ct).ConfigureAwait(false);
        await CopyAsync(connection, transaction, "#DimName", NameColumns, NameTypes, (write.Table?.Columns ?? []).Select(NameRow), null, ct).ConfigureAwait(false);

        // The staging, copied into the write's own tables where the database holds it: no row of the build passes
        // through the process writing it.
        await using (var copy = SqlServerDimensionStage.Command(connection, transaction, SqlServerDimensionStage.CopySql(stage), stage))
        {
            var first = write.Table?.Columns.FirstOrDefault(c => c.Element)?.Name;
            copy.Parameters.Add(new SqlParameter("@elements", SqlDbType.Bit) { Value = write.Table is { HasElements: true } && first is not null });
            copy.Parameters.Add(new SqlParameter("@firstElement", SqlDbType.NVarChar, DeliveryDimensionAttributeValue.MaxNameLength) { Value = (object?)first ?? DBNull.Value });
            await copy.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        Merged merged;
        await using (var command = Command(connection, transaction, MergeSql))
        {
            command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = stage.PartitionId });
            command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = stage.DimensionId });
            command.Parameters.Add(new SqlParameter("@run", SqlDbType.BigInt) { Value = stage.DimensionRunId });
            command.Parameters.Add(new SqlParameter("@now", SqlDbType.DateTime2) { Value = write.CompletedUtc });
            command.Parameters.Add(new SqlParameter("@fieldIndex", SqlDbType.NVarChar, 16) { Value = (object?)write.Field?.Index ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@nestedPath", SqlDbType.NVarChar, DeliveryDimension.MaxPathLength) { Value = (object?)write.Field?.NestedPath ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@aggregateBy", SqlDbType.NVarChar, DeliveryDimension.MaxAggregateByLength) { Value = (object?)write.Field?.AggregateBy ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@repeats", SqlDbType.Bit) { Value = write.Field?.Repeats ?? false });
            command.Parameters.Add(new SqlParameter("@collected", SqlDbType.NVarChar, -1) { Value = (object?)write.CollectedJson ?? DBNull.Value });
            command.Parameters.Add(new SqlParameter("@partial", SqlDbType.Bit) { Value = write.Partial });
            command.Parameters.Add(new SqlParameter("@records", SqlDbType.Bit) { Value = write.KeepsRecords });
            AddTable(command, write.Table, partition, elementRows, write.TableTarget);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException($"Writing dimension {stage.DimensionId} returned no counts.");
            }

            merged = new Merged(
                new Written(
                    new DimensionChangeCounts(
                        MembersAdded: reader.GetInt64(4), MembersRemoved: reader.GetInt64(5), MembersRestored: reader.GetInt64(6),
                        OriginalsAdded: reader.GetInt64(0), OriginalsRemoved: reader.GetInt64(1), OriginalsMoved: reader.GetInt64(2), OriginalsRestored: reader.GetInt64(3)),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11)),
                reader.GetBoolean(12),
                reader.GetBoolean(13));
        }

        // By its whole key, as every statement of the write reads: a seek of its own row, whatever the table holds.
        var run = await db.DeliveryDimensionRuns
            .FirstOrDefaultAsync(r => r.PartitionId == stage.PartitionId && r.DimensionId == stage.DimensionId && r.DimensionRunId == stage.DimensionRunId, ct)
            .ConfigureAwait(false)
            ?? throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Dimension build {stage.DimensionRunId} is not in the ledger, so it cannot be closed."));
        await close(merged.Written, run).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return merged;
    }

    /// <summary>
    /// Refuses a write whose staging is gone or no longer holds every key and value its build staged: a later build of the
    /// dimension took the build for one its process left, which it does only once a build has run for
    /// <see cref="DimensionStage.AbandonedAfter"/>, and dropped its staging, or the dimension was removed. Read under the
    /// dimension's write lock, which such a drop takes too, so nothing of the staging is dropped while the write copies it.
    /// </summary>
    private static async Task CheckStagedAsync(SqlConnection connection, SqlTransaction transaction, DimensionStage stage, CancellationToken ct)
    {
        await using var command = SqlServerDimensionStage.Command(connection, transaction, SqlServerDimensionStage.CountSql(stage), stage);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        var (values, members) = (reader.GetInt64(0), reader.GetInt64(1));
        if (values < 0 || members < 0)
        {
            throw new DeliveryException(SqlServerDimensionStage.GoneMessage(stage));
        }

        if (values != stage.Values || members != stage.Members)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"The staging of dimension build {stage.DimensionRunId} holds {values} of the {stage.Values} key(s) and {members} of the {stage.Members} value(s) it staged, so it is not what the build read. Nothing was written; build the dimension again."));
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is a statement the run's cancellation stopped, which SqlClient reports as a severe error
    /// on the command: told as a cancellation, the run says its build was cancelled, not that it failed.
    /// </summary>
    private static bool StoppedBy(Exception ex, CancellationToken ct)
        => ct.IsCancellationRequested && ex is SqlException or InvalidOperationException;

    private static async Task LockAsync(SqlConnection connection, SqlTransaction transaction, short partitionId, int dimensionId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, LockSql);
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = string.Create(CultureInfo.InvariantCulture, $"osdu-dimension:{partitionId}:{dimensionId}") });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = LockTimeoutMs });
        var granted = Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (granted < 0)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture,
                    $"Another build of dimension {dimensionId} held its write lock for more than {LockTimeoutMs / 1000} seconds (sp_getapplock answered {granted}), so this build wrote nothing. Build it again when the other has finished."));
        }
    }

    /// <summary>
    /// The entity type a record id names, its second segment (<c>master-data--Wellbore</c>); null for an id that names no
    /// record, or is longer than a key a dimension keeps.
    /// </summary>
    public static string? KeyRecordType(string recordId)
    {
        ArgumentNullException.ThrowIfNull(recordId);
        if (recordId.Length > DeliveryDimensionValue.MaxOriginalLength)
        {
            return null;
        }

        var segments = recordId.Split(':');
        return segments.Length >= 3 && segments[0].Length > 0 && segments[1].Length is > 0 and <= DeliveryDimensionKeyRecord.MaxEntityTypeLength
            ? segments[1]
            : null;
    }

    private static readonly string[] NameColumns = ["Name", "Ordinal", "Collected"];

    private static readonly Type[] NameTypes = [typeof(string), typeof(short), typeof(bool)];

    /// <summary>A declared attribute with its place among them, from 1.</summary>
    private static object?[] NameRow(DimensionTableColumn column, int index) => [column.Name, (short)(index + 1), column.Collected];

    /// <summary>
    /// Copies <paramref name="rows"/> into a temporary table as they are listed, never holding a second copy of them; nothing
    /// is sent when there is none. <paramref name="order"/> names the table's key when the rows come in its order, so the
    /// server appends them as they come instead of sorting them first.
    /// </summary>
    private static async Task CopyAsync(
        SqlConnection connection, SqlTransaction transaction, string table, string[] columns, Type[] types, IEnumerable<object?[]> rows, string[]? order,
        CancellationToken ct)
    {
        using var listed = rows.GetEnumerator();
        if (!listed.MoveNext())
        {
            return;
        }

        // Under a table lock a copy into a staging table is logged by the page, not by the row.
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, transaction)
        {
            DestinationTableName = table,
            BatchSize = 10_000,
            BulkCopyTimeout = WriteTimeoutSeconds,
            EnableStreaming = true,
        };
        foreach (var column in columns)
        {
            bulk.ColumnMappings.Add(column, column);
        }

        foreach (var column in order ?? [])
        {
            bulk.ColumnOrderHints.Add(column, SortOrder.Ascending);
        }

        await using var reader = new StreamingDataReader(columns, types, Rows(listed, ct).GetAsyncEnumerator(ct));
        await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
    }

    /// <summary>The rows <paramref name="listed"/> lists, from the one it stands on.</summary>
    private static async IAsyncEnumerable<object?[]> Rows(IEnumerator<object?[]> listed, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var count = 0;
        do
        {
            if (++count % 50_000 == 0)
            {
                // A long copy lets other work on the thread pool run, and notices a cancellation, now and then.
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            yield return listed.Current;
        }
        while (listed.MoveNext());
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql)
        => new(sql, connection, transaction) { CommandTimeout = WriteTimeoutSeconds };

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, sql);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
