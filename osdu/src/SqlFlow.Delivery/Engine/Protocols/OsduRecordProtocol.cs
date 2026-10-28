using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The plain-record protocol (osdu/docs/reference/flow/routes.md, storage): JSON documents upserted by client-supplied
/// id through the storage service's array endpoint, up to <see cref="ProtocolOptions.BatchSize"/> records per request.
/// The write response's <c>recordIdVersions</c> entries carry <c>id:version</c> per record and <c>skippedRecordIds</c>
/// the ones the service found unchanged. A batch the service rejects as a whole (a 4xx) is retried record by record,
/// so one bad document holds itself and not its neighbours.
/// </summary>
public sealed class OsduRecordProtocol : IDeliveryProtocol
{
    public const string DefaultRecordPath = "/api/storage/v2/records";
    public const string DefaultVerifyPath = "/api/storage/v2/records/{id}";
    public const string DefaultProbePath = "/api/storage/v2/info";
    public const string DefaultDeletePath = "/api/storage/v2/records/{id}:delete";
    public const string DefaultPurgePath = "/api/storage/v2/records/{id}";
    public const string DefaultPurgeVersionsPath = "/api/storage/v2/records/{id}/versions";
    public const string DefaultBulkDeletePath = "/api/storage/v2/records/delete";
    public const string DefaultVerifyBatchPath = "/api/storage/v2/query/records";

    /// <summary>
    /// Record ids this route sends in one bulk soft delete request. The storage service's specification sets no limit on
    /// the list (openapi storage v2, <c>POST /records/delete</c>); the bound is this route's own, so one request carries a
    /// bounded body and a chunk the service does not delete whole falls back to a bounded number of single deletes.
    /// </summary>
    public const int MaxBulkDelete = 500;

    /// <summary>Record ids one batched read takes (openapi storage v2, MultiRecordIds caps the list at 100).</summary>
    public const int MaxVerifyBatch = 100;

    public const string RecordsStep = "records";

    /// <summary>The step that brings a record OSDU no longer served back from its soft delete before it is written.</summary>
    public const string RestoreStep = "restore";

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly TimeProvider _time;

    public OsduRecordProtocol(OsduHttpClient client, ProtocolOptions options, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    public DeliveryProtocol Kind => DeliveryProtocol.Storage;

    public int MaxBatch => Math.Clamp(_options.BatchSize, 1, ProtocolOptions.MaxBatchSize);

    int IDeliveryProtocol.MaxVerifyBatch => MaxVerifyBatch;

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var outcome = (await WriteBatchAsync([work], ct).ConfigureAwait(false))[0];
        return outcome.Failure is { } failure ? throw failure : outcome;
    }

    public async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        if (works.Count <= 1)
        {
            return await WriteBatchAsync(works, ct).ConfigureAwait(false);
        }

        var outcomes = new DeliveryOutcome[works.Count];
        foreach (var chunk in works.Select((w, i) => (Work: w, Index: i)).Chunk(MaxBatch))
        {
            var results = await WriteBatchAsync(chunk.Select(c => c.Work).ToList(), ct).ConfigureAwait(false);
            for (var i = 0; i < chunk.Length; i++)
            {
                outcomes[chunk[i].Index] = results[i];
            }
        }

