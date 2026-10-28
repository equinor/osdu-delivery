using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// One record as the in-memory ingestion tables hold it: the record row, the rows of each child dataset, and the system
/// columns SQLFlow's own ingestion would have stamped on them (when the row first arrived and last changed, which file it
/// came from, and whether it is soft-deleted).
/// </summary>
public sealed class MemoryRecord
{
    public required IDictionary<string, object?> Row { get; init; }

    public Dictionary<string, List<IDictionary<string, object?>>> Datasets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime UpdatedUtc { get; set; }

    /// <summary>When the ingestion flow first inserted the row, which it never moves afterwards; null for a table without the column.</summary>
    public DateTime? InsertedUtc { get; set; }

    public string? FileName { get; set; }

    public long? RowNumber { get; set; }

    public DateTime? DeletedUtc { get; set; }

    /// <summary>The newest change time among a child dataset's rows, which the fingerprint folds in.</summary>
    public Dictionary<string, DateTime> DatasetUpdatedUtc { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MemoryRecord With(string column, object? value)
    {
        Row[column] = value;
        return this;
    }

    public MemoryRecord AddChild(string dataset, IDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!Datasets.TryGetValue(dataset, out var rows))
        {
            rows = [];
            Datasets[dataset] = rows;
        }

        rows.Add(row);
        return this;
    }
}

/// <summary>
/// The ingestion tables a flow reads, in memory: the fast suites' stand-in for SQL Server. It answers exactly what the
/// SQL Server source answers (the window, the scope predicate, key selections, slices of the identity primary key, the
/// order a read pages in, the ingestion fingerprint and each record's origin), so the planner, the intake and the worker
/// run their real code paths on every machine, and the SQL Server contract suite proves the two agree.
/// </summary>
public sealed class MemoryIngestionTables : IIngestionSourceFactory
{
    private readonly TimeProvider _time;

