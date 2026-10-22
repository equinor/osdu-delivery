using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One issue keeping a flow's records blocked (osdu/docs/reference/concepts/record-lifecycle.md, Issues): its id, the
/// error its records share with every part that names a record replaced, how many records it keeps blocked (held and
/// failed), when they last changed, its most recently changed record as an example, with that record's own error, and
/// where it lies: <c>set</c> when its records carry the same error (no value of their own, or the same values in each),
/// <c>rows</c> when they name values of their own rows. <c>Values</c> are the values the example names, which for a set
/// error every record names.
/// </summary>
public sealed record DeliveryIssueDto(
    string Issue, string Pattern, long Records, long Held, long Failed, DateTime OldestUtc, DateTime NewestUtc, DeliveryRecordDto? Example,
    string Shape, IReadOnlyList<string> Values);

/// <summary>A record of an issue looked at closely, with the values its error names.</summary>
public sealed record DeliveryIssueSampleDto(DeliveryRecordDto Record, IReadOnlyList<string> Values);

/// <summary>
/// A flow's issues, the most records first: the ones listed, how many there are and how many records they keep blocked in
/// all, and how many blocked records are not sorted into an issue yet (blocked before the ledger kept issues).
/// </summary>
public sealed record DeliveryIssueListDto(IReadOnlyList<DeliveryIssueDto> Issues, long TotalIssues, long TotalRecords, long Unsorted);

/// <summary>An ingestion file some of an issue's records were left at, and how many; a null name for those naming none.</summary>
public sealed record DeliveryIssueFileDto(string? FileName, long Records);

/// <summary>
/// One issue with the files its records came from, the most records first, and samples spread evenly across its records
/// (its newest, its oldest and the ones between), which are what tell its shape: the issue's <c>Shape</c> here is the
/// samples' word, which knows more than the listing's two records.
/// </summary>
public sealed record DeliveryIssueDetailDto(DeliveryIssueDto Issue, IReadOnlyList<DeliveryIssueFileDto> Files, IReadOnlyList<DeliveryIssueSampleDto> Samples);

/// <summary>
/// A release of every record an issue keeps blocked. <c>Run</c> also queues a deliver run of the flow, read under the
/// parameter values the issue's example record was last planned with, which plans and sends every record released.
/// </summary>
public sealed record DeliveryIssueReleaseRequest(bool Run = false, string? Pool = null);

/// <summary>What a release of an issue's records did: how many it released, and the run it queued when asked to.</summary>
public sealed record DeliveryIssueReleaseResult(string Issue, int Released, Guid? RunId);

/// <summary>
/// An adoption asked of every record an issue keeps held because OSDU already holds a record at the id each record's key
/// makes. <c>Confirm</c> is the partition the flow's ledger is kept in, typed back. <c>Run</c> also queues a deliver run of
/// the flow, as an issue's release does, which takes the records over.
/// </summary>
public sealed record DeliveryIssueAdoptRequest(string? Confirm, bool Run = false, string? Pool = null);

/// <summary>What an adoption of an issue's records asked: how many records, and the run it queued when asked to.</summary>
public sealed record DeliveryIssueAdoptResult(string Issue, int Asked, Guid? RunId);

/// <summary>
/// The issues of a flow: what keeps its records blocked, grouped by the issue each record's error names, so an operator
/// facing a million blocked records reads a handful of issues, checks a few records of an issue once its cause is fixed,
/// and releases the issue's records together. Every count is the ledger's, read through the index of blocked records (the
/// ledger's problem index); a release names every record it released under its activity, so each record's history shows it.
/// </summary>
public static class DeliveryIssueEndpoints
{
    /// <summary>The issues a listing names unless asked for fewer.</summary>
    private const int DefaultIssues = 100;

    /// <summary>The files an issue names.</summary>
    private const int MaxFiles = 50;

