using System.Globalization;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>
/// What the search index may not list yet (docs: osdu/docs/reference/flow/assertion.md, Records the index may not list
/// yet). OSDU indexes a change from a queue, so for a while after a delivery, a removal or a restore its index still
/// lists the records as they were, and a test judged then fails on records that are fine. A run reads once, for each
/// settle window its tests give the index, what this module's delivery ledgers changed in OSDU in the partition within
/// it; a test that reads an entity type changed within its window is skipped, saying what changed, when, and from when
/// a run judges it.
/// </summary>
public sealed class IndexSettling
{
    private readonly IReadOnlyDictionary<int, IReadOnlyList<RecentOsduChange>> _changes;

    private IndexSettling(IReadOnlyDictionary<int, IReadOnlyList<RecentOsduChange>> changes)
    {
        _changes = changes;
    }

    /// <summary>
    /// Reads, for each settle window <paramref name="tests"/> give the index (a window of 0 asks for nothing), what the delivery
    /// ledgers of <paramref name="partition"/> changed in OSDU within it before <paramref name="nowUtc"/>.
    /// </summary>
    public static async Task<IndexSettling> ReadAsync(ILedgerRemovals ledger, string partition, IEnumerable<AssertionTest> tests, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(tests);
        var changes = new Dictionary<int, IReadOnlyList<RecentOsduChange>>();
        foreach (var window in tests.Select(t => t.IndexSettleSeconds).Where(s => s > 0).Distinct())
        {
            changes[window] = await ledger.RecentOsduChangesAsync(partition, nowUtc.AddSeconds(-window), ct).ConfigureAwait(false);
        }

        return new IndexSettling(changes);
    }

    /// <summary>
    /// Why <paramref name="test"/> is not judged now: the changes to the entity types it reads within its settle window, the
    /// latest of them, and when the window closes; null when nothing it reads changed within it, or it gives the index no time.
    /// </summary>
    public string? Why(AssertionTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (!_changes.TryGetValue(test.IndexSettleSeconds, out var changes) || changes.Count == 0)
        {
            return null;
        }

        var types = EntityTypes(test);
        var touching = changes
            .Where(c => c.SampleTargetId is { } id && DdmsRouting.EntityTypeOf(id) is { } type && types.Contains(type))
            .ToList();
        if (touching.Count == 0)
        {
            return null;
        }

        var latest = touching.Max(c => c.LatestUtc);
        var judged = latest.AddSeconds(test.IndexSettleSeconds);
        var named = touching.Select(c => DdmsRouting.EntityTypeOf(c.SampleTargetId!)!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"The search index may not list every change to {string.Join(", ", named)} yet: within the last {test.IndexSettleSeconds} s (indexSettleSeconds), {string.Join("; ", touching.Select(Describe))}, the latest at {Stamp(latest)}. The test was skipped rather than judged on what the index lists so far; a run from {Stamp(judged)} judges it.");
    }

    /// <summary>The entity types a test reads: its kind's, or for a test that names records by id, theirs.</summary>
    private static HashSet<string> EntityTypes(AssertionTest test)
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (test.ByIds)
        {
            foreach (var id in test.Ids)
            {
                if (DdmsRouting.EntityTypeOf(id) is { } type)
                {
                    types.Add(type);
                }
            }
        }
        else if (OsduKind.EntityType(test.Kind) is { } type)
        {
            types.Add(type);
        }

        return types;
    }

    /// <summary>What one ledger changed, in the words a skipped test's reason uses.</summary>
    private static string Describe(RecentOsduChange change)
    {
        var parts = new List<string>(3);
        if (change.Written > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"wrote {change.Written} record(s)"));
        }

        if (change.Removed > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"took {change.Removed} out of OSDU or put back an earlier version"));
        }

        if (change.Deleted > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"deleted {change.Deleted} from its ledger"));
        }

        return $"{change.LedgerName} {string.Join(", ", parts)}";
    }

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
