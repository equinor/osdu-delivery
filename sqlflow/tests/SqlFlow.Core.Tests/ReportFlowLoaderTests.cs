using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Query;
using SqlFlow.Core.Reports;
using SqlFlow.Core.Runs;
using SqlFlow.Execution;
using SqlFlow.Lineage.Collection;
using SqlFlow.Orchestration;
using SqlFlow.SqlServer.Query;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A report (flowType: rpt) is read strictly, is never run as a flow, and runs its queries with nothing from a client but
/// values. These pin what the document may say, that every problem names its line and all are reported together, how a
/// run's values are bound, what a statement may be, and that each boundary that could start a run of a flow refuses one.
/// </summary>
public sealed class ReportFlowLoaderTests
{
    private const string Full = """
        flowType: rpt
        name: staging-overview-rpt
        title: Staging overview
        description: What the staging area holds, by source, and what arrived lately.
        batch: staging
        connection: ${env:SQLFLOW_DW}
        database: Staging
        pool: onprem
        timeoutSeconds: 90
        maxRows: 2000

        parameters:
          source:
            label: Source
            description: Only rows of this source.
            choicesFrom: SOURCES
          days:
            label: Days back
            type: integer
            default: 30
          state:
            type: text
            default: open
            choices:
              - open
              - { value: closed, label: Closed orders }

        queries:
          sources:
            sql: SELECT DISTINCT Source FROM stg.Orders WHERE State = @state ORDER BY Source
            show: none

          totals:
            title: The staging area
            section: Overview
            sql: |
              SELECT COUNT(*) AS Staged, MAX(LoadedUtc) AS LastLoaded
              FROM stg.Orders
              WHERE (@source IS NULL OR Source = @source)
            show: figure
            columns:
              Staged: { label: Rows staged, format: integer, unit: rows }
              LastLoaded: datetime

          by_source:
            title: Rows by source
            section: Overview
            timeoutSeconds: 20
            maxRows: 50
            database: Other
            sql: |
              SELECT Source, COUNT(*) AS Staged FROM stg.Orders GROUP BY Source ORDER BY Staged DESC
            show:
              - { type: bar, by: Source, values: Staged, top: 10, selects: SOURCE, width: half }
              - table

          per_day:
            sql: |
              SELECT CAST(LoadedUtc AS date) AS Day, State, COUNT(*) AS Staged
              FROM stg.Orders
              WHERE LoadedUtc >= DATEADD(day, -@days, SYSUTCDATETIME()) AND (@Source IS NULL OR Source = @Source)
              GROUP BY CAST(LoadedUtc AS date), State
            show: { type: column, by: Day, values: Staged, series: State, stacked: true }
        """;

    [Fact]
    public void AFullReport_ReadsAsWritten()
    {
        var report = YamlReportFlowLoader.Parse(Full, "reports/staging.yaml");

        Assert.Equal("staging-overview-rpt", report.Name);
        Assert.Equal("Staging overview", report.Title);
        Assert.Equal("staging", report.Batch);
        Assert.Equal("${env:SQLFLOW_DW}", report.Connection);
        Assert.Equal("Staging", report.Database);
        Assert.Equal("onprem", report.Pool);
        Assert.Equal(90, report.TimeoutSeconds);
        Assert.Equal(2000, report.MaxRows);

        // Parameters keep the document's order; a choicesFrom is kept as the query spells its own name.
        Assert.Equal(["source", "days", "state"], report.Parameters.Select(p => p.Name));
        var source = report.Parameters[0];
        Assert.Equal(("Source", QueryParameterTypes.Text, null, false), (source.Label, source.Type, source.Default, source.Required));
        Assert.Equal("sources", source.ChoicesFrom);
        var days = report.Parameters[1];
        Assert.Equal((QueryParameterTypes.WholeNumber, "30"), (days.Type, days.Default));
        Assert.Equal(
            [new ReportChoice("open", null), new ReportChoice("closed", "Closed orders")],
            report.Parameters[2].Choices);

        // Queries keep the document's order, each with the parameters its statement refers to, as the report spells them.
        Assert.Equal(["sources", "totals", "by_source", "per_day"], report.Queries.Select(q => q.Name));
        Assert.Equal(["state"], report.Queries[0].Parameters);
        Assert.Empty(report.Queries[0].Views);
        Assert.Equal(["source"], report.Queries[1].Parameters);
        Assert.Equal(["days", "source"], report.Queries[3].Parameters);

        var totals = report.Queries[1];
        Assert.Equal(("The staging area", "Overview"), (totals.Title, totals.Section));
        Assert.Equal(ReportViewTypes.Figure, Assert.Single(totals.Views).Type);
        Assert.Equal(
            [
                new ReportColumn { Name = "Staged", Label = "Rows staged", Format = ReportFormats.WholeNumber, Unit = "rows" },
                new ReportColumn { Name = "LastLoaded", Format = ReportFormats.DateTime },
            ],
            totals.Columns);

        var bySource = report.Queries[2];
        Assert.Equal(2, bySource.Views.Count);
        var bar = bySource.Views[0];
        Assert.Equal(
            (ReportViewTypes.Bar, "Source", "source", 10, ReportWidths.Half),
            (bar.Type, bar.By, bar.Selects, bar.Top, bar.Width));
        Assert.Equal(["Staged"], bar.Values);
        Assert.Equal(ReportViewTypes.Table, bySource.Views[1].Type);

        var column = Assert.Single(report.Queries[3].Views);
        Assert.Equal((ReportViewTypes.Column, "Day", "State", true), (column.Type, column.By, column.Series, column.Stacked));

        // What a query says for itself wins over the report; what it leaves out is the report's.
        Assert.Equal((20, 50, "Other"), (report.TimeoutOf(bySource), report.MaxRowsOf(bySource), report.DatabaseOf(bySource)));
        Assert.Equal((90, 2000, "Staging"), (report.TimeoutOf(totals), report.MaxRowsOf(totals), report.DatabaseOf(totals)));
        Assert.Equal("${env:SQLFLOW_DW}", report.ConnectionOf(bySource));
    }

