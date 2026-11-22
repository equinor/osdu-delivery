using System.Collections.Concurrent;
using System.Globalization;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Planning;

/// <summary>What the record row presents to a plan beside its version: when the ingestion table last stamped it.</summary>
/// <param name="Source">The version the row carries: the ingestion fingerprint, and the business version when the flow declares one.</param>
/// <param name="RowUpdatedUtc">The record row's update moment (<c>UpdatedDate_DW</c>), the part of the fingerprint the row itself moves.</param>
/// <param name="PayloadHash">The payload hash this plan resolved, or null for a record without one.</param>
public readonly record struct ReasonSource(SourceVersion Source, DateTime? RowUpdatedUtc, string? PayloadHash);

/// <summary>
/// Builds the reason of each delivery a plan makes (osdu/docs/reference/concepts/delivery-reasons.md): every input that moved
/// between the version the ledger holds and the render the run sends, and the paths of the document that changed. Pure, and
/// fed only what planning already holds: the record's ledger state, the row, the run's render context and commit. One builder
/// serves a plan's every renderer at once.
/// </summary>
/// <remarks>
/// What moved in the rules (mapping, template, parameters, enrichments, cache) depends only on the render context the record
/// was last planned under and the run's, and a flow's records share a handful of contexts, so the comparison is made once per
/// distinct context the plan meets and kept (up to <see cref="MaxContexts"/>): a full render of fifteen thousand records parses
/// and compares a context a few times, not fifteen thousand.
/// </remarks>
public sealed class DeliveryReasons
{
    /// <summary>The most distinct earlier contexts whose comparison a plan keeps; past it each is compared where it is met.</summary>
    public const int MaxContexts = 256;

    /// <summary>The most names a trigger lists (parameters, enrichments); the rest are counted.</summary>
    public const int MaxNames = 20;

    private readonly RenderContext _context;
    private readonly string _contextText;
    private readonly string? _commit;
    private readonly FlowChange _change;
    private readonly ConcurrentDictionary<string, RuleMoves> _moves = new(StringComparer.Ordinal);

