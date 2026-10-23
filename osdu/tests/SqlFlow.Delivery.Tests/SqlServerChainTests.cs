using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;
using SqlFlow.Execution;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;
using Xunit;
using Xunit.Abstractions;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The whole chain on a real SQL Server: files land through SQLFlow's pre-ingestion
/// flows, its ingestion flows key them into the tables the OSDU flow reads, and the delivery plans and sends them to a
/// fake target. Every flow runs through the platform's own document executor, in the wave order a run group puts them
/// in, so what these tests exercise is the path a scheduled chain takes rather than a test-only arrangement.
/// <para>Each test owns a fixture of its own (its schemas, its flow names, its ledger rows), so the suite is safe beside
/// the other SQL Server suites and safe run in parallel with itself.</para>
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public class SqlServerChainTests
{
    private readonly ITestOutputHelper _output;

    public SqlServerChainTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>The file the first batch of record rows lands from, which every record's origin then names.</summary>
    private const string LogFile = "welllog_20260901.csv";

    private const string CurveFile = "welllog_curves_20260901.csv";

    /// <summary>Case 1: the first run of the chain, from files to delivered records.</summary>
    [Fact]
    public async Task A_first_run_loads_the_tables_renders_the_records_kept_for_the_logs_and_delivers_every_log()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var logs = SampleWellLogs.Logs();
        await estate.WriteLogFileAsync(LogFile, logs);
        await estate.WriteCurveFileAsync(CurveFile, logs);
        await estate.WritePayloadsAsync(logs);

        // The chain runs in the order the run group gates it: both pre flows, then both ingestion flows, then the OSDU
        // flow. The group is what decides that order, and the executor runs each member. Its OSDU member is left to this
        // test to run, so that the plan below is read while the ledger still holds nothing, which is the moment a first
        // run of a flow is actually in.
        var order = await EnqueueChainAndRunIngestionAsync(estate);
        Assert.Equal(
            [estate.Rename("welldb-welllog-01-curves-pre"), estate.Rename("welldb-welllog-01-header-pre")],
            order.Where(m => m.Wave == 0).Select(m => m.FlowName).OrderBy(n => n, StringComparer.Ordinal).ToList());
        Assert.Equal(estate.DeliveryFlowName, order.Single(m => m.Wave == 2).FlowName);

        // What the ingestion flows left is what the delivery reads: one row per logging run, one per curve.
        Assert.Equal(5, await estate.CountAsync(estate.SilverSchema, "WellLog"));
        Assert.Equal(logs.Sum(l => l.Curves.Count), await estate.CountAsync(estate.SilverSchema, "WellLogCurve"));

        // The first plan over the loaded tables, with nothing delivered yet: every record is new, so every entry creates.
        // Rendered from the real tables, each document is the one the suites keep for the log (Fixtures/rendered), which is
        // what holds the typing of the ingestion columns (a decimal where the record says a number) to the record OSDU receives.
        var flow = estate.DeliveryFlow();
        using (var runtime = await FlowRuntime.CreateAsync(estate.Engine, flow, SampleEstate.Values))
        {
            runtime.Selection = SourceSelection.Full();
            var plan = await runtime.PlanAsync();
            Assert.Equal(5, plan.Entries.Count);
            Assert.All(plan.Entries, e => Assert.Equal(PlannedAction.Create, e.Action));
            Assert.All(plan.Entries, e => Assert.Equal(LogFile, e.Origin.FileName));
            Assert.All(plan.Entries, e => Assert.Equal(1, e.ChunkCount));
            // The suites keep a record for some of the sample logs, not for every one, so each log that has one is compared
            // and the count is asserted: a kept record whose log stopped being found would otherwise leave this comparing
            // nothing and still passing.
            var kept = RenderedRecord.Names(WellLogVersions.CurrentMapping);
            var compared = 0;
            foreach (var log in logs.Where(l => kept.Contains(KeptName(l))))
            {
                AssertRendersTheKeptRecord(plan.Entries.Single(e => e.Key == log.Key), log);
                compared++;
            }

            Assert.Equal(kept.Count, compared);
        }

        // The group's last member, run now that its first plan has been read.
        await estate.DeliverAsync();

        Assert.Equal(5, estate.Protocol.Deliveries.Count);
        Assert.All(estate.Protocol.Deliveries, w => Assert.True(w.DeliverMetadata && w.DeliverPayload));
        // The payload files each record points at were opened and streamed: the fake target reads every chunk it is
        // handed, so a payload source here is a payload the delivery actually sent.
        foreach (var work in estate.Protocol.Deliveries)
        {
            Assert.NotNull(work.Payload);
            var chunk = Assert.Single(await work.Payload!.ListChunksAsync());
            Assert.EndsWith(SampleWellLogs.ChunkFileName, chunk.Path, StringComparison.Ordinal);
        }

        var submission = await LatestSubmissionAsync(estate);
        Assert.Equal(SubmissionStatus.Completed, submission.Status);
        Assert.Equal(5, submission.Planned);
        Assert.Equal(5, submission.Delivered);
        Assert.Equal(estate.RecordObject, submission.SourceObject);

        // Every delivered record names the file and row it came from, which is the chain's traceability link.
        for (var i = 0; i < logs.Count; i++)
        {
            var record = await estate.Ledger.GetRecordAsync(estate.FlowId, logs[i].Key);
            Assert.NotNull(record);
            Assert.Equal(RecordStatus.Delivered, record!.Status);
            Assert.Equal(LogFile, record.SourceFileName);
            Assert.Equal(i + 1, record.SourceRowNumber);
            Assert.NotNull(record.TargetVersion);
            var attempt = Assert.Single(await estate.Ledger.ListAttemptsAsync(estate.FlowId, logs[i].Key, 5));
            Assert.Equal(AttemptOutcome.Delivered, attempt.Outcome);
            Assert.Equal(LogFile, attempt.SourceFileName);
        }

        // The whole-scope plan moved the scope's watermark, which is what the next run starts from.
        var watermark = await estate.Ledger.GetWatermarkAsync(estate.FlowId, Planner.ScopeKey(SampleEstate.Values));
        Assert.NotNull(watermark);
        Assert.Equal(submission.SubmissionId, watermark!.SubmissionId);
    }

    /// <summary>Case 2: a second run with no new file sends nothing and writes no attempt.</summary>
    [Fact]
    public async Task A_re_run_with_no_new_files_skips_the_window_and_writes_no_attempt()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var logs = SampleWellLogs.Logs();
        await estate.WriteLogFileAsync(LogFile, logs);
        await estate.WriteCurveFileAsync(CurveFile, logs);
        await estate.WritePayloadsAsync(logs);
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();
        Assert.Equal(5, estate.Protocol.Deliveries.Count);
        var attemptsBefore = await AttemptCountAsync(estate, logs);

        // Nothing new arrived, so the pre flows land nothing, the ingestion flows change no row, and the delivery's
        // window holds nothing: the run skips the scope without reading a record.
        estate.Protocol.Deliveries.Clear();
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();

        Assert.Empty(estate.Protocol.Deliveries);
        Assert.Equal(attemptsBefore, await AttemptCountAsync(estate, logs));
        var stats = await estate.Ledger.StatsAsync(estate.FlowId, DateTime.UtcNow);
        Assert.Equal(5, stats.Delivered);
    }

    /// <summary>Case 3: a changed curve re-plans its own log and nothing else, and the record's origin does not move.</summary>
    [Fact]
    public async Task A_changed_curve_updates_only_its_own_log_and_leaves_the_record_origin_on_the_log_file()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var logs = SampleWellLogs.Logs();
        await estate.WriteLogFileAsync(LogFile, logs);
        await estate.WriteCurveFileAsync(CurveFile, logs);
        await estate.WritePayloadsAsync(logs);
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();
        Assert.Equal(5, estate.Protocol.Deliveries.Count);

        // A second curve file describes one curve of the first log differently. The keyed upsert moves that child row
        // only, and the delivery's candidate query brings in the record its changed child belongs to.
        var changedCurveId = logs[0].Curves[1].CurveId;
        var changed = logs[0] with
        {
            Curves = [logs[0].Curves[0], logs[0].Curves[1] with { Description = "Gamma ray, corrected" }, logs[0].Curves[2]],
        };
        await estate.WriteCurveFileAsync("welllog_curves_20260902.csv", [changed]);

        estate.Protocol.Deliveries.Clear();
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();

        var delivered = Assert.Single(estate.Protocol.Deliveries);
        Assert.Equal(logs[0].Key, delivered.Key);

        // The record row itself never moved, so its origin still names the file that landed it.
        var record = await estate.Ledger.GetRecordAsync(estate.FlowId, logs[0].Key);
        Assert.Equal(LogFile, record!.SourceFileName);
        Assert.Equal(1, record.SourceRowNumber);
        Assert.Equal(RecordStatus.Delivered, record.Status);

        // The curve row carries the newer content and the newer change stamp.
        Assert.Equal(
            "Gamma ray, corrected",
            await CurveValueAsync(estate, "curve_description", logs[0].SourceProject, logs[0].LogId, changedCurveId));
    }

    /// <summary>Case 4: landing the same rows again changes nothing, because the ingestion checksum leaves them alone.</summary>
    [Fact]
    public async Task An_identical_re_land_leaves_the_ingestion_rows_and_the_record_origin_untouched()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var logs = SampleWellLogs.Logs();
        await estate.WriteLogFileAsync(LogFile, logs);
        await estate.WriteCurveFileAsync(CurveFile, logs);
        await estate.WritePayloadsAsync(logs);
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();
        var updatedBefore = await estate.ValueAsync("WellLog", "UpdatedDate_DW", logs[0].SourceProject, logs[0].LogId);

        // The same rows arrive under a new file name. The pre table gains them, but the ingestion upsert compares the
        // business columns, finds them identical and touches nothing: not the content, not UpdatedDate_DW, not the
        // origin columns. So the delivery's window holds nothing and the record keeps the file it came from.
        await estate.WriteLogFileAsync("welllog_20260903.csv", logs);
        await estate.WriteCurveFileAsync("welllog_curves_20260903.csv", logs);

        estate.Protocol.Deliveries.Clear();
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();

        Assert.Empty(estate.Protocol.Deliveries);
        Assert.Equal(5, await estate.CountAsync(estate.SilverSchema, "WellLog"));
        Assert.Equal(updatedBefore, await estate.ValueAsync("WellLog", "UpdatedDate_DW", logs[0].SourceProject, logs[0].LogId));
        Assert.Equal(LogFile, await estate.ValueAsync("WellLog", "FileName_DW", logs[0].SourceProject, logs[0].LogId));
        var record = await estate.Ledger.GetRecordAsync(estate.FlowId, logs[0].Key);
        Assert.Equal(LogFile, record!.SourceFileName);
    }

    /// <summary>Case 5: a row whose business version went backwards is a stale skip, never a delivery.</summary>
    [Fact]
    public async Task A_row_landed_with_an_older_business_version_is_skipped_as_stale_and_never_sent()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var logs = SampleWellLogs.Logs();
        await estate.WriteLogFileAsync(LogFile, logs);
        await estate.WriteCurveFileAsync(CurveFile, logs);
        await estate.WritePayloadsAsync(logs);
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();
        Assert.Equal(5, estate.Protocol.Deliveries.Count);

        // A late file carries changed content for one log under an older update_date. The ingestion flow takes it (the
        // content moved, so the row is updated and its change stamp moves with it), and the delivery refuses to send a
        // version older than the one it already delivered.
        var stale = logs[1] with { Creator = "STALE-SOURCE", UpdateDateUtc = logs[1].UpdateDateUtc.AddDays(-7) };
        await estate.WriteLogFileAsync("welllog_20260904.csv", [stale]);

        estate.Protocol.Deliveries.Clear();
        await estate.RunIngestionChainAsync();
        await estate.DeliverAsync();

        Assert.Empty(estate.Protocol.Deliveries);
        var submission = await LatestSubmissionAsync(estate);
        Assert.Equal(1, submission.SkippedStale);
        Assert.Equal(0, submission.Delivered);

        // The record still holds the version that was delivered, and the stale pass is on its history.
        var record = await estate.Ledger.GetRecordAsync(estate.FlowId, logs[1].Key);
        Assert.Equal(RecordStatus.Delivered, record!.Status);
        Assert.Equal(logs[1].UpdateDateUtc, record.SourceModifiedUtc);
        Assert.Equal(1, await estate.Ledger.CountAttemptsAsync(submission.SubmissionId, AttemptOutcome.Skipped, AttemptPhases.Stale));
    }

    /// <summary>Case 6: a fan-out over key slices plans every record exactly once and writes one watermark.</summary>
    [Fact]
    public async Task A_fan_out_over_key_slices_plans_every_record_once_with_disjoint_batches_and_one_watermark()
    {
        const int Records = 2000;
        await using var estate = await SqlServerIngestionFixture.StartAsync(fanOut: 2, batchRecords: 250);
        var template = SampleWellLogs.Logs()[0];
        await estate.WritePayloadsAsync([template]);
        await estate.WriteRowsAsync("welllog", LogFile, SampleWellLogs.LogColumns, GeneratedLogs(template, Records));
        await estate.WriteRowsAsync("curves-meta", CurveFile, SampleWellLogs.CurveColumns, GeneratedCurves(template, Records));
        await estate.RunIngestionChainAsync();
        Assert.Equal(Records, await estate.CountAsync(estate.SilverSchema, "WellLog"));

        // The fan-out needs a dispatcher and a run to belong to; the members run inline here, each on a runtime of its
        // own over the same engine, which is what another node would do with the slices it was dealt.
        var flow = estate.DeliveryFlow();
        var dispatcher = new InlineDispatcher(flow);
        var engine = estate.Engine with { FanOut = dispatcher };
        dispatcher.Engine = engine;
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "chain tests";

        var result = await runtime.RunAsync(force: false);

        Assert.Equal(2, result.IntakeMembers);
        Assert.Equal(SubmissionStatus.Completed, result.Submission.Status);
        Assert.Equal(Records, result.Submission.Planned);
        Assert.Equal(Records, result.Submission.Delivered);
        Assert.Equal(Records, estate.Protocol.Deliveries.Count);

        // Every record was planned exactly once: no key reached two members.
        Assert.Equal(Records, estate.Protocol.Deliveries.Select(d => d.Key).Distinct().Count());

        // Each member numbered its batches in a namespace of its own, so two members never wrote the same batch.
        var batches = await estate.Ledger.ListWorkBatchesAsync(result.Submission.SubmissionId, 1000, 0);
        Assert.Equal(result.Submission.BatchCount, batches.Count);
        Assert.Equal(batches.Count, batches.Select(b => b.Index).Distinct().Count());
        Assert.Contains(batches, b => b.Index >= SubmissionIntake.BatchBase(1));

        // The slices were cut on the table's identity primary key and dealt from one read, and the scope's watermark was
        // written once, by the coordinating run.
        var window = SourceWindowDescription.Parse(result.Submission.SourceWindowJson)!;
        Assert.Equal("RecId", window.SlicedOn);
        Assert.Equal(result.Submission.Slices, window.Slices.Count);
        var intakeMembers = dispatcher.Enqueued.Where(e => e.Operation == DeliveryOperations.Intake).ToList();
        Assert.Equal(2, intakeMembers.Count);
        Assert.Equal(
            intakeMembers.SelectMany(e => e.Payload.Slices).Distinct().Count(),
            intakeMembers.Sum(e => e.Payload.Slices.Count));
        await using var db = estate.Context();
        var watermarks = await db.DeliverySourceWatermarks.Where(w => w.FlowId == estate.FlowId).ToListAsync();
        var watermark = Assert.Single(watermarks);
        Assert.Equal(result.Submission.SubmissionId, watermark.SubmissionId);
    }

    /// <summary>
    /// Case 8: one input, two OSDU pipelines on two versions of the WellLog schema, both running at once, each fanned out
    /// over several nodes that push rows concurrently, and each keeping its own ledger.
    /// </summary>
    [Fact]
    public async Task One_input_fans_out_to_two_pipelines_on_two_schema_versions_whose_nodes_push_concurrently()
    {
        const int Records = 400;
        const int Nodes = 3;
        await using var estate = await SqlServerIngestionFixture.StartAsync(fanOut: Nodes, batchRecords: 40);
        var template = SampleWellLogs.Logs()[0];
        await estate.WritePayloadsAsync([template]);
        await estate.WriteRowsAsync("welllog", LogFile, SampleWellLogs.LogColumns, GeneratedLogs(template, Records));
        await estate.WriteRowsAsync("curves-meta", CurveFile, SampleWellLogs.CurveColumns, GeneratedCurves(template, Records));
        await estate.RunIngestionChainAsync();
        Assert.Equal(Records, await estate.CountAsync(estate.SilverSchema, "WellLog"));

        // The shipped flow delivers WellLog 1.4.0 to the sample partition; the second pipeline renders the same rows with
        // the same mapping entries as WellLog 1.5.0, into a partition of its own.
        var partition = "next-" + estate.Suffix;
        var current = estate.DeliveryFlow();
        var next = WellLogVersions.OnNextVersion(current, estate.DeliveryFlowName + "-next", partition, estate.Root);
        try
        {
            await WellLogVersions.ImportPartitionCacheAsync(new OsduCacheStore(estate.Context), partition, estate.DeliveryFlowName + "-cache");

            // Each push takes a moment, so the pushes a node makes at once overlap.
            estate.Protocol.Before = (_, ct) => Task.Delay(TimeSpan.FromMilliseconds(15), ct);
            var runs = await Task.WhenAll(RunOnNodesAsync(estate, current), RunOnNodesAsync(estate, next));

            await using var db = estate.Context();
            foreach (var (run, flow, kind, target) in new[]
            {
                (runs[0], current, WellLogVersions.CurrentKind, Samples.SamplePartition),
                (runs[1], next, WellLogVersions.NextKind, partition),
            })
            {
                // Planned by the coordinator and its intake nodes, sent by the drain nodes and the coordinator: every row once.
                Assert.Empty(run.Nodes.Failures);
                Assert.Equal((Nodes, Nodes), (run.Result.IntakeMembers, run.Result.DrainMembers));
                Assert.Equal(SubmissionStatus.Completed, run.Result.Submission.Status);
                Assert.Equal((Records, Records), (run.Result.Submission.Planned, run.Result.Submission.Delivered));
                var sent = estate.Protocol.Deliveries.Where(d => d.Document["kind"]!.GetValue<string>() == kind).ToList();
                Assert.Equal(Records, sent.Count);
                Assert.Equal(Records, sent.Select(d => d.Key).Distinct().Count());
                Assert.All(sent, d => Assert.StartsWith(target + ":work-product-component--WellLog:", d.TargetId, StringComparison.Ordinal));

                // The nodes ran at the same time, more than one of them pushed, and a node pushed several rows at once.
                Assert.True(run.Nodes.MaxRunning >= 2, $"{flow.Name}: at most {run.Nodes.MaxRunning} node(s) ran at once.");
                var pushed = estate.Protocol.DeliveriesByNode.Where(n => n.Key.StartsWith(flow.Name + "/", StringComparison.Ordinal)).OrderBy(n => n.Key, StringComparer.Ordinal).ToList();
                var peaks = estate.Protocol.MaxInFlightByNode;
                _output.WriteLine($"{flow.Name} ({kind}): {run.Nodes.MaxRunning} member node(s) at once; " + string.Join(", ", pushed.Select(n => $"{n.Key} pushed {n.Value}, at most {peaks[n.Key]} at once")));
                Assert.Equal(Records, pushed.Sum(n => n.Value));
                Assert.True(pushed.Count >= 2, $"{flow.Name}: only {string.Join(", ", pushed.Select(n => n.Key))} pushed.");
                var inFlight = estate.Protocol.MaxInFlightByNode.Where(n => n.Key.StartsWith(flow.Name + "/", StringComparison.Ordinal)).Max(n => n.Value);
                Assert.True(inFlight > 1, $"{flow.Name}: no node pushed more than one row at a time.");
                var batches = await estate.Ledger.ListWorkBatchesAsync(run.Result.Submission.SubmissionId, 1000, 0);
                Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
                Assert.True(batches.Select(b => b.RunId).Distinct().Count() >= 2, $"{flow.Name}: every batch was drained by one run.");

                // Each pipeline's ledger holds exactly its own records, claims and attempts, whatever the other did at the same time.
                var flowId = flow.Id;
                Assert.Equal(Records, (await estate.Ledger.StatsAsync(flowId, DateTime.UtcNow)).Delivered);
                Assert.Equal(Records, await db.DeliveryRecords.CountAsync(r => r.FlowId == flowId && r.ClaimedTargetId != null && r.ClaimedTargetId.StartsWith(target + ":")));
                Assert.Equal(Records, await db.DeliveryAttempts.CountAsync(a => a.FlowId == flowId && a.Outcome == "delivered"));
                Assert.Single(await db.DeliverySourceWatermarks.Where(w => w.FlowId == flowId).ToListAsync());
            }
        }
        finally
        {
            await estate.ForgetFlowAsync(next.Id);
            await estate.ForgetCacheAsync(partition);
        }
    }

    /// <summary>
    /// Case 9: a worker stalls on a record past the end of its lease, and the next claim of the flow recovers the lease.
    /// When the stalled worker next renews, it finds the lease no longer its own and stops sending; what it held is back
    /// in the queue, and the next worker sends the whole batch.
    /// </summary>
    [Fact]
    public async Task A_worker_whose_lease_was_recovered_while_it_stalled_stops_sending_and_the_next_worker_sends_the_batch()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var (flow, logs, submissionId) = await PlanOneRecordAtATimeAsync(estate);
        var recovered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        estate.Protocol.Before = async (_, ct) =>
        {
            if (!recovered.Task.IsCompleted)
            {
                // Ten minutes on, as far as the recovery is concerned: the stalled worker's lease has run out.
                try
                {
                    recovered.TrySetResult(await estate.Ledger.RecoverExpiredLeasesAsync(flow.Id, DateTime.UtcNow.AddMinutes(10), CancellationToken.None));
                }
                catch (Exception ex)
                {
                    recovered.TrySetException(ex);
                    throw;
                }
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };

        var summary = await KeptWorker(estate, flow, "stalled", CompositeDeliveryListener.Empty).PassAsync(submissionId).WaitAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(logs.Count, await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal((0L, 1), (summary.Processed, summary.Batches));
        var interrupted = Assert.Single(estate.Protocol.Deliveries);
        foreach (var log in logs)
        {
            var record = await estate.Ledger.GetRecordAsync(flow.Id, log.Key);
            Assert.Equal((RecordStatus.Pending, (string?)null), (record!.Status, record.LeaseOwner));
            var tried = Assert.Single(await estate.Ledger.ListAttemptsAsync(flow.Id, log.Key, 10));
            Assert.Equal(AttemptOutcome.Failed, tried.Outcome);
            Assert.Contains("lease expired", tried.Error, StringComparison.Ordinal);
        }

        Assert.Contains("lease expired", (await estate.Ledger.GetRecordAsync(flow.Id, interrupted.Key))!.LastError, StringComparison.Ordinal);
        Assert.Null(await estate.Ledger.NextLeaseExpiryAsync(flow.Id, submissionId));
        Assert.Equal(1, await estate.Ledger.CountWorkBatchesAsync(submissionId, WorkBatchStatus.Queued));

        estate.Protocol.Before = null;
        estate.Protocol.Deliveries.Clear();
        var next = await KeptWorker(estate, flow, "next", CompositeDeliveryListener.Empty).DrainAsync(submissionId);
        Assert.Equal(logs.Count, next.Delivered);
        Assert.Equal(logs.Count, estate.Protocol.Deliveries.Count);
    }

    /// <summary>
    /// Case 10: while a batch runs, each renewal of its lease applies what the worker has sent so far, so the records show
    /// their outcomes before the batch ends, and the batch's progress reaches the run's trace.
    /// </summary>
    [Fact]
    public async Task A_running_batch_applies_what_it_sent_at_each_renewal_and_reports_its_progress()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();
        var (flow, logs, submissionId) = await PlanOneRecordAtATimeAsync(estate);

        // The last record is held until the ones sent before it show as delivered: only a checkpoint of the running
        // batch's lease can have applied them.
        var sent = new List<Identity.DeliveryKey>();
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        estate.Protocol.Before = async (work, ct) =>
        {
            if (sent.Count < logs.Count - 1)
            {
                sent.Add(work.Key);
                return;
            }

            while (true)
            {
                var states = await estate.Ledger.GetRecordsAsync(flow.Id, sent, ct);
                if (states.Values.All(s => s.Status == RecordStatus.Delivered))
                {
                    applied.TrySetResult();
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
            }
        };

        var events = new RecordingListener();
        var summary = await KeptWorker(estate, flow, "progress", events).DrainAsync(submissionId).WaitAsync(TimeSpan.FromMinutes(1));
        await applied.Task;
        Assert.Equal(logs.Count, summary.Delivered);
        var progress = events.Events.Where(e => e.Kind == "batch.progress").ToList();
        Assert.Contains(progress, e => e.Detail!.StartsWith($"{logs.Count - 1} of {logs.Count} record(s) settled so far", StringComparison.Ordinal));
        Assert.All(progress, e => Assert.Equal(submissionId, e.SubmissionId));
        Assert.Equal(logs.Count, events.Events.Count(e => e.Kind == "record.delivered"));
        Assert.Single(events.Events, e => e.Kind == "batch.completed");
        Assert.Null(await estate.Ledger.NextLeaseExpiryAsync(flow.Id, submissionId));
    }

    /// <summary>
    /// Case 11: one source document delivers the wellbores its chain loaded and the well logs that wait for them
    /// (osdu/docs/reference/flow/interfaces.md). The run goes through the platform's document executor with a fan-out,
    /// as a node runs it: each interface plans and sends across member runs that the same executor runs beside it, each
    /// member told which interface it works on, and each interface keeps a ledger of its own.
    /// </summary>
    [Fact]
    public async Task A_source_fans_each_interface_out_over_nodes_in_the_order_its_interfaces_wait_for_each_other()
    {
        const int Wellbores = 60;
        const int Logs = 120;
        const int Nodes = 2;
        await using var estate = await SqlServerIngestionFixture.StartAsync(wellboreChain: true);
        var template = SampleWellLogs.Logs()[0];
        await estate.WritePayloadsAsync([template]);
        await estate.WriteRowsAsync("welllog", LogFile, SampleWellLogs.LogColumns, GeneratedLogs(template, Logs));
        await estate.WriteRowsAsync("curves-meta", CurveFile, SampleWellLogs.CurveColumns, GeneratedCurves(template, Logs));
        await estate.WriteWellboreFilesAsync(GeneratedWellbores(Wellbores), GeneratedAliases(Wellbores));
        await estate.RunWellboreChainAsync();
        await estate.RunIngestionChainAsync();
        Assert.Equal(Wellbores, await estate.CountAsync(estate.SilverSchema, "Wellbore"));
        Assert.Equal(2 * Wellbores, await estate.CountAsync(estate.SilverSchema, "WellboreAlias"));
        Assert.Equal(Logs, await estate.CountAsync(estate.SilverSchema, "WellLog"));

        var name = estate.FlowPrefix + "-source";
        var file = Path.Combine(estate.FlowsDirectory, name + ".yaml");
        await File.WriteAllTextAsync(file, SourceDocument(estate, name, Nodes));
        var ledgers = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["wellbores"] = Identity.FlowId.Of(name + "/wellbores"),
            ["welllogs"] = Identity.FlowId.Of(name + "/welllogs"),
        };

        // A coordinating run holds its own sends until a node of the same interface has started sending, so every
        // interface's drain is shared with its nodes however quickly the coordinator reaches its first batch.
        var nodeSent = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        TaskCompletionSource NodeSent(string kind) => nodeSent.GetOrAdd(kind, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        estate.Protocol.Before = async (work, ct) =>
        {
            var kind = work.Document["kind"]!.GetValue<string>();
            if (FakeProtocol.CurrentNode.Value is null)
            {
                await Task.WhenAny(NodeSent(kind).Task, Task.Delay(TimeSpan.FromSeconds(30), ct));
            }
            else
            {
                NodeSent(kind).TrySetResult();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
        };

        try
        {
            var nodes = new NodePool(estate, file, _output.WriteLine);
            var outcome = await estate.Documents.RunAsync(
                file,
                new DocumentExecutionOptions
                {
                    RunId = Guid.NewGuid(),
                    Actor = "chain tests",
                    FanOut = nodes,
                    Parameters = new RunParameters { Operation = DeliveryOperations.Deliver, Values = SampleEstate.Values },
                }).WaitAsync(TimeSpan.FromMinutes(5));

            Assert.True(outcome.Success, $"{outcome.FlowName}: {outcome.Error}");
            Assert.Empty(nodes.Failures);

            // The wellbores went in the first wave and the well logs, which wait for them, in the second; each planned and
            // sent across its nodes.
            var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outcome.RunDirectory!, "run.json"))).RootElement;
            var interfaces = artifact.GetProperty("result").GetProperty("interfaces").EnumerateArray().ToList();
            Assert.Equal(
                [("wellbores", 1, "storage", InterfaceStates.Completed), ("welllogs", 2, "ddms", InterfaceStates.Completed)],
                interfaces.Select(i => (i.GetProperty("interface").GetString(), i.GetProperty("wave").GetInt32(), i.GetProperty("route").GetString(), i.GetProperty("state").GetString())).ToArray());
            Assert.Equal(Wellbores + Logs, artifact.GetProperty("result").GetProperty("rowsLoaded").GetInt64());
            foreach (var item in interfaces)
            {
                var result = item.GetProperty("result");
                Assert.Equal((Nodes, Nodes), (result.GetProperty("intakeMembers").GetInt32(), result.GetProperty("drainMembers").GetInt32()));
            }

            // Every member was told the interface it works on, and each interface had both kinds of member.
            foreach (var iface in ledgers.Keys)
            {
                var members = nodes.Enqueued.Where(m => m.Payload.Interface == iface).ToList();
                Assert.Equal(Nodes, members.Count(m => m.Operation == DeliveryOperations.Intake));
                Assert.Equal(Nodes, members.Count(m => m.Operation == DeliveryOperations.Drain));
            }

            Assert.Equal(4 * Nodes, nodes.Enqueued.Count);

            // Every record was sent once, every wellbore before the first well log.
            var sent = estate.Protocol.Deliveries.Select(d => d.Document["kind"]!.GetValue<string>()).ToList();
            Assert.Equal(Wellbores, sent.Count(k => k == Samples.WellboreKind));
            Assert.Equal(Logs, sent.Count(k => k == Samples.WellLogKind));
            Assert.True(
                sent.LastIndexOf(Samples.WellboreKind) < sent.IndexOf(Samples.WellLogKind),
                "a well log was sent before the last wellbore, although the well logs wait for the wellbores.");
            Assert.Equal(Wellbores + Logs, estate.Protocol.Deliveries.Select(d => d.TargetId).Distinct(StringComparer.Ordinal).Count());

            // Each interface's ledger holds exactly its own records, sent by more than one run.
            await using var db = estate.Context();
            foreach (var (iface, flowId, count) in new[] { ("wellbores", ledgers["wellbores"], Wellbores), ("welllogs", ledgers["welllogs"], Logs) })
            {
                Assert.Equal(count, (await estate.Ledger.StatsAsync(flowId, DateTime.UtcNow)).Delivered);
                Assert.Equal(count, await db.DeliveryAttempts.CountAsync(a => a.FlowId == flowId && a.Outcome == "delivered"));
                var submission = Assert.Single(await estate.Ledger.ListSubmissionsAsync(flowId, 10));
                Assert.Equal((name + "/" + iface, SubmissionStatus.Completed, (long)count), (submission.FlowName, submission.Status, submission.Delivered));
                var batches = await estate.Ledger.ListWorkBatchesAsync(submission.SubmissionId, 1000, 0);
                Assert.All(batches, b => Assert.Equal(WorkBatchStatus.Done, b.Status));
                Assert.True(batches.Select(b => b.RunId).Distinct().Count() >= 2, $"{iface}: every batch was drained by one run.");
                Assert.Single(await db.DeliverySourceWatermarks.Where(w => w.FlowId == flowId).ToListAsync());
            }
        }
        finally
        {
            foreach (var flowId in ledgers.Values)
            {
                await estate.ForgetFlowAsync(flowId);
            }
        }
    }

    /// <summary>
    /// The source document of case 11: the fixture's wellbore and well log tables as two interfaces of one source, the
    /// well logs waiting for the wellbores and both fanned out over <paramref name="nodes"/> member runs.
    /// </summary>
    private static string SourceDocument(SqlServerIngestionFixture estate, string name, int nodes)
    {
        string Table(string table) => $"\"[{estate.DatabaseName}].[{estate.SilverSchema}].[{table}]\"";
        return $$"""
            flowType: delivery
            name: {{name}}
            parameters:
              logSource: { required: true }
            source:
              connection: ${env:{{estate.ConnectionVariable}}}
              lastModified: update_date
              work: ../.work/{{name}}/{logSource}
            render:
              parameters:
                dataPartition: dev
                aclOwner: data.welldb.owners@dev.dataservices.energy
                aclViewer: data.welldb.viewers@dev.dataservices.energy
                legalTag: dev-osdu-default-legal
            target:
              endpoint: http://localhost:9/facade-api
              auth:
                type: none
              headers:
                data-partition-id: {{Samples.SampleCacheScope}}
              protocolOptions:
                ddmsRoot: /api/os-wellbore-ddms
            parallelism:
              concurrency: 8
              batchSize: 50
              batchRecords: 20
              fanOut: {{nodes.ToString(CultureInfo.InvariantCulture)}}
              fanOutMinRecords: 1
              interfaces: 2
            interfaces:
              wellbores:
                record: { object: {{Table("Wellbore")}}, key: [facility_name], primaryKey: RecId }
                datasets:
                  aliases: { object: {{Table("WellboreAlias")}}, join: { facility_name: facility_name }, orderBy: [alias_name] }
                mapping: Wellbore@1.0.0
              welllogs:
                record: { object: {{Table("WellLog")}}, key: [source_project, log_id], primaryKey: RecId, scope: { log_source: logSource } }
                datasets:
                  curves: { object: {{Table("WellLogCurve")}}, join: { source_project: source_project, log_id: log_id }, orderBy: [curve_ordinal] }
                bulk: { root: ../data/curves, locationColumn: curve_folder, pattern: "chunk_*.parquet", hashColumn: payload_hash, chunkCountColumn: chunk_count }
                protocolOptions: { sessionThresholdChunks: 1, preserveDataKeys: [Datasets, DDMSDatasets, ExtensionProperties] }
                mapping: WellLog@1.4.0
                after: [wellbores]
            """.ReplaceLineEndings("\n");
    }

    /// <summary>The wellbore rows of a generated estate, one per wellbore.</summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> GeneratedWellbores(int count)
    {
        var updated = FixtureWellbores.UpdatedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return Enumerable.Range(0, count)
            .Select(i => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["facility_name"] = string.Create(CultureInfo.InvariantCulture, $"WB-{i:D5}"),
                ["facility_description"] = string.Create(CultureInfo.InvariantCulture, $"Generated wellbore {i}"),
                ["facility_id"] = string.Create(CultureInfo.InvariantCulture, $"srn:master-data/Wellbore:G{i}"),
                ["update_date"] = updated,
            })
            .ToList();
    }

    /// <summary>Two alternative names per generated wellbore.</summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> GeneratedAliases(int count)
        => Enumerable.Range(0, count)
            .SelectMany(i => new[] { "A", "B" }.Select(alias => (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["facility_name"] = string.Create(CultureInfo.InvariantCulture, $"WB-{i:D5}"),
                ["alias_name"] = string.Create(CultureInfo.InvariantCulture, $"{alias}-{i:D5}"),
            }))
            .ToList();

    /// <summary>
    /// Loads the sample rows and plans them into one batch of the delivery flow, sent one record at a time, without
    /// delivering anything.
    /// </summary>
    private static async Task<(FlowDefinition Flow, IReadOnlyList<SampleLog> Logs, Guid SubmissionId)> PlanOneRecordAtATimeAsync(SqlServerIngestionFixture estate)
    {
        var logs = SampleWellLogs.Logs();
        await estate.WriteLogFileAsync(LogFile, logs);
        await estate.WriteCurveFileAsync(CurveFile, logs);
        await estate.WritePayloadsAsync(logs);
        await estate.RunIngestionChainAsync();
        var shipped = estate.DeliveryFlow();
        var flow = shipped with { Parallelism = shipped.Parallelism with { Concurrency = 1 } };
        using var runtime = await FlowRuntime.CreateAsync(estate.Engine, flow, SampleEstate.Values);
        runtime.Selection = SourceSelection.Full();
        var intake = await runtime.IntakeAsync(force: false);
        Assert.True(intake.Submission.Planned == logs.Count, $"The intake planned {intake.Submission.Planned} of the {logs.Count} sample records: {intake.Counts}.");
        Assert.Equal(1, intake.Submission.BatchCount);
        return (flow, logs, intake.Submission.SubmissionId);
    }

    /// <summary>A worker over the fixture's ledger and target whose lease is renewed, and checkpointed, every 20 ms.</summary>
    private static DeliveryWorker KeptWorker(SqlServerIngestionFixture estate, FlowDefinition flow, string name, IDeliveryListener listener)
        => new(estate.Ledger, estate.Engine.Payloads, estate.Engine.Stores, estate.Protocol, flow, TimeProvider.System, listener, Samples.Logger<DeliveryWorker>(), name)
        {
            MaxWait = null,
            KeepInterval = TimeSpan.FromMilliseconds(20),
        };

    /// <summary>
    /// Runs the deliver operation of <paramref name="flow"/> as a coordinating run whose members are nodes of their own,
    /// all running at once. The coordinator's own pushes are counted under its node name.
    /// </summary>
    private async Task<(RunResult Result, ConcurrentNodes Nodes)> RunOnNodesAsync(SqlServerIngestionFixture estate, FlowDefinition flow)
    {
        var nodes = new ConcurrentNodes(flow, _output.WriteLine);
        var engine = estate.Engine with { FanOut = nodes };
        nodes.Engine = engine;
        FakeProtocol.CurrentNode.Value = flow.Name + "/coordinator";
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        runtime.RunId = Guid.NewGuid();
        runtime.Actor = "chain tests";
        return (await runtime.RunAsync(force: false), nodes);
    }

    /// <summary>Case 7: lineage orders the estate's waves pre, then ingestion, then the OSDU flow.</summary>
    [Fact]
    public async Task Lineage_orders_the_chain_pre_then_ingestion_then_the_osdu_flow()
    {
        await using var estate = await SqlServerIngestionFixture.StartAsync();

        // The collector parses with the module's kinds registered, so the delivery document is read and its declared
        // reads of the ingestion tables are what put it after the flows that write them.
        var loader = YamlDocumentLoader.CreateDefault([new DeliveryFlowKind(new DeliveryDocumentLoader())]);
        var collected = new FlowSetCollector(loader).Collect(estate.FlowsDirectory);
        var report = LineageGraphBuilder.Build(collected, estate.FlowsDirectory, [LineageTier.Declared], DateTime.UtcNow);

        Assert.Empty(report.ExecutionPlan.Unordered);
        var waves = report.ExecutionPlan.Waves.ToList();
        int WaveOf(string shipped)
        {
            var name = estate.Rename(shipped);
            var wave = waves.FirstOrDefault(w => w.Flows.Contains(name, StringComparer.OrdinalIgnoreCase));
            Assert.True(wave is not null, $"'{name}' is in no wave of the plan; waves: {Describe(waves)}.");
            return wave!.Wave;
        }

        var pre = WaveOf("welldb-welllog-01-header-pre");
        var curvesPre = WaveOf("welldb-welllog-01-curves-pre");
        var ing = WaveOf("welldb-welllog-02-header-ing");
        var curvesIng = WaveOf("welldb-welllog-02-curves-ing");
        var delivery = WaveOf("welldb-welllog-03-header-delivery");

        Assert.True(pre < ing, $"the pre flow must run before the ingestion flow that reads its view; waves: {Describe(waves)}");
        Assert.True(curvesPre < curvesIng, $"the curve pre flow must run before its ingestion flow; waves: {Describe(waves)}");
        Assert.True(ing < delivery, $"the OSDU flow must run after the ingestion flow that writes its record table; waves: {Describe(waves)}");
        Assert.True(curvesIng < delivery, $"the OSDU flow must run after the ingestion flow that writes its child table; waves: {Describe(waves)}");
    }

    /// <summary>
    /// Every generated document is one the platform can read. A three-part name rewritten for the test database begins
    /// with '[', which a YAML plain scalar may not, so the generator writes it quoted; this is what keeps that true, and
    /// it needs no database to say so.
    /// </summary>
    [Fact]
    public void The_generated_chain_documents_parse_as_the_platform_reads_them()
    {
        const string Database = "OsduDeliveryTest";
        const string Suffix = "abc123de";
        var root = Samples.NewTempDirectory();
        try
        {
            SqlServerIngestionFixture.GenerateEstate(root, Database, Suffix, "SQLFLOW_CHAIN_DB_" + Suffix, fanOut: 2, batchRecords: 250);

            // The loader the platform reads a repository with, knowing the module's kinds, so the delivery document is
            // parsed by the same code a node would parse it with.
            var documents = YamlDocumentLoader.CreateDefault([new DeliveryFlowKind(new DeliveryDocumentLoader())]);
            var files = Directory.EnumerateFiles(Path.Combine(root, "flows"), "*.yaml").OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.Equal(5, files.Count);
            foreach (var file in files)
            {
                Assert.NotNull(DocumentLoader.Load(documents, file));
            }

            // The tables the OSDU flow reads are this database's, and they still parse as three-part names.
            var flow = new DeliveryDocumentLoader().LoadFlow(Path.Combine(root, "flows", "rw" + Suffix + ".yaml"));
            var record = SourceObjectName.Parse(flow.Source.Record.Object);
            Assert.Equal(Database, record.Database);
            Assert.Equal("silver_" + Suffix, record.Schema);
            Assert.Equal("WellLog", record.Name);
            var curves = SourceObjectName.Parse(flow.Source.Datasets["curves"].Object);
            Assert.Equal("silver_" + Suffix, curves.Schema);
            Assert.Equal("WellLogCurve", curves.Name);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A generated document a reader still holds open is left for the operating system's own cleanup.
            }
        }
    }

    private static string Describe(IReadOnlyList<LineageWave> waves)
        => string.Join(" | ", waves.Select(w => w.Wave.ToString(CultureInfo.InvariantCulture) + ": " + string.Join(", ", w.Flows)));

    /// <summary>
    /// Enqueues the whole chain as one wave-gated run group and runs the flows that load the ingestion tables through the
    /// document executor, in the order the queue put them in. The group is the platform's own ordering, so a test never
    /// decides for itself what runs first; its rows are removed again whatever happens.
    /// <para>The OSDU member is enqueued with the rest but not run here, and the queue's order is returned so the caller
    /// can see where it sits. A caller that runs it itself can read the flow's first plan before anything is delivered,
    /// which a helper that delivered on its way through would have made unobservable.</para>
    /// </summary>
    private static async Task<IReadOnlyList<(string FlowName, int Wave)>> EnqueueChainAndRunIngestionAsync(SqlServerIngestionFixture estate)
    {
        var connectionString = estate.ConnectionString;
        await CatalogDatabase.MigrateAsync(connectionString);
        var repoId = FlowIdentity.FromName("chain_" + estate.Suffix);
        var shipped = new Dictionary<string, string>(StringComparer.Ordinal);
        var members = new List<RunScopeMember>();
        foreach (var (name, kind, wave) in Chain())
        {
            var generated = estate.Rename(name);
            shipped[generated] = name;
            members.Add(new RunScopeMember(generated, kind, wave, CatalogPipeline.DefaultBatch));
        }

        try
        {
            List<(string FlowName, int Wave)> queued;
            await using (var db = CatalogDatabase.Create(connectionString))
            {
                var now = DateTime.UtcNow;
                foreach (var member in members)
                {
                    db.Pipelines.Add(new CatalogPipeline
                    {
                        Id = CatalogIdentity.Pipeline(repoId, member.FlowName),
                        RepoId = repoId,
                        Name = member.FlowName,
                        Kind = member.FlowKind,
                        Batch = member.Batch,
                        RelativePath = "flows/" + member.FlowName + ".yaml",
                        Active = true,
                        Wave = member.Wave,
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    });
                }

                await db.SaveChangesAsync();
                var group = await RunQueueStore.EnqueueGroupAsync(
                    db, new RunGroupEnqueueRequest(repoId, RunGroupModes.Node, members[0].FlowName, members), now);
                Assert.Equal(members.Count, group.RunIds.Count);
                queued = await db.Runs.AsNoTracking()
                    .Where(r => r.GroupId == group.GroupId)
                    .OrderBy(r => r.GroupWave).ThenBy(r => r.FlowName)
                    .Select(r => new ValueTuple<string, int>(r.FlowName, r.GroupWave))
                    .ToListAsync();
            }

            foreach (var (flowName, _) in queued)
            {
                var name = shipped[flowName];
                if (name != "welldb-welllog-03-header-delivery")
                {
                    await estate.RunFlowAsync(name);
                }
            }

            return queued;
        }
        finally
        {
            await using var db = CatalogDatabase.Create(connectionString);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>The chain's members: the flow as the repository names it, its kind, and the wave lineage puts it in.</summary>
    private static IReadOnlyList<(string Name, string Kind, int Wave)> Chain() =>
    [
        ("welldb-welllog-01-header-pre", "file", 0),
        ("welldb-welllog-01-curves-pre", "file", 0),
        ("welldb-welllog-02-header-ing", "ing", 1),
        ("welldb-welllog-02-curves-ing", "ing", 1),
        ("welldb-welllog-03-header-delivery", FlowDefinition.FlowTypeName, 2),
    ];

    /// <summary>The name the suites keep a log's record under: its id, with the slash a file name cannot hold as an underscore.</summary>
    private static string KeptName(SampleLog log) => log.LogId.Replace('/', '_');

    /// <summary>
    /// Asserts that what the chain rendered from the real tables is exactly the record the suites keep for the log, compared
    /// canonically so the order the two were written in never decides the outcome, and an empty list and one left out alike.
    /// </summary>
    private static void AssertRendersTheKeptRecord(PlanEntry entry, SampleLog log)
    {
        Assert.NotNull(entry.Render);
        Assert.Empty(entry.Render!.Holds);
        Assert.Equal(
            Canonical(RenderedRecord.Comparable(JsonNode.Parse(RenderedRecord.Expected(WellLogVersions.CurrentMapping, KeptName(log))))),
            Canonical(RenderedRecord.Comparable(entry.Render.Document)));
    }

    /// <summary>One JSON document as a comparable text: object members in name order, array order left as it is.</summary>
    private static string Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var ordered = new JsonObject();
                foreach (var (name, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    ordered[name] = value is null ? null : JsonNode.Parse(Canonical(value));
                }

                return ordered.ToJsonString();
            case JsonArray array:
                var items = new JsonArray();
                foreach (var item in array)
                {
                    items.Add(item is null ? null : JsonNode.Parse(Canonical(item)));
                }

                return items.ToJsonString();
            default:
                return node?.ToJsonString() ?? "null";
        }
    }

    private static async Task<SubmissionState> LatestSubmissionAsync(SqlServerIngestionFixture estate)
    {
        var submissions = await estate.Ledger.ListSubmissionsAsync(estate.FlowId, 20);
        Assert.NotEmpty(submissions);
        return submissions.OrderByDescending(s => s.ReceivedUtc).First();
    }

    private static async Task<int> AttemptCountAsync(SqlServerIngestionFixture estate, IReadOnlyList<SampleLog> logs)
    {
        var total = 0;
        foreach (var log in logs)
        {
            total += (await estate.Ledger.ListAttemptsAsync(estate.FlowId, log.Key, 50)).Count;
        }

        return total;
    }

    private static async Task<object?> CurveValueAsync(SqlServerIngestionFixture estate, string column, string project, string logId, string curveId)
    {
        await using var db = estate.Context();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SET QUOTED_IDENTIFIER ON; SELECT [{column}] FROM [{estate.SilverSchema}].[WellLogCurve] WHERE [source_project] = @p AND [log_id] = @l AND [curve_id] = @c;";
        AddParameter(command, "@p", project);
        AddParameter(command, "@l", logId);
        AddParameter(command, "@c", curveId);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// The record rows of a generated estate: one logging run per record, all pointing at the same payload folder, so a
    /// fan-out has thousands of keys to slice without thousands of folders behind them.
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> GeneratedLogs(SampleLog template, int count)
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>(count);
        for (var i = 0; i < count; i++)
        {
            var row = new Dictionary<string, string?>(SampleWellLogs.LogRow(template), StringComparer.Ordinal);
            var logId = string.Create(CultureInfo.InvariantCulture, $"L-{i:D5}");
            row["log_id"] = logId;
            row["native_uid"] = template.SourceProject + ":" + logId;
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>One index curve per generated record, which is what its document's reference curve is read from.</summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, string?>> GeneratedCurves(SampleLog template, int count)
    {
        var first = SampleWellLogs.CurveRows(template)[0];
        var rows = new List<IReadOnlyDictionary<string, string?>>(count);
        for (var i = 0; i < count; i++)
        {
            var row = new Dictionary<string, string?>(first, StringComparer.Ordinal);
            row["log_id"] = string.Create(CultureInfo.InvariantCulture, $"L-{i:D5}");
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// A fan-out whose members run inline, each on its own runtime over the same engine, as another node of the pool
    /// would run the slices it was dealt.
    /// </summary>
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
            ArgumentNullException.ThrowIfNull(members);
            var groupId = Guid.NewGuid();
            var ids = new List<Guid>();
            foreach (var member in members)
            {
                var runId = Guid.NewGuid();
                ids.Add(runId);
                var (operation, payload, resultJson) = await RunMemberAsync(Engine!, _flow, member, runId, ct);
                Enqueued.Add((operation, payload));
                _members[runId] = new FanOutMemberState(runId, ids.Count, "succeeded", null, resultJson);
            }

            _groups[groupId] = ids;
            return new FanOutHandle(groupId, ids);
        }

        public Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handle);
            return Task.FromResult(new FanOutState(_groups[handle.GroupId].Select(id => _members[id]).ToList()));
        }

        public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default) => Task.CompletedTask;

        /// <summary>
        /// Runs one member as a node runs it: an intake of the slices it was dealt, or a drain of the submission, on a runtime
        /// of its own over <paramref name="engine"/>. Returns what the node journals as the member's result.
        /// </summary>
        public static async Task<(string Operation, DeliveryRunPayload Payload, string ResultJson)> RunMemberAsync(
            EngineContext engine, FlowDefinition flow, RunParameters member, Guid runId, CancellationToken ct)
        {
            var operation = DeliveryOperations.Of(member);
            var payload = DeliveryRunPayload.Parse(member);
            payload.Validate(operation);
            if (operation == DeliveryOperations.Intake)
            {
                // A member run reaches the executor, which takes the partition it names off its values and binds the flow to
                // it: a member of a run bound to a partition names that partition, and is never a parameter of the flow.
                var (partition, values) = PartitionNames.SplitRunValues(member.Values, keptAsParameter: false);
                Assert.Equal(flow.Partition, partition);
                using var runtime = await FlowRuntime.CreateAsync(engine, flow, values, ct);
                runtime.RunId = runId;
                runtime.SubmissionId = payload.SubmissionId;
                runtime.Slices = payload.Slices;
                var intake = await runtime.IntakeAsync(payload.Force, ct);
                return (operation, payload, JsonSerializer.Serialize(
                    IntakeOutcome.From(intake, flow.Source.Record.Object, runtime.Selection.Describe(), payload.Slices), Json));
            }

            using var target = FlowRuntime.ForTarget(engine, flow);
            target.RunId = runId;
            var drained = await target.WorkAsync(once: false, payload.SubmissionId, ct);
            return (operation, payload, JsonSerializer.Serialize(DrainOutcome.From(drained, payload.SubmissionId), Json));
        }
    }

    /// <summary>
    /// The fan-out the platform hands a run, with nodes that run each member as a node runs a claimed run: through the
    /// document executor, on the same document, with the member's parameters, all members at once. What a member returns
    /// to its coordinator is what a node journals on its run row, the <c>result</c> of its <c>run.json</c>. A member's
    /// pushes are counted under a node name that carries its interface. Reading the members waits for them to finish, which
    /// a coordinator does by polling.
    /// </summary>
    private sealed class NodePool : IRunFanOut
    {
        private readonly SqlServerIngestionFixture _estate;
        private readonly string _file;
        private readonly Action<string> _log;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, (int Slot, Task<RunFanOutMember> Run)> _members = [];
        private readonly List<(string Operation, DeliveryRunPayload Payload)> _enqueued = [];
        private readonly List<string> _failures = [];
        private int _nodes;

        public NodePool(SqlServerIngestionFixture estate, string file, Action<string> log)
        {
            _estate = estate;
            _file = file;
            _log = log;
        }

        /// <summary>Every member enqueued, with the operation and payload it was given.</summary>
        public IReadOnlyList<(string Operation, DeliveryRunPayload Payload)> Enqueued
        {
            get
            {
                lock (_gate)
                {
                    return [.. _enqueued];
                }
            }
        }

        /// <summary>The members that failed, each with its node and error.</summary>
        public IReadOnlyList<string> Failures
        {
            get
            {
                lock (_gate)
                {
                    return [.. _failures];
                }
            }
        }

        public Task<RunFanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(members);
            var ids = new List<Guid>();
            lock (_gate)
            {
                foreach (var member in members)
                {
                    var payload = DeliveryRunPayload.Parse(member);
                    var runId = Guid.NewGuid();
                    var node = $"{payload.Interface ?? "source"}/node-{++_nodes}";
                    _enqueued.Add((DeliveryOperations.Of(member), payload));
                    ids.Add(runId);
                    var slot = ids.Count;
                    _members[runId] = (slot, Task.Run(() => RunAsync(member, runId, slot, node, ct), CancellationToken.None));
                }
            }

            return Task.FromResult(new RunFanOutHandle(Guid.NewGuid(), ids));
        }

        public async Task<IReadOnlyList<RunFanOutMember>> MembersAsync(RunFanOutHandle handle, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(handle);
            List<Task<RunFanOutMember>> runs;
            lock (_gate)
            {
                runs = handle.RunIds.Select(id => _members[id].Run).ToList();
            }

            return await Task.WhenAll(runs).WaitAsync(ct);
        }

        public Task CancelAsync(RunFanOutHandle handle, CancellationToken ct) => Task.CompletedTask;

        private async Task<RunFanOutMember> RunAsync(RunParameters member, Guid runId, int slot, string node, CancellationToken ct)
        {
            FakeProtocol.CurrentNode.Value = node;
            try
            {
                var outcome = await _estate.Documents.RunAsync(
                    _file, new DocumentExecutionOptions { RunId = runId, Actor = node, Parameters = member }, ct);
                string? result = null;
                if (outcome.RunDirectory is { } directory)
                {
                    using var artifact = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "run.json"), ct));
                    result = CatalogProjection.RunResultJson(artifact.RootElement);
                }

                if (!outcome.Success)
                {
                    Fail(node, outcome.Error ?? "no error recorded");
                }

                return new RunFanOutMember(runId, slot, outcome.Success ? "succeeded" : "failed", outcome.Error, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Fail(node, ex.ToString());
                return new RunFanOutMember(runId, slot, "failed", ex.Message, null);
            }
        }

        private void Fail(string node, string error)
        {
            lock (_gate)
            {
                _failures.Add($"{node}: {error}");
            }

            _log($"{node} failed: {error}");
        }
    }

    /// <summary>
    /// A fan-out whose members run as nodes of their own, all at once: each member starts on its own flow of control the
    /// moment it is enqueued, on a runtime of its own over the same engine, while the coordinating run carries on with its
    /// own work, as the nodes of a pool would. A member's pushes are counted under its node's name
    /// (<see cref="FakeProtocol.CurrentNode"/>). Reading the state waits for the members to finish, which a coordinator
    /// does by polling; a member that failed is reported failed, and named in <see cref="Failures"/>.
    /// </summary>
    private sealed class ConcurrentNodes : IFanOutDispatcher
    {
        private readonly FlowDefinition _flow;
        private readonly Action<string> _log;
        private readonly object _gate = new();
        private readonly Dictionary<Guid, (int Slot, Task<string> Run)> _members = [];
        private readonly Dictionary<Guid, IReadOnlyList<Guid>> _groups = [];
        private readonly List<string> _failures = [];
        private int _nodes;
        private int _running;
        private int _maxRunning;

        /// <param name="flow">The flow whose members the nodes run.</param>
        /// <param name="log">Where a failed member's whole error, with its stack, is written.</param>
        public ConcurrentNodes(FlowDefinition flow, Action<string> log)
        {
            _flow = flow;
            _log = log;
        }

        public EngineContext? Engine { get; set; }

        public bool Available => true;

        /// <summary>The most members that were running at the same moment.</summary>
        public int MaxRunning
        {
            get
            {
                lock (_gate)
                {
                    return _maxRunning;
                }
            }
        }

        /// <summary>The members that failed, each with its node and error.</summary>
        public IReadOnlyList<string> Failures
        {
            get
            {
                lock (_gate)
                {
                    return [.. _failures];
                }
            }
        }

        public Task<FanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(members);
            var engine = Engine ?? throw new InvalidOperationException("The nodes have no engine to run their members on.");
            var ids = new List<Guid>();
            lock (_gate)
            {
                foreach (var member in members)
                {
                    var runId = Guid.NewGuid();
                    var node = $"{_flow.Name}/node-{++_nodes}";
                    ids.Add(runId);
                    _members[runId] = (ids.Count, Task.Run(() => RunAsync(engine, member, runId, node, ct), CancellationToken.None));
                }

                var groupId = Guid.NewGuid();
                _groups[groupId] = ids;
                return Task.FromResult(new FanOutHandle(groupId, ids));
            }
        }

        public async Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(handle);
            List<(Guid RunId, int Slot, Task<string> Run)> runs;
            lock (_gate)
            {
                runs = _groups[handle.GroupId].Select(id => (id, _members[id].Slot, _members[id].Run)).ToList();
            }

            // Every member settles before its state is read; a failed member's error is its state, not this call's.
            await Task.WhenAll(runs.Select(r => r.Run.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default))).WaitAsync(ct);
            return new FanOutState(runs
                .Select(r => r.Run.IsCompletedSuccessfully
                    ? new FanOutMemberState(r.RunId, r.Slot, "succeeded", null, r.Run.Result)
                    : new FanOutMemberState(r.RunId, r.Slot, "failed", r.Run.Exception?.GetBaseException().Message ?? "cancelled", null))
                .ToList());
        }

        public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default) => Task.CompletedTask;

        private async Task<string> RunAsync(EngineContext engine, RunParameters member, Guid runId, string node, CancellationToken ct)
        {
            FakeProtocol.CurrentNode.Value = node;
            lock (_gate)
            {
                _maxRunning = Math.Max(_maxRunning, ++_running);
            }

            try
            {
                return (await InlineDispatcher.RunMemberAsync(engine, _flow, member, runId, ct)).ResultJson;
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _failures.Add($"{node}: {ex.GetType().Name}: {ex.Message}");
                    _log($"{node} failed: {ex}");
                }

                throw;
            }
            finally
            {
                lock (_gate)
                {
                    _running--;
                }
            }
        }
    }
}
