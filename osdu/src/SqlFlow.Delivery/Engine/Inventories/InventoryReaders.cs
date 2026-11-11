using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>What one read of an inventory took: the pages it read and the requests it sent.</summary>
internal sealed class InventoryReadStats
{
    private int _pages;
    private long _requests;

    public int Pages => _pages;

    public long Requests => _requests;

    public void Page() => Interlocked.Increment(ref _pages);

    public void Request(long count = 1) => Interlocked.Add(ref _requests, count);
}

/// <summary>
/// Reads every id one inventory's kind holds (osdu/docs/reference/flow/inventory.md, Reading OSDU), in pages: whole, or
/// failing, never a part handed on as if it were the whole, since the merge marks every id the read did not list gone.
/// </summary>
internal interface IInventoryReader
{
    IAsyncEnumerable<IReadOnlyList<InventoryScanRow>> ReadAsync(InventorySpec inventory, string? query, InventoryReadStats stats, CancellationToken ct);
}

/// <summary>
/// The search index's read (<c>POST /query_with_cursor</c>, a thousand a page), projected onto the system properties an
/// inventory keeps and the properties a record is named by, through the one cursor read every reader of the module takes
/// (<see cref="OsduSearch.PagesAsync"/>), which fails rather than hand on less than the search matches.
/// </summary>
internal sealed class SearchInventoryReader(OsduSearch search) : IInventoryReader
{
    /// <summary>
    /// The fields an inventory keeps of each record: its system properties, which the search returns for every kind, and the
    /// properties a record is named by (<see cref="RecordNames.Fields"/>), which a record holds or not. They ride on the same
    /// pages, so a name costs no request.
    /// </summary>
    public static IReadOnlyList<string> Fields { get; } = ["id", "kind", "version", "createUser", "createTime", "modifyUser", "modifyTime", .. RecordNames.Fields];

    public async IAsyncEnumerable<IReadOnlyList<InventoryScanRow>> ReadAsync(InventorySpec inventory, string? query, InventoryReadStats stats, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(stats);
        await foreach (var page in search.PagesAsync(new OsduSearchQuery { Kind = inventory.Kind, Query = query, ReturnedFields = Fields }, OsduSearch.MaxPage, ct).ConfigureAwait(false))
        {
            stats.Page();
            stats.Request();
            var rows = new List<InventoryScanRow>(page.Hits.Count);
            foreach (var hit in page.Hits)
            {
                if (InventoryRows.Of(hit) is { } row)
                {
                    rows.Add(row);
                }
            }

            yield return rows;
        }
    }
}

/// <summary>
/// Storage's own read: every active record of each kind (<c>GET /query/records?kind=</c>, a thousand ids a page, which needs
/// the storage service's admin role), then what storage holds of those ids (<see cref="StorageHeaders"/>): with
/// <paramref name="names"/>, their system properties and names through <c>POST /query/records</c> a hundred at a time, as an
/// inventory reads them; without, their system properties alone through <c>POST /query/records/headers</c> a thousand at a
/// time, as a download lists them. A kind with wildcards is expanded through the schema service, since storage lists one kind
/// at a time. An id storage never says whether it holds fails the read, since a read is whole or nothing.
/// </summary>
internal sealed class StorageInventoryReader(OsduHttpClient client, InventorySource source, TimeProvider time, bool names) : IInventoryReader
{
    /// <summary>The most ids one listing page, and one headers request, carry (openapi storage v2).</summary>
    public const int Page = 1000;

    private readonly StorageHeaders _headers = new(client, source, names);

