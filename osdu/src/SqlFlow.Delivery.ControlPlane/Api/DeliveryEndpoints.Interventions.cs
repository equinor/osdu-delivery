using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// A release of a flow's blocked records: those <c>Keys</c> names, or with <c>All</c> every one. A request that names
/// neither is refused rather than taken for every record, and one that names both is refused too. <c>Run</c> also queues a
/// deliver run of the flow under the parameter values its last submission ran with, which sends what the release queued and
/// plans the rest.
/// </summary>
public sealed record DeliveryReleaseRequest(IReadOnlyList<Guid>? Keys, bool Run = false, string? Pool = null, bool All = false);

/// <summary>How many records a release released, and the run it queued when asked to.</summary>
public sealed record DeliveryReleaseResult(int Released, Guid? RunId = null);

/// <summary>
/// A lift of the removals of a flow's records (osdu/docs/reference/flow/removal.md, Delivering a record again): the records
/// <c>Keys</c> names, at most 1000. <c>Run</c> also queues a deliver run of the flow under the parameter values its last
/// submission ran with, which delivers them again at once.
/// </summary>
public sealed record DeliveryLiftRequest(IReadOnlyList<Guid>? Keys, bool Run = false, string? Pool = null);

/// <summary>How many records' removals a lift lifted (their keys no longer kept out), and the deliver run it queued when asked to.</summary>
public sealed record DeliveryLiftResult(int Lifted, Guid? RunId = null, string? MarksKept = null);

/// <summary>
/// A release of one record. <c>Run</c> also queues a deliver run scoped to it, read under the parameter values it was last
/// planned with, so the record is tried again at once: how an operator checks, on one record, that an issue's cause is
/// fixed before releasing every record the issue keeps blocked.
/// </summary>
public sealed record DeliveryRecordReleaseRequest(bool Run = false, string? Pool = null);

/// <summary>
/// An adoption asked of one record held because OSDU already holds a record at the id its key makes. <c>Run</c> also queues
/// a deliver run scoped to it, as a record's release does, which takes the record over at once.
/// </summary>
public sealed record DeliveryRecordAdoptRequest(bool Run = false, string? Pool = null);

/// <summary>
/// An adoption asked of a flow's records held because OSDU already holds a record at the id each one's key makes: those
/// <c>Keys</c> names, every one <c>Filter</c> matches (resolved when it is asked for, and refused when it no longer matches
/// the <c>Expected</c> count the operator was shown), or, with neither, every such record of the flow, of every interface
/// when the request names none. <c>Confirm</c> is the partition the ledgers are kept in, typed back. A record held for
/// another reason is left. <c>Run</c> also queues a deliver run of the flow, which takes the records over.
/// </summary>
public sealed record DeliveryFlowAdoptRequest(
    IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected, string? Confirm, bool Run = false, string? Pool = null);

/// <summary>
/// What an adoption asked: how many records it reached (named, matched, or held for an id OSDU holds), how many it asked
/// to adopt, and the deliver run it queued, which takes them over, when asked to.
/// </summary>
public sealed record DeliveryAdoptResult(int Selected, int Asked, Guid? RunId);

/// <summary>
/// Redeliver: <c>scope</c> is the part to send again, <c>all</c> by default, <c>record</c>, or <c>files</c> or <c>bulk</c>
/// as the record's route sends them (<c>metadata</c> and <c>payload</c> name the same parts); <c>run</c> queues the deliver
/// run that sends it.
/// </summary>
public sealed record DeliveryRedeliverRequest(string? Scope = null, bool Run = true, string? Pool = null);

public sealed record DeliveryRedeliverResult(int Marked, Guid? RunId);

/// <summary>
/// A redelivery of many of a flow's records: those <c>Keys</c> names, every one <c>Filter</c> matches (resolved when it is
/// asked for, and refused when it no longer matches the <c>Expected</c> count the operator was shown), or, with <c>All</c>,
/// every record the flow has delivered. Exactly one of the three names the records: a request that names none is refused
/// rather than taken for every record. <c>Scope</c> is the part to send again, as the flow's route sends it (all when it
/// names none). The records are marked under the caller's name, and with <c>Run</c> a deliver run is queued that plans
/// and sends them; without it the flow's next run does.
/// </summary>
public sealed record DeliveryFlowRedeliverRequest(
    string? Scope, IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected, bool Run = true, string? Pool = null, bool All = false);

/// <summary>
/// A request to bring many of a flow's records up to date: rendered again under the rules of now and sent only where they
/// render differently. The records are named as a redelivery of many names them (<see cref="DeliveryFlowRedeliverRequest"/>):
/// by <c>Keys</c>, by <c>Filter</c> with the <c>Expected</c> count, or with <c>All</c> every record the flow has delivered.
/// </summary>
public sealed record DeliveryRerenderRequest(
    IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected, bool Run = true, string? Pool = null, bool All = false);

/// <summary>How many records were asked to be brought up to date, and the deliver run that brings them, when one was queued.</summary>
public sealed record DeliveryRerenderResult(int Marked, Guid? RunId);

