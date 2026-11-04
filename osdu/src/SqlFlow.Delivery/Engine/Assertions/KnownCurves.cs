using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>
/// What one record's bulk data comes to for the curve proof values known values hold it to (osdu/docs/reference/flow/
/// curve-proof-values.md): each curve's measures and its values at the keys asked for, read in one pass over the curves the
/// rows name and the curve the rows are indexed by; or why its curves cannot be measured at all.
/// </summary>
internal sealed class CurveReading
{
    /// <summary>Why none of the record's curves can be measured (no bulk data, more rows than the test reads, a short read), or null.</summary>
    public string? Failure { get; init; }

    /// <summary>The curves the record's bulk data holds, as the DDMS describes it.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>The rows the DDMS describes the record's bulk data as holding.</summary>
    public long Rows { get; init; }

    /// <summary>The curve the rows are indexed by, when a first or last proof value needs it and the bulk data holds it.</summary>
    public string? Index { get; init; }

    /// <summary>Why a first or last value cannot be read: the record names no index curve, or its bulk data does not hold it.</summary>
    public string? IndexFailure { get; init; }

    /// <summary>The measures of each curve the rows name that the bulk data holds.</summary>
    public IReadOnlyDictionary<string, CurveTally> Curves { get; init; } = new Dictionary<string, CurveTally>(StringComparer.Ordinal);

    /// <summary>What was read of the keys the rows' at proof values ask for, or null when none does.</summary>
    public CurveKeyReading? Keys { get; init; }
}

/// <summary>
/// The measures of one curve, taken row by row: its values aggregated as a column aggregate takes them (numbers as numbers,
/// ISO 8601 dates as instants), the rows without a value, the index at its first and last value, and the differences between
/// its numbers row after row.
/// </summary>
internal sealed class CurveTally(bool steps)
{
    private double? _previous;

    public Aggregator Values { get; } = new(distinct: false);

    public long Missing { get; private set; }

    /// <summary>Whether the curve has a value in any row.</summary>
    public bool Seen { get; private set; }

    /// <summary>The index at the first row the curve has a value in, and that row (from 1); null when the index holds no value there.</summary>
    public JsonNode? First { get; private set; }

    public long FirstRow { get; private set; }

    /// <summary>The index at the last row the curve has a value in, and that row.</summary>
    public JsonNode? Last { get; private set; }

    public long LastRow { get; private set; }

    /// <summary>The differences measured between the curve's numbers row after row, and the smallest and largest with the rows they end at.</summary>
    public long Steps { get; private set; }

    public double StepMin { get; private set; } = double.PositiveInfinity;

    public long StepMinRow { get; private set; }

    public double StepMax { get; private set; } = double.NegativeInfinity;

    public long StepMaxRow { get; private set; }

    /// <summary>Values that are not numbers, which have no step to the value before them.</summary>
    public long NotNumbers { get; private set; }

    public void Observe(JsonNode? value, JsonNode? index, long row)
    {
        if (value is null)
        {
            Missing++;
            return;
        }

        Values.Add(value);
        if (!Seen)
        {
            Seen = true;
            First = index;
            FirstRow = row;
        }

        Last = index;
        LastRow = row;
        if (!steps)
        {
            return;
        }

        // A number, or text holding one, as the curve's aggregates read it.
        if (!RecordValues.TryNumber(value, out var number) && !(RecordValues.TryText(value, out var text) && RecordValues.TryParseNumber(text, out number)))
        {
            NotNumbers++;
            return;
        }

        if (_previous is { } before)
        {
            var step = Difference(before, number);
            Steps++;
            if (step < StepMin)
            {
                StepMin = step;
                StepMinRow = row;
            }

            if (step > StepMax)
            {
                StepMax = step;
                StepMaxRow = row;
            }
        }

        _previous = number;
    }

