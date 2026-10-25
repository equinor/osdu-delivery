using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Grouped views and joins to them built end to end on SQL Server, at every edge: a JSON array per group written from its
/// rows with every text escaped and a null field left out, as Spark's <c>to_json</c> writes one; an aggregate of text past
/// 8,000 bytes; the greatest and least text by code point where SQL Server's binary order differs; white space a field
/// keeps; groups of each partition apart; a group with no row and a key with none; the check's counts over the groups and
/// over the rows a join meets; a conversion of an aggregate that cannot read; a view joining a view its build could not
/// write; and a view written again when the view it joins changes, and left alone when nothing did.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
[Trait(DimensionEdgeCaseSuite.Trait, DimensionEdgeCaseSuite.Name)]
public sealed class DimensionViewSqlServerEdgeCaseTests : IDisposable
{
    private const string WellLog = Samples.WellLogKind;
    private const string AssuranceType = "osdu:wks:reference-data--TechnicalAssuranceType:1.0.0";
    private const string TargetVariable = "OSDU_VIEW_EDGE_TESTS_DB";

    /// <summary>A name whose first character is U+FFFD, which SQL Server's binary order puts after the emoji, and code points before it.</summary>
    private const string Replacement = "� replacement";
    private const string Emoji = "😀 emoji";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { AggregationSize = 50 };

