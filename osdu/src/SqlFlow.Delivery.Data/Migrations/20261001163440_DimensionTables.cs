using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Dimensions as tables, joined by numbers (osdu/docs/reference/flow/dimension-table.md, The dimension's table).
    /// <list type="bullet">
    /// <item><c>Dimension.TableName</c> names the dimension's own table (<c>dim_&lt;dimension&gt;</c>), which
    /// builds make and widen; it is no table of the model, so this migration creates none, and going back down drops the
    /// ones builds made.</item>
    /// <item><c>DimensionAttributeName</c> gives each attribute of a dimension a number, its place among the attributes the
    /// dimension declares, and whether it is collected. It is filled from the names the attribute rows hold.</item>
    /// <item><c>DimensionAttribute</c> and <c>DimensionCollectedText</c> name their attribute by that number instead of its
    /// text, and are keyed by a number of their own, as every other table of a dimension is: the partition's number and
    /// an identity. Every row is kept: its name is looked up once, here.</item>
    /// <item><c>IX_DimensionValue_PartitionId_DimensionId_ValueId</c> reads a dimension's keys in the order they arrived
    /// as one range of the dimension alone.</item>
    /// </list>
    /// The two tables are rebuilt around their new keys, so the migration takes as long as they are large. A dimension
    /// built before it has no table until its flow runs again, or its table is first read.
    /// </summary>
    public partial class DimensionTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TableName",
                schema: "osdu",
                table: "Dimension",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Dimension_TableName",
                schema: "osdu",
                table: "Dimension",
                column: "TableName");

            migrationBuilder.CreateTable(
                name: "DimensionAttributeName",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    AttributeId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Ordinal = table.Column<short>(type: "smallint", nullable: true),
                    Collected = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionAttributeName", x => new { x.PartitionId, x.AttributeId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttributeName_AttributeId",
                schema: "osdu",
                table: "DimensionAttributeName",
                column: "AttributeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttributeName_PartitionId_DimensionId_Name",
                schema: "osdu",
                table: "DimensionAttributeName",
                columns: new[] { "PartitionId", "DimensionId", "Name" },
                unique: true);

            // Every attribute the rows name gets its number. Its place is not known here: the next build of the dimension,
            // or the first read of its table, gives it from the declaration.
            migrationBuilder.Sql("""
                INSERT INTO [osdu].[DimensionAttributeName] ([PartitionId], [DimensionId], [Name], [Ordinal], [Collected])
                SELECT x.[PartitionId], x.[DimensionId], x.[Name], NULL, CAST(MAX(x.[Collected]) AS bit)
                FROM (
                    SELECT [PartitionId], [DimensionId], [Name], CASE WHEN [Records] IS NULL THEN 0 ELSE 1 END AS [Collected]
                    FROM [osdu].[DimensionAttribute]
                    UNION ALL
                    SELECT [PartitionId], [DimensionId], [Name], 1 FROM [osdu].[DimensionCollectedText]) AS x
                GROUP BY x.[PartitionId], x.[DimensionId], x.[Name]
                ORDER BY x.[PartitionId], x.[DimensionId], x.[Name];
                """);

            // DimensionAttribute: keyed by its own number, its attribute by number.
            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_Name_Value",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.AddColumn<long>(
                name: "AttributeValueId",
                schema: "osdu",
                table: "DimensionAttribute",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("SqlServer:Identity", "1, 1");

            migrationBuilder.AddColumn<int>(
                name: "AttributeId",
                schema: "osdu",
                table: "DimensionAttribute",
                type: "int",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE a SET a.[AttributeId] = n.[AttributeId]
                FROM [osdu].[DimensionAttribute] AS a
                INNER JOIN [osdu].[DimensionAttributeName] AS n
                    ON n.[PartitionId] = a.[PartitionId] AND n.[DimensionId] = a.[DimensionId] AND n.[Name] = a.[Name];
                """);

            migrationBuilder.AlterColumn<int>(
                name: "AttributeId",
                schema: "osdu",
                table: "DimensionAttribute",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "Name",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "AttributeValueId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttribute_AttributeValueId",
                schema: "osdu",
                table: "DimensionAttribute",
                column: "AttributeValueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_ValueId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "AttributeId", "Value" },
                unique: true);

            // DimensionCollectedText: the same.
            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionCollectedText",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_Name_Value",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.AddColumn<long>(
                name: "TextId",
                schema: "osdu",
                table: "DimensionCollectedText",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("SqlServer:Identity", "1, 1");

            migrationBuilder.AddColumn<int>(
                name: "AttributeId",
                schema: "osdu",
                table: "DimensionCollectedText",
                type: "int",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE t SET t.[AttributeId] = n.[AttributeId]
                FROM [osdu].[DimensionCollectedText] AS t
                INNER JOIN [osdu].[DimensionAttributeName] AS n
                    ON n.[PartitionId] = t.[PartitionId] AND n.[DimensionId] = t.[DimensionId] AND n.[Name] = t.[Name];
                """);

            migrationBuilder.AlterColumn<int>(
                name: "AttributeId",
                schema: "osdu",
                table: "DimensionCollectedText",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "Name",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionCollectedText",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "TextId" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_TextId",
                schema: "osdu",
                table: "DimensionCollectedText",
                column: "TextId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_TextHash",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId", "TextHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "AttributeId", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_ValueId",
                schema: "osdu",
                table: "DimensionValue",
                columns: new[] { "PartitionId", "DimensionId", "ValueId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The tables builds made are named by the rows that go with the column, so they go first.
            migrationBuilder.Sql("""
                DECLARE @drop nvarchar(max) = N'';
                SELECT @drop = @drop + N'DROP TABLE IF EXISTS [osdu].' + QUOTENAME(t.[TableName]) + N';'
                FROM (SELECT DISTINCT [TableName] FROM [osdu].[Dimension] WHERE [TableName] IS NOT NULL) AS t;
                EXEC sys.sp_executesql @drop;
                """);

            migrationBuilder.DropIndex(
                name: "IX_DimensionValue_PartitionId_DimensionId_ValueId",
                schema: "osdu",
                table: "DimensionValue");

            // DimensionCollectedText: its attribute by name again, keyed by it.
            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionCollectedText",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_TextHash",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropIndex(
                name: "IX_DimensionCollectedText_TextId",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                schema: "osdu",
                table: "DimensionCollectedText",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.Sql("""
                UPDATE t SET t.[Name] = n.[Name]
                FROM [osdu].[DimensionCollectedText] AS t
                INNER JOIN [osdu].[DimensionAttributeName] AS n ON n.[PartitionId] = t.[PartitionId] AND n.[AttributeId] = t.[AttributeId];
                DELETE FROM [osdu].[DimensionCollectedText] WHERE [Name] IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                schema: "osdu",
                table: "DimensionCollectedText",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.DropColumn(
                name: "TextId",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.DropColumn(
                name: "AttributeId",
                schema: "osdu",
                table: "DimensionCollectedText");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionCollectedText",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "Name", "TextHash" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_Name_Value",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "Name", "Value" });

            // DimensionAttribute: the same.
            migrationBuilder.DropPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttribute_AttributeValueId",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_ValueId_AttributeId_Value",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                schema: "osdu",
                table: "DimensionAttribute",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.Sql("""
                UPDATE a SET a.[Name] = n.[Name]
                FROM [osdu].[DimensionAttribute] AS a
                INNER JOIN [osdu].[DimensionAttributeName] AS n ON n.[PartitionId] = a.[PartitionId] AND n.[AttributeId] = a.[AttributeId];
                DELETE FROM [osdu].[DimensionAttribute] WHERE [Name] IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                schema: "osdu",
                table: "DimensionAttribute",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.DropColumn(
                name: "AttributeValueId",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.DropColumn(
                name: "AttributeId",
                schema: "osdu",
                table: "DimensionAttribute");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DimensionAttribute",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "Name", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionAttribute_PartitionId_DimensionId_Name_Value",
                schema: "osdu",
                table: "DimensionAttribute",
                columns: new[] { "PartitionId", "DimensionId", "Name", "Value" });

            migrationBuilder.DropTable(
                name: "DimensionAttributeName",
                schema: "osdu");

            migrationBuilder.DropIndex(
                name: "IX_Dimension_TableName",
                schema: "osdu",
                table: "Dimension");

            migrationBuilder.DropColumn(
                name: "TableName",
                schema: "osdu",
                table: "Dimension");
        }
    }
}
