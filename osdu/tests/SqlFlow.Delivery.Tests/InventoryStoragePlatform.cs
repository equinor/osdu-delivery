using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Web;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Storage's own reads over the records the search stand-in holds (openapi storage v2): the listing of a kind's ids a page at
/// a time under a cursor, the headers of up to a thousand ids (or 404 where the route is not deployed), records by id (their
/// data trimmed to the <c>data.</c> attributes asked, a nested one under its dotted rest, as os-core-common's
/// PersistenceHelper.filterRecordDataFields does), a record's versions, and the schema service's listing of the kinds; and
/// its removals, each taking a record out of what it
/// serves: the soft delete of a list (204, or 207 naming the ids it did not delete), the soft delete of one id, and its purge.
/// A soft-deleted record is kept, as storage keeps it, and its merge patch of <c>{"deleted": false}</c> serves it again
/// (osdu/specs/core/INTEGRATION.md, D3): 400 for a record it serves already, 404 for one it holds no record of, 409 for one it
/// withholds over its legal tags.
/// </summary>
internal sealed class StoragePlatform : DelegatingHandler
{
    private const int Page = 1000;

    public StoragePlatform(FakeDimensionPlatform search)
        : base(search)
    {
        Search = search;
    }

    public FakeDimensionPlatform Search { get; }

