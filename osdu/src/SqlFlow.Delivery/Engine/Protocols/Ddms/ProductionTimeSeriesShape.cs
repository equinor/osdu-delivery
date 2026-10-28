using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols.Ddms;

/// <summary>What the historian shape checked of a record's points before its first request.</summary>
/// <param name="Files">The points files, in payload order.</param>
/// <param name="Series">The series the record defines, by <c>DDMSDatasetID</c>.</param>
/// <param name="Points">How many points the files hold.</param>
/// <param name="Requests">How many requests they are sent in.</param>
/// <param name="Limit">The largest request body, in bytes.</param>
internal sealed record TimeSeriesPlan(IReadOnlyList<PointFile> Files, IReadOnlyDictionary<string, SeriesDefinition> Series, long Points, int Requests, long Limit)
{
    public static TimeSeriesPlan Empty { get; } = new([], new Dictionary<string, SeriesDefinition>(StringComparer.Ordinal), 0, 0, 0);
}

/// <summary>What the ingestion service answered for one series of a request.</summary>
internal sealed record SeriesAnswer(string Id, int Code, long? Version, long? Points, long? Start, long? End, string? Message)
{
    public bool Accepted => Code is >= 200 and < 300;
}

/// <summary>
/// The Production DDMS historian shape (osdu/specs/production-timeseries/INTEGRATION.md). A
/// <c>work-product-component--ProductionValues</c> record is a Storage record (section 3.1): it is written with
/// <c>PUT /api/storage/v2/records</c>, carrying the link to its points in <c>data.DDMSDatasets</c>
/// (<c>urn://pddms/production-values/{id}/timeseries</c>), and read, verified and removed through Storage. Its points are
/// the record's payload, one or more points files (<see cref="TimeSeriesPoints"/>), sent to the ingestion service with
/// <c>POST /production-values/{id}/timeseries</c> in requests of at most the DDMS's <c>maxRequestBytes</c> (section 3.2),
/// typed exactly <c>application/json</c> and carrying the attempt's correlation id as <c>trace-id</c>.
///
/// The service takes no request id, and every accepted series is a new version (section 5), so each request is a step
/// that records the body's hash and the version of each series: a later try splits the points the same way and sends only
/// the requests no try recorded. An answer that is lost after the service acted is the one case a request goes twice; the
/// second version holds the same points. The outcome is read per series from each item's <c>result.code</c>, matched to
/// the request by position, never from the HTTP status, which is 207 for every batch (section 3.3). A series the service
/// refuses holds the record, with the versions of the series it accepted named.
///
/// An acceptance is not proof the points were stored: the service publishes them without waiting (section 3.5). Each
/// accepted version is read back through the query service,
/// <c>GET /production-values/{id}/timeseries/{timeseriesId}/versions/{version}?start=&amp;end=</c>, in ranges of at most
/// <see cref="TimeSeriesRequestBuilder.SettleWindowPoints"/> points, until it serves at least the points sent, for at most
/// the DDMS's <c>settleSeconds</c> (section 6.2). A series the query service does not serve yet (404 "Failed to get a
/// Stream Mapping") is waited for; one that is still missing when the time is up leaves the record for the next try, which
/// reads it back again without sending the points again.
///
/// The historian has no delete for points (section 6.3): a removal takes the record through Storage and says so. The record
/// written before its points, and the series versions each request was accepted under, are artifacts of the record's unit
/// of work (osdu/docs/reference/concepts/artifacts-and-undo.md, Production historian): a delivery that does not complete
/// has the record removed or given back the version it replaced, and its points named on the undo, which cannot take them
/// back.
/// </summary>
internal sealed partial class ProductionTimeSeriesShape(DdmsShapeContext context) : IDdmsShape
{
    /// <summary>The steps sending the points: <c>points-1</c>, <c>points-2</c>, one per request.</summary>
    public const string PointsStepPrefix = "points-";

    /// <summary>The optional correlation header the historian's services log a request under and answer as <c>CorrelationId</c> (section 1.3).</summary>
    public const string TraceHeader = "trace-id";

    /// <summary>The prefix of every link the historian's records carry in <c>data.DDMSDatasets</c>.</summary>
    public const string LinkPrefix = "urn://pddms/";

    /// <summary>A points step's value holding the SHA-256 of the request body.</summary>
    public const string HashValue = "hash";

    /// <summary>A points step's value saying the query service served every point of the request.</summary>
    public const string SettledValue = "settled";

    /// <summary>The smallest request body the route sends points in.</summary>
    private const long SmallestLimit = 1_024;

    private const string DdmsDatasets = "DDMSDatasets";
    private const string ProductionMetricValues = "ProductionMetricValues";
    private const string StorageVersionPath = "recordIdVersions[0]";
    private const int MaxVersionsKept = 50;

    private static readonly TimeSeriesSettings Defaults = new() { QueryRoot = DdmsCatalog.UsualTimeSeriesQueryRoot };

    private readonly OsduHttpClient _client = context.Client;
    private readonly ProtocolOptions _options = context.Options;
    private readonly DdmsRouting _routing = context.Routing;
    private readonly ILogger _logger = context.Logger;
    private readonly long _requestBodyCeiling = context.RequestBodyCeiling;
    private readonly TimeProvider _time = context.Time;

