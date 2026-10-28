using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Engine.Workflows;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The workflow route (osdu/docs/reference/flow/routes.md, workflow; osdu/specs/workflows/INTEGRATION.md section 3.13).
/// For each record: the record itself is written first (the anchor), registered with its files through the Dataset
/// service or written through storage; the workflow's inputs are registered through the Dataset service under ids
/// derived from the anchor's; then each stage's workflow is triggered with its execution context filled in, checked
/// against the workflow's payload contract, and polled; what a stage produces feeds the next; and what the runs created
/// is found the way the route declares and read back from storage, since a finished run is not a complete one
/// (section 6.3). Every step resumes on a retry: a registration lands on the same ids, a run triggered before an
/// interruption is polled rather than triggered again, and a finished run's outputs are kept.
/// </summary>
public sealed class OsduWorkflowProtocol : IDeliveryProtocol
{
    public const string AnchorStep = "anchor";
    public const string ResultsStep = "results";

    /// <summary>The target state value listing the dataset ids registered for an input, comma separated.</summary>
    public static string InputValue(string input) => "input." + input;

    public static string StageStep(int stage) => "stage-" + stage.ToString(CultureInfo.InvariantCulture);

    /// <summary>The values the anchor step keeps of what the storage write recorded about the flow's own content.</summary>
    private static readonly string[] OwnedValues = [OwnedContent.HashValue, OwnedContent.ExcludedValue];

    /// <summary>The most ids a search for a run's results pages through.</summary>
    public const int MaxSearchResults = 100_000;

    private const int SearchPage = 1000;

    /// <summary>The end of the plain search path, and what its cursor form adds to it.</summary>
    private const string PlainQuery = "/query";

    private const string CursorSuffix = "_with_cursor";

    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly WorkflowRoute _route;
    private readonly ILogger _logger;
    private readonly ISecretResolver _secrets;
    private readonly IWorkflowXCom _xcom;
    private readonly long _requestBodyCeiling;
    private readonly TimeProvider _time;
    private readonly WorkflowClient _workflows;
    private readonly DatasetService _datasets;
    private readonly OsduRecordProtocol _records;

    public OsduWorkflowProtocol(
        OsduHttpClient client, ProtocolOptions options, WorkflowRoute route, ILogger logger, ISecretResolver secrets,
        IWorkflowXCom? xcom = null, long requestBodyCeiling = 0, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(secrets);
        _client = client;
        _options = options.ForFiles(besideBulk: false);
        _route = route;
        _logger = logger;
        _secrets = secrets;
        _xcom = xcom ?? new LatestInfoXCom(client);
        _requestBodyCeiling = requestBodyCeiling;
        _time = time ?? TimeProvider.System;
        _workflows = new WorkflowClient(client, logger, _time, options.WorkflowRunPath, options.WorkflowStatusPath, options.WorkflowPath);
        _datasets = new DatasetService(client, options);
        _records = new OsduRecordProtocol(client, options, _time);
    }

    public DeliveryProtocol Kind => DeliveryProtocol.Workflow;

    /// <summary>Inputs and the anchor are written before the runs, and the runs write records before the results are found.</summary>
    public bool Undoes => true;

    /// <summary>The step the anchor's intent is reported under, before the anchor is written.</summary>
    public const string AnchorIntentStep = "anchor-intent";

    /// <summary>The slot of a dataset the route registers for an input, or for a storage anchor's files: one per registration name.</summary>
    public static string DatasetSlot(string name) => "dataset:" + name;

    /// <summary>The slot of a run the route triggered.</summary>
    public static string RunSlot(string runId) => WorkflowRuns.Slot(runId);

    /// <summary>The slot of a record the runs created that the results found: a digest of its id, which may be longer than a slot.</summary>
    public static string OutputSlot(string id)
        => "output:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..24];

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var steps = new DeliverySteps(_time);
        var returned = new Dictionary<string, string>(StringComparer.Ordinal);
        var partition = _client.Header("data-partition-id")
            ?? throw new RecordHeldException("the flow names no data-partition-id, which every workflow context carries");
        try
        {
            var anchorFiles = work.DeliverPayload ? work.Parts.FirstOrDefault(p => p.Payload == PayloadParts.Files) : null;
            var sendsAnchorFiles = anchorFiles is not null && work.Sends(anchorFiles);
            var inputs = work.DeliverPayload ? work.Parts.Where(p => p.Payload != PayloadParts.Files).ToList() : [];
            var writesAnchor = work.DeliverMetadata || sendsAnchorFiles;

            // The anchor first: the record the runs are started for, as the route writes it.
            var document = (JsonObject)work.Document.DeepClone();
            var id = work.TargetId;
            if (_route.AnchorTagKey is { } tagKey)
            {
                if (document["tags"] is not JsonObject tags)
                {
                    tags = new JsonObject();
                    document["tags"] = tags;
                }

                tags[tagKey] = WorkflowValues.Tag(id);
            }

            var values = new WorkflowValues(partition, _options.WorkflowAppKey, document);
            var sentInputs = false;
            var registered = await InputsAsync(work, document, inputs, values, steps, returned, ct).ConfigureAwait(false);
            sentInputs = registered.Sent;

            long? version = work.ExistingVersion;
            if (writesAnchor)
            {
                version = await WriteAnchorAsync(work, document, anchorFiles, sendsAnchorFiles, registered.AnchorFileIds, steps, returned, ct).ConfigureAwait(false) ?? version;
            }

            if (_route.Anchor == WorkflowAnchor.Dataset)
            {
                values.SetInput(PayloadParts.Files, [id]);
            }
            else if (registered.AnchorFileIds is { } fileIds)
            {
                values.SetInput(PayloadParts.Files, fileIds);
            }

            var runs = _route.RunWhen switch
            {
                WorkflowRunWhen.Created => work.ExistingVersion is null || work.Forces(PayloadParts.Workflow),
                WorkflowRunWhen.Requested => work.ExistingVersion is not null && work.Forces(PayloadParts.Workflow),
                _ => writesAnchor || sentInputs || work.Forces(PayloadParts.Workflow),
            };

            var detail = new List<string>();
            if (runs)
            {
                foreach (var secret in _route.Secrets)
                {
                    values.SetSecret(secret.Key, await _secrets.ResolveAsync(secret.Value, ct).ConfigureAwait(false));
                }

                for (var n = 1; n <= _route.Stages.Count; n++)
                {
                    var run = await StageAsync(work, n, values, steps, ct).ConfigureAwait(false);
                    returned[$"stage.{n.ToString(CultureInfo.InvariantCulture)}.runId"] = run.RunId;
                    returned[$"stage.{n.ToString(CultureInfo.InvariantCulture)}.status"] = run.Status;
                    detail.Add($"{run.Workflow} run {run.RunId} {run.Status}");
                }

                var found = await ResultsAsync(work, values, steps, ct).ConfigureAwait(false);
                foreach (var (name, value) in found)
                {
                    returned[name] = value;
                }

                // A workflow can write the anchor itself (a conversion adds an artefact to it), so its version is read back:
                // the ledger holds the version OSDU serves once the runs are done.
                var landed = await RecordWriter.VerifyAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, id, null, ct).ConfigureAwait(false);
                version = landed.ObservedVersion
                    ?? throw new DeliveryException($"the workflow runs for {id} finished, and the record could not be read back: {landed.Detail}");
                returned[PayloadParts.StateKey(PayloadParts.Workflow)] = string.Join(",", _route.Stages.Select((s, i) => returned[$"stage.{(i + 1).ToString(CultureInfo.InvariantCulture)}.runId"]));
                if (found.TryGetValue(RecordsValue, out var count))
                {
                    detail.Add($"{count} record(s) found");
                }
            }

