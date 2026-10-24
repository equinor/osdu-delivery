using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Finding the duplicate records of an inventory (osdu/docs/reference/flow/inventory-duplicates.md): the checks
// an operator's key opens, kept under the inventory's partition with every group of ids that share a key and each copy's
// figures and role. The control plane opens a check, reads it, writes an operator's choice and makes a selection of its picks;
// the inventory flow's duplicates run fills it.
public sealed partial class OsduLedger
{
    /// <summary>The SQL Server error number the duplicate statements refuse with, carrying their own words.</summary>
    private const int DuplicateRefusal = 51000;

    public async Task<long> CreateInventoryDuplicateCheckAsync(string partition, InventoryDuplicateCheckStart start, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.KeyJson);
        if (start.KeyHash.Length != DeliveryModel.SelectionKeyLength)
        {
            throw new DeliveryException($"A duplicate check's key hash is a SHA-256 in hex, {DeliveryModel.SelectionKeyLength} characters; '{start.KeyHash}' is not one.");
        }

        var id = await PartitionIdOfAsync(partition, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Partition '{partition}' keeps no ledger, so it holds no inventory to check.");
        await using var db = Open();
        var row = new DeliveryInventoryDuplicateCheck
        {
            PartitionId = id,
            InventoryId = start.InventoryId,
            Actor = start.Actor.Length <= 200 ? start.Actor : start.Actor[..200],
            KeyJson = start.KeyJson,
            KeyHash = start.KeyHash,
            Status = InventoryDuplicates.Queued,
            BuildRunId = start.BuildRunId,
            CreatedUtc = start.CreatedUtc,
        };
        db.DeliveryInventoryDuplicateChecks.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.InventoryDuplicateCheckId;
    }

