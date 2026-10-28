using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// Manifest ingestion (osdu/docs/reference/flow/routes.md, manifest): the batch's files go to the landing zone and are
/// registered through the file service (openapi file v2, POST files/metadata), as for the file protocol; then one
/// manifest carrying the batch's records, each listing the datasets registered for it, is handed to the ingestion
/// workflow (openapi workflow v1, POST workflow/{name}/workflowRun); the run is polled until it finishes; and the
/// records are read back from storage (openapi storage v2, POST query/records) so each settles on its own evidence:
/// present with a version, delivered; absent, failed with the run named. Registration comes first because it is what
/// makes a file retrievable: a dataset the manifest only describes is created with its file left in the landing zone,
/// where the file service's download URL finds nothing (observed on a live M26 service). The file service mints the
/// dataset ids, so a payload change registers new datasets and the record points at them. Registered datasets are
/// waited for until the search index lists them, because ingestion checks a record's references against the index; and
/// each record's version is read before the run, because a run that drops a record still finishes and a record that
/// existed is still present afterwards, so only a version that moved counts as written. The manifest step records the
/// run id before polling starts (osdu/docs/reference/concepts/protocols.md, Steps and returned values), so a retry
/// after a poll timeout resumes the same run; a run that failed, or that finished without writing the record, is
/// triggered again on the next try, and the failed run stays on the attempt. Every registration, the record a run is to
/// write, and a manifest stored by reference are artifacts of the record's unit of work
/// (osdu/docs/reference/concepts/artifacts-and-undo.md, manifest): a delivery that does not complete has its datasets
/// removed and the record removed or given back the version it replaced, once the run that could still write it has
/// ended.
/// </summary>
public sealed class OsduManifestProtocol : IDeliveryProtocol
{
    public const string DefaultWorkflowRunPath = WorkflowClient.DefaultRunPath;
    public const string DefaultWorkflowStatusPath = WorkflowClient.DefaultStatusPath;
    public const string DefaultProbePath = WorkflowClient.DefaultProbePath;
    public const string DefaultRecordQueryPath = "/api/storage/v2/query/records";

    public const string ManifestStep = "manifest";
    public const string WorkflowStep = "workflow";
    public const string RecordsStep = "records";
    public const string IndexedStep = "indexed";
    public const string DefaultSearchQueryPath = "/api/search/v2/query";

    /// <summary>The manifest step value carrying the version storage held of the record before the run was triggered.</summary>
    public const string PriorVersionValue = "priorVersion";

    /// <summary>The manifest step value naming the workflow the run was triggered on (the inline or the by-reference one).</summary>
    public const string WorkflowValue = "workflow";

    /// <summary>The manifest step value naming the dataset a manifest sent by reference was stored as.</summary>
    public const string ManifestDatasetValue = "manifestDatasetId";

    /// <summary>Dataset ids per search query while waiting for the index to list them.</summary>
    private const int SearchBatch = 50;

    /// <summary>Records per storage read-back request (openapi storage v2, MultiRecordIds takes at most 100).</summary>
    public const int QueryBatch = 100;

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly ILogger _logger;
    private readonly long _requestBodyCeiling;
    private readonly TimeProvider _time;
    private readonly WorkflowClient _workflows;

    public OsduManifestProtocol(OsduHttpClient client, ProtocolOptions options, ILogger logger, long requestBodyCeiling = 0, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _options = options.ForFiles(besideBulk: false);
        _logger = logger;
        _requestBodyCeiling = requestBodyCeiling;
        _time = time ?? TimeProvider.System;
        _workflows = new WorkflowClient(client, logger, _time, options.WorkflowRunPath, options.WorkflowStatusPath, options.WorkflowPath);
        _datasets = new DatasetService(client, options);
    }

    private readonly DatasetService _datasets;

    /// <summary>Whether the partition registers the by-reference workflow, once asked.</summary>
    private bool? _byReferenceRegistered;

    public DeliveryProtocol Kind => DeliveryProtocol.Manifest;

    /// <summary>The files are registered, and a manifest may be stored, before a run writes the record.</summary>
    public bool Undoes => true;

    /// <summary>The slot of the record a run is to write: one per unit, so a retry's new run takes it over rather than adding another.</summary>
    private const string RecordSlot = TargetArtifact.RecordSlot;

    /// <summary>The slot of a manifest stored by reference for a run.</summary>
    private static string ManifestSlot(string runId) => "manifest:" + runId;

    /// <summary>What finds the run an artifact waits on: the workflow and its run id.</summary>
    private static string RunLocator(string workflow, string runId) => workflow + "|" + runId;

