using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// One delivery record a search found: where it belongs (the flow's pipeline and, for a source, the interface, and for a
/// flow that names its partitions, the partition), how it is identified, its custody state, and the ingestion file and row
/// its newest version came from (the queued version's while work waits, the delivered one's otherwise), which is how an
/// operator who holds a file recognises a record.
/// </summary>
public sealed record DeliveryRecordHitDto(
    Guid DeliveryKey, Guid FlowId, string? FlowName, Guid? PipelineId, string SourceKey, string? Label, string? TargetId,
    string Status, DateTime? LastDeliveredUtc, DateTime UpdatedUtc, string? Interface = null,
    IReadOnlyList<DeliveryRecordMatchDto>? Matched = null, string? SourceFileName = null, long? SourceRowNumber = null,
    string? Partition = null);

/// <summary>
/// One value of a record that the term matched, and what that value is (an identity the mapping declares, a key value,
/// a word of the label, the OSDU id, the ingestion file). A hit says why it is a hit, so an operator searching a well
/// name sees the name they typed rather than having to work out which column answered.
/// </summary>
public sealed record DeliveryRecordMatchDto(string Value, string Kind);

/// <summary>
/// How a record the ledger found is described to a reader that holds no flow: the Records page's lookup and the combined
/// search read the same ledger lookup and name the record the same way, so this is the one place a hit is built.
/// </summary>
internal static class DeliveryRecordHits
{
    /// <summary>The matched values shown beside one hit, so a row says why it is a hit without becoming a list of its own.</summary>
    public const int MaxMatchesShown = 3;

    /// <summary>
    /// The records as hits, each with the pipeline and interface its ledger belongs to, in the order given. With a
    /// <paramref name="term"/>, each hit also carries the values of the record that start with it, read from the same
    /// identity index the lookup seeks, so the page can say which value answered; with <see cref="SearchMode.Contains"/>,
    /// the values of its own that hold the term anywhere, as the lookup matched them (<see cref="ContainedMatches"/>).
    /// </summary>
    public static async Task<IReadOnlyList<DeliveryRecordHitDto>> DescribeAsync(
        CatalogDbContext catalog, OsduDbContext osdu, IReadOnlyList<RecordState> records, CancellationToken ct, string? term = null,
        SearchMode mode = SearchMode.Prefix)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return [];
        }

        var pipelines = await DeliveryPipelines.ForLedgersAsync(catalog, osdu, records.Select(r => r.FlowId).Distinct().ToList(), ct).ConfigureAwait(false);
        var matches = mode == SearchMode.Contains
            ? ContainedMatches(records, term)
            : await MatchesAsync(osdu, records, term, ct).ConfigureAwait(false);
        return records
            .Select(record => Describe(
                record,
                pipelines.GetValueOrDefault(record.FlowId),
                matches.GetValueOrDefault((record.FlowId, record.DeliveryKey.Value))))
            .ToList();
    }

    /// <summary>
    /// Which of each record's identity values the term matched, at most <see cref="MaxMatchesShown"/> per record. One
    /// seek of the same index the lookup used, over the records of the page alone.
    /// </summary>
    private static async Task<Dictionary<(Guid FlowId, Guid DeliveryKey), List<DeliveryRecordMatchDto>>> MatchesAsync(
        OsduDbContext osdu, IReadOnlyList<RecordState> records, string? term, CancellationToken ct)
    {
        var by = new Dictionary<(Guid, Guid), List<DeliveryRecordMatchDto>>();
        if (string.IsNullOrWhiteSpace(term) || Guid.TryParse(term.Trim(), out _))
        {
            // A delivery key matched the record itself, not one of its values; there is nothing to explain.
            return by;
        }

        // The records' own tokens, found through the identity index of each record's ledger: the partition and ledger
        // identity lead it, and the directory says which partition each ledger of the page is kept under.
        var folded = RecordIdentities.Fold(term);
        var keys = records.Select(r => r.DeliveryKey.Value).Distinct().ToList();
        var flows = records.Select(r => r.FlowId).Distinct().ToList();
        var partitions = await osdu.DeliveryLedgers.AsNoTracking()
            .Where(l => flows.Contains(l.FlowId))
            .Select(l => l.PartitionId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var rows = await osdu.DeliveryRecordIdentities.AsNoTracking()
            .Where(i => partitions.Contains(i.PartitionId) && flows.Contains(i.FlowId) && keys.Contains(i.DeliveryKey) && i.Token.StartsWith(folded))
            .OrderBy(i => i.Token)
            .Select(i => new { i.FlowId, i.DeliveryKey, i.Display, i.Kind })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var list = by.TryGetValue((row.FlowId, row.DeliveryKey), out var held) ? held : by[(row.FlowId, row.DeliveryKey)] = [];
            if (list.Count < MaxMatchesShown)
            {
                list.Add(new DeliveryRecordMatchDto(row.Display, row.Kind));
            }
        }

        return by;
    }

    /// <summary>
    /// Which of each record's own values hold the term anywhere: the source key, the label, the OSDU id and the ingestion
    /// file names, the values a contains lookup matches. They are on the records already, so nothing more is read.
    /// </summary>
    private static Dictionary<(Guid FlowId, Guid DeliveryKey), List<DeliveryRecordMatchDto>> ContainedMatches(IReadOnlyList<RecordState> records, string? term)
    {
        var by = new Dictionary<(Guid, Guid), List<DeliveryRecordMatchDto>>();
        if (string.IsNullOrWhiteSpace(term) || Guid.TryParse(term.Trim(), out _))
        {
            // A delivery key matched the record itself, not one of its values; there is nothing to explain.
            return by;
        }

        var wanted = term.Trim();
        foreach (var record in records)
        {
            var found = new (string? Value, string Kind)[]
                {
                    (record.SourceKey, "key"),
                    (record.Label, "label"),
                    (record.TargetId, "osdu"),
                    (record.Origin.FileName, "file"),
                    (record.PendingOrigin.FileName, "file"),
                }
                .Where(v => v.Value is not null && v.Value.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                .Select(v => new DeliveryRecordMatchDto(v.Value!, v.Kind))
                .DistinctBy(m => m.Value)
                .Take(MaxMatchesShown)
                .ToList();
            if (found.Count > 0)
            {
                by[(record.FlowId, record.DeliveryKey.Value)] = found;
            }
        }

        return by;
    }

    /// <summary>One record as a hit; a record whose flow no synced repository holds any more is named by its ledger alone.</summary>
    public static DeliveryRecordHitDto Describe(RecordState record, LedgerPipeline? found, IReadOnlyList<DeliveryRecordMatchDto>? matched = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var origin = record.PendingSourceFileName is not null ? record.PendingOrigin : record.Origin;
        return new DeliveryRecordHitDto(
            record.DeliveryKey.Value, record.FlowId, found?.Pipeline.Name, found?.Pipeline.Id, record.SourceKey,
            record.Label, record.TargetId, record.Status.ToString().ToLowerInvariant(), record.LastDeliveredUtc, record.UpdatedUtc,
            found is { Interface.Length: > 0 } ? found.Interface : null,
            matched is { Count: > 0 } ? matched : null,
            origin.FileName,
            origin.RowNumber,
            record.Partition ?? (found is { Partition.Length: > 0 } ? found.Partition : null));
    }

    /// <summary>
    /// How a hit names where it belongs: the pipeline, the interface of a source, and the partition of a flow that names its
    /// partitions, as its ledger is named (<c>flow/interface@partition</c>).
    /// </summary>
    public static string? Named(LedgerPipeline? found)
    {
        if (found is null)
        {
            return null;
        }

        var named = found.Interface.Length == 0 ? found.Pipeline.Name : $"{found.Pipeline.Name}/{found.Interface}";
        return !found.Bound || found.Partition.Length == 0 ? named : $"{named}@{found.Partition}";
    }
}

