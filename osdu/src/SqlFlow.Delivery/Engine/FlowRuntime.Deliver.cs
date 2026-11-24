using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// What a drain did: what its worker sent, and what the sweep a drain of the whole flow ends with undid of unfinished
/// deliveries (osdu/docs/reference/concepts/artifacts-and-undo.md, When the undo runs, and what it is given); a drain of
/// one submission does not sweep, and its undo is empty.
/// </summary>
public sealed record DrainResult(WorkerSummary Work, UndoSummary Undone)
{
    /// <summary>What the drain's after step did (osdu/docs/reference/flow/delivery.md, cache); null when the pipeline names none.</summary>
    public Snapshots.CacheStepOutcome? CacheAfter { get; init; }

    public override string ToString() => Undone.Idle ? Work.ToString() : $"{Work}; {Undone.Describe()}";
}

/// <summary>What a deliver run did: the intake, the drain, the submission it left, and how far it fanned out.</summary>
public sealed record RunResult(IntakeResult Intake, WorkerSummary Work, SubmissionState Submission, int Members = 0)
{
    /// <summary>
    /// What the sweep the run ended with undid of unfinished deliveries
    /// (osdu/docs/reference/concepts/artifacts-and-undo.md, When the undo runs, and what it is given).
    /// </summary>
    public UndoSummary Undone { get; init; } = UndoSummary.Empty;

    /// <summary>
    /// What the run's before step did (osdu/docs/reference/flow/delivery.md, cache): the types it brought up to date from
    /// OSDU before it rendered. Null when the pipeline names none.
    /// </summary>
    public Snapshots.CacheStepOutcome? CacheBefore { get; init; }

    /// <summary>
    /// What the run's after step did (osdu/docs/reference/flow/delivery.md, cache): the records it changed in OSDU, read back
    /// into the types it names. Null when the pipeline names none.
    /// </summary>
    public Snapshots.CacheStepOutcome? CacheAfter { get; init; }

    /// <summary>
    /// What the run did itself: a run re-sending two records of a delivered submission counts two, and one that found the
    /// submission already completed counts none. A fan-out root is the exception, because its members' planning and
    /// deliveries are summed only in the submission, so it counts the submission it covers.
    /// </summary>
    public RunCounts Own => Members > 0
        ? new RunCounts(
            Submission.Planned, Submission.SkippedUnchanged, Submission.AwaitingApproval, Submission.SkippedStale, Submission.UnchangedAtPush,
            Submission.Blocked, Submission.Delivered, Submission.Held, Submission.Failed, Work.Retried, Submission.BatchCount, Submission.Waiting)
        : new RunCounts(
            Intake.Counts.Planned, Intake.Counts.Skipped, Intake.Counts.AwaitingApproval, Intake.Counts.Stale, Work.Unchanged, Intake.Counts.Blocked,
            Work.Delivered, Intake.Counts.Held + Work.Held, Work.Failed, Work.Retried, Intake.Counts.Batches, Work.Waiting);

    /// <summary>
    /// Whether the run changed nothing: it planned, held, blocked and sent no record, and left none waiting on an approval
    /// or on a record it refers to. Rows it read and found unchanged or stale change nothing. The audit trail leaves such a
    /// run out unless asked for it.
    /// </summary>
    public bool Idle => Work.Processed == 0 && Undone.Idle
        && Own is { Planned: 0, AwaitingApproval: 0, UnchangedAtPush: 0, Blocked: 0, Delivered: 0, Held: 0, Failed: 0, Retried: 0, Waiting: 0 };
}

