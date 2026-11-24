using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// What a flow's pages read of its records and their attempts (osdu/docs/reference/concepts/ledger.md, Finding records):
// a flow's records a page at a time, narrowed by a query and counted up to a bound, every record in key order, the
// flows that hold records, and the attempts of a record or a submission, with the origins a record was read from.
public sealed partial class OsduLedger
{
    public Task<long> CountAttemptsAsync(Guid submissionId, AttemptOutcome outcome, string? phase = null, CancellationToken ct = default)
    {
        var text = StatusText.Of(outcome);
        return ReadAsync(
            db =>
            {
                var query = db.DeliveryAttempts.Where(a => a.SubmissionId == submissionId && a.Outcome == text);
                if (phase is not null)
                {
                    query = query.Where(a => a.Phase == phase);
                }

                return query.Select(a => a.DeliveryKey).Distinct().LongCountAsync(ct);
            },
            ct);
    }

    public async Task<IReadOnlyList<RecordState>> ListAsync(Guid flowId, RecordQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        if (query.Offset >= RecordListing.CountLimit)
        {
            throw new RecordQueryTooBroadException(string.Create(
                CultureInfo.InvariantCulture,
                $"A record listing pages through its first {RecordListing.CountLimit} records, and offset {query.Offset} is past them. Narrow the filter (a status, a submission, a run or a search) to reach the records beyond."));
        }

        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var listed = await ReadAsync(
            async db =>
            {
                var rows = await MatchingAsync(db, partition, flowId, query, RecordListing.CountLimit, ct).ConfigureAwait(false);

                // Ties broken by key: a bulk write stamps a whole batch with one update time, and the pages must still partition it.
                var page = rows
                    .OrderByDescending(r => r.UpdatedUtc)
                    .ThenByDescending(r => r.DeliveryKey)
                    .Skip(query.Offset)
                    .Take(Math.Clamp(query.Max, 1, 1000));
                return ToStates(await ReadLeasedAsync(db, page, ct).ConfigureAwait(false));
            },
            ct).ConfigureAwait(false);
        var name = await PartitionNameAsync(partition, ct).ConfigureAwait(false);
        return listed.Select(r => r with { Partition = name }).ToList();
    }

    public async Task<BoundedCount> CountAsync(Guid flowId, RecordQuery query, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return new BoundedCount(0, Exact: true);
        }

