using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine;

// A delivery spread over the fleet (osdu/docs/reference/concepts/submissions.md, Many nodes). The coordinating deliver run
// cuts its read into key slices and queues the flow again once per share of them, through the platform's run fan-out: each
// is a deliver run of the same flow filtered to its slices, which any node of the pool takes. A slice run delivers its share
// on its own: it plans its slices into work batches on its node, sends those batches, and keeps at them until every record of
// the share is settled. No run reads another run's batches, so which node runs which share does not matter. The coordinating
// run delivers its own share the same way, waits for the others, delivers the share of any that never started, stopped or
// failed, and closes the submission.
public sealed partial class FlowRuntime
{
    private static readonly TimeSpan MemberProgress = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan LeaseSettle = TimeSpan.FromSeconds(30);

    /// <summary>How often a coordinating run asks the platform how the slice runs it queued are doing.</summary>
    internal TimeSpan MemberPoll { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a coordinating run that delivered its own share waits for slice runs no node has started, when none is
    /// running and none started or finished meanwhile, before it delivers their shares itself (osdu/docs/reference/concepts/
    /// submissions.md, Many nodes). Long enough for an autoscaler to bring up a node for them; past it the pool has no place
    /// free, every slot holding work its own runs wait on, and waiting longer could wait for ever.
    /// </summary>
    internal TimeSpan MemberStartGrace { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>What a coordinating run's slice runs and the shares it delivered itself sent, and how many runs it queued.</summary>
    private sealed record SharesSent(WorkerSummary Work, int Members);

    /// <summary>What waiting for the slice runs came to: their state once every one ended, and the ones this run stopped.</summary>
    private sealed record MemberWait(FanOutState State, IReadOnlySet<Guid> Stopped);

    /// <summary>
    /// The planning of a deliver run, and the whole delivery when it fans out. It fans out when the flow lets it, this run can
    /// queue runs, and the read is big enough and can be cut: the candidate keys are cut into contiguous slices on the record
    /// table's identity primary key, dealt into shares, and a slice run is queued for every share but this run's own. This run
    /// then delivers its share, waits for the slice runs, delivers the shares of those that did not deliver theirs, finalises
    /// the submission with the totals of every share, and sends what the submission still holds. Returns with no
    /// <see cref="SharesSent"/> when the run does not fan out: it has planned the whole read, and sends it as a run alone does.
    /// </summary>
    private async Task<(IntakeResult Result, SharesSent? FannedOut)> PlanAndFanOutAsync(bool force, Action<FanOutHandle?> track, CancellationToken ct)
    {
        var intake = Intake;
        var prepared = await intake.PrepareAsync(Flow, Mapping, Parameters, Request, force, ct).ConfigureAwait(false);
        if (prepared.Done is { } done)
        {
            SubmissionId = done.Submission.SubmissionId;
            return (done, null);
        }

        var submission = prepared.Submission;
        SubmissionId = submission.SubmissionId;
        var header = prepared.Header;
        var candidates = header.Source.EstimatedCandidates;
        var slices = KeySlices.Count(candidates, Flow.Parallelism.BatchRecords);
        var withheld = FanOutUnavailable()
            ?? TooFewToFanOut(candidates, "candidate record(s)")
            ?? (Flow.Source.Record.PrimaryKey is null
                ? $"the record table names no identity primary key ({KeyPaths.Of(Flow).Record}.primaryKey) to cut the read on"
                : null)
            ?? (slices < 2 || slices - 1 > SubmissionIntake.MaxFanOutPartition
                ? string.Create(CultureInfo.InvariantCulture, $"the read is one work batch ({Flow.Parallelism.BatchRecords} records)")
                : null);

        // The candidates are cut on the record table's identity primary key; when they sit so close together that fewer
        // than two ranges come out, the read is one run's. A submission planned again that was cut before keeps its cut:
        // the slice runs it already has (a run the platform executes again gets them back) deliver the ranges they were
        // dealt, and a new cut would leave rows between the two to nobody.
        var ranges = withheld is not null ? []
            : prepared.Window is { Slices.Count: >= 2 } cut ? Recorded(cut)
            : await Planner.SliceBoundsAsync(header, slices, ct).ConfigureAwait(false);
        var shares = ranges.Count < 2 ? [] : KeySlices.Shares(ranges.Count, Flow.Parallelism.FanOut + 1);
        var memberShares = shares.Skip(1).Where(s => s.Count > 0).ToList();
        if (memberShares.Count == 0)
        {
            if (Flow.Parallelism.FanOut > 0)
            {
                _log.LogInformation(
                    "The delivery is not fanned out: {Reason}.",
                    withheld ?? string.Create(CultureInfo.InvariantCulture, $"its {candidates} candidate record(s) sit too close together on the primary key to cut into two slices"));
            }

            var own = await intake.PlanSlicesAsync(Flow, prepared, null, ct).ConfigureAwait(false);
            var planned = await intake.FinalizePlanningAsync(Flow, submission.SubmissionId, Parameters, own, ct).ConfigureAwait(false);
            return (new IntakeResult(planned, header, own, AlreadyProcessed: false), null);
        }

        prepared = await intake.RecordSlicesAsync(prepared, ranges, ct).ConfigureAwait(false);
        var runs = memberShares.Count + 1;
        var members = memberShares.Select(share => new RunParameters
        {
            Operation = DeliveryOperations.Deliver,
            Values = MemberValues(Parameters),
            Payload = new DeliveryRunPayload
            {
                SubmissionId = submission.SubmissionId,
                Slices = share,
                Force = force,
                Interface = Flow.Interface,
                References = _context.Supplied,
                RateShare = runs,
            }.ToJson(),
        }).ToList();

        // The slice runs are queued before this run delivers its own share, so the nodes deliver theirs while it does.
        var enqueued = _context.Time.GetUtcNow();
        var handle = await _context.Dispatcher.EnqueueAsync(members, ct).ConfigureAwait(false);
        track(handle);
        if (handle.RunIds.Count != members.Count)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture, $"The platform answered the fan-out of submission {submission.SubmissionId:D} with {handle.RunIds.Count} run(s) for the {members.Count} shares it was given, so which run delivers which share cannot be told. The submission stays planned as far as it got, and the flow's next deliver run takes it over."));
        }

