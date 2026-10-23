using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovalFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemovalKey",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    RemovalRunId = table.Column<long>(type: "bigint", nullable: false),
                    Side = table.Column<byte>(type: "tinyint", nullable: false),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceKeyJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RemovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: true),
                    RowNumber = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemovalKey", x => new { x.PartitionId, x.RemovalRunId, x.Side, x.DeliveryKey });
                });

            migrationBuilder.CreateTable(
                name: "RemovalRun",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    RemovalRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Entry = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    LedgerFlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryFlow = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Operation = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceReadUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowsRead = table.Column<long>(type: "bigint", nullable: false),
                    SourceKeys = table.Column<long>(type: "bigint", nullable: false),
                    ListedKeys = table.Column<long>(type: "bigint", nullable: false),
                    IngestionKeys = table.Column<long>(type: "bigint", nullable: false),
                    Compared = table.Column<long>(type: "bigint", nullable: false),
                    Detected = table.Column<long>(type: "bigint", nullable: false),
                    AwaitingApproval = table.Column<long>(type: "bigint", nullable: false),
                    Approved = table.Column<long>(type: "bigint", nullable: false),
                    Removed = table.Column<long>(type: "bigint", nullable: false),
                    AlreadyGone = table.Column<long>(type: "bigint", nullable: false),
                    Settled = table.Column<long>(type: "bigint", nullable: false),
                    KeptOut = table.Column<long>(type: "bigint", nullable: false),
                    Referenced = table.Column<long>(type: "bigint", nullable: false),
                    Shared = table.Column<long>(type: "bigint", nullable: false),
                    Busy = table.Column<long>(type: "bigint", nullable: false),
                    Failed = table.Column<long>(type: "bigint", nullable: false),
                    CameBack = table.Column<long>(type: "bigint", nullable: false),
                    Lifted = table.Column<long>(type: "bigint", nullable: false),
                    GuardPercent = table.Column<int>(type: "int", nullable: false),
                    GuardRecords = table.Column<long>(type: "bigint", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ActivityId = table.Column<long>(type: "bigint", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemovalRun", x => new { x.PartitionId, x.RemovalRunId });
                });

            migrationBuilder.CreateTable(
                name: "RemovedKey",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeliveryKey = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceKeyJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RemovedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(800)", maxLength: 800, nullable: true),
                    RowNumber = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Tries = table.Column<int>(type: "int", nullable: false),
                    RemovalFlow = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Entry = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    DetectedRunId = table.Column<long>(type: "bigint", nullable: false),
                    DoneRunId = table.Column<long>(type: "bigint", nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DoneUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemovedKey", x => new { x.PartitionId, x.FlowId, x.DeliveryKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemovalRun_PartitionId_FlowName_Entry_RemovalRunId",
                schema: "osdu",
                table: "RemovalRun",
                columns: new[] { "PartitionId", "FlowName", "Entry", "RemovalRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_RemovalRun_PartitionId_LedgerFlowId_RemovalRunId",
                schema: "osdu",
                table: "RemovalRun",
                columns: new[] { "PartitionId", "LedgerFlowId", "RemovalRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_RemovedKey_PartitionId_DetectedRunId",
                schema: "osdu",
                table: "RemovedKey",
                columns: new[] { "PartitionId", "DetectedRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_RemovedKey_PartitionId_FlowId_State_DeliveryKey",
                schema: "osdu",
                table: "RemovedKey",
                columns: new[] { "PartitionId", "FlowId", "State", "DeliveryKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemovalKey",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "RemovalRun",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "RemovedKey",
                schema: "osdu");
        }
    }
}
