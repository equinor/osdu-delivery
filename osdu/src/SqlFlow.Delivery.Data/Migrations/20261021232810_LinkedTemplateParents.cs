using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkedTemplateParents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Parent",
                schema: "osdu",
                table: "RepositoryTemplate",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ClaimParentVersion",
                schema: "osdu",
                table: "RecordEvent",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentId",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<long>(
                name: "ParentVersion",
                schema: "osdu",
                table: "Record",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PendingParentVersion",
                schema: "osdu",
                table: "Record",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ParentVersion",
                schema: "osdu",
                table: "Attempt",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Record_PartitionId_ParentId",
                schema: "osdu",
                table: "Record",
                columns: new[] { "PartitionId", "ParentId" },
                filter: "[ParentId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Record_PartitionId_ParentId",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "Parent",
                schema: "osdu",
                table: "RepositoryTemplate");

            migrationBuilder.DropColumn(
                name: "ClaimParentVersion",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "ParentId",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "ParentVersion",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "PendingParentVersion",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "ParentVersion",
                schema: "osdu",
                table: "Attempt");
        }
    }
}
