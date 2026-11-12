using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <inheritdoc />
    public partial class DimensionChangeTexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FromValue",
                schema: "osdu",
                table: "DimensionChange",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "Original",
                schema: "osdu",
                table: "DimensionChange",
                type: "nvarchar(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            migrationBuilder.AddColumn<string>(
                name: "ToValue",
                schema: "osdu",
                table: "DimensionChange",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true,
                collation: "Latin1_General_100_BIN2");

            // A build kept every original and value until now, so each change written before these columns takes their text
            // from the rows it names, as a build writes it from now on.
            migrationBuilder.Sql("""
                UPDATE c SET c.[Original] = v.[Original]
                FROM [osdu].[DimensionChange] AS c
                INNER JOIN [osdu].[DimensionValue] AS v
                    ON v.[PartitionId] = c.[PartitionId] AND v.[DimensionId] = c.[DimensionId] AND v.[ValueId] = c.[ValueId];

                UPDATE c SET c.[FromValue] = m.[Value]
                FROM [osdu].[DimensionChange] AS c
                INNER JOIN [osdu].[DimensionMember] AS m
                    ON m.[PartitionId] = c.[PartitionId] AND m.[DimensionId] = c.[DimensionId] AND m.[MemberId] = c.[FromMemberId];

                UPDATE c SET c.[ToValue] = m.[Value]
                FROM [osdu].[DimensionChange] AS c
                INNER JOIN [osdu].[DimensionMember] AS m
                    ON m.[PartitionId] = c.[PartitionId] AND m.[DimensionId] = c.[DimensionId] AND m.[MemberId] = c.[ToMemberId];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FromValue",
                schema: "osdu",
                table: "DimensionChange");

            migrationBuilder.DropColumn(
                name: "Original",
                schema: "osdu",
                table: "DimensionChange");

            migrationBuilder.DropColumn(
                name: "ToValue",
                schema: "osdu",
                table: "DimensionChange");
        }
    }
}
