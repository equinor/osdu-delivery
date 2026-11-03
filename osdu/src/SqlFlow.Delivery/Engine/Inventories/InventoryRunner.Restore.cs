using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Inventories;

public sealed partial class InventoryRunner
{
    /// <summary>
    /// Brings back in OSDU what an inventory finds gone (osdu/docs/reference/flow/inventory-removals.md, Restoring gone ids): every
    /// gone id, those the operator picked, or the ids one soft-delete removal of the inventory removed, whether or not the inventory
    /// still holds a row of them. A soft delete keeps the record and every version, and storage's merge patch of
    /// <c>{"deleted": false}</c> serves it again at the version it had, with no new version, one id a request through the flow's
    /// own source (its <c>purgePath</c>, storage's record resource). Only for a flow whose document declares <c>removal</c>, since a
    /// restore writes to OSDU as a removal does; only in the partition the request confirms; and only while the inventory (or the
    /// removal) holds as many ids as the operator was shown.
    /// <para>
    /// Every id is checked again just before it goes: the inventory still finds it gone; no ledger of the partition knows it (a
    /// record a ledger holds comes back through its own flow, which records it); and, for a gone id picked or taken with every one,
    /// an identity this estate writes as created it, so a restore brings back only what this estate wrote. What passes is asked
    /// of storage; one it holds no record of (purged) is gone for good, one it serves already is skipped. Every id's outcome is kept
    /// with the restore, the ids brought back are served again in the inventory at once with the finding the reconcile's rule gives
    /// them, and the restore is an activity of the audit trail, kept with the removals as the undoing of one. A round is recorded
    /// before the next is read, and OSDU refusing a whole round for want of permission stops the restore there.
    /// </para>
    /// </summary>
    public async Task<InventoryRemovalOutcome> RestoreAsync(string inventoryName, InventoryRestoreRequest request, string confirm, Guid runId, string actor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inventoryName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var ledger = _context.Ledger ?? throw new DeliveryException(DeliveryServices.NoLedgerMessage);
        if (request.NamesIds && request.Removal is not null)
        {
            throw new DeliveryException("A restore takes the ids it names or what one removal removed, not both. Nothing was restored.");
        }

        var partition = await PartitionAsync(ct).ConfigureAwait(false);
        if (_flow.Removal is null)
        {
            throw new DeliveryException(
                $"Inventory flow '{_flow.Name}' allows no restore: its document declares no 'removal', so it only reads OSDU, and a restore writes to OSDU as a removal does. Add removal: {{ findings: [orphan] }} to the flow to let it remove and restore. Nothing was restored.");
        }

        if (!string.Equals(confirm?.Trim(), partition, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeliveryException(
                $"The restore confirms partition '{confirm}', and inventory flow '{_flow.Name}' reads '{partition}'. Nothing was restored.");
        }

        var spec = _flow.Inventory(inventoryName)
            ?? throw new DeliveryException($"Inventory flow '{_flow.Name}' has no inventory named '{inventoryName}'. Nothing was restored.");
        var state = (await ledger.ListInventoriesAsync(partition, ct).ConfigureAwait(false))
            .FirstOrDefault(i => i.FlowId == _flow.LedgerId && string.Equals(i.Name, spec.Name, StringComparison.OrdinalIgnoreCase));
        if (state?.LastReconcileRunId is null)
        {
            throw new DeliveryException(
                $"Inventory '{spec.Name}' has not been reconciled in '{partition}', so it finds nothing gone to restore yet; build it first. Nothing was restored.");
        }

        await HoldsWhatWasShownAsync(ledger, state, request, partition, ct).ConfigureAwait(false);
        var owners = OwnersOf(state.OwnersJson);
        var parameters = JsonSerializer.Serialize(
            new
            {
                inventory = spec.Name,
                expected = request.Expected,
                ids = request.NamesIds ? request.Ids.Count : (int?)null,
                removal = request.Removal,
            },
            Json);
        return await TrackedAsync(
            ledger,
            DeliveryOperations.Restore,
            InventoryRemovals.RestoreActivityKind,
            spec,
            partition,
            new InventoryRemovalStart(
                state.InventoryId, runId, actor, InventoryFindings.Gone, InventoryRemovals.Restore, request.NamesIds, request.Expected, null, Now,
                RestoresRemovalId: request.Removal),
            parameters,
            restoreId =>
            {
                _log.LogInformation(
                    "inventory '{Inventory}' in '{Partition}', restore {Restore}: {Expected} gone id(s){Of}, through {Endpoint}, as {Actor}",
                    spec.Name, partition, restoreId, request.Expected,
                    request.Removal is { } removal ? string.Create(CultureInfo.InvariantCulture, $" that removal {removal} removed") : string.Empty,
                    _flow.Source.Endpoint, actor);
                return RestoreRoundsAsync(ledger, state, request, owners, restoreId, runId, actor, ct);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Holds the inventory to what the operator was shown before a restore asks anything of OSDU: what one soft-delete removal of
    /// the inventory removed, as many as they were told, the removal ended; every id they picked, an id the inventory holds; or
    /// every gone id, as many as they were told.
    /// </summary>
    private async Task HoldsWhatWasShownAsync(ILedger ledger, InventoryState state, InventoryRestoreRequest request, string partition, CancellationToken ct)
    {
        if (request.Removal is { } removalId)
        {
            var removal = await ledger.GetInventoryRemovalAsync(partition, removalId, ct).ConfigureAwait(false);
            if (removal is null || removal.InventoryId != state.InventoryId)
            {
                throw new DeliveryException($"Inventory '{state.Name}' holds no removal {removalId}. Nothing was restored.");
            }

            if (removal.Scope != InventoryRemovals.SoftDelete)
            {
                throw new DeliveryException(
                    $"Removal {removalId} of inventory '{state.Name}' {InventoryRemovals.Describe(removal.Scope)}: only what a soft delete removed can be brought back. Nothing was restored.");
            }

            if (removal.Status == InventoryRunStatus.Running)
            {
                throw new DeliveryException($"Removal {removalId} of inventory '{state.Name}' is still running; restore what it removed once it has ended. Nothing was restored.");
            }

            if (removal.Removed != request.Expected)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Removal {removalId} of inventory '{state.Name}' removed {removal.Removed} id(s), and the restore was asked for {request.Expected}. Nothing was restored; look at it again and ask again."));
            }

            return;
        }

        if (request.NamesIds)
        {
            var held = await ledger.InventoryRecordsOfAsync(partition, state.InventoryId, request.Ids, ct).ConfigureAwait(false);
            var unknown = request.Ids.Except(held.Select(r => r.TargetId), StringComparer.Ordinal).ToList();
            if (unknown.Count > 0)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The restore names {unknown.Count} id(s) inventory '{state.Name}' holds no row of ({string.Join(", ", unknown.Take(5))}{(unknown.Count > 5 ? ", ..." : "")}). Nothing was restored."));
            }

            return;
        }

        var holds = (await ledger.InventoryCountsAsync(_flow.LedgerId, state.InventoryId, ct).ConfigureAwait(false)).Of(InventoryFindings.Gone);
        if (holds != request.Expected)
        {
            throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"Inventory '{state.Name}' holds {holds} gone id(s) now, and the restore was asked for {request.Expected}: what it holds changed since it was shown. Nothing was restored; look at it again and ask again."));
        }
    }

