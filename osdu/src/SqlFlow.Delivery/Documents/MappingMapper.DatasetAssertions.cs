using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Reads <c>dataset.assert</c> (osdu/docs/reference/flow/mapping-dataset-assertions.md): the rules a mapping states on the
/// rows it renders from, beside the dataset that names them, rather than on a property they are not about. Each rule is one
/// of three: a condition on a column of each row (<c>column</c> and one condition), a comparison of how many rows there are
/// (<c>count</c>), or that no two rows share the values of some columns (<c>unique</c>). It reads the record's own row of
/// the record table, its rows of a child dataset (<c>rows</c>), or every row of the run's scope (<c>per: run</c>), selects
/// rows with <c>where</c>, and says what a record that breaks it does (<c>onFail</c>). Its words are the property
/// assertions' own, so a rule reads the same wherever it is written.
/// </summary>
internal static partial class MappingMapper
{
    /// <summary>The most rules <c>dataset.assert</c> lists.</summary>
    internal const int MaxDatasetAssertions = 50;

    internal const string DatasetAssertKey = "dataset.assert";
    internal const string PerSetting = "per";
    internal const string RowsSetting = "rows";
    internal const string CountSetting = "count";
    internal const string UniqueSetting = "unique";
    internal const string PerRecord = "record";
    internal const string PerRun = "run";

    /// <summary>The words a rule on the incoming rows takes beside a condition.</summary>
    internal static readonly IReadOnlyList<string> DatasetAssertionSettings =
        [NameSetting, "description", PerSetting, RowsSetting, ColumnSetting, CountSetting, UniqueSetting, ValuesSetting, WhereSetting, OnFailSetting, "ignoreCase", "tolerance"];

    /// <summary>The words <c>per</c> takes.</summary>
    internal static readonly IReadOnlyList<string> DatasetAssertionPers = [PerRecord, PerRun];

    /// <summary>The words <c>onFail</c> of a rule on the incoming rows takes: there is no property to leave out.</summary>
    internal static readonly IReadOnlyList<string> DatasetAssertionActions = [AssertionWords.Hold, AssertionWords.Report];

    private const string DatasetAssertExample = "assert: [ { column: AclUnreadable, empty: true } ]";

    /// <summary>The rules <c>dataset.assert</c> lists, or none when it is not written.</summary>
    private static IReadOnlyList<DatasetAssertion> DatasetAssertions(List<object>? written, string source)
    {
        if (written is null)
        {
            return [];
        }

        var at = $"{source}: {DatasetAssertKey}";
        if (written.Count == 0)
        {
            throw new FlowValidationException($"{at} lists no rule; give it one, such as {DatasetAssertExample}, or remove it.");
        }

        if (written.Count > MaxDatasetAssertions)
        {
            throw new FlowValidationException(
                $"{at} lists {written.Count.ToString(CultureInfo.InvariantCulture)} rules, and a mapping states at most {MaxDatasetAssertions.ToString(CultureInfo.InvariantCulture)}; "
                + "fold the ones that ask the same thing into one (in lists the values allowed, between a range).");
        }

        var rules = new List<DatasetAssertion>(written.Count);
        for (var i = 0; i < written.Count; i++)
        {
            var location = $"{DatasetAssertKey}[{i.ToString(CultureInfo.InvariantCulture)}]";
            var rule = ReadDatasetAssertion(written[i], location, source);
            var same = rules.FindIndex(r => string.Equals(r.Label, rule.Label, StringComparison.Ordinal));
            if (same >= 0)
            {
                throw new FlowValidationException(rule.Named || rules[same].Named
                    ? $"{source}: {location} is named '{rule.Label}', as {rules[same].Location} is; each rule has a name of its own, since what it finds is recorded under it."
                    : $"{source}: {location} asserts what {rules[same].Location} asserts ({rule.Label}); remove one of them.");
            }

            rules.Add(rule);
        }

        return rules;
    }

