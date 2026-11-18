using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Keeps the access OSDU holds a record under when its mapping restricts it while a value it marks
    /// <c>$restrictWhenMissing</c> is missing (osdu/docs/reference/flow/mapping-restricted-access.md):
    /// <c>osdu.Record.IncompleteRestrictionId</c>, <c>IncompleteSinceUtc</c>, <c>IncompleteMissing</c> and
    /// <c>IncompleteHash</c>, written when a try that sent the record's document lands, with the access a completion reports
    /// on <c>osdu.RecordEvent</c> (<c>AccessSent</c> and the same three columns). Two filtered indexes hold the restricted
    /// records alone: one a flow's filter, count and groups read, one a registered restriction's count reads. Nothing is
    /// written to existing rows: every record delivered before carries the access its mapping gave it.
    /// </summary>
    public partial class RecordRestrictedUntilComplete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AccessSent",
                schema: "osdu",
                table: "RecordEvent",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "IncompleteHash",
                schema: "osdu",
                table: "RecordEvent",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IncompleteMissing",
                schema: "osdu",
                table: "RecordEvent",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IncompleteRestrictionId",
                schema: "osdu",
                table: "RecordEvent",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "IncompleteHash",
                schema: "osdu",
                table: "Record",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IncompleteMissing",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IncompleteRestrictionId",
                schema: "osdu",
                table: "Record",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "IncompleteSinceUtc",
                schema: "osdu",
                table: "Record",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Record_IncompleteRestrictionId",
                schema: "osdu",
                table: "Record",
                column: "IncompleteRestrictionId",
                filter: "[IncompleteRestrictionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Record_PartitionId_FlowId_Incomplete",
                schema: "osdu",
                table: "Record",
                columns: new[] { "PartitionId", "FlowId", "IncompleteHash" },
                filter: "[IncompleteRestrictionId] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "IncompleteRestrictionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Record_IncompleteRestrictionId",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropIndex(
                name: "IX_Record_PartitionId_FlowId_Incomplete",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "AccessSent",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "IncompleteHash",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "IncompleteMissing",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "IncompleteRestrictionId",
                schema: "osdu",
                table: "RecordEvent");

            migrationBuilder.DropColumn(
                name: "IncompleteHash",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "IncompleteMissing",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "IncompleteRestrictionId",
                schema: "osdu",
                table: "Record");

            migrationBuilder.DropColumn(
                name: "IncompleteSinceUtc",
                schema: "osdu",
                table: "Record");
        }
    }
}
