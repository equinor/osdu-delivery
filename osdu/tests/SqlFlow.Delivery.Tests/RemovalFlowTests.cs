using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Removals;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using SqlFlow.Execution;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Removal flows end to end (osdu/docs/reference/flow/removal.md): the sample well logs delivered by the sample delivery flow
/// to a fake protocol, with the ledger in the module's database on SQL Server; then a removal flow reads the source's keys
/// through SQLFlow's own readers (CSV files, a source table), compares them with that ledger, writes what it finds as the
/// ledger's deletions and takes them out through the delivery flow's removal path. Each scenario covers one rule: what counts
/// as removed and as of when, a row that came back, the guard and its approval, the take-out's outcomes and its incremental
/// work, the plan, the delivery flow's plan keeping a removed key out, lifting a deletion, and the refusals that stop a
/// removal before it compares anything.
/// </summary>
/// <remarks>
/// The removal finds its delivery flow beside it in a copy of the estate the test owns, as a checkout the catalog has not
/// synced: the sample flow's document with its target pointed at a placeholder, which nothing but the fake protocol reaches.
/// The clock is the test's, so every moment a comparison weighs is one the test set.
/// </remarks>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
[Trait("Suite", "Removal")]
public sealed class RemovalFlowTests : IDisposable
{
    private const string DeliveryFlowName = "welldb-welllog-03-header-delivery";

    /// <summary>The environment variable a table source's connection names in these tests, set to the test database for one test.</summary>
    private const string ConnectionVariable = "SQLFLOW_REMOVAL_TEST_DB";

    /// <summary>The source table a table source reads, created and dropped by the test that reads it.</summary>
    private const string KeyTable = "RemovalFlowTestKeys";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly FakeProtocol _protocol = new();
    private readonly OsduLedger _ledger;
    private readonly ServiceProvider _sqlflow;
    private readonly List<FlowRuntime> _runtimes = [];
    private MemoryIngestionTables _tables = null!;
    private FlowRuntime _delivery = null!;
    private DateTime? _readThrough;
    private bool _tableCreated;

