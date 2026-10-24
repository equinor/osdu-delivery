using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Selecting an inventory's ids by what OSDU holds (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU holds):
// the selections an operator's conditions make, kept under the inventory's partition with every id their match found. The
// control plane opens a selection and reads it; the inventory flow's match run fills it.
public sealed partial class OsduLedger
{
    /// <summary>The most selection ids one statement of a sweep deletes, so a sweep never holds a lock on the whole table.</summary>
    private const int SweepBatch = 5000;

    public async Task<long> CreateInventorySelectionAsync(string partition, InventorySelectionStart start, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.ConditionsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.FiltersJson);
        if (start.ConditionsKey.Length != DeliveryModel.SelectionKeyLength)
        {
            throw new DeliveryException($"A selection's conditions key is a SHA-256 in hex, {DeliveryModel.SelectionKeyLength} characters; '{start.ConditionsKey}' is not one.");
        }

        var id = await PartitionIdOfAsync(partition, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Partition '{partition}' keeps no ledger, so it holds no inventory to select from.");
        await using var db = Open();
        var row = new DeliveryInventorySelection
        {
            PartitionId = id,
            InventoryId = start.InventoryId,
            Actor = start.Actor.Length <= 200 ? start.Actor : start.Actor[..200],
            ConditionsJson = start.ConditionsJson,
            ConditionsKey = start.ConditionsKey,
            FiltersJson = start.FiltersJson,
            Status = InventorySelections.Queued,
            BuildRunId = start.BuildRunId,
            CreatedUtc = start.CreatedUtc,
        };
        db.DeliveryInventorySelections.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.InventorySelectionId;
    }

    public async Task<InventorySelectionState?> FindInventorySelectionAsync(string partition, int inventoryId, string conditionsKey, long? buildRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(conditionsKey);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return null;
        }