    public DimensionViewSqlServerEdgeCaseTests()
    {
        Environment.SetEnvironmentVariable(TargetVariable, _db.ConnectionString);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TargetVariable, null);
        _db.Dispose();
        _platform.Dispose();
    }

    /// <summary>The assurances of each log as a JSON array of their types' code and name, as Spark's to_json writes them.</summary>
    private const string Json = """
        CONCAT('[', STRING_AGG(CONCAT('{',
          IIF(Type.Code IS NULL, '', CONCAT('"code":"', STRING_ESCAPE(Type.Code, 'json'), '"')),
          IIF(Type.Code IS NULL OR Type.Name IS NULL, '', ','),
          IIF(Type.Name IS NULL, '', CONCAT('"name":"', STRING_ESCAPE(Type.Name, 'json'), '"')),
          '}'), ',') WITHIN GROUP (ORDER BY element), ']')
        """;

    private static string Flow(string extraColumn = "") => $$"""
        flowType: dimension
        name: edge-dimensions
        partitions: [dev, test]
        source:
          endpoint: http://localhost
          aggregationSize: 50
        target:
          connection: ${env:{{TargetVariable}}}
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 2 }
        dimensions:
          - name: EdgeLog
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            columns: { key: WellLogID, value: WellLogCode }
            attributes:
              Name: data.Name
          - name: EdgeAssurance
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            columns: { key: WellLogID, value: WellLogCode }
            elements:
              path: data.TechnicalAssurances
              fields:
                TypeID: { path: TechnicalAssuranceTypeID, keep: id }
                Note: { path: Note, trim: false }
          - name: EdgeType
            kind: "osdu:wks:reference-data--TechnicalAssuranceType:*"
            path: id
            columns: { key: TypeID, value: TypeCode }
            attributes:
              Code: { path: data.Code, trim: false }
              Name: { path: data.Name, trim: false }
        views:
          - name: edge_assurances
            from: EdgeAssurance
            where: element IS NOT NULL
            groupBy: WellLogID
            join:
              - { on: TypeID, to: EdgeType, as: Type }
            columns:
              WellLogID: WellLogID
              Json: { expression: "{{Json.ReplaceLineEndings(" ").Replace("\"", "\\\"", StringComparison.Ordinal)}}", dataType: nvarchar(max) }
              Count: COUNT(*)
              Typed: COUNT(Type.Code)
              MaxName: MAX(Type.Name)
              MinName: MIN(Type.Name)
              Notes: STRING_AGG(Note, '|') WITHIN GROUP (ORDER BY element ASC)
              FirstElement: { expression: MIN(element), dataType: int }
              Bad: { expression: MAX(Note), dataType: float }
              {{extraColumn}}
          - name: edge_logs
            from: EdgeLog
            join:
              - { on: WellLogID, view: edge_assurances, as: TA }
            columns:
              Name: Name
              Json: TA.Json
              Count: TA.Count
              Greatest: TA.MaxName
        """;

    /// <summary>
    /// Types whose code and name hold what JSON escapes (a quote, a backslash, a new line, a character past U+FFFF); a log of
    /// three assurances, one of a type the partition does not hold and one with a note of white space at its ends; a log of
    /// none, one of an empty list, one whose list holds a null; and a log of 300 assurances, more text than 8,000 bytes.
    /// </summary>
    private void Estate()
    {
        _platform.Add("dev:reference-data--TechnicalAssuranceType:T1", AssuranceType, new JsonObject { ["Code"] = "Ce\"rt\\ified", ["Name"] = Replacement });
        _platform.Add("dev:reference-data--TechnicalAssuranceType:T2", AssuranceType, new JsonObject { ["Code"] = "Trusted", ["Name"] = Emoji + "\nsecond line" });
        _platform.Add("dev:reference-data--TechnicalAssuranceType:T3", AssuranceType, new JsonObject { ["Name"] = "Nameless code" });
        JsonObject Assurance(string type, string? note = null)
        {
            var assurance = new JsonObject { ["TechnicalAssuranceTypeID"] = $"dev:reference-data--TechnicalAssuranceType:{type}:" };
            if (note is not null)
            {
                assurance["Note"] = note;
            }

            return assurance;
        }

        _platform.Add("dev:work-product-component--WellLog:L1", WellLog, new JsonObject
        {
            ["Name"] = "L1",
            ["TechnicalAssurances"] = new JsonArray(Assurance("T1"), Assurance("T2", "  spaced  "), Assurance("TX"), Assurance("T3")),
        });
        _platform.Add("dev:work-product-component--WellLog:L2", WellLog, new JsonObject { ["Name"] = "L2" });
        _platform.Add("dev:work-product-component--WellLog:L3", WellLog, new JsonObject { ["Name"] = "L3", ["TechnicalAssurances"] = new JsonArray() });
        _platform.Add("dev:work-product-component--WellLog:L4", WellLog, new JsonObject { ["Name"] = "L4", ["TechnicalAssurances"] = new JsonArray((JsonNode?)null) });
        var many = new JsonArray();
        for (var i = 0; i < 300; i++)
        {
            many.Add(Assurance("T1"));
        }

        _platform.Add("dev:work-product-component--WellLog:L5", WellLog, new JsonObject { ["Name"] = "L5", ["TechnicalAssurances"] = many });
    }

    private async Task<(DimensionRunner Runner, OsduLedger Ledger)> RunnerAsync(string yaml, string partition = "dev")
    {
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseDimension(yaml, "flows/edge.yaml").ForPartition(partition);
        return (new DimensionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<DimensionRunner>(), _platform, allowLoopback: true), ledger);
    }

    [Fact]
    public async Task A_grouped_view_writes_a_JSON_array_per_group_and_a_view_joining_it_meets_one_row_of_each_key()
    {
        Estate();
        var (runner, ledger) = await RunnerAsync(Flow());

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((3, 0), (outcome.Built, outcome.Failed));
        Assert.Equal(
            [("edge_assurances", DimensionViewWriteStatus.Written, DimensionViewCheckStatus.Passed, 2L), ("edge_logs", DimensionViewWriteStatus.Written, DimensionViewCheckStatus.Passed, 5L)],
            outcome.Views.Select(v => (v.View, v.Status, v.Check!, v.Rows!.Value)));

        // The array holds each assurance in the order the log holds them, every text escaped as JSON escapes it, a field of
        // no value left out, an assurance of a type the partition does not hold an empty object. The greatest name is the
        // one past U+FFFF, by code point, though SQL Server's binary order puts U+FFFD after it.
        Assert.Equal(
            ["1|4|2|Ce\"rt\\ified|" + Replacement + "|Trusted|" + Emoji + "\nsecond line|{}|Nameless code|" + Emoji + "\nsecond line|Nameless code|  spaced  |1"],
            await SqlAsync("""
                SELECT ISJSON(a.[Json]), a.[Count], a.[Typed], JSON_VALUE(a.[Json], '$[0].code'), JSON_VALUE(a.[Json], '$[0].name'),
                       JSON_VALUE(a.[Json], '$[1].code'), JSON_VALUE(a.[Json], '$[1].name'), JSON_QUERY(a.[Json], '$[2]'), JSON_VALUE(a.[Json], '$[3].name'),
                       a.[MaxName], a.[MinName], a.[Notes], a.[FirstElement]
                FROM [osdu].[v_dim_edge_assurances] AS a
                WHERE a.[partition] = N'dev' AND a.[WellLogID] = N'dev:work-product-component--WellLog:L1';
                """));
        Assert.Equal(
            ["{\"name\":\"Nameless code\"}"],
            await SqlAsync("SELECT JSON_QUERY(a.[Json], '$[3]') FROM [osdu].[v_dim_edge_assurances] AS a WHERE a.[WellLogID] = N'dev:work-product-component--WellLog:L1';"));

        // Three hundred assurances are aggregated whole, far past the 8,000 bytes a text of a bounded type holds.
        Assert.Equal(["1|300|300"], await SqlAsync("""
            SELECT ISJSON(a.[Json]), (SELECT COUNT(*) FROM OPENJSON(a.[Json])), a.[Count]
            FROM [osdu].[v_dim_edge_assurances] AS a WHERE a.[partition] = N'dev' AND a.[WellLogID] = N'dev:work-product-component--WellLog:L5';
            """));
        Assert.True(long.Parse((await SqlAsync("SELECT LEN(a.[Json]) FROM [osdu].[v_dim_edge_assurances] AS a WHERE a.[WellLogID] = N'dev:work-product-component--WellLog:L5';"))[0], System.Globalization.CultureInfo.InvariantCulture) > 8000);

        // A log of no assurance, of an empty list or of a list of a null alone is a row of the joining view with nothing
        // joined; every log is one row of it, however many assurances its group holds.
        Assert.Equal(
            ["L1|4|" + Emoji + "\nsecond line", "L2|(null)|(null)", "L3|(null)|(null)", "L4|(null)|(null)", "L5|300|" + Replacement],
            await SqlAsync("SELECT l.[Name], l.[Count], l.[Greatest] FROM [osdu].[v_dim_edge_logs] AS l WHERE l.[partition] = N'dev' ORDER BY l.[Name];"));

        // The check counted the groups, the join over the rows it groups (one names a type the partition does not hold),
        // and a conversion of an aggregate that cannot read.
        var assurances = (await ledger.GetDimensionViewAsync("edge_assurances"))!;
        var check = assurances.Checks[0];
        Assert.Equal(2L, check.Rows);
        Assert.Equal([("Type", 303L, 1L)], check.Joins.Select(j => (j.Alias, j.Matched, j.Unmatched)));
        Assert.Equal(["dev:reference-data--TechnicalAssuranceType:TX"], check.Joins[0].Examples);
        Assert.Equal((long?)1L, check.Columns.Single(c => c.Name == "Bad").Unconverted);
        Assert.Equal("  spaced  ", Assert.Single(check.Columns.Single(c => c.Name == "Bad").Examples).Value);
        Assert.Equal((long?)0L, check.Columns.Single(c => c.Name == "FirstElement").Unconverted);
        Assert.Equal(2L, check.Columns.Single(c => c.Name == "Json").Values);
        Assert.Equal(["v_dim_edge_assurances"], (await ledger.GetDimensionViewAsync("edge_logs"))!.View.Joins.Select(j => j.Table));
    }

    [Fact]
    public async Task Each_partition_is_grouped_apart_and_a_join_to_a_view_meets_its_own_partition_s_row()
    {
        Estate();
        var (dev, _) = await RunnerAsync(Flow());
        var (test, _) = await RunnerAsync(Flow(), "test");

        Assert.Equal(0, (await dev.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Failed);
        Assert.Equal(0, (await test.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Failed);

        Assert.Equal(
            ["dev|4", "test|4"],
            await SqlAsync("SELECT a.[partition], a.[Count] FROM [osdu].[v_dim_edge_assurances] AS a WHERE a.[WellLogID] LIKE N'%:L1' ORDER BY a.[partition];"));
        Assert.Equal(
            ["dev|4", "test|4"],
            await SqlAsync("SELECT l.[partition], l.[Count] FROM [osdu].[v_dim_edge_logs] AS l WHERE l.[Name] = N'L1' ORDER BY l.[partition];"));
        Assert.Equal(["10"], await SqlAsync("SELECT COUNT(*) FROM [osdu].[v_dim_edge_logs];"));
    }

    [Fact]
    public async Task A_view_joining_a_view_the_build_could_not_write_is_not_written_and_both_are_once_it_can()
    {
        Estate();

        // A table no build made holds the joined view's name. The test database empties such a table between tests but does
        // not drop it, so the test drops it whatever happens.
        await SqlAsync("CREATE TABLE [osdu].[v_dim_edge_assurances] ([x] int NULL); SELECT 1;");
        try
        {
            var (runner, _) = await RunnerAsync(Flow());

            // A view that cannot be written fails the run, so the pipelines ordered after the flow do not read it.
            var refused = (await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None))).Outcome;

            Assert.Equal(
                [("edge_assurances", DimensionViewWriteStatus.Failed), ("edge_logs", DimensionViewWriteStatus.Failed)],
                refused.Views.Select(v => (v.View, v.Status)));
            Assert.Contains("no build of a dimension flow made it", refused.Views[0].Error, StringComparison.Ordinal);
            Assert.Contains("which this build could not write, so it was not written either and keeps its last definition", refused.Views[1].Error, StringComparison.Ordinal);
            Assert.Equal(["(null)"], await SqlAsync("SELECT OBJECT_ID(N'[osdu].[v_dim_edge_logs]', N'V');"));

            await SqlAsync("DROP TABLE [osdu].[v_dim_edge_assurances]; SELECT 1;");
            var written = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
            Assert.Equal(
                [("edge_assurances", DimensionViewWriteStatus.Written), ("edge_logs", DimensionViewWriteStatus.Written)],
                written.Views.Select(v => (v.View, v.Status)));
        }
        finally
        {
            await SqlAsync("IF OBJECT_ID(N'[osdu].[v_dim_edge_assurances]', N'U') IS NOT NULL DROP TABLE [osdu].[v_dim_edge_assurances]; SELECT 1;");
        }
    }

    [Fact]
    public async Task A_view_is_written_again_when_a_view_it_joins_changes_and_left_alone_when_nothing_did()
    {
        Estate();
        var (first, _) = await RunnerAsync(Flow());
        Assert.Equal(0, (await first.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Failed);

        var (again, _) = await RunnerAsync(Flow());
        var unchanged = await again.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.All(unchanged.Views, v => Assert.Equal(DimensionViewWriteStatus.Unchanged, v.Status));

        // A column added to the joined view leaves the joining view's statement as it was, and both are written again, so
        // SQL Server reads the columns of the view joined anew.
        var (changed, _) = await RunnerAsync(Flow("Distinct: COUNT(DISTINCT TypeID)"));
        var rewritten = await changed.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.All(rewritten.Views, v => Assert.Equal(DimensionViewWriteStatus.Written, v.Status));
        Assert.Equal(["4"], await SqlAsync("SELECT a.[Distinct] FROM [osdu].[v_dim_edge_assurances] AS a WHERE a.[partition] = N'dev' AND a.[WellLogID] LIKE N'%:L1';"));
    }

    private async Task<List<string>> SqlAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "(null)" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }
}
