using System.Text.Json.Serialization;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Model;

/// <summary>
/// An inventory flow (<c>flowType: inventory</c>, osdu/docs/reference/flow/inventory.md): every record id one or more OSDU
/// kinds hold in a partition, with its version and who created and last changed it, kept in the module's database and
/// compared with every ledger of the partition. The ledgers say what the flows delivered and minted; the inventory says what
/// OSDU serves, and where the two disagree is the report: records no ledger knows (orphans), records a ledger never
/// confirmed, removed or forgot, and records a ledger delivered that OSDU no longer serves. A build, a reconcile and a plan
/// only read OSDU; a flow that declares <see cref="Removal"/> may also remove what it found of the findings it names, when
/// an operator asks for it (osdu/docs/wiki/decisions/0014-inventory-removals.md).
/// </summary>
public sealed record InventoryFlowDefinition
{
    public const string FlowTypeName = "inventory";

    /// <summary>The most inventories one flow declares.</summary>
    public const int MaxInventories = 100;

    /// <summary>The most owners one flow names.</summary>
    public const int MaxOwners = 50;

    /// <summary>How many ids a ledger expects that one build reads from storage to tell missing from merely unlisted, unless the flow says otherwise.</summary>
    public const int DefaultMaxMissingChecks = 100_000;

    /// <summary>The most missing checks a flow may ask for in one build.</summary>
    public const int MaxMissingChecksCeiling = 1_000_000;

    /// <summary>Path of the file the flow was loaded from, for error messages. Null for inline documents.</summary>
    public string? SourcePath { get; init; }

    /// <summary>The flow's name: its pipeline identity, and the name its inventories are kept under.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The platform batch the flow belongs to, from the envelope.</summary>
    public string? Batch { get; init; }

    /// <summary>Parameters the inventories' queries may use as <c>{name}</c> tokens.</summary>
    public IReadOnlyDictionary<string, FlowParameter> Parameters { get; init; } = new Dictionary<string, FlowParameter>(StringComparer.Ordinal);

    /// <summary>The OSDU platform the inventories are read from, and how.</summary>
    public required InventorySource Source { get; init; }

    /// <summary>
    /// The partitions the flow keeps inventories of (<c>partitions</c>, osdu/docs/reference/flow/inventory.md, Top-level
    /// keys), in document order; empty for a flow that names none, which keeps them of the partition its
    /// <c>source.headers</c> name or of every registered one.
    /// </summary>
    public IReadOnlyList<string> Partitions { get; init; } = [];

    /// <summary>
    /// The partition this definition is bound to (<see cref="ForPartition"/>): the one a run reads. Null for a flow whose
    /// partition is its header's, and for one that works in partitions before it is bound.
    /// </summary>
    public string? Partition { get; init; }

    /// <summary>True when the flow names the partitions it keeps inventories of.</summary>
    public bool DeclaresPartitions => Partitions.Count > 0;

    /// <summary>True for a flow that names neither its partitions nor a <c>data-partition-id</c> header: it keeps inventories of every registered partition.</summary>
    public bool FollowsRegistry { get; init; }

    /// <summary>True when the flow works in partitions, named or registered, rather than the one its header names.</summary>
    public bool Partitioned => DeclaresPartitions || FollowsRegistry;

    /// <summary>
    /// The identities whose records are this estate's (<c>owners</c>): a record no ledger knows that one of them created is an
    /// orphan, any other is foreign. Empty when the flow names none, and a build infers them from the records a ledger claims.
    /// </summary>
    public IReadOnlyList<string> Owners { get; init; } = [];

    /// <summary>How many ids a ledger expects that one build reads from storage to tell missing from merely unlisted (<c>maxMissingChecks</c>).</summary>
    public int MaxMissingChecks { get; init; } = DefaultMaxMissingChecks;

    /// <summary>The inventories, in the order the document declares them; names are unique within the flow.</summary>
    public required IReadOnlyList<InventorySpec> Inventories { get; init; }

    /// <summary>
    /// What an operator may remove from OSDU of what the flow's inventories found (<c>removal</c>); null for a flow that declares
    /// none, which only ever reads OSDU.
    /// </summary>
    public InventoryRemovalPolicy? Removal { get; init; }

    public FlowReliability Reliability { get; init; } = new();

