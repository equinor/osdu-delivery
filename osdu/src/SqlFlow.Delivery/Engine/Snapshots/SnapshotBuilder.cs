using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// The cache capture's engine (osdu/docs/reference/concepts/partition-cache.md): captures a cache flow's types from
/// OSDU, or reads them from type files for offline work, and merges them into the cache of the flow's partition, which
/// writes a version when the cached content moved. A capture holds every record the flow's queries match with every
/// path the partition keeps for its types, so what one project captures is complete for every pipeline that reads the
/// partition. Every write then applies the partition's retention (<see cref="CacheRetention"/>), so the cache keeps
/// only the history it needs.
/// </summary>
public sealed partial class SnapshotBuilder
{
    private readonly ICacheStore _store;
    private readonly string _scope;
    private readonly string _flow;
    private readonly TimeProvider _time;
    private readonly ILogger<SnapshotBuilder> _logger;
    private readonly int _retentionDays;

    /// <param name="store">Where the partition's cache lives.</param>
    /// <param name="scope">The partition whose cache the capture merges into.</param>
    /// <param name="flowName">The cache flow the capture is made for, which the version and the membership record.</param>
    /// <param name="time">The clock the capture instant, and the instant the retention is applied at, are read from.</param>
    /// <param name="logger">Where the capture reports what it found.</param>
    /// <param name="retentionDays">The retention the cache flow declares (<see cref="CacheDefinition.RetentionDays"/>).</param>
    public SnapshotBuilder(
        ICacheStore store, string scope, string flowName, TimeProvider time, ILogger<SnapshotBuilder> logger, int retentionDays = CacheRetention.DefaultDays)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retentionDays, CacheRetention.MaxDays);
        _store = store;
        _scope = scope;
        _flow = flowName;
        _time = time;
        _logger = logger;
        _retentionDays = retentionDays;
    }

    /// <summary>Mints a version label from a capture instant: sortable, one per second.</summary>
    public static string MintVersion(DateTimeOffset capturedUtc) => CacheVersionLabel.Mint(capturedUtc);

    /// <summary>
    /// Reads every type file in <paramref name="directory"/> (<c>{Name}.json</c>: the entity type and its items, each an
    /// <c>id</c> and the cached values) and merges them into the partition's cache as the flow's capture. The files have to
    /// hold exactly the types the cache flow declares, each under its declared entity type and with no value the partition's
    /// cache does not keep for the type, because the cache flow is the definition of what it contributes and an import is no
    /// way around it. An import is checked against the partition's declaration exactly as a refresh is: a flow that disagrees
    /// with another flow of the partition is refused, and because a record the import holds replaces the cached record whole,
    /// the files of a type have to carry every value another flow of the partition keeps for it, or the import would drop
    /// those values from every record it holds.
    /// </summary>
    public async Task<CacheWrite> ImportDirectoryAsync(
        string directory, IReadOnlyList<ReferenceTypeSpec> declared, CacheCapture capture, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(capture);
        if (!Directory.Exists(directory))
        {
            throw new DeliveryException($"The directory '{directory}' to import cached types from does not exist.");
        }

        var partition = await _store.DeclarationAsync(_scope, ct).ConfigureAwait(false);
        partition.ThrowOnConflicts(_flow, declared);

        var types = new List<ReferenceType>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            JsonObject node;
            try
            {
                node = JsonNode.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false)) as JsonObject
                    ?? throw new DeliveryException($"Cached type file '{file}' is not a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new DeliveryException($"Cached type file '{file}' is not valid JSON ({ex.Message}).", ex);
            }

            types.Add(ReferenceType.FromJson(Path.GetFileNameWithoutExtension(file), node));
        }

        if (types.Count == 0)
        {
            throw new DeliveryException($"The directory '{directory}' holds no cached type files ({{Name}}.json), so there is nothing to import.");
        }

        CheckDeclared(types, declared, partition, directory);
        return await WriteAsync(types, capture, readings: [], ct).ConfigureAwait(false);
    }

    private void CheckDeclared(IReadOnlyList<ReferenceType> types, IReadOnlyList<ReferenceTypeSpec> declared, CacheDeclaration partition, string directory)
    {
        var problems = new List<string>();

        // A lookup table is filled from its own origin, a table or a dictionary, wherever the flow runs, so it is neither
        // required in an import nor accepted from one: the files stand in for OSDU alone.
        foreach (var spec in declared.Where(spec => !spec.IsLookup && !types.Any(t => t.Name.Equals(spec.Name, StringComparison.OrdinalIgnoreCase))))
        {
            problems.Add($"{spec.Name}.json is missing, and cache flow '{_flow}' declares {spec.Name}");
        }

        foreach (var type in types)
        {
            if (declared.FirstOrDefault(spec => spec.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase)) is not { } spec)
            {
                problems.Add($"{type.Name}.json holds a type cache flow '{_flow}' does not declare");
                continue;
            }

            if (spec.IsLookup)
            {
                problems.Add($"{type.Name}.json holds {spec.Name}, which cache flow '{_flow}' fills from {spec.Describe()}; a lookup table is captured from its origin, never imported");
                continue;
            }

            if (!type.EntityType.Equals(spec.EntityType, StringComparison.Ordinal))
            {
                problems.Add($"{type.Name}.json holds entity type {type.EntityType}, and cache flow '{_flow}' declares {spec.EntityType}");
            }

            // The files stand in for what a search of the partition would return, so every record is an OSDU record of the
            // declared entity type in this partition. Anything else would pass off hand-made rows as the platform's.
            var foreign = type.Items.Where(item => !IsRecordOf(item.Id, spec.EntityType)).Select(item => item.Id).Take(5).ToList();
            if (foreign.Count > 0)
            {
                problems.Add(
                    $"{type.Name}.json holds records whose ids are not ids of {spec.EntityType} records in partition '{_scope}' ({string.Join(", ", foreign)}); an import holds what a search of the partition would return, each id written {_scope}:{spec.EntityType}:<code>");
            }

            // The widened type is what a refresh of the flow would fetch: its own paths, then every path the partition keeps.
            var kept = partition.Widen(spec).Fields;
            var keptNames = kept.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in type.FieldNames.Where(name => !keptNames.Contains(name)))
            {
                problems.Add($"{type.Name}.json holds values under '{name}', which neither cache flow '{_flow}' nor any other cache flow of partition '{_scope}' captures for {spec.Name}");
            }

            if (type.Items.Count == 0)
            {
                // A type holding no record replaces no cached record, so it drops no value.
                continue;
            }

            var own = spec.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var carried = type.FieldNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = kept.Where(f => !own.Contains(f.Name) && !carried.Contains(f.Name)).ToList();
            if (missing.Count > 0)
            {
                var named = missing.Select(field =>
                {
                    var flows = partition.Of(spec.Name)
                        .Where(d => d.Fields.Any(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase)))
                        .Select(d => $"'{d.FlowName}'");
                    return $"'{field.Name}' (from {field.Path}, kept by cache flow {string.Join(", ", flows)})";
                });
                problems.Add(
                    $"{type.Name}.json holds no values under {string.Join(", ", named)}, which the cache of partition '{_scope}' keeps for {spec.Name}; a record the import holds replaces the cached record whole, so importing it would drop those values. Add them to the file");
            }
        }

        if (problems.Count > 0)
        {
            throw new DeliveryException($"The files under '{directory}' are not what cache flow '{_flow}' declares, so nothing was imported: {string.Join("; ", problems)}.");
        }
    }

    /// <summary>Whether <paramref name="id"/> is an OSDU id (without version) of a record of <paramref name="entityType"/> in this partition.</summary>
    private bool IsRecordOf(string id, string entityType)
    {
        var parts = id.Split(':', 3);
        return parts.Length == 3
            && string.Equals(parts[0], _scope, StringComparison.Ordinal)
            && string.Equals(parts[1], entityType, StringComparison.Ordinal)
            && parts[2].TrimEnd(':').Length > 0;
    }

    /// <summary>
    /// Captures every type of <paramref name="spec"/> through the OSDU search service, reads the partition's system
    /// properties from the platform's services (<see cref="SystemPropertyCapture"/>), which every capture does whatever
    /// the flow declares, and merges both into the partition's cache.
    /// </summary>
    public async Task<CacheWrite> CaptureAsync(OsduConnection osdu, ReferenceCaptureSpec spec, CacheCapture capture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(capture);
        var types = new List<ReferenceType>(spec.Types.Count);
        foreach (var typeSpec in spec.Types)
        {
            types.Add(await CaptureTypeAsync(osdu, typeSpec, ct: ct).ConfigureAwait(false));
        }

        var readings = await SystemPropertyCapture.ReadAsync(osdu, _scope, _logger, ct).ConfigureAwait(false);
        return await WriteAsync(types, capture, readings, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges the types into the partition's cache, which writes the next version unless the merge changes no cached content,
    /// then applies the partition's retention. A version label is a timestamp and it enters the render context, so writing
    /// one for a capture that found nothing new would change the metadata hash of every record built from the cache and
    /// deliver them all again for no reason. Comparing content makes refreshing a cache as often as anyone likes free. Every
    /// producer ends here, whatever the origin of the types it captured, so one refresh of a flow writes one version, and
    /// every refresh and import weighs the partition's history, whether it wrote a version or not.
    /// </summary>
    public async Task<CacheWrite> WriteAsync(
        IReadOnlyList<ReferenceType> types, CacheCapture capture, IReadOnlyList<SystemPropertyReading> readings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(readings);
        var write = await _store.MergeAsync(_scope, _flow, types, capture, _time.GetUtcNow(), readings, ct).ConfigureAwait(false);
        if (write.Written)
        {
            _logger.LogInformation(
                "Cache of partition {Scope}: version {Version} written from cache flow {Flow}, holding {Types} type(s) and {Items} record(s).",
                _scope, write.Snapshot.Version, _flow, write.Snapshot.Types.Count, write.Snapshot.Types.Sum(t => t.Items.Count));
        }
        else
        {
            _logger.LogInformation(
                "Cache of partition {Scope}: cache flow {Flow} found exactly what version {Version} holds, so no version was written and nothing built from the cache renders again.",
                _scope, _flow, write.Snapshot.Version);
        }

        return write with { Retention = await RetainAsync(capture.CapturedBy, ct).ConfigureAwait(false) };
    }

    /// <summary>
    /// Applies the partition's retention once the merge is written, recording <paramref name="actor"/>, who asked for the
    /// refresh or the import, on every version it prunes. The merge stands whatever happens here: a retention that fails is
    /// logged and reported in the outcome, and the next refresh or import applies it again, finishing what this one left.
    /// Cancellation still cancels.
    /// </summary>
    private async Task<CacheRetentionOutcome> RetainAsync(string actor, CancellationToken ct)
    {
        try
        {
            var outcome = await _store.ApplyRetentionAsync(
                CacheRetentionRequest.ForFlow(_scope, _flow, _retentionDays, _time.GetUtcNow(), actor), ct).ConfigureAwait(false);
            if (outcome.Deferred is { } waiting)
            {
                _logger.LogInformation(
                    "Cache of partition {Scope}: retention of {Rule} pruned nothing yet: {Waiting}.", _scope, outcome.Rule, waiting);
            }
            else if (outcome.Pruned.Count > 0 || outcome.RowsRemoved > 0)
            {
                _logger.LogInformation(
                    "Cache of partition {Scope}: retention of {Rule} pruned {Pruned} version(s) ({Versions}) and removed {Rows} stored row(s); {Kept} version(s) keep their records.",
                    _scope, outcome.Rule, outcome.Pruned.Count, Listed(outcome.Pruned), outcome.RowsRemoved, outcome.Kept);
            }
            else
            {
                _logger.LogInformation(
                    "Cache of partition {Scope}: retention of {Rule} pruned nothing; {Kept} version(s) keep their records.",
                    _scope, outcome.Rule, outcome.Kept);
            }

            return outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = HeaderRedaction.RedactMessage(ex.GetBaseException().Message);
            _logger.LogWarning(
                ex,
                "Cache of partition {Scope}: the retention could not be applied after cache flow {Flow} updated the cache, which stands; the next refresh or import applies it again. {Reason}",
                _scope, _flow, reason);
            return new CacheRetentionOutcome(_retentionDays, 0, [], 0, reason);
        }
    }

    /// <summary>The pruned versions as a log line names them: the first few, and how many more.</summary>
    private static string Listed(IReadOnlyList<string> versions)
        => versions.Count <= 5 ? string.Join(", ", versions) : $"{string.Join(", ", versions.Take(5))} and {versions.Count - 5} more";
}

/// <summary>
/// Reads one type out of OSDU, whole, and caches the declared paths of every record: of the projection the search index returns,
/// or, for a type that reads its records whole (<see cref="ReferenceTypeSpec.FetchRecords"/>), of each record as the storage
/// service holds it, the search only naming which records the type holds.
/// </summary>
public sealed partial class SnapshotBuilder
{
    /// <summary>The storage service's batch read of records by id a capture reads whole records through (openapi storage v2, POST /query/records).</summary>
    public const string DefaultStoragePath = Protocols.OsduRecordProtocol.DefaultVerifyBatchPath;

    /// <summary>How many storage reads of a search page's records a capture has in flight at once, each of up to <see cref="StorageRecords.Batch"/> ids.</summary>
    public const int StorageReadsAtOnce = 4;

    private const int SearchPageSize = OsduSearch.MaxPage;

    /// <summary>The cursor search the capture pages through (openapi search v2, POST /query_with_cursor).</summary>
    private const string SearchPath = "/api/search/v2/query_with_cursor";

    /// <summary>The plain search that counts a type whose cursor names no exact total (openapi search v2, POST /query).</summary>
    private const string QueryPath = "/api/search/v2/query";

    /// <summary>How many ids a message lists of those storage left out or left unanswered.</summary>
    private const int IdsShown = 5;

    /// <summary>
    /// The properties of a record the storage service holds (openapi storage v2, <c>Record</c>). A path under any other top-level
    /// property reads one the indexer adds to its projection (<c>namespace</c>, <c>type</c>, <c>authority</c>, <c>source</c>,
    /// <c>index</c>), which only the search hit carries.
    /// </summary>
    private static readonly FrozenSet<string> StorageProperties = FrozenSet.ToFrozenSet(
        ["id", "kind", "version", "acl", "legal", "data", "ancestry", "meta", "tags", "createUser", "createTime", "modifyUser", "modifyTime"],
        StringComparer.Ordinal);

    /// <summary>
    /// Captures one reference type: every record of its search kind, projected onto the paths it declares. The records are
    /// found through the shared cursor reader (<see cref="OsduSearch.PagesAsync"/>), which hands each one out once and every
    /// one of them or fails, reading a type again once when its first read fails part way or comes back short. A type that
    /// reads its records whole asks the search for their ids alone and reads each page of them back from
    /// <paramref name="storagePath"/> (<see cref="ReadWholeAsync"/>). A type that cannot be read whole fails the capture, and
    /// with it the refresh, before anything is written, so the partition's cache keeps the version it had rather than one
    /// missing records.
    /// </summary>
    /// <param name="osdu">The connection the type is searched, and its records read, through.</param>
    /// <param name="typeSpec">The type, widened to every path the partition keeps for it.</param>
    /// <param name="storagePath">The storage service's batch read, for a type that reads its records whole.</param>
    /// <param name="ct">Cancels the capture.</param>
    /// <exception cref="DeliveryException">The type could not be read whole.</exception>
    public async Task<ReferenceType> CaptureTypeAsync(
        OsduConnection osdu, ReferenceTypeSpec typeSpec, string storagePath = DefaultStoragePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(osdu);
        ArgumentNullException.ThrowIfNull(typeSpec);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        typeSpec.Validate();

        var items = new Dictionary<string, ReferenceItem>(StringComparer.Ordinal);
        // One pool for the capture: a value many records hold (the field a thousand wellbores lie in) is kept once.
        var pool = new StringPool();
        var coverage = typeSpec.Fields.ToDictionary(f => f.Name, _ => 0, StringComparer.OrdinalIgnoreCase);
        var search = new OsduSearch(osdu.Client, QueryPath, SearchPath, _logger, osdu.Paging);
        var attributes = typeSpec.Fields.Where(f => f.IsAttribute).ToList();

        // What each record has to hold: every path of it the type keeps, and what the first step of each attribute reads of it.
        var read = typeSpec.Fields.SelectMany(PathsRead).Distinct(StringComparer.Ordinal).ToList();
        var indexOnly = read.Select(RootOf).Where(root => root.Length > 0 && !StorageProperties.Contains(root)).ToFrozenSet(StringComparer.Ordinal);
        var whole = typeSpec.FetchRecords ? new WholeRead(new StorageRecords(osdu.Client, storagePath), indexOnly) : null;

        // Reading records whole, the search only names them: it returns their ids, and the paths under a property only the
        // index holds, which storage's record lacks.
        var returned = read.Where(path => !typeSpec.FetchRecords || indexOnly.Contains(RootOf(path))).Prepend("id").Distinct(StringComparer.Ordinal).ToList();
        var query = new OsduSearchQuery
        {
            Kind = typeSpec.Kind ?? throw new DeliveryException($"Reference type {typeSpec.Name} names no kind, so there is nothing to search for it."),
            Query = typeSpec.Query,
            ReturnedFields = returned,
        };

        // An attribute reads the records the cached ones reference as a dimension reads them: from the index, or whole from
        // storage for a type that reads its records whole. The cached records in hand answer its first step.
        var reader = attributes.Count == 0
            ? null
            : new CacheAttributeReader(
                whole is null ? new SearchLabelRecords(search) : new StorageLabelRecords(whole.Storage), StorageReadsAtOnce);
        var asked = returned.ToHashSet(StringComparer.Ordinal);
        Func<IReadOnlyList<string>, bool> covers = whole is null
            ? fields => fields.All(asked.Contains)
            : fields => fields.All(field => RootOf(field) is var root && (StorageProperties.Contains(root) || indexOnly.Contains(root)));
        await foreach (var page in search.PagesAsync(query, SearchPageSize, ct).ConfigureAwait(false))
        {
            var records = whole is null ? Parsed(typeSpec, page.Hits) : await ReadWholeAsync(typeSpec, whole, page.Hits, ct).ConfigureAwait(false);
            var attached = reader is null ? null : await reader.ReadAsync(records, attributes, covers, pool, ct).ConfigureAwait(false);
            foreach (var record in records)
            {
                var item = Project(record, typeSpec.Fields, pool, attached)
                    ?? throw new DeliveryException($"Reference type {typeSpec.Name}: the search of kind {typeSpec.Kind} handed on a record without an id.");
                if (!items.TryAdd(item.Id, item))
                {
                    throw new DeliveryException($"Reference type {typeSpec.Name}: the search of kind {typeSpec.Kind} handed on record {item.Id} twice.");
                }

                foreach (var name in item.Fields.Keys)
                {
                    coverage[name]++;
                }
            }
        }

        if (items.Count == 0 && whole is { Gone: > 0 })
        {
            // The search found records and storage holds none of them: the index has not caught up with their deletion.
            _logger.LogWarning(
                "Reference type {Type}: the search of kind {Kind} for query {Query} found records, and storage holds none of them any more, so nothing is cached for it.",
                typeSpec.Name, typeSpec.Kind, typeSpec.Query);
        }
        else if (items.Count == 0)
        {
            // Nothing matched at all: the paths are not the question, the kind and the query are.
            _logger.LogWarning(
                "Reference type {Type}: the search matched no record of kind {Kind} for query {Query}, so nothing is cached for it. Check the kind and the query.",
                typeSpec.Name, typeSpec.Kind, typeSpec.Query);
        }
        else
        {
            foreach (var field in typeSpec.Fields.Where(f => coverage[f.Name] == 0))
            {
                // Silence here used to look like bad source data at render time, so an empty path is reported at capture. The
                // index holds only what the kind's schema describes, so for a type read from it a path the schema leaves open
                // is named as the likely cause. Why an attribute read nothing, the reader's notes say.
                if (field.IsAttribute)
                {
                    _logger.LogWarning(
                        "Reference type {Type}: no record gave attribute '{Name}' ({Definition}), so nothing is cached under it; the notes below say why.",
                        typeSpec.Name, field.Name, field.Path);
                }
                else if (whole is null)
                {
                    _logger.LogWarning(
                        "Reference type {Type}: no item carried '{Path}', so nothing is cached under '{Name}'. Check the path against the kind {Kind}; the search index holds only what the kind's schema describes, so a path it leaves open (such as one under data.ExtensionProperties) is read with fetchRecords: true on the type.",
                        typeSpec.Name, field.Path, field.Name, typeSpec.Kind);
                }
                else
                {
                    _logger.LogWarning(
                        "Reference type {Type}: no record storage holds carried '{Path}', so nothing is cached under '{Name}'. Check the path against the records of kind {Kind}.",
                        typeSpec.Name, field.Path, field.Name, typeSpec.Kind);
                }
            }
        }

        if (whole is not null)
        {
            if (whole.Gone > 0)
            {
                // The index lags storage: a record deleted since it was indexed is still found, and storage, the record's
                // truth, no longer hands it over.
                _logger.LogWarning(
                    "Reference type {Type}: storage did not return {Gone} record(s) the search of kind {Kind} found (deleted since they were indexed, or not readable with the flow's credentials), so they are not cached. First: {Ids}",
                    typeSpec.Name, whole.Gone, typeSpec.Kind, string.Join(", ", whole.GoneShown));
            }

            _logger.LogInformation(
                "Reference type {Type}: {Read} record(s) read whole from storage in {Requests} request(s).", typeSpec.Name, whole.Read, whole.Requests);
        }

        if (reader is not null)
        {
            // What a dimension build's notes say of its attributes, for the type's: the records without one and why, the
            // records naming more references at a step than an attribute follows, the values cut.
            foreach (var note in reader.Notes())
            {
                _logger.LogWarning("Reference type {Type}: {Note}", typeSpec.Name, note);
            }

            _logger.LogInformation(
                "Reference type {Type}: {Attributes} attribute(s) read through {Queries} read(s) of the records the cached ones reference.",
                typeSpec.Name, attributes.Count, reader.Queries);
        }

        _logger.LogInformation(
            "Captured {Count} {Type} item(s) with {Fields}.",
            items.Count, typeSpec.Name, string.Join(", ", typeSpec.Fields.Select(f => $"{f.Name}={coverage[f.Name]}")));
        return new ReferenceType(typeSpec.Name, typeSpec.EntityType, items.Values.OrderBy(i => i.Id, StringComparer.Ordinal));
    }

    /// <summary>A page's hits as records, each the projection the index returned.</summary>
    private static List<JsonObject> Parsed(ReferenceTypeSpec typeSpec, IReadOnlyList<JsonElement> hits)
    {
        var records = new List<JsonObject>(hits.Count);
        foreach (var hit in hits)
        {
            // The reader hands out only records with an id, each once: a hit that is neither is a fault of the reader, which no
            // capture may cache around.
            records.Add(JsonNode.Parse(hit.GetRawText()) as JsonObject
                ?? throw new DeliveryException($"Reference type {typeSpec.Name}: the search of kind {typeSpec.Kind} handed on a hit that is not a record."));
        }

        return records;
    }

    /// <summary>
    /// A page's records read whole from storage, up to <see cref="StorageRecords.Batch"/> ids a request and
    /// <see cref="StorageReadsAtOnce"/> requests at a time, in the page's order: each as storage holds it, with the properties
    /// only the index holds taken from its hit. A record storage does not return is left out and counted, since storage is the
    /// record's truth and the index lags it; one storage leaves unanswered after being asked twice fails the type, because
    /// whether it holds the record is unknown and the type would be cached short.
    /// </summary>
    /// <exception cref="DeliveryException">Storage left records unanswered, or handed on one that is not a record.</exception>
    private static async Task<List<JsonObject>> ReadWholeAsync(ReferenceTypeSpec typeSpec, WholeRead whole, IReadOnlyList<JsonElement> hits, CancellationToken ct)
    {
        var found = new Dictionary<string, JsonElement>(hits.Count, StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            if (OsduSearch.IdOf(hit) is not { Length: > 0 } id)
            {
                throw new DeliveryException($"Reference type {typeSpec.Name}: the search of kind {typeSpec.Kind} handed on a record without an id.");
            }

            if (!found.TryAdd(id, hit))
            {
                throw new DeliveryException($"Reference type {typeSpec.Name}: the search of kind {typeSpec.Kind} handed on record {id} twice.");
            }
        }

        var chunks = found.Keys.Chunk(StorageRecords.Batch).ToList();
        var reads = new StorageRead[chunks.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, chunks.Count),
            new ParallelOptions { MaxDegreeOfParallelism = StorageReadsAtOnce, CancellationToken = ct },
            async (i, token) => reads[i] = await whole.Storage.ReadAsync(chunks[i], attributes: null, token).ConfigureAwait(false)).ConfigureAwait(false);

        var unanswered = reads.SelectMany(r => r.Unanswered).ToList();
        if (unanswered.Count > 0)
        {
            throw new DeliveryException(
                $"Reference type {typeSpec.Name}: storage did not say whether it holds {unanswered.Count} record(s) the search of kind {typeSpec.Kind} found, asked twice (retryRecords: {string.Join(", ", unanswered.Take(IdsShown))}), so the type could not be read whole and nothing was cached. Refresh again; the cache keeps the version it had.");
        }

        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var record in reads.SelectMany(r => r.Records))
        {
            if (OsduSearch.IdOf(record) is { } id && found.ContainsKey(id))
            {
                byId.TryAdd(id, record);
            }
        }

        var records = new List<JsonObject>(byId.Count);
        foreach (var (id, hit) in found)
        {
            if (!byId.TryGetValue(id, out var stored))
            {
                whole.LeftOut(id);
                continue;
            }

            var record = JsonNode.Parse(stored.GetRawText()) as JsonObject
                ?? throw new DeliveryException($"Reference type {typeSpec.Name}: storage handed on {id} as something other than a record.");
            foreach (var root in whole.IndexOnly)
            {
                if (!record.ContainsKey(root) && hit.TryGetProperty(root, out var indexed))
                {
                    record[root] = JsonNode.Parse(indexed.GetRawText());
                }
            }

            records.Add(record);
        }

        whole.Counted(records.Count, chunks.Count);
        return records;
    }

    /// <summary>
    /// The paths of a cached record a field reads: its own path, or, for an attribute, what its first step reads of the record
    /// (the path without its filters, and the property each filter compares), as a dimension's search asks for it.
    /// </summary>
    internal static IEnumerable<string> PathsRead(ReferenceFieldSpec field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Attribute is not { } attribute)
        {
            return [field.Path];
        }

        var first = DimensionPath.Parse(attribute.Steps[0]);
        return first.Path?.ReturnedFields
            ?? throw new DeliveryException($"Attribute {field.Name} reads '{attribute.Steps[0]}', which is not a path: {first.Problem}.");
    }

    /// <summary>Whether a record read whole from storage holds what <paramref name="path"/> reads: a path under a property storage keeps.</summary>
    internal static bool StorageHolds(string path) => StorageProperties.Contains(RootOf(path));

    /// <summary>The top-level property a path starts at (<c>data</c> of <c>data.Code</c>, <c>index</c> of <c>$.index.statusCode</c>), as the path reader parses it.</summary>
    private static string RootOf(string path)
    {
        var text = path.TrimStart('$').TrimStart('.');
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']', StringComparison.Ordinal);
            return close < 0 ? string.Empty : text[1..close].Trim().Trim('\'', '"');
        }

        var end = text.IndexOfAny(['.', '[']);
        return end < 0 ? text : text[..end];
    }

    /// <summary>
    /// A type's read of its records whole: the storage read, the properties taken from the hit, and what it has read so far. Pages
    /// are read one after the other, so the counts are kept without a lock.
    /// </summary>
    private sealed class WholeRead(StorageRecords storage, FrozenSet<string> indexOnly)
    {
        private readonly List<string> _goneShown = [];

        public StorageRecords Storage { get; } = storage;

        public FrozenSet<string> IndexOnly { get; } = indexOnly;

        /// <summary>The records read whole.</summary>
        public long Read { get; private set; }

        /// <summary>The storage requests made.</summary>
        public long Requests { get; private set; }

        /// <summary>The records the search found that storage did not return.</summary>
        public long Gone { get; private set; }

        /// <summary>The first of those, as a message names them.</summary>
        public IReadOnlyList<string> GoneShown => _goneShown;

        public void LeftOut(string id)
        {
            Gone++;
            if (_goneShown.Count < IdsShown)
            {
                _goneShown.Add(id);
            }
        }

        public void Counted(int read, int requests)
        {
            Read += read;
            Requests += requests;
        }
    }

    /// <summary>
    /// Projects one record onto the declared paths, keeping whatever shape each path yields: a search hit, or a record read whole
    /// from storage, which holds every path the record does. An attribute is no path of the record: its value, read through
    /// <see cref="CacheAttributeReader"/>, comes from <paramref name="attributes"/>, by the record's id.
    /// </summary>
    internal static ReferenceItem? Project(
        JsonObject hit, IReadOnlyList<ReferenceFieldSpec> fields, StringPool pool,
        IReadOnlyDictionary<string, List<KeyValuePair<string, ReferenceValue>>>? attributes = null)
    {
        var id = hit["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var values = new List<KeyValuePair<string, ReferenceValue>>(fields.Count);
        foreach (var field in fields.Where(f => !f.IsAttribute))
        {
            var hits = JsonPathReader.SelectNodes(hit, field.Path);
            if (hits.Count == 0)
            {
                continue;
            }

            values.Add(KeyValuePair.Create(field.Name, ReferenceValue.OfMany(hits, pool)));
        }

        if (attributes is not null && attributes.TryGetValue(id, out var read))
        {
            values.AddRange(read);
        }

        return new ReferenceItem(id, ReferenceFields.Of(values, pool));
    }
}

