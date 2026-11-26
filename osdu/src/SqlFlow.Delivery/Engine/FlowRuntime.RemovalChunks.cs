using System.Data.Common;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

// What the removals do with a chunk of records: one chunk of a removal, or of the removal that opens deleting the
// ledger, taken out of OSDU (or stepped back, or its access changed) and settled in the ledger; the hold a removal
// keeps on the records it takes out, which a removal flow's take-out keeps too (FlowRuntime.SourceRemoval.cs); and the
// events that announce what was done to each record.
public sealed partial class FlowRuntime
{
    /// <summary>
    /// One chunk of a removal (<see cref="RemoveAsync"/>, and the removal that opens <see cref="DeleteLedgerAsync"/>): the
    /// records of <paramref name="chunk"/> OSDU may hold are removed to the extent <paramref name="scope"/> asks, or with
    /// <paramref name="stepBack"/> given the version before the latest back, under one correlation id, and the ledger is settled
    /// for the records the target answered for. A record the flow never wrote to OSDU is reported as skipped. Each record's
    /// result is added to <paramref name="results"/>; returns how many records the extra step deleted from the ledger. A
    /// record in <paramref name="unsettled"/> still has artifacts its unfinished deliveries left that could not be undone, so the
    /// extra step keeps it in the ledger, where the sweep goes on undoing them. A record held under a lease other than the
    /// removal's own (<paramref name="holder"/>) is passed over: a worker is sending it, or another removal has it. With
    /// <paramref name="deliverAgain"/> (an operator's removal) each record removed is asked to be planned again, so the flow's
    /// next run delivers it again; the removal that opens deleting the ledger asks for nothing, since the run after it reads
    /// every row. With <paramref name="access"/>, the records are restricted, or their restrictions lifted, instead.
    /// </summary>
    private async Task<int> RemoveChunkAsync(
        ILedger ledger, IDeliveryProtocol protocol, IReadOnlyList<DeliveryKey> chunk, IReadOnlyDictionary<DeliveryKey, RecordState> records,
        RemovalChoice scope, bool purgeLedger, bool deliverAgain, long? activity, Reversals.PreviousVersionRestore? stepBack, List<RemovalRecordResult> results,
        IReadOnlySet<DeliveryKey> unsettled, string? holder, CancellationToken ct, Restrictions.RecordRestrictions? access = null)
    {
        var removals = new List<RecordRemoval>(chunk.Count);
        foreach (var key in chunk)
        {
            if (!records.TryGetValue(key, out var record))
            {
                results.Add(RemovalRecordResult.Skipped(key, null, null, null, "the record is not in this flow's ledger", null));
            }
            else if (stepBack is null && record.LeaseOwner is not null && !string.Equals(record.LeaseOwner, holder, StringComparison.Ordinal))
            {
                // A worker is sending it now: removing it under that send would leave OSDU holding whichever call landed
                // last, and the ledger saying otherwise. A step back passes such a record over itself, saying why.
                results.Add(RemovalRecordResult.Skipped(key, record.SourceKey, record.Label, null, HeldElsewhere, record.LastSubmissionId));
            }
            else if (record.TargetId is null)
            {
                results.Add(RemovalRecordResult.Skipped(key, record.SourceKey, record.Label, null, "the record has no OSDU id: it was never delivered", record.LastSubmissionId));
            }
            else if (!string.Equals(record.ClaimedTargetId, record.TargetId, StringComparison.Ordinal))
            {
                // Only an id the flow claimed is the flow's to remove: a record that was only ever held never
                // wrote to OSDU, and the id it names can be another flow's record.
                results.Add(RemovalRecordResult.Skipped(
                    key, record.SourceKey, record.Label, null,
                    "the record never queued a document, so this flow wrote nothing to OSDU to remove", record.LastSubmissionId));
            }
            else
            {
                removals.Add(new RecordRemoval(key, record.TargetId, JsonMerge.ToValues(record.TargetStateJson)));
            }
        }

        if (removals.Count == 0)
        {
            return 0;
        }

        // The run's correlation id, which the chunk's calls carry and each removal attempt names, so a removal can be followed
        // into OSDU's own logs like a delivery.
        using var correlation = OsduCorrelation.Begin(FlowTelemetry.Delivery);
        if (stepBack is not null)
        {
            var stepped = await stepBack.RestoreAsync(removals.Select(r => records[r.Key]).ToList(), correlation.Id, ct).ConfigureAwait(false);
            for (var i = 0; i < removals.Count; i++)
            {
                results.Add(await AnnounceRestoreAsync(stepped[i], records[removals[i].Key], ct).ConfigureAwait(false));
            }

            return 0;
        }

        if (access is not null)
        {
            var chosen = removals.Select(r => records[r.Key]).ToList();
            var changed = access.Lifting
                ? await access.LiftAsync(chosen, holder, correlation.Id, ct).ConfigureAwait(false)
                : await access.RestrictAsync(chosen, holder, correlation.Id, ct).ConfigureAwait(false);
            for (var i = 0; i < chosen.Count; i++)
            {
                results.Add(await AnnounceAccessAsync(changed[i], chosen[i], ct).ConfigureAwait(false));
            }

            return 0;
        }

        var outcomes = await protocol.DeleteBatchAsync(removals, scope.Scope(), ct).ConfigureAwait(false);

        // The ledger settles the whole chunk in one write, and only for the records the target actually
        // answered for: a record whose call failed keeps the state it had, so a retry of the removal is
        // still the removal of a record that is still there.
        var settled = outcomes.Where(o => o.Succeeded).Select(o => o.Removal.Key).ToList();
        await ledger.MarkRemovedAsync(Flow.Id, settled, scope.Scope(), Actor, _context.Time.GetUtcNow().UtcDateTime, correlation.Id, deliverAgain, ct: ct).ConfigureAwait(false);

        // The extra step: what OSDU no longer holds goes from the ledger too, one line of each kept; a record whose unfinished
        // deliveries left something the undo could not take back stays, so nothing in OSDU loses the record that names it.
        var purgeable = settled.Where(k => !unsettled.Contains(k)).ToList();
        var gone = purgeLedger && purgeable.Count > 0
            ? (await ledger.PurgeRecordsAsync(Flow.Id, purgeable, Actor, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false)).ToHashSet()
            : [];
        foreach (var outcome in outcomes)
        {
            var result = await AnnounceRemovalAsync(outcome, scope.Scope(), records[outcome.Removal.Key], ct).ConfigureAwait(false);
            results.Add(gone.Contains(outcome.Removal.Key) ? result with { Detail = result.Detail + "; deleted from the ledger" }
                : purgeLedger && outcome.Succeeded && unsettled.Contains(outcome.Removal.Key)
                    ? result with { Detail = result.Detail + "; kept in the ledger, since what its unfinished deliveries left could not all be undone yet" }
                    : purgeLedger && outcome.Succeeded && records[outcome.Removal.Key].Restricted
                        ? result with { Detail = result.Detail + "; kept in the ledger, since its access is restricted: lift the restriction to delete it from the ledger" }
                        : result);
        }

        return gone.Count;
    }

