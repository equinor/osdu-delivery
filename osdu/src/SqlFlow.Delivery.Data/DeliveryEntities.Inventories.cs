namespace SqlFlow.Delivery.Data;

// The inventories of inventory flows (osdu/docs/reference/flow/inventory-builds.md): each inventory with its runs, the
// ids it holds and their versions, the scans that read them, and the selections and removals an operator asks of them.

/// <summary>
/// One inventory of an inventory flow in a partition (osdu/docs/reference/flow/inventory-builds.md, The tables an inventory
/// keeps): what it reads, the owners its last reconcile used and how it knew them, and its last build and reconcile. Its
/// counts by finding are read from its records.
/// </summary>
public sealed class DeliveryInventory
{
    public short PartitionId { get; set; }

    public int InventoryId { get; set; }

    /// <summary>The ledger identity of the inventory flow in the partition.</summary>
    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    /// <summary>The inventory's name in its flow.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The kind it reads, as the flow declares it (wildcards allowed).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The query that narrows a search read; null for every record of the kind.</summary>
    public string? Query { get; set; }

    /// <summary>search or storage.</summary>
    public string ReadMode { get; set; } = string.Empty;

    /// <summary>latest or all.</summary>
    public string Versions { get; set; } = string.Empty;

    /// <summary>The identities the last reconcile took as this estate's, each with how many records it created that a ledger claims (JSON).</summary>
    public string? OwnersJson { get; set; }

    /// <summary>declared (the flow names them), inferred (from the records a ledger claims), or none.</summary>
    public string? OwnersSource { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>The last build that completed, and when.</summary>
    public long? LastBuildRunId { get; set; }

    public DateTime? LastBuiltUtc { get; set; }

    /// <summary>The last reconcile that completed (a build's or one of its own), and when.</summary>
    public long? LastReconcileRunId { get; set; }

    public DateTime? LastReconciledUtc { get; set; }
}

/// <summary>
/// One build or reconcile of an inventory: the platform run and who asked, how it read OSDU, what it found and changed, and its
/// counts by finding as it left them.
/// </summary>
public sealed class DeliveryInventoryRun
{
    public short PartitionId { get; set; }

    public long InventoryRunId { get; set; }

    public int InventoryId { get; set; }

    public Guid? RunId { get; set; }

