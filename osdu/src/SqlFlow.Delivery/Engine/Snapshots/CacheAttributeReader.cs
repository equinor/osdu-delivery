using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Engine.Snapshots;

/// <summary>
/// Reads a cache type's attributes (osdu/docs/reference/flow/cache-attributes.md) exactly as a dimension reads its keys'
/// attributes: through <see cref="DimensionLabeler"/>, each cached record's id the key, every step's references followed,
/// filtered, kept, chosen and decoded as the attribute says, with the dimension's bounds and notes. The cached records a
/// capture holds in hand answer the first step themselves, so a key's own record is not read twice; every record a step
/// reaches past them is read by id from <c>records</c>: the search index, as a dimension reads it, or storage, whole, for a
/// type that reads its records whole. One labeler serves each set of lookup kinds for the whole capture, so a record many
/// cached records reach (the well of a hundred wellbores) is read once, as it is from batch to batch of a dimension's keys.
/// </summary>
internal sealed class CacheAttributeReader
{
    private readonly PageFirst _records;
    private readonly int _concurrency;
    private readonly Dictionary<string, DimensionLabeler> _labelers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LabelTally> _tallies = new(StringComparer.Ordinal);

    /// <param name="records">Where the records a step reaches past the cached ones are read by id.</param>
    /// <param name="concurrency">The most reads asked at once.</param>
    public CacheAttributeReader(ILabelRecords records, int concurrency)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = new PageFirst(records);
        _concurrency = Math.Max(1, concurrency);
    }

    /// <summary>The reads of records asked so far, past the cached records in hand.</summary>
    public long Queries { get; private set; }

    /// <summary>
    /// The attributes of each of <paramref name="records"/> (the cached records of one page, each with its id), by the record's
    /// id, each attribute under its name, for the records it gave a value; a record no attribute gave one has no entry.
    /// <paramref name="covers"/> says whether the records in hand hold the paths a read asks for: an index projection holds
    /// what its search asked for, a whole record every path storage keeps.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, List<KeyValuePair<string, ReferenceValue>>>> ReadAsync(
        IReadOnlyList<JsonObject> records, IReadOnlyList<ReferenceFieldSpec> attributes, Func<IReadOnlyList<string>, bool> covers, StringPool pool,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(covers);
        ArgumentNullException.ThrowIfNull(pool);
        var values = new Dictionary<string, List<KeyValuePair<string, ReferenceValue>>>(StringComparer.Ordinal);
        var held = new Dictionary<string, JsonObject>(records.Count, StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record["id"] is JsonValue id && id.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                held.TryAdd(text, record);
            }
        }

        if (held.Count == 0 || attributes.Count == 0)
        {
            return values;
        }

        _records.Use(held, covers);
        try
        {
            // Attributes looking records up in the same kinds are read together, as a dimension's are.
            foreach (var group in attributes.Where(a => a.IsAttribute).GroupBy(a => a.LookupSignature, StringComparer.Ordinal))
            {
                if (!_labelers.TryGetValue(group.Key, out var labeler))
                {
                    labeler = new DimensionLabeler(_records, _concurrency);
                    _labelers[group.Key] = labeler;
                }

                var read = await labeler.ReadAsync(held.Keys.ToList(), [], [.. group.Select(a => a.Attribute!)], group.First().LookupKinds, ct).ConfigureAwait(false);
                Queries += read.Queries;
                if (read.Tally is { } tally)
                {
                    if (_tallies.TryGetValue(group.Key, out var earlier))
                    {
                        earlier.Add(tally);
                    }
                    else
                    {
                        _tallies[group.Key] = tally;
                    }
                }

                foreach (var (key, states) in read.Attributes)
                {
                    var of = values.TryGetValue(key, out var listed) ? listed : values[key] = [];
                    of.AddRange(states.Select(state => KeyValuePair.Create(state.Name, ReferenceValue.Of(pool.Get(state.Value)))));
                }
            }
        }
        finally
        {
            _records.Use(null, null);
        }

        return values;
    }

    /// <summary>What the reads had to say, a line each, as a dimension build's notes say it: the keys without an attribute and why, the keys bounded at a step, the values cut.</summary>
    public IReadOnlyList<string> Notes() => [.. _tallies.Values.SelectMany(t => t.Notes())];

    /// <summary>
    /// Records by id for a labeler: the cached records in hand where they hold what is asked of them, every other one from the
    /// reader behind. The records in hand are set for one read of a page at a time, which the labeler reads side by side.
    /// </summary>
    private sealed class PageFirst(ILabelRecords inner) : ILabelRecords
    {
        private volatile Held? _held;

        public void Use(IReadOnlyDictionary<string, JsonObject>? records, Func<IReadOnlyList<string>, bool>? covers)
            => _held = records is null || covers is null ? null : new Held(records, covers);

        public async Task<IReadOnlyList<JsonObject>> FindAsync(string kind, IReadOnlyList<string> ids, IReadOnlyList<string> fields, CancellationToken ct)
        {
            var held = _held;
            if (held is null || !held.Covers(fields))
            {
                return await inner.FindAsync(kind, ids, fields, ct).ConfigureAwait(false);
            }

            var found = new List<JsonObject>(ids.Count);
            var rest = new List<string>();
            foreach (var id in ids)
            {
                // A record in hand answers when it is of the kind asked: one whose kind is not known is the type's own, which
                // its search found in the kind it declares.
                if (held.Records.TryGetValue(id, out var record)
                    && (record["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var its) || KindPatterns.Matches(kind, its)))
                {
                    found.Add(record);
                }
                else
                {
                    rest.Add(id);
                }
            }

            if (rest.Count > 0)
            {
                found.AddRange(await inner.FindAsync(kind, rest, fields, ct).ConfigureAwait(false));
            }

            return found;
        }

        private sealed record Held(IReadOnlyDictionary<string, JsonObject> Records, Func<IReadOnlyList<string>, bool> Covers);
    }
}

/// <summary>
/// Reads records by id whole from the storage service (openapi storage v2, POST /query/records), a hundred ids a request,
/// for a labeler reading the attributes of a type that reads its records whole: every path a record holds can be read, and
/// a record of another kind than the one asked, as a lookup kind names it, is not returned, as the search would not return
/// it. A record storage leaves unanswered after being asked twice fails the read, so no attribute is cached short.
/// </summary>
internal sealed class StorageLabelRecords(StorageRecords storage) : ILabelRecords
{
    /// <summary>How many ids a message lists of those storage left unanswered.</summary>
    private const int IdsShown = 5;

    public async Task<IReadOnlyList<JsonObject>> FindAsync(string kind, IReadOnlyList<string> ids, IReadOnlyList<string> fields, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(ids);
        var found = new List<JsonObject>(ids.Count);
        foreach (var chunk in ids.Chunk(StorageRecords.Batch))
        {
            var read = await storage.ReadAsync(chunk, attributes: null, ct).ConfigureAwait(false);
            if (read.Unanswered.Count > 0)
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                    $"Storage did not say whether it holds {read.Unanswered.Count} record(s) an attribute reads through, asked twice (retryRecords: {string.Join(", ", read.Unanswered.Take(IdsShown))}), so the attribute could not be read and nothing was cached. Refresh again; the cache keeps the version it had."));
            }

            foreach (var record in read.Records)
            {
                if (JsonNode.Parse(record.GetRawText()) is JsonObject parsed
                    && (parsed["kind"] is not JsonValue kindValue || !kindValue.TryGetValue<string>(out var its) || KindPatterns.Matches(kind, its)))
                {
                    found.Add(parsed);
                }
            }
        }

        return found;
    }
}
