using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class LineageColumnMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LineageColumnMapping",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RepoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PipelineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Flow = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TargetObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: false),
                    TargetObjectName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    TargetColumn = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Ordinal = table.Column<int>(type: "int", nullable: false),
                    SourceOrdinal = table.Column<int>(type: "int", nullable: false),
                    SourceObjectKey = table.Column<string>(type: "nvarchar(900)", maxLength: 900, nullable: true),
                    SourceObjectName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    SourceColumn = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineageColumnMapping", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LineageColumnMapping_PipelineId",
                schema: "catalog",
                table: "LineageColumnMapping",
                column: "PipelineId");

            migrationBuilder.CreateIndex(
                name: "IX_LineageColumnMapping_RepoId",
                schema: "catalog",
                table: "LineageColumnMapping",
                column: "RepoId");

            migrationBuilder.CreateIndex(
                name: "IX_LineageColumnMapping_SourceObjectKey",
                schema: "catalog",
                table: "LineageColumnMapping",
                column: "SourceObjectKey");

            migrationBuilder.CreateIndex(
                name: "IX_LineageColumnMapping_TargetObjectKey",
                schema: "catalog",
                table: "LineageColumnMapping",
                column: "TargetObjectKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LineageColumnMapping",
                schema: "catalog");
        }
    }
}
