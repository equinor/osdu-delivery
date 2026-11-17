using SqlFlow.Core.Query;

namespace SqlFlow.Core.Reports;

/// <summary>
/// A report (<c>flowType: rpt</c>): a set of read-only queries over one connection that a person runs on demand to look
/// at data, each shown as figures, a chart or a table. A report moves no data and is never run as a flow: it has no
/// place in the execution plan, joins no schedule and has no run history. What runs is one query at a time, when a
/// person opens the report or changes one of its parameters, as a read-only statement whose only inputs are the values
/// of the parameters the report declares.
/// </summary>
public sealed record ReportFlow
{
    /// <summary>The <c>flowType</c> value of a report.</summary>
    public const string FlowType = "rpt";

    /// <summary>The longest name a report may carry: the width the catalog keeps a pipeline's name in.</summary>
    public const int MaxNameLength = 400;

    /// <summary>The longest title, of a report, a query or a view.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>The longest description, of a report, a parameter, a query or a view.</summary>
    public const int MaxDescriptionLength = 1000;

    /// <summary>The longest <see cref="Batch"/>: the width the catalog keeps a pipeline's batch in.</summary>
    public const int MaxBatchLength = 250;

    /// <summary>The longest connection reference.</summary>
    public const int MaxConnectionLength = 512;

    /// <summary>The longest database name.</summary>
    public const int MaxDatabaseLength = 128;

    /// <summary>The longest pool name.</summary>
    public const int MaxPoolLength = 128;

    /// <summary>The most parameters a report declares.</summary>
    public const int MaxParameters = 20;

    /// <summary>The most queries a report declares.</summary>
    public const int MaxQueries = 40;

    /// <summary>The most views one query is shown as.</summary>
    public const int MaxViews = 6;

    /// <summary>The most choices a parameter lists.</summary>
    public const int MaxChoices = 200;

    /// <summary>The most columns a query, or a view, names.</summary>
    public const int MaxColumns = 100;

    /// <summary>The longest name of a query.</summary>
    public const int MaxQueryNameLength = 64;

    /// <summary>The longest column name.</summary>
    public const int MaxColumnNameLength = 128;

    /// <summary>The longest label, of a parameter, a choice or a column.</summary>
    public const int MaxLabelLength = 100;

    /// <summary>The longest unit a column's values are followed by.</summary>
    public const int MaxUnitLength = 16;

    /// <summary>The longest statement: what the read-only parser accepts.</summary>
    public const int MaxSqlLength = 20_000;

    /// <summary>How long a query may run when neither it nor the report says, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>The longest a query may be allowed to run, in seconds.</summary>
    public const int MaxTimeoutSeconds = QueryRunRequest.MaxTimeoutSeconds;

    /// <summary>The most rows a query answers when neither it nor the report says.</summary>
    public const int DefaultMaxRows = 1000;

    /// <summary>The most rows a query may be allowed to answer.</summary>
    public const int MaxMaxRows = QueryRunRequest.MaxMaxRows;

    /// <summary>The most categories a bar or share view may be told to show at first.</summary>
    public const int MaxTop = 50;

    /// <summary>The report's name: its identity in the estate and the pipeline row that holds its document.</summary>
    public required string Name { get; init; }

    /// <summary>The heading a person reads; null shows the name.</summary>
    public string? Title { get; init; }

    /// <summary>What the report is for, in a sentence or two.</summary>
    public string? Description { get; init; }

    /// <summary>A grouping label, as on every flow. Reports are listed by it.</summary>
    public string? Batch { get; init; }

    /// <summary>The connection the report's queries run on: a whole <c>${env:...}</c> or <c>${keyvault:...}</c>
    /// reference, or an <c>@alias</c>. Never a connection string.</summary>
    public required string Connection { get; init; }

    /// <summary>The database the queries run in; null runs them in the connection's own.</summary>
    public string? Database { get; init; }

    /// <summary>The worker pool the queries are routed to, for an estate where only some workers reach the connection;
    /// null lets any worker run them.</summary>
    public string? Pool { get; init; }

    /// <summary>How long a query that says nothing may run, in seconds.</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    /// <summary>The most rows a query that says nothing answers.</summary>
    public int MaxRows { get; init; } = DefaultMaxRows;

    /// <summary>The parameters a person may set, in the document's order.</summary>
    public IReadOnlyList<ReportParameter> Parameters { get; init; } = [];