    public RemovalFlowTests()
    {
        _ledger = _db.Ledger(_clock);
        _sqlflow = new ServiceCollection().AddLogging().AddSqlFlowEngine().BuildServiceProvider();
        WriteEstate();
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static IReadOnlyList<SampleLog> Logs => SampleEstate.Logs();

    private static DeliveryKey Key(int index) => SampleEstate.Key(index);

    private string Estate => Path.Combine(_root, "estate");

    private string RemovalFile => Path.Combine(Estate, "flows", "removal.yaml");

    /// <summary>
    /// The estate's copy the removal reads: the sample delivery flow's document beside it, its target a placeholder only the
    /// fake protocol answers for, and the sample mappings it renders with.
    /// </summary>
    private void WriteEstate()
    {
        Directory.CreateDirectory(Path.Combine(Estate, "flows"));
        var yaml = File.ReadAllText(Samples.Flow).ReplaceLineEndings("\n");
        var target = yaml.IndexOf("target:\n", StringComparison.Ordinal);
        var protocol = yaml.IndexOf("  protocol: ddms\n", target, StringComparison.Ordinal);
        Assert.True(target > 0 && protocol > target, "the sample flow's target block moved; the test's copy of it has to follow");
        yaml = yaml[..target] + "target:\n  endpoint: http://localhost:9/facade-api\n  auth:\n    type: none\n" + yaml[protocol..];
        File.WriteAllText(Path.Combine(Estate, "flows", DeliveryFlowName + ".yaml"), yaml);
        CopyDirectory(Samples.Mappings, Path.Combine(Estate, "mappings"));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var folder in Directory.GetDirectories(from))
        {
            CopyDirectory(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
    }

    /// <summary>The engine every runtime of a test shares: the ledger, the in-memory ingestion tables and the fake protocol.</summary>
    private EngineContext Engine() => Samples.Engine(_ledger, _clock, sources: _tables) with { Protocols = new FakeProtocolFactory(_protocol) };

    /// <summary>Every sample log delivered once, through the delivery flow a removal names, and the clock moved an hour on.</summary>
    private async Task DeliverEverythingAsync()
    {
        _tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        _delivery = await FlowRuntime.CreateAsync(Engine(), Samples.LocalFlow(_root), SampleEstate.Values);
        _runtimes.Add(_delivery);
        await _ledger.RegisterAsync(_delivery.Flow);
        var run = await DeliverAsync();
        Assert.Equal(Logs.Count, run.Work.Delivered);
        _protocol.Deliveries.Clear();
        _clock.Advance(TimeSpan.FromHours(1));
    }

    /// <summary>One deliver run as the executor makes it: the records asked to be planned again, then the rows stamped since the last run.</summary>
    private async Task<RunResult> DeliverAsync()
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var selection = SourceSelection.Incremental(_readThrough);
        _delivery.Selection = selection;
        _delivery.RunId = Guid.CreateVersion7();
        await DeliveryExecutor.DeliverRequestedAsync(_delivery, selection, DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
        _delivery.Selection = selection;
        var run = await _delivery.RunAsync(force: false);
        _readThrough = Now;
        return run;
    }

    /// <summary>A deliver run that reads every row of the scope, in a submission of its own.</summary>
    private async Task FullReadAsync()
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        _delivery.Selection = SourceSelection.Full();
        _delivery.SubmissionId = null;
        _delivery.RunId = Guid.CreateVersion7();
        await _delivery.RunAsync(force: true);
    }

    private Guid LedgerId => _delivery.Flow.Id;

    /// <summary>A CSV file of keys, the source's columns named as the delivery flow's record key, one row per log given.</summary>
    private string KeysFile(string folder, IEnumerable<int> logs, Func<int, string>? removedAt = null)
    {
        var dir = Path.Combine(_root, "keys", folder);
        Directory.CreateDirectory(dir);
        var csv = new StringBuilder(removedAt is null ? "source_project,log_id\n" : "source_project,log_id,removed_at\n");
        foreach (var index in logs)
        {
            csv.Append(Logs[index].SourceProject).Append(',').Append(Logs[index].LogId);
            if (removedAt is not null)
            {
                csv.Append(',').Append(removedAt(index));
            }

            csv.Append('\n');
        }

        File.WriteAllText(Path.Combine(dir, "keys.csv"), csv.ToString());
        return dir;
    }

    /// <summary>The removal flow document of one removal of the delivery flow, compared under the sample's log source.</summary>
    private static string Removal(string sources, string? guard = null, bool keepReferenced = false, string logSource = SampleWellLogs.LogSource) => $"""
        flowType: removal
        name: welldb-welllog-04-removal
        partitions: [dev]
        {guard ?? string.Empty}
        removals:
          - name: logs
            flow: {DeliveryFlowName}
            values: {"{"} logSource: {logSource} {"}"}
            keepReferenced: {(keepReferenced ? "true" : "false")}
        {sources}
        """;

    /// <summary>A reader source of CSV files under the test's key folder.</summary>
    private static string Csv(string side, string folder, string? removedAt = null) => $"""
            {side}:
              type: csv
              location: ../../keys/{folder}
              options: {"{"} delimiter: ",", header: true, srcFile: "*.csv" {"}"}
        {(removedAt is null ? string.Empty : $"      removedAt: {{ name: {removedAt} }}")}
        """;

    private RemovalKeySources Sources() => new(
        _sqlflow.GetServices<ISourceReader>(),
        _sqlflow.GetRequiredService<IConnectionResolver>(),
        _sqlflow.GetRequiredService<IConnectionFactory>(),
        _sqlflow.GetRequiredService<SourceProviderRegistry>(),
        _sqlflow.GetRequiredService<ISecretResolver>());

    /// <summary>One run of the removal flow <paramref name="yaml"/> declares, as the executor runs it, in the sample's partition.</summary>
    private async Task<RemovalRunOutcome> RemoveAsync(
        string yaml, string operation = DeliveryOperations.Remove, RemovalApproval? approval = null, string? confirm = null, string actor = "schedule:welldb-removal")
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        File.WriteAllText(RemovalFile, yaml);
        var flow = new DeliveryDocumentLoader().LoadRemoval(RemovalFile) with { SourcePath = RemovalFile };
        var bound = flow.ForRun(Samples.SamplePartition, RegisteredPartitions.None);
        var runner = new RemovalRunner(Engine(), Sources(), bound, new Dictionary<string, string>(StringComparer.Ordinal), Guid.CreateVersion7(), actor, NullLogger.Instance);
        return await runner.RunAsync(operation, approval is null ? [] : ["logs"], approval, confirm, CancellationToken.None);
    }

