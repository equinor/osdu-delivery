using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// Finding the duplicate records of an inventory (osdu/docs/reference/flow/inventory-duplicates.md): the checks
// an operator's key opens, kept under the inventory's partition with the table each downloads into, every group of ids that
// hold equal values at its key and each copy's figures and role. The control plane opens a check, groups it again by another
// key, asks for a pick, reads it, writes an operator's choice and makes a selection of its picks; the inventory flow's
// duplicates runs download into it and pick.
public sealed partial class InventoryStore
{
    /// <summary>The SQL Server error number the duplicate statements refuse with, carrying their own words.</summary>
    private const int DuplicateRefusal = 51000;

    /// <summary>
    /// Opens a check of an inventory's ids for duplicate records (osdu/docs/reference/flow/inventory-duplicates.md), queued for the
    /// duplicates run of the inventory flow that downloads its properties. Returns the check's number.
    /// </summary>
    public async Task<long> CreateDuplicateCheckAsync(string partition, InventoryDuplicateCheckStart start, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.Actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(start.KeyJson);
        ArgumentNullException.ThrowIfNull(start.Attributes);
        if (start.Attributes.Count is 0 or > InventoryDuplicates.MaxKeyProperties)
        {
            throw new DeliveryException($"A duplicate check downloads one to {InventoryDuplicates.MaxKeyProperties} properties; {start.Attributes.Count} were given.");
        }

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
            AttributesJson = InventoryDuplicateTable.ToJson(start.Attributes),
            KeyedBy = start.Actor.Length <= 200 ? start.Actor : start.Actor[..200],
            KeyedUtc = start.CreatedUtc,
            Status = InventoryDuplicates.Queued,
            BuildRunId = start.BuildRunId,
            CreatedUtc = start.CreatedUtc,
        };
        db.DeliveryInventoryDuplicateChecks.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.InventoryDuplicateCheckId;
    }

    /// <summary>
    /// The newest check of an inventory that still answers for it: one queued or running, or one whose download completed against
    /// the inventory's build <paramref name="buildRunId"/>; null when there is none. A key of the properties it downloaded groups it
    /// again rather than downloading them again.
    /// </summary>
    public async Task<InventoryDuplicateCheckState?> CurrentDuplicateCheckAsync(string partition, int inventoryId, long? buildRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return null;
        }

        return await ReadAsync(
            db => db.DeliveryInventoryDuplicateChecks.AsNoTracking()
                .Where(c => c.PartitionId == id && c.InventoryId == inventoryId
                    && (c.Status == InventoryDuplicates.Queued || c.Status == InventoryDuplicates.Running
                        || (c.Status == InventoryDuplicates.Completed && c.BuildRunId == buildRunId)))
                .OrderByDescending(c => c.InventoryDuplicateCheckId)
                .Select(c => ToDuplicateCheckState(c))
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>One duplicate check by its partition and number, or null.</summary>
    public async Task<InventoryDuplicateCheckState?> GetDuplicateCheckAsync(string partition, long checkId, CancellationToken ct = default)
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

    /// <summary>An inventory's duplicate checks, the newest first.</summary>
    public async Task<IReadOnlyList<InventoryDuplicateCheckState>> ListDuplicateChecksAsync(string partition, int inventoryId, int limit, CancellationToken ct = default)
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

    /// <summary>Names the run a check was queued as.</summary>
    public async Task SetDuplicateCheckRunAsync(string partition, long checkId, Guid runId, CancellationToken ct = default)
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

    /// <summary>
    /// Ends a check whose run never came, or ended before it did, with why (redacted): a download failed, its groups and table
    /// deleted; a pick stopped, the check completed still and its groups not read left to pick. One that ended keeps how it ended.
    /// </summary>
    public async Task FailDuplicateCheckAsync(string partition, long checkId, string why, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return;
        }

        await using var db = Open();
        var row = await db.DeliveryInventoryDuplicateChecks
            .FirstOrDefaultAsync(
                c => c.PartitionId == id && c.InventoryDuplicateCheckId == checkId
                    && (c.Status == InventoryDuplicates.Queued || c.Status == InventoryDuplicates.Running),
                ct).ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        row.Error = RunError(why);
        if (row.Phase == InventoryDuplicates.ReadingCopies)
        {
            // A pick that never came, or stopped, leaves the download and its groups as they are: the groups it did not read wait
            // to be picked again.
            row.Status = InventoryDuplicates.Completed;
            row.Phase = null;
            row.PickedUtc = null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        row.Status = InventoryDuplicates.Failed;
        row.Phase = null;
        row.CompletedUtc = nowUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await DeleteDuplicateGroupsAsync(db, id, checkId, ct).ConfigureAwait(false);
        await SqlServerLedgerBulk.DeleteInventoryDuplicateTableAsync(db, id, checkId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a check's download as the run <paramref name="runId"/>, against the inventory's build <paramref name="buildRunId"/>:
    /// running, downloading, with every group and count an earlier attempt left cleared. Null when the partition holds no such check
    /// of the inventory, or it expired.
    /// </summary>
    public async Task<InventoryDuplicateCheckState?> StartDuplicateCheckAsync(
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
        row.PickedUtc = null;
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

    /// <summary>Notes how many ids the inventory serves for the check to read, and the requests sent so far.</summary>
    public async Task PlanDuplicateCheckAsync(Guid flowId, long checkId, long candidates, long requests, CancellationToken ct = default)
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

    /// <summary>
    /// Makes a check's table afresh (osdu/docs/reference/flow/inventory-duplicates.md, The check's table) through SQLFlow's schema
    /// evolution, with a column for each of <paramref name="attributes"/>' values and for each comparison's hash of them, and
    /// records the table and the properties on the check.
    /// </summary>
    public async Task PrepareDuplicateTableAsync(Guid flowId, long checkId, IReadOnlyList<DuplicateColumn> attributes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count is 0 or > InventoryDuplicates.MaxKeyProperties)
        {
            throw new DeliveryException($"A duplicate check downloads one to {InventoryDuplicates.MaxKeyProperties} properties; {attributes.Count} were given.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        await SqlServerLedgerBulk.PrepareInventoryDuplicateTableAsync(db, partition, checkId, attributes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps a page of a check's download in its table, in one transaction: each record the inventory serves a row of, once, with
    /// the version the index holds and what it holds at each property; how many records the inventory holds no row of, and how
    /// many rows are at another version than it listed; and the check's progress.
    /// </summary>
    public async Task<InventoryDuplicateRowsWrite> AddDuplicateRowsAsync(
        Guid flowId, int inventoryId, long checkId, IReadOnlyList<DuplicateColumn> attributes, IReadOnlyList<InventoryDuplicateRow> rows, long checkedCount,
        long requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(checkedCount);
        ArgumentOutOfRangeException.ThrowIfNegative(requests);
        if (rows.Count > InventoryDuplicates.WriteChunk)
        {
            throw new DeliveryException($"A write of a duplicate check's download keeps at most {InventoryDuplicates.WriteChunk} rows; {rows.Count} were given.");
        }

        if (rows.Any(r => r.Values.Count != attributes.Count))
        {
            throw new DeliveryException($"Each row of a duplicate check's download holds a value for each of its {attributes.Count} properties.");
        }

        if (rows.SelectMany(r => r.Values).OfType<InventoryDuplicateValue>().Any(v => v.Hash is { } hash && hash.Length != InventoryDuplicateTable.HashLength))
        {
            throw new DeliveryException($"A duplicate check keeps each property's values as a SHA-256 of {InventoryDuplicateTable.HashLength} bytes.");
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        return await RetryDeadlockAsync(
            () => SqlServerLedgerBulk.AddInventoryDuplicateRowsAsync(db, partition, inventoryId, checkId, attributes, rows, checkedCount, requests, ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Groups a check's table by a key of properties it downloaded, over whatever it was grouped by before, in SQL and without
    /// asking OSDU: the ids holding equal values at the key numbered into groups, the largest first; a group too large to pick from
    /// left for review; every other group waiting to be picked. The key is recorded with who set it and when.
    /// </summary>
    /// <exception cref="DeliveryException">The key names a property the check did not download, or the check holds no download, or it is picking.</exception>
    public async Task<InventoryDuplicateGrouping> GroupDuplicatesAsync(string partition, long checkId, InventoryDuplicateKeying keying, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(keying);
        ArgumentException.ThrowIfNullOrWhiteSpace(keying.KeyJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(keying.Actor);
        if (keying.KeyHash.Length != DeliveryModel.SelectionKeyLength)
        {
            throw new DeliveryException($"A duplicate check's key hash is a SHA-256 in hex, {DeliveryModel.SelectionKeyLength} characters; '{keying.KeyHash}' is not one.");
        }

        if (keying.Paths.Count is 0 or > InventoryDuplicates.MaxKeyProperties)
        {
            throw new DeliveryException($"A key compares one to {InventoryDuplicates.MaxKeyProperties} properties; {keying.Paths.Count} were given.");
        }

        var id = await PartitionIdOfAsync(partition, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Partition '{partition}' keeps no ledger, so it holds no duplicate check {checkId}.");
        await using var db = Open();
        var check = await db.DeliveryInventoryDuplicateChecks.AsNoTracking()
            .Where(c => c.PartitionId == id && c.InventoryDuplicateCheckId == checkId)
            .Select(c => new { c.AttributesJson, c.TableName })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Partition '{partition}' holds no duplicate check {checkId}.");
        if (check.TableName is null)
        {
            throw new DeliveryException($"Duplicate check {checkId} holds no download to group: it has not downloaded yet, or its download failed or was retired.");
        }

        // The columns a statement names come from the check's own row, read back and checked, never from the key as asked.
        var byPath = InventoryDuplicateTable.Parse(check.AttributesJson).ToDictionary(a => a.Path, StringComparer.Ordinal);
        var keyed = new List<DuplicateColumn>(keying.Paths.Count);
        foreach (var path in keying.Paths)
        {
            keyed.Add(byPath.TryGetValue(path, out var attribute)
                ? attribute
                : throw new DeliveryException(
                    $"Duplicate check {checkId} did not download {path}, so it cannot group by it; check for duplicates by a key holding it, which downloads it."));
        }

        var hashes = keyed.Select(a => InventoryDuplicateTable.HashColumn(a.Column)).ToList();
        var others = byPath.Values.Where(a => !keying.Paths.Contains(a.Path, StringComparer.Ordinal)).ToList();

        try
        {
            return await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.GroupInventoryDuplicatesAsync(db, id, checkId, keyed, others, hashes, keying, InventoryDuplicates.MaxCopiesPicked, SweepBatch, ct),
                ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == DuplicateRefusal)
        {
            throw new DeliveryException($"Duplicate check {checkId} was not grouped again: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Asks for a pick of a completed check whose groups wait to be picked: queued, to read their copies. False when the check is
    /// not completed, is stale against <paramref name="buildRunId"/>, or holds no group to pick.
    /// </summary>
    public async Task<bool> RequestDuplicatePickAsync(string partition, long checkId, long? buildRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return false;
        }

        await using var db = Open();
        var asked = await db.DeliveryInventoryDuplicateChecks
            .Where(c => c.PartitionId == id && c.InventoryDuplicateCheckId == checkId && c.Status == InventoryDuplicates.Completed && c.BuildRunId == buildRunId
                && db.DeliveryInventoryDuplicateGroups.Any(g => g.PartitionId == id && g.InventoryDuplicateCheckId == checkId && g.Outcome == InventoryDuplicates.Pending))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, InventoryDuplicates.Queued)
                .SetProperty(c => c.Phase, InventoryDuplicates.ReadingCopies)
                .SetProperty(c => c.RunId, (Guid?)null)
                .SetProperty(c => c.Error, (string?)null), ct).ConfigureAwait(false);
        return asked > 0;
    }

    /// <summary>Starts a check's pick as the run <paramref name="runId"/>: running, reading copies. Null when the partition holds no such check, or no pick was asked of it.</summary>
    public async Task<InventoryDuplicateCheckState?> StartDuplicatePickAsync(Guid flowId, long checkId, Guid runId, DateTime nowUtc, CancellationToken ct = default)
    {
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var row = await db.DeliveryInventoryDuplicateChecks
            .FirstOrDefaultAsync(c => c.PartitionId == partition && c.InventoryDuplicateCheckId == checkId, ct).ConfigureAwait(false);
        if (row is null || row.Phase != InventoryDuplicates.ReadingCopies || row.Status is not (InventoryDuplicates.Queued or InventoryDuplicates.Running))
        {
            return null;
        }

        row.Status = InventoryDuplicates.Running;
        row.RunId = runId;
        row.Error = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToDuplicateCheckState(row);
    }

    /// <summary>Ends a pick: the check completed again, picked when no error stopped it, or holding why it stopped and the groups it did not read.</summary>
    public async Task CompleteDuplicatePickAsync(Guid flowId, long checkId, InventoryDuplicatePickEnd ending, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ending);
        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        var row = await db.DeliveryInventoryDuplicateChecks
            .FirstOrDefaultAsync(c => c.PartitionId == partition && c.InventoryDuplicateCheckId == checkId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"Duplicate check {checkId} is not in the ledger of {flowId:D}.");
        row.Status = InventoryDuplicates.Completed;
        row.Phase = null;
        row.NotesJson = ending.Notes.Count == 0 ? null : JsonSerializer.Serialize(ending.Notes);
        row.Error = ending.Error is null ? null : RunError(ending.Error);
        row.PickedUtc = ending.Error is null ? nowUtc : null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The next groups whose copies are not read yet, at most <paramref name="limit"/>, after <paramref name="after"/>, each copy with the finding the inventory gives it now.</summary>
    public async Task<IReadOnlyList<InventoryDuplicateGroupRow>> DuplicateGroupsToJudgeAsync(Guid flowId, long checkId, int after, int limit, CancellationToken ct = default)
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

    /// <summary>Writes how a round of groups came out, each copy's figures and role, and moves the check's counts on.</summary>
    public async Task WriteDuplicateJudgementsAsync(
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

    /// <summary>Ends a check's download: completed with its notes, or failed with why, its groups and table deleted.</summary>
    public async Task CompleteDuplicateCheckAsync(Guid flowId, long checkId, InventoryDuplicateCheckEnd outcome, DateTime nowUtc, CancellationToken ct = default)
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
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await DeleteDuplicateGroupsAsync(db, partition, checkId, ct).ConfigureAwait(false);
            await SqlServerLedgerBulk.DeleteInventoryDuplicateTableAsync(db, partition, checkId, ct).ConfigureAwait(false);
            return;
        }

        row.Error = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Only an inventory's newest check is grouped again, and a download asked of a completed check keeps what it downloaded:
        // the tables of the inventory's earlier checks that are not under way are let go now, rather than kept a week. Their
        // groups stay as they were found.
        var superseded = await db.DeliveryInventoryDuplicateChecks.AsNoTracking()
            .Where(c => c.PartitionId == partition && c.InventoryId == row.InventoryId && c.InventoryDuplicateCheckId < checkId && c.TableName != null
                && c.Status != InventoryDuplicates.Queued && c.Status != InventoryDuplicates.Running)
            .Select(c => c.InventoryDuplicateCheckId)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var older in superseded)
        {
            await SqlServerLedgerBulk.DeleteInventoryDuplicateTableAsync(db, partition, older, ct).ConfigureAwait(false);
        }
    }

    /// <summary>How many of a check's groups came out each way.</summary>
    public async Task<IReadOnlyDictionary<string, long>> DuplicateOutcomesAsync(string partition, long checkId, CancellationToken ct = default)
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

    /// <summary>How a check's groups are sized, and the groups waiting to be picked with the copies a pick would read of them.</summary>
    public async Task<InventoryDuplicateSizes> DuplicateSizesAsync(string partition, long checkId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (await PartitionIdOfAsync(partition, ct).ConfigureAwait(false) is not { } id)
        {
            return new InventoryDuplicateSizes([], 0, 0);
        }

        var most = InventoryDuplicates.MaxCopiesPicked;
        var rows = await ReadAsync(
            db => db.DeliveryInventoryDuplicateGroups.AsNoTracking()
                .Where(g => g.PartitionId == id && g.InventoryDuplicateCheckId == checkId)
                .GroupBy(g => g.Copies <= 2 ? 2 : g.Copies <= 10 ? 3 : g.Copies <= most ? 11 : most + 1)
                .Select(s => new { From = s.Key, Groups = s.LongCount(), Copies = s.Sum(g => (long)g.Copies) })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var pending = await ReadAsync(
            db => db.DeliveryInventoryDuplicateGroups.AsNoTracking()
                .Where(g => g.PartitionId == id && g.InventoryDuplicateCheckId == checkId && g.Outcome == InventoryDuplicates.Pending)
                .GroupBy(g => 1)
                .Select(s => new { Groups = s.LongCount(), Copies = s.Sum(g => (long)g.Copies) })
                .FirstOrDefaultAsync(ct),
            ct).ConfigureAwait(false);
        var sizes = new (int From, int? To)[] { (2, 2), (3, 10), (11, most), (most + 1, null) }
            .Select(b => rows.FirstOrDefault(r => r.From == b.From) is { } r
                ? new InventoryDuplicateSize(b.From, b.To, r.Groups, r.Copies)
                : new InventoryDuplicateSize(b.From, b.To, 0, 0))
            .ToList();
        return new InventoryDuplicateSizes(sizes, pending?.Groups ?? 0, pending?.Copies ?? 0);
    }

    /// <summary>
    /// A page of a check's groups, of one outcome or every one, in order after <paramref name="after"/>; each with its copies when
    /// it holds at most <paramref name="copiesPerGroup"/>, every copy beside the inventory's row of it now.
    /// </summary>
    public async Task<IReadOnlyList<InventoryDuplicateGroupPage>> ListDuplicateGroupsAsync(
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

    /// <summary>A page of one group's copies in the inventory's order after <paramref name="after"/>.</summary>
    public async Task<IReadOnlyList<InventoryDuplicateMemberState>> ListDuplicateMembersAsync(
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

    /// <summary>One group of a check, or null.</summary>
    public async Task<InventoryDuplicateGroupState?> GetDuplicateGroupAsync(string partition, long checkId, int groupNumber, CancellationToken ct = default)
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

    /// <summary>
    /// Writes an operator's choice of the copy a group keeps (<see cref="DuplicateChoice.Choose"/>, recorded with
    /// <paramref name="actor"/> and when), or a choice taken back (<see cref="DuplicateChoice.Clear"/>), over the group as it stood;
    /// the check's counts move by the difference.
    /// </summary>
    public async Task WriteDuplicateChoiceAsync(
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

    /// <summary>
    /// Makes a selection of the copies a completed check picked to remove, against the build the check read, each id beside the
    /// copy its group keeps, which a removal by the selection checks storage still holds first. Returns the selection's number.
    /// </summary>
    public async Task<long> CreateDuplicateSelectionAsync(
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

    /// <summary>The copies a selection a duplicate check made keeps beside the ids named, each with the copy its group keeps; none for a selection a match filled.</summary>
    public async Task<IReadOnlyList<InventorySelectionKeeper>> SelectionKeepersAsync(
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

    /// <summary>
    /// Retires a partition's checks made before <paramref name="olderThanUtc"/>: deleted whole with their tables, or, when a removal
    /// took the picks of a selection made of them, kept as expired with their groups and tables deleted.
    /// </summary>
    public async Task<InventoryDuplicateSweep> SweepDuplicateChecksAsync(string partition, DateTime olderThanUtc, CancellationToken ct = default)
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
                await SqlServerLedgerBulk.DeleteInventoryDuplicateTableAsync(db, id, checkId, ct).ConfigureAwait(false);
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
        AttributesJson = c.AttributesJson,
        TableName = c.TableName,
        KeyedBy = c.KeyedBy,
        KeyedUtc = c.KeyedUtc is { } keyed ? DateTime.SpecifyKind(keyed, DateTimeKind.Utc) : null,
        PickedUtc = c.PickedUtc is { } picked ? DateTime.SpecifyKind(picked, DateTimeKind.Utc) : null,
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
