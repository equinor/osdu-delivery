using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Intake;
using SqlFlow.Delivery.Engine.Worker;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine;

// What a deliver run sends besides what it planned (osdu/docs/reference/concepts/submissions.md, Recovering a stopped
// submission): the records a stopped run left leased, each lease waited out and reclaimed, and the submissions no run
// works on any more, each taken over, passed over until nothing of it is claimable, and closed.
public sealed partial class FlowRuntime
{
    /// <summary>
    /// Sends what a stopped run left leased in a submission. The platform runs one execution of a flow at a time, so a
    /// record still leased when a run of that flow starts belongs to a worker that is gone: a control plane or node
    /// stopped mid-delivery, whose run was recovered and requeued. Each such lease is waited out, reclaimed (the ledger
    /// notes why) and the record sent, its completed steps resumed. A lease running out further ahead than the flow's
    /// own lease is not a stopped run's, so it is left alone with a warning, and records in backoff are not waited for.
    /// </summary>
    private async Task<WorkerSummary> SendOrphanedLeasesAsync(DeliveryWorker worker, Guid submissionId, CancellationToken ct)
    {
        var ledger = RequireLedger();
        var longest = TimeSpan.FromSeconds(Flow.Reliability.LeaseSeconds) + LeaseExpiryMargin;
        var total = WorkerSummary.Empty;
        while (await ledger.NextLeaseExpiryAsync(Flow.Id, submissionId, ct).ConfigureAwait(false) is { } expiry)
        {
            ct.ThrowIfCancellationRequested();
            var now = _context.Time.GetUtcNow().UtcDateTime;
            if (expiry >= now)
            {
                var wait = expiry - now + LeaseExpiryMargin;
                if (wait > longest)
                {
                    _log.LogWarning(
                        "A record of submission {SubmissionId} is leased until {Expiry:o}, further ahead than this flow's lease of {Seconds}s, so a running worker holds it and it is left alone.",
                        submissionId, expiry, Flow.Reliability.LeaseSeconds);
                    break;
                }

                _log.LogInformation(
                    "A record of submission {SubmissionId} is still leased by a run that stopped; waiting {Seconds}s for the lease to run out.",
                    submissionId, (int)Math.Ceiling(wait.TotalSeconds));
                await Task.Delay(wait, _context.Time, ct).ConfigureAwait(false);
            }

            var reclaimed = await ledger.RecoverExpiredLeasesAsync(Flow.Id, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            var sent = await PassUntilNothingClaimableAsync(worker, submissionId, ct).ConfigureAwait(false);
            total = total.Add(sent);
            if (reclaimed == 0 && sent.Processed == 0)
            {
                // The wait ended without the lease running out, or the lease was reclaimed elsewhere and nothing of it is
                // left to send: either way there is nothing this run can take over.
                _log.LogWarning(
                    "The lease on a record of submission {SubmissionId} (due to run out at {Expiry:o}) could not be reclaimed, so the record is left for a later run.",
                    submissionId, expiry);
                break;
            }

            if (reclaimed > 0)
            {
                _log.LogInformation("Recovered the leases a stopped run left behind ({Count} record(s) settled or handed back), and sent {Sent}.", reclaimed, sent.Processed);
            }
        }

        return total;
    }

    /// <summary>Claim passes over one submission until nothing of it is claimable, without waiting for records in backoff.</summary>
    private static async Task<WorkerSummary> PassUntilNothingClaimableAsync(DeliveryWorker worker, Guid submissionId, CancellationToken ct)
    {
        var total = WorkerSummary.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pass = await worker.PassAsync(submissionId, ct).ConfigureAwait(false);
            total = total.Add(pass);
            if (pass.Processed == 0 && pass.Batches == 0 && pass.Waiting == 0)
            {
                return total;
            }
        }
    }