    /// <summary>
    /// Runs <paramref name="remove"/> while the records of <paramref name="keys"/> no lease holds are held for a removal
    /// (<see cref="ILedgerLeases.HoldForRemovalAsync"/>), handing it the hold's token (null when it holds nothing): no
    /// worker claims one of them, so OSDU never ends up holding what a worker sent under the removal while the ledger
    /// says removed. The hold is renewed every half of the flow's lease while the removal goes on, and let go when it
    /// ends, whatever its outcome; a removal that stops lets its records go once that lease runs out.
    /// </summary>
    private async Task<T> UnderRemovalHoldAsync<T>(ILedgerLeases ledger, IReadOnlyCollection<DeliveryKey> keys, Func<string?, Task<T>> remove, CancellationToken ct)
    {
        var length = TimeSpan.FromSeconds(Math.Max(Flow.Reliability.LeaseSeconds, 1));
        var hold = await ledger.HoldForRemovalAsync(Flow.Id, keys, RemovalOwner, length, _context.Time.GetUtcNow().UtcDateTime, RunId, ct).ConfigureAwait(false);
        if (hold.Token is not { } token)
        {
            return await remove(null).ConfigureAwait(false);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var keeping = KeepRemovalHoldAsync(ledger, token, length, stop.Token);
        try
        {
            return await remove(token).ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await keeping.ConfigureAwait(false);
            try
            {
                await ledger.CloseLeaseAsync(token, new LeaseClosing { End = LeaseEnd.Done }, _context.Time.GetUtcNow().UtcDateTime, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException or DeliveryException)
            {
                _log.LogWarning(
                    "Could not let go of the records the removal held ({Message}); its lease runs out within {Seconds}s and the flow's next claim lets them go.",
                    ex.Message, (int)length.TotalSeconds);
            }
        }
    }

    /// <summary>Renews a removal's hold every half lease until <paramref name="ct"/> says the removal ended.</summary>
    private async Task KeepRemovalHoldAsync(ILedgerLeases ledger, string token, TimeSpan length, CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(length.TotalMilliseconds / 2, 1000));
        try
        {
            while (true)
            {
                await Task.Delay(interval, _context.Time, ct).ConfigureAwait(false);
                try
                {
                    if (!await ledger.RenewLeaseAsync(token, length, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false))
                    {
                        _log.LogWarning(
                            "The removal's hold on its records ran out before it was renewed, so a worker may claim one of them while it is removed; each record's history shows what was done to it.");
                        return;
                    }
                }
                catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException)
                {
                    _log.LogWarning("Renewing the removal's hold on its records failed ({Message}); trying again in {Seconds}s.", ex.Message, (int)interval.TotalSeconds);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The removal ended, and its hold is let go by the caller: stopping here is how renewing ends.
            return;
        }
    }

    /// <summary>Announces one record's removal, now that the ledger holds it, and says what it did.</summary>
    private async Task<RemovalRecordResult> AnnounceRemovalAsync(RemovalResult outcome, RemovalScope scope, RecordState record, CancellationToken ct)
    {
        var key = outcome.Removal.Key;
        if (outcome.Failure is { } failure)
        {
            return RemovalRecordResult.Failed(key, record.SourceKey, record.Label, record.TargetId, HeaderRedaction.RedactMessage(failure.Message), record.LastSubmissionId);
        }

        var result = outcome.Outcome!;
        var now = _context.Time.GetUtcNow().UtcDateTime;
        await AnnounceAsync(new DeliveryEvent
        {
            AtUtc = now,
            FlowId = Flow.Id,
            FlowName = Flow.Label,
            Interface = Flow.Interface,
            Kind = scope == RemovalScope.History ? "record.history-purged" : "record.deleted",
            SubmissionId = record.LastSubmissionId,
            DeliveryKey = key,
            SourceKey = record.SourceKey,
            Label = record.Label,
            TargetId = record.TargetId,
            TargetVersion = record.TargetVersion,
            Worker = Actor,
            Phase = scope == RemovalScope.History ? "purge-history" : "delete",
            Detail = result.Detail,
        }, ct).ConfigureAwait(false);

        return result.AlreadyGone
            ? RemovalRecordResult.AlreadyGone(key, record.SourceKey, record.Label, record.TargetId, result.Detail, record.LastSubmissionId)
            : RemovalRecordResult.Removed(key, record.SourceKey, record.Label, record.TargetId, result.Detail, record.LastSubmissionId);
    }

    /// <summary>Announces one record whose access was restricted, or whose restriction was lifted, now that the ledger holds it.</summary>
    private async Task<RemovalRecordResult> AnnounceAccessAsync(RemovalRecordResult result, RecordState record, CancellationToken ct)
    {
        if (result.Outcome is not ("restricted" or "lifted"))
        {
            return result;
        }

        var restricted = result.Outcome == "restricted";
        await AnnounceAsync(new DeliveryEvent
        {
            AtUtc = _context.Time.GetUtcNow().UtcDateTime,
            FlowId = Flow.Id,
            FlowName = Flow.Label,
            Interface = Flow.Interface,
            Kind = restricted ? "record.restricted" : "record.unrestricted",
            SubmissionId = record.LastSubmissionId,
            DeliveryKey = record.DeliveryKey,
            SourceKey = record.SourceKey,
            Label = record.Label,
            TargetId = record.TargetId,
            TargetVersion = record.TargetVersion,
            Worker = Actor,
            Phase = restricted ? AttemptPhases.Restrict : AttemptPhases.LiftRestriction,
            Detail = result.Detail,
        }, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Announces one record written back at its version before the latest, now that the ledger holds it, and says what it did.</summary>
    private async Task<RemovalRecordResult> AnnounceRestoreAsync(Reversals.PreviousVersionStep step, RecordState record, CancellationToken ct)
    {
        if (step.Restored is not { } restored)
        {
            return step.Result;
        }

        await AnnounceAsync(new DeliveryEvent
        {
            AtUtc = _context.Time.GetUtcNow().UtcDateTime,
            FlowId = Flow.Id,
            FlowName = Flow.Label,
            Interface = Flow.Interface,
            Kind = "record.restored",
            SubmissionId = record.LastSubmissionId,
            DeliveryKey = record.DeliveryKey,
            SourceKey = record.SourceKey,
            Label = record.Label,
            TargetId = record.TargetId,
            TargetVersion = restored.NewVersion,
            Worker = Actor,
            Phase = AttemptPhases.RestorePrevious,
            Detail = step.Result.Detail,
        }, ct).ConfigureAwait(false);
        return step.Result;
    }
}