    public MemoryIngestionTables(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The records the tables hold, in key order as the source reads them.</summary>
    public List<MemoryRecord> Records { get; } = [];

    /// <summary>How many reads were opened, which a test uses to prove a run read the source once.</summary>
    public int Opens { get; private set; }

    /// <summary>Every selection a read was opened for, in order.</summary>
    public List<SourceSelection> Selections { get; } = [];

    /// <summary>What a shape check reports as wrong with the tables, as the SQL Server source reports a missing table; null when nothing is.</summary>
    public string? ShapeProblem { get; set; }

    /// <summary>The SQL Server type each record key column holds, as the SQL Server source introspects it; a column not named holds nvarchar(400).</summary>
    public Dictionary<string, string> KeyTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public MemoryRecord Add(MemoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Records.Add(record);
        return record;
    }

    public IIngestionSource Open(FlowDefinition flow, IReadOnlyDictionary<string, string> values, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        return new MemorySource(this, flow, values, _time);
    }

    internal void Opened(SourceSelection selection)
    {
        Opens++;
        Selections.Add(selection);
    }

    private sealed class MemorySource : IIngestionSource
    {
        private readonly MemoryIngestionTables _tables;
        private readonly FlowDefinition _flow;
        private readonly IReadOnlyDictionary<string, string> _values;
        private readonly TimeProvider _time;

        public MemorySource(MemoryIngestionTables tables, FlowDefinition flow, IReadOnlyDictionary<string, string> values, TimeProvider time)
        {
            _tables = tables;
            _flow = flow;
            _values = values;
            _time = time;
        }

        public Task VerifyAsync(CancellationToken ct = default)
            => _tables.ShapeProblem is { } problem
                ? Task.FromException(new DeliveryException($"Flow '{_flow.Label}': {problem}"))
                : Task.CompletedTask;

        public Task<SourceHeader> OpenAsync(SourceSelection selection, SourceWindow? stored, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(selection);
            _tables.Opened(selection);
            var upper = stored?.UpperUtc ?? _time.GetUtcNow().UtcDateTime;
            var lower = stored is not null ? stored.LowerUtc : selection.Kind == SourceSelectionKind.Incremental ? selection.LowerUtc : null;
            var window = new SourceWindow(lower, upper);

            var inScope = _tables.Records.Where(InScope).ToList();
            IReadOnlyList<KeyTuple> missing = [];
            IReadOnlyList<KeyTuple> outOfScope = [];
            long candidates;
            if (selection.Kind == SourceSelectionKind.Keys)
            {
                var all = _tables.Records.ToDictionary(KeyOf, r => r);
                missing = selection.Keys.Where(k => !all.ContainsKey(k)).ToList();
                outOfScope = selection.Keys.Where(k => all.TryGetValue(k, out var record) && !InScope(record)).ToList();
                candidates = selection.Keys.Count - missing.Count - outOfScope.Count;
            }
            else
            {
                candidates = inScope.Count(r => InWindow(r, window, selection.Kind));
            }

            return Task.FromResult(new SourceHeader
            {
                Selection = selection,
                Window = window,
                Columns = Columns(),
                KeyColumns = _flow.Source.Record.Key.Select(k => new SourceKeyColumn(k, "nvarchar(400)")).ToList(),
                PrimaryKey = PrimaryKey,
                EstimatedCandidates = candidates,
                HasChanges = candidates > 0,
                MissingKeys = missing,
                OutOfScopeKeys = outOfScope,
            });
        }

        private string? PrimaryKey => _flow.Source.Record.PrimaryKey;

        public Task<IReadOnlyList<ScopeParameterValues>> ScopeValuesAsync(int max, CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
            var listed = _flow.Source.Record.Scope.Select(scope =>
            {
                var values = _tables.Records
                    .Where(r => r.DeletedUtc is null)
                    .Select(r => SourceRow.Stringify(r.Row.TryGetValue(scope.Key, out var value) ? value : null))
                    .OfType<string>()
                    .GroupBy(v => v, StringComparer.Ordinal)
                    .Select(g => new ScopeValue(g.Key, g.LongCount()))
                    .OrderByDescending(v => v.Rows)
                    .ThenBy(v => v.Value, StringComparer.Ordinal)
                    .ToList();
                return new ScopeParameterValues(scope.Value, scope.Key, values.Take(max).ToList(), values.Count > max);
            }).ToList();
            return Task.FromResult<IReadOnlyList<ScopeParameterValues>>(listed);
        }

        public Task<ScopeParameterValues> ColumnValuesAsync(string parameter, string column, string? search, int max, CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
            var values = _tables.Records
                .Where(r => r.DeletedUtc is null)
                .Select(r => SourceRow.Stringify(r.Row.TryGetValue(column, out var value) ? value : null))
                .OfType<string>()
                .Where(v => string.IsNullOrWhiteSpace(search) || v.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
                .GroupBy(v => v, StringComparer.Ordinal)
                .Select(g => new ScopeValue(g.Key, g.LongCount()))
                .OrderByDescending(v => v.Rows)
                .ThenBy(v => v.Value, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(new ScopeParameterValues(parameter, column, values.Take(max).ToList(), values.Count > max));
        }

        public Task<IReadOnlyList<SourceKeyColumn>> KeyColumnsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SourceKeyColumn>>(_flow.Source.Record.Key
                .Select(k => new SourceKeyColumn(k, _tables.KeyTypes.TryGetValue(k, out var type) ? type : "nvarchar(400)"))
                .ToList());

        public async IAsyncEnumerable<IngestionKey> ReadKeysAsync(int pageSize, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
            await Task.CompletedTask.ConfigureAwait(false);
            var ordered = PrimaryKey is null
                ? _tables.Records.Where(InScope).OrderBy(r => KeyOf(r).Json, StringComparer.Ordinal)
                : _tables.Records.Where(InScope).OrderBy(IdOf);
            foreach (var record in ordered)
            {
                ct.ThrowIfCancellationRequested();
                yield return new IngestionKey(KeyOf(record), record.UpdatedUtc);
            }
        }

        public Task<IReadOnlyList<KeyRange>> SliceBoundsAsync(SourceHeader header, int slices, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(header);
            ArgumentOutOfRangeException.ThrowIfLessThan(slices, 1);
            var candidates = Candidates(header).ToList();
            if (slices == 1 || candidates.Count <= 1)
            {
                return Task.FromResult<IReadOnlyList<KeyRange>>([new KeyRange(0, null, null, PrimaryKey)]);
            }

            var primaryKey = PrimaryKey
                ?? throw new DeliveryException($"Flow '{_flow.Name}': a read is cut into slices on the record table's identity primary key, and the flow names none under source.record.primaryKey.");
            var ids = candidates.Select(IdOf).Order().ToList();
            var size = (int)Math.Ceiling(ids.Count / (double)slices);
            var ranges = new List<KeyRange>();
            KeyTuple? from = null;
            for (var cut = size - 1; cut < ids.Count - 1; cut += size)
            {
                var to = KeyTuple.Of(ids[cut].ToString(CultureInfo.InvariantCulture));
                ranges.Add(new KeyRange(ranges.Count, from, to, primaryKey));
                from = to;
            }

            ranges.Add(new KeyRange(ranges.Count, from, null, primaryKey));
            return Task.FromResult<IReadOnlyList<KeyRange>>(ranges);
        }

        public async IAsyncEnumerable<SourceRecord> ReadAsync(SourceHeader header, KeyRange? range, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(header);
            await Task.CompletedTask.ConfigureAwait(false);
            if (range is { } slice && !string.Equals(slice.On, PrimaryKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new DeliveryException($"Flow '{_flow.Name}': slice {slice.Slice} was cut on {slice.On ?? "the record key"}, and the flow reads by {PrimaryKey ?? "the record key"}.");
            }

            var ordered = PrimaryKey is null
                ? Candidates(header).OrderBy(r => KeyOf(r).Json, StringComparer.Ordinal)
                : Candidates(header).OrderBy(IdOf);
            foreach (var record in ordered)
            {
                ct.ThrowIfCancellationRequested();
                var key = KeyOf(record);
                if (range is { } bounds && !Within(record, key, bounds))
                {
                    continue;
                }

                yield return Build(record, key);
            }
        }

        /// <summary>Whether a record falls in a range of the order the read pages in.</summary>
        private bool Within(MemoryRecord record, KeyTuple key, KeyRange bounds)
        {
            if (PrimaryKey is null)
            {
                return (bounds.From is not { } from || string.CompareOrdinal(key.Json, from.Json) > 0)
                    && (bounds.To is not { } to || string.CompareOrdinal(key.Json, to.Json) <= 0);
            }

            var id = IdOf(record);
            return (bounds.From is not { } lower || id > long.Parse(lower.Values[0], CultureInfo.InvariantCulture))
                && (bounds.To is not { } upper || id <= long.Parse(upper.Values[0], CultureInfo.InvariantCulture));
        }

        /// <summary>A record's identity primary key, which every row of a table that names one carries.</summary>
        private long IdOf(MemoryRecord record)
        {
            var column = PrimaryKey ?? throw new InvalidOperationException("The flow names no primary key.");
            return record.Row.TryGetValue(column, out var value) && value is not null
                ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
                : throw new DeliveryException($"A record row of flow '{_flow.Name}' has no value in its primary key column '{column}'.");
        }

        private SourceRecord Build(MemoryRecord record, KeyTuple key)
        {
            var datasets = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
            var versions = new Dictionary<string, DatasetVersion>(StringComparer.Ordinal);
            string? hold = null;
            foreach (var (name, dataset) in _flow.Source.Datasets)
            {
                var rows = record.Datasets.TryGetValue(name, out var children) ? children : [];
                if (rows.Count > dataset.MaxRowsPerRecord)
                {
                    hold ??= $"child dataset '{name}' holds more than {dataset.MaxRowsPerRecord.ToString(CultureInfo.InvariantCulture)} rows for this record, "
                        + $"which is source.datasets.{name}.maxRowsPerRecord; raise the ceiling deliberately, or split the record in the source";
                    rows = rows.Take(dataset.MaxRowsPerRecord).ToList();
                }

                datasets[name] = rows.Select(r => new SourceRow(new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase))).ToList();
                versions[name] = new DatasetVersion(rows.Count, record.DatasetUpdatedUtc.TryGetValue(name, out var updated) ? updated : record.UpdatedUtc);
            }

            return new SourceRecord
            {
                Row = new SourceRow(new Dictionary<string, object?>(record.Row, StringComparer.OrdinalIgnoreCase)),
                Scopes = datasets,
                Origin = new SourceOrigin(record.FileName, record.RowNumber, record.UpdatedUtc, record.InsertedUtc),
                Version = SourceVersion.Of(IngestionFingerprint.Of(record.UpdatedUtc, versions)),
                SourceKeyJson = key.Json,
                DeletedUtc = record.DeletedUtc,
                Hold = hold,
            };
        }

        private IEnumerable<MemoryRecord> Candidates(SourceHeader header)
        {
            if (header.Selection.Kind == SourceSelectionKind.Keys)
            {
                var wanted = header.Selection.Keys.ToHashSet();
                return _tables.Records.Where(r => InScope(r) && wanted.Contains(KeyOf(r)));
            }

            return _tables.Records.Where(r => InScope(r) && InWindow(r, header.Window, header.Selection.Kind));
        }

        private bool InWindow(MemoryRecord record, SourceWindow window, SourceSelectionKind kind)
        {
            if (kind != SourceSelectionKind.Incremental)
            {
                return true;
            }

            // The row changed in the window, or was soft-deleted in it: the ingestion flow tags a row deleted without
            // touching its update column. A record whose own row did not change is still a candidate when a row of one of the
            // flow's child datasets changed or was soft-deleted in it, as the SQL Server source reads it (IngestionSql), since
            // the record's document is built from those rows too; DatasetUpdatedUtc is when the newest of them moved.
            return Within(record.UpdatedUtc, window)
                || (record.DeletedUtc is { } deleted && Within(deleted, window))
                || _flow.Source.Datasets.Keys.Any(name => record.DatasetUpdatedUtc.TryGetValue(name, out var changed) && Within(changed, window));
        }

        private static bool Within(DateTime moment, SourceWindow window)
            => moment <= window.UpperUtc && (window.LowerUtc is not { } lower || moment > lower);

        private bool InScope(MemoryRecord record)
        {
            foreach (var (column, parameter) in _flow.Source.Record.Scope)
            {
                var holds = SourceRow.Stringify(record.Row.TryGetValue(column, out var held) ? held : null);
                var inScope = _values.TryGetValue(parameter, out var value)
                    ? RunValueList.Items(value).Contains(holds, StringComparer.Ordinal)
                    : holds is null;
                if (!inScope)
                {
                    return false;
                }
            }

            return true;
        }

        private KeyTuple KeyOf(MemoryRecord record)
            => new(_flow.Source.Record.Key
                .Select(column => SourceRow.Stringify(record.Row.TryGetValue(column, out var value) ? value : null) ?? string.Empty)
                .ToList());

        private IReadOnlyDictionary<string, IReadOnlySet<string>> Columns()
        {
            var columns = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [SourceDatasets.Record] = _tables.Records
                    .SelectMany(r => r.Row.Keys)
                    .Concat(_flow.Source.Record.Key)
                    .Concat(_flow.Source.LastModified is { } lastModified ? [lastModified] : Array.Empty<string>())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
            };

            foreach (var (name, _) in _flow.Source.Datasets)
            {
                columns[name] = _tables.Records
                    .SelectMany(r => r.Datasets.TryGetValue(name, out var rows) ? rows.SelectMany(row => row.Keys) : [])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            return columns;
        }
    }
}
