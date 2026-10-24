using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Removing what an inventory found (osdu/docs/reference/flow/inventory-removals.md, What a removal does): the removals an
// operator asked of an inventory's ids, kept under the inventory flow's ledger with what each did to every id it
// reached.
public sealed partial class OsduLedger
{
    public async Task<long> StartInventoryRemovalAsync(Guid flowId, InventoryRemovalStart start, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.Actor);
        if (!InventoryRemovals.IsAskable(start.Finding))
        {
            throw new DeliveryException($"'{start.Finding}' is not a finding an inventory removes; it removes {string.Join(", ", InventoryRemovals.Askable)}.");
        }

        if (!InventoryRemovals.IsScope(start.Scope))
        {
            throw new DeliveryException($"'{start.Scope}' is not how much a removal takes; it is one of {string.Join(", ", InventoryRemovals.Scopes)}.");
        }

        if (InventoryRemovals.Mismatch(start.Finding, start.Scope) is { } mismatch)
        {
            throw new DeliveryException($"A removal of {start.Finding} ids cannot take {start.Scope}: {mismatch}");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await db.DeliveryInventoryRemovals
            .Where(r => r.PartitionId == partition && r.InventoryId == start.InventoryId && r.Status == InventoryRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, InventoryRunStatus.Failed)
                .SetProperty(r => r.CompletedUtc, start.StartedUtc)
                .SetProperty(r => r.Error, "the removal ended without finishing: its process stopped before it did; what it recorded stands"), ct).ConfigureAwait(false);
        var removal = new DeliveryInventoryRemoval
        {
            PartitionId = partition,
            InventoryId = start.InventoryId,
            RunId = start.RunId,
            Actor = start.Actor.Length <= 200 ? start.Actor : start.Actor[..200],
            Finding = start.Finding,
            Scope = start.Scope,
            NamesIds = start.NamesIds,
            Requested = start.Requested,
            Status = InventoryRunStatus.Running,
            StartedUtc = start.StartedUtc,
            ActivityId = start.ActivityId,
            InventorySelectionId = start.SelectionId,
        };
        db.DeliveryInventoryRemovals.Add(removal);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return removal.InventoryRemovalId;
    }

    public async Task<IReadOnlyList<InventoryRemovalCandidate>> InventoryRemovalCandidatesAsync(
        Guid flowId, int inventoryId, string finding, IReadOnlyList<string>? ids, IReadOnlyList<string> owners, long after, int limit, long? selectionId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finding);
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (ids is not null && selectionId is not null)
        {
            throw new DeliveryException("A removal names its ids or takes them from a selection, not both.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await SqlServerLedgerBulk.InventoryRemovalCandidatesAsync(db, partition, inventoryId, finding, ids, selectionId, owners, after, limit, ct).ConfigureAwait(false);
    }

    public async Task RecordInventoryRemovalAsync(
        Guid flowId, int inventoryId, long removalId, IReadOnlyList<InventoryRemovalItem> items, string removedDetail, string goneDetail, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(removedDetail);
        ArgumentException.ThrowIfNullOrWhiteSpace(goneDetail);
        if (items.Count == 0)
        {
            return;
        }

        if (items.FirstOrDefault(i => !InventoryRemovals.IsOutcome(i.Outcome)) is { } unknown)
        {
            throw new DeliveryException($"'{unknown.Outcome}' is not what a removal comes to for an id; it is one of {string.Join(", ", InventoryRemovals.Outcomes)}.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await RetryDeadlockAsync(
            () => SqlServerLedgerBulk.RecordInventoryRemovalAsync(db, partition, inventoryId, removalId, items, removedDetail, goneDetail, nowUtc, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<InventoryRemovalRound> RemoveFromInventoryAsync(
        Guid flowId, int inventoryId, long removalId, IReadOnlyList<string>? ids, long after, int limit, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        if (ids is { Count: 0 })
        {
            throw new DeliveryException("A removal that names its ids names at least one; leave them out to remove every gone id.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await RetryDeadlockAsync(
            () => SqlServerLedgerBulk.RemoveFromInventoryAsync(db, partition, inventoryId, removalId, ids, after, limit, nowUtc, ct),
            ct).ConfigureAwait(false);
    }

    public async Task CompleteInventoryRemovalAsync(Guid flowId, long removalId, string status, string? stoppedBy, DateTime nowUtc, CancellationToken ct = default)
    {
        if (status is not (InventoryRunStatus.Completed or InventoryRunStatus.Failed))
        {
            throw new DeliveryException($"A removal ends {InventoryRunStatus.Completed} or {InventoryRunStatus.Failed}, not '{status}'.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var updated = await db.DeliveryInventoryRemovals
            .Where(r => r.PartitionId == partition && r.InventoryRemovalId == removalId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.CompletedUtc, nowUtc)
                .SetProperty(r => r.Error, RunError(stoppedBy)), ct).ConfigureAwait(false);
        if (updated == 0)
        {
            throw new DeliveryException($"Inventory removal {removalId} is not in the ledger of {flowId:D}.");
        }
    }

    public async Task<IReadOnlyList<InventoryRemovalState>> ListInventoryRemovalsAsync(string partition, int inventoryId, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        return await ReadAsync(
            db => db.DeliveryInventoryRemovals.AsNoTracking()
                .Where(r => r.PartitionId == id && r.InventoryId == inventoryId)
                .OrderByDescending(r => r.InventoryRemovalId)
                .Take(limit)
                .Select(r => ToInventoryRemovalState(r))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<InventoryRemovalState?> GetInventoryRemovalAsync(string partition, long removalId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return null;
        }

        return await ReadAsync(
            db => db.DeliveryInventoryRemovals.AsNoTracking()
                .Where(r => r.PartitionId == id && r.InventoryRemovalId == removalId)
                .Select(r => ToInventoryRemovalState(r))
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryRemovalItemState>> ListInventoryRemovalItemsAsync(
        string partition, long removalId, string? outcome, long? after, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (outcome is not null && !InventoryRemovals.IsOutcome(outcome))
        {
            throw new DeliveryException($"'{outcome}' is not what a removal comes to for an id; it is one of {string.Join(", ", InventoryRemovals.Outcomes)}.");
        }

        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        var from = after ?? 0;
        return await ReadAsync(
            db => db.DeliveryInventoryRemovalItems.AsNoTracking()
                .Where(i => i.PartitionId == id && i.InventoryRemovalId == removalId && (outcome == null || i.Outcome == outcome) && i.InventoryRemovalItemId > from)
                .OrderBy(i => i.InventoryRemovalItemId)
                .Take(limit)
                .Select(i => ToInventoryRemovalItemState(i))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryRemovalItemState>> LookupInventoryRemovalItemsAsync(string partition, string targetId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        var wanted = targetId.Trim();
        return await ReadAsync(
            db => db.DeliveryInventoryRemovalItems.AsNoTracking()
                .Where(i => i.PartitionId == id && i.TargetId == wanted)
                .OrderByDescending(i => i.InventoryRemovalItemId)
                .Take(100)
                .Select(i => ToInventoryRemovalItemState(i))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryRecordState>> InventoryRecordsOfAsync(string partition, int inventoryId, IReadOnlyList<string> targetIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(targetIds);
        if (targetIds.Count == 0 || await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        var wanted = targetIds.Distinct(StringComparer.Ordinal).ToList();
        var rows = new List<InventoryRecordState>(wanted.Count);
        foreach (var chunk in wanted.Chunk(InventoryRemovals.Chunk))
        {
            rows.AddRange(await ReadAsync(
                db => db.DeliveryInventoryRecords.AsNoTracking()
                    .Where(r => r.PartitionId == id && r.InventoryId == inventoryId && chunk.Contains(r.TargetId))
                    .Select(r => ToInventoryRecordState(r))
                    .ToListAsync(ct),
                ct).ConfigureAwait(false));
        }

        return rows;
    }

    private static InventoryRemovalState ToInventoryRemovalState(DeliveryInventoryRemoval r) => new()
    {
        InventoryRemovalId = r.InventoryRemovalId,
        InventoryId = r.InventoryId,
        RunId = r.RunId,
        Actor = r.Actor,
        Finding = r.Finding,
        Scope = r.Scope,
        NamesIds = r.NamesIds,
        Requested = r.Requested,
        Status = r.Status,
        StartedUtc = DateTime.SpecifyKind(r.StartedUtc, DateTimeKind.Utc),
        CompletedUtc = r.CompletedUtc == null ? null : DateTime.SpecifyKind(r.CompletedUtc.Value, DateTimeKind.Utc),
        Removed = r.Removed,
        Gone = r.Gone,
        Skipped = r.Skipped,
        Failed = r.Failed,
        Error = r.Error,
        ActivityId = r.ActivityId,
        SelectionId = r.InventorySelectionId,
    };

    private static InventoryRemovalItemState ToInventoryRemovalItemState(DeliveryInventoryRemovalItem i) => new()
    {
        InventoryRemovalItemId = i.InventoryRemovalItemId,
        InventoryRemovalId = i.InventoryRemovalId,
        InventoryRecordId = i.InventoryRecordId,
        TargetId = i.TargetId,
        Version = i.Version,
        Finding = i.Finding,
        Outcome = i.Outcome,
        Reason = i.Reason,
        LedgerFlowId = i.LedgerFlowId,
        DeliveryKey = i.DeliveryKey,
        RecordedUtc = DateTime.SpecifyKind(i.RecordedUtc, DateTimeKind.Utc),
    };
}