    /// <summary>
    /// Sends what submissions of the flow no run works on any more still hold, <see cref="UnattendedSubmissionsPerPage"/>
    /// submissions at a time (osdu/docs/reference/concepts/submissions.md, Recovering a stopped submission). Two kinds belong
    /// to no run: a settled submission holding records released back to pending with their rendered documents after its
    /// run was over, and a submission its run left received, planned or running when it stopped (its node was lost, its
    /// process ended, it ran out of attempts) with records still planned in its work batches. A newer plan skips those
    /// records, because their rows are what they already queue, so without this they would wait for a drain by hand.
    /// Each one is taken over only when no run holds it (<see cref="ILedgerSubmissions.TakeOverSubmissionAsync"/>), so
    /// a run still planning or sending it, a fan-out member included, is never raced, and only when no lease that has
    /// not run out holds its work; it is passed over until nothing of it is claimable (records in backoff are not
    /// waited for), and closed: a settled one has its totals counted again when anything of it was sent, and one its
    /// run left open is closed as its records stand, failed with why when records of it are still to be sent or its
    /// planning never finished. Every claim is the one a run of the submission makes, so a record is sent once,
    /// resuming after the steps a stopped try recorded. A submission is visited once per run: what is left of it is the
    /// next run's, so the run ends however many submissions it finds.
    /// </summary>
    private async Task<WorkerSummary> SendUnattendedAsync(DeliveryWorker worker, Guid current, CancellationToken ct)
    {
        var ledger = RequireLedger();
        var visited = new HashSet<Guid> { current };
        var total = WorkerSummary.Empty;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await ledger.ListUnattendedSubmissionsAsync(Flow.Id, visited, _context.Time.GetUtcNow().UtcDateTime, UnattendedSubmissionsPerPage, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return total;
            }

            foreach (var submission in page)
            {
                visited.Add(submission.SubmissionId);
                total = total.Add(await SendUnattendedAsync(worker, submission, ct).ConfigureAwait(false));
            }
        }
    }

    /// <summary>Takes over one submission no run may work on any more, sends what it holds and closes it (see above).</summary>
    private async Task<WorkerSummary> SendUnattendedAsync(DeliveryWorker worker, SubmissionState submission, CancellationToken ct)
    {
        var ledger = RequireLedger();
        var submissionId = submission.SubmissionId;
        var settled = submission.Status is SubmissionStatus.Completed or SubmissionStatus.Failed;
        await using var hold = await ledger.TakeOverSubmissionAsync(submissionId, ct).ConfigureAwait(false);
        if (hold is null)
        {
            _log.LogInformation(
                "Submission {SubmissionId} ({Status}) is held by a run that still works on it, so it is left to that run.",
                submissionId, Status(submission.Status));
            return WorkerSummary.Empty;
        }

        if (!settled)
        {
            _log.LogWarning(
                "Submission {SubmissionId} was left {Status} by {Run}, which no longer works on it: it ended before the submission was settled. This run takes it over, sends what it still holds and closes it.",
                submissionId, Status(submission.Status), submission.RunId is { } stoppedRun ? $"run {stoppedRun:D}" : "its run");
        }

        var sent = await PassUntilNothingClaimableAsync(worker, submissionId, ct).ConfigureAwait(false);
        if (settled)
        {
            if (sent.Processed > 0 || sent.Waiting > 0)
            {
                await Intake.CompleteAsync(submissionId, Flow.Id, ct).ConfigureAwait(false);
                _log.LogInformation(
                    "Sent {Count} record(s) that submission {SubmissionId} still held after it settled (released back to pending with their rendered documents).",
                    sent.Processed, submissionId);
            }

            return sent;
        }

        if (await ledger.IsSubmissionLeasedAsync(Flow.Id, submissionId, _context.Time.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false))
        {
            // A worker took up some of it meanwhile (a drain of the whole flow claims any submission's batches): closing it now
            // would count records that worker is still sending, so it is left open for the next run to close.
            _log.LogInformation(
                "Sent {Count} record(s) of submission {SubmissionId}; a worker is sending the rest under a lease of its own, so the submission is left open for the flow's next run to close.",
                sent.Processed, submissionId);
            return sent;
        }

        var closed = await Intake.StopAsync(submissionId, Flow.Id, StoppedReason(submission), ct).ConfigureAwait(false);
        _log.LogInformation(
            "Took over submission {SubmissionId}: sent {Count} record(s) it still held, and closed it {Status}: {Summary}.",
            submissionId, sent.Processed, Status(closed.Status), SubmissionIntake.Summarize(closed));
        return sent;
    }

    /// <summary>
    /// Why a submission this run took over is closed failed, when it is: records of it are still to be sent (in backoff for a
    /// retry), or the run that registered it ended before its planning finished.
    /// </summary>
    private string StoppedReason(SubmissionState submission)
    {
        var stopped = submission.RunId is { } run ? $"run {run:D}" : "the run that registered it";
        var takenBy = RunId is { } own ? $"run {own:D}" : "a later run of the flow";
        return submission.Status == SubmissionStatus.Received
            ? $"stopped: {stopped} ended before its planning finished; {takenBy} took it over and sent what it had planned, and the rows it had not reached are left to the flow's plans, which read them again: it moved no watermark and answered no request to plan them"
            : $"stopped: {stopped} ended before its records were all sent; {takenBy} took it over and sent what was due, and the flow's next run sends the rest";
    }

    private static string Status(SubmissionStatus status) => status.ToString().ToLowerInvariant();
}
