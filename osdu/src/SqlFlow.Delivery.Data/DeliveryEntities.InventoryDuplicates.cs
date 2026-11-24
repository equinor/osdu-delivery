namespace SqlFlow.Delivery.Data;

// The duplicate checks of an inventory's ids (osdu/docs/reference/flow/inventory-duplicates.md): each check, the groups
// of ids it found sharing a key, and every id it read.

/// <summary>
/// A check of an inventory's ids for duplicate records (osdu/docs/reference/flow/inventory-duplicates.md): the properties it
/// downloaded of every id into its own table (<see cref="TableName"/>, made at run time, as a dimension's is), the key an operator
/// groups them by, as written, where the check stands and what it found, and the build of the inventory it read. The ids that
/// hold equal values at the key are the <see cref="DeliveryInventoryDuplicateMember"/> rows of its
/// <see cref="DeliveryInventoryDuplicateGroup"/> rows. Its download and its picks run as runs of the inventory flow; the copies it
/// picks to remove become a selection a removal takes.
/// </summary>
public sealed class DeliveryInventoryDuplicateCheck
{
    public short PartitionId { get; set; }

    public long InventoryDuplicateCheckId { get; set; }

    public int InventoryId { get; set; }

    /// <summary>The platform run the check ran as; null until it is queued.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Who asked for the check.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>The key the groups are formed by, as the operator wrote it, in one spelling (JSON): the path of each property whose values the copies hold equal.</summary>
    public string KeyJson { get; set; } = string.Empty;

    /// <summary>A SHA-256 of <see cref="KeyJson"/> in hex, by which the same key asked again of the same build finds the check already made.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>
    /// The properties the check downloaded, each with its column of the check's table (JSON list of path and column): a key of
    /// any of them groups the table again without reading OSDU.
    /// </summary>
    public string? AttributesJson { get; set; }

    /// <summary>
    /// The check's table in the <c>osdu</c> schema (<c>dup_&lt;check&gt;</c>), once its download made it: a row for each id, a column
    /// for each property's values and one for the hash of them. Null before its download and after a sweep dropped it.
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>Who set the key the groups are formed by: the operator who asked for the check, or the one who changed its key since.</summary>
    public string? KeyedBy { get; set; }

    /// <summary>When the key the groups are formed by was set.</summary>
    public DateTime? KeyedUtc { get; set; }

    /// <summary>When a pick last read the copies of every group; null while groups wait to be picked (a key set since starts them over).</summary>
    public DateTime? PickedUtc { get; set; }

    /// <summary>queued, running, completed, failed or expired.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>What a running check is doing: <c>keys</c> (downloading the properties of every id) or <c>copies</c> (reading the copies of each group); null otherwise.</summary>
    public string? Phase { get; set; }

    /// <summary>What the reader should know of how the check read OSDU (JSON list).</summary>
    public string? NotesJson { get; set; }

    /// <summary>The inventory's last build when the check read OSDU; a later build makes the check stale.</summary>
    public long? BuildRunId { get; set; }

    /// <summary>The ids the inventory served when the check started.</summary>
    public long Candidates { get; set; }

    /// <summary>The records the search returned while the check read the keys.</summary>
    public long Checked { get; set; }

    /// <summary>The inventory's ids that hold a value at one of the key's properties at least.</summary>
    public long Keyed { get; set; }

    /// <summary>The inventory's ids that hold no value at any of the key's properties, passed over.</summary>
    public long Unkeyed { get; set; }

    /// <summary>Records the search returned that the inventory serves no row of: written since its last build, or outside its query.</summary>
    public long Outside { get; set; }

    /// <summary>Ids the search returned at another version than the inventory listed: changed since its last build.</summary>
    public long Changed { get; set; }

    /// <summary>The groups of ids that share a key.</summary>
    public long Groups { get; set; }

    /// <summary>The ids in those groups.</summary>
    public long Copies { get; set; }

