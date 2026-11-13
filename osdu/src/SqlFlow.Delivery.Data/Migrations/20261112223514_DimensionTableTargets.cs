using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class DimensionTableTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TableConnection",
                schema: "osdu",
                table: "DimensionView",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TableDatabase",
                schema: "osdu",
                table: "DimensionView",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TableSchema",
                schema: "osdu",
                table: "DimensionView",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TableConnection",
                schema: "osdu",
                table: "Dimension",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TableDatabase",
                schema: "osdu",
                table: "Dimension",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TableRunId",
                schema: "osdu",
                table: "Dimension",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TableSchema",
                schema: "osdu",
                table: "Dimension",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            // Every table a build wrote before this migration was written in the build's own transaction, so it holds that
            // build's rows: a later build lays out only what it changed, as before.
            migrationBuilder.Sql("""
                UPDATE [osdu].[Dimension] SET [TableRunId] = [LastRunId] WHERE [TableName] IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TableConnection",
                schema: "osdu",
                table: "DimensionView");

            migrationBuilder.DropColumn(
                name: "TableDatabase",
                schema: "osdu",
                table: "DimensionView");

            migrationBuilder.DropColumn(
                name: "TableSchema",
                schema: "osdu",
                table: "DimensionView");

            migrationBuilder.DropColumn(
                name: "TableConnection",
                schema: "osdu",
                table: "Dimension");

            migrationBuilder.DropColumn(
                name: "TableDatabase",
                schema: "osdu",
                table: "Dimension");

            migrationBuilder.DropColumn(
                name: "TableRunId",
                schema: "osdu",
                table: "Dimension");

            migrationBuilder.DropColumn(
                name: "TableSchema",
                schema: "osdu",
                table: "Dimension");
        }
    }
}
