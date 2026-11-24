using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// A version of a record's ingestion row as its attempts recorded it: its origin, when the ingestion table marked the row
/// deleted (for the hold of a deleted row), and when the ledger first recorded it.
/// </summary>
public sealed record RecordOriginSeen(RecordOrigin Origin, DateTime? DeletedUtc, DateTime FirstRecordedUtc);

/// <summary>What is uploaded and what is not, per flow: the numbers an operator looks at first.</summary>
public sealed record FlowStats
{
    public long Total { get; init; }

    public long Pending { get; init; }

    public long Delivering { get; init; }

    public long Delivered { get; init; }

    public long Held { get; init; }

    public long Failed { get; init; }

    public long Deleted { get; init; }

    /// <summary>Records a reversal put back to the version OSDU held before the run it reversed, blocked until released.</summary>
    public long Reverted { get; init; }

    /// <summary>Records waiting for a record they refer to that has not landed.</summary>
    public long Waiting { get; init; }

    /// <summary>
    /// Records blocked (held, failed, reverted, or removed by a reversal) until their row moves or someone releases them; a
    /// released record waiting for its run is not.
    /// </summary>
    public long Blocked { get; init; }

    /// <summary>
    /// Held or failed records nothing will move: not blocked, not asked to be planned again, no document to send. What a
    /// release answered by a plan that left the record parked used to leave; a release reaches them as it reaches the blocked.
    /// </summary>
    public long Stranded { get; init; }

    /// <summary>
    /// Held records an adoption may be asked of: held because OSDU already holds a record at the id each one's key makes,
    /// claiming none (<see cref="OccupiedHolds.Adoptable"/>).
    /// </summary>
    public long Adoptable { get; init; }

    /// <summary>Delivered records whose last verify found drift or a missing record.</summary>
    public long Drifted { get; init; }

    /// <summary>Records whose last render set a step of <c>enrich:</c> aside on its hold, the record going ahead without it.</summary>
    public long StepsHeld { get; init; }

    /// <summary>
    /// Records whose access in OSDU is restricted, or asked to be (osdu/docs/reference/concepts/access-restriction.md), whatever
    /// their status: no run delivers them until their restriction is lifted.
    /// </summary>
    public long Restricted { get; init; }

    /// <summary>
    /// Records OSDU holds restricted until complete (osdu/docs/reference/flow/mapping-restricted-access.md): delivered under a
    /// registered restriction's groups because values their mapping marks <c>$restrictWhenMissing</c> were missing, whatever
    /// their status. Each goes back to its own access once a delivery finds every such value; counted apart from
    /// <see cref="Restricted"/>, which an operator asked for and which holds the record off.
    /// </summary>
    public long RestrictedUntilComplete { get; init; }

    /// <summary>
    /// Records whose access in OSDU is restricted either way, by an operator or until complete, each record once however many
    /// apply: the flow's Restricted figure. Every one of them is counted under its status too, so this is never added to
    /// <see cref="Total"/>.
    /// </summary>
    public long AccessRestricted { get; init; }

    public long DeliveredLast24h { get; init; }

    public DateTime? LastDeliveredUtc { get; init; }

    public DateTime? LastVerifiedUtc { get; init; }

    public long Submissions { get; init; }

    public SubmissionState? LastSubmission { get; init; }
}

/// <summary>
/// The filters the lookup across flows and its recency listing take besides a term, a status, a flow and a partition: the
/// ones a flow's listing has too (<see cref="RecordQuery.Drifted"/>, <see cref="RecordQuery.StepHeld"/>), read the same way
/// over every flow of the partitions in view.
/// </summary>
/// <param name="Drifted">Only the records whose last verify found drift or a missing record.</param>
/// <param name="StepHeld">Only the records whose last render set a step of <c>enrich:</c> aside on its hold.</param>
/// <param name="Restricted">Only the records whose access in OSDU is restricted, or asked to be.</param>
/// <param name="Incomplete">Only the records OSDU holds restricted until complete (osdu/docs/reference/flow/mapping-restricted-access.md).</param>
/// <param name="Missing">Only the records restricted until complete for one set of missing values (<see cref="RecordState.IncompleteHash"/>).</param>
/// <param name="AccessRestricted">
/// Only the records whose access in OSDU is restricted either way: by an operator (<paramref name="Restricted"/>) or until
/// complete (<paramref name="Incomplete"/>), each record once.
/// </param>
public readonly record struct LookupNarrowing(
    bool Drifted = false, bool StepHeld = false, bool Restricted = false, bool Incomplete = false, long? Missing = null, bool AccessRestricted = false);

