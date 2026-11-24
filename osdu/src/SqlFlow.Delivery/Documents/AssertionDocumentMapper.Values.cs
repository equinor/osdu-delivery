using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// The assertions on a field's or a column's values and on an aggregate of them (osdu/docs/reference/flow/assertion.md,
// Conditions): the condition, the where filter and the quantifier a value assertion reads, and the values an
// aggregate leaves out.
internal static partial class AssertionMapper
{
    /// <summary>The values an aggregate leaves out: 1 to <see cref="MaxListed"/> scalars, none of them null, which is no value already.</summary>
    private static IReadOnlyList<ExpectedValue> MapExcept(List<object>? declared, string at, TestContext test)
    {
        if (declared is null)
        {
            return [];
        }

        if (declared.Count is 0 or > MaxListed)
        {
            throw new FlowValidationException($"{test.Source}: {at}.except lists between 1 and {MaxListed} values the aggregate leaves out.");
        }

        var values = new List<ExpectedValue>(declared.Count);
        foreach (var raw in declared)
        {
            var value = ConditionReader.Scalar(raw, at + ".except", test.Source);
            if (value.Kind == ExpectedValueKind.Null)
            {
                throw new FlowValidationException($"{test.Source}: {at}.except lists null; an aggregate already leaves out what has no value.");
            }

            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static AggregateAssertion MapAggregate(AssertionItemYaml y, string at, TestContext test)
    {
        var isColumn = y.Column is not null;
        OnlyKeys(y, "aggregate", ConditionReader.ComparisonKeys.Append("aggregate").Append(isColumn ? "column" : "field").Append("tolerance").Append("except").Append("warnWithin"), at, test);
        var function = FlowMapper.ParseEnum<AggregateFunction>(y.Aggregate!.Trim(), at + ".aggregate", test.Source);
        ValueTarget target;
        if (isColumn)
        {
            RequireBulk("column aggregate", at, test);
            target = new ValueTarget(CheckColumn(y.Column!, at + ".column", test.Source), IsColumn: true);
        }
        else
        {
            var field = y.Field ?? throw new FlowValidationException($"{test.Source}: {at} aggregates, and names no field (or column) to aggregate.");
            target = new ValueTarget(ConditionReader.RecordPath(field, at + ".field", test.Source), IsColumn: false);
        }

        // Min and max compare dates as well as numbers; every other aggregate is a number.
        var numbersOnly = function is not (AggregateFunction.Min or AggregateFunction.Max);
        var terms = OperatorsOf(y).Select(o => ConditionReader.Term(o.Key, o.Operand, $"{at}.{o.Key}", test.Source, numbersOnly)).ToList();
        if (terms.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {at} aggregates {target} and compares it with nothing: give it one of {string.Join(", ", ConditionReader.ComparisonKeys)}.");
        }

        var tolerance = ConditionReader.Tolerance(y.Tolerance, at, test.Source);
        if (tolerance is not null && terms.All(t => t.Value.Kind != ExpectedValueKind.Number))
        {
            throw new FlowValidationException($"{test.Source}: {at}.tolerance applies to comparisons of numbers, and the aggregate is compared with a date.");
        }

        var band = ConditionReader.Band(y.WarnWithin, at, test.Source);
        if (band is not null)
        {
            ConditionReader.CheckBand(band, tolerance, new Comparison(terms), at, test.Source);
        }

        var assertion = new AggregateAssertion
        {
            Function = function,
            Target = target,
            Comparison = new Comparison(terms),
            Tolerance = tolerance,
            WarnWithin = band,
            Except = MapExcept(y.Except, at, test),
            Label = string.Empty,
        };
        return assertion with { Label = $"{AssertionText.Of(function)}({target}) {assertion.Expected}" };
    }

    private static ValueAssertion MapValue(AssertionItemYaml y, bool isColumn, string at, TestContext test)
    {
        var subject = isColumn ? "column" : "field";
        var takes = OperatorsOf(y).Select(o => o.Key).ToList();
        takes.AddRange([subject, "ignoreCase", "tolerance", "for", "optional", "where"]);
        if (!isColumn)
        {
            takes.Add("values");
        }

        OnlyKeys(y, subject, takes, at, test);
        ValueTarget target;
        if (isColumn)
        {
            RequireBulk("column", at, test);
            target = new ValueTarget(CheckColumn(y.Column!, at + ".column", test.Source), IsColumn: true);
        }
        else
        {
            target = new ValueTarget(ConditionReader.RecordPath(y.Field!, at + ".field", test.Source), IsColumn: false);
        }

        var condition = MapCondition(y, isColumn, at, test);
        var quantifier = MapQuantifier(y.For, at, test.Source);
        var anyValue = y.Values switch
        {
            null => false,
            string word when word.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) => false,
            string word when word.Trim().Equals("any", StringComparison.OrdinalIgnoreCase) => true,
            string word => throw new FlowValidationException($"{test.Source}: {at}.values '{word}' is not all or any."),
            _ => throw new FlowValidationException($"{test.Source}: {at}.values is all or any, a word rather than a list or a mapping."),
        };
        if (condition.Operator == ValueOperator.Resolves && (y.For is not null || y.Values is not null))
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} asserts that references resolve, which holds for every reference the records carry; for and values do not apply.");
        }

