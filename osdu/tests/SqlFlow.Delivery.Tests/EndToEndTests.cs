using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Engine.Verify;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// End to end over the sample mapping, the real WellLog 1.4.0 schema snapshot, the sample estate in the in-memory
/// ingestion tables, the module's database on SQL Server and a fake protocol.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public class EndToEndTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>How many logs the sample estate holds.</summary>
    private static int LogCount => SampleEstate.Logs().Count;

    private async Task<MemoryIngestionTables> EstateAsync() => await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);

    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> RuntimeAsync(
        MemoryIngestionTables tables, Func<FlowDefinition, FlowDefinition>? adjust = null, FakeProtocol? protocol = null,
        FakeProtocolFactory? protocols = null, IRecordSearchFactory? searches = null)
    {
        var ledger = _db.Ledger(_clock);
        protocol ??= new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables, searches: searches) with { Protocols = protocols ?? new FakeProtocolFactory(protocol) };
        var flow = adjust is null ? Samples.LocalFlow(_root) : adjust(Samples.LocalFlow(_root));
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        return (runtime, protocol, ledger);
    }

    /// <summary>One run's work: plan into batches, drain them, and close the submission, as a deliver run does.</summary>
    private async Task<(WorkerSummary Work, Guid SubmissionId)> RunAsync(FlowRuntime runtime, FakeProtocol protocol, OsduLedger ledger, bool force = false)
    {
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force);
        if (intake.NothingToDo)
        {
            return (WorkerSummary.Empty, intake.Submission.SubmissionId);
        }

        var worker = new DeliveryWorker(
            ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
            CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
        var summary = await worker.DrainAsync(intake.Submission.SubmissionId);
        await runtime.Intake.CompleteAsync(intake.Submission.SubmissionId, runtime.Flow.Id);
        return (summary, intake.Submission.SubmissionId);
    }

    [Fact]
    public async Task Plan_without_a_ledger_creates_everything()
    {
        var tables = await EstateAsync();
        var engine = Samples.Engine(ledger: null, _clock, sources: tables);
        using var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        runtime.Selection = SourceSelection.Full();

        var plan = await runtime.PlanAsync();

        Assert.Equal(LogCount, plan.Entries.Count);
        Assert.All(plan.Entries, e => Assert.Equal(PlannedAction.Create, e.Action));
        Assert.All(plan.Entries, e => Assert.Equal(1, e.ChunkCount));
        Assert.All(plan.Entries, e => Assert.StartsWith("dev:work-product-component--WellLog:", e.TargetId!, StringComparison.Ordinal));
        // Every entry carries the row it was read from, which is what the ledger records as the record's origin.
        Assert.All(plan.Entries, e => Assert.Equal(SampleEstate.FileName, e.Origin.FileName));
        Assert.All(plan.Entries, e => Assert.NotNull(e.SourceKeyJson));
        var doc = plan.Entries[0].Render!.Document;
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", doc["data"]!["VerticalMeasurement"]!["VerticalMeasurementUnitOfMeasureID"]!.GetValue<string>());
    }

    [Fact]
    public async Task Every_record_a_run_settles_is_counted_in_the_delivery_metrics()
    {
        using var capture = new MetricsCapture();
        var name = $"metrics-{Guid.NewGuid():N}";
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, f => f with { Name = name });
        using (runtime)
        {
            var (summary, _) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(LogCount, summary.Delivered);

            var counted = capture.Of("osdu_delivery.records", "flow", runtime.Flow.Label);
            Assert.Equal(LogCount, counted.Count);
            Assert.All(counted, c => Assert.Equal("delivered", c.Tags["outcome"]));
            Assert.All(counted, c => Assert.Equal(DeliveryProtocols.Name(protocol.Kind), c.Tags["route"]));
            Assert.Equal(LogCount, capture.Of("osdu_delivery.record.duration", "flow", runtime.Flow.Label).Count);
        }
    }

    [Fact]
    public async Task A_deliver_run_that_changes_nothing_is_idle_and_every_run_says_only_what_it_did()
    {
        var tables = await EstateAsync();
        var (runtime, _, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            runtime.Actor = "schedule:wells";
            runtime.RunId = Guid.NewGuid();
            var first = await runtime.RunAsync(force: false);
            Assert.False(first.Idle);

            // The schedule fires again with nothing new in the ingestion tables.
            _clock.Advance(TimeSpan.FromHours(1));
            runtime.RunId = Guid.NewGuid();
            var second = await runtime.RunAsync(force: false);
            Assert.True(second.Idle);
            Assert.Equal(0, second.Work.Processed);

            var trail = await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "deliver" });
            Assert.Equal([true, false], trail.Select(a => a.Idle));
            Assert.Equal([second.Submission.SubmissionId, first.Submission.SubmissionId], trail.Select(a => a.SubmissionId));

            // Each summary names only the counts that are not zero; the submission is on the row, not in its text.
            Assert.Equal($"{LogCount} planned, {LogCount} delivered", trail[1].Summary);
            Assert.Equal(second.Own.ToString(), trail[0].Summary);
            Assert.All(trail, a => Assert.DoesNotContain("submission", a.Summary!, StringComparison.Ordinal));
            Assert.DoesNotContain(" 0 ", " " + trail[0].Summary, StringComparison.Ordinal);

            var shown = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Idle = false }));
            Assert.Equal(first.Submission.SubmissionId, shown.SubmissionId);
        }
    }

    [Fact]
    public void A_run_says_only_the_counts_that_are_not_zero()
    {
        Assert.Equal("nothing to deliver", new RunCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0).ToString());
        Assert.Equal("5 unchanged, 1 stale", new RunCounts(0, 3, 0, 1, 2, 0, 0, 0, 0, 0, 0, 0).ToString());
        Assert.Equal("4 planned, 3 delivered, 1 failed, 2 waiting", new RunCounts(4, 0, 0, 0, 0, 0, 3, 0, 1, 0, 1, 2).ToString());
        Assert.Equal("nothing due", WorkerSummary.Empty.Headline);
        Assert.Equal("2 delivered, 1 retrying", new WorkerSummary(3, 2, 1, 0, 0).Headline);
        Assert.Equal("nothing to plan", IntakeCounts.Empty.Headline);
        Assert.Equal("2 planned, 1 held", new IntakeCounts(3, 2, 0, 1, 0, 0, 1).Headline);
        Assert.True(IntakeCounts.Empty.Idle);
        Assert.False(new IntakeCounts(1, 0, 0, 1, 0, 0, 0).Idle);
        Assert.True(WorkerSummary.Empty.Idle);
        Assert.False(new WorkerSummary(0, 0, 0, 0, 0, Waiting: 1).Idle);
    }

    [Fact]
    public async Task A_record_waits_for_the_record_it_refers_to_and_goes_out_when_that_one_lands()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            // The wellbore the sample well logs refer to is another flow's record of this ledger, queued and not yet
            // delivered: the well logs point at a record that is not in OSDU.
            var wellboreFlow = FlowId.Of("wells-wellbore-03-header-delivery");
            await ledger.RegisterAsync(wellboreFlow);
            // The second log is moved into the first log's wellbore, so two logs wait for one wellbore.
            tables.Records[1].Row["wellbore_uwi"] = SampleEstate.Logs()[0].WellboreUwi;
            var wellboreId = "dev:master-data--Wellbore:" + FixedRecordSearchFactory.WellboreId(SampleEstate.Logs()[0].WellboreUwi);
            var wellbore = new DeliveryKey(Guid.NewGuid());
            var submission = Guid.NewGuid();
            await ledger.RegisterSubmissionAsync(new SubmissionState
            {
                SubmissionId = submission,
                FlowId = wellboreFlow,
                FlowName = "wells-wellbore-03-header-delivery",
                MappingReference = "Wellbore@1.3.0",
                RenderContext = "{}",
                SourceConnection = "${env:OSDU_DATA_DB}",
                SourceObject = "OsduData.silver.Wellbore",
            });
            await ledger.UpsertPendingAsync(wellboreFlow, [new RecordState
            {
                DeliveryKey = wellbore,
                FlowId = wellboreFlow,
                SourceKey = SampleEstate.Logs()[0].WellboreUwi,
                MappingName = "Wellbore",
                TargetId = wellboreId,
                LastSubmissionId = submission,
                PendingDocumentRef = "0:0:10",
                PendingRenderContext = "{}",
                PendingMetadataHash = "mh",
                PendingMetadata = true,
            }]);

            // The two logs of that wellbore wait, and no try is charged for them; the other logs refer to their own
            // wellbores, which no record of the ledger holds, so they go out as they are.
            var (work, logSubmission) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(LogCount - 2, work.Delivered);
            Assert.Equal(2, work.Waiting);
            Assert.Equal(LogCount - 2, protocol.Deliveries.Count);
            var waiting = await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Waiting });
            Assert.Equal(2, waiting.Count);
            Assert.All(waiting, r => Assert.Equal(wellboreId, r.WaitingFor));
            Assert.All(waiting, r => Assert.Equal(0, r.AttemptCount));
            Assert.All(waiting, r => Assert.Contains("data.WellboreID", r.LastError!, StringComparison.Ordinal));
            Assert.Equal(2, (await ledger.GetSubmissionAsync(logSubmission))!.Waiting);
            Assert.Equal(2, (await ledger.StatsAsync(runtime.Flow.Id, Now)).Waiting);

            // The wellbore lands, which sends its waiters back to pending; the next drain of the same submission sends them.
            await ledger.CompleteAsync(wellboreFlow, new RecordCompletion
            {
                DeliveryKey = wellbore,
                Status = RecordStatus.Delivered,
                Promote = true,
                TargetVersion = 1,
                TargetId = wellboreId,
                Claimed = ClaimedWork.Of((await ledger.GetRecordAsync(wellboreFlow, wellbore))!),
                Attempt = new AttemptRecord
                {
                    DeliveryKey = wellbore,
                    Worker = "test",
                    StartedUtc = Now,
                    CompletedUtc = Now,
                    Outcome = AttemptOutcome.Delivered,
                    Phase = "metadata",
                },
            });

            var worker = new DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
            var sent = await worker.DrainAsync(logSubmission);

            Assert.Equal(2, sent.Delivered);
            Assert.Equal(0, sent.Waiting);
            Assert.Equal(LogCount, protocol.Deliveries.Count);
            Assert.Empty(await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Waiting }));
        }
    }

    [Fact]
    public async Task With_the_storage_check_on_a_record_whose_reference_is_nowhere_is_held_before_anything_is_sent()
    {
        using var platform = new FakeOsduPlatform();
        using var http = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SqlFlow.Core.Secrets.SecretResolver([new SqlFlow.Core.Secrets.EnvSecretProvider()]), _clock, platform, allowLoopback: true);
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var check = new ReferenceCheck(
                ledger,
                new OsduHttpClient(
                    http, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" }),
                "/api/storage/v2/query/records");

            // Neither the ledger nor storage holds the wellbores the sample well logs refer to.
            var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
            var worker = new DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker")
            {
                MaxWait = null,
                Gate = new ValidationGate(runtime.Flow, (_, _, _) => Task.FromResult<SchemaSnapshot?>(runtime.Mapping.Schema), check.Resolver(runtime.Flow.Id), _clock),
            };
            var held = await worker.DrainAsync(intake.Submission.SubmissionId);

            Assert.Equal(LogCount, held.Held);
            Assert.Equal(0, held.Delivered);
            Assert.Empty(protocol.Deliveries);
            var records = await ledger.ListAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Held });
            Assert.Equal(LogCount, records.Count);
            Assert.All(records, r => Assert.Contains("neither the ledger nor OSDU's storage service holds", r.LastError!, StringComparison.Ordinal));

            Assert.All(records, r => Assert.Contains("dev:master-data--Wellbore:Wellbore-", r.LastError!, StringComparison.Ordinal));

            // Once storage holds every record they refer to (the wellbores, the units, the curve types), the released
            // records go out.
            foreach (var id in records.SelectMany(r => r.PendingReferences).Select(r => r.Id).Distinct(StringComparer.Ordinal))
            {
                platform.Records[id] = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = id,
                    ["kind"] = "osdu:wks:master-data--Wellbore:1.3.0",
                    ["version"] = 1,
                };
            }

            Assert.Equal(LogCount, await ledger.ReleaseAsync(runtime.Flow.Id, records.Select(r => r.DeliveryKey).ToList(), Now));
            var sent = await worker.DrainAsync(intake.Submission.SubmissionId);

            Assert.Equal(LogCount, sent.Delivered);
            Assert.Equal(LogCount, protocol.Deliveries.Count);
        }
    }

    [Fact]
    public async Task A_run_delivers_then_the_next_one_skips_a_scope_whose_rows_did_not_change()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(LogCount, summary.Delivered);
            Assert.Equal(LogCount, protocol.Deliveries.Count);
            Assert.All(protocol.Deliveries, w => Assert.True(w.DeliverMetadata && w.DeliverPayload));

            var submission = await ledger.GetSubmissionAsync(submissionId);
            Assert.Equal(SubmissionStatus.Completed, submission!.Status);
            Assert.Equal(LogCount, submission.Delivered);
            Assert.Equal(LogCount, submission.Planned);
            Assert.Equal(runtime.Flow.Source.Record.Object, submission.SourceObject);
            Assert.Equal(runtime.Flow.Source.Connection, submission.SourceConnection);

            var state = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(RecordStatus.Delivered, state!.Status);
            Assert.NotNull(state.TargetVersion);
            Assert.NotNull(state.MetadataHash);
            Assert.NotNull(state.PayloadHash);
            Assert.Equal(SampleWellLogs.UpdatedUtc(0), state.SourceModifiedUtc);
            Assert.Equal(SampleEstate.FileName, state.SourceFileName);
            Assert.Equal(1, state.SourceRowNumber);

            // The whole-scope plan moved the scope's watermark to the window it covered.
            var watermark = await ledger.GetWatermarkAsync(runtime.Flow.Id, Planner.ScopeKey(runtime.Parameters));
            Assert.NotNull(watermark);
            Assert.Equal(submissionId, watermark!.SubmissionId);
        }

        // Nothing changed in the tables since: the next run reads its window, finds no row in it and skips the scope.
        var (next, nextProtocol, nextLedger) = await RuntimeAsync(tables);
        using (next)
        {
            next.Selection = SourceSelection.Incremental(
                (await nextLedger.GetWatermarkAsync(next.Flow.Id, Planner.ScopeKey(next.Parameters)))!.UpdatedThroughUtc);
            var plan = await next.PlanAsync();
            Assert.True(plan.SkippedWholeRun);
            Assert.Empty(plan.Entries);
            var (summary, _) = await RunAsync(next, nextProtocol, nextLedger);
            Assert.Equal(0, summary.Processed);
            Assert.Empty(nextProtocol.Deliveries);
        }
    }

    [Fact]
    public async Task A_row_that_changed_is_rendered_again_and_only_what_moved_is_sent()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();

            // A column the mapping does not read: the fingerprint moves, the rendered document does not, so nothing is sent.
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "logging_contractor", "SLB", Now, SampleWellLogs.UpdatedUtc(0).AddHours(1));
            var plan = await runtime.PlanAsync(force: true);
            var entry = plan.Entries.Single(e => e.SourceKey.EndsWith(SampleWellLogs.Logs()[0].LogId, StringComparison.Ordinal));
            Assert.Equal(PlannedAction.Skip, entry.Action);
            Assert.Equal(SkipTier.ContentHash, entry.SkipTier);

            // A column the mapping does read: the metadata is sent, and the payload is left alone.
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc(0).AddHours(2));
            var metadata = await runtime.PlanAsync(force: true);
            var changed = metadata.Entries.Single(e => e.SourceKey.EndsWith(SampleWellLogs.Logs()[0].LogId, StringComparison.Ordinal));
            Assert.Equal(PlannedAction.UpdateMetadata, changed.Action);
            Assert.True(changed.DeliverMetadata);
            Assert.False(changed.DeliverPayload);

            var (summary, _) = await RunAsync(runtime, protocol, ledger, force: true);
            Assert.Equal(1, summary.Delivered);
            var work = Assert.Single(protocol.Deliveries);
            Assert.True(work.DeliverMetadata);
            Assert.False(work.DeliverPayload);
            Assert.NotNull(work.ExistingVersion);
        }
    }

    [Fact]
    public async Task Rewritten_curve_files_send_the_payload_and_nothing_else()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();

            // The curve values move: the payload hash on the row moves with them, the document does not.
            var log = SampleWellLogs.Logs()[0];
            var moved = log.WithValues(log.Columns()[1], value => value is null ? null : value + 10);
            _clock.Advance(TimeSpan.FromMinutes(10));
            await SampleEstate.RewritePayloadAsync(_root, tables.Records[0], moved, Now);

            var plan = await runtime.PlanAsync(force: true);
            var entry = plan.Entries.Single(e => e.SourceKey.EndsWith(SampleWellLogs.Logs()[0].LogId, StringComparison.Ordinal));
            Assert.Equal(PlannedAction.UpdatePayload, entry.Action);

            var (summary, _) = await RunAsync(runtime, protocol, ledger, force: true);
            Assert.Equal(1, summary.Delivered);
            var work = Assert.Single(protocol.Deliveries);
            Assert.False(work.DeliverMetadata);
            Assert.True(work.DeliverPayload);
        }
    }

    [Fact]
    public async Task A_batch_asks_every_name_once_in_one_round_and_a_wellbore_known_only_by_an_alias_in_the_next()
    {
        var tables = await EstateAsync();
        tables.Records[2].Row["wellbore_uwi"] = "OLD-NAME-B";
        var aliased = "dev:master-data--Wellbore:" + FixedRecordSearchFactory.WellboreId(SampleEstate.Logs()[2].WellboreUwi);
        var searches = new FixedRecordSearchFactory(
        [
            .. SampleEstate.Logs().Select(l => ("data.FacilityName", l.WellboreUwi, FixedRecordSearchFactory.Partition + ":master-data--Wellbore:" + FixedRecordSearchFactory.WellboreId(l.WellboreUwi))),
            ("data.NameAliases.AliasName", "OLD-NAME-B", FixedRecordSearchFactory.Partition + ":master-data--Wellbore:" + FixedRecordSearchFactory.WellboreId(SampleEstate.Logs()[2].WellboreUwi)),
        ]);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, searches: searches);
        using (runtime)
        {
            var (summary, _) = await RunAsync(runtime, protocol, ledger);

            Assert.Equal(LogCount, summary.Delivered);
            var byAlias = Assert.Single(protocol.Deliveries, w => w.Key == SampleEstate.Key(2));
            Assert.Equal(aliased + ":", byAlias.Document["data"]!["WellboreID"]!.GetValue<string>());

            // Every distinct name is asked once, and the one no wellbore is named by is asked again as an alias, after.
            var asked = Assert.Single(searches.Created).Asked.ToList();
            var names = asked.Where(q => q.Field == "data.FacilityName").Select(q => q.Value).ToList();
            Assert.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Count);
            Assert.Contains("OLD-NAME-B", names);
            Assert.Equal(("data.NameAliases.AliasName", "OLD-NAME-B"), (asked[^1].Field, asked[^1].Value));
            Assert.Single(asked, q => q.Field == "data.NameAliases.AliasName");
        }
    }

    [Fact]
    public async Task Unresolvable_reference_holds_the_record_and_release_requeues_it()
    {
        var tables = await EstateAsync();
        tables.Records[2].Row["wellbore_uwi"] = "Wellbore Z-1";
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(LogCount - 1, summary.Delivered);
            var submission = await ledger.GetSubmissionAsync(submissionId);
            Assert.Equal(1, submission!.Held);
            var held = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2));
            Assert.Equal(RecordStatus.Held, held!.Status);
            Assert.Contains("no Wellbore on the platform (osdu:wks:master-data--Wellbore:*) matches", held.LastError, StringComparison.Ordinal);
            Assert.Contains("data.FacilityName 'Wellbore Z-1' found no record", held.LastError, StringComparison.Ordinal);
            Assert.Equal("Wellbore Z-1 / COMPOSITE / " + SampleEstate.Logs()[2].LogId, held.Label);
            // The held record is traced to the row it was held at.
            Assert.Equal(SampleEstate.FileName, held.PendingSourceFileName);

            // The same rows again: the held record is blocked, not re-attempted
            // (osdu/docs/reference/concepts/record-lifecycle.md, Record states).
            var plan = await runtime.PlanAsync(force: true);
            var blocked = plan.Entries.Single(e => e.Action == PlannedAction.Blocked);
            Assert.Equal(SampleEstate.Key(2), blocked.Key);
            Assert.Contains("held since", blocked.Reason, StringComparison.Ordinal);

            // Released: the next plan renders it again (and holds it again, because the source is still wrong).
            Assert.Equal(1, await ledger.ReleaseAsync(runtime.Flow.Id, null, Now));
            var replanned = await runtime.PlanAsync(force: true);
            Assert.Equal(PlannedAction.Hold, replanned.Entries.Single(e => e.Key == SampleEstate.Key(2)).Action);
        }
    }

    [Fact]
    public async Task A_deleted_row_is_never_delivered_and_says_why()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);

            _clock.Advance(TimeSpan.FromMinutes(10));
            tables.Records[0].DeletedUtc = Now;
            tables.Records[0].UpdatedUtc = Now;

            var plan = await runtime.PlanAsync(force: true);
            var deleted = plan.Entries.Single(e => e.Key == SampleEstate.Key(0));
            Assert.Equal(PlannedAction.Hold, deleted.Action);
            Assert.Contains("marked the record row deleted", deleted.Reason, StringComparison.Ordinal);
            Assert.Contains("Remove the record from OSDU deliberately", deleted.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_row_the_ingestion_table_changed_to_what_OSDU_holds_is_recorded_once_and_nothing_is_sent()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();
            var arrived = tables.Records[1].InsertedUtc;

            // A later file carries the row again with a column the mapping does not read changed: the ingestion table
            // stamps it as changed, and it renders the document OSDU already holds.
            _clock.Advance(TimeSpan.FromMinutes(10));
            tables.Records[1].UpdatedUtc = Now;
            tables.Records[1].DatasetUpdatedUtc["curves"] = Now;
            tables.Records[1].FileName = "welllog_next.csv";
            tables.Records[1].RowNumber = 42;

            var (summary, submission) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(0, summary.Delivered);
            Assert.Empty(protocol.Deliveries);
            var identical = Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(1), 10), a => a.Phase == AttemptPhases.Identical);
            Assert.Equal((AttemptOutcome.Skipped, submission), (identical.Outcome, identical.SubmissionId));
            Assert.Equal(("welllog_next.csv", 42L, (DateTime?)Now), (identical.SourceFileName, identical.SourceRowNumber, identical.SourceUpdatedUtc));
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1));
            Assert.Equal(("welllog_next.csv", (DateTime?)Now, arrived), (record!.SourceFileName, record.SourceUpdatedUtc, record.SourceInsertedUtc));

            // The whole source read again changes nothing, and records nothing.
            runtime.Selection = SourceSelection.Full();
            await RunAsync(runtime, protocol, ledger, force: true);
            Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(1), 10), a => a.Phase == AttemptPhases.Identical);
            Assert.Empty(protocol.Deliveries);
        }
    }

    [Fact]
    public async Task A_sync_consolidates_the_ledger_with_the_ingestion_tables_and_sends_nothing()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();
            var flowId = runtime.Flow.Id;

            // A ledger from before it kept arrivals, and three changes no plan saw: a row that changed, a row the
            // ingestion flow's key match marked deleted, and a row deleted from the table outright.
            await using (var db = _db.CreateDbContext())
            {
                await db.DeliveryRecords.Where(r => r.FlowId == flowId).ExecuteUpdateAsync(s => s.SetProperty(r => r.SourceInsertedUtc, (DateTime?)null));
            }

            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[1], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc(1).AddHours(2));
            tables.Records[2].DeletedUtc = Now;
            var gone = tables.Records[3];
            tables.Records.Remove(gone);

            var synced = await runtime.SyncAsync(null);

            // Nothing reached OSDU. Every row found gave the ledger its arrival; the changed and the deleted row are asked
            // to be planned by the next run; the row that is gone is on its record's history, which keeps its status.
            Assert.Empty(protocol.Deliveries);
            Assert.Equal(
                (LogCount, LogCount - 1, 1, 1, 2, 1, 1),
                (synced.Checked, synced.ArrivalsRecorded, synced.ChangedUnseen, synced.DeletedUnseen, synced.PlansRequested, synced.NotFound, synced.NotFoundRecorded));
            Assert.Equal([(await ledger.GetRecordAsync(flowId, SampleEstate.Key(3)))!.SourceKey], synced.NotFoundSample);
            var arrived = tables.Records[0].InsertedUtc;
            for (var i = 0; i < LogCount; i++)
            {
                var record = await ledger.GetRecordAsync(flowId, SampleEstate.Key(i));
                Assert.Equal(i == 3 ? null : arrived, record!.SourceInsertedUtc);
                Assert.Equal(i is 1 or 2, record.PlanRequestedUtc is not null);
                Assert.Equal(RecordStatus.Delivered, record.Status);
            }

            var missing = Assert.Single(await ledger.ListAttemptsAsync(flowId, SampleEstate.Key(3), 10), a => a.Phase == AttemptPhases.SourceMissing);
            Assert.Equal((AttemptOutcome.Skipped, "sync"), (missing.Outcome, missing.Worker));
            Assert.Contains("did not find the row in the ingestion table", missing.ResultJson, StringComparison.Ordinal);
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = flowId }), a => a.Kind == "sync");
            Assert.Equal("completed", activity.Outcome);
            Assert.Contains("1 not found in the ingestion table", activity.Summary, StringComparison.Ordinal);

            // Synced again, nothing new is written: the arrivals are in, the plans are asked for, the gone row is reported.
            var again = await runtime.SyncAsync(null);
            Assert.Equal(
                (LogCount - 3, 0, 0, 1, 0),
                (again.InAgreement, again.ArrivalsRecorded, again.PlansRequested, again.NotFound, again.NotFoundRecorded));
            Assert.Single(await ledger.ListAttemptsAsync(flowId, SampleEstate.Key(3), 10), a => a.Phase == AttemptPhases.SourceMissing);

            // The next run plans what the sync asked for, by key, as every run takes the records the ledger asked for:
            // the changed row is delivered, and the deleted row is held as deleted.
            var requested = await ledger.ListPlanRequestedAsync(flowId, null, 10);
            Assert.Equal(new[] { SampleEstate.Key(1), SampleEstate.Key(2) }.ToHashSet(), requested.Select(r => r.DeliveryKey).ToHashSet());
            runtime.Selection = SourceSelection.ForKeys([.. requested.Select(r => KeyTuple.FromJson(r.SourceKeyJson!))]);
            Assert.Equal(1, (await RunAsync(runtime, protocol, ledger, force: true)).Work.Delivered);
            Assert.Equal(SampleEstate.Key(1), Assert.Single(protocol.Deliveries).Key);
            Assert.Equal(RecordStatus.Held, (await ledger.GetRecordAsync(flowId, SampleEstate.Key(2)))!.Status);

            // The ledger now agrees with every row there is; a row restored in the source is counted for a release.
            var settled = await runtime.SyncAsync(null);
            Assert.Equal((LogCount - 1, 0, 1), (settled.InAgreement, settled.PlansRequested, settled.NotFound));
            tables.Records[2].DeletedUtc = null;
            Assert.Equal(1, (await runtime.SyncAsync(null)).Restored);
        }
    }

    [Fact]
    public async Task A_sync_of_one_record_reads_its_row_in_the_scope_the_record_was_planned_under()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();
            await using (var db = _db.CreateDbContext())
            {
                await db.DeliveryRecords.Where(r => r.FlowId == runtime.Flow.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.SourceInsertedUtc, (DateTime?)null));
            }

            // A sync run whose own values name another log source, on the target runtime a sync runs on, still reads the
            // record in the scope its last plan ran with: the submission that planned it recorded the values.
            var engine = Samples.Engine(ledger, _clock, sources: tables);
            var elsewhere = new Dictionary<string, string>(StringComparer.Ordinal) { ["logSource"] = "ELSEWHERE" };
            using var other = FlowRuntime.ForTarget(engine, runtime.Flow, elsewhere);
            other.Actor = "gui:tahir";
            var synced = await other.SyncAsync([SampleEstate.Key(0)]);

            Assert.Equal((1, 1, 0, 0), (synced.Checked, synced.ArrivalsRecorded, synced.NotFound, synced.NotInLedger));
            Assert.Equal(tables.Records[0].InsertedUtc, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.SourceInsertedUtc);
            Assert.Null((await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1)))!.SourceInsertedUtc);
            Assert.Empty(protocol.Deliveries);

            // One record's sync is on that record's own history, with who asked for it.
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id }), a => a.Kind == "sync");
            Assert.Equal((SampleEstate.Key(0).Value, "gui:tahir"), (activity.DeliveryKey, activity.Actor));

            // A key the ledger does not hold is named as such, and nothing is read for it.
            var unknown = await other.SyncAsync([new DeliveryKey(Guid.NewGuid())]);
            Assert.Equal((0, 1), (unknown.Checked, unknown.NotInLedger));
        }
    }

    [Fact]
    public async Task A_row_tagged_deleted_is_held_by_the_next_incremental_run_although_its_update_column_did_not_move()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();
            var delivered = Now;

            // The ingestion flow's key match tags the row deleted: the delete column is stamped, the update column is not.
            _clock.Advance(TimeSpan.FromMinutes(10));
            tables.Records[0].DeletedUtc = Now;

            // The next run reads what changed since the last one, as the platform's watermark bounds it.
            runtime.Selection = SourceSelection.Incremental(delivered);
            var (summary, _) = await RunAsync(runtime, protocol, ledger);

            Assert.Equal(0, summary.Delivered);
            Assert.Empty(protocol.Deliveries);
            var held = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(RecordStatus.Held, held!.Status);
            Assert.Contains("marked the record row deleted", held.LastError, StringComparison.Ordinal);
            // The record's history dates the deletion, and the record still knows when its row first arrived.
            var hold = Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10), a => a.Outcome == AttemptOutcome.Held);
            Assert.Equal((AttemptPhases.SourceDeleted, (DateTime?)Now), (hold.Phase, hold.SourceDeletedUtc));
            Assert.Equal(tables.Records[0].InsertedUtc, held.SourceInsertedUtc);
            // The rows nobody touched are read past: the window holds the one that was tagged.
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1)))!.Status);
        }
    }

    [Fact]
    public async Task A_run_plans_every_record_asked_for_again_in_passes_however_many_there_are()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();

            // Every record asked for again, as a release of a whole problem or a redelivery of the flow asks: one run plans
            // all of them, a pass of two at a time, rather than one pass and the rest left to later runs.
            Assert.Equal(LogCount, await runtime.RedeliverAsync(null, RedeliverScope.All));
            runtime.RequestedPerPass = 2;
            var passes = await DeliveryExecutor.DeliverRequestedAsync(
                runtime, SourceSelection.Full(), DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);

            Assert.Equal(((LogCount + 1) / 2, (long)LogCount, (long)LogCount), (passes.Passes, passes.Read, passes.Planned));
            Assert.NotNull(passes.FirstSubmissionId);
            Assert.Equal(LogCount, protocol.Deliveries.Count);
            Assert.Empty(await ledger.ListPlanRequestedAsync(runtime.Flow.Id, null, 10));
        }
    }

    [Fact]
    public async Task Bringing_records_up_to_date_renders_them_again_and_sends_only_what_renders_differently()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();

            // One record OSDU holds as an older render did: what an engine or rule change leaves behind, which no source
            // change reaches. An ordinary run passes it as unchanged without rendering it.
            var stale = SampleEstate.Key(0).Value;
            var fresh = SampleEstate.Key(1);
            await using (var db = _db.CreateDbContext())
            {
                await db.DeliveryRecords.Where(r => r.DeliveryKey == stale).ExecuteUpdateAsync(set => set.SetProperty(r => r.MetadataHash, "rendered-by-older-rules"));
            }

            var before = (await ledger.GetRecordAsync(runtime.Flow.Id, fresh))!;

            // A plan that renders every record says what bringing them up to date would send, and sends and asks nothing.
            async Task<PlanSummary> PlanAsync(bool renderEvery)
            {
                var header = await runtime.Planner.OpenAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, SourceSelection.Full(), gate: false, stored: null, renderEvery: renderEvery);
                var summary = new PlanSummary();
                await foreach (var _ in runtime.Planner.EntriesAsync(header, null, 1, summary, CancellationToken.None))
                {
                }

                return summary;
            }

            var passed = await PlanAsync(renderEvery: false);
            Assert.Equal((0L, (long)LogCount), (passed.Deliveries, passed.Skips));
            var rendered = await PlanAsync(renderEvery: true);
            Assert.Equal((1L, 1L, 0L, (long)LogCount - 1), (rendered.Deliveries, rendered.Metadata, rendered.Payload, rendered.Skips));
            Assert.Empty(protocol.Deliveries);
            Assert.Empty(await ledger.ListPlanRequestedAsync(runtime.Flow.Id, null, 10));

            // Asked of every delivered record: each keeps its delivered hashes and forgets the source version it was
            // planned under, so whichever run meets it renders it. A key the ledger does not hold is left as it is.
            Assert.Equal(1, await runtime.BringUpToDateAsync([fresh, DeliveryKey.Derive("nowhere", ["none"])]));
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(LogCount, await runtime.BringUpToDateAsync(null));
            var asked = (await ledger.GetRecordAsync(runtime.Flow.Id, fresh))!;
            Assert.Equal((before.MetadataHash, before.PayloadHash, (string?)null), (asked.MetadataHash, asked.PayloadHash, asked.SourceFingerprint));
            Assert.NotNull(asked.PlanRequestedUtc);

            // An ordinary run's passes over the requests send the one record that renders differently, its document alone.
            var passes = await DeliveryExecutor.DeliverRequestedAsync(
                runtime, SourceSelection.Full(), DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
            Assert.Equal((long)LogCount, passes.Read);
            var sent = Assert.Single(protocol.Deliveries);
            Assert.Equal(stale, sent.Key.Value);
            Assert.True(sent.DeliverMetadata);
            Assert.False(sent.DeliverPayload);
            Assert.Empty(await ledger.ListPlanRequestedAsync(runtime.Flow.Id, null, 10));

            // A record that rendered the same keeps what it had, its source version written back by the plan that passed it.
            var after = (await ledger.GetRecordAsync(runtime.Flow.Id, fresh))!;
            Assert.Equal((before.MetadataHash, before.SourceFingerprint, before.TargetVersion), (after.MetadataHash, after.SourceFingerprint, after.TargetVersion));

            // Up to date now: asked again, nothing renders differently, and nothing is sent.
            protocol.Deliveries.Clear();
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(LogCount, await runtime.BringUpToDateAsync(null));
            var again = await DeliveryExecutor.DeliverRequestedAsync(
                runtime, SourceSelection.Full(), DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
            Assert.Equal((long)LogCount, again.Read);
            Assert.Empty(protocol.Deliveries);
        }
    }

    [Fact]
    public async Task A_record_a_pass_cannot_plan_does_not_hold_the_run_up_and_a_row_that_is_gone_answers_its_request()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();
            Assert.Equal(LogCount, await runtime.RedeliverAsync(null, RedeliverScope.All));

            // One record's request names a key the tables hold no row for: every pass reads past it, once.
            await using (var db = _db.CreateDbContext())
            {
                var gone = SampleEstate.Key(0).Value;
                await db.DeliveryRecords.Where(r => r.DeliveryKey == gone)
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.SourceKeyJson, "[\"NO_15_9\",\"L-9999\"]"));
            }

            runtime.RequestedPerPass = 2;
            var passes = await DeliveryExecutor.DeliverRequestedAsync(
                runtime, SourceSelection.Full(), DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);

            Assert.Equal(((long)LogCount, (long)(LogCount - 1)), (passes.Read, passes.Planned));
            Assert.Equal(LogCount - 1, protocol.Deliveries.Count);

            // The pass read the record it could not plan again in its own scope: its row is gone, so the request is answered
            // there, with the gone row on the record's history, and no later run reads it again.
            Assert.Empty(await ledger.ListPlanRequestedAsync(runtime.Flow.Id, null, 10));
            Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10), a => a.Phase == AttemptPhases.SourceMissing);
            var again = await DeliveryExecutor.DeliverRequestedAsync(
                runtime, SourceSelection.Full(), DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
            Assert.Equal(0L, again.Read);
        }
    }

    [Fact]
    public async Task A_record_scoped_run_reads_only_the_rows_it_names_by_key()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();

            // A redelivery of one record's payload, planned as a key-scoped read of exactly that row.
            await runtime.RedeliverAsync([SampleEstate.Key(1)], RedeliverScope.Payload);
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1));
            Assert.NotNull(record!.PlanRequestedUtc);

            runtime.Selection = SourceSelection.ForKeys([KeyTuple.FromJson(record.SourceKeyJson!)]);
            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger, force: true);

            Assert.Equal(1, summary.Delivered);
            var sent = Assert.Single(protocol.Deliveries);
            Assert.Equal(SampleEstate.Key(1), sent.Key);
            Assert.False(sent.DeliverMetadata);
            Assert.True(sent.DeliverPayload);

            var submission = await ledger.GetSubmissionAsync(submissionId);
            Assert.Equal(SubmissionKinds.Keys, submission!.Kind);
            // A key-scoped plan says nothing about the rows it never read, so it never moves the scope's watermark.
            Assert.Null(submission.WindowFromUtc);
            Assert.Null((await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1)))!.PlanRequestedUtc);
        }
    }

    [Fact]
    public async Task A_key_the_tables_do_not_hold_is_reported_rather_than_silently_dropped()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            runtime.Selection = SourceSelection.ForKeys([KeyTuple.Of("NO_15_9", "L-9999")]);
            var plan = await runtime.PlanAsync(force: true);

            // Nothing to plan, and the key that matched no row is named rather than quietly left out of the result.
            Assert.Empty(plan.Entries);
            var missing = Assert.Single(plan.Header.Source.MissingKeys);
            Assert.Contains("L-9999", missing.Json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Transient_failures_back_off_and_succeed_later_while_terminal_statuses_hold()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var failures = 0;
            protocol.FailWith = work =>
            {
                if (work.Key == SampleEstate.Key(0) && failures++ == 0)
                {
                    return new OsduStatusException(503, "HTTP 503 Service Unavailable");
                }

                return work.Key == SampleEstate.Key(1) ? new OsduStatusException(409, "HTTP 409 Conflict: acl") : null;
            };

            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(LogCount - 2, summary.Delivered);
            Assert.Equal(1, summary.Retried);
            Assert.Equal(1, summary.Held);

            var retrying = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(RecordStatus.Pending, retrying!.Status);
            Assert.NotNull(retrying.NextAttemptUtc);
            var held = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1));
            Assert.Equal(RecordStatus.Held, held!.Status);
            Assert.Equal(SubmissionStatus.Running, (await ledger.GetSubmissionAsync(submissionId))!.Status);

            _clock.Advance(TimeSpan.FromMinutes(2));
            var worker = new DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };
            var later = await worker.DrainAsync(submissionId);
            Assert.Equal(1, later.Delivered);
            var attempts = await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10);
            Assert.Equal(2, attempts.Count);
            Assert.Equal(AttemptOutcome.Delivered, attempts[0].Outcome);
            Assert.Equal(AttemptOutcome.Failed, attempts[1].Outcome);
            await runtime.Intake.CompleteAsync(submissionId, runtime.Flow.Id);
            Assert.Equal(SubmissionStatus.Completed, (await ledger.GetSubmissionAsync(submissionId))!.Status);
        }
    }

    [Fact]
    public async Task A_record_the_service_asked_to_wait_on_is_not_attempted_again_sooner()
    {
        // The transport does not sit through a long Retry-After; it hands the wait up with the failure, and the
        // worker must not schedule the record's next attempt earlier than the service asked.
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            protocol.FailWith = work => work.Key == SampleEstate.Key(0)
                ? new OsduStatusException(429, "HTTP 429 Too Many Requests", TimeSpan.FromHours(2))
                : null;

            var (summary, _) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(1, summary.Retried);

            var waiting = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(RecordStatus.Pending, waiting!.Status);
            Assert.NotNull(waiting.NextAttemptUtc);
            Assert.True(waiting.NextAttemptUtc!.Value >= Now + TimeSpan.FromHours(2));
        }
    }

    [Fact]
    public async Task Stopping_the_worker_releases_in_flight_records_without_charging_an_attempt()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
            Assert.Equal(LogCount, intake.Submission.Planned);

            // The first delivery blocks until the worker is stopped; the stop arrives while it is in flight.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource();
            protocol.Before = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            };

            var worker = new DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "stopping-worker") { MaxWait = null };
            var drain = worker.DrainAsync(intake.Submission.SubmissionId, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);

            var interrupted = protocol.Deliveries.Single();
            var state = await ledger.GetRecordAsync(runtime.Flow.Id, interrupted.Key);
            Assert.Equal(RecordStatus.Pending, state!.Status);
            Assert.Null(state.LeaseOwner);
            Assert.Equal(0, state.AttemptCount);
            Assert.Empty(await ledger.ListAttemptsAsync(runtime.Flow.Id, interrupted.Key, 10));

            // A fresh worker picks everything up at once; nothing waited for a lease to expire.
            protocol.Before = null;
            var resumed = new DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "next-worker") { MaxWait = null };
            var summary = await resumed.DrainAsync(intake.Submission.SubmissionId);
            Assert.Equal(LogCount, summary.Delivered);
            Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, interrupted.Key, 10));
        }
    }

    /// <summary>
    /// An operation that throws anything at all still ends its activity: the ledger is the only account of what an
    /// operator asked for, and an action left reading as running would say the opposite of what happened.
    /// </summary>
    [Fact]
    public async Task An_operation_that_fails_outright_leaves_its_activity_failed_not_running()
    {
        var tables = await EstateAsync();
        var protocol = new FakeProtocol();
        // The protocol cannot be built: the failure a missing ${env:...} credential raises, outside the engine's own types.
        var protocols = new FakeProtocolFactory(protocol) { FailWith = () => new SqlFlowException("Environment variable 'FACADE_API_URL' is not set.") };
        var (runtime, _, ledger) = await RuntimeAsync(tables, protocol: protocol, protocols: protocols);
        using (runtime)
        {
            runtime.Actor = "gui:tahir";
            var thrown = await Assert.ThrowsAsync<SqlFlowException>(() => runtime.VerifyAsync(100, null, reconcile: false));
            Assert.Equal("Environment variable 'FACADE_API_URL' is not set.", thrown.Message);

            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "verify" }));
            Assert.Equal("failed", activity.Outcome);
            Assert.NotNull(activity.CompletedUtc);
            Assert.Equal("Environment variable 'FACADE_API_URL' is not set.", activity.Summary);
        }
    }

    [Fact]
    public async Task Verify_detects_drift_and_reconcile_queues_redelivery()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await RunAsync(runtime, protocol, ledger);
            protocol.VerifyWith = id => id.EndsWith(SampleEstate.Key(0).Value.ToString("N"), StringComparison.Ordinal)
                ? new VerifyResult(VerifyOutcome.Drifted, 999, "someone edited it")
                : new VerifyResult(VerifyOutcome.Match, null, null);
            var verifier = new Verifier(ledger, protocol, runtime.Flow, _clock, CompositeDeliveryListener.Empty, Samples.Logger<Verifier>());
            var summary = await verifier.RunAsync(100, null, reconcile: true);
            Assert.Equal(LogCount, summary.Checked);
            Assert.Equal(1, summary.Drifted);
            Assert.Equal(LogCount - 1, summary.Matched);

            // Reconcile queued the drifted record for redelivery: the next plan of the scope sends it whole again.
            var plan = await runtime.PlanAsync(force: true);
            var drifted = plan.Entries.Single(e => e.Key == SampleEstate.Key(0));
            Assert.Equal(PlannedAction.UpdateBoth, drifted.Action);
            Assert.Equal(LogCount - 1, plan.Skips);
        }
    }

    [Fact]
    public async Task A_stale_row_is_never_sent_and_the_skip_is_recorded_against_the_record()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            protocol.Deliveries.Clear();

            // The row is re-landed with an older business version than the one delivered: a replay, never sent.
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc(0).AddHours(-1));

            var plan = await runtime.PlanAsync(force: true);
            var entry = plan.Entries.Single(e => e.Key == SampleEstate.Key(0));
            Assert.Equal(PlannedAction.Skip, entry.Action);
            Assert.Equal(SkipTier.Stale, entry.SkipTier);
            Assert.Contains("older than the version last modified", entry.Reason, StringComparison.Ordinal);

            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger, force: true);
            Assert.Equal(0, summary.Processed);
            Assert.Empty(protocol.Deliveries);
            var submission = await ledger.GetSubmissionAsync(submissionId);
            Assert.Equal(1, submission!.SkippedStale);
            var stale = (await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10)).Single(a => a.Phase == AttemptPhases.Stale);
            Assert.Equal(AttemptOutcome.Skipped, stale.Outcome);
            Assert.Equal(submissionId, stale.SubmissionId);

            // The same row read again, as the overlap of the next run's window reads it, is the same skip: recorded once.
            // Stamped again by the ingestion table, it is another row, and recorded again.
            await RunAsync(runtime, protocol, ledger, force: true);
            Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10), a => a.Phase == AttemptPhases.Stale);
            _clock.Advance(TimeSpan.FromMinutes(10));
            tables.Records[0].UpdatedUtc = Now;
            await RunAsync(runtime, protocol, ledger, force: true);
            Assert.Equal(2, (await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10)).Count(a => a.Phase == AttemptPhases.Stale));
            Assert.Empty(protocol.Deliveries);
        }
    }

    /// <summary>
    /// A second mapping over the well log rows, into another OSDU kind: the wellbore each log was run in, keyed like the log
    /// itself, so its records carry the same delivery keys as the well log flow's.
    /// </summary>
    private const string LogWellboreMapping = """
        documentType: mapping
        name: LogWellbore
        version: 1.0.0
        template:
          kind: osdu:wks:master-data--Wellbore:1.3.0
          version: 58d6bdbd9d066a06
        description: The wellbore each well log was run in, one record per log row.
        dataset:
          system: welldb
          key: [source_project, log_id]
          label: "{wellbore_uwi} ({log_id})"
        parameters:
          dataPartition:
            required: true
            description: The OSDU data partition record ids are minted in.
          aclOwner: { required: true }
          aclViewer: { required: true }
          legalTag: { required: true }
        record:
          acl:
            owners: ["{$param.aclOwner}"]
            viewers: ["{$param.aclViewer}"]
          legal:
            legaltags: ["{$param.legalTag}"]
            otherRelevantDataCountries: [US]
          data:
            FacilityName: { $from: wellbore_uwi }
        """;

    /// <summary>The sample well log flow turned into a second flow over the same rows, rendering <see cref="LogWellboreMapping"/>.</summary>
    private FlowDefinition WellboresFlow(FlowDefinition flow)
    {
        var mappings = Path.Combine(_root, "wellbore-mappings");
        Directory.CreateDirectory(mappings);
        File.WriteAllText(Path.Combine(mappings, "LogWellbore@1.0.0.yaml"), LogWellboreMapping);
        return flow with
        {
            Name = "wells-welllog-wellbores",
            Render = flow.Render with { Mapping = "LogWellbore@1.0.0", MappingsDirectory = mappings },
            Target = flow.Target with { Protocol = DeliveryProtocol.Storage },
        };
    }

    [Fact]
    public async Task Two_flows_deliver_the_same_rows_into_different_kinds_and_keep_their_own_ledgers()
    {
        var tables = await EstateAsync();
        var (logs, logProtocol, ledger) = await RuntimeAsync(tables);
        var (wellbores, wellboreProtocol, _) = await RuntimeAsync(tables, WellboresFlow);
        using (logs)
        using (wellbores)
        {
            var (logRun, logSubmission) = await RunAsync(logs, logProtocol, ledger);
            var (wellboreRun, wellboreSubmission) = await RunAsync(wellbores, wellboreProtocol, ledger);
            Assert.Equal((LogCount, LogCount), (logRun.Delivered, wellboreRun.Delivered));
            Assert.All(logProtocol.Deliveries, w => Assert.StartsWith("dev:work-product-component--WellLog:", w.TargetId, StringComparison.Ordinal));
            Assert.All(wellboreProtocol.Deliveries, w => Assert.StartsWith("dev:master-data--Wellbore:", w.TargetId, StringComparison.Ordinal));
            Assert.All(wellboreProtocol.Deliveries, w => Assert.False(w.DeliverPayload));

            // One row, one key, two records: each names its own mapping, OSDU id, submission and history, and both name the
            // same ingestion file and row as their origin.
            for (var i = 0; i < LogCount; i++)
            {
                var key = SampleEstate.Key(i);
                var log = await ledger.GetRecordAsync(logs.Flow.Id, key);
                var wellbore = await ledger.GetRecordAsync(wellbores.Flow.Id, key);
                Assert.Equal(("WellLog", RecordStatus.Delivered, (Guid?)logSubmission), (log!.MappingName, log.Status, log.LastSubmissionId));
                Assert.Equal(("LogWellbore", RecordStatus.Delivered, (Guid?)wellboreSubmission), (wellbore!.MappingName, wellbore.Status, wellbore.LastSubmissionId));
                Assert.NotEqual(log.TargetId, wellbore.TargetId);
                Assert.Equal((SampleEstate.FileName, (long?)(i + 1)), (log.SourceFileName, log.SourceRowNumber));
                Assert.Equal((SampleEstate.FileName, (long?)(i + 1)), (wellbore.SourceFileName, wellbore.SourceRowNumber));
                Assert.Equal(logSubmission, Assert.Single(await ledger.ListAttemptsAsync(logs.Flow.Id, key, 10)).SubmissionId);
                Assert.Equal(wellboreSubmission, Assert.Single(await ledger.ListAttemptsAsync(wellbores.Flow.Id, key, 10)).SubmissionId);
                Assert.Equal(2, (await ledger.LookupAsync(key.Value.ToString(), 10)).Count);
            }

            Assert.Equal(logSubmission, Assert.Single(await ledger.ListSubmissionsAsync(logs.Flow.Id, 10)).SubmissionId);
            Assert.Equal(wellboreSubmission, Assert.Single(await ledger.ListSubmissionsAsync(wellbores.Flow.Id, 10)).SubmissionId);
            Assert.Equal(LogCount, (await ledger.StatsAsync(logs.Flow.Id, Now)).Delivered);
            Assert.Equal(LogCount, (await ledger.StatsAsync(wellbores.Flow.Id, Now)).Delivered);
            Assert.Equal(logSubmission, (await ledger.GetWatermarkAsync(logs.Flow.Id, Planner.ScopeKey(logs.Parameters)))!.SubmissionId);
            Assert.Equal(wellboreSubmission, (await ledger.GetWatermarkAsync(wellbores.Flow.Id, Planner.ScopeKey(wellbores.Parameters)))!.SubmissionId);

            // A column only the well log reads changes: that flow sends its document again, the other renders its own,
            // finds it unchanged, sends nothing and records the change of its row. Each decides from its own record.
            logProtocol.Deliveries.Clear();
            wellboreProtocol.Deliveries.Clear();
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc(0).AddHours(2));
            Assert.Equal(1, (await RunAsync(logs, logProtocol, ledger, force: true)).Work.Delivered);
            Assert.Equal(0, (await RunAsync(wellbores, wellboreProtocol, ledger, force: true)).Work.Processed);
            Assert.Equal(SampleEstate.Key(0), Assert.Single(logProtocol.Deliveries).Key);
            Assert.Empty(wellboreProtocol.Deliveries);
            Assert.Equal(2, (await ledger.ListAttemptsAsync(logs.Flow.Id, SampleEstate.Key(0), 10)).Count);
            var wellboreHistory = await ledger.ListAttemptsAsync(wellbores.Flow.Id, SampleEstate.Key(0), 10);
            Assert.Equal(2, wellboreHistory.Count);
            Assert.Equal((AttemptOutcome.Skipped, AttemptPhases.Identical, (DateTime?)Now), (wellboreHistory[0].Outcome, wellboreHistory[0].Phase, wellboreHistory[0].SourceUpdatedUtc));

            // Removing the wellbores takes the wellbore records out of OSDU and leaves every well log where it is.
            wellbores.Actor = "gui:tahir";
            var removed = await wellbores.RemoveAsync(RemovalSelection.Of([SampleEstate.Key(0)]), RemovalChoice.Record);
            Assert.Equal(1, removed.Removed);
            Assert.StartsWith("dev:master-data--Wellbore:", Assert.Single(wellboreProtocol.Deletes).TargetId, StringComparison.Ordinal);
            Assert.Empty(logProtocol.Deletes);
            Assert.Equal(RecordStatus.Deleted, (await ledger.GetRecordAsync(wellbores.Flow.Id, SampleEstate.Key(0)))!.Status);
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(logs.Flow.Id, SampleEstate.Key(0)))!.Status);
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = wellbores.Flow.Id, DeliveryKey = SampleEstate.Key(0).Value }));
            Assert.Equal("delete", activity.Kind);
            Assert.Empty(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = logs.Flow.Id, DeliveryKey = SampleEstate.Key(0).Value }));
        }
    }

    [Fact]
    public async Task A_second_flow_writing_the_same_osdu_records_is_held_and_never_reaches_them()
    {
        var tables = await EstateAsync();
        var (logs, logProtocol, ledger) = await RuntimeAsync(tables);
        var (copy, copyProtocol, _) = await RuntimeAsync(tables, flow => flow with { Name = "wells-welllog-copy" });
        using (logs)
        using (copy)
        {
            Assert.Equal(LogCount, (await RunAsync(logs, logProtocol, ledger)).Work.Delivered);

            // Same mapping, same partition: the copy would write the very records the first flow owns. It sends nothing,
            // and every record it planned is held with the owner named, so the reason is on the record's own page.
            var (copied, copySubmission) = await RunAsync(copy, copyProtocol, ledger);
            Assert.Equal(0, copied.Processed);
            Assert.Empty(copyProtocol.Deliveries);
            var submission = await ledger.GetSubmissionAsync(copySubmission);
            Assert.Equal((0L, (long)LogCount), (submission!.Delivered, submission.Held));
            for (var i = 0; i < LogCount; i++)
            {
                var held = await ledger.GetRecordAsync(copy.Flow.Id, SampleEstate.Key(i));
                Assert.Equal(RecordStatus.Held, held!.Status);
                Assert.True(held.Blocked);
                Assert.Null(held.TargetId);
                Assert.Contains("already claimed by flow 'welldb-welllog-03-header-delivery'", held.LastError, StringComparison.Ordinal);
                Assert.Equal(SampleEstate.FileName, held.PendingSourceFileName);
                var attempt = Assert.Single(await ledger.ListAttemptsAsync(copy.Flow.Id, SampleEstate.Key(i), 10));
                Assert.Equal((AttemptOutcome.Held, "render"), (attempt.Outcome, attempt.Phase));
                Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(logs.Flow.Id, SampleEstate.Key(i)))!.Status);
            }

            // Removing the copy's records never reaches the first flow's OSDU records: the copy wrote nothing.
            copy.Actor = "gui:tahir";
            var removal = await copy.RemoveAsync(RemovalSelection.Of([.. Enumerable.Range(0, LogCount).Select(SampleEstate.Key)]), RemovalChoice.Everything);
            Assert.Equal((LogCount, 0), (removal.Skipped, removal.Removed));
            Assert.Empty(copyProtocol.Deletes);

            // Released, the copy plans the records again and meets the same owner: held again, still nothing sent.
            await copy.ReleaseAsync(null);
            _clock.Advance(TimeSpan.FromMinutes(1));
            copy.Selection = SourceSelection.Full();
            var (again, _) = await RunAsync(copy, copyProtocol, ledger, force: true);
            Assert.Equal(0, again.Processed);
            Assert.Empty(copyProtocol.Deliveries);
            Assert.All(
                await ledger.ListAsync(copy.Flow.Id, new RecordQuery()),
                r => Assert.Equal((RecordStatus.Held, true), (r.Status, r.Blocked)));
        }
    }

    [Fact]
    public async Task A_renamed_flow_that_keeps_the_ledger_of_its_old_name_carries_on_where_that_name_stopped()
    {
        const string OldName = "welldb-welllog-03-header-delivery";
        const string NewName = "welldb-welllog-03-header-delivery-v140";
        var tables = await EstateAsync();
        var (logs, logProtocol, ledger) = await RuntimeAsync(tables);
        Guid original;
        using (logs)
        {
            Assert.Equal(LogCount, (await RunAsync(logs, logProtocol, ledger)).Work.Delivered);
            original = logs.Flow.Id;
        }

        // Renamed alone, the flow keeps a new, empty ledger: every record is held, and the hold names the ledger to keep,
        // since no repository declares the flow that claimed the ids.
        var (renamed, renamedProtocol, _) = await RuntimeAsync(tables, flow => flow with { Name = NewName });
        using (renamed)
        {
            Assert.NotEqual(original, renamed.Flow.Id);
            await RunAsync(renamed, renamedProtocol, ledger);
            Assert.Empty(renamedProtocol.Deliveries);
            var held = await ledger.GetRecordAsync(renamed.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(RecordStatus.Held, held!.Status);
            Assert.Contains($"already claimed by flow '{OldName}' ({original:D}), which no repository declares any more", held.LastError, StringComparison.Ordinal);
            Assert.Contains("its ledger is orphaned: have this flow take it over on the Orphaned ledgers page", held.LastError, StringComparison.Ordinal);
            Assert.Contains($"with ledger: {OldName} in its document", held.LastError, StringComparison.Ordinal);
        }

        // With ledger: naming the old name, it is the same ledger: nothing changed in the source, so nothing is sent or held,
        // and every record keeps its history and the OSDU id it claimed.
        var (kept, keptProtocol, _) = await RuntimeAsync(tables, flow => flow with { Name = NewName, AdoptedLedger = OldName });
        using (kept)
        {
            Assert.Equal(original, kept.Flow.Id);
            Assert.Equal(NewName, kept.Flow.Label);
            await RunAsync(kept, keptProtocol, ledger);
            Assert.Empty(keptProtocol.Deliveries);
            var records = await ledger.ListAsync(original, new RecordQuery());
            Assert.Equal(LogCount, records.Count);
            Assert.All(records, r => Assert.Equal((RecordStatus.Delivered, r.TargetId), (r.Status, r.ClaimedTargetId)));
            Assert.All(records, r => Assert.NotNull(r.ClaimedTargetId));

            // A redelivery the renamed flow asks for reaches the record the old name delivered, at the id it claimed.
            await kept.RedeliverAsync([SampleEstate.Key(0)], RedeliverScope.All);
            var record = await ledger.GetRecordAsync(original, SampleEstate.Key(0));
            kept.Selection = SourceSelection.ForKeys([KeyTuple.FromJson(record!.SourceKeyJson!)]);
            var (again, _) = await RunAsync(kept, keptProtocol, ledger, force: true);
            Assert.Equal(1, again.Delivered);
            Assert.Equal(record.ClaimedTargetId, Assert.Single(keptProtocol.Deliveries).TargetId);
        }
    }

    [Fact]
    public async Task A_renamed_pipeline_s_run_finishes_the_move_the_sync_asked_for_and_carries_on_where_the_old_name_stopped()
    {
        const string OldName = "welldb-welllog-03-header-delivery";
        const string NewName = "welldb-welllog-03-header-delivery-v140";
        var tables = await EstateAsync();
        var (logs, logProtocol, ledger) = await RuntimeAsync(tables);
        Guid original;
        using (logs)
        {
            Assert.Equal(LogCount, (await RunAsync(logs, logProtocol, ledger)).Work.Delivered);
            original = logs.Flow.Id;
        }

        // Before the sync pairs the two, the renamed pipeline holds every record the old name claimed, in a ledger of its own.
        Guid renamedId;
        var (renamed, renamedProtocol, _) = await RuntimeAsync(tables, flow => flow with { Name = NewName });
        using (renamed)
        {
            renamedId = renamed.Flow.Id;
            await RunAsync(renamed, renamedProtocol, ledger);
            Assert.All(await ledger.ListAsync(renamedId, new RecordQuery()), r => Assert.Equal(RecordStatus.Held, r.Status));
        }

        var historyBefore = (await ledger.ListAttemptsAsync(original, SampleEstate.Key(0), 50)).Count;
        await using (var context = _db.CreateDbContext())
        {
            var partition = await context.DeliveryLedgers.Where(l => l.FlowId == original).Select(l => l.PartitionId).SingleAsync();
            Assert.Equal(LedgerMoveAsk.Asked, (await LedgerMoves.RequestAsync(
                context, new LedgerMoveRequest(partition, original, OldName, OldName, renamedId, NewName, string.Empty, NewName, Guid.NewGuid()), Now, CancellationToken.None)).Ask);
        }

        // The renamed pipeline's next run binds its ledger, which finishes the move first: the old records, with their history
        // and claims, are its own, the records it had held gave way to them, and nothing changed in the source, so nothing is sent.
        var (moved, movedProtocol, _) = await RuntimeAsync(tables, flow => flow with { Name = NewName });
        using (moved)
        {
            Assert.Empty(await ledger.ListAsync(original, new RecordQuery()));
            Assert.Null(await ledger.GetLedgerAsync(original));
            var records = await ledger.ListAsync(renamedId, new RecordQuery());
            Assert.Equal(LogCount, records.Count);
            Assert.All(records, r => Assert.Equal((RecordStatus.Delivered, r.TargetId), (r.Status, r.ClaimedTargetId)));
            var history = await ledger.ListAttemptsAsync(renamedId, SampleEstate.Key(0), 50);
            Assert.Equal(historyBefore + 1, history.Count);
            Assert.Contains(history, a => a.Outcome == AttemptOutcome.Delivered);
            Assert.Contains(history, a => a.Outcome == AttemptOutcome.Held);

            var move = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = renamedId, Kind = LedgerMoves.Kind }));
            Assert.Equal(("completed", LedgerMoves.Actor), (move.Outcome, move.Actor));
            Assert.Equal(
                $"moved {LogCount} record(s) of '{OldName}' to '{NewName}', the same document under its newest name, with their history, their OSDU ids and the reading position; "
                + $"{LogCount} record(s) '{NewName}' had held, which claimed no OSDU id, gave way to them",
                move.Summary);
            Assert.Empty(await ledger.CompleteLedgerMovesAsync(null));

            await RunAsync(moved, movedProtocol, ledger);
            Assert.Empty(movedProtocol.Deliveries);

            // A redelivery the renamed pipeline asks for reaches the record the old name delivered, at the id it claimed.
            await moved.RedeliverAsync([SampleEstate.Key(0)], RedeliverScope.All);
            var record = await ledger.GetRecordAsync(renamedId, SampleEstate.Key(0));
            moved.Selection = SourceSelection.ForKeys([KeyTuple.FromJson(record!.SourceKeyJson!)]);
            var (again, _) = await RunAsync(moved, movedProtocol, ledger, force: true);
            Assert.Equal(1, again.Delivered);
            Assert.Equal(record.ClaimedTargetId, Assert.Single(movedProtocol.Deliveries).TargetId);
        }
    }

    [Fact]
    public async Task Each_attempt_names_the_correlation_id_its_requests_carried()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            // As the executor opens it around every run.
            var runId = Guid.CreateVersion7();
            using (OsduCorrelation.ForRun("wl", FlowTelemetry.Delivery, runId))
            {
                await RunAsync(runtime, protocol, ledger);
            }

            var sent = protocol.Correlations[protocol.Deliveries.FindIndex(w => w.Key == SampleEstate.Key(0))];
            Assert.Equal($"osdu-delivery-wl-dlv-{runId:D}", sent);
            var attempt = Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10));
            using var result = System.Text.Json.JsonDocument.Parse(attempt.ResultJson!);
            Assert.Equal(sent, result.RootElement.GetProperty("correlationId").GetString());
            Assert.Null(attempt.Error);
            Assert.Null(OsduCorrelation.Current);
        }
    }

    [Fact]
    public async Task A_flow_whose_mapping_keys_records_differently_than_its_source_is_refused()
    {
        var tables = await EstateAsync();
        var (runtime, _, _) = await RuntimeAsync(
            tables,
            flow => flow with { Source = flow.Source with { Record = flow.Source.Record with { Key = ["log_id"] } } });
        using (runtime)
        {
            var ex = await Assert.ThrowsAsync<FlowValidationException>(() => runtime.PlanAsync());
            Assert.Contains("source.record.key is [log_id]", ex.Message, StringComparison.Ordinal);
            Assert.Contains("the same columns in the same order", ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>The curve unit map's neutron porosity row, spelled the way the well database writes it.</summary>
    private static ReferenceType UnitAlias(string osduUnit) => LookupCacheTests.Lookup(
        "UnitAlias", "source_unit", ("V/V", new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["osdu_unit"] = osduUnit }));

    [Fact]
    public async Task Records_a_cache_change_holds_back_are_counted_as_awaiting_approval_not_as_unchanged()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            Assert.Equal(LogCount, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);

            // The unit the two logs with a neutron porosity matched by moves, and the change waits for a decision, so their sets are
            // gated. The other logs do not read it.
            var impact = await new CacheImpactAnalyzer(ledger, _clock, NullLogger.Instance)
                .AnalyzeAsync(Samples.SampleCacheScope, UnitAlias("v/v"), UnitAlias("v/v-2"), CacheChangeMode.Approve, "20260908T212727Z", "20260909T000000Z");
            Assert.NotEqual(0, impact.Changes);
            Assert.NotEmpty(await ledger.GatedCacheSetsAsync());

            _clock.Advance(TimeSpan.FromMinutes(10));
            foreach (var record in tables.Records)
            {
                record.UpdatedUtc = Now;
            }

            var plan = await runtime.PlanAsync(force: true);
            Assert.Equal(2, plan.AwaitingApproval);
            Assert.All(plan.Entries.Where(e => e.SkipTier == SkipTier.Approval), e => Assert.Equal(PlannedAction.Skip, e.Action));

            // The held-back records are not unchanged: their document moved with the cache, and an approval is what
            // decides whether it is sent. The other logs are unchanged.
            Assert.Equal(LogCount - 2, plan.Skips);

            var (summary, submissionId) = await RunAsync(runtime, protocol, ledger, force: true);
            Assert.Equal(0, summary.Processed);
            var submission = await ledger.GetSubmissionAsync(submissionId);
            Assert.Equal(2, submission!.AwaitingApproval);
            Assert.Equal(LogCount - 2, submission.SkippedUnchanged);
            Assert.Equal(0, submission.Delivered);
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

        GC.SuppressFinalize(this);
    }
}
