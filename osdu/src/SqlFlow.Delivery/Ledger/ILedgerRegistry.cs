using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// One ledger as the ledger's directory holds it (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and
/// partitions): the rows of one flow (or interface of a source) in one partition, under one ledger identity. The
/// partition is part of the key of every row the ledger keeps.
/// </summary>
public sealed record LedgerEntry
{
    /// <summary>The ledger identity every row of the ledger carries.</summary>
    public required Guid FlowId { get; init; }

    /// <summary>
    /// The data-partition-id the ledger belongs to. Null, when read, for a ledger the upgrade to partition keys could not
    /// place, until its next run adopts it; a registration always names one.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>
    /// <see cref="LedgerKinds.Delivery"/>, <see cref="LedgerKinds.Retrieval"/>, <see cref="LedgerKinds.Assertion"/>,
    /// <see cref="LedgerKinds.Dimension"/>, <see cref="LedgerKinds.Inventory"/> or <see cref="LedgerKinds.Download"/>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>The flow the ledger belongs to: the source, for an interface of a source.</summary>
    public required string FlowName { get; init; }

    /// <summary>The interface of a source, or empty for a flow in the single form.</summary>
    public string Interface { get; init; } = string.Empty;

    /// <summary>The ledger's name as pages show it.</summary>
    public required string LedgerName { get; init; }

    public DateTime RegisteredUtc { get; init; }

    /// <summary>
    /// The name a flow writes as <c>ledger:</c> to keep the ledger named <paramref name="ledgerName"/> in
    /// <paramref name="partition"/>: the name without the <c>@partition</c> a partition keeping a ledger of its own adds
    /// (osdu/docs/reference/flow/interfaces.md, Ledger identity), which a flow bound to that partition adds again.
    /// </summary>
    public static string OwnName(string ledgerName, string? partition)
    {
        ArgumentNullException.ThrowIfNull(ledgerName);
        if (string.IsNullOrEmpty(partition))
        {
            return ledgerName;
        }

        var suffix = "@" + partition;
        return ledgerName.Length > suffix.Length && ledgerName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? ledgerName[..^suffix.Length]
            : ledgerName;
    }
}

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds the ledgers themselves: registered in a partition, moved,
/// taken over and deleted. A caller that uses only this part takes it rather than the whole ledger;
/// <see cref="OsduLedger"/> implements every part.
/// </summary>
public interface ILedgerRegistry
{
    /// <summary>
    /// Registers the ledger a run is about to write, in the partition the run delivers to
    /// (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and partitions), and returns it as the directory now
    /// holds it. Every write of a ledger's rows needs its registration; every row carries the partition in its key. A
    /// ledger belongs to one partition: registering it in another is refused, naming both. A ledger the upgrade could
    /// not place is adopted into the partition registered, unless its records were delivered to another partition.
    /// </summary>
    /// <exception cref="DeliveryException">The ledger belongs to another partition, or holds records delivered to one.</exception>
    Task<LedgerEntry> RegisterLedgerAsync(LedgerEntry ledger, CancellationToken ct = default);

    /// <summary>The ledger <paramref name="flowId"/> names, as the directory holds it, or null when no run has registered it.</summary>
    Task<LedgerEntry?> GetLedgerAsync(Guid flowId, CancellationToken ct = default);

    /// <summary>The ledgers of <paramref name="partition"/> (by its data-partition-id), or every ledger when it is null.</summary>
    Task<IReadOnlyList<LedgerEntry>> ListLedgersAsync(string? partition, CancellationToken ct = default);

    /// <summary>
    /// Carries out the moves of ledgers asked and not done (<see cref="LedgerMoves"/>,
    /// osdu/docs/reference/concepts/renaming-a-pipeline.md): every one, for the control plane's pass, which leaves a move
    /// another process is making, or one waiting for work in flight, to a later pass; or only those into
    /// <paramref name="into"/>, for a run of the flow about to read its ledger, which waits for a move another process is
    /// making and is refused while one cannot be made yet.
    /// </summary>
    /// <exception cref="DeliveryException">
    /// <paramref name="into"/> was named and a move into it could not be finished now: work is in flight on either ledger,
    /// or another process kept moving it past the wait. The message says which; nothing of the run should go on.
    /// </exception>
    Task<IReadOnlyList<LedgerMoveResult>> CompleteLedgerMovesAsync(Guid? into, CancellationToken ct = default);

