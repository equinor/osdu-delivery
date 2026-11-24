using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The statements a build's write runs (osdu/docs/reference/concepts/dimension-tables.md): the tables what the build
/// staged is copied into, the attributes' names and places, the tables the dimension's table is laid out in and its
/// rows, and the merge that adds, changes, marks removed and brings back what the build found.
/// </summary>
internal static partial class SqlServerDimensionStore
{
    // The write's own tables, which what the build staged is copied into and the merge reads. Every key, record and text is
    // matched by its text, exactly; none is wider than a key a dimension keeps (DeliveryDimensionValue.MaxKeyLength), and every
    // index over one is nonclustered, whose key holds 1,700 bytes. SQL Server compares text as though the shorter were padded
    // with spaces, so a key or a text is matched by its length too ([OriginalBytes], [TextBytes]): 'GR' and 'GR ' are two keys.
    private static readonly string StageSql = $$"""
        CREATE TABLE #DimValue (
            [Seq] int NOT NULL,
            [Original] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [OriginalBytes] AS CAST(DATALENGTH([Original]) AS smallint) PERSISTED,
            [CleanValue] nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,
            [LeftOut] nvarchar(16) NULL,
            [Note] nvarchar(400) NULL,
            [Label] nvarchar(1024) NULL,
            [LabelFrom] nvarchar(1024) NULL,
            [Filter] nvarchar(4000) NULL,
            [Count] bigint NOT NULL,
            [Filterable] bit NOT NULL,
            [Attributes] nvarchar(max) NULL,
            [AttributesHash] binary(32) NULL,
            [MemberId] bigint NULL,
            [ValueId] bigint NULL,
            [AttributesChanged] bit NOT NULL DEFAULT (0),
            PRIMARY KEY NONCLUSTERED ([Original], [OriginalBytes]));
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
        CREATE TABLE #DimTouched (
            [ValueId] bigint NOT NULL);
        CREATE TABLE #DimCollected (
            [AttributeId] int NOT NULL PRIMARY KEY);
        CREATE TABLE #DimAttr (
            [Original] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [OriginalBytes] AS CAST(DATALENGTH([Original]) AS smallint) PERSISTED,
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
            [Text] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [TextBytes] AS CAST(DATALENGTH([Text]) AS smallint) PERSISTED,
            [Value] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Records] bigint NOT NULL);
        CREATE TABLE #DimElem (
            [Original] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [OriginalBytes] AS CAST(DATALENGTH([Original]) AS smallint) PERSISTED,
            [Elements] nvarchar(max) NOT NULL,
            [ElementsHash] binary(32) NOT NULL,
            [Objects] int NOT NULL,
            PRIMARY KEY NONCLUSTERED ([Original], [OriginalBytes]) WITH (IGNORE_DUP_KEY = ON));
        CREATE TABLE #DimElemChanged (
            [ValueId] bigint NOT NULL PRIMARY KEY,
            [Elements] nvarchar(max) NULL,
            [ElementsHash] binary(32) NULL);
        CREATE TABLE #DimRead (
            [Original] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [OriginalBytes] AS CAST(DATALENGTH([Original]) AS smallint) PERSISTED,
            [Record] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [EntityType] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL);
        CREATE TABLE #DimReadKeyed (
            [ValueId] bigint NOT NULL,
            [Record] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [EntityType] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            PRIMARY KEY NONCLUSTERED ([ValueId], [Record]));
        CREATE TABLE #DimGone (
            [Original] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [OriginalBytes] AS CAST(DATALENGTH([Original]) AS smallint) PERSISTED,
            PRIMARY KEY NONCLUSTERED ([Original], [OriginalBytes]));
        CREATE TABLE #DimRecRead (
            [Record] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY NONCLUSTERED);
        CREATE TABLE #DimRecHeld (
            [Record] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Original] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [OriginalBytes] AS CAST(DATALENGTH([Original]) AS smallint) PERSISTED);
        CREATE TABLE #DimRecKeyed (
            [Record] nvarchar({{DeliveryDimensionValue.MaxKeyLength}}) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [ValueId] bigint NOT NULL,
            PRIMARY KEY NONCLUSTERED ([Record], [ValueId]));
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
    // own columns, so a dimension of a few attributes lays out those alone), into #DimRow. A write changes the rows of the
    // keys it changed and no other: an incremental load's, the keys it read again and those it removed; a full load's, the
    // keys that arrived, left, moved or came back, and those whose own row, attribute values or objects it wrote (#DimTouched).
    // It lays out and compares those keys' rows alone (#DimScope), so its table write takes as long as what changed, not as
    // the dimension. A table whose rows are written anew (@rewrite: the attribute
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
        DECLARE @scoped bit = CASE WHEN @rewrite = 0 AND @behind = 0 THEN 1 ELSE 0 END;
        DELETE FROM #DimScope;
        IF @scoped = 1
            INSERT INTO #DimScope ([ValueId])
            SELECT s.[ValueId] FROM #DimValue AS s WHERE @partial = 1 AND s.[ValueId] IS NOT NULL
            UNION
            SELECT t.[ValueId] FROM #DimTouched AS t
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
        -- where it had one, or one where it had none) moved. A key is matched by its text, exactly, which a dimension's keys
        -- are unique by.
        INSERT INTO #DimChange ([ValueId], [Change], [FromMemberId], [ToMemberId])
        SELECT v.[ValueId], CASE WHEN v.[RemovedRunId] IS NOT NULL THEN N'restored' ELSE N'moved' END, v.[MemberId], s.[MemberId]
        FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK) INNER JOIN #DimValue AS s ON v.[Original] = s.[Original] AND v.[OriginalBytes] = s.[OriginalBytes]
        WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d
          AND (v.[RemovedRunId] IS NOT NULL OR ISNULL(v.[MemberId], -1) <> ISNULL(s.[MemberId], -1));

        -- A key whose own row changed may have a row of the dimension's table that did: the table step lays it out.
        UPDATE v SET v.[MemberSinceRunId] = CASE WHEN v.[RemovedRunId] IS NOT NULL OR ISNULL(v.[MemberId], -1) <> ISNULL(s.[MemberId], -1)
                THEN @run ELSE v.[MemberSinceRunId] END,
            v.[MemberId] = s.[MemberId], v.[LeftOut] = s.[LeftOut], v.[Note] = s.[Note], v.[Count] = s.[Count], v.[Filterable] = s.[Filterable],
            v.[Label] = s.[Label], v.[LabelFrom] = s.[LabelFrom], v.[Filter] = s.[Filter],
            v.[RemovedRunId] = NULL, v.[RemovedUtc] = NULL
        OUTPUT inserted.[ValueId] INTO #DimTouched ([ValueId])
        FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK) INNER JOIN #DimValue AS s ON v.[Original] = s.[Original] AND v.[OriginalBytes] = s.[OriginalBytes]
        WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d
          AND (v.[RemovedRunId] IS NOT NULL OR ISNULL(v.[MemberId], -1) <> ISNULL(s.[MemberId], -1) OR v.[Count] <> s.[Count]
               OR v.[Filterable] <> s.[Filterable] OR ISNULL(v.[LeftOut], N'') <> ISNULL(s.[LeftOut], N'')
               OR ISNULL(v.[Note], N'') <> ISNULL(s.[Note], N'')
               -- A label compares exactly: a record renamed only in case is a new label.
               OR ISNULL(v.[Label], N'') COLLATE Latin1_General_100_BIN2 <> ISNULL(s.[Label], N'') COLLATE Latin1_General_100_BIN2
               OR (v.[Label] IS NULL AND s.[Label] IS NOT NULL) OR (v.[Label] IS NOT NULL AND s.[Label] IS NULL)
               OR ISNULL(v.[LabelFrom], N'') <> ISNULL(s.[LabelFrom], N'')
               OR ISNULL(v.[Filter], N'') <> ISNULL(s.[Filter], N''));

        INSERT INTO [osdu].[DimensionValue] ([PartitionId], [DimensionId], [Original], [MemberId], [LeftOut], [Note], [Count],
            [Filterable], [Label], [LabelFrom], [Filter], [FirstSeenRunId], [FirstSeenUtc], [MemberSinceRunId])
        OUTPUT inserted.[ValueId], N'added', NULL, inserted.[MemberId] INTO #DimChange ([ValueId], [Change], [FromMemberId], [ToMemberId])
        SELECT @p, @d, s.[Original], s.[MemberId], s.[LeftOut], s.[Note], s.[Count], s.[Filterable], s.[Label], s.[LabelFrom],
            s.[Filter], @run, @now, @run
        FROM #DimValue AS s
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
            WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[Original] = s.[Original] AND v.[OriginalBytes] = s.[OriginalBytes])
        -- The new originals take their ids in the order the build listed them, so arrival order within a build is the build's.
        ORDER BY s.[Seq];

        IF @partial = 0
        BEGIN
            UPDATE v SET v.[RemovedRunId] = @run, v.[RemovedUtc] = @now
            OUTPUT inserted.[ValueId], N'removed', deleted.[MemberId], NULL INTO #DimChange ([ValueId], [Change], [FromMemberId], [ToMemberId])
            FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
            WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[RemovedRunId] IS NULL
              AND NOT EXISTS (SELECT 1 FROM #DimValue AS s WHERE s.[Original] = v.[Original] AND s.[OriginalBytes] = v.[OriginalBytes]);
        END
        ELSE
        BEGIN
            -- The keys an incremental load read again and found held by no record are removed, as a full load removes a key
            -- it no longer finds.
            UPDATE v SET v.[RemovedRunId] = @run, v.[RemovedUtc] = @now
            OUTPUT inserted.[ValueId], N'removed', deleted.[MemberId], NULL INTO #DimChange ([ValueId], [Change], [FromMemberId], [ToMemberId])
            FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
            INNER JOIN #DimGone AS g ON g.[Original] = v.[Original] AND g.[OriginalBytes] = v.[OriginalBytes]
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

        -- Every key the write holds under its number, looked up once: every row of the key below is matched by it.
        UPDATE s SET s.[ValueId] = v.[ValueId]
        FROM #DimValue AS s
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK)
            ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[Original] = s.[Original] AND v.[OriginalBytes] = s.[OriginalBytes];

        -- The attribute values read through a key's record travel with the key, with their hash: a key whose hash is the one
        -- its last write kept read the same, and its values stay as they are; the others' are written again below.
        UPDATE s SET s.[AttributesChanged] = 1
        FROM #DimValue AS s
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = s.[ValueId]
        WHERE v.[AttributesHash] <> s.[AttributesHash]
           OR (v.[AttributesHash] IS NULL AND s.[AttributesHash] IS NOT NULL)
           OR (v.[AttributesHash] IS NOT NULL AND s.[AttributesHash] IS NULL);

        -- Every attribute the build read has its number: an attribute's values are kept and joined by it, never by its
        -- name. A name read that the declaration does not hold (a write that carries none) still gets one.
        INSERT INTO #DimName ([Name], [Ordinal], [Collected])
        SELECT t.[Name], NULL, CAST(MAX(t.[Collected]) AS bit)
        FROM (SELECT DISTINCT [Name], 1 AS [Collected] FROM #DimAttr
              UNION ALL
              SELECT DISTINCT [Name], 1 FROM #DimText
              UNION ALL
              SELECT DISTINCT j.[Name] COLLATE Latin1_General_100_BIN2, 0
              FROM #DimValue AS s CROSS APPLY OPENJSON(s.[Attributes]) WITH ([Name] nvarchar(64) '$[0]') AS j
              WHERE s.[AttributesChanged] = 1 AND s.[Attributes] IS NOT NULL) AS t
        WHERE NOT EXISTS (SELECT 1 FROM #DimName AS s WHERE s.[Name] = t.[Name])
        GROUP BY t.[Name];

        """ + "\n" + NamesSql + "\n\n" + """
        -- The numbers are looked up once, beside the few names, and every row below is joined to them: a row of the
        -- build is never rewritten to carry one.
        UPDATE s SET s.[AttributeId] = n.[AttributeId]
        FROM #DimName AS s INNER JOIN [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK) ON n.[PartitionId] = @p AND n.[DimensionId] = @d AND n.[Name] = s.[Name];

        -- The attributes collected from a key's own records: their values are compared with those kept, a row each; every
        -- other attribute's values are read through the key's record and travel with the key.
        INSERT INTO #DimCollected ([AttributeId])
        SELECT n.[AttributeId] FROM [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK)
        WHERE n.[PartitionId] = @p AND n.[DimensionId] = @d AND n.[Collected] = 1;

        -- Each collected value the build read, under the numbers it is kept by: its key's and its attribute's. The rows
        -- kept are then matched to them on those numbers and the value, which name one row on either side. Matched
        -- through the key's text and the attribute's name instead, the only thing the two sides share directly is the
        -- value, and the server is free to pair them by it: every key holding a value (a country) with every other key
        -- holding it, which is millions of pairs for a few thousand keys.
        INSERT INTO #DimAttrKeyed ([ValueId], [AttributeId], [Value], [ValueFrom], [Records])
        SELECT s.[ValueId], dn.[AttributeId], t.[Value], t.[ValueFrom], t.[Records]
        FROM #DimAttr AS t
        INNER JOIN #DimName AS dn ON dn.[Name] = t.[Name]
        INNER JOIN #DimValue AS s ON s.[Original] = t.[Original] AND s.[OriginalBytes] = t.[OriginalBytes];

        -- The collected values of every original the build found are what it read: one no longer read is dropped, one read
        -- again rewritten only when where it was read or how many records hold it changed, and a new one added. An original
        -- the build did not find keeps what its last build read. A key any of it changed for has its table rows laid out.
        DELETE a
        OUTPUT deleted.[ValueId] INTO #DimTouched ([ValueId])
        FROM [osdu].[DimensionAttribute] AS a WITH (FORCESEEK)
        INNER JOIN #DimValue AS s ON s.[ValueId] = a.[ValueId]
        WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d
          AND a.[AttributeId] IN (SELECT c.[AttributeId] FROM #DimCollected AS c)
          AND NOT EXISTS (
              SELECT 1 FROM #DimAttrKeyed AS t
              WHERE t.[ValueId] = a.[ValueId] AND t.[AttributeId] = a.[AttributeId] AND t.[Value] = a.[Value]);
        DECLARE @attributesChanged bigint = @@ROWCOUNT;

        -- Where a value was read is compared exactly, byte for byte, as an id is.
        UPDATE a SET a.[ValueFrom] = t.[ValueFrom], a.[Records] = t.[Records]
        OUTPUT inserted.[ValueId] INTO #DimTouched ([ValueId])
        FROM [osdu].[DimensionAttribute] AS a WITH (FORCESEEK)
        INNER JOIN #DimAttrKeyed AS t ON t.[ValueId] = a.[ValueId] AND t.[AttributeId] = a.[AttributeId] AND t.[Value] = a.[Value]
        WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d
          AND (ISNULL(a.[ValueFrom], N'') COLLATE Latin1_General_100_BIN2 <> ISNULL(t.[ValueFrom], N'') COLLATE Latin1_General_100_BIN2
               OR (a.[ValueFrom] IS NULL AND t.[ValueFrom] IS NOT NULL)
               OR (a.[ValueFrom] IS NOT NULL AND t.[ValueFrom] IS NULL)
               OR ISNULL(a.[Records], -1) <> ISNULL(t.[Records], -1));
        SET @attributesChanged += @@ROWCOUNT;

        INSERT INTO [osdu].[DimensionAttribute] ([PartitionId], [DimensionId], [ValueId], [AttributeId], [Value], [ValueFrom], [Records])
        OUTPUT inserted.[ValueId] INTO #DimTouched ([ValueId])
        SELECT @p, @d, t.[ValueId], t.[AttributeId], t.[Value], t.[ValueFrom], t.[Records]
        FROM #DimAttrKeyed AS t
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[DimensionAttribute] AS a WITH (FORCESEEK)
            WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d AND a.[ValueId] = t.[ValueId] AND a.[AttributeId] = t.[AttributeId] AND a.[Value] = t.[Value])
        ORDER BY t.[ValueId], t.[AttributeId], t.[Value];
        SET @attributesChanged += @@ROWCOUNT;

        -- The values read through the record of a key whose hash changed are written again from what the build read, and the
        -- key keeps the hash; every other key's stay as they are.
        DELETE a
        FROM [osdu].[DimensionAttribute] AS a WITH (FORCESEEK)
        INNER JOIN #DimValue AS s ON s.[ValueId] = a.[ValueId]
        WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d AND s.[AttributesChanged] = 1
          AND a.[AttributeId] NOT IN (SELECT c.[AttributeId] FROM #DimCollected AS c);
        SET @attributesChanged += @@ROWCOUNT;

        INSERT INTO [osdu].[DimensionAttribute] ([PartitionId], [DimensionId], [ValueId], [AttributeId], [Value], [ValueFrom], [Records])
        SELECT @p, @d, s.[ValueId], dn.[AttributeId], j.[Value], j.[ValueFrom], NULL
        FROM #DimValue AS s
        CROSS APPLY OPENJSON(s.[Attributes]) WITH ([Name] nvarchar(64) '$[0]', [Value] nvarchar(256) '$[1]', [ValueFrom] nvarchar(1024) '$[2]') AS j
        INNER JOIN #DimName AS dn ON dn.[Name] = j.[Name] COLLATE Latin1_General_100_BIN2
        WHERE s.[AttributesChanged] = 1 AND s.[Attributes] IS NOT NULL
          AND dn.[AttributeId] NOT IN (SELECT c.[AttributeId] FROM #DimCollected AS c)
        ORDER BY s.[ValueId], dn.[AttributeId];
        SET @attributesChanged += @@ROWCOUNT;

        UPDATE v SET v.[AttributesHash] = s.[AttributesHash]
        FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
        INNER JOIN #DimValue AS s ON s.[ValueId] = v.[ValueId]
        WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND s.[AttributesChanged] = 1;

        INSERT INTO #DimTouched ([ValueId]) SELECT s.[ValueId] FROM #DimValue AS s WHERE s.[AttributesChanged] = 1;

        -- The objects of a key travel with their hash too: a key whose objects' hash is the one its last write kept read the
        -- same, and its elements stay as they are. Any other key the write holds (one whose record holds none now has none) is
        -- written again from what the build read, each object numbered from 1 in the order its record holds them, an object
        -- holding none of the fields one row of the first element field with no value. A declaration that reads no elements
        -- keeps none of them. A write that carries no declaration leaves them as they are.
        DECLARE @elementsChanged bigint = 0;
        IF @tableName IS NOT NULL
        BEGIN
            IF @elements = 0
            BEGIN
                DELETE e FROM [osdu].[DimensionElement] AS e WITH (FORCESEEK) WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d;
                SET @elementsChanged = @@ROWCOUNT;
                UPDATE v SET v.[ElementsHash] = NULL
                FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
                WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ElementsHash] IS NOT NULL;
            END
            ELSE
            BEGIN
                INSERT INTO #DimElemChanged ([ValueId], [Elements], [ElementsHash])
                SELECT s.[ValueId], x.[Elements], x.[ElementsHash]
                FROM #DimValue AS s
                INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = s.[ValueId]
                LEFT JOIN #DimElem AS x ON x.[Original] = s.[Original] AND x.[OriginalBytes] = s.[OriginalBytes]
                WHERE v.[ElementsHash] <> x.[ElementsHash]
                   OR (v.[ElementsHash] IS NULL AND x.[ElementsHash] IS NOT NULL)
                   OR (v.[ElementsHash] IS NOT NULL AND x.[ElementsHash] IS NULL);

                DELETE e
                FROM [osdu].[DimensionElement] AS e WITH (FORCESEEK)
                INNER JOIN #DimElemChanged AS c ON c.[ValueId] = e.[ValueId]
                WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d;
                SET @elementsChanged = @@ROWCOUNT;

                INSERT INTO [osdu].[DimensionElement] ([PartitionId], [DimensionId], [ValueId], [Seq], [AttributeId], [Value])
                SELECT @p, @d, c.[ValueId], CONVERT(int, o.[key]) + 1, dn.[AttributeId], f.[value]
                FROM #DimElemChanged AS c
                CROSS APPLY OPENJSON(c.[Elements]) AS o
                OUTER APPLY OPENJSON(o.[value]) AS f
                INNER JOIN #DimName AS dn ON dn.[Name] = ISNULL(f.[key], @firstElement) COLLATE Latin1_General_100_BIN2
                WHERE c.[Elements] IS NOT NULL
                ORDER BY c.[ValueId], CONVERT(int, o.[key]), dn.[AttributeId];
                SET @elementsChanged += @@ROWCOUNT;

                UPDATE v SET v.[ElementsHash] = c.[ElementsHash]
                FROM [osdu].[DimensionValue] AS v WITH (FORCESEEK)
                INNER JOIN #DimElemChanged AS c ON c.[ValueId] = v.[ValueId]
                WHERE v.[PartitionId] = @p AND v.[DimensionId] = @d;

                INSERT INTO #DimTouched ([ValueId]) SELECT c.[ValueId] FROM #DimElemChanged AS c;
            END;
        END;

        -- The records each key the write holds was read through are what it read, matched on the key and the record: one no
        -- longer read is dropped and a new one added. A key a load removes keeps none, since no incremental load reads it
        -- again for a record it was read through.
        INSERT INTO #DimReadKeyed ([ValueId], [Record], [EntityType])
        SELECT s.[ValueId], r.[Record], r.[EntityType]
        FROM #DimRead AS r
        INNER JOIN #DimValue AS s ON s.[Original] = r.[Original] AND s.[OriginalBytes] = r.[OriginalBytes];

        DELETE k
        FROM [osdu].[DimensionKeyRecord] AS k WITH (FORCESEEK)
        INNER JOIN #DimValue AS s ON s.[ValueId] = k.[ValueId]
        WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d
          AND NOT EXISTS (SELECT 1 FROM #DimReadKeyed AS t WHERE t.[ValueId] = k.[ValueId] AND t.[Record] = k.[RecordId]);

        DELETE k
        FROM [osdu].[DimensionKeyRecord] AS k WITH (FORCESEEK)
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[ValueId] = k.[ValueId]
        WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND v.[RemovedRunId] = @run;

        INSERT INTO [osdu].[DimensionKeyRecord] ([PartitionId], [DimensionId], [ValueId], [RecordId], [EntityType])
        SELECT @p, @d, t.[ValueId], t.[Record], t.[EntityType]
        FROM #DimReadKeyed AS t
        WHERE NOT EXISTS (
            SELECT 1 FROM [osdu].[DimensionKeyRecord] AS k WITH (FORCESEEK)
            WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND k.[ValueId] = t.[ValueId] AND k.[RecordId] = t.[Record])
        ORDER BY t.[ValueId], t.[Record];

        -- The keys each record held when the load read it, by the record's unique key: a full load's replace every row the
        -- dimension kept, an incremental load's replace those of the records it read. A full load that kept none (its flow
        -- loads in full) leaves none, and a key the load removes keeps none.
        INSERT INTO #DimRecKeyed ([Record], [ValueId])
        SELECT h.[Record], v.[ValueId]
        FROM #DimRecHeld AS h
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK)
            ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[Original] = h.[Original] AND v.[OriginalBytes] = h.[OriginalBytes];

        IF @records = 1
        BEGIN
            DELETE r
            FROM [osdu].[DimensionRecord] AS r WITH (FORCESEEK)
            WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d
              AND (@partial = 0 OR EXISTS (SELECT 1 FROM #DimRecRead AS s WHERE s.[Record] = r.[RecordKey]))
              AND NOT EXISTS (SELECT 1 FROM #DimRecKeyed AS k WHERE k.[Record] = r.[RecordKey] AND k.[ValueId] = r.[ValueId]);

            INSERT INTO [osdu].[DimensionRecord] ([PartitionId], [DimensionId], [RecordKey], [ValueId])
            SELECT @p, @d, k.[Record], k.[ValueId]
            FROM #DimRecKeyed AS k
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[DimensionRecord] AS r WITH (FORCESEEK)
                WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d AND r.[RecordKey] = k.[Record] AND r.[ValueId] = k.[ValueId])
            ORDER BY k.[Record], k.[ValueId];
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
            INSERT INTO [osdu].[DimensionCollectedText] ([PartitionId], [DimensionId], [AttributeId], [Text], [Value], [Records])
            SELECT @p, @d, dn.[AttributeId], t.[Text], t.[Value], t.[Records]
            FROM #DimText AS t INNER JOIN #DimName AS dn ON dn.[Name] = t.[Name]
            WHERE NOT EXISTS (
                SELECT 1 FROM [osdu].[DimensionCollectedText] AS c WITH (FORCESEEK)
                WHERE c.[PartitionId] = @p AND c.[DimensionId] = @d AND c.[AttributeId] = dn.[AttributeId] AND c.[Text] = t.[Text] AND c.[TextBytes] = t.[TextBytes])
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
            ISNULL(@rewrite, CAST(0 AS bit)), ISNULL(@scoped, CAST(0 AS bit)), @elementsChanged;
        """;
}