/// <summary>
/// The plan run that says what bringing a selection up to date would send: the run to watch, how many delivered records of
/// the selection it checks (at most <see cref="DeliveryRerender.PreviewRecords"/>), and how many the selection holds, with
/// whether that count stopped at the listing's bound.
/// </summary>
public sealed record DeliveryRerenderPreviewAccepted(Guid RunId, int Checked, long Selected, bool SelectedCapped);

/// <summary>The bounds of bringing records up to date from the API.</summary>
public static class DeliveryRerender
{
    /// <summary>The most records a preview checks: a plan run is scoped to at most this many records.</summary>
    public const int PreviewRecords = DeliveryRunPayload.MaxRecordKeys;
}

/// <summary>
/// Records already removed from OSDU to delete from the ledger: those <c>Keys</c> names, every one <c>Filter</c> matches (refused
/// when it no longer matches the <c>Expected</c> count the operator was shown), or, with <c>All</c>, every record the ledger
/// marks deleted. Exactly one of the three names the records: a request that names none is refused.
/// </summary>
public sealed record DeliveryLedgerPurgeRequest(IReadOnlyList<Guid>? Keys, DeliveryRecordFilterDto? Filter, int? Expected, bool All = false);

/// <summary>What deleting from the ledger did: records asked about, deleted, and left as they were (not removed from OSDU, or busy).</summary>
public sealed record DeliveryLedgerPurgeResult(int Selected, int Purged, int Left, string Summary);

/// <summary>
/// The interventions that act on a flow's records in the ledger under the caller's name: a release, a lift of a
/// removal, an adoption, a redelivery, bringing records up to date (and the plan run that says what that would send)
/// and deleting removed records from the ledger, each with the deliver run it can queue; and what they share: how a
/// request names its records, and the runtime they act in.
/// </summary>
public static partial class DeliveryEndpoints
{
    /// <summary>
    /// Releases a flow's blocked records: the ones the request names by keys, or every one when it says <c>all</c>. A request
    /// that names neither is refused, so a missing body or a client that dropped its keys never releases the whole flow.
    /// </summary>
    private static async Task<Results<Ok<DeliveryReleaseResult>, Accepted<DeliveryReleaseResult>, ProblemHttpResult>> ReleaseFlowAsync(
        Guid pipelineId, DeliveryReleaseRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var (keys, unselected) = await SelectedAsync(db, flow, request?.Keys, null, null, request?.All ?? false, Intervention.Release, ledger, ct).ConfigureAwait(false);
        if (unselected is not null)
        {
            return unselected;
        }

        using var runtime = await InterventionAsync(engine, config, flow, user, ct).ConfigureAwait(false);
        var released = await runtime.ReleaseAsync(keys is null ? RecordReach.Every : RecordReach.Named(keys), ct).ConfigureAwait(false);
        if (request is not { Run: true })
        {
            return TypedResults.Ok(new DeliveryReleaseResult(released));
        }

        // The run reads under the parameter values the flow's last submission ran with, as a scheduled run of it would.
        var latest = await ledger.ListSubmissionsAsync(flow.FlowId, 1, null, ct).ConfigureAwait(false);
        var runId = await EnqueueRunAsync(db, dispatcher, flow, DeliverRun(flow, latest.Count > 0 ? latest[0].ParametersJson : null), request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryReleaseResult(released, runId));
    }

    /// <summary>
    /// Redelivers many of a flow's records: sends again what the scope names whatever their hashes say. Named records (by
    /// keys or by filter) are marked here under the caller's name and a deliver run is queued that sends them, as a release
    /// of many is; every delivered record is asked of a deliver run, whose node marks and sends them in one recorded run.
    /// The run reads under the parameter values the flow's last submission ran with, as a scheduled run of it would.
    /// </summary>
    private static async Task<Results<Ok<DeliveryRedeliverResult>, Accepted<DeliveryRedeliverResult>, ProblemHttpResult>> RedeliverFlowAsync(
        Guid pipelineId, DeliveryFlowRedeliverRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var part = string.IsNullOrWhiteSpace(request?.Scope) ? RedeliverScopes.All : request.Scope.Trim().ToLowerInvariant();
        RedeliverSelection selection;
        try
        {
            selection = RedeliverScopes.Of(part, flow.Flow);
        }
        catch (DeliveryException ex)
        {
            return Invalid($"scope: {ex.Message}");
        }

        var (keys, unselected) = await SelectedAsync(db, flow, request?.Keys, request?.Filter, request?.Expected, request?.All ?? false, Intervention.Redelivery, ledger, ct).ConfigureAwait(false);
        if (unselected is not null)
        {
            return unselected;
        }

        var latest = await ledger.ListSubmissionsAsync(flow.FlowId, 1, null, ct).ConfigureAwait(false);
        var values = latest.Count > 0 ? latest[0].ParametersJson : null;
        if (keys is null && request is not { Run: false })
        {
            // Every delivered record: the node marks them in the run that sends them, however many the flow holds.
            var all = await EnqueueRunAsync(db, dispatcher, flow, new RunParameters
            {
                Operation = DeliveryOperations.Deliver,
                Values = SubmissionValues(values),
                Payload = new DeliveryRunPayload { Redeliver = part, Interface = flow.Flow.Interface }.ToJson(),
            }, request?.Pool, user, ct).ConfigureAwait(false);
            return TypedResults.Accepted($"/api/v1/runs/{all}", new DeliveryRedeliverResult(0, all));
        }

        using var runtime = await InterventionAsync(engine, config, flow, user, ct).ConfigureAwait(false);
        var marked = await runtime.RedeliverAsync(keys, selection, ct).ConfigureAwait(false);
        if (request is { Run: false })
        {
            return TypedResults.Ok(new DeliveryRedeliverResult(marked, null));
        }

        var runId = await EnqueueRunAsync(db, dispatcher, flow, DeliverRun(flow, values), request?.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRedeliverResult(marked, runId));
    }

