using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A stateful stand-in for the OSDU services the Stage 6 and 7 routes call, built from their pinned contracts and briefs:
/// Storage, Search, Legal, the Dataset service with the staging locations each provider signs, the Workflow service with a
/// scripted engine behind it (what each workflow writes, the status it ends in and the XCom entries it pushes), the
/// Airflow REST API, the Wellbore, Well Delivery, RAFS and Production historian DDMSs, Seismic Store with the object
/// stores behind it (FakeOsduPlatform.Seismic.cs) and the Reservoir Management DDMS
/// (FakeOsduPlatform.ReservoirManagement.cs), and the Production DDMS core service (FakeOsduPlatform.Dspdm.cs). Every request
/// is recorded as <see cref="FakeHttpHandler"/> records them, so the contract harness checks them.
/// </summary>
public sealed partial class FakeOsduPlatform : HttpMessageHandler
{
    public const string Endpoint = "http://localhost";

    public const string AirflowEndpoint = "http://localhost/airflow";

    /// <summary>Where the Wellbore DDMS sits under the platform, as the flows name it with ddmsRoot.</summary>
    public const string DdmsRoot = "/api/os-wellbore-ddms";

    /// <summary>Where the Well Delivery DDMS sits under the platform (its charts' ingress prefix).</summary>
    public const string WellDeliveryRoot = "/api/well-delivery";

    /// <summary>Where the Rock and Fluid Sample DDMS sits under the platform (the prefix its contract is generated with).</summary>
    public const string RafsRoot = "/api/rafs-ddms";

    /// <summary>Where the Production DDMS historian's ingestion service sits under the platform (its contract's server).</summary>
    public const string TimeSeriesRoot = "/api/pddms/ingest/v1";

    /// <summary>Where the Production DDMS historian's query service sits under the platform (its contract's server).</summary>
    public const string TimeSeriesQueryRoot = "/api/pddms/query/v1";

    /// <summary>How the Dataset service signs a location, per provider (osdu/specs/core/INTEGRATION.md section 2.5.1).</summary>
    public enum Staging
    {
        /// <summary>Azure: a blob SAS for a file, a Data Lake directory SAS for a collection.</summary>
        Azure,

        /// <summary>Core-plus on MinIO: a presigned PUT for a file, a POST policy without a key for a collection.</summary>
        Minio,

        /// <summary>Core-plus on S3: a POST policy naming the directory as its key.</summary>
        S3,

        /// <summary>Core-plus on Google Cloud Storage: a folder token for a collection.</summary>
        Google,

        /// <summary>IBM: temporary credentials for an endpoint the location does not name.</summary>
        Ibm,
    }

    /// <summary>What one workflow does when it runs: what it writes, how it ends, and what its tasks push.</summary>
    public sealed class Script
    {
        /// <summary>The status each poll after the first answers; the first answers <see cref="Pending"/>.</summary>
        public string Terminal { get; init; } = "finished";

        public string Pending { get; init; } = string.Empty;

        /// <summary>What the run does to the platform once it is triggered: the records it writes, the files it stores.</summary>
        public Action<FakeOsduPlatform, Run>? Effect { get; init; }

        /// <summary>The XCom entries its tasks push, by task and key.</summary>
        public Func<Run, IReadOnlyDictionary<(string Task, string Key), JsonNode?>>? XCom { get; init; }

        /// <summary>The task the Workflow service's latestInfo names as the run's latest.</summary>
        public string LatestTask { get; init; } = "update_status_finished_task";
    }

    public sealed class Run(string workflow, string runId, JsonObject context)
    {
        public string Workflow { get; } = workflow;

        public string RunId { get; } = runId;

        public JsonObject Context { get; } = context;

        public int Polls { get; set; }

        public IReadOnlyDictionary<(string Task, string Key), JsonNode?> XCom { get; set; } = new Dictionary<(string, string), JsonNode?>();
    }

    private readonly object _gate = new();
    private readonly Dictionary<(string Record, string Series), int> _mappingReads = [];
    private int _locations;
    private int _ingestions;
    private long _timeSeriesClock = 1_781_770_405_994;

    public List<FakeHttpHandler.Request> Calls { get; } = [];

    public Dictionary<string, JsonObject> Records { get; } = new(StringComparer.Ordinal);

