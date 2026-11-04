using System.Globalization;

namespace SqlFlow.Delivery.Snapshots;

/// <summary>
/// One cached type as one cache flow declares it, the way the catalog holds it after the repository sync: where its records
/// come from, for an OSDU type the kind and query it is searched with and whether its records are read whole from storage,
/// for a lookup table the name its key is kept under, and the retention the flow declares for the partition's cache.
/// </summary>
public sealed record CacheTypeDeclaration(
    string FlowName,
    string TypeName,
    string EntityType,
    string? Kind,
    string Query,
    IReadOnlyList<ReferenceFieldSpec> Fields,
    CacheChangeMode OnChange,
    CacheOrigin Origin = CacheOrigin.Osdu,
    string? Key = null,
    int RetentionDays = CacheRetention.DefaultDays,
    string? Connection = null,
    string? SourceObject = null,
    bool FetchRecords = false)
{
    /// <summary>True for a table or a dictionary, whose rows are not OSDU records.</summary>
    public bool IsLookup => Origin != CacheOrigin.Osdu;
}

/// <summary>
/// A type of a partition's cache a pipeline's cache step brings up to date (osdu/docs/reference/flow/delivery.md, cache), as
/// every synced cache flow declares it together: its name and entity type, every path the cache keeps for it, and each
/// flow's declaration of it, whose kind and query a step searches it with.
/// </summary>
public sealed record CacheStepType(string TypeName, string EntityType, IReadOnlyList<ReferenceFieldSpec> Fields, IReadOnlyList<CacheTypeDeclaration> Declarations)
{
    /// <summary>True when the type's records are OSDU records, which a step reads from OSDU; false for a lookup table.</summary>
    public bool IsOsdu => Declarations.All(d => !d.IsLookup);

    /// <summary>
    /// True when some flow declaring the type reads its records whole from storage (<see cref="CacheDeclaration.FetchesRecords"/>):
    /// a step searching it reads every record it finds back from storage too, or it would drop the values only storage holds.
    /// </summary>
    public bool FetchRecords => IsOsdu && Declarations.Any(d => d.FetchRecords);

    /// <summary>
    /// True for a lookup table its cache flow reads from an ingestion table whose connection reference and name the catalog
    /// holds, which a before step can read the way that flow does.
    /// </summary>
    public bool IsTable => Declarations is [{ Origin: CacheOrigin.Table, Connection: not null, SourceObject: not null }];

    /// <summary>True when a record of <paramref name="kind"/> is one the type holds: some declaration's kind pattern matches it.</summary>
    public bool Holds(string kind)
        => IsOsdu && Declarations.Any(d => d.Kind is { } pattern && Engine.Dimensions.KindPatterns.Matches(pattern, kind));
}

/// <summary>
/// What one partition's cache holds as every synced cache flow declares it (osdu/docs/reference/concepts/partition-cache.md,
/// Several cache flows, one cache). Several flows may declare the same type: the cache holds one type under that name, the
/// union of what they declare. Every declared path is kept for every record whichever flow captured it, so a value one
/// project asks for is there for every pipeline reading the partition, and a type's changes wait for approval when any flow
/// declaring it asks for that.
/// </summary>
public sealed class CacheDeclaration
{
    private readonly Dictionary<string, List<CacheTypeDeclaration>> _byType;

