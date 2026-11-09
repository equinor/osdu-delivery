using System.Globalization;

namespace SqlFlow.Delivery.Model;

/// <summary>What a rule on the incoming rows judges (<c>dataset.assert</c>, osdu/docs/reference/flow/mapping-dataset-assertions.md).</summary>
public enum DatasetAssertionKind
{
    /// <summary>A value: one column of each row the rule reads, against a condition (<c>column: AclUnreadable, empty: true</c>).</summary>
    Value,

    /// <summary>How many rows the rule reads (<c>count: { atLeast: 2 }</c>).</summary>
    Count,

    /// <summary>That no two of the rows the rule reads share the values of the columns it names (<c>unique: [curve_id]</c>).</summary>
    Unique,
}

/// <summary>Which rows a rule on the incoming rows reads, and so how often it is judged.</summary>
public enum DatasetAssertionPer
{
    /// <summary>Each record's own rows: its row of the record table, or its rows of one child dataset; judged as each record renders.</summary>
    Record,

    /// <summary>Every row of the record table in the run's scope, together; judged once a run, before any record renders.</summary>
    Run,
}

/// <summary>
/// One rule a mapping states on the rows it renders from (<c>dataset.assert</c>, osdu/docs/reference/flow/mapping-dataset-assertions.md):
/// a condition on a column of each row, a condition on how many rows there are, or that no two rows share a set of values;
/// over a record's own row, over its rows of a child dataset, or over every row of the run's scope. A record that breaks
/// it is held or sent as the property assertions' are, and what it finds is recorded with the record's verdict.
/// </summary>
public sealed record DatasetAssertion
{
    /// <summary>The longest name a rule is given.</summary>
    public const int MaxLabel = NodeAssertion.MaxLabel;

    /// <summary>The most columns one <c>unique</c> rule compares.</summary>
    public const int MaxUniqueColumns = 10;

    /// <summary>What a path to the incoming rows starts with, as the property assertions' incoming stage names a row.</summary>
    public const string RowsRoot = "dataset";

    /// <summary>What messages, the ledger and the reports call it: its <c>name</c>, or a label read off what it asserts.</summary>
    public required string Label { get; init; }

    /// <summary>True when the mapping gives the rule its name, false when the label is read off what it asserts.</summary>
    public bool Named { get; init; }

    public string? Description { get; init; }

    /// <summary>Where the mapping writes it (<c>dataset.assert[0]</c>), for messages.</summary>
    public required string Location { get; init; }

    public required DatasetAssertionKind Kind { get; init; }

    public DatasetAssertionPer Per { get; init; } = DatasetAssertionPer.Record;

    /// <summary>The child dataset whose rows the rule reads (<c>rows: curves</c>); null for the record's own row of the record table.</summary>
    public string? Rows { get; init; }

    /// <summary>The column a <see cref="DatasetAssertionKind.Value"/> rule judges.</summary>
    public string? Column { get; init; }

    /// <summary>The columns a <see cref="DatasetAssertionKind.Unique"/> rule compares, in the order written.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>What the column's value meets, for a <see cref="DatasetAssertionKind.Value"/> rule; null for any other.</summary>
    public ValueCondition? Condition { get; init; }

    /// <summary>
    /// What the number of rows meets, for a <see cref="DatasetAssertionKind.Count"/> rule, in the assertion flows' words for a
    /// count (<c>{ atLeast: 2 }</c>, or a number meaning equals); null for any other.
    /// </summary>
    public Comparison? Count { get; init; }

    /// <summary>For a value rule over a child dataset's rows: true holds when one row meets it, false (the default) only when every row does.</summary>
    public bool AnyRow { get; init; }

    /// <summary>The conditions selecting which rows the rule reads: every one of them has to hold of a row.</summary>
    public IReadOnlyList<AssertionFilter> Where { get; init; } = [];

    public AssertionAction OnFail { get; init; } = AssertionAction.Hold;

    /// <summary>
    /// What the rule judges, as its failures name it for every record alike: a column of the record's row
    /// (<c>dataset.AclUnreadable</c>), a column of a child dataset's rows (<c>dataset.curves[].curve_unit</c>), or the rows
    /// themselves (<c>dataset.curves</c>, and <c>dataset</c> for the record table's rows).
    /// </summary>
    public string At => Kind == DatasetAssertionKind.Value
        ? Rows is null ? $"{RowsRoot}.{Column}" : $"{RowsRoot}.{Rows}[].{Column}"
        : Rows is null ? RowsRoot : $"{RowsRoot}.{Rows}";

    /// <summary>The rows the rule reads, as a sentence names them.</summary>
    public string RowsRead => Per == DatasetAssertionPer.Run
        ? "the record table's rows in the run's scope"
        : Rows is null ? "the record's row" : $"the record's rows of {Rows}";

    /// <summary>What the rule asserts, as findings and its default label quote it.</summary>
    public string Expected
    {
        get
        {
            var asserted = Kind switch
            {
                DatasetAssertionKind.Value => $"{Column} {Condition}" + (Rows is null ? string.Empty : AnyRow ? $" in one row of {Rows}" : $" in every row of {Rows}"),
                DatasetAssertionKind.Count => $"count of {RowsRead} {Count}",
                _ => $"{string.Join(", ", Columns)} unique among {RowsRead}",
            };
            return Where.Count == 0 ? asserted : asserted + " where " + string.Join(" and ", Where);
        }
    }

    /// <summary>How many rows a value text names, for messages: <c>1 row</c>, <c>3 rows</c>.</summary>
    internal static string RowsText(long count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " row" : " rows");
}