    /// <summary>build or reconcile.</summary>
    public string Operation { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    /// <summary>running, completed or failed.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    /// <summary>search or storage.</summary>
    public string ReadMode { get; set; } = string.Empty;

    /// <summary>The ids the read listed, repeats left out.</summary>
    public long Listed { get; set; }

    /// <summary>The pages and requests the read took.</summary>
    public int Pages { get; set; }

    public long Requests { get; set; }

    /// <summary>What the merge changed: ids new to the inventory, ids whose version or headers moved, ids no longer served, and ids served again.</summary>
    public long Added { get; set; }

    public long Changed { get; set; }

    public long Gone { get; set; }

    public long Returned { get; set; }

    /// <summary>The ids a ledger expects that the reconcile read from storage to tell missing from merely unlisted.</summary>
    public long MissingChecked { get; set; }

    /// <summary>The counts by finding the reconcile left (JSON).</summary>
    public string? FindingsJson { get; set; }

    /// <summary>The owners the reconcile used, each with its count, and how it knew them (JSON).</summary>
    public string? OwnersJson { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// One id an inventory holds, or a ledger of its partition expects: its kind, version and system properties as OSDU served
/// them, when it was first listed, last changed and found gone, and what the ledgers hold of it (the finding).
/// </summary>
public sealed class DeliveryInventoryRecord
{
    public short PartitionId { get; set; }

    public long InventoryRecordId { get; set; }

    public int InventoryId { get; set; }

    /// <summary>The OSDU id, compared exactly.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>
    /// The record's name as OSDU last served it: the first of the properties a record is named by that it holds (FacilityName,
    /// Name, ProjectName, Code, a file's name). Null when it holds none, or OSDU never served it to the inventory.
    /// </summary>
    public string? Name { get; set; }

    public string? Kind { get; set; }

    public long? Version { get; set; }

    public string? CreateUser { get; set; }

    public DateTime? CreateTime { get; set; }

    public string? ModifyUser { get; set; }

    public DateTime? ModifyTime { get; set; }

    /// <summary>When a build first listed it; null for an id a ledger expects that OSDU never served while the inventory read it.</summary>
    public DateTime? FirstSeenUtc { get; set; }

    /// <summary>When a build last saw its version or system properties change.</summary>
    public DateTime? ChangedUtc { get; set; }

    /// <summary>When a complete build first did not list it; null while OSDU serves it.</summary>
    public DateTime? GoneUtc { get; set; }

    /// <summary>The latest version its versions were read at (<c>versions: all</c>), so a rebuild reads versions only for what moved.</summary>
    public long? VersionsAt { get; set; }

    /// <summary>
    /// When a reconcile last asked storage for it, a ledger expecting it and the read not listing it; null when none has. The
    /// ids a ledger expects are asked for least recently asked first, so builds bounded by <c>maxMissingChecks</c> take turns
    /// through every one of them.
    /// </summary>
    public DateTime? CheckedUtc { get; set; }

    /// <summary>What the ledgers hold of it: tracked, drifted, unconfirmed, stale, superseded, undoing, forgotten, orphan, foreign, missing, unlisted, gone or unreconciled.</summary>
    public string Finding { get; set; } = string.Empty;

    /// <summary>When its finding became what it is.</summary>
    public DateTime FindingUtc { get; set; }

    /// <summary>The ledger and record that claim it, with the record's status and the version it holds, when one does.</summary>
    public Guid? LedgerFlowId { get; set; }

    public Guid? DeliveryKey { get; set; }

    public string? LedgerStatus { get; set; }

    public long? LedgerVersion { get; set; }

    /// <summary>The artifact a delivery recorded for it, with its state, when the ledgers know it as one.</summary>
    public long? ArtifactId { get; set; }

    public string? ArtifactState { get; set; }

    /// <summary>What the finding rests on, in a line.</summary>
    public string? Detail { get; set; }
}

/// <summary>One version storage keeps of a record an inventory holds (<c>versions: all</c>).</summary>
public sealed class DeliveryInventoryVersion
{
    public short PartitionId { get; set; }

    public long InventoryRecordId { get; set; }

    public long Version { get; set; }
}

/// <summary>
/// One removal of an inventory's ids an operator asked for (osdu/docs/reference/flow/inventory-removals.md, What a removal does),
/// or one restore of its gone ids, which undoes a removal (Restoring gone ids): the run it was, who asked, the finding and how
/// much of each record it removes (or that it restores), how many ids the operator was shown, and what it came to. Every id it
/// reached is a <see cref="DeliveryInventoryRemovalItem"/>.
/// </summary>
public sealed class DeliveryInventoryRemoval
{
    public short PartitionId { get; set; }

    public long InventoryRemovalId { get; set; }

    public int InventoryId { get; set; }

    /// <summary>The platform run the removal ran as.</summary>
    public Guid? RunId { get; set; }

    public string Actor { get; set; } = string.Empty;

    /// <summary>The finding whose ids it removes; gone for a restore.</summary>
    public string Finding { get; set; } = string.Empty;

    /// <summary>record (a soft delete), everything (a purge), inventory (a gone id's row), or restore (a restore of gone ids).</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>True when the removal named its ids; false when it took every id of the finding.</summary>
    public bool NamesIds { get; set; }

    /// <summary>How many ids the operator was shown, which the removal held the inventory to before it removed anything.</summary>
    public long Requested { get; set; }

    /// <summary>running, completed or failed.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public long Removed { get; set; }

    /// <summary>The ids a restore brought back in OSDU; zero for a removal.</summary>
    public long Restored { get; set; }

    public long Gone { get; set; }

    public long Skipped { get; set; }

    public long Failed { get; set; }

    /// <summary>Why the removal stopped, redacted; null for one that completed.</summary>
    public string? Error { get; set; }

    /// <summary>The audit trail's activity of the removal.</summary>
    public long? ActivityId { get; set; }

    /// <summary>
    /// The selection the removal took its ids from (<see cref="DeliveryInventorySelection"/>): the ids of the finding that held
    /// what an operator's conditions asked in OSDU. Null for a removal that named its ids or took every id of the finding.
    /// </summary>
    public long? InventorySelectionId { get; set; }

    /// <summary>
    /// The soft-delete removal of the same inventory whose removed ids this restore brought back (a plain number, as the
    /// selection is). Null for a removal, and for a restore of the gone ids picked or of every one.
    /// </summary>
    public long? RestoresRemovalId { get; set; }
}

/// <summary>
/// The ids of an inventory that hold what an operator's conditions ask in OSDU (osdu/docs/reference/flow/inventory-selections.md,
/// Selecting ids by what OSDU holds): the conditions as the operator wrote them and as the search was asked them, how the
/// match read OSDU and what it found, and the build of the inventory it was matched against. Every id it found is a
/// <see cref="DeliveryInventorySelectionItem"/>. A match runs as a run of the inventory flow; a removal may take its ids.
/// </summary>
public sealed class DeliveryInventorySelection
{
    public short PartitionId { get; set; }

    public long InventorySelectionId { get; set; }

    public int InventoryId { get; set; }

    /// <summary>The platform run the match ran as; null until it is queued.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Who asked for the match.</summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>The conditions as the operator wrote them, a search term by its id (JSON), as the page shows them again.</summary>
    public string ConditionsJson { get; set; } = string.Empty;

    /// <summary>
    /// A hash of <see cref="ConditionsJson"/>, by which the same conditions asked again of the same build find the match
    /// already made rather than reading OSDU again.
    /// </summary>
    public string ConditionsKey { get; set; } = string.Empty;

    /// <summary>
    /// The conditions as the search is asked them: an explorer search of the inventory's kind (JSON), each search term carried
    /// through its mapping to the property its route fills.
    /// </summary>
    public string FiltersJson { get; set; } = string.Empty;

    /// <summary>queued, running, completed, failed or expired.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The Lucene query the conditions came to, as the match sent it; null until the match runs.</summary>
    public string? Query { get; set; }

    /// <summary>
    /// How the match read OSDU: <c>conditions</c> (every record the conditions find in the inventory's kind, joined with the
    /// inventory's ids) or <c>ids</c> (the inventory's ids in chunks, each asked with the conditions); null until it decides.
    /// </summary>
    public string? Direction { get; set; }

    /// <summary>What the reader should know of how the conditions were read (JSON list): a source column read through other records, a value that found nothing.</summary>
    public string? NotesJson { get; set; }

    /// <summary>The properties whose values each id's row carries, those the conditions ask (JSON list).</summary>
    public string? ColumnsJson { get; set; }

    /// <summary>The inventory's last build when the match read OSDU; a later build makes the match stale.</summary>
    public long? BuildRunId { get; set; }

    /// <summary>The ids the inventory serves that the match judged.</summary>
    public long Candidates { get; set; }

    /// <summary>How much the match has to read, and has read: the records the conditions find, or the inventory's ids.</summary>
    public long ToCheck { get; set; }

    public long Checked { get; set; }

    /// <summary>The ids of the inventory that hold what the conditions ask.</summary>
    public long Matched { get; set; }

    /// <summary>Records the conditions find in the inventory's kind that the inventory serves no row of: created since its last build, or outside its query.</summary>
    public long Outside { get; set; }

    /// <summary>Ids matched at another version than the inventory listed: changed since its last build, which a removal leaves for the next build.</summary>
    public long Changed { get; set; }

    /// <summary>The search requests the match sent.</summary>
    public long Requests { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    /// <summary>Why the match failed, redacted; null for one that did not.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// The duplicate check whose picks the selection holds (<see cref="DeliveryInventoryDuplicateCheck"/>): the copies of each
    /// group of duplicates it picked to remove, rather than the ids that hold what conditions ask. Null for a selection a match
    /// filled.
    /// </summary>
    public long? DuplicateCheckId { get; set; }

    /// <summary>
    /// The key the duplicate check's groups were formed by when the selection was made of its picks (JSON, as the check keeps it):
    /// the check's key may change since, and the selection still says what chose its ids. Null for a selection a match filled.
    /// </summary>
    public string? DuplicateKeyJson { get; set; }
}

/// <summary>One id of an inventory a match found holding what its conditions ask: the version the index holds and the values it holds there.</summary>
public sealed class DeliveryInventorySelectionItem
{
    public short PartitionId { get; set; }

    public long InventorySelectionId { get; set; }

    public long InventoryRecordId { get; set; }

    /// <summary>The version the search index held of it when the match read it.</summary>
    public long? MatchedVersion { get; set; }

    /// <summary>What it held at each property the conditions ask, property to values (JSON), cut to the column.</summary>
    public string? ValuesJson { get; set; }

    /// <summary>
    /// For an id of a selection a duplicate check made: the inventory row of the copy its group keeps, which a removal checks
    /// storage still holds before it removes this one. Null for an id a match found.
    /// </summary>
    public long? KeptInventoryRecordId { get; set; }
}

/// <summary>What one removal did to one id: removed, already gone, skipped or failed, at the version it found, and why.</summary>
public sealed class DeliveryInventoryRemovalItem
{
    public short PartitionId { get; set; }

    public long InventoryRemovalItemId { get; set; }

    public long InventoryRemovalId { get; set; }

    public long InventoryRecordId { get; set; }

    /// <summary>The OSDU id, compared exactly.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>The version the inventory listed it at.</summary>
    public long? Version { get; set; }

    /// <summary>The finding it had when the removal reached it.</summary>
    public string Finding { get; set; } = string.Empty;

    /// <summary>removed, gone, skipped or failed.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Why, in a line: what was asked of OSDU, what moved, or what OSDU answered (redacted).</summary>
    public string? Reason { get; set; }

    /// <summary>The ledger and record that held it, for a stale or forgotten id.</summary>
    public Guid? LedgerFlowId { get; set; }

    public Guid? DeliveryKey { get; set; }

    public DateTime RecordedUtc { get; set; }
}

/// <summary>One id a build listed, staged until the build's read is whole and it is merged into the inventory.</summary>
public sealed class DeliveryInventoryScan
{
    public short PartitionId { get; set; }

    public long ScanId { get; set; }

    public long InventoryRunId { get; set; }

    public string TargetId { get; set; } = string.Empty;

    /// <summary>The record's name as the read served it; null when it holds none of the properties a record is named by.</summary>
    public string? Name { get; set; }

    public string? Kind { get; set; }

    public long? Version { get; set; }

    public string? CreateUser { get; set; }

    public DateTime? CreateTime { get; set; }

    public string? ModifyUser { get; set; }

    public DateTime? ModifyTime { get; set; }
}
