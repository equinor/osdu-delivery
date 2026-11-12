using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Removes the render context nothing reads any more (osdu/docs/reference/concepts/storage-and-retention.md, The render
/// context): every hour, the sets of cached values no record points at and no render has used for
/// <see cref="CacheSetKeeping.UnusedFor"/>, with their values. A record's set is replaced whenever a render reads other
/// values, so without this pass the sets of every earlier render stay, and they come to outweigh everything else the ledger
/// holds. A pass that fails (the module database not migrated yet, a lost connection) is tried again a minute on.
/// </summary>
public sealed partial class CacheSetSweepService : BackgroundService
{
    /// <summary>The wait after start before the first pass: past the start-up work.</summary>
    private static readonly TimeSpan FirstPassAfter = TimeSpan.FromMinutes(10);

    /// <summary>How often the pass runs.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <summary>The wait before a pass that failed is tried again.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<CacheSetSweepService> _logger;

    public CacheSetSweepService(IServiceProvider services, TimeProvider clock, ILogger<CacheSetSweepService> logger)
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
                var swept = await SweepAsync(stoppingToken).ConfigureAwait(false);
                if (swept.Sets > 0)
                {
                    LogSwept(swept.Sets, swept.Entries);
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

    /// <summary>One pass: every set no record points at and no render has used for a day, removed with its values.</summary>
    public async Task<CacheSetSweep> SweepAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        return await ledger.SweepCacheSetsAsync(_clock.GetUtcNow().UtcDateTime - CacheSetKeeping.UnusedFor, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Render context: {Sets} set(s) no record points at removed, with {Entries} cached value(s).")]
    private partial void LogSwept(long sets, long entries);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sweeping the render context failed, and is tried again in {Seconds}s: {Error}")]
    private partial void LogPassError(string error, int seconds);
}