    public CacheDeclaration(string scope, IEnumerable<CacheTypeDeclaration> declarations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(declarations);
        Scope = scope;
        _byType = declarations
            .OrderBy(d => d.FlowName, StringComparer.Ordinal)
            .GroupBy(d => d.TypeName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A partition no synced cache flow declares anything for.</summary>
    public static CacheDeclaration None(string scope) => new(scope, []);

    public string Scope { get; }

    public bool IsEmpty => _byType.Count == 0;

    /// <summary>Every type some flow declares for the partition.</summary>
    public IReadOnlyCollection<string> TypeNames => _byType.Keys;

    /// <summary>Every flow's declaration of one type, in flow name order.</summary>
    public IReadOnlyList<CacheTypeDeclaration> Of(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return _byType.TryGetValue(typeName, out var declared) ? declared : [];
    }

    /// <summary>The paths the cache keeps for a type: every name any flow declares, the first flow in name order giving its path.</summary>
    public IReadOnlyList<ReferenceFieldSpec> FieldsOf(string typeName)
    {
        var fields = new List<ReferenceFieldSpec>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in Of(typeName))
        {
            fields.AddRange(declaration.Fields.Where(field => names.Add(field.Name)));
        }

        return fields;
    }

    /// <summary>A type of the partition's cache as a cache step works on it, or null when no synced flow declares it.</summary>
    public CacheStepType? Step(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return _byType.TryGetValue(typeName, out var declared)
            ? new CacheStepType(declared[0].TypeName, declared[0].EntityType, FieldsOf(typeName), declared)
            : null;
    }

    /// <summary>Every type of the partition's cache as a cache step works on it, in name order.</summary>
    public IReadOnlyList<CacheStepType> Steps()
        => [.. _byType.Keys.Order(StringComparer.OrdinalIgnoreCase).Select(name => Step(name)!)];

    /// <summary>The partition's retention: the longest its synced cache flows declare, or the default when none is synced.</summary>
    public int RetentionDays => CacheRetention.OfPartition(_byType.Values.SelectMany(declared => declared).Select(d => d.RetentionDays));

    /// <summary>
    /// The retention a refresh or an import by <paramref name="flowName"/> applies: the longest of <paramref name="declared"/>,
    /// what the flow's document says now, and what the partition's other synced cache flows declare.
    /// </summary>
    public int RetentionFor(string flowName, int declared)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        return CacheRetention.For(
            declared,
            _byType.Values.SelectMany(d => d).Where(d => !string.Equals(d.FlowName, flowName, StringComparison.Ordinal)).Select(d => d.RetentionDays));
    }

    /// <summary>What a change to a type does: approval when any flow declaring it asks for that; <paramref name="undeclared"/> when none declares it.</summary>
    public CacheChangeMode ModeOf(string typeName, CacheChangeMode undeclared)
    {
        var declared = Of(typeName);
        if (declared.Count == 0)
        {
            return undeclared;
        }

        return declared.Any(d => d.OnChange == CacheChangeMode.Approve) ? CacheChangeMode.Approve : CacheChangeMode.Auto;
    }

    /// <summary>
    /// Whether a type's records are read whole from storage: when any flow declaring it says so (<c>fetchRecords</c>). A captured
    /// record replaces the cached one whole, so a flow that read only the index would drop the values another flow reads from
    /// storage for the same record; every capture of the type reads it the same way.
    /// </summary>
    public bool FetchesRecords(string typeName) => Of(typeName).Any(d => !d.IsLookup && d.FetchRecords);

