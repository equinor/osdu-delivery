using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// A dimension's keys, the records they were read through and held by, and its collected texts are found by their text,
    /// exactly, under a unique index, where they were found by a SHA-256 of it (osdu/docs/wiki/decisions/0058-dimension-keys-by-their-text.md).
    /// A key keeps the hash of its objects and of the attribute values read through its record, so a build writes only the
    /// keys whose content changed.
    /// </summary>
    /// <remarks>
    /// A dimension holds what its builds read from OSDU, and any build reads it again, so what cannot be carried over is let
    /// go of: the records keys were read through and held by were kept by the hash of their id alone, and are emptied, and
    /// every dimension's next build loads in full, keeping them again by their text, and lays its table out whole.
    /// </remarks>
    public partial class DimensionTextKeys : Migration
    {
        /// <summary>The longest key, record id and collected text kept (DeliveryDimensionValue.MaxKeyLength).</summary>
        private const int MaxKeyLength = 800;

        /// <summary>The tables whose indexes change, each locked a row at a time as every dimension table is (DimensionRowLocks).</summary>
        private static readonly string[] Tables = ["DimensionCollectedText", "DimensionKeyRecord", "DimensionRecord", "DimensionValue"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("TRUNCATE TABLE [osdu].[DimensionKeyRecord];");
            migrationBuilder.Sql("TRUNCATE TABLE [osdu].[DimensionRecord];");
            migrationBuilder.Sql("UPDATE [osdu].[Dimension] SET [LastFullRunId] = NULL, [LastFullBuiltUtc] = NULL, [TableRunId] = NULL;");

            // A key or a collected text longer than a dimension keeps now cannot stay (an OSDU id is about a hundred characters,
            // so none is expected): the key goes with its attribute values and objects, and its dimension's next build, which
            // lays its table out whole, finds what it can of it.
            migrationBuilder.Sql($"""
                DELETE a FROM [osdu].[DimensionAttribute] AS a
                WHERE EXISTS (SELECT 1 FROM [osdu].[DimensionValue] AS v WHERE v.[ValueId] = a.[ValueId] AND DATALENGTH(v.[Original]) > {MaxKeyLength * 2});
                DELETE e FROM [osdu].[DimensionElement] AS e
                WHERE EXISTS (SELECT 1 FROM [osdu].[DimensionValue] AS v WHERE v.[ValueId] = e.[ValueId] AND DATALENGTH(v.[Original]) > {MaxKeyLength * 2});
                DELETE v FROM [osdu].[DimensionValue] AS v WHERE DATALENGTH(v.[Original]) > {MaxKeyLength * 2};
                DELETE t FROM [osdu].[DimensionCollectedText] AS t WHERE DATALENGTH(t.[Text]) > {MaxKeyLength * 2};
                """);

            // What builds left staged has the shape they staged it in; the next build of each dimension stages its own.
            migrationBuilder.Sql("""
                DECLARE @drop nvarchar(max) = (
                    SELECT STRING_AGG(CONVERT(nvarchar(max), N'DROP TABLE [osdu].' + QUOTENAME(t.[name]) + N';'), N' ')
                    FROM sys.tables AS t
                    WHERE t.[schema_id] = SCHEMA_ID(N'osdu') AND t.[name] LIKE N'dimstage[_]%');
                IF @drop IS NOT NULL EXEC sys.sp_executesql @drop;
                """);

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_OriginalHash",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_RecordHash_ValueId",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_ValueId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_TextHash",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropColumn(
                name: "OriginalHash",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropColumn(
                name: "RecordHash",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.DropColumn(
                name: "RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropColumn(
                name: "TextHash",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.AlterColumn<string>(
                name: "Original",
                schema: "osdu",
                table: "DimensionValue",
                type: "nvarchar(800)",
                maxLength: 800,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(1024)",
                oldMaxLength: 1024,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<byte[]>(
                name: "AttributesHash",
                schema: "osdu",
                table: "DimensionValue",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ElementsHash",
                schema: "osdu",
                table: "DimensionValue",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecordKey",
                schema: "osdu",
                table: "DimensionRecord",
                type: "nvarchar(800)",
                maxLength: 800,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "RecordId",
                schema: "osdu",
                table: "DimensionKeyRecord",
                type: "nvarchar(800)",
                maxLength: 800,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "Text",
                schema: "osdu",
                table: "DimensionCollectedText",
                type: "nvarchar(800)",
                maxLength: 800,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(1024)",
                oldMaxLength: 1024,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<short>(
                name: "OriginalBytes",
                schema: "osdu",
                table: "DimensionValue",
                type: "smallint",
                nullable: false,
                computedColumnSql: "CAST(DATALENGTH([Original]) AS smallint)",
                stored: true);

            migrationBuilder.AddColumn<short>(
                name: "TextBytes",
                schema: "osdu",
                table: "DimensionCollectedText",
                type: "smallint",
                nullable: false,
                computedColumnSql: "CAST(DATALENGTH([Text]) AS smallint)",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_Original_OriginalBytes",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "Original", "OriginalBytes" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_RecordKey_ValueId",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "PartitionId", "DimensionId", "RecordKey", "ValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_RecordId",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_ValueId_RecordId",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "RecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_Text_TextBytes",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId", "Text", "TextBytes" },
                unique: true);

            LockRows(migrationBuilder);
        }

        /// <summary>Every index of the tables changed locks a row at a time, never a page, as DimensionRowLocks set them.</summary>
        private static void LockRows(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"ALTER INDEX ALL ON [osdu].[{table}] SET (ALLOW_PAGE_LOCKS = OFF);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("TRUNCATE TABLE [osdu].[DimensionKeyRecord];");
            migrationBuilder.Sql("TRUNCATE TABLE [osdu].[DimensionRecord];");
            migrationBuilder.Sql("UPDATE [osdu].[Dimension] SET [LastFullRunId] = NULL, [LastFullBuiltUtc] = NULL, [TableRunId] = NULL;");

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_Original_OriginalBytes",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_RecordKey_ValueId",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_RecordId",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_ValueId_RecordId",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_Text_TextBytes",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropColumn(
                name: "OriginalBytes",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropColumn(
                name: "TextBytes",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropColumn(
                name: "AttributesHash",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropColumn(
                name: "ElementsHash",
                schema: "osdu",
                table: "DimensionValue");

            migrationBuilder.DropColumn(
                name: "RecordKey",
                schema: "osdu",
                table: "DimensionRecord");

            migrationBuilder.DropColumn(
                name: "RecordId",
                schema: "osdu",
                table: "DimensionKeyRecord");

            migrationBuilder.AlterColumn<string>(
                name: "Original",
                schema: "osdu",
                table: "DimensionValue",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(800)",
                oldMaxLength: 800,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<byte[]>(
                name: "OriginalHash",
                schema: "osdu",
                table: "DimensionValue",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RecordHash",
                schema: "osdu",
                table: "DimensionRecord",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AlterColumn<string>(
                name: "Text",
                schema: "osdu",
                table: "DimensionCollectedText",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(800)",
                oldMaxLength: 800,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<byte[]>(
                name: "TextHash",
                schema: "osdu",
                table: "DimensionCollectedText",
                type: "binary(32)",
                fixedLength: true,
                maxLength: 32,
                nullable: false,
                defaultValue: new byte[0]);

            // Each statement compiled as it runs, after the column it fills is added, so a script of the migration runs whole.
            migrationBuilder.Sql("""
                EXEC (N'UPDATE [osdu].[DimensionValue] SET [OriginalHash] = CAST(HASHBYTES(''SHA2_256'', CAST([Original] COLLATE Latin1_General_100_BIN2_UTF8 AS varchar(4000))) AS binary(32));');
                EXEC (N'UPDATE [osdu].[DimensionCollectedText] SET [TextHash] = CAST(HASHBYTES(''SHA2_256'', CAST([Text] COLLATE Latin1_General_100_BIN2_UTF8 AS varchar(4000))) AS binary(32));');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_OriginalHash",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "OriginalHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_RecordHash_ValueId",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "PartitionId", "DimensionId", "RecordHash", "ValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "RecordHash" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_ValueId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "RecordHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_TextHash",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId", "TextHash" },
                unique: true);

            LockRows(migrationBuilder);
        }
    }
}
