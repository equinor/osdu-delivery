using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// Deleting a flow's ledger: <c>Confirm</c> names the partition the ledger is kept in, as the operator typed it, and nothing
/// is queued unless it is that partition. <c>Pool</c> names the pool the run is queued on.
/// </summary>
public sealed record DeliveryLedgerDeleteRequest(string? Confirm = null, string? Pool = null);

/// <summary>The run a request to delete the ledger queued, the partition it acts in, and the interfaces whose ledgers it deletes.</summary>
public sealed record DeliveryLedgerDeleteAccepted(Guid RunId, string Status, string Partition, IReadOnlyList<string> Interfaces);

/// <summary>
/// Deleting a flow's ledger (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting the ledger) for every
/// interface of the pipeline at once: a request queues the <c>delete-ledger</c> run, which removes every record from
/// OSDU reversibly and then deletes everything the ledgers keep, on a node, as a run of the pipeline, so no delivery of
/// the pipeline runs beside it.
/// </summary>
public static class DeliveryLedgerEndpoints
{
    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/flows/{pipelineId:guid}/ledger/delete", DeleteLedgerAsync).WithName("DeleteDeliveryLedger");
    }

    /// <summary>
    /// Queues the run that deletes the ledgers of every interface of the pipeline in the partition the request names, as the
    /// caller. Refused with 400 when the confirmation is missing or names another partition than the one the ledgers are
    /// kept in, and with 409 when no run has kept a ledger of the flow yet, or a run deleting it is already queued or running.
    /// The run checks the confirmation again on the node before it removes or deletes anything.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryLedgerDeleteAccepted>, ProblemHttpResult>> DeleteLedgerAsync(
        Guid pipelineId, DeliveryLedgerDeleteRequest? request, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (PartitionConfirmation.Typed(request?.Confirm) is not { } confirm)
        {
            return TypedResults.Problem(
                detail: "Name the partition the ledger is kept in as 'confirm'; nothing is removed or deleted without it.",
                statusCode: StatusCodes.Status400BadRequest, title: "Confirmation required");
        }

        var (unbound, problem) = await DeliveryEndpoints.ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
        if (unbound is null)
        {
            return problem!;
        }

        var registry = await DeliveryEndpoints.RegistryForAsync(partitions, unbound.Source, partition, ct).ConfigureAwait(false);
        if (DeliveryEndpoints.Bind(unbound, partition, registry, out var source) is { } unpartitioned)
        {
            return unpartitioned;
        }

        // The partition the ledgers are kept in: the one the flow is bound to, or for a flow whose partition is its
        // data-partition-id header, the one the ledger's directory holds them in, which only the nodes resolve the header to.
        var kept = source.Source.Partition ?? await KeptPartitionAsync(ledger, source.Source, ct).ConfigureAwait(false);
        if (kept is null)
        {
            return TypedResults.Problem(
                detail: $"No run of '{source.Pipeline.Name}' has kept a ledger yet, so there is nothing to delete.",
                statusCode: StatusCodes.Status409Conflict, title: "Nothing to delete");
        }

        if (!PartitionConfirmation.Names(confirm, kept))
        {
            return TypedResults.Problem(
                detail: $"The confirmation names '{confirm}', but the ledger of '{source.Pipeline.Name}' is kept in partition '{kept}'. Nothing was queued.",
                statusCode: StatusCodes.Status400BadRequest, title: "Confirmation does not match");
        }

        var pipeline = source.Pipeline.Id;
        var active = await db.Runs.AsNoTracking()
            .Where(r => r.PipelineId == pipeline && r.Operation == DeliveryOperations.DeleteLedger && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running))
            .Select(r => (Guid?)r.RunId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (active is { } running)
        {
            return TypedResults.Problem(
                detail: $"Run {running:D} is already deleting the ledger of '{source.Pipeline.Name}'. Nothing was queued.",
                statusCode: StatusCodes.Status409Conflict, title: "Already deleting");
        }

        // The run is the pipeline's: every interface of the source shares its partition, so any of them binds it.
        var flow = new DeliveryEndpoints.FlowContext(source.Pipeline, source.Source, source.Source.First);
        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.DeleteLedger,
            Payload = new DeliveryRunPayload { Confirm = kept }.ToJson(),
        };
        var runId = await DeliveryEndpoints.EnqueueRunAsync(db, dispatcher, flow, parameters, request!.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted(
            $"/api/v1/runs/{runId}",
            new DeliveryLedgerDeleteAccepted(runId, RunStatuses.Queued, kept, source.Source.Names.ToList()));
    }

    /// <summary>The partition the ledger's directory holds a ledger of one of <paramref name="source"/>'s interfaces in, or null when it holds none.</summary>
    private static async Task<string?> KeptPartitionAsync(ILedger ledger, SourceDefinition source, CancellationToken ct)
    {
        foreach (var flow in source.Interfaces)
        {
            if (await ledger.GetLedgerAsync(flow.Id, ct).ConfigureAwait(false) is { Partition: { } kept })
            {
                return kept;
            }
        }

        return null;
    }
}
