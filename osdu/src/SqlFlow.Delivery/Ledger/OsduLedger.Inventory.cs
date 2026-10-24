using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// The inventories of inventory flows (osdu/docs/reference/flow/inventory-builds.md, The tables an inventory keeps): every id an
// OSDU kind holds in a partition, kept under the inventory flow's ledger and compared with every ledger of the
// partition. The rows are the flow's; the comparisons read the other ledgers' records, artifacts and purged records,
// never write them.
public sealed partial class OsduLedger
{
    /// <summary>The most identities an inventory's owners are inferred from.</summary>
    private const int MaxInferredOwners = 200;

    public async Task<InventoryState> RegisterInventoryAsync(Guid flowId, string flowName, string name, string kind, string? query, string readMode, string versions, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var now = Now;
        await using var db = Open();
        var row = await db.DeliveryInventories.FirstOrDefaultAsync(i => i.PartitionId == partition && i.FlowId == flowId && i.Name == name, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new DeliveryInventory { PartitionId = partition, FlowId = flowId, Name = name, CreatedUtc = now };
            db.DeliveryInventories.Add(row);
        }

        row.FlowName = flowName.Length <= DeliveryLedger.MaxFlowNameLength ? flowName : flowName[..DeliveryLedger.MaxFlowNameLength];
        row.Kind = kind;
        row.Query = query;
        row.ReadMode = readMode;
        row.Versions = versions;
        row.UpdatedUtc = now;
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (row.InventoryId == 0)
        {
            // Another process registered it between the read and the write: the row it wrote is the inventory.
            await using var again = Open();
            row = await again.DeliveryInventories.AsNoTracking().FirstAsync(i => i.PartitionId == partition && i.FlowId == flowId && i.Name == name, ct).ConfigureAwait(false);
        }

        return await ToInventoryStateAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<long> StartInventoryRunAsync(Guid flowId, int inventoryId, string operation, Guid? runId, string actor, string readMode, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await db.DeliveryInventoryRuns
            .Where(r => r.PartitionId == partition && r.InventoryId == inventoryId && r.Status == InventoryRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, InventoryRunStatus.Failed)
                .SetProperty(r => r.CompletedUtc, nowUtc)
                .SetProperty(r => r.Error, "the run ended without finishing: its process stopped before it did"), ct).ConfigureAwait(false);
        var earlier = db.DeliveryInventoryRuns.Where(r => r.PartitionId == partition && r.InventoryId == inventoryId).Select(r => r.InventoryRunId);
        await db.DeliveryInventoryScans.Where(s => s.PartitionId == partition && earlier.Contains(s.InventoryRunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var run = new DeliveryInventoryRun
        {
            PartitionId = partition,
            InventoryId = inventoryId,
            RunId = runId,
            Operation = operation,
            Actor = actor.Length <= 200 ? actor : actor[..200],
            Status = InventoryRunStatus.Running,
            StartedUtc = nowUtc,
            ReadMode = readMode,
        };
        db.DeliveryInventoryRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return run.InventoryRunId;
    }

    public async Task AppendInventoryScanAsync(Guid flowId, long inventoryRunId, IReadOnlyList<InventoryScanRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await RetryDeadlockAsync(() => SqlServerLedgerBulk.AppendInventoryScanAsync(db, partition, inventoryRunId, rows, ct), ct).ConfigureAwait(false);
    }

    public async Task<InventoryMerge> MergeInventoryAsync(Guid flowId, int inventoryId, long inventoryRunId, DateTime nowUtc, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await RetryDeadlockAsync(() => SqlServerLedgerBulk.MergeInventoryAsync(db, partition, inventoryId, inventoryRunId, nowUtc, ct), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryVersionsDue>> InventoryVersionsDueAsync(Guid flowId, int inventoryId, int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        return await ReadAsync(
            db => db.DeliveryInventoryRecords.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.InventoryId == inventoryId && r.GoneUtc == null && r.FirstSeenUtc != null && r.Version != null
                    && (r.VersionsAt == null || r.VersionsAt != r.Version))
                .OrderBy(r => r.InventoryRecordId)
                .Take(max)
                .Select(r => new InventoryVersionsDue(r.InventoryRecordId, r.TargetId, r.Version!.Value))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task WriteInventoryVersionsAsync(Guid flowId, IReadOnlyList<InventoryVersionsRead> reads, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reads);
        if (reads.Count == 0)
        {
            return;
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await RetryDeadlockAsync(() => SqlServerLedgerBulk.WriteInventoryVersionsAsync(db, partition, reads, ct), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryOwner>> InventoryOwnersAsync(Guid flowId, int inventoryId, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await SqlServerLedgerBulk.InventoryOwnersAsync(db, partition, inventoryId, MaxInferredOwners, ct).ConfigureAwait(false);
    }

    public async Task ReconcileInventoryAsync(Guid flowId, int inventoryId, IReadOnlyList<string> owners, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owners);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await RetryDeadlockAsync(() => SqlServerLedgerBulk.ReconcileInventoryAsync(db, partition, inventoryId, owners, nowUtc, ct), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryCandidate>> InventoryCandidatesAsync(Guid flowId, int inventoryId, string? typePrefix, int max, CancellationToken ct = default)
    {
        if (max < 1)
        {
            return [];
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await SqlServerLedgerBulk.InventoryCandidatesAsync(db, partition, inventoryId, typePrefix, max, ct).ConfigureAwait(false);
    }

    public async Task RecordInventoryChecksAsync(Guid flowId, int inventoryId, IReadOnlyList<InventoryCheck> checks, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await RetryDeadlockAsync(() => SqlServerLedgerBulk.RecordInventoryChecksAsync(db, partition, inventoryId, checks, nowUtc, ct), ct).ConfigureAwait(false);
    }

    public async Task<InventoryCounts> InventoryCountsAsync(Guid flowId, int inventoryId, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        return await CountsAsync(partition, inventoryId, ct).ConfigureAwait(false);
    }

    public async Task CompleteInventoryRunAsync(Guid flowId, long inventoryRunId, InventoryRunState outcome, string? ownersSource, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var run = await db.DeliveryInventoryRuns.FirstOrDefaultAsync(r => r.PartitionId == partition && r.InventoryRunId == inventoryRunId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Inventory run {inventoryRunId} is not in the ledger of {flowId:D}.");
        run.Status = outcome.Status;
        run.CompletedUtc = nowUtc;
        run.Listed = outcome.Listed;
        run.Pages = outcome.Pages;
        run.Requests = outcome.Requests;
        run.Added = outcome.Added;
        run.Changed = outcome.Changed;
        run.Gone = outcome.Gone;
        run.Returned = outcome.Returned;
        run.MissingChecked = outcome.MissingChecked;
        run.FindingsJson = outcome.FindingsJson;
        run.OwnersJson = outcome.OwnersJson;
        run.Error = RunError(outcome.Error);
        if (outcome.Status == InventoryRunStatus.Completed)
        {
            var inventory = await db.DeliveryInventories.FirstAsync(i => i.PartitionId == partition && i.InventoryId == run.InventoryId, ct).ConfigureAwait(false);
            if (run.Operation == InventoryRunStatus.Build)
            {
                inventory.LastBuildRunId = run.InventoryRunId;
                inventory.LastBuiltUtc = nowUtc;
            }

            if (outcome.FindingsJson is not null)
            {
                inventory.LastReconcileRunId = run.InventoryRunId;
                inventory.LastReconciledUtc = nowUtc;
                inventory.OwnersJson = outcome.OwnersJson;
                inventory.OwnersSource = ownersSource;
            }

            inventory.UpdatedUtc = nowUtc;
        }
        else
        {
            // A failed run's staged read is never merged; the merge of a completed one consumed it already.
            await db.DeliveryInventoryScans.Where(s => s.PartitionId == partition && s.InventoryRunId == inventoryRunId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryState>> ListInventoriesAsync(string? partition, CancellationToken ct = default)
    {
        short? id = null;
        if (!string.IsNullOrWhiteSpace(partition))
        {
            id = await PartitionIdOfAsync(partition, ct).ConfigureAwait(false);
            if (id is null)
            {
                return [];
            }
        }

        var rows = await ReadAsync(
            db => db.DeliveryInventories.AsNoTracking()
                .Where(i => id == null || i.PartitionId == id)
                .OrderBy(i => i.PartitionId).ThenBy(i => i.FlowName).ThenBy(i => i.Name)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var states = new List<InventoryState>(rows.Count);
        foreach (var row in rows)
        {
            states.Add(await ToInventoryStateAsync(row, ct).ConfigureAwait(false));
        }

        return states;
    }

    public async Task<InventoryState?> GetInventoryAsync(string partition, int inventoryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return null;
        }

        var row = await ReadAsync(db => db.DeliveryInventories.AsNoTracking().FirstOrDefaultAsync(i => i.PartitionId == id && i.InventoryId == inventoryId, ct), ct).ConfigureAwait(false);
        return row is null ? null : await ToInventoryStateAsync(row, ct).ConfigureAwait(false);
    }

    public async Task<InventoryCounts> InventoryCountsAsync(string partition, int inventoryId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        return await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is { } id
            ? await CountsAsync(id, inventoryId, ct).ConfigureAwait(false)
            : new InventoryCounts(new Dictionary<string, long>(StringComparer.Ordinal));
    }

    public async Task<IReadOnlyList<InventoryRecordState>> ListInventoryRecordsAsync(string partition, int inventoryId, string? finding, long? after, int limit, CancellationToken ct = default)
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

        var from = after ?? 0;
        return await ReadAsync(
            db => db.DeliveryInventoryRecords.AsNoTracking()
                .Where(r => r.PartitionId == id && r.InventoryId == inventoryId && (finding == null || r.Finding == finding) && r.InventoryRecordId > from)
                .OrderBy(r => r.InventoryRecordId)
                .Take(limit)
                .Select(r => ToInventoryRecordState(r))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryRecordState>> LookupInventoryRecordsAsync(string partition, string targetId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        var wanted = targetId.Trim();
        return await ReadAsync(
            db => db.DeliveryInventoryRecords.AsNoTracking()
                .Where(r => r.PartitionId == id && r.TargetId == wanted)
                .OrderBy(r => r.InventoryId)
                .Select(r => ToInventoryRecordState(r))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryRunState>> ListInventoryRunsAsync(string partition, int inventoryId, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        return await ReadAsync(
            db => db.DeliveryInventoryRuns.AsNoTracking()
                .Where(r => r.PartitionId == id && r.InventoryId == inventoryId)
                .OrderByDescending(r => r.InventoryRunId)
                .Take(limit)
                .Select(r => ToInventoryRunState(r))
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<InventoryRunState?> GetInventoryRunAsync(string partition, long inventoryRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return null;
        }

        return await ReadAsync(
            db => db.DeliveryInventoryRuns.AsNoTracking()
                .Where(r => r.PartitionId == id && r.InventoryRunId == inventoryRunId)
                .Select(r => ToInventoryRunState(r))
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
    }

    private static InventoryRunState ToInventoryRunState(DeliveryInventoryRun r) => new()
    {
        InventoryRunId = r.InventoryRunId,
        InventoryId = r.InventoryId,
        RunId = r.RunId,
        Operation = r.Operation,
        Actor = r.Actor,
        Status = r.Status,
        StartedUtc = r.StartedUtc,
        CompletedUtc = r.CompletedUtc,
        ReadMode = r.ReadMode,
        Listed = r.Listed,
        Pages = r.Pages,
        Requests = r.Requests,
        Added = r.Added,
        Changed = r.Changed,
        Gone = r.Gone,
        Returned = r.Returned,
        MissingChecked = r.MissingChecked,
        FindingsJson = r.FindingsJson,
        OwnersJson = r.OwnersJson,
        Error = r.Error,
    };

    /// <summary>A run's error as the ledger keeps it: redacted, and cut to its column.</summary>
    private static string? RunError(string? error)
    {
        if (error is null)
        {
            return null;
        }

        var redacted = Http.HeaderRedaction.RedactMessage(error);
        return redacted.Length <= 4000 ? redacted : redacted[..4000];
    }

    private async Task<InventoryCounts> CountsAsync(short partition, int inventoryId, CancellationToken ct)
    {
        var rows = await ReadAsync(
            db => db.DeliveryInventoryRecords.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.InventoryId == inventoryId)
                .GroupBy(r => r.Finding)
                .Select(g => new { Finding = g.Key, Count = g.LongCount() })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return new InventoryCounts(rows.ToDictionary(r => r.Finding, r => r.Count, StringComparer.Ordinal));
    }

    private async Task<InventoryState> ToInventoryStateAsync(DeliveryInventory row, CancellationToken ct) => new()
    {
        InventoryId = row.InventoryId,
        Partition = await PartitionNameAsync(row.PartitionId, ct).ConfigureAwait(false) ?? string.Empty,
        FlowId = row.FlowId,
        FlowName = row.FlowName,
        Name = row.Name,
        Kind = row.Kind,
        Query = row.Query,
        ReadMode = row.ReadMode,
        Versions = row.Versions,
        OwnersJson = row.OwnersJson,
        OwnersSource = row.OwnersSource,
        CreatedUtc = DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc),
        UpdatedUtc = DateTime.SpecifyKind(row.UpdatedUtc, DateTimeKind.Utc),
        LastBuildRunId = row.LastBuildRunId,
        LastBuiltUtc = row.LastBuiltUtc is { } built ? DateTime.SpecifyKind(built, DateTimeKind.Utc) : null,
        LastReconcileRunId = row.LastReconcileRunId,
        LastReconciledUtc = row.LastReconciledUtc is { } reconciled ? DateTime.SpecifyKind(reconciled, DateTimeKind.Utc) : null,
    };

    private static InventoryRecordState ToInventoryRecordState(DeliveryInventoryRecord r) => new()
    {
        InventoryRecordId = r.InventoryRecordId,
        InventoryId = r.InventoryId,
        TargetId = r.TargetId,
        Kind = r.Kind,
        Version = r.Version,
        CreateUser = r.CreateUser,
        CreateTime = r.CreateTime,
        ModifyUser = r.ModifyUser,
        ModifyTime = r.ModifyTime,
        FirstSeenUtc = r.FirstSeenUtc,
        ChangedUtc = r.ChangedUtc,
        GoneUtc = r.GoneUtc,
        Finding = r.Finding,
        FindingUtc = r.FindingUtc,
        LedgerFlowId = r.LedgerFlowId,
        DeliveryKey = r.DeliveryKey,
        LedgerStatus = r.LedgerStatus,
        LedgerVersion = r.LedgerVersion,
        ArtifactId = r.ArtifactId,
        ArtifactState = r.ArtifactState,
        Detail = r.Detail,
    };
}

/// <summary>How inventory runs are written in the ledger.</summary>
public static class InventoryRunStatus
{
    public const string Running = "running";

    public const string Completed = "completed";

    public const string Failed = "failed";

    public const string Build = "build";

    public const string Reconcile = "reconcile";
}