    public int MaxBatch => Math.Clamp(_options.BatchSize, 1, ProtocolOptions.MaxBatchSize);

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var outcome = (await DeliverBatchAsync([work], ct).ConfigureAwait(false))[0];
        return outcome.Failure is { } failure ? throw failure : outcome;
    }

    public async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var outcomes = new DeliveryOutcome[works.Count];
        var fresh = new List<Staged>();
        var resumed = new Dictionary<(string RunId, string Workflow, string? Manifest), List<Staged>>();
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
                var ids = new List<string>();
                var files = 0;
                if (work.DeliverPayload)
                {
                    // Every file is registered through the file service before the manifest names it: registration is
                    // what makes it retrievable, and the register steps resume on a retry, so a file is registered once.
                    var chunks = await FileUploads.ListChunksAsync(work, _requestBodyCeiling, ct).ConfigureAwait(false);
                    var uploaded = await FileUploads.UploadAsync(_client, _options, work, chunks, steps, ct).ConfigureAwait(false);
                    foreach (var file in uploaded)
                    {
                        ids.Add(await FileUploads.RegisterAsync(_client, _options, work, file, steps, _time, ct).ConfigureAwait(false));
                    }

                    files = uploaded.Count;
                }
                else
                {
                    ids.AddRange(FileUploads.DatasetIds(work.TargetState));
                }

                var document = (JsonObject)work.Document.DeepClone();
                if (ids.Count > 0)
                {
                    FileUploads.SetDatasets(document, _options.DatasetsProperty, ids);
                }

                var section = _options.ManifestSection ?? SectionOf(document);
                var staged = new Staged(i, work, document, section, ids, steps, files, OwnedContent.PreservedKeys(_options, document));
                if (work.Completed(ManifestStep) is { } earlier && earlier.TryGetValue("runId", out var runId) && !string.IsNullOrEmpty(runId))
                {
                    steps.Resumed(ManifestStep, earlier);
                    staged.PriorVersion = earlier.TryGetValue(PriorVersionValue, out var prior) && long.TryParse(prior, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : null;
                    var runKey = (runId, earlier.GetValueOrDefault(WorkflowValue) ?? _options.WorkflowName, earlier.GetValueOrDefault(ManifestDatasetValue));
                    if (!resumed.TryGetValue(runKey, out var group))
                    {
                        group = [];
                        resumed[runKey] = group;
                    }

                    group.Add(staged);
                }
                else
                {
                    fresh.Add(staged);
                }
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
            {
                outcomes[i] = DeliveryOutcome.Failed(ex, steps.Steps);
            }
        }

        // Runs an earlier try started: a finished one settles the records it wrote here; the records it failed or
        // left out go into the new manifest with everything else.
        foreach (var ((runId, workflow, manifestDataset), group) in resumed)
        {
            try
            {
                var run = await PollAsync(workflow, runId, _time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                await RemoveManifestAsync(manifestDataset, runId, group, ct).ConfigureAwait(false);
                if (run.HasFailed)
                {
                    _logger.LogWarning("Workflow run {RunId} of {Workflow} failed; {Count} record(s) go into a new run.", runId, workflow, group.Count);
                    foreach (var staged in group)
                    {
                        staged.Steps.Add(WorkflowStep, run.Started, 200, run.Values(), "the run failed; a new run is triggered");
                    }

                    fresh.AddRange(group);
                    continue;
                }

                var (missing, _) = await SettleAsync(group, run, outcomes, ct).ConfigureAwait(false);
                if (missing.Count > 0)
                {
                    _logger.LogWarning("Workflow run {RunId} of {Workflow} finished {Status} without {Count} of its {Total} record(s); they go into a new run.", runId, workflow, run.Status, missing.Count, group.Count);
                    fresh.AddRange(missing);
                }
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
            {
                foreach (var staged in group)
                {
                    outcomes[staged.Index] = DeliveryOutcome.Failed(ex, staged.Steps.Steps);
                }
            }
        }

        if (fresh.Count > 0)
        {
            List<List<Staged>> runs;
            try
            {
                await WaitForDatasetsAsync(fresh, ct).ConfigureAwait(false);
                await ReadPriorVersionsAsync(fresh, ct).ConfigureAwait(false);
                runs = await PlanRunsAsync(fresh, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
            {
                foreach (var staged in fresh)
                {
                    outcomes[staged.Index] = DeliveryOutcome.Failed(ex, staged.Steps.Steps);
                }

                runs = [];
            }

            foreach (var group in runs)
            {
                await RunAsync(group, outcomes, ct).ConfigureAwait(false);
            }
        }

        return outcomes;
    }

    /// <summary>
    /// One run for a group: the manifest triggered (inline, or by reference), the run polled and every record settled on
    /// its own read-back; a record the run did not write fails for this try and goes into a new run on the next.
    /// </summary>
    private async Task RunAsync(List<Staged> group, DeliveryOutcome[] outcomes, CancellationToken ct)
    {
        try
        {
            var triggered = await TriggerAsync(group, ct).ConfigureAwait(false);
            var run = await PollAsync(triggered.Run.Workflow, triggered.Run.RunId, triggered.Run.Started, ct).ConfigureAwait(false);
            await RemoveManifestAsync(triggered.ManifestDataset, triggered.Run.RunId, group, ct).ConfigureAwait(false);
            if (run.HasFailed)
            {
                var failure = new DeliveryException($"workflow run {run.RunId} of {run.Workflow} failed; the next try triggers a new run");
                foreach (var staged in group)
                {
                    staged.Steps.Add(WorkflowStep, run.Started, 200, run.Values(), failure.Message);
                    outcomes[staged.Index] = DeliveryOutcome.Failed(failure, staged.Steps.Steps);
                }

                return;
            }

            var (missing, notes) = await SettleAsync(group, run, outcomes, ct).ConfigureAwait(false);
            foreach (var staged in missing)
            {
                var reason = $"workflow run {run.RunId} of {run.Workflow} finished {run.Status} but {notes[staged.Work.TargetId]}; the workflow did not write it and its run log names why, and the next try triggers a new run";
                staged.Steps.Add(RecordsStep, run.Started, null, null, reason);
                outcomes[staged.Index] = DeliveryOutcome.Failed(new DeliveryException(reason), staged.Steps.Steps);
            }
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
        {
            foreach (var staged in group)
            {
                outcomes[staged.Index] = DeliveryOutcome.Failed(ex, staged.Steps.Steps);
            }
        }
    }

    /// <summary>
    /// The runs a batch goes in. One, unless the flow sends large manifests by reference when it can
    /// (<see cref="ManifestReference.Auto"/>) and the partition does not register the by-reference workflow: then the
    /// batch is split into manifests under the inline limit, as External Data Services splits them
    /// (osdu/specs/workflows/INTEGRATION.md section 3.3). A record whose manifest alone is above the limit goes on its
    /// own: the limit is a gateway's, not the Workflow contract's, which states none.
    /// </summary>
    private async Task<List<List<Staged>>> PlanRunsAsync(List<Staged> fresh, CancellationToken ct)
    {
        if (_options.ManifestByReference != ManifestReference.Auto
            || RequestKilobytes(BuildContext(fresh, BuildManifest(fresh))) <= _options.ManifestInlineLimitKb
            || await ByReferenceRegisteredAsync(ct).ConfigureAwait(false))
        {
            return [fresh];
        }

        var runs = new List<List<Staged>>();
        var current = new List<Staged>();
        foreach (var staged in fresh)
        {
            current.Add(staged);
            if (current.Count > 1 && RequestKilobytes(BuildContext(current, BuildManifest(current))) > _options.ManifestInlineLimitKb)
            {
                current.RemoveAt(current.Count - 1);
                runs.Add(current);
                current = [staged];
            }
        }

        runs.Add(current);
        _logger.LogInformation(
            "The partition registers no {Workflow}, so {Count} record(s) above the inline limit of {Limit} KB go in {Runs} manifests.",
            _options.ByReferenceWorkflowName, fresh.Count, _options.ManifestInlineLimitKb, runs.Count);
        return runs;
    }

    /// <summary>Whether the partition registers the by-reference workflow (openapi workflow v1, GET workflow/{workflow_name}), asked once.</summary>
    private async Task<bool> ByReferenceRegisteredAsync(CancellationToken ct)
        => _byReferenceRegistered ??= await _workflows.ExistsAsync(_options.ByReferenceWorkflowName, ct).ConfigureAwait(false);

    /// <summary>
    /// The size of a trigger request in kilobytes, measured as External Data Services measures it: the whole request as
    /// JSON indented by four (osdu/specs/workflows/INTEGRATION.md section 3.3).
    /// </summary>
    internal static double RequestKilobytes(JsonObject executionContext)
    {
        var request = new JsonObject { ["runId"] = Guid.Empty.ToString("D"), ["executionContext"] = executionContext.DeepClone() };
        return Encoding.UTF8.GetByteCount(request.ToJsonString(Indented)) / 1024d;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 4 };

    /// <summary>
    /// Removes the dataset a manifest by reference was stored as, reversibly, once its run has settled: it is a transport,
    /// not a delivered record. Its removal ends its artifact on every record of the run. A failure to remove it does not fail
    /// the records the run wrote: its artifact stays an intent, which the unit's end makes due, so the sweep removes it.
    /// </summary>
    private async Task RemoveManifestAsync(string? datasetId, string runId, List<Staged> group, CancellationToken ct)
    {
        if (datasetId is null)
        {
            return;
        }

        try
        {
            var removed = await _datasets.SoftDeleteAsync(datasetId, ct).ConfigureAwait(false);
            _logger.LogInformation("The manifest dataset {DatasetId} is removed: {Detail}.", datasetId, removed.Detail);
            var done = TargetArtifact.Created(ManifestSlot(runId), ArtifactRoles.Dataset, datasetId) with
            {
                Status = ArtifactStatus.Removed,
                Note = "the manifest a run read by reference, removed once its run settled",
            };
            await Task.WhenAll(group.Select(s => s.Work.ReportStepAsync(
                "manifest-removed", new Dictionary<string, string>(StringComparer.Ordinal) { [ManifestDatasetValue] = datasetId }, [done], ct))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException)
        {
            _logger.LogWarning("The manifest dataset {DatasetId} could not be removed after its run settled: {Message}; the unfinished-delivery sweep removes it.", datasetId, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => RecordWriter.VerifyAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, targetId, expectedVersion, ct);

    int IDeliveryProtocol.MaxVerifyBatch => OsduRecordProtocol.MaxVerifyBatch;

    /// <summary>The workflow writes the records into the storage service, so they verify in batched reads from it.</summary>
    public Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default)
        => RecordWriter.VerifyBatchAsync(_client, _options.VerifyBatchPath ?? OsduRecordProtocol.DefaultVerifyBatchPath, requests, ct);

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.ReadAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, targetId, ct);

    public Task<IReadOnlyList<long>?> VersionsAsync(string targetId, CancellationToken ct = default)
        => RecordWriter.VersionsAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, targetId, ct);

    public Task<JsonObject?> ReadVersionAsync(string targetId, long version, CancellationToken ct = default)
        => RecordWriter.ReadVersionAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, targetId, version, ct);

    public Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
        => RecordWriter.ProbeAsync(_client, _options.ProbePath ?? DefaultProbePath, ct);

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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        return FileUploads.DeleteRecordAndDatasetsAsync(_client, _options, targetId, scope, targetState, ct);
    }

    /// <summary>
    /// Undoes what unfinished deliveries left (osdu/docs/reference/concepts/artifacts-and-undo.md, manifest):
    /// nothing while a run that could still write the record is going (each artifact is tried again by the sweep
    /// once it ends); then every dataset the unit registered, and a manifest stored by reference, removed
    /// reversibly, and the record a run wrote removed when the run created it, or given back the version storage
    /// held before the run.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoAsync(IReadOnlyList<UndoWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var results = new List<UndoResult>();
        foreach (var work in works)
        {
            results.AddRange(await UndoWorkAsync(work, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>One record's undo, as the composed route of manifests and DDMS bulk data also takes it for what its manifests made.</summary>
    internal async Task<IReadOnlyList<UndoResult>> UndoWorkAsync(UndoWork work, CancellationToken ct)
    {
        var runs = work.Items.Where(i => i.Artifact.Role == ArtifactRoles.Run).ToList();
        if (await RunningAsync(work, ct).ConfigureAwait(false) is { } running)
        {
            return work.Items.Select(i => UndoResult.Failed(i, running)).ToList();
        }

        var (waiting, ended) = await WorkflowRuns.SettleAsync(_workflows, runs, ct).ConfigureAwait(false);
        if (waiting is not null)
        {
            return work.Items.Select(i => UndoResult.Failed(i, waiting)).ToList();
        }

        // The record goes back first, so a record that cannot be taken back yet never names datasets already removed: its
        // datasets wait with it.
        var storage = StorageWriter();
        var results = new List<UndoResult>(work.Items.Count);
        results.AddRange(runs.Select(i => UndoResult.Kept(i, ended[i.ArtifactId])));
        var record = work.Items.Where(i => ArtifactRoles.IsTheRecord(i.Artifact.Role)).ToList();
        var recordResults = await ArtifactUndo.RecordItselfAsync(work, record, StorageSide(storage), ct).ConfigureAwait(false);
        results.AddRange(recordResults);
        var datasets = work.Items.Where(i => i.Artifact.Role == ArtifactRoles.Dataset).ToList();
        results.AddRange(ArtifactUndo.WaitForRecord(recordResults) is { } blocked
            ? datasets.Select(i => UndoResult.Failed(i, blocked))
            : await ArtifactUndo.DatasetsAsync(_client, _options, storage, work, datasets, ct).ConfigureAwait(false));
        results.AddRange(ArtifactUndo.Keep(work.Items.Except(runs).Except(datasets).Except(record), "the manifest route makes nothing of this kind"));
        return results;
    }

    /// <summary>Why the undo waits: a run an artifact names that has not ended, which may still write the record; null when none is going.</summary>
    private async Task<string?> RunningAsync(UndoWork work, CancellationToken ct)
    {
        var runs = work.Items
            .Where(i => ArtifactRoles.IsTheRecord(i.Artifact.Role) && i.Artifact.Locator?.Split('|') is [_, _])
            .Select(i => i.Artifact.Locator!.Split('|'))
            .Select(p => (Workflow: p[0], RunId: p[1]))
            .Distinct()
            .ToList();
        foreach (var (workflow, runId) in runs)
        {
            if ((await WorkflowRuns.StateAsync(_workflows, workflow, runId, ct).ConfigureAwait(false)).Waiting is { } waiting)
            {
                return waiting;
            }
        }

        return null;
    }

    /// <summary>The storage writer an undo removes and restores through: the storage service's own paths, with the flow's batching.</summary>
    private OsduRecordProtocol StorageWriter()
        => _storage ??= new OsduRecordProtocol(_client, RecordRestores.WriterOptions(new ProtocolOptions { BatchSize = _options.BatchSize }), _time);

    private OsduRecordProtocol? _storage;

    /// <summary>The record a run wrote is a storage record: storage removes it, says when it created it, and writes an earlier version back.</summary>
    private RecordSide StorageSide(OsduRecordProtocol storage) => new()
    {
        Remove = (id, token) => storage.DeleteAsync(id, RemovalScope.Record, null, token),
        Read = (id, token) => RecordWriter.ReadAsync(_client, OsduRecordProtocol.DefaultVerifyPath, id, token),
        Restorer = storage,
        Versions = (id, token) => RecordWriter.VersionsAsync(_client, OsduRecordProtocol.DefaultVerifyPath, id, token),
    };

    /// <summary>
    /// The dataset id of one file of a record, derived from the record id and the dataset kind so that it is the
    /// same on every delivery: <c>dev:work-product-component--WellLog:abc</c> with
    /// <c>osdu:wks:dataset--File.Generic:1.0.0</c> gives <c>dev:dataset--File.Generic:abc-0</c>.
    /// </summary>
    internal static string DatasetId(string targetId, string datasetKind, int index)
    {
        var first = targetId.IndexOf(':', StringComparison.Ordinal);
        var second = first < 0 ? -1 : targetId.IndexOf(':', first + 1);
        if (first <= 0 || second < 0 || second == targetId.Length - 1)
        {
            throw new DeliveryException($"record id '{targetId}' is not partition:type:name, so no dataset id can be derived from it");
        }

        var kind = datasetKind.Split(':');
        if (kind.Length != 4 || string.IsNullOrEmpty(kind[2]))
        {
            throw new DeliveryException($"target.protocolOptions.datasetKind '{datasetKind}' is not authority:source:type:version");
        }

        return $"{targetId[..first]}:{kind[2]}:{targetId[(second + 1)..]}-{index.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>The manifest section a record belongs in, from the group type of its kind.</summary>
    internal static string SectionOf(JsonObject document)
    {
        var kind = document["kind"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
        var parts = kind.Split(':');
        var type = parts.Length == 4 ? parts[2] : string.Empty;
        var cut = type.IndexOf("--", StringComparison.Ordinal);
        var group = cut > 0 ? type[..cut] : type;
        return group switch
        {
            "work-product-component" => "WorkProductComponents",
            "work-product" => "WorkProduct",
            "dataset" => "Datasets",
            "master-data" => "MasterData",
            "reference-data" => "ReferenceData",
            _ => throw new DeliveryException($"kind '{kind}' names no manifest section (its group type is not work-product-component, work-product, dataset, master-data or reference-data); set target.protocolOptions.manifestSection"),
        };
    }

    /// <summary>
    /// One manifest for the batch (osdu:wks:Manifest:1.0.0): the records in their sections, a record of a dataset kind
    /// under Data. The batch's files are registered before it and referenced from the records, never described here.
    /// </summary>
    private JsonObject BuildManifest(List<Staged> group)
    {
        var manifest = new JsonObject { ["kind"] = _options.ManifestKind };
        var reference = new JsonArray();
        var master = new JsonArray();
        var components = new JsonArray();
        var datasets = new JsonArray();
        JsonObject? workProduct = null;
        foreach (var staged in group)
        {
            var document = (JsonObject)staged.Document.DeepClone();
            switch (staged.Section)
            {
                case "ReferenceData":
                    reference.Add(document);
                    break;
                case "MasterData":
                    master.Add(document);
                    break;
                case "WorkProductComponents":
                    components.Add(document);
                    break;
                case "Datasets":
                    datasets.Add(document);
                    break;
                case "WorkProduct":
                    if (workProduct is not null)
                    {
                        throw new DeliveryException("a manifest carries one WorkProduct and the batch holds more than one; set target.protocolOptions.batchSize to 1");
                    }

                    workProduct = document;
                    break;
                default:
                    throw new DeliveryException($"'{staged.Section}' is not a manifest section");
            }
        }

        if (reference.Count > 0)
        {
            manifest["ReferenceData"] = reference;
        }

        if (master.Count > 0)
        {
            manifest["MasterData"] = master;
        }

        if (workProduct is not null || components.Count > 0 || datasets.Count > 0)
        {
            var data = new JsonObject();
            if (workProduct is not null)
            {
                data["WorkProduct"] = workProduct;
            }

            if (components.Count > 0)
            {
                data["WorkProductComponents"] = components;
            }

            if (datasets.Count > 0)
            {
                data["Datasets"] = datasets;
            }

            manifest["Data"] = data;
        }

        return manifest;
    }

    /// <summary>The execution context of an inline manifest: the Payload and the manifest itself.</summary>
    private JsonObject BuildContext(List<Staged> group, JsonObject manifest)
    {
        var payload = new JsonObject { ["AppKey"] = _options.WorkflowAppKey };
        if (_client.Header("data-partition-id") is { } partition)
        {
            payload["data-partition-id"] = partition;
        }

        foreach (var (name, value) in _options.WorkflowPayload)
        {
            payload[name] = value;
        }

        return new JsonObject { ["Payload"] = payload, ["manifest"] = manifest };
    }

    /// <summary>
    /// Triggers one run for the group and reports the manifest step, with the run id, on every record before the
    /// poll starts. The run id is chosen here, so a request the service accepted before a retry resent it answers
    /// 409 and is polled, not run twice. A manifest goes by reference when the flow says so
    /// (<see cref="ProtocolOptions.ManifestByReference"/>): stored as a dataset, and named by its id.
    /// </summary>
    private async Task<(WorkflowRun Run, string? ManifestDataset)> TriggerAsync(List<Staged> group, CancellationToken ct)
    {
        var runId = Guid.NewGuid().ToString("D");
        var manifest = BuildManifest(group);
        var context = BuildContext(group, manifest);
        var workflow = _options.WorkflowName;
        string? manifestDataset = null;
        var byReference = _options.ManifestByReference switch
        {
            ManifestReference.Always => true,
            ManifestReference.Auto => RequestKilobytes(context) > _options.ManifestInlineLimitKb && await ByReferenceRegisteredAsync(ct).ConfigureAwait(false),
            _ => false,
        };
        if (byReference)
        {
            if (_options.ManifestByReference == ManifestReference.Always && !await ByReferenceRegisteredAsync(ct).ConfigureAwait(false))
            {
                throw new DeliveryException(
                    $"the flow sends every manifest by reference, and this partition's Workflow service has no workflow named {_options.ByReferenceWorkflowName}; not every deployment registers it (osdu/specs/workflows/INTEGRATION.md section 3.3)");
            }

            workflow = _options.ByReferenceWorkflowName;
            manifestDataset = ManifestDatasetId(runId);
        }

        // Before anything of the run is sent: the record the run is to write, and the manifest stored for it, are the unit's,
        // so a run whose trigger or poll is lost is still waited out and its record taken back when the delivery does not
        // complete. The manifest dataset stays an intent until the route removes it.
        await Task.WhenAll(group.Select(s => s.Work.ReportStepAsync(
            "manifest-intent",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["runId"] = runId, [WorkflowValue] = workflow },
            RunArtifacts(s, workflow, runId, manifestDataset),
            ct))).ConfigureAwait(false);

        if (byReference)
        {
            context = await StoreManifestAsync(group, manifest, context, runId, manifestDataset!, ct).ConfigureAwait(false);
        }

        var run = await _workflows.TriggerAsync(workflow, runId, context, ct).ConfigureAwait(false);
        var started = run.Started;
        var values = run.Values();
        values[WorkflowValue] = workflow;
        values["records"] = group.Count.ToString(CultureInfo.InvariantCulture);
        if (manifestDataset is not null)
        {
            values[ManifestDatasetValue] = manifestDataset;
        }

        // The run was triggered for every record of the group, so their steps are reported together, and the worker writes
        // them to the ledger in one go before the run is polled.
        var reports = new List<Task>(group.Count);
        foreach (var staged in group)
        {
            var mine = new Dictionary<string, string>(values, StringComparer.Ordinal);
            if (staged.PriorVersion is { } prior)
            {
                mine[PriorVersionValue] = prior.ToString(CultureInfo.InvariantCulture);
            }

            staged.Steps.Add(ManifestStep, started, run.TriggerStatus, mine);
            reports.Add(staged.Work.ReportStepAsync(ManifestStep, mine, ct));
        }

        await Task.WhenAll(reports).ConfigureAwait(false);
        _logger.LogInformation("Workflow run {RunId} of {Workflow} triggered for {Count} record(s) ({Status}).", run.RunId, workflow, group.Count, run.Status);
        return (run, manifestDataset);
    }

    /// <summary>
    /// Stores a manifest as a <c>dataset--File.Generic</c> through the Dataset service, under an id derived from the run's
    /// (so a retry of the same run stores it again under the same id), with the group's first access and legal blocks at
    /// the manifest's top level, which the workflow copies onto the files it writes itself; and returns the context that
    /// names it (osdu/specs/workflows/INTEGRATION.md section 3.3).
    /// </summary>
    private async Task<JsonObject> StoreManifestAsync(List<Staged> group, JsonObject manifest, JsonObject inline, string runId, string datasetId, CancellationToken ct)
    {
        var first = group[0].Document;
        var acl = first["acl"]?.DeepClone() ?? throw new DeliveryException("the records have no acl block to give the manifest");
        var legal = first["legal"]?.DeepClone() ?? throw new DeliveryException("the records have no legal block to give the manifest");
        var entityType = TargetId.EntityTypeFromKind(_options.DatasetKind);
        var file = (JsonObject)manifest.DeepClone();
        file["acl"] = acl.DeepClone();
        file["legal"] = legal.DeepClone();
        var bytes = Encoding.UTF8.GetBytes(file.ToJsonString());

        var storage = await _datasets.StorageAsync(entityType, ct).ConfigureAwait(false);
        var signed = DatasetUploads.SignedUrl(storage, "signedUrl");
        var fileSource = storage.Text("fileSource") ?? throw new DeliveryException("the dataset service's storage location names no fileSource for the manifest.");
        await _client.SendToSignedUrlAsync(
            HttpMethod.Put, signed, () => new MemoryStream(bytes, writable: false), "application/json", bytes.LongLength,
            FileUploads.SignedUploadHeaders(signed, _options.UploadHeaders), ct).ConfigureAwait(false);

        var name = $"manifest-{runId}.json";
        var record = new JsonObject
        {
            ["id"] = datasetId,
            ["kind"] = _options.DatasetKind,
            ["acl"] = acl.DeepClone(),
            ["legal"] = legal.DeepClone(),
            ["data"] = new JsonObject { ["Name"] = name },
        };
        DatasetUploads.Point(record, new StagedDataset(fileSource, null, [(name, bytes.LongLength)]));
        await _datasets.RegisterAsync([record], ct).ConfigureAwait(false);
        _logger.LogInformation("The manifest of {Count} record(s) is stored as {DatasetId} ({Bytes} bytes) and sent by reference.", group.Count, datasetId, bytes.LongLength);

        return new JsonObject
        {
            ["Payload"] = inline["Payload"]!.DeepClone(),
            ["acl"] = acl,
            ["legal"] = legal,
            ["manifest"] = datasetId,
        };
    }

    /// <summary>The id a run's manifest is stored under when it goes by reference: derived from the run's, so a retry of the run stores it again under the same id.</summary>
    private string ManifestDatasetId(string runId)
    {
        var partition = _client.Header("data-partition-id") ?? throw new DeliveryException("the flow names no data-partition-id to store the manifest in");
        return $"{partition}:{TargetId.EntityTypeFromKind(_options.DatasetKind)}:osdu-delivery-manifest-{runId}";
    }

    /// <summary>
    /// What a run is to make for one record, as intents: the record itself (new when storage held no version of it before the
    /// run, else a version over the one it held), and the manifest stored for the run.
    /// </summary>
    private static IReadOnlyList<TargetArtifact> RunArtifacts(Staged staged, string workflow, string runId, string? manifestDataset)
    {
        var record = new TargetArtifact
        {
            Slot = RecordSlot,
            Role = staged.PriorVersion is null ? ArtifactRoles.Record : ArtifactRoles.Version,
            TargetId = staged.Work.TargetId,
            PriorVersion = staged.PriorVersion,
            Locator = RunLocator(workflow, runId),
            Status = ArtifactStatus.Intent,
        };
        // Each run is named under a slot of its own, so the run a later try triggers never hides one whose trigger answer was lost.
        var run = WorkflowRuns.Artifact(workflow, runId);
        return manifestDataset is null
            ? [record, run]
            : [record, run, TargetArtifact.Intent(ManifestSlot(runId), ArtifactRoles.Dataset, null, manifestDataset)];
    }

    /// <summary>Polls the run until it finishes, or the flow's timeout passes (the next try resumes the same run).</summary>
    private Task<WorkflowRun> PollAsync(string workflow, string runId, DateTime started, CancellationToken ct)
        => _workflows.PollAsync(
            workflow,
            runId,
            started,
            TimeSpan.FromSeconds(Math.Max(1, _options.WorkflowPollSeconds)),
            TimeSpan.FromMinutes(Math.Max(1, _options.WorkflowTimeoutMinutes)),
            ct);

    /// <summary>
    /// Reads the group's records back from storage after the run finished. The ones storage holds settle as
    /// delivered with their version; the ones storage asked to retry fail for this try; the rest are returned to
    /// the caller, which decides whether they go into a new run now or on the next try.
    /// </summary>
    private async Task<(List<Staged> Missing, Dictionary<string, string> Notes)> SettleAsync(List<Staged> group, WorkflowRun run, DeliveryOutcome[] outcomes, CancellationToken ct)
    {
        var read = await ReadRecordsAsync(group, "data." + _options.DatasetsProperty, ct).ConfigureAwait(false);
        var runValues = run.Values();
        var completed = _time.GetUtcNow().UtcDateTime;
        var missing = new List<Staged>();
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var staged in group)
        {
            var id = staged.Work.TargetId;
            staged.Steps.Add(WorkflowStep, run.Started, 200, runValues);
            var present = read.Versions.TryGetValue(id, out var version);
            var unchanged = present && version is { } observed && staged.PriorVersion == observed;
            if (present && !unchanged)
            {
                var recordValues = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = id };
                if (version is { } v)
                {
                    recordValues["version"] = v.ToString(CultureInfo.InvariantCulture);
                }

                staged.Steps.Add(RecordsStep, completed, read.Status, recordValues);
                var returned = new Dictionary<string, string>(runValues, StringComparer.Ordinal);
                foreach (var (name, value) in recordValues)
                {
                    returned[name] = value;
                }

                if (staged.Ids.Count > 0)
                {
                    returned[FileUploads.DatasetIdsValue] = string.Join(",", staged.Ids);
                }

                if (staged.Files > 0)
                {
                    returned["files"] = staged.Files.ToString(CultureInfo.InvariantCulture);
                }

                var superseded = staged.Work.DeliverPayload
                    ? FileUploads.DatasetIds(staged.Work.TargetState).Except(staged.Ids, StringComparer.Ordinal).ToList()
                    : [];

                OwnedContent.Record(returned, staged.Document, staged.Preserved, staged.Work.TargetState);

                // The run wrote the record: its intent is a record the unit made, which the unit's commit ends.
                var written = new TargetArtifact
                {
                    Slot = RecordSlot,
                    Role = staged.PriorVersion is null ? ArtifactRoles.Record : ArtifactRoles.Version,
                    TargetId = id,
                    Version = version,
                    PriorVersion = staged.PriorVersion,
                    Locator = RunLocator(run.Workflow, run.RunId),
                };
                await staged.Work.ReportStepAsync(RecordsStep, recordValues, [written], ct).ConfigureAwait(false);
                outcomes[staged.Index] = new DeliveryOutcome
                {
                    MetadataDelivered = true,
                    PayloadDelivered = staged.Work.DeliverPayload,
                    TargetVersion = version ?? staged.Work.ExistingVersion,
                    ChunksSent = staged.Files,
                    Detail = $"workflow run {run.RunId} {run.Status}",
                    Returned = returned,
                    Steps = staged.Steps.Steps,
                    Superseded = superseded,
                };
            }
            else if (!present && read.Retry.Contains(id))
            {
                var reason = $"storage asked for a retry when {id} was read back after workflow run {run.RunId}";
                staged.Steps.Add(RecordsStep, completed, read.Status, null, reason);
                outcomes[staged.Index] = DeliveryOutcome.Failed(new DeliveryException(reason), staged.Steps.Steps);
            }
            else
            {
                // Not written by this run: absent; named under invalidRecords, which is how storage answers a read of a
                // record it does not hold (a live M26 service does; the OpenAPI description does not say what the list
                // means); or still at the version it held before the run was triggered, which is what a finished run that
                // dropped the record leaves (observed live: a record whose dataset the search index did not list yet).
                // Each goes into a new run rather than reading the same finished run back on every try.
                missing.Add(staged);
                notes[id] = unchanged
                    ? string.Create(CultureInfo.InvariantCulture, $"storage still holds version {version} of {id}, the one it held before the run was triggered")
                    : read.Invalid.Contains(id)
                        ? $"{id} is not in storage (storage names the id under invalidRecords, which is how it answers for a record it does not hold)"
                        : $"{id} is not in storage";
            }
        }

        return (missing, notes);
    }

    /// <summary>
    /// The version storage holds of each record before its run is triggered, carried on the manifest step so that a run
    /// a later try resumes is judged against it too. Ingestion finishes a run that dropped a record, and a record that
    /// already existed is still present afterwards, so only a version that moved shows the run wrote it. An update of a
    /// record the ledger holds then carries the data keys another system writes (the flow's <c>preserveDataKeys</c>, and
    /// the run state External Data Services writes on a data job) from the version storage holds into the manifest, as the
    /// storage route does, read in one batched read of those keys.
    /// </summary>
    private async Task ReadPriorVersionsAsync(List<Staged> group, CancellationToken ct)
    {
        var read = await ReadRecordsAsync(group, "id", ct).ConfigureAwait(false);
        foreach (var staged in group)
        {
            staged.PriorVersion = read.Versions.TryGetValue(staged.Work.TargetId, out var version) ? version : null;
        }

        var updates = group.Where(s => s.Preserved.Count > 0 && s.Work.ExistingVersion is not null && s.PriorVersion is not null).ToList();
        if (updates.Count == 0)
        {
            return;
        }

        var attributes = updates.SelectMany(s => s.Preserved).Distinct(StringComparer.Ordinal).Select(key => "data." + key).ToList();
        var stored = await RecordWriter.ReadManyAsync(
            _client, _options.RecordQueryPath ?? DefaultRecordQueryPath, updates.Select(s => s.Work.TargetId).ToList(), attributes, ct).ConfigureAwait(false);
        foreach (var staged in updates)
        {
            if (stored.TryGetValue(staged.Work.TargetId, out var record))
            {
                RecordWriter.Preserve(record, staged.Document, staged.Preserved, StepLinks.Of(staged.Work));
            }
        }
    }

    /// <summary>One batched read of the group's records from storage (openapi storage v2, POST query/records), projected to <paramref name="attribute"/>.</summary>
    private async Task<RecordRead> ReadRecordsAsync(List<Staged> group, string attribute, CancellationToken ct)
    {
        var url = _client.Url(_options.RecordQueryPath ?? DefaultRecordQueryPath);
        var read = new RecordRead();
        foreach (var chunk in group.Chunk(QueryBatch))
        {
            var body = new JsonObject
            {
                ["records"] = new JsonArray(chunk.Select(s => (JsonNode?)JsonValue.Create(s.Work.TargetId)).ToArray()),
                ["attributes"] = new JsonArray(JsonValue.Create(attribute)),
            };
            var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, null, ct, idempotent: true).ConfigureAwait(false);
            read.Status = (int)result.Status;
            var root = OsduHttpClient.ParseJson(result, url);
            foreach (var record in JsonPathReader.SelectElements(root, "records[*]"))
            {
                if (record.ValueKind == JsonValueKind.Object && record.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String)
                {
                    read.Versions[idNode.GetString()!] = record.TryGetProperty("version", out var versionNode) && versionNode.ValueKind == JsonValueKind.Number && versionNode.TryGetInt64(out var version)
                        ? version
                        : null;
                }
            }

            foreach (var id in JsonPathReader.SelectValues(root, "retryRecords[*]"))
            {
                read.Retry.Add(id);
            }

            foreach (var id in JsonPathReader.SelectValues(root, "invalidRecords[*]"))
            {
                read.Invalid.Add(id);
            }
        }

        return read;
    }

    /// <summary>
    /// Waits, up to <see cref="ProtocolOptions.DatasetIndexWaitSeconds"/>, until the search index lists the datasets
    /// registered for the group's records (openapi search v2, POST query), asking every <c>workflowPollSeconds</c>.
    /// Ingestion checks a record's references against the index and drops a record whose dataset it cannot find yet,
    /// while the run still finishes: on a live M26 service a manifest sent a second after registration lost its record,
    /// and the same manifest sent once the index listed the dataset wrote it. A wait that runs out is recorded on the
    /// step and the run goes ahead; the read-back decides what the run wrote.
    /// </summary>
    private async Task WaitForDatasetsAsync(List<Staged> group, CancellationToken ct)
    {
        var waiting = group.Where(s => s.Work.DeliverPayload && s.Ids.Count > 0).ToList();
        if (waiting.Count == 0 || _options.DatasetIndexWaitSeconds <= 0)
        {
            return;
        }

        var ids = waiting.SelectMany(s => s.Ids).Distinct(StringComparer.Ordinal).ToList();
        var url = _client.Url(_options.SearchQueryPath ?? DefaultSearchQueryPath);
        var started = _time.GetUtcNow();
        var deadline = started + TimeSpan.FromSeconds(_options.DatasetIndexWaitSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.WorkflowPollSeconds));
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var status = 0;
        while (true)
        {
            foreach (var chunk in ids.Where(id => !listed.Contains(id)).Chunk(SearchBatch))
            {
                var body = new JsonObject
                {
                    ["kind"] = _options.DatasetKind,
                    ["query"] = "id:(" + string.Join(" OR ", chunk.Select(id => "\"" + id + "\"")) + ")",
                    ["limit"] = chunk.Length,
                    ["returnedFields"] = new JsonArray(JsonValue.Create("id")),
                };
                var result = await _client.SendJsonAsync(HttpMethod.Post, url, body, null, ct, idempotent: true).ConfigureAwait(false);
                status = (int)result.Status;
                foreach (var hit in JsonPathReader.SelectElements(OsduHttpClient.ParseJson(result, url), "results[*]"))
                {
                    if (hit.ValueKind == JsonValueKind.Object && hit.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String)
                    {
                        listed.Add(idNode.GetString()!);
                    }
                }
            }

            if (ids.All(listed.Contains) || _time.GetUtcNow() + interval > deadline)
            {
                break;
            }

            await Task.Delay(interval, _time, ct).ConfigureAwait(false);
        }

        var waited = (long)(_time.GetUtcNow() - started).TotalSeconds;
        foreach (var staged in waiting)
        {
            var notListed = staged.Ids.Count(id => !listed.Contains(id));
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["datasets"] = staged.Ids.Count.ToString(CultureInfo.InvariantCulture),
                ["listed"] = (staged.Ids.Count - notListed).ToString(CultureInfo.InvariantCulture),
                ["waitedSeconds"] = waited.ToString(CultureInfo.InvariantCulture),
            };
            var note = notListed == 0
                ? null
                : string.Create(CultureInfo.InvariantCulture, $"{notListed} registered dataset(s) not listed by the search index after {waited}s; the manifest goes ahead and the read-back decides what the run wrote");
            staged.Steps.Add(IndexedStep, started.UtcDateTime, status, values, note);
        }

        var unlisted = ids.Where(id => !listed.Contains(id)).ToList();
        if (unlisted.Count > 0)
        {
            _logger.LogWarning(
                "The search index did not list {Count} registered dataset(s) within {Seconds}s; the manifest goes ahead. First: {Ids}",
                unlisted.Count, waited, string.Join(", ", unlisted.Take(5)));
        }
    }

    /// <summary>What one batched read of records returned: versions by id, the ids storage asked to retry and the ids it listed as invalid.</summary>
    private sealed class RecordRead
    {
        public Dictionary<string, long?> Versions { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Retry { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Invalid { get; } = new(StringComparer.Ordinal);

        public int Status { get; set; }
    }

    /// <summary>One record of a batch as its manifest carries it; <c>Preserved</c> names the data keys an update carries forward from the version storage holds.</summary>
    private sealed record Staged(int Index, DeliveryWork Work, JsonObject Document, string Section, List<string> Ids, DeliverySteps Steps, int Files, IReadOnlyList<string> Preserved)
    {
        /// <summary>The version storage held of the record before its run was triggered; null when it held none or it is not known.</summary>
        public long? PriorVersion { get; set; }
    }

}
