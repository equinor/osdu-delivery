using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A run of the first records of its scope (osdu/docs/reference/cli/run.md, A delivery flow's payload, maxRecords), over the
/// sample estate in the in-memory ingestion tables, the module's database on SQL Server and a fake protocol: it plans and
/// delivers the first records in the order a read of the scope pages in, leaves the scope's watermark where it was, and a
/// run of the whole scope afterwards delivers every record it left.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class FirstRecordsRunTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static int LogCount => SampleEstate.Logs().Count;

    /// <summary>
    /// The sample estate with its identity primary key the other way round, so the scope's first records by the order a read
    /// pages in are the estate's last logs, not the ones the tables were filled with first.
    /// </summary>
    private async Task<MemoryIngestionTables> ReversedAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        for (var i = 0; i < tables.Records.Count; i++)
        {
            tables.Records[i].Row["RecId"] = (long)(tables.Records.Count - i);
        }

        return tables;
    }

    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> RuntimeAsync(MemoryIngestionTables tables)
    {
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.Actor = "gui:tahir";
        return (runtime, protocol, ledger);
    }

    private async Task<object> RunAsync(FlowRuntime runtime, string operation, DeliveryRunPayload payload)
    {
        payload.Validate(operation);
        _clock.Advance(TimeSpan.FromMinutes(1));
        runtime.RunId = Guid.CreateVersion7();
        return await DeliveryExecutor.ExecuteInterfaceAsync(runtime, operation, payload, null, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
    }

    [Fact]
    public async Task A_deliver_run_of_the_first_records_delivers_them_alone_and_a_run_of_the_scope_then_delivers_the_rest()
    {
        Assert.True(LogCount > 3, "the sample estate holds more logs than the run takes");
        var tables = await ReversedAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var first = Assert.IsType<DeliverOutcome>(await RunAsync(runtime, DeliveryOperations.Deliver, new DeliveryRunPayload { MaxRecords = 2 }));

            // The two the scope's order puts first, the estate's last two logs, and no other.
            var sent = protocol.Deliveries.Select(d => d.Key).ToList();
            Assert.Equal(2, sent.Count);
            Assert.Contains(SampleEstate.Key(LogCount - 1), sent);
            Assert.Contains(SampleEstate.Key(LogCount - 2), sent);
            Assert.Equal(2, first.Delivered);
            Assert.Equal("the first 2 record(s) of the scope (maxRecords)", first.Selection);
            Assert.Equal(SubmissionKinds.Keys, (await ledger.GetSubmissionAsync(first.SubmissionId))!.Kind);

            // The scope's watermark is where it was, so the scope's next run reads every row and plans the ones left.
            var scope = Planner.ScopeKey(runtime.Parameters);
            Assert.Null(await ledger.GetWatermarkAsync(runtime.Flow.Id, scope));
            protocol.Deliveries.Clear();

            var whole = Assert.IsType<DeliverOutcome>(await RunAsync(runtime, DeliveryOperations.Deliver, DeliveryRunPayload.None));

            Assert.Equal(LogCount - 2, whole.Delivered);
            Assert.DoesNotContain(protocol.Deliveries, d => d.Key == SampleEstate.Key(LogCount - 1) || d.Key == SampleEstate.Key(LogCount - 2));
            Assert.Equal(LogCount, (await ledger.ListRecordsAsync(runtime.Flow.Id, null, LogCount + 10)).Count);
            Assert.NotNull(await ledger.GetWatermarkAsync(runtime.Flow.Id, scope));
        }
    }

    [Fact]
    public async Task A_plan_of_the_first_records_renders_them_and_sends_and_records_nothing()
    {
        var tables = await ReversedAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var plan = Assert.IsType<PlanOutcome>(await RunAsync(runtime, DeliveryOperations.Plan, new DeliveryRunPayload { MaxRecords = 3 }));

            Assert.Equal(3, plan.Records);
            Assert.Equal(3, plan.Deliveries);
            Assert.Equal("the first 3 record(s) of the scope (maxRecords)", plan.Selection);
            Assert.Empty(protocol.Deliveries);
            Assert.Empty(await ledger.ListRecordsAsync(runtime.Flow.Id, null, 10));
            Assert.Null(await ledger.GetWatermarkAsync(runtime.Flow.Id, Planner.ScopeKey(runtime.Parameters)));
        }
    }

    [Fact]
    public async Task A_scope_holding_fewer_records_than_the_run_asks_for_delivers_every_one_and_says_how_many_it_held()
    {
        var tables = await ReversedAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var run = Assert.IsType<DeliverOutcome>(await RunAsync(runtime, DeliveryOperations.Deliver, new DeliveryRunPayload { MaxRecords = DeliveryRunPayload.MaxRecordKeys }));

            Assert.Equal(LogCount, run.Delivered);
            Assert.Equal($"the first 1000 record(s) of the scope (maxRecords), of which it holds {LogCount}", run.Selection);

            // Every record of the scope went, and still the scope was read by key: its watermark waits for a run of the scope.
            Assert.Null(await ledger.GetWatermarkAsync(runtime.Flow.Id, Planner.ScopeKey(runtime.Parameters)));
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
            // A file a run left open on a slow machine; the temp folder is the operating system's to clear.
        }
    }
}
