using SqlFlow.Delivery.Engine.Dimensions;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A grouped view's expressions at every edge: each aggregate over each kind of value, as SQL Server is asked for it
/// (a count that cannot overflow, text ordered by code point, text aggregated past 8,000 bytes), every form an aggregate is
/// refused in (where one row is read, nested, over a window, ordered where only STRING_AGG is, distinct where SQL Server
/// takes none, over NULL alone, over a type it does not take), a column read outside an aggregate, and JSON's escapes.
/// </summary>
[Trait(DimensionEdgeCaseSuite.Trait, DimensionEdgeCaseSuite.Name)]
public class DimensionViewExpressionEdgeCaseTests
{
    private static ViewScope Scope()
    {
        var scope = new ViewScope();
        scope.Add(ViewScope.FromAlias, "LogCurve",
        [
            new ViewColumnSource(ViewScope.FromAlias, "b", "WellLogID", ViewValue.Text, "nvarchar(1024)"),
            new ViewColumnSource(ViewScope.FromAlias, "b", "Mnemonic", ViewValue.Text, "nvarchar(4000)"),
            new ViewColumnSource(ViewScope.FromAlias, "b", "TopDepth", ViewValue.Text, "nvarchar(4000)"),
            new ViewColumnSource(ViewScope.FromAlias, "b", "element", ViewValue.Number, "int"),
        ]);
        scope.Add("Log", "WellLog", [new ViewColumnSource("Log", "j1", "Source", ViewValue.Text, "nvarchar(256)")]);
        return scope;
    }

    private static IReadOnlyList<ViewColumnSource> By(ViewScope scope, params string[] columns)
        => columns.Select(c => c.Contains('.', StringComparison.Ordinal) ? scope.Resolve(c.Split('.')[0], c.Split('.')[1]) : scope.Resolve(ViewScope.FromAlias, c)).ToList();

    private static CompiledViewExpression Grouped(string expression, params string[] groupBy)
    {
        var scope = Scope();
        return DimensionViewExpressions.Compile(expression, scope, By(scope, groupBy.Length == 0 ? ["WellLogID"] : groupBy));
    }

    private static string Refused(string expression, bool grouped = true)
    {
        var scope = Scope();
        return Assert.Throws<ViewExpressionException>(() => DimensionViewExpressions.Compile(expression, scope, grouped ? By(scope, "WellLogID") : null)).Message;
    }

    [Theory]
    [InlineData("COUNT(*)", "COUNT_BIG(*)")]
    [InlineData("count(*)", "COUNT_BIG(*)")]
    [InlineData("COUNT_BIG(*)", "COUNT_BIG(*)")]
    [InlineData("COUNT(Mnemonic)", "COUNT_BIG([b].[Mnemonic])")]
    [InlineData("COUNT(DISTINCT Mnemonic)", "COUNT_BIG(DISTINCT [b].[Mnemonic])")]
    [InlineData("COUNT(element)", "COUNT_BIG([b].[element])")]
    [InlineData("MAX(element)", "MAX([b].[element])")]
    [InlineData("MIN(DISTINCT element)", "MIN(DISTINCT [b].[element])")]
    [InlineData("SUM(element)", "SUM([b].[element])")]
    [InlineData("AVG(DISTINCT element)", "AVG(DISTINCT [b].[element])")]
    public void An_aggregate_is_asked_of_SQL_Server_so_a_count_never_overflows(string expression, string sql)
    {
        var compiled = Grouped(expression);
        Assert.Equal((sql, ViewValue.Number), (compiled.Sql, compiled.Value));
    }

