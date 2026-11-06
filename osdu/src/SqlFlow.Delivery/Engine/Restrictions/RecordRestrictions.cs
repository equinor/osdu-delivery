using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Restrictions;

/// <summary>Whether a flow can restrict its records under one registered restriction, and why not when it cannot.</summary>
/// <param name="Restriction">The registered restriction.</param>
/// <param name="Refusal">Why a restriction under it would be refused before anything changes, or null when the flow can restrict under it and lift it again.</param>
public sealed record RestrictionUse(AccessRestrictionState Restriction, string? Refusal)
{
    /// <summary>Whether the flow can restrict under the restriction and lift it again.</summary>
    public bool Usable => Refusal is null;
}

/// <summary>
/// What a check of the restrictions a flow could restrict its records under found (<see cref="RecordRestrictions.CheckAsync"/>):
/// the flow's identity as Entitlements named it (null when it was not asked), why the flow restricts nothing at all (its route,
/// or what it runs beside the route), and each restriction with why a restriction under it would be refused.
/// </summary>
public sealed record RestrictionCheck(string? Identity, string? Refusal, IReadOnlyList<RestrictionUse> Uses);

/// <summary>
/// Restricts records' access in OSDU, and lifts restrictions (osdu/docs/reference/concepts/access-restriction.md): the ACL of
/// the record and of each record OSDU holds beside it that its target state names (the records the mapping's enrichments add,
/// <see cref="IDeliveryProtocol.AccessBeside"/>) is replaced with a registered restriction's groups through the storage
/// service's metadata patch, which keeps every record and version as they are, and lifting writes back the ACL each had. Each
/// record is marked in the ledger before an ACL is changed, keeping the ACL it and the records beside it had, so a call whose
/// answer is lost leaves it kept out of every run with those ACLs to give back; once OSDU answers, the restriction is
/// confirmed with an attempt, or withdrawn when OSDU changed none of them (a restriction OSDU took for some of them and
/// refused for others is given back first, so a record is restricted whole or not at all). A record restricted is never
/// delivered, released or forgotten by the ledger until it is lifted. One chunk of a removal's records at a time, under the
/// removal's hold, which each call names.
/// </summary>
public sealed class RecordRestrictions
{
    private readonly FlowDefinition _flow;
    private readonly ILedger _ledger;
    private readonly IDeliveryProtocol _protocol;
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
        _protocol = protocol;
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
    /// Says, for each of <paramref name="restrictions"/>, whether the flow could restrict its records under it and lift it
    /// again, asking nothing of OSDU but the flow's own groups: the flow's route reaches storage ACLs alone
    /// (<see cref="RestrictionRoute"/>), the restriction's groups are data groups storage accepts, and the flow's identity is a
    /// member of one of its owner groups (or a data manager), since storage lets only an owner change the record again
    /// (osdu/specs/core/INTEGRATION.md, 2.8, item 3). The removal dialog asks it before a restriction is queued, and the run
    /// asks it again before anything is changed (<see cref="EnsureLiftableAsync"/>), so both refuse with the same words.
    /// </summary>
    public static async Task<RestrictionCheck> CheckAsync(FlowDefinition flow, IDeliveryProtocol protocol, IReadOnlyList<AccessRestrictionState> restrictions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(restrictions);
        if (FlowRefusal(flow, protocol) is { } refusal)
        {
            return new RestrictionCheck(null, refusal, [.. restrictions.Select(r => new RestrictionUse(r, refusal))]);
        }

        var uses = new RestrictionUse?[restrictions.Count];
        for (var i = 0; i < restrictions.Count; i++)
        {
            var restriction = restrictions[i];
            var invalid = EntitlementGroups.NotDataGroups([.. restriction.Acl.Viewers, .. restriction.Acl.Owners]);
            if (invalid.Count > 0)
            {
                uses[i] = new RestrictionUse(
                    restriction,
                    $"access restriction '{restriction.Name}' names {string.Join(", ", invalid)}, which storage does not accept in an ACL (a data group's email, data.<name>@<domain>)");
            }
        }

        if (uses.All(u => u is not null))
        {
            return new RestrictionCheck(null, null, [.. uses.Select(u => u!)]);
        }

        CallerGroups caller;
        try
        {
            caller = await protocol.Access!.CallerGroupsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowException or HttpRequestException or IOException or TimeoutException or System.Text.Json.JsonException)
        {
            var why = HeaderRedaction.RedactMessage(ex.Message);
            return new RestrictionCheck(null, null, [.. restrictions.Select((r, i) => uses[i] ?? new RestrictionUse(
                r,
                $"Entitlements could not say which groups '{flow.Label}''s identity is a member of ({why}), so whether it could lift a restriction to {string.Join(", ", r.Acl.Owners)} is not known"))]);
        }

        return new RestrictionCheck(caller.MemberEmail, null, [.. restrictions.Select((r, i) => uses[i] ?? new RestrictionUse(
            r,
            caller.MayChange(r.Acl.Owners)
                ? null
                : $"{caller.Who} is a member of none of access restriction '{r.Name}''s owner groups ({string.Join(", ", r.Acl.Owners)}), nor a data manager, so a record restricted under it could never be given its access back by '{flow.Label}': storage lets only an owner change a record; add the identity to one of those groups, then restrict again"))]);
    }

