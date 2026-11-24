using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>One stored dependency of a cache set: which cache and cached path it holds, and what it held.</summary>
public sealed record CacheUse(string Scope, string TypeName, string ItemId, string Path, Snapshots.CacheUsageKind Kind, string ValueHash, string ValueText);

/// <summary>A cache set that holds one cached value, for the impact query.</summary>
public sealed record CacheSetUse(long SetId, string Scope, string TypeName, string ItemId, string Path, Snapshots.CacheUsageKind Kind, string ValueHash, string ValueText);

/// <summary>
/// One thing records were built without: a value no record of the type answered to (<c>unlisted</c>), a key no row was
/// listed under (<c>listed</c>, with no rows), an id written without its record (<c>unverified</c>), or a path that held
/// nothing (<c>empty</c>). <see cref="Key"/> is what was looked for (the record id for a path read empty or an unverified
/// id), <see cref="Path"/> the field it was looked for under, and <see cref="Records"/> how many records were built so.
/// </summary>
public sealed record CacheGap(string TypeName, string Path, Snapshots.CacheUsageKind Kind, string Key, string Value, long Records);

/// <summary>One page of the gaps of a partition's cache, and how many there are in all.</summary>
public sealed record CacheGapPage(IReadOnlyList<CacheGap> Items, int Total);

/// <summary>What one rollout pass did, and what is left of the tag.</summary>
public sealed record UpdateRolloutBatch(long TagId, long Marked, long Processed, long Affected, bool Completed);

/// <summary>The intervention that asked a record to be planned again: the audit trail's entry, what it was, who made it and when.</summary>
public sealed record PlanRequestOrigin(long ActivityId, string Kind, string Actor, DateTime StartedUtc);

public sealed record UpdateTag
{
    /// <summary>The most characters of <see cref="ItemId"/> the ledger keeps.</summary>
    public const int MaxItemIdLength = 512;

    /// <summary>The most characters of <see cref="Path"/>, <see cref="OldValue"/> and <see cref="NewValue"/> the ledger keeps.</summary>
    public const int MaxTextLength = 400;

    /// <summary>A text as the ledger keeps it: whole when it fits, else its first <paramref name="max"/> characters.</summary>
    public static string? Kept(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];

    public long TagId { get; init; }

    public string Kind { get; init; } = "cache";

    /// <summary>The partition whose cache the change was found in.</summary>
    public required string Scope { get; init; }

    public required string TypeName { get; init; }

    public required string ItemId { get; init; }

    public required string Path { get; init; }

