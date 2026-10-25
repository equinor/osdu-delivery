using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A grouped view and a join to a view, as the document lays them out before any build, at every edge: each form
/// <c>groupBy</c> is written in and each it is refused in, the derived table a grouped view is read from, its key, its
/// conversions and its condition, the statement its check reads (its joins counted over the rows it groups), the views a
/// join to a view may and may not reach, and the hash a view keeps, which follows the views it joins.
/// </summary>
[Trait(DimensionEdgeCaseSuite.Trait, DimensionEdgeCaseSuite.Name)]
public class DimensionViewDocumentEdgeCaseTests
{
    private const string Head = """
        flowType: dimension
        name: wells-dimensions
        partitions: [dev]
        source:
          endpoint: ${env:OSDU_URL}
        target:
          connection: ${env:SQLFLOW_CATALOG_DB}
        dimensions:
          - name: LogCurve
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            columns: { key: WellLogID, value: WellLogCode }
            elements:
              path: data.Curves
              fields:
                Mnemonic: Mnemonic
                CurveUnitID: { path: CurveUnit, keep: id }
                TopDepth: TopDepth
          - name: WellLog
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            columns: { key: WellLogID, value: WellLogName }
            attributes:
              Name: data.Name
              WellboreID: { path: data.WellboreID, keep: key }
              CompanyID: { path: data.CompanyID, keep: id }
              CurveRef: { path: data.ReferenceCurveID, keep: reference }
          - name: Assurance
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            columns: { key: WellLogID, value: WellLogCode }
            elements:
              path: data.TechnicalAssurances
              fields:
                TypeID: { path: TechnicalAssuranceTypeID, keep: id }
          - name: AssuranceType
            kind: "osdu:wks:reference-data--TechnicalAssuranceType:*"
            path: id
            columns: { key: TypeID, value: TypeCode }
            attributes:
              Code: data.Code
              Name: data.Name
          - name: Wellbore
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.WellboreID
        """;

    /// <summary>The technical assurances of each log, as one JSON array a log, the view a later one joins.</summary>
    private const string Assurances = """
          - name: log_assurances
            from: Assurance
            where: element IS NOT NULL
            groupBy: WellLogID
            join:
              - { on: TypeID, to: AssuranceType, as: Type }
            columns:
              WellLogID: WellLogID
              Assurances:
                expression: "CONCAT('[', STRING_AGG(CONCAT('{\"code\":\"', STRING_ESCAPE(Type.Code, 'json'), '\"}'), ',') WITHIN GROUP (ORDER BY element), ']')"
              Count: COUNT(*)
              FirstElement: { expression: MIN(element), dataType: int }
        """ + "\n";

    private static DimensionFlowDefinition Parse(string views) => new DeliveryDocumentLoader().ParseDimension(Head + "\nviews:\n" + views, "flows/dims.yaml");

    private static string Refused(string views) => Assert.Throws<FlowValidationException>(() => Parse(views)).Message;

    private static DimensionViewSpec View(string views, string name) => Parse(views).Views.Single(v => v.Name == name);

    [Theory]
    [InlineData("groupBy: WellLogID")]
    [InlineData("groupBy: [WellLogID]")]
    [InlineData("groupBy: [ ' WellLogID ' ]")]
    public void A_view_groups_by_one_column_written_alone_or_as_a_list(string groupBy)
        => Assert.Equal(["WellLogID"], View($"""
              - name: per_log
                from: LogCurve
                {groupBy}
                columns:
                  WellLogID: WellLogID
                  Curves: COUNT(*)
            """, "per_log").GroupBy);

    [Theory]
    [InlineData("groupBy: []", "groupBy lists no column")]
    [InlineData("groupBy: { column: WellLogID }", "groupBy is a column (groupBy: WellLogID) or a list of columns")]
    [InlineData("groupBy: [WellLogID, [Mnemonic]]", "groupBy[1] is not a column")]
    [InlineData("groupBy: [WellLogID, WellLogID]", "groupBy[1] names WellLogID twice")]
    [InlineData("groupBy: [WellLogID, wellLogID]", "groupBy[1] names WellLogID twice")]
    [InlineData("groupBy: Nope", "groupBy[0] names Nope, and the table of LogCurve, the view's from dimension, has no column Nope")]
    [InlineData("groupBy: Unit.Name", "groupBy[0] names Unit.Name, and Unit is no join of the view before it")]
    [InlineData("groupBy: 'a.b.c'", "groupBy[0] 'a.b.c' is not a column")]
    [InlineData("groupBy: [c0, c1, c2, c3, c4, c5, c6, c7, c8, c9, c10, c11, c12, c13, c14, c15, c16]", "groupBy lists 17 columns; a view groups by at most 16")]
    public void A_groupBy_that_is_not_one_is_refused(string groupBy, string reason)
        => Assert.Contains(reason, Refused($"""
              - name: per_log
                from: LogCurve
                {groupBy}
                columns:
                  Curves: COUNT(*)
            """), StringComparison.Ordinal);

