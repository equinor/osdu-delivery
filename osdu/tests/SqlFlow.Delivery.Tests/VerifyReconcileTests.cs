using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// <c>verify.reconcile</c> (osdu/docs/reference/flow/delivery.md, verify): a record the drift pass finds edited or removed in
/// OSDU is marked for redelivery as a redelivery marks it, named under the verify's activity, and the flow's next ordinary
/// deliver run sends it again, although its row did not change and the run reads only what changed since its watermark.
/// Through the executor's deliver path, over the module's database on SQL Server and a fake protocol.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class VerifyReconcileTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static int LogCount => SampleEstate.Logs().Count;

    [Fact]
    public async Task A_drifted_record_reconciled_by_a_verify_is_sent_again_by_the_next_incremental_deliver_run()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.Actor = "schedule:welldb";
        var log = Samples.Logger<DeliveryExecutor>();

        // The first deliver run sends every record and leaves the scope's watermark behind it.
        runtime.RunId = Guid.CreateVersion7();
        await DeliveryExecutor.ExecuteInterfaceAsync(runtime, DeliveryOperations.Deliver, DeliveryRunPayload.None, null, log, CancellationToken.None);
        Assert.Equal(LogCount, protocol.Deliveries.Count);
        Assert.NotNull(await ledger.GetWatermarkAsync(runtime.Flow.Id, Engine.Planning.Planner.ScopeKey(runtime.Parameters)));
        protocol.Deliveries.Clear();

        // Someone edits one record in OSDU; the drift pass finds it, and reconciles.
        var drifted = SampleEstate.Key(0);
        protocol.VerifyWith = id => id.EndsWith(drifted.Value.ToString("N"), StringComparison.Ordinal)
            ? new VerifyResult(VerifyOutcome.Drifted, 999, "someone edited it")
            : new VerifyResult(VerifyOutcome.Match, null, null);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var verifyRun = Guid.CreateVersion7();
        runtime.RunId = verifyRun;
        var summary = await runtime.VerifyAsync(100, null, reconcile: true);
        Assert.Equal((LogCount, 1, LogCount - 1), (summary.Checked, summary.Drifted, summary.Matched));

        // The record is asked to be planned again, its delivered state forgotten, and its history names the verify that asked.
        var marked = (await ledger.GetRecordAsync(runtime.Flow.Id, drifted))!;
        Assert.Equal((RecordStatus.Delivered, VerifyOutcome.Drifted), (marked.Status, marked.LastVerifyOutcome!.Value));
        Assert.NotNull(marked.PlanRequestedUtc);
        Assert.Equal(((string?)null, (string?)null, (string?)null), (marked.MetadataHash, marked.PayloadHash, marked.SourceFingerprint));
        Assert.Contains("verify: drifted (observed version 999", marked.LastError, StringComparison.Ordinal);
        var verify = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, DeliveryKey = drifted.Value, Kind = "verify" }));
        Assert.Equal(verifyRun, verify.RunId);
        Assert.Null((await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1)))!.PlanRequestedUtc);

        // The next ordinary deliver run reads no changed row, and still sends the reconciled record, whole, and nothing else.
        _clock.Advance(TimeSpan.FromMinutes(1));
        runtime.RunId = Guid.CreateVersion7();
        await DeliveryExecutor.ExecuteInterfaceAsync(runtime, DeliveryOperations.Deliver, DeliveryRunPayload.None, null, log, CancellationToken.None);
        var sent = Assert.Single(protocol.Deliveries);
        Assert.Equal(drifted, sent.Key);
        Assert.True(sent.DeliverMetadata);
        var redelivered = (await ledger.GetRecordAsync(runtime.Flow.Id, drifted))!;
        Assert.Equal(RecordStatus.Delivered, redelivered.Status);
        Assert.Null(redelivered.PlanRequestedUtc);
        Assert.NotNull(redelivered.MetadataHash);

        // Sent once: the run after it has nothing to send.
        protocol.Deliveries.Clear();
        runtime.RunId = Guid.CreateVersion7();
        await DeliveryExecutor.ExecuteInterfaceAsync(runtime, DeliveryOperations.Deliver, DeliveryRunPayload.None, null, log, CancellationToken.None);
        Assert.Empty(protocol.Deliveries);
    }

    [Fact]
    public async Task A_verify_without_reconcile_records_the_drift_and_asks_for_nothing()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.RunId = Guid.CreateVersion7();
        await runtime.RunAsync(force: false);
        var delivered = (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!;

        protocol.VerifyWith = _ => new VerifyResult(VerifyOutcome.Missing, null, null);
        var summary = await runtime.VerifyAsync(100, null, reconcile: false);

        Assert.Equal(LogCount, summary.Missing);
        var recorded = (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!;
        Assert.Equal(VerifyOutcome.Missing, recorded.LastVerifyOutcome);
        Assert.Null(recorded.PlanRequestedUtc);
        Assert.Equal((delivered.MetadataHash, delivered.SourceFingerprint), (recorded.MetadataHash, recorded.SourceFingerprint));
        Assert.Empty(await ledger.ListPlanRequestedAsync(runtime.Flow.Id, null, 10));
    }

    [Fact]
    public async Task A_verify_checks_that_removed_records_are_gone_from_osdu_and_reports_one_osdu_still_holds_without_writing_to_it()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.RunId = Guid.CreateVersion7();
        await runtime.RunAsync(force: false);

        // Two records are removed from OSDU. OSDU holds nothing at the first id; the second is there again, written by someone else.
        var gone = SampleEstate.Key(0);
        var served = SampleEstate.Key(1);
        await runtime.RemoveAsync(RemovalSelection.Of([gone, served]), RemovalChoice.Record);
        var removedServed = (await ledger.GetRecordAsync(runtime.Flow.Id, served))!;
        protocol.VerifyWith = id => id.EndsWith(gone.Value.ToString("N"), StringComparison.Ordinal)
            ? new VerifyResult(VerifyOutcome.Missing, null, "record not found")
            : new VerifyResult(VerifyOutcome.Match, 7, null);
        protocol.Deliveries.Clear();
        _clock.Advance(TimeSpan.FromMinutes(1));

        // The pass checks the removed records beside the delivered ones, and reconciles only the delivered.
        var summary = await runtime.VerifyAsync(100, null, reconcile: true);
        Assert.Equal((LogCount, LogCount - 2, 1, 1, 0, 0), (summary.Checked, summary.Matched, summary.Gone, summary.Served, summary.Drifted, summary.Missing));
        Assert.Contains("of those removed from OSDU, 1 gone and 1 still in OSDU", summary.ToString(), StringComparison.Ordinal);
        var checkedGone = (await ledger.GetRecordAsync(runtime.Flow.Id, gone))!;
        var checkedServed = (await ledger.GetRecordAsync(runtime.Flow.Id, served))!;
        Assert.Equal((RecordStatus.Deleted, VerifyOutcome.Gone), (checkedGone.Status, checkedGone.LastVerifyOutcome!.Value));
        Assert.Equal((RecordStatus.Deleted, VerifyOutcome.Served), (checkedServed.Status, checkedServed.LastVerifyOutcome!.Value));

        // A removed record OSDU still holds is reported, never written to: the ledger keeps what the removal left of it.
        Assert.Equal((removedServed.LastError, removedServed.PlanRequestedUtc), (checkedServed.LastError, checkedServed.PlanRequestedUtc));
        Assert.Empty(protocol.Deliveries);

        // The drifted narrowing lists the removed record OSDU still holds, and not the one gone from it.
        var drifted = await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Drifted = true, Max = 10 });
        Assert.Equal([served], drifted.Select(r => r.DeliveryKey));

        // A verify of one removed record, as its page asks for, checks that record alone.
        var one = await runtime.VerifyAsync(100, null, reconcile: false, [gone]);
        Assert.Equal((1, 1, 0), (one.Checked, one.Gone, one.Matched));
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
