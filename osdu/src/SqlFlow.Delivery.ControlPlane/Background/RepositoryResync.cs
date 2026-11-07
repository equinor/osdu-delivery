using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Makes every enabled repository source due and wakes the sync loop, so the sync that describes each registry-driven
/// flow in the partitions the registry holds starts at once, with the full lineage recompute "sync now" asks for. What a
/// change of the partition registry asks for, whoever made it. It recomputes, it never moves: a repository that syncs only
/// on request is synced again at the commit it holds, so registering a partition never brings in a merge that no trigger
/// request or person asked for.
/// </summary>
internal static class RepositoryResync
{
    public static async Task NowAsync(CatalogDbContext catalog, RepoSyncSignal syncSignal, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(syncSignal);
        var sources = await catalog.RepoSources.AsNoTracking().Where(s => s.Enabled).Select(s => s.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var source in sources)
        {
            await RepoSourceStore.ResyncAsync(catalog, source, nowUtc, ct).ConfigureAwait(false);
        }

        syncSignal.Wake();
    }
}
