using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>What a removal of an inventory's ids came to.</summary>
public sealed record InventoryRemovalOutcome(
    string Operation,
    string Flow,
    string Partition,
    string Inventory,
    long InventoryRemovalId,
    string Finding,
    string Scope,
    long Requested,
    long Removed,
    long Gone,
    long Skipped,
    long Failed,
    string? Error)
{
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"removal {InventoryRemovalId}, {Requested} {Finding} id(s) of '{Inventory}' in '{Partition}', {InventoryRemovals.Describe(Scope)}: {Removed} removed, {Gone} already gone, {Skipped} skipped, {Failed} failed{(Error is null ? "" : $"; stopped: {Error}")}.");
}

/// <summary>A removal that stopped part way: the run ends failed, carrying what the removal did up to there.</summary>
public sealed class InventoryRemovalFailedException : DeliveryException
{
    public InventoryRemovalFailedException(InventoryRemovalOutcome outcome)
        : base((outcome ?? throw new ArgumentNullException(nameof(outcome))).Describe())
    {
        Outcome = outcome;
    }

    public InventoryRemovalOutcome Outcome { get; }
}

public sealed partial class InventoryRunner
{
    /// <summary>The least time between two lines a removal writes of how far it got, so a removal of millions writes a few dozen.</summary>
    private static readonly TimeSpan RemovalProgressEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Removes from OSDU the ids of one finding of one inventory, as an operator asked (osdu/docs/reference/flow/inventory-removals.md,
    /// What a removal does): every id of the finding, those the operator picked, or those a selection holds (the ids that held
    /// what the operator's conditions asked in OSDU); only for a flow whose document allows the finding (and a purge, for a
    /// purge), only in the partition the request confirms, and only while the inventory (or the selection, still matched against
    /// the inventory's last build) holds as many ids of the finding as the operator was shown. Every id is
    /// checked again just before it goes, a chunk at a time: the inventory still finds it so, the ledgers of the partition give
    /// it that finding now, storage still holds it at the version the inventory listed, and an orphan's creator is still an owner.
    /// What passes is removed through the flow's own source, a soft delete 500 ids a request or a purge one at a time; what does
    /// not is skipped with why. Every id's outcome is kept with the removal, the ids removed are marked gone in the inventory, a
    /// stale record's ledger records its removal, and the removal is an activity of the audit trail. A chunk is recorded before
    /// the next is read, so a removal stopped part way keeps what it did, and the inventory then holds what is left. OSDU refusing
    /// a whole chunk for want of permission stops the removal there rather than asking again for every id after it.
    /// <para>
    /// Gone ids, which OSDU no longer serves, are removed from the inventory alone (scope <c>inventory</c>), whatever the flow's
    /// <c>removal</c> declares, since nothing is asked of OSDU: their rows go with the versions kept of them, a round at a time,
    /// and an id that is not gone now, that OSDU serves again, or that a ledger expects stays, skipped with why.
    /// </para>
    /// </summary>
    public async Task<InventoryRemovalOutcome> RemoveAsync(string inventoryName, InventoryRemovalRequest request, string confirm, Guid runId, string actor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inventoryName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var ledger = _context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
        if (InventoryRemovals.Mismatch(request.Finding, request.Scope) is { } mismatch)
        {
            throw new DeliveryException($"A removal of {request.Finding} ids cannot take {request.Scope}: {mismatch} Nothing was removed.");
        }

        if (request.Selection is not null && (request.NamesIds || !InventoryRemovals.IsRemovable(request.Finding)))
        {
            throw new DeliveryException(
                $"A removal takes the {request.Finding} ids of a selection only when it names no ids and they go from OSDU (orphan, stale or forgotten ids). Nothing was removed.");
        }

        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        var inventoryOnly = request.Scope == InventoryRemovals.InventoryOnly;
        var policy = inventoryOnly ? null : Allowed(request);
        if (!string.Equals(confirm?.Trim(), partition, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeliveryException(
                $"The removal confirms partition '{confirm}', and inventory flow '{_flow.Name}' reads '{partition}'. Nothing was removed.");
        }

        var spec = _flow.Inventory(inventoryName)
            ?? throw new DeliveryException($"Inventory flow '{_flow.Name}' has no inventory named '{inventoryName}'. Nothing was removed.");
        var flowId = _flow.LedgerId;
        var state = (await ledger.ListInventoriesAsync(partition, ct).ConfigureAwait(false))
            .FirstOrDefault(i => i.FlowId == flowId && string.Equals(i.Name, spec.Name, StringComparison.OrdinalIgnoreCase));
        if (state?.LastReconcileRunId is null)
        {
            throw new DeliveryException(
                $"Inventory '{spec.Name}' has not been reconciled in '{partition}', so nothing it found can be removed yet; build it first. Nothing was removed.");
        }

        // Who created an id matters only to an orphan's removal from OSDU; a gone id's row is removed whoever created it.
        IReadOnlyList<string> owners = inventoryOnly ? [] : OwnersOf(state.OwnersJson);
        var selection = await HoldsWhatWasShownAsync(ledger, state, request, partition, ct).ConfigureAwait(false);
        var duplicates = selection?.DuplicateCheckId is not null;

        var started = Now;
        var parameters = JsonSerializer.Serialize(
            new
            {
                inventory = spec.Name,
                finding = request.Finding,
                scope = request.Scope,
                expected = request.Expected,
                ids = request.NamesIds ? request.Ids.Count : (int?)null,
                selection = request.Selection,
            },
            Json);
        var activity = await ledger.StartActivityAsync(
            new ActivityRecord
            {
                FlowId = flowId,
                FlowName = _flow.Name,
                Kind = InventoryRemovals.ActivityKind,
                Actor = actor,
                StartedUtc = started,
                RunId = runId,
                ParametersJson = parameters,
            },
            ct).ConfigureAwait(false);
        var removalId = await ledger.StartInventoryRemovalAsync(
            flowId,
            new InventoryRemovalStart(state.InventoryId, runId, actor, request.Finding, request.Scope, request.NamesIds, request.Expected, activity.ActivityId, started, request.Selection),
            ct).ConfigureAwait(false);
        if (policy is null)
        {
            _log.LogInformation(
                "inventory '{Inventory}' in '{Partition}', removal {Removal}: {Expected} {Finding} id(s), {Scope}, as {Actor}",
                spec.Name, partition, removalId, request.Expected, request.Finding, InventoryRemovals.Describe(request.Scope), actor);
        }
        else
        {
            _log.LogInformation(
                "inventory '{Inventory}' in '{Partition}', removal {Removal}: {Expected} {Finding} id(s){Selected}, {Scope}, through {Endpoint}, as {Actor}; the flow allows removing {Allowed}",
                spec.Name, partition, removalId, request.Expected, request.Finding,
                request.Selection is { } selected ? string.Create(CultureInfo.InvariantCulture, $" of selection {selected}") : string.Empty,
                InventoryRemovals.Describe(request.Scope), _flow.Source.Endpoint, actor, string.Join(", ", policy.Findings));
        }

        var tally = InventoryRemovalTally.None;
        string? error = null;
        var cancelled = false;
        try
        {
            tally = inventoryOnly
                ? await RemoveFromInventoryAsync(ledger, state, request, removalId, ct).ConfigureAwait(false)
                : await RemoveChunksAsync(ledger, state, request, owners, duplicates, removalId, runId, actor, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            error = "the run was cancelled; what the removal recorded before it stands";
        }
        catch (RemovalStoppedException stopped)
        {
            tally = stopped.Tally;
            error = stopped.Message;
        }
        catch (Exception ex) when (Expected(ex, ct))
        {
            error = HeaderRedaction.RedactMessage(ex.Message);
        }

        // The removal and its activity are closed whatever happened, so the audit trail never shows one still running.
        if (error is not null)
        {
            var recorded = await ledger.GetInventoryRemovalAsync(partition, removalId, CancellationToken.None).ConfigureAwait(false);
            if (recorded is not null)
            {
                tally = new InventoryRemovalTally(recorded.Removed, recorded.Gone, recorded.Skipped, recorded.Failed);
            }
        }

        var status = error is null ? InventoryRunStatus.Completed : InventoryRunStatus.Failed;
        await ledger.CompleteInventoryRemovalAsync(flowId, removalId, status, error, Now, CancellationToken.None).ConfigureAwait(false);
        var outcome = new InventoryRemovalOutcome(
            DeliveryOperations.Remove, _flow.Name, partition, spec.Name, removalId, request.Finding, request.Scope, request.Expected,
            tally.Removed, tally.Gone, tally.Skipped, tally.Failed, error);
        await ledger.CompleteActivityAsync(activity.ActivityId, cancelled ? "cancelled" : status, outcome.Describe(), null, Now, ct: CancellationToken.None).ConfigureAwait(false);
        _log.LogInformation("{Outcome}", outcome.Describe());
        if (cancelled)
        {
            ct.ThrowIfCancellationRequested();
        }

        return error is null ? outcome : throw new InventoryRemovalFailedException(outcome);
    }

    /// <summary>The flow's removal policy, when it allows what the request asks; refused before anything is read otherwise.</summary>
    private InventoryRemovalPolicy Allowed(InventoryRemovalRequest request)
    {
        var policy = _flow.Removal ?? throw new DeliveryException(
            $"Inventory flow '{_flow.Name}' allows no removal: its document declares no 'removal', so it only reads OSDU. Add removal: {{ findings: [{request.Finding}] }} to the flow to remove what it finds. Nothing was removed.");
        if (!policy.Allows(request.Finding))
        {
            throw new DeliveryException(
                $"Inventory flow '{_flow.Name}' allows removing {string.Join(", ", policy.Findings)} ids, not {request.Finding} ones; its removal.findings names what it may remove. Nothing was removed.");
        }

        if (request.Scope == InventoryRemovals.Purge && !policy.Purge)
        {
            throw new DeliveryException(
                $"Inventory flow '{_flow.Name}' allows soft deletes only; removal.purge: true lets it purge, which destroys every version for good. Nothing was removed.");
        }

        return policy;
    }

    /// <summary>
    /// Holds the inventory to what the operator was shown: every id of the finding, as many as they were told; the ids they
    /// picked, every one of them an id the inventory holds; or the ids of the finding a selection holds, as many as they were
    /// told, the selection's match (or the duplicate check that made it) complete and made against the inventory's last build.
    /// Returns the selection the removal takes its ids from, or null for one that names its ids or takes every id of the finding.
    /// </summary>
    private async Task<InventorySelectionState?> HoldsWhatWasShownAsync(ILedger ledger, InventoryState state, InventoryRemovalRequest request, string partition, CancellationToken ct)
    {
        if (request.Selection is { } selectionId)
        {
            var selection = await ledger.GetInventorySelectionAsync(partition, selectionId, ct).ConfigureAwait(false);
            if (selection is null || selection.InventoryId != state.InventoryId)
            {
                throw new DeliveryException($"Inventory '{state.Name}' holds no selection {selectionId}: it was retired, or made of another inventory. Nothing was removed.");
            }

            if (selection.Status != InventorySelections.Completed)
            {
                throw new DeliveryException(
                    $"Selection {selectionId} of inventory '{state.Name}' is {selection.Status}, not completed: only a match that read OSDU whole says which ids hold what its conditions ask. Nothing was removed.");
            }

            if (selection.BuildRunId != state.LastBuildRunId)
            {
                throw new DeliveryException(selection.DuplicateCheckId is { } check
                    ? $"Selection {selectionId} holds the picks of duplicate check {check}, made against an earlier build of inventory '{state.Name}', and the inventory was built again since: check for duplicates again before removing what it holds. Nothing was removed."
                    : $"Selection {selectionId} was matched against an earlier build of inventory '{state.Name}', and the inventory was built again since: match its conditions again before removing what it holds. Nothing was removed.");
            }

            var selected = (await ledger.InventorySelectionCountsAsync(partition, selectionId, ct).ConfigureAwait(false)).Of(request.Finding);
            if (selected != request.Expected)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Selection {selectionId} of inventory '{state.Name}' holds {selected} {request.Finding} id(s) now, and the removal was asked for {request.Expected}: what it holds changed since it was shown. Nothing was removed; look at it again and ask again."));
            }

            return selection;
        }

        if (request.NamesIds)
        {
            var held = await ledger.InventoryRecordsOfAsync(partition, state.InventoryId, request.Ids, ct).ConfigureAwait(false);
            var unknown = request.Ids.Except(held.Select(r => r.TargetId), StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The removal names {unknown.Count} id(s) inventory '{state.Name}' holds no row of ({string.Join(", ", unknown.Take(5))}{(unknown.Count > 5 ? ", ..." : "")}). Nothing was removed."));
            }

            return null;
        }

        var counts = await ledger.InventoryCountsAsync(_flow.LedgerId, state.InventoryId, ct).ConfigureAwait(false);
        var holds = counts.Of(request.Finding);
        if (holds != request.Expected)
        {
            throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"Inventory '{state.Name}' holds {holds} {request.Finding} id(s) now, and the removal was asked for {request.Expected}: what it holds changed since it was shown. Nothing was removed; look at it again and ask again."));
        }

        return null;
    }

