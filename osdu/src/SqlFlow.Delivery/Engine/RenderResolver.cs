using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Engine;

/// <summary>A flow's render inputs pinned together: the mapping, its template, the cache, the context and a renderer over them.</summary>
public sealed record ResolvedMapping(
    MappingDefinition Mapping,
    SchemaSnapshot Schema,
    ReferenceSnapshot References,
    RenderContext Context,
    MappingRenderer Renderer)
{
    /// <summary>Where the template was read from: the template folder file on the mapping's path.</summary>
    public string? TemplateOrigin { get; init; }

    /// <summary>
    /// The enrichments of the flow's mapping over <see cref="Renderer"/> (osdu/docs/reference/flow/enrich.md), or null for a
    /// mapping that declares none, whose records <see cref="Renderer"/> renders whole.
    /// </summary>
    public RecordChain? Chain { get; init; }

    /// <summary>The mapping's enrichments, in the order they render; empty for a mapping that declares none.</summary>
    public IReadOnlyList<ResolvedStep> Steps => Chain?.Steps ?? [];

    /// <summary>
    /// Renders one record as a delivery sends it: through the flow's mapping, then every step of its chain over it. This is
    /// the one render a plan, a preview and an intake make, so what a preview shows is what a delivery sends.
    /// </summary>
    public RenderResult Render(SourceRecord record) => Render(record, ChainInputs.None);

    /// <summary>
    /// The record as it is sent, with what its last delivery kept of the steps set aside on their holds and the steps the plan
    /// holds whatever they render (<paramref name="inputs"/>), which only a flow with steps reads.
    /// </summary>
    public RenderResult Render(SourceRecord record, ChainInputs inputs) => Chain is { } chain ? chain.Render(record, inputs) : Renderer.Render(record);

    /// <summary>
    /// True when a record's enrichments leave work for its delivery beyond the document (osdu/docs/reference/flow/enrich.md):
    /// records to add, contributions to keep for one set aside on its holds, or where the rows it read came from to keep.
    /// </summary>
    public bool HasStepWork => Steps.Any(s => s.Step.Records is not null || s.Step.OnHold == EnrichHold.Step || s.Step.ReadsRows);

    /// <summary>
    /// How many rounds of questions a batch of the flow's records can need: the mapping's, then each step's after it, since a
    /// step renders over the record once everything before it has been answered.
    /// </summary>
    public int SearchRounds => Planning.Planner.SearchRounds(Mapping)
        + Steps.Sum(s => (s.Mapping is { } fragment ? Planning.Planner.SearchRounds(fragment) : 0)
            + (s.Records?.Mapping is { } records ? Planning.Planner.SearchRounds(records) : 0));
}

/// <summary>
/// Resolves a flow's <c>render</c> block into a <see cref="ResolvedMapping"/>: the pinned mapping from the repository, the
/// template it fills and the schemas its searches read from the template folders on the mapping's path, the version of the
/// cache of the partition the flow delivers to (its current version, or the one the flow pins) from the catalog, and the
/// mapping parameters the flow supplies. The preflight runs before anything is returned.
/// </summary>
public sealed class RenderResolver
{
    private readonly MappingCatalog _mappings;
    private readonly ICacheStore? _cache;
    private readonly ITemplateSource _templates;
    private readonly ISecretResolver _secrets;
    private readonly IRecordSearch? _search;

