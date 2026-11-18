using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// Keeps on a record held at its render the rules it was rendered under (<c>osdu.Record.HeldRulesHash</c>): the hash of
    /// its render context without the cache version. A plan that reads the record's row under other rules (its mapping,
    /// template or parameters changed) renders it again rather than leaving the error the earlier rules gave. Nothing is
    /// written to the records held before: their rules are not known, so the next plan that reads their rows renders them
    /// again (osdu/docs/reference/concepts/record-lifecycle.md, A record held at its render).
    /// </summary>
    public partial class RecordHeldRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HeldRulesHash",
                schema: "osdu",
                table: "Record",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeldRulesHash",
                schema: "osdu",
                table: "Record");
        }
    }
}