    /// <summary>
    /// Brings many of a flow's records up to date: renders them again under the rules of now and sends only a part that
    /// renders differently, since their delivered hashes stay (osdu/docs/reference/concepts/removal-and-reversal.md,
    /// Redelivering records). Named records are asked here under the caller's name and a deliver run is queued that
    /// plans them; every delivered record is asked of a deliver run, whose node asks and plans them in one recorded
    /// run. A record OSDU does not hold is left as it is.
    /// </summary>
    private static async Task<Results<Ok<DeliveryRerenderResult>, Accepted<DeliveryRerenderResult>, ProblemHttpResult>> RerenderFlowAsync(
        Guid pipelineId, DeliveryRerenderRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var (keys, unselected) = await SelectedAsync(db, flow, request?.Keys, request?.Filter, request?.Expected, request?.All ?? false, Intervention.Rerender, ledger, ct).ConfigureAwait(false);
        if (unselected is not null)
        {
            return unselected;
        }

        var latest = await ledger.ListSubmissionsAsync(flow.FlowId, 1, null, ct).ConfigureAwait(false);
        var values = latest.Count > 0 ? latest[0].ParametersJson : null;
        if (keys is null && request is not { Run: false })
        {
            var all = await EnqueueRunAsync(db, dispatcher, flow, new RunParameters
            {
                Operation = DeliveryOperations.Deliver,
                Values = SubmissionValues(values),
                Payload = new DeliveryRunPayload { Rerender = true, Interface = flow.Flow.Interface }.ToJson(),
            }, request?.Pool, user, ct).ConfigureAwait(false);
            return TypedResults.Accepted($"/api/v1/runs/{all}", new DeliveryRerenderResult(0, all));
        }

        using var runtime = await InterventionAsync(engine, config, flow, user, ct).ConfigureAwait(false);
        var marked = await runtime.BringUpToDateAsync(keys, ct).ConfigureAwait(false);
        if (request is { Run: false })
        {
            return TypedResults.Ok(new DeliveryRerenderResult(marked, null));
        }

        var runId = await EnqueueRunAsync(db, dispatcher, flow, DeliverRun(flow, values), request?.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRerenderResult(marked, runId));
    }

