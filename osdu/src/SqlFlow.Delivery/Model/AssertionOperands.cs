namespace SqlFlow.Delivery.Model;

/// <summary>
/// The values an assertion compares with: every operand of its comparisons and conditions (a count's, an aggregate's, a
/// field's and its where conditions', a grouping's, a proof value's and a curve proof value's). The document reads a query
/// named there into it, and a run replaces each query with the value it answers before anything is judged
/// (osdu/docs/reference/flow/staged-proof-values.md).
/// </summary>
public static class AssertionOperands
{
    /// <summary>Every value <paramref name="assertion"/> compares with, in document order.</summary>
    public static IReadOnlyList<ExpectedValue> Of(TestAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var found = new List<ExpectedValue>();
        Map(assertion, value =>
        {
            found.Add(value);
            return value;
        });
        return found;
    }

    /// <summary><paramref name="assertion"/> with <paramref name="map"/> applied to every value it compares with.</summary>
    public static TestAssertion Map(TestAssertion assertion, Func<ExpectedValue, ExpectedValue> map)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(map);
        Comparison Compare(Comparison comparison) => comparison with
        {
            Terms = comparison.Terms.Select(t => t with { Value = map(t.Value), Upper = t.Upper is null ? null : map(t.Upper) }).ToList(),
        };
        ValueCondition Condition(ValueCondition condition) => condition with
        {
            Operands = condition.Operands.Select(map).ToList(),
            Length = condition.Length is null ? null : Compare(condition.Length),
        };
        return assertion switch
        {
            CountAssertion count => count with { Comparison = Compare(count.Comparison) },
            RowCountAssertion rows => rows with { Comparison = Compare(rows.Comparison) },
            AggregateAssertion aggregate => aggregate with { Comparison = Compare(aggregate.Comparison) },
            ValueAssertion value => value with
            {
                Condition = Condition(value.Condition),
                Where = value.Where.Select(w => w with { Condition = Condition(w.Condition) }).ToList(),
            },
            GroupAssertion group => group with
            {
                Groups = group.Groups.Select(g => g with { Count = Compare(g.Count) }).ToList(),
                GroupCount = group.GroupCount is null ? null : Compare(group.GroupCount),
            },
            KnownValuesAssertion known => known with
            {
                Rows = known.Rows.Select(row => row with
                {
                    Checks = row.Checks.Select(c => c with { Condition = Condition(c.Condition) }).ToList(),
                    Curves = row.Curves?.Select(c => c with { Condition = Condition(c.Condition) }).ToList(),
                }).ToList(),
            },
            _ => assertion,
        };
    }
}
