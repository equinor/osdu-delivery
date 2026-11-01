using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using System.Xml.Linq;
using Amazon.Runtime.Internal.Auth;
using SqlFlow.Delivery.Hashing;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Seismic Store v3 and the object stores behind it, as osdu/specs/seismic-ddms/INTEGRATION.md reads the service and its
/// clients: tenants and subprojects, datasets under write locks with the <c>x-seismic-dms-lockid</c> replays, the
/// <c>seismicmeta</c> record written through Storage, upload credentials per provider, and the Azure block blob, Google
/// resumable upload and S3 multipart APIs the files go to. The S3 store checks every signature: the canonical request is
/// rebuilt here from the SigV4 rules, independently of the route's signer, and signed with the AWS SDK's own function.
/// </summary>
public sealed partial class FakeOsduPlatform
{
    /// <summary>Where Seismic Store sits under the platform (the community charts' prefix).</summary>
    public const string SeismicRoot = "/api/seismic-store/v3";

    /// <summary>The Google Cloud Storage endpoint the Seismic Store flows name for gc.</summary>
    public const string GcsEndpoint = Endpoint + "/gcs";

    /// <summary>The S3 endpoint the Seismic Store flows name for anthos and IBM.</summary>
    public const string S3Endpoint = Endpoint + "/s3";

    private const string SeismicLockHeader = "x-seismic-dms-lockid";
    private const string AmzDate = "x-amz-date";

    private int _seismicIssued;
    private int _gcsSessions;

    /// <summary>The provider label Seismic Store answers with (<c>Service-Provider</c>): azure, gc, anthos or ibm.</summary>
    public string SeismicProvider { get; set; } = "azure";

    /// <summary>The subprojects by <c>tenant/subproject</c>: the legal tag and access policy each was created with, and its storage.</summary>
    public Dictionary<string, (string LegalTag, string Policy, string Bucket)> SeismicSubprojects { get; } = new(StringComparer.Ordinal)
    {
        ["dev/seismic"] = ("dev-public", "uniform", "ss-dev-seismic00001"),
    };

    /// <summary>The datasets by <c>sd://</c> path, as the catalogue keeps them.</summary>
    public Dictionary<string, JsonObject> SeismicDatasets { get; } = new(StringComparer.Ordinal);

    /// <summary>The write locks by <c>sd://</c> path.</summary>
    public Dictionary<string, string> SeismicLocks { get; } = new(StringComparer.Ordinal);

    /// <summary>The objects every store holds, by <c>bucket/key</c> (an Azure container counts as a bucket).</summary>
    public SortedDictionary<string, byte[]> SeismicObjects { get; } = new(StringComparer.Ordinal);

    /// <summary>The content MD5 Azure keeps for each blob (<c>x-ms-blob-content-md5</c>), by <c>container/key</c>.</summary>
    public Dictionary<string, string> AzureBlobMd5 { get; } = new(StringComparer.Ordinal);

    /// <summary>Credentials the stores no longer take: SAS signatures, bearer tokens and S3 session tokens.</summary>
    public HashSet<string> SeismicExpired { get; } = new(StringComparer.Ordinal);

    /// <summary>How many upload credentials Seismic Store issued.</summary>
    public int SeismicCredentialsIssued => _seismicIssued;

    /// <summary>Seismic Store's Storage writes are off (<c>FEATURE_FLAG_SEISMICMETA_STORAGE</c>), so a <c>seismicmeta</c> is not written.</summary>
    public bool SeismicSkipsStorage { get; set; }

    /// <summary>The next registration keeps its lock but saves nothing and answers <c>{}</c>, as a failed registration's replay does.</summary>
    public bool SeismicRegisterEmptyOnce { get; set; }

    /// <summary>Requests to the object stores, counted from 1, answered 500 instead of served.</summary>
    public HashSet<int> SeismicStoreFailing { get; } = [];

    /// <summary>Google sessions keep only half of each piece they are sent, as a session may.</summary>
    public bool GcsHalfPieces { get; set; }

    /// <summary>The next Google piece is kept, and answered 503 as if its answer were lost.</summary>
    public bool GcsLoseNextAnswer { get; set; }

