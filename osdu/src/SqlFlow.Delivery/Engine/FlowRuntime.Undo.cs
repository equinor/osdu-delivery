using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

// Undoing what unfinished deliveries left in OSDU (osdu/docs/reference/concepts/artifacts-and-undo.md, When the undo
// runs, and what it is given): the undo operation, the sweep a deliver run and a flow-wide drain end with, and the
// undo a removal and deleting the ledger run before they take records out of OSDU.
public sealed partial class FlowRuntime
{
    /// <summary>
    /// Undoes what unfinished deliveries of the flow left in OSDU (osdu/docs/reference/concepts/artifacts-and-undo.md, When
    /// the undo runs, and what it is given): the <c>undo</c> operation. Every artifact due, every one whose undo failed and
    /// is past its backoff (with <paramref name="exhausted"/>, also those whose undo failed as often as the sweep tries),
    /// and what a unit its record no longer carries created, a page of whole records at a time, up to
    /// <see cref="ArtifactLimits.SweepPerRun"/> records. Each record's undo is an attempt of phase undo on its history.
    /// </summary>
    public Task<UndoSummary> UndoUnfinishedAsync(bool exhausted, CancellationToken ct = default)
        => TrackAsync(DeliveryOperations.Undo, new { exhausted }, null, async () =>
        {
            var summary = await SweepAsync(exhausted, ct).ConfigureAwait(false);
            return (summary, summary.Describe(), (Guid?)null, summary.Idle);
        }, ct);

