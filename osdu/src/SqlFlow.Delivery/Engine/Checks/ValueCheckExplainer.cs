using System.Globalization;
using SqlFlow.Delivery.Json;

namespace SqlFlow.Delivery.Engine.Checks;

/// <summary>
/// Says what a finding of a value check means and what to do about it (<see cref="ValueCheckExplanation"/>), from what it comes
/// from (<see cref="ValueCheckCause"/>), the values behind it and the cache it was rendered with: "the cache holds no
/// reference-data--UnitOfMeasure record ft", where the render says "the id gives opendes:reference-data--UnitOfMeasure:ft:, and
/// version ... holds no such ... record in UnitOfMeasure". Code (a column, a type, a value) is set in backticks, which the page
/// draws as code and the workbook leaves out (osdu/docs/reference/concepts/value-proof.md).
/// </summary>
public static class ValueCheckExplainer
{
    /// <summary>The most values a headline names; the rest are counted.</summary>
    private const int NamedValues = 3;

    /// <summary>Every finding of <paramref name="variables"/> with what it means, said against <paramref name="inputs"/>.</summary>
    public static IReadOnlyList<ValueCheckVariable> Explain(IReadOnlyList<ValueCheckVariable> variables, ValueCheckInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(inputs);
        return variables
            .Select(variable => variable with { Findings = variable.Findings.Select(finding => finding with { Explanation = Explain(finding, variable, inputs) }).ToList() })
            .ToList();
    }

    /// <summary>What <paramref name="finding"/> of <paramref name="variable"/> means, and what to do about it.</summary>
    public static ValueCheckExplanation Explain(ValueCheckFinding finding, ValueCheckVariable variable, ValueCheckInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(variable);
        ArgumentNullException.ThrowIfNull(inputs);
        var cause = finding.Causes.Count > 0 ? finding.Causes[0] : new ValueCheckCause { Kind = ValueCheckCause.Other };
        var looked = finding.Values.Select(v => v.Value).ToList();
        var headline = Headline(cause, looked, finding.Message, finding.Rule, finding.OtherValues > 0);
        if (finding.Causes.Count > 1)
        {
            headline = string.Create(
                CultureInfo.InvariantCulture,
                $"None of the {finding.Causes.Count} ways the mapping gives `{Leaf(variable.Target)}` found a value; the first: {Lower(headline)}");
        }

        return new ValueCheckExplanation(
            headline,
            Effect(finding, variable),
            Detail(cause, finding, inputs),
            Fixes(cause, finding, variable));
    }

    /// <summary>
    /// What one occurrence comes from, for a listing of every failing row: the headline of its first cause, about the one value
    /// it met. A row of the workbook says this beside the reason the render gave.
    /// </summary>
    public static string Headline(ValueCheckOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var cause = occurrence.Causes.Count > 0 ? occurrence.Causes[0] : new ValueCheckCause { Kind = ValueCheckCause.Other };
        var headline = Headline(cause, occurrence.Value is { Length: > 0 } value ? [value] : [], occurrence.Message, occurrence.Rule, others: false);

        // An assertion's reason for one value is its own: what the value is against what the assertion asks.
        return cause.Kind == ValueCheckCause.Assertion ? $"{headline}: {occurrence.Message}" : headline;
    }

    /// <param name="cause">What it comes from.</param>
    /// <param name="values">The values behind it, the most frequent first.</param>
    /// <param name="message">The reason as the render states it.</param>
    /// <param name="rule">The rule or assertion it breaks.</param>
    /// <param name="others">Whether more values are behind it than <paramref name="values"/> lists.</param>
    private static string Headline(ValueCheckCause cause, IReadOnlyList<string> values, string message, string? rule, bool others)
    {
        switch (cause.Kind)
        {
            case ValueCheckCause.Empty when cause.Columns.Count > 0:
                return cause.Columns.Count == 1
                    ? $"{Code(cause.Columns[0])} is empty"
                    : $"{Joined(cause.Columns.Select(Code).ToList(), "and")} are empty";
            case ValueCheckCause.IdNotHeld:
                return $"The cache holds no {Code(cause.EntityType ?? Joined(cause.Types, "or"))} record {Named(values.Select(IdCode).ToList(), others)}";
            case ValueCheckCause.NoMatch:
                return $"No {Code(First(cause.Types, "record"))} in the cache has {Fields(cause)} equal to {Named(values, others)}"
                    + (cause.IgnoreSeparators ? ", even with punctuation and spacing ignored" : string.Empty);
            case ValueCheckCause.Ambiguous:
                return $"{Named(values, others)} matches {cause.Candidates.Count.ToString(CultureInfo.InvariantCulture)} {Code(First(cause.Types, "cached"))} records once case is ignored ({string.Join(", ", cause.Candidates.Select(Code))}), so none is taken";
            case ValueCheckCause.NoType:
                return $"The cache holds no type {Code(First(cause.Types, "?"))}";
            case ValueCheckCause.FieldEmpty:
                return cause.Candidates.Count == 1
                    ? $"The {Code(First(cause.Types, "cached"))} record {Code(cause.Candidates[0])} holds nothing at {Fields(cause)}"
                    : $"The {Code(First(cause.Types, "cached"))} records {string.Join(", ", cause.Candidates.Select(Code))} hold nothing at {Fields(cause)}";
            case ValueCheckCause.NotFound:
                return $"No {Code(EntityOf(cause.EntityType))} on the platform has {Fields(cause)} equal to {Named(values, others)}";
            case ValueCheckCause.RuleBroken:
                return $"The value breaks the template's {Code(rule ?? "rule")} rule: {message}";
            case ValueCheckCause.Assertion:
                return $"The value fails the mapping's assertion {Code(rule ?? "?")}";
            default:
                return Upper(message);
        }
    }