/// <summary>A read-only OSDU connection for a cache capture: a flow's auth and headers against an OSDU base URL.</summary>
public sealed class OsduConnection : IDisposable
{
    private readonly HttpRuntime _http;

    private OsduConnection(
        string endpoint,
        TargetAuth auth,
        IReadOnlyDictionary<string, string> headers,
        FlowReliability reliability,
        ISecretResolver secrets,
        HttpMessageHandler? handler,
        bool allowLoopback,
        IHttpObserver? observer,
        SearchPaging paging)
    {
        Endpoint = endpoint;
        Paging = paging;
        _http = new HttpRuntime(reliability, secrets, handler: handler, allowLoopback: allowLoopback, observer: observer);
        Client = new Protocols.OsduHttpClient(_http, endpoint, auth, headers);
    }

    /// <summary>
    /// A connection over an endpoint and headers as a flow declares them, every <c>${env:...}</c> and
    /// <c>${keyvault:...}</c> reference in them resolved here, the way <see cref="Protocols.ProtocolFactory.ClientAsync"/>
    /// resolves a delivery target's. A declared value is never used as a URL or a header unresolved.
    /// </summary>
    /// <remarks>
    /// <paramref name="handler"/> replaces the built transport (null builds the configured one) and
    /// <paramref name="allowLoopback"/> lets the URL guard accept a loopback endpoint; both exist for tests.
    /// <paramref name="observer"/> is told of every call the capture sends, which a cache run puts on its trace.
    /// <paramref name="paging"/> is how a capture pages a type's search, as the flow's <c>source.paging</c> says.
    /// </remarks>
    /// <exception cref="DeliveryException">The endpoint does not resolve to an absolute http or https URL.</exception>
    public static async Task<OsduConnection> CreateAsync(
        string endpoint,
        TargetAuth auth,
        IReadOnlyDictionary<string, string> headers,
        FlowReliability reliability,
        ISecretResolver secrets,
        HttpMessageHandler? handler = null,
        bool allowLoopback = false,
        IHttpObserver? observer = null,
        SearchPaging paging = SearchPaging.Cursor,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(reliability);
        ArgumentNullException.ThrowIfNull(secrets);

        var resolved = (await secrets.ResolveAsync(endpoint, ct).ConfigureAwait(false)).Trim().TrimEnd('/');
        if (!Uri.TryCreate(resolved, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            // The declared value is named, never what it resolved to: a reference is safe to show, its value may not be.
            throw new DeliveryException($"The OSDU endpoint '{endpoint}' does not resolve to an absolute http or https URL.");
        }

        var resolvedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            resolvedHeaders[name] = await secrets.ResolveAsync(value, ct).ConfigureAwait(false);
        }

        return new OsduConnection(resolved, auth, resolvedHeaders, reliability, secrets, handler, allowLoopback, observer, paging);
    }

