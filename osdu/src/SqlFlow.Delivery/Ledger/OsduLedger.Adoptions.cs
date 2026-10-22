using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// Adopting the records OSDU already holds at the ids records' keys make (osdu/docs/reference/concepts/record-lifecycle.md,
// Adopting a record OSDU already holds). The intake holds a record whose id OSDU already holds a record at that no record of
// a ledger claimed, and keeps the id and the version on the hold; an operator who knows the OSDU record is this record's
// (written by an earlier ledger, or another instance of the delivery) adopts it here, and the record claims the id as if it
// had queued a document. Everything after the claim is what a claimed record does: the next run writes it to the id, and a
// read back, a removal and an inventory reach it through the claim.
public sealed partial class OsduLedger
{
    /// <summary>How many times one chunk of an adoption is tried when an intake claimed one of its ids in that moment.</summary>
    private const int AdoptionAttempts = 3;

    public async Task<IReadOnlyList<RecordAdoption>> AdoptOccupiedAsync(
        Guid flowId, ReleaseSelection selection, string actor, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        if (selection.Keys is null && selection.Problem is null)
        {
            throw new ArgumentException(
                "An adoption names its records, or the problem that keeps them held; it never reaches every blocked record of a flow.", nameof(selection));
        }

        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var adopted = new List<RecordAdoption>();
        if (selection.Keys is { } named)
        {
            foreach (var chunk in named.Select(k => k.Value).Distinct().Chunk(ChunkSize))
            {
                ct.ThrowIfCancellationRequested();
                adopted.AddRange(await AdoptChunkAsync(partition, flowId, chunk, actor, activityId, nowUtc, ct).ConfigureAwait(false));
            }

            return adopted;
        }

        // The problem's records are walked through the problem index a page at a time, as a release of a problem walks them:
        // a record adopted leaves the index, one left behind stays where the walk has passed, so the walk ends.
        await using var db = Open();
        var after = SqlServerLedgerBulk.WalkPosition.Start;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var position = after;
            var page = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.ReleasePageAsync(db, partition, flowId, selection.Problem, null, position, WriteSlice, ct), ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return adopted;
            }

            foreach (var chunk in page.Select(p => p.DeliveryKey).Chunk(ChunkSize))
            {
                adopted.AddRange(await AdoptChunkAsync(partition, flowId, chunk, actor, activityId, nowUtc, ct).ConfigureAwait(false));
            }

            if (page.Count < WriteSlice)
            {
                return adopted;
            }

            after = page[^1];
        }
    }

    /// <summary>
    /// One chunk of <see cref="AdoptOccupiedAsync"/>, tried again when an intake claimed one of its ids between the chunk's
    /// read and its write (the claim's unique index refuses the write): the chunk then reads the claim, and leaves that record.
    /// </summary>
    private async Task<IReadOnlyList<RecordAdoption>> AdoptChunkAsync(
        short partition, Guid flowId, IReadOnlyList<Guid> ids, string actor, long? activityId, DateTime nowUtc, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await AdoptOnceAsync(partition, flowId, ids, actor, activityId, nowUtc, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < AdoptionAttempts && SqlServerLedgerBulk.IsContention(ex))
            {
                // Waiting a moment, longer each time and never the same for two writers, keeps the two from meeting in the
                // same way again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The records of <paramref name="ids"/> held for an id OSDU holds, adopted in one transaction with their attempts and the
    /// activity's links to them, so a record never claims an id nothing records. The records are read under repeatable read,
    /// so the hold each is adopted from is the one it is written over.
    /// </summary>
    private async Task<IReadOnlyList<RecordAdoption>> AdoptOnceAsync(
        short partition, Guid flowId, IReadOnlyList<Guid> ids, string actor, long? activityId, DateTime nowUtc, CancellationToken ct)
    {
        var held = StatusText.Of(RecordStatus.Held);
        await using var db = Open();
        // A retried run of this block starts from the database, not from what a rolled back one left tracked, and the
        // connection leaves it at read committed, so the next read on it does not hold repeatable read locks.
        return await PooledIsolation.InTransactionAsync(db, System.Data.IsolationLevel.RepeatableRead, async () =>
        {
            var entities = await db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && ids.Contains(r.DeliveryKey)
                    && r.Status == held && r.ClaimedTargetId == null && r.OccupiedTargetId != null && r.LeaseOwner == null)
                .OrderBy(r => r.DeliveryKey)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var wanted = entities.Select(e => e.OccupiedTargetId!).Distinct(StringComparer.Ordinal).ToList();

            // A claim is the ledger's across every flow and partition: an id another record claimed since the hold is never
            // taken, and of two records of the chunk held for one id, the first in key order takes it.
            var taken = (await db.DeliveryRecords.AsNoTracking()
                    .Where(r => r.ClaimedTargetId != null && wanted.Contains(r.ClaimedTargetId))
                    .Select(r => r.ClaimedTargetId!)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

            var done = new List<RecordAdoption>();
            foreach (var entity in entities)
            {
                var targetId = entity.OccupiedTargetId!;
                if (!taken.Add(targetId))
                {
                    continue;
                }

                var version = entity.OccupiedTargetVersion;
                var at = version is { } v ? string.Create(CultureInfo.InvariantCulture, $" at version {v}") : string.Empty;
                var note = $"adopted by {actor}: the record claims {targetId}, the OSDU id made from its key, where OSDU held a record{at} that no "
                    + "record of this ledger had claimed (written by another system, or another instance of the delivery); the next deliver run writes this row there";
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    PartitionId = entity.PartitionId,
                    FlowId = entity.FlowId,
                    DeliveryKey = entity.DeliveryKey,
                    SubmissionId = entity.LastSubmissionId,
                    Worker = Truncate(actor, 200)!,
                    StartedUtc = nowUtc,
                    CompletedUtc = nowUtc,
                    Outcome = StatusText.Of(AttemptOutcome.Skipped),
                    Phase = AttemptPhases.Adopt,
                    TargetVersion = version,
                    ResultJson = AttemptResult.Adopted(targetId, version, note),
                });

                // The record claims the id as a record that queued a document there would, and takes OSDU's record as the
                // version the ledger knows: the write that follows carries forward the data keys OSDU keeps of a record it
                // holds (preserveDataKeys), as an update does. Nothing it was rendered from is known, so the next plan sends it whole.
                entity.ClaimedTargetId = targetId;
                entity.TargetId = targetId;
                entity.TargetVersion = version;
                entity.OccupiedTargetId = null;
                entity.OccupiedTargetVersion = null;
                entity.Blocked = false;
                entity.ProblemHash = null;
                entity.PlanRequestedUtc = nowUtc;
                entity.LastError = Truncate(note, 2000);
                entity.UpdatedUtc = nowUtc;
                if (activityId is { } activity)
                {
                    db.DeliveryActivityRecords.Add(new DeliveryActivityRecord
                    {
                        PartitionId = entity.PartitionId, FlowId = entity.FlowId, DeliveryKey = entity.DeliveryKey, ActivityId = activity,
                    });
                }

                done.Add(new RecordAdoption(new DeliveryKey(entity.DeliveryKey), targetId, version));
            }

            return (IReadOnlyList<RecordAdoption>)done;
        }, ct).ConfigureAwait(false);
    }
}
