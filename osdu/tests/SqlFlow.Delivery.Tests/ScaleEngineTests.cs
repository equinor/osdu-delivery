using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
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

    [Fact]
    public async Task A_deliver_run_fans_its_intake_and_drains_out_and_completes_the_submission()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var local = Samples.LocalFlow(_root);
        var flow = local with { Parallelism = local.Parallelism with { FanOut = 2, FanOutMinRecords = 1, BatchRecords = 1 } };
        var dispatcher = new InlineDispatcher(flow);
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol), FanOut = dispatcher };
        dispatcher.Engine = engine;
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "test";

        var result = await runtime.RunAsync(force: false);
        var submissionId = result.Submission.SubmissionId;

        // Five candidate records at one record per batch: five slices, dealt as shares to the coordinating run and its
        // two members.
        Assert.Equal(2, result.IntakeMembers);
        Assert.Equal(2, result.DrainMembers);
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal(5, result.Submission.Planned);
        Assert.Equal(5, result.Submission.Delivered);
        Assert.Equal(5, result.Submission.BatchCount);
        Assert.Equal(5, result.Submission.Slices);
        Assert.Equal("RecId", SourceWindowDescription.Parse(result.Submission.SourceWindowJson)!.SlicedOn);
        Assert.Equal(5, protocol.Deliveries.Count);

        var intakeMembers = dispatcher.Enqueued.Where(e => e.Operation == DeliveryOperations.Intake).ToList();
        Assert.Equal(2, intakeMembers.Count);
        Assert.All(intakeMembers, e => Assert.Equal(submissionId, e.Payload.SubmissionId));
        // Every member plans a share of its own, and no slice is planned twice.
        Assert.All(intakeMembers, e => Assert.NotEmpty(e.Payload.Slices));
        Assert.Equal(intakeMembers.SelectMany(e => e.Payload.Slices).Distinct().Count(), intakeMembers.Sum(e => e.Payload.Slices.Count));
        Assert.Equal(2, dispatcher.Enqueued.Count(e => e.Operation == DeliveryOperations.Drain));
        Assert.All(dispatcher.Enqueued.Where(e => e.Operation == DeliveryOperations.Drain), e => Assert.Equal(submissionId, e.Payload.SubmissionId));

        var batches = await ledger.ListWorkBatchesAsync(submissionId, 10, 0);
        Assert.Equal(5, batches.Count);
        Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
        // The member's batch numbers live in their slice's namespace, never colliding with the root's.
        Assert.Contains(batches, b => b.Index >= SubmissionIntake.BatchBase(1));
        Assert.Contains(batches, b => b.Index < SubmissionIntake.BatchBase(1));

        var activities = await ledger.ListActivitiesAsync(new ActivityQuery { SubmissionId = submissionId, Max = 20 });
        Assert.Contains(activities, a => a.Kind == "deliver" && a.Outcome == "completed");
        Assert.Contains(activities, a => a.Kind == "intake" && a.Outcome == "completed");
        Assert.Contains(activities, a => a.Kind == "drain" && a.Outcome == "completed");
    }

    /// <summary>
    /// A flow fans out by default: the drain enqueues one member per work batch beyond the one the coordinating run takes, up
    /// to parallelism.fanOut, and every member carries how many runs share the flow's rate limit.
    /// </summary>
    [Fact]
    public async Task A_drain_fans_out_one_member_per_batch_beyond_its_own_and_every_member_shares_the_rate_limit()
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
        var batches = await ledger.ListWorkBatchesAsync(result.Submission.SubmissionId, 10, 0);
        Assert.True(batches.Count >= 3, $"five records at two to a batch fill at least three batches; {batches.Count} were written");

        // Five candidates at two to a slice: two slices, one of them a member's. A member per batch beyond the coordinating
        // run's own, far fewer than the eight the flow allows, so no member is enqueued with nothing to take.
        Assert.Equal(1, result.IntakeMembers);
        Assert.Equal(Math.Min(FlowParallelism.DefaultFanOut, batches.Count - 1), result.DrainMembers);
        var intake = dispatcher.Enqueued.Where(e => e.Operation == DeliveryOperations.Intake).ToList();
        var drains = dispatcher.Enqueued.Where(e => e.Operation == DeliveryOperations.Drain).ToList();
        Assert.Equal(result.DrainMembers, drains.Count);
        Assert.All(intake, e => Assert.Equal(2, e.Payload.RateShare));
        Assert.All(drains, e => Assert.Equal(drains.Count + 1, e.Payload.RateShare));
    }

    /// <summary>
    /// A coordinating run never waits for ever on members no node starts. When every place in the pool holds work, its own
    /// slot among them, the intake members are stopped once the start grace has passed with none started, and their shares
    /// are planned here; the drain members are stopped as soon as this run's drain leaves nothing for them. Every record is
    /// planned once and delivered once.
    /// </summary>
    [Fact]
    public async Task A_run_whose_members_no_node_starts_does_their_part_itself()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var local = Samples.LocalFlow(_root);
        var flow = local with { Parallelism = local.Parallelism with { FanOut = 2, FanOutMinRecords = 1, BatchRecords = 1 } };
        var dispatcher = new UnstartedDispatcher(_clock, TimeSpan.FromMinutes(1));
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol), FanOut = dispatcher };
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "test";
        runtime.MemberPoll = TimeSpan.FromMilliseconds(10);
        runtime.MemberStartGrace = TimeSpan.FromMinutes(3);

        var result = await runtime.RunAsync(force: false);

        Assert.Equal(2, result.IntakeMembers);
        Assert.Equal(2, result.DrainMembers);
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal(5, result.Submission.Planned);
        Assert.Equal(5, result.Submission.Delivered);
        Assert.Equal(5, protocol.Deliveries.Count);
        Assert.Equal(5, protocol.Deliveries.Select(d => d.Key).Distinct().Count());

        // The intake members were stopped only once the grace had passed on the run's clock with none started; the drain's as
        // soon as this run had drained, the first time it asked after them.
        Assert.Equal([DeliveryOperations.Intake, DeliveryOperations.Drain], dispatcher.Cancelled.Select(c => c.Operation));
        Assert.True(dispatcher.Cancelled[0].After >= TimeSpan.FromMinutes(3), $"the intake members were stopped after {dispatcher.Cancelled[0].After}");
        Assert.Equal(TimeSpan.FromMinutes(1), dispatcher.Cancelled[1].After);

        var batches = await ledger.ListWorkBatchesAsync(result.Submission.SubmissionId, 10, 0);
        Assert.Equal(5, batches.Count);
        Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
    }

    /// <summary>
    /// A relative work location is a folder in the copy of the repository each node runs the flow from, which no other node
    /// reads, so a delivery with one stays on its own run whatever its size: nothing is enqueued, and it plans and sends it
    /// all.
    /// </summary>
    [Fact]
    public async Task A_delivery_whose_work_location_no_other_node_reaches_stays_on_one_run()
    {
        var tables = await EstateAsync();
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var local = Samples.InFolder(Samples.LocalFlow(_root), _root);
        var flow = local with
        {
            Source = local.Source with { Work = "../work" },
            Parallelism = local.Parallelism with { FanOut = 2, FanOutMinRecords = 1, BatchRecords = 1 },
        };
        var dispatcher = new UnstartedDispatcher(_clock, TimeSpan.FromMinutes(1));
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol), FanOut = dispatcher };
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "test";

        var result = await runtime.RunAsync(force: false);

        Assert.Equal((0, 0), (result.IntakeMembers, result.DrainMembers));
        Assert.Equal(0, dispatcher.Enqueued);
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal(5, result.Submission.Delivered);
        Assert.Equal(Path.Combine(_root, "work"), result.Submission.WorkLocation);
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

        /// <summary>Every fan-out cancelled, in order: its members' operation, and how long after it was enqueued.</summary>
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

    /// <summary>A fan-out that runs every member inline, on its own runtime over the same engine, as another node would.</summary>
    private sealed class InlineDispatcher : IFanOutDispatcher
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly FlowDefinition _flow;
        private readonly Dictionary<Guid, FanOutMemberState> _members = [];
        private readonly Dictionary<Guid, List<Guid>> _groups = [];

        public InlineDispatcher(FlowDefinition flow)
        {
            _flow = flow;
        }

        public EngineContext? Engine { get; set; }

        public List<(string Operation, DeliveryRunPayload Payload)> Enqueued { get; } = [];

        public bool Available => true;

        public async Task<FanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, CancellationToken ct = default)
        {
            var groupId = Guid.NewGuid();
            var ids = new List<Guid>();
            foreach (var member in members)
            {
                var operation = DeliveryOperations.Of(member);
                var payload = DeliveryRunPayload.Parse(member);
                payload.Validate(operation);
                Enqueued.Add((operation, payload));
                var runId = Guid.NewGuid();
                ids.Add(runId);
                string resultJson;
                if (operation == DeliveryOperations.Intake)
                {
                    // A member run reaches the executor, which takes the partition it names off its values and binds the flow to
                    // it: a member of a run bound to a partition names that partition, and is never a parameter of the flow.
                    var (partition, values) = PartitionNames.SplitRunValues(member.Values, keptAsParameter: false);
                    Assert.Equal(_flow.Partition, partition);
                    using var runtime = await FlowRuntime.CreateAsync(Engine!, _flow, values, ct);
                    runtime.RunId = runId;
                    runtime.SubmissionId = payload.SubmissionId;
                    runtime.Slices = payload.Slices;
                    var intake = await runtime.IntakeAsync(payload.Force, ct);
                    resultJson = JsonSerializer.Serialize(
                        IntakeOutcome.From(intake, _flow.Source.Record.Object, runtime.Selection.Describe(), payload.Slices), Json);
                }
                else
                {
                    using var runtime = FlowRuntime.ForTarget(Engine!, _flow);
                    runtime.RunId = runId;
                    var drained = await runtime.WorkAsync(once: false, payload.SubmissionId, ct);
                    resultJson = JsonSerializer.Serialize(DrainOutcome.From(drained, payload.SubmissionId), Json);
                }

                _members[runId] = new FanOutMemberState(runId, ids.Count, "succeeded", null, resultJson);
            }

            _groups[groupId] = ids;
            return new FanOutHandle(groupId, ids);
        }

        public Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
            => Task.FromResult(new FanOutState(_groups[handle.GroupId].Select(id => _members[id]).ToList()));

        public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default) => Task.CompletedTask;
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
