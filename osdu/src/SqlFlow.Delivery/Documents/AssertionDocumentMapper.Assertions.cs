using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// The assertions of a test (osdu/docs/reference/flow/assertion.md, Assertions): each read by its subject, with
// the keys every subject takes, the operators a condition sets, and the keys and bulk read a subject refuses or
// needs. Each subject's own reading is in a file beside this one.
internal static partial class AssertionMapper
{
    private static IReadOnlyList<TestAssertion> MapAssertions(List<AssertionItemYaml>? declared, TestContext test)
    {
        if (declared is null || declared.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {test.Where}: assert must list at least one assertion; a test without one asserts nothing.");
        }

        if (declared.Count > MaxAssertions)
        {
            throw new FlowValidationException($"{test.Source}: {test.Where}: assert lists {declared.Count} assertions; a test holds at most {MaxAssertions}. Split it into several tests.");
        }

        var assertions = new List<TestAssertion>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{test.Where}: assert[{i}]");
            var item = declared[i] ?? throw new FlowValidationException($"{test.Source}: {at} is empty.");
            assertions.Add(MapAssertion(item, at, test));
        }

        return assertions;
    }

    /// <summary>The keys an assertion item sets, by the names the document writes them with.</summary>
    private static List<string> KeysOf(AssertionConditionYaml y)
    {
        var keys = new List<string>();
        void Add(bool present, string key)
        {
            if (present)
            {
                keys.Add(key);
            }
        }

        Add(y.Field is not null, "field");
        Add(y.Column is not null, "column");
        foreach (var (key, _) in OperatorsOf(y))
        {
            keys.Add(key);
        }

        Add(y.IgnoreCase is not null, "ignoreCase");
        Add(y.Tolerance is not null, "tolerance");
        if (y is AssertionItemYaml item)
        {
            Add(item.Count is not null, "count");
            Add(item.Aggregate is not null, "aggregate");
            Add(item.Unique is not null, "unique");
            Add(item.GroupBy is not null, "groupBy");
            Add(item.Groups is not null, "groups");
            Add(item.Absent is not null, "absent");
            Add(item.Mode is not null, "mode");
            Add(item.GroupCount is not null, "groupCount");
            Add(item.RecordSet is not null, "recordSet");
            Add(item.KnownValues is not null, "knownValues");
            Add(item.Except is not null, "except");
            Add(item.WarnWithin is not null, "warnWithin");
            Add(item.Conforms is not null, "conforms");
            Add(item.Mapping is not null, "mapping");
            Add(item.Indexed is not null, "indexed");
            Add(item.Legal is not null, "legal");
            Add(item.Delivered is not null, "delivered");
            Add(item.Interface is not null, "interface");
            Add(item.Exact is not null, "exact");
            Add(item.RowCount is not null, "rowCount");
            Add(item.Columns is not null, "columns");
            Add(item.Monotonic is not null, "monotonic");
            Add(item.For is not null, "for");
            Add(item.Values is not null, "values");
            Add(item.Optional is not null, "optional");
            Add(item.Disabled is not null, "disabled");
            Add(item.AsOf is not null, "asOf");
            Add(item.ValidUntil is not null, "validUntil");
            Add(item.ValidFor is not null, "validFor");
            Add(item.Where is not null, "where");
        }

        return keys;
    }

    /// <summary>The operators a condition sets, each with its operand as the document writes it.</summary>
    private static List<(string Key, object Operand)> OperatorsOf(AssertionConditionYaml y)
    {
        var operators = new List<(string, object)>();
        void Add(object? operand, string key)
        {
            if (operand is not null)
            {
                operators.Add((key, operand));
            }
        }

        Add(y.EqualTo, "equals");
        Add(y.NotEquals, "notEquals");
        Add(y.In, "in");
        Add(y.NotIn, "notIn");
        Add(y.AtLeast, "atLeast");
        Add(y.AtMost, "atMost");
        Add(y.GreaterThan, "greaterThan");
        Add(y.LessThan, "lessThan");
        Add(y.Between, "between");
        Add(y.Matches, "matches");
        Add(y.NotMatches, "notMatches");
        Add(y.StartsWith, "startsWith");
        Add(y.EndsWith, "endsWith");
        Add(y.Contains, "contains");
        Add(y.NotContains, "notContains");
        Add(y.Exists, "exists");
        Add(y.Empty, "empty");
        Add(y.Type, "type");
        Add(y.Length, "length");
        Add(y.Resolves, "resolves");
        return operators;
    }

    private static readonly string[] Common = ["name", "description", "severity", "disabled", "asOf", "validUntil", "validFor"];

    private static TestAssertion MapAssertion(AssertionItemYaml y, string at, TestContext test)
    {
        var subjects = new List<string>();
        foreach (var (present, key) in new (bool, string)[]
                 {
                     (y.Count is not null, "count"), (y.Aggregate is not null, "aggregate"), (y.Monotonic is not null, "monotonic"),
                     (y.Unique is not null, "unique"), (y.GroupBy is not null, "groupBy"), (y.RecordSet is not null, "recordSet"),
                     (y.Conforms is not null, "conforms"), (y.Indexed is not null, "indexed"), (y.Legal is not null, "legal"),
                     (y.Delivered is not null, "delivered"), (y.RowCount is not null, "rowCount"), (y.Columns is not null, "columns"),
                     (y.Mapping is not null, "mapping"), (y.KnownValues is not null, "knownValues"),
                 })
        {
            if (present)
            {
                subjects.Add(key);
            }
        }

        if (subjects.Count == 0)
        {
            if (y.Field is not null && y.Column is not null)
            {
                throw new FlowValidationException($"{test.Source}: {at} names both a field and a column; an assertion looks at a field of each record or a column of its bulk data, not both.");
            }

            if (y.Field is not null)
            {
                subjects.Add("field");
            }
            else if (y.Column is not null)
            {
                subjects.Add("column");
            }
        }

        if (subjects.Count != 1)
        {
            throw new FlowValidationException(subjects.Count == 0
                ? $"{test.Source}: {at} names no subject; an assertion is one of count, field, column, aggregate, unique, groupBy, recordSet, knownValues, conforms, mapping, indexed, legal, delivered, rowCount, columns or monotonic."
                : $"{test.Source}: {at} names {string.Join(" and ", subjects)}; an assertion has one subject. Write one assertion for each.");
        }

        var subject = subjects[0];
        var severity = FlowMapper.ParseEnum(y.Severity, test.Severity, at + ".severity", test.Source);
        var description = Optional(y.Description);
        TestAssertion assertion = subject switch
        {
            "count" => MapCount(y, at, test),
            "field" or "column" => MapValue(y, subject == "column", at, test),
            "aggregate" => MapAggregate(y, at, test),
            "unique" => MapUnique(y, at, test),
            "groupBy" => MapGroup(y, at, test),
            "recordSet" => MapRecordSet(y, at, test),
            "knownValues" => MapKnownValues(y, at, test),
            "conforms" => MapConforms(y, at, test),
            "mapping" => MapMapping(y, at, test),
            "indexed" => MapIndexed(y, at, test),
            "legal" => MapLegal(y, at, test),
            "delivered" => MapDelivered(y, at, test),
            "rowCount" => MapRowCount(y, at, test),
            "columns" => MapColumns(y, at, test),
            _ => MapMonotonic(y, at, test),
        };

        var name = Optional(y.Name);
        if (name is not null && name.Length > MaxLabel)
        {
            throw new FlowValidationException($"{test.Source}: {at}.name is longer than {MaxLabel} characters.");
        }

        return assertion with
        {
            Label = name ?? Clip(assertion.Label, MaxLabel),
            IsNamed = name is not null,
            Description = description,
            Severity = severity,
            Disabled = DisabledOf(y.Disabled, at, test.Source),
            AsOf = DateOf(y.AsOf, at + ".asOf", test.Source),
            ValidUntil = ValidUntilOf(y.AsOf ?? test.AsOf, y.ValidUntil, at, test.Source),
            ValidFor = ValidForOf(y.AsOf ?? test.AsOf, y.ValidUntil, y.ValidFor, at, test.Source),
        };
    }

    /// <summary>Refuses every key of <paramref name="y"/> that an assertion of <paramref name="subject"/> does not take.</summary>
    private static void OnlyKeys(AssertionConditionYaml y, string subject, IEnumerable<string> allowed, string at, TestContext test)
    {
        var accepted = new HashSet<string>(allowed.Concat(Common), StringComparer.Ordinal);
        var stray = KeysOf(y).Where(k => !accepted.Contains(k)).ToList();
        if (stray.Count > 0)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} is {Article(subject)} {subject} assertion, which does not take {string.Join(", ", stray)}.");
        }
    }

    private static string Article(string subject) => subject.Length > 0 && "aeiou".Contains(char.ToLowerInvariant(subject[0]), StringComparison.Ordinal) ? "an" : "a";

    private static void RequireBulk(string subject, string at, TestContext test)
    {
        if (!test.Bulk)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} is {Article(subject)} {subject} assertion, which reads each record's bulk data; give the test a bulk block (bulk: {{ columns: [...] }}).");
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";
}
