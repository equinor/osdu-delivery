using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// The current state of one deliverable: what OSDU holds for it, what is pending, and why it is where it is. A record of a
/// linked template that reads its parent names it (<c>ParentId</c>) and the version of it its delivered and its queued
/// document were rendered from (osdu/docs/reference/flow/mapping-parent.md). <c>Claimed</c> says whether the record claimed
/// its OSDU id for its flow (it queued a document once, or took over the record OSDU held there): a record that was only
/// ever held names the id its key makes, but its flow wrote nothing there, so there is nothing of its flow's to read back.
/// </summary>
public sealed record DeliveryRecordDto(
    Guid DeliveryKey, Guid FlowId, string SourceKey, string? Label, string MappingName, string? RenderContext,
    string? SourceFingerprint, DateTime? SourceModifiedUtc, string? MetadataHash, string? PayloadHash, DateTime? PayloadModifiedUtc, string? TargetId, long? TargetVersion, string Status,
    DateTime? LastDeliveredUtc, DateTime? LastVerifiedUtc, string? LastVerifyOutcome, string? LeaseOwner, DateTime? LeaseExpiresUtc,
    Guid? LastSubmissionId, int AttemptCount, DateTime? NextAttemptUtc, string? LastError, bool HasPendingDocument,
    bool PendingMetadata, bool PendingPayload, string? PendingPayloadLocation, bool Blocked, DateTime CreatedUtc, DateTime UpdatedUtc,
    string? PendingDocumentRef, int? WorkBatch, JsonElement? TargetState, JsonElement? PendingSteps,
    string? SourceFileName, long? SourceRowNumber, DateTime? SourceUpdatedUtc,
    string? PendingSourceFileName, long? PendingSourceRowNumber, DateTime? PendingSourceUpdatedUtc,
    string? SourceKeyJson, DateTime? PlanRequestedUtc,
    string? WaitingFor = null, IReadOnlyList<DeliveryRecordReferenceDto>? References = null, DateTime? SourceInsertedUtc = null,
    string? Partition = null, string? Issue = null, string? ParentId = null, long? ParentVersion = null, long? PendingParentVersion = null,
    string? OccupiedTargetId = null, long? OccupiedTargetVersion = null, bool Adoptable = false, DateTime? AdoptRequestedUtc = null,
    bool Claimed = false, int? RestrictionId = null, DateTime? RestrictedUtc = null,
    int? IncompleteRestrictionId = null, DateTime? IncompleteSinceUtc = null, IReadOnlyList<string>? IncompleteMissing = null, string? IncompleteGroup = null);

/// <summary>
/// The access restriction a record carries (osdu/docs/reference/concepts/access-restriction.md): the registered restriction,
/// its groups (null when the registry no longer names it), when OSDU confirmed it (null while it is asked and unanswered), the
/// ACL the record had before, which lifting it writes back, and the records beside it the restriction reached with it (the
/// records the mapping's enrichments add), which lifting gives the ACL each had.
/// </summary>
public sealed record DeliveryRecordRestrictionDto(
    int RestrictionId, string? Name, string? Description, IReadOnlyList<string>? Viewers, IReadOnlyList<string>? Owners, DateTime? RestrictedUtc,
    IReadOnlyList<string>? FormerViewers, IReadOnlyList<string>? FormerOwners, IReadOnlyList<string>? Beside = null);

/// <summary>
/// The access OSDU holds a record restricted until complete under (osdu/docs/reference/flow/mapping-restricted-access.md): the
/// registered restriction its mapping named (its name, what it is for and its groups; null when the registry no longer names
/// it), since when OSDU holds it so, what the document OSDU holds goes without, one value to a line, and the group every record
/// missing the same shares, which the Records listing narrows to with <c>missing=</c>.
/// </summary>
public sealed record DeliveryRecordIncompleteDto(
    int RestrictionId, string? Name, string? Description, IReadOnlyList<string>? Viewers, IReadOnlyList<string>? Owners, DateTime? SinceUtc,
    IReadOnlyList<string> Missing, string? Group);

/// <summary>An OSDU id a record's pending document refers to, and the property of the record holding it.</summary>
public sealed record DeliveryRecordReferenceDto(string Id, string Property);

/// <summary>A record of the ledger another record waits for, or that waits for it: where it is and how it stands.</summary>
public sealed record DeliveryRecordLinkDto(
    Guid FlowId, Guid DeliveryKey, Guid? PipelineId, string? FlowName, string? Interface, string SourceKey, string? Label, string? TargetId, string Status,
    string? Partition = null);

/// <summary>
/// A flow the record lookup can be narrowed to: the ledger identity its records carry (<c>FlowId</c>, what the lookup's
/// <c>flowId</c> takes), the pipeline and interface (null for the single form) it is named by, and the partition its
/// ledger is kept under. Only an identity that holds records is offered.
/// </summary>
public sealed record DeliveryRecordFlowDto(Guid FlowId, Guid PipelineId, string FlowName, string? Interface, string? Partition = null);

