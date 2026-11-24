namespace SqlFlow.Delivery.Data;

// Taking records out of OSDU and putting them back (osdu/docs/reference/concepts/removal-and-reversal.md): the
// reversals of a run or a submission and each record they reach, the runs of removal flows and the keys they stage, and
// the keys a source removed.

/// <summary>
/// One reversal: what one run or one submission put into OSDU, put back record by record as OSDU held it before
/// (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission). There is one per source of a
/// ledger; asking again resumes it. Its counts are read from its items, never kept here.
/// </summary>
public sealed class DeliveryReversal
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ReversalId { get; set; }

    public Guid FlowId { get; set; }

    public string FlowName { get; set; } = string.Empty;

    /// <summary>run or submission.</summary>
    public string SourceKind { get; set; } = string.Empty;

    /// <summary>The run or the submission reversed.</summary>
    public Guid SourceId { get; set; }

    /// <summary>The submissions the source covers, as a JSON array of ids, fixed when the reversal was opened.</summary>
    public string SubmissionsJson { get; set; } = "[]";

    /// <summary>capturing, reversing, completed, failed or cancelled.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Who asked for the reversal first.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    public DateTime RequestedUtc { get; set; }

    /// <summary>When every record the source delivered was listed as an item; null while the listing is not finished.</summary>
    public DateTime? CapturedUtc { get; set; }

    /// <summary>When the latest run working on it started.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>When the latest run working on it ended.</summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>The latest run that worked on it.</summary>
    public Guid? LastRunId { get; set; }

    /// <summary>Why the latest run stopped, redacted; null when it did not fail.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// One record a reversal reaches: what its source delivered of it, what OSDU held before, and what the reversal did. Written
/// when the reversal lists its source, and settled in the transaction that changes the record.
/// </summary>
public sealed class DeliveryReversalItem
{
    /// <summary>The partition the row belongs to, as the ledger directory numbers it (<see cref="DeliveryLedgerPartition"/>): the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long ReversalId { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The OSDU id the record claimed when the reversal listed it; null for a record that claimed none.</summary>
    public string? TargetId { get; set; }

    /// <summary>The source's first delivered attempt of the record.</summary>
    public long FirstAttemptId { get; set; }

    /// <summary>The version that attempt wrote.</summary>
    public long? FirstVersion { get; set; }

    /// <summary>The version the source's last delivered attempt of the record left.</summary>
    public long? RunVersion { get; set; }

    /// <summary>What OSDU held before the source: version, none or unknown.</summary>
    public string Prior { get; set; } = string.Empty;

    /// <summary>With <see cref="Prior"/> version: the version OSDU held.</summary>
    public long? PriorVersion { get; set; }

    /// <summary>The attempt that delivered or restored the version OSDU held before, when the ledger still has it: its hashes and origin are what the record gets back.</summary>
    public long? PriorAttemptId { get; set; }

    /// <summary>pending, sending, done, skipped or failed.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>What came of it once settled: restored, removed, already-gone, or why it was passed over or failed.</summary>
    public string? Outcome { get; set; }

    /// <summary>The reason, redacted.</summary>
    public string? Detail { get; set; }

    /// <summary>The version a restore put back.</summary>
    public long? RestoredVersion { get; set; }

    /// <summary>The version OSDU gave the restored record.</summary>
    public long? NewVersion { get; set; }