    /// <summary>The access key and secret of every key triple Seismic Store issued, for checking signatures.</summary>
    private Dictionary<string, (string Secret, string Session)> S3Keys { get; } = new(StringComparer.Ordinal);

    private Dictionary<string, (string Bucket, string Name, long Length, MemoryStream Received, bool Done)> GcsSessions { get; } = new(StringComparer.Ordinal);

    private Dictionary<string, (string Bucket, string Key, SortedDictionary<int, byte[]> Parts)> S3Uploads { get; } = new(StringComparer.Ordinal);

    private Dictionary<(string Blob, string Id), byte[]> AzureBlocks { get; } = [];

    private int _storeRequests;

    private HttpResponseMessage? SeismicRoute(string method, string path, Uri uri, string? body, byte[] bytes, HttpRequestMessage request)
    {
        if (path.StartsWith(SeismicRoot + "/", StringComparison.Ordinal))
        {
            return Seismic(method, path[SeismicRoot.Length..], uri, body, request);
        }

        if (path.StartsWith("/blob/", StringComparison.Ordinal))
        {
            return StoreFails() ?? AzureBlob(method, path["/blob/".Length..], uri, bytes, request);
        }

        if (path.StartsWith("/gcs/", StringComparison.Ordinal))
        {
            return StoreFails() ?? Gcs(method, path["/gcs".Length..], uri, bytes, request);
        }

        if (path.StartsWith("/s3/", StringComparison.Ordinal))
        {
            return StoreFails() ?? S3(method, path["/s3/".Length..], uri, bytes, request);
        }

        return null;
    }

    /// <summary>Fails the store's request numbered <paramref name="request"/> and every repeat of it: a block that stays failed.</summary>
    public void FailBlock(int request)
        => SeismicStoreFailing.UnionWith(Enumerable.Range(request, SqlFlow.Delivery.Engine.Protocols.UploadParts.PartFailures));

    private HttpResponseMessage? StoreFails()
        => SeismicStoreFailing.Contains(++_storeRequests) ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;

