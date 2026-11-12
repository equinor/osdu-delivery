using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>What one record's bulk data came to against one assertion.</summary>
/// <param name="Passed">Whether the record's bulk data meets the assertion.</param>
/// <param name="Value">What the record held where it failed (a row and its value, an aggregate), or null.</param>
/// <param name="Reason">Why it failed; empty when it passed.</param>
/// <param name="WithinBand">For one that failed: whether it missed only within the assertion's warning band, so it warns.</param>
public sealed record BulkVerdict(bool Passed, string? Value, string Reason, bool WithinBand = false)
{
    /// <summary>
    /// What the record's bulk data came to where the assertion reads one thing of it (its row count, its columns, an
    /// aggregate of a column), whether it holds or not; null for an assertion that judges it row by row.
    /// </summary>
    public string? Read { get; init; }

    public static BulkVerdict Pass { get; } = new(true, null, string.Empty);

    public static BulkVerdict Fail(string? value, string reason) => new(false, value, reason);

    /// <summary>A record's bulk data that misses the assertion by no more than its warning band: it does not hold, and only warns.</summary>
    public static BulkVerdict Warn(string? value, string reason) => new(false, value, reason, WithinBand: true);
}

/// <summary>One record's bulk data held to one assertion: fed the record's rows a page at a time, then settled.</summary>
public abstract class BulkCheck
{
    /// <summary>True when the check reads the record's rows; one that the shape settles reads none.</summary>
    public virtual bool ReadsRows => false;

    public virtual void Observe(BulkFrame frame)
    {
    }

    public abstract BulkVerdict Finish();
}

/// <summary>
/// An assertion on each record's bulk data (osdu/docs/reference/flow/assertion.md, Bulk data): every record the test
/// reads is held to it on its own, the records at once as the flow's concurrency allows, and the assertion holds when
/// every record's bulk data meets it. The examples are the records that failed, with the row or value that failed them.
/// </summary>
public abstract class BulkEvaluator : Evaluator
{
    private readonly Lock _gate = new();
    private readonly List<AssertionExample> _examples = [];
    private readonly ReadingLog _read = new();
    private long _records;
    private long _failing;
    private long _withinBand;

    protected BulkEvaluator(int index, TestAssertion assertion, int examples)
        : base(index, assertion, examples)
    {
    }

    public override RecordNeed Need => RecordNeed.Ids;

    /// <summary>The columns whose values the evaluator reads; none when the shape of the bulk data settles it.</summary>
    public virtual IReadOnlyList<string> Columns => [];

    /// <summary>What the assertion reads of each record's bulk data, as a value read names it (<see cref="BulkVerdict.Read"/>).</summary>
    protected virtual string Reads => Assertion.Type;

    /// <summary>A check of one record's bulk data, whose shape is <paramref name="shape"/> (null when the DDMS holds none for it).</summary>
    public abstract BulkCheck Start(string id, BulkShape? shape, long? rowsNotRead);

    /// <summary>Records what one record's bulk data came to. Called for records at once.</summary>
    public void Settle(string id, BulkVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        lock (_gate)
        {
            _records++;
            if (verdict.Read is { } read)
            {
                _read.Add(Reads, verdict.Passed, read, id);
            }

            if (!verdict.Passed)
            {
                _failing++;
                if (verdict.WithinBand)
                {
                    _withinBand++;
                }

                if (_examples.Count < Examples)
                {
                    _examples.Add(new AssertionExample(id, verdict.Value is null ? null : TestResults.Quote(verdict.Value), verdict.Reason));
                }
            }
        }
    }

    public override Task<AssertionOutcome> CompleteAsync(TestScope scope, TestSubject subject, CancellationToken ct)
    {
        if (subject.NotRead is not null)
        {
            return Task.FromResult(NotRead(subject));
        }

        lock (_gate)
        {
            var passed = _records > 0 && _failing == 0;
            var actual = string.Create(CultureInfo.InvariantCulture, $"{_records - _failing} of {_records} record(s)' bulk data meet it");
            var message = _records == 0
                ? "No record was there whose bulk data could be checked."
                : string.Create(CultureInfo.InvariantCulture, $"The bulk data of {_failing} of {_records} record(s) does not meet it.");
            var outcome = Result(passed, actual, message, _records, _failing, _records == 0 ? null : 100.0 * (_records - _failing) / _records, _examples.ToList(), _read);

            // Every record that misses, misses within the warning band: the assertion warns.
            return Task.FromResult(!passed && _records > 0 && _withinBand == _failing
                ? Warned(outcome, string.Create(CultureInfo.InvariantCulture, $"each of the {_failing} misses it within the warning band, so it warns"))
                : outcome);
        }
    }

