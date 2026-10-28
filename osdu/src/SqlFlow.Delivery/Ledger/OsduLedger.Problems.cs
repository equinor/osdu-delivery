using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The ledger's problems (osdu/docs/reference/concepts/record-lifecycle.md, Issues): what keeps a flow's records
/// blocked, grouped by the problem each record's error names (<see cref="ProblemSignature"/>), counted from the records
/// through the problem index, and the signing of the records blocked before the ledger kept problems.
/// </summary>
public sealed partial class OsduLedger
{
    /// <summary>The most problems one listing names; the rest are counted, not named.</summary>
    public const int MaxProblems = 500;

    /// <summary>The most files one problem's listing names.</summary>
    public const int MaxProblemFiles = 200;

    /// <summary>The most samples one problem names.</summary>
    public const int MaxProblemSamples = 20;

    public async Task<ProblemListing> ListProblemsAsync(Guid flowId, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return new ProblemListing([], 0, 0, 0);
        }

        var take = Math.Clamp(max, 1, MaxProblems);
        var (counts, examples, unsorted) = await ReadAsync(
            async db =>
            {
                var counts = await SqlServerLedgerBulk.ProblemsAsync(db, partition, flowId, null, take, ct).ConfigureAwait(false);
                var examples = await ExamplesAsync(db, partition, flowId, counts.Select(c => c.Problem).ToList(), ct).ConfigureAwait(false);
                var unsorted = await SqlServerLedgerBulk.UnsortedCountAsync(db, partition, flowId, ct).ConfigureAwait(false);
                return (counts, examples, unsorted);
            },
            ct).ConfigureAwait(false);