    /// <param name="mappings">Where the flow's mapping is read from.</param>
    /// <param name="cache">The partitions' caches, for a mapping that reads one.</param>
    /// <param name="templates">
    /// Where the mapping's template and the schemas its searches pin are read: the template folders on the mapping's path
    /// (<see cref="EngineContext.DocumentTemplates"/>).
    /// </param>
    /// <param name="secrets">Resolves the references the flow supplies as parameters.</param>
    /// <param name="search">
    /// Where the mapping's search sources are answered: the platform the flow delivers to. A mapping that searches and is
    /// resolved without one renders every search as finding nothing, which holds the records that need it.
    /// </param>
    public RenderResolver(MappingCatalog mappings, ICacheStore? cache, ITemplateSource templates, ISecretResolver secrets, IRecordSearch? search = null)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(secrets);
        _mappings = mappings;
        _cache = cache;
        _templates = templates;
        _secrets = secrets;
        _search = search;
    }

    /// <summary>
    /// Resolves the flow's mapping and runs its preflight. The flow is bound to the enrichments its mapping declares first
    /// (<see cref="EnrichBinding"/>), so a flow handed over unbound renders what a run of it renders.
    /// </summary>
    public async Task<ResolvedMapping> ResolveAsync(FlowDefinition flow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var where = KeyPaths.Where(flow);
        var mapping = _mappings.Load(flow.Render.Mapping);
        if (mapping.Fragment)
        {
            throw new FlowValidationException(
                $"{where}: {KeyPaths.Of(flow).Mapping} names {mapping.Reference}, an enrichment mapping (enrichment: true): what an enrichment of another mapping renders for its records, never a record keyed of its own. Pin the mapping of the whole record, which names this one under its enrich:.");
        }

        flow = EnrichBinding.Bind(flow, mapping, _mappings.Load);

        var template = await _templates.ResolveAsync(mapping.TemplateDocument, mapping.Template.Kind, mapping.Template.Version, ct).ConfigureAwait(false);
        var schema = template.Schema
            ?? throw new FlowValidationException($"{where}: mapping {mapping.Reference} cannot render: {template.Problem}");

        // The enrichment mappings render into the record the mapping renders, or beside it, so they read the one cache version
        // and system properties the record is rendered under: a flow reads the cache when any part of its chain does.
        var fragments = LoadFragments(flow);
        var chain = new List<MappingDefinition> { mapping };
        chain.AddRange(fragments.SelectMany(f => new[] { f.Fragment, f.FilesFragment }).OfType<MappingDefinition>());
        var (references, scope, systemProperties) = await CacheAsync(flow, chain, where, ct).ConfigureAwait(false);

        var context = new RenderContext
        {
            MappingReference = mapping.Reference,
            MappingFingerprint = mapping.Fingerprint,
            CacheScope = scope,
            CacheVersion = references.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = await ParametersAsync(flow, mapping, ct).ConfigureAwait(false),
            SystemProperties = systemProperties,
        };

        var searches = await SearchesAsync(_templates, mapping, schema, ct).ConfigureAwait(false);
        var issues = Preflight.Check(mapping, schema, references, context, sourceColumns: null, searches).ToList();
        var steps = new List<ResolvedStep>(fragments.Count);
        var renderSteps = new List<RenderStep>(fragments.Count);
        foreach (var fragment in fragments)
        {
            var resolved = await StepAsync(flow, fragment, mapping, schema, references, context, where, issues, ct).ConfigureAwait(false);
            if (resolved is not null)
            {
                steps.Add(resolved.Value.Step);
                renderSteps.Add(resolved.Value.Context);
            }
        }

        if (flow.Enrich is { Count: > 0 })
        {
            issues.AddRange(Preflight.CheckChain(flow, mapping, schema, steps, where));
        }

        Preflight.ThrowIfFailed(issues, where);
        context = context with { Steps = renderSteps };
        var renderer = new MappingRenderer(mapping, schema, references, context, searches, _search);
        return new ResolvedMapping(mapping, schema, references, context, renderer)
        {
            TemplateOrigin = template.Template?.File,
            Chain = steps.Count == 0 ? null : new RecordChain(renderer, steps),
        };
    }

    /// <summary>
    /// One enrichment of the flow's mapping with the enrichment mapping it renders: one adding paths to the record, or one
    /// rendering the datasets its files are registered as; the other null.
    /// </summary>
    private sealed record StepFragments(EnrichStep Step, MappingDefinition? Fragment, MappingDefinition? FilesFragment);

    /// <summary>
    /// The enrichment mapping each enrichment of the flow's mapping renders, in the order they render, read from the flow's
    /// mappings the way its own mapping is. Binding the flow refused one that is not an enrichment mapping.
    /// </summary>
    private List<StepFragments> LoadFragments(FlowDefinition flow)
    {
        var fragments = new List<StepFragments>();
        foreach (var step in flow.Enrich ?? [])
        {
            fragments.Add(new StepFragments(
                step,
                step.Mapping is { } own ? _mappings.Load(own) : null,
                step.Records is { } records ? _mappings.Load(records.Mapping) : null));
        }

        return fragments;
    }

    /// <summary>
    /// The values <paramref name="mapping"/> renders with: its own defaults, under what the flow supplies and the kind owns,
    /// every reference resolved. A flow without <c>enrich:</c> steps hands its mapping every value it supplies, and the
    /// preflight refuses one the mapping does not declare. A flow with steps supplies one <c>render.parameters</c> block to the
    /// whole chain, so each mapping of it takes the values it declares, and the chain's check refuses a value none declares.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ParametersAsync(FlowDefinition flow, MappingDefinition mapping, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, declared) in mapping.Parameters)
        {
            if (declared.Default is not null)
            {
                parameters[name] = declared.Default;
            }
        }

        // Where a record goes and under whose access and legal terms belongs to the kind, not to any one mapping: a
        // parameter the kind owns that the flow leaves out takes the value the kind gives it, the partition the flow is
        // bound to (its partitions' or its header's, the one its requests carry and its cache is read in) or the reference
        // the kind names. A flow that names its own value still wins, so a document can pin a destination when it has to.
        var supplied = DeliveryDestination.Supplied(flow, mapping.Parameters.Keys);
        var chained = flow.Enrich is { Count: > 0 };

        // What reaches a mapping from outside it is deployment configuration, not mapping content: the partition, the
        // legal tag and the access groups of an estate all differ between test and production while the mapping stays the
        // same. So these carry ${env:NAME} and ${keyvault:vault/secret} references exactly as target.endpoint and
        // target.headers do, and they are expanded here, before the render context is built, so the context (and the
        // rendered hash and the ledger row that records what rendered a document) holds the value that reached the
        // record, never the reference that produced it. A mapping's own default is mapping content and stays literal.
        foreach (var (name, value) in supplied)
        {
            if (chained && !mapping.Parameters.ContainsKey(name))
            {
                continue;
            }

            parameters[name] = await _secrets.ResolveAsync(value, ct).ConfigureAwait(false);
        }

        return parameters;
    }

    /// <summary>
    /// One enrichment resolved: its enrichment mapping's template, which for one adding paths has to be the very template the
    /// flow's mapping fills, its parameters, its searches and its preflight, and a renderer over it under the record's cache
    /// version. What is wrong is added to <paramref name="issues"/>, every problem of every enrichment at once, and the
    /// enrichment is left out (null).
    /// </summary>
    private async Task<(ResolvedStep Step, RenderStep Context)?> StepAsync(
        FlowDefinition flow,
        StepFragments fragments,
        MappingDefinition mapping,
        SchemaSnapshot schema,
        ReferenceSnapshot references,
        RenderContext context,
        string where,
        List<ValidationIssue> issues,
        CancellationToken ct)
    {
        var step = fragments.Step;
        var at = $"{where}: enrich.{step.Name}";
        RenderStep renderStep = new(step.Name, null, null, schema.Version, new Dictionary<string, string>(StringComparer.Ordinal))
        {
            SetAsideOnHold = step.OnHold == EnrichHold.Step,
            SuppliesBulk = step.Bulk is not null,
            RunsWorkflow = step.Workflow is not null,
        };
        MappingRenderer? renderer = null;
        if (fragments.Fragment is { } fragment)
        {
            var template = await _templates.ResolveAsync(fragment.TemplateDocument, fragment.Template.Kind, fragment.Template.Version, ct).ConfigureAwait(false);
            if (template.Schema is not { } filled)
            {
                issues.Add(ValidationIssue.Error($"{at}: enrichment mapping {fragment.Reference} cannot render: {template.Problem}"));
                return null;
            }

            if (!string.Equals(filled.Version, schema.Version, StringComparison.Ordinal))
            {
                issues.Add(ValidationIssue.Error(
                    $"{at}: enrichment mapping {fragment.Reference} fills {filled.Kind} version {filled.Version}, and the flow's mapping {mapping.Reference} fills version {schema.Version}; an enrichment adds to the record the mapping renders, so both fill one version of the template. Pin the same template.version in both, or leave both to the template folder."));
                return null;
            }

            renderStep = new RenderStep(step.Name, fragment.Reference, fragment.Fingerprint, filled.Version, await ParametersAsync(flow, fragment, ct).ConfigureAwait(false))
            {
                SetAsideOnHold = step.OnHold == EnrichHold.Step,
                SuppliesBulk = step.Bulk is not null,
                RunsWorkflow = step.Workflow is not null,
            };
            renderer = await FragmentRendererAsync(fragment, filled, references, context.ForStep(renderStep), $"enrich.{step.Name}", issues, ct).ConfigureAwait(false);
            if (renderer is null)
            {
                return null;
            }
        }

        ResolvedRecords? files = null;
        if (step.Records is { } declared)
        {
            // What decides the datasets' ids enters the context: a new kind or key moves them, and the records render again. Where
            // the record links them is the mapping's $link nodes, which its fingerprint covers.
            var rules = new JsonObject
            {
                ["kind"] = declared.Kind,
                ["key"] = new JsonArray(declared.Key.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()),
                ["through"] = declared.RegistersFiles ? "dataset" : "storage",
            };
            MappingRenderer? filesRenderer = null;
            SchemaSnapshot? filesSchema = null;
            if (fragments.FilesFragment is { } filesFragment)
            {
                var template = await _templates.ResolveAsync(filesFragment.TemplateDocument, filesFragment.Template.Kind, filesFragment.Template.Version, ct).ConfigureAwait(false);
                if (template.Schema is not { } datasetSchema)
                {
                    issues.Add(ValidationIssue.Error($"{at}: enrichment mapping {filesFragment.Reference} cannot render: {template.Problem}"));
                    return null;
                }

                filesSchema = datasetSchema;
                var parameters = await ParametersAsync(flow, filesFragment, ct).ConfigureAwait(false);
                var filesContext = context with
                {
                    MappingReference = filesFragment.Reference,
                    MappingFingerprint = filesFragment.Fingerprint,
                    SchemaSnapshotVersion = datasetSchema.Version,
                    Parameters = parameters,
                    Steps = [],
                };
                filesRenderer = await FragmentRendererAsync(filesFragment, datasetSchema, references, filesContext, $"enrich.{step.Name}", issues, ct).ConfigureAwait(false);
                if (filesRenderer is null)
                {
                    return null;
                }

                var given = new JsonObject();
                foreach (var (name, value) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    given[name] = value;
                }

                rules["mapping"] = filesFragment.Reference;
                rules["mappingFingerprint"] = filesFragment.Fingerprint;
                rules["schema"] = datasetSchema.Version;
                rules["parameters"] = given;
            }

            files = new ResolvedRecords(declared, TargetId.EntityTypeFromKind(declared.Kind), fragments.FilesFragment, filesRenderer) { Schema = filesSchema };
            renderStep = renderStep with { Records = CanonicalJson.ToString(rules) };
        }

        return (new ResolvedStep(step, fragments.Fragment, schema, renderer) { Records = files }, renderStep);
    }

    /// <summary>
    /// A renderer over a fragment of a step, under <paramref name="context"/>, once its searches resolved and its preflight
    /// passed; null, with every problem added to <paramref name="issues"/> under <paramref name="key"/>, when it did not.
    /// </summary>
    private async Task<MappingRenderer?> FragmentRendererAsync(
        MappingDefinition fragment, SchemaSnapshot schema, ReferenceSnapshot references, RenderContext context, string key, List<ValidationIssue> issues, CancellationToken ct)
    {
        var searches = await SearchesAsync(_templates, fragment, schema, ct).ConfigureAwait(false);
        var found = Preflight.Check(fragment, schema, references, context, sourceColumns: null, searches);
        issues.AddRange(found.Select(issue => issue with { Message = $"{key}: {issue.Message}" }));
        return found.Any(issue => issue.Severity == IssueSeverity.Error)
            ? null
            : new MappingRenderer(fragment, schema, references, context, searches, _search);
    }

    /// <summary>
    /// <paramref name="mapping"/>'s searches resolved against the templates they pin, read from
    /// <paramref name="templates"/>: how each property a search compares is indexed. A pinned template that cannot be read
    /// is reported among the problems, with why, for the preflight to list. For a mapping that reads its records' parent
    /// (<c>$parent</c>), the template of the kind its own template, <paramref name="schema"/>, is linked to, settled from the
    /// same folders the way a kind named alone is (osdu/docs/reference/flow/mapping-parent.md); and for a mapping that links its
    /// records' children (<c>$child</c>), the template of each kind it links, settled the same way, which says whether the kind
    /// is linked to the mapping's own (osdu/docs/reference/flow/mapping-child.md).
    /// </summary>
    public static async Task<ResolvedSearches> SearchesAsync(ITemplateSource templates, MappingDefinition mapping, SchemaSnapshot schema, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(schema);
        var searches = await DeclaredSearchesAsync(templates, mapping, ct).ConfigureAwait(false);
        if (mapping.ReadsParent && schema.Parent is { } link)
        {
            var parent = await templates.ResolveAsync(mapping.TemplateDocument, link.Kind, null, ct).ConfigureAwait(false);
            searches = searches.WithParent(new ResolvedParent(link, parent.Schema, parent.Schema is null ? parent.Problem : null));
        }

        if (mapping.LinksChildren)
        {
            var children = new List<ResolvedChild>();
            foreach (var kind in mapping.ChildKinds())
            {
                var child = await templates.ResolveAsync(mapping.TemplateDocument, kind, null, ct).ConfigureAwait(false);
                children.Add(new ResolvedChild(kind, child.Schema, child.Schema is null ? child.Problem : null));
            }

            searches = searches.WithChildren(children);
        }

        return searches;
    }

    /// <summary>The searches <paramref name="mapping"/> declares, resolved against the templates they pin.</summary>
    private static async Task<ResolvedSearches> DeclaredSearchesAsync(ITemplateSource templates, MappingDefinition mapping, CancellationToken ct)
    {
        if (mapping.Searches.Count == 0)
        {
            return ResolvedSearches.None;
        }

        var schemas = new Dictionary<TemplateReference, SchemaSnapshot>();
        var missing = new Dictionary<TemplateReference, string>();
        foreach (var pinned in mapping.Searches.Values.Select(s => s.Schema).Distinct())
        {
            var resolved = await templates.ResolveAsync(mapping.TemplateDocument, pinned.Kind, pinned.Version, ct).ConfigureAwait(false);
            if (resolved.Schema is { } schema)
            {
                schemas[pinned] = schema;
            }
            else
            {
                missing[pinned] = resolved.Problem!;
            }
        }

        return ResolvedSearches.Resolve(mapping, schemas, missing);
    }

    /// <summary>
    /// The partition a flow's requests carry, resolved: its <c>data-partition-id</c> header with every reference expanded,
    /// checked to be a partition a cache can be named by.
    /// </summary>
    private async Task<string> PartitionAsync(FlowDefinition flow, string where, CancellationToken ct)
    {
        var declared = flow.Target.Headers
            .FirstOrDefault(h => h.Key.Equals(CacheScope.PartitionHeader, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(declared))
        {
            throw new FlowValidationException(
                $"{where}: the headers declare no '{CacheScope.PartitionHeader}', so there is no partition whose cache the flow uses.");
        }

        return CacheScope.Normalize(await _secrets.ResolveAsync(declared, ct).ConfigureAwait(false), where);
    }

    /// <summary>
    /// The version of the partition's cache the mapping renders against, the partition, and the system properties its
    /// searches are written under (<see cref="SystemProperties.Pinned"/>; none when it declares no searches).
    /// </summary>
    /// <remarks>
    /// The cache is the one of the partition the flow delivers to (<c>target.headers.data-partition-id</c>). A mapping that
    /// neither reads the cache nor searches renders against no cache at all, so refreshing a cache never moves the render
    /// context of records that never read it.
    /// A mapping that reads the cache renders against a version, the current one or the one the flow pins, and its
    /// searches under that version's properties. A mapping that only searches reads no cached record, so it renders
    /// against no version and is pinned to the properties alone: those of the version the flow names, read without its
    /// records. Pinning it to the version would render every one of its records again, and ask the platform every one of
    /// their questions again, whenever a capture changed reference data it never reads.
    /// A flow with <c>enrich:</c> steps renders each record with its mapping and every step's fragment, so the record reads
    /// what any of them reads: <paramref name="chain"/> is the mapping, then the fragments.
    /// </remarks>
    private async Task<(ReferenceSnapshot References, string? Scope, IReadOnlyList<SystemProperty> SystemProperties)> CacheAsync(
        FlowDefinition flow, IReadOnlyList<MappingDefinition> chain, string where, CancellationToken ct)
    {
        var readsCache = chain.Any(m => m.CacheTypesRead().Count > 0);
        var searches = chain.Any(m => m.Searches.Count > 0);
        if (!readsCache && !searches)
        {
            return (ReferenceSnapshot.Empty, null, []);
        }

        // The mapping a message names: the first of the chain that reads the cache.
        var mapping = chain.FirstOrDefault(m => m.CacheTypesRead().Count > 0) ?? chain[0];

        // The cache holds one partition's reference data, so it is keyed by the partition the flow actually reaches, not
        // by the text the document spells it with. An estate that names its partition ${env:...} would otherwise key its
        // cache by that text: the same reference in two estates delivering to two partitions would share one cache, and
        // two documents naming one partition different ways would each need their own capture of identical data.
        var scope = await PartitionAsync(flow, $"{where}: target", ct).ConfigureAwait(false);
        if (!readsCache)
        {
            return (ReferenceSnapshot.Empty, null, SystemProperties.Pinned(await SearchPropertiesAsync(flow, scope, where, ct).ConfigureAwait(false)));
        }

        if (_cache is null)
        {
            throw new FlowValidationException(
                $"{where}: mapping {mapping.Reference} reads the cache of partition '{scope}', and caches live in the module's database, which this host was started without. {OsduModuleDatabase.StartWithCatalogConnection}");
        }

        var version = flow.Render.CacheVersion;
        if (version.Equals(FlowRender.CurrentCacheVersion, StringComparison.OrdinalIgnoreCase))
        {
            version = await _cache.CurrentVersionAsync(scope, ct).ConfigureAwait(false)
                ?? throw new FlowValidationException(
                    $"{where}: mapping {mapping.Reference} reads the cache of partition '{scope}', which holds no version yet. Refresh a cache flow that builds it (one naming '{scope}' under partitions, or whose source.headers.data-partition-id is '{scope}') to capture one.");
        }

        ReferenceSnapshot? references;
        try
        {
            references = await _cache.LoadAsync(scope, version, ct).ConfigureAwait(false);
        }
        catch (CacheVersionPrunedException pruned)
        {
            // The current version is never pruned, so only a pin can name a pruned one.
            throw PinnedVersionPruned(where, pruned);
        }

        if (references is null)
        {
            throw new FlowValidationException($"{where}: render.cacheVersion pins version {version} of the cache of partition '{scope}', which the catalog does not hold.");
        }

        return (references, scope, searches ? SystemProperties.Pinned(references.SystemProperties) : []);
    }

    /// <summary>What a flow pinning a version the cache's retention pruned is told: what happened, and the two ways on.</summary>
    private static FlowValidationException PinnedVersionPruned(string where, CacheVersionPrunedException pruned)
        => new(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{where}: render.cacheVersion pins version {pruned.Version} of the cache of partition '{pruned.Scope}', whose records the cache's retention pruned at {pruned.PrunedUtc:yyyy-MM-dd HH:mm:ss}Z, so nothing can render against it. Pin a version the cache still holds ('sqlflow cache list {pruned.Scope}' lists them), or remove render.cacheVersion to render against the current version. A version a synced delivery flow pins is kept from the next refresh on."),
            pruned);

    /// <summary>
    /// The system properties of the version of the partition's cache the flow names, read from the version's row alone:
    /// none when it names the current version and the partition holds none yet, or when the host has no module database,
    /// and a search then asks exact questions only. A version the flow pins that the catalog does not hold is refused, as
    /// it is for a mapping that reads the cache.
    /// </summary>
    private async Task<IReadOnlyList<SystemProperty>> SearchPropertiesAsync(FlowDefinition flow, string scope, string where, CancellationToken ct)
    {
        if (_cache is null)
        {
            return [];
        }

        var pinned = flow.Render.CacheVersion.Equals(FlowRender.CurrentCacheVersion, StringComparison.OrdinalIgnoreCase) ? null : flow.Render.CacheVersion;
        var version = await _cache.VersionAsync(scope, pinned, ct).ConfigureAwait(false);
        if (version is not null)
        {
            return version.SystemProperties;
        }

        return pinned is null
            ? []
            : throw new FlowValidationException($"{where}: render.cacheVersion pins version {pinned} of the cache of partition '{scope}', which the catalog does not hold.");
    }
}
