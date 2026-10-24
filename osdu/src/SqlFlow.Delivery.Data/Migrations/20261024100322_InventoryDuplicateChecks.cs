using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class InventoryDuplicateChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "KeptInventoryRecordId",
                schema: "osdu",
                table: "InventorySelectionItem",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DuplicateCheckId",
                schema: "osdu",
                table: "InventorySelection",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InventoryDuplicateCheck",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    InventoryDuplicateCheckId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InventoryId = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    KeyJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    KeyHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Phase = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    NotesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BuildRunId = table.Column<long>(type: "bigint", nullable: true),
                    Candidates = table.Column<long>(type: "bigint", nullable: false),
                    Checked = table.Column<long>(type: "bigint", nullable: false),
                    Keyed = table.Column<long>(type: "bigint", nullable: false),
                    Unkeyed = table.Column<long>(type: "bigint", nullable: false),
                    Outside = table.Column<long>(type: "bigint", nullable: false),
                    Changed = table.Column<long>(type: "bigint", nullable: false),
                    Groups = table.Column<long>(type: "bigint", nullable: false),
                    Copies = table.Column<long>(type: "bigint", nullable: false),
                    GroupsJudged = table.Column<long>(type: "bigint", nullable: false),
                    Decided = table.Column<long>(type: "bigint", nullable: false),
                    Review = table.Column<long>(type: "bigint", nullable: false),
                    ToRemove = table.Column<long>(type: "bigint", nullable: false),
                    Requests = table.Column<long>(type: "bigint", nullable: false),
                    SelectionId = table.Column<long>(type: "bigint", nullable: true),
                    ChosenUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryDuplicateCheck", x => new { x.PartitionId, x.InventoryDuplicateCheckId });
                });

            migrationBuilder.CreateTable(
                name: "InventoryDuplicateGroup",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    InventoryDuplicateCheckId = table.Column<long>(type: "bigint", nullable: false),
                    GroupNumber = table.Column<int>(type: "int", nullable: false),
                    KeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Copies = table.Column<int>(type: "int", nullable: false),
                    ValuesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    KeptInventoryRecordId = table.Column<long>(type: "bigint", nullable: true),
                    ChosenBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ChosenUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryDuplicateGroup", x => new { x.PartitionId, x.InventoryDuplicateCheckId, x.GroupNumber });
                });

            migrationBuilder.CreateTable(
                name: "InventoryDuplicateMember",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    InventoryDuplicateCheckId = table.Column<long>(type: "bigint", nullable: false),
                    InventoryRecordId = table.Column<long>(type: "bigint", nullable: false),
                    KeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    GroupNumber = table.Column<int>(type: "int", nullable: true),
                    IndexedVersion = table.Column<long>(type: "bigint", nullable: true),
                    ValuesJson = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    StorageState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    StoredVersion = table.Column<long>(type: "bigint", nullable: true),
                    Attributes = table.Column<int>(type: "int", nullable: true),
                    MentionedBy = table.Column<long>(type: "bigint", nullable: true),
                    Role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryDuplicateMember", x => new { x.PartitionId, x.InventoryDuplicateCheckId, x.InventoryRecordId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryDuplicateCheck_PartitionId_CreatedUtc",
                schema: "osdu",
                table: "InventoryDuplicateCheck",
                columns: new[] { "PartitionId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryDuplicateCheck_PartitionId_InventoryId_InventoryDuplicateCheckId",
                schema: "osdu",
                table: "InventoryDuplicateCheck",
                columns: new[] { "PartitionId", "InventoryId", "InventoryDuplicateCheckId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryDuplicateCheck_PartitionId_InventoryId_KeyHash_InventoryDuplicateCheckId",
                schema: "osdu",
                table: "InventoryDuplicateCheck",
                columns: new[] { "PartitionId", "InventoryId", "KeyHash", "InventoryDuplicateCheckId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryDuplicateGroup_PartitionId_InventoryDuplicateCheckId_Outcome_GroupNumber",
                schema: "osdu",
                table: "InventoryDuplicateGroup",
                columns: new[] { "PartitionId", "InventoryDuplicateCheckId", "Outcome", "GroupNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryDuplicateMember_PartitionId_InventoryDuplicateCheckId_GroupNumber_InventoryRecordId",
                schema: "osdu",
                table: "InventoryDuplicateMember",
                columns: new[] { "PartitionId", "InventoryDuplicateCheckId", "GroupNumber", "InventoryRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryDuplicateMember_PartitionId_InventoryDuplicateCheckId_KeyHash",
                schema: "osdu",
                table: "InventoryDuplicateMember",
                columns: new[] { "PartitionId", "InventoryDuplicateCheckId", "KeyHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryDuplicateCheck",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "InventoryDuplicateGroup",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "InventoryDuplicateMember",
                schema: "osdu");

            migrationBuilder.DropColumn(
                name: "KeptInventoryRecordId",
                schema: "osdu",
                table: "InventorySelectionItem");

            migrationBuilder.DropColumn(
                name: "DuplicateCheckId",
                schema: "osdu",
                table: "InventorySelection");
        }
    }
}
