using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>The engine at scale, in miniature: work batches, step resumption, and the fan-out choreography.</summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public class ScaleEngineTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DeliveryWorker Worker(FlowRuntime runtime, FakeProtocol protocol, OsduLedger ledger, string id = "test-worker")
        => new(ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), id) { MaxWait = null };

    /// <summary>The sample logs in the ingestion tables, with their payload files written under this test's root.</summary>
    private async Task<MemoryIngestionTables> EstateAsync()
        => await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime);

    [Fact]
    public async Task A_run_writes_work_batches_and_records_every_step_and_returned_value()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock, sources: tables);
        var local = Samples.LocalFlow(_root);
        var flow = local with { Parallelism = local.Parallelism with { BatchRecords = 2 } };
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        var protocol = new FakeProtocol();

        var intake = await runtime.Intake.IntakeAsync(flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        var submissionId = intake.Submission.SubmissionId;
        Assert.Equal(5, intake.Counts.Planned);
        Assert.Equal(3, intake.Counts.Batches);
        var submission = (await ledger.GetSubmissionAsync(submissionId))!;
        Assert.Equal(3, submission.BatchCount);
        Assert.Equal(SubmissionStatus.Planned, submission.Status);
        // The first run of a scope has no watermark, so its window opens at the beginning; it is still an incremental
        // read, and it is the watermark it writes that lets the next run start where this one stopped.
        Assert.Equal(SubmissionKinds.Incremental, submission.Kind);
        Assert.Equal(flow.Source.Record.Object, submission.SourceObject);
        Assert.NotNull(submission.WorkLocation);
        Assert.True(File.Exists(WorkBatchFile.PathFor(submission.WorkLocation!, submissionId, 0)));
        Assert.True(File.Exists(WorkBatchFile.PathFor(submission.WorkLocation!, submissionId, 1)));
        Assert.True(File.Exists(WorkBatchFile.PathFor(submission.WorkLocation!, submissionId, 2)));
        var pending = await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(0));
        Assert.NotNull(pending!.PendingDocumentRef);
        Assert.NotNull(pending.WorkBatch);
        // The origin of the queued version: the file and row the ingestion tables carry for it.
        Assert.Equal(SampleEstate.FileName, pending.PendingSourceFileName);
        Assert.Equal(1, pending.PendingSourceRowNumber);

        var summary = await Worker(runtime, protocol, ledger).DrainAsync(submissionId);
        Assert.Equal(5, summary.Delivered);
        Assert.Equal(3, summary.Batches);
        var batches = await ledger.ListWorkBatchesAsync(submissionId, 10, 0);
        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
        Assert.Equal(5, batches.Sum(b => b.Delivered));
        Assert.All(batches, b => Assert.Null(b.LeaseOwner));

        var delivered = await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(0));
        Assert.Equal(RecordStatus.Delivered, delivered!.Status);
        Assert.Null(delivered.PendingDocumentRef);
        Assert.Null(delivered.WorkBatch);
        Assert.Equal(SampleEstate.FileName, delivered.SourceFileName);
        Assert.Contains("\"recordId\":\"" + delivered.TargetId, delivered.TargetStateJson, StringComparison.Ordinal);
        Assert.Contains("\"version\"", delivered.TargetStateJson, StringComparison.Ordinal);

        var attempts = await ledger.ListAttemptsAsync(flow.Id, SampleEstate.Key(0), 5);
        var attempt = Assert.Single(attempts);
        Assert.NotNull(attempt.WorkBatch);
        Assert.Equal(SampleEstate.FileName, attempt.SourceFileName);
        using var result = JsonDocument.Parse(attempt.ResultJson!);
        Assert.Equal("fake", result.RootElement.GetProperty("steps")[0].GetProperty("name").GetString());
        Assert.Equal(200, result.RootElement.GetProperty("steps")[0].GetProperty("status").GetInt32());
        Assert.Equal(delivered.TargetId, result.RootElement.GetProperty("returned").GetProperty("recordId").GetString());

        var closed = await runtime.Intake.CompleteAsync(submissionId, flow.Id);
        Assert.Equal(SubmissionStatus.Completed, closed.Status);
        Assert.Equal(5, closed.Delivered);
    }

    [Fact]
    public async Task A_retry_resumes_after_the_steps_the_first_try_completed()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock, sources: tables);
        var flow = Samples.LocalFlow(_root);
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        var protocol = new FakeProtocol();
        var resumed = 0;
        protocol.Before = async (work, ct) =>
        {
            if (work.Completed("metadata") is { } done)
            {
                Assert.Equal("41", done["version"]);
                resumed++;
                return;
            }

            await work.ReportStepAsync("metadata", new Dictionary<string, string> { ["version"] = "41", ["recordId"] = work.TargetId }, ct);
            throw new OsduStatusException(503, "HTTP 503 Service Unavailable");
        };

        var intake = await runtime.Intake.IntakeAsync(flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        var submissionId = intake.Submission.SubmissionId;
        var first = await Worker(runtime, protocol, ledger).DrainAsync(submissionId);
        Assert.Equal(5, first.Retried);
        var waiting = await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(0));
        Assert.Equal(RecordStatus.Pending, waiting!.Status);
        Assert.Contains("\"metadata\"", waiting.PendingStepJson, StringComparison.Ordinal);
        Assert.NotNull(await ledger.NextDueAsync(flow.Id, submissionId, _clock.GetUtcNow().UtcDateTime));

        _clock.Advance(TimeSpan.FromMinutes(2));
        var second = await Worker(runtime, protocol, ledger, "resumer").DrainAsync(submissionId);
        Assert.Equal(5, second.Delivered);
        Assert.Equal(5, resumed);
        var delivered = await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(0));
        Assert.Equal(RecordStatus.Delivered, delivered!.Status);
        Assert.Null(delivered.PendingStepJson);
        var attempts = await ledger.ListAttemptsAsync(flow.Id, SampleEstate.Key(0), 5);
        Assert.Equal(2, attempts.Count);
        Assert.Contains("\"resumed\":true", attempts[0].ResultJson, StringComparison.Ordinal);
        Assert.Equal(AttemptOutcome.Failed, attempts[1].Outcome);
        Assert.Contains("\"metadata\"", attempts[1].ResultJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// A deliver run fans out into slice runs: the flow queued again once per share of the key slices, each run delivering
    /// its share on its own. Every run here has a copy of the repository, and with it a work folder, of its own, as every
    /// node of a pool has: no run reads another's batches, so every record is planned once and delivered once, by the run
    /// whose share it is in.
    /// </summary>
    [Fact]
    public async Task A_deliver_run_fans_out_into_slice_runs_that_each_deliver_their_share_from_their_own_node()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var flow = OnNode(Fanned(Samples.LocalFlow(_root), fanOut: 2), "coordinator");
        var dispatcher = new InlineDispatcher(flow) { OnNode = slot => OnNode(flow, $"node-{slot}") };
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol), FanOut = dispatcher };
        dispatcher.Engine = engine;
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "test";

        var result = await runtime.RunAsync(force: false);
        var submissionId = result.Submission.SubmissionId;

        // Five candidate records at one record per batch: five slices, dealt as shares to the coordinating run and the two
        // runs it queued, each the deliver operation of the same flow filtered to its slices.
        Assert.Equal(2, result.Members);
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal((5, 5, 0), (result.Submission.Planned, result.Submission.Delivered, result.Submission.Held));
        Assert.Equal(5, result.Submission.BatchCount);
        Assert.Equal(5, result.Submission.Slices);
        Assert.Equal("RecId", SourceWindowDescription.Parse(result.Submission.SourceWindowJson)!.SlicedOn);
        Assert.Equal(5, protocol.Deliveries.Count);
        Assert.Equal(5, protocol.Deliveries.Select(d => d.Key).Distinct().Count());

        Assert.Equal(2, dispatcher.Enqueued.Count);
        Assert.All(dispatcher.Enqueued, e => Assert.Equal(DeliveryOperations.Deliver, e.Operation));
        Assert.All(dispatcher.Enqueued, e => Assert.Equal(submissionId, e.Payload.SubmissionId));
        Assert.All(dispatcher.Enqueued, e => Assert.Equal(3, e.Payload.RateShare));
        var dealt = dispatcher.Enqueued.SelectMany(e => e.Payload.Slices).ToList();
        Assert.Equal(dealt.Count, dealt.Distinct().Count());
        Assert.DoesNotContain(0, dealt);

        // Every batch was sent, each from the folder of the run that wrote it: the coordinating run's share in its own, each
        // slice run's in its node's.
        var batches = await ledger.ListWorkBatchesAsync(submissionId, 10, 0);
        Assert.Equal(5, batches.Count);
        Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
        Assert.True(File.Exists(WorkBatchFile.PathFor(Path.Combine(_root, "coordinator", "work"), submissionId, 0)));
        foreach (var (slot, slices) in dispatcher.Enqueued.Select((e, i) => (i + 1, e.Payload.Slices)))
        {
            foreach (var slice in slices)
            {
                Assert.True(File.Exists(WorkBatchFile.PathFor(Path.Combine(_root, $"node-{slot}", "work"), submissionId, SubmissionIntake.BatchBase(slice))));
            }
        }

        var activities = await ledger.ListActivitiesAsync(new ActivityQuery { SubmissionId = submissionId, Max = 20 });
        Assert.Equal(3, activities.Count(a => a.Kind == "deliver" && a.Outcome == "completed"));
    }

    /// <summary>
    /// A flow fans out by default, and every slice run carries how many runs share the flow's rate limit: the coordinating
    /// run and the runs it queued.
    /// </summary>
    [Fact]
    public async Task A_flow_fans_out_by_default_and_every_slice_run_shares_the_rate_limit()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var local = Samples.LocalFlow(_root);
        Assert.Equal(FlowParallelism.DefaultFanOut, local.Parallelism.FanOut);
        var flow = local with { Parallelism = local.Parallelism with { FanOutMinRecords = 1, BatchRecords = 2 } };
        var dispatcher = new InlineDispatcher(flow);
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol), FanOut = dispatcher };
        dispatcher.Engine = engine;
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "test";

        var result = await runtime.RunAsync(force: false);

        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal(5, result.Submission.Delivered);

        // Five candidates at two to a slice: two slices, one of them a slice run's, far fewer than the eight the flow allows,
        // so no run is queued with nothing to deliver.
        Assert.Equal(1, result.Members);
        var queued = Assert.Single(dispatcher.Enqueued);
        Assert.Equal(DeliveryOperations.Deliver, queued.Operation);
        Assert.Equal(2, queued.Payload.RateShare);
    }

    /// <summary>
    /// A coordinating run never waits for ever on slice runs no node starts. When every place in the pool holds work, its
    /// own slot among them, the slice runs are stopped once the start grace has passed with none started, and their shares
    /// are delivered here. Every record is planned once and delivered once.
    /// </summary>
    [Fact]
    public async Task A_run_whose_slice_runs_no_node_starts_delivers_their_shares_itself()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var flow = Fanned(Samples.LocalFlow(_root), fanOut: 2);
        var dispatcher = new UnstartedDispatcher(_clock, TimeSpan.FromMinutes(1));
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol), FanOut = dispatcher };
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "test";
        runtime.MemberPoll = TimeSpan.FromMilliseconds(10);
        runtime.MemberStartGrace = TimeSpan.FromMinutes(3);

        var result = await runtime.RunAsync(force: false);

        Assert.Equal(2, result.Members);
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal((5, 5), (result.Submission.Planned, result.Submission.Delivered));
        Assert.Equal(5, protocol.Deliveries.Count);
        Assert.Equal(5, protocol.Deliveries.Select(d => d.Key).Distinct().Count());

        // The slice runs were stopped only once the grace had passed on the run's clock with none started.
        var cancelled = Assert.Single(dispatcher.Cancelled);
        Assert.Equal(DeliveryOperations.Deliver, cancelled.Operation);
        Assert.True(cancelled.After >= TimeSpan.FromMinutes(3), $"the slice runs were stopped after {cancelled.After}");

        var batches = await ledger.ListWorkBatchesAsync(result.Submission.SubmissionId, 10, 0);
        Assert.Equal(5, batches.Count);
        Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
    }

    /// <summary>
    /// A slice run that fails before it delivers anything leaves its share to the coordinating run, which delivers it: the
    /// fan-out completes with every record delivered, and the failure is the slice run's, not the delivery's.
    /// </summary>
    [Fact]
    public async Task A_slice_run_that_fails_has_its_share_delivered_by_the_coordinating_run()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var flow = OnNode(Fanned(Samples.LocalFlow(_root), fanOut: 2), "coordinator");
        var dispatcher = new InlineDispatcher(flow) { OnNode = slot => OnNode(flow, $"node-{slot}"), Failing = new HashSet<int> { 1 } };
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol), FanOut = dispatcher };
        dispatcher.Engine = engine;
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "test";

        var result = await runtime.RunAsync(force: false);

        Assert.Equal(2, result.Members);
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal((5, 5, 0), (result.Submission.Planned, result.Submission.Delivered, result.Submission.Held));
        Assert.Equal(5, protocol.Deliveries.Select(d => d.Key).Distinct().Count());

        // The failed run's share was planned and sent from the coordinating run's own folder.
        foreach (var slice in dispatcher.Enqueued[0].Payload.Slices)
        {
            Assert.True(File.Exists(WorkBatchFile.PathFor(Path.Combine(_root, "coordinator", "work"), result.Submission.SubmissionId, SubmissionIntake.BatchBase(slice))));
        }
    }

    /// <summary>
    /// A slice run whose node stopped after it planned its share leaves records queued in batch files on that node, which no
    /// other run reads. The coordinating run delivers the rest of the share, finds those documents missing and gives the
    /// records back to be planned again by the flow's next run: the delivery completes, nothing is sent from a document that
    /// is not the record's, and no record is lost.
    /// </summary>
    [Fact]
    public async Task A_slice_run_that_stopped_after_planning_leaves_its_queued_records_to_be_planned_again()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var flow = OnNode(Fanned(Samples.LocalFlow(_root), fanOut: 2), "coordinator");
        var dispatcher = new InlineDispatcher(flow) { OnNode = slot => OnNode(flow, $"node-{slot}"), PlanOnly = new HashSet<int> { 1 } };
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol), FanOut = dispatcher };
        dispatcher.Engine = engine;
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "test";

        var result = await runtime.RunAsync(force: false);

        var stopped = dispatcher.Enqueued[0].Payload.Slices;
        var delivered = 5 - stopped.Count;
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal(delivered, protocol.Deliveries.Count);
        Assert.Equal((delivered, stopped.Count), (result.Submission.Delivered, result.Submission.Held));
        var held = new List<RecordState>();
        for (var index = 0; index < 5; index++)
        {
            var record = (await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(index)))!;
            if (record.Status != RecordStatus.Delivered)
            {
                held.Add(record);
            }
        }

        Assert.Equal(stopped.Count, held.Count);
        Assert.All(held, r => Assert.Equal(RecordStatus.Held, r.Status));
        Assert.All(held, r => Assert.Contains("could not be read", r.LastError, StringComparison.Ordinal));
        Assert.All(held, r => Assert.NotNull(r.PlanRequestedUtc));
    }

    /// <summary>
    /// A work location that names the deployment's storage (<c>${env:LANDING_URL}/...</c>) is resolved with the run's own
    /// values as the run opens, and the delivery's runs write their batches under it.
    /// </summary>
    [Fact]
    public async Task A_work_location_naming_the_deployment_s_storage_is_resolved_and_the_delivery_fans_out_over_it()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var local = Samples.LocalFlow(_root);
        var flow = local with
        {
            Source = local.Source with { Work = "${env:LANDING_URL}/.work/welllog" },
            Parallelism = local.Parallelism with { FanOut = 2, FanOutMinRecords = 1, BatchRecords = 1 },
        };
        var landing = Path.Combine(_root, "landing");
        using var supplied = SuppliedReferences.Supply(new Dictionary<string, string>(StringComparer.Ordinal) { ["LANDING_URL"] = landing });
        var dispatcher = new InlineDispatcher(flow);
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol), FanOut = dispatcher };
        dispatcher.Engine = engine;
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "test";

        var result = await runtime.RunAsync(force: false);

        Assert.Equal(2, result.Members);
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal(5, result.Submission.Delivered);
        Assert.Equal(Path.Combine(landing, ".work", "welllog"), result.Submission.WorkLocation);
        Assert.True(File.Exists(WorkBatchFile.PathFor(result.Submission.WorkLocation!, result.Submission.SubmissionId, 0)));
    }

    [Fact]
    public async Task A_work_location_naming_a_reference_the_run_cannot_resolve_stops_it_before_anything_is_read()
    {
        var tables = await EstateAsync();
        var engine = Samples.Engine(_db.Ledger(_clock), _clock, sources: tables);
        var local = Samples.LocalFlow(_root);
        var flow = local with { Source = local.Source with { Work = "${env:OSDU_TEST_NO_SUCH_WORK_ROOT}/.work" } };

        var refused = await Assert.ThrowsAsync<SqlFlowException>(() => FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values));

        Assert.StartsWith("The source.work '${env:OSDU_TEST_NO_SUCH_WORK_ROOT}/.work' names a reference that does not resolve", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, tables.Opens);
    }

    /// <summary>
    /// A submission planned again (a run resuming it, a member executed again, a coordinating run planning a stopped member's
    /// share) passes over the records it already queued, which keep reading their documents from the batch files they were
    /// written to. The new plan writes its batches after those, so no file a queued record reads from is written again.
    /// </summary>
    [Fact]
    public async Task A_submission_planned_again_writes_its_batches_after_the_ones_its_queued_records_read_from()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock, sources: tables);
        var local = Samples.LocalFlow(_root);
        var flow = local with { Parallelism = local.Parallelism with { BatchRecords = 2 } };
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);

        var first = await runtime.Intake.IntakeAsync(flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        var submissionId = first.Submission.SubmissionId;
        var work = first.Submission.WorkLocation!;
        Assert.Equal(3, first.Counts.Batches);
        var queued = (await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(0)))!;
        Assert.Equal(0, DocumentRef.Parse(queued.PendingDocumentRef!).Batch);

        // One record changes within the window the submission recorded, and the submission is planned again.
        SampleEstate.Change(tables.Records[4], "creator", "HAL", tables.Records[4].UpdatedUtc, SampleWellLogs.UpdatedUtc(4).AddHours(2));
        runtime.SubmissionId = submissionId;
        var again = await runtime.Intake.IntakeAsync(flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);

        Assert.Equal(1, again.Counts.Planned);
        Assert.Equal(1, again.Counts.Batches);
        var batches = await ledger.ListWorkBatchesAsync(submissionId, 10, 0);
        Assert.Equal([0, 1, 2, 3], batches.Select(b => b.Index));
        var changed = (await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(4)))!;
        Assert.Equal(3, DocumentRef.Parse(changed.PendingDocumentRef!).Batch);

        // The record queued first still reads its own document from the batch it was written to.
        var item = await WorkBatchFile.ReadOneAsync(runtime.Context.Stores, work, submissionId, DocumentRef.Parse(queued.PendingDocumentRef!));
        Assert.Equal(SampleEstate.Key(0).Value, item.Key);
        var keys = new List<Guid>();
        await foreach (var (entry, _) in WorkBatchFile.ReadAllAsync(runtime.Context.Stores, work, submissionId, 0))
        {
            keys.Add(entry.Key);
        }

        Assert.Equal(new[] { SampleEstate.Key(0).Value, SampleEstate.Key(1).Value }.Order(), keys.Order());
    }

    /// <summary>
    /// A record retried outside its batch reads its document by offset from the batch file it was queued in. A line there that
    /// belongs to another record (the file was written again since) is never sent under it: the record is held, to be planned
    /// again from its row.
    /// </summary>
    [Fact]
    public async Task A_retried_record_never_sends_a_document_its_batch_file_now_holds_for_another_record()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock, sources: tables);
        var local = Samples.LocalFlow(_root);
        var flow = local with { Parallelism = local.Parallelism with { BatchRecords = 2 } };
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        var protocol = new FakeProtocol();
        var failed = false;
        protocol.Before = (work, ct) =>
        {
            if (!failed && work.Key == SampleEstate.Key(0))
            {
                failed = true;
                throw new OsduStatusException(503, "HTTP 503 Service Unavailable");
            }

            return Task.CompletedTask;
        };

        var intake = await runtime.Intake.IntakeAsync(flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        var submissionId = intake.Submission.SubmissionId;
        var first = await Worker(runtime, protocol, ledger).DrainAsync(submissionId);
        Assert.Equal(4, first.Delivered);
        var waiting = (await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(0)))!;
        Assert.Equal(RecordStatus.Pending, waiting.Status);
        var reference = DocumentRef.Parse(waiting.PendingDocumentRef!);

        // The batch file is written again, another record's document where the retried record's was, padded to its length.
        var other = WorkBatchFile.Encode(new WorkItem(SampleEstate.Key(1).Value, "dev:master-data--Other:1", "{}", null));
        Assert.True(other.Length <= reference.Length);
        var rewritten = new byte[reference.Offset + reference.Length + 1];
        Array.Fill(rewritten, (byte)' ');
        other.CopyTo(rewritten, checked((int)reference.Offset));
        rewritten[^1] = (byte)'\n';
        await File.WriteAllBytesAsync(WorkBatchFile.PathFor(intake.Submission.WorkLocation!, submissionId, reference.Batch), rewritten);

        _clock.Advance(TimeSpan.FromMinutes(2));
        var deliveries = protocol.Deliveries.Count;
        var second = await Worker(runtime, protocol, ledger, "retrier").DrainAsync(submissionId);

        Assert.Equal(0, second.Delivered);
        Assert.Equal(deliveries, protocol.Deliveries.Count);
        var held = (await ledger.GetRecordAsync(flow.Id, SampleEstate.Key(0)))!;
        Assert.Equal(RecordStatus.Held, held.Status);
        Assert.Contains("could not be read", held.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fan-out no node ever starts: every member stays queued until it is cancelled, and the platform's clock moves on by
    /// <c>step</c> each time the state is asked, so a run waiting on it sees time pass as it would.
    /// </summary>
    private sealed class UnstartedDispatcher(TestClock clock, TimeSpan step) : IFanOutDispatcher
    {
        private readonly Dictionary<Guid, (string Operation, DateTimeOffset Enqueued, List<FanOutMemberState> Members)> _groups = [];

        /// <summary>Every fan-out cancelled, in order: its runs' operation, and how long after it was enqueued.</summary>
        public List<(string Operation, TimeSpan After)> Cancelled { get; } = [];

        /// <summary>How many fan-outs were enqueued.</summary>
        public int Enqueued => _groups.Count;

        public bool Available => true;

        public Task<FanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, CancellationToken ct = default)
        {
            var groupId = Guid.NewGuid();
            var states = new List<FanOutMemberState>();
            var operation = DeliveryOperations.Of(members[0]);
            for (var i = 0; i < members.Count; i++)
            {
                DeliveryRunPayload.Parse(members[i]).Validate(operation);
                states.Add(new FanOutMemberState(Guid.NewGuid(), i + 1, "queued", null, null));
            }

            _groups[groupId] = (operation, clock.GetUtcNow(), states);
            return Task.FromResult(new FanOutHandle(groupId, states.Select(s => s.RunId).ToList()));
        }

        public Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
        {
            clock.Advance(step);
            return Task.FromResult(new FanOutState([.. _groups[handle.GroupId].Members]));
        }

        public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default)
        {
            var (operation, enqueued, members) = _groups[handle.GroupId];
            Cancelled.Add((operation, clock.GetUtcNow() - enqueued));
            for (var i = 0; i < members.Count; i++)
            {
                if (members[i].Status == "queued")
                {
                    members[i] = members[i] with { Status = "cancelled", Error = "cancelled: the run that fanned it out cancelled its fan-out." };
                }
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A fan-out that runs every slice run inline, as a node runs it: the deliver operation of the flow its node has
    /// (<see cref="OnNode"/>, the coordinating run's flow unless given), on a runtime of its own over the same engine. A slot
    /// named in <see cref="Failing"/> fails before it does anything; one in <see cref="PlanOnly"/> plans its share on its node
    /// and fails before it sends any of it.
    /// </summary>
    private sealed class InlineDispatcher : IFanOutDispatcher
    {
        private readonly FlowDefinition _flow;
        private readonly Dictionary<Guid, FanOutMemberState> _members = [];
        private readonly Dictionary<Guid, List<Guid>> _groups = [];

        public InlineDispatcher(FlowDefinition flow)
        {
            _flow = flow;
        }

        public EngineContext? Engine { get; set; }

        /// <summary>The flow the node a slot runs on has: its own copy of the repository, and with it its own work folder.</summary>
        public Func<int, FlowDefinition>? OnNode { get; init; }

        public IReadOnlySet<int> Failing { get; init; } = new HashSet<int>();

        public IReadOnlySet<int> PlanOnly { get; init; } = new HashSet<int>();

        public List<(string Operation, DeliveryRunPayload Payload)> Enqueued { get; } = [];

        public bool Available => true;

        public async Task<FanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, CancellationToken ct = default)
        {
            var groupId = Guid.NewGuid();
            var ids = new List<Guid>();
            foreach (var member in members)
            {
                var runId = Guid.NewGuid();
                ids.Add(runId);
                var slot = ids.Count;
                var flow = OnNode?.Invoke(slot) ?? _flow;
                var payload = DeliveryRunPayload.Parse(member);
                Enqueued.Add((DeliveryOperations.Of(member), payload));
                if (Failing.Contains(slot))
                {
                    _members[runId] = new FanOutMemberState(runId, slot, "failed", "the node lost its connection to the ledger", null);
                    continue;
                }

                if (PlanOnly.Contains(slot))
                {
                    await FanOutMembers.PlanOnlyAsync(Engine!, flow, member, runId, ct);
                    _members[runId] = new FanOutMemberState(runId, slot, "failed", "the node stopped after planning its share", null);
                    continue;
                }

                var (_, _, resultJson) = await FanOutMembers.RunAsync(Engine!, flow, member, runId, ct);
                _members[runId] = new FanOutMemberState(runId, slot, "succeeded", null, resultJson);
            }

            _groups[groupId] = ids;
            return new FanOutHandle(groupId, ids);
        }

        public Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
            => Task.FromResult(new FanOutState(_groups[handle.GroupId].Select(id => _members[id]).ToList()));

        public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary><paramref name="flow"/> fanning out over at most <paramref name="fanOut"/> slice runs, one record to a slice.</summary>
    private static FlowDefinition Fanned(FlowDefinition flow, int fanOut)
        => flow with { Parallelism = flow.Parallelism with { FanOut = fanOut, FanOutMinRecords = 1, BatchRecords = 1 } };

    /// <summary>
    /// <paramref name="flow"/> as a node named <paramref name="node"/> has it: its document in that node's copy of the
    /// repository under this test's root, and its work folder written as the estate writes it, next to the document.
    /// </summary>
    private FlowDefinition OnNode(FlowDefinition flow, string node)
    {
        var copy = Samples.InFolder(flow, Path.Combine(_root, node));
        return copy with { Source = copy.Source with { Work = "../work" } };
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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