    public async Task<object?> PrepareAsync(DeliveryWork work, DdmsRecordPaths paths, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var writesRecord = work.DeliverMetadata && work.Completed(OsduDdmsProtocol.MetadataStep) is null;
        if (!writesRecord && !work.DeliverPayload)
        {
            return TimeSeriesPlan.Empty;
        }

        if (RecordProblem(work.Document) is { } problem)
        {
            throw new RecordHeldException(problem);
        }

        if (!work.DeliverPayload)
        {
            return TimeSeriesPlan.Empty;
        }

        var payload = work.Payload ?? throw new RecordHeldException("the record needs points but no points file is attached");
        var listed = await payload.ListChunksAsync(ct).ConfigureAwait(false);
        if (listed.Count == 0)
        {
            throw new RecordHeldException("no points file was found for the record");
        }

        var files = new List<PointFile>(listed.Count);
        foreach (var file in listed)
        {
            var name = FileUploads.FileName(file.Path);
            var form = TimeSeriesPoints.FormOf(name)
                ?? throw new RecordHeldException($"the points file {name} is neither .parquet nor .json, the two forms the historian's points are read from");
            files.Add(new PointFile(file, form, name));
        }

        if (SeriesProblem((JsonObject)work.Document["data"]!, out var series) is { } unread)
        {
            throw new RecordHeldException(unread);
        }

        var limit = LimitOf(route);
        using var builder = new TimeSeriesRequestBuilder(limit);
        long points = 0;
        await TimeSeriesPoints.ScanAsync(
            payload,
            files,
            series,
            (id, timestamp, value) =>
            {
                points++;
                builder.Add(id, timestamp, value);
                return ValueTask.CompletedTask;
            },
            ct).ConfigureAwait(false);
        builder.Finish();
        if (points == 0)
        {
            throw new RecordHeldException(
                $"the points files ({string.Join(", ", files.Select(f => f.Name))}) hold no point: every value is empty. Give the series values, or deliver the record without its points");
        }

        return new TimeSeriesPlan(files, series, points, builder.Requests, limit);
    }

    public async Task<DeliveryOutcome> SendAsync(DeliveryWork work, DdmsRecordPaths paths, object? prepared, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var plan = prepared as TimeSeriesPlan
            ?? throw new InvalidOperationException("The historian shape was handed work another shape prepared.");
        var steps = new DeliverySteps(_time);
        var version = work.ExistingVersion;
        var metadataDelivered = false;
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
                var (written, status) = await WriteRecordAsync(work, route, ct).ConfigureAwait(false);
                version = written ?? version;
                var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
                if (version is { } v)
                {
                    returned["version"] = v.ToString(CultureInfo.InvariantCulture);
                }

                steps.Add(OsduDdmsProtocol.MetadataStep, started, status, returned);

                // Points follow the record, so the record is the unit's until they are accepted.
                IReadOnlyList<TargetArtifact> recordItself = work.DeliverPayload && plan.Points > 0
                    ? [TargetArtifact.RecordWritten(work.TargetId, version, work.ExistingVersion)]
                    : [];
                await work.ReportStepAsync(OsduDdmsProtocol.MetadataStep, returned, recordItself, ct).ConfigureAwait(false);
            }

