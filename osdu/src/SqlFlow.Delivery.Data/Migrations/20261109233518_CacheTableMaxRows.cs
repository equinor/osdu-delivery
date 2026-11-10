using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class CacheTableMaxRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxRows",
                schema: "osdu",
                table: "CacheDefinition",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxRows",
                schema: "osdu",
                table: "CacheDefinition");
        }
    }
}
