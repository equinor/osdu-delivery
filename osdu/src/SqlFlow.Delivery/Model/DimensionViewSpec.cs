namespace SqlFlow.Delivery.Model;

/// <summary>
/// Where the pipelines that read a dimension flow's tables and views find them
/// (osdu/docs/reference/flow/dimension-views.md): the module's database, named by the connection a pipeline reading
/// them names it by, so SQLFlow's lineage orders such a pipeline after the flow. A build checks that it reaches the
/// module's own database.
/// </summary>
/// <param name="Connection">
/// A <c>${env:NAME}</c> or <c>${keyvault:vault/secret}</c> reference, or a SQL Server connection string whose password is
/// one, as a delivery flow's <c>source.connection</c> is written; never resolved outside the node that runs the flow.
/// </param>
public sealed record DimensionTarget(string Connection);

/// <summary>One join of a view: the column it joins on, the dimension or view it joins to, and the alias the joined columns are read by.</summary>
/// <param name="On">The column joined on as the document writes it: a column of the view's <c>from</c> dimension, bare, or <c>alias.column</c> of an earlier join.</param>
/// <param name="To">The dimension joined to, or the view, by its name as the flow declares it.</param>
/// <param name="As">The alias the joined columns are read by: the dimension's or the view's name unless the document gives one.</param>
/// <param name="View">Whether <paramref name="To"/> is a view of the flow, declared before this one and grouping by the key joined on, rather than a dimension.</param>
public sealed record DimensionViewJoin(string On, string To, string As, bool View = false);

/// <summary>One column a view lists: its name, the expression it is computed by, the type it is converted to, and what it holds.</summary>
/// <param name="Name">The column's name in the view.</param>
/// <param name="Expression">The T-SQL scalar expression as the document writes it.</param>
/// <param name="DataType">The type the value is converted to, as SQLFlow names a type (<c>decimal(18,3)</c>); null keeps the expression's type.</param>
/// <param name="Description">What the column holds, shown with it.</param>
public sealed record DimensionViewColumnSpec(string Name, string Expression, string? DataType, string? Description);

/// <summary>
/// A view a dimension flow declares (osdu/docs/reference/flow/dimension-views.md): the dimensions of the flow side by
/// side at the grain of one of them, joined on the keys they share, with the columns the document lists, each an
/// expression converted to a data type. A build writes it as <c>osdu.v_dim_&lt;name&gt;</c>, from <see
/// cref="Definition"/>, which the document alone settles.
/// </summary>
public sealed record DimensionViewSpec
{
    /// <summary>What every view's name begins with, so a view of the module's is told from a dimension's table at a glance.</summary>
    public const string Prefix = "v_dim_";

    /// <summary>The most views one flow declares.</summary>
    public const int MaxViews = 50;

    /// <summary>The most joins one view makes.</summary>
    public const int MaxJoins = 32;

    /// <summary>The most columns one view lists, beside its <c>partition</c> and <c>id</c>.</summary>
    public const int MaxColumns = 256;

    /// <summary>The longest name of a view, a column of one, or an alias.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest description of a view or a column.</summary>
    public const int MaxDescriptionLength = 1000;

    /// <summary>The columns every view begins with: the partition of the row, and the number of the <c>from</c> row it stands for, which is the view's key.</summary>
    public static readonly IReadOnlyList<string> KeyColumns = ["partition", "id"];

    /// <summary>The view's name as the document gives it.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>The dimension whose rows the view's rows are.</summary>
    public required string From { get; init; }

    /// <summary>The joins, in the order the document writes them; each reads only what the ones before it make available.</summary>
    public IReadOnlyList<DimensionViewJoin> Joins { get; init; } = [];

    /// <summary>The columns the document lists, in its order; empty when it lists none and the view holds every column.</summary>
    public IReadOnlyList<DimensionViewColumnSpec> Columns { get; init; } = [];

    /// <summary>The condition a row of the view is kept by, as the document writes it; null keeps every row of <see cref="From"/>.</summary>
    public string? Where { get; init; }

    /// <summary>
    /// The columns the view groups its rows by, as the document writes them (bare, or <c>alias.column</c>); empty for a view
    /// of a row per row of <see cref="From"/>. A grouped view is a row per partition and group, its columns the ones it groups
    /// by and aggregates, and its <c>id</c> the least number of the rows of its group.
    /// </summary>
    public IReadOnlyList<string> GroupBy { get; init; } = [];

    /// <summary>The most columns a view groups by.</summary>
    public const int MaxGroupBy = 16;

