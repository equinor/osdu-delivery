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
/// Writes the search terms of every repository with delivery flows again, once, when the control plane starts
/// (osdu/docs/reference/concepts/search-terms.md): from the catalog's copies of its delivery flows (the tables each
/// reads and the mapping it renders with) and the mappings and cache declarations the module database holds, as the
/// repository sync writes them. A module upgraded since a repository's last sync, or migrated before the terms existed,
/// so describes its terms as this version does without waiting for the next sync; a repository with terms and no
/// delivery flow any longer loses them. What people made of the terms is kept apart, by the terms' identity. A pass
/// that fails (the module database not migrated yet, a lost connection) is tried again until one completes.
/// </summary>
public sealed partial class SearchTermCatalogRefreshService : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<SearchTermCatalogRefreshService> _logger;

    public SearchTermCatalogRefreshService(IServiceProvider services, TimeProvider clock, ILogger<SearchTermCatalogRefreshService> logger)
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
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
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
    /// One pass over every repository with delivery flows in the catalog or search terms in the module database, each in a
    /// write of its own.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var osdu = scope.ServiceProvider.GetRequiredService<OsduDbContext>();
        var documents = scope.ServiceProvider.GetRequiredService<DeliveryDocumentLoader>();
        var pipelines = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Kind == FlowDefinition.FlowTypeName)
            .Select(p => new { p.RepoId, p.Name, p.RelativePath, p.Yaml, p.Active })
            .ToListAsync(ct).ConfigureAwait(false);
        var withTerms = await osdu.DeliverySearchTerms.AsNoTracking()
            .Select(t => t.RepoId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var repoId in pipelines.Select(p => p.RepoId).Concat(withTerms).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var warnings = new List<string>();
            var sources = new List<RepositorySource>();
            foreach (var pipeline in pipelines.Where(p => p.RepoId == repoId).OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                try
                {
                    sources.Add(new RepositorySource(pipeline.RelativePath, documents.ParseSource(pipeline.Yaml, pipeline.RelativePath), pipeline.Active));
                }
                catch (FlowValidationException ex)
                {
                    // A catalog copy that does not parse reads no table; its pipeline's page says why.
                    warnings.Add($"{pipeline.RelativePath}: gives no search terms, since the flow does not read: {ex.Message}");
                }
            }

            var counts = await DeliverySearchTermCatalog.ReconcileAsync(
                osdu, scope.ServiceProvider.GetRequiredService<IRepositoryFolders>(), repoId, sources, documents, _clock.GetUtcNow().UtcDateTime, warnings, ct).ConfigureAwait(false);
            osdu.ChangeTracker.Clear();
            LogRefreshed(repoId, counts.Added, counts.Updated, counts.Removed, counts.Unchanged);
            foreach (var warning in warnings)
            {
                LogWarning(repoId, warning);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Search terms of repository {RepoId}: {Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged.")]
    private partial void LogRefreshed(Guid repoId, int added, int updated, int removed, int unchanged);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repository {RepoId}: {Warning}")]
    private partial void LogWarning(Guid repoId, string warning);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the search terms again failed, and is tried again in {Seconds}s: {Error}")]
    private partial void LogPassError(string error, int seconds);
}
