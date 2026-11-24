namespace SqlFlow.Delivery.Data;

// The cache sets records were built from and the changes that reach them
// (osdu/docs/reference/concepts/partition-cache.md, From a cache change to OSDU): each set, the values it holds, and
// the update tags that roll a cache change out.

/// <summary>
/// One distinct combination of cached values that records were built from: the set of (type, cached record, path,
/// value) a render consumed. Records that read the same values share a set; a record whose render reads other values
/// points at another. A record points at its set; a cache change finds the sets that hold the changed value and, through
/// them, the records to update. A set no record points at is read by nothing, and the render-context sweep removes it
/// once no render has used it for a day (<c>CacheSetKeeping</c>).
/// </summary>
public sealed class DeliveryCacheSet
{
    public long SetId { get; set; }

    /// <summary>Content hash of the set's entries: the identity a render computes without a round trip.</summary>
    public string SetHash { get; set; } = string.Empty;

    public int EntryCount { get; set; }

    /// <summary>
    /// Set while a change to one of these values is tagged and unapproved. The planner reads the gated sets once
    /// per run and skips their records, so holding records back never means writing to them.
    /// </summary>
    public bool Gated { get; set; }

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// A cached value as a render read it: what it was read from (the partition, the cached type, the cached record or the key
/// looked up, the path, and how the render used it) and what it held. It is kept once however many sets hold it, so a set
/// names it by its number (<see cref="DeliveryCacheSetEntry"/>), and a cache change finds the sets holding a cached record
/// by seeking the record here. A value no set holds is read by nothing, and the render-context sweep removes it
/// (<c>CacheSetKeeping</c>).
/// </summary>
public sealed class DeliveryCacheSetValue
{
    public long ValueId { get; set; }

    /// <summary>The partition whose cache the value was read from: the partition the delivery flow delivers to.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cached type's short name (UnitOfMeasure).</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>The cached record's OSDU id, or the key a lookup was asked for.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>What the mapping read: <c>id</c>, <c>Name</c>, <c>NameAlias.AliasName</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// How the render used it: match (the value the source resolved by), value (a value written into the document), empty
    /// (a path that held no value), unlisted (a key a lookup table listed no row under), listed (the rows a $findAll found
    /// by a key), unverified (an id written as an unverified reference) or missing (an id the cache did not hold).
    /// </summary>
    public string Kind { get; set; } = "value";

    /// <summary>The SHA-256 of the whole value consumed (<c>ContentHash</c>'s, as bytes): what tells two values of one place apart.</summary>
    public byte[] ValueHash { get; set; } = [];

    /// <summary>The value as it was consumed, truncated for display.</summary>
    public string ValueText { get; set; } = string.Empty;
}

/// <summary>That a set holds a value (<see cref="DeliveryCacheSetValue"/>, by its number). A set holds a value once.</summary>
public sealed class DeliveryCacheSetEntry
{
    public long EntryId { get; set; }

    public long SetId { get; set; }

    /// <summary>The value the set holds: what was read, and what it read at the time (<see cref="DeliveryCacheSetValue"/>).</summary>
    public long ValueId { get; set; }
}

/// <summary>
/// One change to the cache that delivered records were built from, and what happens about it. A tag is written per
/// change, not per record: a corrected unit name is one decision covering every record that read it, with the count
/// of what it affects. Under <c>approve</c> the affected sets are gated until someone decides; under <c>auto</c> it
/// is approved as written. Either way the update is rolled out in batches from <see cref="Cursor"/>, so a change
/// touching millions of records drains at a controlled rate instead of flooding the estate.
/// </summary>
public sealed class DeliveryUpdateTag
{
    public long TagId { get; set; }

    /// <summary>What caused the tag; <c>cache</c> today.</summary>
    public string Kind { get; set; } = "cache";

    /// <summary>The partition whose cache the change was found in.</summary>
    public string Scope { get; set; } = string.Empty;

    public string TypeName { get; set; } = string.Empty;

    public string ItemId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// changed (the value moved, or a path that gave none gives one), removed (the cached record is gone), unmatched (what it
    /// matched by is gone), listed (a lookup table now lists a key records looked up and found no row under, or a type now
    /// holds a record by a value records found none by), relisted (the rows a key finds are no longer the ones records read)
    /// or found (the cache now holds a record records reference as an unverified id).
    /// </summary>
    public string Change { get; set; } = "changed";

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string? FromVersion { get; set; }

    public string ToVersion { get; set; } = string.Empty;

    /// <summary>auto or approve, as the cache declared for this type when the tag was written.</summary>
    public string Mode { get; set; } = "approve";

    /// <summary>pending, approved, rejected, rolling, delivering or applied.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>The cache sets holding the changed value, comma separated; the records are found through them.</summary>
    public string SetIds { get; set; } = string.Empty;

    /// <summary>How many delivered records were built from the old value when the change was found.</summary>
    public long AffectedRecords { get; set; }

    /// <summary>How many of them the rollout has marked for redelivery so far.</summary>
    public long Processed { get; set; }

    /// <summary>
    /// Where the rollout got to, in delivery-key order and then flow order, so a pass resumes rather than restarts: the
    /// delivery key of the last record marked.
    /// </summary>
    public Guid? Cursor { get; set; }

    /// <summary>The flow of the last record marked; with <see cref="Cursor"/>, where the rollout got to.</summary>
    public Guid? CursorFlowId { get; set; }

    public DateTime DetectedUtc { get; set; }

    public DateTime? DecidedUtc { get; set; }

    public string? DecidedBy { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }
}
