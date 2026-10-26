using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>What a search asks for (openapi search v2, <c>QueryRequest</c>): the kind, the query, a spatial filter, the order and the fields returned.</summary>
public sealed record OsduSearchQuery
{
    /// <summary>The kind searched, <c>authority:source:entityType:version</c>, wildcards allowed per segment.</summary>
    public required string Kind { get; init; }

    /// <summary>A Lucene query narrowing the kind, or null for every record of it.</summary>
    public string? Query { get; init; }

    /// <summary>The search's <c>spatialFilter</c>, as a document wrote it, or null.</summary>
    public JsonObject? Spatial { get; init; }

    /// <summary>The fields the search orders by, each descending or not; empty leaves the order to the service.</summary>
    public IReadOnlyList<(string Field, bool Descending)> Sort { get; init; } = [];

    /// <summary>The fields each hit is projected onto; empty returns whole hits.</summary>
    public IReadOnlyList<string> ReturnedFields { get; init; } = [];

    /// <summary>The request body of this query with the paging properties a call adds: the page size, and where it starts.</summary>
    internal JsonObject Body(int limit, int offset = 0)
    {
        var body = new JsonObject { ["kind"] = Kind, ["limit"] = limit };
        if (offset > 0)
        {
            body["offset"] = offset;
        }

        if (!string.IsNullOrWhiteSpace(Query))
        {
            body["query"] = Query;
        }

        if (Spatial is not null)
        {
            body["spatialFilter"] = Spatial.DeepClone();
        }

        if (Sort.Count > 0)
        {
            body["sort"] = new JsonObject
            {
                ["field"] = new JsonArray(Sort.Select(s => (JsonNode?)JsonValue.Create(s.Field)).ToArray()),
                ["order"] = new JsonArray(Sort.Select(s => (JsonNode?)JsonValue.Create(s.Descending ? "DESC" : "ASC")).ToArray()),
            };
        }

        if (ReturnedFields.Count > 0)
        {
            body["returnedFields"] = new JsonArray(ReturnedFields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        }

        return body;
    }
}

/// <summary>One page of a cursor search: its hits, valid until the next page is asked for, and the count the first page reports.</summary>
public sealed class OsduSearchPage
{
    internal OsduSearchPage(IReadOnlyList<JsonElement> hits, long? totalCount, int returned)
    {
        Hits = hits;
        TotalCount = totalCount;
        Returned = returned;
    }

    /// <summary>
    /// The page's records not handed out before: a record an earlier page returned, or a read before this one, is left out,
    /// so the pages of a search hand out each record once.
    /// </summary>
    public IReadOnlyList<JsonElement> Hits { get; }

    /// <summary>The records the query matches, as the first page reports it; null on later pages.</summary>
    public long? TotalCount { get; }

    /// <summary>The hits the service returned on this page, repeats included.</summary>
    public int Returned { get; }
}

/// <summary>
/// One group of an aggregation: a distinct value of the field and how many records (for a property of a nested array, how
/// many of the array's objects) hold it. <paramref name="Key"/> is null when the service named no key: it renders a number
/// term's key from Elasticsearch's <c>key_as_string</c>, which only a formatted field (a date, a boolean) carries, so a plain
/// number's group can come back without one (<c>CoreQueryBase.getAggregationFromSearchResponse</c>). That is kept apart
/// from the text <c>null</c>, which is a key like any other.
/// </summary>
public sealed record OsduSearchBucket(string? Key, long Count);

/// <summary>
/// What one page of a search answered (<see cref="OsduSearch.PageAsync"/>): the records the query matches in all, the page's
/// hits, the groups of the field it was asked to aggregate, or, when the service refused the query, its words and nothing
/// else.
/// </summary>
/// <param name="Total">The records the query matches, exactly; zero for a refusal.</param>
/// <param name="Hits">The page's hits, each as its own JSON.</param>
/// <param name="Buckets">The groups of the aggregated field; empty when none was asked for.</param>
/// <param name="Refusal">The service's words when it refused the query (400), redacted and cut short; null otherwise.</param>
public sealed record OsduSearchAnswer(long Total, IReadOnlyList<JsonObject> Hits, IReadOnlyList<OsduSearchBucket> Buckets, string? Refusal);

/// <summary>
/// How a read of every record a search matches pages through them, as a flow's <c>source.paging</c> names it
/// (<c>cursor</c>, the default, or <c>createTime</c>).
/// </summary>
public enum SearchPaging
{
    /// <summary>
    /// Through the search service's cursor (openapi search v2, POST /query_with_cursor): a context the service holds for the
    /// read and moves on with every page it answers, in the order the service or the caller chooses.
    /// </summary>
    Cursor,

