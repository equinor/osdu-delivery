using System.Data.SqlTypes;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The ledger over the module database's <c>osdu</c> schema (<see cref="OsduDbContext"/>). A claim is one lease row
/// (osdu/docs/reference/concepts/submissions.md, Leases and claims): the worker takes a work batch, or a group of
/// records due for a retry, under a new token in one compare-and-swap, the records it holds carry the token, and a
/// crashed worker's lease simply runs out and is recovered by the next claim of its flow. While it delivers, a worker
/// renews its one row and appends what it learns; the lease applies that to the records a slice at a time. A read takes
/// no transaction of its own and does not rely on
/// snapshot isolation: one that needs a record and its lease asks for both in one statement, and one chosen as a
/// deadlock victim is read again (see ReadAsync). Every query the GUI issues is index-backed (see
/// <see cref="DeliveryModel"/>). Each operation opens its own
/// context from the factory, so the ledger is safe to share across the worker's bounded concurrency. The ledger runs on
/// SQL Server alone, and the volume writes (staging pending records, appending and applying a lease's events) go through
/// a bulk copy and set-based statements (<see cref="SqlServerLedgerBulk"/>).
/// </summary>
public sealed partial class OsduLedger : ILedger
{
    /// <summary>Cached item ids or set ids per lookup, well inside the parameter ceiling of one command.</summary>
    private const int LookupChunk = 500;

    /// <summary>A change whose records are all marked, waiting for the flows that build them to render them again.</summary>
    private const string Delivering = "delivering";

    private const string DeletedStatus = "deleted";

    private const string RevertedStatus = "reverted";

    private const string HeldStatus = "held";

    private const string FailedStatus = "failed";

    /// <summary>
    /// Cache sets this ledger has already resolved, by hash. A run stages hundreds of thousands of records across
    /// a handful of distinct dependency sets, so this turns the trail into a few queries rather than one per record.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _cacheSets = new(StringComparer.Ordinal);

    private const int MaxLogLength = 200_000;

    private const int ChunkSize = 500;

    /// <summary>
    /// Records whose identity tokens are rewritten in one statement round. It is smaller than a record chunk because
    /// each record carries up to <see cref="RecordIdentityLimits.MaxPerRecord"/> rows of its own.
    /// </summary>
    private const int IdentityChunk = 100;

    private readonly Func<OsduDbContext> _factory;
    private readonly TimeProvider _time;

    /// <summary>The most records a contains search reads (<see cref="RecordListing.ContainsScanLimit"/>); tests lower it.</summary>
    internal int ContainsScanLimit { get; init; } = RecordListing.ContainsScanLimit;

    /// <summary>
    /// The most records one statement writes when a write can reach more (1,000; tests lower it): a staging slice, a
    /// batch's lease, a release or redelivery. SQL Server turns the row locks of a statement holding 5,000 of them on one
    /// index into a lock on the whole table, which would stop every other node and flow writing records while it ran; a
    /// record's write takes a lock in each index it changes, and at most two there.
    /// </summary>
    internal int WriteSlice { get; init; } = 1_000;

    /// <summary>How many times a claim or lease statement runs when the database ends a deadlock by rolling it back.</summary>
    private const int DeadlockAttempts = 5;

    public OsduLedger(Func<OsduDbContext> factory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>A tracking context: the host may pool no-tracking contexts (the control plane does), and the ledger's
    /// read-modify-write operations rely on tracking.</summary>
    private OsduDbContext Open()
    {
        var db = _factory();
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        return db;
    }

    public async Task<SubmissionState?> GetSubmissionAsync(Guid submissionId, CancellationToken ct = default)
    {
        var entity = await ReadAsync(db => db.DeliverySubmissions.FirstOrDefaultAsync(s => s.SubmissionId == submissionId, ct), ct).ConfigureAwait(false);
        return entity is null ? null : await NamedAsync(entity, ct).ConfigureAwait(false);
    }

    /// <summary>A submission as the ledger holds it, naming its partition.</summary>
    private async Task<SubmissionState> NamedAsync(DeliverySubmission entity, CancellationToken ct)
        => ToState(entity) with { Partition = await PartitionNameAsync(entity.PartitionId, ct).ConfigureAwait(false) };

    public async Task<IReadOnlyList<PlanRequestedRecord>> ListPlanRequestedAsync(Guid flowId, PlanRequestedRecord? after, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(max, 1, 10_000);
        var (records, purged) = await ReadAsync(
            async db =>
            {
                // The order of the filtered index (PartitionId, FlowId, PlanRequestedUtc), whose key ends with the delivery
                // key, so a page is a seek past the last one read.
                var query = db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && r.PlanRequestedUtc != null);

                // The records deleted from the ledger whose rows wait to be read again, from their own filtered index in
                // the same order, so both are a seek past the same cursor. A deletion asks only for a row it has a key tuple
                // to read by, and only such a line is listed: a request with no key would have every run read its whole scope.
                var lines = db.DeliveryPurgedRecords.Where(p => p.PartitionId == partition && p.FlowId == flowId && p.PlanRequestedUtc != null && p.SourceKeyJson != null);

                if (after is { } cursor)
                {
                    var at = cursor.RequestedUtc;
                    var key = cursor.DeliveryKey.Value;
                    query = query.Where(r => r.PlanRequestedUtc >= at && !(r.PlanRequestedUtc == at && r.DeliveryKey.CompareTo(key) <= 0));
                    lines = lines.Where(p => p.PlanRequestedUtc >= at && !(p.PlanRequestedUtc == at && p.DeliveryKey.CompareTo(key) <= 0));
                }

                var asked = await query
                    .OrderBy(r => r.PlanRequestedUtc)
                    .ThenBy(r => r.DeliveryKey)
                    .Select(r => new { r.DeliveryKey, r.SourceKeyJson, r.PlanRequestedUtc })
                    .Take(take)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                var waiting = await lines
                    .OrderBy(p => p.PlanRequestedUtc)
                    .ThenBy(p => p.DeliveryKey)
                    .Select(p => new { p.DeliveryKey, p.SourceKeyJson, p.PlanRequestedUtc })
                    .Take(take)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                return (
                    asked.Select(r => new PlanRequestedRecord(new DeliveryKey(r.DeliveryKey), r.SourceKeyJson, DateTime.SpecifyKind(r.PlanRequestedUtc!.Value, DateTimeKind.Utc))).ToList(),
                    waiting.Select(p => new PlanRequestedRecord(new DeliveryKey(p.DeliveryKey), p.SourceKeyJson, DateTime.SpecifyKind(p.PlanRequestedUtc!.Value, DateTimeKind.Utc)) { Purged = true }).ToList());
            },
            ct).ConfigureAwait(false);
        if (purged.Count == 0)
        {
            return records;
        }

        // Both pages merged in the order SQL Server keeps them in: by the moment, then by the delivery key as a uniqueidentifier
        // compares, which is not the order Guid compares in. The first of the two is the page, and its last the next cursor.
        return records.Concat(purged)
            .OrderBy(r => r.RequestedUtc)
            .ThenBy(r => new SqlGuid(r.DeliveryKey.Value))
            .Take(take)
            .ToList();
    }

    public async Task<(SubmissionState Submission, bool Created)> RegisterSubmissionAsync(SubmissionState submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var partition = await WritePartitionAsync(submission.FlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var existing = await db.DeliverySubmissions.AsNoTracking().FirstOrDefaultAsync(s => s.SubmissionId == submission.SubmissionId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return (await NamedAsync(existing, ct).ConfigureAwait(false), false);
        }

        var entity = new DeliverySubmission { PartitionId = partition };
        Apply(entity, submission with { ReceivedUtc = submission.ReceivedUtc == default ? Now : submission.ReceivedUtc });
        db.DeliverySubmissions.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Lost a race with another intake of the same id: idempotent by construction.
            var raced = await db.DeliverySubmissions.AsNoTracking().FirstAsync(s => s.SubmissionId == submission.SubmissionId, ct).ConfigureAwait(false);
            return (await NamedAsync(raced, ct).ConfigureAwait(false), false);
        }

        return (await NamedAsync(entity, ct).ConfigureAwait(false), true);
    }

    public async Task UpdateSubmissionAsync(SubmissionState submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await using var db = Open();
        var entity = await db.DeliverySubmissions.FirstOrDefaultAsync(s => s.SubmissionId == submission.SubmissionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Submission {submission.SubmissionId} is not in the ledger.");
        Apply(entity, submission);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubmissionState>> ListSubmissionsAsync(Guid? flowId, int max, string? partition = null, CancellationToken ct = default)
    {
        // Each partition's newest through its own range of (PartitionId, FlowId, ReceivedUtc) or (PartitionId, ReceivedUtc),
        // merged when the listing covers several.
        var take = Math.Clamp(max, 1, 1000);
        var partitions = await ReadPartitionsAsync(flowId, partition, ct).ConfigureAwait(false);
        var list = new List<DeliverySubmission>();
        foreach (var within in partitions)
        {
            list.AddRange(await ReadAsync(
                db =>
                {
                    var query = db.DeliverySubmissions.Where(s => s.PartitionId == within);
                    if (flowId is { } flow)
                    {
                        query = query.Where(s => s.FlowId == flow);
                    }

                    return query.OrderByDescending(s => s.ReceivedUtc).Take(take).ToListAsync(ct);
                },
                ct).ConfigureAwait(false));
        }

        if (partitions.Count > 1)
        {
            list = list.OrderByDescending(s => s.ReceivedUtc).Take(take).ToList();
        }

        var named = new List<SubmissionState>(list.Count);
        foreach (var row in list)
        {
            named.Add(await NamedAsync(row, ct).ConfigureAwait(false));
        }

        return named;
    }

    public async Task<IReadOnlyDictionary<DeliveryKey, RecordState>> GetRecordsAsync(Guid flowId, IEnumerable<DeliveryKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = new Dictionary<DeliveryKey, RecordState>();
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return result;
        }

        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(ChunkSize))
        {
            var states = await ReadAsync(
                async db => ToStates(await ReadLeasedAsync(db, db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && chunk.Contains(r.DeliveryKey)), ct).ConfigureAwait(false)),
                ct).ConfigureAwait(false);
            foreach (var state in states)
            {
                result[state.DeliveryKey] = state;
            }
        }