    [Fact]
    public void TheSmallestReport_IsOneQuery_ShownAsItsAnswerSuggests()
    {
        var report = YamlReportFlowLoader.Parse("""
            flowType: rpt
            name: orders
            connection: "@dwh"
            queries:
              orders:
                sql: SELECT COUNT(*) AS Orders FROM stg.Orders
            """);

        Assert.Null(report.Title);
        Assert.Equal("@dwh", report.Connection);
        Assert.Equal(ReportFlow.DefaultTimeoutSeconds, report.TimeoutSeconds);
        Assert.Equal(ReportFlow.DefaultMaxRows, report.MaxRows);
        Assert.Empty(report.Parameters);
        var query = Assert.Single(report.Queries);
        Assert.Equal(ReportViewTypes.Auto, Assert.Single(query.Views).Type);
        Assert.Empty(query.Parameters);
    }

    [Fact]
    public void AParameterListedWithNothing_IsAnOptionalTextValue()
    {
        var report = YamlReportFlowLoader.Parse("""
            flowType: rpt
            name: orders
            connection: ${env:SQLFLOW_DW}
            parameters:
              source:
            queries:
              orders:
                sql: SELECT COUNT(*) AS Orders FROM stg.Orders WHERE (@source IS NULL OR Source = @source)
            """);

        var parameter = Assert.Single(report.Parameters);
        Assert.Equal((QueryParameterTypes.Text, null, false), (parameter.Type, parameter.Default, parameter.Required));
    }

    private static string With(string rootLines = "", string parameters = "", string query = "sql: SELECT 1 AS One", string more = "")
        => "flowType: rpt\nname: probe\nconnection: ${env:SQLFLOW_DW}\n" + rootLines
           + (parameters.Length == 0 ? string.Empty : "parameters:\n" + Indent(parameters, 2))
           + "queries:\n  q:\n" + Indent(query, 4) + more;

    private static string Indent(string text, int by)
        => string.Join('\n', text.TrimEnd('\n').Split('\n').Select(line => new string(' ', by) + line)) + "\n";

