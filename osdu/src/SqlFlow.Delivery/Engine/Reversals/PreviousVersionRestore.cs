using System.Globalization;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Reversals;

/// <summary>What stepping one record back came to: the result a removal reports, and for a record written back, its versions.</summary>
public sealed record PreviousVersionStep(RemovalRecordResult Result, PreviousVersionRestored? Restored);

/// <summary>
/// Takes the latest version of records out of being current (<see cref="RemovalChoice.Previous"/>;
/// osdu/docs/reference/concepts/removal-and-reversal.md, Restoring the previous version). OSDU has no call that removes
/// a record's latest version alone, so the version OSDU held before the write that left the latest, as the ledger tells
/// it (<see cref="ILedger.PriorVersionsAsync"/>), is read and written back as a new version
/// (<see cref="VersionWriteBack"/>, the write a reversal makes). A record is stepped back only while OSDU's latest
/// version is the one the ledger says this flow left: stepping back a record something else wrote since would undo that
/// write instead, so it is left as it is, saying so. Each record written back is settled in the ledger as reverted and
/// blocked, with an attempt naming both versions and who asked.
/// </summary>
public sealed class PreviousVersionRestore
{
    private readonly FlowDefinition _flow;
    private readonly ILedger _ledger;
    private readonly IDeliveryProtocol _protocol;
    private readonly TimeProvider _time;
    private readonly string _actor;
    private readonly Guid? _runId;
    private readonly Dictionary<string, ReversalRoute> _routes = new(StringComparer.Ordinal);

    public PreviousVersionRestore(FlowDefinition flow, ILedger ledger, IDeliveryProtocol protocol, TimeProvider time, string actor, Guid? runId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        _flow = flow;
        _ledger = ledger;
        _protocol = protocol;
        _time = time;
        _actor = actor;
        _runId = runId;
    }

    /// <summary>OSDU reads made at once: the flow's own concurrency, at least one.</summary>
    private int Concurrency => Math.Max(1, _flow.Parallelism.Concurrency);

    /// <summary>
    /// Steps back <paramref name="records"/>, each a record of the flow that claimed its OSDU id, and settles those written
    /// back in the ledger. Results align with <paramref name="records"/>.
    /// </summary>
    public async Task<IReadOnlyList<PreviousVersionStep>> RestoreAsync(IReadOnlyList<RecordState> records, string correlationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        var steps = new PreviousVersionStep?[records.Count];

        // What the ledger alone decides: a record with work in flight, one that is removed or whose version is not known,
        // and one on a route that cannot write an earlier version back are passed over before OSDU is asked anything.
        var asked = new List<int>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            if (Refusal(records[i]) is { } reason)
            {
                steps[i] = Skipped(records[i], reason);
            }
            else
            {
                asked.Add(i);
            }
        }

        // What OSDU holds now: only the version this flow left is stepped back.
        var verified = await VersionWriteBack.VerifyAsync(
            _protocol,
            asked.Select(i => new VerifyRequest(records[i].TargetId!, records[i].TargetVersion, JsonMerge.ToValues(records[i].TargetStateJson))).ToList(),
            Concurrency,
            ct).ConfigureAwait(false);
        var current = new List<(int Index, long Latest)>(asked.Count);
        for (var j = 0; j < asked.Count; j++)
        {
            var i = asked[j];
            var record = records[i];
            var answer = verified[j];
            switch (answer.Outcome)
            {
                case VerifyOutcome.Match:
                    current.Add((i, answer.ObservedVersion ?? record.TargetVersion!.Value));
                    break;

                case VerifyOutcome.Missing:
                    steps[i] = Skipped(record, "OSDU no longer holds the record: it was removed outside this flow, so there is no latest version to step back from");
                    break;

                case VerifyOutcome.Drifted:
                    steps[i] = Skipped(record, string.Create(
                        CultureInfo.InvariantCulture,
                        $"OSDU's latest version is {ReversalPlan.Version(answer.ObservedVersion)}, not version {ReversalPlan.Version(record.TargetVersion)} the ledger says this flow left: something wrote the record since, and stepping back would undo that write instead, so it is left as it is"));
                    break;

                default:
                    steps[i] = Failed(record, $"OSDU could not be asked what it holds of the record: {answer.Detail}");
                    break;
            }
        }

