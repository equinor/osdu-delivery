using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// A dimension's elements (osdu/docs/reference/flow/dimension-elements.md, Elements): the objects of a nested array each key's
    /// records hold, a row each in the dimension's table. The dimension keeps what it declares of them (<c>ElementsJson</c>),
    /// and <c>DimensionElement</c> keeps each field of each object, by the key, the object's place and the field.
    /// </summary>
    public partial class DimensionElements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ElementsJson",
                schema: "osdu",
                table: "Dimension",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DimensionElement",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    ElementValueId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    ValueId = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<int>(type: "int", nullable: false),
                    AttributeId = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionElement", x => new { x.PartitionId, x.ElementValueId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionElement_ElementValueId",
                schema: "osdu",
                table: "DimensionElement",
                column: "ElementValueId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionElement_PartitionId_DimensionId_ValueId_Seq_AttributeId",
                schema: "osdu",
                table: "DimensionElement",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "Seq", "AttributeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DimensionElement",
                schema: "osdu");

            migrationBuilder.DropColumn(
                name: "ElementsJson",
                schema: "osdu",
                table: "Dimension");
        }
    }
}
