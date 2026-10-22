using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Every way a record is held or failed, and every way out of it (osdu/docs/reference/concepts/record-lifecycle.md): held at
/// render or as a deleted row, held or failed by a try with its document kept, then a corrected row, a row that did not
/// change, a late row, a release, a release of one issue, a redelivery, a sync and a removal. Each scenario runs deliver
/// runs as the executor does (the passes over the records asked to be planned again, then the run's own read of the rows
/// stamped since the run before) over the sample estate, the module's database on SQL Server and a fake protocol. The
/// sample flow orders its rows by a business date (<c>source.lastModified: update_date</c>), which a correction leaves as
/// it was unless a test moves it.
/// </summary>
/// <remarks>
/// Every scenario ends by reading the whole ledger for a record no run, release or schedule can move any more: held,
/// failed or reverted, and neither blocked, nor asked to be planned again, nor holding a document a worker sends.
/// </remarks>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class HoldAndReleaseTests : IDisposable
{
    /// <summary>A wellbore no record on the platform is named by: the sample mapping holds a log that names it at render.</summary>
    private const string Unresolvable = "Wellbore Z-1";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    /// <summary>Where the last deliver run's own read ended: the next run reads the rows the ingestion tables stamped after it.</summary>
    private DateTime? _readThrough;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static DeliveryKey Key(int index) => SampleEstate.Key(index);

    private async Task<MemoryIngestionTables> EstateAsync() => await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);

    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> RuntimeAsync(
        MemoryIngestionTables tables, Func<FlowDefinition, FlowDefinition>? adjust = null, IRecordSearchFactory? searches = null)
    {
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables, searches: searches) with { Protocols = new FakeProtocolFactory(protocol) };
        var flow = adjust is null ? Samples.LocalFlow(_root) : adjust(Samples.LocalFlow(_root));
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        return (runtime, protocol, ledger);
    }

    /// <summary>
    /// One deliver run as the executor makes it: the records the ledger was asked to plan again, read by key, then the rows
    /// the ingestion tables stamped since the last run, planned and sent.
    /// </summary>
    private async Task<RunResult> DeliverAsync(FlowRuntime runtime)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        var selection = SourceSelection.Incremental(_readThrough);
        runtime.Selection = selection;
        runtime.RunId = Guid.CreateVersion7();
        await DeliveryExecutor.DeliverRequestedAsync(runtime, selection, DeliveryRunPayload.None, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
        runtime.Selection = selection;
        var run = await runtime.RunAsync(force: false);
        _readThrough = Now;
        return run;
    }

    /// <summary>
    /// A run that reads the whole scope, in a submission of its own (a runtime otherwise keeps the submission it planned
    /// last, and runs it again over the window that submission recorded).
    /// </summary>
    private async Task FullReadAsync(FlowRuntime runtime)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        runtime.Selection = SourceSelection.Full();
        runtime.SubmissionId = null;
        runtime.RunId = Guid.CreateVersion7();
        await runtime.RunAsync(force: true);
    }

    /// <summary>Every record delivered once, and the protocol's record of it cleared.</summary>
    private async Task DeliverEverythingAsync(FlowRuntime runtime, FakeProtocol protocol)
    {
        var run = await DeliverAsync(runtime);
        Assert.Equal(SampleEstate.Logs().Count, run.Work.Delivered);
        protocol.Deliveries.Clear();
    }

    /// <summary>The row loaded again with <paramref name="column"/> set, stamped by the ingestion table; its business date moves only when given.</summary>
    private void Load(MemoryRecord row, string column, object? value, DateTime? businessDate = null)
    {
        _clock.Advance(TimeSpan.FromMinutes(1));
        row.Row[column] = value;
        if (businessDate is { } moved)
        {
            row.Row["update_date"] = moved;
        }

        row.UpdatedUtc = Now;
    }

    private static async Task<RecordState> StateAsync(OsduLedger ledger, FlowRuntime runtime, int index)
        => (await ledger.GetRecordAsync(runtime.Flow.Id, Key(index)))!;

    private static async Task<IReadOnlyList<AttemptRecord>> AttemptsAsync(OsduLedger ledger, FlowRuntime runtime, int index)
        => await ledger.ListAttemptsAsync(runtime.Flow.Id, Key(index), 100);

    private static void AssertDelivered(RecordState record)
    {
        Assert.Equal(RecordStatus.Delivered, record.Status);
        Assert.False(record.Blocked);
        Assert.Null(record.ProblemHash);
        Assert.Null(record.PendingDocumentRef);
        Assert.Null(record.PlanRequestedUtc);
        Assert.Equal(0, record.AttemptCount);
    }

    private static void AssertBlocked(RecordState record, RecordStatus status, string error)
    {
        Assert.Equal(status, record.Status);
        Assert.True(record.Blocked);
        Assert.NotNull(record.ProblemHash);
        Assert.Contains(error, record.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// What every scenario ends with: no record no run, release or schedule can move any more, and no record whose block,
    /// issue and custody state disagree.
    /// </summary>
    private async Task AssertNothingStrandedAsync(FlowRuntime runtime)
    {
        await using var db = _db.CreateDbContext();
        var records = await db.DeliveryRecords.AsNoTracking()
            .Where(r => r.FlowId == runtime.Flow.Id)
            .Select(r => new { r.SourceKey, r.Status, r.Blocked, r.ProblemHash, r.PlanRequestedUtc, r.PendingDocumentRef })
            .ToListAsync();
        string[] parked = ["held", "failed", "reverted"];
        string[] blockable = ["held", "failed", "deleted", "reverted"];
        string[] issued = ["held", "failed"];
        Assert.DoesNotContain(records, r => parked.Contains(r.Status) && !r.Blocked && r.PlanRequestedUtc is null && r.PendingDocumentRef is null);
        Assert.DoesNotContain(records, r => r.Blocked && !blockable.Contains(r.Status));
        Assert.DoesNotContain(records, r => r.ProblemHash is not null && !(r.Blocked && issued.Contains(r.Status)));
    }

    // ---- Held at render: no document is kept, the row is what moves the record ------------------------------------------

    [Fact]
    public async Task A_held_record_stays_blocked_while_its_row_does_not_move_and_records_nothing_more()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);
            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, Unresolvable);
            var attempts = (await AttemptsAsync(ledger, runtime, 2)).Count;

            // Run after run, and a read of the whole source: the same row is the same known problem.
            await DeliverAsync(runtime);
            await FullReadAsync(runtime);
            await DeliverAsync(runtime);

            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, Unresolvable);
            Assert.Equal(attempts, (await AttemptsAsync(ledger, runtime, 2)).Count);
            Assert.Empty(protocol.Deliveries);

            // The flow's statistics count it blocked, from the ledger.
            var stats = await ledger.StatsAsync(runtime.Flow.Id, Now);
            Assert.Equal((1L, 1L, 0L), (stats.Held, stats.Blocked, stats.Stranded));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_row_corrected_back_to_what_was_delivered_settles_the_held_record_as_delivered_with_nothing_sent()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var delivered = await StateAsync(ledger, runtime, 2);
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            // Corrected in its file and loaded again, at the same business date.
            Load(tables.Records[2], "wellbore_uwi", wellbore);
            await DeliverAsync(runtime);

            var settled = await StateAsync(ledger, runtime, 2);
            AssertDelivered(settled);
            Assert.Null(settled.LastError);
            Assert.Equal((delivered.TargetVersion, delivered.MetadataHash, delivered.PayloadHash), (settled.TargetVersion, settled.MetadataHash, settled.PayloadHash));
            Assert.Empty(protocol.Deliveries);
            var identical = Assert.Single(await AttemptsAsync(ledger, runtime, 2), a => a.Phase == AttemptPhases.Identical);
            Assert.Contains("the held record is delivered again", identical.ResultJson, StringComparison.Ordinal);

            // From here it is a delivered record like any other.
            await DeliverAsync(runtime);
            Assert.Empty(protocol.Deliveries);
            Assert.Single(await AttemptsAsync(ledger, runtime, 2), a => a.Phase == AttemptPhases.Identical);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_row_corrected_to_something_new_at_the_same_business_date_is_sent_once()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            Load(tables.Records[2], "wellbore_uwi", wellbore);
            Load(tables.Records[2], "creator", "HAL");
            var run = await DeliverAsync(runtime);

            Assert.Equal(1, run.Work.Delivered);
            var sent = Assert.Single(protocol.Deliveries);
            Assert.Equal(Key(2), sent.Key);
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            await DeliverAsync(runtime);
            Assert.Single(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_row_corrected_with_a_later_business_date_moves_the_held_record()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var date = (DateTime)tables.Records[2].Row["update_date"]!;
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[2], "wellbore_uwi", Unresolvable, date.AddDays(1));
            await DeliverAsync(runtime);

            Load(tables.Records[2], "wellbore_uwi", wellbore, date.AddDays(2));
            await DeliverAsync(runtime);

            // The document did not change, only the business date: nothing to send, and the record is delivered again.
            var settled = await StateAsync(ledger, runtime, 2);
            AssertDelivered(settled);
            Assert.Equal(date.AddDays(2), settled.SourceModifiedUtc);
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_row_loaded_again_still_wrong_is_held_again_once_and_then_stays_blocked()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            // The ingestion table stamped the row again with the same wrong value: a new version of the row, tried once.
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);
            await DeliverAsync(runtime);

            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, Unresolvable);
            Assert.Equal(2, (await AttemptsAsync(ledger, runtime, 2)).Count(a => a.Outcome == AttemptOutcome.Held));
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_late_row_older_than_the_version_a_record_was_held_at_never_moves_it()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var date = (DateTime)tables.Records[2].Row["update_date"]!;
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[2], "wellbore_uwi", Unresolvable, date.AddDays(2));
            await DeliverAsync(runtime);
            var attempts = (await AttemptsAsync(ledger, runtime, 2)).Count;

            // An older file re-landed: the row carries a business date before the one the record was held at.
            Load(tables.Records[2], "wellbore_uwi", wellbore, date.AddDays(1));
            await DeliverAsync(runtime);

            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, Unresolvable);
            Assert.Equal(attempts, (await AttemptsAsync(ledger, runtime, 2)).Count);
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_record_held_before_it_was_ever_delivered_is_created_once_its_row_is_corrected()
    {
        var tables = await EstateAsync();
        var wellbore = tables.Records[2].Row["wellbore_uwi"];
        tables.Records[2].Row["wellbore_uwi"] = Unresolvable;
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverAsync(runtime);
            var held = await StateAsync(ledger, runtime, 2);
            AssertBlocked(held, RecordStatus.Held, Unresolvable);
            Assert.Null(held.TargetVersion);
            Assert.DoesNotContain(protocol.Deliveries, w => w.Key == Key(2));

            Load(tables.Records[2], "wellbore_uwi", wellbore);
            protocol.Deliveries.Clear();
            await DeliverAsync(runtime);

            var created = Assert.Single(protocol.Deliveries);
            Assert.Equal((Key(2), (long?)null), (created.Key, created.ExistingVersion));
            var delivered = await StateAsync(ledger, runtime, 2);
            AssertDelivered(delivered);
            Assert.NotNull(delivered.TargetVersion);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_held_row_marked_deleted_is_held_as_deleted_and_restoring_it_settles_the_record()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            // The ingestion table marks the row deleted: a change of its own, held for what it is, never sent or removed.
            _clock.Advance(TimeSpan.FromMinutes(1));
            tables.Records[2].DeletedUtc = Now;
            tables.Records[2].UpdatedUtc = Now;
            await DeliverAsync(runtime);
            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, "marked the record row deleted");
            Assert.Equal(AttemptPhases.SourceDeleted, (await AttemptsAsync(ledger, runtime, 2))[0].Phase);

            // The row restored as it was delivered: the record is delivered again with nothing sent.
            tables.Records[2].DeletedUtc = null;
            Load(tables.Records[2], "wellbore_uwi", wellbore);
            await DeliverAsync(runtime);
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    // ---- Releasing a record held at render: the next run plans it from its row ------------------------------------------

    [Fact]
    public async Task A_release_of_a_record_whose_row_is_still_wrong_holds_it_again_under_a_fresh_error()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);
            var problem = (await StateAsync(ledger, runtime, 2)).ProblemHash;

            Assert.Equal(1, await runtime.ReleaseAsync([Key(2)]));
            var released = await StateAsync(ledger, runtime, 2);
            Assert.Equal((RecordStatus.Held, false, (long?)null), (released.Status, released.Blocked, released.ProblemHash));
            Assert.NotNull(released.PlanRequestedUtc);
            await AssertNothingStrandedAsync(runtime);

            await DeliverAsync(runtime);
            var held = await StateAsync(ledger, runtime, 2);
            AssertBlocked(held, RecordStatus.Held, Unresolvable);
            Assert.Equal(problem, held.ProblemHash);
            Assert.Null(held.PlanRequestedUtc);
            Assert.Equal(2, (await AttemptsAsync(ledger, runtime, 2)).Count(a => a.Outcome == AttemptOutcome.Held));
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_release_after_the_row_was_corrected_in_place_settles_the_record_and_leaves_nothing_to_release()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            // Corrected in the ingestion table without its stamp moving: the row alone does not move the record.
            tables.Records[2].Row["wellbore_uwi"] = wellbore;
            await DeliverAsync(runtime);
            Assert.True((await StateAsync(ledger, runtime, 2)).Blocked);

            Assert.Equal(1, await runtime.ReleaseAsync(null));
            await DeliverAsync(runtime);

            AssertDelivered(await StateAsync(ledger, runtime, 2));
            Assert.Empty(protocol.Deliveries);
            Assert.Contains("the held record is delivered again", Assert.Single(await AttemptsAsync(ledger, runtime, 2), a => a.Phase == AttemptPhases.Identical).ResultJson, StringComparison.Ordinal);
            Assert.Equal(0, await runtime.ReleaseAsync(null));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_release_after_the_row_was_corrected_in_place_to_something_new_sends_it()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            tables.Records[2].Row["wellbore_uwi"] = SampleEstate.Logs()[3].WellboreUwi;
            Assert.Equal(1, await runtime.ReleaseAsync(null));
            await DeliverAsync(runtime);

            var sent = Assert.Single(protocol.Deliveries);
            Assert.Equal(Key(2), sent.Key);
            Assert.NotNull(sent.ExistingVersion);
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_released_record_whose_row_is_marked_deleted_before_the_run_is_held_as_deleted()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);
            Assert.Equal(1, await runtime.ReleaseAsync(null));

            _clock.Advance(TimeSpan.FromMinutes(1));
            tables.Records[2].DeletedUtc = Now;
            tables.Records[2].UpdatedUtc = Now;
            await DeliverAsync(runtime);

            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, "marked the record row deleted");
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_released_record_whose_row_is_gone_is_blocked_again_until_the_row_is_back()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            var row = tables.Records[2];
            Load(row, "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);
            Assert.Equal(1, await runtime.ReleaseAsync(null));

            // The row leaves the ingestion table: the run's pass cannot plan the record, and reads it again in its own scope.
            // A row that is gone answers the request, so no later run reads it again, and the record is blocked again,
            // saying why, with the gone row on its history.
            tables.Records.Remove(row);
            await DeliverAsync(runtime);
            var gone = await StateAsync(ledger, runtime, 2);
            AssertBlocked(gone, RecordStatus.Held, "row is gone from the ingestion table");
            Assert.Null(gone.PlanRequestedUtc);
            Assert.Single(await AttemptsAsync(ledger, runtime, 2), a => a.Phase == AttemptPhases.SourceMissing);
            Assert.Empty(await ledger.ListPlanRequestedAsync(runtime.Flow.Id, null, 10));
            await AssertNothingStrandedAsync(runtime);

            // A later run finds no request and no row: nothing more is written.
            var attempts = (await AttemptsAsync(ledger, runtime, 2)).Count;
            await DeliverAsync(runtime);
            Assert.Equal(attempts, (await AttemptsAsync(ledger, runtime, 2)).Count);

            // Landed again, corrected, the ingestion table stamps it anew: the record moves and is delivered again.
            tables.Records.Add(row);
            Load(row, "wellbore_uwi", wellbore);
            await DeliverAsync(runtime);
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_released_record_whose_row_went_back_before_the_version_OSDU_holds_is_delivered_again_at_that_version()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var delivered = await StateAsync(ledger, runtime, 2);
            var date = (DateTime)tables.Records[2].Row["update_date"]!;
            Load(tables.Records[2], "wellbore_uwi", Unresolvable, date.AddDays(1));
            await DeliverAsync(runtime);
            Assert.Equal(1, await runtime.ReleaseAsync(null));

            // Before the run that plans it, the row goes back to a business date older than the version OSDU holds.
            Load(tables.Records[2], "wellbore_uwi", Unresolvable, date.AddDays(-1));
            await DeliverAsync(runtime);

            var settled = await StateAsync(ledger, runtime, 2);
            AssertDelivered(settled);
            Assert.Equal((delivered.TargetVersion, delivered.SourceModifiedUtc), (settled.TargetVersion, settled.SourceModifiedUtc));
            Assert.Contains(
                "the held record is delivered again, at the version OSDU holds",
                Assert.Single(await AttemptsAsync(ledger, runtime, 2), a => a.Phase == AttemptPhases.Stale).ResultJson,
                StringComparison.Ordinal);
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_release_reaches_only_blocked_records_and_a_second_release_finds_nothing()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Assert.Equal(0, await runtime.ReleaseAsync(null));
            Assert.Equal(0, await runtime.ReleaseAsync([Key(0), Key(1)]));

            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);
            Assert.Equal(0, await runtime.ReleaseAsync([Key(0)]));
            Assert.Equal(1, await runtime.ReleaseAsync([Key(0), Key(2)]));
            Assert.Equal(0, await runtime.ReleaseAsync([Key(2)]));
            Assert.Equal(0, await runtime.ReleaseAsync(null));

            // Each release that reached a record names it under its activity; one that reached nothing names none.
            var releases = await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "release" });
            Assert.Equal(6, releases.Count);
            Assert.Single(releases, a => a.Summary!.StartsWith("released 1 record", StringComparison.Ordinal));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_release_of_one_issue_leaves_the_records_of_another_issue_blocked()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[1], "wellbore_uwi", Unresolvable);
            Load(tables.Records[2], "wellbore_uwi", "Wellbore Z-2");
            _clock.Advance(TimeSpan.FromMinutes(1));
            tables.Records[3].DeletedUtc = Now;
            tables.Records[3].UpdatedUtc = Now;
            await DeliverAsync(runtime);

            // Two records held for the same reason share an issue whatever wellbore they name; the deleted row is another.
            var unresolved = (await StateAsync(ledger, runtime, 1)).ProblemHash!.Value;
            Assert.Equal(unresolved, (await StateAsync(ledger, runtime, 2)).ProblemHash);
            Assert.NotEqual(unresolved, (await StateAsync(ledger, runtime, 3)).ProblemHash);

            Assert.Equal(2, await runtime.ReleaseProblemAsync(unresolved, "pattern shown"));
            Assert.False((await StateAsync(ledger, runtime, 1)).Blocked);
            Assert.False((await StateAsync(ledger, runtime, 2)).Blocked);
            AssertBlocked(await StateAsync(ledger, runtime, 3), RecordStatus.Held, "marked the record row deleted");
            Assert.Equal(0, await runtime.ReleaseProblemAsync(unresolved, "pattern shown"));

            await DeliverAsync(runtime);
            AssertBlocked(await StateAsync(ledger, runtime, 1), RecordStatus.Held, Unresolvable);
            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, "Wellbore Z-2");
            await AssertNothingStrandedAsync(runtime);
        }
    }

    // ---- Held or failed by a try: the rendered document is kept --------------------------------------------------------

    [Fact]
    public async Task A_refused_record_keeps_its_document_and_a_release_sends_that_document_again()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            protocol.FailWith = work => work.Key == Key(2) ? new OsduStatusException(400, "HTTP 400 Bad Request: data.Name is too long") : null;
            await DeliverAsync(runtime);
            var held = await StateAsync(ledger, runtime, 2);
            AssertBlocked(held, RecordStatus.Held, "HTTP 400");
            Assert.NotNull(held.PendingDocumentRef);
            Assert.Null(held.TargetVersion);

            // The same rows again send nothing more: the refusal is known.
            var tries = protocol.Deliveries.Count(w => w.Key == Key(2));
            await DeliverAsync(runtime);
            Assert.Equal(tries, protocol.Deliveries.Count(w => w.Key == Key(2)));

            // Released while OSDU still refuses it: tried once more with the kept document, held again.
            Assert.Equal(1, await runtime.ReleaseAsync([Key(2)]));
            var released = await StateAsync(ledger, runtime, 2);
            Assert.Equal((RecordStatus.Pending, false, 0), (released.Status, released.Blocked, released.AttemptCount));
            await DeliverAsync(runtime);
            Assert.Equal(tries + 1, protocol.Deliveries.Count(w => w.Key == Key(2)));
            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, "HTTP 400");

            // OSDU accepts it now: released, the kept document lands.
            protocol.FailWith = null;
            Assert.Equal(1, await runtime.ReleaseAsync(null));
            Assert.NotNull((await StateAsync(ledger, runtime, 2)).AcceptedMetadataHash);
            await DeliverAsync(runtime);
            var delivered = await StateAsync(ledger, runtime, 2);
            AssertDelivered(delivered);
            Assert.NotNull(delivered.TargetVersion);

            // Delivered, the document is no longer one a release accepted: the same document queued again is judged again.
            Assert.Null(delivered.AcceptedMetadataHash);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_refused_update_whose_row_goes_back_to_what_OSDU_holds_settles_and_drops_the_kept_document()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var delivered = await StateAsync(ledger, runtime, 2);
            var creator = tables.Records[2].Row["creator"];
            protocol.FailWith = work => work.Key == Key(2) ? new OsduStatusException(400, "HTTP 400 Bad Request: data.Source is not allowed") : null;
            Load(tables.Records[2], "creator", "HAL");
            await DeliverAsync(runtime);
            var held = await StateAsync(ledger, runtime, 2);
            AssertBlocked(held, RecordStatus.Held, "HTTP 400");
            Assert.NotNull(held.PendingDocumentRef);

            // The change is taken back in the source: OSDU still holds what the row now renders.
            protocol.Deliveries.Clear();
            Load(tables.Records[2], "creator", creator);
            await DeliverAsync(runtime);

            var settled = await StateAsync(ledger, runtime, 2);
            AssertDelivered(settled);
            Assert.Equal((delivered.TargetVersion, delivered.MetadataHash), (settled.TargetVersion, settled.MetadataHash));
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_refused_update_whose_row_is_corrected_to_something_new_sends_the_new_document()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            protocol.FailWith = work => work.Key == Key(2) && work.Document["data"]?["Source"]?.GetValue<string>() == "HAL"
                ? new OsduStatusException(400, "HTTP 400 Bad Request: data.Source is not allowed")
                : null;
            Load(tables.Records[2], "creator", "HAL");
            await DeliverAsync(runtime);
            var held = await StateAsync(ledger, runtime, 2);
            AssertBlocked(held, RecordStatus.Held, "HTTP 400");

            protocol.Deliveries.Clear();
            Load(tables.Records[2], "creator", "HAL 9000");
            await DeliverAsync(runtime);

            var sent = Assert.Single(protocol.Deliveries);
            Assert.Equal("HAL 9000", sent.Document["data"]!["Source"]!.GetValue<string>());
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_record_that_used_its_tries_is_failed_and_a_release_gives_it_a_fresh_budget()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(
            tables, f => f with { Reliability = f.Reliability with { Retry = f.Reliability.Retry with { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } } });
        using (runtime)
        {
            protocol.FailWith = work => work.Key == Key(2) ? new OsduStatusException(503, "HTTP 503 Service Unavailable") : null;
            await DeliverAsync(runtime);
            var failed = await StateAsync(ledger, runtime, 2);
            AssertBlocked(failed, RecordStatus.Failed, "failed after 1 attempt");
            Assert.NotNull(failed.PendingDocumentRef);

            // Still down: the same rows try nothing more.
            var tries = protocol.Deliveries.Count(w => w.Key == Key(2));
            await DeliverAsync(runtime);
            Assert.Equal(tries, protocol.Deliveries.Count(w => w.Key == Key(2)));

            protocol.FailWith = null;
            Assert.Equal(1, await runtime.ReleaseAsync(null));
            Assert.Equal(0, (await StateAsync(ledger, runtime, 2)).AttemptCount);
            await DeliverAsync(runtime);
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_failed_update_whose_row_goes_back_to_what_OSDU_holds_settles_the_record()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(
            tables, f => f with { Reliability = f.Reliability with { Retry = f.Reliability.Retry with { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } } });
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var creator = tables.Records[2].Row["creator"];
            protocol.FailWith = work => work.Key == Key(2) ? new OsduStatusException(503, "HTTP 503 Service Unavailable") : null;
            Load(tables.Records[2], "creator", "HAL");
            await DeliverAsync(runtime);
            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Failed, "failed after 1 attempt");

            protocol.Deliveries.Clear();
            Load(tables.Records[2], "creator", creator);
            await DeliverAsync(runtime);

            AssertDelivered(await StateAsync(ledger, runtime, 2));
            Assert.Empty(protocol.Deliveries);
            Assert.Contains(
                "the failed record is delivered again",
                Assert.Single(await AttemptsAsync(ledger, runtime, 2), a => a.Phase == AttemptPhases.Identical).ResultJson,
                StringComparison.Ordinal);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    // ---- Redelivery, sync and removal of a held record -----------------------------------------------------------------

    [Fact]
    public async Task A_redelivery_of_a_held_record_plans_it_again_and_sends_it_once_its_row_renders()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            // Redelivered while the row is still wrong: planned again, held again, nothing sent.
            Assert.Equal(1, await runtime.RedeliverAsync([Key(2)], RedeliverScope.All));
            await DeliverAsync(runtime);
            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, Unresolvable);
            Assert.Equal(2, (await AttemptsAsync(ledger, runtime, 2)).Count(a => a.Outcome == AttemptOutcome.Held));
            Assert.Empty(protocol.Deliveries);

            // Corrected in place and redelivered: a redelivery forgets what OSDU holds, so it is sent again whole.
            tables.Records[2].Row["wellbore_uwi"] = wellbore;
            Assert.Equal(1, await runtime.RedeliverAsync([Key(2)], RedeliverScope.All));
            await DeliverAsync(runtime);
            Assert.Equal(Key(2), Assert.Single(protocol.Deliveries).Key);
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_sync_asks_for_a_record_an_earlier_release_left_unplanned_and_the_next_run_settles_it()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);
            Load(tables.Records[2], "wellbore_uwi", wellbore);

            // What an earlier version left behind: released, its request answered, still held, its row read past.
            await using (var db = _db.CreateDbContext())
            {
                var key = Key(2).Value;
                await db.DeliveryRecords.Where(r => r.FlowId == runtime.Flow.Id && r.DeliveryKey == key)
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.Blocked, false).SetProperty(r => r.ProblemHash, (long?)null).SetProperty(r => r.PlanRequestedUtc, (DateTime?)null));
            }

            _readThrough = Now;
            await DeliverAsync(runtime);
            Assert.Equal(RecordStatus.Held, (await StateAsync(ledger, runtime, 2)).Status);

            // A sync finds the row moved on and asks for it; the next run settles it, sending nothing.
            await runtime.SyncAsync([Key(2)]);
            Assert.NotNull((await StateAsync(ledger, runtime, 2)).PlanRequestedUtc);
            await DeliverAsync(runtime);
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_held_record_removed_from_OSDU_is_unblocked_and_created_again_by_the_next_change_of_its_row()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            var removal = await runtime.RemoveAsync(RemovalSelection.Of([Key(2)]), RemovalChoice.Record);
            Assert.Equal(1, removal.Removed);
            var removed = await StateAsync(ledger, runtime, 2);
            Assert.Equal((RecordStatus.Deleted, false, (long?)null, (long?)null), (removed.Status, removed.Blocked, removed.ProblemHash, removed.TargetVersion));

            Load(tables.Records[2], "wellbore_uwi", wellbore);
            await DeliverAsync(runtime);
            var created = Assert.Single(protocol.Deliveries);
            Assert.Equal((Key(2), (long?)null), (created.Key, created.ExistingVersion));
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    // ---- Records an older version left stranded, and documents that are gone ------------------------------------------

    /// <summary>Leaves a held record as a release answered by a plan that left it parked used to: unblocked, unasked, with no document.</summary>
    private async Task StrandAsync(FlowRuntime runtime, int index)
    {
        await using var db = _db.CreateDbContext();
        var key = Key(index).Value;
        await db.DeliveryRecords.Where(r => r.FlowId == runtime.Flow.Id && r.DeliveryKey == key)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Blocked, false)
                .SetProperty(r => r.ProblemHash, (long?)null)
                .SetProperty(r => r.PlanRequestedUtc, (DateTime?)null)
                .SetProperty(r => r.LastError, "released; the flow's next run plans it again from its ingestion rows"));
    }

    [Fact]
    public async Task A_release_reaches_a_record_an_earlier_version_left_held_with_nothing_to_move_it()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[1], "wellbore_uwi", Unresolvable);
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);
            tables.Records[1].Row["wellbore_uwi"] = SampleEstate.Logs()[1].WellboreUwi;
            tables.Records[2].Row["wellbore_uwi"] = wellbore;
            await StrandAsync(runtime, 1);
            await StrandAsync(runtime, 2);
            var stranded = await ledger.StatsAsync(runtime.Flow.Id, Now);
            Assert.Equal((2L, 0L, 2L), (stranded.Held, stranded.Blocked, stranded.Stranded));

            // Named, or among every record a release of the flow reaches, it is asked to be planned again, and the next
            // run settles it from its row.
            Assert.Equal(1, await runtime.ReleaseAsync([Key(1)]));
            Assert.Equal(1, await runtime.ReleaseAsync(null));
            Assert.NotNull((await StateAsync(ledger, runtime, 1)).PlanRequestedUtc);
            Assert.NotNull((await StateAsync(ledger, runtime, 2)).PlanRequestedUtc);
            var released = await ledger.StatsAsync(runtime.Flow.Id, Now);
            Assert.Equal((2L, 0L, 0L), (released.Held, released.Blocked, released.Stranded));
            await DeliverAsync(runtime);
            AssertDelivered(await StateAsync(ledger, runtime, 1));
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_kept_document_that_is_gone_is_planned_again_from_its_row_rather_than_held_for_a_release()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            protocol.FailWith = work => work.Key == Key(2) ? new OsduStatusException(400, "HTTP 400 Bad Request: data.Name is too long") : null;
            await DeliverAsync(runtime);
            var held = await StateAsync(ledger, runtime, 2);
            AssertBlocked(held, RecordStatus.Held, "HTTP 400");

            // The work batch holding the kept document is pruned.
            var submission = (await ledger.GetSubmissionAsync(held.LastSubmissionId!.Value))!;
            Assert.True(DocumentRef.TryParse(held.PendingDocumentRef, out var reference));
            File.Delete(WorkBatchFile.PathFor(submission.WorkLocation!, submission.SubmissionId, reference.Batch));

            // Released, the try finds nothing to send: the record drops what it had queued and asks for a plan of its row,
            // with no issue to release and nothing sent.
            protocol.FailWith = null;
            protocol.Deliveries.Clear();
            Assert.Equal(1, await runtime.ReleaseAsync([Key(2)]));
            await DeliverAsync(runtime);
            var replanned = await StateAsync(ledger, runtime, 2);
            Assert.Equal((RecordStatus.Held, false, (string?)null, (long?)null), (replanned.Status, replanned.Blocked, replanned.PendingDocumentRef, replanned.ProblemHash));
            Assert.NotNull(replanned.PlanRequestedUtc);
            Assert.Contains("could not be read", replanned.LastError, StringComparison.Ordinal);
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);

            // The next run renders it from its row and sends it.
            await DeliverAsync(runtime);
            Assert.Equal(Key(2), Assert.Single(protocol.Deliveries).Key);
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_record_removed_after_it_was_released_is_sent_back_once_by_the_next_run()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);
            Assert.Equal(1, await runtime.ReleaseAsync([Key(2)]));
            var released = (await StateAsync(ledger, runtime, 2)).PlanRequestedUtc;
            Assert.NotNull(released);
            tables.Records[2].Row["wellbore_uwi"] = SampleEstate.Logs()[3].WellboreUwi;

            // Removed before any run answered the release: the removal's own request takes its place, so the next run reads
            // the row by key and sends the record back, once.
            _clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([Key(2)]), RemovalChoice.Record)).Removed);
            Assert.Equal(Now, (await StateAsync(ledger, runtime, 2)).PlanRequestedUtc);
            await DeliverAsync(runtime);

            Assert.Single(protocol.Deliveries, w => w.Key == Key(2));
            var state = await StateAsync(ledger, runtime, 2);
            Assert.Equal((RecordStatus.Delivered, (DateTime?)null), (state.Status, state.PlanRequestedUtc));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_held_record_whose_earlier_delivery_is_not_undone_yet_is_not_settled_by_a_row_that_renders_what_OSDU_holds()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            // What an unfinished delivery of the record left in OSDU is still to be undone.
            await using (var db = _db.CreateDbContext())
            {
                var key = Key(2).Value;
                var row = await db.DeliveryRecords.AsNoTracking().SingleAsync(r => r.FlowId == runtime.Flow.Id && r.DeliveryKey == key);
                db.DeliveryArtifacts.Add(new DeliveryArtifact
                {
                    PartitionId = row.PartitionId,
                    FlowId = row.FlowId,
                    DeliveryKey = key,
                    UnitId = Guid.NewGuid(),
                    UnitStartedUtc = Now,
                    Slot = "record",
                    Role = ArtifactRoles.Record,
                    TargetId = row.TargetId,
                    State = ArtifactStatuses.Name(ArtifactStatus.Failed),
                    CreatedUtc = Now,
                    UpdatedUtc = Now,
                });
                await db.SaveChangesAsync();
            }

            // The row corrected: the record is not delivered again while OSDU holds more than the ledger says. It is blocked at
            // the corrected row, saying why, and a run reading the row again does nothing more.
            Load(tables.Records[2], "wellbore_uwi", wellbore);
            await DeliverAsync(runtime);
            var waiting = await StateAsync(ledger, runtime, 2);
            AssertBlocked(waiting, RecordStatus.Held, "not undone yet");
            Assert.Equal(AttemptPhases.UndoWait, (await AttemptsAsync(ledger, runtime, 2))[0].Phase);
            var attempts = (await AttemptsAsync(ledger, runtime, 2)).Count;
            await FullReadAsync(runtime);
            Assert.Equal(attempts, (await AttemptsAsync(ledger, runtime, 2)).Count);

            // Released while the undo is still open: blocked again, never settled.
            Assert.Equal(1, await runtime.ReleaseAsync([Key(2)]));
            await DeliverAsync(runtime);
            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, "not undone yet");
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    // ---- A plan writes only against the record it read: what an operator does while a run plans is never lost ---------

    [Fact]
    public async Task A_release_made_while_a_run_plans_a_blocked_record_is_kept_for_the_next_run()
    {
        var tables = await EstateAsync();
        var race = new RacingSearchFactory();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, searches: race);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            // A run reads the whole scope. After it read the blocked record from the ledger, while another record of the same
            // batch asks the platform about a wellbore, an operator releases the blocked one.
            race.Arm(() => ledger.ReleaseAsync(runtime.Flow.Id, [Key(2)], Now));
            Load(tables.Records[1], "wellbore_uwi", "Wellbore Q-1");
            await FullReadAsync(runtime);
            Assert.True(race.Ran);

            var released = await StateAsync(ledger, runtime, 2);
            Assert.Equal((RecordStatus.Held, false), (released.Status, released.Blocked));
            Assert.NotNull(released.PlanRequestedUtc);
            await AssertNothingStrandedAsync(runtime);

            // The next run answers the release: the row is still wrong, so the record is held again.
            await DeliverAsync(runtime);
            var held = await StateAsync(ledger, runtime, 2);
            AssertBlocked(held, RecordStatus.Held, Unresolvable);
            Assert.Null(held.PlanRequestedUtc);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_release_made_while_a_run_renders_the_record_is_not_undone_by_that_run_holding_it()
    {
        var tables = await EstateAsync();
        var race = new RacingSearchFactory();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, searches: race);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            // Loaded again naming another wellbore nobody holds: the run renders it, and while it asks the platform about
            // that wellbore, an operator releases the record. The run's hold is not the record's any more, and is not written.
            race.Arm(() => ledger.ReleaseAsync(runtime.Flow.Id, [Key(2)], Now));
            Load(tables.Records[2], "wellbore_uwi", "Wellbore Q-2");
            await DeliverAsync(runtime);
            Assert.True(race.Ran);
            var released = await StateAsync(ledger, runtime, 2);
            Assert.Equal((RecordStatus.Held, false), (released.Status, released.Blocked));
            Assert.NotNull(released.PlanRequestedUtc);
            Assert.Single(await AttemptsAsync(ledger, runtime, 2), a => a.Outcome == AttemptOutcome.Held);
            await AssertNothingStrandedAsync(runtime);

            // The next run answers the release from the row as it is now.
            await DeliverAsync(runtime);
            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, "Wellbore Q-2");
            Assert.Equal(2, (await AttemptsAsync(ledger, runtime, 2)).Count(a => a.Outcome == AttemptOutcome.Held));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_redelivery_made_while_a_run_settles_a_held_record_stands_and_sends_the_record()
    {
        var tables = await EstateAsync();
        var race = new RacingSearchFactory();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, searches: race);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var wellbore = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);

            // The row is corrected; while the run plans it (another record asks the platform), an operator redelivers it. The
            // plan found it rendering what OSDU holds, but the redelivery forgot what OSDU holds since: the settle is not
            // written, and the redelivery stands.
            race.Arm(() => ledger.ForceRedeliverAsync(runtime.Flow.Id, [Key(2)], RedeliverScope.All, Now));
            Load(tables.Records[2], "wellbore_uwi", wellbore);
            Load(tables.Records[1], "wellbore_uwi", "Wellbore Q-3");
            await DeliverAsync(runtime);
            Assert.True(race.Ran);
            var asked = await StateAsync(ledger, runtime, 2);
            Assert.Equal(RecordStatus.Held, asked.Status);
            Assert.NotNull(asked.PlanRequestedUtc);
            Assert.DoesNotContain(protocol.Deliveries, w => w.Key == Key(2));
            await AssertNothingStrandedAsync(runtime);

            // The next run plans it under the redelivery, blocked or not, and sends it whole.
            await DeliverAsync(runtime);
            var sent = Assert.Single(protocol.Deliveries, w => w.Key == Key(2));
            Assert.True(sent.DeliverMetadata);
            AssertDelivered(await StateAsync(ledger, runtime, 2));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_redelivery_made_while_a_run_queues_the_record_stays_for_the_next_run()
    {
        var tables = await EstateAsync();
        var race = new RacingSearchFactory();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables, searches: race);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);

            // The run queues the changed record; after it read the record, an operator asks for a redelivery of it.
            race.Arm(() => ledger.ForceRedeliverAsync(runtime.Flow.Id, [Key(0)], RedeliverScope.All, Now));
            Load(tables.Records[0], "creator", "HAL");
            Load(tables.Records[1], "wellbore_uwi", "Wellbore Q-4");
            await DeliverAsync(runtime);
            Assert.True(race.Ran);

            // This run sends what it planned, and the request made after it read the record stays.
            Assert.Single(protocol.Deliveries, w => w.Key == Key(0));
            var delivered = await StateAsync(ledger, runtime, 0);
            Assert.Equal(RecordStatus.Delivered, delivered.Status);
            Assert.NotNull(delivered.PlanRequestedUtc);

            // The next run answers it: the payload the redelivery forgot is sent again.
            protocol.Deliveries.Clear();
            await DeliverAsync(runtime);
            var again = Assert.Single(protocol.Deliveries, w => w.Key == Key(0));
            Assert.True(again.DeliverPayload);
            Assert.Null((await StateAsync(ledger, runtime, 0)).PlanRequestedUtc);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_record_a_cache_change_waits_on_keeps_its_request_until_a_plan_decides_it()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);
            Assert.Equal(1, await runtime.ReleaseAsync([Key(2)]));
            var read = await StateAsync(ledger, runtime, 2);
            var submission = read.LastSubmissionId!.Value;

            // A plan that found the record held back by a cache change awaiting approval decided nothing: the release stays.
            await ledger.MarkSkippedAsync(runtime.Flow.Id, [new SkippedRecord { DeliveryKey = Key(2), Kind = SkipKind.AwaitingApproval, Reason = "awaiting approval", Basis = PlanBasis.Of(read) }], submission);
            Assert.Equal(read.PlanRequestedUtc, (await StateAsync(ledger, runtime, 2)).PlanRequestedUtc);
            await AssertNothingStrandedAsync(runtime);

            // Once decided, the plan that reads it answers the release.
            await DeliverAsync(runtime);
            AssertBlocked(await StateAsync(ledger, runtime, 2), RecordStatus.Held, Unresolvable);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task Newer_work_is_refused_against_the_document_a_waiting_record_queued()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var date = (DateTime)tables.Records[0].Row["update_date"]!;

            // A later version is queued and waits for a record it refers to.
            Load(tables.Records[0], "creator", "HAL", date.AddDays(2));
            runtime.Selection = SourceSelection.Full();
            runtime.SubmissionId = null;
            await runtime.IntakeAsync(force: true);
            await using (var db = _db.CreateDbContext())
            {
                var key = Key(0).Value;
                await db.DeliveryRecords.Where(r => r.FlowId == runtime.Flow.Id && r.DeliveryKey == key)
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, "waiting").SetProperty(r => r.WaitingFor, "dev:master-data--Wellbore:elsewhere"));
            }

            var queued = await StateAsync(ledger, runtime, 0);
            Assert.True(queued.HasPendingWork);

            // A row older than the queued version is stale against it, never queued in its place.
            Load(tables.Records[0], "creator", "SLB", date.AddDays(1));
            var plan = await runtime.PlanAsync(force: true);
            var entry = plan.Entries.Single(e => e.Key == Key(0));
            Assert.Equal((PlannedAction.Skip, SkipTier.Stale), (entry.Action, entry.SkipTier));
            Assert.Contains("already queued", entry.Reason, StringComparison.Ordinal);
        }
    }

    // ---- Leases that run out, removals under a send, attempts, records with no source key, waiting records -------------

    /// <summary>Plans the whole scope into a submission of its own and stops there: the records it queued stay pending.</summary>
    private async Task StageAsync(FlowRuntime runtime)
    {
        _clock.Advance(TimeSpan.FromSeconds(1));
        runtime.Selection = SourceSelection.Full();
        runtime.SubmissionId = null;
        runtime.RunId = Guid.CreateVersion7();
        await runtime.IntakeAsync(force: true);
    }

    /// <summary>How a worker's try of <paramref name="claimed"/> ended, as the worker appends it under its lease.</summary>
    private RecordCompletion Completion(RecordState claimed, RecordStatus status, string? error, long? version = null) => new()
    {
        DeliveryKey = claimed.DeliveryKey,
        Status = status,
        Promote = status == RecordStatus.Delivered,
        TargetVersion = version,
        TargetId = claimed.TargetId,
        Error = error,
        NextAttemptUtc = status == RecordStatus.Pending ? Now.AddMinutes(1) : null,
        Claimed = ClaimedWork.Of(claimed),
        ClaimedAttemptCount = claimed.AttemptCount,
        Attempt = new AttemptRecord
        {
            DeliveryKey = claimed.DeliveryKey,
            SubmissionId = claimed.LastSubmissionId,
            Worker = "worker",
            StartedUtc = Now,
            CompletedUtc = Now,
            Outcome = status switch
            {
                RecordStatus.Delivered => AttemptOutcome.Delivered,
                RecordStatus.Held => AttemptOutcome.Held,
                _ => AttemptOutcome.Failed,
            },
            Phase = status == RecordStatus.Delivered ? "metadata" : "none",
            TargetVersion = version,
            Error = error,
        },
    };

    [Fact]
    public async Task A_try_that_finishes_after_its_lease_ran_out_never_overwrites_what_happened_to_the_record_since()
    {
        var tables = await EstateAsync();
        tables.Records.RemoveRange(1, tables.Records.Count - 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await StageAsync(runtime);
            var flow = runtime.Flow.Id;
            var stalled = await ledger.ClaimAsync(flow, null, "stalled-worker", 10, TimeSpan.FromMinutes(1), Now, attempts: 4);
            var claimed = Assert.Single(stalled.Records);

            // The worker stalls past its lease: the next claim recovers the lease, hands the record back and takes it, and
            // its try lands.
            _clock.Advance(TimeSpan.FromMinutes(2));
            var next = await ledger.ClaimAsync(flow, null, "next-worker", 10, TimeSpan.FromMinutes(5), Now, attempts: 4);
            var retaken = Assert.Single(next.Records);
            Assert.Equal(claimed.AttemptCount + 1, retaken.AttemptCount);
            await ledger.AppendAsync(flow, next.Lease!.Token, new LeaseAppend([], [Completion(retaken, RecordStatus.Delivered, null, version: 1002)]));
            await ledger.CloseLeaseAsync(next.Lease.Token, new LeaseClosing { End = LeaseEnd.Done }, Now);
            Assert.Equal((RecordStatus.Delivered, (long?)1002), ((await StateAsync(ledger, runtime, 0)).Status, (await StateAsync(ledger, runtime, 0)).TargetVersion));

            // The stalled worker finishes after all, with a failure to try again: too late to take the delivered record back
            // to pending. Its attempt is on the record's history all the same, since the try happened.
            await ledger.AppendAsync(flow, stalled.Lease!.Token, new LeaseAppend([], [Completion(claimed, RecordStatus.Pending, "HTTP 503 Service Unavailable")]));
            await ledger.CloseLeaseAsync(stalled.Lease.Token, new LeaseClosing { End = LeaseEnd.Done }, Now);
            var delivered = await StateAsync(ledger, runtime, 0);
            Assert.Equal((RecordStatus.Delivered, (long?)1002, (DateTime?)null), (delivered.Status, delivered.TargetVersion, delivered.NextAttemptUtc));
            Assert.Contains(await AttemptsAsync(ledger, runtime, 0), a => a.Error == "HTTP 503 Service Unavailable");

            // A record nothing touched since its lease ran out takes the late completion: it is the newest word on it.
            Load(tables.Records[0], "creator", "HAL");
            await StageAsync(runtime);
            var slow = await ledger.ClaimAsync(flow, null, "slow-worker", 10, TimeSpan.FromMinutes(1), Now, attempts: 4);
            var slowClaim = Assert.Single(slow.Records);
            _clock.Advance(TimeSpan.FromMinutes(2));
            await ledger.RecoverExpiredLeasesAsync(flow, Now);
            Assert.Equal(RecordStatus.Pending, (await StateAsync(ledger, runtime, 0)).Status);
            await ledger.AppendAsync(flow, slow.Lease!.Token, new LeaseAppend([], [Completion(slowClaim, RecordStatus.Delivered, null, version: 1003)]));
            await ledger.CloseLeaseAsync(slow.Lease.Token, new LeaseClosing { End = LeaseEnd.Done }, Now);
            Assert.Equal((RecordStatus.Delivered, (long?)1003), ((await StateAsync(ledger, runtime, 0)).Status, (await StateAsync(ledger, runtime, 0)).TargetVersion));

            // Handed back, and held by a plan of a row that cannot be delivered before the stalled worker finishes: the
            // hold stands, the try's attempt is on the history.
            Load(tables.Records[0], "creator", "SLB");
            await StageAsync(runtime);
            var stuck = await ledger.ClaimAsync(flow, null, "stuck-worker", 10, TimeSpan.FromMinutes(1), Now, attempts: 4);
            var stuckClaim = Assert.Single(stuck.Records);
            _clock.Advance(TimeSpan.FromMinutes(2));
            await ledger.RecoverExpiredLeasesAsync(flow, Now);
            Load(tables.Records[0], "wellbore_uwi", Unresolvable);
            await StageAsync(runtime);
            AssertBlocked(await StateAsync(ledger, runtime, 0), RecordStatus.Held, Unresolvable);
            await ledger.AppendAsync(flow, stuck.Lease!.Token, new LeaseAppend([], [Completion(stuckClaim, RecordStatus.Delivered, null, version: 1004)]));
            await ledger.CloseLeaseAsync(stuck.Lease.Token, new LeaseClosing { End = LeaseEnd.Done }, Now);
            var held = await StateAsync(ledger, runtime, 0);
            AssertBlocked(held, RecordStatus.Held, Unresolvable);
            Assert.Equal((long?)1003, held.TargetVersion);
            Assert.Contains(await AttemptsAsync(ledger, runtime, 0), a => a.TargetVersion == 1004);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_record_whose_worker_stops_mid_attempt_every_time_fails_once_its_tries_are_used_up()
    {
        var tables = await EstateAsync();
        tables.Records.RemoveRange(1, tables.Records.Count - 1);
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await StageAsync(runtime);
            var flow = runtime.Flow.Id;

            // Two tries, each claimed by a worker that stops mid-attempt: each lease runs out and is recovered.
            for (var i = 1; i <= 2; i++)
            {
                Assert.Single((await ledger.ClaimAsync(flow, null, "crashing-worker", 10, TimeSpan.FromMinutes(1), Now, attempts: 2)).Records);
                _clock.Advance(TimeSpan.FromMinutes(2));
                await ledger.RecoverExpiredLeasesAsync(flow, Now);
                var after = await StateAsync(ledger, runtime, 0);
                if (i == 1)
                {
                    Assert.Equal((RecordStatus.Pending, false, 1), (after.Status, after.Blocked, after.AttemptCount));
                }
            }

            // Every try is on the record's history, and the second used up the budget: failed for good, blocked, not claimed.
            var failed = await StateAsync(ledger, runtime, 0);
            AssertBlocked(failed, RecordStatus.Failed, "failed after 2 attempt(s): the lease expired mid-attempt every time");
            var tries = (await AttemptsAsync(ledger, runtime, 0)).Where(a => a.Outcome == AttemptOutcome.Failed).ToList();
            Assert.Equal(2, tries.Count);
            Assert.Contains(tries, a => a.Error!.Contains("the lease expired mid-attempt (the worker stopped)", StringComparison.Ordinal));
            Assert.Empty((await ledger.ClaimAsync(flow, null, "next-worker", 10, TimeSpan.FromMinutes(5), Now, attempts: 2)).Records);
            Assert.Equal(1L, (await ledger.StatsAsync(flow, Now)).Blocked);

            // Released with fresh tries, it is sent.
            Assert.Equal(1, await runtime.ReleaseAsync([Key(0)]));
            Assert.Equal(0, (await StateAsync(ledger, runtime, 0)).AttemptCount);
            await DeliverAsync(runtime);
            AssertDelivered(await StateAsync(ledger, runtime, 0));
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_record_being_delivered_is_not_removed_under_the_delivery()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[0], "creator", "HAL");
            await StageAsync(runtime);
            var lease = await ledger.ClaimAsync(runtime.Flow.Id, null, "sending-worker", 10, TimeSpan.FromMinutes(5), Now);
            Assert.Equal(Key(0), Assert.Single(lease.Records).DeliveryKey);

            // A worker is sending it: the removal leaves it, saying why, and asks OSDU nothing.
            var refused = await runtime.RemoveAsync(RemovalSelection.Of([Key(0)]), RemovalChoice.Record);
            var result = Assert.Single(refused.Records);
            Assert.Equal("skipped", result.Outcome);
            Assert.Contains("a worker is delivering it", result.Detail, StringComparison.Ordinal);
            Assert.Empty(protocol.Deletes);
            Assert.Equal(RecordStatus.Delivering, (await StateAsync(ledger, runtime, 0)).Status);

            // Once the send settles, the removal goes through.
            await ledger.CloseLeaseAsync(lease.Lease!.Token, new LeaseClosing { End = LeaseEnd.Stopped }, Now);
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([Key(0)]), RemovalChoice.Record)).Removed);
            Assert.Single(protocol.Deletes);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task A_removal_holds_its_records_so_no_worker_sends_one_while_it_is_taken_out()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[0], "creator", "HAL");
            await StageAsync(runtime);
            Assert.Equal(RecordStatus.Pending, (await StateAsync(ledger, runtime, 0)).Status);

            // While the removal calls OSDU, a worker comes to claim the record's queued work: the record is the removal's,
            // and nothing is sent.
            var raced = new List<(ClaimedRecords Claim, RecordState During)>();
            protocol.BeforeDelete = async _ =>
                raced.Add((await ledger.ClaimAsync(runtime.Flow.Id, null, "racing-worker", 10, TimeSpan.FromMinutes(5), Now), await StateAsync(ledger, runtime, 0)));
            var removal = await runtime.RemoveAsync(RemovalSelection.Of([Key(0)]), RemovalChoice.Record);
            protocol.BeforeDelete = null;

            Assert.Equal(1, removal.Removed);
            var (claim, during) = Assert.Single(raced);
            Assert.Empty(claim.Records);
            Assert.Equal(RecordStatus.Pending, during.Status);
            Assert.NotNull(during.LeaseOwner);
            var removed = await StateAsync(ledger, runtime, 0);
            Assert.Equal((RecordStatus.Deleted, (string?)null, (string?)null), (removed.Status, removed.LeaseOwner, removed.PendingDocumentRef));
            Assert.Empty((await ledger.ClaimAsync(runtime.Flow.Id, null, "next-worker", 10, TimeSpan.FromMinutes(5), Now)).Records);
            Assert.Empty(protocol.Deliveries);
            Assert.Null(await ledger.NextLeaseExpiryAsync(runtime.Flow.Id, null));
        }
    }

    [Fact]
    public async Task A_removal_that_stops_lets_its_records_go_once_its_hold_runs_out_with_no_try_charged()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            Load(tables.Records[0], "creator", "HAL");
            await StageAsync(runtime);
            var flow = runtime.Flow.Id;
            var before = (await AttemptsAsync(ledger, runtime, 0)).Count;

            // A removal holds a queued record and a delivered one, and stops: no claim takes the queued one, and newer work
            // queued meanwhile stays held.
            var hold = await ledger.HoldForRemovalAsync(flow, [Key(0), Key(1)], "stopped-removal", TimeSpan.FromMinutes(5), Now);
            Assert.Equal(new HashSet<DeliveryKey> { Key(0), Key(1) }, hold.Held.ToHashSet());
            Assert.Empty((await ledger.ClaimAsync(flow, null, "worker", 10, TimeSpan.FromMinutes(5), Now)).Records);
            Load(tables.Records[0], "creator", "SLB");
            await StageAsync(runtime);
            Assert.Equal((RecordStatus.Pending, hold.Token), ((await StateAsync(ledger, runtime, 0)).Status, (await StateAsync(ledger, runtime, 0)).LeaseOwner));
            Assert.Empty((await ledger.ClaimAsync(flow, null, "worker", 10, TimeSpan.FromMinutes(5), Now)).Records);

            // A second removal passes over what the first holds.
            Assert.Empty((await ledger.HoldForRemovalAsync(flow, [Key(0)], "second-removal", TimeSpan.FromMinutes(5), Now)).Held);

            // Its hold runs out and the next claim lets the records go as they are: nothing charged, nothing written.
            _clock.Advance(TimeSpan.FromMinutes(6));
            var claim = await ledger.ClaimAsync(flow, null, "worker", 10, TimeSpan.FromMinutes(5), Now);
            Assert.Equal(Key(0), Assert.Single(claim.Records).DeliveryKey);
            Assert.Equal(1, Assert.Single(claim.Records).AttemptCount);
            Assert.Equal(before, (await AttemptsAsync(ledger, runtime, 0)).Count);
            var other = await StateAsync(ledger, runtime, 1);
            Assert.Equal((RecordStatus.Delivered, (string?)null), (other.Status, other.LeaseOwner));
            await ledger.CloseLeaseAsync(claim.Lease!.Token, new LeaseClosing { End = LeaseEnd.Stopped }, Now);
            await DeliverAsync(runtime);
            AssertDelivered(await StateAsync(ledger, runtime, 0));
            Assert.Single(protocol.Deliveries);
        }
    }

    [Fact]
    public async Task A_try_that_sends_only_the_document_records_the_payload_OSDU_already_holds()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var delivered = await StateAsync(ledger, runtime, 0);
            Load(tables.Records[0], "creator", "HAL");
            await StageAsync(runtime);
            var queued = await StateAsync(ledger, runtime, 0);
            Assert.Equal((true, false), (queued.PendingMetadata, queued.PendingPayload));

            // The queued work names a payload it does not send (the row's, read where its files were older than OSDU's).
            await using (var db = _db.CreateDbContext())
            {
                var key = Key(0).Value;
                await db.DeliveryRecords.Where(r => r.FlowId == runtime.Flow.Id && r.DeliveryKey == key)
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.PendingPayloadHash, "a-payload-never-sent"));
            }

            await DeliverAsync(runtime);
            var sent = Assert.Single(protocol.Deliveries);
            Assert.Equal((true, false), (sent.DeliverMetadata, sent.DeliverPayload));

            // What the attempt says OSDU holds at the new version is what a reversal or a step back writes back later.
            var attempt = (await AttemptsAsync(ledger, runtime, 0)).First(a => a.Outcome == AttemptOutcome.Delivered);
            Assert.Equal(delivered.PayloadHash, attempt.PayloadHash);
            Assert.Equal(delivered.PayloadHash, (await StateAsync(ledger, runtime, 0)).PayloadHash);
        }
    }

    [Fact]
    public async Task A_record_asked_for_again_with_no_source_key_is_planned_by_a_read_of_the_whole_scope()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            var wellbore1 = tables.Records[1].Row["wellbore_uwi"];
            var wellbore2 = tables.Records[2].Row["wellbore_uwi"];
            Load(tables.Records[1], "wellbore_uwi", Unresolvable);
            Load(tables.Records[2], "wellbore_uwi", Unresolvable);
            await DeliverAsync(runtime);
            _clock.Advance(TimeSpan.FromHours(1));
            await DeliverAsync(runtime);

            // Both corrected in place and released, as records an older version kept without a source key.
            tables.Records[1].Row["wellbore_uwi"] = wellbore1;
            tables.Records[2].Row["wellbore_uwi"] = wellbore2;
            Assert.Equal(2, await runtime.ReleaseAsync([Key(1), Key(2)]));
            await using (var db = _db.CreateDbContext())
            {
                Guid[] keyless = [Key(1).Value, Key(2).Value];
                await db.DeliveryRecords.Where(r => r.FlowId == runtime.Flow.Id && keyless.Contains(r.DeliveryKey))
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.SourceKeyJson, (string?)null));
            }

            // Record 1's row is gone from the table; record 2's is there, stamped before the scope's watermark, so an ordinary
            // run's window would not meet it.
            var row1 = tables.Records[1];
            var row2 = tables.Records[2];
            tables.Records.Remove(row1);
            var read = await ScopeReads.DecideAsync(ledger, runtime.Flow, runtime.Parameters, runtime.Mapping.Context);
            Assert.Equal(ScopeReadReason.SinceWatermark, read.Reason);
            Assert.True(row2.UpdatedUtc < read.Watermark!.UpdatedThroughUtc.AddSeconds(-runtime.Flow.Source.Incremental.OverlapSeconds));

            // A deliver run, as the executor makes it: no pass can read them by key, so the run reads the whole scope, which
            // meets record 2's row (planned, delivered again, keyed) and not record 1's (its request answered, blocked again).
            runtime.RunId = Guid.CreateVersion7();
            runtime.SubmissionId = null;
            await DeliveryExecutor.ExecuteInterfaceAsync(runtime, DeliveryOperations.Deliver, DeliveryRunPayload.None, null, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);

            var planned = await StateAsync(ledger, runtime, 2);
            AssertDelivered(planned);
            Assert.NotNull(planned.SourceKeyJson);
            var gone = await StateAsync(ledger, runtime, 1);
            AssertBlocked(gone, RecordStatus.Held, "row is gone from the ingestion table");
            Assert.Null(gone.PlanRequestedUtc);
            Assert.Contains(await AttemptsAsync(ledger, runtime, 1), a => a.Phase == AttemptPhases.SourceMissing);
            Assert.Empty(await ledger.ListPlanRequestedAsync(runtime.Flow.Id, null, 10));
            Assert.Empty(protocol.Deliveries);
            await AssertNothingStrandedAsync(runtime);
        }
    }

    [Fact]
    public async Task Sending_a_waiting_record_without_waiting_still_checks_its_other_references()
    {
        var tables = await EstateAsync();
        var (runtime, protocol, ledger) = await RuntimeAsync(tables);
        using (runtime)
        {
            await DeliverEverythingAsync(runtime, protocol);
            const string waitedFor = "dev:work-product-component--WellLog:not-delivered-yet";
            const string elsewhere = "dev:master-data--Well:another-system";

            // Record 2 holds an id it has not delivered; record 0's queued document refers to it, and to an id of another
            // system, and waits.
            Load(tables.Records[0], "creator", "HAL");
            await StageAsync(runtime);
            await using (var db = _db.CreateDbContext())
            {
                var holder = Key(2).Value;
                var waiting = Key(0).Value;
                var references = RecordReferences.Encode([new RecordReference(waitedFor, "data.WellboreID"), new RecordReference(elsewhere, "data.WellID")]);
                await db.DeliveryRecords.Where(r => r.FlowId == runtime.Flow.Id && r.DeliveryKey == holder)
                    .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, "held").SetProperty(r => r.Blocked, true).SetProperty(r => r.TargetVersion, (long?)null).SetProperty(r => r.TargetId, waitedFor));
                await db.DeliveryRecords.Where(r => r.FlowId == runtime.Flow.Id && r.DeliveryKey == waiting)
                    .ExecuteUpdateAsync(set => set
                        .SetProperty(r => r.Status, "waiting")
                        .SetProperty(r => r.WaitingFor, waitedFor)
                        .SetProperty(r => r.PendingReferences, references));
            }

            // Sent without waiting: the reference it waited for goes, the other stays for the check before sending, and no
            // claim leaves it waiting again.
            Assert.Equal(1, await runtime.ReleaseAsync([Key(0)]));
            var released = await StateAsync(ledger, runtime, 0);
            Assert.Equal((RecordStatus.Pending, (string?)null), (released.Status, released.WaitingFor));
            Assert.Equal([new RecordReference(elsewhere, "data.WellID")], released.PendingReferences);
            var claim = await ledger.ClaimAsync(runtime.Flow.Id, null, "w", 10, TimeSpan.FromMinutes(5), Now);
            Assert.Equal(Key(0), Assert.Single(claim.Records).DeliveryKey);
            Assert.Empty(claim.Waiting);
        }
    }

    /// <summary>
    /// The sample wellbore search, with one action run the first time a plan asks it a question after the action is armed:
    /// a moment after the plan read its batch's records from the ledger and before the intake writes what it decided about
    /// them, where an operator's release or redelivery races a run.
    /// </summary>
    private sealed class RacingSearchFactory : IRecordSearchFactory
    {
        private readonly FixedRecordSearchFactory _inner = FixedRecordSearchFactory.SampleWellbores();
        private Func<Task>? _race;

        public bool Ran { get; private set; }

        public void Arm(Func<Task> race) => _race = race;

        public IRecordSearch Create(FlowDefinition flow, Func<CancellationToken, Task<OsduHttpClient>> target, ILoggerFactory loggers)
            => new Racing(_inner.Create(flow, target, loggers), this);

        private async Task RaceAsync()
        {
            if (Interlocked.Exchange(ref _race, null) is { } race)
            {
                await race();
                Ran = true;
            }
        }

        private sealed class Racing(IRecordSearch inner, RacingSearchFactory owner) : IRecordSearch
        {
            public bool TryAnswer(SearchQuestion question, [NotNullWhen(true)] out SearchAnswer? answer) => inner.TryAnswer(question, out answer);

            public bool TryRead(ParentQuestion question, [NotNullWhen(true)] out ParentAnswer? answer) => inner.TryRead(question, out answer);

            public async Task AnswerAsync(IReadOnlyCollection<PlatformQuestion> questions, CancellationToken ct = default)
            {
                if (questions.Count > 0)
                {
                    await owner.RaceAsync();
                }

                await inner.AnswerAsync(questions, ct);
            }
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
    }
}
