using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// Keys a pass over a dimension's records covers, the query that finds their records, and the ranges of keys the pass is cut
/// into. A full load's scope is every key the dimension's own read found, which the build staged rather than holds: a record's
/// key is the scope's whatever it is, and the build's write keeps only the keys it staged. An incremental load's scopes are
/// the keys it reads again, a query's worth at a time, each group held with the filter finding its records, so a record two
/// groups' filters find is counted in each under that group's keys alone. The keys of two scopes are never the same.
/// </summary>
/// <param name="Query">The query finding the scope's records; null for every record of the kind.</param>
/// <param name="Held">The scope's keys with how many records hold each; null for every key the dimension's own read found.</param>
/// <param name="Ranges">The ranges of keys a pass over the scope's records is cut into, read side by side.</param>
/// <param name="Keys">How many keys the scope holds.</param>
/// <param name="Units">The records (or objects of a nested array) holding the scope's keys, a record holding several counted under each.</param>
/// <param name="Records">The records the query matches, when the read counted them.</param>
internal sealed record DimensionScope(
    string? Query, IReadOnlyDictionary<string, long>? Held, IReadOnlyList<DistinctSlice> Ranges, long Keys, long Units, long? Records)
{
    /// <summary>Whether a key a record holds is one of the scope's.</summary>
    public bool Holds(string key) => Held is null || Held.ContainsKey(key);

    /// <summary>
    /// Every key the dimension's read (<paramref name="read"/>) found, under <paramref name="query"/>, cut along the ranges the
    /// read covered (<see cref="DimensionCollector.Ranges(OsduField, IReadOnlyList{DistinctRange}, IComparer{string}, int)"/>).
    /// </summary>
    public static DimensionScope Every(string? query, DistinctCounts read, OsduField keyField, int readers)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(keyField);
        return new DimensionScope(
            query, null, DimensionCollector.Ranges(keyField, read.Ranges, DimensionValueText.Order(keyField.Index), readers), read.Keys, read.Units, read.Records);
    }

    /// <summary>The keys <paramref name="keys"/> holds, under <paramref name="query"/>, cut along them.</summary>
    public static DimensionScope Of(string? query, IReadOnlyDictionary<string, long> keys, OsduField keyField, int readers)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(keyField);
        return new DimensionScope(
            query, keys, DimensionCollector.Ranges(keyField, keys, DimensionValueText.Order(keyField.Index), readers), keys.Count, keys.Values.Sum(), null);
    }
}
