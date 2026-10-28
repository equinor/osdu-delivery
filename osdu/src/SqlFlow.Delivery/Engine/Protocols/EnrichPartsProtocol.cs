using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>
/// The route of a flow whose <c>enrich:</c> steps register files (osdu/docs/reference/flow/enrich-files.md):
/// the flow's own route, with each record's step datasets registered before it in the same delivery. Each dataset whose hash
/// moved (its files or what its fragment rendered into it) has its files stored where the Dataset service says and is
/// registered under the id the record's id and the step make, with an intent declared first, so a registration whose answer
/// is lost is undone or found again rather than registered twice, and is checked to answer retrieval instructions. The record
/// names its datasets already, so it is written through the flow's route only when its document changed: new files register
/// a new version of the same dataset and leave the record as OSDU holds it. A delivery that does not complete is undone at
/// the Dataset service's reversible scope, and removing everything purges the datasets with the record.
/// </summary>
public sealed class EnrichPartsProtocol : IDeliveryProtocol
{
    /// <summary>The artifact slot of a dataset a step registers, which this route undoes and the flow's own route does not.</summary>
    public const string DatasetSlot = "enrich-dataset";

    private readonly IDeliveryProtocol _inner;
    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly DatasetService _datasets;
    private readonly OsduRecordProtocol _records;
    private readonly long _requestBodyCeiling;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<StepWorkflow> _workflows;

    /// <param name="inner">The flow's own route, which writes the record.</param>
    /// <param name="client">The flow's client, under its endpoint, credentials and partition.</param>
    /// <param name="options">The flow's route options: the Dataset service's paths and the media type files go as.</param>
    /// <param name="requestBodyCeiling">The largest file one upload sends, or 0 for no ceiling.</param>
    /// <param name="time">The clock the steps are timed by.</param>
    /// <param name="workflows">The workflows the flow's steps run over each record once it landed (osdu/docs/reference/flow/enrich-workflow.md).</param>
    public EnrichPartsProtocol(
        IDeliveryProtocol inner, OsduHttpClient client, ProtocolOptions options, long requestBodyCeiling = 0, TimeProvider? time = null, IReadOnlyList<StepWorkflow>? workflows = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _inner = inner;
        _client = client;
        _options = options.ForFiles(besideBulk: true);
        _datasets = new DatasetService(client, _options);
        _time = time ?? TimeProvider.System;
        _records = new OsduRecordProtocol(client, options, _time);
        _requestBodyCeiling = requestBodyCeiling;
        _workflows = workflows ?? [];
    }

    /// <summary>The workflows the flow's steps run over each record, by step.</summary>
    public IReadOnlyList<StepWorkflow> Workflows => _workflows;

    /// <summary>The flow's own route, which this one wraps.</summary>
    public IDeliveryProtocol Inner => _inner;

    public DeliveryProtocol Kind => _inner.Kind;

    public int MaxBatch => _inner.MaxBatch;

    public int MaxVerifyBatch => _inner.MaxVerifyBatch;

    public bool VerifiesWithTargetState => _inner.VerifiesWithTargetState;

    /// <summary>A step's dataset is registered before the record is written, so a delivery can leave one behind until its undo.</summary>
    public bool Undoes => true;

    /// <summary>The step a part's registration intent is reported under, before the registration is sent.</summary>
    public static string IntentStep(int index) => string.Create(CultureInfo.InvariantCulture, $"enrich-register-intent-{index}");

    /// <summary>The step a part's registration is reported under.</summary>
    public static string RegisterStep(int index) => string.Create(CultureInfo.InvariantCulture, $"enrich-register-{index}");

    /// <summary>The payload name a part's uploads are reported under (<c>storage-enrich-0</c>, <c>upload-enrich-0-1</c>).</summary>
    public static string PayloadName(int index) => string.Create(CultureInfo.InvariantCulture, $"enrich-{index}");

    public async Task<DeliveryOutcome> DeliverAsync(DeliveryWork work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var outcome = (await DeliverBatchAsync([work], ct).ConfigureAwait(false))[0];
        return outcome.Failure is { } failure ? throw failure : outcome;
    }

