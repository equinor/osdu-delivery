using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class InventoryDuplicateKeyIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryDuplicateMember_PartitionId_InventoryDuplicateCheckId_KeyHash",
                schema: "osdu",
                table: "InventoryDuplicateMember");

            migrationBuilder.DropIndex(
                name: "IX_InventoryDuplicateCheck_PartitionId_InventoryId_KeyHash_InventoryDuplicateCheckId",
                schema: "osdu",
                table: "InventoryDuplicateCheck");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InventoryDuplicateMember_PartitionId_InventoryDuplicateCheckId_KeyHash",
                schema: "osdu",
                table: "InventoryDuplicateMember",
                columns: new[] { "PartitionId", "InventoryDuplicateCheckId", "KeyHash" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryDuplicateCheck_PartitionId_InventoryId_KeyHash_InventoryDuplicateCheckId",
                schema: "osdu",
                table: "InventoryDuplicateCheck",
                columns: new[] { "PartitionId", "InventoryId", "KeyHash", "InventoryDuplicateCheckId" });
        }
    }
}
