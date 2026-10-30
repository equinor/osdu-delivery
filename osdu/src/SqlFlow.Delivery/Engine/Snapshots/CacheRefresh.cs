using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// Refreshes what one cache flow contributes to its partition's cache (osdu/docs/reference/concepts/partition-cache.md,
/// What a refresh does). The flow says which reference and master-data types it captures and with which queries; a
/// refresh sweeps every declared type in full, fetching every path any synced flow keeps for the type in the partition,
/// and merges the result into the partition's cache, which writes a new version when the cached content moved. It then
/// compares the new version with the one it replaced and tags each changed value that delivered records were built
/// from, so what a refresh does to the delivered estate is decided change by change, under the partition's approval
/// setting for the type.
/// </summary>
public sealed class CacheRefresher
{
    /// <summary>The offset search a plan counts with (openapi search v2, POST /query with trackTotalCount).</summary>
    private const string QueryPath = "/api/search/v2/query";

    /// <summary>The storage service's batch read a refresh asks about records the search index did not answer (openapi storage v2, POST /query/records).</summary>
    private const string StoragePath = Protocols.OsduRecordProtocol.DefaultVerifyBatchPath;

    private readonly EngineContext _context;
    private readonly ILogger _logger;

    public CacheRefresher(EngineContext context, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// <paramref name="flow"/> narrowed to the types a run names (osdu/docs/reference/flow/cache.md, Operations and the run
    /// payload): the flow itself when it names none. A name the flow does not declare for its partition is refused, naming
    /// the ones it does.
    /// </summary>
    /// <exception cref="SqlFlowException">A name is not a type of the flow.</exception>
    public static CacheDefinition Narrow(CacheDefinition flow, IReadOnlyList<string> types)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(types);
        if (types.Count == 0)
        {
            return flow;
        }

        var unknown = types.Where(name => !flow.Types.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
        {
            var where = flow.Partitioned && flow.Partition is { } partition ? $" for partition '{partition}'" : string.Empty;
            throw new SqlFlowException(
                $"payload types names {string.Join(", ", unknown.Select(n => $"'{n}'"))}, which cache flow '{flow.Name}' does not declare{where}; it declares {string.Join(", ", flow.Types.Select(t => t.Name))}.");
        }

        return flow with { Types = [.. flow.Types.Where(t => types.Contains(t.Name, StringComparer.OrdinalIgnoreCase))] };
    }

    /// <summary>
    /// The records of each captured OSDU type that the cache holds, the capture did not find, and a delivery holds, which
    /// OSDU's storage service no longer holds either (osdu/docs/reference/concepts/partition-cache.md, Pipelines
    /// bring the cache up to date). A record a delivery landed stays in the cache while the search index has not caught up with it, so the
    /// refresh asks storage, which a write reaches at once, about those alone: a record storage still holds stays, one it
    /// does not (deleted, or not readable with the cache flow's credentials) loses the deliveries' hold.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> GoneAsync(
        ICacheStore store, string scope, StorageRecords storage, IReadOnlyList<ReferenceType> captured, ILogger logger, CancellationToken ct)
    {
        var gone = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        if (await store.CurrentVersionAsync(scope, ct).ConfigureAwait(false) is not { } version
            || await store.LoadAsync(scope, version, ct).ConfigureAwait(false) is not { } current)
        {
            return gone;
        }

        foreach (var type in captured.Where(t => !t.IsLookup))
        {
            if (current.Type(type.Name) is not { } held)
            {
                continue;
            }

            var found = type.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            var notFound = held.Items.Select(i => i.Id).Where(id => !found.Contains(id)).ToList();
            if (notFound.Count == 0)
            {
                continue;
            }

            var delivered = await store.HeldByDeliveriesAsync(scope, type.Name, notFound, ct).ConfigureAwait(false);
            var missing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var batch in delivered.Chunk(StorageRecords.Batch))
            {
                var read = await storage.ReadAsync(batch, null, ct).ConfigureAwait(false);
                missing.UnionWith(read.Missing);
            }

            if (delivered.Count > 0)
            {
                logger.LogInformation(
                    "{Type}: {Held} record(s) deliveries landed are not in the search index yet or any more; storage holds {Kept} of them, which stay, and not {Gone}, which leave the cache.",
                    type.Name, delivered.Count, delivered.Count - missing.Count, missing.Count);
            }

            if (missing.Count > 0)
            {
                gone[type.Name] = missing;
            }
        }