/// <summary>
/// A record with the pipeline (and, for a source, the interface) it belongs to. The pending document itself lives in the
/// submission's work batches on storage, which the nodes read; its reference and batch are on the record. A waiting record
/// names the record it waits for (<c>WaitsOn</c>), and every record lists the records waiting for it (<c>WaitedOnBy</c>,
/// the first <see cref="DeliveryEndpoints.MaxWaitersShown"/>). A record reading its parent names the record of the ledger
/// holding the parent's id (<c>Parent</c>, null when none of the partition's flows delivers it), and every record lists the
/// records reading it as their parent (<c>ReadBy</c>, the first <see cref="DeliveryEndpoints.MaxWaitersShown"/>, a record
/// taken out of OSDU left out): the same minted id in the other namespace, one click away. <c>KeyColumns</c> names the parts
/// of the record's key tuple (<c>SourceKeyJson</c>): its interface's <c>source.record.key</c> as the catalog's copy of the
/// flow declares it now, null when the catalog holds no readable copy of that flow or interface. <c>Partition</c> is the
/// partition a request about the record's flow names, for a flow that names or follows its partitions; null for one whose
/// partition is its header's, which takes none. The partition the record's ledger is kept under, either way, is the
/// record's own.
/// </summary>
public sealed record DeliveryRecordDetailDto(
    DeliveryRecordDto Record, Guid? PipelineId, Guid? RepoId, string? FlowName, string? Interface = null,
    DeliveryRecordLinkDto? WaitsOn = null, IReadOnlyList<DeliveryRecordLinkDto>? WaitedOnBy = null,
    IReadOnlyList<string>? KeyColumns = null, string? Partition = null,
    DeliveryRecordLinkDto? Parent = null, IReadOnlyList<DeliveryRecordLinkDto>? ReadBy = null, DeliveryRemovedKeyDto? RemovedKey = null,
    DeliveryRecordRestrictionDto? Restriction = null, DeliveryRecordIncompleteDto? RestrictedUntilComplete = null);

/// <summary>
/// One delivery try, as the append-only history holds it: its outcome, and every step with what the target returned. A
/// delivered try says why it was sent (<c>Reason</c>, lifted out of its result; osdu/docs/reference/concepts/change-detection.md,
/// Why a record was delivered): its <c>action</c>, its one line <c>summary</c>, every input that moved under <c>triggers</c>
/// and the paths of the document that changed under <c>paths</c>, with <c>recorded</c> true; a delivery made before the ledger
/// kept reasons says <c>{"recorded": false, "summary": "reason not recorded"}</c>, never a guess. Every other try has none.
/// </summary>
public sealed record DeliveryAttemptDto(
    long AttemptId, Guid DeliveryKey, Guid? SubmissionId, Guid? RunId, string Worker, DateTime StartedUtc, DateTime CompletedUtc,
    string Outcome, string Phase, string? MetadataHash, string? PayloadHash, long? TargetVersion, string? Error, JsonElement? Result, int? WorkBatch,
    string? SourceFileName, long? SourceRowNumber, DateTime? SourceUpdatedUtc, DateTime? SourceDeletedUtc = null, long? ParentVersion = null,
    JsonElement? Reason = null);

/// <summary>
/// The listing a removal is aimed at, the same filter the records list is built from. <c>SubmissionId</c> names the
/// records a submission last planned; <c>DeliveredBy</c> names the records a submission delivered, which stay its
/// however many submissions touch them afterwards: the set "the batch we ran" means.
/// </summary>
/// <summary>
/// A record listing's filter, as a listing, a removal and a sync name it. <c>Issue</c> keeps the blocked records one
/// issue keeps blocked: the sixteen characters the flow's issues listing gives it. <c>Incomplete</c> keeps the records OSDU
/// holds restricted until complete (osdu/docs/reference/flow/mapping-restricted-access.md), and <c>Missing</c> those of them
/// restricted for one set of missing values: the sixteen characters a record's <c>incompleteGroup</c> gives it.
/// <c>AccessRestricted</c> keeps the records whose access in OSDU is restricted either way, by an operator or until complete:
/// the records a flow's Restricted figure counts. <c>Trigger</c>, with <c>DeliveredBy</c> or <c>RunId</c>, keeps the records it
/// delivered for one kind of trigger (osdu/docs/reference/concepts/delivery-reasons.md).
/// </summary>
public sealed record DeliveryRecordFilterDto(
    string? Status, string? Search, string? Mode, Guid? SubmissionId, Guid? RunId, bool Drifted = false, Guid? DeliveredBy = null, string? Issue = null,
    bool StepHeld = false, bool Restricted = false, bool Incomplete = false, string? Missing = null, bool AccessRestricted = false, string? Trigger = null);

