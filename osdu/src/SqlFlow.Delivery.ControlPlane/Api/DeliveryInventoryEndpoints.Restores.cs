using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A restore of an inventory's gone ids an operator asks for (osdu/docs/reference/flow/inventory-removals.md, Restoring gone ids):
/// how many ids the operator was shown, the gone ids when they picked them or the soft-delete removal whose removed ids it
/// brings back (<c>removal</c>), the partition it acts in (<c>confirm</c>), and the pool its run is queued on.
/// </summary>
public sealed record DeliveryInventoryRestoreRequest(long? Expected, IReadOnlyList<string>? Ids, long? Removal, string? Confirm, string? Pool);

/// <summary>The restore queued: its run, the partition and inventory it acts in, how many ids it brings back, and the removal it takes them from.</summary>
public sealed record DeliveryInventoryRestoreAccepted(Guid RunId, string Status, string Partition, string Inventory, long Expected, long? Removal = null);

public static partial class DeliveryInventoryEndpoints
{
    /// <summary>
    /// Queues the run that brings back in OSDU what an inventory finds gone, as the caller (osdu/docs/reference/flow/inventory-removals.md,
    /// Restoring gone ids): every gone id, those picked, or the ids one soft-delete removal of the inventory removed. Refused with
    /// 400 for a request that does not say what it restores, names both ids and a removal, or whose confirmation names another
    /// partition; with 409 when the flow declares no removal (a restore writes to OSDU as a removal does), the inventory was never
    /// reconciled, it holds another number of gone ids than the operator was shown (or does not find gone an id they picked), the
    /// removal is not a finished soft delete of the inventory or removed another number of ids, or a run of the flow is already
    /// queued or running. The run checks it all again on the node, and every id again before it is brought back.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryInventoryRestoreAccepted>, ProblemHttpResult>> RestoreAsync(
        string partition, int inventoryId, DeliveryInventoryRestoreRequest? request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (inventory, problem) = await InventoryAsync(ledger, partition, inventoryId, ct).ConfigureAwait(false);
        if (inventory is null)
        {
            return problem!;
        }

        if (RestoreProblem(request, inventory) is { } refused)
        {
            return refused;
        }

        var expected = request!.Expected!.Value;
        var ids = request.Ids?.Select(i => i.Trim()).ToList() ?? [];
        var (pipeline, flow, declared) = await DeclaringFlowAsync(db, documents, inventory, ct).ConfigureAwait(false);
        if (pipeline is null || flow is null || declared != true)
        {
            return Problem(StatusCodes.Status409Conflict, "Not declared",
                $"No flow in the catalog declares inventory '{inventory.Name}' of '{inventory.FlowName}' now, so nothing can be restored through it.");
        }

        if (flow.Removal is null)
        {
            return Problem(StatusCodes.Status409Conflict, "Restore not allowed",
                $"Inventory flow '{flow.Name}' declares no 'removal', so it only reads OSDU, and a restore writes to OSDU as a removal does. Add removal: {{ findings: [orphan] }} to the flow to let it remove and restore.");
        }

        if (inventory.LastReconcileRunId is null)
        {
            return Problem(StatusCodes.Status409Conflict, "Not reconciled",
                $"Inventory '{inventory.Name}' has not been reconciled in '{inventory.Partition}', so it finds nothing gone to restore yet.");
        }

        if (request.Removal is { } removalId)
        {
            var removal = await ledger.GetInventoryRemovalAsync(inventory.Partition, removalId, ct).ConfigureAwait(false);
            if (removal is null || removal.InventoryId != inventory.InventoryId)
            {
                return Problem(StatusCodes.Status404NotFound, "Not found", $"Inventory '{inventory.Name}' holds no removal {removalId}.");
            }

            if (removal.Scope != InventoryRemovals.SoftDelete)
            {
                return Problem(StatusCodes.Status409Conflict, "Not a soft delete",
                    $"Removal {removalId} of inventory '{inventory.Name}' {InventoryRemovals.Describe(removal.Scope)}: only what a soft delete removed can be brought back.");
            }

            if (removal.Status == InventoryRunStatus.Running)
            {
                return Problem(StatusCodes.Status409Conflict, "Removal running",
                    $"Removal {removalId} of inventory '{inventory.Name}' is still running; restore what it removed once it has ended. Nothing was queued.");
            }

            if (removal.Removed != expected)
            {
                return Problem(StatusCodes.Status409Conflict, "Count changed",
                    $"Removal {removalId} of inventory '{inventory.Name}' removed {removal.Removed} id(s), and the restore was asked for {expected}. Nothing was queued; look at it again and ask again.");
            }
        }
        else if (ids.Count == 0)
        {
            var holds = (await ledger.InventoryCountsAsync(inventory.Partition, inventory.InventoryId, ct).ConfigureAwait(false)).Of(InventoryFindings.Gone);
            if (holds != expected)
            {
                return Problem(StatusCodes.Status409Conflict, "Count changed",
                    $"Inventory '{inventory.Name}' holds {holds} gone id(s) now, and the restore was asked for {expected}. Nothing was queued; look at it again and ask again.");
            }
        }
        else
        {
            var held = await ledger.InventoryRecordsOfAsync(inventory.Partition, inventory.InventoryId, ids, ct).ConfigureAwait(false);
            var gone = held.Where(r => r.Finding == InventoryFindings.Gone).Select(r => r.TargetId).ToHashSet(StringComparer.Ordinal);
            var not = ids.Where(i => !gone.Contains(i)).ToList();
            if (not.Count > 0)
            {
                return Problem(StatusCodes.Status409Conflict, "Ids changed",
                    $"{not.Count} of the ids picked {(not.Count == 1 ? "is" : "are")} not a gone id of inventory '{inventory.Name}' now ({string.Join(", ", not.Take(5))}{(not.Count > 5 ? ", ..." : "")}). Nothing was queued.");
            }
        }

