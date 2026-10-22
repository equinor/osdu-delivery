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
    public async Task Every_record_held_for_an_id_osdu_holds_is_adopted_in_one_batch_and_the_next_run_takes_each_over()
    {
        // A first run of a ledger over records an earlier instance of the delivery wrote: OSDU holds a record at the code id of
        // two logs, and could not say for a third. The two are held keeping the id and the version, the third is held too.
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, Rendering(Mappings("key", FromKey)));
        using (runtime)
        {
            var first = CodeId(SampleEstate.Logs()[0]);
            var second = CodeId(SampleEstate.Logs()[1]);
            var unsure = CodeId(SampleEstate.Logs()[2]);
            var versions = new Dictionary<string, long>(StringComparer.Ordinal) { [first] = 7, [second] = 7 };
            protocol.VerifyWith = id => versions.TryGetValue(id, out var v)
                ? new VerifyResult(VerifyOutcome.Match, v, null)
                : id == unsure ? new VerifyResult(VerifyOutcome.Error, null, "HTTP 503 Service Unavailable") : new VerifyResult(VerifyOutcome.Missing, null, "record not found");
            Assert.Equal(LogCount - 3, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);
            var held = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal((RecordStatus.Held, (string?)null, first, (long?)7), (held!.Status, held.ClaimedTargetId, held.OccupiedTargetId, held.OccupiedTargetVersion));
            Assert.StartsWith(OccupiedHolds.Prefix + first, held.LastError, StringComparison.Ordinal);
            Assert.True(OccupiedHolds.Adoptable(held));
            Assert.False(OccupiedHolds.Adoptable((await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2)))!));
            Assert.Equal(2, (await ledger.StatsAsync(runtime.Flow.Id, Now)).Adoptable);

            // One batch asks every record held so, and nothing else; nothing is claimed or sent until the run.
            runtime.Actor = "gui:tahir";
            var asked = await runtime.AdoptEveryAsync();
            Assert.Equal(new[] { SampleEstate.Key(0), SampleEstate.Key(1) }.OrderBy(k => k.Value), asked.OrderBy(k => k.Value));
            var waiting = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal((RecordStatus.Held, false, (string?)null), (waiting!.Status, waiting.Blocked, waiting.ClaimedTargetId));
            Assert.NotNull(waiting.AdoptRequestedUtc);
            Assert.NotNull(waiting.PlanRequestedUtc);
            Assert.StartsWith("adoption asked: the next run takes over the record OSDU holds", waiting.LastError, StringComparison.Ordinal);
            Assert.False(OccupiedHolds.Adoptable(waiting));
            Assert.Equal(0, (await ledger.StatsAsync(runtime.Flow.Id, Now)).Adoptable);
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "adopt" }));
            Assert.StartsWith("asked 2 record(s) held for an id OSDU holds", activity.Summary, StringComparison.Ordinal);
            Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, DeliveryKey = SampleEstate.Key(1).Value, Kind = "adopt" }));
            Assert.Null((await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2)))!.AdoptRequestedUtc);

            // The other instance wrote the first record again since: the run takes each over at the version OSDU holds now, and
            // writes it there as an update of that version.
            versions[first] = 9;
            protocol.Deliveries.Clear();
            _clock.Advance(TimeSpan.FromMinutes(1));
            runtime.Selection = SourceSelection.Full();
            Assert.Equal(2, (await RunAsync(runtime, protocol, ledger, force: true)).Work.Delivered);
            Assert.Equal(
                new[] { (first, (long?)9), (second, (long?)7) }.Order(),
                protocol.Deliveries.Select(d => (d.TargetId, d.ExistingVersion)).Order());
            foreach (var (key, id, version) in new[] { (SampleEstate.Key(0), first, 9L), (SampleEstate.Key(1), second, 7L) })
            {
                var record = await ledger.GetRecordAsync(runtime.Flow.Id, key);
                Assert.Equal((RecordStatus.Delivered, id, (string?)null, (DateTime?)null), (record!.Status, record.ClaimedTargetId, record.OccupiedTargetId, record.AdoptRequestedUtc));
                var attempts = await ledger.ListAttemptsAsync(runtime.Flow.Id, key, 10);
                var adoption = Assert.Single(attempts, a => a.Phase == AttemptPhases.Adopt);
                Assert.Equal((AttemptOutcome.Skipped, (long?)version, "intake"), (adoption.Outcome, adoption.TargetVersion, adoption.Worker));
                Assert.Contains($"\"targetId\":\"{id}\"", adoption.ResultJson, StringComparison.Ordinal);
                Assert.True(attempts.Single(a => a.Outcome == AttemptOutcome.Delivered).StartedUtc >= adoption.StartedUtc);
            }

            // In step now: nothing is left to adopt, and the record held because OSDU could not say is still held for it.
            Assert.Empty(await runtime.AdoptEveryAsync());
            var still = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2));
            Assert.Equal((RecordStatus.Held, true), (still!.Status, still.Blocked));
        }
    }

    [Fact]
    public async Task An_issue_s_records_are_asked_together_and_the_run_settles_an_id_freed_or_claimed_since_as_it_stands()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var first = CodeId(SampleEstate.Logs()[0]);
        var second = CodeId(SampleEstate.Logs()[1]);
        var third = CodeId(SampleEstate.Logs()[2]);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, Rendering(Mappings("key", FromKey)));
        using var owner = runtime;
        var occupied = new HashSet<string>(StringComparer.Ordinal) { first, second, third };
        protocol.VerifyWith = id => occupied.Contains(id) ? new VerifyResult(VerifyOutcome.Match, 3, null) : new VerifyResult(VerifyOutcome.Missing, null, "record not found");
        Assert.Equal(LogCount - 3, (await RunAsync(runtime, protocol, ledger)).Work.Delivered);

        // The three share one issue; adopting it asks all three. Naming a record delivered here asks nothing of it.
        var issue = (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.ProblemHash!.Value;
        Assert.Equal(issue, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(2)))!.ProblemHash);
        runtime.Actor = "cli:tahir@host";
        Assert.Empty(await runtime.AdoptAsync([SampleEstate.Key(3)]));
        Assert.Equal(3, (await runtime.AdoptProblemAsync(issue, OccupiedHolds.Prefix + "<osdu id>")).Count);
        var byIssue = (await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "adopt" }))
            .Single(a => a.Summary!.Contains(" issue ", StringComparison.Ordinal));
        Assert.StartsWith("asked 3 record(s) issue ", byIssue.Summary, StringComparison.Ordinal);

        // Before the run, OSDU stops holding the first record, and another flow of the partition claims the second id.
        occupied.Remove(first);
        var (codes, codesProtocol, _) = await RuntimeAsync(tables, Rendering(Mappings("fresh", FromKey), "wells-welllog-codes"));
        using var other = codes;
        codesProtocol.VerifyWith = id => id == second ? new VerifyResult(VerifyOutcome.Missing, null, "record not found") : new VerifyResult(VerifyOutcome.Match, 3, null);
        await RunAsync(codes, codesProtocol, ledger);
        Assert.Equal(second, (await ledger.GetRecordAsync(codes.Flow.Id, SampleEstate.Key(1)))!.ClaimedTargetId);

        // The run adopts the third, delivers the first as a new record (nothing at its id to take over), and holds the second,
        // naming the record that claims its id; only the third keeps an adopt attempt.
        protocol.Deliveries.Clear();
        _clock.Advance(TimeSpan.FromMinutes(1));
        runtime.Selection = SourceSelection.Full();
        Assert.Equal(2, (await RunAsync(runtime, protocol, ledger, force: true)).Work.Delivered);
        Assert.Equal(
            new[] { (first, (long?)null), (third, (long?)3) }.Order(),
            protocol.Deliveries.Select(d => (d.TargetId, d.ExistingVersion)).Order());
        Assert.DoesNotContain(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10), a => a.Phase == AttemptPhases.Adopt);
        Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(2), 10), a => a.Phase == AttemptPhases.Adopt);
        var taken = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1));
        Assert.Equal((RecordStatus.Held, (string?)null), (taken!.Status, taken.ClaimedTargetId));
        Assert.Contains($"OSDU id {second} is already claimed by flow", taken.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(1), 10), a => a.Phase == AttemptPhases.Adopt);
        Assert.False(OccupiedHolds.Adoptable(taken));
        Assert.Empty(await runtime.AdoptAsync([SampleEstate.Key(1)]));
    }

    [Fact]
    public async Task An_adoption_asked_outlasts_a_run_osdu_did_not_answer_and_the_run_after_a_release_takes_the_record_over()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, Rendering(Mappings("key", FromKey)));
        using (runtime)
        {
            var id = CodeId(SampleEstate.Logs()[0]);
            var answer = new VerifyResult(VerifyOutcome.Match, 7, null);
            protocol.VerifyWith = asked => asked == id ? answer : new VerifyResult(VerifyOutcome.Missing, null, "record not found");
            await RunAsync(runtime, protocol, ledger);
            Assert.Equal([SampleEstate.Key(0)], await runtime.AdoptEveryAsync());

            // OSDU does not answer for the id at the run: the record is held for that, claiming nothing, and the request stands.
            answer = new VerifyResult(VerifyOutcome.Error, null, "HTTP 503 Service Unavailable");
            protocol.Deliveries.Clear();
            _clock.Advance(TimeSpan.FromMinutes(1));
            runtime.Selection = SourceSelection.Full();
            Assert.Equal(0, (await RunAsync(runtime, protocol, ledger, force: true)).Work.Delivered);
            Assert.Empty(protocol.Deliveries);
            var held = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal((RecordStatus.Held, true, (string?)null), (held!.Status, held.Blocked, held.ClaimedTargetId));
            Assert.Contains($"OSDU could not say whether it holds a record at {id}", held.LastError, StringComparison.Ordinal);
            Assert.NotNull(held.AdoptRequestedUtc);
            Assert.False(OccupiedHolds.Adoptable(held));
            Assert.Empty(await runtime.AdoptEveryAsync());
            Assert.Equal(0, (await ledger.StatsAsync(runtime.Flow.Id, Now)).Adoptable);
            Assert.DoesNotContain(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10), a => a.Phase == AttemptPhases.Adopt);

            // Released once OSDU answers again, the record is taken over at the version OSDU holds then.
            answer = new VerifyResult(VerifyOutcome.Match, 8, null);
            await runtime.ReleaseAsync([SampleEstate.Key(0)]);
            _clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(1, (await RunAsync(runtime, protocol, ledger, force: true)).Work.Delivered);
            var write = Assert.Single(protocol.Deliveries);
            Assert.Equal((id, (long?)8), (write.TargetId, write.ExistingVersion));
            var record = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal((RecordStatus.Delivered, id, (DateTime?)null), (record!.Status, record.ClaimedTargetId, record.AdoptRequestedUtc));
            Assert.Single(await ledger.ListAttemptsAsync(runtime.Flow.Id, SampleEstate.Key(0), 10), a => a.Phase == AttemptPhases.Adopt);
        }
    }

    [Fact]
    public async Task Records_held_before_the_hold_kept_the_id_are_adopted_by_what_they_were_held_for()
    {
        // Records held by an earlier module version, whose hold kept the id in its message alone: nothing is rewritten, the
        // hold's reason makes them adoptable, and the run takes them over by the id their keys make now.
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, Rendering(Mappings("key", FromKey)));
        using (runtime)
        {
            var earlier = CodeId(SampleEstate.Logs()[0]);
            var unsure = CodeId(SampleEstate.Logs()[1]);
            protocol.VerifyWith = id => id == earlier
                ? new VerifyResult(VerifyOutcome.Match, 7, null)
                : id == unsure ? new VerifyResult(VerifyOutcome.Error, null, "HTTP 503 Service Unavailable") : new VerifyResult(VerifyOutcome.Missing, null, "record not found");
            await RunAsync(runtime, protocol, ledger);
            var earlierHold = $"OSDU already holds a record at {earlier} at version 7, the OSDU id made from the key, and no record of the ledger claimed it: "
                + "it is another system's, so nothing is sent rather than writing a new version of it. Remove or rename that record, or give the mapping a key or dataset.idFrom that yields another id";
            await using (var db = _db.CreateDbContext())
            {
                await db.DeliveryRecords.Where(r => r.FlowId == runtime.Flow.Id && r.DeliveryKey == SampleEstate.Key(0).Value).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.OccupiedTargetId, (string?)null)
                    .SetProperty(r => r.OccupiedTargetVersion, (long?)null)
                    .SetProperty(r => r.LastError, earlierHold));
            }

            var old = await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0));
            Assert.Equal(((string?)null, true), (old!.OccupiedTargetId, OccupiedHolds.Adoptable(old)));
            Assert.False(OccupiedHolds.Adoptable((await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(1)))!));
            Assert.Equal(1, (await ledger.StatsAsync(runtime.Flow.Id, Now)).Adoptable);

            Assert.Equal([SampleEstate.Key(0)], await runtime.AdoptEveryAsync());
            protocol.Deliveries.Clear();
            _clock.Advance(TimeSpan.FromMinutes(1));
            runtime.Selection = SourceSelection.Full();
            Assert.Equal(1, (await RunAsync(runtime, protocol, ledger, force: true)).Work.Delivered);
            var write = Assert.Single(protocol.Deliveries);
            Assert.Equal((SampleEstate.Key(0), earlier, (long?)7), (write.Key, write.TargetId, write.ExistingVersion));
            Assert.Equal(earlier, (await ledger.GetRecordAsync(runtime.Flow.Id, SampleEstate.Key(0)))!.ClaimedTargetId);
        }
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
