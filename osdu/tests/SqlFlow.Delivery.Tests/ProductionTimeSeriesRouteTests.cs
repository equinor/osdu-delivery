using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Protocols.Ddms;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The ddms route to the Production DDMS historian (osdu/specs/production-timeseries/INTEGRATION.md) against fakes of its
/// ingestion and query services built from the brief: the ProductionValues record through Storage with its link to its
/// points, the points split into requests under the body limit and sent once each, every accepted version read back
/// until the query service serves it, and whatever the ingestion service would refuse held before anything is sent.
/// </summary>
public sealed class ProductionTimeSeriesRouteTests
{
    private const string ValuesId = "dev:work-product-component--ProductionValues:pv-1";
    private const string ValuesKind = "osdu:wks:work-product-component--ProductionValues:2.0.0";
    private const string Well = "dev:master-data--Well:w-1:";
    private const string Records = "/api/storage/v2/records";
    private const string Ingest = FakeOsduPlatform.TimeSeriesRoot + "/production-values/" + ValuesId + "/timeseries";
    private const string Query = FakeOsduPlatform.TimeSeriesQueryRoot + "/production-values/" + ValuesId + "/timeseries/";
    private const long Day0 = 1_704_067_200_000;
    private const long Day = 86_400_000;
    private const long Minute = 60_000;
    private const long FirstVersion = 1_781_770_405_994;

    private static readonly (string Id, string Kind)[] Standard = [("OIL", "Double"), ("WELLS", "Integer"), ("FLAG", "Boolean")];

    private static readonly (string Name, Type Type)[] StandardColumns =
        [("timestamp", typeof(long)), ("OIL", typeof(double)), ("WELLS", typeof(long)), ("FLAG", typeof(bool))];

    /// <summary>The difference the brief records between the ingestion contract and its code (section 10, item 1): a point's time is <c>timestamp</c>.</summary>
    private static bool PointName(string violation) => violation.Contains("the required property 'point' is missing", StringComparison.Ordinal);

    private static TimeSeriesSettings Settings(int settle = 30, int poll = 1, long maxRequestBytes = TimeSeriesSettings.DefaultMaxRequestBytes)
        => new() { QueryRoot = FakeOsduPlatform.TimeSeriesQueryRoot, SettleSeconds = settle, PollSeconds = poll, MaxRequestBytesPerRequest = maxRequestBytes };

    private sealed class Rig : IDisposable
    {
        public Rig(FakeOsduPlatform platform, TimeSeriesSettings? settings = null, long ceiling = 0)
        {
            Runtime = new HttpRuntime(
                new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
                new SecretResolver([new EnvSecretProvider()]), new TestClock(), platform, allowLoopback: true);
            Client = new OsduHttpClient(
                Runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
            Options = new ProtocolOptions { WorkflowPollSeconds = 1, DatasetIndexWaitSeconds = 0 };
            var historian = new DdmsService("historian", FakeOsduPlatform.TimeSeriesRoot, DdmsShape.ProductionTimeSeriesV1, DdmsCatalog.TimeSeriesCollections)
            {
                TimeSeries = settings ?? Settings(),
            };
            Flow = Samples.Targeting(new FlowTarget { Endpoint = FakeOsduPlatform.Endpoint, Protocol = DeliveryProtocol.Ddms, Ddms = [historian], ProtocolOptions = Options }, "production");
            Protocol = new OsduDdmsProtocol(Client, Options, NullLogger.Instance, ceiling, Clock, DdmsRouting.Of(Flow));
        }

        public SteppingClock Clock { get; } = new();

        public HttpRuntime Runtime { get; }

        public OsduHttpClient Client { get; }

        public ProtocolOptions Options { get; }

        public FlowDefinition Flow { get; }

        public OsduDdmsProtocol Protocol { get; }

        public void Dispose() => Runtime.Dispose();
    }

    /// <summary>A clock that moves on by each wait it is asked for and ends the wait at once, so a read back loop runs without waiting.</summary>
    internal sealed class SteppingClock : TimeProvider
    {
        private long _now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        private long _waited;

        public TimeSpan Waited => TimeSpan.FromTicks(Interlocked.Read(ref _waited));

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _now), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Interlocked.Add(ref _now, dueTime.Ticks);
                Interlocked.Add(ref _waited, dueTime.Ticks);
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new Elapsed();
        }

        private sealed class Elapsed : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static JsonObject Values(params (string Id, string Kind)[] series) => Values(ValuesKind, Well, series);

    private static JsonObject Values(string kind, string? reportingEntity, params (string Id, string Kind)[] series)
    {
        var data = new JsonObject
        {
            ["Name"] = "Daily allocation",
            ["ProductionMetricValues"] = new JsonArray(series.Select(s => (JsonNode?)new JsonObject
            {
                ["DDMSDatasetID"] = s.Id,
                ["ParameterKindID"] = s.Kind.Contains(':', StringComparison.Ordinal) ? s.Kind : $"dev:reference-data--ParameterKind:{s.Kind}:",
                ["UnitOfMeasureID"] = "dev:reference-data--UnitOfMeasure:bbl%2Fd:",
            }).ToArray()),
        };
        if (reportingEntity is not null)
        {
            data["ReportingEntityID"] = reportingEntity;
        }

        return FakeOsduPlatform.Record(ValuesId, kind, data);
    }

