using System.Globalization;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// What is picked in one dimension: values, by id or as the dimension holds them, and attribute values its keys hold
/// (<c>Country</c> is <c>United States</c>). With attributes alone, every key holding them; with both, the keys of the values picked
/// that hold them. A collected attribute's value picks records as well: those holding it, not every record of its keys.
/// </summary>
public sealed record DimensionPick(
    DimensionState Dimension, IReadOnlyCollection<long> ValueIds, IReadOnlyCollection<string> Values, IReadOnlyList<DimensionAttributeMatch>? Attributes = null)
{
    /// <summary>Whether the pick picks anything.</summary>
    public bool Picks => ValueIds.Count + Values.Count > 0 || Attributes is { Count: > 0 };
}

/// <summary>One dimension's part of a composed search: the values it covers, how many keys they hold, and its filter.</summary>
/// <param name="DimensionId">The dimension.</param>
/// <param name="Dimension">Its name.</param>
/// <param name="AggregateBy">The field the filter compares, as the search's aggregateBy names it.</param>
/// <param name="Values">The values picked that the dimension holds now, each with its id, records and keys.</param>
/// <param name="Keys">The keys the filter compares.</param>
/// <param name="Unfilterable">Keys of the values picked that no query can carry, which the filter leaves out.</param>
/// <param name="Filter">
/// The part's filter: the keys, in one query or several joined with OR when they are more than one query holds, and the
/// values picked of each collected attribute, joined with AND.
/// </param>
/// <param name="Query">The dimension's own query, the records its values were read from; null for every record of its kind.</param>
/// <param name="Attributes">The attribute values the keys were picked by, each under its declared name; empty for a pick of values alone.</param>
/// <param name="Clauses">The clauses the filter holds.</param>
public sealed record DimensionSearchPart(
    int DimensionId, string Dimension, string AggregateBy, IReadOnlyList<DimensionMemberState> Values, int Keys, int Unfilterable, string Filter, string? Query,
    IReadOnlyList<DimensionAttributeMatch> Attributes, int Clauses);

/// <summary>
/// A search composed from values picked across dimensions: the kind to search, the query (each dimension's own query once,
/// then each dimension's filter, joined with AND), each part, how many clauses the query holds, and what the picks left out.
/// </summary>
public sealed record DimensionSearchSet(
    string Kind, string Query, IReadOnlyList<DimensionSearchPart> Parts, int Clauses, IReadOnlyList<string> Removed, IReadOnlyList<string> Missing,
    IReadOnlyList<string> Notes);

/// <summary>
/// Composes the OSDU search that finds the records holding the values picked across a kind's dimensions
/// (osdu/docs/reference/cli/dimensions.md, search): within a dimension, a record holding any key of the values picked;
/// across dimensions, a record matching every dimension; within each dimension's own query, the records its values were
/// read from. A dimension's keys can be picked by the attribute values they hold as well as by value. The API and the
/// CLI compose through here, so a search is the same text wherever it comes from.
/// </summary>
public static class DimensionSearch
{
    /// <summary>
    /// The most clauses a composed query holds: the service allows 1024 in a query (the search API document), and a few are
    /// kept for the dimensions' own queries and one a caller adds.
    /// </summary>
    public const int MaxClauses = 1000;

    /// <summary>The most dimensions one search combines.</summary>
    public const int MaxDimensions = 20;

