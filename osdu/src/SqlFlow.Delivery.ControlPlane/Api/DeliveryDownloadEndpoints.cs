using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>How many records of a download stand where, and the bytes their files hold.</summary>
public sealed record DeliveryDownloadCountsDto(long Written, long Due, long Problem, long Gone, long Bytes);

/// <summary>
/// One run of a download: the platform run, who asked, how it listed (pages, requests), where it wrote and whether it wrote every
/// record again and why, what the listing changed, and the files it wrote and the records storage did not return.
/// </summary>
public sealed record DeliveryDownloadRunDto(
    long DownloadRunId, int DownloadId, Guid? RunId, string Actor, string Status, DateTime StartedUtc, DateTime? CompletedUtc, string ReadMode,
    string Location, string? Rewrite, long Listed, int Pages, long Requests, long Added, long Changed, long Gone, long Returned, long Written,
    long Bytes, long Missing, string? Error);

/// <summary>One download as the partition keeps it: what it reads, the folder its last completed run wrote, its counts and its last run.</summary>
public sealed record DeliveryDownloadDto(
    int DownloadId, string Partition, string Name, string Kind, string? Query, string ReadMode, string? Location, DateTime CreatedUtc, DateTime UpdatedUtc,
    DateTime? LastCompletedUtc, DeliveryDownloadCountsDto Counts, DeliveryDownloadRunDto? LastRun);

/// <summary>
/// A download a flow declares or keeps: what the flow says of it, whether it still declares it, and the download as the
/// partition keeps it (left out until a run registers it).
/// </summary>
public sealed record DeliveryDownloadEntryDto(string Name, string? Description, string Kind, string? Query, bool Declared, DeliveryDownloadDto? Download);

/// <summary>
/// One download flow in the partition it is read in: the partitions it reads and whether this one is one of them, how it lists
/// OSDU, the folder its downloads' folders are made in as it declares it, the parameters a run takes, its downloads, and what
/// keeps it from being shown (a document the catalog cannot parse, a partition it does not read).
/// </summary>
public sealed record DeliveryDownloadFlowDto(
    Guid PipelineId, Guid RepoId, string Name, string? Description, string? Batch, string? Partition, bool ReadsPartition, IReadOnlyList<string> Partitions,
    Guid? LedgerId, string? Read, string? Location, IReadOnlyList<DeliveryParameterDto> Parameters, string? Problem, IReadOnlyList<DeliveryDownloadEntryDto> Downloads);

/// <summary>
/// One record of a download: its id, kind and the version the listing gave, the file it is written to, where it stands, when it
/// was first listed, last changed and found gone, the version its file holds with when and by which run it was written, the
/// file's size and SHA-256, and why its last write did not happen.
/// </summary>
public sealed record DeliveryDownloadRecordDto(
    long DownloadRecordId, int DownloadId, string TargetId, string? Kind, long? Version, string FileName, string State, DateTime FirstSeenUtc,
    DateTime ChangedUtc, DateTime? GoneUtc, long? WrittenVersion, DateTime? WrittenUtc, long? WrittenRunId, long? Bytes, string? Sha256,
    string? Problem, DateTime? ProblemUtc);

/// <summary>A page of a download's records, with the number the next page starts after; left out on the last page.</summary>
public sealed record DeliveryDownloadRecordPageDto(IReadOnlyList<DeliveryDownloadRecordDto> Items, long? Next);

/// <summary>
/// The report of download flows (osdu/docs/reference/concepts/api.md, Downloads): a download flow's downloads in a partition,
/// each with its counts and last run, a download's runs, and its records a page at a time (keyset paged by the download's own
/// numbering), of one state or all, found by id or file name. Every read answers from the module's database, nothing here
/// talks to OSDU or reads a file, and a download is a run like any other. An answer leaves out what it holds no value for.
/// </summary>
public static class DeliveryDownloadEndpoints
{
    /// <summary>The records a page holds when the request names no limit.</summary>
    public const int DefaultPage = 100;