    /// <summary>How many version lists a build reads at once (<c>parallelism.recordsAtOnce</c>).</summary>
    public RunParallelism Parallelism { get; init; } = new();

    /// <summary>
    /// The ledger identity the flow's inventories are kept under: derived from the name and the bound partition for a flow
    /// that works in partitions, so each partition keeps inventories of its own, and from the name alone for a flow whose
    /// partition is its header's.
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

    /// <summary>The partitions the flow keeps inventories of: those it names, every registered one for a flow that follows the registry, none for a header flow.</summary>
    public IReadOnlyList<string> Served(RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return DeclaresPartitions ? Partitions : FollowsRegistry ? registry.Names : [];
    }

    /// <summary>
    /// This definition bound to <paramref name="partition"/>: its requests carry the partition as <c>data-partition-id</c>, its
    /// <c>{partition}</c> token is the partition, and its inventories are kept under the partition's ledger.
    /// </summary>
    /// <exception cref="DeliveryException">The flow's partition is its header's, or the flow does not keep inventories of this partition.</exception>
    public InventoryFlowDefinition ForPartition(string partition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var wanted = partition.Trim();
        if (!Partitioned)
        {
            throw new DeliveryException(
                $"Inventory flow '{Name}' names no partitions; it reads the partition its source.headers name, so it cannot be bound to '{wanted}'.");
        }

        var declared = DeclaresPartitions
            ? Partitions.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeliveryException($"Inventory flow '{Name}' does not read partition '{wanted}'; it names {PartitionNames.Listed(Partitions)}.")
            : CacheScope.IsPartitionId(wanted)
                ? wanted
                : throw new DeliveryException($"Inventory flow '{Name}' cannot read '{wanted}': a partition is a data-partition-id (letters, digits, underscore, hyphen and dot).");
        var headers = new Dictionary<string, string>(Source.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [CacheScope.PartitionHeader] = declared,
        };
        return this with { Partition = declared, Source = Source with { Headers = headers } };
    }

    /// <summary>
    /// The flow as a run reads it (osdu/docs/reference/flow/inventory.md, Top-level keys): the partition the run names, which
    /// the flow has to serve; when it names none, the registry's default when the flow serves it, else the flow's only
    /// partition. A flow whose partition is its header's is read as it is. A run reads one partition:
    /// <see cref="PartitionNames.Every"/> is refused, since an inventory and the ledgers it is compared with belong to one
    /// partition.
    /// </summary>
    /// <exception cref="DeliveryException">The partition cannot be settled, or is not one the flow reads.</exception>
    public InventoryFlowDefinition ForRun(string? requested, RegisteredPartitions registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var wanted = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (!Partitioned)
        {
            return wanted is null
                ? this
                : throw new DeliveryException(
                    $"Inventory flow '{Name}' names no partitions; it reads the partition its source.headers name, so a run cannot target '{wanted}'. Leave the partition out, or take the header out so the flow reads every registered partition.");
        }

        if (wanted == PartitionNames.Every)
        {
            throw new DeliveryException(
                $"Inventory flow '{Name}' reads one partition per run, since an inventory is compared with the ledgers of one partition; name the partition to read instead of '{PartitionNames.Every}'.");
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
            ? $"No partition is registered with the catalog, so inventory flow '{Name}' has none to read. Name one in the control plane's configuration (Osdu:Partitions:<name>), or register one with 'sqlflow partition add <name>'."
            : $"Inventory flow '{Name}' reads {PartitionNames.Listed(served)}, and {(registry.Default is null ? "no partition is the default" : $"the default partition '{registry.Default}' is not one of them")}; name the partition this run reads.");
    }