    private static string Effect(ValueCheckFinding finding, ValueCheckVariable variable)
    {
        var leaf = Code(Leaf(variable.Target));
        return finding.Outcome switch
        {
            "held" => "Held: these records are not delivered until it is fixed.",
            "empty" => $"Left out: these records are delivered without {leaf}.",
            "invalid" => $"Invalid: these records are delivered with a {leaf} the template does not accept.",
            "asserted" => finding.OnFail switch
            {
                "report" => "Reported: these records are delivered, and the failure is recorded on each.",
                "omit" => $"Left out: {leaf} is dropped from these records.",
                _ => "Held: these records are not delivered until it is fixed.",
            },
            _ => "Not applicable: the mapping means no value for these rows.",
        };
    }

    private static string? Detail(ValueCheckCause cause, ValueCheckFinding finding, ValueCheckInputs inputs)
    {
        var parts = new List<string>();
        var cache = inputs.CacheVersion is null
            ? null
            : inputs.CachePartition is { } partition ? $"version {Code(inputs.CacheVersion)} of the cache of partition {Code(partition)}" : $"cache version {Code(inputs.CacheVersion)}";
        switch (cause.Kind)
        {
            case ValueCheckCause.IdNotHeld or ValueCheckCause.NoMatch or ValueCheckCause.Ambiguous or ValueCheckCause.FieldEmpty when cache is not null:
                parts.Add($"Looked up in {cache}, which holds what a cache flow last captured.");
                break;
            case ValueCheckCause.NoType when cache is not null:
                parts.Add($"Looked for in {cache}.");
                break;
            case ValueCheckCause.NotFound:
                parts.Add("Searched for on the platform, as the flow's identity sees it.");
                break;
        }

        // What the source holds, beside what the mapping made of it when that is not the same.
        if (finding.SourceValues.Count > 0 && finding.Values.Count > 0)
        {
            var source = finding.SourceValues[0];
            var made = finding.Values[0].Value;
            if (!string.Equals(source.Value.Trim(), made.Trim(), StringComparison.Ordinal))
            {
                parts.Add($"The source holds {Code(source.Value)} in {Code(source.Column)}, which the mapping turns into {Code(made)}.");
            }
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private static IReadOnlyList<string> Fixes(ValueCheckCause cause, ValueCheckFinding finding, ValueCheckVariable variable)
    {
        var columns = cause.Columns.Count > 0 ? Joined(cause.Columns.Select(Code).ToList(), "and") : null;
        var where = columns is null ? "the source" : $"{columns} in the source";
        var type = Code(First(cause.Types, "the type"));
        var fixes = new List<string>();
        switch (cause.Kind)
        {
            case ValueCheckCause.Empty:
                fixes.Add($"Fill {where} for the records listed.");
                if (finding.Outcome == "held" && variable.Required)
                {
                    fixes.Add("If a record may go without it, make the attribute optional in the mapping (`$required: false`).");
                }

                fixes.Add("Or give the attribute another source in the mapping (`$coalesce`), for rows that leave this one empty.");
                break;
            case ValueCheckCause.IdNotHeld or ValueCheckCause.NoMatch:
                fixes.Add($"If the value is wrong, correct {where} for the records listed.");
                fixes.Add("If OSDU knows it under another code, translate it in the mapping: a `replace` modifier, or a row of the lookup table the mapping replaces through.");
                fixes.Add(cause.EntityType is not null
                    ? $"If the record is missing from OSDU, load it into the partition, then refresh the cache flow that captures {type}, so the next check finds it."
                    : $"If the row is missing from the lookup table {type}, add it to the table the cache flow reads, then refresh that cache flow.");
                break;
            case ValueCheckCause.Ambiguous:
                fixes.Add($"Make {where} name one record exactly, in the case that record is written in, for the records listed.");
                fixes.Add("Or translate the value in the mapping with a `replace` modifier to the one record meant.");
                break;
            case ValueCheckCause.NoType:
                fixes.Add($"Declare {type} in a cache flow of the partition and refresh it; the mapping reads a type the cache does not capture.");
                fixes.Add($"Or, if {type} is misspelled in the mapping, correct the name the mapping reads.");
                break;
            case ValueCheckCause.FieldEmpty:
                fixes.Add($"Fill {Fields(cause)} on the record in OSDU (or the row of the lookup table), then refresh the cache flow that captures {type}.");
                fixes.Add("Or read another field of the record in the mapping.");
                break;
            case ValueCheckCause.NotFound:
                fixes.Add($"If the value is wrong, correct {where} for the records listed.");
                fixes.Add("If the record is not in OSDU yet, deliver it first: a check sends nothing, so a record another flow delivers first is not found until it has.");
                fixes.Add("If OSDU holds it under another name, add that name as an alias of the record, or another `$findBy` line to the mapping.");
                break;
            case ValueCheckCause.RuleBroken:
                fixes.Add($"Correct {where} for the records listed, so the value meets the rule.");
                fixes.Add("Or convert the value in the mapping with a modifier (`date`, `number`, `replace`) so what is written meets it.");
                break;
            case ValueCheckCause.Assertion:
                fixes.Add($"Correct {where} for the records listed.");
                fixes.Add("If the assertion is what is wrong, change it in the mapping.");
                break;
            default:
                fixes.Add(columns is null ? "The reason as rendered says what the render could not do, and names the column or the part of the mapping to correct." : $"Correct {where} for the records listed.");
                break;
        }

        return fixes;
    }

    /// <summary>The values a headline names: the first few, then how many more are listed, or that there are more.</summary>
    private static string Named(IReadOnlyList<string> values, bool others)
    {
        if (values.Count == 0)
        {
            return "for this value";
        }

        var named = values.Take(NamedValues).Select(Code).ToList();
        var rest = values.Count - named.Count;
        return rest > 0 || others
            ? string.Create(CultureInfo.InvariantCulture, $"{string.Join(", ", named)} and {(rest > 0 ? $"{rest}{(others ? "+" : string.Empty)}" : "other")} more values")
            : Joined(named, "or");
    }

    private static string Fields(ValueCheckCause cause)
        => cause.Fields.Count == 0 ? "a value" : Joined(cause.Fields.Select(Code).ToList(), "or");

    private static string Joined(IReadOnlyList<string> items, string word)
        => items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            _ => $"{string.Join(", ", items.Take(items.Count - 1))} {word} {items[^1]}",
        };

    /// <summary>The code an OSDU reference names (<c>ft</c> of <c>opendes:reference-data--UnitOfMeasure:ft:</c>), decoded; the value itself otherwise.</summary>
    internal static string IdCode(string value)
    {
        var parts = value.Split(':');
        if (parts.Length < 3 || !parts[1].Contains("--", StringComparison.Ordinal) || parts[2].Length == 0)
        {
            return value;
        }

        try
        {
            return Uri.UnescapeDataString(parts[2]);
        }
        catch (UriFormatException)
        {
            return parts[2];
        }
    }

    /// <summary>The entity type a kind names (<c>master-data--Wellbore</c> of <c>osdu:wks:master-data--Wellbore:1.3.0</c>).</summary>
    private static string EntityOf(string? kind)
    {
        if (kind is null)
        {
            return "record";
        }

        var parts = kind.Split(':');
        return parts.Length >= 3 ? parts[2] : kind;
    }

    private static string First(IReadOnlyList<string> items, string otherwise) => items.Count > 0 ? items[0] : otherwise;

    /// <summary>The last property a target names, as the record holds its name: <c>Unit</c> of <c>osdu.data.Curves[].Unit</c>, <c>lra.dataID</c> of <c>osdu.tags['lra.dataID']</c>.</summary>
    private static string Leaf(string target) => RecordPathNotation.LastName(target);

    private static string Code(string text) => "`" + text.Replace("`", "'", StringComparison.Ordinal) + "`";

    private static string Upper(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
