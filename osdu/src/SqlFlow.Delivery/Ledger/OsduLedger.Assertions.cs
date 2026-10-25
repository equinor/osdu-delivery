using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The report of assertion flows (osdu/docs/reference/flow/assertion.md, The report): a row per run of a flow's tests
/// in a partition, and a row per test and run with the whole result. Both are ledger tables, written under the ledger
/// identity the run registered, so a partition's reports are kept together and read in one range of it.
/// </summary>
public sealed partial class OsduLedger
{
    /// <summary>The widest detail one result keeps; a larger one is refused by the engine before it reaches here.</summary>
    public const int MaxAssertionDetail = 4 * 1024 * 1024;

    /// <summary>The most old runs one prune statement takes up at a time.</summary>
    private const int PruneRunBatch = 100;

    public async Task<AssertionRunState> StartAssertionRunAsync(AssertionRunState run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        var partition = await WritePartitionAsync(run.FlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var entity = new DeliveryAssertionRun
        {
            PartitionId = partition,
            FlowId = run.FlowId,
            FlowName = Truncate(run.FlowName, 200)!,
            RunId = run.RunId,
            Actor = Truncate(run.Actor, 200)!,
            Selection = run.Selection,
            Status = run.Status,
            Tests = run.Counts.Tests,
            Passed = run.Counts.Passed,
            Failed = run.Counts.Failed,
            Warned = run.Counts.Warned,
            Errored = run.Counts.Errored,
            Skipped = run.Counts.Skipped,
            DefinitionsHash = Truncate(run.DefinitionsHash, 64),
            StartedUtc = run.StartedUtc,
            CompletedUtc = run.CompletedUtc,
            Error = Truncate(run.Error, 4000),
        };
        db.DeliveryAssertionRuns.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return run with { AssertionRunId = entity.AssertionRunId, Partition = await PartitionNameAsync(partition, ct).ConfigureAwait(false) };
    }

    public async Task<AssertionResultState> RecordAssertionResultAsync(AssertionResultState result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Detail.Length > MaxAssertionDetail)
        {
            throw new DeliveryException(
                $"The result of test '{result.TestName}' is {result.Detail.Length} characters of detail, more than the {MaxAssertionDetail} a result keeps; the engine bounds a result before it records it.");
        }

        var partition = await WritePartitionAsync(result.FlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var entity = new DeliveryAssertionResult
        {
            PartitionId = partition,
            AssertionRunId = result.AssertionRunId,
            FlowId = result.FlowId,
            TestName = Truncate(result.TestName, 100)!,
            Kind = Truncate(result.Kind, 400)!,
            Outcome = result.Outcome,
            Severity = result.Severity,
            Matched = result.Matched,
            Evaluated = result.Evaluated,
            Sampled = result.Sampled,
            Assertions = result.Assertions,
            FailedAssertions = result.FailedAssertions,
            DefinitionHash = Truncate(result.DefinitionHash, 32)!,
            DurationMs = result.DurationMs,
            Error = Truncate(result.Error, 4000),
            Detail = result.Detail,
            StartedUtc = result.StartedUtc,
            CompletedUtc = result.CompletedUtc,
        };
        db.DeliveryAssertionResults.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return result with { ResultId = entity.ResultId };
    }

    public async Task CompleteAssertionRunAsync(long assertionRunId, string status, AssertionCounts counts, string? failure, DateTime completedUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(counts);
        await using var db = Open();
        var entity = await db.DeliveryAssertionRuns.FirstOrDefaultAsync(r => r.AssertionRunId == assertionRunId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Assertion run {assertionRunId} is not in the ledger.");
        entity.Status = status;
        entity.Tests = counts.Tests;
        entity.Passed = counts.Passed;
        entity.Failed = counts.Failed;
        entity.Warned = counts.Warned;
        entity.Errored = counts.Errored;
        entity.Skipped = counts.Skipped;
        entity.Error = Truncate(failure, 4000);
        entity.CompletedUtc = completedUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<AssertionRunState?> GetAssertionRunAsync(long assertionRunId, CancellationToken ct = default)
    {
        var entity = await ReadAsync(db => db.DeliveryAssertionRuns.AsNoTracking().FirstOrDefaultAsync(r => r.AssertionRunId == assertionRunId, ct), ct)
            .ConfigureAwait(false);
        return entity is null ? null : ToState(entity) with { Partition = await PartitionNameAsync(entity.PartitionId, ct).ConfigureAwait(false) };
    }

    public async Task<IReadOnlyList<AssertionRunState>> ListAssertionRunsAsync(Guid flowId, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var rows = await ReadAsync(
            db => db.DeliveryAssertionRuns.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.FlowId == flowId)
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.AssertionRunId)
                .Take(Math.Clamp(max, 1, 1000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var name = await PartitionNameAsync(partition, ct).ConfigureAwait(false);
        return rows.Select(r => ToState(r) with { Partition = name }).ToList();
    }

    public async Task<IReadOnlyList<AssertionResultState>> ListAssertionResultsAsync(long assertionRunId, CancellationToken ct = default)
    {
        var run = await ReadAsync(
            db => db.DeliveryAssertionRuns.AsNoTracking().Where(r => r.AssertionRunId == assertionRunId).Select(r => (short?)r.PartitionId).FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
        if (run is not { } partition)
        {
            return [];
        }

        var rows = await ReadAsync(
            db => db.DeliveryAssertionResults.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.AssertionRunId == assertionRunId)
                .OrderBy(r => r.ResultId)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task<IReadOnlyList<AssertionResultState>> LatestAssertionResultsAsync(IReadOnlyCollection<Guid> flowIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flowIds);
        var byPartition = new Dictionary<short, List<Guid>>();
        foreach (var flowId in flowIds.Distinct())
        {
            if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is { } partition)
            {
                (byPartition.TryGetValue(partition, out var ids) ? ids : byPartition[partition] = []).Add(flowId);
            }
        }

        var results = new List<AssertionResultState>();
        foreach (var (partition, ids) in byPartition)
        {
            foreach (var chunk in ids.Chunk(500))
            {
                var rows = await ReadAsync(
                    db => db.DeliveryAssertionResults.AsNoTracking()
                        .Where(r => r.PartitionId == partition && chunk.Contains(r.FlowId)
                            && r.Outcome != TestOutcomes.Skipped
                            && r.AssertionRunId == db.DeliveryAssertionResults
                                .Where(x => x.PartitionId == r.PartitionId && x.FlowId == r.FlowId && x.TestName == r.TestName && x.Outcome != TestOutcomes.Skipped)
                                .Max(x => x.AssertionRunId))
                        .Select(Summary)
                        .ToListAsync(ct),
                    ct).ConfigureAwait(false);
                results.AddRange(rows.Select(ToState));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<AssertionResultState>> AssertionHistoryAsync(Guid flowId, string? testName, int runs, bool withDetail, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(runs, 1, 500);
        var test = string.IsNullOrWhiteSpace(testName) ? null : testName.Trim();
        var rows = await ReadAsync(
            db =>
            {
                // A test's history is the runs that ran it; the flow's history is its most recent runs, whatever each ran.
                var runIds = test is null
                    ? db.DeliveryAssertionRuns.Where(r => r.PartitionId == partition && r.FlowId == flowId)
                        .OrderByDescending(r => r.AssertionRunId).Select(r => r.AssertionRunId).Take(take)
                    : db.DeliveryAssertionResults.Where(r => r.PartitionId == partition && r.FlowId == flowId && r.TestName == test)
                        .OrderByDescending(r => r.AssertionRunId).Select(r => r.AssertionRunId).Take(take);
                var query = db.DeliveryAssertionResults.AsNoTracking()
                    .Where(r => r.PartitionId == partition && r.FlowId == flowId && runIds.Contains(r.AssertionRunId));
                if (test is not null)
                {
                    query = query.Where(r => r.TestName == test);
                }

                var ordered = query.OrderByDescending(r => r.AssertionRunId).ThenBy(r => r.ResultId);
                return withDetail ? ordered.ToListAsync(ct) : ordered.Select(Summary).ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task<long> CountPrunableAssertionRunsAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        // The prune's own test, as one query: a finished run started before the cut-off none of whose results is the latest
        // of its test.
        return await db.DeliveryAssertionRuns
            .Where(r => r.StartedUtc < olderThanUtc && r.Status != AssertionRunStatus.Running
                && !db.DeliveryAssertionResults.Any(x => x.PartitionId == r.PartitionId && x.AssertionRunId == r.AssertionRunId
                    && !db.DeliveryAssertionResults.Any(b => b.PartitionId == x.PartitionId && b.FlowId == x.FlowId && b.TestName == x.TestName
                        && b.AssertionRunId > x.AssertionRunId && (b.Outcome != TestOutcomes.Skipped || x.Outcome == TestOutcomes.Skipped))))
            .LongCountAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int> PruneAssertionRunsAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        var removed = 0;
        long after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = Open();
            var runs = await db.DeliveryAssertionRuns.AsNoTracking()
                .Where(r => r.AssertionRunId > after && r.StartedUtc < olderThanUtc && r.Status != AssertionRunStatus.Running)
                .OrderBy(r => r.AssertionRunId)
                .Select(r => new { r.PartitionId, r.AssertionRunId })
                .Take(PruneRunBatch)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (runs.Count == 0)
            {
                return removed;
            }

            foreach (var run in runs)
            {
                // A result is superseded by a later result of the same test that says more: a later outcome of a test that ran,
                // or anything later of a test that was skipped. A run goes whole, once every result it holds is superseded, so a
                // report that is kept is the report as it ran, and a run holding a test's latest result stays.
                var holdsLatest = await db.DeliveryAssertionResults.AnyAsync(
                        r => r.PartitionId == run.PartitionId && r.AssertionRunId == run.AssertionRunId
                            && !db.DeliveryAssertionResults.Any(b => b.PartitionId == r.PartitionId && b.FlowId == r.FlowId && b.TestName == r.TestName
                                && b.AssertionRunId > r.AssertionRunId && (b.Outcome != TestOutcomes.Skipped || r.Outcome == TestOutcomes.Skipped)),
                        ct)
                    .ConfigureAwait(false);
                if (!holdsLatest)
                {
                    var strategy = db.Database.CreateExecutionStrategy();
                    removed += await strategy.ExecuteAsync(async () =>
                    {
                        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                        await db.DeliveryAssertionResults
                            .Where(r => r.PartitionId == run.PartitionId && r.AssertionRunId == run.AssertionRunId)
                            .ExecuteDeleteAsync(ct)
                            .ConfigureAwait(false);
                        var gone = await db.DeliveryAssertionRuns
                            .Where(r => r.PartitionId == run.PartitionId && r.AssertionRunId == run.AssertionRunId)
                            .ExecuteDeleteAsync(ct)
                            .ConfigureAwait(false);
                        await tx.CommitAsync(ct).ConfigureAwait(false);
                        return gone;
                    }).ConfigureAwait(false);
                }

                after = run.AssertionRunId;
            }
        }
    }

    /// <summary>A result without its detail: what listings across tests read.</summary>
    private static readonly System.Linq.Expressions.Expression<Func<DeliveryAssertionResult, DeliveryAssertionResult>> Summary = r => new DeliveryAssertionResult
    {
        PartitionId = r.PartitionId,
        ResultId = r.ResultId,
        AssertionRunId = r.AssertionRunId,
        FlowId = r.FlowId,
        TestName = r.TestName,
        Kind = r.Kind,
        Outcome = r.Outcome,
        Severity = r.Severity,
        Matched = r.Matched,
        Evaluated = r.Evaluated,
        Sampled = r.Sampled,
        Assertions = r.Assertions,
        FailedAssertions = r.FailedAssertions,
        DefinitionHash = r.DefinitionHash,
        DurationMs = r.DurationMs,
        Error = r.Error,
        Detail = string.Empty,
        StartedUtc = r.StartedUtc,
        CompletedUtc = r.CompletedUtc,
    };

    private static AssertionRunState ToState(DeliveryAssertionRun r) => new()
    {
        AssertionRunId = r.AssertionRunId,
        FlowId = r.FlowId,
        FlowName = r.FlowName,
        RunId = r.RunId,
        Actor = r.Actor,
        Selection = r.Selection,
        Status = r.Status,
        Counts = new AssertionCounts(r.Tests, r.Passed, r.Failed, r.Warned, r.Errored, r.Skipped),
        DefinitionsHash = r.DefinitionsHash,
        StartedUtc = DateTime.SpecifyKind(r.StartedUtc, DateTimeKind.Utc),
        CompletedUtc = r.CompletedUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
        Error = r.Error,
    };

    private static AssertionResultState ToState(DeliveryAssertionResult r) => new()
    {
        ResultId = r.ResultId,
        AssertionRunId = r.AssertionRunId,
        FlowId = r.FlowId,
        TestName = r.TestName,
        Kind = r.Kind,
        Outcome = r.Outcome,
        Severity = r.Severity,
        Matched = r.Matched,
        Evaluated = r.Evaluated,
        Sampled = r.Sampled,
        Assertions = r.Assertions,
        FailedAssertions = r.FailedAssertions,
        DefinitionHash = r.DefinitionHash,
        DurationMs = r.DurationMs,
        Error = r.Error,
        Detail = r.Detail,
        StartedUtc = DateTime.SpecifyKind(r.StartedUtc, DateTimeKind.Utc),
        CompletedUtc = DateTime.SpecifyKind(r.CompletedUtc, DateTimeKind.Utc),
    };
}