    private HttpResponseMessage Seismic(string method, string path, Uri uri, string? body, HttpRequestMessage request)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);
        if (path == "/svcstatus" && method == "GET")
        {
            return SeismicText(HttpStatusCode.OK, "service OK");
        }

        if (path == "/svcstatus/access" && method == "GET")
        {
            return SeismicJson(HttpStatusCode.OK, new JsonObject { ["status"] = "running" });
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (parts is ["subproject", "tenant", var tenant, "subproject", var name] && method == "GET")
        {
            return SeismicSubprojects.TryGetValue($"{tenant}/{name}", out var sub)
                ? SeismicJson(HttpStatusCode.OK, new JsonObject
                {
                    ["name"] = name,
                    ["tenant"] = tenant,
                    ["ltag"] = sub.LegalTag,
                    ["access_policy"] = sub.Policy,
                    ["gcs_bucket"] = sub.Bucket,
                    ["acls"] = new JsonObject { ["admins"] = new JsonArray("data.sdms.admin@dev.example.com"), ["viewers"] = new JsonArray("data.sdms.viewer@dev.example.com") },
                })
                : SeismicText(HttpStatusCode.NotFound, $"[seismic-store-service] The subproject {name} does not exist");
        }

        if (parts is ["utility", "upload-connection-string"] && method == "GET")
        {
            var sdpath = query["sdpath"] ?? string.Empty;
            return SeismicDatasets.TryGetValue(sdpath, out var dataset)
                ? SeismicJson(HttpStatusCode.OK, Credentials(dataset))
                : SeismicText(HttpStatusCode.NotFound, $"[seismic-store-service] The dataset {sdpath} does not exist");
        }

        // dataset / tenant / {t} / subproject / {s} / dataset / {name} [/ lock | / unlock]
        if (parts.Length < 7 || parts[0] != "dataset" || parts[1] != "tenant" || parts[3] != "subproject" || parts[5] != "dataset")
        {
            return SeismicText(HttpStatusCode.NotFound, "[seismic-store-service] no route " + path);
        }

        return SeismicDatasetCall(method, parts, query, body, request);
    }

    /// <summary>The calls on one dataset: register, read, patch and close, lock, unlock and delete.</summary>
    private HttpResponseMessage SeismicDatasetCall(string method, string[] parts, System.Collections.Specialized.NameValueCollection query, string? body, HttpRequestMessage request)
    {
        var (tenant, subproject, name) = (parts[2], parts[4], parts[6]);
        var action = parts.Length > 7 ? parts[7] : string.Empty;
        var folder = (query["path"] ?? string.Empty).Trim('/');
        var sd = $"sd://{tenant}/{subproject}/{(folder.Length == 0 ? string.Empty : folder + "/")}{name}";
        if (!SeismicSubprojects.TryGetValue($"{tenant}/{subproject}", out var sub))
        {
            return SeismicText(HttpStatusCode.NotFound, $"[seismic-store-service] The subproject {subproject} does not exist");
        }

        var lockId = request.Headers.TryGetValues(SeismicLockHeader, out var ids) ? ids.Single() : null;
        SeismicDatasets.TryGetValue(sd, out var existing);
        SeismicLocks.TryGetValue(sd, out var held);
        switch (method, action)
        {
            case ("POST", ""):
                {
                    if (lockId is not null && !lockId.StartsWith('W'))
                    {
                        return SeismicText(HttpStatusCode.BadRequest, "[seismic-store-service] The lock id must start with W");
                    }

                    if (held is not null)
                    {
                        if (held != lockId)
                        {
                            return SeismicText((HttpStatusCode)423, $"[seismic-store-service] {sd} is write locked [RCODE:WL86400]");
                        }

                        // A replay: the stored dataset, or nothing when the first call never saved one.
                        return existing is null ? SeismicJson(HttpStatusCode.OK, new JsonObject()) : SeismicJson(HttpStatusCode.OK, Stored(existing, held, withSuffix: false));
                    }

                    if (existing is not null)
                    {
                        return SeismicText(HttpStatusCode.Conflict, $"[seismic-store-service] The dataset {sd} already exists");
                    }

                    SeismicLocks[sd] = lockId ?? "W" + Guid.NewGuid().ToString("N")[..15];
                    if (SeismicRegisterEmptyOnce)
                    {
                        SeismicRegisterEmptyOnce = false;
                        return SeismicJson(HttpStatusCode.OK, new JsonObject());
                    }

                    var register = body is null ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
                    var dataset = new JsonObject
                    {
                        ["name"] = name,
                        ["tenant"] = tenant,
                        ["subproject"] = subproject,
                        ["path"] = "/" + (folder.Length == 0 ? string.Empty : folder + "/"),
                        ["created_by"] = "delivery-app",
                        ["created_date"] = "2026-09-17T12:00:00Z",
                        ["last_modified_date"] = "2026-09-17T12:00:00Z",
                        ["gcsurl"] = Location(sub.Bucket),
                        ["ctag"] = NewCtag(),
                        ["ltag"] = request.Headers.TryGetValues("ltag", out var ltags) ? ltags.Single() : sub.LegalTag,
                        ["readonly"] = false,
                    };
                    if (register["seismicmeta"] is JsonObject record)
                    {
                        dataset["seismicmeta_guid"] = record["id"]?.GetValue<string>();
                        if (!SeismicSkipsStorage)
                        {
                            Put((JsonObject)record.DeepClone());
                        }
                    }

                    SeismicDatasets[sd] = dataset;
                    return SeismicJson(HttpStatusCode.OK, Stored(dataset, SeismicLocks[sd], withSuffix: true));
                }

            case ("GET", ""):
                return existing is null
                    ? SeismicText(HttpStatusCode.NotFound, $"[seismic-store-service] The dataset {sd} does not exist")
                    : SeismicJson(HttpStatusCode.OK, Stored(existing, held, withSuffix: true));

            case ("PATCH", ""):
                {
                    var close = query["close"];
                    if (close is not null)
                    {
                        if (held is not null && held != close)
                        {
                            return SeismicText(HttpStatusCode.NotFound, $"[seismic-store-service] {sd} has been locked with different ID");
                        }

                        SeismicLocks.Remove(sd);
                    }

                    if (existing is null)
                    {
                        return SeismicText(HttpStatusCode.NotFound, $"[seismic-store-service] The dataset {sd} does not exist");
                    }

                    var patch = string.IsNullOrEmpty(body) ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
                    if (close is null && patch.Count == 0)
                    {
                        return SeismicText(HttpStatusCode.BadRequest, "[seismic-store-service] The request body is empty");
                    }

                    if (patch["filemetadata"] is JsonObject metadata)
                    {
                        var stored = existing["filemetadata"] as JsonObject ?? new JsonObject();
                        foreach (var (key, value) in metadata)
                        {
                            stored[key] = value?.DeepClone();
                        }

                        existing["filemetadata"] = stored;
                    }

                    if (patch["readonly"] is JsonValue flag)
                    {
                        existing["readonly"] = flag.GetValue<bool>();
                    }

                    if (patch["seismicmeta"] is JsonObject record)
                    {
                        existing["seismicmeta_guid"] = record["id"]?.GetValue<string>();
                        if (!SeismicSkipsStorage)
                        {
                            Put((JsonObject)record.DeepClone());
                        }
                    }

                    existing["ctag"] = NewCtag();
                    return SeismicJson(HttpStatusCode.OK, Stored(existing, SeismicLocks.GetValueOrDefault(sd), withSuffix: true));
                }

            case ("PUT", "lock"):
                {
                    if (existing is null)
                    {
                        return SeismicText(HttpStatusCode.NotFound, $"[seismic-store-service] The dataset {sd} does not exist");
                    }

                    if (query["openmode"] == "write" && existing["readonly"]?.GetValue<bool>() == true)
                    {
                        return SeismicText(HttpStatusCode.BadRequest, $"[seismic-store-service] The dataset {sd} is read only and cannot be locked for write");
                    }

                    if (held is not null && held != lockId)
                    {
                        return SeismicText((HttpStatusCode)423, $"[seismic-store-service] {sd} is write locked [RCODE:WL86400]");
                    }

                    SeismicLocks[sd] = lockId ?? "W" + Guid.NewGuid().ToString("N")[..15];
                    return SeismicJson(HttpStatusCode.OK, Stored(existing, SeismicLocks[sd], withSuffix: true));
                }

            case ("PUT", "unlock"):
                if (held is null && existing is null)
                {
                    return SeismicText(HttpStatusCode.NotFound, $"[seismic-store-service] The dataset {sd} does not exist");
                }

                SeismicLocks.Remove(sd);
                return existing is null ? SeismicJson(HttpStatusCode.OK, new JsonObject()) : SeismicJson(HttpStatusCode.OK, Stored(existing, null, withSuffix: true));

            case ("DELETE", ""):
                {
                    if (existing is null)
                    {
                        return new HttpResponseMessage(HttpStatusCode.OK);
                    }

                    // gc passes the subproject folder as the dataset folder, so every dataset of the subproject goes.
                    var location = existing["gcsurl"]!.GetValue<string>();
                    var scope = SeismicProvider == "gc" ? string.Join('/', location.Split('/').Take(2)) + "/" : location.Replace("$$", "/", StringComparison.Ordinal) + "/";
                    foreach (var key in SeismicObjects.Keys.Where(k => k.StartsWith(scope, StringComparison.Ordinal)).ToList())
                    {
                        SeismicObjects.Remove(key);
                    }

                    SeismicDatasets.Remove(sd);
                    SeismicLocks.Remove(sd);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
        }

        return SeismicText(HttpStatusCode.NotFound, "[seismic-store-service] no dataset route " + method + " " + action);
    }

    /// <summary>The upload credentials a dataset gets on the provider the fake runs as (section 4.3).</summary>
    private JsonObject Credentials(JsonObject dataset)
    {
        var n = ++_seismicIssued;
        var location = dataset["gcsurl"]!.GetValue<string>();
        switch (SeismicProvider)
        {
            case "azure":
                return new JsonObject
                {
                    ["access_token"] = $"{Endpoint}/blob/{location}?sv=2021-08-06&sr=c&sp=racwdl&sig=sig{n}",
                    ["token_type"] = "SasUrl",
                    ["expires_in"] = 3599,
                };
            case "gc":
                return new JsonObject { ["access_token"] = $"gcs-token-{n}", ["token_type"] = "Bearer", ["expires_in"] = 3_599_000 };
            default:
                S3Keys[$"AKID{n}"] = ($"secret{n}", $"session{n}");
                return new JsonObject { ["access_token"] = $"AKID{n}:secret{n}:session{n}", ["token_type"] = "Bearer", ["expires_in"] = 3599 };
        }
    }

    /// <summary>Where a new dataset's objects go, in the form the provider writes <c>gcsurl</c> (section 4.2).</summary>
    private string Location(string bucket)
    {
        var id = Guid.NewGuid().ToString("N");
        return SeismicProvider switch
        {
            "gc" => $"{bucket}/sp0001/{id}",
            "anthos" => $"{bucket}$$folder01/{id}",
            _ => $"{bucket}/{id}",
        };
    }

    private static JsonObject Stored(JsonObject dataset, string? held, bool withSuffix)
    {
        var copy = (JsonObject)dataset.DeepClone();
        copy["sbit"] = held;
        copy["sbit_count"] = held is null ? 0 : 1;
        if (withSuffix)
        {
            copy["ctag"] = copy["ctag"]!.GetValue<string>() + "dev-gcp;dev";
            copy["access_policy"] = "uniform";
        }

        return copy;
    }

    private static string NewCtag() => RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 16);

    private HttpResponseMessage SeismicText(HttpStatusCode status, string text)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "text/html") };
        response.Headers.TryAddWithoutValidation("Service-Provider", SeismicProvider);
        return response;
    }

    private HttpResponseMessage SeismicJson(HttpStatusCode status, JsonNode node)
    {
        var response = Json(status, node);
        response.Headers.TryAddWithoutValidation("Service-Provider", SeismicProvider);
        return response;
    }

    /// <summary>Azure block blobs under a container SAS: staged blocks, block lists, whole blobs and deletes.</summary>
    private HttpResponseMessage AzureBlob(string method, string path, Uri uri, byte[] bytes, HttpRequestMessage request)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);
        var sig = query["sig"];
        if (sig is null || SeismicExpired.Contains(sig))
        {
            return AzureError(HttpStatusCode.Forbidden, "AuthenticationFailed");
        }

        if (!request.Headers.TryGetValues("x-ms-version", out var versions) || versions.Single() != query["sv"])
        {
            return AzureError(HttpStatusCode.BadRequest, "InvalidHeaderValue");
        }

        var blob = Uri.UnescapeDataString(path);
        switch (method, query["comp"])
        {
            case ("PUT", "block"):
                {
                    var md5 = request.Content?.Headers.ContentMD5;
                    if (md5 is not null && !StoreMd5(bytes).SequenceEqual(md5))
                    {
                        return AzureError(HttpStatusCode.BadRequest, "Md5Mismatch");
                    }

                    AzureBlocks[(blob, query["blockid"]!)] = bytes;
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }

            case ("PUT", "blocklist"):
                {
                    using var assembled = new MemoryStream();
                    foreach (var id in XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root!.Elements().Select(e => e.Value))
                    {
                        if (!AzureBlocks.Remove((blob, id), out var block))
                        {
                            return AzureError(HttpStatusCode.BadRequest, "InvalidBlockList");
                        }

                        assembled.Write(block);
                    }

                    SeismicObjects[blob] = assembled.ToArray();
                    AzureBlobMd5[blob] = request.Headers.TryGetValues("x-ms-blob-content-md5", out var md5) ? md5.Single() : Convert.ToBase64String(StoreMd5(assembled.ToArray()));
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }

            case ("PUT", null) when request.Headers.TryGetValues("x-ms-blob-type", out var types) && types.Single() == "BlockBlob":
                SeismicObjects[blob] = bytes;
                AzureBlobMd5[blob] = request.Headers.TryGetValues("x-ms-blob-content-md5", out var whole) ? whole.Single() : Convert.ToBase64String(StoreMd5(bytes));
                return new HttpResponseMessage(HttpStatusCode.Created);

            case ("DELETE", null):
                return SeismicObjects.Remove(blob) ? new HttpResponseMessage(HttpStatusCode.Accepted) : AzureError(HttpStatusCode.NotFound, "BlobNotFound");
        }

        return AzureError(HttpStatusCode.BadRequest, "UnsupportedQueryParameter");
    }

    private static HttpResponseMessage AzureError(HttpStatusCode status, string code)
        => new(status) { Content = new StringContent($"<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>{code}</Code><Message>{code}</Message></Error>", Encoding.UTF8, "application/xml") };

    /// <summary>Google Cloud Storage's resumable uploads, their status queries, and deletes.</summary>
    private HttpResponseMessage Gcs(string method, string path, Uri uri, byte[] bytes, HttpRequestMessage request)
    {
        var token = request.Headers.Authorization?.Parameter;
        if (request.Headers.Authorization?.Scheme != "Bearer" || token is null || !token.StartsWith("gcs-token-", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (SeismicExpired.Contains(token))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":{\"code\":401,\"message\":\"Invalid Credentials\"}}", Encoding.UTF8, "application/json") };
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        if (method == "POST" && path.StartsWith("/upload/storage/v1/b/", StringComparison.Ordinal) && query["uploadType"] == "resumable")
        {
            var bucket = Uri.UnescapeDataString(path["/upload/storage/v1/b/".Length..].Split('/')[0]);
            var length = long.Parse(request.Headers.GetValues("X-Upload-Content-Length").Single(), CultureInfo.InvariantCulture);
            var id = (++_gcsSessions).ToString(CultureInfo.InvariantCulture);
            GcsSessions[id] = (bucket, query["name"]!, length, new MemoryStream(), false);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Location = new Uri($"{GcsEndpoint}/session/{id}?upload_id=secret-upload-{id}");
            return response;
        }

        if (method == "PUT" && path.StartsWith("/session/", StringComparison.Ordinal))
        {
            var id = path["/session/".Length..];
            if (!GcsSessions.TryGetValue(id, out var session))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var range = request.Content?.Headers.TryGetValues("Content-Range", out var ranges) == true ? ranges.Single() : string.Empty;
            if (range.StartsWith("bytes */", StringComparison.Ordinal))
            {
                if (session.Length == 0 && !session.Done)
                {
                    session = session with { Done = true };
                    GcsSessions[id] = session;
                    SeismicObjects[$"{session.Bucket}/{session.Name}"] = [];
                }

                return session.Done ? GcsObject(session) : GcsIncomplete(session.Received.Length);
            }

            var dash = range.IndexOf('-', StringComparison.Ordinal);
            var first = long.Parse(range["bytes ".Length..dash], CultureInfo.InvariantCulture);
            if (first != session.Received.Length)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"code\":400,\"message\":\"Invalid request\"}}") };
            }

            var take = GcsHalfPieces && bytes.Length > 1 && first + bytes.Length < session.Length ? bytes.Length / 2 : bytes.Length;
            session.Received.Write(bytes, 0, take);
            if (session.Received.Length == session.Length)
            {
                session = session with { Done = true };
                GcsSessions[id] = session;
                SeismicObjects[$"{session.Bucket}/{session.Name}"] = session.Received.ToArray();
            }

            if (GcsLoseNextAnswer)
            {
                GcsLoseNextAnswer = false;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return session.Done ? GcsObject(session) : GcsIncomplete(session.Received.Length);
        }

        if (method == "DELETE" && path.StartsWith("/storage/v1/b/", StringComparison.Ordinal))
        {
            var rest = path["/storage/v1/b/".Length..];
            var bucket = Uri.UnescapeDataString(rest[..rest.IndexOf('/', StringComparison.Ordinal)]);
            var name = Uri.UnescapeDataString(rest[(rest.IndexOf("/o/", StringComparison.Ordinal) + 3)..]);
            return SeismicObjects.Remove($"{bucket}/{name}") ? new HttpResponseMessage(HttpStatusCode.NoContent) : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage GcsObject((string Bucket, string Name, long Length, MemoryStream Received, bool Done) session)
        => Json(HttpStatusCode.OK, new JsonObject
        {
            ["bucket"] = session.Bucket,
            ["name"] = session.Name,
            ["size"] = session.Length.ToString(CultureInfo.InvariantCulture),
            ["crc32c"] = Crc32C.ToBase64(Crc32C.Append(0, session.Received.ToArray())),
        });

    private static HttpResponseMessage GcsIncomplete(long received)
    {
        var response = new HttpResponseMessage((HttpStatusCode)308);
        if (received > 0)
        {
            response.Headers.TryAddWithoutValidation("Range", string.Create(CultureInfo.InvariantCulture, $"bytes=0-{received - 1}"));
        }

        return response;
    }

    /// <summary>An S3 store with path-style addresses: single puts, multipart uploads and deletes, every request's signature checked.</summary>
    private HttpResponseMessage S3(string method, string path, Uri uri, byte[] bytes, HttpRequestMessage request)
    {
        if (S3Refusal(request) is { } refused)
        {
            return refused;
        }

        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
        {
            return S3Error(HttpStatusCode.BadRequest, "InvalidRequest");
        }

        var bucket = Uri.UnescapeDataString(path[..slash]);
        var key = Uri.UnescapeDataString(path[(slash + 1)..]);
        var query = HttpUtility.ParseQueryString(uri.Query);
        var hasUploads = uri.Query.TrimStart('?').Split('&').Contains("uploads");
        switch (method)
        {
            case "POST" when hasUploads:
                {
                    var id = "upload-" + Guid.NewGuid().ToString("N");
                    S3Uploads[id] = (bucket, key, new SortedDictionary<int, byte[]>());
                    return S3Xml(HttpStatusCode.OK, $"<InitiateMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Bucket>{bucket}</Bucket><Key>{key}</Key><UploadId>{id}</UploadId></InitiateMultipartUploadResult>");
                }

            case "PUT" when query["uploadId"] is { } id && query["partNumber"] is { } number:
                {
                    if (!S3Uploads.TryGetValue(id, out var upload))
                    {
                        return S3Error(HttpStatusCode.NotFound, "NoSuchUpload");
                    }

                    if (Md5Refusal(request, bytes) is { } bad)
                    {
                        return bad;
                    }

                    upload.Parts[int.Parse(number, CultureInfo.InvariantCulture)] = bytes;
                    var response = new HttpResponseMessage(HttpStatusCode.OK);
                    response.Headers.TryAddWithoutValidation("ETag", "\"" + Convert.ToHexStringLower(StoreMd5(bytes)) + "\"");
                    return response;
                }

            case "POST" when query["uploadId"] is { } id:
                {
                    if (!S3Uploads.Remove(id, out var upload))
                    {
                        return S3Error(HttpStatusCode.NotFound, "NoSuchUpload");
                    }

                    var listed = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Descendants().Where(e => e.Name.LocalName == "Part").ToList();
                    using var assembled = new MemoryStream();
                    foreach (var part in listed)
                    {
                        var number = int.Parse(part.Elements().Single(e => e.Name.LocalName == "PartNumber").Value, CultureInfo.InvariantCulture);
                        var etag = part.Elements().Single(e => e.Name.LocalName == "ETag").Value;
                        if (!upload.Parts.TryGetValue(number, out var data) || etag != "\"" + Convert.ToHexStringLower(StoreMd5(data)) + "\"")
                        {
                            return S3Xml(HttpStatusCode.OK, "<Error><Code>InvalidPart</Code><Message>One or more of the specified parts could not be found.</Message></Error>");
                        }

                        assembled.Write(data);
                    }

                    SeismicObjects[$"{upload.Bucket}/{upload.Key}"] = assembled.ToArray();
                    return S3Xml(HttpStatusCode.OK, $"<CompleteMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Bucket>{upload.Bucket}</Bucket><Key>{upload.Key}</Key></CompleteMultipartUploadResult>");
                }

            case "DELETE" when query["uploadId"] is { } id:
                S3Uploads.Remove(id);
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            case "PUT":
                {
                    if (Md5Refusal(request, bytes) is { } bad)
                    {
                        return bad;
                    }

                    SeismicObjects[$"{bucket}/{key}"] = bytes;
                    var response = new HttpResponseMessage(HttpStatusCode.OK);
                    response.Headers.TryAddWithoutValidation("ETag", "\"" + Convert.ToHexStringLower(StoreMd5(bytes)) + "\"");
                    return response;
                }

            case "DELETE":
                SeismicObjects.Remove($"{bucket}/{key}");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return S3Error(HttpStatusCode.MethodNotAllowed, "MethodNotAllowed");
    }

    /// <summary>
    /// Why the store refuses a request's signature, or null. The canonical request is built here from the SigV4 rules (each
    /// path segment and query part encoded once, sorted, headers lowercased and trimmed), and signed with the AWS SDK.
    /// </summary>
    private HttpResponseMessage? S3Refusal(HttpRequestMessage request)
    {
        var authorization = request.Headers.Authorization;
        if (authorization?.Scheme != "AWS4-HMAC-SHA256" || authorization.Parameter is not { } parameter)
        {
            return S3Error(HttpStatusCode.Forbidden, "AccessDenied");
        }

        var fields = parameter.Split(", ").Select(f => f.Split('=', 2)).ToDictionary(f => f[0], f => f[1], StringComparer.Ordinal);
        var scope = fields["Credential"].Split('/');
        var (accessKey, day, region, service) = (scope[0], scope[1], scope[2], scope[3]);
        if (!S3Keys.TryGetValue(accessKey, out var key))
        {
            return S3Error(HttpStatusCode.Forbidden, "InvalidAccessKeyId");
        }

        var session = request.Headers.TryGetValues("x-amz-security-token", out var sessions) ? sessions.Single() : null;
        if (session != key.Session)
        {
            return S3Error(HttpStatusCode.Forbidden, "InvalidToken");
        }

        if (SeismicExpired.Contains(session))
        {
            return S3Error(HttpStatusCode.BadRequest, "ExpiredToken");
        }

        var stamp = request.Headers.GetValues(AmzDate).Single();
        var signedAt = DateTime.ParseExact(stamp, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        if (stamp[..8] != day)
        {
            return S3Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        }

        var uri = request.RequestUri!;
        var names = fields["SignedHeaders"].Split(';');
        if (!names.Contains("host") || !names.Contains("x-amz-date") || !names.Contains("x-amz-content-sha256") || !names.Contains("x-amz-security-token"))
        {
            return S3Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
        }

        var canonical = new StringBuilder();
        canonical.Append(request.Method.Method).Append('\n');
        canonical.Append(string.Join('/', uri.AbsolutePath.Split('/').Select(s => SpecEncode(Uri.UnescapeDataString(s))))).Append('\n');
        var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => (SpecEncode(Uri.UnescapeDataString(p[0])), SpecEncode(Uri.UnescapeDataString(p.Length > 1 ? p[1] : string.Empty))))
            .OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal);
        canonical.Append(string.Join('&', pairs.Select(p => p.Item1 + "=" + p.Item2))).Append('\n');
        foreach (var name in names)
        {
            var value = name switch
            {
                "host" => uri.Authority,
                "content-md5" => Convert.ToBase64String(request.Content!.Headers.ContentMD5!),
                _ => string.Join(',', request.Headers.GetValues(name)),
            };
            canonical.Append(name).Append(':').Append(value.Trim()).Append('\n');
        }

        canonical.Append('\n').Append(fields["SignedHeaders"]).Append('\n').Append(request.Headers.GetValues("x-amz-content-sha256").Single());
        var expected = AWS4Signer.ComputeSignature(accessKey, key.Secret, region, signedAt, service, fields["SignedHeaders"], canonical.ToString()).Signature;
        return expected == fields["Signature"] ? null : S3Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch");
    }

    private static HttpResponseMessage? Md5Refusal(HttpRequestMessage request, byte[] bytes)
        => request.Content?.Headers.ContentMD5 is { } md5 && !StoreMd5(bytes).SequenceEqual(md5) ? S3Error(HttpStatusCode.BadRequest, "BadDigest") : null;

    /// <summary>A value encoded once, as the SigV4 specification writes it: the RFC 3986 unreserved characters kept, every other UTF-8 byte escaped in upper case.</summary>
    private static string SpecEncode(string value)
    {
        const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~";
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            builder.Append(Unreserved.Contains((char)b, StringComparison.Ordinal) ? ((char)b).ToString() : "%" + b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>The MD5 the stores keep and check (Content-MD5, ETags, blob content MD5).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "The fake stores check the MD5 checksums the real stores define; no security rests on them.")]
    private static byte[] StoreMd5(byte[] bytes) => MD5.HashData(bytes);

    private static HttpResponseMessage S3Error(HttpStatusCode status, string code)
        => S3Xml(status, $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{code}</Message></Error>");

    private static HttpResponseMessage S3Xml(HttpStatusCode status, string xml)
        => new(status) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
}