/// <summary>
/// One problem keeping records of a ledger blocked (<see cref="ProblemSignature"/>): what it says, and how many records it
/// keeps blocked, counted from the records.
/// </summary>
public sealed record ProblemGroup
{
    public required long Problem { get; init; }

    /// <summary>The error with every part that names a record replaced: what the problem's records have in common.</summary>
    public required string Pattern { get; init; }

    /// <summary>The records it keeps blocked: the held and the failed together.</summary>
    public required long Records { get; init; }

    /// <summary>Of those, the ones held: a data problem, or a refusal that is not retried.</summary>
    public long Held { get; init; }

    /// <summary>Of those, the ones failed: the retries ran out.</summary>
    public long Failed { get; init; }

    /// <summary>When the record of the problem that changed longest ago last changed.</summary>
    public DateTime OldestUtc { get; init; }

    /// <summary>When its most recently changed record last changed.</summary>
    public DateTime NewestUtc { get; init; }

    /// <summary>Its most recently changed record, with the error as that record carries it.</summary>
    public RecordState? Example { get; init; }

    /// <summary>
    /// Where the problem lies, as its newest and its oldest record say: a set error when they name the same values (or the
    /// pattern names none), row errors when they name different ones. A problem's samples tell it more surely.
    /// </summary>
    public ProblemShape Shape { get; init; }

    /// <summary>The values the example names (<see cref="ProblemSignature.Values"/>): for a set error, the values every record names.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>
    /// The error as the problem's records looked at say it (<see cref="ProblemSignature.Shared"/>): the pattern with what
    /// they all name written in, a placeholder where they differ. Read like the shape, from the newest and the oldest record.
    /// </summary>
    public required string Shared { get; init; }

    /// <summary>
    /// The problem as <paramref name="samples"/> spread across it tell it, which know more than its newest and oldest record:
    /// its shape and what its records share, read from the samples and the example. With no sample it is as it was.
    /// </summary>
    public ProblemGroup Sampled(IReadOnlyCollection<RecordState> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return this;
        }

        var errors = samples.Select(s => s.LastError).Append(Example?.LastError).ToList();
        return this with { Shape = ProblemSignature.ShapeOf(errors), Shared = ProblemSignature.Shared(Pattern, errors) };
    }
}

/// <summary>A ledger's problems, the largest first, with what the page leaves out.</summary>
/// <param name="Problems">The problems listed.</param>
/// <param name="TotalProblems">How many problems keep the ledger's records blocked, listed or not.</param>
/// <param name="TotalRecords">How many records they keep blocked.</param>
/// <param name="Unsorted">
/// Blocked records the ledger has not sorted into a problem yet: held or failed before it kept problems, which the
/// control plane's backfill sorts a page at a time.
/// </param>
public sealed record ProblemListing(IReadOnlyList<ProblemGroup> Problems, long TotalProblems, long TotalRecords, long Unsorted);

/// <summary>An ingestion file some records of one problem were left at, and how many; a null name for the records that name none.</summary>
public sealed record ProblemFile(string? FileName, long Records);

/// <summary>A count that stops at a limit: the number of matching records when <see cref="Exact"/>, otherwise a floor.</summary>
public readonly record struct BoundedCount(int Count, bool Exact);

/// <summary>How many records of one ledger went to one OSDU partition, as the partition their OSDU id names.</summary>
public sealed record PartitionRecords(string Partition, long Records);

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds what the pages, the CLI and the statistics read: records,
/// attempts, lookups and problems. A caller that uses only this part takes it rather than the whole ledger;
/// <see cref="OsduLedger"/> implements every part.
/// </summary>
public interface ILedgerQueries
{
    /// <summary>How many distinct records a submission's attempts settled with the given outcome (and phase, when given).</summary>
    Task<long> CountAttemptsAsync(Guid submissionId, AttemptOutcome outcome, string? phase = null, CancellationToken ct = default);

