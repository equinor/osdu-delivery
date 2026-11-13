using System.Globalization;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Search;

/// <summary>
/// The explorer's list of types (osdu/docs/reference/concepts/explorer.md, The types): the kinds the records a search finds
/// are of, counted by the search service's groups of <c>kind</c>, and completed from the kinds the partition's Schema
/// service lists.
/// </summary>
/// <remarks>
/// <para>
/// The search service names at most 1,000 groups in one answer, the largest first (search-service
/// <c>AggregationParserUtil</c>, <c>aggregationSize</c>), so in a partition holding records of more kinds the smallest are
/// left out, which a partition's own kinds usually are. The kinds the schema listing holds and the first answer did not name
/// are then counted apart, <see cref="FillBatch"/> to an answer, each asked as the first was with a condition naming them.
/// </para>
/// <para>
/// The search counts only the records the connection may read, so a kind the Schema service lists with nothing the search
/// finds is a kind with no records, or none the connection may read. With no text and no condition it is listed all the
/// same, with no records, so the list holds every kind the partition has a schema for; a search lists only where it found
/// records.
/// </para>
/// </remarks>
public sealed partial class RecordExplorer
{
    /// <summary>How many kinds one answer counting the kinds the first left out names: far inside the 1,024 clauses a query may hold.</summary>
    public const int FillBatch = 100;

    /// <summary>The most answers one list of types asks to count the kinds the first left out.</summary>
    public const int MaxFillAsks = 40;

    /// <summary>How many of those answers are asked at once.</summary>
    public const int FillConcurrency = 4;

    /// <summary>The record's own <c>kind</c>, a keyword the groups and the conditions on kinds are asked of.</summary>
    private static readonly OsduField KindField = OsduField.Of("kind", OsduFieldIndex.Keyword, null);

    /// <summary>
    /// The kinds of the records <paramref name="search"/>'s text and property values find across every kind, each with its count:
    /// the search's own kind, order and page do not narrow it, since the list is what a reader picks a kind from.
    /// </summary>
    public Task<ExplorerTypes> TypesAsync(ExplorerSearch search, CancellationToken ct) => TypesAsync(search, null, ct);

    /// <summary>
    /// The kinds of the records <paramref name="search"/> finds, as <see cref="TypesAsync(ExplorerSearch, CancellationToken)"/>
    /// counts them, completed from <paramref name="schemas"/>, the listing of the partition's schemas, as the remarks on
    /// <see cref="RecordExplorer"/>'s types describe: the kinds the service's groups left out counted apart, and with no text
    /// and no condition, every kind listed. Each kind the listing holds carries its schema's status and scope.
    /// </summary>
    public async Task<ExplorerTypes> TypesAsync(ExplorerSearch search, SchemaKindsView? schemas, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(search);
        if (search.Problem() is { } problem)
        {
            throw new DeliveryException(problem);
        }

        var notes = new List<string>();
        search = await ThroughOthersAsync(search, notes, ct).ConfigureAwait(false);
        var reading = Interpret(search with { Kind = null }, _partition);
        var asked = await AskAsync(reading, search.Filters, ExplorerKinds.Any, ExplorerSort.Relevance, 0, 1, "kind", ["id"], notes, ct).ConfigureAwait(false);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var bucket in asked.Answer.Buckets.Where(b => !string.IsNullOrEmpty(b.Key)))
        {
            counts[bucket.Key!] = bucket.Count;
        }

        var listing = schemas?.Last;
        var registered = listing?.Kinds ?? new Dictionary<string, SchemaListing>(StringComparer.Ordinal);
        if (asked.Answer.Refusal is null && counts.Values.Sum() < asked.Answer.Total && registered.Count > 0)
        {
            await FillAsync(asked.Query, registered.Keys.Where(k => !counts.ContainsKey(k)).Order(StringComparer.Ordinal).ToList(), counts, notes, ct).ConfigureAwait(false);
        }

        // Every kind the partition has a schema for is a place to pick, records or none, while nothing narrows the list.
        if (reading.Reading == "everything" && search.Filters.Count == 0 && asked.Answer.Refusal is null)
        {
            foreach (var kind in registered.Keys)
            {
                counts.TryAdd(kind, 0);
            }
        }

        var kinds = counts
            .Select(c => registered.TryGetValue(c.Key, out var schema)
                ? new ExplorerKindCount(c.Key, c.Value, schema.Status, schema.Scope)
                : new ExplorerKindCount(c.Key, c.Value))
            .OrderBy(k => k.Kind, StringComparer.Ordinal)
            .ToList();
        var state = schemas is null ? null : new ExplorerTypeSchemas(schemas.State, registered.Count, listing?.ReadUtc, listing is null ? schemas.Problem : null);
        if (listing is not null)
        {
            notes.AddRange(listing.Notes);
        }

        return new ExplorerTypes(reading.Reading, asked.Query, asked.Answer.Total, kinds, kinds.Sum(k => k.Count), notes, asked.Answer.Refusal, state);
    }

    /// <summary>
    /// Counts the records of <paramref name="missing"/>, kinds the first answer did not name, as it was asked
    /// (<paramref name="query"/>) with a condition naming them, <see cref="FillBatch"/> to an answer, at most
    /// <see cref="MaxFillAsks"/> answers; a kind with no record found stays uncounted. A refusal stops it, with a note.
    /// </summary>
    private async Task FillAsync(string? query, IReadOnlyList<string> missing, Dictionary<string, long> counts, List<string> notes, CancellationToken ct)
    {
        var batches = missing.Chunk(FillBatch).ToList();
        string? refused = null;
        await Parallel.ForEachAsync(
            batches.Take(MaxFillAsks),
            new ParallelOptions { MaxDegreeOfParallelism = FillConcurrency, CancellationToken = ct },
            async (batch, token) =>
            {
                var named = $"({OsduQuery.AnyOf(KindField, batch).Text})";
                var answer = await PageAsync(ExplorerKinds.Any, query is null ? named : $"({query}) AND {named}", ExplorerSort.Relevance, 0, 1, "kind", ["id"], token)
                    .ConfigureAwait(false);
                lock (counts)
                {
                    if (answer.Refusal is not null)
                    {
                        refused ??= answer.Refusal;
                        return;
                    }

                    foreach (var bucket in answer.Buckets.Where(b => !string.IsNullOrEmpty(b.Key)))
                    {
                        counts.TryAdd(bucket.Key!, bucket.Count);
                    }
                }
            }).ConfigureAwait(false);

        if (refused is not null)
        {
            notes.Add($"The search service names at most 1,000 kinds in one answer, and would not count the records of the kinds it left out: {refused}");
        }

        if (batches.Count > MaxFillAsks)
        {
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The partition's schemas list {missing.Count:N0} kinds the search service's first answer left out; the records of the first {MaxFillAsks * FillBatch:N0} were counted."));
        }
    }
}