    /// <summary>The most records one page holds.</summary>
    public const int MaxPage = 1000;

    /// <summary>The runs a download's history lists when the request names no limit.</summary>
    public const int DefaultRuns = 30;

    /// <summary>The most runs one history lists.</summary>
    public const int MaxRuns = 200;

    /// <summary>The longest text a search of the records takes.</summary>
    public const int MaxSearchLength = 500;

    /// <summary>How the answers are written: the API's own conventions, with what holds no value left out.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static void MapReads(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/flows/{pipelineId:guid}/downloads", GetFlowAsync).WithName("GetDeliveryDownloadFlow");
        delivery.MapGet("/downloads/{partition}/{downloadId:int}/runs", ListRunsAsync).WithName("ListDeliveryDownloadRuns");
        delivery.MapGet("/downloads/{partition}/{downloadId:int}/records", ListRecordsAsync).WithName("ListDeliveryDownloadRecords");
    }

    /// <summary>
    /// A download flow in the partition the request reads (its own, else the workbench's, else the one a run of the flow would
    /// read): every download it declares, each with what the partition keeps of it, and those it keeps that the flow no longer declares.
    /// </summary>
    private static async Task<Results<JsonHttpResult<DeliveryDownloadFlowDto>, ProblemHttpResult>> GetFlowAsync(
        Guid pipelineId, string? partition, HttpRequest request, CatalogDbContext db, DeliveryDocumentLoader documents, ILedger ledger,
        IPartitionRegistry registry, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipelineId, ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No pipeline '{pipelineId}'.");
        }

        if (!string.Equals(pipeline.Kind, DownloadFlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status409Conflict, "Not a download flow", $"Pipeline '{pipeline.Name}' is a '{pipeline.Kind}' flow, not a download flow.");
        }

        var named = WorkbenchPartition.Named(partition, request);
        if (named is not null && PartitionProblem(named) is { } bad)
        {
            return bad;
        }

        DownloadFlowDefinition flow;
        try
        {
            flow = documents.ParseDownload(pipeline.Yaml, pipeline.RelativePath);
        }
        catch (FlowValidationException ex)
        {
            return TypedResults.Json(
                new DeliveryDownloadFlowDto(
                    pipeline.Id, pipeline.RepoId, pipeline.Name, null, pipeline.Batch, named, false, [], null, null, null, [],
                    $"The catalog's copy of the flow does not parse: {ex.Message} Re-sync the repository.", []),
                Json);
        }

        var registered = await registry.ReadAsync(ct).ConfigureAwait(false);
        var (bound, kept, why) = await SettleAsync(flow, named, ledger, registered, ct).ConfigureAwait(false);
        var parameters = flow.Parameters.Select(p => new DeliveryParameterDto(p.Key, p.Value.Required, p.Value.Default, p.Value.Description)).ToList();
        if (bound is null)
        {
            return TypedResults.Json(
                new DeliveryDownloadFlowDto(
                    pipeline.Id, pipeline.RepoId, pipeline.Name, flow.Description, flow.Batch ?? pipeline.Batch, kept, false, flow.Served(registered), null,
                    ReadName(flow.Source.Read), flow.Target.Location, parameters, why, Declared(flow, [])),
                Json);
        }

        var held = await ledger.ListDownloadsAsync(bound.LedgerId, ct).ConfigureAwait(false);
        var summaries = new List<DeliveryDownloadDto>(held.Count);
        foreach (var download in held)
        {
            summaries.Add(await SummaryAsync(ledger, download, ct).ConfigureAwait(false));
        }

        return TypedResults.Json(
            new DeliveryDownloadFlowDto(
                pipeline.Id, pipeline.RepoId, pipeline.Name, bound.Description, bound.Batch ?? pipeline.Batch, kept, true, bound.Served(registered), bound.LedgerId,
                ReadName(bound.Source.Read), bound.Target.Location, parameters, null, Declared(bound, summaries)),
            Json);
    }

    /// <summary>A download's latest runs, the newest first.</summary>
    private static async Task<Results<JsonHttpResult<IReadOnlyList<DeliveryDownloadRunDto>>, ProblemHttpResult>> ListRunsAsync(
        string partition, int downloadId, int? limit, ILedger ledger, CancellationToken ct)
    {
        if (limit is < 1 or > MaxRuns)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid limit", $"limit is how many runs the history lists: 1 to {MaxRuns}.");
        }

        var (download, problem) = await DownloadAsync(ledger, partition, downloadId, ct).ConfigureAwait(false);
        if (download is null)
        {
            return problem!;
        }

        var runs = await ledger.ListDownloadRunsAsync(download.Partition, downloadId, limit ?? DefaultRuns, ct).ConfigureAwait(false);
        return TypedResults.Json<IReadOnlyList<DeliveryDownloadRunDto>>(runs.Select(ToDto).ToList(), Json);
    }

    /// <summary>A page of a download's records, of one state or all, found by id or file name when the request searches.</summary>
    private static async Task<Results<JsonHttpResult<DeliveryDownloadRecordPageDto>, ProblemHttpResult>> ListRecordsAsync(
        string partition, int downloadId, string? state, string? q, long? after, int? limit, ILedger ledger, CancellationToken ct)
    {
        var asked = string.IsNullOrWhiteSpace(state) ? null : state.Trim().ToLowerInvariant();
        if (asked is not null && !DownloadRecordStates.IsKnown(asked))
        {
            return Problem(
                StatusCodes.Status400BadRequest, "Unknown state", $"'{state}' is not where a downloaded record stands; it is one of {string.Join(", ", DownloadRecordStates.All)}.");
        }

        if (q is { Length: > MaxSearchLength })
        {
            return Problem(StatusCodes.Status400BadRequest, "Search too long", $"q is the text an id or a file name holds: at most {MaxSearchLength} characters.");
        }

        if (after is < 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid cursor", "after is the next of the page before: a number, zero or more.");
        }

        if (limit is < 1 or > MaxPage)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid limit", $"limit is how many records a page holds: 1 to {MaxPage}.");
        }

        var (download, problem) = await DownloadAsync(ledger, partition, downloadId, ct).ConfigureAwait(false);
        if (download is null)
        {
            return problem!;
        }

        // One more than the page is read, so the answer knows whether another page follows without a read that finds none.
        var take = limit ?? DefaultPage;
        var rows = await ledger.ListDownloadRecordsAsync(download.Partition, downloadId, asked, q, after, take + 1, ct).ConfigureAwait(false);
        var shown = rows.Count > take ? rows.Take(take).ToList() : rows;
        return TypedResults.Json(
            new DeliveryDownloadRecordPageDto(shown.Select(ToDto).ToList(), rows.Count > take ? shown[^1].DownloadRecordId : null),
            Json);
    }

    /// <summary>
    /// Every download the flow declares, in its order, each with what the partition keeps of it; then those the partition keeps
    /// that the flow no longer declares, by name.
    /// </summary>
    private static List<DeliveryDownloadEntryDto> Declared(DownloadFlowDefinition flow, IReadOnlyList<DeliveryDownloadDto> held)
    {
        var byName = held.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var entries = flow.Downloads
            .Select(d => new DeliveryDownloadEntryDto(d.Name, d.Description, d.Kind, d.Query, true, byName.GetValueOrDefault(d.Name)))
            .ToList();
        entries.AddRange(held
            .Where(d => flow.Download(d.Name) is null)
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DeliveryDownloadEntryDto(d.Name, null, d.Kind, d.Query, false, d)));
        return entries;
    }

    /// <summary>
    /// The flow as a run in <paramref name="partition"/> reads it, the partition it is read in, and why it is not read there:
    /// a flow whose partition is its header's is read in the partition its ledger keeps, and is shown in no other.
    /// </summary>
    private static async Task<(DownloadFlowDefinition? Flow, string? Partition, string? Why)> SettleAsync(
        DownloadFlowDefinition flow, string? partition, ILedger ledger, RegisteredPartitions registered, CancellationToken ct)
    {
        if (!flow.Partitioned)
        {
            var kept = (await ledger.GetLedgerAsync(flow.LedgerId, ct).ConfigureAwait(false))?.Partition;
            return partition is null || kept is null || string.Equals(kept, partition, StringComparison.OrdinalIgnoreCase)
                ? (flow, kept ?? partition, null)
                : (null, kept, $"Download flow '{flow.Name}' reads the partition its source.headers name, '{kept}', not '{partition}'.");
        }

        try
        {
            var bound = flow.ForRun(partition, registered);
            return (bound, bound.Partition, null);
        }
        catch (DeliveryException ex)
        {
            return (null, partition, ex.Message);
        }
    }

    private static async Task<DeliveryDownloadDto> SummaryAsync(ILedger ledger, DownloadState download, CancellationToken ct)
    {
        var counts = await ledger.DownloadCountsAsync(download.Partition, download.DownloadId, ct).ConfigureAwait(false);
        var latest = await ledger.ListDownloadRunsAsync(download.Partition, download.DownloadId, 1, ct).ConfigureAwait(false);
        var last = latest.Count > 0 ? latest[0] : null;
        return new DeliveryDownloadDto(
            download.DownloadId, download.Partition, download.Name, download.Kind, download.Query, download.ReadMode, download.Location, download.CreatedUtc,
            download.UpdatedUtc, download.LastCompletedUtc, new DeliveryDownloadCountsDto(counts.Written, counts.Due, counts.Problem, counts.Gone, counts.Bytes),
            last is null ? null : ToDto(last));
    }

    /// <summary>The download a request names, or the problem that answers it: a partition that is none, or a download it does not keep.</summary>
    private static async Task<(DownloadState? Download, ProblemHttpResult? Problem)> DownloadAsync(ILedger ledger, string partition, int downloadId, CancellationToken ct)
    {
        if (PartitionProblem(partition) is { } bad)
        {
            return (null, bad);
        }

        var download = await ledger.GetDownloadAsync(partition.Trim(), downloadId, ct).ConfigureAwait(false);
        return download is null
            ? (null, Problem(StatusCodes.Status404NotFound, "Not found", $"Partition '{partition}' keeps no download {downloadId}."))
            : (download, null);
    }

    private static DeliveryDownloadRunDto ToDto(DownloadRunState r) => new(
        r.DownloadRunId, r.DownloadId, r.RunId, r.Actor, r.Status, r.StartedUtc, r.CompletedUtc, r.ReadMode, r.Location, r.Rewrite, r.Listed, r.Pages,
        r.Requests, r.Added, r.Changed, r.Gone, r.Returned, r.Written, r.Bytes, r.Missing, r.Error);

    private static DeliveryDownloadRecordDto ToDto(DownloadRecordState r) => new(
        r.DownloadRecordId, r.DownloadId, r.TargetId, r.Kind, r.Version, r.FileName, r.State, r.FirstSeenUtc, r.ChangedUtc, r.GoneUtc, r.WrittenVersion,
        r.WrittenUtc, r.WrittenRunId, r.Bytes, r.Sha256, r.Problem, r.ProblemUtc);

    private static ProblemHttpResult? PartitionProblem(string partition)
        => CacheScope.IsPartitionId(partition.Trim())
            ? null
            : Problem(StatusCodes.Status400BadRequest, "No such partition", $"'{partition}' is not a partition: a data-partition-id is letters, digits, underscore, hyphen and dot.");

    private static string ReadName(InventoryRead read) => read == InventoryRead.Storage ? "storage" : "search";

    private static ProblemHttpResult Problem(int status, string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);
}