    /// <summary>
    /// Removes the removal's gone ids from the inventory a round at a time, until none is left, each round in one transaction
    /// held against the inventory's merge. Nothing is asked of OSDU, and an id removed keeps no row: the removal keeps how many.
    /// </summary>
    private async Task<InventoryRemovalTally> RemoveFromInventoryAsync(ILedger ledger, InventoryState state, InventoryRemovalRequest request, long removalId, CancellationToken ct)
    {
        var tally = InventoryRemovalTally.None;
        var reported = _context.Time.GetUtcNow();
        long after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var round = await ledger.RemoveFromInventoryAsync(
                _flow.LedgerId, state.InventoryId, removalId, request.NamesIds ? request.Ids : null, after, InventoryRemovals.InventoryChunk, Now, ct).ConfigureAwait(false);
            tally = tally with { Removed = tally.Removed + round.Removed, Skipped = tally.Skipped + round.Skipped };
            if (round.Read < InventoryRemovals.InventoryChunk)
            {
                return tally;
            }

            after = round.Last;
            var now = _context.Time.GetUtcNow();
            if (now - reported >= RemovalProgressEvery)
            {
                reported = now;
                _log.LogInformation("removal {Removal}: {Done} of {Expected} id(s) reached so far: {Tally}", removalId, tally.Total, request.Expected, tally.Describe());
            }
        }
    }

    /// <summary>
    /// Reads, checks, removes and records the removal's ids a chunk at a time, until none is left. A removal by a selection a
    /// duplicate check made (<paramref name="duplicates"/>) also holds each copy to the copy its group keeps.
    /// </summary>
    private async Task<InventoryRemovalTally> RemoveChunksAsync(
        ILedger ledger, InventoryState state, InventoryRemovalRequest request, IReadOnlyList<string> owners, bool duplicates, long removalId, Guid runId, string actor,
        CancellationToken ct)
    {
        using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
        var storage = new OsduRecordProtocol(
            client,
            new ProtocolOptions { DeletePath = _flow.Source.DeletePath, BulkDeletePath = _flow.Source.BulkDeletePath, PurgePath = _flow.Source.PurgePath },
            _context.Time);
        var headers = new StorageHeaders(client, _flow.Source);
        var scope = request.Scope == InventoryRemovals.Purge ? RemovalScope.Everything : RemovalScope.Record;
        var removedDetail = string.Create(CultureInfo.InvariantCulture, $"removed by {actor} in removal {removalId} (run {runId:D}): {InventoryRemovals.Describe(request.Scope)}");
        var goneDetail = string.Create(CultureInfo.InvariantCulture, $"OSDU no longer served it when removal {removalId} reached it");
        var stats = new InventoryReadStats();
        var tally = InventoryRemovalTally.None;
        var reported = _context.Time.GetUtcNow();
        long after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = await ledger.InventoryRemovalCandidatesAsync(
                _flow.LedgerId, state.InventoryId, request.Finding, request.NamesIds ? request.Ids : null, owners, after, InventoryRemovals.Chunk, request.Selection, ct).ConfigureAwait(false);
            if (chunk.Count == 0)
            {
                return tally;
            }

            after = chunk[^1].InventoryRecordId;
            var (items, refusal) = await RemoveChunkAsync(ledger, storage, headers, chunk, request, scope, owners, duplicates, removalId, actor, stats, ct).ConfigureAwait(false);
            await ledger.RecordInventoryRemovalAsync(_flow.LedgerId, state.InventoryId, removalId, items, removedDetail, goneDetail, Now, ct).ConfigureAwait(false);
            tally = tally.Add(items);
            if (refusal is not null)
            {
                throw new RemovalStoppedException(refusal, tally);
            }

            var now = _context.Time.GetUtcNow();
            if (now - reported >= RemovalProgressEvery)
            {
                reported = now;
                _log.LogInformation("removal {Removal}: {Done} of {Expected} id(s) reached so far: {Tally}", removalId, tally.Total, request.Expected, tally.Describe());
            }
        }
    }

    /// <summary>
    /// One chunk: what no longer has the finding (in the inventory, or against the ledgers now) is skipped, what storage no
    /// longer holds is gone, what changed since the inventory listed it or (an orphan) was created by no owner is skipped, and
    /// the rest is removed. A copy a duplicate check picked (<paramref name="duplicates"/>) is skipped too while storage does
    /// not hold the copy its group keeps, so a removal never takes the last copy of a record. Returns every id's outcome, and why
    /// the removal has to stop when OSDU refused the whole chunk for want of permission.
    /// </summary>
    private async Task<(IReadOnlyList<InventoryRemovalItem> Items, string? Refusal)> RemoveChunkAsync(
        ILedger ledger, OsduRecordProtocol storage, StorageHeaders headers, IReadOnlyList<InventoryRemovalCandidate> chunk, InventoryRemovalRequest request,
        RemovalScope scope, IReadOnlyList<string> owners, bool duplicates, long removalId, string actor, InventoryReadStats stats, CancellationToken ct)
    {
        var items = new List<InventoryRemovalItem>(chunk.Count);
        var checking = new List<InventoryRemovalCandidate>(chunk.Count);
        foreach (var candidate in chunk)
        {
            if (candidate.Recorded == InventoryFindings.Gone)
            {
                items.Add(Item(candidate, request.Finding, InventoryRemovals.Gone, "the inventory already finds it gone: OSDU no longer served it at its last build"));
            }
            else if (candidate.Recorded != request.Finding)
            {
                items.Add(Item(candidate, candidate.Recorded, InventoryRemovals.Skipped, $"the inventory finds it {candidate.Recorded} now, not {request.Finding}"));
            }
            else if (candidate.Current != request.Finding)
            {
                items.Add(Item(candidate, candidate.Recorded, InventoryRemovals.Skipped,
                    $"the ledgers make it {candidate.Current} now: something claimed or changed it since the inventory was reconciled"));
            }
            else
            {
                checking.Add(candidate);
            }
        }

        if (checking.Count == 0)
        {
            return (items, null);
        }

        // A copy a duplicate check picked goes only while storage holds the copy its group keeps: both are read in one pass.
        var keepers = duplicates && request.Selection is { } selectionId
            ? (await ledger.InventorySelectionKeepersAsync(_flow.LedgerId, selectionId, checking.Select(c => c.InventoryRecordId).ToList(), ct).ConfigureAwait(false))
                .ToDictionary(k => k.InventoryRecordId)
            : [];
        var served = await headers.ReadAsync(
            checking.Select(c => c.TargetId).Concat(keepers.Values.Select(k => k.KeptTargetId)).Distinct(StringComparer.Ordinal).ToList(), stats, ct).ConfigureAwait(false);
        var removing = new List<InventoryRemovalCandidate>(checking.Count);
        foreach (var candidate in checking)
        {
            if (!served.TryGetValue(candidate.TargetId, out var held))
            {
                items.Add(Item(candidate, candidate.Recorded, InventoryRemovals.Gone, "storage no longer holds it: nothing was asked of OSDU"));
            }
            else if (candidate.Version is { } listed && held.Version is { } now && now != listed)
            {
                items.Add(Item(candidate, candidate.Recorded, InventoryRemovals.Skipped, string.Create(
                    CultureInfo.InvariantCulture,
                    $"OSDU serves version {now}, and the inventory listed version {listed}: it changed since, so it is left for the next build to look at")));
            }
            else if (duplicates && !keepers.ContainsKey(candidate.InventoryRecordId))
            {
                items.Add(Item(candidate, candidate.Recorded, InventoryRemovals.Skipped,
                    "the selection names no copy its group keeps for it, so it is left: a copy goes only beside the copy kept"));
            }
            else if (duplicates && !served.ContainsKey(keepers[candidate.InventoryRecordId].KeptTargetId))
            {
                items.Add(Item(candidate, candidate.Recorded, InventoryRemovals.Skipped,
                    $"storage no longer holds {keepers[candidate.InventoryRecordId].KeptTargetId}, the copy its group keeps, so this copy is left: removing it could leave no copy of the record"));
            }
            else if (request.Finding == InventoryFindings.Orphan && (held.CreateUser is null || !owners.Contains(held.CreateUser, StringComparer.OrdinalIgnoreCase)))
            {
                items.Add(Item(candidate, candidate.Recorded, InventoryRemovals.Skipped,
                    $"storage says {held.CreateUser ?? "no identity"} created it, not an identity this estate writes as"));
            }
            else
            {
                removing.Add(candidate);
            }
        }

        if (removing.Count == 0)
        {
            return (items, null);
        }

        var removals = removing
            .Select(c => new RecordRemoval(c.DeliveryKey is { } key ? new DeliveryKey(key) : default, c.TargetId, null))
            .ToList();
        var results = await storage.DeleteBatchAsync(removals, scope, ct).ConfigureAwait(false);
        var byId = results.GroupBy(r => r.Removal.TargetId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var refused = 0;
        Exception? refusal = null;
        var forbidden = new List<int>();
        var marked = new List<(Guid Flow, DeliveryKey Key)>();
        foreach (var candidate in removing)
        {
            if (!byId.TryGetValue(candidate.TargetId, out var result))
            {
                items.Add(Item(candidate, candidate.Recorded, InventoryRemovals.Failed, "OSDU's answer said nothing of it"));
                continue;
            }

            if (result.Failure is { } failure)
            {
                if (failure is OsduStatusException { StatusCode: 401 or 403 } status)
                {
                    refused++;
                    refusal ??= failure;
                    if (status.StatusCode == 403)
                    {
                        forbidden.Add(items.Count);
                    }
                }

                items.Add(Item(candidate, candidate.Recorded, InventoryRemovals.Failed, HeaderRedaction.RedactMessage(failure.Message)));
                continue;
            }

            if (result.Outcome is { AlreadyGone: true })
            {
                items.Add(Item(candidate, candidate.Recorded, InventoryRemovals.Gone, "storage no longer held it when the removal was sent"));
                continue;
            }

            items.Add(Item(candidate, candidate.Recorded, InventoryRemovals.Removed, result.Outcome?.Detail ?? InventoryRemovals.Describe(request.Scope)));
            if (candidate.Recorded == InventoryFindings.Stale && candidate.LedgerStatus is not null && candidate.LedgerFlowId is { } ledgerFlow && candidate.DeliveryKey is { } deliveryKey)
            {
                marked.Add((ledgerFlow, new DeliveryKey(deliveryKey)));
            }
        }

        // A stale record's own ledger says it was removed: its history gets the attempt that did it, who asked and when. OSDU has
        // removed the ids by now, so a ledger that cannot take the mark (its record purged meanwhile) leaves the ids removed, and
        // their outcome says the mark is missing, rather than losing what the chunk did. The mark asks for nothing more: OSDU
        // now agrees with what the ledger already said, and a request the removal before it left stays as it was.
        foreach (var group in marked.GroupBy(m => m.Flow))
        {
            try
            {
                await ledger.MarkRemovedAsync(
                    group.Key, group.Select(m => m.Key).ToList(), scope, actor, Now, string.Create(CultureInfo.InvariantCulture, $"inventory removal {removalId}"),
                    deliverAgain: false, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DeliveryException or Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                var why = HeaderRedaction.RedactMessage(ex.Message);
                _log.LogWarning("removal {Removal}: the ledger {Flow} could not record the removal of {Count} stale record(s): {Error}", removalId, group.Key, group.Count(), why);
                var keys = group.Select(m => m.Key.Value).ToHashSet();
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i].Outcome == InventoryRemovals.Removed && items[i].DeliveryKey is { } key && keys.Contains(key))
                    {
                        items[i] = items[i] with { Reason = $"{items[i].Reason}; its ledger record could not be marked removed: {why}" };
                    }
                }
            }
        }

        // OSDU took some ids of the chunk and refused others with 403: storage refuses a record so when the caller is no owner of
        // it (its acl.owners), so a record written under another ACL fails every removal until its ACL names the flow's
        // credentials or one of its owners removes it. A chunk refused whole stops the removal below, with its own explanation.
        if (refused < removing.Count)
        {
            foreach (var index in forbidden)
            {
                items[index] = items[index] with
                {
                    Reason = $"OSDU refused it (403): the flow's source credentials are no owner of this record (its acl.owners), so it stays in OSDU until its ACL names them or one of its owners removes it; {items[index].Reason}",
                };
            }
        }

        var stop = refused == removing.Count && refusal is OsduStatusException denied
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"OSDU refused the removal ({denied.StatusCode}) of every id of a chunk: the flow's source credentials must be an owner of the records (users.datalake.editors, and in each record's owners ACL). Nothing more was asked of it.")
            : null;
        return (items, stop);
    }

    private static InventoryRemovalItem Item(InventoryRemovalCandidate candidate, string finding, string outcome, string reason)
        => new(candidate.InventoryRecordId, candidate.TargetId, candidate.Version, finding, outcome, reason, candidate.LedgerFlowId, candidate.DeliveryKey);

    /// <summary>The identities the inventory's last reconcile used as owners, from what it kept of them.</summary>
    private static IReadOnlyList<string> OwnersOf(string? ownersJson)
    {
        if (string.IsNullOrWhiteSpace(ownersJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(ownersJson);
            if (!document.RootElement.TryGetProperty("owners", out var owners) || owners.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return owners.EnumerateArray()
                .Select(o => o.ValueKind == JsonValueKind.Object && o.TryGetProperty("identity", out var identity) ? identity.GetString() : null)
                .OfType<string>()
                .Where(identity => identity.Length > 0)
                .ToList();
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"The owners the inventory's last reconcile kept cannot be read ({ex.Message}); reconcile it again before removing anything.", ex);
        }
    }

    /// <summary>OSDU refused a whole chunk for want of permission: the removal stops, keeping what it recorded.</summary>
    private sealed class RemovalStoppedException(string message, InventoryRemovalTally tally) : DeliveryException(message)
    {
        public InventoryRemovalTally Tally { get; } = tally;
    }
}
