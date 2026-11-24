using System.Collections;
using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// Curve proof values as known values write them (osdu/docs/reference/flow/curve-proof-values.md): a curve's values
// at the keys its rows are indexed by, each checked as a proof value is.
internal static partial class AssertionMapper
{
    /// <summary>
    /// Curve proof values as known values write them (osdu/docs/reference/flow/known-values.md, Curve proof values): each
    /// curve of the record's bulk data, as the DDMS names it, mapped to its measures, and each measure to the value it equals or
    /// a comparison, with the assertion's tolerance and warning band where it gives none of its own.
    /// </summary>
    private static List<CurveCheck> ReadCurves(Dictionary<string, object?>? declared, double? tolerance, WarningBand? band, string at, TestContext test)
    {
        var checks = new List<CurveCheck>();
        foreach (var (written, raw) in declared ?? [])
        {
            var curveAt = $"{at}.{written}";
            var curve = CheckColumn(written, curveAt, test.Source);
            if (raw is not IDictionary measures || measures.Count == 0)
            {
                throw new FlowValidationException(
                    $"{test.Source}: {curveAt} maps the curve's measures to their proof values, such as {curve}: {{ count: 5120, max: 150.2 }}; a measure is one of {string.Join(", ", CurveProofValues.Names)}.");
            }

            foreach (System.Collections.DictionaryEntry entry in measures)
            {
                var name = entry.Key as string ?? string.Empty;
                var measureAt = $"{curveAt}.{name}";
                var measure = Enum.GetValues<CurveMeasure>().Where(m => AssertionText.Of(m) == name).Select(m => (CurveMeasure?)m).FirstOrDefault()
                    ?? throw new FlowValidationException(
                        $"{test.Source}: {curveAt} has '{Shown(name)}', which is not a measure of a curve; a measure is one of {string.Join(", ", CurveProofValues.Names)}.");
                if (measure == CurveMeasure.At)
                {
                    checks.AddRange(ReadCurveKeys(curve, entry.Value, tolerance, band, measureAt, test));
                }
                else
                {
                    checks.Add(ReadCurveCheck(curve, measure, entry.Value, tolerance, band, measureAt, test));
                }
            }
        }

        return checks;
    }

