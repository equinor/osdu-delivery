using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// The cache sets records were rendered with (osdu/docs/reference/concepts/partition-cache.md, From a cache change to
// OSDU): a set minted once for the values a render read and shared by every record that read the same, swept once no
// record points at it, and read by a record's history, by the impact query a refresh runs over the sets, and by the
// list of what records were built without.
public sealed partial class OsduLedger
{
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

    public async Task<long> EnsureCacheSetAsync(string scope, IReadOnlyList<Snapshots.CacheUsage> usages, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(usages);
        var entries = Canonical(usages);
        var hash = SetHash(scope, entries);
        if (_cacheSets.TryGetValue(hash, out var known) && Now - known.CheckedUtc < CacheSetKeeping.RecheckAfter)
        {
            return known.SetId;
        }

        for (var attempt = 1; ; attempt++)
        {
            var now = Now;
            await using var db = Open();
            var existing = await db.DeliveryCacheSets.AsNoTracking()
                .Where(c => c.SetHash == hash)
                .Select(c => (long?)c.SetId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (existing is { } found)
            {
                // Marking the set used is what keeps the sweep off it until the render's record points at it. A set the sweep
                // removed between the read and the mark is gone with its hash, and the next attempt mints it again.
                var marked = await db.DeliveryCacheSets
                    .Where(c => c.SetId == found)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSeenUtc, now), ct).ConfigureAwait(false);
                if (marked == 1)
                {
                    _cacheSets[hash] = new KnownCacheSet(found, now);
                    return found;
                }

                if (attempt >= 3)
                {
                    throw new DeliveryException(
                        $"Cache set {found} of partition '{scope}' was removed by the render-context sweep each of {attempt} times it was about to be used; run the delivery again.");
                }

                continue;
            }

            try
            {
                var setId = await MintCacheSetAsync(db, scope, hash, entries, now, ct).ConfigureAwait(false);
                _cacheSets[hash] = new KnownCacheSet(setId, now);
                return setId;
            }
            catch (SqlException ex) when (attempt < 3 && IsDuplicate(ex))
            {
                // Another run minted the same set meanwhile (the parts of a split delivery plan at the same moment and
                // render the same cached values): the set's unique hash refused this one, and the next attempt reads
                // the set that run committed.
            }
        }
    }

    /// <summary>
    /// Writes a set and what it holds in one transaction, so no run reads a set without its values: a run stopped between
    /// the two writes leaves neither, and a set another run is minting is seen only once it is whole. A value is kept once
    /// however many sets hold it (<c>osdu.CacheSetValue</c>), written first when it is new, and the set names it by its
    /// number (<see cref="SqlServerLedgerBulk.MintCacheSetAsync"/>).
    /// </summary>
    private static async Task<long> MintCacheSetAsync(
        OsduDbContext db, string scope, string hash, IReadOnlyList<Snapshots.CacheUsage> entries, DateTime now, CancellationToken ct)
    {
        // The database cuts a text longer than its column without a word, so each is checked or cut here, as its column is.
        if (scope.Length > CacheSetScopeLength)
        {
            throw new DeliveryException(
                $"A set of cached values names partition '{scope}', longer than the {CacheSetScopeLength} characters a set's value keeps; a data-partition-id is at most {Snapshots.CacheScope.MaxLength}.");
        }

        var json = new StringBuilder((entries.Count * 160) + 2).Append('[');
        for (var ordinal = 0; ordinal < entries.Count; ordinal++)
        {
            var usage = entries[ordinal];
            if (usage.TypeName.Length > CacheSetTypeLength)
            {
                throw new DeliveryException(
                    $"A set of cached values of partition '{scope}' names the cached type '{usage.TypeName}', longer than the {CacheSetTypeLength} characters a set's value keeps.");
            }

            if (ordinal > 0)
            {
                json.Append(',');
            }

            json.Append(JsonSerializer.Serialize(
                new object[]
                {
                    ordinal,
                    usage.TypeName,
                    Truncate(usage.ItemId, Snapshots.CacheUsage.MaxItemIdLength)!,
                    Truncate(usage.Path, Snapshots.CacheUsage.MaxPathLength)!,
                    KindText(usage.Kind),
                    usage.ValueHash,
                    usage.Display,
                },
                CacheSetJson));
        }

        var entriesJson = json.Append(']').ToString();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            () => SqlServerLedgerBulk.MintCacheSetAsync(db, scope, hash, entriesJson, entries.Count, now, ct)).ConfigureAwait(false);
    }

    /// <summary>The longest partition name a set's value keeps (<c>osdu.CacheSetValue.Scope</c>).</summary>
    private const int CacheSetScopeLength = 200;

    /// <summary>The longest cached type name a set's value keeps (<c>osdu.CacheSetValue.TypeName</c>).</summary>
    private const int CacheSetTypeLength = 200;

    /// <summary>How a set's values are written for the database to read: every character as it is, none escaped away.</summary>
    private static readonly JsonSerializerOptions CacheSetJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public Task<CacheSetSweep> SweepCacheSetsAsync(DateTime unusedSinceUtc, CancellationToken ct = default)
        => SweepCacheSetsAsync(unusedSinceUtc, CacheSetKeeping.SetsPerRound, CacheSetKeeping.EntriesPerRound, CacheSetKeeping.ValuesPerRound, ct);

    /// <summary>
    /// <see cref="SweepCacheSetsAsync(DateTime, CancellationToken)"/> in rounds of at most <paramref name="setsPerRound"/>
    /// sets and <paramref name="entriesPerRound"/> entries, each its own short transaction, walking the sets in key order
    /// until none is left past the last one looked at; then the values the same way, <paramref name="valuesPerRound"/> at a
    /// time, removing those no set holds: the values only the sets just removed held, and those of a set that was never
    /// written whole.
    /// </summary>
    internal async Task<CacheSetSweep> SweepCacheSetsAsync(DateTime unusedSinceUtc, int setsPerRound, int entriesPerRound, int valuesPerRound, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(setsPerRound, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(entriesPerRound, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(valuesPerRound, 1);
        long sets = 0;
        long entries = 0;
        var after = long.MinValue;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = Open();
            var round = await SqlServerLedgerBulk.SweepCacheSetsAsync(db, unusedSinceUtc, after, setsPerRound, entriesPerRound, ct)
                .ConfigureAwait(false);
            sets += round.Sets;
            entries += round.Entries;
            if (round.Last is not { } last)
            {
                break;
            }

            after = last;
        }

        long values = 0;
        after = long.MinValue;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = Open();
            var round = await SqlServerLedgerBulk.SweepCacheSetValuesAsync(db, after, valuesPerRound, ct).ConfigureAwait(false);
            values += round.Values;
            if (round.Last is not { } last)
            {
                return new CacheSetSweep(sets, entries, values);
            }

            after = last;
        }
    }

    /// <summary>A cache set this ledger resolved, and when it last checked the set against the database.</summary>
    private readonly record struct KnownCacheSet(long SetId, DateTime CheckedUtc);

    public async Task<IReadOnlyList<CacheUse>> ListCacheSetAsync(long setId, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => CacheSetHeld(db)
                .Where(v => v.SetId == setId)
                .OrderBy(v => v.Scope).ThenBy(v => v.TypeName).ThenBy(v => v.Path).ThenBy(v => v.ItemId).ThenBy(v => v.Kind)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(v => new CacheUse(v.Scope, v.TypeName, v.ItemId, v.Path, ToKind(v.Kind), HashText(v.ValueHash), v.ValueText)).ToList();
    }

    /// <summary>A value a set holds: a row of <c>osdu.CacheSetEntry</c> with the value it names.</summary>
    private sealed class CacheSetHeldValue
    {
        public long SetId { get; init; }

        public string Scope { get; init; } = string.Empty;

        public string TypeName { get; init; } = string.Empty;

        public string ItemId { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public string Kind { get; init; } = string.Empty;

        public byte[] ValueHash { get; init; } = [];

        public string ValueText { get; init; } = string.Empty;
    }

    /// <summary>
    /// What the sets hold: each entry with the value it names (<c>osdu.CacheSetValue</c>). A read that names a cached
    /// record seeks the values by partition, type and item, and reaches the sets through the values' numbers; one that
    /// names a set seeks the set's entries, and each one's value by its number.
    /// </summary>
    private static IQueryable<CacheSetHeldValue> CacheSetHeld(OsduDbContext db)
        => from entry in db.DeliveryCacheSetEntries.AsNoTracking()
           join value in db.DeliveryCacheSetValues.AsNoTracking() on entry.ValueId equals value.ValueId
           select new CacheSetHeldValue
           {
               SetId = entry.SetId,
               Scope = value.Scope,
               TypeName = value.TypeName,
               ItemId = value.ItemId,
               Path = value.Path,
               Kind = value.Kind,
               ValueHash = value.ValueHash,
               ValueText = value.ValueText,
           };

    /// <summary>A value's hash as the ledger hands it out: <c>ContentHash</c>'s lower-case hex of the bytes the database keeps.</summary>
    private static string HashText(byte[] hash) => Convert.ToHexStringLower(hash);

    private static CacheSetUse ToUse(CacheSetHeldValue v)
        => new(v.SetId, v.Scope, v.TypeName, v.ItemId, v.Path, ToKind(v.Kind), HashText(v.ValueHash), v.ValueText);

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
                db => CacheSetHeld(db)
                    .Where(v => v.Scope == scope && v.TypeName == typeName && ids.Contains(v.ItemId))
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            found.AddRange(rows.Select(ToUse));
        }

        return found;
    }

    public async Task<IReadOnlyList<CacheSetUse>> FindCompositeMissesAsync(string scope, string typeName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        var unlisted = KindText(Snapshots.CacheUsageKind.Unlisted);
        // A constant prefix is a range of the binary-collated item id, so the type's composite misses are read by a seek of
        // the values (IX_CacheSetValue_Scope_TypeName_ItemId_Path_Kind_ValueHash), never by a pass over everything its sets hold.
        var rows = await ReadAsync(
            db => CacheSetHeld(db)
                .Where(v => v.Scope == scope && v.TypeName == typeName && v.ItemId.StartsWith(Snapshots.CacheUsage.CompositeMarker) && v.Kind == unlisted)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToUse).ToList();
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
                var entries = CacheSetHeld(db).Where(e => e.Scope == scope
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

                    var found = await CacheSetHeld(db)
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
                        // Values a line of several comparisons looked up together are kept under one key of them all.
                        keys.AddRange(Snapshots.CacheUsage.CompositeKeys(tag.Path, looked));
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
}
