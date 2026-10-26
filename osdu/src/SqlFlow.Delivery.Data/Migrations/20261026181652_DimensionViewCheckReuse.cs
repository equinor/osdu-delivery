using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class DimensionViewCheckReuse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReadVersion",
                schema: "osdu",
                table: "DimensionViewCheck",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReusedCheckId",
                schema: "osdu",
                table: "DimensionViewCheck",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TableVersion",
                schema: "osdu",
                table: "Dimension",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReadVersion",
                schema: "osdu",
                table: "DimensionViewCheck");

            migrationBuilder.DropColumn(
                name: "ReusedCheckId",
                schema: "osdu",
                table: "DimensionViewCheck");

            migrationBuilder.DropColumn(
                name: "TableVersion",
                schema: "osdu",
                table: "Dimension");
        }
    }
}
