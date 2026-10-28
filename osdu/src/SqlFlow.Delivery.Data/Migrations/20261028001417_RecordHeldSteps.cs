using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordHeldSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HeldSteps",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Record_PartitionId_FlowId_HeldSteps",
                schema: "osdu",
                table: "Record",
                columns: new[] { "PartitionId", "FlowId" },
                filter: "[HeldSteps] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "HeldSteps" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Record_PartitionId_FlowId_HeldSteps",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "HeldSteps",
                schema: "osdu",
                table: "Record");
        }
    }
}
