using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Storage;

namespace SqlFlow.Delivery.Engine.Protocols.Ddms;

/// <summary>What the Wellbore DDMS shape checked of a record's bulk data before its first request.</summary>
/// <param name="Chunks">The bulk data's chunk files; empty when no bulk data is sent.</param>
/// <param name="Shapes">The chunks' shapes read from their footers, when they were read.</param>
/// <param name="Session">True when the bulk data goes through a session.</param>
internal sealed record WellboreBulkPlan(IReadOnlyList<PayloadFile> Chunks, IReadOnlyList<ParquetShape>? Shapes, bool Session);

/// <summary>
/// The Wellbore DDMS v3 shape (osdu/docs/reference/flow/ddms.md, "The Wellbore DDMS (wellboreDdmsV3)";
/// osdu/specs/wellbore-ddms/INTEGRATION.md): each record through the collection serving its entity type, then, on a
/// bulk collection, its bulk data streamed past the service. A single chunk goes in one request; more than the
/// threshold opens an overwrite session, streams each chunk in order and commits. Chunks are never buffered: each
/// request re-opens its blob so the retry stack can resend it, and carries its length. Each step reports what the
/// service returned (the record version, the session id, the chunk count); a retry after a payload failure resumes past
/// the metadata step it already completed. Every rule the Wellbore DDMS applies that can be checked from the record and
/// its chunks' footers is checked before the first request
/// (<see cref="WellboreDdmsRules"/>), and a metadata update carries the bulk link the DDMS manages
/// (<see cref="WellboreDdmsBulkLink"/>). A record written before its bulk data, and every session, are artifacts of the
/// record's unit of work (osdu/docs/reference/concepts/artifacts-and-undo.md, Wellbore DDMS): a delivery that does not
/// complete has its sessions abandoned and the record removed, or given back the version it replaced.
/// </summary>
internal sealed class WellboreDdmsV3Shape(DdmsShapeContext context) : IDdmsShape
{
    /// <summary>The step that records the bulk data landed, so a try that failed after it reads the record back without sending it again.</summary>
    public const string BulkStep = "bulk";

    /// <summary>How often a commit answered "committing" is asked again, and how many times, before the try gives up on it.</summary>
    private static readonly TimeSpan CommittingPoll = TimeSpan.FromSeconds(2);

    private const int CommittingPolls = 30;

    private readonly OsduHttpClient _client = context.Client;
    private readonly ProtocolOptions _options = context.Options;
    private readonly DdmsRouting _routing = context.Routing;
    private readonly ILogger _logger = context.Logger;
    private readonly long _requestBodyCeiling = context.RequestBodyCeiling;
    private readonly TimeProvider _time = context.Time;

    public async Task<object?> PrepareAsync(DeliveryWork work, DdmsRecordPaths paths, CancellationToken ct)
    {
        var writesMetadata = work.DeliverMetadata && work.Completed(OsduDdmsProtocol.MetadataStep) is null;
        if (work.DeliverPayload && BulkProblem(work.TargetId, paths) is { } nowhere)
        {
            throw new RecordHeldException(nowhere);
        }

        // The record rules first: they cost nothing, and a record the DDMS would refuse is held before its chunks are read.
        if ((writesMetadata || work.DeliverPayload) && WellboreDdmsRules.RecordProblem(paths.EntityType, work.Document, work.DeliverPayload) is { } problem)
        {
            throw new RecordHeldException(problem);
        }

        IReadOnlyList<PayloadFile> chunks = [];
        IReadOnlyList<ParquetShape>? shapes = null;
        var session = false;
        if (work.DeliverPayload)
        {
            // Check the payload against the ceilings, its columns against the record, and a session's chunks against each
            // other, before anything is sent, so a chunk the target cannot take whole holds the record instead of failing
            // or losing rows mid-session after the metadata write (osdu/docs/reference/flow/ddms.md, The bulk ceilings).
            var payload = work.Payload ?? throw new RecordHeldException("the record needs a payload but none is attached");
            chunks = await payload.ListChunksAsync(ct).ConfigureAwait(false);
            if (chunks.Count == 0)
            {
                throw new RecordHeldException("no payload chunk files were found for the record");
            }

            session = chunks.Count > 1 || _options.SessionThresholdChunks < 1;
            shapes = await PreflightAsync(work.Document, paths, payload, chunks, session, ct).ConfigureAwait(false);
        }

        return new WellboreBulkPlan(chunks, shapes, session);
    }

