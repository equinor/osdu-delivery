using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// What a pass over a dimension's records read of them: each record identified, by the SHA-256 of its unique key
/// (<see cref="DimensionRecordReader.RecordHash"/>); each key each such record holds; the records read; those holding no
/// single value at a key column, which cannot be told apart and are left out; and the pages read.
/// </summary>
internal sealed record RecordRead(
    IReadOnlyList<byte[]> Records, IReadOnlyList<(byte[] Record, string Original)> Held, long Read, long Unidentified, int Pages, int Ranges);

/// <summary>What a pass over a dimension's records staged: the records read, those left out, the keys they hold, and the pages and ranges read.</summary>
internal sealed record RecordsStaged(long Read, long Unidentified, long Held, int Pages, int Ranges);

/// <summary>
/// Reads a dimension's records through the search cursor, each record's unique key (the flow's
/// <c>incremental.keyColumns</c>) and the keys it holds together, for its incremental loads: a full load reads every record,
/// cut into ranges of the dimension's keys read side by side as a collected attribute's pass is
/// (<see cref="DimensionScope.Ranges"/>), and stages the keys each holds a page at a time; an incremental load reads the
/// records that changed in its window, each key each holds, and looks up what they held before by their unique keys. A
/// record holding keys of two ranges is read in both and keeps the keys of each range from each, so every pair is read once.
/// </summary>
internal sealed class DimensionRecordReader(OsduSearch search, ILogger log, int concurrency)
{
    /// <summary>The most keys a full load keeps by record between all a dimension's records: past it, the dimension is loaded in full only.</summary>
    public const long MaxHeld = 20_000_000;

    /// <summary>What separates the values of a unique key of several columns before it is hashed: a character no id holds.</summary>
    private const char Separator = '\u001F';

    /// <summary>The SHA-256 a record is kept by: of its key columns' values, in order, joined.</summary>
    public static byte[] RecordHash(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return SqlServerDimensionStore.HashOf(string.Join(Separator, values));
    }

