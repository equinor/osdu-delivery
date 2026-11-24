using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds the artifacts a delivery mints beside its records: opened,
/// recorded, settled and swept. A caller that uses only this part takes it rather than the whole ledger;
/// <see cref="OsduLedger"/> implements every part.
/// </summary>
public interface ILedgerArtifacts
{
    /// <summary>
    /// The artifacts of <paramref name="keys"/> that may still be undone
    /// (osdu/docs/reference/concepts/artifacts-and-undo.md, States): intents, pending ones of an open or abandoned unit,
    /// and those due or failed to undo, in the order they were created.
    /// </summary>
    Task<IReadOnlyList<LedgerArtifact>> OpenArtifactsAsync(Guid flowId, IReadOnlyCollection<DeliveryKey> keys, CancellationToken ct = default);

    /// <summary>Every artifact of one record, the newest first, at most <paramref name="max"/>: what its page shows.</summary>
    Task<IReadOnlyList<LedgerArtifact>> RecordArtifactsAsync(Guid flowId, DeliveryKey key, int max, CancellationToken ct = default);

    /// <summary>
    /// The next page of artifacts the sweep undoes (osdu/docs/reference/concepts/artifacts-and-undo.md, Retries, bounds and
    /// force): the next <paramref name="maxRecords"/> records after <paramref name="after"/>, whole, with their artifacts
    /// due; failed ones past their backoff and, unless <paramref name="exhausted"/>, short of
    /// <see cref="ArtifactLimits.MaxUndoAttempts"/> (with it, every failed one); and the intents and pending ones of a unit
    /// their record no longer carries. Only artifacts of records no lease holds are listed, so nothing a worker is
    /// delivering, or has not finished applying, is undone under it.
    /// </summary>
    Task<ArtifactSweepPage> SweepArtifactsAsync(Guid flowId, DateTime nowUtc, DeliveryKey? after, int maxRecords, bool exhausted, CancellationToken ct = default);

    /// <summary>
    /// Writes undos run outside a lease (the sweep, a removal), each with its attempt and what it settled each artifact as, in
    /// one transaction. Only open artifacts are settled, unless <paramref name="minted"/>: deleting the ledger removes the ids
    /// committed deliveries minted, which are live or superseded, too.
    /// </summary>
    Task SettleArtifactsAsync(Guid flowId, IReadOnlyList<RecordUndo> undos, bool minted = false, CancellationToken ct = default);

    /// <summary>
    /// The ids the committed deliveries of <paramref name="keys"/> minted, live or superseded, that nothing names once their
    /// ledger is deleted: datasets, content and outputs (<see cref="ArtifactRoles.KeptAfterCommit"/>), never a dataspace, whose
    /// removal would purge an OSDU record other records share.
    /// </summary>
    Task<IReadOnlyList<LedgerArtifact>> MintedArtifactsAsync(Guid flowId, IReadOnlyCollection<DeliveryKey> keys, CancellationToken ct = default);

    /// <summary>How many artifacts of the flow are still open, by state, read through the open artifacts' index.</summary>
    Task<ArtifactCounts> ArtifactCountsAsync(Guid flowId, CancellationToken ct = default);

    /// <summary>
    /// A page of the flow's records whose artifacts an undo may still take, read through the open artifacts' index: first the
    /// records with an undo the sweep has stopped trying, then those with failed undos, then those with artifacts due, then the
    /// oldest; each with its open artifacts counted by state. <see cref="OpenArtifactRecordPage.Total"/> counts every such record.
    /// </summary>
    Task<OpenArtifactRecordPage> OpenArtifactRecordsAsync(Guid flowId, int offset, int max, CancellationToken ct = default);
}