    public async Task<DeliveryOutcome> SendAsync(DeliveryWork work, DdmsRecordPaths paths, object? prepared, CancellationToken ct)
    {
        var (chunks, shapes, session) = prepared as WellboreBulkPlan
            ?? throw new InvalidOperationException("The Wellbore DDMS shape was handed work another shape prepared.");
        var steps = new DeliverySteps(_time);
        var version = work.ExistingVersion;
        var metadataDelivered = false;

        if (work.DeliverMetadata)
        {
            if (work.Completed(OsduDdmsProtocol.MetadataStep) is { } done)
            {
                // The previous try wrote the record and failed later: reuse its version, do not write it again.
                steps.Resumed(OsduDdmsProtocol.MetadataStep, done);
                version = done.TryGetValue("version", out var text) ? RecordWriter.ParseVersion(text) ?? version : version;
            }
            else
            {
                var started = steps.Now;
                if (work.DeliverPayload)
                {
                    // Bulk data follows the record: the write is named before it goes, so a write that lands without its answer
                    // is still taken back when the delivery does not complete.
                    await work.ReportStepAsync(
                        OsduDdmsProtocol.MetadataStep + "-intent",
                        new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId },
                        [TargetArtifact.RecordWritten(work.TargetId, null, work.ExistingVersion) with { Status = ArtifactStatus.Intent }],
                        ct).ConfigureAwait(false);
                }

                var (written, status) = await WriteRecordAsync(work, paths, ct).ConfigureAwait(false);
                version = written ?? version;
                var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
                if (version is { } v)
                {
                    returned["version"] = v.ToString(CultureInfo.InvariantCulture);
                }

                steps.Add(OsduDdmsProtocol.MetadataStep, started, status, returned);

                // Bulk data follows the record, so the record is the unit's until the bulk data lands: a delivery that does
                // not get there removes it, or gives back the version it replaced.
                IReadOnlyList<TargetArtifact> recordItself = work.DeliverPayload ? [TargetArtifact.RecordWritten(work.TargetId, version, work.ExistingVersion)] : [];
                await work.ReportStepAsync(OsduDdmsProtocol.MetadataStep, returned, recordItself, ct).ConfigureAwait(false);
            }

            metadataDelivered = true;
        }

