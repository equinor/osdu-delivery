using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Describes the interfaces of the delivery pipelines a repository sync has not described yet: the pipelines synced before
/// the read model of sources and interfaces existed (<see cref="DeliveryInterfaceCatalog"/>), and those described before
/// it recorded the cache version each interface renders against. Once, when the control plane starts, it parses each such
/// repository's catalog copies and writes their rows exactly as the repository sync writes them, so a record's page finds
/// its flow, and the cache's retention knows which versions are pinned, without waiting for the repository's next sync. The
/// rows of a repository the catalog holds no delivery pipeline of any more become inactive, as a sync leaves an interface
/// its repository stops declaring, so nothing waits on a flow no repository declares. A repository the read model
/// describes completely is its sync's to keep current. The flows of the other kinds that keep a ledger (retrieval,
/// assertion, dimension, inventory; <see cref="LedgerFlowCatalog"/>) of a repository the read model holds none of are
/// described the same way, so the ledgers they keep are not taken for orphans before the repository's next sync. A pass
/// that fails (the module database not migrated yet, a lost connection) is tried again until one completes.
/// </summary>
public sealed partial class InterfaceCatalogBackfillService : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<InterfaceCatalogBackfillService> _logger;

    public InterfaceCatalogBackfillService(IServiceProvider services, TimeProvider clock, ILogger<InterfaceCatalogBackfillService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BackfillAsync(stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown disposed the container out from under the pass; leave quietly.
                return;
            }
            catch (Exception ex)
            {
                LogPassError(SecretHygiene.RedactedMessage(ex), (int)RetryInterval.TotalSeconds);
            }

            try
            {
                await Task.Delay(RetryInterval, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One pass: every repository with delivery pipelines and no interface rows, or with an active row whose cache version
    /// or document hash is not recorded, is described from its catalog copies; the active rows of every repository the
    /// catalog holds no delivery pipeline of become inactive; and every repository with flows of the other kinds that keep a
    /// ledger and no row of them has those described from their catalog copies.
    /// </summary>
    public async Task BackfillAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var osdu = scope.ServiceProvider.GetRequiredService<OsduDbContext>();
        var documents = scope.ServiceProvider.GetRequiredService<DeliveryDocumentLoader>();
        var pipelines = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Kind == FlowDefinition.FlowTypeName)
            .Select(p => new { p.RepoId, p.Name, p.RelativePath, p.Yaml, p.Active })
            .ToListAsync(ct).ConfigureAwait(false);

        // Per described repository: whether it has active rows, and whether any of them was written before the cache
        // version an interface renders against was recorded, or before the document's hash was, which a sync pairs a renamed
        // pipeline with the one it replaced by. A row of a pipeline that left is not: the catalog keeps no copy of it.
        var described = (await osdu.DeliveryInterfaces.AsNoTracking()
                .GroupBy(i => i.RepoId)
                .Select(g => new
                {
                    RepoId = g.Key,
                    Active = g.Count(i => i.Active),
                    Unrecorded = g.Count(i => i.Active && (i.CacheVersion == null || i.DocumentHash == null)),
                })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(d => d.RepoId, d => new DescribedRepository(d.RepoId, d.Active > 0, d.Unrecorded > 0));

        foreach (var repository in pipelines.GroupBy(p => p.RepoId))
        {
            ct.ThrowIfCancellationRequested();
            var rows = described.GetValueOrDefault(repository.Key);
            if (rows is not null && !rows.Unrecorded)
            {
                continue;
            }

            var sources = new List<RepositorySource>();
            var unreadable = 0;
            foreach (var pipeline in repository.OrderByDescending(p => p.Active).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                try
                {
                    sources.Add(new RepositorySource(pipeline.RelativePath, documents.ParseSource(pipeline.Yaml, pipeline.RelativePath), pipeline.Active));
                }
                catch (FlowValidationException)
                {
                    // A catalog copy that does not parse describes no interface; its pipeline's page says why.
                    unreadable++;
                }
            }

            // A repository none of whose copies parse has nothing to describe; one whose rows were written before is
            // described the sync's way, so what no copy describes becomes inactive.
            if (sources.Count == 0 && rows is null)
            {
                continue;
            }

            var warnings = new List<string>();
            var kinds = await DeliveryCatalogSync.MappingKindsAsync(osdu, repository.Key, ct).ConfigureAwait(false);
            var mappings = await DeliveryCatalogSync.MappingHashesAsync(osdu, repository.Key, ct).ConfigureAwait(false);
            var counts = await DeliveryInterfaceCatalog.ReconcileAsync(
                osdu, repository.Key, sources, kinds, mappings, _clock.GetUtcNow().UtcDateTime, warnings, LedgerMoveGuard.None, ct).ConfigureAwait(false);
            LogBackfilled(repository.Key, counts.Added + counts.Updated, sources.Count, unreadable);
            foreach (var warning in warnings)
            {
                LogWarning(repository.Key, warning);
            }
        }

        // The rows of a repository the catalog holds no delivery pipeline of: no repository declares those flows and none of
        // them can run, so they stop answering first, and the cache's retention stops waiting on what they pin.
        var held = pipelines.Select(p => p.RepoId).ToHashSet();
        foreach (var orphaned in described.Values.Where(d => d.Active && !held.Contains(d.RepoId)).Select(d => d.RepoId))
        {
            ct.ThrowIfCancellationRequested();
            var warnings = new List<string>();
            var counts = await DeliveryInterfaceCatalog.ReconcileAsync(
                osdu, orphaned, [], new Dictionary<string, string>(), new Dictionary<string, string>(), _clock.GetUtcNow().UtcDateTime, warnings, LedgerMoveGuard.None, ct).ConfigureAwait(false);
            LogRetired(orphaned, counts.Removed);
        }

        await BackfillFlowDocumentsAsync(catalog, osdu, documents, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Describes, from their catalog copies, the flows of the other kinds that keep a ledger of every repository the read
    /// model holds none of (<see cref="LedgerFlowCatalog"/>), as its sync would.
    /// </summary>
    private async Task BackfillFlowDocumentsAsync(CatalogDbContext catalog, OsduDbContext osdu, DeliveryDocumentLoader documents, CancellationToken ct)
    {
        var kinds = LedgerFlowCatalog.Kinds.ToArray();
        var pipelines = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Active && kinds.Contains(p.Kind))
            .Select(p => new { p.RepoId, p.Kind, p.Name, p.RelativePath, p.Yaml })
            .ToListAsync(ct).ConfigureAwait(false);
        var described = (await osdu.DeliveryLedgerFlows.AsNoTracking().Select(d => d.RepoId).Distinct().ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        foreach (var repository in pipelines.GroupBy(p => p.RepoId).Where(r => !described.Contains(r.Key)))
        {
            ct.ThrowIfCancellationRequested();
            var flows = new List<RepositoryFlow>();
            var unreadable = 0;
            foreach (var pipeline in repository.OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                try
                {
                    flows.Add(DeliveryCatalogSync.DescribeFlow(documents, pipeline.Kind, pipeline.Yaml, pipeline.RelativePath));
                }
                catch (FlowValidationException)
                {
                    // A catalog copy that does not parse describes no flow; its pipeline's page says why.
                    unreadable++;
                }
            }

            var warnings = new List<string>();
            var counts = await LedgerFlowCatalog.ReconcileAsync(osdu, repository.Key, flows, _clock.GetUtcNow().UtcDateTime, warnings, LedgerMoveGuard.None, ct).ConfigureAwait(false);
            LogFlowsBackfilled(repository.Key, counts.Added + counts.Updated, unreadable);
            foreach (var warning in warnings)
            {
                LogWarning(repository.Key, warning);
            }
        }
    }

    /// <summary>What the read model holds of one repository: whether any of its rows is active, and whether an active one lacks its cache version.</summary>
    private sealed record DescribedRepository(Guid RepoId, bool Active, bool Unrecorded);

    [LoggerMessage(Level = LogLevel.Information, Message = "Made {Interfaces} interface(s) of repository {RepoId} inactive: the catalog holds no delivery flow of it any more.")]
    private partial void LogRetired(Guid repoId, int interfaces);

    [LoggerMessage(Level = LogLevel.Information, Message = "Described {Interfaces} interface(s) of {Flows} delivery flow(s) of repository {RepoId} from their catalog copies, with the cache version each renders against ({Unreadable} did not parse).")]
    private partial void LogBackfilled(Guid repoId, int interfaces, int flows, int unreadable);

    [LoggerMessage(Level = LogLevel.Information, Message = "Described {Flows} retrieval, assertion, dimension and inventory flow(s) of repository {RepoId} from their catalog copies ({Unreadable} did not parse).")]
    private partial void LogFlowsBackfilled(Guid repoId, int flows, int unreadable);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repository {RepoId}: {Warning}")]
    private partial void LogWarning(Guid repoId, string warning);

    [LoggerMessage(Level = LogLevel.Error, Message = "Describing the interfaces of delivery flows failed, and is tried again in {Seconds}s: {Error}")]
    private partial void LogPassError(string error, int seconds);
}
