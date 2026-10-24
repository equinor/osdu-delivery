using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class LedgerFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LedgerFlow",
                schema: "osdu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Partitioned = table.Column<bool>(type: "bit", nullable: false),
                    DocumentHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RelativePath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Active = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerFlow", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerFlow_Kind_FlowName",
                schema: "osdu",
                table: "LedgerFlow",
                columns: new[] { "Kind", "FlowName" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerFlow_RepoId_FlowName",
                schema: "osdu",
                table: "LedgerFlow",
                columns: new[] { "RepoId", "FlowName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LedgerFlow",
                schema: "osdu");
        }
    }
}