        _log.LogInformation(
            "Fanned the delivery of {Records} candidate record(s) in {Slices} key slice(s) out to {Members} run(s) of the flow (group {GroupId}), each delivering its share from its own node; this run delivers slices {Own}.",
            candidates, ranges.Count, members.Count, handle.GroupId, KeySlices.Describe(shares[0]));
        IntakeCounts totals;
        WorkerSummary work;
        MemberWait wait;
        ShareRateLimit(runs);
        try
        {
            totals = await PlanShareAsync(prepared, shares[0], ct).ConfigureAwait(false);
            work = await SendShareAsync(submission.SubmissionId, BatchesOf(shares[0]), prepared.WorkRoot, ct).ConfigureAwait(false);
            wait = await WaitForMembersAsync(handle, enqueued, MemberStartGrace, ct).ConfigureAwait(false);
        }
        finally
        {
            ShareRateLimit(1);
        }

        track(null);
        var taken = new List<int>();
        var takenFrom = 0;
        for (var i = 0; i < handle.RunIds.Count; i++)
        {
            var runId = handle.RunIds[i];
            var member = wait.State.Members.FirstOrDefault(m => m.RunId == runId)
                ?? throw new DeliveryException($"Slice run {runId:D} is missing from the state of its fan-out (group {handle.GroupId:D}). The submission stays planned as far as it got, and the flow's next deliver run takes it over.");
            if (member.Succeeded && DeliverOutcome.ShareOf(member.ResultJson) is { } outcome)
            {
                totals = totals.Add(outcome.ToCounts());
                continue;
            }

            // A share its run did not deliver is delivered here. Planning it again is safe: the records the run sent are
            // unchanged and passed over, and the rows it never reached are planned now. A record it queued and did not send
            // has its document on that run's node: this run finds it missing, and the flow's next run plans it again.
            if (!wait.Stopped.Contains(runId))
            {
                _log.LogWarning(
                    "Slice run {Slot} (run {RunId}) {Outcome}; this run delivers its slices {Slices}.",
                    member.Slot, member.RunId,
                    member.Succeeded ? "reported no outcome of its share" : $"{member.Status}: {member.Error ?? "no error recorded"}",
                    KeySlices.Describe(memberShares[i]));
            }

            taken.AddRange(memberShares[i]);
            takenFrom++;
        }