    /// <summary>The inventory named <paramref name="name"/> (case-insensitively), or null.</summary>
    public InventorySpec? Inventory(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Inventories.FirstOrDefault(i => string.Equals(i.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The inventories a run builds or reconciles: those <paramref name="names"/> names, in document order; every inventory when it names none.</summary>
    /// <exception cref="DeliveryException">A name is not an inventory of the flow.</exception>
    public IReadOnlyList<InventorySpec> Select(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return Inventories;
        }

        var unknown = names.Where(n => Inventory(n) is null).ToList();
        if (unknown.Count > 0)
        {
            var known = Inventories.Select(i => i.Name).ToList();
            throw new DeliveryException(
                $"Inventory flow '{Name}' has no inventory named {string.Join(", ", unknown.Select(n => $"'{n}'"))}; its inventories are "
                + (known.Count <= 20 ? string.Join(", ", known) : string.Join(", ", known.Take(20)) + $" and {known.Count - 20} more") + ".");
        }

        return Inventories.Where(i => names.Contains(i.Name, StringComparer.OrdinalIgnoreCase)).ToList();
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
                ? $"Inventory flow '{Name}' reads each of {PartitionNames.Listed(Partitions)}; bind it to the partition a run reads before asking for its ledger."
                : $"Inventory flow '{Name}' reads every registered partition; bind it to the partition a run reads before asking for its ledger.");

    private static bool IsReference(string value) => value.Contains("${", StringComparison.Ordinal);
}

/// <summary>How an inventory reads OSDU (osdu/docs/reference/flow/inventory.md, Reading OSDU).</summary>
public enum InventoryRead
{
    /// <summary>The search index, paged by a cursor: a viewer's entitlements suffice, and a record written seconds before, or not indexed, is not listed.</summary>
    Search,

    /// <summary>Storage itself: every active record of the kind, unindexed ones included; it needs the storage service's admin role.</summary>
    Storage,
}

/// <summary>Which versions of each record an inventory keeps.</summary>
public enum InventoryVersions
{
    /// <summary>The latest version alone, which every read returns.</summary>
    Latest,

    /// <summary>Every version storage keeps, read for a record that is new or whose latest version moved since the last build.</summary>
    All,
}

/// <summary>The OSDU platform an inventory flow reads, where each service it reads is served, and how it reads.</summary>
public sealed record InventorySource
{
    public const string DefaultQueryPath = "/api/search/v2/query";
    public const string DefaultSearchPath = "/api/search/v2/query_with_cursor";

    /// <summary>Storage's listing of a kind's ids (GET) and its read of records by id (POST), openapi storage v2 <c>/query/records</c>.</summary>
    public const string DefaultRecordQueryPath = "/api/storage/v2/query/records";

    /// <summary>Storage's read of up to 1,000 records' system properties by id, openapi storage v2 <c>/query/records/headers</c>.</summary>
    public const string DefaultHeadersPath = "/api/storage/v2/query/records/headers";

    /// <summary>Storage's list of a record's versions, openapi storage v2 <c>/records/versions/{id}</c>.</summary>
    public const string DefaultVersionsPath = "/api/storage/v2/records/versions";

    /// <summary>The schema service, which expands a kind with wildcards into the kinds storage lists one at a time.</summary>
    public const string DefaultSchemaPath = "/api/schema-service/v1/schema";

    /// <summary>Storage's soft delete of one record, openapi storage v2 <c>POST /records/{id}:delete</c>, which a removal falls back to one id at a time.</summary>
    public const string DefaultDeletePath = "/api/storage/v2/records/{id}:delete";

    /// <summary>Storage's soft delete of a list of records, openapi storage v2 <c>POST /records/delete</c>, which a removal sends 500 ids at a time.</summary>
    public const string DefaultBulkDeletePath = "/api/storage/v2/records/delete";

    /// <summary>Storage's purge of a record and every version of it, openapi storage v2 <c>DELETE /records/{id}</c>.</summary>
    public const string DefaultPurgePath = "/api/storage/v2/records/{id}";

    public required string Endpoint { get; init; }

    public TargetAuth Auth { get; init; } = new() { Type = TargetAuthType.None };

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How the inventories read OSDU: through search (the default) or storage.</summary>
    public InventoryRead Read { get; init; } = InventoryRead.Search;

    public string QueryPath { get; init; } = DefaultQueryPath;

    public string SearchPath { get; init; } = DefaultSearchPath;

    /// <summary>
    /// How a read of every record a search matches pages through them (<c>source.paging</c>): through the search cursor, the
    /// default, or in createTime order through the plain search, which holds nothing on the service between pages.
    /// </summary>
    public SearchPaging Paging { get; init; } = SearchPaging.Cursor;

    public string RecordQueryPath { get; init; } = DefaultRecordQueryPath;

    public string HeadersPath { get; init; } = DefaultHeadersPath;

    public string VersionsPath { get; init; } = DefaultVersionsPath;

    public string SchemaPath { get; init; } = DefaultSchemaPath;

    public string DeletePath { get; init; } = DefaultDeletePath;

    public string BulkDeletePath { get; init; } = DefaultBulkDeletePath;

    public string PurgePath { get; init; } = DefaultPurgePath;
}

/// <summary>
/// What an inventory flow lets an operator remove from OSDU (<c>removal</c>, osdu/docs/reference/flow/inventory.md, Removal):
/// the ids of the findings it names, through the flow's own source and credentials, soft deleted, or purged when it also allows
/// that. The power to delete is so a line of the flow's document, reviewed where the document is.
/// </summary>
public sealed record InventoryRemovalPolicy
{
    /// <summary>The findings whose ids may be removed: of orphan, stale and forgotten, at least one.</summary>
    public required IReadOnlyList<string> Findings { get; init; }

    /// <summary>Whether a removal may purge (<c>DELETE /records/{id}</c>, every version destroyed) rather than soft delete.</summary>
    public bool Purge { get; init; }

    /// <summary>Whether the ids of <paramref name="finding"/> may be removed.</summary>
    public bool Allows(string finding) => Findings.Contains(finding, StringComparer.Ordinal);
}

/// <summary>
/// What a removal run removes (the run payload's <c>removal</c>, with the inventory in <c>inventories</c> and the partition in
/// <c>confirm</c>): the ids of one finding, every one of them, those it names, or those a selection holds, how much of each it
/// takes, and how many the operator was shown, which the run holds the inventory to before it removes anything.
/// </summary>
public sealed record InventoryRemovalRequest
{
    /// <summary>The most ids one removal names; a larger removal takes every id of the finding.</summary>
    public const int MaxIds = 1000;

    /// <summary>The finding whose ids are removed.</summary>
    public required string Finding { get; init; }

    /// <summary>
    /// How much of each record is removed: <c>record</c> (a soft delete, reversible) or <c>everything</c> (a purge) for an id OSDU
    /// serves; <c>inventory</c> (the inventory's own row, nothing asked of OSDU) for a gone id, and only for one.
    /// </summary>
    public required string Scope { get; init; }

    /// <summary>How many ids the operator was shown: every id of the finding, or as many as <see cref="Ids"/> names.</summary>
    public required long Expected { get; init; }

    /// <summary>The ids removed, when the operator picked them; empty removes every id of the finding (or of <see cref="Selection"/>).</summary>
    public IReadOnlyList<string> Ids { get; init; } = [];

    /// <summary>
    /// The selection whose ids of the finding are removed (osdu/docs/reference/flow/inventory-selections.md, Selecting ids by what OSDU
    /// holds): those that held what the operator's conditions asked in OSDU. Null removes every id of the finding, or those
    /// <see cref="Ids"/> names; a removal names ids or a selection, not both.
    /// </summary>
    public long? Selection { get; init; }

    /// <summary>True when the removal names its ids rather than taking every id of the finding.</summary>
    public bool NamesIds => Ids.Count > 0;
}

/// <summary>
/// One inventory: every id one kind (wildcards allowed) holds, narrowed by a query when the read is a search, with the latest
/// version of each or every version.
/// </summary>
public sealed record InventorySpec
{
    /// <summary>The inventory's name, unique in its flow ignoring case.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The kind read, <c>authority:source:entityType:version</c>, each segment a value or <c>*</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>A Lucene query narrowing the records a search reads; null for every record of the kind. A storage read takes none.</summary>
    public string? Query { get; init; }

    public InventoryVersions Versions { get; init; } = InventoryVersions.Latest;

    /// <summary>The entity type the kind names (<c>work-product-component--WellLog</c>), or null when its entity type is a wildcard.</summary>
    [JsonIgnore]
    public string? EntityType => Kind.Split(':') is [_, _, var entity, _] && entity.Length > 0 && !entity.Contains('*', StringComparison.Ordinal) ? entity : null;

    /// <summary>
    /// True when the kind names every record of its entity type, whatever its authority, source and version (only those three
    /// segments are wildcards) and no query narrows it: the inventory then holds every record of the entity type OSDU serves,
    /// and a ledger's record of the type it does not hold may be missing.
    /// </summary>
    [JsonIgnore]
    public bool CoversEntityType => EntityType is not null && Query is null
        && Kind.Split(':') is [var authority, var source, _, var version] && authority == "*" && source == "*" && version == "*";
}
