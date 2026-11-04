using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Proof values from staged data and the staged delivered assertion as documents write them
/// (osdu/docs/reference/flow/staged-proof-values.md, osdu/docs/reference/flow/staged-accounting.md): the flow's queries, each
/// where it runs and its one read-only SELECT; a query's value wherever a value is compared with, named or written in place;
/// known values read from a query, their values its columns; and delivered's staged, explained and values. Every refusal says
/// what to write instead, and a flow writing none of them keeps the definition hash it had.
/// </summary>
public sealed class StagedProofValueDocumentTests
{
    private static readonly DeliveryDocumentLoader Loader = new();

    private const string Connection = "${env:OSDU_DATA_DB}";

    private static AssertionFlowDefinition Parse(string yaml) => Loader.ParseAssertion(yaml, "flow.yaml");

    private static string Flow(string assertions, string queries = DefaultQueries, string kind = Samples.WellboreKind) => $"""
        flowType: assertion
        name: staged
        partitions: [dev]
        parameters:
          system: {"{"} default: welldb {"}"}
        {queries}
        source: {"{"} endpoint: http://localhost {"}"}
        tests:
          - name: wellbores
            kind: {kind}
            assert:
        {Indent(assertions, 6)}
        """;

    private const string DefaultQueries = """
        queries:
          staged-wellbores:
            connection: ${env:OSDU_DATA_DB}
            sql: SELECT COUNT(*) FROM silver.Wellbore WHERE source_system = {system}
          staged-rows:
            connection: ${env:OSDU_DATA_DB}
            sql: SELECT wellbore_id, wellbore_name, kb FROM silver.Wellbore WHERE source_system = {system}
            timeoutSeconds: 60
        """;

    private static string Indent(string text, int spaces)
        => string.Join("\n", text.Split('\n').Where(l => l.Trim().Length > 0).Select(l => new string(' ', spaces) + l.TrimEnd('\r')));

    private static string Queries(string sql, string extra = "") => $"""
        queries:
          q:
            connection: {Connection}
            sql: "{sql}"
        {Indent(extra, 4)}
        """;

    private static string Refusal(string assertions, string queries = DefaultQueries)
        => Assert.Throws<FlowValidationException>(() => Parse(Flow(assertions, queries))).Message;

    [Fact]
    public void A_flow_declares_its_queries_each_with_where_it_runs_and_its_select()
    {
        var flow = Parse(Flow("- count: { query: staged-wellbores }"));

        var counted = flow.Queries["staged-wellbores"];
        Assert.Equal("staged-wellbores", counted.Name);
        Assert.Equal(Connection, counted.Connection);
        Assert.Equal("SELECT COUNT(*) FROM silver.Wellbore WHERE source_system = {system}", counted.Sql);
        Assert.Equal(StagedQuery.DefaultTimeoutSeconds, counted.TimeoutSeconds);
        Assert.Equal(60, flow.Queries["staged-rows"].TimeoutSeconds);

        var count = Assert.IsType<CountAssertion>(flow.Tests[0].Assertions[0]);
        var value = Assert.Single(count.Comparison.Terms).Value;
        Assert.Equal(ExpectedValueKind.Query, value.Kind);
        Assert.Equal(counted, value.Query);
        Assert.Equal("count equals query staged-wellbores", count.Label);
    }

