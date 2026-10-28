using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// What one of a pipeline's cache steps did (osdu/docs/reference/flow/delivery.md, cache): which step, the partition and the
/// types it brought up to date, how many records it took in (the records the searches found before the run, or the records
/// the run changed in OSDU after it) and how many of those OSDU's storage service returned, how many it took out because
/// OSDU no longer holds them, the version of the cache it left current and whether it wrote it, how many records built from
/// what moved it asked to be planned again, and, when it did nothing, why.
/// </summary>
public sealed record CacheStepOutcome(
    string Step,
    string Scope,
    IReadOnlyList<string> Types,
    int Records,
    int Read,
    int Removed,
    string? Version,
    bool Written,
    long Released,
    string? Skipped)
{
    /// <summary>The step that brings the cache up to date before the run renders.</summary>
    public const string BeforeStep = "before";

    /// <summary>The step that puts what the run changed in OSDU into the cache as it ends.</summary>
    public const string AfterStep = "after";

    /// <summary>The step in one sentence, as the run's result carries it for the run page.</summary>
    public string Summary => Describe();

    /// <summary>The step as a run's log line and its result say it.</summary>
    public string Describe()
    {
        // A step that failed before it knew the types, or the partition, names none.
        var types = Types.Count == 0 ? string.Empty : $" ({string.Join(", ", Types)})";
        var cache = Scope.Length == 0 ? "the partition's cache" : $"the cache of partition '{Scope}'";
        var subject = Scope.Length == 0 ? "The partition's cache" : $"The cache of partition '{Scope}'";
        var when = Step == BeforeStep ? "before the run" : "after the run";
        if (Skipped is not null)
        {
            return $"{subject}{types} was not brought up to date {when}: {Skipped}";
        }

        var removed = Removed == 0 ? string.Empty : $", taking out {Removed} OSDU no longer holds";
        var state = Written ? $"as version {Version}" : $"unchanged at version {Version}";
        var released = Released == 0 ? string.Empty : $"; {Released} record(s) built without what changed are planned again";
        if (Step == BeforeStep)
        {
            return $"{subject}{types} was brought up to date {when} from the {Records} record(s) OSDU holds{removed}, {state}{released}.";
        }

        return Records == 0
            ? $"The run changed no record in OSDU that {cache}{types} holds."
            : $"{subject}{types} took the {Read} record(s) the run landed{removed}, {state}{released}.";
    }
}

/// <summary>
/// A pipeline's cache steps (osdu/docs/reference/flow/delivery.md, cache; osdu/docs/reference/concepts/partition-cache.md,
/// Pipelines bring the cache up to date). Before the run renders, the types it names are brought up to date from OSDU as their
/// cache flows would capture them, searched through the pipeline's own connection to its partition, so the run renders against
/// what OSDU holds now whoever delivered it. As the run ends, the records it changed in OSDU are read back from the storage
/// service by id and put into the types it names, so the pipelines after it render against them; storage is read because
/// the search index holds a record only a moment after it is written. Each step tags what its changes reach, as a refresh
/// does, and asks for those records to be planned again at once, so a pipeline later in the schedule finds them due.
/// </summary>
public sealed class CacheKeeper
{
    /// <summary>
    /// The most records the after step reads back. A run changing more is a bulk load, for which the cache flow's refresh is
    /// the cheaper way to bring the cache up to date; the step then says so and reads nothing.
    /// </summary>
    public const int MaxRecords = 50_000;

    /// <summary>
    /// The most records a step asks to plan again itself; the control plane's rollout carries the rest of a change at its
    /// own pace.
    /// </summary>
    public const long MaxReleased = 100_000;

    /// <summary>The records one rollout batch marks.</summary>
    private const int RolloutBatch = 5_000;

    /// <summary>The newest approved changes of the partition a step looks through for its own.</summary>
    private const int TagsRead = 1_000;

    private readonly EngineContext _context;
    private readonly ILogger _logger;

