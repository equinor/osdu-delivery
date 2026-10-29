using System.Collections.Concurrent;
using System.Globalization;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

/// <summary>What a removal flow hands the delivery flow's runtime to take out the ledger's pending deletions.</summary>
public sealed record SourceRemovalWork
{
    /// <summary>The removal run doing the work: the removal flow, its removal and run. Each key's own why is its deletion's.</summary>
    public required SourceRemovalNote Note { get; init; }

    /// <summary>Whether a record other records in OSDU still name stays there until they are gone.</summary>
    public required bool KeepReferenced { get; init; }

    /// <summary>What the activity names as the request: the removal flow, its removal and run, and an approval.</summary>
    public required object Parameters { get; init; }

    /// <summary>OSDU ids taken out earlier in the same removal run (its removals before this one), which no longer count as naming a record.</summary>
    public IReadOnlySet<string> RemovedBefore { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>What taking out the pending deletions of one delivery flow's ledger came to.</summary>
public sealed record SourceRemovalSummary
{
    public required Ledger.RemovalTally Tally { get; init; }

    public required UndoSummary Undone { get; init; }

    public CacheStepOutcome? CacheAfter { get; init; }

    /// <summary>The activity the work is recorded under in the delivery flow's ledger.</summary>
    public long? ActivityId { get; init; }

    /// <summary>The OSDU ids taken out, which later removals of the same run do not count as naming a record.</summary>
    public required IReadOnlySet<string> RemovedIds { get; init; }

    /// <summary>The summary as the audit trail shows it.</summary>
    public string Describe()
    {
        var t = Tally;
        var parts = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"{t.Removed} removed from OSDU"),
            string.Create(CultureInfo.InvariantCulture, $"{t.Settled} settled in the ledger"),
        };

        void Add(long count, string what)
        {
            if (count > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{count} {what}"));
            }
        }

        Add(t.KeptOut, "kept out (never delivered)");
        Add(t.AlreadyGone, "already gone");
        Add(t.Referenced, "left in OSDU since other records name them");
        Add(t.Shared, "left in OSDU since another ledger holds them");
        Add(t.Busy, "busy, taken by the next run");
        Add(t.Failed, "failed");
        return "the source no longer holds them: " + string.Join(", ", parts);
    }
}

public sealed partial class FlowRuntime
{
    /// <summary>The most records that name an id one reference check reads.</summary>
    private const int ReferencesShown = 25;

    /// <summary>How many reference checks of one chunk are asked at once.</summary>
    private const int ReferenceChecksAtOnce = 8;

    /// <summary>
    /// Registers this runtime's ledger in the partition it delivers to, as every operation does before it writes a row of it:
    /// what a removal flow does before it opens a run under the ledger.
    /// </summary>
    public async Task EnsureLedgerAsync(CancellationToken ct = default)
    {
        var ledger = RequireLedger();
        await RegisterLedgerAsync(ledger, ct).ConfigureAwait(false);
    }

    /// <summary>The most keys one lift names.</summary>
    public const int MaxLift = 1000;