            metadataDelivered = true;
        }

        var all = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
        string? detail = null;
        var posted = 0;
        var payloadDelivered = false;
        if (work.DeliverPayload && plan.Points > 0)
        {
            var payload = work.Payload ?? throw new RecordHeldException("the record needs points but no points file is attached");
            var accepted = await SendPointsAsync(work, route, plan, payload, steps, ct).ConfigureAwait(false);
            posted = accepted.Count(a => !a.Resumed);
            var settled = await SettleAsync(work, route, accepted, steps, ct).ConfigureAwait(false);
            Summarize(plan, accepted, settled, all);
            var resumed = accepted.Count - posted;
            detail = string.Create(
                CultureInfo.InvariantCulture,
                $"{plan.Points} point(s) of {accepted.SelectMany(a => a.Series).Select(s => s.Id).Distinct(StringComparer.Ordinal).Count()} series in {accepted.Count} request(s)")
                + (resumed > 0 ? string.Create(CultureInfo.InvariantCulture, $", {resumed} of them accepted on an earlier try") : string.Empty)
                + "; " + settled;
            payloadDelivered = true;
        }

        if (version is { } finalVersion)
        {
            all["version"] = finalVersion.ToString(CultureInfo.InvariantCulture);
        }

        return new DeliveryOutcome
        {
            MetadataDelivered = metadataDelivered,
            PayloadDelivered = payloadDelivered,
            TargetVersion = version,
            ChunksSent = posted,
            Detail = detail,
            Returned = all,
            Steps = steps.Steps,
        };
    }

    public Task<VerifyResult> VerifyAsync(DdmsRecordPaths paths, string targetId, long? expectedVersion, CancellationToken ct)
        => RecordWriter.VerifyAsync(_client, RouteOf(paths).RecordPath, targetId, expectedVersion, ct);

    public Task<JsonObject?> ReadAsync(DdmsRecordPaths paths, string targetId, CancellationToken ct)
        => RecordWriter.ReadAsync(_client, RouteOf(paths).RecordPath, targetId, ct);

    /// <summary>
    /// Every scope is Storage's, since the record is a Storage record: <c>POST /records/{id}:delete</c>, the version purge
    /// and the purge. The points stay in the historian, which has no delete for them (section 6.3).
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(DdmsRecordPaths paths, string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        var route = RouteOf(paths);
        var path = scope switch
        {
            RemovalScope.Record => _routing.StorageDeletePath,
            RemovalScope.History => _routing.HistoryPath,
            _ => _routing.StoragePurgePath,
        } ?? throw new RecordHeldException(
            $"{route.Describe()} keeps its records in Storage, and this flow does not say where the storage service is. Give the DDMS its root under target.ddms, "
            + "the flow's endpoint being the OSDU platform root.");
        var outcome = await RecordWriter.DeleteAsync(_client, new RemovalPaths(path, path, path), targetId, scope, ct).ConfigureAwait(false);
        return outcome with { Detail = outcome.Detail + "; the points of its series stay in the historian, which has no delete for points" };
    }

    /// <summary>
    /// Puts the record's link to its points, <c>urn://pddms/production-values/{id}/timeseries</c>, in
    /// <paramref name="document"/>'s <c>data.DDMSDatasets</c> in place of any other historian link. Returns false when the
    /// document rendered a historian link that differs from it.
    /// </summary>
    public bool CarryLink(JsonObject? stored, JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return (Text(document["id"]) ?? Text(stored?["id"])) is not { } id || EnsureLink(document, id);
    }

    /// <summary>
    /// Undoes what unfinished deliveries made beside the record: nothing a call can take back. The historian has no delete for
    /// points (section 6.3), so each request's accepted series versions are kept and named; the record given back its earlier
    /// version, or removed, no longer describes them.
    /// </summary>
    public Task<IReadOnlyList<UndoResult>> UndoAsync(UndoWork work, DdmsRecordPaths paths, IReadOnlyList<UndoItem> items, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<UndoResult>>(items
            .Select(i => i.Artifact.Role == ArtifactRoles.Points
                ? UndoResult.Kept(i, $"the historian has no delete for points, so the series versions it accepted stay ({i.Artifact.Locator ?? "versions not recorded"})")
                : UndoResult.Kept(i, "the historian shape makes nothing of this kind beside a record"))
            .ToList());

    /// <summary>The record is a Storage record: its reversible removal and its read are storage's.</summary>
    public RecordSide RecordSide(DdmsRecordPaths paths) => new()
    {
        Remove = _routing.StorageDeletePath is { } delete
            ? (id, token) => RecordWriter.DeleteAsync(_client, new RemovalPaths(delete, delete, delete), id, RemovalScope.Record, token)
            : null,
        RemoveRefusal = "the record is a Storage record, and this flow does not say where the storage service is; give the DDMS its root under target.ddms, the flow's endpoint being the OSDU platform root",
        Read = (id, token) => RecordWriter.ReadAsync(_client, RouteOf(paths).RecordPath, id, token),
    };

    /// <summary>The series versions a request was accepted under, as an artifact's locator names them: <c>series=version</c>, a few at most.</summary>
    private static string VersionsNamed(IReadOnlyDictionary<string, long> versions)
        => DdmsShapeValues.Bounded(versions.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.Key}={v.Value}")).ToList(), 10);

    /// <summary>The link a ProductionValues record carries to its points (section 2).</summary>
    public static string Link(string recordId) => $"{LinkPrefix}production-values/{recordId}/timeseries";

    /// <summary>
    /// Why the historian cannot take <paramref name="document"/>, or null: a ProductionValues kind of version 2.0.0 or later,
    /// the reporting entity, and one entry per series with a unique <c>DDMSDatasetID</c> and a <c>ParameterKindID</c>
    /// (sections 2 and 9.1).
    /// </summary>
    internal static string? RecordProblem(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var kind = Text(document["kind"]);
        var parts = kind?.Split(':') ?? [];
        if (kind is null || parts.Length != 4 || !string.Equals(parts[2], DdmsCatalog.ProductionValues, StringComparison.OrdinalIgnoreCase)
            || KindVersion().Match(parts[3]) is not { Success: true } semantic)
        {
            return $"the kind '{kind}' is not a ProductionValues kind (<authority>:<source>:{DdmsCatalog.ProductionValues}:<major.minor.patch>), the records the historian keeps points for";
        }

        if (!int.TryParse(semantic.Groups["major"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major) || major < 2)
        {
            return $"the kind '{kind}' is ProductionValues {parts[3]}; the historian keeps the points of ProductionValues 2.0.0 and later, whose series data.ProductionMetricValues lists";
        }

        if (document["data"] is not JsonObject data)
        {
            return "the record has no data object; the historian reads the record's series from data.ProductionMetricValues";
        }

        if (Text(data["ReportingEntityID"]) is not { } entity)
        {
            return "data.ReportingEntityID is not given; a ProductionValues record names the master data it reports for (a field, reservoir, well, wellbore and the like)";
        }

        if (!entity.Contains(":master-data--", StringComparison.Ordinal))
        {
            return $"data.ReportingEntityID '{entity}' is not a master data record; a ProductionValues record reports for a field, reservoir, reservoir segment, well, wellbore, isolated interval or wellbore opening";
        }

        if (data[DdmsDatasets] is { } links && links is not JsonArray)
        {
            return "data.DDMSDatasets is not a list; the record's link to its points goes in it";
        }

        return SeriesProblem(data, out _);
    }

    /// <summary>Carries the link to the record's points into <paramref name="document"/>; false when it held a different historian link.</summary>
    internal static bool EnsureLink(JsonObject document, string recordId)
    {
        var link = Link(recordId);
        if (document["data"] is not JsonObject data)
        {
            if (document["data"] is not null)
            {
                return true;
            }

            data = new JsonObject();
            document["data"] = data;
        }

        if (data[DdmsDatasets] is not JsonArray list)
        {
            if (data[DdmsDatasets] is not null)
            {
                return true;
            }

            list = new JsonArray();
            data[DdmsDatasets] = list;
        }

        var agreed = true;
        var present = false;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (Text(list[i]) is not { } entry || !entry.StartsWith(LinkPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(entry, link, StringComparison.Ordinal) && !present)
            {
                present = true;
                continue;
            }

            agreed &= string.Equals(entry, link, StringComparison.Ordinal);
            list.RemoveAt(i);
        }

        if (!present)
        {
            list.Add(link);
        }

        return agreed;
    }

    /// <summary>
    /// What the ingestion service answered for each series of <paramref name="request"/>, in the request's order: the record's
    /// item of the answer (<c>BatchResponse</c>), its series items matched by position, or by id where the count differs and
    /// every item names its series (section 3.3).
    /// </summary>
    internal static IReadOnlyList<SeriesAnswer> ParseIngestion(HttpFetchResult result, Uri url, string recordId, TimeSeriesRequest request)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);
        var number = request.Number.ToString(CultureInfo.InvariantCulture);
        var body = OsduHttpClient.ParseJson(result, url);
        var record = RecordItem(body, recordId)
            ?? throw new DeliveryException($"{url.AbsolutePath} answered request {number} of the points of {recordId} without a result for the record: {Preview(result.BodyText)}");
        if (!record.TryGetProperty("timeseries", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            var (code, message) = Result(record);
            var refused = $"the ingestion service refused request {number} of the points of {recordId} as a whole ({code?.ToString(CultureInfo.InvariantCulture) ?? "no code"}): {message}";
            throw code is 400 or 401 or 403 or 404 ? new RecordHeldException(refused) : new DeliveryException(refused);
        }

        var items = list.EnumerateArray().ToList();
        var ids = items.Select(i => Text(i, "timeseriesId")).ToList();
        var sent = request.Series;
        List<JsonElement?> aligned;
        if (items.Count == sent.Count && ids.Select((id, i) => id is null || string.Equals(id, sent[i].Id, StringComparison.Ordinal)).All(same => same))
        {
            aligned = items.Select(i => (JsonElement?)i).ToList();
        }
        else if (ids.All(id => id is not null))
        {
            aligned = sent.Select(s => items.Where(i => string.Equals(Text(i, "timeseriesId"), s.Id, StringComparison.Ordinal)).Select(i => (JsonElement?)i).FirstOrDefault()).ToList();
        }
        else
        {
            throw new DeliveryException(string.Create(
                CultureInfo.InvariantCulture,
                $"{url.AbsolutePath} answered {items.Count} result(s) for the {sent.Count} series of request {number} of {recordId}, not every one naming its series, so they cannot be matched to what was sent"));
        }

        return sent.Select((s, i) => aligned[i] is { } item
                ? Answer(s.Id, item)
                : new SeriesAnswer(s.Id, 0, null, null, null, null, "the answer holds no result for the series"))
            .ToList();
    }

    /// <summary>
    /// What a read of one series version answered (<c>BatchResponse</c> with one series item, or an <c>AppError</c>): the
    /// series' code, how many points it served, and what the service said.
    /// </summary>
    internal static (int Code, long Points, string Message) ParseRead(HttpFetchResult result, string recordId, string seriesId)
    {
        ArgumentNullException.ThrowIfNull(result);
        var status = (int)result.Status;
        JsonElement body;
        try
        {
            using var document = JsonDocument.Parse(result.Body);
            body = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (status, 0, Preview(result.BodyText));
        }

        if (RecordItem(body, recordId) is not { } record)
        {
            return (status, 0, Preview(result.BodyText));
        }

        if (!record.TryGetProperty("timeseries", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            var (code, message) = Result(record);
            return (code ?? status, 0, message);
        }

        var items = list.EnumerateArray().ToList();
        var item = items.Where(i => string.Equals(Text(i, "timeseriesId"), seriesId, StringComparison.Ordinal)).Select(i => (JsonElement?)i).FirstOrDefault()
            ?? items.Where(i => Text(i, "timeseriesId") is null).Select(i => (JsonElement?)i).FirstOrDefault();
        if (item is not { } found)
        {
            return (status is >= 200 and < 300 ? 404 : status, 0, $"the answer holds no result for {seriesId}");
        }

        var (seriesCode, seriesMessage) = Result(found);
        var points = Long(found, "pointsCount") ?? Long(found, "count")
            ?? (found.TryGetProperty("points", out var served) && served.ValueKind == JsonValueKind.Array ? served.GetArrayLength() : 0);
        return (seriesCode ?? status, points, seriesMessage);
    }

    /// <summary>Why the record's series cannot be read, or null, with the series by id.</summary>
    private static string? SeriesProblem(JsonObject data, out Dictionary<string, SeriesDefinition> series)
    {
        series = new Dictionary<string, SeriesDefinition>(StringComparer.Ordinal);
        if (data[ProductionMetricValues] is not JsonArray values || values.Count == 0)
        {
            return "data.ProductionMetricValues lists no series; a ProductionValues record defines the series the historian keeps points for there, one entry each";
        }

        var position = 0;
        foreach (var node in values)
        {
            position++;
            var at = string.Create(CultureInfo.InvariantCulture, $"entry {position} of data.ProductionMetricValues");
            if (node is not JsonObject entry)
            {
                return $"{at} is not an object";
            }

            if (Text(entry["DDMSDatasetID"]) is not { } id)
            {
                return $"{at} has no DDMSDatasetID, the id the historian keeps its points under";
            }

            if (Text(entry["ParameterKindID"]) is not { } kind)
            {
                return $"{id} ({at}) has no ParameterKindID, which fixes the kind of its values";
            }

            if (!series.TryAdd(id, new SeriesDefinition(id, kind, SeriesDefinition.KindOf(kind))))
            {
                return $"data.ProductionMetricValues lists the DDMSDatasetID {id} more than once; the historian keeps one series under each";
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the record through Storage, with the data keys the flow preserves and the link to its points; a response
    /// without the version is settled by reading the record back.
    /// </summary>
    private async Task<(long? Version, int Status)> WriteRecordAsync(DeliveryWork work, DdmsRoute route, CancellationToken ct)
    {
        var document = (JsonObject)work.Document.DeepClone();
        if (_options.PreserveDataKeys.Count > 0 && work.ExistingVersion is not null)
        {
            await RecordWriter.PreserveAsync(_client, route.RecordPath, work.TargetId, document, _options.PreserveDataKeys, StepLinks.Of(work), ct).ConfigureAwait(false);
        }

        EnsureLink(document, work.TargetId);
        var (version, status) = await RecordWriter.SendAsync(_client, _options with { VersionPath = StorageVersionPath }, route.RecordsPath, "PUT", document, ct).ConfigureAwait(false);
        if (version is null)
        {
            var stored = await RecordWriter.VerifyAsync(_client, route.RecordPath, work.TargetId, null, ct).ConfigureAwait(false);
            version = stored.ObservedVersion;
        }

        return (version, status);
    }

    /// <summary>
    /// Sends the points request by request as the files are read. A request an earlier try recorded with the same body is
    /// not sent again; its versions are taken from the record's steps.
    /// </summary>
    private async Task<List<AcceptedRequest>> SendPointsAsync(DeliveryWork work, DdmsRoute route, TimeSeriesPlan plan, IPayloadSource payload, DeliverySteps steps, CancellationToken ct)
    {
        using var builder = new TimeSeriesRequestBuilder(plan.Limit);
        var accepted = new List<AcceptedRequest>(plan.Requests);
        var url = _client.Url(route.DataPath!, work.TargetId);

        async ValueTask HandleAsync(TimeSeriesRequest request)
        {
            var step = PointsStepPrefix + request.Number.ToString(CultureInfo.InvariantCulture);
            if (work.Completed(step) is { } done
                && done.TryGetValue(HashValue, out var hash) && string.Equals(hash, request.Hash, StringComparison.Ordinal)
                && Versions(done, request) is { } known)
            {
                steps.Resumed(step, done);
                accepted.Add(new AcceptedRequest(step, request.Series, known, done, resumed: true)
                {
                    Settled = done.TryGetValue(SettledValue, out var settled) && settled == "true",
                });
                return;
            }

            var started = steps.Now;
            int status;
            IReadOnlyList<SeriesAnswer> answers;
            try
            {
                (status, answers) = await PostAsync(work.TargetId, url, request, ct).ConfigureAwait(false);
            }
            catch (PartlyAcceptedException partly)
            {
                // The service stored the series it accepted as new versions, which no call takes back: they are the unit's, and
                // named, before the try fails.
                var kept = partly.Accepted.ToDictionary(a => a.Id, a => a.Version!.Value, StringComparer.Ordinal);
                var partial = new Dictionary<string, string>(StringComparer.Ordinal) { [HashValue] = request.Hash, ["accepted"] = VersionsNamed(kept) };
                await work.ReportStepAsync(
                    step + "-partial",
                    partial,
                    [TargetArtifact.Created($"points:{request.Number.ToString(CultureInfo.InvariantCulture)}:partial:{Guid.NewGuid():N}", ArtifactRoles.Points, work.TargetId, locator: VersionsNamed(kept))],
                    ct).ConfigureAwait(false);
                throw partly.Failure;
            }
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HashValue] = request.Hash,
                ["bytes"] = request.Body.Length.ToString(CultureInfo.InvariantCulture),
                ["points"] = request.Points.ToString(CultureInfo.InvariantCulture),
                ["series"] = request.Series.Count.ToString(CultureInfo.InvariantCulture),
            };
            var versions = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var answer in answers)
            {
                versions[answer.Id] = answer.Version!.Value;
                values[answer.Id + ".version"] = answer.Version.Value.ToString(CultureInfo.InvariantCulture);
                values[answer.Id + ".points"] = (answer.Points ?? request.Series.First(s => s.Id == answer.Id).Points).ToString(CultureInfo.InvariantCulture);
            }

            steps.Add(step, started, status, values);
            await work.ReportStepAsync(
                step,
                values,
                [TargetArtifact.Created("points:" + request.Number.ToString(CultureInfo.InvariantCulture), ArtifactRoles.Points, work.TargetId, locator: VersionsNamed(versions))],
                ct).ConfigureAwait(false);
            accepted.Add(new AcceptedRequest(step, request.Series, versions, values, resumed: false));
        }

        await TimeSeriesPoints.ScanAsync(
            payload,
            plan.Files,
            plan.Series,
            (id, timestamp, value) => builder.Add(id, timestamp, value) is { } finished ? HandleAsync(finished) : ValueTask.CompletedTask,
            ct).ConfigureAwait(false);
        if (builder.Finish() is { } last)
        {
            await HandleAsync(last).ConfigureAwait(false);
        }

        if (accepted.Count != plan.Requests)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"The points of {work.TargetId} were checked as {plan.Requests} request(s) and sent as {accepted.Count}; the points files changed between the two reads."));
        }

        _logger.LogInformation(
            "The ingestion service accepted {Points} point(s) of {TargetId} in {Requests} request(s) ({Resumed} accepted on an earlier try).",
            plan.Points, work.TargetId, accepted.Count, accepted.Count(a => a.Resumed));
        return accepted;
    }

    /// <summary>
    /// Sends one request and returns the status with every series' answer, each one accepted under a version and with
    /// every point it was sent. A series the service refused holds the record, or leaves it for the next try when the
    /// refusal is the service's own.
    /// </summary>
    private async Task<(int Status, IReadOnlyList<SeriesAnswer> Answers)> PostAsync(string recordId, Uri url, TimeSeriesRequest request, CancellationToken ct)
    {
        var number = request.Number.ToString(CultureInfo.InvariantCulture);
        HttpFetchResult result;
        try
        {
            result = await _client.SendJsonBytesAsync(HttpMethod.Post, url, request.Body, null, ct, idempotent: false, headers: TraceHeaders(), bareJsonType: true).ConfigureAwait(false);
        }
        catch (OsduStatusException ex) when (ex.StatusCode is 400 or 403 or 404)
        {
            throw new RecordHeldException(ex.StatusCode switch
            {
                404 => $"the ingestion service did not find {recordId} for request {number} of its points (HTTP 404); the record was written through Storage first, so the service "
                    + $"reads another partition or cannot see the record: {ex.Message}",
                403 => $"the ingestion service refused request {number} of the points of {recordId} (HTTP 403); the flow's identity needs service.pddms.editor or service.pddms.admin, "
                    + $"and read access to the record: {ex.Message}",
                _ => $"the ingestion service refused request {number} of the points of {recordId} (HTTP 400): {ex.Message}",
            }, ex);
        }

        var answers = ParseIngestion(result, url, recordId, request);
        var refused = new List<string>();
        var held = false;
        foreach (var answer in answers)
        {
            var sent = request.Series.First(s => s.Id == answer.Id);
            if (!answer.Accepted)
            {
                held |= answer.Code is 400 or 401 or 403 or 404;
                refused.Add(string.Create(CultureInfo.InvariantCulture, $"{answer.Id} ({(answer.Code == 0 ? "no code" : answer.Code.ToString(CultureInfo.InvariantCulture))}: {answer.Message})"));
            }
            else if (answer.Version is null)
            {
                held = true;
                refused.Add($"{answer.Id} (accepted without the version it is stored under, so it cannot be read back)");
            }
            else if (answer.Points is { } count && count != sent.Points)
            {
                held = true;
                refused.Add(string.Create(CultureInfo.InvariantCulture, $"{answer.Id} (accepted {count} of its {sent.Points} points as version {answer.Version})"));
            }
        }

        if (refused.Count == 0)
        {
            return ((int)result.Status, answers);
        }

        var acceptedSeries = answers.Where(a => a.Accepted && a.Version is not null).ToList();
        var taken = acceptedSeries.Select(a => string.Create(CultureInfo.InvariantCulture, $"{a.Id} as version {a.Version}")).ToList();
        var message = $"the ingestion service did not take every series of request {number} of the points of {recordId}: {string.Join("; ", refused)}"
            + (taken.Count == 0 ? string.Empty : $". It accepted {string.Join(", ", taken)}, which a later delivery sends again as new versions");
        Exception failure = held ? new RecordHeldException(message) : new DeliveryException(message);
        throw acceptedSeries.Count == 0 ? failure : new PartlyAcceptedException(acceptedSeries, failure);
    }

    /// <summary>A request the ingestion service took part of: the series it accepted, and the failure the try ends with.</summary>
    private sealed class PartlyAcceptedException(IReadOnlyList<SeriesAnswer> accepted, Exception failure) : DeliveryException(failure.Message, failure)
    {
        public IReadOnlyList<SeriesAnswer> Accepted { get; } = accepted;

        public Exception Failure { get; } = failure;
    }

    /// <summary>
    /// Reads every accepted version back until the query service serves its points, for at most the DDMS's
    /// <c>settleSeconds</c>. Returns how the points were confirmed, for the attempt's detail.
    /// </summary>
    private async Task<string> SettleAsync(DeliveryWork work, DdmsRoute route, List<AcceptedRequest> accepted, DeliverySteps steps, CancellationToken ct)
    {
        var settings = route.Service.TimeSeries ?? Defaults;
        if (settings.SettleSeconds <= 0)
        {
            return "not read back (settleSeconds is 0)";
        }

        var open = accepted.Where(a => !a.Settled).ToList();
        if (open.Count == 0)
        {
            return "read back through the query service on an earlier try";
        }

        var started = steps.Now;
        var deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(settings.SettleSeconds);
        var interval = TimeSpan.FromSeconds(Math.Clamp(settings.PollSeconds, 1, TimeSeriesSettings.MaxPollSeconds));
        var reads = 0;
        var rounds = 0;
        while (true)
        {
            rounds++;
            string? waiting = null;
            foreach (var request in open.Where(r => !r.Settled))
            {
                var served = true;
                foreach (var series in request.Series)
                {
                    while (request.Next(series.Id) < series.Windows.Count)
                    {
                        reads++;
                        var window = series.Windows[request.Next(series.Id)];
                        if (await ReadWindowAsync(route, work.TargetId, series.Id, request.Versions[series.Id], window, ct).ConfigureAwait(false) is { } pending)
                        {
                            waiting ??= pending;
                            served = false;
                            break;
                        }

                        request.Advance(series.Id);
                    }
                }

                if (served)
                {
                    request.Settled = true;
                    var values = new Dictionary<string, string>(request.Values, StringComparer.Ordinal) { [SettledValue] = "true" };
                    await work.ReportStepAsync(request.Step, values, ct).ConfigureAwait(false);
                }
            }

            var unsettled = open.Count(r => !r.Settled);
            if (unsettled == 0)
            {
                break;
            }

            if (_time.GetUtcNow() + interval > deadline)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"the ingestion service accepted every point of {work.TargetId}, and after {settings.SettleSeconds}s the query service does not serve {unsettled} of the {open.Count} request(s) "
                    + $"yet ({waiting}). The next try reads them back again without sending the points again"));
            }

            _logger.LogDebug("The query service does not serve {Unsettled} request(s) of {TargetId} yet ({Waiting}); reading again in {Seconds}s.", unsettled, work.TargetId, waiting, interval.TotalSeconds);
            await Task.Delay(interval, _time, ct).ConfigureAwait(false);
        }

        steps.Add("settle", started, null, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["requests"] = open.Count.ToString(CultureInfo.InvariantCulture),
            ["reads"] = reads.ToString(CultureInfo.InvariantCulture),
            ["rounds"] = rounds.ToString(CultureInfo.InvariantCulture),
        });
        return "read back through the query service";
    }

    /// <summary>Reads one range of one accepted version; null when the query service serves every point of it, else what it answered.</summary>
    private async Task<string?> ReadWindowAsync(DdmsRoute route, string recordId, string seriesId, long version, SettleWindow window, CancellationToken ct)
    {
        var versionText = version.ToString(CultureInfo.InvariantCulture);
        var url = _client.Url(route.SeriesVersionPath!, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = recordId,
            ["timeseriesId"] = seriesId,
            ["version"] = versionText,
        });
        url = OsduHttpClient.WithQuery(OsduHttpClient.WithQuery(url, "start", window.Start.ToString(CultureInfo.InvariantCulture)), "end", (window.End + 1).ToString(CultureInfo.InvariantCulture));
        HttpFetchResult result;
        try
        {
            result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct, headers: TraceHeaders()).ConfigureAwait(false);
        }
        catch (OsduStatusException ex) when (ex.StatusCode == 403)
        {
            throw new RecordHeldException(
                $"the ingestion service accepted the points of {recordId}, and the query service refused to read {seriesId} version {versionText} back (HTTP 403). Grant the flow's identity "
                + $"service.pddms.viewer, or set settleSeconds to 0 to deliver without reading back; a delivery after that sends the points again as new versions: {ex.Message}",
                ex);
        }

        var (code, points, message) = ParseRead(result, recordId, seriesId);
        if (code is >= 200 and < 300 && points >= window.Points)
        {
            return null;
        }

        return code is >= 200 and < 300
            ? string.Create(CultureInfo.InvariantCulture, $"{seriesId} version {versionText} serves {points} of the {window.Points} point(s) from {window.Start} to {window.End}")
            : string.Create(CultureInfo.InvariantCulture, $"{seriesId} version {versionText} answers {code}: {message}");
    }

    /// <summary>What the target state keeps of the points: the requests and points of this delivery, and per series its versions and range.</summary>
    private static void Summarize(TimeSeriesPlan plan, List<AcceptedRequest> accepted, string settled, Dictionary<string, string> all)
    {
        all["timeSeries.requests"] = accepted.Count.ToString(CultureInfo.InvariantCulture);
        all["timeSeries.points"] = plan.Points.ToString(CultureInfo.InvariantCulture);
        all["timeSeries.settled"] = settled;
        foreach (var group in accepted.SelectMany(a => a.Series.Select(s => (Series: s, Version: a.Versions[s.Id]))).GroupBy(x => x.Series.Id, StringComparer.Ordinal))
        {
            var versions = group.Select(x => x.Version.ToString(CultureInfo.InvariantCulture)).ToList();
            var prefix = "timeSeries." + group.Key;
            all[prefix + ".versions"] = versions.Count <= MaxVersionsKept
                ? string.Join(",", versions)
                : string.Join(",", versions.Take(MaxVersionsKept)) + string.Create(CultureInfo.InvariantCulture, $" and {versions.Count - MaxVersionsKept} more");
            all[prefix + ".points"] = group.Sum(x => (long)x.Series.Points).ToString(CultureInfo.InvariantCulture);
            all[prefix + ".start"] = group.Min(x => x.Series.Start).ToString(CultureInfo.InvariantCulture);
            all[prefix + ".end"] = group.Max(x => x.Series.End).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The versions a completed points step recorded for every series of <paramref name="request"/>, or null when one is missing.</summary>
    private static Dictionary<string, long>? Versions(IReadOnlyDictionary<string, string> done, TimeSeriesRequest request)
    {
        var versions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var series in request.Series)
        {
            if (!done.TryGetValue(series.Id + ".version", out var text) || RecordWriter.ParseVersion(text) is not { } version)
            {
                return null;
            }

            versions[series.Id] = version;
        }

        return versions;
    }

    private long LimitOf(DdmsRoute route)
    {
        var limit = (route.Service.TimeSeries ?? Defaults).MaxRequestBytesPerRequest;
        if (_requestBodyCeiling > 0)
        {
            limit = Math.Min(limit, _requestBodyCeiling);
        }

        return limit >= SmallestLimit
            ? limit
            : throw new RecordHeldException(string.Create(
                CultureInfo.InvariantCulture,
                $"the target's declared request body ceiling of {_requestBodyCeiling} bytes (reliability.maxRequestBodyBytes) leaves no room for points; the historian's requests need at least {SmallestLimit}"));
    }

    private static IReadOnlyDictionary<string, string>? TraceHeaders()
        => OsduCorrelation.Current is { } id
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [TraceHeader] = id }
            : null;

    private static DdmsRoute RouteOf(DdmsRecordPaths paths)
        => paths.Route ?? throw new InvalidOperationException($"{paths.EntityType} records have no historian collection to go to.");

    /// <summary>The item of a <c>BatchResponse</c> for <paramref name="recordId"/>, its only item, or an error body itself.</summary>
    private static JsonElement? RecordItem(JsonElement body, string recordId)
    {
        if (body.ValueKind == JsonValueKind.Object)
        {
            return body;
        }

        if (body.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var items = body.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).ToList();
        return items.Where(i => string.Equals(Text(i, "recordId"), recordId, StringComparison.Ordinal)).Select(i => (JsonElement?)i).FirstOrDefault()
            ?? (items.Count == 1 ? items[0] : null);
    }

    private static SeriesAnswer Answer(string id, JsonElement item)
    {
        var (code, message) = Result(item);
        return new SeriesAnswer(id, code ?? 0, Long(item, "version"), Long(item, "pointsCount") ?? Long(item, "count"), Long(item, "start"), Long(item, "end"), message);
    }

    /// <summary>The code and message of an item's <c>result</c> (<c>{"code","reason","message"}</c>).</summary>
    private static (int? Code, string Message) Result(JsonElement item)
    {
        if (!item.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            return (null, "no result");
        }

        int? code = result.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
        var message = Text(result, "message") ?? Text(result, "reason") ?? string.Empty;
        return (code, HeaderRedaction.RedactMessage(Preview(message)));
    }

    private static long? Long(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;

    private static string? Text(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string Preview(string text) => text.Length <= 300 ? text : text[..300] + "...";

    [GeneratedRegex(@"^(?<major>[0-9]+)\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KindVersion();

    /// <summary>A request the ingestion service accepted: its step, its series, the version each is stored under, and how far its read back got.</summary>
    private sealed class AcceptedRequest(string step, IReadOnlyList<RequestSeries> series, IReadOnlyDictionary<string, long> versions, IReadOnlyDictionary<string, string> values, bool resumed)
    {
        private readonly Dictionary<string, int> _next = new(StringComparer.Ordinal);

        public string Step { get; } = step;

        public IReadOnlyList<RequestSeries> Series { get; } = series;

        public IReadOnlyDictionary<string, long> Versions { get; } = versions;

        public IReadOnlyDictionary<string, string> Values { get; } = values;

        public bool Resumed { get; } = resumed;

        public bool Settled { get; set; }

        /// <summary>The first range of <paramref name="series"/> not read back yet.</summary>
        public int Next(string series) => _next.TryGetValue(series, out var next) ? next : 0;

        public void Advance(string series) => _next[series] = Next(series) + 1;
    }
}
