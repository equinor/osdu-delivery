using System.Security.Cryptography;
using System.Text;

namespace SqlFlow.Delivery.Data;

// The partition caches (osdu/docs/reference/concepts/partition-cache.md): the types each cache flow declares, the
// versions of a partition's cache, the cached records each version holds, and which flow's capture held each.

/// <summary>
/// A cached OSDU type as one cache flow (<c>flowType: cache</c>) declares it: which records it captures into its partition's
/// cache and which paths of each it keeps. The cache flow's YAML in the repository is the only place what is cached is
/// defined; the sync copies the declaration here, so a refresh knows every path the partition keeps for a type and the GUI
/// can show which files fill a cache. Several flows may declare the same type for one partition: the cache holds their union.
/// </summary>
public sealed class DeliveryCacheDefinition
{
    /// <summary>Stable id: derived from the repo id, the cache flow name and the cached type name.</summary>
    public Guid Id { get; set; }

    public Guid RepoId { get; set; }

    /// <summary>The cache flow that declares the type.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>
    /// The partition whose cache the flow fills: one of the partitions it names (<c>partitions</c>), or for a flow that names
    /// none, its <c>source.headers.data-partition-id</c> resolved.
    /// </summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// True when the flow works in partitions (osdu/docs/reference/flow/cache.md, Which partitions a flow fills): it names
    /// the partitions it builds a cache for, or leaves them to the registry. It then has a row per type and partition it
    /// serves, and a refresh of it names the partition it builds. False for a flow whose partition is its header's.
    /// </summary>
    public bool DeclaresPartitions { get; set; }

    /// <summary>
    /// Where the type's records come from: <c>osdu</c> (searched on the platform), <c>table</c> (an ingestion table) or
    /// <c>dictionary</c> (a dictionary document in the repository). Rows written before origins existed are osdu.
    /// </summary>
    public string Origin { get; set; } = "osdu";

    /// <summary>For an OSDU type: the endpoint the flow searches, as declared (a reference, never a resolved value).</summary>
    public string? Endpoint { get; set; }

    /// <summary>For a table type: the flow's source connection, as declared (a reference, never a resolved value).</summary>
    public string? Connection { get; set; }

    /// <summary>For a table type: the three-part name of the ingestion table read.</summary>
    public string? SourceObject { get; set; }

    /// <summary>For a table or dictionary type: the name each row's key is kept under.</summary>
    public string? KeyField { get; set; }

    /// <summary>For a dictionary type: the dictionary document's file, relative to the repository root.</summary>
    public string? DictionaryPath { get; set; }

    /// <summary>The cache flow's file, relative to the repository root.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>The short name mappings use (UnitOfMeasure).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The OSDU entity type (reference-data--UnitOfMeasure), or lookup--&lt;Name&gt; for a table or dictionary type.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>For an OSDU type: the search kind the capture sweeps.</summary>
    public string? Kind { get; set; }

    /// <summary>For an OSDU type: the search query narrowing the capture.</summary>
    public string? Query { get; set; }

    /// <summary>
    /// For an OSDU type: set when the flow reads each record its search finds whole from the storage service
    /// (<c>types[].fetchRecords</c>), so a path the search index does not hold is kept. Every capture of the type in the
    /// partition reads it so when any flow declaring it does.
    /// </summary>
    public bool FetchRecords { get; set; }

    /// <summary>
    /// For a table type: the most rows its capture takes, as the flow declares it (<c>types[].maxRows</c>); null when it
    /// declares none, which takes 1,000,000, and for every other origin. A table holding more refuses the capture.
    /// </summary>
    public int? MaxRows { get; set; }

    /// <summary>
    /// What the cache keeps of each record as JSON: <c>[{ "path": "data.Code", "as": "Code" }]</c>, the path a column for a
    /// table type, and for a dictionary type the fields its document names.
    /// </summary>
    public string FieldsJson { get; set; } = "[]";

    /// <summary>approve or auto: what a changed cached value of this type does to the records already built from it.</summary>
    public string OnChange { get; set; } = "auto";

    /// <summary>
    /// How many days the partition's cache keeps the records of a version after a newer one replaced it, as the flow declares
    /// it (<c>retentionDays</c>, 7 when it declares none). The partition keeps the longest any of its cache flows asks for.
    /// </summary>
    public int RetentionDays { get; set; } = 7;

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// One version of a partition's cache: the whole cache as one merge left it, written by the cache flow whose capture moved
/// it. A capture that changes what the cache holds writes the next version, which becomes current, and one that changes
/// nothing writes none, because a new version moves the render context of every record built against the cache. A version's
/// content never changes once written. Its records are kept while it is current, while it is the version the current one
/// replaced, while a delivery flow pins it, and for the partition's retention after a newer version replaced it; then a
/// refresh prunes them (<see cref="PrunedUtc"/>). The row itself is kept for as long as the ledger exists: a delivered
/// record's render context names the version, and the row says who captured it, when, from where and what it changed.
/// </summary>
public sealed class DeliveryCacheVersion
{
    /// <summary>Stable id: derived from the partition and the version label.</summary>
    public Guid Id { get; set; }

    /// <summary>The partition whose cache the version belongs to.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cache flow whose capture, or import, wrote the version.</summary>
    public string FlowName { get; set; } = string.Empty;

    /// <summary>The label, minted from the capture instant (20260910T165153Z), with the sequence appended when two captures share a second.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The version's place in the partition's history, 1 for the first; the ranges of <see cref="DeliveryCacheItem"/> count in it.</summary>
    public int Sequence { get; set; }

    public DateTime CapturedUtc { get; set; }

