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
/// The route of a flow whose mapping's enrichments add records to each record (osdu/docs/reference/flow/enrich-records.md):
/// the flow's own route, with each record's enrichment records written before it in the same delivery. Each record whose hash
/// moved (what its mapping rendered into it, or its files) is written under the id the record's id and the enrichment make,
/// with an intent declared first, so a write whose answer is lost is undone or found again rather than made twice blind: a
/// dataset whose rows name files has them stored where the Dataset service says, is registered through it and is checked to
/// answer retrieval instructions; every other record is written through storage as it rendered. The record links them
/// already, so it is written through the flow's route only when its document changed: a changed enrichment record writes a
/// new version of the same id and leaves the record as OSDU holds it. A delivery that does not complete is undone at the
/// reversible scope, and removing everything purges the enrichment records with the record.
/// </summary>
public sealed class EnrichPartsProtocol : IDeliveryProtocol
{
    /// <summary>The artifact slots of the datasets an enrichment registers with their files, which this route undoes and the flow's own route does not.</summary>
    public const string DatasetSlot = "enrich-dataset";

    /// <summary>The artifact slots of the records an enrichment writes through storage, which this route undoes and the flow's own route does not.</summary>
    public const string RecordSlot = "enrich-record";

    /// <summary>
    /// The slot of the record <paramref name="id"/> an enrichment adds, in <paramref name="slots"/> (<see cref="DatasetSlot"/>
    /// or <see cref="RecordSlot"/>): a digest of its id, which may be longer than a slot, so each record a delivery adds keeps
    /// an artifact of its own and an undo reaches every one.
    /// </summary>
    public static string SlotOf(string slots, string id) => slots + ":" + ContentHash.Of(id)[..24];

    /// <summary>The value of an intent step naming the slot its intent was declared in.</summary>
    public const string SlotValue = "slot";

    /// <summary>
    /// The slot <paramref name="staged"/>'s registration is declared in: its record's own, or the one every record of a delivery
    /// shared when an earlier try of the unit declared it there (a build before each had its own, which named no slot in the
    /// intent step), so the resumed try completes that intent rather than leaving it open to be undone once the unit delivers.
    /// </summary>
    private static string SlotFor(StagedPart staged)
    {
        var slots = staged.Part.RegistersFiles ? DatasetSlot : RecordSlot;
        return staged.Plan.Work.Completed(IntentStep(staged.Index)) is { } declared && !declared.ContainsKey(SlotValue)
            ? slots
            : SlotOf(slots, staged.Part.RecordId);
    }

    /// <summary>
    /// True when <paramref name="slot"/> is one of <paramref name="slots"/>: a record's own, or the one slot every record of a
    /// delivery shared before each had its own, which a ledger may still hold open.
    /// </summary>
    private static bool IsIn(string slot, string slots)
        => string.Equals(slot, slots, StringComparison.Ordinal) || (slot.StartsWith(slots, StringComparison.Ordinal) && slot.Length > slots.Length && slot[slots.Length] == ':');

    private readonly IDeliveryProtocol _inner;
    private readonly OsduHttpClient _client;
    private readonly ProtocolOptions _options;
    private readonly DatasetService _datasets;
    private readonly OsduRecordProtocol _records;
    private readonly long _requestBodyCeiling;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<StepWorkflow> _workflows;
    private readonly bool _registersFiles;
    private readonly RemovalPaths _removals;

