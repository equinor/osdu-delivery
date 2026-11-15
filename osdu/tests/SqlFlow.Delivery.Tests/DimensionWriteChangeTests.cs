using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dimension's write follows what changed, not the dimension (osdu/docs/wiki/decisions/0058-dimension-keys-by-their-text.md):
/// a key's objects and the attribute values read through its record are staged with their hash, so a full load whose records
/// did not change writes none of them and no row of the dimension's table, and one whose log changed rewrites that log's
/// rows alone. Its trace says each step of the write, and its notes keep what each took.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DimensionWriteChangeTests : IDisposable
{
    private const string WellLog = Samples.WellLogKind;

    private const string Wellbore = Samples.WellboreKind;

    private const string Flow = """
        flowType: dimension
        name: write-change-dimensions
        partitions: [dev]
        source:
          endpoint: http://localhost
          aggregationSize: 50
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 2 }
        incremental:
          keyColumns: [id]
          fullLoadAfterHours: 168

        dimensions:
          - name: LogCurve
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            label: data.Name
            columns: { key: WellLogID, value: WellLogName }
            attributes:
              WellboreName: [data.WellboreID, data.FacilityName]
            elements:
              path: data.Curves
              fields:
                Mnemonic: Mnemonic
                Unit: CurveUnit
                Owners: { path: acl.owners, up: 2, many: all }
        """;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { AggregationSize = 50 };

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    [Fact]
    public async Task A_full_load_writes_only_the_keys_whose_content_changed_and_its_trace_says_each_step()
    {
        for (var w = 0; w < 3; w++)
        {
            _platform.Add($"dev:master-data--Wellbore:W{w}", Wellbore, new JsonObject { ["FacilityName"] = $"Wellbore {w}" });
        }

        for (var log = 0; log < 12; log++)
        {
            var curves = new JsonArray();
            for (var curve = 0; curve < 4; curve++)
            {
                curves.Add(new JsonObject { ["Mnemonic"] = $"M{curve}", ["CurveUnit"] = curve == 0 ? "m" : "gAPI" });
            }

            var record = _platform.Add($"dev:work-product-component--WellLog:L{log:D2}", WellLog, new JsonObject
            {
                ["Name"] = $"Log {log}",
                ["WellboreID"] = $"dev:master-data--Wellbore:W{log % 3}:",
                ["Curves"] = curves,
            });
            record["acl"] = new JsonObject { ["owners"] = new JsonArray("data.default.owners@dev.example.com", $"data.log-{log % 2}.owners@dev.example.com") };
        }

        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseDimension(Flow, "flows/write-change.yaml").ForPartition("dev");
        async Task<List<string>> BuildAsync()
        {
            var log = new LinesLogger();
            await new DimensionRunner(engine, flow, new Dictionary<string, string>(), log, _platform, allowLoopback: true)
                .BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
            return log.Lines;
        }

        // The first build writes every key's objects and values, each key with its hashes, and every row of its table.
        var first = await BuildAsync();
        Assert.Contains(first, l => l.Contains("dimension LogCurve: merging took ", StringComparison.Ordinal)
            && l.Contains(": 12 key(s) arrived, 0 left, 0 moved and 0 came back; 12 attribute value(s) and 144 element field(s) added, rewritten or dropped", StringComparison.Ordinal));
        Assert.Contains(first, l => l.StartsWith("dimension LogCurve: writing its table: osdu.dim_LogCurve, every row", StringComparison.Ordinal));
        Assert.Contains(first, l => l.Contains("dimension LogCurve: writing its table took ", StringComparison.Ordinal) && l.EndsWith(": 48 row(s) added, rewritten or deleted", StringComparison.Ordinal));
        var hashes = await HashesAsync();
        Assert.Equal(12, hashes.Count);
        Assert.All(hashes.Values, h => Assert.True(h.Elements is not null && h.Attributes is not null));

        // A week on, nothing changed: the weekly full load reads every log again and writes none of its objects or values,
        // and no row of its table.
        _clock.Advance(TimeSpan.FromHours(169));
        var unchanged = await BuildAsync();
        Assert.Contains(unchanged, l => l.Contains("loading in full, since its last full load completed 169 hour(s) ago", StringComparison.Ordinal));
        Assert.Contains(unchanged, l => l.StartsWith("dimension LogCurve: deleting what earlier builds removed took ", StringComparison.Ordinal)
            && l.EndsWith(": 0 key(s) and 0 value(s) deleted", StringComparison.Ordinal));
        Assert.Contains(unchanged, l => l.Contains("dimension LogCurve: merging took ", StringComparison.Ordinal)
            && l.EndsWith(": 0 key(s) arrived, 0 left, 0 moved and 0 came back; 0 attribute value(s) and 0 element field(s) added, rewritten or dropped", StringComparison.Ordinal));
        Assert.Contains(unchanged, l => l.StartsWith("dimension LogCurve: writing its table: osdu.dim_LogCurve, the rows of the keys the write changed", StringComparison.Ordinal));
        Assert.Contains(unchanged, l => l.Contains("dimension LogCurve: writing its table took ", StringComparison.Ordinal) && l.EndsWith(": 0 row(s) added, rewritten or deleted", StringComparison.Ordinal));
        Assert.Equal(hashes, await HashesAsync());
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "LogCurve"))!;
        var build = (await ledger.ListDimensionRunsAsync(dimension.DimensionId, 1))[0];
        Assert.StartsWith("Written in ", build.Read.Notes[^1], StringComparison.Ordinal);
        Assert.Contains("; merging ", build.Read.Notes[^1], StringComparison.Ordinal);
        Assert.Contains("; writing its table ", build.Read.Notes[^1], StringComparison.Ordinal);

        // A curve of one log is renamed, and the wellbore four logs read their WellboreName through: the next full load writes
        // that log's objects again and the values of the logs naming that wellbore, and those keys' rows alone.
        var changed = _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:work-product-component--WellLog:L05");
        changed["data"]!["Curves"]![1]!["Mnemonic"] = "GR";
        _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:master-data--Wellbore:W0")["data"]!["FacilityName"] = "Wellbore zero";
        _clock.Advance(TimeSpan.FromHours(169));
        var some = await BuildAsync();

        // L05's 12 fields go and come back; the four logs on W0 (L00, L03, L06, L09) each lose and gain one value.
        Assert.Contains(some, l => l.Contains("dimension LogCurve: merging took ", StringComparison.Ordinal)
            && l.EndsWith(": 0 key(s) arrived, 0 left, 0 moved and 0 came back; 8 attribute value(s) and 24 element field(s) added, rewritten or dropped", StringComparison.Ordinal));
        var after = await HashesAsync();
        Assert.Equal(
            ["dev:work-product-component--WellLog:L05"],
            after.Where(h => h.Value.Elements != hashes[h.Key].Elements).Select(h => h.Key).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["dev:work-product-component--WellLog:L00", "dev:work-product-component--WellLog:L03", "dev:work-product-component--WellLog:L06", "dev:work-product-component--WellLog:L09"],
            after.Where(h => h.Value.Attributes != hashes[h.Key].Attributes).Select(h => h.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["GR"], await SqlAsync("SELECT [Mnemonic] FROM [osdu].[dim_LogCurve] WHERE [WellLogID] = N'dev:work-product-component--WellLog:L05' AND [element] = 2;"));
        Assert.Equal(
            ["dev:work-product-component--WellLog:L00|16", "dev:work-product-component--WellLog:L03|16", "dev:work-product-component--WellLog:L06|16", "dev:work-product-component--WellLog:L09|16"],
            await SqlAsync("SELECT [WellLogID], COUNT(*) * 4 FROM [osdu].[dim_LogCurve] WHERE [WellboreName] = N'Wellbore zero' GROUP BY [WellLogID] ORDER BY [WellLogID];"));

        // The table step compared the five keys' rows alone: L05's curve and the 16 rows of the four logs on W0 changed.
        Assert.Contains(some, l => l.Contains("dimension LogCurve: writing its table took ", StringComparison.Ordinal) && l.EndsWith(": 17 row(s) added, rewritten or deleted", StringComparison.Ordinal));
    }

    /// <summary>Each key's hashes, by its text, each as hex.</summary>
    private async Task<Dictionary<string, (string? Elements, string? Attributes)>> HashesAsync()
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT [Original], [ElementsHash], [AttributesHash] FROM [osdu].[DimensionValue] WHERE [RemovedRunId] IS NULL;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var found = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            found[reader.GetString(0)] = (
                reader.IsDBNull(1) ? null : Convert.ToHexString((byte[])reader[1]),
                reader.IsDBNull(2) ? null : Convert.ToHexString((byte[])reader[2]));
        }

        return found;
    }

    private async Task<List<string>> SqlAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "(null)" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    /// <summary>A run's log, each line as it reads.</summary>
    private sealed class LinesLogger : ILogger
    {
        private readonly Lock _gate = new();

        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            lock (_gate)
            {
                Lines.Add(formatter(state, exception));
            }
        }
    }
}