    /// <summary>The queries, in the document's order, which is the order they are shown in.</summary>
    public IReadOnlyList<ReportQuery> Queries { get; init; } = [];

    /// <summary>The query named <paramref name="name"/> (compared without regard to case), or null.</summary>
    public ReportQuery? Query(string name)
        => Queries.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The parameter named <paramref name="name"/> (compared without regard to case, as T-SQL compares a
    /// variable's name), or null.</summary>
    public ReportParameter? Parameter(string name)
        => Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The connection <paramref name="query"/> runs on: its own, or the report's.</summary>
    public string ConnectionOf(ReportQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Connection ?? Connection;
    }

    /// <summary>The database <paramref name="query"/> runs in: its own, or the report's, or null for the connection's.</summary>
    public string? DatabaseOf(ReportQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Database ?? Database;
    }

    /// <summary>How long <paramref name="query"/> may run, in seconds.</summary>
    public int TimeoutOf(ReportQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.TimeoutSeconds ?? TimeoutSeconds;
    }

    /// <summary>The most rows <paramref name="query"/> answers.</summary>
    public int MaxRowsOf(ReportQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.MaxRows ?? MaxRows;
    }

    /// <summary>
    /// The values <paramref name="query"/> binds for a run given <paramref name="given"/>, the values a person set by
    /// parameter name. Only the parameters the statement refers to are bound, each with its declared type. A parameter
    /// that is not in <paramref name="given"/> takes its default, or NULL when it has none; one given null or an empty
    /// value was cleared by the person and binds NULL whatever its default. A given value is checked as a value of the
    /// parameter's type and, for a parameter with a fixed list of choices, as one of them.
    /// </summary>
    /// <exception cref="SqlFlowException">A value names no parameter of the report, is not a value of its parameter's
    /// type or is not one of its choices, or a required parameter the statement binds has no value.</exception>
    public IReadOnlyList<QueryParameter> Bind(ReportQuery query, IReadOnlyDictionary<string, string?> given)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(given);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in given)
        {
            var parameter = Parameter(name) ?? throw new SqlFlowException(Parameters.Count == 0
                ? $"The report '{Name}' declares no parameter, so '{name}' cannot be given a value."
                : $"The report '{Name}' declares no parameter '{name}'; its parameters are: {string.Join(", ", Parameters.Select(p => p.Name))}.");
            if (!values.TryAdd(parameter.Name, value))
            {
                throw new SqlFlowException($"Parameter '{parameter.Name}' is given a value twice.");
            }
        }

        var bound = new List<QueryParameter>();
        foreach (var name in query.Parameters)
        {
            var parameter = Parameter(name)
                ?? throw new SqlFlowException($"Query '{query.Name}' of the report '{Name}' binds '@{name}', which the report does not declare.");
            var value = values.TryGetValue(parameter.Name, out var set)
                ? (string.IsNullOrEmpty(set) ? null : set)
                : parameter.Default;
            if (value is null)
            {
                if (parameter.Required)
                {
                    throw new SqlFlowException(
                        $"Parameter '{parameter.Name}' is required by query '{query.Name}' and has no value.");
                }

                bound.Add(new QueryParameter(parameter.Name, parameter.Type, null));
                continue;
            }

            if (!QueryParameter.TryRead(parameter.Type, value, out _, out var canonical, out var problem))
            {
                throw new SqlFlowException($"The value of parameter '{parameter.Name}' is not {QueryParameterTypes.Named(parameter.Type)}: {problem}.");
            }

            if (parameter.Choices.Count > 0
                && !parameter.Choices.Any(c => string.Equals(c.Value, canonical, StringComparison.Ordinal)))
            {
                throw new SqlFlowException(
                    $"The value of parameter '{parameter.Name}' is not one of its choices: {string.Join(", ", parameter.Choices.Select(c => c.Value))}.");
            }

            bound.Add(new QueryParameter(parameter.Name, parameter.Type, canonical));
        }

        return bound;
    }
}

/// <summary>One value a person may set on a report, which its queries bind as <c>@name</c>.</summary>
public sealed record ReportParameter
{
    /// <summary>The parameter's name: the <c>@name</c> a statement refers to, written without the <c>@</c>.</summary>
    public required string Name { get; init; }

    /// <summary>What a person reads beside the control; null shows the name.</summary>
    public string? Label { get; init; }

    /// <summary>What the parameter narrows, in a sentence.</summary>
    public string? Description { get; init; }