    /// <summary>A run expected to end failed: a removal failed, or its guard held what it found; the outcome still comes back.</summary>
    private async Task<RemovalRunOutcome> RemoveFailedAsync(string yaml, string operation = DeliveryOperations.Remove)
        => (await Assert.ThrowsAsync<RemovalRunFailedException>(() => RemoveAsync(yaml, operation))).Outcome;

    private Task<IReadOnlyList<RemovedKeyState>> DeletionsAsync(string? state = null) => _ledger.ListRemovedKeysAsync(LedgerId, state, null, null, 1000);

    private async Task<RecordState> RecordAsync(int index) => (await _ledger.GetRecordAsync(LedgerId, Key(index)))!;

    private static string TargetOf(RecordState record) => record.TargetId!;

    [Fact]
    public async Task A_full_key_read_finds_the_keys_the_source_dropped_and_takes_each_record_out_once()
    {
        await DeliverEverythingAsync();
        var gone = await RecordAsync(0);
        KeysFile("current", Enumerable.Range(1, Logs.Count - 1));

        var outcome = await RemoveAsync(Removal(Csv("keys", "current")));

        var removal = Assert.Single(outcome.Removals);
        Assert.Equal((SourceRemovals.Completed, DeliveryFlowName), (removal.Status, removal.DeliveryFlow));
        Assert.Equal((Logs.Count - 1L, (long)Logs.Count, 1L, 1L, 0L), (removal.Tally.SourceKeys, removal.Tally.Compared, removal.Tally.Detected, removal.Tally.Removed, removal.Pending));
        Assert.Equal([(TargetOf(gone), RemovalScope.Record)], _protocol.Deletes);

        // The ledger's deletion says why and as of when, and the record is removed with an attempt naming the removal.
        var deletion = Assert.Single(await DeletionsAsync());
        Assert.Equal((Key(0), SourceRemovals.Done, SourceRemovals.Removed, SourceRemovals.Absent, 1), (deletion.Key, deletion.State, deletion.Outcome, deletion.Reason, deletion.Tries));
        Assert.Equal(("welldb-welllog-04-removal", "logs", removal.RemovalRunId!.Value, removal.RemovalRunId), (deletion.RemovalFlow, deletion.Entry, deletion.DetectedRunId, deletion.DoneRunId));
        Assert.Equal($"[\"{Logs[0].SourceProject}\",\"{Logs[0].LogId}\"]", deletion.SourceKeyJson);
        Assert.Equal(RecordStatus.Deleted, (await RecordAsync(0)).Status);
        Assert.Null((await RecordAsync(0)).PlanRequestedUtc);
        var attempt = (await _ledger.ListAttemptsAsync(LedgerId, Key(0), 10))[0];
        Assert.Equal((AttemptOutcome.Deleted, AttemptPhases.SourceRemoved), (attempt.Outcome, attempt.Phase));
        Assert.All(Enumerable.Range(1, Logs.Count - 1), i => Assert.Equal(RecordStatus.Delivered, RecordAsync(i).GetAwaiter().GetResult().Status));

        // The run is kept under the delivery ledger with what it read and did.
        var run = (await _ledger.GetRemovalRunAsync(Samples.SamplePartition, removal.RemovalRunId.Value))!;
        Assert.Equal((SourceRemovals.Completed, LedgerId, 1L, 1L), (run.Status, run.LedgerFlowId, run.Tally.Detected, run.Tally.Removed));
        Assert.NotNull(run.SourceReadUtc);

        // The next run finds nothing new, and asks OSDU nothing: a key taken out is never asked again.
        _protocol.Deletes.Clear();
        var again = Assert.Single((await RemoveAsync(Removal(Csv("keys", "current")))).Removals);
        Assert.Equal((0L, 0L, 0L), (again.Tally.Detected, again.Tally.Removed, again.Pending));
        Assert.Empty(_protocol.Deletes);
    }