    [Fact]
    public void A_grouped_view_orders_the_rows_of_a_group_one_way_whichever_column_orders_them()
    {
        Assert.Contains(
            "columns.Last: the expression 'STRING_AGG(Mnemonic, ';') WITHIN GROUP (ORDER BY element DESC)' orders the rows of a group otherwise than columns.First does",
            Refused("""
              - name: per_log
                from: LogCurve
                groupBy: WellLogID
                columns:
                  First: STRING_AGG(Mnemonic, ',') WITHIN GROUP (ORDER BY element)
                  Unordered: STRING_AGG(Mnemonic, ',')
                  Last: STRING_AGG(Mnemonic, ';') WITHIN GROUP (ORDER BY element DESC)
            """),
            StringComparison.Ordinal);

        // Alike, ascending written or not, and with aggregates of no order beside them, the view is read in one order.
        Assert.NotNull(View("""
              - name: per_log
                from: LogCurve
                groupBy: WellLogID
                columns:
                  First: STRING_AGG(Mnemonic, ',') WITHIN GROUP (ORDER BY element)
                  Unordered: STRING_AGG(Mnemonic, ',')
                  Last: STRING_AGG(Mnemonic, ';') WITHIN GROUP (ORDER BY element ASC)
            """, "per_log"));
    }

    [Fact]
    public void A_grouped_view_lists_its_columns()
        => Assert.Contains("a grouped view holds the columns it lists", Refused("""
              - name: per_log
                from: LogCurve
                groupBy: WellLogID
            """), StringComparison.Ordinal);

