using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;
using RenderRestriction = SqlFlow.Delivery.Snapshots.RenderRestriction;

namespace SqlFlow.Delivery.Engine.Reversals;

/// <summary>
/// One record to write back as OSDU held it at an earlier version: the version to put back, the latest version OSDU holds
/// (whose keys another system owns are carried over), and the target state the ledger keeps for the record.
/// </summary>
/// <param name="Key">The record's delivery key.</param>
/// <param name="TargetId">Its OSDU id.</param>
/// <param name="Version">The version to put back.</param>
/// <param name="Latest">The latest version OSDU holds.</param>
/// <param name="TargetState">The target state the ledger keeps for the record.</param>
/// <param name="Acl">
/// The ACL the version is written back with in place of the one it is read with: the groups of the access restriction a record
/// OSDU holds restricted until complete is under (osdu/docs/reference/flow/mapping-restricted-access.md), so a write-back never
/// gives such a record an access a delivery would not. Null writes the version back as it is read.
/// </param>
/// <param name="Refusal">Why the record is not written back at all, said as its failure; null to write it.</param>
public sealed record WriteBack(
    DeliveryKey Key, string TargetId, long Version, long? Latest, IReadOnlyDictionary<string, string>? TargetState, JsonObject? Acl = null, string? Refusal = null);

/// <summary>How writing one record back ended.</summary>
public enum WriteBackOutcome
{
    /// <summary>The version was read and written back; <see cref="WriteBackResult.NewVersion"/> is the version OSDU gave the write.</summary>
    Restored,

    /// <summary>OSDU no longer holds the version (the record's earlier versions were purged), so nothing was written.</summary>
    VersionMissing,

    /// <summary>The version could not be read, so nothing was written.</summary>
    ReadFailed,

    /// <summary>The version was read, and its write was refused or failed.</summary>
    WriteFailed,
}

/// <summary>
/// What writing one record back came to: the version OSDU gave the write and what the write returned, or why it did not
/// land, as the service or the route said it (callers redact it before it is stored or shown).
/// </summary>
public sealed record WriteBackResult(WriteBack Request, WriteBackOutcome Outcome, long? NewVersion, IReadOnlyDictionary<string, string>? Returned, string? Failure);

