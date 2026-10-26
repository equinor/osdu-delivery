using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>What a build collected of one attribute from its dimension's own records, the rows staged: how it read them, and what it has to say.</summary>
/// <param name="Passed">Whether the records were read in one pass of the search cursor, rather than with a read per value.</param>
/// <param name="Aggregations">Aggregations asked.</param>
/// <param name="ScanPages">Cursor pages read.</param>
/// <param name="Notes">What the read has to say, a line each.</param>
internal sealed record CollectedAttribute(bool Passed, int Aggregations, int ScanPages, IReadOnlyList<string> Notes);

/// <summary>
/// Collects an attribute from a dimension's own records (osdu/docs/reference/flow/dimension-attributes.md, Labels and attributes):
/// every value the records holding each key hold at the attribute's path, with how many hold it, and, when the
/// dimension names a value for what is not read, how many hold none of them. The attribute's distinct values are read
/// first, as any field's are, so however many there are the read pages; a value is shown as a label is (a reference by
/// the code its id ends with, trimmed), and a text no query can carry, or one shown as nothing, is no value and counts
/// as none. What it collects is staged as it is read (<see cref="DimensionStage"/>): each key's values, a row for each
/// time a read counted one (the write adds them up, naming where a value was read by the first of its texts, ordinally),
/// and each text with the value it is shown as.
/// </summary>
/// <remarks>
/// The keys are then read the cheaper of two ways, both exact. With few values (one query can exclude them all) and fewer
/// reads than one pass would take pages, a distinct read of the dimension's own field per value, narrowed to the records
/// holding it, and one more narrowed to those holding none. Otherwise one pass over the dimension's records through the
/// search cursor, each record's key and value read together, which costs a page per thousand records however many values
/// there are. A cursor reads its pages one after another, so the pass is cut into ranges of the dimension's keys holding
/// about as many records each, read side by side: several cursors, each over records of its own. A key outside the scope is
/// passed over.
/// </remarks>
internal sealed class DimensionCollector(OsduSearch search, ILogger log, int aggregationSize, int concurrency)
{
    /// <summary>The fewest records a range of the pass holds: a few pages, so no cursor is opened for a handful of records.</summary>
    internal const int MinRangeRecords = 4_000;

    /// <summary>The ranges the pass is cut into for each reader, so the readers stay busy to the end though ranges differ in how fast they answer.</summary>
    private const int RangesPerReader = 4;

    /// <summary>Collects <paramref name="attribute"/>, held at <paramref name="field"/>, for the keys of <paramref name="keyField"/> <paramref name="scope"/> covers, into <paramref name="stage"/>.</summary>
    /// <exception cref="DeliveryException">The records hold more distinct values at the path than the dimension keeps (its maxValues).</exception>
    public async Task<CollectedAttribute> CollectAsync(
        DimensionSpec dimension, DimensionAttributeSpec attribute, DimensionScope scope, OsduField keyField, bool keyRepeats, OsduField field, bool repeats,
        DimensionStage stage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(attribute);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(keyField);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(stage);

        // Each text as it is shown, staged with the records holding it; a text no query can carry is no value a pick could
        // find, so it is left out with a note, and its records count as holding none, as a search for none of the values
        // finds them.
        var texts = new ShownTexts(attribute, field, stage);
        DistinctCounts read;
        try
        {
            read = await DistinctValues.ReadAsync(
                new SearchDistinctSource(search, dimension.Kind, scope.Query, field),
                new DistinctReadOptions(aggregationSize, dimension.MaxValues, repeats, Checks: false, Concurrency: Math.Max(1, concurrency)),
                log, texts, ct).ConfigureAwait(false);
        }
        catch (DimensionTooLargeException ex)
        {
            throw new DeliveryException($"Attribute {attribute.Name} of dimension {dimension.Name} collects {attribute.Collect}: {ex.Message}", ex);
        }

        var notes = new List<string>();
        if (texts.Uncut > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{attribute.Name}: {texts.Uncut} value(s) are longer than the {DimensionSpec.MaxAttributeValueLength} characters a dimension keeps, and are left out rather than cut, since a key cut joins to nothing."));
        }

        if (texts.Unfilterable > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{attribute.Name}: {texts.Unfilterable} value(s) the records hold cannot be carried in a query, so no pick finds them and their records hold none: {string.Join(", ", texts.FirstUnfilterable().Select(t => $"'{(t.Length > 60 ? t[..60] + "..." : t)}'"))}{(texts.Unfilterable > 5 ? ", ..." : string.Empty)}."));
        }

        var records = scope.Records ?? scope.Units;
        var passPages = (records + OsduSearch.MaxPage - 1) / OsduSearch.MaxPage;
        var byValue = texts.Shown is { } few && few.Values.Distinct(StringComparer.Ordinal).Count() + (dimension.Unlabelled is null ? 0 : 1) < passPages;
        var passRanges = 0;
        int aggregations;
        int pages;
        if (byValue)
        {
            (aggregations, pages) = await ByValueAsync(dimension, attribute, scope, keyField, keyRepeats, field, texts.Shown!, stage, ct).ConfigureAwait(false);
        }
        else
        {
            (pages, passRanges) = await InOnePassAsync(dimension, attribute, scope, keyField, field, stage, ct).ConfigureAwait(false);
            aggregations = 0;
        }

        log.LogInformation(
            "dimension {Dimension}: attribute {Attribute} collected {Texts} text(s) from {Path}, {How}",
            dimension.Name, attribute.Name, texts.Count, attribute.Collect,
            byValue
                ? string.Create(CultureInfo.InvariantCulture, $"read per value in {aggregations} aggregation(s)")
                : string.Create(CultureInfo.InvariantCulture, $"read in one pass of {pages} page(s) over {passRanges} range(s) of keys"));
        return new CollectedAttribute(!byValue, read.Aggregations + aggregations, read.ScanPages + pages, notes);
    }

