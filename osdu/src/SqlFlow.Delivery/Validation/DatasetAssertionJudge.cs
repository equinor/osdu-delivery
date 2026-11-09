using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// Judges a mapping's rules on the rows it renders from (<c>dataset.assert</c>, osdu/docs/reference/flow/mapping-dataset-assertions.md)
/// on one record: its own row of the record table, or its rows of a child dataset, as the ingestion tables hold them. What a
/// rule finds is recorded on the record's assertion log as an incoming-stage finding, named by the rows it read
/// (<c>dataset.AclUnreadable</c>, <c>dataset.curves[3].curve_unit</c>, <c>dataset.curves</c>), and holds the record or is
/// reported as a property's assertion is. The rules judged once a run (<c>per: run</c>) are the planner's, through
/// <see cref="RunRowsFindings"/>.
/// </summary>
public static class DatasetAssertionJudge
{
    /// <summary>What joins a row's values into one key of the rows sharing them: a character no value of a row holds.</summary>
    private const char UnitSeparator = (char)0x1f;

    /// <summary>Judges every rule <paramref name="rules"/> reads per record on <paramref name="record"/>, onto <paramref name="log"/>.</summary>
    public static void Judge(SourceRecord record, IReadOnlyList<DatasetAssertion> rules, AssertionLog log)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(log);
        foreach (var rule in rules.Where(r => r.Per == DatasetAssertionPer.Record))
        {
            try
            {
                JudgeOne(record, rule, log);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A defect of one rule's judging must not pass a record as meeting it, nor stop the others being judged.
                log.Judged();
                log.Fail(rule.At, rule.At, rule.Label, AssertionStage.Incoming, rule.OnFail, string.Empty,
                    string.Create(CultureInfo.InvariantCulture, $"could not be judged ({ex.GetType().Name}: {ex.Message}), so it counts as failed"));
            }
        }
    }

    private static void JudgeOne(SourceRecord record, DatasetAssertion rule, AssertionLog log)
    {
        // A condition on the record's own row says whether the rule is the record's to meet at all, as a property
        // assertion's where does; a condition on a child dataset's rows selects the rows the rule reads.
        if (!rule.Where.Where(f => f.Column!.Child is null).All(filter => Selects(filter, record.Row)))
        {
            return;
        }

        IReadOnlyList<SourceRow> rows = rule.Rows is null ? [record.Row] : record.ScopeRows(rule.Rows);
        var selected = new List<(SourceRow Row, int Index)>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (rule.Where.Where(f => f.Column!.Child is not null).All(filter => Selects(filter, row)))
            {
                selected.Add((row, i));
            }
        }

        switch (rule.Kind)
        {
            case DatasetAssertionKind.Value when rule.AnyRow:
                JudgeAnyRow(rule, selected, log);
                return;
            case DatasetAssertionKind.Value:
                foreach (var (row, index) in selected)
                {
                    var value = MappingAssertionJudge.Incoming(row.Get(rule.Column!));
                    if (MappingAssertionJudge.Judge(rule.Condition!, value, log) is { } reason)
                    {
                        log.Fail(rule.At, ValuePath(rule, index), rule.Label, AssertionStage.Incoming, rule.OnFail, MappingAssertionJudge.Shown(value), reason);
                    }
                }

                return;
            case DatasetAssertionKind.Count:
                log.Judged();
                var count = selected.Count;
                if (!ValueComparer.Evaluate(rule.Count!, Measured.Of(count), null))
                {
                    log.Fail(rule.At, rule.At, rule.Label, AssertionStage.Incoming, rule.OnFail, count.ToString(CultureInfo.InvariantCulture), CountReason(rule, count));
                }

                return;
            default:
                log.Judged();
                foreach (var shared in Shared(rule, selected))
                {
                    log.Fail(rule.At, $"{rule.At}[{shared.Indexes[0].ToString(CultureInfo.InvariantCulture)}]", rule.Label, AssertionStage.Incoming, rule.OnFail,
                        shared.Values,
                        $"rows {Listed(shared.Indexes)} of {rule.Rows} share it, and the rule asks that no two rows do");
                }

                return;
        }
    }

    /// <summary>A value rule over a child dataset's rows with <c>values: any</c>: judged once, holding when one row meets it.</summary>
    private static void JudgeAnyRow(DatasetAssertion rule, List<(SourceRow Row, int Index)> selected, AssertionLog log)
    {
        string? first = null;
        JsonNode? firstValue = null;
        var judged = 0;
        foreach (var (row, _) in selected)
        {
            var value = MappingAssertionJudge.Incoming(row.Get(rule.Column!));
            switch (MappingAssertionJudge.Meets(rule.Condition!, value, out var reason))
            {
                case true:
                    log.Judged();
                    return;
                case false:
                    judged++;
                    if (first is null)
                    {
                        first = reason;
                        firstValue = value;
                    }

                    break;
            }
        }

        if (judged == 0)
        {
            // No row was there to judge, or none gives a value a condition other than exists or empty judges.
            return;
        }

        log.Judged();
        log.Fail(rule.At, rule.At, rule.Label, AssertionStage.Incoming, rule.OnFail, MappingAssertionJudge.Shown(firstValue),
            string.Create(CultureInfo.InvariantCulture, $"none of its {judged} rows meets it; one {first}"));
    }

    /// <summary>Whether a <c>where</c> condition holds of the row whose column it names.</summary>
    private static bool Selects(AssertionFilter filter, SourceRow row)
        => ValueComparer.Selects(filter.Condition, MappingAssertionJudge.Present(MappingAssertionJudge.Incoming(row.Get(filter.Column!.Column))));

    /// <summary>Where a value a rule judged was read: the record's row (<c>dataset.AclUnreadable</c>), or a row of a child dataset (<c>dataset.curves[3].curve_unit</c>).</summary>
    private static string ValuePath(DatasetAssertion rule, int index)
        => rule.Rows is null
            ? rule.At
            : string.Create(CultureInfo.InvariantCulture, $"{DatasetAssertion.RowsRoot}.{rule.Rows}[{index}].{rule.Column}");

    /// <summary>Why a count misses its comparison, the rows it counted named.</summary>
    internal static string CountReason(DatasetAssertion rule, long count)
    {
        var selecting = rule.Where.Where(f => f.Column?.Child is not null).ToList();
        var where = selecting.Count == 0 ? string.Empty : $" where {string.Join(" and ", selecting)}";
        var counted = rule.Per == DatasetAssertionPer.Run
            ? $"the run's scope holds {DatasetAssertion.RowsText(count)} of the record table"
            : $"the record has {DatasetAssertion.RowsText(count)} of {rule.Rows}{where}";
        return $"{counted}, and the rule asks for count {rule.Count}";
    }

    /// <summary>
    /// The sets of rows sharing the values of a <c>unique</c> rule's columns, each with the values (<c>curve_id 'GR'</c>) and
    /// the places of its rows, in the order their first rows come. A row lacking a value in one of the columns is compared
    /// with none, since no value is no value to share. Values compare exactly, as the row holds them.
    /// </summary>
    private static IEnumerable<(string Values, IReadOnlyList<int> Indexes)> Shared(DatasetAssertion rule, List<(SourceRow Row, int Index)> selected)
    {
        var groups = new Dictionary<string, (string Values, List<int> Indexes)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (row, index) in selected)
        {
            var values = rule.Columns.Select(c => MappingAssertionJudge.Incoming(row.Get(c))).ToList();
            if (values.Any(v => v is null))
            {
                continue;
            }

            var key = string.Join(UnitSeparator, values.Select(v => v!.ToJsonString()));
            if (!groups.TryGetValue(key, out var group))
            {
                group = (Described(rule.Columns, values!), []);
                groups[key] = group;
                order.Add(key);
            }

            group.Indexes.Add(index);
        }

        return order.Select(k => groups[k]).Where(g => g.Indexes.Count > 1).Select(g => (g.Values, (IReadOnlyList<int>)g.Indexes));
    }

    /// <summary>The values a row holds in some columns, as a failure shows them: <c>curve_id 'GR', depth '1500'</c>.</summary>
    internal static string Described(IReadOnlyList<string> columns, IReadOnlyList<JsonNode> values)
        => string.Join(", ", columns.Select((c, i) => $"{c} '{MappingAssertionJudge.Shown(values[i])}'"));

    private static string Listed(IReadOnlyList<int> indexes)
    {
        var shown = indexes.Take(10).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        var more = indexes.Count > shown.Count ? string.Create(CultureInfo.InvariantCulture, $" and {indexes.Count - shown.Count} more") : string.Empty;
        return shown.Count == 2 && more.Length == 0 ? $"{shown[0]} and {shown[1]}" : string.Join(", ", shown) + more;
    }
}
