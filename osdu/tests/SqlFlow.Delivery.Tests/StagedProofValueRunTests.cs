using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Proof values from staged data end to end (osdu/docs/reference/flow/staged-proof-values.md): a staged table in the test
/// database, an assertion flow whose queries read it through a connection reference, and a fake OSDU. A count equals what a
/// query counts, known values take one row per row a query answers, a query's tokens are bound as values, and a query that
/// fails or answers what cannot be compared leaves its assertion errored, saying why, while the test's other assertions are
/// judged.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class StagedProofValueRunTests : IDisposable
{
    /// <summary>The environment variable the flows' connection names, set to the test database for the class's life.</summary>
    private const string ConnectionVariable = "SQLFLOW_STAGED_PROOF_TEST_DB";

    /// <summary>The staged table the queries read, created and dropped by the class.</summary>
    private const string Table = "StagedProofValueWellbores";

    private const string Wellbore = Samples.WellboreKind;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeAssertionPlatform _platform = new();

    public StagedProofValueRunTests()
    {
        Execute($"""
            IF OBJECT_ID(N'dbo.{Table}') IS NOT NULL DROP TABLE dbo.{Table};
            CREATE TABLE dbo.{Table} (wellbore_id nvarchar(50) NULL, wellbore_name nvarchar(100) NULL, source_system nvarchar(20) NOT NULL, sequence decimal(18, 4) NULL);
            INSERT INTO dbo.{Table} VALUES
              (N'w1', N'A/1-F-11', N'WellDB', 1.0000),
              (N'w2', N'A/1-F-12', N'WellDB', 2.0000),
              (N'w3', N'A/1-F-13', N'WellDB', 3.0000),
              (N'w9', N'A/1-F-19', N'WellDB', 9.0000),
              (NULL, N'nameless', N'WellDB', NULL),
              (N'x1', N'Other''s', N'Other', 1.0000);
            """);
        Environment.SetEnvironmentVariable(ConnectionVariable, _db.ConnectionString);
        _platform.Add("dev:master-data--Wellbore:w1", Wellbore, Data("w1", "A/1-F-11", 1));
        _platform.Add("dev:master-data--Wellbore:w2", Wellbore, Data("w2", "A/1-F-12", 2));
        _platform.Add("dev:master-data--Wellbore:w3", Wellbore, Data("w3", "A/1-F-33", 3));
    }

    public void Dispose()
    {
        Execute($"IF OBJECT_ID(N'dbo.{Table}') IS NOT NULL DROP TABLE dbo.{Table};");
        Environment.SetEnvironmentVariable(ConnectionVariable, null);
        _platform.Dispose();
        _db.Dispose();
    }

    private void Execute(string sql)
    {
        using var connection = new SqlConnection(_db.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static JsonObject Data(string id, string name, int sequence)
        => new() { ["FacilityID"] = id, ["FacilityName"] = name, ["SequenceNumber"] = sequence, ["Source"] = "WellDB" };

    private static string Flow(string assertions, string queries = "") => $$"""
        flowType: assertion
        name: staged-proof-assertion
        partitions: [dev]
        parameters:
          system: { default: WellDB }
        queries:
          staged-wellbores:
            connection: ${env:{{ConnectionVariable}}}
            sql: SELECT COUNT(*) FROM dbo.{{Table}} WHERE source_system = {system} AND wellbore_id IS NOT NULL
          staged-rows:
            connection: ${env:{{ConnectionVariable}}}
            sql: |
              SELECT wellbore_id, wellbore_name, sequence FROM dbo.{{Table}} WHERE source_system = {system}
        {{queries}}
        source:
          endpoint: http://localhost
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        tests:
          - name: wellbores
            kind: {{Wellbore}}
            assert:
        {{assertions}}
        """;

    private async Task<TestResult> RunAsync(string yaml)
    {
        var ledger = _db.Ledger(_clock);
        var flow = new DeliveryDocumentLoader().ParseAssertion(yaml, "flow.yaml").ForPartition("dev");
        var runner = new AssertionRunner(Samples.Engine(ledger, _clock), flow, new Dictionary<string, string>(StringComparer.Ordinal) { ["system"] = "WellDB" }, Samples.Logger<AssertionRunner>(), _platform, allowLoopback: true);
        var outcome = await runner.TestAsync([], [], Guid.CreateVersion7(), "tests", CancellationToken.None);
        return TestResults.Deserialize(Assert.Single(await ledger.ListAssertionResultsAsync(outcome.AssertionRunId)).Detail)!;
    }

    [Fact]
    public async Task A_count_equals_what_a_named_query_counts_and_says_where_the_value_came_from()
    {
        var result = await RunAsync(Flow("""
                  - count: { query: staged-wellbores }
                  - count: { atLeast: { connection: "${env:SQLFLOW_STAGED_PROOF_TEST_DB}", sql: "SELECT COUNT(*) - 3 FROM dbo.StagedProofValueWellbores" } }
            """));

        Assert.Equal(TestOutcomes.Failed, result.Outcome);
        var named = result.Assertions[0];
        Assert.Equal(TestOutcomes.Failed, named.Outcome);
        Assert.Equal("equals 4 (query staged-wellbores)", named.Expected);
        Assert.Equal("3 record(s)", named.Actual);
        var inline = result.Assertions[1];
        Assert.Equal(TestOutcomes.Passed, inline.Outcome);
        Assert.Equal("atLeast 3 (an inline query)", inline.Expected);
        Assert.Contains("Query staged-wellbores answered 4.", result.Notes);
        Assert.Contains("An inline query answered 3.", result.Notes);
    }

    [Fact]
    public async Task A_warning_band_widens_a_count_a_query_answered()
    {
        var result = await RunAsync(Flow("""
                  - count: { query: staged-wellbores }
                    warnWithin: 25%
            """));

        Assert.Equal(TestOutcomes.Warned, result.Outcome);
        Assert.Equal("warning", Assert.Single(result.Assertions).Severity);
    }

    [Fact]
    public async Task A_query_that_fails_or_answers_what_cannot_be_compared_leaves_its_assertion_errored_and_the_rest_judged()
    {
        var result = await RunAsync(Flow(
            """
                  - count: { query: missing-table }
                  - count: { query: two-rows }
                  - count: { query: a-name }
                  - count: 3
            """,
            $$"""
              missing-table:
                connection: ${env:{{ConnectionVariable}}}
                sql: SELECT COUNT(*) FROM dbo.NoSuchStagedTable
              two-rows:
                connection: ${env:{{ConnectionVariable}}}
                sql: SELECT wellbore_id FROM dbo.{{Table}} WHERE wellbore_id IN ('w1', 'w2')
              a-name:
                connection: ${env:{{ConnectionVariable}}}
                sql: SELECT wellbore_name FROM dbo.{{Table}} WHERE wellbore_id = 'w1'
            """));

        Assert.Equal(TestOutcomes.Errored, result.Assertions[0].Outcome);
        Assert.StartsWith("query missing-table failed (SQL error 208): ", result.Assertions[0].Message, StringComparison.Ordinal);
        Assert.Equal(TestOutcomes.Errored, result.Assertions[1].Outcome);
        Assert.Equal("query two-rows answered more than one row; a value compared with is one row of one column, so give it an aggregate (COUNT, MAX) or a WHERE that finds one row.", result.Assertions[1].Message);
        Assert.Equal(TestOutcomes.Errored, result.Assertions[2].Outcome);
        Assert.Equal("Query a-name answered \"A/1-F-11\", and a count is compared with a whole number, zero or more.", result.Assertions[2].Message);
        Assert.Equal(TestOutcomes.Passed, result.Assertions[3].Outcome);
    }

    [Fact]
    public async Task Known_values_hold_osdu_to_each_row_a_query_answers()
    {
        var result = await RunAsync(Flow("""
                  - knownValues:
                      query: staged-rows
                      locateBy: { data.FacilityID: wellbore_id }
                      check:
                        data.FacilityName: wellbore_name
                        data.SequenceNumber: { equals: sequence, tolerance: 0.001 }
            """));

        var known = Assert.Single(result.Assertions);
        Assert.Equal(TestOutcomes.Failed, known.Outcome);
        Assert.Equal("each of the 5 row(s) query staged-rows answered finds one record and holds its 10 proof value(s)", known.Expected);
        Assert.StartsWith("5 row(s) with 10 proof value(s): 2 hold their values, 1 differ, 2 not found, 0 found more than once", known.Actual, StringComparison.Ordinal);
        Assert.Contains("Query staged-rows answered 5 row(s) of known values.", result.Notes);
        var differs = Assert.Single(known.Examples, e => e.Id == "dev:master-data--Wellbore:w3");
        Assert.Contains("data.FacilityName is \"A/1-F-33\", expected it equals \"A/1-F-13\"", differs.Reason, StringComparison.Ordinal);
        Assert.Contains(known.Examples, e => e.Value == "\"w9 \"" || (e.Value ?? string.Empty).Contains("w9", StringComparison.Ordinal));
        var unlocated = Assert.Single(known.Examples, e => e.Reason.Contains("answered no value", StringComparison.Ordinal));
        Assert.Equal("query staged-rows answered no value in wellbore_id, which the row is located by (data.FacilityID)", unlocated.Reason);
    }

    [Fact]
    public async Task Known_values_name_the_columns_a_query_does_not_answer()
    {
        var result = await RunAsync(Flow("""
                  - knownValues:
                      query: staged-rows
                      locateBy: { data.FacilityID: wellbore_id }
                      check: { data.FacilityName: facility_name }
            """));

        var known = Assert.Single(result.Assertions);
        Assert.Equal(TestOutcomes.Errored, known.Outcome);
        Assert.Equal("The known values name the column facility_name, which query staged-rows does not answer; it answers wellbore_id, wellbore_name, sequence.", known.Message);
    }

    [Fact]
    public async Task A_token_is_bound_as_a_value_so_a_quote_in_it_is_text()
    {
        var yaml = Flow(
            """
                  - count: { query: other-system }
            """,
            $$"""
              other-system:
                connection: ${env:{{ConnectionVariable}}}
                sql: SELECT COUNT(*) FROM dbo.{{Table}} WHERE wellbore_name = {name}
            """).Replace("  system: { default: WellDB }", "  system: { default: WellDB }\n  name: { default: \"Other's\" }", StringComparison.Ordinal);
        var ledger = _db.Ledger(_clock);
        var flow = new DeliveryDocumentLoader().ParseAssertion(yaml, "flow.yaml").ForPartition("dev");
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["system"] = "WellDB", ["name"] = "Other's" };
        var runner = new AssertionRunner(Samples.Engine(ledger, _clock), flow, values, Samples.Logger<AssertionRunner>(), _platform, allowLoopback: true);
        var outcome = await runner.TestAsync([], [], Guid.CreateVersion7(), "tests", CancellationToken.None);
        var result = TestResults.Deserialize(Assert.Single(await ledger.ListAssertionResultsAsync(outcome.AssertionRunId)).Detail)!;

        // One row is named Other's; the count of the wellbores OSDU holds is 3, so the comparison fails on 3 against 1.
        Assert.Equal("equals 1 (query other-system)", Assert.Single(result.Assertions).Expected);
    }

    [Fact]
    public async Task Many_rows_located_alike_are_found_by_reading_the_tests_records_once()
    {
        // One INSERT takes at most 1000 rows, so the rows go in two.
        foreach (var start in new[] { 0, 600 })
        {
            var insert = new StringBuilder($"INSERT INTO dbo.{Table} VALUES ");
            for (var i = start; i < start + 600; i++)
            {
                insert.Append(CultureInfo.InvariantCulture, $"{(i == start ? string.Empty : ", ")}(N'm{i}', N'M-{i}', N'Many', {i})");
                _platform.Add($"dev:master-data--Wellbore:m{i}", Wellbore, Data($"m{i}", $"M-{i}", i));
            }

            Execute(insert.ToString());
        }
        var result = await RunAsync(Flow(
            """
                  - knownValues:
                      query: many-rows
                      locateBy: { data.FacilityID: wellbore_id }
                      check: { data.FacilityName: wellbore_name }
            """,
            $$"""
              many-rows:
                connection: ${env:{{ConnectionVariable}}}
                sql: SELECT wellbore_id, wellbore_name FROM dbo.{{Table}} WHERE source_system = 'Many'
            """).Replace("    kind: ", "    maxRecords: 5000\n    kind: ", StringComparison.Ordinal));

        var known = Assert.Single(result.Assertions);
        Assert.Equal(TestOutcomes.Passed, known.Outcome);
        Assert.StartsWith("1200 row(s) with 1200 proof value(s): 1200 hold their values", known.Actual, StringComparison.Ordinal);
        Assert.Contains("1203 record(s) read in 1 search(es)", known.Actual, StringComparison.Ordinal);
    }
}