/// <summary>
/// What the ledger keeps of a record deleted from it after it was removed from OSDU. <c>PlanRequestedUtc</c> is set while the
/// flow's next run is still to read its row again and deliver it as a new record.
/// </summary>
public sealed record DeliveryPurgedRecordDto(
    Guid FlowId, Guid DeliveryKey, string SourceKey, string? Label, string? TargetId, long? LastVersion, int Attempts, long? ActivityId,
    string PurgedBy, DateTime PurgedUtc, DateTime? PlanRequestedUtc = null);

/// <summary>
/// How a record is addressed in the API and the GUI: the ledger's flow id and the delivery key, together. A delivery key
/// alone names one record per flow that reads the row, and the flow id (not the pipeline) is what outlives a flow's
/// removal from its repository, so a record's history stays reachable.
/// </summary>
public static class DeliveryRecordRoutes
{
    /// <summary>The record's path segment: <c>{flowId}/{deliveryKey}</c>.</summary>
    public static string Path(Guid flowId, Guid deliveryKey) => $"{flowId:D}/{deliveryKey:D}";
}

/// <summary>
/// The records of the ledger: a flow's listing and the Records page's lookup across every flow, each a page at a time
/// or as an Excel workbook, the flows the lookup can be narrowed to, and one record's page: what it waits for and what
/// reads it, its access, its attempts, its row through the ingestion tables and its activities.
/// </summary>
public static partial class DeliveryEndpoints
{
    private const int MaxAttempts = 500;

