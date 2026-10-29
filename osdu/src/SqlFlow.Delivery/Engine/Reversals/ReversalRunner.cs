using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Reversals;

/// <summary>What bounds a reversal's work, wherever it runs.</summary>
public static class ReversalLimits
{
    /// <summary>
    /// Records a reversal takes per page: read from the ledger together, checked against OSDU in batches, written to OSDU in
    /// batches (a bulk removal takes this many in one request) and settled in the ledger together.
    /// </summary>
    public const int Page = 500;

    /// <summary>The most records a preview classifies to say what a reversal would do; past it, it says what that sample comes to.</summary>
    public const int PreviewSample = 1_000;
}

/// <summary>What one run did of a reversal, beside the reversal's counts across every run that worked on it.</summary>
/// <param name="ReversalId">The reversal.</param>
/// <param name="Records">Records the reversal lists, from its source.</param>
/// <param name="Taken">Records this run took.</param>
/// <param name="Restored">Of those, restored to the version OSDU held before.</param>
/// <param name="Removed">Removed, or found already gone, because the source created them.</param>
/// <param name="Skipped">Passed over, saying why.</param>
/// <param name="Failed">Refused or failed, taken again when the reversal is asked again.</param>
/// <param name="Counts">The reversal's records by outcome, across every run.</param>
public sealed record ReversalSummary(long ReversalId, long Records, long Taken, long Restored, long Removed, long Skipped, long Failed, ReversalCounts Counts)
{
    /// <summary>What the pipeline's after step did with what the reversal changed in OSDU (osdu/docs/reference/flow/delivery.md, cache); null when it names none.</summary>
    public Snapshots.CacheStepOutcome? CacheAfter { get; init; }

    /// <summary>The one line the activity trail carries.</summary>
    public string Describe(ReversalSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return string.Create(CultureInfo.InvariantCulture, $"reversal {ReversalId} of {source}: {Counts}");
    }
}

/// <summary>
/// Reverses what one run or one submission delivered (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a
/// run or a submission): lists what its source delivered, then walks the list a page at a time in key order. For each
/// page the ledger decides what each record needs (<see cref="ReversalPlan"/>), OSDU is asked what it holds now, the
/// records about to be written are marked sending, removals go in bulk and restores read their earlier versions with
/// the flow's concurrency and write them back in batches, and the page is settled with its records in the ledger. A run
/// stopped anywhere loses nothing: a settled item stays settled, and an item left sending is checked against OSDU
/// before it is written again.
/// </summary>
public sealed class ReversalRunner
{
    private readonly FlowDefinition _flow;
    private readonly ILedger _ledger;
    private readonly IDeliveryProtocol _protocol;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly RunTrace? _trace;
    private readonly string _actor;
    private readonly Guid? _runId;
    private readonly ConcurrentDictionary<string, ReversalRoute> _routes = new(StringComparer.Ordinal);

    public ReversalRunner(FlowDefinition flow, ILedger ledger, IDeliveryProtocol protocol, TimeProvider time, ILogger log, RunTrace? trace, string actor, Guid? runId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        _flow = flow;
        _ledger = ledger;
        _protocol = protocol;
        _time = time;
        _log = log;
        _trace = trace;
        _actor = actor;
        _runId = runId;
    }

    /// <summary>Records per page; tests lower it to cross page boundaries with a few records.</summary>
    internal int PageSize { get; init; } = ReversalLimits.Page;

    /// <summary>OSDU reads made at once: the flow's own concurrency, at least one.</summary>
    private int Concurrency => Math.Max(1, _flow.Parallelism.RecordsAtOnce);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>Opens or resumes the reversal of <paramref name="source"/>, works it through, and closes it as it ended.</summary>
    public async Task<ReversalSummary> RunAsync(ReversalSource source, long? activityId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);

        // A source that delivered nothing, or whose reversal has nothing left to take, is refused before a reversal is opened,
        // for the same reason the API and the GUI give: a run never opens an empty reversal or one it would change nothing in.
        var availability = await ReversalAvailability.ReadAsync(_ledger, _flow.Id, source, activeRun: null, ct).ConfigureAwait(false);
        if (!availability.Reversible)
        {
            throw new DeliveryException(availability.Reason!);
        }

