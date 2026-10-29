using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class DownloadFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Download",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DownloadId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FlowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FlowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ReadMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastRunId = table.Column<long>(type: "bigint", nullable: true),
                    LastCompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Download", x => new { x.PartitionId, x.DownloadId });
                });

            migrationBuilder.CreateTable(
                name: "DownloadRecord",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DownloadRecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DownloadId = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false, collation: "Latin1_General_100_CI_AS"),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    GoneUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WrittenVersion = table.Column<long>(type: "bigint", nullable: true),
                    WrittenUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WrittenRunId = table.Column<long>(type: "bigint", nullable: true),
                    Bytes = table.Column<long>(type: "bigint", nullable: true),
                    Sha256 = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true),
                    Problem = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProblemUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadRecord", x => new { x.PartitionId, x.DownloadRecordId });
                });

            migrationBuilder.CreateTable(
                name: "DownloadRun",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DownloadRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DownloadId = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReadMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Rewrite = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Listed = table.Column<long>(type: "bigint", nullable: false),
                    Pages = table.Column<int>(type: "int", nullable: false),
                    Requests = table.Column<long>(type: "bigint", nullable: false),
                    Added = table.Column<long>(type: "bigint", nullable: false),
                    Changed = table.Column<long>(type: "bigint", nullable: false),
                    Gone = table.Column<long>(type: "bigint", nullable: false),
                    Returned = table.Column<long>(type: "bigint", nullable: false),
                    Written = table.Column<long>(type: "bigint", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    Missing = table.Column<long>(type: "bigint", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadRun", x => new { x.PartitionId, x.DownloadRunId });
                });

            migrationBuilder.CreateTable(
                name: "DownloadScan",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    ScanId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DownloadRunId = table.Column<long>(type: "bigint", nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Kind = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: true),
                    PlainName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: true, collation: "Latin1_General_100_CI_AS"),
                    DistinctName = table.Column<string>(type: "nvarchar(240)", maxLength: 240, nullable: false, collation: "Latin1_General_100_CI_AS")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadScan", x => new { x.PartitionId, x.ScanId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Download_PartitionId_FlowId_Name",
                schema: "osdu",
                table: "Download",
                columns: new[] { "PartitionId", "FlowId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRecord_PartitionId_DownloadId_DownloadRecordId",
                schema: "osdu",
                table: "DownloadRecord",
                columns: new[] { "PartitionId", "DownloadId", "DownloadRecordId" })
                .Annotation("SqlServer:Include", new[] { "TargetId", "Version", "WrittenVersion", "GoneUtc", "FileName" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRecord_PartitionId_DownloadId_FileName",
                schema: "osdu",
                table: "DownloadRecord",
                columns: new[] { "PartitionId", "DownloadId", "FileName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRecord_PartitionId_DownloadId_TargetId",
                schema: "osdu",
                table: "DownloadRecord",
                columns: new[] { "PartitionId", "DownloadId", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRecord_PartitionId_TargetId",
                schema: "osdu",
                table: "DownloadRecord",
                columns: new[] { "PartitionId", "TargetId" })
                .Annotation("SqlServer:Include", new[] { "DownloadId", "FileName", "WrittenVersion", "GoneUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadRun_PartitionId_DownloadId_DownloadRunId",
                schema: "osdu",
                table: "DownloadRun",
                columns: new[] { "PartitionId", "DownloadId", "DownloadRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadScan_PartitionId_DownloadRunId_TargetId",
                schema: "osdu",
                table: "DownloadScan",
                columns: new[] { "PartitionId", "DownloadRunId", "TargetId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Download",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DownloadRecord",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DownloadRun",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DownloadScan",
                schema: "osdu");
        }
    }
}