    /// <summary>
    /// Says what bringing a selection up to date would send, sending nothing: a plan run over the selection's delivered
    /// records (at most <see cref="DeliveryRerender.PreviewRecords"/> of them), each rendered under the rules of now and
    /// decided by its hashes. The run's outcome counts what would go (the record, its payload) and names the first records.
    /// </summary>
    private static async Task<Results<Accepted<DeliveryRerenderPreviewAccepted>, ProblemHttpResult>> PreviewRerenderAsync(
        Guid pipelineId, DeliveryRerenderRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions,
        ILedger ledger, IRunDispatcher dispatcher, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var (keys, unselected) = await SelectedAsync(db, flow, request?.Keys, request?.Filter, request?.Expected, request?.All ?? false, Intervention.Rerender, ledger, ct).ConfigureAwait(false);
        if (unselected is not null)
        {
            return unselected;
        }

        long selected;
        bool capped;
        IReadOnlyList<DeliveryKey> candidates;
        if (keys is null)
        {
            // Every record the flow has delivered: the ledger's own count of them, however many, and the first of them in the
            // listing's order to stand for the rest.
            var (query, invalid) = BuildQuery(new DeliveryRecordFilterDto(RecordStatus.Delivered.ToString().ToLowerInvariant(), null, null, null, null));
            if (query is null)
            {
                return invalid!;
            }

            var stats = await ledger.StatsAsync(flow.FlowId, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            (selected, capped) = (stats.Delivered, false);
            candidates = await ledger.ListKeysAsync(flow.FlowId, query, DeliveryRerender.PreviewRecords, ct).ConfigureAwait(false);
        }
        else
        {
            (selected, capped) = (keys.Count, false);
            candidates = keys.Take(DeliveryRerender.PreviewRecords).ToList();
        }

        // Only a record OSDU holds is brought up to date, and a plan reads a record by the key tuple the ledger stored.
        var records = await ledger.GetRecordsAsync(flow.FlowId, candidates, ct).ConfigureAwait(false);
        var checkable = candidates
            .Where(k => records.TryGetValue(k, out var r) && r.Status == RecordStatus.Delivered && r.TargetId is not null && r.SourceKeyJson is not null)
            .Select(k => k.Value)
            .ToList();
        if (checkable.Count == 0)
        {
            return TypedResults.Problem(
                detail: "None of the selected records is delivered with the source key it was built from, so there is nothing to bring up to date. A held, failed, removed or reverted record is released, or redelivered, instead.",
                statusCode: StatusCodes.Status409Conflict, title: "Nothing to check");
        }

        var latest = await ledger.ListSubmissionsAsync(flow.FlowId, 1, null, ct).ConfigureAwait(false);
        var runId = await EnqueueRunAsync(db, dispatcher, flow, new RunParameters
        {
            Operation = DeliveryOperations.Plan,
            Values = SubmissionValues(latest.Count > 0 ? latest[0].ParametersJson : null),
            Payload = new DeliveryRunPayload { RecordKeys = checkable, Rerender = true, Interface = flow.Flow.Interface }.ToJson(),
        }, null, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRerenderPreviewAccepted(runId, checkable.Count, selected, capped));
    }

    /// <summary>
    /// What a many-record intervention is called in what it answers: its noun and verb, the records <c>all</c> reaches
    /// (<paramref name="Every"/>), and whether it names records by a filter as well as by keys.
    /// </summary>
    private sealed record Intervention(string Noun, string Verb, string Every, bool Filters = true)
    {
        public static Intervention Release { get; } = new("A release", "release", "every blocked record of the interface", Filters: false);

        public static Intervention Redelivery { get; } = new("A redelivery", "redeliver", "every record the flow has delivered");

        public static Intervention Rerender { get; } = new("A request to bring records up to date", "bring up to date", "every record the flow has delivered");

        public static Intervention Purge { get; } = new("Deleting from the ledger", "delete from the ledger", "every record the ledger marks deleted");

        public static Intervention Adoption { get; } = new("An adoption", "adopt", "every record held because OSDU holds one at its id");

        /// <summary>The ways it names its records, as a refusal lists them.</summary>
        public string Ways => Filters ? "by keys, by a filter, or all: true" : "by keys or all: true";
    }

    /// <summary>
    /// Asks, as the caller, a flow's records held because OSDU already holds a record at the id each one's key makes to adopt
    /// it, the partition typed back first (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already
    /// holds): the records the request names by keys, every one its filter matches, or, with neither, every such record of
    /// the interface it names, or of every interface of the source when it names none. A record held for another reason is
    /// left, and the answer counts both. With <c>run</c>, a deliver run is queued (of the whole source for an adoption of every
    /// interface), which takes the records over.
    /// </summary>
    private static async Task<Results<Ok<DeliveryAdoptResult>, Accepted<DeliveryAdoptResult>, ProblemHttpResult>> AdoptFlowAsync(
        Guid pipelineId, DeliveryFlowAdoptRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db,
        DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine, DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var selects = request?.Keys is { Count: > 0 } || request?.Filter is not null;
        IReadOnlyList<FlowContext> ledgers;
        if (selects || interfaceName is not null)
        {
            var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
            if (flow is null)
            {
                return problem!;
            }

            ledgers = [flow];
        }
        else
        {
            var (unbound, problem) = await ResolveSourceAsync(db, documents, pipelineId, ct).ConfigureAwait(false);
            if (unbound is null)
            {
                return problem!;
            }

            if (Bind(unbound, partition, await RegistryForAsync(partitions, unbound.Source, partition, ct).ConfigureAwait(false), out var bound) is { } unbindable)
            {
                return unbindable;
            }

            ledgers = [.. bound.Source.Interfaces.Select(f => new FlowContext(bound.Pipeline, bound.Source, f))];
        }

        List<DeliveryKey>? keys = null;
        if (selects)
        {
            // An adoption of every such record is asked with neither keys nor filter and confirmed by the partition typed back
            // below, so only a selection is resolved here.
            var (selected, unselected) = await SelectedAsync(db, ledgers[0], request?.Keys, request?.Filter, request?.Expected, all: false, Intervention.Adoption, ledger, ct).ConfigureAwait(false);
            if (unselected is not null)
            {
                return unselected;
            }

            keys = selected;
        }

        // Every interface whose ledger keeps records is confirmed before any record is asked, so a confirmation that does not
        // match leaves every interface as it was; an interface no run has kept records of yet has nothing to adopt.
        var reach = keys is null ? RecordReach.Every : RecordReach.Named(keys);
        var runtimes = new List<FlowRuntime>(ledgers.Count);
        var asked = 0;
        try
        {
            var kept = new List<FlowRuntime>(ledgers.Count);
            foreach (var flow in ledgers)
            {
                var runtime = await InterventionAsync(engine, config, flow, user, ct).ConfigureAwait(false);
                runtimes.Add(runtime);
                if (await runtime.KeptPartitionAsync(ct).ConfigureAwait(false) is not null)
                {
                    kept.Add(runtime);
                }
            }

            if (kept.Count == 0)
            {
                var label = ledgers.Count == 1 ? ledgers[0].Flow.Label : ledgers[0].Pipeline.Name;
                return TypedResults.Problem(
                    detail: $"{label} keeps no records in a partition yet, so there is nothing to adopt.",
                    statusCode: StatusCodes.Status409Conflict, title: "Nothing to adopt");
            }

            foreach (var runtime in kept)
            {
                await runtime.ConfirmAdoptionAsync(reach, request?.Confirm, ct).ConfigureAwait(false);
            }

            foreach (var runtime in kept)
            {
                asked += (await runtime.AdoptAsync(reach, request?.Confirm, ct).ConfigureAwait(false)).Count;
            }
        }
        catch (InterventionRefusedException refused)
        {
            return Refused(refused);
        }
        finally
        {
            foreach (var runtime in runtimes)
            {
                runtime.Dispose();
            }
        }

        var selectedCount = keys?.Count ?? asked;
        if (request is not { Run: true } || asked == 0)
        {
            return TypedResults.Ok(new DeliveryAdoptResult(selectedCount, asked, null));
        }

        // The run reads under the parameter values the flow's last submission ran with, as a release's run does; an adoption of
        // every interface runs the whole source, so each interface's records are taken over in the order its waves give.
        var latest = await ledger.ListSubmissionsAsync(ledgers[0].FlowId, 1, null, ct).ConfigureAwait(false);
        var values = latest.Count > 0 ? latest[0].ParametersJson : null;
        var run = ledgers.Count == 1
            ? DeliverRun(ledgers[0], values)
            : new RunParameters { Operation = DeliveryOperations.Deliver, Values = SubmissionValues(values), Payload = new DeliveryRunPayload().ToJson() };
        var runId = await EnqueueRunAsync(db, dispatcher, ledgers[0], run, request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryAdoptResult(selectedCount, asked, runId));
    }

    /// <summary>
    /// Deletes records already removed from OSDU from the ledger, in this process and asking nothing of OSDU
    /// (osdu/docs/reference/concepts/removal-and-reversal.md, Deleting removed records from the ledger): those
    /// <c>keys</c> names, every one <c>filter</c> matches (refused when it no longer matches the <c>expected</c> count
    /// the operator was shown), or, with neither, every record the ledger marks deleted. A record OSDU may still hold
    /// is never deleted; it is counted as left.
    /// </summary>
    private static async Task<Results<Ok<DeliveryLedgerPurgeResult>, ProblemHttpResult>> PurgeRecordsAsync(
        Guid pipelineId, DeliveryLedgerPurgeRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db,
        DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine, DeliveryConfigStore config, ILedger ledger, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        var (keys, unselected) = await SelectedAsync(db, flow, request?.Keys, request?.Filter, request?.Expected, request?.All ?? false, Intervention.Purge, ledger, ct).ConfigureAwait(false);
        if (unselected is not null)
        {
            return unselected;
        }

        using var runtime = await InterventionAsync(engine, config, flow, user, ct).ConfigureAwait(false);
        var summary = await runtime.PurgeFromLedgerAsync(keys, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryLedgerPurgeResult(summary.Selected, summary.Purged, summary.Left, summary.Describe()));
    }

    /// <summary>Deletes one record already removed from OSDU from the ledger; refused with 409 for a record OSDU may still hold.</summary>
    private static async Task<Results<Ok<DeliveryLedgerPurgeResult>, ProblemHttpResult>> PurgeRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine, DeliveryConfigStore config, ILedger ledger,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        if (record.Status != RecordStatus.Deleted)
        {
            return TypedResults.Problem(
                detail: $"The record is {record.Status.ToString().ToLowerInvariant()}, so OSDU may still hold it: only a record removed from OSDU is deleted from the ledger. Remove it from OSDU first.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Not removed from OSDU");
        }

        if (record.Restricted)
        {
            return TypedResults.Problem(
                detail: "The record's access in OSDU is restricted, and the ledger never forgets a restricted record: the next run would deliver it again under the ACL its mapping gives. Lift the restriction first, then delete it from the ledger.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Access restricted");
        }

        if ((await ledger.OpenArtifactsAsync(flow.Flow.Id, [new DeliveryKey(key)], ct).ConfigureAwait(false)).Count is > 0 and var open)
        {
            return TypedResults.Problem(
                detail: string.Create(
                    CultureInfo.InvariantCulture,
                    $"An unfinished delivery of the record left {open} item(s) in OSDU that its undo has not taken back yet; the undo reaches them through the record, so it stays in the ledger. Run the flow's undo, then ask again."),
                statusCode: StatusCodes.Status409Conflict,
                title: "Undo unfinished");
        }

        using var runtime = await InterventionAsync(engine, config, flow, user, ct).ConfigureAwait(false);
        var summary = await runtime.PurgeFromLedgerAsync([new DeliveryKey(key)], ct).ConfigureAwait(false);
        if (summary.Purged == 0)
        {
            return TypedResults.Problem(
                detail: "Work for the record started after it was read, so it was left as it was. Ask again once it has settled.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Record busy");
        }

        return TypedResults.Ok(new DeliveryLedgerPurgeResult(summary.Selected, summary.Purged, summary.Left, summary.Describe()));
    }