        return gone;
    }

    /// <summary>Captures the cache flow's types, merges them into the partition's cache, and tags what the changes reach.</summary>
    public async Task<CacheRefreshOutcome> RefreshAsync(
        CacheDefinition flow, IReadOnlyDictionary<string, string> values, Guid runId, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var store = _context.Cache ?? throw new DeliveryException(
            $"Cache flow '{flow.Name}' writes into the cache of its partition, which lives in the module's database, and this host was started without it. Run it through the control plane or a node, or start the CLI with it. {OsduModuleDatabase.StartWithCatalogConnection}");
        // The capture is keyed by the partition the flow actually reaches, the way a render's read of that cache is, so a
        // document naming its partition ${env:...} captures into the partition's cache rather than into one named after
        // the text. Both sides resolve, so both sides agree.
        var scope = CacheScope.Normalize(
            await _context.Secrets.ResolveAsync(flow.Scope, ct).ConfigureAwait(false),
            $"{flow.SourcePath ?? flow.Name}: source");
        var declaration = await store.DeclarationAsync(scope, ct).ConfigureAwait(false);
        declaration.ThrowOnConflicts(flow.Name, flow.Types);
        var spec = CaptureSpec(flow, values, declaration);

        // Each origin is captured its own way, and everything captured is merged into one version: a flow's refresh writes one
        // version of its partition's cache, whatever its types come from.
        var builder = new SnapshotBuilder(store, scope, flow.Name, _context.Time, _context.Loggers.CreateLogger<SnapshotBuilder>(), flow.RetentionDays);
        var captured = new List<ReferenceType>(spec.Types.Count);
        var origins = new List<string>();
        IReadOnlyList<SystemPropertyReading> readings = [];
        IReadOnlyDictionary<string, IReadOnlySet<string>> gone = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        var searched = spec.Types.Where(t => t.Origin == CacheOrigin.Osdu).ToList();
        if (searched.Count > 0)
        {
            using var osdu = await ConnectAsync(flow, ct).ConfigureAwait(false);
            foreach (var type in searched)
            {
                captured.Add(await builder.CaptureTypeAsync(osdu, type, ct).ConfigureAwait(false));
            }

            gone = await GoneAsync(store, scope, new StorageRecords(osdu.Client, StoragePath), captured, _logger, ct).ConfigureAwait(false);

            // The partition's system properties are read wherever the platform is reached, which is a capture of OSDU types; a
            // flow of lookup tables alone reads none and keeps what the cache knew.
            readings = await SystemPropertyCapture.ReadAsync(osdu, scope, _logger, ct).ConfigureAwait(false);
            origins.Add(flow.Source.Endpoint!);
        }

        foreach (var type in spec.Types.Where(t => t.Origin == CacheOrigin.Table))
        {
            captured.Add(await TableCapture.CaptureAsync(flow, type, _context.Secrets, _logger, ct).ConfigureAwait(false));
            origins.Add($"table {type.Table}");
        }

        foreach (var type in spec.Types.Where(t => t.Origin == CacheOrigin.Dictionary))
        {
            ct.ThrowIfCancellationRequested();
            var loaded = LoadDictionary(flow, type);
            captured.Add(loaded.Dictionary.ToLookup(type.Name));
            origins.Add($"dictionary {loaded.ShownPath}");
            _logger.LogInformation(
                "Captured {Count} {Type} row(s) from dictionary {Dictionary}, keyed by {Key}.", loaded.Dictionary.Entries.Count, type.Name, loaded.ShownPath, loaded.Dictionary.Key);
        }

        foreach (var type in spec.Types.Where(t => t.Origin == CacheOrigin.Dimension))
        {
            captured.Add(await DimensionCapture.CaptureAsync(DimensionLedger(flow, type), scope, type, _logger, ct).ConfigureAwait(false));
            origins.Add(type.Describe());
        }

        var write = await builder.WriteAsync(captured, new CacheCapture(runId, actor, string.Join("; ", origins)) { Gone = gone }, readings, ct).ConfigureAwait(false);
        var snapshot = write.Snapshot;
        var previousVersion = write.Previous?.Version;

        var types = new List<CachedTypeOutcome>(spec.Types.Count);
        foreach (var typeSpec in spec.Types)
        {
            if (snapshot.Type(typeSpec.Name) is not { } type)
            {
                continue;
            }

            // The version moved because something in the partition's cache did; a type whose own hash did not holds exactly
            // what the version before held, so nothing built from it can have moved and nothing of it is analysed or tagged.
            var change = write.Changes.Of(type.Name) ?? CacheTypeChange.Unchanged;
            var mode = declaration.ModeOf(typeSpec.Name, typeSpec.OnChange);
            var impact = !write.Written || _context.Ledger is null || change == CacheTypeChange.Unchanged
                ? new CacheImpactResult(type.Name, 0, 0, 0, 0)
                : await new CacheImpactAnalyzer(_context.Ledger, _context.Time, _logger)
                    .AnalyzeAsync(scope, write.Previous?.Type(type.Name), type, mode, previousVersion, snapshot.Version, ct)
                    .ConfigureAwait(false);
            // A lookup table's fields are what its origin holds; an OSDU type's are the paths the flow declares.
            var fields = typeSpec.IsLookup ? type.FieldNames.ToList() : typeSpec.Fields.Select(f => f.Name).ToList();
            types.Add(new CachedTypeOutcome(
                type.Name, type.EntityType, CacheOrigins.Text(typeSpec.Origin), typeSpec.Describe(), typeSpec.Kind, type.Items.Count,
                fields, ModeText(mode), impact.ChangedItems, impact.Changes, impact.AffectedRecords, CacheTypeChanges.Text(change), type.ContentHash()));
        }

        var outcome = new CacheRefreshOutcome(
            DeliveryOperations.Refresh, scope, flow.Name, snapshot.Version, previousVersion, write.Written, snapshot.CapturedUtc.UtcDateTime, types,
            snapshot.SystemProperties, write.Retention);
        var moved = write.Changes.Moved;
        _logger.LogInformation(
            "Cache of partition {Scope} refreshed by {Flow}: {Outcome}, {Types} type(s), {Items} record(s); {Moved}. {Changed} cached record(s) moved, reaching {Records} delivered record(s) through {Changes} change(s).",
            scope, flow.Name, write.Written ? $"version {snapshot.Version} written and made current" : $"unchanged at version {snapshot.Version}",
            types.Count, outcome.Items,
            !write.Written ? "no type changed" : moved.Count == 0 && write.Changes.Removed.Count == 0
                ? "no type changed, only the partition's system properties"
                : Describe(write.Changes),
            types.Sum(t => t.ChangedItems), outcome.AffectedRecords, types.Sum(t => t.Changes));
        return outcome;
    }

    /// <summary>Which types a written version added, changed and removed, for the refresh's log line.</summary>
    private static string Describe(CacheTypeChanges changes)
    {
        var parts = new List<string>();
        foreach (var kind in new[] { CacheTypeChange.Added, CacheTypeChange.Changed })
        {
            var names = changes.Types.Where(t => t.Value == kind).Select(t => t.Key).Order(StringComparer.Ordinal).ToList();
            if (names.Count > 0)
            {
                parts.Add($"{CacheTypeChanges.Text(kind)} {string.Join(", ", names)}");
            }
        }

        if (changes.Removed.Count > 0)
        {
            parts.Add($"removed {string.Join(", ", changes.Removed)}");
        }

        var unchanged = changes.Types.Count(t => t.Value == CacheTypeChange.Unchanged);
        if (unchanged > 0)
        {
            parts.Add($"{unchanged} type(s) unchanged");
        }

        return string.Join("; ", parts);
    }

    /// <summary>
    /// What a refresh would capture: how many records of each declared type the search matches, and the partition's system
    /// properties as the platform reports them now, merged onto what the current version holds. Writes nothing.
    /// </summary>
    public async Task<CachePlanOutcome> PlanAsync(CacheDefinition flow, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);

        // The partition the refresh would capture into, resolved as a refresh resolves it: the text a document spells it
        // with names no cache.
        var scope = CacheScope.Normalize(
            await _context.Secrets.ResolveAsync(flow.Scope, ct).ConfigureAwait(false),
            $"{flow.SourcePath ?? flow.Name}: source");
        var declaration = _context.Cache is { } store ? await store.DeclarationAsync(scope, ct).ConfigureAwait(false) : CacheDeclaration.None(scope);
        var spec = CaptureSpec(flow, values, declaration);
        var current = _context.Cache is { } cache ? await cache.VersionAsync(scope, version: null, ct).ConfigureAwait(false) : null;

        var types = new List<CachePlanType>(spec.Types.Count);
        foreach (var type in spec.Types.Where(t => t.Origin == CacheOrigin.Table))
        {
            var total = await TableCapture.CountAsync(flow, type, _context.Secrets, ct).ConfigureAwait(false);
            _logger.LogInformation("plan {Type}: table {Table} holds {Total} row(s)", type.Name, type.Table, total);
            types.Add(new CachePlanType(
                type.Name, CacheOrigins.Text(type.Origin), type.Describe(), null, "*", type.Fields.Select(f => f.Name).Prepend(type.Key!).ToList(), total));
        }

        foreach (var type in spec.Types.Where(t => t.Origin == CacheOrigin.Dictionary))
        {
            var loaded = LoadDictionary(flow, type);
            _logger.LogInformation("plan {Type}: dictionary {Dictionary} holds {Total} entry(ies)", type.Name, loaded.ShownPath, loaded.Dictionary.Entries.Count);
            types.Add(new CachePlanType(
                type.Name, CacheOrigins.Text(type.Origin), $"dictionary {loaded.ShownPath}", null, "*", [loaded.Dictionary.Key, .. loaded.Dictionary.Fields],
                loaded.Dictionary.Entries.Count));
        }

        foreach (var type in spec.Types.Where(t => t.Origin == CacheOrigin.Dimension))
        {
            var dimension = await DimensionCapture.FindAsync(DimensionLedger(flow, type), scope, type, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "plan {Type}: dimension {Dimension} of {Flow} holds {Members} member(s) from {Originals} original(s)",
                type.Name, dimension.Name, dimension.FlowName, dimension.Members, dimension.Originals);
            types.Add(new CachePlanType(
                type.Name, CacheOrigins.Text(type.Origin), type.Describe(), null, "*", [DimensionColumns.Value, .. type.Fields.Select(f => f.Name)], dimension.Members));
        }

        var searched = spec.Types.Where(t => t.Origin == CacheOrigin.Osdu).ToList();
        if (searched.Count == 0)
        {
            return new CachePlanOutcome(DeliveryOperations.Plan, scope, flow.Name, current?.Version, types, types.Sum(t => t.Records), current?.SystemProperties ?? []);
        }

        using var osdu = await ConnectAsync(flow, ct).ConfigureAwait(false);
        foreach (var type in searched)
        {
            var body = new JsonObject { ["kind"] = type.Kind, ["query"] = type.Query, ["limit"] = 1, ["trackTotalCount"] = true };
            var page = await osdu.PostJsonAsync(QueryPath, body, ct).ConfigureAwait(false);
            var total = page["totalCount"] is JsonValue count && count.TryGetValue<long>(out var value)
                ? value
                : throw new DeliveryException($"{QueryPath} did not report totalCount for kind {type.Kind}.");
            _logger.LogInformation("plan {Type}: {Total} record(s) of kind {Kind} match the query {Query}", type.Name, total, type.Kind, type.Query);
            types.Add(new CachePlanType(type.Name, CacheOrigins.Text(type.Origin), type.Describe(), type.Kind, type.Query, type.Fields.Select(f => f.Name).ToList(), total));
        }

        var readings = await SystemPropertyCapture.ReadAsync(osdu, scope, _logger, ct).ConfigureAwait(false);
        var properties = SystemProperties.Merge(current?.SystemProperties ?? [], readings);
        return new CachePlanOutcome(DeliveryOperations.Plan, scope, flow.Name, current?.Version, types, types.Sum(t => t.Records), properties);
    }

    /// <summary>
    /// The declared types as a refresh captures them: widened to every path the partition keeps for each, with the run's
    /// parameter values substituted into each query.
    /// </summary>
    private static ReferenceCaptureSpec CaptureSpec(CacheDefinition flow, IReadOnlyDictionary<string, string> values, CacheDeclaration declaration)
        => new() { Types = flow.Types.Select(type => declaration.Widen(type) with { Query = FlowParameters.Substitute(type.Query, values) }).ToList() };

    /// <summary>
    /// The dictionary a type holds, found beside the flow's file as the repository sync found it: in the nearest dictionaries
    /// directory walking up. A node running the flow has the repository's tree at the run's commit, because a flow holding a
    /// dictionary requires it, so the file read is the one that commit holds.
    /// </summary>
    private DictionaryFile LoadDictionary(CacheDefinition flow, ReferenceTypeSpec type)
    {
        var folder = flow.SourcePath is { } path
            ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory()
            : throw new DeliveryException(
                $"Cache flow '{flow.Name}' holds dictionary {type.Dictionary}, which is found beside the flow's file, and this flow was not loaded from a file.");
        try
        {
            return new DictionaryCatalog(_context.Documents).Load(
                type.Dictionary!, folder, full => Shown(full, folder));
        }
        catch (FlowValidationException ex)
        {
            throw new DeliveryException($"Cache flow '{flow.Name}' could not read dictionary {type.Dictionary} for type {type.Name}, so nothing was captured: {ex.Message}", ex);
        }
    }

    /// <summary>The ledger a dimension type's members are read from: the module's, which every host that refreshes a cache has.</summary>
    private ILedger DimensionLedger(CacheDefinition flow, ReferenceTypeSpec type)
        => _context.Ledger ?? throw new DeliveryException(
            $"Cache flow '{flow.Name}' holds {type.Describe()}, whose members live in the module's database, and this host was started without it. Run it through the control plane or a node, or start the CLI with it. {OsduModuleDatabase.StartWithCatalogConnection}");

    /// <summary>A path as a message names it: from the folder holding the dictionaries directory, so it reads dictionaries/Name.yaml.</summary>
    private static string Shown(string full, string flowFolder)
    {
        var directory = DictionaryCatalog.Locate(flowFolder);
        var root = directory is null ? flowFolder : Path.GetDirectoryName(directory) ?? flowFolder;
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    private Task<OsduConnection> ConnectAsync(CacheDefinition flow, CancellationToken ct)
        => OsduConnection.CreateAsync(
            flow.Source.Endpoint ?? throw new DeliveryException(
                $"Cache flow '{flow.Name}' declares a type searched on OSDU and no source.endpoint to search it on."),
            flow.Source.Auth, flow.Source.Headers, flow.Reliability, _context.Secrets, allowLoopback: EngineContext.LoopbackAllowed,
            observer: _context.HttpObserver, paging: flow.Source.Paging, ct: ct);

    private static string ModeText(CacheChangeMode mode) => mode == CacheChangeMode.Auto ? "auto" : "approve";
}