    [Fact]
    public void The_greatest_and_least_text_is_read_by_code_point_and_back_in_the_database_s_collation()
    {
        var max = Grouped("MAX(Mnemonic)");
        Assert.Equal(ViewValue.Text, max.Value);
        Assert.Equal($"CAST(MAX(CAST(([b].[Mnemonic]) COLLATE {DimensionViewExpressions.CodePointCollation} AS varchar(max))) AS nvarchar(max)) COLLATE DATABASE_DEFAULT", max.Sql);
        Assert.StartsWith("CAST(MIN(DISTINCT CAST(", Grouped("MIN(DISTINCT Mnemonic)").Sql, StringComparison.Ordinal);

        // A text computed is ordered the same way: the expression is wrapped whole before it is compared.
        Assert.Contains("CAST((UPPER([b].[Mnemonic])) COLLATE", Grouped("MAX(UPPER(Mnemonic))").Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_aggregated_past_8000_bytes_in_the_order_written_text_ordered_by_code_point()
    {
        Assert.Equal("STRING_AGG(CAST([b].[Mnemonic] AS nvarchar(max)), N', ')", Grouped("STRING_AGG(Mnemonic, ', ')").Sql);
        Assert.Equal("STRING_AGG(CAST([b].[Mnemonic] AS nvarchar(max)), N'it''s')", Grouped("STRING_AGG(Mnemonic, 'it''s')").Sql);
        Assert.Equal(
            $"STRING_AGG(CAST([b].[Mnemonic] AS nvarchar(max)), N';') WITHIN GROUP (ORDER BY [b].[element] DESC, CAST(([b].[Mnemonic]) COLLATE {DimensionViewExpressions.CodePointCollation} AS varchar(max)) ASC, [b].[element])",
            Grouped("STRING_AGG(Mnemonic, ';') WITHIN GROUP (ORDER BY element DESC, Mnemonic ASC, element)").Sql);
        Assert.Equal(ViewValue.Text, Grouped("STRING_AGG(Mnemonic, ',')").Value);
    }

    [Fact]
    public void A_JSON_array_of_objects_is_written_with_its_texts_escaped()
    {
        var compiled = Grouped("CONCAT('[', STRING_AGG(CONCAT('{\"code\":\"', STRING_ESCAPE(Mnemonic, 'json'), '\"}'), ',') WITHIN GROUP (ORDER BY element), ']')");
        Assert.Equal(ViewValue.Text, compiled.Value);
        Assert.Contains("STRING_ESCAPE([b].[Mnemonic], 'json')", compiled.Sql, StringComparison.Ordinal);
        Assert.Contains("STRING_ESCAPE([b].[Mnemonic], 'json')", DimensionViewExpressions.Compile("STRING_ESCAPE(Mnemonic, 'JSON')", Scope()).Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mnemonic", "names Mnemonic outside an aggregate, and the view groups its rows by WellLogID")]
    [InlineData("CONCAT(WellLogID, Mnemonic)", "names Mnemonic outside an aggregate")]
    [InlineData("Log.Source", "names Log.Source outside an aggregate")]
    [InlineData("MAX(MIN(Mnemonic))", "calls MIN inside another aggregate")]
    [InlineData("MAX(Mnemonic) OVER (PARTITION BY WellLogID)", "over a window")]
    [InlineData("MAX(NULL)", "gives MAX NULL alone")]
    [InlineData("COUNT(NULL)", "gives COUNT NULL alone")]
    [InlineData("SUM(Mnemonic)", "gives SUM text (Mnemonic)")]
    [InlineData("AVG(TopDepth)", "gives AVG text")]
    [InlineData("STRING_AGG(element, ',')", "gives STRING_AGG a number")]
    [InlineData("STRING_AGG(Mnemonic, Mnemonic)", "a separator that is not a text in quotes")]
    [InlineData("STRING_AGG(Mnemonic)", "calls STRING_AGG with 1 argument(s); it takes 2")]
    [InlineData("STRING_AGG(Mnemonic, ',') WITHIN GROUP (ORDER BY NULL)", "orders STRING_AGG by NULL alone")]
    [InlineData("MAX(TRY_CAST(Mnemonic AS uniqueidentifier))", "gives MAX a GUID")]
    [InlineData("COUNT(Mnemonic, element)", "calls COUNT with 2 argument(s); it takes 1")]
    [InlineData("STRING_ESCAPE(WellLogID, 'xml')", "a type other than 'json'")]
    [InlineData("STRING_ESCAPE(WellLogID, WellLogID)", "a type other than 'json'")]
    [InlineData("STRING_ESCAPE(WellLogID)", "calls STRING_ESCAPE with 1 argument(s); it takes 2")]
    [InlineData("STRING_ESCAPE(COUNT(*), 'json')", "gives STRING_ESCAPE a number")]
    public void A_grouped_view_s_expression_that_breaks_a_rule_is_refused_saying_why(string expression, string reason)
        => Assert.Contains(reason, Refused(expression), StringComparison.Ordinal);

    [Theory]
    [InlineData("COUNT(DISTINCT *)")]
    [InlineData("STRING_AGG(DISTINCT Mnemonic, ',')")]
    [InlineData("MAX(Mnemonic) WITHIN GROUP (ORDER BY element)")]
    public void A_form_SQL_Server_does_not_take_is_refused_whether_or_not_it_parses(string expression)
    {
        var message = Refused(expression);
        Assert.True(
            message.Contains("does not parse", StringComparison.Ordinal) || message.Contains("which SQL Server does not take", StringComparison.Ordinal)
            || message.Contains("which only STRING_AGG takes", StringComparison.Ordinal),
            message);
    }

    [Theory]
    [InlineData("CONCAT(STRING_AGG(Mnemonic, ',') WITHIN GROUP (ORDER BY element), STRING_AGG(Mnemonic, ';') WITHIN GROUP (ORDER BY element DESC))", "orders the rows of a group 2 ways")]
    [InlineData("CONCAT(STRING_AGG(Mnemonic, ',') WITHIN GROUP (ORDER BY element), STRING_AGG(Mnemonic, ';') WITHIN GROUP (ORDER BY Mnemonic))", "orders the rows of a group 2 ways")]
    public void An_expression_ordering_a_group_two_ways_is_refused_as_SQL_Server_refuses_to_read_it(string expression, string reason)
        => Assert.Contains(reason, Refused(expression), StringComparison.Ordinal);

    [Theory]
    [InlineData("CONCAT(STRING_AGG(Mnemonic, ',') WITHIN GROUP (ORDER BY element), STRING_AGG(Mnemonic, ';') WITHIN GROUP (ORDER BY element ASC))")]
    [InlineData("CONCAT(STRING_AGG(Mnemonic, ',') WITHIN GROUP (ORDER BY element), STRING_AGG(Mnemonic, ';'))")]
    public void An_expression_ordering_a_group_alike_or_once_reads_one_order(string expression)
        => Assert.Equal(["[b].[element]"], Grouped(expression).Orders);

    [Theory]
    [InlineData("MAX(Mnemonic)")]
    [InlineData("COUNT(*)")]
    [InlineData("STRING_AGG(Mnemonic, ',')")]
    public void An_aggregate_is_refused_where_one_row_is_read(string expression)
        => Assert.Contains("an aggregate of the rows of a group, where one row is read", Refused(expression, grouped: false), StringComparison.Ordinal);

    [Fact]
    public void A_condition_reads_one_row_and_aggregates_nothing()
        => Assert.Contains(
            "an aggregate of the rows of a group, where one row is read",
            Assert.Throws<ViewExpressionException>(() => DimensionViewExpressions.CompileCondition("COUNT(*) > 1", Scope())).Message,
            StringComparison.Ordinal);

    [Theory]
    [InlineData("WellLogID", ViewValue.Text)]
    [InlineData("UPPER(WellLogID)", ViewValue.Text)]
    [InlineData("IIF(COUNT(*) > 1, 'many', 'one')", ViewValue.Text)]
    [InlineData("COUNT(*) + 1", ViewValue.Number)]
    [InlineData("CASE WHEN MAX(element) IS NULL THEN 0 ELSE MAX(element) END", ViewValue.Number)]
    [InlineData("CONCAT(WellLogID, ':', CAST(COUNT(*) AS nvarchar(20)))", ViewValue.Text)]
    [InlineData("NULL", ViewValue.Null)]
    public void A_grouped_view_reads_its_group_columns_and_its_aggregates_anywhere_in_an_expression(string expression, ViewValue value)
        => Assert.Equal(value, Grouped(expression).Value);

    [Fact]
    public void A_view_grouped_by_a_joined_column_reads_it_bare()
    {
        Assert.Equal("[j1].[Source]", Grouped("Log.Source", "WellLogID", "Log.Source").Sql);
        Assert.Contains("names Mnemonic outside an aggregate, and the view groups its rows by WellLogID, Log.Source", Assert.Throws<ViewExpressionException>(
            () => DimensionViewExpressions.Compile("Mnemonic", Scope(), By(Scope(), "WellLogID", "Log.Source"))).Message, StringComparison.Ordinal);
    }
}