        return outcomes;
    }

    /// <summary>
    /// One request for the whole batch; a rejected batch of more than one record falls back to one request per record. A
    /// record OSDU no longer held when it was last looked for (<see cref="DeliveryWork.RestoreFirst"/>) is brought back from
    /// its soft delete first, so the keys the write keeps of it are read from it rather than lost.
    /// </summary>
    private async Task<IReadOnlyList<DeliveryOutcome>> WriteBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct)
    {
        var outcomes = new DeliveryOutcome[works.Count];
        var toWrite = new List<(int Index, DeliveryWork Work, JsonObject Document, IReadOnlyList<string> Preserved)>();
        var restores = new Dictionary<int, (RecordRestore Outcome, int Status, DateTime Started)>();
        for (var i = 0; i < works.Count; i++)
        {
            var work = works[i];
            if (!work.DeliverMetadata)
            {
                outcomes[i] = new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, TargetVersion = work.ExistingVersion };
                continue;
            }

            try
            {
                var document = (JsonObject)work.Document.DeepClone();
                RecordRestore? restored = null;
                if (work.RestoreFirst)
                {
                    var restoreStarted = _time.GetUtcNow().UtcDateTime;
                    var (outcome, status) = await RecordWriter.RestoreAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, work.TargetId, ct).ConfigureAwait(false);
                    restores[i] = (outcome, status, restoreStarted);
                    restored = outcome;
                }

                // A record OSDU holds no trace of has nothing to keep: it is written again as it renders.
                var preserved = OwnedContent.PreservedKeys(_options, document);
                if (preserved.Count > 0 && work.ExistingVersion is not null && restored != RecordRestore.NotHeld)
                {
                    await RecordWriter.PreserveAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, work.TargetId, document, preserved, StepLinks.Of(work), ct).ConfigureAwait(false);
                }

                toWrite.Add((i, work, document, preserved));
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                outcomes[i] = DeliveryOutcome.Failed(ex);
            }
        }

        if (toWrite.Count == 0)
        {
            return outcomes;
        }

        var steps = new DeliverySteps(_time);
        var started = steps.Now;
        var url = _client.Url(_options.RecordPath ?? DefaultRecordPath);
        if (_options.SkipDuplicates)
        {
            url = OsduHttpClient.WithQuery(url, "skipdupes", "true");
        }

        var method = new HttpMethod((_options.RecordMethod ?? "PUT").ToUpperInvariant());
        HttpFetchResult result;
        try
        {
            result = await _client.SendJsonAsync(method, url, new JsonArray(toWrite.Select(t => (JsonNode)t.Document).ToArray()), null, ct, idempotent: true).ConfigureAwait(false);
        }
        catch (OsduStatusException ex) when (toWrite.Count > 1 && ex.StatusCode is >= 400 and < 500 and not (401 or 408 or 425 or 429))
        {
            // The service refused the array as a whole; find out which records it refuses by sending them alone.
            foreach (var (index, work, _, _) in toWrite)
            {
                var alone = await WriteBatchAsync([work], ct).ConfigureAwait(false);
                outcomes[index] = alone[0];
            }

            return outcomes;
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
        {
            steps.Add(RecordsStep, started, (ex as OsduStatusException)?.StatusCode, null, ex.Message);
            foreach (var (index, _, _, _) in toWrite)
            {
                outcomes[index] = DeliveryOutcome.Failed(ex, steps.Steps);
            }

            return outcomes;
        }

        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        string? single = null;
        if (result.Body.Length > 0)
        {
            var root = OsduHttpClient.ParseJson(result, url);
            // A single-record write also honours the flow's versionPath, for services whose response names the
            // record differently from the id that was sent.
            single = toWrite.Count == 1 ? JsonPathReader.SelectValue(root, _options.VersionPath) : null;
            foreach (var idVersion in JsonPathReader.SelectValues(root, "recordIdVersions[*]"))
            {
                var colon = idVersion.LastIndexOf(':');
                if (colon > 0)
                {
                    versions[idVersion[..colon]] = idVersion[(colon + 1)..];
                }
            }

            foreach (var id in JsonPathReader.SelectValues(root, "skippedRecordIds[*]"))
            {
                skipped.Add(id);
            }
        }

        var held = await SettleSkippedAsync(toWrite.Select(t => t.Work.TargetId).Where(id => skipped.Contains(id) && !versions.ContainsKey(id)).ToList(), ct).ConfigureAwait(false);

        // Every record of the request is written at the target now, so their steps are reported together, and the worker
        // writes them to the ledger in one go.
        var reports = new List<Task>(toWrite.Count);
        foreach (var (index, work, document, preserved) in toWrite)
        {
            if (held.Failed.TryGetValue(work.TargetId, out var unserved))
            {
                // Skipped as unchanged, and not served: the record stands at no version OSDU serves, so it is not delivered.
                // Its write step is not reported, so its next try writes it again rather than resuming past the write.
                var refused = new DeliverySteps(_time);
                refused.Add(RecordsStep, started, (int)result.Status, null, unserved.Message);
                outcomes[index] = DeliveryOutcome.Failed(unserved, refused.Steps);
                continue;
            }

            var version = versions.TryGetValue(work.TargetId, out var text)
                ? RecordWriter.ParseVersion(text)
                : held.Versions.TryGetValue(work.TargetId, out var current) ? current : RecordWriter.ParseVersion(single);
            var returned = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
            if (version is { } v)
            {
                returned["version"] = v.ToString(CultureInfo.InvariantCulture);
            }

            OwnedContent.RecordWritten(returned, document, preserved);

            if (skipped.Contains(work.TargetId))
            {
                returned["skipped"] = "true";
            }

            var own = new DeliverySteps(_time);
            string? restoredDetail = null;
            if (restores.TryGetValue(index, out var restore))
            {
                own.Add(RestoreStep, restore.Started, restore.Status, new Dictionary<string, string>(StringComparer.Ordinal) { ["outcome"] = RecordWriter.Describe(restore.Outcome) });
                restoredDetail = restore.Outcome switch
                {
                    RecordRestore.Restored => $"OSDU held {work.TargetId} soft-deleted: it was brought back under its id, then written",
                    RecordRestore.NotHeld => $"OSDU held no trace of {work.TargetId} (purged): it was written again under its id",
                    _ => null,
                };
            }

            if (held.Restored.TryGetValue(work.TargetId, out var brought))
            {
                own.Add(RestoreStep, brought.Started, brought.Status, new Dictionary<string, string>(StringComparer.Ordinal) { ["outcome"] = RecordWriter.Describe(RecordRestore.Restored) });
                restoredDetail = $"OSDU held {work.TargetId} soft-deleted and skipped the unchanged write, which leaves a deleted record deleted: it was brought back under its id";
            }

            own.Add(RecordsStep, started, (int)result.Status, returned);
            reports.Add(work.ReportStepAsync(RecordsStep, returned, ct));
            outcomes[index] = new DeliveryOutcome
            {
                MetadataDelivered = true,
                PayloadDelivered = false,
                TargetVersion = version ?? work.ExistingVersion,
                Returned = returned,
                Steps = own.Steps,
                Detail = skipped.Contains(work.TargetId)
                    ? (restoredDetail is null ? "unchanged at the target" : $"{restoredDetail}; unchanged at the target") + held.Note(work.TargetId)
                    : restoredDetail,
            };
        }

        await Task.WhenAll(reports).ConfigureAwait(false);
        return outcomes;
    }

    /// <summary>
    /// Settles the records a write skipped as unchanged without naming a version (storage leaves skipped ids out of
    /// <c>recordIdVersions</c>, osdu/specs/core/INTEGRATION.md section 2.2). Their versions are read in one batched read: the
    /// version such a record stands at is OSDU's, which another writer (another instance linked to the record) may have
    /// moved since this ledger last knew it. A skipped record storage does not serve is one it holds soft-deleted, which a
    /// skipped write leaves deleted: it is brought back under its id (a merge patch, at the version it had) and its version
    /// read again. One that cannot be brought back fails with why: OSDU serves nothing at its id, so it is not delivered.
    /// A read that fails keeps the version the ledger knew, and the outcome's detail says so.
    /// </summary>
    private async Task<SkippedRecords> SettleSkippedAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return SkippedRecords.None;
        }

        var versions = new Dictionary<string, long>(StringComparer.Ordinal);
        var restored = new Dictionary<string, (DateTime Started, int Status)>(StringComparer.Ordinal);
        var failed = new Dictionary<string, Exception>(StringComparer.Ordinal);
        IReadOnlyList<VerifyResult> read;
        try
        {
            read = await VerifyBatchAsync(ids.Select(id => new VerifyRequest(id, null)).ToList(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
        {
            return new SkippedRecords(versions, restored, failed, HeaderRedaction.RedactMessage(ex.Message));
        }

        var unserved = new List<string>();
        for (var i = 0; i < ids.Count && i < read.Count; i++)
        {
            switch (read[i])
            {
                case { Outcome: VerifyOutcome.Match, ObservedVersion: { } observed }:
                    versions[ids[i]] = observed;
                    break;
                case { Outcome: VerifyOutcome.Missing }:
                    unserved.Add(ids[i]);
                    break;
            }
        }

        foreach (var id in unserved)
        {
            var at = _time.GetUtcNow().UtcDateTime;
            try
            {
                var (outcome, status) = await RecordWriter.RestoreAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, id, ct).ConfigureAwait(false);
                if (outcome == RecordRestore.NotHeld)
                {
                    failed[id] = new DeliveryException(
                        $"OSDU skipped the write of {id} as unchanged, and holds no record of it now (purged meanwhile); nothing stands at the id, so the next try writes it again");
                    continue;
                }

                restored[id] = (at, status);
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                failed[id] = ex;
            }
        }

        if (restored.Count > 0)
        {
            var back = restored.Keys.ToList();
            try
            {
                var again = await VerifyBatchAsync(back.Select(id => new VerifyRequest(id, null)).ToList(), ct).ConfigureAwait(false);
                for (var i = 0; i < back.Count && i < again.Count; i++)
                {
                    if (again[i] is { Outcome: VerifyOutcome.Match, ObservedVersion: { } observed })
                    {
                        versions[back[i]] = observed;
                    }
                }
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                return new SkippedRecords(versions, restored, failed, HeaderRedaction.RedactMessage(ex.Message));
            }
        }

        return new SkippedRecords(versions, restored, failed, null);
    }

    /// <summary>
    /// What became of the records a write skipped: the versions read, the ones brought back from a soft delete (when and with
    /// what status), the ones that could not be (with why), and why a read failed when one did.
    /// </summary>
    private sealed record SkippedRecords(
        IReadOnlyDictionary<string, long> Versions,
        IReadOnlyDictionary<string, (DateTime Started, int Status)> Restored,
        IReadOnlyDictionary<string, Exception> Failed,
        string? ReadFailure)
    {
        public static SkippedRecords None { get; } = new(
            new Dictionary<string, long>(StringComparer.Ordinal),
            new Dictionary<string, (DateTime, int)>(StringComparer.Ordinal),
            new Dictionary<string, Exception>(StringComparer.Ordinal),
            null);

        /// <summary>What the outcome of a skipped record adds when its version was not read: why, and which version stands.</summary>
        public string Note(string targetId)
            => Versions.ContainsKey(targetId)
                ? string.Empty
                : ReadFailure is null
                    ? "; OSDU did not name the version it holds, so the version the ledger knew stands"
                    : $"; the version OSDU holds could not be read ({ReadFailure}), so the version the ledger knew stands";
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => RecordWriter.VerifyAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, targetId, expectedVersion, ct);

    public Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default)
        => RecordWriter.VerifyBatchAsync(_client, _options.VerifyBatchPath ?? DefaultVerifyBatchPath, requests, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.ReadAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, targetId, ct);

    public Task<IReadOnlyList<long>?> VersionsAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.VersionsAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, targetId, ct);

    public Task<JsonObject?> ReadVersionAsync(string targetId, long version, CancellationToken ct = default)
        => RecordWriter.ReadVersionAsync(_client, _options.VerifyPath ?? DefaultVerifyPath, targetId, version, ct);

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => RecordWriter.ProbeAsync(_client, _options.ProbePath ?? DefaultProbePath, ct);

    /// <summary>
    /// Writes records back as they were at an earlier version through the same array write a delivery takes
    /// (<see cref="RecordRestores"/>): the version read back, without its system properties, as the next version.
    /// </summary>
    public Task<IReadOnlyList<RestoreResult>> RestoreBatchAsync(IReadOnlyList<VersionRestore> restores, CancellationToken ct = default)
    {
        _restorer ??= new OsduRecordProtocol(_client, RecordRestores.WriterOptions(_options), _time);
        return RecordRestores.WriteAsync(_restorer, restores, ct);
    }

    /// <summary>The writer a restore goes through: this route's paths and batching, without the keys a delivery carries forward.</summary>
    private OsduRecordProtocol? _restorer;

    private LegalTagValidator? _legal;

    /// <summary>Asks the legal service under this target, when the flow's target reaches it (see <see cref="LegalTagValidator.PathFor"/>).</summary>
    public async Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (LegalTagValidator.PathFor(_options, platformEndpoint: true) is not { } path)
        {
            return null;
        }

        _legal ??= new LegalTagValidator(_client, path, _time);
        return await _legal.InvalidAsync(tags, ct).ConfigureAwait(false);
    }

    public Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
        => RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options), targetId, scope, ct);

    /// <summary>
    /// The reversible scope goes through the storage service's bulk soft delete (openapi storage v2,
    /// <c>POST /records/delete</c>), sent <see cref="MaxBulkDelete"/> ids at a time (this route's chunk size), which answers 204
    /// when it deleted them all or 207 when it did not. A 207 has deleted every id it does not name, so only the ids it names
    /// are asked about again one at a time; a 207 whose answer names none leaves every id of the chunk failed with that answer,
    /// since asking again would find the ids it deleted gone and report them so. A route the service does not serve, and a
    /// list it rejects over something in it, fall back to removing the chunk one record at a time. The two purges have no bulk
    /// endpoint and always go one at a time.
    /// </summary>
    public async Task<IReadOnlyList<RemovalResult>> DeleteBatchAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(removals);
        if (scope != RemovalScope.Record || removals.Count <= 1)
        {
            return await OneByOneAsync(removals, scope, ct).ConfigureAwait(false);
        }

        var results = new List<RemovalResult>(removals.Count);
        foreach (var chunk in removals.Chunk(MaxBulkDelete))
        {
            ct.ThrowIfCancellationRequested();
            results.AddRange(await BulkSoftDeleteAsync(chunk, ct).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<IReadOnlyList<RemovalResult>> BulkSoftDeleteAsync(IReadOnlyList<RecordRemoval> chunk, CancellationToken ct)
    {
        var url = _client.Url(_options.BulkDeletePath ?? DefaultBulkDeletePath);
        var body = new JsonArray(chunk.Select(r => (JsonNode?)JsonValue.Create(r.TargetId)).ToArray());
        HttpFetchResult result;
        try
        {
            result = await _client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 207, 404 }, ct, idempotent: true).ConfigureAwait(false);
        }
        catch (OsduStatusException ex) when (ex.StatusCode is 400 or 405)
        {
            // The service refused the list over something in it (a malformed id) or does not offer the bulk
            // endpoint at all; ask it record by record which ones it objects to.
            return await OneByOneAsync(chunk, RemovalScope.Record, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
        {
            // Not a verdict on the records: the caller is unauthorised, throttled past its retries, or the service
            // is down. Sending the chunk again one id at a time would repeat the same failure hundreds of times, so
            // every record in it carries the one failure that actually happened.
            return chunk.Select(r => new RemovalResult(r, null, ex)).ToList();
        }

        if ((int)result.Status == 207)
        {
            if (NotDeleted(result, chunk) is not { } refused)
            {
                // The answer does not say which ids it kept, and the ids it deleted would answer 404 if asked again, so a second
                // round could not tell them from ids that were gone before. Each id carries what happened; the next removal
                // settles it.
                var unread = new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{HeaderRedaction.DescribeUrl(url)} answered 207: it kept at least one of the {chunk.Count} ids sent and deleted the others, and its answer names none that can be matched to them, so whether this one was deleted is unknown ({Preview(result)}). A removal of it again settles it."));
                return chunk.Select(r => new RemovalResult(r, null, unread)).ToList();
            }

            // The others are deleted (osdu/specs/core/INTEGRATION.md, D2): only the ids the answer names are asked about one
            // at a time, so an id the bulk call removed is said to be removed, not found gone by the second call.
            var asked = chunk.Where(r => refused.Contains(r.TargetId)).ToList();
            var answered = await OneByOneAsync(asked, RemovalScope.Record, ct).ConfigureAwait(false);
            var byId = answered.ToDictionary(r => r.Removal.TargetId, StringComparer.Ordinal);
            return chunk
                .Select(r => byId.TryGetValue(r.TargetId, out var one) ? one : new RemovalResult(r, new DeleteOutcome(true, false, "removed from OSDU (reversible, in bulk)"), null))
                .ToList();
        }

        if ((int)result.Status == 404)
        {
            // The service does not serve the bulk route, so it deleted nothing: every id goes on its own.
            return await OneByOneAsync(chunk, RemovalScope.Record, ct).ConfigureAwait(false);
        }

        return chunk
            .Select(r => new RemovalResult(r, new DeleteOutcome(true, false, "removed from OSDU (reversible, in bulk)"), null))
            .ToList();
    }

    /// <summary>
    /// The ids of <paramref name="chunk"/> a partly refused bulk soft delete names as not deleted. The service answers its 207
    /// with a JSON array of <c>{"notDeletedRecordId", "message"}</c> (osdu/specs/core/INTEGRATION.md, D2); its contract
    /// describes the exception behind it instead (<c>DeleteRecordsException.notDeletedRecords</c>, <c>{key, value}</c> pairs),
    /// and that shape is read too, as is either one sent as a JSON string. Null when the answer names no id, names one in a shape
    /// neither describes, or names one that was not sent: the ids it deleted cannot be told from the ids it kept then.
    /// </summary>
    private static HashSet<string>? NotDeleted(HttpFetchResult result, IReadOnlyList<RecordRemoval> chunk)
    {
        if (result.Body.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.Body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(root.GetString() ?? string.Empty);
                return Named(inner.RootElement, chunk);
            }

            return Named(root, chunk);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HashSet<string>? Named(JsonElement root, IReadOnlyList<RecordRemoval> chunk)
    {
        var (listed, idProperty) = root.ValueKind switch
        {
            JsonValueKind.Array => (root, "notDeletedRecordId"),
            JsonValueKind.Object when root.TryGetProperty("notDeletedRecords", out var pairs) && pairs.ValueKind == JsonValueKind.Array => (pairs, "key"),
            _ => (default, string.Empty),
        };
        if (idProperty.Length == 0)
        {
            return null;
        }

        var sent = chunk.Select(r => r.TargetId).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in listed.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty(idProperty, out var named)
                || named.ValueKind != JsonValueKind.String
                || named.GetString() is not { Length: > 0 } id
                || !sent.Contains(id))
            {
                return null;
            }

            ids.Add(id);
        }

        return ids.Count == 0 ? null : ids;
    }

    /// <summary>The start of an answer's body, redacted, to quote in a message.</summary>
    private static string Preview(HttpFetchResult result)
    {
        var text = Encoding.UTF8.GetString(result.Body);
        return HeaderRedaction.RedactMessage(text.Length <= 200 ? text : text[..200] + "...");
    }

    /// <summary>The shared per-record removal, which is what every fallback here lands on.</summary>
    private Task<IReadOnlyList<RemovalResult>> OneByOneAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct)
        => ((IDeliveryProtocol)this).DeleteOneByOneAsync(removals, scope, ct);
}

