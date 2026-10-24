using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;

namespace SqlFlow.Delivery.Engine.Inventories;

/// <summary>One copy a pick read whole from storage: its OSDU id and the record storage returned, as JSON text.</summary>
public sealed record DuplicateCopyRecord(string TargetId, string Json);

/// <summary>How many records name a copy, or why the search would not say.</summary>
public sealed record DuplicateReferenceCount(long? Count, string? Problem);

/// <summary>
/// How many records name each copy a pick read (osdu/docs/reference/flow/inventory-duplicates.md, Which copy is kept), asked of
/// OSDU's search over every kind a batch of copies at a time, not a copy at a time: one query names every copy of the batch,
/// and since most copies are named by no record, it answers for the whole batch in one request. A batch whose query finds a
/// record, or that the service refuses (too many clauses for its index), is split in two and each half asked again, until
/// each copy a record names is counted on its own. Every query leaves out the records of the batch itself, so a copy named by
/// another copy of the batch is counted from the copies' own records, which the pick read already. The searches are sent one
/// after another, never side by side: a search over every kind is the one OSDU limits hardest.
/// </summary>
public sealed class InventoryDuplicateReferences
{
    /// <summary>The kind the search reads: every kind, since a record of any type may name a copy.</summary>
    public const string EveryKind = "*:*:*:*";

    private readonly OsduSearch _search;
    private readonly int _batch;

    public InventoryDuplicateReferences(OsduSearch search, int batch)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        _search = search;
        _batch = batch;
    }

    /// <summary>The searches sent so far.</summary>
    public long Searches { get; private set; }

    /// <summary>How many records name each of <paramref name="copies"/>, by the copy's OSDU id.</summary>
    public async Task<IReadOnlyDictionary<string, DuplicateReferenceCount>> CountAsync(IReadOnlyList<DuplicateCopyRecord> copies, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(copies);
        var counted = new Dictionary<string, DuplicateReferenceCount>(StringComparer.Ordinal);
        foreach (var batch in copies.DistinctBy(c => c.TargetId, StringComparer.Ordinal).Chunk(_batch))
        {
            var named = batch.Select(c => (c.TargetId, Named: TargetId.WithoutVersion(c.TargetId))).ToList();
            var excluded = named.Select(n => n.Named).ToList();
            var among = named.ToDictionary(
                n => n.TargetId,
                n => (long)batch.Count(other => !string.Equals(other.TargetId, n.TargetId, StringComparison.Ordinal) && Names(other.Json, n.Named)),
                StringComparer.Ordinal);
            await SplitAsync(named, excluded, among, counted, ct).ConfigureAwait(false);
        }

        return counted;
    }

    /// <summary>
    /// Whether a record's JSON names <paramref name="named"/> as a reference does: as a whole string value, or one followed by a
    /// version or the trailing colon (<c>"…:abc"</c>, <c>"…:abc:"</c>, <c>"…:abc:1700"</c>), never as the start of a longer id.
    /// </summary>
    public static bool Names(string json, string named)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(named);
        var quoted = "\"" + named;
        for (var at = json.IndexOf(quoted, StringComparison.Ordinal); at >= 0; at = json.IndexOf(quoted, at + 1, StringComparison.Ordinal))
        {
            var next = at + quoted.Length;
            if (next < json.Length && json[next] is '"' or ':')
            {
                return true;
            }
        }

        return false;
    }

    private async Task SplitAsync(
        IReadOnlyList<(string TargetId, string Named)> asked, IReadOnlyList<string> excluded, IReadOnlyDictionary<string, long> among,
        Dictionary<string, DuplicateReferenceCount> counted, CancellationToken ct)
    {
        var query = RecordExplorer.MentionsQuery(asked.Select(a => a.Named).ToList(), excluded);
        var answer = await _search.PageAsync(new OsduSearchQuery { Kind = EveryKind, Query = query, ReturnedFields = ["id"] }, 0, 1, null, ct).ConfigureAwait(false);
        Searches++;
        if (answer.Refusal is { } refusal)
        {
            if (asked.Count > 1)
            {
                await SplitHalvesAsync(asked, excluded, among, counted, ct).ConfigureAwait(false);
                return;
            }

            counted[asked[0].TargetId] = new DuplicateReferenceCount(null, HeaderRedaction.RedactMessage(refusal));
            return;
        }

        if (answer.Total == 0)
        {
            foreach (var (id, _) in asked)
            {
                counted[id] = new DuplicateReferenceCount(among[id], null);
            }

            return;
        }

        if (asked.Count == 1)
        {
            counted[asked[0].TargetId] = new DuplicateReferenceCount(answer.Total + among[asked[0].TargetId], null);
            return;
        }

        await SplitHalvesAsync(asked, excluded, among, counted, ct).ConfigureAwait(false);
    }

    private async Task SplitHalvesAsync(
        IReadOnlyList<(string TargetId, string Named)> asked, IReadOnlyList<string> excluded, IReadOnlyDictionary<string, long> among,
        Dictionary<string, DuplicateReferenceCount> counted, CancellationToken ct)
    {
        var half = asked.Count / 2;
        await SplitAsync(asked.Take(half).ToList(), excluded, among, counted, ct).ConfigureAwait(false);
        await SplitAsync(asked.Skip(half).ToList(), excluded, among, counted, ct).ConfigureAwait(false);
    }
}
