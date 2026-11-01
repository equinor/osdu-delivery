using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The script a person runs to see what an attribute of a mapping reads: written from the layout a read pages with, so it names
/// the rows a delivery reads, and shows the source as it stands, with each record's child rows left joined and no two child
/// datasets multiplying each other. Whether it runs is <see cref="SqlServerIngestionSourceTests"/>'s to prove, on a real table.
/// </summary>
public sealed class SourceQueryTests
{
    private const string Flow = "welldb-delivery";

    private static readonly IngestionDataset Curves = new(
        "curves", SourceObjectName.Parse("[Osdu].[silver].[Curve]"), [new("log_key", "log_id")], ["curve_no"], "UpdatedDate_DW", "DeletedDate_DW", 1000);

    private static readonly IngestionDataset Aliases = new(
        "aliases", SourceObjectName.Parse("[Osdu].[silver].[Alias]"), [new("log_key", "log_id")], [], null, null, 1000);

    private static SourceDescription Source(
        string? primaryKey = "RecId", IReadOnlyList<string>? scopeValues = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? columns = null)
    {
        var scope = scopeValues is null ? [] : new[] { new SourceScopeValues("log_source", "logSource", "varchar(20)", scopeValues) };
        return new SourceDescription(
            new IngestionLayout
            {
                Record = SourceObjectName.Parse("[Osdu].[silver].[WellLog]"),
                Key = [new SourceKeyColumn("log_id", "nvarchar(50)")],
                PrimaryKey = primaryKey is null ? null : new SourceKeyColumn(primaryKey, "bigint"),
                Updated = "UpdatedDate_DW",
                Deleted = "DeletedDate_DW",
                Scope = scope.Select(s => new IngestionScope(s.Column, s.Parameter, s.Values.Count)).ToList(),
                Datasets = [Aliases, Curves],
            },
            columns ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [SourceDatasets.Record] = ["RecId", "log_id", "name", "depth", "unit", "well_uwi"],
                ["curves"] = ["log_key", "curve_no", "curve_id", "top", "name"],
                ["aliases"] = ["log_key", "alias"],
            },
            scope,
            ["FileName_DW", "RowNumber_DW"]);
    }

    private static TemplatePath Path(string text)
        => TemplatePath.TryParse(text, out var path, out var error) ? path! : throw new InvalidOperationException(error);

    private static SourceQueryAnswer Query(
        string entries, string? target, SourceDescription? source = null, long maxRows = 1000, IReadOnlyList<KeyTuple>? keys = null, string? baseData = null)
    {
        var mapping = baseData is null ? TestSchema.Mapping(entries) : TestSchema.Mapping(entries, baseData);
        return SourceQuery.For(source ?? Source(), mapping, Flow, null, target is null ? null : Path(target), maxRows, keys ?? []);
    }

    private const string CurvesEntry = """
        Curves:
          $forEach: curves
          $item:
            CurveID: { $from: curve_id }
            TopDepth: { $from: top }
        """;

    [Fact]
    public void An_attribute_of_the_record_reads_its_key_its_column_and_where_the_row_came_from_over_the_rows_a_delivery_reads()
    {
        var query = Query(string.Empty, "osdu.data.Depth", Source(scopeValues: ["COMPOSITE"]));

        Assert.Equal("osdu.data.Depth", query.Target);
        Assert.Equal(1, query.Entries);
        Assert.Equal([new SourceQueryRead("dataset.depth", "Osdu.silver.WellLog")], query.Reads);
        Assert.Empty(query.Missing);

        var sql = query.Sql!;
        Assert.Contains("-- The scope: logSource", sql, StringComparison.Ordinal);
        Assert.Contains("DECLARE @scope0 varchar(20) = N'COMPOSITE';", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT TOP (1000) r.[log_id], r.[depth], r.[FileName_DW], r.[RowNumber_DW]", sql, StringComparison.Ordinal);
        Assert.Contains("FROM [Osdu].[silver].[WellLog] r", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE r.[log_source] = @scope0 AND r.[DeletedDate_DW] IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.[RecId];", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("JOIN", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_budget_of_none_returns_every_row_and_a_table_without_an_identity_orders_by_the_key()
    {
        var sql = Query(string.Empty, "osdu.data.Depth", Source(primaryKey: null), maxRows: 0).Sql!;

        Assert.Contains("SELECT r.[log_id], r.[depth]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.[log_id];", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_item_left_joins_its_child_rows_so_a_record_with_none_still_shows()
    {
        var query = Query(CurvesEntry, "osdu.data.Curves[].CurveID");

        Assert.Equal([new SourceQueryRead("dataset.curves.curve_id", "Osdu.silver.Curve")], query.Reads);
        var sql = query.Sql!;
        Assert.Contains("c.[curve_id] AS [curves.curve_id]", sql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN [Osdu].[silver].[Curve] c ON c.[log_key] = r.[log_id] AND c.[DeletedDate_DW] IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.[RecId], c.[curve_no];", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_column_of_a_coalesce_is_read_each_once()
    {
        var query = Query("""
            Unit:
              $coalesce:
                - $from: unit
                - $from: well_uwi
                - $from: unit
            """, "osdu.data.Unit");

        Assert.Equal(["dataset.unit", "dataset.well_uwi"], query.Reads.Select(r => r.Column));
        Assert.Contains("r.[unit], r.[well_uwi]", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_child_datasets_have_a_statement_each_so_they_do_not_multiply_each_others_rows()
    {
        var sql = Query(CurvesEntry + "\nAliases:\n  $forEach: aliases\n  $item:\n    Alias: { $from: alias }\n", null).Sql!;

        Assert.Equal(3, sql.Split("SELECT", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, sql.Split("LEFT JOIN [Osdu].[silver].[Curve]", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, sql.Split("LEFT JOIN [Osdu].[silver].[Alias]", StringSplitOptions.None).Length - 1);
        Assert.Contains("c.[alias] AS [aliases.alias]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_reads_what_it_holds()
    {
        var query = Query(CurvesEntry, "osdu.data.Curves");

        Assert.Equal(["dataset.curves.curve_id", "dataset.curves.top"], query.Reads.Select(r => r.Column));
    }

    [Fact]
    public void A_column_the_tables_do_not_hold_is_named_and_left_out_so_the_script_still_runs()
    {
        var columns = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [SourceDatasets.Record] = ["RecId", "log_id", "unit"],
        };
        var query = Query("Unit:\n  $coalesce:\n    - $from: unit\n    - $from: gone\n", "osdu.data.Unit", Source(columns: columns));

        Assert.Equal(["dataset.gone"], query.Missing);
        Assert.Contains("left out here: dataset.gone", query.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[gone]", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_column_is_named_as_the_table_names_it()
    {
        var query = Query("Unit: { $from: UNIT }\n", "osdu.data.Unit");

        Assert.Contains("r.[unit]", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void What_reads_no_source_column_has_no_script_and_says_whether_anything_fills_it()
    {
        var fixedValue = Query("Unit: { $value: metres }\n", "osdu.data.Unit");
        Assert.Equal(1, fixedValue.Entries);
        Assert.Empty(fixedValue.Reads);
        Assert.Null(fixedValue.Sql);

        var nothing = Query(string.Empty, "osdu.data.Nested.Inner");
        Assert.Equal(0, nothing.Entries);
        Assert.Null(nothing.Sql);
    }

    [Fact]
    public void Failing_rows_narrow_to_their_keys_rather_than_to_a_row_budget()
    {
        var query = Query(string.Empty, "osdu.data.Depth", keys: [KeyTuple.Of("LOG-1"), KeyTuple.Of("O'Brien")]);

        Assert.Equal(2, query.Keys);
        var sql = query.Sql!;
        // The key's JSON escapes the quote, which OPENJSON reads back: what runs against a real table is proved beside the other reads.
        Assert.Contains("DECLARE @keys nvarchar(max) = N'[[\"LOG-1\"],[\"O\\u0027Brien\"]]';", sql, StringComparison.Ordinal);
        Assert.Contains("INNER JOIN OPENJSON(@keys) WITH ([log_id] nvarchar(50) '$[0]') j ON r.[log_id] = j.[log_id]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_cannot_end_its_string_and_several_values_are_each_declared()
    {
        var sql = Query(string.Empty, "osdu.data.Depth", Source(scopeValues: ["it's'; DROP TABLE x; --", "B"])).Sql!;

        Assert.Contains("DECLARE @scope0_0 varchar(20) = N'it''s''; DROP TABLE x; --';", sql, StringComparison.Ordinal);
        Assert.Contains("DECLARE @scope0_1 varchar(20) = N'B';", sql, StringComparison.Ordinal);
        Assert.Contains("r.[log_source] IN (@scope0_0, @scope0_1)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_cannot_end_its_identifier_or_its_comment()
    {
        var source = Source();
        var odd = source with
        {
            Layout = source.Layout with { Key = [new SourceKeyColumn("log]id; DROP TABLE x; --", "nvarchar(50)")] },
        };
        var query = SourceQuery.For(odd, TestSchema.Mapping(), "flow\nDROP TABLE x;", null, Path("osdu.data.Depth"), 10, []);

        Assert.Contains("r.[log]]id; DROP TABLE x; --]", query.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\nDROP TABLE x;", query.Sql, StringComparison.Ordinal);
    }
}
