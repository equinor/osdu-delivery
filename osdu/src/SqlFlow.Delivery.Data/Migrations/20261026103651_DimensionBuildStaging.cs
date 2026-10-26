using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class DimensionBuildStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A running dimension build stages what it reads here as it reads it, and its write merges the staging: what a
            // build holds no longer grows with its dimension. Each table keeps a dimension's builds' rows together, each build's
            // in the order it staged them.
            migrationBuilder.CreateTable(
                name: "DimensionStageAttribute",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    DimensionRunId = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    KeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Value = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ValueFrom = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Records = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionStageAttribute", x => new { x.PartitionId, x.DimensionId, x.DimensionRunId, x.Seq });
                });

            migrationBuilder.CreateTable(
                name: "DimensionStageElement",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    DimensionRunId = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    KeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Record = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true, collation: "Latin1_General_100_BIN2"),
                    Value = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionStageElement", x => new { x.PartitionId, x.DimensionId, x.DimensionRunId, x.Seq });
                });

            migrationBuilder.CreateTable(
                name: "DimensionStageKey",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    DimensionRunId = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    KeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    Gone = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionStageKey", x => new { x.PartitionId, x.DimensionId, x.DimensionRunId, x.Seq });
                });

            migrationBuilder.CreateTable(
                name: "DimensionStageMember",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    DimensionRunId = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Records = table.Column<long>(type: "bigint", nullable: false),
                    RecordsExact = table.Column<bool>(type: "bit", nullable: false),
                    Originals = table.Column<int>(type: "int", nullable: false),
                    Unfilterable = table.Column<int>(type: "int", nullable: false),
                    Filter = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilterParts = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionStageMember", x => new { x.PartitionId, x.DimensionId, x.DimensionRunId, x.Seq });
                });

            migrationBuilder.CreateTable(
                name: "DimensionStageRecord",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    DimensionRunId = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    RecordHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    KeyHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: true),
                    EntityType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionStageRecord", x => new { x.PartitionId, x.DimensionId, x.DimensionRunId, x.Seq });
                });

            migrationBuilder.CreateTable(
                name: "DimensionStageText",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    DimensionRunId = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    TextHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Value = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Records = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionStageText", x => new { x.PartitionId, x.DimensionId, x.DimensionRunId, x.Seq });
                });

            migrationBuilder.CreateTable(
                name: "DimensionStageValue",
                schema: "osdu",
                columns: table => new
                {
                    PartitionId = table.Column<short>(type: "smallint", nullable: false),
                    DimensionId = table.Column<int>(type: "int", nullable: false),
                    DimensionRunId = table.Column<long>(type: "bigint", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    OriginalHash = table.Column<byte[]>(type: "binary(32)", fixedLength: true, maxLength: 32, nullable: false),
                    Original = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false, collation: "Latin1_General_100_BIN2"),
                    CleanValue = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true, collation: "Latin1_General_100_BIN2"),
                    LeftOut = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Label = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    LabelFrom = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Filter = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    Filterable = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DimensionStageValue", x => new { x.PartitionId, x.DimensionId, x.DimensionRunId, x.Seq });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DimensionStageAttribute",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionStageElement",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionStageKey",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionStageMember",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionStageRecord",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionStageText",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "DimensionStageValue",
                schema: "osdu");
        }
    }
}