    /// <summary>What the view is in the database: its name, the tables it reads, its columns and the SQL a build writes.</summary>
    public required DimensionViewDefinition Definition { get; init; }

    /// <summary>The view's name in the module's schema: <c>v_dim_&lt;name&gt;</c>.</summary>
    public string ViewName => Prefix + Name;

    /// <summary>Whether <paramref name="name"/> names a view, a view's column or an alias: a letter, then letters, digits and underscores.</summary>
    public static bool IsName(string? name)
        => name is { Length: > 0 and <= MaxNameLength } && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}

/// <summary>A view as a build writes it, settled from its document alone.</summary>
/// <param name="ViewName">The view's name in the module's schema, without the schema (<c>v_dim_Curve</c>).</param>
/// <param name="Tables">The dimension tables the view reads, its <c>from</c> table first, each once.</param>
/// <param name="Columns">The view's columns in order, its <c>partition</c> and <c>id</c> first.</param>
/// <param name="Joins">The joins, in order.</param>
/// <param name="FromSql">The view's <c>FROM</c> clause: the <c>from</c> table, its joins and the columns computed once each.</param>
/// <param name="CreateSql">The statement that writes the view.</param>
/// <param name="Hash">SHA-256 of <paramref name="CreateSql"/>, hex: the same view writes the same text and is not written again.</param>
/// <param name="WhereSql">The SQL of the condition the view keeps its rows by; null when it keeps every row, and for a grouped view, whose condition is inside its <c>FROM</c> clause.</param>
/// <param name="Views">The views of the flow the view joins, by their names in the module's schema (<c>v_dim_...</c>), each once: written before it.</param>
/// <param name="RowsFromSql">For a grouped view, the <c>FROM</c> clause of the rows it groups, its joins in it, which the check counts each join over; null for any other view.</param>
/// <param name="RowsWhereSql">For a grouped view, the condition it keeps the rows it groups by; null when it keeps every row, and for any other view.</param>
public sealed record DimensionViewDefinition(
    string ViewName,
    IReadOnlyList<string> Tables,
    IReadOnlyList<DimensionViewColumnDefinition> Columns,
    IReadOnlyList<DimensionViewJoinDefinition> Joins,
    string FromSql,
    string CreateSql,
    string Hash,
    string? WhereSql,
    IReadOnlyList<string>? Views = null,
    string? RowsFromSql = null,
    string? RowsWhereSql = null)
{
    /// <summary>Whether the view groups its rows.</summary>
    public bool Grouped => RowsFromSql is not null;

    /// <summary>The views the view joins; none when it joins dimensions alone.</summary>
    public IReadOnlyList<string> JoinedViews => Views ?? [];
}

/// <summary>One column of a view as a build writes it.</summary>
/// <param name="Name">The column's name in the view.</param>
/// <param name="Type">The column's type: the data type it is converted to, a table column's own type, or the kind of value an expression gives (<c>text</c>, <c>number</c>, <c>date</c>, <c>time</c>, <c>guid</c>).</param>
/// <param name="Sql">The SQL the column is computed by, over the <c>FROM</c> clause's aliases.</param>
/// <param name="Expression">The expression as the document writes it; null for a column the view holds without one (its key, or every column of a view that lists none).</param>
/// <param name="DataType">The data type the document converts the column to; null for none.</param>
/// <param name="Description">What the column holds.</param>
/// <param name="InputSql">For a converted column, the SQL of the value before it is converted, which the check compares the converted value with; null otherwise.</param>
/// <param name="GroupSql">For a column of a grouped view, the SQL it is computed by in the derived table that groups the rows, over the tables the view reads; null otherwise.</param>
public sealed record DimensionViewColumnDefinition(
    string Name, string Type, string Sql, string? Expression, string? DataType, string? Description, string? InputSql, string? GroupSql = null);

/// <summary>One join of a view as a build writes it.</summary>
/// <param name="Alias">The alias the document reads the joined columns by.</param>
/// <param name="To">The dimension joined to.</param>
/// <param name="On">The column joined on, as the document writes it.</param>
/// <param name="Table">The joined dimension's table.</param>
/// <param name="SqlAlias">The alias the SQL gives the joined table (<c>j1</c>, ...).</param>
/// <param name="OnSql">The SQL of the value joined on, which the check counts the rows that found no row by.</param>
/// <param name="View">Whether the join reads a view of the flow (<paramref name="Table"/> is its name) rather than a dimension's table.</param>
public sealed record DimensionViewJoinDefinition(string Alias, string To, string On, string Table, string SqlAlias, string OnSql, bool View = false);