    public CacheKeeper(EngineContext context, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Brings <paramref name="types"/> of <paramref name="scope"/>'s cache up to date from OSDU before a run renders: each
    /// declaring cache flow's kind and query are searched with every path the partition keeps for the type, and the capture is
    /// merged as that flow's own, exactly as a refresh of the flow narrowed to these types would merge it. A record a delivery
    /// holds that the search did not find stays while OSDU's storage service still holds it. The step is the run's to
    /// complete: anything that keeps a type from being read whole fails it, and the run with it, before anything is sent.
    /// </summary>
    /// <param name="scope">The partition the pipeline delivers to, whose cache the step brings up to date.</param>
    /// <param name="types">The types <c>cache.before</c> names, resolved (<see cref="CacheStepPlan"/>).</param>
    /// <param name="pipeline">The pipeline, as the version's origin names it.</param>
    /// <param name="connect">Opens a search connection to the pipeline's platform and partition, under its own auth.</param>
    /// <param name="storagePath">The storage service's batch read (openapi storage v2, POST /query/records).</param>
    /// <param name="runId">The run, which a version written records.</param>
    /// <param name="actor">Who asked for the run.</param>
    /// <param name="ct">Cancels the step.</param>
    /// <exception cref="DeliveryException">A type cannot be searched as its cache flows declare it, or its capture failed.</exception>
    public async Task<CacheStepOutcome> BeforeAsync(
        string scope,
        IReadOnlyList<CacheStepType> types,
        string pipeline,
        Func<CancellationToken, Task<OsduConnection>> connect,
        string storagePath,
        Guid? runId,
        string actor,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeline);
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var store = _context.Cache ?? throw new DeliveryException(
            $"Pipeline '{pipeline}' brings the cache of partition '{scope}' up to date before it runs, and caches live in the module's database, which this host was started without. {OsduModuleDatabase.StartWithCatalogConnection}");
        var names = types.Select(t => t.TypeName).ToList();

        // A declaration whose query takes the cache flow's parameters can only be searched by that flow's run, which fills
        // them; a type every declaration of which does is not one this step can bring up to date. A lookup table from an
        // ingestion table is read as its cache flow reads it.
        var searched = new List<(string Flow, CacheStepType Type, CacheTypeDeclaration Declaration)>();
        var tables = types.Where(t => t.IsTable).ToList();
        var problems = new List<string>();
        foreach (var type in types.Where(t => !t.IsTable))
        {
            var usable = type.Declarations.Where(d => !d.IsLookup && d.Kind is not null && !FlowMapper.Tokens(d.Query).Any()).ToList();
            if (usable.Count == 0)
            {
                problems.Add($"{type.TypeName}: every cache flow declaring it searches it with a query that takes the flow's parameters ({string.Join(", ", type.Declarations.Select(d => d.FlowName))}), which only that flow's run fills");
                continue;
            }

            searched.AddRange(usable.Select(d => (d.FlowName, type, d)));
        }

        if (problems.Count > 0)
        {
            throw new DeliveryException(
                $"Pipeline '{pipeline}' cannot bring the cache of partition '{scope}' up to date before it runs, so nothing was read or sent: {string.Join("; ", problems)}. Run those cache flows before the pipeline, or leave the types out of cache.before.");
        }

        var declaration = await store.DeclarationAsync(scope, ct).ConfigureAwait(false);
        int found = 0, removed = 0;
        long released = 0;
        string? version = null;
        var written = false;

        // A lookup table is declared by one cache flow, and its capture replaces the whole type, as that flow's refresh does.
        foreach (var type in tables)
        {
            var declared = type.Declarations[0];
            var spec = new ReferenceTypeSpec
            {
                Name = type.TypeName,
                EntityType = type.EntityType,
                Origin = CacheOrigin.Table,
                Table = declared.SourceObject,
                Key = declared.Key,
                Fields = declared.Fields,
                OnChange = declared.OnChange,
            };
            var flow = new CacheDefinition { Name = declared.FlowName, Source = new CacheSource { Connection = declared.Connection }, Types = [spec] };
            var table = await TableCapture.CaptureAsync(flow, spec, _context.Secrets, _logger, ct).ConfigureAwait(false);
            var builder = new SnapshotBuilder(store, scope, declared.FlowName, _context.Time, _context.Loggers.CreateLogger<SnapshotBuilder>(), declaration.RetentionDays);
            var write = await builder.WriteAsync(
                [table], new CacheCapture(runId, actor, $"{declared.FlowName}'s {type.TypeName} from {declared.SourceObject}, read before pipeline '{pipeline}' ran"), [], ct)
                .ConfigureAwait(false);
            found += table.Items.Count;
            released += await TagAsync(scope, declaration, write, [type.TypeName], ct).ConfigureAwait(false);
            version = write.Snapshot.Version;
            written |= write.Written;
        }

        if (searched.Count == 0)
        {
            var only = new CacheStepOutcome(CacheStepOutcome.BeforeStep, scope, names, found, found, removed, version, written, released, null);
            _logger.LogInformation("{Outcome}", only.Describe());
            return only;
        }

        using var osdu = await connect(ct).ConfigureAwait(false);
        var storage = new StorageRecords(osdu.Client, storagePath);

        // One capture per declaring cache flow, merged as that flow's, so each flow keeps exactly what its own query finds.
        foreach (var flow in searched.GroupBy(s => s.Flow, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var builder = new SnapshotBuilder(store, scope, flow.Key, _context.Time, _context.Loggers.CreateLogger<SnapshotBuilder>(), declaration.RetentionDays);
            var captured = new List<ReferenceType>();
            foreach (var (_, type, declared) in flow)
            {
                captured.Add(await builder.CaptureTypeAsync(
                    osdu,
                    new ReferenceTypeSpec
                    {
                        Name = type.TypeName,
                        EntityType = type.EntityType,
                        Kind = declared.Kind,
                        Query = declared.Query,
                        Fields = type.Fields,
                        OnChange = declared.OnChange,
                    },
                    ct).ConfigureAwait(false));
            }

            var gone = await CacheRefresher.GoneAsync(store, scope, storage, captured, _logger, ct).ConfigureAwait(false);
            var capture = new CacheCapture(runId, actor, $"{flow.Key}'s {string.Join(", ", captured.Select(c => c.Name))}, searched before pipeline '{pipeline}' ran, through its connection")
            {
                Gone = gone,
            };
            var write = await builder.WriteAsync(captured, capture, [], ct).ConfigureAwait(false);
            found += captured.Sum(c => c.Items.Count);
            removed += gone.Values.Sum(ids => ids.Count);
            released += await TagAsync(scope, declaration, write, captured.Select(c => c.Name), ct).ConfigureAwait(false);
            version = write.Snapshot.Version;
            written |= write.Written;
        }

        var outcome = new CacheStepOutcome(CacheStepOutcome.BeforeStep, scope, names, found, found, removed, version, written, released, null);
        _logger.LogInformation("{Outcome}", outcome.Describe());
        return outcome;
    }

    /// <summary>
    /// Puts what the ledger <paramref name="ledgerFlowId"/> (named <paramref name="ledgerName"/>) changed in OSDU since
    /// <paramref name="sinceUtc"/> into <paramref name="types"/> of <paramref name="scope"/>'s cache as a run ends: the records
    /// it delivered, removed or reverted, and with <paramref name="stepDatasets"/> the datasets their steps of <c>enrich:</c>
    /// link, are read back from OSDU's storage service by id; a record storage returns is put in as it is now, held by the
    /// delivery, into the types holding its kind, and one storage no longer holds is taken out of the types whoever held it.
    /// </summary>
    /// <param name="scope">The partition the run delivered to.</param>
    /// <param name="types">The types <c>cache.after</c> names, resolved (<see cref="CacheStepPlan"/>).</param>
    /// <param name="ledgerFlowId">The ledger the run delivered into.</param>
    /// <param name="ledgerName">The ledger's name, which the delivery's hold of its records is named after.</param>
    /// <param name="sinceUtc">When the run started, less a margin for the clocks of the nodes that delivered for it.</param>
    /// <param name="client">Opens the connection to OSDU the run delivered through, used only when there is something to read.</param>
    /// <param name="storagePath">The storage service's batch read (openapi storage v2, POST /query/records).</param>
    /// <param name="runId">The run, which the version written records.</param>
    /// <param name="actor">Who asked for the run.</param>
    /// <param name="alsoChanged">
    /// OSDU ids the run changed that the ledger no longer names: the records a removal deleted from the ledger as it took
    /// them out of OSDU.
    /// </param>
    /// <param name="stepDatasets">
    /// True when a type holds a kind the pipeline's steps register (<see cref="CacheStepPlan.TakesStepDatasets"/>): the datasets
    /// the changed records link, as their target state lists them, are read back too.
    /// </param>
    /// <param name="ct">Cancels the step; what it merged before stands.</param>
    public async Task<CacheStepOutcome?> AfterAsync(
        string scope,
        IReadOnlyList<CacheStepType> types,
        Guid ledgerFlowId,
        string ledgerName,
        DateTime sinceUtc,
        Func<CancellationToken, Task<OsduHttpClient>> client,
        string storagePath,
        Guid? runId,
        string actor,
        IReadOnlyCollection<string>? alsoChanged,
        bool stepDatasets,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(types);
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerName);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (types.Count == 0 || _context.Cache is not { } store || _context.Ledger is not { } ledger)
        {
            return null;
        }

        var names = types.Select(t => t.TypeName).ToList();
        IReadOnlyList<string> changed = [
            .. await ledger.ChangedSinceAsync(ledgerFlowId, sinceUtc, MaxRecords + 1, stepDatasets ? EnrichParts.DatasetsValue : null, ct).ConfigureAwait(false),
            .. alsoChanged ?? []];
        if (changed.Count == 0)
        {
            return new CacheStepOutcome(CacheStepOutcome.AfterStep, scope, names, 0, 0, 0, null, false, 0, null);
        }

        if (changed.Count > MaxRecords)
        {
            return new CacheStepOutcome(
                CacheStepOutcome.AfterStep, scope, names, changed.Count, 0, 0, null, false, 0,
                $"the run changed more than {MaxRecords:N0} records, which the cache flows' refresh reads more cheaply than storage does one by one. Run the refresh of the cache flows declaring {string.Join(", ", names)}.");
        }

        // A cached record is kept under its id without a version, as the search index answers it.
        var ids = changed.Select(id => TargetId.WithoutVersion(id).TrimEnd(':')).Distinct(StringComparer.Ordinal).ToList();
        var storage = new StorageRecords(await client(ct).ConfigureAwait(false), storagePath);
        var records = new List<JsonObject>(ids.Count);
        var missing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in ids.Chunk(StorageRecords.Batch))
        {
            var read = await storage.ReadAsync(batch, null, ct).ConfigureAwait(false);
            missing.UnionWith(read.Missing.Select(id => TargetId.WithoutVersion(id).TrimEnd(':')));
            foreach (var record in read.Records)
            {
                if (JsonNode.Parse(record.GetRawText()) is JsonObject parsed)
                {
                    records.Add(parsed);
                }
            }
        }

