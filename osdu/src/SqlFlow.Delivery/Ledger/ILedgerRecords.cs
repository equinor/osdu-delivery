using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Ledger;

/// <summary>A record given a new OSDU id: the id OSDU lost, and the one it is written under from now on.</summary>
public sealed record TargetIdReassignment(DeliveryKey Key, string Lost, string Assigned);

/// <summary>
/// A record an operator adopted: the OSDU id it claimed, which OSDU already held a record at, and the version OSDU held there
/// when the record was held for it (null when OSDU's answer named none).
/// </summary>
public sealed record RecordAdoption(DeliveryKey Key, string TargetId, long? Version);

/// <summary>What a sync found of one record's row that the ledger has to take in.</summary>
public sealed record SourceSyncFinding
{
    public required DeliveryKey DeliveryKey { get; init; }

    /// <summary>When the ingestion table first inserted the row, for a record whose ledger holds another moment or none.</summary>
    public DateTime? InsertedUtc { get; init; }

    /// <summary>The row changed, or was marked deleted, since the version the ledger stands at: the flow's next run plans it.</summary>
    public bool RequestPlan { get; init; }

    /// <summary>Why the row was not found (gone from the table, or out of the record's scope), for the attempt that says so.</summary>
    public string? NotFound { get; init; }

    /// <summary>
    /// The row is not in the table at all (not merely out of the record's scope): no run can plan the record, so a request to
    /// plan it again is answered, and a held or failed record a release left unblocked is blocked again until its row is back.
    /// </summary>
    public bool Gone { get; init; }

    /// <summary>The origin the ledger last recorded for the row, which the attempt of a row not found names.</summary>
    public RecordOrigin Origin { get; init; }
}

/// <summary>What the ledger wrote of a sync's findings: arrivals set, plans newly requested, rows reported not found.</summary>
public sealed record SourceSyncApplied(int Arrivals, int PlansRequested, int NotFound);

/// <summary>Why the intake left a record's delivered state as it is.</summary>
public enum SkipKind
{
    /// <summary>The source version, payload and render context equal what OSDU holds: decided without rendering.</summary>
    Unchanged,

    /// <summary>Rendered, and the document and payload hashes equal what OSDU holds (or what is already queued).</summary>
    Rendered,

    /// <summary>The source carries a version older than the one delivered or queued; OSDU keeps the newer one.</summary>
    Stale,

    /// <summary>
    /// Not decided: a cache change tagged against the values the record was built from waits for approval, and OSDU keeps
    /// the document it has. A request to plan the record again stays, for the plan that runs once the change is decided.
    /// </summary>
    AwaitingApproval,

    /// <summary>
    /// Not delivered: the source no longer holds the key, and the ledger keeps it out (a deletion pending or removed) while
    /// the row is not newer than the moment it was removed. A request to plan the record again is answered.
    /// </summary>
    SourceRemoved,
}

/// <summary>One record the intake skipped, with what it saw, so the ledger can advance or record it.</summary>
public sealed record SkippedRecord
{
    public required DeliveryKey DeliveryKey { get; init; }

    public required SkipKind Kind { get; init; }

    /// <summary>The ledger row the plan decided against; null for a skip that is not a plan's, which is written as it stands.</summary>
    public PlanBasis? Basis { get; init; }

    /// <summary>What the plan said about the record, for the attempt a stale or identical skip writes.</summary>
    public required string Reason { get; init; }

    /// <summary>The record's key tuple, as the source read it.</summary>
    public string? SourceKeyJson { get; init; }

    /// <summary>The ingestion fingerprint the source carried.</summary>
    public string? SourceFingerprint { get; init; }

    public DateTime? SourceModifiedUtc { get; init; }

    /// <summary>The ingestion file and row the plan read, for the attempt a stale or identical skip writes.</summary>
    public RecordOrigin Origin { get; init; }

    /// <summary>When the ingestion table first inserted the row, as the plan read it.</summary>
    public DateTime? SourceInsertedUtc { get; init; }

    /// <summary>The payload watermark the source carried.</summary>
    public DateTime? PayloadModifiedUtc { get; init; }