    /// <summary>
    /// The records a many-record intervention names: those <paramref name="keys"/> lists, or every one <paramref name="filter"/>
    /// matches, refused when it no longer matches the <paramref name="expected"/> count the operator was shown, at most
    /// <see cref="RemovalLimits.MaxSelection"/> either way; or, with <paramref name="all"/>, null keys: every record the
    /// intervention reaches. Exactly one of the three is given: a request that names none is refused rather than taken for
    /// every record, so a missing body or a client that dropped its selection acts on nothing.
    /// </summary>
    private static async Task<(List<DeliveryKey>? Keys, ProblemHttpResult? Problem)> SelectedAsync(
        CatalogDbContext db, FlowContext flow, IReadOnlyList<Guid>? keys, DeliveryRecordFilterDto? filter, int? expected, bool all, Intervention intervention, ILedger ledger,
        CancellationToken ct)
    {
        var byKeys = keys is { Count: > 0 };
        var ways = (byKeys ? 1 : 0) + (filter is null ? 0 : 1) + (all ? 1 : 0);
        if (ways > 1)
        {
            return (null, Invalid($"{intervention.Noun} names its records one way only: {intervention.Ways}."));
        }

        if (ways == 0)
        {
            return (null, Invalid($"{intervention.Noun} names no records. Name them {intervention.Ways} for {intervention.Every}."));
        }

        if (all)
        {
            return (null, null);
        }

        if (byKeys)
        {
            var named = keys!.Distinct().Select(k => new DeliveryKey(k)).ToList();
            if (named.Count > RemovalLimits.MaxSelection)
            {
                return (null, TypedResults.Problem(
                    detail: $"{intervention.Noun} names at most {RemovalLimits.MaxSelection} records; {named.Count} were selected. Ask it of {intervention.Every} with all: true, or narrow the selection.",
                    statusCode: StatusCodes.Status400BadRequest, title: "Too many records"));
            }

            return (named, null);
        }

        // Neither keys nor all, and exactly one way given: the filter names them.
        var (query, invalid) = await BuildQueryAsync(db, filter!, ct).ConfigureAwait(false);
        if (query is null)
        {
            return (null, invalid!);
        }

        BoundedCount matched;
        try
        {
            matched = await ledger.CountAsync(flow.FlowId, query, RemovalLimits.MaxSelection + 1, ct).ConfigureAwait(false);
        }
        catch (RecordQueryTooBroadException ex)
        {
            return (null, TooBroad(ex.Message));
        }

        if (!matched.Exact || matched.Count > RemovalLimits.MaxSelection)
        {
            return (null, TypedResults.Problem(
                detail: $"The filter matches more than {RemovalLimits.MaxSelection} records as far as the listing counts; ask it of {intervention.Every} with all: true, or narrow the filter.",
                statusCode: StatusCodes.Status409Conflict, title: "Too many records"));
        }

        if (matched.Count == 0)
        {
            return (null, TypedResults.Problem(
                detail: $"The filter matches no records, so there is nothing to {intervention.Verb}.",
                statusCode: StatusCodes.Status409Conflict, title: "Nothing selected"));
        }

        if (expected is { } shown && shown != matched.Count)
        {
            return (null, TypedResults.Problem(
                detail: $"The filter matched {shown} records when it was shown and matches {matched.Count} now. Nothing was asked; check the list and ask again.",
                statusCode: StatusCodes.Status409Conflict, title: "The selection changed"));
        }

        return ([.. await ledger.ListKeysAsync(flow.FlowId, query, RemovalLimits.MaxSelection, ct).ConfigureAwait(false)], null);
    }

