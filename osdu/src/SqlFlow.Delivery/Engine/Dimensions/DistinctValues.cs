using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// A range of a field's values, in the order the index keeps the field: <see cref="From"/> included, <see cref="To"/> left
/// out, a null end open. Two slices that meet at a value hold every value once between them.
/// </summary>
public sealed record DistinctSlice(string? From, string? To)
{
    /// <summary>Every value of the field.</summary>
    public static DistinctSlice Whole { get; } = new(null, null);

    public bool IsWhole => From is null && To is null;

    /// <summary>Whether <paramref name="value"/> falls inside the slice, by <paramref name="order"/>.</summary>
    public bool Contains(string value, IComparer<string> order)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(order);
        return (From is null || order.Compare(value, From) >= 0) && (To is null || order.Compare(value, To) < 0);
    }

    public override string ToString() => IsWhole ? "every value" : $"[{From ?? "*"}, {To ?? "*"})";
}

/// <summary>
/// One thing a dimension counts: a record, or for a property of a nested array one object of the array, which is what the
/// index's own counts are of there. Its values are counted once each, however often it holds them.
/// </summary>
public sealed record DistinctUnit(IReadOnlyList<ScannedValue> Values);

/// <summary>A count a build takes beside the values, to say how complete what it read is.</summary>
public enum DistinctCheck
{
    /// <summary>The records the query matches.</summary>
    Records,

    /// <summary>
    /// The records the exact field holds a value for: the keyword sub-field of text, which holds a null as the text null and
    /// no value longer than 256 characters, and the field itself otherwise. These are the records the groups account for.
    /// </summary>
    WithValue,

    /// <summary>
    /// The records that hold a text value at the path but none in its keyword sub-field: every value they hold there is
    /// longer than the 256 characters the sub-field keeps, so no exact match, and no aggregation, can reach it.
    /// </summary>
    TooLong,
}

/// <summary>
/// What a dimension reads its values from: the groups of its field over the records a slice matches, the records a slice
/// matches with the values each holds, and the counts that say how complete a read is. The search service implements it
/// (<see cref="SearchDistinctSource"/>); so does an in-memory index in the tests, which is how the paging is proven.
/// </summary>
public interface IDistinctValueSource
{
    /// <summary>The field read, as the index stores it.</summary>
    OsduField Field { get; }

    /// <summary>The groups of the field over the records the slice matches, and how many records it matches.</summary>
    Task<(long Total, IReadOnlyList<OsduSearchBucket> Buckets)> AggregateAsync(DistinctSlice slice, CancellationToken ct);

    /// <summary>Every unit the slice's records hold, a page at a time, each with the values it holds at the path.</summary>
    IAsyncEnumerable<IReadOnlyList<DistinctUnit>> ScanAsync(DistinctSlice slice, CancellationToken ct);

    /// <summary>The count <paramref name="check"/> asks for, or null when this field cannot be asked it.</summary>
    Task<long?> CountAsync(DistinctCheck check, CancellationToken ct);
}

/// <summary>How a read pages, and how far it may go.</summary>
/// <param name="AggregationSize">
/// The most groups one aggregation returns: the platform's <c>aggregationSize</c> (1000 unless its operators changed it).
/// A slice answered with fewer is complete, so a value set higher than the platform's loses values, and one set lower only
/// asks more often.
/// </param>
/// <param name="MaxValues">The most distinct values the read keeps before it stops, so memory stays bounded.</param>
/// <param name="Repeats">
/// Whether a record can hold the field more than once (an array, a nested array): then the counts of values need not add
/// up to the records holding a value, and the read does not say they disagree.
/// </param>
/// <param name="Checks">
/// Whether the read counts the records it covers and those holding a value, to say how complete it is; a read that is one of
/// many over parts of the same records (a collected attribute's values, one read each) leaves them to the read of the whole.
/// </param>
/// <param name="Concurrency">
/// The most ranges asked of the service at once. A field of many values is read in many ranges, each an aggregation the
/// service computes on its own, so asking several at a time reads it that many times sooner; one reads them in turn.
/// </param>
public sealed record DistinctReadOptions(int AggregationSize, long MaxValues, bool Repeats, bool Checks = true, int Concurrency = 1)
{
    /// <summary>The fewest groups an aggregation may be taken to return; a smaller setting could never page a real field.</summary>
    public const int MinAggregationSize = 10;