        return await ReadAsync(
            db => db.DeliveryInventorySelections.AsNoTracking()
                .Where(s => s.PartitionId == id && s.InventoryId == inventoryId && s.ConditionsKey == conditionsKey && s.DuplicateCheckId == null
                    && (s.Status == InventorySelections.Queued || s.Status == InventorySelections.Running
                        || (s.Status == InventorySelections.Completed && s.BuildRunId == buildRunId)))
                .OrderByDescending(s => s.InventorySelectionId)
                .Select(s => ToInventorySelectionState(s))
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<InventorySelectionState?> GetInventorySelectionAsync(string partition, long selectionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return null;
        }

        return await ReadAsync(
            db => db.DeliveryInventorySelections.AsNoTracking()
                .Where(s => s.PartitionId == id && s.InventorySelectionId == selectionId)
                .Select(s => ToInventorySelectionState(s))
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task SetInventorySelectionRunAsync(string partition, long selectionId, Guid runId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var id = await PartitionIdOfAsync(partition, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Partition '{partition}' keeps no ledger, so it holds no selection {selectionId}.");
        await using var db = Open();
        // A match that started already names its run itself; only a queued one is named here.
        await db.DeliveryInventorySelections
            .Where(s => s.PartitionId == id && s.InventorySelectionId == selectionId && s.RunId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RunId, runId), ct).ConfigureAwait(false);
    }

    public async Task FailInventorySelectionAsync(string partition, long selectionId, string why, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return;
        }

        await using var db = Open();
        var error = RunError(why);
        await db.DeliveryInventorySelections
            .Where(s => s.PartitionId == id && s.InventorySelectionId == selectionId && (s.Status == InventorySelections.Queued || s.Status == InventorySelections.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, InventorySelections.Failed)
                .SetProperty(r => r.CompletedUtc, nowUtc)
                .SetProperty(r => r.Error, error), ct).ConfigureAwait(false);
        await DeleteSelectionItemsAsync(db, id, selectionId, ct).ConfigureAwait(false);
    }

    public async Task<InventorySelectionState?> StartInventorySelectionAsync(
        Guid flowId, int inventoryId, long selectionId, Guid runId, long? buildRunId, DateTime nowUtc, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var row = await db.DeliveryInventorySelections
            .FirstOrDefaultAsync(s => s.PartitionId == partition && s.InventorySelectionId == selectionId && s.InventoryId == inventoryId, ct).ConfigureAwait(false);
        if (row is null || row.DuplicateCheckId is not null)
        {
            // A selection a duplicate check made holds that check's picks; no match fills it.
            return null;
        }

        // A run tried again starts its match afresh: what an attempt before it found is cleared with its counts.
        await DeleteSelectionItemsAsync(db, partition, selectionId, ct).ConfigureAwait(false);
        row.Status = InventorySelections.Running;
        row.RunId = runId;
        row.BuildRunId = buildRunId;
        row.StartedUtc = nowUtc;
        row.CompletedUtc = null;
        row.Error = null;
        row.Query = null;
        row.Direction = null;
        row.NotesJson = null;
        row.ColumnsJson = null;
        row.Candidates = 0;
        row.ToCheck = 0;
        row.Checked = 0;
        row.Matched = 0;
        row.Outside = 0;
        row.Changed = 0;
        row.Requests = 0;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToInventorySelectionState(row);
    }

    public async Task<long> CountInventoryServedAsync(Guid flowId, int inventoryId, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        return await ReadAsync(
            db => db.DeliveryInventoryRecords.AsNoTracking()
                .LongCountAsync(r => r.PartitionId == partition && r.InventoryId == inventoryId && r.GoneUtc == null && r.FirstSeenUtc != null, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryServedId>> InventoryServedAsync(Guid flowId, int inventoryId, long after, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        return await ReadAsync(
            db => db.DeliveryInventoryRecords.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.InventoryId == inventoryId && r.InventoryRecordId > after && r.GoneUtc == null && r.FirstSeenUtc != null)
                .OrderBy(r => r.InventoryRecordId)
                .Take(limit)
                .Select(r => new InventoryServedId(r.InventoryRecordId, r.TargetId, r.Version))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task PlanInventorySelectionAsync(Guid flowId, long selectionId, long candidates, long toCheck, string direction, string? query, long requests, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(candidates);
        ArgumentOutOfRangeException.ThrowIfNegative(toCheck);
        ArgumentOutOfRangeException.ThrowIfNegative(requests);
        if (direction is not (InventorySelections.ByConditions or InventorySelections.ByIds))
        {
            throw new DeliveryException($"A match reads OSDU by {InventorySelections.ByConditions} or by {InventorySelections.ByIds}, not '{direction}'.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await db.DeliveryInventorySelections
            .Where(s => s.PartitionId == partition && s.InventorySelectionId == selectionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Candidates, candidates)
                .SetProperty(r => r.ToCheck, toCheck)
                .SetProperty(r => r.Direction, direction)
                .SetProperty(r => r.Query, query)
                .SetProperty(r => r.Requests, r => r.Requests + requests), ct).ConfigureAwait(false);
    }

    public async Task<InventorySelectionWrite> AddInventorySelectionItemsAsync(
        Guid flowId, int inventoryId, long selectionId, IReadOnlyList<InventorySelectionHit> hits, long checkedCount, long requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentOutOfRangeException.ThrowIfNegative(checkedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(requests);
        if (hits.Count > InventorySelections.WriteChunk)
        {
            throw new DeliveryException($"A write of a match keeps at most {InventorySelections.WriteChunk} records; {hits.Count} were given.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await RetryDeadlockAsync(
            () => SqlServerLedgerBulk.AddInventorySelectionItemsAsync(db, partition, inventoryId, selectionId, hits, checkedCount, requests, ct),
            ct).ConfigureAwait(false);
    }

    public async Task CompleteInventorySelectionAsync(Guid flowId, long selectionId, InventorySelectionEnd outcome, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Status is not (InventorySelections.Completed or InventorySelections.Failed))
        {
            throw new DeliveryException($"A match ends {InventorySelections.Completed} or {InventorySelections.Failed}, not '{outcome.Status}'.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var row = await db.DeliveryInventorySelections
            .FirstOrDefaultAsync(s => s.PartitionId == partition && s.InventorySelectionId == selectionId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Inventory selection {selectionId} is not in the ledger of {flowId:D}.");
        row.Status = outcome.Status;
        row.CompletedUtc = nowUtc;
        row.Query = outcome.Query ?? row.Query;
        row.Direction = outcome.Direction ?? row.Direction;
        row.NotesJson = outcome.Notes.Count == 0 ? null : JsonSerializer.Serialize(outcome.Notes);
        row.ColumnsJson = outcome.Columns.Count == 0 ? null : JsonSerializer.Serialize(outcome.Columns);
        if (outcome.Status == InventorySelections.Failed)
        {
            // A failed match holds no id: what it found before it stopped is not what the conditions match.
            row.Error = RunError(outcome.Error ?? "the match stopped without saying why");
            row.Matched = 0;
            row.Outside = 0;
            row.Changed = 0;
            await DeleteSelectionItemsAsync(db, partition, selectionId, ct).ConfigureAwait(false);
        }
        else
        {
            row.Error = null;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<InventoryCounts> InventorySelectionCountsAsync(string partition, long selectionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return new InventoryCounts(new Dictionary<string, long>(StringComparer.Ordinal));
        }

        var rows = await ReadAsync(
            db => (from s in db.DeliveryInventorySelectionItems.AsNoTracking()
                   join r in db.DeliveryInventoryRecords.AsNoTracking() on new { s.PartitionId, s.InventoryRecordId } equals new { r.PartitionId, r.InventoryRecordId }
                   where s.PartitionId == id && s.InventorySelectionId == selectionId
                   group r by r.Finding into g
                   select new { Finding = g.Key, Count = g.LongCount() })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return new InventoryCounts(rows.ToDictionary(r => r.Finding, r => r.Count, StringComparer.Ordinal));
    }

    public async Task<IReadOnlyList<InventorySelectionRecord>> ListInventorySelectionRecordsAsync(
        string partition, long selectionId, string? finding, long? after, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (finding is not null && !InventoryFindings.IsKnown(finding))
        {
            throw new DeliveryException($"'{finding}' is not a finding; an inventory finds {string.Join(", ", InventoryFindings.All)}.");
        }

        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        var start = after ?? 0;
        var rows = await ReadAsync(
            db => (from s in db.DeliveryInventorySelectionItems.AsNoTracking()
                   join r in db.DeliveryInventoryRecords.AsNoTracking() on new { s.PartitionId, s.InventoryRecordId } equals new { r.PartitionId, r.InventoryRecordId }
                   where s.PartitionId == id && s.InventorySelectionId == selectionId && s.InventoryRecordId > start && (finding == null || r.Finding == finding)
                   orderby s.InventoryRecordId
                   select new { Record = r, s.MatchedVersion, s.ValuesJson })
                .Take(limit)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(r => new InventorySelectionRecord(ToInventoryRecordState(r.Record), r.MatchedVersion, r.ValuesJson)).ToList();
    }

    public async Task<InventorySelectionSweep> SweepInventorySelectionsAsync(string partition, DateTime olderThanUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return new InventorySelectionSweep(0, 0);
        }

        await using var db = Open();
        var old = await db.DeliveryInventorySelections.AsNoTracking()
            .Where(s => s.PartitionId == id && s.CreatedUtc < olderThanUtc && s.Status != InventorySelections.Expired)
            .Select(s => s.InventorySelectionId)
            .ToListAsync(ct).ConfigureAwait(false);
        if (old.Count == 0)
        {
            return new InventorySelectionSweep(0, 0);
        }

        var deleted = 0;
        var expired = 0;
        foreach (var chunk in old.Chunk(InventoryRemovals.Chunk))
        {
            var kept = await db.DeliveryInventoryRemovals.AsNoTracking()
                .Where(r => r.PartitionId == id && r.InventorySelectionId != null && chunk.Contains(r.InventorySelectionId.Value))
                .Select(r => r.InventorySelectionId!.Value)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var selectionId in chunk)
            {
                await DeleteSelectionItemsAsync(db, id, selectionId, ct).ConfigureAwait(false);
            }

            expired += await db.DeliveryInventorySelections
                .Where(s => s.PartitionId == id && kept.Contains(s.InventorySelectionId))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, InventorySelections.Expired), ct).ConfigureAwait(false);
            var gone = chunk.Except(kept).ToList();
            deleted += await db.DeliveryInventorySelections
                .Where(s => s.PartitionId == id && gone.Contains(s.InventorySelectionId))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        return new InventorySelectionSweep(deleted, expired);
    }

    /// <summary>Deletes a selection's ids a batch at a time, so a selection of hundreds of thousands never holds one long lock.</summary>
    private static async Task DeleteSelectionItemsAsync(OsduDbContext db, short partition, long selectionId, CancellationToken ct)
    {
        while (await db.DeliveryInventorySelectionItems
                   .Where(i => i.PartitionId == partition && i.InventorySelectionId == selectionId)
                   .OrderBy(i => i.InventoryRecordId)
                   .Take(SweepBatch)
                   .ExecuteDeleteAsync(ct).ConfigureAwait(false) == SweepBatch)
        {
        }
    }

    private static InventorySelectionState ToInventorySelectionState(DeliveryInventorySelection s) => new()
    {
        InventorySelectionId = s.InventorySelectionId,
        InventoryId = s.InventoryId,
        RunId = s.RunId,
        Actor = s.Actor,
        ConditionsJson = s.ConditionsJson,
        ConditionsKey = s.ConditionsKey,
        FiltersJson = s.FiltersJson,
        Status = s.Status,
        Query = s.Query,
        Direction = s.Direction,
        NotesJson = s.NotesJson,
        ColumnsJson = s.ColumnsJson,
        BuildRunId = s.BuildRunId,
        Candidates = s.Candidates,
        ToCheck = s.ToCheck,
        Checked = s.Checked,
        Matched = s.Matched,
        Outside = s.Outside,
        Changed = s.Changed,
        Requests = s.Requests,
        CreatedUtc = DateTime.SpecifyKind(s.CreatedUtc, DateTimeKind.Utc),
        StartedUtc = s.StartedUtc is { } started ? DateTime.SpecifyKind(started, DateTimeKind.Utc) : null,
        CompletedUtc = s.CompletedUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
        Error = s.Error,
        DuplicateCheckId = s.DuplicateCheckId,
        DuplicateKeyJson = s.DuplicateKeyJson,
    };
}