    /// <summary>The render context the record was rendered under (a rendered skip advances the record to it).</summary>
    public string? RenderContext { get; init; }

    /// <summary>With <see cref="RenderContext"/>: the estate commit the plan's run executed, when it knew it.</summary>
    public string? RenderCommit { get; init; }

    /// <summary>
    /// For a render identical to what OSDU holds: what moved to render it (osdu/docs/reference/concepts/delivery-reasons.md),
    /// which the skip's attempt keeps; null when nothing the ledger compares moved, and for every other skip.
    /// </summary>
    public Planning.DeliveryReason? Why { get; init; }

    /// <summary>
    /// For a rendered skip of a record whose document's paths the ledger never kept: the paths of the document OSDU holds,
    /// which the render found identical (<see cref="Planning.RenderPaths.EncodeText"/>). Null for every other skip.
    /// </summary>
    public string? RenderPaths { get; init; }

    /// <summary>
    /// For a rendered skip, the unique segment of the ids of the children the render looked for (<see cref="RecordState.ChildLinkKey"/>),
    /// null when it looked for none: a record rendered again under a mapping that now links its children keeps it although
    /// nothing is sent, so the children landing later find it.
    /// </summary>
    public string? ChildLinkKey { get; init; }

    /// <summary>For a rendered skip, the steps set aside on its holds (<see cref="RecordState.HeldSteps"/>); null when none was.</summary>
    public string? HeldSteps { get; init; }

    /// <summary>The platform run of the intake, for the attempt a stale or identical skip writes.</summary>
    public Guid? RunId { get; init; }
}

/// <summary>What staging a batch of pending work did.</summary>
/// <param name="Staged">Records whose pending work was written (queued behind an in-flight delivery included).</param>
/// <param name="Refused">
/// Records refused because the ledger already holds a newer source version or payload for them, delivered or queued
/// by a concurrent intake since this one read them. They are stale and are recorded as such.
/// </param>
/// <param name="Conflicts">
/// Records refused because another record, of another flow or of the same one, has claimed the OSDU id they would be
/// written to, or because another record of the same staging gives the same id. Nothing was written for them; the caller
/// holds them with the owner named.
/// </param>
public sealed record PendingStaging(int Staged, IReadOnlyList<DeliveryKey> Refused, IReadOnlyList<TargetIdConflict> Conflicts)
{
    public static PendingStaging Empty { get; } = new(0, [], []);
}