    /// <summary>Hash of the whole content, checked on every load so a version altered after it was written is refused.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>The version that was current when this one was written, which the capture was merged onto; null for the first.</summary>
    public string? PreviousVersion { get; set; }

    /// <summary>Whether this is the newest version, the one deliveries render against unless a flow pins another.</summary>
    public bool Current { get; set; }

    /// <summary>The platform run that captured the version; null for one imported from files.</summary>
    public Guid? RunId { get; set; }

    /// <summary>
    /// Who asked for it: the capturing run's requester as SQLFlow records it (the token's subject for a run a person queued,
    /// schedule:&lt;name&gt; for one a schedule fired, cli:&lt;user&gt;@&lt;machine&gt; for a direct CLI run), or
    /// cli:&lt;user&gt;@&lt;machine&gt; for an import from the command line.
    /// </summary>
    public string CapturedBy { get; set; } = string.Empty;

    /// <summary>Where the content came from: the OSDU endpoint reference a capture searched, or the directory an import read.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>The types the version holds, by name, each with its entity type and record count: <c>[{ "name", "entityType", "items" }]</c>.</summary>
    public string TypesJson { get; set; } = "[]";

    /// <summary>
    /// The partition's system properties the capture found, kept apart from the cached records because they describe the
    /// platform rather than any record: <c>[{ "service", "name", "state", "source", "detail" }]</c>. Empty for a version
    /// written before captures recorded them, or by an import onto a cache no capture had asked about.
    /// </summary>
    public string SystemPropertiesJson { get; set; } = "[]";

    /// <summary>How many cached records the version holds across its types.</summary>
    public long Items { get; set; }

    /// <summary>
    /// When the cache's retention removed the version's records; null while they are kept. A pruned version can no longer be
    /// read, compared or rendered against, and its row still describes it.
    /// </summary>
    public DateTime? PrunedUtc { get; set; }

    /// <summary>
    /// Who pruned the version's records: the requester of the refresh that applied the retention (a person, or
    /// schedule:&lt;name&gt;), the account of the import, or the operator who purged the cache's history. Null while they are
    /// kept, and for a version pruned before this was recorded.
    /// </summary>
    public string? PrunedBy { get; set; }

    /// <summary>
    /// What the version changed against the one before it, per type, as counted from its records before a retention removed
    /// any of them: <c>[{ "type", "changed", "added", "removed" }]</c>, listing the types that moved. Null while the history
    /// can still count them from the records.
    /// </summary>
    public string? ChangesJson { get; set; }
}

/// <summary>
/// One cached record as a run of consecutive versions of its partition's cache held it: the OSDU id and the values captured
/// at the declared paths, valid from the version at <see cref="FromSequence"/> up to, and not including, the one at
/// <see cref="ToSequence"/>, which is null while the newest version still holds it unchanged. A record is stored once per
/// partition however many cache flows capture it, and a merge writes rows only for the records that changed, arrived or
/// left, so keeping every version costs rows in proportion to what moved rather than to the size of the cache times the
/// number of captures.
/// </summary>
public sealed class DeliveryCacheItem
{
    public long ItemId { get; set; }

    /// <summary>The partition whose cache holds the record.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cached type's short name (UnitOfMeasure).</summary>
    public string TypeName { get; set; } = string.Empty;

    public string EntityType { get; set; } = string.Empty;

    /// <summary>The OSDU record id, without a version.</summary>
    public string RecordId { get; set; } = string.Empty;

    /// <summary>The captured values as JSON, in whatever shape the paths yielded, names in ordinal order.</summary>
    public string FieldsJson { get; set; } = "{}";

    /// <summary>
    /// The SHA-256 of <see cref="FieldsJson"/>, which the database computes (<see cref="FieldsHashSql"/>) and keeps in the
    /// index of the open ranges, so a merge tells a record that changed from one that did not by the hash alone, without
    /// reading the values back.
    /// </summary>
    public byte[] FieldsHash { get; set; } = [];

    /// <summary>Every scalar the item holds, newline separated: what a search over cached values matches on.</summary>
    public string Terms { get; set; } = string.Empty;

    /// <summary>The sequence of the first version that holds the record with these values.</summary>
    public int FromSequence { get; set; }

    /// <summary>The sequence of the first version that no longer holds it so; null while the newest version still does.</summary>
    public int? ToSequence { get; set; }

    /// <summary>
    /// How the database computes <see cref="FieldsHash"/>: SHA-256 over the UTF-16 bytes an <c>nvarchar</c> holds, which
    /// is what <see cref="HashOf"/> hashes. Not persisted: the hash is kept in the index of the open ranges, which is where
    /// the merge reads it.
    /// </summary>
    public const string FieldsHashSql = "CAST(HASHBYTES('SHA2_256', [FieldsJson]) AS binary(32))";

    /// <summary>The <see cref="FieldsHash"/> the database computes for <paramref name="fieldsJson"/>.</summary>
    public static byte[] HashOf(string fieldsJson)
    {
        ArgumentNullException.ThrowIfNull(fieldsJson);
        return SHA256.HashData(Encoding.Unicode.GetBytes(fieldsJson));
    }
}

/// <summary>
/// That a cache flow's last capture of a type held a record. It is what lets several flows share one partition's cache: a
/// record a flow no longer finds leaves the cache only when no other flow's capture still holds it. Current state, not
/// history; what each version held is in <see cref="DeliveryCacheItem"/>.
/// </summary>
public sealed class DeliveryCacheMember
{
    /// <summary>The partition whose cache holds the record.</summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>The cached type's short name (UnitOfMeasure).</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>The OSDU record id, without a version.</summary>
    public string RecordId { get; set; } = string.Empty;

    /// <summary>The cache flow whose last capture of the type held the record.</summary>
    public string FlowName { get; set; } = string.Empty;
}
