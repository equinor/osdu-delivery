using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovalRunMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "Marked",
                schema: "osdu",
                table: "RemovalRun",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "Unmarked",
                schema: "osdu",
                table: "RemovalRun",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Marked",
                schema: "osdu",
                table: "RemovalRun");

            migrationBuilder.DropColumn(
                name: "Unmarked",
                schema: "osdu",
                table: "RemovalRun");
        }
    }
}
