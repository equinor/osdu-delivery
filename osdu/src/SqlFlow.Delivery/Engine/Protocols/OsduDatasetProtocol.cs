using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The dataset route (osdu/docs/reference/flow/routes.md, dataset; osdu/specs/core/INTEGRATION.md sections 2.5 and
/// 3.3). A record of a dataset kind is the dataset: its files go where the Dataset service says for its entity type,
/// and it is registered under its own id with its <c>DatasetProperties</c> pointing at them. Any other record refers to
/// one dataset of the flow's dataset kind holding its files, registered under an id derived from the record's, and is
/// written through storage. The Dataset service keeps the ids it is given, so new files land on the same dataset. A
/// change to a record alone is written through storage with the dataset properties OSDU holds, since a registration
/// copies the staging area again. Every registered dataset is checked to answer retrieval instructions.
/// </summary>
public sealed class OsduDatasetProtocol : IDeliveryProtocol
{
    public const string RegisterStep = "register";

    /// <summary>The suffix of the dataset a record of another kind keeps its files in.</summary>
    public const string FilesSuffix = PayloadParts.Files;

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly ILogger _logger;
    private readonly long _requestBodyCeiling;
    private readonly TimeProvider _time;
    private readonly DatasetService _datasets;
    private readonly OsduRecordProtocol _records;

    public OsduDatasetProtocol(OsduHttpClient client, ProtocolOptions options, ILogger logger, long requestBodyCeiling = 0, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options.ForFiles(besideBulk: false);
        _logger = logger;
        _requestBodyCeiling = requestBodyCeiling;
        _time = time ?? TimeProvider.System;
        _datasets = new DatasetService(client, _options);
        _records = new OsduRecordProtocol(client, options, _time);
    }

    public DeliveryProtocol Kind => DeliveryProtocol.Dataset;

    /// <summary>A record of another kind registers its dataset before it is written, so a delivery can leave the dataset behind until its undo.</summary>
    public bool Undoes => true;

    /// <summary>The step a registration's intent is reported under, before the registration is sent.</summary>
    public const string RegisterIntentStep = "register-intent";

    /// <summary>The slot of the dataset a record of another kind keeps its files in; a dataset record is reported as the record itself.</summary>
    public const string FilesDatasetSlot = "files-dataset";

    /// <summary>Records per request: a registration takes at most 20.</summary>
    public int MaxBatch => Math.Clamp(_options.BatchSize, 1, DatasetService.MaxRecords);

    /// <summary>The id of the dataset a record of a non-dataset kind keeps its files in.</summary>
    public string DatasetIdFor(string targetId) => DatasetIdFor(_options, targetId);

    /// <summary>
    /// The id of the dataset a record of a non-dataset kind keeps its files in on a route with <paramref name="options"/>:
    /// derived from the record's id and the route's dataset kind, so it is the same on every delivery and known before one.
    /// </summary>
    public static string DatasetIdFor(ProtocolOptions options, string targetId)
    {
        ArgumentNullException.ThrowIfNull(options);
        return WorkflowValues.DerivedDatasetId(targetId, TargetId.EntityTypeFromKind(options.DatasetKind), FilesSuffix);
    }

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var outcome = (await DeliverBatchAsync([work], ct).ConfigureAwait(false))[0];
        return outcome.Failure is { } failure ? throw failure : outcome;
    }

    public async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var outcomes = new DeliveryOutcome?[works.Count];
        var staged = new List<Staged>();
        for (var i = 0; i < works.Count; i++)
        {
            var work = works[i];
            if (!work.DeliverMetadata && !work.DeliverPayload)
            {
                outcomes[i] = new DeliveryOutcome { MetadataDelivered = false, PayloadDelivered = false, TargetVersion = work.ExistingVersion };
                continue;
            }

            var steps = new DeliverySteps(_time);
            try
            {
                staged.Add(await StageAsync(i, work, steps, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
            {
                outcomes[i] = DeliveryOutcome.Failed(ex, steps.Steps);
            }
        }

        await RegisterAsync(staged, ct).ConfigureAwait(false);
        await WriteRecordsAsync(staged, ct).ConfigureAwait(false);
        foreach (var item in staged)
        {
            outcomes[item.Index] = item.Outcome();
        }

        return outcomes.Select(o => o!).ToList();
    }

    /// <summary>What one record needs: its files staged, and the dataset record and the storage write that follow.</summary>
    private async Task<Staged> StageAsync(int index, DeliveryWork work, DeliverySteps steps, CancellationToken ct)
    {
        var document = (JsonObject)work.Document.DeepClone();
        var kind = document["kind"] is JsonValue k && k.TryGetValue<string>(out var text) ? text : throw new RecordHeldException("the record has no kind");
        var entityType = TargetId.EntityTypeFromKind(kind);
        var isDataset = DatasetService.IsDatasetType(entityType);
        var item = new Staged(index, work, steps) { IsDataset = isDataset };

        if (isDataset)
        {
            if (work.DeliverPayload)
            {
                var source = work.Payload ?? throw new RecordHeldException("the record needs its files and none are attached");
                var files = await DatasetUploads.UploadAsync(_client, _datasets, _options, work, PayloadParts.Files, source, entityType, _requestBodyCeiling, steps, ct).ConfigureAwait(false);
                DatasetUploads.Point(document, files);
                item.Files = files.Files.Count;
                item.Registration = document;
            }
            else
            {
                // The record alone changed: storage takes it, with the dataset properties OSDU holds.
                await CarryPropertiesAsync(work.TargetId, document, ct).ConfigureAwait(false);
                item.Record = document;
            }

            return item;
        }

        var datasetKind = _options.DatasetKind;
        var datasetType = TargetId.EntityTypeFromKind(datasetKind);
        if (!DatasetService.IsDatasetType(datasetType))
        {
            throw new RecordHeldException($"target.protocolOptions.datasetKind '{datasetKind}' is not a dataset kind, so the record's files have no dataset to be registered as");
        }

        var datasetId = DatasetIdFor(work.TargetId);
        if (work.DeliverPayload)
        {
            var source = work.Payload ?? throw new RecordHeldException("the record needs its files and none are attached");
            var files = await DatasetUploads.UploadAsync(_client, _datasets, _options, work, PayloadParts.Files, source, datasetType, _requestBodyCeiling, steps, ct).ConfigureAwait(false);
            var dataset = new JsonObject
            {
                ["id"] = datasetId,
                ["kind"] = datasetKind,
                ["acl"] = document["acl"]?.DeepClone() ?? throw new RecordHeldException("the rendered record has no acl block to copy onto its dataset record"),
                ["legal"] = document["legal"]?.DeepClone() ?? throw new RecordHeldException("the rendered record has no legal block to copy onto its dataset record"),
                ["data"] = new JsonObject(),
            };
            DatasetUploads.Point(dataset, files);
            item.Files = files.Files.Count;
            item.Registration = dataset;
        }

        FileUploads.SetDatasets(document, _options.DatasetsProperty, [datasetId]);
        item.DatasetId = datasetId;

        // The dataset keeps its id, so the record needs writing only when it changed or has not named the dataset yet.
        var named = FileUploads.DatasetIds(work.TargetState).Contains(datasetId, StringComparer.Ordinal);
        if (work.DeliverMetadata || !named)
        {
            item.Record = document;
        }

        return item;
    }

    /// <summary>
    /// Registers the staged datasets, twenty to a request, skipping the ones an earlier try registered, and checks that
    /// the Dataset service answers retrieval instructions for each. A registration refused as a whole is tried record by
    /// record, so one bad record fails alone.
    /// </summary>
    private async Task RegisterAsync(List<Staged> staged, CancellationToken ct)
    {
        var pending = new List<Staged>();
        foreach (var item in staged.Where(s => s.Registration is not null))
        {
            if (item.Work.Completed(RegisterStep) is { } done && done.TryGetValue("version", out var known))
            {
                item.Steps.Resumed(RegisterStep, done);
                item.RegisteredVersion = RecordWriter.ParseVersion(known);
                continue;
            }

            pending.Add(item);
        }

        foreach (var chunk in pending.Chunk(DatasetService.MaxRecords))
        {
            try
            {
                await RegisterChunkAsync(chunk, ct).ConfigureAwait(false);
            }
            catch (OsduStatusException ex) when (chunk.Length > 1 && ex.StatusCode is >= 400 and < 500 and not (401 or 408 or 425 or 429))
            {
                foreach (var one in chunk)
                {
                    try
                    {
                        await RegisterChunkAsync([one], ct).ConfigureAwait(false);
                    }
                    catch (Exception single) when (single is SqlFlowException or HttpRequestException or IOException or JsonException)
                    {
                        one.Failure = single;
                    }
                }
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
            {
                foreach (var one in chunk)
                {
                    one.Failure = ex;
                }
            }
        }
    }

    private async Task RegisterChunkAsync(IReadOnlyList<Staged> chunk, CancellationToken ct)
    {
        var ready = await DeclareAsync(chunk, ct).ConfigureAwait(false);
        if (ready.Count == 0)
        {
            return;
        }

        var started = _time.GetUtcNow().UtcDateTime;
        var records = ready.Select(c => c.Registration!).ToList();
        var landed = await _datasets.RegisterAsync(records, ct).ConfigureAwait(false);
        var ids = records.Select(r => r["id"]!.GetValue<string>()).ToList();
        var retrievable = await _datasets.RetrievableAsync(ids, ct).ConfigureAwait(false);
        foreach (var item in ready)
        {
            var id = item.Registration!["id"]!.GetValue<string>();
            if (!retrievable.Contains(id))
            {
                item.Failure = new DeliveryException($"the dataset service registered {id} and answers no retrieval instructions for it, so its files cannot be read back; the next try registers it again");
                continue;
            }

            item.RegisteredVersion = landed.GetValueOrDefault(id);
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["datasetId"] = id, ["retrievable"] = "true" };
            if (item.RegisteredVersion is { } version)
            {
                values["version"] = version.ToString(CultureInfo.InvariantCulture);
            }

            item.Steps.Add(RegisterStep, started, 201, values);
            await item.Work.ReportStepAsync(RegisterStep, values, item.Declared is { } declared ? [RegistrationIntents.Landed(declared, item.RegisteredVersion)] : [], ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports what each registration of <paramref name="chunk"/> is about to write, before it is sent, so a registration
    /// whose answer is lost is still undone (<see cref="RegistrationIntents"/>): the dataset a record of another kind keeps its
    /// files in, or a dataset record itself, under the id the route gives it. A record storage could not answer for fails
    /// alone, unregistered; the others go on.
    /// </summary>
    private async Task<List<Staged>> DeclareAsync(IReadOnlyList<Staged> chunk, CancellationToken ct)
    {
        var intents = await RegistrationIntents.DeclareAsync(
            _records,
            chunk.Select(c => new Registration(c.IsDataset ? TargetArtifact.RecordSlot : FilesDatasetSlot, c.Registration!["id"]!.GetValue<string>())).ToList(),
            ct).ConfigureAwait(false);
        var ready = new List<Staged>(chunk.Count);
        for (var i = 0; i < chunk.Count; i++)
        {
            var item = chunk[i];
            if (intents[i].Artifact is not { } declared)
            {
                item.Failure = new DeliveryException(intents[i].Refusal!);
                continue;
            }

            item.Declared = declared;
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["datasetId"] = declared.TargetId! };
            if (declared.PriorVersion is { } version)
            {
                values["priorVersion"] = version.ToString(CultureInfo.InvariantCulture);
            }

            await item.Work.ReportStepAsync(RegisterIntentStep, values, [declared], ct).ConfigureAwait(false);
            ready.Add(item);
        }

        return ready;
    }

    /// <summary>
    /// Undoes what unfinished deliveries registered (osdu/docs/reference/concepts/artifacts-and-undo.md, dataset): a
    /// dataset the unit created is removed reversibly through the Dataset service, once storage confirms OSDU created
    /// it after the unit began, and a dataset the unit wrote a new version of is given back the version before. Newer
    /// work of the record does not keep what the unit wrote: a record of another kind names its dataset without a
    /// version, and a dataset record's own write carries the <c>DatasetProperties</c> storage holds, so either would
    /// take on the files of a delivery that did not complete. The record of another kind is written last, in one write,
    /// so a unit never leaves it behind; the files a registration staged stay in the platform's storage, which no
    /// public OSDU operation deletes.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoAsync(IReadOnlyList<UndoWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var side = new RecordSide
        {
            Remove = (id, token) => DeleteAsync(id, RemovalScope.Record, null, token),
            Read = _records.ReadAsync,
            Restorer = _records,
            Versions = _records.VersionsAsync,
        };
        var results = new List<UndoResult>();
        foreach (var work in works)
        {
            var registered = work.Items.Where(i => ArtifactRoles.IsTheRecord(i.Artifact.Role)).ToList();
            results.AddRange(await ArtifactUndo.RecordItselfAsync(work with { KeepRecord = false }, registered, side, ct).ConfigureAwait(false));
            results.AddRange(ArtifactUndo.Keep(work.Items.Except(registered), "the dataset route creates nothing of this kind"));
        }

        return results;
    }

    /// <summary>The storage writes: a dataset record whose record alone changed, and the records that refer to a dataset.</summary>
    private async Task WriteRecordsAsync(List<Staged> staged, CancellationToken ct)
    {
        var writes = staged.Where(s => s.Failure is null && s.Record is not null).ToList();
        if (writes.Count == 0)
        {
            return;
        }

        var outcomes = await _records.DeliverBatchAsync(
            writes.Select(w => w.Work with { Document = w.Record!, DeliverMetadata = true, DeliverPayload = false, Payload = null, Parts = [] }).ToList(), ct).ConfigureAwait(false);
        for (var i = 0; i < writes.Count; i++)
        {
            writes[i].Written = outcomes[i];
            if (outcomes[i].Failure is { } failure)
            {
                writes[i].Failure = failure;
            }
        }
    }

    /// <summary>Copies the <c>DatasetProperties</c> storage holds for the record into the document.</summary>
    private async Task CarryPropertiesAsync(string targetId, JsonObject document, CancellationToken ct)
    {
        var stored = await RecordWriter.ReadAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, targetId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"{targetId} is not in storage, so the dataset properties that point at its files cannot be carried into the rewrite; redeliver its files");
        if (stored["data"]?["DatasetProperties"] is not JsonObject)
        {
            throw new DeliveryException($"{targetId} holds no DatasetProperties in storage, so the rewrite would not point at its files; redeliver its files");
        }

        RecordWriter.Preserve(stored, document, ["DatasetProperties"], StepLinks.None);
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => _records.VerifyAsync(targetId, expectedVersion, ct);

    int IDeliveryProtocol.MaxVerifyBatch => OsduRecordProtocol.MaxVerifyBatch;

    public Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default)
        => _records.VerifyBatchAsync(requests, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => _records.ReadAsync(targetId, ct);

    public Task<IReadOnlyList<long>?> VersionsAsync(string targetId, CancellationToken ct = default)
        => _records.VersionsAsync(targetId, ct);

    public Task<JsonObject?> ReadVersionAsync(string targetId, long version, CancellationToken ct = default)
        => _records.ReadVersionAsync(targetId, version, ct);

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => RecordWriter.ProbeAsync(_client, _options.ProbePath ?? DatasetService.DefaultProbePath, ct);

    public Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
        => _records.InvalidLegalTagsAsync(tags, ct);

    /// <summary>
    /// A dataset record goes through the Dataset service's reversible removal, which its undelete restores; a record that
    /// refers to a dataset goes through storage's and leaves its dataset, so OSDU can restore it whole. The history purge
    /// is storage's. Everything purges the record and the dataset it refers to through storage; the files a collection
    /// staged stay in the platform's storage, which no public OSDU operation deletes.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var isDataset = DatasetService.IsDatasetType(TargetIdEntityType(targetId));
        if (scope == RemovalScope.Record && isDataset)
        {
            return await _datasets.SoftDeleteAsync(targetId, ct).ConfigureAwait(false);
        }

        var outcome = await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options), targetId, scope, ct).ConfigureAwait(false);
        if (scope != RemovalScope.Everything || isDataset)
        {
            return outcome;
        }

        var dataset = await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options), DatasetIdFor(targetId), scope, ct).ConfigureAwait(false);
        return dataset.Deleted
            ? outcome with { Deleted = true, Detail = $"{outcome.Detail}; its dataset {DatasetIdFor(targetId)} purged too" }
            : outcome;
    }

    /// <summary>The entity type an OSDU id names (<c>{partition}:{entityType}:{key}</c>).</summary>
    private static string TargetIdEntityType(string targetId)
    {
        var first = targetId.IndexOf(':', StringComparison.Ordinal);
        var second = first < 0 ? -1 : targetId.IndexOf(':', first + 1);
        return first > 0 && second > first ? targetId[(first + 1)..second] : string.Empty;
    }

    private sealed class Staged(int index, DeliveryWork work, DeliverySteps steps)
    {
        public int Index { get; } = index;

        public DeliveryWork Work { get; } = work;

        public DeliverySteps Steps { get; } = steps;

        public bool IsDataset { get; init; }

        /// <summary>The dataset record to register, when files go.</summary>
        public JsonObject? Registration { get; set; }

        /// <summary>The record to write through storage, when one is written.</summary>
        public JsonObject? Record { get; set; }

        public string? DatasetId { get; set; }

        public int Files { get; set; }

        public long? RegisteredVersion { get; set; }

        /// <summary>What the registration was declared to write, before it was sent.</summary>
        public TargetArtifact? Declared { get; set; }

        public DeliveryOutcome? Written { get; set; }

        public Exception? Failure { get; set; }

        public DeliveryOutcome Outcome()
        {
            var steps = Steps.Steps.Concat(Written?.Steps ?? []).ToList();
            if (Failure is { } failure)
            {
                return DeliveryOutcome.Failed(failure, steps);
            }

            var returned = new Dictionary<string, string>(Written?.Returned ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["recordId"] = Work.TargetId,
            };
            var version = IsDataset ? (RegisteredVersion ?? Written?.TargetVersion) : (Written?.TargetVersion ?? Work.ExistingVersion);
            if (version is { } v)
            {
                returned["version"] = v.ToString(CultureInfo.InvariantCulture);
            }

            if (DatasetId is not null)
            {
                returned[FileUploads.DatasetIdsValue] = DatasetId;
            }

            if (Files > 0)
            {
                returned["files"] = Files.ToString(CultureInfo.InvariantCulture);
            }

            if (IsDataset && Registration is not null && Written is null)
            {
                // A registration writes the record whole and carries nothing forward, so no hash of the flow's own content stands.
                OwnedContent.Record(returned, Registration, [], Work.TargetState);
            }

            return new DeliveryOutcome
            {
                MetadataDelivered = Work.DeliverMetadata || (IsDataset && Registration is not null) || Written is not null,
                PayloadDelivered = Registration is not null,
                TargetVersion = version ?? Work.ExistingVersion,
                ChunksSent = Files,
                Detail = Files > 0 ? $"{Files.ToString(CultureInfo.InvariantCulture)} file(s) stored and registered through the dataset service" : null,
                Returned = returned,
                Steps = steps,
            };
        }
    }
}
