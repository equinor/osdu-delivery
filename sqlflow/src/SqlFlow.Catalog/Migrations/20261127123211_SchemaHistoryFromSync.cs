using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class SchemaHistoryFromSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "RunId",
                schema: "catalog",
                table: "SchemaChange",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<string>(
                name: "ObjectKey",
                schema: "catalog",
                table: "SchemaChange",
                type: "nvarchar(900)",
                maxLength: 900,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                schema: "catalog",
                table: "SchemaChange",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Snapshot");

            migrationBuilder.CreateTable(
                name: "SchemaChangeColumn",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SchemaChangeId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ChangeType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    FromType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ToType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    FromNullable = table.Column<bool>(type: "bit", nullable: true),
                    ToNullable = table.Column<bool>(type: "bit", nullable: true),
                    FromOrdinal = table.Column<int>(type: "int", nullable: true),
                    ToOrdinal = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchemaChangeColumn", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SchemaChangeColumn_SchemaChange_SchemaChangeId",
                        column: x => x.SchemaChangeId,
                        principalSchema: "catalog",
                        principalTable: "SchemaChange",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SchemaChange_ObjectKey_OccurredUtc",
                schema: "catalog",
                table: "SchemaChange",
                columns: new[] { "ObjectKey", "OccurredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SchemaChangeColumn_Name",
                schema: "catalog",
                table: "SchemaChangeColumn",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_SchemaChangeColumn_SchemaChangeId",
                schema: "catalog",
                table: "SchemaChangeColumn",
                column: "SchemaChangeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchemaChangeColumn",
                schema: "catalog");

            migrationBuilder.DropIndex(
                name: "IX_SchemaChange_ObjectKey_OccurredUtc",
                schema: "catalog",
                table: "SchemaChange");

            migrationBuilder.DropColumn(
                name: "ObjectKey",
                schema: "catalog",
                table: "SchemaChange");

            migrationBuilder.DropColumn(
                name: "Origin",
                schema: "catalog",
                table: "SchemaChange");

            migrationBuilder.AlterColumn<Guid>(
                name: "RunId",
                schema: "catalog",
                table: "SchemaChange",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);
        }
    }
}
