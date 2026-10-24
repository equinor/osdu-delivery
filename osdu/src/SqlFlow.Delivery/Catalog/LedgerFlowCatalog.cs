using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// One flow of a repository that keeps a ledger and delivers nothing, as the repository sync parsed it: its kind, name,
/// whether it keeps a ledger per partition, and its document's hash (<see cref="DeliveryDocumentHash"/>).
/// </summary>
public sealed record RepositoryFlow(string RelativePath, string Kind, string Name, bool Partitioned, string DocumentHash);

/// <summary>
/// The read model of the flows that keep a ledger and are no delivery flows (<see cref="DeliveryLedgerFlow"/>): retrieval,
/// assertion, dimension and inventory flows, written for a whole repository at a time by the repository sync. It says which
/// of their ledgers a declared flow keeps, and keeps the hash of a flow that left, so the sync pairs a renamed flow with the
/// one it replaced by the rule every kind follows (<see cref="LedgerOwnership"/>), as the interfaces do a delivery flow's.
/// </summary>
public static class LedgerFlowCatalog
{
    /// <summary>The kinds of flow this read model describes: every kind that keeps a ledger, but delivery.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [LedgerKinds.Retrieval, LedgerKinds.Assertion, LedgerKinds.Dimension, LedgerKinds.Inventory];

