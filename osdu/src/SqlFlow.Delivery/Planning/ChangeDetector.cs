using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Planning;

/// <summary>What a plan decides for one record.</summary>
public enum PlannedAction
{
    /// <summary>Nothing in OSDU would change.</summary>
    Skip,

    /// <summary>OSDU holds nothing for this key yet.</summary>
    Create,

    /// <summary>The metadata document changed; the payload did not (or there is none).</summary>
    UpdateMetadata,

    /// <summary>The payload changed; the metadata document did not.</summary>
    UpdatePayload,

    /// <summary>Both changed.</summary>
    UpdateBoth,

    /// <summary>The record cannot be delivered as it stands.</summary>
    Hold,

    /// <summary>
    /// Held, failed or deleted earlier and the source has not changed since: it stays where it is until an
    /// operator releases it (osdu/docs/reference/concepts/record-lifecycle.md, Record states).
    /// </summary>
    Blocked,
}

/// <summary>Why a skip was decided, for honest run summaries.</summary>
public enum SkipTier
{
    None,

    /// <summary>Tier 1: source fingerprint and render context unchanged, no render needed.</summary>
    Fingerprint,

    /// <summary>Tier 2: rendered and compared; the content hashes matched.</summary>
    ContentHash,

    /// <summary>Not a change gate: a cache change is tagged against the record and nobody has approved it yet.</summary>
    Approval,

    /// <summary>
    /// The row the run reads (or its payload files) is last modified before the version the ledger already holds,
    /// delivered or queued: a replayed or late row, which must never take OSDU back to an earlier version.
    /// </summary>
    Stale,

    /// <summary>
    /// A removal flow found the source no longer holds the key (osdu/docs/reference/flow/removal.md), and the row is not newer
    /// than the moment it was removed: the ledger keeps the key out, so the row is never delivered.
    /// </summary>
    SourceRemoved,
}

/// <summary>What tier 2 found when it compared a render's hashes with what the ledger holds.</summary>
public enum ChangeBasisKind
{
    /// <summary>The ledger holds no record for the key.</summary>
    NotInLedger,

    /// <summary>The ledger holds the record, and OSDU has never held a version of it.</summary>
    NeverDelivered,

    /// <summary>Neither the document hash nor the payload hash moved.</summary>
    Unchanged,

    /// <summary>The document hash, the payload hash or both moved, or a change key sends them whatever they say.</summary>
    Changed,
}

/// <summary>Whether tier 2 compared a render with the work already queued for the record, and what it found there.</summary>
public enum QueueComparison
{
    /// <summary>Nothing is queued, or the queue did not change the decision.</summary>
    None,

    /// <summary>The render is what is queued: a skip, since that work lands it.</summary>
    EqualsQueued,

    /// <summary>The render differs from what is queued and replaces it.</summary>
    ReplacesQueued,

    /// <summary>
    /// What is queued cannot be sent by the run that plans: its document is not where that run reads its work from (another
    /// node wrote it there, or the work location was pruned). The render replaces it whatever it held, so the record is
    /// sent from a document the run can read.
    /// </summary>
    UnreadableQueued,
}

/// <summary>
/// What tier 2 decided a record on, as the structure a delivery's reason (<see cref="DeliveryReason"/>) is built from and the
/// words a plan says it in (<see cref="Describe"/>).
/// </summary>
public sealed record ChangeBasis
{
    public required ChangeBasisKind Kind { get; init; }

    /// <summary>For <see cref="ChangeBasisKind.NeverDelivered"/>: the status the ledger holds the record in.</summary>
    public RecordStatus? Status { get; init; }

    /// <summary>The document is sent: its hash moved, or a change key sends it.</summary>
    public bool MetadataChanged { get; init; }

    /// <summary>The payload is sent: its hash moved, or a change key sends it.</summary>
    public bool PayloadChanged { get; init; }

    public QueueComparison Queue { get; init; }

