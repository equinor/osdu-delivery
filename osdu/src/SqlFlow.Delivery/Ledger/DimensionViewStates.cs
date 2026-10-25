using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Ledger;

/// <summary>What a view's check came to.</summary>
public static class DimensionViewCheckStatus
{
    /// <summary>The view was read whole: its counts are what it holds.</summary>
    public const string Passed = "passed";

    /// <summary>The view could not be read: a value failed an expression no guard could be written around.</summary>
    public const string Failed = "failed";
}

/// <summary>What a build did with one view of its flow.</summary>
public static class DimensionViewWriteStatus
{
    /// <summary>The view was written, new or changed.</summary>
    public const string Written = "written";

    /// <summary>The view was as its document declares it, so nothing was written; it was still checked.</summary>
    public const string Unchanged = "unchanged";

    /// <summary>The view could not be written, and keeps its last definition.</summary>
    public const string Failed = "failed";
}

/// <summary>A view a build writes, with the tables it reads as the flow's dimensions declare them, so a table no build has made yet can be made.</summary>
public sealed record DimensionViewToWrite(DimensionViewSpec View, IReadOnlyList<DimensionTableSpec> Tables);

/// <summary>
/// The views of a flow as one build run writes them (osdu/docs/reference/flow/dimension-views.md): every view the flow
/// declares, written where it changed and checked in the run's partition, and every view the flow made before and no
/// longer declares, dropped.
/// </summary>
public sealed record DimensionViewWrite
{
    /// <summary>The flow's name, which its views are recorded under.</summary>
    public required string Flow { get; init; }

    /// <summary>The ledger the run builds under, whose partition the checks are kept in.</summary>
    public required Guid FlowLedgerId { get; init; }

    /// <summary>The partition the run builds in, which the check reads.</summary>
    public required string Partition { get; init; }

    public required IReadOnlyList<DimensionViewToWrite> Views { get; init; }

    /// <summary>The platform run.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Who started the run.</summary>
    public required string Actor { get; init; }

    public required DateTime Now { get; init; }
}

/// <summary>What a build did with one view: written, unchanged or failed, why it failed, and what its check found.</summary>
public sealed record DimensionViewOutcome(string Name, string ViewName, string Status, string? Error, DimensionViewCheckState? Check)
{
    /// <summary>Whether the view stopped the run: it could not be written, or its check could not read it.</summary>
    public bool Failed => Status == DimensionViewWriteStatus.Failed || Check?.Status == DimensionViewCheckStatus.Failed;
}

/// <summary>What a build run did with its flow's views: each view it declares, and those it no longer declares, dropped.</summary>
public sealed record DimensionViewsWritten(IReadOnlyList<DimensionViewOutcome> Views, IReadOnlyList<string> Dropped)
{
    public static DimensionViewsWritten None { get; } = new([], []);
}

/// <summary>One join of a view as its record keeps it.</summary>
public sealed record DimensionViewJoinState(string Alias, string To, string On, string Table);

/// <summary>One column of a view as its record keeps it: its type, the expression it is computed by, the type it is converted to, and what it holds.</summary>
public sealed record DimensionViewColumnState(string Name, string Type, string? Expression, string? DataType, string? Description);

/// <summary>
/// A view a dimension flow made (osdu/docs/reference/flow/dimension-views.md): its names, its flow and the ledger of
/// the build that last wrote it, what its document declares, the statement it was last written with, and whether the
/// database holds it now, with why not when it does not.
/// </summary>
public sealed record DimensionViewState(
    int ViewId,
    string Name,
    string ViewName,
    string FlowName,
    Guid LedgerId,
    string? Description,
    string From,
    IReadOnlyList<DimensionViewJoinState> Joins,
    IReadOnlyList<DimensionViewColumnState> Columns,
    IReadOnlyList<string> Tables,
    string Sql,
    bool Written,
    string? Note,
    Guid? WrittenRunId,
    string WrittenBy,
    DateTime WrittenUtc,
    DateTime CreatedUtc,
    string? Where,
    IReadOnlyList<string>? GroupBy = null);

/// <summary>What a check found of one join: the rows that found their row, those whose value found none, and some of those values.</summary>
public sealed record DimensionViewJoinCheck(string Alias, string To, string On, long Matched, long Unmatched, IReadOnlyList<string> Examples);

/// <summary>A row whose value a column's conversion could not read: the row's number, and the value as text.</summary>
public sealed record DimensionViewExample(long Id, string Value);

/// <summary>What a check found of one column: the rows holding a value, and for a converted column the values it could not read, with examples.</summary>
public sealed record DimensionViewColumnCheck(string Name, long Values, long? Unconverted, IReadOnlyList<DimensionViewExample> Examples);

/// <summary>
/// What a build's check of a view found in one partition (osdu/docs/reference/flow/dimension-views.md): the rows,
/// each join, each column; or, for a view that could not be read, the column it failed at and SQL Server's message.
/// </summary>
public sealed record DimensionViewCheckState(
    long CheckId,
    string Partition,
    Guid? RunId,
    string Status,
    long Rows,
    IReadOnlyList<DimensionViewJoinCheck> Joins,
    IReadOnlyList<DimensionViewColumnCheck> Columns,
    string? Error,
    DateTime CheckedUtc,
    int DurationMs)
{
    /// <summary>The notes a run's result carries of the check: each join that left rows without their row, each column whose conversion left values, a sentence each.</summary>
    public IReadOnlyList<string> Notes()
    {
        var notes = new List<string>();
        if (Error is not null)
        {
            notes.Add(Error);
        }

        foreach (var join in Joins.Where(j => j.Unmatched > 0))
        {
            notes.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{join.On}: {join.Unmatched:N0} of {Rows:N0} rows name no row of {join.To}{(join.Examples.Count > 0 ? $", for example '{string.Join("', '", join.Examples)}'" : string.Empty)}."));
        }

        foreach (var join in Joins.Where(j => j.Matched == 0 && j.Unmatched == 0 && Rows > 0))
        {
            notes.Add($"{join.On}: no row holds a value to join {join.To} on.");
        }

        foreach (var column in Columns.Where(c => c.Unconverted > 0))
        {
            notes.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{column.Name}: {column.Unconverted:N0} value(s) did not convert{(column.Examples.Count > 0 ? $", for example '{column.Examples[0].Value}' (row {column.Examples[0].Id})" : string.Empty)}."));
        }

        return notes;
    }
}

/// <summary>A view and its newest checks, newest first, as a page shows it.</summary>
public sealed record DimensionViewDetail(DimensionViewState View, IReadOnlyList<DimensionViewCheckState> Checks);

/// <summary>What removing a view took: the view, from the database when it was there, and its record with its checks.</summary>
public sealed record DimensionViewRemoved(string Name, string ViewName, string FlowName, bool Dropped, long Checks)
{
    /// <summary>The removal as its activity's result says it.</summary>
    public string Describe()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"View {Name} of {FlowName} removed: {(Dropped ? $"osdu.{ViewName} dropped" : $"osdu.{ViewName} was not in the database")}, {Checks} check(s) taken with its record.");
}

/// <summary>What a plan found of one view: what stops a build writing it, and what a build will do that the plan says beforehand.</summary>
public sealed record DimensionViewProbe(string Name, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes);

/// <summary>A database as the server names it: its server, its name and when it was made, which tell two databases apart however a connection spells them.</summary>
public sealed record DatabaseIdentity(string Server, string Database, DateTime CreatedUtc);
