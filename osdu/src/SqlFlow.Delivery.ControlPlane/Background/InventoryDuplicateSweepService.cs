using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>What one pass of the duplicate check sweep did: the partitions it read, the checks it closed, deleted and kept expired.</summary>
public sealed record InventoryDuplicateSweepPass(int Partitions, int Settled, int Deleted, int Expired);

/// <summary>
/// Retires the duplicate checks of every partition on a clock (osdu/docs/reference/flow/inventory-duplicates.md, The check's
/// table), so a check's table goes when its week is up whether or not anyone asks for another check: a check under way whose
/// run ended without finishing it is closed (its download's table dropped), and a check older than a week is deleted with its
/// groups and its table, or kept expired when a removal took the picks of a selection made of it. The same steps run when an
/// operator asks for a check or reads one; this pass is for the partitions nobody visits. A pass that fails (the module
/// database not migrated yet, a lost connection) is tried again a minute on.
/// </summary>
public sealed partial class InventoryDuplicateSweepService : BackgroundService
{
    /// <summary>The wait after start before the first pass: past the start-up work.</summary>
    private static readonly TimeSpan FirstPassAfter = TimeSpan.FromMinutes(5);

    /// <summary>How often the pass runs: a check's table outlives its week by an hour at most.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <summary>The wait before a pass that failed is tried again.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<InventoryDuplicateSweepService> _logger;

    public InventoryDuplicateSweepService(IServiceProvider services, TimeProvider clock, ILogger<InventoryDuplicateSweepService> logger)
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
        var wait = FirstPassAfter;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var pass = await SweepAsync(stoppingToken).ConfigureAwait(false);
                if (pass.Settled + pass.Deleted + pass.Expired > 0)
                {
                    LogSwept(pass.Partitions, pass.Settled, pass.Deleted, pass.Expired);
                }

                wait = SweepInterval;
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
                wait = RetryInterval;
            }
        }
    }

    /// <summary>
    /// One pass over every partition holding duplicate checks: each check under way settled as the API settles it, then the
    /// partition's checks past their week retired.
    /// </summary>
    public async Task<InventoryDuplicateSweepPass> SweepAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var osdu = scope.ServiceProvider.GetRequiredService<OsduDbContext>();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var partitions = await (from c in osdu.DeliveryInventoryDuplicateChecks.AsNoTracking()
                                join p in osdu.DeliveryLedgerPartitions.AsNoTracking() on c.PartitionId equals p.PartitionId
                                select new { p.PartitionId, p.Name })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        int settled = 0, deleted = 0, expired = 0;
        foreach (var partition in partitions.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var underWay = await osdu.DeliveryInventoryDuplicateChecks.AsNoTracking()
                .Where(c => c.PartitionId == partition.PartitionId
                    && (c.Status == InventoryDuplicates.Queued || c.Status == InventoryDuplicates.Running))
                .Select(c => c.InventoryDuplicateCheckId)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var checkId in underWay)
            {
                if (await ledger.GetInventoryDuplicateCheckAsync(partition.Name, checkId, ct).ConfigureAwait(false) is not { } check)
                {
                    continue;
                }

                var after = await DeliveryInventoryEndpoints.SettleCheckAsync(catalog, ledger, partition.Name, check, _clock, ct).ConfigureAwait(false);
                if (!InventoryDuplicates.IsUnderWay(after.Status))
                {
                    settled++;
                }
            }

            var swept = await ledger.SweepInventoryDuplicateChecksAsync(partition.Name, _clock.GetUtcNow().UtcDateTime - InventoryDuplicates.KeptFor, ct)
                .ConfigureAwait(false);
            deleted += swept.Deleted;
            expired += swept.Expired;
        }

        return new InventoryDuplicateSweepPass(partitions.Count, settled, deleted, expired);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Duplicate checks of {Partitions} partition(s): {Settled} whose run had ended closed, {Deleted} past their week deleted with their tables, {Expired} kept expired for the removals that took their picks.")]
    private partial void LogSwept(int partitions, int settled, int deleted, int expired);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sweeping the duplicate checks failed, and is tried again in {Seconds}s: {Error}")]
    private partial void LogPassError(string error, int seconds);
}
