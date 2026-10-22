using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Records whose OSDU ids are made from their key (<c>dataset.idFrom: key</c>), delivered end to end over the sample
/// WellLog mapping, the sample estate, the module's database on SQL Server and a fake protocol: what is sent, that an update
/// and a retry land on the id the record claimed, and that no record's id ever moves or is shared, whether a mapping's ids
/// change under delivered records or two keys of one flow come to give one id.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed partial class RecordIdFromKeyDeliveryTests : IDisposable
{
    private const string WellLogs = "work-product-component--WellLog";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static int LogCount => SampleEstate.Logs().Count;

    [GeneratedRegex(@"(?m)^  key: \[source_project, log_id\]")]
    private static partial Regex KeyLine();

    [GeneratedRegex(@"(?m)^  system: welldb\b")]
    private static partial Regex SystemLine();

    /// <summary>The sample WellLog mapping, changed by <paramref name="change"/>, in a folder of its own a flow renders from.</summary>
    private string Mappings(string name, Func<string, string> change)
    {
        var folder = Path.Combine(_root, "mappings-" + name);
        Directory.CreateDirectory(folder);
        var yaml = File.ReadAllText(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"));
        var changed = change(yaml);
        Assert.NotEqual(yaml, changed);
        File.WriteAllText(Path.Combine(folder, "WellLog@1.4.0.yaml"), changed);
        return folder;
    }

    private static string FromKey(string yaml) => KeyLine().Replace(yaml, "$0\n  idFrom: key", 1);

    private static Func<FlowDefinition, FlowDefinition> Rendering(string mappings, string? name = null)
        => flow => flow with { Name = name ?? flow.Name, Render = flow.Render with { MappingsDirectory = mappings } };

    /// <summary>The id a log's record is delivered to under <c>idFrom: key</c>: its project and its log id, the slash of a log id encoded.</summary>
    private static string CodeId(SampleLog log) => TargetId.ComposeFromKey(Samples.SamplePartition, WellLogs, [log.SourceProject, log.LogId], out _)!;

    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> RuntimeAsync(
        MemoryIngestionTables tables, Func<FlowDefinition, FlowDefinition>? adjust = null)
    {
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        // The fake platform holds what it was sent and nothing else, which is what the intake asks before a first claim.
        protocol.VerifyWith = id => protocol.Deliveries.Any(d => string.Equals(d.TargetId, id, StringComparison.Ordinal))
            ? new VerifyResult(VerifyOutcome.Match, 1, null)
            : new VerifyResult(VerifyOutcome.Missing, null, "record not found");
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        var flow = adjust is null ? Samples.LocalFlow(_root) : adjust(Samples.LocalFlow(_root));
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        return (runtime, protocol, ledger);
    }

    private DeliveryWorker Worker(FlowRuntime runtime, FakeProtocol protocol, OsduLedger ledger) => new(
        ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
        CompositeDeliveryListener.Empty, Samples.Logger<DeliveryWorker>(), "test-worker") { MaxWait = null };

    /// <summary>One run's work: plan into batches, drain them, and close the submission, as a deliver run does.</summary>
    private async Task<(WorkerSummary Work, Guid SubmissionId)> RunAsync(FlowRuntime runtime, FakeProtocol protocol, OsduLedger ledger, bool force = false)
    {
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force);
        if (intake.NothingToDo)
        {
            return (WorkerSummary.Empty, intake.Submission.SubmissionId);
        }

        var summary = await Worker(runtime, protocol, ledger).DrainAsync(intake.Submission.SubmissionId);
        await runtime.Intake.CompleteAsync(intake.Submission.SubmissionId, runtime.Flow.Id);
        return (summary, intake.Submission.SubmissionId);
    }

    [Fact]
    public async Task Records_whose_ids_are_made_from_the_key_are_sent_retried_and_updated_under_the_one_id_each_claimed()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, Rendering(Mappings("key", FromKey)));
        using (runtime)
        {
            // The first log fails once with a transient error and goes out on its retry; the rest go out at once.
            var failures = 0;
            protocol.FailWith = work => work.Key == SampleEstate.Key(0) && failures++ == 0 ? new OsduStatusException(503, "HTTP 503 Service Unavailable") : null;
            var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
            var submission = intake.Submission.SubmissionId;
            var first = await Worker(runtime, protocol, ledger).DrainAsync(submission);
            Assert.Equal(((long)LogCount - 1, 1L), (first.Delivered, first.Retried));
            _clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(1, (await Worker(runtime, protocol, ledger).DrainAsync(submission)).Delivered);
            await runtime.Intake.CompleteAsync(submission, runtime.Flow.Id);

            foreach (var log in SampleEstate.Logs())
            {
                var record = await ledger.GetRecordAsync(runtime.Flow.Id, log.Key);
                Assert.Equal((RecordStatus.Delivered, CodeId(log), CodeId(log)), (record!.Status, record.TargetId, record.ClaimedTargetId));
                Assert.All(protocol.Deliveries.Where(d => d.Key == log.Key), d =>
                {
                    Assert.Equal(CodeId(log), d.TargetId);
                    Assert.Equal(CodeId(log), d.Document["id"]!.GetValue<string>());
                });
            }

            var retried = protocol.Deliveries.Where(d => d.Key == SampleEstate.Key(0)).ToList();
            Assert.Equal(2, retried.Count);
            Assert.Contains("%2F", CodeId(SampleEstate.Logs().First(l => l.LogId.Contains('/', StringComparison.Ordinal))), StringComparison.Ordinal);

            // An edited row is the same record, sent again to the id it claimed: a new version of the same OSDU record.
            protocol.Deliveries.Clear();
            protocol.FailWith = null;
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc(0).AddHours(2));
            Assert.Equal(1, (await RunAsync(runtime, protocol, ledger, force: true)).Work.Delivered);
            var update = Assert.Single(protocol.Deliveries);
            Assert.Equal((SampleEstate.Key(0), CodeId(SampleEstate.Logs()[0])), (update.Key, update.TargetId));
            Assert.Equal(CodeId(SampleEstate.Logs()[0]), update.Document["id"]!.GetValue<string>());
            Assert.Equal(3, (await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10)).Count);

            // The record is found by its code, as by every other value it is known by.
            Assert.Contains(await ledger.LookupAsync(CodeId(SampleEstate.Logs()[0]), 10), r => r.DeliveryKey == SampleEstate.Key(0));
        }
    }

    [Fact]
    public async Task Records_delivered_under_the_delivery_keys_ids_keep_them_when_the_mapping_makes_its_ids_from_the_key()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var (guids, guidProtocol, ledger) = await RuntimeAsync(tables);
        using (guids)
        {
            Assert.Equal(LogCount, (await RunAsync(guids, guidProtocol, ledger)).Work.Delivered);
        }

        // The same flow now renders from a mapping that makes its ids from the key. Every record already claimed its
        // delivery key's id, so nothing is sent: each record is held, naming both ids, and keeps the id it claimed.
        var (codes, codeProtocol, _) = await RuntimeAsync(tables, Rendering(Mappings("key", FromKey)));
        using (codes)
        {
            Assert.Equal(guids.Flow.Id, codes.Flow.Id);
            var (run, _) = await RunAsync(codes, codeProtocol, ledger, force: true);
            Assert.Equal(0, run.Processed);
            Assert.Empty(codeProtocol.Deliveries);
            foreach (var log in SampleEstate.Logs())
            {
                var record = await ledger.GetRecordAsync(codes.Flow.Id, log.Key);
                var guid = TargetId.Compose(Samples.SamplePartition, WellLogs, log.Key);
                Assert.Equal((RecordStatus.Held, guid, guid), (record!.Status, record.TargetId, record.ClaimedTargetId));
                Assert.Contains($"the mapping now gives this record the OSDU id {CodeId(log)}, and the record claimed {guid}", record.LastError, StringComparison.Ordinal);
            }

            // Released, a record meets the same refusal: the id a record claimed is its id for good.
            await codes.ReleaseAsync(null);
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(0, (await RunAsync(codes, codeProtocol, ledger, force: true)).Work.Processed);
            Assert.Empty(codeProtocol.Deliveries);

            // Removing the records takes the delivery keys' ids out of OSDU, through the ledger that claimed them.
            codes.Actor = "gui:tahir";
            var removed = await codes.RemoveAsync(RemovalSelection.Of([.. SampleEstate.Logs().Select(l => l.Key)]), RemovalChoice.Record);
            Assert.Equal(LogCount, removed.Removed);
            Assert.Equal(
                SampleEstate.Logs().Select(l => TargetId.Compose(Samples.SamplePartition, WellLogs, l.Key)).Order(StringComparer.Ordinal),
                codeProtocol.Deletes.Select(d => d.TargetId).Order(StringComparer.Ordinal));
        }

        // A ledger of its own delivers the rows under the ids the key gives, and the first ledger keeps the history of the
        // records it removed, each still naming the id it held.
        var (fresh, freshProtocol, _) = await RuntimeAsync(tables, Rendering(Mappings("fresh", FromKey), "wells-welllog-codes"));
        using (fresh)
        {
            Assert.NotEqual(guids.Flow.Id, fresh.Flow.Id);
            Assert.Equal(LogCount, (await RunAsync(fresh, freshProtocol, ledger)).Work.Delivered);
            Assert.Equal(SampleEstate.Logs().Select(CodeId).Order(StringComparer.Ordinal), freshProtocol.Deliveries.Select(d => d.TargetId).Order(StringComparer.Ordinal));
            foreach (var log in SampleEstate.Logs())
            {
                var removed = await ledger.GetRecordAsync(guids.Flow.Id, log.Key);
                Assert.Equal((RecordStatus.Deleted, TargetId.Compose(Samples.SamplePartition, WellLogs, log.Key)), (removed!.Status, removed.ClaimedTargetId));
                Assert.Equal(CodeId(log), (await ledger.GetRecordAsync(fresh.Flow.Id, log.Key))!.ClaimedTargetId);
            }
        }
    }

    [Fact]
    public async Task Two_records_of_one_flow_never_share_an_id_when_the_mapping_re_keys_them()
    {
        // Delivered under one system, then rendered under another: every row has a new delivery key, so a new record, whose
        // key gives the id the first record claimed. The new records are held naming the record that holds each id.
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var (first, firstProtocol, ledger) = await RuntimeAsync(tables, Rendering(Mappings("key", FromKey)));
        using (first)
        {
            Assert.Equal(LogCount, (await RunAsync(first, firstProtocol, ledger)).Work.Delivered);
        }

        var rekeyed = Mappings("rekeyed", yaml => SystemLine().Replace(FromKey(yaml), "  system: welldb-v2", 1));
        var (second, secondProtocol, _) = await RuntimeAsync(tables, Rendering(rekeyed));
        using (second)
        {
            Assert.Equal(first.Flow.Id, second.Flow.Id);
            var (run, _) = await RunAsync(second, secondProtocol, ledger, force: true);
            Assert.Equal(0, run.Processed);
            Assert.Empty(secondProtocol.Deliveries);
            foreach (var log in SampleEstate.Logs())
            {
                var rekey = DeliveryKey.Derive("welldb-v2", [log.SourceProject, log.LogId]);
                var held = await ledger.GetRecordAsync(second.Flow.Id, rekey);
                Assert.Equal((RecordStatus.Held, (string?)null, (string?)null), (held!.Status, held.TargetId, held.ClaimedTargetId));
                Assert.Contains($"OSDU id {CodeId(log)} is already claimed by record ", held.LastError, StringComparison.Ordinal);
                Assert.Contains($"({log.Key}) of this flow", held.LastError, StringComparison.Ordinal);
                Assert.Equal((RecordStatus.Delivered, CodeId(log)), ((await ledger.GetRecordAsync(first.Flow.Id, log.Key))!.Status, (await ledger.GetRecordAsync(first.Flow.Id, log.Key))!.ClaimedTargetId));
            }
        }
    }

    [Fact]
    public async Task A_record_whose_id_osdu_already_holds_is_never_sent_until_the_id_is_free()
    {
        // A record OSDU holds at a code id that no record of the ledger claimed is another system's. The record that would
        // claim the id is held before it claims anything, and nothing is sent; so is one whose id OSDU cannot vouch for.
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, Rendering(Mappings("key", FromKey)));
        using (runtime)
        {
            var foreign = CodeId(SampleEstate.Logs()[0]);
            var unsure = CodeId(SampleEstate.Logs()[1]);
            protocol.VerifyWith = id => id == foreign
                ? new VerifyResult(VerifyOutcome.Match, 7, null)
                : id == unsure ? new VerifyResult(VerifyOutcome.Error, null, "HTTP 503 Service Unavailable") : new VerifyResult(VerifyOutcome.Missing, null, "record not found");

            var (first, _) = await RunAsync(runtime, protocol, ledger);
            Assert.Equal(LogCount - 2, first.Delivered);
            Assert.DoesNotContain(protocol.Deliveries, d => d.TargetId == foreign || d.TargetId == unsure);
            var taken = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal((RecordStatus.Held, (string?)null, (string?)null), (taken!.Status, taken.TargetId, taken.ClaimedTargetId));
            Assert.StartsWith($"OSDU already holds a record at {foreign} at version 7, the OSDU id made from the key, and no record of the ledger claimed it", taken.LastError, StringComparison.Ordinal);
            var undecided = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1));
            Assert.Equal((RecordStatus.Held, (string?)null), (undecided!.Status, undecided.ClaimedTargetId));
            Assert.Contains($"OSDU could not say whether it holds a record at {unsure}", undecided.LastError, StringComparison.Ordinal);

            // Once OSDU holds nothing at either id, a release delivers both, each to its own id.
            protocol.VerifyWith = _ => new VerifyResult(VerifyOutcome.Missing, null, "record not found");
            protocol.Deliveries.Clear();
            await runtime.ReleaseAsync(null);
            _clock.Advance(TimeSpan.FromMinutes(1));
            runtime.Selection = SourceSelection.Full();
            Assert.Equal(2, (await RunAsync(runtime, protocol, ledger, force: true)).Work.Delivered);
            Assert.Equal(new[] { foreign, unsure }.Order(StringComparer.Ordinal), protocol.Deliveries.Select(d => d.TargetId).Order(StringComparer.Ordinal));
            Assert.Equal(foreign, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.ClaimedTargetId);

            // A record that claimed its id is never asked again: its updates go to the id it owns whatever OSDU answers.
            protocol.VerifyWith = _ => new VerifyResult(VerifyOutcome.Match, 99, null);
            protocol.Deliveries.Clear();
            _clock.Advance(TimeSpan.FromMinutes(10));
            SampleEstate.Change(tables.Records[0], "creator", "HAL", Now, SampleWellLogs.UpdatedUtc(0).AddHours(2));
            Assert.Equal(1, (await RunAsync(runtime, protocol, ledger, force: true)).Work.Delivered);
            Assert.Equal(foreign, Assert.Single(protocol.Deliveries).TargetId);
        }
    }

    [Fact]
    public async Task A_record_whose_id_osdu_already_holds_is_adopted_and_the_next_run_writes_it_there()
    {
        // An earlier ledger, or another instance of the delivery, wrote the record OSDU holds at a code id, and this ledger has
        // no record of it. The record is held, keeping the id and the version OSDU answered; adopted, it claims the id at that
        // version, and the next run writes it there as an update of the record OSDU holds.
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, Rendering(Mappings("key", FromKey)));
        using (runtime)
        {
            var earlier = CodeId(SampleEstate.Logs()[0]);
            var other = CodeId(SampleEstate.Logs()[1]);
            protocol.VerifyWith = id => id == earlier || id == other
                ? new VerifyResult(VerifyOutcome.Match, 7, null)
                : new VerifyResult(VerifyOutcome.Missing, null, "record not found");
            Assert.Equal(LogCount - 2, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);

            var held = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal((RecordStatus.Held, (string?)null, (string?)null, earlier, 7L), (held!.Status, held.TargetId, held.ClaimedTargetId, held.OccupiedTargetId, held.OccupiedTargetVersion));
            Assert.Contains("Adopt it when it is this record's", held.LastError, StringComparison.Ordinal);

            // A named record held for no id OSDU holds (one delivered here) has nothing to adopt, and is left as it is.
            runtime.Actor = "gui:tahir";
            var adopted = Assert.Single(await runtime.AdoptAsync([SampleEstate.Key(0), SampleEstate.Key(2)]));
            Assert.Equal((SampleEstate.Key(0), earlier, (long?)7), (adopted.Key, adopted.TargetId, adopted.Version));
            var claimed = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(
                (RecordStatus.Held, false, earlier, earlier, (long?)7, (string?)null, (long?)null),
                (claimed!.Status, claimed.Blocked, claimed.TargetId, claimed.ClaimedTargetId, claimed.TargetVersion, claimed.OccupiedTargetId, claimed.OccupiedTargetVersion));
            Assert.NotNull(claimed.PlanRequestedUtc);
            Assert.StartsWith($"adopted by gui:tahir: the record claims {earlier}", claimed.LastError, StringComparison.Ordinal);
            var adoption = Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10), a => a.Phase == AttemptPhases.Adopt);
            Assert.Equal((AttemptOutcome.Skipped, (long?)7, "gui:tahir"), (adoption.Outcome, adoption.TargetVersion, adoption.Worker));
            Assert.Contains($"\"targetId\":\"{earlier}\"", adoption.ResultJson, StringComparison.Ordinal);
            Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, DeliveryKey = SampleEstate.Key(0).Value, Kind = "adopt" }));
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2)))!.Status);
            Assert.Equal(other, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1)))!.OccupiedTargetId);

            // The next run writes the adopted record to the id, told the version OSDU holds there, so the write keeps what
            // OSDU keeps of a record and a write OSDU finds unchanged lands at it. OSDU is not asked about the id again.
            protocol.Deliveries.Clear();
            protocol.Verifies.Clear();
            _clock.Advance(TimeSpan.FromMinutes(1));
            runtime.Selection = SourceSelection.Full();
            Assert.Equal(1, (await RunAsync(runtime, protocol, ledger, force: true)).Work.Delivered);
            var write = Assert.Single(protocol.Deliveries);
            Assert.Equal((SampleEstate.Key(0), earlier, (long?)7), (write.Key, write.TargetId, write.ExistingVersion));
            Assert.DoesNotContain(protocol.Verifies, v => v.TargetId == earlier);
            var delivered = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal((RecordStatus.Delivered, earlier, (string?)null), (delivered!.Status, delivered.ClaimedTargetId, delivered.OccupiedTargetId));

            // A record that claimed its id has nothing left to adopt; the one still held for an id OSDU holds keeps it.
            Assert.Empty(await runtime.AdoptAsync([SampleEstate.Key(0)]));
            var still = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1));
            Assert.Equal((RecordStatus.Held, true, other), (still!.Status, still.Blocked, still.OccupiedTargetId));
        }
    }

    [Fact]
    public async Task An_issue_s_records_are_adopted_together_and_an_id_another_flow_claimed_since_is_left_to_it()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var first = CodeId(SampleEstate.Logs()[0]);
        var second = CodeId(SampleEstate.Logs()[1]);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, Rendering(Mappings("key", FromKey)));
        using var owner = runtime;
        protocol.VerifyWith = id => id == first || id == second
            ? new VerifyResult(VerifyOutcome.Match, 3, null)
            : new VerifyResult(VerifyOutcome.Missing, null, "record not found");
        Assert.Equal(LogCount - 2, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);

        // Another flow of the partition, reading the same rows, claims the second id meanwhile; the first id it is held for.
        var (codes, codesProtocol, _) = await RuntimeAsync(tables, Rendering(Mappings("fresh", FromKey), "wells-welllog-codes"));
        using var other = codes;
        codesProtocol.VerifyWith = id => id == first ? new VerifyResult(VerifyOutcome.Match, 3, null) : new VerifyResult(VerifyOutcome.Missing, null, "record not found");
        await RunAsync(codes, codesProtocol, ledger);
        Assert.Equal(second, (await ledger.GetRecordAsync(codes.Flow.Id, SampleEstate.Key(1)))!.ClaimedTargetId);
        Assert.Equal(first, (await ledger.GetRecordAsync(codes.Flow.Id, SampleEstate.Key(0)))!.OccupiedTargetId);

        // Both records of the first flow are held by one issue; adopting it takes the first id and leaves the second, which
        // another record claimed since the hold, to that record.
        var issue = (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.ProblemHash!.Value;
        Assert.Equal(issue, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1)))!.ProblemHash);
        protocol.Deliveries.Clear();
        runtime.Actor = "cli:tahir@host";
        var adopted = Assert.Single(await runtime.AdoptProblemAsync(issue, "OSDU already holds a record at <id>"));
        Assert.Equal((SampleEstate.Key(0), first, (long?)3), (adopted.Key, adopted.TargetId, adopted.Version));
        Assert.Equal(first, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.ClaimedTargetId);
        var left = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1));
        Assert.Equal((RecordStatus.Held, (string?)null), (left!.Status, left.ClaimedTargetId));
        var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "adopt" }));
        Assert.StartsWith("adopted 1 record(s) issue ", activity.Summary, StringComparison.Ordinal);

        // The other flow's record held for the first id finds it claimed now, and adopts nothing.
        Assert.Empty(await ledger.AdoptOccupiedAsync(codes.Flow.Id, ReleaseSelection.Named([SampleEstate.Key(0)]), "gui:tahir", null, Now));
        Assert.Equal(first, (await ledger.GetRecordAsync(codes.Flow.Id, SampleEstate.Key(0)))!.OccupiedTargetId);

        // An adoption reaches the records it names or one issue's, never every blocked record of a flow, and sends nothing.
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.AdoptOccupiedAsync(runtime.Flow.Id, ReleaseSelection.EveryBlocked, "gui:tahir", null, Now));
        Assert.Empty(protocol.Deliveries);
    }

    [Fact]
    public async Task A_queued_document_written_to_another_id_than_its_records_is_never_sent()
    {
        // The ledger's state was changed outside it after the document was queued: the record now names another id than
        // the one the document carries. The worker sends nothing and holds the record, naming both.
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, Rendering(Mappings("key", FromKey)));
        using (runtime)
        {
            var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
            var moved = $"{Samples.SamplePartition}:{WellLogs}:moved";
            await using (var db = _db.CreateDbContext())
            {
                await db.DeliveryRecords
                    .Where(r => r.FlowId == runtime.Flow.Id && r.DeliveryKey == SampleEstate.Key(0).Value)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.TargetId, moved).SetProperty(r => r.ClaimedTargetId, moved));
            }

            var summary = await Worker(runtime, protocol, ledger).DrainAsync(intake.Submission.SubmissionId);
            Assert.Equal(((long)LogCount - 1, 1L), (summary.Delivered, summary.Held));
            Assert.DoesNotContain(protocol.Deliveries, d => d.Key == SampleEstate.Key(0));
            var held = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(RecordStatus.Held, held!.Status);
            Assert.Equal(
                $"the queued document is written to {CodeId(SampleEstate.Logs()[0])}, and the record's OSDU id is {moved}, so nothing was sent; redeliver it to plan it again",
                held.LastError);
        }
    }

    [Fact]
    public async Task A_record_is_previewed_by_an_id_made_from_its_key_before_any_ledger_holds_it()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var engine = Samples.Engine(ledger: null, _clock, sources: tables);
        using var runtime = await FlowRuntime.CreateAsync(engine, Rendering(Mappings("key", FromKey))(Samples.LocalFlow(_root)), SampleEstate.Values);
        var log = SampleEstate.Logs().First(l => l.LogId.Contains('/', StringComparison.Ordinal));

        var preview = await new RecordPreviewer(runtime).PreviewAsync(CodeId(log));

        Assert.True(preview.Found, preview.Reason);
        Assert.Equal(PreviewKeyForms.OsduId, preview.Asked.How);
        Assert.Equal([log.SourceProject, log.LogId], preview.Source!.KeyParts);
        Assert.Equal(log.Key.Value, preview.Source.DeliveryKey);
        Assert.Equal(CodeId(log), preview.Document!.TargetId);
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