        // The version before the latest is the one OSDU held before the write that left it, as the ledger tells it: not the
        // version just below in OSDU's list, since one delivery can write two (a Wellbore DDMS record, then its bulk data),
        // and the first of those is a half of the latest, not the record as it was.
        var previous = new long?[records.Count];
        var priors = await _ledger.PriorVersionsAsync(
            _flow.Id, current.ToDictionary(c => records[c.Index].DeliveryKey, c => records[c.Index].TargetVersion!.Value), ct).ConfigureAwait(false);
        var unknown = new List<(int Index, long FirstVersion)>();
        foreach (var (i, _) in current)
        {
            var record = records[i];
            var prior = priors.TryGetValue(record.DeliveryKey, out var told) ? told : new PriorVersion(ReversalPriors.Unknown, null, null);
            switch (prior.Prior)
            {
                case ReversalPriors.Version when prior.Version is { } version:
                    previous[i] = version;
                    break;

                case ReversalPriors.None:
                    steps[i] = Skipped(record, string.Create(
                        CultureInfo.InvariantCulture,
                        $"the write that left version {record.TargetVersion} created the record, so OSDU held nothing before it; remove the record instead"));
                    break;

                default:
                    if (prior.FirstVersion is { } first)
                    {
                        unknown.Add((i, first));
                    }
                    else
                    {
                        // Without the write that left the latest, the version list cannot tell a version that write left (a
                        // Wellbore DDMS record before its bulk data) from the one before it, so nothing is guessed.
                        steps[i] = Skipped(record, string.Create(
                            CultureInfo.InvariantCulture,
                            $"the ledger no longer holds the attempt that wrote version {record.TargetVersion} (its attempts were pruned), so which version came before it cannot be told; read the record's versions in the explorer and remove or redeliver it instead"));
                    }

                    break;
            }
        }

