using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Keeps the inventories of inventory flows (osdu/docs/reference/flow/inventory-builds.md, The tables an inventory keeps):
    /// <c>osdu.Inventory</c>, one row per inventory of a flow in a partition; <c>osdu.InventoryRun</c>, one per build or
    /// reconcile; <c>osdu.InventoryRecord</c>, one per id an inventory holds or a ledger expects, unique on the inventory and
    /// the id, indexed in order within the inventory for the grid of every id and the export, by finding for the report, and by
    /// OSDU id across inventories; <c>osdu.InventoryVersion</c>, every version of a record an inventory reads them for; and
    /// <c>osdu.InventoryScan</c>, the ids a build listed, staged until it merges them. Every table is keyed by the partition
    /// first. No other table changes; the tables are created empty, and going back down drops them.
    /// </summary>
    public partial class InventoryFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Inventory",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    InventoryId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ReadMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Versions = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OwnersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OwnersSource = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastBuildRunId = table.Column<long>(type: "bigint", nullable: true),
                    LastBuiltUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastReconcileRunId = table.Column<long>(type: "bigint", nullable: true),
                    LastReconciledUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inventory", x => new { x.PartitionId, x.InventoryId });
                });

            migrationBuilder.CreateTable(
                name: "InventoryRecord",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    InventoryRecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InventoryId = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: true),
                    CreateUser = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreateTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifyUser = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ModifyTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ChangedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    GoneUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    VersionsAt = table.Column<long>(type: "bigint", nullable: true),
                    Finding = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FindingUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LedgerFlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LedgerStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    LedgerVersion = table.Column<long>(type: "bigint", nullable: true),
                    ArtifactId = table.Column<long>(type: "bigint", nullable: true),
                    ArtifactState = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryRecord", x => new { x.PartitionId, x.InventoryRecordId });
                });

            migrationBuilder.CreateTable(
                name: "InventoryRun",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    InventoryRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InventoryId = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Operation = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReadMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Listed = table.Column<long>(type: "bigint", nullable: false),
                    Pages = table.Column<int>(type: "int", nullable: false),
                    Requests = table.Column<long>(type: "bigint", nullable: false),
                    Added = table.Column<long>(type: "bigint", nullable: false),
                    Changed = table.Column<long>(type: "bigint", nullable: false),
                    Gone = table.Column<long>(type: "bigint", nullable: false),
                    Returned = table.Column<long>(type: "bigint", nullable: false),
                    MissingChecked = table.Column<long>(type: "bigint", nullable: false),
                    FindingsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OwnersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryRun", x => new { x.PartitionId, x.InventoryRunId });
                });

            migrationBuilder.CreateTable(
                name: "InventoryScan",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    ScanId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InventoryRunId = table.Column<long>(type: "bigint", nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: true),
                    CreateUser = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreateTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ModifyUser = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ModifyTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryScan", x => new { x.PartitionId, x.ScanId });
                });

            migrationBuilder.CreateTable(
                name: "InventoryVersion",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    InventoryRecordId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryVersion", x => new { x.PartitionId, x.InventoryRecordId, x.Version });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Inventory_PartitionId_FlowId_Name",
                schema: "osdu",
                table: "Inventory",
                columns: new[] { "PartitionId", "FlowId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryRecord_PartitionId_InventoryId_Finding_InventoryRecordId",
                schema: "osdu",
                table: "InventoryRecord",
                columns: new[] { "PartitionId", "InventoryId", "Finding", "InventoryRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryRecord_PartitionId_InventoryId_InventoryRecordId",
                schema: "osdu",
                table: "InventoryRecord",
                columns: new[] { "PartitionId", "InventoryId", "InventoryRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryRecord_PartitionId_InventoryId_TargetId",
                schema: "osdu",
                table: "InventoryRecord",
                columns: new[] { "PartitionId", "InventoryId", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryRecord_PartitionId_TargetId",
                schema: "osdu",
                table: "InventoryRecord",
                columns: new[] { "PartitionId", "TargetId" })
                .Annotation("SqlServer:Include", new[] { "InventoryId", "Finding", "GoneUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryRun_PartitionId_InventoryId_InventoryRunId",
                schema: "osdu",
                table: "InventoryRun",
                columns: new[] { "PartitionId", "InventoryId", "InventoryRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryScan_PartitionId_InventoryRunId_TargetId",
                schema: "osdu",
                table: "InventoryScan",
                columns: new[] { "PartitionId", "InventoryRunId", "TargetId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Inventory",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "InventoryRecord",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "InventoryRun",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "InventoryScan",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "InventoryVersion",
                schema: "osdu");
        }
    }
}
