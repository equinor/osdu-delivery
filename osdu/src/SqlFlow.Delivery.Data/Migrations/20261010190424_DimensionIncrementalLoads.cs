using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// A dimension's incremental loads (osdu/docs/reference/flow/dimension-incremental.md, Full and incremental loads): a build says how
    /// it read (<c>Mode</c>, <c>full</c> for every build before this one), the window an incremental load read and up to
    /// when a completed build read (<c>WindowFrom</c>, <c>WindowTo</c>), what an incremental load found changed and read
    /// again (<c>ChangedRecords</c>, <c>TouchedKeys</c>), and the unique key a load kept records by (<c>RecordKey</c>). The
    /// dimension keeps its last full load (<c>LastFullRunId</c>, <c>LastFullBuiltUtc</c>); <c>DimensionKeyRecord</c> keeps
    /// the records each key's label and attributes were read through, and <c>DimensionRecord</c> the keys each record held,
    /// by the record's unique key, so an incremental load finds the keys a changed record was read for or held.
    /// </summary>
    public partial class DimensionIncrementalLoads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ChangedRecords",
                schema: "osdu",
                table: "DimensionRun",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                schema: "osdu",
                table: "DimensionRun",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "full");

            migrationBuilder.AddColumn<string>(
                name: "RecordKey",
                schema: "osdu",
                table: "DimensionRun",
                type: "nvarchar(1600)",
                maxLength: 1600,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TouchedKeys",
                schema: "osdu",
                table: "DimensionRun",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WindowFrom",
                schema: "osdu",
                table: "DimensionRun",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WindowTo",
                schema: "osdu",
                table: "DimensionRun",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastFullBuiltUtc",
                schema: "osdu",
                table: "Dimension",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastFullRunId",
                schema: "osdu",
                table: "Dimension",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DimensionKeyRecord",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    KeyRecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    ValueId = table.Column<long>(type: "bigint", nullable: false),
                    RecordHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionKeyRecord", x => new { x.PartitionId, x.KeyRecordId });
                });

            migrationBuilder.CreateTable(
                name: "DimensionRecord",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionRecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    RecordHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    ValueId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionRecord", x => new { x.PartitionId, x.DimensionRecordId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_KeyRecordId",
                schema: "osdu",
                table: "DimensionKeyRecord",
                column: "KeyRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_EntityType",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "EntityType" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "RecordHash" });

            migrationBuilder.CreateIndex(
                name: "IX_DimensionKeyRecord_PartitionId_DimensionId_ValueId_RecordHash",
                schema: "osdu",
                table: "DimensionKeyRecord",
                columns: new[] { "PartitionId", "DimensionId", "ValueId", "RecordHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRecord_DimensionRecordId",
                schema: "osdu",
                table: "DimensionRecord",
                column: "DimensionRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_RecordHash_ValueId",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "PartitionId", "DimensionId", "RecordHash", "ValueId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DimensionRecord_PartitionId_DimensionId_ValueId",
                schema: "osdu",
                table: "DimensionRecord",
                columns: new[] { "PartitionId", "DimensionId", "ValueId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DimensionKeyRecord",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionRecord",
                schema: "osdu");

            migrationBuilder.DropColumn(
                name: "ChangedRecords",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropColumn(
                name: "Mode",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropColumn(
                name: "RecordKey",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropColumn(
                name: "TouchedKeys",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropColumn(
                name: "WindowFrom",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropColumn(
                name: "WindowTo",
                schema: "osdu",
                table: "DimensionRun");

            migrationBuilder.DropColumn(
                name: "LastFullBuiltUtc",
                schema: "osdu",
                table: "Dimension");

            migrationBuilder.DropColumn(
                name: "LastFullRunId",
                schema: "osdu",
                table: "Dimension");
        }
    }
}