        // Where the ledger no longer says (the record's earlier attempts were pruned), OSDU's version list decides: the newest
        // version older than the first one the latest write left.
        await Parallel.ForEachAsync(unknown, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct }, async (entry, token) =>
        {
            var record = records[entry.Index];
            try
            {
                var versions = await _protocol.VersionsAsync(record.TargetId!, token).ConfigureAwait(false);
                if (versions is null)
                {
                    steps[entry.Index] = Skipped(record, "the ledger no longer says which version OSDU held before the latest (the record's earlier attempts were pruned), and the route keeps no version list of the record");
                }
                else if (versions.Where(v => v < entry.FirstVersion).Select(v => (long?)v).Max() is { } before)
                {
                    previous[entry.Index] = before;
                }
                else
                {
                    steps[entry.Index] = Skipped(record, string.Create(
                        CultureInfo.InvariantCulture,
                        $"OSDU keeps no version of the record before version {entry.FirstVersion}: it was written once, or its earlier versions were purged; remove the record instead"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                steps[entry.Index] = Failed(record, $"OSDU's version list of the record could not be read: {ex.Message}");
            }
        }).ConfigureAwait(false);

        var work = current.Where(c => previous[c.Index] is not null).ToList();
        var written = await VersionWriteBack.RunAsync(
            _protocol,
            work.Select(c => new WriteBack(
                records[c.Index].DeliveryKey, records[c.Index].TargetId!, previous[c.Index]!.Value, c.Latest, JsonMerge.ToValues(records[c.Index].TargetStateJson))).ToList(),
            Concurrency,
            ct).ConfigureAwait(false);

        var landed = new List<(int Index, PreviousVersionRestored Restored)>(work.Count);
        for (var j = 0; j < work.Count; j++)
        {
            var (i, latest) = work[j];
            var record = records[i];
            var result = written[j];
            var version = result.Request.Version;
            switch (result.Outcome)
            {
                case WriteBackOutcome.Restored:
                    var newVersion = result.NewVersion!.Value;
                    landed.Add((i, new PreviousVersionRestored(
                        record.DeliveryKey, latest, version, newVersion,
                        VersionWriteBack.TargetStateAfter(record.TargetStateJson, record.TargetId!, newVersion, result.Returned))));
                    break;

                case WriteBackOutcome.VersionMissing:
                    steps[i] = Skipped(record, string.Create(
                        CultureInfo.InvariantCulture,
                        $"OSDU no longer holds version {version}, the one before the latest (the record's earlier versions were purged), so there is nothing to put back"));
                    break;

                case WriteBackOutcome.ReadFailed:
                    steps[i] = Failed(record, string.Create(CultureInfo.InvariantCulture, $"version {version}, the one before the latest, could not be read: {result.Failure}"));
                    break;

                default:
                    steps[i] = Failed(record, string.Create(CultureInfo.InvariantCulture, $"version {version} could not be written back: {result.Failure}"));
                    break;
            }
        }

        if (landed.Count > 0)
        {
            // OSDU holds these writes whether or not the removal is cancelled now, so the ledger is told regardless.
            var moved = (await _ledger.MarkRestoredAsync(
                    _flow.Id, landed.Select(l => l.Restored).ToList(), _actor, _runId, _time.GetUtcNow().UtcDateTime, correlationId, CancellationToken.None)
                .ConfigureAwait(false)).ToHashSet();
            foreach (var (i, restored) in landed)
            {
                var record = records[i];
                steps[i] = moved.Contains(record.DeliveryKey)
                    ? Failed(record, string.Create(
                        CultureInfo.InvariantCulture,
                        $"the record changed in the ledger while its previous version was written back, so the ledger was left as it is; OSDU holds version {restored.NewVersion}, which put back version {restored.Restored}. Compare the record with OSDU before asking again"))
                    : new PreviousVersionStep(
                        RemovalRecordResult.Restored(
                            record.DeliveryKey, record.SourceKey, record.Label, record.TargetId,
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"version {restored.Restored}, the one before the latest, written back as version {restored.NewVersion}; version {restored.Replaced} stays in the record's history (reversible)"),
                            record.LastSubmissionId),
                        restored);
            }
        }

        return steps.Select(s => s!).ToList();
    }

    /// <summary>Why the ledger alone says <paramref name="record"/> cannot be stepped back, or null when OSDU is to be asked.</summary>
    private string? Refusal(RecordState record)
    {
        if (record.Status == RecordStatus.Deleted)
        {
            return "the record was removed from OSDU, so there is no latest version to step back from";
        }

        if (record.LeaseOwner is not null || record.Status is RecordStatus.Pending or RecordStatus.Delivering or RecordStatus.Waiting)
        {
            return $"work is queued for the record ({record.Status.ToString().ToLowerInvariant()}); ask again once it has settled";
        }

        if (record.TargetVersion is null)
        {
            return "the ledger holds no OSDU version of the record, so which version is the latest cannot be told";
        }

        return RouteOf(record.TargetId!).RestoreRefusal;
    }

    /// <summary>What the route can do for the record <paramref name="targetId"/>, by the entity type it names; worked out once per type.</summary>
    private ReversalRoute RouteOf(string targetId)
    {
        var type = DdmsRouting.EntityTypeOf(targetId) ?? string.Empty;
        if (!_routes.TryGetValue(type, out var route))
        {
            route = ReversalRoute.ForRecord(_flow, targetId);
            _routes[type] = route;
        }

        return route;
    }

    private static PreviousVersionStep Skipped(RecordState record, string detail)
        => new(RemovalRecordResult.Skipped(record.DeliveryKey, record.SourceKey, record.Label, record.TargetId, detail, record.LastSubmissionId), null);

    private static PreviousVersionStep Failed(RecordState record, string detail)
        => new(RemovalRecordResult.Failed(record.DeliveryKey, record.SourceKey, record.Label, record.TargetId, HeaderRedaction.RedactMessage(detail), record.LastSubmissionId), null);
}
