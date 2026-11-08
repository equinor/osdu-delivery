using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class CacheItemFieldsHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CacheItem_Scope_ToSequence",
                schema: "osdu",
                table: "CacheItem");

            migrationBuilder.DropIndex(
                name: "IX_CacheItem_Scope_TypeName_RecordId_FromSequence",
                schema: "osdu",
                table: "CacheItem");

            migrationBuilder.AddColumn<byte[]>(
                name: "FieldsHash",
                schema: "osdu",
                table: "CacheItem",
                type: "binary(32)",
                nullable: false,
                computedColumnSql: "CAST(HASHBYTES('SHA2_256', [FieldsJson]) AS binary(32))",
                stored: false);

            migrationBuilder.CreateIndex(
                name: "IX_CacheItem_Scope_ToSequence",
                schema: "osdu",
                table: "CacheItem",
                columns: new[] { "Scope", "ToSequence" },
                filter: "[ToSequence] IS NOT NULL")
                .Annotation("SqlServer:Include", new[] { "FromSequence", "TypeName", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_CacheItem_Scope_TypeName_RecordId",
                schema: "osdu",
                table: "CacheItem",
                columns: new[] { "Scope", "TypeName", "RecordId" },
                filter: "[ToSequence] IS NULL")
                .Annotation("SqlServer:Include", new[] { "EntityType", "FieldsHash", "ToSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_CacheItem_Scope_TypeName_RecordId_FromSequence",
                schema: "osdu",
                table: "CacheItem",
                columns: new[] { "Scope", "TypeName", "RecordId", "FromSequence" },
                unique: true)
                .Annotation("SqlServer:Include", new[] { "ToSequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CacheItem_Scope_ToSequence",
                schema: "osdu",
                table: "CacheItem");

            migrationBuilder.DropIndex(
                name: "IX_CacheItem_Scope_TypeName_RecordId",
                schema: "osdu",
                table: "CacheItem");

            migrationBuilder.DropIndex(
                name: "IX_CacheItem_Scope_TypeName_RecordId_FromSequence",
                schema: "osdu",
                table: "CacheItem");

            migrationBuilder.DropColumn(
                name: "FieldsHash",
                schema: "osdu",
                table: "CacheItem");

            migrationBuilder.CreateIndex(
                name: "IX_CacheItem_Scope_ToSequence",
                schema: "osdu",
                table: "CacheItem",
                columns: new[] { "Scope", "ToSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_CacheItem_Scope_TypeName_RecordId_FromSequence",
                schema: "osdu",
                table: "CacheItem",
                columns: new[] { "Scope", "TypeName", "RecordId", "FromSequence" },
                unique: true);
        }
    }
}