    /// <summary>For <see cref="QueueComparison.ReplacesQueued"/>: what the comparison with the queued work found.</summary>
    public ChangeBasis? Queued { get; init; }

    public static ChangeBasis NotInLedger { get; } = new() { Kind = ChangeBasisKind.NotInLedger, MetadataChanged = true };

    /// <summary>What a plan says of work it replaces because the run cannot read the document it queued.</summary>
    public const string UnreadableQueue =
        "the document already queued for the record is not where this run reads its work from, so this render replaces it and the record is sent from here";

    /// <summary>The basis in the words a plan's entry says it in.</summary>
    public string Describe() => (Kind, Queue) switch
    {
        (_, QueueComparison.EqualsQueued) => "metadata and payload hashes equal the version already queued",
        (_, QueueComparison.ReplacesQueued) => $"{Queued?.Describe() ?? "it differs"} against the version already queued, which this one replaces",
        (_, QueueComparison.UnreadableQueued) => $"{(this with { Queue = QueueComparison.None }).Describe()}; {UnreadableQueue}",
        (ChangeBasisKind.NotInLedger, _) => "not in ledger",
        (ChangeBasisKind.NeverDelivered, _) => $"ledger status {Status?.ToString().ToLowerInvariant() ?? "unknown"}, never delivered",
        (ChangeBasisKind.Unchanged, _) => "metadata and payload hashes unchanged",
        _ => (MetadataChanged, PayloadChanged) switch
        {
            (true, true) => "metadata and payload changed",
            (true, false) => "metadata changed, payload unchanged",
            _ => "payload changed, metadata unchanged",
        },
    };
}

/// <summary>
/// What a plan decides for a record: the action, which halves it sends, and the structured basis it decided on. Its
/// <see cref="Reason"/> is the basis in words unless the plan said something in its place (a payload older than the one
/// delivered, a newer payload kept from the queue).
/// </summary>
public sealed record ChangeDecision(PlannedAction Action, SkipTier SkipTier, bool DeliverMetadata, bool DeliverPayload, ChangeBasis Basis)
{
    private readonly string? _reason;

    public bool IsDelivery => DeliverMetadata || DeliverPayload;

    /// <summary>The decision in words: the basis's (<see cref="ChangeBasis.Describe"/>), or what the plan set in its place.</summary>
    public string Reason
    {
        get => _reason ?? Basis.Describe();
        init => _reason = value;
    }
}

