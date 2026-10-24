using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A takeover a person asks for: the missing flow of <c>Kind</c>, the declared flow that takes over its ledgers, and the one
/// partition whose ledgers it takes over (the workbench's when it names none; every partition from a client that sends none).
/// </summary>
public sealed record DeliveryTakeoverRequest(string? Kind = null, string? From = null, string? To = null, string? Partition = null);

/// <summary>What a takeover did: each orphaned ledger's move, and how many of them ended each way.</summary>
public sealed record DeliveryTakeoverAnswer(IReadOnlyList<LedgerMoveResult> Ledgers, int Completed, int Waiting, int Failed, int Skipped);

/// <summary>
/// The missing documents and their takeover (osdu/docs/reference/concepts/renaming-a-pipeline.md, Missing documents): the
/// flows no repository declares any more whose ledgers hold anything, and handing a missing document's ledgers to a declared
/// flow, which moves each ledger to the flow's at once, as the person who asked.
/// </summary>
public static class DeliveryOrphanedLedgerEndpoints
{
    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/ledgers/orphaned", ListAsync).WithName("ListDeliveryOrphanedDocuments");
    }

    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/ledgers/orphaned/takeover", TakeOverAsync).WithName("TakeOverDeliveryOrphanedLedgers");
    }

    /// <summary>The missing documents with a ledger in the partition the query names, else the workbench's, else in any partition.</summary>
    private static async Task<Ok<IReadOnlyList<OrphanedDocument>>> ListAsync(string? partition, HttpRequest request, ILedger ledger, CancellationToken ct)
        => TypedResults.Ok(await ledger.ListOrphanedDocumentsAsync(WorkbenchPartition.Named(partition, request), ct).ConfigureAwait(false));

    /// <summary>
    /// Hands the missing document's ledgers to the declared flow, as the caller. Refused with 400 when the request names no
    /// kind, missing flow or flow to take over, and with 409 when the document is not missing or the flow is not declared
    /// (once); otherwise answers what came of each ledger.
    /// </summary>
    private static async Task<Results<Ok<DeliveryTakeoverAnswer>, ProblemHttpResult>> TakeOverAsync(
        DeliveryTakeoverRequest? body, HttpRequest request, ILedger ledger, ClaimsPrincipal user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body?.Kind) || string.IsNullOrWhiteSpace(body.From) || string.IsNullOrWhiteSpace(body.To))
        {
            return TypedResults.Problem(
                detail: "Name the missing flow's kind as 'kind', the missing flow as 'from' and the declared flow that takes over its ledgers as 'to'.",
                statusCode: StatusCodes.Status400BadRequest, title: "Takeover incomplete");
        }

        IReadOnlyList<LedgerMoveResult> moves;
        try
        {
            moves = await ledger.TakeOverLedgersAsync(
                new LedgerTakeoverRequest(body.Kind.Trim(), body.From.Trim(), body.To.Trim(), WorkbenchPartition.Named(body.Partition, request)),
                RequestActor.Label(user), ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            return TypedResults.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Not taken over");
        }

        return TypedResults.Ok(new DeliveryTakeoverAnswer(
            moves,
            moves.Count(m => m.Outcome == LedgerMoves.Completed),
            moves.Count(m => m.Outcome == LedgerMoves.Running),
            moves.Count(m => m.Outcome == LedgerMoves.Failed),
            moves.Count(m => m.Outcome == LedgerBindings.Skipped)));
    }
}
