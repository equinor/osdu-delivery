using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// Fills the identity index for the records a ledger already held when the index was added, so an operator can look a
/// record up by a wellbore id, a well name or a file name without waiting for that record to be planned again. One
/// bounded page at a time, pausing between pages, so a ledger of millions is indexed without competing with the
/// deliveries running beside it; records staged from now on write their own rows as they are staged.
///
/// The pass is resumable and repeatable: it walks records in key order and writes only for records the index does not
/// hold, so a control plane that restarts mid-pass starts again and costs one read per page it had already done.
/// </summary>
public sealed partial class RecordIdentityBackfillService : BackgroundService
{
    /// <summary>Records read per page. Each contributes at most a couple of dozen small rows.</summary>
    private const int PageSize = 500;

    /// <summary>The wait between pages, which is what keeps the pass in the background rather than in the way.</summary>
    private static readonly TimeSpan BetweenPages = TimeSpan.FromMilliseconds(250);

    /// <summary>The wait before a pass that failed is tried again (the module database not migrated yet, a lost connection).</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>How often the pass runs again after it has completed: a record restored from a backup, or one written by an older build.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<RecordIdentityBackfillService> _logger;

    public RecordIdentityBackfillService(IServiceProvider services, TimeProvider clock, ILogger<RecordIdentityBackfillService> logger)
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
                var (records, pages) = await BackfillAsync(stoppingToken).ConfigureAwait(false);
                if (records > 0)
                {
                    LogIndexed(records, pages);
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

    /// <summary>One pass over the ledger: the records indexed, and the pages it took.</summary>
    public async Task<(int Records, int Pages)> BackfillAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var indexed = scope.ServiceProvider.GetRequiredService<OsduLedger>();
        var records = 0;
        var pages = 0;
        RecordCursor? after = null;
        while (!ct.IsCancellationRequested)
        {
            var (last, written, _) = await indexed.BackfillIdentitiesAsync(after, PageSize, ct).ConfigureAwait(false);
            if (last is null)
            {
                break;
            }

            after = last;
            records += written;
            pages++;
            await Task.Delay(BetweenPages, _clock, ct).ConfigureAwait(false);
        }

        return (records, pages);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Indexed {Records} record(s) for lookup by name, id and file, in {Pages} page(s).")]
    private partial void LogIndexed(int records, int pages);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Filling the record identity index failed: {Error}. Trying again in about {Seconds}s.")]
    private partial void LogPassError(string error, int seconds);
}