    /// <summary>The value's type: one of <see cref="QueryParameterTypes"/>.</summary>
    public string Type { get; init; } = QueryParameterTypes.Text;

    /// <summary>The value a query binds when a person sets none, in its type's form; null binds NULL.</summary>
    public string? Default { get; init; }

    /// <summary>True when a query that binds the parameter does not run until it has a value.</summary>
    public bool Required { get; init; }

    /// <summary>The values a person picks from, when the document lists them; empty lets any value of the type.</summary>
    public IReadOnlyList<ReportChoice> Choices { get; init; } = [];

    /// <summary>The query of this report whose rows are the choices: its first column the value, its second (when it
    /// has one) the label. Null when the choices are not read from a query.</summary>
    public string? ChoicesFrom { get; init; }
}

/// <summary>One value a parameter may take, with the words shown for it.</summary>
/// <param name="Value">The value, in its type's form.</param>
/// <param name="Label">What a person reads; null shows the value.</param>
public sealed record ReportChoice(string Value, string? Label);

/// <summary>One read-only query of a report, and how its answer is shown.</summary>
public sealed record ReportQuery
{
    /// <summary>The query's name: how a request, a parameter's <c>choicesFrom</c> and an address refer to it.</summary>
    public required string Name { get; init; }

    /// <summary>The heading a person reads; null shows the name.</summary>
    public string? Title { get; init; }

    /// <summary>What the answer says, in a sentence.</summary>
    public string? Description { get; init; }

    /// <summary>The heading the query is shown under; consecutive queries naming one section share it. Null puts the
    /// query under the section before it, or under none.</summary>
    public string? Section { get; init; }

    /// <summary>The connection the query runs on when it is not the report's.</summary>
    public string? Connection { get; init; }

    /// <summary>The database the query runs in when it is not the report's.</summary>
    public string? Database { get; init; }

    /// <summary>How long the query may run, in seconds, when it is not the report's.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>The most rows the query answers, when it is not the report's.</summary>
    public int? MaxRows { get; init; }

    /// <summary>The statement: one read-only <c>SELECT</c>.</summary>
    public required string Sql { get; init; }

    /// <summary>The report's parameters the statement refers to, as the report spells them, in the order the statement
    /// first names each.</summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];

    /// <summary>How the answer is shown, in order; one <see cref="ReportViewTypes.Auto"/> view when the document says
    /// nothing, and none for a query that only feeds a parameter's choices.</summary>
    public IReadOnlyList<ReportView> Views { get; init; } = [];

    /// <summary>How the answer's columns are labelled and written, for the columns the document says anything about.</summary>
    public IReadOnlyList<ReportColumn> Columns { get; init; } = [];
}

/// <summary>The ways a query's answer is shown.</summary>
public static class ReportViewTypes
{
    /// <summary>Chosen from the answer's shape: figures for one row of numbers, a line over dates, bars by one name,
    /// otherwise a table.</summary>
    public const string Auto = "auto";

    /// <summary>One large figure per value of the first row.</summary>
    public const string Figure = "figure";

    /// <summary>Horizontal bars, one per row, in the order the query answers.</summary>
    public const string Bar = "bar";

    /// <summary>Vertical columns, one per row, in the order the query answers: for buckets that have an order.</summary>
    public const string Column = "column";

    /// <summary>A line over an ordered column, usually a date.</summary>
    public const string Line = "line";

    /// <summary>One bar divided into the parts of a whole.</summary>
    public const string Share = "share";

    /// <summary>The rows as they are.</summary>
    public const string Table = "table";

    /// <summary>A grid of one value by two names: rows down, columns across.</summary>
    public const string Matrix = "matrix";

    /// <summary>Not shown: the query only feeds a parameter's choices.</summary>
    public const string None = "none";

    /// <summary>Every type, in the order a message lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Auto, Figure, Bar, Column, Line, Share, Table, Matrix, None];

    /// <summary>Whether <paramref name="type"/> is one of <see cref="All"/>, written exactly.</summary>
    public static bool IsKnown(string? type) => type is not null && All.Contains(type, StringComparer.Ordinal);
}

/// <summary>How wide a view is on the page.</summary>
public static class ReportWidths
{
    /// <summary>A quarter of the page's width.</summary>
    public const string Quarter = "quarter";

    /// <summary>A third of the page's width.</summary>
    public const string Third = "third";

