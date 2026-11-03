using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class InventoryRestores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Restored",
                schema: "osdu",
                table: "InventoryRemoval",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RestoresRemovalId",
                schema: "osdu",
                table: "InventoryRemoval",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Restored",
                schema: "osdu",
                table: "InventoryRemoval");

            migrationBuilder.DropColumn(
                name: "RestoresRemovalId",
                schema: "osdu",
                table: "InventoryRemoval");
        }
    }
}
