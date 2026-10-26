using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Data;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>A key a build staged, with its place in the order the build read its keys and how many records hold it.</summary>
public sealed record StagedKey(long Seq, string Key, long Count);

/// <summary>
/// One object of a nested array a key's records hold, as a build read it: the record holding it, its place among that
/// record's objects, and each of its fields that holds a value, by the field's name, as the field keeps it.
/// </summary>
public sealed record StagedElement(string Key, string Record, int Ordinal, IReadOnlyList<KeyValuePair<string, string>> Values);

/// <summary>A key of a value a build lays out: the value, the key, how many records hold it, and whether a query can carry it.</summary>
public sealed record StagedMemberKey(string Value, string Original, long Count, bool Filterable);

/// <summary>A value whose records a build counts with one search of its filter.</summary>
public sealed record StagedMemberCount(long Seq, string Filter);

/// <summary>
/// What a running dimension build has read, staged in the module's database as it reads it (osdu/docs/reference/flow/dimension-build.md,
/// What a build does): the keys, then each key as cleaned with its label and attributes, the values laid out from them, the
/// texts collected, the objects of a nested array, and the records the keys were read through or held by. Rows are kept in
/// memory a batch at a time and copied to the staging tables as each batch fills, so what a build holds is bounded however
/// large its dimension is; a build that fills a batch while the one before it is still being copied waits for it. The
/// build's write (<see cref="ILedger.WriteDimensionAsync(DimensionStage, DimensionStagedWrite, CancellationToken)"/>) merges
/// the staging into the dimension in one transaction, and the build deletes its staging when it ends
/// (<see cref="DiscardAsync"/>). Safe to stage into from several threads at once.
/// </summary>
public sealed class DimensionStage : IAsyncDisposable
{
    /// <summary>How long a build may run before a later build of its dimension takes it for one its process left, and deletes its staging.</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(48);

    /// <summary>How many rows one statement deletes when a build's staging is deleted.</summary>
    internal const int DeleteSlice = 50_000;

    private readonly Func<OsduDbContext> _open;
    private readonly StagedTable _keys;
    private readonly StagedTable _values;
    private readonly StagedTable _members;
    private readonly StagedTable _attributes;
    private readonly StagedTable _texts;
    private readonly StagedTable _elements;
    private readonly StagedTable _records;
    private long _gone;
    private long _held;
    private int _discarded;

    internal DimensionStage(Func<OsduDbContext> open, short partitionId, int dimensionId, long dimensionRunId, Guid flowId)
    {
        _open = open;
        PartitionId = partitionId;
        DimensionId = dimensionId;
        DimensionRunId = dimensionRunId;
        FlowId = flowId;
        _keys = new StagedTable(this, SqlServerDimensionStage.KeyTable, SqlServerDimensionStage.KeyColumns, SqlServerDimensionStage.KeyTypes);
        _values = new StagedTable(this, SqlServerDimensionStage.ValueTable, SqlServerDimensionStage.ValueColumns, SqlServerDimensionStage.ValueTypes);
        _members = new StagedTable(this, SqlServerDimensionStage.MemberTable, SqlServerDimensionStage.MemberColumns, SqlServerDimensionStage.MemberTypes);
        _attributes = new StagedTable(this, SqlServerDimensionStage.AttributeTable, SqlServerDimensionStage.AttributeColumns, SqlServerDimensionStage.AttributeTypes);
        _texts = new StagedTable(this, SqlServerDimensionStage.TextTable, SqlServerDimensionStage.TextColumns, SqlServerDimensionStage.TextTypes);
        _elements = new StagedTable(this, SqlServerDimensionStage.ElementTable, SqlServerDimensionStage.ElementColumns, SqlServerDimensionStage.ElementTypes);
        _records = new StagedTable(this, SqlServerDimensionStage.RecordTable, SqlServerDimensionStage.RecordColumns, SqlServerDimensionStage.RecordTypes);
    }

    /// <summary>The build staging.</summary>
    public long DimensionRunId { get; }

    /// <summary>The dimension it builds.</summary>
    public int DimensionId { get; }

    /// <summary>The ledger identity of the flow building it.</summary>
    public Guid FlowId { get; }

    internal short PartitionId { get; }

    /// <summary>The keys staged, those an incremental load found gone aside.</summary>
    public long Keys => _keys.Staged - Interlocked.Read(ref _gone);

    /// <summary>The keys staged as cleaned: the keys the write holds.</summary>
    public long Values => _values.Staged;

    /// <summary>The values staged.</summary>
    public long Members => _members.Staged;

    /// <summary>The objects of a nested array staged.</summary>
    public long Elements => _elements.Staged;

    /// <summary>The keys a record read by its unique key holds, staged.</summary>
    public long Held => Interlocked.Read(ref _held);

    /// <summary>
    /// Stages keys a build read, with how many records hold each, in the order given, after those staged before. A key is
    /// staged once a build: the build reads each key once.
    /// </summary>
    public ValueTask AddKeysAsync(IReadOnlyCollection<KeyValuePair<string, long>> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return _keys.AddAsync(keys.Select(k => Row(SqlServerDimensionStore.HashOf(k.Key), k.Key, k.Value, false)).ToList(), ct);
    }

