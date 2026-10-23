using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Retrieval;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An incremental retrieval's window never moves backwards: a run with nothing to read (a declared start still ahead, a lag
/// raised since the last run) records its start as its upper bound, a run never reads before the declared start, and a run
/// recorded with an upper bound below its own start is read as standing at its start. And a target location is written out:
/// a reference in it is refused where the document is read.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class RetrievalWindowTests : IDisposable
{
    private const string Kind = "osdu:wks:master-data--Wellbore:1.*.*";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();

    public void Dispose() => _db.Dispose();

    private static string Yaml(string since, int lagMinutes, string location = "{root}/out") => $$"""
        flowType: retrieval
        name: wellbores-out
        parameters:
          root: { required: true }
        source:
          endpoint: http://localhost/osdu
          headers: { data-partition-id: dev }
          kind: "{{Kind}}"
          incremental: { field: modifyTime, since: "{{since}}", lagMinutes: {{lagMinutes}} }
        target:
          location: "{{location}}"
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { concurrency: 1 }
        """;

    private sealed record Retrieval(RetrievalRunner Runner, FakeHttpHandler Handler, HttpRuntime Runtime, OsduLedger Ledger) : IDisposable
    {
        public void Dispose() => Runtime.Dispose();

        /// <summary>The query of the most recent page the run asked the search for.</summary>
        public string LastQuery => JsonNode.Parse(Handler.Calls[^1].Body!)!["query"]!.GetValue<string>();
    }

    private async Task<Retrieval> RunnerAsync(string yaml)
    {
        var flow = new DeliveryDocumentLoader().ParseRetrieval(yaml, "f");
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/query_with_cursor", HttpStatusCode.OK, """{"results":[],"totalCount":0}""");
        var runtime = new HttpRuntime(flow.Reliability, new SecretResolver([new EnvSecretProvider()]), _clock, handler, allowLoopback: true);
        var client = new OsduHttpClient(runtime, flow.Source.Endpoint, flow.Source.Auth, flow.Source.Headers);
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterAsync(flow);
        var values = FlowParameters.Resolve(flow.Parameters, "f", new Dictionary<string, string>(StringComparer.Ordinal) { ["root"] = Samples.NewTempDirectory() });
        return new Retrieval(new RetrievalRunner(flow, values, client, Samples.Stores(), ledger, _clock, Samples.Logger<RetrievalRunner>()), handler, runtime, ledger);
    }

    private static DateTime At(int hour, int minute) => new(2026, 9, 7, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_declared_start_still_ahead_is_where_the_window_waits_and_no_run_reads_before_it()
    {
        // Now is 12:00; the flow starts at 13:00 and lags 5 minutes.
        using var retrieval = await RunnerAsync(Yaml("2026-09-07T13:00:00Z", 5));

        var early = await retrieval.Runner.RunAsync(Guid.NewGuid(), "tester", force: false, CancellationToken.None);

        Assert.True(early.NothingToDo);
        Assert.Equal((At(13, 0), At(13, 0)), (early.Window!.From, early.Window.To));
        var row = Assert.Single(await retrieval.Ledger.ListRetrievalsAsync(FlowIdOf(), 10));
        Assert.Equal((RetrievalStatus.Done, At(13, 0), At(13, 0)), (row.Status, row.WindowFrom!.Value, row.WindowTo!.Value));
        Assert.Empty(retrieval.Handler.Calls);

        // At 14:00 the window runs from the declared start, not from where the clock stood at the first run.
        _clock.Advance(TimeSpan.FromHours(2));
        var next = await retrieval.Runner.RunAsync(Guid.NewGuid(), "tester", force: false, CancellationToken.None);

        Assert.Equal((At(13, 0), At(13, 55)), (next.Window!.From, next.Window.To));
        Assert.Equal(
            "(modifyTime:[2026-09-07T13:00:00.000Z TO 2026-09-07T13:55:00.000Z} OR (createTime:[2026-09-07T13:00:00.000Z TO 2026-09-07T13:55:00.000Z} AND NOT _exists_:modifyTime))",
            retrieval.LastQuery);
    }

    [Fact]
    public async Task A_lag_raised_since_the_last_run_leaves_the_window_where_it_stood()
    {
        using (var first = await RunnerAsync(Yaml("2026-09-01T00:00:00Z", 5)))
        {
            var run = await first.Runner.RunAsync(Guid.NewGuid(), "tester", force: false, CancellationToken.None);
            Assert.Equal(At(11, 55), run.Window!.To);
        }

        // The flow now lags an hour: now minus the lag (11:00) is behind the last upper bound, so the window is empty there.
        _clock.Advance(TimeSpan.FromMinutes(10));
        using (var lagging = await RunnerAsync(Yaml("2026-09-01T00:00:00Z", 60)))
        {
            var idle = await lagging.Runner.RunAsync(Guid.NewGuid(), "tester", force: false, CancellationToken.None);
            Assert.True(idle.NothingToDo);
            Assert.Equal((At(11, 55), At(11, 55)), (idle.Window!.From, idle.Window.To));
            var (window, estimates) = await lagging.Runner.EstimateAsync(force: false, CancellationToken.None);
            Assert.Equal((At(11, 55), At(11, 55)), (window!.From, window.To));
            Assert.Equal(0, Assert.Single(estimates).TotalCount);
        }

        // Back to five minutes, the next run carries on from 11:55 and reads nothing twice.
        using var back = await RunnerAsync(Yaml("2026-09-01T00:00:00Z", 5));
        var resumed = await back.Runner.RunAsync(Guid.NewGuid(), "tester", force: false, CancellationToken.None);
        Assert.Equal((At(11, 55), At(12, 5)), (resumed.Window!.From, resumed.Window.To));
        Assert.All(await back.Ledger.ListRetrievalsAsync(FlowIdOf(), 10), r => Assert.True(r.WindowTo >= r.WindowFrom, $"retrieval {r.RetrievalId} moved its window backwards"));
    }

    [Fact]
    public async Task A_run_recorded_with_its_upper_bound_below_its_start_is_read_as_standing_at_its_start()
    {
        using var retrieval = await RunnerAsync(Yaml("2026-09-01T00:00:00Z", 5));

        // A run that did nothing, recorded before windows were held from moving backwards: from 11:30, to 11:00.
        var recorded = await retrieval.Ledger.StartRetrievalAsync(new RetrievalState
        {
            FlowId = FlowIdOf(), FlowName = "wellbores-out", RunId = Guid.NewGuid(), Actor = "tester", Kinds = Kind, WindowField = "modifyTime",
            WindowFrom = At(11, 30), WindowTo = At(11, 0), Location = "out", Status = RetrievalStatus.Running, StartedUtc = At(11, 5),
        });
        await retrieval.Ledger.CompleteRetrievalAsync(recorded.RetrievalId, RetrievalStatus.Done, 0, 0, 0, null, null, At(11, 5));

        var window = await retrieval.Runner.WindowAsync(_clock.GetUtcNow().UtcDateTime, force: false, CancellationToken.None);

        Assert.Equal((At(11, 30), At(11, 55)), (window!.From, window.To));

        // A forced run still goes back to the declared start.
        var forced = await retrieval.Runner.WindowAsync(_clock.GetUtcNow().UtcDateTime, force: true, CancellationToken.None);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), forced!.From);
    }

    [Theory]
    [InlineData("${env:LAKE_ROOT}/osdu/wellbores")]
    [InlineData("abfss://lake@account.dfs.core.windows.net/${keyvault:folder}")]
    public void A_reference_in_the_target_location_is_refused_where_the_document_is_read(string location)
    {
        var refused = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseRetrieval(Yaml("2026-09-01T00:00:00Z", 5, location), "flows/retrieval.yaml"));

        Assert.Equal(
            $"flows/retrieval.yaml: target.location '{location}' holds a ${{...}} reference, and a location is not resolved from one: write the path or storage URI itself, and vary it per run or environment with a {{parameter}} token declared under parameters.",
            refused.Message);
    }

    private static Guid FlowIdOf() => Identity.FlowId.Of("wellbores-out");
}