/// <summary>
/// The <c>records</c> category the module adds to the control plane's search: a delivery key lands on one record, and an
/// OSDU id, a source key, a label or an origin file name lists the records that start with it, across every flow of the
/// partition the workbench works in (<see cref="WorkbenchPartition"/>), or of every partition when the request names none.
/// The lookup is the ledger's own indexed one, so it answers in milliseconds at production volume and counts no further
/// than its bound, which is what <see cref="SearchContribution.TotalCapped"/> then says.
/// </summary>
public sealed class RecordSearchContributor : ISearchContributor
{
    /// <summary>The category's key: the member the combined search carries it under, and its route segment.</summary>
    public const string CategoryKey = "records";

    private readonly ILedger _ledger;
    private readonly CatalogDbContext _catalog;
    private readonly OsduDbContext _osdu;
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _http;

    public RecordSearchContributor(ILedger ledger, CatalogDbContext catalog, OsduDbContext osdu, Microsoft.AspNetCore.Http.IHttpContextAccessor http)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(http);
        _ledger = ledger;
        _catalog = catalog;
        _osdu = osdu;
        _http = http;
    }

    public string Key => CategoryKey;

    public string Label => "Delivery records";

    /// <summary>The search surface's own read policy is enough: a record hit carries no more than the records page does.</summary>
    public string? RequiredPolicy => null;

    public async Task<SearchContribution> SearchAsync(SearchContributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The ledger's lookup answers from the identity indexes and reads at most its candidate bound, so a page past
        // that bound is empty rather than a scan: the phrase has to be narrowed instead.
        var wanted = (long)request.Page * request.PageSize;
        var take = (int)Math.Min(wanted, RecordListing.LookupCandidateLimit);
        var skip = (request.Page - 1) * request.PageSize;
        var partition = WorkbenchPartition.Of(_http.HttpContext?.Request);
        var found = skip >= RecordListing.LookupCandidateLimit
            ? []
            : await _ledger.LookupAsync(request.Phrase, take, partition: partition, ct: ct).ConfigureAwait(false);
        var page = found.Skip(skip).Take(request.PageSize).ToList();

        // Fewer hits than asked for means no identity index ran into its bound, so that count is exact.
        var total = found.Count < take
            ? new BoundedCount(found.Count, Exact: true)
            : await _ledger.CountLookupAsync(request.Phrase, RecordListing.LookupCandidateLimit, partition: partition, ct: ct).ConfigureAwait(false);

        var pipelines = await DeliveryPipelines.ForLedgersAsync(_catalog, _osdu, page.Select(r => r.FlowId).Distinct().ToList(), ct).ConfigureAwait(false);
        var items = page
            .Select(record =>
            {
                var pipeline = pipelines.GetValueOrDefault(record.FlowId);
                // A record is its flow and its key: the same row read by two flows is two hits, each with its own page.
                var path = DeliveryRecordRoutes.Path(record.FlowId, record.DeliveryKey.Value);
                return new SearchHitDto(
                    path,
                    record.Label ?? record.SourceKey,
                    DeliveryRecordHits.Named(pipeline),
                    "/delivery/records/" + path,
                    DeliveryRecordHits.Describe(record, pipeline));
            })
            .ToList();

        return new SearchContribution(items, total.Count, !total.Exact);
    }
}