        var name = await PartitionNameAsync(partition, ct).ConfigureAwait(false);
        var problems = counts
            .Select(c => Group(c, examples.TryGetValue(c.Problem, out var ends) ? ends : null, name))
            .OfType<ProblemGroup>()
            .ToList();
        var first = counts.Count > 0 ? counts[0] : null;
        return new ProblemListing(problems, first?.Problems ?? 0, first?.Total ?? 0, unsorted);
    }

    public async Task<ProblemGroup?> GetProblemAsync(Guid flowId, long problem, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var (counts, examples) = await ReadAsync(
            async db =>
            {
                var counts = await SqlServerLedgerBulk.ProblemsAsync(db, partition, flowId, problem, 1, ct).ConfigureAwait(false);
                var examples = await ExamplesAsync(db, partition, flowId, counts.Select(c => c.Problem).ToList(), ct).ConfigureAwait(false);
                return (counts, examples);
            },
            ct).ConfigureAwait(false);
        if (counts.Count == 0)
        {
            return null;
        }

        return Group(counts[0], examples.TryGetValue(problem, out var ends) ? ends : null, await PartitionNameAsync(partition, ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<ProblemFile>> ListProblemFilesAsync(Guid flowId, long problem, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        return await ReadAsync(
            db => SqlServerLedgerBulk.ProblemFilesAsync(db, partition, flowId, problem, Math.Clamp(max, 1, MaxProblemFiles), ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sorts the next <paramref name="max"/> blocked records the ledger has not sorted into a problem, across every
    /// ledger: each record's problem is read from its stored error, exactly as a hold or a failure of it would be sorted
    /// now, and written only while the record is still the blocked record that error was read from. Returns how many
    /// records it read and how many it sorted; a pass reads pages until one is empty, or until a page sorts nothing, which
    /// only records changing under it can cause and the next pass settles.
    /// </summary>
    public async Task<(int Read, int Sorted)> SortProblemsAsync(int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        await using var db = Open();
        var page = await RetryDeadlockAsync(() => SqlServerLedgerBulk.UnsortedPageAsync(db, Math.Clamp(max, 1, 1000), ct), ct).ConfigureAwait(false);
        if (page.Count == 0)
        {
            return (0, 0);
        }

        var sorted = page.Select(r => (Record: r, Problem: ProblemSignature.Of(r.LastError))).ToList();
        var written = await RetryDeadlockAsync(() => SqlServerLedgerBulk.SortAsync(db, sorted, ct), ct).ConfigureAwait(false);
        return (page.Count, written);
    }

    public async Task<IReadOnlyList<RecordState>> ListProblemSamplesAsync(Guid flowId, long problem, int count, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(count, 1, MaxProblemSamples);
        var samples = await ReadAsync(
            async db =>
            {
                var counts = await SqlServerLedgerBulk.ProblemsAsync(db, partition, flowId, problem, 1, ct).ConfigureAwait(false);
                if (counts.Count == 0)
                {
                    return [];
                }

                var keys = await SqlServerLedgerBulk.ProblemSamplesAsync(db, partition, flowId, problem, Positions(counts[0].Records, take), ct).ConfigureAwait(false);
                var rows = await db.DeliveryRecords.AsNoTracking()
                    .Where(r => r.PartitionId == partition && r.FlowId == flowId && keys.Contains(r.DeliveryKey))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                // In the order the positions name them, newest first; a record whose problem moved on meanwhile is left out.
                var order = keys.Select((key, index) => (key, index)).ToDictionary(k => k.key, k => k.index);
                return rows.Where(r => r.ProblemHash == problem).OrderBy(r => order[r.DeliveryKey]).Select(ToState).ToList();
            },
            ct).ConfigureAwait(false);
        var name = await PartitionNameAsync(partition, ct).ConfigureAwait(false);
        return samples.Select(s => s with { Partition = name }).ToList();
    }

    /// <summary>
    /// Where <paramref name="count"/> samples of <paramref name="records"/> sit in a problem's order, 1 the newest: the first,
    /// the last and the ones between at even steps, each once.
    /// </summary>
    internal static IReadOnlyList<long> Positions(long records, int count)
    {
        if (records <= 0 || count <= 0)
        {
            return [];
        }

        if (count == 1 || records == 1)
        {
            return [1];
        }

        var positions = new SortedSet<long>();
        for (var i = 0; i < count; i++)
        {
            positions.Add(1 + (long)Math.Round(i * (records - 1) / (double)(count - 1), MidpointRounding.AwayFromZero));
        }

        return positions.ToList();
    }

    /// <summary>The newest and the oldest record of each problem named, read whole.</summary>
    private static async Task<IReadOnlyDictionary<long, (RecordState Newest, RecordState Oldest)>> ExamplesAsync(
        Data.OsduDbContext db, short partition, Guid flowId, IReadOnlyCollection<long> problems, CancellationToken ct)
    {
        var ends = await SqlServerLedgerBulk.ProblemExamplesAsync(db, partition, flowId, problems, ct).ConfigureAwait(false);
        if (ends.Count == 0)
        {
            return new Dictionary<long, (RecordState, RecordState)>();
        }

        var keys = ends.SelectMany(e => new[] { e.Newest, e.Oldest }).Distinct().ToArray();
        var rows = (await db.DeliveryRecords.AsNoTracking()
            .Where(r => r.PartitionId == partition && r.FlowId == flowId && keys.Contains(r.DeliveryKey))
            .ToListAsync(ct)
            .ConfigureAwait(false))
            .ToDictionary(r => r.DeliveryKey);

        // A record whose problem moved on between the two reads is no example of the one it was found for.
        var examples = new Dictionary<long, (RecordState, RecordState)>();
        foreach (var end in ends)
        {
            if (rows.TryGetValue(end.Newest, out var newest) && newest.ProblemHash == end.Problem)
            {
                var oldest = rows.TryGetValue(end.Oldest, out var found) && found.ProblemHash == end.Problem ? found : newest;
                examples[end.Problem] = (ToState(newest), ToState(oldest));
            }
        }

        return examples;
    }

    /// <summary>
    /// A problem as a listing names it: its counts, its example, its pattern read from the example's error, and its shape
    /// and what its records share as its newest and oldest records say. A problem whose records all moved on between the
    /// count and the example's read has no example, and is left out.
    /// </summary>
    private static ProblemGroup? Group(SqlServerLedgerBulk.ProblemCount count, (RecordState Newest, RecordState Oldest)? ends, string? partition)
    {
        if (ends is not { } found)
        {
            return null;
        }

        var pattern = ProblemSignature.Pattern(found.Newest.LastError);
        string?[] errors = [found.Newest.LastError, found.Oldest.LastError];
        return new ProblemGroup
        {
            Problem = count.Problem,
            Pattern = pattern,
            Records = count.Records,
            Held = count.Held,
            Failed = count.Failed,
            OldestUtc = count.OldestUtc,
            NewestUtc = count.NewestUtc,
            Example = found.Newest with { Partition = partition },
            Shape = ProblemSignature.ShapeOf(errors),
            Values = ProblemSignature.Values(found.Newest.LastError),
            Shared = ProblemSignature.Shared(pattern, errors),
        };
    }
}
