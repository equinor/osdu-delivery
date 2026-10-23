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

public sealed record ChangeDecision(PlannedAction Action, SkipTier SkipTier, bool DeliverMetadata, bool DeliverPayload, string Reason)
{
    public bool IsDelivery => DeliverMetadata || DeliverPayload;
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
            return new ChangeDecision(PlannedAction.Create, SkipTier.None, true, hasPayload, existing is null ? "not in ledger" : $"ledger status {existing.Status.ToString().ToLowerInvariant()}, never delivered");
        }

        var metadataChanged = change.Detect == ChangeDetection.Always
            || change.OnUnchanged == UnchangedAction.Deliver
            || !string.Equals(existing!.MetadataHash, metadataHash, StringComparison.Ordinal);

        var payloadChanged = hasPayload && (change.PayloadDetect == ChangeDetection.Always
            || change.OnUnchanged == UnchangedAction.Deliver
            || !string.Equals(existing!.PayloadHash, payloadHash, StringComparison.Ordinal));

        if (!metadataChanged && !payloadChanged)
        {
            return new ChangeDecision(PlannedAction.Skip, SkipTier.ContentHash, false, false, "metadata and payload hashes unchanged");
        }

        if (metadataChanged && payloadChanged)
        {
            return new ChangeDecision(PlannedAction.UpdateBoth, SkipTier.None, true, true, "metadata and payload changed");
        }

        return metadataChanged
            ? new ChangeDecision(PlannedAction.UpdateMetadata, SkipTier.None, true, false, "metadata changed, payload unchanged")
            : new ChangeDecision(PlannedAction.UpdatePayload, SkipTier.None, false, true, "payload changed, metadata unchanged");
    }

    /// <summary>
    /// Tier 2 against both what OSDU holds and what is already queued for it. A render identical to the queued work is
    /// a skip, because that work lands it. Otherwise a half is sent when it differs from either: the queue it replaces
    /// may carry a half OSDU does not hold yet, and a delivery in flight may land something this render differs from.
    /// The worker's final hash check (<see cref="AtPush"/>) then drops whatever the target turns out to hold already.
    /// </summary>
    public static ChangeDecision DecideWithQueue(RecordState? existing, string metadataHash, string? payloadHash, bool hasPayload, FlowChange change)
    {
        var delivered = Decide(existing, metadataHash, payloadHash, hasPayload, change);
        if (existing is not { HasPendingWork: true })
        {
            return delivered;
        }

        var queued = Decide(Expected(existing), metadataHash, payloadHash, hasPayload, change);
        if (queued.Action == PlannedAction.Skip)
        {
            return queued with { Reason = "metadata and payload hashes equal the version already queued" };
        }

        var metadata = delivered.DeliverMetadata || queued.DeliverMetadata;
        var payload = delivered.DeliverPayload || queued.DeliverPayload;
        var action = delivered.Action == PlannedAction.Create
            ? PlannedAction.Create
            : (metadata, payload) switch
            {
                (true, true) => PlannedAction.UpdateBoth,
                (true, false) => PlannedAction.UpdateMetadata,
                _ => PlannedAction.UpdatePayload,
            };
        return new ChangeDecision(action, SkipTier.None, metadata, payload, $"{queued.Reason} against the version already queued, which this one replaces");
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