            returned["recordId"] = id;
            if (version is { } v)
            {
                returned["version"] = v.ToString(CultureInfo.InvariantCulture);
            }

            if (sendsAnchorFiles)
            {
                returned[PayloadParts.StateKey(anchorFiles!.Payload)] = anchorFiles.Hash;
            }

            if (!runs)
            {
                detail.Add(_route.RunWhen switch
                {
                    WorkflowRunWhen.Requested => "the record is written; its workflow runs when a redelivery names it",
                    WorkflowRunWhen.Created => "the record is written; its workflow ran when the record was created",
                    _ => "nothing the workflow reads changed, so it was not run",
                });
            }

            return new DeliveryOutcome
            {
                MetadataDelivered = writesAnchor,
                PayloadDelivered = sendsAnchorFiles || sentInputs || runs,
                TargetVersion = version,
                Detail = string.Join("; ", detail),
                Returned = returned,
                Steps = steps.Steps,
            };
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
        {
            return DeliveryOutcome.Failed(ex, steps.Steps);
        }
    }

    /// <summary>The target state value holding how many records the runs created were found.</summary>
    public const string RecordsValue = "workflow.records";

    /// <summary>The target state value holding the ids found, comma separated, up to the declared number.</summary>
    public const string RecordIdsValue = "workflow.recordIds";

    /// <summary>The target state values a search for the results is repeated from when a removal needs every id.</summary>
    public const string SearchKindValue = "workflow.searchKind";

    public const string SearchQueryValue = "workflow.searchQuery";

    private sealed record Registered(bool Sent, IReadOnlyList<string>? AnchorFileIds);

    /// <summary>
    /// Registers each input's files through the Dataset service under ids derived from the anchor's (one dataset per file,
    /// or one collection), sets them on the values the contexts read, and, for a storage anchor, its own files the same
    /// way, so its dataset list can name them.
    /// </summary>
    private async Task<Registered> InputsAsync(
        DeliveryWork work, JsonObject anchor, IReadOnlyList<WorkPayloadPart> inputs, WorkflowValues values, DeliverySteps steps, Dictionary<string, string> returned, CancellationToken ct)
    {
        var sent = false;
        foreach (var declared in _route.Inputs)
        {
            var part = inputs.FirstOrDefault(p => p.Payload == declared.Name);
            IReadOnlyList<string> ids;
            if (part is not null && work.Sends(part))
            {
                ids = part.Source is null
                    ? []
                    : await RegisterFilesAsync(work, anchor, part, declared.DatasetKind, steps, ct).ConfigureAwait(false);
                returned[PayloadParts.StateKey(part.Payload)] = part.Hash;
                sent = true;
            }
            else
            {
                ids = Ids(work.TargetState, InputValue(declared.Name));
            }

            returned[InputValue(declared.Name)] = string.Join(",", ids);
            values.SetInput(declared.Name, ids);
        }

        IReadOnlyList<string>? anchorFiles = null;
        if (_route.Anchor == WorkflowAnchor.Storage)
        {
            var files = work.DeliverPayload ? work.Parts.FirstOrDefault(p => p.Payload == PayloadParts.Files) : null;
            if (files is not null && work.Sends(files) && files.Source is not null)
            {
                anchorFiles = await RegisterFilesAsync(work, anchor, files, _options.DatasetKind, steps, ct).ConfigureAwait(false);
                sent = true;
            }
            else if (Ids(work.TargetState, FileUploads.DatasetIdsValue) is { Count: > 0 } known)
            {
                anchorFiles = known;
            }

            if (anchorFiles is not null)
            {
                returned[FileUploads.DatasetIdsValue] = string.Join(",", anchorFiles);
            }
        }

        return new Registered(sent, anchorFiles);
    }

    /// <summary>
    /// The files of one part registered as datasets of <paramref name="kind"/> with the anchor's access and legal
    /// blocks: one collection holding them all for a collection kind, one dataset per file otherwise.
    /// </summary>
    private async Task<IReadOnlyList<string>> RegisterFilesAsync(DeliveryWork work, JsonObject anchor, WorkPayloadPart part, string kind, DeliverySteps steps, CancellationToken ct)
    {
        var entityType = TargetId.EntityTypeFromKind(kind);
        var step = "register-" + part.Payload;
        if (work.Completed(step) is { } done && done.TryGetValue("ids", out var known) && !string.IsNullOrEmpty(known))
        {
            steps.Resumed(step, done);
            return known.Split(',', StringSplitOptions.RemoveEmptyEntries);
        }

        var source = part.Source!;
        var records = new List<JsonObject>();
        var names = new List<string>();
        if (DatasetService.IsCollectionType(entityType))
        {
            var staged = await DatasetUploads.UploadAsync(_client, _datasets, _options, work, part.Payload, source, entityType, _requestBodyCeiling, steps, ct).ConfigureAwait(false);
            var record = InputRecord(anchor, kind, WorkflowValues.DerivedDatasetId(work.TargetId, entityType, part.Payload));
            DatasetUploads.Point(record, staged);
            records.Add(record);
            names.Add(part.Payload);
        }
        else
        {
            // One dataset per file: each file is its own stored and registered dataset.
            var chunks = await source.ListChunksAsync(ct).ConfigureAwait(false);
            foreach (var chunk in chunks)
            {
                var single = new SingleFile(source, chunk);
                var slot = part.Payload + "-" + chunk.Index.ToString(CultureInfo.InvariantCulture);
                var staged = await DatasetUploads.UploadAsync(_client, _datasets, _options, work, slot, single, entityType, _requestBodyCeiling, steps, ct).ConfigureAwait(false);
                var record = InputRecord(anchor, kind, WorkflowValues.DerivedDatasetId(work.TargetId, entityType, slot));
                DatasetUploads.Point(record, staged);
                records.Add(record);
                names.Add(slot);
            }
        }

        var started = steps.Now;
        var ids = records.Select(r => r["id"]!.GetValue<string>()).ToList();

        // What the registration writes, before it is sent: its answer can be lost after it landed.
        var declared = await DeclareAsync(work, step + "-intent", ids.Select((id, i) => new Registration(DatasetSlot(names[i]), id)).ToList(), ct).ConfigureAwait(false);
        var landed = await _datasets.RegisterAsync(records, ct).ConfigureAwait(false);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ids"] = string.Join(",", ids),
            ["datasets"] = ids.Count.ToString(CultureInfo.InvariantCulture),
        };
        steps.Add(step, started, 201, values);
        await work.ReportStepAsync(step, values, declared.Select(d => RegistrationIntents.Landed(d, landed.GetValueOrDefault(d.TargetId!))).ToList(), ct).ConfigureAwait(false);
        _logger.LogDebug("Registered {Count} input dataset(s) for {TargetId} ({Versions}).", landed.Count, work.TargetId, string.Join(", ", landed.Values));
        return ids;
    }

    /// <summary>
    /// Reports what registrations under ids the route chose are about to write (<see cref="RegistrationIntents"/>), under
    /// <paramref name="step"/>, before they are sent; storage that cannot say what one would replace stops them all.
    /// </summary>
    private async Task<IReadOnlyList<TargetArtifact>> DeclareAsync(DeliveryWork work, string step, IReadOnlyList<Registration> registrations, CancellationToken ct)
    {
        var intents = await RegistrationIntents.DeclareAsync(_records, registrations, ct).ConfigureAwait(false);
        if (intents.FirstOrDefault(i => i.Artifact is null) is { } refused)
        {
            throw new DeliveryException(refused.Refusal!);
        }

        var declared = intents.Select(i => i.Artifact!).ToList();
        await work.ReportStepAsync(step, new Dictionary<string, string>(StringComparer.Ordinal) { ["ids"] = string.Join(",", registrations.Select(r => r.Id)) }, declared, ct).ConfigureAwait(false);
        return declared;
    }

    /// <summary>A dataset record for an input, carrying the anchor's access and legal blocks, never restating them.</summary>
    private static JsonObject InputRecord(JsonObject anchor, string kind, string id)
        => new()
        {
            ["id"] = id,
            ["kind"] = kind,
            ["acl"] = anchor["acl"]?.DeepClone() ?? throw new DeliveryException("the anchor record has no acl block to copy onto its input datasets"),
            ["legal"] = anchor["legal"]?.DeepClone() ?? throw new DeliveryException("the anchor record has no legal block to copy onto its input datasets"),
            ["data"] = new JsonObject(),
        };

    /// <summary>
    /// Writes the anchor. A dataset anchor with new files is registered through the Dataset service with its files; one
    /// whose files are unchanged is written through storage with the <c>DatasetProperties</c> OSDU holds, because a
    /// registration copies the staging area again and the staged files may be gone. A storage anchor is written through
    /// storage, its dataset list pointing at its own registered files.
    /// </summary>
    private async Task<long?> WriteAnchorAsync(
        DeliveryWork work, JsonObject document, WorkPayloadPart? files, bool sendsFiles, IReadOnlyList<string>? fileIds, DeliverySteps steps, Dictionary<string, string> returned, CancellationToken ct)
    {
        if (work.Completed(AnchorStep) is { } done && done.TryGetValue("version", out var known))
        {
            steps.Resumed(AnchorStep, done);
            foreach (var key in OwnedValues)
            {
                if (done.TryGetValue(key, out var value))
                {
                    returned[key] = value;
                }
            }

            return RecordWriter.ParseVersion(known);
        }

        var started = steps.Now;
        long? version;
        TargetArtifact written;
        var owned = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_route.Anchor == WorkflowAnchor.Dataset && sendsFiles)
        {
            var entityType = TargetId.EntityTypeFromKind(document["kind"]?.GetValue<string>() ?? throw new RecordHeldException("the record has no kind"));
            var staged = await DatasetUploads.UploadAsync(_client, _datasets, _options, work, files!.Payload, files.Source!, entityType, _requestBodyCeiling, steps, ct).ConfigureAwait(false);
            DatasetUploads.Point(document, staged);
            var declared = (await DeclareAsync(work, AnchorIntentStep, [new Registration(TargetArtifact.RecordSlot, work.TargetId)], ct).ConfigureAwait(false))[0];
            var landed = await _datasets.RegisterAsync([document], ct).ConfigureAwait(false);
            version = landed.GetValueOrDefault(work.TargetId);
            written = RegistrationIntents.Landed(declared, version);

            // A registration writes the record whole and carries nothing forward, so no hash of the flow's own content stands.
            OwnedContent.Record(owned, document, [], work.TargetState);
        }
        else
        {
            if (_route.Anchor == WorkflowAnchor.Dataset && work.ExistingVersion is not null)
            {
                var stored = await RecordWriter.ReadAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, work.TargetId, ct).ConfigureAwait(false)
                    ?? throw new DeliveryException($"{work.TargetId} is not in storage, so its dataset properties cannot be carried into the rewrite; redeliver its files");
                RecordWriter.Preserve(stored, document, ["DatasetProperties"], StepLinks.None);
            }

            if (fileIds is { Count: > 0 })
            {
                FileUploads.SetDatasets(document, _options.DatasetsProperty, fileIds);
            }

            // The storage write's answer can be lost after it landed, and the runs follow it, so it is declared first.
            await work.ReportStepAsync(
                AnchorIntentStep,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId },
                [TargetArtifact.RecordWritten(work.TargetId, null, work.ExistingVersion) with { Status = ArtifactStatus.Intent }],
                ct).ConfigureAwait(false);
            var outcome = (await _records.DeliverBatchAsync([work with { Document = document, DeliverMetadata = true, DeliverPayload = false, Payload = null, Parts = [] }], ct).ConfigureAwait(false))[0];
            if (outcome.Failure is { } failure)
            {
                throw failure;
            }

            version = outcome.TargetVersion;
            written = TargetArtifact.RecordWritten(work.TargetId, version, work.ExistingVersion);
            foreach (var key in OwnedValues)
            {
                if (outcome.Returned.TryGetValue(key, out var value))
                {
                    owned[key] = value;
                }
            }
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = work.TargetId };
        if (version is { } v)
        {
            values["version"] = v.ToString(CultureInfo.InvariantCulture);
        }

        foreach (var (name, value) in owned)
        {
            values[name] = value;
        }

        steps.Add(AnchorStep, started, null, values);
        await work.ReportStepAsync(AnchorStep, values, [written], ct).ConfigureAwait(false);
        foreach (var (name, value) in values)
        {
            returned[name] = value;
        }

        return version;
    }

    /// <summary>
    /// One stage: its run triggered under a run id recorded before the request goes out (a retry sends the same id, which
    /// the service answers 409 to once it has the run), polled to its end, and its outputs read. A failed run fails the
    /// record, and the next try triggers a new one; a finished run's outputs are kept for the next try.
    /// </summary>
    private async Task<WorkflowRun> StageAsync(DeliveryWork work, int n, WorkflowValues values, DeliverySteps steps, CancellationToken ct)
    {
        var stage = _route.Stages[n - 1];
        var contract = WorkflowCatalog.Find(stage.Workflow, stage.Contract);
        var step = StageStep(n);
        var interval = TimeSpan.FromSeconds(stage.PollSeconds > 0 ? stage.PollSeconds : Math.Max(1, _options.WorkflowPollSeconds));
        var timeout = TimeSpan.FromMinutes(stage.TimeoutMinutes > 0 ? stage.TimeoutMinutes : Math.Max(Math.Max(1, _options.WorkflowTimeoutMinutes), contract?.TimeoutMinutes ?? 0));
        var earlier = work.Completed(step);
        if (earlier is not null
            && earlier.TryGetValue("state", out var state) && state == "finished"
            && earlier.TryGetValue("runId", out var finishedRun)
            && earlier.TryGetValue("outputs", out var outputsJson))
        {
            steps.Resumed(step, earlier);
            foreach (var (name, value) in JsonMerge.Parse(outputsJson))
            {
                values.SetOutput(n, name, value);
            }

            values.RunId = finishedRun;
            return new WorkflowRun(stage.Workflow, finishedRun, null, earlier.GetValueOrDefault("status") ?? "FINISHED", null, null, steps.Now);
        }

        string runId;
        DateTime started;
        if (earlier is not null && earlier.TryGetValue("runId", out var pending) && earlier.GetValueOrDefault("state") is "triggering" or "triggered")
        {
            runId = pending;
            started = steps.Now;
            if (earlier["state"] == "triggering")
            {
                // The request may or may not have reached the service: the same run id is sent again, and a 409 says it did.
                await TriggerAsync(work, n, stage, contract, values, runId, step, steps, ct).ConfigureAwait(false);
            }
            else
            {
                steps.Resumed(step, earlier);
            }
        }
        else
        {
            runId = Guid.NewGuid().ToString("D");
            started = steps.Now;
            await TriggerAsync(work, n, stage, contract, values, runId, step, steps, ct).ConfigureAwait(false);
        }

        var run = await _workflows.PollAsync(stage.Workflow, runId, started, interval, timeout, ct).ConfigureAwait(false);
        if (run.HasFailed)
        {
            var failed = new Dictionary<string, string>(run.Values(), StringComparer.Ordinal) { ["state"] = "failed", ["workflow"] = stage.Workflow };
            steps.Add(step, started, 200, failed, $"workflow run {runId} of {stage.Workflow} failed; the next try triggers a new run");
            await work.ReportStepAsync(step, failed, ct).ConfigureAwait(false);
            throw new DeliveryException($"workflow run {runId} of {stage.Workflow} failed (stage {n.ToString(CultureInfo.InvariantCulture)}); its log is in Airflow, and the next try triggers a new run");
        }

        values.RunId = runId;
        var outputs = new JsonObject();
        foreach (var (name, output) in stage.Outputs)
        {
            JsonNode? value;
            if (output.Value is { } template)
            {
                value = WorkflowTemplate.RenderString(template, values, revealSecrets: false);
            }
            else
            {
                var raw = await _xcom.ReadAsync(stage.Workflow, runId, output.XComTask!, output.XComKey!, ct).ConfigureAwait(false)
                    ?? throw new DeliveryException($"workflow run {runId} of {stage.Workflow} finished {run.Status}, and task {output.XComTask} pushed no XCom entry {output.XComKey}, which output '{name}' is read from");
                var ids = WorkflowIds.Extract(raw, output.Match);
                if (ids.Count == 0)
                {
                    throw new DeliveryException($"workflow run {runId} of {stage.Workflow} finished {run.Status}, and XCom entry {output.XComKey} of task {output.XComTask} names no record{(output.Match is null ? string.Empty : " of " + output.Match)}");
                }

                value = ids.Count == 1 ? JsonValue.Create(ids[0]) : new JsonArray(ids.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray());
            }

            values.SetOutput(n, name, value);
            outputs[name] = value?.DeepClone();
        }

        var finished = new Dictionary<string, string>(run.Values(), StringComparer.Ordinal)
        {
            ["state"] = "finished",
            ["workflow"] = stage.Workflow,
            ["outputs"] = outputs.ToJsonString(),
        };
        steps.Add(step, started, 200, finished);
        await work.ReportStepAsync(step, finished, ct).ConfigureAwait(false);
        return run;
    }

    /// <summary>
    /// Fills the stage's context, checks it against the workflow's contract, marks the step with the run id, and triggers
    /// the run. The context the step and the log keep has its secrets redacted; only the request carries them.
    /// </summary>
    private async Task TriggerAsync(
        DeliveryWork work, int n, WorkflowStage stage, WorkflowContract? contract, WorkflowValues values, string runId, string step, DeliverySteps steps, CancellationToken ct)
    {
        values.RunId = runId;
        var shown = (JsonObject)WorkflowTemplate.Render(stage.Context, values, revealSecrets: false)!;
        var addsPayload = contract?.AddsPayload ?? true;
        if (addsPayload && shown["Payload"] is null)
        {
            shown["Payload"] = new JsonObject { ["AppKey"] = values.AppKey, ["data-partition-id"] = values.Partition };
        }

        if (contract is not null && WorkflowContextCheck.CheckContext(contract, shown) is { Count: > 0 } problems)
        {
            throw new RecordHeldException($"stage {n.ToString(CultureInfo.InvariantCulture)} would send {stage.Workflow} a context its contract refuses: {string.Join("; ", problems)}");
        }

        var sent = (JsonObject)WorkflowTemplate.Render(stage.Context, values, revealSecrets: true)!;
        if (addsPayload && sent["Payload"] is null)
        {
            sent["Payload"] = shown["Payload"]!.DeepClone();
        }

        var marked = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["runId"] = runId,
            ["state"] = "triggering",
            ["workflow"] = stage.Workflow,
            ["context"] = Bounded(shown.ToJsonString()),
        };

        // The run is named before it is triggered, under the id the route chose, so an undo waits for it to end whether or not
        // the trigger's answer came back.
        await work.ReportStepAsync(step, marked, [WorkflowRuns.Artifact(stage.Workflow, runId)], ct).ConfigureAwait(false);
        var run = await _workflows.TriggerAsync(stage.Workflow, runId, sent, ct).ConfigureAwait(false);
        var triggered = new Dictionary<string, string>(run.Values(), StringComparer.Ordinal)
        {
            ["state"] = "triggered",
            ["workflow"] = stage.Workflow,
            ["context"] = marked["context"],
        };
        steps.Add(step, run.Started, run.TriggerStatus, triggered);
        await work.ReportStepAsync(step, triggered, ct).ConfigureAwait(false);
        _logger.LogInformation("Workflow run {RunId} of {Workflow} triggered for {TargetId} (stage {Stage}, {Status}).", run.RunId, stage.Workflow, work.TargetId, n, run.Status);
    }

    /// <summary>
    /// What the runs created, found the way the route declares and read back from storage. Fewer records than the route
    /// requires fails the record for this try; the runs are not repeated, only the finding. The ids are kept on the
    /// record up to the declared number, with their count, and a search is kept so a removal can repeat it. Each id kept is
    /// reported as the runs' output, found or not enough found, so an undo can reach what the runs created.
    /// </summary>
    private async Task<Dictionary<string, string>> ResultsAsync(DeliveryWork work, WorkflowValues values, DeliverySteps steps, CancellationToken ct)
    {
        var results = _route.Results;
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (results.Strategy == WorkflowResultStrategy.None)
        {
            return found;
        }

        var started = steps.Now;
        IReadOnlyList<string> ids;
        var wait = TimeSpan.FromSeconds(Math.Max(0, results.WaitSeconds ?? _options.DatasetIndexWaitSeconds));
        var deadline = _time.GetUtcNow() + wait;
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.WorkflowPollSeconds));
        while (true)
        {
            ids = await FindAsync(work, values, found, ct).ConfigureAwait(false);
            var present = ids.Count == 0 ? [] : await PresentAsync(ids, ct).ConfigureAwait(false);
            if (present.Count >= results.Minimum || _time.GetUtcNow() + interval > deadline)
            {
                ids = present;
                break;
            }

            await Task.Delay(interval, _time, ct).ConfigureAwait(false);
        }

        found[RecordsValue] = ids.Count.ToString(CultureInfo.InvariantCulture);
        found[RecordIdsValue] = string.Join(",", ids.Take(results.MaxRecorded));
        var outputs = ids
            .Where(id => !string.Equals(id, work.TargetId, StringComparison.Ordinal))
            .Take(results.MaxRecorded)
            .Select(id => TargetArtifact.Created(OutputSlot(id), ArtifactRoles.Output, id))
            .ToList();
        if (outputs.Count > 0)
        {
            await work.ReportStepAsync(ResultsStep, new Dictionary<string, string>(StringComparer.Ordinal) { [RecordsValue] = found[RecordsValue] }, outputs, ct).ConfigureAwait(false);
        }

        if (ids.Count < results.Minimum)
        {
            var reason = string.Create(
                CultureInfo.InvariantCulture,
                $"the workflow runs finished, and {ids.Count} of the {results.Minimum} record(s) the route requires were found ({results.Strategy.ToString().ToLowerInvariant()}); the next try looks again without running the workflow again");
            steps.Add(ResultsStep, started, null, found, reason);
            throw new DeliveryException(reason);
        }

        steps.Add(ResultsStep, started, null, found);
        return found;
    }

    /// <summary>The ids the declared strategy names, before they are read back.</summary>
    private async Task<IReadOnlyList<string>> FindAsync(DeliveryWork work, WorkflowValues values, Dictionary<string, string> found, CancellationToken ct)
    {
        var results = _route.Results;
        switch (results.Strategy)
        {
            case WorkflowResultStrategy.Anchor:
                return [work.TargetId];
            case WorkflowResultStrategy.Ids:
                return WorkflowIds.Extract(WorkflowTemplate.RenderString(results.Template!, values, revealSecrets: false), null);
            case WorkflowResultStrategy.XCom:
                var last = _route.Stages.Count;
                var runId = values.RunId ?? throw new DeliveryException("no workflow run to read an XCom entry of");
                var raw = await _xcom.ReadAsync(_route.Stages[last - 1].Workflow, runId, results.XCom!.XComTask!, results.XCom.XComKey!, ct).ConfigureAwait(false);
                return WorkflowIds.Extract(raw, results.XCom.Match);
            case WorkflowResultStrategy.Artefact:
                return await ArtefactsAsync(work.TargetId, ct).ConfigureAwait(false);
            case WorkflowResultStrategy.Search:
                var kind = WorkflowTemplate.RenderText(results.Kind!, values);
                var query = results.Query is null ? null : WorkflowTemplate.RenderText(results.Query, values);
                found[SearchKindValue] = kind;
                if (query is not null)
                {
                    found[SearchQueryValue] = query;
                }

                return await SearchAsync(kind, query, ct).ConfigureAwait(false);
            case WorkflowResultStrategy.Manifest:
                return await ManifestIdsAsync(WorkflowTemplate.RenderText(results.Template!, values), ct).ConfigureAwait(false);
            default:
                return [];
        }
    }

    /// <summary>
    /// The records the anchor's <c>data.Artefacts</c> entries of the declared role and kind name (the conversions add
    /// them; osdu/specs/workflows/INTEGRATION.md sections 3.9 to 3.11 and 5.2), each without its version.
    /// </summary>
    private async Task<IReadOnlyList<string>> ArtefactsAsync(string anchorId, CancellationToken ct)
    {
        var stored = await RecordWriter.ReadAsync(_client, _options.VerifyPath ?? OsduRecordProtocol.DefaultVerifyPath, anchorId, ct).ConfigureAwait(false);
        var ids = new List<string>();
        foreach (var node in stored?["data"]?["Artefacts"] as JsonArray ?? [])
        {
            if (node is not JsonObject artefact)
            {
                continue;
            }

            var role = Text(artefact, "RoleID") ?? string.Empty;
            var kind = Text(artefact, "ResourceKind") ?? string.Empty;
            var resource = Text(artefact, "ResourceID");
            if (resource is not null
                && role.Contains(":reference-data--ArtefactRole:" + _route.Results.ArtefactRole, StringComparison.OrdinalIgnoreCase)
                && kind.Contains(_route.Results.ArtefactKind!, StringComparison.OrdinalIgnoreCase))
            {
                var id = TargetId.WithoutVersion(resource);
                if (!ids.Contains(id, StringComparer.Ordinal))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// The ids a search lists (openapi search v2, POST query_with_cursor), every one of them, read through the shared cursor
    /// reader (<see cref="OsduSearch.PagesAsync"/>), which reads a search again once when its first read fails part way or
    /// comes back short and fails when the second does too. A search matching more than <see cref="MaxSearchResults"/>
    /// records fails rather than name only some of them, so neither a route's results nor a removal stops part way through
    /// a run's records without saying so. The index lags a write by at least 30 seconds, which the caller waits for.
    /// </summary>
    internal async Task<IReadOnlyList<string>> SearchAsync(string kind, string? query, CancellationToken ct)
    {
        // The flow's search path names the plain query; its cursor form is the same path with _with_cursor, and a path that
        // is not the plain query (a cursor path already, or a facade's) is used as written. The plain query is what counts a
        // search whose cursor names no exact total.
        var path = _options.SearchQueryPath ?? OsduManifestProtocol.DefaultSearchQueryPath;
        var cursorPath = path.EndsWith(PlainQuery, StringComparison.Ordinal) ? path + CursorSuffix : path;
        var queryPath = path.EndsWith(PlainQuery + CursorSuffix, StringComparison.Ordinal) ? path[..^CursorSuffix.Length] : path;
        var search = new OsduSearch(_client, queryPath, cursorPath, _logger);
        var ids = new List<string>();
        await foreach (var page in search.PagesAsync(new OsduSearchQuery { Kind = kind, Query = query, ReturnedFields = ["id"] }, SearchPage, ct).ConfigureAwait(false))
        {
            if (page.TotalCount is > MaxSearchResults and var matched)
            {
                throw TooManyFound(kind, query, matched.ToString(CultureInfo.InvariantCulture));
            }

            ids.AddRange(page.Hits.Select(hit => OsduSearch.IdOf(hit)!));
            if (ids.Count > MaxSearchResults)
            {
                throw TooManyFound(kind, query, string.Create(CultureInfo.InvariantCulture, $"more than {MaxSearchResults}"));
            }
        }

        return ids;
    }

    private static DeliveryException TooManyFound(string kind, string? query, string matched)
        => new(string.Create(CultureInfo.InvariantCulture,
            $"the search of kind {kind}{(string.IsNullOrWhiteSpace(query) ? string.Empty : $" for '{query}'")} matches {matched} records, more than the {MaxSearchResults} a route names; narrow the route's results query so it finds only the records the run made"));

    /// <summary>
    /// The ids of the records a manifest file lists (the manifest a translation writes, osdu/specs/workflows/INTEGRATION.md
    /// section 3.8): its retrieval instructions from the Dataset service, the file read from the signed URL they give, and
    /// every record id in its sections; a surrogate key is not an id and is left out.
    /// </summary>
    private async Task<IReadOnlyList<string>> ManifestIdsAsync(string datasetId, CancellationToken ct)
    {
        var retrieval = await _datasets.RetrievalAsync(datasetId, ct).ConfigureAwait(false)
            ?? throw new DeliveryException($"the dataset service lists no manifest dataset {datasetId}, so the records it names cannot be found");
        var signed = DatasetUploads.SignedUrl(new DatasetStorage(retrieval, null), "signedUrl");
        var content = await _client.GetSignedUrlAsync(signed, ct).ConfigureAwait(false);
        JsonNode? manifest;
        try
        {
            manifest = JsonNode.Parse(content);
            if (manifest is JsonValue text && text.TryGetValue<string>(out var inner))
            {
                // The by-reference operator accepts a JSON string that itself holds the manifest (section 3.3).
                manifest = JsonNode.Parse(inner);
            }
        }
        catch (JsonException ex)
        {
            throw new DeliveryException($"the manifest dataset {datasetId} does not hold JSON: {ex.Message}", ex);
        }

        var ids = new List<string>();
        foreach (var section in new[] { "ReferenceData", "MasterData" })
        {
            Collect(manifest?[section], ids);
        }

        if (manifest?["Data"] is JsonObject data)
        {
            Collect(data["WorkProduct"], ids);
            Collect(data["WorkProductComponents"], ids);
            Collect(data["Datasets"], ids);
        }

        return ids;

        static void Collect(JsonNode? node, List<string> into)
        {
            IEnumerable<JsonNode?> records = node switch
            {
                JsonArray array => array,
                JsonObject single => [single],
                _ => [],
            };
            foreach (var record in records)
            {
                if (record is JsonObject obj
                    && Text(obj, "id") is { } id
                    && !id.StartsWith("surrogate-key:", StringComparison.Ordinal)
                    && !into.Contains(id, StringComparer.Ordinal))
                {
                    into.Add(TargetId.WithoutVersion(id));
                }
            }
        }
    }

    /// <summary>
    /// Undoes what unfinished deliveries left (osdu/docs/reference/concepts/artifacts-and-undo.md, workflow), once no
    /// run they triggered is still going, since a run reads the inputs and can write the anchor until it ends: the
    /// records the runs created that the results found, removed reversibly when the route removes them and storage
    /// confirms OSDU created them after the unit began; the anchor, removed when the unit created it and given back the
    /// version before when it updated it; and the datasets registered for the inputs and a storage anchor's files the
    /// same way. A dataset anchor and the inputs are taken back even when newer work follows, since that work reads
    /// what OSDU holds of them. Records a run wrote that no result named stay, for an inventory of their kind to find.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoAsync(IReadOnlyList<UndoWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var results = new List<UndoResult>();
        foreach (var work in works)
        {
            var runs = work.Items.Where(i => i.Artifact.Role == ArtifactRoles.Run).ToList();
            var (waiting, ended) = await WorkflowRuns.SettleAsync(_workflows, runs, ct).ConfigureAwait(false);
            if (waiting is not null)
            {
                results.AddRange(work.Items.Select(i => UndoResult.Failed(i, waiting)));
                continue;
            }

            results.AddRange(runs.Select(i => UndoResult.Kept(i, ended[i.ArtifactId])));
            var outputs = work.Items.Where(i => i.Artifact.Role == ArtifactRoles.Output).ToList();
            results.AddRange(await OutputsAsync(work, outputs, ct).ConfigureAwait(false));

            var side = new RecordSide
            {
                Remove = (id, token) => RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options), id, RemovalScope.Record, token),
                Read = _records.ReadAsync,
                Restorer = _records,
                Versions = _records.VersionsAsync,
            };
            var anchor = work.Items.Where(i => ArtifactRoles.IsTheRecord(i.Artifact.Role) && i.Artifact.Slot == TargetArtifact.RecordSlot).ToList();
            var anchorSide = _route.Anchor == WorkflowAnchor.Dataset ? side with { Remove = (id, token) => _datasets.SoftDeleteAsync(id, token) } : side;
            var anchorWork = _route.Anchor == WorkflowAnchor.Dataset ? work with { KeepRecord = false } : work;
            results.AddRange(await ArtifactUndo.RecordItselfAsync(anchorWork, anchor, anchorSide, ct).ConfigureAwait(false));

            var registered = work.Items.Where(i => ArtifactRoles.IsTheRecord(i.Artifact.Role) && i.Artifact.Slot != TargetArtifact.RecordSlot).ToList();
            results.AddRange(await ArtifactUndo.RecordItselfAsync(work with { KeepRecord = false }, registered, side, ct).ConfigureAwait(false));
            results.AddRange(ArtifactUndo.Keep(work.Items.Except(runs).Except(outputs).Except(anchor).Except(registered), "the workflow route makes nothing of this kind"));
        }

        return results;
    }

    /// <summary>
    /// The records the runs created that the results found: kept when the route does not remove them with the anchor, or when
    /// OSDU created one before the unit began (a run that updated it), else removed reversibly through storage.
    /// </summary>
    private async Task<IReadOnlyList<UndoResult>> OutputsAsync(UndoWork work, IReadOnlyList<UndoItem> outputs, CancellationToken ct)
    {
        if (outputs.Count == 0)
        {
            return [];
        }

        if (!_route.Results.Remove)
        {
            return ArtifactUndo.Keep(outputs, "the route does not remove the records its runs create (results.remove is off), so the records an unfinished delivery's runs created stay").ToList();
        }

        var results = new List<UndoResult>(outputs.Count);
        var targets = new List<(UndoItem Item, string Id)>();
        foreach (var item in outputs)
        {
            if (item.Artifact.TargetId is not { } id)
            {
                results.Add(UndoResult.Kept(item, "the output names no record"));
                continue;
            }

            try
            {
                var stored = await _records.ReadAsync(id, ct).ConfigureAwait(false);
                if (stored is null)
                {
                    results.Add(UndoResult.Gone(item, $"{id}: OSDU no longer holds the record"));
                }
                else if (ArtifactUndo.CreateTimeOf(stored) is { } created && created < item.UnitStartedUtc - ArtifactLimits.ClockSkew)
                {
                    results.Add(UndoResult.Kept(item, string.Create(CultureInfo.InvariantCulture, $"{id}: OSDU created the record at {created:u}, before this delivery began at {item.UnitStartedUtc:u}, so a run updated it and it is not this delivery's to remove")));
                }
                else
                {
                    targets.Add((item, id));
                }
            }
            catch (Exception ex) when (ArtifactUndo.Answerable(ex, ct))
            {
                results.Add(UndoResult.Failed(item, $"{id}: {ArtifactUndo.Redact(ex)}"));
            }
        }

        results.AddRange(await ArtifactUndo.SoftDeleteAsync(_records, work.Key, targets, ct).ConfigureAwait(false));
        return results;
    }

    /// <summary>The ids among <paramref name="ids"/> storage holds, in the order given (openapi storage v2, POST query/records, in batches).</summary>
    private async Task<IReadOnlyList<string>> PresentAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var present = await StoragePresence.PresentAsync(_client, _options.VerifyBatchPath ?? OsduRecordProtocol.DefaultVerifyBatchPath, ids, ct).ConfigureAwait(false);
        return ids.Where(present.Contains).ToList();
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

    /// <summary>
    /// The Workflow service, and every workflow the route runs as this partition registers it (openapi workflow v1, GET
    /// workflow/{workflow_name}): names are deployment configuration (section 1.4), so a name the partition does not
    /// know stops the run before anything is sent.
    /// </summary>
    public async Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
    {
        var service = await RecordWriter.ProbeAsync(_client, _options.ProbePath ?? WorkflowClient.DefaultProbePath, ct).ConfigureAwait(false);
        if (!service.Reachable)
        {
            return service;
        }

        foreach (var stage in _route.Stages.DistinctBy(s => s.Workflow))
        {
            try
            {
                if (!await _workflows.ExistsAsync(stage.Workflow, ct).ConfigureAwait(false))
                {
                    return new ProbeOutcome(false, 404, $"the Workflow service of this partition has no workflow named {stage.Workflow}; workflow names differ per deployment (osdu/specs/workflows/INTEGRATION.md section 1.4)", WorkflowClient.DefaultWorkflowPath);
                }
            }
            catch (OsduStatusException ex)
            {
                return new ProbeOutcome(false, ex.StatusCode, HeaderRedaction.RedactMessage(ex.Message), WorkflowClient.DefaultWorkflowPath);
            }
        }

        return service with { Detail = $"the Workflow service answered and knows {string.Join(", ", _route.Stages.Select(s => s.Workflow).Distinct(StringComparer.Ordinal))}" };
    }

    private LegalTagValidator? _legal;

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

    /// <summary>
    /// Removes the anchor, and with it what the route put into OSDU for it: at the reversible scope the records its runs
    /// created (when the route removes them), at the everything scope those, its registered inputs and files too. A
    /// dataset anchor goes through the Dataset service's reversible removal, which its undelete restores; a history purge
    /// touches the anchor's own earlier versions alone.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        DeleteOutcome outcome;
        if (_route.Anchor == WorkflowAnchor.Dataset && scope == RemovalScope.Record)
        {
            outcome = await _datasets.SoftDeleteAsync(targetId, ct).ConfigureAwait(false);
        }
        else
        {
            outcome = await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options), targetId, scope, ct).ConfigureAwait(false);
        }

        if (scope == RemovalScope.History)
        {
            return outcome;
        }

        var (removed, notes) = await RemoveCreatedAsync(targetId, scope, targetState, ct).ConfigureAwait(false);
        return outcome with { Deleted = outcome.Deleted || removed > 0, Detail = string.Join("; ", [outcome.Detail, .. notes]) };
    }

    /// <summary>
    /// What a removal of <paramref name="targetId"/> at <paramref name="scope"/> takes with it beside the record (the records the
    /// runs created, when the results remove them, and at <see cref="RemovalScope.Everything"/> the inputs and files registered for
    /// it), each removed at the same scope: how many were, and what a note says of them. A step of <c>enrich:</c> running a workflow
    /// over a record removes its runs' records this way, its record being the flow's route's (osdu/docs/reference/flow/enrich-workflow.md).
    /// </summary>
    internal async Task<(int Removed, IReadOnlyList<string> Notes)> RemoveCreatedAsync(
        string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var notes = new List<string>();
        var removed = 0;
        var created = new List<string>();
        if (_route.Results.Remove)
        {
            created.AddRange(await CreatedAsync(targetState, ct).ConfigureAwait(false));
        }

        var owned = new List<string>(created);
        if (scope == RemovalScope.Everything)
        {
            foreach (var input in _route.Inputs)
            {
                owned.AddRange(Ids(targetState, InputValue(input.Name)));
            }

            owned.AddRange(Ids(targetState, FileUploads.DatasetIdsValue));
        }

        foreach (var id in owned.Distinct(StringComparer.Ordinal).Where(id => !string.Equals(id, targetId, StringComparison.Ordinal)))
        {
            var one = await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options), id, scope, ct).ConfigureAwait(false);
            if (one.Deleted)
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{removed} record(s) the route created for it removed at the same scope"));
        }

        if (targetState is not null
            && targetState.TryGetValue(RecordsValue, out var countText)
            && int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            && _route.Results.Remove
            && count > created.Count)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"the runs created {count} record(s) and {created.Count} could be named for removal; remove the others by their kind"));
        }

        return (removed, notes);
    }

    /// <summary>
    /// The records a removal takes with the anchor: the ones a search finds again when the route searched for them (a
    /// search names them all, where the record keeps only some), else the ones the record kept.
    /// </summary>
    private async Task<IReadOnlyList<string>> CreatedAsync(IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
    {
        if (targetState is not null && targetState.TryGetValue(SearchKindValue, out var kind))
        {
            return await SearchAsync(kind, targetState.GetValueOrDefault(SearchQueryValue), ct).ConfigureAwait(false);
        }

        return Ids(targetState, RecordIdsValue);
    }

    private static IReadOnlyList<string> Ids(IReadOnlyDictionary<string, string>? state, string key)
        => state is not null && state.TryGetValue(key, out var joined) && !string.IsNullOrWhiteSpace(joined)
            ? joined.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    private static string? Text(JsonObject node, string name)
        => node[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>A context kept on a step: its JSON, cut to what a step value holds.</summary>
    private static string Bounded(string json) => json.Length <= 4000 ? json : json[..4000];

    /// <summary>One file of a payload as a payload of its own, so each file of an input is stored and registered on its own.</summary>
    private sealed class SingleFile(IPayloadSource source, PayloadFile file) : IPayloadSource
    {
        public Task<IReadOnlyList<PayloadFile>> ListChunksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PayloadFile>>([file with { Index = 0 }]);

        public Task<Stream> OpenAsync(PayloadFile chunk, CancellationToken ct = default)
            => source.OpenAsync(file, ct);
    }
}