    /// <summary>The most groups any platform is taken to return; Elasticsearch's own limit on buckets is far above.</summary>
    public const int MaxAggregationSize = 10_000;
}

/// <summary>
/// Where a read takes the values it finds (<see cref="DistinctValues.ReadAsync(IDistinctValueSource, DistinctReadOptions, Microsoft.Extensions.Logging.ILogger, IDistinctSink, CancellationToken)"/>):
/// a range's values together, each value in its canonical text with how many units hold it. Two ranges never hold one value,
/// so each value is taken once, however many ranges a read answers.
/// </summary>
public interface IDistinctSink
{
    /// <summary>Takes the values one range of the read holds, in the order the read found them.</summary>
    ValueTask AddAsync(IReadOnlyList<KeyValuePair<string, long>> values, CancellationToken ct);

    /// <summary>Forgets every value taken: the read starts over, every value read again by scanning.</summary>
    ValueTask ResetAsync(CancellationToken ct);
}

/// <summary>A range of a field's values a read covered whole, with how many units hold a value inside it.</summary>
public sealed record DistinctRange(DistinctSlice Slice, long Units);

/// <summary>What reading a dimension's values counted, with how it read them and how complete that is.</summary>
public class DistinctCounts
{
    /// <summary>The distinct values read.</summary>
    public long Keys { get; init; }

    /// <summary>The units counted under a value, a unit holding several values counted under each.</summary>
    public long Units { get; init; }

    /// <summary>
    /// The ranges the values were read in, each covered whole, in the order the index keeps the field, between them every
    /// value; one whole range when every value was read by scanning. Passes over the records holding the values are cut
    /// along them.
    /// </summary>
    public IReadOnlyList<DistinctRange> Ranges { get; init; } = [];

    /// <summary>Units whose value is null (text only: the index keeps a null text as the text null).</summary>
    public long Nulls { get; init; }

    /// <summary>Units holding a value that is not of the field's type, or an object where a value was expected.</summary>
    public long Unreadable { get; init; }

    /// <summary>The records the query matches; null when that could not be counted.</summary>
    public long? Records { get; init; }

    /// <summary>
    /// The records the exact field holds a value for (<see cref="DistinctCheck.WithValue"/>); null when that could not be
    /// counted, as inside a nested array.
    /// </summary>
    public long? WithValue { get; init; }

    /// <summary>The records whose every text value is longer than the keyword sub-field keeps; null when not counted.</summary>
    public long? TooLong { get; init; }

    /// <summary>Aggregations asked.</summary>
    public int Aggregations { get; init; }

    /// <summary>Slices an aggregation answered whole.</summary>
    public int Slices { get; init; }

    /// <summary>Slices split because their aggregation was cut off.</summary>
    public int Splits { get; init; }

    /// <summary>Slices read by scanning, because they could not be split.</summary>
    public int ScannedSlices { get; init; }

    /// <summary>Cursor pages read by scanning.</summary>
    public int ScanPages { get; init; }

    /// <summary>Units read by scanning.</summary>
    public long ScannedUnits { get; init; }

    /// <summary>True when every value was read by scanning, because the aggregation named no keys.</summary>
    public bool ScannedWhole { get; init; }

    /// <summary>What the read has to say about how it read and how complete that is, a line each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Every request the read made of the search service.</summary>
    public int Requests => Aggregations + ScanPages;
}

/// <summary>What reading a dimension's values found, held in memory: every value with how many units hold it, and the counts.</summary>
public sealed class DistinctRead : DistinctCounts
{
    /// <summary>Every distinct value in its canonical text, with how many units hold it.</summary>
    public required IReadOnlyDictionary<string, long> Values { get; init; }

    /// <summary>The counts of a read whose values <paramref name="values"/> holds.</summary>
    public static DistinctRead Of(DistinctCounts counts, IReadOnlyDictionary<string, long> values)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(values);
        return new DistinctRead
        {
            Values = values,
            Keys = counts.Keys,
            Units = counts.Units,
            Ranges = counts.Ranges,
            Nulls = counts.Nulls,
            Unreadable = counts.Unreadable,
            Records = counts.Records,
            WithValue = counts.WithValue,
            TooLong = counts.TooLong,
            Aggregations = counts.Aggregations,
            Slices = counts.Slices,
            Splits = counts.Splits,
            ScannedSlices = counts.ScannedSlices,
            ScanPages = counts.ScanPages,
            ScannedUnits = counts.ScannedUnits,
            ScannedWhole = counts.ScannedWhole,
            Notes = counts.Notes,
        };
    }
}

