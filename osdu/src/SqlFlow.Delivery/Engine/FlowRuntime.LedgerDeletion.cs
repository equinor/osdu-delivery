using System.Globalization;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

// Deleting from the ledger (osdu/docs/reference/concepts/removal-and-reversal.md): the records already removed from
// OSDU, asking nothing of OSDU, and the whole ledger, once every record it holds is out of OSDU.
public sealed partial class FlowRuntime
{
    /// <summary>
    /// Deletes records already removed from OSDU from the ledger (osdu/docs/reference/concepts/removal-and-reversal.md,
    /// Deleting removed records from the ledger), asking nothing of OSDU: the named records, or with null keys every
    /// record the ledger marks deleted, a page at a time. Only a record the ledger marks deleted goes, none a lease
    /// holds, and none whose unfinished deliveries left something the undo has not taken back yet; any other named
    /// record is left as it is and counted as such. Each record deleted keeps one line and is named under this
    /// intervention's activity, the same way the removal's extra step deletes one
    /// (<see cref="ILedgerRemovals.PurgeRecordsAsync"/>), and its line asks for its row to be read again, so the flow's
    /// next run delivers it as a new record.
    /// </summary>
    public Task<LedgerPurgeSummary> PurgeFromLedgerAsync(IReadOnlyList<DeliveryKey>? keys, CancellationToken ct = default)
        => TrackAsync(
            "purge",
            keys is null
                ? new { every = (string?)"deleted", records = (int?)null, keys = (List<string>?)null }
                : new { every = (string?)null, records = (int?)keys.Count, keys = (List<string>?)keys.Take(20).Select(k => k.ToString()).ToList() },
            keys is { Count: 1 } ? keys[0] : null,
            async activity =>
            {
                var ledger = RequireLedger();
                var now = _context.Time.GetUtcNow().UtcDateTime;
                LedgerPurgeSummary summary;
                if (keys is not null)
                {
                    var purged = await ledger.PurgeRecordsAsync(Flow.Id, keys.Distinct().ToList(), Actor, activity, now, ct).ConfigureAwait(false);
                    summary = new LedgerPurgeSummary(keys.Distinct().Count(), purged.Count);
                }
                else
                {
                    // Every record the ledger marks deleted, a selection's worth at a time. A record a page leaves (a lease holds it,
                    // or an undo has still to settle what its deliveries left) comes back on the next page, so the walk goes on while
                    // a page deletes something and ends on one that deletes nothing: it ends however the ledger moves underneath it,
                    // and each record is counted once.
                    var seen = new HashSet<DeliveryKey>();
                    var total = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        var page = await ledger.ListKeysAsync(Flow.Id, new RecordQuery { Status = RecordStatus.Deleted }, RemovalLimits.MaxSelection, ct).ConfigureAwait(false);
                        if (page.Count == 0)
                        {
                            break;
                        }

                        var purged = await ledger.PurgeRecordsAsync(Flow.Id, page, Actor, activity, now, ct).ConfigureAwait(false);
                        seen.UnionWith(page);
                        total += purged.Count;
                        if (purged.Count == 0 || page.Count < RemovalLimits.MaxSelection)
                        {
                            break;
                        }
                    }

                    summary = new LedgerPurgeSummary(seen.Count, total);
                }

                return (summary, summary.Describe(), (Guid?)null, false);
            },
            ct);

    /// <summary>
    /// Deletes the ledger (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting the ledger). Nothing happens
    /// unless <paramref name="confirm"/> names the partition the ledger is kept in, and no worker holds a lease on its
    /// records. Then every record OSDU may hold is removed from it first, reversibly (the record scope), through the
    /// removal a selection takes, a page of the ledger at a time in key order and <see cref="RemovalLanes"/> pages at once;
    /// a record the ledger already marks removed is not asked about again. Only when OSDU answered for every one, whatever
    /// order the pages ended in, is the ledger deleted whole
    /// (<see cref="ILedgerRemovals.DeleteLedgerAsync"/>): its records, whatever their state, each kept as one line, and
    /// what it keeps of its runs, so the next run reads every row and delivers each as a new record. Before a page's
    /// records are removed, what their unfinished deliveries left is undone; once a record is out of OSDU, the
    /// datasets, content and outputs its committed deliveries minted are removed reversibly too, since nothing would
    /// name them afterwards. A removal OSDU refused, or an undo it could not take, leaves the ledger as it was, with
    /// the records it did remove marked removed, and fails naming them: OSDU never holds a record or an id the ledger
    /// forgot. A route with no reversible removal is refused outright.
    /// </summary>
    public Task<LedgerDeleteSummary> DeleteLedgerAsync(string confirm, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirm);
        if (Flow.Target.Protocol is DeliveryProtocol.Dspdm or DeliveryProtocol.Etp)
        {
            throw new DeliveryException(
                $"'{Flow.Label}' is delivered by the {DeliveryProtocols.Name(Flow.Target.Protocol)} route, which keeps nothing it deletes, so its records have no reversible removal; deleting the ledger removes every record from OSDU reversibly first, and is refused here.");
        }

        return TrackAsync(DeliveryOperations.DeleteLedger, new { confirm }, null, async activity =>
        {
            var ledger = RequireLedger();
            var partition = _ledgerPartition ?? throw new InvalidOperationException("The ledger is registered before an intervention's action runs, which names its partition.");
            if (!string.Equals(confirm.Trim(), partition, StringComparison.OrdinalIgnoreCase))
            {
                throw new DeliveryException(
                    $"The run names partition '{confirm.Trim()}' to confirm deleting the ledger of '{Flow.Label}', which is kept in partition '{partition}', so nothing was removed or deleted. Name '{partition}' to delete it.");
            }

            // A record whose access is restricted is never forgotten: deleting the ledger would deliver it again, open to everyone.
            if (await ledger.CountRestrictedAsync(Flow.Id, ct).ConfigureAwait(false) is > 0 and var restricted)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{restricted} record(s) of '{Flow.Label}' have their access restricted in OSDU, so nothing was removed or deleted: deleting the ledger would forget them, and the next run would deliver them again under the ACL their mapping gives. Lift their restrictions first (Remove, Lift the restriction), then delete the ledger."));
            }

            var now = _context.Time.GetUtcNow().UtcDateTime;
            await ledger.RecoverExpiredLeasesAsync(Flow.Id, now, ct).ConfigureAwait(false);
            if (await ledger.NextLeaseExpiryAsync(Flow.Id, null, ct).ConfigureAwait(false) is { } leased)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A worker holds a lease on records of '{Flow.Label}' until {leased:u}, so nothing was removed or deleted: work is in flight on this ledger. Delete it once that work has ended."));
            }

            var protocol = await ProtocolAsync(ct).ConfigureAwait(false);
            var tally = new RemovalTally();
            var undone = UndoSummary.Empty;
            var counting = new object();

            // A page of the ledger at a time in the order of its key, as many pages at once as the flow sends records at once,
            // each page as large as shares the ledger out over them (counted no further than fills every lane).
            var lanes = RemovalLanes;
            var counted = await ledger.CountAsync(Flow.Id, new RecordQuery(), lanes * RemovalLimits.Chunk, ct).ConfigureAwait(false);
            await ForEachPageAsync(LedgerPagesAsync(ledger, RemovalPageSize(counted.Count, lanes, SmallestRemovalPage), ct), lanes, async (pageKeys, token) =>
            {
                // The page's records are held while they are taken out of OSDU, so no run that started since sends one meanwhile;
                // one a worker holds already is refused below, and the ledger is kept.
                await UnderRemovalHoldAsync(ledger, pageKeys, async holder =>
                {
                    var read = await ledger.GetRecordsAsync(Flow.Id, pageKeys, token).ConfigureAwait(false);
                    var current = pageKeys.Where(read.ContainsKey).Select(k => read[k]).ToList();

                    // What the page's unfinished deliveries left, a removed record's included, is undone before anything is removed.
                    var opened = (await UndoForRemovalAsync(ledger, protocol, current, open: true, minted: false, holder, token).ConfigureAwait(false)).Undone;
                    var held = current.Where(r => r.Status != RecordStatus.Deleted).ToList();
                    var results = new List<RemovalRecordResult>(held.Count);
                    if (held.Count > 0)
                    {
                        await RemoveChunkAsync(
                            ledger, protocol, held.Select(r => r.DeliveryKey).ToList(), held.ToDictionary(r => r.DeliveryKey), RemovalChoice.Record,
                            purgeLedger: false, deliverAgain: false, activity, stepBack: null, results, new HashSet<DeliveryKey>(), holder, token).ConfigureAwait(false);
                    }

                    // Once a record is out of OSDU, the ids its committed deliveries minted are removed reversibly with it.
                    var outOfOsdu = results.Where(r => r.Outcome is "removed" or "already-gone").Select(r => new DeliveryKey(r.DeliveryKey)).ToHashSet();
                    var retired = current.Where(r => r.Status == RecordStatus.Deleted || outOfOsdu.Contains(r.DeliveryKey)).ToList();
                    var minted = (await UndoForRemovalAsync(ledger, protocol, retired, open: false, minted: true, holder, token).ConfigureAwait(false)).Undone;
                    lock (counting)
                    {
                        tally.AlreadyRemoved += current.Count - held.Count;
                        tally.Add(results);
                        undone = undone.Add(opened).Add(minted);
                    }

                    return 0;
                }, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if (tally.Failed > 0)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{tally.Failed} record(s) of '{Flow.Label}' could not be removed from OSDU, so the ledger was kept as it was and OSDU holds nothing it forgot: {tally.Failures}. The {tally.Removed + tally.AlreadyGone} record(s) OSDU answered for are marked removed; delete the ledger again once OSDU removes the rest."));
            }

            // An id the undo could not take back keeps the ledger too: the ledger is the one place that names it.
            var left = await ledger.ArtifactCountsAsync(Flow.Id, ct).ConfigureAwait(false);
            if (left.ToUndo > 0)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{left.ToUndo} item(s) the deliveries of '{Flow.Label}' left in OSDU could not be undone ({undone.Describe()}), so the ledger was kept as it was and OSDU holds nothing it forgot. Every record OSDU answered for is marked removed; the sweep goes on undoing the rest, and the ledger can be deleted again once it has."));
            }

            // Every record is marked removed now, and the ledger still names them: what OSDU no longer holds leaves the cache types
            // the pipeline puts its records into before the ledger is deleted.
            var cacheAfter = await PutIntoCacheAsync(now, ct).ConfigureAwait(false);
            var deleted = await ledger.DeleteLedgerAsync(Flow.Id, Actor, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            var summary = new LedgerDeleteSummary(partition, tally.Removed, tally.AlreadyGone, tally.AlreadyRemoved, tally.Skipped, deleted)
            {
                Undone = undone,
                CacheAfter = cacheAfter,
            };
            return (summary, summary.Describe(), (Guid?)null, false);
        }, ct);
    }

    /// <summary>What the removal opening <see cref="DeleteLedgerAsync"/> did, counted as it walks the ledger, and its first refusals.</summary>
    private sealed class RemovalTally
    {
        private const int Named = 3;

        private readonly List<string> _failures = [];

        public int Removed { get; private set; }

        public int AlreadyGone { get; private set; }

        public int AlreadyRemoved { get; set; }

        public int Skipped { get; private set; }

        public int Failed { get; private set; }

        /// <summary>The first refusals, each with its record and why, and how many more there were.</summary>
        public string Failures => string.Join("; ", _failures) + (Failed > _failures.Count ? string.Create(CultureInfo.InvariantCulture, $"; and {Failed - _failures.Count} more") : string.Empty);

        public void Add(IEnumerable<RemovalRecordResult> results)
        {
            foreach (var result in results)
            {
                switch (result.Outcome)
                {
                    case "removed":
                        Removed++;
                        break;
                    case "already-gone":
                        AlreadyGone++;
                        break;
                    case "skipped" when !string.Equals(result.Detail, HeldElsewhere, StringComparison.Ordinal):
                        Skipped++;
                        break;
                    default:
                        Failed++;
                        if (_failures.Count < Named)
                        {
                            _failures.Add($"{result.TargetId ?? result.SourceKey ?? result.DeliveryKey.ToString()}: {result.Detail}");
                        }

                        break;
                }
            }
        }
    }
}
