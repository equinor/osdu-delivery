using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Validation;

/// <summary>Whether the record an id names was found, and where it was looked for.</summary>
public enum ReferenceState
{
    /// <summary>Nothing that was asked can say: the ledger does not hold it, the cache holds no record of its type, and OSDU was not asked.</summary>
    NotChecked,

    /// <summary>A record of the ledger holds the id: delivered, or still to land, which the claim waits for.</summary>
    InLedger,

    /// <summary>The cache version holds the record: reference data a cache flow captured from the partition.</summary>
    InCache,

    /// <summary>OSDU's storage service holds the record.</summary>
    InOsdu,

    /// <summary>Where it was looked for holds no such record: the cache, which holds records of its type, or OSDU's storage service.</summary>
    Missing,
}

/// <summary>What was found of the record an id names, and for a missing one, what said so.</summary>
public sealed record ReferenceAnswer(ReferenceState State, string? Detail = null);

/// <summary>
/// Whether the records a record refers to exist, asked of what knows: the ledger (the records this deployment delivers),
/// then OSDU's storage service when the flow asks for it (<c>target.verifyReferences: storage</c>), else the cache version
/// (the reference data a cache flow captured from the partition). A document does not carry what it refers to, so this is
/// the one place an id is resolved: the gate before a record is sent, the record preview and the explorer ask here.
/// </summary>
/// <remarks>
/// Storage, when asked, is the authority for every id the ledger does not hold, as <c>target.verifyReferences</c> has
/// always meant; the cache answers only where storage is not asked, and only for the entity types it holds records of,
/// since a type it does not capture says nothing about whether a record exists. Each source is asked once for every id of
/// a call, so a batch of records costs one question per source.
/// </remarks>
public sealed class ReferenceResolver
{
    private readonly ReferenceSnapshot? _cache;
    private readonly string _cacheLabel;
    private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? _ledger;
    private readonly Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? _osdu;
    private readonly bool _cacheFirst;

    /// <param name="cache">The cache version to look reference data up in, or null for none.</param>
    /// <param name="cacheLabel">How a message names the cache version (<c>cache version 'v12' of dev</c>).</param>
    /// <param name="ledger">The ids, without their version, that a record of the ledger holds; null to not ask the ledger.</param>
    /// <param name="osdu">The ids, without their version, that OSDU's storage service holds; null to not ask OSDU.</param>
    /// <param name="cacheFirst">
    /// Whether what the cache version holds is taken as found rather than asked of storage again: for a check that reads
    /// thousands of records naming the same reference data, which the cache captured from the partition. Storage still decides
    /// every id the cache does not hold. Off, storage is the authority, as <c>target.verifyReferences</c> has always meant.
    /// </param>
    public ReferenceResolver(
        ReferenceSnapshot? cache,
        string? cacheLabel,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? ledger,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? osdu,
        bool cacheFirst = false)
    {
        _cache = cache;
        _cacheLabel = string.IsNullOrWhiteSpace(cacheLabel) ? (cache is null ? "the cache" : $"cache version '{cache.Version}'") : cacheLabel;
        _ledger = ledger;
        _osdu = osdu;
        _cacheFirst = cacheFirst;
    }

    /// <summary>A resolver that asks nothing: every id is not checked.</summary>
    public static ReferenceResolver None { get; } = new(null, null, null, null);

    /// <summary>Whether OSDU's storage service is asked.</summary>
    public bool AsksOsdu => _osdu is not null;

    /// <summary>The key an id is resolved under: the id without its version.</summary>
    public static string Key(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return TargetId.WithoutVersion(id.Trim());
    }

    /// <summary>
    /// What is found of each id among <paramref name="references"/>, by <see cref="Key"/>. A source that cannot be read
    /// throws, so the caller decides what an unanswered question means for its records.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ReferenceAnswer>> ResolveAsync(IEnumerable<FoundReference> references, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(references);
        var byKey = new Dictionary<string, FoundReference>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            byKey.TryAdd(Key(reference.Id), reference);
        }

        var answers = new Dictionary<string, ReferenceAnswer>(StringComparer.Ordinal);
        if (byKey.Count == 0)
        {
            return answers;
        }

        var open = byKey.Keys.ToList();
        if (_ledger is not null)
        {
            var held = await _ledger(open, ct).ConfigureAwait(false);
            foreach (var key in open.Where(held.Contains))
            {
                answers[key] = new ReferenceAnswer(ReferenceState.InLedger);
            }

            open = open.Where(key => !answers.ContainsKey(key)).ToList();
        }

        if (open.Count == 0)
        {
            return answers;
        }

        if (_osdu is not null)
        {
            if (_cacheFirst)
            {
                foreach (var key in open)
                {
                    if (FromCache(byKey[key]) is { State: ReferenceState.InCache } cached)
                    {
                        answers[key] = cached;
                    }
                }

                open = open.Where(key => !answers.ContainsKey(key)).ToList();
                if (open.Count == 0)
                {
                    return answers;
                }
            }

            var present = await _osdu(open, ct).ConfigureAwait(false);
            foreach (var key in open)
            {
                answers[key] = present.Contains(key)
                    ? new ReferenceAnswer(ReferenceState.InOsdu)
                    : new ReferenceAnswer(ReferenceState.Missing, "OSDU's storage service holds no such record");
            }

            return answers;
        }

        foreach (var key in open)
        {
            answers[key] = FromCache(byKey[key]);
        }

        return answers;
    }

    /// <summary>What the cache version says of an id: found, missing from the types holding its entity type, or nothing.</summary>
    private ReferenceAnswer FromCache(FoundReference reference)
    {
        if (_cache is null || CachedReferences.Named(reference.Id) is not { } named)
        {
            return new ReferenceAnswer(ReferenceState.NotChecked);
        }

        var holding = CachedReferences.Holding(_cache, named.EntityType);
        if (holding.Count == 0)
        {
            return new ReferenceAnswer(ReferenceState.NotChecked);
        }

        return CachedReferences.Find(holding, named) is not null
            ? new ReferenceAnswer(ReferenceState.InCache)
            : new ReferenceAnswer(ReferenceState.Missing, $"{_cacheLabel} holds no such {named.EntityType} record in {string.Join(", ", holding.Select(t => t.Name))}");
    }
}
