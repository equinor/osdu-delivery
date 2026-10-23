using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// Removal flows (osdu/docs/reference/flow/removal.md): the runs a removal flow keeps under the ledger of each delivery flow it
// names, the keys it stages, and the ledger's deletions: every key a source removed, with how far taking it out has come.
// Everything here is written under the delivery flow's ledger identity and partition: a removal flow keeps no ledger of its own.
public sealed partial class OsduLedger
{
    public async Task<long> StartRemovalRunAsync(Guid ledgerFlowId, RemovalRunStart start, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.FlowName);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.Entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.Actor);
        if (start.Operation is not (Engine.DeliveryOperations.Remove or Engine.DeliveryOperations.Plan))
        {
            throw new DeliveryException($"A removal run removes or plans, not '{start.Operation}'.");
        }

        var partition = await WritePartitionAsync(ledgerFlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var open = await db.DeliveryRemovalRuns
            .Where(r => r.PartitionId == partition && r.FlowName == start.FlowName && r.Entry == start.Entry && r.Status == SourceRemovals.Running)
            .Select(r => r.RemovalRunId)
            .ToListAsync(ct).ConfigureAwait(false);
        if (open.Count > 0)
        {
            await db.DeliveryRemovalRuns
                .Where(r => r.PartitionId == partition && open.Contains(r.RemovalRunId))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, SourceRemovals.Failure)
                    .SetProperty(r => r.CompletedUtc, start.StartedUtc)
                    .SetProperty(r => r.Error, "the run ended without finishing: its process stopped before it did; what it recorded stands, and the next run decides again"), ct)
                .ConfigureAwait(false);
            await SqlServerLedgerBulk.ClearRemovalStagesAsync(db, partition, open, ct).ConfigureAwait(false);
        }

        var run = new DeliveryRemovalRun
        {
            PartitionId = partition,
            FlowName = start.FlowName,
            Entry = Bounded(start.Entry, DeliveryModel.MaxRemovalNameLength),
            LedgerFlowId = ledgerFlowId,
            DeliveryFlow = Bounded(start.DeliveryFlow, DeliveryModel.MaxRemovalNameLength),
            RunId = start.RunId,
            Operation = start.Operation,
            Actor = Bounded(start.Actor, 200),
            Status = SourceRemovals.Running,
            StartedUtc = start.StartedUtc,
            GuardPercent = start.GuardPercent,
            GuardRecords = start.GuardRecords,
            ApprovedBy = start.ApprovedBy is null ? null : Bounded(start.ApprovedBy, 200),
        };
        db.DeliveryRemovalRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return run.RemovalRunId;
    }

    public async Task MarkRemovalSourceReadAsync(Guid ledgerFlowId, long removalRunId, DateTime sourceReadUtc, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(ledgerFlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await db.DeliveryRemovalRuns
            .Where(r => r.PartitionId == partition && r.RemovalRunId == removalRunId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SourceReadUtc, sourceReadUtc), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConvertedRemovalKey>> ConvertRemovalKeysAsync(RemovalKeyConversion conversion, IReadOnlyList<RawRemovalKey> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversion);
        ArgumentNullException.ThrowIfNull(rows);
        if (conversion.Columns.Count == 0)
        {
            throw new DeliveryException("A key conversion names at least one key column.");
        }

        if (rows.Count == 0)
        {
            return [];
        }

        await using var db = Open();
        return await RetryDeadlockAsync(() => SqlServerLedgerBulk.ConvertRemovalKeysAsync(db, conversion, rows, ct), ct).ConfigureAwait(false);
    }

    public async Task<long> StageRemovalKeysAsync(Guid ledgerFlowId, long removalRunId, byte side, IReadOnlyList<StagedRemovalKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (side is not (SourceRemovals.SourceSide or SourceRemovals.RemovedSide or SourceRemovals.IngestionSide))
        {
            throw new DeliveryException($"A removal stages its keys on side 1 (the source's keys), 2 (its removed keys) or 3 (the record table), not {side}.");
        }

        if (keys.Count == 0)
        {
            return 0;
        }

        var partition = await WritePartitionAsync(ledgerFlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await RetryDeadlockAsync(() => SqlServerLedgerBulk.StageRemovalKeysAsync(db, partition, removalRunId, side, keys, ct), ct).ConfigureAwait(false);
    }

    public async Task<RemovalDetection> DetectRemovalsAsync(Guid ledgerFlowId, long removalRunId, RemovalComparison comparison, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (comparison.ReadsSource && comparison.SourceReadUtc is null)
        {
            throw new DeliveryException("A comparison against the source's current keys needs the moment their read began.");
        }

        if (!comparison.ReadsSource && !comparison.ReadsRemoved)
        {
            throw new DeliveryException("A comparison reads the source's current keys, its removed keys, or both.");
        }

        if (comparison.GuardPercent is < 0 or > 100)
        {
            throw new DeliveryException($"A removal's guard is a share from 0 to 100, not {comparison.GuardPercent}.");
        }

        var partition = await WritePartitionAsync(ledgerFlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await RetryDeadlockAsync(() => SqlServerLedgerBulk.DetectRemovalsAsync(db, partition, ledgerFlowId, removalRunId, comparison, ct), ct).ConfigureAwait(false);
    }

    public async Task<long> ApproveRemovalsAsync(Guid ledgerFlowId, string removalFlow, string entry, long expected, string approvedBy, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(removalFlow);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);
        ArgumentOutOfRangeException.ThrowIfLessThan(expected, 1);
        var partition = await WritePartitionAsync(ledgerFlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);
            var waiting = db.DeliveryRemovedKeys.Where(k =>
                k.PartitionId == partition && k.FlowId == ledgerFlowId && k.State == SourceRemovals.AwaitingApproval && k.RemovalFlow == removalFlow && k.Entry == entry);
            var held = await waiting.LongCountAsync(ct).ConfigureAwait(false);
            if (held != expected)
            {
                throw new DeliveryException(
                    $"Removal '{entry}' of removal flow '{removalFlow}' has {held} removed key(s) waiting for approval in this ledger, and the approval was given for {expected}: what waits changed since it was shown. Nothing was approved; look at it again and approve again.");
            }

            var approved = await waiting.ExecuteUpdateAsync(u => u
                .SetProperty(k => k.State, SourceRemovals.Pending)
                .SetProperty(k => k.ApprovedBy, Bounded(approvedBy, 200))
                .SetProperty(k => k.ApprovedUtc, nowUtc)
                .SetProperty(k => k.UpdatedUtc, nowUtc), ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (long)approved;
        }).ConfigureAwait(false);
    }

    public async Task<long> CountAwaitingApprovalAsync(Guid ledgerFlowId, string removalFlow, string entry, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(removalFlow);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        if (await PartitionOfAsync(ledgerFlowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return 0;
        }

        return await ReadAsync(
            db => db.DeliveryRemovedKeys.AsNoTracking().LongCountAsync(k =>
                k.PartitionId == partition && k.FlowId == ledgerFlowId && k.State == SourceRemovals.AwaitingApproval && k.RemovalFlow == removalFlow && k.Entry == entry, ct),
            ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<RemovedKeyState>> PendingRemovalsAsync(Guid ledgerFlowId, DeliveryKey? after, int max, CancellationToken ct = default)
        => ListRemovedKeysAsync(ledgerFlowId, SourceRemovals.Pending, null, after, max, ct);

    public async Task SettleRemovalsAsync(Guid ledgerFlowId, long removalRunId, Guid? runId, IReadOnlyList<RemovalKeyOutcome> outcomes, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0)
        {
            return;
        }

        if (outcomes.FirstOrDefault(o => !SourceRemovals.IsOutcome(o.Outcome)) is { } unknown)
        {
            throw new DeliveryException($"'{unknown.Outcome}' is not what a try to take out a removed key comes to; it is one of {string.Join(", ", SourceRemovals.Outcomes)}.");
        }

        var partition = await WritePartitionAsync(ledgerFlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await RetryDeadlockAsync(async () =>
        {
            await SqlServerLedgerBulk.SettleRemovalsAsync(db, partition, ledgerFlowId, removalRunId, runId, outcomes, nowUtc, ct).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
    }

    public async Task AddRemovalTallyAsync(Guid ledgerFlowId, long removalRunId, RemovalTally tally, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tally);
        var partition = await WritePartitionAsync(ledgerFlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await RetryDeadlockAsync(
            () => db.DeliveryRemovalRuns
                .Where(r => r.PartitionId == partition && r.RemovalRunId == removalRunId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.RowsRead, r => r.RowsRead + tally.RowsRead)
                    .SetProperty(r => r.SourceKeys, r => r.SourceKeys + tally.SourceKeys)
                    .SetProperty(r => r.ListedKeys, r => r.ListedKeys + tally.ListedKeys)
                    .SetProperty(r => r.IngestionKeys, r => r.IngestionKeys + tally.IngestionKeys)
                    .SetProperty(r => r.Compared, r => r.Compared + tally.Compared)
                    .SetProperty(r => r.Detected, r => r.Detected + tally.Detected)
                    .SetProperty(r => r.AwaitingApproval, r => r.AwaitingApproval + tally.AwaitingApproval)
                    .SetProperty(r => r.Approved, r => r.Approved + tally.Approved)
                    .SetProperty(r => r.Removed, r => r.Removed + tally.Removed)
                    .SetProperty(r => r.AlreadyGone, r => r.AlreadyGone + tally.AlreadyGone)
                    .SetProperty(r => r.Settled, r => r.Settled + tally.Settled)
                    .SetProperty(r => r.KeptOut, r => r.KeptOut + tally.KeptOut)
                    .SetProperty(r => r.Referenced, r => r.Referenced + tally.Referenced)
                    .SetProperty(r => r.Shared, r => r.Shared + tally.Shared)
                    .SetProperty(r => r.Busy, r => r.Busy + tally.Busy)
                    .SetProperty(r => r.Failed, r => r.Failed + tally.Failed)
                    .SetProperty(r => r.CameBack, r => r.CameBack + tally.CameBack)
                    .SetProperty(r => r.Lifted, r => r.Lifted + tally.Lifted), ct),
            ct).ConfigureAwait(false);
    }

    public async Task CompleteRemovalRunAsync(Guid ledgerFlowId, long removalRunId, string status, string? stoppedBy, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        if (status is not (SourceRemovals.Completed or SourceRemovals.Guarded or SourceRemovals.Failure))
        {
            throw new DeliveryException($"A removal run ends {SourceRemovals.Completed}, {SourceRemovals.Guarded} or {SourceRemovals.Failure}, not '{status}'.");
        }

        var partition = await WritePartitionAsync(ledgerFlowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await SqlServerLedgerBulk.CompleteRemovalRunAsync(db, partition, removalRunId, status, RunError(stoppedBy), activityId, nowUtc, ct).ConfigureAwait(false);
    }

    public async Task<RemovalRunState?> GetRemovalRunAsync(string partition, long removalRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return null;
        }

        var row = await ReadAsync(db => db.DeliveryRemovalRuns.AsNoTracking().FirstOrDefaultAsync(r => r.PartitionId == id && r.RemovalRunId == removalRunId, ct), ct).ConfigureAwait(false);
        return row is null ? null : Run(row, partition.Trim());
    }

    public async Task<IReadOnlyList<RemovalRunState>> ListRemovalRunsAsync(string partition, string flowName, string? entry, int max, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        var rows = await ReadAsync(db =>
        {
            var query = db.DeliveryRemovalRuns.AsNoTracking().Where(r => r.PartitionId == id && r.FlowName == flowName);
            if (entry is not null)
            {
                query = query.Where(r => r.Entry == entry);
            }

            return query.OrderByDescending(r => r.RemovalRunId).Take(max).ToListAsync(ct);
        }, ct).ConfigureAwait(false);
        return rows.Select(r => Run(r, partition.Trim())).ToList();
    }

    public async Task<IReadOnlyList<RemovalRunState>> ListRemovalRunsOfLedgerAsync(Guid ledgerFlowId, int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        if (await PartitionOfAsync(ledgerFlowId, ct).ConfigureAwait(false) is not { } id || await PartitionNameAsync(id, ct).ConfigureAwait(false) is not { } name)
        {
            return [];
        }

        var rows = await ReadAsync(
            db => db.DeliveryRemovalRuns.AsNoTracking().Where(r => r.PartitionId == id && r.LedgerFlowId == ledgerFlowId)
                .OrderByDescending(r => r.RemovalRunId).Take(max).ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(r => Run(r, name)).ToList();
    }

    public async Task<IReadOnlyList<RemovedKeyState>> ListRemovedKeysAsync(
        Guid ledgerFlowId, string? state, long? detectedRunId, DeliveryKey? after, int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        if (state is not null && !SourceRemovals.IsState(state))
        {
            throw new DeliveryException($"'{state}' is not a state a removed key is in; it is one of {string.Join(", ", SourceRemovals.States)}.");
        }

        if (await PartitionOfAsync(ledgerFlowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        return await ReadAsync(async db =>
        {
            var query = db.DeliveryRemovedKeys.AsNoTracking().Where(k => k.PartitionId == partition && k.FlowId == ledgerFlowId);
            if (state is not null)
            {
                query = query.Where(k => k.State == state);
            }

            if (detectedRunId is { } run)
            {
                query = query.Where(k => k.DetectedRunId == run);
            }

            if (after is { } from)
            {
                query = query.Where(k => k.DeliveryKey.CompareTo(from.Value) > 0);
            }

            var rows = await query.OrderBy(k => k.DeliveryKey).Take(max).ToListAsync(ct).ConfigureAwait(false);
            var keys = rows.Select(r => r.DeliveryKey).ToList();
            var statuses = await db.DeliveryRecords.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.FlowId == ledgerFlowId && keys.Contains(r.DeliveryKey))
                .Select(r => new { r.DeliveryKey, r.Status })
                .ToDictionaryAsync(r => r.DeliveryKey, r => r.Status, ct).ConfigureAwait(false);
            return (IReadOnlyList<RemovedKeyState>)rows.Select(r => RemovedKey(r, statuses.GetValueOrDefault(r.DeliveryKey))).ToList();
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, long>> RemovedKeyCountsAsync(Guid ledgerFlowId, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(ledgerFlowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        var counts = await ReadAsync(
            db => db.DeliveryRemovedKeys.AsNoTracking().Where(k => k.PartitionId == partition && k.FlowId == ledgerFlowId)
                .GroupBy(k => k.State).Select(g => new { State = g.Key, Count = g.LongCount() }).ToListAsync(ct),
            ct).ConfigureAwait(false);
        return counts.ToDictionary(c => c.State, c => c.Count, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<DeliveryKey, DateTime>> RemovedKeysAsync(Guid flowId, IReadOnlyCollection<DeliveryKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0 || await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return new Dictionary<DeliveryKey, DateTime>();
        }

        var found = new Dictionary<DeliveryKey, DateTime>();
        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(ChunkSize))
        {
            var rows = await ReadAsync(
                db => db.DeliveryRemovedKeys.AsNoTracking()
                    .Where(k => k.PartitionId == partition && k.FlowId == flowId && chunk.Contains(k.DeliveryKey) && k.State != SourceRemovals.AwaitingApproval)
                    .Select(k => new { k.DeliveryKey, k.RemovedUtc })
                    .ToListAsync(ct),
                ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                found[new DeliveryKey(row.DeliveryKey)] = DateTime.SpecifyKind(row.RemovedUtc, DateTimeKind.Utc);
            }
        }

        return found;
    }

    public async Task<RemovedKeyState?> GetRemovedKeyAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return null;
        }

        return await ReadAsync(async db =>
        {
            var row = await db.DeliveryRemovedKeys.AsNoTracking()
                .FirstOrDefaultAsync(k => k.PartitionId == partition && k.FlowId == flowId && k.DeliveryKey == key.Value, ct).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            var status = await db.DeliveryRecords.AsNoTracking()
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && r.DeliveryKey == key.Value)
                .Select(r => r.Status)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return RemovedKey(row, status);
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<DeliveryKey>> LiftRemovedKeysAsync(
        Guid flowId, IReadOnlyCollection<DeliveryKey> keys, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var lifted = new HashSet<DeliveryKey>();
        if (keys.Count == 0)
        {
            return lifted;
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var deleted = StatusText.Of(RecordStatus.Deleted);
        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(SourceRemovals.Chunk))
        {
            await using var db = Open();
            var strategy = db.Database.CreateExecutionStrategy();
            var held = await strategy.ExecuteAsync(async () =>
            {
                // A retry starts from what the database holds, not from what a failed try left tracked.
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                var named = await db.DeliveryRemovedKeys
                    .Where(k => k.PartitionId == partition && k.FlowId == flowId && chunk.Contains(k.DeliveryKey))
                    .Select(k => k.DeliveryKey)
                    .ToListAsync(ct).ConfigureAwait(false);
                if (named.Count == 0)
                {
                    return named;
                }

                await db.DeliveryRemovedKeys
                    .Where(k => k.PartitionId == partition && k.FlowId == flowId && named.Contains(k.DeliveryKey))
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);

                // A record the removal took out is planned again, so its row is delivered under its id; one still live (its
                // deletion pending) has nothing to bring back, and keeps whatever else holds it.
                await db.DeliveryRecords
                    .Where(r => r.PartitionId == partition && r.FlowId == flowId && named.Contains(r.DeliveryKey) && r.Status == deleted)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.PlanRequestedUtc, nowUtc)
                        .SetProperty(r => r.UpdatedUtc, nowUtc), ct).ConfigureAwait(false);
                if (activityId is { } activity)
                {
                    var records = await db.DeliveryRecords
                        .Where(r => r.PartitionId == partition && r.FlowId == flowId && named.Contains(r.DeliveryKey))
                        .Select(r => r.DeliveryKey)
                        .ToListAsync(ct).ConfigureAwait(false);
                    db.DeliveryActivityRecords.AddRange(records.Select(key =>
                        new DeliveryActivityRecord { PartitionId = partition, FlowId = flowId, DeliveryKey = key, ActivityId = activity }));
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return named;
            }).ConfigureAwait(false);
            lifted.UnionWith(held.Select(k => new DeliveryKey(k)));
        }

        return lifted;
    }

    public async Task<IReadOnlySet<string>> SharedIdsAsync(Guid flowId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0 || await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        await using var db = Open();
        return await RetryDeadlockAsync(() => SqlServerLedgerBulk.SharedIdsAsync(db, partition, flowId, ids, ct), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> DeliveryFlowPathsAsync(string flowName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        var name = flowName.Trim();
        return await ReadAsync(
            async db => (IReadOnlyList<string>)await db.DeliveryInterfaces.AsNoTracking()
                .Where(i => i.FlowName == name && i.Active)
                .Select(i => i.RelativePath)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeliveryKey>> SettleRemovedBySourceAsync(
        Guid flowId, IReadOnlyDictionary<DeliveryKey, SourceRemovalNote> keys, string worker, string? holder, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        if (keys.Count == 0)
        {
            return [];
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var passed = new List<DeliveryKey>();
        foreach (var chunk in keys.Keys.Chunk(ChunkSize))
        {
            await using var db = Open();
            var ids = chunk.Select(k => k.Value).Distinct().ToList();
            var entities = await db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId && ids.Contains(r.DeliveryKey)).ToListAsync(ct).ConfigureAwait(false);
            foreach (var entity in entities)
            {
                if (entity.LeaseOwner is not null && !string.Equals(entity.LeaseOwner, holder, StringComparison.Ordinal))
                {
                    // A worker is sending it, or another removal holds it: the next removal run takes it.
                    passed.Add(new DeliveryKey(entity.DeliveryKey));
                    continue;
                }

                var source = keys[new DeliveryKey(entity.DeliveryKey)];
                var note = source.Describe(inOsdu: false);
                var removed = string.Equals(entity.Status, StatusText.Of(RecordStatus.Deleted), StringComparison.Ordinal);
                db.DeliveryAttempts.Add(SourceRemovalAttempt(entity, worker, AttemptOutcome.Skipped, source, note, correlationId: null, nowUtc));
                if (!removed)
                {
                    Retire(entity, note, nowUtc, deliverAgain: false);
                }
                else
                {
                    entity.UpdatedUtc = nowUtc;
                }

                if (source.ActivityId is { } activity)
                {
                    db.DeliveryActivityRecords.Add(new DeliveryActivityRecord { PartitionId = partition, FlowId = flowId, DeliveryKey = entity.DeliveryKey, ActivityId = activity });
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return passed;
    }

    /// <summary>The attempt a removal flow writes on a record it took out (outcome deleted) or settled (outcome skipped).</summary>
    private static DeliveryAttempt SourceRemovalAttempt(
        DeliveryRecord entity, string worker, AttemptOutcome outcome, SourceRemovalNote source, string note, string? correlationId, DateTime nowUtc)
        => new()
        {
            PartitionId = entity.PartitionId,
            FlowId = entity.FlowId,
            DeliveryKey = entity.DeliveryKey,
            SubmissionId = entity.LastSubmissionId,
            RunId = source.RunId,
            Worker = worker,
            StartedUtc = nowUtc,
            CompletedUtc = nowUtc,
            Outcome = StatusText.Of(outcome),
            Phase = AttemptPhases.SourceRemoved,
            TargetVersion = entity.TargetVersion,
            ResultJson = AttemptResult.SourceRemoval(correlationId, entity.TargetStateJson, note, source),
            SourceFileName = source.FileName,
            SourceRowNumber = source.RowNumber,
            SourceDeletedUtc = source.RemovedUtc,
        };

    private static RemovalRunState Run(DeliveryRemovalRun row, string partition) => new()
    {
        Partition = partition,
        RemovalRunId = row.RemovalRunId,
        FlowName = row.FlowName,
        Entry = row.Entry,
        LedgerFlowId = row.LedgerFlowId,
        DeliveryFlow = row.DeliveryFlow,
        RunId = row.RunId,
        Operation = row.Operation,
        Actor = row.Actor,
        Status = row.Status,
        StartedUtc = DateTime.SpecifyKind(row.StartedUtc, DateTimeKind.Utc),
        CompletedUtc = row.CompletedUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
        SourceReadUtc = row.SourceReadUtc is { } read ? DateTime.SpecifyKind(read, DateTimeKind.Utc) : null,
        Tally = new RemovalTally
        {
            RowsRead = row.RowsRead,
            SourceKeys = row.SourceKeys,
            ListedKeys = row.ListedKeys,
            IngestionKeys = row.IngestionKeys,
            Compared = row.Compared,
            Detected = row.Detected,
            AwaitingApproval = row.AwaitingApproval,
            Approved = row.Approved,
            Removed = row.Removed,
            AlreadyGone = row.AlreadyGone,
            Settled = row.Settled,
            KeptOut = row.KeptOut,
            Referenced = row.Referenced,
            Shared = row.Shared,
            Busy = row.Busy,
            Failed = row.Failed,
            CameBack = row.CameBack,
            Lifted = row.Lifted,
        },
        GuardPercent = row.GuardPercent,
        GuardRecords = row.GuardRecords,
        ApprovedBy = row.ApprovedBy,
        ActivityId = row.ActivityId,
        Error = row.Error,
    };

    private static RemovedKeyState RemovedKey(DeliveryRemovedKey row, string? recordStatus) => new()
    {
        Key = new DeliveryKey(row.DeliveryKey),
        RecordStatus = recordStatus,
        SourceKeyJson = row.SourceKeyJson,
        Reason = row.Reason,
        RemovedUtc = DateTime.SpecifyKind(row.RemovedUtc, DateTimeKind.Utc),
        FileName = row.FileName,
        RowNumber = row.RowNumber,
        State = row.State,
        Outcome = row.Outcome,
        Detail = row.Detail,
        Tries = row.Tries,
        RemovalFlow = row.RemovalFlow,
        Entry = row.Entry,
        DetectedRunId = row.DetectedRunId,
        DoneRunId = row.DoneRunId,
        RunId = row.RunId,
        ApprovedBy = row.ApprovedBy,
        ApprovedUtc = row.ApprovedUtc is { } approved ? DateTime.SpecifyKind(approved, DateTimeKind.Utc) : null,
        CreatedUtc = DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc),
        DoneUtc = row.DoneUtc is { } done ? DateTime.SpecifyKind(done, DateTimeKind.Utc) : null,
        UpdatedUtc = DateTime.SpecifyKind(row.UpdatedUtc, DateTimeKind.Utc),
    };

    private static string Bounded(string value, int length) => value.Length <= length ? value : value[..length];
}