/// <summary>
/// Writing records back as OSDU held them at an earlier version, and the reads that decide it: the one path a reversal
/// (<see cref="ReversalRunner"/>) and a step back to the version before the latest (<see cref="PreviousVersionRestore"/>) both
/// take, so the two ask OSDU the same way, write the same content and leave the same target state in the ledger.
/// </summary>
public static class VersionWriteBack
{
    /// <summary>
    /// What OSDU holds of each record, read in the route's batches, as many batches at once as <paramref name="concurrency"/>
    /// allows. Results align with <paramref name="requests"/>; a batch that could not be read answers each of its records
    /// with the redacted error.
    /// </summary>
    public static async Task<VerifyResult[]> VerifyAsync(IDeliveryProtocol protocol, IReadOnlyList<VerifyRequest> requests, int concurrency, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(requests);
        var results = new VerifyResult[requests.Count];
        var chunks = requests.Select((r, i) => (Request: r, Index: i)).Chunk(Math.Max(1, protocol.MaxVerifyBatch)).ToList();
        await Parallel.ForEachAsync(chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct }, async (chunk, token) =>
        {
            try
            {
                var answered = await protocol.VerifyBatchAsync(chunk.Select(c => c.Request).ToList(), token).ConfigureAwait(false);
                for (var j = 0; j < chunk.Length; j++)
                {
                    results[chunk[j].Index] = answered[j];
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                var detail = HeaderRedaction.RedactMessage(ex.Message);
                foreach (var (_, index) in chunk)
                {
                    results[index] = new VerifyResult(VerifyOutcome.Error, null, detail);
                }
            }
        }).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Reads the version each record is to get back, as many at once as <paramref name="concurrency"/> allows, and writes
    /// the versions read back in the route's batches. Results align with <paramref name="work"/>: a record is written only
    /// once its version was read, and a write the route refused for the whole batch is reported against each record.
    /// </summary>
    public static async Task<IReadOnlyList<WriteBackResult>> RunAsync(IDeliveryProtocol protocol, IReadOnlyList<WriteBack> work, int concurrency, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(work);
        if (work.Count == 0)
        {
            return [];
        }

        var results = new WriteBackResult?[work.Count];
        var stored = new JsonObject?[work.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, work.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct }, async (index, token) =>
        {
            var w = work[index];
            if (w.Refusal is { } refusal)
            {
                results[index] = new WriteBackResult(w, WriteBackOutcome.WriteFailed, null, null, refusal);
                return;
            }

            try
            {
                var version = await protocol.ReadVersionAsync(w.TargetId, w.Version, token).ConfigureAwait(false);
                if (version is null)
                {
                    results[index] = new WriteBackResult(w, WriteBackOutcome.VersionMissing, null, null, null);
                }
                else
                {
                    // A record restricted until complete keeps the restriction's groups whatever ACL the version is read with.
                    if (w.Acl is { } acl)
                    {
                        version["acl"] = acl.DeepClone();
                    }

                    stored[index] = version;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                results[index] = new WriteBackResult(w, WriteBackOutcome.ReadFailed, null, null, ex.Message);
            }
        }).ConfigureAwait(false);

        var ready = Enumerable.Range(0, work.Count).Where(i => stored[i] is not null).ToList();
        if (ready.Count > 0)
        {
            var restores = ready
                .Select(i => new VersionRestore(work[i].Key, work[i].TargetId, work[i].Version, stored[i]!, work[i].Latest, work[i].TargetState))
                .ToList();
            IReadOnlyList<RestoreResult> written;
            try
            {
                written = await protocol.RestoreBatchAsync(restores, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                written = restores.Select(r => new RestoreResult(r, null, null, ex)).ToList();
            }

            if (written.Count != restores.Count)
            {
                throw new DeliveryException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The route answered {written.Count} result(s) for {restores.Count} record(s) written back, so which records it wrote cannot be told."));
            }

            for (var j = 0; j < ready.Count; j++)
            {
                var result = written[j];
                results[ready[j]] = result.Succeeded
                    ? new WriteBackResult(work[ready[j]], WriteBackOutcome.Restored, result.NewVersion, result.Returned, null)
                    : new WriteBackResult(work[ready[j]], WriteBackOutcome.WriteFailed, null, null, result.Failure?.Message ?? "the route answered no version for the write");
            }
        }

        return results.Select(r => r!).ToList();
    }

    /// <summary>
    /// <paramref name="writeBack"/> made safe for <paramref name="record"/>'s access (osdu/docs/reference/flow/mapping-restricted-access.md):
    /// a record OSDU holds restricted until complete is written back with the groups of the restriction it is under, found in
    /// <paramref name="restrictions"/>, so stepping it back or reversing a run never opens it; a record with its own access is
    /// written back as its version is read. A record whose restriction is not among them (no longer registered) is refused,
    /// with why, rather than written back with an ACL no one decided.
    /// </summary>
    public static WriteBack Guarded(WriteBack writeBack, RecordState record, IReadOnlyDictionary<int, AccessRestrictionState> restrictions)
    {
        ArgumentNullException.ThrowIfNull(writeBack);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(restrictions);
        if (record.IncompleteRestrictionId is not { } id)
        {
            return writeBack;
        }

        return restrictions.TryGetValue(id, out var restriction)
            ? writeBack with { Acl = new RenderRestriction(restriction.RestrictionId, restriction.Name, restriction.Acl.Viewers, restriction.Acl.Owners).Acl() }
            : writeBack with
            {
                Refusal = string.Create(
                    CultureInfo.InvariantCulture,
                    $"OSDU holds the record restricted until complete under access restriction {id}, which is no longer registered, so it is not written back: the version would go out with an access no one decided"),
            };
    }

    /// <summary>The access restrictions <paramref name="records"/> are restricted until complete under, by id, read once.</summary>
    public static async Task<IReadOnlyDictionary<int, AccessRestrictionState>> RestrictionsOfAsync(ILedger ledger, IEnumerable<RecordState> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(records);
        var ids = records.Where(r => r.IncompleteRestrictionId is not null).Select(r => r.IncompleteRestrictionId!.Value).Distinct().ToList();
        return ids.Count == 0 ? new Dictionary<int, AccessRestrictionState>() : await ledger.AccessRestrictionsAsync(ids, ct).ConfigureAwait(false);
    }

    /// <summary>The target state a record written back holds: what it held, with what the write returned, at the new version.</summary>
    public static string? TargetStateAfter(string? targetStateJson, string targetId, long version, IReadOnlyDictionary<string, string>? returned)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["recordId"] = targetId,
            ["version"] = version.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (name, value) in returned ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            values[name] = value;
        }

        return JsonMerge.Merge(targetStateJson, JsonMerge.FromValues(values));
    }
}