    /// <summary>
    /// A deliver run of <paramref name="flow"/>'s interface under the parameter values <paramref name="parametersJson"/> names
    /// (a submission's): what a release asks for when it asks for a run. The run plans every record the release asked to be
    /// planned again, a pass at a time, and sends every record it queued.
    /// </summary>
    internal static RunParameters DeliverRun(FlowContext flow, string? parametersJson) => new()
    {
        Operation = DeliveryOperations.Deliver,
        Values = SubmissionValues(parametersJson),
        Payload = new DeliveryRunPayload { Interface = flow.Flow.Interface }.ToJson(),
    };

    private static async Task<Results<Ok<DeliveryReleaseResult>, Accepted<DeliveryReleaseResult>, ProblemHttpResult>> ReleaseRecordAsync(
        Guid flowId, Guid key, DeliveryRecordReleaseRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        using var runtime = await InterventionAsync(engine, config, flow, user, ct).ConfigureAwait(false);
        var released = await runtime.ReleaseAsync(RecordReach.Named([record.DeliveryKey]), ct).ConfigureAwait(false);
        if (request is not { Run: true })
        {
            return TypedResults.Ok(new DeliveryReleaseResult(released));
        }

        // The release is recorded under the caller's name here; the run reads the record by the key tuple the ledger stored,
        // under the parameter values its last plan ran with, and sends what it renders now.
        var last = record.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Deliver,
            Values = SubmissionValues(last?.ParametersJson),
            Payload = new DeliveryRunPayload { RecordKeys = [key], Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryReleaseResult(released, runId));
    }