    /// <summary>The groups whose copies the check has read and judged, which a page following it counts against <see cref="Groups"/>.</summary>
    public long GroupsJudged { get; set; }

    /// <summary>The groups whose copy to keep is settled: picked by the check, or chosen by an operator.</summary>
    public long Decided { get; set; }

    /// <summary>The groups left for an operator to look at.</summary>
    public long Review { get; set; }

    /// <summary>The copies picked to remove.</summary>
    public long ToRemove { get; set; }

    /// <summary>The requests the check sent: searches and storage reads.</summary>
    public long Requests { get; set; }

    /// <summary>The selection last made of the copies picked to remove; null until one is made.</summary>
    public long? SelectionId { get; set; }

    /// <summary>When an operator last chose the copy a group keeps, which a selection made before then no longer reflects.</summary>
    public DateTime? ChosenUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    /// <summary>Why the check's download failed, or why its last pick stopped, redacted; null for one that did neither.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// One group of ids a duplicate check found sharing a key: how many copies, the values they share, and how it came out: the copy
/// it keeps and why, picked by the check or chosen by an operator (who, and when), or left for review with why.
/// </summary>
public sealed class DeliveryInventoryDuplicateGroup
{
    public short PartitionId { get; set; }

    public long InventoryDuplicateCheckId { get; set; }

    /// <summary>The group's number within its check: the largest group first.</summary>
    public int GroupNumber { get; set; }

    /// <summary>The key its copies share, as each id's row keeps it.</summary>
    public byte[] KeyHash { get; set; } = [];

    public int Copies { get; set; }

    /// <summary>What its first copy holds at each property of the key, property to values (JSON), cut to the column.</summary>
    public string? ValuesJson { get; set; }

    /// <summary><c>pending</c> (its copies not read yet), <c>picked</c>, <c>chosen</c>, <c>review</c> or <c>settled</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Why the group came out as it did, in a line.</summary>
    public string? Reason { get; set; }

    /// <summary>The inventory row of the copy the group keeps; null while none is decided.</summary>
    public long? KeptInventoryRecordId { get; set; }

    /// <summary>The operator who chose the copy kept, when one did.</summary>
    public string? ChosenBy { get; set; }

    public DateTime? ChosenUtc { get; set; }
}

/// <summary>
/// One id of an inventory a duplicate check read: the hash of its key and what it holds there, the version the index held; once
/// its group's copies are read, what storage held of it, how many values it holds under <c>data</c> and how many records name
/// it; and its role in its group (kept, picked to remove, left, or left for review) with why.
/// </summary>
public sealed class DeliveryInventoryDuplicateMember
{
    public short PartitionId { get; set; }

    public long InventoryDuplicateCheckId { get; set; }

    public long InventoryRecordId { get; set; }

    public byte[] KeyHash { get; set; } = [];

    /// <summary>Its group; null while the check reads the keys, before the ids that share none are let go.</summary>
    public int? GroupNumber { get; set; }

    /// <summary>The version the search index held of it when the check read its key.</summary>
    public long? IndexedVersion { get; set; }

    /// <summary>What it held at each property of the key, property to values (JSON), cut to the column.</summary>
    public string? ValuesJson { get; set; }

    /// <summary>What storage answered for it when its group's copies were read: <c>held</c>, <c>missing</c> or <c>unanswered</c>; null before.</summary>
    public string? StorageState { get; set; }

    /// <summary>The version storage held when its group's copies were read.</summary>
    public long? StoredVersion { get; set; }

    /// <summary>How many values it holds under <c>data</c>, each item of a list counted on its own: how complete the copy is.</summary>
    public int? Attributes { get; set; }

    /// <summary>How many records name it, as OSDU's search counts them.</summary>
    public long? MentionedBy { get; set; }

    /// <summary><c>keep</c>, <c>remove</c>, <c>leave</c>, <c>review</c> or <c>gone</c>; null before its group's copies are read.</summary>
    public string? Role { get; set; }

    /// <summary>Why it has its role, in a line.</summary>
    public string? Reason { get; set; }
}
