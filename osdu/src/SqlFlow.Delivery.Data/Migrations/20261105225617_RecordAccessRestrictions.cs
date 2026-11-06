using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordAccessRestrictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RestrictedFromAcl",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RestrictedUtc",
                schema: "osdu",
                table: "Record",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RestrictionId",
                schema: "osdu",
                table: "Record",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccessRestriction",
                schema: "osdu",
                columns: table => new
                {
                    RestrictionId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Partition = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Viewers = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Owners = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRestriction", x => x.RestrictionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Record_PartitionId_FlowId_Restricted",
                schema: "osdu",
                table: "Record",
                columns: new[] { "PartitionId", "FlowId" },
                filter: "[RestrictionId] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "RestrictionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Record_RestrictionId",
                schema: "osdu",
                table: "Record",
                column: "RestrictionId",
                filter: "[RestrictionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRestriction_Partition_Name",
                schema: "osdu",
                table: "AccessRestriction",
                columns: new[] { "Partition", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessRestriction",
                schema: "osdu");

            migrationBuilder.DropIndex(
                name: "IX_Record_PartitionId_FlowId_Restricted",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropIndex(
                name: "IX_Record_RestrictionId",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "RestrictedFromAcl",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "RestrictedUtc",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "RestrictionId",
                schema: "osdu",
                table: "Record");
        }
    }
}
