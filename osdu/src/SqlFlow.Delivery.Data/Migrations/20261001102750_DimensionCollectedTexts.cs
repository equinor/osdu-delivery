using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// The texts a collected attribute's values stand for, in their own table (osdu/docs/reference/concepts/dimension-tables.md, The
    /// tables a dimension keeps): <c>DimensionCollectedText</c>, one row per attribute and text, indexed by value so a
    /// search finds the texts of the values it picks in one seek, however many values an attribute collects. A build
    /// before it kept them in the dimension's <c>CollectedJson</c>, which is cleared here, so a search picking a collected
    /// value asks for the dimension to be built again rather than finding no text; every key keeps its values.
    /// </summary>
    public partial class DimensionCollectedTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DimensionCollectedText",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TextHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Value = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Records = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionCollectedText", x => new { x.PartitionId, x.DimensionId, x.Name, x.TextHash });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionCollectedText_PartitionId_DimensionId_Name_Value",
                schema: "osdu",
                table: "DimensionCollectedText",
                columns: new[] { "PartitionId", "DimensionId", "Name", "Value" });

            migrationBuilder.Sql("UPDATE [osdu].[Dimension] SET [CollectedJson] = NULL WHERE [CollectedJson] IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DimensionCollectedText",
                schema: "osdu");
        }
    }
}
