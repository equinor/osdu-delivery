using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// One delivery flow document of a repository, parsed, with the path it was read from; <paramref name="Active"/> is false
/// for the catalog copy of a flow the repository no longer declares.
/// </summary>
public sealed record RepositorySource(string RelativePath, SourceDefinition Source, bool Active = true);

/// <summary>
/// A pipeline's interface as the read model knows it: the flow, the interface (empty for the single form), the partition
/// whose ledger it is (empty for a flow that names no partitions) and that ledger.
/// </summary>
public sealed record InterfaceLocation(Guid RepoId, string FlowName, string Interface, Guid LedgerFlowId, string LedgerName, bool Active, string Partition = "");

/// <summary>
/// The read model of sources and their interfaces (<see cref="DeliveryInterface"/>): written for a whole repository at a
/// time by the repository sync, and read wherever a ledger identity needs its pipeline or a pipeline its interfaces. Every
/// lookup of a delivery flow's ledger identity goes through here, so a source's interfaces, an adopted ledger and a flow in
/// the single form are found the same way.
/// </summary>
public static class DeliveryInterfaceCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Makes the repository's rows describe <paramref name="sources"/>: one row per interface (per interface and partition
    /// for a source that names its partitions), added or updated, and the rows of interfaces the sources no longer declare
    /// kept but made inactive.
    /// <paramref name="kinds"/> gives the OSDU kind of each mapping reference the repository holds valid. A ledger identity
    /// kept by an interface of another flow is reported: two flows delivering the same records would take turns with them.
    /// So is a flow renamed in its file while the ledger of its old name holds the OSDU ids its records claimed.
    /// </summary>
    public static async Task<CatalogSyncCounts> ReconcileAsync(
        OsduDbContext context, Guid repoId, IReadOnlyList<RepositorySource> sources, IReadOnlyDictionary<string, string> kinds,
        DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(warnings);
        var existing = await context.DeliveryInterfaces.Where(i => i.RepoId == repoId).AsTracking().ToDictionaryAsync(i => i.Id, ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        var flows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int added = 0, updated = 0, unchanged = 0;

        // A source that leaves its partitions to the registry is described in every registered partition, each with the
        // ledger it keeps there; a partition registered later is described at the next sync.
        var registry = sources.Any(s => s.Source.FollowsRegistry)
            ? await DeliveryPartitionRegistry.ReadAsync(context, ct).ConfigureAwait(false)
            : RegisteredPartitions.None;

        foreach (var (relative, source, active) in sources)
        {
            if (!flows.TryAdd(source.Name, relative))
            {
                warnings.Add($"{relative}: delivery flow '{source.Name}' is already declared by {flows[source.Name]}; the first file wins.");
                continue;
            }

            var ordinals = source.Interfaces.Select((flow, ordinal) => (flow.Interface ?? string.Empty, ordinal))
                .ToDictionary(i => i.Item1, i => i.ordinal, StringComparer.OrdinalIgnoreCase);
            foreach (var flow in source.EveryLedger(registry))
            {
                var name = flow.Interface ?? string.Empty;
                var partition = flow.Partition ?? string.Empty;
                var id = FlowIdentity.FromName(
                    $"delivery-interface/{repoId:N}/{source.Name.ToLowerInvariant()}/{name.ToLowerInvariant()}"
                    + (partition.Length == 0 ? string.Empty : $"@{partition.ToLowerInvariant()}"));
                seen.Add(id);
                var wanted = new DeliveryInterface
                {
                    Id = id,
                    RepoId = repoId,
                    FlowName = source.Name,
                    Interface = name,
                    Partition = partition,
                    TargetPartition = Ordering.OrderDeliveries.PartitionOf(flow) ?? string.Empty,
                    Ordinal = ordinals[name],
                    LedgerFlowId = flow.Id,
                    LedgerName = flow.LedgerName,
                    Route = DeliveryProtocols.Name(flow.Target.Protocol),
                    RouteReason = Clip(flow.RouteReason, 1000),
                    MappingReference = flow.Render.Mapping,
                    // What the partition's retention keeps for it: a pinned version's records; the current version is always kept.
                    CacheVersion = flow.Render.CacheVersion.Equals(FlowRender.CurrentCacheVersion, StringComparison.OrdinalIgnoreCase)
                        ? FlowRender.CurrentCacheVersion
                        : Clip(flow.Render.CacheVersion, 64),
                    Kind = kinds.GetValueOrDefault(flow.Render.Mapping, string.Empty),
                    RecordObject = flow.Source.Record.Object,
                    AfterJson = JsonSerializer.Serialize(flow.After, Json),
                    RelativePath = relative,
                    Active = active,
                };

                if (!existing.TryGetValue(id, out var row))
                {
                    wanted.FirstSeenUtc = nowUtc;
                    wanted.LastSeenUtc = nowUtc;
                    context.DeliveryInterfaces.Add(wanted);
                    added++;
                    continue;
                }

                if (Same(row, wanted))
                {
                    row.LastSeenUtc = nowUtc;
                    unchanged++;
                    continue;
                }

                row.FlowName = wanted.FlowName;
                row.Interface = wanted.Interface;
                row.Partition = wanted.Partition;
                row.Ordinal = wanted.Ordinal;
                row.LedgerFlowId = wanted.LedgerFlowId;
                row.LedgerName = wanted.LedgerName;
                row.Route = wanted.Route;
                row.RouteReason = wanted.RouteReason;
                row.MappingReference = wanted.MappingReference;
                row.CacheVersion = wanted.CacheVersion;
                row.Kind = wanted.Kind;
                row.RecordObject = wanted.RecordObject;
                row.AfterJson = wanted.AfterJson;
                row.TargetPartition = wanted.TargetPartition;
                row.RelativePath = wanted.RelativePath;
                row.Active = wanted.Active;
                row.LastSeenUtc = nowUtc;
                updated++;
            }
        }

        var removed = 0;
        foreach (var (id, row) in existing)
        {
            if (!seen.Contains(id) && row.Active)
            {
                // The records it delivered still lead to their flow; the row just stops answering first.
                row.Active = false;
                row.LastSeenUtc = nowUtc;
                removed++;
            }
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        await ReportSharedLedgersAsync(context, repoId, warnings, ct).ConfigureAwait(false);
        await ReportRenamedFlowsAsync(context, repoId, warnings, ct).ConfigureAwait(false);
        await ReportPrunedPinsAsync(context, repoId, warnings, ct).ConfigureAwait(false);
        return new CatalogSyncCounts(added, updated, unchanged, removed);
    }

    /// <summary>
    /// Warns about every interface of this repository that pins a version of a cache whose records the cache's retention
    /// has pruned: a run of it fails, since nothing can render against the version, until it pins one the cache still holds.
    /// A version a pin names before it is pruned is kept from the next refresh on, so this is a pin added too late.
    /// </summary>
    private static async Task ReportPrunedPinsAsync(OsduDbContext context, Guid repoId, ICollection<string> warnings, CancellationToken ct)
    {
        var pins = await context.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.RepoId == repoId && i.Active && i.CacheVersion != null && i.CacheVersion != FlowRender.CurrentCacheVersion)
            .Select(i => new { i.FlowName, i.Interface, i.Partition, i.RelativePath, Version = i.CacheVersion! })
            .ToListAsync(ct).ConfigureAwait(false);
        if (pins.Count == 0)
        {
            return;
        }

        var labels = pins.Select(p => p.Version).Distinct(StringComparer.Ordinal).ToList();
        var versions = await context.DeliveryCacheVersions.AsNoTracking()
            .Where(v => labels.Contains(v.Version))
            .Select(v => new { v.Scope, v.Version, v.PrunedUtc })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var pin in pins)
        {
            // A flow whose partition is its header's may resolve to any partition holding the label; it is warned about only
            // when every one of them has pruned it.
            var held = versions
                .Where(v => v.Version == pin.Version && (pin.Partition.Length == 0 || string.Equals(v.Scope, pin.Partition, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (held.Count > 0 && held.All(v => v.PrunedUtc is not null))
            {
                var partition = pin.Partition.Length == 0 ? string.Empty : $" of partition '{pin.Partition}'";
                warnings.Add(
                    $"{pin.RelativePath}: {Describe(pin.FlowName, pin.Interface)} pins version {pin.Version} of the cache{partition} with render.cacheVersion, and the cache's retention has pruned that version's records, so its runs fail. Pin a version the cache still holds ('sqlflow cache list' lists them), or remove render.cacheVersion to render against the current version.");
            }
        }
    }

    /// <summary>The interfaces that keep <paramref name="ledgerFlowId"/>, the active ones first, then by flow and interface name.</summary>
    public static async Task<IReadOnlyList<InterfaceLocation>> KeepingAsync(OsduDbContext context, Guid ledgerFlowId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await context.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.LedgerFlowId == ledgerFlowId)
            .OrderByDescending(i => i.Active).ThenBy(i => i.FlowName).ThenBy(i => i.Interface)
            .Select(i => new InterfaceLocation(i.RepoId, i.FlowName, i.Interface, i.LedgerFlowId, i.LedgerName, i.Active, i.Partition))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The interfaces a repository declares for one flow, in document order, in <paramref name="partition"/> (null or empty
    /// for a flow that names no partitions); empty when the sync has not described it.
    /// </summary>
    public static async Task<IReadOnlyList<DeliveryInterface>> OfFlowAsync(
        OsduDbContext context, Guid repoId, string flowName, string? partition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        var scope = partition ?? string.Empty;
        return await context.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.RepoId == repoId && i.FlowName == flowName && i.Partition == scope && i.Active)
            .OrderBy(i => i.Ordinal)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The flows of a repository the read model describes, by name, whether the repository still declares them or not.</summary>
    public static async Task<IReadOnlySet<string>> DescribedFlowsAsync(OsduDbContext context, Guid repoId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var names = await context.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.RepoId == repoId)
            .Select(i => i.FlowName)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool Same(DeliveryInterface row, DeliveryInterface wanted)
        => row.FlowName == wanted.FlowName && row.Interface == wanted.Interface && row.Partition == wanted.Partition && row.Ordinal == wanted.Ordinal
           && row.LedgerFlowId == wanted.LedgerFlowId && row.LedgerName == wanted.LedgerName && row.Route == wanted.Route
           && row.RouteReason == wanted.RouteReason && row.MappingReference == wanted.MappingReference && row.CacheVersion == wanted.CacheVersion
           && row.Kind == wanted.Kind
           && row.RecordObject == wanted.RecordObject && row.AfterJson == wanted.AfterJson
           && row.TargetPartition == wanted.TargetPartition && row.RelativePath == wanted.RelativePath
           && row.Active == wanted.Active;

    /// <summary>
    /// Warns about every ledger identity this repository's interfaces keep that another flow's interface keeps too: an
    /// interface adopting the ledger of a flow that is still declared, or two flows of the same name in two repositories.
    /// </summary>
    private static async Task ReportSharedLedgersAsync(OsduDbContext context, Guid repoId, ICollection<string> warnings, CancellationToken ct)
    {
        var own = await context.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.RepoId == repoId && i.Active)
            .Select(i => new { i.LedgerFlowId, i.FlowName, i.Interface, i.LedgerName, i.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = own.Select(i => i.LedgerFlowId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var keepers = await context.DeliveryInterfaces.AsNoTracking()
            .Where(i => ids.Contains(i.LedgerFlowId) && i.Active)
            .Select(i => new { i.LedgerFlowId, i.RepoId, i.FlowName, i.Interface })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var group in keepers.GroupBy(k => k.LedgerFlowId).Where(g => g.Count() > 1))
        {
            var mine = own.First(o => o.LedgerFlowId == group.Key);
            var named = group.Select(k => Describe(k.FlowName, k.Interface) + (k.RepoId == repoId ? string.Empty : " (another repository)"));
            warnings.Add(
                $"{mine.RelativePath}: the ledger '{mine.LedgerName}' is kept by {string.Join(", ", named)}. Every one of them delivers the same records under it; "
                + "remove or rename the flow whose ledger an interface adopted, so only one of them keeps it.");
        }
    }

    /// <summary>
    /// Warns about every flow of this repository whose file declared it under another name before, while the ledger it kept
    /// under that name still holds OSDU ids its records claimed and nothing keeps it. A flow keeps its ledger under its name
    /// (osdu/docs/reference/flow/interfaces.md, Ledger identity), so the renamed flow starts an empty ledger, and every record
    /// of it whose id the old ledger claimed is held. Quiet once the flow keeps the old ledger with <c>ledger:</c>, and once
    /// its own ledger has claimed an id, which says the new ledger is meant.
    /// </summary>
    private static async Task ReportRenamedFlowsAsync(OsduDbContext context, Guid repoId, ICollection<string> warnings, CancellationToken ct)
    {
        var rows = await context.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.RepoId == repoId)
            .Select(i => new { i.FlowName, i.Interface, i.Partition, i.RelativePath, i.LedgerFlowId, i.LedgerName, i.Active })
            .ToListAsync(ct).ConfigureAwait(false);
        var renames = (
            from now in rows.Where(r => r.Active)
            join was in rows.Where(r => !r.Active)
                on (Path: now.RelativePath, Interface: now.Interface.ToLowerInvariant(), Partition: now.Partition.ToLowerInvariant())
                equals (Path: was.RelativePath, Interface: was.Interface.ToLowerInvariant(), Partition: was.Partition.ToLowerInvariant())
            where !string.Equals(now.FlowName, was.FlowName, StringComparison.OrdinalIgnoreCase) && now.LedgerFlowId != was.LedgerFlowId
            select (Now: now, Was: was)).ToList();
        if (renames.Count == 0)
        {
            return;
        }

        var left = renames.Select(r => r.Was.LedgerFlowId).Distinct().ToList();
        var kept = await context.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.Active && left.Contains(i.LedgerFlowId))
            .Select(i => i.LedgerFlowId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var (now, was) in renames.Where(r => !kept.Contains(r.Was.LedgerFlowId)))
        {
            if (!await HoldsClaimsAsync(context, was.LedgerFlowId, ct).ConfigureAwait(false)
                || await HoldsClaimsAsync(context, now.LedgerFlowId, ct).ConfigureAwait(false))
            {
                continue;
            }

            var ledger = LedgerEntry.OwnName(was.LedgerName, was.Partition);
            var key = now.Interface.Length == 0 ? $"ledger: {ledger}" : $"interfaces.{now.Interface}.ledger: {ledger}";
            var partition = now.Partition.Length == 0 ? string.Empty : $" in partition '{now.Partition}'";
            warnings.Add(
                $"{now.RelativePath}: {Describe(now.FlowName, now.Interface)} was declared as '{was.FlowName}' before, and a flow keeps its ledger under its name, "
                + $"so it starts an empty ledger{partition} while the records '{was.FlowName}' delivered stay in '{was.LedgerName}' with the OSDU ids they claimed. "
                + $"Each of its records whose id that ledger claimed is held, and nothing is sent for it. Keep the ledger with {key}, "
                + "and the records carry on with their history and their OSDU ids.");
        }
    }

    /// <summary>True when a record of the ledger <paramref name="flowId"/> has claimed an OSDU id: one seek of the directory, then of the ledger's records.</summary>
    private static Task<bool> HoldsClaimsAsync(OsduDbContext context, Guid flowId, CancellationToken ct)
        => (from l in context.DeliveryLedgers
            where l.FlowId == flowId
            join r in context.DeliveryRecords on new { l.PartitionId, l.FlowId } equals new { r.PartitionId, r.FlowId }
            where r.ClaimedTargetId != null
            select r.DeliveryKey).AnyAsync(ct);

    private static string Describe(string flow, string name) => name.Length == 0 ? $"flow '{flow}'" : $"interface '{name}' of flow '{flow}'";

    private static string? Clip(string? text, int length) => text is null || text.Length <= length ? text : text[..length];
}

/// <summary>What a reconciliation did to the rows.</summary>
public readonly record struct CatalogSyncCounts(int Added, int Updated, int Unchanged, int Removed);
