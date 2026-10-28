using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols.Ddms;

/// <summary>One content table a RAFS record's payload holds: its file, content type, content schema version and media type.</summary>
/// <param name="File">The payload file.</param>
/// <param name="ContentType">The content type, as the service's path takes it (<c>nmr</c>, <c>depthshift</c>).</param>
/// <param name="SchemaVersion">The content schema version the table follows (<c>1.0.0</c>).</param>
/// <param name="MediaType">How the table is sent: <c>application/json</c> or <c>application/x-parquet</c>.</param>
internal sealed record RafsContent(PayloadFile File, string ContentType, string SchemaVersion, string MediaType);

/// <summary>What the RAFS shape checked of a record's content before its first request.</summary>
internal sealed record RafsContentPlan(IReadOnlyList<RafsContent> Contents)
{
    public static RafsContentPlan Empty { get; } = new([]);
}

/// <summary>What a RAFS content write answered: its URN, and what the URN says.</summary>
/// <param name="Urn">The URN the service wrote into the record's <c>data.DDMSDatasets</c>.</param>
/// <param name="ContentId">The id every read of the content takes.</param>
/// <param name="SchemaVersion">The content schema version the URN names.</param>
/// <param name="DatasetId">The <c>dataset--File.Generic</c> the service registered for the content, without its version; null in blob mode.</param>
internal sealed record RafsUrn(string Urn, string ContentId, string SchemaVersion, string? DatasetId);

/// <summary>
/// The Rock and Fluid Sample DDMS v2 shape (osdu/specs/rafs-ddms/INTEGRATION.md). A record goes to
/// <c>POST /v2/{collection}</c> in an array, typed exactly <c>application/json</c>, which RAFS checks against the kinds the
/// collection accepts, the kind's schema, its mandatory references and the records it refers to before it writes through
/// Storage (section 2.3). A content collection then takes each content table of the record's payload in one request,
/// <c>POST .../{id}/data</c>, or <c>.../{id}/data/{contentType}</c> where it holds several types, with the content schema
/// version in the query (section 2.4). A payload file is named after what it holds: <c>&lt;contentType&gt;.json</c> or
/// <c>.parquet</c>, optionally <c>&lt;contentType&gt;.&lt;schemaVersion&gt;.parquet</c>, the version defaulting to
/// <c>protocolOptions.contentSchemaVersion</c>. Each content type and version is checked against the service's own
/// catalogue before anything is written, and a depth shift table must hold one row.
///
/// Each content write registers a <c>dataset--File.Generic</c> (in the default dataset mode), points a URN in the record's
/// <c>data.DDMSDatasets</c> at it, and writes a new record version; the tables of one record go one after the other, and
/// the record is read back for the version it ends at. RAFS never removes those datasets, so the ledger keeps their ids
/// and a removal takes them through Storage. <c>data.DDMSDatasets</c> belongs to RAFS: a metadata update carries the URNs
/// the stored record holds. Reads bypass the service's response cache. The record written before its content, and each
/// content dataset, are artifacts of the record's unit of work (osdu/docs/reference/concepts/artifacts-and-undo.md, RAFS):
/// a delivery that does not complete has the content datasets it minted removed, a dataset it gave a new version written
/// back, and the record removed or given back the version it replaced.
/// </summary>
internal sealed partial class RafsShape(DdmsShapeContext context) : IDdmsShape
{
    /// <summary>The target state entry listing the content datasets RAFS registered for the record.</summary>
    public const string DatasetsKey = "rafs.datasets";

    private const string DdmsDatasets = "DDMSDatasets";
    private const string DepthShiftSegment = "depthshift";

