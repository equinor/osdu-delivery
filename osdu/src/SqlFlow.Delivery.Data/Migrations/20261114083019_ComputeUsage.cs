using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class ComputeUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComputeUsage",
                schema: "osdu",
                columns: table => new
                {
                    EndedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsageId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MeasurementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Node = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    WorkId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    Team = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Repo = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Project = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Flow = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RequestedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WallClockMs = table.Column<long>(type: "bigint", nullable: false),
                    Cores = table.Column<int>(type: "int", nullable: false),
                    Slots = table.Column<int>(type: "int", nullable: false),
                    NodeSlots = table.Column<int>(type: "int", nullable: false),
                    ComputeUnits = table.Column<double>(type: "float", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeUsage", x => new { x.EndedUtc, x.UsageId });
                });

            migrationBuilder.CreateTable(
                name: "ComputeUsageDay",
                schema: "osdu",
                columns: table => new
                {
                    UsageDayId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Team = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Repo = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Project = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Flow = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    WorkCount = table.Column<long>(type: "bigint", nullable: false),
                    WallClockMs = table.Column<long>(type: "bigint", nullable: false),
                    ComputeUnits = table.Column<double>(type: "float", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComputeUsageDay", x => x.UsageDayId);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryTeam",
                schema: "osdu",
                columns: table => new
                {
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Folder = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Team = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryTeam", x => new { x.RepoId, x.Folder });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComputeUsage_MeasurementId",
                schema: "osdu",
                table: "ComputeUsage",
                column: "MeasurementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ComputeUsageDay_Day_Team_Repo_Project_Flow_Kind",
                schema: "osdu",
                table: "ComputeUsageDay",
                columns: new[] { "Day", "Team", "Repo", "Project", "Flow", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComputeUsage",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "ComputeUsageDay",
                schema: "osdu");

            migrationBuilder.DropTable(
                name: "RepositoryTeam",
                schema: "osdu");
        }
    }
}
