using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A flow the audit trail can be narrowed to: the ledger identity its activities carry (<c>FlowId</c>, what the trail's
/// <c>flowId</c> takes), the flow and interface (null for the single form) the ledger's directory names it by, the kind of
/// ledger it is (a delivery flow, a dimension), and the partition it is kept under. Only an identity with activity is offered.
/// </summary>
public sealed record DeliveryActivityFlowDto(Guid FlowId, string FlowName, string Kind, string? Interface, string? Partition);

/// <summary>
/// One entry of the audit trail: who did what, when, with which inputs, and how it ended, and the partition whose ledger it
/// was done to. <c>Idle</c> marks a run that completed having changed nothing. One entry read on its own also names the
/// pipeline of its ledger, the interface of it, and the partition a request about the flow names
/// (<c>NamedPartition</c>: null for a flow whose partition is its header's), so what it did can be acted on from it (a run
/// reversed); a listing leaves them out.
/// </summary>
public sealed record DeliveryActivityDto(
    long ActivityId, Guid FlowId, string FlowName, string Kind, string Actor, DateTime StartedUtc, DateTime? CompletedUtc,
    string Outcome, string? ParametersJson, Guid? SubmissionId, Guid? DeliveryKey, Guid? RunId, string? Summary, string? Log,
    string? Partition = null, bool Idle = false, Guid? PipelineId = null, string? Interface = null, string? NamedPartition = null);

/// <summary>
/// The audit trail: its entries, newest first, narrowed to a flow, a submission, a run, a kind, an actor, an outcome
/// or a time, the flows it can be narrowed to, and one entry with the pipeline, interface and partition it can be acted
/// on through.
/// </summary>
public static partial class DeliveryEndpoints
{
    /// <summary>
    /// The audit trail, newest first. <paramref name="idle"/> false leaves out the runs that changed nothing, true lists only
    /// them (its total is how many the trail left out), and leaving it out lists every activity. One flow's trail is named by
    /// its pipeline (with the interface and partition a flow's own pages name), or by the ledger identity
    /// (<paramref name="flowId"/>, one of <see cref="ListActivityFlowsAsync"/>) the trail's flow choice carries; never both.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DeliveryActivityDto>>, ProblemHttpResult>> ListActivitiesAsync(
        Guid? pipelineId, Guid? flowId, Guid? submissionId, Guid? runId, string? kind, string? actor, string? outcome, bool? idle, DateTime? since, DateTime? until,
        int? page, int? pageSize, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents,
        IPartitionRegistry partitions, ILedger ledger, HttpRequest request, CancellationToken ct)
    {
        if (pipelineId is not null && flowId is not null)
        {
            return Invalid("Name the flow whose trail to read by its pipeline ('pipelineId') or by its ledger identity ('flowId'), not both.");
        }

        // One flow's trail named by its pipeline is read in the partition the request names, as every read of one flow is:
        // the flow's own for a flow whose partition is its header's. The trail across flows, and the trail of a ledger
        // identity, are the partition's the request names, else the workbench's, else every partition's; a ledger kept in
        // another partition than that reads empty, as the Records page's lookup of one does.
        var ledgerId = flowId;
        if (pipelineId is { } pid)
        {
            var (flow, problem) = await ResolveKeptAsync(db, documents, partitions, ledger, pid, interfaceName, partition, ct).ConfigureAwait(false);
            if (flow is null)
            {
                return problem!;
            }

            ledgerId = flow.FlowId;
        }
        else
        {
            partition = WorkbenchPartition.Named(partition, request);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = new ActivityQuery
        {
            Partition = pipelineId is null ? partition : null,
            FlowId = ledgerId,
            SubmissionId = submissionId,
            RunId = runId,
            Kind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant(),
            Actor = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim(),
            Outcome = string.IsNullOrWhiteSpace(outcome) ? null : outcome.Trim().ToLowerInvariant(),
            Idle = idle,
            SinceUtc = since is { } s ? DateTime.SpecifyKind(s.ToUniversalTime(), DateTimeKind.Utc) : null,
            UntilUtc = until is { } u ? DateTime.SpecifyKind(u.ToUniversalTime(), DateTimeKind.Utc) : null,
            Offset = (p - 1) * size,
            Max = size,
        };
        var items = await ledger.ListActivitiesAsync(query, ct).ConfigureAwait(false);
        var total = await ledger.CountActivitiesAsync(query, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryActivityDto>(items.Select(ToDto).ToList(), p, size, total));
    }

    /// <summary>
    /// The flows the audit trail can be narrowed to: every ledger identity of the partition the request names, else the
    /// workbench's, else every partition, that has at least one activity, named by the ledger's directory as its activities
    /// name it (the flow, and the interface of a source that delivers several). A ledger with no activity is not a choice,
    /// since narrowing to it could only show an empty trail. Unlike the Records page's choices, a ledger no synced pipeline
    /// holds any more stays one: the trail keeps what was done to a flow after the flow is gone. Ordered by flow, then
    /// interface, then partition.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryActivityFlowDto>>> ListActivityFlowsAsync(
        [FromQuery] string? partition, ILedger ledger, HttpRequest request, CancellationToken ct)
    {
        partition = WorkbenchPartition.Named(partition, request);
        var ledgers = await ledger.ListLedgersAsync(partition, ct).ConfigureAwait(false);
        var active = await ledger.FlowsWithActivitiesAsync(ledgers.Select(l => l.FlowId).ToList(), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryActivityFlowDto>>(ledgers
            .Where(l => active.Contains(l.FlowId))
            .Select(l => new DeliveryActivityFlowDto(l.FlowId, l.FlowName, l.Kind, l.Interface.Length == 0 ? null : l.Interface, l.Partition))
            .OrderBy(f => f.FlowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Interface ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Partition ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static async Task<Results<Ok<DeliveryActivityDto>, ProblemHttpResult>> GetActivityAsync(
        long activityId, CatalogDbContext db, OsduDbContext osdu, ILedgerActivities ledger, CancellationToken ct)
    {
        var activity = await ledger.GetActivityAsync(activityId, ct).ConfigureAwait(false);
        if (activity is null)
        {
            return TypedResults.Problem(detail: $"No activity '{activityId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        // The pipeline and interface of the ledger, as a submission names them, so the entry can be acted on where it is read.
        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, activity.FlowId, ct).ConfigureAwait(false);
        return TypedResults.Ok(ToDto(activity) with { PipelineId = found?.Pipeline.Id, Interface = NamedInterface(found), NamedPartition = NamedPartition(found) });
    }
}