    /// <summary>
    /// Every record of every scope, found by the scope's query and cut into its ranges, with the keys it holds among the
    /// scope's, staged in <paramref name="stage"/> a page at a time: what a full load keeps by record.
    /// </summary>
    /// <exception cref="DeliveryException">The records hold more keys between them than a full load keeps by record.</exception>
    public async Task<RecordsStaged> ReadAsync(
        DimensionSpec dimension, IReadOnlyList<DimensionScope> scopes, OsduField keyField, IReadOnlyList<OsduField> keyColumns, DimensionStage stage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(keyField);
        ArgumentNullException.ThrowIfNull(stage);
        var order = DimensionValueText.Order(keyField.Index);
        var work = scopes
            .SelectMany(scope => scope.Ranges.Select(range => new Pass(Own(scope.Query), range, key => scope.Holds(key) && range.Contains(key, order))))
            .ToList();
        long read = 0;
        long unidentified = 0;
        var pages = 0;
        await PassAsync(dimension, work, keyField, keyColumns, async (page, token) =>
        {
            Interlocked.Increment(ref pages);
            Interlocked.Add(ref read, page.Read);
            Interlocked.Add(ref unidentified, page.Unidentified);

            // A full load replaces every row the dimension keeps by record, so the records it read are not staged: only the
            // keys they hold are.
            await stage.AddRecordsAsync([], page.Held, token).ConfigureAwait(false);
            if (stage.Held > MaxHeld)
            {
                throw TooMany(dimension);
            }
        }, ct).ConfigureAwait(false);

        log.LogInformation(
            "dimension {Dimension}: {Records} record(s) read by their unique key with {Held} key(s) between them in {Pages} page(s) over {Ranges} range(s){Unidentified}",
            dimension.Name, read, stage.Held, pages, work.Count,
            unidentified > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {unidentified} hold no single value at a key column and are left out") : string.Empty);
        return new RecordsStaged(read, unidentified, stage.Held, pages, work.Count);
    }

    /// <summary>Every record <paramref name="query"/> finds, with every key it holds: the records an incremental load found changed.</summary>
    /// <exception cref="DeliveryException">The records hold more keys between them than a load keeps by record.</exception>
    public async Task<RecordRead> ReadAllAsync(DimensionSpec dimension, string query, OsduField keyField, IReadOnlyList<OsduField> keyColumns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(keyField);
        var records = new List<byte[]>();
        var held = new List<(byte[] Record, string Original)>();
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
        long read = 0;
        long unidentified = 0;
        var pages = 0;
        var gate = new Lock();
        await PassAsync(dimension, [new Pass(Own(query), DistinctSlice.Whole, _ => true)], keyField, keyColumns, (page, _) =>
        {
            // A page is kept in one go, under the lock the passes share; a key is held once however many records hold it.
            lock (gate)
            {
                pages++;
                read += page.Read;
                unidentified += page.Unidentified;
                records.AddRange(page.Records);
                foreach (var (hash, key) in page.Held)
                {
                    if (!canonical.TryGetValue(key, out var kept))
                    {
                        canonical[key] = kept = key;
                    }

                    held.Add((hash, kept));
                }

                if (held.Count > MaxHeld)
                {
                    throw TooMany(dimension);
                }
            }

            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);

        log.LogInformation(
            "dimension {Dimension}: {Records} record(s) read by their unique key with {Held} key(s) between them in {Pages} page(s) over 1 range(s){Unidentified}",
            dimension.Name, read, held.Count, pages,
            unidentified > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {unidentified} hold no single value at a key column and are left out") : string.Empty);
        return new RecordRead(records, held, read, unidentified, pages, 1);
    }

    /// <summary>What one page of a pass read: its records identified, the keys each held, and the records read and left out.</summary>
    private sealed record PageRead(List<byte[]> Records, List<(byte[] Record, string Original)> Held, long Read, long Unidentified);

    /// <summary>Reads every pass's records through the search cursor, the passes side by side, handing each page as it is read to <paramref name="take"/>.</summary>
    private async Task PassAsync(
        DimensionSpec dimension, IReadOnlyList<Pass> work, OsduField keyField, IReadOnlyList<OsduField> keyColumns, Func<PageRead, CancellationToken, ValueTask> take,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keyColumns);
        if (keyColumns.Count == 0)
        {
            throw new ArgumentException("A record is identified by at least one key column.", nameof(keyColumns));
        }

        var returned = keyColumns.Select(c => c.Path).Append(keyField.Path).Distinct(StringComparer.Ordinal).ToList();
        await Parallel.ForEachAsync(
            work,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct },
            async (pass, token) =>
            {
                var request = new OsduSearchQuery
                {
                    Kind = dimension.Kind,
                    Query = pass.Range.IsWhole ? pass.Query : OsduSearch.Within(pass.Query, OsduQuery.Range(keyField, pass.Range.From, pass.Range.To).Text),
                    ReturnedFields = returned,
                };

                // The reader hands each record out once and every record of the range, or fails the load.
                await foreach (var page in search.PagesAsync(request, OsduSearch.MaxPage, token).ConfigureAwait(false))
                {
                    var pageRecords = new List<byte[]>(page.Hits.Count);
                    var pageHeld = new List<(byte[] Record, string Original)>();
                    var pageUnidentified = 0;
                    foreach (var hit in page.Hits)
                    {
                        if (Identity(hit, keyColumns) is not { } identity)
                        {
                            pageUnidentified++;
                            continue;
                        }

                        var hash = RecordHash(identity);
                        pageRecords.Add(hash);
                        foreach (var key in DimensionCollector.TextsOf(hit, keyField).Where(pass.Keeps))
                        {
                            pageHeld.Add((hash, key));
                        }
                    }

                    await take(new PageRead(pageRecords, pageHeld, page.Hits.Count, pageUnidentified), token).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
    }

    private static DeliveryException TooMany(DimensionSpec dimension)
        => new(string.Create(CultureInfo.InvariantCulture,
            $"The records of dimension {dimension.Name} hold more than {MaxHeld:N0} keys between them, more than a load keeps by record for incremental loads. Narrow its query, split it over dimensions with narrower queries, or take out the flow's incremental block to load it in full only."));

    /// <summary>The values of a record's key columns, in order; null when one holds no single value, so the record cannot be told apart.</summary>
    private static IReadOnlyList<string>? Identity(JsonElement hit, IReadOnlyList<OsduField> keyColumns)
    {
        var values = new List<string>(keyColumns.Count);
        foreach (var column in keyColumns)
        {
            var held = DimensionCollector.TextsOf(hit, column);
            if (held.Count != 1)
            {
                return null;
            }

            values.Add(held.First());
        }

        return values;
    }

    /// <summary>A query as a pass asks it: none for every record.</summary>
    private static string? Own(string? query) => string.IsNullOrWhiteSpace(query) || query.Trim() == "*" ? null : query.Trim();

    /// <summary>One cursor of a pass: its query, the range of keys it reads, and which keys it keeps of a record.</summary>
    private sealed record Pass(string? Query, DistinctSlice Range, Func<string, bool> Keeps);
}
