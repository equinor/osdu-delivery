using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Blocked records grouped by the problem that keeps them so (osdu/docs/reference/concepts/record-lifecycle.md,
/// Issues), on SQL Server: every write that blocks a record sorts it into its problem and every write that lets it go
/// clears it, a flow's problems are counted from the problem index, a problem's records are released together with each
/// one named under the release, and records blocked before the ledger kept problems are sorted by the backfill.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class RecordProblemTests : IAsyncLifetime, IDisposable
{
    /// <summary>The hold of a record whose wellbore the cache does not hold: one problem, whatever the wellbore.</summary>
    private static string MissingWellbore(string wellbore) => $"osdu.data.WellboreID: '{wellbore}' matches no cached Wellbore, and the entry is required";

    private const string EmptyTag = "osdu.tags.Tag4: dataset.tag4 is empty, and the entry is required";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("problem-flow");
    private readonly Guid _other = FlowId.Of("problem-flow-other");

    private OsduLedger Ledger => _db.Ledger(_clock);

    public Task InitializeAsync() => Ledger.RegisterAsync(_flow, _other);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _db.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private RecordState Held(string sourceKey, string error, string file = "wells.csv", Guid? flow = null) => new()
    {
        DeliveryKey = DeliveryKey.Derive("test", [sourceKey]),
        FlowId = flow ?? _flow,
        SourceKey = sourceKey,
        MappingName = "Thing",
        LastSubmissionId = Guid.NewGuid(),
        PendingSourceFileName = file,
        PendingSourceRowNumber = 1,
        LastError = error,
    };

    private RecordState Pending(string sourceKey, Guid submission) => new()
    {
        DeliveryKey = DeliveryKey.Derive("test", [sourceKey]),
        FlowId = _flow,
        SourceKey = sourceKey,
        MappingName = "Thing",
        TargetId = "dev:master-data--Thing:" + sourceKey,
        LastSubmissionId = submission,
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingSourceFingerprint = "fp",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
        PendingSourceFileName = "wells.csv",
    };

    [Fact]
    public async Task Holds_are_sorted_into_their_problems_and_a_flow_s_problems_are_counted_the_largest_first()
    {
        var wellbores = Enumerable.Range(1, 4).Select(i => Held($"W-{i}", MissingWellbore($"WB-{i}"), i <= 3 ? "wells.csv" : "late.csv")).ToList();
        var tags = Enumerable.Range(1, 2).Select(i => Held($"T-{i}", EmptyTag)).ToList();
        await Ledger.MarkHeldAsync(_flow, wellbores);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Ledger.MarkHeldAsync(_flow, tags);
        await Ledger.MarkHeldAsync(_other, [Held("W-1", MissingWellbore("WB-1"), flow: _other)]);

        var listing = await Ledger.ListProblemsAsync(_flow, 10);

        Assert.Equal((2L, 6L, 0L), (listing.TotalProblems, listing.TotalRecords, listing.Unsorted));
        var missing = listing.Problems[0];
        Assert.Equal("osdu.data.WellboreID: '<value>' matches no cached Wellbore, and the entry is required", missing.Pattern);
        Assert.Equal((4L, 4L, 0L), (missing.Records, missing.Held, missing.Failed));
        Assert.Equal(ProblemSignature.Of(wellbores[0].LastError), missing.Problem);
        Assert.Contains(missing.Example!.SourceKey, wellbores.Select(w => w.SourceKey));
        Assert.Equal(missing.Problem, missing.Example.ProblemHash);
        var tag = listing.Problems[1];
        Assert.Equal((EmptyTag, 2L), (tag.Pattern, tag.Records));
        Assert.Equal(Now, tag.NewestUtc);
        Assert.Equal(Now.AddMinutes(-1), missing.NewestUtc);

        // One listing names fewer than there are, and still counts them all.
        var first = await Ledger.ListProblemsAsync(_flow, 1);
        Assert.Equal((1, 2L, 6L), (first.Problems.Count, first.TotalProblems, first.TotalRecords));

        // A problem's records, its count alone, and its files, the most records first.
        var records = await Ledger.ListAsync(_flow, new RecordQuery { Problem = missing.Problem, Max = 10 });
        Assert.Equal(wellbores.Select(w => w.SourceKey).Order(), records.Select(r => r.SourceKey).Order());
        Assert.Equal(4, (await Ledger.CountAsync(_flow, new RecordQuery { Problem = missing.Problem }, 100)).Count);
        Assert.Equal(4, (await Ledger.GetProblemAsync(_flow, missing.Problem))!.Records);
        Assert.Null(await Ledger.GetProblemAsync(_flow, 42));
        Assert.Equal(
            [new ProblemFile("wells.csv", 3), new ProblemFile("late.csv", 1)],
            await Ledger.ListProblemFilesAsync(_flow, missing.Problem, 10));

        // The other flow's record of the same row and problem is its own.
        Assert.Equal(1, (await Ledger.ListProblemsAsync(_other, 10)).TotalRecords);
    }

    [Fact]
    public async Task A_problem_whose_records_name_one_value_is_a_set_error_and_one_whose_records_differ_is_row_errors()
    {
        // A prepared set with one bad unit in every row, and wellbores the cache lacks, each row its own.
        var units = Enumerable.Range(1, 3).Select(i => Held($"U-{i}", "osdu.data.CurveUnit: 'gAPI ' is not a unit the cache lists")).ToList();
        await Ledger.MarkHeldAsync(_flow, units);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var wellbores = Enumerable.Range(1, 3).Select(i => Held($"W-{i}", MissingWellbore($"WB-{i}"))).ToList();
        await Ledger.MarkHeldAsync(_flow, wellbores);

        var listing = await Ledger.ListProblemsAsync(_flow, 10);
        var unit = Assert.Single(listing.Problems, p => p.Pattern.Contains("CurveUnit", StringComparison.Ordinal));
        Assert.Equal(ProblemShape.Set, unit.Shape);
        Assert.Equal(["gAPI "], unit.Values);
        // A set error reads as its records say it; row errors keep the place each row names its own value.
        Assert.Equal("osdu.data.CurveUnit: 'gAPI ' is not a unit the cache lists", unit.Shared);
        var missing = Assert.Single(listing.Problems, p => p.Pattern.Contains("WellboreID", StringComparison.Ordinal));
        Assert.Equal(ProblemShape.Rows, missing.Shape);
        Assert.Equal(missing.Pattern, missing.Shared);

        // Samples spread across the problem, newest first: every record of a three-record problem, each once.
        var samples = await Ledger.ListProblemSamplesAsync(_flow, missing.Problem, 5);
        Assert.Equal(wellbores.Select(w => w.DeliveryKey.Value).Order(), samples.Select(s => s.DeliveryKey.Value).Order());
        Assert.Equal(ProblemShape.Rows, ProblemSignature.ShapeOf(samples.Select(s => s.LastError)));
        var sampled = missing.Sampled(samples);
        Assert.Equal((ProblemShape.Rows, missing.Pattern), (sampled.Shape, sampled.Shared));
        Assert.Equal(missing, missing.Sampled([]));
        Assert.Equal(2, (await Ledger.ListProblemSamplesAsync(_flow, unit.Problem, 2)).Count);
        Assert.Empty(await Ledger.ListProblemSamplesAsync(_flow, 42, 5));
    }

    [Theory]
    [InlineData(0L, 5, new long[0])]
    [InlineData(1L, 5, new long[] { 1 })]
    [InlineData(3L, 5, new long[] { 1, 2, 3 })]
    [InlineData(1_000_000L, 5, new long[] { 1, 250_001, 500_001, 750_000, 1_000_000 })]
    [InlineData(10L, 1, new long[] { 1 })]
    public void Samples_sit_at_the_newest_the_oldest_and_even_steps_between(long records, int count, long[] expected)
        => Assert.Equal(expected, OsduLedger.Positions(records, count));

    [Fact]
    public async Task A_worker_s_failures_are_sorted_as_they_are_applied_and_a_delivery_has_no_problem()
    {
        var submission = Guid.NewGuid();
        var records = Enumerable.Range(1, 3).Select(i => Pending($"F-{i}", submission)).ToList();
        await Ledger.UpsertPendingAsync(_flow, records);
        await Ledger.ClaimAsync(_flow, submission, "w", 10, TimeSpan.FromMinutes(5), Now);

        static string Refused(int i) => $"HTTP 503 ServiceUnavailable from PUT https://osdu.example.com/api/storage/v2/records (correlation-id {Guid.NewGuid()}): try {i} of 5";
        await Ledger.CompleteManyAsync(_flow, [
            Completion(records[0].DeliveryKey, RecordStatus.Failed, Refused(1)),
            Completion(records[1].DeliveryKey, RecordStatus.Failed, Refused(2)),
            Completion(records[2].DeliveryKey, RecordStatus.Delivered, null),
        ]);

        var listing = await Ledger.ListProblemsAsync(_flow, 10);
        var failed = Assert.Single(listing.Problems);
        Assert.Equal((2L, 0L, 2L), (failed.Records, failed.Held, failed.Failed));
        Assert.Equal("HTTP <n> ServiceUnavailable from PUT https://osdu.example.com/api/storage/v2/records (correlation-id <id>): try <n> of <n>", failed.Pattern);
        Assert.Null((await Ledger.GetRecordAsync(_flow, records[2].DeliveryKey))!.ProblemHash);
    }

    [Fact]
    public async Task Writes_that_let_a_record_go_clear_its_problem()
    {
        var staged = Held("S-1", EmptyTag);
        var removed = Held("R-1", EmptyTag);
        await Ledger.MarkHeldAsync(_flow, [staged, removed]);
        Assert.All(
            [await Ledger.GetRecordAsync(_flow, staged.DeliveryKey), await Ledger.GetRecordAsync(_flow, removed.DeliveryKey)],
            r => Assert.Equal(ProblemSignature.Of(EmptyTag), r!.ProblemHash));

        // Its source changed and it rendered: new work, and no problem.
        await Ledger.UpsertPendingAsync(_flow, [Pending("S-1", Guid.NewGuid())]);
        Assert.Null((await Ledger.GetRecordAsync(_flow, staged.DeliveryKey))!.ProblemHash);

        // Removed by an operator: no longer blocked by its problem, and no problem to fix.
        await Ledger.MarkRemovedAsync(_flow, [removed.DeliveryKey], RemovalScope.Record, "user:test", Now);
        var gone = await Ledger.GetRecordAsync(_flow, removed.DeliveryKey);
        Assert.Equal((RecordStatus.Deleted, false, (long?)null), (gone!.Status, gone.Blocked, gone.ProblemHash));
        Assert.Equal(0, (await Ledger.ListProblemsAsync(_flow, 10)).TotalRecords);
    }

    [Fact]
    public async Task A_problem_s_records_are_released_a_slice_at_a_time_each_named_under_the_release()
    {
        var sliced = new OsduLedger(_db.CreateDbContext, _clock) { WriteSlice = 2 };
        var wellbores = Enumerable.Range(1, 5).Select(i => Held($"W-{i}", MissingWellbore($"WB-{i}"))).ToList();
        var tags = Enumerable.Range(1, 2).Select(i => Held($"T-{i}", EmptyTag)).ToList();
        await sliced.MarkHeldAsync(_flow, [.. wellbores, .. tags]);
        var problem = ProblemSignature.Of(wellbores[0].LastError);
        var release = await sliced.StartActivityAsync(new ActivityRecord { FlowId = _flow, FlowName = "problem-flow", Kind = "release", Actor = "user:test", StartedUtc = Now });

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(5, await sliced.ReleaseAsync(_flow, ReleaseSelection.OfProblem(problem), release.ActivityId, Now));

        foreach (var wellbore in wellbores)
        {
            var released = await sliced.GetRecordAsync(_flow, wellbore.DeliveryKey);
            Assert.Equal((RecordStatus.Held, false, (long?)null), (released!.Status, released.Blocked, released.ProblemHash));
            Assert.Equal(Now, released.PlanRequestedUtc);

            // The record's own history holds the release that reached it, though it named the problem rather than the record.
            Assert.Contains(await sliced.ListActivitiesAsync(new ActivityQuery { FlowId = _flow, DeliveryKey = wellbore.DeliveryKey.Value }), a => a.ActivityId == release.ActivityId);
        }

        foreach (var tag in tags)
        {
            Assert.Empty(await sliced.ListActivitiesAsync(new ActivityQuery { FlowId = _flow, DeliveryKey = tag.DeliveryKey.Value }));
        }

        var left = await sliced.ListProblemsAsync(_flow, 10);
        Assert.Equal((1L, 2L, ProblemSignature.Of(EmptyTag)), (left.TotalProblems, left.TotalRecords, left.Problems.Single().Problem));
        await using var db = _db.CreateDbContext();
        Assert.Equal(5, await db.DeliveryActivityRecords.CountAsync(l => l.ActivityId == release.ActivityId));

        // Released again, a problem with no records releases nothing.
        Assert.Equal(0, await sliced.ReleaseAsync(_flow, ReleaseSelection.OfProblem(problem), release.ActivityId, Now));
    }

    [Fact]
    public async Task A_release_of_every_blocked_record_walks_each_state_and_names_each_record_released()
    {
        var sliced = new OsduLedger(_db.CreateDbContext, _clock) { WriteSlice = 2 };
        var held = Enumerable.Range(1, 3).Select(i => Held($"H-{i}", EmptyTag)).ToList();
        await sliced.MarkHeldAsync(_flow, held);
        var removed = Held("D-1", EmptyTag);
        await sliced.MarkHeldAsync(_flow, [removed]);
        await sliced.MarkRemovedAsync(_flow, [removed.DeliveryKey], RemovalScope.Record, "user:test", Now);
        var release = await sliced.StartActivityAsync(new ActivityRecord { FlowId = _flow, FlowName = "problem-flow", Kind = "release", Actor = "user:test", StartedUtc = Now });

        // The removed record is not blocked, so a release of every blocked record does not reach it.
        Assert.Equal(3, await sliced.ReleaseAsync(_flow, ReleaseSelection.EveryBlocked, release.ActivityId, Now));
        Assert.Equal(0, await sliced.ReleaseAsync(_flow, ReleaseSelection.EveryBlocked, release.ActivityId, Now));
        await using var db = _db.CreateDbContext();
        Assert.Equal(3, await db.DeliveryActivityRecords.CountAsync(l => l.ActivityId == release.ActivityId));
    }

    [Fact]
    public async Task Records_blocked_before_the_ledger_kept_problems_are_sorted_by_the_backfill()
    {
        var wellbores = Enumerable.Range(1, 3).Select(i => Held($"W-{i}", MissingWellbore($"WB-{i}"))).ToList();
        var silent = Held("N-1", string.Empty) with { LastError = null };
        await Ledger.MarkHeldAsync(_flow, [.. wellbores, silent]);
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("P-1", submission)]);

        // As the migration leaves a ledger that held them: blocked, with no problem.
        await using (var db = _db.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE [osdu].[Record] SET [ProblemHash] = NULL");
        }

        var before = await Ledger.ListProblemsAsync(_flow, 10);
        Assert.Equal((0L, 4L), (before.TotalRecords, before.Unsorted));

        Assert.Equal((4, 4), await Ledger.SortProblemsAsync(2 * 4));
        Assert.Equal((0, 0), await Ledger.SortProblemsAsync(10));

        var after = await Ledger.ListProblemsAsync(_flow, 10);
        Assert.Equal((2L, 4L, 0L), (after.TotalProblems, after.TotalRecords, after.Unsorted));
        Assert.Contains(after.Problems, p => p.Pattern == ProblemSignature.NoReason && p.Records == 1);
        Assert.Null((await Ledger.GetRecordAsync(_flow, Pending("P-1", submission).DeliveryKey))!.ProblemHash);
    }

    [Fact]
    public async Task The_backfill_sorts_a_record_only_while_it_is_the_blocked_record_its_error_was_read_from()
    {
        var record = Held("C-1", EmptyTag);
        await Ledger.MarkHeldAsync(_flow, [record]);
        await using var db = _db.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE [osdu].[Record] SET [ProblemHash] = NULL");

        // The error changed between the read and the write: nothing is written, and the next page reads it again.
        var changed = new SqlServerLedgerBulk.UnsortedRecord(
            await db.DeliveryRecords.Select(r => r.PartitionId).FirstAsync(), _flow, record.DeliveryKey.Value, "an error it no longer carries");
        Assert.Equal(0, await SqlServerLedgerBulk.SortAsync(db, [(changed, ProblemSignature.Of(changed.LastError))], default));
        Assert.Equal(1L, (await Ledger.ListProblemsAsync(_flow, 10)).Unsorted);
        Assert.Equal((1, 1), await Ledger.SortProblemsAsync(10));
    }

    [Fact]
    public async Task A_record_s_requests_list_both_those_made_for_it_and_those_that_reached_it()
    {
        var record = Held("A-1", EmptyTag);
        await Ledger.MarkHeldAsync(_flow, [record]);
        var own = await Ledger.StartActivityAsync(new ActivityRecord { FlowId = _flow, FlowName = "problem-flow", Kind = "release", Actor = "user:test", StartedUtc = Now, DeliveryKey = record.DeliveryKey.Value });
        Assert.Equal(1, await Ledger.ReleaseAsync(_flow, ReleaseSelection.Named([record.DeliveryKey]), own.ActivityId, Now));

        await Ledger.MarkHeldAsync(_flow, [record]);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var every = await Ledger.StartActivityAsync(new ActivityRecord { FlowId = _flow, FlowName = "problem-flow", Kind = "release", Actor = "user:test", StartedUtc = Now });
        Assert.Equal(1, await Ledger.ReleaseAsync(_flow, ReleaseSelection.EveryBlocked, every.ActivityId, Now));

        var requests = await Ledger.ListActivitiesAsync(new ActivityQuery { FlowId = _flow, DeliveryKey = record.DeliveryKey.Value });
        Assert.Equal([every.ActivityId, own.ActivityId], requests.Select(a => a.ActivityId));
        Assert.Equal(2, await Ledger.CountActivitiesAsync(new ActivityQuery { FlowId = _flow, DeliveryKey = record.DeliveryKey.Value }));
    }

    private RecordCompletion Completion(DeliveryKey key, RecordStatus status, string? error) => new()
    {
        DeliveryKey = key,
        Status = status,
        Promote = status == RecordStatus.Delivered,
        TargetVersion = status == RecordStatus.Delivered ? 1 : null,
        Error = error,
        Attempt = new AttemptRecord
        {
            DeliveryKey = key,
            Worker = "w",
            StartedUtc = Now,
            CompletedUtc = Now,
            Outcome = status == RecordStatus.Delivered ? AttemptOutcome.Delivered : AttemptOutcome.Failed,
            Phase = "metadata",
            Error = error,
        },
    };
}
