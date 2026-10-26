using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class DimensionRowLocks : Migration
    {
        /// <summary>
        /// The tables holding a dimension's rows as one range of their clustered key (partition, dimension, id). Builds of
        /// different dimensions write them at once, each its own range; the page where two ranges meet holds rows of both.
        /// </summary>
        private static readonly string[] Tables =
        [
            "DimensionAttribute", "DimensionAttributeName", "DimensionChange", "DimensionCollectedText", "DimensionElement",
            "DimensionKeyRecord", "DimensionMember", "DimensionRecord", "DimensionRun", "DimensionValue",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A dimension's rows are locked a row at a time, never a page at a time (osdu/docs/reference/flow/dimension-build.md,
            // What a build does): two builds of different dimensions each lock only their own keys, where page locks on the
            // pages their ranges share deadlocked one with the other. EF cannot declare an index's lock options, so they are
            // set here, on every index of each table.
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"ALTER INDEX ALL ON [osdu].[{table}] SET (ALLOW_PAGE_LOCKS = OFF);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"ALTER INDEX ALL ON [osdu].[{table}] SET (ALLOW_PAGE_LOCKS = ON);");
            }
        }
    }
}