    /// <summary>
    /// The sweep a deliver run and a flow-wide drain end with. Its failure is not the run's: what the run delivered stands, the
    /// artifacts stay as they were, and the next run sweeps them.
    /// </summary>
    private async Task<UndoSummary> SweepAfterRunAsync(CancellationToken ct)
    {
        try
        {
            return await SweepAsync(exhausted: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or System.Data.Common.DbException or TimeoutException or InvalidOperationException)
        {
            _log.LogWarning(
                "Undoing what unfinished deliveries left could not finish ({Message}); what is left stays recorded, and the next run undoes it.",
                HeaderRedaction.RedactMessage(ex.Message));
            return UndoSummary.Empty;
        }
    }

    private async Task<UndoSummary> SweepAsync(bool exhausted, CancellationToken ct)
    {
        var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
        if (!protocol.Undoes)
        {
            return UndoSummary.Empty;
        }

        var ledger = RequireLedger();

        // A lease a stopped worker left is applied first, so the records it held say which unit they carry before anything of
        // theirs is told abandoned.
        await ledger.RecoverExpiredLeasesAsync(Flow.Id, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        var runner = new UndoRunner(protocol, _context.Time, Truncate($"{Environment.MachineName}/{Environment.ProcessId}/undo", 200), RunId);
        var total = UndoSummary.Empty;
        DeliveryKey? after = null;
        while (total.Records < ArtifactLimits.SweepPerRun)
        {
            ct.ThrowIfCancellationRequested();
            var now = _context.Time.GetUtcNow().UtcDateTime;
            var page = await ledger.SweepArtifactsAsync(Flow.Id, now, after, Math.Min(ArtifactLimits.SweepPage, ArtifactLimits.SweepPerRun - total.Records), exhausted, ct).ConfigureAwait(false);
            if (page.Artifacts.Count == 0)
            {
                break;
            }

            after = page.Last;
            var records = await ledger.GetRecordsAsync(Flow.Id, page.Artifacts.Select(a => a.Key).Distinct().ToList(), ct).ConfigureAwait(false);
            var requests = page.Artifacts
                .GroupBy(a => a.Key)
                .Where(g => records.TryGetValue(g.Key, out var record) && record.LeaseOwner is null)
                .Select(g =>
                {
                    var record = records[g.Key];
                    var artifacts = g.ToList();

                    // A record with work queued that writes its metadata, as the final hash check will find it, keeps the
                    // record itself, which that work writes again; any other has everything its unfinished units left taken back.
                    return new UndoRequest(record, artifacts, DeliveryWorker.ReasonOf(record, artifacts), KeepRecord: record.HasPendingWork && ChangeDetector.AtPush(record, Flow.Change).Metadata);
                })
                .ToList();
            if (requests.Count == 0)
            {
                continue;
            }

            using var correlation = OsduCorrelation.Begin(FlowTelemetry.Delivery);
            var undos = await runner.RunAsync(requests, correlation.Id, ct).ConfigureAwait(false);
            await ledger.SettleArtifactsAsync(Flow.Id, undos, ct: ct).ConfigureAwait(false);
            var swept = UndoSummary.Of(undos);
            total = total.Add(swept);
            if (swept.Failed > 0)
            {
                _log.LogWarning("{Summary}; the undos that failed are tried again later.", swept.Describe());
            }
        }

        if (!total.Idle)
        {
            _log.LogInformation(RunTrace.Bounded, "Undid what unfinished deliveries left: {Summary}.", total.Describe());
        }

        return total;
    }

    /// <summary>
    /// Undoes, before records are removed or their ledger deleted (osdu/docs/reference/concepts/artifacts-and-undo.md, When
    /// the undo runs, and what it is given), what deliveries of <paramref name="records"/> left in OSDU, through the one
    /// undo path, for each record no worker holds: with <paramref name="open"/>, every open artifact of their unfinished
    /// deliveries, the record itself included; with <paramref name="minted"/>, the ids their committed deliveries minted
    /// (datasets, content, outputs), removed reversibly, since nothing names them once the ledger is gone. Returns what the
    /// undo did, and the records left with an artifact it could not take back yet, which the sweep goes on undoing. A
    /// record the removal itself holds (<paramref name="holder"/>) counts as one no worker holds.
    /// </summary>
    private async Task<(UndoSummary Undone, IReadOnlySet<DeliveryKey> Unsettled)> UndoForRemovalAsync(
        ILedgerArtifacts ledger, IDeliveryProtocol protocol, IReadOnlyList<RecordState> records, bool open, bool minted, string? holder, CancellationToken ct)
    {
        var free = records.Where(r => r.LeaseOwner is null || string.Equals(r.LeaseOwner, holder, StringComparison.Ordinal)).ToDictionary(r => r.DeliveryKey);
        if (!protocol.Undoes || free.Count == 0)
        {
            return (UndoSummary.Empty, new HashSet<DeliveryKey>());
        }

        var artifacts = new List<LedgerArtifact>();
        if (open)
        {
            artifacts.AddRange(await ledger.OpenArtifactsAsync(Flow.Id, free.Keys, ct).ConfigureAwait(false));
        }

        if (minted)
        {
            artifacts.AddRange(await ledger.MintedArtifactsAsync(Flow.Id, free.Keys, ct).ConfigureAwait(false));
        }

        if (artifacts.Count == 0)
        {
            return (UndoSummary.Empty, new HashSet<DeliveryKey>());
        }

        var requests = artifacts
            .GroupBy(a => a.Key)
            .Select(g => new UndoRequest(free[g.Key], g.OrderBy(a => a.ArtifactId).ToList(), UndoReason.Removed, KeepRecord: false))
            .ToList();
        using var correlation = OsduCorrelation.Begin(FlowTelemetry.Delivery);
        var undos = await new UndoRunner(protocol, _context.Time, Truncate(Actor, 200), RunId).RunAsync(requests, correlation.Id, ct).ConfigureAwait(false);
        await ledger.SettleArtifactsAsync(Flow.Id, undos, minted, ct).ConfigureAwait(false);
        var unsettled = undos.Where(u => u.Settlements.Any(s => s.Status == ArtifactStatus.Failed)).Select(u => u.DeliveryKey).ToHashSet();
        var undone = UndoSummary.Of(undos);
        if (undone.Failed > 0)
        {
            _log.LogWarning("{Summary}; the undos that failed are tried again later.", undone.Describe());
        }

        return (undone, unsettled);
    }
}
