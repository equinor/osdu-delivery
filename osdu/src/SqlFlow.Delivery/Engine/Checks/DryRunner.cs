using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine.Checks;

/// <summary>
/// Does what a delivery does to a flow's staged rows, up to sending them (osdu/docs/reference/concepts/dry-run.md). The rows come
/// from the read a value check makes (<see cref="ValueChecker"/>), a batch at a time; each is planned by the planner a run
/// plans with, against the ledger for what the next run would do with it, which renders what it would send, and rendered once
/// more without the ledger only where the plan had no need to, so every row renders once and an unchanged row still shows its
/// document. Every document is then held to the template the mapping pins and to the schema the partition's Schema service
/// holds, the records it refers to are looked up, and an id made from a key that nothing has claimed is asked of OSDU.
/// </summary>
/// <remarks>
/// <para>
/// A dry run of any size holds one batch of documents at a time: a document is dropped when its row has been judged, and what is
/// kept of a row is a line of it, capped (<see cref="DryRunLimits"/>), so memory is the same for a million rows as for a
/// thousand and every count stays exact. What it asks of OSDU is asked once a batch, however many records ask it.
/// </para>
/// <para>
/// Nothing is written: a plan reads the ledger, the source and the payload files. A service that cannot be asked (the Schema
/// service, the legal service, storage) leaves what it decides not checked, with why, and the run goes on: a dry run is how a
/// person finds out what is wrong, so it never stops at the first thing that is.
/// </para>
/// </remarks>
internal sealed class DryRunner
{
    private readonly FlowRuntime _runtime;
    private readonly DryRunLimits _limits;
    private readonly SchemaCrossCheck _cross;
    private readonly DryRunLegalTags _legal;
    private readonly Planner? _unledgered;
    private readonly List<(long At, DryRunRecord Record)> _stopped = [];
    private readonly List<(long At, DryRunRecord Record)> _speaking = [];
    private readonly List<(long At, DryRunRecord Record)> _clean = [];
    private readonly Dictionary<(string At, string Rule), RuleTally> _rules = [];
    private readonly HashSet<string> _own = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notes = new(StringComparer.Ordinal);
    private long _rows;
    private long _ready;
    private long _attention;
    private long _held;
    private long _passedOver;
    private long _create;
    private long _update;
    private long _unchanged;
    private long _hold;
    private long _missing;
    private long _occupied;
    private long _rulesUnlisted;
    private VerdictTally _template;
    private VerdictTally _schema;

    private DryRunner(FlowRuntime runtime, DryRunLimits limits, SchemaCrossCheck cross, DryRunLegalTags legal)
    {
        _runtime = runtime;
        _limits = limits;
        _cross = cross;
        _legal = legal;
        var context = runtime.Context;
        _unledgered = context.Ledger is null ? null : new Planner(runtime.Source, context.Payloads, ledger: null, context.Loggers.CreateLogger<Planner>());
    }

    /// <summary>Asks once what holds for every record alike: what the Schema service holds of the kind, and what the legal service says of the tags.</summary>
    public static async Task<DryRunner> OpenAsync(FlowRuntime runtime, DryRunLimits? limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var cross = await SchemaCrossCheck.OpenAsync(runtime, ct).ConfigureAwait(false);
        return new DryRunner(runtime, limits ?? DryRunLimits.Default, cross, await LegalTagsAsync(runtime, ct).ConfigureAwait(false));
    }

