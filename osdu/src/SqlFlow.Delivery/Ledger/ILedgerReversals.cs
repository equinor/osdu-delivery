using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds reversals: putting records back to the versions OSDU held
/// before a delivery. A caller that uses only this part takes it rather than the whole ledger; <see cref="OsduLedger"/>
/// implements every part.
/// </summary>
public interface ILedgerReversals
{
    /// <summary>
    /// What OSDU held of each record before the write that left the version <paramref name="current"/> names for it, as the
    /// ledger tells it (<see cref="PriorVersion"/>): the attempt that delivered or restored that version, and the one before
    /// it. A record no such attempt names is <see cref="ReversalPriors.Unknown"/>.
    /// </summary>
    Task<IReadOnlyDictionary<DeliveryKey, PriorVersion>> PriorVersionsAsync(Guid flowId, IReadOnlyDictionary<DeliveryKey, long> current, CancellationToken ct = default);

    /// <summary>
    /// Opens the reversal of <paramref name="source"/> in the flow's ledger, or resumes the one it holds
    /// (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission): one reversal per source of
    /// a ledger, which the run <paramref name="runId"/> now works on. A new one fixes the submissions its source covers (a
    /// submission, or the submissions a run coordinated). Throws when the
    /// source is not the ledger's: a submission of another ledger, or a run that neither planned a submission of it nor
    /// delivered a record of it.
    /// </summary>
    Task<ReversalState> OpenReversalAsync(Guid flowId, string flowName, ReversalSource source, string actor, Guid? runId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Lists every record the reversal's source delivered as an item, with what OSDU held of it before, unless the listing
    /// is finished: the source's delivered attempts are read a page at a time in index order and each record is added once,
    /// so a listing stopped half way and started again adds only what is missing. <paramref name="progress"/> hears after
    /// every page. Returns how many it added and how many items the reversal holds.
    /// </summary>
    Task<ReversalCapture> CaptureReversalAsync(long reversalId, Func<ReversalCapture, Task>? progress, CancellationToken ct = default);

    /// <summary>
    /// The next items of a reversal to work on after <paramref name="after"/>, in key order, at most <paramref name="max"/>:
    /// pending, left sending by a run that stopped, failed, or passed over as busy. A run walks them forward once.
    /// </summary>
    Task<IReadOnlyList<ReversalItemState>> ListReversalWorkAsync(long reversalId, DeliveryKey? after, int max, CancellationToken ct = default);

    /// <summary>Marks items as sending before the run writes to OSDU for them, so a run that stops mid-write is known to have.</summary>
    Task MarkReversalSendingAsync(long reversalId, IReadOnlyList<DeliveryKey> keys, Guid? runId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Settles items of a reversal with their records, a slice to a transaction: each record a restore or a removal puts
    /// back becomes reverted or deleted and blocked, while it is still the record the source left; every item gets its
    /// attempt (phase reverse), is named under <paramref name="activityId"/> when it changed its record, and is settled. The
    /// records that link one a removal took out of OSDU as their child are asked to be planned again, as
    /// <see cref="ILedgerRecords.MarkRemovedAsync"/> asks them.
    /// </summary>
    Task<ReversalSettled> SettleReversalAsync(
        long reversalId, IReadOnlyList<ReversalSettlement> settlements, string actor, long? activityId, Guid? runId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Ends the latest run's work on a reversal in <paramref name="status"/>, with the redacted <paramref name="failure"/> of one that failed.</summary>
    Task CloseReversalAsync(long reversalId, string status, string? failure, DateTime nowUtc, CancellationToken ct = default);

    Task<ReversalState?> GetReversalAsync(long reversalId, CancellationToken ct = default);

    /// <summary>The reversal of <paramref name="source"/> in the flow's ledger, or null when none was asked for.</summary>
    Task<ReversalState?> FindReversalAsync(Guid flowId, ReversalSource source, CancellationToken ct = default);

    /// <summary>The ledger's reversals, newest first.</summary>
    Task<IReadOnlyList<ReversalState>> ListReversalsAsync(Guid flowId, int max, CancellationToken ct = default);

    /// <summary>A reversal's records counted by state and outcome, read from its items.</summary>
    Task<ReversalCounts> CountReversalAsync(long reversalId, CancellationToken ct = default);

    /// <summary>
    /// A page of a reversal's items after <paramref name="after"/> in key order, every one or those of one
    /// <paramref name="outcome"/> (<see cref="ReversalItemStates.Pending"/> for those not settled yet).
    /// </summary>
    Task<IReadOnlyList<ReversalItemState>> ListReversalItemsAsync(long reversalId, string? outcome, DeliveryKey? after, int max, CancellationToken ct = default);

    /// <summary>
    /// What reversing <paramref name="source"/> would reach, writing nothing: the submissions it covers, how many records it
    /// delivered, and the first <paramref name="sample"/> of them in key order as the reversal would list them.
    /// </summary>
    Task<ReversalSourceRead> ReadReversalSourceAsync(Guid flowId, string flowName, ReversalSource source, int sample, CancellationToken ct = default);

    /// <summary>
    /// Whether <paramref name="source"/> delivered a record of the flow's ledger, which is what a reversal of it would take:
    /// a delivered attempt under the submission, or under a submission the run planned (a seek of the submission's index), or
    /// one of the run's own, read from at most <see cref="OsduLedger.SourceDeliveredProbe"/> of the run's attempts in this
    /// ledger. Writes nothing; a source the ledger does not know delivered nothing.
    /// </summary>
    Task<bool> SourceDeliveredAsync(Guid flowId, ReversalSource source, CancellationToken ct = default);
}
