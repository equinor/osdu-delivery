using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Finishes the ledger moves that could not be made when they were asked (osdu/docs/reference/concepts/renaming-a-pipeline.md;
/// <see cref="LedgerMoves"/>): the repository sync makes the moves it asks once it committed, and a takeover makes its own at
/// once, but a move waiting for work in flight on either ledger, one another process was making, and one a sync or a
/// control plane stopped half way are left running. This pass takes each of them up again every few seconds, oldest first,
/// a slice at a time under the move's own lock, until it is done; a run of the flow a ledger moves to finishes it first if it
/// gets there earlier. A pass that fails (the module database not migrated yet, a lost connection) is tried again a minute on.
/// </summary>
public sealed partial class LedgerMoveService : BackgroundService
{
    /// <summary>How often the pass looks for moves left running: a move that waited is taken up within this of the work it waited for ending.</summary>
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

    /// <summary>One pass: every move asked and not done, oldest first.</summary>
    public async Task<IReadOnlyList<LedgerMoveResult>> PassAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var ledger = scope.ServiceProvider.GetRequiredService<ILedger>();
        var results = await ledger.CompleteLedgerMovesAsync(null, ct).ConfigureAwait(false);
        foreach (var result in results)
        {
            switch (result.Outcome)
            {
                case LedgerMoves.Completed:
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
