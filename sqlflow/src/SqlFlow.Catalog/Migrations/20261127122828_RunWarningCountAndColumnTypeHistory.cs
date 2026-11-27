using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <summary>
    /// Adds a run's warning count (<c>Run.WarningCount</c>: its artifact's events at level warning, so a run that
    /// succeeded with warnings is told apart without changing its status) and the column type history
    /// (<c>ColumnTypeChange</c>: one row per change to an existing column's type a run reported, keyed by the object and
    /// column it changed). A run recorded before this keeps a count of zero: its events are not read again here, since
    /// counting them would scan the whole event log while the control plane starts.
    /// </summary>
    public partial class RunWarningCountAndColumnTypeHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "WarningCount",
                schema: "catalog",
                table: "Run",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ColumnTypeChange",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(776)", maxLength: 776, nullable: false),
                    Layer = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    FromType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ToType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Tier = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    RewritesTable = table.Column<bool>(type: "bit", nullable: false),
                    IsKey = table.Column<bool>(type: "bit", nullable: false),
                    RequiresOptIn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Rows = table.Column<long>(type: "bigint", nullable: true),
                    FailingValues = table.Column<long>(type: "bigint", nullable: true),
                    SampleJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ManualStepsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnTypeChange", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnTypeChange_ObjectKey_ColumnName_OccurredUtc",
                schema: "catalog",
                table: "ColumnTypeChange",
                columns: new[] { "ObjectKey", "ColumnName", "OccurredUtc" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ColumnTypeChange_RunId",
                schema: "catalog",
                table: "ColumnTypeChange",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ColumnTypeChange",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "WarningCount",
                schema: "catalog",
                table: "Run");
        }
    }
}
