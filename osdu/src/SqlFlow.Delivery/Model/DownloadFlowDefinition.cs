using System.Text.Json.Serialization;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// A download flow (<c>flowType: download</c>, osdu/docs/reference/flow/download.md): every record one or more OSDU kinds
/// hold in a partition, written to a folder as a JSON file of its own, named after its id, holding the record as storage
/// keeps it. Its ids are listed as an inventory lists them (<see cref="InventorySource"/>), and its ledger keeps, per id, the
/// version its file holds, so a run writes only what is new or changed, a newer version replaces the file, and an id OSDU
/// stops serving is marked gone and its file left in place. A record's metadata is downloaded, never the data a dataset
/// links to. Nothing is written to OSDU.
/// </summary>
public sealed record DownloadFlowDefinition
{
    public const string FlowTypeName = "download";

    /// <summary>The most downloads one flow declares.</summary>
    public const int MaxDownloads = 100;

    /// <summary>Path of the file the flow was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The flow's name: its pipeline identity, and the name its downloads are kept under.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to, from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>
    /// The flow's operation code (<c>telemetry.operation</c>), which its runs' correlation id says what ran with
    /// (osdu/docs/reference/concepts/request-correlation.md); the kind's own when the flow names none.
    /// </summary>
    public string TelemetryOperation { get; init; } = FlowTelemetry.Download;