/// <summary>A dimension that holds more distinct values than it may keep.</summary>
public sealed class DimensionTooLargeException : DeliveryException
{
    public DimensionTooLargeException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Reads every distinct value of one field of the records a query matches, however many there are, from the search
/// service's aggregation, which returns at most <see cref="DistinctReadOptions.AggregationSize"/> groups and has no paging
/// of its own (<c>AggregationParserUtil</c>, <c>SearchConfigurationProperties.aggregationSize</c>).
/// </summary>
/// <remarks>
/// <para>
/// The read pages by value ranges. A slice whose aggregation returns fewer groups than the most it can is complete, and its
/// counts are exact: every shard returned every term it holds. One that returns that many was cut off, so none of its counts
/// is used: it is split at the middle of the keys it did return, in the index's order, and each half is asked again with
/// the range added to the query. A slice keeps only the keys inside its own range, because a record holding several
/// values brings in the groups of other slices too. Each half holds fewer distinct values than the slice, since the key it
/// splits at and the first key both exist and fall one on each side, so the read always ends.
/// </para>
/// <para>
/// A cut-off slice is read through the search cursor instead when that is cheaper or the only way: when its records fit
/// one page, when most of the groups it brought back belong to other slices (its records hold many values each), and when
/// it cannot be split (one key left inside it, or no key the service could carry as a bound). The whole field is scanned
/// when the aggregation names no keys (a plain number's groups). A scan counts the way the aggregation does, each value
/// once per unit that holds it.
/// </para>
/// </remarks>
public static class DistinctValues
{
    /// <summary>Requests between progress lines in the run's log, so a long read shows it is moving without a line per request.</summary>
    private const int ProgressEvery = 250;

    /// <summary>The most cursor pages a slice whose groups are mostly other slices' is scanned in rather than split.</summary>
    private const int PollutedScanPages = 10;

    /// <summary>Reads every distinct value of <paramref name="source"/>'s field, and holds them in memory.</summary>
    /// <exception cref="DimensionTooLargeException">The field holds more distinct values than the options allow.</exception>
    public static async Task<DistinctRead> ReadAsync(IDistinctValueSource source, DistinctReadOptions options, ILogger log, CancellationToken ct)
    {
        var held = new HeldValues();
        var counts = await ReadAsync(source, options, log, held, ct).ConfigureAwait(false);
        return DistinctRead.Of(counts, held.Values);
    }

    /// <summary>
    /// Reads every distinct value of <paramref name="source"/>'s field into <paramref name="sink"/>, a range's values at a
    /// time as each range is read whole: what a read holds is the range it is reading, however many values the field holds.
    /// </summary>
    /// <exception cref="DimensionTooLargeException">The field holds more distinct values than the options allow.</exception>
    public static async Task<DistinctCounts> ReadAsync(IDistinctValueSource source, DistinctReadOptions options, ILogger log, IDistinctSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.AggregationSize, DistinctReadOptions.MinAggregationSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.AggregationSize, DistinctReadOptions.MaxAggregationSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxValues, 1);

        var field = source.Field;
        var order = DimensionValueText.Order(field.Index);
        var tally = new Tally(field, options.MaxValues, sink);
        var notes = new List<string>();
        // The ranges still to read, the widest first: the halves of a split wait behind the ranges asked before them, so the
        // read fans out to as many ranges as it may ask at once as soon as there are that many.
        var pending = new Queue<DistinctSlice>();
        pending.Enqueue(DistinctSlice.Whole);

        // Every split leaves fewer distinct values on each side, so paging ends by itself; this bound only stops an index
        // that keeps gaining values while it is read from keeping a build going without end.
        var ceiling = (2 * options.MaxValues) + 16;
        var aggregations = 0;
        var slices = 0;
        var splits = 0;
        var scannedSlices = 0;
        var scannedWhole = false;