    private static async Task<byte[]> TableAsync(IReadOnlyList<(string Name, Type Type)> columns, IEnumerable<object?[]> rows, IReadOnlyDictionary<string, string>? metadata = null)
    {
        using var buffer = new MemoryStream();
        var data = rows
            .Select(r => (IReadOnlyDictionary<string, object?>)columns.Select((c, i) => (c.Name, Value: r[i])).ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal))
            .ToList();
        await ParquetFiles.WriteAsync(buffer, columns, data, metadata);
        return buffer.ToArray();
    }

    /// <summary>Five days of the standard series; the oil value of the third day is empty.</summary>
    private static Task<byte[]> StandardTableAsync()
        => TableAsync(StandardColumns, Enumerable.Range(0, 5).Select(i => new object?[] { Day0 + (i * Day), i == 2 ? null : 100.5 + i, (long)i, i % 2 == 0 }));

    /// <summary>A points file in the ingestion service's own body.</summary>
    private static byte[] Series(params (string Id, IEnumerable<(long T, JsonNode? V)> Points)[] series)
        => Encoding.UTF8.GetBytes(new JsonObject
        {
            ["timeseries"] = new JsonArray(series.Select(s => (JsonNode?)new JsonObject
            {
                ["timeseriesId"] = s.Id,
                ["points"] = new JsonArray(s.Points.Select(p => (JsonNode?)new JsonObject { ["timestamp"] = p.T, ["value"] = p.V }).ToArray()),
            }).ToArray()),
        }.ToJsonString());

    private static RafsRouteTests.NamedFiles Files(params (string Name, byte[] Bytes)[] files) => new(files);

    private static DeliveryWork Work(
        JsonObject document, IPayloadSource? points = null, bool metadata = true, long? existing = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? completed = null,
        Dictionary<string, IReadOnlyDictionary<string, string>>? reported = null) => new()
        {
            Key = SqlFlow.Delivery.Identity.DeliveryKey.Derive("production", [document["id"]!.GetValue<string>()]),
            TargetId = document["id"]!.GetValue<string>(),
            Document = document,
            DeliverMetadata = metadata,
            DeliverPayload = points is not null,
            Payload = points,
            ExistingVersion = existing,
            CompletedSteps = completed ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal),
            StepCompleted = reported is null
                ? null
                : (report, _) =>
                {
                    reported[report.Step] = report.Returned;
                    return Task.CompletedTask;
                },
        };

    private static List<string> Sent(FakeOsduPlatform platform, int from = 0)
        => platform.Calls.Skip(from).Select(c => c.Method + " " + Uri.UnescapeDataString(c.Uri.PathAndQuery)).ToList();

    private static string Read(string series, long version, long start, long end)
        => string.Create(CultureInfo.InvariantCulture, $"GET {Query}{series}/versions/{version}?start={start}&end={end + 1}");

    private static string[] Links(JsonObject record) => record["data"]!["DDMSDatasets"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    [Theory]
    [InlineData("dev:reference-data--ParameterKind:Double:", "Double")]
    [InlineData("osdu:reference-data--ParameterKind:Integer:1", "Integer")]
    [InlineData("dev:reference-data--parameterkind:BOOLEAN:", "Boolean")]
    [InlineData("dev:reference-data--ParameterKind:String:", "String")]
    [InlineData("dev:reference-data--ParameterKind:Timestamp:", "Timestamp")]
    [InlineData("dev:reference-data--ParameterKind:set-string:", "SetString")]
    [InlineData("dev:reference-data--ParameterKind:SetString:", "SetString")]
    [InlineData("Double", "Double")]
    [InlineData("dev:reference-data--ParameterKind:Long:", "Unknown")]
    [InlineData("", "Unknown")]
    public void A_series_kind_is_read_from_the_code_its_parameter_kind_names(string parameterKind, string expected)
        => Assert.Equal(Enum.Parse<SeriesKind>(expected), SeriesDefinition.KindOf(parameterKind));

    [Fact]
    public async Task A_record_goes_through_storage_its_points_in_one_request_and_each_version_is_read_back()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        using var correlation = OsduCorrelation.ForRun("pdms", FlowTelemetry.Delivery, Guid.CreateVersion7());

        var outcome = await rig.Protocol.DeliverAsync(Work(Values(Standard), Files(("points.parquet", await StandardTableAsync()))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);

        var last = Day0 + (4 * Day);
        Assert.Equal(
            [
                "PUT " + Records,
                "POST " + Ingest,
                Read("OIL", FirstVersion, Day0, last),
                Read("WELLS", FirstVersion + 1, Day0, last),
                Read("FLAG", FirstVersion + 2, Day0, last),
            ],
            Sent(platform));

        // The request is typed exactly as the service's clients type it, and carries the run's correlation id twice.
        Assert.Equal("application/json", Assert.Single(platform.TimeSeriesContentTypes));
        Assert.Equal(correlation.Id, platform.Calls[1].Headers[ProductionTimeSeriesShape.TraceHeader]);
        Assert.Equal(correlation.Id, platform.Calls[1].Headers[OsduCorrelation.HeaderName]);
        Assert.Equal(correlation.Id, platform.Calls[2].Headers[ProductionTimeSeriesShape.TraceHeader]);

        // Every series once, in column order; an empty cell is no point; each value in its series' kind.
        var body = platform.Calls[1].Body!;
        Assert.StartsWith("{\"timeseries\":[{\"timeseriesId\":\"OIL\",\"points\":[{\"timestamp\":1704067200000,\"value\":100.5},{\"timestamp\":1704153600000,\"value\":101.5},", body, StringComparison.Ordinal);
        var series = JsonNode.Parse(body)!["timeseries"]!.AsArray();
        Assert.Equal(["OIL", "WELLS", "FLAG"], series.Select(s => s!["timeseriesId"]!.GetValue<string>()));
        Assert.Equal([100.5, 101.5, 103.5, 104.5], series[0]!["points"]!.AsArray().Select(p => p!["value"]!.GetValue<double>()));
        Assert.Equal(["0", "1", "2", "3", "4"], series[1]!["points"]!.AsArray().Select(p => p!["value"]!.ToJsonString()));
        Assert.Equal(["true", "false", "true", "false", "true"], series[2]!["points"]!.AsArray().Select(p => p!["value"]!.ToJsonString()));

        // The record is a storage record with its link to its points.
        var stored = platform.Records[ValuesId];
        Assert.Equal([ProductionTimeSeriesShape.Link(ValuesId)], Links(stored));
        Assert.Equal("urn://pddms/production-values/" + ValuesId + "/timeseries", ProductionTimeSeriesShape.Link(ValuesId));
        Assert.Equal(stored["version"]!.GetValue<long>(), outcome.TargetVersion);
        Assert.Equal(4, Assert.Single(platform.TimeSeries[(ValuesId, "OIL")]).Points.Count);

        Assert.Equal(["metadata", "points-1", "settle"], outcome.Steps.Select(s => s.Name));
        Assert.Equal(207, outcome.Steps[1].Status);
        Assert.Equal(1, outcome.ChunksSent);
        Assert.True(outcome.MetadataDelivered);
        Assert.True(outcome.PayloadDelivered);
        Assert.Equal("14 point(s) of 3 series in 1 request(s); read back through the query service", outcome.Detail);
        Assert.Equal("1", outcome.Returned["timeSeries.requests"]);
        Assert.Equal("14", outcome.Returned["timeSeries.points"]);
        Assert.Equal("read back through the query service", outcome.Returned["timeSeries.settled"]);
        Assert.Equal(FirstVersion.ToString(CultureInfo.InvariantCulture), outcome.Returned["timeSeries.OIL.versions"]);
        Assert.Equal("4", outcome.Returned["timeSeries.OIL.points"]);
        Assert.Equal(Day0.ToString(CultureInfo.InvariantCulture), outcome.Returned["timeSeries.OIL.start"]);
        Assert.Equal(last.ToString(CultureInfo.InvariantCulture), outcome.Returned["timeSeries.OIL.end"]);
        Assert.Equal((FirstVersion + 2).ToString(CultureInfo.InvariantCulture), outcome.Returned["timeSeries.FLAG.versions"]);
        Assert.Equal(TimeSpan.Zero, rig.Clock.Waited);

        Assert.Equal(
            [
                "core/storage PUT /records",
                "production-timeseries/ingestion POST /production-values/{recordId}/timeseries",
                "production-timeseries/query GET /production-values/{recordId}/timeseries/{timeseriesId}/versions/{version}",
            ],
            OsduContracts.AssertConform(platform.Calls, null, PointName, OsduContracts.Storage, OsduContracts.ProductionTimeSeriesIngestion, OsduContracts.ProductionTimeSeries));
    }

    [Fact]
    public async Task A_record_without_points_is_written_with_its_link_and_the_historian_is_not_called()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var document = Values(Standard);
        document["data"]!["DDMSDatasets"] = new JsonArray("urn://other/keep", "urn://pddms/production-values/dev:work-product-component--ProductionValues:other/timeseries");

        var outcome = await rig.Protocol.DeliverAsync(Work(document));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(["PUT " + Records], Sent(platform));
        Assert.Equal(["urn://other/keep", ProductionTimeSeriesShape.Link(ValuesId)], Links(platform.Records[ValuesId]));
        Assert.False(outcome.PayloadDelivered);
        Assert.Null(outcome.Detail);
        Assert.Equal(["metadata"], outcome.Steps.Select(s => s.Name));
    }

    [Fact]
    public async Task Points_go_in_requests_under_the_limit_and_a_later_try_sends_only_the_ones_no_try_recorded()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, Settings(maxRequestBytes: 10_000));
        var points = Files(("oil.json", Series(("OIL", Enumerable.Range(0, 600).Select(i => (Day0 + (i * Day), (JsonNode?)JsonValue.Create(1000.25 + i)))))));

        // The third request fails as the service fails when its storage lookup throws; the two before it are recorded.
        platform.TimeSeriesFailing.Add(3);
        var reported = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var failed = await Assert.ThrowsAsync<OsduStatusException>(() => rig.Protocol.DeliverAsync(Work(Values(Standard), points, reported: reported)));
        Assert.Equal(500, failed.StatusCode);
        Assert.Equal(["metadata", "points-1", "points-2"], reported.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(FirstVersion.ToString(CultureInfo.InvariantCulture), reported["points-1"]["OIL.version"]);
        Assert.Equal("226", reported["points-1"]["OIL.points"]);
        Assert.Equal(64, reported["points-1"][ProductionTimeSeriesShape.HashValue].Length);
        Assert.Equal(["PUT " + Records, "POST " + Ingest, "POST " + Ingest, "POST " + Ingest], Sent(platform));

        // 50 bytes of envelope and 44 per point: 226 points fill a request, and the last takes the 148 left.
        var bodies = platform.Calls.Where(c => c.Method == HttpMethod.Post).Select(c => Encoding.UTF8.GetByteCount(c.Body!)).ToList();
        Assert.Equal([9_994, 9_994, 6_562], bodies);
        Assert.Equal("9994", reported["points-1"]["bytes"]);

        // The next try resumes past what landed: only the third request is sent, and every request is read back.
        var calls = platform.Calls.Count;
        var again = new Dictionary<string, IReadOnlyDictionary<string, string>>(reported, StringComparer.Ordinal);
        var outcome = await rig.Protocol.DeliverAsync(Work(Values(Standard), points, completed: reported, reported: again));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(
            [
                "POST " + Ingest,
                Read("OIL", FirstVersion, Day0, Day0 + (225 * Day)),
                Read("OIL", FirstVersion + 1, Day0 + (226 * Day), Day0 + (451 * Day)),
                Read("OIL", FirstVersion + 2, Day0 + (452 * Day), Day0 + (599 * Day)),
            ],
            Sent(platform, calls));
        Assert.Equal(["metadata", "points-1", "points-2", "points-3", "settle"], outcome.Steps.Select(s => s.Name));
        Assert.Equal([true, true, true, false, false], outcome.Steps.Select(s => s.Resumed));
        Assert.Equal(1, outcome.ChunksSent);
        Assert.Equal("600 point(s) of 1 series in 3 request(s), 2 of them accepted on an earlier try; read back through the query service", outcome.Detail);
        Assert.Equal($"{FirstVersion},{FirstVersion + 1},{FirstVersion + 2}", outcome.Returned["timeSeries.OIL.versions"]);
        Assert.Equal("600", outcome.Returned["timeSeries.OIL.points"]);
        Assert.All(new[] { "points-1", "points-2", "points-3" }, step => Assert.Equal("true", again[step][ProductionTimeSeriesShape.SettledValue]));

        // What the historian holds is every point once, in order.
        var held = platform.TimeSeries[(ValuesId, "OIL")];
        Assert.Equal(3, held.Count);
        Assert.Equal(Enumerable.Range(0, 600).Select(i => Day0 + (i * Day)), held.SelectMany(v => v.Points.Keys));
        Assert.Equal("1000.25", held[0].Points[Day0]!.ToJsonString());

        // A try after that reads nothing back again: every request says it was served.
        calls = platform.Calls.Count;
        var third = await rig.Protocol.DeliverAsync(Work(Values(Standard), points, completed: again));
        Assert.True(third.Succeeded, third.Failure?.Message);
        Assert.Empty(Sent(platform, calls));
        Assert.Equal("600 point(s) of 1 series in 3 request(s), 3 of them accepted on an earlier try; read back through the query service on an earlier try", third.Detail);
    }

    [Fact]
    public async Task A_request_split_otherwise_than_the_one_a_try_recorded_is_sent_again()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var points = Files(("oil.json", Series(("OIL", [(Day0, JsonValue.Create(1.5))]))));
        var recorded = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            ["metadata"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = ValuesId, ["version"] = "7" },
            ["points-1"] = new Dictionary<string, string>(StringComparer.Ordinal) { [ProductionTimeSeriesShape.HashValue] = new string('0', 64), ["OIL.version"] = "5" },
        };
        platform.Put(Values(Standard));

        var outcome = await rig.Protocol.DeliverAsync(Work(Values(Standard), points, completed: recorded));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(["POST " + Ingest, Read("OIL", FirstVersion, Day0, Day0)], Sent(platform));
        Assert.Equal(FirstVersion.ToString(CultureInfo.InvariantCulture), outcome.Returned["timeSeries.OIL.versions"]);
        Assert.Equal(7, outcome.TargetVersion);
    }

    [Fact]
    public async Task A_version_the_query_service_does_not_serve_yet_is_read_again_until_it_does()
    {
        var platform = new FakeOsduPlatform { TimeSeriesMappingDelay = 2 };
        using var rig = new Rig(platform, Settings(settle: 60, poll: 5));
        var points = Files(("oil.json", Series(("OIL", [(Day0, JsonValue.Create(1.5)), (Day0 + Day, JsonValue.Create(2.5))]))));

        var outcome = await rig.Protocol.DeliverAsync(Work(Values(Standard), points));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var read = Read("OIL", FirstVersion, Day0, Day0 + Day);
        Assert.Equal(["PUT " + Records, "POST " + Ingest, read, read, read], Sent(platform));
        Assert.Equal(TimeSpan.FromSeconds(10), rig.Clock.Waited);
        var settle = outcome.Steps.Single(s => s.Name == "settle");
        Assert.Equal("3", settle.Returned["reads"]);
        Assert.Equal("3", settle.Returned["rounds"]);
    }

    [Fact]
    public async Task Points_not_served_when_the_time_is_up_leave_the_record_for_a_try_that_reads_them_back_without_sending_them()
    {
        var platform = new FakeOsduPlatform { TimeSeriesMappingDelay = 100 };
        using var rig = new Rig(platform, Settings(settle: 3, poll: 1));
        var points = Files(("oil.json", Series(("OIL", [(Day0, JsonValue.Create(1.5))]))));
        var reported = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        var late = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Values(Standard), points, reported: reported)));
        Assert.Equal(
            $"the ingestion service accepted every point of {ValuesId}, and after 3s the query service does not serve 1 of the 1 request(s) yet "
            + $"(OIL version {FirstVersion} answers 404: Failed to get a Stream Mapping). The next try reads them back again without sending the points again",
            late.Message);
        Assert.Equal(4, platform.Calls.Count(c => c.Method == HttpMethod.Get));
        Assert.False(reported["points-1"].ContainsKey(ProductionTimeSeriesShape.SettledValue));

        platform.TimeSeriesMappingDelay = 0;
        var calls = platform.Calls.Count;
        var outcome = await rig.Protocol.DeliverAsync(Work(Values(Standard), points, completed: reported));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal([Read("OIL", FirstVersion, Day0, Day0)], Sent(platform, calls));
        Assert.Single(platform.TimeSeries[(ValuesId, "OIL")]);
    }

    [Fact]
    public async Task A_version_the_historian_lost_after_accepting_it_is_never_taken_for_served()
    {
        var platform = new FakeOsduPlatform();
        platform.TimeSeriesLost.Add("OIL");
        using var rig = new Rig(platform, Settings(settle: 2, poll: 1));

        var points = Files(("oil.json", Series(("OIL", [(Day0, JsonValue.Create(1.5))]))));
        var lost = await Assert.ThrowsAsync<DeliveryException>(() => rig.Protocol.DeliverAsync(Work(Values(Standard), points)));
        Assert.Contains($"OIL version {FirstVersion} answers 404: Failed to get a Stream Mapping", lost.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_series_longer_than_one_read_is_read_back_in_ranges_each_within_what_a_query_returns()
    {
        var platform = new FakeOsduPlatform { TimeSeriesReadLimit = 12_000 };
        using var rig = new Rig(platform);
        var table = await TableAsync([("timestamp", typeof(long)), ("WELLS", typeof(int))], Enumerable.Range(0, 25_000).Select(i => new object?[] { Day0 + (i * Minute), i }));

        var outcome = await rig.Protocol.DeliverAsync(Work(Values(Standard), Files(("wells.parquet", table))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal(
            [
                "PUT " + Records,
                "POST " + Ingest,
                Read("WELLS", FirstVersion, Day0, Day0 + (9_999 * Minute)),
                Read("WELLS", FirstVersion, Day0 + (10_000 * Minute), Day0 + (19_999 * Minute)),
                Read("WELLS", FirstVersion, Day0 + (20_000 * Minute), Day0 + (24_999 * Minute)),
            ],
            Sent(platform));
        Assert.Equal("25000", outcome.Returned["timeSeries.WELLS.points"]);

        // A query that returns fewer points than a range holds is not taken for the range.
        var cut = new FakeOsduPlatform { TimeSeriesReadLimit = 9_000 };
        using var limited = new Rig(cut, Settings(settle: 1, poll: 1));
        var failed = await Assert.ThrowsAsync<DeliveryException>(() => limited.Protocol.DeliverAsync(Work(Values(Standard), Files(("wells.parquet", table)))));
        Assert.Contains(
            string.Create(CultureInfo.InvariantCulture, $"(WELLS version {FirstVersion} serves 9000 of the 10000 point(s) from {Day0} to {Day0 + (9_999 * Minute)})"),
            failed.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timestamps_are_read_from_whole_numbers_timestamps_and_dates_and_a_pandas_index_column_is_left_out()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(Day0);
        var stamped = await TableAsync(
            [("timestamp", typeof(DateTime)), ("OIL", typeof(double)), ("__index_level_0__", typeof(long))],
            Enumerable.Range(0, 3).Select(i => new object?[] { start.UtcDateTime.AddDays(i), 1.5 + i, (long)i }),
            new Dictionary<string, string> { [ParquetFiles.PandasMetadataKey] = "{\"index_columns\":[\"__index_level_0__\"]}" });
        var dated = await TableAsync(
            [("timestamp", typeof(DateOnly)), ("WELLS", typeof(long))],
            Enumerable.Range(0, 2).Select(i => new object?[] { DateOnly.FromDateTime(start.UtcDateTime).AddDays(i), (long)(10 + i) }));
        var indexed = await TableAsync(
            [("FLAG", typeof(bool)), ("timestamp", typeof(long))],
            [[true, Day0 + (5 * Day)]],
            new Dictionary<string, string> { [ParquetFiles.PandasMetadataKey] = "{\"index_columns\":[\"timestamp\"]}" });

        var outcome = await rig.Protocol.DeliverAsync(Work(Values(Standard), Files(("a.parquet", stamped), ("b.parquet", dated), ("c.parquet", indexed))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal([Day0, Day0 + Day, Day0 + (2 * Day)], platform.TimeSeries[(ValuesId, "OIL")].Single().Points.Keys);
        Assert.Equal([Day0, Day0 + Day], platform.TimeSeries[(ValuesId, "WELLS")].Single().Points.Keys);
        Assert.Equal([Day0 + (5 * Day)], platform.TimeSeries[(ValuesId, "FLAG")].Single().Points.Keys);
        Assert.Equal(["OIL", "WELLS", "FLAG"], JsonNode.Parse(platform.Calls[1].Body!)!["timeseries"]!.AsArray().Select(s => s!["timeseriesId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_set_string_series_comes_from_json_and_a_double_keeps_the_digits_it_was_given()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var json = "{\"timeseries\":["
            + "{\"timeseriesId\":\"TAGS\",\"points\":[{\"timestamp\":" + Day0.ToString(CultureInfo.InvariantCulture) + ",\"value\":[\"shut-in\",\"tested\"]},"
            + "{\"timestamp\":" + (Day0 + Day).ToString(CultureInfo.InvariantCulture) + ",\"value\":[]}]},"
            + "{\"timeseriesId\":\"OIL\",\"points\":[{\"timestamp\":" + Day0.ToString(CultureInfo.InvariantCulture) + ",\"value\":58883469.740},"
            + "{\"timestamp\":" + (Day0 + Day).ToString(CultureInfo.InvariantCulture) + ",\"value\":1e3},"
            + "{\"timestamp\":" + (Day0 + (2 * Day)).ToString(CultureInfo.InvariantCulture) + ",\"value\":7}]},"
            + "{\"timeseriesId\":\"WELLS\",\"points\":[{\"timestamp\":" + Day0.ToString(CultureInfo.InvariantCulture) + ",\"value\":4.0}]}"
            + "]}";

        var outcome = await rig.Protocol.DeliverAsync(Work(Values(("TAGS", "dev:reference-data--ParameterKind:set-string:"), ("OIL", "Double"), ("WELLS", "Integer")), Files(("points.json", Encoding.UTF8.GetBytes(json)))));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        var body = platform.Calls[1].Body!;
        Assert.Contains("\"value\":[\"shut-in\",\"tested\"]", body, StringComparison.Ordinal);
        Assert.Contains("\"value\":[]", body, StringComparison.Ordinal);
        Assert.Contains("\"value\":58883469.740}", body, StringComparison.Ordinal);
        Assert.Contains("\"value\":1e3}", body, StringComparison.Ordinal);
        Assert.Contains("\"timeseriesId\":\"WELLS\",\"points\":[{\"timestamp\":1704067200000,\"value\":4}]", body, StringComparison.Ordinal);
        Assert.Equal(["TAGS", "OIL", "WELLS"], JsonNode.Parse(body)!["timeseries"]!.AsArray().Select(s => s!["timeseriesId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Whatever_the_historian_would_refuse_holds_the_record_before_anything_is_sent()
    {
        var withTimes = Values(("WHEN", "Timestamp"), ("OIL", "Double"));
        var withUnknown = Values(("ODD", "Decimal"), ("OIL", "Double"));
        var withSet = Values(("TAGS", "dev:reference-data--ParameterKind:set-string:"));
        var oldKind = Values("osdu:wks:work-product-component--ProductionValues:1.3.0", Well, Standard);
        var noEntity = Values(ValuesKind, null, Standard);
        var notMaster = Values(ValuesKind, "dev:work-product-component--WellLog:x:", Standard);
        var noSeries = Values();
        var twice = Values(("OIL", "Double"), ("OIL", "Integer"));
        var noKind = Values(Standard);
        noKind["data"]!["ProductionMetricValues"]![1]!.AsObject().Remove("ParameterKindID");
        var oneOil = Series(("OIL", [(Day0, JsonValue.Create(1.5))]));
        var subMillisecond = await TableAsync([("timestamp", typeof(DateTime)), ("OIL", typeof(double))], [[DateTimeOffset.FromUnixTimeMilliseconds(Day0).UtcDateTime.AddTicks(5), 1.0]]);

        var cases = new (JsonObject Document, (string Name, byte[] Bytes)[] Files, string Expected)[]
        {
            (Values(Standard), [("points.json", Series(("GAS", [(Day0, JsonValue.Create(1.5))])))], "holds points of GAS, which the record does not define"),
            (Values(Standard), [("points.json", Series(("OIL", [(Day0, JsonValue.Create("abc"))])))], "has the value \"abc\", which is not a finite number, as the Double series OIL takes"),
            (Values(Standard), [("points.json", Series(("WELLS", [(Day0, JsonValue.Create(2.5))])))], "has the value 2.5, which is not a whole number of 64 bits, as the Integer series WELLS takes"),
            (Values(Standard), [("points.json", Series(("FLAG", [(Day0, JsonValue.Create(1))])))], "which is not true or false, as the Boolean series FLAG takes"),
            (Values(Standard), [("points.parquet", await TableAsync([("timestamp", typeof(long)), ("OIL", typeof(string))], [[Day0, "abc"]]))], "row 1 of points.parquet holds abc (String) for OIL, which is not a finite number"),
            (Values(Standard), [("points.parquet", await TableAsync([("timestamp", typeof(long)), ("WELLS", typeof(double))], [[Day0, 1.0], [Day0 + Day, 2.5]]))], "row 2 of points.parquet holds 2.5 (Double) for WELLS, which is not a whole number"),
            (Values(Standard), [("points.parquet", await TableAsync([("timestamp", typeof(long)), ("OIL", typeof(double))], [[Day0, double.PositiveInfinity]]))], "for OIL, which is not a finite number"),
            (withTimes, [("points.json", Series(("WHEN", [(Day0, JsonValue.Create("2020-12-16T11:46:20.163Z"))])))], "WHEN is a date-time series (dev:reference-data--ParameterKind:Timestamp:)"),
            (withUnknown, [("points.json", Series(("ODD", [(Day0, JsonValue.Create(1.5))])))], "ODD names the kind dev:reference-data--ParameterKind:Decimal:, which the ingestion service does not know"),
            (Values(Standard), [("points.json", Series(("OIL", [(Day0, JsonValue.Create(1.5)), (Day0, JsonValue.Create(2.5))])))], $"point 2 of OIL in points.json is at {Day0}, the timestamp of the point before it"),
            (Values(Standard), [("points.parquet", await TableAsync([("timestamp", typeof(long)), ("OIL", typeof(double))], [[Day0 + Day, 1.0], [Day0, 2.0]]))], $"the point of OIL in row 2 of points.parquet is at {Day0}, before the point ahead of it ({Day0 + Day})"),
            (Values(Standard), [("points.json", Encoding.UTF8.GetBytes("{\"timeseries\":[{\"timeseriesId\":\"OIL\",\"points\":[{\"point\":1,\"value\":1.5}]}]}"))], "names its time point, which the ingestion service reads from timestamp"),
            (Values(Standard), [("points.json", Encoding.UTF8.GetBytes("{\"timeseries\":[{\"timeseriesId\":\"OIL\",\"unit\":\"bbl\",\"points\":[]}]}"))], "names a unit, which the ingestion service ignores"),
            (Values(Standard), [("points.json", Encoding.UTF8.GetBytes("{\"timeseries\":[{\"timeseriesId\":\"OIL\",\"points\":[{\"timestamp\":1,\"value\":null}]}]}"))], "point 1 of OIL in points.json has no value; leave out a point that has none"),
            (Values(Standard), [("points.json", Encoding.UTF8.GetBytes("{\"productionValues\":[]}"))], "is not in the form the ingestion service takes for one record"),
            (Values(Standard), [("points.json", Encoding.UTF8.GetBytes("{\"timeseries\":[{\"timeseriesId\":\"OIL\",\"points\":[]},{\"timeseriesId\":\"OIL\",\"points\":[]}]}"))], "the points file points.json lists OIL more than once"),
            (Values(Standard), [("points.json", Encoding.UTF8.GetBytes("not json"))], "the points file points.json is not JSON"),
            (withSet, [("points.parquet", await TableAsync([("timestamp", typeof(long)), ("TAGS", typeof(string))], [[Day0, "a"]]))], "TAGS is a SET-STRING series (dev:reference-data--ParameterKind:set-string:), whose values are lists of strings; send its points from a JSON points file"),
            (withSet, [("points.json", Series(("TAGS", [(Day0, new JsonArray("a", "a"))])))], "which is not a list of distinct strings, as the SET-STRING series TAGS takes"),
            (Values(Standard), [("a.json", oneOil), ("b.json", oneOil)], "OIL has points in both a.json and b.json; keep the points of a series in one file"),
            (Values(Standard), [("points.csv", Encoding.UTF8.GetBytes("timestamp,OIL"))], "the points file points.csv is neither .parquet nor .json"),
            (Values(Standard), [("points.parquet", await TableAsync([("timestamp", typeof(long)), ("OIL", typeof(double))], [[Day0, null], [Day0 + Day, double.NaN]]))], "hold no point: every value is empty"),
            (Values(Standard), [("points.parquet", await TableAsync([("time", typeof(long)), ("OIL", typeof(double))], [[Day0, 1.0]]))], "the points file points.parquet has no timestamp column"),
            (Values(Standard), [("points.parquet", await TableAsync([("timestamp", typeof(string)), ("OIL", typeof(double))], [["2024-01-01", 1.0]]))], "row 1 of points.parquet has the timestamp 2024-01-01 (String); the column holds epoch milliseconds as whole numbers, timestamps or dates"),
            (Values(Standard), [("points.parquet", await TableAsync([("timestamp", typeof(long))], [[Day0]]))], "the points file points.parquet has no column of values"),
            (Values(Standard), [("points.parquet", subMillisecond)], "finer than the milliseconds the historian keeps"),
            (Values(Standard), [("points.json", Series(("OIL", [(long.MaxValue, JsonValue.Create(1.5))])))], "is at the largest timestamp there is"),
            (Values(Standard), [("points.parquet", Encoding.UTF8.GetBytes("PAR1 not really"))], "the points file points.parquet is declared as parquet but is not a parquet file: it does not start and end with the parquet marker"),
            (Values(Standard), [("points.parquet", Encoding.UTF8.GetBytes("PAR1 truncated PAR1"))], "is not a parquet file: its footer is said to be 543450484 bytes long, which the file cannot hold"),
            (Values(Standard), [("points.parquet", Encoding.UTF8.GetBytes("PAR1"))], "is not a parquet file: it is 4 bytes long, shorter than the smallest parquet file"),
            (Values(Standard), [("points.parquet", [.. "PAR1"u8, .. "not a footer"u8, 12, 0, 0, 0, .. "PAR1"u8])], "the points file points.parquet is declared as parquet but could not be read"),
            (oldKind, [("points.json", oneOil)], "is ProductionValues 1.3.0; the historian keeps the points of ProductionValues 2.0.0 and later"),
            (noEntity, [("points.json", oneOil)], "data.ReportingEntityID is not given"),
            (notMaster, [("points.json", oneOil)], "data.ReportingEntityID 'dev:work-product-component--WellLog:x:' is not a master data record"),
            (noSeries, [("points.json", oneOil)], "data.ProductionMetricValues lists no series"),
            (twice, [("points.json", oneOil)], "data.ProductionMetricValues lists the DDMSDatasetID OIL more than once"),
            (noKind, [("points.json", oneOil)], "WELLS (entry 2 of data.ProductionMetricValues) has no ParameterKindID"),
            (Values(Standard), [], "no points file was found for the record"),
        };

        foreach (var (document, files, expected) in cases)
        {
            var platform = new FakeOsduPlatform();
            using var rig = new Rig(platform);
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(document, Files(files))));
            Assert.True(held.Message.Contains(expected, StringComparison.Ordinal), $"expected '{expected}' in: {held.Message}");
            Assert.Empty(platform.Calls);
        }

        // A record the historian cannot take is held even when it goes without points.
        var alone = new FakeOsduPlatform();
        using var bare = new Rig(alone);
        await Assert.ThrowsAsync<RecordHeldException>(() => bare.Protocol.DeliverAsync(Work(oldKind)));
        Assert.Empty(alone.Calls);
    }

    [Fact]
    public async Task A_point_too_large_for_a_request_or_a_ceiling_too_small_for_any_holds_the_record()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform, Settings(maxRequestBytes: 10_000));
        var note = Files(("notes.json", Series(("NOTE", [(Day0, JsonValue.Create(new string('x', 20_000)))]))));
        var large = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(("NOTE", "String")), note)));
        Assert.Contains($"the point of NOTE at {Day0} takes", large.Message, StringComparison.Ordinal);
        Assert.Contains("above the 10000 bytes a request to the ingestion service is given; shorten the value or raise maxRequestBytes", large.Message, StringComparison.Ordinal);
        Assert.Empty(platform.Calls);

        // The target's declared ceiling bounds the requests below the DDMS's limit.
        var capped = new FakeOsduPlatform();
        using var bounded = new Rig(capped, ceiling: 2_000);
        var points = Files(("oil.json", Series(("OIL", Enumerable.Range(0, 100).Select(i => (Day0 + (i * Day), (JsonNode?)JsonValue.Create(i + 0.5)))))));
        var outcome = await bounded.Protocol.DeliverAsync(Work(Values(Standard), points));
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.All(capped.Calls.Where(c => c.Method == HttpMethod.Post), c => Assert.True(Encoding.UTF8.GetByteCount(c.Body!) <= 2_000));
        Assert.True(capped.Calls.Count(c => c.Method == HttpMethod.Post) > 1);

        var tiny = new FakeOsduPlatform();
        using var cramped = new Rig(tiny, ceiling: 500);
        var cramp = await Assert.ThrowsAsync<RecordHeldException>(() => cramped.Protocol.DeliverAsync(Work(Values(Standard), points)));
        Assert.Contains("the target's declared request body ceiling of 500 bytes (reliability.maxRequestBodyBytes) leaves no room for points", cramp.Message, StringComparison.Ordinal);
        Assert.Empty(tiny.Calls);
    }

    [Fact]
    public async Task A_series_the_ingestion_service_refuses_holds_the_record_and_names_the_versions_it_accepted()
    {
        var points = Files(("points.json", Series(("OIL", [(Day0, JsonValue.Create(1.5))]), ("WELLS", [(Day0, JsonValue.Create(3))]))));

        // The deployment takes one partition, and this flow sends another.
        var other = new FakeOsduPlatform { TimeSeriesPartition = "tenant2" };
        using (var rig = new Rig(other))
        {
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(Standard), points)));
            Assert.Equal(
                $"the ingestion service did not take every series of request 1 of the points of {ValuesId}: OIL (400: Data partition id is not valid); WELLS (400: Data partition id is not valid)",
                held.Message);
        }

        // The record the service reads defines OIL alone: WELLS is refused, and the version OIL was accepted under is named.
        var platform = new FakeOsduPlatform();
        platform.Put(Values(("OIL", "Double")));
        using (var rig = new Rig(platform))
        {
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(Standard), points, metadata: false, existing: 1)));
            Assert.Equal(
                $"the ingestion service did not take every series of request 1 of the points of {ValuesId}: WELLS (404: 'WELLS' timeseries not found in {ValuesId}). "
                + $"It accepted OIL as version {FirstVersion}, which a later delivery sends again as new versions",
                held.Message);
        }

        // The service cannot read the record at all.
        var empty = new FakeOsduPlatform();
        using (var rig = new Rig(empty))
        {
            var held = await Assert.ThrowsAsync<RecordHeldException>(() => rig.Protocol.DeliverAsync(Work(Values(Standard), points, metadata: false, existing: 1)));
            Assert.StartsWith($"the ingestion service did not find {ValuesId} for request 1 of its points (HTTP 404)", held.Message, StringComparison.Ordinal);
            Assert.Equal(["POST " + Ingest], Sent(empty));
        }
    }

    [Fact]
    public async Task A_removal_goes_to_storage_at_every_scope_and_says_the_points_stay()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var delivered = await rig.Protocol.DeliverAsync(Work(Values(Standard)));
        Assert.True(delivered.Succeeded, delivered.Failure?.Message);

        var removed = await rig.Protocol.DeleteAsync(ValuesId, RemovalScope.Record);
        Assert.True(removed.Deleted);
        Assert.Equal("removed from OSDU (reversible); the points of its series stay in the historian, which has no delete for points", removed.Detail);
        var gone = await rig.Protocol.DeleteAsync(ValuesId, RemovalScope.Record);
        Assert.True(gone.AlreadyGone);
        Assert.StartsWith("record not found in OSDU; the points", gone.Detail, StringComparison.Ordinal);

        var history = await rig.Protocol.DeleteAsync(ValuesId, RemovalScope.History);
        Assert.True(history.Deleted);
        var everything = await rig.Protocol.DeleteAsync(ValuesId, RemovalScope.Everything);
        Assert.True(everything.Deleted);
        Assert.Equal(
            [
                "PUT " + Records,
                $"POST {Records}/{ValuesId}:delete",
                $"POST {Records}/{ValuesId}:delete",
                $"DELETE {Records}/{ValuesId}/versions",
                $"DELETE {Records}/{ValuesId}",
            ],
            Sent(platform));
        Assert.Contains(ValuesId, platform.Purged);

        var endpoints = RemovalEndpoints.Of(rig.Flow, ValuesKind);
        Assert.Equal("/api/storage/v2/records/{id}:delete", endpoints.Record);
        Assert.Equal("POST", endpoints.RecordMethod);
        Assert.Equal("/api/storage/v2/records/{id}/versions", endpoints.History);
        Assert.Equal("/api/storage/v2/records/{id}", endpoints.Everything);

        OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Storage);
    }

    [Fact]
    public async Task Verify_and_read_go_to_storage_and_the_probe_asks_both_services()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var delivered = await rig.Protocol.DeliverAsync(Work(Values(Standard)));

        var verified = await rig.Protocol.VerifyAsync(ValuesId, delivered.TargetVersion);
        Assert.Equal(VerifyOutcome.Match, verified.Outcome);
        var read = await rig.Protocol.ReadAsync(ValuesId);
        Assert.Equal(ValuesId, read!["id"]!.GetValue<string>());

        var probe = await rig.Protocol.ProbeAsync();
        Assert.True(probe.Reachable, probe.Detail);
        Assert.Equal("the DDMS answered all 2 probes", probe.Detail);
        Assert.Equal(FakeOsduPlatform.TimeSeriesRoot + "/info, " + FakeOsduPlatform.TimeSeriesQueryRoot + "/info", probe.Path);
        Assert.Equal(
            [
                "core/storage GET /records/{id}",
                "core/storage PUT /records",
                "production-timeseries/ingestion GET /info",
                "production-timeseries/query GET /info",
            ],
            OsduContracts.AssertConform(platform.Calls, null, OsduContracts.Storage, OsduContracts.ProductionTimeSeriesIngestion, OsduContracts.ProductionTimeSeries));
    }

    [Fact]
    public void The_link_to_the_points_is_carried_into_a_document_the_record_is_rewritten_from()
    {
        var platform = new FakeOsduPlatform();
        using var rig = new Rig(platform);
        var link = ProductionTimeSeriesShape.Link(ValuesId);

        var bare = Values(Standard);
        Assert.True(rig.Protocol.CarryLink(ValuesId, null, bare));
        Assert.Equal([link], Links(bare));

        var wrong = Values(Standard);
        wrong["data"]!["DDMSDatasets"] = new JsonArray("urn://pddms/production-values/other/timeseries", "urn://kept", link, link);
        Assert.False(rig.Protocol.CarryLink(ValuesId, null, wrong));
        Assert.Equal(["urn://kept", link], Links(wrong));

        var right = Values(Standard);
        right["data"]!["DDMSDatasets"] = new JsonArray(link, link);
        Assert.True(rig.Protocol.CarryLink(ValuesId, null, right));
        Assert.Equal([link], Links(right));

        var unnamed = new JsonObject { ["data"] = new JsonObject() };
        Assert.True(rig.Protocol.CarryLink(ValuesId, Values(Standard), unnamed));
        Assert.Equal([link], Links(unnamed));
    }

    [Fact]
    public async Task A_manifest_writes_a_new_record_with_its_link_and_the_points_follow_it()
    {
        var platform = new FakeOsduPlatform();
        platform.Register("Osdu_ingest", new FakeOsduPlatform.Script { Effect = ComposedRouteTests.Ingest });
        using var rig = new Rig(platform);
        var protocol = new OsduManifestAndDdmsProtocol(rig.Client, rig.Options, NullLogger.Instance, time: rig.Clock, routing: DdmsRouting.Of(rig.Flow));
        var points = Files(("oil.json", Series(("OIL", [(Day0, JsonValue.Create(1.5))]))));
        var work = Work(Values(Standard)) with
        {
            DeliverPayload = true,
            Parts = [new WorkPayloadPart(PayloadParts.Bulk, "points", points, "h1")],
        };

        var outcome = (await protocol.DeliverBatchAsync([work]))[0];
        Assert.True(outcome.Succeeded, outcome.Failure?.Message);
        Assert.Equal([ProductionTimeSeriesShape.Link(ValuesId)], Links(platform.Records[ValuesId]));
        Assert.Single(platform.Triggers("Osdu_ingest"));
        Assert.Single(platform.TimeSeries[(ValuesId, "OIL")]);
        Assert.Equal(FirstVersion.ToString(CultureInfo.InvariantCulture), outcome.Returned["timeSeries.OIL.versions"]);
    }
}