    /// <summary>
    /// Lifts, as the caller, the ledger's deletion of one record's key (osdu/docs/reference/flow/removal.md, Delivering a record
    /// again): the key is no longer kept out, and the record is asked to be planned again, so the flow's next deliver run reads
    /// its row and delivers it under its id. With <c>run</c>, a deliver run scoped to the record is queued, which does it at
    /// once. A record whose key the ledger keeps out for no removal answers 409.
    /// </summary>
    private static async Task<Results<Ok<DeliveryLiftResult>, Accepted<DeliveryLiftResult>, ProblemHttpResult>> LiftRemovalAsync(
        Guid flowId, Guid key, DeliveryRecordReleaseRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        using var runtime = await InterventionAsync(engine, config, flow, user, ct).ConfigureAwait(false);
        var lift = await runtime.LiftRemovalsAsync([record.DeliveryKey], ct).ConfigureAwait(false);
        if (lift.Lifted.Count == 0)
        {
            return TypedResults.Problem(
                detail: "The ledger keeps this record's key out for no removal: no removal flow found its source no longer holds it.",
                statusCode: StatusCodes.Status409Conflict, title: "Not removed by its source");
        }

        if (request is not { Run: true })
        {
            return TypedResults.Ok(new DeliveryLiftResult(1, null, lift.MarksKept));
        }

        var last = record.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Deliver,
            Values = SubmissionValues(last?.ParametersJson),
            Payload = new DeliveryRunPayload { RecordKeys = [key], Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryLiftResult(1, runId));
    }