    /// <summary>Stages keys an incremental load read again and found held by no record: its write removes them.</summary>
    public async ValueTask AddGoneAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var rows = keys.Where(k => k.Length <= DeliveryDimensionValue.MaxOriginalLength).Distinct(StringComparer.Ordinal)
            .Select(k => Row(SqlServerDimensionStore.HashOf(k), k, 0L, true)).ToList();
        await _keys.AddAsync(rows, ct).ConfigureAwait(false);
        Interlocked.Add(ref _gone, rows.Count);
    }

    /// <summary>
    /// Deletes every key staged so far, which a read that starts over (every value read again by scanning) stages again;
    /// the keys staged after take places after theirs.
    /// </summary>
    public async Task ResetKeysAsync(CancellationToken ct)
    {
        await _keys.FlushAsync(ct).ConfigureAwait(false);
        await SqlServerDimensionStage.DeleteAsync(_open, this, SqlServerDimensionStage.KeyTable, ct).ConfigureAwait(false);
        _keys.Restart();
        Interlocked.Exchange(ref _gone, 0);
    }

    /// <summary>The keys staged, those found gone aside, in the order they were staged, <paramref name="batch"/> at a time.</summary>
    public async IAsyncEnumerable<IReadOnlyList<StagedKey>> KeysAsync(int batch, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        await _keys.FlushAsync(ct).ConfigureAwait(false);
        var after = -1L;
        while (true)
        {
            var read = await SqlServerDimensionStage.KeysAsync(_open, this, after, batch, ct).ConfigureAwait(false);
            if (read.Count == 0)
            {
                yield break;
            }

            yield return read;
            if (read.Count < batch)
            {
                yield break;
            }

            after = read[^1].Seq;
        }
    }

    /// <summary>
    /// Stages keys as cleaned, each under its place in the order the build read its keys (<paramref name="values"/>' Seq): the
    /// keys the write holds, with what each is under, its label and its filter. The note, the label and where it was read
    /// from are cut to what a key keeps, and a filter longer than a key keeps is none.
    /// </summary>
    /// <exception cref="ArgumentException">A key is longer than a dimension keeps.</exception>
    public ValueTask AddValuesAsync(IReadOnlyList<(long Seq, DimensionOriginalWrite Original)> values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        var rows = new List<object?[]>(values.Count);
        foreach (var (seq, value) in values)
        {
            if (value.Original.Length > DeliveryDimensionValue.MaxOriginalLength)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"A key of dimension {DimensionId} is {value.Original.Length} characters, more than the {DeliveryDimensionValue.MaxOriginalLength} a dimension keeps; the build leaves such keys out before it stages them."),
                    nameof(values));
            }

            rows.Add(
            [
                PartitionId, DimensionId, DimensionRunId, seq, SqlServerDimensionStore.HashOf(value.Original), value.Original, value.CleanValue, value.LeftOut,
                OsduLedger.Truncate(value.Note, 400), OsduLedger.Truncate(value.Label, DeliveryDimensionValue.MaxLabelLength),
                OsduLedger.Truncate(value.LabelFrom, DeliveryDimensionValue.MaxOriginalLength),
                value.Filter is { Length: > DeliveryDimensionValue.MaxFilterLength } ? null : value.Filter, value.Count, value.Filterable,
            ]);
        }

        return _values.AddAsync(rows, keepSeq: true, ct);
    }

    /// <summary>
    /// Stages attribute values of keys: each cut to what an attribute keeps, where it was read cut to what a key keeps, and
    /// one that is empty or under a name longer than an attribute's left out. A value collected from a key's records is
    /// staged with the records it counts; the write adds up the rows of one key, attribute and value.
    /// </summary>
    public ValueTask AddAttributesAsync(IReadOnlyList<(string Key, DimensionAttributeState Attribute)> attributes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var rows = new List<object?[]>(attributes.Count);
        foreach (var (key, attribute) in attributes)
        {
            if (string.IsNullOrEmpty(attribute.Value) || attribute.Name.Length > DeliveryDimensionAttributeValue.MaxNameLength)
            {
                continue;
            }

            if (!hashes.TryGetValue(key, out var hash))
            {
                hash = SqlServerDimensionStore.HashOf(key);
                hashes[key] = hash;
            }

            rows.Add(
            [
                PartitionId, DimensionId, DimensionRunId, 0L, hash, attribute.Name, OsduLedger.Truncate(attribute.Value, DeliveryDimensionAttributeValue.MaxValueLength),
                OsduLedger.Truncate(attribute.From, DeliveryDimensionValue.MaxOriginalLength), attribute.Records,
            ]);
        }

        return _attributes.AddAsync(rows, ct);
    }

    /// <summary>
    /// Stages, for every key staged, a collected attribute's value for records holding none of its values, with the key's
    /// records: what a build collects when the records hold no value of the attribute at all.
    /// </summary>
    public async Task AddToEveryKeyAsync(string name, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        await _keys.FlushAsync(ct).ConfigureAwait(false);
        var keys = Keys;
        if (keys == 0)
        {
            return;
        }

        var first = _attributes.Reserve(keys);
        await SqlServerDimensionStage.AttributeToEveryKeyAsync(
            _open, this, first, OsduLedger.Truncate(name, DeliveryDimensionAttributeValue.MaxNameLength)!,
            OsduLedger.Truncate(value, DeliveryDimensionAttributeValue.MaxValueLength)!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stages texts a build collected: a text longer than a key keeps, under a name longer than an attribute's, or shown as
    /// nothing, is left out. The write adds up the rows of one text.
    /// </summary>
    public ValueTask AddTextsAsync(IReadOnlyList<DimensionCollectedText> texts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var rows = texts
            .Where(t => t.Text.Length <= DeliveryDimensionValue.MaxOriginalLength && t.Name.Length <= DeliveryDimensionAttributeValue.MaxNameLength && !string.IsNullOrEmpty(t.Value))
            .Select(t => new object?[]
            {
                PartitionId, DimensionId, DimensionRunId, 0L, t.Name, SqlServerDimensionStore.HashOf(t.Text), t.Text,
                OsduLedger.Truncate(t.Value, DeliveryDimensionAttributeValue.MaxValueLength), t.Records,
            })
            .ToList();
        return _texts.AddAsync(rows, ct);
    }

    /// <summary>
    /// Stages objects of a nested array: each field named twice in one object keeps its first value, one that is empty or
    /// under a name longer than a field's is left out, and a value is cut to what a field keeps; an object holding none of
    /// the fields is one row with no field.
    /// </summary>
    /// <exception cref="ArgumentException">A record's id is longer than an object is staged under.</exception>
    public ValueTask AddElementsAsync(IReadOnlyList<StagedElement> elements, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(elements);
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var rows = new List<object?[]>(elements.Count * 4);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            if (element.Record.Length > DeliveryDimensionStageElement.MaxRecordLength)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"Record '{element.Record[..60]}...' has an id of {element.Record.Length} characters, more than the {DeliveryDimensionStageElement.MaxRecordLength} its objects are staged under."),
                    nameof(elements));
            }

            if (!hashes.TryGetValue(element.Key, out var hash))
            {
                hash = SqlServerDimensionStore.HashOf(element.Key);
                hashes[element.Key] = hash;
            }

            seen.Clear();
            var any = false;
            foreach (var (name, value) in element.Values)
            {
                if (name.Length > DeliveryDimensionAttributeValue.MaxNameLength || string.IsNullOrEmpty(value) || !seen.Add(name))
                {
                    continue;
                }

                any = true;
                rows.Add([PartitionId, DimensionId, DimensionRunId, 0L, hash, element.Record, element.Ordinal, name, OsduLedger.Truncate(value, DeliveryDimensionElement.MaxValueLength)]);
            }

            if (!any)
            {
                rows.Add([PartitionId, DimensionId, DimensionRunId, 0L, hash, element.Record, element.Ordinal, null, null]);
            }
        }

        return _elements.AddAsync(rows, ct);
    }

    /// <summary>
    /// Stages the records each key's label and attributes were read through: each record once a key, named by its id without
    /// its version (an id naming no record, or longer than a key keeps, is no record), with the entity type it names.
    /// </summary>
    public ValueTask AddReadThroughAsync(IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> keys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var rows = new List<object?[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, records) in keys)
        {
            seen.Clear();
            byte[]? hash = null;
            foreach (var record in records)
            {
                if (SqlServerDimensionStore.KeyRecordType(record) is { } type && seen.Add(record))
                {
                    hash ??= SqlServerDimensionStore.HashOf(key);
                    rows.Add([PartitionId, DimensionId, DimensionRunId, 0L, DeliveryDimensionStageRecord.ReadThrough, SqlServerDimensionStore.HashOf(record), hash, type]);
                }
            }
        }

        return _records.AddAsync(rows, ct);
    }

    /// <summary>
    /// Stages records read by their unique key (by the SHA-256 of it) and the keys each held: what a full load keeps for
    /// incremental loads, or what an incremental load found its changed records hold. A key longer than a dimension keeps is
    /// no key of it.
    /// </summary>
    public async ValueTask AddRecordsAsync(IReadOnlyCollection<byte[]> read, IReadOnlyList<(byte[] Record, string Key)> held, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(held);
        var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var rows = new List<object?[]>(read.Count + held.Count);
        rows.AddRange(read.Select(r => new object?[] { PartitionId, DimensionId, DimensionRunId, 0L, DeliveryDimensionStageRecord.Read, r, null, null }));
        var pairs = 0;
        foreach (var (record, key) in held)
        {
            if (key.Length > DeliveryDimensionValue.MaxOriginalLength)
            {
                continue;
            }

            if (!hashes.TryGetValue(key, out var hash))
            {
                hash = SqlServerDimensionStore.HashOf(key);
                hashes[key] = hash;
            }

            rows.Add([PartitionId, DimensionId, DimensionRunId, 0L, DeliveryDimensionStageRecord.Held, record, hash, null]);
            pairs++;
        }

        await _records.AddAsync(rows, ct).ConfigureAwait(false);
        Interlocked.Add(ref _held, pairs);
    }

    /// <summary>Stages values laid out from their keys.</summary>
    public ValueTask AddMembersAsync(IReadOnlyList<DimensionMemberWrite> members, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(members);
        return _members.AddAsync(
            members.Select(m => new object?[]
            {
                PartitionId, DimensionId, DimensionRunId, 0L, m.Value, m.Records, m.RecordsExact, m.Originals, m.Unfilterable, m.Filter, m.FilterParts,
            }).ToList(),
            ct);
    }

    /// <summary>
    /// The keys of every value the write lays out, the value's keys together, in the order of the values and then of the keys
    /// (both exactly, as a filter orders them): for a full load, every key staged as cleaned that belongs to a value; for a
    /// partial one (an incremental load's), those, and every other key the dimension holds under a value one of the keys
    /// read again is under now or was under before, which the read does not hold again.
    /// </summary>
    public async IAsyncEnumerable<StagedMemberKey> MemberKeysAsync(bool partial, [EnumeratorCancellation] CancellationToken ct)
    {
        await _values.FlushAsync(ct).ConfigureAwait(false);
        await _keys.FlushAsync(ct).ConfigureAwait(false);
        await foreach (var key in SqlServerDimensionStage.MemberKeysAsync(_open, this, partial, ct).ConfigureAwait(false))
        {
            yield return key;
        }
    }

    /// <summary>The values staged whose records one search of their filter counts: those whose keys could share a record, under one filter, every key of which a query carries.</summary>
    public async IAsyncEnumerable<IReadOnlyList<StagedMemberCount>> MembersToCountAsync(int batch, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batch, 1);
        await _members.FlushAsync(ct).ConfigureAwait(false);
        var after = -1L;
        while (true)
        {
            var read = await SqlServerDimensionStage.MembersToCountAsync(_open, this, after, batch, ct).ConfigureAwait(false);
            if (read.Count == 0)
            {
                yield break;
            }

            yield return read;
            if (read.Count < batch)
            {
                yield break;
            }

            after = read[^1].Seq;
        }
    }

    /// <summary>Sets the records of staged values counted by a search of their filter, now exact.</summary>
    public Task CountedAsync(IReadOnlyList<(long Seq, long Records)> counted, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(counted);
        return counted.Count == 0 ? Task.CompletedTask : SqlServerDimensionStage.CountedAsync(_open, this, counted, ct);
    }

    /// <summary>The keys staged, those found gone aside, whose records hold at least one object of the nested array.</summary>
    public async Task<long> KeysWithElementsAsync(CancellationToken ct)
    {
        await _elements.FlushAsync(ct).ConfigureAwait(false);
        await _keys.FlushAsync(ct).ConfigureAwait(false);
        return await SqlServerDimensionStage.KeysWithElementsAsync(_open, this, ct).ConfigureAwait(false);
    }

    /// <summary>Copies every row still held in memory to the staging tables.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        foreach (var table in Tables())
        {
            await table.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes everything the build staged, a slice at a time; what a call that did not finish left, the next deletes. A
    /// build deletes its staging when it ends, written or not.
    /// </summary>
    public async Task DiscardAsync(CancellationToken ct)
    {
        foreach (var table in Tables())
        {
            await table.FlushAsync(ct).ConfigureAwait(false);
        }

        await SqlServerDimensionStage.DeleteAllAsync(_open, this, ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _discarded, 1);
    }

    /// <summary>Lets go of what the staging holds in memory; the staging tables are left as they are (<see cref="DiscardAsync"/>).</summary>
    public ValueTask DisposeAsync()
    {
        foreach (var table in Tables())
        {
            table.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Whether the build's staging was deleted.</summary>
    public bool Discarded => Volatile.Read(ref _discarded) == 1;

    internal Func<OsduDbContext> Open => _open;

    private StagedTable[] Tables() => [_values, _keys, _members, _attributes, _texts, _elements, _records];

    private object?[] Row(byte[] hash, string key, long count, bool gone) => [PartitionId, DimensionId, DimensionRunId, 0L, hash, key, count, gone];

    /// <summary>
    /// One staging table of the build: rows held in memory until a batch fills (<see cref="MaxRows"/> rows, or about
    /// <see cref="MaxChars"/> characters of text), each given its place in the order staged, and copied to the table in one
    /// statement a batch, one copy at a time; a caller that fills a batch while the one before it is still being copied
    /// waits for it.
    /// </summary>
    private sealed class StagedTable(DimensionStage stage, string table, string[] columns, Type[] types) : IDisposable
    {
        /// <summary>The most rows a batch holds.</summary>
        private const int MaxRows = 5_000;

        /// <summary>About the most characters of text a batch holds.</summary>
        private const long MaxChars = 2_000_000;

        private readonly Lock _gate = new();
        private readonly SemaphoreSlim _copying = new(1, 1);
        private List<object?[]> _rows = [];
        private long _chars;
        private long _next;
        private long _staged;

        /// <summary>The rows staged so far.</summary>
        public long Staged => Interlocked.Read(ref _staged);

        /// <summary>Takes <paramref name="count"/> places for rows a statement stages itself, and returns the first.</summary>
        public long Reserve(long count)
        {
            lock (_gate)
            {
                var first = _next;
                _next += count;
                _staged += count;
                return first;
            }
        }

        /// <summary>Starts the count of rows over, for a table whose rows were deleted; places go on after those taken.</summary>
        public void Restart()
        {
            lock (_gate)
            {
                _staged = 0;
            }
        }

        public ValueTask AddAsync(List<object?[]> rows, CancellationToken ct) => AddAsync(rows, keepSeq: false, ct);

        /// <summary>Holds <paramref name="rows"/>, each given its place unless <paramref name="keepSeq"/> says it carries one, copying a batch that fills.</summary>
        public async ValueTask AddAsync(List<object?[]> rows, bool keepSeq, CancellationToken ct)
        {
            if (rows.Count == 0)
            {
                return;
            }

            var chars = rows.Sum(Size);
            List<object?[]>? full = null;
            lock (_gate)
            {
                foreach (var row in rows)
                {
                    if (!keepSeq)
                    {
                        row[3] = _next++;
                    }

                    _rows.Add(row);
                }

                _staged += rows.Count;
                _chars += chars;
                if (_rows.Count >= MaxRows || _chars >= MaxChars)
                {
                    full = _rows;
                    _rows = [];
                    _chars = 0;
                }
            }

            if (full is not null)
            {
                await CopyAsync(full, ct).ConfigureAwait(false);
            }
        }

        /// <summary>Copies the rows held, and waits for a copy under way, so every row staged before the call is in the table.</summary>
        public async Task FlushAsync(CancellationToken ct)
        {
            List<object?[]> rows;
            lock (_gate)
            {
                rows = _rows;
                _rows = [];
                _chars = 0;
            }

            if (rows.Count > 0)
            {
                await CopyAsync(rows, ct).ConfigureAwait(false);
                return;
            }

            await _copying.WaitAsync(ct).ConfigureAwait(false);
            _copying.Release();
        }

        public void Dispose() => _copying.Dispose();

        private async Task CopyAsync(List<object?[]> rows, CancellationToken ct)
        {
            await _copying.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await SqlServerDimensionStage.CopyAsync(stage.Open, table, columns, types, rows, ct).ConfigureAwait(false);
            }
            finally
            {
                _copying.Release();
            }
        }

        /// <summary>About how much text a row holds, by which a batch is bounded besides its rows.</summary>
        private static long Size(object?[] row)
        {
            long size = 16;
            foreach (var cell in row)
            {
                size += cell is string text ? text.Length : 8;
            }

            return size;
        }
    }
}

/// <summary>
/// The statements of a dimension build's staging (<see cref="DimensionStage"/>): its copies, its reads and its deletes, each a
/// seek of the build's own rows (the partition, the dimension and the build lead every staging table's key).
/// </summary>
internal static class SqlServerDimensionStage
{
    /// <summary>How long a staging statement may run: as long as its rows take, bounded by the build's cancellation.</summary>
    private const int TimeoutSeconds = 0;

    internal const string KeyTable = "DimensionStageKey";
    internal const string ValueTable = "DimensionStageValue";
    internal const string MemberTable = "DimensionStageMember";
    internal const string AttributeTable = "DimensionStageAttribute";
    internal const string TextTable = "DimensionStageText";
    internal const string ElementTable = "DimensionStageElement";
    internal const string RecordTable = "DimensionStageRecord";

    /// <summary>Every staging table, the cleaned keys first: a build whose staging is being deleted shows it there first, which its write checks.</summary>
    internal static readonly string[] Tables = [ValueTable, MemberTable, KeyTable, AttributeTable, TextTable, ElementTable, RecordTable];

    private static readonly string[] Leading = ["PartitionId", "DimensionId", "DimensionRunId", "Seq"];
    private static readonly Type[] LeadingTypes = [typeof(short), typeof(int), typeof(long), typeof(long)];

    internal static readonly string[] KeyColumns = [.. Leading, "KeyHash", "Key", "Count", "Gone"];
    internal static readonly Type[] KeyTypes = [.. LeadingTypes, typeof(byte[]), typeof(string), typeof(long), typeof(bool)];

    internal static readonly string[] ValueColumns =
        [.. Leading, "OriginalHash", "Original", "CleanValue", "LeftOut", "Note", "Label", "LabelFrom", "Filter", "Count", "Filterable"];

    internal static readonly Type[] ValueTypes =
    [
        .. LeadingTypes, typeof(byte[]), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
        typeof(long), typeof(bool),
    ];

    internal static readonly string[] MemberColumns = [.. Leading, "Value", "Records", "RecordsExact", "Originals", "Unfilterable", "Filter", "FilterParts"];
    internal static readonly Type[] MemberTypes = [.. LeadingTypes, typeof(string), typeof(long), typeof(bool), typeof(int), typeof(int), typeof(string), typeof(int)];

    internal static readonly string[] AttributeColumns = [.. Leading, "KeyHash", "Name", "Value", "ValueFrom", "Records"];
    internal static readonly Type[] AttributeTypes = [.. LeadingTypes, typeof(byte[]), typeof(string), typeof(string), typeof(string), typeof(long)];

    internal static readonly string[] TextColumns = [.. Leading, "Name", "TextHash", "Text", "Value", "Records"];
    internal static readonly Type[] TextTypes = [.. LeadingTypes, typeof(string), typeof(byte[]), typeof(string), typeof(string), typeof(long)];

    internal static readonly string[] ElementColumns = [.. Leading, "KeyHash", "Record", "Ordinal", "Name", "Value"];
    internal static readonly Type[] ElementTypes = [.. LeadingTypes, typeof(byte[]), typeof(string), typeof(int), typeof(string), typeof(string)];

    internal static readonly string[] RecordColumns = [.. Leading, "Kind", "RecordHash", "KeyHash", "EntityType"];
    internal static readonly Type[] RecordTypes = [.. LeadingTypes, typeof(byte), typeof(byte[]), typeof(byte[]), typeof(string)];

    private const string Own = "[PartitionId] = @p AND [DimensionId] = @d AND [DimensionRunId] = @run";

    /// <summary>
    /// The staging copied into the write's temporary tables (<c>SqlServerDimensionStore</c>'s stage), as the merge reads them:
    /// the keys as cleaned; the values; each key's value of an attribute once, its records added up and where it was read
    /// the first, exactly, of the rows naming it (one for a value read through the key's record, the first text for a
    /// collected one); each text once, its records added up; each key's objects numbered from 1 in the order of their records'
    /// ids and then of the objects in each, an object holding no field one row of the table's first element column; the
    /// records each key was read through; the keys an incremental load removes; and the records read and the keys they hold.
    /// Only the keys the write holds have attributes, objects and records read through.
    /// </summary>
    internal const string CopySql = """
        INSERT INTO #DimValue ([Seq], [Original], [OriginalHash], [CleanValue], [LeftOut], [Note], [Label], [LabelFrom], [Filter], [Count], [Filterable])
        SELECT CONVERT(int, s.[Seq]), s.[Original], s.[OriginalHash], s.[CleanValue], s.[LeftOut], s.[Note], s.[Label], s.[LabelFrom], s.[Filter], s.[Count], s.[Filterable]
        FROM [osdu].[DimensionStageValue] AS s
        WHERE s.[PartitionId] = @p AND s.[DimensionId] = @d AND s.[DimensionRunId] = @run
        ORDER BY s.[OriginalHash];

        INSERT INTO #DimMember ([Value], [Records], [RecordsExact], [Originals], [Unfilterable], [Filter], [FilterParts])
        SELECT m.[Value], m.[Records], m.[RecordsExact], m.[Originals], m.[Unfilterable], m.[Filter], m.[FilterParts]
        FROM [osdu].[DimensionStageMember] AS m
        WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[DimensionRunId] = @run
        ORDER BY m.[Value];

        INSERT INTO #DimAttr ([OriginalHash], [Name], [Value], [ValueFrom], [Records])
        SELECT a.[KeyHash], a.[Name], a.[Value], MIN(a.[ValueFrom] COLLATE Latin1_General_100_BIN2), SUM(a.[Records])
        FROM [osdu].[DimensionStageAttribute] AS a
        WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d AND a.[DimensionRunId] = @run
          AND EXISTS (SELECT 1 FROM #DimValue AS v WHERE v.[OriginalHash] = a.[KeyHash])
        GROUP BY a.[KeyHash], a.[Name], a.[Value];

        INSERT INTO #DimText ([Name], [TextHash], [Text], [Value], [Records])
        SELECT t.[Name], t.[TextHash], MIN(t.[Text]), MIN(t.[Value]), SUM(t.[Records])
        FROM [osdu].[DimensionStageText] AS t
        WHERE t.[PartitionId] = @p AND t.[DimensionId] = @d AND t.[DimensionRunId] = @run
        GROUP BY t.[Name], t.[TextHash];

        IF @elements = 1
            INSERT INTO #DimElem ([OriginalHash], [Seq], [Name], [Value])
            SELECT e.[KeyHash], CONVERT(int, DENSE_RANK() OVER (PARTITION BY e.[KeyHash] ORDER BY e.[Record], e.[Ordinal])), ISNULL(e.[Name], @firstElement), e.[Value]
            FROM [osdu].[DimensionStageElement] AS e
            WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d AND e.[DimensionRunId] = @run
              AND EXISTS (SELECT 1 FROM #DimValue AS v WHERE v.[OriginalHash] = e.[KeyHash]);

        INSERT INTO #DimRead ([OriginalHash], [RecordHash], [EntityType])
        SELECT r.[KeyHash], r.[RecordHash], MIN(r.[EntityType])
        FROM [osdu].[DimensionStageRecord] AS r
        WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d AND r.[DimensionRunId] = @run AND r.[Kind] = 1
          AND EXISTS (SELECT 1 FROM #DimValue AS v WHERE v.[OriginalHash] = r.[KeyHash])
        GROUP BY r.[KeyHash], r.[RecordHash];

        INSERT INTO #DimGone ([OriginalHash])
        SELECT DISTINCT k.[KeyHash]
        FROM [osdu].[DimensionStageKey] AS k
        WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND k.[DimensionRunId] = @run AND k.[Gone] = 1;

        INSERT INTO #DimRecRead ([RecordHash])
        SELECT DISTINCT r.[RecordHash]
        FROM [osdu].[DimensionStageRecord] AS r
        WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d AND r.[DimensionRunId] = @run AND r.[Kind] = 2;

        INSERT INTO #DimRecHeld ([RecordHash], [OriginalHash])
        SELECT DISTINCT r.[RecordHash], r.[KeyHash]
        FROM [osdu].[DimensionStageRecord] AS r
        WHERE r.[PartitionId] = @p AND r.[DimensionId] = @d AND r.[DimensionRunId] = @run AND r.[Kind] = 3;
        """;

    /// <summary>How many keys as cleaned and values the staging holds, which the write compares with what the build staged.</summary>
    internal const string CountSql = """
        SELECT
            (SELECT COUNT_BIG(*) FROM [osdu].[DimensionStageValue] WHERE [PartitionId] = @p AND [DimensionId] = @d AND [DimensionRunId] = @run),
            (SELECT COUNT_BIG(*) FROM [osdu].[DimensionStageMember] WHERE [PartitionId] = @p AND [DimensionId] = @d AND [DimensionRunId] = @run);
        """;

    // The builds of the dimension whose staging a table holds, the current one aside: a seek a build, never a read of its rows.
    private static string BuildsSql(string table) => $"""
        SET @after = -1;
        WHILE 1 = 1
        BEGIN
            SET @next = NULL;
            SELECT TOP (1) @next = s.[DimensionRunId] FROM [osdu].[{table}] AS s
            WHERE s.[PartitionId] = @p AND s.[DimensionId] = @d AND s.[DimensionRunId] > @after ORDER BY s.[DimensionRunId];
            IF @next IS NULL BREAK;
            IF @next <> @run AND NOT EXISTS (SELECT 1 FROM @builds WHERE [Id] = @next) INSERT INTO @builds ([Id]) VALUES (@next);
            SET @after = @next;
        END;
        """;

    /// <summary>
    /// The builds of the dimension that left staging behind, with whether each is still running and when it started.
    /// </summary>
    private static readonly string LeftSql = "SET NOCOUNT ON;\nDECLARE @builds TABLE ([Id] bigint NOT NULL PRIMARY KEY);\nDECLARE @after bigint, @next bigint;\n"
        + string.Join("\n", Tables.Select(BuildsSql)) + """

        SELECT b.[Id], r.[Status], r.[StartedUtc]
        FROM @builds AS b
        LEFT JOIN [osdu].[DimensionRun] AS r ON r.[PartitionId] = @p AND r.[DimensionRunId] = b.[Id];
        """;

    // The write lock of the dimension, taken without waiting by a build deleting what another left: a build that is writing
    // keeps its staging until it has written.
    private const string TryLockSql = """
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
        SELECT @granted;
        """;

    private const string KeysSql = """
        SELECT TOP (@batch) k.[Seq], k.[Key], k.[Count]
        FROM [osdu].[DimensionStageKey] AS k
        WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND k.[DimensionRunId] = @run AND k.[Seq] > @after AND k.[Gone] = 0
        ORDER BY k.[Seq];
        """;

    private const string EveryKeySql = """
        INSERT INTO [osdu].[DimensionStageAttribute] ([PartitionId], [DimensionId], [DimensionRunId], [Seq], [KeyHash], [Name], [Value], [ValueFrom], [Records])
        SELECT @p, @d, @run, @first + ROW_NUMBER() OVER (ORDER BY k.[Seq]) - 1, k.[KeyHash], @name, @value, NULL, k.[Count]
        FROM [osdu].[DimensionStageKey] AS k
        WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND k.[DimensionRunId] = @run AND k.[Gone] = 0;
        """;

    private const string MemberKeysSql = """
        SELECT s.[CleanValue], s.[Original], s.[Count], s.[Filterable]
        FROM [osdu].[DimensionStageValue] AS s
        WHERE s.[PartitionId] = @p AND s.[DimensionId] = @d AND s.[DimensionRunId] = @run AND s.[LeftOut] IS NULL
        ORDER BY s.[CleanValue], s.[Original];
        """;

    // A partial write lays out every value a key it holds is under now or was under before, from all its keys: those it holds,
    // and the dimension's others under the value that it does not hold again (nor removes), each by seeks of the dimension.
    private const string PartialMemberKeysSql = """
        SET NOCOUNT ON;
        CREATE TABLE #Leaving ([Hash] binary(32) NOT NULL PRIMARY KEY);
        INSERT INTO #Leaving ([Hash])
        SELECT s.[OriginalHash] FROM [osdu].[DimensionStageValue] AS s WHERE s.[PartitionId] = @p AND s.[DimensionId] = @d AND s.[DimensionRunId] = @run
        UNION
        SELECT k.[KeyHash] FROM [osdu].[DimensionStageKey] AS k WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND k.[DimensionRunId] = @run AND k.[Gone] = 1;

        CREATE TABLE #Affected ([Value] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY);
        INSERT INTO #Affected ([Value])
        SELECT s.[CleanValue] FROM [osdu].[DimensionStageValue] AS s
        WHERE s.[PartitionId] = @p AND s.[DimensionId] = @d AND s.[DimensionRunId] = @run AND s.[LeftOut] IS NULL
        UNION
        SELECT m.[Value]
        FROM #Leaving AS l
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[OriginalHash] = l.[Hash]
        INNER JOIN [osdu].[DimensionMember] AS m WITH (FORCESEEK) ON m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[MemberId] = v.[MemberId]
        WHERE v.[RemovedRunId] IS NULL;

        SELECT s.[CleanValue], s.[Original], s.[Count], s.[Filterable]
        FROM [osdu].[DimensionStageValue] AS s
        WHERE s.[PartitionId] = @p AND s.[DimensionId] = @d AND s.[DimensionRunId] = @run AND s.[LeftOut] IS NULL
        UNION ALL
        SELECT m.[Value], v.[Original], v.[Count], v.[Filterable]
        FROM #Affected AS a
        INNER JOIN [osdu].[DimensionMember] AS m WITH (FORCESEEK) ON m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[Value] = a.[Value]
        INNER JOIN [osdu].[DimensionValue] AS v WITH (FORCESEEK) ON v.[PartitionId] = @p AND v.[DimensionId] = @d AND v.[MemberId] = m.[MemberId]
        WHERE m.[RemovedRunId] IS NULL AND v.[RemovedRunId] IS NULL
          AND NOT EXISTS (SELECT 1 FROM #Leaving AS l WHERE l.[Hash] = v.[OriginalHash])
        ORDER BY 1, 2;
        """;

    private const string MembersToCountSql = """
        SELECT TOP (@batch) m.[Seq], m.[Filter]
        FROM [osdu].[DimensionStageMember] AS m
        WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[DimensionRunId] = @run AND m.[Seq] > @after
          AND m.[RecordsExact] = 0 AND m.[FilterParts] = 1 AND m.[Unfilterable] = 0 AND m.[Filter] IS NOT NULL
        ORDER BY m.[Seq];
        """;

    private const string CountedSql = """
        UPDATE m SET m.[Records] = c.[Records], m.[RecordsExact] = 1
        FROM [osdu].[DimensionStageMember] AS m
        INNER JOIN #Counted AS c ON c.[Seq] = m.[Seq]
        WHERE m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[DimensionRunId] = @run;
        """;

    private const string KeysWithElementsSql = """
        SELECT COUNT_BIG(*)
        FROM (SELECT DISTINCT e.[KeyHash] FROM [osdu].[DimensionStageElement] AS e
              WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d AND e.[DimensionRunId] = @run) AS h
        WHERE EXISTS (
            SELECT 1 FROM [osdu].[DimensionStageKey] AS k
            WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND k.[DimensionRunId] = @run AND k.[Gone] = 0 AND k.[KeyHash] = h.[KeyHash]);
        """;

    /// <summary>Copies <paramref name="rows"/>, in the order of their places, to a staging table in one statement.</summary>
    internal static async Task CopyAsync(Func<OsduDbContext> open, string table, string[] columns, Type[] types, List<object?[]> rows, CancellationToken ct)
    {
        await using var db = open();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        using var bulk = new SqlBulkCopy(connection)
        {
            DestinationTableName = $"[osdu].[{table}]",
            BatchSize = 0,
            BulkCopyTimeout = TimeoutSeconds,
            EnableStreaming = true,
        };
        foreach (var column in columns)
        {
            bulk.ColumnMappings.Add(column, column);
        }

        foreach (var column in Leading)
        {
            bulk.ColumnOrderHints.Add(column, SortOrder.Ascending);
        }

        rows.Sort((left, right) => ((long)left[3]!).CompareTo((long)right[3]!));
        await using var reader = new StreamingDataReader(columns, types, Listed(rows, ct).GetAsyncEnumerator(ct));
        await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
    }

    /// <summary>The builds of the stage's dimension that left staging behind: each with its status and when it started, or none when its build is gone.</summary>
    internal static async Task<IReadOnlyList<(long Build, string? Status, DateTime? StartedUtc)>> LeftAsync(Func<OsduDbContext> open, DimensionStage stage, CancellationToken ct)
    {
        await using var db = open();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await using var command = Command(connection, null, LeftSql, stage);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var found = new List<(long, string?, DateTime?)>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)));
        }

        return found;
    }

    /// <summary>
    /// Deletes what build <paramref name="build"/> of the stage's dimension staged, a slice at a time, each under the
    /// dimension's write lock taken without waiting; returns false, deleting no more, when another build is writing the
    /// dimension, so what is left is left for a later build.
    /// </summary>
    internal static async Task<bool> DeleteLeftAsync(Func<OsduDbContext> open, DimensionStage stage, long build, CancellationToken ct)
    {
        foreach (var table in Tables)
        {
            while (true)
            {
                await using var db = open();
                await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
                var connection = (SqlConnection)db.Database.GetDbConnection();
                await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using (var locking = Command(connection, tx, TryLockSql, stage))
                {
                    locking.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = string.Create(CultureInfo.InvariantCulture, $"osdu-dimension:{stage.PartitionId}:{stage.DimensionId}") });
                    if (Convert.ToInt32(await locking.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) < 0)
                    {
                        return false;
                    }
                }

                int deleted;
                await using (var delete = Command(connection, tx, $"DELETE TOP (@slice) FROM [osdu].[{table}] WHERE [PartitionId] = @p AND [DimensionId] = @d AND [DimensionRunId] = @build;", stage))
                {
                    delete.Parameters.Add(new SqlParameter("@slice", SqlDbType.Int) { Value = DimensionStage.DeleteSlice });
                    delete.Parameters.Add(new SqlParameter("@build", SqlDbType.BigInt) { Value = build });
                    deleted = await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                if (deleted < DimensionStage.DeleteSlice)
                {
                    break;
                }
            }
        }

        return true;
    }

    /// <summary>Deletes what the stage's build staged in every table, a slice at a time.</summary>
    internal static async Task DeleteAllAsync(Func<OsduDbContext> open, DimensionStage stage, CancellationToken ct)
    {
        foreach (var table in Tables)
        {
            await DeleteAsync(open, stage, table, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes what the stage's build staged in <paramref name="table"/>, a slice at a time.</summary>
    internal static async Task DeleteAsync(Func<OsduDbContext> open, DimensionStage stage, string table, CancellationToken ct)
    {
        await using var db = open();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        while (true)
        {
            await using var delete = Command(connection, null, $"DELETE TOP (@slice) FROM [osdu].[{table}] WHERE {Own};", stage);
            delete.Parameters.Add(new SqlParameter("@slice", SqlDbType.Int) { Value = DimensionStage.DeleteSlice });
            if (await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false) < DimensionStage.DeleteSlice)
            {
                return;
            }
        }
    }

    internal static async Task<IReadOnlyList<StagedKey>> KeysAsync(Func<OsduDbContext> open, DimensionStage stage, long after, int batch, CancellationToken ct)
    {
        await using var db = open();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await using var command = Command(connection, null, KeysSql, stage);
        command.Parameters.Add(new SqlParameter("@after", SqlDbType.BigInt) { Value = after });
        command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = batch });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var keys = new List<StagedKey>(batch);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(new StagedKey(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
        }

        return keys;
    }

    internal static async Task AttributeToEveryKeyAsync(Func<OsduDbContext> open, DimensionStage stage, long first, string name, string value, CancellationToken ct)
    {
        await using var db = open();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await using var command = Command(connection, null, EveryKeySql, stage);
        command.Parameters.Add(new SqlParameter("@first", SqlDbType.BigInt) { Value = first });
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, DeliveryDimensionAttributeValue.MaxNameLength) { Value = name });
        command.Parameters.Add(new SqlParameter("@value", SqlDbType.NVarChar, DeliveryDimensionAttributeValue.MaxValueLength) { Value = value });
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static async IAsyncEnumerable<StagedMemberKey> MemberKeysAsync(
        Func<OsduDbContext> open, DimensionStage stage, bool partial, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var db = open();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await using var command = Command(connection, null, partial ? PartialMemberKeysSql : MemberKeysSql, stage);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new StagedMemberKey(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetBoolean(3));
        }
    }

    internal static async Task<IReadOnlyList<StagedMemberCount>> MembersToCountAsync(Func<OsduDbContext> open, DimensionStage stage, long after, int batch, CancellationToken ct)
    {
        await using var db = open();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await using var command = Command(connection, null, MembersToCountSql, stage);
        command.Parameters.Add(new SqlParameter("@after", SqlDbType.BigInt) { Value = after });
        command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = batch });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var found = new List<StagedMemberCount>(batch);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            found.Add(new StagedMemberCount(reader.GetInt64(0), reader.GetString(1)));
        }

        return found;
    }

    internal static async Task CountedAsync(Func<OsduDbContext> open, DimensionStage stage, IReadOnlyList<(long Seq, long Records)> counted, CancellationToken ct)
    {
        await using var db = open();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        // Made by a statement of its own, with no parameter: a parameterized one runs in a scope of its own, which takes its
        // temporary tables with it when it ends.
        await using (var create = new SqlCommand("CREATE TABLE #Counted ([Seq] bigint NOT NULL PRIMARY KEY, [Records] bigint NOT NULL);", connection) { CommandTimeout = TimeoutSeconds })
        {
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "#Counted", BulkCopyTimeout = TimeoutSeconds })
        {
            var rows = counted.OrderBy(c => c.Seq).Select(c => new object?[] { c.Seq, c.Records }).ToList();
            await using var reader = new StreamingDataReader(["Seq", "Records"], [typeof(long), typeof(long)], Listed(rows, ct).GetAsyncEnumerator(ct));
            await bulk.WriteToServerAsync(reader, ct).ConfigureAwait(false);
        }

        await using var update = Command(connection, null, CountedSql, stage);
        await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<long> KeysWithElementsAsync(Func<OsduDbContext> open, DimensionStage stage, CancellationToken ct)
    {
        await using var db = open();
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await using var command = Command(connection, null, KeysWithElementsSql, stage);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>A statement over the stage's build, its partition, dimension and build given.</summary>
    internal static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql, DimensionStage stage)
    {
        var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = TimeoutSeconds };
        command.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = stage.PartitionId });
        command.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = stage.DimensionId });
        command.Parameters.Add(new SqlParameter("@run", SqlDbType.BigInt) { Value = stage.DimensionRunId });
        return command;
    }

    private static async IAsyncEnumerable<object?[]> Listed(List<object?[]> rows, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (i % 50_000 == 49_999)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            yield return rows[i];
        }
    }
}
