using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A delivery run given several values of its scope parameter (osdu/docs/reference/flow/delivery.md, Parameters), over the
/// sample estate in the in-memory ingestion tables, the module's database on SQL Server and a fake protocol: it delivers the
/// rows holding any of them, keeps one watermark for the set, and is refused before it plans anything when the set makes a
/// scope key the ledger cannot keep a watermark under.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class ScopeValueListRunTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The sample estate with every other log moved to a second log source.</summary>
    private async Task<MemoryIngestionTables> TwoSourcesAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        for (var i = 0; i < tables.Records.Count; i += 2)
        {
            tables.Records[i].Row["log_source"] = "WIRELINE";
        }

        return tables;
    }

    private static Dictionary<string, string> LogSources(params string[] sources)
        => new(StringComparer.Ordinal) { ["logSource"] = RunValueList.Of(sources) };

    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> RuntimeAsync(MemoryIngestionTables tables, IReadOnlyDictionary<string, string> values)
    {
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), values);
        await ledger.RegisterAsync(runtime.Flow);
        return (runtime, protocol, ledger);
    }

    /// <summary>One run's work: plan into batches, drain them, and close the submission, as a deliver run does.</summary>
    private async Task<WorkerSummary> RunAsync(FlowRuntime runtime, FakeProtocol protocol, OsduLedger ledger)
    {
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        if (intake.NothingToDo)
        {
            return WorkerSummary.Empty;
        }

        var worker = new DeliveryWorker(
            ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
            CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
        var summary = await worker.DrainAsync(intake.Submission.SubmissionId);
        await runtime.Intake.CompleteAsync(intake.Submission.SubmissionId, runtime.Flow.Id);
        return summary;
    }

    [Fact]
    public async Task A_run_given_two_log_sources_delivers_the_logs_of_each_and_keeps_its_watermark_under_the_set()
    {
        var tables = await TwoSourcesAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, LogSources("WIRELINE", "COMPOSITE"));
        using (runtime)
        {
            var summary = await RunAsync(runtime, protocol, ledger);

            Assert.Equal(tables.Records.Count, summary.Delivered);
            Assert.Equal(["COMPOSITE", "WIRELINE"], RunValueList.Items(runtime.Parameters["logSource"]));
            var set = Planner.ScopeKey(runtime.Parameters);
            Assert.Equal("""logSource=["COMPOSITE","WIRELINE"]""", set);
            Assert.NotNull(await ledger.GetWatermarkAsync(runtime.Flow.Id, set));
            Assert.Null(await ledger.GetWatermarkAsync(runtime.Flow.Id, "logSource=COMPOSITE"));

            // The submission records the set as the run gave it, so a redelivery of one of its records reads that scope again.
            var submission = (await ledger.ListSubmissionsAsync(runtime.Flow.Id, 10)).Single();
            Assert.Equal("""{"logSource":["COMPOSITE","WIRELINE"]}""", submission.ParametersJson);
        }
    }

    [Fact]
    public async Task A_run_given_one_of_the_log_sources_delivers_only_its_logs()
    {
        var tables = await TwoSourcesAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, LogSources("WIRELINE"));
        using (runtime)
        {
            var summary = await RunAsync(runtime, protocol, ledger);

            Assert.Equal(tables.Records.Count(r => (string?)r.Row["log_source"] == "WIRELINE"), summary.Delivered);
            Assert.Equal("WIRELINE", runtime.Parameters["logSource"]);
        }
    }

    [Fact]
    public async Task A_set_whose_scope_key_the_ledger_cannot_keep_is_refused_before_anything_is_planned()
    {
        var tables = await TwoSourcesAsync();
        var many = Enumerable.Range(0, 30).Select(i => $"LOG_SOURCE_{i:00}").ToArray();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, LogSources(many));
        using (runtime)
        {
            var ex = await Assert.ThrowsAsync<DeliveryException>(() => RunAsync(runtime, protocol, ledger));

            Assert.StartsWith("The parameter values of this run make a scope key of ", ex.Message, StringComparison.Ordinal);
            Assert.Contains("or give a parameter fewer values at once", ex.Message, StringComparison.Ordinal);
            Assert.Empty(protocol.Deliveries);
            Assert.Empty(await ledger.ListSubmissionsAsync(runtime.Flow.Id, 10));
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory a reader still holds open is left for the operating system to reclaim.
        }
    }
}
