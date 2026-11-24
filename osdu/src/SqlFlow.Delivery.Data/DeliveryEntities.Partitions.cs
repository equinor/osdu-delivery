namespace SqlFlow.Delivery.Data;

// Ledgers and partitions (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and partitions): the partitions the
// ledger keys by, every ledger, the OSDU partitions registered with the catalog, and the access restrictions registered
// for each.

/// <summary>
/// A partition the ledger keeps rows under, and the number that stands for it in every ledger key
/// (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and partitions). A partition's name is a data-partition-id
/// of up to 200 characters; carried in every key of the record table it would push the source file index past SQL
/// Server's 1700-byte limit and widen every index of the largest tables, so the ledger keys by this number and names
/// the partition here. A row is added the first time a ledger of the partition is registered, and is never renumbered
/// or removed: every ledger row names its partition by it.
/// </summary>
public sealed class DeliveryLedgerPartition
{
    /// <summary>The number every ledger key starts with. <see cref="DeliveryModel.UnassignedPartition"/> (0) has no row.</summary>
    public short PartitionId { get; set; }

    /// <summary>The data-partition-id, as runs name it; compared regardless of case, so a partition has one number.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One ledger: the rows of one flow (or interface of a source) in one partition, under one ledger identity
/// (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and partitions). The engine registers a ledger when a run
/// binds its flow, before it writes a row, with the partition the run delivers to: the one the flow names, the
/// registry's, or the one its <c>data-partition-id</c> header resolves to. From then on the ledger belongs to that
/// partition. Every ledger row carries the partition in its key, written from here, so which partition a record, a
/// submission or an audit entry belongs to is known from the ledger alone.
/// </summary>
/// <remarks>
/// A ledger the upgrade to partition keys could not place (no interface named its partition and its records carry no OSDU
/// id, or ids of more than one partition) is <see cref="DeliveryModel.UnassignedPartition"/> until its next run adopts it,
/// which refuses a ledger holding records delivered to another partition than the run's.
/// </remarks>
public sealed class DeliveryLedger
{
    /// <summary>The widest flow name a ledger keeps.</summary>
    public const int MaxFlowNameLength = 200;

    /// <summary>The partition the ledger belongs to, or <see cref="DeliveryModel.UnassignedPartition"/>.</summary>
    public short PartitionId { get; set; }

    /// <summary>The ledger identity every row of the ledger carries.</summary>
    public Guid FlowId { get; set; }

    /// <summary>delivery, retrieval, assertion, dimension, inventory or download: the kind of flow whose ledger this is.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The flow (for a source with interfaces, the source) the ledger belongs to.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The interface of a source, or empty for a flow in the single form.</summary>
    public string Interface { get; set; } = string.Empty;

    /// <summary>The ledger's name, as pages show it: <c>&lt;ledger&gt;@&lt;partition&gt;</c> for a partition keeping a ledger of its own.</summary>
    public string LedgerName { get; set; } = string.Empty;

    public DateTime RegisteredUtc { get; set; }
}

/// <summary>
/// An OSDU partition registered with the catalog (osdu/docs/reference/concepts/partitions.md, The partition registry): the
/// partitions every run, cache and ledger of the module is keyed by. A flow that names no partitions serves every
/// registered one, so the same documents deploy to every environment; a run that names none runs in the one marked the
/// default. A flow that names its partitions serves those of them that are registered.
/// </summary>
/// <remarks>
/// Removing a partition deletes nothing kept under it: its caches, ledgers and runs stay as they are, and no run can target
/// it until it is registered again. At most one partition is the default, which a filtered unique index keeps.
/// </remarks>
public sealed class DeliveryPartition
{
    /// <summary>The widest description a partition keeps.</summary>
    public const int MaxDescriptionLength = 400;

    /// <summary>The data-partition-id, as runs name it and caches and ledgers are keyed by it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the partition is for (the environment, the platform), in the words of whoever registered it.</summary>
    public string? Description { get; set; }

    /// <summary>True for the one partition a run that names none runs in.</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// True while the control plane's configuration (<c>Osdu:Partitions</c>) names the partition: the control plane
    /// registered it from there, and only the configuration describes it, makes it the default or takes it out. A partition
    /// the configuration stops naming stays registered with this cleared.
    /// </summary>
    public bool IsConfigured { get; set; }

    /// <summary>
    /// How many versions of the partition's cache keep their records, newest first, the current one counted
    /// (osdu/docs/reference/concepts/partition-cache.md, Retention): every refresh prunes the records of the older ones, except
    /// a version a delivery flow pins. 3 unless an admin sets another number, from 2 to 1000; the configuration never sets
    /// it, so it may be changed on a partition the configuration names. <see cref="UpdatedUtc"/> and
    /// <see cref="UpdatedBy"/> say when and by whom it was last set.
    /// </summary>
    public int CacheVersionsKept { get; set; } = 3;

    public DateTime CreatedUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}

/// <summary>
/// An access restriction registered for one OSDU partition (osdu/docs/reference/concepts/access-restriction.md): a name,
/// what it is for, and the entitlements groups a restricted record's ACL in OSDU is replaced with, as its viewers and its
/// owners. A record restricted under it names it (<see cref="DeliveryRecord.RestrictionId"/>), so it cannot be removed
/// while any record still carries it. Its groups never change once registered: OSDU holds them on the records restricted
/// under it, so a different set of groups is a restriction of its own.
/// </summary>
public sealed class DeliveryAccessRestriction
{
    /// <summary>The longest name a restriction keeps.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The widest description a restriction keeps.</summary>
    public const int MaxDescriptionLength = 400;

    /// <summary>The longest list of groups, as JSON, a restriction keeps as its viewers or its owners.</summary>
    public const int MaxGroupsLength = 2000;

    public int RestrictionId { get; set; }

    /// <summary>The data-partition-id the restriction's groups belong to: records of that partition alone are restricted under it.</summary>
    public string Partition { get; set; } = string.Empty;

    /// <summary>What operators pick it by, unique within its partition whatever its case.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the restriction is for (a legal hold, a quality quarantine), in the words of whoever registered it.</summary>
    public string? Description { get; set; }

    /// <summary>The groups a restricted record's <c>acl.viewers</c> becomes, as a JSON array of group emails.</summary>
    public string Viewers { get; set; } = "[]";

    /// <summary>The groups a restricted record's <c>acl.owners</c> becomes, as a JSON array of group emails.</summary>
    public string Owners { get; set; } = "[]";

    public DateTime CreatedUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}
