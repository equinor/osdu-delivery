using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dimension flow's views as its document declares them (osdu/docs/reference/flow/dimension-views.md): what a view
/// is laid out as before any build, and every rule the loader holds a view, a join, a column, an expression and a data
/// type to, each refusal naming the place in the document it is about.
/// </summary>
public class DimensionViewDocumentTests
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
            label: data.Name
            columns: { key: WellLogID, value: WellLogName }
            elements:
              path: data.Curves
              fields:
                Mnemonic: Mnemonic
                CurveUnitID: { path: CurveUnit, keep: id }
                CurveUnit: CurveUnit
                TopDepth: TopDepth
                BaseDepth: BaseDepth
          - name: WellLog
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            label: data.Name
            columns: { key: WellLogID, value: LogName }
            attributes:
              SamplingDomainTypeID: { path: data.SamplingDomainTypeID, keep: id }
              WellboreID: { path: data.WellboreID, keep: key }
              Created: data.CreationDateTime
          - name: Unit
            kind: "osdu:wks:reference-data--UnitOfMeasure:*"
            path: id
            label: data.Code
            columns: { key: UnitID, value: UnitCode }
          - name: Domain
            kind: "osdu:wks:reference-data--SamplingDomainType:*"
            path: id
            label: data.Name
            columns: { key: DomainID, value: DomainName }
          - name: Wellbore
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.WellboreID
            label: data.FacilityName
          - name: WellboreSource
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.WellboreID
            columns: { key: WellboreID, value: WellboreName }
            attributes:
              Source: { collect: data.Source }

        """;

    private const string Curve = """
        views:
          - name: Curve
            description: Every curve of every well log, with its log, the log's sampling domain and the curve's unit.
            from: LogCurve
            join:
              - { on: WellLogID, to: WellLog }
              - { on: WellLog.SamplingDomainTypeID, to: Domain, as: SamplingDomain }
              - { on: CurveUnitID, to: Unit }
              - { on: WellLog.WellboreID, to: Wellbore }
            columns:
              WellLogID: WellLogID
              Log: WellLog.LogName
              Curve: Mnemonic
              Unit: Unit.UnitCode
              Domain: SamplingDomain.DomainName
              Wellbore: Wellbore.FacilityName
              TopDepth: { expression: TopDepth, dataType: float }
              BaseDepth: { expression: "NULLIF(TRY_CAST(BaseDepth AS float), -999.25)", dataType: float }
              Interval: { expression: "TRY_CAST(BaseDepth AS float) - TRY_CAST(TopDepth AS float)", dataType: "decimal(18,3)" }
              Created:
                expression: WellLog.Created
                dataType: datetime2(0)
                description: When the log was made, in UTC.
        """;

    private static DimensionFlowDefinition Parse(string yaml) => new DeliveryDocumentLoader().ParseDimension(yaml, "flows/dims.yaml");

    private static string Refused(string yaml) => Assert.Throws<FlowValidationException>(() => Parse(yaml)).Message;

    /// <summary>The document with one view of <paramref name="columns"/> over LogCurve and its joins to WellLog and Unit.</summary>
    private static string View(string columns, string joins = "      - { on: WellLogID, to: WellLog }\n      - { on: CurveUnitID, to: Unit }")
        => Head + "views:\n  - name: Test\n    from: LogCurve\n    join:\n" + joins + "\n    columns:\n" + columns;

    [Fact]
    public void A_view_is_laid_out_from_its_document_alone()
    {
        var flow = Parse(Head + Curve);

        var view = Assert.Single(flow.Views);
        Assert.Equal("Curve", view.Name);
        Assert.Equal("v_dim_Curve", view.ViewName);
        Assert.Equal("LogCurve", view.From);
        Assert.Equal(["${env:SQLFLOW_CATALOG_DB}"], new[] { flow.Target!.Connection });
        Assert.Equal(
            [("WellLogID", "WellLog", "WellLog"), ("WellLog.SamplingDomainTypeID", "Domain", "SamplingDomain"), ("CurveUnitID", "Unit", "Unit"), ("WellLog.WellboreID", "Wellbore", "Wellbore")],
            view.Joins.Select(j => (j.On, j.To, j.As)));

        var definition = view.Definition;
        Assert.Equal(["dim_LogCurve", "dim_WellLog", "dim_Domain", "dim_Unit", "dim_Wellbore"], definition.Tables);
        Assert.Equal(
            ["partition", "id", "WellLogID", "Log", "Curve", "Unit", "Domain", "Wellbore", "TopDepth", "BaseDepth", "Interval", "Created"],
            definition.Columns.Select(c => c.Name));
        Assert.Equal(
            ["nvarchar(256)", "bigint", "nvarchar(1024)", "nvarchar(256)", "nvarchar(4000)", "nvarchar(256)", "nvarchar(256)", "nvarchar(256)", "float", "float", "decimal(18, 3)", "datetime2(0)"],
            definition.Columns.Select(c => c.Type));
        Assert.Equal("When the log was made, in UTC.", definition.Columns[^1].Description);

        // Every join compares the partition, the key's hash and the key's text exactly; a joined dimension is joined left.
        var create = definition.CreateSql;
        Assert.StartsWith("CREATE OR ALTER VIEW [osdu].[v_dim_Curve] ([partition], [id], [WellLogID], [Log]", create, StringComparison.Ordinal);
        Assert.Contains("FROM [osdu].[dim_LogCurve] AS [b]", create, StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN [osdu].[dim_Domain] AS [j2]", create, StringComparison.Ordinal);
        Assert.Contains("AND [j2].[key_hash] = CAST(HASHBYTES('SHA2_256', [j1].[SamplingDomainTypeID]) AS binary(32))", create, StringComparison.Ordinal);
        Assert.Contains("AND [j2].[DomainID] COLLATE Latin1_General_100_BIN2 = [j1].[SamplingDomainTypeID] COLLATE Latin1_General_100_BIN2", create, StringComparison.Ordinal);
        Assert.DoesNotContain("*", create, StringComparison.Ordinal);

        // A computed value is computed once and converted by the module: a date as ISO 8601 alone, moved to UTC.
        Assert.Contains("CROSS APPLY (SELECT NULLIF(TRY_CAST([b].[BaseDepth] AS float), (-999.25)) AS [v]) AS [c1]", create, StringComparison.Ordinal);
        Assert.Contains("TRY_CAST(SWITCHOFFSET(TRY_CONVERT(datetimeoffset(7), [j1].[Created], 127), '+00:00') AS datetime2(0))", create, StringComparison.Ordinal);
        Assert.Equal(DimensionViews.HashOf(create), definition.Hash);

        // The document alone settles it: the same document lays the view out the same way.
        Assert.Equal(definition.Hash, Parse(Head + Curve).Views[0].Definition.Hash);
    }

    [Fact]
    public void A_view_that_lists_no_columns_holds_every_column_with_each_join_s_under_its_alias()
    {
        var flow = Parse(View(string.Empty, "      - { on: WellLogID, to: WellLog }\n      - { on: CurveUnitID, to: Unit, as: U }").Replace("    columns:\n", string.Empty, StringComparison.Ordinal));

        Assert.Equal(
            [
                "partition", "id", "WellLogID", "WellLogName", "records", "element", "Mnemonic", "CurveUnitID", "CurveUnit", "TopDepth", "BaseDepth",
                "WellLog_id", "WellLog_LogName", "WellLog_records", "WellLog_SamplingDomainTypeID", "WellLog_WellboreID", "WellLog_Created",
                "U_id", "U_UnitCode", "U_records",
            ],
            flow.Views[0].Definition.Columns.Select(c => c.Name));
        Assert.Empty(flow.Views[0].Columns);
    }

    [Fact]
    public void A_flow_declaring_views_writes_its_tables_and_views_on_its_target_connection_in_lineage()
    {
        var flow = Parse(Head + Curve);

        var objects = DimensionLineage.Describe(flow).Objects;

        Assert.All(objects, o => Assert.Equal((LineageRelation.Writes, "${env:SQLFLOW_CATALOG_DB}", "osdu"), (o.Relation, o.ConnectionReference, o.Schema)));
        Assert.Equal(
            ["dim_LogCurve", "dim_WellLog", "dim_Unit", "dim_Domain", "dim_Wellbore", "dim_WellboreSource", "v_dim_Curve"],
            objects.Select(o => o.Name));
        Assert.Equal(LineageNodeKind.View, objects[^1].Kind);

        // Without a target, a flow declares no table: nothing tells which connection a reader would name.
        var plain = Parse(Head.ReplaceLineEndings("\n").Replace("target:\n  connection: ${env:SQLFLOW_CATALOG_DB}\n", string.Empty, StringComparison.Ordinal));
        Assert.Empty(DimensionLineage.Describe(plain).Objects);
    }

    [Fact]
    public void A_view_makes_at_most_32_joins()
    {
        static string Joins(int count) => string.Join("\n", Enumerable.Range(1, count).Select(i => $"      - {{ on: CurveUnitID, to: Unit, as: U{i} }}"));

        var view = Parse(View("      Curve: Mnemonic\n", Joins(DimensionViewSpec.MaxJoins))).Views[0];
        Assert.Equal(32, view.Joins.Count);
        Assert.Equal("j32", view.Definition.Joins[^1].SqlAlias);

        Assert.Contains("join lists 33 joins; a view makes at most 32", Refused(View("      Curve: Mnemonic\n", Joins(33))), StringComparison.Ordinal);
    }

    [Fact]
    public void The_target_connection_is_a_credential_reference_of_the_flow()
        => Assert.Contains(new KeyValuePair<string, string>("target.connection", "${env:SQLFLOW_CATALOG_DB}"), Parse(Head + Curve).CredentialReferences());

    [Fact]
    public void A_view_keeps_the_rows_its_where_holds_for_in_its_statement_and_in_every_statement_its_check_reads()
    {
        var view = Parse(View("      Curve: Mnemonic\n") + "    where: element IS NOT NULL AND WellLog.LogName <> 'TEST'\n").Views[0];

        Assert.Equal("element IS NOT NULL AND WellLog.LogName <> 'TEST'", view.Where);
        const string Kept = "(([b].[element] IS NOT NULL) AND ([j1].[LogName] <> N'TEST'))";
        Assert.Equal(Kept, view.Definition.WhereSql);
        Assert.EndsWith($"\nWHERE {Kept};", view.Definition.CreateSql.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains($"WHERE [b].[partition] = @partition AND {Kept}", DimensionViews.CheckSql(view.Definition), StringComparison.Ordinal);
        Assert.Contains($"WHERE [b].[partition] = @partition AND {Kept}", DimensionViews.ColumnSql(view.Definition, view.Definition.Columns[^1]), StringComparison.Ordinal);
        Assert.Contains($"WHERE [b].[partition] = @partition AND {Kept} AND", DimensionViews.UnmatchedSql(view.Definition, view.Definition.Joins[0]), StringComparison.Ordinal);

        // A view without one keeps every row, and its statement is the one it was before views took a where.
        var every = Parse(View("      Curve: Mnemonic\n")).Views[0];
        Assert.Null(every.Where);
        Assert.Null(every.Definition.WhereSql);
        Assert.DoesNotContain("\nWHERE ", every.Definition.CreateSql, StringComparison.Ordinal);
        Assert.NotEqual(every.Definition.Hash, view.Definition.Hash);
    }

    [Theory]
    [InlineData("element", "where 'element' is a value, and a row is kept by a condition")]
    [InlineData("''", "where '' is empty")]
    [InlineData("Nope IS NULL", "where 'Nope IS NULL' names Nope, and the table of LogCurve")]
    [InlineData("TopDepth > 3", "where 'TopDepth > 3' ")]
    [InlineData("EXISTS (SELECT 1)", "where 'EXISTS (SELECT 1)' tests")]
    public void A_where_is_a_condition_over_the_view_s_tables_and_nothing_else(string condition, string reason)
        => Assert.Contains(reason, Refused(View("      Curve: Mnemonic\n") + $"    where: {condition}\n"), StringComparison.Ordinal);

    [Theory]
    [InlineData("connection: 'Server=db;Database=osdu;User ID=x;Password=hunter2'", "literal password")]
    [InlineData("connection: ''", "target.connection")]
    public void A_target_connection_holds_references_only(string connection, string reason)
        => Assert.Contains(reason, Refused(Head.Replace("connection: ${env:SQLFLOW_CATALOG_DB}", connection, StringComparison.Ordinal) + Curve), StringComparison.Ordinal);

    [Fact]
    public void Views_need_the_target_connection()
        => Assert.Contains(
            "views needs target.connection",
            Refused(Head.ReplaceLineEndings("\n").Replace("target:\n  connection: ${env:SQLFLOW_CATALOG_DB}\n", string.Empty, StringComparison.Ordinal) + Curve),
            StringComparison.Ordinal);

    [Theory]
    [InlineData("  - name: dimv\n    from: Nope", "views[0] 'dimv': from names 'Nope', which is not a dimension of the flow")]
    [InlineData("  - name: 9Lives\n    from: LogCurve", "views[0].name '9Lives' is not a view name")]
    [InlineData("  - name: Twice\n    from: LogCurve\n  - name: twice\n    from: Unit", "views[1] is named 'twice', as views 'Twice' is")]
    [InlineData("  - name: T\n    from: LogCurve\n    join:\n      - { on: WellLogID, to: Nope }", "join[0].to names 'Nope', which is not a dimension of the flow")]
    [InlineData("  - name: T\n    from: LogCurve\n    join:\n      - { on: Mnemonic, to: Unit }", "join[0].on 'Mnemonic' holds no key: Mnemonic is a field kept as a value")]
    [InlineData("  - name: T\n    from: WellLog\n    join:\n      - { on: Created, to: Unit }", "join[0].on 'Created' holds no key: Created is an attribute kept as a value")]
    [InlineData("  - name: T\n    from: LogCurve\n    join:\n      - { on: records, to: Unit }", "holds no key: records is a column every dimension's table has")]
    [InlineData("  - name: T\n    from: LogCurve\n    join:\n      - { on: WellLogID, to: Unit }", "the id of a work-product-component--WellLog and the dimension is keyed by the id of a reference-data--UnitOfMeasure")]
    [InlineData("  - name: T\n    from: WellLog\n    join:\n      - { on: WellboreID, to: Unit }", "it holds the text data.WellboreID holds and the dimension is keyed by the id of a reference-data--UnitOfMeasure")]
    [InlineData("  - name: T\n    from: WellLog\n    join:\n      - { on: WellboreID, to: WellboreSource }", "whose table holds a row per value it collects of each key, so a join to it would repeat the view's rows")]
    [InlineData("  - name: T\n    from: WellLog\n    join:\n      - { on: WellLogID, to: LogCurve }", "whose table holds a row per element of each key, so a join to it would repeat the view's rows")]
    [InlineData("  - name: T\n    from: LogCurve\n    join:\n      - { on: CurveUnitID, to: Unit }\n      - { on: CurveUnitID, to: Unit }", "join[1] reads Unit as Unit, an alias an earlier join has")]
    [InlineData("  - name: T\n    from: LogCurve\n    join:\n      - { on: Unit.UnitID, to: Unit }", "join[0].on names Unit.UnitID, and Unit is no join of the view before it")]
    [InlineData("  - name: T\n    from: LogCurve\n    join:\n      - { on: a.b.c, to: Unit }", "join[0].on 'a.b.c' is not a column")]
    public void A_view_and_its_joins_are_refused_where_they_could_not_be_written_or_would_repeat_rows(string views, string reason)
        => Assert.Contains(reason, Refused(Head + "views:\n" + views), StringComparison.Ordinal);

    [Theory]
    [InlineData("      id: Mnemonic", "takes the name of a column every view begins with")]
    [InlineData("      Curve: Mnemonic\n      curve: Mnemonic", "columns.curve is named twice")]
    [InlineData("      Curve: Nope", "the expression 'Nope' names Nope, and the table of LogCurve, the view's from dimension, has no column Nope")]
    [InlineData("      Curve: X.Mnemonic", "names X.Mnemonic, and X is no join of the view before it")]
    [InlineData("      Curve: '(SELECT TOP 1 Name FROM sys.tables)'", "uses a subquery")]
    [InlineData("      Curve: '@x'", "uses a variable")]
    [InlineData("      Curve: '@@SERVERNAME'", "a system value")]
    [InlineData("      Curve: CURRENT_TIMESTAMP", "a value of the session or the clock")]
    [InlineData("      Curve: GETDATE()", "calls GETDATE, which is not one a view's expression may call")]
    [InlineData("      Curve: dbo.f(Mnemonic)", "calls F on an object or a schema")]
    [InlineData("      Curve: 'ROW_NUMBER() OVER (ORDER BY id)'", "over a window or a group")]
    [InlineData("      Curve: Mnemonic COLLATE Latin1_General_BIN", "uses COLLATE")]
    [InlineData("      Interval: BaseDepth - TopDepth", "gives the operator - text (BaseDepth), where it takes a number")]
    [InlineData("      Deep: CASE WHEN TopDepth > 1000 THEN 1 ELSE 0 END", "mixes text (TopDepth) and a number")]
    [InlineData("      Label: CONCAT(Mnemonic, id)", "gives CONCAT, which joins texts")]
    [InlineData("      Year: YEAR(WellLog.Created)", "gives YEAR text (WellLog.Created), where it takes a date")]
    [InlineData("      Day: TRY_CAST(id AS date)", "converts a number to date")]
    [InlineData("      Day: CONVERT(datetime2, WellLog.Created, 126)", "converts with a style")]
    [InlineData("      Flag: TopDepth = '0'", "is a condition, and a column holds a value")]
    [InlineData("      Broken: 'TopDepth +'", "does not parse as a T-SQL expression")]
    [InlineData("      Unit: [Unit.UnitCode]", "is a list. An expression that starts with [ is a list in YAML, so quote it")]
    [InlineData("      Depth: { expression: TopDepth, dataType: varchar(10) }", "varchar and char would turn every character")]
    [InlineData("      Depth: { expression: TopDepth, dataType: datetime }", "use datetime2(n)")]
    [InlineData("      Depth: { expression: TopDepth, dataType: nvarchar }", "give nvarchar its length")]
    [InlineData("      Depth: { expression: TopDepth, dataType: \"decimal(40,2)\" }", "holds 1 to 38 digits")]
    [InlineData("      Depth: { expression: TopDepth, dataType: xml }", "is not a type a view converts to")]
    [InlineData("      Depth: { expression: TopDepth, type: float }", "names type; a column is an expression")]
    [InlineData("      Depth: { dataType: float }", "gives no expression")]
    [InlineData("      Depth: ~", "is empty")]
    public void A_column_is_refused_where_its_expression_or_its_type_is_not_one_a_view_writes(string columns, string reason)
        => Assert.Contains(reason, Refused(View(columns)), StringComparison.Ordinal);

    [Fact]
    public void What_could_fail_a_read_is_written_so_it_cannot()
    {
        var flow = Parse(View(
            """
                  Ratio: TRY_CAST(TopDepth AS float) / TRY_CAST(BaseDepth AS float)
                  Root: SQRT(TRY_CAST(TopDepth AS float))
                  Log: LOG(TRY_CAST(TopDepth AS float), 10)
                  Head: LEFT(Mnemonic, 3 - LEN(Mnemonic))
                  Power: POWER(TRY_CAST(TopDepth AS float), 0.5)
            """));

        var sql = flow.Views[0].Definition.Columns.Skip(2).Select(c => c.Sql).ToList();
        Assert.Contains("/ NULLIF(", sql[0], StringComparison.Ordinal);
        Assert.StartsWith("SQRT(CASE WHEN ", sql[1], StringComparison.Ordinal);
        Assert.Contains(">= 0 THEN ", sql[1], StringComparison.Ordinal);
        Assert.StartsWith("LOG(CASE WHEN ", sql[2], StringComparison.Ordinal);
        Assert.EndsWith(", CASE WHEN 10 > 0 AND 10 <> 1 THEN 10 END)", sql[2], StringComparison.Ordinal);
        Assert.StartsWith("LEFT([b].[Mnemonic], CASE WHEN ", sql[3], StringComparison.Ordinal);
        Assert.StartsWith("POWER(CASE WHEN ", sql[4], StringComparison.Ordinal);
        Assert.Contains("<> FLOOR(0.5)) THEN NULL ELSE ", sql[4], StringComparison.Ordinal);

        // Every conversion written, in whichever of the four forms, is the module's: none fails, none reads a style.
        Assert.All(sql, s => Assert.DoesNotContain("CAST(TopDepth", s, StringComparison.Ordinal));
        Assert.DoesNotContain(" CAST([b].[TopDepth] AS float)", string.Join(" ", sql), StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_s_yaml_is_located_line_by_line()
    {
        var yaml = (Head + Curve).ReplaceLineEndings("\n");

        var block = DimensionYamlSource.LocateView(yaml, "curve")!;

        var lines = yaml.Split('\n');
        Assert.Equal("  - name: Curve", lines[block.FirstLine - 1]);
        var span = block.Spans.Single(s => s.Target == "join.1.on");
        Assert.Equal("WellLog.SamplingDomainTypeID", lines[span.Line - 1][(span.Column - 1)..(span.EndColumn - 1)].Split(": ")[1]);
        Assert.Contains(block.Spans, s => s.Target == "columns.Created.dataType");
        Assert.Contains(block.Spans, s => s.Target == "columns.TopDepth");
        Assert.Contains(block.Spans, s => s.Target == "from");
        Assert.Null(DimensionYamlSource.LocateView(yaml, "Nope"));

        // The dimensions' own blocks are found as before.
        Assert.NotNull(DimensionYamlSource.Locate(yaml, "WellLog"));
    }
}
