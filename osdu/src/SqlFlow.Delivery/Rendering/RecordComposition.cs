using System.Text.Json.Nodes;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// What a fragment rendered for one record (<see cref="MappingRenderer.RenderFragment"/>): the paths it adds, what it is held
/// for, what it read from the cache and the platform, and what its assertions found. It is never sent on its own: the record's
/// chain adds it to the record (<see cref="RecordChain"/>).
/// </summary>
public sealed record FragmentRender
{
    /// <summary>The paths the fragment adds, laid out from the record's root, normalized.</summary>
    public required JsonObject Document { get; init; }

    public required IReadOnlyList<string> Holds { get; init; }

    public IReadOnlyList<CacheUsage> CacheUsages { get; init; } = [];

    public IReadOnlyList<SearchUsage> SearchUsages { get; init; } = [];

    public IReadOnlyList<PlatformQuestion> Unanswered { get; init; } = [];

    public IReadOnlyList<CoalesceChoice> Choices { get; init; } = [];

    public Validation.AssertionFindings? Assertions { get; init; }
}

/// <summary>What a step's fragment did at one path of the record (osdu/docs/reference/flow/enrich.md, How a fragment adds to the record).</summary>
public enum CompositionEffectKind
{
    /// <summary>The step wrote a value where the record held none: a single value, or an object with everything in it.</summary>
    Set,

    /// <summary>The step added items to a list, each one the list did not hold already.</summary>
    Add,
}

/// <summary>One thing a step's fragment did to the record: the path, set or added to, and what it wrote there.</summary>
/// <param name="Path">The path from the record's root, property names joined by dots (<c>data.ExtensionProperties.WellDb.Grade</c>).</param>
/// <param name="Kind">Whether the step set the path or added items to the list there.</param>
/// <param name="Value">The value it set, or the list of the items it added.</param>
public sealed record CompositionEffect(string Path, CompositionEffectKind Kind, JsonNode Value);

/// <summary>What one step of the flow's <c>enrich:</c> did to one record, as the record's composition shows it.</summary>
public sealed record StepContribution
{
    /// <summary>The step's name, as <c>enrich:</c> declares it.</summary>
    public required string Step { get; init; }

    /// <summary>The fragment the step rendered, as pinned (<c>Name@version</c>); null for a step that registers files alone.</summary>
    public string? Mapping { get; init; }

    /// <summary>How many rows of its dataset the record had; null for a step that reads no rows.</summary>
    public int? Rows { get; init; }

    /// <summary>The paths it set and the items it added, in the order it did them; empty when it added nothing.</summary>
    public IReadOnlyList<CompositionEffect> Effects { get; init; } = [];

    /// <summary>Why the step holds the record, each prefixed with the step (<c>enrich.quality: ...</c>); empty when it holds nothing.</summary>
    public IReadOnlyList<string> Holds { get; init; } = [];

    /// <summary>
    /// True when the step did not render for the record: one before it held the record, or the platform had not answered what
    /// one before it asked yet.
    /// </summary>
    public bool NotRendered { get; init; }

    /// <summary>
    /// The datasets the step's files are registered as (osdu/docs/reference/flow/enrich-files.md), one per
    /// row, each under the id the record's own id and the step make; empty for a step that registers no files.
    /// </summary>
    public IReadOnlyList<StepDataset> Datasets { get; init; } = [];

    /// <summary>
    /// True when the step held and was set aside (<c>onHold: step</c>, osdu/docs/reference/flow/enrich-holds.md): the record
    /// goes ahead with what the step added to its last delivery, which <see cref="Effects"/> lists, and <see cref="Holds"/>
    /// says why the step held, without holding the record.
    /// </summary>
    public bool SetAside { get; init; }

    /// <summary>What the step composed into the record, in order: its fragment, then the links to its datasets. Kept for a step set aside on its hold.</summary>
    public IReadOnlyList<JsonObject> Composed { get; init; } = [];

    /// <summary>For a step set aside, the datasets its last delivery registered, which the record keeps linking.</summary>
    public IReadOnlyList<string> KeptDatasets { get; init; } = [];
}

