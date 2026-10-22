using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// Records that read their parent (osdu/docs/reference/flow/mapping-parent.md, When the parent changes). A record of a
// linked template keeps the OSDU id of the parent its render read sections of (ParentId). When a record of any flow of the
// partition lands under that id, the records that read it are planned again: one held at its render because the parent was
// not in OSDU is released, under an intervention the trail shows, and one rendered from an older version of the parent is
// asked to be planned again, and sent only when it renders differently. A record a check held with its document kept is
// held for something else, and a release would send that document as it is, so it is left to its own hold.
public sealed partial class OsduLedger
{
    /// <summary>How many records one landing reaches; the rest are reached by their parent's next landing, or by an operator.</summary>
    internal const int MaxChildrenPlanned = 10_000;

    /// <summary>What the trail names an intervention releasing the records whose parent landed.</summary>
    internal const string ParentActor = "parent";

    public async Task<IReadOnlyList<RecordState>> ListChildrenOfAsync(Guid flowId, string parentId, int max, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);
        if (await PartitionOfAsync(flowId, ct).ConfigureAwait(false) is not { } partition)
        {
            return [];
        }

        var take = Math.Clamp(max, 1, 1000);
        return await ReadAsync(
            // The column compares exactly (a binary collation), as an OSDU id is compared.
            async db => ToStates(
                await ReadLeasedAsync(
                    db,
                    db.DeliveryRecords
                        .Where(r => r.PartitionId == partition && r.ParentId != null && r.ParentId == parentId)
                        .OrderByDescending(r => r.UpdatedUtc)
                        .Take(take),
                    ct).ConfigureAwait(false)),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Plans again the records of partition <paramref name="partition"/> that read one of the parents in
    /// <paramref name="landed"/>, the OSDU ids of records that just landed there, found through the partition's index of what
    /// records read. Returns how many it reached.
    /// </summary>
    private async Task<int> PlanChildrenOfAsync(short partition, IReadOnlyCollection<string> landed, DateTime nowUtc, CancellationToken ct)
    {
        if (landed.Count == 0)
        {
            return 0;
        }

        var deleted = StatusText.Of(RecordStatus.Deleted);
        var reverted = StatusText.Of(RecordStatus.Reverted);
        var children = new List<(Guid FlowId, Guid DeliveryKey, bool Blocked, bool HasDocument, string ParentId)>();
        await using (var db = Open())
        {
            foreach (var chunk in landed.Distinct(StringComparer.Ordinal).Chunk(LookupChunk))
            {
                var ids = chunk.ToList();
                var left = MaxChildrenPlanned - children.Count;
                var found = await RetryDeadlockAsync(
                    () => db.DeliveryRecords.AsNoTracking()
                        .Where(r => r.PartitionId == partition && r.ParentId != null && ids.Contains(r.ParentId) && r.Status != deleted && r.Status != reverted)
                        .Select(r => new { r.FlowId, r.DeliveryKey, r.Blocked, HasDocument = r.PendingDocumentRef != null, ParentId = r.ParentId! })
                        .Take(left)
                        .ToListAsync(ct),
                    ct).ConfigureAwait(false);
                children.AddRange(found.Select(r => (r.FlowId, r.DeliveryKey, r.Blocked, r.HasDocument, r.ParentId)));
                if (children.Count >= MaxChildrenPlanned)
                {
                    break;
                }
            }
        }

        if (children.Count == 0)
        {
            return 0;
        }

        // A record not blocked (queued, waiting, being delivered or delivered) renders again from the parent's new version on
        // its flow's next run, and is sent only if its document changed: the request is recorded once, and a request already
        // there stays as it is.
        var following = children.Where(c => !c.Blocked).Select(c => new RecordKey(partition, c.FlowId, c.DeliveryKey)).ToList();
        var reached = 0;
        if (following.Count > 0)
        {
            await using var db = Open();
            reached += await WriteByKeyAsync(
                db.DeliveryRecords.Where(r => !r.Blocked && r.PlanRequestedUtc == null),
                following,
                rows => rows.ExecuteUpdateAsync(s => s.SetProperty(r => r.PlanRequestedUtc, nowUtc).SetProperty(r => r.UpdatedUtc, nowUtc), ct),
                ct).ConfigureAwait(false);
        }

        // A record held at its render, which keeps no document, was held for what its render could not read, its parent
        // among it: it is released, and the release names the parents that landed.
        foreach (var flow in children.Where(c => c.Blocked && !c.HasDocument).GroupBy(c => c.FlowId))
        {
            var flowName = await ReadAsync(
                db => db.DeliveryLedgers.AsNoTracking().Where(l => l.PartitionId == partition && l.FlowId == flow.Key).Select(l => l.FlowName).FirstOrDefaultAsync(ct),
                ct).ConfigureAwait(false);
            var parents = flow.Select(c => c.ParentId).Distinct(StringComparer.Ordinal).ToList();
            var activity = await StartActivityAsync(new ActivityRecord
            {
                FlowId = flow.Key,
                FlowName = flowName ?? flow.Key.ToString("D"),
                Kind = "release",
                Actor = ParentActor,
                StartedUtc = nowUtc,
                ParametersJson = JsonSerializer.Serialize(new { reason = "the parent records they read landed in OSDU", parents = parents.Take(20), parentCount = parents.Count }),
            }, ct).ConfigureAwait(false);
            var released = await ReleaseAsync(flow.Key, ReleaseSelection.Named(flow.Select(c => new DeliveryKey(c.DeliveryKey))), activity.ActivityId, nowUtc, ct).ConfigureAwait(false);
            await CompleteActivityAsync(
                activity.ActivityId,
                "completed",
                $"{released} held record(s) released: the parent record{(parents.Count == 1 ? string.Empty : "s")} they read ({string.Join(", ", parents.Take(5))}{(parents.Count > 5 ? $" and {parents.Count - 5} more" : string.Empty)}) landed in OSDU.",
                null,
                nowUtc,
                ct: ct).ConfigureAwait(false);
            reached += released;
        }

        return reached;
    }
}