        return await ReadAsync(
            async db =>
            {
                var rows = await MatchingAsync(db, partition, flowId, query, limit, ct).ConfigureAwait(false);
                var count = await rows.Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false);
                if (count >= limit)
                {
                    return new BoundedCount(count, Exact: false);
                }

                // Below the limit the count is exact, unless a prefix search left out candidates another filter would have kept.
                var truncated = PrefixTerm(query) is { } term
                    && Narrows(query)
                    && await PrefixCandidatesTruncatedAsync(db, partition, flowId, term, limit, ct).ConfigureAwait(false);
                return new BoundedCount(count, Exact: !truncated);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<Guid>> FlowsWithRecordsAsync(IReadOnlyCollection<Guid> flowIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flowIds);
        if (flowIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // One statement with an EXISTS per identity, never a DISTINCT over the records, which would read every record of
        // each identity to learn that it has one.
        var held = await ReadAsync(db => SqlServerLedgerBulk.FlowsWithRecordsAsync(db, flowIds, ct), ct).ConfigureAwait(false);
        return held.ToHashSet();
    }

    /// <summary>
    /// The records a listing matches, as a query each of whose paths reads a bounded part of the ledger. The filters
    /// other than the search seek their <c>(FlowId, column)</c> indexes. A prefix search takes at most
    /// <paramref name="candidates"/> records from each identity column's index and from the flow's identity tokens, in
    /// index order, and the rest of the filter applies to those. A contains term has no index, so the records the rest of
    /// the filter leaves are counted first, no further than the scan limit, and a filter that leaves more is refused.
    /// </summary>
    private async Task<IQueryable<DeliveryRecord>> MatchingAsync(OsduDbContext db, short partition, Guid flowId, RecordQuery query, int candidates, CancellationToken ct)
    {
        var flow = db.DeliveryRecords.AsNoTracking().Where(r => r.PartitionId == partition && r.FlowId == flowId);
        var rows = Narrow(db, partition, flowId, flow, query);
        if (string.IsNullOrWhiteSpace(query.Search))
        {
            return rows;
        }

        var term = query.Search.Trim();
        if (Guid.TryParse(term, out var key))
        {
            return rows.Where(r => r.DeliveryKey == key);
        }

        if (query.Mode != SearchMode.Contains)
        {
            // A column's start finds a label written out (`Wellbore B-1 / COMP`); the identity tokens find the values a row
            // shows inside one (its key values, the words of its label, the OSDU id's own part), as the lookup across flows does.
            var matches = PrefixCandidates(flow, term, candidates)
                .Concat(IdentityCandidates(db, term, candidates, flowId, [partition]).Select(i => i.DeliveryKey));
            return rows.Where(r => matches.Contains(r.DeliveryKey));
        }

        await RefuseBroadContainsAsync(rows, "narrow by status, submission or run", ct).ConfigureAwait(false);
        return Containing(rows, term);
    }

    /// <summary>The listing's filters other than its search, over one flow's records; each one seeks an index.</summary>
    private static IQueryable<DeliveryRecord> Narrow(OsduDbContext db, short partition, Guid flowId, IQueryable<DeliveryRecord> rows, RecordQuery query)
    {
        if (query.Status is { } status)
        {
            var text = StatusText.Of(status);
            rows = rows.Where(r => r.Status == text);
        }

        if (query.SubmissionId is { } submissionId)
        {
            rows = rows.Where(r => r.LastSubmissionId == submissionId);
        }

        if (query.RunId is { } runId)
        {
            // The attempt table is indexed on (RunId, PartitionId, FlowId, DeliveryKey), so this is a semi-join over that index
            // rather than a scan: one seek for the run, and one for each part of a delivery it split over nodes.
            var attempts = db.DeliveryAttempts.AsNoTracking().Where(a => a.PartitionId == partition && a.FlowId == flowId);
            if (query.DeliveredFor is { } cause)
            {
                // The records the run delivered for one cause: its delivered attempts naming the kind.
                var sentFor = StatusText.Of(AttemptOutcome.Delivered);
                var flag = (int)cause;
                attempts = attempts.Where(a => a.Outcome == sentFor && a.Triggers != null && (a.Triggers.Value & flag) != 0);
            }

            List<Guid?> runs = [runId, .. query.RunParts.Where(part => part != runId).Distinct().Select(part => (Guid?)part)];
            var touched = (runs.Count == 1 ? attempts.Where(a => a.RunId == runId) : attempts.Where(a => runs.Contains(a.RunId))).Select(a => a.DeliveryKey);
            rows = rows.Where(r => touched.Contains(r.DeliveryKey));
        }

        if (query.DeliveredBySubmissionId is { } deliveredBy)
        {
            // The attempt table is indexed on (SubmissionId, Outcome, Phase) with the key included, so the records a
            // submission delivered are a seek of that index whatever the submission was: a semi-join, not a scan.
            var delivered = StatusText.Of(AttemptOutcome.Delivered);
            var sending = db.DeliveryAttempts.AsNoTracking()
                .Where(a => a.SubmissionId == deliveredBy && a.Outcome == delivered && a.PartitionId == partition && a.FlowId == flowId);
            if (query.DeliveredFor is { } cause)
            {
                // One cause of the submission's deliveries: the kind is read from the attempt each record's key leads to.
                var flag = (int)cause;
                sending = sending.Where(a => a.Triggers != null && (a.Triggers.Value & flag) != 0);
            }

            var sent = sending.Select(a => a.DeliveryKey);
            rows = rows.Where(r => sent.Contains(r.DeliveryKey));
        }

        var scope = db.DeliveryRecords.AsNoTracking().Where(r => r.PartitionId == partition && r.FlowId == flowId);
        rows = NarrowedByKeys(rows, scope, new LookupNarrowing(query.Drifted, query.StepHeld, query.Restricted, query.Incomplete, query.Missing, query.AccessRestricted));
        if (query.Problem is { } problem)
        {
            // The problem index holds the blocked records alone; naming its filter lets the server read it.
            rows = rows.Where(r => r.ProblemHash != null && r.ProblemHash == problem);
        }

        if (query.EverDelivered is { } everDelivered)
        {
            rows = everDelivered ? rows.Where(r => r.LastDeliveredUtc != null) : rows.Where(r => r.LastDeliveredUtc == null);
        }

        return rows;
    }

    /// <summary>Whether the listing has a filter besides its search.</summary>
    private static bool Narrows(RecordQuery query)
        => query.Status is not null || query.SubmissionId is not null || query.DeliveredBySubmissionId is not null || query.RunId is not null
            || query.Drifted || query.StepHeld || query.Restricted || query.Incomplete || query.Missing is not null || query.AccessRestricted
            || query.EverDelivered is not null;

    /// <summary>The listing's search term when it is a prefix search: not empty, not a delivery key, not a contains search.</summary>
    private static string? PrefixTerm(RecordQuery query)
        => string.IsNullOrWhiteSpace(query.Search) || query.Mode == SearchMode.Contains || Guid.TryParse(query.Search.Trim(), out _)
            ? null
            : query.Search.Trim();

    /// <summary>
    /// The delivery keys of one flow's records whose source key, label, OSDU id or origin file name starts with
    /// <paramref name="term"/>: at most <paramref name="limit"/> from each column, each read in the order of its own index
    /// so the read stops there. A record matching on two columns appears twice, which a membership test does not mind.
    /// Keys identify records only inside one flow, so <paramref name="scope"/> is one flow's records.
    /// </summary>
    private static IQueryable<Guid> PrefixCandidates(IQueryable<DeliveryRecord> scope, string term, int limit)
        => scope.Where(r => r.SourceKey.StartsWith(term)).OrderBy(r => r.SourceKey).Select(r => r.DeliveryKey).Take(limit)
            .Concat(scope.Where(r => r.Label != null && r.Label.StartsWith(term)).OrderBy(r => r.Label).Select(r => r.DeliveryKey).Take(limit))
            .Concat(scope.Where(r => r.TargetId != null && r.TargetId.StartsWith(term)).OrderBy(r => r.TargetId).Select(r => r.DeliveryKey).Take(limit))
            .Concat(scope.Where(r => r.SourceFileName != null && r.SourceFileName.StartsWith(term)).OrderBy(r => r.SourceFileName).Select(r => r.DeliveryKey).Take(limit));

    /// <summary>
    /// Whether any identity column, or the flow's identity tokens, has at least <paramref name="limit"/> prefix matches,
    /// so candidates were left out.
    /// </summary>
    private static async Task<bool> PrefixCandidatesTruncatedAsync(OsduDbContext db, short partition, Guid flowId, string term, int limit, CancellationToken ct)
    {
        var scope = db.DeliveryRecords.AsNoTracking().Where(r => r.PartitionId == partition && r.FlowId == flowId);
        var folded = RecordIdentities.Fold(term);
        return await scope.Where(r => r.SourceKey.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit
            || await scope.Where(r => r.Label != null && r.Label.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit
            || await scope.Where(r => r.TargetId != null && r.TargetId.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit
            || await scope.Where(r => r.SourceFileName != null && r.SourceFileName.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit
            || await db.DeliveryRecordIdentities.AsNoTracking()
                .Where(i => i.PartitionId == partition && i.FlowId == flowId && i.Token.StartsWith(folded))
                .Select(i => i.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit;
    }

    public async Task<IReadOnlyList<AttemptRecord>> ListAttemptsAsync(Guid flowId, DeliveryKey key, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var rows = await ReadAsync(
            db => db.DeliveryAttempts
                .Where(a => a.PartitionId == partition && a.FlowId == flowId && a.DeliveryKey == key.Value)
                .OrderByDescending(a => a.StartedUtc)
                .ThenByDescending(a => a.AttemptId)
                .Take(Math.Clamp(max, 1, 1000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task<IReadOnlyList<RecordOriginSeen>> ListOriginsAsync(Guid flowId, DeliveryKey key, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        // One grouped read over the record's attempts, sought through (PartitionId, FlowId, DeliveryKey, StartedUtc): a
        // record is tried a bounded number of times between retention's prunes, and its versions are far fewer than its tries.
        var rows = await ReadAsync(
            db => db.DeliveryAttempts
                .Where(a => a.PartitionId == partition && a.FlowId == flowId && a.DeliveryKey == key.Value && (a.SourceUpdatedUtc != null || a.SourceDeletedUtc != null))
                .GroupBy(a => new { a.SourceUpdatedUtc, a.SourceFileName, a.SourceRowNumber, a.SourceDeletedUtc })
                .Select(g => new { g.Key.SourceUpdatedUtc, g.Key.SourceFileName, g.Key.SourceRowNumber, g.Key.SourceDeletedUtc, First = g.Min(a => a.StartedUtc) })
                .OrderByDescending(v => v.SourceDeletedUtc ?? v.SourceUpdatedUtc)
                .Take(Math.Clamp(max, 1, 1000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows
            .Select(v => new RecordOriginSeen(
                new RecordOrigin(v.SourceFileName, v.SourceRowNumber, v.SourceUpdatedUtc is { } updated ? DateTime.SpecifyKind(updated, DateTimeKind.Utc) : null),
                v.SourceDeletedUtc is { } deleted ? DateTime.SpecifyKind(deleted, DateTimeKind.Utc) : null,
                DateTime.SpecifyKind(v.First, DateTimeKind.Utc)))
            .ToList();
    }

    public async Task<IReadOnlyList<RecordState>> ListRecordsAsync(Guid flowId, DeliveryKey? after, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        return await ReadAsync(
            async db =>
            {
                var query = db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId);
                if (after is { } cursor)
                {
                    var from = cursor.Value;
                    query = query.Where(r => r.DeliveryKey.CompareTo(from) > 0);
                }

                return ToStates(await ReadLeasedAsync(db, query.OrderBy(r => r.DeliveryKey).Take(Math.Clamp(max, 1, 10_000)), ct).ConfigureAwait(false));
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<DeliveryKey, AttemptRecord>> LatestAttemptsAsync(Guid flowId, IReadOnlyList<DeliveryKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = new Dictionary<DeliveryKey, AttemptRecord>();
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return result;
        }

        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(ChunkSize))
        {
            // The newest attempt of each record, ranked by the database over (PartitionId, FlowId, DeliveryKey, StartedUtc):
            // a record tried many times costs its own rows of the index, not a read of every attempt into memory.
            var rows = await ReadAsync(
                db => db.DeliveryAttempts
                    .Where(a => a.PartitionId == partition && a.FlowId == flowId && chunk.Contains(a.DeliveryKey))
                    .GroupBy(a => a.DeliveryKey)
                    .Select(g => g.OrderByDescending(a => a.StartedUtc).ThenByDescending(a => a.AttemptId).First())
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                result[new DeliveryKey(row.DeliveryKey)] = ToRecord(row);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<AttemptRecord>> ListAttemptsForSubmissionAsync(Guid submissionId, int max, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => db.DeliveryAttempts
                .Where(a => a.SubmissionId == submissionId)
                .OrderByDescending(a => a.StartedUtc)
                .ThenByDescending(a => a.AttemptId)
                .Take(Math.Clamp(max, 1, 5000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    private static AttemptRecord ToRecord(DeliveryAttempt a) => new()
    {
        AttemptId = a.AttemptId,
        DeliveryKey = new DeliveryKey(a.DeliveryKey),
        SubmissionId = a.SubmissionId,
        RunId = a.RunId,
        Worker = a.Worker,
        StartedUtc = a.StartedUtc,
        CompletedUtc = a.CompletedUtc,
        Outcome = StatusText.ToAttemptOutcome(a.Outcome),
        Phase = a.Phase,
        MetadataHash = a.MetadataHash,
        PayloadHash = a.PayloadHash,
        TargetVersion = a.TargetVersion,
        Error = a.Error,
        ResultJson = a.ResultJson,
        Triggers = a.Triggers is { } triggers ? (Planning.TriggerKinds)triggers : null,
        WorkBatch = a.WorkBatch,
        SourceFileName = a.SourceFileName,
        SourceRowNumber = a.SourceRowNumber,
        SourceUpdatedUtc = a.SourceUpdatedUtc is { } updated ? DateTime.SpecifyKind(updated, DateTimeKind.Utc) : null,
        SourceDeletedUtc = a.SourceDeletedUtc is { } deleted ? DateTime.SpecifyKind(deleted, DateTimeKind.Utc) : null,
        ParentVersion = a.ParentVersion,
    };
}
