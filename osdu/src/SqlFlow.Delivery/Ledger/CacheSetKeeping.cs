namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// How long a set of cached values outlives its use (osdu/docs/reference/concepts/storage-and-retention.md, The render
/// context). Only a record's current set is ever read: the record's page, change detection, and holding and releasing all
/// start from the set a record points at, and no attempt or other history names a set. So a set no record points at is
/// removed once no render has used it for <see cref="UnusedFor"/>, and a ledger checks a set it remembers against the
/// database at least every <see cref="RecheckAfter"/>: a set it hands a render was used within that time, so it is never one
/// the sweep may take before the render's record is written.
/// </summary>
public static class CacheSetKeeping
{
    /// <summary>How long a set no record points at stays after a render last used it.</summary>
    public static readonly TimeSpan UnusedFor = TimeSpan.FromDays(1);

    /// <summary>How long a ledger hands out a set it remembers before it checks the set against the database again.</summary>
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromHours(1);

    /// <summary>The most sets one round of the sweep looks at.</summary>
    internal const int SetsPerRound = 200;

    /// <summary>
    /// The most values one round of the sweep removes (a set holding more is removed alone): few enough that a round's
    /// transaction stays short and takes row locks, never a lock on the whole table the renders write to.
    /// </summary>
    internal const int EntriesPerRound = 2_000;
}

/// <summary>What a sweep of the render context removed: the sets no record pointed at, and the values they held.</summary>
public sealed record CacheSetSweep(long Sets, long Entries);
