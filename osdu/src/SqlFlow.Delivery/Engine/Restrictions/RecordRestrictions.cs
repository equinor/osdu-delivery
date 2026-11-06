using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Restrictions;

/// <summary>
/// Restricts records' access in OSDU, and lifts restrictions (osdu/docs/reference/concepts/access-restriction.md): the
/// record's ACL is replaced with a registered restriction's groups through the storage service's metadata patch, which keeps
/// the record and every version as they are, and lifting writes back the ACL the record had. Each record is marked in the
/// ledger before its ACL is changed, keeping the ACL it had, so a call whose answer is lost leaves it kept out of every run
/// with that ACL to give back; once OSDU answers, the restriction is confirmed with an attempt, or withdrawn when OSDU kept
/// the record's ACL. A record restricted is never delivered, released or forgotten by the ledger until it is lifted. One
/// chunk of a removal's records at a time, under the removal's hold, which each call names.
/// </summary>
public sealed class RecordRestrictions
{
    private readonly FlowDefinition _flow;
    private readonly ILedger _ledger;
    private readonly IRecordAccess? _access;
    private readonly string? _accessRefusal;
    private readonly TimeProvider _time;
    private readonly string _actor;
    private readonly Guid? _runId;
    private readonly AccessRestrictionState? _restriction;
    private readonly Dictionary<string, RestrictionRoute> _routes = new(StringComparer.Ordinal);

