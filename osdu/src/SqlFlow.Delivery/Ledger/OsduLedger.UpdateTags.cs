using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// The update tags that roll a cache change out (osdu/docs/reference/concepts/partition-cache.md, From a cache change to
// OSDU): one tag per change a refresh found, the sets an undecided change touches gated, a decision on it, and the
// rollout that marks its records a batch at a time and closes it once no flow builds a record from the old value.
public sealed partial class OsduLedger
{
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

    public async Task<IReadOnlyList<UpdateTag>> CacheChangesBetweenAsync(string scope, string fromVersion, string toVersion, int max, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(toVersion);

        // A version's label is its capture instant (20260910T165153Z), so the labels order the captures; a change waiting for
        // approval, or rejected, reached no record. The partition's changes of each status are one range of their index.
        string[] reached = ["approved", "rolling", Delivering, "applied"];
#pragma warning disable CA1309 // Translated to SQL's comparison of the labels, which are ASCII digits and letters: no overload taking a StringComparison is translatable.
        var rows = await ReadAsync(
            db => db.DeliveryUpdateTags.AsNoTracking()
                .Where(t => t.Scope == scope && reached.Contains(t.Status)
                    && string.Compare(t.ToVersion, fromVersion) > 0 && string.Compare(t.ToVersion, toVersion) <= 0)
                .OrderBy(t => t.TagId)
                .Take(Math.Clamp(max, 1, 10_000))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
#pragma warning restore CA1309
        return rows.Select(ToTag).ToList();
    }

    public async Task<IReadOnlyDictionary<DeliveryKey, PlanRequestOrigin>> PlanRequestOriginsAsync(
        Guid flowId, IReadOnlyList<(DeliveryKey Key, DateTime AskedUtc)> asked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asked);
        var found = new Dictionary<DeliveryKey, PlanRequestOrigin>();
        if (asked.Count == 0 || await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return found;
        }

        foreach (var chunk in asked.GroupBy(a => a.Key).Select(g => (Key: g.Key, AskedUtc: g.Max(a => a.AskedUtc))).Chunk(LookupChunk))
        {
            var keys = chunk.Select(a => a.Key.Value).ToArray();
            var earliest = chunk.Min(a => a.AskedUtc);
            var latest = chunk.Max(a => a.AskedUtc);

            // The activities naming the records, as their one record or through the records they reached, that were under way
            // at some request of the chunk: one seek of each record's key in either index, bounded by the chunk's moments.
            var rows = await ReadAsync(
                async db =>
                {
                    var direct = db.DeliveryActivities.AsNoTracking()
                        .Where(a => a.PartitionId == partition && a.FlowId == flowId && a.DeliveryKey != null && keys.Contains(a.DeliveryKey.Value)
                            && a.StartedUtc <= latest && (a.CompletedUtc == null || a.CompletedUtc >= earliest))
                        .Select(a => new { Key = a.DeliveryKey!.Value, a.ActivityId, a.Kind, a.Actor, a.StartedUtc, a.CompletedUtc });
                    var reached = from l in db.DeliveryActivityRecords.AsNoTracking()
                                  join a in db.DeliveryActivities.AsNoTracking() on new { l.PartitionId, l.ActivityId } equals new { a.PartitionId, a.ActivityId }
                                  where l.PartitionId == partition && l.FlowId == flowId && keys.Contains(l.DeliveryKey)
                                      && a.StartedUtc <= latest && (a.CompletedUtc == null || a.CompletedUtc >= earliest)
                                  select new { Key = l.DeliveryKey, a.ActivityId, a.Kind, a.Actor, a.StartedUtc, a.CompletedUtc };
                    return (await direct.ToListAsync(ct).ConfigureAwait(false)).Concat(await reached.ToListAsync(ct).ConfigureAwait(false)).ToList();
                },
                ct).ConfigureAwait(false);

            var byKey = rows.ToLookup(r => r.Key);
            foreach (var (key, at) in chunk)
            {
                var origin = byKey[key.Value]
                    .Where(r => r.StartedUtc <= at && (r.CompletedUtc is null || r.CompletedUtc >= at))
                    .OrderByDescending(r => r.StartedUtc)
                    .ThenByDescending(r => r.ActivityId)
                    .FirstOrDefault();
                if (origin is not null)
                {
                    found[key] = new PlanRequestOrigin(origin.ActivityId, origin.Kind, origin.Actor, origin.StartedUtc);
                }
            }
        }

        return found;
    }

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
}