    /// <summary>
    /// The keys of each value read by a distinct read of the dimension's field over the records holding one of its texts, in
    /// parallel; and those of the records holding none, when the dimension names a value for them.
    /// </summary>
    private async Task<(int Aggregations, int Pages)> ByValueAsync(
        DimensionSpec dimension, DimensionAttributeSpec attribute, DimensionScope scope, OsduField keyField, bool keyRepeats, OsduField field,
        IReadOnlyDictionary<string, string> shown, DimensionStage stage, CancellationToken ct)
    {
        var options = new DistinctReadOptions(aggregationSize, dimension.MaxValues, keyRepeats, Checks: false);
        var aggregations = 0;
        var pages = 0;
        var gate = new Lock();
        var groups = shown.GroupBy(s => s.Value, s => s.Key, StringComparer.Ordinal).ToList();
        await Parallel.ForEachAsync(
            groups,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct },
            async (group, token) =>
            {
                var texts = group.Order(StringComparer.Ordinal).ToList();
                var filter = string.Join(" OR ", DimensionFilters.Of(field, texts).Select(q => $"({q})"));
                var holding = await DistinctValues.ReadAsync(
                    new SearchDistinctSource(search, dimension.Kind, OsduSearch.Within(scope.Query, filter), keyField), options, log,
                    new KeysHolding(scope, attribute.Name, group.Key, texts[0], stage), token).ConfigureAwait(false);
                lock (gate)
                {
                    aggregations += holding.Aggregations;
                    pages += holding.ScanPages;
                }
            }).ConfigureAwait(false);

        if (dimension.Unlabelled is { } none)
        {
            if (shown.Count == 0)
            {
                // No value at all: every record of every key holds none.
                if (scope.Held is { } held)
                {
                    await stage.AddAttributesAsync(held.Select(k => (k.Key, new DimensionAttributeState(attribute.Name, none, null, k.Value))).ToList(), ct).ConfigureAwait(false);
                }
                else
                {
                    await stage.AddToEveryKeyAsync(attribute.Name, none, ct).ConfigureAwait(false);
                }
            }
            else
            {
                var lacking = await DistinctValues.ReadAsync(
                    new SearchDistinctSource(search, dimension.Kind, OsduSearch.Within(scope.Query, DimensionFilters.NoneOf(field, shown.Keys.ToList())), keyField),
                    options with { Concurrency = Math.Max(1, concurrency) }, log, new KeysHolding(scope, attribute.Name, none, null, stage), ct).ConfigureAwait(false);
                aggregations += lacking.Aggregations;
                pages += lacking.ScanPages;
            }
        }