    /// <summary>Takes one batch of rows through everything a delivery does short of sending them, and keeps what they came to.</summary>
    public async Task RowsAsync(PlanHeader header, IReadOnlyList<SourceRecord> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        var flow = _runtime.Flow;
        var parallelism = flow.Parallelism.EffectiveRenderThreads;
        var decided = await PlanAsync(_runtime.Planner, header, rows, parallelism, ct).ConfigureAwait(false);

        // What the plan had no need to render (a row it skips as unchanged, a record the ledger keeps blocked) renders now, as a
        // first delivery would, so every row has its document and no row renders twice.
        var unrendered = rows.Where(row => decided.GetValueOrDefault(Named(row)) is { Render: null } entry && Renderable(row, entry)).ToList();
        var rendered = unrendered.Count > 0 && _unledgered is not null
            ? await PlanAsync(_unledgered, header, unrendered, parallelism, ct).ConfigureAwait(false)
            : new Dictionary<string, PlanEntry>(StringComparer.Ordinal);

        var work = rows
            .Select(row => decided.GetValueOrDefault(Named(row)) is { } entry
                ? new Work(row, entry, entry.Render ?? rendered.GetValueOrDefault(Named(row))?.Render)
                : null)
            .OfType<Work>()
            .ToList();
        Judge(work, parallelism, ct);
        foreach (var item in work)
        {
            if (item.Render is { IsHeld: false } render)
            {
                item.Added = await _cross.AddedOfAsync(render, ct).ConfigureAwait(false);
                Own(render.TargetId);
            }
        }

        var (answers, unasked) = await _cross.ReferencesAsync(
            work.SelectMany(w => (w.Service?.References ?? []).Concat(w.Template?.References ?? []).Concat(w.Added.SelectMany(a => a.Findings?.References ?? []))).ToList(),
            ct).ConfigureAwait(false);
        Note(unasked);
        var occupied = await OccupiedAsync(work, ct).ConfigureAwait(false);
        var now = _runtime.Context.Time.GetUtcNow().UtcDateTime;
        var owned = OwnAnswered(answers);
        foreach (var item in work)
        {
            Take(item, owned, occupied, now);
        }
    }

    /// <summary>What every batch came to.</summary>
    public DryRun Result()
    {
        // What stops a delivery is listed before what only asks for a look, and that before a clean row, however the rows were read.
        var listed = _stopped.Concat(_speaking).Concat(_clean).Take(_limits.MaxListed).OrderBy(line => line.At).Select(line => line.Record).ToList();
        var rules = _rules
            .OrderByDescending(rule => rule.Value.Records).ThenBy(rule => rule.Key.At, StringComparer.Ordinal).ThenBy(rule => rule.Key.Rule, StringComparer.Ordinal)
            .ToList();
        var cut = _rows > listed.Count;
        var notes = _notes.ToList();
        if (cut)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{_rows:N0} rows were read and {listed.Count:N0} are listed: rows with something to say first, then clean rows in the order they were read. The counts cover every row."));
        }