        var pool = new StringPool();
        var captured = types
            .Select(type => new ReferenceType(
                type.TypeName,
                type.EntityType,
                records
                    .Where(record => Takes(type, record))
                    .Select(record => SnapshotBuilder.Project(record, type.Fields, pool))
                    .OfType<ReferenceItem>()
                    .GroupBy(item => item.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .OrderBy(item => item.Id, StringComparer.Ordinal)))
            .ToList();

        var declaration = await store.DeclarationAsync(scope, ct).ConfigureAwait(false);
        var holder = CacheHolders.Delivery(ledgerName);
        var builder = new SnapshotBuilder(store, scope, holder, _context.Time, _context.Loggers.CreateLogger<SnapshotBuilder>(), declaration.RetentionDays);
        IReadOnlySet<string> gone = missing;
        var capture = new CacheCapture(runId, actor, $"{records.Count} record(s) {ledgerName} changed in OSDU, read back from its storage service")
        {
            Adds = true,
            Removed = names.ToDictionary(name => name, _ => gone, StringComparer.OrdinalIgnoreCase),
        };
        var write = await builder.WriteAsync(captured, capture, [], ct).ConfigureAwait(false);
        var released = await TagAsync(scope, declaration, write, names, ct).ConfigureAwait(false);
        var outcome = new CacheStepOutcome(
            CacheStepOutcome.AfterStep, scope, names, ids.Count, records.Count, missing.Count, write.Snapshot.Version, write.Written, released, null);
        _logger.LogInformation("{Outcome}", outcome.Describe());
        return outcome;
    }

