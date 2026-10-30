using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// The submissions runs work on, and the ones no run works on any more (osdu/docs/reference/concepts/submissions.md,
// Recovering a stopped submission). A run holds each submission it works on (SubmissionHold), from before it registers or
// reopens the submission until it is done with it, so a submission left received, planned or running with no holder and
// no lease that has not run out is one whose run ended before settling it. The next deliver run of the flow takes it
// over, sends what it holds and closes it; an attempt of a run the platform executes again resumes the one its interrupted
// attempt left.
public sealed partial class OsduLedger
{
    /// <summary>How long a run waits to hold a submission another run is taking over in that moment.</summary>
    internal const int HoldWaitMilliseconds = 30_000;

    /// <summary>The most unsettled submissions of one run the ledger reads: one per pass of the run, and a run makes few.</summary>
    private const int RunSubmissionsRead = 100;

    /// <summary>The statuses of a submission no run has settled yet.</summary>
    private static readonly string[] UnsettledStatuses =
        [StatusText.Of(SubmissionStatus.Received), StatusText.Of(SubmissionStatus.Planned), StatusText.Of(SubmissionStatus.Running)];

    public async Task<IReadOnlyList<SubmissionState>> ListUnattendedSubmissionsAsync(
        Guid flowId, IReadOnlyCollection<Guid> except, DateTime nowUtc, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(except);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(max, 1, 100);
        var unsettled = UnsettledStatuses;
        var excluded = except.ToArray();

        // The flow's unsettled submissions through the (PartitionId, FlowId, Status) index, less those a lease that has not
        // run out holds work of: a worker is sending them now.
        var stopped = await ReadAsync(
            db => Unleased(db, partition, flowId, nowUtc,
                    db.DeliverySubmissions.Where(s => s.PartitionId == partition && s.FlowId == flowId && unsettled.Contains(s.Status) && !excluded.Contains(s.SubmissionId)))
                .OrderBy(s => s.ReceivedUtc)
                .ThenBy(s => s.SubmissionId)
                .Take(take)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var found = new List<SubmissionState>(take);
        foreach (var row in stopped)
        {
            found.Add(await NamedAsync(row, ct).ConfigureAwait(false));
        }

        if (found.Count < take)
        {
            var named = except.Concat(found.Select(s => s.SubmissionId)).ToList();
            foreach (var id in await ListSettledSubmissionsWithDueWorkAsync(flowId, named, nowUtc, take - found.Count, ct).ConfigureAwait(false))
            {
                if (await GetSubmissionAsync(id, ct).ConfigureAwait(false) is { } settled)
                {
                    found.Add(settled);
                }
            }
        }

        return found;
    }

    public async Task<bool> IsSubmissionLeasedAsync(Guid flowId, Guid submissionId, DateTime nowUtc, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return false;
        }

        var delivering = StatusText.Of(RecordStatus.Delivering);
        return await ReadAsync(
            db => db.DeliveryLeases.AnyAsync(
                l => l.PartitionId == partition && l.FlowId == flowId && l.ExpiresUtc >= nowUtc
                    && (l.SubmissionId == submissionId
                        || db.DeliveryRecords.Any(r => r.LeaseOwner == l.Token && r.PartitionId == partition && r.FlowId == flowId && r.LastSubmissionId == submissionId && r.Status == delivering)),
                ct),
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SubmissionState>> ListUnsettledSubmissionsOfRunAsync(Guid flowId, Guid runId, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var unsettled = UnsettledStatuses;
        var rows = await ReadAsync(
            db => db.DeliverySubmissions
                .Where(s => s.RunId == runId && s.PartitionId == partition && s.FlowId == flowId && unsettled.Contains(s.Status))
                .OrderByDescending(s => s.ReceivedUtc)
                .ThenByDescending(s => s.SubmissionId)
                .Take(RunSubmissionsRead)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var named = new List<SubmissionState>(rows.Count);
        foreach (var row in rows)
        {
            named.Add(await NamedAsync(row, ct).ConfigureAwait(false));
        }

        return named;
    }

    public async Task<IReadOnlyList<SubmissionState>> ListSubmissionsOfRunAsync(Guid runId, CancellationToken ct = default)
    {
        var rows = await ReadAsync(
            db => db.DeliverySubmissions
                .Where(s => s.RunId == runId)
                .OrderByDescending(s => s.ReceivedUtc)
                .ThenByDescending(s => s.SubmissionId)
                .Take(RunSubmissionsRead)
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var named = new List<SubmissionState>(rows.Count);
        foreach (var row in rows)
        {
            named.Add(await NamedAsync(row, ct).ConfigureAwait(false));
        }

        return named;
    }

    public async Task<SubmissionHold> HoldSubmissionAsync(Guid submissionId, CancellationToken ct = default)
    {
        // A context of its own: its connection carries the hold for as long as the run keeps it.
        var db = Open();
        try
        {
            var granted = await SqlServerLedgerBulk.HoldSubmissionAsync(db, SubmissionHold.Resource(submissionId), HoldWaitMilliseconds, ct).ConfigureAwait(false);
            if (granted < 0)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Submission {submissionId:D} could not be held for this run within {HoldWaitMilliseconds / 1000} seconds (sp_getapplock answered {granted}): another run of the flow was taking it over, as a deliver run does with a submission no run works on any more, and that run sends what it holds. Run this one again once that run has ended."));
            }

            return new SubmissionHold(db, submissionId);
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<SubmissionHold?> TakeOverSubmissionAsync(Guid submissionId, CancellationToken ct = default)
    {
        var db = Open();
        try
        {
            if (await SqlServerLedgerBulk.TakeOverSubmissionAsync(db, SubmissionHold.Resource(submissionId), ct).ConfigureAwait(false) < 0)
            {
                await db.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new SubmissionHold(db, submissionId);
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The submissions of <paramref name="submissions"/> no lease of the flow that has not run out at <paramref name="nowUtc"/>
    /// holds work of: none of their batches, and none of their records under a lease of the whole flow. The leases are read
    /// through the flow's (PartitionId, FlowId, ExpiresUtc) index, the records a lease holds through the lease owner index.
    /// </summary>
    private static IQueryable<DeliverySubmission> Unleased(OsduDbContext db, short partition, Guid flowId, DateTime nowUtc, IQueryable<DeliverySubmission> submissions)
    {
        var delivering = StatusText.Of(RecordStatus.Delivering);
        return submissions.Where(s => !db.DeliveryLeases.Any(l =>
            l.PartitionId == partition && l.FlowId == flowId && l.ExpiresUtc >= nowUtc
            && (l.SubmissionId == s.SubmissionId
                || db.DeliveryRecords.Any(r => r.LeaseOwner == l.Token && r.PartitionId == partition && r.FlowId == flowId && r.LastSubmissionId == s.SubmissionId && r.Status == delivering))));
    }
}
