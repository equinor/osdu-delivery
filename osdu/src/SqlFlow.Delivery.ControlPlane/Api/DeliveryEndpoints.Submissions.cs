using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One plan of a flow over its ingestion tables as the ledger received it, and what became of it: which selection it
/// read (<c>Kind</c>, the window and what else bounded it), where the records came from (<c>SourceConnection</c> as the
/// flow declares it, <c>SourceObject</c>), the run that carried it, and the partition whose ledger holds it.
/// </summary>
public sealed record DeliverySubmissionDto(
    Guid SubmissionId, Guid FlowId, string FlowName, string MappingReference, string RenderContext,
    string ParametersJson, long RecordCount, string Status, DateTime ReceivedUtc, DateTime? StartedUtc, DateTime? CompletedUtc,
    long Planned, long SkippedUnchanged, long AwaitingApproval, long SkippedStale, long UnchangedAtPush, long Blocked, long Delivered, long Held, long Failed, string? Error,
    string? WorkLocation, int BatchCount, int Slices,
    string Kind = SubmissionKinds.Incremental, long Untracked = 0,
    string SourceConnection = "", string SourceObject = "", DateTime? WindowFromUtc = null, DateTime? WindowToUtc = null,
    JsonElement? SourceWindow = null, Guid? RunId = null, long Waiting = 0, string? Partition = null);

/// <summary>One retrieval run of a retrieval flow: the window it covered, where its files went, and its outcome.</summary>
public sealed record DeliveryRetrievalDto(
    long RetrievalId, Guid FlowId, string FlowName, Guid? RunId, string Actor, string Kinds, string? Query, string? WindowField,
    DateTime? WindowFrom, DateTime? WindowTo, string Location, string? ManifestLocation, string Status, long Records, int Files, long Bytes,
    DateTime StartedUtc, DateTime? CompletedUtc, string? Error);

/// <summary>One work batch of a submission: a file of rendered documents and how far its drain got.</summary>
public sealed record DeliveryWorkBatchDto(
    Guid SubmissionId, int Index, string Location, int RecordCount, string Status, string? LeaseOwner, DateTime? LeaseExpiresUtc, Guid? RunId,
    DateTime CreatedUtc, DateTime? StartedUtc, DateTime? CompletedUtc, long Delivered, long Held, long Failed, long Retrying, string? Error,
    long Waiting = 0);

/// <summary>
/// A submission with the pipeline, interface and partition that planned it, the runs that carried it, and what its deliveries
/// were sent for (<c>DeliveredBy</c>).
/// </summary>
public sealed record DeliverySubmissionDetailDto(
    DeliverySubmissionDto Submission, Guid? PipelineId, IReadOnlyList<Guid> RunIds, string? Interface = null, string? Partition = null,
    DeliverySubmissionTriggersDto? DeliveredBy = null);

/// <summary>
/// The records a submission delivered, counted by what made them render and be sent: under each trigger kind its reasons
/// name (a record under every kind its reason names), and those delivered before the ledger kept reasons
/// (osdu/docs/reference/concepts/delivery-reasons.md).
/// </summary>
public sealed record DeliverySubmissionTriggersDto(long Delivered, long NotRecorded, IReadOnlyList<DeliveryTriggerCountDto> Triggers);

/// <summary>How many of a submission's delivered records one kind of trigger reached.</summary>
public sealed record DeliveryTriggerCountDto(string Kind, long Records);

/// <summary>
/// What the runs of a flow did: a delivery flow's submissions, one of them with the runs that carried it and what its
/// deliveries were sent for, its attempts and its work batches; and a retrieval flow's retrievals.
/// </summary>
public static partial class DeliveryEndpoints
{
    /// <summary>Runs one submission's page lists: the run that registered it and everything that has worked on it since.</summary>
    private const int MaxSubmissionRuns = 100;

