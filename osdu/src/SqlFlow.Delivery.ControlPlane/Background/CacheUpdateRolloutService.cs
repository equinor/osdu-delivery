using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Carries approved cache changes out to the estate, at a pace an operator sets. When a cached value moves, the
/// records built from it must be delivered again, and one corrected unit can reach millions of records: doing
/// that in one statement would hold locks on the delivery table for as long as it takes and flood OSDU behind it.
/// This service instead takes one approved change at a time and marks a bounded batch of its records for
/// redelivery, in delivery-key order from the change's own cursor, then stops until the next tick. A change drains
/// over as many passes as it needs, a restart resumes where it stopped, and the marking is metadata only, so no
/// payload is ever re-uploaded because a reference value was corrected.
/// </summary>
/// <remarks>
/// Hosted on every replica: the pass claims nothing, but each batch's update is idempotent (marking a record for
/// redelivery twice is the same as once) and the cursor only moves forward, so two replicas ticking at once cost a
/// little duplicated work and never a wrong result. <c>Osdu:CacheRollout:BatchSize</c> and
/// <c>:BatchesPerPass</c> bound the work of one tick; <c>:PollSeconds</c> sets how often a tick happens.
/// </remarks>
public sealed partial class CacheUpdateRolloutService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly CacheRolloutOptions _options;
    private readonly ILogger<CacheUpdateRolloutService> _logger;

    public CacheUpdateRolloutService(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<CacheRolloutOptions> options,
        ILogger<CacheUpdateRolloutService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PassAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown disposed the DI container out from under this tick; leave quietly.
                break;
            }
            catch (Exception ex)
            {
                LogPassError(SecretHygiene.RedactedMessage(ex));
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>One tick: at most <c>BatchesPerPass</c> batches, oldest decision first, then out of the way.</summary>
    private async Task PassAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedgerCacheSets>();

        // A change whose records are all marked closes when the last flow reading it has rendered them again.
        var settled = await ledger.SettleRolloutsAsync(_clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (settled > 0)
        {
            LogSettled(settled);
        }

        var queue = await ledger.ListRolloutQueueAsync(_options.BatchesPerPass, ct).ConfigureAwait(false);
        if (queue.Count == 0)
        {
            return;
        }

        var budget = _options.BatchesPerPass;
        foreach (var tag in queue)
        {
            if (budget <= 0 || ct.IsCancellationRequested)
            {
                break;
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            var batch = await ledger.RollOutTagAsync(tag.TagId, _options.BatchSize, now, ct).ConfigureAwait(false);
            budget--;
            if (batch.Marked > 0)
            {
                LogBatch(batch.Marked, tag.TagId, tag.Describe(), batch.Processed, batch.Affected);
            }

            if (batch.Completed)
            {
                LogCompleted(tag.TagId, tag.Describe(), batch.Processed);
            }
            else if (batch.Marked < _options.BatchSize && batch.Processed > 0)
            {
                LogMarked(tag.TagId, tag.Describe(), batch.Processed);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache rollout marked {Marked} record(s) for redelivery from tag {TagId} ({Change}); {Processed} of {Affected} done.")]
    private partial void LogBatch(long marked, long tagId, string change, long processed, long affected);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache rollout marked every record of tag {TagId} ({Change}), {Processed} in all; it is rolled out when each flow reading them has rendered them again.")]
    private partial void LogMarked(long tagId, string change, long processed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache rollout finished tag {TagId} ({Change}): {Processed} record(s) delivered again.")]
    private partial void LogCompleted(long tagId, string change, long processed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache rollout closed {Settled} change(s): every flow reading them has rendered their records again.")]
    private partial void LogSettled(int settled);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cache rollout pass error: {Error}")]
    private partial void LogPassError(string error);
}
