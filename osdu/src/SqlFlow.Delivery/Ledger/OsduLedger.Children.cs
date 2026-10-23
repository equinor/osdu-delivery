using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// Records that link their children (osdu/docs/reference/flow/mapping-child.md, When a child lands). A record whose render
// looked for its children keeps the unique segment their ids share with the id its key gives (ChildLinkKey). When a record of
// any flow of the partition lands under an id with that segment and another entity type, the records that link it are asked
// to be planned again, and each is sent only when it renders differently: a child that landed for the first time gives its
// parent the link, and one delivered again changes nothing. A child never holds its parent, so a blocked record is held for
// something else and is left to that hold; its next render looks for its children again.
public sealed partial class OsduLedger
{
    /// <summary>How many records linking a child one landing reaches; the rest are reached by the child's next landing, or by their own next render.</summary>
    internal const int MaxParentsPlanned = 10_000;

    /// <summary>
    /// Asks to plan again the records of partition <paramref name="partition"/> that link one of the records in
    /// <paramref name="landed"/> as their child: the OSDU ids of records that just landed there, matched by their unique segment
    /// through the partition's index of what records link, a record landing under its own id left out. Returns how many it asked.
    /// </summary>
    private async Task<int> PlanParentsOfAsync(short partition, IReadOnlyCollection<string> landed, DateTime nowUtc, CancellationToken ct)
    {
        if (landed.Count == 0)
        {
            return 0;
        }

        var byUnique = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in landed.Distinct(StringComparer.Ordinal))
        {
            if (TargetId.Unique(id) is not { } unique)
            {
                continue;
            }

            if (!byUnique.TryGetValue(unique, out var ids))
            {
                ids = [];
                byUnique[unique] = ids;
            }

            ids.Add(id);
        }

        if (byUnique.Count == 0)
        {
            return 0;
        }

        var deleted = StatusText.Of(RecordStatus.Deleted);
        var reverted = StatusText.Of(RecordStatus.Reverted);
        var parents = new List<RecordKey>();
        await using (var db = Open())
        {
            foreach (var chunk in byUnique.Keys.Chunk(LookupChunk))
            {
                var keys = chunk.ToList();
                var left = MaxParentsPlanned - parents.Count;
                var found = await RetryDeadlockAsync(
                    // The column compares exactly (a binary collation), as an OSDU id is compared.
                    () => db.DeliveryRecords.AsNoTracking()
                        .Where(r => r.PartitionId == partition && r.ChildLinkKey != null && keys.Contains(r.ChildLinkKey)
                            && !r.Blocked && r.PlanRequestedUtc == null && r.Status != deleted && r.Status != reverted)
                        .Select(r => new { r.FlowId, r.DeliveryKey, r.TargetId, ChildLinkKey = r.ChildLinkKey! })
                        .Take(left)
                        .ToListAsync(ct),
                    ct).ConfigureAwait(false);

                // A record landing under the record's own id (its own delivery, or another instance delivering the same record)
                // shares the segment without being its child.
                parents.AddRange(found
                    .Where(r => byUnique[r.ChildLinkKey].Any(id => !string.Equals(id, r.TargetId, StringComparison.Ordinal)))
                    .Select(r => new RecordKey(partition, r.FlowId, r.DeliveryKey)));
                if (parents.Count >= MaxParentsPlanned)
                {
                    break;
                }
            }
        }

        if (parents.Count == 0)
        {
            return 0;
        }

        // The request is recorded once, and a request already there stays as it is: the record renders again on its flow's
        // next run, finds the child, and is sent only if its document changed.
        await using var write = Open();
        return await WriteByKeyAsync(
            write.DeliveryRecords.Where(r => !r.Blocked && r.PlanRequestedUtc == null),
            parents,
            rows => rows.ExecuteUpdateAsync(s => s.SetProperty(r => r.PlanRequestedUtc, nowUtc).SetProperty(r => r.UpdatedUtc, nowUtc), ct),
            ct).ConfigureAwait(false);
    }
}