    /// <summary>
    /// Carries out the moves of renamed flows' ledgers the sync of repository <paramref name="repoId"/> asked for, once it
    /// committed, as the control plane's pass does: a move another process is making, or one waiting for work in flight, is
    /// left to a later pass.
    /// </summary>
    Task<IReadOnlyList<LedgerMoveResult>> CompleteRepositoryMovesAsync(Guid repoId, CancellationToken ct = default);

    /// <summary>
    /// The missing documents: the flows no repository declares any more whose ledgers hold anything, each with its orphaned
    /// ledgers and the declared flows that can take them over (<see cref="LedgerBindings"/>,
    /// osdu/docs/reference/concepts/renaming-a-pipeline.md): those with a ledger in partition <paramref name="partition"/>,
    /// or in any partition when it is null.
    /// </summary>
    Task<IReadOnlyList<OrphanedDocument>> ListOrphanedDocumentsAsync(string? partition, CancellationToken ct = default);

    /// <summary>
    /// Hands the orphaned ledgers of the missing document <paramref name="request"/> names to the declared flow it names, as
    /// <paramref name="actor"/> (<see cref="LedgerBindings"/>): each ledger to the one the flow keeps in its partition, for
    /// its interface, as a move of its own, asked and carried out at once. Answers what came of each: <c>completed</c>;
    /// <c>running</c> for a move that waits for work in flight on either ledger, which the control plane's pass finishes;
    /// <c>failed</c> for one refused; <c>skipped</c> for a ledger the flow keeps no counterpart of, which stays orphaned.
    /// </summary>
    /// <exception cref="DeliveryException">The document is not missing, or no repository (or more than one) declares the flow; nothing was asked.</exception>
    Task<IReadOnlyList<LedgerMoveResult>> TakeOverLedgersAsync(LedgerTakeoverRequest request, string actor, CancellationToken ct = default);

    /// <summary>
    /// What deleting the missing document <paramref name="request"/> names would do in its partition, writing nothing
    /// (<see cref="LedgerDeletes"/>): each of its orphaned ledgers there with what it holds, and why it cannot be deleted
    /// now when it cannot (<see cref="LedgerDeleteStep.Refusal"/>).
    /// </summary>
    /// <exception cref="DeliveryException">The document left no orphaned ledger in the partition.</exception>
    Task<IReadOnlyList<LedgerDeleteStep>> PreviewOrphanedLedgerDeleteAsync(LedgerDeleteRequest request, CancellationToken ct = default);

    /// <summary>
    /// Deletes what the missing document <paramref name="request"/> names left behind in its partition, as
    /// <paramref name="actor"/> (<see cref="LedgerDeletes"/>, osdu/docs/reference/concepts/renaming-a-pipeline.md): each
    /// orphaned ledger as a deletion of its own, asked and carried out at once. A deletion removes history and never what
    /// only the ledger knows of OSDU: a delivery ledger with a record OSDU may still hold, one whose access is restricted or
    /// an item still to undo, an inventory ledger holding a removal, and a dimension a cache flow captures or another
    /// flow's view reads are refused, and nothing of that ledger is touched. Nothing is asked of OSDU. Answers what came of
    /// each: <c>completed</c>; <c>running</c> for one that waits for work in flight or that another process is deleting,
    /// which the control plane's pass finishes; <c>failed</c> for one refused, with why. The ledger's audit trail, the
    /// deletion in it, its entry in the directory and one line of each record of a delivery ledger stay.
    /// </summary>
    /// <exception cref="DeliveryException">The document left no orphaned ledger in the partition; nothing was asked.</exception>
    Task<IReadOnlyList<LedgerDeleteResult>> DeleteOrphanedLedgersAsync(LedgerDeleteRequest request, string actor, CancellationToken ct = default);

    /// <summary>
    /// Carries out the deletions of orphaned ledgers asked and not done (<see cref="LedgerDeletes"/>), oldest first, for the
    /// control plane's pass: one that stopped part way is taken up where it stopped, and one another process is deleting, or
    /// one waiting for work in flight, is left to a later pass.
    /// </summary>
    Task<IReadOnlyList<LedgerDeleteResult>> CompleteLedgerDeletesAsync(CancellationToken ct = default);
}
