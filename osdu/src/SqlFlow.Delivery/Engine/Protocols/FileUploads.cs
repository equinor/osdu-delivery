using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>One payload chunk after its upload: what it is called, how long it is, and where the landing zone keeps it.</summary>
internal sealed record UploadedFile(int Index, string Name, long Size, string FileSource, string? FileId);

/// <summary>
/// The file service half the file and manifest protocols share (openapi file v2). Per chunk, <c>GET files/uploadURL</c>
/// hands out a signed landing-zone location and the FileSource path the file keeps; the chunk streams to the signed
/// URL with its length (the URL carries its own authorisation, so only the declared upload headers go with it); the
/// file protocol then registers the dataset record against that FileSource through <c>POST files/metadata</c>.
/// Every chunk is its own resumable step (osdu/docs/reference/concepts/protocols.md, Steps and returned values): a
/// retry after the third of five uploads sends two, not five, and a retry after a failed registration registers the
/// file it already uploaded.
/// </summary>
internal static class FileUploads
{
    public const string DefaultUploadUrlPath = "/api/file/v2/files/uploadURL";
    public const string DefaultFileMetadataPath = "/api/file/v2/files/metadata";
    public const string DefaultFileDeletePath = "/api/file/v2/files/{id}/metadata";
    public const string DefaultFileProbePath = "/api/file/v2/info";

    /// <summary>The target-state value listing the dataset record ids the record's files became, comma separated.</summary>
    public const string DatasetIdsValue = "datasetIds";

    /// <summary>
    /// What a registration step holds while its request is in flight: the landing-zone path it is registering, and
    /// no dataset id, so the step never counts as completed and the next try knows what to look the dataset up by.
    /// </summary>
    public const string RegisteringState = "registering";

    public static string UploadStep(int index) => "upload-" + index.ToString(CultureInfo.InvariantCulture);

