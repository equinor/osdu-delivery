using System.Globalization;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// Adopting the records OSDU already holds at the ids records' keys make (osdu/docs/reference/concepts/record-lifecycle.md,
// Adopting a record OSDU already holds). An adoption is a batch operation asked of a ledger's held records: the ledger marks
// every record held because OSDU held a record at its id, and the next run of the flow does the adopting. That run makes the
// id from each record's key under the mapping it renders with, asks OSDU, and claims the id at the version OSDU holds then,
// so nothing an earlier hold wrote, nor its wording, is read to decide what is claimed; a record whose id OSDU no longer
// holds is delivered under it as a new record.
public sealed partial class OsduLedger
{
    /// <summary>What a record asked to adopt says until the next run takes the record over.</summary>
    private const string AdoptionAskedNote =
        "adoption asked: the next run takes over the record OSDU holds at the OSDU id this record's key makes, at the version OSDU holds then, and writes this row there; if OSDU holds none there by then, the row is delivered as a new record";

    public async Task<IReadOnlyList<DeliveryKey>> RequestAdoptionAsync(Guid flowId, ReleaseSelection selection, long? activityId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentOutOfRangeException.ThrowIfLessThan(WriteSlice, 1);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        await using var db = Open();
        var asked = new List<DeliveryKey>();
        if (selection.Keys is { } named)
        {
            foreach (var slice in named.Select(k => k.Value).Distinct().Chunk(WriteSlice))
            {
                ct.ThrowIfCancellationRequested();
                asked.AddRange(await RetryDeadlockAsync(
                    () => SqlServerLedgerBulk.AdoptSliceAsync(db, partition, flowId, slice, activityId, AdoptionAskedNote, OccupiedHolds.Prefix, nowUtc, ct), ct).ConfigureAwait(false));
            }

            return asked;
        }

        // One problem's records through the problem index, or every held record through the status index, a page of keys at a
        // time as a release walks them: a record asked leaves the problem index, and one left behind stays where the walk has
        // passed, so the walk ends however many records the ledger holds.
        var after = SqlServerLedgerBulk.WalkPosition.Start;
        var status = selection.Problem is null ? StatusText.Of(RecordStatus.Held) : null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var position = after;
            var page = await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.ReleasePageAsync(db, partition, flowId, selection.Problem, status, position, WriteSlice, ct), ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                return asked;
            }

            var keys = page.Select(p => p.DeliveryKey).ToList();
            asked.AddRange(await RetryDeadlockAsync(
                () => SqlServerLedgerBulk.AdoptSliceAsync(db, partition, flowId, keys, activityId, AdoptionAskedNote, OccupiedHolds.Prefix, nowUtc, ct), ct).ConfigureAwait(false));
            if (page.Count < WriteSlice)
            {
                return asked;
            }

            after = page[^1];
        }
    }

    public async Task RecordAdoptionsAsync(Guid flowId, IReadOnlyList<RecordAdoption> adoptions, Guid? submissionId, Guid? runId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adoptions);
        if (adoptions.Count == 0)
        {
            return;
        }

        var partition = await WritePartitionAsync(flowId, ct).ConfigureAwait(false);
        await using var db = Open();
        foreach (var chunk in adoptions.Chunk(ChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var adoption in chunk)
            {
                var at = adoption.Version is { } v ? string.Create(CultureInfo.InvariantCulture, $" at version {v}") : string.Empty;
                var note = $"adopted, as an operator asked: the record claims {adoption.TargetId}, the OSDU id its key makes, where OSDU holds a record{at} "
                    + "that no record of this ledger had claimed (written by another system, or another instance of the delivery); this run writes the row there";
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    PartitionId = partition,
                    FlowId = flowId,
                    DeliveryKey = adoption.Key.Value,
                    SubmissionId = submissionId,
                    RunId = runId,
                    Worker = "intake",
                    StartedUtc = nowUtc,
                    CompletedUtc = nowUtc,
                    Outcome = StatusText.Of(AttemptOutcome.Skipped),
                    Phase = AttemptPhases.Adopt,
                    TargetVersion = adoption.Version,
                    ResultJson = AttemptResult.Adopted(adoption.TargetId, adoption.Version, note),
                });
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