    /// <param name="inner">The flow's own route, which writes the record.</param>
    /// <param name="client">The flow's client, under its endpoint, credentials and partition.</param>
    /// <param name="options">The flow's route options: the Dataset and storage services' paths and the media type files go as.</param>
    /// <param name="requestBodyCeiling">The largest file one upload sends, or 0 for no ceiling.</param>
    /// <param name="time">The clock the steps are timed by.</param>
    /// <param name="workflows">The workflows the flow runs over each record's enrichments once it landed (osdu/docs/reference/flow/enrich-workflow.md).</param>
    /// <param name="registersFiles">True when an enrichment registers datasets with their files, so the probe asks the Dataset service too.</param>
    public EnrichPartsProtocol(
        IDeliveryProtocol inner,
        OsduHttpClient client,
        ProtocolOptions options,
        long requestBodyCeiling = 0,
        TimeProvider? time = null,
        IReadOnlyList<StepWorkflow>? workflows = null,
        bool registersFiles = true)
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
        _registersFiles = registersFiles;
        _removals = RemovalPaths.From(options);
    }

    /// <summary>The workflows the flow's steps run over each record, by step.</summary>
    public IReadOnlyList<StepWorkflow> Workflows => _workflows;

    /// <summary>The flow's own route, which this one wraps.</summary>
    public IDeliveryProtocol Inner => _inner;

    public DeliveryProtocol Kind => _inner.Kind;

    public int MaxBatch => _inner.MaxBatch;

    public int MaxVerifyBatch => _inner.MaxVerifyBatch;

    public bool VerifiesWithTargetState => _inner.VerifiesWithTargetState;

    /// <summary>An enrichment's records are written before the record is, so a delivery can leave one behind until its undo.</summary>
    public bool Undoes => true;

    /// <summary>What the name of every step a part's registration intent is reported under begins with.</summary>
    public const string IntentStepPrefix = "enrich-register-intent-";

    /// <summary>What the name of every step a part's registration is reported under begins with, followed by the part's index.</summary>
    public const string RegisterStepPrefix = "enrich-register-";

    /// <summary>The step a part's registration intent is reported under, before the registration is sent.</summary>
    public static string IntentStep(int index) => IntentStepPrefix + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The step a part's registration is reported under.</summary>
    public static string RegisterStep(int index) => RegisterStepPrefix + index.ToString(CultureInfo.InvariantCulture);

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
    /// Stages the record's parts whose hash moved (or that a redelivery naming <c>files</c> forces): a dataset's files stored
    /// where the Dataset service says for its kind and its record built around them, and every other record built as it
    /// rendered. A part an earlier try of the same work wrote is not staged again.
    /// </summary>
    private async Task StageAsync(Plan plan, CancellationToken ct)
    {
        var work = plan.Work;
        var forced = work.StepPartsForced || work.ForcedParts.Contains(EnrichPart.Role);
        for (var index = 0; index < work.StepParts.Count; index++)
        {
            var (part, source) = (work.StepParts[index].Part, work.StepParts[index].Source);
            if (!forced && work.TargetState.TryGetValue(EnrichParts.StateKey(part.RecordId), out var delivered) && string.Equals(delivered, part.Hash, StringComparison.Ordinal))
            {
                continue;
            }

            if (work.Completed(RegisterStep(index)) is { } done && done.TryGetValue("version", out var resumed))
            {
                plan.Steps.Resumed(RegisterStep(index), done);
                plan.Registered.Add(part);
                if (long.TryParse(resumed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var landedVersion))
                {
                    plan.Versions[part.RecordId] = landedVersion;
                }

                continue;
            }

            if (!part.RegistersFiles)
            {
                plan.Staged.Add(new StagedPart(plan, index, part, PartRecord(work, part)));
                continue;
            }

            var entityType = TargetId.EntityTypeFromKind(part.Kind);
            var options = part.ContentType is { } contentType ? _options with { PayloadContentType = contentType } : _options;
            var files = source ?? throw new DeliveryException($"the dataset {part.RecordId} enrichment '{part.Step}' adds registers files, and the work names nowhere to read them from");
            var staged = await DatasetUploads.UploadAsync(_client, _datasets, options, work, PayloadName(index), files, entityType, _requestBodyCeiling, plan.Steps, ct).ConfigureAwait(false);
            var record = PartRecord(work, part);
            if (record["data"] is not JsonObject data)
            {
                data = new JsonObject();
                record["data"] = data;
            }

            if (data["Name"] is null && staged.Files.Count > 0)
            {
                data["Name"] = staged.Files[0].Name;
            }

            DatasetUploads.Point(record, staged);
            plan.Staged.Add(new StagedPart(plan, index, part, record));
        }
    }

    /// <summary>
    /// The record a part writes: what its enrichment mapping rendered, under the id and kind the part names, with the enriched
    /// record's own access list and legal block, which a record beside it shares, so whoever may read the one may read the
    /// other.
    /// </summary>
    private static JsonObject PartRecord(DeliveryWork work, EnrichPart part)
    {
        var record = EnrichParts.RecordOf(part);
        record["id"] = part.RecordId;
        record["kind"] = part.Kind;
        record["acl"] = work.Document["acl"]?.DeepClone() ?? throw new RecordHeldException($"the rendered record has no acl block to copy onto the record {part.RecordId} its enrichment {part.Step} adds");
        record["legal"] = work.Document["legal"]?.DeepClone() ?? throw new RecordHeldException($"the rendered record has no legal block to copy onto the record {part.RecordId} its enrichment {part.Step} adds");
        return record;
    }

    /// <summary>
    /// Writes the staged parts, twenty to a request, each declared first: datasets with files registered through the Dataset
    /// service and checked to answer retrieval instructions, every other record written through storage. A request refused as
    /// a whole is tried part by part, so one bad record fails its own enriched record alone.
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
        var intents = await RegistrationIntents.DeclareAsync(
            _records, chunk.Select(c => new Registration(SlotFor(c), c.Part.RecordId)).ToList(), ct).ConfigureAwait(false);
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

            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["recordId"] = item.Part.RecordId, ["step"] = item.Part.Step, [SlotValue] = declared.Slot,
            };
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
        var datasets = ready.Where(r => r.Part.Part.RegistersFiles).ToList();
        var landed = new Dictionary<string, long?>(StringComparer.Ordinal);
        var retrievable = new HashSet<string>(StringComparer.Ordinal);
        if (datasets.Count > 0)
        {
            foreach (var (id, version) in await _datasets.RegisterAsync(datasets.Select(r => r.Part.Record).ToList(), ct).ConfigureAwait(false))
            {
                landed[id] = version;
            }

            retrievable.UnionWith(await _datasets.RetrievableAsync(datasets.Select(r => r.Part.Part.RecordId).ToList(), ct).ConfigureAwait(false));
        }

        foreach (var (item, declared) in ready)
        {
            var id = item.Part.RecordId;
            long? version;
            var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["recordId"] = id, ["step"] = item.Part.Step };
            if (item.Part.RegistersFiles)
            {
                if (!retrievable.Contains(id))
                {
                    item.Plan.Failure ??= new DeliveryException($"the dataset service registered {id}, the dataset enrichment '{item.Part.Step}' adds, and answers no retrieval instructions for it, so its files cannot be read back; the next try registers it again");
                    continue;
                }

                version = landed.GetValueOrDefault(id);
                values["retrievable"] = "true";
            }
            else
            {
                try
                {
                    (version, _) = await RecordWriter.SendAsync(_client, _options, _options.RecordPath ?? OsduRecordProtocol.DefaultRecordPath, "PUT", item.Record, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or JsonException)
                {
                    item.Plan.Failure ??= new DeliveryException($"storage refused {id}, the record enrichment '{item.Part.Step}' adds: {ex.Message}", ex);
                    continue;
                }
            }

            if (version is { } v)
            {
                values["version"] = v.ToString(CultureInfo.InvariantCulture);
                item.Plan.Versions[id] = v;
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
    /// The record goes as the flow's route removes it. Its enrichment records stay at the reversible scopes, since the record's
    /// earlier versions link them and a restored record finds them; removing everything purges them too, as the dataset route
    /// purges a record's own dataset.
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

        var added = EnrichParts.Records(targetState);
        if (scope != RemovalScope.Everything || added.Count == 0)
        {
            return outcome;
        }

        var purged = 0;
        foreach (var id in added)
        {
            var removed = await RecordWriter.DeleteAsync(_client, _removals, id, RemovalScope.Everything, ct).ConfigureAwait(false);
            if (removed.Deleted || removed.AlreadyGone)
            {
                purged++;
            }
        }

        return outcome with { Detail = string.Create(CultureInfo.InvariantCulture, $"{outcome.Detail}; {purged} of the {added.Count} record(s) its enrichments added purged too") };
    }

    /// <summary>Removes as the flow's route batches its removals, except removing everything, which purges each record's enrichment records after it.</summary>
    public Task<IReadOnlyList<RemovalResult>> DeleteBatchAsync(IReadOnlyList<RecordRemoval> removals, RemovalScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(removals);
        return (scope == RemovalScope.Everything && removals.Any(r => EnrichParts.Records(r.TargetState).Count > 0))
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
    /// The flow's own services as its route asks them, then the Dataset service the enrichments register their files through
    /// (when one does), then the Workflow service and the workflows each one's stages name, as the workflow route asks them.
    /// </summary>
    public async Task<ProbeOutcome> ProbeAsync(CancellationToken ct = default)
    {
        var probe = await _inner.ProbeAsync(ct).ConfigureAwait(false);
        if (!probe.Reachable)
        {
            return probe;
        }

        if (_registersFiles)
        {
            probe = await RecordWriter.ProbeAsync(_client, DatasetService.DefaultProbePath, ct).ConfigureAwait(false);
        }

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
    /// Undoes what unfinished deliveries left: an enrichment record a unit created is removed reversibly once storage confirms
    /// OSDU created it after the unit began (a dataset through the Dataset service's removal, any other record through
    /// storage's), and one a unit wrote a new version of is given back the version before, as the dataset route undoes its own
    /// datasets; everything else is the flow's own route's to undo.
    /// </summary>
    public async Task<IReadOnlyList<UndoResult>> UndoAsync(IReadOnlyList<UndoWork> works, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(works);
        var datasetSide = new RecordSide
        {
            Remove = _datasets.SoftDeleteAsync,
            Read = _records.ReadAsync,
            Restorer = _records,
            Versions = _records.VersionsAsync,
        };
        var recordSide = datasetSide with { Remove = (id, token) => RecordWriter.DeleteAsync(_client, _removals, id, RemovalScope.Record, token) };
        var results = new List<UndoResult>();
        var rest = new List<UndoWork>();
        foreach (var work in works)
        {
            // A newer try writes the same id again from rows and files that may differ, so what this unit wrote is undone whatever
            // the newer work is, as the dataset route undoes the dataset a record keeps its files in.
            var datasets = work.Items.Where(i => IsIn(i.Artifact.Slot, DatasetSlot)).ToList();
            if (datasets.Count > 0)
            {
                results.AddRange(await ArtifactUndo.RecordItselfAsync(work with { KeepRecord = false }, datasets, datasetSide, ct).ConfigureAwait(false));
            }

            var records = work.Items.Where(i => IsIn(i.Artifact.Slot, RecordSlot)).ToList();
            if (records.Count > 0)
            {
                results.AddRange(await ArtifactUndo.RecordItselfAsync(work with { KeepRecord = false }, records, recordSide, ct).ConfigureAwait(false));
            }

            // What an enrichment's workflow left (its runs, the records they created) is the workflow route's to undo, under the
            // names it reported them by; the results carry the items as the ledger holds them.
            var claimed = new List<UndoItem>([.. datasets, .. records]);
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

    /// <summary>A part staged to be written: its record's plan, its place among the record's parts, the part, and the record to write.</summary>
    private sealed record StagedPart(Plan Plan, int Index, EnrichPart Part, JsonObject Record);

    /// <summary>What one record's delivery comes to: its parts staged and registered, and the flow's route's write of the record.</summary>
    private sealed class Plan(int index, DeliveryWork work, DeliverySteps steps)
    {
        public int Index { get; } = index;

        public DeliveryWork Work { get; } = work;

        public DeliverySteps Steps { get; } = steps;

        public List<StagedPart> Staged { get; } = [];

        /// <summary>The parts this try registered, or found registered by an earlier try of the same work.</summary>
        public List<EnrichPart> Registered { get; } = [];

        /// <summary>The version OSDU answered each part this try registered with, by its id; none when it answered none.</summary>
        public Dictionary<string, long> Versions { get; } = new(StringComparer.Ordinal);

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
            => (Work.StepParts.Count == 0 && Work.StepKeptRecords.Count == 0)
                || !Work.StepDocumentUnchanged
                || Work.DeliverPayload
                || Work.ExistingVersion is null
                || Work.RestoreFirst
                || !(Work.TargetState.TryGetValue(EnrichParts.DocumentValue, out var written) && string.Equals(written, DocumentHash, StringComparison.Ordinal));

        public DeliveryOutcome Outcome()
        {
            if (Work.StepParts.Count == 0 && Work.StepKeptRecords.Count == 0)
            {
                var outcome = Written ?? DeliveryOutcome.Failed(Failure ?? new DeliveryException("the record was not written"));
                var dropped = EnrichParts.Records(Work.TargetState);
                if (!outcome.Succeeded || dropped.Count == 0)
                {
                    return outcome;
                }

                // The record no longer links the records its enrichments added: they stay live, since earlier versions name them.
                return outcome with
                {
                    Returned = new Dictionary<string, string>(outcome.Returned, StringComparer.Ordinal) { [EnrichParts.RegisteredValue] = EnrichParts.RegisteredText([]) },
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

            // The record links the records added now and those of the enrichments set aside, which an earlier delivery added.
            var linked = Work.StepParts.Select(p => p.Part.RecordId).Concat(Work.StepKeptRecords).ToList();
            var returned = new Dictionary<string, string>(Written?.Returned ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                [EnrichParts.RegisteredValue] = EnrichParts.RegisteredText(linked),
                [EnrichParts.DocumentValue] = DocumentHash,
            };
            foreach (var part in Work.StepParts.Select(p => p.Part))
            {
                returned[EnrichParts.StateKey(part.RecordId)] = part.Hash;
                if (part.RegistersFiles)
                {
                    returned[EnrichParts.FilesStateKey(part.RecordId)] = part.FilesHash;
                }

                if (Versions.TryGetValue(part.RecordId, out var version))
                {
                    returned[EnrichTrace.VersionKey(part.RecordId)] = version.ToString(CultureInfo.InvariantCulture);
                }
            }

            // A record the enriched record no longer links stays live, since its earlier versions name it: superseded, not removed.
            var current = linked.ToHashSet(StringComparer.Ordinal);
            var superseded = EnrichParts.Records(Work.TargetState).Where(id => !current.Contains(id)).ToList();
            var registered = Registered.Count;
            if (Written is { } written)
            {
                return written with
                {
                    Steps = steps,
                    Returned = returned,
                    PayloadDelivered = written.PayloadDelivered || registered > 0,
                    Superseded = [.. written.Superseded, .. superseded],
                    Detail = registered == 0 ? written.Detail : string.Create(CultureInfo.InvariantCulture, $"{written.Detail}{(written.Detail is null ? string.Empty : "; ")}{registered} record(s) of its enrichments written"),
                };
            }

            return new DeliveryOutcome
            {
                MetadataDelivered = true,
                PayloadDelivered = registered > 0,
                TargetVersion = Work.ExistingVersion,
                Detail = string.Create(CultureInfo.InvariantCulture, $"{registered} record(s) of its enrichments written; the record links them already and is not written again"),
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