        return new DryRun
        {
            Outcomes = new DryRunOutcomeCounts { Rows = _rows, Ready = _ready, Attention = _attention, Held = _held, PassedOver = _passedOver },
            NextRun = new DryRunNextRun { Create = _create, Update = _update, Unchanged = _unchanged, Hold = _hold },
            Template = _template.Counts(),
            Schema = _schema.Counts(),
            SchemaMatch = _cross.Match,
            AddedSchemas = _cross.Added,
            LegalTags = _legal,
            WithMissingReferences = _missing,
            Occupied = _occupied,
            Rules = rules.Take(_limits.MaxRules)
                .Select(rule => new DryRunRule(rule.Key.At, rule.Key.Rule, rule.Value.Records, rule.Value.ExampleKey, rule.Value.ExampleMessage, rule.Value.ExampleValue))
                .ToList(),
            RulesUnlisted = _rulesUnlisted + rules.Skip(_limits.MaxRules).Sum(rule => rule.Value.Records),
            Records = listed,
            Cut = cut,
            Notes = notes,
        };
    }

    /// <summary>
    /// What the legal service says of the tags on every record, as an answer whatever it says: a run would stop on a refusal,
    /// and a dry run says so and goes on to everything else.
    /// </summary>
    private static async Task<DryRunLegalTags> LegalTagsAsync(FlowRuntime runtime, CancellationToken ct)
    {
        try
        {
            var (tags, refused) = await runtime.AskLegalTagsAsync(ct).ConfigureAwait(false);
            return tags.Count == 0 ? new DryRunLegalTags(DryRunLegalStates.NotApplicable, tags)
                : refused is null ? new DryRunLegalTags(DryRunLegalStates.NotAsked, tags, "The target does not ask the legal service: validateLegalTags is off, or the route has no path to it.")
                : refused.Count == 0 ? new DryRunLegalTags(DryRunLegalStates.Accepted, tags)
                : new DryRunLegalTags(
                    DryRunLegalStates.Refused,
                    tags,
                    string.Join("; ", refused.OrderBy(tag => tag.Key, StringComparer.Ordinal).Select(tag => tag.Key + ": " + tag.Value)));
        }
        catch (Exception ex) when (SchemaCrossCheck.Unreached(ex, ct))
        {
            return new DryRunLegalTags(DryRunLegalStates.NotChecked, [], $"The legal service could not be asked: {HeaderRedaction.RedactMessage(ex.Message)}");
        }
    }

    private static async Task<IReadOnlyDictionary<string, PlanEntry>> PlanAsync(
        Planner planner, PlanHeader header, IReadOnlyList<SourceRecord> rows, int parallelism, CancellationToken ct)
    {
        var entries = new Dictionary<string, PlanEntry>(rows.Count, StringComparer.Ordinal);
        await foreach (var entry in planner.EntriesOfAsync(header, Stream(rows, ct), parallelism, new PlanSummary(), ct).WithCancellation(ct).ConfigureAwait(false))
        {
            entries[entry.SourceKeyJson ?? entry.SourceKey] = entry;
        }

        return entries;
    }

    private static async IAsyncEnumerable<SourceRecord> Stream(IReadOnlyList<SourceRecord> rows, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            yield return row;
        }
    }

    private string Named(SourceRecord row)
    {
        if (row.SourceKeyJson is { } json)
        {
            return json;
        }

        var renderer = _runtime.Mapping.Renderer;
        _ = renderer.DeriveKey(row.Row, out var values);
        return SourceKey.Display(_runtime.Mapping.Mapping.Dataset.System, values);
    }

    /// <summary>Whether a render would say anything of a row the plan rendered nothing for: not one the source cannot give, or holds out.</summary>
    private static bool Renderable(SourceRecord row, PlanEntry entry)
        => entry.Key is not null && row.Hold is null && row.DeletedUtc is null && entry.SkipTier != SkipTier.SourceRemoved;

    /// <summary>Holds each document to the template and to the Schema service's schema, on up to <paramref name="parallelism"/> threads.</summary>
    private void Judge(IReadOnlyList<Work> work, int parallelism, CancellationToken ct)
    {
        var flow = _runtime.Flow;
        var template = SchemaRules.Of(_runtime.Mapping.Schema);
        var service = _cross.Rules;
        Parallel.ForEach(
            work.Where(w => w.Render is { IsHeld: false }),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct },
            item =>
            {
                var document = item.Render!.Document;
                var filled = ValidationGate.RouteFilled(flow, document);
                item.Template = RecordValidator.Check(document, template, filled);
                item.Service = service is null ? null : RecordValidator.Check(document, service, filled);
            });
    }

    private void Own(string? targetId)
    {
        if (targetId is not null && _own.Count < _limits.MaxOwnIds)
        {
            _own.Add(ReferenceResolver.Key(targetId));
        }
    }

    /// <summary><paramref name="answers"/> with a reference to a record this run would deliver read as no reference to nothing.</summary>
    private Dictionary<string, ReferenceAnswer> OwnAnswered(IReadOnlyDictionary<string, ReferenceAnswer> answers)
    {
        var answered = new Dictionary<string, ReferenceAnswer>(answers, StringComparer.Ordinal);
        foreach (var (id, answer) in answers)
        {
            if (answer.State == ReferenceState.Missing && _own.Contains(id))
            {
                answered[id] = new ReferenceAnswer(ReferenceState.NotChecked);
            }
        }

        return answered;
    }

    /// <summary>
    /// The ids of <paramref name="work"/> a delivery would hold as occupied, as an intake finds them
    /// (<see cref="Intake.SubmissionIntake"/>): an id made from a record's key that no ledger has claimed, which OSDU already holds
    /// a record at, unless an operator asked the record to adopt it. A target that keeps records under keys of its own stores
    /// nothing at the id, and is not asked. Answers none when OSDU could not be asked, and says why.
    /// </summary>
    private async Task<IReadOnlySet<string>> OccupiedAsync(IReadOnlyList<Work> work, CancellationToken ct)
    {
        var mapping = _runtime.Mapping.Mapping;
        if (mapping.Dataset.IdFrom != MappingIdSource.Key || DspdmKinds.Is(mapping.Kind) || EtpKinds.Is(mapping.Kind))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var ids = work
            .Where(item => item.Render is { IsHeld: false, TargetId: not null } && item.Decided.Existing?.ClaimedTargetId is null && item.Decided.Existing?.AdoptRequestedUtc is null)
            .Select(item => item.Render!.TargetId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            var flow = _runtime.Flow;
            var client = await _runtime.TargetClientAsync(ct).ConfigureAwait(false);
            var claimed = _runtime.Context.Ledger is { } ledger
                ? await ledger.ClaimedTargetIdsAsync(ids, ct).ConfigureAwait(false)
                : new HashSet<string>(StringComparer.Ordinal);
            return await StoragePresence.PresentAsync(
                client, flow.Target.ProtocolOptions.VerifyBatchPath ?? OsduRecordProtocol.DefaultVerifyBatchPath, ids.Where(id => !claimed.Contains(id)).ToList(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (SchemaCrossCheck.Unreached(ex, ct))
        {
            Note($"Whether OSDU already holds records at the ids made from their keys could not be asked: {HeaderRedaction.RedactMessage(ex.Message)}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void Note(string? note)
    {
        if (note is not null && _notes.Count < 20)
        {
            _notes.Add(note);
        }
    }

    /// <summary>Adds one row to the counts, and what is listed of it.</summary>
    private void Take(Work item, IReadOnlyDictionary<string, ReferenceAnswer> answers, IReadOnlySet<string> occupied, DateTime now)
    {
        var at = _rows++;
        var (decided, render) = (item.Decided, item.Render);
        var template = item.Template is null ? null : ValidationVerdict.Of(item.Template, SchemaRules.Of(_runtime.Mapping.Schema), VerdictSchema.Template, answers, now);
        var service = item.Service is null ? null : ValidationVerdict.Of(item.Service, _cross.Rules, VerdictSchema.SchemaService, answers, now);
        var addedProblems = item.Added.Sum(added => added.Findings is null || added.Schema.Rules is null
            ? 0
            : ValidationVerdict.Of(added.Findings, added.Schema.Rules, VerdictSchema.SchemaService, answers, now).ProblemCount);
        var isOccupied = render?.TargetId is { } id && occupied.Contains(id);
        var (outcome, reason) = Decide(item, template, service, addedProblems, isOccupied);

        var name = RecordPreviewer.ActionName(decided.Action);
        Count(outcome, name);
        _template = _template.Add(item.Template);
        _schema = _schema.Add(item.Service);
        var missing = (service ?? template)?.References.Missing ?? 0;
        _missing += missing > 0 ? 1 : 0;
        _occupied += isOccupied ? 1 : 0;
        Tally(service ?? template, decided.SourceKey);

        var record = new DryRunRecord
        {
            SourceKey = decided.SourceKey,
            KeyJson = decided.Key is null ? null : decided.SourceKeyJson,
            DeliveryKey = decided.Key?.Value,
            Label = decided.Label,
            File = decided.Origin.FileName,
            Row = decided.Origin.RowNumber,
            Outcome = outcome,
            Reason = reason is null ? null : Clip(HeaderRedaction.RedactMessage(reason)),
            NextRun = name,
            TargetId = render?.TargetId ?? decided.TargetId,
            Template = template is null ? null : ValidationOutcomes.Name(template.Outcome),
            TemplateProblems = template?.ProblemCount ?? 0,
            Schema = service is null ? null : ValidationOutcomes.Name(service.Outcome),
            SchemaProblems = service?.ProblemCount ?? 0,
            AssertionFailures = render?.Assertions?.Failed ?? 0,
            MissingReferences = missing,
            Occupied = isOccupied,
            AddedRecords = item.Added.Count,
            AddedProblems = addedProblems,
        };

        var kept = outcome switch
        {
            DryRunOutcomes.Held => _stopped,
            DryRunOutcomes.Ready => _clean,
            _ => _speaking,
        };
        if (kept.Count < _limits.MaxListed)
        {
            kept.Add((at, record));
        }
    }

    private void Count(string outcome, string nextRun)
    {
        switch (outcome)
        {
            case DryRunOutcomes.Ready:
                _ready++;
                break;
            case DryRunOutcomes.Attention:
                _attention++;
                break;
            case DryRunOutcomes.Held:
                _held++;
                break;
            default:
                _passedOver++;
                break;
        }

        switch (nextRun)
        {
            case "create":
                _create++;
                break;
            case "skip":
                _unchanged++;
                break;
            case "hold" or "blocked":
                _hold++;
                break;
            default:
                _update++;
                break;
        }
    }

    /// <summary>
    /// What a row comes to and the first thing to know about it. Whatever stops a delivery comes first, in the order a delivery
    /// would meet it; then what a delivery would send regardless and a reader should look at, the Schema service's word first,
    /// since it is what the partition holds the record to.
    /// </summary>
    private (string Outcome, string? Reason) Decide(Work item, ValidationVerdict? template, ValidationVerdict? service, long addedProblems, bool occupied)
    {
        var (decided, render) = (item.Decided, item.Render);
        var flow = _runtime.Flow;
        if (decided.SourceDeletedUtc is not null || decided.SkipTier == SkipTier.SourceRemoved)
        {
            return (DryRunOutcomes.PassedOver, decided.Reason);
        }

        if (render is null)
        {
            return (DryRunOutcomes.Held, decided.Reason);
        }

        if (render.IsHeld)
        {
            return (DryRunOutcomes.Held, render.Holds[0]);
        }

        if (render.Assertions?.Holds == true)
        {
            return (DryRunOutcomes.Held, render.Assertions.HoldMessage());
        }

        if (template is not null && flow.Target.Validation.Holds(template.Outcome))
        {
            return (DryRunOutcomes.Held, template.HoldMessage(ValidationPolicy.Setting(template.Outcome)));
        }

        var references = (service ?? template)?.References;
        if (references is { Missing: > 0 } && flow.Target.VerifyReferences == ReferenceVerification.Storage)
        {
            return (DryRunOutcomes.Held, $"It refers to {references.MissingListed[0].Id} ({references.MissingListed[0].Path}), which nothing holds, and target.verifyReferences is storage.");
        }

        if (occupied)
        {
            return (DryRunOutcomes.Held, $"OSDU already holds a record at {render.TargetId}, and no ledger claims it, so a delivery holds this record rather than write over it. Adopt it if the record is this flow's.");
        }

        if (service is { Outcome: ValidationOutcome.Invalid } && service.Problems.Count > 0)
        {
            return (DryRunOutcomes.Attention, $"{ValidationVerdict.Where(service.Problems[0])}: {service.Problems[0].Message}");
        }

        if (template is { Outcome: ValidationOutcome.Invalid } && template.Problems.Count > 0)
        {
            return (DryRunOutcomes.Attention, $"{ValidationVerdict.Where(template.Problems[0])}: {template.Problems[0].Message}");
        }

        if (render.Assertions is { Failed: > 0 } assertions)
        {
            return (DryRunOutcomes.Attention, assertions.Summary());
        }

        return addedProblems > 0
            ? (DryRunOutcomes.Attention, string.Create(CultureInfo.InvariantCulture, $"The records its enrichments add break their schemas: {addedProblems} problem(s)."))
            : (DryRunOutcomes.Ready, null);
    }

    private void Tally(ValidationVerdict? verdict, string sourceKey)
    {
        foreach (var rule in (verdict?.Problems ?? []).GroupBy(problem => (problem.At, problem.Rule)))
        {
            if (_rules.TryGetValue(rule.Key, out var known))
            {
                known.Records++;
            }
            else if (_rules.Count < _limits.TrackedRules)
            {
                var first = rule.First();
                _rules[rule.Key] = new RuleTally(sourceKey, Clip(first.Message), Clip(first.Value));
            }
            else
            {
                _rulesUnlisted++;
            }
        }
    }

    private string Clip(string text) => text.Length <= _limits.MaxReasonChars ? text : text[.._limits.MaxReasonChars] + "...";

    /// <summary>One row on its way through a batch: what the plan decided, its render, and what the schemas found of it.</summary>
    private sealed class Work(SourceRecord row, PlanEntry decided, RenderResult? render)
    {
        public SourceRecord Row { get; } = row;

        public PlanEntry Decided { get; } = decided;

        public RenderResult? Render { get; } = render;

        public RecordFindings? Template { get; set; }

        public RecordFindings? Service { get; set; }

        public IReadOnlyList<AddedRecordCheck> Added { get; set; } = [];
    }

    private sealed class RuleTally(string exampleKey, string exampleMessage, string exampleValue)
    {
        public long Records { get; set; } = 1;

        public string ExampleKey { get; } = exampleKey;

        public string ExampleMessage { get; } = exampleMessage;

        public string ExampleValue { get; } = exampleValue;
    }

    /// <summary>
    /// What a schema has said of the documents so far, by its own rules alone: a record a document refers to that nothing holds
    /// is no rule of the schema broken, and is counted apart (<see cref="DryRun.WithMissingReferences"/>).
    /// </summary>
    private readonly record struct VerdictTally(long Valid, long Invalid, long Unverified, long NotChecked)
    {
        public VerdictTally Add(RecordFindings? findings) => findings switch
        {
            null => this with { NotChecked = NotChecked + 1 },
            { ProblemCount: > 0 } => this with { Invalid = Invalid + 1 },
            { UnverifiedCount: > 0 } => this with { Unverified = Unverified + 1 },
            _ => this with { Valid = Valid + 1 },
        };

        public DryRunVerdictCounts Counts() => new() { Valid = Valid, Invalid = Invalid, Unverified = Unverified, NotChecked = NotChecked };
    }
}