/// <summary>
/// The counts of what one deliver run did (<see cref="RunResult.Own"/>). <c>SkippedUnchanged</c> and <c>SkippedStale</c>
/// count rows it read and had no reason to send; <c>UnchangedAtPush</c> the records the final hash check found OSDU
/// already holding, settled without sending anything.
/// </summary>
public sealed record RunCounts(
    long Planned, long SkippedUnchanged, long AwaitingApproval, long SkippedStale, long UnchangedAtPush, long Blocked, long Delivered, long Held,
    long Failed, long Retried, int Batches, long Waiting)
{
    /// <summary>The counts that are not zero, as the audit trail shows the run: "3 delivered, 2 unchanged".</summary>
    public override string ToString() => CountLine.Of(
        "nothing to deliver",
        (Planned, "planned"), (Delivered, "delivered"), (SkippedUnchanged + UnchangedAtPush, "unchanged"), (AwaitingApproval, "awaiting approval"),
        (SkippedStale, "stale"), (Blocked, "blocked"), (Held, "held"), (Failed, "failed"), (Retried, "retrying"), (Waiting, "waiting"));
}

// The deliver operation and its steps run alone (osdu/docs/reference/concepts/submissions.md): what a plan would
// change, the deliver run that plans the records into a submission and sends them, fanned out when the flow asks for
// it (FlowRuntime.FanOut.cs), the intake alone, the drain of what is pending, and how a run that stopped closes the
// submission it was delivering.
public sealed partial class FlowRuntime
{
    /// <summary>Reads the source and reports what would change, every entry collected. Changes nothing, records nothing.</summary>
    public Task<DeliveryPlan> PlanAsync(bool force = false, CancellationToken ct = default)
        => Planner.PlanAsync(Flow, Mapping, Parameters, Selection, force, ct);

