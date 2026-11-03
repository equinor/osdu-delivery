namespace SqlFlow.Delivery.Snapshots;

/// <summary>Who and what wrote a cache version, kept on the version so a cached value can be traced to the capture that produced it.</summary>
/// <param name="RunId">The platform run that captured the version; null for an import from files.</param>
/// <param name="CapturedBy">Who asked: the run's requester (a person, or schedule:&lt;name&gt;), or cli:&lt;user&gt;@&lt;machine&gt; for an import from the CLI.</param>
/// <param name="Origin">Where the content came from: the OSDU endpoint reference searched, or the directory imported.</param>
public sealed record CacheCapture(Guid? RunId, string CapturedBy, string Origin)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> NoneGone =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the capture adds the records it holds and takes none away but <see cref="Removed"/>: the records a pipeline's
    /// run changed (osdu/docs/reference/concepts/partition-cache.md, Pipelines bring the cache up to date), not everything a
    /// cache flow's query matched. The capture's holder keeps every record it held before beside the ones it adds.
    /// </summary>
    public bool Adds { get; init; }

    /// <summary>
    /// By type name, records OSDU's storage service no longer holds, known for certain because the run that changed them read
    /// them back: they leave the type whichever flow or delivery held them.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> Removed { get; init; } = NoneGone;

    /// <summary>
    /// By type name, records OSDU's storage service no longer holds, which a cache flow's capture did not find and only
    /// deliveries held: they lose the deliveries' hold, so the merge takes them out unless another cache flow still holds them.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> Gone { get; init; } = NoneGone;
}

/// <summary>
/// Who holds a cached record besides the cache flows: a delivery that landed it, named after the ledger that delivered it
/// (osdu/docs/reference/concepts/partition-cache.md, Pipelines bring the cache up to date).
/// </summary>
public static class CacheHolders
{
    /// <summary>What every delivery's holder name starts with; no cache flow's name has a colon.</summary>
    public const string DeliveryPrefix = "delivery:";

    /// <summary>The longest holder name the cache's membership keeps.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// The holder a delivery holds its landed records under: <c>delivery:</c> and the ledger's name, cut and suffixed with a
    /// hash of the whole name when that would be longer than the membership keeps, so two long names stay two holders.
    /// </summary>
    public static string Delivery(string ledgerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ledgerName);
        var holder = DeliveryPrefix + ledgerName;
        if (holder.Length <= MaxLength)
        {
            return holder;
        }

        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ledgerName)))[..12];
        return $"{holder[..(MaxLength - 13)]}~{hash}";
    }

    /// <summary>Whether <paramref name="holder"/> is a delivery's rather than a cache flow's.</summary>
    public static bool IsDelivery(string holder) => holder.StartsWith(DeliveryPrefix, StringComparison.Ordinal);
}

/// <summary>
/// One type a cache version holds, how many records of it, and for a lookup table the name its key is kept under; with the
/// type's own content hash, how it compares with the version before, and the version its content dates from. A version is
/// the whole partition's cache, so it moves whenever any type or the partition's system properties do; a type's hash and
/// <paramref name="Since"/> move only when that type does.
/// </summary>
/// <param name="Name">The name mappings read the type by.</param>
/// <param name="EntityType">The OSDU entity type, or <c>lookup--&lt;Name&gt;</c> for a lookup table.</param>
/// <param name="Items">How many records of the type the version holds.</param>
/// <param name="Key">For a lookup table, the name its key is kept under; null for a type of OSDU records.</param>
/// <param name="Hash">The type's content hash (<see cref="ReferenceType.ContentHash"/>); null for a version written before types were hashed.</param>
/// <param name="Change">How the type compares with the version before; null for a version written before types were hashed.</param>
/// <param name="Since">
/// The version that last added or changed the type, whose content this version holds of it unchanged; this version itself
/// when it added or changed the type. Null when a version written before types were hashed cannot say.
/// </param>
public sealed record CacheVersionType(
    string Name, string EntityType, long Items, string? Key = null, string? Hash = null, CacheTypeChange? Change = null, string? Since = null);

/// <summary>
/// A version of a partition's cache as its row describes it, without its records: <c>Scope</c> is the partition whose cache
/// the version belongs to, and <c>FlowName</c> the cache flow whose capture or import wrote it. <c>SystemProperties</c> are
/// the partition's own settings the capture found, kept apart from the types. <c>PrunedUtc</c> is when the cache's retention
/// pruned the version's records (<see cref="CacheRetention"/>), null while they are kept, and <c>PrunedBy</c> who: the
/// requester of the refresh, the account of the import, or the operator who purged; null for a version pruned before that
/// was recorded.
/// </summary>
public sealed record CacheVersionInfo(
    string Scope,
    string Version,
    int Sequence,
    DateTime CapturedUtc,
    bool Current,
    string? PreviousVersion,
    Guid? RunId,
    string CapturedBy,
    string Origin,
    string FlowName,
    long Items,
    IReadOnlyList<CacheVersionType> Types,
    IReadOnlyList<SystemProperty> SystemProperties,
    DateTime? PrunedUtc = null,
    string? PrunedBy = null)
{
    /// <summary>True when the cache's retention pruned the version's records: it can no longer be read or compared.</summary>
    public bool Pruned => PrunedUtc is not null;
}