/// <summary>
/// The <c>result</c> of a refresh run: the partition whose cache it merged into, the flow, the version the cache holds after
/// it and the one it replaced, whether a version was written at all, per type what was captured and what its changes reach
/// in the delivered estate, and what the partition's retention pruned afterwards.
/// </summary>
public sealed record CacheRefreshOutcome(
    string Operation, string Scope, string Flow, string Version, string? PreviousVersion, bool Written, DateTime CapturedUtc,
    IReadOnlyList<CachedTypeOutcome> Types, IReadOnlyList<SystemProperty> SystemProperties, CacheRetentionOutcome? Retention = null)
{
    public long Items => Types.Sum(t => (long)t.Items);

    /// <summary>Delivered records the refresh found no longer matching the cache.</summary>
    public long AffectedRecords => Types.Sum(t => t.AffectedRecords);
}

/// <summary>
/// One cached type as the refresh left it, with what its changes did to the delivered estate: its origin (osdu, table,
/// dictionary or dimension), where its records came from as a person reads it, for an OSDU type the kind searched, its content hash, and
/// by that hash how the version the refresh left holds it against the version before: <paramref name="Change"/> is added,
/// changed or unchanged. Only a type that was added or changed is analysed for what it reaches.
/// </summary>
public sealed record CachedTypeOutcome(
    string Name, string EntityType, string Origin, string Source, string? Kind, int Items, IReadOnlyList<string> Fields, string OnChange,
    int ChangedItems, int Changes, long AffectedRecords, string Change, string Hash);

/// <summary>One declared type as a plan counts it; <paramref name="Kind"/> and <paramref name="Query"/> are an OSDU type's search.</summary>
public sealed record CachePlanType(string Name, string Origin, string Source, string? Kind, string Query, IReadOnlyList<string> Fields, long Records);

/// <summary>
/// The <c>result</c> of a plan run on a cache flow: what each type's search matches, the version the partition's cache
/// holds now, and the system properties a refresh would record for the partition.
/// </summary>
public sealed record CachePlanOutcome(
    string Operation, string Scope, string Flow, string? CurrentVersion, IReadOnlyList<CachePlanType> Types, long Records,
    IReadOnlyList<SystemProperty> SystemProperties);