    /// <summary>
    /// Lifts, as the caller, the ledger's deletions of the records a request names, at most 1000 (osdu/docs/reference/flow/removal.md,
    /// Delivering a record again), in one activity naming every record lifted; a key the ledger keeps out for no removal is
    /// passed over and not counted. With <c>run</c>, a deliver run of the flow is queued under the parameter values its last
    /// submission ran with, which delivers them again at once. Refused with 400 for a request that names no key or too many.
    /// </summary>
    private static async Task<Results<Ok<DeliveryLiftResult>, Accepted<DeliveryLiftResult>, ProblemHttpResult>> LiftFlowRemovalsAsync(
        Guid pipelineId, DeliveryLiftRequest? request, [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db,
        DeliveryDocumentLoader documents, IPartitionRegistry partitions, EngineContext engine, DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher,
        ClaimsPrincipal user, CancellationToken ct)
    {
        if (request?.Keys is not { Count: > 0 } named || named.Count > FlowRuntime.MaxLift)
        {
            return Invalid(string.Create(CultureInfo.InvariantCulture, $"keys: name from 1 to {FlowRuntime.MaxLift} records whose removal to lift."));
        }

        var (flow, problem) = await ResolveAsync(db, documents, partitions, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return problem!;
        }

        using var runtime = await InterventionAsync(engine, config, flow, user, ct).ConfigureAwait(false);
        var lift = await runtime.LiftRemovalsAsync(named.Distinct().Select(g => new DeliveryKey(g)).ToList(), ct).ConfigureAwait(false);
        if (request is not { Run: true } || lift.Lifted.Count == 0)
        {
            return TypedResults.Ok(new DeliveryLiftResult(lift.Lifted.Count, null, lift.MarksKept));
        }

        var latest = await ledger.ListSubmissionsAsync(flow.FlowId, 1, null, ct).ConfigureAwait(false);
        var runId = await EnqueueRunAsync(db, dispatcher, flow, DeliverRun(flow, latest.Count > 0 ? latest[0].ParametersJson : null), request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryLiftResult(lift.Lifted.Count, runId, lift.MarksKept));
    }

    /// <summary>
    /// Asks, as the caller, one record held because OSDU already holds a record at the id its key makes to adopt that record
    /// (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already holds): the flow's next deliver run
    /// takes it over at the version OSDU holds then. With <c>run</c>, a deliver run scoped to the record is queued, which does
    /// it at once. A record that is not held so is refused, saying why.
    /// </summary>
    private static async Task<Results<Ok<DeliveryAdoptResult>, Accepted<DeliveryAdoptResult>, ProblemHttpResult>> AdoptRecordAsync(
        Guid flowId, Guid key, DeliveryRecordAdoptRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine,
        DeliveryConfigStore config, ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        using var runtime = await InterventionAsync(engine, config, flow, user, ct).ConfigureAwait(false);
        try
        {
            await runtime.AdoptRecordAsync(record, ct).ConfigureAwait(false);
        }
        catch (InterventionRefusedException refused)
        {
            return Refused(refused);
        }

        if (request is not { Run: true })
        {
            return TypedResults.Ok(new DeliveryAdoptResult(1, 1, null));
        }

        // The run reads the record by the key tuple the ledger stored, under the parameter values its last plan ran with, as
        // a record's release with a run does, and takes it over.
        var last = record.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
        var parameters = new RunParameters
        {
            Operation = DeliveryOperations.Deliver,
            Values = SubmissionValues(last?.ParametersJson),
            Payload = new DeliveryRunPayload { RecordKeys = [key], Interface = flow.Flow.Interface }.ToJson(),
        };
        var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, request.Pool, user, ct).ConfigureAwait(false);
        return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryAdoptResult(1, 1, runId));
    }

    private static async Task<Results<Ok<DeliveryRedeliverResult>, Accepted<DeliveryRedeliverResult>, ProblemHttpResult>> RedeliverAsync(
        Guid flowId, Guid key, DeliveryRedeliverRequest? request, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, EngineContext engine, DeliveryConfigStore config,
        ILedger ledger, IRunDispatcher dispatcher, ClaimsPrincipal user, CancellationToken ct)
    {
        var (flow, record, problem) = await ResolveForRecordAsync(db, osdu, documents, ledger, flowId, key, ct).ConfigureAwait(false);
        if (flow is null || record is null)
        {
            return problem!;
        }

        // The part to send again, named by what it is on the record's route (record, files, bulk), or all of it.
        var part = string.IsNullOrWhiteSpace(request?.Scope) ? RedeliverScopes.All : request.Scope.Trim().ToLowerInvariant();
        RedeliverSelection scope;
        try
        {
            scope = RedeliverScopes.Of(part, flow.Flow);
        }
        catch (DeliveryException ex)
        {
            return TypedResults.Problem(detail: $"scope: {ex.Message}", statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // With a run, the node marks and re-sends in one recorded run (the mark carries the run id and the scope);
        // without one, the mark is made here under the caller's name and the flow's next run sends the record. The run
        // reads the record by the key tuple the ledger stored, under the parameter values its last plan ran with, so a
        // flow whose scope is a parameter reads the row in the record's own scope rather than in the flow's defaults.
        if (request?.Run ?? true)
        {
            var last = record.LastSubmissionId is { } lastId ? await ledger.GetSubmissionAsync(lastId, ct).ConfigureAwait(false) : null;
            var parameters = new RunParameters
            {
                Operation = DeliveryOperations.Deliver,
                Values = SubmissionValues(last?.ParametersJson),
                Payload = new DeliveryRunPayload { RecordKeys = [key], Redeliver = part, Interface = flow.Flow.Interface }.ToJson(),
            };
            var runId = await EnqueueRunAsync(db, dispatcher, flow, parameters, request?.Pool, user, ct).ConfigureAwait(false);
            return TypedResults.Accepted($"/api/v1/runs/{runId}", new DeliveryRedeliverResult(1, runId));
        }

        using var runtime = await InterventionAsync(engine, config, flow, user, ct).ConfigureAwait(false);
        var marked = await runtime.RedeliverAsync([record.DeliveryKey], scope, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryRedeliverResult(marked, null));
    }

    /// <summary>
    /// The engine an intervention runs on in this process: its references resolve from the central configuration first, in
    /// the layer of the flow's partition, exactly as the flow's runs resolve them on a node. So the partition the flow's
    /// ledger is registered in here is the one its runs deliver to (osdu/docs/reference/concepts/ledger.md, Ledgers,
    /// flows and partitions).
    /// </summary>
    internal static async Task<EngineContext> ConfiguredAsync(EngineContext engine, DeliveryConfigStore config, FlowContext flow, CancellationToken ct)
    {
        var configuration = await config.ConfigurationAsync(flow.Pipeline.RepoId, ct).ConfigureAwait(false);
        return engine.WithSuppliedReferences(configuration.For(flow.Flow.Partition));
    }

    /// <summary>
    /// The runtime an operator's action on <paramref name="flow"/>'s records runs in (<see cref="FlowRuntime.ForTarget"/>),
    /// configured as the flow's repository is, with what it does recorded under <paramref name="user"/>'s name.
    /// </summary>
    internal static async Task<FlowRuntime> InterventionAsync(EngineContext engine, DeliveryConfigStore config, FlowContext flow, ClaimsPrincipal user, CancellationToken ct)
    {
        var runtime = FlowRuntime.ForTarget(await ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false), flow.Flow);
        runtime.Actor = RequestActor.Label(user);
        return runtime;
    }

    /// <summary>The answer to a release or an adoption refused before it changed anything (<see cref="InterventionRefusedException"/>).</summary>
    internal static ProblemHttpResult Refused(InterventionRefusedException refused)
    {
        ArgumentNullException.ThrowIfNull(refused);
        return refused.Refusal switch
        {
            InterventionRefusal.UnknownIssue => TypedResults.Problem(detail: refused.Message, statusCode: StatusCodes.Status404NotFound, title: "Not found"),
            InterventionRefusal.Unconfirmed => TypedResults.Problem(
                detail: $"{refused.Message} Type the partition back as confirm.", statusCode: StatusCodes.Status400BadRequest, title: "Confirmation required"),
            InterventionRefusal.ConfirmationDiffers => TypedResults.Problem(
                detail: refused.Message, statusCode: StatusCodes.Status400BadRequest, title: "Confirmation does not match"),
            InterventionRefusal.MovedOn => TypedResults.Problem(detail: refused.Message, statusCode: StatusCodes.Status409Conflict, title: "Not asked"),
            _ => TypedResults.Problem(detail: refused.Message, statusCode: StatusCodes.Status409Conflict, title: "Nothing to adopt"),
        };
    }

    /// <summary>The flow parameter values a submission was opened with; none when it recorded none or is unknown.</summary>
    internal static IReadOnlyDictionary<string, string> SubmissionValues(string? parametersJson)
    {
        try
        {
            return RunParameters.ValuesFromJson(parametersJson);
        }
        catch (SqlFlowException ex)
        {
            throw new DeliveryException($"The submission's recorded parameters are not valid: {ex.Message}", ex);
        }
    }
}