/// <summary>
/// One dataset a step's files are registered as: its id, made from the record's id, the step and the row's key; its kind; the
/// row of the step's dataset it registers the files of, counting from zero; and what the step's dataset fragment rendered into
/// it, beside the file properties the registration writes.
/// </summary>
public sealed record StepDataset(string Id, string Kind, int Row, JsonObject Record);

/// <summary>
/// One step of a flow's <c>enrich:</c> as a run renders it: the step, the fragment it pins and the renderer over that fragment,
/// which shares the flow's cache version, parameters and platform search; both null for a step that registers files alone.
/// </summary>
public sealed record ResolvedStep(EnrichStep Step, MappingDefinition? Mapping, SchemaSnapshot Schema, MappingRenderer? Renderer)
{
    /// <summary>The files the step registers, resolved; null for a step that registers none.</summary>
    public ResolvedFiles? Files { get; init; }
}

/// <summary>
/// The files a step registers, as a run renders them: the step's settings, the entity type of the dataset kind, and the fragment
/// over that kind rendered into each dataset record with its renderer, or null for a dataset record of its file properties alone.
/// </summary>
public sealed record ResolvedFiles(EnrichFiles Files, string EntityType, MappingDefinition? Mapping, MappingRenderer? Renderer)
{
    /// <summary>The template of the dataset kind <see cref="Mapping"/> fills, which says what its references name; null without a fragment.</summary>
    public SchemaSnapshot? Schema { get; init; }
}

/// <summary>
/// How a fragment is added to the record (osdu/docs/reference/flow/enrich.md, How a fragment adds to the record). Two things
/// only, both of which give the same record however many times they are done: a value is set where the record holds none,
/// and items are added to a list, each one the list does not hold already (compared as canonical JSON), after the items it
/// holds. An object is gone into, property by property. Anything else would change what the flow's mapping or a step before
/// wrote, and is a conflict that holds the record: a different value where the record holds one, or a value of another shape.
/// </summary>
public static class RecordComposer
{
    /// <summary>
    /// Adds <paramref name="fragment"/> to <paramref name="record"/> in place, noting each thing it did in
    /// <paramref name="effects"/> and each conflict, with its path, in <paramref name="conflicts"/>. The record is changed
    /// only where the fragment adds to it; a conflicting path is left as the record held it.
    /// </summary>
    public static void Compose(JsonObject record, JsonObject fragment, List<CompositionEffect> effects, List<string> conflicts)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(conflicts);
        Merge(record, fragment, string.Empty, effects, conflicts);
    }

    private static void Merge(JsonObject into, JsonObject from, string at, List<CompositionEffect> effects, List<string> conflicts)
    {
        foreach (var (name, value) in from)
        {
            if (value is null)
            {
                continue;
            }

            var path = at.Length == 0 ? name : $"{at}.{name}";
            into.TryGetPropertyValue(name, out var held);
            switch (value)
            {
                case JsonObject section when held is null:
                    into[name] = section.DeepClone();
                    effects.Add(new CompositionEffect(path, CompositionEffectKind.Set, section.DeepClone()));
                    break;
                case JsonObject section when held is JsonObject holder:
                    Merge(holder, section, path, effects, conflicts);
                    break;
                case JsonObject:
                    conflicts.Add($"{path}: the step writes an object there, and the record holds {Shape(held!)} from the flow's mapping or a step before it");
                    break;
                case JsonArray items when held is null:
                    into[name] = items.DeepClone();
                    effects.Add(new CompositionEffect(path, CompositionEffectKind.Add, items.DeepClone()));
                    break;
                case JsonArray items when held is JsonArray list:
                    Add(list, items, path, effects);
                    break;
                case JsonArray:
                    conflicts.Add($"{path}: the step adds items to a list there, and the record holds {Shape(held!)} from the flow's mapping or a step before it");
                    break;
                default:
                    if (held is null)
                    {
                        into[name] = value.DeepClone();
                        effects.Add(new CompositionEffect(path, CompositionEffectKind.Set, value.DeepClone()));
                    }
                    else if (held is JsonObject or JsonArray)
                    {
                        conflicts.Add($"{path}: the step sets a single value there, and the record holds {Shape(held)} from the flow's mapping or a step before it");
                    }
                    else if (!string.Equals(Text(held), Text(value), StringComparison.Ordinal))
                    {
                        conflicts.Add($"{path}: the step sets {Text(value)}, and the record already holds {Text(held)} there from the flow's mapping or a step before it");
                    }

                    break;
            }
        }
    }

    /// <summary>Adds each item of <paramref name="items"/> the list does not hold yet, after the items it holds, in their order.</summary>
    private static void Add(JsonArray list, JsonArray items, string path, List<CompositionEffect> effects)
    {
        var present = new HashSet<string>(list.Select(Text), StringComparer.Ordinal);
        var added = new JsonArray();
        foreach (var item in items)
        {
            if (present.Add(Text(item)))
            {
                list.Add(item?.DeepClone());
                added.Add(item?.DeepClone());
            }
        }

        if (added.Count > 0)
        {
            effects.Add(new CompositionEffect(path, CompositionEffectKind.Add, added));
        }
    }

    private static string Text(JsonNode? node) => CanonicalJson.ToString(node);

    private static string Shape(JsonNode node) => node switch
    {
        JsonObject => "an object",
        JsonArray => "a list",
        _ => $"the single value {Text(node)}",
    };
}