    /// <summary>
    /// Makes the repository's rows describe <paramref name="flows"/>: one row per flow, added or updated, and the rows of
    /// flows the repository no longer declares kept but made inactive, with the hash they last had, except those of a file
    /// <paramref name="moves"/> says did not read or parse, which stay as they were. When <paramref name="moves"/> asks, the
    /// ledgers of a flow's other names move to the name that owns its document.
    /// </summary>
    public static async Task<CatalogSyncCounts> ReconcileAsync(
        OsduDbContext context, Guid repoId, IReadOnlyList<RepositoryFlow> flows, DateTime nowUtc, ICollection<string> warnings, LedgerMoveGuard moves, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(flows);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(moves);
        var existing = await context.DeliveryLedgerFlows.Where(d => d.RepoId == repoId).AsTracking().ToDictionaryAsync(d => d.Id, ct).ConfigureAwait(false);
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<Guid>();
        int added = 0, updated = 0, unchanged = 0;
        foreach (var flow in flows)
        {
            if (!named.TryAdd(flow.Name, flow.RelativePath))
            {
                warnings.Add($"{flow.RelativePath}: {flow.Kind} flow '{flow.Name}' is already declared by {named[flow.Name]}; the first file wins.");
                continue;
            }

            var id = IdOf(repoId, flow.Name);
            seen.Add(id);
            if (!existing.TryGetValue(id, out var row))
            {
                context.DeliveryLedgerFlows.Add(new DeliveryLedgerFlow
                {
                    Id = id,
                    RepoId = repoId,
                    FlowName = flow.Name,
                    Kind = flow.Kind,
                    Partitioned = flow.Partitioned,
                    DocumentHash = flow.DocumentHash,
                    RelativePath = flow.RelativePath,
                    Active = true,
                    FirstSeenUtc = nowUtc,
                    LastSeenUtc = nowUtc,
                });
                added++;
                continue;
            }

            if (row.FlowName == flow.Name && row.Kind == flow.Kind && row.Partitioned == flow.Partitioned && row.DocumentHash == flow.DocumentHash
                && row.RelativePath == flow.RelativePath && row.Active)
            {
                row.LastSeenUtc = nowUtc;
                unchanged++;
                continue;
            }

            // A flow that comes back after it left is found anew: of the names of one document, the one found last owns it.
            if (!row.Active)
            {
                row.FirstSeenUtc = nowUtc;
            }

            row.FlowName = flow.Name;
            row.Kind = flow.Kind;
            row.Partitioned = flow.Partitioned;
            row.DocumentHash = flow.DocumentHash;
            row.RelativePath = flow.RelativePath;
            row.Active = true;
            row.LastSeenUtc = nowUtc;
            updated++;
        }

        var removed = 0;
        foreach (var (id, row) in existing)
        {
            if (!seen.Contains(id) && row.Active && !moves.UnreadFiles.Contains(row.RelativePath))
            {
                row.Active = false;
                row.LastSeenUtc = nowUtc;
                removed++;
            }
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        if (moves.Asks)
        {
            await RequestLedgerMovesAsync(context, repoId, moves, nowUtc, warnings, ct).ConfigureAwait(false);
        }

        return new CatalogSyncCounts(added, updated, unchanged, removed);
    }

    /// <summary>The id of a flow's row: derived from the repository and the flow's name.</summary>
    public static Guid IdOf(Guid repoId, string flowName) => FlowIdentity.FromName($"ledger-flow/{repoId:N}/{flowName.ToLowerInvariant()}");

    /// <summary>
    /// Asks for the moves that keep each document's ledgers with the flow that owns it (<see cref="LedgerOwnership"/>). A flow
    /// of these kinds keeps its ledgers under its name, one per partition for a flow that works in partitions, so every name
    /// of a document is described in each partition where a name of it keeps a ledger, with the ledger it keeps or would
    /// keep there.
    /// </summary>
    private static async Task RequestLedgerMovesAsync(
        OsduDbContext context, Guid repoId, LedgerMoveGuard guard, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        var rows = await context.DeliveryLedgerFlows.AsNoTracking().Where(d => d.RepoId == repoId).ToListAsync(ct).ConfigureAwait(false);
        var documents = rows.GroupBy(r => (r.Kind, r.DocumentHash)).Where(g => g.Count() > 1 && g.Any(r => r.Active)).ToList();
        if (documents.Count == 0)
        {
            return;
        }

        var names = documents.SelectMany(d => d.Select(r => r.FlowName)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var kinds = documents.Select(d => d.Key.Kind).Distinct(StringComparer.Ordinal).ToList();
        var ledgers = await context.DeliveryLedgers.AsNoTracking()
            .Where(l => l.PartitionId != DeliveryModel.UnassignedPartition && kinds.Contains(l.Kind) && names.Contains(l.FlowName))
            .Select(l => new { l.Kind, l.FlowName, l.PartitionId })
            .ToListAsync(ct).ConfigureAwait(false);
        var partitions = await context.DeliveryLedgerPartitions.AsNoTracking().ToDictionaryAsync(p => p.PartitionId, p => p.Name, ct).ConfigureAwait(false);
        var holders = new List<LedgerHolder>();
        foreach (var document in documents)
        {
            var kept = ledgers
                .Where(l => l.Kind == document.Key.Kind && document.Any(r => string.Equals(r.FlowName, l.FlowName, StringComparison.OrdinalIgnoreCase)))
                .Select(l => partitions[l.PartitionId])
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var partition in kept)
            {
                holders.AddRange(document.Select(r => Holder(r, partition)));
            }
        }

        await LedgerOwnership.RequestMovesAsync(context, repoId, holders, (ids, token) => KeepersAsync(context, ids, token), guard, nowUtc, warnings, ct).ConfigureAwait(false);
    }

    /// <summary>The declared flows of these kinds, of any repository, that keep one of <paramref name="ledgers"/>.</summary>
    private static async Task<IReadOnlyList<LedgerHolder>> KeepersAsync(OsduDbContext context, IReadOnlyCollection<Guid> ledgers, CancellationToken ct)
    {
        var placed = await context.DeliveryLedgers.AsNoTracking()
            .Where(l => ledgers.Contains(l.FlowId))
            .Join(context.DeliveryLedgerPartitions, l => l.PartitionId, p => p.PartitionId, (l, p) => new { l.FlowId, l.Kind, l.FlowName, Partition = p.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var names = placed.Select(p => p.FlowName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var declared = await context.DeliveryLedgerFlows.AsNoTracking()
            .Where(d => d.Active && names.Contains(d.FlowName))
            .ToListAsync(ct).ConfigureAwait(false);
        return [.. placed.SelectMany(p => declared
            .Where(d => d.Kind == p.Kind && string.Equals(d.FlowName, p.FlowName, StringComparison.OrdinalIgnoreCase))
            .Select(d => Holder(d, p.Partition))
            .Where(h => h.LedgerFlowId == p.FlowId))];
    }

    private static LedgerHolder Holder(DeliveryLedgerFlow row, string partition)
    {
        var (id, name) = LedgerBindings.OwnLedger(row, partition);
        return new LedgerHolder(row.RepoId, row.Kind, row.FlowName, string.Empty, partition, row.RelativePath, row.DocumentHash, id, name, row.Active, row.FirstSeenUtc);
    }
}
