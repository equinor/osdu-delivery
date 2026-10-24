using SqlFlow.Core.Secrets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Catalog;
using SqlFlow.Catalog.Modules;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Templates;
using SqlFlow.Lineage.Collection;
using ContentHash = SqlFlow.Delivery.Hashing.ContentHash;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The delivery kind's document families in the repository sync: every file of the repository's template folders (each
/// folder named <c>templates</c>, anywhere in the tree), every mapping document (<c>documentType: mapping</c>, anywhere in
/// the tree) with the template the folders on its path settle it to, what every cache flow (<c>flowType: cache</c>)
/// declares it caches, and the interfaces of every delivery flow (<c>flowType: delivery</c>) become rows of the
/// <c>osdu</c> schema, so the GUI lists what a flow renders with, what each cache holds and which pipeline keeps a
/// ledger, without opening the repository. A template folder's file is indexed, never copied: whoever reads a template
/// reads its file (osdu/docs/reference/concepts/templates.md, Template folders). Rows are keyed by repository and reference; a document that disappears from the tree loses its row. The
/// sync only reads the repository: the versions of a cache are written by the runs of its cache flow, never by the sync.
/// <para>
/// The <c>osdu</c> schema lives in the catalog's database, so the <c>osdu</c> context opens on the catalog context's
/// connection and joins its transaction: the rows commit or roll back with the sync.
/// </para>
/// </summary>
public sealed class DeliveryCatalogSync : ICatalogSyncExtension
{
    /// <summary>
    /// The id a mapping is synced under: derived from the repository that declares it and its reference, so the same mapping
    /// keeps its id across syncs, and a page or a check names it without reading the catalog first.
    /// </summary>
    public static Guid MappingId(Guid repoId, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return FlowIdentity.FromName($"delivery-mapping/{repoId:N}/{reference}");
    }

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".sqlflow", "bin", "obj", "node_modules", "runs",
    };

    private static readonly JsonSerializerOptions SummaryJson = new(JsonSerializerDefaults.Web);

    /// <summary>The widths of <c>osdu.RepositoryTemplate</c>'s kind and version, which a mapping row's pin mirrors.</summary>
    private const int MaxTemplateKindLength = 200;

    private const int MaxTemplateVersionLength = 64;

    private readonly DeliveryDocumentLoader _documents;
    private readonly ISecretResolver? _secrets;

    /// <summary>
    /// The sync over <paramref name="documents"/>, writing the module's rows on the catalog's connection, inside the sync's
    /// transaction. <paramref name="secrets"/> resolves a cache document's <c>${env:...}</c> partition, as a run resolves it.
    /// </summary>
    public DeliveryCatalogSync(DeliveryDocumentLoader documents, ISecretResolver? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents;
        _secrets = secrets;
    }

    /// <summary>
    /// The partition <paramref name="cache"/> fills, resolved where this host can resolve it, and as the document writes
    /// it where it cannot. Resolution never fails a sync: a repository is described by what its documents say, and a value
    /// this host has no way to know is not a reason to refuse the document.
    /// </summary>
    private async Task<string> ResolvedScopeAsync(CacheDefinition cache, CancellationToken ct)
    {
        if (_secrets is null)
        {
            return cache.Scope;
        }

        try
        {
            return Snapshots.CacheScope.Normalize(
                await _secrets.ResolveAsync(cache.Scope, ct).ConfigureAwait(false),
                cache.SourcePath ?? cache.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return cache.Scope;
        }
    }

    public async Task<CatalogSyncExtensionResult> SyncAsync(
        CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(warnings);
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"The repository sync of repository {repoId:D} called the delivery extension outside its transaction, so the rows it writes beside the catalog's could not commit with them.");
        }

        await using var osdu = await OpenAsync(context, ct).ConfigureAwait(false);
        var result = await ReconcileAsync(osdu, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false);
        await CheckOrderAsync(context, osdu, repoId, root, warnings, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Checks the repository's waves against the OSDU order of its own deliveries, as the sync has just reconciled them
    /// (osdu/docs/reference/concepts/osdu-order.md, Checking a repository's waves): every flow runs after the flows of the
    /// repository delivering what its records refer to, so a schedule running both runs them in that order. Lineage takes
    /// the order, so a breach is rare: a cycle lineage could only put in its fallback wave, or a flow it gave no wave. The
    /// waves are the ones lineage stamped in this sync's transaction, read from the catalog context's tracked pipelines,
    /// and the deliveries are the repository's own rows, which the sync already holds. A breach is a warning: the sync
    /// still lands. The templates the deliveries' mappings fill are read from the files the sync is reading, in
    /// <paramref name="root"/>.
    /// </summary>
    private static async Task CheckOrderAsync(
        CatalogDbContext catalog, OsduDbContext context, Guid repoId, string root, ICollection<string> warnings, CancellationToken ct)
    {
        var waves = catalog.Pipelines.Local
            .Where(p => p.RepoId == repoId && p.Active && p.Wave >= 0)
            .ToDictionary(p => p.Name, p => p.Wave, StringComparer.OrdinalIgnoreCase);
        var read = await Ordering.OrderCatalog.ReadAsync(context, RepositoryFolders.Of(repoId, root), repoId, ct).ConfigureAwait(false);
        var report = Ordering.RepositoryOrder.Check(repoId, Ordering.RepositoryOrder.Plan(read, new Dictionary<Guid, string>()), waves, read.Problems);
        foreach (var warning in report.Warnings())
        {
            warnings.Add(warning);
        }
    }

    /// <summary>
    /// Whether a mapping document changed since the last sync reconciled the mappings: a delivery flow's OSDU type and cache
    /// reads come from its mapping, so a changed, added or removed mapping changes the lineage while every flow document
    /// stays the same. Compared by path and content hash against the rows the last sync wrote, through the same discovery
    /// the reconciliation uses. A document that is a second declaration of a reference already on record (which the
    /// reconciliation leaves out) is not a change. Cache flows need no check here: they are flows, which the sync compares
    /// itself. A changed template folder is a change too (<see cref="TemplateFoldersChangedAsync"/>): what a <c>ref</c> is
    /// checked against is read off the template a mapping fills, and a mapping that names its template's kind alone fills
    /// whatever version its folders hold. And so is a stored graph that does not hold what the documents describe, whatever
    /// the documents did: one a build drew before a mapping was a node of it (<see cref="StoredGraphLacksMappingsAsync"/>),
    /// which no document change would ever bring forward.
    /// </summary>
    public async Task<bool> LineageInputsChangedAsync(CatalogDbContext context, Guid repoId, string root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (await StoredGraphLacksMappingsAsync(context, repoId, ct).ConfigureAwait(false))
        {
            return true;
        }

        await using var osdu = await OpenAsync(context, ct).ConfigureAwait(false);
        return await MappingsChangedAsync(osdu, repoId, root, ct).ConfigureAwait(false)
            || await TemplateFoldersChangedAsync(osdu, repoId, root, ct).ConfigureAwait(false)
            || await ReleaseBuiltSinceAsync(osdu, repoId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a release of OSDU's lineage holding the kind of a mapping of the repository that no template folder on its
    /// path gives a template was built after the sync that last saw the mapping (osdu/docs/reference/concepts/osdu-order.md,
    /// OSDU's lineage by release). The OSDU order reads such a kind's relationships from the newest built release holding
    /// it, so the lineage computed before the build ordered its flow by nothing, and the first sync after the build computes
    /// it again. Every reconciliation stamps the mappings it sees, so this holds for exactly one sync.
    /// </summary>
    public static Task<bool> ReleaseBuiltSinceAsync(OsduDbContext context, Guid repoId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == repoId && m.Kind != string.Empty && m.TemplatePath == string.Empty)
            .Join(context.SchemaLineageKinds.AsNoTracking(), m => m.Kind, k => k.Kind, (m, k) => new { m.LastSeenUtc, k.LineageId })
            .Join(context.SchemaLineages.AsNoTracking(), x => x.LineageId, l => l.Id, (x, l) => new { x.LastSeenUtc, l.BuiltUtc })
            .AnyAsync(x => x.BuiltUtc > x.LastSeenUtc, ct);
    }

    /// <summary>
    /// Whether the graph the catalog holds for the repository lacks a mapping it should hold: a delivery flow stored as
    /// writing an OSDU type, with no read of a mapping's node. A delivery flow writes an OSDU type because its mapping
    /// names one, and reads that mapping as a node, so a stored graph with the write and without the read was drawn by a
    /// build from before mappings were nodes. The sync only computes lineage again when a document changes, and upgrading
    /// the build changes none, so without this the old picture (every cache type wired straight to the flow, and the ones
    /// a mapping only checks its ids against wired to nothing) would stay until someone edited a flow or pressed sync now.
    /// </summary>
    /// <remarks>
    /// The question is asked of the catalog, which is where the stored graph is, so the answer cannot drift from it: once
    /// a sync has stored the mapping's node this is false, and it stays false. Two flows keep it true, and so have their
    /// repository's lineage computed on every sync until they are put right; each already carries a sync warning. One is a
    /// flow whose mapping cannot be a node (<see cref="OsduLineage.Mapping"/>), which reads what the mapping reads itself.
    /// The other is a flow whose mapping cannot be read while its protocol still registers a dataset kind, which is then
    /// the only OSDU type it is stored as writing.
    /// </remarks>
    public static Task<bool> StoredGraphLacksMappingsAsync(CatalogDbContext catalog, Guid repoId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var types = ServerIdentity.DatasetPrefix + OsduLineage.TypeSystem;
        var mappings = ServerIdentity.DatasetPrefix + OsduLineage.MappingSystem + ":";
        var reads = nameof(LineageRelation.Reads);
        var writes = nameof(LineageRelation.Writes);
        return catalog.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && p.Active && p.Kind == FlowDefinition.FlowTypeName)
            .AnyAsync(
                p => catalog.LineageEdges.Any(e => e.PipelineId == p.Id && e.Relation == writes && e.ObjectKey.StartsWith(types))
                    && !catalog.LineageEdges.Any(e => e.PipelineId == p.Id && e.Relation == reads && e.ObjectKey.StartsWith(mappings)),
                ct);
    }

    /// <summary>
    /// Whether the repository's template folders on disk differ from the files <paramref name="context"/> recorded for it at
    /// the last sync: a file added, removed, or holding another template (or none) now. A changed file can move the
    /// template a mapping that names its template's kind alone fills, and what a <c>ref</c> is checked against with it.
    /// </summary>
    public static async Task<bool> TemplateFoldersChangedAsync(OsduDbContext context, Guid repoId, string root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var stored = await context.DeliveryRepositoryTemplates.AsNoTracking()
            .Where(t => t.RepoId == repoId)
            .Select(t => new { t.Path, t.Kind, t.Version, t.Problem })
            .ToListAsync(ct).ConfigureAwait(false);
        var found = RecordedFiles(TemplateFolders.Scan(root, ct), warnings: null)
            .Select(f => (f.Path, f.Kind, f.Version, f.Problem))
            .ToHashSet();
        return found.Count != stored.Count
            || stored.Any(s => !found.Contains((s.Path, s.Kind, s.Version, s.Problem)));
    }

    /// <summary>
    /// The module context this sync works through: on the catalog context's connection, since the <c>osdu</c> schema lives
    /// in the catalog's database, and inside the sync's transaction when there is one, so the rows commit or roll back
    /// with the sync.
    /// </summary>
    /// <param name="context">The catalog's context, as the repository sync hands it to the extension.</param>
    /// <param name="ct">Cancels the open.</param>
    private static async Task<OsduDbContext> OpenAsync(CatalogDbContext context, CancellationToken ct)
    {
        var osdu = new OsduDbContext(OsduDbContext.SqlServerOptions(context.Database.GetDbConnection()));
        if (context.Database.CurrentTransaction is { } transaction)
        {
            try
            {
                await osdu.Database.UseTransactionAsync(transaction.GetDbTransaction(), ct).ConfigureAwait(false);
            }
            catch
            {
                await osdu.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        return osdu;
    }

    /// <summary>
    /// Whether the repository's mapping documents on disk differ from the mapping rows <paramref name="context"/> holds for
    /// it, as <see cref="LineageInputsChangedAsync"/> describes.
    /// </summary>
    public async Task<bool> MappingsChangedAsync(OsduDbContext context, Guid repoId, string root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var stored = await context.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == repoId)
            .Select(m => new { m.RelativePath, m.ContentHash, m.Reference })
            .ToListAsync(ct).ConfigureAwait(false);
        var hashByPath = stored.ToDictionary(m => m.RelativePath, m => m.ContentHash, StringComparer.Ordinal);
        var references = stored.Select(m => m.Reference).ToHashSet(StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in EnumerateYaml(root))
        {
            ct.ThrowIfCancellationRequested();
            string yaml;
            try
            {
                yaml = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // What cannot be read now may have been read before: the lineage is recomputed, and the reconciliation reports the file.
                return true;
            }

            if (!LooksLikeMapping(yaml))
            {
                continue;
            }

            var relative = Relative(root, file);
            if (hashByPath.TryGetValue(relative, out var hash) && hash == ContentHash.Of(yaml))
            {
                matched.Add(relative);
                continue;
            }

            // A file the rows do not hold as it is: a change, unless it declares a reference another file already holds.
            string reference;
            try
            {
                // Only the reference it declares is asked, so its templates are left as it names them.
                reference = _documents.ParseMapping(yaml, relative, TemplatePins.DocumentAlone).Reference;
            }
            catch (FlowValidationException)
            {
                return true;
            }

            if (!references.Contains(reference) || hashByPath.ContainsKey(relative))
            {
                return true;
            }
        }

        return matched.Count != hashByPath.Count;
    }

    /// <summary>
    /// Reconciles the repository's mapping documents, cache declarations, interfaces and search terms into
    /// <paramref name="context"/>: the one write the sync extension makes, on whatever connection and transaction the context
    /// was opened with.
    /// </summary>
    public async Task<CatalogSyncExtensionResult> ReconcileAsync(
        OsduDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(warnings);

        // The template folders first: the mappings are settled from what they hold.
        var folders = await SyncTemplateFoldersAsync(context, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false);
        var mappings = await SyncMappingsAsync(context, repoId, root, folders, nowUtc, warnings, ct).ConfigureAwait(false);
        var caches = await SyncCacheDefinitionsAsync(context, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false);
        var (interfaces, sources) = await SyncInterfacesAsync(context, repoId, root, nowUtc, warnings, ct).ConfigureAwait(false);
        // The search terms are read from the delivery flows parsed above and the rows the passes above wrote: the mappings
        // and the cached types. They are derived from the documents counted above, so the sync's counts stay the documents';
        // their warnings are reported.
        await DeliverySearchTermCatalog.ReconcileAsync(context, RepositoryFolders.Of(repoId, root), repoId, sources, _documents, nowUtc, warnings, ct).ConfigureAwait(false);
        return mappings.Add(caches).Add(interfaces);
    }

    private async Task<CatalogSyncExtensionResult> SyncMappingsAsync(
        OsduDbContext context, Guid repoId, string root, SyncedTemplates folders, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        var existing = await context.DeliveryMappings.Where(m => m.RepoId == repoId).AsTracking().ToDictionaryAsync(m => m.Id, ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        int added = 0, updated = 0, unchanged = 0, invalid = 0;

        foreach (var file in EnumerateYaml(root))
        {
            ct.ThrowIfCancellationRequested();
            string yaml;
            try
            {
                yaml = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                warnings.Add($"{Relative(root, file)}: could not be read ({ex.Message}).");
                continue;
            }

            if (!LooksLikeMapping(yaml))
            {
                continue;
            }

            var relative = Relative(root, file);
            MappingDefinition? mapping = null;
            string? message = null;
            try
            {
                // Its template, and the schema each of its searches reads, are settled from the template folders on its path
                // as this sync found them: a version it names must be held there, and a kind it names alone takes the one
                // version the nearest folder holding the kind holds.
                mapping = _documents.ParseMapping(yaml, relative, folders.For(relative), Path.GetFullPath(file));
            }
            catch (FlowValidationException ex)
            {
                message = Bounded(ex.Message, MaxMessageLength);
            }

            // A document whose only fault is a template no folder on its path settles still says who it is, so its row keeps
            // the reference it is known by (and the flows that pin it find it, invalid and saying why) while the template is
            // missing, rather than becoming a row of its path.
            MappingDefinition? alone = null;
            if (mapping is null)
            {
                try
                {
                    alone = _documents.ParseMapping(yaml, relative, TemplatePins.DocumentAlone);
                }
                catch (FlowValidationException)
                {
                    alone = null;
                }
            }

            var reference = mapping?.Reference ?? alone?.Reference ?? relative;
            var id = MappingId(repoId, reference);
            if (!seen.Add(id))
            {
                warnings.Add($"{relative}: mapping '{reference}' is declared more than once in the repository; the first file wins.");
                continue;
            }

            // The template the document fills, and the file it was settled from. A document that fails to load still names
            // the version it pins, and still pins it: deleting that template would leave the fix with nothing to render
            // against. Everything here is derived again for an unchanged document too, since what its folders hold, and
            // what this build makes of a pin, can change while the document does not.
            var pinned = mapping?.Template ?? NamedTemplate(yaml);
            var kind = mapping?.Kind ?? pinned?.Kind ?? NamedKind(yaml) ?? string.Empty;
            var templateVersion = pinned?.Version ?? string.Empty;
            var templatePath = mapping is null
                ? string.Empty
                : folders.ChainFor(relative).Pick(mapping.Template.Kind, mapping.Template.Version).File?.Shown ?? string.Empty;
            var status = mapping is null ? "invalid" : "valid";
            var hash = ContentHash.Of(yaml);
            if (!existing.TryGetValue(id, out var row))
            {
                row = new DeliveryMapping { Id = id, RepoId = repoId, FirstSeenUtc = nowUtc };
                context.DeliveryMappings.Add(row);
                added++;
            }
            else if (row.ContentHash == hash && row.RelativePath == relative && row.Kind == kind && row.TemplateVersion == templateVersion
                && row.TemplatePath == templatePath && row.Status == status && row.Message == message)
            {
                row.LastSeenUtc = nowUtc;
                unchanged++;
                if (mapping is null)
                {
                    invalid++;
                }

                continue;
            }
            else
            {
                updated++;
            }

            row.Reference = reference;
            row.Name = (mapping ?? alone)?.Name ?? Path.GetFileNameWithoutExtension(file);
            row.Version = (mapping ?? alone)?.Version ?? string.Empty;
            row.Kind = kind;
            row.TemplateVersion = templateVersion;
            row.TemplatePath = templatePath;
            row.RelativePath = relative;
            row.ContentHash = hash;
            row.Yaml = yaml;
            row.SummaryJson = mapping is null ? "{}" : JsonSerializer.Serialize(Summarize(mapping), SummaryJson);
            row.Status = status;
            row.Message = message;
            row.LastSeenUtc = nowUtc;
            if (mapping is null)
            {
                invalid++;
                warnings.Add($"{relative}: {message}");
            }
        }

        var removed = 0;
        foreach (var (id, row) in existing)
        {
            if (!seen.Contains(id))
            {
                context.DeliveryMappings.Remove(row);
                removed++;
            }
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return new CatalogSyncExtensionResult(added, updated, unchanged, removed, invalid);
    }

    /// <summary>
    /// The types the repository's cache flows declare (<c>flowType: cache</c>): which records each flow captures into the cache
    /// of its partition, and which paths of them it keeps. A refresh reads every declaration of its partition, so it fetches
    /// every path the partition keeps for a type, and the GUI shows which files fill a partition's cache. A declaration that
    /// disagrees with another flow's declaration of the same type for the partition, another entity type or a name cached
    /// from another path, is left out with a warning, because merged, one name would hold two meanings.
    /// </summary>
    private async Task<CatalogSyncExtensionResult> SyncCacheDefinitionsAsync(
        OsduDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        var existing = await context.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).AsTracking().ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);
        var seen = new HashSet<Guid>();
        var flows = new Dictionary<string, string>(StringComparer.Ordinal);
        var parsed = new List<(CacheDefinition Cache, string Relative)>();
        int added = 0, updated = 0, unchanged = 0, invalid = 0;

        foreach (var file in EnumerateYaml(root))
        {
            ct.ThrowIfCancellationRequested();
            string yaml;
            try
            {
                yaml = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The mapping pass reports an unreadable file; one warning per file is enough.
                continue;
            }

            if (!DeclaresFlowType(yaml, CacheDefinition.FlowTypeName))
            {
                continue;
            }

            var relative = Relative(root, file);
            CacheDefinition cache;
            try
            {
                cache = _documents.ParseCache(yaml, relative);
            }
            catch (FlowValidationException ex)
            {
                invalid++;
                warnings.Add($"{relative}: {ex.Message}");
                continue;
            }

            if (!flows.TryAdd(cache.Name, relative))
            {
                warnings.Add($"{relative}: cache flow '{cache.Name}' is already declared by {flows[cache.Name]}; the first file wins.");
                continue;
            }

            parsed.Add((cache, relative));
        }

        // What the other repositories declare for the same partitions: this repository's declarations have to agree with them.
        // Each document is bound to every partition it builds a cache for (osdu/docs/reference/flow/cache.md, Which
        // partitions a flow fills): the ones it names, every registered one for a flow that leaves them to the
        // registry, or the one its headers name, resolved once. Everything below is keyed by that partition: the scopes
        // read from other repositories, the conflict check, and the rows written. Binding in one place is what keeps
        // those three agreeing with each other and with the capture and the render. A partition registered later is
        // bound at the next sync.
        var registry = parsed.Any(p => p.Cache.FollowsRegistry)
            ? await DeliveryPartitionRegistry.ReadAsync(context, ct).ConfigureAwait(false)
            : RegisteredPartitions.None;
        var bindings = new List<(CacheDefinition Cache, string Relative, string Scope)>();
        foreach (var (cache, relative) in parsed)
        {
            if (cache.Partitioned)
            {
                var served = cache.Served(registry);
                if (served.Count == 0)
                {
                    warnings.Add($"{relative}: cache flow '{cache.Name}' builds a cache for every partition registered with the catalog, and none is registered yet, so it builds none. Name one in the control plane's configuration (Osdu:Partitions:<name>), or register one with 'sqlflow partition add <name>'.");
                }

                bindings.AddRange(served.Select(partition => cache.ForPartition(partition)).Select(bound => (bound, relative, bound.Partition!)));
            }
            else
            {
                bindings.Add((cache, relative, await ResolvedScopeAsync(cache, ct).ConfigureAwait(false)));
            }
        }

        var scopes = bindings.Select(b => b.Scope).Distinct(StringComparer.Ordinal).ToList();
        var names = flows.Keys.ToList();
        var elsewhere = await context.DeliveryCacheDefinitions.AsNoTracking()
            .Where(c => c.RepoId != repoId && scopes.Contains(c.Scope) && !names.Contains(c.FlowName))
            .Select(c => new { c.Scope, c.FlowName, c.Name, c.EntityType, c.Kind, c.Query, c.FieldsJson, c.Endpoint, c.Origin })
            .ToListAsync(ct).ConfigureAwait(false);
        var declared = scopes.ToDictionary(
            scope => scope,
            scope => elsewhere
                .Where(c => c.Scope == scope)
                .Select(c => new Snapshots.CacheTypeDeclaration(
                    c.FlowName, c.Name, c.EntityType, c.Kind, c.Query ?? "*", OsduCacheStore.ParseFields(c.FieldsJson, c.FlowName, c.Name), Snapshots.CacheChangeMode.Auto,
                    Snapshots.CacheOrigins.Parse(c.Origin)))
                .ToList(),
            StringComparer.Ordinal);

        foreach (var (cache, relative, scope) in bindings)
        {
            // A cache is keyed by the partition its flow reaches, so the declaration is recorded under that partition, which
            // is what a capture and a render both look under: one the flow names, or the one its headers resolve to. A control
            // plane that cannot resolve the reference records it as written: the declaration is then found by a node that
            // resolves it the same way, and one that does not fails naming the partition it could not find rather than
            // writing under two keys. A flow bound to a partition holds only the types built for it.
            foreach (var declaredType in cache.Types)
            {
                // A dictionary type is recorded as its document says: its key, its fields and its file, found the way a
                // refresh finds it. One that cannot be found or read is left out now, not discovered by the next refresh.
                var type = declaredType;
                if (type.Origin == Snapshots.CacheOrigin.Dictionary)
                {
                    try
                    {
                        type = ResolveDictionary(root, relative, type);
                    }
                    catch (FlowValidationException ex)
                    {
                        invalid++;
                        warnings.Add($"{relative}: {type.Name} is left out of the cache of partition '{scope}', because its dictionary could not be read: {ex.Message}");
                        continue;
                    }
                }

                var problems = new Snapshots.CacheDeclaration(scope, declared[scope]).Conflicts(cache.Name, type);
                if (problems.Count > 0)
                {
                    invalid++;
                    warnings.Add(
                        $"{relative}: {type.Name} is left out of the cache of partition '{scope}', because {string.Join("; ", problems)}. Make the declarations agree, or give the type another name.");
                    continue;
                }

                declared[scope].Add(new Snapshots.CacheTypeDeclaration(
                    cache.Name, type.Name, type.EntityType, type.Kind, type.Query, type.Fields, type.OnChange, type.Origin, RetentionDays: cache.RetentionDays));
                // A flow that names no partitions keeps the identity its declarations always had; one that names them keeps
                // one per partition.
                var id = FlowIdentity.FromName(
                    $"delivery-cache/{repoId:N}/{cache.Name}/{type.Name}" + (cache.Partitioned ? $"@{scope.ToLowerInvariant()}" : string.Empty));
                seen.Add(id);
                var declaration = Declaration(cache, type, relative);
                if (!existing.TryGetValue(id, out var row))
                {
                    row = new DeliveryCacheDefinition { Id = id, RepoId = repoId, FirstSeenUtc = nowUtc };
                    context.DeliveryCacheDefinitions.Add(row);
                    added++;
                }
                else if (declaration.Matches(row) && row.FlowName == cache.Name && row.Scope == scope)
                {
                    row.LastSeenUtc = nowUtc;
                    unchanged++;
                    continue;
                }
                else
                {
                    updated++;
                }

                row.FlowName = cache.Name;
                row.Scope = scope;
                declaration.WriteTo(row);
                row.LastSeenUtc = nowUtc;
            }
        }

        var removed = 0;
        var released = new List<(string Scope, string Flow, string Type)>();
        foreach (var (id, row) in existing)
        {
            if (!seen.Contains(id))
            {
                released.Add((row.Scope, row.FlowName, row.Name));
                context.DeliveryCacheDefinitions.Remove(row);
                removed++;
            }
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);

        // A flow that no longer declares a type for a partition holds none of its records there: its membership goes, so the
        // next capture of the type by another flow lets go of what only this flow kept.
        foreach (var (scope, flow, type) in released)
        {
            if (await context.DeliveryCacheDefinitions.AnyAsync(c => c.Scope == scope && c.FlowName == flow && c.Name == type, ct).ConfigureAwait(false))
            {
                continue;
            }

            await context.DeliveryCacheMembers
                .Where(m => m.Scope == scope && m.FlowName == flow && m.TypeName == type)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        // A partition has one cache holding what every one of its cache flows captures, so the flows searching it should search
        // one platform. A table or a dictionary is not searched, and says nothing about the platform.
        foreach (var scope in scopes)
        {
            var endpoints = bindings
                .Where(b => b.Scope == scope && b.Cache.Types.Any(t => t.Origin == Snapshots.CacheOrigin.Osdu) && b.Cache.Source.Endpoint is not null)
                .Select(b => Clip(b.Cache.Source.Endpoint!, 1000))
                .Concat(elsewhere.Where(c => c.Scope == scope && c.Endpoint is not null).Select(c => c.Endpoint!))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (endpoints.Count > 1)
            {
                warnings.Add(
                    $"The cache flows of partition '{scope}' search different endpoints ({string.Join(", ", endpoints)}); the partition's one cache holds what all of them capture, so they should name the same OSDU platform.");
            }
        }

        // A cache flow is named globally, like every flow: the same flow declared by another repository captures into the same
        // partition's cache under the same name, which is never what either repository means.
        if (flows.Count > 0)
        {
            var shared = await context.DeliveryCacheDefinitions.AsNoTracking()
                .Where(c => c.RepoId != repoId && names.Contains(c.FlowName))
                .Select(c => c.FlowName)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var name in shared.Order(StringComparer.Ordinal))
            {
                warnings.Add($"{flows[name]}: cache flow '{name}' is also declared by another repository, and both would capture into the cache under the same name. Rename one of them.");
            }
        }

        return new CatalogSyncExtensionResult(added, updated, unchanged, removed, invalid);
    }

    /// <summary>
    /// The interfaces of the repository's delivery flows (osdu/docs/reference/flow/interfaces.md, The interface read
    /// model), each with the ledger identity it keeps and the kind its mapping fills, as the mapping rows this sync just
    /// wrote name it; with the flows as parsed, which the search terms are read from. A flow document that does not parse
    /// describes no interface; the platform's own sync reports it.
    /// </summary>
    private async Task<(CatalogSyncExtensionResult Counts, IReadOnlyList<RepositorySource> Sources)> SyncInterfacesAsync(
        OsduDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        var sources = new List<RepositorySource>();
        var unread = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = 0;
        foreach (var file in EnumerateYaml(root))
        {
            ct.ThrowIfCancellationRequested();
            string yaml;
            try
            {
                yaml = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The mapping pass reports an unreadable file; one warning per file is enough. Its pipeline only looks gone.
                unread.Add(Relative(root, file));
                continue;
            }

            if (!DeclaresFlowType(yaml, FlowDefinition.FlowTypeName))
            {
                continue;
            }

            var relative = Relative(root, file);
            try
            {
                sources.Add(new RepositorySource(relative, _documents.ParseSource(yaml, relative)));
            }
            catch (FlowValidationException)
            {
                unread.Add(relative);
                invalid++;
            }
        }

        var kinds = await MappingKindsAsync(context, repoId, ct).ConfigureAwait(false);
        var mappings = await MappingHashesAsync(context, repoId, ct).ConfigureAwait(false);
        var counts = await DeliveryInterfaceCatalog.ReconcileAsync(
            context, repoId, sources, kinds, mappings, nowUtc, warnings, new LedgerMoveGuard(Asks: true, unread), ct).ConfigureAwait(false);
        return (new CatalogSyncExtensionResult(counts.Added, counts.Updated, counts.Unchanged, counts.Removed, invalid), sources);
    }

    /// <summary>The content hash of every mapping document the repository's rows hold, valid or not, by reference.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> MappingHashesAsync(OsduDbContext context, Guid repoId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rows = await context.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == repoId)
            .Select(m => new { m.Reference, m.ContentHash })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(m => m.Reference, m => m.ContentHash, StringComparer.Ordinal);
    }

    /// <summary>The OSDU kind of every valid mapping the repository's rows hold, by reference.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> MappingKindsAsync(OsduDbContext context, Guid repoId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rows = await context.DeliveryMappings.AsNoTracking()
            .Where(m => m.RepoId == repoId && m.Status == "valid")
            .Select(m => new { m.Reference, m.Kind })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(m => m.Reference, m => m.Kind, StringComparer.Ordinal);
    }

    private static object Summarize(MappingDefinition mapping) => new
    {
        mapping.Name,
        mapping.Version,
        mapping.Kind,
        TemplateVersion = mapping.Template.Version,
        mapping.TemplateVersionNamed,
        mapping.EntityType,
        mapping.Description,
        System = mapping.Dataset.System,
        Key = mapping.Dataset.Key,
        IdFrom = mapping.Dataset.IdFrom == MappingIdSource.Key ? MappingMapper.IdFromKey : MappingMapper.IdFromDeliveryKey,
        mapping.Dataset.Label,
        ChildDatasets = mapping.ChildDatasets,
        Parameters = mapping.Parameters.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
        Entries = mapping.Entries.Count,
        Assertions = mapping.Assertions().Count(),
        LegalTags = mapping.Envelope.LegalTags,
        Countries = mapping.Envelope.OtherRelevantDataCountries,
        Owners = mapping.Envelope.Owners,
        Viewers = mapping.Envelope.Viewers,
    };

    /// <summary>
    /// The template an invalid mapping document names. A kind or version longer than the catalog holds for a template
    /// can name no template a folder holds, so it counts as naming none rather than failing the row.
    /// </summary>
    private TemplateReference? NamedTemplate(string yaml)
        => _documents.TemplateNamedBy(yaml) is { } named && named.Kind.Length <= MaxTemplateKindLength && named.Version.Length <= MaxTemplateVersionLength
            ? named
            : null;

    /// <summary>The kind an invalid mapping document names for its template, with or without a version; null when it names none the catalog can hold.</summary>
    private string? NamedKind(string yaml)
        => _documents.TemplateKindNamedBy(yaml) is { } kind && kind.Length <= MaxTemplateKindLength ? kind : null;

    /// <summary>The longest message a mapping row keeps.</summary>
    private const int MaxMessageLength = 4000;

    private static string Bounded(string value, int max) => value.Length <= max ? value : value[..(max - 3)] + "...";

    /// <summary>
    /// The repository's template folders, as this sync finds them on disk (<see cref="TemplateFolders.Scan"/>): each file a
    /// row of <c>[osdu].[RepositoryTemplate]</c>, which says the template it holds or why it holds none, and holds none of
    /// it: whoever reads the template reads the file. A file that holds no template, a folder named almost like a template folder, the same
    /// template twice in one folder and two versions of a kind in one folder are reported as warnings; the sync goes on.
    /// Returns the folders, which settle the repository's mappings.
    /// </summary>
    private static async Task<SyncedTemplates> SyncTemplateFoldersAsync(
        OsduDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct)
    {
        var scan = TemplateFolders.Scan(root, ct);
        var files = RecordedFiles(scan, warnings);
        foreach (var warning in scan.Warnings)
        {
            warnings.Add(warning);
        }

        foreach (var file in files.Where(f => f.Problem is not null))
        {
            warnings.Add($"{file.Path}: {file.Problem}");
        }

        foreach (var folder in scan.Folders)
        {
            // A template's link to its parent (concepts/templates.md, Linked templates) is read where a mapping reads $parent,
            // and preflight refuses one that cannot be read there; the sync says so of the file first.
            foreach (var file in folder.Files.Where(f => f.IsTemplate))
            {
                if (file.ParentProblem is { } link)
                {
                    warnings.Add($"{file.Shown}: {link}, so the template is linked to no parent and a mapping of it cannot read $parent.");
                }
                else if (file.Parent is { } parent
                    && string.Equals(TargetId.EntityTypeFromKind(parent), TargetId.EntityTypeFromKind(file.Kind!), StringComparison.Ordinal))
                {
                    warnings.Add(
                        $"{file.Shown}: the template {file.Kind} is linked to {parent}, of its own entity type, so a record and its parent would have the same OSDU id; a linked template takes an entity type of its own.");
                }
            }

            foreach (var same in folder.Files.Where(f => f.IsTemplate).GroupBy(f => f.Reference).Where(g => g.Count() > 1))
            {
                warnings.Add($"{TemplateChain.Listed(same.Select(f => f.Shown))} hold the same template, {same.Key}; one file of it in a folder is enough.");
            }

            foreach (var kind in folder.Files.Where(f => f.IsTemplate).GroupBy(f => f.Kind!, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Select(f => f.Version!).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            {
                warnings.Add(
                    $"{folder.Shown} holds {kind.Select(f => f.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count()} versions of {kind.Key} "
                    + $"({TemplateChain.Listed(kind.Select(f => $"{f.Version} in {f.Shown}"))}), so a document that names the kind alone and reads this folder has to name its version.");
            }
        }

        var existing = await context.DeliveryRepositoryTemplates.Where(t => t.RepoId == repoId).AsTracking()
            .ToDictionaryAsync(t => t.Path, StringComparer.Ordinal, ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            seen.Add(file.Path);
            if (!existing.TryGetValue(file.Path, out var row))
            {
                row = new DeliveryRepositoryTemplate
                {
                    Id = FlowIdentity.FromName($"delivery-template-file/{repoId:N}/{file.Path}"),
                    RepoId = repoId,
                    Path = file.Path,
                    FirstSeenUtc = nowUtc,
                };
                context.DeliveryRepositoryTemplates.Add(row);
            }

            // The row says which template the file holds, as the scan read it; whoever reads the template reads the file.
            row.Folder = file.Folder;
            row.Kind = file.Problem is null ? file.Kind : string.Empty;
            row.Version = file.Problem is null ? file.Version : string.Empty;
            row.Problem = file.Problem;
            row.Parent = file.Problem is null ? file.Parent : null;
            row.LastSeenUtc = nowUtc;
        }

        foreach (var (path, row) in existing)
        {
            if (!seen.Contains(path))
            {
                context.DeliveryRepositoryTemplates.Remove(row);
            }
        }

        await context.SaveChangesAsync(ct).ConfigureAwait(false);
        return SyncedTemplates.Of(scan);
    }

    /// <summary>A file of a template folder as the sync records it, with the kind its template is linked to, if any.</summary>
    private sealed record RecordedFile(string Path, string Folder, string Kind, string Version, string? Problem, string? Parent);

    /// <summary>
    /// The files of <paramref name="scan"/> as rows record them, in path order: a path longer than a row holds is left out,
    /// and said so to <paramref name="warnings"/> when given.
    /// </summary>
    private static List<RecordedFile> RecordedFiles(TemplateFolderScan scan, ICollection<string>? warnings)
    {
        var files = new List<RecordedFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in scan.Folders)
        {
            foreach (var file in folder.Files)
            {
                if (file.Shown.Length > DeliveryRepositoryTemplate.MaxPathLength || folder.Shown.Length > DeliveryRepositoryTemplate.MaxPathLength)
                {
                    warnings?.Add($"{file.Shown}: the path is longer than {DeliveryRepositoryTemplate.MaxPathLength} characters, so the file is not recorded as a template.");
                    continue;
                }

                if (!seen.Add(file.Shown))
                {
                    continue;
                }

                files.Add(new RecordedFile(
                    file.Shown, folder.Shown, file.Kind ?? string.Empty, file.Version ?? string.Empty,
                    file.Problem is null ? null : Bounded(file.Problem, DeliveryRepositoryTemplate.MaxProblemLength), file.Parent));
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return files;
    }

    /// <summary>A cheap textual pre-check, so only candidate documents are parsed: every mapping declares its type.</summary>
    /// <summary>
    /// A cheap textual pre-check for a mapping: a top-level <c>documentType: mapping</c>. The value is matched exactly, so a
    /// dictionary or any other document that merely mentions the word is not taken for a mapping and stored as a broken one.
    /// </summary>
    private static bool LooksLikeMapping(string yaml) => DeclaresTopLevel(yaml, "documentType", MappingDefinition.DocumentTypeName);

    /// <summary>A cheap textual pre-check: a top-level <c>flowType:</c> line whose value is <paramref name="flowType"/>, quoted or not.</summary>
    private static bool DeclaresFlowType(string yaml, string flowType) => DeclaresTopLevel(yaml, "flowType", flowType);

    /// <summary>
    /// Whether the first top-level <paramref name="key"/> line holds <paramref name="value"/>, quoted or not, ignoring a
    /// trailing comment and a byte order mark before the first line.
    /// </summary>
    private static bool DeclaresTopLevel(string yaml, string key, string value)
    {
        var prefix = key + ":";
        foreach (var raw in yaml.AsSpan().EnumerateLines())
        {
            var line = raw.TrimStart('﻿');
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var declared = line[prefix.Length..].Trim().Trim('"').Trim('\'');
            var comment = declared.IndexOf('#');
            if (comment >= 0)
            {
                declared = declared[..comment].TrimEnd().Trim('"').Trim('\'');
            }

            return declared.Equals(value, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static IEnumerable<string> EnumerateYaml(string root)
        => Walk(root).Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Walk(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries.OrderBy(e => e, StringComparer.Ordinal))
            {
                if (Directory.Exists(entry))
                {
                    if (!SkippedDirectories.Contains(Path.GetFileName(entry)))
                    {
                        pending.Push(entry);
                    }
                }
                else
                {
                    yield return entry;
                }
            }
        }
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    private static string? ClipOrNull(string? text, int length) => text is null ? null : Clip(text, length);

    /// <summary>
    /// A dictionary type as its document makes it: the dictionary found for the cache flow at <paramref name="relative"/>
    /// inside the repository at <paramref name="root"/>, and the type given the document's key, fields and file.
    /// </summary>
    private Snapshots.ReferenceTypeSpec ResolveDictionary(string root, string relative, Snapshots.ReferenceTypeSpec type)
    {
        var fullRoot = Path.GetFullPath(root);
        var folder = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(fullRoot, relative))) ?? fullRoot;
        var loaded = new DictionaryCatalog(_documents).Load(
            type.Dictionary!, folder, full => Relative(fullRoot, full), within: path => IsWithin(fullRoot, path));
        var resolved = type with { Key = loaded.Dictionary.Key, Fields = loaded.Dictionary.FieldSpecs(), DictionaryPath = loaded.ShownPath };
        resolved.Validate();
        return resolved;
    }

    private static bool IsWithin(string root, string path)
    {
        var full = Path.GetFullPath(path);
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What the catalog records of one declared type, computed once so the check for an unchanged row and the write agree:
    /// the origin, and only the settings of that origin, so a type that changes origin leaves nothing of the old one behind.
    /// </summary>
    private static CacheDefinitionRow Declaration(CacheDefinition cache, Snapshots.ReferenceTypeSpec type, string relative) => new(
        Snapshots.CacheOrigins.Text(type.Origin),
        type.Origin == Snapshots.CacheOrigin.Osdu ? ClipOrNull(cache.Source.Endpoint, 1000) : null,
        type.Origin == Snapshots.CacheOrigin.Table ? ClipOrNull(cache.Source.Connection, 1000) : null,
        type.Origin switch
        {
            Snapshots.CacheOrigin.Table => type.Table,
            Snapshots.CacheOrigin.Dimension => $"{type.DimensionFlow}/{type.Dimension}",
            _ => null,
        },
        type.IsLookup ? type.Key : null,
        type.Origin == Snapshots.CacheOrigin.Dictionary ? ClipOrNull(type.DictionaryPath, 1000) : null,
        relative,
        type.Name,
        type.EntityType,
        type.Origin == Snapshots.CacheOrigin.Osdu ? type.Kind : null,
        type.Origin == Snapshots.CacheOrigin.Osdu ? type.Query : null,
        JsonSerializer.Serialize(type.Fields.Select(f => new { f.Path, As = f.Name }).ToList(), SummaryJson),
        type.OnChange == Snapshots.CacheChangeMode.Auto ? "auto" : "approve",
        cache.Partitioned,
        cache.RetentionDays);

    private sealed record CacheDefinitionRow(
        string Origin, string? Endpoint, string? Connection, string? SourceObject, string? KeyField, string? DictionaryPath, string RelativePath,
        string Name, string EntityType, string? Kind, string? Query, string FieldsJson, string OnChange, bool DeclaresPartitions, int RetentionDays)
    {
        public bool Matches(DeliveryCacheDefinition row)
            => row.Origin == Origin && row.Endpoint == Endpoint && row.Connection == Connection && row.SourceObject == SourceObject
               && row.KeyField == KeyField && row.DictionaryPath == DictionaryPath && row.RelativePath == RelativePath && row.Name == Name
               && row.EntityType == EntityType && row.Kind == Kind && row.Query == Query && row.FieldsJson == FieldsJson && row.OnChange == OnChange
               && row.DeclaresPartitions == DeclaresPartitions && row.RetentionDays == RetentionDays;

        public void WriteTo(DeliveryCacheDefinition row)
        {
            row.Origin = Origin;
            row.Endpoint = Endpoint;
            row.Connection = Connection;
            row.SourceObject = SourceObject;
            row.KeyField = KeyField;
            row.DictionaryPath = DictionaryPath;
            row.RelativePath = RelativePath;
            row.Name = Name;
            row.EntityType = EntityType;
            row.Kind = Kind;
            row.Query = Query;
            row.FieldsJson = FieldsJson;
            row.OnChange = OnChange;
            row.DeclaresPartitions = DeclaresPartitions;
            row.RetentionDays = RetentionDays;
        }
    }
}