/// <summary>What merging a capture into a partition's cache did.</summary>
/// <param name="Snapshot">The version written, or the current version when the merge changed no cached content.</param>
/// <param name="Previous">The version that was current before the merge; null for the partition's first version.</param>
/// <param name="Written">False when the merge changed no cached content, so no version was written.</param>
/// <param name="Changes">
/// What the merge did to each type, by its content hash: which arrived, which changed and which the version holds exactly as
/// <paramref name="Previous"/> did, and which it no longer holds. Every type is unchanged when no version was written. A
/// version is written when anything moved, so this is what tells a type that moved from one that only rode along.
/// </param>
public sealed record CacheWrite(ReferenceSnapshot Snapshot, ReferenceSnapshot? Previous, bool Written, CacheTypeChanges Changes)
{
    /// <summary>What the partition's retention pruned once the merge was written; null until it is applied.</summary>
    public CacheRetentionOutcome? Retention { get; init; }
}

/// <summary>
/// Where every cache lives (osdu/docs/reference/concepts/partition-cache.md): the catalog, one cache per OSDU data
/// partition. Every cache flow that searches a partition merges its captures into that partition's cache, and every
/// delivery flow that delivers to the partition renders against it. Versions form one line per partition, the newest always
/// current. A version's content never changes once written; the partition's retention prunes the records of the versions it
/// no longer needs, and keeps their rows.
/// </summary>
public interface ICacheStore
{
    /// <summary>The newest version of the partition's cache, or null when it holds none.</summary>
    Task<string?> CurrentVersionAsync(string scope, CancellationToken ct = default);

    /// <summary>The current version of every partition's cache, in partition order, as each row describes it, without reading a record.</summary>
    Task<IReadOnlyList<CacheVersionInfo>> CurrentVersionsAsync(CancellationToken ct = default);

    /// <summary>
    /// One version of the partition's cache with every record it holds, or null when there is no such version. Callers that
    /// ask for a version together share one read of it, and a version read once is kept in memory for the next.
    /// </summary>
    /// <exception cref="CacheVersionPrunedException">The partition's retention pruned the version's records.</exception>
    Task<ReferenceSnapshot?> LoadAsync(string scope, string version, CancellationToken ct = default);

    /// <summary>Every version of the partition's cache, newest first.</summary>
    Task<IReadOnlyList<CacheVersionInfo>> ListVersionsAsync(string scope, CancellationToken ct = default);

    /// <summary>
    /// One version of the partition's cache as its row describes it, without reading a record: <paramref name="version"/>,
    /// or the current one when it is null. Null when there is no such version.
    /// </summary>
    Task<CacheVersionInfo?> VersionAsync(string scope, string? version, CancellationToken ct = default);

    /// <summary>What the synced cache flows declare the partition's cache holds; empty when no flow for it is synced.</summary>
    Task<CacheDeclaration> DeclarationAsync(string scope, CancellationToken ct = default);

    /// <summary>
    /// Merges one cache flow's capture into the partition's cache (<see cref="CacheMerge"/>) and writes the result as the
    /// next version, which becomes current, unless the cached content did not change. Either way the flow's membership of
    /// the captured types becomes what it captured. <paramref name="readings"/> are what the platform's services said about
    /// the partition's system properties during the capture; none keeps the current ones, as an import does. Fails without
    /// writing anything when another write of the partition wins.
    /// </summary>
    Task<CacheWrite> MergeAsync(
        string scope,
        string flowName,
        IReadOnlyList<ReferenceType> captured,
        CacheCapture capture,
        DateTimeOffset capturedUtc,
        IReadOnlyList<SystemPropertyReading>? readings = null,
        CancellationToken ct = default);

    /// <summary>
    /// Which of <paramref name="ids"/>, records of type <paramref name="typeName"/> of the partition's cache, a delivery holds
    /// (<see cref="CacheHolders"/>): the ones a cache flow's capture leaves in the cache though it did not find them.
    /// </summary>
    Task<IReadOnlySet<string>> HeldByDeliveriesAsync(string scope, string typeName, IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>
    /// Applies a retention to the partition's cache (<see cref="CacheRetention"/>): the one a refresh or an import of a cache
    /// flow applies once its merge is written, the longest of what the flow declares and what the partition's other synced
    /// cache flows declare, or an operator's purge, which keeps exactly the days it names. Every version whose records it
    /// prunes has its change counts recorded first, so the cache's history reads the same before and after, and records when
    /// it was pruned and by whom. A dry run says what it would prune and changes nothing. Safe to run beside a merge and
    /// beside another retention of the same partition, and repeatable: a pass cut short is finished by the next.
    /// </summary>
    Task<CacheRetentionOutcome> ApplyRetentionAsync(CacheRetentionRequest request, CancellationToken ct = default);

    /// <summary>
    /// What one statement of a finding's proof reads of a version of the partition's cache (osdu/docs/reference/concepts/value-proof.md):
    /// the statement a person copies, its values bound as parameters, at most <see cref="CacheProofQuery.Limit"/> rows of it.
    /// A version that is gone, or whose records the retention pruned, is refused, so an empty answer is always one about
    /// records that can be read.
    /// </summary>
    Task<Source.ProofRows> ReadProofAsync(CacheProofQuery query, CancellationToken ct = default);
}
