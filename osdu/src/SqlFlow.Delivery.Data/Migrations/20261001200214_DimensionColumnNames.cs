using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SqlFlow.Delivery.Data.Migrations
{
    /// <summary>
    /// A dimension's table names its key's and its value's columns after what the dimension reads
    /// (osdu/docs/reference/flow/dimension-table.md, The dimension's table): <c>WellboreID</c> and <c>FacilityName</c>, not
    /// <c>key</c> and <c>value</c>.
    /// <list type="bullet">
    /// <item><c>Dimension.KeyColumn</c> and <c>Dimension.ValueColumn</c> record what the two columns of the dimension's own
    /// table are named now, which is what a reader names them by. The tables are no tables of the model, so this
    /// migration renames nothing in them: a table made before it holds <c>key</c> and <c>value</c>, which is what is
    /// recorded here, and the dimension's next build renames the two columns where they are, keeping every row and its
    /// number.</item>
    /// </list>
    /// Going back down gives the columns of the tables builds have renamed their old names again, since the code before
    /// this migration reads them as <c>key</c> and <c>value</c>.
    /// </summary>
    public partial class DimensionColumnNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KeyColumn",
                schema: "osdu",
                table: "Dimension",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValueColumn",
                schema: "osdu",
                table: "Dimension",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            // Every table a build has written so far holds its key and its value under these two names.
            migrationBuilder.Sql("""
                UPDATE [osdu].[Dimension] SET [KeyColumn] = N'key', [ValueColumn] = N'value' WHERE [TableName] IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The tables builds made are named by the rows that go with the columns, so their columns are renamed first:
            // each back to the name the code before this migration reads it by, where the table has it and that name is free.
            migrationBuilder.Sql("""
                DECLARE @table nvarchar(300), @key sysname, @value sysname, @column nvarchar(776);
                DECLARE named CURSOR LOCAL FAST_FORWARD FOR
                    SELECT DISTINCT N'[osdu].' + QUOTENAME([TableName]), [KeyColumn], [ValueColumn]
                    FROM [osdu].[Dimension]
                    WHERE [TableName] IS NOT NULL AND [KeyColumn] IS NOT NULL AND [ValueColumn] IS NOT NULL;
                OPEN named;
                FETCH NEXT FROM named INTO @table, @key, @value;
                WHILE @@FETCH_STATUS = 0
                BEGIN
                    IF @key <> N'key' COLLATE Latin1_General_100_BIN2 AND COL_LENGTH(@table, @key) IS NOT NULL AND COL_LENGTH(@table, N'key') IS NULL
                    BEGIN
                        SET @column = @table + N'.' + QUOTENAME(@key);
                        EXEC sys.sp_rename @objname = @column, @newname = N'key', @objtype = N'COLUMN';
                    END;

                    IF @value <> N'value' COLLATE Latin1_General_100_BIN2 AND COL_LENGTH(@table, @value) IS NOT NULL AND COL_LENGTH(@table, N'value') IS NULL
                    BEGIN
                        SET @column = @table + N'.' + QUOTENAME(@value);
                        EXEC sys.sp_rename @objname = @column, @newname = N'value', @objtype = N'COLUMN';
                    END;

                    FETCH NEXT FROM named INTO @table, @key, @value;
                END;
                CLOSE named;
                DEALLOCATE named;
                """);

            migrationBuilder.DropColumn(
                name: "KeyColumn",
                schema: "osdu",
                table: "Dimension");

            migrationBuilder.DropColumn(
                name: "ValueColumn",
                schema: "osdu",
                table: "Dimension");
        }
    }
}
