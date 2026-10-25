using System.Globalization;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// The search filters a dimension's values and keys stand for (osdu/docs/reference/flow/dimension-build.md, What a build
/// does): the query that finds every record holding any of the keys given, asked the way the index holds the field, in
/// as few queries as keep each well inside the service's clause limit. A build writes each key's and each value's
/// filter with it, and the API and the CLI build the filter of any set of values with it, so a filter is the same text
/// wherever it comes from. In the ledger a value is a member (<see cref="DimensionMemberState"/>) and a key an original
/// (<see cref="DimensionValueState.Original"/>).
/// </summary>
public static class DimensionFilters
{
    /// <summary>
    /// The most keys one filter query holds: each is a clause, and the service allows 1024 in a query (the search API
    /// document), so this leaves room for the query a filter is combined with.
    /// </summary>
    public const int MaxOriginalsPerQuery = 500;

    /// <summary>The most values one filter is asked for by.</summary>
    public const int MaxMembersPerFilter = 1000;

    /// <summary>The most keys one filter covers: a hundred queries, each well inside the service's clause limit.</summary>
    public const int MaxOriginalsPerFilter = 100 * MaxOriginalsPerQuery;

    /// <summary>The most keys no query can carry that a filter names, beyond the count of them.</summary>
    private const int MaxUnfilterableNamed = 100;

    /// <summary>
    /// The filter of the values of <paramref name="dimension"/> named by id (<paramref name="memberIds"/>) or as the dimension
    /// holds them (<paramref name="values"/>): the queries finding every record that holds one of their keys now, each alone
    /// and joined with the dimension's own query, with what the filter leaves out and why. A value no build finds any more is
    /// named as removed, and one the dimension never held as missing; neither adds to the filter.
    /// </summary>
    /// <exception cref="DeliveryException">
    /// No value is named, more than <see cref="MaxMembersPerFilter"/> are, their keys are more than
    /// <see cref="MaxOriginalsPerFilter"/>, or no build has settled the dimension's field.
    /// </exception>
    public static async Task<DimensionFilterSet> ForMembersAsync(
        ILedger ledger, DimensionState dimension, IReadOnlyCollection<long> memberIds, IReadOnlyCollection<string> values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(memberIds);
        ArgumentNullException.ThrowIfNull(values);
        var asked = memberIds.Distinct().Count() + values.Distinct(StringComparer.Ordinal).Count();
        if (asked == 0)
        {
            throw new DeliveryException("A filter is asked for by at least one value, by its id or as the dimension holds it.");
        }

        if (asked > MaxMembersPerFilter)
        {
            throw new DeliveryException(
                $"A filter is asked for by at most {MaxMembersPerFilter} values, and {asked} were named. Ask for several filters, or narrow the dimension with its query.");
        }

        var field = FieldOf(dimension.Path, dimension.Field)
            ?? throw new DeliveryException($"Dimension {dimension.Name} has not been built with a field yet, so no filter can be written for it. Build it first.");
        var found = await ledger.GetDimensionMembersAsync(dimension.DimensionId, memberIds, values, ct).ConfigureAwait(false);
        var missing = memberIds.Where(id => found.All(m => m.MemberId != id)).Select(id => id.ToString(CultureInfo.InvariantCulture))
            .Concat(values.Where(v => found.All(m => !string.Equals(m.Value, v, StringComparison.Ordinal))))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var held = found.Where(m => m.RemovedRunId is null).ToList();
        var removed = found.Where(m => m.RemovedRunId is not null).Select(m => m.Value).ToList();
        var total = held.Sum(m => (long)m.Originals);
        if (total > MaxOriginalsPerFilter)
        {
            throw new DeliveryException(
                $"The {held.Count} value(s) named hold {total} keys, more than the {MaxOriginalsPerFilter} one filter covers. Ask for fewer values at a time.");
        }

        var originals = await ledger.MemberOriginalsAsync(dimension.DimensionId, held.Select(m => m.MemberId).ToList(), ct).ConfigureAwait(false);
        var filterable = originals.Where(o => o.Filterable).Select(o => o.Original).ToList();
        var unfilterable = originals.Where(o => !o.Filterable).Select(o => o.Original).ToList();
        IReadOnlyList<string> queries = filterable.Count == 0 ? [] : Of(field, filterable);
        return new DimensionFilterSet(
            dimension.Kind,
            dimension.Query,
            field.AggregateBy,
            queries,
            queries.Select(q => OsduSearch.Within(dimension.Query, q)).ToList(),
            held,
            filterable.Count,
            unfilterable.Count,
            unfilterable.Take(MaxUnfilterableNamed).ToList(),
            removed,
            missing);
    }