    /// <summary>The samples an issue names unless asked for another number.</summary>
    private const int DefaultSamples = 5;

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/flows/{pipelineId:guid}/issues", ListIssuesAsync).WithName("ListDeliveryIssues");
        delivery.MapGet("/flows/{pipelineId:guid}/issues/{issue}", GetIssueAsync).WithName("GetDeliveryIssue");
    }

    public static void MapWrites(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapPost("/flows/{pipelineId:guid}/issues/{issue}/release", ReleaseIssueAsync).WithName("ReleaseDeliveryIssue");
        delivery.MapPost("/flows/{pipelineId:guid}/issues/{issue}/adopt", AdoptIssueAsync).WithName("AdoptDeliveryIssue");
    }

    /// <summary>The issues of one interface's ledger, the most records first, at most <paramref name="max"/>.</summary>
    private static async Task<Results<Ok<DeliveryIssueListDto>, ProblemHttpResult>> ListIssuesAsync(
        Guid pipelineId, int? max, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        var (flow, unresolved) = await DeliveryEndpoints.ResolveKeptAsync(db, documents, partitions, ledger, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return unresolved!;
        }

        var listing = await ledger.ListProblemsAsync(flow.FlowId, Math.Clamp(max ?? DefaultIssues, 1, OsduLedger.MaxProblems), ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryIssueListDto(listing.Problems.Select(ToDto).ToList(), listing.TotalProblems, listing.TotalRecords, listing.Unsorted));
    }

    /// <summary>One issue of one interface's ledger, with the files its records came from and samples spread across it.</summary>
    private static async Task<Results<Ok<DeliveryIssueDetailDto>, ProblemHttpResult>> GetIssueAsync(
        Guid pipelineId, string issue, int? samples, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        if (Parse(issue) is not { } hash)
        {
            return Invalid(issue);
        }

        var (flow, unresolved) = await DeliveryEndpoints.ResolveKeptAsync(db, documents, partitions, ledger, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return unresolved!;
        }

        var group = await ledger.GetProblemAsync(flow.FlowId, hash, ct).ConfigureAwait(false);
        if (group is null)
        {
            return NoSuchIssue(issue, flow);
        }

        var files = await ledger.ListProblemFilesAsync(flow.FlowId, hash, MaxFiles, ct).ConfigureAwait(false);
        var sampled = await ledger.ListProblemSamplesAsync(flow.FlowId, hash, Math.Clamp(samples ?? DefaultSamples, 1, OsduLedger.MaxProblemSamples), ct).ConfigureAwait(false);
        var shape = sampled.Count == 0 ? group.Shape : ProblemSignature.ShapeOf(sampled.Select(s => s.LastError).Append(group.Example?.LastError));
        return TypedResults.Ok(new DeliveryIssueDetailDto(
            ToDto(group with { Shape = shape }),
            files.Select(f => new DeliveryIssueFileDto(f.FileName, f.Records)).ToList(),
            sampled.Select(s => new DeliveryIssueSampleDto(DeliveryEndpoints.ToDto(s), ProblemSignature.Values(s.LastError))).ToList()));
    }

    /// <summary>
    /// Releases every record the issue keeps blocked, as the caller, recorded on the audit trail with the issue and its
    /// pattern and with every record it released named under it. With <c>run</c>, a deliver run of the flow is queued too.
    /// </summary>
    private static async Task<Results<Ok<DeliveryIssueReleaseResult>, Accepted<DeliveryIssueReleaseResult>, ProblemHttpResult>> ReleaseIssueAsync(
        Guid pipelineId, string issue, DeliveryIssueReleaseRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine, DeliveryConfigStore config, ILedger ledger,
        IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        if (Parse(issue) is not { } hash)
        {
            return Invalid(issue);
        }

        var (flow, unresolved) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return unresolved!;
        }

        // The pattern the release records is the issue as the ledger names it now, which is what the operator was shown.
        var group = await ledger.GetProblemAsync(flow.FlowId, hash, ct).ConfigureAwait(false);
        if (group is null)
        {
            return NoSuchIssue(issue, flow);
        }

        using var runtime = FlowRuntime.ForTarget(await DeliveryEndpoints.ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var released = await runtime.ReleaseProblemAsync(hash, group.Pattern, ct).ConfigureAwait(false);
        var named = ProblemSignature.Format(hash);
        if (request is not { Run: true })
        {
            return TypedResults.Ok(new DeliveryIssueReleaseResult(named, released, null));
        }

        // The run reads under the parameter values the example was last planned with, as a record's own run does, so a flow
        // whose scope is a parameter reads the records in the scope they were planned in.
        var last = group.Example?.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
        var runId = await DeliveryEndpoints.EnqueueRunAsync(db, dispatcher, flow, DeliveryEndpoints.DeliverRun(flow, last?.ParametersJson), request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryIssueReleaseResult(named, released, runId));
    }

    /// <summary>
    /// Asks, as the caller, every record the issue keeps held because OSDU already holds a record at the id its key makes to
    /// adopt that record, the partition typed back first (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record
    /// OSDU already holds), recorded on the audit trail with the issue and its pattern and with every record asked named under
    /// it. A record of the issue held for anything else is left. The flow's next deliver run takes the records over; with
    /// <c>run</c>, one is queued.
    /// </summary>
    private static async Task<Results<Ok<DeliveryIssueAdoptResult>, Accepted<DeliveryIssueAdoptResult>, ProblemHttpResult>> AdoptIssueAsync(
        Guid pipelineId, string issue, DeliveryIssueAdoptRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition,
        CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine, DeliveryConfigStore config, ILedger ledger,
        IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        if (Parse(issue) is not { } hash)
        {
            return Invalid(issue);
        }

        var (flow, unresolved) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return unresolved!;
        }

        var group = await ledger.GetProblemAsync(flow.FlowId, hash, ct).ConfigureAwait(false);
        if (group is null || (await ledger.GetLedgerAsync(flow.FlowId, ct).ConfigureAwait(false))?.Partition is not { Length: > 0 } kept)
        {
            return NoSuchIssue(issue, flow);
        }

        if (DeliveryEndpoints.AdoptionUnconfirmed(request?.Confirm, kept, $"Adopting the records of issue {issue}", flow.Flow.Label) is { } unconfirmed)
        {
            return unconfirmed;
        }

        using var runtime = FlowRuntime.ForTarget(await DeliveryEndpoints.ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        var asked = await runtime.AdoptProblemAsync(hash, group.Pattern, ct).ConfigureAwait(false);
        var named = ProblemSignature.Format(hash);
        if (request is not { Run: true } || asked.Count == 0)
        {
            return TypedResults.Ok(new DeliveryIssueAdoptResult(named, asked.Count, null));
        }

        // The run reads under the parameter values the example was last planned with, as an issue's release with a run does.
        var last = group.Example?.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
        var runId = await DeliveryEndpoints.EnqueueRunAsync(db, dispatcher, flow, DeliveryEndpoints.DeliverRun(flow, last?.ParametersJson), request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryIssueAdoptResult(named, asked.Count, runId));
    }

    /// <summary>An issue as a route names it: the sixteen characters the listing gives it.</summary>
    private static long? Parse(string? text)
        => ProblemSignature.TryParse(text?.Trim().ToLowerInvariant(), out var hash) ? hash : null;

    private static ProblemHttpResult Invalid(string? issue) => TypedResults.Problem(
        detail: $"'{issue}' is not an issue: an issue is the {ProblemSignature.TextLength} characters the flow's issues listing gives it.",
        statusCode: StatusCodes.Status400BadRequest,
        title: "Invalid request");

    private static ProblemHttpResult NoSuchIssue(string issue, DeliveryEndpoints.FlowContext flow) => TypedResults.Problem(
        detail: $"No record of {flow.Flow.Label} is blocked by issue {issue}: its records were released, their source changed, or they were planned again since.",
        statusCode: StatusCodes.Status404NotFound,
        title: "Not found");

    private static DeliveryIssueDto ToDto(ProblemGroup group) => new(
        ProblemSignature.Format(group.Problem), group.Pattern, group.Records, group.Held, group.Failed, group.OldestUtc, group.NewestUtc,
        group.Example is { } example ? DeliveryEndpoints.ToDto(example) : null,
        ShapeName(group.Shape), group.Values);

    /// <summary>An issue's shape as the API names it.</summary>
    private static string ShapeName(ProblemShape shape) => shape == ProblemShape.Rows ? "rows" : "set";
}
