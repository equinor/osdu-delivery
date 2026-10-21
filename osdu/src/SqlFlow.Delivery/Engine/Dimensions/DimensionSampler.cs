using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>How the platform indexes a path, as the builder carries it between the control plane and the read: the path, its index's type, and the nested array it sits in.</summary>
/// <param name="Path">The path from the record's root.</param>
/// <param name="Index">text, keyword, number, boolean or date.</param>
/// <param name="NestedPath">The nested array the path sits in; null for none.</param>
public sealed record DimensionFieldWire(string Path, string Index, string? NestedPath)
{
    /// <summary>The field, or null for a wire whose index is none the platform keeps.</summary>
    public OsduField? Field() => Index switch
    {
        "text" => OsduField.Text(Path, NestedPath),
        "keyword" => OsduField.Keyword(Path, NestedPath),
        "number" => OsduField.Number(Path, NestedPath),
        "boolean" => OsduField.Boolean(Path, NestedPath),
        "date" => OsduField.Date(Path, NestedPath),
        _ => null,
    };

    public static DimensionFieldWire Of(OsduField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return new DimensionFieldWire(field.Path, DimensionRunner.Index(field.Index), field.NestedPath);
    }
}

/// <summary>
/// What the builder asks of the keys a dimension reads: the kind and the query narrowing it, the key's path, and how the
/// path is indexed, which says how the search groups its values.
/// </summary>
public sealed record DimensionKeysRequest
{
    public required string Kind { get; init; }

    /// <summary>The query narrowing the records, its tokens filled; null for every record of the kind.</summary>
    public string? Query { get; init; }

    public required string Path { get; init; }

    /// <summary>How the path is indexed; null where no template and no mapping of the record says so, and the keys cannot be grouped.</summary>
    public DimensionFieldWire? KeyField { get; init; }

    /// <summary>
    /// Whether <see cref="KeyField"/> is a guess, made because no template a synced repository holds says how the path is
    /// indexed: text, which most properties of data are. A guess is said in the answer's notes, since a build refuses such a
    /// path until a template folder on the flow's path holds the template.
    /// </summary>
    public bool KeyFieldGuessed { get; init; }
}

/// <summary>A key of the dimension with how many of its records hold it, as the search counts them.</summary>
public sealed record DimensionKeyCount(string Key, long Count);

/// <summary>
/// The keys a dimension reads, as the builder steps through them for its example: how many records the kind (and the
/// query) holds, the commonest keys with their records, whether there are more, how the key was read (and whether that was
/// a guess), what the read had to say, and the search service's own words when it refused the query.
/// </summary>
public sealed record DimensionKeys(
    long Total, IReadOnlyList<DimensionKeyCount> Keys, bool MoreKeys, DimensionFieldWire? KeyField, bool KeyFieldGuessed,
    IReadOnlyList<string> Notes, string? Refusal);

/// <summary>
/// One key of a draft as a build would make it: the key, its label and the record it came from, why it has none, its value
/// (or why it is left out of every value), its attributes, the records holding it and the filter finding them, and what
/// the reads had to say.
/// </summary>
public sealed record DimensionExample(
    string Key, string? Label, string? LabelFrom, string? Problem, string? Value, string? LeftOut, string? Note,
    IReadOnlyList<DimensionAttributeState> Attributes, long? Records, string? Filter, IReadOnlyList<string> Notes);

/// <summary>
/// The reads the explorer's dimension builder makes of OSDU (osdu/docs/reference/concepts/explorer.md, Building a
/// dimension), beyond the explorer's own reads of the records a person browses: the commonest keys of a path, which the
/// builder's example steps through, and one key made into its row exactly as a build makes it, through the build's own
/// labeler, collector display, cleaner and attribute assembly. Nothing is written.
/// </summary>
public sealed class DimensionSampler
{
    /// <summary>The keys offered as examples: the commonest, as a dimension's own pages offer theirs.</summary>
    public const int ExampleKeys = 25;

    private readonly OsduSearch _search;
    private readonly ILogger _log;

    public DimensionSampler(OsduSearch search, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(log);
        _search = search;
        _log = log;
    }

    /// <summary>
    /// The commonest keys <paramref name="request"/> names, with the count of the records the query matches: one search,
    /// grouped by the key's field, its refusal answered in the service's words rather than thrown.
    /// </summary>
    public async Task<DimensionKeys> KeysAsync(DimensionKeysRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var notes = new List<string>();
        var field = request.KeyField?.Field();
        if (field is null)
        {
            notes.Add($"How {request.Path} is indexed is not known, so its keys are not grouped: put the template of {request.Kind} in a template folder and sync the repository to read them.");
        }
        else if (request.KeyFieldGuessed)
        {
            notes.Add($"No template a synced repository holds of {request.Kind} says how {field.Path} is indexed, so it is read here as text, which most properties are. A build reads it only once a template folder on the flow's path holds the template.");
        }

        var answer = await _search.PageAsync(new OsduSearchQuery { Kind = request.Kind, Query = request.Query }, 0, 1, field?.AggregateBy, ct).ConfigureAwait(false);
        if (answer.Refusal is { } refusal)
        {
            return new DimensionKeys(0, [], false, request.KeyField, request.KeyFieldGuessed, notes, refusal);
        }

        var held = answer.Buckets.Where(b => !string.IsNullOrEmpty(b.Key)).ToList();
        return new DimensionKeys(
            answer.Total,
            held.Take(ExampleKeys).Select(b => new DimensionKeyCount(b.Key!, b.Count)).ToList(),
            held.Count > ExampleKeys,
            request.KeyField,
            request.KeyFieldGuessed,
            notes,
            null);
    }