    private static DatasetAssertion ReadDatasetAssertion(object? written, string location, string source)
    {
        var at = $"{source}: {location}";
        if (written is not IDictionary<object, object> { Count: > 0 } item)
        {
            throw new FlowValidationException(
                $"{at} is a rule on the incoming rows: a column and one condition ({{ column: AclUnreadable, empty: true }}), a count of rows "
                + "({ rows: curves, count: { atLeast: 2 } }), or columns no two rows share ({ per: run, unique: [log_name] }).");
        }

        foreach (var key in item.Keys.Select(KeyText))
        {
            if (ConditionReader.Operators.Contains(key, StringComparer.Ordinal) || DatasetAssertionSettings.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            if (key == StageSetting)
            {
                throw new FlowValidationException(
                    $"{at}: a rule on the incoming rows judges the rows as the ingestion tables hold them, so it takes no {StageSetting}; "
                    + $"{StageSetting} belongs to a property's {AssertKey}, where it says whether the value the row gives or the value the record carries is judged.");
            }

            if (key == FieldSetting)
            {
                throw new FlowValidationException(
                    $"{at}: a rule on the incoming rows reads the rows before any record is written, so it names a {ColumnSetting} of them, not a {FieldSetting} of the record; "
                    + $"a rule on a property of the record is written in that property's {AssertKey}.");
            }

            var bare = key.StartsWith(Marker, StringComparison.Ordinal) ? key.TrimStart('$') : null;
            if (bare is not null && (ConditionReader.Operators.Contains(bare, StringComparer.Ordinal) || DatasetAssertionSettings.Contains(bare, StringComparer.Ordinal)))
            {
                throw new FlowValidationException($"{at}: the words of a rule carry no '{Marker}', since they are not properties of the record; write {bare}, not {key}.");
            }

            throw new FlowValidationException(Nearest(key, ConditionReader.Operators.Concat(DatasetAssertionSettings)) is { } nearest
                ? $"{at}: '{key}' is not a word of a rule on the incoming rows. Did you mean '{nearest}'?"
                : $"{at}: '{key}' is not a word of a rule on the incoming rows, which takes {ColumnSetting} and one condition ({AssertionOperators}), or {CountSetting}, or {UniqueSetting}, "
                  + $"and {SettingList([NameSetting, "description", PerSetting, RowsSetting, ValuesSetting, WhereSetting, OnFailSetting])}.");
        }

        var named = new[] { ColumnSetting, CountSetting, UniqueSetting }.Where(k => Has(item, k)).ToList();
        if (named.Count != 1)
        {
            throw new FlowValidationException(named.Count == 0
                ? $"{at} says nothing to judge: name a {ColumnSetting} and one condition ({{ column: AclUnreadable, empty: true }}), a {CountSetting} of rows "
                  + $"({{ rows: curves, count: {{ atLeast: 2 }} }}), or the columns no two rows share ({{ per: run, unique: [log_name] }})."
                : $"{at} names {string.Join(" and ", named)}; a rule judges one of them. Write one rule for each.");
        }

        var kind = named[0] switch
        {
            ColumnSetting => DatasetAssertionKind.Value,
            CountSetting => DatasetAssertionKind.Count,
            _ => DatasetAssertionKind.Unique,
        };

        var per = Word(item, PerSetting, DatasetAssertionPers, at) == PerRun ? DatasetAssertionPer.Run : DatasetAssertionPer.Record;
        var rows = Has(item, RowsSetting) ? RowsName(item, at) : null;
        var operators = ConditionReader.OperatorsOf(item);
        if (kind != DatasetAssertionKind.Value && operators.Count > 0)
        {
            throw new FlowValidationException(kind == DatasetAssertionKind.Count
                ? $"{at}: a {CountSetting} is compared inside it, such as {CountSetting}: {{ atLeast: 2 }}, not with {operators[0].Key} beside it."
                : $"{at}: a {UniqueSetting} rule asks only that no two rows share the values of its columns, and takes no condition ({operators[0].Key}); judge a column's values with a rule of its own.");
        }

        if (kind != DatasetAssertionKind.Value && (Has(item, "ignoreCase") || Has(item, "tolerance")))
        {
            throw new FlowValidationException(
                $"{at}: ignoreCase and tolerance go with a condition on a column's value, and this rule {(kind == DatasetAssertionKind.Count ? "compares how many rows there are" : "compares rows' values exactly")}; remove them.");
        }

        if (per == DatasetAssertionPer.Run)
        {
            RefuseForRun(item, kind, rows, at);
        }
        else if (kind != DatasetAssertionKind.Value && rows is null)
        {
            throw new FlowValidationException(
                $"{at} {(kind == DatasetAssertionKind.Count ? "counts" : "compares")} the record's own row of the record table, and a record has one; "
                + $"write {RowsSetting}: <child dataset> to {(kind == DatasetAssertionKind.Count ? "count" : "compare")} the record's rows of a child dataset, "
                + $"or {PerSetting}: {PerRun} to {(kind == DatasetAssertionKind.Count ? "count" : "compare")} every row of the record table in the run's scope.");
        }

        var anyRow = Word(item, ValuesSetting, AssertionQuantities, at) == "any";
        if (Has(item, ValuesSetting) && (kind != DatasetAssertionKind.Value || rows is null))
        {
            throw new FlowValidationException(
                $"{at}: {ValuesSetting} says whether every row of a child dataset has to meet the condition, or one of them, and this rule "
                + (kind != DatasetAssertionKind.Value ? "judges the rows together" : "judges the record's one row of the record table") + $"; remove {ValuesSetting}.");
        }

        var onFail = DatasetAction(item, at);
        var scope = new TreeScope(rows);
        string? column = null;
        ValueCondition? condition = null;
        Comparison? count = null;
        IReadOnlyList<string> columns = [];
        switch (kind)
        {
            case DatasetAssertionKind.Value:
                column = ColumnName(Text(item, ColumnSetting, at), rows, $"{at}.{ColumnSetting}");
                condition = ConditionReader.Read(
                    operators, Flag(item, "ignoreCase", at), Get(item, "tolerance"), ResolvesInMapping, location, source);
                break;
            case DatasetAssertionKind.Count:
                count = CountOf(Get(item, CountSetting), $"{location}.{CountSetting}", source);
                break;
            default:
                columns = UniqueColumns(Get(item, UniqueSetting), rows, $"{at}.{UniqueSetting}");
                break;
        }

        var where = Filters(item, AssertionStage.Incoming, scope, location, source);
        var name = Text(item, NameSetting, at)?.Trim();
        if (name is not null && (name.Length == 0 || name.Length > DatasetAssertion.MaxLabel))
        {
            throw new FlowValidationException(
                $"{at}: {NameSetting} is what the rule is called wherever what it finds is shown: 1 to {DatasetAssertion.MaxLabel.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        var rule = new DatasetAssertion
        {
            Label = string.Empty,
            Location = location,
            Kind = kind,
            Per = per,
            Rows = rows,
            Column = column,
            Columns = columns,
            Condition = condition,
            Count = count,
            AnyRow = anyRow,
            Where = where,
            OnFail = onFail,
            Description = Text(item, "description", at)?.Trim() is { Length: > 0 } description ? description : null,
        };
        return rule with
        {
            Label = name ?? Clipped(rule.Expected, DatasetAssertion.MaxLabel),
            Named = name is not null,
        };
    }

    /// <summary>
    /// What a rule over every row of the run's scope cannot take: a run's rows are counted and compared by the database over
    /// the record table, before any record renders, so the rule reads the record table alone, judges the rows together, and
    /// selects no rows of its own.
    /// </summary>
    private static void RefuseForRun(IDictionary<object, object> item, DatasetAssertionKind kind, string? rows, string at)
    {
        if (kind == DatasetAssertionKind.Value)
        {
            throw new FlowValidationException(
                $"{at}: {PerSetting}: {PerRun} judges the run's rows together, by how many there are ({CountSetting}) or that no two share some values ({UniqueSetting}); "
                + $"a condition on each row's {ColumnSetting} is judged as each record renders, so leave out {PerSetting}: {PerRun}.");
        }

        if (rows is not null)
        {
            throw new FlowValidationException(
                $"{at}: {PerSetting}: {PerRun} reads every row of the record table in the run's scope, and {RowsSetting} names a child dataset; "
                + $"a record's rows of {rows} are {(kind == DatasetAssertionKind.Count ? "counted" : "compared")} per record, without {PerSetting}: {PerRun}.");
        }

        if (Has(item, WhereSetting))
        {
            throw new FlowValidationException(
                $"{at}: {PerSetting}: {PerRun} is judged by the database over every row of the run's scope, which a {WhereSetting} does not narrow; "
                + "narrow the rows a run reads with the flow's scope, or judge the rows of each record without per: run.");
        }
    }

    /// <summary>A rule's <c>onFail</c>: hold, the default, or report; a rule on the rows judges no property to leave out.</summary>
    private static AssertionAction DatasetAction(IDictionary<object, object> item, string at)
    {
        if (!Has(item, OnFailSetting))
        {
            return AssertionAction.Hold;
        }

        var written = Text(item, OnFailSetting, at)?.Trim() ?? string.Empty;
        return written switch
        {
            AssertionWords.Hold => AssertionAction.Hold,
            AssertionWords.Report => AssertionAction.Report,
            AssertionWords.Omit => throw new FlowValidationException(
                $"{at}: onFail: omit leaves a property's value out of the record, and a rule on the incoming rows judges no property; "
                + "hold the record (onFail: hold, the default) or send it and record the failure (onFail: report). To leave a value out, assert on its property."),
            _ => throw new FlowValidationException(
                $"{at}: {OnFailSetting} is '{written}'; write hold (the default, keeps the record from being sent) or report (sends it and records the failure)."),
        };
    }

    /// <summary>The child dataset a rule reads the rows of, as the flow declares it under <c>source.datasets</c>.</summary>
    private static string RowsName(IDictionary<object, object> item, string at)
    {
        var rows = Text(item, RowsSetting, at)?.Trim() ?? string.Empty;
        if (!NamePattern().IsMatch(rows))
        {
            throw new FlowValidationException(
                $"{at}: {RowsSetting} names a child dataset whose rows the rule reads, as the flow declares it under source.datasets, such as {RowsSetting}: curves; '{rows}' is not one.");
        }

        return rows;
    }

    /// <summary>A column of the rows a rule reads, written by its name alone.</summary>
    private static string ColumnName(string? written, string? rows, string at)
    {
        var column = written?.Trim() ?? string.Empty;
        if (NamePattern().IsMatch(column))
        {
            return column;
        }

        throw new FlowValidationException(column.StartsWith(DatasetReference + ".", StringComparison.Ordinal) && rows is not null
            ? $"{at}: '{column}' reads the record's row, and the rule judges the rows of {rows}; judge a column of the record's row in a rule without {RowsSetting}, "
              + $"or select the rows of {rows} by it with {WhereSetting}: [ {{ {ColumnSetting}: {column}, ... }} ]."
            : $"{at}: '{column}' is not a column; name a column of {(rows is null ? "the record table" : "the rows of " + rows)} as it is, such as {(rows is null ? "AclUnreadable" : "curve_unit")}.");
    }

    /// <summary>The columns a <c>unique</c> rule compares: one name, or a list of them, each once.</summary>
    private static List<string> UniqueColumns(object? written, string? rows, string at)
    {
        List<object?> listed = written switch
        {
            string one => [one],
            IEnumerable<object> many => [.. many],
            _ => throw new FlowValidationException($"{at} names the columns no two rows share, one or a list, such as {UniqueSetting}: [curve_id]."),
        };
        if (listed.Count is 0 or > DatasetAssertion.MaxUniqueColumns)
        {
            throw new FlowValidationException(
                $"{at} names between 1 and {DatasetAssertion.MaxUniqueColumns.ToString(CultureInfo.InvariantCulture)} columns, such as {UniqueSetting}: [curve_id].");
        }

        var columns = new List<string>(listed.Count);
        foreach (var (column, i) in listed.Select((c, i) => (c, i)))
        {
            var text = column switch
            {
                string one => one,
                null or IDictionary<object, object> or IEnumerable<object> => null,
                var scalar => ScalarText(scalar),
            };
            var name = ColumnName(text, rows, $"{at}[{i.ToString(CultureInfo.InvariantCulture)}]");
            if (columns.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new FlowValidationException($"{at} names {name} more than once.");
            }

            columns.Add(name);
        }

        return columns;
    }

    /// <summary>How many rows a <c>count</c> rule expects, in the assertion flows' words for a count; a query's value is theirs alone.</summary>
    private static Comparison CountOf(object? written, string location, string source)
    {
        if (written is null)
        {
            throw new FlowValidationException($"{source}: {location} has no value; compare the count, such as {CountSetting}: {{ atLeast: 2 }}.");
        }

        var count = ConditionReader.CountComparison(written, location, source);
        if (count.Terms.Any(t => t.Value.Pending || t.Upper is { Pending: true }))
        {
            throw new FlowValidationException(
                $"{source}: {location} compares with a query's value, which an assertion flow runs; a mapping compares a count with numbers, such as {CountSetting}: {{ atLeast: 2 }}.");
        }

        return count;
    }
}