    public async Task<InventoryDuplicateCheckState?> FindInventoryDuplicateCheckAsync(string partition, int inventoryId, string keyHash, long? buildRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHash);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return null;
        }

        return await ReadAsync(
            db => db.DeliveryInventoryDuplicateChecks.AsNoTracking()
                .Where(c => c.PartitionId == id && c.InventoryId == inventoryId && c.KeyHash == keyHash
                    && (c.Status == InventoryDuplicates.Queued || c.Status == InventoryDuplicates.Running
                        || (c.Status == InventoryDuplicates.Completed && c.BuildRunId == buildRunId)))
                .OrderByDescending(c => c.InventoryDuplicateCheckId)
                .Select(c => ToDuplicateCheckState(c))
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<InventoryDuplicateCheckState?> GetInventoryDuplicateCheckAsync(string partition, long checkId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return null;
        }

        return await ReadAsync(
            db => db.DeliveryInventoryDuplicateChecks.AsNoTracking()
                .Where(c => c.PartitionId == id && c.InventoryDuplicateCheckId == checkId)
                .Select(c => ToDuplicateCheckState(c))
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryDuplicateCheckState>> ListInventoryDuplicateChecksAsync(string partition, int inventoryId, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        return await ReadAsync<IReadOnlyList<InventoryDuplicateCheckState>>(
            async db => await db.DeliveryInventoryDuplicateChecks.AsNoTracking()
                .Where(c => c.PartitionId == id && c.InventoryId == inventoryId)
                .OrderByDescending(c => c.InventoryDuplicateCheckId)
                .Take(limit)
                .Select(c => ToDuplicateCheckState(c))
                .ToListAsync(ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    public async Task SetInventoryDuplicateCheckRunAsync(string partition, long checkId, Guid runId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var id = await PartitionIdOfAsync(partition, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Partition '{partition}' keeps no ledger, so it holds no duplicate check {checkId}.");
        await using var db = Open();
        // A check that started already names its run itself; only a queued one is named here.
        await db.DeliveryInventoryDuplicateChecks
            .Where(c => c.PartitionId == id && c.InventoryDuplicateCheckId == checkId && c.RunId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RunId, runId), ct).ConfigureAwait(false);
    }

    public async Task FailInventoryDuplicateCheckAsync(string partition, long checkId, string why, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return;
        }

        await using var db = Open();
        var error = RunError(why);
        var failed = await db.DeliveryInventoryDuplicateChecks
            .Where(c => c.PartitionId == id && c.InventoryDuplicateCheckId == checkId
                && (c.Status == InventoryDuplicates.Queued || c.Status == InventoryDuplicates.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, InventoryDuplicates.Failed)
                .SetProperty(c => c.Phase, (string?)null)
                .SetProperty(c => c.CompletedUtc, nowUtc)
                .SetProperty(c => c.Error, error), ct).ConfigureAwait(false);
        if (failed > 0)
        {
            await DeleteDuplicateGroupsAsync(db, id, checkId, ct).ConfigureAwait(false);
        }
    }

    public async Task<InventoryDuplicateCheckState?> StartInventoryDuplicateCheckAsync(
        Guid flowId, int inventoryId, long checkId, Guid runId, long? buildRunId, DateTime nowUtc, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var row = await db.DeliveryInventoryDuplicateChecks
            .FirstOrDefaultAsync(c => c.PartitionId == partition && c.InventoryDuplicateCheckId == checkId && c.InventoryId == inventoryId, ct).ConfigureAwait(false);
        if (row is null || row.Status == InventoryDuplicates.Expired)
        {
            return null;
        }

        // A run tried again starts its check afresh: what an attempt before it found is cleared with its counts.
        await DeleteDuplicateGroupsAsync(db, partition, checkId, ct).ConfigureAwait(false);
        row.Status = InventoryDuplicates.Running;
        row.Phase = InventoryDuplicates.ReadingKeys;
        row.RunId = runId;
        row.BuildRunId = buildRunId;
        row.StartedUtc = nowUtc;
        row.CompletedUtc = null;
        row.Error = null;
        row.NotesJson = null;
        row.Candidates = 0;
        row.Checked = 0;
        row.Keyed = 0;
        row.Unkeyed = 0;
        row.Outside = 0;
        row.Changed = 0;
        row.Groups = 0;
        row.Copies = 0;
        row.GroupsJudged = 0;
        row.Decided = 0;
        row.Review = 0;
        row.ToRemove = 0;
        row.Requests = 0;
        row.SelectionId = null;
        row.ChosenUtc = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToDuplicateCheckState(row);
    }

    public async Task PlanInventoryDuplicateCheckAsync(Guid flowId, long checkId, long candidates, long requests, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(candidates);
        ArgumentOutOfRangeException.ThrowIfNegative(requests);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await db.DeliveryInventoryDuplicateChecks
            .Where(c => c.PartitionId == partition && c.InventoryDuplicateCheckId == checkId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Candidates, candidates)
                .SetProperty(c => c.Requests, c => c.Requests + requests), ct).ConfigureAwait(false);
    }

    public async Task<InventoryDuplicateKeyWrite> AddInventoryDuplicateKeysAsync(
        Guid flowId, int inventoryId, long checkId, IReadOnlyList<InventoryDuplicateKeyHit> hits, long checkedCount, long requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentOutOfRangeException.ThrowIfNegative(checkedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(requests);
        if (hits.Count > InventoryDuplicates.WriteChunk)
        {
            throw new DeliveryException($"A write of a duplicate check keeps at most {InventoryDuplicates.WriteChunk} keys; {hits.Count} were given.");
        }

        if (hits.Any(h => h.KeyHash is { } hash && hash.Length != DeliveryModel.DuplicateKeyHashLength))
        {
            throw new DeliveryException($"A duplicate check keeps each key as a SHA-256 of {DeliveryModel.DuplicateKeyHashLength} bytes.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await RetryDeadlockAsync(
            () => SqlServerLedgerBulk.AddInventoryDuplicateKeysAsync(db, partition, inventoryId, checkId, hits, checkedCount, requests, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<InventoryDuplicateGrouping> GroupInventoryDuplicatesAsync(Guid flowId, long checkId, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await RetryDeadlockAsync(
            () => SqlServerLedgerBulk.GroupInventoryDuplicatesAsync(db, partition, checkId, InventoryDuplicates.MaxCopiesPicked, SweepBatch, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryDuplicateGroupRow>> InventoryDuplicateGroupsToJudgeAsync(Guid flowId, long checkId, int after, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        return await ReadAsync<IReadOnlyList<InventoryDuplicateGroupRow>>(
            async db =>
            {
                var numbers = await db.DeliveryInventoryDuplicateGroups.AsNoTracking()
                    .Where(g => g.PartitionId == partition && g.InventoryDuplicateCheckId == checkId && g.Outcome == InventoryDuplicates.Pending && g.GroupNumber > after)
                    .OrderBy(g => g.GroupNumber)
                    .Take(limit)
                    .Select(g => g.GroupNumber)
                    .ToListAsync(ct).ConfigureAwait(false);
                if (numbers.Count == 0)
                {
                    return [];
                }

                var first = numbers[0];
                var last = numbers[^1];
                var copies = await (from m in db.DeliveryInventoryDuplicateMembers.AsNoTracking()
                                    join r in db.DeliveryInventoryRecords.AsNoTracking() on new { m.PartitionId, m.InventoryRecordId } equals new { r.PartitionId, r.InventoryRecordId }
                                    where m.PartitionId == partition && m.InventoryDuplicateCheckId == checkId && m.GroupNumber >= first && m.GroupNumber <= last
                                    orderby m.GroupNumber, m.InventoryRecordId
                                    select new { GroupNumber = m.GroupNumber!.Value, r.InventoryRecordId, r.TargetId, r.Finding, r.Version, r.GoneUtc, r.FirstSeenUtc })
                    .ToListAsync(ct).ConfigureAwait(false);
                var asked = numbers.ToHashSet();
                return copies
                    .Where(c => asked.Contains(c.GroupNumber))
                    .GroupBy(c => c.GroupNumber)
                    .Select(g => new InventoryDuplicateGroupRow(
                        g.Key,
                        g.Select(c => new InventoryDuplicateCopyRow(
                            c.InventoryRecordId, c.TargetId, c.GoneUtc is null && c.FirstSeenUtc is not null ? c.Finding : InventoryFindings.Gone, c.Version)).ToList()))
                    .ToList();
            },
            ct).ConfigureAwait(false);
    }

    public async Task WriteInventoryDuplicateJudgementsAsync(
        Guid flowId, long checkId, IReadOnlyList<InventoryDuplicateJudgement> judgements, long requests, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(judgements);
        ArgumentOutOfRangeException.ThrowIfNegative(requests);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await RetryDeadlockAsync(
            () => WriteJudgementsAsync(db, partition, checkId, judgements, requests, DuplicateChoice.None, null, nowUtc, ct),
            ct).ConfigureAwait(false);
    }

    public async Task CompleteInventoryDuplicateCheckAsync(Guid flowId, long checkId, InventoryDuplicateCheckEnd outcome, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Status is not (InventoryDuplicates.Completed or InventoryDuplicates.Failed))
        {
            throw new DeliveryException($"A duplicate check ends {InventoryDuplicates.Completed} or {InventoryDuplicates.Failed}, not '{outcome.Status}'.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var row = await db.DeliveryInventoryDuplicateChecks
            .FirstOrDefaultAsync(c => c.PartitionId == partition && c.InventoryDuplicateCheckId == checkId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Duplicate check {checkId} is not in the ledger of {flowId:D}.");
        row.Status = outcome.Status;
        row.Phase = null;
        row.CompletedUtc = nowUtc;
        row.NotesJson = outcome.Notes.Count == 0 ? null : JsonSerializer.Serialize(outcome.Notes);
        if (outcome.Status == InventoryDuplicates.Failed)
        {
            // A failed check holds no group: what it found before it stopped is not every set of ids that share the key.
            row.Error = RunError(outcome.Error ?? "the check stopped without saying why");
            row.Groups = 0;
            row.Copies = 0;
            row.GroupsJudged = 0;
            row.Decided = 0;
            row.Review = 0;
            row.ToRemove = 0;
            await DeleteDuplicateGroupsAsync(db, partition, checkId, ct).ConfigureAwait(false);
        }
        else
        {
            row.Error = null;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, long>> InventoryDuplicateOutcomesAsync(string partition, long checkId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return new Dictionary<string, long>(StringComparer.Ordinal);
        }

        var rows = await ReadAsync(
            db => db.DeliveryInventoryDuplicateGroups.AsNoTracking()
                .Where(g => g.PartitionId == id && g.InventoryDuplicateCheckId == checkId)
                .GroupBy(g => g.Outcome)
                .Select(g => new { Outcome = g.Key, Count = g.LongCount() })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Outcome, r => r.Count, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<InventoryDuplicateGroupPage>> ListInventoryDuplicateGroupsAsync(
        string partition, long checkId, string? outcome, int? after, int limit, int copiesPerGroup, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(copiesPerGroup);
        if (outcome is not null && !InventoryDuplicates.Outcomes.Contains(outcome, StringComparer.Ordinal))
        {
            throw new DeliveryException($"'{outcome}' is not how a group of duplicates comes out; a group is {string.Join(", ", InventoryDuplicates.Outcomes)}.");
        }

        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        var start = after ?? 0;
        return await ReadAsync<IReadOnlyList<InventoryDuplicateGroupPage>>(
            async db =>
            {
                var groups = await db.DeliveryInventoryDuplicateGroups.AsNoTracking()
                    .Where(g => g.PartitionId == id && g.InventoryDuplicateCheckId == checkId && g.GroupNumber > start && (outcome == null || g.Outcome == outcome))
                    .OrderBy(g => g.GroupNumber)
                    .Take(limit)
                    .ToListAsync(ct).ConfigureAwait(false);
                if (groups.Count == 0)
                {
                    return [];
                }

                var shown = copiesPerGroup == 0
                    ? []
                    : groups.Where(g => g.Copies <= copiesPerGroup).Select(g => g.GroupNumber).ToList();
                var members = shown.Count == 0
                    ? []
                    : await MembersQuery(db, id, checkId)
                        .Where(x => shown.Contains(x.Member.GroupNumber!.Value))
                        .OrderBy(x => x.Member.GroupNumber).ThenBy(x => x.Member.InventoryRecordId)
                        .ToListAsync(ct).ConfigureAwait(false);
                var byGroup = members.ToLookup(m => m.Member.GroupNumber!.Value);
                return groups
                    .Select(g => new InventoryDuplicateGroupPage(ToDuplicateGroupState(g), byGroup[g.GroupNumber].Select(ToDuplicateMemberState).ToList()))
                    .ToList();
            },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryDuplicateMemberState>> ListInventoryDuplicateMembersAsync(
        string partition, long checkId, int groupNumber, long? after, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return [];
        }

        var start = after ?? 0;
        var rows = await ReadAsync(
            db => MembersQuery(db, id, checkId)
                .Where(x => x.Member.GroupNumber == groupNumber && x.Member.InventoryRecordId > start)
                .OrderBy(x => x.Member.InventoryRecordId)
                .Take(limit)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        return rows.Select(ToDuplicateMemberState).ToList();
    }

    public async Task<InventoryDuplicateGroupState?> GetInventoryDuplicateGroupAsync(string partition, long checkId, int groupNumber, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return null;
        }

        var row = await ReadAsync(
            db => db.DeliveryInventoryDuplicateGroups.AsNoTracking()
                .FirstOrDefaultAsync(g => g.PartitionId == id && g.InventoryDuplicateCheckId == checkId && g.GroupNumber == groupNumber, ct),
            ct).ConfigureAwait(false);
        return row is null ? null : ToDuplicateGroupState(row);
    }

    public async Task WriteInventoryDuplicateChoiceAsync(
        string partition, long checkId, InventoryDuplicateJudgement judgement, DuplicateChoice choice, string actor, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(judgement);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (choice == DuplicateChoice.None)
        {
            throw new DeliveryException("An operator's choice of the copy a group keeps either chooses one or takes a choice back.");
        }

        var id = await PartitionIdOfAsync(partition, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Partition '{partition}' keeps no ledger, so it holds no duplicate check {checkId}.");
        await using var db = Open();
        await RetryDeadlockAsync(
            () => WriteJudgementsAsync(db, id, checkId, [judgement], 0, choice, choice == DuplicateChoice.Choose ? actor : null, nowUtc, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<long> CreateInventoryDuplicateSelectionAsync(
        string partition, long checkId, string actor, string filtersJson, string? columnsJson, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(filtersJson);
        var id = await PartitionIdOfAsync(partition, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Partition '{partition}' keeps no ledger, so it holds no duplicate check {checkId}.");

        // A selection made of a check's picks is never one a match made of conditions: its key names the check, which no
        // conditions' hash can be, and a lookup of conditions passes over every selection a check made.
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"duplicate-check\n{checkId}\n{nowUtc:O}")));
        await using var db = Open();
        try
        {
            return await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.CreateInventoryDuplicateSelectionAsync(db, id, checkId, actor, key, filtersJson, columnsJson, nowUtc, ct),
                ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == DuplicateRefusal)
        {
            throw new DeliveryException($"No selection was made of duplicate check {checkId}: {ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyList<InventorySelectionKeeper>> InventorySelectionKeepersAsync(
        Guid flowId, long selectionId, IReadOnlyList<long> inventoryRecordIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inventoryRecordIds);
        if (inventoryRecordIds.Count == 0)
        {
            return [];
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var asked = inventoryRecordIds.Distinct().ToList();
        return await ReadAsync<IReadOnlyList<InventorySelectionKeeper>>(
            async db => await (from s in db.DeliveryInventorySelectionItems.AsNoTracking()
                               join r in db.DeliveryInventoryRecords.AsNoTracking() on new { s.PartitionId, InventoryRecordId = s.KeptInventoryRecordId!.Value } equals new { r.PartitionId, r.InventoryRecordId }
                               where s.PartitionId == partition && s.InventorySelectionId == selectionId && s.KeptInventoryRecordId != null && asked.Contains(s.InventoryRecordId)
                               select new InventorySelectionKeeper(s.InventoryRecordId, r.InventoryRecordId, r.TargetId))
                .ToListAsync(ct).ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    public async Task<InventoryDuplicateSweep> SweepInventoryDuplicateChecksAsync(string partition, DateTime olderThanUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return new InventoryDuplicateSweep(0, 0);
        }

        await using var db = Open();
        var old = await db.DeliveryInventoryDuplicateChecks.AsNoTracking()
            .Where(c => c.PartitionId == id && c.CreatedUtc < olderThanUtc && c.Status != InventoryDuplicates.Expired)
            .Select(c => c.InventoryDuplicateCheckId)
            .ToListAsync(ct).ConfigureAwait(false);
        if (old.Count == 0)
        {
            return new InventoryDuplicateSweep(0, 0);
        }

        var deleted = 0;
        var expired = 0;
        foreach (var chunk in old.Chunk(InventoryRemovals.Chunk))
        {
            // A check a removal took the picks of is kept with its key and counts, so the removal still says what chose its ids.
            var kept = await (from s in db.DeliveryInventorySelections.AsNoTracking()
                              join r in db.DeliveryInventoryRemovals.AsNoTracking() on new { s.PartitionId, SelectionId = (long?)s.InventorySelectionId } equals new { r.PartitionId, SelectionId = r.InventorySelectionId }
                              where s.PartitionId == id && s.DuplicateCheckId != null && chunk.Contains(s.DuplicateCheckId.Value)
                              select s.DuplicateCheckId!.Value)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var checkId in chunk)
            {
                await DeleteDuplicateGroupsAsync(db, id, checkId, ct).ConfigureAwait(false);
            }

            expired += await db.DeliveryInventoryDuplicateChecks
                .Where(c => c.PartitionId == id && kept.Contains(c.InventoryDuplicateCheckId))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, InventoryDuplicates.Expired), ct).ConfigureAwait(false);
            var gone = chunk.Except(kept).ToList();
            deleted += await db.DeliveryInventoryDuplicateChecks
                .Where(c => c.PartitionId == id && gone.Contains(c.InventoryDuplicateCheckId))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        return new InventoryDuplicateSweep(deleted, expired);
    }

    private static async Task<int> WriteJudgementsAsync(
        OsduDbContext db, short partition, long checkId, IReadOnlyList<InventoryDuplicateJudgement> judgements, long requests, DuplicateChoice choice, string? chosenBy,
        DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            return await SqlServerLedgerBulk.WriteInventoryDuplicateJudgementsAsync(db, partition, checkId, judgements, requests, choice, chosenBy, nowUtc, ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == DuplicateRefusal)
        {
            throw new DeliveryException($"Duplicate check {checkId} did not take the judgement: {ex.Message}", ex);
        }
    }

    /// <summary>A check's members, each beside the inventory's row of it now.</summary>
    private static IQueryable<DuplicateMemberRow> MembersQuery(OsduDbContext db, short partition, long checkId)
        => from m in db.DeliveryInventoryDuplicateMembers.AsNoTracking()
           join r in db.DeliveryInventoryRecords.AsNoTracking() on new { m.PartitionId, m.InventoryRecordId } equals new { r.PartitionId, r.InventoryRecordId }
           where m.PartitionId == partition && m.InventoryDuplicateCheckId == checkId && m.GroupNumber != null
           select new DuplicateMemberRow { Member = m, Record = r };

    /// <summary>Deletes a check's groups and members a batch at a time, so a check of hundreds of thousands never holds one long lock.</summary>
    private static async Task DeleteDuplicateGroupsAsync(OsduDbContext db, short partition, long checkId, CancellationToken ct)
    {
        while (await db.DeliveryInventoryDuplicateMembers
                   .Where(m => m.PartitionId == partition && m.InventoryDuplicateCheckId == checkId)
                   .OrderBy(m => m.InventoryRecordId)
                   .Take(SweepBatch)
                   .ExecuteDeleteAsync(ct).ConfigureAwait(false) == SweepBatch)
        {
        }

        while (await db.DeliveryInventoryDuplicateGroups
                   .Where(g => g.PartitionId == partition && g.InventoryDuplicateCheckId == checkId)
                   .OrderBy(g => g.GroupNumber)
                   .Take(SweepBatch)
                   .ExecuteDeleteAsync(ct).ConfigureAwait(false) == SweepBatch)
        {
        }
    }

    private static InventoryDuplicateCheckState ToDuplicateCheckState(DeliveryInventoryDuplicateCheck c) => new()
    {
        InventoryDuplicateCheckId = c.InventoryDuplicateCheckId,
        InventoryId = c.InventoryId,
        RunId = c.RunId,
        Actor = c.Actor,
        KeyJson = c.KeyJson,
        KeyHash = c.KeyHash,
        Status = c.Status,
        Phase = c.Phase,
        NotesJson = c.NotesJson,
        BuildRunId = c.BuildRunId,
        Candidates = c.Candidates,
        Checked = c.Checked,
        Keyed = c.Keyed,
        Unkeyed = c.Unkeyed,
        Outside = c.Outside,
        Changed = c.Changed,
        Groups = c.Groups,
        Copies = c.Copies,
        GroupsJudged = c.GroupsJudged,
        Decided = c.Decided,
        Review = c.Review,
        ToRemove = c.ToRemove,
        Requests = c.Requests,
        SelectionId = c.SelectionId,
        ChosenUtc = c.ChosenUtc is { } chosen ? DateTime.SpecifyKind(chosen, DateTimeKind.Utc) : null,
        CreatedUtc = DateTime.SpecifyKind(c.CreatedUtc, DateTimeKind.Utc),
        StartedUtc = c.StartedUtc is { } started ? DateTime.SpecifyKind(started, DateTimeKind.Utc) : null,
        CompletedUtc = c.CompletedUtc is { } completed ? DateTime.SpecifyKind(completed, DateTimeKind.Utc) : null,
        Error = c.Error,
    };

    private static InventoryDuplicateGroupState ToDuplicateGroupState(DeliveryInventoryDuplicateGroup g)
        => new(g.GroupNumber, g.Copies, g.ValuesJson, g.Outcome, g.Reason, g.KeptInventoryRecordId, g.ChosenBy,
            g.ChosenUtc is { } chosen ? DateTime.SpecifyKind(chosen, DateTimeKind.Utc) : null);

    private static InventoryDuplicateMemberState ToDuplicateMemberState(DuplicateMemberRow x)
        => new(ToInventoryRecordState(x.Record), x.Member.GroupNumber ?? 0, x.Member.IndexedVersion, x.Member.ValuesJson, x.Member.StorageState,
            x.Member.StoredVersion, x.Member.Attributes, x.Member.MentionedBy, x.Member.Role, x.Member.Reason);

    /// <summary>A member of a check beside the inventory's row of it: a class with settable members, so a query composes over it.</summary>
    private sealed class DuplicateMemberRow
    {
        public required DeliveryInventoryDuplicateMember Member { get; init; }

        public required DeliveryInventoryRecord Record { get; init; }
    }
}