    [Fact]
    public async Task A_plan_finds_what_a_remove_run_would_and_writes_nothing()
    {
        await DeliverEverythingAsync();
        KeysFile("current", Enumerable.Range(1, Logs.Count - 1));

        var plan = Assert.Single((await RemoveAsync(Removal(Csv("keys", "current")), DeliveryOperations.Plan)).Removals);

        Assert.Equal((SourceRemovals.Completed, 1L, 0L, false), (plan.Status, plan.Tally.Detected, plan.Tally.Removed, plan.WouldHold));
        Assert.Equal(1L, plan.Pending);
        Assert.Empty(await DeletionsAsync());
        Assert.Empty(_protocol.Deletes);
        Assert.Equal(RecordStatus.Delivered, (await RecordAsync(0)).Status);
        Assert.Equal(DeliveryOperations.Plan, (await _ledger.GetRemovalRunAsync(Samples.SamplePartition, plan.RemovalRunId!.Value))!.Operation);
    }

    [Fact]
    public async Task A_listed_key_is_removed_as_of_its_moment_and_one_whose_row_arrived_after_that_moment_came_back()
    {
        await DeliverEverythingAsync();
        var loaded = Now.AddHours(-1).AddMinutes(-5);

        // Log 0 was listed removed after its row was loaded; log 1 before, so its row came back after the removal.
        KeysFile("removed", [0, 1], i => (i == 0 ? loaded.AddMinutes(30) : loaded.AddMinutes(-30)).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));

        var removal = Assert.Single((await RemoveAsync(Removal(Csv("removed", "removed", "removed_at")))).Removals);