    private static async Task<Results<Ok<IReadOnlyList<DeliverySubmissionDto>>, ProblemHttpResult>> ListSubmissionsAsync(
        Guid pipelineId, int? max, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        var (flow, problem) = await ResolveKeptAsync(db, documents, partitions, ledger, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var submissions = await ledger.ListSubmissionsAsync(flow.FlowId, Math.Clamp(max ?? 100, 1, 1000), ct: ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliverySubmissionDto>>(submissions.Select(ToDto).ToList());
    }

    /// <summary>A retrieval flow's runs, newest first. The flow id derives from the pipeline's name, as the executor derives it.</summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryRetrievalDto>>, ProblemHttpResult>> ListRetrievalsAsync(
        Guid pipelineId, int? max, CatalogDbContext db, ILedger ledger, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return NotFound("pipeline", pipelineId);
        }

        if (!string.Equals(pipeline.Kind, RetrievalDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(
                detail: $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a retrieval flow.",
                statusCode: StatusCodes.Status409Conflict, title: "Not a retrieval flow");
        }

        var rows = await ledger.ListRetrievalsAsync(FlowId.Of(pipeline.Name), Math.Clamp(max ?? 100, 1, 1000), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryRetrievalDto>>(rows.Select(r => new DeliveryRetrievalDto(
            r.RetrievalId, r.FlowId, r.FlowName, r.RunId, r.Actor, r.Kinds, r.Query, r.WindowField, r.WindowFrom, r.WindowTo, r.Location, r.ManifestLocation,
            r.Status, r.Records, r.Files, r.Bytes, r.StartedUtc, r.CompletedUtc, r.Error)).ToList());
    }

    private static async Task<Results<Ok<DeliverySubmissionDetailDto>, ProblemHttpResult>> GetSubmissionAsync(
        Guid submissionId, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission is null)
        {
            return NotFound("submission", submissionId);
        }

        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, submission.FlowId, ct).ConfigureAwait(false);
        var runIds = await SubmissionRunsAsync(db, submissionId, submission.RunId, found?.Pipeline.Id, ct).ConfigureAwait(false);
        var counted = await ledger.CountDeliveredTriggersAsync(submissionId, ct).ConfigureAwait(false);
        var deliveredBy = new DeliverySubmissionTriggersDto(
            counted.Delivered, counted.NotRecorded, [.. counted.ByTrigger.Select(t => new DeliveryTriggerCountDto(t.Kind, t.Records))]);
        return TypedResults.Ok(new DeliverySubmissionDetailDto(ToDto(submission), found?.Pipeline.Id, runIds, NamedInterface(found), NamedPartition(found), deliveredBy));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryAttemptDto>>, ProblemHttpResult>> ListSubmissionAttemptsAsync(
        Guid submissionId, int? max, ILedger ledger, CancellationToken ct)
    {
        var submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission is null)
        {
            return NotFound("submission", submissionId);
        }

        var attempts = await ledger.ListAttemptsForSubmissionAsync(submissionId, Math.Clamp(max ?? 500, 1, 5000), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryAttemptDto>>(attempts.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<PagedResult<DeliveryWorkBatchDto>>, ProblemHttpResult>> ListSubmissionBatchesAsync(
        Guid submissionId, string? status, int? page, int? pageSize, ILedger ledger, CancellationToken ct)
    {
        var submission = await ledger.GetSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (submission is null)
        {
            return NotFound("submission", submissionId);
        }

        WorkBatchStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<WorkBatchStatus>(status, ignoreCase: true, out var parsed))
            {
                return TypedResults.Problem(
                    detail: $"status must be one of {string.Join(", ", Enum.GetNames<WorkBatchStatus>().Select(n => n.ToLowerInvariant()))}.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
            }

            statusFilter = parsed;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var items = await ledger.ListWorkBatchesAsync(submissionId, size, (p - 1) * size, ct).ConfigureAwait(false);
        if (statusFilter is { } wanted)
        {
            items = items.Where(b => b.Status == wanted).ToList();
        }

        var total = await ledger.CountWorkBatchesAsync(submissionId, statusFilter, ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<DeliveryWorkBatchDto>(items.Select(ToDto).ToList(), p, size, (int)Math.Min(total, int.MaxValue)));
    }

    /// <summary>
    /// The runs that worked on one submission, newest first: the run that registered it, and every run whose kind
    /// arguments name it (a re-run, a drain, a fan-out member). A run carries the submission in its payload rather than in
    /// a column of its own, so the payload is what finds them. That match is scoped to the submission's own flow, which is
    /// the only flow those runs belong to; without the scope the search reads every run the estate has ever recorded to
    /// answer one submission's page.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> SubmissionRunsAsync(
        CatalogDbContext db, Guid submissionId, Guid? runId, Guid? pipelineId, CancellationToken ct)
    {
        var named = submissionId.ToString("D");
        return await db.Runs.AsNoTracking()
            .Where(r => (runId != null && r.RunId == runId)
                || (pipelineId != null && r.PipelineId == pipelineId && r.Payload != null && r.Payload.Contains(named)))
            .OrderByDescending(r => r.EnqueuedUtc)
            .Select(r => r.RunId)
            .Take(MaxSubmissionRuns)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private static DeliveryWorkBatchDto ToDto(WorkBatchState b) => new(
        b.SubmissionId, b.Index, b.Location, b.RecordCount, b.Status.ToString().ToLowerInvariant(), b.LeaseOwner, b.LeaseExpiresUtc, b.RunId,
        b.CreatedUtc, b.StartedUtc, b.CompletedUtc, b.Delivered, b.Held, b.Failed, b.Retrying, b.Error, b.Waiting);
}
