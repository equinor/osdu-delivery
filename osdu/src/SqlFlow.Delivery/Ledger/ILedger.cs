namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The ledger (osdu/docs/reference/concepts/ledger.md): submissions, records and append-only attempts, with leasing for
/// the worker, plus the activity audit trail and the catalog read-model. Implemented over the <c>osdu</c> schema through
/// EF Core; the interface keeps the engine free of EF.
/// Its callers reach it by the part they use (<see cref="ILedgerRecords"/>, <see cref="ILedgerLeases"/> and the other
/// parts it extends, each in a file with the types it speaks), and by the whole ledger only where they use several.
/// </summary>
public interface ILedger : ILedgerRegistry, ILedgerSubmissions, ILedgerLeases, ILedgerArtifacts, ILedgerRecords,
    ILedgerQueries, ILedgerCacheSets, ILedgerActivities, ILedgerRemovals, ILedgerReversals, ILedgerRestrictions,
    ILedgerDocuments
{
    /// <summary>
    /// The inventories of inventory flows, with their selections, duplicate checks and removals
    /// (osdu/docs/reference/flow/inventory.md): a store beside the ledger (<see cref="LedgerStore"/>), since an inventory
    /// writes no delivered record.
    /// </summary>
    InventoryStore Inventories { get; }

    /// <summary>
    /// The downloads of download flows (osdu/docs/reference/flow/download.md, What a run records): a store beside the ledger
    /// (<see cref="LedgerStore"/>), since a download writes no delivered record.
    /// </summary>
    DownloadStore Downloads { get; }

    /// <summary>
    /// The runs and results of assertion flows (osdu/docs/reference/flow/assertion.md): a store beside the ledger
    /// (<see cref="LedgerStore"/>), since an assertion run writes no delivered record.
    /// </summary>
    AssertionStore Assertions { get; }

    /// <summary>
    /// The dimensions of dimension flows, with their builds, members and views
    /// (osdu/docs/reference/concepts/dimension-tables.md): a store beside the ledger (<see cref="LedgerStore"/>), since a
    /// dimension writes no delivered record.
    /// </summary>
    DimensionStore Dimensions { get; }
}
