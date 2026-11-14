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
/// A deletion a person asks for, or previews: the missing flow of <c>Kind</c> and the one partition whose orphaned ledgers of
/// it go (the workbench's when it names none). A deletion also names that partition as <c>Confirm</c>.
/// </summary>
public sealed record DeliveryOrphanDeleteRequest(string? Kind = null, string? Flow = null, string? Partition = null, string? Confirm = null);

/// <summary>
/// One orphaned ledger as a deletion's preview shows it: what it holds, and when it cannot be deleted now, why, in full
/// (<c>Refusal</c>) and in a few words (<c>RefusedFor</c>).
/// </summary>
public sealed record DeliveryOrphanDeleteLedger(Guid Ledger, string Partition, string Interface, string LedgerName, long Rows, long Claimed, string? Refusal, string? RefusedFor);

/// <summary>What deleting a missing document would do in a partition: each ledger, and how many can go and how many are refused.</summary>
public sealed record DeliveryOrphanDeletePreview(string Kind, string FlowName, string Partition, IReadOnlyList<DeliveryOrphanDeleteLedger> Ledgers, int Deletable, int Refused);

/// <summary>What a deletion did: each orphaned ledger's deletion, and how many of them ended each way.</summary>
public sealed record DeliveryOrphanDeleteAnswer(IReadOnlyList<LedgerDeleteResult> Ledgers, int Completed, int Waiting, int Failed);

/// <summary>
/// The missing documents, their takeover and their deletion (osdu/docs/reference/concepts/renaming-a-pipeline.md, Missing
/// documents): the flows no repository declares any more whose ledgers hold anything; handing a missing document's ledgers to
/// a declared flow, which moves each ledger to the flow's at once, as the person who asked; and deleting what a missing
/// document left behind in one partition, once that partition is named back, which is refused for a ledger that knows
/// something of OSDU nothing else does.
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
        delivery.MapPost("/ledgers/orphaned/delete/preview", PreviewDeleteAsync).WithName("PreviewDeleteDeliveryOrphanedLedgers");
        delivery.MapPost("/ledgers/orphaned/delete", DeleteAsync).WithName("DeleteDeliveryOrphanedLedgers");
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

    /// <summary>
    /// What deleting the missing document would do in the partition, writing nothing: each of its orphaned ledgers there
    /// with what it holds, and why it cannot be deleted now when it cannot. Refused with 400 when the request names no kind,
    /// flow or partition, and with 409 when the document left no orphaned ledger in the partition.
    /// </summary>
    private static async Task<Results<Ok<DeliveryOrphanDeletePreview>, ProblemHttpResult>> PreviewDeleteAsync(
        DeliveryOrphanDeleteRequest? body, HttpRequest request, ILedger ledger, CancellationToken ct)
    {
        if (Named(body, request, out var named) is { } incomplete)
        {
            return incomplete;
        }

        IReadOnlyList<LedgerDeleteStep> steps;
        try
        {
            steps = await ledger.PreviewOrphanedLedgerDeleteAsync(named, ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            return TypedResults.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Nothing to delete");
        }

        return TypedResults.Ok(new DeliveryOrphanDeletePreview(
            named.Kind, steps[0].FlowName, steps[0].Partition,
            [.. steps.Select(s => new DeliveryOrphanDeleteLedger(s.Ledger, s.Partition, s.Interface, s.LedgerName, s.Rows, s.Claimed, s.Refusal, s.RefusedFor))],
            steps.Count(s => s.Refusal is null),
            steps.Count(s => s.Refusal is not null)));
    }

    /// <summary>
    /// Deletes what the missing document left behind in the partition, as the caller. Refused with 400 when the request
    /// names no kind, flow or partition, or its confirmation is missing or names another partition, and with 409 when the
    /// document left no orphaned ledger in the partition; otherwise answers what came of each ledger, a ledger that cannot
    /// be deleted saying why.
    /// </summary>
    private static async Task<Results<Ok<DeliveryOrphanDeleteAnswer>, ProblemHttpResult>> DeleteAsync(
        DeliveryOrphanDeleteRequest? body, HttpRequest request, ILedger ledger, ClaimsPrincipal user, CancellationToken ct)
    {
        if (Named(body, request, out var named) is { } incomplete)
        {
            return incomplete;
        }

        var confirm = body?.Confirm?.Trim();
        if (string.IsNullOrEmpty(confirm))
        {
            return TypedResults.Problem(
                detail: $"Name the partition the orphaned ledgers are kept in ('{named.Partition}') as 'confirm'; nothing is deleted without it.",
                statusCode: StatusCodes.Status400BadRequest, title: "Confirmation required");
        }

        if (!string.Equals(confirm, named.Partition, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(
                detail: $"The confirmation names '{confirm}', but the orphaned ledgers of '{named.Flow}' to delete are kept in partition '{named.Partition}'. Nothing was deleted.",
                statusCode: StatusCodes.Status400BadRequest, title: "Confirmation does not match");
        }

        IReadOnlyList<LedgerDeleteResult> deleted;
        try
        {
            deleted = await ledger.DeleteOrphanedLedgersAsync(named, RequestActor.Label(user), ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            return TypedResults.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Not deleted");
        }

        return TypedResults.Ok(new DeliveryOrphanDeleteAnswer(
            deleted,
            deleted.Count(d => d.Outcome == LedgerMoves.Completed),
            deleted.Count(d => d.Outcome == LedgerMoves.Running),
            deleted.Count(d => d.Outcome == LedgerMoves.Failed)));
    }

    /// <summary>
    /// The missing document and the one partition a deletion or its preview names; a problem when the request names no kind
    /// or flow, or neither it nor the workbench names a partition, since a deletion is made, and confirmed, a partition at a
    /// time.
    /// </summary>
    private static ProblemHttpResult? Named(DeliveryOrphanDeleteRequest? body, HttpRequest request, out LedgerDeleteRequest named)
    {
        named = new LedgerDeleteRequest(string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(body?.Kind) || string.IsNullOrWhiteSpace(body.Flow))
        {
            return TypedResults.Problem(
                detail: "Name the missing flow's kind as 'kind' and the missing flow as 'flow'.",
                statusCode: StatusCodes.Status400BadRequest, title: "Deletion incomplete");
        }

        if (WorkbenchPartition.Named(body.Partition, request) is not { } partition)
        {
            return TypedResults.Problem(
                detail: "Name the partition whose orphaned ledgers to delete as 'partition': a deletion is made, and confirmed, one partition at a time.",
                statusCode: StatusCodes.Status400BadRequest, title: "Deletion incomplete");
        }

        named = new LedgerDeleteRequest(body.Kind.Trim(), body.Flow.Trim(), partition);
        return null;
    }
}