    /// <summary>Every version storage keeps of each record, oldest first, as each write stored it: what its version reads answer.</summary>
    public Dictionary<string, List<JsonObject>> History { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Removed { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Purged { get; } = new(StringComparer.Ordinal);

    /// <summary>The ids storage brought back from a soft delete through a merge patch, in the order it did.</summary>
    public List<string> Restores { get; } = [];

    /// <summary>
    /// Whether a write of <paramref name="sent"/> changes nothing storage compares for skipdupes in <paramref name="held"/>:
    /// data, meta, acl, legal, tags and ancestry, not the kind (osdu/specs/core/INTEGRATION.md section 2.2).
    /// </summary>
    private static bool SameForSkip(JsonObject held, JsonObject sent)
        => new[] { "data", "meta", "acl", "legal", "tags", "ancestry" }.All(block => JsonNode.DeepEquals(held[block], sent[block]));

    /// <summary>The workflows the partition registers, with what each does.</summary>
    public Dictionary<string, Script> Workflows { get; } = new(StringComparer.Ordinal);

    public List<Run> Runs { get; } = [];

    public Staging Provider { get; set; } = Staging.Azure;

    /// <summary>The bytes each staged file was uploaded with, by its path in the staging area.</summary>
    public Dictionary<string, byte[]> Staged { get; } = new(StringComparer.Ordinal);

    /// <summary>The bytes a registered dataset's retrieval URL serves, by dataset id.</summary>
    public Dictionary<string, byte[]> Content { get; } = new(StringComparer.Ordinal);

    /// <summary>What a search finds, from its kind and query.</summary>
    public Func<string, string?, IReadOnlyList<string>>? Search { get; set; }

    /// <summary>The ids retrieval instructions leave out, as the service leaves out an id it cannot read.</summary>
    public HashSet<string> Unretrievable { get; } = new(StringComparer.Ordinal);

    /// <summary>The Airflow REST API version the fake answers on.</summary>
    public string AirflowVersion { get; set; } = "v1";

    /// <summary>The bulk data the DDMS holds for each record, as the bytes it was sent.</summary>
    public Dictionary<string, byte[]> Bulk { get; } = new(StringComparer.Ordinal);

    /// <summary>The DDMS refuses a record write whose bulk link differs from the one it holds (wellbore-ddms brief section 4).</summary>
    public int RefusedLinks { get; private set; }

    /// <summary>The Well Delivery DDMS's store, by <c>type|entityId|version</c>, each entry the entity as written and whether it is deleted.</summary>
    public SortedDictionary<string, (JsonObject Entity, bool Deleted)> WellDeliveryEntities { get; } = new(StringComparer.Ordinal);

    /// <summary>The references the Well Delivery DDMS indexed, by the entity key that holds them (only those ending in a version).</summary>
    public Dictionary<string, List<string>> WellDeliveryIndex { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether the Well Delivery deployment copies each entity into storage (<c>app.entity.storage</c>).</summary>
    public bool WellDeliveryMirror { get; set; } = true;

    /// <summary>Whether the Well Delivery store is IBM's Cloudant, which refuses a second save of a key with a 500.</summary>
    public bool WellDeliveryCloudant { get; set; }

    /// <summary>A server error the Well Delivery DDMS answers the next write with, after it stored the entity.</summary>
    public bool WellDeliveryFailsAfterWrite { get; set; }

    /// <summary>Whether RAFS stores content as blobs (<c>USE_BLOB_STORAGE</c>) rather than registering datasets.</summary>
    public bool RafsBlobMode { get; set; }

    /// <summary>The content RAFS holds, by <c>record id|content type</c>: the bytes, the media type and the schema version.</summary>
    public Dictionary<string, (byte[] Bytes, string MediaType, string SchemaVersion)> RafsContent { get; } = new(StringComparer.Ordinal);

    /// <summary>The SamplesAnalysis content types RAFS serves, with their versions (a subset of the brief's section 3.3).</summary>
    public Dictionary<string, string[]> RafsAnalysisTypes { get; } = new(StringComparer.Ordinal)
    {
        ["nmr"] = ["1.0.0"],
        ["capillarypressure"] = ["1.0.0", "1.1.0"],
        ["routinecoreanalysis"] = ["1.0.0"],
    };

    /// <summary>The one partition the historian's ingestion deployment takes (its <c>DATA_PARTITION_ID</c>).</summary>
    public string TimeSeriesPartition { get; set; } = "dev";

    /// <summary>
    /// The versions the historian stores, by record and series, in the order it accepted them, each with its points by
    /// timestamp as they were sent.
    /// </summary>
    public Dictionary<(string Record, string Series), List<(long Version, SortedDictionary<long, JsonNode?> Points)>> TimeSeries { get; } = [];

    /// <summary>How many reads of a series the query service answers 404 "Failed to get a Stream Mapping" before the mapping exists.</summary>
    public int TimeSeriesMappingDelay { get; set; }

    /// <summary>Series whose next accepted version the historian loses after answering 202, as a publish that fails its retries does.</summary>
    public HashSet<string> TimeSeriesLost { get; } = new(StringComparer.Ordinal);

    /// <summary>The most points one read of the query service returns, as its page limit cuts a longer answer without saying so.</summary>
    public int TimeSeriesReadLimit { get; set; } = int.MaxValue;

    /// <summary>The ingestion requests, counted from 1, the service answers 500 "Storage service error" instead of taking.</summary>
    public HashSet<int> TimeSeriesFailing { get; } = [];

    /// <summary>The whole Content-Type header of every ingestion request, parameters included.</summary>
    public List<string?> TimeSeriesContentTypes { get; } = [];

    public void Register(string workflow, Script? script = null) => Workflows[workflow] = script ?? new Script();

    /// <summary>Stores a record as storage would, with the next version.</summary>
    public long Put(JsonObject record)
    {
        var id = record["id"]!.GetValue<string>();
        lock (_gate)
        {
            var version = Records.TryGetValue(id, out var existing) ? existing["version"]!.GetValue<long>() + 1 : 1_700_000_000_000_000L + Records.Count;
            var stored = (JsonObject)record.DeepClone();
            stored["version"] = version;
            Records[id] = stored;
            if (!History.TryGetValue(id, out var versions))
            {
                versions = [];
                History[id] = versions;
            }

            versions.Add((JsonObject)stored.DeepClone());
            Removed.Remove(id);
            return version;
        }
    }

    public static JsonObject Record(string id, string kind, JsonObject? data = null) => new()
    {
        ["id"] = id,
        ["kind"] = kind,
        ["acl"] = new JsonObject { ["viewers"] = new JsonArray("data.default.viewers@dev.example.com"), ["owners"] = new JsonArray("data.default.owners@dev.example.com") },
        ["legal"] = new JsonObject { ["legaltags"] = new JsonArray("dev-public"), ["otherRelevantDataCountries"] = new JsonArray("US") },
        ["data"] = data ?? new JsonObject { ["Name"] = id },
    };

    /// <summary>The requests that go to a location a service signed, which no OSDU contract describes.</summary>
    public static bool ToSignedLocation(FakeHttpHandler.Request request)
        => request.Uri.AbsolutePath.StartsWith("/staging/", StringComparison.Ordinal)
           || request.Uri.AbsolutePath.StartsWith("/download/", StringComparison.Ordinal)
           || request.Uri.AbsolutePath.StartsWith("/upload/storage/", StringComparison.Ordinal);

    public IEnumerable<FakeHttpHandler.Request> Triggers(string workflow)
        => Calls.Where(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith($"/workflow/{workflow}/workflowRun", StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? body = null;
        byte[] bytes = [];
        if (request.Content is not null)
        {
            bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            body = request.Content.Headers.ContentType?.MediaType is { } media && (media.Contains("json", StringComparison.Ordinal) || media.StartsWith("text/", StringComparison.Ordinal) || media.StartsWith("multipart/", StringComparison.Ordinal))
                ? Encoding.UTF8.GetString(bytes)
                : (bytes.Length == 0 ? null : $"({bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes)");
        }

        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            Calls.Add(new FakeHttpHandler.Request(request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType, headers));
        }

        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;
        lock (_gate)
        {
            return Route(method, path, request.RequestUri, body, bytes, request);
        }
    }

    private HttpResponseMessage Route(string method, string path, Uri uri, string? body, byte[] bytes, HttpRequestMessage request)
    {
        const string workflowRoot = "/api/workflow/v1";
        if (path == workflowRoot + "/info" || path == "/api/dataset/v1/info" || path == "/api/storage/v2/info" || path == "/api/file/v2/info")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["version"] = "1.0.0" });
        }

        if (path.StartsWith(workflowRoot + "/workflow/", StringComparison.Ordinal))
        {
            return WorkflowService(method, path[(workflowRoot + "/workflow/").Length..].Split('/'), body);
        }

        if (path.StartsWith("/api/dataset/v1/", StringComparison.Ordinal))
        {
            return DatasetService(method, path["/api/dataset/v1/".Length..], uri, body);
        }

        if (path.StartsWith("/api/storage/v2/", StringComparison.Ordinal))
        {
            return StorageService(method, Uri.UnescapeDataString(path["/api/storage/v2/".Length..]), body, HttpUtility.ParseQueryString(uri.Query)["skipdupes"] == "true");
        }

        if (path.StartsWith("/api/file/v2/files/", StringComparison.Ordinal))
        {
            return FileService(method, path["/api/file/v2/files/".Length..], body);
        }

        if (path.StartsWith(DdmsRoot + "/", StringComparison.Ordinal))
        {
            return Ddms(method, path[DdmsRoot.Length..], bytes, body);
        }

        if (path.StartsWith(WellDeliveryRoot + "/", StringComparison.Ordinal))
        {
            return WellDelivery(method, path[WellDeliveryRoot.Length..], body, request);
        }

        if (path.StartsWith(RafsRoot + "/", StringComparison.Ordinal))
        {
            return Rafs(method, path[RafsRoot.Length..], uri, bytes, body, request);
        }

        if (path.StartsWith(TimeSeriesRoot + "/", StringComparison.Ordinal))
        {
            return TimeSeriesIngestion(method, path[TimeSeriesRoot.Length..], body, request);
        }

        if (path.StartsWith(TimeSeriesQueryRoot + "/", StringComparison.Ordinal))
        {
            return TimeSeriesQuery(method, path[TimeSeriesQueryRoot.Length..], uri);
        }

        if (SeismicRoute(method, path, uri, body, bytes, request) is { } seismic)
        {
            return seismic;
        }

        if (ReservoirManagementRoute(method, path, uri, body) is { } reservoirManagement)
        {
            return reservoirManagement;
        }

        if (DspdmRoute(method, path, body, request) is { } dspdm)
        {
            return dspdm;
        }

        if (SchemaServiceRoute(method, path, uri) is { } schema)
        {
            return schema;
        }

        if (path == "/api/search/v2/query_with_cursor" || path == "/api/search/v2/query")
        {
            var query = JsonNode.Parse(body!)!;
            var ids = Search?.Invoke(query["kind"]!.GetValue<string>(), query["query"]?.GetValue<string>()) ?? [];
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["results"] = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray()),
                ["totalCount"] = ids.Count,
            });
        }

        if (path == "/api/legal/v1/legaltags:validate")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["invalidLegalTags"] = new JsonArray() });
        }

        if (path.StartsWith("/staging/", StringComparison.Ordinal))
        {
            return StagingArea(method, path, uri, bytes, request);
        }

        if (path.StartsWith("/upload/storage/v1/b/", StringComparison.Ordinal))
        {
            var name = HttpUtility.ParseQueryString(uri.Query)["name"]!;
            Staged["/staging/gcs/" + name] = bytes;
            return Json(HttpStatusCode.OK, new JsonObject { ["name"] = name });
        }

        if (path.StartsWith("/download/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/download/".Length..]);
            return Content.TryGetValue(id, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (path.StartsWith("/airflow/", StringComparison.Ordinal))
        {
            return Airflow(method, path["/airflow".Length..], request);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no route for " + method + " " + path) };
    }

    private HttpResponseMessage WorkflowService(string method, string[] parts, string? body)
    {
        var workflow = Uri.UnescapeDataString(parts[0]);
        if (!Workflows.TryGetValue(workflow, out var script))
        {
            return Error(HttpStatusCode.NotFound, $"Workflow: {workflow} doesn't exist");
        }

        if (parts.Length == 1 && method == "GET")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["workflowId"] = "wf-" + workflow, ["workflowName"] = workflow });
        }

        if (parts.Length == 2 && parts[1] == "workflowRun" && method == "POST")
        {
            var trigger = JsonNode.Parse(body!)!.AsObject();
            var runId = trigger["runId"]?.GetValue<string>() ?? Guid.NewGuid().ToString("D");
            if (Runs.Any(r => r.RunId == runId))
            {
                return Error(HttpStatusCode.Conflict, "A Workflow with the given name already exists.");
            }

            var run = new Run(workflow, runId, (JsonObject)(trigger["executionContext"]?.DeepClone() ?? new JsonObject()));
            Runs.Add(run);
            script.Effect?.Invoke(this, run);
            run.XCom = script.XCom?.Invoke(run) ?? new Dictionary<(string, string), JsonNode?>();
            return Json(HttpStatusCode.OK, new JsonObject { ["workflowId"] = "wf-" + workflow, ["runId"] = runId, ["status"] = "submitted" });
        }

        var found = parts.Length >= 3 ? Runs.FirstOrDefault(r => r.Workflow == workflow && r.RunId == Uri.UnescapeDataString(parts[2])) : null;
        if (found is null)
        {
            return Error(HttpStatusCode.NotFound, "no such run");
        }

        if (parts.Length == 4 && parts[3] == "latestInfo")
        {
            var xcom = new JsonObject();
            foreach (var ((task, key), value) in found.XCom.Where(x => x.Key.Task == script.LatestTask))
            {
                // The Workflow service gives each entry as the text Airflow 2 renders.
                xcom[key] = value is JsonValue text && text.TryGetValue<string>(out var s) ? s : PythonRepr(value);
            }

            return Json(HttpStatusCode.OK, new JsonObject { ["dag_id"] = workflow, ["task_id"] = script.LatestTask, ["state"] = "success", ["xcom"] = xcom });
        }

        found.Polls++;
        var status = found.Polls == 1 && script.Pending.Length > 0 ? script.Pending : script.Terminal;
        return Json(HttpStatusCode.OK, new JsonObject { ["workflowId"] = "wf-" + workflow, ["runId"] = found.RunId, ["status"] = status });
    }

    private HttpResponseMessage DatasetService(string method, string operation, Uri uri, string? body)
    {
        if (operation == "storageInstructions" && method == "POST")
        {
            var type = HttpUtility.ParseQueryString(uri.Query)["kindSubType"]!;
            var n = (++_locations).ToString(CultureInfo.InvariantCulture);
            var collection = type.StartsWith("dataset--FileCollection.", StringComparison.Ordinal);
            var location = (Provider, collection) switch
            {
                (Staging.Azure, false) => new JsonObject { ["signedUrl"] = $"{Endpoint}/staging/blob/f-{n}?sv=2021-06-08&sr=b&sp=cw&sig=secret", ["fileSource"] = $"/staging/blob/f-{n}", ["createdBy"] = "delivery" },
                (Staging.Azure, true) => new JsonObject { ["signedUrl"] = $"{Endpoint}/staging/dfs/d-{n}?sv=2021-06-08&sr=d&sp=racwl&sdd=1&sig=secret", ["fileCollectionSource"] = $"/d-{n}", ["createdBy"] = "delivery" },
                (Staging.Minio or Staging.S3 or Staging.Google, false) => new JsonObject { ["signedUrl"] = $"{Endpoint}/staging/obm/u-{n}/f-{n}?X-Amz-Signature=secret", ["fileSource"] = $"/u-{n}/f-{n}", ["createdBy"] = "delivery" },
                (Staging.Minio, true) => new JsonObject
                {
                    ["url"] = $"{Endpoint}/staging/minio/",
                    ["fileCollectionSource"] = $"c{n}",
                    ["createdBy"] = "delivery",
                    ["signingOptions"] = new JsonObject { ["x-amz-algorithm"] = "AWS4-HMAC-SHA256", ["x-amz-credential"] = "cred", ["x-amz-date"] = "20260917T000000Z", ["policy"] = "cG9saWN5", ["x-amz-signature"] = "secret" },
                },
                (Staging.S3, true) => new JsonObject
                {
                    ["url"] = $"{Endpoint}/staging/s3/",
                    ["fileCollectionSource"] = $"c{n}",
                    ["createdBy"] = "delivery",
                    ["signingOptions"] = new JsonObject { ["policy"] = "cG9saWN5", ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256", ["X-Amz-Credential"] = "cred", ["X-Amz-Date"] = "20260917T000000Z", ["X-Amz-Signature"] = "secret", ["key"] = $"c{n}/" },
                },
                (Staging.Google, true) => new JsonObject
                {
                    ["url"] = "https://storage.googleapis.com/staging-bucket/",
                    ["fileCollectionSource"] = $"c{n}",
                    ["createdBy"] = "delivery",
                    ["signingOptions"] = new JsonObject { ["bucket"] = "staging-bucket", ["filepath"] = $"c{n}/", ["connectionString"] = "folder-token" },
                },
                // IBM: temporary credentials, twice, and the object's unsigned address, with no endpoint for either.
                _ => new JsonObject
                {
                    ["connectionString"] = "AccessKeyId=a;SecretAccessKey=b;SessionToken=c;Expiration=2026-09-17T01:00:00Z",
                    ["credentials"] = new JsonObject { ["accessKeyId"] = "a", ["secretAccessKey"] = "b", ["sessionToken"] = "c", ["expiration"] = "2026-09-17T01:00:00Z" },
                    ["unsignedUrl"] = $"s3://bucket/c{n}",
                },
            };
            var provider = Provider switch { Staging.Azure => "AZURE", Staging.Minio => "ANTHOS", Staging.S3 => "S3", Staging.Google => "GCP", _ => "IBM" };
            return Json(HttpStatusCode.OK, new JsonObject { ["storageLocation"] = location, ["providerKey"] = provider });
        }

        if (operation == "registerDataset" && method == "PUT")
        {
            var registered = new JsonArray();
            foreach (var node in JsonNode.Parse(body!)!["datasetRegistries"]!.AsArray())
            {
                var record = node!.AsObject();
                var id = record["id"]!.GetValue<string>();
                var kind = record["kind"]!.GetValue<string>();
                if (!string.Equals(id.Split(':')[1], kind.Split(':')[2], StringComparison.OrdinalIgnoreCase))
                {
                    return Error(HttpStatusCode.BadRequest, "Invalid record id");
                }

                Put(record);
                registered.Add(Records[id].DeepClone());
            }

            return Json(HttpStatusCode.Created, new JsonObject { ["datasetRegistries"] = registered });
        }

        if (operation == "retrievalInstructions" && method == "POST")
        {
            var datasets = new JsonArray();
            foreach (var id in JsonNode.Parse(body!)!["datasetRegistryIds"]!.AsArray().Select(i => i!.GetValue<string>()))
            {
                if (Records.ContainsKey(id) && !Removed.Contains(id) && !Unretrievable.Contains(id))
                {
                    datasets.Add(new JsonObject
                    {
                        ["datasetRegistryId"] = id,
                        ["providerKey"] = "AZURE",
                        ["retrievalProperties"] = new JsonObject { ["signedUrl"] = $"{Endpoint}/download/{Uri.EscapeDataString(id)}?sig=secret", ["createdBy"] = "delivery" },
                    });
                }
            }

            return Json(HttpStatusCode.OK, new JsonObject { ["datasets"] = datasets });
        }

        if (operation.StartsWith("metadataRecord/", StringComparison.Ordinal) && operation.EndsWith("/softDelete", StringComparison.Ordinal) && method == "POST")
        {
            var id = Uri.UnescapeDataString(operation["metadataRecord/".Length..^"/softDelete".Length]);
            if (!Records.ContainsKey(id) || Removed.Contains(id))
            {
                return Error(HttpStatusCode.NotFound, "Record not found");
            }

            Removed.Add(id);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return Error(HttpStatusCode.NotFound, "no dataset route " + operation);
    }

    private HttpResponseMessage StorageService(string method, string operation, string? body, bool skipDuplicates = false)
    {
        if (operation == "records" && method == "PUT")
        {
            var ids = new JsonArray();
            var skipped = new JsonArray();
            foreach (var node in JsonNode.Parse(body!)!.AsArray())
            {
                // With skipdupes, an update that changes nothing storage compares (data, meta, acl, legal, tags, ancestry, not
                // the kind) writes no version and names the id under skippedRecordIds, a record held soft-deleted included,
                // which stays deleted (osdu/specs/core/INTEGRATION.md section 2.2).
                var record = node!.AsObject();
                var id = record["id"]!.GetValue<string>();
                if (skipDuplicates && Records.TryGetValue(id, out var existing) && SameForSkip(existing, record))
                {
                    skipped.Add(id);
                    continue;
                }

                var version = Put(record);
                ids.Add(id + ":" + version.ToString(CultureInfo.InvariantCulture));
            }

            // Storage creates each list on the first record added to it, so a list with nothing in it is left out.
            var answer = new JsonObject { ["recordCount"] = ids.Count + skipped.Count };
            if (ids.Count > 0)
            {
                answer["recordIdVersions"] = ids;
            }

            if (skipped.Count > 0)
            {
                answer["skippedRecordIds"] = skipped;
            }

            return Json(HttpStatusCode.Created, answer);
        }

        // A merge patch bringing a soft-deleted record back (openapi storage v2, PATCH /records/{id}): 404 for an id storage
        // holds no record of, 400 for a record it already serves (storage RecordServiceImpl.patchRecord).
        if (operation.StartsWith("records/", StringComparison.Ordinal) && method == "PATCH")
        {
            var id = operation["records/".Length..];
            if (!Records.ContainsKey(id))
            {
                return Error(HttpStatusCode.NotFound, "Record not found");
            }

            if (!Removed.Remove(id))
            {
                return Error(HttpStatusCode.BadRequest, "Record State already updated");
            }

            Restores.Add(id);
            return Json(HttpStatusCode.OK, new JsonObject { ["recordCount"] = 1, ["recordIds"] = new JsonArray(id) });
        }

        if (operation == "query/records" && method == "POST")
        {
            var found = new JsonArray();
            var invalid = new JsonArray();
            foreach (var id in JsonNode.Parse(body!)!["records"]!.AsArray().Select(i => i!.GetValue<string>()))
            {
                if (Records.TryGetValue(id, out var record) && !Removed.Contains(id))
                {
                    found.Add(record.DeepClone());
                }
                else
                {
                    invalid.Add(id);
                }
            }

            return Json(HttpStatusCode.OK, new JsonObject { ["records"] = found, ["invalidRecords"] = invalid, ["retryRecords"] = new JsonArray() });
        }

        if (operation == "records/delete" && method == "POST")
        {
            foreach (var id in JsonNode.Parse(body!)!.AsArray().Select(i => i!.GetValue<string>()))
            {
                Removed.Add(id);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        // The version list beside the record (openapi storage v2, GET /records/versions/{id}): every version the record's
        // writes left that no purge took, oldest first, as the service lists them.
        if (operation.StartsWith("records/versions/", StringComparison.Ordinal) && method == "GET")
        {
            var id = operation["records/versions/".Length..];
            return History.TryGetValue(id, out var kept) && Records.ContainsKey(id) && !Removed.Contains(id)
                ? Json(HttpStatusCode.OK, new JsonObject
                {
                    ["recordId"] = id,
                    ["versions"] = new JsonArray(kept.Select(v => (JsonNode?)JsonValue.Create(v["version"]!.GetValue<long>())).ToArray()),
                })
                : Error(HttpStatusCode.NotFound, "Record not found");
        }

        if (operation.StartsWith("records/", StringComparison.Ordinal))
        {
            var rest = operation["records/".Length..];
            // A record at one version (GET /records/{id}/{version}): any version its history still keeps.
            var slash = rest.LastIndexOf('/');
            if (method == "GET" && slash > 0 && long.TryParse(rest[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var atVersion))
            {
                var id = rest[..slash];
                var held = History.TryGetValue(id, out var kept) && !Removed.Contains(id)
                    ? kept.FirstOrDefault(v => v["version"]!.GetValue<long>() == atVersion)
                    : null;
                return held is not null
                    ? Json(HttpStatusCode.OK, held.DeepClone())
                    : Error(HttpStatusCode.NotFound, "Record version not found");
            }

            if (rest.EndsWith(":delete", StringComparison.Ordinal) && method == "POST")
            {
                var id = rest[..^":delete".Length];
                if (!Records.ContainsKey(id) || Removed.Contains(id))
                {
                    return Error(HttpStatusCode.NotFound, "Record not found");
                }

                Removed.Add(id);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (rest.EndsWith("/versions", StringComparison.Ordinal) && method == "DELETE")
            {
                // Every earlier version goes; the latest stays.
                var id = rest[..^"/versions".Length];
                if (History.TryGetValue(id, out var kept) && kept.Count > 1)
                {
                    kept.RemoveRange(0, kept.Count - 1);
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == "DELETE")
            {
                if (!Records.Remove(rest))
                {
                    return Error(HttpStatusCode.NotFound, "Record not found");
                }

                History.Remove(rest);
                Purged.Add(rest);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == "GET")
            {
                return Records.TryGetValue(rest, out var record) && !Removed.Contains(rest)
                    ? Json(HttpStatusCode.OK, record.DeepClone())
                    : Error(HttpStatusCode.NotFound, "Record not found");
            }
        }

        return Error(HttpStatusCode.NotFound, "no storage route " + operation);
    }

    private HttpResponseMessage StagingArea(string method, string path, Uri uri, byte[] bytes, HttpRequestMessage request)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);
        if (path.StartsWith("/staging/dfs/", StringComparison.Ordinal))
        {
            // The Data Lake create, append and flush of a file under a directory SAS.
            if (request.Headers.TryGetValues("x-ms-version", out var versions) is false || versions.Single() != query["sv"])
            {
                return Error(HttpStatusCode.BadRequest, "x-ms-version must state the SAS version");
            }

            switch (method, query["resource"], query["action"])
            {
                case ("PUT", "file", null):
                    Staged[path] = [];
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case ("PATCH", null, "append"):
                    var position = long.Parse(query["position"]!, CultureInfo.InvariantCulture);
                    if (!Staged.TryGetValue(path, out var sofar) || sofar.LongLength != position)
                    {
                        return Error(HttpStatusCode.BadRequest, "InvalidFlushPosition");
                    }

                    Staged[path] = [.. sofar, .. bytes];
                    return new HttpResponseMessage(HttpStatusCode.Accepted);
                case ("PATCH", null, "flush"):
                    return long.Parse(query["position"]!, CultureInfo.InvariantCulture) == Staged[path].LongLength
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                        : Error(HttpStatusCode.BadRequest, "InvalidFlushPosition");
                default:
                    return Error(HttpStatusCode.BadRequest, "not a Data Lake path operation");
            }
        }

        if ((path == "/staging/minio/" || path == "/staging/s3/") && method == "POST")
        {
            var form = Encoding.UTF8.GetString(bytes);
            var key = FormField(form, "key") ?? string.Empty;
            if (FormField(form, "policy") is null || FormField(form, "file") is null)
            {
                return Error(HttpStatusCode.BadRequest, "a POST policy upload names its policy and its file");
            }

            Staged[path + key] = bytes;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (method == "PUT")
        {
            if (path.StartsWith("/staging/blob/", StringComparison.Ordinal) && !request.Headers.Contains("x-ms-blob-type"))
            {
                return Error(HttpStatusCode.BadRequest, "MissingRequiredHeader");
            }

            Staged[path] = bytes;
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        return Error(HttpStatusCode.BadRequest, "not a staging operation");
    }

    /// <summary>
    /// The File service calls of the file routes: a landing-zone location, a dataset record registered with an id the
    /// service mints, and its removal with its file.
    /// </summary>
    private HttpResponseMessage FileService(string method, string operation, string? body)
    {
        if (operation == "uploadURL" && method == "GET")
        {
            var n = (++_locations).ToString(CultureInfo.InvariantCulture);
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["FileID"] = "file-" + n,
                ["Location"] = new JsonObject { ["SignedURL"] = $"{Endpoint}/staging/landing/l-{n}?sig=secret", ["FileSource"] = $"/staging/landing/l-{n}" },
            });
        }

        if (operation == "metadata" && method == "POST")
        {
            var record = JsonNode.Parse(body!)!.AsObject();
            var id = $"dev:dataset--File.Generic:minted-{(++_locations).ToString(CultureInfo.InvariantCulture)}";
            record["id"] = id;
            Put(record);
            return Json(HttpStatusCode.Created, new JsonObject { ["id"] = id });
        }

        if (operation.EndsWith("/metadata", StringComparison.Ordinal) && method == "DELETE")
        {
            var id = Uri.UnescapeDataString(operation[..^"/metadata".Length]);
            if (!Records.ContainsKey(id) || Removed.Contains(id))
            {
                return Error(HttpStatusCode.NotFound, "Record Not Found");
            }

            Removed.Add(id);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return Error(HttpStatusCode.NotFound, "no file route " + operation);
    }

    /// <summary>
    /// The Wellbore DDMS v3 calls the routes make: records written and read through a collection, bulk data written in one
    /// request (which sets the record's bulk link and gives it a new version), its description read back, and deletes.
    /// </summary>
    private HttpResponseMessage Ddms(string method, string path, byte[] bytes, string? body)
    {
        if (path == "/about" && method == "GET")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["service"] = "Wellbore DDMS", ["version"] = "0.2" });
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "ddms" || parts[1] != "v3")
        {
            return Error(HttpStatusCode.NotFound, "no DDMS route " + path);
        }

        if (parts.Length == 3 && method == "POST")
        {
            var versions = new JsonArray();
            var ids = new JsonArray();
            foreach (var node in JsonNode.Parse(body!)!.AsArray())
            {
                var record = node!.AsObject();
                var id = record["id"]!.GetValue<string>();
                // The DDMS reads the latest version through storage, which answers 404 for a record it holds as deleted, and a
                // record it cannot read has no link to match (wellbore-domain-services ddms_v3_utils.py, _raise_if_invalid_bulk_uri_task;
                // storage QueryServiceImpl.getRecordInfo).
                var held = Records.TryGetValue(id, out var existing) && !Removed.Contains(id)
                    ? existing["data"]?["ExtensionProperties"]?["wdms"]?["bulkURI"]?.GetValue<string>()
                    : null;
                var sent = record["data"]?["ExtensionProperties"]?["wdms"]?["bulkURI"]?.GetValue<string>();
                if (held != sent)
                {
                    RefusedLinks++;
                    return Error(HttpStatusCode.BadRequest, "bulkURI differs from the latest version");
                }

                var version = Put(record);
                ids.Add(id);
                versions.Add(id + ":" + version.ToString(CultureInfo.InvariantCulture));
            }

            return Json(HttpStatusCode.OK, new JsonObject { ["recordCount"] = ids.Count, ["recordIds"] = ids, ["recordIdVersions"] = versions, ["skippedRecordIds"] = new JsonArray() });
        }

        var recordId = Uri.UnescapeDataString(parts[3]);
        if (parts.Length == 4)
        {
            switch (method)
            {
                case "GET":
                    return Records.TryGetValue(recordId, out var record) && !Removed.Contains(recordId)
                        ? Json(HttpStatusCode.OK, record.DeepClone())
                        : Error(HttpStatusCode.NotFound, "record not found");
                case "DELETE":
                    if (!Records.ContainsKey(recordId))
                    {
                        return Error(HttpStatusCode.NotFound, "record not found");
                    }

                    Removed.Add(recordId);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        if (parts.Length == 5 && parts[4] == "data")
        {
            if (method == "POST")
            {
                if (!Records.TryGetValue(recordId, out var record))
                {
                    return Error(HttpStatusCode.NotFound, "record not found");
                }

                Bulk[recordId] = bytes;
                var linked = (JsonObject)record.DeepClone();
                linked["data"]!["ExtensionProperties"] = new JsonObject { ["wdms"] = new JsonObject { ["bulkURI"] = "urn:wdms-1:uuid:" + Guid.NewGuid().ToString("D") } };
                Put(linked);
                return Json(HttpStatusCode.OK, new JsonObject());
            }

            if (method == "GET")
            {
                return Bulk.ContainsKey(recordId)
                    ? Json(HttpStatusCode.OK, new JsonObject { ["numberOfRows"] = 1, ["columns"] = new JsonArray("MD", "GR") })
                    : Error(HttpStatusCode.NotFound, "no bulk data");
            }
        }

        return Error(HttpStatusCode.NotFound, "no DDMS route " + path);
    }

    /// <summary>
    /// The Well Delivery DDMS as its brief reads the service (osdu/specs/well-delivery-ddms/INTEGRATION.md sections 2 to 5):
    /// one entity per PUT under the path of its type, the checks it runs before it writes, the version it keys the entity
    /// by, the references it indexes (only those ending in a version), its copy into storage, and the reads and deletes
    /// that name an entity by its entity id, the DELETEs among them only with a JSON content type.
    /// </summary>
    private HttpResponseMessage WellDelivery(string method, string path, string? body, HttpRequestMessage request)
    {
        if (path == "/info" && method == "GET")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["groupId"] = "org.opengroup.osdu", ["artifactId"] = "well-delivery", ["version"] = "0.29.0-SNAPSHOT" });
        }

        const string prefix = "/storage/v1/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return WellDeliveryError(HttpStatusCode.NotFound, "no Well Delivery route " + path);
        }

        var parts = path[prefix.Length..].Split('/').Select(Uri.UnescapeDataString).ToArray();
        var type = parts[0].ToLowerInvariant();
        var json = request.Content?.Headers.ContentType?.MediaType == "application/json";
        if (parts.Length == 1 && method == "PUT")
        {
            return json ? WellDeliveryWrite(parts[0], body, request) : new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType);
        }

        if (parts.Length < 2)
        {
            return WellDeliveryError(HttpStatusCode.NotFound, "no Well Delivery route " + path);
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(parts[0], "^[0-9a-zA-Z-]*$"))
        {
            return WellDeliveryError(HttpStatusCode.BadRequest, $"Invalid entity type: {parts[0]}");
        }

        var purge = parts.Length == 2 && parts[1].EndsWith(":purge", StringComparison.Ordinal);
        var entityId = purge ? parts[1][..^":purge".Length] : parts[1];
        var versions = WellDeliveryEntities.Where(e => e.Key.StartsWith($"{type}|{entityId}|", StringComparison.Ordinal)).ToList();
        if (method == "GET")
        {
            if (!WellDeliveryEntities.Keys.Any(k => k.StartsWith(type + "|", StringComparison.Ordinal)))
            {
                return WellDeliveryError(HttpStatusCode.BadRequest, $"Collection {parts[0]}Container is not existed.");
            }

            var live = versions.Where(v => !v.Value.Deleted).ToList();
            var found = parts.Length == 3 ? live.FirstOrDefault(v => v.Key.EndsWith("|" + parts[2], StringComparison.Ordinal)) : live.LastOrDefault();
            return found.Key is null
                ? WellDeliveryError(HttpStatusCode.NotFound, $"Could not find entity with id: {entityId}")
                : Json(HttpStatusCode.OK, found.Value.Entity.DeepClone());
        }

        if (method == "DELETE" && parts.Length == 2)
        {
            if (!json)
            {
                return new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType);
            }

            if (versions.Count == 0)
            {
                return WellDeliveryError(HttpStatusCode.NotFound, $"Could not find entity with id: {entityId}");
            }

            foreach (var (key, entry) in versions)
            {
                if (purge)
                {
                    WellDeliveryEntities.Remove(key);
                    WellDeliveryIndex.Remove(key);
                }
                else
                {
                    WellDeliveryEntities[key] = (entry.Entity, true);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return WellDeliveryError(HttpStatusCode.NotFound, "no Well Delivery route " + path);
    }

    private HttpResponseMessage WellDeliveryWrite(string pathType, string? body, HttpRequestMessage request)
    {
        if (JsonNode.Parse(body!) is not JsonObject entity || entity["id"] is not JsonValue idValue || idValue.GetValue<string>() is not { Length: > 0 } id)
        {
            return WellDeliveryError(HttpStatusCode.BadRequest, "Entity Id is empty.");
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[\w\-\.]+:[0-9a-zA-Z\-]+\-\-[\w\-]*:[\w\-\.\:\%]+$"))
        {
            return WellDeliveryError(HttpStatusCode.BadRequest, "Entity Id is invalid.");
        }

        var segments = id.Split(':');
        var idType = segments[1][(segments[1].IndexOf("--", StringComparison.Ordinal) + 2)..].ToLowerInvariant();
        var entityId = string.Join(':', segments.Skip(2));
        if (!string.Equals(idType, pathType, StringComparison.OrdinalIgnoreCase))
        {
            return WellDeliveryError(HttpStatusCode.BadRequest, $"Entity type in API({pathType}) and body({idType}) are not same.");
        }

        if (entity["kind"] is null)
        {
            return WellDeliveryError(HttpStatusCode.BadRequest, $"The Kind of Entity {id} is empty.");
        }

        long version;
        if (entity["version"] is JsonValue sent)
        {
            // Jackson's isLong: a value that fits in 32 bits is an int node, and refused.
            if (!sent.TryGetValue(out version) || version is >= int.MinValue and <= int.MaxValue)
            {
                return WellDeliveryError(HttpStatusCode.BadRequest, $"The version of Entity {id} is invalid.");
            }
        }
        else
        {
            version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        if (entity["legal"]?["legaltags"] is not JsonArray { Count: > 0 } || entity["legal"]?["otherRelevantDataCountries"] is not JsonArray { Count: > 0 })
        {
            return WellDeliveryError(HttpStatusCode.BadRequest, "Legal Tags are empty");
        }

        if (entity["acl"]?["owners"] is not JsonArray { Count: > 0 } owners || entity["acl"]?["viewers"] is not JsonArray { Count: > 0 } viewers)
        {
            return WellDeliveryError(HttpStatusCode.BadRequest, "ACL are empty");
        }

        if (owners.Concat(viewers).Any(g => !g!.GetValue<string>().Contains('@', StringComparison.Ordinal)))
        {
            return WellDeliveryError(HttpStatusCode.InternalServerError, "Unknown error happened when validating ACL");
        }

        if (entity["data"] is not JsonObject data)
        {
            return WellDeliveryError(HttpStatusCode.BadRequest, "Entity data is empty");
        }

        if (data["ExistenceKind"] is not JsonValue existence)
        {
            return WellDeliveryError(HttpStatusCode.BadRequest, "ExistenceKind is empty.");
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(existence.GetValue<string>(), @"^[\w\-\.]+:[0-9a-zA-Z\-]+\-\-[\w\-]*:[\w\-\.\:\%]+:[0-9]*$"))
        {
            return WellDeliveryError(HttpStatusCode.BadRequest, "ExistenceKind is not reference data format.");
        }

        var key = $"{idType}|{entityId}|{version.ToString(CultureInfo.InvariantCulture)}";
        if (WellDeliveryCloudant && WellDeliveryEntities.ContainsKey(key))
        {
            return WellDeliveryError(HttpStatusCode.InternalServerError, "An unknown error has occurred.");
        }

        var stored = new JsonObject
        {
            ["id"] = id,
            ["kind"] = entity["kind"]!.DeepClone(),
            ["version"] = version,
            ["acl"] = entity["acl"]!.DeepClone(),
            ["legal"] = entity["legal"]!.DeepClone(),
            ["valid"] = data["SchemaInvalid"] is null,
            ["data"] = data.DeepClone(),
        };
        WellDeliveryEntities[key] = (stored, false);
        WellDeliveryIndex[key] = WellDeliveryReferences(data, idType);

        if (WellDeliveryMirror && version > 20000)
        {
            var partition = request.Headers.TryGetValues("data-partition-id", out var values) ? values.Single() : segments[0];
            var copy = (JsonObject)entity.DeepClone();
            copy["id"] = id.Replace(segments[0], partition, StringComparison.Ordinal);
            copy.Remove("version");
            copy["data"]!["origId"] = id;
            copy["data"]!["entityType"] = idType;
            copy["data"]!["entityId"] = entityId;
            copy["data"]!["ddmsid"] = "well-delivery-ddms-1";
            Put(copy);
        }

        if (WellDeliveryFailsAfterWrite)
        {
            WellDeliveryFailsAfterWrite = false;
            return WellDeliveryError(HttpStatusCode.InternalServerError, "An unknown error has occurred.");
        }

        var answer = (JsonObject)stored.DeepClone();
        if (data["SchemaInvalid"] is not null)
        {
            answer["errors"] = new JsonArray("$.data.SchemaInvalid: is not defined in the schema and the schema does not allow additional properties");
        }

        return Json(HttpStatusCode.Created, answer);
    }

    /// <summary>The references the Well Delivery DDMS indexes: strings of data ending in a version, not of the entity's own type.</summary>
    private static List<string> WellDeliveryReferences(JsonObject data, string ownType)
    {
        var found = new List<string>();
        void Walk(JsonNode? node, bool inArray)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (_, value) in obj)
                    {
                        Walk(value, false);
                    }

                    break;
                case JsonArray array when !inArray:
                    foreach (var item in array)
                    {
                        Walk(item, true);
                    }

                    break;
                case JsonValue value when value.TryGetValue(out string? text)
                    && System.Text.RegularExpressions.Regex.Match(text, @"^[\w\-\.]+:[0-9a-zA-Z\-]+\-\-(?<type>[\w\-]*):[\w\-\.\:\%]+:[0-9]+$") is { Success: true } match
                    && !string.Equals(match.Groups["type"].Value, ownType, StringComparison.OrdinalIgnoreCase):
                    found.Add(text);
                    break;
            }
        }

        Walk(data, false);
        return found;
    }

    private static HttpResponseMessage WellDeliveryError(HttpStatusCode status, string message)
        => Json(status, new JsonObject { ["message"] = message });

    /// <summary>The entity types each RAFS v2 collection accepts (osdu/specs/rafs-ddms/INTEGRATION.md section 2.1).</summary>
    private static readonly Dictionary<string, string[]> RafsKinds = new(StringComparer.Ordinal)
    {
        ["masterdata"] = ["master-data--GenericFacility", "master-data--GenericSite", "master-data--Sample", "master-data--SampleAcquisitionJob", "master-data--SampleChainOfCustodyEvent", "master-data--SampleContainer"],
        ["samplesanalysesreport"] = ["work-product-component--SamplesAnalysesReport"],
        ["samplesanalysis"] = ["work-product-component--SamplesAnalysis"],
        ["saturationfunctionset"] = ["work-product-component--SaturationFunctionSet"],
        ["reservoirsimulationrockphysicsmodel"] = ["work-product-component--ReservoirSimulationRockPhysicsModel"],
        ["fluidmodel"] = ["work-product-component--FluidModel"],
        ["depthshift"] = ["work-product-component--DepthShift"],
    };

    /// <summary>
    /// RAFS v2 as its brief reads the service (osdu/specs/rafs-ddms/INTEGRATION.md sections 2 to 5): records posted in an
    /// array typed exactly <c>application/json</c> and written through storage, content posted per type with its schema
    /// version and registered as a dataset (or kept as a blob), the record re-versioned with the content's URN, the type
    /// catalogues and content schemas, reads, and the logical delete.
    /// </summary>
    private HttpResponseMessage Rafs(string method, string path, Uri uri, byte[] bytes, string? body, HttpRequestMessage request)
    {
        if (path == "/info" && method == "GET")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["name"] = "rafs-ddms-services", ["app_version"] = "0.2.0", ["release_version"] = "M26" });
        }

        var parts = path.StartsWith("/v2/", StringComparison.Ordinal) ? path["/v2/".Length..].Split('/').Select(Uri.UnescapeDataString).ToArray() : [];
        if (parts.Length == 0 || !RafsKinds.TryGetValue(parts[0], out var accepted))
        {
            return RafsError(HttpStatusCode.NotFound, "Not Found");
        }

        var collection = parts[0];
        var query = HttpUtility.ParseQueryString(uri.Query);
        var typed = collection is "samplesanalysis" or "fluidmodel";
        switch (parts.Length, method)
        {
            case (2, "GET") when collection == "samplesanalysis" && parts[1] == "analysistypes":
                return Json(HttpStatusCode.OK, new JsonObject(RafsAnalysisTypes.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)new JsonArray(t.Value.Select(v => (JsonNode?)v).ToArray())))));
            case (2, "GET") when collection == "fluidmodel" && parts[1] == "fluidmodeltypes":
                return Json(HttpStatusCode.OK, new JsonObject { ["blackoilfluidmodel"] = new JsonArray("1.0.0"), ["compositionalfluidmodel"] = new JsonArray("1.0.0") });
            case (3, "GET") when !typed && parts[1] == "data" && parts[2] == "schema":
                return query["content_schema_version"] == "1.0.0"
                    ? Json(HttpStatusCode.OK, new JsonObject { ["title"] = collection, ["type"] = "object" })
                    : RafsError(HttpStatusCode.NotFound, $"Model not found for type '{collection}', version '{query["content_schema_version"]}'. Available versions: ['1.0.0']");
            case (1, "POST"):
                return RafsRecords(collection, accepted, body, request);
        }

        var recordId = parts[1];
        if (parts.Length == 2 && method == "GET")
        {
            return Records.TryGetValue(recordId, out var record) && !Removed.Contains(recordId)
                ? Json(HttpStatusCode.OK, record.DeepClone())
                : Json(HttpStatusCode.NotFound, new JsonObject { ["code"] = 404, ["reason"] = "Record not found", ["message"] = recordId });
        }

        if (parts.Length == 2 && method == "DELETE")
        {
            if (!Records.ContainsKey(recordId) || Removed.Contains(recordId))
            {
                return Json(HttpStatusCode.NotFound, new JsonObject { ["code"] = 404, ["reason"] = "Record not found", ["message"] = recordId });
            }

            Removed.Add(recordId);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (parts.Length == (typed ? 4 : 3) && parts[2] == "data" && method == "POST")
        {
            return RafsContentWrite(collection, recordId, typed ? parts[3] : collection, query["content_schema_version"], bytes, request);
        }

        return RafsError(HttpStatusCode.NotFound, "Not Found");
    }

    private HttpResponseMessage RafsRecords(string collection, string[] accepted, string? body, HttpRequestMessage request)
    {
        var contentType = request.Content?.Headers.ContentType;
        if (contentType is null)
        {
            return RafsError(HttpStatusCode.BadRequest, "Content-Type header is required, but was not provided");
        }

        // The route compares the raw header with the allowed values, so a charset parameter is refused.
        if (contentType.ToString() != "application/json")
        {
            return RafsError(HttpStatusCode.UnsupportedMediaType, "The provided content-type is not supported.");
        }

        if (JsonNode.Parse(body!) is not JsonArray records)
        {
            return RafsError(HttpStatusCode.UnprocessableEntity, "body value is not a valid list");
        }

        var versions = new JsonArray();
        var warned = new JsonArray();
        foreach (var record in records.Select(r => r!.AsObject()))
        {
            var kind = record["kind"]?.GetValue<string>() ?? string.Empty;
            var kindParts = kind.Split(':');
            if (kindParts.Length != 4 || kindParts[1] != "wks" || !accepted.Contains(kindParts[2], StringComparer.Ordinal))
            {
                return RafsError(HttpStatusCode.UnprocessableEntity, $"Kind `{kind}` not supported in RAFS-DDMS. Supported kinds for this endpoint: [{string.Join(", ", accepted)}]");
            }

            if (collection == "samplesanalysis" && record["data"]?["SampleAnalysisTypeIDs"] is not JsonArray { Count: > 0 })
            {
                return RafsError(HttpStatusCode.UnprocessableEntity, "Missing SampleAnalysisTypeIDs in index 0");
            }
        }

        foreach (var record in records.Select(r => r!.AsObject()))
        {
            var id = record["id"]!.GetValue<string>();
            var version = Put(record);
            versions.Add(id + ":" + version.ToString(CultureInfo.InvariantCulture));
            if (collection == "fluidmodel" && record["data"]?["FluidModelTypeID"] is null)
            {
                warned.Add(id + ":" + version.ToString(CultureInfo.InvariantCulture));
            }
        }

        var answer = new JsonObject { ["recordCount"] = records.Count, ["recordIdVersions"] = versions, ["skippedRecordCount"] = 0 };
        if (warned.Count > 0)
        {
            answer["warning"] = "Records missing FluidModelTypeID will not be included in outputs produced by search endpoints";
            answer["warningRecordIds"] = warned;
        }

        return Json(HttpStatusCode.OK, answer);
    }

    private HttpResponseMessage RafsContentWrite(string collection, string recordId, string contentType, string? schemaVersion, byte[] bytes, HttpRequestMessage request)
    {
        var media = request.Content?.Headers.ContentType?.MediaType;
        if (media is not ("application/json" or "application/x-parquet"))
        {
            return RafsError(HttpStatusCode.UnsupportedMediaType, "The provided content-type is not supported.");
        }

        if (schemaVersion is null)
        {
            return RafsError(HttpStatusCode.NotAcceptable, "No schema version provided.");
        }

        var known = collection switch
        {
            "samplesanalysis" => RafsAnalysisTypes.TryGetValue(contentType, out var versions) && versions.Contains(schemaVersion),
            "fluidmodel" => contentType is "blackoilfluidmodel" or "compositionalfluidmodel" && schemaVersion == "1.0.0",
            _ => schemaVersion == "1.0.0",
        };
        if (!known)
        {
            return RafsError(HttpStatusCode.NotFound, $"Model not found for type '{contentType}', version '{schemaVersion}'.");
        }

        if (!Records.TryGetValue(recordId, out var parent) || Removed.Contains(recordId))
        {
            return Json(HttpStatusCode.NotFound, new JsonObject { ["code"] = 404, ["reason"] = "Record not found", ["message"] = recordId });
        }

        if (collection == "depthshift" && media == "application/x-parquet")
        {
            using var stream = new MemoryStream(bytes);
            if (ParquetFiles.ReadShapeAsync(stream).GetAwaiter().GetResult().Rows != 1)
            {
                return RafsError(HttpStatusCode.UnprocessableEntity, "DepthShift content must hold exactly one row.");
            }
        }

        var updated = (JsonObject)parent.DeepClone();
        updated.Remove("version");
        var data = updated["data"]!.AsObject();
        var entries = data["DDMSDatasets"] as JsonArray ?? [];
        data["DDMSDatasets"] = entries;
        string urn;
        var answer = new JsonObject();
        if (RafsBlobMode)
        {
            urn = $"urn://rafs/{recordId}/{collection}/{contentType}/{schemaVersion}/{Guid.NewGuid():N}";
            var sameSlot = $"/{collection}/{contentType}/{schemaVersion}/";
            RemoveEntries(entries, e => e.StartsWith("urn://rafs/", StringComparison.Ordinal) && e.Contains(sameSlot, StringComparison.Ordinal));
        }
        else
        {
            var existing = entries.Select(e => e!.GetValue<string>())
                .Where(e => e.StartsWith("urn://rafs-v2/", StringComparison.Ordinal))
                .Select(e => e.Split('/')[^2])
                .Select(contentId => contentId[..contentId.LastIndexOf(':')])
                .FirstOrDefault(dataset => dataset.Split(':')[2].StartsWith(contentType + "-", StringComparison.Ordinal));
            var dataset = existing ?? $"dev:dataset--File.Generic:{contentType}-{Guid.NewGuid():D}";
            var registered = Record(dataset, "osdu:wks:dataset--File.Generic:1.0.0", new JsonObject
            {
                ["DatasetProperties"] = new JsonObject { ["FileSourceInfo"] = new JsonObject { ["FileSource"] = $"/rafs/{dataset}", ["FileSize"] = bytes.Length.ToString(CultureInfo.InvariantCulture) } },
            });
            registered["acl"] = parent["acl"]!.DeepClone();
            registered["legal"] = parent["legal"]!.DeepClone();
            var datasetVersion = Put(registered);
            urn = $"urn://rafs-v2/{contentType}data/{recordId}/{dataset}:{datasetVersion.ToString(CultureInfo.InvariantCulture)}/{schemaVersion}";
            RemoveEntries(entries, e => e.StartsWith("urn://rafs-v2/", StringComparison.Ordinal) && e.Contains("/" + dataset + ":", StringComparison.Ordinal));
        }

        entries.Add(urn);
        var parentVersion = Put(updated);
        RafsContent[recordId + "|" + contentType] = (bytes, media, schemaVersion);
        answer["ddms_urn"] = urn;
        if (RafsBlobMode)
        {
            answer["updated_wpc_id"] = new JsonArray(recordId + ":" + parentVersion.ToString(CultureInfo.InvariantCulture));
        }

        return Json(HttpStatusCode.OK, answer);
    }

    private static void RemoveEntries(JsonArray entries, Func<string, bool> matches)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (matches(entries[i]!.GetValue<string>()))
            {
                entries.RemoveAt(i);
            }
        }
    }

    private static HttpResponseMessage RafsError(HttpStatusCode status, string reason)
        => Json(status, new JsonObject { ["code"] = (int)status, ["reason"] = reason });

    /// <summary>
    /// The historian's ingestion service as its brief reads the code (osdu/specs/production-timeseries/INTEGRATION.md
    /// sections 1.3 and 3): the record is read from storage; each series is checked against the record, the deployment's
    /// partition and its kind, and accepted under a version of its own; the answer is 207 with one item per series in order.
    /// </summary>
    private HttpResponseMessage TimeSeriesIngestion(string method, string path, string? body, HttpRequestMessage request)
    {
        if (path == "/info" && method == "GET")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["groupId"] = "org.opengroup.osdu.production", ["artifactId"] = "pddms-timeseries-ingestion", ["version"] = "0.1.0" });
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (method != "POST" || parts is not ["production-values", var recordId, "timeseries"])
        {
            return TimeSeriesError(HttpStatusCode.NotFound, "The requested resource could not be found.");
        }

        TimeSeriesContentTypes.Add(request.Content?.Headers.ContentType?.ToString());
        if (request.Content?.Headers.ContentType?.MediaType != "application/json")
        {
            return TimeSeriesError(HttpStatusCode.UnsupportedMediaType, "The request's Content-Type is not supported. Expected: application/json");
        }

        if (TimeSeriesFailing.Contains(++_ingestions))
        {
            return TimeSeriesError(HttpStatusCode.InternalServerError, "Storage service error");
        }

        if (!Records.TryGetValue(recordId, out var record) || Removed.Contains(recordId))
        {
            return TimeSeriesError(HttpStatusCode.NotFound, $"Record not found {recordId}");
        }

        var kinds = SeriesKinds(record);
        var partition = request.Headers.TryGetValues("data-partition-id", out var partitions) ? partitions.Single() : null;
        var items = new JsonArray();
        foreach (var node in JsonNode.Parse(body!)!["timeseries"]!.AsArray())
        {
            var entry = node!.AsObject();
            var series = entry["timeseriesId"]!.GetValue<string>();
            var points = entry["points"]!.AsArray();
            if (partition != TimeSeriesPartition)
            {
                items.Add(new JsonObject { ["content"] = string.Empty, ["result"] = TimeSeriesResult(400, "Bad Request", "Data partition id is not valid") });
                continue;
            }

            if (!kinds.TryGetValue(series, out var kind))
            {
                items.Add(new JsonObject { ["timeseriesId"] = series, ["result"] = TimeSeriesResult(404, "Not Found", $"'{series}' timeseries not found in {recordId}") });
                continue;
            }

            if (points.Count == 0)
            {
                items.Add(new JsonObject { ["result"] = TimeSeriesResult(400, "Bad Request", "No data point is posted in the request") });
                continue;
            }

            if (TimeSeriesValueProblem(series, kind, points) is { } invalid)
            {
                items.Add(new JsonObject { ["result"] = TimeSeriesResult(400, "Bad Request", invalid) });
                continue;
            }

            var version = _timeSeriesClock++;
            var stamps = points.Select(p => p!["timestamp"]!.GetValue<long>()).ToList();
            if (!TimeSeriesLost.Remove(series))
            {
                var stored = new SortedDictionary<long, JsonNode?>();
                foreach (var point in points)
                {
                    stored[point!["timestamp"]!.GetValue<long>()] = point["value"]?.DeepClone();
                }

                if (!TimeSeries.TryGetValue((recordId, series), out var versions))
                {
                    versions = [];
                    TimeSeries[(recordId, series)] = versions;
                }

                versions.Add((version, stored));
            }

            items.Add(new JsonObject
            {
                ["timeseriesId"] = series,
                ["version"] = version,
                ["start"] = stamps.Min(),
                ["end"] = stamps.Max(),
                ["metadata"] = new JsonObject { ["parameterKindId"] = kind },
                ["pointsCount"] = points.Count,
                ["result"] = TimeSeriesResult(202, "Accepted", "The request has been accepted for processing, but the processing has not been completed."),
            });
        }

        return Json((HttpStatusCode)207, new JsonArray(new JsonObject
        {
            ["recordId"] = recordId,
            ["reportingEntityId"] = record["data"]?["ReportingEntityID"]?.DeepClone(),
            ["timeseries"] = items,
            ["result"] = TimeSeriesResult(207, "Multi-Status", "Partially successful. See sub-requests response codes."),
        }));
    }

    /// <summary>
    /// The historian's query service's read of one series version (section 6.1): the version's data as known when it was
    /// accepted, later versions winning at a timestamp, in <c>[start, end)</c>; 404 "Failed to get a Stream Mapping" until
    /// the series has a mapping.
    /// </summary>
    private HttpResponseMessage TimeSeriesQuery(string method, string path, Uri uri)
    {
        if (path == "/info" && method == "GET")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["groupId"] = "org.opengroup.osdu.production", ["artifactId"] = "pddms-timeseries", ["version"] = "0.1.0" });
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (method != "GET" || parts is not ["production-values", var recordId, "timeseries", var series, "versions", var versionText])
        {
            return TimeSeriesError(HttpStatusCode.NotFound, "The requested resource could not be found.");
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        if (!long.TryParse(versionText, CultureInfo.InvariantCulture, out var version)
            || !long.TryParse(query["start"], CultureInfo.InvariantCulture, out var start)
            || !long.TryParse(query["end"], CultureInfo.InvariantCulture, out var end))
        {
            return TimeSeriesError(HttpStatusCode.BadRequest, "start, end and version must be numbers");
        }

        if (!Records.TryGetValue(recordId, out var record) || Removed.Contains(recordId))
        {
            return TimeSeriesError(HttpStatusCode.NotFound, $"Record not found {recordId}");
        }

        if (!SeriesKinds(record).ContainsKey(series))
        {
            return TimeSeriesAnswer(HttpStatusCode.NotFound, recordId, record, new JsonObject
            {
                ["timeseriesId"] = series,
                ["result"] = TimeSeriesResult(404, "Not Found", $"'{series}' timeseries not found in {recordId}"),
            });
        }

        var reads = _mappingReads.GetValueOrDefault((recordId, series)) + 1;
        _mappingReads[(recordId, series)] = reads;
        if (reads <= TimeSeriesMappingDelay || !TimeSeries.TryGetValue((recordId, series), out var versions))
        {
            return TimeSeriesAnswer(HttpStatusCode.NotFound, recordId, record, new JsonObject
            {
                ["timeseriesId"] = series,
                ["result"] = TimeSeriesResult(404, "Not Found", "Failed to get a Stream Mapping"),
            });
        }

        var known = new SortedDictionary<long, JsonNode?>();
        foreach (var (_, points) in versions.Where(v => v.Version <= version))
        {
            foreach (var (timestamp, value) in points)
            {
                known[timestamp] = value;
            }
        }

        var served = known.Where(p => p.Key >= start && p.Key < end).Take(TimeSeriesReadLimit).ToList();
        return TimeSeriesAnswer(HttpStatusCode.OK, recordId, record, new JsonObject
        {
            ["timeseriesId"] = series,
            ["version"] = version,
            ["start"] = start,
            ["end"] = end,
            ["pointsCount"] = served.Count,
            ["points"] = new JsonArray(served.Select(p => (JsonNode?)new JsonObject { ["timestamp"] = p.Key, ["value"] = p.Value?.DeepClone() }).ToArray()),
            ["result"] = TimeSeriesResult(200, "OK", "OK"),
        });
    }

    /// <summary>The kind each series of a stored ProductionValues record names, by its DDMSDatasetID.</summary>
    private static Dictionary<string, string> SeriesKinds(JsonObject record)
        => (record["data"]?["ProductionMetricValues"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(m => m["DDMSDatasetID"] is not null)
            .ToDictionary(m => m["DDMSDatasetID"]!.GetValue<string>(), m => m["ParameterKindID"]?.GetValue<string>() ?? string.Empty, StringComparer.Ordinal);

    /// <summary>The ingestion service's check of a series' values against its kind (section 3.4); null when every value passes.</summary>
    private static string? TimeSeriesValueProblem(string series, string kind, JsonArray points)
    {
        var code = kind.Split(':').SkipWhile(p => !p.EndsWith("reference-data--ParameterKind", StringComparison.Ordinal)).Skip(1).FirstOrDefault() ?? kind;
        if (kind.Contains("set-string", StringComparison.OrdinalIgnoreCase) || kind.Contains("setstring", StringComparison.OrdinalIgnoreCase))
        {
            return points.All(p => p!["value"] is JsonArray set && set.All(v => v is JsonValue text && text.GetValueKind() == JsonValueKind.String)
                    && set.Select(v => v!.GetValue<string>()).Distinct(StringComparer.Ordinal).Count() == set.Count)
                ? null
                : $"Invalid Point value - {series} input is not a SET";
        }

        if (code is not ("Double" or "Integer" or "Boolean" or "String"))
        {
            // The detector never yields the timestamp kind, so a date-time series refuses every value.
            return code == "Timestamp"
                ? $"Invalid point value type. Expected Timestamp, but invalid value(s) are found for {series}"
                : $"Unknown data type '{kind}' for '{series}'";
        }

        foreach (var point in points)
        {
            var value = point!["value"];
            var detected = value switch
            {
                JsonValue text when text.GetValueKind() == JsonValueKind.String => "String",
                JsonValue flag when flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False => "Boolean",
                JsonValue number when number.GetValueKind() == JsonValueKind.Number => number.ToJsonString().IndexOfAny(['.', 'e', 'E']) >= 0 ? "Double" : "Integer",
                _ => "Unknown",
            };
            if (detected != code && !(code == "Double" && detected == "Integer"))
            {
                return $"Invalid point value type. Expected {code}, but invalid value(s): '{value?.ToJsonString()}' of {detected} is(are) found for timestamp(s): {point["timestamp"]}";
            }
        }

        return null;
    }

    private static JsonObject TimeSeriesResult(int code, string reason, string message)
        => new() { ["code"] = code, ["reason"] = reason, ["message"] = message };

    private static HttpResponseMessage TimeSeriesAnswer(HttpStatusCode status, string recordId, JsonObject record, JsonObject series)
        => Json(status, new JsonArray(new JsonObject
        {
            ["recordId"] = recordId,
            ["reportingEntityId"] = record["data"]?["ReportingEntityID"]?.DeepClone(),
            ["timeseries"] = new JsonArray(series),
            ["result"] = TimeSeriesResult((int)status, status.ToString(), status.ToString()),
        }));

    private static HttpResponseMessage TimeSeriesError(HttpStatusCode status, string message)
        => Json(status, new JsonObject { ["result"] = TimeSeriesResult((int)status, status.ToString(), message) });

    // The token the fake Airflow 2 issues and then expects back: an unsigned JWT that expires in 2100.
    private const string AirflowAccessToken = "eyJhbGciOiJub25lIn0.eyJleHAiOjQxMDI0NDQ4MDB9.sig";

    private HttpResponseMessage Airflow(string method, string path, HttpRequestMessage request)
    {
        if (path == "/auth/token" && method == "POST")
        {
            return Json(HttpStatusCode.Created, new JsonObject { ["access_token"] = AirflowAccessToken });
        }

        var prefix = $"/api/{AirflowVersion}/dags/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || method != "GET")
        {
            return Error(HttpStatusCode.NotFound, "no airflow route");
        }

        var expected = AirflowVersion == "v2" ? "Bearer " + AirflowAccessToken : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("airflow-user:airflow-password"));
        if (request.Headers.Authorization?.ToString() != expected)
        {
            return Error(HttpStatusCode.Unauthorized, "unauthenticated");
        }

        // dags/{dag}/dagRuns/{run}/taskInstances/{task}/xcomEntries/{key}
        var parts = path[prefix.Length..].Split('/').Select(Uri.UnescapeDataString).ToArray();
        var run = Runs.FirstOrDefault(r => r.Workflow == parts[0] && r.RunId == parts[2]);
        if (run is null || !run.XCom.TryGetValue((parts[4], parts[6]), out var value))
        {
            return Error(HttpStatusCode.NotFound, "no xcom entry");
        }

        var rendered = AirflowVersion == "v1"
            ? (JsonNode?)(value is JsonValue text && text.TryGetValue<string>(out var s) ? s : PythonRepr(value))
            : value?.DeepClone();
        return Json(HttpStatusCode.OK, new JsonObject { ["key"] = parts[6], ["task_id"] = parts[4], ["dag_id"] = parts[0], ["value"] = rendered });
    }

    /// <summary>The text Airflow 2 gives for a stored Python value: <c>str()</c> of it, with single quotes.</summary>
    public static string PythonRepr(JsonNode? value) => value switch
    {
        null => "None",
        JsonArray array => "[" + string.Join(", ", array.Select(PythonRepr)) + "]",
        JsonObject obj => "{" + string.Join(", ", obj.Select(kv => "'" + kv.Key + "': " + PythonRepr(kv.Value))) + "}",
        JsonValue text when text.TryGetValue<string>(out var s) => "'" + s + "'",
        _ => value.ToJsonString(),
    };

    private static string? FormField(string form, string name)
    {
        var at = -1;
        foreach (var marker in new[] { $"name={name}\r\n", $"name=\"{name}\"", $"name={name};" })
        {
            at = form.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                break;
            }
        }

        if (at < 0)
        {
            return null;
        }

        var start = form.IndexOf("\r\n\r\n", at, StringComparison.Ordinal) + 4;
        var end = form.IndexOf("\r\n", start, StringComparison.Ordinal);
        return form[start..end];
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode node)
        => new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string message)
        => Json(status, new JsonObject { ["code"] = (int)status, ["reason"] = status.ToString(), ["message"] = message });
}

/// <summary>A record's files held in memory, one payload set, each opened fresh for every request.</summary>
internal sealed class MemoryFiles(params (string Name, string Text)[] files) : IPayloadSource
{
    public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PayloadFile>>(files.Select((f, i) => new PayloadFile(i, "mem://files/" + f.Name, Encoding.UTF8.GetByteCount(f.Text))).ToList());

    public Task<Stream> OpenAsync(PayloadFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(files[file.Index].Text), writable: false));
    }
}