    /// <summary>
    /// Through pages of the plain search (openapi search v2, POST /query) in the order of each record's <c>createTime</c> and
    /// then its id, each page asked from the last record the page before it handed out. The service holds nothing for the
    /// read, so a page whose answer was lost is asked again as it was.
    /// </summary>
    CreateTime,
}

/// <summary>
/// The OSDU search service as the module reads it (openapi search v2): the exact count of what a query matches, the distinct
/// values of a field with their counts, one page of hits, and every hit a search matches, paged through the cursor or in
/// createTime order (<see cref="SearchPaging"/>). One implementation serves every reader: a retrieval's pages, a dimension's
/// scans, a cache capture, an assertion's reads and a workflow route's search go through the same requests, the same paging,
/// the same check that a read is whole and the same release of a cursor a read leaves open.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SemaphoreSlim's wait handle is never accessed, so it holds no unmanaged resource to release.")]
public sealed partial class OsduSearch
{
    /// <summary>The largest page the search service returns (openapi search v2, limit).</summary>
    public const int MaxPage = 1000;

    /// <summary>
    /// Pages in a row that bring no record new to the read before the read takes the service as going in circles and ends.
    /// A deployment may hand back the same cursor for every page while the context behind it advances (Azure Data Manager
    /// for Energy did on 2026-09-18), so a page with nothing new, not a repeated cursor, is what says a read is circling;
    /// whether a read that ended so is whole is then for the count to say.
    /// </summary>
    public const int BarrenPages = 3;

    /// <summary>
    /// The reads one cursor search is given: the first, and one more when the first failed part way or came back with fewer
    /// records than the search matches. A search two reads cannot read whole fails, rather than hand on part of what it matches.
    /// </summary>
    public const int MaxReads = 2;

    private readonly OsduHttpClient _client;
    private readonly string _queryPath;
    private readonly string _cursorPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim? _requests;

    /// <param name="client">The client every request is sent through.</param>
    /// <param name="queryPath">The plain search (openapi search v2, POST /query).</param>
    /// <param name="cursorPath">The cursor search (openapi search v2, POST /query_with_cursor).</param>
    /// <param name="logger">Where the reads say what they did.</param>
    /// <param name="paging">How <see cref="PagesAsync"/> pages a search whose order the caller leaves open.</param>
    /// <param name="requestsAtOnce">
    /// The most requests this search sends at once, every reader using it together (a dimension build's dimensions share
    /// one); 0 bounds none, leaving each reader to bound its own.
    /// </param>
    public OsduSearch(OsduHttpClient client, string queryPath, string cursorPath, ILogger logger, SearchPaging paging = SearchPaging.Cursor, int requestsAtOnce = 0)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorPath);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegative(requestsAtOnce);
        if (!Enum.IsDefined(paging))
        {
            throw new ArgumentOutOfRangeException(nameof(paging), paging, "Not a way a search is paged.");
        }