    /// <summary>
    /// The difference between two numbers as the DDMS wrote them: taken in decimal from each one's shortest text, so depths
    /// written 0.1524 apart are 0.1524 apart, not 0.15240000000002 as their binary values differ; in binary for numbers beyond
    /// what a decimal holds.
    /// </summary>
    internal static double Difference(double before, double after)
    {
        if (decimal.TryParse(RecordValues.Format(before), NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            && decimal.TryParse(RecordValues.Format(after), NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
        {
            try
            {
                return (double)(a - b);
            }
            catch (OverflowException)
            {
                return after - before;
            }
        }

        return after - before;
    }
}

/// <summary>
/// Curve proof values (osdu/docs/reference/flow/curve-proof-values.md): reads one record's bulk data for the
/// curve proof values its rows state, through the Wellbore DDMS route the bulk assertions read by (its description, then only
/// the curves needed, a page at a time), and says which proof values the measures do not hold.
/// </summary>
internal static class KnownCurves
{
    /// <summary>The curves a reason names of a record's bulk data, when a curve is not among them.</summary>
    private const int NamedCurves = 30;

    /// <summary>
    /// Measures the curves <paramref name="checks"/> name in the bulk data of the record <paramref name="id"/>, with the curve
    /// its rows are indexed by when a first, last or at proof value needs it: <paramref name="curveIndex"/>, or the curve the
    /// record names at <see cref="CurveProofValues.ReferenceCurvePath"/>.
    /// </summary>
    public static async Task<CurveReading> MeasureAsync(
        TestScope scope, string id, JsonNode record, IReadOnlyCollection<CurveCheck> checks, string? curveIndex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(checks);
        var bulk = scope.Test.Bulk
            ?? throw new InvalidOperationException($"Test '{scope.Test.Name}' holds curve proof values and has no bulk block; the document is refused without one.");
        var shape = await scope.Bulk.DescribeAsync(bulk.Collection, id, ct).ConfigureAwait(false);
        if (shape is null)
        {
            return new CurveReading { Failure = "the DDMS holds no bulk data for the record" };
        }

        if (shape.Rows > bulk.MaxRows)
        {
            return new CurveReading
            {
                Columns = shape.Columns,
                Rows = shape.Rows,
                Failure = string.Create(CultureInfo.InvariantCulture,
                    $"its bulk data holds {shape.Rows} rows, more than the test's bulk.maxRows {bulk.MaxRows}, and curve proof values are measured over every row; raise bulk.maxRows (at most {AssertionBulk.MaxRowsCeiling})"),
            };
        }

        string? index = null;
        string? indexFailure = null;
        if (checks.Any(c => CurveProofValues.ReadsIndex(c.Measure)))
        {
            index = curveIndex ?? ReferenceCurve(record);
            if (index is null)
            {
                indexFailure = $"the record names no {CurveProofValues.ReferenceCurvePath}, so the curve its rows are indexed by is not known; name it with the assertion's curveIndex";
            }
            else if (!shape.Columns.Contains(index, StringComparer.Ordinal))
            {
                indexFailure = $"the curve its rows are indexed by, {index} ({(curveIndex is null ? "its " + CurveProofValues.ReferenceCurvePath : "curveIndex")}), is not among the curves of its bulk data";
                index = null;
            }
        }

        var steps = checks.Where(c => c.Measure == CurveMeasure.Step).Select(c => c.Curve).ToHashSet(StringComparer.Ordinal);
        var tallies = checks.Select(c => c.Curve)
            .Distinct(StringComparer.Ordinal)
            .Where(c => shape.Columns.Contains(c, StringComparer.Ordinal))
            .ToDictionary(c => c, c => new CurveTally(steps.Contains(c)), StringComparer.Ordinal);
        var read = tallies.Keys.Concat(index is null ? [] : [index]).Distinct(StringComparer.Ordinal).ToList();
        var keys = new CurveKeyReading(checks);
        string? failure = null;
        if (read.Count > 0 && shape.Rows > 0)
        {
            long rows = 0;
            await foreach (var frame in scope.Bulk.ReadAsync(bulk.Collection, id, read, shape.Rows, read.Count, ct).ConfigureAwait(false))
            {
                var positions = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var curve in read)
                {
                    var at = BulkEvaluator.IndexOf(frame, curve);
                    if (at < 0)
                    {
                        failure = BulkEvaluator.Unanswered(curve, frame.Offset).Reason;
                        break;
                    }

                    positions[curve] = at;
                }

                if (failure is not null)
                {
                    break;
                }

                var indexAt = index is null ? -1 : positions[index];
                var position = frame.Offset;
                foreach (var row in frame.Data.EnumerateArray())
                {
                    position++;
                    rows++;
                    var indexValue = indexAt < 0 ? null : BulkEvaluator.Cell(row, indexAt);
                    foreach (var (curve, tally) in tallies)
                    {
                        tally.Observe(BulkEvaluator.Cell(row, positions[curve]), indexValue, position);
                    }

                    if (keys.Asks && indexAt >= 0)
                    {
                        var current = row;
                        keys.Observe(indexValue, position, curve => positions.TryGetValue(curve, out var at) ? BulkEvaluator.Cell(current, at) : null);
                    }
                }
            }

            if (failure is null && rows != shape.Rows)
            {
                failure = string.Create(CultureInfo.InvariantCulture,
                    $"the DDMS describes its bulk data as {shape.Rows} rows and answered {rows}, so its curves cannot be measured whole");
            }
        }

        return new CurveReading
        {
            Failure = failure,
            Columns = shape.Columns,
            Rows = shape.Rows,
            Index = index,
            IndexFailure = indexFailure,
            Curves = tallies,
            Keys = keys.Asks ? keys : null,
        };
    }

    /// <summary>
    /// The curve proof values of a row the record's bulk data does not hold, each with what it holds instead and whether it
    /// misses only within its warning band: one difference for bulk data that cannot be measured at all, one for each curve
    /// it does not hold, and one for the first, last and at values its index cannot give. Disabled ones are not checked.
    /// </summary>
    public static List<(string Path, string Why, bool WithinBand)> Differences(IReadOnlyList<CurveCheck> checks, CurveReading reading)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(reading);
        var enabled = checks.Where(c => c.Disabled is null).ToList();
        var differences = new List<(string, string, bool)>();
        if (enabled.Count == 0)
        {
            return differences;
        }

        if (reading.Failure is { } failure)
        {
            differences.Add(("bulk data", "its curves cannot be measured: " + failure, false));
            return differences;
        }

        foreach (var curve in enabled.Select(c => c.Curve).Distinct(StringComparer.Ordinal).Where(c => !reading.Curves.ContainsKey(c)))
        {
            differences.Add((curve, $"{curve} is not among the curves of its bulk data ({Listed(reading.Columns)})", false));
        }

        var held = enabled.Where(c => reading.Curves.ContainsKey(c.Curve)).ToList();
        var indexed = held.Where(c => CurveProofValues.ReadsIndex(c.Measure)).ToList();
        if (indexed.Count > 0 && reading.IndexFailure is { } unindexed)
        {
            var labels = string.Join(", ", indexed.Select(c => c.Label));
            differences.Add((labels, $"{labels} cannot be read: {unindexed}", false));
            held = held.Except(indexed).ToList();
        }

        foreach (var check in held)
        {
            if (Why(check, reading.Curves[check.Curve], reading) is { } why)
            {
                differences.Add((check.Label, why.Why, why.WithinBand));
            }
        }

        return differences;
    }

    /// <summary>Why a curve's measure does not meet its proof value, and whether it misses only within its warning band; null when it meets it.</summary>
    internal static (string Why, bool WithinBand)? Why(CurveCheck check, CurveTally tally, CurveReading reading)
    {
        var condition = check.Condition;
        JsonNode found;
        var note = string.Empty;
        switch (check.Measure)
        {
            case CurveMeasure.Count:
                found = JsonValue.Create((double)tally.Values.Count);
                break;
            case CurveMeasure.Missing:
                found = JsonValue.Create((double)tally.Missing);
                break;
            case CurveMeasure.First or CurveMeasure.Last:
                var first = check.Measure == CurveMeasure.First;
                if (!tally.Seen)
                {
                    return ($"{check.Label} has no value: {check.Curve} holds no value in any of its {Rows(reading.Rows)}; expected it {condition}", false);
                }

                if ((first ? tally.First : tally.Last) is not { } at)
                {
                    return (string.Create(CultureInfo.InvariantCulture,
                        $"{check.Label} has no value: the index curve {reading.Index} holds none in row {(first ? tally.FirstRow : tally.LastRow)}, where {check.Curve} has its {(first ? "first" : "last")} value; expected it {condition}"), false);
                }

                found = at;
                note = string.Create(CultureInfo.InvariantCulture, $" (row {(first ? tally.FirstRow : tally.LastRow)})");
                break;
            case CurveMeasure.Step:
                return Steps(check, tally);
            case CurveMeasure.At:
                return At(check, reading);
            default:
                var function = check.Measure switch
                {
                    CurveMeasure.Min => AggregateFunction.Min,
                    CurveMeasure.Max => AggregateFunction.Max,
                    CurveMeasure.Sum => AggregateFunction.Sum,
                    _ => AggregateFunction.Avg,
                };
                var comparing = new Comparison(condition.Operands.Select(o => new ComparisonTerm(ComparisonOperator.EqualTo, o)).ToList());
                var (measured, none) = tally.Values.Measure(function, tally.Missing, comparing);
                if (measured is not { } taken)
                {
                    return ($"{check.Label} has no value: {(tally.Values.Count == 0 ? $"{check.Curve} holds no value in any of its {Rows(reading.Rows)}" : none)}; expected it {condition}", false);
                }

                found = taken.Number is { } number ? JsonValue.Create(number) : JsonValue.Create(taken.ToString());
                note = tally.Values.Note(function);
                break;
        }

        if (ValueComparer.Holds(condition, found, out _))
        {
            return null;
        }

        var differs = $"{check.Label} is {RecordValues.Text(found)}{note}, expected it {condition}";
        return check.WarnWithin is { } band && ValueComparer.Within(condition, found, band)
            ? (differs + "; " + Evaluator.WithinBand(band, ValueComparer.Miss(condition, found)), true)
            : (differs, false);
    }

    /// <summary>
    /// Whether every step of a curve meets its proof value: the smallest and the largest do, since the comparisons a step takes
    /// each hold of a range of numbers, so every step between those two holds as well.
    /// </summary>
    private static (string Why, bool WithinBand)? Steps(CurveCheck check, CurveTally tally)
    {
        var condition = check.Condition;
        if (tally.NotNumbers > 0)
        {
            return (string.Create(CultureInfo.InvariantCulture,
                $"{check.Label} has no value: {tally.NotNumbers} of {check.Curve}'s values are not numbers, so its steps cannot be measured; expected each step {condition}"), false);
        }

        if (tally.Steps == 0)
        {
            return ($"{check.Label} has no value: {check.Curve} holds {(tally.Seen ? "one number" : "no number")}, and a step is between two; expected each step {condition}", false);
        }

        JsonNode smallest = JsonValue.Create(tally.StepMin);
        JsonNode largest = JsonValue.Create(tally.StepMax);
        if (ValueComparer.Holds(condition, smallest, out _) && ValueComparer.Holds(condition, largest, out _))
        {
            return null;
        }

        var found = tally.StepMin == tally.StepMax
            ? $"{check.Label} is {RecordValues.Format(tally.StepMin)} in every one of its {tally.Steps} steps"
            : string.Create(CultureInfo.InvariantCulture,
                $"{check.Label} is from {RecordValues.Format(tally.StepMin)} (to row {tally.StepMinRow}) to {RecordValues.Format(tally.StepMax)} (to row {tally.StepMaxRow}) over {tally.Steps} steps");
        var differs = $"{found}, expected each step {condition}";
        return check.WarnWithin is { } band && ValueComparer.Within(condition, smallest, band) && ValueComparer.Within(condition, largest, band)
            ? (differs + "; " + Evaluator.WithinBand(band, null), true)
            : (differs, false);
    }

    /// <summary>
    /// Whether a curve's value at a key meets its proof value: the one row whose index holds the key, its value of the curve.
    /// A key no row holds names the rows either side of where it would lie, or the index's first and last value when it lies
    /// outside them; a key several rows hold names them, since a key names one row.
    /// </summary>
    private static (string Why, bool WithinBand)? At(CurveCheck check, CurveReading reading)
    {
        var condition = check.Condition;
        var keys = reading.Keys
            ?? throw new InvalidOperationException($"The proof value {check.Label} was judged without the keys being read.");
        var hits = keys.Hits(CurveKeyReading.Key(check));
        var written = check.Key!.Text;
        if (hits.Count == 0)
        {
            var where = hits.Before is { } before && hits.After is { } after
                ? string.Create(CultureInfo.InvariantCulture, $"nearest: {RecordValues.Text(before.Value)} in row {before.Row} and {RecordValues.Text(after.Value)} in row {after.Row}")
                : keys.First is { } first && keys.Last is { } last
                    ? string.Create(CultureInfo.InvariantCulture, $"it runs from {RecordValues.Text(first.Value)} in row {first.Row} to {RecordValues.Text(last.Value)} in row {last.Row}")
                    : "it holds no value";
            return ($"{check.Label} has no row: the index {reading.Index} holds no {written} ({where}); expected it {condition}", false);
        }

        if (hits.Count > 1)
        {
            return (string.Create(CultureInfo.InvariantCulture,
                $"{check.Label} is in {hits.Count} rows ({string.Join(", ", hits.Rows)}{(hits.Count > hits.Rows.Count ? ", ..." : string.Empty)}), and a key names one row; expected it {condition}"), false);
        }

        var row = hits.Rows[0];
        if (hits.Values.GetValueOrDefault(check.Curve) is not { } value)
        {
            return (string.Create(CultureInfo.InvariantCulture, $"{check.Label} has no value (row {row}); expected it {condition}"), false);
        }

        if (ValueComparer.Holds(condition, value, out _))
        {
            return null;
        }

        var differs = string.Create(CultureInfo.InvariantCulture, $"{check.Label} is {RecordValues.Text(value)} (row {row}), expected it {condition}");
        return check.WarnWithin is { } band && ValueComparer.Within(condition, value, band)
            ? (differs + "; " + Evaluator.WithinBand(band, ValueComparer.Miss(condition, value)), true)
            : (differs, false);
    }

    /// <summary>The curve a record names its rows indexed by, or null when it names none.</summary>
    private static string? ReferenceCurve(JsonNode record)
    {
        foreach (var value in RecordValues.Select(record, CurveProofValues.ReferenceCurvePath))
        {
            if (RecordValues.TryText(value, out var text) && text.Trim().Length > 0)
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static string Rows(long rows) => rows == 1 ? "1 row" : string.Create(CultureInfo.InvariantCulture, $"{rows} rows");

    private static string Listed(IReadOnlyList<string> columns)
        => columns.Count == 0 ? "it holds none" : string.Join(", ", columns.Take(NamedCurves)) + (columns.Count > NamedCurves ? ", ..." : string.Empty);
}
