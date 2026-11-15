using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Keeps each cached value a render read once. Every set repeated, for every value it held, the partition, cached type,
    /// record id, path and kind the value was read from, as text, in a key of six columns the table was clustered on and its
    /// second index carried again, with the value's hash as 64 characters and its text: the render context came to outweigh
    /// everything else the ledger holds, though the sets of an estate read mostly the same values. A value
    /// (<c>osdu.CacheSetValue</c>) is now kept once, under an identity, and what a set holds (<c>osdu.CacheSetEntry</c>) is
    /// the set's number and the value's, clustered on an identity of its own.
    /// <para>
    /// What the sets hold is carried over, not emptied: delivered records point at their sets, and a cache change finds the
    /// records to update through them. The table is built anew beside the one it replaces and takes its name once every
    /// entry is in it, in the migration's transaction, so a migration that fails leaves the render context as it was. The
    /// former table is read, and then copied, in <see cref="Slices"/> statements each, the sets in order, since a
    /// statement of a migration has thirty minutes and the table can hold tens of millions of rows; the count written is
    /// checked against the count read before the former table goes.
    /// </para>
    /// </summary>
    public partial class CacheSetValues : Migration
    {
        private const string Schema = "osdu";

        /// <summary>How many statements the former table is read in, and copied in: each a run of sets holding about as many values.</summary>
        private const int Slices = 16;

        // Where the reading and the copying are cut, by the count each set keeps of its values; the values read, empty for
        // now, with the columns and collations of the table they are read from; and the tally of entries copied. The three
        // are temporary tables of the migration's session, read by the statements after this one.
        private const string PrepareSql = """
            SELECT b.[Slice], MAX(b.[SetId]) AS [Through]
            INTO #CacheSetCuts
            FROM (
                SELECT s.[SetId],
                       CASE WHEN s.[Total] = 0 THEN 1 WHEN s.[Before] >= s.[Total] THEN {0} ELSE 1 + CAST((s.[Before] * {0}) / s.[Total] AS int) END AS [Slice]
                FROM (
                    SELECT c.[SetId],
                           ISNULL(SUM(CAST(c.[EntryCount] AS bigint)) OVER (ORDER BY c.[SetId] ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS [Before],
                           SUM(CAST(c.[EntryCount] AS bigint)) OVER () AS [Total]
                    FROM [osdu].[CacheSet] AS c) AS s) AS b
            GROUP BY b.[Slice];

            SELECT f.[Scope], f.[TypeName], f.[ItemId], f.[Path], f.[Kind],
                   ISNULL(TRY_CONVERT(binary(32), f.[ValueHash], 2), 0x) AS [ValueHash], f.[ValueText], CAST(0 AS bigint) AS [Held]
            INTO #CacheSetValuesRead
            FROM [osdu].[CacheSetEntryFormer] AS f
            WHERE 1 = 0;

            SELECT CAST(0 AS bigint) AS [Written] INTO #CacheSetCopied;
            """;

        // One run of sets: those past the cut before this slice, through this slice's cut; the last slice takes every set
        // past the cut before it, so a row whose set is not among the sets (none the ledger leaves) is taken too. {0} is the
        // slice, {1} its cut and {2} the last slice.
        private const string SliceSql = """
            DECLARE @after bigint = (SELECT MAX(c.[Through]) FROM #CacheSetCuts AS c WHERE c.[Slice] < {0});
            DECLARE @through bigint = {1};

            """;

        // The values the slice's rows hold, each once (a place, as the former table's key compared it, and the hash of what
        // it held), with how many rows hold it. A value several slices hold is read once a slice, and kept once when the
        // values are written. A hash that is not hex (none the ledger wrote) becomes 32 zero bytes, which no value's hash
        // equals, so the value reads as changed, never as the same.
        private const string ReadSliceSql = """
            IF {0} = {2} OR @through IS NOT NULL
                INSERT INTO #CacheSetValuesRead ([Scope], [TypeName], [ItemId], [Path], [Kind], [ValueHash], [ValueText], [Held])
                SELECT f.[Scope], f.[TypeName], f.[ItemId], f.[Path], f.[Kind],
                       ISNULL(TRY_CONVERT(binary(32), f.[ValueHash], 2), 0x), MIN(f.[ValueText]), COUNT_BIG(*)
                FROM [osdu].[CacheSetEntryFormer] AS f
                WHERE (@after IS NULL OR f.[SetId] > @after) AND (@through IS NULL OR f.[SetId] <= @through)
                GROUP BY f.[Scope], f.[TypeName], f.[ItemId], f.[Path], f.[Kind], ISNULL(TRY_CONVERT(binary(32), f.[ValueHash], 2), 0x)
                OPTION (RECOMPILE);
            """;

        private const string WriteValuesSql = """
            INSERT INTO [osdu].[CacheSetValue] ([Scope], [TypeName], [ItemId], [Path], [Kind], [ValueHash], [ValueText])
            SELECT r.[Scope], r.[TypeName], r.[ItemId], r.[Path], r.[Kind], r.[ValueHash], MIN(r.[ValueText])
            FROM #CacheSetValuesRead AS r
            GROUP BY r.[Scope], r.[TypeName], r.[ItemId], r.[Path], r.[Kind], r.[ValueHash]
            ORDER BY r.[Scope], r.[TypeName], r.[ItemId], r.[Path], r.[Kind], r.[ValueHash];
            """;

        // What a set holds is written together, in the order of its values' numbers.
        private const string CopySliceSql = """
            DECLARE @copied bigint = 0;

            IF {0} = {2} OR @through IS NOT NULL
            BEGIN
                INSERT INTO [osdu].[CacheSetEntry] ([SetId], [ValueId])
                SELECT f.[SetId], v.[ValueId]
                FROM [osdu].[CacheSetEntryFormer] AS f
                INNER JOIN [osdu].[CacheSetValue] AS v
                    ON v.[Scope] = f.[Scope] AND v.[TypeName] = f.[TypeName] AND v.[ItemId] = f.[ItemId] AND v.[Path] = f.[Path] AND v.[Kind] = f.[Kind]
                   AND v.[ValueHash] = ISNULL(TRY_CONVERT(binary(32), f.[ValueHash], 2), 0x)
                WHERE (@after IS NULL OR f.[SetId] > @after) AND (@through IS NULL OR f.[SetId] <= @through)
                ORDER BY f.[SetId], v.[ValueId]
                OPTION (RECOMPILE);
                SET @copied = ROWCOUNT_BIG();

                UPDATE #CacheSetCopied SET [Written] = [Written] + @copied;
            END;
            """;

        private const string CheckCopySql = """
            DECLARE @read bigint = (SELECT ISNULL(SUM(r.[Held]), 0) FROM #CacheSetValuesRead AS r);
            DECLARE @written bigint = (SELECT c.[Written] FROM #CacheSetCopied AS c);

            IF @written <> @read
            BEGIN
                DECLARE @lost nvarchar(400) = CONCAT(
                    N'The render context was not carried over whole: ', @read, N' row(s) read from osdu.CacheSetEntry and ', @written,
                    N' written. Nothing was changed; the migration CacheSetValues can be run again.');
                THROW 51000, @lost, 1;
            END;

            DROP TABLE #CacheSetValuesRead;
            DROP TABLE #CacheSetCuts;
            DROP TABLE #CacheSetCopied;
            """;

        /// <summary>A statement over one slice of the sets: <see cref="SliceSql"/>, then <paramref name="statement"/>.</summary>
        private static string OverSlice(int slice, string statement)
        {
            var through = slice == Slices
                ? "NULL"
                : string.Format(CultureInfo.InvariantCulture, "(SELECT MAX(c.[Through]) FROM #CacheSetCuts AS c WHERE c.[Slice] <= {0})", slice);
            return string.Format(CultureInfo.InvariantCulture, SliceSql + statement, slice, through, Slices);
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The table being replaced keeps its rows under another name, and gives up its key's name, until every row is
            // carried over.
            migrationBuilder.Sql("EXEC sp_rename N'[osdu].[PK_CacheSetEntry]', N'PK_CacheSetEntryFormer', N'OBJECT';");
            migrationBuilder.RenameTable(name: "CacheSetEntry", schema: Schema, newName: "CacheSetEntryFormer", newSchema: Schema);

            migrationBuilder.CreateTable(
                name: "CacheSetValue",
                schema: Schema,
                columns: table => new
                {
                    ValueId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TypeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ValueHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    ValueText = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheSetValue", x => x.ValueId);
                });

            migrationBuilder.CreateTable(
                name: "CacheSetEntry",
                schema: Schema,
                columns: table => new
                {
                    EntryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SetId = table.Column<long>(type: "bigint", nullable: false),
                    ValueId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheSetEntry", x => x.EntryId);
                });

            migrationBuilder.Sql(string.Format(CultureInfo.InvariantCulture, PrepareSql, Slices));
            for (var slice = 1; slice <= Slices; slice++)
            {
                migrationBuilder.Sql(OverSlice(slice, ReadSliceSql));
            }

            migrationBuilder.Sql(WriteValuesSql);

            // The values are unique before a set names one, so each former row finds exactly one value.
            migrationBuilder.CreateIndex(
                name: "IX_CacheSetValue_Scope_TypeName_ItemId_Path_Kind_ValueHash",
                schema: Schema,
                table: "CacheSetValue",
                columns: new[] { "Scope", "TypeName", "ItemId", "Path", "Kind", "ValueHash" },
                unique: true);

            for (var slice = 1; slice <= Slices; slice++)
            {
                migrationBuilder.Sql(OverSlice(slice, CopySliceSql));
            }

            migrationBuilder.Sql(CheckCopySql);

            migrationBuilder.CreateIndex(
                name: "IX_CacheSetEntry_SetId_ValueId",
                schema: Schema,
                table: "CacheSetEntry",
                columns: new[] { "SetId", "ValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CacheSetEntry_ValueId",
                schema: Schema,
                table: "CacheSetEntry",
                column: "ValueId");

            migrationBuilder.DropTable(name: "CacheSetEntryFormer", schema: Schema);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("EXEC sp_rename N'[osdu].[PK_CacheSetEntry]', N'PK_CacheSetEntryLatter', N'OBJECT';");
            migrationBuilder.RenameTable(name: "CacheSetEntry", schema: Schema, newName: "CacheSetEntryLatter", newSchema: Schema);

            migrationBuilder.CreateTable(
                name: "CacheSetEntry",
                schema: Schema,
                columns: table => new
                {
                    SetId = table.Column<long>(type: "bigint", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TypeName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ItemId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ValueHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ValueText = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CacheSetEntry", x => new { x.SetId, x.Scope, x.TypeName, x.ItemId, x.Path, x.Kind });
                });

            // Each row gets back the text of its value, and its hash as the lower-case hex the ledger wrote. An entry whose
            // value is gone (none the ledger leaves) would be lost, so the rows written are counted against those read.
            migrationBuilder.Sql("""
                DECLARE @read bigint = (SELECT COUNT_BIG(*) FROM [osdu].[CacheSetEntryLatter]);
                DECLARE @written bigint;

                INSERT INTO [osdu].[CacheSetEntry] ([SetId], [Scope], [TypeName], [ItemId], [Path], [Kind], [ValueHash], [ValueText])
                SELECT l.[SetId], v.[Scope], v.[TypeName], v.[ItemId], v.[Path], v.[Kind], LOWER(CONVERT(char(64), v.[ValueHash], 2)), v.[ValueText]
                FROM [osdu].[CacheSetEntryLatter] AS l
                INNER JOIN [osdu].[CacheSetValue] AS v ON v.[ValueId] = l.[ValueId]
                ORDER BY l.[SetId], v.[Scope], v.[TypeName], v.[ItemId], v.[Path], v.[Kind];

                SET @written = ROWCOUNT_BIG();
                IF @written <> @read
                BEGIN
                    DECLARE @lost nvarchar(400) = CONCAT(
                        N'The render context was not carried back whole: ', @read, N' row(s) read from osdu.CacheSetEntry and ', @written,
                        N' written. Nothing was changed.');
                    THROW 51000, @lost, 1;
                END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_CacheSetEntry_Scope_TypeName_ItemId",
                schema: Schema,
                table: "CacheSetEntry",
                columns: new[] { "Scope", "TypeName", "ItemId" });

            migrationBuilder.DropTable(name: "CacheSetEntryLatter", schema: Schema);
            migrationBuilder.DropTable(name: "CacheSetValue", schema: Schema);
        }
    }
}
