using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class TemplateFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TemplatePath",
                schema: "osdu",
                table: "Mapping",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "RepositoryTemplate",
                schema: "osdu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Folder = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Problem = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryTemplate", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryTemplate_Kind_Version",
                schema: "osdu",
                table: "RepositoryTemplate",
                columns: new[] { "Kind", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryTemplate_RepoId_Path",
                schema: "osdu",
                table: "RepositoryTemplate",
                columns: new[] { "RepoId", "Path" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepositoryTemplate",
                schema: "osdu");

            migrationBuilder.DropColumn(
                name: "TemplatePath",
                schema: "osdu",
                table: "Mapping");
        }
    }
}