/// <summary>A record whose OSDU id another record has claimed: the id, and the record that holds it.</summary>
/// <param name="DeliveryKey">The record that was not staged.</param>
/// <param name="TargetId">The OSDU id it would have been written to.</param>
/// <param name="OwnerFlowId">The flow whose record claimed the id.</param>
/// <param name="OwnerFlowName">That flow's name as its last submission recorded it, when the ledger knows it.</param>
/// <param name="OwnerDeliveryKey">The record that claimed the id, when it is of the same flow as the refused one.</param>
/// <param name="OwnerSourceKey">That record's source key, as the ledger shows it.</param>
public sealed record TargetIdConflict(
    DeliveryKey DeliveryKey, string TargetId, Guid OwnerFlowId, string? OwnerFlowName, DeliveryKey? OwnerDeliveryKey = null, string? OwnerSourceKey = null)
{
    /// <summary>
    /// The name the owner's ledger is kept under, as a flow writes it in <c>ledger:</c> to keep that ledger
    /// (<see cref="LedgerEntry.OwnName"/>); null when the directory does not name it.
    /// </summary>
    public string? OwnerLedger { get; init; }

    /// <summary>
    /// False when no pipeline a repository declares keeps the owner's ledger any more: the flow that claimed the id was
    /// removed or renamed. True when one does, or when it is not known.
    /// </summary>
    public bool OwnerDeclared { get; init; } = true;

    /// <summary>What the held record says about the conflict: which id, whose it is, and how to resolve it.</summary>
    public string Describe()
    {
        if (OwnerDeliveryKey is { } record)
        {
            // Two records of one flow: their keys differ and give the same id, which only an id made from the key's values
            // can, or the ledger holds the id under a key the mapping no longer derives (dataset.system or key changed).
            var owner = OwnerSourceKey is null ? $"record {record}" : $"record {OwnerSourceKey} ({record})";
            return $"OSDU id {TargetId} is already claimed by {owner} of this flow, and one OSDU record belongs to one record of the ledger. "
                + "Two keys of this flow give the same id: keep one of them in the source. When the mapping's dataset.system or key changed "
                + "since that record was delivered, the id stays that record's: put the mapping back, or remove the flow's records from OSDU "
                + "(the record scope) and deliver them under a ledger of their own (ledger:, or interfaces.<name>.ledger), or delete them from the ledger "
                + "with the removal.";
        }

        var flow = OwnerFlowName is null ? $"flow {OwnerFlowId:D}" : $"flow '{OwnerFlowName}' ({OwnerFlowId:D})";
        const string Elsewhere = "this flow to another data partition, or give its mapping a dataset.system or key that yields other OSDU ids.";
        if (OwnerDeclared || (OwnerLedger ?? OwnerFlowName) is not { } ledger)
        {
            return $"OSDU id {TargetId} is already claimed by {flow}, and one OSDU record belongs to one flow. Deliver {Elsewhere}";
        }

        // The flow that claimed it is gone from every repository: most often this flow under its old name, since a flow keeps
        // its ledger under its name and a rename starts an empty one. The repository sync moves such a ledger to the flow that
        // replaced it when the two are one document (LedgerMoves); otherwise it is orphaned until someone binds it
        // (LedgerBindings), or a flow keeps it by name.
        return $"OSDU id {TargetId} is already claimed by {flow}, which no repository declares any more, and one OSDU record belongs to one flow. "
            + "When this flow replaced it, its ledger is orphaned: have this flow take it over on the Orphaned ledgers page, or sync the repository, "
            + "which moves it here by itself when this flow is the same document under a new name. Then run this flow again, and these records deliver as updates of the ids they claimed. "
            + $"A flow can also keep that ledger by name, with ledger: {ledger} in its document (interfaces.<name>.ledger for an interface of a source). "
            + $"Otherwise deliver {Elsewhere}";
    }
}

/// <summary>
/// The watermark of one flow scope: the upper bound of the last whole-scope plan that completed, and the submission that
/// wrote it. The next incremental plan reads the rows changed after it, less the flow's overlap.
/// </summary>
public sealed record SourceWatermark(Guid FlowId, string Scope, DateTime UpdatedThroughUtc, Guid SubmissionId, DateTime RecordedUtc, string? ContextHash);

/// <summary>Which half of a record a forced redelivery re-sends.</summary>
public enum RedeliverScope
{
    All,
    Metadata,
    Payload,
}

/// <summary>
/// What a forced redelivery re-sends: a half of the record, and, for a payload sent in parts, the parts of it (files,
/// bulk, workflow); no parts means every part.
/// </summary>
public sealed record RedeliverSelection(RedeliverScope Scope, IReadOnlyList<string> Parts)
{
    /// <summary>
    /// True when OSDU no longer holds the records (an inventory found them missing): only a record the ledger holds as
    /// delivered is marked, and it is marked as a verify marks one it finds missing, so the route that writes it brings a
    /// soft-deleted record back under its id before it writes, and writes a purged one again under the same id.
    /// </summary>
    public bool Lost { get; init; }

    public override string ToString()
        => Parts.Count == 0 ? Scope.ToString().ToLowerInvariant() : string.Join(" and ", Parts);
}

/// <summary>
/// What a release reaches: the records named by key, the records one problem keeps blocked, or every blocked record of
/// the flow.
/// </summary>
public sealed record ReleaseSelection
{
    private ReleaseSelection(IReadOnlyList<DeliveryKey>? keys, long? problem)
    {
        Keys = keys;
        Problem = problem;
    }

    /// <summary>Every held, failed and deleted record of the flow that is blocked.</summary>
    public static ReleaseSelection EveryBlocked { get; } = new(null, null);

