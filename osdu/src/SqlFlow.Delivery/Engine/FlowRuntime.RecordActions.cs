using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Verify;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine;

// What an operator asks of a flow's records in the ledger, each under an activity that names the records: the verify
// pass against what OSDU holds, the sync with the ingestion tables, a release and an adoption (the steps of
// FlowRuntime.Interventions.cs), a redelivery, new OSDU ids for records OSDU lost, and bringing records up to date;
// and how such an action names its records in its activity and its events.
public sealed partial class FlowRuntime
{
    public async Task<Verifier> VerifierAsync(CancellationToken ct = default)
        => new(RequireLedger(), await ProtocolAsync(ct).ConfigureAwait(false), Flow, _context.Time, _context.Listener, _context.Loggers.CreateLogger<Verifier>())
        {
            Trace = _context.Trace,
        };

    /// <summary>
    /// The drift pass: the <c>verify</c> operation. With <paramref name="reconcile"/>, each record it marks for redelivery is
    /// named under the pass's activity, so the record's history shows which verify asked for it to be sent again.
    /// </summary>
    public Task<VerifySummary> VerifyAsync(int max, TimeSpan? notVerifiedWithin, bool reconcile, IReadOnlyList<DeliveryKey>? keys = null, CancellationToken ct = default)
        => TrackAsync("verify", new { max, notVerifiedWithin, reconcile, keys = keys?.Select(k => k.ToString()).ToList() }, keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var verifier = await VerifierAsync(ct).ConfigureAwait(false);
            var summary = await verifier.RunAsync(max, notVerifiedWithin, reconcile, keys, activity, ct).ConfigureAwait(false);
            return (summary, summary.ToString(), (Guid?)null, summary.Idle);
        }, ct);

    /// <summary>
    /// Consolidates the ledger with the ingestion tables for <paramref name="keys"/>, or for every record of the flow when
    /// it is null (<see cref="SourceSync"/>): each row read by its stored key in the scope its record was planned under,
    /// its arrival recorded, a change the ledger never saw asked to be planned by the next run, and a row that is gone
    /// put on its record's history. Nothing is rendered and nothing reaches OSDU.
    /// </summary>
    public Task<SourceSyncSummary> SyncAsync(IReadOnlyList<DeliveryKey>? keys, CancellationToken ct = default)
        => TrackAsync("sync", new { keys = keys?.Select(k => k.ToString()).ToList() }, keys is { Count: 1 } ? keys[0] : null, async () =>
        {
            var sync = new SourceSync(_context, Flow, RequireLedger(), Parameters, RunId, _context.Loggers.CreateLogger<SourceSync>());
            var summary = await sync.RunAsync(keys, ct).ConfigureAwait(false);
            return (summary, summary.ToString(), (Guid?)null, false);
        }, ct);

    /// <summary>
    /// The step of a release by key or of every blocked record (<see cref="ReleaseAsync(RecordReach, CancellationToken)"/>):
    /// releases held, failed, deleted or reverted records, all of them when <paramref name="keys"/> is null.
    /// </summary>
    private Task<int> ReleaseNamedAsync(IReadOnlyList<DeliveryKey>? keys, CancellationToken ct)
        => TrackAsync("release", new { keys = keys?.Select(k => k.ToString()).ToList() }, keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var selection = keys is null ? ReleaseSelection.EveryBlocked : ReleaseSelection.Named(keys);
            var released = await RequireLedger().ReleaseAsync(Flow.Id, selection, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.released", keys, $"released by {Actor}", "all blocked records", ct).ConfigureAwait(false);
            return (released, $"released {released} record(s)", (Guid?)null, false);
        }, ct);

    /// <summary>
    /// Releases every record one issue keeps blocked (osdu/docs/reference/concepts/record-lifecycle.md, Issues), after
    /// its cause was fixed: every record blocked by it, however many. The activity names the issue
    /// and its pattern, and every record it releases is named under the activity, so each record's history shows the
    /// release. <paramref name="pattern"/> is what the operator was shown, recorded as they saw it.
    /// </summary>
    private Task<int> ReleaseProblemAsync(long problem, string pattern, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var named = ProblemSignature.Format(problem);
        return TrackAsync("release", new { issue = named, pattern }, null, async activity =>
        {
            var released = await RequireLedger().ReleaseAsync(Flow.Id, ReleaseSelection.OfProblem(problem), activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.released", null, $"released by {Actor}", $"the records issue {named} kept blocked", ct).ConfigureAwait(false);
            return (released, $"released {released} record(s) issue {named} kept blocked: {pattern}", (Guid?)null, false);
        }, ct);
    }

    /// <summary>
    /// Marks records for redelivery (osdu/docs/reference/concepts/removal-and-reversal.md, Redelivering records): the
    /// ledger forgets what OSDU holds of them so the next plan re-sends them, and the flow's next run is asked to plan them
    /// again. Null keys means every record the flow has delivered.
    /// </summary>
    public Task<int> RedeliverAsync(IReadOnlyList<DeliveryKey>? keys, RedeliverScope scope, CancellationToken ct = default)
        => RedeliverAsync(keys, new RedeliverSelection(scope, []), ct);

    /// <summary>Marks records for redelivery of what <paramref name="selection"/> names, parts of a payload sent in parts included.</summary>
    public Task<int> RedeliverAsync(IReadOnlyList<DeliveryKey>? keys, RedeliverSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        object extra = selection.Lost
            ? new { scope = selection.Scope.ToString(), parts = selection.Parts, lost = true }
            : new { scope = selection.Scope.ToString(), parts = selection.Parts };
        return TrackAsync("redeliver", Named(keys, extra), keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var marked = await RequireLedger().ForceRedeliverAsync(Flow.Id, keys, selection, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitNamedAsync("record.redeliver", keys, $"redelivery of {selection} requested by {Actor}", ct).ConfigureAwait(false);
            return (marked, keys is null
                ? $"marked every delivered record, {marked} in all, for redelivery of {selection}"
                : $"marked {marked} record(s) for redelivery of {selection}", (Guid?)null, false);
        }, ct);
    }

    /// <summary>
    /// Gives records OSDU lost a new OSDU id each, in place of the one they claimed (osdu/docs/reference/flow/inventory-removals.md,
    /// Repairing missing ids), so the next deliver run writes them under it: only a record the ledger holds as delivered
    /// and no lease holds, whose new id no other record claims; any other is left as it is. <paramref name="reason"/> says
    /// why, in each record's history. Returns the records given one, each with its lost and new id.
    /// </summary>
    public Task<IReadOnlyList<TargetIdReassignment>> ReassignTargetIdsAsync(IReadOnlyList<DeliveryKey> keys, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return TrackAsync("reassign-id", Named(keys, new { reason }), keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var reassigned = await RequireLedger().ReassignTargetIdsAsync(Flow.Id, keys, Actor, reason, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitNamedAsync("record.reassign-id", reassigned.Select(r => r.Key).ToList(), $"new OSDU ids given by {Actor}: {reason}", ct).ConfigureAwait(false);
            return (reassigned, string.Create(
                CultureInfo.InvariantCulture,
                $"gave {reassigned.Count} of {keys.Count} record(s) a new OSDU id in place of one OSDU no longer holds; a record not delivered now, or held by a lease, was left as it is"),
                (Guid?)null, false);
        }, ct);
    }

    /// <summary>
    /// The step of an adoption by key (<see cref="AdoptAsync(RecordReach, string?, CancellationToken)"/>): asks the named
    /// records held because OSDU already holds a record at the id each one's key makes to adopt that record
    /// (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already holds): the flow's next deliver run
    /// takes each over, at the version OSDU holds then, and writes its row there. A named record held for another reason, or
    /// claiming an id already, is left as it is. Returns the records asked.
    /// </summary>
    private Task<IReadOnlyList<DeliveryKey>> AdoptNamedAsync(IReadOnlyList<DeliveryKey> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return TrackAsync("adopt", Named(keys, null), keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var asked = await RequireLedger().RequestAdoptionAsync(Flow.Id, ReleaseSelection.Named(keys), activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitNamedAsync("record.adoption-asked", [.. asked], $"adoption asked by {Actor}", ct).ConfigureAwait(false);
            return (asked, string.Create(
                CultureInfo.InvariantCulture,
                $"asked {asked.Count} of {keys.Count} record(s) to adopt the record OSDU holds at the id its key makes; a record not held for such an id, or claiming one already, was left as it is"),
                (Guid?)null, false);
        }, ct);
    }

    /// <summary>
    /// Asks every record one issue keeps held to adopt the record OSDU holds at the id its key makes
    /// (osdu/docs/reference/concepts/record-lifecycle.md, Issues): the issue OSDU already holding a record at such an id, as many
    /// records as it keeps held. The activity names the issue and its pattern as the operator was shown it, and every record
    /// asked is named under it.
    /// </summary>
    private Task<IReadOnlyList<DeliveryKey>> AdoptProblemAsync(long problem, string pattern, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var named = ProblemSignature.Format(problem);
        return TrackAsync("adopt", new { issue = named, pattern }, null, async activity =>
        {
            var asked = await RequireLedger().RequestAdoptionAsync(Flow.Id, ReleaseSelection.OfProblem(problem), activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.adoption-asked", null, $"adoption asked by {Actor}", $"the records issue {named} kept held", ct).ConfigureAwait(false);
            return (asked, $"asked {asked.Count} record(s) issue {named} kept held to adopt the records OSDU holds at their ids: {pattern}", (Guid?)null, false);
        }, ct);
    }

    /// <summary>
    /// Asks every record of the ledger held because OSDU already holds a record at the id its key makes to adopt that record:
    /// the batch an operator runs after a run held them, so that the next run takes them all over and the ledger is in step
    /// with OSDU. The activity names every record asked.
    /// </summary>
    private Task<IReadOnlyList<DeliveryKey>> AdoptEveryAsync(CancellationToken ct)
        => TrackAsync("adopt", new { every = "held for an id OSDU holds" }, null, async activity =>
        {
            var asked = await RequireLedger().RequestAdoptionAsync(Flow.Id, ReleaseSelection.EveryBlocked, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitAsync("record.adoption-asked", null, $"adoption asked by {Actor}", "every record held for an id OSDU holds", ct).ConfigureAwait(false);
            return (asked, $"asked {asked.Count} record(s) held for an id OSDU holds to adopt the records OSDU holds at their ids", (Guid?)null, false);
        }, ct);

    /// <summary>
    /// Asks records OSDU holds to be brought up to date (osdu/docs/reference/concepts/removal-and-reversal.md,
    /// Redelivering records): rendered again by the next plan that meets them, whichever run that is, and sent only
    /// where they render differently, since their delivered hashes stay. Null keys means every record the flow has
    /// delivered; a named record OSDU does not hold is left as it is. Returns how many were marked.
    /// </summary>
    public Task<int> BringUpToDateAsync(IReadOnlyList<DeliveryKey>? keys, CancellationToken ct = default)
        => TrackAsync("rerender", Named(keys, null), keys is { Count: 1 } ? keys[0] : null, async activity =>
        {
            var marked = await RequireLedger().RequestRenderAsync(Flow.Id, keys, activity, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await EmitNamedAsync("record.rerender", keys, $"bringing up to date requested by {Actor}", ct).ConfigureAwait(false);
            return (marked, keys is null
                ? $"asked every delivered record, {marked} in all, to be brought up to date"
                : $"asked {marked} of {keys.Count} record(s) to be brought up to date; a record that is not delivered (held, failed, removed, reverted, or never delivered) is left as it is: release or redeliver it instead", (Guid?)null, false);
        }, ct);

    /// <summary>
    /// The most records an intervention names one by one, in its activity and in its events. Beyond it the activity says how
    /// many it asked for and one event speaks for the flow, while the ledger still names every record under the activity.
    /// </summary>
    internal const int NamedPerIntervention = 100;

    /// <summary>What an intervention on <paramref name="keys"/> records as its parameters: the keys, or how many when they are many, with <paramref name="extra"/> beside them.</summary>
    private static Dictionary<string, object?> Named(IReadOnlyList<DeliveryKey>? keys, object? extra)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (keys is null || keys.Count <= NamedPerIntervention)
        {
            parameters["keys"] = keys?.Select(k => k.ToString()).ToList();
        }
        else
        {
            parameters["count"] = keys.Count;
        }

        if (extra is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(extra).EnumerateObject())
            {
                parameters[property.Name] = property.Value;
            }
        }

        return parameters;
    }

    /// <summary>Tells the listeners what an intervention on <paramref name="keys"/> asked for: an event per record while they are few, else one for the flow.</summary>
    private Task EmitNamedAsync(string kind, IReadOnlyList<DeliveryKey>? keys, string detail, CancellationToken ct)
        => keys is { Count: <= NamedPerIntervention }
            ? EmitAsync(kind, keys, detail, ct)
            : EmitAsync(kind, null, detail, keys is null ? "every delivered record" : $"{keys.Count} records", ct);
}
