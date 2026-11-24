using System.Globalization;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Templates;

// A node's assertions and the rules on the rows (osdu/docs/reference/flow/mapping-assertions.md,
// osdu/docs/reference/flow/mapping-dataset-assertions.md): what each lacks before the loader would take it, and
// each written as the document writes it.
public static partial class MappingBuilder
{
    /// <summary>
    /// What an entry's assertions lack before they can be written, as the loader would refuse them: a node that has a value
    /// of its own to judge, a condition the assertion flows know with its operand on one line, the words a stage and an
    /// action take, the incoming stage only where the row gives the value, <c>omit</c> only where the property may be left
    /// out and each value is judged on its own, a name of its own, and for each condition what it reads. Whether an operand
    /// suits the operator and the property is judged when the composed mapping is read and checked, as any mapping's is.
    /// </summary>
    private static IEnumerable<string> AssertionProblems(MappingDraftEntry entry)
    {
        if (entry.Assertions.Count == 0)
        {
            yield break;
        }

        if (entry.Input is MappingDraftInput.Static or MappingDraftInput.List or MappingDraftInput.Group)
        {
            yield return entry.Input == MappingDraftInput.Static
                ? "an assertion judges the value a record is given, and a fixed value gives every record the same one; remove its assertions, or read the value from the row."
                : "an assertion judges the value of one property, and a list has no value of its own to judge; give the assertions to the properties its items read.";
            yield break;
        }

        if (entry.Assertions.Count > MappingMapper.MaxNodeAssertions)
        {
            yield return string.Create(
                CultureInfo.InvariantCulture,
                $"it states {entry.Assertions.Count} assertions, and a property states at most {MappingMapper.MaxNodeAssertions}.");
        }

        var readsRow = entry.Input is MappingDraftInput.Dataset or MappingDraftInput.Expression;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entry.Assertions.Count; i++)
        {
            var assertion = entry.Assertions[i];
            var at = string.Create(CultureInfo.InvariantCulture, $"assertion {i + 1}");
            if (OperandProblem(assertion.Operator, assertion.Operand) is { } operand)
            {
                yield return $"{at}: {operand}";
            }

            var stage = string.IsNullOrWhiteSpace(assertion.Stage) ? AssertionWords.Record : assertion.Stage.Trim();
            var onFail = string.IsNullOrWhiteSpace(assertion.OnFail) ? AssertionWords.Hold : assertion.OnFail.Trim();
            if (!MappingMapper.AssertionStages.Contains(stage, StringComparer.Ordinal))
            {
                yield return $"{at}: its stage is record or incoming.";
            }

            if (!MappingMapper.AssertionActions.Contains(onFail, StringComparer.Ordinal))
            {
                yield return $"{at}: what a failure does is hold, report or omit.";
            }

            var incoming = stage == AssertionWords.Incoming;
            if (incoming && !readsRow)
            {
                yield return $"{at}: the incoming stage judges the value the row gives, which a dataset column or an expression reads; this property's value comes from elsewhere, so judge the value the record carries (stage: record).";
            }

            if (incoming && assertion.AnyValue)
            {
                yield return $"{at}: the incoming stage judges the one value the row gives, so it judges no set of values; judge every value.";
            }

            if (onFail == AssertionWords.Omit && entry.Required)
            {
                yield return $"{at}: omit leaves the value out of the record, and the property is required; make it not required, or hold or report the record.";
            }

            if (onFail == AssertionWords.Omit && assertion.AnyValue)
            {
                yield return $"{at}: omit leaves out each value that fails, and judging whether one of several values meets it judges them together; judge every value, or hold or report the record.";
            }

            if (!string.IsNullOrWhiteSpace(assertion.Name))
            {
                var name = assertion.Name.Trim();
                if (name.Length > NodeAssertion.MaxLabel)
                {
                    yield return string.Create(CultureInfo.InvariantCulture, $"{at}: its name is 1 to {NodeAssertion.MaxLabel} characters.");
                }
                else if (!names.Add(name))
                {
                    yield return $"{at}: another assertion of this property is named '{name}'; each has a name of its own, since what it finds is recorded under it.";
                }
            }

            if (assertion.Where.Count > MappingMapper.MaxAssertionConditions)
            {
                yield return string.Create(CultureInfo.InvariantCulture, $"{at}: it is judged under at most {MappingMapper.MaxAssertionConditions} conditions.");
            }

            var reads = incoming ? MappingMapper.ColumnSetting : MappingMapper.FieldSetting;
            foreach (var filter in assertion.Where)
            {
                if (!string.Equals(filter.Reads?.Trim(), reads, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(filter.Path))
                {
                    yield return incoming
                        ? $"{at}: each of its conditions reads a column of the row, since it is judged before the record is written; name the column."
                        : $"{at}: each of its conditions reads a field of the record, such as data.Name; name the field.";
                }

                if (OperandProblem(filter.Operator, filter.Operand) is { } condition)
                {
                    yield return $"{at}, a condition: {condition}";
                }
            }
        }
    }