        return (aggregations, pages);
    }

    /// <summary>
    /// Every record of the dimension read once through the search cursor, its key and its value together: each value it holds
    /// counted once under each key it holds, or, holding none, under the value for what is not read when the dimension names
    /// one. The records are read in ranges of the scope's keys, side by side; a record holding keys of two ranges is read in
    /// both and counted in each under the keys of that range alone, so every record and key is counted once. A page's counts
    /// are staged together.
    /// </summary>
    private async Task<(int Pages, int Ranges)> InOnePassAsync(
        DimensionSpec dimension, DimensionAttributeSpec attribute, DimensionScope scope, OsduField keyField, OsduField field, DimensionStage stage, CancellationToken ct)
    {
        var order = DimensionValueText.Order(keyField.Index);
        var fields = new[] { "id", keyField.Path, field.Path }.Distinct(StringComparer.Ordinal).ToList();
        var own = string.IsNullOrWhiteSpace(scope.Query) || scope.Query.Trim() == "*" ? null : scope.Query.Trim();
        var pages = 0;
        await Parallel.ForEachAsync(
            scope.Ranges,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct },
            async (range, token) =>
            {
                var request = new OsduSearchQuery
                {
                    Kind = dimension.Kind,
                    Query = range.IsWhole ? own : OsduSearch.Within(own, OsduQuery.Range(keyField, range.From, range.To).Text),
                    ReturnedFields = fields,
                };

                // The reader hands each record out once, so a record the cursor returns twice is counted once, as an aggregation
                // counts it; and it hands out every record of the range or fails the build, so no range is counted short.
                await foreach (var page in search.PagesAsync(request, OsduSearch.MaxPage, token).ConfigureAwait(false))
                {
                    var counted = new Dictionary<(string Key, string Value), (string? Text, long Records)>();
                    foreach (var hit in page.Hits)
                    {
                        var held = TextsOf(hit, keyField).Where(key => scope.Holds(key) && range.Contains(key, order)).ToList();
                        if (held.Count == 0)
                        {
                            continue;
                        }

                        // A value counted once a record however many of its texts the record holds, under the first of them.
                        var values = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var text in TextsOf(hit, field).Order(StringComparer.Ordinal))
                        {
                            if (DimensionFilters.Filterable(field, text) && ShownAs(text, attribute.Keep) is { } value)
                            {
                                values.TryAdd(value, text);
                            }
                        }

                        foreach (var key in held)
                        {
                            if (values.Count == 0)
                            {
                                if (dimension.Unlabelled is { } none)
                                {
                                    Count(counted, key, none, null);
                                }

                                continue;
                            }

                            foreach (var (value, text) in values)
                            {
                                Count(counted, key, value, text);
                            }
                        }
                    }

                    await stage.AddAttributesAsync(
                        counted.Select(c => (c.Key.Key, new DimensionAttributeState(attribute.Name, c.Key.Value, c.Value.Text, c.Value.Records))).ToList(), token).ConfigureAwait(false);
                    Interlocked.Increment(ref pages);
                }
            }).ConfigureAwait(false);

        return (pages, scope.Ranges.Count);
    }

    /// <summary>Counts one record holding <paramref name="value"/> under <paramref name="key"/>, naming it by the first of the texts it was read as, ordinally.</summary>
    private static void Count(Dictionary<(string Key, string Value), (string? Text, long Records)> counted, string key, string value, string? text)
    {
        if (!counted.TryGetValue((key, value), out var held))
        {
            counted[(key, value)] = (text, 1);
            return;
        }

        var first = held.Text is null || (text is not null && string.CompareOrdinal(text, held.Text) < 0) ? text : held.Text;
        counted[(key, value)] = (first, held.Records + 1);
    }

    /// <summary>
    /// The dimension's keys, in the order the index keeps them, cut into ranges holding about as many records each: enough
    /// of them to keep <paramref name="readers"/> readers busy, none of fewer than <see cref="MinRangeRecords"/> records.
    /// The first range is open below and the last above, so every record holding a key is in one; a dimension of few
    /// records is one range, the whole of it.
    /// </summary>
    internal static IReadOnlyList<DistinctSlice> Ranges(OsduField keyField, IReadOnlyDictionary<string, long> found, IComparer<string> order, int readers)
    {
        ArgumentNullException.ThrowIfNull(keyField);
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(order);
        var total = found.Values.Sum();
        var wanted = (int)Math.Min((long)Math.Max(1, readers) * RangesPerReader, total / MinRangeRecords);
        if (wanted < 2 || found.Count < 2)
        {
            return [DistinctSlice.Whole];
        }

        var keys = found.OrderBy(k => k.Key, order).ToList();
        var share = (double)total / wanted;
        var ranges = new List<DistinctSlice>(wanted);
        string? from = null;
        long before = 0;
        for (var i = 0; i < keys.Count && ranges.Count < wanted - 1; i++)
        {
            // A range ends before the first key that takes the records read so far past its share, when that key can bound a range.
            if (i > 0 && before >= share * (ranges.Count + 1) && Bounds(keyField, keys[i].Key))
            {
                ranges.Add(new DistinctSlice(from, keys[i].Key));
                from = keys[i].Key;
            }

            before += keys[i].Value;
        }

        ranges.Add(new DistinctSlice(from, null));
        return ranges.Count == 1 ? [DistinctSlice.Whole] : ranges;
    }

    /// <summary>
    /// The ranges a dimension's values were read in (<see cref="DistinctCounts.Ranges"/>), which between them hold every value
    /// in the order the index keeps the field, joined into ranges holding about as many records each, as
    /// <see cref="Ranges(OsduField, IReadOnlyDictionary{string, long}, IComparer{string}, int)"/> cuts the keys themselves: a
    /// range ends where one the read covered begins, which a query can carry, since the read split there.
    /// </summary>
    internal static IReadOnlyList<DistinctSlice> Ranges(OsduField keyField, IReadOnlyList<DistinctRange> read, IComparer<string> order, int readers)
    {
        ArgumentNullException.ThrowIfNull(keyField);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(order);
        var total = read.Sum(r => r.Units);
        var wanted = (int)Math.Min((long)Math.Max(1, readers) * RangesPerReader, total / MinRangeRecords);
        if (wanted < 2 || read.Count < 2)
        {
            return [DistinctSlice.Whole];
        }

        var covered = read.OrderBy(r => r.Slice.From is null ? 0 : 1).ThenBy(r => r.Slice.From ?? string.Empty, order).ToList();
        var share = (double)total / wanted;
        var ranges = new List<DistinctSlice>(wanted);
        string? from = null;
        long before = 0;
        for (var i = 0; i < covered.Count && ranges.Count < wanted - 1; i++)
        {
            if (i > 0 && before >= share * (ranges.Count + 1) && covered[i].Slice.From is { } bound && Bounds(keyField, bound))
            {
                ranges.Add(new DistinctSlice(from, bound));
                from = bound;
            }

            before += covered[i].Units;
        }

        ranges.Add(new DistinctSlice(from, null));
        return ranges.Count == 1 ? [DistinctSlice.Whole] : ranges;
    }

    /// <summary>
    /// A text the dimension's records hold at a collected path, as a key shows it: as a label is (a reference by the code its
    /// id ends with, its escapes decoded), trimmed, and cut at the longest attribute value; null when that leaves nothing,
    /// which is no value. An attribute that keeps a key or an id keeps it whole, and is no value when it is longer than a
    /// dimension keeps, since a key cut joins to nothing. A build and the dimension builder's example show it the same way.
    /// </summary>
    internal static string? ShownAs(string text, DimensionValueKeep keep = DimensionValueKeep.Value)
        => DimensionKeeping.Keep(text, keep, DimensionSpec.MaxAttributeValueLength).Value;

    /// <summary>Whether a key can be the end of a range: the query language can carry it as a bound of the field.</summary>
    private static bool Bounds(OsduField keyField, string key)
    {
        try
        {
            _ = OsduQuery.Range(keyField, key, null);
            return true;
        }
        catch (OsduQueryException)
        {
            return false;
        }
    }

    /// <summary>
    /// The texts a hit holds at <paramref name="field"/>, each once, in the text the read keeps a value under: a text longer
    /// than the keyword keeps is no value, as no aggregation or exact match reaches it.
    /// </summary>
    internal static HashSet<string> TextsOf(JsonElement hit, OsduField field)
    {
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in SearchDistinctSource.Units(hit, field))
        {
            foreach (var value in unit.Values)
            {
                var (reading, text) = DimensionValueText.FromScan(field.Index, value);
                if (reading == ValueReading.Value && (field.Index != OsduFieldIndex.Text || text.Length <= OsduQuery.KeywordIgnoreAbove))
                {
                    texts.Add(text);
                }
            }
        }

        return texts;
    }

    /// <summary>
    /// The texts a collected path's read finds, as they come a range at a time: each a value shown (staged with the records
    /// holding it), one no query can carry, or one shown as nothing. The texts shown are held while they are few enough for a
    /// read per value to cover (<see cref="DimensionFilters.MaxOriginalsPerQuery"/>), and the first of those no query can
    /// carry for a note; every other is staged and let go.
    /// </summary>
    private sealed class ShownTexts(DimensionAttributeSpec attribute, OsduField field, DimensionStage stage) : IDistinctSink
    {
        /// <summary>The texts no query can carry that a note names.</summary>
        private const int Named = 5;

        private readonly SortedSet<string> _unfilterable = new(StringComparer.Ordinal);
        private Dictionary<string, string>? _shown = new(StringComparer.Ordinal);
        private bool _taken;

        /// <summary>The texts shown, each with its value, while there are few enough to read per value; null once there are more.</summary>
        public IReadOnlyDictionary<string, string>? Shown => _shown;

        /// <summary>The texts shown.</summary>
        public long Count { get; private set; }

        /// <summary>The texts that are longer than an attribute keeps whole.</summary>
        public long Uncut { get; private set; }

        /// <summary>The texts no query can carry.</summary>
        public long Unfilterable { get; private set; }

        /// <summary>The first texts no query can carry, ordinally, as many as a note names.</summary>
        public IReadOnlyList<string> FirstUnfilterable() => [.. _unfilterable];

        public async ValueTask AddAsync(IReadOnlyList<KeyValuePair<string, long>> values, CancellationToken ct)
        {
            var staged = new List<DimensionCollectedText>(values.Count);
            foreach (var (text, records) in values)
            {
                if (!DimensionFilters.Filterable(field, text))
                {
                    Unfilterable++;
                    _unfilterable.Add(text);
                    if (_unfilterable.Count > Named)
                    {
                        _unfilterable.Remove(_unfilterable.Max!);
                    }

                    continue;
                }

                if (ShownAs(text, attribute.Keep) is { } value)
                {
                    staged.Add(new DimensionCollectedText(attribute.Name, text, value, records));
                    Count++;
                    if (_shown is not null)
                    {
                        _shown[text] = value;
                        if (_shown.Count > DimensionFilters.MaxOriginalsPerQuery)
                        {
                            _shown = null;
                        }
                    }
                }
                else if (DimensionKeeping.Keep(text, attribute.Keep, DimensionSpec.MaxAttributeValueLength).TooLong)
                {
                    Uncut++;
                }
            }

            await stage.AddTextsAsync(staged, ct).ConfigureAwait(false);
            _taken = true;
        }

        /// <summary>
        /// Starts over, before any text was taken: a read starts over when its first answer names no key for the groups (a plain
        /// number), so every text is read by scanning. One that had taken texts would stage them twice, so it is refused.
        /// </summary>
        public ValueTask ResetAsync(CancellationToken ct)
            => _taken
                ? throw new DeliveryException(
                    $"The read of attribute {attribute.Name}'s values ({attribute.Collect}) started over after it had taken some, which would count them twice; build the dimension again.")
                : ValueTask.CompletedTask;
    }

    /// <summary>
    /// The keys a read of the dimension's field over the records holding one value of a collected attribute finds, each
    /// staged as holding the value with how many records hold it; a key outside the scope is passed over.
    /// </summary>
    private sealed class KeysHolding(DimensionScope scope, string name, string value, string? text, DimensionStage stage) : IDistinctSink
    {
        private bool _taken;

        public async ValueTask AddAsync(IReadOnlyList<KeyValuePair<string, long>> values, CancellationToken ct)
        {
            await stage.AddAttributesAsync(
                values.Where(v => scope.Holds(v.Key)).Select(v => (v.Key, new DimensionAttributeState(name, value, text, v.Value))).ToList(), ct).ConfigureAwait(false);
            _taken = true;
        }

        /// <summary>Starts over before any key was taken, as a read of a plain number's values does; after, it would count them twice, so it is refused.</summary>
        public ValueTask ResetAsync(CancellationToken ct)
            => _taken
                ? throw new DeliveryException(
                    $"The read of the keys holding '{value}' of attribute {name} started over after it had taken some, which would count them twice; build the dimension again.")
                : ValueTask.CompletedTask;
    }
}
