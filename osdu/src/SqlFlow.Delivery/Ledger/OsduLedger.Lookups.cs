using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// The Records page's lookup across every flow (osdu/docs/reference/concepts/ledger.md, Finding records): the records a
// term finds through the identity indexes, or the most recently updated, narrowed to a flow, a status, a partition or
// what keeps them (drift, a step set aside, restricted access), and counted up to a bound. A contains search has no
// index, so it runs only where the rest of the lookup leaves few records.
public sealed partial class OsduLedger
{
    public async Task<IReadOnlyList<RecordState>> LookupAsync(
        string term, int max, RecordStatus? status = null, Guid? flowId = null, string? partition = null, SearchMode mode = SearchMode.Prefix,
        LookupNarrowing narrowTo = default, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        var partitions = await ReadPartitionsAsync(flowId, partition, ct).ConfigureAwait(false);
        if (partitions.Count == 0)
        {
            return [];
        }

        var states = await ReadAsync(
            async db =>
            {
                var found = await LookupMatchingAsync(db, term, RecordListing.LookupCandidateLimit, status, flowId, partitions, mode, narrowTo, ct).ConfigureAwait(false);
                var page = found
                    .OrderByDescending(r => r.UpdatedUtc)
                    .ThenByDescending(r => r.DeliveryKey)
                    .Take(Math.Clamp(max, 1, RecordListing.LookupCandidateLimit));
                return await ReadLeasedAsync(db, page, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return await NamedAsync(states, ct).ConfigureAwait(false);
    }

    public async Task<BoundedCount> CountLookupAsync(
        string term, int limit, RecordStatus? status = null, Guid? flowId = null, string? partition = null, SearchMode mode = SearchMode.Prefix,
        LookupNarrowing narrowTo = default, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var partitions = await ReadPartitionsAsync(flowId, partition, ct).ConfigureAwait(false);
        if (partitions.Count == 0)
        {
            return new BoundedCount(0, Exact: true);
        }

        var count = await ReadAsync(
            async db =>
            {
                var found = await LookupMatchingAsync(db, term, limit, status, flowId, partitions, mode, narrowTo, ct).ConfigureAwait(false);
                return await found.Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return new BoundedCount(count, Exact: count < limit);
    }

    public async Task<IReadOnlyList<RecordState>> ListRecentAsync(
        int max, RecordStatus? status = null, Guid? flowId = null, string? partition = null, LookupNarrowing narrowTo = default, CancellationToken ct = default)
    {
        var take = Math.Clamp(max, 1, RecordListing.LookupCandidateLimit);
        var partitions = await ReadPartitionsAsync(flowId, partition, ct).ConfigureAwait(false);
        var rows = new List<Leased>();

        // Each partition's newest records are the end of its own recency range; the newest across partitions are among
        // them, so every partition is read to the same depth and the pages merged.
        foreach (var within in partitions)
        {
            rows.AddRange(await ReadAsync(
                db => ReadLeasedAsync(
                    db,
                    RecentFilter(db, status, flowId, within, narrowTo)
                        .OrderByDescending(r => r.UpdatedUtc)
                        .ThenByDescending(r => r.DeliveryKey)
                        .Take(take),
                    ct),
                ct).ConfigureAwait(false));
        }

        // Ties broken by key: a bulk write stamps a whole batch with one update time, and the pages must still partition it.
        var newest = rows
            .OrderByDescending(r => r.Record.UpdatedUtc)
            .ThenByDescending(r => r.Record.DeliveryKey)
            .Take(take)
            .ToList();
        return await NamedAsync(newest, ct).ConfigureAwait(false);
    }

    public async Task<BoundedCount> CountRecentAsync(
        int limit, RecordStatus? status = null, Guid? flowId = null, string? partition = null, LookupNarrowing narrowTo = default, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var count = 0;
        foreach (var within in await ReadPartitionsAsync(flowId, partition, ct).ConfigureAwait(false))
        {
            var left = limit - count;
            if (left <= 0)
            {
                break;
            }

            count += await ReadAsync(db => RecentFilter(db, status, flowId, within, narrowTo).Select(r => r.DeliveryKey).Take(left).CountAsync(ct), ct).ConfigureAwait(false);
        }

        return new BoundedCount(count, Exact: count < limit);
    }

    /// <summary>Records read across ledgers, each naming its partition.</summary>
    private async Task<IReadOnlyList<RecordState>> NamedAsync(IReadOnlyList<Leased> rows, CancellationToken ct)
    {
        var names = await PartitionNamesAsync(rows.Select(r => r.Record.PartitionId), ct).ConfigureAwait(false);
        return rows.Select(r => ToState(r.Record) with { LeaseExpiresUtc = r.LeaseExpiresUtc, Partition = names[r.Record.PartitionId] }).ToList();
    }

    /// <summary>
    /// The records of one partition the recency listing orders: every record of it, those in one custody state, those of
    /// one ledger identity, or those of one identity in one state. Each reads an index that starts with the partition and
    /// ends with the update time ([PartitionId, UpdatedUtc], [PartitionId, Status, UpdatedUtc], [PartitionId, FlowId,
    /// UpdatedUtc] or [PartitionId, FlowId, Status, UpdatedUtc]), so the newest rows are the end of a range the server walks
    /// backwards, and the listing costs what it shows rather than what the ledger holds. A narrowing keeps the records it
    /// names among them.
    /// </summary>
    /// <summary>
    /// The records of one partition the newest-first listing across flows reads, before its order: a whole range, so its
    /// narrowing takes the records through the indexes that hold them (<see cref="NarrowedByKeys"/>).
    /// </summary>
    private static IQueryable<DeliveryRecord> RecentFilter(OsduDbContext db, RecordStatus? status, Guid? flowId, short partition, LookupNarrowing narrowing)
    {
        var scope = db.DeliveryRecords.AsNoTracking().Where(r => r.PartitionId == partition);
        if (flowId is { } flow)
        {
            scope = scope.Where(r => r.FlowId == flow);
        }

        return NarrowedByKeys(Within(scope, status, flowId: null, default), scope, narrowing);
    }

    /// <summary>
    /// The records among <paramref name="rows"/> of one ledger identity and one custody state, each when asked, and of
    /// those the ones <paramref name="narrowing"/> keeps: what every read across flows narrows by besides its term.
    /// </summary>
    private static IQueryable<DeliveryRecord> Within(IQueryable<DeliveryRecord> rows, RecordStatus? status, Guid? flowId, LookupNarrowing narrowing)
    {
        if (flowId is { } flow)
        {
            rows = rows.Where(r => r.FlowId == flow);
        }

        if (status is { } wanted)
        {
            var text = StatusText.Of(wanted);
            rows = rows.Where(r => r.Status == text);
        }

        return Narrowed(rows, narrowing);
    }

    /// <summary>
    /// The records among <paramref name="rows"/> a narrowing keeps: those whose last verify found drift, a missing record or
    /// a removed one OSDU still holds, and those an enrichment was set aside on. A flow's listing narrows by the same two (<see cref="Narrow"/>).
    /// </summary>
    private static IQueryable<DeliveryRecord> Narrowed(IQueryable<DeliveryRecord> rows, LookupNarrowing narrowing)
    {
        if (narrowing.Drifted)
        {
            rows = rows.Where(r => r.LastVerifyOutcome == "drifted" || r.LastVerifyOutcome == "missing" || r.LastVerifyOutcome == "served");
        }

        if (narrowing.StepHeld)
        {
            // The held-steps index holds the records a step was set aside on, and no others.
            rows = rows.Where(r => r.HeldSteps != null);
        }

        if (narrowing.Restricted)
        {
            // The restricted records' index holds the records whose access is restricted, and no others.
            rows = rows.Where(r => r.RestrictionId != null);
        }

        if (narrowing.Incomplete)
        {
            // The index of the records restricted until complete holds them, and no others.
            rows = rows.Where(r => r.IncompleteRestrictionId != null);
        }

        if (narrowing.Missing is { } missing)
        {
            // The same index holds them by what they go without; naming its filter lets the server read it.
            rows = rows.Where(r => r.IncompleteRestrictionId != null && r.IncompleteHash == missing);
        }

        if (narrowing.AccessRestricted)
        {
            rows = rows.Where(r => r.RestrictionId != null || r.IncompleteRestrictionId != null);
        }

        return rows;
    }

    /// <summary>
    /// <paramref name="rows"/> narrowed as <see cref="Narrowed"/> narrows them, for a read over a whole range of the ledger
    /// (a flow's listing, the newest records across flows) rather than a bounded set of candidates. A filter with a filtered
    /// index of its own (a step held, an operator's restriction, restricted until complete and its missing values) holds a
    /// few records of a range that may hold millions; written beside the listing's order, the server reads the whole range
    /// for it instead of the index (measured on a copy of the table, a flow of 300,000 records: 36,540 pages against 166).
    /// So each takes the records whose keys its index holds, read from <paramref name="scope"/> through the index's own
    /// filter, and the records restricted either way are the keys of both indexes.
    /// </summary>
    private static IQueryable<DeliveryRecord> NarrowedByKeys(IQueryable<DeliveryRecord> rows, IQueryable<DeliveryRecord> scope, LookupNarrowing narrowing)
    {
        rows = Narrowed(rows, new LookupNarrowing(Drifted: narrowing.Drifted));
        if (narrowing.StepHeld)
        {
            rows = HeldIn(rows, scope.Where(r => r.HeldSteps != null));
        }

        if (narrowing.Restricted)
        {
            rows = HeldIn(rows, scope.Where(r => r.RestrictionId != null));
        }

        if (narrowing.Incomplete)
        {
            rows = HeldIn(rows, scope.Where(r => r.IncompleteRestrictionId != null));
        }

        if (narrowing.Missing is { } missing)
        {
            rows = HeldIn(rows, scope.Where(r => r.IncompleteRestrictionId != null && r.IncompleteHash == missing));
        }

        if (narrowing.AccessRestricted)
        {
            rows = HeldIn(rows, scope.Where(r => r.RestrictionId != null), scope.Where(r => r.IncompleteRestrictionId != null));
        }

        return rows;
    }

    /// <summary>The records among <paramref name="rows"/> whose key one of <paramref name="holders"/> holds, by the whole key.</summary>
    private static IQueryable<DeliveryRecord> HeldIn(IQueryable<DeliveryRecord> rows, params IQueryable<DeliveryRecord>[] holders)
    {
        var keys = holders
            .Select(holder => holder.Select(r => new RecordIdentityRow { PartitionId = r.PartitionId, FlowId = r.FlowId, DeliveryKey = r.DeliveryKey }))
            .Aggregate((a, b) => a.Concat(b));
        return rows.Where(r => keys.Any(k => k.PartitionId == r.PartitionId && k.FlowId == r.FlowId && k.DeliveryKey == r.DeliveryKey));
    }

    /// <summary>
    /// The records a lookup across flows matches, unordered. A delivery key and a prefix are found through the identity
    /// index (<see cref="LookupFilter"/>). A contains term has no index, so it is matched as a flow's listing matches one:
    /// over the records the rest of the lookup leaves (its partitions, flow, status and narrowing), counted first and
    /// refused when they are more than the scan limit (<see cref="RefuseBroadContainsAsync"/>).
    /// </summary>
    private async Task<IQueryable<DeliveryRecord>> LookupMatchingAsync(
        OsduDbContext db, string term, int candidates, RecordStatus? status, Guid? flowId, IReadOnlyList<short> partitions, SearchMode mode,
        LookupNarrowing narrowing, CancellationToken ct)
    {
        var t = term.Trim();
        if (mode != SearchMode.Contains || Guid.TryParse(t, out _))
        {
            return LookupFilter(db, t, candidates, status, flowId, partitions, narrowing);
        }

        var rows = Within(db.DeliveryRecords.AsNoTracking().Where(r => partitions.Contains(r.PartitionId)), status, flowId, narrowing);
        await RefuseBroadContainsAsync(rows, "narrow by flow or status", ct).ConfigureAwait(false);
        return Containing(rows, t);
    }

    /// <summary>
    /// Refuses a contains search over <paramref name="rows"/> when they are more than the scan limit: a contains term has
    /// no index, so it reads every one of them. The count stops one past the limit, so the check costs at most that read.
    /// <paramref name="narrowBy"/> says which filters bring the records under it.
    /// </summary>
    private async Task RefuseBroadContainsAsync(IQueryable<DeliveryRecord> rows, string narrowBy, CancellationToken ct)
    {
        var scanned = await rows.Select(r => r.DeliveryKey).Take(ContainsScanLimit + 1).CountAsync(ct).ConfigureAwait(false);
        if (scanned > ContainsScanLimit)
        {
            throw new RecordQueryTooBroadException(string.Create(
                CultureInfo.InvariantCulture,
                $"A contains search reads every record the rest of the filter leaves, and this filter leaves more than {ContainsScanLimit}. Use a prefix search, which is indexed, or {narrowBy} first."));
        }
    }

    /// <summary>
    /// The records among <paramref name="rows"/> whose source key, label, OSDU id or ingestion file (the one the version
    /// OSDU holds came from, or the queued version's) holds <paramref name="term"/> anywhere: what a contains search
    /// matches, in a flow's listing and across flows alike.
    /// </summary>
    private static IQueryable<DeliveryRecord> Containing(IQueryable<DeliveryRecord> rows, string term)
        => rows.Where(r => r.SourceKey.Contains(term)
            || (r.Label != null && r.Label.Contains(term))
            || (r.TargetId != null && r.TargetId.Contains(term))
            || (r.SourceFileName != null && r.SourceFileName.Contains(term))
            || (r.PendingSourceFileName != null && r.PendingSourceFileName.Contains(term)));

    /// <summary>
    /// A UUID is a delivery key, which every flow reading the row holds a record under (a seek of the partition's key
    /// index); anything else is a prefix over the identity index, which holds every value a record is findable by (the
    /// mapping's declared identities, the key values, the words of the label, the OSDU id and its trailing part, the
    /// ingestion file name), folded to upper case. A candidate is a record, partition, flow and key together, so a match in
    /// one flow never brings in another flow's record of the same row, and the records are read by joining from the few
    /// candidates to the primary key, so the read stays the size of the candidates however many records the ledger holds.
    /// A status narrows the candidates after they are found, so it costs nothing more than reading them: the index is what
    /// bounds the read. A flow is different: its candidates are its own tokens, read from the identity index in flow order,
    /// so the candidate bound is spent on that flow's records rather than on every flow's. A narrowing keeps candidates as a
    /// status does. Every read is a range of the partitions in <paramref name="partitions"/>, each index leading with the
    /// partition.
    /// </summary>
    private static IQueryable<DeliveryRecord> LookupFilter(
        OsduDbContext db, string term, int candidates, RecordStatus? status, Guid? flowId, IReadOnlyList<short> partitions, LookupNarrowing narrowing)
    {
        var t = term.Trim();
        var rows = db.DeliveryRecords.AsNoTracking();
        var found = Guid.TryParse(t, out var key)
            ? rows.Where(r => partitions.Contains(r.PartitionId) && r.DeliveryKey == key)
            : IdentityCandidates(db, t, candidates, flowId, partitions)
                .Join(rows, m => new { m.PartitionId, m.FlowId, m.DeliveryKey }, r => new { r.PartitionId, r.FlowId, r.DeliveryKey }, (_, r) => r);
        return Within(found, status, flowId, narrowing);
    }

    /// <summary>
    /// The records, of any flow of <paramref name="partitions"/> or of the one ledger identity <paramref name="flowId"/>
    /// names, one of whose identity tokens starts with <paramref name="term"/>: a seek of the identity index in token order
    /// (the primary key, [PartitionId, Token, ...], or [PartitionId, FlowId, Token] for one flow), at most
    /// <paramref name="candidates"/> of them. A record with several matching tokens is one candidate, because the distinct
    /// is over the record, not the token.
    /// </summary>
    private static IQueryable<RecordIdentityRow> IdentityCandidates(OsduDbContext db, string term, int candidates, Guid? flowId, IReadOnlyList<short> partitions)
    {
        var folded = RecordIdentities.Fold(term);
        var tokens = db.DeliveryRecordIdentities.AsNoTracking().Where(i => partitions.Contains(i.PartitionId));
        if (flowId is { } flow)
        {
            tokens = tokens.Where(i => i.FlowId == flow);
        }

        return tokens
            .Where(i => i.Token.StartsWith(folded))
            .OrderBy(i => i.Token)
            .Select(i => new RecordIdentityRow { PartitionId = i.PartitionId, FlowId = i.FlowId, DeliveryKey = i.DeliveryKey })
            .Take(candidates)
            .Distinct();
    }

    /// <summary>A record named by an identity token: the key the lookup joins back to the record on.</summary>
    private sealed class RecordIdentityRow
    {
        public short PartitionId { get; init; }

        public Guid FlowId { get; init; }

        public Guid DeliveryKey { get; init; }
    }
}