/// <summary>
/// The per-record gates (osdu/docs/reference/concepts/change-detection.md, What is decided for each record read) and
/// the two independently decided hashes (osdu/docs/reference/concepts/change-detection.md, The versions and hashes
/// compared). Pure: given what the ledger holds and what the row the run reads presents, decide what to send.
/// </summary>
public static class ChangeDetector
{
    /// <summary>
    /// Tier 1: can the record be skipped without rendering? True when the ledger holds a delivered record whose
    /// source version (the fingerprint, or the last-modified moment), payload hash and render context all match what
    /// the row the run reads presents. Pass the <see cref="Expected"/> state, so work already queued counts as held. A
    /// record asked to be planned again is never skipped here: the request asks for a render.
    /// </summary>
    public static bool CanSkipWithoutRender(RecordState? existing, SourceVersion source, string? payloadHash, string renderContext, FlowChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (existing is null || change.OnUnchanged == UnchangedAction.Deliver)
        {
            return false;
        }

        // A record asked to be planned again (brought up to date, released, its cache changed) is answered by what it renders
        // to now, whatever source version it was last planned under: the request is the ledger saying the version alone no
        // longer tells what a render would send.
        if (existing.PlanRequestedUtc is not null)
        {
            return false;
        }

        // A forgotten metadata hash (a redelivery, a drift, a cache change) is the ledger saying it no longer knows
        // what OSDU holds; only a render can answer that.
        if (existing.Status != RecordStatus.Delivered || existing.MetadataHash is null)
        {
            return false;
        }

        if (change.Detect == ChangeDetection.Always || change.PayloadDetect == ChangeDetection.Always)
        {
            return false;
        }

        // Every part of the version the source carries has to agree: the ingestion fingerprint says the rows are the ones
        // the version held was built from, and the business version, when the flow declares one, says the source did not
        // move it either. A flow that decides changes by source.lastModified alone carries no fingerprint, and then the
        // moment is the whole version: a record left at no known moment has nothing to compare, so it is rendered.
        var sameSource = source switch
        {
            { Fingerprint: not null } => string.Equals(existing.SourceFingerprint, source.Fingerprint, StringComparison.Ordinal)
                && (source.ModifiedUtc is not { } moved || existing.SourceModifiedUtc == moved),
            { ModifiedUtc: { } modified } => existing.SourceModifiedUtc == modified,
            _ => false,
        };

        return sameSource
            && string.Equals(existing.RenderContext, renderContext, StringComparison.Ordinal)
            && string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal);
    }

    /// <summary>Tier 2: decide from the rendered document's hash and the payload hash.</summary>
    public static ChangeDecision Decide(
        RecordState? existing,
        string metadataHash,
        string? payloadHash,
        bool hasPayload,
        FlowChange change)
    {
        ArgumentNullException.ThrowIfNull(metadataHash);
        ArgumentNullException.ThrowIfNull(change);

        var delivered = existing is { Status: RecordStatus.Delivered } || existing?.TargetVersion is not null;
        if (!delivered)
        {
            return new ChangeDecision(
                PlannedAction.Create, SkipTier.None, true, hasPayload,
                existing is null
                    ? ChangeBasis.NotInLedger with { PayloadChanged = hasPayload }
                    : new ChangeBasis { Kind = ChangeBasisKind.NeverDelivered, Status = existing.Status, MetadataChanged = true, PayloadChanged = hasPayload });
        }

        var metadataChanged = change.Detect == ChangeDetection.Always
            || change.OnUnchanged == UnchangedAction.Deliver
            || !string.Equals(existing!.MetadataHash, metadataHash, StringComparison.Ordinal);

        var payloadChanged = hasPayload && (change.PayloadDetect == ChangeDetection.Always
            || change.OnUnchanged == UnchangedAction.Deliver
            || !string.Equals(existing!.PayloadHash, payloadHash, StringComparison.Ordinal));

        if (!metadataChanged && !payloadChanged)
        {
            return new ChangeDecision(PlannedAction.Skip, SkipTier.ContentHash, false, false, new ChangeBasis { Kind = ChangeBasisKind.Unchanged });
        }

        var basis = new ChangeBasis { Kind = ChangeBasisKind.Changed, MetadataChanged = metadataChanged, PayloadChanged = payloadChanged };
        if (metadataChanged && payloadChanged)
        {
            return new ChangeDecision(PlannedAction.UpdateBoth, SkipTier.None, true, true, basis);
        }

        return metadataChanged
            ? new ChangeDecision(PlannedAction.UpdateMetadata, SkipTier.None, true, false, basis)
            : new ChangeDecision(PlannedAction.UpdatePayload, SkipTier.None, false, true, basis);
    }

    /// <summary>
    /// Tier 2 against both what OSDU holds and what is already queued for it. A render identical to the queued work is
    /// a skip, because that work lands it. Otherwise a half is sent when it differs from either: the queue it replaces
    /// may carry a half OSDU does not hold yet, and a delivery in flight may land something this render differs from.
    /// The worker's final hash check (<see cref="AtPush"/>) then drops whatever the target turns out to hold already.
    /// <para>
    /// With <paramref name="queueReadable"/> false, the run that plans cannot read the document the queued work points at
    /// (another node wrote it to its own work folder, or the work location was pruned), so that work cannot land from
    /// here and is never a reason to skip: the render replaces it even when it is identical to it, sending the halves OSDU
    /// does not hold yet. A render identical to the queue and to what OSDU holds still replaces the queue, as a document the
    /// final check then finds OSDU holding, so the record settles with nothing sent rather than keep work no run can send.
    /// </para>
    /// </summary>
    public static ChangeDecision DecideWithQueue(
        RecordState? existing, string metadataHash, string? payloadHash, bool hasPayload, FlowChange change, bool queueReadable = true)
    {
        var delivered = Decide(existing, metadataHash, payloadHash, hasPayload, change);
        if (existing is not { HasPendingWork: true })
        {
            return delivered;
        }

        var queued = Decide(Expected(existing), metadataHash, payloadHash, hasPayload, change);
        if (queued.Action == PlannedAction.Skip && queueReadable)
        {
            return queued with { Basis = queued.Basis with { Queue = QueueComparison.EqualsQueued } };
        }

        var metadata = delivered.DeliverMetadata || queued.DeliverMetadata;
        var payload = delivered.DeliverPayload || queued.DeliverPayload;
        if (!metadata && !payload)
        {
            // Only work that cannot be read reaches here: identical to the queue and to what OSDU holds.
            metadata = true;
        }

        var action = delivered.Action == PlannedAction.Create
            ? PlannedAction.Create
            : (metadata, payload) switch
            {
                (true, true) => PlannedAction.UpdateBoth,
                (true, false) => PlannedAction.UpdateMetadata,
                _ => PlannedAction.UpdatePayload,
            };
        // Against the version OSDU holds, the render is a create, or sends what moved from it; the queue adds the halves it
        // differs from, and the basis keeps both so a reason can say the render replaces queued work. Work replaced because
        // it cannot be read says what the render is against the version OSDU holds, as it stands: nothing moved there for a
        // render the queue already held.
        var basis = delivered.Basis with
        {
            Kind = queueReadable && delivered.Basis.Kind == ChangeBasisKind.Unchanged ? ChangeBasisKind.Changed : delivered.Basis.Kind,
            MetadataChanged = metadata,
            PayloadChanged = payload,
            Queue = queueReadable ? QueueComparison.ReplacesQueued : QueueComparison.UnreadableQueued,
            Queued = queued.Basis,
        };
        return new ChangeDecision(action, SkipTier.None, metadata, payload, basis);
    }

    /// <summary>
    /// What OSDU will hold for the record once the work already queued for it lands: the record as it is when nothing
    /// is queued, else its delivered state with the queued document, payload and source version in their place. A
    /// row that brings exactly what is queued changes nothing; one that brings something else replaces the queue.
    /// </summary>
    public static RecordState? Expected(RecordState? existing)
    {
        if (existing is not { HasPendingWork: true })
        {
            return existing;
        }

        return existing with
        {
            Status = RecordStatus.Delivered,
            RenderContext = existing.PendingRenderContext ?? existing.RenderContext,
            SourceFingerprint = existing.PendingSourceFingerprint ?? existing.SourceFingerprint,
            SourceModifiedUtc = existing.PendingSourceModifiedUtc ?? existing.SourceModifiedUtc,
            MetadataHash = existing.PendingMetadata ? existing.PendingMetadataHash : existing.MetadataHash,
            PayloadHash = existing.PendingPayload ? existing.PendingPayloadHash : existing.PayloadHash,
            PayloadModifiedUtc = existing.PendingPayload ? existing.PendingPayloadModifiedUtc ?? existing.PayloadModifiedUtc : existing.PayloadModifiedUtc,
        };
    }

    /// <summary>The newest source last-modified moment the record holds, delivered or queued; null when it holds none.</summary>
    public static DateTime? NewestSourceModified(RecordState? existing)
        => existing is null ? null : Latest(existing.SourceModifiedUtc, existing.HasPendingWork ? existing.PendingSourceModifiedUtc : null);

    /// <summary>The newest payload watermark the record holds, delivered or queued; null when it holds none.</summary>
    public static DateTime? NewestPayloadModified(RecordState? existing)
        => existing is null ? null : Latest(existing.PayloadModifiedUtc, existing is { HasPendingWork: true, PendingPayload: true } ? existing.PendingPayloadModifiedUtc : null);

    /// <summary>
    /// Whether a blocked record (held, failed, deleted or reverted, and not released) stays blocked for the version the row
    /// the run reads carries. It does until the row moves on from the version it was left at: the ingestion table rewrote it
    /// (its fingerprint differs from the one it was left at), or, when the flow orders its rows by a last-modified column,
    /// the row's moment moved past the one it was left at. Under that order a row whose moment is older than the one it was
    /// left at is a late or re-landed row and moves nothing, whatever its fingerprint; one at the same moment moves the
    /// record when the ingestion table rewrote it, since a correction need not touch the column (a business date names the
    /// day the data describes, not the day it was fixed). A record left at no fingerprint, or a row that says nothing about
    /// its version, cannot be seen to move, and needs an operator: a release, or a redelivery, which asks for it to be
    /// planned whatever version it was left at.
    /// </summary>
    public static bool StaysBlocked(RecordState blocked, SourceVersion source, bool ordered)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        if (ordered && source.ModifiedUtc is { } modified)
        {
            if (blocked.PendingSourceModifiedUtc is not { } left || modified > left)
            {
                return false;
            }

            if (modified < left)
            {
                return true;
            }
        }

        return source.Fingerprint is null
            || blocked.PendingSourceFingerprint is null
            || string.Equals(blocked.PendingSourceFingerprint, source.Fingerprint, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a blocked record is held at its render: held keeping no document, so its error is what its row and the rules
    /// it was rendered under gave, and no release has a document to send. A record held by the check before sending or by a
    /// try keeps its document; a record held because OSDU already holds a record at its id waits for an adoption, and is
    /// told by its id or, held before the hold kept the id, by its error (<see cref="OccupiedHolds"/>).
    /// </summary>
    public static bool HeldAtRender(RecordState blocked)
    {
        ArgumentNullException.ThrowIfNull(blocked);
        return blocked is { Status: RecordStatus.Held, PendingDocumentRef: null, OccupiedTargetId: null }
            && blocked.LastError?.StartsWith(OccupiedHolds.Prefix, StringComparison.Ordinal) != true;
    }

    /// <summary>
    /// Whether a blocked record was held at its render under other rules than the ones a plan renders with now
    /// (<paramref name="rulesHash"/>, <see cref="Snapshots.RenderContext.RulesHash"/>): its mapping, template or parameters
    /// moved since, so its error may be one those rules no longer give, and the plan renders it again rather than leaving it
    /// blocked (osdu/docs/reference/concepts/record-lifecycle.md, A record held at its render). A hold that recorded no rules,
    /// made before the ledger kept them, was made under rules no one knows, and is rendered again too.
    /// </summary>
    public static bool HeldUnderOtherRules(RecordState blocked, string rulesHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesHash);
        return HeldAtRender(blocked) && !string.Equals(blocked.HeldRulesHash, rulesHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// The last check before anything is sent (osdu/docs/reference/concepts/change-detection.md, Work already queued,
    /// and the final check): the claimed record's queued document and payload against what OSDU holds at the moment the
    /// worker has the record. Work is planned against the state the intake read, and a delivery of the same content can
    /// land in between (work queued behind an in-flight delivery, a submission re-run); sending it again would only
    /// burn a version. Returns which halves to send; a half the work does not carry is never sent.
    /// </summary>
    public static (bool Metadata, bool Payload) AtPush(RecordState claimed, FlowChange change)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        ArgumentNullException.ThrowIfNull(change);
        var held = claimed.LastDeliveredUtc is not null ? claimed with { Status = RecordStatus.Delivered } : claimed;
        var decision = Decide(held, claimed.PendingMetadataHash ?? string.Empty, claimed.PendingPayloadHash, claimed.PendingPayload, change);
        return (claimed.PendingMetadata && decision.DeliverMetadata, claimed.PendingPayload && decision.DeliverPayload);
    }

    private static DateTime? Latest(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : a > b ? a : b;
}