    /// <summary>
    /// Checks, before anything is changed, that a restriction under <paramref name="restriction"/> could be lifted by the flow
    /// (<see cref="CheckAsync"/>). A record restricted to groups the flow is not in could never be given its access back by the flow.
    /// </summary>
    /// <exception cref="DeliveryException">The restriction could not be lifted by the flow, or the route cannot restrict; nothing was changed.</exception>
    public static async Task EnsureLiftableAsync(FlowDefinition flow, IDeliveryProtocol protocol, AccessRestrictionState restriction, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(restriction);
        var check = await CheckAsync(flow, protocol, [restriction], ct).ConfigureAwait(false);
        if ((check.Refusal ?? check.Uses[0].Refusal) is { } reason)
        {
            throw new DeliveryException($"{char.ToUpperInvariant(reason[0])}{reason[1..]}. Nothing was restricted.");
        }
    }

    /// <summary>Why the flow restricts no record at all, whatever the restriction: its route reaches no storage ACL, or it runs what a restriction cannot reach beside it.</summary>
    private static string? FlowRefusal(FlowDefinition flow, IDeliveryProtocol protocol)
    {
        if (protocol.Access is null)
        {
            return $"'{flow.Label}' cannot restrict its records' access: {protocol.AccessRefusal ?? RestrictionRoute.Of(flow, null).Refusal ?? $"the {DeliveryProtocols.Name(flow.Target.Protocol)} route reaches no storage ACL"}";
        }

        return RestrictionRoute.BesideRefusal(flow) is { } beside ? $"'{flow.Label}' cannot restrict its records' access: {beside}" : null;
    }

    /// <summary>
    /// Restricts <paramref name="records"/>, each a record of the flow that claimed its OSDU id, with the records beside each,
    /// and returns what each came to. Results align with <paramref name="records"/>.
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