/// <summary>The three removal endpoints of one flow's target, defaulted per protocol and overridable per flow.</summary>
internal sealed record RemovalPaths(string Delete, string PurgeVersions, string Purge)
{
    public static RemovalPaths From(ProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new RemovalPaths(
            options.DeletePath ?? OsduRecordProtocol.DefaultDeletePath,
            options.PurgeVersionsPath ?? OsduRecordProtocol.DefaultPurgeVersionsPath,
            options.PurgePath ?? OsduRecordProtocol.DefaultPurgePath);
    }

    public string For(RemovalScope scope) => scope switch
    {
        RemovalScope.Record => Delete,
        RemovalScope.History => PurgeVersions,
        RemovalScope.Everything => Purge,
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
}

/// <summary>The record write, read-back, probe and delete shared by the OSDU protocols.</summary>
/// <summary>What bringing a record back from its soft delete came to (<see cref="RecordWriter.RestoreAsync"/>).</summary>
internal enum RecordRestore
{
    /// <summary>Storage held it soft-deleted and serves it again, at the version it had.</summary>
    Restored,

    /// <summary>Storage holds no record of the id (purged, or never written): the write creates it again under the id.</summary>
    NotHeld,

    /// <summary>Storage serves the record already: nothing changed.</summary>
    Active,
}

internal static class RecordWriter
{
    /// <summary>Writes one prepared record through the array endpoint and returns the version the response reported, with the status.</summary>
    public static async Task<(long? Version, int Status)> SendAsync(OsduHttpClient client, ProtocolOptions options, string recordPath, string method, JsonObject document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(document);
        var url = client.Url(recordPath);
        var body = new JsonArray(document);
        HttpFetchResult result;
        try
        {
            result = await client.SendJsonAsync(new HttpMethod(method.ToUpperInvariant()), url, body, null, ct, idempotent: true).ConfigureAwait(false);
        }
        finally
        {
            // The document is the caller's, and a caller that writes it again gives it to another array.
            body.Remove(document);
        }

        if (result.Body.Length == 0)
        {
            return (null, (int)result.Status);
        }

        var root = OsduHttpClient.ParseJson(result, url);
        var idVersion = JsonPathReader.SelectValue(root, options.VersionPath);
        return (ParseVersion(idVersion), (int)result.Status);
    }