        Assert.Equal((2L, 1L, 1L, 1L), (removal.Tally.ListedKeys, removal.Tally.Detected, removal.Tally.CameBack, removal.Tally.Removed));
        var deletion = Assert.Single(await DeletionsAsync());
        Assert.Equal((Key(0), SourceRemovals.Listed, "keys.csv", 1L), (deletion.Key, deletion.Reason, Path.GetFileName(deletion.FileName)!, deletion.RowNumber!.Value));
        Assert.Equal(loaded.AddMinutes(30), deletion.RemovedUtc, TimeSpan.FromSeconds(1));
        Assert.Equal(RecordStatus.Delivered, (await RecordAsync(1)).Status);
    }

    [Fact]
    public async Task The_guard_holds_a_run_that_finds_too_many_until_an_operator_approves_the_count_they_were_shown()
    {
        await DeliverEverythingAsync();
        KeysFile("current", [0]);
        var removal = Removal(Csv("keys", "current"));

        var held = Assert.Single((await RemoveFailedAsync(removal)).Removals);
        Assert.Equal((SourceRemovals.Guarded, Logs.Count - 1L, Logs.Count - 1L, 0L), (held.Status, held.Tally.Detected, held.Tally.AwaitingApproval, held.Tally.Removed));
        Assert.Contains("wait for an operator's approval", held.Error, StringComparison.Ordinal);
        Assert.Empty(_protocol.Deletes);
        Assert.Equal(Logs.Count - 1, (await DeletionsAsync(SourceRemovals.AwaitingApproval)).Count);

        // Nothing waiting for approval is kept out: a full read plans and delivers those rows as before.
        Assert.Empty(await _ledger.RemovedKeysAsync(LedgerId, [Key(1), Key(2)]));

        // The next run finds nothing new, so it holds nothing more and leaves what waits as it is.
        var waiting = Assert.Single((await RemoveAsync(removal)).Removals);
        Assert.Equal((SourceRemovals.Completed, 0L, Logs.Count - 1L), (waiting.Status, waiting.Tally.Detected, waiting.AwaitingApproval));

        // An approval for another count approves nothing; one for the count shown makes them pending and takes them out.
        var stale = Assert.Single((await Assert.ThrowsAsync<RemovalRunFailedException>(() => RemoveAsync(removal, approval: new RemovalApproval { Expected = 2 }, confirm: "dev"))).Outcome.Removals);
        Assert.Contains("what waits changed since it was shown", stale.Error, StringComparison.Ordinal);
        Assert.Empty(_protocol.Deletes);

        Assert.Contains("confirms partition 'test'", (await Assert.ThrowsAsync<DeliveryException>(
            () => RemoveAsync(removal, approval: new RemovalApproval { Expected = Logs.Count - 1 }, confirm: "test"))).Message, StringComparison.Ordinal);

        var approved = Assert.Single((await RemoveAsync(removal, approval: new RemovalApproval { Expected = Logs.Count - 1 }, confirm: "dev", actor: "user:tahir")).Removals);
        Assert.Equal((SourceRemovals.Completed, Logs.Count - 1L, Logs.Count - 1L), (approved.Status, approved.Tally.Approved, approved.Tally.Removed));
        Assert.Equal(Logs.Count - 1, _protocol.Deletes.Count);
        Assert.All(await DeletionsAsync(), d => Assert.Equal((SourceRemovals.Done, "user:tahir"), (d.State, d.ApprovedBy)));
        Assert.Equal(RecordStatus.Delivered, (await RecordAsync(0)).Status);
    }

    [Fact]
    public async Task A_read_of_the_sources_keys_that_holds_none_is_held_whatever_the_guard_allows()
    {
        await DeliverEverythingAsync();
        KeysFile("current", []);

        var held = Assert.Single((await RemoveFailedAsync(Removal(Csv("keys", "current"), guard: "guard: { maxPercent: 100 }"))).Removals);

        Assert.Equal((SourceRemovals.Guarded, 0L, (long)Logs.Count), (held.Status, held.Tally.SourceKeys, held.Tally.AwaitingApproval));
        Assert.Contains("the source's keys read none", held.Error, StringComparison.Ordinal);
        Assert.Empty(_protocol.Deletes);
    }

    [Fact]
    public async Task The_delivery_flows_plan_keeps_a_removed_key_out_until_a_newer_row_arrives_and_then_the_deletion_is_lifted()
    {
        await DeliverEverythingAsync();
        KeysFile("current", Enumerable.Range(1, Logs.Count - 1));
        await RemoveAsync(Removal(Csv("keys", "current")));
        Assert.Equal(RecordStatus.Deleted, (await RecordAsync(0)).Status);

        // A read of every row plans the removed key's row as removed by its source: it stays out, nothing is sent.
        await FullReadAsync();
        Assert.Equal(RecordStatus.Deleted, (await RecordAsync(0)).Status);
        Assert.DoesNotContain(_protocol.Deliveries, d => d.Key == Key(0));

        // A newer row is the key coming back: it is delivered, and the next removal run lifts the deletion it outlived.
        _clock.Advance(TimeSpan.FromMinutes(10));
        SampleEstate.Change(_tables.Records[0], "log_name", "renamed", Now);
        await DeliverAsync();
        Assert.Equal(RecordStatus.Delivered, (await RecordAsync(0)).Status);
        Assert.Contains(_protocol.Deliveries, d => d.Key == Key(0));

        _clock.Advance(TimeSpan.FromMinutes(1));
        KeysFile("current", Enumerable.Range(0, Logs.Count));
        var lifted = Assert.Single((await RemoveAsync(Removal(Csv("keys", "current")))).Removals);
        Assert.Equal((1L, 0L), (lifted.Tally.Lifted, lifted.Tally.Detected));
        Assert.Empty(await DeletionsAsync());
    }

    [Fact]
    public async Task A_key_back_in_the_source_lifts_its_deletion_and_its_record_is_delivered_again()
    {
        await DeliverEverythingAsync();
        KeysFile("current", Enumerable.Range(1, Logs.Count - 1));
        await RemoveAsync(Removal(Csv("keys", "current")));

        _clock.Advance(TimeSpan.FromHours(1));
        KeysFile("current", Enumerable.Range(0, Logs.Count));
        var lifted = Assert.Single((await RemoveAsync(Removal(Csv("keys", "current")))).Removals);

        Assert.Equal((1L, 0L), (lifted.Tally.Lifted, lifted.Tally.Detected));
        Assert.Empty(await DeletionsAsync());
        Assert.NotNull((await RecordAsync(0)).PlanRequestedUtc);
        await DeliverAsync();
        Assert.Equal(RecordStatus.Delivered, (await RecordAsync(0)).Status);
        Assert.Contains(_protocol.Deliveries, d => d.Key == Key(0));
    }

    [Fact]
    public async Task An_operator_lifts_a_deletion_in_one_activity_and_the_record_is_delivered_again()
    {
        await DeliverEverythingAsync();
        KeysFile("current", Enumerable.Range(2, Logs.Count - 2));
        await RemoveAsync(Removal(Csv("keys", "current"), guard: "guard: { maxPercent: 50 }"));
        Assert.Equal(2, (await DeletionsAsync(SourceRemovals.Done)).Count);

        _delivery.Actor = "user:tahir";
        var lifted = await _delivery.LiftRemovalsAsync([Key(0), Key(1), Key(2)]);

        Assert.Equal(new HashSet<DeliveryKey> { Key(0), Key(1) }, lifted);
        Assert.Empty(await DeletionsAsync());
        var activity = Assert.Single(await _ledger.ListActivitiesAsync(new ActivityQuery { FlowId = LedgerId, DeliveryKey = Key(0).Value, Kind = SourceRemovals.LiftActivityKind }));
        Assert.Equal("user:tahir", activity.Actor);
        await DeliverAsync();
        Assert.Equal((RecordStatus.Delivered, RecordStatus.Delivered), ((await RecordAsync(0)).Status, (await RecordAsync(1)).Status));
        Assert.Empty(await _delivery.LiftRemovalsAsync([Key(0)]));
        Assert.Throws<DeliveryException>(() => _delivery.LiftRemovalsAsync([]).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Each_outcome_of_a_take_out_is_kept_and_only_what_did_not_finish_is_tried_again()
    {
        await DeliverEverythingAsync();

        // Log 0: OSDU refuses its removal. Log 1: OSDU no longer holds it. Log 2: removed from OSDU by an operator before.
        _protocol.Refuse.Add(TargetOf(await RecordAsync(0)));
        _protocol.Gone.Add(TargetOf(await RecordAsync(1)));
        await _ledger.MarkRemovedAsync(LedgerId, [Key(2)], RemovalScope.Record, "user:tahir", Now);

        // A key only the record table holds, which no run delivered.
        var unseen = Logs[3] with { LogId = Logs[3].LogId + "-NEW" };
        _tables.Add(SampleEstate.Record(unseen, Now.AddHours(-2), 99));

        KeysFile("current", Enumerable.Range(3, Logs.Count - 3));
        var first = Assert.Single((await RemoveAsync(Removal(Csv("keys", "current"), guard: "guard: { maxPercent: 100 }"))).Removals);

        Assert.Equal((1L, 1L, 1L, 1L, 0L), (first.Tally.Failed, first.Tally.AlreadyGone, first.Tally.Settled, first.Tally.KeptOut, first.Tally.Removed));
        var deletions = (await DeletionsAsync()).ToDictionary(d => d.Key);
        Assert.Equal((SourceRemovals.Pending, SourceRemovals.Failed, 1), (deletions[Key(0)].State, deletions[Key(0)].Outcome, deletions[Key(0)].Tries));
        Assert.Contains("403", deletions[Key(0)].Detail, StringComparison.Ordinal);
        Assert.Equal((SourceRemovals.Done, SourceRemovals.AlreadyGone), (deletions[Key(1)].State, deletions[Key(1)].Outcome));
        Assert.Equal((SourceRemovals.Done, SourceRemovals.Settled), (deletions[Key(2)].State, deletions[Key(2)].Outcome));
        Assert.Equal((SourceRemovals.Done, SourceRemovals.KeptOut, (string?)null), (deletions[unseen.Key].State, deletions[unseen.Key].Outcome, deletions[unseen.Key].RecordStatus));
        Assert.Equal(RecordStatus.Delivered, (await RecordAsync(0)).Status);

        // The next run asks OSDU about the one that did not finish alone.
        _protocol.Deletes.Clear();
        _protocol.Refuse.Clear();
        var second = Assert.Single((await RemoveAsync(Removal(Csv("keys", "current"), guard: "guard: { maxPercent: 100 }"))).Removals);
        Assert.Equal((1L, 0L), (second.Tally.Removed, second.Tally.Failed));
        Assert.Equal([TargetOf(await RecordAsync(0))], _protocol.Deletes.Select(d => d.TargetId));
        var retried = Assert.Single(await DeletionsAsync(), d => d.Key == Key(0));
        Assert.Equal((SourceRemovals.Done, SourceRemovals.Removed, 2), (retried.State, retried.Outcome, retried.Tries));

        // The key only the record table held stays out of every run of the delivery flow.
        await FullReadAsync();
        Assert.Null(await _ledger.GetRecordAsync(LedgerId, unseen.Key));
    }

    [Fact]
    public async Task A_record_whose_references_cannot_be_checked_stays_in_osdu_for_the_next_run()
    {
        await DeliverEverythingAsync();
        KeysFile("current", Enumerable.Range(1, Logs.Count - 1));

        // The placeholder target answers no search, so whether other records name the record cannot be told.
        var removal = Assert.Single((await RemoveAsync(Removal(Csv("keys", "current"), keepReferenced: true))).Removals);

        Assert.Equal((1L, 0L), (removal.Tally.Failed, removal.Tally.Removed));
        Assert.Empty(_protocol.Deletes);
        var deletion = Assert.Single(await DeletionsAsync());
        Assert.Equal((SourceRemovals.Pending, SourceRemovals.Failed), (deletion.State, deletion.Outcome));
        Assert.Contains("OSDU's search could not say whether other records name it", deletion.Detail, StringComparison.Ordinal);
        Assert.Equal(RecordStatus.Delivered, (await RecordAsync(0)).Status);
    }

    [Fact]
    public async Task A_removal_compares_only_the_records_planned_under_its_scope_values()
    {
        await DeliverEverythingAsync();
        KeysFile("current", []);

        var elsewhere = Assert.Single((await RemoveAsync(Removal(Csv("keys", "current"), logSource: "CPI"))).Removals);

        Assert.Equal((SourceRemovals.Completed, 0L, 0L), (elsewhere.Status, elsewhere.Tally.Compared, elsewhere.Tally.Detected));
        Assert.Empty(await DeletionsAsync());
    }

    [Fact]
    public async Task A_key_a_source_holds_blank_stops_the_removal_before_it_compares_anything_naming_the_row()
    {
        await DeliverEverythingAsync();
        var dir = KeysFile("current", Enumerable.Range(0, Logs.Count));
        File.AppendAllText(Path.Combine(dir, "keys.csv"), Logs[0].SourceProject + ",\n");

        var failed = Assert.Single((await RemoveFailedAsync(Removal(Csv("keys", "current")))).Removals);

        Assert.Equal(SourceRemovals.Failure, failed.Status);
        Assert.Contains("keys.csv", failed.Error, StringComparison.Ordinal);
        Assert.Contains("row", failed.Error, StringComparison.Ordinal);
        Assert.Empty(await DeletionsAsync());
        Assert.Equal(SourceRemovals.Failure, (await _ledger.GetRemovalRunAsync(Samples.SamplePartition, failed.RemovalRunId!.Value))!.Status);
    }

    [Fact]
    public async Task A_run_its_process_left_running_is_closed_by_the_next_run_of_the_same_removal()
    {
        await DeliverEverythingAsync();
        var stopped = await _ledger.StartRemovalRunAsync(LedgerId, new RemovalRunStart
        {
            FlowName = "welldb-welllog-04-removal",
            Entry = "logs",
            DeliveryFlow = DeliveryFlowName,
            Operation = DeliveryOperations.Remove,
            Actor = "schedule:welldb-removal",
            StartedUtc = Now,
            GuardPercent = RemovalGuard.DefaultMaxPercent,
        });
        KeysFile("current", Enumerable.Range(0, Logs.Count));

        await RemoveAsync(Removal(Csv("keys", "current")));

        var closed = (await _ledger.GetRemovalRunAsync(Samples.SamplePartition, stopped))!;
        Assert.Equal(SourceRemovals.Failure, closed.Status);
        Assert.Contains("its process stopped before it did", closed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keys_read_from_a_source_table_through_an_expression_land_on_the_ledgers_delivery_keys()
    {
        await DeliverEverythingAsync();
        var database = new SqlConnectionStringBuilder(_db.ConnectionString).InitialCatalog;
        await using (var connection = new SqlConnection(_db.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = $"IF OBJECT_ID(N'dbo.{KeyTable}') IS NOT NULL DROP TABLE dbo.{KeyTable}; CREATE TABLE dbo.{KeyTable} (project nvarchar(100) NULL, log nvarchar(100) NULL, state nvarchar(16) NOT NULL);";
            await create.ExecuteNonQueryAsync();
            _tableCreated = true;
            for (var i = 0; i < Logs.Count; i++)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = $"INSERT INTO dbo.{KeyTable} (project, log, state) VALUES (@project, @log, @state);";
                insert.Parameters.AddWithValue("@project", Logs[i].SourceProject.ToLowerInvariant());
                insert.Parameters.AddWithValue("@log", "  " + Logs[i].LogId + "  ");
                insert.Parameters.AddWithValue("@state", i == 0 ? "retired" : "active");
                await insert.ExecuteNonQueryAsync();
            }
        }

        Environment.SetEnvironmentVariable(ConnectionVariable, _db.ConnectionString);
        var table = $"""
                keys:
                  connection: ${"{"}env:{ConnectionVariable}{"}"}
                  object: "[{database}].[dbo].[{KeyTable}]"
                  filter: AND state = 'active'
                  columns:
                    - {"{"} name: project, as: source_project, expr: "UPPER(@ColName)" {"}"}
                    - {"{"} name: log, as: log_id, expr: "LTRIM(RTRIM(@ColName))" {"}"}
            """;

        var removal = Assert.Single((await RemoveAsync(Removal(table))).Removals);

        Assert.Equal((Logs.Count - 1L, 1L, 1L), (removal.Tally.SourceKeys, removal.Tally.Detected, removal.Tally.Removed));
        Assert.Equal(Key(0), Assert.Single(await DeletionsAsync()).Key);
    }

    public void Dispose()
    {
        foreach (var runtime in _runtimes)
        {
            runtime.Dispose();
        }

        if (_tableCreated)
        {
            using var connection = new SqlConnection(_db.ConnectionString);
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = $"IF OBJECT_ID(N'dbo.{KeyTable}') IS NOT NULL DROP TABLE dbo.{KeyTable};";
            drop.ExecuteNonQuery();
        }

        Environment.SetEnvironmentVariable(ConnectionVariable, null);
        _sqlflow.Dispose();
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a reader still holds open on Windows; the temp folder is the test's own and the OS clears it.
        }
    }
}
