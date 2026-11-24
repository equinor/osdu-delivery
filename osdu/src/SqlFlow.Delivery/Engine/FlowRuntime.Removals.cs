using System.Globalization;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

// Changing what OSDU holds of the records the flow delivered (osdu/docs/reference/concepts/removal-and-reversal.md,
// osdu/docs/reference/concepts/access-restriction.md): a removal of the records a selection names, a step back to
// the version before the latest, a restriction of their access or its lift, and the reversal of a run or a submission.
public sealed partial class FlowRuntime
{
    /// <summary>
    /// Removes records from OSDU through the flow's protocol, to the extent the choice asks for, and records what
    /// happened to each one in the ledger. One record and ten thousand take the same path: the selection is
    /// resolved to keys, the records are removed in chunks (batched into one request where the protocol and the
    /// scope allow it), and every record gets its own ledger attempt naming the scope and the operator. A record
    /// the flow never delivered has nothing in OSDU to remove and is reported as skipped rather than failing the
    /// removal; a record OSDU has already lost settles the ledger just as a removal would, because the state the
    /// operator asked for is the state OSDU is in. <see cref="RemovalChoice.Previous"/> takes the same path to the same
    /// records and, in place of a delete, writes the version before the latest back
    /// (<see cref="Reversals.PreviousVersionRestore"/>), under an activity of its own, since it removes nothing. With
    /// <paramref name="purgeLedger"/>, a removal that takes the record out of OSDU (<see cref="RemovalChoice.Record"/> or
    /// <see cref="RemovalChoice.Everything"/>) also deletes each record OSDU answered for from the ledger, keeping one line
    /// of it (<see cref="ILedgerRemovals.PurgeRecordsAsync"/>); a record whose removal failed stays in the ledger as it
    /// was. Either way each record removed is asked for again, on its record or on its line, so the flow's next run
    /// reads its row by key and delivers it again: what the source holds and OSDU does not comes back.
    /// <see cref="RemovalChoice.Restrict"/> and <see cref="RemovalChoice.Unrestrict"/> take the same path too,
    /// replacing each record's ACL in OSDU with the registered access restriction <paramref name="restriction"/> names,
    /// or giving it back the ACL it had (<see cref="Restrictions.RecordRestrictions"/>;
    /// osdu/docs/reference/concepts/access-restriction.md); before anything is restricted, the flow's identity is
    /// checked to be able to lift it again.
    /// </summary>
    public Task<RemovalSummary> RemoveAsync(RemovalSelection selection, RemovalChoice scope, bool purgeLedger = false, string? restriction = null, CancellationToken ct = default)
    {
        // A selection by filter is resolved here, when the removal runs: whoever queued it (the API, which counted it, or a run
        // triggered by hand, which did not), what it matches now is what it removes, and never a part of it.
        ArgumentNullException.ThrowIfNull(selection);
        if (purgeLedger && scope is not (RemovalChoice.Record or RemovalChoice.Everything))
        {
            throw new DeliveryException(
                $"A record is deleted from the ledger only with a removal that takes it out of OSDU (record or everything), not with {Operations.RemovalScopes.Wire(scope)}.");
        }

        if (scope == RemovalChoice.Restrict && string.IsNullOrWhiteSpace(restriction))
        {
            throw new DeliveryException("A restriction names the registered access restriction whose groups the records' ACL becomes, and this one names none.");
        }

        if (scope != RemovalChoice.Restrict && restriction is not null)
        {
            throw new DeliveryException($"Only a restriction names an access restriction, not a removal of scope {Operations.RemovalScopes.Wire(scope)}.");
        }

        var single = selection.Keys is { Count: 1 } only ? only[0] : (DeliveryKey?)null;
        var kind = scope switch
        {
            RemovalChoice.Previous => AttemptPhases.RestorePrevious,
            RemovalChoice.Restrict => AttemptPhases.Restrict,
            RemovalChoice.Unrestrict => AttemptPhases.LiftRestriction,
            _ => "delete",
        };
        return TrackAsync(kind, selection.Describe(scope, purgeLedger, restriction?.Trim()), single, async activity =>
        {
            var removalStarted = _context.Time.GetUtcNow().UtcDateTime;
            var ledger = RequireLedger();
            var keys = selection.Keys ?? await FilteredKeysAsync(ledger, selection.Filter!, ct).ConfigureAwait(false);
            var protocol = await ProtocolAsync(ct).ConfigureAwait(false);

            // A lease that ran out is settled first, so the only records a worker holds below are ones it is sending now.
            await ledger.RecoverExpiredLeasesAsync(Flow.Id, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            var stepBack = scope == RemovalChoice.Previous
                ? new Reversals.PreviousVersionRestore(Flow, ledger, protocol, _context.Time, Actor, RunId)
                : null;
            var access = scope.ChangesAccess()
                ? new Restrictions.RecordRestrictions(Flow, ledger, protocol, _context.Time, Actor, RunId, await RestrictionToSetAsync(ledger, protocol, scope, restriction, ct).ConfigureAwait(false))
                : null;
            var purged = 0;
            var undone = UndoSummary.Empty;

            // A page of records at a time, as many pages at once as the flow sends records at once; a step back and an access
            // change go a page at a time, since each keeps what it learns of the routes it writes through for the next page.
            var lanes = stepBack is null && access is null ? RemovalLanes : 1;
            var size = RemovalPageSize(keys.Count, lanes, SmallestRemovalPage);
            var pageResults = new List<RemovalRecordResult>[(keys.Count + size - 1) / size];
            var counting = new object();
            await ForEachPageAsync(PagesOf(keys, size), lanes, async (page, token) =>
            {
                var (index, chunk) = page;
                var mine = new List<RemovalRecordResult>(chunk.Length);
                var pageUndone = UndoSummary.Empty;

                // The page's records are held while they are taken out of OSDU, so no worker sends one meanwhile. A step back
                // holds none: it passes over a record with work queued or under a lease, and writes only a version OSDU held.
                var pagePurged = await UnderRemovalHoldAsync(ledger, stepBack is null ? chunk : [], async holder =>
                {
                    var records = await ledger.GetRecordsAsync(Flow.Id, chunk, token).ConfigureAwait(false);

                    // What unfinished deliveries of these records left is undone first, through the one undo path, so the
                    // removal never leaves behind a dataset, session or version no record names.
                    var (chunkUndone, unsettled) = await UndoForRemovalAsync(ledger, protocol, records.Values.ToList(), open: true, minted: false, holder, token).ConfigureAwait(false);
                    pageUndone = chunkUndone;
                    return await RemoveChunkAsync(
                        ledger, protocol, chunk, records, scope, purgeLedger, deliverAgain: true, activity, stepBack, mine, unsettled, holder, token, access).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
                pageResults[index] = mine;
                lock (counting)
                {
                    purged += pagePurged;
                    undone = undone.Add(pageUndone);
                }
            }, ct).ConfigureAwait(false);

            // Each record's result in the order the records were named, whichever page finished first.
            var results = pageResults.SelectMany(r => r ?? []).ToList();

            // What the removal took out of OSDU, or stepped back, leaves the cache types the pipeline puts its records into. A
            // record deleted from the ledger as well leaves no row to tell it, so its id is handed to the step.
            var purgedIds = purgeLedger
                ? results.Where(r => r.Outcome is "removed" or "already-gone" && r.TargetId is not null).Select(r => r.TargetId!).ToList()
                : null;
            var summary = RemovalSummary.Of(scope, keys.Count, results, purged) with
            {
                Undone = undone,
                CacheAfter = await PutIntoCacheAsync(removalStarted, purgedIds, ct).ConfigureAwait(false),
                Restriction = restriction?.Trim(),
            };
            return (summary, summary.Describe(), results.Count == 1 ? results[0].SubmissionId : null, false);
        }, ct);
    }

    /// <summary>
    /// The registered access restriction a restriction of the records sets (osdu/docs/reference/concepts/access-restriction.md),
    /// found in the partition the ledger is kept in, once the flow's identity is checked to be able to lift it again; null for a
    /// lift, which gives each record back the ACL it had.
    /// </summary>
    private async Task<AccessRestrictionState?> RestrictionToSetAsync(ILedger ledger, IDeliveryProtocol protocol, RemovalChoice scope, string? restriction, CancellationToken ct)
    {
        if (scope != RemovalChoice.Restrict)
        {
            return null;
        }

        var partition = _ledgerPartition ?? throw new InvalidOperationException("The ledger is registered before an intervention's action runs, which names its partition.");
        var registered = await ledger.FindAccessRestrictionAsync(partition, restriction!, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(
                $"No access restriction named '{restriction!.Trim()}' is registered for partition '{partition}', so nothing was restricted. Register it under Access restrictions, or name one that is.");
        await Restrictions.RecordRestrictions.EnsureLiftableAsync(Flow, protocol, registered, ct).ConfigureAwait(false);
        return registered;
    }

    /// <summary>
    /// The records a removal's listing filter matches now, as the listing counts them. A filter that matches more than one
    /// removal takes is refused, as the API refuses it, so a removal never takes the part of what it matches that the listing's
    /// order happens to put first.
    /// </summary>
    private async Task<IReadOnlyList<DeliveryKey>> FilteredKeysAsync(ILedger ledger, RecordQuery filter, CancellationToken ct)
    {
        var matched = await ledger.CountAsync(Flow.Id, filter, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false);
        if (!matched.Exact || matched.Count > RemovalLimits.MaxSelection)
        {
            throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"The filter matches more than {RemovalLimits.MaxSelection} records as far as the listing counts; a removal takes at most {RemovalLimits.MaxSelection} at a time. Narrow the filter and remove in parts."));
        }

        return await ledger.ListKeysAsync(Flow.Id, filter, RemovalLimits.MaxSelection, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reverses what <paramref name="source"/> delivered (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a
    /// run or a submission): opens or resumes its reversal, lists every record it delivered, and puts each back as OSDU
    /// held it before, through the flow's protocol, every step written to the ledger under this run's activity. Resumable:
    /// a reversal stopped anywhere is finished by running it again.
    /// </summary>
    public Task<Reversals.ReversalSummary> ReverseAsync(ReversalSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return TrackAsync("reverse", new { source = source.Kind, sourceId = source.Id }, null, async activity =>
        {
            var reversalStarted = _context.Time.GetUtcNow().UtcDateTime;
            var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
            var runner = new Reversals.ReversalRunner(Flow, RequireLedger(), protocol, _context.Time, _log, _context.Trace, Actor, RunId);
            var summary = await runner.RunAsync(source, activity, ct).ConfigureAwait(false);

            // What the reversal restored or removed in OSDU goes into the cache types the pipeline puts its records into.
            summary = summary with { CacheAfter = await PutIntoCacheAsync(reversalStarted, ct).ConfigureAwait(false) };
            return (summary, summary.Describe(source), source.IsRun ? (Guid?)null : source.Id, false);
        }, ct);
    }
}
