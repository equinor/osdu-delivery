using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordChildLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChildLinkKey",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.CreateIndex(
                name: "IX_Record_PartitionId_ChildLinkKey",
                schema: "osdu",
                table: "Record",
                columns: new[] { "PartitionId", "ChildLinkKey" },
                filter: "[ChildLinkKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Record_PartitionId_ChildLinkKey",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "ChildLinkKey",
                schema: "osdu",
                table: "Record");
        }
    }
}