    public static string RegisterStep(int index) => "register-" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The slot a registration's artifact is kept under (osdu/docs/reference/concepts/artifacts-and-undo.md, file):
    /// the file's place and the landing-zone path it was uploaded to, hashed to a fixed width. A try that uploads the
    /// file again lands it at a new path, so each registration it may have made is an artifact of its own, and none is
    /// lost when an answer was.
    /// </summary>
    public static string DatasetSlot(UploadedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.FileSource)))[..24].ToLowerInvariant();
        return string.Create(CultureInfo.InvariantCulture, $"file:{file.Index}:{hash}");
    }

    /// <summary>
    /// Lists the payload chunks and checks each has something in it before anything is sent, so an empty file holds the
    /// record instead of failing after some uploads (osdu/docs/reference/flow/routes.md, file).
    /// </summary>
    public static async Task<IReadOnlyList<PayloadFile>> ListChunksAsync(DeliveryWork work, CancellationToken ct)
    {
        var payload = work.Payload ?? throw new RecordHeldException("the record needs a payload but none is attached");
        var chunks = await payload.ListChunksAsync(ct).ConfigureAwait(false);
        if (chunks.Count == 0)
        {
            throw new RecordHeldException("no payload chunk files were found for the record");
        }

        foreach (var chunk in chunks)
        {
            if (chunk.Size <= 0)
            {
                throw new RecordHeldException($"payload chunk {chunk.Index} ({FileName(chunk.Path)}) is empty; an empty file cannot be registered as a dataset");
            }
        }

        return chunks;
    }

    /// <summary>
    /// Holds a file that has to go in one request and is above the declared request body ceiling. It is read once the
    /// location is known, since a location that takes blocks never carries more than the ceiling in a request.
    /// </summary>
    public static void CheckCeiling(PayloadFile chunk, long requestBodyCeiling)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (requestBodyCeiling > 0 && chunk.Size > requestBodyCeiling)
        {
            throw new RecordHeldException(
                $"payload chunk {chunk.Index} is {chunk.Size.ToString(CultureInfo.InvariantCulture)} bytes, above the target's declared request body ceiling of {requestBodyCeiling.ToString(CultureInfo.InvariantCulture)} bytes (reliability.maxRequestBodyBytes); re-chunk in prepare");
        }
    }

    /// <summary>Uploads the chunks an earlier try did not, reporting each as a step, and returns where every chunk landed.</summary>
    public static async Task<List<UploadedFile>> UploadAsync(OsduHttpClient client, ProtocolOptions options, DeliveryWork work, IReadOnlyList<PayloadFile> chunks, long requestBodyCeiling, DeliverySteps steps, CancellationToken ct)
    {
        var payload = work.Payload ?? throw new RecordHeldException("the record needs a payload but none is attached");
        var files = new List<UploadedFile>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var step = UploadStep(chunk.Index);
            var name = FileName(chunk.Path);
            if (work.Completed(step) is { } done && done.TryGetValue("fileSource", out var landed) && !string.IsNullOrEmpty(landed))
            {
                steps.Resumed(step, done);
                files.Add(new UploadedFile(chunk.Index, name, chunk.Size, landed, done.TryGetValue("fileId", out var fileId) ? fileId : null));
                continue;
            }

            var started = steps.Now;
            var location = await UploadLocationAsync(client, options, ct).ConfigureAwait(false);
            var sent = await SignedUploads.PutAsync(client, options, location.SignedUrl, payload, chunk, requestBodyCeiling, wantsMd5: true, ct).ConfigureAwait(false);
            var returned = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fileSource"] = location.FileSource,
                ["name"] = name,
                ["size"] = chunk.Size.ToString(CultureInfo.InvariantCulture),
            };
            if (location.FileId is not null)
            {
                returned["fileId"] = location.FileId;
            }

            sent.AddTo(returned);
            steps.Add(step, started, sent.Status, returned);
            await work.ReportStepAsync(step, returned, ct).ConfigureAwait(false);
            files.Add(new UploadedFile(chunk.Index, name, chunk.Size, location.FileSource, location.FileId));
        }

        return files;
    }

    /// <summary>
    /// Registers the dataset record of an uploaded file (openapi file v2, POST files/metadata) and returns its id.
    /// The step is marked before the request goes out, because the service mints a new dataset record for every
    /// accepted POST: a try that dies between the response and the report would otherwise register the same file
    /// again and leave the first dataset in OSDU with nothing referencing it.
    /// </summary>
    public static async Task<string> RegisterAsync(OsduHttpClient client, ProtocolOptions options, DeliveryWork work, UploadedFile file, DeliverySteps steps, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(steps);
        var step = RegisterStep(file.Index);
        if (work.Completed(step) is { } done)
        {
            if (done.TryGetValue("datasetId", out var known) && !string.IsNullOrEmpty(known))
            {
                steps.Resumed(step, done);
                return known;
            }

            // Marked but never completed: an earlier try was sending this registration when it stopped, and whether
            // the service accepted it is unknown. Ask what it registered for this landing-zone path instead of
            // registering a second dataset for the same file.
            if (done.TryGetValue("fileSource", out var attempted)
                && string.Equals(attempted, file.FileSource, StringComparison.Ordinal)
                && await RegisteredForAsync(client, options, time, file, ct).ConfigureAwait(false) is { } found)
            {
                var (adopted, extra) = (found[0], found.Skip(1).ToList());
                var recovered = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["datasetId"] = adopted,
                    ["fileSource"] = file.FileSource,
                    ["adopted"] = "true",
                };
                var artifacts = new List<TargetArtifact> { TargetArtifact.Created(DatasetSlot(file), ArtifactRoles.Dataset, adopted, locator: file.FileSource) };
                artifacts.AddRange(await RemoveExtraAsync(client, options, file, extra, ct).ConfigureAwait(false));
                if (extra.Count > 0)
                {
                    recovered["removedDuplicates"] = string.Join(",", extra);
                }

                steps.Resumed(step, recovered);
                await work.ReportStepAsync(step, recovered, artifacts, ct).ConfigureAwait(false);
                return adopted;
            }
        }

        var started = steps.Now;

        // The intent goes with the mark: the dataset this call may mint is in the ledger before the call is sent, findable by
        // where its file landed if the answer is lost.
        await work.ReportStepAsync(
            step,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["fileSource"] = file.FileSource, ["state"] = RegisteringState },
            [TargetArtifact.Intent(DatasetSlot(file), ArtifactRoles.Dataset, file.FileSource)],
            ct).ConfigureAwait(false);
        var url = client.Url(options.FileMetadataPath ?? DefaultFileMetadataPath);
        // Not repeated on an unclear outcome: every accepted POST mints another dataset record. The step is
        // resumable, so the next try of the record registers the file once.
        var result = await client.SendJsonAsync(HttpMethod.Post, url, DatasetRecord(options, work.Document, file), null, ct, idempotent: false).ConfigureAwait(false);
        var datasetId = JsonPathReader.SelectValue(OsduHttpClient.ParseJson(result, url), "id");
        if (string.IsNullOrWhiteSpace(datasetId))
        {
            throw new DeliveryException($"{url.AbsolutePath} did not return the id of the dataset record for {file.Name}.");
        }

        var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["datasetId"] = datasetId, ["fileSource"] = file.FileSource };
        steps.Add(step, started, (int)result.Status, returned);
        await work.ReportStepAsync(step, returned, [TargetArtifact.Created(DatasetSlot(file), ArtifactRoles.Dataset, datasetId, locator: file.FileSource)], ct).ConfigureAwait(false);
        return datasetId;
    }

    /// <summary>
    /// Removes reversibly the datasets a lost answer let a later try register again for one landing-zone path (the path is one
    /// upload's alone, so every dataset registered for it is this record's), and returns each as the artifact it is: removed by
    /// the route, or, when its removal failed, an intent naming it by id alone, which the unit's end makes due whatever the
    /// delivery comes to, so the sweep removes it. It carries no landing path: an undo that searched by the path would find the
    /// dataset the record keeps as well.
    /// </summary>
    private static async Task<IReadOnlyList<TargetArtifact>> RemoveExtraAsync(OsduHttpClient client, ProtocolOptions options, UploadedFile file, IReadOnlyList<string> extra, CancellationToken ct)
    {
        var artifacts = new List<TargetArtifact>(extra.Count);
        foreach (var id in extra)
        {
            var slot = DatasetSlot(file) + ":" + id;
            try
            {
                var outcome = await RecordWriter.DeleteAsync(client, RemovalPaths.From(options), id, RemovalScope.Record, ct).ConfigureAwait(false);
                artifacts.Add(TargetArtifact.Created(slot, ArtifactRoles.Dataset, id, locator: file.FileSource) with
                {
                    Status = ArtifactStatus.Removed,
                    Note = $"a second registration of {file.Name} for the same landing-zone path, {outcome.Detail}",
                });
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                artifacts.Add(TargetArtifact.Intent(slot, ArtifactRoles.Dataset, null, id) with
                {
                    Note = $"a second registration of {file.Name} for the landing-zone path {file.FileSource}, which could not be removed yet: {Http.HeaderRedaction.RedactMessage(ex.Message)}",
                });
            }
        }

        return artifacts;
    }

    /// <summary>
    /// Every dataset record the index lists for a landing-zone path, asked once (openapi search v2, POST query): what an undo
    /// finds a registration whose answer was lost by.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RegisteredAtAsync(OsduHttpClient client, ProtocolOptions options, string fileSource, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileSource);
        return await SearchFileSourceAsync(client, options, fileSource, MaxRegistrationsPerPath, ct).ConfigureAwait(false);
    }

    /// <summary>The most dataset records one landing-zone path is looked up for: more than one is already a repeated registration.</summary>
    private const int MaxRegistrationsPerPath = 20;

    private static async Task<IReadOnlyList<string>> SearchFileSourceAsync(OsduHttpClient client, ProtocolOptions options, string fileSource, int limit, CancellationToken ct)
    {
        var url = client.Url(options.SearchQueryPath ?? OsduManifestProtocol.DefaultSearchQueryPath);
        var body = new JsonObject
        {
            ["kind"] = options.DatasetKind,
            ["query"] = "data.DatasetProperties.FileSourceInfo.FileSource:\"" + fileSource.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
            ["limit"] = limit,
            ["returnedFields"] = new JsonArray(JsonValue.Create("id")),
        };
        var result = await client.SendJsonAsync(HttpMethod.Post, url, body, null, ct, idempotent: true).ConfigureAwait(false);
        var ids = new List<string>();
        foreach (var hit in JsonPathReader.SelectElements(OsduHttpClient.ParseJson(result, url), "results[*]"))
        {
            if (hit.ValueKind == JsonValueKind.Object
                && hit.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } text
                && !ids.Contains(text, StringComparer.Ordinal))
            {
                ids.Add(text);
            }
        }

        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    /// <summary>
    /// The dataset records the file service holds for a landing-zone path, asked of the search service (openapi search v2,
    /// POST query), the one the record keeps first; null when it lists none.
    ///
    /// The file service mints the dataset id itself and reads metadata back by that id alone (openapi file v2,
    /// GET files/{id}/metadata), so a registration whose response was lost leaves the landing-zone path as the only
    /// way back to it, and only search can answer by it. A dataset reaches the index a moment after it is
    /// registered, so the ask is repeated until <see cref="ProtocolOptions.DatasetIndexWaitSeconds"/> runs out, as
    /// the manifest protocol's wait for its own datasets does. Nothing listed means the registration never landed
    /// (or is still not indexed) and the file is registered again. Two datasets for one path are two registrations of this
    /// record's one upload: the record keeps the first in id order, and the caller removes the rest.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> RegisteredForAsync(OsduHttpClient client, ProtocolOptions options, TimeProvider time, UploadedFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(time);
        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(0, options.DatasetIndexWaitSeconds));
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.WorkflowPollSeconds));
        while (true)
        {
            var ids = await SearchFileSourceAsync(client, options, file.FileSource, MaxRegistrationsPerPath, ct).ConfigureAwait(false);
            if (ids.Count > 0)
            {
                return ids;
            }

            if (time.GetUtcNow() + interval > deadline)
            {
                return null;
            }

            await Task.Delay(interval, time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The dataset record of one uploaded file (openapi file v2, FileMetadata): the flow's ACL and legal tags copied
    /// from the rendered record, never restated, and
    /// FileSourceInfo pointing at the landing-zone path the upload returned.
    /// </summary>
    public static JsonObject DatasetRecord(ProtocolOptions options, JsonObject document, UploadedFile file)
    {
        var acl = document["acl"]?.DeepClone() ?? throw new DeliveryException("the rendered record has no acl block to copy onto its dataset record");
        var legal = document["legal"]?.DeepClone() ?? throw new DeliveryException("the rendered record has no legal block to copy onto its dataset record");
        var size = file.Size.ToString(CultureInfo.InvariantCulture);
        // No id: the file service mints the dataset id and ignores one the request supplies (observed on a live M26 service).
        var record = new JsonObject();
        record["kind"] = options.DatasetKind;
        record["acl"] = acl;
        record["legal"] = legal;
        record["data"] = new JsonObject
        {
            ["Name"] = file.Name,
            ["TotalSize"] = size,
            ["DatasetProperties"] = new JsonObject
            {
                ["FileSourceInfo"] = new JsonObject
                {
                    ["FileSource"] = file.FileSource,
                    ["Name"] = file.Name,
                    ["FileSize"] = size,
                },
            },
        };
        return record;
    }

    /// <summary>
    /// Points the record's dataset list at the given dataset records, keeping any the mapping rendered first, without
    /// duplicates. The list holds references, not record ids: the work product component schemas require each entry to
    /// be the id followed by a colon and an optional version (<c>^[\w\-\.]+:dataset\-\-[\w\-\.]+:[\w\-\.\:\%]+:[0-9]*$</c>).
    /// Storage keeps whatever it is given, but manifest ingestion validates the pattern and drops a record that breaks
    /// it while still creating its datasets (observed on a live M26 service), so the reference form is what is written.
    /// A rendered entry naming the same dataset, with the colon or without, is not repeated.
    /// </summary>
    public static void SetDatasets(JsonObject document, string property, IReadOnlyList<string> ids)
    {
        if (document["data"] is not JsonObject data)
        {
            data = new JsonObject();
            document["data"] = data;
        }

        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (data[property] is JsonArray existing)
        {
            foreach (var node in existing)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var text) && seen.Add(text.TrimEnd(':')))
                {
                    merged.Add(text);
                }
            }
        }

        foreach (var id in ids)
        {
            if (seen.Add(id.TrimEnd(':')))
            {
                merged.Add(DatasetReference(id));
            }
        }

        data[property] = new JsonArray(merged.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
    }

    /// <summary>A dataset record as a record's dataset list references it: the id and a colon, which leaves the version open.</summary>
    public static string DatasetReference(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return id.EndsWith(':') ? id : id + ":";
    }

    /// <summary>The dataset ids the record's earlier deliveries registered, from its target state.</summary>
    public static IReadOnlyList<string> DatasetIds(IReadOnlyDictionary<string, string>? targetState)
        => targetState is not null && targetState.TryGetValue(DatasetIdsValue, out var joined) && !string.IsNullOrWhiteSpace(joined)
            ? joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>
    /// Storage semantics for the record itself, plus the dataset records the record owns. Only
    /// <see cref="RemovalScope.Everything"/> takes the datasets and their files with it (openapi file v2,
    /// DELETE files/{id}/metadata): the reversible removal leaves them in place so OSDU can restore the record
    /// whole, and a history purge only touches the record's own earlier versions. A dataset already gone is not an
    /// error.
    /// </summary>
    public static async Task<DeleteOutcome> DeleteRecordAndDatasetsAsync(OsduHttpClient client, ProtocolOptions options, string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var outcome = await RecordWriter.DeleteAsync(client, RemovalPaths.From(options), targetId, scope, ct).ConfigureAwait(false);
        return scope == RemovalScope.Everything
            ? await WithDatasetsDeletedAsync(client, options, outcome, targetState, ct).ConfigureAwait(false)
            : outcome;
    }

    /// <summary>
    /// The dataset records a record's earlier deliveries registered through the file service, deleted with their files
    /// (openapi file v2, DELETE files/{id}/metadata), and <paramref name="outcome"/> saying so. A dataset already gone is
    /// not an error. Only a removal of everything calls this: the others leave a record's datasets where they are.
    /// </summary>
    public static async Task<DeleteOutcome> WithDatasetsDeletedAsync(OsduHttpClient client, ProtocolOptions options, DeleteOutcome outcome, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(outcome);
        var deleted = 0;
        foreach (var id in DatasetIds(targetState))
        {
            var url = client.Url(options.FileDeletePath ?? DefaultFileDeletePath, id);
            var result = await client.SendJsonAsync(HttpMethod.Delete, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
            if ((int)result.Status != 404)
            {
                deleted++;
            }
        }

        return deleted == 0
            ? outcome
            : outcome with { Deleted = true, Detail = $"{outcome.Detail}; {deleted.ToString(CultureInfo.InvariantCulture)} dataset record(s) and their files deleted" };
    }

    /// <summary>
    /// Uploads and registers the files of one payload part through the file service (<see cref="UploadAsync"/>,
    /// <see cref="RegisterAsync"/>), each a resumable step, and returns the dataset ids and how many files there were.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Ids, int Files)> UploadAndRegisterAsync(
        OsduHttpClient client, ProtocolOptions options, DeliveryWork work, IPayloadSource files, long requestBodyCeiling, DeliverySteps steps, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(files);
        var view = work with { Payload = files };
        var chunks = await ListChunksAsync(view, ct).ConfigureAwait(false);
        var uploaded = await UploadAsync(client, options, view, chunks, requestBodyCeiling, steps, ct).ConfigureAwait(false);
        var ids = new List<string>(uploaded.Count);
        foreach (var file in uploaded)
        {
            ids.Add(await RegisterAsync(client, options, view, file, steps, time, ct).ConfigureAwait(false));
        }

        return (ids, uploaded.Count);
    }

    /// <summary>The header Azure Blob Storage requires on a PUT that creates a blob, and the blob type a file upload is.</summary>
    public const string AzureBlobTypeHeader = "x-ms-blob-type";

    /// <summary>
    /// The headers the upload to a signed URL carries: the ones the flow declares, plus the blob type when the landing
    /// zone is Azure Blob Storage and the flow did not name one.
    ///
    /// A PUT that creates a blob must say which kind of blob it is, and Azure answers one without the header with
    /// 400 <c>MissingRequiredHeader</c>, after the file service has already handed out the location. OSDU on Azure
    /// hands out exactly such a URL, and the platform's own upload scripts send <c>x-ms-blob-type: BlockBlob</c> with
    /// it. Adding it only for an Azure blob keeps other landing zones (S3, Google Cloud Storage) receiving nothing
    /// they did not sign for; a value the flow declares always wins.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SignedUploadHeaders(Uri signedUrl, IReadOnlyDictionary<string, string> declared)
    {
        ArgumentNullException.ThrowIfNull(signedUrl);
        ArgumentNullException.ThrowIfNull(declared);
        if (!SignedUploads.IsAzureBlob(signedUrl) || declared.Keys.Any(k => string.Equals(k, AzureBlobTypeHeader, StringComparison.OrdinalIgnoreCase)))
        {
            return declared;
        }

        var headers = new Dictionary<string, string>(declared, StringComparer.OrdinalIgnoreCase)
        {
            [AzureBlobTypeHeader] = "BlockBlob",
        };
        return headers;
    }

    /// <summary>The last path segment, the name the dataset record carries.</summary>
    public static string FileName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static async Task<UploadLocation> UploadLocationAsync(OsduHttpClient client, ProtocolOptions options, CancellationToken ct)
    {
        var url = client.Url(options.UploadUrlPath ?? DefaultUploadUrlPath);
        if (!string.IsNullOrEmpty(options.UploadUrlExpiry))
        {
            var builder = new UriBuilder(url);
            var query = builder.Query.TrimStart('?');
            builder.Query = (query.Length == 0 ? string.Empty : query + "&") + "expiryTime=" + Uri.EscapeDataString(options.UploadUrlExpiry);
            url = builder.Uri;
        }

        var result = await client.SendJsonAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
        var root = OsduHttpClient.ParseJson(result, url);
        var signed = JsonPathReader.SelectValue(root, "Location.SignedURL");
        var fileSource = JsonPathReader.SelectValue(root, "Location.FileSource");
        if (string.IsNullOrWhiteSpace(signed) || string.IsNullOrWhiteSpace(fileSource))
        {
            throw new DeliveryException($"{url.AbsolutePath} did not return Location.SignedURL and Location.FileSource.");
        }

        if (!Uri.TryCreate(signed, UriKind.Absolute, out var signedUrl) || (signedUrl.Scheme != Uri.UriSchemeHttps && signedUrl.Scheme != Uri.UriSchemeHttp))
        {
            throw new DeliveryException($"{url.AbsolutePath} returned a signed upload URL that is not an absolute http(s) URL.");
        }

        return new UploadLocation(signedUrl, fileSource, JsonPathReader.SelectValue(root, "FileID"));
    }

    /// <summary>The signed URL is a credential: it is used for the one upload and never logged, stored or returned.</summary>
    private sealed record UploadLocation(Uri SignedUrl, string FileSource, string? FileId);
}
