using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Reversals;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// What a reversal is asked to reverse: one run (<c>runId</c>) or one submission (<c>submissionId</c>), never both.
/// <c>Expected</c> is how many records the preview said the source delivered: the request is refused with 409 when the source
/// now reaches another number, so a reversal never takes more than the operator saw. <c>Pool</c> names the pool the reverse
/// run is queued on.
/// </summary>
public sealed record DeliveryReversalRequest(Guid? RunId = null, Guid? SubmissionId = null, long? Expected = null, string? Pool = null);

/// <summary>What a reversal can do on the flow's route: the calls a restore and a removal make, or why the route cannot.</summary>
public sealed record DeliveryReversalRouteDto(string Protocol, bool Restores, string Restore, bool Removes, string Remove);

/// <summary>
/// What reversing a source would reach, with nothing written: the submissions it covers, how many records it delivered, and
/// for a sample of them (all of them, up to the sample's size) what the reversal would do, by the decision the run takes:
/// how many it would restore to an earlier version, remove, read the earlier version of from OSDU's version list (the
/// ledger no longer says), and pass over, by why. <c>Existing</c> is the reversal of this source when one was asked for.
/// </summary>
public sealed record DeliveryReversalPreviewDto(
    string Source, Guid SourceId, int Submissions, long Records, int Sampled, bool SampleIsAll, int Restore, int Remove, int ResolvedFromOsdu,
    IReadOnlyDictionary<string, int> PassedOver, DeliveryReversalRouteDto Route, DeliveryTargetDto Target, DeliveryReversalDto? Existing);

/// <summary>
/// One reversal: its source, its state, who asked and when, the latest run that worked on it, and its records by state and by
/// outcome (null in a listing, which does not count them).
/// </summary>
public sealed record DeliveryReversalDto(
    long ReversalId, Guid FlowId, string FlowName, string? Partition, string Source, Guid SourceId, int Submissions, string Status, string RequestedBy,
    DateTime RequestedUtc, DateTime? CapturedUtc, DateTime? StartedUtc, DateTime? CompletedUtc, Guid? LastRunId, string? Error,
    long? Records = null, IReadOnlyDictionary<string, long>? States = null, IReadOnlyDictionary<string, long>? Outcomes = null);

/// <summary>A reversal with its pipeline and interface, the submissions it covers, and the runs that worked on it, newest first.</summary>
public sealed record DeliveryReversalDetailDto(
    DeliveryReversalDto Reversal, Guid? PipelineId, string? Interface, IReadOnlyList<Guid> SubmissionIds, IReadOnlyList<Guid> RunIds);

/// <summary>
/// One record of a reversal: what its source left (<c>RunVersion</c>), what OSDU held before (<c>Prior</c>: version, none or
/// unknown, with the version), its state and what came of it and why, the version put back and the one OSDU gave it, the run
/// that settled it, and the record's source key and label as the ledger names it now.
/// </summary>
public sealed record DeliveryReversalItemDto(
    Guid DeliveryKey, string? TargetId, string State, string? Outcome, string? Detail, long? RunVersion, string Prior, long? PriorVersion,
    long? RestoredVersion, long? NewVersion, Guid? RunId, DateTime UpdatedUtc, string? SourceKey, string? Label);

/// <summary>A page of a reversal's records in key order, and the key the next page starts after (null on the last page).</summary>
public sealed record DeliveryReversalItemPageDto(IReadOnlyList<DeliveryReversalItemDto> Items, Guid? Next);

/// <summary>The reverse run a request queued, and the source it reverses with how many records it delivered.</summary>
public sealed record DeliveryReversalAccepted(Guid RunId, string Status, string Source, Guid SourceId, long Records);

