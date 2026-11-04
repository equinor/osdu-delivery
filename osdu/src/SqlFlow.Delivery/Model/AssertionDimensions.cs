namespace SqlFlow.Delivery.Model;

/// <summary>
/// What an assertion is about, along every axis the dashboard maps and groups assertions by
/// (osdu/docs/reference/flow/assertion-sets.md, Dimensions): three every assertion has without a word of YAML (its
/// <see cref="Set"/>, the <see cref="Type"/> of record it reads, and the <see cref="Check"/> it makes, read off its
/// assertions), then the dimensions its flow and the test name. A document's own <c>Type</c> or <c>Check</c> takes the
/// place of the one read off the test; <c>Set</c> is always the flow's set.
/// </summary>
public static class AssertionDimensions
{
    public const string Set = "Set";

    public const string Type = "Type";

    public const string Check = "Check";

    /// <summary>The kinds of check a test makes, in the order one names a test that makes several.</summary>
    private static readonly string[] Checks = ["Proof values", "Counts", "Schema", "References", "Legal tags", "Delivery", "Bulk data", "Values"];

    /// <summary>Every dimension of a test of a flow, in order: Set, Type and Check, then the ones its documents name.</summary>
    public static IReadOnlyList<AssertionDimension> Of(AssertionFlowDefinition flow, AssertionTest test)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(test);
        var all = new List<AssertionDimension>(test.Dimensions.Count + 3);
        if (flow.Set is { } set)
        {
            all.Add(new AssertionDimension(Set, set));
        }

        all.Add(new AssertionDimension(Type, TypeOf(test.Kind)));
        if (CheckOf(test) is { } check)
        {
            all.Add(new AssertionDimension(Check, check));
        }

        foreach (var named in test.Dimensions)
        {
            var at = all.FindIndex(d => string.Equals(d.Name, named.Name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                all[at] = named;
            }
            else
            {
                all.Add(named);
            }
        }

        return all;
    }

    /// <summary>A kind as people name it: its entity type without its group, and its version (<c>WellLog 1.4.0</c>).</summary>
    public static string TypeOf(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var parts = kind.Split(':');
        if (parts.Length < 4)
        {
            return kind;
        }

        var entity = parts[2];
        var at = entity.IndexOf("--", StringComparison.Ordinal);
        return $"{(at >= 0 ? entity[(at + 2)..] : entity)} {parts[3]}";
    }

    /// <summary>
    /// The check a test makes, read off its assertions (the ones it judges, or all when every one is disabled): proof values
    /// for known values and record sets, counts for counts, aggregates, groups and row counts, schema for conformance and a
    /// mapping's rules, references for a field that resolves, legal tags, delivery for the ledger and the index, bulk data
    /// for columns, and values for any other field or uniqueness. A test making several is named by the first in that order.
    /// </summary>
    public static string? CheckOf(AssertionTest test)
    {
        ArgumentNullException.ThrowIfNull(test);
        var judged = test.Assertions.Where(a => a.Disabled is null).ToList();
        var made = (judged.Count > 0 ? judged : test.Assertions).Select(KindOfCheck).ToHashSet(StringComparer.Ordinal);
        return Checks.FirstOrDefault(made.Contains);
    }

    private static string KindOfCheck(TestAssertion assertion) => assertion switch
    {
        KnownValuesAssertion or RecordSetAssertion => "Proof values",
        CountAssertion or AggregateAssertion { Target.IsColumn: false } or GroupAssertion or RowCountAssertion => "Counts",
        ConformsAssertion or MappingRulesAssertion => "Schema",
        ValueAssertion { Condition.Operator: ValueOperator.Resolves } => "References",
        LegalAssertion => "Legal tags",
        DeliveredAssertion or IndexedAssertion => "Delivery",
        ColumnsAssertion or MonotonicAssertion or ValueAssertion { Target.IsColumn: true } or AggregateAssertion => "Bulk data",
        _ => "Values",
    };
}
