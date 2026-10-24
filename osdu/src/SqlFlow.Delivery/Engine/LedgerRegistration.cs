using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Places a delivery flow's ledger in the partition the flow delivers to, before a row of it is written
/// (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and partitions). Every ledger table is keyed by its
/// partition, so the ledger's directory has to know the partition first: a run registers it, and so does whatever
/// writes to a ledger outside a run (an intervention, a scheduled probe). One place works out the partition, so each of
/// them places a ledger the same way.
/// </summary>
public static class LedgerRegistration
{
    /// <summary>
    /// The partition <paramref name="flow"/> delivers to: the one it is bound to, or for a flow whose partition is its
    /// <c>data-partition-id</c> header, what the header resolves to with <paramref name="secrets"/>.
    /// </summary>
    /// <exception cref="SqlFlowException">The flow names no partition and no header, or the header does not resolve to a data-partition-id.</exception>
    public static async Task<string> PartitionAsync(FlowDefinition flow, ISecretResolver secrets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(secrets);
        if (flow.Partition is { } bound)
        {
            return bound;
        }

        var declared = flow.Target.Headers.FirstOrDefault(h => h.Key.Equals(CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new DeliveryException(
                $"Flow '{flow.Label}' names no partition, and its target.headers no '{CacheScope.PartitionHeader}', so the partition its ledger belongs to is unknown.");
        }

        return CacheScope.Normalize(await secrets.ResolveAsync(declared, ct).ConfigureAwait(false), $"{flow.Label}: target.headers");
    }

    /// <summary>
    /// Registers <paramref name="flow"/>'s ledger in the partition it delivers to, resolved with <paramref name="secrets"/>:
    /// created on its first registration, confirmed on every later one, and refused when the ledger belongs to another
    /// partition, which is how a header that drifted is caught before it writes a row.
    /// </summary>
    /// <param name="ledger">The ledger the flow's rows are written to.</param>
    /// <param name="flow">The flow, bound to its partition or naming its partition in its data-partition-id header.</param>
    /// <param name="secrets">What resolves the header: the node's resolver, with the central configuration ahead of it.</param>
    /// <param name="keptWhenUnresolved">
    /// For work that reads and writes only the ledger, or that resolves the header itself when it calls the target: a header
    /// that does not resolve where this runs then takes the partition the directory already holds for the ledger, rather
    /// than stop a release of records a run delivered. A ledger the directory does not hold still needs its header.
    /// </param>
    /// <param name="ct">Stops the registration.</param>
    /// <returns>The ledger's directory entry, naming the partition its rows are kept under.</returns>
    /// <exception cref="SqlFlowException">
    /// The partition is unknown, the ledger belongs to another partition, or the move of a renamed pipeline's ledger into it
    /// cannot be finished now; the message says which.
    /// </exception>
    /// <remarks>
    /// A move of the ledger of the pipeline this one replaced, which the repository sync asked for, is finished before the
    /// entry is returned (<see cref="ILedger.CompleteLedgerMovesAsync"/>), so whatever registered the ledger reads it whole.
    /// </remarks>
    public static async Task<LedgerEntry> RegisterAsync(ILedger ledger, FlowDefinition flow, ISecretResolver secrets, bool keptWhenUnresolved, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(flow);
        string partition;
        try
        {
            partition = await PartitionAsync(flow, secrets, ct).ConfigureAwait(false);
        }
        catch (SqlFlowException) when (keptWhenUnresolved && flow.Partition is null)
        {
            if (await ledger.GetLedgerAsync(flow.Id, ct).ConfigureAwait(false) is { Partition: not null } kept)
            {
                await ledger.CompleteLedgerMovesAsync(flow.Id, ct).ConfigureAwait(false);
                return kept;
            }

            throw;
        }

        var entry = await ledger.RegisterLedgerAsync(Entry(flow, partition), ct).ConfigureAwait(false);
        await ledger.CompleteLedgerMovesAsync(flow.Id, ct).ConfigureAwait(false);
        return entry;
    }

    /// <summary>The directory entry of <paramref name="flow"/>'s ledger in <paramref name="partition"/>.</summary>
    public static LedgerEntry Entry(FlowDefinition flow, string partition)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return new LedgerEntry
        {
            FlowId = flow.Id,
            Partition = partition,
            Kind = LedgerKinds.Delivery,
            FlowName = flow.Name,
            Interface = flow.Interface ?? string.Empty,
            LedgerName = flow.LedgerName,
        };
    }
}