/// <summary>
/// Whether a run or a submission has anything to reverse now, by the answer a request and the reverse run go by
/// (<see cref="ReversalAvailability"/>): what the GUI offers the action by. <c>Resumes</c> says the request would resume the
/// reversal that exists; <c>Reason</c> says why there is nothing to reverse (it delivered nothing, its reversal settled every
/// record, or a reverse run of it is queued or running); <c>Reversal</c> is the reversal of the source, counted, when one was
/// asked for.
/// </summary>
public sealed record DeliveryReversibleDto(string Source, Guid SourceId, bool Reversible, bool Resumes, string? Reason, DeliveryReversalDto? Reversal);

/// <summary>
/// Reversals (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission): what one run or
/// submission put into OSDU, put back record by record as OSDU held it before. A preview reads what a reversal would
/// reach and decides a sample of it as the run would; a request queues the <c>reverse</c> run of the flow, which lists,
/// reverses and settles every record on a node, resumably; the listing, the detail and the records page read what the
/// ledger keeps of each reversal.
/// </summary>
public static class DeliveryReversalEndpoints
{
    /// <summary>The reversals a listing names unless asked for fewer.</summary>
    private const int DefaultReversals = 50;

    /// <summary>A page of a reversal's records unless asked for another size.</summary>
    private const int DefaultItems = 100;

