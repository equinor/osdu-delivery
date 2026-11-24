using System.Runtime.CompilerServices;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

// How a removal and deleting the ledger work through many records (osdu/docs/reference/concepts/removal-and-reversal.md,
// How a removal works through many records): a page of records at a time, as many pages at once as the flow sends records
// at once (parallelism.recordsAtOnce). Pages hold disjoint records, each under a removal hold of its own, and each takes
// its records in the order of the ledger's key, so two pages never wait on one another. OSDU's rate limit is the run's
// own and holds across them.
public sealed partial class FlowRuntime
{
    /// <summary>
    /// Serialises what a removal tells the listeners: its pages run at once, and a listener hears one event at a time, as it
    /// does from a worker's journal.
    /// </summary>
    private readonly SemaphoreSlim _announcing = new(1, 1);

    /// <summary>How many pages of a removal run at once: the records the flow sends to OSDU at the same time, at least one.</summary>
    internal int RemovalLanes => Math.Max(1, Flow.Parallelism.RecordsAtOnce);

    /// <summary>The fewest records a page of a removal holds (<see cref="RemovalLimits.SmallestPage"/>); tests lower it.</summary>
    internal int SmallestRemovalPage { get; set; } = RemovalLimits.SmallestPage;

    /// <summary>
    /// The records a page of a removal of <paramref name="records"/> holds, with <paramref name="lanes"/> pages at once: as
    /// many as share the records out over every lane, never more than a storage call takes at once
    /// (<see cref="RemovalLimits.Chunk"/>) and never fewer than <paramref name="smallest"/>
    /// (<see cref="RemovalLimits.SmallestPage"/>), below which a page's own round trips to the ledger cost more than the lanes
    /// save.
    /// </summary>
    internal static int RemovalPageSize(long records, int lanes, int smallest = RemovalLimits.SmallestPage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(records);
        ArgumentOutOfRangeException.ThrowIfLessThan(lanes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(smallest, 1);
        var share = (records + lanes - 1) / lanes;
        return (int)Math.Clamp(share, Math.Min(smallest, RemovalLimits.Chunk), RemovalLimits.Chunk);
    }

    /// <summary>
    /// Runs <paramref name="page"/> over every page of <paramref name="pages"/>, <paramref name="lanes"/> at once. The first page
    /// that fails stops the rest: pages under way are cancelled (each lets its records go as its hold ends) and no page starts
    /// after it, and the failure is what this throws. A run that is cancelled stops them the same way.
    /// </summary>
    private static Task ForEachPageAsync<T>(IAsyncEnumerable<T> pages, int lanes, Func<T, CancellationToken, Task> page, CancellationToken ct)
        => Parallel.ForEachAsync(
            pages,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, lanes), CancellationToken = ct },
            async (item, token) => await page(item, token).ConfigureAwait(false));

    /// <summary>The pages of a list of keys, in its order, each with its place among them.</summary>
    private static async IAsyncEnumerable<(int Index, DeliveryKey[] Keys)> PagesOf(IReadOnlyList<DeliveryKey> keys, int size)
    {
        var index = 0;
        foreach (var chunk in keys.Chunk(size))
        {
            yield return (index++, chunk);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Every record of the flow's ledger, <paramref name="size"/> at a time in the order of its key: each page read when a lane
    /// asks for it, after the last key of the page before, so records a page under way marks removed never move the walk.
    /// </summary>
    private async IAsyncEnumerable<IReadOnlyList<DeliveryKey>> LedgerPagesAsync(ILedgerQueries ledger, int size, [EnumeratorCancellation] CancellationToken ct = default)
    {
        DeliveryKey? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ledger.ListRecordsAsync(Flow.Id, after, size, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                yield break;
            }

            after = page[^1].DeliveryKey;
            yield return page.Select(r => r.DeliveryKey).ToList();
            if (page.Count < size)
            {
                yield break;
            }
        }
    }

    /// <summary>Tells the listeners of <paramref name="evt"/>, one event at a time however many pages announce at once.</summary>
    private async Task AnnounceAsync(DeliveryEvent evt, CancellationToken ct)
    {
        await _announcing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _context.Listener.OnEventAsync(evt, ct).ConfigureAwait(false);
        }
        finally
        {
            _announcing.Release();
        }
    }
}