    Task<long> CountAsync(Guid flowId, Guid? submissionId, RecordStatus status, CancellationToken ct = default);

    /// <summary>Whether the ledger of <paramref name="flowId"/> holds any record: an index seek, however large the ledger.</summary>
    Task<bool> HoldsRecordsAsync(Guid flowId, CancellationToken ct = default);

    /// <summary>
    /// How many records of <paramref name="flowId"/> went to each OSDU partition, read from the partition their OSDU id names
    /// (the id delivered, or the one claimed before delivery), most first; a record with neither is not counted. It reads the
    /// flow's whole ledger, so it is asked when the ledgers of a flow that names its partitions are checked, never per record.
    /// </summary>
    Task<IReadOnlyList<PartitionRecords>> DeliveredPartitionsAsync(Guid flowId, CancellationToken ct = default);

    /// <summary>
    /// A page of the records a listing matches, most recently updated first and ties broken by key. It reads a bounded
    /// part of the ledger at any volume: the page starts inside the first <see cref="RecordListing.CountLimit"/> records,
    /// a prefix search orders at most that many candidates from each identity index, and a contains search is refused
    /// when the rest of the filter leaves more than <see cref="RecordListing.ContainsScanLimit"/> records. A listing past
    /// those bounds throws <see cref="RecordQueryTooBroadException"/>.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ListAsync(Guid flowId, RecordQuery query, CancellationToken ct = default);