    /// <summary>The runs a reversal's detail names.</summary>
    private const int MaxRuns = 200;

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/flows/{pipelineId:guid}/reversible", ReversibleAsync).WithName("GetDeliveryReversible");
        delivery.MapGet("/flows/{pipelineId:guid}/reversals", ListReversalsAsync).WithName("ListDeliveryReversals");
        delivery.MapGet("/reversals/{reversalId:long}", GetReversalAsync).WithName("GetDeliveryReversal");
        delivery.MapGet("/reversals/{reversalId:long}/records", ListReversalRecordsAsync).WithName("ListDeliveryReversalRecords");
    }

    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/flows/{pipelineId:guid}/reverse/preview", PreviewAsync).WithName("PreviewDeliveryReversal");
        delivery.MapPost("/flows/{pipelineId:guid}/reverse", ReverseAsync).WithName("ReverseDeliveryRun");
    }

    /// <summary>What reversing the source would reach, deciding a sample of its records as the run would; nothing is written.</summary>
    private static async Task<Results<Ok<DeliveryReversalPreviewDto>, ProblemHttpResult>> PreviewAsync(
        Guid pipelineId, DeliveryReversalRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        if (SourceOf(request) is not { } source)
        {
            return OneSource();
        }

        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        // The sample is decided exactly as the run decides each record, so what the preview counts is what the run does.
        ReversalPreview preview;
        try
        {
            var kind = await DeliveryEndpoints.MappingKindAsync(osdu, flow, ct).ConfigureAwait(false);
            preview = await ReversalPreview.ReadAsync(ledger, flow.Flow, source, kind, ReversalLimits.PreviewSample, ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            return NotReversible(ex.Message);
        }

        var route = preview.Route;
        return TypedResults.Ok(new DeliveryReversalPreviewDto(
            source.Kind, source.Id, preview.Submissions.Count, preview.Records, preview.Sampled, preview.SampleIsAll, preview.Restore, preview.Remove,
            preview.ResolvedFromOsdu, preview.PassedOver,
            new DeliveryReversalRouteDto(DeliveryProtocols.Name(flow.Flow.Target.Protocol), route.Restores, route.Restore, route.Removes, route.Remove),
            await DeliveryEndpoints.ToTargetDtoAsync(osdu, flow, ct).ConfigureAwait(false),
            preview.Existing is { } existing ? ToDto(existing, preview.ExistingCounts) : null));
    }

    /// <summary>
    /// Queues the reverse run of the source, as the caller. The source has to be the interface's (a submission of its ledger,
    /// or a run that planned a submission of it or delivered a record of it), and when <c>expected</c> is given it has to
    /// reach that many records still. The run reverses every record on a node; asking again resumes a reversal that stopped.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryReversalAccepted>, ProblemHttpResult>> ReverseAsync(
        Guid pipelineId, DeliveryReversalRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedgerReversals ledger, IRunDispatcher dispatcher, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (SourceOf(request) is not { } source)
        {
            return OneSource();
        }

        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        ReversalSourceRead read;
        try
        {
            read = await ledger.ReadReversalSourceAsync(flow.FlowId, flow.Flow.Label, source, 0, ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            return NotReversible(ex.Message);
        }

        // A source with nothing to reverse now is refused for the reason the GUI gives, rather than queuing a run that would
        // act on nothing: it delivered nothing, its reversal settled every record, or a reverse run of it is on its way.
        var availability = await AvailabilityAsync(db, ledger, flow, source, ct).ConfigureAwait(false);
        if (!availability.Reversible)
        {
            return TypedResults.Problem(detail: availability.Reason, statusCode: StatusCodes.Status409Conflict, title: "Nothing to reverse");
        }

        if (request!.Expected is { } expected && expected != read.Records)
        {
            return TypedResults.Problem(
                detail: $"The {source.Kind} delivered {expected} record(s) when it was shown and reaches {read.Records} now. Nothing was queued; preview it again and confirm.",
                statusCode: StatusCodes.Status409Conflict, title: "The source changed");
        }

        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Reverse,
            Payload = new DeliveryRunPayload
            {
                SubmissionId = source.IsRun ? null : source.Id,
                RunId = source.IsRun ? source.Id : null,
                Interface = flow.Flow.Interface,
            }.ToJson(),
        };
        var runId = await DeliveryEndpoints.EnqueueRunAsync(db, dispatcher, flow, parameters, request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryReversalAccepted(runId, RunStatuses.Queued, source.Kind, source.Id, read.Records));
    }

    /// <summary>
    /// Whether the source has anything to reverse now: what the run and submission pages and the audit trail offer the action
    /// by. Reads the ledger a seek at a time and the platform's queued and running reverse runs of the flow.
    /// </summary>
    private static async Task<Results<Ok<DeliveryReversibleDto>, ProblemHttpResult>> ReversibleAsync(
        Guid pipelineId, Guid? runId, Guid? submissionId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedgerReversals ledger, CancellationToken ct)
    {
        if (SourceOf(runId, submissionId) is not { } source)
        {
            return OneSource();
        }

        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var availability = await AvailabilityAsync(db, ledger, flow, source, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryReversibleDto(
            source.Kind, source.Id, availability.Reversible, availability.Resumes, availability.Reason,
            availability.Reversal is { } reversal ? ToDto(reversal, availability.Counts) : null));
    }

    /// <summary>Whether <paramref name="source"/> has anything to reverse now in the interface's ledger, a reverse run of it queued or running included.</summary>
    private static async Task<ReversalAvailability> AvailabilityAsync(
        CatalogDbContext db, ILedgerReversals ledger, DeliveryEndpoints.FlowContext flow, ReversalSource source, CancellationToken ct)
        => await ReversalAvailability.ReadAsync(ledger, flow.FlowId, source, await ActiveReverseRunAsync(db, flow, source, ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);

    /// <summary>
    /// A reverse run of <paramref name="source"/> in the interface (and partition) the platform has queued or is running: the
    /// flow's queued and running reverse runs, read through the run table's pipeline and operation index, matched by their
    /// payload. A reverse run opens its reversal only when it starts, so the ledger alone does not know of one still queued.
    /// </summary>
    private static async Task<Guid?> ActiveReverseRunAsync(CatalogDbContext db, DeliveryEndpoints.FlowContext flow, ReversalSource source, CancellationToken ct)
    {
        var pipelineId = flow.Pipeline.Id;
        var runs = await db.Runs.AsNoTracking()
            .Where(r => r.PipelineId == pipelineId && r.Operation == DeliveryOperations.Reverse && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running))
            .Select(r => new { r.RunId, r.Payload, r.ValuesJson })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var run in runs)
        {
            DeliveryRunPayload payload;
            try
            {
                payload = DeliveryRunPayload.Parse(run.Payload);
            }
            catch (SqlFlowException)
            {
                // A payload no reverse run could have been queued with names no source.
                continue;
            }

            var names = source.IsRun ? payload.RunId == source.Id : payload.SubmissionId == source.Id;
            if (names
                && string.Equals(payload.Interface, flow.Flow.Interface, StringComparison.Ordinal)
                && (flow.Flow.Partition is not { } partition || string.Equals(RunPartition(run.ValuesJson), partition, StringComparison.OrdinalIgnoreCase)))
            {
                return run.RunId;
            }
        }

        return null;
    }

    /// <summary>The partition a queued run's values name, or null when they name none or are not a JSON object.</summary>
    private static string? RunPartition(string? valuesJson)
    {
        if (string.IsNullOrWhiteSpace(valuesJson))
        {
            return null;
        }

        try
        {
            using var parsed = JsonDocument.Parse(valuesJson);
            return parsed.RootElement.ValueKind == JsonValueKind.Object
                && parsed.RootElement.TryGetProperty(PartitionNames.RunValue, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The interface's reversals, newest first, without their counts; or with <c>runId</c> or <c>submissionId</c>, the reversal of
    /// that source, counted, when one was asked for.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<DeliveryReversalDto>>, ProblemHttpResult>> ListReversalsAsync(
        Guid pipelineId, int? max, Guid? runId, Guid? submissionId, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        if (runId is not null && submissionId is not null)
        {
            return OneSource();
        }

        var (flow, problem) = await DeliveryEndpoints.ResolveKeptAsync(db, documents, partitions, ledger, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        if (runId is not null || submissionId is not null)
        {
            var source = runId is { } run ? ReversalSource.Run(run) : ReversalSource.Submission(submissionId!.Value);
            var found = await ledger.FindReversalAsync(flow.FlowId, source, ct).ConfigureAwait(false);
            return TypedResults.Ok<IReadOnlyList<DeliveryReversalDto>>(
                found is null ? [] : [await ToDtoAsync(ledger, found, counted: true, ct).ConfigureAwait(false)]);
        }

        var reversals = await ledger.ListReversalsAsync(flow.FlowId, Math.Clamp(max ?? DefaultReversals, 1, OsduLedger.MaxReversals), ct).ConfigureAwait(false);
        var dtos = new List<DeliveryReversalDto>(reversals.Count);
        foreach (var reversal in reversals)
        {
            dtos.Add(await ToDtoAsync(ledger, reversal, counted: false, ct).ConfigureAwait(false));
        }

        return TypedResults.Ok<IReadOnlyList<DeliveryReversalDto>>(dtos);
    }

    /// <summary>One reversal, counted, with its pipeline, the submissions it covers and the runs that worked on it.</summary>
    private static async Task<Results<Ok<DeliveryReversalDetailDto>, ProblemHttpResult>> GetReversalAsync(
        long reversalId, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var reversal = await ledger.GetReversalAsync(reversalId, ct).ConfigureAwait(false);
        if (reversal is null)
        {
            return NoSuchReversal(reversalId);
        }

        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, reversal.FlowId, ct).ConfigureAwait(false);
        var runs = await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = reversal.FlowId, Kind = ReverseKind, Max = MaxRuns }, ct).ConfigureAwait(false);
        var runIds = runs
            .Where(a => a.RunId is not null && Reverses(a.ParametersJson, reversal.Source))
            .Select(a => a.RunId!.Value)
            .Distinct()
            .ToList();
        return TypedResults.Ok(new DeliveryReversalDetailDto(
            await ToDtoAsync(ledger, reversal, counted: true, ct).ConfigureAwait(false),
            found?.Pipeline.Id,
            DeliveryEndpoints.NamedInterface(found),
            reversal.Submissions,
            runIds));
    }

    /// <summary>A page of a reversal's records in key order, every one or those of one outcome (<c>pending</c> for those not settled yet).</summary>
    private static async Task<Results<Ok<DeliveryReversalItemPageDto>, ProblemHttpResult>> ListReversalRecordsAsync(
        long reversalId, string? outcome, Guid? after, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (outcome is not null && !ReversalOutcomes.All.Contains(outcome, StringComparer.Ordinal) && outcome != ReversalItemStates.Pending)
        {
            return DeliveryEndpoints.Invalid(
                $"'{outcome}' is not what came of a record of a reversal: one of {string.Join(", ", ReversalOutcomes.All)}, or {ReversalItemStates.Pending} for those not settled yet.");
        }

        var reversal = await ledger.GetReversalAsync(reversalId, ct).ConfigureAwait(false);
        if (reversal is null)
        {
            return NoSuchReversal(reversalId);
        }

        var take = Math.Clamp(limit ?? DefaultItems, 1, OsduLedger.MaxReversalPage);
        var items = await ledger.ListReversalItemsAsync(reversalId, outcome, after is { } key ? new DeliveryKey(key) : null, take, ct).ConfigureAwait(false);
        var records = await ledger.GetRecordsAsync(reversal.FlowId, items.Select(i => i.DeliveryKey), ct).ConfigureAwait(false);
        var dtos = items.Select(i =>
        {
            records.TryGetValue(i.DeliveryKey, out var record);
            return new DeliveryReversalItemDto(
                i.DeliveryKey.Value, i.TargetId, i.State, i.Outcome, i.Detail, i.RunVersion, i.Prior, i.PriorVersion, i.RestoredVersion, i.NewVersion, i.RunId,
                i.UpdatedUtc, record?.SourceKey, record?.Label);
        }).ToList();
        return TypedResults.Ok(new DeliveryReversalItemPageDto(dtos, items.Count == take ? items[^1].DeliveryKey.Value : null));
    }

    /// <summary>The activity kind a reverse run records itself under.</summary>
    internal const string ReverseKind = "reverse";

    private static ReversalSource? SourceOf(DeliveryReversalRequest? request) => request is null ? null : SourceOf(request.RunId, request.SubmissionId);

    /// <summary>The one source a request names, a run or a submission; null for both, neither, or an empty id.</summary>
    private static ReversalSource? SourceOf(Guid? runId, Guid? submissionId) => (runId, submissionId) switch
    {
        ({ } run, null) when run != Guid.Empty => ReversalSource.Run(run),
        (null, { } submission) when submission != Guid.Empty => ReversalSource.Submission(submission),
        _ => null,
    };

    /// <summary>Whether an activity's parameters name <paramref name="source"/>: a reverse run records the source it reversed.</summary>
    private static bool Reverses(string? parametersJson, ReversalSource source)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return false;
        }

        try
        {
            using var parsed = JsonDocument.Parse(parametersJson);
            var root = parsed.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("source", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == source.Kind
                && root.TryGetProperty("sourceId", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var named) && named == source.Id;
        }
        catch (JsonException)
        {
            // Parameters that are not JSON are not a reverse run's.
            return false;
        }
    }

    private static async Task<DeliveryReversalDto> ToDtoAsync(ILedgerReversals ledger, ReversalState reversal, bool counted, CancellationToken ct)
        => ToDto(reversal, counted ? await ledger.CountReversalAsync(reversal.ReversalId, ct).ConfigureAwait(false) : null);

    private static DeliveryReversalDto ToDto(ReversalState reversal, ReversalCounts? counts) => new(
        reversal.ReversalId, reversal.FlowId, reversal.FlowName, reversal.Partition, reversal.Source.Kind, reversal.Source.Id, reversal.Submissions.Count,
        reversal.Status, reversal.RequestedBy, reversal.RequestedUtc, reversal.CapturedUtc, reversal.StartedUtc, reversal.CompletedUtc, reversal.LastRunId,
        reversal.Error, counts?.Records, counts?.States, counts?.Outcomes);

    private static ProblemHttpResult OneSource() => DeliveryEndpoints.Invalid("A reversal reverses one run (runId) or one submission (submissionId): name exactly one.");

    private static ProblemHttpResult NotReversible(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "Nothing to reverse");

    private static ProblemHttpResult NoSuchReversal(long reversalId)
        => TypedResults.Problem(detail: $"Reversal {reversalId} is not in the ledger.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
}