    public DeliveryReasons(RenderContext context, string? commit, FlowChange change)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(change);
        _context = context;
        _contextText = context.Canonical();
        _commit = string.IsNullOrWhiteSpace(commit) ? null : commit;
        _change = change;
    }

    /// <summary>The estate commit the run executes, when it knows it.</summary>
    public string? Commit => _commit;

    /// <summary>The cache version the run renders against.</summary>
    public string CacheVersion => _context.CacheVersion;

    /// <summary>
    /// The reason for a delivery the plan decided: <paramref name="decision"/> is the tier-2 decision, <paramref name="rendered"/>
    /// the paths of the document it sends (null for a delivery that sends no document).
    /// </summary>
    public DeliveryReason For(RecordState? state, ChangeDecision decision, ReasonSource row, RenderPaths? rendered)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new DeliveryReason
        {
            Action = DeliveryReason.ActionOf(decision.Action),
            Triggers = Triggers(state, decision, row),
            Paths = decision.DeliverMetadata && rendered is not null ? Paths(state, rendered) : null,
        };
    }

    /// <summary>
    /// Why a record whose render is identical to the document OSDU holds was rendered at all: every input the ledger compares
    /// that moved (a request, its rules, the cache, its rows), with nothing sent. Null when none moved (a plan that renders
    /// every record it reads, such as a replan), which leaves nothing to say about the record.
    /// </summary>
    public DeliveryReason? ForUnchanged(RecordState? state, ChangeDecision decision, ReasonSource row)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var moved = Moved(state, decision, row);
        return moved.Count == 0 ? null : new DeliveryReason { Action = DeliveryReason.NoneAction, Triggers = moved };
    }

    /// <summary>Every input that moved since the version the ledger holds, in <see cref="ReasonKinds.All"/>'s order; never empty.</summary>
    public IReadOnlyList<ReasonTrigger> Triggers(RecordState? state, ChangeDecision decision, ReasonSource row)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var found = Moved(state, decision, row);
        if (found.Count == 0)
        {
            found.Add(new ReasonTrigger
            {
                Kind = ReasonKinds.OtherInputChanged,
                Detail = decision.Basis.Queue == QueueComparison.ReplacesQueued
                    ? "nothing the ledger compares moved since the version OSDU holds; the render replaces work already queued for the record, which differed from it"
                    : "nothing the ledger compares moved since the version OSDU holds, and the document renders differently: the render reads something those inputs do not cover, such as a record it searches for in OSDU, its parent, or a child it links",
            });
        }

        return found;
    }

    /// <summary>The inputs the ledger compares that moved, in <see cref="ReasonKinds.All"/>'s order; empty when none did.</summary>
    private List<ReasonTrigger> Moved(RecordState? state, ChangeDecision decision, ReasonSource row)
    {
        var found = new List<ReasonTrigger>();
        if (state is null)
        {
            found.Add(new ReasonTrigger { Kind = ReasonKinds.FirstDelivery, Detail = "the ledger held no record for the key" });
        }
        else
        {
            var held = state is { Status: RecordStatus.Delivered } || state.TargetVersion is not null;
            if (!held && state.LastDeliveredUtc is null)
            {
                found.Add(new ReasonTrigger
                {
                    Kind = ReasonKinds.FirstDelivery,
                    Detail = $"the ledger held the record as {state.Status.ToString().ToLowerInvariant()}, and OSDU had never held a version of it",
                });
            }

            Requested(state, found);
            Drift(state, found);
            Source(state, row, found);
            Rules(state, found);
            Payload(state, decision, row, found);
        }

        Forced(decision, found);
        found.Sort((a, b) => ReasonKinds.Order(a.Kind).CompareTo(ReasonKinds.Order(b.Kind)));
        return found;
    }

    /// <summary>
    /// The paths of <paramref name="rendered"/> that differ from the document OSDU holds, or why they are not compared: a record
    /// OSDU holds no version of, or one whose paths the ledger did not keep.
    /// </summary>
    public static PathChanges Paths(RecordState? state, RenderPaths rendered)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        if (state is null)
        {
            return PathChanges.Without("the record is new to the ledger, so there is no earlier render to compare with");
        }

        if (state is not { Status: RecordStatus.Delivered } && state.TargetVersion is null)
        {
            return PathChanges.Without("OSDU holds no version of the record to compare with");
        }

        if (state.RenderPaths is not { } kept)
        {
            return PathChanges.Without("the ledger keeps no path hashes of the version OSDU holds: it was delivered before the ledger kept them, or a reversal or a step back put an earlier version in its place");
        }

        return RenderPaths.DecodeText(kept, out var problem) is { } before
            ? RenderPaths.Compare(before, rendered)
            : PathChanges.Without(problem!);
    }

    /// <summary>The paths of the document a plan sends, for the reason and for the ledger to keep once it lands.</summary>
    public static RenderPaths PathsOf(string canonicalDocument) => RenderPaths.Of(canonicalDocument);

    private static void Requested(RecordState state, List<ReasonTrigger> found)
    {
        if (state.PlanRequestedUtc is not { } asked)
        {
            return;
        }

        // Who asked is filled in from the audit trail by the plan (one read for the batch's deliveries); the record's note is
        // what a redelivery, a release or a cache rollout left on it, kept for a request no intervention names.
        found.Add(new ReasonTrigger
        {
            Kind = ReasonKinds.Requested,
            At = asked,
            Detail = !state.Blocked && !string.IsNullOrWhiteSpace(state.LastError) ? $"the record's note: {state.LastError}" : null,
        });
    }

    private static void Drift(RecordState state, List<ReasonTrigger> found)
    {
        var detail = state.LastVerifyOutcome switch
        {
            VerifyOutcome.Drifted => "verification found OSDU holding another version, or other content, than the ledger names",
            VerifyOutcome.Missing => "verification found OSDU holding no record at the id",
            _ => null,
        };
        if (detail is not null)
        {
            found.Add(new ReasonTrigger { Kind = ReasonKinds.Drift, Detail = detail, At = state.LastVerifiedUtc });
        }
    }

    /// <summary>
    /// The record row and what it is read with. The fingerprint covers the row's update moment, and each child dataset's count
    /// and newest update, and the update moment of each lookup row joined: when it moved while the row's own moment did not, a
    /// child or a lookup row did. A request forgets the fingerprint, and then the row's moment alone says whether the row moved.
    /// </summary>
    private static void Source(RecordState state, ReasonSource row, List<ReasonTrigger> found)
    {
        var rowMoved = state.SourceUpdatedUtc is { } was && row.RowUpdatedUtc is { } now && was != now;
        var fingerprintMoved = state.SourceFingerprint is { } kept && row.Source.Fingerprint is { } read && !string.Equals(kept, read, StringComparison.Ordinal);
        var businessMoved = state.SourceModifiedUtc is { } before && row.Source.ModifiedUtc is { } after && before != after;
        if (fingerprintMoved && !rowMoved && state.SourceUpdatedUtc is not null && row.RowUpdatedUtc is not null)
        {
            found.Add(new ReasonTrigger
            {
                Kind = ReasonKinds.ChildOrLookupChanged,
                Detail = "the record row's update moment did not move and its ingestion fingerprint did: a row of a child dataset, or a lookup row it joins, was written or marked deleted since",
            });
        }

        if (!rowMoved && !businessMoved && !(fingerprintMoved && (state.SourceUpdatedUtc is null || row.RowUpdatedUtc is null)))
        {
            return;
        }

        var said = new List<string>();
        if (rowMoved)
        {
            said.Add("the ingestion table stamped the record row again (UpdatedDate_DW); which columns changed is not kept, since the fingerprint covers the row's update moment, not its values");
        }
        else if (!businessMoved)
        {
            said.Add("the ingestion fingerprint of the record's rows moved");
        }

        if (businessMoved)
        {
            said.Add($"source.lastModified moved from {Moment(state.SourceModifiedUtc!.Value)} to {Moment(row.Source.ModifiedUtc!.Value)}");
        }

        found.Add(new ReasonTrigger
        {
            Kind = ReasonKinds.SourceChanged,
            From = rowMoved ? Moment(state.SourceUpdatedUtc!.Value) : null,
            To = rowMoved ? Moment(row.RowUpdatedUtc!.Value) : null,
            Detail = string.Join("; ", said),
        });
    }

    /// <summary>What moved in the rules: compared once per distinct context the ledger holds, then given the record's own commit.</summary>
    private void Rules(RecordState state, List<ReasonTrigger> found)
    {
        if (state.RenderContext is not { } kept || string.Equals(kept, _contextText, StringComparison.Ordinal))
        {
            return;
        }

        var moves = _moves.TryGetValue(kept, out var known) ? known : Compare(kept);
        if (known is null && _moves.Count < MaxContexts)
        {
            _moves.TryAdd(kept, moves);
        }

        foreach (var trigger in moves.Triggers)
        {
            found.Add(trigger.Kind == ReasonKinds.MappingChanged ? trigger with { FromCommit = state.RenderCommit } : trigger);
        }
    }

    private static void Payload(RecordState state, ChangeDecision decision, ReasonSource row, List<ReasonTrigger> found)
    {
        // A payload hash a redelivery forgot, or left as the marker naming the parts to send, is no version of the payload: the
        // request is what moved, and it is listed.
        if (!decision.DeliverPayload || row.PayloadHash is not { } now
            || string.Equals(state.PayloadHash, now, StringComparison.Ordinal)
            || PayloadParts.RedeliverRoles(state.PayloadHash) is not null
            || (state.PayloadHash is null && state.PlanRequestedUtc is not null))
        {
            return;
        }

        found.Add(new ReasonTrigger { Kind = ReasonKinds.PayloadChanged, From = state.PayloadHash, To = now });
    }

    private void Forced(ChangeDecision decision, List<ReasonTrigger> found)
    {
        var keys = new List<string>();
        if (_change.Detect == ChangeDetection.Always && decision.DeliverMetadata)
        {
            keys.Add("change.detect: always");
        }

        if (_change.PayloadDetect == ChangeDetection.Always && decision.DeliverPayload)
        {
            keys.Add("change.payloadDetect: always");
        }

        if (_change.OnUnchanged == UnchangedAction.Deliver)
        {
            keys.Add("change.onUnchanged: deliver");
        }

        if (keys.Count > 0)
        {
            found.Add(new ReasonTrigger { Kind = ReasonKinds.Forced, Names = keys, Detail = "the flow sends what it reads whatever the hashes say" });
        }
    }

    /// <summary>The triggers between the context <paramref name="kept"/> and the run's, or none when it cannot be read.</summary>
    private RuleMoves Compare(string kept)
    {
        RenderContext before;
        try
        {
            before = RenderContext.Parse(kept);
        }
        catch (Exception ex) when (ex is DeliveryException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            // A context the ledger cannot read names no rule: what else moved is still listed, and nothing is guessed here.
            return new RuleMoves([]);
        }

        var after = _context;
        var triggers = new List<ReasonTrigger>();
        if (!string.Equals(before.MappingReference, after.MappingReference, StringComparison.Ordinal)
            || !string.Equals(before.MappingFingerprint, after.MappingFingerprint, StringComparison.Ordinal))
        {
            triggers.Add(new ReasonTrigger
            {
                Kind = ReasonKinds.MappingChanged,
                From = before.MappingReference,
                To = after.MappingReference,
                FromFingerprint = before.MappingFingerprint,
                ToFingerprint = after.MappingFingerprint,
                ToCommit = _commit,
            });
        }

        if (!string.Equals(before.SchemaSnapshotVersion, after.SchemaSnapshotVersion, StringComparison.Ordinal))
        {
            triggers.Add(new ReasonTrigger { Kind = ReasonKinds.TemplateChanged, From = before.SchemaSnapshotVersion, To = after.SchemaSnapshotVersion });
        }

        var parameters = before.Parameters.Keys.Union(after.Parameters.Keys, StringComparer.Ordinal)
            .Where(name => !string.Equals(before.Parameters.GetValueOrDefault(name), after.Parameters.GetValueOrDefault(name), StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (!Equals(Restriction(before), Restriction(after)))
        {
            parameters.Add("restrictedAccess");
        }

        if (parameters.Count > 0)
        {
            triggers.Add(Named(ReasonKinds.ParametersChanged, parameters, "the parameter values the mapping resolves (and the restriction its restrictedAccess names) moved"));
        }

        if (Enrichments(before, after) is { } enrichment)
        {
            triggers.Add(enrichment);
        }

        var cacheMoved = !string.Equals(before.CacheVersion, after.CacheVersion, StringComparison.Ordinal);
        var scopeMoved = !string.Equals(before.CacheScope, after.CacheScope, StringComparison.Ordinal);
        var propertiesMoved = !before.SystemProperties.Select(p => $"{p.Service}/{p.Name}={p.State}")
            .SequenceEqual(after.SystemProperties.Select(p => $"{p.Service}/{p.Name}={p.State}"), StringComparer.Ordinal);
        if (cacheMoved || scopeMoved || propertiesMoved)
        {
            var said = new List<string>();
            if (scopeMoved)
            {
                said.Add($"the render reads the cache of {after.CacheScope ?? "no partition"} where it read {before.CacheScope ?? "none"}");
            }

            if (propertiesMoved)
            {
                said.Add("the partition's system properties the mapping's searches rely on moved");
            }

            triggers.Add(new ReasonTrigger
            {
                Kind = ReasonKinds.CacheChanged,
                From = cacheMoved ? before.CacheVersion : null,
                To = cacheMoved ? after.CacheVersion : null,
                Detail = said.Count > 0 ? string.Join("; ", said) : null,
            });
        }

        return new RuleMoves(triggers);
    }

    /// <summary>The enrichments that moved: added, removed, rendered otherwise, or in another order.</summary>
    private static ReasonTrigger? Enrichments(RenderContext before, RenderContext after)
    {
        var was = before.Steps.ToDictionary(s => s.Name, s => CanonicalJson.ToString(s.Node()), StringComparer.Ordinal);
        var now = after.Steps.ToDictionary(s => s.Name, s => CanonicalJson.ToString(s.Node()), StringComparer.Ordinal);
        var added = now.Keys.Where(n => !was.ContainsKey(n)).Order(StringComparer.Ordinal).ToList();
        var removed = was.Keys.Where(n => !now.ContainsKey(n)).Order(StringComparer.Ordinal).ToList();
        var changed = now.Keys.Where(n => was.TryGetValue(n, out var text) && !string.Equals(text, now[n], StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var reordered = added.Count == 0 && removed.Count == 0 && changed.Count == 0
            && !before.Steps.Select(s => s.Name).SequenceEqual(after.Steps.Select(s => s.Name), StringComparer.Ordinal);
        if (added.Count + removed.Count + changed.Count == 0 && !reordered)
        {
            return null;
        }

        var said = new List<string>();
        if (added.Count > 0)
        {
            said.Add($"added: {string.Join(", ", added)}");
        }

        if (removed.Count > 0)
        {
            said.Add($"removed: {string.Join(", ", removed)}");
        }

        if (changed.Count > 0)
        {
            said.Add($"its mapping, template or parameters moved: {string.Join(", ", changed)}");
        }

        if (reordered)
        {
            said.Add("the enrichments render in another order");
        }

        return Named(ReasonKinds.EnrichmentChanged, [.. added, .. removed, .. changed, .. reordered ? after.Steps.Select(s => s.Name) : []], string.Join("; ", said));
    }

    private static ReasonTrigger Named(string kind, IReadOnlyList<string> names, string detail)
    {
        var distinct = names.Distinct(StringComparer.Ordinal).ToList();
        return new ReasonTrigger
        {
            Kind = kind,
            Names = distinct.Take(MaxNames).ToList(),
            More = Math.Max(0, distinct.Count - MaxNames),
            Detail = detail,
        };
    }

    private static string? Restriction(RenderContext context)
        => context.RestrictedAccess is { } r
            ? string.Create(CultureInfo.InvariantCulture, $"{r.RestrictionId}|{r.Name}|{string.Join(',', r.Viewers)}|{string.Join(',', r.Owners)}")
            : null;

    private static string Moment(DateTime utc) => CanonicalJson.FormatDateTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    /// <summary>What moved in the rules between one earlier context and the run's.</summary>
    private sealed record RuleMoves(IReadOnlyList<ReasonTrigger> Triggers);
}
