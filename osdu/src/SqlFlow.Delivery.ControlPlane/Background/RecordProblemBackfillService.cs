using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Sorts into their problems the blocked records the ledger has none for
/// (osdu/docs/reference/concepts/record-lifecycle.md, Issues): the records held or failed before the ledger kept
/// problems, and any a completion appended by an older build blocked. A record held or failed from now on is sorted by
/// the write that blocks it; these are read from an index that holds them alone, a bounded page at a time with a pause
/// between pages, so a ledger with millions of blocked records is sorted without competing with the deliveries running
/// beside it.
///
/// The pass is resumable and repeatable: a record sorted leaves the index it is read from, so a control plane that
/// restarts mid-pass starts again where it stopped, and a pass over nothing costs one seek.
/// </summary>
public sealed partial class RecordProblemBackfillService : BackgroundService
{
    /// <summary>Records read and sorted per page: one read of the index, one statement of updates.</summary>
    private const int PageSize = 500;

    /// <summary>The wait between pages, which is what keeps the pass in the background rather than in the way.</summary>
    private static readonly TimeSpan BetweenPages = TimeSpan.FromMilliseconds(250);

    /// <summary>The wait before a pass that failed is tried again (the module database not migrated yet, a lost connection).</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>How often the pass looks again once it found nothing: a completion an older node appended can block a record unsorted.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<RecordProblemBackfillService> _logger;

    public RecordProblemBackfillService(IServiceProvider services, TimeProvider clock, ILogger<RecordProblemBackfillService> logger)
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
            var wait = SweepInterval;
            try
            {
                var (sorted, pages) = await BackfillAsync(stoppingToken).ConfigureAwait(false);
                if (sorted > 0)
                {
                    LogSorted(sorted, pages);
                }
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

            try
            {
                await Task.Delay(wait, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One pass: pages until none is left to sort, or until a page sorts nothing (its records changed under it, which the
    /// writes that changed them settle). The records sorted, and the pages it took.
    /// </summary>
    public async Task<(int Sorted, int Pages)> BackfillAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var sortable = scope.ServiceProvider.GetRequiredService<OsduLedger>();
        var sorted = 0;
        var pages = 0;
        while (!ct.IsCancellationRequested)
        {
            var (read, written) = await sortable.SortProblemsAsync(PageSize, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            sorted += written;
            pages++;
            if (written == 0)
            {
                LogStalled(read);
                break;
            }

            await Task.Delay(BetweenPages, _clock, ct).ConfigureAwait(false);
        }

        return (sorted, pages);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sorted {Records} blocked record(s) into the issues that keep them blocked, in {Pages} page(s).")]
    private partial void LogSorted(int records, int pages);

    [LoggerMessage(Level = LogLevel.Information, Message = "A page of {Records} blocked record(s) changed while it was being sorted into issues; the next pass sorts what is still blocked.")]
    private partial void LogStalled(int records);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sorting blocked records into issues failed: {Error}. Trying again in about {Seconds}s.")]
    private partial void LogPassError(string error, int seconds);
}