    /// <summary>Reads, checks, restores and records the restore's ids a round at a time, until none is left.</summary>
    private async Task<InventoryRemovalTally> RestoreRoundsAsync(
        ILedger ledger, InventoryState state, InventoryRestoreRequest request, IReadOnlyList<string> owners, long restoreId, Guid runId, string actor, CancellationToken ct)
    {
        using var http = new HttpRuntime(_flow.Reliability, _context.Secrets, _context.Time, _transport, _allowLoopback, observer: _context.HttpObserver);
        var client = await ProtocolFactory.ClientAsync(http, _flow.Source.Endpoint, _flow.Source.Auth, _flow.Source.Headers, _context.Secrets, ct).ConfigureAwait(false);
        var restoredDetail = string.Create(CultureInfo.InvariantCulture, $"restored by {actor} in restore {restoreId} (run {runId:D}): OSDU serves it again at the version it had");
        var tally = InventoryRemovalTally.None;
        var reported = _context.Time.GetUtcNow();
        long after = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var round = await ledger.InventoryRestoreCandidatesAsync(
                _flow.LedgerId, state.InventoryId, request.NamesIds ? request.Ids : null, request.Removal, owners, after, InventoryRemovals.RestoreChunk, ct).ConfigureAwait(false);
            if (round.Count == 0)
            {
                return tally;
            }

            after = round[^1].Order;
            var (items, refusal) = await RestoreRoundAsync(client, round, request, ct).ConfigureAwait(false);
            await ledger.RecordInventoryRestoreAsync(_flow.LedgerId, state.InventoryId, restoreId, items, owners, restoredDetail, Now, ct).ConfigureAwait(false);
            tally = tally.Add(items);
            if (refusal is not null)
            {
                throw new RemovalStoppedException(refusal, tally);
            }

            var now = _context.Time.GetUtcNow();
            if (now - reported >= RemovalProgressEvery)
            {
                reported = now;
                _log.LogInformation("restore {Restore}: {Done} of {Expected} id(s) reached so far: {Tally}", restoreId, tally.Total, request.Expected, tally.DescribeRestore());
            }
        }
    }

    /// <summary>
    /// One round: what the inventory no longer finds gone, what a ledger knows, and (for gone ids picked or taken with every one)
    /// what no identity this estate writes as created is skipped with why; the rest is asked of storage one id at a time. Returns
    /// every id's outcome, and why the restore has to stop when OSDU refused every id it was asked for want of permission.
    /// </summary>
    private async Task<(IReadOnlyList<InventoryRemovalItem> Items, string? Refusal)> RestoreRoundAsync(
        OsduHttpClient client, IReadOnlyList<InventoryRestoreCandidate> round, InventoryRestoreRequest request, CancellationToken ct)
    {
        var items = new List<InventoryRemovalItem>(round.Count);
        var restoring = new List<InventoryRestoreCandidate>(round.Count);
        foreach (var candidate in round)
        {
            if (candidate.Held && candidate.Recorded != InventoryFindings.Gone)
            {
                items.Add(Item(candidate, InventoryRemovals.Skipped, candidate.Recorded is InventoryFindings.Missing or InventoryFindings.Unlisted
                    ? $"the inventory finds it {candidate.Recorded} now: a ledger expects it, so it comes back through that ledger's flow (Deliver again), not by a restore"
                    : $"the inventory finds it {candidate.Recorded} now: OSDU serves it, so there is nothing to restore"));
            }
            else if (candidate.KnownToLedger)
            {
                items.Add(Item(candidate, InventoryRemovals.Skipped, LedgerKnows(candidate)));
            }
            else if (request.Removal is null && candidate.Current != InventoryFindings.Orphan)
            {
                // A removal of this inventory checked who created what it removed; an id gone some other way is restored only
                // when this estate wrote it.
                items.Add(Item(candidate, InventoryRemovals.Skipped,
                    $"storage last said {candidate.CreateUser ?? "no identity"} created it, not an identity this estate writes as: a restore brings back only what this estate wrote"));
            }
            else
            {
                restoring.Add(candidate);
            }
        }

        var path = _flow.Source.PurgePath;
        var refused = 0;
        Exception? refusal = null;
        foreach (var candidate in restoring)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (outcome, _) = await RecordWriter.RestoreAsync(client, path, candidate.TargetId, ct).ConfigureAwait(false);
                items.Add(outcome switch
                {
                    RecordRestore.Restored => Item(candidate, InventoryRemovals.Restored, $"brought back at the version it had (PATCH {path} with {{\"deleted\": false}}; no new version)"),
                    RecordRestore.NotHeld => Item(candidate, InventoryRemovals.Gone, "storage holds no record of it: it was purged, or never written, so nothing can bring it back"),
                    _ => Item(candidate, InventoryRemovals.Skipped, "OSDU serves it already: it was brought back since the inventory found it gone, and the next build lists it"),
                });
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                if (ex is OsduStatusException { StatusCode: 401 or 403 })
                {
                    refused++;
                    refusal ??= ex;
                }

                items.Add(Item(candidate, InventoryRemovals.Failed, HeaderRedaction.RedactMessage(ex.Message)));
            }
        }

        var stop = restoring.Count > 0 && refused == restoring.Count && refusal is OsduStatusException denied
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"OSDU refused the restore ({denied.StatusCode}) of every id of a round: the flow's source credentials must be an owner of the records (users.datalake.editors, and in each record's owners ACL). Nothing more was asked of it.")
            : null;
        return (items, stop);
    }

    /// <summary>Why a restore leaves an id a ledger knows: what the ledger holds of it comes back through that ledger's own flow.</summary>
    private static string LedgerKnows(InventoryRestoreCandidate candidate) => candidate.LedgerStatus is { } status
        ? $"a ledger holds its record ({status}): a record comes back through its own flow, which records it, so a restore leaves it"
        : candidate.ArtifactId is { } artifact
            ? string.Create(CultureInfo.InvariantCulture, $"a delivery recorded it as artifact {artifact} ({candidate.ArtifactState}): its record's ledger accounts for it, so a restore leaves it")
            : "its record was purged from a ledger, whose flow delivers the record's row again under this id, so a restore leaves it";

    private static InventoryRemovalItem Item(InventoryRestoreCandidate candidate, string outcome, string reason)
        => new(candidate.InventoryRecordId, candidate.TargetId, candidate.Version, candidate.Recorded ?? InventoryFindings.Gone, outcome, reason, candidate.LedgerFlowId, candidate.DeliveryKey);
}
