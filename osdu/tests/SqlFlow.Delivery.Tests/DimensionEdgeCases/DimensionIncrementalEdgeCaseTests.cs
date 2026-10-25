using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An incremental load at the edges of the settings it takes now, on a stand-in search and a ledger on SQL Server: a
/// record the last full load read that left the index loads the dimension in full when the flow asks
/// (<c>fullLoadWhenRecordsLeave</c>), and only then; and a record a key's attributes were read through changes the key
/// in the kind the dimension looks that type up in, and a record of another kind of it does not.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
[Trait(DimensionEdgeCaseSuite.Trait, DimensionEdgeCaseSuite.Name)]
public sealed class DimensionIncrementalEdgeCaseTests : IDisposable
{
    private const string WellLog = Samples.WellLogKind;

    private const string Wellbore = Samples.WellboreKind;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { AggregationSize = 50 };

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    private static DateTime At(int hour, int minute = 0) => new(2026, 9, 7, hour, minute, 0, DateTimeKind.Utc);

    private static string Flow(string incremental, string lookupKinds = "") => $$"""
        flowType: dimension
        name: edge-incremental
        partitions: [dev]
        source:
          endpoint: http://localhost
          aggregationSize: 50
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 2 }
        incremental:
          keyColumns: [id]
          {{incremental}}
        dimensions:
          - name: Wellbore
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.WellboreID
            label: data.FacilityName
            attributes:
              Country: [data.CountryID, data.Name]
            {{lookupKinds}}
        """;