    /// <summary>Parameters the downloads' queries and the target location may use as <c>{name}</c> tokens.</summary>
    public IReadOnlyDictionary<string, FlowParameter> Parameters { get; init; } = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);

    /// <summary>The OSDU platform the records are read from, and how its ids are listed: the keys an inventory flow's source takes to read.</summary>
    public required InventorySource Source { get; init; }

    /// <summary>Where the files go.</summary>
    public required DownloadTarget Target { get; init; }

    /// <summary>
    /// The partitions the flow downloads from (<c>partitions</c>), in document order; empty for a flow that names none, which
    /// downloads from the partition its <c>source.headers</c> name or from every registered one.
    /// </summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>
    /// The partition this definition is bound to (<see cref="ForPartition"/>): the one a run reads. Null for a flow whose
    /// partition is its header's, and for one that works in partitions before it is bound.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>True when the flow names the partitions it downloads from.</summary>
    public bool DeclaresPartitions => Partitions.Count > 0;

    /// <summary>True for a flow that names neither its partitions nor a <c>data-partition-id</c> header: it downloads from every registered partition.</summary>
    public bool FollowsRegistry { get; init; }

    /// <summary>True when the flow works in partitions, named or registered, rather than the one its header names.</summary>
    public bool Partitioned => DeclaresPartitions || FollowsRegistry;

    /// <summary>The downloads, in the order the document declares them; names are unique within the flow.</summary>
    public required IReadOnlyList<DownloadSpec> Downloads { get; init; }

    public FlowReliability Reliability { get; init; } = new();

    /// <summary>How many storage reads a run has in flight at once (<c>parallelism.recordsAtOnce</c>), each of up to a hundred records.</summary>
    public RunParallelism Parallelism { get; init; } = new();

    /// <summary>
    /// The ledger identity the flow's downloads are kept under: derived from the name and the bound partition for a flow that
    /// works in partitions, so each partition keeps downloads of its own, and from the name alone for a flow whose partition is
    /// its header's.
    /// </summary>
    /// <exception cref="InvalidOperationException">The flow works in partitions and is bound to none.</exception>
    [JsonIgnore]
    public Guid LedgerId => Partitioned
        ? Identity.FlowId.Of(Name, BoundPartition())
        : Identity.FlowId.Of(Name);

    /// <summary>The ledger's name as pages show it: <c>name@partition</c> for a flow that works in partitions, the name otherwise.</summary>
    [JsonIgnore]
    public string LedgerName => Partitioned ? $"{Name}@{BoundPartition()}" : Name;

    /// <summary>
    /// Whether settling the partition a run reads (<paramref name="requested"/>, or none) needs the registry: always for a
    /// flow that follows it, and for one that names several partitions when none is named.
    /// </summary>
    public bool NeedsRegistry(string? requested)
        => FollowsRegistry || (DeclaresPartitions && string.IsNullOrWhiteSpace(requested) && Partitions.Count > 1);

    /// <summary>The partitions the flow downloads from: those it names, every registered one for a flow that follows the registry, none for a header flow.</summary>
    public IReadOnlyList<string> Served(RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return DeclaresPartitions ? Partitions : FollowsRegistry ? registry.Names : [];
    }

    /// <summary>
    /// This definition bound to <paramref name="partition"/>: its requests carry the partition as <c>data-partition-id</c>, its
    /// <c>{partition}</c> token is the partition, and its downloads are kept under the partition's ledger.
    /// </summary>
    /// <exception cref="DeliveryException">The flow's partition is its header's, or the flow does not download from this partition.</exception>
    public DownloadFlowDefinition ForPartition(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var wanted = partition.Trim();
        if (!Partitioned)
        {
            throw new DeliveryException(
                $"Download flow '{Name}' names no partitions; it reads the partition its source.headers name, so it cannot be bound to '{wanted}'.");
        }

        var declared = DeclaresPartitions
            ? Partitions.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeliveryException($"Download flow '{Name}' does not read partition '{wanted}'; it names {PartitionNames.Listed(Partitions)}.")
            : CacheScope.IsPartitionId(wanted)
                ? wanted
                : throw new DeliveryException($"Download flow '{Name}' cannot read '{wanted}': a partition is a data-partition-id (letters, digits, underscore, hyphen and dot).");
        var headers = new Dictionary<string, string>(Source.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [CacheScope.PartitionHeader] = declared,
        };
        return this with { Partition = declared, Source = Source with { Headers = headers } };
    }

    /// <summary>
    /// The flow as a run reads it: the partition the run names, which the flow has to serve; when it names none, the registry's
    /// default when the flow serves it, else the flow's only partition. A flow whose partition is its header's is read as it
    /// is. A run reads one partition: <see cref="PartitionNames.Every"/> is refused, since a download's ledger belongs to one
    /// partition.
    /// </summary>
    /// <exception cref="DeliveryException">The partition cannot be settled, or is not one the flow reads.</exception>
    public DownloadFlowDefinition ForRun(string? requested, RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var wanted = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (!Partitioned)
        {
            return wanted is null
                ? this
                : throw new DeliveryException(
                    $"Download flow '{Name}' names no partitions; it reads the partition its source.headers name, so a run cannot target '{wanted}'. Leave the partition out, or take the header out so the flow reads every registered partition.");
        }

        if (wanted == PartitionNames.Every)
        {
            throw new DeliveryException(
                $"Download flow '{Name}' reads one partition per run, since its ledger of what each file holds belongs to one partition; name the partition to read instead of '{PartitionNames.Every}'.");
        }

        if (wanted is not null)
        {
            return ForPartition(FollowsRegistry ? registry.Find(wanted) ?? throw new DeliveryException(registry.NotRegistered(wanted)) : wanted);
        }

        var served = Served(registry);
        if (registry.Default is { } fallback && served.Contains(fallback, StringComparer.OrdinalIgnoreCase))
        {
            return ForPartition(fallback);
        }

        if (DeclaresPartitions && Partitions.Count == 1)
        {
            return ForPartition(Partitions[0]);
        }

        throw new DeliveryException(FollowsRegistry && registry.All.Count == 0
            ? $"No partition is registered with the catalog, so download flow '{Name}' has none to read. Name one in the control plane's configuration (Osdu:Partitions:<name>), or register one with 'sqlflow partition add <name>'."
            : $"Download flow '{Name}' reads {PartitionNames.Listed(served)}, and {(registry.Default is null ? "no partition is the default" : $"the default partition '{registry.Default}' is not one of them")}; name the partition this run reads.");
    }

    /// <summary>The download named <paramref name="name"/> (case-insensitively), or null.</summary>
    public DownloadSpec? Download(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Downloads.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The downloads a run takes: those <paramref name="names"/> names, in document order; every download when it names none.</summary>
    /// <exception cref="DeliveryException">A name is not a download of the flow.</exception>
    public IReadOnlyList<DownloadSpec> Select(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return Downloads;
        }

        var unknown = names.Where(n => Download(n) is null).ToList();
        if (unknown.Count > 0)
        {
            var known = Downloads.Select(d => d.Name).ToList();
            throw new DeliveryException(
                $"Download flow '{Name}' has no download named {string.Join(", ", unknown.Select(n => $"'{n}'"))}; its downloads are "
                + (known.Count <= 20 ? string.Join(", ", known) : string.Join(", ", known.Take(20)) + $" and {known.Count - 20} more") + ".");
        }

        return Downloads.Where(d => names.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

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

        if (IsReference(Source.Endpoint))
        {
            yield return new("source.endpoint", Source.Endpoint);
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

    private string BoundPartition() => Partition
        ?? throw new InvalidOperationException(
            DeclaresPartitions
                ? $"Download flow '{Name}' reads each of {PartitionNames.Listed(Partitions)}; bind it to the partition a run reads before asking for its ledger."
                : $"Download flow '{Name}' reads every registered partition; bind it to the partition a run reads before asking for its ledger.");

    private static bool IsReference(string value) => value.Contains("${", StringComparison.Ordinal);
}

/// <summary>Where a download flow writes its files (<c>target</c>).</summary>
public sealed record DownloadTarget
{
    /// <summary>
    /// The folder each download's folder is made in: an absolute local path, or an <c>abfss://</c> or
    /// <c>https://&lt;account&gt;.blob.core.windows.net</c> URI, with <c>{partition}</c> and the flow's parameters as tokens.
    /// </summary>
    public required string Location { get; init; }
}

/// <summary>One download: every record one kind (wildcards allowed) holds, narrowed by a query when the read is a search.</summary>
public sealed record DownloadSpec
{
    /// <summary>The download's name, unique in its flow ignoring case: the folder its files are written to.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The kind read, <c>authority:source:entityType:version</c>, each segment a value or a pattern with <c>*</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>A Lucene query narrowing the records a search reads; null for every record of the kind. A storage read takes none.</summary>
    public string? Query { get; init; }

    /// <summary>The download as the inventory's readers list it: its kind and query, the latest version of each record.</summary>
    public InventorySpec AsInventory() => new() { Name = Name, Description = Description, Kind = Kind, Query = Query, Versions = InventoryVersions.Latest };
}
