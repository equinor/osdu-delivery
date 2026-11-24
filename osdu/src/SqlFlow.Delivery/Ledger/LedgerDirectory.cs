using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The ledger's directory as a process knows it (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and partitions): the
/// number each partition is keyed by, and the partition each ledger belongs to. Every ledger table starts its key with the
/// partition's number, so every read and write of a ledger names it: the directory resolves it once per ledger, and keeps
/// it, since a ledger's partition never changes once it has one. A move takes a ledger's identity out of the directory, and
/// the directory forgets it (<see cref="Forget"/>).
/// </summary>
/// <remarks>
/// <see cref="OsduLedger"/> makes one and hands it to each store it holds, so a ledger the ledger registered or moved is known,
/// or forgotten, by every store at once: a store with a directory of its own would go on writing under an identity a move
/// took away.
/// </remarks>
internal sealed class LedgerDirectory
{
    private const short Unassigned = DeliveryModel.UnassignedPartition;

    private readonly LedgerDatabase _database;

    /// <summary>
    /// Ledger identities this process has resolved to their partition's number. An unassigned ledger is not kept: its next
    /// run adopts it, perhaps in another process, and it is read again until then.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, short> _ledgerPartitions = new();

    /// <summary>Partition numbers this process has resolved, by data-partition-id, compared regardless of case.</summary>
    private readonly ConcurrentDictionary<string, short> _partitionIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Partition names by number, for the rows the ledger reads back.</summary>
    private readonly ConcurrentDictionary<short, string> _partitionNames = new();

    public LedgerDirectory(LedgerDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Keeps that <paramref name="flowId"/>'s rows are kept under partition number <paramref name="partitionId"/>, once a registration placed it.</summary>
    public void Remember(Guid flowId, short partitionId) => _ledgerPartitions[flowId] = partitionId;

    /// <summary>Forgets the partition of <paramref name="flowId"/>, an identity a move took out of the directory.</summary>
    public void Forget(Guid flowId) => _ledgerPartitions.TryRemove(flowId, out _);

    /// <summary>
    /// The partition number <paramref name="flowId"/>'s rows are kept under, for a read: null when no run has registered the
    /// ledger, so there is nothing of it to read; <see cref="DeliveryModel.UnassignedPartition"/> for a ledger not yet placed.
    /// </summary>
    public async ValueTask<short?> PartitionOfAsync(Guid flowId, CancellationToken ct)
    {
        if (_ledgerPartitions.TryGetValue(flowId, out var known))
        {
            return known;
        }

        var row = await _database.ReadAsync(db => db.DeliveryLedgers.AsNoTracking().Where(l => l.FlowId == flowId).Select(l => (short?)l.PartitionId).FirstOrDefaultAsync(ct), ct).ConfigureAwait(false);
        if (row is { } found && found != Unassigned)
        {
            _ledgerPartitions[flowId] = found;
        }

        return row;
    }

    /// <summary>The partition number every row a write adds to <paramref name="flowId"/>'s ledger is keyed by.</summary>
    /// <exception cref="DeliveryException">No run has registered the ledger.</exception>
    public async ValueTask<short> WritePartitionAsync(Guid flowId, CancellationToken ct)
        => await PartitionOfAsync(flowId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException(
                $"Ledger {flowId:D} is not in the ledger's directory, so nothing of it can be written: a run registers the ledger of the partition it delivers to before it writes a row of it (osdu/docs/reference/concepts/ledger.md, Ledgers, flows and partitions).");

    /// <summary>The number of the partition named <paramref name="partition"/>, or null when no ledger has been kept under it.</summary>
    public async ValueTask<short?> PartitionIdOfAsync(string partition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var name = partition.Trim();
        if (_partitionIds.TryGetValue(name, out var known))
        {
            return known;
        }

        var row = await _database.ReadAsync(db => db.DeliveryLedgerPartitions.AsNoTracking().Where(p => p.Name == name).Select(p => new { p.PartitionId, p.Name }).FirstOrDefaultAsync(ct), ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        _partitionIds[row.Name] = row.PartitionId;
        _partitionNames[row.PartitionId] = row.Name;
        return row.PartitionId;
    }

    /// <summary>The data-partition-id of partition number <paramref name="partitionId"/>, or null for an unassigned ledger.</summary>
    public async ValueTask<string?> PartitionNameAsync(short partitionId, CancellationToken ct)
    {
        if (partitionId == Unassigned)
        {
            return null;
        }

        if (_partitionNames.TryGetValue(partitionId, out var known))
        {
            return known;
        }

        // The table holds one row per partition the ledger has kept, so every name is read at once.
        var rows = await _database.ReadAsync(db => db.DeliveryLedgerPartitions.AsNoTracking().Select(p => new { p.PartitionId, p.Name }).ToListAsync(ct), ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            _partitionNames[row.PartitionId] = row.Name;
            _partitionIds[row.Name] = row.PartitionId;
        }

        return _partitionNames.TryGetValue(partitionId, out var name)
            ? name
            : throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"Partition number {partitionId} is not in the ledger's directory, which numbers every partition a ledger row names."));
    }
}
