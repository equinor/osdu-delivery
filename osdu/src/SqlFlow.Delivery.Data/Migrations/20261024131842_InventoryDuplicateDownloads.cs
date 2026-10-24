using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class InventoryDuplicateDownloads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DuplicateKeyJson",
                schema: "osdu",
                table: "InventorySelection",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributesJson",
                schema: "osdu",
                table: "InventoryDuplicateCheck",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyedBy",
                schema: "osdu",
                table: "InventoryDuplicateCheck",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "KeyedUtc",
                schema: "osdu",
                table: "InventoryDuplicateCheck",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PickedUtc",
                schema: "osdu",
                table: "InventoryDuplicateCheck",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TableName",
                schema: "osdu",
                table: "InventoryDuplicateCheck",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DuplicateKeyJson",
                schema: "osdu",
                table: "InventorySelection");

            migrationBuilder.DropColumn(
                name: "AttributesJson",
                schema: "osdu",
                table: "InventoryDuplicateCheck");

            migrationBuilder.DropColumn(
                name: "KeyedBy",
                schema: "osdu",
                table: "InventoryDuplicateCheck");

            migrationBuilder.DropColumn(
                name: "KeyedUtc",
                schema: "osdu",
                table: "InventoryDuplicateCheck");

            migrationBuilder.DropColumn(
                name: "PickedUtc",
                schema: "osdu",
                table: "InventoryDuplicateCheck");

            migrationBuilder.DropColumn(
                name: "TableName",
                schema: "osdu",
                table: "InventoryDuplicateCheck");
        }
    }
}