    public static TheoryData<string, string, string> Problems => new()
    {
        { "an unknown key", With("colour: red\n"), "the report has an unknown key 'colour'" },
        { "a key in the wrong case", With("Title: x\n"), "keys are case-sensitive, so write 'title'" },
        { "a schedule", With("schedule: nightly\n"), "a report is never run as a flow, so remove it" },
        { "a mode", With("mode: manual\n"), "a report is never run as a flow" },
        { "a connections block", With("connections:\n  dwh: x\n"), "a report names the one connection its queries run on under 'connection'" },
        { "a target", With("target:\n  table: t\n"), "a report only reads" },
        { "a timeout out of bounds", With("timeoutSeconds: 601\n"), "'timeoutSeconds' of the report is 601; the seconds a query may run is from 1 to 600" },
        { "a row limit that is not a number", With("maxRows: many\n"), "'maxRows' of the report must be a whole number from 1 to 5000" },
        { "a reference in a name", With("batch: ${env:B}\n"), "'batch' of the report holds a reference" },
        { "a credential in prose", With("description: use password=hunter2 to sign in\n"), "'description' of the report appears to carry a credential" },
        { "a statement that refers to nothing declared", With(query: "sql: SELECT @x AS X"), "'sql' of query 'q' refers to @x, and the report declares no parameter" },
        {
            "a statement that refers to an undeclared parameter",
            With(parameters: "a:\n", query: "sql: SELECT @a AS A, @b AS B"),
            "'sql' of query 'q' refers to @b, which the report does not declare; its parameters are: a"
        },
        { "a parameter no statement binds", With(parameters: "a:\n"), "parameter 'a' is bound by no query, so setting it would change nothing" },
        { "a parameter name a statement cannot write", With(parameters: "my-source:\n"), "parameter 'my-source' has a name a statement cannot refer to" },
        { "a parameter declared twice", With(parameters: "a:\nA:\n", query: "sql: SELECT @a AS A"), "parameter 'A' is declared twice" },
        { "an unknown type", With(parameters: "a: { type: string }\n", query: "sql: SELECT @a AS A"), "'type' of parameter 'a' is 'string'; it may be: text, integer, number, date, datetime, flag" },
        { "a type in the wrong case", With(parameters: "a: { type: Date }\n", query: "sql: SELECT @a AS A"), "it is written 'date', in lower case" },
        { "a default of the wrong type", With(parameters: "a: { type: integer, default: many }\n", query: "sql: SELECT @a AS A"), "'default' of parameter 'a' is not an integer" },
        { "a default written with zeros its choices are written without", With(parameters: "a: { type: number, default: \"1.50\", choices: [2, 3.0] }\n", query: "sql: SELECT @a AS A"), "'default' of parameter 'a' is not one of its 'choices'" },
        { "an empty list of views", With(query: "sql: SELECT 1 AS One\nshow: []"), "'show' of query 'q' is an empty list" },
        { "a key the automatic view does not take", With(query: "sql: SELECT 1 AS One\nshow: { type: auto, by: A }"), "is an 'auto' view, which takes no 'by'" },
        { "a credential in a comment", With("# sign in with password=hunter2\n"), "carries what reads as a credential outside its values" },
        { "a default that is not a choice", With(parameters: "a: { default: z, choices: [x, y] }\n", query: "sql: SELECT @a AS A"), "'default' of parameter 'a' is not one of its 'choices'" },
        { "a choice twice", With(parameters: "a: { choices: [x, x] }\n", query: "sql: SELECT @a AS A"), "choice 2 of parameter 'a' repeats a value" },
        { "choices two ways", With(parameters: "a: { choices: [x], choicesFrom: q }\n", query: "sql: SELECT @a AS A"), "has both 'choices' and 'choicesFrom'" },
        { "choices on a flag", With(parameters: "a: { type: flag, choices: [true] }\n", query: "sql: SELECT @a AS A"), "is a flag, which is on or off" },
        { "choices from a query that is not there", With(parameters: "a: { choicesFrom: other }\n", query: "sql: SELECT @a AS A"), "names the query 'other', which the report does not have; its queries are: q" },
        { "choices that depend on the parameter", With(parameters: "a: { choicesFrom: q }\n", query: "sql: SELECT @a AS A"), "the choices of a parameter cannot depend on its own value" },
        { "a query with no statement", With(query: "title: Nothing"), "query 'q' has no 'sql'" },
        { "a query key that is not one", With(query: "sql: SELECT 1 AS One\nchart: bar"), "query 'q' has an unknown key 'chart'" },
        { "a credential in a statement", With(query: "sql: SELECT 1 AS One WHERE 'a' = 'password=hunter2'"), "'sql' of query 'q' appears to carry a credential" },
        { "an unknown view type", With(query: "sql: SELECT 1 AS One\nshow: pie"), "'show' of query 'q' is 'pie'; it may be: auto, figure, bar, column, line, share, table, matrix, none" },
        { "a matrix by its name alone", With(query: "sql: SELECT 1 AS One\nshow: matrix"), "is a matrix, which needs 'across'" },
        { "a matrix without across", With(query: "sql: SELECT 1 AS One\nshow: { type: matrix, by: A }"), "name that column under 'across'" },
        { "a view without a type", With(query: "sql: SELECT 1 AS One\nshow: { by: A }"), "'show' of query 'q' has no 'type'" },
        { "a key its view type does not take", With(query: "sql: SELECT 1 AS One\nshow: { type: share, series: A }"), "is a 'share' view, which takes no 'series'; 'series' goes with: bar, column, line" },
        { "two values on a share", With(query: "sql: SELECT 1 AS One\nshow: { type: share, by: A, values: [B, C] }"), "names 2 columns; a 'share' view shows one value" },
        { "a series beside several values", With(query: "sql: SELECT 1 AS One\nshow: { type: bar, by: A, values: [B, C], series: D }"), "a series splits one value" },
        { "a column named twice", With(query: "sql: SELECT 1 AS One\nshow: { type: bar, values: [B, b] }"), "names column 'b' twice" },
        { "selects without the column to read", With(parameters: "a:\n", query: "sql: SELECT @a AS A\nshow: { type: bar, selects: a }"), "so the view names the column that value is read from under 'by'" },
        { "selects of a parameter that is not there", With(query: "sql: SELECT 1 AS One\nshow: { type: bar, by: A, selects: a }"), "names the parameter 'a', and the report declares no parameter" },
        { "none beside another view", With(query: "sql: SELECT 1 AS One\nshow: [table, none]"), "is 'none', which shows nothing, beside other views" },
        { "a top out of bounds", With(query: "sql: SELECT 1 AS One\nshow: { type: bar, top: 51 }"), "'top' of 'show' of query 'q' is 51; the categories kept is from 1 to 50" },
        { "an unknown width", With(query: "sql: SELECT 1 AS One\nshow: { type: table, width: wide }"), "it may be: quarter, third, half, two-thirds, full" },
        { "an unknown format", With(query: "sql: SELECT 1 AS One\ncolumns: { One: money }"), "column 'One' of query 'q' is 'money'; it may be: text, integer, number, percent, bytes, duration, date, datetime, flag, code" },
        { "decimals on text", With(query: "sql: SELECT 1 AS One\ncolumns: { One: { format: text, decimals: 2 } }"), "'decimals' of column 'One' of query 'q' goes with a number" },
        { "every query hidden", With(query: "sql: SELECT 1 AS One\nshow: none"), "every query of the report has 'show: none'" },
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public void AProblem_IsRefused_SayingWhatToWrite(string what, string yaml, string expected)
    {
        var ex = Assert.Throws<FlowValidationException>(() => YamlReportFlowLoader.Parse(yaml, "reports/probe.yaml"));

        Assert.True(ex.Message.Contains(expected, StringComparison.Ordinal), $"{what}: expected '{expected}' in: {ex.Message}");
        Assert.StartsWith("reports/probe.yaml:", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Server=db;Database=dw;Integrated Security=true")]
    [InlineData("Server=db;User Id=sa;Password=hunter2")]
    [InlineData("${env:HOST};Database=dw")]
    [InlineData("dwh")]
    public void AConnection_IsOneWholeReference_AndWhatWasWrittenIsNeverRepeated(string connection)
    {
        var ex = Assert.Throws<FlowValidationException>(() => YamlReportFlowLoader.Parse(
            $"flowType: rpt\nname: probe\nconnection: \"{connection}\"\nqueries:\n  q:\n    sql: SELECT 1 AS One\n", "reports/probe.yaml"));

        Assert.Contains("must be one whole reference", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(connection, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProblem_IsReportedTogether_EachWithItsLine()
    {
        var ex = Assert.Throws<FlowValidationException>(() => YamlReportFlowLoader.Parse("""
            flowType: rpt
            schedule: nightly
            parameters:
              days: { type: number, default: lots }
            queries:
              a:
                sql: SELECT @days AS Days, @since AS Since
                show: pie
              b:
                title: No statement
            """, "reports/probe.yaml"));

        Assert.StartsWith("reports/probe.yaml: 7 problems in the report:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reports/probe.yaml:2: the report has 'schedule'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the report has no 'name'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the report has no 'connection'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reports/probe.yaml:4: 'default' of parameter 'days' is not a number", ex.Message, StringComparison.Ordinal);
        // A parameter that did not load is said once, where it is declared, and not again by every statement that
        // refers to it; one the document never declared is.
        Assert.DoesNotContain("refers to @days", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reports/probe.yaml:7: 'sql' of query 'a' refers to @since, which the report does not declare; its parameters are: days", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reports/probe.yaml:8: 'show' of query 'a' is 'pie'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("reports/probe.yaml:10: query 'b' has no 'sql'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flowType: rpt\nname: r\nconnection: ${env:X}\n", "the report has no 'queries'")]
    [InlineData("flowType: rpt\nname: r\nconnection: ${env:X}\nqueries: {}\n", "'queries' is empty")]
    [InlineData("", "the document is empty")]
    [InlineData("- a\n- b\n", "must be a mapping that starts with 'flowType: rpt'")]
    [InlineData("flowType: rpt\nname: [unclosed\n", "invalid YAML")]
    public void ADocumentThatShowsNothing_OrIsNotOne_IsRefused(string yaml, string expected)
    {
        var ex = Assert.Throws<FlowValidationException>(() => YamlReportFlowLoader.Parse(yaml, "reports/probe.yaml"));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStatementsParameters_AreItsOwn_NotWhatAStringACommentOrANameHolds()
    {
        const string Sql = """
            SELECT @First, [@not], "@nor", '@neither ''@still'' not', @@ROWCOUNT, col@x -- @comment
            FROM t /* @block /* @nested */ @still */ WHERE a = @second AND b = @FIRST AND c = @_third
            """;

        Assert.Equal(["First", "second", "_third"], ReportSql.ParameterNames(Sql));
        Assert.Empty(ReportSql.ParameterNames("SELECT 1 AS One"));
        Assert.Empty(ReportSql.ParameterNames("SELECT 'unterminated @x"));
    }

    [Fact]
    public void ARunsValues_AreTheDefaults_UnlessGiven_AndNullWhenCleared()
    {
        var report = YamlReportFlowLoader.Parse(Full);
        var perDay = report.Query("PER_DAY");
        Assert.NotNull(perDay);

        // Nothing given: the default, or NULL for a parameter without one. Only what the statement refers to is bound.
        Assert.Equal(
            [new QueryParameter("days", "integer", "30"), new QueryParameter("source", "text", null)],
            report.Bind(perDay, new Dictionary<string, string?>()));

        // Given: read as a value of the parameter's type and written its one way, whatever case names the parameter.
        Assert.Equal(
            [new QueryParameter("days", "integer", "7"), new QueryParameter("source", "text", "ERP")],
            report.Bind(perDay, new Dictionary<string, string?> { ["DAYS"] = " 007 ", ["source"] = "ERP" }));

        // Cleared: NULL, whatever the default.
        Assert.Equal(
            [new QueryParameter("days", "integer", null), new QueryParameter("source", "text", null)],
            report.Bind(perDay, new Dictionary<string, string?> { ["days"] = "", ["source"] = null }));
    }

    [Theory]
    [InlineData("days", "a week", "The value of parameter 'days' is not an integer")]
    [InlineData("region", "north", "declares no parameter 'region'; its parameters are: source, days, state")]
    public void ARunsValue_ThatIsNotOne_IsRefused(string name, string value, string expected)
    {
        var report = YamlReportFlowLoader.Parse(Full);

        var ex = Assert.Throws<SqlFlowException>(() => report.Bind(report.Queries[3], new Dictionary<string, string?> { [name] = value }));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AValue_MustBeOneOfAFixedListOfChoices_AndARequiredParameterMustHaveOne()
    {
        var report = YamlReportFlowLoader.Parse(Full);
        var sources = report.Queries[0];

        var notAChoice = Assert.Throws<SqlFlowException>(() => report.Bind(sources, new Dictionary<string, string?> { ["state"] = "lost" }));
        Assert.Contains("is not one of its choices: open, closed", notAChoice.Message, StringComparison.Ordinal);
        Assert.Equal([new QueryParameter("state", "text", "closed")], report.Bind(sources, new Dictionary<string, string?> { ["state"] = "closed" }));

        var required = YamlReportFlowLoader.Parse("""
            flowType: rpt
            name: r
            connection: ${env:X}
            parameters:
              day: { type: date, required: true }
            queries:
              q:
                sql: SELECT @day AS Day
            """);
        var missing = Assert.Throws<SqlFlowException>(() => required.Bind(required.Queries[0], new Dictionary<string, string?>()));
        Assert.Contains("Parameter 'day' is required by query 'q' and has no value", missing.Message, StringComparison.Ordinal);
        Assert.Equal(
            [new QueryParameter("day", "date", "2026-01-31")],
            required.Bind(required.Queries[0], new Dictionary<string, string?> { ["day"] = "2026-01-31" }));
    }

    [Theory]
    [InlineData("text", "  kept as written  ", "  kept as written  ")]
    [InlineData("integer", "-0042", "-42")]
    [InlineData("number", "1200.50", "1200.5")]
    [InlineData("number", "-0.2500", "-0.25")]
    [InlineData("number", "3.000", "3")]
    [InlineData("number", "0.00000001", "0.00000001")]
    [InlineData("date", "2026-01-31", "2026-01-31")]
    [InlineData("datetime", "2026-01-31 06:00", "2026-01-31T06:00:00")]
    [InlineData("datetime", "2026-01-31T06:00:00.5", "2026-01-31T06:00:00.5")]
    [InlineData("flag", "TRUE", "true")]
    public void AValue_IsWrittenOneWay(string type, string value, string canonical)
    {
        Assert.True(QueryParameter.TryRead(type, value, out _, out var written, out var problem), problem);
        Assert.Equal(canonical, written);
    }

    [Theory]
    [InlineData("integer", "1.5")]
    [InlineData("integer", "1,000")]
    [InlineData("number", "1,5")]
    [InlineData("date", "31.01.2026")]
    [InlineData("datetime", "2026-01-31T06:00:00Z")]
    [InlineData("flag", "yes")]
    [InlineData("money", "1")]
    public void AValue_ThatIsNotOfItsType_IsRefused_WithoutRepeatingIt(string type, string value)
    {
        Assert.False(QueryParameter.TryRead(type, value, out _, out _, out var problem));
        Assert.NotNull(problem);
        Assert.DoesNotContain(value, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT name FROM sys.tables")]
    [InlineData("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES")]
    [InlineData("SELECT COL_NAME(OBJECT_ID('dbo.t'), 1) AS FirstColumn")]
    [InlineData("SELECT SUM(row_count) AS Staged FROM sys.dm_db_partition_stats WHERE index_id < 2")]
    public void ADeclaredStatement_MayReadTheServersOwnCatalog_WhichAQuestionsStatementMayNot(string sql)
    {
        Assert.Equal(sql, ReadOnlyQueryGuard.Validate(sql, declared: true));
        Assert.True(ReadOnlyQueryGuard.IsReadOnly(sql, declared: true, out _));

        Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate(sql));
        Assert.False(ReadOnlyQueryGuard.IsReadOnly(sql, out _));
    }

    [Theory]
    [InlineData("INSERT INTO stg.Orders (Id) VALUES (1)", "Only a SELECT may be run here")]
    [InlineData("SELECT 1 AS One; DELETE FROM stg.Orders", "exactly ONE statement")]
    [InlineData("SELECT * INTO stg.Copy FROM stg.Orders", "SELECT ... INTO creates a table")]
    [InlineData("SELECT * FROM OPENQUERY(other, 'DELETE FROM t')", "OPENQUERY")]
    [InlineData("SELECT NEXT VALUE FOR dbo.Seq AS Id", "NEXT VALUE FOR")]
    [InlineData("SELECT dbo.xp_cmdshell('dir') AS Out", "xp_cmdshell")]
    [InlineData("DECLARE @x int = 1", "Only a SELECT may be run here")]
    [InlineData("SELECT FROM", "does not parse as T-SQL")]
    public void ADeclaredStatement_IsStillHeldToEveryRuleThatKeepsItReadOnly(string sql, string expected)
    {
        var ex = Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate(sql, declared: true));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANumber_IsOneValue_HoweverManyZerosFollowIt()
    {
        var report = YamlReportFlowLoader.Parse("""
            flowType: rpt
            name: r
            connection: ${env:X}
            parameters:
              factor: { type: number, default: "1.50", choices: [1.5, 2.0] }
            queries:
              q:
                sql: SELECT @factor AS Factor
            """);

        var factor = Assert.Single(report.Parameters);
        Assert.Equal("1.5", factor.Default);
        Assert.Equal(["1.5", "2"], factor.Choices.Select(c => c.Value));
        Assert.Equal(
            [new QueryParameter("factor", "number", "2")],
            report.Bind(report.Queries[0], new Dictionary<string, string?> { ["factor"] = "2.000" }));
    }

    [Fact]
    public void Validate_RefusesAReportWhoseStatementCannotRun_InTheFolderFormAsInTheSingleFileForm()
    {
        var loader = YamlDocumentLoader.CreateDefault();
        var broken = loader.Parse("""
            flowType: rpt
            name: r
            connection: ${env:X}
            queries:
              good:
                sql: SELECT 1 AS One
              bad:
                sql: DELETE FROM stg.Orders
            """, "reports/r.yaml");

        // Both forms of validate call this one check after loading a document.
        var ex = Assert.Throws<FlowValidationException>(() => DocumentLoader.CheckOffline(broken, "reports/r.yaml"));

        Assert.StartsWith("reports/r.yaml: 1 query of the report cannot run:", ex.Message, StringComparison.Ordinal);
        Assert.Contains("query 'bad': Only a SELECT may be run here; this is a DELETE.", ex.Message, StringComparison.Ordinal);

        var sound = loader.Parse("flowType: rpt\nname: r\nconnection: ${env:X}\nqueries:\n  q:\n    sql: SELECT 1 AS One\n", "reports/r.yaml");
        DocumentLoader.CheckOffline(sound, "reports/r.yaml");
    }

    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (1)", "this is an INSERT.")]
    [InlineData("EXEC dbo.p", "this is an EXEC.")]
    [InlineData("UPDATE t SET a = 1", "this is an UPDATE.")]
    [InlineData("DELETE FROM t", "this is a DELETE.")]
    [InlineData("TRUNCATE TABLE t", "this is a TRUNCATE TABLE.")]
    public void ARefusedStatement_IsNamedAsASentenceNamesIt(string sql, string expected)
    {
        var ex = Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate(sql, declared: true));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReportsStatements_AreCheckedInOnePlace()
    {
        var report = YamlReportFlowLoader.Parse("""
            flowType: rpt
            name: r
            connection: ${env:X}
            queries:
              good:
                sql: SELECT COUNT(*) AS Staged FROM stg.Orders
              bad:
                sql: UPDATE stg.Orders SET State = 'x'
              worse:
                sql: SELECT 1 AS One; SELECT 2 AS Two
            """);

        var problems = ReportStatements.Problems(report);

        Assert.Equal(["bad", "worse"], problems.Select(p => p.Query));
        Assert.Contains("Only a SELECT may be run here", problems[0].Problem, StringComparison.Ordinal);
        Assert.Null(ReportStatements.Problem(report.Queries[0]));
    }

    [Fact]
    public void TheLoader_ReadsItInEveryHost_AsAPipelineThatIsNeverRun()
    {
        var loader = YamlDocumentLoader.CreateDefault();

        var document = Assert.IsType<ReportFlowDocument>(loader.Parse(Full, "reports/staging.yaml"));

        Assert.True(loader.IsKnownFlowType("rpt"));
        Assert.Null(loader.FindKind("rpt"));
        Assert.DoesNotContain(loader.Kinds, k => k.FlowType == "rpt");
        Assert.Contains("rpt", YamlDocumentLoader.NeverRunKinds);
        Assert.Equal(ReportFlowKind.NeverRuns, YamlDocumentLoader.NeverRunReason(" RPT "));
        Assert.Equal("rpt", document.Kind);
        Assert.Equal(ExecutionMode.Disabled, document.Mode);
        Assert.Null(document.Schedule);
        Assert.False(document.ParticipatesInLineage);

        // The connection it reads is declared, so the report is listed among what uses that connection, and every
        // connection the document names is a credential reference the hygiene check sees.
        Assert.Equal("${env:SQLFLOW_DW}", document.SourceConnectionReference);
        Assert.Null(document.TargetConnectionReference);
        Assert.Equal([new KeyValuePair<string, string>("connection", "${env:SQLFLOW_DW}")], document.CredentialReferences);

        // Every catalog consumer projects the same header: a pipeline outside the graph, in no wave, that reads a server.
        var header = Assert.Single(FlowDocumentHeaders.Project(document));
        Assert.Equal(("staging-overview-rpt", "rpt", "staging"), (header.Name, header.Kind, header.Batch));
        Assert.False(header.ParticipatesInLineage);
        Assert.Equal("${env:SQLFLOW_DW}", header.SourceServerRef);
        Assert.Null(header.Schedule);
        Assert.Equal(ExecutionMode.Disabled, header.Mode);

        var profile = FlowLoadProfiles.Describe(document);
        Assert.Equal(FlowReadModes.NotApplicable, profile.ReadMode);
        Assert.Contains("4 read-only queries", profile.Read, StringComparison.Ordinal);
        Assert.Contains("never run as a flow", profile.Write, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostKind_MayNotTakeItsFlowType()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => YamlDocumentLoader.CreateDefault([new ImpostorKind()]));

        Assert.Contains("claims the built-in flowType 'RPT'", ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<RunParameters> Runs => new()
    {
        new RunParameters(),
        new RunParameters { FullLoad = true },
        new RunParameters { Operation = "refresh" },
    };

    [Theory]
    [MemberData(nameof(Runs))]
    public void TheLoadersRule_RefusesEveryRun(RunParameters parameters)
    {
        var ex = Assert.Throws<SqlFlowException>(() => YamlDocumentLoader.CreateDefault().ValidateRunParameters("rpt", parameters));

        Assert.Equal(ReportFlowKind.NeverRuns, ex.Message);
    }

    [Fact]
    public async Task TheExecutor_RefusesIt_BeforeAnythingRuns()
    {
        var loader = YamlDocumentLoader.CreateDefault();
        using var provider = new ServiceCollection().AddSingleton(loader).BuildServiceProvider();
        var document = loader.Parse(Full, "reports/staging.yaml");

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => new DocumentExecutor(provider).ExecuteAsync(
            document, "reports/staging.yaml", new DocumentExecutionOptions()));

        Assert.Equal(ReportFlowKind.NeverRuns, ex.Message);
    }

    [Fact]
    public void AReportsQuery_IsQueuedAsItsOwnOperation_WithTypedValues_AndReadsBackAsWritten()
    {
        var payload = new ComputeTaskPayload
        {
            Operation = ComputeOperations.ReportQuery,
            SourceRef = "${env:SQLFLOW_DW}",
            Database = "Staging",
            Sql = "SELECT name FROM sys.tables WHERE create_date >= @since",
            MaxRows = 50,
            TimeoutSeconds = 20,
            Parameters = [new QueryParameter("since", "date", "2026-01-31"), new QueryParameter("source", "text", null)],
            Report = "staging-overview-rpt",
            ReportQuery = "tables",
        };

        var read = ComputeTaskPayload.FromJson(payload.ToJson());

        Assert.Equal(payload.Parameters, read.Parameters);
        Assert.Equal(("staging-overview-rpt", "tables"), (read.Report, read.ReportQuery));
        var request = read.ToQueryRunRequest();
        Assert.True(request.Declared);
        Assert.Equal((50, 20, "Staging"), (request.MaxRows, request.TimeoutSeconds, request.Database));
        Assert.Equal(2, request.Parameters.Count);

        // An approved query is the other operation that carries a statement: it binds nothing and is not a declared one.
        var approved = new ComputeTaskPayload { Operation = ComputeOperations.RunQuery, SourceRef = "${env:SQLFLOW_DW}", Sql = "SELECT 1 AS One" };
        Assert.False(approved.ToQueryRunRequest().Declared);
        Assert.True(ComputeOperations.IsKnown(ComputeOperations.ReportQuery));
        Assert.Contains(ComputeOperations.ReportQuery, ComputeOperations.All);
    }

    [Theory]
    [InlineData("an approved query that binds values", "runQuery", "SELECT 1 AS One", "since", "date", "2026-01-31", "Only the reportQuery operation binds parameters")]
    [InlineData("a value that is not of its type", "reportQuery", "SELECT 1 AS One", "since", "date", "yesterday", "The value of parameter 'since' is not a date")]
    [InlineData("a name that is not a parameter's", "reportQuery", "SELECT 1 AS One", "since; DROP", "text", "x", "is not a parameter name")]
    [InlineData("a type that is not one", "reportQuery", "SELECT 1 AS One", "since", "money", "1", "has the type 'money'")]
    [InlineData("no statement", "reportQuery", "", "since", "date", "2026-01-31", "reportQuery carries the statement a report declares")]
    public void AQueuedQuery_ThatCouldNotRun_IsRefusedAtBothEnds(
        string what, string operation, string sql, string name, string type, string value, string expected)
    {
        var payload = new ComputeTaskPayload
        {
            Operation = operation,
            SourceRef = "${env:SQLFLOW_DW}",
            Sql = sql,
            Parameters = [new QueryParameter(name, type, value)],
        };

        var ex = Assert.Throws<SqlFlowException>(payload.Validate);
        Assert.True(ex.Message.Contains(expected, StringComparison.Ordinal), $"{what}: {ex.Message}");

        // The worker reads the queue row with the same rule.
        Assert.Throws<SqlFlowException>(() => ComputeTaskPayload.FromJson(payload.ToJson()));
    }

    [Fact]
    public void AParameterGivenTwice_InTwoSpellings_IsRefused()
    {
        var request = new QueryRunRequest
        {
            Sql = "SELECT @a AS A",
            Parameters = [new QueryParameter("a", "text", "x"), new QueryParameter("A", "text", "y")],
        };

        var ex = Assert.Throws<SqlFlowException>(() => request.Validate());

        Assert.Contains("Parameter 'A' is given twice", ex.Message, StringComparison.Ordinal);
    }

    private sealed class ImpostorKind : IFlowDocumentKind
    {
        public string FlowType => "RPT";

        public string Description => "an impostor";

        public IReadOnlyList<FlowKindOperation> Operations => [];

        public RegisteredFlowDocument Parse(string yaml, string source) => throw new InvalidOperationException("never parsed");

        public void ValidateParameters(RunParameters parameters)
        {
        }
    }
}
