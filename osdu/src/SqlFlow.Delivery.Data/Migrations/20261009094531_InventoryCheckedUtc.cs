using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// When a reconcile last asked storage for an id a ledger expects and the inventory's read did not list
    /// (osdu/docs/reference/flow/inventory-builds.md, What a build does), so the ids beyond one build's <c>maxMissingChecks</c>
    /// are asked for by the next builds, least recently asked first. Existing rows start never asked, which puts them first.
    /// </summary>
    public partial class InventoryCheckedUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CheckedUtc",
                schema: "osdu",
                table: "InventoryRecord",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckedUtc",
                schema: "osdu",
                table: "InventoryRecord");
        }
    }
}