        // The records beside each that the restriction reaches with it: those an earlier ask of this restriction kept, when one
        // left the record asked, else those its target state names now.
        var earlier = new Dictionary<int, RestrictedAccess>();
        var beside = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var i in asked)
        {
            var record = records[i];
            if (record.RestrictionId == restriction.RestrictionId && record.RestrictedFromAcl is { } text)
            {
                try
                {
                    earlier[i] = RestrictedAccess.Parse(text);
                }
                catch (DeliveryException ex)
                {
                    results[i] = Failed(record, $"the access the ledger kept for the record does not read ({ex.Message}); set its ACL in OSDU, then lift the restriction");
                    continue;
                }
            }

            beside[i] = earlier.TryGetValue(i, out var kept) ? kept.BesideIds : _protocol.AccessBeside(record.TargetId!, JsonMerge.ToValues(record.TargetStateJson));
        }

        // What OSDU holds of each now and of the records beside it: their ACLs, which the ledger keeps to give back.
        RecordAccessReads held;
        try
        {
            held = await access.ReadAsync([.. beside.SelectMany(b => Ids(records[b.Key].TargetId!, b.Value)).Distinct(StringComparer.Ordinal)], ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var why = $"OSDU could not be asked what it holds of the record, so nothing was changed: {HeaderRedaction.RedactMessage(ex.Message)}";
            foreach (var i in beside.Keys)
            {
                results[i] = Failed(records[i], why);
            }

            return Done(results);
        }

        var marking = new List<(int Index, RestrictionAsked Asked, bool Again, string Note)>(beside.Count);
        foreach (var (i, besideIds) in beside)
        {
            var record = records[i];
            var again = record.RestrictionId == restriction.RestrictionId;
            var answer = held.AnswerFor(record.TargetId!);
            if (answer == RecordAccessAnswer.NotFound)
            {
                results[i] = again
                    ? Skipped(record, "OSDU no longer serves the record (storage names it under invalidRecords: it was removed outside this flow); it stays restricted here, so no run delivers it until the restriction is lifted")
                    : Skipped(record, "OSDU no longer serves the record (storage names it under invalidRecords: it was removed outside this flow), so it has no access to restrict");
                continue;
            }

            if (answer != RecordAccessAnswer.Held)
            {
                results[i] = Failed(record, $"OSDU did not say which ACL the record holds ({RecordAccessReads.Describe(answer)}), so nothing was changed; {Remedy(answer)}");
                continue;
            }

            var read = held.Held[record.TargetId!];

            if (!again && read.Acl.SameAs(restriction.Acl))
            {
                results[i] = Skipped(record, $"OSDU holds the record under the restriction's groups already ({read.Acl.Describe()}), set outside this restriction, so the ACL it had before cannot be told; set its ACL in OSDU, then restrict it again");
                continue;
            }

            var note = string.Empty;
            RestrictedAccess from;
            if (earlier.TryGetValue(i, out var kept))
            {
                from = kept;
            }
            else
            {
                var reached = new List<(string Id, RecordAcl Acl)>(besideIds.Count);
                var unserved = 0;
                var restrictedAlready = 0;
                var unread = new List<RecordAccessAnswer>();
                foreach (var id in besideIds)
                {
                    var besideAnswer = held.AnswerFor(id);
                    if (besideAnswer == RecordAccessAnswer.NotFound)
                    {
                        unserved++;
                    }
                    else if (besideAnswer != RecordAccessAnswer.Held)
                    {
                        unread.Add(besideAnswer);
                    }
                    else if (held.Held[id].Acl.SameAs(restriction.Acl))
                    {
                        restrictedAlready++;
                    }
                    else
                    {
                        reached.Add((id, held.Held[id].Acl));
                    }
                }

                // A record beside it whose ACL is not known cannot be restricted with it, and restricting the record alone would
                // leave that one readable: whole or not at all.
                if (unread.Count > 0)
                {
                    var why = string.Join("; ", unread.GroupBy(a => a).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Count()}: {RecordAccessReads.Describe(g.Key)}")));
                    results[i] = Failed(record, string.Create(
                        CultureInfo.InvariantCulture,
                        $"OSDU did not say which ACL {unread.Count} of the {besideIds.Count} record(s) its enrichments add beside it hold ({why}), and restricting the record alone would leave them readable, so nothing was changed; {Remedy(unread[0])}"));
                    continue;
                }

                from = RestrictedAccess.Of(read.Acl, reached);
                if (unserved > 0)
                {
                    note += string.Create(CultureInfo.InvariantCulture, $"; {unserved} record(s) beside it OSDU no longer serves (storage names them under invalidRecords: removed outside this flow) were left as they are");
                }

                if (restrictedAlready > 0)
                {
                    note += string.Create(CultureInfo.InvariantCulture, $"; {restrictedAlready} record(s) beside it held the restriction's groups already, set outside this restriction, and were left as they are");
                }
            }

            var json = from.ToJson();
            if (json.Length > DeliveryModel.MaxRestrictedFromAclLength)
            {
                results[i] = Skipped(record, string.Create(
                    CultureInfo.InvariantCulture,
                    $"its ACL in OSDU and those of the {from.BesideIds.Count} record(s) beside it take more than the ledger keeps to give back ({json.Length} characters, at most {DeliveryModel.MaxRestrictedFromAclLength}), so it is not restricted; remove the record instead"));
                continue;
            }

            if (read.Version is { } version && record.TargetVersion is { } known && version != known)
            {
                note += string.Create(CultureInfo.InvariantCulture, $"; OSDU holds version {version}, not version {known} this flow left, and the restriction covers every version");
            }

            marking.Add((i, new RestrictionAsked(record.DeliveryKey, from), again, note));
        }

        if (marking.Count == 0)
        {
            return Done(results);
        }

        // Kept out first, with the ACLs to give back; then OSDU is asked.
        var marked = await _ledger.AskRestrictionAsync(_flow.Id, restriction, [.. marking.Select(m => m.Asked)], holder, Now, ct).ConfigureAwait(false);
        var sent = new List<(int Index, bool Again, string Note, RestrictedAccess From)>(marking.Count);
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

        // One patch for every record and every record beside one: storage takes up to 500 ids a request.
        var patched = sent.SelectMany(s => Ids(records[s.Index].TargetId!, s.From.BesideIds)).Distinct(StringComparer.Ordinal).ToList();
        IReadOnlyDictionary<string, AclChange> changes;
        try
        {
            changes = ById(patched, await access.ReplaceAsync(patched, restriction.Acl, ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Whatever OSDU did, the records stay restricted here: lifting gives each the ACL it had, and asking again finishes it.
            throw;
        }

        var confirmed = new List<RestrictionConfirmed>();
        var withdrawn = new List<DeliveryKey>();
        foreach (var (i, again, note, from) in sent)
        {
            var record = records[i];
            var own = changes[record.TargetId!];
            var others = from.BesideIds.Select(id => changes[id]).ToList();
            var gone = others.Count(c => c.Outcome == AclChangeOutcome.NotFound);
            if (own.Outcome == AclChangeOutcome.Replaced && others.All(c => c.Outcome is AclChangeOutcome.Replaced or AclChangeOutcome.NotFound))
            {
                confirmed.Add(new RestrictionConfirmed(
                    record.DeliveryKey,
                    $"access restricted to {restriction.Acl.Describe()} under '{restriction.Name}' by {_actor}{BesideReached(others.Count - gone, gone)}; the version and history stay as they were{note}. No run delivers it until the restriction is lifted"));
                continue;
            }

            var problems = string.Join("; ", Problems(own, others));
            var replaced = Replaced(record.TargetId!, own, from, changes);
            if (!again && replaced.Count == 0 && own.Outcome != AclChangeOutcome.Unknown && others.All(c => c.Outcome != AclChangeOutcome.Unknown))
            {
                // OSDU changed none of them: the restriction is withdrawn and the record follows its source again.
                withdrawn.Add(record.DeliveryKey);
                results[i] = own.Outcome == AclChangeOutcome.NotFound
                    ? Skipped(record, "OSDU no longer serves the record (it was removed outside this flow), so it has no access to restrict")
                    : Failed(record, $"OSDU kept the record's access as it was: {problems}");
                continue;
            }

            if (!again && others.Count > 0 && own.Outcome != AclChangeOutcome.Unknown && others.All(c => c.Outcome != AclChangeOutcome.Unknown)
                && await GiveBackAsync(access, replaced, ct).ConfigureAwait(false))
            {
                // OSDU took it for some and refused it for others: what it took is given back, so the record is restricted whole
                // or not at all, and the restriction is withdrawn.
                withdrawn.Add(record.DeliveryKey);
                results[i] = Failed(record, $"OSDU refused the restriction for part of the record ({problems}), so the ACL of the {replaced.Count} it took was given back and the record's access is as it was");
                continue;
            }

            // An earlier ask may have restricted it already, or this one may have: kept restricted here either way, since that never
            // shows the record to anyone, and lifting gives each the ACL it had.
            results[i] = Failed(record, from.Beside.Count == 0
                ? $"{problems}; the record is kept restricted here, so no run delivers it: restrict it again to finish, or lift the restriction to give it back the ACL it had ({from.Describe()})"
                : $"{problems}; the record is kept restricted here, so no run delivers it: restrict it again to finish, or lift the restriction to give it and the records beside it back the ACL each had ({from.Describe()})");
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
    /// Lifts the restrictions of <paramref name="records"/>, writing back the ACL each had and each record beside it had, and
    /// returns what each came to. Results align with <paramref name="records"/>.
    /// </summary>
    public async Task<IReadOnlyList<RemovalRecordResult>> LiftAsync(IReadOnlyList<RecordState> records, string? holder, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var results = new RemovalRecordResult?[records.Count];
        var restrictions = await _ledger.AccessRestrictionsAsync([.. records.Where(r => r.RestrictionId is not null).Select(r => r.RestrictionId!.Value)], ct).ConfigureAwait(false);
        var lifting = new List<(int Index, RestrictionLifted Lift)>();
        var asked = new List<(int Index, RestrictedAccess From, bool Removed, IReadOnlyList<(string Id, RecordAcl Acl)> GivenBack)>();
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.RestrictionId is not { } restrictionId)
            {
                results[i] = Skipped(record, "the record's access is not restricted");
                continue;
            }

            var name = NameOf(restrictions, restrictionId);
            var removed = record.Status == RecordStatus.Deleted;
            if (record.RestrictedFromAcl is not { } kept)
            {
                if (removed)
                {
                    lifting.Add((i, new RestrictionLifted(record.DeliveryKey, $"restriction '{name}' lifted by {_actor}; the record was removed from OSDU, so nothing was asked of it, and it follows its source again")));
                }
                else
                {
                    results[i] = Failed(record, $"the ledger keeps no ACL the record had before restriction '{name}', so there is nothing to give back; set its ACL in OSDU, then lift the restriction again");
                }

                continue;
            }

            RestrictedAccess from;
            try
            {
                from = RestrictedAccess.Parse(kept);
            }
            catch (DeliveryException ex)
            {
                results[i] = Failed(record, $"the access the ledger kept for the record before restriction '{name}' does not read ({ex.Message}), so there is nothing to give back; set its ACL in OSDU, then lift the restriction again");
                continue;
            }

            // A record removed from OSDU is asked nothing; the records beside it, which a removal of the record alone leaves in
            // OSDU, are given their access back.
            IReadOnlyList<(string Id, RecordAcl Acl)> givenBack = removed ? from.BesideAcls : [(record.TargetId!, from.Record), .. from.BesideAcls];
            if (givenBack.Count == 0)
            {
                lifting.Add((i, new RestrictionLifted(record.DeliveryKey, $"restriction '{name}' lifted by {_actor}; the record was removed from OSDU, so nothing was asked of it, and it follows its source again")));
                continue;
            }

            if (Route(record.TargetId!).Refusal is { } refusal)
            {
                results[i] = Failed(record, $"the route cannot give the record its access back: {refusal}");
                continue;
            }

            asked.Add((i, from, removed, givenBack));
        }

        if (asked.Count > 0 && _access is not { } access)
        {
            foreach (var (i, _, _, _) in asked)
            {
                results[i] = Failed(records[i], $"the route cannot give the record its access back: {_accessRefusal ?? "it reaches no storage ACL"}");
            }
        }
        else if (asked.Count > 0)
        {
            // One patch per ACL to give back: a flow's records, and the records beside them, mostly had the same one.
            var changes = new Dictionary<string, AclChange>(StringComparer.Ordinal);
            foreach (var group in asked.SelectMany(a => a.GivenBack).GroupBy(g => g.Acl.ToJson(), StringComparer.Ordinal))
            {
                var ids = group.Select(g => g.Id).Distinct(StringComparer.Ordinal).ToList();
                try
                {
                    foreach (var (id, change) in ById(ids, await _access!.ReplaceAsync(ids, group.First().Acl, ct).ConfigureAwait(false)))
                    {
                        changes[id] = change;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
            }

            foreach (var (i, from, removed, _) in asked)
            {
                var record = records[i];
                var name = NameOf(restrictions, record.RestrictionId!.Value);
                var others = from.BesideIds.Select(id => changes[id]).ToList();
                var own = removed ? null : changes[record.TargetId!];
                if (others.Any(c => c.Outcome is not (AclChangeOutcome.Replaced or AclChangeOutcome.NotFound))
                    || own is { Outcome: not (AclChangeOutcome.Replaced or AclChangeOutcome.NotFound) })
                {
                    results[i] = Failed(record, $"{string.Join("; ", Problems(own, others))}; the record stays restricted, so no run delivers it: lift it again once that is settled");
                    continue;
                }

                var beside = BesideGivenBack(others.Count(c => c.Outcome == AclChangeOutcome.Replaced), others.Count(c => c.Outcome == AclChangeOutcome.NotFound));
                lifting.Add(own switch
                {
                    null => (i, new RestrictionLifted(record.DeliveryKey, $"restriction '{name}' lifted by {_actor}; the record was removed from OSDU, so nothing was asked of it{beside}, and it follows its source again")),
                    { Outcome: AclChangeOutcome.Replaced } => (i, new RestrictionLifted(record.DeliveryKey, $"restriction '{name}' lifted by {_actor}: access given back ({from.Record.Describe()}){beside}; the record follows its source again")),
                    _ => (i, new RestrictionLifted(
                        record.DeliveryKey,
                        $"restriction '{name}' lifted by {_actor}; OSDU no longer holds the record{beside}, so the flow's next run sends it again under its id",
                        Lost: true)),
                });
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

    /// <summary>
    /// Gives back the ACL each of <paramref name="replaced"/> had, one patch per ACL; true when OSDU took every one, or no longer
    /// holds the record.
    /// </summary>
    private static async Task<bool> GiveBackAsync(IRecordAccess access, IReadOnlyList<(string Id, RecordAcl Acl)> replaced, CancellationToken ct)
    {
        foreach (var group in replaced.GroupBy(r => r.Acl.ToJson(), StringComparer.Ordinal))
        {
            var changes = await access.ReplaceAsync([.. group.Select(g => g.Id)], group.First().Acl, ct).ConfigureAwait(false);
            if (changes.Any(c => c.Outcome is not (AclChangeOutcome.Replaced or AclChangeOutcome.NotFound)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Of the record and the records beside it, those whose ACL OSDU replaced, each with the ACL it had.</summary>
    private static List<(string Id, RecordAcl Acl)> Replaced(string targetId, AclChange own, RestrictedAccess from, IReadOnlyDictionary<string, AclChange> changes)
    {
        var replaced = new List<(string Id, RecordAcl Acl)>();
        if (own.Outcome == AclChangeOutcome.Replaced)
        {
            replaced.Add((targetId, from.Record));
        }

        replaced.AddRange(from.BesideAcls.Where(b => changes[b.Id].Outcome == AclChangeOutcome.Replaced));
        return replaced;
    }

    /// <summary>What went wrong for the record and the records beside it, each named by its id when there are several.</summary>
    private static IEnumerable<string> Problems(AclChange? own, IReadOnlyList<AclChange> others)
    {
        if (own is { Outcome: not AclChangeOutcome.Replaced })
        {
            yield return others.Count == 0 ? own.Detail : $"the record: {own.Detail}";
        }

        foreach (var other in others.Where(c => c.Outcome is not (AclChangeOutcome.Replaced or AclChangeOutcome.NotFound)))
        {
            yield return $"{other.TargetId} beside it: {other.Detail}";
        }
    }

    /// <summary>What a confirmed restriction says of the records beside the record it reached.</summary>
    private static string BesideReached(int reached, int gone)
    {
        var text = reached > 0 ? string.Create(CultureInfo.InvariantCulture, $", with the {reached} record(s) its enrichments add beside it") : string.Empty;
        return gone > 0 ? text + string.Create(CultureInfo.InvariantCulture, $" ({gone} beside it OSDU no longer holds)") : text;
    }

    /// <summary>What a lift says of the records beside the record it gave their access back.</summary>
    private static string BesideGivenBack(int givenBack, int gone)
    {
        var text = givenBack > 0 ? string.Create(CultureInfo.InvariantCulture, $", and the {givenBack} record(s) beside it were given back their access") : string.Empty;
        return gone > 0 ? text + string.Create(CultureInfo.InvariantCulture, $" ({gone} beside it OSDU no longer holds)") : text;
    }

    /// <summary>The record and the records beside it, the record first.</summary>
    private static IEnumerable<string> Ids(string targetId, IReadOnlyList<string> beside) => [targetId, .. beside];

    /// <summary>Each change of a patch by the id it is for; results align with <paramref name="ids"/>.</summary>
    private static Dictionary<string, AclChange> ById(IReadOnlyList<string> ids, IReadOnlyList<AclChange> changes)
    {
        if (changes.Count != ids.Count)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"The ACL patch answered {changes.Count} result(s) for {ids.Count} record(s), so which records it changed cannot be told."));
        }

        var byId = new Dictionary<string, AclChange>(StringComparer.Ordinal);
        for (var i = 0; i < ids.Count; i++)
        {
            byId[ids[i]] = changes[i];
        }

        return byId;
    }

    private static string NameOf(IReadOnlyDictionary<int, AccessRestrictionState> restrictions, int restrictionId)
        => restrictions.TryGetValue(restrictionId, out var restriction) ? restriction.Name : restrictionId.ToString(CultureInfo.InvariantCulture);

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

    /// <summary>What settles a record storage did not answer with its ACL, so asking again restricts it.</summary>
    private static string Remedy(RecordAccessAnswer answer) => answer switch
    {
        RecordAccessAnswer.Unanswered => "ask again once storage answers for it",
        RecordAccessAnswer.Withheld => "add the flow's identity to an owner group of its ACL in OSDU (or make it a data manager), then restrict it again",
        RecordAccessAnswer.NoAcl => "set its ACL in OSDU, then restrict it again",
        _ => throw new ArgumentOutOfRangeException(nameof(answer), answer, "Only a record storage did not answer with its ACL has a remedy."),
    };
}
