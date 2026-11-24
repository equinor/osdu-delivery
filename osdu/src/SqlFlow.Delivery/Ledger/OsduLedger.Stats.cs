using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

// A flow's statistics, counted from its records (osdu/docs/reference/concepts/ledger.md, Statistics are counted from
// the records): the records in a status, whether it holds any, the partitions it delivered to, and its dashboard's
// counts.
public sealed partial class OsduLedger
{
    public async Task<long> CountAsync(Guid flowId, Guid? submissionId, RecordStatus status, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return 0;
        }

        var text = StatusText.Of(status);
        return await ReadAsync(
            db => db.DeliveryRecords.LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && (submissionId == null || r.LastSubmissionId == submissionId) && r.Status == text, ct),
            ct).ConfigureAwait(false);
    }

    public async Task<bool> HoldsRecordsAsync(Guid flowId, CancellationToken ct = default)
        => await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is { } partition
            && await ReadAsync(db => db.DeliveryRecords.AnyAsync(r => r.PartitionId == partition && r.FlowId == flowId, ct), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<PartitionRecords>> DeliveredPartitionsAsync(Guid flowId, CancellationToken ct = default)
    {
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        // An OSDU id is <partition>:<entity type>:<id>, so the partition is everything before its first colon. A record is
        // counted by the id it was delivered as, or by the id it claimed when it has not been delivered yet. The two columns
        // are compared under different collations, so each is grouped on its own rather than coalesced into one expression
        // the server could not collate. The queries run on SQL Server, where IndexOf(string) translates to CHARINDEX; the
        // char overload the analyzer prefers has no translation there.
#pragma warning disable CA1866 // Use 'string.IndexOf(char)': the expression is translated to SQL, not run in .NET.
        var delivered = await ReadAsync(
            db => db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && r.TargetId != null && r.TargetId.IndexOf(":") > 0)
                .GroupBy(r => r.TargetId!.Substring(0, r.TargetId.IndexOf(":")))
                .Select(g => new { Partition = g.Key, Records = g.LongCount() })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
        var claimed = await ReadAsync(
            db => db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && r.TargetId == null && r.ClaimedTargetId != null && r.ClaimedTargetId.IndexOf(":") > 0)
                .GroupBy(r => r.ClaimedTargetId!.Substring(0, r.ClaimedTargetId.IndexOf(":")))
                .Select(g => new { Partition = g.Key, Records = g.LongCount() })
                .ToListAsync(ct),
            ct).ConfigureAwait(false);
#pragma warning restore CA1866
        return delivered.Concat(claimed)
            .GroupBy(c => c.Partition, StringComparer.Ordinal)
            .Select(g => new PartitionRecords(g.Key, g.Sum(c => c.Records)))
            .OrderByDescending(c => c.Records)
            .ThenBy(c => c.Partition, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<FlowStats> StatsAsync(Guid flowId, DateTime nowUtc, CancellationToken ct = default)
    {
        // A ledger no run has registered holds nothing to count.
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return new FlowStats();
        }

        var stats = await ReadAsync(db => StatsAsync(db, partition, flowId, nowUtc, ct), ct).ConfigureAwait(false);
        return stats.LastSubmission is { } last ? stats with { LastSubmission = last with { Partition = await PartitionNameAsync(partition, ct).ConfigureAwait(false) } } : stats;
    }

    /// <summary>A flow's statistics, every count read from one snapshot of the ledger.</summary>
    private static async Task<FlowStats> StatsAsync(OsduDbContext db, short partition, Guid flowId, DateTime nowUtc, CancellationToken ct)
    {
        var since = nowUtc.AddHours(-24);
        var (byStatus, drifted, last24, stepsHeld, restricted, incomplete, both) = await CountFromRecordsAsync(db, partition, flowId, since, ct).ConfigureAwait(false);
        var (blocked, stranded) = await SqlServerLedgerBulk.ParkedCountsAsync(db, partition, flowId, ct).ConfigureAwait(false);

        // The held records an adoption may be asked of, read from the held records alone through the status index: the test is
        // the one an adoption makes (OccupiedHolds.Adoptable), the id a hold kept, or a hold whose message names one.
        var held = StatusText.Of(RecordStatus.Held);
        var adoptable = await db.DeliveryRecords
            .LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.Status == held && r.ClaimedTargetId == null && r.LeaseOwner == null
                && r.AdoptRequestedUtc == null && (r.OccupiedTargetId != null || r.LastError!.StartsWith(OccupiedHolds.Prefix)), ct)
            .ConfigureAwait(false);
        var lastDelivered = await db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId).MaxAsync(r => r.LastDeliveredUtc, ct).ConfigureAwait(false);
        var lastVerified = await db.DeliveryRecords.Where(r => r.PartitionId == partition && r.FlowId == flowId).MaxAsync(r => r.LastVerifiedUtc, ct).ConfigureAwait(false);
        var submissions = await db.DeliverySubmissions.LongCountAsync(s => s.PartitionId == partition && s.FlowId == flowId, ct).ConfigureAwait(false);
        var lastSubmission = await db.DeliverySubmissions.Where(s => s.PartitionId == partition && s.FlowId == flowId).OrderByDescending(s => s.ReceivedUtc).FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return new FlowStats
        {
            Total = byStatus.Values.Sum(),
            Pending = byStatus.GetValueOrDefault("pending"),
            Delivering = byStatus.GetValueOrDefault("delivering"),
            Delivered = byStatus.GetValueOrDefault("delivered"),
            Held = byStatus.GetValueOrDefault("held"),
            Failed = byStatus.GetValueOrDefault("failed"),
            Deleted = byStatus.GetValueOrDefault("deleted"),
            Reverted = byStatus.GetValueOrDefault("reverted"),
            Waiting = byStatus.GetValueOrDefault("waiting"),
            Blocked = blocked,
            Stranded = stranded,
            Adoptable = adoptable,
            Drifted = drifted,
            StepsHeld = stepsHeld,
            Restricted = restricted,
            RestrictedUntilComplete = incomplete,
            AccessRestricted = restricted + incomplete - both,
            DeliveredLast24h = last24,
            LastDeliveredUtc = lastDelivered,
            LastVerifiedUtc = lastVerified,
            Submissions = submissions,
            LastSubmission = lastSubmission is null ? null : ToState(lastSubmission),
        };
    }

    /// <summary>
    /// A flow's counts, from the records themselves through the status index. There is no maintained aggregate: one
    /// kept the counts in a few rows per flow and was written inside the transaction of every record write, so every
    /// node delivering a flow met every other one on those rows.
    /// </summary>
    private static async Task<(Dictionary<string, long> ByStatus, long Drifted, long DeliveredSince, long StepsHeld, long Restricted, long Incomplete, long Both)> CountFromRecordsAsync(
        OsduDbContext db, short partition, Guid flowId, DateTime since, CancellationToken ct)
    {
        var byStatus = await db.DeliveryRecords
            .Where(r => r.PartitionId == partition && r.FlowId == flowId)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(c => c.Status, c => c.Count, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
        var drifted = await db.DeliveryRecords
            .LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && (r.LastVerifyOutcome == "drifted" || r.LastVerifyOutcome == "missing" || r.LastVerifyOutcome == "served"), ct)
            .ConfigureAwait(false);
        var deliveredSince = await db.DeliveryRecords
            .LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.LastDeliveredUtc != null && r.LastDeliveredUtc >= since, ct)
            .ConfigureAwait(false);
        var stepsHeld = await db.DeliveryRecords
            .LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.HeldSteps != null, ct)
            .ConfigureAwait(false);
        var restricted = await db.DeliveryRecords
            .LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.RestrictionId != null, ct)
            .ConfigureAwait(false);

        // The records restricted until complete, read from the index that holds them alone.
        var incomplete = await db.DeliveryRecords
            .LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.IncompleteRestrictionId != null, ct)
            .ConfigureAwait(false);

        // The records restricted both ways, read from the operator's restrictions' index, so a record restricted either way
        // counts once in the flow's Restricted figure.
        var both = restricted == 0 || incomplete == 0
            ? 0
            : await db.DeliveryRecords
                .LongCountAsync(r => r.PartitionId == partition && r.FlowId == flowId && r.RestrictionId != null && r.IncompleteRestrictionId != null, ct)
                .ConfigureAwait(false);
        return (byStatus, drifted, deliveredSince, stepsHeld, restricted, incomplete, both);
    }
}