    /// <summary>
    /// What makes <paramref name="type"/>, as <paramref name="flowName"/> declares it, disagree with another flow's declaration
    /// of the same type in the partition: a different entity type, or a name cached from a different path. Merged, either
    /// would hold two meanings under one name. A lookup table, from a table or a dictionary, is declared by one flow only:
    /// its capture replaces the whole type, so a second flow declaring it would replace the first one's rows with its own.
    /// </summary>
    public IReadOnlyList<string> Conflicts(string flowName, ReferenceTypeSpec type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(type);
        var problems = new List<string>();
        foreach (var other in Of(type.Name).Where(d => !string.Equals(d.FlowName, flowName, StringComparison.Ordinal)))
        {
            if (type.IsLookup || other.IsLookup)
            {
                problems.Add(
                    $"cache flow '{other.FlowName}' declares {type.Name} ({Describe(other)}) and '{flowName}' declares it too ({type.Describe()}); a lookup table from a table or a dictionary is declared by one cache flow of a partition only");
                continue;
            }

            if (!string.Equals(other.EntityType, type.EntityType, StringComparison.Ordinal))
            {
                problems.Add($"cache flow '{other.FlowName}' declares {type.Name} as {other.EntityType}, and '{flowName}' declares it as {type.EntityType}");
            }

            foreach (var field in type.Fields)
            {
                if (other.Fields.FirstOrDefault(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase)) is { } theirs && !SameDefinition(theirs, field))
                {
                    problems.Add($"{type.Name}.{field.Name} is cached from {theirs.Path} by cache flow '{other.FlowName}' and from {field.Path} by '{flowName}'");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Refuses a flow whose types disagree with another synced flow of the partition about what a name holds. Merged, the
    /// cache would hold two meanings under one name, and whichever flow wrote last would decide which one every pipeline
    /// reads. A refresh and an import check the same way before they write anything.
    /// </summary>
    /// <exception cref="DeliveryException">A type of <paramref name="types"/> conflicts with another flow's declaration.</exception>
    public void ThrowOnConflicts(string flowName, IEnumerable<ReferenceTypeSpec> types)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(types);
        var problems = types.SelectMany(type => Conflicts(flowName, type)).ToList();
        if (problems.Count > 0)
        {
            throw new DeliveryException(
                $"Cache flow '{flowName}' disagrees with another cache flow of partition '{Scope}' about what the cache holds, so nothing was captured or imported: {string.Join("; ", problems)}. Make the declarations agree, or give one of the types another name.");
        }
    }

    /// <summary>
    /// A flow's type widened to every path the partition keeps for it, so its capture fills them all, and read whole from storage
    /// when any flow of the partition reads it so (<see cref="FetchesRecords"/>). A lookup table is declared by one flow only,
    /// so it keeps what that flow declares.
    /// </summary>
    public ReferenceTypeSpec Widen(ReferenceTypeSpec type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsLookup)
        {
            return type;
        }

        var fields = type.Fields.ToList();
        var names = fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        fields.AddRange(FieldsOf(type.Name).Where(field => names.Add(field.Name)));
        return type with { Fields = fields, FetchRecords = type.FetchRecords || FetchesRecords(type.Name) };
    }

    /// <summary>
    /// Whether two declarations of one name read it alike: two paths of the record that name one place, or two attributes read
    /// with the same steps and every setting the same; a path and an attribute never.
    /// </summary>
    private static bool SameDefinition(ReferenceFieldSpec theirs, ReferenceFieldSpec ours)
        => theirs.IsAttribute == ours.IsAttribute && (theirs.IsAttribute
            ? string.Equals(theirs.Path, ours.Path, StringComparison.Ordinal)
            : string.Equals(ReferenceField.Normalize(theirs.Path), ReferenceField.Normalize(ours.Path), StringComparison.OrdinalIgnoreCase));

    private static string Describe(CacheTypeDeclaration declaration) => declaration.Origin switch
    {
        CacheOrigin.Table => "a table",
        CacheOrigin.Dictionary => "a dictionary",
        CacheOrigin.Dimension => "a dimension",
        _ => $"kind {declaration.Kind}",
    };
}

/// <summary>A cached record of a type, as the membership of the partition's cache names it.</summary>
public readonly record struct CacheMemberKey(string TypeName, string RecordId)
{
    /// <summary>Type names compare as mappings read them (ignoring case); record ids exactly, as OSDU holds them.</summary>
    public static IEqualityComparer<CacheMemberKey> Comparer { get; } = new KeyComparer();

    private sealed class KeyComparer : IEqualityComparer<CacheMemberKey>
    {
        public bool Equals(CacheMemberKey x, CacheMemberKey y)
            => string.Equals(x.TypeName, y.TypeName, StringComparison.OrdinalIgnoreCase) && string.Equals(x.RecordId, y.RecordId, StringComparison.Ordinal);

        public int GetHashCode(CacheMemberKey obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TypeName), StringComparer.Ordinal.GetHashCode(obj.RecordId));
    }
}

/// <summary>The partition's cache after one flow's capture is merged in, and the types it no longer holds.</summary>
public sealed record CacheMergePlan(ReferenceSnapshot Snapshot, IReadOnlyList<string> RemovedTypes);

/// <summary>
/// How one cache flow's capture enters its partition's cache. A captured record replaces what the cache held for it, so the
/// newest capture of a record is what every pipeline reads, whichever flow made it. A record the cache holds that the capture
/// did not find leaves only when no other flow's last capture still holds it, because another project's query may be what
/// keeps it, and no delivery holds it. Types the capture does not cover are untouched, except a type no synced flow declares
/// any more. A capture that adds (what a delivery landed) replaces and adds records and takes none away.
/// </summary>
public static class CacheMerge
{
    private static readonly IReadOnlySet<string> NoFlows = new HashSet<string>(StringComparer.Ordinal);