        _client = client;
        _queryPath = queryPath;
        _cursorPath = cursorPath;
        _logger = logger;
        _requests = requestsAtOnce > 0 ? new SemaphoreSlim(requestsAtOnce, requestsAtOnce) : null;
        RequestsAtOnce = requestsAtOnce;
        Paging = paging;
    }

    /// <summary>How <see cref="PagesAsync"/> pages a search whose order the caller leaves open.</summary>
    public SearchPaging Paging { get; }

    /// <summary>The most requests this search sends at once, every reader together; 0 when it bounds none.</summary>
    public int RequestsAtOnce { get; }

    /// <summary>
    /// Sends one request once a place is free among the <see cref="RequestsAtOnce"/> this search allows, and frees it when the
    /// answer is in; at once when it bounds none. A place is held only while a request is sent, never while a reader waits on
    /// anything else, so readers sharing the search never wait on each other's work.
    /// </summary>
    private async Task<HttpFetchResult> SendAsync(Func<CancellationToken, Task<HttpFetchResult>> send, CancellationToken ct)
    {
        if (_requests is null)
        {
            return await send(ct).ConfigureAwait(false);
        }

        await _requests.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await send(ct).ConfigureAwait(false);
        }
        finally
        {
            _requests.Release();
        }
    }

    /// <summary>
    /// <paramref name="filter"/> joined with <paramref name="query"/>, as one search: the filter alone when the query is empty
    /// or matches everything.
    /// </summary>
    public static string Within(string? query, string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        return string.IsNullOrWhiteSpace(query) || query.Trim() == "*" ? filter : $"({query.Trim()}) AND ({filter})";
    }

    /// <summary>
    /// The furthest an offset page reaches: Elasticsearch's default <c>index.max_result_window</c>, past which a page of
    /// POST /query is refused and only the cursor search reads on (openapi search v2, <c>limit</c>).
    /// </summary>
    public const int MaxWindow = 10_000;

    /// <summary>How much of a refusal's body an answer quotes.</summary>
    private const int RefusalPreview = 500;

    /// <summary>The exact number of records the query matches (openapi search v2, POST /query with trackTotalCount).</summary>
    public async Task<long> CountAsync(OsduSearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var answer = await QueryAsync(query, 0, 1, null, refusalAnswers: false, ct).ConfigureAwait(false);
        return answer.Total;
    }

    /// <summary>
    /// One page of what the query matches, <paramref name="limit"/> hits from <paramref name="offset"/> on (openapi search
    /// v2, POST /query with trackTotalCount and offset), with the groups of <paramref name="aggregateBy"/> when one is named.
    /// The page ends inside <see cref="MaxWindow"/>. A query the service refuses (400) is answered with its words rather than
    /// thrown, so a reader can ask a plainer one; anything else that keeps an answer from coming fails the call.
    /// </summary>
    public Task<OsduSearchAnswer> PageAsync(OsduSearchQuery query, int offset, int limit, string? aggregateBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (offset + limit > MaxWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"A page ends within the first {MaxWindow} hits; offset {offset} and limit {limit} reach past them.");
        }

        return QueryAsync(query, offset, Math.Min(limit, MaxPage), aggregateBy, refusalAnswers: true, ct);
    }

    /// <summary>
    /// The distinct values of <paramref name="field"/> among the records the query matches, each with its count (openapi
    /// search v2, POST /query with aggregateBy), and the query's total. The service decides how many groups it returns.
    /// </summary>
    public async Task<(long Total, IReadOnlyList<OsduSearchBucket> Buckets)> AggregateAsync(OsduSearchQuery query, string field, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        var answer = await QueryAsync(query, 0, 1, field, refusalAnswers: false, ct).ConfigureAwait(false);
        return (answer.Total, answer.Buckets);
    }

    /// <summary>The first <paramref name="limit"/> hits the query matches (openapi search v2, POST /query), each as its own JSON, and the total.</summary>
    public async Task<(long Total, IReadOnlyList<JsonObject> Hits)> FirstAsync(OsduSearchQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var answer = await QueryAsync(query, 0, Math.Clamp(limit, 1, MaxPage), null, refusalAnswers: false, ct).ConfigureAwait(false);
        return (answer.Total, answer.Hits);
    }

    private async Task<OsduSearchAnswer> QueryAsync(
        OsduSearchQuery query, int offset, int limit, string? aggregateBy, bool refusalAnswers, CancellationToken ct)
    {
        var url = _client.Url(_queryPath);
        var body = query.Body(limit, offset);
        body["trackTotalCount"] = true;
        if (aggregateBy is not null)
        {
            body["aggregateBy"] = aggregateBy;
        }

        var result = await SendAsync(token => _client.SendJsonAsync(HttpMethod.Post, url, body, refusalAnswers ? Refusals : null, token, idempotent: true), ct).ConfigureAwait(false);
        if (result.Status == System.Net.HttpStatusCode.BadRequest)
        {
            return new OsduSearchAnswer(0, [], [], RefusalOf(result));
        }

        var root = OsduHttpClient.ParseJson(result, url);
        var total = root.TryGetProperty("totalCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var value)
            ? value
            : throw new DeliveryException($"{url.AbsolutePath} did not report totalCount for kind {query.Kind}.");
        var hits = new List<JsonObject>();
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in results.EnumerateArray())
            {
                if (JsonNode.Parse(hit.GetRawText()) is JsonObject obj)
                {
                    hits.Add(obj);
                }
            }
        }

        var buckets = new List<OsduSearchBucket>();
        if (aggregateBy is not null && root.TryGetProperty("aggregations", out var aggregations) && aggregations.ValueKind == JsonValueKind.Array)
        {
            foreach (var bucket in aggregations.EnumerateArray())
            {
                if (bucket.ValueKind == JsonValueKind.Object
                    && bucket.TryGetProperty("count", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var c))
                {
                    var key = !bucket.TryGetProperty("key", out var named) || named.ValueKind == JsonValueKind.Null
                        ? null
                        : named.ValueKind == JsonValueKind.String ? named.GetString() : named.GetRawText();
                    buckets.Add(new OsduSearchBucket(key, c));
                }
            }
        }

        return new OsduSearchAnswer(total, hits, buckets, null);
    }

    /// <summary>The status a page of the search answers with its words rather than fails on: the service refusing the query.</summary>
    private static readonly IReadOnlySet<int> Refusals = new HashSet<int> { 400 };

    /// <summary>The words of a service that refused a query, redacted and cut short.</summary>
    private static string RefusalOf(HttpFetchResult result)
    {
        var said = HeaderRedaction.RedactMessage(result.BodyText.ReplaceLineEndings(" ").Trim());
        return said.Length <= RefusalPreview ? said : said[..RefusalPreview] + "...";
    }

    /// <summary>
    /// Every record the query matches, a page at a time: each record handed out once, and every record or an exception, never
    /// a read that ends short. A page's hits are valid until the next page is asked for. A search whose order the caller
    /// leaves open is paged as <see cref="Paging"/> says; one that names its own order goes through the search cursor, since
    /// only the service can page an order the caller chose.
    /// </summary>
    /// <remarks>
    /// The cursor read is described on <see cref="CursorPagesAsync"/>, the read in createTime order on
    /// <see cref="OrderedPagesAsync"/>.
    /// </remarks>
    /// <exception cref="DeliveryException">The search could not be read whole.</exception>
    public IAsyncEnumerable<OsduSearchPage> PagesAsync(OsduSearchQuery query, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(pageSize, 1, MaxPage);
        return Paging == SearchPaging.CreateTime && query.Sort.Count == 0
            ? OrderedPagesAsync(query, limit, ct)
            : CursorPagesAsync(query, limit, ct);
    }

    /// <summary>
    /// Every record the query matches, a page at a time, through the search cursor (openapi search v2, POST
    /// /query_with_cursor): each record handed out once, and every record or an exception, never a read that ends short.
    /// A page's hits are valid until the next page is asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cursor names a search context the service moves on each time it answers a page, so a page asked for again with the
    /// same cursor is the page after it, and the one whose answer was lost is gone. A page after the first is therefore sent
    /// once. It is repeated only after a status that says the service refused it unread (401, when the token is renewed;
    /// 408, 425 and 429, after the wait the flow's retry policy allows), since then the context has not moved. Any other
    /// failure (a timeout, a dropped connection, an answer cut off or not JSON, any other status) ends the read. The first
    /// page opens a context of its own and is repeated like any read: a context a lost answer opened is never read and
    /// expires.
    /// </para>
    /// <para>
    /// A read that reaches the end is checked against what the search matches: the first page asks for the exact total, and
    /// a read that returned fewer distinct records than that is short. When the first page names no total, or names the
    /// 10,000 an uncounted total stops at, the exact count is asked of the plain search (POST /query, trackTotalCount)
    /// instead. A read that failed or came back short is made again from the first page with a new cursor, once
    /// (<see cref="MaxReads"/>); when that one fails or comes back short as well, the enumeration ends in a
    /// <see cref="DeliveryException"/> naming what each read came to, and nothing built from the pages may be taken as
    /// whole.
    /// </para>
    /// <para>
    /// The pages of a second read hand out only the records the first did not, so a caller meets no record twice and misses
    /// none. A record deleted between the two reads was handed out by the first and stays with the caller, as it would had
    /// it been deleted just after the read. A record is known by its id, so a narrowed search always returns the id, and a
    /// hit without one ends the read as an answer the service got wrong. <see cref="BarrenPages"/> pages in a row that bring
    /// no record new to the read end it, since a service handing back records it already returned is going in circles; the
    /// check against the total then says whether the read was whole. A context the service still holds when a read ends
    /// for any reason (the empty page after the last, a failure, a read going in circles, the caller leaving the loop, an
    /// exception) is released (DELETE /query_with_cursor/{cursor}); only a read the service ended by naming no cursor
    /// leaves nothing to release.
    /// </para>
    /// </remarks>
    /// <exception cref="DeliveryException">No read of <see cref="MaxReads"/> returned every record the search matches.</exception>
    private async IAsyncEnumerable<OsduSearchPage> CursorPagesAsync(OsduSearchQuery query, int limit, [EnumeratorCancellation] CancellationToken ct)
    {
        var url = _client.Url(_cursorPath);
        var asked = WithId(query);
        var seen = new SeenIds();
        var first = true;
        var reads = new List<string>(MaxReads);
        for (var read = 1; ; read++)
        {
            var state = new CursorRead();
            PageAnswer? answer = null;
            string? failure = null;
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    answer?.Dispose();
                    answer = null;
                    var continuing = state.Cursor is not null;
                    var body = asked.Body(limit);
                    if (continuing)
                    {
                        body["cursor"] = state.Cursor;
                    }
                    else
                    {
                        body["trackTotalCount"] = true;
                    }

                    answer = continuing
                        ? await NextPageAsync(url, body, ct).ConfigureAwait(false)
                        : await FirstPageAsync(url, body, ct).ConfigureAwait(false);
                    if (answer.Failure is { } failed)
                    {
                        failure = string.Create(CultureInfo.InvariantCulture, $"page {state.Pages + 1} failed: {failed}");
                        break;
                    }

                    state.Pages++;
                    if (!continuing)
                    {
                        state.Total = answer.Total;
                    }

                    // The cursor the service named last is the context it holds; none means it holds none.
                    state.Cursor = answer.Cursor;

                    // Every hit is checked before any is marked as handed out: a page that fails part way must leave no
                    // record marked that the caller never received, or the next read would pass it over.
                    foreach (var hit in answer.Hits)
                    {
                        if (string.IsNullOrWhiteSpace(IdOf(hit)))
                        {
                            failure = string.Create(CultureInfo.InvariantCulture,
                                $"page {state.Pages} returned {(hit.ValueKind == JsonValueKind.Object ? "a record without an id" : "a hit that is not a record")}, so the read cannot tell which record it is");
                            break;
                        }
                    }

                    if (failure is not null)
                    {
                        break;
                    }

                    var kept = new List<JsonElement>(answer.Hits.Count);
                    var fresh = 0;
                    foreach (var hit in answer.Hits)
                    {
                        switch (seen.Mark(IdOf(hit)!, read))
                        {
                            case SeenIds.Met.Never:
                                kept.Add(hit);
                                fresh++;
                                break;
                            case SeenIds.Met.EarlierRead:
                                fresh++;
                                break;
                            case SeenIds.Met.ThisRead:
                                break;
                        }
                    }

                    state.Distinct += fresh;
                    yield return new OsduSearchPage(kept, first ? state.Total : null, answer.Hits.Count);
                    first = false;
                    if (answer.Hits.Count == 0 || state.Cursor is null)
                    {
                        break;
                    }

                    state.Barren = fresh == 0 ? state.Barren + 1 : 0;
                    if (state.Barren >= BarrenPages)
                    {
                        state.Circling = true;
                        break;
                    }
                }
            }
            finally
            {
                answer?.Dispose();
                if (state.Cursor is not null)
                {
                    await CloseCursorAsync(state.Cursor).ConfigureAwait(false);
                }
            }

            if (failure is null)
            {
                if (state.Circling)
                {
                    _logger.LogInformation(
                        "search of {Kind}: {Pages} page(s) in a row brought no record new to read {Read}, so the read ends there after {Total} page(s).",
                        query.Kind, BarrenPages, read, state.Pages);
                }

                var expected = await ExpectedAsync(asked, state.Total, ct).ConfigureAwait(false);
                if (state.Distinct >= expected)
                {
                    if (read > 1)
                    {
                        _logger.LogInformation(
                            "search of {Kind}: read {Read} returned all {Expected} record(s) the search matches, in {Pages} page(s).",
                            query.Kind, read, expected, state.Pages);
                    }

                    yield break;
                }

                failure = string.Create(CultureInfo.InvariantCulture,
                    $"it returned {state.Distinct} of the {expected} record(s) the search matches in {state.Pages} page(s){(state.Circling ? $", the last {BarrenPages} bringing no record it had not returned" : string.Empty)}");
            }

            reads.Add(string.Create(CultureInfo.InvariantCulture, $"read {read}: {failure}"));
            if (read >= MaxReads)
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                    $"The search of kind {query.Kind}{Shown(asked.Query)} could not be read whole in {MaxReads} reads ({string.Join("; ", reads)}). What was read is part of what the search matches, so it is not used as the whole."));
            }

            _logger.LogWarning(
                "search of {Kind}: read {Read} of {Reads}: {Failure}. The search is read again from its first page, and only the records not yet handed on are passed on.",
                query.Kind, read, MaxReads, failure);
        }
    }

    /// <summary>The id of a hit, or null when it carries none.</summary>
    public static string? IdOf(JsonElement hit)
        => hit.ValueKind == JsonValueKind.Object && hit.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    /// <summary>How long a query an error quotes may be.</summary>
    private const int QueryPreview = 200;

    /// <summary>The query as an error names it, cut short when long; nothing for a search of every record of the kind.</summary>
    private static string Shown(string? query)
        => string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : $" for '{(query.Length <= QueryPreview ? query : query[..QueryPreview] + "...")}'";

    /// <summary>The query with the id among the fields a narrowed search returns, since a read knows its records by id.</summary>
    private static OsduSearchQuery WithId(OsduSearchQuery query)
        => query.ReturnedFields.Count == 0 || query.ReturnedFields.Contains("id", StringComparer.Ordinal)
            ? query
            : query with { ReturnedFields = [.. query.ReturnedFields, "id"] };

    /// <summary>
    /// The first page of a read, which opens a context of its own: repeated like any read after a failure the retry policy
    /// takes as passing, since a context a lost answer opened is never read and expires. A failure that outlasts the policy
    /// ends the search, as a second read would begin with the same request; an answer that is not one ends this read.
    /// </summary>
    private async Task<PageAnswer> FirstPageAsync(Uri url, JsonObject body, CancellationToken ct)
    {
        var result = await SendAsync(token => _client.SendJsonAsync(HttpMethod.Post, url, body, null, token, idempotent: true), ct).ConfigureAwait(false);
        return PageAnswer.Of(result, url);
    }

    /// <summary>
    /// A page after the first: sent once, and repeated only after a status saying the service refused it unread, since a
    /// context the service answered has moved on whether or not the answer arrived. Any other failure is this read's.
    /// </summary>
    private async Task<PageAnswer> NextPageAsync(Uri url, JsonObject body, CancellationToken ct)
    {
        try
        {
            var result = await SendAsync(token => _client.SendJsonAsync(HttpMethod.Post, url, body, null, token, idempotent: false, repeatRefused: true), ct).ConfigureAwait(false);
            return PageAnswer.Of(result, url);
        }
        catch (Exception ex) when ((ex is SqlFlowException or HttpRequestException or IOException) && !ct.IsCancellationRequested)
        {
            return PageAnswer.Failed(HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>
    /// How many records a whole read returns: the exact total the first page named, or, when it named none or named the
    /// 10,000 an uncounted total stops at, the exact count of the plain search.
    /// </summary>
    private async Task<long> ExpectedAsync(OsduSearchQuery query, long? reported, CancellationToken ct)
    {
        if (reported is { } total && total != MaxWindow)
        {
            return total;
        }

        var counted = await CountAsync(query with { ReturnedFields = ["id"] }, ct).ConfigureAwait(false);
        _logger.LogDebug(
            "search of {Kind}: the cursor named {Reported} as the total, so the records were counted: {Counted}.",
            query.Kind, reported?.ToString(CultureInfo.InvariantCulture) ?? "nothing", counted);
        return counted;
    }

    /// <summary>Where one read of a cursor search stands.</summary>
    private sealed class CursorRead
    {
        /// <summary>The context the service holds for the read, as its last answer named it; null when it holds none.</summary>
        public string? Cursor { get; set; }

        /// <summary>The exact total the first page named; null when it named none.</summary>
        public long? Total { get; set; }

        /// <summary>The distinct records the read returned, those an earlier read returned too included.</summary>
        public long Distinct { get; set; }

        /// <summary>The pages the service answered.</summary>
        public int Pages { get; set; }

        /// <summary>Pages in a row that brought no record new to the read.</summary>
        public int Barren { get; set; }

        /// <summary>Whether the read ended because <see cref="BarrenPages"/> pages in a row brought nothing new.</summary>
        public bool Circling { get; set; }
    }

    /// <summary>
    /// Releases a search context a read leaves open (openapi search v2, DELETE /query_with_cursor/{cursor}), so it does not
    /// hold index resources until it expires; a service holds only so many at once. The read's own outcome is what its
    /// caller reports; failing to close is logged and nothing more.
    /// </summary>
    private async Task CloseCursorAsync(string cursor)
    {
        try
        {
            var url = _client.Url(CloseTemplate(_cursorPath), cursor);
            await SendAsync(token => _client.SendJsonAsync(HttpMethod.Delete, url, null, new HashSet<int> { 404 }, token), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException)
        {
            _logger.LogWarning("Could not close the search cursor after the read stopped: {Message}", HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>
    /// The path a cursor is released at (openapi search v2, DELETE /query_with_cursor/{cursor}): the cursor search's path with
    /// the cursor as its last segment, and the query the path carries after it, since a query such as
    /// <c>?search_after=true</c> says which kind of context the cursor names and the release must say the same.
    /// </summary>
    internal static string CloseTemplate(string cursorPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorPath);
        var query = cursorPath.IndexOf('?', StringComparison.Ordinal);
        return query < 0
            ? cursorPath.TrimEnd('/') + "/{id}"
            : cursorPath[..query].TrimEnd('/') + "/{id}" + cursorPath[query..];
    }

    /// <summary>A date and time as a Lucene range bound takes it, or <c>*</c> for an open end.</summary>
    public static string LuceneTime(DateTime? value)
        => value is { } v ? v.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : "*";
}

/// <summary>
/// One page of a search as the service answered it (openapi search v2, <c>CursorQueryResponse</c> for the cursor search,
/// <c>QueryResponse</c> for the plain one), why the answer cannot be read as one, or the words of a service that refused the
/// query. The hits belong to the answer's document and are valid until it is disposed.
/// </summary>
internal sealed class PageAnswer : IDisposable
{
    private readonly JsonDocument? _document;

    private PageAnswer(JsonDocument? document, IReadOnlyList<JsonElement> hits, long? total, string? cursor, string? failure, string? refusal = null)
    {
        _document = document;
        Hits = hits;
        Total = total;
        Cursor = cursor;
        Failure = failure;
        Refusal = refusal;
    }

    /// <summary>The service's words when it refused the query (400), redacted and cut short; null otherwise.</summary>
    public string? Refusal { get; }

    /// <summary>An answer saying the service refused the query, in its words.</summary>
    public static PageAnswer Refused(string words) => new(null, [], null, null, null, words);

    /// <summary>The page's hits, as the service returned them.</summary>
    public IReadOnlyList<JsonElement> Hits { get; }

    /// <summary>The records the query matches, when the answer names a count; null otherwise.</summary>
    public long? Total { get; }

    /// <summary>The context that serves the next page; null when the service names none, which says the read is done.</summary>
    public string? Cursor { get; }

    /// <summary>Why the answer is no page, redacted; null for a page.</summary>
    public string? Failure { get; }

    /// <summary>An answer that is no page, for the reason given.</summary>
    public static PageAnswer Failed(string why) => new(null, [], null, null, why);

    /// <summary>
    /// The page a service's answer holds. An answer without results, or with null for them, is a page without hits, which
    /// ends a read and leaves the count to say whether it was whole; an answer that is not a JSON object, or whose results
    /// are something other than a list, is no page.
    /// </summary>
    public static PageAnswer Of(HttpFetchResult result, Uri url)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(url);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.Body);
        }
        catch (JsonException ex)
        {
            return Failed($"{url.AbsolutePath} answered with a body that is not JSON ({ex.Message})");
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return Failed($"{url.AbsolutePath} answered with JSON that is not an object");
        }

        IReadOnlyList<JsonElement> hits = [];
        if (root.TryGetProperty("results", out var results) && results.ValueKind != JsonValueKind.Null)
        {
            if (results.ValueKind != JsonValueKind.Array)
            {
                document.Dispose();
                return Failed($"{url.AbsolutePath} answered with results that are not a list");
            }

            hits = results.EnumerateArray().ToList();
        }

        long? total = root.TryGetProperty("totalCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var t) && t >= 0
            ? t
            : null;
        var cursor = root.TryGetProperty("cursor", out var next) && next.ValueKind == JsonValueKind.String && next.GetString() is { Length: > 0 } named
            ? named
            : null;
        return new PageAnswer(document, hits, total, cursor, null);
    }

    public void Dispose() => _document?.Dispose();
}

/// <summary>
/// The records a cursor search has handed out, each with the read that met it last. A record is kept by a 128-bit digest of
/// its id (the first 16 bytes of its SHA-256) rather than by the id itself, so a search of millions of records holds a few
/// tens of bytes for each rather than the hundreds an id string costs. Two ids sharing a digest is a chance below one in
/// 10^22 among a hundred million records, far below the failures a read guards against.
/// </summary>
internal sealed class SeenIds
{
    /// <summary>The longest id digested without renting a buffer.</summary>
    private const int StackBytes = 512;

    private readonly Dictionary<UInt128, int> _lastRead = [];

    /// <summary>When a record was met before.</summary>
    public enum Met
    {
        /// <summary>Never: the record is new to the search.</summary>
        Never,

        /// <summary>By an earlier read only: handed out already, and new to this read.</summary>
        EarlierRead,

        /// <summary>By this read already: the service returned it twice.</summary>
        ThisRead,
    }

    /// <summary>Marks <paramref name="id"/> as met by <paramref name="read"/>, and says when it was met before.</summary>
    public Met Mark(string id, int read)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ref var last = ref CollectionsMarshal.GetValueRefOrAddDefault(_lastRead, Digest(id), out var known);
        if (!known)
        {
            last = read;
            return Met.Never;
        }

        if (last == read)
        {
            return Met.ThisRead;
        }

        last = read;
        return Met.EarlierRead;
    }

    private static UInt128 Digest(string id)
    {
        var most = Encoding.UTF8.GetMaxByteCount(id.Length);
        byte[]? rented = null;
        try
        {
            Span<byte> bytes = most <= StackBytes ? stackalloc byte[StackBytes] : (rented = ArrayPool<byte>.Shared.Rent(most));
            var length = Encoding.UTF8.GetBytes(id, bytes);
            Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(bytes[..length], hash);
            return BinaryPrimitives.ReadUInt128LittleEndian(hash);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}

/// <summary>What one storage read returned: the records it holds, and the ids it does not hand over.</summary>
/// <param name="Records">The records storage returned, in no particular order.</param>
/// <param name="Missing">The ids asked for that storage did not return: deleted, never written, or hidden from the caller.</param>
/// <param name="Invalid">The ids storage named under invalidRecords, a subset of <paramref name="Missing"/>.</param>
public sealed record StorageRead(IReadOnlyList<JsonElement> Records, IReadOnlyList<string> Missing, IReadOnlyList<string> Invalid)
{
    /// <summary>
    /// The ids storage still named under retryRecords when asked again, a subset of <see cref="Missing"/>: storage did
    /// not say whether it holds them. A reader that must not take an unanswered id for a missing one reads this.
    /// </summary>
    public IReadOnlyList<string> Unanswered { get; init; } = [];
}

/// <summary>
/// Reads records by id from the storage service (openapi storage v2, POST /query/records), a hundred ids per request, and
/// asks once more for the ids storage says to retry. An id storage does not return (deleted, never written, or one the
/// caller may not read, which storage leaves out without a word) is reported missing, never guessed at.
/// </summary>
public sealed class StorageRecords
{
    /// <summary>The most ids one request reads (openapi storage v2, MultiRecordIds.records maxItems).</summary>
    public const int Batch = 100;

    private readonly OsduHttpClient _client;
    private readonly string _path;

    public StorageRecords(OsduHttpClient client, string path)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _client = client;
        _path = path;
    }

    /// <summary>Reads up to <see cref="Batch"/> ids in one request (and one more for the ids storage asks to retry).</summary>
    public async Task<StorageRead> ReadAsync(IReadOnlyList<string> ids, IReadOnlyList<string>? attributes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count > Batch)
        {
            throw new ArgumentException($"One storage read takes at most {Batch} ids.", nameof(ids));
        }

        var url = _client.Url(_path);
        var records = new List<JsonElement>(ids.Count);
        var found = new HashSet<string>(StringComparer.Ordinal);
        var invalid = new List<string>();
        var pending = ids;
        IReadOnlyList<string> unanswered = [];
        for (var attempt = 0; attempt < 2 && pending.Count > 0; attempt++)
        {
            var body = new JsonObject { ["records"] = new JsonArray(pending.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
            if (attributes is { Count: > 0 })
            {
                body["attributes"] = new JsonArray(attributes.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
            }

            // A service that holds none of the ids may answer 404 rather than an empty list; either way, none was found.
            var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
            if ((int)result.Status == 404 || result.Body.Length == 0)
            {
                break;
            }

            using var document = JsonDocument.Parse(result.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new DeliveryException($"{url.AbsolutePath} answered with something other than a JSON object.");
            }

            if (root.TryGetProperty("records", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var record in array.EnumerateArray())
                {
                    if (OsduSearch.IdOf(record) is { } id && found.Add(id))
                    {
                        records.Add(record.Clone());
                    }
                }
            }

            var retry = new List<string>();
            if (root.TryGetProperty("retryRecords", out var retries) && retries.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in retries.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is { } text && !found.Contains(text))
                    {
                        retry.Add(text);
                    }
                }
            }

            if (root.TryGetProperty("invalidRecords", out var rejected) && rejected.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in rejected.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is { } text && !invalid.Contains(text, StringComparer.Ordinal))
                    {
                        invalid.Add(text);
                    }
                }
            }

            pending = retry;
            unanswered = retry;
        }

        return new StorageRead(records, ids.Where(id => !found.Contains(id)).ToList(), invalid) { Unanswered = unanswered };
    }
}