    public async IAsyncEnumerable<IReadOnlyList<InventoryScanRow>> ReadAsync(InventorySpec inventory, string? query, InventoryReadStats stats, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(stats);
        if (query is not null)
        {
            throw new DeliveryException($"Inventory '{inventory.Name}' narrows its read with a query, and storage lists every record of a kind; a storage read takes no query.");
        }

        foreach (var kind in await KindsAsync(client, time, source, inventory.Kind, stats, ct).ConfigureAwait(false))
        {
            string? cursor = null;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var url = client.Url(source.RecordQueryPath);
                url = OsduHttpClient.WithQuery(url, "kind", kind);
                url = OsduHttpClient.WithQuery(url, "limit", Page.ToString(CultureInfo.InvariantCulture));
                if (cursor is not null)
                {
                    url = OsduHttpClient.WithQuery(url, "cursor", cursor);
                }

                var result = await client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
                stats.Request();
                stats.Page();
                if ((int)result.Status == 404)
                {
                    // A kind storage holds no record of answers 404 on its first page; a cursor it no longer knows on a later one.
                    if (cursor is null)
                    {
                        break;
                    }

                    throw new DeliveryException($"Storage no longer knows the cursor of its listing of {kind} part way through, so the listing cannot be read whole; the next build reads it again from the start.");
                }

                var root = OsduHttpClient.ParseJson(result, url);
                var ids = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
                    ? results.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList()
                    : throw new DeliveryException($"{url.AbsolutePath} answered the listing of {kind} without its results.");
                if (ids.Count > 0)
                {
                    var read = await _headers.ReadAsync(ids, stats, ct).ConfigureAwait(false);
                    if (read.Unanswered.Count > 0)
                    {
                        throw new DeliveryException(string.Create(
                            CultureInfo.InvariantCulture,
                            $"Storage listed {read.Unanswered.Count} id(s) of {kind} and, asked twice, never said whether it holds them (retryRecords; {read.Unanswered.Order(StringComparer.Ordinal).First()} among them), so the listing cannot be read whole; the next build reads it again."));
                    }

                    // An id the listing named that storage no longer holds was deleted between the two calls: it is not served.
                    yield return ids.Where(read.Held.ContainsKey).Select(id => read.Held[id]).ToList();
                }

                cursor = root.TryGetProperty("cursor", out var next) && next.ValueKind == JsonValueKind.String && next.GetString() is { Length: > 0 } text ? text : null;
                if (cursor is null || ids.Count == 0)
                {
                    break;
                }

                if (!seenCursors.Add(cursor))
                {
                    throw new DeliveryException($"Storage handed back a cursor of its listing of {kind} it had handed back before, so the listing goes in circles and cannot be read whole.");
                }
            }
        }
    }

    /// <summary>
    /// The exact kinds <paramref name="kind"/> names: itself when it has no wildcard, else every schema the schema service lists
    /// that it matches, in every status and scope, each segment matched as the search service matches a kind (a <c>*</c>
    /// standing for any text within its segment, so <c>1.*.*</c> takes every 1.x.y version; osdu/specs/core/search,
    /// <c>kind</c>: "Wildcards are supported per segment"). A plan counts with the same expansion a build reads with.
    /// </summary>
    public static async Task<IReadOnlyList<string>> KindsAsync(OsduHttpClient client, TimeProvider time, InventorySource source, string kind, InventoryReadStats stats, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stats);
        if (OsduKind.IsExact(kind))
        {
            return [kind];
        }

        var reader = new SchemaServiceReader(client, time, source.SchemaPath);
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var status in SchemaServiceReader.Statuses)
        {
            foreach (var scope in SchemaServiceReader.Scopes)
            {
                var listing = await reader.ListAsync(status, scope, ct).ConfigureAwait(false);
                stats.Request();
                foreach (var schema in listing.Schemas.Where(s => KindPatterns.Matches(kind, s.Id)))
                {
                    kinds.Add(schema.Id);
                }
            }
        }

        return [.. kinds];
    }
}

/// <summary>
/// What storage answered for records read by id: the system properties (and, where asked for, the name) of each it holds, and
/// the ids it never said whether it holds (<c>retryRecords</c> on the second asking too), which are neither held nor missing.
/// </summary>
internal sealed record StorageHeadersRead(IReadOnlyDictionary<string, InventoryScanRow> Held, IReadOnlySet<string> Unanswered);

/// <summary>
/// What storage holds of records read by id. Without names: their system properties through <c>POST /query/records/headers</c>,
/// a thousand ids a request, or where a deployment does not serve it (404 or 405), through <c>POST /query/records</c>. With
/// <paramref name="names"/>: through <c>POST /query/records</c> alone, a hundred ids a request, since the headers route returns
/// no data and a record's name is in its data. The records route asks for the properties a record is named by
/// (<see cref="RecordNames.Fields"/>), so storage returns its system properties with only those of its data
/// (os-core-common PersistenceHelper.filterRecordDataFields). An id storage does not hold (soft-deleted, never written, or one
/// the caller may not see) is left out of what it holds; one it asked twice to retry is unanswered.
/// </summary>
internal sealed class StorageHeaders(OsduHttpClient client, InventorySource source, bool names)
{
    private static readonly string[] Attributes = ["kind", "version", "createUser", "createTime", "modifyUser", "modifyTime"];

