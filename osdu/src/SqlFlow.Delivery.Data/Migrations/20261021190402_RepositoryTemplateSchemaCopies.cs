using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class RepositoryTemplateSchemaCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RepositoryTemplate_Kind_Version",
                schema: "osdu",
                table: "RepositoryTemplate");

            migrationBuilder.AddColumn<string>(
                name: "SchemaJson",
                schema: "osdu",
                table: "RepositoryTemplate",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SchemaJson",
                schema: "osdu",
                table: "RepositoryTemplate");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryTemplate_Kind_Version",
                schema: "osdu",
                table: "RepositoryTemplate",
                columns: new[] { "Kind", "Version" });
        }
    }
}
