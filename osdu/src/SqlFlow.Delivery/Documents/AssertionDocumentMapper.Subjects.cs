using System.Collections;
using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

// The assertions whose subject is the test's records as a whole or as the platform holds them: count, rowCount,
// conforms, mapping, indexed, legal and delivered (osdu/docs/reference/flow/staged-accounting.md), and the bulk
// data's columns and order, unique fields, groups and record sets.
internal static partial class AssertionMapper
{
    private static CountAssertion MapCount(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "count", ["count", "warnWithin"], at, test);
        var comparison = ConditionReader.CountComparison(y.Count!, at + ".count", test.Source);
        var band = ConditionReader.Band(y.WarnWithin, at, test.Source);
        if (band is not null)
        {
            ConditionReader.CheckBand(band, null, comparison, at, test.Source);
        }

        return new CountAssertion(comparison) { WarnWithin = band, Label = $"count {comparison}" };
    }

    private static RowCountAssertion MapRowCount(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "rowCount", ["rowCount"], at, test);
        RequireBulk("rowCount", at, test);
        var comparison = ConditionReader.CountComparison(y.RowCount!, at + ".rowCount", test.Source);
        return new RowCountAssertion(comparison) { Label = $"rowCount {comparison}" };
    }

    private static ConformsAssertion MapConforms(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "conforms", ["conforms"], at, test);
        RequireTrue(y.Conforms, "conforms", at, test);
        if (test.Read == AssertionRead.Index)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} asserts that every record meets its template, which needs each record as storage holds it; the test reads the index (read: index), which holds only what it indexed. Take read: index out.");
        }

        return new ConformsAssertion { Label = "conforms to its template" };
    }

    /// <summary>
    /// A <c>mapping</c> assertion: the mapping, as <c>Name@version</c>, whose record-stage assertions every record read is held
    /// to. It reads each record whole, as storage holds it; what the index holds of a record leaves out what an assertion
    /// may read.
    /// </summary>
    private static MappingRulesAssertion MapMapping(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "mapping", ["mapping"], at, test);
        var reference = MappingReference(y.Mapping!, at + ".mapping", "the mapping whose assertions the records are held to", "mapping: WellLog@1.4.0", test.Source);

        if (test.Read == AssertionRead.Index)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} holds every record to what mapping {reference} asserts, which needs each record as storage holds it; the test reads the index (read: index), which holds only what it indexed. Take read: index out.");
        }

        return new MappingRulesAssertion(reference) { Label = $"meets what mapping {reference} asserts" };
    }

    private static IndexedAssertion MapIndexed(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "indexed", ["indexed"], at, test);
        RequireTrue(y.Indexed, "indexed", at, test);
        if (test.ByIds)
        {
            throw new FlowValidationException($"{test.Source}: {at} asks the search index how it indexed the records, and the test reads its records by id; give it a query instead.");
        }

        return new IndexedAssertion { Label = "indexed cleanly" };
    }

    private static void RequireTrue(bool? flag, string key, string at, TestContext test)
    {
        if (flag != true)
        {
            throw new FlowValidationException($"{test.Source}: {at}.{key} is written 'true'; to stop asserting it, remove the assertion.");
        }
    }

    private static LegalAssertion MapLegal(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "legal", ["legal"], at, test);
        if (!string.Equals(y.Legal?.Trim(), "valid", StringComparison.Ordinal))
        {
            throw new FlowValidationException($"{test.Source}: {at}.legal is written 'valid': every legal tag the records carry has to be valid.");
        }

        return new LegalAssertion { Label = "legal tags valid" };
    }

    private static DeliveredAssertion MapDelivered(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "delivered", ["delivered", "interface", "exact", "staged", "explained", "values"], at, test);
        var flow = y.Delivered!.Trim();
        if (flow.Length == 0 || flow.Length > FlowDefinition.MaxLedgerNameLength || flow.Any(char.IsControl))
        {
            throw new FlowValidationException($"{test.Source}: {at}.delivered names the delivery flow whose ledger is read: 1 to {FlowDefinition.MaxLedgerNameLength} characters.");
        }

        var named = Optional(y.Interface);
        if (named is not null && !SourceDefinition.IsInterfaceName(named))
        {
            throw new FlowValidationException($"{test.Source}: {at}.interface '{named}' is not an interface name: a letter followed by letters, digits, '_' and '-'.");
        }

        var staged = y.Staged ?? false;
        if (!staged && (y.Explained is not null || y.Values is not null))
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} takes explained and values only with staged: true, which reads the rows {flow} stages; its ledger's delivered records are read under no values and explain nothing.");
        }

        var explained = y.Explained is null ? (AssertionSeverity?)null : FlowMapper.ParseEnum<AssertionSeverity>(y.Explained.Trim(), at + ".explained", test.Source);
        return new DeliveredAssertion
        {
            Flow = flow,
            Interface = named,
            Exact = y.Exact ?? false,
            Staged = staged,
            Explained = explained,
            Values = staged ? DeliveryValues(y.Values, at, test) : null,
            Label = staged
                ? $"every row {flow}{(named is null ? string.Empty : "/" + named)} stages accounted for"
                : $"delivered by {flow}{(named is null ? string.Empty : "/" + named)}",
        };
    }

    /// <summary>
    /// A staged delivered assertion's <c>values</c>: the delivery flow's parameter values its rows are read under, each a literal,
    /// a <c>{name}</c> token of this flow's parameters, or a list of them for a parameter its record scope reads several values
    /// of, written as a removal writes them (osdu/docs/reference/flow/removal.md). Null when it names none.
    /// </summary>
    private static SortedDictionary<string, string>? DeliveryValues(object? declared, string at, TestContext test)
    {
        if (declared is null)
        {
            return null;
        }

        if (declared is not IDictionary map)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at}.values maps the delivery flow's parameters to the values its rows are read under ({{ logSource: COMPOSITE }}).");
        }

        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in map)
        {
            var name = Convert.ToString(entry.Key, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (name.Length == 0)
            {
                throw new FlowValidationException($"{test.Source}: {at}.values names a parameter with an empty name.");
            }

            if (string.Equals(name, PartitionNames.RunValue, StringComparison.Ordinal))
            {
                throw new FlowValidationException(
                    $"{test.Source}: {at}.values.{PartitionNames.RunValue} is set; the partition is the run's, never a value of the delivery flow. Remove it.");
            }

            IReadOnlyList<string?> items = entry.Value switch
            {
                null => [],
                IList list => list.Cast<object?>().Select(i => i is null or IDictionary or IList ? null : Convert.ToString(i, CultureInfo.InvariantCulture)).ToList(),
                IDictionary => [null],
                var scalar => [Convert.ToString(scalar, CultureInfo.InvariantCulture)],
            };
            if (items.Count == 0 || items.Any(string.IsNullOrWhiteSpace))
            {
                throw new FlowValidationException(
                    $"{test.Source}: {at}.values.{name} has no value; give the delivery flow's parameter a value, or a list of values for a parameter its record scope reads several of, or remove it.");
            }

            foreach (var item in items)
            {
                foreach (var token in FlowMapper.Tokens(item!))
                {
                    if (token != PartitionNames.RunValue && !test.Parameters.ContainsKey(token))
                    {
                        throw new FlowValidationException(
                            $"{test.Source}: {at}.values.{name} uses '{{{token}}}', which is neither {{{PartitionNames.RunValue}}} nor declared under parameters.");
                    }
                }
            }

            values[name] = entry.Value is IList ? RunValueList.Of(items.Select(i => i!)) : items[0]!;
        }

        return values;
    }

    private static ColumnsAssertion MapColumns(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "columns", ["columns"], at, test);
        RequireBulk("columns", at, test);
        var c = y.Columns!;
        IReadOnlyList<string> List(List<string>? declared, string key)
        {
            var list = new List<string>();
            foreach (var raw in declared ?? [])
            {
                var column = CheckColumn(raw, $"{at}.columns.{key}", test.Source);
                if (!list.Contains(column, StringComparer.Ordinal))
                {
                    list.Add(column);
                }
            }

            return list;
        }

        var assertion = new ColumnsAssertion
        {
            Includes = List(c.Includes, "includes"),
            Excludes = List(c.Excludes, "excludes"),
            Exactly = List(c.Exactly, "equals"),
            Label = "columns",
        };
        if (assertion.Includes.Count + assertion.Excludes.Count + assertion.Exactly.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {at}.columns names no column: list them under includes, excludes or equals.");
        }

        var both = assertion.Includes.Concat(assertion.Exactly).Intersect(assertion.Excludes, StringComparer.Ordinal).ToList();
        if (both.Count > 0)
        {
            throw new FlowValidationException($"{test.Source}: {at}.columns both expects and excludes {string.Join(", ", both)}.");
        }

        return assertion with { Label = "columns " + assertion.Expected };
    }

    private static MonotonicAssertion MapMonotonic(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "monotonic", ["monotonic", "column"], at, test);
        RequireBulk("monotonic", at, test);
        var column = CheckColumn(y.Column ?? throw new FlowValidationException($"{test.Source}: {at} asserts an order, and names no column to read it in."), at + ".column", test.Source);
        var direction = FlowMapper.ParseEnum<MonotonicDirection>(y.Monotonic!.Trim(), at + ".monotonic", test.Source);
        return new MonotonicAssertion(column, direction) { Label = $"{column} {AssertionText.Of(direction)}" };
    }

    private static UniqueAssertion MapUnique(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "unique", ["unique"], at, test);
        var fields = new List<string>();
        foreach (var raw in y.Unique!)
        {
            var path = ConditionReader.RecordPath(raw, at + ".unique", test.Source);
            if (fields.Contains(path, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{test.Source}: {at}.unique names '{path}' more than once.");
            }

            fields.Add(path);
        }

        if (fields.Count is 0 or > MaxColumns)
        {
            throw new FlowValidationException($"{test.Source}: {at}.unique lists between 1 and {MaxColumns} fields whose values, together, no two records may share.");
        }

        return new UniqueAssertion(fields) { Label = "unique " + string.Join(" + ", fields) };
    }

    private static GroupAssertion MapGroup(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "groupBy", ["groupBy", "groups", "absent", "mode", "groupCount"], at, test);
        if (test.ByIds)
        {
            throw new FlowValidationException($"{test.Source}: {at} groups what the search index holds, and the test reads its records by id; give it a query instead.");
        }

        var field = ConditionReader.RecordPath(y.GroupBy!, at + ".groupBy", test.Source);
        var groups = new List<ExpectedGroup>();
        foreach (var (key, value) in y.Groups ?? [])
        {
            var group = key?.Trim() ?? string.Empty;
            if (group.Length == 0)
            {
                throw new FlowValidationException($"{test.Source}: {at}.groups names a group with an empty key.");
            }

            groups.Add(new ExpectedGroup(group, ConditionReader.CountComparison(value, $"{at}.groups.{group}", test.Source)));
        }

        var absent = new List<string>();
        foreach (var raw in y.Absent ?? [])
        {
            var group = raw?.Trim() ?? string.Empty;
            if (group.Length == 0 || absent.Contains(group, StringComparer.Ordinal))
            {
                throw new FlowValidationException($"{test.Source}: {at}.absent lists an empty group, or one group twice.");
            }

            absent.Add(group);
        }

        var clash = groups.Select(g => g.Key).Intersect(absent, StringComparer.Ordinal).ToList();
        if (clash.Count > 0)
        {
            throw new FlowValidationException($"{test.Source}: {at} both expects and excludes the group {string.Join(", ", clash)}.");
        }

        var mode = y.Mode?.Trim().ToLowerInvariant();
        if (mode is not (null or "includes" or "exact"))
        {
            throw new FlowValidationException($"{test.Source}: {at}.mode '{y.Mode}' is not includes or exact.");
        }

        if (mode is not null && groups.Count == 0)
        {
            throw new FlowValidationException($"{test.Source}: {at}.mode says how the groups listed under groups compare with the ones found, and groups lists none.");
        }

        var groupCount = y.GroupCount is null ? null : ConditionReader.CountComparison(y.GroupCount, at + ".groupCount", test.Source);
        if (groups.Count == 0 && absent.Count == 0 && groupCount is null)
        {
            throw new FlowValidationException($"{test.Source}: {at} groups by {field} and expects nothing of the groups: name them under groups or absent, or bound their number with groupCount.");
        }

        return new GroupAssertion
        {
            Field = field,
            Groups = groups,
            Exact = mode == "exact",
            Absent = absent,
            GroupCount = groupCount,
            Label = $"groups of {field}",
        };
    }

    private static RecordSetAssertion MapRecordSet(AssertionItemYaml y, string at, TestContext test)
    {
        OnlyKeys(y, "recordSet", ["recordSet"], at, test);
        var set = y.RecordSet!;
        var columns = new List<string>();
        foreach (var raw in set.Columns ?? [])
        {
            columns.Add(ConditionReader.RecordPath(raw, at + ".recordSet.columns", test.Source));
        }

        if (columns.Count is 0 or > MaxColumns)
        {
            throw new FlowValidationException($"{test.Source}: {at}.recordSet.columns lists between 1 and {MaxColumns} fields each record is projected onto.");
        }

        var mode = FlowMapper.ParseEnum(set.Mode, RecordSetMode.Exact, at + ".recordSet.mode", test.Source);
        if (mode == RecordSetMode.Ordered && !test.Sorted)
        {
            throw new FlowValidationException(
                $"{test.Source}: {at} compares the records in order, and the test names no sort; the search returns records in no particular order without one. Give the test a sort.");
        }

        if (set.Rows is null || set.Rows.Count > MaxRows)
        {
            throw new FlowValidationException($"{test.Source}: {at}.recordSet.rows lists at most {MaxRows} rows, each a list of {columns.Count} value(s).");
        }

        var rows = new List<IReadOnlyList<ExpectedValue?>>(set.Rows.Count);
        for (var i = 0; i < set.Rows.Count; i++)
        {
            var rowAt = string.Create(CultureInfo.InvariantCulture, $"{at}.recordSet.rows[{i}]");
            var row = set.Rows[i] ?? throw new FlowValidationException($"{test.Source}: {rowAt} is empty.");
            if (row.Count != columns.Count)
            {
                throw new FlowValidationException($"{test.Source}: {rowAt} holds {row.Count} value(s) for {columns.Count} column(s).");
            }

            rows.Add(row.Select(cell => cell is null ? null : ConditionReader.Scalar(cell, rowAt, test.Source)).ToList());
        }

        if (rows.Count == 0 && mode is RecordSetMode.Includes or RecordSetMode.Excludes)
        {
            throw new FlowValidationException($"{test.Source}: {at}.recordSet.rows lists no row, so '{AssertionText.Of(mode)}' has nothing to compare; list the rows, or use mode exact to assert that no record matches.");
        }

        return new RecordSetAssertion
        {
            Columns = columns,
            Mode = mode,
            Rows = rows,
            Label = $"record set of {string.Join(", ", columns)}",
        };
    }
}