        var payload = new DeliveryRunPayload
        {
            Inventories = [inventory.Name],
            Restore = new InventoryRestoreRequest { Expected = expected, Ids = ids, Removal = request.Removal },
            Confirm = inventory.Partition,
        };
        var (runId, busy) = await QueueChangeAsync(
            db, dispatcher, pipeline, flow, inventory, DeliveryOperations.Restore, payload, request.Pool, "a restore waits until the flow is idle, so what it brings back is what the inventory holds", user, ct).ConfigureAwait(false);
        if (busy is not null)
        {
            return busy;
        }

        return TypedResults.Accepted(
            $"/api/v1/runs/{runId}",
            new DeliveryInventoryRestoreAccepted(runId!.Value, RunStatuses.Queued, inventory.Partition, inventory.Name, expected, request.Removal));
    }

    /// <summary>What is wrong with a restore request on its face, before the flow and the inventory are read; null when nothing is.</summary>
    private static ProblemHttpResult? RestoreProblem(DeliveryInventoryRestoreRequest? request, InventoryState inventory)
    {
        if (request is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Restore required",
                "Name the count you were shown, the gone ids you picked or the removal whose removed ids to bring back (or neither, for every gone id), and the partition to confirm.");
        }

        if (request.Expected is not > 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Count required", "Name how many ids you were shown (expected); nothing is restored without it.");
        }

        if (IdsProblem(request.Ids, request.Expected.Value, "restore every gone id") is { } badIds)
        {
            return badIds;
        }

        if (request.Removal is { } removal)
        {
            if (request.Ids is not null)
            {
                return Problem(StatusCodes.Status400BadRequest, "Ids or a removal",
                    "A restore takes the ids it names or what one removal removed, not both: leave ids out to restore what the removal removed.");
            }

            if (removal < 1)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid removal", "removal is the number of a removal of the inventory, at least 1.");
            }
        }

        return ConfirmationProblem(request.Confirm, inventory, "restored");
    }

    /// <summary>
    /// What is wrong with the ids a removal or a restore names, or null: 1 to the most one names, each a plausible OSDU id, each
    /// once, as many as <paramref name="expected"/> says.
    /// </summary>
    private static ProblemHttpResult? IdsProblem(IReadOnlyList<string>? ids, long expected, string otherwise)
    {
        if (ids is null)
        {
            return null;
        }

        var named = ids.Select(i => i?.Trim() ?? string.Empty).ToList();
        if (named.Count == 0 || named.Count > InventoryRemovalRequest.MaxIds || named.Any(i => i.Length == 0 || i.Length > MaxIdLength))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid ids",
                $"ids names 1 to {InventoryRemovalRequest.MaxIds} OSDU ids; leave it out to {otherwise}.");
        }

        return named.Distinct(StringComparer.Ordinal).Count() != named.Count || named.Count != expected
            ? Problem(StatusCodes.Status400BadRequest, "Invalid ids", "ids names each id once, as many as expected says.")
            : null;
    }

    /// <summary>
    /// Why the partition typed back does not let a removal or a restore through, or null when it names the partition the inventory
    /// is kept in: nothing is <paramref name="done"/> without it.
    /// </summary>
    private static ProblemHttpResult? ConfirmationProblem(string? typed, InventoryState inventory, string done)
    {
        var confirm = typed?.Trim();
        if (string.IsNullOrEmpty(confirm))
        {
            return Problem(StatusCodes.Status400BadRequest, "Confirmation required",
                $"Name the partition the inventory is kept in as 'confirm'; nothing is {done} without it.");
        }

        return string.Equals(confirm, inventory.Partition, StringComparison.OrdinalIgnoreCase)
            ? null
            : Problem(StatusCodes.Status400BadRequest, "Confirmation does not match",
                $"The confirmation names '{confirm}', and inventory '{inventory.Name}' is kept in partition '{inventory.Partition}'. Nothing was queued.");
    }

    /// <summary>
    /// Queues the run of an inventory flow that changes what OSDU holds of an inventory's ids (a removal, a restore), as the
    /// caller, once the flow is idle: a run queued or running could change what the inventory holds under the operator, so
    /// nothing is queued while one is (<paramref name="waits"/> says so). Returns the run, or why none was queued.
    /// </summary>
    private static async Task<(Guid? RunId, ProblemHttpResult? Busy)> QueueChangeAsync(
        CatalogDbContext db, IRunDispatcher dispatcher, CatalogPipeline pipeline, InventoryFlowDefinition flow, InventoryState inventory, string operation,
        DeliveryRunPayload payload, string? pool, string waits, ClaimsPrincipal user, CancellationToken ct)
    {
        var busy = await db.Runs.AsNoTracking()
            .Where(r => r.PipelineId == pipeline.Id && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running))
            .Select(r => (Guid?)r.RunId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (busy is { } running)
        {
            return (null, Problem(StatusCodes.Status409Conflict, "Flow busy", $"Run {running:D} of '{pipeline.Name}' is queued or running; {waits}. Nothing was queued."));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (flow.Partitioned)
        {
            values[PartitionNames.RunValue] = inventory.Partition;
        }

        var parameters = new RunParameters { Operation = operation, Values = values, Payload = payload.ToJson() };
        var runId = await dispatcher.EnqueueAsync(
            db,
            new RunEnqueueRequest(
                pipeline.RepoId, pipeline.Name, pipeline.Kind, string.IsNullOrWhiteSpace(pool) ? null : pool.Trim(), null, parameters,
                RequestedBy: RequestActor.Of(user)),
            ct).ConfigureAwait(false);
        return (runId, null);
    }
}
