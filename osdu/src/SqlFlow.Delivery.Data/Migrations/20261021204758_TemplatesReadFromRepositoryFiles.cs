using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class TemplatesReadFromRepositoryFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Template",
                schema: "osdu");

            migrationBuilder.DropIndex(
                name: "IX_Mapping_Kind_TemplateVersion",
                schema: "osdu",
                table: "Mapping");

            migrationBuilder.DropColumn(
                name: "SchemaJson",
                schema: "osdu",
                table: "RepositoryTemplate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SchemaJson",
                schema: "osdu",
                table: "RepositoryTemplate",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Mapping_Kind_TemplateVersion",
                schema: "osdu",
                table: "Mapping",
                columns: new[] { "Kind", "TemplateVersion" });

            migrationBuilder.CreateTable(
                name: "Template",
                schema: "osdu",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CapturedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CapturedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Origin = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SchemaJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Template", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Template_Kind_Version",
                schema: "osdu",
                table: "Template",
                columns: new[] { "Kind", "Version" },
                unique: true);
        }
    }
}