/// <summary>
/// The render of a record of a flow with <c>enrich:</c> steps (osdu/docs/reference/flow/enrich.md): the flow's own mapping
/// renders the record, then each step, in the order they render, renders its fragment over the record as it stands and adds it
/// (<see cref="RecordComposer"/>), and the record with every step in it is the document that is hashed and sent. A change to
/// the record's row, to a step's rows, to any mapping or to the cache values any of them read renders the whole chain again,
/// so the record is written once for it, whichever part of the chain moved.
/// </summary>
public sealed class RecordChain
{
    private readonly MappingRenderer _owner;
    private readonly IReadOnlyList<ResolvedStep> _steps;

    /// <param name="owner">The renderer over the flow's own mapping, which keys, names and envelopes the record.</param>
    /// <param name="steps">The flow's steps, in the order they render.</param>
    public RecordChain(MappingRenderer owner, IReadOnlyList<ResolvedStep> steps)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(steps);
        _owner = owner;
        _steps = steps;
    }

    /// <summary>The flow's steps, in the order they render.</summary>
    public IReadOnlyList<ResolvedStep> Steps => _steps;

    /// <summary>The record rendered from its rows alone, as a record OSDU holds no version of.</summary>
    public RenderResult Render(SourceRecord record) => Render(record, ChainInputs.None);

    /// <summary>
    /// Renders the record and every step over it. A record the flow's mapping holds, or renders unfinished, is returned as it
    /// rendered: the steps render once it is whole. A step that renders unfinished stops the chain there, with its questions,
    /// and the whole chain renders again once they are answered, since a later step reads what this one adds. A step that holds,
    /// or that conflicts with what the record holds, holds the record and stops the chain, so every reason given is a true one;
    /// unless it is set aside on its hold (<c>onHold: step</c>, osdu/docs/reference/flow/enrich-holds.md): then what it added to
    /// the record's last delivery (<paramref name="inputs"/>) takes its place, nothing for a record never delivered, and the
    /// steps after it render. A step whose dataset holds no row for the record adds nothing; one that reads one row and finds
    /// several holds.
    /// </summary>
    public RenderResult Render(SourceRecord record, ChainInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(inputs);
        var owner = _owner.Render(record);
        if (_steps.Count == 0 || owner.IsIncomplete || owner.IsHeld)
        {
            return owner;
        }

        var document = (JsonObject)owner.Document.DeepClone();
        var holds = new List<string>();
        var unanswered = new List<PlatformQuestion>();
        var usages = new List<CacheUsage>(owner.CacheUsages);
        var searches = new List<SearchUsage>(owner.SearchUsages);
        var choices = new List<CoalesceChoice>(owner.Choices);
        var findings = new List<Validation.AssertionFindings>();
        if (owner.Assertions is { } own)
        {
            findings.Add(own);
        }

        var contributions = new List<StepContribution>(_steps.Count);
        var trail = new ChainTrail(usages, searches, choices, findings, unanswered);
        var stopped = false;
        foreach (var resolved in _steps)
        {
            var step = resolved.Step;
            var named = new StepContribution { Step = step.Name, Mapping = resolved.Mapping?.Reference };
            if (stopped)
            {
                contributions.Add(named with { NotRendered = true });
                continue;
            }

            var rows = step.ReadsRows ? record.ScopeRows(step.Name) : null;
            named = named with { Rows = rows?.Count };
            if (rows is { Count: 0 })
            {
                // The step has nothing for this record: it adds nothing, and the record is written without it.
                contributions.Add(named);
                continue;
            }

            // The step composes into a copy of the record, which becomes the record once it composed whole: a step that holds
            // leaves the record as the steps before it left it, whether it holds the record or is set aside.
            var working = (JsonObject)document.DeepClone();
            var effects = new List<CompositionEffect>();
            var reasons = new List<string>();
            var datasets = new List<StepDataset>();
            var composed = new List<JsonObject>();
            var waiting = false;
            if (inputs.Holding.TryGetValue(step.Name, out var holding))
            {
                reasons.Add(holding);
            }
            else if (step.Rows == EnrichRows.One && rows is { Count: > 1 })
            {
                reasons.Add(
                    $"the step reads one row per record (rows: one), and its dataset holds {rows.Count} for this record; join it on more columns, or declare rows: many and read them with $forEach: {step.Name}");
            }
            else
            {
                if (resolved.Renderer is { } renderer)
                {
                    var fragment = renderer.RenderFragment(StepRecord(record, step, rows), document);
                    waiting = trail.Gather(fragment);
                    if (!waiting && fragment.Holds.Count > 0)
                    {
                        reasons.AddRange(fragment.Holds);
                    }
                    else if (!waiting)
                    {
                        RecordComposer.Compose(working, fragment.Document, effects, reasons);
                        composed.Add(fragment.Document);
                    }
                }

                if (!waiting && reasons.Count == 0 && resolved.Files is { } files)
                {
                    waiting = AddFiles(record, step, files, rows!, owner.TargetId!, working, datasets, effects, reasons, composed, trail);
                }
            }

            if (waiting)
            {
                // The platform has not answered what the step asked: the whole chain renders again once it has.
                contributions.Add(named with { Effects = effects });
                stopped = true;
                continue;
            }

            if (reasons.Count > 0)
            {
                var held = reasons.Select(r => Prefixed(step, r)).ToList();
                if (step.OnHold == EnrichHold.Step)
                {
                    if (SetAside(step, inputs.Kept, document, out var kept) is not { } refusal)
                    {
                        // Set aside: the record goes ahead with what the step added to its last delivery, and the steps after it render.
                        contributions.Add(named with { Effects = kept.Effects, Holds = held, SetAside = true, KeptDatasets = kept.Datasets });
                        continue;
                    }

                    held.Add(Prefixed(step, refusal));
                }

                holds.AddRange(held);
                contributions.Add(named with { Effects = effects, Holds = held, Datasets = datasets });
                stopped = true;
                continue;
            }

            document = working;
            contributions.Add(named with { Effects = effects, Datasets = datasets, Composed = composed });
        }

        var assertions = Validation.AssertionFindings.Combine(findings);
        if (unanswered.Count > 0)
        {
            // Not finished: the questions are asked and the whole chain renders again. The document is never sent as it stands.
            return owner with
            {
                Unanswered = unanswered.Distinct().ToList(),
                CacheUsages = Distinct(usages),
                SearchUsages = searches.Distinct().ToList(),
                Choices = choices,
                Assertions = assertions,
                Enrichment = contributions,
            };
        }

        var normalized = (JsonObject)CanonicalJson.Normalize(document)!;
        string canonical;
        try
        {
            canonical = CanonicalJson.ToString(normalized);
        }
        catch (InvalidOperationException ex)
        {
            // Every value a fragment writes is converted as the mapping's are before it reaches the record, so this is an
            // engine defect; the failure names the record so it can be found from the log line alone.
            throw new DeliveryException($"{owner.SourceKey}: the record{(owner.TargetId is null ? string.Empty : " " + owner.TargetId)} with its enrich: steps cannot be written as canonical JSON ({ex.Message})", ex);
        }

        return owner with
        {
            Document = normalized,
            Canonical = canonical,
            MetadataHash = ContentHash.Of(canonical),
            Holds = [.. owner.Holds, .. holds],
            CacheUsages = Distinct(usages),
            SearchUsages = searches.Distinct().ToList(),
            Choices = choices,
            Assertions = assertions,
            Enrichment = contributions,
        };
    }

    /// <summary>
    /// What a step's fragment reads as its rows: its one row as the fragment's own, with its rows as the child dataset named
    /// after the step either way, so <c>$forEach: &lt;step&gt;</c> reads them; or no row at all for a step that reads none.
    /// The origin and version are the record's, since the fragment is part of the record's render.
    /// </summary>
    private static SourceRecord StepRecord(SourceRecord record, EnrichStep step, IReadOnlyList<SourceRow>? rows)
    {
        var scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
        if (rows is not null)
        {
            scopes[step.Name] = rows;
        }

        var row = rows is { Count: 1 } && step.Rows == EnrichRows.One ? rows[0] : SourceRow.Empty;
        return record with { Row = row, Scopes = scopes };
    }

    private static string Prefixed(EnrichStep step, string reason) => $"enrich.{step.Name}: {reason}";

    /// <summary>
    /// Sets a held step aside (osdu/docs/reference/flow/enrich-holds.md): what it added to the record's last delivery is composed
    /// into <paramref name="document"/> in its place, nothing for a record OSDU holds no version of. Null when it is set aside,
    /// with what it added in <paramref name="kept"/>; otherwise why the step holds the record after all: OSDU holds a version
    /// whose contribution of the step was not kept, or what it kept no longer fits the record.
    /// </summary>
    private static string? SetAside(EnrichStep step, KeptSteps last, JsonObject document, out (IReadOnlyList<CompositionEffect> Effects, IReadOnlyList<string> Datasets) kept)
    {
        kept = ([], []);
        if (!last.Steps.TryGetValue(step.Name, out var previous))
        {
            return last.Delivered
                ? "the step is set aside on its holds, and what it added to the version OSDU holds was not kept (that version was delivered before the step was set aside), so the record is held until the step renders"
                : null;
        }

        var aside = (JsonObject)document.DeepClone();
        var effects = new List<CompositionEffect>();
        var conflicts = new List<string>();
        foreach (var fragment in previous.Fragments)
        {
            RecordComposer.Compose(aside, (JsonObject)fragment.DeepClone(), effects, conflicts);
        }

        if (conflicts.Count > 0)
        {
            return $"the step is set aside on its holds, and what it added to the version OSDU holds no longer fits the record ({string.Join("; ", conflicts)}), so the record is held until the step renders";
        }

        foreach (var (name, value) in aside.ToList())
        {
            document[name] = value?.DeepClone();
        }

        kept = (effects, previous.Datasets);
        return null;
    }

    /// <summary>
    /// The datasets a step's files are registered as, one per row it takes (osdu/docs/reference/flow/enrich-files.md),
    /// and the links to them added to the record at the step's link path. Each id is the record's own with the
    /// dataset's entity type and a suffix of <c>enrich.</c>, the step's name and, for a step taking many rows, a hash of the row's key, so the
    /// same row names the same dataset on every render and the record names it before anything is sent. What the step's dataset
    /// fragment renders from the row goes into the dataset record. True when that fragment waits for the platform.
    /// </summary>
    private static bool AddFiles(
        SourceRecord record,
        EnrichStep step,
        ResolvedFiles files,
        IReadOnlyList<SourceRow> rows,
        string ownerId,
        JsonObject document,
        List<StepDataset> datasets,
        List<CompositionEffect> effects,
        List<string> reasons,
        List<JsonObject> composed,
        ChainTrail trail)
    {
        var taken = step.Rows == EnrichRows.One ? rows.Take(1).ToList() : rows;
        for (var i = 0; i < taken.Count; i++)
        {
            // The suffix holds a dot, which neither the dataset a record's own files go in (files) nor a workflow input's name can,
            // so a step's dataset never takes the id another route derives from the record's.
            var row = taken[i];
            var suffix = EnrichFiles.SuffixPrefix + step.Name;
            if (files.Files.Key.Count > 0)
            {
                var values = files.Files.Key.Select(column => row.GetString(column)?.Trim()).ToList();
                if (values.FindIndex(string.IsNullOrEmpty) is var empty and >= 0)
                {
                    reasons.Add($"files.key column '{files.Files.Key[empty]}' is empty in row {i + 1} of the step's dataset, and each row's dataset is named by its key");
                    return false;
                }

                suffix = $"{EnrichFiles.SuffixPrefix}{step.Name}.{DeliveryKey.Derive(step.Name, values).Value:N}";
            }

            string id;
            try
            {
                id = Engine.Workflows.WorkflowValues.DerivedDatasetId(ownerId, files.EntityType, suffix);
            }
            catch (DeliveryException ex)
            {
                reasons.Add(ex.Message);
                return false;
            }

            if (datasets.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal)) is { } same)
            {
                reasons.Add($"rows {same.Row + 1} and {i + 1} of the step's dataset name the same dataset {id}: their files.key values are the same");
                return false;
            }

            var dataset = new JsonObject();
            if (files.Renderer is { } renderer)
            {
                var scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase) { [step.Name] = rows };
                var fragment = renderer.RenderFragment(record with { Row = row, Scopes = scopes }, document);
                if (trail.Gather(fragment))
                {
                    return true;
                }

                if (fragment.Holds.Count > 0)
                {
                    reasons.AddRange(fragment.Holds.Select(h => $"the dataset of row {i + 1}: {h}"));
                    return false;
                }

                dataset = fragment.Document;
            }

            datasets.Add(new StepDataset(id, files.Files.Kind, i, dataset));
        }

        if (datasets.Count > 0)
        {
            // The links, as the record holds them (EnrichParts.Link).
            var link = new JsonObject();
            var holder = link;
            var segments = files.Files.Link.Split('.');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var next = new JsonObject();
                holder[segments[i]] = next;
                holder = next;
            }

            holder[segments[^1]] = new JsonArray(datasets.Select(d => (JsonNode?)JsonValue.Create(EnrichParts.Link(d.Id))).ToArray());
            RecordComposer.Compose(document, link, effects, reasons);
            composed.Add(link);
        }

        return false;
    }

    /// <summary>What every fragment of one record's chain read and asked, gathered into the record's render.</summary>
    private sealed class ChainTrail(
        List<CacheUsage> usages, List<SearchUsage> searches, List<CoalesceChoice> choices, List<Validation.AssertionFindings> findings, List<PlatformQuestion> unanswered)
    {
        /// <summary>Takes what <paramref name="fragment"/> read and found; true when it waits for the platform.</summary>
        public bool Gather(FragmentRender fragment)
        {
            usages.AddRange(fragment.CacheUsages);
            searches.AddRange(fragment.SearchUsages);
            choices.AddRange(fragment.Choices);
            if (fragment.Assertions is { } asserted)
            {
                findings.Add(asserted);
            }

            unanswered.AddRange(fragment.Unanswered);
            return fragment.Unanswered.Count > 0;
        }
    }

    /// <summary>One cache usage per cached path the record and its steps consumed.</summary>
    private static IReadOnlyList<CacheUsage> Distinct(List<CacheUsage> usages)
        => usages.DistinctBy(u => (u.TypeName, u.ItemId, u.Path, u.Kind)).ToList();
}