    private async Task<(DimensionRunner Runner, OsduLedger Ledger, DimensionFlowDefinition Flow)> RunnerAsync(string yaml)
    {
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseDimension(yaml, "flows/edge.yaml").ForPartition("dev");
        return (new DimensionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<DimensionRunner>(), _platform, allowLoopback: true), ledger, flow);
    }

    private JsonObject Record(string id, string kind, JsonObject data, DateTime created)
    {
        var record = _platform.Add(id, kind, data);
        record["createTime"] = OsduSearch.LuceneTime(created);
        return record;
    }

    /// <summary>Two countries, each in a kind of its own; two wellbores; a log of each wellbore and one more of A, all created at 11:00.</summary>
    private void Estate()
    {
        Record("dev:master-data--GeoPoliticalEntity:NO", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["Name"] = "Norway" }, At(11));
        Record("dev:master-data--GeoPoliticalEntity:MX", "osdu:wks:master-data--GeoPoliticalEntity:2.0.0", new JsonObject { ["Name"] = "Mexico" }, At(11));
        Record("dev:master-data--Wellbore:A", Wellbore, new JsonObject { ["FacilityName"] = "A-1", ["CountryID"] = "dev:master-data--GeoPoliticalEntity:NO:" }, At(11));
        Record("dev:master-data--Wellbore:B", Wellbore, new JsonObject { ["FacilityName"] = "B-1", ["CountryID"] = "dev:master-data--GeoPoliticalEntity:MX:" }, At(11));
        Record("dev:work-product-component--WellLog:1", WellLog, new JsonObject { ["WellboreID"] = "dev:master-data--Wellbore:A:" }, At(11));
        Record("dev:work-product-component--WellLog:2", WellLog, new JsonObject { ["WellboreID"] = "dev:master-data--Wellbore:A:" }, At(11));
        Record("dev:work-product-component--WellLog:3", WellLog, new JsonObject { ["WellboreID"] = "dev:master-data--Wellbore:B:" }, At(11));
    }

    private static async Task<Dictionary<string, DimensionValueState>> KeysAsync(OsduLedger ledger, int dimensionId)
        => (await ledger.ListDimensionValuesAsync(dimensionId, new DimensionValueQuery(null, null, false, true, null, 100)))
            .ToDictionary(k => k.Original, StringComparer.Ordinal);

    [Fact]
    public async Task A_record_that_left_the_index_loads_the_dimension_in_full_when_the_flow_asks_and_removes_the_keys_only_it_held()
    {
        Estate();
        var (runner, ledger, flow) = await RunnerAsync(Flow("fullLoadWhenRecordsLeave: true"));
        Assert.Equal(DimensionRunModes.Full, Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions).Load);

        // An hour on, no record changed: the build loads incrementally, as no record left either.
        _clock.Advance(TimeSpan.FromHours(1));
        var quiet = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);
        Assert.Equal(DimensionRunModes.Incremental, quiet.Load);

        // Log 3, wellbore B's only log, is soft deleted: it leaves the index, which no window sees.
        _platform.Records.RemoveAll(r => r["id"]!.GetValue<string>() == "dev:work-product-component--WellLog:3");
        _clock.Advance(TimeSpan.FromHours(1));
        var full = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);

        Assert.Equal(DimensionRunModes.Full, full.Load);
        Assert.Contains(full.Notes, n => n.Contains("at least 1 record(s) its last full load read are gone from the index or no longer match its query, and incremental.fullLoadWhenRecordsLeave asks for a full load then", StringComparison.Ordinal));
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = await KeysAsync(ledger, dimension.DimensionId);
        Assert.NotNull(keys["dev:master-data--Wellbore:B:"].RemovedRunId);
        Assert.Null(keys["dev:master-data--Wellbore:A:"].RemovedRunId);
    }

    [Fact]
    public async Task Without_the_setting_a_record_that_left_the_index_stays_until_the_next_full_load_and_is_said_so()
    {
        Estate();
        var (runner, ledger, flow) = await RunnerAsync(Flow(string.Empty));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        _platform.Records.RemoveAll(r => r["id"]!.GetValue<string>() == "dev:work-product-component--WellLog:3");
        _clock.Advance(TimeSpan.FromHours(1));
        var second = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);

        Assert.Equal(DimensionRunModes.Incremental, second.Load);
        Assert.Contains(second.Notes, n => n.Contains("At least 1 record(s) the last full load read", StringComparison.Ordinal));
        var keys = await KeysAsync(ledger, (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!.DimensionId);
        Assert.Null(keys["dev:master-data--Wellbore:B:"].RemovedRunId);
    }

    [Fact]
    public async Task A_key_is_read_again_for_a_changed_record_of_the_kind_its_type_is_looked_up_in_and_not_another()
    {
        Estate();
        var (runner, ledger, flow) = await RunnerAsync(Flow(string.Empty, "lookupKinds: [osdu:wks:master-data--GeoPoliticalEntity:1.0.0]"));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var first = await KeysAsync(ledger, dimension.DimensionId);

        // Mexico is of a kind the dimension does not look countries up in: wellbore B has no country at all.
        Assert.Equal("Norway", Assert.Single(first["dev:master-data--Wellbore:A:"].Attributes).Value);
        Assert.Empty(first["dev:master-data--Wellbore:B:"].Attributes);

        // Both countries are renamed at 12:30: the one of the kind looked up is read again, the other stays out of it.
        foreach (var (id, name) in new[] { ("dev:master-data--GeoPoliticalEntity:NO", "Kingdom of Norway"), ("dev:master-data--GeoPoliticalEntity:MX", "United Mexican States") })
        {
            var record = _platform.Records.Single(r => r["id"]!.GetValue<string>() == id);
            record["data"]!["Name"] = name;
            record["modifyTime"] = OsduSearch.LuceneTime(At(12, 30));
        }

        _clock.Advance(TimeSpan.FromHours(1));
        var second = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);

        Assert.Equal((DimensionRunModes.Incremental, (long?)1L), (second.Load, second.TouchedKeys));
        var keys = await KeysAsync(ledger, dimension.DimensionId);
        Assert.Equal("Kingdom of Norway", Assert.Single(keys["dev:master-data--Wellbore:A:"].Attributes).Value);
        Assert.Empty(keys["dev:master-data--Wellbore:B:"].Attributes);
    }
}