    /// <summary>Half the page's width.</summary>
    public const string Half = "half";

    /// <summary>Two thirds of the page's width.</summary>
    public const string TwoThirds = "two-thirds";

    /// <summary>The page's whole width.</summary>
    public const string Full = "full";

    /// <summary>Every width, narrowest first.</summary>
    public static readonly IReadOnlyList<string> All = [Quarter, Third, Half, TwoThirds, Full];

    /// <summary>Whether <paramref name="width"/> is one of <see cref="All"/>, written exactly.</summary>
    public static bool IsKnown(string? width) => width is not null && All.Contains(width, StringComparer.Ordinal);
}

/// <summary>One way a query's answer is shown.</summary>
public sealed record ReportView
{
    /// <summary>One of <see cref="ReportViewTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>The view's own heading, when it is not the query's.</summary>
    public string? Title { get; init; }

    /// <summary>What the view shows, when it is not the query's description.</summary>
    public string? Description { get; init; }

    /// <summary>One of <see cref="ReportWidths"/>; null takes the width its type has.</summary>
    public string? Width { get; init; }

    /// <summary>The column that names each mark: a bar's or a share's category, a line's or a column chart's position, a
    /// matrix's rows. Null takes the first column that fits.</summary>
    public string? By { get; init; }

    /// <summary>The columns drawn as values (figures, bars, lines), or the columns a table lists; empty takes every
    /// column that fits.</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>The column whose values split one value into several series (stacked bars, a line each).</summary>
    public string? Series { get; init; }

    /// <summary>A matrix's columns: the column whose values are laid out across.</summary>
    public string? Across { get; init; }

    /// <summary>Whether several values or series are stacked on one bar; null stacks series and sets several values side
    /// by side.</summary>
    public bool? Stacked { get; init; }

    /// <summary>How many categories a view shows at first: a bar view lists that many and keeps the rest behind "Show
    /// all"; a share view draws that many parts and adds the rest together as one. Null takes the view type's own
    /// number.</summary>
    public int? Top { get; init; }

    /// <summary>The parameter a click on a mark sets to the mark's <see cref="By"/> value, so a click on one bar
    /// narrows every query that binds it. Null makes the marks not clickable.</summary>
    public string? Selects { get; init; }
}

/// <summary>The ways a column's values are written.</summary>
public static class ReportFormats
{
    /// <summary>As the query answers.</summary>
    public const string Text = "text";

    /// <summary>A whole number with digit grouping.</summary>
    public const string WholeNumber = "integer";

    /// <summary>A number with digit grouping and decimals.</summary>
    public const string Number = "number";

    /// <summary>A fraction written as a percentage: 0.25 reads 25 %.</summary>
    public const string Percent = "percent";

    /// <summary>A count of bytes written in the largest unit that fits: 1536 reads 1.5 KB.</summary>
    public const string Bytes = "bytes";

    /// <summary>A number of seconds written as a duration: 3725 reads 1 h 02 min.</summary>
    public const string Duration = "duration";

    /// <summary>A date without its time.</summary>
    public const string Date = "date";

    /// <summary>A date and time to the second.</summary>
    public const string DateTime = "datetime";

    /// <summary>Yes or no.</summary>
    public const string Flag = "flag";

    /// <summary>Text in a fixed-width face: a key, a code, a file name.</summary>
    public const string Code = "code";

    /// <summary>Every format, in the order a message lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Text, WholeNumber, Number, Percent, Bytes, Duration, Date, DateTime, Flag, Code];

    /// <summary>Whether <paramref name="format"/> is one of <see cref="All"/>, written exactly.</summary>
    public static bool IsKnown(string? format) => format is not null && All.Contains(format, StringComparer.Ordinal);
}

/// <summary>How one column of a query's answer is labelled and written.</summary>
public sealed record ReportColumn
{
    /// <summary>The column's name, as the statement answers it.</summary>
    public required string Name { get; init; }

    /// <summary>What a person reads; null shows the column's name.</summary>
    public string? Label { get; init; }

    /// <summary>One of <see cref="ReportFormats"/>; null takes the format its type has.</summary>
    public string? Format { get; init; }

    /// <summary>How many decimals a number, a percentage, a byte count or a duration shows; null takes the format's
    /// own.</summary>
    public int? Decimals { get; init; }

    /// <summary>What follows each value (rows, files, m).</summary>
    public string? Unit { get; init; }
}