    /// <summary>
    /// A curve's values at keys of the curve the rows are indexed by (<c>at: { 1500.1524: 74.2 }</c>): each key a depth (a
    /// number) or a time (an ISO 8601 date and time), mapped to the proof value of the curve's value in the row holding it. A
    /// number is compared as written, so two keys naming one number (1500 and 1500.0) are refused.
    /// </summary>
    private static List<CurveCheck> ReadCurveKeys(string curve, object? raw, double? tolerance, WarningBand? band, string at, TestContext test)
    {
        const string Shape = "a key is a depth of the curve the rows are indexed by, such as 1500.1524, or a time, such as \"2026-01-01T00:00:00Z\"";
        if (raw is not IDictionary keys || keys.Count == 0)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} maps keys of the curve the rows are indexed by to {curve}'s value there, such as at: {{ 1500.1524: 74.2 }}; {Shape}.");
        }

        if (keys.Count > CurveProofValues.MaxKeys)
        {
            throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture,
                $"{test.Source}: {at} names {keys.Count} keys; a curve of a row checks its value at {CurveProofValues.MaxKeys} at most. Split them over rows of their own assertions."));
        }

        var checks = new List<CurveCheck>(keys.Count);
        var seen = new Dictionary<Engine.Assertions.CurveKey, string>();
        foreach (System.Collections.DictionaryEntry entry in keys)
        {
            var key = ConditionReader.Scalar(entry.Key, at, test.Source);
            if (Engine.Assertions.CurveKey.Of(key) is not { Kind: not Engine.Assertions.CurveKeyKind.Text } canonical)
            {
                throw new FlowValidationException($"{test.Source}: {at} has the key {key}; {Shape}.");
            }

            if (seen.TryGetValue(canonical, out var earlier))
            {
                throw new FlowValidationException(earlier == key.Text
                    ? $"{test.Source}: {at} names the key {key.Text} twice; give each row once."
                    : $"{test.Source}: {at} names one key twice, as {earlier} and {key.Text}; give each row once.");
            }

            seen[canonical] = key.Text;
            checks.Add(ReadCurveCheck(curve, CurveMeasure.At, entry.Value, tolerance, band, $"{at}.{key.Text}", test) with { Key = key });
        }

        return checks;
    }

    /// <summary>
    /// One curve proof value: the number the measure equals (written plainly), or a comparison of it, with its own tolerance,
    /// warning band and disabled reason. Every measure is a number; min, max, first and last of a curve of dates are dates.
    /// </summary>
    private static CurveCheck ReadCurveCheck(string curve, CurveMeasure measure, object? raw, double? tolerance, WarningBand? band, string at, TestContext test)
    {
        var operators = new List<(string Key, object Operand)>();
        object? ownTolerance = null;
        object? ownBand = null;
        object? disabled = null;
        const string Shape = "a curve proof value is the number the measure equals, or a comparison: one of equals, notEquals, atLeast, atMost, greaterThan, lessThan or between, with tolerance, warnWithin and disabled";
        switch (raw)
        {
            case null:
                throw new FlowValidationException($"{test.Source}: {at} has no value; {Shape}.");
            case IList:
                throw new FlowValidationException($"{test.Source}: {at} is a list; {Shape}.");
            case IDictionary map when ConditionReader.IsQuery(map):
                operators.Add(("equals", raw));
                break;
            case IDictionary map:
                foreach (System.Collections.DictionaryEntry entry in map)
                {
                    var key = entry.Key as string ?? string.Empty;
                    switch (key)
                    {
                        case "tolerance":
                            ownTolerance = entry.Value;
                            break;
                        case "warnWithin":
                            ownBand = entry.Value ?? throw new FlowValidationException($"{test.Source}: {at}.warnWithin has no value.");
                            break;
                        case "disabled":
                            disabled = entry.Value ?? string.Empty;
                            break;
                        default:
                            if (!ConditionReader.ComparisonKeys.Contains(key, StringComparer.Ordinal))
                            {
                                throw new FlowValidationException($"{test.Source}: {at} has '{Shown(key)}'; {Shape}.");
                            }

                            if (entry.Value is not null)
                            {
                                operators.Add((key, entry.Value));
                            }

                            break;
                    }
                }

                break;
            default:
                operators.Add(("equals", raw));
                break;
        }

        if (operators.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {at} names no comparison; {Shape}.");
        }

        var condition = Proof(operators, null, ownTolerance, tolerance, false, at, test);
        var dated = measure is CurveMeasure.Min or CurveMeasure.Max or CurveMeasure.First or CurveMeasure.Last;

        // A value at a key is the curve's own value, of whatever type the curve holds; every other measure is a number.
        foreach (var operand in measure == CurveMeasure.At ? [] : condition.Operands.Where(o => !o.Pending))
        {
            var isDate = operand.Kind == ExpectedValueKind.Text && Engine.Assertions.RecordValues.TryInstant(operand.Text, out _);
            if (operand.Kind != ExpectedValueKind.Number && !(dated && isDate))
            {
                throw new FlowValidationException(
                    $"{test.Source}: {at} compares {curve}'s {AssertionText.Of(measure)}, which is a number{(dated ? " (or a date, for a curve of dates)" : string.Empty)}; give a number{(dated ? " or an ISO 8601 date" : string.Empty)}, not {operand}.");
            }

            if (measure is CurveMeasure.Count or CurveMeasure.Missing && (operand.Number is not { } rows || rows < 0 || Math.Floor(rows) != rows))
            {
                throw new FlowValidationException(
                    $"{test.Source}: {at} compares how many rows {curve} {(measure == CurveMeasure.Count ? "has a value in" : "has no value in")}, a whole number of zero or more, not {operand}.");
            }
        }

        if (measure == CurveMeasure.Step && condition.Operator == ValueOperator.NotEqualTo)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} holds every step of {curve} to it, and notEquals is not a range every step can lie in; give the step it equals, with a tolerance, or between.");
        }

        var own = ConditionReader.Band(ownBand, at, test.Source);
        if (own is not null)
        {
            ConditionReader.CheckBand(own, condition.Tolerance, [AssertionText.Of(condition.Operator)], condition.Operands, at, test.Source);
        }

        return new CurveCheck(curve, measure, condition, own ?? Shared(band, condition), DisabledOf(disabled, at, test.Source));
    }
}
