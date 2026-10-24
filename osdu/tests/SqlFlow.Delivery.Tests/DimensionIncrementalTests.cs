using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dimension flow's incremental loads (osdu/docs/reference/flow/dimension.md, Full and incremental loads) end to end,
/// on a stand-in search whose records carry OSDU's own <c>createTime</c> and <c>modifyTime</c> and a ledger on SQL
/// Server: the first build loads in full and keeps the keys each record holds by its unique key; a later one reads the
/// records that changed in its window, reads again the keys they hold now and held before and the keys read through a
/// record that changed, removes a key no record holds, and leaves every other key as it was; a record that left the
/// index stays until a full load.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DimensionIncrementalTests : IDisposable
{
    private const string WellLog = Samples.WellLogKind;

    private const string Wellbore = Samples.WellboreKind;

    private const string Head = """
        flowType: dimension
        name: wells-dimensions
        partitions: [dev]
        source:
          endpoint: http://localhost
          aggregationSize: 50
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 2 }

        """;

    private const string Incremental = """
        incremental:
          keyColumns: [id]

        """;

    private const string Wellbores = """
        dimensions:
          - name: Wellbore
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.WellboreID
            label: data.FacilityName
            attributes:
              Country: [data.CountryID, data.Name]
        """;

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { AggregationSize = 50 };

    public void Dispose()
    {
        _db.Dispose();
        _platform.Dispose();
    }

    private static DateTime At(int hour, int minute = 0) => new(2026, 9, 7, hour, minute, 0, DateTimeKind.Utc);

    private async Task<(DimensionRunner Runner, OsduLedger Ledger, DimensionFlowDefinition Flow)> RunnerAsync(string yaml)
    {
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseDimension(yaml, "flows/dims.yaml").ForPartition("dev");
        return (new DimensionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<DimensionRunner>(), _platform, allowLoopback: true), ledger, flow);
    }

    private JsonObject Record(string id, string kind, JsonObject data, DateTime created)
    {
        var record = _platform.Add(id, kind, data);
        record["createTime"] = OsduSearch.LuceneTime(created);
        return record;
    }

    private void Country(string code, string name, DateTime created)
        => Record($"dev:master-data--GeoPoliticalEntity:{code}", "osdu:wks:master-data--GeoPoliticalEntity:1.0.0", new JsonObject { ["Name"] = name }, created);

    private void WellboreRecord(string code, string name, string country, DateTime created)
        => Record($"dev:master-data--Wellbore:{code}", Wellbore,
            new JsonObject { ["FacilityName"] = name, ["CountryID"] = $"dev:master-data--GeoPoliticalEntity:{country}:" }, created);

    private void Log(string id, string wellbore, DateTime created)
        => Record($"dev:work-product-component--WellLog:{id}", WellLog, new JsonObject { ["WellboreID"] = $"dev:master-data--Wellbore:{wellbore}:" }, created);

    /// <summary>A record changed in OSDU: a new version, its data changed and its modifyTime set.</summary>
    private void Change(string id, DateTime at, Action<JsonObject> change)
    {
        var record = _platform.Records.Single(r => r["id"]!.GetValue<string>() == id);
        change(record["data"]!.AsObject());
        record["modifyTime"] = OsduSearch.LuceneTime(at);
    }

    /// <summary>Two countries, three wellbores (one no log names), and logs of two of them, all created at 11:00.</summary>
    private void Estate()
    {
        Country("NO", "Norway", At(11));
        Country("MX", "Mexico", At(11));
        WellboreRecord("A", "A-1", "NO", At(11));
        WellboreRecord("B", "B-1", "NO", At(11));
        WellboreRecord("C", "C-1", "MX", At(11));
        Log("1", "A", At(11));
        Log("2", "A", At(11));
        Log("3", "B", At(11));
    }

    private static async Task<Dictionary<string, DimensionValueState>> KeysAsync(OsduLedger ledger, int dimensionId)
        => (await ledger.ListDimensionValuesAsync(dimensionId, new DimensionValueQuery(null, null, false, true, null, 100)))
            .ToDictionary(k => k.Original, StringComparer.Ordinal);

    private static string Key(string wellbore) => $"dev:master-data--Wellbore:{wellbore}:";

    [Fact]
    public async Task The_first_build_loads_in_full_and_a_later_one_reads_again_only_the_keys_of_the_records_that_changed()
    {
        Estate();
        var (runner, ledger, flow) = await RunnerAsync(Head + Incremental + Wellbores);

        var first = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);

        Assert.Equal((DimensionRunModes.Full, At(11, 55)), (first.Load, first.WindowTo));
        Assert.Contains(first.Notes, n => n.StartsWith("Loaded in full, since it has not been loaded in this partition yet", StringComparison.Ordinal));
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var full = Assert.Single(await ledger.ListDimensionRunsAsync(dimension.DimensionId, 10));
        Assert.Equal("id", full.Read.RecordKey);

        // At 12:30 log 3 moves from wellbore B to C, and log 4 of wellbore A is created.
        Change("dev:work-product-component--WellLog:3", At(12, 30), data => data["WellboreID"] = Key("C"));
        Log("4", "A", At(12, 30));
        _clock.Advance(TimeSpan.FromHours(1));
        _platform.Calls.Clear();

        var second = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);

        // The window runs from a lag before where the first build read up to, to the build's start less the lag: two records
        // changed, and the keys they hold now (A, C) and held before (B) are read again.
        Assert.Equal(
            (DimensionRunModes.Incremental, (DateTime?)At(11, 50), (DateTime?)At(12, 55), (long?)2L, (long?)3L),
            (second.Load, second.WindowFrom, second.WindowTo, second.ChangedRecords, second.TouchedKeys));
        var keys = await KeysAsync(ledger, dimension.DimensionId);
        Assert.Equal((3L, "A-1", (long?)null), (keys[Key("A")].Count, keys[Key("A")].MemberValue, keys[Key("A")].RemovedRunId));
        Assert.Equal((1L, "C-1"), (keys[Key("C")].Count, keys[Key("C")].MemberValue));
        Assert.Equal("Mexico", Assert.Single(keys[Key("C")].Attributes).Value);

        // B's only log left it: read again, it is held by no record and is removed, and its value with it.
        Assert.NotNull(keys[Key("B")].RemovedRunId);
        Assert.Equal((1L, 1L), (second.Changes.KeysAdded, second.Changes.KeysRemoved));
        var changes = await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(second.BuildId, null, null, null, null, 10));
        Assert.Equal(
            [(Key("B"), DimensionChangeKinds.Removed), (Key("C"), DimensionChangeKinds.Added)],
            changes.Select(c => (c.Original, c.Change)).Order());
        var values = (await ledger.ListDimensionMembersAsync(dimension.DimensionId, new DimensionMemberQuery(null, false, null, 10))).Select(m => m.Value);
        Assert.Equal(["A-1", "C-1"], values);

        // Every value the incremental load read was narrowed to the window or to the keys it read again: no read of the
        // whole kind.
        foreach (var call in _platform.Calls.Where(c => c.Body is not null))
        {
            var body = JsonNode.Parse(call.Body!)!.AsObject();
            // The kinds the pattern matches are asked once, to read their templates by: a count of kinds, not of values.
            var kindsOnly = body["aggregateBy"]?.GetValue<string>() == "kind";
            if (!kindsOnly && (body["aggregateBy"] is not null || call.Uri.AbsolutePath.EndsWith("query_with_cursor", StringComparison.Ordinal) && body["cursor"] is null))
            {
                var query = body["query"]?.GetValue<string>() ?? string.Empty;
                Assert.True(query.Contains("modifyTime", StringComparison.Ordinal) || query.Contains("WellboreID", StringComparison.Ordinal) || query.Contains("id:", StringComparison.Ordinal), query);
            }
        }

        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_key_whose_label_or_attribute_was_read_through_a_record_that_changed_is_read_again()
    {
        Estate();
        var (runner, ledger, flow) = await RunnerAsync(Head + Incremental + Wellbores);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        // Wellbore A is renamed, and Norway, which B's attribute was read through, too: no log changed.
        Change("dev:master-data--Wellbore:A", At(12, 30), data => data["FacilityName"] = "A-renamed");
        Change("dev:master-data--GeoPoliticalEntity:NO", At(12, 40), data => data["Name"] = "Norge");
        _clock.Advance(TimeSpan.FromHours(1));

        var second = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);

        Assert.Equal((DimensionRunModes.Incremental, (long?)0L, (long?)2L), (second.Load, second.ChangedRecords, second.TouchedKeys));
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = await KeysAsync(ledger, dimension.DimensionId);
        Assert.Equal(("A-renamed", "A-renamed", "Norge"), (keys[Key("A")].Label, keys[Key("A")].MemberValue, Assert.Single(keys[Key("A")].Attributes).Value));
        Assert.Equal("Norge", Assert.Single(keys[Key("B")].Attributes).Value);
        Assert.Equal(2L, keys[Key("A")].Count);

        // The value A had is left with no key and is removed; the key moved to its new one.
        Assert.Equal(["A-renamed", "B-1"], (await ledger.ListDimensionMembersAsync(dimension.DimensionId, new DimensionMemberQuery(null, false, null, 10))).Select(m => m.Value));
        Assert.Equal((0L, 1L), (second.Changes.KeysRemoved, second.Changes.KeysMoved));
    }

    [Fact]
    public async Task A_record_that_left_the_index_is_kept_until_a_full_load_which_a_run_asks_for_or_the_flow_s_age_says()
    {
        Estate();
        var (runner, ledger, flow) = await RunnerAsync(Head + Incremental.Replace("keyColumns: [id]", "keyColumns: [id]\n  fullLoadAfterHours: 24", StringComparison.Ordinal) + Wellbores);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        // Logs 2 and 3 are deleted: the search no longer holds them, and nothing in the index says they were.
        _platform.Records.RemoveAll(r => r["id"]!.GetValue<string>() is "dev:work-product-component--WellLog:2" or "dev:work-product-component--WellLog:3");
        _clock.Advance(TimeSpan.FromHours(1));

        var second = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);

        Assert.Equal((DimensionRunModes.Incremental, DimensionRunStatus.Completed, (long?)0L), (second.Load, second.Status, second.TouchedKeys));
        Assert.Contains(second.Notes, n => n.StartsWith("At least 2 record(s) the last full load read", StringComparison.Ordinal));
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        var keys = await KeysAsync(ledger, dimension.DimensionId);
        Assert.Equal((2L, 1L, (long?)null), (keys[Key("A")].Count, keys[Key("B")].Count, keys[Key("B")].RemovedRunId));

        // A run asking for a full load reads every key: B goes and A counts one log.
        var asked = Assert.Single((await runner.BuildAsync([], new DimensionLoadRequest(FullLoad: true), Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);
        Assert.Equal(DimensionRunModes.Full, asked.Load);
        Assert.Contains(asked.Notes, n => n.StartsWith("Loaded in full, since the run asked for a full load (fullLoad)", StringComparison.Ordinal));
        keys = await KeysAsync(ledger, dimension.DimensionId);
        Assert.Equal((1L, true), (keys[Key("A")].Count, keys[Key("B")].RemovedRunId is not null));

        // A day later the flow's own age loads it in full again.
        _clock.Advance(TimeSpan.FromHours(25));
        var aged = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);
        Assert.Equal(DimensionRunModes.Full, aged.Load);
        Assert.Contains(aged.Notes, n => n.Contains("more than the 24 incremental.fullLoadAfterHours allows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_backfill_window_reads_what_changed_in_it_the_watermark_never_moves_back_and_a_flow_loading_in_full_refuses_one()
    {
        Estate();
        var (runner, ledger, flow) = await RunnerAsync(Head + Incremental + Wellbores);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Log("4", "C", At(12, 30));
        _clock.Advance(TimeSpan.FromHours(1));

        var before = Assert.Single((await runner.BuildAsync([], new DimensionLoadRequest(From: At(12), To: At(12, 20)), Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);
        Assert.Equal(((long?)0L, (DateTime?)At(12), (DateTime?)At(12, 20)), (before.ChangedRecords, before.WindowFrom, before.WindowTo));
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.False((await KeysAsync(ledger, dimension.DimensionId)).ContainsKey(Key("C")));

        var within = Assert.Single((await runner.BuildAsync([], new DimensionLoadRequest(From: At(12, 20), To: At(12, 40)), Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);
        Assert.Equal((long?)1L, within.ChangedRecords);
        Assert.Equal(1L, (await KeysAsync(ledger, dimension.DimensionId))[Key("C")].Count);

        // The next build reads on from the latest any build read up to, less the lag; and a window named for the past
        // afterwards moves nothing back.
        Assert.Equal(At(12, 40), await ledger.DimensionReadUpToAsync(dimension.DimensionId));
        var next = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);
        Assert.Equal(((DateTime?)At(12, 35), (DateTime?)At(12, 55)), (next.WindowFrom, next.WindowTo));
        await runner.BuildAsync([], new DimensionLoadRequest(From: At(12), To: At(12, 20)), Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(At(12, 55), await ledger.DimensionReadUpToAsync(dimension.DimensionId));

        var (fullOnly, _, _) = await RunnerAsync(Head + Wellbores);
        var refused = await Assert.ThrowsAsync<DeliveryException>(() => fullOnly.BuildAsync([], new DimensionLoadRequest(From: At(12)), Guid.NewGuid(), "tests", CancellationToken.None));
        Assert.Contains("declares no incremental block", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_window_in_which_most_records_changed_loads_in_full_and_the_plan_says_how_a_build_would_load()
    {
        for (var i = 0; i < 600; i++)
        {
            Log(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "W" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), At(11));
        }

        var (runner, _, _) = await RunnerAsync(Head + Incremental + """
            dimensions:
              - name: WellboreKey
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
            """);
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        foreach (var record in _platform.Records)
        {
            record["modifyTime"] = OsduSearch.LuceneTime(At(12, 30));
        }

        _clock.Advance(TimeSpan.FromHours(1));
        var plan = Assert.Single((await runner.PlanAsync([], CancellationToken.None)).Dimensions);
        Assert.Equal((DimensionRunModes.Incremental, (long?)600L, (DateTime?)At(11, 50)), (plan.Load, plan.ChangedRecords, plan.WindowFrom));

        var built = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);

        Assert.Equal(DimensionRunModes.Full, built.Load);
        Assert.Contains(built.Notes, n => n.StartsWith("Loaded in full, since 600 of the 600 record(s) its last full load read changed in the window", StringComparison.Ordinal));
        Assert.Equal(600, built.Keys);
    }

    [Fact]
    public async Task A_full_load_of_a_flow_that_loads_in_full_keeps_no_records_so_declaring_incremental_loads_in_full_once_more()
    {
        Estate();
        var (fullOnly, ledger, flow) = await RunnerAsync(Head + Wellbores);
        var first = Assert.Single((await fullOnly.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);
        Assert.Equal(DimensionRunModes.Full, first.Load);
        Assert.DoesNotContain(first.Notes, n => n.StartsWith("Loaded in full", StringComparison.Ordinal));

        _clock.Advance(TimeSpan.FromHours(1));
        var (runner, _, _) = await RunnerAsync(Head + Incremental + Wellbores);
        var second = Assert.Single((await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None)).Dimensions);

        Assert.Equal(DimensionRunModes.Full, second.Load);
        Assert.Contains(second.Notes, n => n.StartsWith("Loaded in full, since its last full load kept no record of the keys each record holds", StringComparison.Ordinal));
        var dimension = (await ledger.FindDimensionAsync(flow.LedgerId, "Wellbore"))!;
        Assert.Equal("id", (await ledger.ListDimensionRunsAsync(dimension.DimensionId, 1))[0].Read.RecordKey);
    }
}
