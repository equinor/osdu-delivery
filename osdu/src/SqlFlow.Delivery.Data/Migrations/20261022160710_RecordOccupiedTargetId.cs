using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordOccupiedTargetId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OccupiedTargetId",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<long>(
                name: "OccupiedTargetVersion",
                schema: "osdu",
                table: "Record",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OccupiedTargetId",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "OccupiedTargetVersion",
                schema: "osdu",
                table: "Record");
        }
    }
}