    public static Task<VerifyResult> VerifyAsync(OsduHttpClient client, string verifyPath, string targetId, long? expectedVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        return VerifyAsync(client, client.Url(verifyPath, targetId), expectedVersion, ct);
    }

    /// <summary>
    /// Reads the record at <paramref name="url"/> and compares its <c>version</c> with <paramref name="expectedVersion"/>;
    /// <paramref name="headers"/> go with the read alone (a cache directive).
    /// </summary>
    public static async Task<VerifyResult> VerifyAsync(OsduHttpClient client, Uri url, long? expectedVersion, CancellationToken ct, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(url);
        var result = await client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct, headers: headers).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return new VerifyResult(VerifyOutcome.Missing, null, "record not found");
        }

        var root = OsduHttpClient.ParseJson(result, url);
        var observed = ParseVersion(JsonPathReader.SelectValue(root, "version"));
        if (observed is null)
        {
            return new VerifyResult(VerifyOutcome.Error, null, "record has no version");
        }

        if (expectedVersion is null)
        {
            return new VerifyResult(VerifyOutcome.Match, observed, "no expected version recorded; observed version adopted");
        }

        return observed == expectedVersion
            ? new VerifyResult(VerifyOutcome.Match, observed, null)
            : new VerifyResult(VerifyOutcome.Drifted, observed, $"observed version {observed}, ledger holds {expectedVersion}");
    }

    /// <summary>
    /// Verifies a set of records in batched reads (openapi storage v2, <c>POST /query/records</c>, which takes up
    /// to <see cref="OsduRecordProtocol.MaxVerifyBatch"/> ids per request), rather than one read per record.
    /// Attributes are projected down so the service does not return whole data blocks for a pass that only compares
    /// versions. The records the response returns carry their observed version; a record it does not return is missing
    /// from the target, whether or not it names the id under <c>invalidRecords</c>, which is how storage answers for a
    /// record it does not hold (observed on a live M26 service; the OpenAPI description does not say what the list means).
    ///
    /// Every protocol that writes through the storage service verifies through this, which is what keeps a drift
    /// pass over a large estate to a handful of requests whichever of them delivered the records.
    /// </summary>
    public static async Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(OsduHttpClient client, string batchPath, IReadOnlyList<VerifyRequest> requests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        var results = new VerifyResult[requests.Count];
        var url = client.Url(batchPath);
        foreach (var chunk in requests.Select((r, i) => (Request: r, Index: i)).Chunk(OsduRecordProtocol.MaxVerifyBatch))
        {
            ct.ThrowIfCancellationRequested();
            var body = new JsonObject
            {
                ["records"] = new JsonArray(chunk.Select(c => (JsonNode?)JsonValue.Create(c.Request.TargetId)).ToArray()),

                // A projection the service understands and that matches no data field, so the read carries record
                // headers (id and version among them) and not every record's payload.
                ["attributes"] = new JsonArray(JsonValue.Create("id")),
            };
            var result = await client.SendJsonAsync(HttpMethod.Post, url, body, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
            var versions = new Dictionary<string, long?>(StringComparer.Ordinal);
            var invalid = new HashSet<string>(StringComparer.Ordinal);
            var retry = new HashSet<string>(StringComparer.Ordinal);
            if (result.Body.Length > 0)
            {
                var root = OsduHttpClient.ParseJson(result, url);
                foreach (var record in JsonPathReader.SelectElements(root, "records[*]"))
                {
                    if (record.ValueKind == JsonValueKind.Object
                        && record.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String
                        && id.GetString() is { } text)
                    {
                        versions[text] = ParseVersion(JsonPathReader.SelectValue(record, "version"));
                    }
                }

                foreach (var id in JsonPathReader.SelectValues(root, "invalidRecords[*]"))
                {
                    invalid.Add(id);
                }

                foreach (var id in JsonPathReader.SelectValues(root, "retryRecords[*]"))
                {
                    retry.Add(id);
                }
            }

            foreach (var (request, index) in chunk)
            {
                // An id storage asks to be read again was not answered for: it is neither held nor missing yet.
                results[index] = retry.Contains(request.TargetId) && !versions.ContainsKey(request.TargetId)
                    ? new VerifyResult(VerifyOutcome.Error, null, "storage asked for the record to be read again (retryRecords), so whether it holds it is not known")
                    : SettleVerify(request, versions, invalid);
            }
        }

        await SettleMovedAsync(client, batchPath, requests, results, ct).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Reads whole, in batched reads, the records whose version moved while their delivery recorded the hash of the content
    /// it wrote (<see cref="OwnedContent"/>). A record whose only changes are in the data keys another system writes (External
    /// Data Services updating a data job's run state) matches: the version is the other system's, not drift. A read that
    /// fails leaves those records undecided rather than drifted, so a reconciling pass does not send them again on a guess.
    /// </summary>
    private static async Task SettleMovedAsync(OsduHttpClient client, string batchPath, IReadOnlyList<VerifyRequest> requests, VerifyResult[] results, CancellationToken ct)
    {
        var moved = Enumerable.Range(0, requests.Count)
            .Where(i => results[i].Outcome == VerifyOutcome.Drifted && OwnedContent.Recorded(requests[i].TargetState) is not null)
            .ToList();
        if (moved.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, JsonObject> stored;
        try
        {
            stored = await ReadManyAsync(client, batchPath, moved.Select(i => requests[i].TargetId).ToList(), null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
        {
            foreach (var i in moved)
            {
                results[i] = new VerifyResult(
                    VerifyOutcome.Error,
                    results[i].ObservedVersion,
                    $"{results[i].Detail}; the record could not be read to tell whether the content the flow wrote changed, or only the version moved: {HeaderRedaction.RedactMessage(ex.Message)}");
            }

            return;
        }

        foreach (var i in moved)
        {
            var request = requests[i];
            if (!stored.TryGetValue(request.TargetId, out var record) || OwnedContent.Unchanged(record, request.TargetState) != true)
            {
                continue;
            }

            var observed = record["version"] is JsonValue read && read.TryGetValue<long>(out var version) ? version : results[i].ObservedVersion;
            var excluded = OwnedContent.Recorded(request.TargetState)!.Value.Excluded;
            results[i] = new VerifyResult(
                VerifyOutcome.Match,
                observed,
                excluded.Count == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"observed version {observed}, ledger holds {request.ExpectedVersion}: the newer version holds the content the flow wrote, written again by another writer (another instance linked to the record), which is not drift")
                    : string.Create(CultureInfo.InvariantCulture, $"observed version {observed}, ledger holds {request.ExpectedVersion}: the newer version changed only data keys another system writes ({string.Join(", ", excluded)}), which is not drift"));
        }
    }

    /// <summary>What one record's batched read means for it: its version, or an absence.</summary>
    private static VerifyResult SettleVerify(VerifyRequest request, IReadOnlyDictionary<string, long?> versions, IReadOnlySet<string> invalid)
    {
        if (!versions.TryGetValue(request.TargetId, out var observed))
        {
            return new VerifyResult(VerifyOutcome.Missing, null, invalid.Contains(request.TargetId)
                ? "record not found (storage names the id under invalidRecords, as it does for a record it does not hold)"
                : "record not found");
        }

        if (observed is null)
        {
            return new VerifyResult(VerifyOutcome.Error, null, "record has no version");
        }

        if (request.ExpectedVersion is null)
        {
            return new VerifyResult(VerifyOutcome.Match, observed, "no expected version recorded; observed version adopted");
        }

        return observed == request.ExpectedVersion
            ? new VerifyResult(VerifyOutcome.Match, observed, null)
            : new VerifyResult(VerifyOutcome.Drifted, observed, $"observed version {observed.Value.ToString(CultureInfo.InvariantCulture)}, ledger holds {request.ExpectedVersion.Value.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Reads records back whole in batched reads (openapi storage v2, <c>POST /query/records</c>, at most
    /// <see cref="StorageRecords.Batch"/> ids per request), keyed by id; a record the service does not return is not in the
    /// result. <paramref name="attributes"/> projects the data block when it names data paths. The reads are
    /// <see cref="StorageRecords"/>', the one storage batch read every reader of records uses.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, JsonObject>> ReadManyAsync(
        OsduHttpClient client, string batchPath, IReadOnlyCollection<string> ids, IReadOnlyList<string>? attributes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(ids);
        var found = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return found;
        }

        var storage = new StorageRecords(client, batchPath);
        foreach (var chunk in ids.Distinct(StringComparer.Ordinal).Chunk(StorageRecords.Batch))
        {
            ct.ThrowIfCancellationRequested();
            var read = await storage.ReadAsync(chunk, attributes, ct).ConfigureAwait(false);
            foreach (var record in read.Records)
            {
                if (JsonNode.Parse(record.GetRawText()) is JsonObject stored && OsduSearch.IdOf(record) is { } id)
                {
                    found[id] = stored;
                }
            }
        }

        return found;
    }

    /// <summary>Reads a record back as the target holds it; null on 404.</summary>
    public static Task<JsonObject?> ReadAsync(OsduHttpClient client, string verifyPath, string targetId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        return ReadAsync(client, client.Url(verifyPath, targetId), ct);
    }

    /// <summary>Reads the record at <paramref name="url"/> as the target holds it; null on 404. <paramref name="headers"/> go with the read alone.</summary>
    public static async Task<JsonObject?> ReadAsync(OsduHttpClient client, Uri url, CancellationToken ct, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(url);
        var result = await client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct, headers: headers).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return null;
        }

        return JsonNode.Parse(result.Body) as JsonObject
            ?? throw new DeliveryException($"{url.AbsolutePath}: the target answered with something other than a JSON record.");
    }

    /// <summary>
    /// The storage service keeps a record's version list beside its record path (openapi storage v2,
    /// <c>GET /records/versions/{id}</c> next to <c>GET /records/{id}</c>), so the list's path is derived from the flow's
    /// read path and follows it wherever the flow points its reads. Null when the read path is not the storage record
    /// path: no other service has the endpoint.
    /// </summary>
    public static string? VersionsPath(string verifyPath)
    {
        ArgumentNullException.ThrowIfNull(verifyPath);
        const string RecordSegment = "/records/{id}";
        return verifyPath.EndsWith(RecordSegment, StringComparison.Ordinal)
            ? string.Concat(verifyPath.AsSpan(0, verifyPath.Length - RecordSegment.Length), "/records/versions/{id}")
            : null;
    }

    /// <summary>
    /// The versions the storage service keeps of one record, newest first: null when the flow's read path is not the
    /// storage service's (there is then no list to ask for), and empty when the service holds no such record (404).
    /// </summary>
    public static async Task<IReadOnlyList<long>?> VersionsAsync(OsduHttpClient client, string verifyPath, string targetId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        if (VersionsPath(verifyPath) is not { } path)
        {
            return null;
        }

        var url = client.Url(path, targetId);
        var result = await client.SendJsonAsync(HttpMethod.Get, url, null, new HashSet<int> { 404 }, ct).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return [];
        }

        if (JsonNode.Parse(result.Body) is not JsonObject root || root["versions"] is not JsonArray listed)
        {
            throw new DeliveryException($"{url.AbsolutePath}: the target answered with something other than a version list.");
        }

        var versions = new List<long>(listed.Count);
        foreach (var node in listed)
        {
            if (node is JsonValue value && value.TryGetValue<long>(out var version))
            {
                versions.Add(version);
            }
            else
            {
                throw new DeliveryException($"{url.AbsolutePath}: the version list holds '{node?.ToJsonString()}', which is not a version number.");
            }
        }

        versions.Sort((a, b) => b.CompareTo(a));
        return versions;
    }

    /// <summary>The record as the storage service held it at one version (openapi storage v2, <c>GET /records/{id}/{version}</c>); null on 404.</summary>
    public static Task<JsonObject?> ReadVersionAsync(OsduHttpClient client, string verifyPath, string targetId, long version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var url = client.Url(verifyPath + "/{version}", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = targetId,
            ["version"] = version.ToString(CultureInfo.InvariantCulture),
        });
        return ReadAsync(client, url, ct);
    }

    /// <summary>
    /// One removal call at <paramref name="url"/>: a 404 is a record already gone, and every other refusal throws.
    /// Returns true when the call removed something.
    /// </summary>
    public static async Task<bool> RemoveAtAsync(OsduHttpClient client, HttpMethod method, Uri url, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(url);
        var result = await client.SendJsonAsync(method, url, null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        return (int)result.Status != 404;
    }

    /// <summary>
    /// Calls the service's info endpoint under the flow's auth. A refused or failed call is an outcome, not an
    /// exception: the probe exists to report exactly that. Messages are redacted before they leave.
    /// </summary>
    public static async Task<ProbeOutcome> ProbeAsync(OsduHttpClient client, string probePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var url = client.Url(probePath);
        try
        {
            var result = await client.SendJsonAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
            return new ProbeOutcome(true, (int)result.Status, "the service answered", url.AbsolutePath);
        }
        catch (OsduStatusException ex)
        {
            return new ProbeOutcome(false, ex.StatusCode, HeaderRedaction.RedactMessage(ex.Message), url.AbsolutePath);
        }
        catch (Exception ex) when (ex is DeliveryException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new ProbeOutcome(false, 0, HeaderRedaction.RedactMessage(ex.Message), url.AbsolutePath);
        }
    }

    /// <summary>
    /// The storage service's three removals (openapi storage v2), each a different endpoint and a different
    /// promise: <c>POST /records/{id}:delete</c> stops the record resolving and OSDU can revert it;
    /// <c>DELETE /records/{id}/versions</c> destroys every earlier version and leaves the latest live;
    /// <c>DELETE /records/{id}</c> destroys the record and all of its versions. All three answer 204, and a record
    /// that is already gone (404) is reported as such rather than as a failure.
    /// </summary>
    public static async Task<DeleteOutcome> DeleteAsync(OsduHttpClient client, RemovalPaths paths, string targetId, RemovalScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var url = client.Url(paths.For(scope), targetId);
        var method = scope == RemovalScope.Record ? HttpMethod.Post : HttpMethod.Delete;
        var result = await client.SendJsonAsync(method, url, null, new HashSet<int> { 404 }, ct, idempotent: true).ConfigureAwait(false);
        if ((int)result.Status == 404)
        {
            return new DeleteOutcome(false, true, "record not found in OSDU");
        }

        return new DeleteOutcome(true, false, scope switch
        {
            RemovalScope.Record => "removed from OSDU (reversible)",
            RemovalScope.History => "earlier versions purged; the latest version is still live",
            RemovalScope.Everything => "purged from OSDU (the record and every version)",
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        });
    }

    /// <summary>The media type of storage's merge patch of a record (osdu/specs/core/storage/openapi.yaml, <c>PATCH /records/{id}</c>).</summary>
    public const string MergePatchMediaType = "application/merge-patch+json";

    /// <summary>
    /// Brings a soft-deleted record back under its id (osdu/specs/core/INTEGRATION.md section 5.4, D3): a merge patch of
    /// <c>{"deleted": false}</c>, after which storage serves the record at the version it had, with no new version (storage
    /// source, RecordServiceImpl.patchRecord). A 404 is an id storage holds no record of (purged, or never written); a 400
    /// saying the state is already so is a record storage serves. Every other refusal throws, 403 naming credentials that
    /// are no owner of the record.
    /// </summary>
    public static async Task<(RecordRestore Outcome, int Status)> RestoreAsync(OsduHttpClient client, string recordPath, string targetId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var url = client.Url(recordPath, targetId);
        HttpFetchResult result;
        try
        {
            result = await client.SendJsonAsync(
                HttpMethod.Patch, url, new JsonObject { ["deleted"] = false }, new HashSet<int> { 400, 404 }, ct, idempotent: true, mediaType: MergePatchMediaType).ConfigureAwait(false);
        }
        catch (OsduStatusException ex) when (ex.StatusCode == 403)
        {
            throw new OsduStatusException(
                403,
                $"OSDU refused to bring {targetId} back from its soft delete (403): the flow's credentials are no owner of the record (its acl.owners), so nothing was written",
                ex);
        }

        var status = (int)result.Status;
        if (status == 404)
        {
            return (RecordRestore.NotHeld, status);
        }

        if (status == 400)
        {
            var answer = result.BodyText;
            return answer.Contains("already updated", StringComparison.OrdinalIgnoreCase)
                ? (RecordRestore.Active, status)
                : throw new OsduStatusException(400, $"OSDU refused to bring {targetId} back from its soft delete (400): {(answer.Length <= 300 ? answer : answer[..300])}");
        }

        return (RecordRestore.Restored, status);
    }

    /// <summary>A restore's outcome as a step records it.</summary>
    public static string Describe(RecordRestore outcome) => outcome switch
    {
        RecordRestore.Restored => "restored",
        RecordRestore.NotHeld => "not held",
        _ => "active",
    };

    /// <summary>
    /// Copies the OSDU-owned data keys (osdu/docs/reference/flow/routes.md, Options every route reads) from the current record,
    /// read from the target, into the document, merging a list the record's steps link datasets in (<paramref name="links"/>).
    /// </summary>
    public static async Task PreserveAsync(OsduHttpClient client, string verifyPath, string targetId, JsonObject document, IReadOnlyList<string> keys, StepLinks links, CancellationToken ct)
    {
        if (await ReadAsync(client, verifyPath, targetId, ct).ConfigureAwait(false) is { } existing)
        {
            Preserve(existing, document, keys, links);
        }
    }

    /// <summary>
    /// Copies the OSDU-owned data keys (osdu/docs/reference/flow/routes.md, Options every route reads) from
    /// <paramref name="existing"/> into the document. A list the record's steps of <c>enrich:</c> link datasets in is merged
    /// instead (<see cref="StepLinks.Merge"/>): OSDU's items stay, with the step's links as the document has them now.
    /// </summary>
    public static void Preserve(JsonObject existing, JsonObject document, IReadOnlyList<string> keys, StepLinks links)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(links);
        if (existing["data"] is not JsonObject existingData)
        {
            return;
        }

        if (document["data"] is not JsonObject data)
        {
            data = new JsonObject();
            document["data"] = data;
        }

        foreach (var key in keys)
        {
            if (existingData[key] is { } value)
            {
                data[key] = value is JsonArray stored && links.Merge(stored, data[key] as JsonArray) is { } merged ? merged : value.DeepClone();
            }
        }
    }

    public static long? ParseVersion(string? idVersion)
    {
        if (string.IsNullOrWhiteSpace(idVersion))
        {
            return null;
        }

        var last = idVersion.LastIndexOf(':');
        var text = last >= 0 && last < idVersion.Length - 1 ? idVersion[(last + 1)..] : idVersion;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