    /// <summary>
    /// The search finding the records that hold one of the values picked in each dimension of <paramref name="picks"/>, in
    /// <paramref name="kind"/> when given (each dimension's kind has to cover it), else in the one kind every dimension reads;
    /// <paramref name="within"/> narrows it further.
    /// </summary>
    /// <exception cref="DeliveryException">
    /// Nothing is picked, the dimensions read different kinds and none is given, a dimension's kind does not cover the kind
    /// given, a dimension has no build, a pick holds no key a query can carry, or the query would hold more than
    /// <see cref="MaxClauses"/> clauses.
    /// </exception>
    public static async Task<DimensionSearchSet> ComposeAsync(
        DimensionStore dimensions, IReadOnlyList<DimensionPick> picks, string? kind, string? within, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(picks);
        var picked = picks.Where(p => p.Picks).ToList();
        if (picked.Count == 0)
        {
            throw new DeliveryException("A search is composed from at least one value picked in a dimension.");
        }

        if (picked.Count > MaxDimensions)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture, $"A search combines at most {MaxDimensions} dimensions, and {picked.Count} were picked in."));
        }

        if (picked.Select(p => p.Dimension.DimensionId).Distinct().Count() != picked.Count)
        {
            throw new DeliveryException("A dimension is picked in once; name all its values in one pick.");
        }

        var searched = KindOf(picked, kind);
        var parts = new List<DimensionSearchPart>(picked.Count);
        var removed = new List<string>();
        var missing = new List<string>();
        var notes = new List<string>();
        foreach (var pick in picked)
        {
            if (pick.Attributes is { Count: > 0 })
            {
                parts.Add(await ByAttributesAsync(dimensions, pick, removed, missing, notes, ct).ConfigureAwait(false));
                continue;
            }

            var set = await DimensionFilters.ForMembersAsync(dimensions, pick.Dimension, pick.ValueIds, pick.Values, ct).ConfigureAwait(false);
            removed.AddRange(set.Removed.Select(v => $"{pick.Dimension.Name}: {v}"));
            missing.AddRange(set.Missing.Select(v => $"{pick.Dimension.Name}: {v}"));
            if (set.Filters.Count == 0)
            {
                throw new DeliveryException(
                    $"Nothing picked in dimension {pick.Dimension.Name} can be searched for: {(set.Members.Count == 0 ? "no value picked is one it holds now" : "no key of the values picked can be carried in a query")}.");
            }

            if (set.Unfilterable > 0)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{pick.Dimension.Name}: {set.Unfilterable} key(s) of the values picked cannot be carried in a query, so the search does not find the records holding them."));
            }

            var filter = set.Filters.Count == 1 ? set.Filters[0] : "(" + string.Join(" OR ", set.Filters.Select(f => $"({f})")) + ")";
            parts.Add(new DimensionSearchPart(
                pick.Dimension.DimensionId, pick.Dimension.Name, set.AggregateBy, set.Members, set.Originals, set.Unfilterable, filter,
                pick.Dimension.Query, [], set.Originals));
        }

        // Each dimension's own query once, since dimensions of one flow often read the same records; then every filter.
        var scopes = parts.Select(p => p.Query).Where(q => !string.IsNullOrWhiteSpace(q) && q!.Trim() != "*").Select(q => q!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var clauses = parts.Sum(p => p.Clauses) + scopes.Count + (string.IsNullOrWhiteSpace(within) ? 0 : 1);
        if (clauses > MaxClauses)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"The picks hold {parts.Sum(p => p.Keys)} keys and {clauses} clauses in all, and one search holds at most {MaxClauses} (the service allows 1024). Pick fewer values, or search each dimension's values on their own."));
        }

        var terms = scopes.Concat(parts.Select(p => p.Filter)).ToList();
        if (!string.IsNullOrWhiteSpace(within))
        {
            terms.Add(within.Trim());
        }

        // Every term is grouped when there are several, so an OR inside one never reaches across the AND between them.
        var query = terms.Count == 1 ? terms[0] : string.Join(" AND ", terms.Select(t => $"({t})"));
        return new DimensionSearchSet(searched, query, parts, clauses, removed, missing, notes);
    }

    /// <summary>
    /// The part of a pick by attributes: the keys holding every attribute value picked (and belonging to one of the values
    /// picked, when there are some), at most <see cref="DimensionStore.MaxPage"/>, and the filter finding their records;
    /// a value picked of a collected attribute narrows those records to the ones holding it. Collected values picked alone
    /// pick no keys: the filter finds every record holding one, among the records holding a key.
    /// </summary>
    private static async Task<DimensionSearchPart> ByAttributesAsync(
        DimensionStore dimensions, DimensionPick pick, List<string> removed, List<string> missing, List<string> notes, CancellationToken ct)
    {
        var dimension = pick.Dimension;
        var declared = DimensionRunner.AttributesOf(dimension.AttributesJson);
        var matches = new List<DimensionAttributeMatch>();
        foreach (var match in pick.Attributes!)
        {
            var attribute = declared.FirstOrDefault(a => string.Equals(a.Name, match.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new DeliveryException(
                    $"Dimension {dimension.Name} reads no attribute '{match.Name}'{(declared.Count == 0 ? "; it reads none" : $"; it reads {string.Join(", ", declared.Select(a => a.Name))}")}.");
            var values = match.Values.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count == 0)
            {
                throw new DeliveryException($"Attribute {attribute.Name} of dimension {dimension.Name} is picked with no value.");
            }

            // Several matches of one attribute are one: any of their values.
            var index = matches.FindIndex(m => m.Name == attribute.Name);
            if (index >= 0)
            {
                matches[index] = matches[index] with { Values = matches[index].Values.Concat(values).Distinct(StringComparer.Ordinal).ToList() };
            }
            else
            {
                matches.Add(new DimensionAttributeMatch(attribute.Name, values));
            }
        }

        var field = DimensionFilters.FieldOf(dimension.Path, dimension.Field)
            ?? throw new DeliveryException($"Dimension {dimension.Name} has not been built with a field yet, so no filter can be written for it. Build it first.");
        var collected = matches.Where(m => declared.First(a => a.Name == m.Name).IsCollected).ToList();
        var terms = new List<string>();
        var termClauses = 0;
        foreach (var match in collected)
        {
            var (term, count) = await CollectedFilterAsync(dimensions, dimension, match, missing, ct).ConfigureAwait(false);
            terms.Add(term);
            termClauses += count;
        }

        if (pick.ValueIds.Count + pick.Values.Count == 0 && collected.Count == matches.Count)
        {
            // Collected values alone: the records holding one, among those holding a key where the index can say so (the
            // service's _exists_ does not reach inside a nested array).
            if (field.NestedPath is null)
            {
                terms.Insert(0, $"_exists_:{field.ExactPath}");
                termClauses++;
            }

            return new DimensionSearchPart(
                dimension.DimensionId, dimension.Name, field.AggregateBy, [], 0, 0, Joined(terms), dimension.Query, matches, termClauses);
        }

        IReadOnlyCollection<long>? memberIds = null;
        var picked = new List<DimensionMemberState>();
        if (pick.ValueIds.Count + pick.Values.Count > 0)
        {
            var named = await dimensions.GetMembersAsync(dimension.DimensionId, pick.ValueIds, pick.Values, ct).ConfigureAwait(false);
            missing.AddRange(pick.ValueIds.Where(id => named.All(m => m.MemberId != id)).Select(id => $"{dimension.Name}: {id.ToString(CultureInfo.InvariantCulture)}")
                .Concat(pick.Values.Where(v => named.All(m => !string.Equals(m.Value, v, StringComparison.Ordinal))).Select(v => $"{dimension.Name}: {v}")));
            removed.AddRange(named.Where(m => m.RemovedRunId is not null).Select(m => $"{dimension.Name}: {m.Value}"));
            picked = named.Where(m => m.RemovedRunId is null).ToList();
            memberIds = picked.Select(m => m.MemberId).ToList();
            if (memberIds.Count == 0)
            {
                throw new DeliveryException($"Nothing picked in dimension {dimension.Name} can be searched for: no value picked is one it holds now.");
            }
        }

        var keys = await dimensions.ListValuesAsync(
            dimension.DimensionId,
            new DimensionValueQuery(null, null, false, false, null, DimensionStore.MaxPage, DimensionValueOrder.Arrival, matches, memberIds),
            ct).ConfigureAwait(false);
        var described = string.Join(" and ", matches.Select(m => $"{m.Name} is {string.Join(" or ", m.Values)}"));
        if (keys.Count >= DimensionStore.MaxPage)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"The keys of dimension {dimension.Name} where {described} are {DimensionStore.MaxPage} or more, more than one search holds. Pick narrower attribute values, or values of the dimension with them."));
        }

        var filterable = keys.Where(k => k.Filterable).Select(k => k.Original).ToList();
        if (filterable.Count == 0)
        {
            throw new DeliveryException(keys.Count == 0
                ? $"No key of dimension {dimension.Name} holds what is picked: {described}."
                : $"No key of dimension {dimension.Name} where {described} can be carried in a query.");
        }

        if (keys.Count > filterable.Count)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{dimension.Name}: {keys.Count - filterable.Count} key(s) where {described} cannot be carried in a query, so the search does not find the records holding them."));
        }

        var filters = DimensionFilters.Of(field, filterable);
        terms.Insert(0, filters.Count == 1 ? filters[0] : "(" + string.Join(" OR ", filters.Select(f => $"({f})")) + ")");
        if (picked.Count == 0)
        {
            var ids = keys.Where(k => k.MemberId is not null).Select(k => k.MemberId!.Value).Distinct().ToList();
            picked = (await dimensions.GetMembersAsync(dimension.DimensionId, ids, [], ct).ConfigureAwait(false)).ToList();
        }

        return new DimensionSearchPart(
            dimension.DimensionId, dimension.Name, field.AggregateBy, picked, filterable.Count, keys.Count - filterable.Count, Joined(terms), dimension.Query, matches,
            filterable.Count + termClauses);
    }

    /// <summary>Terms that all have to hold, each grouped when there are several.</summary>
    private static string Joined(IReadOnlyList<string> terms) => terms.Count == 1 ? terms[0] : string.Join(" AND ", terms.Select(t => $"({t})"));

    /// <summary>
    /// The filter a pick of a collected attribute adds, with the clauses it holds: the records holding one of the texts the
    /// values picked stand for, as the dimension's last build collected them; and for the value the dimension names for what
    /// is not read, the records holding none of the attribute's texts. A value the attribute does not hold is named as missing.
    /// </summary>
    private static async Task<(string Filter, int Clauses)> CollectedFilterAsync(
        DimensionStore dimensions, DimensionState dimension, DimensionAttributeMatch match, List<string> missing, CancellationToken ct)
    {
        var state = DimensionRunner.CollectedOf(dimension.CollectedJson).FirstOrDefault(c => c.Name == match.Name)
            ?? throw new DeliveryException(
                $"Dimension {dimension.Name} has not been built since it began collecting attribute {match.Name}, so no search can pick its values yet. Build it first.");
        var field = DimensionFilters.FieldOf(state.Path, state.Field)!;
        var asked = match.Values.Distinct(StringComparer.Ordinal).ToList();
        var picked = await dimensions.ListCollectedTextsAsync(dimension.DimensionId, match.Name, asked, MaxClauses + 1, ct).ConfigureAwait(false);
        if (picked.Count > MaxClauses)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"The values picked of attribute {match.Name} of dimension {dimension.Name} stand for more than {MaxClauses} texts, more than one search holds. Pick fewer values."));
        }

        var texts = picked.Select(t => t.Text).ToList();
        var none = state.Missing is { } unread && asked.Contains(unread, StringComparer.Ordinal);
        missing.AddRange(asked
            .Where(v => !string.Equals(v, state.Missing, StringComparison.Ordinal) && picked.All(t => !string.Equals(t.Value, v, StringComparison.Ordinal)))
            .Select(v => $"{dimension.Name}.{match.Name}: {v}"));

        IReadOnlyList<string> all = [];
        if (none)
        {
            // The records holding none of the values are every record but those holding one of the attribute's texts: one
            // search can exclude only so many.
            var every = await dimensions.ListCollectedTextsAsync(dimension.DimensionId, match.Name, null, MaxClauses + 1, ct).ConfigureAwait(false);
            if (every.Count > MaxClauses)
            {
                throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                    $"{state.Missing} of attribute {match.Name} of dimension {dimension.Name} stands for the records holding none of its values, which are more than the {MaxClauses} one search can exclude. Pick its values instead."));
            }

            all = every.Select(t => t.Text).ToList();
        }

        var alternatives = new List<string>();
        if (texts.Count > 0)
        {
            alternatives.AddRange(DimensionFilters.Of(field, texts));
        }

        if (none)
        {
            alternatives.Add(DimensionFilters.NoneOf(field, all));
        }

        if (alternatives.Count == 0)
        {
            throw new DeliveryException(
                $"No value picked of attribute {match.Name} of dimension {dimension.Name} is one it collects: {string.Join(", ", match.Values)}.");
        }

        var filter = alternatives.Count == 1 ? alternatives[0] : string.Join(" OR ", alternatives.Select(a => $"({a})"));
        return (filter, texts.Count + (none ? all.Count + 1 : 0));
    }

    /// <summary>
    /// The kind a composed search reads: the one given, which every dimension's kind has to cover, or the one kind every
    /// dimension reads.
    /// </summary>
    private static string KindOf(IReadOnlyList<DimensionPick> picks, string? kind)
    {
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var wanted = kind.Trim();
            if (!OsduKind.IsValid(wanted))
            {
                throw new DeliveryException($"'{wanted}' is not a kind: authority:source:entityType:version, wildcards allowed per segment.");
            }

            var outside = picks.Where(p => !Covers(p.Dimension.Kind, wanted)).Select(p => $"{p.Dimension.Name} ({p.Dimension.Kind})").ToList();
            return outside.Count == 0
                ? wanted
                : throw new DeliveryException(
                    $"The search reads {wanted}, and {string.Join(", ", outside)} read{(outside.Count == 1 ? "s" : string.Empty)} other records: a dimension's values filter only the kind its keys were read from.");
        }

        var kinds = picks.Select(p => p.Dimension.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return kinds.Count == 1
            ? kinds[0]
            : throw new DeliveryException(
                $"The dimensions picked in read different kinds ({string.Join(", ", kinds)}); a search reads one. Name the kind to search, which each dimension's kind has to cover.");
    }

    /// <summary>Whether a dimension reading <paramref name="pattern"/> covers <paramref name="kind"/>: each segment the same, or a wildcard.</summary>
    internal static bool Covers(string pattern, string kind)
    {
        var have = pattern.Split(':');
        var want = kind.Split(':');
        return have.Length == 4 && want.Length == 4
            && have.Zip(want).All(s => s.First == "*" || string.Equals(s.First, s.Second, StringComparison.OrdinalIgnoreCase));
    }
}