    private bool _headersServed = !names;

    public async Task<StorageHeadersRead> ReadAsync(IReadOnlyList<string> ids, InventoryReadStats stats, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var found = new Dictionary<string, InventoryScanRow>(StringComparer.Ordinal);
        var unanswered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in ids.Distinct(StringComparer.Ordinal).Chunk(StorageInventoryReader.Page))
        {
            if (_headersServed)
            {
                var url = client.Url(source.HeadersPath);
                var body = new JsonObject
                {
                    ["records"] = new JsonArray(chunk.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                    ["attributes"] = new JsonArray(Attributes.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                };
                var result = await client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 404, 405 }, ct, idempotent: true).ConfigureAwait(false);
                stats.Request();
                var status = (int)result.Status;
                if (status == 200 || (status == 404 && Answered(result)))
                {
                    // A 404 the route answers itself names the ids it does not hold, and holds none of them.
                    if (status == 200)
                    {
                        Collect(OsduHttpClient.ParseJson(result, url), found);
                    }

                    continue;
                }

                // The route is not deployed (405, or a 404 that is no answer of it): every later read goes through POST /query/records.
                _headersServed = false;
            }

            var records = new StorageRecords(client, source.RecordQueryPath);
            foreach (var hundred in chunk.Chunk(StorageRecords.Batch))
            {
                var read = await records.ReadAsync(hundred, RecordNames.Fields, ct).ConfigureAwait(false);
                stats.Request();
                foreach (var record in read.Records)
                {
                    if (InventoryRows.Of(record) is { } row)
                    {
                        found[row.TargetId] = row;
                    }
                }

                unanswered.UnionWith(read.Unanswered.Where(id => !found.ContainsKey(id)));
            }
        }

        return new StorageHeadersRead(found, unanswered);
    }

    /// <summary>Whether a 404 is the headers route's own answer (the records were not found) rather than a route the deployment does not serve.</summary>
    private static bool Answered(Http.HttpFetchResult result)
    {
        try
        {
            using var document = JsonDocument.Parse(result.Body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && (document.RootElement.TryGetProperty("notFound", out _) || document.RootElement.TryGetProperty("records", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Collect(JsonElement root, Dictionary<string, InventoryScanRow> found)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var record in records.EnumerateArray())
        {
            if (InventoryRows.Of(record) is { } row)
            {
                found[row.TargetId] = row;
            }
        }
    }
}

/// <summary>The versions storage keeps of a record (<c>GET /records/versions/{id}</c>); null when storage holds no such record.</summary>
internal sealed class StorageVersions(OsduHttpClient client, InventorySource source)
{
    public async Task<IReadOnlyList<long>?> ReadAsync(string targetId, InventoryReadStats stats, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var url = client.Url(source.VersionsPath + "/{id}", targetId);
        var result = await client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        stats.Request();
        if ((int)result.Status == 404)
        {
            return null;
        }

        var root = OsduHttpClient.ParseJson(result, url);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("versions", out var listed) || listed.ValueKind != JsonValueKind.Array)
        {
            throw new DeliveryException($"{url.AbsolutePath} answered {targetId}'s versions without a version list.");
        }

        return listed.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _)).Select(v => v.GetInt64()).Distinct().Order().ToList();
    }
}

/// <summary>An inventory's row as a read gives it.</summary>
internal static class InventoryRows
{
    /// <summary>
    /// The row a search hit or a storage record gives, its name among it (<see cref="RecordNames.Of"/>; null for a headers read,
    /// which returns no data), or null for one that names no id.
    /// </summary>
    public static InventoryScanRow? Of(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object || OsduSearch.IdOf(record) is not { } id || id.Length > Data.DeliveryModel.MaxTargetIdLength)
        {
            return null;
        }

        return new InventoryScanRow(
            id,
            Text(record, "kind"),
            record.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt64(out var v) ? v
                : version.ValueKind == JsonValueKind.String && long.TryParse(version.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            Text(record, "createUser"),
            Time(record, "createTime"),
            Text(record, "modifyUser"),
            Time(record, "modifyTime"),
            RecordNames.Of(record));
    }

    private static string? Text(JsonElement record, string name)
        => record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static DateTime? Time(JsonElement record, string name)
        => Text(record, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at.UtcDateTime
            : null;
}