    /// <summary>The records named; a waiting one among them is sent without waiting.</summary>
    public IReadOnlyList<DeliveryKey>? Keys { get; }

    /// <summary>The problem whose records are released (<see cref="ProblemSignature"/>).</summary>
    public long? Problem { get; }

    /// <summary>The records named by key.</summary>
    public static ReleaseSelection Named(IEnumerable<DeliveryKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return new ReleaseSelection(keys.Distinct().ToList(), null);
    }

    /// <summary>The records one problem keeps blocked.</summary>
    public static ReleaseSelection OfProblem(long problem) => new(null, problem);
}

/// <summary>
/// The part of the ledger (<see cref="ILedger"/>) that holds a record's state: planned, skipped, held, waiting,
/// released, redelivered, adopted and verified. A caller that uses only this part takes it rather than the whole
/// ledger; <see cref="OsduLedger"/> implements every part.
/// </summary>
public interface ILedgerRecords
{
    Task<IReadOnlyDictionary<DeliveryKey, RecordState>> GetRecordsAsync(Guid flowId, IEnumerable<DeliveryKey> keys, CancellationToken ct = default);

    Task<RecordState?> GetRecordAsync(Guid flowId, DeliveryKey key, CancellationToken ct = default);

    /// <summary>
    /// Which of <paramref name="targetIds"/> a record of the ledger has claimed, of any flow and partition. Ids compare
    /// exactly, as OSDU compares them; one seek of the claim index per id.
    /// </summary>
    Task<IReadOnlySet<string>> ClaimedTargetIdsAsync(IReadOnlyCollection<string> targetIds, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates one flow's records with pending work; existing current-state columns are preserved. A record
    /// another worker is delivering right now keeps its lease and status, and the new work is queued behind the delivery:
    /// the worker's completion leaves it pending for the next pass. Work carrying a source or payload version older
    /// than the one the record already holds, delivered or queued, is refused, so concurrent intakes can never take
    /// a record back to an earlier version. Work for an OSDU id another flow's record has claimed is refused as a
    /// conflict, and the first staging of a record claims its id for the flow. Staging writes the record's key tuple and
    /// the pending origin, and answers the request to plan it again that the plan read (<see cref="RecordState.Basis"/>);
    /// a request made after that read stays for the next run. Every record must carry <paramref name="flowId"/>.
    /// </summary>
    Task<PendingStaging> UpsertPendingAsync(Guid flowId, IReadOnlyList<RecordState> records, CancellationToken ct = default);

    /// <summary>
    /// Records what the intake skipped. A record with no pending work moves to the submission; a record with pending
    /// work stays with the submission that queued it. A rendered skip of a delivered record advances its source
    /// version, payload watermark and render context to what was just found identical, so the next plan decides it
    /// without rendering. A stale skip writes an attempt saying which version and origin the source carried and which
    /// version stands, once per row. A held, failed or reverted record OSDU holds a version of is delivered again when the
    /// plan found it rendering exactly that version, or, released, carrying a row older than it. A skip with a
    /// <see cref="SkippedRecord.Basis"/> is written only while the record is the one the plan read; a record changed since is
    /// asked to be planned again instead. Every skip writes the record's key tuple and answers the request to plan it again
    /// the plan read, except one awaiting approval, which keeps it.
    /// </summary>
    Task MarkSkippedAsync(Guid flowId, IReadOnlyList<SkippedRecord> records, Guid submissionId, CancellationToken ct = default);

    /// <summary>
    /// Marks one flow's records held without queueing work (render-time holds), writing the key tuple, the origin they
    /// were held at and the rules they were rendered under (<see cref="RecordState.HeldRulesHash"/>). A held record claims no
    /// OSDU id, and it is given no id another flow's record has claimed. A record
    /// with a <see cref="RecordState.Basis"/> that changed since the plan read it is asked to be planned again instead of
    /// held; one asked to be planned again after that read is held unblocked, keeping the request for the next run. Every
    /// record must carry <paramref name="flowId"/>. Returns the records it held.
    /// </summary>
    Task<IReadOnlyList<DeliveryKey>> MarkHeldAsync(Guid flowId, IEnumerable<RecordState> records, CancellationToken ct = default);

    /// <summary>
    /// The records of a flow the ledger asked to be planned again, in the order they were asked for (then by delivery key)
    /// after <paramref name="after"/>, at most <paramref name="max"/>: what a run pages through to plan them as a key-scoped
    /// read. The records deleted from the ledger whose rows wait to be read again come in the same order, marked
    /// <see cref="PlanRequestedRecord.Purged"/>. Each page is a seek of the two indexes that hold the requests alone, however
    /// many are waiting.
    /// </summary>
    Task<IReadOnlyList<PlanRequestedRecord>> ListPlanRequestedAsync(Guid flowId, PlanRequestedRecord? after, int max, CancellationToken ct = default);

    /// <summary>
    /// Whether the flow (or submission) holds a record still to send: pending, or being sent. With
    /// <paramref name="batches"/>, only records queued in those work batches count.
    /// </summary>
    Task<bool> HasPendingAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, BatchRange? batches = null, CancellationToken ct = default);

