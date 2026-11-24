using System.Data.SqlTypes;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

// A record's state as runs read it (osdu/docs/reference/concepts/ledger.md, osdu.Record): records by key, the OSDU ids
// records have claimed, the records asked to be planned again, whether a flow still has records to send and when the
// next is due, the keys a listing matches, the OSDU ids a run changed since a moment, and the watermarks of a flow's
// incremental reads.
public sealed partial class OsduLedger
{
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
                // key, so a page is a seek past the last one read. A record whose access is restricted keeps its request
                // until the restriction is lifted: every plan would leave it as it is, so no run reads its row for it.
                var query = db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && r.PlanRequestedUtc != null && r.RestrictionId == null);

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

    public async Task<bool> HasPendingAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, BatchRange? batches = null, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return false;
        }

        var pending = StatusText.Of(RecordStatus.Pending);
        var delivering = StatusText.Of(RecordStatus.Delivering);
        var scoped = batches is not null;
        var first = batches?.First ?? 0;
        var beyond = batches?.Beyond ?? 0;
        return await ReadAsync(
            db => db.DeliveryRecords.AnyAsync(
                r => r.PartitionId == partition
                    && r.FlowId == flowId
                    && (submissionId == null || r.LastSubmissionId == submissionId)
                    && (!scoped || (r.WorkBatch >= first && r.WorkBatch < beyond))
                    && (r.Status == pending || r.Status == delivering),
                ct),
            ct).ConfigureAwait(false);
    }

    public async Task<DateTime?> NextDueAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, BatchRange? batches = null, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        var pending = StatusText.Of(RecordStatus.Pending);
        var scoped = batches is not null;
        var first = batches?.First ?? 0;
        var beyond = batches?.Beyond ?? 0;
        return await ReadAsync(
            db => db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && (submissionId == null || r.LastSubmissionId == submissionId)
                    && (!scoped || (r.WorkBatch >= first && r.WorkBatch < beyond))
                    && r.Status == pending && r.NextAttemptUtc != null && r.NextAttemptUtc > nowUtc)
                .MinAsync(r => r.NextAttemptUtc, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ChangedSinceAsync(Guid flowId, DateTime sinceUtc, int max, string? listedUnder = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        // Two ranges, each on an index of its own: (PartitionId, FlowId, LastDeliveredUtc) for what was sent, and
        // (PartitionId, FlowId, Status, UpdatedUtc) for what was deleted or reverted. A record sent and then removed in the same
        // run is answered by the second; the reader asks OSDU what it holds of each either way. The target state is read only
        // when its ids are asked for.
        var listed = listedUnder is not null;
        var delivered = StatusText.Of(RecordStatus.Delivered);
        var deleted = StatusText.Of(RecordStatus.Deleted);
        var reverted = StatusText.Of(RecordStatus.Reverted);
        var changed = new List<string>();
        var sent = await ReadAsync(
            db => db.DeliveryRecords.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && r.LastDeliveredUtc != null && r.LastDeliveredUtc >= sinceUtc
                    && r.Status == delivered && r.TargetId != null)
                .Select(r => new { Id = r.TargetId!, State = listed ? r.TargetStateJson : null })
                .Take(max)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        foreach (var row in sent)
        {
            changed.Add(row.Id);
            changed.AddRange(ListedIds(row.State, listedUnder));
        }

        if (sent.Count < max)
        {
            var taken = await ReadAsync(
                db => db.DeliveryRecords.AsNoTracking()
                    .Where(r => r.PartitionId == partition && r.FlowId == flowId && (r.Status == deleted || r.Status == reverted) && r.UpdatedUtc >= sinceUtc
                        && r.TargetId != null)
                    .Select(r => new { Id = r.TargetId!, State = listed ? r.TargetStateJson : null })
                    .Take(max - sent.Count)
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            foreach (var row in taken)
            {
                changed.Add(row.Id);
                changed.AddRange(ListedIds(row.State, listedUnder));
            }
        }

        return [.. changed.Distinct(StringComparer.Ordinal).Take(max)];
    }

    /// <summary>The ids a record's target state lists under <paramref name="value"/>, as a JSON array of strings; none when it lists none or no value is asked for.</summary>
    private static IEnumerable<string> ListedIds(string? targetStateJson, string? value)
    {
        if (value is null || !JsonMerge.ToValues(targetStateJson).TryGetValue(value, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(text)?.Where(id => !string.IsNullOrWhiteSpace(id)) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            // A value that is not a list of ids names nothing to read back; the records themselves still are.
            return [];
        }
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

    /// <summary>The longest scope key (the flow's parameter values) a watermark is kept under.</summary>
    public const int MaxWatermarkScopeLength = 400;

    /// <summary>Why a scope key of <paramref name="length"/> characters keeps no watermark.</summary>
    public static string WatermarkScopeTooLong(int length)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"The parameter values of this run make a scope key of {length} characters; the ledger keeps a watermark under at most {MaxWatermarkScopeLength}. Shorten the flow's parameter values, or give a parameter fewer values at once.");

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
            throw new DeliveryException(WatermarkScopeTooLong(watermark.Scope.Length));
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
}