    /// <summary>
    /// <paramref name="key"/> made into its row as a build makes it, for <paramref name="dimension"/> read with
    /// <paramref name="query"/> (its tokens filled): its label and attributes read through the record it names by the build's
    /// labeler, the values it collects from the records holding it (<paramref name="collected"/>, each attribute's field) shown
    /// as a build shows them, its value cleaned by <paramref name="cleaner"/> from what a build cleans it from, and the records
    /// holding it with the filter finding them, when <paramref name="keyField"/> says how the key is indexed.
    /// </summary>
    public async Task<DimensionExample> ExampleAsync(
        DimensionSpec dimension, string key, string? query, OsduField? keyField, IReadOnlyDictionary<string, OsduField> collected, DimensionCleaner cleaner,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(collected);
        ArgumentNullException.ThrowIfNull(cleaner);
        var notes = new List<string>();
        var labeler = new DimensionLabeler(_search, _log, concurrency: 4);
        var labels = await labeler.ReadAsync([key], dimension.Label, dimension.Attributes, ct).ConfigureAwait(false);
        notes.AddRange(labels.Notes);
        var labelled = labels.Labels.GetValueOrDefault(key);

        string? filter = null;
        long? records = null;
        var collects = new List<DimensionAttributeState>();
        if (keyField is null)
        {
            notes.Add("How the key is indexed is not known, so the records holding it are not counted and its collected values are not read.");
        }
        else if (!DimensionFilters.Filterable(keyField, key))
        {
            notes.Add("The key holds what no query can carry, so no filter finds its records: a build keeps it, marked unfilterable.");
        }
        else
        {
            filter = DimensionFilters.Of(keyField, [key])[0];
            var holding = OsduSearch.Within(query, filter);
            records = await _search.CountAsync(new OsduSearchQuery { Kind = dimension.Kind, Query = holding }, ct).ConfigureAwait(false);
            foreach (var attribute in dimension.Attributes.Where(a => a.IsCollected))
            {
                if (!collected.TryGetValue(attribute.Name, out var field))
                {
                    notes.Add($"How {attribute.Collect} is indexed is not known, so the values {attribute.Name} collects are not read.");
                    continue;
                }

                collects.AddRange(await CollectAsync(dimension, attribute, holding, field, records ?? 0, notes, ct).ConfigureAwait(false));
            }
        }

        var attributes = DimensionRunner.KeyAttributes(
            dimension, [key], labels, new Dictionary<string, List<DimensionAttributeState>>(StringComparer.Ordinal) { [key] = collects });
        var cleaned = cleaner.Clean(DimensionRunner.ValueSourceOf(dimension, key, labelled));
        return new DimensionExample(
            key, labelled?.Label, labelled?.From, labelled?.Problem,
            cleaned.Outcome == CleanOutcome.Member ? cleaned.Value : null,
            cleaned.Outcome == CleanOutcome.Member ? null : cleaned.Note,
            cleaned.Outcome == CleanOutcome.Member ? cleaned.Note : null,
            attributes.GetValueOrDefault(key) ?? [], records, filter, notes);
    }

    /// <summary>
    /// The values <paramref name="attribute"/> collects from the records holding the key (<paramref name="holding"/>), each shown
    /// as a build shows it with how many of those records hold it; and, for a dimension naming the value of what is not
    /// read, that value for the records holding none of them, as a build counts them.
    /// </summary>
    private async Task<List<DimensionAttributeState>> CollectAsync(
        DimensionSpec dimension, DimensionAttributeSpec attribute, string holding, OsduField field, long records, List<string> notes, CancellationToken ct)
    {
        IReadOnlyList<OsduSearchBucket> buckets;
        try
        {
            (_, buckets) = await _search.AggregateAsync(new OsduSearchQuery { Kind = dimension.Kind, Query = holding }, field.AggregateBy, ct).ConfigureAwait(false);
        }
        catch (OsduStatusException ex)
        {
            notes.Add($"The search service would not group the key's records by {attribute.Collect}: {ex.Message}");
            return [];
        }

        // Each text as a build shows it; texts shown alike are one value, their records added up, as a build keeps them.
        var texts = new List<string>();
        var values = new Dictionary<string, DimensionAttributeState>(StringComparer.Ordinal);
        foreach (var bucket in buckets)
        {
            if (string.IsNullOrEmpty(bucket.Key) || !DimensionFilters.Filterable(field, bucket.Key))
            {
                continue;
            }

            texts.Add(bucket.Key);
            if (DimensionCollector.ShownAs(bucket.Key) is { } shown)
            {
                values[shown] = values.TryGetValue(shown, out var held)
                    ? held with { Records = held.Records + bucket.Count }
                    : new DimensionAttributeState(attribute.Name, shown, bucket.Key, bucket.Count);
            }
        }

        if (dimension.Unlabelled is { } none && records > 0)
        {
            // The records holding none of the texts take the dimension's value for what is not read, as a build counts them;
            // that is told only when the search named every text, which one answer does up to the platform's group size.
            if (buckets.Count < DimensionSource.DefaultAggregationSize)
            {
                var without = texts.Count == 0
                    ? records
                    : await _search.CountAsync(
                        new OsduSearchQuery { Kind = dimension.Kind, Query = OsduSearch.Within(holding, DimensionFilters.NoneOf(field, texts)) }, ct).ConfigureAwait(false);
                if (without > 0)
                {
                    values[none] = values.TryGetValue(none, out var held)
                        ? held with { Records = held.Records + without }
                        : new DimensionAttributeState(attribute.Name, none, null, without);
                }
            }
            else
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"The key's records hold {buckets.Count} or more values of {attribute.Collect}, so those holding none of them are not counted here."));
            }
        }

        return values.Values.OrderByDescending(v => v.Records).ThenBy(v => v.Value, StringComparer.Ordinal).ToList();
    }
}