    /// <summary>
    /// Lifts, as an operator asked, the ledger's deletions of <paramref name="keys"/> (osdu/docs/reference/flow/removal.md,
    /// Delivering a record again): each key is no longer kept out, and a record the removal took out is asked to be planned
    /// again, so the flow's next run reads its row and delivers it under its id. Recorded as one <c>lift-removal</c> activity
    /// naming every record lifted. Returns the keys lifted; a key the ledger keeps out for no removal is left out of it. While the
    /// source still does not hold a key, the removal flow's next run finds it removed again.
    /// </summary>
    /// <exception cref="DeliveryException">No key, or more than <see cref="MaxLift"/>, is named.</exception>
    public Task<IReadOnlySet<DeliveryKey>> LiftRemovalsAsync(IReadOnlyList<DeliveryKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count is 0 or > MaxLift)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"A lift names from 1 to {MaxLift} records; {keys.Count} were named."));
        }

        // The ledger names every record lifted under the activity; the request names them itself while they are few.
        var parameters = new { count = keys.Count, keys = keys.Count <= 20 ? keys.Select(k => k.ToString()).ToList() : null };
        return TrackAsync(SourceRemovals.LiftActivityKind, parameters, keys.Count == 1 ? keys[0] : null, async (long? activity) =>
        {
            var lifted = await RequireLedger().LiftRemovedKeysAsync(Flow.Id, keys, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            if (lifted.Count > 0)
            {
                await EmitAsync("record.removal-lifted", [.. lifted], $"removal lifted by {Actor}: the flow's next run delivers the records again", ct).ConfigureAwait(false);
            }

            var summary = lifted.Count == 0
                ? "the ledger keeps none of the keys out for a removal"
                : string.Create(CultureInfo.InvariantCulture, $"lifted the removal of {lifted.Count} record(s): the flow's next run delivers them again");
            return (lifted, summary, (Guid?)null, lifted.Count == 0);
        }, ct);
    }

    /// <summary>
    /// Takes out of OSDU, and settles in this flow's ledger, the ledger's pending deletions: the keys a removal flow found the
    /// source no longer holds (osdu/docs/reference/flow/removal.md, Taking out what the source removed). It works through the
    /// pending deletions alone, 500 at a time in key order, so a key taken out is never asked of OSDU again, and takes the path
    /// an operator's removal takes: the chunk's records are held under the removal's lease, what unfinished deliveries left is
    /// undone first, the records go in one bulk soft delete (the record scope, reversible), each gets an attempt (phase
    /// <see cref="AttemptPhases.SourceRemoved"/>) named under one activity, what their deliveries minted goes reversibly too, and
    /// the pipeline's cache after-step runs. A key the ledger holds no record of is kept out, and a record OSDU does not hold is
    /// settled in the ledger, both with nothing asked of OSDU. A record another ledger of the partition holds the same id of,
    /// one other records still name (when the work keeps referenced records), and one a worker or another removal holds stay
    /// pending for the next run, each with why. Nothing is asked to be planned again: the deletion keeps the record out.
    /// </summary>
    public Task<SourceRemovalSummary> RemoveForSourceAsync(SourceRemovalWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return TrackAsync(SourceRemovals.ActivityKind, work.Parameters, null, async (long? activity) =>
        {
            var started = _context.Time.GetUtcNow().UtcDateTime;
            var ledger = RequireLedger();
            var note = work.Note with { ActivityId = activity };
            await ledger.RecoverExpiredLeasesAsync(Flow.Id, started, ct).ConfigureAwait(false);
            var tally = Ledger.RemovalTally.Empty;
            var undone = UndoSummary.Empty;
            var removed = new HashSet<string>(work.RemovedBefore, StringComparer.Ordinal);
            IDeliveryProtocol? protocol = null;
            DeliveryKey? after = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await ledger.PendingRemovalsAsync(Flow.Id, after, SourceRemovals.Chunk, ct).ConfigureAwait(false);
                if (page.Count == 0)
                {
                    break;
                }

                after = page[^1].Key;
                var (outcomes, chunkUndone) = await RemoveSourceChunkAsync(
                    ledger, async () => protocol ??= await ProtocolAsync(ct).ConfigureAwait(false), page, note, work.KeepReferenced, removed, ct).ConfigureAwait(false);
                await ledger.SettleRemovalsAsync(Flow.Id, note.RemovalRunId, note.RunId, outcomes, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                tally = tally.Add(Ledger.RemovalTally.Of(outcomes.Select(o => o.Outcome)));
                undone = undone.Add(chunkUndone);
            }

            var summary = new SourceRemovalSummary
            {
                Tally = tally,
                Undone = undone,
                CacheAfter = tally.Removed + tally.AlreadyGone > 0 ? await PutIntoCacheAsync(started, null, ct).ConfigureAwait(false) : null,
                ActivityId = activity,
                RemovedIds = removed,
            };
            var reached = tally.Removed + tally.AlreadyGone + tally.Settled + tally.KeptOut + tally.Referenced + tally.Shared + tally.Busy + tally.Failed;
            return (summary, summary.Describe(), (Guid?)null, reached == 0);
        }, ct);
    }

    /// <summary>
    /// One chunk of pending deletions, under the removal's hold: each key is kept out, settled in the ledger, or taken out of
    /// OSDU as what the ledger holds of it, the partition's other ledgers and the records still naming it allow.
    /// </summary>
    private async Task<(IReadOnlyList<RemovalKeyOutcome> Outcomes, UndoSummary Undone)> RemoveSourceChunkAsync(
        ILedger ledger, Func<Task<IDeliveryProtocol>> protocolOf, IReadOnlyList<RemovedKeyState> page, SourceRemovalNote note, bool keepReferenced, HashSet<string> removed,
        CancellationToken ct)
    {
        var keys = page.Select(k => k.Key).ToList();
        return await UnderRemovalHoldAsync(ledger, keys, async holder =>
        {
            var records = await ledger.GetRecordsAsync(Flow.Id, keys, ct).ConfigureAwait(false);
            var outcomes = new List<RemovalKeyOutcome>(page.Count);
            var settle = new List<RemovedKeyState>();
            var take = new List<(RemovedKeyState Key, RecordState Record)>();
            foreach (var key in page)
            {
                if (!records.TryGetValue(key.Key, out var record))
                {
                    // Only the record table held it, or the ledger let go of its record since: nothing is in OSDU of it.
                    outcomes.Add(new RemovalKeyOutcome(key.Key, SourceRemovals.KeptOut, "the ledger holds no record of the key; it is kept out, so no run delivers it"));
                }
                else if (record.LeaseOwner is not null && !string.Equals(record.LeaseOwner, holder, StringComparison.Ordinal))
                {
                    outcomes.Add(new RemovalKeyOutcome(key.Key, SourceRemovals.Busy, HeldElsewhere));
                }
                else if (record.TargetId is null || !string.Equals(record.ClaimedTargetId, record.TargetId, StringComparison.Ordinal) || record.Status == RecordStatus.Deleted)
                {
                    settle.Add(key);
                }
                else
                {
                    take.Add((key, record));
                }
            }

            if (settle.Count > 0)
            {
                var passed = (await ledger.SettleRemovedBySourceAsync(
                    Flow.Id, settle.ToDictionary(k => k.Key, note.For), Actor, holder, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false)).ToHashSet();
                outcomes.AddRange(settle.Select(key => passed.Contains(key.Key)
                    ? new RemovalKeyOutcome(key.Key, SourceRemovals.Busy, HeldElsewhere)
                    : new RemovalKeyOutcome(key.Key, SourceRemovals.Settled, "OSDU held nothing of it for this flow; its queued work was dropped and it was marked removed")));
            }

            if (take.Count == 0)
            {
                return ((IReadOnlyList<RemovalKeyOutcome>)outcomes, UndoSummary.Empty);
            }

            // An id another ledger of the partition holds live is that ledger's to keep: taking it out would take it from under it.
            var shared = await ledger.SharedIdsAsync(Flow.Id, take.Select(t => t.Record.TargetId!).ToList(), ct).ConfigureAwait(false);
            var going = new List<(RemovedKeyState Key, RecordState Record)>(take.Count);
            foreach (var item in take)
            {
                if (shared.Contains(item.Record.TargetId!))
                {
                    outcomes.Add(new RemovalKeyOutcome(item.Key.Key, SourceRemovals.Shared,
                        $"another ledger of the partition holds {item.Record.TargetId} live, so it stays in OSDU for that ledger; remove it there if it should go"));
                }
                else
                {
                    going.Add(item);
                }
            }

            if (keepReferenced && going.Count > 0)
            {
                var chunkIds = going.Select(g => g.Record.TargetId!).ToHashSet(StringComparer.Ordinal);
                var named = await NamingRecordsAsync(going.Select(g => g.Record.TargetId!).ToList(), chunkIds, removed, ct).ConfigureAwait(false);
                var kept = new List<(RemovedKeyState Key, RecordState Record)>(going.Count);
                foreach (var item in going)
                {
                    if (named.TryGetValue(item.Record.TargetId!, out var why))
                    {
                        outcomes.Add(new RemovalKeyOutcome(item.Key.Key, why.Failed ? SourceRemovals.Failed : SourceRemovals.Referenced, why.Detail));
                    }
                    else
                    {
                        kept.Add(item);
                    }
                }

                going = kept;
            }

            if (going.Count == 0)
            {
                return ((IReadOnlyList<RemovalKeyOutcome>)outcomes, UndoSummary.Empty);
            }

            // What unfinished deliveries of these records left is undone first, through the one undo path, so a removal never
            // leaves a dataset, session or version no record names; one whose undo could not settle yet waits for the next run.
            var protocol = await protocolOf().ConfigureAwait(false);
            var (undoneFirst, unsettled) = await UndoForRemovalAsync(ledger, protocol, going.Select(g => g.Record).ToList(), open: true, minted: false, holder, ct).ConfigureAwait(false);
            var removals = new List<RecordRemoval>(going.Count);
            foreach (var (key, record) in going)
            {
                if (unsettled.Contains(key.Key))
                {
                    outcomes.Add(new RemovalKeyOutcome(key.Key, SourceRemovals.Failed,
                        "what an unfinished delivery of it left in OSDU could not be undone yet; the next run tries again"));
                }
                else
                {
                    removals.Add(new RecordRemoval(key.Key, record.TargetId!, JsonMerge.ToValues(record.TargetStateJson)));
                }
            }

            if (removals.Count == 0)
            {
                return ((IReadOnlyList<RemovalKeyOutcome>)outcomes, undoneFirst);
            }

            using var correlation = OsduCorrelation.Begin(FlowTelemetry.Removal);
            var results = await protocol.DeleteBatchAsync(removals, RemovalScope.Record, ct).ConfigureAwait(false);
            var byKey = going.ToDictionary(g => g.Key.Key);
            var succeeded = results.Where(r => r.Succeeded).ToList();
            if (succeeded.Count > 0)
            {
                await ledger.MarkRemovedAsync(
                    Flow.Id, succeeded.Select(r => r.Removal.Key).ToList(), RemovalScope.Record, Actor, _context.Time.GetUtcNow().UtcDateTime, correlation.Id,
                    deliverAgain: false, sources: succeeded.ToDictionary(r => r.Removal.Key, r => note.For(byKey[r.Removal.Key].Key)), ct: ct).ConfigureAwait(false);
            }

            // Once the records are out of OSDU, what their deliveries minted (datasets, content, outputs) goes reversibly too, as
            // deleting the ledger takes it, since no record names it any more.
            var retired = succeeded.Select(r => byKey[r.Removal.Key].Record).ToList();
            var undoneAfter = retired.Count > 0
                ? (await UndoForRemovalAsync(ledger, protocol, retired, open: false, minted: true, holder, ct).ConfigureAwait(false)).Undone
                : UndoSummary.Empty;

            foreach (var result in results)
            {
                var record = byKey[result.Removal.Key].Record;
                var announced = await AnnounceRemovalAsync(result, RemovalScope.Record, record, ct).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    removed.Add(record.TargetId!);
                    outcomes.Add(new RemovalKeyOutcome(result.Removal.Key, result.Outcome!.AlreadyGone ? SourceRemovals.AlreadyGone : SourceRemovals.Removed, announced.Detail));
                }
                else
                {
                    outcomes.Add(new RemovalKeyOutcome(result.Removal.Key, SourceRemovals.Failed, announced.Detail));
                }
            }

            return ((IReadOnlyList<RemovalKeyOutcome>)outcomes, undoneFirst.Add(undoneAfter));
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// For each id, whether records in OSDU other than the ones this removal takes out still name it: the search for the id
    /// (the explorer's mention query), less the id itself, the ids of <paramref name="chunk"/> and the ids taken out already.
    /// An id named is kept, with the first records that name it; one the search could not be asked about is failed, saying why.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, (bool Failed, string Detail)>> NamingRecordsAsync(
        IReadOnlyList<string> ids, IReadOnlySet<string> chunk, IReadOnlySet<string> removed, CancellationToken ct)
    {
        var client = await _target.ClientAsync(ct).ConfigureAwait(false);
        var search = new OsduSearch(client, OsduRecordSearch.QueryPath, Model.RetrievalSource.DefaultSearchPath, _log);
        var named = new ConcurrentDictionary<string, (bool Failed, string Detail)>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = ReferenceChecksAtOnce, CancellationToken = ct }, async (id, token) =>
        {
            var reading = RecordExplorer.Interpret(new ExplorerSearch { Mentions = id }, Flow.Partition);
            try
            {
                var (_, hits) = await search.FirstAsync(
                    new OsduSearchQuery { Kind = "*:*:*:*", Query = reading.Query, ReturnedFields = ["id"] }, ReferencesShown, token).ConfigureAwait(false);
                var naming = hits
                    .Select(h => h["id"]?.GetValue<string>())
                    .OfType<string>()
                    .Where(other => !string.Equals(other, id, StringComparison.Ordinal) && !chunk.Contains(other) && !removed.Contains(other))
                    .ToList();
                if (naming.Count > 0)
                {
                    named[id] = (false,
                        $"other records in OSDU still name it ({string.Join(", ", naming.Take(5))}{(naming.Count > 5 ? ", ..." : string.Empty)}), so it stays until they are gone; the next run checks again");
                }
            }
            catch (Exception ex) when ((ex is DeliveryException or HttpRequestException or TimeoutException) && !token.IsCancellationRequested)
            {
                named[id] = (true, $"OSDU's search could not say whether other records name it, so it was not removed: {HeaderRedaction.RedactMessage(ex.Message)}");
            }
        }).ConfigureAwait(false);
        return named;
    }
}