        var reversal = await _ledger.OpenReversalAsync(_flow.Id, _flow.Label, source, _actor, _runId, Now, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Reversal {ReversalId} of {Source} in '{Flow}': {State}.",
            reversal.ReversalId, source, _flow.Label, reversal.CapturedUtc is null ? "listing what it delivered" : "resuming");
        try
        {
            var summary = await WorkAsync(reversal, activityId, ct).ConfigureAwait(false);
            await _ledger.CloseReversalAsync(reversal.ReversalId, ReversalStatuses.Completed, null, Now, CancellationToken.None).ConfigureAwait(false);
            return summary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CloseQuietlyAsync(reversal.ReversalId, ReversalStatuses.Cancelled, "the run was cancelled; asking again resumes it").ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await CloseQuietlyAsync(reversal.ReversalId, ReversalStatuses.Failed, ex.Message).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ReversalSummary> WorkAsync(ReversalState reversal, long? activityId, CancellationToken ct)
    {
        var lastProgress = _time.GetUtcNow();
        var listed = await _ledger.CaptureReversalAsync(
            reversal.ReversalId,
            capture =>
            {
                if (ProgressDue(ref lastProgress))
                {
                    _log.LogInformation(RunTrace.Bounded, "Reversal {ReversalId}: listed {Added:N0} record(s) from {Read:N0} delivered attempt(s) so far.", reversal.ReversalId, capture.Added, capture.Records);
                }

                return Task.CompletedTask;
            },
            ct).ConfigureAwait(false);
        _log.LogInformation("Reversal {ReversalId} lists {Records:N0} record(s) {Source} delivered.", reversal.ReversalId, listed.Records, reversal.Source);

        var tally = new Tally();
        DeliveryKey? after = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await _ledger.ListReversalWorkAsync(reversal.ReversalId, after, PageSize, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            var settlements = await ReversePageAsync(reversal, page, ct).ConfigureAwait(false);
            var settled = await _ledger.SettleReversalAsync(reversal.ReversalId, settlements, _actor, activityId, _runId, Now, ct).ConfigureAwait(false);
            tally.Add(settlements, settled);
            if (settled.Moved > 0)
            {
                _log.LogWarning(
                    "Reversal {ReversalId}: {Moved} record(s) changed in the ledger while the reversal wrote to OSDU; each is settled failed, saying what OSDU now holds.",
                    reversal.ReversalId, settled.Moved);
            }

            if (ProgressDue(ref lastProgress))
            {
                _log.LogInformation(
                    RunTrace.Bounded,
                    "Reversal {ReversalId}: {Taken:N0} of {Records:N0} record(s) taken by this run: {Restored:N0} restored, {Removed:N0} removed, {Skipped:N0} passed over, {Failed:N0} failed.",
                    reversal.ReversalId, tally.Taken, listed.Records, tally.Restored, tally.Removed, tally.Skipped, tally.Failed);
            }

            if (page.Count < PageSize)
            {
                break;
            }

            after = page[^1].DeliveryKey;
        }

        var counts = await _ledger.CountReversalAsync(reversal.ReversalId, ct).ConfigureAwait(false);
        var summary = new ReversalSummary(reversal.ReversalId, listed.Records, tally.Taken, tally.Restored, tally.Removed, tally.Skipped, tally.Failed, counts);
        _log.LogInformation("Reversal {ReversalId} of {Source}: {Counts}.", reversal.ReversalId, reversal.Source, counts);
        return summary;
    }

    /// <summary>What one page comes to: one settlement per item.</summary>
    private async Task<IReadOnlyList<ReversalSettlement>> ReversePageAsync(ReversalState reversal, IReadOnlyList<ReversalItemState> page, CancellationToken ct)
    {
        var settlements = new List<ReversalSettlement>(page.Count);
        var records = await _ledger.GetRecordsAsync(_flow.Id, page.Select(i => i.DeliveryKey), ct).ConfigureAwait(false);

        // The run's correlation id, which the page's calls carry and each record's attempt names, so a reversal is followed
        // into OSDU's own logs as a delivery is.
        using var correlation = OsduCorrelation.Begin(FlowTelemetry.Delivery);
        var work = new List<Work>(page.Count);
        foreach (var item in page)
        {
            records.TryGetValue(item.DeliveryKey, out var record);
            var route = item.TargetId is { } id ? RouteOf(id) : ReversalRoute.Of(_flow, null);
            var step = ReversalPlan.Decide(item, record, route);
            if (step.Action == ReversalAction.Skip)
            {
                settlements.Add(ReversalSettlement.Skipped(item.DeliveryKey, step.Outcome!, step.Detail!, correlation.Id));
            }
            else
            {
                work.Add(new Work(item, record!, route, step.Action));
            }
        }

        if (work.Count == 0)
        {
            return settlements;
        }

        // What OSDU holds now: a record changed outside this flow since the source wrote it is left alone, and one whose
        // write by an earlier run of this reversal landed is settled without writing again.
        var verified = await VerifyAsync(work, ct).ConfigureAwait(false);
        var removals = new List<Work>();
        var restores = new List<Work>();
        var landedChecks = new List<Work>();
        for (var i = 0; i < work.Count; i++)
        {
            var w = work[i];
            var result = verified[i];
            // A write an earlier run of this reversal sent may have landed: it stopped (sending), or the answer was lost (failed).
            var sending = w.Item.State is ReversalItemStates.Sending or ReversalItemStates.Failed;
            switch (result.Outcome)
            {
                case VerifyOutcome.Missing when w.Action == ReversalAction.Remove:
                    settlements.Add(ReversalSettlement.Removed(
                        w.Item.DeliveryKey,
                        alreadyGone: !sending,
                        sending
                            ? $"removed from OSDU (reversible): {reversal.Source} created it; the removal an earlier run of this reversal sent had landed"
                            : $"OSDU had already lost the record {reversal.Source} created; the ledger now says so",
                        correlation.Id));
                    break;

                case VerifyOutcome.Missing:
                    settlements.Add(ReversalSettlement.Skipped(
                        w.Item.DeliveryKey, ReversalOutcomes.MissingInOsdu, "OSDU no longer holds the record: it was removed outside this flow after the source updated it", correlation.Id));
                    break;

                case VerifyOutcome.Error:
                    settlements.Add(ReversalSettlement.Failed(w.Item.DeliveryKey, $"OSDU could not be asked what it holds of the record: {result.Detail}", correlation.Id));
                    break;

                case VerifyOutcome.Drifted when sending && w.Action != ReversalAction.Remove:
                    landedChecks.Add(w with { Latest = result.ObservedVersion });
                    break;

                case VerifyOutcome.Drifted:
                    settlements.Add(ReversalSettlement.Skipped(
                        w.Item.DeliveryKey,
                        ReversalOutcomes.ChangedInOsdu,
                        string.Create(CultureInfo.InvariantCulture, $"OSDU holds version {ReversalPlan.Version(result.ObservedVersion)}, written after the source left version {ReversalPlan.Version(w.Item.RunVersion)} by something outside this flow; it is left as it is"),
                        correlation.Id));
                    break;

                default:
                    var current = w with { Latest = result.ObservedVersion ?? w.Item.RunVersion };
                    if (w.Action == ReversalAction.Remove)
                    {
                        removals.Add(current);
                    }
                    else
                    {
                        restores.Add(current);
                    }

                    break;
            }
        }

        // A record whose earlier attempts were pruned: OSDU's version list says what it held before the source's first write.
        var unresolved = restores.Where(w => w.Action == ReversalAction.ResolvePrior).ToList();
        if (unresolved.Count > 0)
        {
            var resolved = await ResolvePriorsAsync(unresolved, reversal.Source, correlation.Id, ct).ConfigureAwait(false);
            restores = restores.Where(w => w.Action != ReversalAction.ResolvePrior).ToList();
            foreach (var (w, prior, skipped) in resolved)
            {
                if (skipped is not null)
                {
                    settlements.Add(skipped);
                }
                else if (prior is null)
                {
                    if (w.Route.Removes)
                    {
                        removals.Add(w with { Action = ReversalAction.Remove });
                    }
                    else
                    {
                        settlements.Add(ReversalSettlement.Skipped(w.Item.DeliveryKey, ReversalOutcomes.NotReversible, w.Route.RemoveRefusal!, correlation.Id));
                    }
                }
                else
                {
                    restores.Add(w with { Action = ReversalAction.Restore, Prior = prior });
                }
            }
        }

        settlements.AddRange(await LandedAsync(landedChecks, reversal.Source, correlation.Id, ct).ConfigureAwait(false));
        if (removals.Count == 0 && restores.Count == 0)
        {
            return settlements;
        }

        // From here OSDU is written: say so first, so a run that stops mid-write is known to have.
        await _ledger.MarkReversalSendingAsync(
            reversal.ReversalId, removals.Concat(restores).Select(w => w.Item.DeliveryKey).ToList(), _runId, Now, ct).ConfigureAwait(false);
        settlements.AddRange(await RemoveAsync(removals, reversal.Source, correlation.Id, ct).ConfigureAwait(false));
        settlements.AddRange(await RestoreAsync(restores, reversal.Source, correlation.Id, ct).ConfigureAwait(false));
        return settlements;
    }

    /// <summary>What OSDU holds of each record, read in the route's batches, as many at once as the flow's concurrency allows.</summary>
    private Task<VerifyResult[]> VerifyAsync(IReadOnlyList<Work> work, CancellationToken ct)
        => VersionWriteBack.VerifyAsync(
            _protocol, work.Select(w => new VerifyRequest(w.Item.TargetId!, w.Item.RunVersion, TargetStateOf(w.Record))).ToList(), Concurrency, ct);

    /// <summary>
    /// The version OSDU held before the source's first write of each record, read from its version list: the newest version
    /// older than that write, or none when the source created it.
    /// </summary>
    private async Task<IReadOnlyList<(Work Work, long? Prior, ReversalSettlement? Skipped)>> ResolvePriorsAsync(
        IReadOnlyList<Work> work, ReversalSource source, string correlationId, CancellationToken ct)
    {
        var resolved = new (Work, long?, ReversalSettlement?)[work.Count];
        await Parallel.ForEachAsync(work.Select((w, i) => (Work: w, Index: i)), new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct }, async (entry, token) =>
        {
            var (w, index) = entry;
            if (w.Item.FirstVersion is not { } first)
            {
                resolved[index] = (w, null, ReversalSettlement.Skipped(
                    w.Item.DeliveryKey, ReversalOutcomes.NotReversible, $"the ledger no longer says what OSDU held before {source}, and its first delivery recorded no version to look before", correlationId));
                return;
            }

            try
            {
                var versions = await _protocol.VersionsAsync(w.Item.TargetId!, token).ConfigureAwait(false);
                resolved[index] = versions is null
                    ? (w, null, ReversalSettlement.Skipped(
                        w.Item.DeliveryKey, ReversalOutcomes.NotReversible, $"the ledger no longer says what OSDU held before {source}, and the route keeps no version list of the record", correlationId))
                    : (w, versions.Where(v => v < first).Select(v => (long?)v).Max(), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                resolved[index] = (w, null, ReversalSettlement.Failed(w.Item.DeliveryKey, $"OSDU's version list of the record could not be read: {ex.Message}", correlationId));
            }
        }).ConfigureAwait(false);
        return resolved;
    }

    /// <summary>
    /// Settles the records an earlier run of this reversal was writing back when it stopped, whose latest version OSDU holds
    /// is no longer the one the source left: when that version holds what the reversal writes (the earlier version's
    /// content), the write landed; otherwise something outside this flow wrote it, and the record is left as it is.
    /// </summary>
    private async Task<IReadOnlyList<ReversalSettlement>> LandedAsync(IReadOnlyList<Work> work, ReversalSource source, string correlationId, CancellationToken ct)
    {
        if (work.Count == 0)
        {
            return [];
        }

        var settled = new ReversalSettlement[work.Count];
        await Parallel.ForEachAsync(work.Select((w, i) => (Work: w, Index: i)), new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct }, async (entry, token) =>
        {
            var (w, index) = entry;
            try
            {
                var prior = w.Item.PriorVersion;
                if (prior is null && w.Item.FirstVersion is { } first && await _protocol.VersionsAsync(w.Item.TargetId!, token).ConfigureAwait(false) is { } versions)
                {
                    prior = versions.Where(v => v < first).Select(v => (long?)v).Max();
                }

                var latest = await _protocol.ReadAsync(w.Item.TargetId!, TargetStateOf(w.Record), token).ConfigureAwait(false);
                var earlier = prior is { } p ? await _protocol.ReadVersionAsync(w.Item.TargetId!, p, token).ConfigureAwait(false) : null;
                if (latest is not null && earlier is not null && w.Latest is { } landed && SameContent(latest, earlier))
                {
                    settled[index] = ReversalSettlement.Restored(
                        w.Item.DeliveryKey, prior!.Value, landed, VersionWriteBack.TargetStateAfter(w.Record.TargetStateJson, w.Item.TargetId!, landed, null),
                        string.Create(CultureInfo.InvariantCulture, $"restored version {prior}, the one OSDU held before {source}, as version {landed}: the write an earlier run of this reversal sent had landed"),
                        correlationId);
                    return;
                }

                settled[index] = ReversalSettlement.Skipped(
                    w.Item.DeliveryKey,
                    ReversalOutcomes.ChangedInOsdu,
                    string.Create(CultureInfo.InvariantCulture, $"OSDU holds version {ReversalPlan.Version(w.Latest)}, which is not what this reversal writes: something outside this flow wrote it after the source left version {ReversalPlan.Version(w.Item.RunVersion)}; it is left as it is"),
                    correlationId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                settled[index] = ReversalSettlement.Failed(w.Item.DeliveryKey, $"whether an earlier run's write of the record landed could not be told: {ex.Message}", correlationId);
            }
        }).ConfigureAwait(false);
        return settled;
    }

    /// <summary>Removes the records the source created, reversibly, in the route's batches.</summary>
    private async Task<IReadOnlyList<ReversalSettlement>> RemoveAsync(IReadOnlyList<Work> work, ReversalSource source, string correlationId, CancellationToken ct)
    {
        if (work.Count == 0)
        {
            return [];
        }

        var removals = work.Select(w => new RecordRemoval(w.Item.DeliveryKey, w.Item.TargetId!, TargetStateOf(w.Record))).ToList();
        IReadOnlyList<RemovalResult> results;
        try
        {
            results = await _protocol.DeleteBatchAsync(removals, RemovalScope.Record, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return work.Select(w => ReversalSettlement.Failed(w.Item.DeliveryKey, $"the removal was refused: {ex.Message}", correlationId)).ToList();
        }

        return results.Select(r => r.Succeeded
            ? ReversalSettlement.Removed(
                r.Removal.Key,
                r.Outcome!.AlreadyGone,
                r.Outcome.AlreadyGone
                    ? $"OSDU had already lost the record {source} created; the ledger now says so"
                    : $"removed from OSDU (reversible): {source} created it",
                correlationId)
            : ReversalSettlement.Failed(r.Removal.Key, $"the removal was refused: {r.Failure?.Message ?? r.Outcome?.Detail}", correlationId)).ToList();
    }

    /// <summary>
    /// Reads the version each record held before the source, as many at once as the flow's concurrency allows, and writes
    /// them back in the route's batches (<see cref="VersionWriteBack"/>).
    /// </summary>
    private async Task<IReadOnlyList<ReversalSettlement>> RestoreAsync(IReadOnlyList<Work> work, ReversalSource source, string correlationId, CancellationToken ct)
    {
        if (work.Count == 0)
        {
            return [];
        }

        var written = await VersionWriteBack.RunAsync(
            _protocol,
            work.Select(w => new WriteBack(w.Item.DeliveryKey, w.Item.TargetId!, w.Prior ?? w.Item.PriorVersion!.Value, w.Latest, TargetStateOf(w.Record))).ToList(),
            Concurrency,
            ct).ConfigureAwait(false);
        var settlements = new List<ReversalSettlement>(work.Count);
        for (var i = 0; i < work.Count; i++)
        {
            var w = work[i];
            var result = written[i];
            var prior = result.Request.Version;
            settlements.Add(result.Outcome switch
            {
                WriteBackOutcome.Restored => ReversalSettlement.Restored(
                    w.Item.DeliveryKey,
                    prior,
                    result.NewVersion!.Value,
                    VersionWriteBack.TargetStateAfter(w.Record.TargetStateJson, w.Item.TargetId!, result.NewVersion.Value, result.Returned),
                    string.Create(CultureInfo.InvariantCulture, $"restored version {prior}, the one OSDU held before {source}, as version {result.NewVersion.Value}"),
                    correlationId),
                WriteBackOutcome.VersionMissing => ReversalSettlement.Skipped(
                    w.Item.DeliveryKey,
                    ReversalOutcomes.VersionMissing,
                    string.Create(CultureInfo.InvariantCulture, $"OSDU no longer holds version {prior}, the one it held before {source} (the record's earlier versions were purged), so there is nothing to put back"),
                    correlationId),
                WriteBackOutcome.ReadFailed => ReversalSettlement.Failed(
                    w.Item.DeliveryKey, string.Create(CultureInfo.InvariantCulture, $"version {prior}, the one OSDU held before the source, could not be read: {result.Failure}"), correlationId),
                _ => ReversalSettlement.Failed(
                    w.Item.DeliveryKey, string.Create(CultureInfo.InvariantCulture, $"version {prior} could not be written back: {result.Failure}"), correlationId),
            });
        }

        return settlements;
    }

    /// <summary>What a reversal can do for the record <paramref name="targetId"/>, by the entity type it names; worked out once per type.</summary>
    private ReversalRoute RouteOf(string targetId)
        => _routes.GetOrAdd(DdmsRouting.EntityTypeOf(targetId) ?? string.Empty, _ => ReversalRoute.ForRecord(_flow, targetId));

    private static IReadOnlyDictionary<string, string> TargetStateOf(RecordState record) => JsonMerge.ToValues(record.TargetStateJson);

    /// <summary>
    /// Whether two stored versions of a record hold the same content: every property but those the storage service writes
    /// on each version, and but the data keys another system writes on its own, which a write back carries from the latest
    /// version (<see cref="Protocols.RecordRestores"/>).
    /// </summary>
    private static bool SameContent(JsonObject latest, JsonObject earlier)
    {
        var carried = Protocols.RecordRestores.CarriedKeys(earlier);
        return JsonNode.DeepEquals(Content(latest, carried), Content(earlier, carried));
    }

    private static JsonObject Content(JsonObject stored, IReadOnlyList<string> carried)
    {
        var content = (JsonObject)stored.DeepClone();
        foreach (var property in new[] { "version", "createUser", "createTime", "modifyUser", "modifyTime" })
        {
            content.Remove(property);
        }

        if (content["data"] is JsonObject data)
        {
            foreach (var key in carried)
            {
                data.Remove(key);
            }
        }

        return content;
    }

    private bool ProgressDue(ref DateTimeOffset last)
    {
        var now = _time.GetUtcNow();
        if (_trace is { } trace ? !trace.ProgressDue("reverse", now) : now - last < RunTrace.ProgressInterval)
        {
            return false;
        }

        last = now;
        return true;
    }

    private async Task CloseQuietlyAsync(long reversalId, string status, string reason)
    {
        try
        {
            await _ledger.CloseReversalAsync(reversalId, status, reason, Now, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or InvalidOperationException or System.Data.Common.DbException or TimeoutException)
        {
            _log.LogWarning("Could not record reversal {ReversalId} as {Status} in the ledger: {Message}", reversalId, status, ex.Message);
        }
    }

    /// <summary>One record a page works on: its item, its record, its route, what it needs, and what OSDU holds of it now.</summary>
    private sealed record Work(ReversalItemState Item, RecordState Record, ReversalRoute Route, ReversalAction Action)
    {
        public long? Latest { get; init; }

        /// <summary>The version to write back, once OSDU's version list said it.</summary>
        public long? Prior { get; init; }
    }

    /// <summary>What this run did, counted as it settles pages.</summary>
    private sealed class Tally
    {
        public long Taken { get; private set; }

        public long Restored { get; private set; }

        public long Removed { get; private set; }

        public long Skipped { get; private set; }

        public long Failed { get; private set; }

        public void Add(IReadOnlyList<ReversalSettlement> settlements, ReversalSettled settled)
        {
            // A restore or removal whose record changed in the ledger meanwhile is settled failed, not as OSDU was told.
            Taken += settlements.Count;
            Restored += settlements.Count(s => s.Outcome == ReversalOutcomes.Restored) - settled.MovedRestores;
            Removed += settlements.Count(s => s.Outcome is ReversalOutcomes.Removed or ReversalOutcomes.AlreadyGone) - settled.MovedRemovals;
            Skipped += settlements.Count(s => s.State == ReversalItemStates.Skipped);
            Failed += settlements.Count(s => s.State == ReversalItemStates.Failed) + settled.Moved;
        }
    }
}