    private static readonly IReadOnlyDictionary<string, string> Catalogues = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["samplesanalysis"] = "/v2/samplesanalysis/analysistypes",
        ["fluidmodel"] = "/v2/fluidmodel/fluidmodeltypes",
    };

    private readonly OsduHttpClient _client = context.Client;
    private readonly ProtocolOptions _options = context.Options;
    private readonly DdmsRouting _routing = context.Routing;
    private readonly long _requestBodyCeiling = context.RequestBodyCeiling;
    private readonly TimeProvider _time = context.Time;
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<string, IReadOnlyList<string>>>>> _catalogues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _schemas = new(StringComparer.Ordinal);

    public async Task<object?> PrepareAsync(DeliveryWork work, DdmsRecordPaths paths, CancellationToken ct)
    {
        var route = RouteOf(paths);
        if (work.DeliverMetadata && work.Completed(OsduDdmsProtocol.MetadataStep) is null && RecordProblem(route, work.Document) is { } problem)
        {
            throw new RecordHeldException(problem);
        }

        if (!work.DeliverPayload)
        {
            return RafsContentPlan.Empty;
        }

        if (!route.Collection.Bulk)
        {
            throw new RecordHeldException($"{work.TargetId} goes to {route.Describe()}, which holds records alone and takes no content; deliver the record without a payload");
        }

        var payload = work.Payload ?? throw new RecordHeldException("the record needs content but none is attached");
        var files = await payload.ListChunksAsync(ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            throw new RecordHeldException("no content files were found for the record");
        }

        var contents = new List<RafsContent>(files.Count);
        foreach (var file in files)
        {
            if (_requestBodyCeiling > 0 && file.Size > _requestBodyCeiling)
            {
                throw new RecordHeldException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"content file {FileUploads.FileName(file.Path)} is {file.Size} bytes, above the target's declared request body ceiling of {_requestBodyCeiling} bytes (reliability.maxRequestBodyBytes); RAFS takes a table in one request"));
            }

            var content = Content(file, route, out var named);
            if (content is null)
            {
                throw new RecordHeldException(named!);
            }

            if (contents.Any(c => string.Equals(c.ContentType, content.ContentType, StringComparison.Ordinal)))
            {
                throw new RecordHeldException(
                    $"the record's payload holds {content.ContentType} content more than once; RAFS keeps one table per content type of a record, so one would overwrite the other");
            }

            contents.Add(content);
        }

        foreach (var content in contents)
        {
            if (await ContentProblemAsync(route, content, ct).ConfigureAwait(false) is { } unknown)
            {
                throw new RecordHeldException(unknown);
            }

            if (string.Equals(route.Collection.Segment, DepthShiftSegment, StringComparison.Ordinal) && content.MediaType == ParquetType)
            {
                var shape = await ParquetPayloads.ShapeAsync(payload, content.File, "its rows cannot be counted", ct).ConfigureAwait(false);
                if (shape.Rows != 1)
                {
                    throw new RecordHeldException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"the depth shift table {FileUploads.FileName(content.File.Path)} holds {shape.Rows} rows; RAFS stores a depth shift of exactly one row"));
                }
            }
        }

        return new RafsContentPlan(contents.OrderBy(c => c.ContentType, StringComparer.Ordinal).ToList());
    }

    public async Task<DeliveryOutcome> SendAsync(DeliveryWork work, DdmsRecordPaths paths, object? prepared, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var plan = prepared as RafsContentPlan ?? RafsContentPlan.Empty;
        var steps = new DeliverySteps(_time);
        var version = work.ExistingVersion;
        var metadataDelivered = false;
        var warnings = new List<string>();

        if (work.DeliverMetadata)
        {
            if (work.Completed(OsduDdmsProtocol.MetadataStep) is { } done)
            {
                steps.Resumed(OsduDdmsProtocol.MetadataStep, done);
                version = done.TryGetValue("version", out var text) ? RecordWriter.ParseVersion(text) ?? version : version;
            }
            else
            {
                var started = steps.Now;
                var (written, status, warning) = await WriteRecordAsync(work, route, ct).ConfigureAwait(false);
                version = written ?? version;
                var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
                if (version is { } v)
                {
                    returned["version"] = v.ToString(CultureInfo.InvariantCulture);
                }

                if (warning is not null)
                {
                    returned["rafs.warning"] = warning;
                    warnings.Add(warning);
                }

                steps.Add(OsduDdmsProtocol.MetadataStep, started, status, returned);

                // Content follows the record, so the record is the unit's until the content lands.
                IReadOnlyList<TargetArtifact> recordItself = work.DeliverPayload && plan.Contents.Count > 0
                    ? [TargetArtifact.RecordWritten(work.TargetId, version, work.ExistingVersion)]
                    : [];
                await work.ReportStepAsync(OsduDdmsProtocol.MetadataStep, returned, recordItself, ct).ConfigureAwait(false);
            }

            metadataDelivered = true;
        }

        var committed = DdmsShapeValues.Ids(work.TargetState, DatasetsKey);
        var datasets = committed.ToList();
        var sent = 0;
        var payloadDelivered = false;
        if (work.DeliverPayload && plan.Contents.Count > 0)
        {
            var payload = work.Payload ?? throw new RecordHeldException("the record needs content but none is attached");
            foreach (var content in plan.Contents)
            {
                var step = "content-" + content.ContentType;
                IReadOnlyDictionary<string, string> values;

                // Only a table whose write answered is resumed: an earlier try's intent alone says the write may not have
                // landed, and a write sent again re-versions the dataset the record names.
                if (work.Completed(step) is { } written && written.ContainsKey("urn"))
                {
                    steps.Resumed(step, written);
                    values = written;
                }
                else
                {
                    var started = steps.Now;
                    var url = OsduHttpClient.WithQuery(
                        _client.Url(route.DataPath!, new Dictionary<string, string>(StringComparer.Ordinal) { ["id"] = work.TargetId, ["contentType"] = content.ContentType }),
                        "content_schema_version",
                        content.SchemaVersion);

                    // RAFS mints the content's dataset: an intent goes first, so a write whose answer is lost is found by the URN
                    // RAFS wrote into the record when the delivery does not complete.
                    var slot = ContentSlot(content.ContentType);
                    await work.ReportStepAsync(
                        step + "-intent",
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["state"] = "writing" },
                        [TargetArtifact.Intent(slot, ArtifactRoles.Content, ContentLocator(work.TargetId, content.ContentType))],
                        ct).ConfigureAwait(false);

                    // A second write of the same table re-versions the same dataset and replaces its URN, so a write whose
                    // answer was lost may be sent again.
                    var result = await _client.SendStreamAsync(
                        HttpMethod.Post, url, () => OsduDdmsProtocol.OpenSync(payload, content.File), content.MediaType, content.File.Size > 0 ? content.File.Size : null, ct, idempotent: true).ConfigureAwait(false);
                    var urn = ParseUrn(result, url);
                    var answered = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["urn"] = urn.Urn,
                        ["contentId"] = urn.ContentId,
                        ["schemaVersion"] = urn.SchemaVersion,
                    };
                    if (urn.DatasetId is { } dataset)
                    {
                        answered["dataset"] = dataset;
                    }

                    steps.Add(step, started, (int)result.Status, answered);
                    await work.ReportStepAsync(step, answered, ContentArtifacts(work, content.ContentType, urn, committed), ct).ConfigureAwait(false);
                    values = answered;
                    sent++;
                }

                if (values.TryGetValue("dataset", out var registered) && !datasets.Contains(registered, StringComparer.Ordinal))
                {
                    datasets.Add(registered);
                }
            }

            // Every content write gives the record a new version, which the answer does not carry.
            var landed = await RecordWriter.VerifyAsync(_client, RecordUrl(route, work.TargetId), null, ct, DdmsShapeValues.NoStore).ConfigureAwait(false);
            version = landed.ObservedVersion
                ?? throw new DeliveryException($"The content of {work.TargetId} was written, but the record's version could not be read back from {route.RecordPath}: {landed.Detail}.");
            payloadDelivered = true;
        }

        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in steps.Steps)
        {
            var content = step.Name.StartsWith("content-", StringComparison.Ordinal) ? step.Name["content-".Length..] : null;
            foreach (var (name, value) in step.Returned)
            {
                all[content is null ? name : $"content.{content}.{name}"] = value;
            }
        }

        all["recordId"] = work.TargetId;
        if (datasets.Count > 0)
        {
            all[DatasetsKey] = string.Join(",", datasets);
        }

        if (version is { } finalVersion)
        {
            all["version"] = finalVersion.ToString(CultureInfo.InvariantCulture);
        }

        if (sent > 0)
        {
            warnings.Insert(0, string.Create(CultureInfo.InvariantCulture, $"{sent} content table(s)"));
        }

        return new DeliveryOutcome
        {
            MetadataDelivered = metadataDelivered,
            PayloadDelivered = payloadDelivered,
            TargetVersion = version,
            ChunksSent = sent,
            Detail = warnings.Count == 0 ? null : string.Join("; ", warnings),
            Returned = all,
            Steps = steps.Steps,
        };
    }

    public Task<VerifyResult> VerifyAsync(DdmsRecordPaths paths, string targetId, long? expectedVersion, CancellationToken ct)
        => RecordWriter.VerifyAsync(_client, RecordUrl(RouteOf(paths), targetId), expectedVersion, ct, DdmsShapeValues.NoStore);

    public Task<JsonObject?> ReadAsync(DdmsRecordPaths paths, string targetId, CancellationToken ct)
        => RecordWriter.ReadAsync(_client, RecordUrl(RouteOf(paths), targetId), ct, DdmsShapeValues.NoStore);

    /// <summary>
    /// The reversible scope is RAFS's logical delete (<c>DELETE /v2/{collection}/{id}</c>, storage's <c>:delete</c>
    /// underneath) and storage's reversible delete of every content dataset RAFS registered for the record, which RAFS
    /// leaves behind. RAFS has no purge and no version operation, so the history scope is storage's version purge of the
    /// record, and everything is storage's purge of the record and of its content datasets.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(DdmsRecordPaths paths, string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var datasets = DdmsShapeValues.Ids(targetState, DatasetsKey);
        if (scope == RemovalScope.History)
        {
            var versions = _routing.HistoryPath ?? throw new RecordHeldException(
                $"{route.Describe()} has no version purge, and this flow does not say where the storage service is. Give the DDMS its root under target.ddms when the endpoint "
                + "is the OSDU platform root, or name purgeVersionsPath (an absolute URL such as https://<host>/api/storage/v2/records/{id}/versions).");
            return await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options) with { PurgeVersions = versions }, targetId, scope, ct).ConfigureAwait(false);
        }

        var storagePath = scope == RemovalScope.Record ? _routing.StorageDeletePath : _routing.StoragePurgePath;
        if (storagePath is null && (scope == RemovalScope.Everything || datasets.Count > 0))
        {
            throw new RecordHeldException(
                scope == RemovalScope.Everything
                    ? $"{route.Describe()} has no purge, and this flow does not say where the storage service is to purge {targetId}. Give the DDMS its root under target.ddms "
                        + "when the endpoint is the OSDU platform root, or name purgePath (an absolute URL such as https://<host>/api/storage/v2/records/{id})."
                    : $"RAFS registered {datasets.Count.ToString(CultureInfo.InvariantCulture)} content dataset(s) for {targetId} and never removes them, and this flow does not say where "
                        + "the storage service is to remove them. Give the DDMS its root under target.ddms when the endpoint is the OSDU platform root.");
        }

        bool removedRecord;
        if (scope == RemovalScope.Record)
        {
            removedRecord = await RecordWriter.RemoveAtAsync(_client, HttpMethod.Delete, RecordUrl(route, targetId), ct).ConfigureAwait(false);
        }
        else
        {
            removedRecord = await RecordWriter.RemoveAtAsync(_client, HttpMethod.Delete, _client.Url(storagePath!, targetId), ct).ConfigureAwait(false);
        }

        var removedDatasets = 0;
        foreach (var dataset in datasets)
        {
            if (await RecordWriter.RemoveAtAsync(_client, scope == RemovalScope.Record ? HttpMethod.Post : HttpMethod.Delete, _client.Url(storagePath!, dataset), ct).ConfigureAwait(false))
            {
                removedDatasets++;
            }
        }

        if (!removedRecord && removedDatasets == 0)
        {
            return new DeleteOutcome(false, true, "record not found in OSDU");
        }

        var what = !removedRecord ? "already gone from OSDU"
            : scope == RemovalScope.Record ? "removed through RAFS (reversible)"
            : "purged from OSDU (the record and every version)";
        var contents = datasets.Count == 0 ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"; {removedDatasets} of {datasets.Count} content dataset(s) {(scope == RemovalScope.Record ? "removed (reversible)" : "purged")}, the rest already gone");
        return new DeleteOutcome(true, false, what + contents);
    }

    /// <summary>
    /// Undoes the content unfinished deliveries wrote: a content dataset RAFS minted for the unit is removed reversibly through
    /// storage (RAFS never removes one); a write whose answer was lost is found by the URN RAFS wrote into the record, and
    /// its dataset removed when it is not one an earlier delivery made. A dataset the unit gave a new version is the record's
    /// to write back (<see cref="ArtifactRoles.Version"/>), with the record. The files a content dataset points at stay.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoAsync(UndoWork work, DdmsRecordPaths paths, IReadOnlyList<UndoItem> items, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var results = new List<UndoResult>(items.Count);
        var committed = DdmsShapeValues.Ids(work.TargetState, DatasetsKey).ToHashSet(StringComparer.Ordinal);
        JsonObject? stored = null;
        var read = false;
        foreach (var item in items)
        {
            if (item.Artifact.Role == ArtifactRoles.Objects)
            {
                results.Add(UndoResult.Kept(item, "blob-mode content stays in RAFS's own store, which no call removes; the record given back its earlier version no longer names it"));
                continue;
            }

            if (item.Artifact.Role != ArtifactRoles.Content)
            {
                results.Add(UndoResult.Kept(item, "the RAFS shape makes nothing of this kind beside a record"));
                continue;
            }

            if (_routing.StorageDeletePath is not { } storage)
            {
                results.Add(UndoResult.Kept(item, "RAFS never removes a content dataset, and this flow does not say where the storage service is to remove it; give the DDMS its root under target.ddms when the endpoint is the OSDU platform root"));
                continue;
            }

            try
            {
                var id = item.Artifact.TargetId;
                if (id is null)
                {
                    if (!read)
                    {
                        stored = await RecordWriter.ReadAsync(_client, RecordUrl(route, work.TargetId), ct, DdmsShapeValues.NoStore).ConfigureAwait(false);
                        read = true;
                    }

                    var type = item.Artifact.Locator is { } locator && locator.LastIndexOf('#') is var hash and > 0 ? locator[(hash + 1)..] : null;
                    id = type is null ? null : MintedFor(stored, type, committed);
                    if (id is null && type is not null && RevisedFor(stored, type, committed, work.TargetState) is { } revised)
                    {
                        // The write landed on the dataset an earlier delivery made and gave it a new version: that version is
                        // taken back as the answered write's would be, the version the ledger holds written back.
                        results.Add(await RewriteAsync(work, item, revised.Dataset, revised.Version, revised.Prior, ct).ConfigureAwait(false));
                        continue;
                    }

                    if (id is null)
                    {
                        results.Add(UndoResult.Gone(item, $"{work.TargetId} names no {type ?? "such"} content dataset an earlier delivery did not make: RAFS did not write the content, or failed before naming it in the record (an inventory of the dataset kind finds a dataset it registered then)"));
                        continue;
                    }
                }

                var outcome = await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options) with { Delete = storage }, id, RemovalScope.Record, ct).ConfigureAwait(false);
                results.Add(outcome.AlreadyGone ? UndoResult.Gone(item, $"{id}: {outcome.Detail}") : UndoResult.Removed(item, $"{id}: {outcome.Detail}"));
            }
            catch (Exception ex) when (ArtifactUndo.Answerable(ex, ct))
            {
                results.Add(UndoResult.Failed(item, ArtifactUndo.Redact(ex)));
            }
        }

        return results;
    }

    /// <summary>The record's reversible removal is RAFS's logical delete; its read (past RAFS's cache) says when OSDU created it.</summary>
    public RecordSide RecordSide(DdmsRecordPaths paths)
    {
        var route = RouteOf(paths);
        return new RecordSide
        {
            Remove = async (id, token) => await RecordWriter.RemoveAtAsync(_client, HttpMethod.Delete, RecordUrl(route, id), token).ConfigureAwait(false)
                ? new DeleteOutcome(true, false, "removed through RAFS (reversible)")
                : new DeleteOutcome(false, true, "record not found in OSDU"),
            Read = (id, token) => RecordWriter.ReadAsync(_client, RecordUrl(route, id), token, DdmsShapeValues.NoStore),
        };
    }

    /// <summary>The slot of a content table's artifact: one per content type of a unit, as RAFS keeps one table per type.</summary>
    private static string ContentSlot(string contentType) => "content:" + contentType;

    /// <summary>What finds a content write whose answer was lost: the record, and the content type whose URN RAFS writes into it.</summary>
    private static string ContentLocator(string targetId, string contentType) => targetId + "#" + contentType;

    /// <summary>
    /// What a content write made, as its artifact: a dataset RAFS minted for the unit (<see cref="ArtifactRoles.Content"/>),
    /// or a new version of a dataset an earlier delivery made (<see cref="ArtifactRoles.Version"/>, with the version the record
    /// named before, which an undo writes back); none in blob mode, where RAFS keeps no dataset.
    /// </summary>
    private static IReadOnlyList<TargetArtifact> ContentArtifacts(DeliveryWork work, string contentType, RafsUrn urn, IReadOnlyCollection<string> committed)
    {
        if (urn.DatasetId is not { } dataset)
        {
            // Blob mode: RAFS keeps the table in its own store, not as an OSDU record, so it stands only for the unit's progress.
            return [TargetArtifact.Created(ContentSlot(contentType), ArtifactRoles.Objects, urn.ContentId, locator: ContentLocator(work.TargetId, contentType))];
        }

        var version = VersionOf(urn.ContentId);
        if (!committed.Contains(dataset, StringComparer.Ordinal))
        {
            return [TargetArtifact.Created(ContentSlot(contentType), ArtifactRoles.Content, dataset, version, ContentLocator(work.TargetId, contentType))];
        }

        var prior = work.TargetState.TryGetValue($"content.{contentType}.contentId", out var named) ? VersionOf(named) : null;
        return [new TargetArtifact
        {
            Slot = ContentSlot(contentType),
            Role = ArtifactRoles.Version,
            TargetId = dataset,
            Version = version,
            PriorVersion = prior,
            Locator = ContentLocator(work.TargetId, contentType),
        }];
    }

    /// <summary>The version a content id names after its last colon (<c>{dataset id}:{version}</c>), or null.</summary>
    private static long? VersionOf(string contentId)
    {
        var colon = contentId.LastIndexOf(':');
        return colon > 0 && long.TryParse(contentId[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : null;
    }

    /// <summary>
    /// The content dataset RAFS minted for <paramref name="contentType"/> that the record names and no earlier delivery made:
    /// RAFS names a dataset it mints <c>{partition}:dataset--File.Generic:{contentType}-{uuid}</c>, and writes its URN into
    /// <c>data.DDMSDatasets</c>.
    /// </summary>
    private static string? MintedFor(JsonObject? stored, string contentType, IReadOnlySet<string> committed)
    {
        if ((stored?["data"] as JsonObject)?[DdmsDatasets] is not JsonArray list)
        {
            return null;
        }

        foreach (var urn in list.Select(Text).OfType<string>().Where(u => u.StartsWith("urn://rafs-v2/", StringComparison.Ordinal)))
        {
            var segments = urn.Split('/');
            if (segments.Length < 5)
            {
                continue;
            }

            var contentId = segments[^2];
            var colon = contentId.LastIndexOf(':');
            var dataset = colon > 0 && contentId[(colon + 1)..].All(char.IsAsciiDigit) ? contentId[..colon] : contentId;
            if (!committed.Contains(dataset) && DdmsShapeValues.EntityId(dataset) is { } entity && entity.StartsWith(contentType + "-", StringComparison.Ordinal))
            {
                return dataset;
            }
        }

        return null;
    }

    /// <summary>
    /// The content dataset of <paramref name="contentType"/> an earlier delivery made that the record names at another version
    /// than the one the ledger holds (<c>content.{type}.contentId</c>): a content write whose answer was lost landed on it.
    /// </summary>
    private static (string Dataset, long? Version, long? Prior)? RevisedFor(JsonObject? stored, string contentType, IReadOnlySet<string> committed, IReadOnlyDictionary<string, string> targetState)
    {
        if ((stored?["data"] as JsonObject)?[DdmsDatasets] is not JsonArray list
            || !targetState.TryGetValue($"content.{contentType}.contentId", out var held))
        {
            return null;
        }

        foreach (var urn in list.Select(Text).OfType<string>().Where(u => u.StartsWith("urn://rafs-v2/", StringComparison.Ordinal)))
        {
            var segments = urn.Split('/');
            if (segments.Length < 5)
            {
                continue;
            }

            var contentId = segments[^2];
            var colon = contentId.LastIndexOf(':');
            var dataset = colon > 0 && contentId[(colon + 1)..].All(char.IsAsciiDigit) ? contentId[..colon] : contentId;
            if (committed.Contains(dataset) && DdmsShapeValues.EntityId(dataset) is { } entity && entity.StartsWith(contentType + "-", StringComparison.Ordinal)
                && !string.Equals(contentId, held, StringComparison.Ordinal))
            {
                return (dataset, VersionOf(contentId), VersionOf(held));
            }
        }

        return null;
    }

    /// <summary>
    /// Writes back the version of a content dataset the ledger holds over the one a lost write gave it, through storage (the
    /// write a reversal makes), and answers for <paramref name="item"/>.
    /// </summary>
    private async Task<UndoResult> RewriteAsync(UndoWork work, UndoItem item, string dataset, long? version, long? prior, CancellationToken ct)
    {
        _storage ??= new OsduRecordProtocol(_client, RecordRestores.WriterOptions(new ProtocolOptions { BatchSize = _options.BatchSize }), _time);
        var revised = item with
        {
            Artifact = item.Artifact with { Role = ArtifactRoles.Version, TargetId = dataset, Version = version, PriorVersion = prior },
        };
        var side = new RecordSide
        {
            Read = _storage.ReadAsync,
            Restorer = _storage,
            Versions = _storage.VersionsAsync,
            RemoveRefusal = "a content dataset an earlier delivery made is given back its version, not removed",
        };
        var result = (await ArtifactUndo.RecordItselfAsync(work with { KeepRecord = false }, [revised], side, ct).ConfigureAwait(false))[0];
        return result with { Item = item };
    }

    /// <summary>The storage writer an undo writes a content dataset's version back through, made when one first needs it.</summary>
    private OsduRecordProtocol? _storage;

    /// <summary>
    /// Carries the content URNs <paramref name="stored"/> holds in <c>data.DDMSDatasets</c> into <paramref name="document"/>,
    /// in place of any the document rendered itself; other entries of the list are left as rendered. Returns false when
    /// the document rendered RAFS URNs that differ from the stored ones.
    /// </summary>
    public bool CarryLink(JsonObject? stored, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var held = (stored?["data"] as JsonObject)?[DdmsDatasets] is JsonArray storedList
            ? storedList.Select(Text).OfType<string>().Where(IsRafsUrn).ToList()
            : [];
        var data = document["data"] as JsonObject;
        var list = data?[DdmsDatasets] as JsonArray;
        var rendered = list?.Select(Text).OfType<string>().Where(IsRafsUrn).ToList() ?? [];
        var agreed = rendered.Count == 0 || rendered.ToHashSet(StringComparer.Ordinal).SetEquals(held);
        if (held.Count == 0 && rendered.Count == 0)
        {
            return agreed;
        }

        if (data is null)
        {
            if (document["data"] is not null)
            {
                return agreed;
            }

            data = new JsonObject();
            document["data"] = data;
        }

        if (list is null)
        {
            if (data[DdmsDatasets] is not null)
            {
                return agreed;
            }

            list = new JsonArray();
            data[DdmsDatasets] = list;
        }

        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (Text(list[i]) is { } entry && IsRafsUrn(entry))
            {
                list.RemoveAt(i);
            }
        }

        foreach (var urn in held.Distinct(StringComparer.Ordinal))
        {
            list.Add(urn);
        }

        return agreed;
    }

    /// <summary>
    /// Why RAFS would refuse <paramref name="document"/> for <paramref name="route"/>'s collection before it writes it, or
    /// null (osdu/specs/rafs-ddms/INTEGRATION.md sections 2.3 and 3.1).
    /// </summary>
    internal static string? RecordProblem(DdmsRoute route, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(document);
        var kind = Text(document["kind"]);
        var parts = kind?.Split(':') ?? [];
        if (kind is null || parts.Length != 4 || !string.Equals(parts[1], "wks", StringComparison.Ordinal) || !SemanticVersion().IsMatch(parts[3]))
        {
            return $"the kind '{kind}' is not one RAFS accepts: <authority>:wks:<entity type>:<major.minor.patch>";
        }

        if (document["acl"] is not JsonObject acl || OnlyKeys(acl, "viewers", "owners") is not null || !NonEmpty(acl["viewers"]) || !NonEmpty(acl["owners"]))
        {
            return "acl must hold non-empty viewers and owners and nothing else; RAFS refuses any other acl";
        }

        if (document["legal"] is not JsonObject legal || OnlyKeys(legal, "legaltags", "otherRelevantDataCountries", "status") is not null
            || !NonEmpty(legal["legaltags"]) || !NonEmpty(legal["otherRelevantDataCountries"]))
        {
            return "legal must hold non-empty legaltags and otherRelevantDataCountries (and optionally status) and nothing else; RAFS refuses any other legal block, and Storage one without a tag or a country";
        }

        if (document["data"] is not JsonObject data)
        {
            return "the record has no data object, which RAFS requires";
        }

        if (string.Equals(route.Collection.EntityType, "work-product-component--SamplesAnalysis", StringComparison.OrdinalIgnoreCase)
            && !(data["SampleAnalysisTypeIDs"] is JsonArray types && types.Any(t => Text(t) is not null)))
        {
            return "data.SampleAnalysisTypeIDs names no analysis type; RAFS requires at least one on a SamplesAnalysis";
        }

        if (string.Equals(route.Collection.EntityType, "work-product-component--SaturationFunctionSet", StringComparison.OrdinalIgnoreCase)
            && !HasIdentifiedFunction(data["SaturationFunctions"]))
        {
            return "data.SaturationFunctions carries no value under a key naming an ID; RAFS requires one on a SaturationFunctionSet";
        }

        return null;
    }

    /// <summary>
    /// What a content write answered, read from its <c>ddms_urn</c>: the content id and schema version sit in the last two
    /// URN segments, in dataset mode as <c>{dataset id}:{version}/{schema version}</c>, in blob mode as
    /// <c>{schema version}/{uuid}</c> (osdu/specs/rafs-ddms/INTEGRATION.md section 4).
    /// </summary>
    internal static RafsUrn ParseUrn(HttpFetchResult result, Uri url)
    {
        var body = OsduHttpClient.ParseJson(result, url);
        var urn = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("ddms_urn", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new DeliveryException($"{url.AbsolutePath} answered the content write without the ddms_urn RAFS writes into the record.");
        var segments = urn.Split('/');
        if (segments.Length < 5 || segments[^1].Length == 0 || segments[^2].Length == 0)
        {
            throw new DeliveryException($"{url.AbsolutePath} answered the content write with the URN '{urn}', which does not name the content it wrote.");
        }

        if (urn.StartsWith("urn://rafs-v2/", StringComparison.Ordinal))
        {
            var contentId = segments[^2];
            var colon = contentId.LastIndexOf(':');
            var dataset = colon > 0 && contentId[(colon + 1)..].All(char.IsAsciiDigit) ? contentId[..colon] : contentId;
            return new RafsUrn(urn, contentId, segments[^1], dataset);
        }

        if (urn.StartsWith("urn://rafs/", StringComparison.Ordinal))
        {
            return new RafsUrn(urn, segments[^1], segments[^2], null);
        }

        throw new DeliveryException($"{url.AbsolutePath} answered the content write with the URN '{urn}', which is neither a dataset-mode nor a blob-mode RAFS URN.");
    }

    private const string ParquetType = "application/x-parquet";

    /// <summary>
    /// The content a payload file holds, from its name: <c>&lt;contentType&gt;[.&lt;schemaVersion&gt;].json|parquet</c>. A
    /// collection holding one content type takes files named after the collection.
    /// </summary>
    private RafsContent? Content(PayloadFile file, DdmsRoute route, out string? problem)
    {
        var name = FileUploads.FileName(file.Path);
        var mediaType = name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase) ? ParquetType
            : name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json"
            : null;
        if (mediaType is null)
        {
            problem = $"the content file {name} is neither .json nor .parquet, the two forms RAFS takes a table in";
            return null;
        }

        var stem = name[..name.LastIndexOf('.')];
        var dot = stem.IndexOf('.', StringComparison.Ordinal);
        var type = dot < 0 ? stem : stem[..dot];
        var version = dot < 0 ? _options.ContentSchemaVersion : stem[(dot + 1)..];
        if (!ContentTypeName().IsMatch(type))
        {
            problem = $"the content file {name} does not start with a content type ({type}): RAFS takes content types in lower-case letters and digits, such as nmr or depthshift";
            return null;
        }

        if (!SchemaVersionPattern().IsMatch(version))
        {
            problem = $"the content file {name} names the content schema version '{version}', which is not a version such as 1.0.0";
            return null;
        }

        if (!route.Collection.TypedContent && !string.Equals(type, route.Collection.Segment, StringComparison.Ordinal))
        {
            problem = $"the content file {name} holds {type} content, and {route.Describe()} holds {route.Collection.Segment} content alone; name the file {route.Collection.Segment}.json or {route.Collection.Segment}.parquet";
            return null;
        }

        problem = null;
        return new RafsContent(file, type, version, mediaType);
    }

    /// <summary>
    /// Why RAFS has no model for <paramref name="content"/>, or null: a collection holding several types is asked for its
    /// type catalogue (<c>GET /v2/samplesanalysis/analysistypes</c>, <c>GET /v2/fluidmodel/fluidmodeltypes</c>), one
    /// holding one type for its content schema at the version (<c>GET /v2/{collection}/data/schema</c>), each read once.
    /// </summary>
    private async Task<string?> ContentProblemAsync(DdmsRoute route, RafsContent content, CancellationToken ct)
    {
        var root = route.Service.Root ?? string.Empty;
        if (route.Collection.TypedContent)
        {
            if (!Catalogues.TryGetValue(route.Collection.Segment, out var catalogue))
            {
                return null;
            }

            var types = await CachedAsync(_catalogues, root + catalogue, () => CatalogueAsync(root + catalogue, ct)).ConfigureAwait(false);
            if (!types.TryGetValue(content.ContentType, out var versions))
            {
                return $"RAFS serves no {content.ContentType} content in {route.Describe()}; its catalogue lists {DdmsShapeValues.Bounded(types.Keys.Order(StringComparer.Ordinal).ToList(), 60)}";
            }

            return versions.Contains(content.SchemaVersion, StringComparer.Ordinal)
                ? null
                : $"RAFS serves {content.ContentType} content at the schema version(s) {string.Join(", ", versions)}, not {content.SchemaVersion}";
        }

        var schema = OsduHttpClient.WithQuery(_client.Url(route.CollectionPath + "/data/schema"), "content_schema_version", content.SchemaVersion);
        return await CachedAsync(_schemas, schema.AbsoluteUri, () => SchemaProblemAsync(schema, route, content, ct)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> CatalogueAsync(string path, CancellationToken ct)
    {
        var url = _client.Url(path);
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, null, ct, headers: DdmsShapeValues.NoStore).ConfigureAwait(false);
        var body = OsduHttpClient.ParseJson(result, url);
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new DeliveryException($"{url.AbsolutePath} answered with something other than the type catalogue RAFS serves there.");
        }

        var types = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var type in body.EnumerateObject())
        {
            types[type.Name] = type.Value.ValueKind == JsonValueKind.Array
                ? type.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                : [];
        }

        return types;
    }

    private async Task<string?> SchemaProblemAsync(Uri url, DdmsRoute route, RafsContent content, CancellationToken ct)
    {
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct, headers: DdmsShapeValues.NoStore).ConfigureAwait(false);
        return (int)result.Status == 404
            ? $"RAFS has no {content.ContentType} content schema at version {content.SchemaVersion} for {route.Describe()}: {HeaderRedaction.RedactMessage(Preview(result.BodyText))}"
            : null;
    }

    /// <summary>
    /// Writes the record in an array, typed exactly <c>application/json</c>. A record the ledger knows carries the content
    /// URNs the stored version holds, and the data keys the flow preserves. The version is matched by id in the
    /// response, whose names are camelCase on the wire and snake_case in the contract; a record Storage skipped is read
    /// back. Returns the warning a FluidModel without its type gets.
    /// </summary>
    private async Task<(long? Version, int Status, string? Warning)> WriteRecordAsync(DeliveryWork work, DdmsRoute route, CancellationToken ct)
    {
        var document = (JsonObject)work.Document.DeepClone();
        if (work.ExistingVersion is not null || DdmsShapeValues.Ids(work.TargetState, DatasetsKey).Count > 0 || _options.PreserveDataKeys.Count > 0)
        {
            var stored = await RecordWriter.ReadAsync(_client, RecordUrl(route, work.TargetId), ct, DdmsShapeValues.NoStore).ConfigureAwait(false);
            if (stored is not null)
            {
                CarryLink(stored, document);
                if (_options.PreserveDataKeys.Count > 0)
                {
                    RecordWriter.Preserve(stored, document, _options.PreserveDataKeys, StepLinks.Of(work));
                }
            }
        }

        var url = _client.Url(route.CollectionPath);
        var result = await _client.SendJsonAsync(HttpMethod.Post, url, new JsonArray(document), null, ct, idempotent: true, bareJsonType: true).ConfigureAwait(false);
        long? version = null;
        long skipped = 0;
        string? warning = null;
        if (result.Body.Length > 0)
        {
            var body = OsduHttpClient.ParseJson(result, url);
            if (body.ValueKind == JsonValueKind.Object)
            {
                foreach (var idVersion in Strings(body, "recordIdVersions", "record_id_versions"))
                {
                    var colon = idVersion.LastIndexOf(':');
                    if (colon > 0 && string.Equals(idVersion[..colon], work.TargetId, StringComparison.Ordinal))
                    {
                        version = RecordWriter.ParseVersion(idVersion);
                    }
                }

                foreach (var name in new[] { "skippedRecordCount", "skipped_record_count" })
                {
                    if (body.TryGetProperty(name, out var count) && count.TryGetInt64(out var n))
                    {
                        skipped = n;
                    }
                }

                if (body.TryGetProperty("warning", out var said) && said.ValueKind == JsonValueKind.String)
                {
                    var named = Strings(body, "warningRecordIds", "warning_record_ids").ToList();
                    warning = named.Count == 0 || named.Any(n => n.StartsWith(work.TargetId, StringComparison.Ordinal))
                        ? "RAFS warned: " + said.GetString()
                        : null;
                }
            }
        }

        if (version is null && (skipped > 0 || result.Body.Length == 0))
        {
            var stored = await RecordWriter.VerifyAsync(_client, RecordUrl(route, work.TargetId), null, ct, DdmsShapeValues.NoStore).ConfigureAwait(false);
            version = stored.ObservedVersion;
        }

        return (version, (int)result.Status, warning);
    }

    private Uri RecordUrl(DdmsRoute route, string targetId) => _client.Url(route.RecordPath, targetId);

    private static DdmsRoute RouteOf(DdmsRecordPaths paths)
        => paths.Route ?? throw new InvalidOperationException($"{paths.EntityType} records have no RAFS collection to go to.");

    private static async Task<T> CachedAsync<T>(ConcurrentDictionary<string, Lazy<Task<T>>> cache, string key, Func<Task<T>> load)
    {
        var entry = cache.GetOrAdd(key, _ => new Lazy<Task<T>>(load));
        try
        {
            return await entry.Value.ConfigureAwait(false);
        }
        catch
        {
            // A failed read is asked again by the next record rather than remembered.
            cache.TryRemove(new KeyValuePair<string, Lazy<Task<T>>>(key, entry));
            throw;
        }
    }

    private static IEnumerable<string> Strings(JsonElement body, params string[] names)
    {
        foreach (var name in names)
        {
            if (body.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String))
                {
                    yield return item.GetString()!;
                }
            }
        }
    }

    private static bool IsRafsUrn(string entry)
        => entry.StartsWith("urn://rafs-v2/", StringComparison.Ordinal) || entry.StartsWith("urn://rafs/", StringComparison.Ordinal);

    private static string? OnlyKeys(JsonObject block, params string[] allowed)
        => block.Select(p => p.Key).FirstOrDefault(key => !allowed.Contains(key, StringComparer.Ordinal));

    private static bool NonEmpty(JsonNode? node) => node is JsonArray array && array.Any(item => Text(item) is not null);

    private static bool HasIdentifiedFunction(JsonNode? functions)
    {
        IEnumerable<JsonObject> items = functions switch
        {
            JsonArray array => array.OfType<JsonObject>(),
            JsonObject single => [single],
            _ => [],
        };
        return items.Any(item => item.Any(p => p.Key.Contains("ID", StringComparison.Ordinal) && p.Value switch
        {
            null => false,
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonArray list => list.Count > 0,
            _ => true,
        }));
    }

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string Preview(string text) => text.Length <= 300 ? text : text[..300] + "...";

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersion();

    // deps/schema_version.py: the query parameter's version form.
    [GeneratedRegex(@"^\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaVersionPattern();

    [GeneratedRegex(@"^[a-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ContentTypeName();
}