    public string Endpoint { get; }

    /// <summary>How a capture over this connection pages a type's search (<see cref="OsduSearch.PagesAsync"/>).</summary>
    public SearchPaging Paging { get; }

    /// <summary>
    /// The connection as the protocols' client, over the same transport, auth and headers: what the shared search reader
    /// sends through, so a capture pages its types exactly as every other reader of the search service does.
    /// </summary>
    public Protocols.OsduHttpClient Client { get; }

    /// <summary>A GET of a service's JSON object, under the flow's auth and headers.</summary>
    public async Task<JsonObject> GetJsonAsync(string path, CancellationToken ct)
    {
        var url = Url(path);
        var result = await Client.SendJsonAsync(HttpMethod.Get, url, null, null, ct).ConfigureAwait(false);
        return JsonNode.Parse(result.Body) as JsonObject ?? throw new DeliveryException($"{HeaderRedaction.DescribeUrl(url)} did not return a JSON object.");
    }

    /// <summary>A POST of a search, a read the service answers alike however often it is asked, so it is repeated like any read.</summary>
    public async Task<JsonObject> PostJsonAsync(string path, JsonObject body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var url = Url(path);
        var result = await Client.SendJsonAsync(HttpMethod.Post, url, body, null, ct, idempotent: true).ConfigureAwait(false);
        return JsonNode.Parse(result.Body) as JsonObject ?? throw new DeliveryException($"{HeaderRedaction.DescribeUrl(url)} did not return a JSON object.");
    }

    /// <summary>A path under the endpoint, taken as written: no token in it is substituted.</summary>
    private Uri Url(string path) => new(Endpoint + "/" + path.TrimStart('/'));

    public void Dispose() => _http.Dispose();
}