    /// <summary>
    /// Whether <paramref name="type"/> takes <paramref name="record"/>: a type holds the records of its own kinds, so a delivered
    /// log goes into a type of logs and the dataset its step registered into a type of datasets, never the other way round.
    /// </summary>
    private static bool Takes(CacheStepType type, JsonObject record)
        => record["kind"] is not JsonValue value || !value.TryGetValue<string>(out var kind) || type.Holds(kind);

    /// <summary>
    /// Tags what a step's version changed for the records built from it, as a refresh's changes are tagged, and carries the
    /// approved changes out at once, up to <see cref="MaxReleased"/> records: the records built without a value the step
    /// brought in are asked to be planned again before the pipelines after it run. Returns how many it asked for.
    /// </summary>
    private async Task<long> TagAsync(string scope, CacheDeclaration declaration, CacheWrite write, IEnumerable<string> names, CancellationToken ct)
    {
        if (!write.Written || _context.Ledger is not { } ledger)
        {
            return 0;
        }

        var analyzer = new CacheImpactAnalyzer(ledger, _context.Time, _logger);
        var tagged = 0;
        long released = 0;
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var change = write.Changes.Of(name) ?? CacheTypeChange.Unchanged;
            if (change == CacheTypeChange.Unchanged || write.Snapshot.Type(name) is not { } type)
            {
                continue;
            }

            var result = await analyzer.AnalyzeAsync(
                scope, write.Previous?.Type(name), type, declaration.ModeOf(name, CacheChangeMode.Auto), write.Previous?.Version, write.Snapshot.Version, ct)
                .ConfigureAwait(false);
            tagged += result.NewTags;
            released += result.Released;
        }

        if (tagged == 0)
        {
            return released;
        }

        var marked = released;
        var approved = await ledger.ListTagsAsync("approved", TagsRead, 0, scope, ct).ConfigureAwait(false);
        foreach (var tag in approved.Where(t => string.Equals(t.ToVersion, write.Snapshot.Version, StringComparison.Ordinal)))
        {
            while (marked - released < MaxReleased)
            {
                var batch = await ledger.RollOutTagAsync(tag.TagId, RolloutBatch, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
                marked += batch.Marked;
                if (batch.Completed || batch.Marked == 0)
                {
                    break;
                }
            }
        }

        return marked;
    }
}