        // The ranges still to read are asked several at a time. What a range's answer leads to (its keys counted, a split,
        // a scan) is settled here, one answer at a time, so the counts are kept by one thread whatever is in flight.
        var readers = Math.Max(1, options.Concurrency);
        var asked = new List<Task<(DistinctSlice Slice, long Matched, IReadOnlyList<OsduSearchBucket> Buckets)>>(readers);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (pending.Count > 0 || asked.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                while (pending.Count > 0 && asked.Count < readers)
                {
                    if (aggregations >= ceiling)
                    {
                        throw new DeliveryException(
                            string.Create(CultureInfo.InvariantCulture, $"Reading the values of {field.Path} asked {aggregations} aggregations without covering every range; the index is changing faster than it can be read. Build the dimension again when it is quieter."));
                    }

                    asked.Add(AskAsync(source, pending.Dequeue(), stop.Token));
                    aggregations++;
                    if (aggregations % ProgressEvery == 0)
                    {
                        log.LogInformation(
                            "values of {Path}: {Aggregations} aggregation(s), {Values} value(s) so far, {Pending} range(s) still to read",
                            field.Path, aggregations, tally.Count, pending.Count);
                    }
                }

                var answered = await Task.WhenAny(asked).ConfigureAwait(false);
                asked.Remove(answered);
                var (slice, matched, buckets) = await answered.ConfigureAwait(false);
                if (buckets.Any(b => b.Key is null))
                {
                    // The service renders a number's key from key_as_string, which a plain number has none of: the groups
                    // cannot say which value they are, so every value is read from the records themselves. The ranges
                    // already asked are let finish, and what they answer goes with what was counted so far.
                    notes.Add("The search named no key for the groups of this field, which it does for a plain number, so every value was read by scanning the records.");
                    pending.Clear();
                    await Task.WhenAll(asked).ConfigureAwait(false);
                    asked.Clear();
                    await tally.ResetAsync(ct).ConfigureAwait(false);
                    await ScanAsync(source, DistinctSlice.Whole, order, tally, ct).ConfigureAwait(false);
                    scannedWhole = true;
                    break;
                }

                var inside = buckets.Where(b => slice.Contains(Canonical(field, b.Key!), order)).ToList();
                if (buckets.Count < options.AggregationSize)
                {
                    await tally.AddGroupsAsync(slice, inside, ct).ConfigureAwait(false);
                    slices++;
                    continue;
                }

                // A cut-off slice is scanned rather than split when its records fit one cursor page, which one request reads
                // where a split asks two aggregations at least; and when most of its groups belong to other slices, its
                // records hold many values each, so no split would bring it under the limit before it narrowed to single
                // values, and a scan of a few pages reads what that would have read one value at a time.
                if (matched <= OsduSearch.MaxPage || (inside.Count * 2 < buckets.Count && matched <= PollutedScanPages * OsduSearch.MaxPage))
                {
                    scannedSlices++;
                    await ScanAsync(source, slice, order, tally, ct).ConfigureAwait(false);
                    continue;
                }

                if (SplitPoint(field, inside, order) is { } split)
                {
                    splits++;
                    pending.Enqueue(new DistinctSlice(slice.From, split));
                    pending.Enqueue(new DistinctSlice(split, slice.To));
                    continue;
                }

                scannedSlices++;
                await ScanAsync(source, slice, order, tally, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // The read failed: the ranges still asked are told to stop and waited for, so none outlives the read. What
            // they come to is not reported; the failure that ended the read is.
            await stop.CancelAsync().ConfigureAwait(false);
            await ((Task)Task.WhenAll(asked)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        var records = options.Checks ? await CheckAsync(source, DistinctCheck.Records, notes, ct).ConfigureAwait(false) : null;
        var withValue = options.Checks ? await CheckAsync(source, DistinctCheck.WithValue, notes, ct).ConfigureAwait(false) : null;
        var tooLong = options.Checks && field.Index == OsduFieldIndex.Text && field.NestedPath is null
            ? await CheckAsync(source, DistinctCheck.TooLong, notes, ct).ConfigureAwait(false)
            : null;

        if (tooLong is > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"{tooLong} record(s) hold only values longer than the {OsduQuery.KeywordIgnoreAbove} characters the index keeps for exact matching; search cannot match them exactly, so they are no member's value."));
        }

        if (!options.Repeats && withValue is { } holding && tally.Unreadable == 0)
        {
            // A field a record holds once: every record the exact field holds a value for (a null text included, which the
            // index keeps as the text null) is in exactly one group. When the sums differ, the index changed while it was read.
            var counted = tally.Total + tally.Nulls;
            if (counted != holding)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"The values read account for {counted} record(s), and {holding} hold a value at {field.Path}: the index changed while it was read, so counts may be off by the difference; the next build reads it again."));
            }
        }

        var read = new DistinctCounts
        {
            Keys = tally.Count,
            Units = tally.Total,
            Ranges = [.. tally.Ranges],
            Nulls = tally.Nulls,
            Unreadable = tally.Unreadable,
            Records = records,
            WithValue = withValue,
            TooLong = tooLong,
            Aggregations = aggregations,
            Slices = slices,
            Splits = splits,
            ScannedSlices = scannedSlices,
            ScanPages = tally.ScanPages,
            ScannedUnits = tally.ScannedUnits,
            ScannedWhole = scannedWhole,
            Notes = notes,
        };
        log.LogInformation(
            "values of {Path}: {Values} distinct value(s) from {Aggregations} aggregation(s) over {Slices} range(s), {Splits} split(s), {Scanned} range(s) scanned in {Pages} page(s)",
            field.Path, read.Keys, aggregations, slices, splits, scannedWhole ? "every" : scannedSlices.ToString(CultureInfo.InvariantCulture), read.ScanPages);
        return read;
    }

    /// <summary>One range asked of the service, answered with the range it was asked of.</summary>
    private static async Task<(DistinctSlice Slice, long Matched, IReadOnlyList<OsduSearchBucket> Buckets)> AskAsync(
        IDistinctValueSource source, DistinctSlice slice, CancellationToken ct)
    {
        var (matched, buckets) = await source.AggregateAsync(slice, ct).ConfigureAwait(false);
        return (slice, matched, buckets);
    }

    /// <summary>
    /// Where a cut-off slice is split: the key nearest the middle of those inside it, in the index's order, that the service
    /// can carry as a range bound, and that is not the first key, so both halves hold a value. Null when none will do.
    /// </summary>
    private static string? SplitPoint(OsduField field, IReadOnlyList<OsduSearchBucket> inside, IComparer<string> order)
    {
        var keys = inside
            .Select(b => Canonical(field, b.Key!))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, order)
            .ToList();
        if (keys.Count < 2)
        {
            return null;
        }

        var middle = keys.Count / 2;
        for (var offset = 0; offset < keys.Count; offset++)
        {
            foreach (var at in offset == 0 ? new[] { middle } : new[] { middle + offset, middle - offset })
            {
                if (at < 1 || at >= keys.Count || order.Compare(keys[at], keys[0]) <= 0)
                {
                    continue;
                }

                try
                {
                    _ = OsduQuery.Range(field, keys[at], null);
                    return keys[at];
                }
                catch (OsduQueryException)
                {
                    // A key the service would misread as a bound (the nested marker, or inside a nested array a value its
                    // rewriting alters) is passed over for the next nearest.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the slice's values from its records through the search cursor, each value counted once a unit, and takes them
    /// when the slice is read whole. A slice scanned is small (a few pages, or one value), but every value of a field read by
    /// scanning is one slice, held until it is read; the most values a dimension keeps bound it.
    /// </summary>
    private static async Task ScanAsync(IDistinctValueSource source, DistinctSlice slice, IComparer<string> order, Tally tally, CancellationToken ct)
    {
        var field = source.Field;
        var nullInside = slice.Contains(OsduQuery.KeywordNullValue, order);
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var page in source.ScanAsync(slice, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            tally.ScanPages++;
            foreach (var unit in page)
            {
                tally.ScannedUnits++;
                var held = new HashSet<string>(StringComparer.Ordinal);
                var isNull = false;
                var unreadable = false;
                foreach (var value in unit.Values)
                {
                    var (reading, text) = DimensionValueText.FromScan(field.Index, value);
                    switch (reading)
                    {
                        case ValueReading.Value:
                            // A text value longer than the keyword keeps is in no range and no group; the build counts the
                            // records holding only such values apart.
                            if ((field.Index != OsduFieldIndex.Text || text.Length <= OsduQuery.KeywordIgnoreAbove) && slice.Contains(text, order))
                            {
                                held.Add(text);
                            }

                            break;
                        case ValueReading.Null:
                            isNull |= nullInside;
                            break;
                        default:
                            unreadable = true;
                            break;
                    }
                }

                foreach (var text in held)
                {
                    if (values.TryGetValue(text, out var count))
                    {
                        values[text] = count + 1;
                    }
                    else
                    {
                        tally.Room(values.Count + 1);
                        values[text] = 1;
                    }
                }

                if (isNull)
                {
                    tally.Nulls++;
                }

                // A unit is scanned in every slice one of its values falls in, so what cannot be placed in any is counted
                // only by a scan of every value, where each unit is read once.
                if (unreadable && slice.IsWhole)
                {
                    tally.Unreadable++;
                }
            }
        }

        await tally.TakeAsync(slice, values, ct).ConfigureAwait(false);
    }

    private static async Task<long?> CheckAsync(IDistinctValueSource source, DistinctCheck check, List<string> notes, CancellationToken ct)
    {
        try
        {
            return await source.CountAsync(check, ct).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            // The values are read; a count that says how complete they are is worth having, not worth failing for.
            notes.Add($"Could not count {Describe(check)}: {SecretHygiene.RedactedMessage(ex)}");
            return null;
        }
    }

    private static string Describe(DistinctCheck check) => check switch
    {
        DistinctCheck.Records => "the records the query matches",
        DistinctCheck.WithValue => "the records holding a value",
        _ => "the records holding only values too long to match exactly",
    };

    /// <summary>A key in the text the read keeps it under; a key that is no value of the field's type stays as it is.</summary>
    private static string Canonical(OsduField field, string key) => DimensionValueText.FromKey(field.Index, key).Text;

    /// <summary>
    /// What a read has counted so far, within the most values it may keep: how many values it took, the units under them,
    /// and the ranges it took them in. A range's values go to the sink together once it is read whole; no value is held
    /// after.
    /// </summary>
    private sealed class Tally(OsduField field, long maxValues, IDistinctSink sink)
    {
        private readonly List<DistinctRange> _ranges = [];

        /// <summary>The values taken.</summary>
        public long Count { get; private set; }

        /// <summary>The units counted under a value, a unit holding several values counted under each.</summary>
        public long Total { get; private set; }

        public long Nulls { get; set; }

        public long Unreadable { get; set; }

        public int ScanPages { get; set; }

        public long ScannedUnits { get; set; }

        public IReadOnlyList<DistinctRange> Ranges => _ranges;

        /// <summary>
        /// Takes the groups of a slice an aggregation answered whole: each a value (several keys of one value counted together),
        /// the text a null is kept as, or a key that is no value of the type.
        /// </summary>
        public ValueTask AddGroupsAsync(DistinctSlice slice, IEnumerable<OsduSearchBucket> groups, CancellationToken ct)
        {
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var group in groups)
            {
                var (reading, text) = DimensionValueText.FromKey(field.Index, group.Key!);
                switch (reading)
                {
                    case ValueReading.Value:
                        values[text] = values.GetValueOrDefault(text) + group.Count;
                        break;
                    case ValueReading.Null:
                        Nulls += group.Count;
                        break;
                    default:
                        Unreadable += group.Count;
                        break;
                }
            }

            return TakeAsync(slice, values, ct);
        }

        /// <summary>Takes the values of a slice read whole, in the order they were found.</summary>
        public ValueTask TakeAsync(DistinctSlice slice, Dictionary<string, long> values, CancellationToken ct)
        {
            Room(values.Count);
            var units = values.Values.Sum();
            Count += values.Count;
            Total += units;
            _ranges.Add(new DistinctRange(slice, units));
            return values.Count == 0 ? ValueTask.CompletedTask : sink.AddAsync([.. values], ct);
        }

        /// <summary>Refuses a read past the most values the dimension keeps, which <paramref name="more"/> values not taken yet would take it.</summary>
        public void Room(long more)
        {
            if (Count + more > maxValues)
            {
                throw new DimensionTooLargeException(
                    string.Create(CultureInfo.InvariantCulture,
                        $"{field.Path} holds more than {maxValues} distinct values, the most this dimension keeps (maxValues). Narrow the dimension's query, or raise maxValues if the partition really holds that many."));
            }
        }

        /// <summary>Forgets everything counted and taken: the read starts over.</summary>
        public async ValueTask ResetAsync(CancellationToken ct)
        {
            (Count, Total, Nulls, Unreadable, ScanPages, ScannedUnits) = (0, 0, 0, 0, 0, 0);
            _ranges.Clear();
            await sink.ResetAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>The values of a read held in memory, in the order they were taken.</summary>
    private sealed class HeldValues : IDistinctSink
    {
        private readonly Dictionary<string, long> _values = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, long> Values => _values;

        public ValueTask AddAsync(IReadOnlyList<KeyValuePair<string, long>> values, CancellationToken ct)
        {
            foreach (var (value, count) in values)
            {
                _values[value] = _values.GetValueOrDefault(value) + count;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask ResetAsync(CancellationToken ct)
        {
            _values.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