    /// <param name="flow">The flow whose records these are.</param>
    /// <param name="ledger">Its ledger.</param>
    /// <param name="protocol">Its route, whose <see cref="IDeliveryProtocol.Access"/> reads and replaces the records' ACL.</param>
    /// <param name="time">The clock.</param>
    /// <param name="actor">Who asked, as each record's attempt names them.</param>
    /// <param name="runId">The run that asked.</param>
    /// <param name="restriction">The restriction to restrict under; null for a lift.</param>
    public RecordRestrictions(FlowDefinition flow, ILedger ledger, IDeliveryProtocol protocol, TimeProvider time, string actor, Guid? runId, AccessRestrictionState? restriction)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        _flow = flow;
        _ledger = ledger;
        _access = protocol.Access;
        _accessRefusal = protocol.AccessRefusal;
        _time = time;
        _actor = actor;
        _runId = runId;
        _restriction = restriction;
    }

    /// <summary>Whether this restricts records (a restriction was given) or lifts their restrictions.</summary>
    public bool Lifting => _restriction is null;

    /// <summary>
    /// Checks, before anything is changed, that a restriction under <paramref name="restriction"/> could be lifted by the flow:
    /// its groups are data groups storage accepts, and the flow's identity is a member of one of its owner groups (or a data
    /// manager), since storage lets only an owner change the record again (osdu/specs/core/INTEGRATION.md, 2.8, item 3). A
    /// record restricted to groups the flow is not in could never be given its access back by the flow.
    /// </summary>
    /// <exception cref="DeliveryException">The restriction could not be lifted by the flow, or the route cannot restrict; nothing was changed.</exception>
    public static async Task EnsureLiftableAsync(FlowDefinition flow, IDeliveryProtocol protocol, AccessRestrictionState restriction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(restriction);
        var invalid = EntitlementGroups.NotDataGroups([.. restriction.Acl.Viewers, .. restriction.Acl.Owners]);
        if (invalid.Count > 0)
        {
            throw new DeliveryException(
                $"Access restriction '{restriction.Name}' names {string.Join(", ", invalid)}, which storage does not accept in an ACL (a data group's email, data.<name>@<domain>), so nothing was restricted.");
        }

        if (protocol.Access is not { } access)
        {
            throw new DeliveryException(
                $"'{flow.Label}' cannot restrict its records' access: {protocol.AccessRefusal ?? RestrictionRoute.Of(flow, null).Refusal ?? $"the {DeliveryProtocols.Name(flow.Target.Protocol)} route reaches no storage ACL"}. Nothing was restricted.");
        }

        CallerGroups caller;
        try
        {
            caller = await access.CallerGroupsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or TimeoutException or System.Text.Json.JsonException)
        {
            throw new DeliveryException(
                $"Entitlements could not say which groups '{flow.Label}''s identity is a member of ({HeaderRedaction.RedactMessage(ex.Message)}), so whether it could lift a restriction to {string.Join(", ", restriction.Acl.Owners)} is not known, and nothing was restricted.",
                ex);
        }

        if (!caller.MayChange(restriction.Acl.Owners))
        {
            throw new DeliveryException(
                $"{char.ToUpperInvariant(caller.Who[0])}{caller.Who[1..]} is a member of none of access restriction '{restriction.Name}''s owner groups ({string.Join(", ", restriction.Acl.Owners)}), nor a data manager, so a record restricted under it could never be given its access back by '{flow.Label}': storage lets only an owner change a record. Nothing was restricted; add the identity to one of those groups, then restrict again.");
        }
    }

    /// <summary>
    /// Restricts <paramref name="records"/>, each a record of the flow that claimed its OSDU id, and returns what each came to.
    /// Results align with <paramref name="records"/>.
    /// </summary>
    public async Task<IReadOnlyList<RemovalRecordResult>> RestrictAsync(IReadOnlyList<RecordState> records, string? holder, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var restriction = _restriction ?? throw new InvalidOperationException("A lift restricts nothing; restrict with a restriction.");
        var results = new RemovalRecordResult?[records.Count];

        // What the ledger alone decides: a record OSDU holds nothing of, one restricted already, one the route cannot restrict.
        var asked = new List<int>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            if (RestrictRefusal(records[i], restriction) is { } reason)
            {
                results[i] = Skipped(records[i], reason);
            }
            else
            {
                asked.Add(i);
            }
        }

        if (asked.Count == 0)
        {
            return Done(results);
        }

        if (_access is not { } access)
        {
            foreach (var i in asked)
            {
                results[i] = Failed(records[i], $"the route cannot restrict the record's access: {_accessRefusal ?? "it reaches no storage ACL"}");
            }

            return Done(results);
        }

        // What OSDU holds of each now: its ACL, which the ledger keeps to give back, unless an earlier ask kept it already.
        IReadOnlyDictionary<string, RecordAccessRead> held;
        try
        {
            held = await access.ReadAsync([.. asked.Select(i => records[i].TargetId!)], ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var why = $"OSDU could not be asked what it holds of the record, so nothing was changed: {HeaderRedaction.RedactMessage(ex.Message)}";
            foreach (var i in asked)
            {
                results[i] = Failed(records[i], why);
            }

            return Done(results);
        }

        var marking = new List<(int Index, RestrictionAsked Asked, bool Again, string Note)>(asked.Count);
        foreach (var i in asked)
        {
            var record = records[i];
            var again = record.RestrictionId == restriction.RestrictionId;
            if (!held.TryGetValue(record.TargetId!, out var read))
            {
                results[i] = again
                    ? Skipped(record, "OSDU no longer serves the record (it was removed outside this flow); it stays restricted here, so no run delivers it until the restriction is lifted")
                    : Skipped(record, "OSDU no longer serves the record (it was removed outside this flow), so it has no access to restrict");
                continue;
            }

            var from = again && record.RestrictedFromAcl is { } kept ? RecordAcl.Parse(kept) : read.Acl;
            if (!again && read.Acl.SameAs(restriction.Acl))
            {
                results[i] = Skipped(record, $"OSDU holds the record under the restriction's groups already ({read.Acl.Describe()}), set outside this restriction, so the ACL it had before cannot be told; set its ACL in OSDU, then restrict it again");
                continue;
            }

            if (from.ToJson().Length > DeliveryModel.MaxRestrictedFromAclLength)
            {
                results[i] = Skipped(record, string.Create(
                    CultureInfo.InvariantCulture,
                    $"its ACL in OSDU names more groups than the ledger keeps to give back ({from.ToJson().Length} characters, at most {DeliveryModel.MaxRestrictedFromAclLength}), so it is not restricted"));
                continue;
            }

            var note = read.Version is { } version && record.TargetVersion is { } known && version != known
                ? string.Create(CultureInfo.InvariantCulture, $"; OSDU holds version {version}, not version {known} this flow left, and the restriction covers every version")
                : string.Empty;
            marking.Add((i, new RestrictionAsked(record.DeliveryKey, from), again, note));
        }

        if (marking.Count == 0)
        {
            return Done(results);
        }

        // Kept out first, with the ACL to give back; then OSDU is asked.
        var marked = await _ledger.AskRestrictionAsync(_flow.Id, restriction, [.. marking.Select(m => m.Asked)], holder, Now, ct).ConfigureAwait(false);
        var sent = new List<(int Index, bool Again, string Note, RecordAcl From)>(marking.Count);
        foreach (var (i, ask, again, note) in marking)
        {
            if (marked.Written.Contains(records[i].DeliveryKey))
            {
                sent.Add((i, again, note, ask.From));
            }
            else
            {
                results[i] = Skipped(records[i], "the record changed in the ledger while it was read (a worker took it, or it was restricted, lifted or removed meanwhile); ask again once it has settled");
            }
        }

        if (sent.Count == 0)
        {
            return Done(results);
        }

        IReadOnlyList<AclChange> changes;
        try
        {
            changes = await access.ReplaceAsync([.. sent.Select(s => records[s.Index].TargetId!)], restriction.Acl, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Whatever OSDU did, the records stay restricted here: lifting gives each the ACL it had, and asking again finishes it.
            throw;
        }

        var confirmed = new List<RestrictionConfirmed>();
        var withdrawn = new List<DeliveryKey>();
        for (var j = 0; j < sent.Count; j++)
        {
            var (i, again, note, from) = sent[j];
            var record = records[i];
            var change = changes[j];
            switch (change.Outcome)
            {
                case AclChangeOutcome.Replaced:
                    confirmed.Add(new RestrictionConfirmed(record.DeliveryKey, $"access restricted to {restriction.Acl.Describe()} under '{restriction.Name}' by {_actor}; the version and history stay as they were{note}. No run delivers it until the restriction is lifted"));
                    break;

                case AclChangeOutcome.NotFound when !again:
                    withdrawn.Add(record.DeliveryKey);
                    results[i] = Skipped(record, "OSDU no longer serves the record (it was removed outside this flow), so it has no access to restrict");
                    break;

                case AclChangeOutcome.Refused when !again:
                    withdrawn.Add(record.DeliveryKey);
                    results[i] = Failed(record, $"OSDU kept the record's access as it was: {change.Detail}");
                    break;

                default:
                    // An earlier ask may have restricted it already, or this one may have: kept restricted here either way, since
                    // that never shows the record to anyone, and lifting gives it the ACL it had.
                    results[i] = Failed(record, $"{change.Detail}; the record is kept restricted here, so no run delivers it: restrict it again to finish, or lift the restriction to give it back the ACL it had ({from.Describe()})");
                    break;
            }
        }

        // OSDU holds these changes whether or not the run is cancelled now, so the ledger is told regardless.
        var settled = await _ledger.SettleRestrictionAsync(_flow.Id, restriction, confirmed, withdrawn, _actor, _runId, Now, correlationId, CancellationToken.None).ConfigureAwait(false);
        var byKey = confirmed.ToDictionary(c => c.Key);
        foreach (var (i, _, _, _) in sent)
        {
            var record = records[i];
            if (!byKey.TryGetValue(record.DeliveryKey, out var done))
            {
                continue;
            }

            results[i] = settled.Written.Contains(record.DeliveryKey)
                ? RemovalRecordResult.Restricted(record.DeliveryKey, record.SourceKey, record.Label, record.TargetId, done.Detail, record.LastSubmissionId)
                : Failed(record, $"OSDU holds the record under '{restriction.Name}' ({restriction.Acl.Describe()}), but the ledger's record changed while it was restricted, so it was not marked restricted; compare the record with OSDU, then restrict or lift it again");
        }

        return Done(results);
    }

    /// <summary>
    /// Lifts the restrictions of <paramref name="records"/>, writing back the ACL each had, and returns what each came to.
    /// Results align with <paramref name="records"/>.
    /// </summary>
    public async Task<IReadOnlyList<RemovalRecordResult>> LiftAsync(IReadOnlyList<RecordState> records, string? holder, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var results = new RemovalRecordResult?[records.Count];
        var restrictions = await _ledger.AccessRestrictionsAsync([.. records.Where(r => r.RestrictionId is not null).Select(r => r.RestrictionId!.Value)], ct).ConfigureAwait(false);
        var lifting = new List<(int Index, RestrictionLifted Lift)>();
        var asked = new List<(int Index, RecordAcl Acl)>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.RestrictionId is not { } restrictionId)
            {
                results[i] = Skipped(record, "the record's access is not restricted");
                continue;
            }

            var name = restrictions.TryGetValue(restrictionId, out var restriction) ? restriction.Name : restrictionId.ToString(CultureInfo.InvariantCulture);
            if (record.Status == RecordStatus.Deleted)
            {
                lifting.Add((i, new RestrictionLifted(record.DeliveryKey, $"restriction '{name}' lifted by {_actor}; the record was removed from OSDU, so nothing was asked of it, and it follows its source again")));
                continue;
            }

            if (record.RestrictedFromAcl is not { } from)
            {
                results[i] = Failed(record, $"the ledger keeps no ACL the record had before restriction '{name}', so there is nothing to give back; set its ACL in OSDU, then lift the restriction again");
                continue;
            }

            if (Route(record.TargetId!).Refusal is { } refusal)
            {
                results[i] = Failed(record, $"the route cannot give the record its access back: {refusal}");
                continue;
            }

            asked.Add((i, RecordAcl.Parse(from)));
        }

        if (asked.Count > 0 && _access is not { } access)
        {
            foreach (var (i, _) in asked)
            {
                results[i] = Failed(records[i], $"the route cannot give the record its access back: {_accessRefusal ?? "it reaches no storage ACL"}");
            }
        }
        else if (asked.Count > 0)
        {
            // One patch per ACL to give back: a flow's records mostly had the same one.
            foreach (var group in asked.GroupBy(a => a.Acl.ToJson(), StringComparer.Ordinal))
            {
                var members = group.ToList();
                IReadOnlyList<AclChange> changes;
                try
                {
                    changes = await _access!.ReplaceAsync([.. members.Select(m => records[m.Index].TargetId!)], members[0].Acl, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }

                for (var j = 0; j < members.Count; j++)
                {
                    var (i, acl) = members[j];
                    var record = records[i];
                    var name = restrictions.TryGetValue(record.RestrictionId!.Value, out var restriction) ? restriction.Name : record.RestrictionId!.Value.ToString(CultureInfo.InvariantCulture);
                    switch (changes[j].Outcome)
                    {
                        case AclChangeOutcome.Replaced:
                            lifting.Add((i, new RestrictionLifted(record.DeliveryKey, $"restriction '{name}' lifted by {_actor}: access given back ({acl.Describe()}); the record follows its source again")));
                            break;

                        case AclChangeOutcome.NotFound:
                            lifting.Add((i, new RestrictionLifted(
                                record.DeliveryKey,
                                $"restriction '{name}' lifted by {_actor}; OSDU no longer holds the record, so the flow's next run sends it again under its id",
                                Lost: true)));
                            break;

                        default:
                            results[i] = Failed(record, $"{changes[j].Detail}; the record stays restricted, so no run delivers it: lift it again once that is settled");
                            break;
                    }
                }
            }
        }

        if (lifting.Count > 0)
        {
            // OSDU holds what it was given back whether or not the run is cancelled now, so the ledger is told regardless.
            var written = await _ledger.LiftRestrictionAsync(_flow.Id, [.. lifting.Select(l => l.Lift)], restrictions, holder, _actor, _runId, Now, correlationId, CancellationToken.None).ConfigureAwait(false);
            foreach (var (i, lift) in lifting)
            {
                var record = records[i];
                results[i] = written.Written.Contains(record.DeliveryKey)
                    ? RemovalRecordResult.Lifted(record.DeliveryKey, record.SourceKey, record.Label, record.TargetId, lift.Detail, record.LastSubmissionId)
                    : Failed(record, "the record changed in the ledger while its restriction was lifted (a worker took it, or it was lifted meanwhile), so the ledger was left as it is; compare the record with OSDU, then lift it again");
            }
        }

        return Done(results);
    }

    /// <summary>Why the ledger alone says <paramref name="record"/> cannot be restricted under <paramref name="restriction"/>, or null when OSDU is to be asked.</summary>
    private string? RestrictRefusal(RecordState record, AccessRestrictionState restriction)
    {
        if (record.Status == RecordStatus.Deleted)
        {
            return "the record was removed from OSDU, so it has no access to restrict";
        }

        if (record.TargetVersion is null)
        {
            return "OSDU holds no version of the record this flow delivered (it was never delivered), so it has no access to restrict";
        }

        if (record.RestrictionId is { } current)
        {
            if (current != restriction.RestrictionId)
            {
                return $"the record is restricted under another restriction already; lift it, then restrict the record under '{restriction.Name}'";
            }

            if (record.RestrictedUtc is { } since)
            {
                return string.Create(CultureInfo.InvariantCulture, $"the record is restricted under '{restriction.Name}' already, since {since:u}");
            }
        }

        return Route(record.TargetId!).Refusal;
    }

    /// <summary>What the route can do for the record <paramref name="targetId"/>, by the entity type it names; worked out once per type.</summary>
    private RestrictionRoute Route(string targetId)
    {
        var type = DdmsRouting.EntityTypeOf(targetId) ?? string.Empty;
        if (!_routes.TryGetValue(type, out var route))
        {
            route = RestrictionRoute.ForRecord(_flow, targetId);
            _routes[type] = route;
        }

        return route;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static IReadOnlyList<RemovalRecordResult> Done(RemovalRecordResult?[] results) => [.. results.Select(r => r!)];

    private static RemovalRecordResult Skipped(RecordState record, string detail)
        => RemovalRecordResult.Skipped(record.DeliveryKey, record.SourceKey, record.Label, record.TargetId, detail, record.LastSubmissionId);

    private static RemovalRecordResult Failed(RecordState record, string detail)
        => RemovalRecordResult.Failed(record.DeliveryKey, record.SourceKey, record.Label, record.TargetId, HeaderRedaction.RedactMessage(detail), record.LastSubmissionId);
}