    /// <summary>
    /// The earliest retry time among pending records that are not yet due, or null when nothing waits. With
    /// <paramref name="batches"/>, only records queued in those work batches count.
    /// </summary>
    Task<DateTime?> NextDueAsync(Guid flowId, Guid? submissionId, DateTime nowUtc, BatchRange? batches = null, CancellationToken ct = default);

    /// <summary>
    /// Sends back to pending the flow's waiting records (the ones named, or all of them) whose wait is over: the record
    /// they wait for has landed, or no record the ledger holds is left to wait for. The next claim decides again. Returns
    /// how many went back.
    /// </summary>
    Task<int> ReleaseResolvedWaitsAsync(Guid flowId, IReadOnlyCollection<DeliveryKey>? keys, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// The records waiting for <paramref name="targetId"/>, in any flow of the partition of <paramref name="flowId"/>'s
    /// ledger (an id is referred to within its partition), at most <paramref name="max"/>, most recently updated first.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ListWaitingForAsync(Guid flowId, string targetId, int max, CancellationToken ct = default);

    /// <summary>
    /// The ids among <paramref name="ids"/> that a record of the partition of <paramref name="flowId"/>'s ledger holds,
    /// other than a record removed from OSDU.
    /// </summary>
    Task<IReadOnlySet<string>> HeldIdsAsync(Guid flowId, IReadOnlyCollection<string> ids, CancellationToken ct = default);

    /// <summary>
    /// The records that hold <paramref name="targetId"/> as the id they are delivered to, in any flow of the partition of
    /// <paramref name="flowId"/>'s ledger, compared exactly: the one that claimed it first, then any held before it claimed
    /// one. At most <paramref name="max"/>.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ListHoldersAsync(Guid flowId, string targetId, int max, CancellationToken ct = default);

    /// <summary>
    /// The records that read <paramref name="parentId"/> as their parent (osdu/docs/reference/flow/mapping-parent.md), in any
    /// flow of the partition of <paramref name="flowId"/>'s ledger, compared exactly, most recently updated first; a record
    /// taken out of OSDU reads it no longer and is left out. At most <paramref name="max"/>.
    /// </summary>
    Task<IReadOnlyList<RecordState>> ListChildrenOfAsync(Guid flowId, string parentId, int max, CancellationToken ct = default);

    /// <summary>
    /// Takes in what a sync found of the flow's records' rows: each arrival the ledger lacks or holds otherwise, a request
    /// to plan each record whose row changed or was marked deleted unseen, and an attempt
    /// (<see cref="AttemptPhases.SourceMissing"/>) for each record whose row is gone. A record keeps its status, and one
    /// already asked to be planned keeps the moment it was asked.
    /// </summary>
    Task<SourceSyncApplied> ApplySourceSyncAsync(Guid flowId, IReadOnlyList<SourceSyncFinding> findings, Guid? runId, CancellationToken ct = default);

    /// <summary>Delivered records, and records removed from OSDU with an id, due for the drift pass, oldest verification first.</summary>
    Task<IReadOnlyList<RecordState>> ListForVerifyAsync(Guid flowId, DateTime? verifiedBeforeUtc, int max, CancellationToken ct = default);

    /// <summary>
    /// Records what the drift pass found of one delivered record. With <paramref name="requeue"/> (the flow's
    /// <c>verify.reconcile</c>), a record found drifted or missing that OSDU still holds by the ledger's account is marked
    /// for redelivery of all of it by the one statement every redelivery marks records with
    /// (<see cref="ForceRedeliverAsync(Guid, IEnumerable{DeliveryKey}?, RedeliverSelection, long?, DateTime, CancellationToken)"/>):
    /// what OSDU holds and the source version it was planned under are forgotten, and the flow's next deliver run plans it
    /// again whatever its incremental window reads, named under the verify's activity <paramref name="activityId"/> when
    /// given. The mark is written before the outcome, so a write that fails between the two leaves the record queued and
    /// verified again by the next pass, never verified and not queued.
    /// </summary>
    Task RecordVerifyAsync(Guid flowId, DeliveryKey key, VerifyOutcome outcome, long? observedVersion, DateTime nowUtc, bool requeue, long? activityId = null, CancellationToken ct = default);

    /// <summary>
    /// Releases held, failed, deleted or reverted records: those with a pending document go back to pending for the worker,
    /// the others are unblocked and asked to be planned again by the flow's next run. Null keys means every blocked record.
    /// A waiting record named by key goes back to pending without its references, so it is sent without waiting any more;
    /// a release of the whole flow leaves waiting records to their wait.
    /// </summary>
    Task<int> ReleaseAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Releases the records <paramref name="selection"/> names, as the release by keys above does: a record still holding
    /// its rendered document goes back to pending, any other is unblocked and asked to be planned again, and a waiting
    /// record named by key is sent without waiting. No statement writes more than a slice of records, however many the
    /// selection reaches. A release of every blocked record, or of the records a problem keeps blocked, walks an index
    /// forward a page at a time and ends at its end; a record a run blocks with the same problem while it walks is
    /// released too. With <paramref name="activityId"/>, the statement that releases a record names it under that intervention
    /// (<c>osdu.ActivityRecord</c>), so the record's own history shows the release, who asked and when, however many
    /// records it reached. Returns how many records it released.
    /// </summary>
    Task<int> ReleaseAsync(Guid flowId, ReleaseSelection selection, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Forgets what OSDU holds for the records (the whole record, the metadata document or the payload) and asks the
    /// flow's next run to plan them again. Null keys means every record the flow has delivered. Returns how many records
    /// were marked.
    /// </summary>
    Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverScope scope, DateTime nowUtc, CancellationToken ct = default)
        => ForceRedeliverAsync(flowId, keys, new RedeliverSelection(scope, []), nowUtc, ct);

    /// <summary>
    /// Marks records so the next plan sends again what <paramref name="selection"/> names: the named ones, or with null
    /// keys every record the flow has delivered, a slice at a time. A selection of payload parts
    /// (osdu/docs/reference/concepts/protocols.md, Payload parts) leaves them on the record's delivered payload hash
    /// (<see cref="Model.PayloadParts.RedeliverMarker"/>), where the plan reads them and the next payload delivery replaces them.
    /// </summary>
    Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverSelection selection, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// As the redelivery above, with every record it marks named under the intervention <paramref name="activityId"/>
    /// (<c>osdu.ActivityRecord</c>), so each record's history shows the redelivery that reached it, who asked and when. A
    /// redelivery of every delivered record walks them a page at a time, passing over those it marked. A record still
    /// blocked by a problem keeps its error, which its problem was read from.
    /// </summary>
    Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverSelection selection, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Asks records OSDU holds to be brought up to date: rendered again by the next plan that meets them, and sent only where
    /// they render differently. The named records, or with null keys every record the flow has delivered, a slice at a time;
    /// a named record OSDU does not hold is left as it is. Unlike a redelivery, nothing of what OSDU holds is forgotten: the
    /// delivered hashes stay, and decide what is sent. The fingerprint a record was last planned under is cleared and its
    /// business date kept; the request is what has a plan render it, so no plan passes it as unchanged without rendering it,
    /// whichever run meets it first, and a plan that finds it unchanged writes that version back. Every record marked is named under the intervention <paramref name="activityId"/>. Returns how
    /// many were marked.
    /// </summary>
    Task<int> RequestRenderAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Records what a removal did to a set of the flow's records, in one round trip. <see cref="RemovalScope.Record"/> and
    /// <see cref="RemovalScope.Everything"/> take the record out of OSDU, so the ledger marks it deleted and forgets the
    /// hashes and the version, without blocking it. With <paramref name="deliverAgain"/> (an operator's removal) the record
    /// is asked to be planned again, so the flow's next run reads its row by key and delivers it again, as it would a record
    /// never delivered; without it (the removal that opens deleting the whole ledger, an inventory's removal of an id the
    /// ledger already holds as removed) the removal asks for nothing itself and leaves any request the record carried.
    /// <see cref="RemovalScope.History"/> leaves the record live, so its custody state is untouched and only the attempt is
    /// written. Either way every record gets its own attempt, saying which scope ran and who asked for it, because that
    /// attempt is how the removal is audited afterwards. A record taken out of OSDU by either of the first two scopes can no
    /// longer be linked as a child: the records of the partition that link it (<c>$child</c>) are asked to be planned again,
    /// so they render without the link (osdu/docs/reference/flow/mapping-child.md, When a child is taken out).
    /// </summary>
    /// <remarks>
    /// With <paramref name="sources"/> the records are taken out because their source no longer holds them (a removal flow,
    /// osdu/docs/reference/flow/removal.md): each attempt says so (phase <see cref="AttemptPhases.SourceRemoved"/>, the removal
    /// flow, run and the file and row that said so), carries the removal's run, and is named under its activity; the record
    /// itself is not asked to be planned again, since what keeps it out is the ledger's deletion of its key
    /// (<see cref="ILedgerRemovals.DetectRemovalsAsync"/>).
    /// </remarks>
    Task MarkRemovedAsync(
        Guid flowId, IReadOnlyList<DeliveryKey> keys, RemovalScope scope, string worker, DateTime nowUtc, string? correlationId = null, bool deliverAgain = true,
        IReadOnlyDictionary<DeliveryKey, SourceRemovalNote>? sources = null, CancellationToken ct = default);

    /// <summary>
    /// Records that each record's version before the latest was written back as its current version
    /// (osdu/docs/reference/concepts/removal-and-reversal.md, Restoring the previous version), a chunk to a transaction.
    /// A record is settled only while the ledger still holds it at
    /// the version that was replaced, with no lease and no work queued; one that moved on meanwhile is left as it is and
    /// returned, so the caller can say what OSDU now holds. A settled record becomes reverted and blocked at the new version,
    /// with the hashes and origin of the attempt that delivered or restored the version put back when the ledger holds one
    /// (none otherwise, so a release sends it again where it renders differently), and gets its own attempt (outcome
    /// restored, phase <see cref="AttemptPhases.RestorePrevious"/>) naming both versions and who asked. A record already
    /// blocked by an earlier write back keeps the source version it is blocked at.
    /// </summary>
    /// <summary>
    /// Gives each named record of one ledger a new OSDU id in place of the one it claimed, which OSDU no longer holds
    /// (osdu/docs/reference/flow/inventory-removals.md, Repairing missing ids), a chunk to a transaction:
    /// <see cref="Identity.TargetId.Reassigned"/>, claimed at once and marked assigned, so the next plan writes the record under
    /// it rather than the id its mapping makes from its key. What OSDU held of the old id is forgotten (its version, target
    /// state and hashes), and the record is asked to be planned again. Only a record the ledger holds as delivered, with no
    /// lease, whose new id no other record claims, is given one; any other is left as it is. Each record given one keeps an
    /// attempt naming both ids, who asked and <paramref name="reason"/> (phase <see cref="AttemptPhases.ReassignId"/>), is
    /// named under <paramref name="activityId"/>, and is found by its new id. Returns the records given one.
    /// </summary>
    Task<IReadOnlyList<TargetIdReassignment>> ReassignTargetIdsAsync(
        Guid flowId, IReadOnlyList<DeliveryKey> keys, string actor, string reason, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Asks the records of one ledger that <paramref name="selection"/> reaches (the ones named, the ones a problem keeps held,
    /// or every blocked record) to adopt the record OSDU holds at the id each one's key makes
    /// (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already holds), a slice to a statement: each
    /// record held because OSDU held a record at that id (<see cref="OccupiedHolds"/>), claiming none and no lease holding it, is
    /// marked (<see cref="RecordState.AdoptRequestedUtc"/>), unblocked and asked to be planned again, and named under
    /// <paramref name="activityId"/>. Any other record is left as it is. The adoption itself is the next run's: it makes the
    /// id from the record's key, asks OSDU, and claims the id at the version OSDU holds then. Returns the records asked.
    /// </summary>
    Task<IReadOnlyList<DeliveryKey>> RequestAdoptionAsync(Guid flowId, ReleaseSelection selection, long? activityId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Records on the history of each record of <paramref name="adoptions"/> that a run adopted the record OSDU held at its
    /// id, as an operator asked: an attempt (phase <see cref="AttemptPhases.Adopt"/>, outcome skipped, since adopting sends
    /// nothing) naming the id and the version OSDU held, under the run and submission that took it over.
    /// </summary>
    Task RecordAdoptionsAsync(Guid flowId, IReadOnlyList<RecordAdoption> adoptions, Guid? submissionId, Guid? runId, DateTime nowUtc, CancellationToken ct = default);

    Task<IReadOnlyList<DeliveryKey>> MarkRestoredAsync(
        Guid flowId, IReadOnlyList<PreviousVersionRestored> restored, string worker, Guid? runId, DateTime nowUtc, string? correlationId = null, CancellationToken ct = default);

    /// <summary>
    /// The keys of every record a listing matches, in key order, up to <paramref name="max"/>. Key order is what
    /// makes this safe to act on: a removal changes the records it touches, and the newest-first order the listing
    /// pages in would shuffle rows between pages while the removal ran. Bounded like <see cref="ILedgerQueries.ListAsync"/>: a prefix
    /// search reads at most <see cref="RecordListing.CountLimit"/> + 1 candidates per identity index, and a broad
    /// contains search is refused.
    /// </summary>
    Task<IReadOnlyList<DeliveryKey>> ListKeysAsync(Guid flowId, RecordQuery query, int max, CancellationToken ct = default);

    /// <summary>
    /// The OSDU ids of the records of <paramref name="flowId"/>'s ledger a run changed in OSDU since <paramref name="sinceUtc"/>,
    /// at most <paramref name="max"/> of them, in no particular order: the delivered records last sent since then, and the
    /// records deleted or reverted since then. A pipeline's after step reads them back
    /// (osdu/docs/reference/concepts/partition-cache.md, Pipelines bring the cache up to date). A record found unchanged was
    /// not sent, and is not among them. With <paramref name="listedUnder"/>, each record's ids its target state lists under
    /// that value (a JSON array, as <c>enrich.datasets</c> lists the datasets its steps registered) come with it.
    /// </summary>
    Task<IReadOnlyList<string>> ChangedSinceAsync(Guid flowId, DateTime sinceUtc, int max, string? listedUnder = null, CancellationToken ct = default);

    /// <summary>The watermark of one flow scope, or null when no whole-scope plan of it has completed.</summary>
    Task<SourceWatermark?> GetWatermarkAsync(Guid flowId, string scope, CancellationToken ct = default);

    /// <summary>Writes the watermark of one flow scope; a watermark never moves back to an earlier bound.</summary>
    Task SetWatermarkAsync(SourceWatermark watermark, CancellationToken ct = default);
}