    /// <summary>
    /// A cell of a frame as a value: null for an absent value, which the DDMS writes as null, or for a float column as the
    /// text "NaN" (seen live on ADME), and null for a value that is not a finite number.
    /// </summary>
    internal static JsonNode? Cell(JsonElement row, int column)
    {
        if (row.ValueKind != JsonValueKind.Array || column < 0 || column >= row.GetArrayLength())
        {
            return null;
        }

        var cell = row[column];
        return cell.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Number => cell.TryGetDouble(out var number) && double.IsFinite(number) ? JsonValue.Create(number) : null,
            JsonValueKind.String when cell.ValueEquals("NaN") || cell.ValueEquals("Infinity") || cell.ValueEquals("-Infinity") => null,
            JsonValueKind.String => JsonValue.Create(cell.GetString()),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            _ => JsonNode.Parse(cell.GetRawText()),
        };
    }

    /// <summary>The failure of a record whose bulk data is not there, or holds more rows than the test reads.</summary>
    internal static BulkVerdict? Unreadable(BulkShape? shape, IReadOnlyList<string> columns, long? rowsNotRead)
    {
        if (shape is null)
        {
            return BulkVerdict.Fail(null, "the DDMS holds no bulk data for the record");
        }

        var absent = columns.Where(c => !shape.Columns.Contains(c, StringComparer.Ordinal)).ToList();
        if (absent.Count > 0)
        {
            return BulkVerdict.Fail(string.Join(", ", shape.Columns.Take(20)), $"its bulk data has no column {string.Join(", ", absent)}");
        }

        return rowsNotRead is { } rows
            ? BulkVerdict.Fail(null, string.Create(CultureInfo.InvariantCulture, $"its bulk data holds {shape.Rows} rows, more than the test's bulk.maxRows {rows}; raise it, or set sample: true to read the first rows"))
            : null;
    }

    /// <summary>The position of a column in a frame, or -1.</summary>
    internal static int IndexOf(BulkFrame frame, string column)
    {
        for (var i = 0; i < frame.Columns.Count; i++)
        {
            if (string.Equals(frame.Columns[i], column, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The failure of a record one of whose pages the DDMS answered without a column the read asked for (its description
    /// listed the column, and the page left it out): its values cannot be judged, so the record fails saying so rather than
    /// being judged as holding no value there.
    /// </summary>
    internal static BulkVerdict Unanswered(string column, long offset)
        => BulkVerdict.Fail(null, string.Create(CultureInfo.InvariantCulture,
            $"the DDMS answered the page of its bulk data from row {offset + 1} without the column {column}, which the read asked for and its description lists"));
}

/// <summary>How many rows each record's bulk data holds.</summary>
internal sealed class RowCountEvaluator(int index, RowCountAssertion assertion, int examples) : BulkEvaluator(index, assertion, examples)
{
    protected override string Reads => "row count";

    public override BulkCheck Start(string id, BulkShape? shape, long? rowsNotRead) => new Settled(shape is null
        ? BulkVerdict.Fail(null, "the DDMS holds no bulk data for the record")
        : (ValueComparer.Evaluate(assertion.Comparison, Measured.Of(shape.Rows), null)
            ? BulkVerdict.Pass
            : BulkVerdict.Fail(shape.Rows.ToString(CultureInfo.InvariantCulture), string.Create(CultureInfo.InvariantCulture, $"holds {shape.Rows} row(s); expected {assertion.Comparison}")))
            with { Read = shape.Rows.ToString(CultureInfo.InvariantCulture) });

    /// <summary>A check the record's shape already settled.</summary>
    internal sealed class Settled(BulkVerdict verdict) : BulkCheck
    {
        public override BulkVerdict Finish() => verdict;
    }
}

/// <summary>The columns each record's bulk data holds.</summary>
internal sealed class ColumnsEvaluator(int index, ColumnsAssertion assertion, int examples) : BulkEvaluator(index, assertion, examples)
{
    protected override string Reads => "columns";

    public override BulkCheck Start(string id, BulkShape? shape, long? rowsNotRead)
    {
        if (shape is null)
        {
            return new RowCountEvaluator.Settled(BulkVerdict.Fail(null, "the DDMS holds no bulk data for the record"));
        }

        var held = new HashSet<string>(shape.Columns, StringComparer.Ordinal);
        var problems = new List<string>();
        var missing = assertion.Includes.Concat(assertion.Exactly).Where(c => !held.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            problems.Add("has no column " + string.Join(", ", missing));
        }

        var excluded = assertion.Excludes.Where(held.Contains).ToList();
        if (excluded.Count > 0)
        {
            problems.Add("has the column(s) " + string.Join(", ", excluded) + ", which must not be there");
        }

        if (assertion.Exactly.Count > 0)
        {
            var extra = shape.Columns.Where(c => !assertion.Exactly.Contains(c, StringComparer.Ordinal)).ToList();
            if (extra.Count > 0)
            {
                problems.Add("has the column(s) " + string.Join(", ", extra) + " beside those expected");
            }
        }

        var listed = string.Join(", ", shape.Columns.Take(30)) + (shape.Columns.Count > 30 ? ", ..." : string.Empty);
        return new RowCountEvaluator.Settled((problems.Count == 0 ? BulkVerdict.Pass : BulkVerdict.Fail(listed, string.Join("; ", problems))) with { Read = listed });
    }
}

/// <summary>A condition on the values of a column, held for the share of each record's rows the quantifier asks.</summary>
internal sealed class ColumnValueEvaluator(int index, ValueAssertion assertion, int examples) : BulkEvaluator(index, assertion, examples)
{
    public override IReadOnlyList<string> Columns { get; } =
        assertion.Where.Select(w => w.Target.Path).Prepend(assertion.Target.Path).Distinct(StringComparer.Ordinal).ToList();

    public override BulkCheck Start(string id, BulkShape? shape, long? rowsNotRead)
        => Unreadable(shape, Columns, rowsNotRead) is { } failure ? new RowCountEvaluator.Settled(failure) : new Check(assertion);

    private sealed class Check(ValueAssertion assertion) : BulkCheck
    {
        private readonly Tally _tally = new(assertion.For, 1);
        private BulkVerdict? _unanswered;

        public override bool ReadsRows => true;

        public override void Observe(BulkFrame frame)
        {
            if (_unanswered is not null)
            {
                return;
            }

            var column = IndexOf(frame, assertion.Target.Path);
            var filters = assertion.Where.Select(w => (w, IndexOf(frame, w.Target.Path))).ToList();
            if (column < 0 || filters.Exists(f => f.Item2 < 0))
            {
                _unanswered = Unanswered(column < 0 ? assertion.Target.Path : filters.First(f => f.Item2 < 0).w.Target.Path, frame.Offset);
                return;
            }

            var position = frame.Offset;
            foreach (var row in frame.Data.EnumerateArray())
            {
                position++;
                if (filters.Count > 0 && !Selected(assertion.Where, target =>
                {
                    var at = filters.First(f => ReferenceEquals(f.w.Target, target)).Item2;
                    return Cell(row, at) is { } v ? [v] : [];
                }))
                {
                    _tally.Filtered++;
                    continue;
                }

                var value = Cell(row, column);
                var shown = string.Create(CultureInfo.InvariantCulture, $"row {position}: {(value is null ? "(no value)" : RecordValues.Text(value))}");
                var condition = assertion.Condition;
                if (value is null)
                {
                    switch (condition.Operator)
                    {
                        case ValueOperator.Exists:
                            _tally.Add(!condition.Flag, null, shown, "has no value");
                            break;
                        case ValueOperator.Empty:
                            _tally.Add(condition.Flag, null, shown, "has no value, and is expected to hold one");
                            break;
                        default:
                            if (assertion.Optional)
                            {
                                _tally.Skipped++;
                            }
                            else
                            {
                                _tally.Add(false, null, shown, "has no value");
                            }

                            break;
                    }

                    continue;
                }

                bool holds;
                string reason;
                if (condition.Operator == ValueOperator.Exists)
                {
                    holds = condition.Flag;
                    reason = "has a value, and is expected to have none";
                }
                else
                {
                    holds = ValueComparer.Holds(condition, value, out reason);
                }

                _tally.Add(holds, null, shown, reason);
            }
        }

        public override BulkVerdict Finish()
        {
            if (_unanswered is not null)
            {
                return _unanswered;
            }

            if (_tally.Passed)
            {
                return BulkVerdict.Pass;
            }

            var first = _tally.Examples.Count > 0 ? _tally.Examples[0] : null;
            return BulkVerdict.Fail(first?.Value, _tally.Message("row") + (first is null ? string.Empty : " First: " + first.Reason));
        }
    }
}

/// <summary>An aggregate of a column over each record's rows, compared record by record.</summary>
internal sealed class ColumnAggregateEvaluator(int index, AggregateAssertion assertion, int examples) : BulkEvaluator(index, assertion, examples)
{
    protected override string Reads => $"{AssertionText.Of(assertion.Function)}({assertion.Target})";

    public override IReadOnlyList<string> Columns { get; } = [assertion.Target.Path];

    public override BulkCheck Start(string id, BulkShape? shape, long? rowsNotRead)
        => Unreadable(shape, Columns, rowsNotRead) is { } failure ? new RowCountEvaluator.Settled(failure) : new Check(assertion);

    private sealed class Check(AggregateAssertion assertion) : BulkCheck
    {
        private readonly Aggregator _aggregator = new(assertion.Function == AggregateFunction.Distinct);
        private long _missing;
        private long _leftOut;
        private BulkVerdict? _unanswered;

        public override bool ReadsRows => true;

        public override void Observe(BulkFrame frame)
        {
            if (_unanswered is not null)
            {
                return;
            }

            var column = IndexOf(frame, assertion.Target.Path);
            if (column < 0)
            {
                _unanswered = Unanswered(assertion.Target.Path, frame.Offset);
                return;
            }

            foreach (var row in frame.Data.EnumerateArray())
            {
                if (Cell(row, column) is not { } value)
                {
                    _missing++;
                }
                else if (AggregateEvaluator.LeftOut(assertion, value))
                {
                    _leftOut++;
                    _missing++;
                }
                else
                {
                    _aggregator.Add(value);
                }
            }
        }

        public override BulkVerdict Finish()
        {
            if (_unanswered is not null)
            {
                return _unanswered;
            }

            var (measured, why) = _aggregator.Measure(assertion.Function, _missing, assertion.Comparison);
            if (measured is not { } value)
            {
                return BulkVerdict.Fail(null, why);
            }

            if (ValueComparer.Evaluate(assertion.Comparison, value, assertion.Tolerance))
            {
                return BulkVerdict.Pass with { Read = value.ToString() };
            }

            var reason = $"{AssertionText.Of(assertion.Function)}({assertion.Target}) is {value}{_aggregator.Note(assertion.Function)}{AggregateEvaluator.LeftOutNote(assertion, _leftOut)}; expected {assertion.Expected}";
            return (assertion.WarnWithin is { } band && ValueComparer.Within(assertion.Comparison, value, assertion.Tolerance, band)
                ? BulkVerdict.Warn(value.ToString(), reason + "; " + Evaluator.WithinBand(band, ValueComparer.Miss(assertion.Comparison, value, assertion.Tolerance)))
                : BulkVerdict.Fail(value.ToString(), reason)) with { Read = value.ToString() };
        }
    }
}

/// <summary>A column's values rise (or fall) row after row in each record's bulk data; absent values are passed over.</summary>
internal sealed class MonotonicEvaluator(int index, MonotonicAssertion assertion, int examples) : BulkEvaluator(index, assertion, examples)
{
    public override IReadOnlyList<string> Columns { get; } = [assertion.Column];

    public override BulkCheck Start(string id, BulkShape? shape, long? rowsNotRead)
        => Unreadable(shape, Columns, rowsNotRead) is { } failure ? new RowCountEvaluator.Settled(failure) : new Check(assertion);

    private sealed class Check(MonotonicAssertion assertion) : BulkCheck
    {
        private double? _previous;
        private long _previousRow;
        private BulkVerdict? _failure;
        private long _values;
        private long _nonNumbers;

        public override bool ReadsRows => true;

        public override void Observe(BulkFrame frame)
        {
            if (_failure is not null)
            {
                return;
            }

            var column = IndexOf(frame, assertion.Column);
            if (column < 0)
            {
                _failure = Unanswered(assertion.Column, frame.Offset);
                return;
            }

            var position = frame.Offset;
            foreach (var row in frame.Data.EnumerateArray())
            {
                position++;
                if (Cell(row, column) is not { } cell)
                {
                    continue;
                }

                if (!RecordValues.TryNumber(cell, out var value))
                {
                    _nonNumbers++;
                    continue;
                }

                _values++;
                if (_previous is { } before)
                {
                    var holds = assertion.Direction switch
                    {
                        MonotonicDirection.Increasing => value >= before,
                        MonotonicDirection.Decreasing => value <= before,
                        MonotonicDirection.StrictlyIncreasing => value > before,
                        _ => value < before,
                    };
                    if (!holds)
                    {
                        _failure = BulkVerdict.Fail(
                            string.Create(CultureInfo.InvariantCulture, $"row {_previousRow}: {RecordValues.Format(before)}, row {position}: {RecordValues.Format(value)}"),
                            string.Create(CultureInfo.InvariantCulture, $"is not {AssertionText.Of(assertion.Direction)} at row {position}"));
                        return;
                    }
                }

                _previous = value;
                _previousRow = position;
            }
        }

        public override BulkVerdict Finish()
        {
            if (_failure is not null)
            {
                return _failure;
            }

            if (_values == 0)
            {
                return BulkVerdict.Fail(null, _nonNumbers > 0 ? $"none of the values of {assertion.Column} is a number" : $"{assertion.Column} holds no value");
            }

            return BulkVerdict.Pass;
        }
    }
}

/// <summary>Reads what each bulk evaluator of a test needs of one record's bulk data and settles every one of them.</summary>
internal static class BulkReader
{
    /// <summary>Holds one record's bulk data to every bulk assertion of the test.</summary>
    public static async Task EvaluateAsync(TestScope scope, IReadOnlyList<BulkEvaluator> evaluators, string id, CancellationToken ct)
    {
        var bulk = scope.Test.Bulk!;
        var shape = await scope.Bulk.DescribeAsync(bulk.Collection, id, ct).ConfigureAwait(false);
        long? rowsNotRead = shape is { } s && s.Rows > bulk.MaxRows && !scope.Test.Sample ? bulk.MaxRows : null;
        var checks = evaluators.Select(e => (Evaluator: e, Check: e.Start(id, shape, rowsNotRead))).ToList();
        var reading = checks.Where(c => c.Check.ReadsRows).ToList();
        if (shape is not null && reading.Count > 0)
        {
            // The columns the test lists and every column a reading assertion names: an assertion's own columns are always read,
            // whatever the list says (the document is refused when its list leaves one out), so no check reads a column the
            // read did not ask for. A column the record's description does not list fails its checks at Start, unread.
            var columns = bulk.Columns.Concat(reading.SelectMany(c => c.Evaluator.Columns))
                .Distinct(StringComparer.Ordinal)
                .Where(c => shape.Columns.Contains(c, StringComparer.Ordinal))
                .ToList();
            var rows = Math.Min(shape.Rows, bulk.MaxRows);
            await foreach (var frame in scope.Bulk.ReadAsync(bulk.Collection, id, columns, rows, Math.Max(1, columns.Count), ct).ConfigureAwait(false))
            {
                foreach (var (_, check) in reading)
                {
                    check.Observe(frame);
                }
            }
        }

        foreach (var (evaluator, check) in checks)
        {
            evaluator.Settle(id, check.Finish());
        }
    }
}
