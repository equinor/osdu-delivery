using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dry run over records the ledger holds: what the next run would do with each comes from the ledger, a record that would be
/// skipped as unchanged still has its document judged, and a dry run leaves the ledger as it found it.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DryRunLedgerTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly FakeOsduPlatform _platform = new();
    private readonly TargetClients _clients;

    public DryRunLedgerTests() => _clients = new TargetClients(_clock, _platform, allowLoopback: true);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        _clients.Dispose();
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a failed assertion left open is removed with the temporary folder by the operating system.
        }
    }

    /// <summary>The sample estate delivered once through a fake target, and a runtime over the same ledger, whose Schema service holds the template.</summary>
    private async Task<(FlowRuntime Runtime, OsduLedger Ledger, MemoryIngestionTables Tables)> DeliveredAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol), Clients = _clients };
        var flow = Samples.LocalFlow(_root);
        var runtime = await FlowRuntime.CreateAsync(engine, flow with { Target = flow.Target with { Endpoint = FakeOsduPlatform.Endpoint } }, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        var worker = new DeliveryWorker(
            ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
            CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
        await worker.DrainAsync(intake.Submission.SubmissionId);
        await runtime.Intake.CompleteAsync(intake.Submission.SubmissionId, runtime.Flow.Id);
        _clock.Advance(TimeSpan.FromMinutes(1));
        _platform.Schemas[runtime.Mapping.Mapping.Kind] = (JsonObject)runtime.Mapping.Schema.Root.DeepClone();
        return (runtime, ledger, tables);
    }

    [Fact]
    public async Task A_record_a_run_would_skip_still_has_its_document_judged_and_the_ledger_is_left_as_it_was()
    {
        var (runtime, ledger, _) = await DeliveredAsync();
        using (runtime)
        {
            var logs = SampleEstate.Logs().Count;
            var before = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));

            var run = (await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { DryRun = true })).DryRun!;

            Assert.Equal(logs, run.Outcomes.Rows);
            Assert.Equal(logs, run.NextRun.Unchanged);
            Assert.Equal(0, run.NextRun.Create);

            // Unchanged is the ledger's word for what a run would do; the document is still held to both schemas.
            Assert.Equal(0, run.Template.NotChecked);
            Assert.Equal(0, run.Schema.NotChecked);
            var first = run.Records[0];
            Assert.Equal("skip", first.NextRun);
            Assert.NotNull(first.Template);
            Assert.NotNull(first.Schema);
            Assert.NotNull(first.TargetId);

            Assert.Equal(before, await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)));
        }
    }

    [Fact]
    public async Task A_row_that_changed_is_said_as_an_update_with_the_document_a_run_would_send()
    {
        var (runtime, _, tables) = await DeliveredAsync();
        using (runtime)
        {
            var record = tables.Records[0];
            record.Row["log_run"] = "RUN-CHANGED";
            record.UpdatedUtc = Now;

            var run = (await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { DryRun = true })).DryRun!;

            Assert.Equal(1, run.NextRun.Update);
            Assert.Equal(SampleEstate.Logs().Count - 1, run.NextRun.Unchanged);
            Assert.StartsWith("update", Assert.Single(run.Records, r => r.DeliveryKey == SampleEstate.Key(0).Value).NextRun, StringComparison.Ordinal);
        }
    }
}