    Task<FlowStats> StatsAsync(Guid flowId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>One of the flow's records' attempts, newest first: the record's history, and nothing of another flow's record with the same key.</summary>
    Task<IReadOnlyList<AttemptRecord>> ListAttemptsAsync(Guid flowId, DeliveryKey key, int max, CancellationToken ct = default);

    /// <summary>
    /// The versions of one of the flow's records' ingestion row its attempts recorded, newest first: each distinct origin
    /// (the stamp, file and row) an attempt was built from or recorded, and each moment the ingestion table marked the row
    /// deleted, with when the ledger first recorded it. Read over every attempt the record has, not a page of them; at
    /// most <paramref name="max"/>. The record's own delivered and queued origins are on the record.
    /// </summary>
    Task<IReadOnlyList<RecordOriginSeen>> ListOriginsAsync(Guid flowId, DeliveryKey key, int max, CancellationToken ct = default);

    /// <summary>
    /// One flow's records in delivery-key order after <paramref name="after"/>, at most <paramref name="max"/>: what a
    /// sync of every record pages through, one seek of the record's key a page.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ListRecordsAsync(Guid flowId, DeliveryKey? after, int max, CancellationToken ct = default);

    /// <summary>The newest attempt of each of the flow's records named; a record that has none is not in the answer.</summary>
    Task<IReadOnlyDictionary<DeliveryKey, AttemptRecord>> LatestAttemptsAsync(Guid flowId, IReadOnlyList<DeliveryKey> keys, CancellationToken ct = default);

    /// <summary>The attempts a submission produced, newest first: the submission view.</summary>
    Task<IReadOnlyList<AttemptRecord>> ListAttemptsForSubmissionAsync(Guid submissionId, int max, CancellationToken ct = default);

    /// <summary>
    /// How many records match a listing, counting no further than <paramref name="limit"/>: exact below it, a floor at
    /// it. A prefix search that ran into its candidate bound while another filter narrowed it is a floor too.
    /// </summary>
    Task<BoundedCount> CountAsync(Guid flowId, RecordQuery query, int limit, CancellationToken ct = default);

    /// <summary>Records matching a lookup across every flow: an exact delivery key (one record per flow that reads the
    /// row), or a prefix over the OSDU id, the source key, the label and the origin file name. At most
    /// <see cref="RecordListing.LookupCandidateLimit"/> candidates are read from each identity index, and the most recently
    /// updated of them are returned. With <paramref name="status"/>, only the candidates in that state. With
    /// <paramref name="flowId"/>, the records of that ledger identity alone, its candidates read from its own tokens. With
    /// <paramref name="partition"/>, the records of that partition alone (the workbench reads one partition at a time); a
    /// ledger identity names its own partition. <paramref name="narrowTo"/> keeps the candidates it names. With
    /// <see cref="SearchMode.Contains"/>, a term that is not a delivery key matches anywhere in the source key, the label,
    /// the OSDU id or the origin file name; it has no index, so it runs only when the rest of the lookup leaves at most
    /// <see cref="RecordListing.ContainsScanLimit"/> records, and throws <see cref="RecordQueryTooBroadException"/>
    /// otherwise.</summary>
    Task<IReadOnlyList<RecordState>> LookupAsync(
        string term, int max, RecordStatus? status = null, Guid? flowId = null, string? partition = null, SearchMode mode = SearchMode.Prefix,
        LookupNarrowing narrowTo = default, CancellationToken ct = default);

    /// <summary>How many records a lookup matches, counting no further than <paramref name="limit"/>.</summary>
    Task<BoundedCount> CountLookupAsync(
        string term, int limit, RecordStatus? status = null, Guid? flowId = null, string? partition = null, SearchMode mode = SearchMode.Prefix,
        LookupNarrowing narrowTo = default, CancellationToken ct = default);

    /// <summary>The ledger's most recently updated records across every flow, at most <paramref name="max"/> of them,
    /// newest first and ties broken by key: what the delivery system last took in, sent or was answered about, without a
    /// term to seek. With <paramref name="status"/>, only the records in that state; with <paramref name="flowId"/>, only
    /// the records of that ledger identity; with <paramref name="narrowTo"/>, only the records it names. It reads the end
    /// of a recency index, and it reaches no further back than <see cref="RecordListing.LookupCandidateLimit"/> records.
    /// With <paramref name="partition"/>, the records of that partition alone.</summary>
    Task<IReadOnlyList<RecordState>> ListRecentAsync(
        int max, RecordStatus? status = null, Guid? flowId = null, string? partition = null, LookupNarrowing narrowTo = default, CancellationToken ct = default);

    /// <summary>How many records the recency listing has to show, counting no further than <paramref name="limit"/>.</summary>
    Task<BoundedCount> CountRecentAsync(
        int limit, RecordStatus? status = null, Guid? flowId = null, string? partition = null, LookupNarrowing narrowTo = default, CancellationToken ct = default);

    /// <summary>The ledger identities among <paramref name="flowIds"/> that hold at least one record, in any state. It asks
    /// whether each holds one, never how many, so it costs one index seek per identity however many records the ledger
    /// holds.</summary>
    Task<IReadOnlySet<Guid>> FlowsWithRecordsAsync(IReadOnlyCollection<Guid> flowIds, CancellationToken ct = default);

    /// <summary>
    /// The problems keeping the flow's records blocked (<see cref="ProblemSignature"/>), the most records first and at most
    /// <paramref name="max"/> of them, each with its held and failed records, its files, when its records last changed and
    /// its most recently changed record as the example; with how many problems and records there are in all, and how many
    /// blocked records the ledger has not sorted into problems yet. Counted from the records, through the problem index alone.
    /// </summary>
    Task<ProblemListing> ListProblemsAsync(Guid flowId, int max, CancellationToken ct = default);

    /// <summary>One problem of the flow, counted as <see cref="ListProblemsAsync"/> counts it; null when no record has it.</summary>
    Task<ProblemGroup?> GetProblemAsync(Guid flowId, long problem, CancellationToken ct = default);

    /// <summary>
    /// The ingestion files the records one problem keeps blocked came from, the most records first and at most
    /// <paramref name="max"/>: the file of the version each record was left at.
    /// </summary>
    Task<IReadOnlyList<ProblemFile>> ListProblemFilesAsync(Guid flowId, long problem, int max, CancellationToken ct = default);

    /// <summary>
    /// Records of one problem spread evenly across it, at most <paramref name="count"/>: its newest, its oldest and the ones
    /// between at even steps of the problem index's order, so records loaded at different times, from different files,
    /// stand side by side. What an operator checks before releasing the problem's records together, and what tells a set
    /// error from row errors. Each sample is a seek of its own after one pass over the problem's range of the index.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ListProblemSamplesAsync(Guid flowId, long problem, int count, CancellationToken ct = default);
}