    /// <summary>The reverse run that settled it, or marked it sending.</summary>
    public Guid? RunId { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>
/// One run of one removal of a removal flow (osdu/docs/reference/flow/removal.md, A run): the removal flow and removal, the
/// delivery flow's ledger it compared and wrote to, the platform run it ran as and who asked, what it read, what it found the
/// source removed, and what taking the removed keys out came to. A run opens it <c>running</c>, adds to its tallies as it goes,
/// and closes it <c>completed</c>, <c>guarded</c> (its guard held what it found for an operator's approval) or <c>failed</c>.
/// </summary>
public sealed class DeliveryRemovalRun
{
    /// <summary>The partition of the delivery flow's ledger the run wrote to, as the ledger directory numbers it: the first column of the key.</summary>
    public short PartitionId { get; set; }

    public long RemovalRunId { get; set; }

    /// <summary>The removal flow, by name.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The removal of the flow, by name.</summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>The ledger identity of the delivery flow (or interface) whose records the removal compared and took out.</summary>
    public Guid LedgerFlowId { get; set; }

    /// <summary>The delivery flow as pages name it: its name, and its interface after a slash for a source with interfaces.</summary>
    public string DeliveryFlow { get; set; } = string.Empty;

    /// <summary>The platform run the removal ran as.</summary>
    public Guid? RunId { get; set; }

    /// <summary>remove or plan.</summary>
    public string Operation { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    /// <summary>running, completed, guarded or failed.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    /// <summary>When the read of the source's current keys began: the moment the keys it did not find are removed as of. Null for a removal that reads only removed keys.</summary>
    public DateTime? SourceReadUtc { get; set; }

    /// <summary>Rows its key sources returned.</summary>
    public long RowsRead { get; set; }

    /// <summary>Distinct keys the source's current key set held.</summary>
    public long SourceKeys { get; set; }

    /// <summary>Distinct keys the source of removed keys listed.</summary>
    public long ListedKeys { get; set; }

    /// <summary>Distinct keys the delivery flow's record table held in the removal's scope.</summary>
    public long IngestionKeys { get; set; }

    /// <summary>Distinct keys compared: the ledger's records in the scope and the record table's rows, which the guard's share is of.</summary>
    public long Compared { get; set; }

    /// <summary>Keys the run found the source removed that the ledger did not hold as removed yet: the rows it added to the ledger's removed keys.</summary>
    public long Detected { get; set; }

    /// <summary>Of them, the keys the guard held for an operator's approval.</summary>
    public long AwaitingApproval { get; set; }

    /// <summary>Keys waiting for approval an operator's approval the run carried out made pending.</summary>
    public long Approved { get; set; }

    public long Removed { get; set; }

    public long AlreadyGone { get; set; }

    /// <summary>Records the ledger held that OSDU did not: settled in the ledger with nothing asked of OSDU.</summary>
    public long Settled { get; set; }

    /// <summary>Keys only the record table held: kept out, so no run delivers them.</summary>
    public long KeptOut { get; set; }

    /// <summary>Records left in OSDU because other records still name them.</summary>
    public long Referenced { get; set; }

    /// <summary>Records left in OSDU because another ledger of the partition holds the same id live.</summary>
    public long Shared { get; set; }

    /// <summary>Records a worker or another removal held, taken by the next run.</summary>
    public long Busy { get; set; }

    public long Failed { get; set; }

    /// <summary>Keys whose row arrived after the moment of their removal: the key came back, and nothing was taken out.</summary>
    public long CameBack { get; set; }

    /// <summary>Removed keys whose key the source holds again, or whose record was delivered again since: no longer kept out.</summary>
    public long Lifted { get; set; }

    /// <summary>Rows of the delivery flow's record table the run marked deleted, as its deletions say.</summary>
    public long Marked { get; set; }

    /// <summary>Rows of the record table whose deleted mark the run took off, as back in the source.</summary>
    public long Unmarked { get; set; }

    /// <summary>The guard's share, 0 to 100, the run was held to.</summary>
    public int GuardPercent { get; set; }

    /// <summary>The guard's count the run was held to; null for none.</summary>
    public long? GuardRecords { get; set; }

    /// <summary>Who approved the keys waiting for approval this run made pending, for a run that carried out an approval.</summary>
    public string? ApprovedBy { get; set; }

    /// <summary>The activity on the delivery flow's ledger the run's removals are recorded under.</summary>
    public long? ActivityId { get; set; }

    /// <summary>Why the run stopped, or what its guard held, redacted; null for one that completed.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// One key a running removal staged (osdu/docs/reference/flow/removal.md, A run): read from one of its sides (the source's
/// current keys, its removed keys, or the delivery flow's record table) and derived to the delivery key the ledger keys the
/// record by. The rows of a run are deleted when it closes; a run its process stopped leaves them for the next run of the
/// same removal to delete.
/// </summary>
public sealed class DeliveryRemovalKey
{
    public short PartitionId { get; set; }

    public long RemovalRunId { get; set; }

    /// <summary>1, the source's current keys; 2, its removed keys; 3, the delivery flow's record table.</summary>
    public byte Side { get; set; }

    public Guid DeliveryKey { get; set; }

    /// <summary>The key tuple, a JSON array of strings in the record key's order.</summary>
    public string? SourceKeyJson { get; set; }

    /// <summary>For the record table's side: when its row last changed (<c>UpdatedDate_DW</c>).</summary>
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>For the removed side: when the source removed the key.</summary>
    public DateTime? RemovedUtc { get; set; }

    /// <summary>The file the key was read from, for a reader source.</summary>
    public string? FileName { get; set; }

    /// <summary>The key's row in that file, or in its table.</summary>
    public long? RowNumber { get; set; }
}

/// <summary>
/// A key a source removed (osdu/docs/reference/concepts/removal-and-reversal.md, Records the source no longer holds): the
/// ledger's own record of a deletion, under the delivery flow's ledger identity, written by a removal flow. A removal run that
/// finds a key the source no longer holds adds its row once (pending, or waiting for approval when the run's guard held what it
/// found); taking it out of OSDU works through the pending rows alone and marks each removed, so a key taken out is never asked
/// of OSDU again. A row of the key not newer than <see cref="RemovedUtc"/> is not delivered while the row is pending or removed;
/// the key's return, a newer row delivered, or an operator's lift takes the row away and the record is delivered again.
/// </summary>
public sealed class DeliveryRemovedKey
{
    public short PartitionId { get; set; }

    /// <summary>The ledger identity of the delivery flow (or interface) the key's records belong to.</summary>
    public Guid FlowId { get; set; }

    public Guid DeliveryKey { get; set; }

    public string? SourceKeyJson { get; set; }

    /// <summary>absent (the source's current keys no longer held it) or listed (the source listed it as removed).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>The moment the removal stands for: a row of the key not newer than it is not delivered.</summary>
    public DateTime RemovedUtc { get; set; }

    /// <summary>The file of the row that listed the key as removed.</summary>
    public string? FileName { get; set; }

    public long? RowNumber { get; set; }

    /// <summary>awaiting-approval (the guard held it), pending (to be taken out) or removed (taken out, settled or kept out).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>What the last try came to: removed, already-gone, settled, kept-out, referenced, shared, busy or failed; null before the first.</summary>
    public string? Outcome { get; set; }

    /// <summary>Why it came to that outcome: the records still naming it, what OSDU answered, redacted.</summary>
    public string? Detail { get; set; }

    /// <summary>How many times a removal run tried to take it out.</summary>
    public int Tries { get; set; }

    /// <summary>The removal flow that found it, by name.</summary>
    public string RemovalFlow { get; set; } = string.Empty;

    /// <summary>The removal of that flow that found it.</summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>The removal run that found it.</summary>
    public long DetectedRunId { get; set; }

    /// <summary>The removal run that took it out (or settled or kept it out); null while it is not removed.</summary>
    public long? DoneRunId { get; set; }

    /// <summary>The platform run of the removal run that last acted on it.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Who approved it, for a key the guard held.</summary>
    public string? ApprovedBy { get; set; }

    public DateTime? ApprovedUtc { get; set; }

    /// <summary>When the removal run found it.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When it was taken out; null while it is not removed.</summary>
    public DateTime? DoneUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
