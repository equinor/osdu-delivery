using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Ledger;

// The interventions an operator makes on a flow's records, each named under its activity: a release of blocked records
// (osdu/docs/reference/concepts/record-lifecycle.md, Releasing a record), a redelivery of what OSDU holds or of parts
// of it (osdu/docs/reference/concepts/removal-and-reversal.md, Redelivering records), bringing records up to date, and
// giving records a new OSDU id in place of one OSDU no longer holds (osdu/docs/reference/flow/inventory-removals.md,
// Repairing missing ids).
public sealed partial class OsduLedger
{
    public Task<int> ReleaseAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, DateTime nowUtc, CancellationToken ct = default)
        => ReleaseAsync(flowId, keys is null ? ReleaseSelection.EveryBlocked : ReleaseSelection.Named(keys), null, nowUtc, ct);

    public async Task<int> ReleaseAsync(Guid flowId, ReleaseSelection selection, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return 0;
        }

        await using var db = Open();

        // Named records are released as named, a slice to a statement; a waiting one among them is sent without waiting.
        if (selection.Keys is { } named)
        {
            var released = 0;
            foreach (var slice in named.Select(k => k.Value).Chunk(WriteSlice))
            {
                ct.ThrowIfCancellationRequested();
                await StripWaitedReferencesAsync(partition, flowId, slice, ct).ConfigureAwait(false);
                released += await RetryDeadlockAsync(
                    () => SqlServerLedgerBulk.ReleaseSliceAsync(db, partition, flowId, slice, waiting: true, activityId, ReleasedNote, nowUtc, ct), ct).ConfigureAwait(false);
            }

            return released;
        }

        // A problem's records are walked through the problem index; every blocked record through the status index, a
        // custody state at a time. A release of the whole flow is for what is blocked, and leaves the waits to end on
        // their own.
        if (selection.Problem is { } problem)
        {
            return await ReleaseWalkAsync(db, partition, flowId, problem, null, activityId, nowUtc, ct).ConfigureAwait(false);
        }

        var total = 0;
        foreach (var status in BlockedStatuses)
        {
            total += await ReleaseWalkAsync(db, partition, flowId, null, status, activityId, nowUtc, ct).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>What the ledger says of a record a release unblocked without a rendered document to send.</summary>
    private const string ReleasedNote = "released; the flow's next run plans it again from its ingestion rows";

    /// <summary>The custody states a blocked record is in: what a release of every blocked record walks.</summary>
    private static readonly string[] BlockedStatuses =
        [StatusText.Of(RecordStatus.Held), StatusText.Of(RecordStatus.Failed), StatusText.Of(RecordStatus.Deleted), StatusText.Of(RecordStatus.Reverted)];

    /// <summary>
    /// Releases the records one problem keeps blocked, or the blocked records in one custody state, a page of keys at a time
    /// in the order an index holds them: each page is read, then released in a statement of its own, so neither the read
    /// nor the write ever holds more than a slice of records however many the walk reaches. A record released leaves what
    /// is walked (the problem index, or the blocked records) and the walk only moves forward, so it ends.
    /// </summary>
    private async Task<int> ReleaseWalkAsync(OsduDbContext db, short partition, Guid flowId, long? problem, string? status, long? activityId, DateTime nowUtc, CancellationToken ct)
    {
        var released = 0;
        var after = SqlServerLedgerBulk.WalkPosition.Start;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var position = after;
            var page = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.ReleasePageAsync(db, partition, flowId, problem, status, position, WriteSlice, ct), ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return released;
            }

            var keys = page.Select(p => p.DeliveryKey).ToList();
            released += await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.ReleaseSliceAsync(db, partition, flowId, keys, waiting: false, activityId, ReleasedNote, nowUtc, ct), ct).ConfigureAwait(false);
            if (page.Count < WriteSlice)
            {
                return released;
            }

            after = page[^1];
        }
    }

    public Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverScope scope, DateTime nowUtc, CancellationToken ct = default)
        => ForceRedeliverAsync(flowId, keys, new RedeliverSelection(scope, []), nowUtc, ct);

    public Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverSelection selection, DateTime nowUtc, CancellationToken ct = default)
        => ForceRedeliverAsync(flowId, keys, selection, null, nowUtc, ct);

    public async Task<int> ForceRedeliverAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, RedeliverSelection selection, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return 0;
        }

        await using var db = Open();
        var note = selection.Lost
            ? $"redelivery of {selection} requested: OSDU no longer holds the record, so the next deliver run brings it back under its id (a soft-deleted one restored, a purged one written again)"
            : $"redelivery of {selection} requested";

        // Parts of a payload sent in parts are named on its delivered hash; a selection of every part clears it, as a
        // whole payload's redelivery always has.
        var marker = selection is { Scope: RedeliverScope.Payload, Parts.Count: > 0 } ? PayloadParts.RedeliverMarker(selection.Parts) : null;
        if (keys is not null)
        {
            // Records OSDU lost are brought back only where the ledger holds them delivered: one it holds otherwise (held,
            // reverted, removed) is acted on through its own state.
            return await RedeliverKeysAsync(
                db, partition, flowId, keys.Select(k => k.Value), selection.Scope, marker, deliveredOnly: selection.Lost, activityId, note, nowUtc, ct, selection.Lost).ConfigureAwait(false);
        }

        // Every record OSDU holds of the flow, walked through the status index a page at a time as a release of the whole
        // flow is, so neither the read nor the write ever holds more than a slice however many records the flow delivered.
        var marked = 0;
        var after = SqlServerLedgerBulk.WalkPosition.Start;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var position = after;
            var page = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.DeliveredPageAsync(db, partition, flowId, position, nowUtc, WriteSlice, ct), ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return marked;
            }

            marked += await RedeliverKeysAsync(
                db, partition, flowId, page.Select(p => p.DeliveryKey), selection.Scope, marker, deliveredOnly: true, activityId, note, nowUtc, ct, selection.Lost).ConfigureAwait(false);
            if (page.Count < WriteSlice)
            {
                return marked;
            }

            after = page[^1];
        }
    }

    public async Task<int> RequestRenderAsync(Guid flowId, IEnumerable<DeliveryKey>? keys, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return 0;
        }

        await using var db = Open();
        if (keys is not null)
        {
            var marked = 0;
            foreach (var slice in keys.Select(k => k.Value).Distinct().Chunk(WriteSlice))
            {
                ct.ThrowIfCancellationRequested();
                marked += await RetryDeadlockAsync(
                    () => SqlServerLedgerBulk.RenderSliceAsync(db, partition, flowId, slice, activityId, nowUtc, ct), ct).ConfigureAwait(false);
            }

            return marked;
        }

        // Every record OSDU holds of the flow, walked through the status index a page at a time as a redelivery of the whole
        // flow is: a record marked carries the walk's moment as its update time and its request, so the walk passes it over.
        var total = 0;
        var after = SqlServerLedgerBulk.WalkPosition.Start;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var position = after;
            var page = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.DeliveredPageAsync(db, partition, flowId, position, nowUtc, WriteSlice, ct), ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return total;
            }

            var keysOfPage = page.Select(p => p.DeliveryKey).ToList();
            total += await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.RenderSliceAsync(db, partition, flowId, keysOfPage, activityId, nowUtc, ct), ct).ConfigureAwait(false);
            if (page.Count < WriteSlice)
            {
                return total;
            }

            after = page[^1];
        }
    }

    public async Task<IReadOnlyList<TargetIdReassignment>> ReassignTargetIdsAsync(
        Guid flowId, IReadOnlyList<DeliveryKey> keys, string actor, string reason, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (keys.Count == 0)
        {
            return [];
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        var reassigned = new List<TargetIdReassignment>();
        foreach (var chunk in keys.Select(k => k.Value).Distinct().Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            reassigned.AddRange(await ReassignChunkAsync(partition, flowId, chunk, actor, reason, activityId, nowUtc, ct).ConfigureAwait(false));
        }

        return reassigned;
    }

    /// <summary>
    /// One chunk of <see cref="ReassignTargetIdsAsync"/> in one transaction: the records, their attempts, the activity's links
    /// to them and their search entries are written together, so a record is never left claiming an id nothing records.
    /// </summary>
    private async Task<IReadOnlyList<TargetIdReassignment>> ReassignChunkAsync(
        short partition, Guid flowId, IReadOnlyList<Guid> ids, string actor, string reason, long? activityId, DateTime nowUtc, CancellationToken ct)
    {
        var delivered = StatusText.Of(RecordStatus.Delivered);
        await using var db = Open();
        // A retried run of this block starts from the database, not from what a rolled back one left tracked, and the
        // connection leaves it at read committed, so the next read on it does not hold repeatable read locks.
        return await PooledIsolation.InTransactionAsync(db, System.Data.IsolationLevel.RepeatableRead, async () =>
        {
            var entities = await db.DeliveryRecords
                .Where(r => r.PartitionId == partition && r.FlowId == flowId && ids.Contains(r.DeliveryKey)
                    && r.Status == delivered && r.ClaimedTargetId != null && r.LeaseOwner == null)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            var wanted = entities
                .Select(e => (Entity: e, Lost: e.ClaimedTargetId!, Assigned: TargetId.Reassigned(e.ClaimedTargetId!, new DeliveryKey(e.DeliveryKey))))
                .Where(w => w.Assigned is not null)
                .ToList();
            var candidates = wanted.Select(w => w.Assigned!).ToList();

            // A claim is the ledger's across every flow and partition: an id another record claims is never taken.
            var taken = (await db.DeliveryRecords.AsNoTracking()
                    .Where(r => r.ClaimedTargetId != null && candidates.Contains(r.ClaimedTargetId))
                    .Select(r => r.ClaimedTargetId!)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);

            var done = new List<TargetIdReassignment>();
            var states = new List<RecordState>();
            foreach (var (entity, lost, assigned) in wanted)
            {
                if (taken.Contains(assigned!))
                {
                    continue;
                }

                var note = $"given the new OSDU id {assigned} by {actor} in place of {lost}, which OSDU no longer holds ({reason}); the next deliver run writes it under the new id, and records that name {lost} still name the old one";
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    PartitionId = entity.PartitionId,
                    FlowId = entity.FlowId,
                    DeliveryKey = entity.DeliveryKey,
                    SubmissionId = entity.LastSubmissionId,
                    Worker = actor,
                    StartedUtc = nowUtc,
                    CompletedUtc = nowUtc,
                    Outcome = StatusText.Of(AttemptOutcome.Skipped),
                    Phase = AttemptPhases.ReassignId,
                    TargetVersion = entity.TargetVersion,
                    ResultJson = AttemptResult.Reassigned(lost, assigned!, entity.TargetStateJson, note),
                });

                // The record is written under the new id from now on, as a record OSDU never held: nothing it held under the
                // lost id is the new record's, and the next plan that meets it renders and sends it whole.
                entity.ClaimedTargetId = assigned;
                entity.TargetId = assigned;
                entity.TargetIdAssignedUtc = nowUtc;
                entity.TargetVersion = null;
                entity.TargetStateJson = null;
                entity.MetadataHash = null;
                entity.PayloadHash = null;
                entity.PayloadModifiedUtc = null;
                entity.SourceFingerprint = null;
                entity.PendingSourceFingerprint = null;
                entity.LastVerifiedUtc = nowUtc;
                entity.LastVerifyOutcome = null;
                entity.PlanRequestedUtc = nowUtc;
                entity.LastError = note.Length <= 2000 ? note : note[..2000];
                entity.UpdatedUtc = nowUtc;
                if (activityId is { } activity)
                {
                    db.DeliveryActivityRecords.Add(new DeliveryActivityRecord
                    {
                        PartitionId = entity.PartitionId, FlowId = entity.FlowId, DeliveryKey = entity.DeliveryKey, ActivityId = activity,
                    });
                }

                done.Add(new TargetIdReassignment(new DeliveryKey(entity.DeliveryKey), lost, assigned!));
                states.Add(ToState(entity));
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await WriteIdentitiesAsync(db, partition, flowId, states, ct).ConfigureAwait(false);
            return (IReadOnlyList<TargetIdReassignment>)done;
        }, ct).ConfigureAwait(false);
    }
}