    public HashSet<string> DeletedAfterListing { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, JsonObject> StorageOnly { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, long[]> Versions { get; } = new(StringComparer.Ordinal);

    public bool HeadersDeployed { get; set; } = true;

    /// <summary>The headers route answers 404 with every id it was asked for under notFound: it holds none of them.</summary>
    public bool HeadersHoldNone { get; set; }

    /// <summary>The ids a read by id names under retryRecords every time it is asked: storage never says whether it holds them.</summary>
    public HashSet<string> RetryForever { get; } = new(StringComparer.Ordinal);

    /// <summary>The attributes each read by id asked for, in the order the reads came.</summary>
    public ConcurrentQueue<IReadOnlyList<string>> AttributesAsked { get; } = new();

    public ListingCursors Cursors { get; set; }

    private int _listings;
    private int _headerReads;
    private int _recordReads;
    private int _versionReads;

    public int Listings => Volatile.Read(ref _listings);

    public int HeaderReads => Volatile.Read(ref _headerReads);

    public int RecordReads => Volatile.Read(ref _recordReads);

    public int VersionReads => Volatile.Read(ref _versionReads);

    public List<string> KindsListed { get; } = [];

    /// <summary>The ids storage refuses to delete: the bulk soft delete names them as not deleted, a single delete answers 403.</summary>
    public HashSet<string> Undeletable { get; } = new(StringComparer.Ordinal);

    /// <summary>The status every removal answers instead of acting (401 or 403 for a caller that may not delete), or null.</summary>
    public HttpStatusCode? RemovalsRefused { get; set; }

    /// <summary>The ids soft deleted, in the order they were, and those purged.</summary>
    public List<string> SoftDeleted { get; } = [];

    public List<string> Purged { get; } = [];

    /// <summary>The ids brought back from their soft delete, in the order they were.</summary>
    public List<string> Restored { get; } = [];

    /// <summary>The ids storage withholds over their legal tags: a restore of one answers 409.</summary>
    public HashSet<string> Withheld { get; } = new(StringComparer.Ordinal);

    /// <summary>The records soft deleted and not brought back, which storage keeps with every version.</summary>
    private readonly Dictionary<string, JsonObject> _kept = new(StringComparer.Ordinal);

    private int _bulkDeletes;
    private int _singleDeletes;
    private int _restores;

    public int BulkDeletes => Volatile.Read(ref _bulkDeletes);

    public int SingleDeletes => Volatile.Read(ref _singleDeletes);

    /// <summary>How many restores (merge patches of a record's state) storage was asked for.</summary>
    public int Restores => Volatile.Read(ref _restores);

    /// <summary>Soft deletes a record outside the flows, as another tool would: storage keeps it, and stops serving it.</summary>
    public void SoftDelete(string id)
    {
        Assert.True(Take(id, keep: true), $"storage serves no record {id} to soft delete");
    }

    /// <summary>Brings a soft-deleted record back outside the flows, as another tool would: storage serves it again.</summary>
    public void Undelete(string id)
    {
        lock (Search.Records)
        {
            Assert.True(_kept.Remove(id, out var record), $"storage keeps no soft-deleted record {id} to bring back");
            Search.Records.Add(record);
        }
    }

    /// <summary>Purges a record outside the flows: storage keeps nothing of it.</summary>
    public void Purge(string id)
    {
        lock (Search.Records)
        {
            if (!_kept.Remove(id))
            {
                Assert.True(Take(id, keep: false), $"storage holds no record {id} to purge");
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (path == "/api/storage/v2/query/records" && request.Method == HttpMethod.Get)
        {
            Interlocked.Increment(ref _listings);
            var kind = query["kind"]!;
            KindsListed.Add(kind);
            var ids = Search.Records.Where(r => r["kind"]!.GetValue<string>() == kind).Select(r => r["id"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
            if (ids.Count == 0)
            {
                return FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"code":404,"reason":"Not Found","message":"Kind not found"}""");
            }

            if (query["cursor"] is not null && Cursors == ListingCursors.Lost)
            {
                return FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"code":404,"reason":"Not Found","message":"Cursor not found"}""");
            }

            var from = Cursors == ListingCursors.Circling ? 0 : int.Parse(query["cursor"] ?? "0", CultureInfo.InvariantCulture);
            var page = ids.Skip(from).Take(int.Parse(query["limit"]!, CultureInfo.InvariantCulture)).ToList();
            var answer = new JsonObject { ["results"] = new JsonArray(page.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) };
            if (from + page.Count < ids.Count)
            {
                answer["cursor"] = Cursors == ListingCursors.Circling ? "the-same-cursor" : (from + page.Count).ToString(CultureInfo.InvariantCulture);
            }

            return FakeHttpHandler.Json(HttpStatusCode.OK, answer.ToJsonString());
        }

        if (path == "/api/storage/v2/query/records/headers" && request.Method == HttpMethod.Post)
        {
            Interlocked.Increment(ref _headerReads);
            if (!HeadersDeployed)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no route") };
            }

            if (HeadersHoldNone)
            {
                // The route's own answer when it holds none of the ids it was asked for.
                var none = JsonNode.Parse(body!)!["records"]!.AsArray().Select(i => (JsonNode?)JsonValue.Create(i!.GetValue<string>())).ToArray();
                return FakeHttpHandler.Json(HttpStatusCode.NotFound, new JsonObject { ["records"] = new JsonArray(), ["notFound"] = new JsonArray(none) }.ToJsonString());
            }

            var asked = JsonNode.Parse(body!)!["records"]!.AsArray().Select(i => i!.GetValue<string>()).ToList();
            Assert.True(asked.Count <= Page);
            var (found, missing) = Held(asked);
            return FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject
            {
                ["records"] = new JsonArray(found.Select(r => (JsonNode?)Header(r)).ToArray()),
                ["notFound"] = new JsonArray(missing.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
            }.ToJsonString());
        }

        if (path == "/api/storage/v2/query/records" && request.Method == HttpMethod.Post)
        {
            Interlocked.Increment(ref _recordReads);
            var asking = JsonNode.Parse(body!)!;
            var asked = asking["records"]!.AsArray().Select(i => i!.GetValue<string>()).ToList();
            Assert.True(asked.Count <= 100);
            var attributes = asking["attributes"]?.AsArray().Select(a => a!.GetValue<string>()).ToList() ?? [];
            AttributesAsked.Enqueue(attributes);
            var retry = asked.Where(RetryForever.Contains).ToList();
            var (found, missing) = Held(asked.Except(retry, StringComparer.Ordinal).ToList());
            return FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject
            {
                ["records"] = new JsonArray(found.Select(r => (JsonNode?)Projected(r, attributes)).ToArray()),
                ["invalidRecords"] = new JsonArray(missing.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["retryRecords"] = new JsonArray(retry.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
            }.ToJsonString());
        }

        if (path.StartsWith("/api/storage/v2/records/versions/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
        {
            Interlocked.Increment(ref _versionReads);
            var id = Uri.UnescapeDataString(path["/api/storage/v2/records/versions/".Length..]);
            return Versions.TryGetValue(id, out var versions)
                ? FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject { ["recordId"] = id, ["versions"] = new JsonArray(versions.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) }.ToJsonString())
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (path == "/api/storage/v2/records/delete" && request.Method == HttpMethod.Post)
        {
            Interlocked.Increment(ref _bulkDeletes);
            if (RemovalsRefused is { } refused)
            {
                return Refusal(refused);
            }

            var asked = JsonNode.Parse(body!)!.AsArray().Select(i => i!.GetValue<string>()).ToList();
            Assert.True(asked.Count <= 500);
            // The service deletes what it can and answers 207 with an array naming each id it kept (osdu/specs/core/INTEGRATION.md, D2).
            var notDeleted = new JsonArray();
            foreach (var id in asked)
            {
                if (Undeletable.Contains(id))
                {
                    notDeleted.Add(new JsonObject { ["notDeletedRecordId"] = id, ["message"] = $"The user is not authorized to perform delete record with id {id}" });
                }
                else if (Take(id, keep: true))
                {
                    SoftDeleted.Add(id);
                }
                else
                {
                    notDeleted.Add(new JsonObject { ["notDeletedRecordId"] = id, ["message"] = $"Record with id '{id}' not found" });
                }
            }

            return notDeleted.Count == 0
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : FakeHttpHandler.Json((HttpStatusCode)207, notDeleted.ToJsonString());
        }

        var softDelete = request.Method == HttpMethod.Post && path.EndsWith(":delete", StringComparison.Ordinal);
        if (path.StartsWith("/api/storage/v2/records/", StringComparison.Ordinal) && (softDelete || request.Method == HttpMethod.Delete))
        {
            Interlocked.Increment(ref _singleDeletes);
            var tail = path["/api/storage/v2/records/".Length..];
            var id = Uri.UnescapeDataString(softDelete ? tail[..^":delete".Length] : tail);
            if (RemovalsRefused is { } refused)
            {
                return Refusal(refused);
            }

            if (Undeletable.Contains(id))
            {
                return Refusal(HttpStatusCode.Forbidden);
            }

            if (!Take(id, keep: softDelete))
            {
                return FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"code":404,"reason":"Not Found","message":"Record not found"}""");
            }

            (softDelete ? SoftDeleted : Purged).Add(id);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (path.StartsWith("/api/storage/v2/records/", StringComparison.Ordinal) && request.Method == HttpMethod.Patch)
        {
            Interlocked.Increment(ref _restores);
            var id = Uri.UnescapeDataString(path["/api/storage/v2/records/".Length..]);
            Assert.Equal("application/merge-patch+json", request.Content!.Headers.ContentType!.MediaType);
            Assert.False(JsonNode.Parse(body!)!["deleted"]!.GetValue<bool>());
            if (RemovalsRefused is { } refused)
            {
                return Refusal(refused);
            }

            if (Withheld.Contains(id))
            {
                return FakeHttpHandler.Json(HttpStatusCode.Conflict, """{"code":409,"reason":"Record is legally withheld","message":"Record cannot be undeleted while its legal tags are incompliant"}""");
            }

            lock (Search.Records)
            {
                if (_kept.Remove(id, out var record))
                {
                    Search.Records.Add(record);
                    Restored.Add(id);
                    return FakeHttpHandler.Json(HttpStatusCode.OK, record.ToJsonString());
                }

                return Held([id]).Found.Count > 0
                    ? FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"code":400,"reason":"Record State already updated","message":"Record State already updated"}""")
                    : FakeHttpHandler.Json(HttpStatusCode.NotFound, $$"""{"code":404,"reason":"Record not found for patching","message":"The record '{{id}}' was not found for patching"}""");
            }
        }

        if (path == "/api/schema-service/v1/schema" && request.Method == HttpMethod.Get)
        {
            var offset = int.Parse(query["offset"] ?? "0", CultureInfo.InvariantCulture);
            var kinds = query["status"] == "PUBLISHED" && query["scope"] == "INTERNAL"
                ? Search.Records.Select(r => r["kind"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Skip(offset).ToList()
                : [];
            return FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject
            {
                ["schemaInfos"] = new JsonArray(kinds.Select(k => (JsonNode?)new JsonObject { ["schemaIdentity"] = new JsonObject { ["id"] = k }, ["status"] = "PUBLISHED", ["scope"] = "INTERNAL" }).ToArray()),
            }.ToJsonString());
        }

        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>What storage answers a caller that may not remove what it asks to.</summary>
    private static HttpResponseMessage Refusal(HttpStatusCode status)
        => FakeHttpHandler.Json(status, new JsonObject { ["code"] = (int)status, ["reason"] = "Forbidden", ["message"] = "The user is not authorized to perform this action" }.ToJsonString());

    /// <summary>
    /// Takes a record out of what storage serves, keeping it for a restore when <paramref name="keep"/> says it was soft deleted;
    /// false when it serves no such record.
    /// </summary>
    private bool Take(string id, bool keep)
    {
        lock (Search.Records)
        {
            if (StorageOnly.Remove(id, out var only))
            {
                if (keep)
                {
                    _kept[id] = only;
                }

                return true;
            }

            var record = Search.Records.FirstOrDefault(r => r["id"]!.GetValue<string>() == id);
            if (record is null || DeletedAfterListing.Contains(id) || !Search.Records.Remove(record))
            {
                return false;
            }

            if (keep)
            {
                _kept[id] = record;
            }

            return true;
        }
    }

    private (List<JsonObject> Found, List<string> Missing) Held(IReadOnlyList<string> asked)
    {
        var found = new List<JsonObject>();
        var missing = new List<string>();
        foreach (var id in asked)
        {
            if (StorageOnly.TryGetValue(id, out var only))
            {
                found.Add(only);
            }
            else if (!DeletedAfterListing.Contains(id) && Search.Records.FirstOrDefault(r => r["id"]!.GetValue<string>() == id) is { } record)
            {
                found.Add(record);
            }
            else
            {
                missing.Add(id);
            }
        }

        return (found, missing);
    }

    /// <summary>
    /// A record as storage's read by id returns it: whole when no attribute names a <c>data.</c> property, else with its data
    /// holding only those, each under the path after <c>data.</c>, a nested one under that dotted path rather than nested.
    /// </summary>
    private static JsonObject Projected(JsonObject record, IReadOnlyList<string> attributes)
    {
        var copy = (JsonObject)record.DeepClone();
        var wanted = attributes.Select(a => a.Trim()).Where(a => a.StartsWith("data.", StringComparison.Ordinal)).Select(a => a["data.".Length..]).ToList();
        if (wanted.Count == 0)
        {
            return copy;
        }

        var data = new JsonObject();
        foreach (var attribute in wanted)
        {
            JsonNode? node = record["data"];
            foreach (var segment in attribute.Split('.'))
            {
                node = node is JsonObject inner ? inner[segment] : null;
            }

            if (node is not null)
            {
                data[attribute] = node.DeepClone();
            }
        }

        copy["data"] = data;
        return copy;
    }

    private static JsonObject Header(JsonObject record)
    {
        var header = new JsonObject();
        foreach (var name in new[] { "id", "kind", "version", "createUser", "createTime", "modifyUser", "modifyTime" })
        {
            if (record[name] is { } value)
            {
                header[name] = value.DeepClone();
            }
        }

        return header;
    }
}

/// <summary>How storage's listing hands out its cursor.</summary>
internal enum ListingCursors
{
    /// <summary>A cursor naming where the next page starts.</summary>
    Paging,

    /// <summary>The same cursor for every page, and the first page each time.</summary>
    Circling,

    /// <summary>A cursor storage no longer knows when it is handed back.</summary>
    Lost,
}