    /// <summary>
    /// Plan and send, fanned out across the fleet when the flow asks for it: the <c>deliver</c> operation. A run that names
    /// key slices (<see cref="Slices"/>) is a part of a split delivery, and delivers the rows of those slices of the delivery's
    /// submission alone.
    /// </summary>
    public Task<RunResult> RunAsync(bool force, CancellationToken ct = default)
        => TrackAsync("deliver", new { force, source = Flow.Source.Record.Object, selection = Selection.Describe(), parameters = Parameters, nodes = Flow.Parallelism.Nodes, slices = Slices is null ? null : KeySlices.Describe(Slices) }, null, async () =>
        {
            if (Slices is { Count: > 0 } share)
            {
                // A part never closes the submission it works on, however it ends: the delivery's first run does.
                try
                {
                    return Tracked(await DeliverShareAsync(share, force, ct).ConfigureAwait(false));
                }
                finally
                {
                    await HeldSubmissions.ReleaseAllAsync().ConfigureAwait(false);
                }
            }

            var started = _context.Time.GetUtcNow().UtcDateTime;
            await EnsureLegalTagsAsync(ct).ConfigureAwait(false);
            await EnsureRestrictedAccessAsync(ct).ConfigureAwait(false);

            // The cache the pipeline needs current is brought up to date before anything is read; when it cannot be, the run
            // stops here, with nothing sent.
            var before = await BringCacheUpToDateAsync(ct).ConfigureAwait(false);
            await ReleaseEndedWaitsAsync(ct).ConfigureAwait(false);
            FanOutHandle? handle = null;
            try
            {
                var (intake, fanned) = await PlanAndFanOutAsync(force, h => handle = h, ct).ConfigureAwait(false);
                handle = null;
                if (!intake.AlreadyProcessed && fanned is null)
                {
                    // What the planning could not render counts before anything is sent; a guard it trips cancels the run here.
                    Guard?.Planned(intake.Counts.Held, intake.Counts.Planned);
                    _context.Trace?.AddPlanned(intake.Counts.Planned);
                    ct.ThrowIfCancellationRequested();
                }

                if (intake.NothingToDo && fanned is null)
                {
                    // A plan with nothing new is not a run with nothing to send: a record released back to pending with
                    // its rendered document, or one a stopped run left due, still waits in this submission, and the plan
                    // skips it because the source row is exactly what it already queues. What is due now is sent; a record
                    // in backoff is not waited for, because a run that planned nothing must not sit out a retry's wait.
                    var worker = await WorkerAsync(ct).ConfigureAwait(false);
                    var sent = await PassUntilNothingClaimableAsync(worker, intake.Submission.SubmissionId, ct).ConfigureAwait(false);
                    sent = sent.Add(await SendOrphanedLeasesAsync(worker, intake.Submission.SubmissionId, ct).ConfigureAwait(false));
                    var leftovers = await SendUnattendedAsync(worker, intake.Submission.SubmissionId, ct).ConfigureAwait(false);
                    var swept = await SweepAfterRunAsync(ct).ConfigureAwait(false);
                    if (sent.Processed == 0 && leftovers.Processed == 0 && sent.Waiting == 0 && leftovers.Waiting == 0)
                    {
                        return Tracked(new RunResult(intake, WorkerSummary.Empty, intake.Submission)
                        {
                            Undone = swept,
                            CacheBefore = before,
                            CacheAfter = swept.Idle ? null : await PutIntoCacheAsync(started, ct).ConfigureAwait(false),
                        });
                    }

                    var settled = sent.Processed > 0 || sent.Waiting > 0
                        ? await Intake.CompleteAsync(intake.Submission.SubmissionId, Flow.Id, ct).ConfigureAwait(false)
                        : intake.Submission;
                    var sentAfter = await PutIntoCacheAsync(started, ct).ConfigureAwait(false);
                    return Tracked(new RunResult(intake, sent.Add(leftovers), settled) { Undone = swept, CacheBefore = before, CacheAfter = sentAfter });
                }

                // A fanned-out run has sent every share by now; a run alone sends what it planned.
                var work = fanned?.Work ?? await SendShareAsync(intake.Submission.SubmissionId, null, null, ct).ConfigureAwait(false);
                var submission = await Intake.CompleteAsync(intake.Submission.SubmissionId, Flow.Id, ct).ConfigureAwait(false);

                // Its own records sent, the run takes what submissions no run works on any more still hold: records released
                // after their run settled, and submissions whose run ended before settling them.
                var unattended = await SendUnattendedAsync(await WorkerAsync(ct).ConfigureAwait(false), intake.Submission.SubmissionId, ct).ConfigureAwait(false);

                // Every delivery of the run has ended, its fan-out members' too: what unfinished deliveries left is undone now.
                var undone = await SweepAfterRunAsync(ct).ConfigureAwait(false);

                // What the run changed in OSDU, its members' too, goes into the cache types the pipeline names, so the pipelines
                // that run after it render against it.
                var after = await PutIntoCacheAsync(started, ct).ConfigureAwait(false);
                return Tracked(new RunResult(intake, work.Add(unattended), submission, fanned?.Members ?? 0)
                {
                    Undone = undone,
                    CacheBefore = before,
                    CacheAfter = after,
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (handle is not null)
                {
                    // A cancelled root takes its members with it.
                    await CancelMembersAsync(handle).ConfigureAwait(false);
                }

                await SettleStoppedAsync().ConfigureAwait(false);
                throw;
            }
            catch when (handle is not null)
            {
                // A root that fails ends its members before its failure is reported, as a cancelled one does: the platform ends
                // them only once it writes the failure, and a member left working meanwhile works for a run that is gone.
                await CancelMembersAsync(handle).ConfigureAwait(false);
                throw;
            }
            finally
            {
                // Whatever ended the run, it works on no submission any more: a later run may take over what it left.
                await HeldSubmissions.ReleaseAllAsync().ConfigureAwait(false);
            }

            // What the audit trail records of the run: what it did itself, the submission it worked on, and whether it changed nothing.
            static (RunResult, string, Guid?, bool) Tracked(RunResult run) => (run, run.Own.ToString(), run.Submission.SubmissionId, run.Idle);
        }, ct);

    /// <summary>
    /// Closes the submission this run was delivering when it stopped (its failure guard tripped, or the run was cancelled),
    /// failed with why, while records of it are still pending, so the flow's next run sends them as it sends what any
    /// closed submission still holds. It is bookkeeping after the stop, so it runs whatever the run's token says; a ledger
    /// that cannot be reached leaves the submission open, and a drain of it sends the rest.
    /// </summary>
    private async Task SettleStoppedAsync()
    {
        if (SubmissionId is not { } submissionId || _context.Ledger is null)
        {
            return;
        }

        var reason = Guard?.Reason is { } tripped
            ? $"stopped: {tripped}"
            : "stopped: the run was cancelled before its records were all sent";
        try
        {
            await Intake.StopAsync(submissionId, Flow.Id, reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeliveryException or System.Data.Common.DbException or InvalidOperationException or TimeoutException)
        {
            _log.LogWarning(
                "Submission {SubmissionId} stays open after the stop ({Message}); a drain of it sends the records it still holds.",
                submissionId, HeaderRedaction.RedactMessage(ex.Message));
        }
    }

    /// <summary>Intake only: plan this run's records (or a member's share of the slices) into work batches.</summary>
    public Task<IntakeResult> IntakeAsync(bool force, CancellationToken ct = default)
        => TrackAsync("intake", new { force, source = Flow.Source.Record.Object, selection = Selection.Describe(), parameters = Parameters, slices = Slices is null ? null : KeySlices.Describe(Slices) }, null, async () =>
        {
            await EnsureLegalTagsAsync(ct).ConfigureAwait(false);
            await EnsureRestrictedAccessAsync(ct).ConfigureAwait(false);
            var intake = await Intake.IntakeAsync(Flow, Mapping, Parameters, Request, force, ct).ConfigureAwait(false);
            return (intake, intake.AlreadyProcessed ? "the submission was already planned" : intake.Counts.Headline, intake.Submission.SubmissionId, intake.Counts.Idle);
        }, ct);

    /// <summary>Drains the pending work of the flow (one pass, or until nothing is due), optionally of one submission: the <c>drain</c> operation.</summary>
    public Task<DrainResult> WorkAsync(bool once, Guid? submissionId = null, CancellationToken ct = default)
        => TrackAsync("drain", new { once, submissionId }, null, async () =>
        {
            var started = _context.Time.GetUtcNow().UtcDateTime;
            if (submissionId is null)
            {
                // A drain of the whole flow ends with the after step, so a step the partition's cache cannot take is refused
                // before anything is sent.
                await CacheStepsAsync(ct).ConfigureAwait(false);
            }

            await ReleaseEndedWaitsAsync(ct).ConfigureAwait(false);
            var worker = await WorkerAsync(ct).ConfigureAwait(false);
            if (submissionId is { } s)
            {
                // A drain of one submission works on it as its run does (a fan-out member's drain, an operator's), so it
                // holds it for as long as it drains, and no later run takes it over meanwhile.
                var held = await HeldSubmissions.HoldAsync(s, ct).ConfigureAwait(false);
                try
                {
                    var drained = once ? await worker.PassAsync(s, ct).ConfigureAwait(false) : await worker.DrainAsync(s, ct).ConfigureAwait(false);
                    await Intake.CompleteAsync(s, Flow.Id, ct).ConfigureAwait(false);
                    return (new DrainResult(drained, UndoSummary.Empty), drained.Headline, submissionId, drained.Idle);
                }
                finally
                {
                    if (held)
                    {
                        await HeldSubmissions.ReleaseAsync(s).ConfigureAwait(false);
                    }
                }
            }

            var summary = once ? await worker.PassAsync(null, ct).ConfigureAwait(false) : await worker.DrainAsync(null, ct).ConfigureAwait(false);

            // A drain of the whole flow ends with the sweep a deliver run ends with. A drain of a submission does not: it is what a
            // fan-out member runs while its coordinator and other members deliver, and the coordinator sweeps once they are done.
            var undone = await SweepAfterRunAsync(ct).ConfigureAwait(false);
            var after = summary.Delivered > 0 || !undone.Idle ? await PutIntoCacheAsync(started, ct).ConfigureAwait(false) : null;
            var headline = undone.Idle ? summary.Headline : $"{summary.Headline}; {undone.Describe()}";
            return (new DrainResult(summary, undone) { CacheAfter = after }, headline, submissionId, summary.Idle && undone.Idle);
        }, ct);
}