    private static async Task<Results<Ok<PagedResult<DeliveryRecordDto>>, ProblemHttpResult>> ListRecordsAsync(
        Guid pipelineId, string? search, string? mode, string? status, Guid? submissionId, Guid? runId, bool? drifted, Guid? deliveredBy, string? issue, bool? stepHeld, bool? restricted, bool? incomplete, string? missing, bool? accessRestricted, string? trigger, int? page, int? pageSize,
        [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, ILedger ledger, CancellationToken ct)
    {
        var (flow, unresolved) = await ResolveKeptAsync(db, documents, partitions, ledger, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return unresolved!;
        }

        var (query, invalid) = await BuildQueryAsync(db, new DeliveryRecordFilterDto(status, search, mode, submissionId, runId, drifted == true, deliveredBy, issue, stepHeld == true, restricted == true, incomplete == true, missing, accessRestricted == true, trigger), ct).ConfigureAwait(false);
        if (query is null)
        {
            return invalid!;
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var offset = (long)(p - 1) * size;
        if (offset >= RecordListing.CountLimit)
        {
            return TooBroad(
                $"A record listing pages through its first {RecordListing.CountLimit} records, and page {p} of {size} starts past them. " +
                "Narrow the filter (a status, a submission, a run or a search) to reach the records beyond.");
        }

        query = query with { Offset = (int)offset, Max = size };
        try
        {
            var items = await ledger.ListAsync(flow.FlowId, query, ct).ConfigureAwait(false);
            var total = await ledger.CountAsync(flow.FlowId, query, RecordListing.CountLimit + 1, ct).ConfigureAwait(false);
            return TypedResults.Ok(new PagedResult<DeliveryRecordDto>(
                items.Select(ToDto).ToList(), p, size, Math.Min(total.Count, RecordListing.CountLimit), TotalCapped: !total.Exact));
        }
        catch (RecordQueryTooBroadException ex)
        {
            return TooBroad(ex.Message);
        }
    }

    /// <summary>
    /// A record by what an operator holds, across every flow: the Records page's lookup. A delivery key lands on the
    /// record of every flow reading that row; anything else is a prefix over the OSDU id, the source key, the label and
    /// the ingestion file name, or, with <c>mode=contains</c>, the same values matched anywhere. It is narrowed, when
    /// asked, to one custody state, to one flow's ledger identity (<paramref name="flowId"/>, one of
    /// <see cref="ListRecordFlowsAsync"/>), to the records a verify found drifted or missing, and to the records an
    /// enrichment was set aside on: the filters a flow's Records tab has, read across every flow. With no term it is the
    /// ledger's recency listing instead: the records the delivery system last took in or sent, newest first, which is what
    /// the page shows before anything is typed. A prefix and the listing are indexed reads that reach no further than the
    /// candidate bound, so a page past that bound is empty rather than a scan; a contains term reads every record the
    /// rest of the lookup leaves, and is refused as too broad when they are more than the scan limit.
    /// </summary>
    private static async Task<Results<Ok<PagedResult<DeliveryRecordHitDto>>, ProblemHttpResult>> LookupRecordsAsync(
        string? search, string? mode, string? status, Guid? flowId, bool? drifted, bool? stepHeld, bool? restricted, bool? incomplete, string? missing, bool? accessRestricted, [FromQuery] string? partition, int? page, int? pageSize,
        CatalogDbContext db, OsduDbContext osdu, ILedger ledger, HttpRequest request, CancellationToken ct)
    {
        // The partition the page reads: the one the request names, else the workbench's.
        partition = WorkbenchPartition.Named(partition, request);
        var term = search?.Trim() is { Length: > 0 } typed ? typed : null;
        var (query, invalid) = BuildQuery(new DeliveryRecordFilterDto(status, term, mode, null, null, drifted == true, StepHeld: stepHeld == true, Restricted: restricted == true, Incomplete: incomplete == true, Missing: missing, AccessRestricted: accessRestricted == true));
        if (query is null)
        {
            return invalid!;
        }

        var narrowing = new LookupNarrowing(query.Drifted, query.StepHeld, query.Restricted, query.Incomplete, query.Missing, query.AccessRestricted);
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var wanted = (long)p * size;
        var take = (int)Math.Min(wanted, RecordListing.LookupCandidateLimit);
        var skip = (p - 1) * size;
        try
        {
            var found = skip >= RecordListing.LookupCandidateLimit
                ? []
                : term is null
                    ? await ledger.ListRecentAsync(take, query.Status, flowId, partition, narrowing, ct).ConfigureAwait(false)
                    : await ledger.LookupAsync(term, take, query.Status, flowId, partition, query.Mode, narrowing, ct).ConfigureAwait(false);
            var items = found.Skip(skip).Take(size).ToList();

            // Fewer records than asked for means neither the recency index nor an identity index ran into its bound, so that count is exact.
            var total = found.Count < take
                ? new BoundedCount(found.Count, Exact: true)
                : term is null
                    ? await ledger.CountRecentAsync(RecordListing.LookupCandidateLimit, query.Status, flowId, partition, narrowing, ct).ConfigureAwait(false)
                    : await ledger.CountLookupAsync(term, RecordListing.LookupCandidateLimit, query.Status, flowId, partition, query.Mode, narrowing, ct).ConfigureAwait(false);

            var hits = await DeliveryRecordHits.DescribeAsync(db, osdu, items, ct, term, query.Mode).ConfigureAwait(false);
            return TypedResults.Ok(new PagedResult<DeliveryRecordHitDto>(hits, p, size, total.Count, TotalCapped: !total.Exact));
        }
        catch (RecordQueryTooBroadException ex)
        {
            return TooBroad(ex.Message);
        }
    }

    /// <summary>
    /// The records a listing filter of one interface matches, as an Excel workbook (<see cref="RecordExport"/>): at most the
    /// listing's count limit, a broader filter refused as a listing too broad. The file is written whole before the answer
    /// starts, so a refusal or a failure is answered as a problem, never as half a file.
    /// </summary>
    private static async Task<Results<PushStreamHttpResult, ProblemHttpResult>> ExportRecordsAsync(
        Guid pipelineId, string? search, string? mode, string? status, Guid? submissionId, Guid? runId, bool? drifted, Guid? deliveredBy, string? issue, bool? stepHeld, bool? restricted, bool? incomplete, string? missing, bool? accessRestricted, string? trigger,
        [FromQuery(Name = "interface")] string? interfaceName, [FromQuery] string? partition, CatalogDbContext db, DeliveryDocumentLoader documents,
        IPartitionRegistry partitions, ILedger ledger, TimeProvider clock, HttpResponse response, CancellationToken ct)
    {
        var (flow, unresolved) = await ResolveKeptAsync(db, documents, partitions, ledger, pipelineId, interfaceName, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            return unresolved!;
        }

        var (query, invalid) = await BuildQueryAsync(db, new DeliveryRecordFilterDto(status, search, mode, submissionId, runId, drifted == true, deliveredBy, issue, stepHeld == true, restricted == true, incomplete == true, missing, accessRestricted == true, trigger), ct).ConfigureAwait(false);
        if (query is null)
        {
            return invalid!;
        }

        var file = WorkbookBuffer(response);
        try
        {
            await RecordExport.WriteAsync(ledger, flow.Flow, query, clock.GetUtcNow().UtcDateTime, file, ct).ConfigureAwait(false);
        }
        catch (RecordQueryTooBroadException ex)
        {
            return TooBroad(ex.Message);
        }

        return Workbook(file, RecordExport.FileName(flow.Flow, query), ct);
    }

    /// <summary>
    /// The records the Records page's lookup reaches, as an Excel workbook (<see cref="RecordExport.WriteLookup"/>): the same
    /// lookup as <see cref="LookupRecordsAsync"/>, every record it reaches rather than a page of them, each with the flow that
    /// delivers it. The About sheet says when more records match than a lookup reaches. A contains term that reads more
    /// records than the scan limit is refused as too broad, before any of the file is written.
    /// </summary>
    private static async Task<Results<PushStreamHttpResult, ProblemHttpResult>> ExportRecordLookupAsync(
        string? search, string? mode, string? status, Guid? flowId, bool? drifted, bool? stepHeld, bool? restricted, bool? incomplete, string? missing, bool? accessRestricted, [FromQuery] string? partition, CatalogDbContext db,
        OsduDbContext osdu, ILedger ledger, TimeProvider clock, HttpRequest request, HttpResponse response, CancellationToken ct)
    {
        partition = WorkbenchPartition.Named(partition, request);
        var term = search?.Trim() is { Length: > 0 } typed ? typed : null;
        var (query, invalid) = BuildQuery(new DeliveryRecordFilterDto(status, term, mode, null, null, drifted == true, StepHeld: stepHeld == true, Restricted: restricted == true, Incomplete: incomplete == true, Missing: missing, AccessRestricted: accessRestricted == true));
        if (query is null)
        {
            return invalid!;
        }

        var narrowing = new LookupNarrowing(query.Drifted, query.StepHeld, query.Restricted, query.Incomplete, query.Missing, query.AccessRestricted);
        const int reach = RecordListing.LookupCandidateLimit;
        IReadOnlyList<RecordState> found;
        var more = false;
        try
        {
            found = term is null
                ? await ledger.ListRecentAsync(reach, query.Status, flowId, partition, narrowing, ct).ConfigureAwait(false)
                : await ledger.LookupAsync(term, reach, query.Status, flowId, partition, query.Mode, narrowing, ct).ConfigureAwait(false);

            // Fewer records than the lookup reaches means it reached them all; otherwise one more than it reaches is counted.
            if (found.Count >= reach)
            {
                var total = term is null
                    ? await ledger.CountRecentAsync(reach + 1, query.Status, flowId, partition, narrowing, ct).ConfigureAwait(false)
                    : await ledger.CountLookupAsync(term, reach + 1, query.Status, flowId, partition, query.Mode, narrowing, ct).ConfigureAwait(false);
                more = !total.Exact;
            }
        }
        catch (RecordQueryTooBroadException ex)
        {
            return TooBroad(ex.Message);
        }

        // A hit names the flow, interface and partition of its record, in the order the records were given.
        var described = await DeliveryRecordHits.DescribeAsync(db, osdu, found, ct, term, query.Mode).ConfigureAwait(false);
        var hits = found
            .Select((record, i) => new RecordExportHit(
                record, described[i].FlowName, described[i].Interface, described[i].Partition, described[i].Matched?.Select(m => m.Value).ToList() ?? []))
            .ToList();
        var narrowed = flowId is { } id ? (await ledger.GetLedgerAsync(id, ct).ConfigureAwait(false))?.LedgerName ?? id.ToString("D") : null;
        var lookup = new RecordLookupExport(term, query.Status, narrowed, partition, more, query.Mode, narrowing);

        var file = WorkbookBuffer(response);
        RecordExport.WriteLookup(lookup, hits, clock.GetUtcNow().UtcDateTime, file);
        return Workbook(file, RecordExport.FileName(lookup), ct);
    }

    /// <summary>
    /// Where a workbook is written before the answer starts: in memory, and past a threshold in a temporary file, released
    /// with the response.
    /// </summary>
    private static FileBufferingWriteStream WorkbookBuffer(HttpResponse response)
    {
        var file = new FileBufferingWriteStream();
        response.RegisterForDisposeAsync(file);
        return file;
    }

    /// <summary>A workbook written to <paramref name="file"/>, sent as a download named <paramref name="fileName"/>.</summary>
    private static PushStreamHttpResult Workbook(FileBufferingWriteStream file, string fileName, CancellationToken ct)
        => TypedResults.Stream(body => file.DrainBufferAsync(body, ct), RecordExport.MediaType, fileName);

    /// <summary>
    /// The flows the Records page can be narrowed to: every ledger identity the synced repositories name that holds at
    /// least one record, with the pipeline and interface a hit of it is named by, found the way a hit finds them, so the
    /// choice reads as the Flow column does. A source that delivers several interfaces is one choice per interface,
    /// because its records are kept per interface and never summed; an interface that has delivered nothing yet is not a
    /// choice, since narrowing to it could only show an empty page. A ledger no synced pipeline holds any more is not a
    /// choice either; its records still appear under every flow, as "no longer synced". Ordered by flow, then interface.
    /// With <paramref name="partition"/> (or the workbench's), the ledgers of that partition alone, as the ledger's
    /// directory keeps them.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<DeliveryRecordFlowDto>>> ListRecordFlowsAsync(
        [FromQuery] string? partition, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, HttpRequest request, CancellationToken ct)
    {
        partition = WorkbenchPartition.Named(partition, request);
        var named = (await ledger.ListLedgersAsync(partition, ct).ConfigureAwait(false))
            .Where(l => l.Kind == LedgerKinds.Delivery)
            .Select(l => l.FlowId)
            .ToList();
        var holding = await ledger.FlowsWithRecordsAsync(named, ct).ConfigureAwait(false);
        var ledgers = named.Where(holding.Contains).ToList();
        var found = await DeliveryPipelines.ForLedgersAsync(db, osdu, ledgers, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryRecordFlowDto>>(found
            .Select(f => new DeliveryRecordFlowDto(f.Key, f.Value.Pipeline.Id, f.Value.Pipeline.Name, NamedInterface(f.Value), KeptPartition(f.Value)))
            .OrderBy(f => f.FlowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Interface ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Partition ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static async Task<Results<Ok<DeliveryRecordDetailDto>, ProblemHttpResult>> GetRecordAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, DeliveryDocumentLoader documents, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return await ledger.FindPurgedAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false) is { } purged
                ? RecordPurged(purged)
                : RecordNotFound(flowId, key);
        }

        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, record.FlowId, ct).ConfigureAwait(false);
        DeliveryRecordLinkDto? waitsOn = null;
        var holders = record is { Status: RecordStatus.Waiting, WaitingFor: { } waitingFor }
            ? await ledger.ListHoldersAsync(record.FlowId, waitingFor, 1, ct).ConfigureAwait(false)
            : [];
        if (holders.Count > 0)
        {
            waitsOn = await LinkAsync(db, osdu, holders[0], ct).ConfigureAwait(false);
        }

        var waitedOnBy = new List<DeliveryRecordLinkDto>();
        if (record.TargetId is { } targetId)
        {
            foreach (var waiter in await ledger.ListWaitingForAsync(record.FlowId, targetId, MaxWaitersShown, ct).ConfigureAwait(false))
            {
                waitedOnBy.Add(await LinkAsync(db, osdu, waiter, ct).ConfigureAwait(false));
            }
        }

        // The record and its parent are the same minted id in two namespaces: each page links to the other.
        DeliveryRecordLinkDto? parent = null;
        if (record.ParentId is { } parentId
            && (await ledger.ListHoldersAsync(record.FlowId, parentId, 1, ct).ConfigureAwait(false)) is [var holder, ..])
        {
            parent = await LinkAsync(db, osdu, holder, ct).ConfigureAwait(false);
        }

        var readBy = new List<DeliveryRecordLinkDto>();
        if (record.TargetId is { } id)
        {
            foreach (var child in await ledger.ListChildrenOfAsync(record.FlowId, id, MaxWaitersShown, ct).ConfigureAwait(false))
            {
                readBy.Add(await LinkAsync(db, osdu, child, ct).ConfigureAwait(false));
            }
        }

        // The ledger's deletion of the record's key, when a removal flow found its source no longer holds it.
        var removedKey = await ledger.GetRemovedKeyAsync(record.FlowId, record.DeliveryKey, ct).ConfigureAwait(false);
        return TypedResults.Ok(new DeliveryRecordDetailDto(
            ToDto(record), found?.Pipeline.Id, found?.Pipeline.RepoId, found?.Pipeline.Name, NamedInterface(found), waitsOn, waitedOnBy,
            KeyColumnsOf(documents, found), NamedPartition(found), parent, readBy, removedKey is null ? null : DeliveryRemovalEndpoints.ToDto(removedKey),
            await RestrictionOfAsync(ledger, record, ct).ConfigureAwait(false),
            await IncompleteOfAsync(ledger, record, ct).ConfigureAwait(false)));
    }

    /// <summary>
    /// The access OSDU holds <paramref name="record"/> restricted until complete under, with the restriction's registered name
    /// and groups and what the record goes without; null for a record OSDU holds with its own access.
    /// </summary>
    private static async Task<DeliveryRecordIncompleteDto?> IncompleteOfAsync(ILedger ledger, RecordState record, CancellationToken ct)
    {
        if (record.IncompleteRestrictionId is not { } id)
        {
            return null;
        }

        var registered = (await ledger.AccessRestrictionsAsync([id], ct).ConfigureAwait(false)).GetValueOrDefault(id);
        return new DeliveryRecordIncompleteDto(
            id, registered?.Name, registered?.Description, registered?.Acl.Viewers, registered?.Acl.Owners, record.IncompleteSinceUtc,
            MissingLines(record.IncompleteMissing) ?? [], record.IncompleteHash is { } missing ? ProblemSignature.Format(missing) : null);
    }

    /// <summary>The access restriction <paramref name="record"/> carries, with its registered groups and the ACL it had before; null for a record not restricted.</summary>
    private static async Task<DeliveryRecordRestrictionDto?> RestrictionOfAsync(ILedger ledger, RecordState record, CancellationToken ct)
    {
        if (record.RestrictionId is not { } id)
        {
            return null;
        }

        var registered = (await ledger.AccessRestrictionsAsync([id], ct).ConfigureAwait(false)).GetValueOrDefault(id);
        RestrictedAccess? former = null;
        if (record.RestrictedFromAcl is { } kept)
        {
            try
            {
                former = RestrictedAccess.Parse(kept);
            }
            catch (DeliveryException)
            {
                // A kept ACL that does not read is shown as unknown; lifting the restriction says why it cannot give it back.
                former = null;
            }
        }

        return new DeliveryRecordRestrictionDto(
            id, registered?.Name, registered?.Description, registered?.Acl.Viewers, registered?.Acl.Owners, record.RestrictedUtc,
            former?.Record.Viewers, former?.Record.Owners, former is { BesideIds.Count: > 0 } ? former.BesideIds : null);
    }

    /// <summary>
    /// The key columns a ledger identity's interface declares (<c>source.record.key</c>), in the order its records' key
    /// tuples hold their parts; null when the catalog holds no pipeline for it, its copy does not parse, or it no longer
    /// declares the interface.
    /// </summary>
    private static IReadOnlyList<string>? KeyColumnsOf(DeliveryDocumentLoader documents, LedgerPipeline? found)
    {
        if (found is null || Parse(documents, found.Pipeline).Source is not { } source)
        {
            return null;
        }

        return Pick(source.Source, NamedInterface(found), out var flow) is null ? flow!.Source.Record.Key : null;
    }

    /// <summary>How many of the records waiting for one record its page lists.</summary>
    public const int MaxWaitersShown = 50;

    /// <summary>A record as another record's page links to it: its flow, pipeline and interface, and how it stands.</summary>
    private static async Task<DeliveryRecordLinkDto> LinkAsync(CatalogDbContext db, OsduDbContext osdu, RecordState record, CancellationToken ct)
    {
        var found = await DeliveryPipelines.ForLedgerAsync(db, osdu, record.FlowId, ct).ConfigureAwait(false);
        return new DeliveryRecordLinkDto(
            record.FlowId, record.DeliveryKey.Value, found?.Pipeline.Id, found?.Pipeline.Name, NamedInterface(found),
            record.SourceKey, record.Label, record.TargetId, record.Status.ToString().ToLowerInvariant(), record.Partition ?? KeptPartition(found));
    }

    private static async Task<Results<Ok<IReadOnlyList<DeliveryAttemptDto>>, ProblemHttpResult>> ListRecordAttemptsAsync(
        Guid flowId, Guid key, int? max, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return RecordNotFound(flowId, key);
        }

        var attempts = await ledger.ListAttemptsAsync(flowId, record.DeliveryKey, Math.Clamp(max ?? 100, 1, MaxAttempts), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryAttemptDto>>(attempts.Select(ToDto).ToList());
    }

    /// <summary>
    /// The record's row through its ingestion table: when it arrived, and every change of it the ledger recorded, each
    /// with the ingestion run that wrote it and the landing that brought its file in, as the platform recorded them. A
    /// run that reloaded the row without changing it is not a change and is not named. The delivery half of the record's
    /// history is its attempts and activities, which the page reads beside it.
    /// </summary>
    private static async Task<Results<Ok<DeliveryRecordChainDto>, ProblemHttpResult>> GetRecordChainAsync(
        Guid flowId, Guid key, CatalogDbContext db, OsduDbContext osdu, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        return record is null
            ? RecordNotFound(flowId, key)
            : TypedResults.Ok(await RecordChain.OfAsync(
                db, ledger, record, await SourceTableAsync(osdu, record.FlowId, ct).ConfigureAwait(false), ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The ingestion table a flow reads its records from, which is what names the runs that wrote a row's changes. The
    /// sync records it per interface, so it is one read and it survives a document that stopped parsing; a ledger no
    /// synced interface names any more costs the ingestion runs and nothing else, and the changes and their landings
    /// still answer. The declared interface wins over one left behind by an older sync.
    /// </summary>
    private static async Task<string?> SourceTableAsync(OsduDbContext osdu, Guid ledgerFlowId, CancellationToken ct)
        => await osdu.DeliveryInterfaces.AsNoTracking()
            .Where(i => i.LedgerFlowId == ledgerFlowId && i.RecordObject != "")
            .OrderByDescending(i => i.Active)
            .ThenByDescending(i => i.LastSeenUtc)
            .Select(i => i.RecordObject)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private static async Task<Results<Ok<IReadOnlyList<DeliveryActivityDto>>, ProblemHttpResult>> ListRecordActivitiesAsync(
        Guid flowId, Guid key, int? max, ILedger ledger, CancellationToken ct)
    {
        var record = await ledger.GetRecordAsync(flowId, new DeliveryKey(key), ct).ConfigureAwait(false);
        if (record is null)
        {
            return RecordNotFound(flowId, key);
        }

        var activities = await ledger.ListActivitiesAsync(
            new ActivityQuery { FlowId = flowId, DeliveryKey = key, Max = Math.Clamp(max ?? 100, 1, MaxAttempts) }, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryActivityDto>>(activities.Select(ToDto).ToList());
    }

    /// <summary>The partition a ledger is kept under, whichever way its flow names it; null for a ledger not yet placed.</summary>
    private static string? KeptPartition(LedgerPipeline? found) => found is { Partition.Length: > 0 } ? found.Partition : null;

    /// <summary>
    /// A record deleted from the ledger after it was removed from OSDU: not found, saying who deleted it and when, with the
    /// line the ledger keeps of it under <c>purged</c>.
    /// </summary>
    private static ProblemHttpResult RecordPurged(PurgedRecordState purged)
    {
        var dto = new DeliveryPurgedRecordDto(
            purged.FlowId, purged.DeliveryKey.Value, purged.SourceKey, purged.Label, purged.TargetId, purged.LastVersion, purged.Attempts, purged.ActivityId,
            purged.PurgedBy, purged.PurgedUtc, purged.PlanRequestedUtc);
        var osdu = purged.TargetId is { } id
            ? string.Create(CultureInfo.InvariantCulture, $" (OSDU id {id}{(purged.LastVersion is { } v ? $", last version {v}" : string.Empty)})")
            : string.Empty;
        var next = purged.PlanRequestedUtc is null ? string.Empty : " The flow's next run reads its row again and delivers it as a new record.";
        return TypedResults.Problem(
            detail: string.Create(
                CultureInfo.InvariantCulture,
                $"Record {purged.Label ?? purged.SourceKey}{osdu} was deleted from the ledger by {purged.PurgedBy} at {purged.PurgedUtc:u}, after it was removed from OSDU; its {purged.Attempts} attempt(s) went with it.{next}"),
            statusCode: StatusCodes.Status404NotFound,
            title: "Deleted from the ledger",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["purged"] = dto });
    }

    internal static DeliveryRecordDto ToDto(RecordState r) => new(
        r.DeliveryKey.Value, r.FlowId, r.SourceKey, r.Label, r.MappingName, r.RenderContext, r.SourceFingerprint, r.SourceModifiedUtc, r.MetadataHash, r.PayloadHash, r.PayloadModifiedUtc,
        r.TargetId, r.TargetVersion, r.Status.ToString().ToLowerInvariant(), r.LastDeliveredUtc, r.LastVerifiedUtc,
        r.LastVerifyOutcome?.ToString().ToLowerInvariant(), r.LeaseOwner, r.LeaseExpiresUtc, r.LastSubmissionId, r.AttemptCount, r.NextAttemptUtc,
        r.LastError, r.PendingDocumentRef is not null, r.PendingMetadata, r.PendingPayload, r.PendingPayloadLocation, r.Blocked, r.CreatedUtc, r.UpdatedUtc,
        r.PendingDocumentRef, r.WorkBatch, ParseJsonOrNull(r.TargetStateJson), ParseJsonOrNull(r.PendingStepJson),
        // Where the record came from. Traceability is the product: a delivered record says which ingestion file and row
        // it was built from, and a record with work waiting says which file and row that work will be built from.
        r.SourceFileName, r.SourceRowNumber, r.SourceUpdatedUtc,
        r.PendingSourceFileName, r.PendingSourceRowNumber, r.PendingSourceUpdatedUtc,
        r.SourceKeyJson, r.PlanRequestedUtc,
        // What the pending document refers to, and, while the record waits, the record it waits for.
        r.WaitingFor, r.PendingReferences.Select(p => new DeliveryRecordReferenceDto(p.Id, p.Property)).ToList(),
        // When the row first reached the ingestion table, which later changes never move.
        r.SourceInsertedUtc,
        // The partition the record's ledger is kept under, whichever way its flow names it.
        r.Partition,
        // While it is blocked, held or failed: the issue keeping it so, which every record refused for the same reason shares.
        r.ProblemHash is { } problem ? ProblemSignature.Format(problem) : null,
        // The parent a record of a linked template reads, and the versions of it its documents were rendered from.
        r.ParentId, r.ParentVersion, r.PendingParentVersion,
        // While it is held because OSDU already holds a record at the id its key makes: that id and version when the hold kept
        // them, whether an adoption may be asked of it, and when one was.
        r.OccupiedTargetId, r.OccupiedTargetVersion, OccupiedHolds.Adoptable(r), r.AdoptRequestedUtc,
        // Whether its flow wrote to OSDU under the id: only a claimed id is read back or removed.
        r.ClaimedTargetId is not null,
        // While its access in OSDU is restricted: the restriction, and when OSDU confirmed it.
        r.RestrictionId, r.RestrictedUtc,
        // While OSDU holds it restricted until complete: the restriction its mapping named, since when, what the document OSDU
        // holds goes without (one value to a line, as the ledger keeps it), and the group every record missing the same shares.
        r.IncompleteRestrictionId, r.IncompleteSinceUtc, MissingLines(r.IncompleteMissing),
        r.IncompleteHash is { } missing ? ProblemSignature.Format(missing) : null);

    /// <summary>What a record restricted until complete goes without, one value to a line as the ledger keeps it; null for one that goes without nothing.</summary>
    internal static IReadOnlyList<string>? MissingLines(string? missing)
        => string.IsNullOrEmpty(missing) ? null : missing.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
