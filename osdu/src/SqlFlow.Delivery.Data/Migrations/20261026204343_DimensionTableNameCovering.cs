using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class DimensionTableNameCovering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Dimension_TableName",
                schema: "osdu",
                table: "Dimension");

            migrationBuilder.CreateIndex(
                name: "IX_Dimension_TableName",
                schema: "osdu",
                table: "Dimension",
                column: "TableName")
                .Annotation("SqlServer:Include", new[] { "FlowName", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Dimension_TableName",
                schema: "osdu",
                table: "Dimension");

            migrationBuilder.CreateIndex(
                name: "IX_Dimension_TableName",
                schema: "osdu",
                table: "Dimension",
                column: "TableName");
        }
    }
}
