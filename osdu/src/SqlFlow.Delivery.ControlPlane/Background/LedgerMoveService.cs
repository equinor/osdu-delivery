using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Moves the ledger of each name of a document the repository sync found to the name that owns it
/// (osdu/docs/reference/concepts/renaming-a-pipeline.md; <see cref="LedgerMoves"/>), within seconds of the sync, so the
/// renamed pipeline's records, history and OSDU ids are under its new name before anyone looks or a run starts. A move is
/// made a slice at a time under its own lock; one waiting for work in flight on either ledger, or that another process is
/// making, is taken up on a later pass, and a run of the renamed pipeline finishes it first if it gets there earlier. A pass
/// that fails (the module database not migrated yet, a lost connection) is tried again a minute on.
/// </summary>
public sealed partial class LedgerMoveService : BackgroundService
{
    /// <summary>How often the pass looks for moves the sync asked for: a move starts within this of the sync.</summary>
    private static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(15);

    /// <summary>The wait before a pass that failed is tried again.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<LedgerMoveService> _logger;

    public LedgerMoveService(IServiceProvider services, TimeProvider clock, ILogger<LedgerMoveService> logger)
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
        var wait = PassInterval;
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
                await PassAsync(stoppingToken).ConfigureAwait(false);
                wait = PassInterval;
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

    /// <summary>One pass: every move the sync asked for and nobody has made yet, oldest first.</summary>
    public async Task<IReadOnlyList<LedgerMoveResult>> PassAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var results = await ledger.CompleteLedgerMovesAsync(null, ct).ConfigureAwait(false);
        foreach (var result in results)
        {
            switch (result.Outcome)
            {
                case "completed":
                    LogMoved(result.Partition, result.From, result.To, result.Records, result.Summary);
                    break;
                case "failed":
                    LogRefused(result.Partition, result.From, result.To, result.Summary);
                    break;
                default:
                    LogWaiting(result.Partition, result.From, result.To, result.Summary);
                    break;
            }
        }

        return results;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved ledger {From} to {To} in partition {Partition}: {Records} record(s). {Summary}")]
    private partial void LogMoved(string partition, Guid from, Guid to, long records, string summary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Did not move ledger {From} to {To} in partition {Partition}: {Summary}")]
    private partial void LogRefused(string partition, Guid from, Guid to, string summary);

    [LoggerMessage(Level = LogLevel.Information, Message = "The move of ledger {From} to {To} in partition {Partition} waits: {Summary}")]
    private partial void LogWaiting(string partition, Guid from, Guid to, string summary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The ledger move pass failed ({Message}); trying again in {Seconds} seconds.")]
    private partial void LogPassError(string message, int seconds);
}