    /// <param name="current">The partition's current version, or null when the cache holds none.</param>
    /// <param name="members">For every record of the captured types, the flows whose last capture held it.</param>
    /// <param name="flowName">The cache flow whose capture is merged.</param>
    /// <param name="captured">The captured types, each holding every record the flow's query matched.</param>
    /// <param name="declaredTypes">The types synced flows declare for the partition; empty when none are synced, which removes no type.</param>
    /// <param name="version">The label of the version the merge would write.</param>
    /// <param name="capturedUtc">When the capture was made.</param>
    /// <param name="readings">
    /// What the platform's services said about the partition's system properties during the capture
    /// (<see cref="SystemProperties.Merge"/>); none for an import from files, which keeps the current ones.
    /// </param>
    /// <param name="adds">Whether the capture adds what it holds and takes nothing away but <paramref name="removedRecords"/> (<see cref="CacheCapture.Adds"/>).</param>
    /// <param name="removedRecords">By type name, records OSDU no longer holds, which leave the type whoever held them (<see cref="CacheCapture.Removed"/>).</param>
    public static CacheMergePlan Apply(
        ReferenceSnapshot? current,
        IReadOnlyDictionary<CacheMemberKey, IReadOnlySet<string>> members,
        string flowName,
        IReadOnlyList<ReferenceType> captured,
        IReadOnlyCollection<string> declaredTypes,
        string version,
        DateTimeOffset capturedUtc,
        IReadOnlyList<SystemPropertyReading>? readings = null,
        bool adds = false,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? removedRecords = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        ArgumentNullException.ThrowIfNull(captured);
        ArgumentNullException.ThrowIfNull(declaredTypes);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var types = new Dictionary<string, ReferenceType>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in current?.Types ?? [])
        {
            types[type.Name] = type;
        }

        foreach (var capture in captured)
        {
            var existing = types.GetValueOrDefault(capture.Name);
            if (existing is not null && !string.Equals(existing.EntityType, capture.EntityType, StringComparison.Ordinal))
            {
                throw new DeliveryException(
                    $"The cache holds {existing.Name} as {existing.EntityType}, and cache flow '{flowName}' captured it as {capture.EntityType}. One name cannot hold both, so nothing was written; give one of the types another name.");
            }

            // A lookup table is declared by one flow only and its capture holds every row its table or dictionary holds, so
            // the capture is the type: a row it no longer holds is gone, and no other flow keeps one.
            if (capture.IsLookup)
            {
                if (adds)
                {
                    throw new DeliveryException(
                        $"'{flowName}' added rows to lookup table {capture.Name}; only OSDU records are added to a cache, a lookup table is captured whole by its cache flow.");
                }

                types[capture.Name] = new ReferenceType(existing?.Name ?? capture.Name, capture.EntityType, capture.Items, capture.Key);
                continue;
            }

            var items = new Dictionary<string, ReferenceItem>(StringComparer.Ordinal);
            foreach (var item in existing?.Items ?? [])
            {
                items[item.Id] = item;
            }

            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in capture.Items)
            {
                items[item.Id] = item;
                found.Add(item.Id);
            }

            foreach (var id in adds ? [] : items.Keys.Where(id => !found.Contains(id)).ToList())
            {
                var holders = members.TryGetValue(new CacheMemberKey(capture.Name, id), out var flows) ? flows : NoFlows;
                if (!holders.Any(flow => !string.Equals(flow, flowName, StringComparison.Ordinal)))
                {
                    items.Remove(id);
                }
            }

            // What the run that changed it found OSDU no longer holds leaves the type, whoever held it.
            if (removedRecords is not null && removedRecords.TryGetValue(capture.Name, out var gone))
            {
                foreach (var id in gone.Where(id => !found.Contains(id)))
                {
                    items.Remove(id);
                }
            }

            types[capture.Name] = new ReferenceType(existing?.Name ?? capture.Name, capture.EntityType, items.Values, capture.Key);
        }

        var removed = new List<string>();
        if (declaredTypes.Count > 0)
        {
            var declared = new HashSet<string>(declaredTypes, StringComparer.OrdinalIgnoreCase);
            declared.UnionWith(captured.Select(t => t.Name));
            foreach (var name in types.Keys.Where(name => !declared.Contains(name)).ToList())
            {
                removed.Add(types[name].Name);
                types.Remove(name);
            }
        }

        var properties = SystemProperties.Merge(current?.SystemProperties ?? [], readings ?? []);
        return new CacheMergePlan(new ReferenceSnapshot(version, capturedUtc, types.Values, properties).Normalized(), removed);
    }
}

/// <summary>The label of a cache version: the capture instant, sortable; the store appends the sequence when two captures share a second.</summary>
public static class CacheVersionLabel
{
    public static string Mint(DateTimeOffset capturedUtc)
        => capturedUtc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}
