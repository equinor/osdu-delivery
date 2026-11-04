using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class CacheFetchRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "FetchRecords",
                schema: "osdu",
                table: "CacheDefinition",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FetchRecords",
                schema: "osdu",
                table: "CacheDefinition");
        }
    }
}