    /// <summary>
    /// changed, removed, unmatched, listed (a lookup table now lists a key records looked up and found no row under, or a
    /// type of OSDU records now holds a record by a value records found none by), relisted (the rows a key finds are no
    /// longer the ones records read every one of), or found (the cache now holds a record records reference as an
    /// unverified id).
    /// </summary>
    public required string Change { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public string? FromVersion { get; init; }

    public required string ToVersion { get; init; }

    /// <summary>auto or approve.</summary>
    public required string Mode { get; init; }

    /// <summary>pending, approved, rejected, rolling (records being marked), delivering (every record marked, some not yet rendered again by their flow) or applied.</summary>
    public string Status { get; init; } = "pending";

    /// <summary>The cache sets holding the value that moved.</summary>
    public IReadOnlyList<long> SetIds { get; init; } = [];

    public long AffectedRecords { get; init; }

    public long Processed { get; init; }

    public DateTime DetectedUtc { get; init; }

    public DateTime? DecidedUtc { get; init; }

    public string? DecidedBy { get; init; }

    public DateTime? StartedUtc { get; init; }

    public DateTime? CompletedUtc { get; init; }

    /// <summary>A tag waits for a decision only under approve; auto is decided as it is written.</summary>
    public bool WaitsForApproval => Mode.Equals("approve", StringComparison.OrdinalIgnoreCase) && Status.Equals("pending", StringComparison.OrdinalIgnoreCase);

    /// <summary>Records still to be marked for redelivery.</summary>
    public long Remaining => Math.Max(0, AffectedRecords - Processed);

    /// <summary>
    /// The records of each flow still built from the value that moved, as the ledger listed the change: the flows that have
    /// not rendered them again since. Empty for a change rolled out or rejected.
    /// </summary>
    public IReadOnlyDictionary<Guid, long> WaitingByFlow { get; init; } = new Dictionary<Guid, long>();

    /// <summary>The records, of every flow, still built from the value that moved.</summary>
    public long Waiting => WaitingByFlow.Values.Sum();

    public string Describe() => Change switch
    {
        "removed" => $"{TypeName} '{ItemId}' is no longer in the cache (it held {Path} = '{OldValue}')",
        "unmatched" => $"{TypeName} '{ItemId}' no longer matches by {Path} = '{OldValue}'",
        "found" => $"{TypeName} now holds '{ItemId}', which delivered records reference as an unverified id",
        "listed" when Snapshots.CacheUsage.CompositeText(Path, OldValue) is { } together
            => $"{TypeName} now holds '{ItemId}', where {together}, which delivered records found no row meeting together",
        "listed" => NewValue is null
            ? $"{TypeName} now lists '{OldValue}' as '{ItemId}', which gives no {Path}"
            : $"{TypeName} now lists '{OldValue}' as '{ItemId}', giving {Path} = '{NewValue}'",
        "relisted" => $"{TypeName} rows whose {Path} holds '{ItemId}' are now {Rows(NewValue)}, where they were {Rows(OldValue)}",
        _ when OldValue is null => $"{TypeName} '{ItemId}': {Path} gave no value and now gives '{NewValue}'",
        _ => $"{TypeName} '{ItemId}': {Path} changed from '{OldValue}' to '{NewValue}'",
    };

    /// <summary>The rows a listing names, as a description reads them.</summary>
    private static string Rows(string? ids) => string.IsNullOrEmpty(ids) ? "none" : ids;
}

/// <summary>A held record waiting on what a cache set found missing: its flow, as the ledger names it, and its key.</summary>
public sealed record HeldOnCache(Guid FlowId, string FlowName, DeliveryKey Key);

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds the cache sets records were rendered with, and the update
/// tags that roll a cache change out. A caller that uses only this part takes it rather than the whole ledger;
/// <see cref="OsduLedger"/> implements every part.
/// </summary>
public interface ILedgerCacheSets
{
    /// <summary>
    /// The held records, of every flow and partition, whose held render read one of <paramref name="setIds"/>, at most
    /// <paramref name="max"/> of them: the records a cache version that brings in what those sets found missing releases
    /// (osdu/docs/reference/concepts/partition-cache.md, A record held for a reference waits for it).
    /// </summary>
    Task<IReadOnlyList<HeldOnCache>> HeldOnCacheSetsAsync(IReadOnlyCollection<long> setIds, int max, CancellationToken ct = default);

    /// <summary>
    /// The id of the cache set holding exactly these values of <paramref name="scope"/>, creating it the first time it
    /// is seen. A render hands over what it consumed and gets back one number to put on the record, so records that read
    /// the same values cost one set between them. The set is marked used, so the render-context sweep
    /// (<see cref="SweepCacheSetsAsync"/>) leaves it until the record points at it.
    /// </summary>
    Task<long> EnsureCacheSetAsync(string scope, IReadOnlyList<Snapshots.CacheUsage> usages, CancellationToken ct = default);

    /// <summary>
    /// Removes every set no record points at (as its current set or the set it is held on) that no render has used since
    /// <paramref name="unusedSinceUtc"/>, with the values it holds, a short transaction of a few thousand values at a time
    /// (<see cref="CacheSetKeeping"/>); then every value no set holds any more, each kept once for the sets that hold it.
    /// Safe to interrupt, to repeat and to run beside renders and other sweeps.
    /// </summary>
    Task<CacheSetSweep> SweepCacheSetsAsync(DateTime unusedSinceUtc, CancellationToken ct = default);

    /// <summary>The values behind one set, for a record's history page.</summary>
    Task<IReadOnlyList<CacheUse>> ListCacheSetAsync(long setId, CancellationToken ct = default);

    /// <summary>
    /// The sets that hold a cached value of the given items of one cache, with the value each of them holds. This is the
    /// impact query: it runs over the sets, never over the records, so it stays the same size as the cache.
    /// </summary>
    Task<IReadOnlyList<CacheSetUse>> FindCacheSetsAsync(string scope, string typeName, IReadOnlyList<string> itemIds, CancellationToken ct = default);

    /// <summary>
    /// Every set of one cache that noted values a findBy line of several comparisons found no row meeting together
    /// (<see cref="Snapshots.CacheUsage.CompositeMiss"/>), with the values it noted. They are read by the prefix their items
    /// share (<see cref="Snapshots.CacheUsage.CompositeMarker"/>), so the impact query reads them alone, whatever else the
    /// type's sets hold.
    /// </summary>
    Task<IReadOnlyList<CacheSetUse>> FindCompositeMissesAsync(string scope, string typeName, CancellationToken ct = default);

    /// <summary>How many delivered records were built from these sets.</summary>
    Task<long> CountRecordsInSetsAsync(IReadOnlyList<long> setIds, CancellationToken ct = default);