        return result;
    }

    public async Task<RecordState?> GetRecordAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var states = await ReadAsync(
            async db => ToStates(await ReadLeasedAsync(db, db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && r.DeliveryKey == key.Value).Take(1), ct).ConfigureAwait(false)),
            ct).ConfigureAwait(false);
        return states.Count == 0 ? null : states[0] with { Partition = await PartitionNameAsync(partition, ct).ConfigureAwait(false) };
    }

    public async Task<PendingStaging> UpsertPendingAsync(Guid flowId, IReadOnlyList<RecordState> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        RequireFlow(flowId, records.Select(r => (r.FlowId, r.DeliveryKey)));
        if (records.Count == 0)
        {
            return PendingStaging.Empty;
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var now = Now;
        await using var db = Open();
        var staged = await SqlServerLedgerBulk.UpsertPendingAsync(db, partition, flowId, records, WriteSlice, now, ct).ConfigureAwait(false);
        await WriteIdentitiesAsync(db, partition, flowId, Staged(records, staged), ct).ConfigureAwait(false);
        return staged;
    }

    /// <summary>
    /// The records a staging actually wrote: every record it was given except the ones it refused as older and the ones
    /// whose OSDU id another flow holds. Only those have a row to be findable by.
    /// </summary>
    private static IReadOnlyList<RecordState> Staged(IReadOnlyList<RecordState> records, PendingStaging result)
    {
        if (result.Refused.Count == 0 && result.Conflicts.Count == 0)
        {
            return records;
        }

        var skipped = result.Refused.Select(k => k.Value).Concat(result.Conflicts.Select(c => c.DeliveryKey.Value)).ToHashSet();
        return records.Where(r => !skipped.Contains(r.DeliveryKey.Value)).ToList();
    }

    /// <summary>
    /// Rewrites the identity tokens of the records given: what an operator can find each by, from the values the record
    /// was staged with (the mapping's declared identities, its key values, its label, its OSDU id, its ingestion file).
    /// A record's tokens are replaced as a set, so a staging that renames or re-keys a record leaves nothing stale
    /// behind, and staging the same record again writes the same rows.
    /// </summary>
    private static async Task WriteIdentitiesAsync(OsduDbContext db, short partition, Guid flowId, IReadOnlyList<RecordState> records, CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return;
        }

        foreach (var chunk in records.Chunk(IdentityChunk))
        {
            ct.ThrowIfCancellationRequested();
            var keys = chunk.Select(r => r.DeliveryKey.Value).ToArray();
            var wanted = new List<DeliveryRecordIdentity>(chunk.Length * 4);
            foreach (var record in chunk)
            {
                foreach (var token in TokensOf(record))
                {
                    wanted.Add(new DeliveryRecordIdentity
                    {
                        PartitionId = partition,
                        FlowId = flowId,
                        DeliveryKey = record.DeliveryKey.Value,
                        Token = token.Token,
                        Display = token.Display,
                        Kind = RecordIdentities.Text(token.Kind),
                    });
                }
            }

            var held = await db.DeliveryRecordIdentities
                .Where(i => i.PartitionId == partition && i.FlowId == flowId && keys.Contains(i.DeliveryKey))
                .ToListAsync(ct).ConfigureAwait(false);
            var by = (DeliveryRecordIdentity row) => (row.DeliveryKey, row.Token);
            var keep = wanted.ToDictionary(by);
            foreach (var row in held)
            {
                if (keep.Remove(by(row), out var still))
                {
                    // The same token, whose display or kind may have moved (a label word that is now a declared identity).
                    row.Display = still.Display;
                    row.Kind = still.Kind;
                }
                else
                {
                    db.DeliveryRecordIdentities.Remove(row);
                }
            }

            db.DeliveryRecordIdentities.AddRange(keep.Values);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the identity tokens of records the index does not hold yet: the records of a ledger that predates the
    /// index, or that a failed pass left without rows. One bounded pass over the records after
    /// <paramref name="after"/> in the record table's key order (partition, ledger identity, delivery key), so a caller
    /// walks the whole ledger a page at a time and can stop and resume, and a delivery key held by several ledgers is
    /// passed once for each; the answer is the last record it read, or null when the ledger holds no more records.
    ///
    /// The tokens are derived from the record row, so a record whose mapping declares identities gains those on its
    /// next staging: the row alone cannot say what the mapping declared, and nothing is invented here.
    /// </summary>
    public async Task<(RecordCursor? Last, int Records, int Tokens)> BackfillIdentitiesAsync(RecordCursor? after, int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        await using var db = Open();
        var rows = db.DeliveryRecords.AsNoTracking();
        if (after is { } cursor)
        {
            var (p, f, k) = (cursor.PartitionId, cursor.FlowId, cursor.DeliveryKey);
            rows = rows.Where(r => r.PartitionId > p
                || (r.PartitionId == p && (r.FlowId.CompareTo(f) > 0 || (r.FlowId == f && r.DeliveryKey.CompareTo(k) > 0))));
        }

        var records = await rows
            .OrderBy(r => r.PartitionId)
            .ThenBy(r => r.FlowId)
            .ThenBy(r => r.DeliveryKey)
            .Take(Math.Clamp(max, 1, 1000))
            .Select(r => new
            {
                r.PartitionId,
                r.FlowId,
                r.DeliveryKey,
                r.SourceKey,
                r.SourceKeyJson,
                r.Label,
                r.TargetId,
                r.SourceFileName,
                r.PendingSourceFileName,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return (null, 0, 0);
        }

        var keys = records.Select(r => r.DeliveryKey).Distinct().ToArray();
        var partitions = records.Select(r => r.PartitionId).Distinct().ToArray();
        var indexed = (await db.DeliveryRecordIdentities.AsNoTracking()
            .Where(i => partitions.Contains(i.PartitionId) && keys.Contains(i.DeliveryKey))
            .Select(i => new { i.PartitionId, i.FlowId, i.DeliveryKey })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false))
            .Select(i => (i.PartitionId, i.FlowId, i.DeliveryKey))
            .ToHashSet();

        var written = 0;
        var tokens = 0;
        foreach (var flow in records.Where(r => !indexed.Contains((r.PartitionId, r.FlowId, r.DeliveryKey))).GroupBy(r => (r.PartitionId, r.FlowId)))
        {
            var states = flow.Select(r => new RecordState
            {
                DeliveryKey = new DeliveryKey(r.DeliveryKey),
                FlowId = r.FlowId,
                SourceKey = r.SourceKey,
                SourceKeyJson = r.SourceKeyJson,
                Label = r.Label,
                MappingName = string.Empty,
                TargetId = r.TargetId,
                SourceFileName = r.SourceFileName,
                PendingSourceFileName = r.PendingSourceFileName,
            }).ToList();
            await WriteIdentitiesAsync(db, flow.Key.PartitionId, flow.Key.FlowId, states, ct).ConfigureAwait(false);
            written += states.Count;
            tokens += states.Sum(s => TokensOf(s).Count);
        }

        var last = records[^1];
        return (new RecordCursor(last.PartitionId, last.FlowId, last.DeliveryKey), written, tokens);
    }

    /// <summary>What one record is findable by, as the identity index stores it.</summary>
    private static IReadOnlyList<RecordIdentityToken> TokensOf(RecordState record)
        => RecordIdentities.Of(
            record.Identities,
            record.SourceKey,
            RecordIdentities.KeyValues(record.SourceKeyJson),
            record.Label,
            record.TargetId,
            record.SourceFileName ?? record.PendingSourceFileName);

    /// <summary>Refuses records of another flow than the one a flow-scoped write was called for, naming the first.</summary>
    private static void RequireFlow(Guid flowId, IEnumerable<(Guid FlowId, DeliveryKey Key)> records)
    {
        foreach (var (recordFlow, key) in records)
        {
            if (recordFlow != flowId)
            {
                throw new ArgumentException(
                    $"Record {key} belongs to flow {recordFlow:D}, and this write is for flow {flowId:D}: a flow writes only its own records.",
                    nameof(records));
            }
        }
    }

    public async Task<IReadOnlySet<string>> ClaimedTargetIdsAsync(IReadOnlyCollection<string> targetIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetIds);
        if (targetIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        await using var db = Open();
        return await ClaimedAsync(db, targetIds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Which of <paramref name="targetIds"/> a record has claimed, of any flow, the asking flow's included. Asked for the ids
    /// of records that claimed none, so a claim found is always another record's. Ids compare exactly, as OSDU compares them.
    /// </summary>
    private static async Task<HashSet<string>> ClaimedAsync(OsduDbContext db, IEnumerable<string?> targetIds, CancellationToken ct)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var ids = targetIds.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (var chunk in ids.Chunk(LookupChunk))
        {
            var wanted = chunk.ToList();
            var found = await db.DeliveryRecords.AsNoTracking()
                .Where(r => r.ClaimedTargetId != null && wanted.Contains(r.ClaimedTargetId))
                .Select(r => r.ClaimedTargetId!)
                .ToListAsync(ct).ConfigureAwait(false);
            claimed.UnionWith(found.Where(id => wanted.Contains(id, StringComparer.Ordinal)));
        }

        return claimed;
    }

    public async Task MarkSkippedAsync(Guid flowId, IReadOnlyList<SkippedRecord> records, Guid submissionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var now = Now;
        var pending = StatusText.Of(RecordStatus.Pending);
        var delivering = StatusText.Of(RecordStatus.Delivering);
        var waiting = StatusText.Of(RecordStatus.Waiting);
        var delivered = StatusText.Of(RecordStatus.Delivered);
        string[] parked = [StatusText.Of(RecordStatus.Held), StatusText.Of(RecordStatus.Failed), StatusText.Of(RecordStatus.Reverted)];
        await using var db = Open();

        // An unchanged record, or one a cache change waiting for approval holds back, carries nothing to write but the
        // submission pointer: one statement per chunk, however many a quiet run skips. A record with queued work stays with
        // the submission that queued it, or that submission's drain would never find it.
        foreach (var chunk in records.Where(r => r.Kind is SkipKind.Unchanged or SkipKind.AwaitingApproval).Select(r => r.DeliveryKey.Value).Chunk(ChunkSize))
        {
            await db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && chunk.Contains(r.DeliveryKey)
                    && !(r.PendingDocumentRef != null && (r.Status == pending || r.Status == delivering || r.Status == waiting)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.LastSubmissionId, submissionId)
                    .SetProperty(r => r.UpdatedUtc, now), ct)
                .ConfigureAwait(false);
        }

        // A plan that saw a record has answered the request to plan it again that it read, whatever it decided, except one
        // that waits for a cache change to be approved: the plan that runs once the change is decided answers that.
        await AnswerRequestsAsync(db, partition, flowId, records.Where(r => r.Kind != SkipKind.AwaitingApproval).Select(r => (r.DeliveryKey.Value, r.Basis)), ct)
            .ConfigureAwait(false);

        foreach (var chunk in records.Where(r => r.Kind is SkipKind.Rendered or SkipKind.Stale).Chunk(ChunkSize))
        {
            var keys = chunk.Select(r => r.DeliveryKey.Value).ToArray();
            var entities = await db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && keys.Contains(r.DeliveryKey)).ToDictionaryAsync(r => r.DeliveryKey, ct).ConfigureAwait(false);
            var recorded = await StaleRecordedAsync(db, partition, flowId, chunk, ct).ConfigureAwait(false);
            var unfinished = await UnfinishedAsync(db, partition, flowId, entities.Values.Where(e => parked.Contains(e.Status, StringComparer.Ordinal)).Select(e => e.DeliveryKey), ct)
                .ConfigureAwait(false);
            foreach (var skip in chunk)
            {
                entities.TryGetValue(skip.DeliveryKey.Value, out var entity);
                if (Moved(entity, skip.Basis))
                {
                    // Released, redelivered, restored, delivered or deleted from the ledger since the plan read it: what the
                    // plan decided is not the record's any more. The next run plans it again from its row.
                    if (entity is not null)
                    {
                        entity.PlanRequestedUtc ??= now;
                    }

                    continue;
                }

                if (entity is null)
                {
                    throw new DeliveryException($"Record {skip.DeliveryKey} is not in the ledger, but a {skip.Kind.ToString().ToLowerInvariant()} skip is only ever decided against a record the ledger holds.");
                }

                entity.SourceKeyJson = Truncate(skip.SourceKeyJson, 2000) ?? entity.SourceKeyJson;

                // A record the ledger parked (held, failed or reverted) that OSDU holds a version of, and that the plan
                // found rendering exactly that version, or (released, so no longer blocked) carrying a row older than it:
                // OSDU holds what the source has to give, so the record is delivered again, with nothing sent. Left
                // parked, it would stay held with nothing to release, since the plan has answered the request to plan it.
                // Not while what an unfinished delivery left in OSDU is still to be undone: until then OSDU holds more than
                // the ledger says, so a released record is blocked again, saying why.
                var was = entity.Status;
                var candidate = entity.TargetVersion is not null && parked.Contains(was, StringComparer.Ordinal)
                    && (skip.Kind == SkipKind.Rendered || (skip.Kind == SkipKind.Stale && !entity.Blocked));
                var settles = candidate && !unfinished.Contains(entity.DeliveryKey);
                if (candidate && !settles)
                {
                    // Blocked at the row just read, so no plan renders it again until the row moves or the undo is done and
                    // someone releases it; the attempt says why it was not delivered again.
                    entity.Blocked = true;
                    entity.LastError = $"the {was} record renders what OSDU holds, but what an earlier delivery of it left in OSDU is not undone yet; "
                        + "release it once the undo is done (the record's artifacts say what is left)";
                    entity.ProblemHash = ProblemSignature.Of(entity.LastError);
                    entity.PendingSourceFingerprint = skip.SourceFingerprint ?? entity.PendingSourceFingerprint;
                    entity.PendingSourceModifiedUtc = skip.SourceModifiedUtc ?? entity.PendingSourceModifiedUtc;
                    entity.PendingSourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength) ?? entity.PendingSourceFileName;
                    entity.PendingSourceRowNumber = skip.Origin.RowNumber ?? entity.PendingSourceRowNumber;
                    entity.PendingSourceUpdatedUtc = skip.Origin.UpdatedUtc ?? entity.PendingSourceUpdatedUtc;
                    entity.LastSubmissionId = submissionId;
                    entity.UpdatedUtc = now;
                    db.DeliveryAttempts.Add(new DeliveryAttempt
                    {
                        PartitionId = partition,
                        FlowId = flowId,
                        DeliveryKey = entity.DeliveryKey,
                        SubmissionId = submissionId,
                        RunId = skip.RunId,
                        Worker = "intake",
                        StartedUtc = now,
                        CompletedUtc = now,
                        Outcome = StatusText.Of(AttemptOutcome.Held),
                        Phase = AttemptPhases.UndoWait,
                        Error = entity.LastError,
                        SourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength),
                        SourceRowNumber = skip.Origin.RowNumber,
                        SourceUpdatedUtc = skip.Origin.UpdatedUtc,
                    });
                    continue;
                }

                if (skip.Kind == SkipKind.Stale)
                {
                    // The record is left as it is, unless it settles: the attempt is the whole of what happened, and says
                    // which version and origin the source carried and which version stands. A row the ledger already
                    // recorded as stale is the same skip read again (the overlap of the next run's window, or a run's pass
                    // over the records asked for followed by its own read), so it is recorded once.
                    if (!settles && skip.Origin.UpdatedUtc is { } read && !recorded.Add((entity.DeliveryKey, read)))
                    {
                        continue;
                    }

                    db.DeliveryAttempts.Add(new DeliveryAttempt
                    {
                        PartitionId = partition,
                        FlowId = flowId,
                        DeliveryKey = entity.DeliveryKey,
                        SubmissionId = submissionId,
                        RunId = skip.RunId,
                        Worker = "intake",
                        StartedUtc = now,
                        CompletedUtc = now,
                        Outcome = StatusText.Of(AttemptOutcome.Skipped),
                        Phase = AttemptPhases.Stale,
                        ResultJson = settles
                            ? AttemptResult.Settled(Truncate(Http.HeaderRedaction.RedactMessage(Settled(skip.Reason, was, stale: true)), 2000)!)
                            : AttemptResult.WithDetail(null, Truncate(Http.HeaderRedaction.RedactMessage(skip.Reason), 2000)),
                        SourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength),
                        SourceRowNumber = skip.Origin.RowNumber,
                        SourceUpdatedUtc = skip.Origin.UpdatedUtc,
                    });
                    if (settles)
                    {
                        entity.LastSubmissionId = submissionId;
                        SettleDelivered(entity, now);
                    }

                    continue;
                }

                entity.SourceInsertedUtc = skip.SourceInsertedUtc ?? entity.SourceInsertedUtc;

                // The ingestion table changed the row since the version the ledger stands at, delivered or queued, and it
                // renders the document OSDU already has: a change of the record although nothing is sent. The attempt
                // keeps it with the row's new origin, and the origin moves on with the fingerprint below, so a later plan
                // that renders the same row again (a cache or mapping rollout) finds the change recorded and writes nothing.
                // A parked record's pending version is the one it was parked at, not one that lands, so only the version
                // OSDU holds counts for it; and settling it is a change of the record whether its row moved or not.
                var changed = skip.Origin.UpdatedUtc is { } stamp
                    && !(entity.SourceUpdatedUtc >= stamp)
                    && (settles || !(entity.PendingSourceUpdatedUtc >= stamp));
                if (changed || settles)
                {
                    db.DeliveryAttempts.Add(new DeliveryAttempt
                    {
                        PartitionId = partition,
                        FlowId = flowId,
                        DeliveryKey = entity.DeliveryKey,
                        SubmissionId = submissionId,
                        RunId = skip.RunId,
                        Worker = "intake",
                        StartedUtc = now,
                        CompletedUtc = now,
                        Outcome = StatusText.Of(AttemptOutcome.Skipped),
                        Phase = AttemptPhases.Identical,
                        ResultJson = settles
                            ? AttemptResult.Settled(Truncate(Http.HeaderRedaction.RedactMessage(Settled(skip.Reason, was, stale: false)), 2000)!)
                            : AttemptResult.WithDetail(null, Truncate(Http.HeaderRedaction.RedactMessage(skip.Reason), 2000)),
                        SourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength),
                        SourceRowNumber = skip.Origin.RowNumber,
                        SourceUpdatedUtc = skip.Origin.UpdatedUtc,
                    });
                }

                if (entity.PendingDocumentRef is not null && (entity.Status == pending || entity.Status == delivering || entity.Status == waiting))
                {
                    // Identical to the queued work: the version the drop carried belongs to that work and lands with it.
                    entity.PendingSourceFingerprint = skip.SourceFingerprint ?? entity.PendingSourceFingerprint;
                    entity.PendingSourceModifiedUtc = Latest(entity.PendingSourceModifiedUtc, skip.SourceModifiedUtc);
                    if (entity.PendingPayload)
                    {
                        entity.PendingPayloadModifiedUtc = Latest(entity.PendingPayloadModifiedUtc, skip.PayloadModifiedUtc);
                    }

                    if (changed)
                    {
                        entity.PendingSourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength);
                        entity.PendingSourceRowNumber = skip.Origin.RowNumber;
                        entity.PendingSourceUpdatedUtc = skip.Origin.UpdatedUtc;
                    }

                    entity.UpdatedUtc = now;
                    continue;
                }

                entity.LastSubmissionId = submissionId;
                entity.UpdatedUtc = now;
                if (settles)
                {
                    SettleDelivered(entity, now);
                }

                if (entity.Status == delivered)
                {
                    // OSDU holds a document identical to this render, so it is as true of the version and context just
                    // rendered as of the ones it was built from: the next plan decides the record without rendering.
                    entity.SourceFingerprint = skip.SourceFingerprint ?? entity.SourceFingerprint;
                    entity.SourceModifiedUtc = Latest(entity.SourceModifiedUtc, skip.SourceModifiedUtc);
                    entity.PayloadModifiedUtc = Latest(entity.PayloadModifiedUtc, skip.PayloadModifiedUtc);
                    entity.RenderContext = skip.RenderContext ?? entity.RenderContext;
                    if (changed)
                    {
                        entity.SourceFileName = Truncate(skip.Origin.FileName, DeliveryModel.MaxSourceFileNameLength);
                        entity.SourceRowNumber = skip.Origin.RowNumber;
                        entity.SourceUpdatedUtc = skip.Origin.UpdatedUtc;
                    }
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static DateTime? Latest(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>
    /// Whether a record is held or failed at a row of its own: its pending columns name the version it was held at, which is
    /// the latest row the ledger read for it, and the one a write back that blocks it again leaves it at.
    /// </summary>
    private static bool HeldAtItsRow(DeliveryRecord entity)
        => (entity.Status == HeldStatus || entity.Status == FailedStatus)
            && (entity.PendingSourceFingerprint is not null || entity.PendingSourceModifiedUtc is not null);

    /// <summary>
    /// Whether a record changed since the plan read it (<see cref="PlanBasis"/>): another write moved its update time, or it
    /// was added to or deleted from the ledger meanwhile. A write that is not a plan's has no basis and never moved.
    /// </summary>
    private static bool Moved(DeliveryRecord? entity, PlanBasis? basis)
        => basis is { } read && (entity is null
            ? read.UpdatedUtc is not null
            : read.UpdatedUtc is not { } at || entity.UpdatedUtc != at);

    /// <summary>
    /// Takes the requests to plan records again that their plans answered: for a plan's decision, only the request it read,
    /// so one made while the run planned (an operator's release or redelivery, a cache rollout) stays for the next run; for
    /// a write that is not a plan's, whatever request the record carries. One statement per distinct request moment and
    /// chunk: a release stamps every record it reaches with one moment, so a pass over them is a handful of statements.
    /// </summary>
    private static async Task AnswerRequestsAsync(
        OsduDbContext db, short partition, Guid flowId, IEnumerable<(Guid DeliveryKey, PlanBasis? Basis)> answered, CancellationToken ct)
    {
        var list = answered.ToList();
        foreach (var chunk in list.Where(a => a.Basis is null).Select(a => a.DeliveryKey).Distinct().Chunk(ChunkSize))
        {
            await db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && chunk.Contains(r.DeliveryKey) && r.PlanRequestedUtc != null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.PlanRequestedUtc, (DateTime?)null), ct)
                .ConfigureAwait(false);
        }

        foreach (var request in list.Where(a => a.Basis?.PlanRequestedUtc is not null).GroupBy(a => a.Basis!.Value.PlanRequestedUtc!.Value))
        {
            var seen = request.Key;
            foreach (var chunk in request.Select(a => a.DeliveryKey).Distinct().Chunk(ChunkSize))
            {
                await db.DeliveryRecords
                    .Where(r => r.PartitionId == partition && r.FlowId == flowId && chunk.Contains(r.DeliveryKey) && r.PlanRequestedUtc == seen)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.PlanRequestedUtc, (DateTime?)null), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Which of <paramref name="keys"/> have something an unfinished delivery left in OSDU still to undo: an artifact in an
    /// open state, read through the index that holds open artifacts.
    /// </summary>
    private static async Task<HashSet<Guid>> UnfinishedAsync(OsduDbContext db, short partition, Guid flowId, IEnumerable<Guid> keys, CancellationToken ct)
    {
        var found = new HashSet<Guid>();
        foreach (var chunk in keys.Distinct().Chunk(LookupChunk))
        {
            var rows = await db.DeliveryArtifacts.AsNoTracking()
                .Where(a => a.PartitionId == partition && a.FlowId == flowId && chunk.Contains(a.DeliveryKey) && OpenStates.Contains(a.State))
                .Select(a => a.DeliveryKey)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            found.UnionWith(rows);
        }

        return found;
    }

    /// <summary>
    /// The rows of <paramref name="chunk"/>'s stale skips the ledger already recorded as stale, by record and the moment the
    /// ingestion table stamped the row: a seek of each record's attempts, made only for a chunk with a stale skip.
    /// </summary>
    private static async Task<HashSet<(Guid DeliveryKey, DateTime Stamped)>> StaleRecordedAsync(
        OsduDbContext db, short partition, Guid flowId, IEnumerable<SkippedRecord> chunk, CancellationToken ct)
    {
        var stale = chunk.Where(s => s.Kind == SkipKind.Stale && s.Origin.UpdatedUtc is not null).Select(s => s.DeliveryKey.Value).Distinct().ToArray();
        if (stale.Length == 0)
        {
            return [];
        }

        var skipped = StatusText.Of(AttemptOutcome.Skipped);
        var rows = await db.DeliveryAttempts.AsNoTracking()
            .Where(a => a.PartitionId == partition && a.FlowId == flowId && stale.Contains(a.DeliveryKey)
                && a.Outcome == skipped && a.Phase == AttemptPhases.Stale && a.SourceUpdatedUtc != null)
            .Select(a => new { a.DeliveryKey, a.SourceUpdatedUtc })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(a => (a.DeliveryKey, a.SourceUpdatedUtc!.Value)).ToHashSet();
    }

    /// <summary>
    /// A parked record a plan found OSDU already holding the source's version of, delivered again: unblocked, its problem
    /// and error gone, its tries counted afresh, and whatever it was parked with (a kept document, the steps of a try, the
    /// version it was left at) dropped, as a delivery that sent nothing leaves a record. What OSDU holds is unchanged.
    /// </summary>
    private static void SettleDelivered(DeliveryRecord entity, DateTime now)
    {
        entity.Status = StatusText.Of(RecordStatus.Delivered);
        entity.Blocked = false;
        entity.ProblemHash = null;
        entity.LastError = null;
        entity.PlanRequestedUtc = null;
        entity.AttemptCount = 0;
        entity.NextAttemptUtc = null;
        entity.LeaseOwner = null;
        entity.PendingDocumentRef = null;
        entity.WorkBatch = null;
        entity.PendingStepJson = null;
        entity.PendingReferences = null;
        entity.WaitingFor = null;
        entity.PendingMetadata = false;
        entity.PendingPayload = false;
        entity.PendingPayloadLocation = null;
        entity.PendingPayloadModifiedUtc = null;
        entity.PendingSourceFingerprint = null;
        entity.PendingSourceModifiedUtc = null;
        entity.PendingSourceFileName = null;
        entity.PendingSourceRowNumber = null;
        entity.PendingSourceUpdatedUtc = null;
        entity.UpdatedUtc = now;
    }

    /// <summary>What an attempt that settles a parked record says: the plan's reason, and what became of the record.</summary>
    private static string Settled(string reason, string was, bool stale)
        => stale
            ? $"{reason}; nothing was sent and the {was} record is delivered again, at the version OSDU holds"
            : $"{reason}; OSDU already holds the document this row renders, so nothing was sent and the {was} record is delivered again";

    public async Task<IReadOnlyList<DeliveryKey>> MarkHeldAsync(Guid flowId, IEnumerable<RecordState> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        var list = records as IReadOnlyList<RecordState> ?? records.ToList();
        RequireFlow(flowId, list.Select(r => (r.FlowId, r.DeliveryKey)));
        if (list.Count == 0)
        {
            return [];
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var now = Now;
        var held = new List<DeliveryKey>(list.Count);
        await using var db = Open();
        foreach (var chunk in list.Chunk(ChunkSize))
        {
            var keys = chunk.Select(r => r.DeliveryKey.Value).ToArray();
            var existing = await db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && keys.Contains(r.DeliveryKey)).ToDictionaryAsync(r => r.DeliveryKey, ct).ConfigureAwait(false);
            // A held record claims nothing, and it is not given an id another record holds, of another flow or of this one:
            // nothing this flow does to it (a removal, a read back) may reach that record's OSDU record. A record that claimed
            // an id keeps it; one that claimed none names the id its latest render gives, since no earlier one was written.
            var claims = await ClaimedAsync(
                db, chunk.Where(r => existing.GetValueOrDefault(r.DeliveryKey.Value)?.ClaimedTargetId is null).Select(r => r.TargetId), ct).ConfigureAwait(false);
            foreach (var record in chunk)
            {
                existing.TryGetValue(record.DeliveryKey.Value, out var entity);
                if (Moved(entity, record.Basis))
                {
                    // Released, redelivered, restored or delivered since the plan read it: the hold is not the record's any
                    // more. The next run plans it again from its row, and holds it then if the row still calls for it.
                    if (entity is not null)
                    {
                        entity.PlanRequestedUtc ??= now;
                    }

                    continue;
                }

                if (entity is null)
                {
                    entity = new DeliveryRecord
                    {
                        PartitionId = partition,
                        DeliveryKey = record.DeliveryKey.Value,
                        FlowId = flowId,
                        SourceKey = Truncate(record.SourceKey, 400)!,
                        MappingName = record.MappingName,
                        CreatedUtc = now,
                    };
                    db.DeliveryRecords.Add(entity);
                    existing[entity.DeliveryKey] = entity;
                }

                entity.Label = Truncate(record.Label, 400) ?? entity.Label;
                if (entity.ClaimedTargetId is null && record.TargetId is { } targetId)
                {
                    entity.TargetId = claims.Contains(targetId) ? null : targetId;
                }

                // The id OSDU already holds a record at, for a record held because of it, is what an adoption claims; any
                // other hold, and a record that claimed an id, has none. The intake asks OSDU only of ids no record claimed.
                entity.OccupiedTargetId = entity.ClaimedTargetId is null ? record.OccupiedTargetId : null;
                entity.OccupiedTargetVersion = entity.OccupiedTargetId is null ? null : record.OccupiedTargetVersion;
                // A request to plan the record again made after the plan read it, which did not move the record (a sync that
                // found its row changed), is kept: the record is held unblocked, so the next run plans it under that request.
                var asked = record.Basis is { } basis && entity.PlanRequestedUtc is { } requested && requested != basis.PlanRequestedUtc
                    ? requested
                    : (DateTime?)null;
                entity.Status = StatusText.Of(RecordStatus.Held);
                entity.LastError = Truncate(record.LastError, 2000);
                entity.ProblemHash = asked is null ? ProblemSignature.Of(entity.LastError) : null;
                entity.LastSubmissionId = record.LastSubmissionId;
                entity.LeaseOwner = null;
                entity.PendingDocumentRef = null;
                entity.WorkBatch = null;
                entity.PendingStepJson = null;
                entity.PendingReferences = null;
                entity.WaitingFor = null;
                entity.SourceKeyJson = Truncate(record.SourceKeyJson, 2000) ?? entity.SourceKeyJson;
                entity.PendingSourceFingerprint = record.PendingSourceFingerprint;
                entity.PendingSourceModifiedUtc = record.PendingSourceModifiedUtc;
                entity.PendingSourceFileName = Truncate(record.PendingSourceFileName, DeliveryModel.MaxSourceFileNameLength);
                entity.PendingSourceRowNumber = record.PendingSourceRowNumber;
                entity.PendingSourceUpdatedUtc = record.PendingSourceUpdatedUtc;
                entity.SourceInsertedUtc = record.SourceInsertedUtc ?? entity.SourceInsertedUtc;
                entity.PlanRequestedUtc = asked;
                entity.PendingMetadata = false;
                entity.PendingPayload = false;
                entity.Blocked = asked is null;
                entity.UpdatedUtc = now;

                // What the held render read, the ids it found missing among them, so a cache version that brings one in
                // releases the record. What the document OSDU holds was built from stays the record's cache set.
                entity.HeldCacheSetId = record.CacheSetId;

                // The parent the held render read, or found missing, so the parent landing plans the record again.
                entity.ParentId = record.ParentId;
                entity.PendingParentVersion = record.PendingParentVersion;
                held.Add(record.DeliveryKey);
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    PartitionId = partition,
                    FlowId = flowId,
                    DeliveryKey = record.DeliveryKey.Value,
                    SubmissionId = record.LastSubmissionId,
                    RunId = record.RunId,
                    Worker = "intake",
                    StartedUtc = now,
                    CompletedUtc = now,
                    Outcome = StatusText.Of(AttemptOutcome.Held),
                    Phase = record.PendingSourceDeletedUtc is null ? "render" : AttemptPhases.SourceDeleted,
                    Error = Truncate(record.LastError, 2000),
                    SourceFileName = Truncate(record.PendingSourceFileName, DeliveryModel.MaxSourceFileNameLength),
                    SourceRowNumber = record.PendingSourceRowNumber,
                    SourceUpdatedUtc = record.PendingSourceUpdatedUtc,
                    ParentVersion = record.PendingParentVersion,
                    SourceDeletedUtc = record.PendingSourceDeletedUtc,
                });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return held;
    }

    /// <summary>
    /// Runs <paramref name="work"/>, and again when the database ended a deadlock by rolling it back. Only for a read, or
    /// for one statement whose second run does what the first would have: a claim, a lease, a release.
    /// </summary>
    private static async Task<T> RetryDeadlockAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < DeadlockAttempts && SqlServerLedgerBulk.IsDeadlock(ex))
            {
                // Waiting a moment, longer each time and never the same for two writers, keeps the two from meeting in
                // the same way again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), ct).ConfigureAwait(false);
            }
        }
    }

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

    public async Task<long> CountAsync(Guid flowId, Guid? submissionId, RecordStatus status, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return 0;
        }

        var text = StatusText.Of(status);
        return await ReadAsync(
            db => db.DeliveryRecords.LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && (submissionId == null || r.LastSubmissionId == submissionId) && r.Status == text, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<bool> HoldsRecordsAsync(Guid flowId, CancellationToken ct = default)
        => await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is { } partition
            && await ReadAsync(db => db.DeliveryRecords.AnyAsync(r => r.PartitionId == partition && r.FlowId == flowId, ct), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<PartitionRecords>> DeliveredPartitionsAsync(Guid flowId, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        // An OSDU id is <partition>:<entity type>:<id>, so the partition is everything before its first colon. A record is
        // counted by the id it was delivered as, or by the id it claimed when it has not been delivered yet. The two columns
        // are compared under different collations, so each is grouped on its own rather than coalesced into one expression
        // the server could not collate. The queries run on SQL Server, where IndexOf(string) translates to CHARINDEX; the
        // char overload the analyzer prefers has no translation there.
#pragma warning disable CA1866 // Use 'string.IndexOf(char)': the expression is translated to SQL, not run in .NET.
        var delivered = await ReadAsync(
            db => db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && r.TargetId != null && r.TargetId.IndexOf(":") > 0)
                .GroupBy(r => r.TargetId!.Substring(0, r.TargetId.IndexOf(":")))
                .Select(g => new { Partition = g.Key, Records = g.LongCount() })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var claimed = await ReadAsync(
            db => db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && r.TargetId == null && r.ClaimedTargetId != null && r.ClaimedTargetId.IndexOf(":") > 0)
                .GroupBy(r => r.ClaimedTargetId!.Substring(0, r.ClaimedTargetId.IndexOf(":")))
                .Select(g => new { Partition = g.Key, Records = g.LongCount() })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
#pragma warning restore CA1866
        return delivered.Concat(claimed)
            .GroupBy(c => c.Partition, StringComparer.Ordinal)
            .Select(g => new PartitionRecords(g.Key, g.Sum(c => c.Records)))
            .OrderByDescending(c => c.Records)
            .ThenBy(c => c.Partition, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<bool> HasPendingAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return false;
        }

        var pending = StatusText.Of(RecordStatus.Pending);
        var delivering = StatusText.Of(RecordStatus.Delivering);
        return await ReadAsync(
            db => db.DeliveryRecords.AnyAsync(
                r => r.PartitionId == partition
                    && r.FlowId == flowId
                    && (submissionId == null || r.LastSubmissionId == submissionId)
                    && (r.Status == pending || r.Status == delivering),
                ct),
            ct).ConfigureAwait(false);
    }

    public async Task<DateTime?> NextDueAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var pending = StatusText.Of(RecordStatus.Pending);
        return await ReadAsync(
            db => db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && (submissionId == null || r.LastSubmissionId == submissionId) && r.Status == pending && r.NextAttemptUtc != null && r.NextAttemptUtc > nowUtc)
                .MinAsync(r => r.NextAttemptUtc, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> ListSettledSubmissionsWithDueWorkAsync(Guid flowId, IReadOnlyCollection<Guid> except, DateTime nowUtc, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(except);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var pending = StatusText.Of(RecordStatus.Pending);
        var completed = StatusText.Of(SubmissionStatus.Completed);
        var failed = StatusText.Of(SubmissionStatus.Failed);
        return await ReadAsync(
            db =>
            {
                var excluded = except.ToArray();
                var settled = db.DeliverySubmissions
                    .Where(s => s.PartitionId == partition && s.FlowId == flowId && (s.Status == completed || s.Status == failed) && !excluded.Contains(s.SubmissionId))
                    .Select(s => s.SubmissionId);
                return db.DeliveryRecords
                    .Where(r => r.PartitionId == partition && r.FlowId == flowId
                        && r.Status == pending
                        && r.PendingDocumentRef != null
                        && (r.NextAttemptUtc == null || r.NextAttemptUtc <= nowUtc)
                        && r.LastSubmissionId != null
                        && settled.Contains(r.LastSubmissionId.Value))
                    .Select(r => r.LastSubmissionId!.Value)
                    .Distinct()
                    .Take(Math.Clamp(max, 1, 100))
                    .ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
    }

    public async Task AddWorkBatchAsync(WorkBatchState batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var partition = await WritePartitionAsync(batch.FlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var exists = await db.DeliveryWorkBatches.AnyAsync(b => b.SubmissionId == batch.SubmissionId && b.Index == batch.Index, ct).ConfigureAwait(false);
        if (exists)
        {
            await db.DeliveryWorkBatches
                .Where(b => b.SubmissionId == batch.SubmissionId && b.Index == batch.Index)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Location, batch.Location)
                    .SetProperty(b => b.RecordCount, batch.RecordCount)
                    .SetProperty(b => b.Status, StatusText.Of(WorkBatchStatus.Queued))
                    .SetProperty(b => b.LeaseOwner, (string?)null)
                    .SetProperty(b => b.CompletedUtc, (DateTime?)null)
                    .SetProperty(b => b.Error, (string?)null), ct)
                .ConfigureAwait(false);
            return;
        }

        db.DeliveryWorkBatches.Add(new DeliveryWorkBatch
        {
            PartitionId = partition,
            SubmissionId = batch.SubmissionId,
            Index = batch.Index,
            FlowId = batch.FlowId,
            Location = Truncate(batch.Location, 2000)!,
            RecordCount = batch.RecordCount,
            Status = StatusText.Of(WorkBatchStatus.Queued),
            CreatedUtc = batch.CreatedUtc == default ? Now : batch.CreatedUtc,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="write"/> over every record <paramref name="rows"/> matches: the records' keys are read first,
    /// by a plain read that holds no lock past the row it is on, and then written <see cref="WriteSlice"/> to a statement
    /// (<see cref="WriteByKeyAsync"/>). The keys take 32 bytes a record.
    /// </summary>
    private Task<int> WriteEachAsync(IQueryable<DeliveryRecord> rows, Func<IQueryable<DeliveryRecord>, Task<int>> write, CancellationToken ct)
        => WriteEachAsync(rows, rows, write, ct);

    /// <summary>
    /// As <see cref="WriteEachAsync(IQueryable{DeliveryRecord}, Func{IQueryable{DeliveryRecord}, Task{int}}, CancellationToken)"/>,
    /// with the keys read from <paramref name="find"/>: records that include every one <paramref name="rows"/> matches, and
    /// that one index answers alone, however many other records the table holds. A found record <paramref name="rows"/>
    /// does not match is left alone by its write.
    /// </summary>
    private async Task<int> WriteEachAsync(IQueryable<DeliveryRecord> find, IQueryable<DeliveryRecord> rows, Func<IQueryable<DeliveryRecord>, Task<int>> write, CancellationToken ct)
    {
        var keys = await RetryDeadlockAsync(() => find.Select(r => new RecordKey(r.PartitionId, r.FlowId, r.DeliveryKey)).ToListAsync(ct), ct).ConfigureAwait(false);
        return await WriteByKeyAsync(rows, keys, write, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="write"/> over the records of <paramref name="rows"/> that <paramref name="keys"/> names,
    /// <see cref="WriteSlice"/> to a statement. Each statement names its partition, flow and keys, the record table's key,
    /// which every index of the table ends with, so the database finds each record with one seek and reads, and locks, no
    /// record it does not write. <paramref name="rows"/> still applies, so a record that changed since its key was read is
    /// left as it now is.
    /// </summary>
    private async Task<int> WriteByKeyAsync(IQueryable<DeliveryRecord> rows, IEnumerable<RecordKey> keys, Func<IQueryable<DeliveryRecord>, Task<int>> write, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        var written = 0;
        foreach (var ledger in keys.GroupBy(k => (k.PartitionId, k.FlowId)))
        {
            var (partition, flowId) = ledger.Key;
            foreach (var slice in ledger.Select(k => k.DeliveryKey).Distinct().Chunk(WriteSlice))
            {
                ct.ThrowIfCancellationRequested();
                written += await RetryDeadlockAsync(() => write(rows.Where(r => r.PartitionId == partition && r.FlowId == flowId && slice.Contains(r.DeliveryKey))), ct).ConfigureAwait(false);
            }
        }

        return written;
    }

    /// <summary>A record's key: its partition, its ledger identity and its delivery key.</summary>
    private readonly record struct RecordKey(short PartitionId, Guid FlowId, Guid DeliveryKey);

    public Task<IReadOnlyList<WorkBatchState>> ListWorkBatchesAsync(Guid submissionId, int max, int offset, CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<WorkBatchState>>(
            async db =>
            {
                // A running batch shows when its lease runs out.
                var rows = await db.DeliveryWorkBatches
                    .Where(b => b.SubmissionId == submissionId)
                    .OrderBy(b => b.Index)
                    .Skip(Math.Max(0, offset))
                    .Take(Math.Clamp(max, 1, 1000))
                    .Select(b => new
                    {
                        Batch = b,
                        Expires = db.DeliveryLeases.Where(l => l.PartitionId == b.PartitionId && l.Token == b.LeaseOwner).Select(l => (DateTime?)l.ExpiresUtc).FirstOrDefault(),
                    })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                return rows.Select(r => ToState(r.Batch) with { LeaseExpiresUtc = r.Expires }).ToList();
            },
            ct);

    public Task<long> CountWorkBatchesAsync(Guid submissionId, WorkBatchStatus? status, CancellationToken ct = default)
        => ReadAsync(
            db =>
            {
                var query = db.DeliveryWorkBatches.Where(b => b.SubmissionId == submissionId);
                if (status is { } s)
                {
                    var text = StatusText.Of(s);
                    query = query.Where(b => b.Status == text);
                }

                return query.LongCountAsync(ct);
            },
            ct);

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
                    && await PrefixCandidatesTruncatedAsync(db.DeliveryRecords.AsNoTracking().Where(r => r.PartitionId == partition && r.FlowId == flowId), term, limit, ct).ConfigureAwait(false);
                return new BoundedCount(count, Exact: !truncated);
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HeldOnCache>> HeldOnCacheSetsAsync(IReadOnlyCollection<long> setIds, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        if (setIds.Count == 0)
        {
            return [];
        }

        var held = StatusText.Of(RecordStatus.Held);
        return await ReadAsync(
            async db =>
            {
                var found = new List<HeldOnCache>();
                foreach (var chunk in setIds.Distinct().Chunk(LookupChunk))
                {
                    var ids = chunk.ToList();
                    var rows = await db.DeliveryRecords.AsNoTracking()
                        .Where(r => r.HeldCacheSetId != null && ids.Contains(r.HeldCacheSetId!.Value) && r.Status == held)
                        .Join(db.DeliveryLedgers.AsNoTracking(), r => new { r.PartitionId, r.FlowId }, l => new { l.PartitionId, l.FlowId }, (r, l) => new { r.FlowId, l.FlowName, r.DeliveryKey })
                        .Take(max - found.Count)
                        .ToListAsync(ct).ConfigureAwait(false);
                    found.AddRange(rows.Select(r => new HeldOnCache(r.FlowId, r.FlowName, new DeliveryKey(r.DeliveryKey))));
                    if (found.Count >= max)
                    {
                        break;
                    }
                }

                return (IReadOnlyList<HeldOnCache>)found;
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ChangedSinceAsync(Guid flowId, DateTime sinceUtc, int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        // Two ranges, each on an index of its own: (PartitionId, FlowId, LastDeliveredUtc) for what was sent, and
        // (PartitionId, FlowId, Status, UpdatedUtc) for what was deleted or reverted. A record sent and then removed in the same
        // run is answered by the second; the reader asks OSDU what it holds of each either way.
        var delivered = StatusText.Of(RecordStatus.Delivered);
        var deleted = StatusText.Of(RecordStatus.Deleted);
        var reverted = StatusText.Of(RecordStatus.Reverted);
        var sent = await ReadAsync(
            db => db.DeliveryRecords.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && r.LastDeliveredUtc != null && r.LastDeliveredUtc >= sinceUtc
                    && r.Status == delivered && r.TargetId != null)
                .Select(r => r.TargetId!)
                .Take(max)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        if (sent.Count >= max)
        {
            return sent;
        }

        var taken = await ReadAsync(
            db => db.DeliveryRecords.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && (r.Status == deleted || r.Status == reverted) && r.UpdatedUtc >= sinceUtc
                    && r.TargetId != null)
                .Select(r => r.TargetId!)
                .Take(max - sent.Count)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return [.. sent, .. taken];
    }

    public async Task<IReadOnlyList<DeliveryKey>> ListKeysAsync(Guid flowId, RecordQuery query, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var keys = await ReadAsync(
            async db =>
            {
                var rows = await MatchingAsync(db, partition, flowId, query, RecordListing.CountLimit + 1, ct).ConfigureAwait(false);
                return await rows
                    .OrderBy(r => r.DeliveryKey)
                    .Select(r => r.DeliveryKey)
                    .Take(Math.Clamp(max, 1, RemovalLimits.MaxSelection))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return keys.Select(k => new DeliveryKey(k)).ToList();
    }

    public async Task<IReadOnlyList<RecordState>> LookupAsync(string term, int max, RecordStatus? status = null, Guid? flowId = null, string? partition = null, CancellationToken ct = default)
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
                var page = LookupFilter(db, term, RecordListing.LookupCandidateLimit, status, flowId, partitions)
                    .OrderByDescending(r => r.UpdatedUtc)
                    .ThenByDescending(r => r.DeliveryKey)
                    .Take(Math.Clamp(max, 1, 200));
                return await ReadLeasedAsync(db, page, ct).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
        return await NamedAsync(states, ct).ConfigureAwait(false);
    }

    public async Task<BoundedCount> CountLookupAsync(string term, int limit, RecordStatus? status = null, Guid? flowId = null, string? partition = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var partitions = await ReadPartitionsAsync(flowId, partition, ct).ConfigureAwait(false);
        if (partitions.Count == 0)
        {
            return new BoundedCount(0, Exact: true);
        }

        var count = await ReadAsync(db => LookupFilter(db, term, limit, status, flowId, partitions).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct), ct).ConfigureAwait(false);
        return new BoundedCount(count, Exact: count < limit);
    }

    public async Task<IReadOnlyList<RecordState>> ListRecentAsync(int max, RecordStatus? status = null, Guid? flowId = null, string? partition = null, CancellationToken ct = default)
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
                    RecentFilter(db, status, flowId, within)
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

    public async Task<BoundedCount> CountRecentAsync(int limit, RecordStatus? status = null, Guid? flowId = null, string? partition = null, CancellationToken ct = default)
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

            count += await ReadAsync(db => RecentFilter(db, status, flowId, within).Select(r => r.DeliveryKey).Take(left).CountAsync(ct), ct).ConfigureAwait(false);
        }

        return new BoundedCount(count, Exact: count < limit);
    }

    /// <summary>
    /// The partitions a read across ledgers covers: the one <paramref name="flowId"/>'s ledger belongs to, the one
    /// <paramref name="partition"/> names, or every partition the ledger keeps rows under, the unassigned included; none
    /// when the ledger or the partition holds nothing. A ledger and a partition together are both filters, so a ledger of
    /// another partition than the one named reads as empty. Each is read through its own range of a partition-first index.
    /// </summary>
    private async Task<IReadOnlyList<short>> ReadPartitionsAsync(Guid? flowId, string? partition, CancellationToken ct)
    {
        if (flowId is { } flow)
        {
            if (await PartitionOfAsync(flow, ct).ConfigureAwait(false) is not { } own)
            {
                return [];
            }

            return string.IsNullOrWhiteSpace(partition) || await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) == own ? [own] : [];
        }

        if (!string.IsNullOrWhiteSpace(partition))
        {
            return await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is { } named ? [named] : [];
        }

        return await EveryPartitionIdAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Every partition number a ledger row can carry: the unassigned one, and each partition the directory numbers.</summary>
    private async Task<IReadOnlyList<short>> EveryPartitionIdAsync(CancellationToken ct)
    {
        var every = await ReadAsync(db => db.DeliveryLedgerPartitions.AsNoTracking().Select(p => p.PartitionId).ToListAsync(ct), ct).ConfigureAwait(false);
        return [Unassigned, .. every.Order()];
    }

    /// <summary>Records read across ledgers, each naming its partition.</summary>
    private async Task<IReadOnlyList<RecordState>> NamedAsync(IReadOnlyList<Leased> rows, CancellationToken ct)
    {
        var names = await PartitionNamesAsync(rows.Select(r => r.Record.PartitionId), ct).ConfigureAwait(false);
        return rows.Select(r => ToState(r.Record) with { LeaseExpiresUtc = r.LeaseExpiresUtc, Partition = names[r.Record.PartitionId] }).ToList();
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

    public async Task<IReadOnlySet<Guid>> FlowsWithActivitiesAsync(IReadOnlyCollection<Guid> flowIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flowIds);
        if (flowIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // An EXISTS per identity, as for the records: a partition's trail is mostly scheduled runs, and a DISTINCT over it
        // would read all of them to learn which flows ran.
        var active = await ReadAsync(db => SqlServerLedgerBulk.FlowsWithActivitiesAsync(db, flowIds, ct), ct).ConfigureAwait(false);
        return active.ToHashSet();
    }

    /// <summary>
    /// The records of one partition the recency listing orders: every record of it, those in one custody state, those of
    /// one ledger identity, or those of one identity in one state. Each reads an index that starts with the partition and
    /// ends with the update time ([PartitionId, UpdatedUtc], [PartitionId, Status, UpdatedUtc], [PartitionId, FlowId,
    /// UpdatedUtc] or [PartitionId, FlowId, Status, UpdatedUtc]), so the newest rows are the end of a range the server walks
    /// backwards, and the listing costs what it shows rather than what the ledger holds.
    /// </summary>
    private static IQueryable<DeliveryRecord> RecentFilter(OsduDbContext db, RecordStatus? status, Guid? flowId, short partition)
    {
        var rows = db.DeliveryRecords.AsNoTracking().Where(r => r.PartitionId == partition);
        if (flowId is { } flow)
        {
            rows = rows.Where(r => r.FlowId == flow);
        }

        if (status is { } wanted)
        {
            var text = StatusText.Of(wanted);
            rows = rows.Where(r => r.Status == text);
        }

        return rows;
    }

    /// <summary>
    /// A UUID is a delivery key, which every flow reading the row holds a record under (a seek of the partition's key
    /// index); anything else is a prefix over the identity index, which holds every value a record is findable by (the
    /// mapping's declared identities, the key values, the words of the label, the OSDU id and its trailing part, the
    /// ingestion file name), folded to upper case. A candidate is a record, partition, flow and key together, so a match in
    /// one flow never brings in another flow's record of the same row, and the records are read by joining from the few
    /// candidates to the primary key, so the read stays the size of the candidates however many records the ledger holds.
    /// A status narrows the candidates after they are found, so it costs nothing more than reading them: the index is what
    /// bounds the read. A flow is different: its candidates are its own tokens, read from the identity index in flow order,
    /// so the candidate bound is spent on that flow's records rather than on every flow's. Every read is a range of the
    /// partitions in <paramref name="partitions"/>, each index leading with the partition.
    /// </summary>
    private static IQueryable<DeliveryRecord> LookupFilter(OsduDbContext db, string term, int candidates, RecordStatus? status, Guid? flowId, IReadOnlyList<short> partitions)
    {
        var t = term.Trim();
        var rows = db.DeliveryRecords.AsNoTracking();
        var found = Guid.TryParse(t, out var key)
            ? rows.Where(r => partitions.Contains(r.PartitionId) && r.DeliveryKey == key)
            : IdentityCandidates(db, t, candidates, flowId, partitions)
                .Join(rows, m => new { m.PartitionId, m.FlowId, m.DeliveryKey }, r => new { r.PartitionId, r.FlowId, r.DeliveryKey }, (_, r) => r);
        if (flowId is { } flow)
        {
            found = found.Where(r => r.FlowId == flow);
        }

        if (status is { } wanted)
        {
            var text = StatusText.Of(wanted);
            found = found.Where(r => r.Status == text);
        }

        return found;
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

    /// <summary>
    /// The records a listing matches, as a query each of whose paths reads a bounded part of the ledger. The filters
    /// other than the search seek their <c>(FlowId, column)</c> indexes. A prefix search takes at most
    /// <paramref name="candidates"/> records from each identity column's index, in index order, and the rest of the
    /// filter applies to those. A contains term has no index, so the records the rest of the filter leaves are counted
    /// first, no further than the scan limit, and a filter that leaves more is refused.
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
            var matches = PrefixCandidates(flow, term, candidates);
            return rows.Where(r => matches.Contains(r.DeliveryKey));
        }

        var scanned = await rows.Select(r => r.DeliveryKey).Take(ContainsScanLimit + 1).CountAsync(ct).ConfigureAwait(false);
        if (scanned > ContainsScanLimit)
        {
            throw new RecordQueryTooBroadException(string.Create(
                CultureInfo.InvariantCulture,
                $"A contains search reads every record the rest of the filter leaves, and this filter leaves more than {ContainsScanLimit}. Use a prefix search, which is indexed, or narrow by status, submission or run first."));
        }

        return rows.Where(r => r.SourceKey.Contains(term) || (r.Label != null && r.Label.Contains(term)) || (r.TargetId != null && r.TargetId.Contains(term)));
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
            // The attempt table is indexed on (RunId, PartitionId, FlowId, DeliveryKey), so this is a semi-join over that index rather than a scan.
            var touched = db.DeliveryAttempts.AsNoTracking().Where(a => a.RunId == runId && a.PartitionId == partition && a.FlowId == flowId).Select(a => a.DeliveryKey);
            rows = rows.Where(r => touched.Contains(r.DeliveryKey));
        }

        if (query.DeliveredBySubmissionId is { } deliveredBy)
        {
            // The attempt table is indexed on (SubmissionId, Outcome, Phase) with the key included, so the records a
            // submission delivered are a seek of that index whatever the submission was: a semi-join, not a scan.
            var delivered = StatusText.Of(AttemptOutcome.Delivered);
            var sent = db.DeliveryAttempts.AsNoTracking()
                .Where(a => a.SubmissionId == deliveredBy && a.Outcome == delivered && a.PartitionId == partition && a.FlowId == flowId)
                .Select(a => a.DeliveryKey);
            rows = rows.Where(r => sent.Contains(r.DeliveryKey));
        }

        if (query.Drifted)
        {
            rows = rows.Where(r => r.LastVerifyOutcome == "drifted" || r.LastVerifyOutcome == "missing");
        }

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
            || query.Drifted || query.EverDelivered is not null;

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
    /// The records, of any flow, whose source key, label, OSDU id or origin file name starts with <paramref name="term"/>,
    /// as flow and key pairs: at most <paramref name="limit"/> from each column, read in the order of its own index.
    /// </summary>
    private static IQueryable<RecordIdentity> PrefixRecordCandidates(IQueryable<DeliveryRecord> scope, string term, int limit)
        => scope.Where(r => r.SourceKey.StartsWith(term)).OrderBy(r => r.SourceKey).Select(r => new RecordIdentity { FlowId = r.FlowId, DeliveryKey = r.DeliveryKey }).Take(limit)
            .Concat(scope.Where(r => r.Label != null && r.Label.StartsWith(term)).OrderBy(r => r.Label).Select(r => new RecordIdentity { FlowId = r.FlowId, DeliveryKey = r.DeliveryKey }).Take(limit))
            .Concat(scope.Where(r => r.TargetId != null && r.TargetId.StartsWith(term)).OrderBy(r => r.TargetId).Select(r => new RecordIdentity { FlowId = r.FlowId, DeliveryKey = r.DeliveryKey }).Take(limit))
            .Concat(scope.Where(r => r.SourceFileName != null && r.SourceFileName.StartsWith(term)).OrderBy(r => r.SourceFileName).Select(r => new RecordIdentity { FlowId = r.FlowId, DeliveryKey = r.DeliveryKey }).Take(limit));

    /// <summary>A record's identity in a query: its flow and its delivery key.</summary>
    private sealed class RecordIdentity
    {
        public Guid FlowId { get; init; }

        public Guid DeliveryKey { get; init; }
    }

    /// <summary>Whether any identity column has at least <paramref name="limit"/> prefix matches, so candidates were left out.</summary>
    private static async Task<bool> PrefixCandidatesTruncatedAsync(IQueryable<DeliveryRecord> scope, string term, int limit, CancellationToken ct)
        => await scope.Where(r => r.SourceKey.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit
            || await scope.Where(r => r.Label != null && r.Label.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit
            || await scope.Where(r => r.TargetId != null && r.TargetId.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit
            || await scope.Where(r => r.SourceFileName != null && r.SourceFileName.StartsWith(term)).Select(r => r.DeliveryKey).Take(limit).CountAsync(ct).ConfigureAwait(false) >= limit;

    public async Task<FlowStats> StatsAsync(Guid flowId, DateTime nowUtc, CancellationToken ct = default)
    {
        // A ledger no run has registered holds nothing to count.
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return new FlowStats();
        }

        var stats = await ReadAsync(db => StatsAsync(db, partition, flowId, nowUtc, ct), ct).ConfigureAwait(false);
        return stats.LastSubmission is { } last ? stats with { LastSubmission = last with { Partition = await PartitionNameAsync(partition, ct).ConfigureAwait(false) } } : stats;
    }

    /// <summary>A flow's statistics, every count read from one snapshot of the ledger.</summary>
    private static async Task<FlowStats> StatsAsync(OsduDbContext db, short partition, Guid flowId, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc.AddHours(-24);
        var (byStatus, drifted, last24) = await CountFromRecordsAsync(db, partition, flowId, since, ct).ConfigureAwait(false);
        var (blocked, stranded) = await SqlServerLedgerBulk.ParkedCountsAsync(db, partition, flowId, ct).ConfigureAwait(false);
        var lastDelivered = await db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId).MaxAsync(r => r.LastDeliveredUtc, ct).ConfigureAwait(false);
        var lastVerified = await db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId).MaxAsync(r => r.LastVerifiedUtc, ct).ConfigureAwait(false);
        var submissions = await db.DeliverySubmissions.LongCountAsync(s => s.PartitionId == partition && s.FlowId == flowId, ct).ConfigureAwait(false);
        var lastSubmission = await db.DeliverySubmissions.Where(s => s.PartitionId == partition && s.FlowId == flowId).OrderByDescending(s => s.ReceivedUtc).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return new FlowStats
        {
            Total = byStatus.Values.Sum(),
            Pending = byStatus.GetValueOrDefault("pending"),
            Delivering = byStatus.GetValueOrDefault("delivering"),
            Delivered = byStatus.GetValueOrDefault("delivered"),
            Held = byStatus.GetValueOrDefault("held"),
            Failed = byStatus.GetValueOrDefault("failed"),
            Deleted = byStatus.GetValueOrDefault("deleted"),
            Reverted = byStatus.GetValueOrDefault("reverted"),
            Waiting = byStatus.GetValueOrDefault("waiting"),
            Blocked = blocked,
            Stranded = stranded,
            Drifted = drifted,
            DeliveredLast24h = last24,
            LastDeliveredUtc = lastDelivered,
            LastVerifiedUtc = lastVerified,
            Submissions = submissions,
            LastSubmission = lastSubmission is null ? null : ToState(lastSubmission),
        };
    }

    /// <summary>
    /// A flow's counts, from the records themselves through the status index. There is no maintained aggregate: one
    /// kept the counts in a few rows per flow and was written inside the transaction of every record write, so every
    /// node delivering a flow met every other one on those rows.
    /// </summary>
    private static async Task<(Dictionary<string, long> ByStatus, long Drifted, long DeliveredSince)> CountFromRecordsAsync(
        OsduDbContext db, short partition, Guid flowId, DateTime since, CancellationToken ct)
    {
        var byStatus = await db.DeliveryRecords
            .Where(r => r.PartitionId == partition && r.FlowId == flowId)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(c => c.Status, c => c.Count, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
        var drifted = await db.DeliveryRecords
            .LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && (r.LastVerifyOutcome == "drifted" || r.LastVerifyOutcome == "missing"), ct)
            .ConfigureAwait(false);
        var deliveredSince = await db.DeliveryRecords
            .LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.LastDeliveredUtc != null && r.LastDeliveredUtc >= since, ct)
            .ConfigureAwait(false);
        return (byStatus, drifted, deliveredSince);
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

    public async Task<SourceSyncApplied> ApplySourceSyncAsync(Guid flowId, IReadOnlyList<SourceSyncFinding> findings, Guid? runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var now = Now;
        var arrivals = 0;
        var plans = 0;
        var notFound = 0;
        if (findings.Count == 0)
        {
            return new SourceSyncApplied(arrivals, plans, notFound);
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        foreach (var chunk in findings.Chunk(ChunkSize))
        {
            var keys = chunk.Select(f => f.DeliveryKey.Value).ToArray();
            var entities = await db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && keys.Contains(r.DeliveryKey)).ToDictionaryAsync(r => r.DeliveryKey, ct).ConfigureAwait(false);
            foreach (var finding in chunk)
            {
                if (!entities.TryGetValue(finding.DeliveryKey.Value, out var entity))
                {
                    throw new DeliveryException($"Record {finding.DeliveryKey} is not in the ledger, but a sync only ever reads the rows of records the ledger holds.");
                }

                // The arrival is a fact of the row, not a decision about the record: it is written as read.
                if (finding.InsertedUtc is { } inserted && entity.SourceInsertedUtc != inserted)
                {
                    entity.SourceInsertedUtc = inserted;
                    arrivals++;
                }

                // A change the ledger never saw is decided by a plan, as every change is: the flow's next run reads the
                // row by its key. The record's state moves, so the record says when.
                if (finding.RequestPlan && entity.PlanRequestedUtc is null)
                {
                    entity.PlanRequestedUtc = now;
                    entity.UpdatedUtc = now;
                    plans++;
                }

                if (finding.NotFound is { } reason)
                {
                    db.DeliveryAttempts.Add(new DeliveryAttempt
                    {
                        PartitionId = partition,
                        FlowId = flowId,
                        DeliveryKey = entity.DeliveryKey,
                        RunId = runId,
                        Worker = "sync",
                        StartedUtc = now,
                        CompletedUtc = now,
                        Outcome = StatusText.Of(AttemptOutcome.Skipped),
                        Phase = AttemptPhases.SourceMissing,
                        ResultJson = AttemptResult.WithDetail(null, Truncate(reason, 2000)),
                        SourceFileName = Truncate(finding.Origin.FileName, DeliveryModel.MaxSourceFileNameLength),
                        SourceRowNumber = finding.Origin.RowNumber,
                        SourceUpdatedUtc = finding.Origin.UpdatedUtc,
                    });
                    notFound++;
                }

                // A row the table no longer holds cannot be planned: a request to plan the record again is answered, and a
                // held or failed record a release left unblocked is blocked again, so it moves when its row is back (a row
                // the ingestion table lands again is stamped anew) or when someone releases it.
                if (finding.Gone)
                {
                    if (entity.PlanRequestedUtc is not null)
                    {
                        entity.PlanRequestedUtc = null;
                        entity.UpdatedUtc = now;
                    }

                    if (!entity.Blocked && (entity.Status == HeldStatus || entity.Status == FailedStatus) && entity.PendingDocumentRef is null)
                    {
                        entity.Blocked = true;
                        entity.LastError = "the record's row is gone from the ingestion table (deleted from it, or its key changed), so no run can plan it; "
                            + "it is planned again when its row is back, or released";
                        entity.ProblemHash = ProblemSignature.Of(entity.LastError);
                        entity.UpdatedUtc = now;
                    }
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new SourceSyncApplied(arrivals, plans, notFound);
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

    public async Task<IReadOnlyList<RecordState>> ListForVerifyAsync(Guid flowId, DateTime? verifiedBeforeUtc, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var delivered = StatusText.Of(RecordStatus.Delivered);
        var rows = await ReadAsync(
            db =>
            {
                var query = db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && r.Status == delivered && r.TargetId != null);
                if (verifiedBeforeUtc is { } before)
                {
                    query = query.Where(r => r.LastVerifiedUtc == null || r.LastVerifiedUtc < before);
                }

                return query.OrderBy(r => r.LastVerifiedUtc).Take(Math.Clamp(max, 1, 10_000)).ToListAsync(ct);
            },
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    public async Task RecordVerifyAsync(
        Guid flowId, DeliveryKey key, VerifyOutcome outcome, long? observedVersion, DateTime nowUtc, bool requeue, long? activityId = null, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var entity = await db.DeliveryRecords.FirstOrDefaultAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.DeliveryKey == key.Value, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Record {key} is not in the ledger of flow {flowId:D}.");
        if (requeue && outcome is VerifyOutcome.Drifted or VerifyOutcome.Missing)
        {
            // Reconciling is a redelivery of all of the record, marked by the statement every redelivery marks with: it
            // forgets what OSDU holds and the source version it was planned under, and asks the flow's next run to plan the
            // record again, which reads it by key whatever its incremental window holds. Only a record OSDU holds by the
            // ledger's account is marked; one a run took up since the pass read it is that run's.
            var observed = observedVersion?.ToString(CultureInfo.InvariantCulture) ?? "none";
            var expected = entity.TargetVersion?.ToString(CultureInfo.InvariantCulture) ?? "none";
            var note = $"verify: {StatusText.Of(outcome)} (observed version {observed}, expected {expected}); redelivery requested, the flow's next deliver run sends it again";
            await RedeliverKeysAsync(db, partition, flowId, [key.Value], RedeliverScope.All, marker: null, deliveredOnly: true, activityId, note, nowUtc, ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
            entity = await db.DeliveryRecords.FirstAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.DeliveryKey == key.Value, ct).ConfigureAwait(false);
        }

        entity.LastVerifiedUtc = nowUtc;
        entity.LastVerifyOutcome = StatusText.Of(outcome);
        entity.UpdatedUtc = nowUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<int> ReleaseAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, DateTime nowUtc, CancellationToken ct = default)
        => ReleaseAsync(flowId, keys is null ? ReleaseSelection.EveryBlocked : ReleaseSelection.Named(keys), null, nowUtc, ct);

    public async Task<int> ReleaseAsync(Guid flowId, ReleaseSelection selection, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return 0;
        }

        await using var db = Open();

        // Named records are released as named, a slice to a statement; a waiting one among them is sent without waiting.
        if (selection.Keys is { } named)
        {
            var released = 0;
            foreach (var slice in named.Select(k => k.Value).Chunk(WriteSlice))
            {
                ct.ThrowIfCancellationRequested();
                await StripWaitedReferencesAsync(partition, flowId, slice, ct).ConfigureAwait(false);
                released += await RetryDeadlockAsync(
                    () => SqlServerLedgerBulk.ReleaseSliceAsync(db, partition, flowId, slice, waiting: true, activityId, ReleasedNote, nowUtc, ct), ct).ConfigureAwait(false);
            }

            return released;
        }

        // A problem's records are walked through the problem index; every blocked record through the status index, a
        // custody state at a time. A release of the whole flow is for what is blocked, and leaves the waits to end on
        // their own.
        if (selection.Problem is { } problem)
        {
            return await ReleaseWalkAsync(db, partition, flowId, problem, null, activityId, nowUtc, ct).ConfigureAwait(false);
        }

        var total = 0;
        foreach (var status in BlockedStatuses)
        {
            total += await ReleaseWalkAsync(db, partition, flowId, null, status, activityId, nowUtc, ct).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>What the ledger says of a record a release unblocked without a rendered document to send.</summary>
    private const string ReleasedNote = "released; the flow's next run plans it again from its ingestion rows";

    /// <summary>The custody states a blocked record is in: what a release of every blocked record walks.</summary>
    private static readonly string[] BlockedStatuses =
        [StatusText.Of(RecordStatus.Held), StatusText.Of(RecordStatus.Failed), StatusText.Of(RecordStatus.Deleted), StatusText.Of(RecordStatus.Reverted)];

    /// <summary>
    /// Releases the records one problem keeps blocked, or the blocked records in one custody state, a page of keys at a time
    /// in the order an index holds them: each page is read, then released in a statement of its own, so neither the read
    /// nor the write ever holds more than a slice of records however many the walk reaches. A record released leaves what
    /// is walked (the problem index, or the blocked records) and the walk only moves forward, so it ends.
    /// </summary>
    private async Task<int> ReleaseWalkAsync(OsduDbContext db, short partition, Guid flowId, long? problem, string? status, long? activityId, DateTime nowUtc, CancellationToken ct)
    {
        var released = 0;
        var after = SqlServerLedgerBulk.WalkPosition.Start;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var position = after;
            var page = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.ReleasePageAsync(db, partition, flowId, problem, status, position, WriteSlice, ct), ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return released;
            }

            var keys = page.Select(p => p.DeliveryKey).ToList();
            released += await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.ReleaseSliceAsync(db, partition, flowId, keys, waiting: false, activityId, ReleasedNote, nowUtc, ct), ct).ConfigureAwait(false);
            if (page.Count < WriteSlice)
            {
                return released;
            }

            after = page[^1];
        }
    }

    public Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverScope scope, DateTime nowUtc, CancellationToken ct = default)
        => ForceRedeliverAsync(flowId, keys, new RedeliverSelection(scope, []), nowUtc, ct);

    public Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverSelection selection, DateTime nowUtc, CancellationToken ct = default)
        => ForceRedeliverAsync(flowId, keys, selection, null, nowUtc, ct);

    public async Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverSelection selection, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return 0;
        }

        await using var db = Open();
        var note = selection.Lost
            ? $"redelivery of {selection} requested: OSDU no longer holds the record, so the next deliver run brings it back under its id (a soft-deleted one restored, a purged one written again)"
            : $"redelivery of {selection} requested";

        // Parts of a payload sent in parts are named on its delivered hash; a selection of every part clears it, as a
        // whole payload's redelivery always has.
        var marker = selection is { Scope: RedeliverScope.Payload, Parts.Count: > 0 } ? PayloadParts.RedeliverMarker(selection.Parts) : null;
        if (keys is not null)
        {
            // Records OSDU lost are brought back only where the ledger holds them delivered: one it holds otherwise (held,
            // reverted, removed) is acted on through its own state.
            return await RedeliverKeysAsync(
                db, partition, flowId, keys.Select(k => k.Value), selection.Scope, marker, deliveredOnly: selection.Lost, activityId, note, nowUtc, ct, selection.Lost).ConfigureAwait(false);
        }

        // Every record OSDU holds of the flow, walked through the status index a page at a time as a release of the whole
        // flow is, so neither the read nor the write ever holds more than a slice however many records the flow delivered.
        var marked = 0;
        var after = SqlServerLedgerBulk.WalkPosition.Start;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var position = after;
            var page = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.DeliveredPageAsync(db, partition, flowId, position, nowUtc, WriteSlice, ct), ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return marked;
            }

            marked += await RedeliverKeysAsync(
                db, partition, flowId, page.Select(p => p.DeliveryKey), selection.Scope, marker, deliveredOnly: true, activityId, note, nowUtc, ct, selection.Lost).ConfigureAwait(false);
            if (page.Count < WriteSlice)
            {
                return marked;
            }

            after = page[^1];
        }
    }

    public async Task<int> RequestRenderAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return 0;
        }

        await using var db = Open();
        if (keys is not null)
        {
            var marked = 0;
            foreach (var slice in keys.Select(k => k.Value).Distinct().Chunk(WriteSlice))
            {
                ct.ThrowIfCancellationRequested();
                marked += await RetryDeadlockAsync(
                    () => SqlServerLedgerBulk.RenderSliceAsync(db, partition, flowId, slice, activityId, nowUtc, ct), ct).ConfigureAwait(false);
            }

            return marked;
        }

        // Every record OSDU holds of the flow, walked through the status index a page at a time as a redelivery of the whole
        // flow is: a record marked carries the walk's moment as its update time and its request, so the walk passes it over.
        var total = 0;
        var after = SqlServerLedgerBulk.WalkPosition.Start;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var position = after;
            var page = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.DeliveredPageAsync(db, partition, flowId, position, nowUtc, WriteSlice, ct), ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return total;
            }

            var keysOfPage = page.Select(p => p.DeliveryKey).ToList();
            total += await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.RenderSliceAsync(db, partition, flowId, keysOfPage, activityId, nowUtc, ct), ct).ConfigureAwait(false);
            if (page.Count < WriteSlice)
            {
                return total;
            }

            after = page[^1];
        }
    }

    /// <summary>
    /// Marks the named records of one ledger for redelivery of <paramref name="scope"/>, <see cref="WriteSlice"/> to a
    /// statement, each naming the records it marked under <paramref name="activityId"/> when given: the one statement a
    /// redelivery by name, a redelivery of the whole flow and a cache change's rollout all mark records with. With
    /// <paramref name="lost"/>, each is also marked as OSDU no longer holding it, as a verify that finds it missing marks it.
    /// </summary>
    private async Task<int> RedeliverKeysAsync(
        OsduDbContext db, short partition, Guid flowId, IEnumerable<Guid> keys, RedeliverScope scope, string? marker, bool deliveredOnly, long? activityId,
        string note, DateTime nowUtc, CancellationToken ct, bool lost = false)
    {
        var named = scope switch
        {
            RedeliverScope.Metadata => "metadata",
            RedeliverScope.Payload => "payload",
            _ => "all",
        };
        var marked = 0;
        foreach (var slice in keys.Distinct().Chunk(WriteSlice))
        {
            ct.ThrowIfCancellationRequested();
            marked += await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.RedeliverSliceAsync(db, partition, flowId, slice, named, marker, deliveredOnly, activityId, note, nowUtc, ct, lost), ct).ConfigureAwait(false);
        }

        return marked;
    }

    public async Task MarkRemovedAsync(
        Guid flowId, IReadOnlyList<DeliveryKey> keys, RemovalScope scope, string worker, DateTime nowUtc, string? correlationId = null, bool deliverAgain = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        if (keys.Count == 0)
        {
            return;
        }

        var then = deliverAgain ? "the flow's next run reads its row again and delivers it" : "the next run that reads its row delivers it again";
        var note = scope switch
        {
            RemovalScope.Record => $"removed from OSDU (reversible) by {worker}; {then}",
            RemovalScope.History => $"earlier versions purged from OSDU by {worker}; the latest version is still live",
            RemovalScope.Everything => $"purged from OSDU (the record and every version) by {worker}; {then}",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);

        // A removal of many records settles in chunks: one query for the rows and one save for the whole chunk,
        // rather than a round trip per record.
        foreach (var chunk in keys.Chunk(ChunkSize))
        {
            await using var db = Open();
            var ids = chunk.Select(k => k.Value).Distinct().ToList();
            var entities = await db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && ids.Contains(r.DeliveryKey)).ToListAsync(ct).ConfigureAwait(false);
            if (entities.Count != ids.Count)
            {
                var missing = ids.Except(entities.Select(e => e.DeliveryKey)).First();
                throw new DeliveryException($"Record {missing} is not in the ledger of flow {flowId:D}.");
            }

            foreach (var entity in entities)
            {
                MarkRemoved(db, entity, scope, worker, note, nowUtc, correlationId, deliverAgain);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static void MarkRemoved(OsduDbContext db, DeliveryRecord entity, RemovalScope scope, string worker, string note, DateTime nowUtc, string? correlationId, bool deliverAgain)
    {
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            PartitionId = entity.PartitionId,
            FlowId = entity.FlowId,
            DeliveryKey = entity.DeliveryKey,
            SubmissionId = entity.LastSubmissionId,
            Worker = worker,
            StartedUtc = nowUtc,
            CompletedUtc = nowUtc,
            Outcome = StatusText.Of(scope == RemovalScope.History ? AttemptOutcome.HistoryPurged : AttemptOutcome.Deleted),
            Phase = scope == RemovalScope.History ? "purge-history" : "delete",
            TargetVersion = entity.TargetVersion,
            ResultJson = AttemptResult.Removal(correlationId, entity.TargetStateJson, note),
        });

        // A history purge leaves the record live in OSDU at the version the ledger already holds, so its custody
        // state is still true and must not be disturbed: the attempt above is the whole of what happened.
        if (scope == RemovalScope.History)
        {
            entity.UpdatedUtc = nowUtc;
            return;
        }

        // Removed, not blocked: a removal is a clean-up, and the record follows its source again from here. An operator's
        // removal asks for it to be planned again, so the flow's next run reads its row by key, finds nothing delivered and
        // sends it, whether or not the row changed: what the source holds and OSDU does not is delivered again. A row the
        // ingestion table marks deleted is never sent, so a record that should stay out of OSDU is taken out of the source.
        // Released from any block it carried, since nothing of what blocked it is left in OSDU. Removed by an operator
        // rather than kept back by a problem: nothing about it is a problem to fix. The removal that opens deleting the whole
        // ledger, and an inventory's removal of an id the ledger already holds as removed, ask for nothing themselves and
        // leave any request the record carried as it is: the one is followed by a run that reads every row, the other only
        // makes OSDU agree with what the ledger already says, and the operator's removal before it asked for what it asked.
        entity.Status = StatusText.Of(RecordStatus.Deleted);
        entity.Blocked = false;
        entity.ProblemHash = null;
        if (deliverAgain)
        {
            entity.PlanRequestedUtc = nowUtc;
        }
        entity.PendingSourceFingerprint = null;
        entity.PendingSourceModifiedUtc = null;
        entity.TargetVersion = null;
        entity.TargetStateJson = null;
        entity.MetadataHash = null;
        entity.PayloadHash = null;
        entity.PayloadModifiedUtc = null;
        entity.SourceFingerprint = null;
        entity.SourceModifiedUtc = null;
        entity.LastVerifiedUtc = null;
        entity.LastVerifyOutcome = null;
        entity.LeaseOwner = null;
        entity.NextAttemptUtc = null;
        entity.AttemptCount = 0;
        entity.PendingDocumentRef = null;
        entity.WorkBatch = null;
        entity.PendingStepJson = null;
        entity.PendingReferences = null;
        entity.WaitingFor = null;
        entity.PendingMetadata = false;
        entity.PendingPayload = false;
        entity.PendingPayloadLocation = null;
        entity.PendingPayloadModifiedUtc = null;
        entity.LastError = note;
        entity.UpdatedUtc = nowUtc;
    }

    public async Task<IReadOnlyList<TargetIdReassignment>> ReassignTargetIdsAsync(
        Guid flowId, IReadOnlyList<DeliveryKey> keys, string actor, string reason, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (keys.Count == 0)
        {
            return [];
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var reassigned = new List<TargetIdReassignment>();
        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            reassigned.AddRange(await ReassignChunkAsync(partition, flowId, chunk, actor, reason, activityId, nowUtc, ct).ConfigureAwait(false));
        }

        return reassigned;
    }

    /// <summary>
    /// One chunk of <see cref="ReassignTargetIdsAsync"/> in one transaction: the records, their attempts, the activity's links
    /// to them and their search entries are written together, so a record is never left claiming an id nothing records.
    /// </summary>
    private async Task<IReadOnlyList<TargetIdReassignment>> ReassignChunkAsync(
        short partition, Guid flowId, IReadOnlyList<Guid> ids, string actor, string reason, long? activityId, DateTime nowUtc, CancellationToken ct)
    {
        var delivered = StatusText.Of(RecordStatus.Delivered);
        await using var db = Open();
        // A retried run of this block starts from the database, not from what a rolled back one left tracked, and the
        // connection leaves it at read committed, so the next read on it does not hold repeatable read locks.
        return await PooledIsolation.InTransactionAsync(db, System.Data.IsolationLevel.RepeatableRead, async () =>
        {
            var entities = await db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && ids.Contains(r.DeliveryKey)
                    && r.Status == delivered && r.ClaimedTargetId != null && r.LeaseOwner == null)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var wanted = entities
                .Select(e => (Entity: e, Lost: e.ClaimedTargetId!, Assigned: TargetId.Reassigned(e.ClaimedTargetId!, new DeliveryKey(e.DeliveryKey))))
                .Where(w => w.Assigned is not null)
                .ToList();
            var candidates = wanted.Select(w => w.Assigned!).ToList();

            // A claim is the ledger's across every flow and partition: an id another record claims is never taken.
            var taken = (await db.DeliveryRecords.AsNoTracking()
                    .Where(r => r.ClaimedTargetId != null && candidates.Contains(r.ClaimedTargetId))
                    .Select(r => r.ClaimedTargetId!)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

            var done = new List<TargetIdReassignment>();
            var states = new List<RecordState>();
            foreach (var (entity, lost, assigned) in wanted)
            {
                if (taken.Contains(assigned!))
                {
                    continue;
                }

                var note = $"given the new OSDU id {assigned} by {actor} in place of {lost}, which OSDU no longer holds ({reason}); the next deliver run writes it under the new id, and records that name {lost} still name the old one";
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    PartitionId = entity.PartitionId,
                    FlowId = entity.FlowId,
                    DeliveryKey = entity.DeliveryKey,
                    SubmissionId = entity.LastSubmissionId,
                    Worker = actor,
                    StartedUtc = nowUtc,
                    CompletedUtc = nowUtc,
                    Outcome = StatusText.Of(AttemptOutcome.Skipped),
                    Phase = AttemptPhases.ReassignId,
                    TargetVersion = entity.TargetVersion,
                    ResultJson = AttemptResult.Reassigned(lost, assigned!, entity.TargetStateJson, note),
                });

                // The record is written under the new id from now on, as a record OSDU never held: nothing it held under the
                // lost id is the new record's, and the next plan that meets it renders and sends it whole.
                entity.ClaimedTargetId = assigned;
                entity.TargetId = assigned;
                entity.TargetIdAssignedUtc = nowUtc;
                entity.TargetVersion = null;
                entity.TargetStateJson = null;
                entity.MetadataHash = null;
                entity.PayloadHash = null;
                entity.PayloadModifiedUtc = null;
                entity.SourceFingerprint = null;
                entity.PendingSourceFingerprint = null;
                entity.LastVerifiedUtc = nowUtc;
                entity.LastVerifyOutcome = null;
                entity.PlanRequestedUtc = nowUtc;
                entity.LastError = note.Length <= 2000 ? note : note[..2000];
                entity.UpdatedUtc = nowUtc;
                if (activityId is { } activity)
                {
                    db.DeliveryActivityRecords.Add(new DeliveryActivityRecord
                    {
                        PartitionId = entity.PartitionId, FlowId = entity.FlowId, DeliveryKey = entity.DeliveryKey, ActivityId = activity,
                    });
                }

                done.Add(new TargetIdReassignment(new DeliveryKey(entity.DeliveryKey), lost, assigned!));
                states.Add(ToState(entity));
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await WriteIdentitiesAsync(db, partition, flowId, states, ct).ConfigureAwait(false);
            return (IReadOnlyList<TargetIdReassignment>)done;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeliveryKey>> MarkRestoredAsync(
        Guid flowId, IReadOnlyList<PreviousVersionRestored> restored, string worker, Guid? runId, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(restored);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        if (restored.Count == 0)
        {
            return [];
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var moved = new List<DeliveryKey>();
        foreach (var chunk in restored.GroupBy(r => r.Key).Select(g => g.Last()).Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            moved.AddRange(await RetryDeadlockAsync(() => MarkRestoredChunkAsync(partition, flowId, chunk, worker, runId, nowUtc, correlationId, ct), ct).ConfigureAwait(false));
        }

        return moved;
    }

    /// <summary>
    /// One chunk of <see cref="MarkRestoredAsync"/>, in one transaction: the records are read under repeatable read, so the
    /// version and lease each is checked at are the ones it is written at. Returns the records that moved on.
    /// </summary>
    private async Task<IReadOnlyList<DeliveryKey>> MarkRestoredChunkAsync(
        short partition, Guid flowId, IReadOnlyList<PreviousVersionRestored> chunk, string worker, Guid? runId, DateTime nowUtc, string? correlationId, CancellationToken ct)
    {
        var ids = chunk.Select(r => r.Key.Value).ToList();
        var versions = chunk.Select(r => (long?)r.Restored).Distinct().ToList();
        var putBack = new[] { StatusText.Of(AttemptOutcome.Delivered), StatusText.Of(AttemptOutcome.Restored) };
        var busy = new[] { StatusText.Of(RecordStatus.Pending), StatusText.Of(RecordStatus.Delivering), StatusText.Of(RecordStatus.Waiting), StatusText.Of(RecordStatus.Deleted) };
        var reverted = StatusText.Of(RecordStatus.Reverted);
        await using var db = Open();
        // A retried run of this block starts from the database, not from what a rolled back one left tracked, and the
        // connection leaves it at read committed, so the next read on it does not hold repeatable read locks.
        return await PooledIsolation.InTransactionAsync(db, System.Data.IsolationLevel.RepeatableRead, async () =>
        {
            var entities = await db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && ids.Contains(r.DeliveryKey))
                .ToDictionaryAsync(r => r.DeliveryKey, ct)
                .ConfigureAwait(false);

            // The attempt that delivered or restored each version put back says what the record's hashes and origin become.
            var priors = (await db.DeliveryAttempts.AsNoTracking()
                    .Where(a => a.PartitionId == partition && a.FlowId == flowId && ids.Contains(a.DeliveryKey)
                        && putBack.Contains(a.Outcome) && versions.Contains(a.TargetVersion))
                    .Select(a => new { a.DeliveryKey, a.TargetVersion, a.AttemptId, a.MetadataHash, a.PayloadHash, a.SourceFileName, a.SourceRowNumber, a.SourceUpdatedUtc })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .GroupBy(a => (a.DeliveryKey, a.TargetVersion))
                .ToDictionary(g => g.Key, g => g.MaxBy(a => a.AttemptId)!);

            var moved = new List<DeliveryKey>();
            foreach (var r in chunk)
            {
                if (!entities.TryGetValue(r.Key.Value, out var entity)
                    || entity.TargetVersion != r.Replaced || entity.LeaseOwner is not null || busy.Contains(entity.Status))
                {
                    moved.Add(r.Key);
                    continue;
                }

                priors.TryGetValue((entity.DeliveryKey, (long?)r.Restored), out var prior);
                var note = string.Create(
                    CultureInfo.InvariantCulture,
                    $"reverted by {worker}: version {r.Restored}, the one before the latest, written back as version {r.NewVersion}; version {r.Replaced} stays in the record's history. Release the record or change the source to plan it again");
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    PartitionId = entity.PartitionId,
                    FlowId = entity.FlowId,
                    DeliveryKey = entity.DeliveryKey,
                    SubmissionId = entity.LastSubmissionId,
                    RunId = runId,
                    Worker = worker,
                    StartedUtc = nowUtc,
                    CompletedUtc = nowUtc,
                    Outcome = StatusText.Of(AttemptOutcome.Restored),
                    Phase = AttemptPhases.RestorePrevious,
                    MetadataHash = prior?.MetadataHash,
                    PayloadHash = prior?.PayloadHash,
                    TargetVersion = r.NewVersion,
                    ResultJson = AttemptResult.PreviousRestored(correlationId, r.TargetStateJson, r.Replaced, r.Restored, note),
                    SourceFileName = prior?.SourceFileName,
                    SourceRowNumber = prior?.SourceRowNumber,
                    SourceUpdatedUtc = prior?.SourceUpdatedUtc,
                });

                // Blocked at the source version it was left at, as a reversal leaves a record: the next plans pass it over
                // until its row moves or someone releases it. A record an earlier write back already blocked holds that
                // version among its pending columns (its own were cleared), and keeps it; so does a held or failed record,
                // whose pending columns hold the later row it was held at, which is the row it stands at now.
                if (entity.Status != reverted && !HeldAtItsRow(entity))
                {
                    entity.PendingSourceFingerprint = entity.SourceFingerprint;
                    entity.PendingSourceModifiedUtc = entity.SourceModifiedUtc ?? nowUtc;
                    entity.PendingSourceFileName = entity.SourceFileName;
                    entity.PendingSourceRowNumber = entity.SourceRowNumber;
                    entity.PendingSourceUpdatedUtc = entity.SourceUpdatedUtc;
                }

                entity.Status = reverted;
                entity.Blocked = true;
                entity.ProblemHash = null;
                entity.SourceFileName = prior?.SourceFileName;
                entity.SourceRowNumber = prior?.SourceRowNumber;
                entity.SourceUpdatedUtc = prior?.SourceUpdatedUtc;
                entity.MetadataHash = prior?.MetadataHash;
                entity.PayloadHash = prior?.PayloadHash;
                entity.PayloadModifiedUtc = null;
                entity.SourceFingerprint = null;
                entity.SourceModifiedUtc = null;
                entity.RenderContext = null;
                entity.TargetVersion = r.NewVersion;
                entity.TargetStateJson = r.TargetStateJson;
                entity.LastVerifiedUtc = null;
                entity.LastVerifyOutcome = null;
                entity.NextAttemptUtc = null;
                entity.AttemptCount = 0;
                entity.PendingDocumentRef = null;
                entity.WorkBatch = null;
                entity.PendingStepJson = null;
                entity.PendingReferences = null;
                entity.WaitingFor = null;
                entity.PendingMetadata = false;
                entity.PendingPayload = false;
                entity.PendingPayloadLocation = null;
                entity.PendingPayloadModifiedUtc = null;
                entity.PendingRenderContext = null;
                entity.PendingMetadataHash = null;
                entity.PendingPayloadHash = null;
                entity.PendingParentVersion = null;
                entity.AcceptedMetadataHash = null;
                entity.PlanRequestedUtc = null;
                entity.LastError = Truncate(note, 2000);
                entity.UpdatedUtc = nowUtc;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return (IReadOnlyList<DeliveryKey>)moved;
        }, ct).ConfigureAwait(false);
    }

    public async Task<long> EnsureCacheSetAsync(string scope, IReadOnlyList<Snapshots.CacheUsage> usages, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(usages);
        var entries = Canonical(usages);
        var hash = SetHash(scope, entries);
        if (_cacheSets.TryGetValue(hash, out var known))
        {
            return known;
        }

        var now = Now;
        await using var db = Open();
        var existing = await db.DeliveryCacheSets.FirstOrDefaultAsync(c => c.SetHash == hash, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.LastSeenUtc = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _cacheSets[hash] = existing.SetId;
            return existing.SetId;
        }

        var set = new DeliveryCacheSet
        {
            SetHash = hash,
            EntryCount = entries.Count,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        };
        db.DeliveryCacheSets.Add(set);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var usage in entries)
        {
            db.DeliveryCacheSetEntries.Add(new DeliveryCacheSetEntry
            {
                SetId = set.SetId,
                Scope = scope,
                TypeName = usage.TypeName,
                ItemId = Truncate(usage.ItemId, 512)!,
                Path = Truncate(usage.Path, 400)!,
                Kind = KindText(usage.Kind),
                ValueHash = usage.ValueHash,
                ValueText = usage.Display,
            });
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two workers of one run can mint the same set at the same time; the unique hash decides, and the
            // loser reads the winner's id rather than failing a staging batch over a race.
            var winner = await db.DeliveryCacheSets.AsNoTracking().FirstOrDefaultAsync(c => c.SetHash == hash, ct).ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            _cacheSets[hash] = winner.SetId;
            return winner.SetId;
        }

        _cacheSets[hash] = set.SetId;
        return set.SetId;
    }

    public async Task<IReadOnlyList<CacheUse>> ListCacheSetAsync(long setId, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => db.DeliveryCacheSetEntries
                .Where(e => e.SetId == setId)
                .OrderBy(e => e.Scope).ThenBy(e => e.TypeName).ThenBy(e => e.Path)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(e => new CacheUse(e.Scope, e.TypeName, e.ItemId, e.Path, ToKind(e.Kind), e.ValueHash, e.ValueText)).ToList();
    }

    public async Task<IReadOnlyList<CacheSetUse>> FindCacheSetsAsync(string scope, string typeName, IReadOnlyList<string> itemIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            return [];
        }

        var found = new List<CacheSetUse>();
        foreach (var chunk in itemIds.Chunk(LookupChunk))
        {
            var ids = chunk.ToList();
            var rows = await ReadAsync(
                db => db.DeliveryCacheSetEntries
                    .Where(e => e.Scope == scope && e.TypeName == typeName && ids.Contains(e.ItemId))
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(e => new CacheSetUse(e.SetId, e.Scope, e.TypeName, e.ItemId, e.Path, ToKind(e.Kind), e.ValueHash, e.ValueText)));
        }

        return found;
    }

    public async Task<long> CountRecordsInSetsAsync(IReadOnlyList<long> setIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setIds);
        if (setIds.Count == 0)
        {
            return 0;
        }

        return await ReadAsync(
            async db =>
            {
                long total = 0;
                foreach (var chunk in setIds.Chunk(LookupChunk))
                {
                    var ids = chunk.ToList();
                    total += await db.DeliveryRecords
                        .Where(r => r.CacheSetId != null && ids.Contains(r.CacheSetId!.Value))
                        .LongCountAsync(ct).ConfigureAwait(false);
                }

                return total;
            },
            ct).ConfigureAwait(false);
    }

    public async Task<CacheGapPage> ListCacheGapsAsync(string scope, string? typeName, bool empty, int take, int skip, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        var unlisted = KindText(Snapshots.CacheUsageKind.Unlisted);
        var unverified = KindText(Snapshots.CacheUsageKind.Unverified);
        var listed = KindText(Snapshots.CacheUsageKind.Listed);
        var emptied = KindText(Snapshots.CacheUsageKind.Empty);
        // Stands for every key of a $findAll: a change to what one key finds changes what the whole node found.
        const string AnyListing = "\n";
        return await ReadAsync(
            async db =>
            {
                var entries = db.DeliveryCacheSetEntries.Where(e => e.Scope == scope
                    && (e.Kind == unlisted || e.Kind == unverified || (e.Kind == listed && e.ValueText == string.Empty) || (empty && e.Kind == emptied)));
                if (typeName is not null)
                {
                    entries = entries.Where(e => e.TypeName == typeName);
                }

                // A gap is held per set, and counts the records built without it through the sets: they number in the thousands.
                var held = await entries
                    .Select(e => new { e.SetId, e.TypeName, e.Path, e.Kind, e.ItemId, e.ValueText })
                    .ToListAsync(ct).ConfigureAwait(false);
                var records = new Dictionary<long, long>();
                var answered = new HashSet<(long SetId, string TypeName, string Path)>();
                var listedSets = held.Where(e => e.Kind == listed).Select(e => e.SetId).Distinct().ToHashSet();
                foreach (var chunk in held.Select(e => e.SetId).Distinct().Chunk(LookupChunk))
                {
                    var ids = chunk.ToList();
                    var counts = await WaitingRecords(db, ids)
                        .GroupBy(r => r.CacheSetId!.Value)
                        .Select(g => new { SetId = g.Key, Records = g.LongCount() })
                        .ToListAsync(ct).ConfigureAwait(false);
                    foreach (var count in counts)
                    {
                        records[count.SetId] = count.Records;
                    }

                    // A $findAll reads the rows of every key it is given (each form of a reference, each of a wellbore's
                    // countries) and is missing only when none of them found a row, as the render judged it: a key that found
                    // nothing beside one that found rows left the record wanting nothing.
                    var withListings = ids.Where(listedSets.Contains).ToList();
                    if (withListings.Count == 0)
                    {
                        continue;
                    }

                    var found = await db.DeliveryCacheSetEntries
                        .Where(e => e.Scope == scope && e.Kind == listed && e.ValueText != string.Empty && withListings.Contains(e.SetId))
                        .Select(e => new { e.SetId, e.TypeName, e.Path })
                        .Distinct()
                        .ToListAsync(ct).ConfigureAwait(false);
                    foreach (var listing in found)
                    {
                        answered.Add((listing.SetId, listing.TypeName, listing.Path));
                    }
                }

                // What a refresh has since brought is no longer missing from the cache: it is a change, listed with the
                // changes, and leaves the record's set when the record's flow renders it again.
                var types = held.Select(e => e.TypeName).Distinct().ToList();
                var tags = await db.DeliveryUpdateTags.AsNoTracking()
                    .Where(t => t.Scope == scope && types.Contains(t.TypeName))
                    .Select(t => new { t.TypeName, t.Path, t.ItemId, t.Change, t.OldValue, t.SetIds })
                    .ToListAsync(ct).ConfigureAwait(false);
                var brought = new HashSet<(long SetId, string TypeName, string Path, string Key)>();
                foreach (var tag in tags)
                {
                    var keys = new List<string> { tag.ItemId };
                    if (tag.Change == "relisted")
                    {
                        keys.Add(AnyListing);
                    }
                    else if (tag.Change == "listed" && tag.OldValue is { Length: > 0 } looked)
                    {
                        keys.AddRange(looked.Split(", ").Append(looked).Select(Snapshots.CacheUsage.ListingKey));
                    }

                    foreach (var set in ParseSets(tag.SetIds))
                    {
                        foreach (var key in keys)
                        {
                            brought.Add((set, tag.TypeName, tag.Path, key));
                        }
                    }
                }

                var gaps = held
                    .Where(e => records.ContainsKey(e.SetId)
                        && !brought.Contains((e.SetId, e.TypeName, e.Path, e.Kind == listed ? AnyListing : e.ItemId))
                        && (e.Kind != listed || !answered.Contains((e.SetId, e.TypeName, e.Path))))
                    .GroupBy(e => (e.TypeName, e.Path, e.Kind, Key: e.Kind == listed ? ListedKey(e.ItemId) : e.ItemId, e.ValueText))
                    .Select(g => new CacheGap(g.Key.TypeName, g.Key.Path, ToKind(g.Key.Kind), g.Key.Key, g.Key.ValueText, g.Select(e => e.SetId).Distinct().Sum(s => records[s])))
                    .ToList();
                var page = gaps
                    .OrderByDescending(g => g.Records)
                    .ThenBy(g => g.TypeName, StringComparer.Ordinal)
                    .ThenBy(g => g.Path, StringComparer.Ordinal)
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .ThenBy(g => g.Value, StringComparer.Ordinal)
                    .ThenBy(g => g.Kind)
                    .Skip(skip)
                    .Take(take)
                    .ToList();
                return new CacheGapPage(page, gaps.Count);
            },
            ct).ConfigureAwait(false);

        // The record a listed key names is shown as a reference is written, whichever form of it was looked up.
        static string ListedKey(string key) => Rendering.CachedReferences.Parse(key) is { } reference ? Rendering.CachedReferences.Written(reference.Id) : key;
    }

    public async Task<int> TagUpdatesAsync(IReadOnlyList<UpdateTag> tags, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count == 0)
        {
            return 0;
        }

        var written = 0;
        var gate = new List<long>();
        await using var db = Open();
        foreach (var tag in tags)
        {
            // The standing tag is found by the item and the path as the ledger keeps them, so one longer than its column is
            // still found rather than raised again on every refresh.
            var itemId = UpdateTag.Kept(tag.ItemId, UpdateTag.MaxItemIdLength);
            var path = UpdateTag.Kept(tag.Path, UpdateTag.MaxTextLength);
            var open = await db.DeliveryUpdateTags.FirstOrDefaultAsync(
                t => t.Scope == tag.Scope && t.TypeName == tag.TypeName && t.ItemId == itemId && t.Path == path
                     && (t.Status == "pending" || t.Status == "approved" || t.Status == "rolling" || t.Status == Delivering),
                ct).ConfigureAwait(false);
            if (open is not null)
            {
                if (open.Status == Delivering)
                {
                    // Every record was marked, and the value moved again: the sets holding it now are marked from the start.
                    open.Status = "approved";
                    open.Cursor = null;
                    open.CursorFlowId = null;
                    open.Processed = 0;
                    open.CompletedUtc = null;
                }

                // The same value moved again. An approval was for what someone looked at, so a further move
                // reopens the question rather than riding on the old decision.
                var moved = !string.Equals(open.NewValue, UpdateTag.Kept(tag.NewValue, UpdateTag.MaxTextLength), StringComparison.Ordinal);
                open.NewValue = UpdateTag.Kept(tag.NewValue, UpdateTag.MaxTextLength);
                open.ToVersion = tag.ToVersion;
                open.Change = tag.Change;
                open.DetectedUtc = nowUtc;
                open.SetIds = string.Join(',', tag.SetIds);
                open.AffectedRecords = tag.AffectedRecords;
                if (moved && open.Status != "pending" && open.Mode.Equals("approve", StringComparison.OrdinalIgnoreCase))
                {
                    open.Status = "pending";
                    open.DecidedUtc = null;
                    open.DecidedBy = null;
                    gate.AddRange(tag.SetIds);
                }

                continue;
            }

            var approved = tag.Mode.Equals("auto", StringComparison.OrdinalIgnoreCase);
            db.DeliveryUpdateTags.Add(new DeliveryUpdateTag
            {
                Kind = tag.Kind,
                Scope = tag.Scope,
                TypeName = tag.TypeName,
                ItemId = UpdateTag.Kept(tag.ItemId, UpdateTag.MaxItemIdLength)!,
                Path = UpdateTag.Kept(tag.Path, UpdateTag.MaxTextLength)!,
                Change = tag.Change,
                OldValue = UpdateTag.Kept(tag.OldValue, UpdateTag.MaxTextLength),
                NewValue = UpdateTag.Kept(tag.NewValue, UpdateTag.MaxTextLength),
                FromVersion = tag.FromVersion,
                ToVersion = tag.ToVersion,
                Mode = tag.Mode,
                Status = approved ? "approved" : "pending",
                SetIds = string.Join(',', tag.SetIds),
                AffectedRecords = tag.AffectedRecords,
                DetectedUtc = nowUtc,
                DecidedUtc = approved ? nowUtc : null,
                DecidedBy = approved ? "system:auto" : null,
            });
            written++;
            if (!approved)
            {
                gate.AddRange(tag.SetIds);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await SetGateAsync(db, gate, gated: true, ct).ConfigureAwait(false);
        return written;
    }

    public async Task<IReadOnlyList<long>> GatedCacheSetsAsync(CancellationToken ct = default)
        => await ReadAsync(db => db.DeliveryCacheSets.Where(c => c.Gated).Select(c => c.SetId).ToListAsync(ct), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<UpdateTag>> ListTagsAsync(string? status, int max, int offset, string? scope = null, CancellationToken ct = default)
    {
        return await ReadAsync(
            async db =>
            {
                var rows = await TagQuery(db, status, scope)
                    .OrderByDescending(t => t.TagId)
                    .Skip(Math.Max(0, offset)).Take(Math.Clamp(max, 1, 1000))
                    .ToListAsync(ct).ConfigureAwait(false);
                var open = rows.Where(t => t.Status is not ("applied" or "rejected")).ToList();
                var waiting = await WaitingBySetAsync(db, open.SelectMany(t => ParseSets(t.SetIds)).Distinct().ToList(), ct).ConfigureAwait(false);
                return (IReadOnlyList<UpdateTag>)rows.Select(t =>
                {
                    var flows = t.Status is "applied" or "rejected"
                        ? new Dictionary<Guid, long>()
                        : ParseSets(t.SetIds)
                            .SelectMany(s => waiting.GetValueOrDefault(s) ?? [])
                            .GroupBy(f => f.Key)
                            .ToDictionary(g => g.Key, g => g.Sum(f => f.Value));
                    return ToTag(t) with { WaitingByFlow = flows };
                }).ToList();
            },
            ct).ConfigureAwait(false);
    }

    public Task<int> CountTagsAsync(string? status, string? scope = null, CancellationToken ct = default)
        => ReadAsync(db => TagQuery(db, status, scope).CountAsync(ct), ct);

    public async Task<int> DecideTagsAsync(IReadOnlyList<long> tagIds, bool approve, string actor, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (tagIds.Count == 0)
        {
            return 0;
        }

        await using var db = Open();
        var rows = await db.DeliveryUpdateTags.Where(t => tagIds.Contains(t.TagId) && t.Status == "pending").ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.Status = approve ? "approved" : "rejected";
            row.DecidedUtc = nowUtc;
            row.DecidedBy = actor;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await ReleaseUngatedAsync(db, rows.SelectMany(r => ParseSets(r.SetIds)).Distinct().ToList(), ct).ConfigureAwait(false);
        return rows.Count;
    }

    public async Task<UpdateRolloutBatch> RollOutTagAsync(long tagId, int batchSize, DateTime nowUtc, CancellationToken ct = default)
    {
        var size = Math.Clamp(batchSize, 1, 100_000);
        await using var db = Open();
        var tag = await db.DeliveryUpdateTags.FirstOrDefaultAsync(t => t.TagId == tagId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Update tag {tagId} is not in the ledger.");
        if (tag.Status is not ("approved" or "rolling" or Delivering))
        {
            return new UpdateRolloutBatch(tagId, 0, tag.Processed, tag.AffectedRecords, tag.Status == "applied");
        }

        var sets = ParseSets(tag.SetIds);
        if (sets.Count == 0 || tag.Status == Delivering)
        {
            var settled = await SettleAsync(db, tag, sets, nowUtc, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new UpdateRolloutBatch(tagId, 0, tag.Processed, tag.AffectedRecords, settled);
        }

        // One bounded page of records, in key and then flow order from where the last pass stopped: a change over millions
        // of records never becomes one statement, and a pass that is interrupted resumes instead of starting over. The
        // same row read by several flows is one record per flow, and each is marked in its own flow.
        var records = SetRecords(db, sets, tag.Cursor, tag.CursorFlowId);
        var page = await records
            .OrderBy(r => r.DeliveryKey)
            .ThenBy(r => r.FlowId)
            .Select(r => new { r.PartitionId, r.FlowId, r.DeliveryKey })
            .Take(size)
            .ToListAsync(ct).ConfigureAwait(false);

        if (page.Count > 0)
        {
            // A cache change rewrites the manifest row, never the payload: forgetting the metadata hash and the
            // fingerprint is what makes the next plan render and send the document again, and the curves that
            // were uploaded with it stay where they are. This is the same marking a metadata redelivery makes.
            var note = $"redelivery of metadata requested by cache change {tag.TagId}";
            foreach (var ledger in page.GroupBy(r => (r.PartitionId, r.FlowId)))
            {
                await RedeliverKeysAsync(
                    db, ledger.Key.PartitionId, ledger.Key.FlowId, ledger.Select(r => r.DeliveryKey), RedeliverScope.Metadata, null, deliveredOnly: false, null, note, nowUtc, ct)
                    .ConfigureAwait(false);
            }

            tag.Cursor = page[^1].DeliveryKey;
            tag.CursorFlowId = page[^1].FlowId;
            tag.Processed += page.Count;
        }

        tag.StartedUtc ??= nowUtc;
        tag.Status = "rolling";
        var completed = page.Count < size && await SettleAsync(db, tag, sets, nowUtc, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new UpdateRolloutBatch(tagId, page.Count, tag.Processed, tag.AffectedRecords, completed);
    }

    /// <summary>
    /// A change whose records are all marked: waiting for the flows that build them to render them again, until none is
    /// still built from the sets holding the old value. Each flow renders its own on its next run, so a cache many flows read
    /// is rolled out when the last of them has run. True when the change is rolled out.
    /// </summary>
    private static async Task<bool> SettleAsync(OsduDbContext db, DeliveryUpdateTag tag, IReadOnlyList<long> sets, DateTime nowUtc, CancellationToken ct)
    {
        var waiting = sets.Count == 0 ? 0 : await WaitingRecords(db, sets).LongCountAsync(ct).ConfigureAwait(false);
        tag.Status = waiting == 0 ? "applied" : Delivering;
        tag.CompletedUtc = waiting == 0 ? nowUtc : null;
        return waiting == 0;
    }

    /// <summary>
    /// The records still built from any of <paramref name="sets"/>: not rendered again since, not deleted from OSDU, and not
    /// reverted, since a reverted record stays as an operator's reversal left it until its row moves or it is released, and
    /// takes the change then.
    /// </summary>
    private static IQueryable<DeliveryRecord> WaitingRecords(OsduDbContext db, IReadOnlyList<long> sets)
        => db.DeliveryRecords.AsNoTracking().Where(r => r.CacheSetId != null && sets.Contains(r.CacheSetId!.Value) && r.Status != DeletedStatus && r.Status != RevertedStatus);

    public async Task<int> SettleRolloutsAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await using var db = Open();
        var tags = await db.DeliveryUpdateTags.Where(t => t.Status == Delivering).ToListAsync(ct).ConfigureAwait(false);
        if (tags.Count == 0)
        {
            return 0;
        }

        var waiting = await WaitingBySetAsync(db, tags.SelectMany(t => ParseSets(t.SetIds)).Distinct().ToList(), ct).ConfigureAwait(false);
        var settled = 0;
        foreach (var tag in tags.Where(t => ParseSets(t.SetIds).All(s => !waiting.ContainsKey(s))))
        {
            tag.Status = "applied";
            tag.CompletedUtc = nowUtc;
            settled++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return settled;
    }

    /// <summary>How many records of each flow are still built from each of <paramref name="sets"/>, by set.</summary>
    private static async Task<Dictionary<long, Dictionary<Guid, long>>> WaitingBySetAsync(OsduDbContext db, IReadOnlyList<long> sets, CancellationToken ct)
    {
        var result = new Dictionary<long, Dictionary<Guid, long>>();
        foreach (var chunk in sets.Chunk(LookupChunk))
        {
            var counts = await WaitingRecords(db, chunk)
                .GroupBy(r => new { SetId = r.CacheSetId!.Value, r.FlowId })
                .Select(g => new { g.Key.SetId, g.Key.FlowId, Records = g.LongCount() })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var count in counts)
            {
                if (!result.TryGetValue(count.SetId, out var flows))
                {
                    result[count.SetId] = flows = [];
                }

                flows[count.FlowId] = count.Records;
            }
        }

        return result;
    }

    /// <summary>
    /// The records built from any of <paramref name="sets"/> that come after the rollout cursor in key and then flow order,
    /// less those an operator took out of OSDU's current state: removed (deleted), whose next run would otherwise send them
    /// back, and reverted, whose reversal or restore would otherwise be undone by a cache change. A held or failed record is
    /// marked: a change of the values it was built from is a change of its render, and it is planned again under it.
    /// A cursor without a flow is past every record of its key. The key's lower bound is stated on its own, so each set's
    /// page is a range seek of the rollout index from the cursor on, however many records the sets hold.
    /// </summary>
    private static IQueryable<DeliveryRecord> SetRecords(OsduDbContext db, IReadOnlyList<long> sets, Guid? cursor, Guid? cursorFlow)
    {
        var rows = db.DeliveryRecords.AsNoTracking()
            .Where(r => r.CacheSetId != null && sets.Contains(r.CacheSetId!.Value) && r.Status != DeletedStatus && r.Status != RevertedStatus);
        if (cursor is not { } key)
        {
            return rows;
        }

        return cursorFlow is { } flow
            ? rows.Where(r => r.DeliveryKey.CompareTo(key) >= 0 && (r.DeliveryKey != key || r.FlowId.CompareTo(flow) > 0))
            : rows.Where(r => r.DeliveryKey.CompareTo(key) > 0);
    }

    public async Task<IReadOnlyList<UpdateTag>> ListRolloutQueueAsync(int max, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => db.DeliveryUpdateTags
                .Where(t => t.Status == "approved" || t.Status == "rolling")
                .OrderBy(t => t.DecidedUtc ?? t.DetectedUtc)
                .Take(Math.Clamp(max, 1, 1000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToTag).ToList();
    }

    /// <summary>Gates or releases sets in one statement; the set table is small, the record table is never touched.</summary>
    private static async Task SetGateAsync(OsduDbContext db, IReadOnlyList<long> setIds, bool gated, CancellationToken ct)
    {
        if (setIds.Count == 0)
        {
            return;
        }

        foreach (var chunk in setIds.Distinct().Chunk(LookupChunk))
        {
            var ids = chunk.ToList();
            await db.DeliveryCacheSets.Where(c => ids.Contains(c.SetId) && c.Gated != gated)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.Gated, gated), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Releases the sets no undecided tag covers any more.</summary>
    private static async Task ReleaseUngatedAsync(OsduDbContext db, IReadOnlyList<long> setIds, CancellationToken ct)
    {
        if (setIds.Count == 0)
        {
            return;
        }

        var pending = await db.DeliveryUpdateTags.AsNoTracking().Where(t => t.Status == "pending").Select(t => t.SetIds).ToListAsync(ct).ConfigureAwait(false);
        var stillGated = pending.SelectMany(ParseSets).ToHashSet();
        await SetGateAsync(db, setIds.Where(id => !stillGated.Contains(id)).ToList(), gated: false, ct).ConfigureAwait(false);
    }

    private static IQueryable<DeliveryUpdateTag> TagQuery(OsduDbContext db, string? status, string? scope)
    {
        var query = db.DeliveryUpdateTags.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            // Rolling out covers both halves of it: records being marked, and marked records waiting for their flows.
            var s = status.Trim().ToLowerInvariant();
            query = s == "rolling"
                ? query.Where(t => t.Status == "rolling" || t.Status == Delivering)
                : query.Where(t => t.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(scope))
        {
            var cache = scope.Trim();
            query = query.Where(t => t.Scope == cache);
        }

        return query;
    }

    /// <summary>The set's entries in a stable order, without duplicates: two renders reading the same values hash alike.</summary>
    private static IReadOnlyList<Snapshots.CacheUsage> Canonical(IReadOnlyList<Snapshots.CacheUsage> usages)
        => usages
            .GroupBy(u => (u.TypeName, u.ItemId, u.Path, u.Kind))
            .Select(g => g.First())
            .OrderBy(u => u.TypeName, StringComparer.Ordinal)
            .ThenBy(u => u.ItemId, StringComparer.Ordinal)
            .ThenBy(u => u.Path, StringComparer.Ordinal)
            .ThenBy(u => u.Kind)
            .ToList();

    private static string SetHash(string scope, IReadOnlyList<Snapshots.CacheUsage> entries)
        => Hashing.ContentHash.Of(scope + "\n" + string.Join('\n', entries.Select(e => $"{e.TypeName}|{e.ItemId}|{e.Path}|{KindText(e.Kind)}|{e.ValueHash}")));

    private static string KindText(Snapshots.CacheUsageKind kind) => kind switch
    {
        Snapshots.CacheUsageKind.Match => "match",
        Snapshots.CacheUsageKind.Empty => "empty",
        Snapshots.CacheUsageKind.Unlisted => "unlisted",
        Snapshots.CacheUsageKind.Unverified => "unverified",
        Snapshots.CacheUsageKind.Listed => "listed",
        Snapshots.CacheUsageKind.Missing => "missing",
        _ => "value",
    };

    private static Snapshots.CacheUsageKind ToKind(string kind)
        => kind.Equals("match", StringComparison.OrdinalIgnoreCase) ? Snapshots.CacheUsageKind.Match
            : kind.Equals("empty", StringComparison.OrdinalIgnoreCase) ? Snapshots.CacheUsageKind.Empty
            : kind.Equals("unlisted", StringComparison.OrdinalIgnoreCase) ? Snapshots.CacheUsageKind.Unlisted
            : kind.Equals("unverified", StringComparison.OrdinalIgnoreCase) ? Snapshots.CacheUsageKind.Unverified
            : kind.Equals("listed", StringComparison.OrdinalIgnoreCase) ? Snapshots.CacheUsageKind.Listed
            : kind.Equals("missing", StringComparison.OrdinalIgnoreCase) ? Snapshots.CacheUsageKind.Missing
            : Snapshots.CacheUsageKind.Value;

    private static List<long> ParseSets(string setIds)
        => setIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => long.TryParse(v, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();

    private static UpdateTag ToTag(DeliveryUpdateTag t) => new()
    {
        TagId = t.TagId,
        Kind = t.Kind,
        Scope = t.Scope,
        TypeName = t.TypeName,
        ItemId = t.ItemId,
        Path = t.Path,
        Change = t.Change,
        OldValue = t.OldValue,
        NewValue = t.NewValue,
        FromVersion = t.FromVersion,
        ToVersion = t.ToVersion,
        Mode = t.Mode,
        Status = t.Status,
        SetIds = ParseSets(t.SetIds),
        AffectedRecords = t.AffectedRecords,
        Processed = t.Processed,
        DetectedUtc = t.DetectedUtc,
        DecidedUtc = t.DecidedUtc,
        DecidedBy = t.DecidedBy,
        StartedUtc = t.StartedUtc,
        CompletedUtc = t.CompletedUtc,
    };

    /// <summary>The longest scope key (the flow's parameter values) a watermark is kept under.</summary>
    public const int MaxWatermarkScopeLength = 400;

    public async Task<SourceWatermark?> GetWatermarkAsync(Guid flowId, string scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var row = await ReadAsync(db => db.DeliverySourceWatermarks.FirstOrDefaultAsync(w => w.PartitionId == partition && w.FlowId == flowId && w.Scope == scope, ct), ct).ConfigureAwait(false);
        return row is null ? null : ToWatermark(row);
    }

    public async Task SetWatermarkAsync(SourceWatermark watermark, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        if (watermark.Scope.Length > MaxWatermarkScopeLength)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture, $"The parameter values of this run make a scope key of {watermark.Scope.Length} characters; the ledger keeps a watermark under at most {MaxWatermarkScopeLength}. Shorten the flow's parameter values."));
        }

        var through = DateTime.SpecifyKind(watermark.UpdatedThroughUtc, DateTimeKind.Utc);
        var partition = await WritePartitionAsync(watermark.FlowId, ct).ConfigureAwait(false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = Open();
            var entity = await db.DeliverySourceWatermarks.FirstOrDefaultAsync(x => x.PartitionId == partition && x.FlowId == watermark.FlowId && x.Scope == watermark.Scope, ct).ConfigureAwait(false);
            if (entity is null)
            {
                entity = new DeliverySourceWatermark { PartitionId = partition, FlowId = watermark.FlowId, Scope = watermark.Scope };
                db.DeliverySourceWatermarks.Add(entity);
            }
            else if (through < entity.UpdatedThroughUtc)
            {
                // A re-run of an older plan completes after a newer one: the scope's watermark stays where the newer left it.
                return;
            }

            entity.UpdatedThroughUtc = through;
            entity.SubmissionId = watermark.SubmissionId;
            entity.ContextHash = watermark.ContextHash;
            entity.RecordedUtc = DateTime.SpecifyKind(watermark.RecordedUtc, DateTimeKind.Utc);
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Two plans of the scope finished together and both inserted the first watermark: read the winner and compare again.
            }
        }
    }

    private static SourceWatermark ToWatermark(DeliverySourceWatermark w)
        => new(w.FlowId, w.Scope, DateTime.SpecifyKind(w.UpdatedThroughUtc, DateTimeKind.Utc), w.SubmissionId, DateTime.SpecifyKind(w.RecordedUtc, DateTimeKind.Utc), w.ContextHash);

    /// <summary>
    /// The most attempts one prune statement removes (4,000; tests lower it). Each statement is its own short transaction,
    /// so pruning an estate's history never holds a long lock on the attempt table, which every drain appends to, or writes
    /// one huge log record; and it stays under the 5,000 row locks at which SQL Server would lock the whole table instead.
    /// </summary>
    internal int PruneBatch { get; init; } = 4_000;

    public async Task<int> PruneAttemptsAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(PruneBatch, 1);
        var pruned = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = Open();
            // An attempt goes only when a later attempt of the same flow's record exists, so a record's last outcome is
            // always explainable. The old attempts are read in start order from the start index, and the later one is a
            // seek of the record's own timeline.
            var deleted = await db.DeliveryAttempts
                .Where(a => a.StartedUtc < olderThanUtc
                    && db.DeliveryAttempts.Any(b => b.PartitionId == a.PartitionId && b.FlowId == a.FlowId && b.DeliveryKey == a.DeliveryKey && b.AttemptId > a.AttemptId))
                .OrderBy(a => a.StartedUtc)
                .Take(PruneBatch)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
            pruned += deleted;
            if (deleted < PruneBatch)
            {
                return pruned;
            }
        }
    }

    // ---- Retrievals ----------------------------------------------------------------------------------------------

    public async Task<RetrievalState> StartRetrievalAsync(RetrievalState retrieval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(retrieval);
        var partition = await WritePartitionAsync(retrieval.FlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var entity = new DeliveryRetrieval
        {
            PartitionId = partition,
            FlowId = retrieval.FlowId,
            FlowName = retrieval.FlowName,
            RunId = retrieval.RunId,
            Actor = retrieval.Actor,
            Kinds = retrieval.Kinds,
            Query = retrieval.Query,
            WindowField = retrieval.WindowField,
            WindowFrom = retrieval.WindowFrom,
            WindowTo = retrieval.WindowTo,
            Location = retrieval.Location,
            ManifestLocation = retrieval.ManifestLocation,
            Status = retrieval.Status,
            Records = retrieval.Records,
            Files = retrieval.Files,
            Bytes = retrieval.Bytes,
            StartedUtc = retrieval.StartedUtc,
            CompletedUtc = retrieval.CompletedUtc,
            Error = Truncate(retrieval.Error, 4000),
        };
        db.DeliveryRetrievals.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return retrieval with { RetrievalId = entity.RetrievalId };
    }

    public async Task CompleteRetrievalAsync(long retrievalId, string status, long records, int files, long bytes, string? manifestLocation, string? failure, DateTime completedUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        await using var db = Open();
        var entity = await db.DeliveryRetrievals.FirstOrDefaultAsync(r => r.RetrievalId == retrievalId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Retrieval {retrievalId} is not in the ledger.");
        entity.Status = status;
        entity.Records = records;
        entity.Files = files;
        entity.Bytes = bytes;
        entity.ManifestLocation = manifestLocation ?? entity.ManifestLocation;
        entity.Error = Truncate(failure, 4000);
        entity.CompletedUtc = completedUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<RetrievalState?> LastRetrievalAsync(Guid flowId, string status, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var entity = await ReadAsync(
            db => db.DeliveryRetrievals
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && r.Status == status)
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.RetrievalId)
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
        return entity is null ? null : ToState(entity);
    }

    public async Task<IReadOnlyList<RetrievalState>> ListRetrievalsAsync(Guid flowId, int max, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var rows = await ReadAsync(
            db => db.DeliveryRetrievals
                .Where(r => r.PartitionId == partition && r.FlowId == flowId)
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.RetrievalId)
                .Take(Math.Clamp(max, 1, 1000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToState).ToList();
    }

    private static RetrievalState ToState(DeliveryRetrieval r) => new()
    {
        RetrievalId = r.RetrievalId,
        FlowId = r.FlowId,
        FlowName = r.FlowName,
        RunId = r.RunId,
        Actor = r.Actor,
        Kinds = r.Kinds,
        Query = r.Query,
        WindowField = r.WindowField,
        WindowFrom = r.WindowFrom is { } from ? DateTime.SpecifyKind(from, DateTimeKind.Utc) : null,
        WindowTo = r.WindowTo is { } to ? DateTime.SpecifyKind(to, DateTimeKind.Utc) : null,
        Location = r.Location,
        ManifestLocation = r.ManifestLocation,
        Status = r.Status,
        Records = r.Records,
        Files = r.Files,
        Bytes = r.Bytes,
        StartedUtc = DateTime.SpecifyKind(r.StartedUtc, DateTimeKind.Utc),
        CompletedUtc = r.CompletedUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
        Error = r.Error,
    };

    public async Task<ActivityRecord> StartActivityAsync(ActivityRecord activity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var partition = await WritePartitionAsync(activity.FlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var entity = new DeliveryActivity
        {
            PartitionId = partition,
            FlowId = activity.FlowId,
            FlowName = activity.FlowName,
            Kind = activity.Kind,
            Actor = Truncate(activity.Actor, 200)!,
            StartedUtc = activity.StartedUtc == default ? Now : activity.StartedUtc,
            CompletedUtc = activity.CompletedUtc,
            Outcome = activity.Outcome,
            ParametersJson = activity.ParametersJson,
            SubmissionId = activity.SubmissionId,
            DeliveryKey = activity.DeliveryKey,
            RunId = activity.RunId,
            Summary = Truncate(activity.Summary, 2000),
            Log = Truncate(activity.Log, MaxLogLength),
        };
        db.DeliveryActivities.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToRecord(entity) with { Partition = await PartitionNameAsync(partition, ct).ConfigureAwait(false) };
    }

    public async Task CompleteActivityAsync(long activityId, string outcome, string? summary, string? log, DateTime completedUtc, Guid? submissionId = null, bool idle = false, CancellationToken ct = default)
    {
        await using var db = Open();
        var entity = await db.DeliveryActivities.FirstOrDefaultAsync(a => a.ActivityId == activityId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Activity {activityId} is not in the ledger.");
        entity.Outcome = outcome;
        entity.SubmissionId ??= submissionId;
        entity.Summary = Truncate(summary, 2000);
        entity.Log = Truncate(log, MaxLogLength);
        entity.CompletedUtc = completedUtc;
        // A failed or cancelled run is never idle: the trail always shows it.
        entity.Idle = idle && outcome == "completed";
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<ActivityRecord?> GetActivityAsync(long activityId, CancellationToken ct = default)
    {
        var entity = await ReadAsync(db => db.DeliveryActivities.FirstOrDefaultAsync(a => a.ActivityId == activityId, ct), ct).ConfigureAwait(false);
        return entity is null ? null : ToRecord(entity) with { Partition = await PartitionNameAsync(entity.PartitionId, ct).ConfigureAwait(false) };
    }

    public async Task<IReadOnlyList<ActivityRecord>> ListActivitiesAsync(ActivityQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await ActivityPartitionAsync(query, ct).ConfigureAwait(false) is not { } scope)
        {
            return [];
        }

        var skip = Math.Max(0, query.Offset);
        var take = Math.Clamp(query.Max, 1, 1000);
        List<DeliveryActivity> list;
        if (scope.PartitionId is null && query.SubmissionId is null && query.RunId is null)
        {
            // Every partition's trail: each partition's newest through its own range of (PartitionId, StartedUtc), merged,
            // rather than one sort of every activity kept. One narrowed to a submission or a run seeks that id's index.
            var merged = new List<DeliveryActivity>();
            foreach (var partition in await EveryPartitionIdAsync(ct).ConfigureAwait(false))
            {
                merged.AddRange(await ActivityPageAsync(query, partition, 0, skip + take, ct).ConfigureAwait(false));
            }

            list = merged.OrderByDescending(a => a.StartedUtc).ThenByDescending(a => a.ActivityId).Skip(skip).Take(take).ToList();
        }
        else
        {
            list = await ActivityPageAsync(query, scope.PartitionId, skip, take, ct).ConfigureAwait(false);
        }

        var names = await PartitionNamesAsync(list.Select(a => a.PartitionId), ct).ConfigureAwait(false);
        return list.Select(a => ToRecord(a) with { Partition = names[a.PartitionId] }).ToList();
    }

    /// <summary>One page of activities <paramref name="query"/> selects in <paramref name="partition"/> (every one for null), newest first, without their logs.</summary>
    private Task<List<DeliveryActivity>> ActivityPageAsync(ActivityQuery query, short? partition, int skip, int take, CancellationToken ct)
        => ReadAsync(
            db => FilterActivities(db, query, partition)
                .OrderByDescending(a => a.StartedUtc)
                .ThenByDescending(a => a.ActivityId)
                .Skip(skip)
                .Take(take)
                .Select(a => new DeliveryActivity
                {
                    PartitionId = a.PartitionId,
                    ActivityId = a.ActivityId,
                    FlowId = a.FlowId,
                    FlowName = a.FlowName,
                    Kind = a.Kind,
                    Actor = a.Actor,
                    StartedUtc = a.StartedUtc,
                    CompletedUtc = a.CompletedUtc,
                    Outcome = a.Outcome,
                    ParametersJson = a.ParametersJson,
                    SubmissionId = a.SubmissionId,
                    DeliveryKey = a.DeliveryKey,
                    RunId = a.RunId,
                    Summary = a.Summary,
                    // The log is fetched per activity, not in listings.
                    Log = null,
                    Idle = a.Idle,
                })
                .ToListAsync(ct),
            ct);

    public async Task<int> CountActivitiesAsync(ActivityQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await ActivityPartitionAsync(query, ct).ConfigureAwait(false) is not { } scope)
        {
            return 0;
        }

        return await ReadAsync(db => FilterActivities(db, query, scope.PartitionId).CountAsync(ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The partition an activity listing reads: its ledger's, when it names a ledger identity; the one it names; or every
    /// partition (a null number). Null when the ledger or the partition holds no activity to read, or when it names both
    /// and the ledger belongs to another partition.
    /// </summary>
    private async Task<ActivityScope?> ActivityPartitionAsync(ActivityQuery query, CancellationToken ct)
    {
        if (query.FlowId is { } flowId)
        {
            var own = await PartitionOfAsync(flowId, ct).ConfigureAwait(false);
            return own is not null && (string.IsNullOrWhiteSpace(query.Partition) || await PartitionIdOfAsync(query.Partition, ct).ConfigureAwait(false) == own)
                ? new ActivityScope(own)
                : null;
        }

        if (!string.IsNullOrWhiteSpace(query.Partition))
        {
            return await PartitionIdOfAsync(query.Partition, ct).ConfigureAwait(false) is { } named ? new ActivityScope(named) : null;
        }

        return new ActivityScope(null);
    }

    /// <summary>The partition an activity listing reads, or null for every partition.</summary>
    private sealed record ActivityScope(short? PartitionId);

    private static IQueryable<DeliveryActivity> FilterActivities(OsduDbContext db, ActivityQuery query, short? partition)
    {
        var rows = db.DeliveryActivities.AsQueryable();
        if (partition is { } within)
        {
            rows = rows.Where(a => a.PartitionId == within);
        }

        if (query.FlowId is { } flowId)
        {
            rows = rows.Where(a => a.FlowId == flowId);
        }

        if (query.DeliveryKey is { } key)
        {
            if (partition is { } ledgerPartition && query.FlowId is { } ledger)
            {
                // A record's requests: one made for the record alone names it on its own row, and one made for many
                // records names it among the records it reached, a seek of the record's key there and of each activity's
                // id here. The two never meet, so the record's requests are both together, each once.
                var reached = db.DeliveryActivityRecords
                    .Where(l => l.PartitionId == ledgerPartition && l.FlowId == ledger && l.DeliveryKey == key)
                    .Select(l => l.ActivityId);
                rows = rows.Where(a => a.DeliveryKey == key)
                    .Concat(rows.Where(a => (a.DeliveryKey == null || a.DeliveryKey != key) && reached.Contains(a.ActivityId)));
            }
            else
            {
                rows = rows.Where(a => a.DeliveryKey == key);
            }
        }

        if (query.SubmissionId is { } submissionId)
        {
            rows = rows.Where(a => a.SubmissionId == submissionId);
        }

        if (query.RunId is { } runId)
        {
            rows = rows.Where(a => a.RunId == runId);
        }

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            rows = rows.Where(a => a.Kind == query.Kind);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            var actor = query.Actor.Trim();
            rows = rows.Where(a => a.Actor.StartsWith(actor));
        }

        if (!string.IsNullOrWhiteSpace(query.Outcome))
        {
            rows = rows.Where(a => a.Outcome == query.Outcome);
        }

        if (query.Idle is { } idle)
        {
            rows = rows.Where(a => a.Idle == idle);
        }

        if (query.SinceUtc is { } since)
        {
            rows = rows.Where(a => a.StartedUtc >= since);
        }

        if (query.UntilUtc is { } until)
        {
            rows = rows.Where(a => a.StartedUtc < until);
        }

        return rows;
    }

    internal static string? Truncate(string? text, int max)
        => text is null ? null : text.Length <= max ? text : text[..max];

    private static DeliveryAttempt ToEntity(short partition, Guid flowId, AttemptRecord attempt) => new()
    {
        PartitionId = partition,
        FlowId = flowId,
        DeliveryKey = attempt.DeliveryKey.Value,
        SubmissionId = attempt.SubmissionId,
        RunId = attempt.RunId,
        Worker = Truncate(attempt.Worker, 200)!,
        StartedUtc = attempt.StartedUtc,
        CompletedUtc = attempt.CompletedUtc,
        Outcome = StatusText.Of(attempt.Outcome),
        Phase = Truncate(attempt.Phase, 32)!,
        MetadataHash = attempt.MetadataHash,
        PayloadHash = attempt.PayloadHash,
        TargetVersion = attempt.TargetVersion,
        Error = Truncate(attempt.Error, 2000),
        ResultJson = attempt.ResultJson,
        WorkBatch = attempt.WorkBatch,
        SourceFileName = Truncate(attempt.SourceFileName, DeliveryModel.MaxSourceFileNameLength),
        SourceRowNumber = attempt.SourceRowNumber,
        SourceUpdatedUtc = attempt.SourceUpdatedUtc,
        SourceDeletedUtc = attempt.SourceDeletedUtc,
        ParentVersion = attempt.ParentVersion,
    };

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
        WorkBatch = a.WorkBatch,
        SourceFileName = a.SourceFileName,
        SourceRowNumber = a.SourceRowNumber,
        SourceUpdatedUtc = a.SourceUpdatedUtc is { } updated ? DateTime.SpecifyKind(updated, DateTimeKind.Utc) : null,
        SourceDeletedUtc = a.SourceDeletedUtc is { } deleted ? DateTime.SpecifyKind(deleted, DateTimeKind.Utc) : null,
        ParentVersion = a.ParentVersion,
    };

    private static ActivityRecord ToRecord(DeliveryActivity a) => new()
    {
        ActivityId = a.ActivityId,
        FlowId = a.FlowId,
        FlowName = a.FlowName,
        Kind = a.Kind,
        Actor = a.Actor,
        StartedUtc = a.StartedUtc,
        CompletedUtc = a.CompletedUtc,
        Outcome = a.Outcome,
        ParametersJson = a.ParametersJson,
        SubmissionId = a.SubmissionId,
        DeliveryKey = a.DeliveryKey,
        RunId = a.RunId,
        Summary = a.Summary,
        Log = a.Log,
        Idle = a.Idle,
    };

    private static WorkBatchState ToState(DeliveryWorkBatch b) => new()
    {
        SubmissionId = b.SubmissionId,
        FlowId = b.FlowId,
        Index = b.Index,
        Location = b.Location,
        RecordCount = b.RecordCount,
        Status = StatusText.ToWorkBatchStatus(b.Status),
        LeaseOwner = b.LeaseOwner,
        RunId = b.RunId,
        CreatedUtc = b.CreatedUtc,
        StartedUtc = b.StartedUtc,
        CompletedUtc = b.CompletedUtc,
        Delivered = b.Delivered,
        Held = b.Held,
        Failed = b.Failed,
        Retrying = b.Retrying,
        Waiting = b.Waiting,
        Error = b.Error,
    };

    private static void Apply(DeliverySubmission entity, SubmissionState s)
    {
        entity.SubmissionId = s.SubmissionId;
        entity.FlowId = s.FlowId;
        entity.FlowName = s.FlowName;
        entity.MappingReference = s.MappingReference;
        entity.RenderContext = s.RenderContext;
        entity.WorkLocation = Truncate(s.WorkLocation, 2000);
        entity.BatchCount = s.BatchCount;
        entity.Slices = s.Slices;
        entity.ParametersJson = s.ParametersJson;
        entity.RecordCount = s.RecordCount;
        entity.Status = StatusText.Of(s.Status);
        entity.ReceivedUtc = s.ReceivedUtc;
        entity.StartedUtc = s.StartedUtc;
        entity.CompletedUtc = s.CompletedUtc;
        entity.Planned = s.Planned;
        entity.SkippedUnchanged = s.SkippedUnchanged;
        entity.AwaitingApproval = s.AwaitingApproval;
        entity.SkippedStale = s.SkippedStale;
        entity.UnchangedAtPush = s.UnchangedAtPush;
        entity.Blocked = s.Blocked;
        entity.Delivered = s.Delivered;
        entity.Held = s.Held;
        entity.Failed = s.Failed;
        entity.Waiting = s.Waiting;
        entity.Error = Truncate(s.Error, 4000);
        entity.Kind = SubmissionKinds.All.Contains(s.Kind, StringComparer.Ordinal)
            ? s.Kind
            : throw new DeliveryException($"Submission {s.SubmissionId:D}: '{s.Kind}' is not a submission kind; it is one of {string.Join(", ", SubmissionKinds.All)}.");
        entity.Untracked = s.Untracked;
        entity.SourceConnection = Truncate(s.SourceConnection, 400)!;
        entity.SourceObject = Truncate(s.SourceObject, 400)!;
        entity.WindowFromUtc = s.WindowFromUtc;
        entity.WindowToUtc = s.WindowToUtc;
        entity.SourceWindowJson = s.SourceWindowJson;
        entity.RunId = s.RunId;
    }

    private static SubmissionState ToState(DeliverySubmission e) => new()
    {
        SubmissionId = e.SubmissionId,
        FlowId = e.FlowId,
        FlowName = e.FlowName,
        MappingReference = e.MappingReference,
        RenderContext = e.RenderContext,
        WorkLocation = e.WorkLocation,
        BatchCount = e.BatchCount,
        Slices = e.Slices,
        ParametersJson = e.ParametersJson,
        RecordCount = e.RecordCount,
        Status = StatusText.ToSubmissionStatus(e.Status),
        ReceivedUtc = e.ReceivedUtc,
        StartedUtc = e.StartedUtc,
        CompletedUtc = e.CompletedUtc,
        Planned = e.Planned,
        SkippedUnchanged = e.SkippedUnchanged,
        AwaitingApproval = e.AwaitingApproval,
        SkippedStale = e.SkippedStale,
        UnchangedAtPush = e.UnchangedAtPush,
        Blocked = e.Blocked,
        Delivered = e.Delivered,
        Held = e.Held,
        Failed = e.Failed,
        Waiting = e.Waiting,
        Error = e.Error,
        Kind = e.Kind,
        Untracked = e.Untracked,
        SourceConnection = e.SourceConnection,
        SourceObject = e.SourceObject,
        WindowFromUtc = e.WindowFromUtc is { } from ? DateTime.SpecifyKind(from, DateTimeKind.Utc) : null,
        WindowToUtc = e.WindowToUtc is { } to ? DateTime.SpecifyKind(to, DateTimeKind.Utc) : null,
        SourceWindowJson = e.SourceWindowJson,
        RunId = e.RunId,
    };

    private static RecordState ToState(DeliveryRecord r) => new()
    {
        DeliveryKey = new DeliveryKey(r.DeliveryKey),
        FlowId = r.FlowId,
        SourceKey = r.SourceKey,
        SourceKeyJson = r.SourceKeyJson,
        Label = r.Label,
        MappingName = r.MappingName,
        RenderContext = r.RenderContext,
        SourceFingerprint = r.SourceFingerprint,
        SourceModifiedUtc = r.SourceModifiedUtc,
        SourceFileName = r.SourceFileName,
        SourceRowNumber = r.SourceRowNumber,
        SourceUpdatedUtc = r.SourceUpdatedUtc,
        SourceInsertedUtc = r.SourceInsertedUtc,
        MetadataHash = r.MetadataHash,
        PayloadHash = r.PayloadHash,
        PayloadModifiedUtc = r.PayloadModifiedUtc,
        TargetId = r.TargetId,
        ClaimedTargetId = r.ClaimedTargetId,
        TargetIdAssignedUtc = r.TargetIdAssignedUtc is { } assigned ? DateTime.SpecifyKind(assigned, DateTimeKind.Utc) : null,
        OccupiedTargetId = r.OccupiedTargetId,
        OccupiedTargetVersion = r.OccupiedTargetVersion,
        TargetVersion = r.TargetVersion,
        Status = StatusText.ToRecordStatus(r.Status),
        LastDeliveredUtc = r.LastDeliveredUtc,
        LastVerifiedUtc = r.LastVerifiedUtc,
        LastVerifyOutcome = StatusText.ToVerifyOutcome(r.LastVerifyOutcome),
        LeaseOwner = r.LeaseOwner,
        LastSubmissionId = r.LastSubmissionId,
        AttemptCount = r.AttemptCount,
        NextAttemptUtc = r.NextAttemptUtc,
        LastError = r.LastError,
        PendingDocumentRef = r.PendingDocumentRef,
        WorkBatch = r.WorkBatch,
        TargetStateJson = r.TargetStateJson,
        PendingStepJson = r.PendingStepJson,
        PendingRenderContext = r.PendingRenderContext,
        PendingSourceFingerprint = r.PendingSourceFingerprint,
        PendingSourceModifiedUtc = r.PendingSourceModifiedUtc,
        PendingSourceFileName = r.PendingSourceFileName,
        PendingSourceRowNumber = r.PendingSourceRowNumber,
        PendingSourceUpdatedUtc = r.PendingSourceUpdatedUtc,
        PendingMetadataHash = r.PendingMetadataHash,
        PendingPayloadHash = r.PendingPayloadHash,
        PendingPayloadModifiedUtc = r.PendingPayloadModifiedUtc,
        PendingPayloadLocation = r.PendingPayloadLocation,
        PendingMetadata = r.PendingMetadata,
        PendingPayload = r.PendingPayload,
        PendingReferences = RecordReferences.Decode(r.PendingReferences),
        WaitingFor = r.WaitingFor,
        Blocked = r.Blocked,
        ProblemHash = r.ProblemHash,
        ValidationOutcome = r.ValidationOutcome,
        ValidationProblems = r.ValidationProblems,
        ValidatedUtc = r.ValidatedUtc,
        AssertionFailures = r.AssertionFailures,
        AcceptedMetadataHash = r.AcceptedMetadataHash,
        CacheSetId = r.CacheSetId,
        ParentId = r.ParentId,
        ParentVersion = r.ParentVersion,
        PendingParentVersion = r.PendingParentVersion,
        PlanRequestedUtc = r.PlanRequestedUtc,
        CreatedUtc = r.CreatedUtc,
        UpdatedUtc = r.UpdatedUtc,
    };
}

/// <summary>The short status strings the ledger tables store, and their enum forms.</summary>
internal static class StatusText
{
    public static string Of(RecordStatus status) => status switch
    {
        RecordStatus.Pending => "pending",
        RecordStatus.Delivering => "delivering",
        RecordStatus.Delivered => "delivered",
        RecordStatus.Held => "held",
        RecordStatus.Failed => "failed",
        RecordStatus.Deleted => "deleted",
        RecordStatus.Reverted => "reverted",
        RecordStatus.Waiting => "waiting",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static RecordStatus ToRecordStatus(string text) => text switch
    {
        "pending" => RecordStatus.Pending,
        "delivering" => RecordStatus.Delivering,
        "delivered" => RecordStatus.Delivered,
        "held" => RecordStatus.Held,
        "failed" => RecordStatus.Failed,
        "deleted" => RecordStatus.Deleted,
        "reverted" => RecordStatus.Reverted,
        "waiting" => RecordStatus.Waiting,
        _ => throw new DeliveryException($"Unknown record status '{text}' in the ledger."),
    };

    public static string Of(SubmissionStatus status) => status.ToString().ToLowerInvariant();

    public static SubmissionStatus ToSubmissionStatus(string text)
        => Enum.TryParse<SubmissionStatus>(text, ignoreCase: true, out var s) ? s : throw new DeliveryException($"Unknown submission status '{text}' in the ledger.");

    public static string Of(AttemptOutcome outcome) => outcome.ToString().ToLowerInvariant();

    public static AttemptOutcome ToAttemptOutcome(string text)
        => Enum.TryParse<AttemptOutcome>(text, ignoreCase: true, out var o) ? o : throw new DeliveryException($"Unknown attempt outcome '{text}' in the ledger.");

    public static string Of(VerifyOutcome outcome) => outcome.ToString().ToLowerInvariant();

    public static VerifyOutcome? ToVerifyOutcome(string? text)
        => text is null ? null : Enum.TryParse<VerifyOutcome>(text, ignoreCase: true, out var o) ? o : null;

    public static string Of(WorkBatchStatus status) => status.ToString().ToLowerInvariant();

    public static WorkBatchStatus ToWorkBatchStatus(string text)
        => Enum.TryParse<WorkBatchStatus>(text, ignoreCase: true, out var s) ? s : throw new DeliveryException($"Unknown work batch status '{text}' in the ledger.");
}