    /// <summary>Why an operator and its operand cannot be written as one condition, or null when they can.</summary>
    private static string? OperandProblem(string? op, string? operand)
        => !ConditionReader.Operators.Contains(op?.Trim() ?? string.Empty, StringComparer.Ordinal) || op!.Trim() == "resolves"
            ? $"'{op}' is not a condition; use one of {string.Join(", ", ConditionReader.Operators.Where(o => o != "resolves"))}."
            : string.IsNullOrWhiteSpace(operand)
                ? $"give {op.Trim()} what it compares with, such as {(op.Trim() == "between" ? "[0, 250]" : op.Trim() is "exists" or "empty" ? "true" : "'GR'")}."
                : operand.Any(char.IsControl)
                    ? $"write what {op.Trim()} compares with on one line, as a list in brackets or a map in braces."
                    : null;

    /// <summary>
    /// One assertion, as an item of <c>$assert</c>: its condition first, then the settings that differ from their defaults,
    /// its conditions and its description.
    /// </summary>
    private static void WriteAssertion(MappingDraftAssertion assertion, string indent, string? scope, Action<string> line)
    {
        var more = indent + "  ";
        line(indent + "- " + OneLine(assertion.Operator) + ": " + OneLine(assertion.Operand));
        if (!string.IsNullOrWhiteSpace(assertion.Name))
        {
            line(more + MappingMapper.NameSetting + ": " + Scalar(assertion.Name.Trim()));
        }

        if (!string.Equals(assertion.Stage?.Trim(), AssertionWords.Record, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(assertion.Stage))
        {
            line(more + MappingMapper.StageSetting + ": " + assertion.Stage.Trim());
        }

        if (!string.Equals(assertion.OnFail?.Trim(), AssertionWords.Hold, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(assertion.OnFail))
        {
            line(more + MappingMapper.OnFailSetting + ": " + assertion.OnFail.Trim());
        }

        if (assertion.AnyValue)
        {
            line(more + MappingMapper.ValuesSetting + ": any");
        }

        WriteConditionSettings(assertion.IgnoreCase, assertion.Tolerance, more, line);
        if (assertion.Where.Count > 0)
        {
            line(more + MappingMapper.WhereSetting + ":");
            foreach (var filter in assertion.Where)
            {
                var reads = OneLine(filter.Reads);
                var path = reads == MappingMapper.ColumnSetting ? ScopedColumn(filter.Path ?? string.Empty, scope) : (filter.Path ?? string.Empty).Trim();
                line(more + "  - " + reads + ": " + Scalar(path));
                line(more + "    " + OneLine(filter.Operator) + ": " + OneLine(filter.Operand));
                WriteConditionSettings(filter.IgnoreCase, filter.Tolerance, more + "    ", line);
            }
        }

        if (!string.IsNullOrWhiteSpace(assertion.Description))
        {
            line(more + "description: " + Scalar(assertion.Description.Trim()));
        }
    }

    /// <summary>
    /// One rule on the rows, as an item of <c>dataset.assert</c>: its name, then the rows it reads, what it judges, the settings
    /// that differ from their defaults, its conditions and its description.
    /// </summary>
    private static void WriteRule(MappingDraftRowRule rule, string indent, Action<string> line)
    {
        var more = indent + "  ";
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(rule.Name))
        {
            lines.Add(MappingMapper.NameSetting + ": " + Scalar(rule.Name.Trim()));
        }

        if (string.Equals(rule.Per?.Trim(), MappingMapper.PerRun, StringComparison.Ordinal))
        {
            lines.Add(MappingMapper.PerSetting + ": " + MappingMapper.PerRun);
        }

        if (!string.IsNullOrWhiteSpace(rule.Rows))
        {
            lines.Add(MappingMapper.RowsSetting + ": " + Scalar(rule.Rows.Trim()));
        }

        switch (rule.Kind?.Trim())
        {
            case MappingDraftRowRule.CountKind:
                lines.Add(MappingMapper.CountSetting + ": " + OneLine(rule.Count));
                break;
            case MappingDraftRowRule.UniqueKind:
                lines.Add(MappingMapper.UniqueSetting + ": [" + string.Join(", ", rule.Unique.Select(c => FlowScalar(c.Trim()))) + "]");
                break;
            default:
                lines.Add(MappingMapper.ColumnSetting + ": " + Scalar((rule.Column ?? string.Empty).Trim()));
                lines.Add(OneLine(rule.Operator) + ": " + OneLine(rule.Operand));
                if (rule.IgnoreCase)
                {
                    lines.Add("ignoreCase: true");
                }

                if (rule.Tolerance is { } tolerance)
                {
                    lines.Add("tolerance: " + tolerance.ToString("R", CultureInfo.InvariantCulture));
                }

                if (rule.AnyRow)
                {
                    lines.Add(MappingMapper.ValuesSetting + ": any");
                }

                break;
        }

        if (!string.Equals(rule.OnFail?.Trim(), AssertionWords.Hold, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(rule.OnFail))
        {
            lines.Add(MappingMapper.OnFailSetting + ": " + rule.OnFail.Trim());
        }

        line(indent + "- " + lines[0]);
        foreach (var written in lines.Skip(1))
        {
            line(more + written);
        }

        if (rule.Where.Count > 0)
        {
            var scope = string.IsNullOrWhiteSpace(rule.Rows) ? null : rule.Rows.Trim();
            line(more + MappingMapper.WhereSetting + ":");
            foreach (var filter in rule.Where)
            {
                line(more + "  - " + MappingMapper.ColumnSetting + ": " + Scalar(ScopedColumn(filter.Path ?? string.Empty, scope)));
                line(more + "    " + OneLine(filter.Operator) + ": " + OneLine(filter.Operand));
                WriteConditionSettings(filter.IgnoreCase, filter.Tolerance, more + "    ", line);
            }
        }

        if (!string.IsNullOrWhiteSpace(rule.Description))
        {
            line(more + "description: " + Scalar(rule.Description.Trim()));
        }
    }

    /// <summary>
    /// A word or an operand of an assertion as one line of YAML: what the draft lacks is reported as an issue, and the YAML
    /// written beside the issues keeps its shape.
    /// </summary>
    private static string OneLine(string? text) => Comment(text ?? string.Empty).Trim();

    private static void WriteConditionSettings(bool ignoreCase, double? tolerance, string indent, Action<string> line)
    {
        if (ignoreCase)
        {
            line(indent + "ignoreCase: true");
        }

        if (tolerance is { } value)
        {
            line(indent + "tolerance: " + value.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
