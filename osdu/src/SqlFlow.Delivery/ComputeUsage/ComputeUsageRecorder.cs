using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Delivery.ComputeUsage;

/// <summary>
/// Writes what the node's <see cref="ComputeUsageMeter"/> measured to the module's database, in the background, so
/// measuring never waits for a write and never fails the work it measures. A write that fails is tried again, after 5
/// seconds and then twice as long each time up to a minute, with the batch kept; work that ends meanwhile waits in the
/// meter's queue.
/// </summary>
/// <remarks>
/// The recorder outlives the node's drain: a stopping host stops its services before, or while, the node finishes the
/// runs it holds, and those runs' usage must still be written. So it keeps writing through the stop and finishes only
/// once every service has stopped (<see cref="StoppedAsync"/>): what the node still held then ends as interrupted, the
/// queue is written once more, and whatever the database does not take in that last try is named in the log.
/// </remarks>
public sealed partial class ComputeUsageRecorder : IHostedLifecycleService, IDisposable
{
    /// <summary>The most measurements one write holds.</summary>
    internal const int BatchSize = 200;

    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongestRetry = TimeSpan.FromSeconds(60);

    private readonly ComputeUsageMeter _meter;
    private readonly ComputeUsageStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<ComputeUsageRecorder> _logger;
    private readonly CancellationTokenSource _finishing = new();
    private Task _writing = Task.CompletedTask;
    private long _reportedDropped;

    public ComputeUsageRecorder(ComputeUsageMeter meter, ComputeUsageStore store, TimeProvider clock, ILogger<ComputeUsageRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(meter);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _meter = meter;
        _store = store;
        _clock = clock;
        _logger = logger;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_store.Available)
        {
            LogNoDatabase();
        }

        _writing = Task.Run(WriteAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Every service has stopped, the node among them: ends what the meter still holds, and gives the queue one last
    /// write within <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        _meter.Stop();
        await _finishing.CancelAsync().ConfigureAwait(false);
        try
        {
            await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogStopCutShort();
        }
    }

    public void Dispose() => _finishing.Dispose();

    /// <summary>Writes the queue until the meter stops and the queue is empty.</summary>
    private async Task WriteAsync()
    {
        var reader = _meter.Measured;
        var batch = new List<ComputeWork>(BatchSize);
        while (await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < BatchSize && reader.TryRead(out var work))
            {
                batch.Add(work);
            }

            await WriteBatchAsync(batch).ConfigureAwait(false);
            ReportDropped();
        }

        ReportDropped();
    }

    /// <summary>One batch, tried until the database takes it; once the host is finishing, tried once more and then named as lost.</summary>
    private async Task WriteBatchAsync(List<ComputeWork> batch)
    {
        if (!_store.Available)
        {
            return;
        }

        var wait = FirstRetry;
        while (true)
        {
            try
            {
                await _store.RecordAsync(batch, CancellationToken.None).ConfigureAwait(false);
                return;
            }
#pragma warning disable CA1031 // A meter that cannot write must never stop the node: every failure is logged and the batch kept.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_finishing.IsCancellationRequested)
                {
                    LogLost(batch.Count, batch.Sum(w => w.Units), SecretHygiene.RedactedMessage(ex));
                    return;
                }

                LogWriteFailed(batch.Count, (int)wait.TotalSeconds, SecretHygiene.RedactedMessage(ex));
            }

            try
            {
                await Task.Delay(wait, _clock, _finishing.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The host is finishing: the loop tries once more at once, and names the batch if that fails too.
            }

            wait = wait + wait < LongestRetry ? wait + wait : LongestRetry;
        }
    }

    private void ReportDropped()
    {
        var dropped = _meter.Dropped;
        if (dropped > _reportedDropped)
        {
            LogDropped(dropped - _reportedDropped, ComputeUsageMeter.Capacity);
            _reportedDropped = dropped;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compute usage is not recorded on this node: it was started without the module's database.")]
    private partial void LogNoDatabase();

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the compute usage of {Count} piece(s) of work failed, and is tried again in {Seconds}s: {Error}")]
    private partial void LogWriteFailed(int count, int seconds, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The compute usage of {Count} piece(s) of work ({Units:0.######} compute units) was not recorded: the node stopped before the database took it: {Error}")]
    private partial void LogLost(int count, double units, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "The compute usage of {Count} piece(s) of work was not recorded: more than {Capacity} were waiting for the database.")]
    private partial void LogDropped(long count, int capacity);

    [LoggerMessage(Level = LogLevel.Error, Message = "The node's stop ran out of time before its last compute usage was written.")]
    private partial void LogStopCutShort();
}