        var where = new List<ValueFilter>();
        foreach (var (filter, i) in (y.Where ?? []).Select((c, i) => (c, i)))
        {
            var whereAt = string.Create(CultureInfo.InvariantCulture, $"{at}.where[{i}]");
            if (filter is null)
            {
                throw new FlowValidationException($"{test.Source}: {whereAt} is empty.");
            }

            where.Add(MapFilter(filter, isColumn, whereAt, test));
        }

        if (y.Where is { Count: 0 or > MaxConditions })
        {
            throw new FlowValidationException($"{test.Source}: {at}.where lists between 1 and {MaxConditions} conditions.");
        }

        var assertion = new ValueAssertion
        {
            Target = target,
            Condition = condition,
            For = quantifier,
            AnyValue = anyValue,
            Optional = y.Optional ?? false,
            Where = where,
            Label = string.Empty,
        };
        var label = $"{target} {condition}"
            + (quantifier.Mode == QuantifierMode.All ? string.Empty : $" for {quantifier}")
            + (where.Count == 0 ? string.Empty : " where " + string.Join(" and ", where));
        return assertion with { Label = label };
    }

    private static ValueFilter MapFilter(AssertionConditionYaml y, bool isColumn, string at, TestContext test)
    {
        var subject = isColumn ? "column" : "field";
        var keys = KeysOf(y);
        var allowed = OperatorsOf(y).Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        allowed.UnionWith([subject, "ignoreCase", "tolerance"]);
        var stray = keys.Where(k => !allowed.Contains(k)).ToList();
        if (stray.Count > 0)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} selects the {(isColumn ? "rows" : "records")} the assertion looks at by a {subject}, and does not take {string.Join(", ", stray)}.");
        }

        var target = isColumn
            ? new ValueTarget(CheckColumn(y.Column ?? throw new FlowValidationException($"{test.Source}: {at} names no column; a condition on a column assertion selects rows by a column."), at + ".column", test.Source), IsColumn: true)
            : new ValueTarget(ConditionReader.RecordPath(y.Field ?? throw new FlowValidationException($"{test.Source}: {at} names no field; a condition on a field assertion selects records by a field."), at + ".field", test.Source), IsColumn: false);
        var condition = MapCondition(y, isColumn, at, test);
        if (condition.Operator == ValueOperator.Resolves)
        {
            throw new FlowValidationException($"{test.Source}: {at} selects by whether a reference resolves, which only an assertion can ask.");
        }

        return new ValueFilter(target, condition);
    }

    private static ValueCondition MapCondition(AssertionConditionYaml y, bool isColumn, string at, TestContext test)
        => ConditionReader.Read(
            OperatorsOf(y), y.IgnoreCase, y.Tolerance, isColumn ? "asks whether a reference resolves, and a column of bulk data holds no references." : null, at, test.Source);

    private static Quantifier MapQuantifier(string? declared, string at, string source)
    {
        var text = declared?.Trim().ToLowerInvariant();
        switch (text)
        {
            case null or "all":
                return Quantifier.All;
            case "any":
                return Quantifier.Any;
            case "none":
                return Quantifier.None;
        }

        if (text.EndsWith('%')
            && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            && percent is > 0 and <= 100)
        {
            return percent == 100 ? Quantifier.All : Quantifier.AtLeast(percent);
        }

        throw new FlowValidationException($"{source}: {at}.for '{declared}' is all, any, none, or a share such as 95%.");
    }
}