    /// <summary>
    /// The filter queries finding every record whose <paramref name="field"/> holds one of the keys <paramref name="originals"/>:
    /// one query for up to <see cref="MaxOriginalsPerQuery"/> keys, more for more, none for none. A key no query can carry is
    /// not given; ask <see cref="Filterable"/> first.
    /// </summary>
    /// <exception cref="OsduQueryException">A key cannot be carried in a query.</exception>
    public static IReadOnlyList<string> Of(OsduField field, IReadOnlyCollection<string> originals)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(originals);
        return originals
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Chunk(MaxOriginalsPerQuery)
            .Select(chunk => OsduQuery.AnyOf(field, chunk).Text)
            .ToList();
    }

    /// <summary>
    /// The filter finding every record whose <paramref name="field"/> holds none of <paramref name="originals"/>: every record
    /// (each holds an id) but those holding one of them. The service refuses a query that only excludes, so the filter
    /// starts from every record. A key no query can carry is not given; ask <see cref="Filterable"/> first.
    /// </summary>
    /// <exception cref="OsduQueryException">A key cannot be carried in a query.</exception>
    public static string NoneOf(OsduField field, IReadOnlyCollection<string> originals)
    {
        var any = Of(field, originals);
        return any.Count == 0 ? EveryRecord : EveryRecord + " AND " + string.Join(" AND ", any.Select(q => $"NOT ({q})"));
    }

    /// <summary>The clause every record matches: each holds an id.</summary>
    public const string EveryRecord = OsduQuery.EveryRecord;

    /// <summary>Whether a query can carry the key <paramref name="original"/> of <paramref name="field"/>, so a filter can find its records.</summary>
    public static bool Filterable(OsduField field, string original) => OsduQuery.EqualProblem(field, original) is null;

    /// <summary>
    /// The field a dimension's builds read, rebuilt from what the ledger keeps of it (<paramref name="field"/>, as a build
    /// settled it), so a filter asked for later is written as the build wrote its values' filters. Null when no build has
    /// settled the field.
    /// </summary>
    /// <exception cref="OsduQueryException">The ledger's field is not one a query can ask.</exception>
    public static OsduField? FieldOf(string path, DimensionFieldState? field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (field is null)
        {
            return null;
        }

        var index = field.Index switch
        {
            "text" => OsduFieldIndex.Text,
            "keyword" => OsduFieldIndex.Keyword,
            "number" => OsduFieldIndex.Number,
            "boolean" => OsduFieldIndex.Boolean,
            "date" => OsduFieldIndex.Date,
            _ => throw new OsduQueryException($"'{field.Index}' is not a way the indexer stores a property: the ledger names text, keyword, number, boolean or date."),
        };
        return OsduField.Of(path, index, field.NestedPath);
    }
}

/// <summary>The filter of a set of a dimension's values, as the API answers it and the CLI prints it.</summary>
/// <param name="Kind">The kind the dimension reads, which every query searches.</param>
/// <param name="Query">The dimension's own query, as its last build ran it; null for every record of the kind.</param>
/// <param name="AggregateBy">The field the filter compares, as the search's aggregateBy names it.</param>
/// <param name="Filters">The filter queries alone, each for up to <see cref="DimensionFilters.MaxOriginalsPerQuery"/> keys.</param>
/// <param name="Searches">Each filter joined with the dimension's own query: the searches that find the values' records.</param>
/// <param name="Members">The values the filter covers, each held by the dimension now.</param>
/// <param name="Originals">The keys the filter finds.</param>
/// <param name="Unfilterable">The keys of the values no query can carry, which the filter leaves out.</param>
/// <param name="UnfilterableNamed">The first of those, by name.</param>
/// <param name="Removed">The values named that no build finds any more.</param>
/// <param name="Missing">The ids and values named that are no value of the dimension.</param>
public sealed record DimensionFilterSet(
    string Kind, string? Query, string AggregateBy, IReadOnlyList<string> Filters, IReadOnlyList<string> Searches, IReadOnlyList<DimensionMemberState> Members,
    int Originals, int Unfilterable, IReadOnlyList<string> UnfilterableNamed, IReadOnlyList<string> Removed, IReadOnlyList<string> Missing);
