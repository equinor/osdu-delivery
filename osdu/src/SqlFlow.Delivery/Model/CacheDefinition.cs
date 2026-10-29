using System.Text.Json.Serialization;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// A cache flow (<c>flowType: cache</c>, osdu/docs/reference/flow/cache.md): reference and master data the mappings resolve
/// against, declared in the repository and captured into the cache of the flow's partition in the catalog. The document is
/// the one place what the flow caches is defined: the OSDU platform and partition to search, the types to cache and, for
/// each type, the paths of a record to keep. A refresh run captures every declared type in full and merges it into the
/// partition's cache, which writes a new version when the cached content moved. The partition has one cache, filled by
/// every cache flow that searches it and read by every delivery flow that delivers to it.
/// </summary>
public sealed record CacheDefinition
{
    public const string FlowTypeName = "cache";

    /// <summary>Path of the file the flow was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The flow's name: its pipeline identity, and the name its versions and its hold on cached records are recorded under.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to, from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>
    /// The flow's operation code (<c>telemetry.operation</c>), which its runs' correlation id says what ran with
    /// (osdu/docs/reference/concepts/request-correlation.md); the kind's own when the flow names none.
    /// </summary>
    public string TelemetryOperation { get; init; } = FlowTelemetry.Cache;

    /// <summary>Stable id derived from <see cref="Name"/>, the same way every flow's is.</summary>
    public Guid Id => Identity.FlowId.Of(Name);