    public async Task<IReadOnlyList<DeliveryOutcome>> DeliverBatchAsync(IReadOnlyList<DeliveryWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var plans = new List<Plan>(works.Count);
        for (var i = 0; i < works.Count; i++)
        {
            var plan = new Plan(i, works[i], new DeliverySteps(_time));
            plans.Add(plan);
            if (plan.Work.StepParts.Count == 0)
            {
                continue;
            }

            try
            {
                await StageAsync(plan, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
            {
                plan.Failure = ex;
            }
        }

        await RegisterAsync(plans.SelectMany(p => p.Failure is null ? p.Staged : []).ToList(), ct).ConfigureAwait(false);

        // The record is written once its datasets are registered, and only when it changed: it names them already.
        var writes = plans.Where(p => p.Failure is null && p.WritesRecord).ToList();
        if (writes.Count > 0)
        {
            var outcomes = await _inner.DeliverBatchAsync(writes.Select(p => p.Work).ToList(), ct).ConfigureAwait(false);
            for (var i = 0; i < writes.Count; i++)
            {
                writes[i].Written = outcomes[i];
            }
        }

        // The steps' workflows run over each record once it and the step's datasets landed, one after another.
        foreach (var plan in plans.Where(p => p.Failure is null && p.Written is not { Failure: not null }))
        {
            foreach (var workflow in _workflows.Where(w => Due(plan, w)))
            {
                var outcome = await RunAsync(plan, workflow, ct).ConfigureAwait(false);
                plan.Workflows.Add((workflow, outcome));
                if (outcome.Failure is not null)
                {
                    break;
                }
            }
        }

        return plans.Select(p => p.Finish(p.Outcome())).ToList();
    }

    /// <summary>
    /// Whether a step's workflow runs for a record in this delivery (osdu/docs/reference/flow/enrich-workflow.md): <c>changed</c>
    /// when the step's datasets were registered (or, for a step registering none, the record was written), <c>created</c> when
    /// the record was created, <c>requested</c> when a redelivery named the workflow; and every one when the whole record was
    /// asked to be sent again, or never was.
    /// </summary>
    private static bool Due(Plan plan, StepWorkflow workflow)
    {
        var work = plan.Work;
        var named = work.Forces(PayloadParts.Workflow);
        var resent = work.StepPartsForced;
        return workflow.RunWhen switch
        {
            WorkflowRunWhen.Created => named || work.ExistingVersion is null,
            WorkflowRunWhen.Requested => named || (resent && work.ExistingVersion is not null),
            _ => named || resent
                || (workflow.RegistersFiles
                    ? plan.Registered.Any(p => string.Equals(p.Step, workflow.Step, StringComparison.Ordinal))
                    : plan.Written is { MetadataDelivered: true }),
        };
    }

    /// <summary>
    /// Runs a step's workflow over the record, through the workflow route itself: its stages triggered and polled, its results
    /// found, nothing written of the record. Every step it reports, and every artifact, is named under the step
    /// (<c>enrich.&lt;step&gt;.</c>), so two steps' runs never share a name, and a retry resumes after what the run completed.
    /// </summary>
    private static async Task<DeliveryOutcome> RunAsync(Plan plan, StepWorkflow workflow, CancellationToken ct)
    {
        var prefix = workflow.Prefix;
        var outer = plan.Work;
        var report = outer.StepCompleted;
        var run = outer with
        {
            DeliverMetadata = false,
            DeliverPayload = true,
            Payload = null,
            Parts = [],
            ForcedParts = new HashSet<string>([PayloadParts.Workflow], StringComparer.Ordinal),
            StepParts = [],
            StepKeep = new Dictionary<string, string>(StringComparer.Ordinal),
            ExistingVersion = plan.Written?.TargetVersion ?? outer.ExistingVersion,
            RestoreFirst = false,
            TargetState = outer.TargetState.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value, StringComparer.Ordinal),
            CompletedSteps = outer.CompletedSteps.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value, StringComparer.Ordinal),
            StepCompleted = report is null
                ? null
                : (step, token) => report(step with { Step = prefix + step.Step, Artifacts = step.Artifacts.Select(a => a with { Slot = prefix + a.Slot }).ToList() }, token),
        };
        return await workflow.Route.DeliverAsync(run, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stages the record's parts whose hash moved (or that a redelivery naming <c>files</c> forces): each one's files stored where
    /// the Dataset service says for its kind, and its dataset record built around them. A part whose dataset an earlier try of the
    /// same work registered is not staged again.
    /// </summary>
    private async Task StageAsync(Plan plan, CancellationToken ct)
    {
        var work = plan.Work;
        var forced = work.StepPartsForced || work.ForcedParts.Contains(EnrichPart.Role);
        for (var index = 0; index < work.StepParts.Count; index++)
        {
            var (part, source) = (work.StepParts[index].Part, work.StepParts[index].Source);
            if (!forced && work.TargetState.TryGetValue(EnrichParts.StateKey(part.DatasetId), out var delivered) && string.Equals(delivered, part.Hash, StringComparison.Ordinal))
            {
                continue;
            }

            if (work.Completed(RegisterStep(index)) is { } done && done.TryGetValue("version", out _))
            {
                plan.Steps.Resumed(RegisterStep(index), done);
                plan.Registered.Add(part);
                continue;
            }

            var entityType = TargetId.EntityTypeFromKind(part.Kind);
            var options = part.ContentType is { } contentType ? _options with { PayloadContentType = contentType } : _options;
            var staged = await DatasetUploads.UploadAsync(_client, _datasets, options, work, PayloadName(index), source, entityType, _requestBodyCeiling, plan.Steps, ct).ConfigureAwait(false);
            var record = DatasetRecord(work, part, staged);
            DatasetUploads.Point(record, staged);
            plan.Staged.Add(new StagedPart(plan, index, part, record));
        }
    }

    /// <summary>
    /// The dataset record a part registers: what its fragment rendered, under the id and kind the part names, with the record's
    /// own access list and legal block, which a dataset beside it shares, and the name of its first file where the fragment
    /// names none.
    /// </summary>
    private static JsonObject DatasetRecord(DeliveryWork work, EnrichPart part, StagedDataset staged)
    {
        var record = EnrichParts.RecordOf(part);
        record["id"] = part.DatasetId;
        record["kind"] = part.Kind;
        record["acl"] = work.Document["acl"]?.DeepClone() ?? throw new RecordHeldException($"the rendered record has no acl block to copy onto its dataset {part.DatasetId}");
        record["legal"] = work.Document["legal"]?.DeepClone() ?? throw new RecordHeldException($"the rendered record has no legal block to copy onto its dataset {part.DatasetId}");
        if (record["data"] is not JsonObject data)
        {
            data = new JsonObject();
            record["data"] = data;
        }

        if (data["Name"] is null && staged.Files.Count > 0)
        {
            data["Name"] = staged.Files[0].Name;
        }

        return record;
    }

    /// <summary>
    /// Registers the staged parts, twenty to a request, each declared first, and checks that the Dataset service answers retrieval
    /// instructions for each. A registration refused as a whole is tried part by part, so one bad dataset fails its record alone.
    /// </summary>
    private async Task RegisterAsync(List<StagedPart> staged, CancellationToken ct)
    {
        foreach (var chunk in staged.Chunk(DatasetService.MaxRecords))
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
                        one.Plan.Failure ??= single;
                    }
                }
            }
            catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
            {
                foreach (var one in chunk)
                {
                    one.Plan.Failure ??= ex;
                }
            }
        }
    }

    private async Task RegisterChunkAsync(IReadOnlyList<StagedPart> chunk, CancellationToken ct)
    {
        var intents = await RegistrationIntents.DeclareAsync(_records, chunk.Select(c => new Registration(DatasetSlot, c.Part.DatasetId)).ToList(), ct).ConfigureAwait(false);
        var ready = new List<(StagedPart Part, TargetArtifact Declared)>(chunk.Count);
        for (var i = 0; i < chunk.Count; i++)
        {
            var item = chunk[i];
            if (item.Plan.Failure is not null)
            {
                continue;
            }

            if (intents[i].Artifact is not { } declared)
            {
                item.Plan.Failure = new DeliveryException(intents[i].Refusal!);
                continue;
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["datasetId"] = item.Part.DatasetId, ["step"] = item.Part.Step };
            if (declared.PriorVersion is { } prior)
            {
                values["priorVersion"] = prior.ToString(CultureInfo.InvariantCulture);
            }

            await item.Plan.Work.ReportStepAsync(IntentStep(item.Index), values, [declared], ct).ConfigureAwait(false);
            ready.Add((item, declared));
        }

        if (ready.Count == 0)
        {
            return;
        }

        var started = _time.GetUtcNow().UtcDateTime;
        var landed = await _datasets.RegisterAsync(ready.Select(r => r.Part.Record).ToList(), ct).ConfigureAwait(false);
        var retrievable = await _datasets.RetrievableAsync(ready.Select(r => r.Part.Part.DatasetId).ToList(), ct).ConfigureAwait(false);
        foreach (var (item, declared) in ready)
        {
            var id = item.Part.DatasetId;
            if (!retrievable.Contains(id))
            {
                item.Plan.Failure ??= new DeliveryException($"the dataset service registered {id}, the dataset of step '{item.Part.Step}', and answers no retrieval instructions for it, so its files cannot be read back; the next try registers it again");
                continue;
            }

            var version = landed.GetValueOrDefault(id);
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["datasetId"] = id, ["step"] = item.Part.Step, ["retrievable"] = "true" };
            if (version is { } v)
            {
                values["version"] = v.ToString(CultureInfo.InvariantCulture);
            }

            item.Plan.Steps.Add(RegisterStep(item.Index), started, 201, values);
            await item.Plan.Work.ReportStepAsync(RegisterStep(item.Index), values, [RegistrationIntents.Landed(declared, version)], ct).ConfigureAwait(false);
            item.Plan.Registered.Add(item.Part);
        }
    }

    public Task<VerifyResult> VerifyAsync(string targetId, long? expectedVersion, CancellationToken ct = default)
        => _inner.VerifyAsync(targetId, expectedVersion, ct);

    public Task<IReadOnlyList<VerifyResult>> VerifyBatchAsync(IReadOnlyList<VerifyRequest> requests, CancellationToken ct = default)
        => _inner.VerifyBatchAsync(requests, ct);

    public Task<IReadOnlyDictionary<string, string>?> InvalidLegalTagsAsync(IReadOnlyCollection<string> tags, CancellationToken ct = default)
        => _inner.InvalidLegalTagsAsync(tags, ct);

    /// <summary>
    /// The record goes as the flow's route removes it. Its step datasets stay at the reversible scopes, since the record's
    /// earlier versions name them and a restored record finds them; removing everything purges them too, as the dataset
    /// route purges a record's own dataset.
    /// </summary>
    public async Task<DeleteOutcome> DeleteAsync(string targetId, RemovalScope scope, IReadOnlyDictionary<string, string>? targetState = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var outcome = await _inner.DeleteAsync(targetId, scope, targetState, ct).ConfigureAwait(false);
        if (scope != RemovalScope.History)
        {
            // What each step's workflow runs created for the record goes with it, as the workflow route takes its runs' records.
            foreach (var workflow in _workflows)
            {
                var kept = (targetState ?? new Dictionary<string, string>(StringComparer.Ordinal))
                    .Where(kv => kv.Key.StartsWith(workflow.Prefix, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key[workflow.Prefix.Length..], kv => kv.Value, StringComparer.Ordinal);
                var (removed, notes) = await workflow.Route.RemoveCreatedAsync(targetId, scope, kept, ct).ConfigureAwait(false);
                outcome = outcome with
                {
                    Deleted = outcome.Deleted || removed > 0,
                    Detail = string.Join("; ", [outcome.Detail, .. notes.Select(n => $"enrich.{workflow.Step}: {n}")]),
                };
            }
        }

        var datasets = EnrichParts.Datasets(targetState);
        if (scope != RemovalScope.Everything || datasets.Count == 0)
        {
            return outcome;
        }

        var purged = 0;
        foreach (var id in datasets)
        {
            var dataset = await RecordWriter.DeleteAsync(_client, RemovalPaths.From(_options), id, RemovalScope.Everything, ct).ConfigureAwait(false);
            if (dataset.Deleted || dataset.AlreadyGone)
            {
                purged++;
            }
        }

        return outcome with { Detail = string.Create(CultureInfo.InvariantCulture, $"{outcome.Detail}; {purged} of the {datasets.Count} dataset(s) its enrich: steps registered purged too") };
    }

    /// <summary>Removes as the flow's route batches its removals, except removing everything, which purges each record's step datasets after it.</summary>
    public Task<IReadOnlyList<RemovalResult>> DeleteBatchAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(removals);
        return (scope == RemovalScope.Everything && removals.Any(r => EnrichParts.Datasets(r.TargetState).Count > 0))
            || (scope != RemovalScope.History && _workflows.Count > 0)
            ? ((IDeliveryProtocol)this).DeleteOneByOneAsync(removals, scope, ct)
            : _inner.DeleteBatchAsync(removals, scope, ct);
    }

    public Task<JsonObject?> ReadAsync(string targetId, CancellationToken ct = default)
        => _inner.ReadAsync(targetId, ct);

    public Task<JsonObject?> ReadAsync(string targetId, IReadOnlyDictionary<string, string>? targetState, CancellationToken ct)
        => _inner.ReadAsync(targetId, targetState, ct);

    public Task<IReadOnlyList<long>?> VersionsAsync(string targetId, CancellationToken ct = default)
        => _inner.VersionsAsync(targetId, ct);

    public Task<JsonObject?> ReadVersionAsync(string targetId, long version, CancellationToken ct = default)
        => _inner.ReadVersionAsync(targetId, version, ct);

    /// <summary>
    /// The flow's own services as its route asks them, then the Dataset service the steps register their files through, then the
    /// Workflow service and the workflows each step's stages name, as the workflow route asks them.
    /// </summary>
    public async Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
    {
        var probe = await _inner.ProbeAsync(ct).ConfigureAwait(false);
        if (!probe.Reachable)
        {
            return probe;
        }

        probe = await RecordWriter.ProbeAsync(_client, DatasetService.DefaultProbePath, ct).ConfigureAwait(false);
        foreach (var workflow in _workflows)
        {
            if (!probe.Reachable)
            {
                return probe;
            }

            probe = await workflow.Route.ProbeAsync(ct).ConfigureAwait(false);
        }

        return probe;
    }

    /// <summary>
    /// Undoes what unfinished deliveries left: a step dataset a unit created is removed through the Dataset service's reversible
    /// removal once storage confirms OSDU created it after the unit began, and one a unit wrote a new version of is given back the
    /// version before, as the dataset route undoes its own datasets; everything else is the flow's own route's to undo.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoAsync(IReadOnlyList<UndoWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var side = new RecordSide
        {
            Remove = _datasets.SoftDeleteAsync,
            Read = _records.ReadAsync,
            Restorer = _records,
            Versions = _records.VersionsAsync,
        };
        var results = new List<UndoResult>();
        var rest = new List<UndoWork>();
        foreach (var work in works)
        {
            var datasets = work.Items.Where(i => string.Equals(i.Artifact.Slot, DatasetSlot, StringComparison.Ordinal)).ToList();
            if (datasets.Count > 0)
            {
                // A newer try registers the same id again from files that may differ, so what this unit registered is undone whatever
                // the newer work is, as the dataset route undoes the dataset a record keeps its files in.
                results.AddRange(await ArtifactUndo.RecordItselfAsync(work with { KeepRecord = false }, datasets, side, ct).ConfigureAwait(false));
            }

            // What a step's workflow left (its runs, the records they created) is the workflow route's to undo, under the names it
            // reported them by; the results carry the items as the ledger holds them.
            var claimed = new List<UndoItem>(datasets);
            foreach (var workflow in _workflows)
            {
                var items = work.Items.Where(i => i.Artifact.Slot.StartsWith(workflow.Prefix, StringComparison.Ordinal)).ToList();
                if (items.Count == 0)
                {
                    continue;
                }

                claimed.AddRange(items);
                var originals = items.ToDictionary(i => i.ArtifactId);
                var named = items.Select(i => i with { Artifact = i.Artifact with { Slot = i.Artifact.Slot[workflow.Prefix.Length..] } }).ToList();
                var undone = await workflow.Route.UndoAsync([work with { Items = named }], ct).ConfigureAwait(false);
                results.AddRange(undone.Select(r => r with { Item = originals[r.Item.ArtifactId] }));
            }

            var others = work.Items.Except(claimed).ToList();
            if (others.Count > 0)
            {
                rest.Add(work with { Items = others });
            }
        }

        if (rest.Count > 0)
        {
            results.AddRange(await _inner.UndoAsync(rest, ct).ConfigureAwait(false));
        }

        return results;
    }

    public Task<IReadOnlyList<RestoreResult>> RestoreBatchAsync(IReadOnlyList<VersionRestore> restores, CancellationToken ct = default)
        => _inner.RestoreBatchAsync(restores, ct);

    /// <summary>A part staged for registration: its record's plan, its place among the record's parts, the part, and the dataset record to register.</summary>
    private sealed record StagedPart(Plan Plan, int Index, EnrichPart Part, JsonObject Record)
    {
        public string DatasetId => Part.DatasetId;
    }

    /// <summary>What one record's delivery comes to: its parts staged and registered, and the flow's route's write of the record.</summary>
    private sealed class Plan(int index, DeliveryWork work, DeliverySteps steps)
    {
        public int Index { get; } = index;

        public DeliveryWork Work { get; } = work;

        public DeliverySteps Steps { get; } = steps;

        public List<StagedPart> Staged { get; } = [];

        /// <summary>The parts this try registered, or found registered by an earlier try of the same work.</summary>
        public List<EnrichPart> Registered { get; } = [];

        public DeliveryOutcome? Written { get; set; }

        public Exception? Failure { get; set; }

        /// <summary>The steps' workflows that ran over the record in this delivery, each with what it came to.</summary>
        public List<(StepWorkflow Workflow, DeliveryOutcome Outcome)> Workflows { get; } = [];

        /// <summary>
        /// The record's outcome with what its steps' workflows came to: a failed run fails the record, as the workflow route's
        /// does; a finished one's steps and returned values are kept under the step, and the version OSDU serves once the runs
        /// are done is the record's, since a workflow may write the record itself.
        /// </summary>
        public DeliveryOutcome Finish(DeliveryOutcome outcome)
        {
            if (Workflows.Count == 0 || !outcome.Succeeded)
            {
                return outcome;
            }

            var steps = outcome.Steps.ToList();
            var returned = new Dictionary<string, string>(outcome.Returned, StringComparer.Ordinal);
            var version = outcome.TargetVersion;
            var detail = new List<string>();
            if (outcome.Detail is { Length: > 0 } own)
            {
                detail.Add(own);
            }

            foreach (var (workflow, ran) in Workflows)
            {
                steps.AddRange(ran.Steps.Select(s => s with { Name = workflow.Prefix + s.Name }));
                if (ran.Failure is { } failure)
                {
                    return DeliveryOutcome.Failed(new DeliveryException($"enrich.{workflow.Step}: its workflow did not finish: {failure.Message}", failure), steps);
                }

                foreach (var (name, value) in ran.Returned)
                {
                    if (name == "version" && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var read))
                    {
                        version = read;
                        returned["version"] = value;
                    }
                    else if (name != "recordId")
                    {
                        returned[workflow.Prefix + name] = value;
                    }
                }

                detail.Add($"enrich.{workflow.Step}: {ran.Detail}");
            }

            return outcome with
            {
                Steps = steps,
                Returned = returned,
                TargetVersion = version,
                PayloadDelivered = true,
                Detail = string.Join("; ", detail),
            };
        }

        /// <summary>The hash of the record's document as the flow's route writes it, its parts left out.</summary>
        public string DocumentHash => ContentHash.Of(CanonicalJson.ToString(Work.Document));

        /// <summary>
        /// Whether the flow's route writes the record: always for a record without parts, and otherwise unless the plan found its
        /// document the one last written and the last delivery kept the same document's hash: its payload goes, it has not been
        /// written yet, OSDU lost it, or it was asked to be sent again.
        /// </summary>
        public bool WritesRecord
            => (Work.StepParts.Count == 0 && Work.StepKeptDatasets.Count == 0)
                || !Work.StepDocumentUnchanged
                || Work.DeliverPayload
                || Work.ExistingVersion is null
                || Work.RestoreFirst
                || !(Work.TargetState.TryGetValue(EnrichParts.DocumentValue, out var written) && string.Equals(written, DocumentHash, StringComparison.Ordinal));

        public DeliveryOutcome Outcome()
        {
            if (Work.StepParts.Count == 0 && Work.StepKeptDatasets.Count == 0)
            {
                var outcome = Written ?? DeliveryOutcome.Failed(Failure ?? new DeliveryException("the record was not written"));
                var dropped = EnrichParts.Datasets(Work.TargetState);
                if (!outcome.Succeeded || dropped.Count == 0)
                {
                    return outcome;
                }

                // The record no longer links the datasets its steps registered: they stay live, since earlier versions name them.
                return outcome with
                {
                    Returned = new Dictionary<string, string>(outcome.Returned, StringComparer.Ordinal) { [EnrichParts.DatasetsValue] = EnrichParts.DatasetsText([]) },
                    Superseded = [.. outcome.Superseded, .. dropped],
                };
            }

            var steps = Steps.Steps.Concat(Written?.Steps ?? []).ToList();
            if (Failure is { } failure)
            {
                return DeliveryOutcome.Failed(failure, steps);
            }

            if (Written is { Failure: { } writeFailure })
            {
                return DeliveryOutcome.Failed(writeFailure, steps);
            }

            // The record links the datasets registered now and those of the steps set aside, which an earlier delivery registered.
            var linked = Work.StepParts.Select(p => p.Part.DatasetId).Concat(Work.StepKeptDatasets).ToList();
            var returned = new Dictionary<string, string>(Written?.Returned ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                [EnrichParts.DatasetsValue] = EnrichParts.DatasetsText(linked),
                [EnrichParts.DocumentValue] = DocumentHash,
            };
            foreach (var part in Work.StepParts.Select(p => p.Part))
            {
                returned[EnrichParts.StateKey(part.DatasetId)] = part.Hash;
                returned[EnrichParts.FilesStateKey(part.DatasetId)] = part.FilesHash;
            }

            // A dataset the record no longer links stays live, since earlier versions of the record name it: superseded, not removed.
            var current = linked.ToHashSet(StringComparer.Ordinal);
            var superseded = EnrichParts.Datasets(Work.TargetState).Where(id => !current.Contains(id)).ToList();
            var registered = Registered.Count;
            if (Written is { } written)
            {
                return written with
                {
                    Steps = steps,
                    Returned = returned,
                    PayloadDelivered = written.PayloadDelivered || registered > 0,
                    Superseded = [.. written.Superseded, .. superseded],
                    Detail = registered == 0 ? written.Detail : string.Create(CultureInfo.InvariantCulture, $"{written.Detail}{(written.Detail is null ? string.Empty : "; ")}{registered} dataset(s) of its enrich: steps registered"),
                };
            }

            return new DeliveryOutcome
            {
                MetadataDelivered = true,
                PayloadDelivered = registered > 0,
                TargetVersion = Work.ExistingVersion,
                Detail = string.Create(CultureInfo.InvariantCulture, $"{registered} dataset(s) of its enrich: steps registered; the record names them already and is not written again"),
                Returned = returned,
                Steps = steps,
                Superseded = superseded,
            };
        }
    }
}

/// <summary>
/// The workflow one step of <c>enrich:</c> runs over each record once it landed (osdu/docs/reference/flow/enrich-workflow.md):
/// the step, when it runs, whether the step registers files (whose registration then says the run is due), and the workflow
/// route that runs it over the record without writing it.
/// </summary>
/// <param name="Step">The step's name.</param>
/// <param name="RunWhen">When the workflow runs.</param>
/// <param name="RegistersFiles">True when the step registers files, whose registration makes a <c>changed</c> run due.</param>
/// <param name="Route">The workflow route over the step's workflow, anchored on the record the flow's route writes.</param>
public sealed record StepWorkflow(string Step, WorkflowRunWhen RunWhen, bool RegistersFiles, OsduWorkflowProtocol Route)
{
    /// <summary>What the record's steps, values and artifacts of this step's runs are named under: <c>enrich.&lt;step&gt;.</c>.</summary>
    public string Prefix => "enrich." + Step + ".";
}
