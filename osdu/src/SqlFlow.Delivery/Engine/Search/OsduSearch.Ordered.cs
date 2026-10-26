using System.Buffers;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>The read of every record a search matches in createTime order (<see cref="SearchPaging.CreateTime"/>).</summary>
public sealed partial class OsduSearch
{
    /// <summary>
    /// About how many records one batch of a read in createTime order holds. A batch closes at the first record past this
    /// many whose createTime moves on, so the records that share a createTime (storage stamps every record one request
    /// creates with the same instant) never straddle two batches, and each batch is a range of createTime the search counts.
    /// </summary>
    public const int BatchRecords = 10_000;

    /// <summary>The system property a read in createTime order orders and bounds its pages by, indexed as a date.</summary>
    private const string CreateTimeField = RecordChanges.CreateTime;

    /// <summary>The system property that orders the records sharing a createTime, indexed as a keyword.</summary>
    private const string IdField = "id";

    /// <summary>The records of a search that hold no createTime, which no range on it reaches.</summary>
    private const string Undated = "(_exists_:" + IdField + " AND NOT _exists_:" + CreateTimeField + ")";

    /// <summary>
    /// Every record the query matches, a page at a time, in the order of each record's createTime and then its id, through
    /// the plain search (openapi search v2, POST /query): each record handed out once, and every record or an exception,
    /// never a read that ends short. A page's hits are valid until the next page is asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read first asks for the newest record the search matches (sorted on createTime and id, descending, one hit) and
    /// reads no further than its createTime, so a record created while the read runs is left to the next one and the read
    /// has a fixed end. Every page is then asked at offset 0, sorted on createTime and id, ascending, narrowed to the records
    /// after the last one the read handed out: <c>createTime</c> past the last createTime, or the same createTime and an id
    /// past the last id. The service holds nothing for the read, so nothing expires between pages, no context counts
    /// against the partition's limit, and the search's 10,000 window never applies. A page is a request like any read, sent
    /// again under the flow's retry policy whatever became of the answer.
    /// </para>
    /// <para>
    /// The order is complete because two records never share a createTime and an id, so a record the search matches from
    /// the start of the read to its end is met exactly once: storage keeps a record's createTime through every version
    /// (storage-core <c>IngestionServiceImpl</c>), and the indexer indexes it as a date and the id as a keyword
    /// (indexer-core <c>TypeMapper</c>). The id is not optional: storage stamps every record one request creates with the
    /// same instant, so a page boundary falls among records sharing a createTime all the time. Every hit is checked against
    /// the order before any is handed out: a hit before the last one, outside the range asked, or a full page that does not
    /// move past the last record ends the read in an exception, since a service that does not keep to the order cannot say
    /// what it skipped.
    /// </para>
    /// <para>
    /// A read that reaches its end is checked against what the search matches up to its end, records without a createTime
    /// included; a read of a single page is checked against that page's own count. The read keeps a tally per batch of
    /// about <see cref="BatchRecords"/> records, each a range of createTime. A read that returned fewer records than the
    /// search matches counts every batch, and the next pass reads again only the batches short of their count, handing out
    /// only the records not yet handed out. A page that fails (after the flow's retry policy, an answer that is not a page,
    /// a hit without an id or a createTime) ends the pass, and the next pass picks up from the last record handed out
    /// rather than from the first page. A read has <see cref="MaxReads"/> passes; when the last fails or comes back short,
    /// the enumeration ends in a <see cref="DeliveryException"/> naming what each pass came to, and nothing built from the
    /// pages may be taken as whole. A record without a createTime cannot be reached in this order, so a search holding one
    /// fails rather than leave it out.
    /// </para>
    /// <para>
    /// A narrowed search returns the id and the createTime the order is kept by; when the caller did not ask for the
    /// createTime, the hits it is handed leave it out.
    /// </para>
    /// </remarks>
    /// <exception cref="DeliveryException">
    /// The service refused the search in this order or did not keep to it, the search holds records without a createTime, or
    /// no pass of <see cref="MaxReads"/> returned every record the search matches.
    /// </exception>
    private async IAsyncEnumerable<OsduSearchPage> OrderedPagesAsync(OsduSearchQuery query, int limit, [EnumeratorCancellation] CancellationToken ct)
    {
        var asked = InCreateTimeOrder(query);
        var strip = query.ReturnedFields.Count > 0 && !query.ReturnedFields.Contains(CreateTimeField, StringComparer.Ordinal);
        var url = _client.Url(_queryPath);
        var (matched, newest) = await NewestAsync(query, asked, ct).ConfigureAwait(false);
        if (newest is not { } end)
        {
            // Nothing matches: the read is whole, and its one page says so.
            yield return new OsduSearchPage([], matched, 0);
            yield break;
        }

        var read = new OrderedRead(end);
        var seen = new SeenIds();
        var passes = new List<string>(MaxReads);
        var pages = 0;
        long? counted = null;
        for (var pass = 1; ; pass++)
        {
            string? failure = null;
            while (failure is null && read.Plan.TryPeek(out var span))
            {
                ct.ThrowIfCancellationRequested();
                var body = (asked with { Query = Within(query.Query, span.Clause()) }).Body(limit);
                if (pages == 0)
                {
                    body["trackTotalCount"] = true;
                }

                PageAnswer answer;
                try
                {
                    answer = await OrderedPageAsync(url, body, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when ((ex is SqlFlowException or HttpRequestException or IOException) && !ct.IsCancellationRequested)
                {
                    answer = PageAnswer.Failed(HeaderRedaction.RedactMessage(ex.Message));
                }

                using (answer)
                {
                    if (answer.Refusal is { } words)
                    {
                        throw Refused(query, words);
                    }

                    var failed = answer.Failure;
                    IReadOnlyList<OrderedKey> keys = [];
                    if (failed is null)
                    {
                        (keys, failed) = KeysOf(answer.Hits);
                    }

                    if (failed is not null)
                    {
                        failure = string.Create(CultureInfo.InvariantCulture, $"page {pages + 1} failed {span.Where()}: {failed}");
                        break;
                    }

                    // Every hit is checked before any is handed out, so a page out of order hands out nothing.
                    CheckOrder(query, span, keys, full: answer.Hits.Count >= limit);
                    var kept = new List<JsonElement>(keys.Count);
                    for (var i = 0; i < keys.Count; i++)
                    {
                        if (seen.Mark(keys[i].Id, pass) == SeenIds.Met.Never)
                        {
                            kept.Add(answer.Hits[i]);
                            read.Take(span, keys[i]);
                        }
                    }

                    pages++;
                    if (pages == 1)
                    {
                        counted = answer.Total;
                    }

                    IReadOnlyList<JsonElement> hits = kept;
                    JsonDocument? rebuilt = null;
                    if (strip)
                    {
                        (hits, rebuilt) = WithoutCreateTime(kept);
                    }

                    using (rebuilt)
                    {
                        yield return new OsduSearchPage(hits, pages == 1 ? matched : null, answer.Hits.Count);
                    }

                    if (keys.Count > 0)
                    {
                        span.Position = keys[^1];
                    }

                    if (answer.Hits.Count < limit)
                    {
                        read.Done(span);
                    }
                }
            }

            if (failure is null)
            {
                // A read of one page is checked against that page's count, unless the search matched more when the read began
                // than the page counted with a createTime: then the records it counted without one are asked for as well.
                var expected = pass == 1 && pages == 1 && counted is { } once && matched <= once
                    ? once
                    : await OrderedCountAsync(query, asked, $"({CreateTimeField}:[* TO {Time(end)}] OR {Undated})", ct).ConfigureAwait(false);
                var handed = read.Handed;
                if (handed >= expected)
                {
                    if (pass > 1)
                    {
                        _logger.LogInformation(
                            "search of {Kind} in createTime order: pass {Pass} returned all {Expected} record(s) the search matches, in {Pages} page(s).",
                            query.Kind, pass, expected, pages);
                    }

                    yield break;
                }

                var shortOf = await ShortBatchesAsync(query, asked, read, ct).ConfigureAwait(false);
                if (shortOf.Count == 0)
                {
                    var undated = await OrderedCountAsync(query, asked, Undated, ct).ConfigureAwait(false);
                    if (undated > 0)
                    {
                        throw UndatedRecords(query, undated);
                    }

                    // The batches, counted after the whole, each hold every record their own count names, and together they
                    // cover the read's range: records left the index between the two counts.
                    _logger.LogInformation(
                        "search of {Kind} in createTime order: the search counted {Expected} record(s) and the read returned {Handed}, yet each of its {Batches} batch(es) holds every record counted in it since, so records left the index while it was counted; the read is whole.",
                        query.Kind, expected, handed, read.Batches.Count);
                    yield break;
                }

                foreach (var batch in shortOf)
                {
                    read.Plan.Enqueue(new OrderedSpan(batch.After, batch.Through, batch));
                }

                failure = string.Create(CultureInfo.InvariantCulture,
                    $"it returned {handed} of the {expected} record(s) the search matches, {shortOf.Count} of its {read.Batches.Count} batch(es) short of their count");
            }

            passes.Add(string.Create(CultureInfo.InvariantCulture, $"pass {pass}: {failure}"));
            if (pass >= MaxReads)
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                    $"The search of kind {query.Kind}{Shown(query.Query)} could not be read whole in createTime order in {MaxReads} passes ({string.Join("; ", passes)}). What was read is part of what the search matches, so it is not used as the whole."));
            }

            _logger.LogWarning(
                "search of {Kind} in createTime order: pass {Pass} of {Passes}: {Failure}. The next pass picks up where this one stopped, or reads again the batches short of their count, and passes on only the records not yet handed on.",
                query.Kind, pass, MaxReads, failure);
        }
    }

    /// <summary>The query sorted on createTime and id, ascending, and, when narrowed, returning both.</summary>
    private static OsduSearchQuery InCreateTimeOrder(OsduSearchQuery query)
    {
        IReadOnlyList<string> fields = query.ReturnedFields.Count == 0
            ? query.ReturnedFields
            : [.. query.ReturnedFields, .. new[] { IdField, CreateTimeField }.Where(f => !query.ReturnedFields.Contains(f, StringComparer.Ordinal))];
        return query with { Sort = [(CreateTimeField, false), (IdField, false)], ReturnedFields = fields };
    }

    /// <summary>
    /// How many records the search matches, whatever their createTime, and the createTime of the newest one; null when none
    /// matches. A search that matches records none of which holds a createTime cannot be read in this order.
    /// </summary>
    private async Task<(long Matched, long? Newest)> NewestAsync(OsduSearchQuery query, OsduSearchQuery asked, CancellationToken ct)
    {
        var newest = asked with { Sort = [(CreateTimeField, true), (IdField, true)], ReturnedFields = [IdField, CreateTimeField] };
        var answer = await QueryAsync(newest, 0, 1, null, refusalAnswers: true, ct).ConfigureAwait(false);
        if (answer.Refusal is { } words)
        {
            throw Refused(query, words);
        }

        if (answer.Total == 0)
        {
            return (0, null);
        }

        if (answer.Hits.Count == 0)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"The search of kind {query.Kind}{Shown(query.Query)} counted {answer.Total} record(s) and returned none when asked for the newest, so a read in createTime order has no end to read to."));
        }

        // A record without a createTime sorts last either way, so a newest without one says no record holds one.
        return MillisOf(answer.Hits[0][CreateTimeField]) is { } millis ? (answer.Total, millis) : throw UndatedRecords(query, answer.Total);
    }

    /// <summary>One page of the plain search: answered, refused with the service's words, or failed after the retry policy.</summary>
    private async Task<PageAnswer> OrderedPageAsync(Uri url, JsonObject body, CancellationToken ct)
    {
        var result = await SendAsync(token => _client.SendJsonAsync(HttpMethod.Post, url, body, Refusals, token, idempotent: true), ct).ConfigureAwait(false);
        return result.Status == HttpStatusCode.BadRequest ? PageAnswer.Refused(RefusalOf(result)) : PageAnswer.Of(result, url);
    }

    /// <summary>The records the query matches within <paramref name="clause"/>, exactly (POST /query with trackTotalCount).</summary>
    private async Task<long> OrderedCountAsync(OsduSearchQuery query, OsduSearchQuery asked, string clause, CancellationToken ct)
    {
        var counting = asked with { Query = Within(query.Query, clause), Sort = [], ReturnedFields = [IdField] };
        var answer = await QueryAsync(counting, 0, 1, null, refusalAnswers: true, ct).ConfigureAwait(false);
        return answer.Refusal is { } words ? throw Refused(query, words) : answer.Total;
    }

    /// <summary>The batches the search now counts more records in than the read handed out from them.</summary>
    private async Task<List<OrderedBatch>> ShortBatchesAsync(OsduSearchQuery query, OsduSearchQuery asked, OrderedRead read, CancellationToken ct)
    {
        var shortOf = new List<OrderedBatch>();
        foreach (var batch in read.Batches)
        {
            if (await OrderedCountAsync(query, asked, batch.Clause(), ct).ConfigureAwait(false) > batch.Handed)
            {
                shortOf.Add(batch);
            }
        }

        return shortOf;
    }

    /// <summary>
    /// The place of each hit in the order, or why the page cannot be placed: a hit that is not a record, or one without an id
    /// or a createTime.
    /// </summary>
    private static (IReadOnlyList<OrderedKey> Keys, string? Failure) KeysOf(IReadOnlyList<JsonElement> hits)
    {
        var keys = new List<OrderedKey>(hits.Count);
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            if (IdOf(hit) is not { } id || string.IsNullOrWhiteSpace(id))
            {
                return ([], string.Create(CultureInfo.InvariantCulture,
                    $"hit {i + 1} is {(hit.ValueKind == JsonValueKind.Object ? "a record without an id" : "not a record")}, so the read cannot tell which record it is"));
            }

            if (!hit.TryGetProperty(CreateTimeField, out var created) || MillisOf(created) is not { } millis)
            {
                return ([], string.Create(CultureInfo.InvariantCulture, $"record {id} came back without a createTime the read can place it by"));
            }

            keys.Add(new OrderedKey(millis, id));
        }

        return (keys, null);
    }

    /// <summary>
    /// Fails the read when the page does not keep to the order: a hit outside the range it was asked for, a hit before the
    /// one ahead of it, or a full page that brings no record past the last one handed out. The same record twice in a row is
    /// a repeat, which the read passes over.
    /// </summary>
    private static void CheckOrder(OsduSearchQuery query, OrderedSpan span, IReadOnlyList<OrderedKey> keys, bool full)
    {
        var previous = span.Position;
        foreach (var key in keys)
        {
            if (!span.Holds(key.Millis))
            {
                throw OutOfOrder(query, string.Create(CultureInfo.InvariantCulture,
                    $"record {key.Id} came back with createTime {Time(key.Millis)}, outside {span.Shown()} it was asked for"));
            }

            if (previous is { } before && key.CompareTo(before) < 0)
            {
                throw OutOfOrder(query, string.Create(CultureInfo.InvariantCulture,
                    $"record {key.Id} (createTime {Time(key.Millis)}) came back after record {before.Id} (createTime {Time(before.Millis)})"));
            }

            previous = key;
        }

        if (full && span.Position is { } last && keys[^1].CompareTo(last) <= 0)
        {
            throw OutOfOrder(query, string.Create(CultureInfo.InvariantCulture,
                $"a full page brought no record past record {last.Id} (createTime {Time(last.Millis)})"));
        }
    }

    /// <summary>The hits without their createTime, in a document of their own that the caller disposes; none for no hits.</summary>
    private static (IReadOnlyList<JsonElement> Hits, JsonDocument? Document) WithoutCreateTime(IReadOnlyList<JsonElement> hits)
    {
        if (hits.Count == 0)
        {
            return (hits, null);
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var hit in hits)
            {
                writer.WriteStartObject();
                foreach (var property in hit.EnumerateObject())
                {
                    if (!property.NameEquals(CreateTimeField))
                    {
                        property.WriteTo(writer);
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        var document = JsonDocument.Parse(buffer.WrittenMemory);
        return (document.RootElement.EnumerateArray().ToList(), document);
    }

    /// <summary>
    /// A createTime as milliseconds since the epoch, the resolution the index keeps a date at: from the text the indexer
    /// writes, or from a number of milliseconds; null when it is neither.
    /// </summary>
    private static long? MillisOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => MillisOf(value.GetString()),
        JsonValueKind.Number when value.TryGetInt64(out var millis) => millis,
        _ => null,
    };

    /// <inheritdoc cref="MillisOf(JsonElement)"/>
    private static long? MillisOf(JsonNode? value) => value is JsonValue scalar
        ? scalar.TryGetValue<string>(out var text) ? MillisOf(text) : scalar.TryGetValue<long>(out var millis) ? (long?)millis : null
        : null;

    private static long? MillisOf(string? text)
        => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at.ToUnixTimeMilliseconds()
            : null;

    /// <summary>Milliseconds since the epoch as a Lucene range bound takes them.</summary>
    private static string Time(long millis) => LuceneTime(DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime);

    /// <summary>An id as a quoted bound of a Lucene range: a backslash and a double quote escaped.</summary>
    private static string Quoted(string id) => "\"" + id.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>A range of createTime, after an instant or from the first record, through another.</summary>
    private static string CreatedBetween(long? after, long through)
        => after is { } from
            ? $"{CreateTimeField}:{{{Time(from)} TO {Time(through)}]"
            : $"{CreateTimeField}:[* TO {Time(through)}]";

    private static DeliveryException Refused(OsduSearchQuery query, string words) => new(
        $"The search service refused the search of kind {query.Kind}{Shown(query.Query)} in createTime order: {words}. A read in createTime order sorts the search on createTime and id and narrows it on them; source.paging: cursor reads the search through the search cursor instead.");

    private static DeliveryException OutOfOrder(OsduSearchQuery query, string what) => new(
        $"The search of kind {query.Kind}{Shown(query.Query)} came back out of createTime order: {what}. A read in createTime order relies on the service sorting on createTime and then id and keeping to the range it asks for, or it cannot tell what it missed; source.paging: cursor reads the search through the search cursor instead.");

    private static DeliveryException UndatedRecords(OsduSearchQuery query, long count) => new(string.Create(CultureInfo.InvariantCulture,
        $"{count} record(s) the search of kind {query.Kind}{Shown(query.Query)} matches hold no createTime, which a read in createTime order orders and bounds every page by, so it cannot reach them; source.paging: cursor reads the search through the search cursor instead."));

    /// <summary>
    /// Ids in the order the index sorts a keyword: by Unicode code point, which is the order of their UTF-8 bytes. An ordinal
    /// comparison of UTF-16 would put a character past U+E000 after a surrogate pair, so surrogates are lifted above it.
    /// </summary>
    internal static int CompareIds(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            if (left[i] != right[i])
            {
                return CodePointRank(left[i]).CompareTo(CodePointRank(right[i]));
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int CodePointRank(char c) => c >= 0xE000 ? c - 0x800 : c >= 0xD800 ? c + 0x2000 : c;

    /// <summary>Where a record sits in the order: its createTime in milliseconds, then its id.</summary>
    private readonly record struct OrderedKey(long Millis, string Id) : IComparable<OrderedKey>
    {
        public int CompareTo(OrderedKey other) => Millis != other.Millis ? Millis.CompareTo(other.Millis) : CompareIds(Id, other.Id);
    }

    /// <summary>
    /// A range of createTime the read tallies on its own: after one instant (or from the first record) through another, and
    /// how many distinct records the read handed out from it.
    /// </summary>
    private sealed class OrderedBatch(long? after)
    {
        /// <summary>The createTime the batch begins after; null for the first batch, which begins at the first record.</summary>
        public long? After { get; } = after;

        /// <summary>The last createTime the batch holds, once it is closed.</summary>
        public long Through { get; private set; }

        /// <summary>The createTime of the last record handed out into the batch while the read builds it.</summary>
        public long Last { get; set; } = long.MinValue;

        /// <summary>The distinct records the read handed out from the batch.</summary>
        public long Handed { get; set; }

        public void Close(long through) => Through = through;

        public string Clause() => CreatedBetween(After, Through);
    }

    /// <summary>
    /// A range of createTime one pass reads: the read's whole range the first time, while its batches are built, or one batch
    /// read again. The position is the last record the range handed out, which every page after it starts past.
    /// </summary>
    private sealed class OrderedSpan(long? after, long through, OrderedBatch? batch)
    {
        public long? After { get; } = after;

        public long Through { get; } = through;

        /// <summary>The batch read again, or null for the read's whole range.</summary>
        public OrderedBatch? Batch { get; } = batch;

        public OrderedKey? Position { get; set; }

        /// <summary>
        /// The range as a Lucene clause, past the position once there is one: a later createTime, or the same createTime and
        /// a later id.
        /// </summary>
        public string Clause()
        {
            if (Position is not { } at)
            {
                return CreatedBetween(After, Through);
            }

            var tied = $"({CreateTimeField}:[{Time(at.Millis)} TO {Time(at.Millis)}] AND {IdField}:{{{Quoted(at.Id)} TO *])";
            return at.Millis >= Through ? tied : $"({CreateTimeField}:{{{Time(at.Millis)} TO {Time(Through)}] OR {tied})";
        }

        public bool Holds(long millis) => (After is not { } from || millis > from) && millis <= Through;

        public string Shown() => After is { } from ? $"({Time(from)}, {Time(Through)}]" : $"(*, {Time(Through)}]";

        public string Where() => Position is { } at
            ? $"after record {at.Id} (createTime {Time(at.Millis)})"
            : Batch is null ? "at the start of the read" : $"at the start of the batch {Shown()}";
    }

    /// <summary>Where a read in createTime order stands: its end, its batches and the ranges left to read.</summary>
    private sealed class OrderedRead
    {
        public OrderedRead(long end)
        {
            End = end;
            Batches.Add(new OrderedBatch(null));
            Plan.Enqueue(new OrderedSpan(null, end, null));
        }

        /// <summary>The createTime of the newest record the search matched when the read began, which it reads no further than.</summary>
        public long End { get; }

        public List<OrderedBatch> Batches { get; } = [];

        public Queue<OrderedSpan> Plan { get; } = new();

        public long Handed => Batches.Sum(b => b.Handed);

        /// <summary>Tallies a record handed out from <paramref name="span"/>, closing the batch being built where it is full.</summary>
        public void Take(OrderedSpan span, OrderedKey key)
        {
            if (span.Batch is { } again)
            {
                again.Handed++;
                return;
            }

            var open = Batches[^1];
            if (open.Handed >= BatchRecords && key.Millis > open.Last)
            {
                open.Close(open.Last);
                open = new OrderedBatch(open.Last);
                Batches.Add(open);
            }

            open.Handed++;
            open.Last = key.Millis;
        }

        /// <summary>Ends <paramref name="span"/>, which is read to its end; the read's whole range closes its last batch at the end.</summary>
        public void Done(OrderedSpan span)
        {
            Plan.Dequeue();
            if (span.Batch is null)
            {
                Batches[^1].Close(End);
            }
        }
    }
}