        if (taken.Count > 0)
        {
            taken.Sort();
            _log.LogInformation("Delivering slices {Slices} here, the shares of {Members} run(s) that did not deliver them.", KeySlices.Describe(taken), takenFrom);
            totals = totals.Add(await PlanShareAsync(prepared, taken, ct).ConfigureAwait(false));
        }

        var finalized = await intake.FinalizePlanningAsync(Flow, submission.SubmissionId, Parameters, totals, ct).ConfigureAwait(false);

        // Every slice run has ended. What the submission still holds is sent here: the shares taken over, records a slice run
        // left in backoff or whose lease ran out, and records released from a wait once what they waited for landed.
        var rest = await SendShareAsync(submission.SubmissionId, null, null, ct).ConfigureAwait(false);
        return (new IntakeResult(finalized, header, totals, AlreadyProcessed: false), new SharesSent(work.Add(rest), members.Count));
    }

    /// <summary>
    /// A slice run of a fan-out: this run delivers the key slices its payload names of its coordinating run's submission. It
    /// plans them into work batches on this node, sends those batches and keeps at them until every record of the share is
    /// settled, waiting out retries, and reads no other run's batches. Its coordinating run brought the cache up to date,
    /// answered the run's requests, and finalises, completes and sweeps the submission once every share has ended.
    /// </summary>
    private async Task<RunResult> DeliverShareAsync(IReadOnlyList<int> share, bool force, CancellationToken ct)
    {
        var submissionId = SubmissionId
            ?? throw new DeliveryException("A run delivering key slices of a fan-out names the submission they were cut from (payload submissionId); this run names none.");
        var batches = BatchesOf(share);
        await EnsureLegalTagsAsync(ct).ConfigureAwait(false);
        var prepared = await Intake.PrepareAsync(Flow, Mapping, Parameters, Request, force, ct).ConfigureAwait(false);
        if (prepared.Done is { } done)
        {
            return new RunResult(done, WorkerSummary.Empty, done.Submission);
        }

        var counts = await PlanShareAsync(prepared, share, ct).ConfigureAwait(false);
        _log.LogInformation(
            "Planned slices {Slices} of submission {SubmissionId}: {Counts}. Sending them from this node.",
            KeySlices.Describe(share), submissionId, counts);
        var work = await SendShareAsync(submissionId, batches, prepared.WorkRoot, ct).ConfigureAwait(false);
        var submission = await RequireLedger().GetSubmissionAsync(submissionId, ct).ConfigureAwait(false) ?? prepared.Submission;
        return new RunResult(new IntakeResult(submission, prepared.Header, counts, AlreadyProcessed: false), work, submission);
    }

    /// <summary>
    /// Plans <paramref name="share"/> of the submission into work batches, and has the failure guard judge what it held
    /// before any of it is sent; a guard it trips cancels the run here.
    /// </summary>
    private async Task<IntakeCounts> PlanShareAsync(PreparedIntake prepared, IReadOnlyList<int> share, CancellationToken ct)
    {
        var counts = await Intake.PlanSlicesAsync(Flow, prepared, share, ct).ConfigureAwait(false);
        Guard?.Planned(counts.Held, counts.Planned);
        _context.Trace?.AddPlanned(counts.Planned);
        ct.ThrowIfCancellationRequested();
        return counts;
    }

    /// <summary>
    /// Sends what the submission holds and keeps at it until it is settled or the run is cancelled: with
    /// <paramref name="batches"/>, the records queued in those batches alone, read from <paramref name="workRoot"/>, the
    /// folder this run wrote them to; without, every record of the submission, read from the work location it records.
    /// Leases a run that stopped held run out and are taken back, and records in backoff come due.
    /// </summary>
    private async Task<WorkerSummary> SendShareAsync(Guid submissionId, BatchRange? batches, string? workRoot, CancellationToken ct)
    {
        var ledger = RequireLedger();
        var worker = await WorkerAsync(batches, workRoot, ct).ConfigureAwait(false);
        var total = await worker.DrainAsync(submissionId, ct).ConfigureAwait(false);
        while (await ledger.HasPendingAsync(Flow.Id, submissionId, _context.Time.GetUtcNow().UtcDateTime, batches, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var reclaimed = await ledger.RecoverExpiredLeasesAsync(Flow.Id, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            if (reclaimed > 0)
            {
                _log.LogInformation("Recovered the leases that ran out: {Count} record(s) settled or handed back.", reclaimed);
            }

            var more = await worker.DrainAsync(submissionId, ct).ConfigureAwait(false);
            total = total.Add(more);
            if (more.Processed == 0 && more.Batches == 0 && more.Waiting == 0)
            {
                if (!await ledger.HasPendingAsync(Flow.Id, submissionId, _context.Time.GetUtcNow().UtcDateTime, batches, ct).ConfigureAwait(false))
                {
                    break;
                }

                _log.LogInformation("Records are still leased elsewhere; waiting {Seconds}s for the leases to settle.", (int)LeaseSettle.TotalSeconds);
                await Task.Delay(LeaseSettle, _context.Time, ct).ConfigureAwait(false);
            }
        }

        return total;
    }

    /// <summary>
    /// The work batches a share of key slices is planned into: every slice's batches are numbered in a namespace of its own
    /// (<see cref="SubmissionIntake.BatchBase"/>), and a share is a run of neighbouring slices, so its batches are one range.
    /// </summary>
    /// <exception cref="DeliveryException">The share is empty, or its slices are not neighbours, so its range would take in batches of another run's.</exception>
    internal static BatchRange BatchesOf(IReadOnlyList<int> share)
    {
        ArgumentNullException.ThrowIfNull(share);
        if (share.Count == 0)
        {
            throw new DeliveryException("A share of a fan-out names at least one key slice.");
        }

        var first = share.Min();
        var last = share.Max();
        if (last - first + 1 != share.Distinct().Count())
        {
            throw new DeliveryException(
                $"Slices {KeySlices.Describe(share.Order().ToList())} are not neighbours: a run delivers a share of neighbouring key slices, whose work batches are one range no other run's are in.");
        }

        return new BatchRange(SubmissionIntake.BatchBase(first), checked(SubmissionIntake.BatchBase(last) + SubmissionIntake.BatchesPerPartition));
    }

    /// <summary>
    /// Why this run fans nothing out whatever its read, or null when it can: the flow turned it off, or the run cannot queue
    /// runs.
    /// </summary>
    private string? FanOutUnavailable()
        => Flow.Parallelism.FanOut <= 0 ? "parallelism.fanOut is 0"
            : !_context.Dispatcher.Available || RunId is null ? "this run cannot queue runs of the flow: it runs outside the fleet"
            : null;

    /// <summary>Why <paramref name="records"/> <paramref name="what"/> are too few to fan out, or null when they are enough.</summary>
    private string? TooFewToFanOut(long records, string what)
        => records < Flow.Parallelism.FanOutMinRecords
            ? string.Create(CultureInfo.InvariantCulture, $"{records} {what}, fewer than parallelism.fanOutMinRecords ({Flow.Parallelism.FanOutMinRecords})")
            : null;

    /// <summary>The key ranges a submission recorded it was cut into, in slice order.</summary>
    private static IReadOnlyList<KeyRange> Recorded(SourceWindowDescription cut)
        => cut.Slices.OrderBy(s => s.Slice).Select(s => cut.Range(s.Slice)!.Value).ToList();

    /// <summary>
    /// The values a slice run of this one carries: <paramref name="values"/>, and the partition the flow is bound to, so a
    /// slice run binds to the partition this run delivers to and keeps its records in the same ledger. With the central
    /// configuration this run resolves with, carried in the slice run's payload, it resolves every reference as this run
    /// did, whatever the node it lands on holds.
    /// </summary>
    private IReadOnlyDictionary<string, string> MemberValues(IReadOnlyDictionary<string, string> values)
    {
        if (Flow.Partition is not { } partition)
        {
            return values;
        }

        return new Dictionary<string, string>(values, StringComparer.Ordinal) { [PartitionNames.RunValue] = partition };
    }

    /// <summary>
    /// Waits until every slice run of <paramref name="handle"/> has ended. One no node has started is not waited for for
    /// ever: once none is running and none has started or ended for <paramref name="stall"/> since <paramref name="since"/>,
    /// the ones still queued are stopped (the platform cancels a queued run outright, and asks one a node took meanwhile to
    /// stop) and named in <see cref="MemberWait.Stopped"/>, for this run to deliver their shares. Every slot of the pool can be
    /// holding work its own runs wait on, a coordinating run's own slot among them, so waiting on would wait for ever.
    /// </summary>
    private async Task<MemberWait> WaitForMembersAsync(FanOutHandle handle, DateTimeOffset since, TimeSpan stall, CancellationToken ct)
    {
        var dispatcher = _context.Dispatcher;
        var lastProgress = _context.Time.GetUtcNow();
        var lastChange = since;
        var seen = (Running: 0, Ended: 0);
        var stopping = false;
        IReadOnlySet<Guid> stopped = new HashSet<Guid>();
        while (true)
        {
            var state = await dispatcher.StateAsync(handle, ct).ConfigureAwait(false);
            if (state.AllTerminal)
            {
                _log.LogInformation("The slice runs finished: {State}.", state);
                return new MemberWait(state, stopped);
            }

            var now = _context.Time.GetUtcNow();
            var shape = (Running: state.Running, Ended: state.Members.Count(m => m.IsTerminal));
            if (shape != seen)
            {
                seen = shape;
                lastChange = now;
            }

            if (!stopping && state.Running == 0 && now - lastChange >= stall)
            {
                stopping = true;
                stopped = state.Members.Where(m => !m.IsTerminal).Select(m => m.RunId).ToHashSet();
                _log.LogInformation(
                    "No node has started {Count} slice run(s) in {Seconds}s and none is running: every place in the pool holds work. This run stops them and delivers their shares itself.",
                    stopped.Count, (int)(now - lastChange).TotalSeconds);
                await dispatcher.CancelAsync(handle, ct).ConfigureAwait(false);

                // Asked again at once: the queued ones have ended by now, and one a node took meanwhile ends when it hears the stop.
                continue;
            }

            if (_context.Trace is { } trace ? trace.ProgressDue("members", now) : now - lastProgress >= MemberProgress)
            {
                lastProgress = now;
                _log.LogInformation(RunTrace.Bounded, "Waiting for the slice runs: {State}.", state);
            }

            await Task.Delay(MemberPoll, _context.Time, ct).ConfigureAwait(false);
        }
    }

    private async Task CancelMembersAsync(FanOutHandle handle)
    {
        try
        {
            await _context.Dispatcher.CancelAsync(handle, CancellationToken.None).ConfigureAwait(false);
            _log.LogInformation("Cancelled the slice runs of group {GroupId} with this run.", handle.GroupId);
        }
        catch (Exception ex) when (ex is DeliveryException or InvalidOperationException or System.Data.Common.DbException)
        {
            _log.LogWarning("Could not cancel the slice runs of group {GroupId}: {Message}", handle.GroupId, HeaderRedaction.RedactMessage(ex.Message));
        }
    }
}
