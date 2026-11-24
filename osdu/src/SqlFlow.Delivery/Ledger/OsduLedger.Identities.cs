using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Ledger;

// The identity tokens an operator finds a record by (osdu/docs/reference/concepts/ledger.md, Finding records): written
// with each record a staging writes, and for the records of a ledger that predates the index, a bounded pass at a time.
public sealed partial class OsduLedger
{
    /// <summary>
    /// Rewrites the identity tokens of the records given: what an operator can find each by, from the values the record
    /// was staged with (the mapping's declared identities, its key values, its label, its OSDU id, its ingestion file).
    /// A record's tokens are replaced as a set, so a staging that renames or re-keys a record leaves nothing stale
    /// behind, and staging the same record again writes the same rows.
    /// </summary>
    private static async Task WriteIdentitiesAsync(OsduDbContext db, short partition, Guid flowId, IReadOnlyList<RecordState> records, CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return;
        }

        foreach (var chunk in records.Chunk(IdentityChunk))
        {
            ct.ThrowIfCancellationRequested();
            var keys = chunk.Select(r => r.DeliveryKey.Value).ToArray();
            var wanted = new List<DeliveryRecordIdentity>(chunk.Length * 4);
            foreach (var record in chunk)
            {
                foreach (var token in TokensOf(record))
                {
                    wanted.Add(new DeliveryRecordIdentity
                    {
                        PartitionId = partition,
                        FlowId = flowId,
                        DeliveryKey = record.DeliveryKey.Value,
                        Token = token.Token,
                        Display = token.Display,
                        Kind = RecordIdentities.Text(token.Kind),
                    });
                }
            }

            var held = await db.DeliveryRecordIdentities
                .Where(i => i.PartitionId == partition && i.FlowId == flowId && keys.Contains(i.DeliveryKey))
                .ToListAsync(ct).ConfigureAwait(false);
            var by = (DeliveryRecordIdentity row) => (row.DeliveryKey, row.Token);
            var keep = wanted.ToDictionary(by);
            foreach (var row in held)
            {
                if (keep.Remove(by(row), out var still))
                {
                    // The same token, whose display or kind may have moved (a label word that is now a declared identity).
                    row.Display = still.Display;
                    row.Kind = still.Kind;
                }
                else
                {
                    db.DeliveryRecordIdentities.Remove(row);
                }
            }

            db.DeliveryRecordIdentities.AddRange(keep.Values);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the identity tokens of records the index does not hold yet: the records of a ledger that predates the
    /// index, or that a failed pass left without rows. One bounded pass over the records after
    /// <paramref name="after"/> in the record table's key order (partition, ledger identity, delivery key), so a caller
    /// walks the whole ledger a page at a time and can stop and resume, and a delivery key held by several ledgers is
    /// passed once for each; the answer is the last record it read, or null when the ledger holds no more records.
    ///
    /// The tokens are derived from the record row, so a record whose mapping declares identities gains those on its
    /// next staging: the row alone cannot say what the mapping declared, and nothing is invented here.
    /// </summary>
    public async Task<(RecordCursor? Last, int Records, int Tokens)> BackfillIdentitiesAsync(RecordCursor? after, int max, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        await using var db = Open();
        var rows = db.DeliveryRecords.AsNoTracking();
        if (after is { } cursor)
        {
            var (p, f, k) = (cursor.PartitionId, cursor.FlowId, cursor.DeliveryKey);
            rows = rows.Where(r => r.PartitionId > p
                || (r.PartitionId == p && (r.FlowId.CompareTo(f) > 0 || (r.FlowId == f && r.DeliveryKey.CompareTo(k) > 0))));
        }

        var records = await rows
            .OrderBy(r => r.PartitionId)
            .ThenBy(r => r.FlowId)
            .ThenBy(r => r.DeliveryKey)
            .Take(Math.Clamp(max, 1, 1000))
            .Select(r => new
            {
                r.PartitionId,
                r.FlowId,
                r.DeliveryKey,
                r.SourceKey,
                r.SourceKeyJson,
                r.Label,
                r.TargetId,
                r.SourceFileName,
                r.PendingSourceFileName,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return (null, 0, 0);
        }

        var keys = records.Select(r => r.DeliveryKey).Distinct().ToArray();
        var partitions = records.Select(r => r.PartitionId).Distinct().ToArray();
        var indexed = (await db.DeliveryRecordIdentities.AsNoTracking()
            .Where(i => partitions.Contains(i.PartitionId) && keys.Contains(i.DeliveryKey))
            .Select(i => new { i.PartitionId, i.FlowId, i.DeliveryKey })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false))
            .Select(i => (i.PartitionId, i.FlowId, i.DeliveryKey))
            .ToHashSet();

        var written = 0;
        var tokens = 0;
        foreach (var flow in records.Where(r => !indexed.Contains((r.PartitionId, r.FlowId, r.DeliveryKey))).GroupBy(r => (r.PartitionId, r.FlowId)))
        {
            var states = flow.Select(r => new RecordState
            {
                DeliveryKey = new DeliveryKey(r.DeliveryKey),
                FlowId = r.FlowId,
                SourceKey = r.SourceKey,
                SourceKeyJson = r.SourceKeyJson,
                Label = r.Label,
                MappingName = string.Empty,
                TargetId = r.TargetId,
                SourceFileName = r.SourceFileName,
                PendingSourceFileName = r.PendingSourceFileName,
            }).ToList();
            await WriteIdentitiesAsync(db, flow.Key.PartitionId, flow.Key.FlowId, states, ct).ConfigureAwait(false);
            written += states.Count;
            tokens += states.Sum(s => TokensOf(s).Count);
        }

        var last = records[^1];
        return (new RecordCursor(last.PartitionId, last.FlowId, last.DeliveryKey), written, tokens);
    }

    /// <summary>What one record is findable by, as the identity index stores it.</summary>
    private static IReadOnlyList<RecordIdentityToken> TokensOf(RecordState record)
        => RecordIdentities.Of(
            record.Identities,
            record.SourceKey,
            RecordIdentities.KeyValues(record.SourceKeyJson),
            record.Label,
            record.TargetId,
            record.SourceFileName ?? record.PendingSourceFileName,
            record.AddedRecords);
}
