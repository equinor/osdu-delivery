using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds records removed from OSDU: removal runs, removed keys, and
/// the ledger purges that follow. A caller that uses only this part takes it rather than the whole ledger;
/// <see cref="OsduLedger"/> implements every part.
/// </summary>
public interface ILedgerRemovals
{
    /// <summary>
    /// Answers the requests of records deleted from the ledger that a run settled (<paramref name="answered"/>, each as the
    /// listing gave it): their rows planned as new records, or gone from the ingestion table. Only a request still standing
    /// as it was read is answered, so one a later deletion of the same record wrote stays. Returns how many it answered.
    /// </summary>
    Task<int> AnswerPurgedRequestsAsync(Guid flowId, IReadOnlyList<PlanRequestedRecord> answered, CancellationToken ct = default);

    /// <summary>
    /// Settles in the ledger records a removal flow found the source no longer holds, which OSDU does not hold either (never
    /// delivered, or taken out already): what is queued for them is dropped and they are marked removed, nothing asked of
    /// OSDU, each with an attempt (outcome skipped, phase <see cref="AttemptPhases.SourceRemoved"/>) saying why, named under the
    /// removal's activity. A record already marked removed keeps its state and gains the attempt. One the ledger last knew in
    /// OSDU was taken out by something else, so the records that link it as their child are asked to be planned again, as
    /// <see cref="ILedgerRecords.MarkRemovedAsync"/> asks them. Records a lease holds other than <paramref name="holder"/>'s are passed over
    /// and returned.
    /// </summary>
    Task<IReadOnlyList<DeliveryKey>> SettleRemovedBySourceAsync(
        Guid flowId, IReadOnlyDictionary<DeliveryKey, SourceRemovalNote> keys, string worker, string? holder, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Deletes records from the ledger that were removed from OSDU
    /// (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting removed records from the ledger), a slice to a
    /// transaction. Only a record the ledger marks deleted goes, none a lease holds, and none with an artifact an undo
    /// has still to settle (intent, pending, due or failed), whose undo reaches it through its record; any other is
    /// left as it is. Each record deleted keeps one line (<see cref="PurgedRecordState"/>: what it was, its OSDU id and
    /// last version, who deleted it and when) and is named under <paramref name="activityId"/>; its attempts, its
    /// search entries and its row are deleted. The activities that name it stay, as the audit trail does. The line asks for
    /// the record's row to be read again, by the key tuple it keeps, so the flow's next run delivers it as a record the
    /// ledger never held (<see cref="ILedgerRecords.ListPlanRequestedAsync"/>); a record a reversal keeps out (blocked) and one with no key
    /// tuple ask for nothing, and an earlier line of the same record gives its request up. Nothing else of the ledger moves:
    /// its watermarks stay. Returns the records it deleted.
    /// </summary>
    Task<IReadOnlyList<DeliveryKey>> PurgeRecordsAsync(
        Guid flowId, IReadOnlyList<DeliveryKey> keys, string actor, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Deletes the whole ledger of <paramref name="flowId"/> (osdu/docs/reference/concepts/removal-and-reversal.md,
    /// Deleting the ledger), which the run deleting it calls once it has removed from OSDU every record OSDU held:
    /// every record, whatever its state, a slice to a transaction,
    /// each kept as one line named under <paramref name="activityId"/> with its attempts, search entries and row deleted as
    /// <see cref="PurgeRecordsAsync"/> deletes one; then, in one transaction, what the ledger keeps of its runs (submissions,
    /// work batches, leases and their events, watermarks, reversals), so its next run reads every row and delivers each as a
    /// new record. No line asks for its row to be read again, and the requests earlier deletions left are answered, since
    /// that run reads every row anyway. Leases that ran out are settled first. The activities, their links and the lines of
    /// the deleted records stay, and so does the ledger's entry in the directory.
    /// </summary>
    /// <exception cref="DeliveryException">A worker still holds a lease on the ledger's records: the records no lease held
    /// are deleted, and the rest of the ledger is left as it was.</exception>
    Task<LedgerDeletion> DeleteLedgerAsync(Guid flowId, string actor, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>What the ledger keeps of <paramref name="key"/> after the record was deleted from it, the latest time it was; null when it never was.</summary>
    Task<PurgedRecordState?> FindPurgedAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default);

    /// <summary>
    /// What every delivery ledger of <paramref name="partition"/> changed in OSDU after <paramref name="sinceUtc"/>
    /// (<see cref="RecentOsduChange"/>), ordered by ledger name; a ledger that changed nothing is left out, and so is every
    /// ledger of a partition the directory does not hold. Each figure is one index seek per ledger of the partition.
    /// </summary>
    Task<IReadOnlyList<RecentOsduChange>> RecentOsduChangesAsync(string partition, DateTime sinceUtc, CancellationToken ct = default);

    // Removal flows (osdu/docs/reference/flow/removal.md): the runs a removal flow keeps under the ledgers of the delivery flows it
    // names, the keys it stages, and the ledger's deletions: every key a source removed, with how far taking it out has come. The
    // ledger flow is always the delivery flow's (or interface's): a removal flow keeps no ledger of its own.
    /// <summary>
    /// Opens a removal run under the delivery ledger <paramref name="ledgerFlowId"/>: it closes the runs of the same removal its
    /// process left open (failed, saying so) and deletes the keys they staged, and returns the new run's number.
    /// </summary>
    Task<long> StartRemovalRunAsync(Guid ledgerFlowId, RemovalRunStart start, CancellationToken ct = default);

    /// <summary>Records when the read of the source's current keys began: the moment the keys it does not hold are removed as of.</summary>
    Task MarkRemovalSourceReadAsync(Guid ledgerFlowId, long removalRunId, DateTime sourceReadUtc, CancellationToken ct = default);

    /// <summary>
    /// Converts a batch of raw key values where the ledger is kept, as SQL Server converts text: each column's expression over
    /// its raw value, then <c>TRY_CONVERT</c> to the record table's type, and the removal moment to a UTC instant. Returns one
    /// converted row per raw row, in order; a value that does not convert is null, and the caller decides what that means.
    /// </summary>
    Task<IReadOnlyList<ConvertedRemovalKey>> ConvertRemovalKeysAsync(RemovalKeyConversion conversion, IReadOnlyList<RawRemovalKey> rows, CancellationToken ct = default);

    /// <summary>
    /// Stages derived keys of one side of a running removal (<see cref="SourceRemovals.SourceSide"/>,
    /// <see cref="SourceRemovals.RemovedSide"/> or <see cref="SourceRemovals.IngestionSide"/>). A key staged on the side already is
    /// kept once: for the removed side with the later removal moment. Returns how many keys the batch added to the side.
    /// </summary>
    Task<long> StageRemovalKeysAsync(Guid ledgerFlowId, long removalRunId, byte side, IReadOnlyList<StagedRemovalKey> keys, CancellationToken ct = default);

    /// <summary>
    /// Compares the staged keys with the ledger's records of the flow in the comparison's scope and with the ledger's deletions:
    /// a key the source removed that the ledger holds no deletion of yet becomes one (pending, or waiting for approval when there
    /// are more than the guard allows), a deletion whose key the source holds again or whose record was delivered again since is
    /// lifted, and a deletion found again keeps the later moment. A plan writes nothing and counts what a run would find.
    /// </summary>
    Task<RemovalDetection> DetectRemovalsAsync(Guid ledgerFlowId, long removalRunId, RemovalComparison comparison, CancellationToken ct = default);

    /// <summary>
    /// After a run's comparison, the record table's rows whose deleted mark disagrees with the ledger's deletions, <paramref name="take"/>
    /// at a time in delivery key order after <paramref name="after"/> (osdu/docs/reference/flow/removal-marks.md): rows to mark,
    /// whose key holds a pending or removed deletion; and rows to take the mark off, whose key holds no deletion and that are
    /// back, loaded again after the mark or held by the source's current keys read after it (<paramref name="sourceReadUtc"/>,
    /// null when the removal reads none).
    /// </summary>
    Task<IReadOnlyList<RemovalMark>> RemovalMarksAsync(
        Guid ledgerFlowId, long removalRunId, DateTime? sourceReadUtc, DeliveryKey? after, int take, CancellationToken ct = default);

    /// <summary>
    /// Makes pending, as an operator approved, the deletions of the ledger <paramref name="removalFlow"/>'s removal
    /// <paramref name="entry"/> found that wait for approval, when there are exactly <paramref name="expected"/> of them, as the
    /// operator was shown; each keeps who approved it and when. Returns how many it made pending.
    /// </summary>
    /// <exception cref="DeliveryException">The ledger holds another number of them than the operator was shown; nothing was approved.</exception>
    Task<long> ApproveRemovalsAsync(Guid ledgerFlowId, string removalFlow, string entry, long expected, string approvedBy, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>How many of the ledger's deletions <paramref name="removalFlow"/>'s removal <paramref name="entry"/> found wait for approval.</summary>
    Task<long> CountAwaitingApprovalAsync(Guid ledgerFlowId, string removalFlow, string entry, CancellationToken ct = default);

    /// <summary>The ledger's pending deletions, in key order after <paramref name="after"/>, at most <paramref name="max"/>: what taking them out works through.</summary>
    Task<IReadOnlyList<RemovedKeyState>> PendingRemovalsAsync(Guid ledgerFlowId, DeliveryKey? after, int max, CancellationToken ct = default);

    /// <summary>
    /// Records what tries to take out deletions came to: a finishing outcome (<see cref="SourceRemovals.Finishing"/>) makes the
    /// deletion removed, naming the run, and it is never asked of OSDU again; any other leaves it pending for the next run, with
    /// the outcome and why. Either way the try is counted.
    /// </summary>
    Task SettleRemovalsAsync(Guid ledgerFlowId, long removalRunId, Guid? runId, IReadOnlyList<RemovalKeyOutcome> outcomes, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Adds to a run's tallies.</summary>
    Task AddRemovalTallyAsync(Guid ledgerFlowId, long removalRunId, RemovalTally tally, CancellationToken ct = default);

    /// <summary>
    /// Closes a run <paramref name="status"/> (<see cref="SourceRemovals.Completed"/>, <see cref="SourceRemovals.Guarded"/> or
    /// <see cref="SourceRemovals.Failure"/>), saying why for a run that did not complete, and deletes the keys it staged.
    /// </summary>
    Task CompleteRemovalRunAsync(Guid ledgerFlowId, long removalRunId, string status, string? stoppedBy, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>A removal run, by its number in the partition; null when the partition holds none of that number.</summary>
    Task<RemovalRunState?> GetRemovalRunAsync(string partition, long removalRunId, CancellationToken ct = default);

    /// <summary>The runs of a removal flow in a partition, the newest first, of one removal when <paramref name="entry"/> names it.</summary>
    Task<IReadOnlyList<RemovalRunState>> ListRemovalRunsAsync(string partition, string flowName, string? entry, int max, CancellationToken ct = default);

    /// <summary>The removal runs that wrote to the ledger of <paramref name="ledgerFlowId"/>, the newest first.</summary>
    Task<IReadOnlyList<RemovalRunState>> ListRemovalRunsOfLedgerAsync(Guid ledgerFlowId, int max, CancellationToken ct = default);

    /// <summary>
    /// The ledger's deletions, in key order after <paramref name="after"/>, at most <paramref name="max"/>, of one state when
    /// <paramref name="state"/> names it, of those one removal run found when <paramref name="detectedRunId"/> names it.
    /// </summary>
    Task<IReadOnlyList<RemovedKeyState>> ListRemovedKeysAsync(
        Guid ledgerFlowId, string? state, long? detectedRunId, DeliveryKey? after, int max, CancellationToken ct = default);

    /// <summary>How many of the ledger's deletions are in each state.</summary>
    Task<IReadOnlyDictionary<string, long>> RemovedKeyCountsAsync(Guid ledgerFlowId, CancellationToken ct = default);

    /// <summary>
    /// The keys among <paramref name="keys"/> the source removed that the ledger keeps out (pending or removed), with the moment
    /// each removal stands for: what the plan skips a row of, unless the row is newer. One read for a page of keys.
    /// </summary>
    Task<IReadOnlyDictionary<DeliveryKey, DateTime>> RemovedKeysAsync(Guid flowId, IReadOnlyCollection<DeliveryKey> keys, CancellationToken ct = default);

    /// <summary>The ledger's deletion of a key; null when it holds none.</summary>
    Task<RemovedKeyState?> GetRemovedKeyAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default);

    /// <summary>
    /// Lifts the deletions of <paramref name="keys"/>, as an operator asked: each key is no longer kept out, and a record the
    /// removal took out is asked to be planned again, so the flow's next run reads its row and delivers it under its id; a record
    /// still live is left as it is, its next changed row planned as any other. Every record lifted is named under
    /// <paramref name="activityId"/>. Returns the keys whose deletion the ledger held, each with its key tuple (the record table's
    /// row to take a deleted mark off); a key it held none of is left out.
    /// </summary>
    Task<IReadOnlyDictionary<DeliveryKey, string?>> LiftRemovedKeysAsync(
        Guid flowId, IReadOnlyCollection<DeliveryKey> keys, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// The ids among <paramref name="ids"/> a record of another ledger of the flow's partition holds live (claimed or not, and not
    /// removed): a removal leaves such an id in OSDU rather than take it from under that ledger.
    /// </summary>
    Task<IReadOnlySet<string>> SharedIdsAsync(Guid flowId, IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>
    /// The paths, relative to their repository's root, of the files the catalog's read model says declare delivery flow
    /// <paramref name="flowName"/> (active interfaces only): where a removal flow finds the delivery flow it names.
    /// </summary>
    Task<IReadOnlyList<string>> DeliveryFlowPathsAsync(string flowName, CancellationToken ct = default);
}