    /// <summary>
    /// What records of <paramref name="scope"/>'s cache were built without, most records first: each value a node found no
    /// cached record by (a wellbore the cache does not hold), each key a <c>$findAll</c> found no row under (a field no
    /// access group lists) in any of the forms it was asked for, a reference with and without its version separator, shown
    /// once, and each id written without the record it names, with how many records were built so. With
    /// <paramref name="typeName"/>, the gaps of one cached type; with <paramref name="empty"/>, the paths read that held
    /// nothing as well (a wellbore without a field), which include the empty fields a <c>$findAll</c> asked for. Every gap
    /// is filled by the refresh that brings what is missing, which tags the records and redelivers them. Paged by
    /// <paramref name="skip"/> and <paramref name="take"/> in that order, with the count of every gap.
    /// </summary>
    Task<CacheGapPage> ListCacheGapsAsync(string scope, string? typeName, bool empty, int take, int skip, CancellationToken ct = default);

    /// <summary>
    /// Writes the tags a cache change produced, one per change rather than one per record, and gates the sets an
    /// unapproved change touches. Returns how many tags were new; a change already open for the same value updates
    /// the standing tag, and reopens it when the value moved again after an approval.
    /// </summary>
    Task<int> TagUpdatesAsync(IReadOnlyList<UpdateTag> tags, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>The gated sets, which a plan reads once per run to know which records are held back.</summary>
    Task<IReadOnlyList<long>> GatedCacheSetsAsync(CancellationToken ct = default);

    /// <summary>
    /// The cache changes of <paramref name="scope"/> found by the captures after <paramref name="fromVersion"/>, up to and with
    /// <paramref name="toVersion"/>, that reach records (approved or rolled out, not waiting or rejected): what a delivery's
    /// reason names when a record rendered against <paramref name="fromVersion"/> renders against <paramref name="toVersion"/>
    /// (osdu/docs/reference/concepts/delivery-reasons.md). At most <paramref name="max"/>, oldest
    /// first; a plan reads them once for each version its records were last rendered against.
    /// </summary>
    Task<IReadOnlyList<UpdateTag>> CacheChangesBetweenAsync(string scope, string fromVersion, string toVersion, int max, CancellationToken ct = default);

    /// <summary>
    /// The intervention that asked each record to be planned again, as the audit trail recorded it: the newest activity naming
    /// the record (as its one record, or among the records it reached) that was under way when the request was made. A request
    /// no intervention made (a cache rollout, a parent or child landing, a sync of the record's row) has none, and is left out.
    /// </summary>
    Task<IReadOnlyDictionary<DeliveryKey, PlanRequestOrigin>> PlanRequestOriginsAsync(
        Guid flowId, IReadOnlyList<(DeliveryKey Key, DateTime AskedUtc)> asked, CancellationToken ct = default);

    /// <summary>
    /// The tags in a status, newest first; with a cache name, only the changes that cache's refreshes found. <c>rolling</c>
    /// covers the changes being marked and the ones waiting for their flows. Each open change carries the records of each flow
    /// still built from the value that moved.
    /// </summary>
    Task<IReadOnlyList<UpdateTag>> ListTagsAsync(string? status, int max, int offset, string? scope = null, CancellationToken ct = default);

    Task<int> CountTagsAsync(string? status, string? scope = null, CancellationToken ct = default);

    /// <summary>
    /// Decides tags: approving lets the rollout carry the change out, rejecting leaves the delivered documents
    /// alone. Either way the sets stop being gated unless another undecided tag still covers them.
    /// </summary>
    Task<int> DecideTagsAsync(IReadOnlyList<long> tagIds, bool approve, string actor, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Carries one batch of an approved tag: marks up to <paramref name="batchSize"/> of its records, of every flow, for
    /// redelivery in key and then flow order from the tag's cursor (asking each flow's next run to plan them again),
    /// advances the cursor and reports what is left. A change over millions of records is drained a batch at a time by a caller that decides
    /// the pace. Once every record is marked the change waits (<c>delivering</c>) until no flow still builds one of them from
    /// the old value, and only then is it rolled out (<c>applied</c>); a call on a waiting change checks that again.
    /// </summary>
    Task<UpdateRolloutBatch> RollOutTagAsync(long tagId, int batchSize, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>The approved tags with rollout still to do, oldest decision first.</summary>
    Task<IReadOnlyList<UpdateTag>> ListRolloutQueueAsync(int max, CancellationToken ct = default);

    /// <summary>
    /// Closes every change whose records are all marked and none of which any flow still builds from the old value: the
    /// last flow reading it has run. Returns how many it closed.
    /// </summary>
    Task<int> SettleRolloutsAsync(DateTime nowUtc, CancellationToken ct = default);
}