        var chunksSent = 0;
        var payloadDelivered = false;
        if (work.DeliverPayload)
        {
            var payload = work.Payload!;
            var started = steps.Now;
            string? sessionId = null;

            if (work.Completed(BulkStep) is { } landedBefore)
            {
                // An earlier try's bulk data landed and the try failed after it (reading the record back): it is not sent again.
                steps.Resumed(BulkStep, landedBefore);
                chunksSent = landedBefore.TryGetValue("chunks", out var count) && int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : chunks.Count;
                sessionId = landedBefore.TryGetValue("sessionId", out var landedSession) ? landedSession : null;
            }
            else
            {
                // One chunk may go straight to the bulk endpoint, and only one: that request carries "the entire bulk
                // which will replace as latest version any previous bulk" (openapi wellbore_ddms,
                // POST /ddms/v3/{collection}/{record_id}/data). Posting several chunks to it in turn would leave the record
                // holding the last one and report every one of them as delivered. Aggregating chunks is what a session
                // is for, so anything past the first uses one.
                if (!session)
                {
                    var chunk = chunks[0];
                    var url = _client.Url(paths.Data!, work.TargetId);
                    await _client.SendStreamAsync(HttpMethod.Post, url, () => OsduDdmsProtocol.OpenSync(payload, chunk), _options.PayloadContentType, chunk.Size > 0 ? chunk.Size : null, ct, idempotent: true).ConfigureAwait(false);
                    chunksSent = 1;
                }
                else
                {
                    (chunksSent, sessionId) = await SendSessionAsync(work, paths, payload, chunks, version, ct).ConfigureAwait(false);
                }

                var bulkLanded = new Dictionary<string, string>(StringComparer.Ordinal) { ["chunks"] = chunksSent.ToString(CultureInfo.InvariantCulture) };
                if (sessionId is not null)
                {
                    bulkLanded["sessionId"] = sessionId;
                }

                await work.ReportStepAsync(BulkStep, bulkLanded, ct).ConfigureAwait(false);
            }

            var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["chunks"] = chunksSent.ToString(CultureInfo.InvariantCulture) };
            if (sessionId is not null)
            {
                returned["sessionId"] = sessionId;

                // The labels read before the session opened keep its chunks from colliding where the footers can tell
                // them; the committed bulk is read back as well, because a collision the footers cannot show loses rows
                // just as silently.
                if (shapes is not null && await CommittedRowsAsync(work.TargetId, paths, sessionId, shapes, ct).ConfigureAwait(false) is { } rows)
                {
                    returned["rows"] = rows.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Writing the bulk creates a new version of the record ("It creates a new version", openapi wellbore_ddms,
            // POST /ddms/v3/{collection}/{record_id}/data; a session commit creates one too), and the direct write
            // documents no response body to read it from. The ledger has to hold the version OSDU now serves: the
            // metadata write's version makes every verify report the record drifted, and opens the next session from a
            // version that is no longer the latest. So the record is read back.
            var landed = await RecordWriter.VerifyAsync(_client, paths.Record, work.TargetId, null, ct).ConfigureAwait(false);
            var landedVersion = landed.ObservedVersion
                ?? throw new DeliveryException(
                    $"The bulk data for {work.TargetId} was written, but the record's version could not be read back from {paths.Record}: {landed.Detail}.");
            version = landedVersion;
            returned["version"] = landedVersion.ToString(CultureInfo.InvariantCulture);

            steps.Add(OsduDdmsProtocol.PayloadStep, started, null, returned);
            payloadDelivered = true;
        }

        var all = new Dictionary<string, string>(steps.Returned, StringComparer.Ordinal);
        all["recordId"] = work.TargetId;
        if (version is { } finalVersion)
        {
            all["version"] = finalVersion.ToString(CultureInfo.InvariantCulture);
        }

        return new DeliveryOutcome
        {
            MetadataDelivered = metadataDelivered,
            PayloadDelivered = payloadDelivered,
            TargetVersion = version,
            ChunksSent = chunksSent,
            Detail = chunksSent > 0 ? $"{chunksSent} chunk(s)" : null,
            Returned = all,
            Steps = steps.Steps,
        };
    }

    public Task<VerifyResult> VerifyAsync(DdmsRecordPaths paths, string targetId, long? expectedVersion, CancellationToken ct)
        => RecordWriter.VerifyAsync(_client, paths.Record, targetId, expectedVersion, ct);

    public Task<JsonObject?> ReadAsync(DdmsRecordPaths paths, string targetId, CancellationToken ct)
        => RecordWriter.ReadAsync(_client, paths.Record, targetId, ct);

    /// <summary>
    /// Wellbore DDMS semantics (openapi wellbore_ddms, DELETE /ddms/v3/{collection}/{record_id}): a logical deletion of
    /// the record by default, a physical one with <c>?purge=true</c> on the four bulk collections, which also removes
    /// the bulk data; no recursive delete of owned entities; 204. A record-only collection's DELETE is logical only, so
    /// <see cref="RemovalScope.Everything"/> goes to the storage service's purge for its records, which is the purge the
    /// DDMS itself calls for a bulk record.
    ///
    /// The DDMS has no operation on a record's versions (its only versions route is a GET listing), and versions
    /// belong to the storage service for every kind of record, so <see cref="RemovalScope.History"/> goes to
    /// storage's version purge and leaves the DDMS record itself untouched, which is exactly what that scope
    /// promises. Storage is a different service from a DDMS endpoint, though, and the DDMS paths carry no
    /// <c>/api/&lt;service&gt;/</c> prefix, so the storage default resolves only when the endpoint is the platform root.
    /// Otherwise the flow says where storage lives (<c>purgeVersionsPath</c>, <c>purgePath</c>), usually as an
    /// absolute URL, and without it the scope is refused, because guessing would send a delete somewhere nobody chose.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(DdmsRecordPaths paths, string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        if (scope == RemovalScope.History)
        {
            if (_routing.HistoryPath is not { } purgeVersions)
            {
                throw new RecordHeldException(
                    "a DDMS has no version purge, and this flow does not say where the storage service is. "
                    + "Declare target.protocolOptions.ddmsRoot (or a root for the DDMS under target.ddms) when the endpoint is the OSDU platform root, or "
                    + "purgeVersionsPath (an absolute URL such as https://<host>/api/storage/v2/records/{id}/versions) "
                    + "when it is the DDMS itself, before purging a record's history.");
            }

            return await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options) with { PurgeVersions = purgeVersions }, targetId, scope, ct).ConfigureAwait(false);
        }

        if (scope == RemovalScope.Everything && paths.Route is { Collection.Bulk: false } route)
        {
            if (_routing.StoragePurgePath is not { } purge)
            {
                throw new RecordHeldException(
                    $"{targetId} is in {route.Describe()}, whose DELETE is logical only, and this flow does not say where the storage service is to purge it. "
                    + "Declare target.protocolOptions.ddmsRoot (or a root for the DDMS under target.ddms) when the endpoint is the OSDU platform root, or "
                    + "purgePath (an absolute URL such as https://<host>/api/storage/v2/records/{id}) when it is the DDMS itself.");
            }

            return await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options) with { Purge = purge }, targetId, scope, ct).ConfigureAwait(false);
        }

        var url = _client.Url(paths.Delete, targetId);
        if (scope == RemovalScope.Everything)
        {
            url = OsduHttpClient.WithQuery(url, "purge", "true");
        }

        var result = await _client.SendJsonAsync(HttpMethod.Delete, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return new DeleteOutcome(false, true, "record not found in OSDU");
        }

        return new DeleteOutcome(true, false, scope == RemovalScope.Everything
            ? "purged from OSDU (the record, every version and its bulk data)"
            : "removed from OSDU (reversible)");
    }

    public bool CarryLink(JsonObject? stored, JsonObject document) => WellboreDdmsBulkLink.Carry(stored, document);

    /// <summary>
    /// Undoes the sessions unfinished deliveries opened: each abandoned (<c>PATCH {state: abandon}</c>), a session already
    /// committed or abandoned left as it is; a session whose create was never answered is found among the record's open
    /// sessions and abandoned. What a session aggregated stays in the DDMS's store, which no call removes.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoAsync(UndoWork work, DdmsRecordPaths paths, IReadOnlyList<UndoItem> items, CancellationToken ct)
    {
        var results = new List<UndoResult>(items.Count);
        foreach (var item in items)
        {
            if (item.Artifact.Role != ArtifactRoles.Session)
            {
                results.Add(UndoResult.Kept(item, "the Wellbore DDMS shape makes nothing of this kind beside a record"));
                continue;
            }

            if (paths.Session is null || paths.Sessions is null)
            {
                results.Add(UndoResult.Kept(item, "the flow names no session path for the record's collection, so its session cannot be reached"));
                continue;
            }

            try
            {
                results.Add(item.Artifact.TargetId is { } session
                    ? await AbandonForUndoAsync(work.TargetId, paths, session, item, ct).ConfigureAwait(false)
                    : await AbandonOpenAsync(work.TargetId, paths, item, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ArtifactUndo.Answerable(ex, ct))
            {
                results.Add(UndoResult.Failed(item, ArtifactUndo.Redact(ex)));
            }
        }

        return results;
    }

    /// <summary>The record's reversible removal is the DDMS's logical delete; its read says when OSDU created it.</summary>
    public RecordSide RecordSide(DdmsRecordPaths paths) => new()
    {
        Remove = (id, token) => DeleteAsync(paths, id, RemovalScope.Record, null, token),
        Read = (id, token) => RecordWriter.ReadAsync(_client, paths.Record, id, token),
    };

    private async Task<UndoResult> AbandonForUndoAsync(string targetId, DdmsRecordPaths paths, string sessionId, UndoItem item, CancellationToken ct)
    {
        // A state that cannot be read throws, and the item is answered failed for a later undo: a session is gone only when the
        // DDMS says so.
        var state = await ReadSessionStateAsync(targetId, paths, sessionId, ct).ConfigureAwait(false);
        if (state is null)
        {
            return UndoResult.Gone(item, $"session {sessionId} is no longer known to the DDMS");
        }

        if (Settling(state))
        {
            return UndoResult.Failed(item, SettlingNote(sessionId, state));
        }

        if (state is not "open")
        {
            return UndoResult.Gone(item, $"session {sessionId} is {state}, so there is nothing to abandon");
        }

        var url = _client.Url(paths.Session!, targetId, sessionId);
        await _client.SendJsonAsync(HttpMethod.Patch, url, new JsonObject { ["state"] = "abandon" }, null, ct).ConfigureAwait(false);
        return UndoResult.Removed(item, $"session {sessionId} abandoned");
    }

    /// <summary>A session whose create was never answered: the record's open sessions are listed, and each abandoned.</summary>
    private async Task<UndoResult> AbandonOpenAsync(string targetId, DdmsRecordPaths paths, UndoItem item, CancellationToken ct)
    {
        var url = _client.Url(paths.Sessions!, targetId);
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404 || result.Body.Length == 0)
        {
            return UndoResult.Gone(item, $"the DDMS lists no session of {targetId}");
        }

        var open = new List<string>();
        var settling = new List<(string Id, string State)>();
        var root = OsduHttpClient.ParseJson(result, url);
        var listed = root.ValueKind == System.Text.Json.JsonValueKind.Array ? root : root.TryGetProperty("sessions", out var inner) ? inner : default;
        if (listed.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var session in listed.EnumerateArray())
            {
                if (session.ValueKind == System.Text.Json.JsonValueKind.Object
                    && session.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } sessionId
                    && session.TryGetProperty("state", out var state) && state.GetString()?.ToLowerInvariant() is { } listedState)
                {
                    if (listedState == "open")
                    {
                        open.Add(sessionId);
                    }
                    else if (Settling(listedState))
                    {
                        settling.Add((sessionId, listedState));
                    }
                }
            }
        }

        if (settling.Count > 0)
        {
            return UndoResult.Failed(item, SettlingNote(string.Join(", ", settling.Select(s => s.Id)), string.Join(", ", settling.Select(s => s.State).Distinct(StringComparer.Ordinal))));
        }

        foreach (var sessionId in open)
        {
            await _client.SendJsonAsync(HttpMethod.Patch, _client.Url(paths.Session!, targetId, sessionId), new JsonObject { ["state"] = "abandon" }, null, ct).ConfigureAwait(false);
        }

        return open.Count == 0
            ? UndoResult.Gone(item, $"no session of {targetId} is open: the create never landed, or its session ended")
            : UndoResult.Removed(item, $"open session(s) {string.Join(", ", open)} of {targetId} abandoned");
    }

    /// <summary>
    /// Writes the record through its collection. A record of a bulk collection carries the bulk link the DDMS holds for
    /// it: the latest version is read first when the ledger knows the record was delivered, and when it does not, a
    /// refused write reads it once and is sent again if the DDMS holds a link after all (a delivery whose outcome was
    /// lost). The OSDU-owned data keys the flow preserves (<see cref="ProtocolOptions.PreserveDataKeys"/>) come from the
    /// same read.
    /// </summary>
    private async Task<(long? Version, int Status)> WriteRecordAsync(DeliveryWork work, DdmsRecordPaths paths, CancellationToken ct)
    {
        var document = (JsonObject)work.Document.DeepClone();
        var preserve = _options.PreserveDataKeys.Count > 0;
        var method = _options.RecordMethod ?? "POST";
        var read = work.ExistingVersion is not null && (preserve || paths.Bulk);
        JsonObject? stored = null;
        if (read)
        {
            stored = await RecordWriter.ReadAsync(_client, paths.Record, work.TargetId, ct).ConfigureAwait(false);
            if (stored is not null && preserve)
            {
                RecordWriter.Preserve(stored, document, _options.PreserveDataKeys, StepLinks.Of(work));
            }
        }

        if (paths.Bulk)
        {
            Link(stored, document, work.TargetId);
        }

        try
        {
            return await RecordWriter.SendAsync(_client, _options, paths.Records, method, document, ct).ConfigureAwait(false);
        }
        catch (OsduStatusException ex) when (ex.StatusCode == 400 && paths.Bulk && !read)
        {
            stored = await RecordWriter.ReadAsync(_client, paths.Record, work.TargetId, ct).ConfigureAwait(false);
            if (WellboreDdmsBulkLink.Of(stored) is null)
            {
                throw;
            }

            _logger.LogWarning(
                "The DDMS holds {TargetId} with a bulk link the ledger did not know of and refused the write without it; writing it again with the link: {Message}",
                work.TargetId, HeaderRedaction.RedactMessage(ex.Message));
            Link(stored, document, work.TargetId);
            return await RecordWriter.SendAsync(_client, _options, paths.Records, method, document, ct).ConfigureAwait(false);
        }
    }

    private void Link(JsonObject? stored, JsonObject document, string targetId)
    {
        if (!WellboreDdmsBulkLink.Carry(stored, document))
        {
            _logger.LogWarning(
                "The record {TargetId} is rendered with its own data.ExtensionProperties.wdms.bulkURI, which the DDMS manages; the link the DDMS holds is sent instead.",
                targetId);
        }
    }

    /// <summary>Why the record's bulk data has nowhere to go, or null when every bulk path is known.</summary>
    private static string? BulkProblem(string targetId, DdmsRecordPaths paths)
    {
        if (paths.Data is not null && paths.Sessions is not null && paths.SessionData is not null && paths.Session is not null)
        {
            return null;
        }

        return paths.Route is { Collection.Bulk: false } route
            ? $"{targetId} goes to {route.Describe()}, which holds records alone and takes no bulk data; deliver the record without a payload"
            : $"{targetId} has bulk data and the flow names no DDMS path for it (dataPath, sessionPath, sessionDataPath, sessionCommitPath)";
    }

    /// <summary>
    /// What a payload is checked for before its first request. Two ceilings bound a chunk. The estate's request body
    /// size is declared as <c>reliability.maxRequestBodyBytes</c> and can be raised where it is configured. The
    /// wellbore DDMS bulk shape (<see cref="WellboreDdmsBulkLimits"/>) cannot: it is the frame the service
    /// materialises, so a chunk can be small enough to send and still be too large to accept. The columns of all the
    /// chunks are checked against the record as the collection's DDMS checks them (<see cref="WellboreDdmsRules"/>). A
    /// session's chunks are also checked against each other, because the session aggregates them by row label and two
    /// chunks that give the same labels to different rows lose rows while the commit still succeeds
    /// (<see cref="WellboreDdmsSessionChunks"/>). Shapes are read from the parquet footers, never from the chunks'
    /// contents, when the payload is parquet and a ceiling or a column rule is in force or a session opens. They are
    /// returned so the committed bulk can be checked against them, and are null when they were not read.
    /// </summary>
    private async Task<IReadOnlyList<ParquetShape>?> PreflightAsync(JsonObject document, DdmsRecordPaths paths, IPayloadSource payload, IReadOnlyList<PayloadFile> chunks, bool session, CancellationToken ct)
    {
        var parquet = _options.PayloadContentType.Contains("parquet", StringComparison.OrdinalIgnoreCase);
        var readsShape = parquet && (session || _options.MaxChunkValues > 0 || _options.MaxChunkColumns > 0 || paths.Columns != DdmsBulkColumns.Unchecked);
        var measured = readsShape ? new List<WellboreDdmsSessionChunks.Chunk>(chunks.Count) : null;

        foreach (var chunk in chunks)
        {
            if (_requestBodyCeiling > 0 && chunk.Size > _requestBodyCeiling)
            {
                throw new RecordHeldException(
                    $"payload chunk {chunk.Index.ToString(CultureInfo.InvariantCulture)} is {chunk.Size.ToString(CultureInfo.InvariantCulture)} bytes, above the target's declared request body ceiling of {_requestBodyCeiling.ToString(CultureInfo.InvariantCulture)} bytes (reliability.maxRequestBodyBytes); re-chunk in prepare");
            }

            if (measured is null)
            {
                continue;
            }

            var shape = await ParquetPayloads.ShapeAsync(payload, chunk, "its shape cannot be checked against the target's bulk ceilings", ct).ConfigureAwait(false);
            if (WellboreDdmsBulkLimits.Exceeded(chunk.Index, chunk.Path, shape.Rows, shape.Columns, _options.MaxChunkValues, _options.MaxChunkColumns) is { } held)
            {
                throw new RecordHeldException(held);
            }

            measured.Add(new WellboreDdmsSessionChunks.Chunk(chunk.Index, chunk.Path, shape));
        }

        if (measured is null)
        {
            return null;
        }

        var labels = measured.SelectMany(m => m.Shape.ColumnNames).Distinct(StringComparer.Ordinal).ToList();
        if (WellboreDdmsRules.ColumnsProblem(paths.Columns, document, labels) is { } columns)
        {
            throw new RecordHeldException(columns);
        }

        if (session && WellboreDdmsSessionChunks.Conflict(measured) is { } conflict)
        {
            throw new RecordHeldException(conflict);
        }

        if (session && WellboreDdmsRules.SessionReferenceProblem(paths.Columns, document, labels) is { } reference)
        {
            throw new RecordHeldException(reference);
        }

        return measured.Select(m => m.Shape).ToList();
    }

    /// <summary>
    /// Reads the bulk a session committed back (openapi wellbore_ddms, GET /ddms/v3/{collection}/{record_id}/data with
    /// <c>describe=true</c>, which answers the number of rows and the column names) and holds the record when the bulk
    /// lacks rows or columns its chunks carried. Returns the rows the bulk holds, or null when the target cannot describe
    /// it (a facade without the describe query, for instance): the delivery then stands unchecked, with a warning,
    /// rather than failing bulk data that did land.
    /// </summary>
    private async Task<long?> CommittedRowsAsync(string targetId, DdmsRecordPaths paths, string sessionId, IReadOnlyList<ParquetShape> shapes, CancellationToken ct)
    {
        var url = OsduHttpClient.WithQuery(_client.Url(paths.Data!, targetId), "describe", "true");
        long rows;
        List<string>? columns = null;
        try
        {
            var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 400, 404, 405, 422, 501 }, ct).ConfigureAwait(false);
            if ((int)result.Status is < 200 or >= 300 || result.Body.Length == 0)
            {
                LogUnchecked(targetId, sessionId, "HTTP " + ((int)result.Status).ToString(CultureInfo.InvariantCulture));
                return null;
            }

            var described = OsduHttpClient.ParseJson(result, url);
            if (described.ValueKind != System.Text.Json.JsonValueKind.Object
                || !described.TryGetProperty("numberOfRows", out var count)
                || !count.TryGetInt64(out rows))
            {
                LogUnchecked(targetId, sessionId, "the description carries no numberOfRows");
                return null;
            }

            if (described.TryGetProperty("columns", out var names) && names.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                columns = names.EnumerateArray()
                    .Where(name => name.ValueKind == System.Text.Json.JsonValueKind.String)
                    .Select(name => name.GetString()!)
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException)
        {
            LogUnchecked(targetId, sessionId, HeaderRedaction.RedactMessage(ex.Message));
            return null;
        }

        var shortfall = WellboreDdmsSessionChunks.Shortfall(
            targetId,
            sessionId,
            WellboreDdmsSessionChunks.ExpectedRows(shapes),
            columns is null ? [] : WellboreDdmsSessionChunks.ExpectedColumns(shapes),
            rows,
            (IReadOnlyCollection<string>?)columns ?? []);
        if (shortfall is not null)
        {
            throw new RecordHeldException(shortfall);
        }

        return rows;
    }

    private void LogUnchecked(string targetId, string sessionId, string reason)
        => _logger.LogWarning(
            "The bulk session {SessionId} committed for {TargetId} could not be described, so its rows were not checked against its chunks: {Reason}",
            sessionId, targetId, reason);

    private async Task<(int Sent, string SessionId)> SendSessionAsync(DeliveryWork work, DdmsRecordPaths paths, IPayloadSource payload, IReadOnlyList<PayloadFile> chunks, long? version, CancellationToken ct)
    {
        var createUrl = _client.Url(paths.Sessions!, work.TargetId);
        var createBody = new JsonObject
        {
            ["mode"] = "overwrite",
            ["fromVersion"] = version ?? 0,
            ["timeToLive"] = 1440,
        };

        // The DDMS mints the session's id, and the create is never resent: an intent goes first, so a session whose answer is
        // lost is found among the record's open sessions and abandoned when the delivery does not complete.
        var slot = "session:" + Guid.NewGuid().ToString("N");
        await work.ReportStepAsync("session", new Dictionary<string, string>(StringComparer.Ordinal) { ["state"] = "opening" }, [TargetArtifact.Intent(slot, ArtifactRoles.Session, work.TargetId)], ct).ConfigureAwait(false);
        var created = await _client.SendJsonAsync(HttpMethod.Post, createUrl, createBody, null, ct, idempotent: false).ConfigureAwait(false);
        var sessionId = JsonPathReader.SelectValue(OsduHttpClient.ParseJson(created, createUrl), "id")
            ?? throw new DeliveryException($"{createUrl.AbsolutePath} did not return a session id.");
        await work.ReportStepAsync("session", new Dictionary<string, string>(StringComparer.Ordinal) { ["sessionId"] = sessionId }, [TargetArtifact.Created(slot, ArtifactRoles.Session, sessionId, locator: work.TargetId)], ct).ConfigureAwait(false);

        var sent = 0;
        try
        {
            foreach (var chunk in chunks)
            {
                var url = _client.Url(paths.SessionData!, work.TargetId, sessionId);

                // A chunk sent twice into a session lands twice in the committed bulk, so a chunk whose outcome is
                // unclear fails the session (which is abandoned) rather than being resent.
                await _client.SendStreamAsync(HttpMethod.Post, url, () => OsduDdmsProtocol.OpenSync(payload, chunk), _options.PayloadContentType, chunk.Size > 0 ? chunk.Size : null, ct, idempotent: false).ConfigureAwait(false);
                sent++;
            }

            await CommitAsync(work.TargetId, paths, sessionId, ct).ConfigureAwait(false);
            return (sent, sessionId);
        }
        catch (Exception)
        {
            // Abandoned whatever stopped the session, a cancelled run included: a session left open holds nothing back, but
            // it is the DDMS's to keep until it expires. An abandon that worked ends the session's artifact; one that did not
            // leaves it for the unit's undo.
            if (await AbandonAsync(work.TargetId, paths, sessionId).ConfigureAwait(false))
            {
                await work.ReportStepAsync(
                    "session",
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["sessionId"] = sessionId, ["state"] = "abandoned" },
                    [TargetArtifact.Created(slot, ArtifactRoles.Session, sessionId, locator: work.TargetId) with { Status = ArtifactStatus.Removed, Note = "abandoned when its delivery failed" }],
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Commits the session, and settles a commit the service answers 409 or 412 to by asking what state the session
    /// is actually in (openapi wellbore_ddms, GET /ddms/v3/{collection}/{record_id}/sessions/{session_id}).
    ///
    /// The commit is a PATCH and is never resent blind, so its outcome can be unclear: the connection went, a
    /// gateway answered 5xx after the service had acted, or an intermediary resent it and the second copy met a
    /// session that is no longer open (409 or 412). Treating any of those as a failure would fail a record whose
    /// bulk data did land, and send the whole payload again on the next try. The session's own state says which
    /// happened, so it is read rather than guessed: committed or committing is the commit that worked, anything
    /// else is a real failure.
    /// </summary>
    private async Task CommitAsync(string targetId, DdmsRecordPaths paths, string sessionId, CancellationToken ct)
    {
        var commitUrl = _client.Url(paths.Session!, targetId, sessionId);
        try
        {
            await _client.SendJsonAsync(HttpMethod.Patch, commitUrl, new JsonObject { ["state"] = "commit" }, null, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is OsduStatusException { StatusCode: 409 or 412 or >= 500 } || ex is DeliveryException { InnerException: HttpRequestException or IOException or TaskCanceledException })
        {
            var state = await SessionStateAsync(targetId, paths, sessionId, ct).ConfigureAwait(false);

            // "committing" is a commit under way, not one that worked: the DDMS is still writing the merged bulk and the record
            // version that names it, and either can still fail and send the session back to open. It is asked again until it
            // settles, and a commit still under way when the polls run out is not taken as landed.
            for (var poll = 0; state == "committing" && poll < CommittingPolls; poll++)
            {
                await Task.Delay(CommittingPoll, _time, ct).ConfigureAwait(false);
                state = await SessionStateAsync(targetId, paths, sessionId, ct).ConfigureAwait(false);
            }

            if (state != "committed")
            {
                throw new DeliveryException(
                    $"session {sessionId} for {targetId} could not be committed and is {state ?? "in an unknown state"}; the payload did not land.", ex);
            }

            _logger.LogInformation(
                "Session {SessionId} for {TargetId} was already committed when the commit was resent; the payload landed on the first commit.",
                sessionId, targetId);
        }
    }

    /// <summary>The session's state as the service reports it, or null when it cannot be read.</summary>
    private async Task<string?> SessionStateAsync(string targetId, DdmsRecordPaths paths, string sessionId, CancellationToken ct)
    {
        try
        {
            return await ReadSessionStateAsync(targetId, paths, sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException)
        {
            _logger.LogWarning("Could not read session {SessionId} for {TargetId}: {Message}", sessionId, targetId, HeaderRedaction.RedactMessage(ex.Message));
            return null;
        }
    }

    /// <summary>A session's state as the DDMS answers it, lower case; null only when the DDMS no longer knows the session (404). Any other failure throws.</summary>
    private async Task<string?> ReadSessionStateAsync(string targetId, DdmsRecordPaths paths, string sessionId, CancellationToken ct)
    {
        var url = _client.Url(paths.Session!, targetId, sessionId);
        var result = await _client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404 || result.Body.Length == 0)
        {
            return null;
        }

        return JsonPathReader.SelectValue(OsduHttpClient.ParseJson(result, url), "state")?.ToLowerInvariant();
    }

    /// <summary>A session the DDMS is still settling: a commit or an abandon in progress, which can still write the unit's bulk data.</summary>
    private static bool Settling(string state) => state is "committing" or "abandoning";

    private static string SettlingNote(string sessions, string state)
        => $"session {sessions} is still {state}, and a commit that lands writes the unfinished delivery's bulk data under the record; the undo waits until it settles";

    /// <summary>Abandons a session after its delivery failed; true when the DDMS took the abandon.</summary>
    private async Task<bool> AbandonAsync(string targetId, DdmsRecordPaths paths, string sessionId)
    {
        try
        {
            var url = _client.Url(paths.Session!, targetId, sessionId);
            await _client.SendJsonAsync(HttpMethod.Patch, url, new JsonObject { ["state"] = "abandon" }, null, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or OperationCanceledException)
        {
            _logger.LogWarning("Could not abandon session {SessionId} for {TargetId}: {Message}; the delivery's undo abandons it.", sessionId, targetId, HeaderRedaction.RedactMessage(ex.Message));
            return false;
        }
    }
}

/// <summary>Reading the shape of a parquet payload file, which the ddms route's shapes check before they send one.</summary>
internal static class ParquetPayloads
{
    /// <summary>
    /// Reads one file's shape from its footer. A file that does not parse holds the record, since the service would refuse
    /// it too; <paramref name="consequence"/> says what the failure prevents.
    /// </summary>
    public static async Task<ParquetShape> ShapeAsync(IPayloadSource payload, PayloadFile file, string consequence, CancellationToken ct)
    {
        var opened = await payload.OpenAsync(file, ct).ConfigureAwait(false);
        Stream seekable;
        try
        {
            seekable = await ParquetFiles.EnsureSeekableAsync(opened, ct).ConfigureAwait(false);
        }
        catch
        {
            await opened.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        ParquetShape shape;
        await using (seekable.ConfigureAwait(false))
        {
            try
            {
                shape = await ParquetFiles.ReadShapeAsync(seekable, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new RecordHeldException(
                    $"payload chunk {file.Index.ToString(CultureInfo.InvariantCulture)} ({Path.GetFileName(file.Path)}) is declared as parquet but its footer could not be read, so {consequence}: {HeaderRedaction.RedactMessage(ex.Message)}",
                    ex);
            }
        }

        if (shape.PandasDefect is { } defect)
        {
            // A bulk service reads a chunk as a dataframe. A file a dataframe reader raises on is refused as malformed
            // whatever its rows hold, so the record is held here, where the reason can be read, rather than sent.
            throw new RecordHeldException(
                $"payload chunk {file.Index.ToString(CultureInfo.InvariantCulture)} ({Path.GetFileName(file.Path)}) "
                + $"carries pandas metadata a dataframe reader cannot read, so the service would refuse the data as malformed: {defect}");
        }

        return shape;
    }
}
