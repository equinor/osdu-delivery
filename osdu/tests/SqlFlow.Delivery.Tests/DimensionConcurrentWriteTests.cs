using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A flow's dimensions building at once (osdu/docs/reference/concepts/database.md, How the tables are keyed): the dimension
/// tables keep each dimension's rows together and every statement of a build names its dimension, so a write reads and
/// locks its own rows alone. A dozen dimensions building at once, writing new keys, then changed, removed and new ones
/// beside the removal of a dimension the flow no longer declares, then restored ones, all complete; and no statement any
/// of them ran reached a dimension table's rows but through one dimension or the rows it names, which is what keeps one
/// build from waiting on, and deadlocking with, another's rows. A dimension's own table holds every partition it is built
/// in, and keeps each partition's rows together (osdu/docs/reference/flow/dimension-table.md): the flow built in two
/// partitions at once writes each partition's rows of it by seeks of that partition alone.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DimensionConcurrentWriteTests : IDisposable
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";

    private const string Wellbore = "osdu:wks:master-data--Wellbore:1.3.0";

    private const int Wellbores = 120;

    private const int Logs = 360;

    private const string Head = """
        flowType: dimension
        name: welldb-dimensions
        partitions: [dev, test]
        source:
          endpoint: http://localhost
          aggregationSize: 50
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 13 }
        dimensions:
          - name: Wellbore
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.WellboreID
            label: data.FacilityName
            attributes:
              Country: [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName]
              Source: { collect: data.Source }
          - name: WellboreName
            kind: "osdu:wks:master-data--Wellbore:*"
            path: data.FacilityName
            clean: [trim, upper]
          - name: Country
            kind: "osdu:wks:master-data--Wellbore:*"
            path: data.GeoContexts.GeoPoliticalEntityID
            label: data.GeoPoliticalEntityName
          - name: Operator
            kind: "osdu:wks:master-data--Wellbore:*"
            path: data.CurrentOperatorID
            label: data.OrganisationName
          - name: ServiceCompany
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.ServiceCompanyID
            label: data.OrganisationName
          - name: Mnemonic
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.Curves.Mnemonic
            clean: [trim, upper]
          - name: CurveUnit
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.Curves.CurveUnit
            label: data.Name
          - name: LogName
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.Name
          - name: LogSource
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.Source
          - name: LogWellbore
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.WellboreID
          - name: Log
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            label: data.Name
            columns: { key: WellLogID, value: WellLogName }
            attributes:
              Wellbore: data.WellboreID
              Company: [data.ServiceCompanyID, data.OrganisationName]
          - name: LogCurve
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            label: data.Name
            columns: { key: WellLogID, value: WellLogName }
            elements:
              path: data.Curves
              fields:
                Mnemonic: Mnemonic
                CurveUnit: CurveUnit

        """;

    /// <summary>The dimension the first build declares and the later ones do not, removed while they build.</summary>
    private const string Retired = """
          - name: Retired
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.Source
            clean: [lower]

        """;

    /// <summary>The dimension tables a build or a removal writes, which hold the rows of every dimension of a partition.</summary>
    private static readonly HashSet<string> DimensionTables = new(StringComparer.Ordinal)
    {
        "[Dimension]", "[DimensionAttributeName]", "[DimensionMember]", "[DimensionValue]", "[DimensionAttribute]", "[DimensionElement]",
        "[DimensionCollectedText]", "[DimensionKeyRecord]", "[DimensionRecord]", "[DimensionChange]", "[DimensionRun]",
    };

    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { AggregationSize = 50 };

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    [Fact]
    public async Task A_flows_dimensions_build_at_once_and_each_completes_beside_the_others_and_a_removal()
    {
        var builds = await BuildThreeTimesAsync();

        Assert.All(builds, outcome => Assert.Equal(0, outcome.Failed));
        Assert.Equal([13, 12, 12], builds.Select(b => b.Built));
        Assert.All(builds.SelectMany(b => b.Dimensions), d => Assert.Equal(DimensionRunStatus.Completed, d.Status));

        // What each build wrote is what it read: the renamed wellbores, the logs that left, the new ones and those back.
        var ledger = _db.Ledger(_clock);
        var flow = Flow(retired: false);
        var names = (await ledger.FindDimensionAsync(flow.LedgerId, "WellboreName"))!;
        Assert.Equal((long)Wellbores, names.Members);
        Assert.Equal(Wellbores / 3L, builds[1].Dimensions.Single(d => d.Dimension == "WellboreName").Changes.ValuesAdded);
        var logs = (await ledger.FindDimensionAsync(flow.LedgerId, "Log"))!;
        Assert.Equal(Logs + 6L, logs.Originals);
        Assert.Equal(
            [((long)Logs, 0L, 0L), (6L, 6L, 0L), (0L, 0L, 6L)],
            builds.Select(b => b.Dimensions.Single(d => d.Dimension == "Log").Changes).Select(c => (c.KeysAdded, c.KeysRemoved, c.KeysRestored)));
        Assert.Null(await ledger.FindDimensionAsync(flow.LedgerId, "Retired"));
    }

    [SkippableFact]
    public async Task No_statement_of_a_build_reads_a_dimension_table_past_the_dimension_it_writes()
    {
        await ClearPlansAsync();

        var builds = await BuildThreeTimesAsync();
        Assert.All(builds, outcome => Assert.Equal(0, outcome.Failed));

        // Every plan SQL Server made for the statements the dimension store sends, which hold a write's locks from one
        // statement to the next (the writes and their table layouts, the settling of a table's columns, the removal),
        // reaches a table's rows through one dimension, whatever the tables held when it was made.
        var (statements, past) = await ReadsPastTheirDimensionAsync(_db.ConnectionString);
        Assert.True(statements > 20, $"the builds' statements are in the plan cache ({statements} found)");
        Assert.True(past.Count == 0, "Statements reading a dimension table past the dimension they write:\n" + string.Join("\n", past));
    }

    [SkippableFact]
    public async Task The_flow_built_in_two_partitions_at_once_writes_each_partitions_rows_of_a_table_by_seeks_of_that_partition()
    {
        await ClearPlansAsync();
        Records();
        var flows = new[] { Flow(retired: false, "dev"), Flow(retired: false, "test") };
        var first = await Task.WhenAll(flows.Select(f => RunnerAsync(_db.Ledger(_clock), f).BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)));

        // A wellbore renamed and a log gone: both partitions build again, at once, and rewrite and remove their own rows.
        var renamed = _platform.Records.First(r => r["kind"]!.GetValue<string>() == Wellbore)["data"]!.AsObject();
        renamed["FacilityName"] = renamed["FacilityName"]!.GetValue<string>() + " ST2";
        _platform.Records.Remove(_platform.Records.First(r => r["kind"]!.GetValue<string>() == WellLog));
        var second = await Task.WhenAll(flows.Select(f => RunnerAsync(_db.Ledger(_clock), f).BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)));

        Assert.All(first.Concat(second), outcome => Assert.Equal((12, 0), (outcome.Built, outcome.Failed)));
        string Rows(string partition) => string.Create(CultureInfo.InvariantCulture, $"{partition}|{Logs - 1}");
        Assert.Equal([Rows("dev"), Rows("test")], await SqlAsync("SELECT [partition], COUNT_BIG(*) FROM [osdu].[dim_Log] GROUP BY [partition] ORDER BY [partition];"));

        var (statements, past) = await ReadsPastTheirDimensionAsync(_db.ConnectionString);
        Assert.True(statements > 20, $"the builds' statements are in the plan cache ({statements} found)");
        Assert.True(past.Count == 0, "Statements reading a dimension table past the dimension they write:\n" + string.Join("\n", past));
    }

    /// <summary>
    /// Builds the flow three times, each with its dimensions building at once: thirteen dimensions over wellbores and their
    /// logs; then twelve, the thirteenth removed while they build, over wellbores renamed, logs gone and logs new; then the
    /// twelve again, with the logs that went back. The platform's records are made here.
    /// </summary>
    private async Task<IReadOnlyList<DimensionBuildOutcome>> BuildThreeTimesAsync()
    {
        Records();
        var ledger = _db.Ledger(_clock);
        var first = await RunnerAsync(ledger, Flow(retired: true)).BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        // Wellbores renamed, six logs gone (their keys removed) and six new.
        foreach (var record in _platform.Records.Where(r => r["kind"]!.GetValue<string>() == Wellbore).Take(Wellbores / 3))
        {
            var data = record["data"]!.AsObject();
            data["FacilityName"] = data["FacilityName"]!.GetValue<string>() + " ST2";
        }

        var gone = _platform.Records.Where(r => r["kind"]!.GetValue<string>() == WellLog).Take(6).ToList();
        foreach (var record in gone)
        {
            _platform.Records.Remove(record);
        }

        for (var log = Logs; log < Logs + 6; log++)
        {
            AddLog(log);
        }

        var flow = Flow(retired: false);
        var retired = (await ledger.FindDimensionAsync(flow.LedgerId, "Retired"))!;
        var building = RunnerAsync(ledger, flow).BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var removing = DimensionRemoval.RemoveAsync(_db.Ledger(_clock), retired, "tests", _clock, CancellationToken.None);
        var second = await building;
        await removing;

        _platform.Records.AddRange(gone);
        var third = await RunnerAsync(ledger, flow).BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        return [first, second, third];
    }

    private DimensionRunner RunnerAsync(OsduLedger ledger, DimensionFlowDefinition flow)
        => new(Samples.Engine(ledger, _clock), flow, new Dictionary<string, string>(), Samples.Logger<DimensionRunner>(), _platform, allowLoopback: true);

    private static DimensionFlowDefinition Flow(bool retired, string partition = "dev")
        => new DeliveryDocumentLoader().ParseDimension(Head + (retired ? Retired : string.Empty), "flows/welldb-dimensions.yaml").ForPartition(partition);

    /// <summary>Wellbores in three countries run by three operators, and three logs a wellbore, each holding four curves.</summary>
    private void Records()
    {
        foreach (var (code, name) in new[] { ("NO", "Norway"), ("MX", "Mexico"), ("BR", "Brazil") })
        {
            _platform.Add($"dev:master-data--GeoPoliticalEntity:{code}", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["GeoPoliticalEntityName"] = name });
        }

        for (var company = 0; company < 3; company++)
        {
            _platform.Add($"dev:master-data--Organisation:C{company}", "osdu:wks:master-data--Organisation:1.2.0", new JsonObject { ["OrganisationName"] = $"Company {company}" });
        }

        foreach (var (code, name) in new[] { ("m", "metre"), ("ft", "foot"), ("gAPI", "API gamma ray unit"), ("ohm.m", "ohm metre") })
        {
            _platform.Add($"dev:reference-data--UnitOfMeasure:{code}", "osdu:wks:reference-data--UnitOfMeasure:1.0.0", new JsonObject { ["Name"] = name, ["Code"] = code });
        }

        var countries = new[] { "NO", "MX", "BR" };
        for (var wellbore = 0; wellbore < Wellbores; wellbore++)
        {
            _platform.Add($"dev:master-data--Wellbore:W{wellbore:D4}", Wellbore, new JsonObject
            {
                ["FacilityName"] = $"Wellbore {wellbore:D4}",
                ["CurrentOperatorID"] = $"dev:master-data--Organisation:C{wellbore % 3}:",
                ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = $"dev:master-data--GeoPoliticalEntity:{countries[wellbore % 3]}:" }),
            });
        }

        for (var log = 0; log < Logs; log++)
        {
            AddLog(log);
        }
    }

    private void AddLog(int log)
    {
        var units = new[] { "m", "ft", "gAPI", "ohm.m" };
        var curves = new JsonArray();
        for (var curve = 0; curve < 4; curve++)
        {
            curves.Add(new JsonObject { ["Mnemonic"] = $"C{(log + curve) % 40:D2}", ["CurveUnit"] = $"dev:reference-data--UnitOfMeasure:{units[curve]}:" });
        }

        _platform.Add($"dev:work-product-component--WellLog:L{log:D4}", WellLog, new JsonObject
        {
            ["Name"] = $"Log {log:D4}",
            ["WellboreID"] = $"dev:master-data--Wellbore:W{log % Wellbores:D4}:",
            ["ServiceCompanyID"] = $"dev:master-data--Organisation:C{log % 3}:",
            ["Source"] = $"S{log % 12:D2}",
            ["Curves"] = curves,
        });
    }

    private async Task<List<string>> SqlAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    /// <summary>Empties the plans SQL Server keeps for the test database, so the plans read after the builds are theirs.</summary>
    private async Task ClearPlansAsync()
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;", connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The statements of the dimension store's batches (each of which names the seeks it reads by) that SQL Server holds
    /// plans for in this database and that read a dimension table, and those of them that read it past one dimension: a
    /// scan of an index, or a seek on the partition alone, either of which reads every dimension's rows of the partition,
    /// and so waits on the rows another build is writing while it holds its own. A seek naming the dimension, or a row by
    /// its number, reads only the rows it names. A dimension's own table (<c>dim_</c>) holds one dimension in every
    /// partition, so a statement over it reads past its partition when it scans, or seeks neither its partition nor a row
    /// by its id. A statement of one of the store's batches that names no seek is checked with the rest of its batch; a
    /// statement on its own reads the rows of one dimension at a time (the pages, the lookups), so it can wait on a write
    /// but never hold what the write waits for.
    /// </summary>
    private static async Task<(int Statements, List<string> Past)> ReadsPastTheirDimensionAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT SUBSTRING(st.[text], qs.[statement_start_offset] / 2 + 1,
                       ((CASE qs.[statement_end_offset] WHEN -1 THEN DATALENGTH(st.[text]) ELSE qs.[statement_end_offset] END)
                        - qs.[statement_start_offset]) / 2 + 1),
                   qp.[query_plan]
            FROM sys.dm_exec_query_stats AS qs
            CROSS APPLY sys.dm_exec_sql_text(qs.[sql_handle]) AS st
            CROSS APPLY sys.dm_exec_text_query_plan(qs.[plan_handle], qs.[statement_start_offset], qs.[statement_end_offset]) AS qp
            CROSS APPLY (SELECT CONVERT(int, pa.[value]) AS [dbid] FROM sys.dm_exec_plan_attributes(qs.[plan_handle]) AS pa WHERE pa.[attribute] = N'dbid') AS a
            WHERE a.[dbid] = DB_ID() AND (st.[text] LIKE N'%[[]osdu].[[]Dimension%' OR st.[text] LIKE N'%[[]osdu].[[]dim[_]%')
              AND st.[text] LIKE N'%WITH (FORCESEEK)%' AND qp.[query_plan] IS NOT NULL;
            """,
            connection);
        var statements = 0;
        var past = new List<string>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var statement = string.Join(' ', reader.GetString(0).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                var plan = XDocument.Parse(reader.GetString(1));
                var reads = plan.Descendants(Showplan + "IndexScan")
                    .Select(scan => (Scan: scan, Object: scan.Element(Showplan + "Object")))
                    .Where(s => s.Object?.Attribute("Schema")?.Value == "[osdu]" && (DimensionTables.Contains(s.Object.Attribute("Table")?.Value ?? string.Empty)
                        || (s.Object.Attribute("Table")?.Value ?? string.Empty).StartsWith("[dim_", StringComparison.Ordinal)))
                    .ToList();
                if (reads.Count == 0)
                {
                    continue;
                }

                statements++;
                foreach (var (scan, table) in reads)
                {
                    var seeks = scan.Elements(Showplan + "SeekPredicates").Descendants(Showplan + "SeekKeys").ToList();
                    var where = $"{table!.Attribute("Table")!.Value}.{table.Attribute("Index")?.Value}";
                    if (seeks.Count == 0)
                    {
                        past.Add($"{where} is scanned by: {Shown(statement)}");
                        continue;
                    }

                    var own = table.Attribute("Table")!.Value.StartsWith("[dim_", StringComparison.Ordinal);
                    foreach (var seek in seeks)
                    {
                        var named = seek.Elements(Showplan + "Prefix").Descendants(Showplan + "ColumnReference")
                            .Select(c => (c.Attribute("Column")?.Value ?? string.Empty).Trim('[', ']'))
                            .ToList();
                        if (own && !named.Any(c => c is "partition" or "id"))
                        {
                            past.Add($"{where} is sought by neither its partition nor a row's id by: {Shown(statement)}");
                        }
                        else if (!own && named.All(c => c == "PartitionId"))
                        {
                            past.Add($"{where} is sought by its partition alone by: {Shown(statement)}");
                        }
                    }
                }
            }
        }
        catch (SqlException ex) when (ex.Number is 297 or 300)
        {
            Skip.If(true, "Reading the plans SQL Server made needs VIEW SERVER PERFORMANCE STATE (or VIEW SERVER STATE): " + ex.Message);
            throw;
        }

        return (statements, past);
    }

    private static string Shown(string statement)
        => statement.Length <= 300 ? statement : string.Create(CultureInfo.InvariantCulture, $"{statement[..300]}... ({statement.Length} characters)");
}