    [Fact]
    public void A_grouped_view_is_read_from_a_derived_table_of_its_groups_keyed_by_the_least_row_number_of_each()
    {
        var definition = View(Assurances, "log_assurances").Definition;

        Assert.True(definition.Grouped);
        Assert.Null(definition.WhereSql);
        Assert.Equal("([b].[element] IS NOT NULL)", definition.RowsWhereSql);
        Assert.StartsWith("FROM (\nSELECT\n    [b].[partition] AS [partition],\n    MIN([b].[id]) AS [id],", definition.FromSql, StringComparison.Ordinal);
        Assert.Contains("\nWHERE ([b].[element] IS NOT NULL)\nGROUP BY [b].[partition], [b].[WellLogID]\n) AS [b]", definition.FromSql, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN [osdu].[dim_AssuranceType] AS [j1]", definition.FromSql, StringComparison.Ordinal);

        // Each column computed in the groups once, under its name, and read (or converted) over them by that name.
        Assert.Equal(
            [("partition", "[b].[partition]"), ("id", "[b].[id]"), ("WellLogID", "[b].[WellLogID]"), ("Assurances", "[b].[Assurances]"), ("Count", "[b].[Count]")],
            definition.Columns.Take(5).Select(c => (c.Name, c.Sql)));
        var first = definition.Columns.Single(c => c.Name == "FirstElement");
        Assert.Equal(("int", "[b].[FirstElement]", "MIN([b].[element])"), (first.Type, first.InputSql, first.GroupSql));
        Assert.Contains("[b].[FirstElement]", first.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE OR ALTER VIEW [osdu].[v_dim_log_assurances] ([partition], [id], [WellLogID], [Assurances], [Count], [FirstElement])", definition.CreateSql, StringComparison.Ordinal);
        Assert.Equal(DimensionViews.HashOf(definition.CreateSql), definition.Hash);
    }

    [Fact]
    public void A_grouped_view_s_check_counts_its_groups_and_its_columns_over_them_and_its_joins_over_the_rows_it_groups()
    {
        var definition = View(Assurances, "log_assurances").Definition;
        var check = DimensionViews.CheckSql(definition);

        // One row: the groups, then each join's found and unfound, then each conversion's unread, then each column's values.
        Assert.StartsWith("SELECT\n    [g].[n0],\n    [r].[j0],\n    [r].[j1],\n    [g].[c0],\n    [g].[c1],", check, StringComparison.Ordinal);
        Assert.Contains("CROSS JOIN (", check, StringComparison.Ordinal);
        Assert.Contains("COUNT_BIG([j1].[id]) AS [j0]", check, StringComparison.Ordinal);
        Assert.Contains("WHERE [b].[partition] = @partition AND ([b].[element] IS NOT NULL)\n) AS [r]", check, StringComparison.Ordinal);
        Assert.EndsWith("OPTION (MAX_GRANT_PERCENT = 10);", check, StringComparison.Ordinal);

        // A join's values that found nothing are read from the rows it is made over, before they are grouped.
        var unmatched = DimensionViews.UnmatchedSql(definition, definition.Joins[0]);
        Assert.DoesNotContain("GROUP BY [b].[partition]", unmatched, StringComparison.Ordinal);
        Assert.Contains("AND ([b].[element] IS NOT NULL) AND [b].[TypeID] IS NOT NULL AND [j1].[id] IS NULL", unmatched, StringComparison.Ordinal);

        // A grouped view that joins nothing reads its groups alone.
        var alone = View("""
              - name: per_log
                from: LogCurve
                groupBy: WellLogID
                columns:
                  WellLogID: WellLogID
                  Curves: COUNT(*)
            """, "per_log").Definition;
        Assert.DoesNotContain("CROSS JOIN", DimensionViews.CheckSql(alone), StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_joins_a_grouped_view_on_the_key_it_groups_by_and_meets_one_row_of_it()
    {
        var flow = Parse(Assurances + """
              - name: logs
                from: WellLog
                join:
                  - { on: WellLogID, view: log_assurances, as: TA }
                columns:
                  Name: Name
                  Assurances: TA.Assurances
                  More: { expression: "TA.Count + 1", dataType: bigint }
            """);
        var logs = flow.Views.Single(v => v.Name == "logs");
        var definition = logs.Definition;

        Assert.Equal([new DimensionViewJoin("WellLogID", "log_assurances", "TA", View: true)], logs.Joins);
        Assert.Equal(["v_dim_log_assurances"], definition.JoinedViews);
        Assert.Equal(["dim_WellLog"], definition.Tables);
        Assert.True(Assert.Single(definition.Joins).View);
        Assert.Equal("v_dim_log_assurances", definition.Joins[0].Table);

        // A view holds no key's hash: the join compares the partition and the column it groups by, exactly.
        Assert.Contains("LEFT JOIN [osdu].[v_dim_log_assurances] AS [j1]\n    ON [j1].[partition] = [b].[partition]\n   AND [j1].[WellLogID] COLLATE Latin1_General_100_BIN2 = [b].[WellLogID] COLLATE Latin1_General_100_BIN2", definition.FromSql, StringComparison.Ordinal);
        Assert.DoesNotContain("[j1].[key_hash]", definition.FromSql, StringComparison.Ordinal);

        // The view is known by its statement and by the views it joins, so a change of the view joined writes it again.
        Assert.NotEqual(DimensionViews.HashOf(definition.CreateSql), definition.Hash);
        var changedBase = Parse(Assurances.Replace("Count: COUNT(*)", "Count: COUNT(DISTINCT TypeID)", StringComparison.Ordinal) + """
              - name: logs
                from: WellLog
                join:
                  - { on: WellLogID, view: log_assurances, as: TA }
                columns:
                  Name: Name
                  Assurances: TA.Assurances
                  More: { expression: "TA.Count + 1", dataType: bigint }
            """).Views.Single(v => v.Name == "logs").Definition;
        Assert.Equal(definition.CreateSql, changedBase.CreateSql);
        Assert.NotEqual(definition.Hash, changedBase.Hash);
    }

    [Theory]
    [InlineData("view: later", "names 'later', which is no view declared before this one in the flow")]
    [InlineData("view: nope", "names 'nope', which is no view declared before this one in the flow (log_assurances, ungrouped, two_keys, unlisted, converted)")]
    [InlineData("view: ungrouped", "joins view ungrouped, which a join cannot meet one row of: it groups no rows")]
    [InlineData("view: two_keys", "it groups its rows by 2 columns, and a join compares one key")]
    [InlineData("view: unlisted", "it groups by WellLogID and does not list it as a column of its own, bare and not converted")]
    [InlineData("view: converted", "it groups by WellLogID and does not list it as a column of its own, bare and not converted")]
    public void A_view_joins_only_a_view_declared_before_it_grouped_by_one_key_it_lists_bare(string join, string reason)
        => Assert.Contains(reason, Refused(Assurances + $$"""
              - name: ungrouped
                from: LogCurve
                columns:
                  WellLogID: WellLogID
              - name: two_keys
                from: LogCurve
                groupBy: [WellLogID, Mnemonic]
                columns:
                  WellLogID: WellLogID
                  Mnemonic: Mnemonic
              - name: unlisted
                from: LogCurve
                groupBy: WellLogID
                columns:
                  Curves: COUNT(*)
              - name: converted
                from: LogCurve
                groupBy: WellLogID
                columns:
                  WellLogID: { expression: WellLogID, dataType: nvarchar(1024) }
              - name: reader
                from: WellLog
                join:
                  - { on: WellLogID, {{join}} }
                columns:
                  Name: Name
              - name: later
                from: LogCurve
                groupBy: WellLogID
                columns:
                  WellLogID: WellLogID
            """), StringComparison.Ordinal);

    [Theory]
    [InlineData("{ on: WellLogID, to: WellLog, view: log_assurances }", "names both to and view")]
    [InlineData("{ on: WellLogID }", "names neither to nor view")]
    [InlineData("{ on: WellboreID, view: log_assurances }", "joins 'WellboreID' to log_assurances, and it holds the text data.WellboreID holds and the dimension is keyed by the id of a work-product-component--WellLog")]
    [InlineData("{ on: Name, view: log_assurances }", "holds no key")]
    public void A_join_to_a_view_that_breaks_a_rule_is_refused(string join, string reason)
        => Assert.Contains(reason, Refused(Assurances + $$"""
              - name: reader
                from: WellLog
                join:
                  - {{join}}
                columns:
                  Name: Name
            """), StringComparison.Ordinal);

    [Fact]
    public void A_view_that_joins_a_view_lists_its_columns_and_names_no_alias_twice()
    {
        Assert.Contains("a view that joins a view holds the columns it lists", Refused(Assurances + """
              - name: reader
                from: WellLog
                join:
                  - { on: WellLogID, view: log_assurances }
            """), StringComparison.Ordinal);
        Assert.Contains("reads log_assurances as WellLog, an alias an earlier join has", Refused(Assurances + """
              - name: reader
                from: LogCurve
                join:
                  - { on: WellLogID, to: WellLog }
                  - { on: WellLogID, view: log_assurances, as: WellLog }
                columns:
                  Name: WellLog.Name
            """), StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_kept_with_its_version_joins_a_dimension_keyed_by_id()
    {
        var view = View("""
              - name: curve_refs
                from: WellLog
                join:
                  - { on: CurveRef, to: AssuranceType, as: T }
                columns:
                  Code: T.Code
            """, "curve_refs");
        Assert.Contains("[j1].[TypeID] COLLATE Latin1_General_100_BIN2 = [b].[CurveRef] COLLATE Latin1_General_100_BIN2", view.Definition.FromSql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_of_a_row_per_row_is_laid_out_as_it_was_before_views_grouped()
    {
        var definition = View("""
              - name: curves
                from: LogCurve
                join:
                  - { on: WellLogID, to: WellLog, as: Log }
                columns:
                  Log: Log.Name
                  Top: { expression: TopDepth, dataType: float }
            """, "curves").Definition;
        Assert.False(definition.Grouped);
        Assert.Empty(definition.JoinedViews);
        Assert.Equal(DimensionViews.HashOf(definition.CreateSql), definition.Hash);
        Assert.StartsWith("FROM [osdu].[dim_LogCurve] AS [b]\nLEFT JOIN [osdu].[dim_WellLog] AS [j1]", definition.FromSql, StringComparison.Ordinal);
        Assert.Contains("[j1].[key_hash]", definition.FromSql, StringComparison.Ordinal);
        Assert.All(definition.Columns, c => Assert.Null(c.GroupSql));
    }

    [Fact]
    public void A_view_may_join_a_view_that_joins_a_view()
    {
        var flow = Parse(Assurances + """
              - name: per_log
                from: WellLog
                groupBy: WellLogID
                join:
                  - { on: WellLogID, view: log_assurances, as: TA }
                columns:
                  WellLogID: WellLogID
                  Assurances: MAX(TA.Assurances)
              - name: top
                from: WellLog
                join:
                  - { on: WellLogID, view: per_log, as: P }
                columns:
                  Assurances: P.Assurances
            """);
        Assert.Equal(["v_dim_per_log"], flow.Views.Single(v => v.Name == "top").Definition.JoinedViews);
    }
}
