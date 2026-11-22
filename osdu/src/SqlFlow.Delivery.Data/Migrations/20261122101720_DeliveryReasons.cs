using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeliveryReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RenderCommit",
                schema: "osdu",
                table: "RecordEvent",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RenderPaths",
                schema: "osdu",
                table: "RecordEvent",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RenderCommit",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RenderPaths",
                schema: "osdu",
                table: "Record",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Triggers",
                schema: "osdu",
                table: "Attempt",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RenderCommit",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "RenderPaths",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "RenderCommit",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "RenderPaths",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "Triggers",
                schema: "osdu",
                table: "Attempt");
        }
    }
}