    /// <summary>Parameters a type's query may use as <c>{name}</c> tokens.</summary>
    public IReadOnlyDictionary<string, FlowParameter> Parameters { get; init; } = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);

    /// <summary>The OSDU platform the types are searched on.</summary>
    public required CacheSource Source { get; init; }

    /// <summary>
    /// The partitions the flow builds a cache for (<c>partitions</c>, osdu/docs/reference/flow/cache.md, Which partitions
    /// a flow fills), in document order; empty for a flow that names none and fills the cache of the partition its
    /// <c>source.headers</c> name.
    /// </summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>
    /// The partition this definition is bound to (<see cref="ForPartition"/>): the one a refresh builds the cache of. Null for
    /// a flow that names no partitions, and for one that names some before it is bound.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>True when the flow names the partitions it builds a cache for.</summary>
    public bool DeclaresPartitions => Partitions.Count > 0;

    /// <summary>
    /// True for a cache flow that names neither its partitions nor a <c>data-partition-id</c> header: it builds a cache for
    /// every partition registered with the catalog, and a refresh builds the one it targets, the registry's default when it
    /// names none.
    /// </summary>
    public bool FollowsRegistry { get; init; }

    /// <summary>True when the flow builds caches for partitions, named or registered.</summary>
    public bool Partitioned => DeclaresPartitions || FollowsRegistry;

    /// <summary>
    /// Whether settling the partitions a refresh builds (<paramref name="requested"/>, or none) needs the registry: always for
    /// a flow that follows it, and for one that names several partitions when none is named, to take the default among them.
    /// A hard-coded partition is settled from the document alone.
    /// </summary>
    public bool NeedsRegistry(string? requested)
        => FollowsRegistry || (DeclaresPartitions && string.IsNullOrWhiteSpace(requested) && Partitions.Count > 1);

    /// <summary>
    /// The partitions the flow builds a cache for: those it names, or for a flow that follows the registry, every registered
    /// one; none for a flow whose partition is its header's.
    /// </summary>
    public IReadOnlyList<string> Served(RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return DeclaresPartitions ? Partitions : FollowsRegistry ? registry.Names : [];
    }

    /// <summary>
    /// The partition whose cache the flow fills: the partition it is bound to, or for a flow that names none, the
    /// <c>data-partition-id</c> its searches carry, as the document writes it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The flow names its partitions and this definition is bound to none.</exception>
    /// <remarks>
    /// Derived from the partition a definition is bound to, so it is no part of the document as written: the definition the
    /// catalog keeps as JSON leaves it out, as it does for a flow bound to no partition yet.
    /// </remarks>
    [JsonIgnore]
    public string Scope => Partition
        ?? (Partitioned
            ? throw new InvalidOperationException(
                (DeclaresPartitions
                    ? $"Cache flow '{Name}' builds a cache for each of {PartitionNames.Listed(Partitions)}; bind it to the partition a refresh builds before asking for its scope."
                    : $"Cache flow '{Name}' builds a cache for every registered partition; bind it to the partition a refresh builds before asking for its scope."))
            : CacheScope.Of(Source.Headers, SourcePath ?? Name));

    /// <summary>
    /// The types the flow captures into its partition's cache; a cache flow declares at least one. A definition bound to a
    /// partition holds only the types built for it.
    /// </summary>
    public required IReadOnlyList<ReferenceTypeSpec> Types { get; init; }

    /// <summary>
    /// This definition bound to <paramref name="partition"/>, one of the partitions the flow names: its searches carry the
    /// partition as <c>data-partition-id</c>, and it holds only the types built for that partition.
    /// </summary>
    /// <exception cref="DeliveryException">The flow names no partitions, or not this one.</exception>
    public CacheDefinition ForPartition(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var wanted = partition.Trim();
        if (!Partitioned)
        {
            throw new DeliveryException(
                $"Cache flow '{Name}' names no partitions; it fills the cache of the partition its source.headers name, so it cannot be bound to '{wanted}'.");
        }

        var declared = DeclaresPartitions
            ? Partitions.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeliveryException($"Cache flow '{Name}' builds no cache for partition '{wanted}'; it names {PartitionNames.Listed(Partitions)}.")
            : CacheScope.IsPartitionId(wanted)
                ? wanted
                : throw new DeliveryException($"Cache flow '{Name}' cannot build a cache for '{wanted}': a partition is a data-partition-id (letters, digits, underscore, hyphen and dot).");
        var headers = new Dictionary<string, string>(Source.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [CacheScope.PartitionHeader] = declared,
        };
        return this with
        {
            Partition = declared,
            Source = Source with { Headers = headers },
            Types = Types.Where(t => t.IsBuiltFor(declared)).ToList(),
        };
    }

    /// <summary>
    /// The flow as a run refreshes it (osdu/docs/reference/flow/cache.md, Which partitions a flow fills). A flow hard-codes
    /// its partitions (<c>partitions</c>) or leaves them to the registry, and serves those it names or every registered one. A
    /// run builds the partition it names, which the flow has to serve (a partition a flow leaves to the registry has to be
    /// registered); every partition the flow serves, one after another, when it names every partition
    /// (<see cref="PartitionNames.Every"/>); and when it names none, the registry's default when the flow serves it, else the
    /// only partition the flow names. A flow whose partition is its header's is refreshed as it is. Each partition's cache is
    /// merged on its own.
    /// </summary>
    /// <exception cref="DeliveryException">
    /// A partition is named for a flow whose partition is its header's, the one named is not the flow's (or not registered,
    /// for a flow that follows the registry), or none is named and neither the default nor a single partition settles it.
    /// </exception>
    public IReadOnlyList<CacheDefinition> ForRun(string? requested, RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var wanted = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (!Partitioned)
        {
            return wanted is null
                ? [this]
                : throw new DeliveryException(
                    $"Cache flow '{Name}' names no partitions; it fills the cache of the partition its source.headers name, so a run cannot target '{wanted}'. Leave the partition out, or take the header out so the flow builds a cache for every registered partition.");
        }

        var served = Served(registry);
        if (wanted == PartitionNames.Every)
        {
            return served.Count > 0
                ? served.Select(ForPartition).ToList()
                : throw new DeliveryException($"No partition is registered with the catalog, so cache flow '{Name}' has none to build. Name one in the control plane's configuration (Osdu:Partitions:<name>), or register one with 'sqlflow partition add <name>'.");
        }

        if (wanted is not null)
        {
            return [ForPartition(FollowsRegistry ? registry.Find(wanted) ?? throw new DeliveryException(registry.NotRegistered(wanted)) : wanted)];
        }

        if (registry.Default is { } fallback && served.Contains(fallback, StringComparer.OrdinalIgnoreCase))
        {
            return [ForPartition(fallback)];
        }

        if (DeclaresPartitions && Partitions.Count == 1)
        {
            return [ForPartition(Partitions[0])];
        }

        // A flow that names its partitions needs no registration; only a registry-driven one has nothing to build without it.
        throw new DeliveryException(FollowsRegistry && registry.All.Count == 0
            ? $"No partition is registered with the catalog, so cache flow '{Name}' has none to build. Name one in the control plane's configuration (Osdu:Partitions:<name>), or register one with 'sqlflow partition add <name>'."
            : $"Cache flow '{Name}' builds {PartitionNames.Listed(served)}, and {(registry.Default is null ? "no partition is the default" : $"the default partition '{registry.Default}' is not one of them")}; name the partition this refresh builds, or '{PartitionNames.Every}' for every one.");
    }

    /// <summary>The default <c>onChange</c> for the types that do not state one.</summary>
    public CacheChangeMode OnChange { get; init; } = CacheChangeMode.Auto;

    /// <summary>
    /// How many days the partition's cache keeps the records of a version after a newer one replaced it
    /// (<c>retentionDays</c>, <see cref="CacheRetention.DefaultDays"/> when the document declares none). The partition keeps
    /// the longest any of its cache flows asks for (<see cref="CacheRetention"/>).
    /// </summary>
    public int RetentionDays { get; init; } = CacheRetention.DefaultDays;

    public FlowReliability Reliability { get; init; } = new();

    /// <summary>The secret references the source declares, keyed by document path; references only, never values.</summary>
    public IEnumerable<KeyValuePair<string, string>> CredentialReferences()
    {
        if (Source.Auth.SecretRef is { } secret)
        {
            yield return new("source.auth.secretRef", secret);
        }

        if (Source.Auth.SecondarySecretRef is { } secondary)
        {
            yield return new("source.auth.secondarySecretRef", secondary);
        }

        if (Source.Endpoint is { } endpoint && IsReference(endpoint))
        {
            yield return new("source.endpoint", endpoint);
        }

        if (Source.Connection is { } connection && IsReference(connection))
        {
            yield return new("source.connection", connection);
        }

        foreach (var (name, value) in Source.Headers.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            yield return new($"source.headers.{name}", value);
        }

        if (Source.Auth.Token is { } token)
        {
            foreach (var (name, value) in token.Body.Where(kv => IsReference(kv.Value)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                yield return new($"source.auth.token.body.{name}", value);
            }
        }
    }

    private static bool IsReference(string value) => value.Contains("${", StringComparison.Ordinal);
}

/// <summary>
/// Where a cache flow's types come from: the OSDU platform its OSDU types are searched on and how to authenticate there, and
/// the database its table types are read from. The headers name the partition whose cache the flow fills, whatever the
/// origins of its types.
/// </summary>
public sealed record CacheSource
{
    /// <summary>The platform base URL, declared when the flow has an OSDU type; ${env:NAME} and ${keyvault:NAME} references allowed.</summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// The database the flow's table types are read from, declared exactly as a delivery flow's <c>source.connection</c> is:
    /// a whole secret reference, or a SQL Server connection string whose secrets are references.
    /// </summary>
    public string? Connection { get; init; }

    public TargetAuth Auth { get; init; } = new() { Type = TargetAuthType.None };

    /// <summary>The headers every search carries, the data partition among them.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How a read of every record a search matches pages through them (<c>source.paging</c>): through the search cursor, the
    /// default, or in createTime order through the plain search, which holds nothing on the service between pages.
    /// </summary>
    public SearchPaging Paging { get; init; } = SearchPaging.Cursor;
}