    [Theory]
    [InlineData("connection: Server=tcp:db;Database=x;User ID=u;Password=hunter2", "carries a literal password")]
    [InlineData("sql: SELECT 1", "queries.q.connection names the database the query runs against")]
    [InlineData("connection: ${env:OSDU_DATA_DB}", "queries.q.sql is the query's one SELECT")]
    [InlineData("connection: ${env:OSDU_DATA_DB}\n    sql: DELETE FROM silver.Wellbore", "Only a SELECT may be run here")]
    [InlineData("connection: ${env:OSDU_DATA_DB}\n    sql: SELECT 1; DROP TABLE silver.Wellbore", "exactly ONE statement")]
    [InlineData("connection: ${env:OSDU_DATA_DB}\n    sql: SELECT * INTO silver.Copy FROM silver.Wellbore", "SELECT ... INTO creates a table")]
    [InlineData("connection: ${env:OSDU_DATA_DB}\n    sql: SELECT COUNT(*) FROM silver.Wellbore WHERE s = {nope}", "uses '{nope}', which is neither {partition} nor declared under parameters")]
    [InlineData("connection: ${env:OSDU_DATA_DB}\n    sql: SELECT 1\n    timeoutSeconds: 0", "timeoutSeconds is how long the query may run")]
    [InlineData("connection: ${env:OSDU_DATA_DB}\n    sql: SELECT 1\n    timeoutSeconds: 3601", "1 to 3600")]
    public void A_query_that_could_leak_a_secret_or_change_data_or_never_end_is_refused(string body, string says)
    {
        var queries = "queries:\n  q:\n    " + body;
        var message = Refusal("- count: { query: q }", queries);
        Assert.Contains(says, message, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_inside_quotes_would_be_read_as_text_so_it_is_refused_saying_how_to_write_it()
    {
        var literal = Refusal("- count: { query: q }", "queries:\n  q:\n    connection: ${env:OSDU_DATA_DB}\n    sql: \"SELECT COUNT(*) FROM t WHERE s = '{system}'\"");
        Assert.Contains("writes {system} inside a string literal ('{system}'), where it would be read as text. A token is bound as a value: write it without quotes, as source_system = {system}.", literal, StringComparison.Ordinal);

        var named = Refusal("- count: { query: q }", "queries:\n  q:\n    connection: ${env:OSDU_DATA_DB}\n    sql: SELECT COUNT(*) FROM [{system}]");
        Assert.Contains("inside a quoted name", named, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("staged rows", "is not a query name")]
    [InlineData("Staged-Wellbores", "twice, ignoring case")]
    public void A_query_is_named_as_a_test_is_and_once(string name, string says)
    {
        var queries = DefaultQueries + $"\n  {name}:\n    connection: {Connection}\n    sql: SELECT 1";
        Assert.Contains(says, Refusal("- count: { query: staged-wellbores }", queries), StringComparison.Ordinal);
    }

    [Fact]
    public void A_query_is_named_or_written_in_place_where_a_value_is_compared_with()
    {
        var flow = Parse(Flow("""
            - count: { atLeast: { query: staged-wellbores } }
            - field: data.FacilityName
              equals: { connection: "${env:OSDU_DATA_DB}", sql: "SELECT MAX(wellbore_name) FROM silver.Wellbore", timeoutSeconds: 30 }
            - aggregate: max
              field: data.SequenceNumber
              between: [0, { query: staged-wellbores }]
            """));

        var assertions = flow.Tests[0].Assertions;
        Assert.Equal(ExpectedValueKind.Query, ((CountAssertion)assertions[0]).Comparison.Terms[0].Value.Kind);
        var inline = Assert.Single(((ValueAssertion)assertions[1]).Condition.Operands).Query!;
        Assert.Null(inline.Name);
        Assert.Equal(30, inline.TimeoutSeconds);
        Assert.Equal("an inline query", inline.ToString());
        var between = ((AggregateAssertion)assertions[2]).Comparison.Terms[0];
        Assert.Equal(ExpectedValueKind.Number, between.Value.Kind);
        Assert.Equal(ExpectedValueKind.Query, between.Upper!.Kind);
    }

    [Theory]
    [InlineData("- count: { query: nope }", "names the query 'nope', which the flow does not declare under queries; it declares staged-rows, staged-wellbores.")]
    [InlineData("- count: { query: staged-wellbores, sql: SELECT 1 }", "connection, sql and timeoutSeconds go with it under queries")]
    [InlineData("- field: data.FacilityName\n  equals: { colour: red }", "takes a single value, or the value of a query")]
    [InlineData("- count: { equals: { sql: SELECT 1 } }", "connection names the database the query runs against")]
    public void A_query_a_value_names_is_one_the_flow_declares_or_one_written_whole(string assertion, string says)
        => Assert.Contains(says, Refusal(assertion), StringComparison.Ordinal);

    [Fact]
    public void A_query_named_where_the_flow_declares_none_says_so()
        => Assert.Contains("names the query 'q', and the flow declares no queries; declare it under queries, with its connection and sql.", Refusal("- count: { query: q }", string.Empty), StringComparison.Ordinal);

    [Fact]
    public void Known_values_read_from_a_query_name_its_columns()
    {
        var known = (KnownValuesAssertion)Parse(Flow("""
            - knownValues:
                query: staged-rows
                maxRows: 50000
                tolerance: 0.01
                locateBy: { data.FacilityID: wellbore_id }
                check:
                  data.FacilityName: wellbore_name
                  data.SequenceNumber: { atLeast: kb }
            """)).Tests[0].Assertions[0];

        Assert.Equal("staged-rows", known.Query!.Name);
        Assert.Equal(50000, known.MaxRows);
        var pattern = Assert.Single(known.Rows);
        Assert.Equal(ExpectedValue.OfColumn("wellbore_id"), pattern.Locate[0].Value);
        Assert.Equal(ExpectedValue.OfColumn("wellbore_name"), pattern.Checks[0].Condition.Operands[0]);
        Assert.Equal(ExpectedValue.OfColumn("kb"), pattern.Checks[1].Condition.Operands[0]);
        Assert.Equal(0.01, pattern.Checks[1].Condition.Tolerance);
        Assert.Equal("known values of each row of query staged-rows", known.Label);
        Assert.Equal("each row query staged-rows answers finds one record and holds its 2 proof value(s)", known.Expected);
    }

    [Fact]
    public void Known_values_take_a_query_written_in_place_too()
    {
        var known = (KnownValuesAssertion)Parse(Flow("""
            - knownValues:
                connection: "${env:OSDU_DATA_DB}"
                sql: SELECT wellbore_id FROM silver.Wellbore
                locateBy: { data.FacilityID: wellbore_id }
                check: { data.Source: ~ }
            """)).Tests[0].Assertions[0];

        Assert.Null(known.Query!.Name);
        Assert.Equal("SELECT wellbore_id FROM silver.Wellbore", known.Query.Sql);
    }

    [Theory]
    [InlineData("query: staged-rows\nrows: [{ locateBy: { data.FacilityID: w1 } }]\nlocateBy: { data.Name: wellbore_name }", "each row the query answers is a row")]
    [InlineData("query: staged-rows\nconnection: \"${env:OSDU_DATA_DB}\"\nlocateBy: { data.FacilityID: wellbore_id }", "name a query, or write one here, not both")]
    [InlineData("maxRows: 10\nlocateBy: { data.FacilityID: w1 }", "maxRows bounds the rows a query answers, and the known values read no query")]
    [InlineData("query: staged-rows\nmaxRows: 0\nlocateBy: { data.FacilityID: wellbore_id }", "1 to 1000000")]
    [InlineData("query: staged-rows\nlocateBy: { data.FacilityID: 5 }", "names a column of the rows the known values' query answers, and 5 is not a column name")]
    [InlineData("query: nope\nlocateBy: { data.FacilityID: wellbore_id }", "knownValues.query names 'nope', which the flow does not declare under queries")]
    [InlineData("locateBy: { data.FacilityID: { query: staged-wellbores } }", "a row is located by values its record equals")]
    public void Known_values_read_from_a_query_are_written_one_way(string body, string says)
        => Assert.Contains(says, Refusal("- knownValues:\n" + Indent(body, 4)), StringComparison.Ordinal);

    [Fact]
    public void A_staged_delivered_assertion_reads_under_values_and_weighs_explained_rows()
    {
        var delivered = (DeliveredAssertion)Parse(Flow("""
            - delivered: welldb-wellbore-03-delivery
              staged: true
              exact: true
              explained: warning
              values: { region: [north, "{system}"], area: A1 }
            """, string.Empty)).Tests[0].Assertions[0];

        Assert.True(delivered.Staged);
        Assert.True(delivered.Exact);
        Assert.Equal(AssertionSeverity.Warning, delivered.Explained);
        Assert.Equal(["area", "region"], delivered.Values!.Keys);
        Assert.Equal("A1", delivered.Values["area"]);
        Assert.Equal(SqlFlow.Core.Runs.RunValueList.Of(["north", "{system}"]), delivered.Values["region"]);
        Assert.Equal("every row welldb-wellbore-03-delivery stages accounted for", delivered.Label);
        Assert.StartsWith("every row welldb-wellbore-03-delivery stages is a record the test matches, or its ledger says why not, and the test matches no record", delivered.Expected, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("explained: warning", "takes explained and values only with staged: true")]
    [InlineData("values: { region: north }", "takes explained and values only with staged: true")]
    [InlineData("staged: true\nvalues: { partition: dev }", "the partition is the run's, never a value of the delivery flow")]
    [InlineData("staged: true\nvalues: { region: \"{nope}\" }", "uses '{nope}', which is neither {partition} nor declared under parameters")]
    [InlineData("staged: true\nvalues: { region: ~ }", "has no value")]
    [InlineData("staged: true\nvalues: [north]", "maps the delivery flow's parameters to the values its rows are read under")]
    [InlineData("staged: true\nexplained: fatal", "is not one of info, warning, error")]
    public void A_delivered_assertion_takes_its_staged_settings_one_way(string settings, string says)
        => Assert.Contains(says, Refusal("- delivered: welldb-wellbore-03-delivery\n" + Indent(settings, 2), string.Empty), StringComparison.Ordinal);

    [Fact]
    public void A_field_quantifier_is_still_a_word()
        => Assert.Contains("values is all or any, a word rather than a list or a mapping", Refusal("- field: data.FacilityName\n  exists: true\n  values: [all]", string.Empty), StringComparison.Ordinal);

    [Fact]
    public void A_flow_writing_none_of_it_keeps_the_definition_hash_it_had()
    {
        // The hash main gave this test before staged proof values existed: a flow without them keeps every result it has.
        Assert.Equal(HashOnMain, Parse(WithoutStaged).Tests[0].DefinitionHash);

        var staged = Parse(WithoutStaged.Replace("      - delivered: wells-delivery\n", "      - delivered: wells-delivery\n        staged: true\n", StringComparison.Ordinal)).Tests[0].DefinitionHash;
        Assert.NotEqual(HashOnMain, staged);
    }

    /// <summary>What main's loader gave <see cref="WithoutStaged"/>'s test, measured on main before this change.</summary>
    private const string HashOnMain = "d438b062edcf2693";

    private const string WithoutStaged = """
        flowType: assertion
        name: staged
        partitions: [dev]
        source: { endpoint: http://localhost }
        tests:
          - name: wellbores
            kind: osdu:wks:master-data--Wellbore:1.3.0
            assert:
              - count: { atLeast: 1, atMost: 5000 }
              - { field: data.FacilityName, equals: "A/1-F-11", values: any }
              - { aggregate: max, field: data.SequenceNumber, between: [0, 10] }
              - delivered: wells-delivery
                exact: true
        """;

    [Fact]
    public void A_token_outside_quotes_and_comments_is_bound_as_a_parameter()
    {
        var (sql, tokens) = StagedSql.Parameterize("SELECT COUNT(*) FROM t -- {system} in a comment stays\nWHERE a = {system} /* {area} */ AND b = 'it''s' AND c = {1st} AND d = {system}", "q");

        Assert.Equal("SELECT COUNT(*) FROM t -- {system} in a comment stays\nWHERE a = @system /* {area} */ AND b = 'it''s' AND c = @_1st AND d = @system", sql);
        Assert.Equal(["system", "1st"], tokens);

        var read = StagedSql.Read(new StagedQuery { Connection = Connection, Sql = "SELECT 1 WHERE x = {system} AND p = {partition}" }, new Dictionary<string, string>(StringComparer.Ordinal) { ["system"] = "welldb", ["partition"] = "dev" });
        Assert.Equal("SELECT 1 WHERE x = @system AND p = @partition", read.Sql);
        Assert.Equal("welldb", read.Parameters["system"]);
        Assert.Equal("dev", read.Parameters["partition"]);
        Assert.Equal(StagedQuery.DefaultTimeoutSeconds, read.TimeoutSeconds);

        var missing = Assert.Throws<DeliveryException>(() => StagedSql.Read(new StagedQuery { Name = "q", Connection = Connection, Sql = "SELECT {area}" }, new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Equal("query q uses '{area}', which the run gives no value.", missing.Message);
    }

    [Fact]
    public void A_value_a_query_answers_compares_as_the_value_it_is()
    {
        Assert.Equal(ExpectedValue.OfNumber(1250.4, "1250.4"), StagedQueries.Value(1250.4000m));
        Assert.Equal(ExpectedValue.OfNumber(7, "7"), StagedQueries.Value(7L));
        Assert.Equal(ExpectedValue.OfNumber(0, "0"), StagedQueries.Value(0.0000m));
        Assert.Equal(ExpectedValueKind.Number, StagedQueries.Value(74.2f).Kind);
        Assert.Equal(ExpectedValue.OfText("2026-01-02T03:04:05Z"), StagedQueries.Value(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified)));
        Assert.Equal(ExpectedValue.OfText("2026-01-02T03:04:05.5+02:00"), StagedQueries.Value(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 500, TimeSpan.FromHours(2))));
        Assert.Equal(ExpectedValue.OfBoolean(true), StagedQueries.Value(true));
        Assert.Equal(ExpectedValue.OfText("0x0A0B"), StagedQueries.Value(new byte[] { 10, 11 }));
        Assert.Equal(ExpectedValue.OfText("00000000-0000-0000-0000-000000000001"), StagedQueries.Value(new Guid("00000000-0000-0000-0000-000000000001")));
    }
}
