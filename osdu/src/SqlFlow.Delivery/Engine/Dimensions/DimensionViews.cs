using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// What a column of a dimension's table holds as a key another dimension's table is keyed by
/// (osdu/docs/reference/flow/dimension-views.md): the id of a record (of an entity type, when the document says
/// which), or the text a path of a record holds exactly. Two columns meet when their keys do.
/// </summary>
public abstract record DimensionJoinKey
{
    /// <summary>A record's id, without its version: a dimension keyed by <c>id</c> holds them, and a column kept as <c>id</c>.</summary>
    /// <param name="EntityType">The entity type the ids are of, when the kind the dimension reads names one.</param>
    public sealed record Id(string? EntityType) : DimensionJoinKey;

    /// <summary>The text a record holds at a path, exactly: a dimension keyed by the path holds it, and a column kept as <c>key</c> read there.</summary>
    /// <param name="Path">The path, its filters left out.</param>
    public sealed record AtPath(string Path) : DimensionJoinKey;

    /// <summary>The key as a sentence names it.</summary>
    public string Describe() => this switch
    {
        Id { EntityType: { } type } => $"the id of a {type}",
        Id => "a record's id",
        AtPath at => $"the text {at.Path} holds",
        _ => "a key",
    };
}

/// <summary>
/// A view's SQL (osdu/docs/reference/flow/dimension-views.md), laid out from what its document declares: the tables of
/// the dimensions it reads and the columns an expression may name in each, the keys a join may compare, the columns a
/// view holds when its document lists none, the statement that writes the view, and the statements its check reads it
/// by. Every join compares the partition and the key exactly: the key's hash finds the row through an index, and the
/// text is compared under a binary collation, so ids that differ only in case never meet, whatever the database
/// compares text by.
/// </summary>
public static class DimensionViews
{
    /// <summary>The alias the SQL gives the view's <c>from</c> table.</summary>
    public const string FromSqlAlias = "b";

    /// <summary>The longest name SQL Server gives a column.</summary>
    public const int MaxSqlNameLength = 128;

    /// <summary>The columns of <paramref name="dimension"/>'s table as an expression names them, under the alias the document and the SQL read the table by.</summary>
    public static IReadOnlyList<ViewColumnSource> ColumnsOf(DimensionSpec dimension, string alias, string sqlAlias)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ViewColumnSource Text(string name, int length) => new(alias, sqlAlias, name, ViewValue.Text, string.Create(CultureInfo.InvariantCulture, $"nvarchar({length})"));
        ViewColumnSource Number(string name, string type) => new(alias, sqlAlias, name, ViewValue.Number, type);
        var columns = new List<ViewColumnSource>
        {
            Number("id", "bigint"),
            Text("partition", 256),
            Number("key_id", "bigint"),
            Text(dimension.KeyColumn, DeliveryDimensionValue.MaxOriginalLength),
            Text(dimension.ValueColumn, DimensionSpec.MaxCleanLength),
            Number("records", "bigint"),
            Text("filter", DeliveryDimensionValue.MaxFilterLength),
        };
        if (dimension.Elements is not null)
        {
            columns.Add(Number(DimensionElementsSpec.Column, "int"));
        }

        columns.AddRange(dimension.Attributes.Select(a => Text(a.Name, DeliveryDimensionAttributeValue.MaxValueLength)));
        columns.AddRange((dimension.Elements?.Fields ?? []).Select(f => Text(f.Name, DeliveryDimensionElement.MaxValueLength)));
        return columns;
    }

    /// <summary>The key a dimension's table is keyed by: the id of its records for a dimension keyed by <c>id</c>, else the text its path holds.</summary>
    public static DimensionJoinKey KeyOf(DimensionSpec dimension)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        return string.Equals(dimension.Path, "id", StringComparison.Ordinal)
            ? new DimensionJoinKey.Id(OsduKind.EntityType(dimension.Kind))
            : new DimensionJoinKey.AtPath(dimension.Path);
    }

    /// <summary>
    /// The key <paramref name="column"/> of <paramref name="dimension"/>'s table holds, which a join may compare: its key's
    /// column, or an attribute or an element's field kept as <c>key</c> or <c>id</c>; null, with why, for any other.
    /// </summary>
    public static (DimensionJoinKey? Key, string? Why) KeyOf(DimensionSpec dimension, string column)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentNullException.ThrowIfNull(column);
        if (string.Equals(column, dimension.KeyColumn, StringComparison.OrdinalIgnoreCase))
        {
            return (KeyOf(dimension), null);
        }

        if (dimension.Attribute(column) is { } attribute)
        {
            return attribute.Keep switch
            {
                DimensionValueKeep.Id or DimensionValueKeep.Reference => (new DimensionJoinKey.Id(null), null),
                DimensionValueKeep.Key => (new DimensionJoinKey.AtPath(PathOf(attribute.Collect ?? attribute.Steps[^1])), null),
                _ => (null, $"{column} is an attribute kept as a value, as a person reads it (a reference by the code its id ends with), which matches no key; keep it as key or id ({column}: {{ path: ..., keep: id }})"),
            };
        }

        if (dimension.Elements?.Fields.FirstOrDefault(f => string.Equals(f.Name, column, StringComparison.OrdinalIgnoreCase)) is { } field)
        {
            return field.Keep switch
            {
                DimensionValueKeep.Id or DimensionValueKeep.Reference => (new DimensionJoinKey.Id(null), null),
                DimensionValueKeep.Key => (new DimensionJoinKey.AtPath(FieldPath(dimension.Elements!, field)), null),
                _ => (null, $"{column} is a field kept as a value, as a person reads it, which matches no key; keep it as key or id ({column}: {{ path: ..., keep: id }})"),
            };
        }

        return (null, $"{column} is a column every dimension's table has, which holds no key another dimension is keyed by; a join is on a key's column or a column kept as key or id");
    }

    /// <summary>
    /// Whether a column holding <paramref name="from"/> meets a table keyed by <paramref name="to"/>; null when it does, else
    /// why not.
    /// </summary>
    public static string? Meets(DimensionJoinKey from, DimensionJoinKey to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return (from, to) switch
        {
            (DimensionJoinKey.Id a, DimensionJoinKey.Id b) when a.EntityType is { } left && b.EntityType is { } right
                && !string.Equals(left, right, StringComparison.OrdinalIgnoreCase) =>
                $"it holds {a.Describe()} and the dimension is keyed by {b.Describe()}, and an id carries its entity type, so the two never meet",
            (DimensionJoinKey.Id, DimensionJoinKey.Id) => null,
            (DimensionJoinKey.AtPath a, DimensionJoinKey.AtPath b) when string.Equals(a.Path, b.Path, StringComparison.Ordinal) => null,
            (DimensionJoinKey.AtPath a, DimensionJoinKey.AtPath b) =>
                $"it holds {a.Describe()} and the dimension is keyed by {b.Describe()}; a key is joined to the dimension keyed by the same path, or kept as id and joined to a dimension keyed by id",
            (DimensionJoinKey.Id a, _) =>
                $"it holds {a.Describe()} and the dimension is keyed by {to.Describe()}; an id joins a dimension keyed by id",
            _ =>
                $"it holds {from.Describe()} and the dimension is keyed by {to.Describe()}; keep the column as id to join a dimension keyed by id",
        };
    }

    /// <summary>Whether <paramref name="dimension"/>'s table holds more than one row a key: a row per element, or per value it collects.</summary>
    public static bool RowsPerKey(DimensionSpec dimension)
    {
        ArgumentNullException.ThrowIfNull(dimension);
        return dimension.Elements is not null || dimension.Attributes.Any(a => a.IsCollected);
    }

    /// <summary>
    /// The columns a view holds when its document lists none (osdu/docs/reference/flow/dimension-views.md): every
    /// column of <paramref name="from"/> but its numbers, the partition and the filter, under the names its table gives
    /// them, then every column of each join the same way, prefixed by the join's alias and an underscore, with the joined
    /// row's number as <c>&lt;alias&gt;_id</c>. A joined key is the value joined on, so it is left out.
    /// </summary>
    public static IReadOnlyList<(string Name, ViewColumnSource Source)> DefaultColumns(
        DimensionSpec from, IReadOnlyList<(string Alias, string SqlAlias, DimensionSpec Dimension)> joins)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(joins);
        var hidden = new HashSet<string>(["id", "partition", "key_id", "filter"], StringComparer.OrdinalIgnoreCase);
        var columns = new List<(string, ViewColumnSource)>();
        foreach (var column in ColumnsOf(from, ViewScope.FromAlias, FromSqlAlias).Where(c => !hidden.Contains(c.Column)))
        {
            columns.Add((column.Column, column));
        }

        foreach (var (alias, sqlAlias, dimension) in joins)
        {
            foreach (var column in ColumnsOf(dimension, alias, sqlAlias)
                .Where(c => string.Equals(c.Column, "id", StringComparison.Ordinal) || (!hidden.Contains(c.Column) && !string.Equals(c.Column, dimension.KeyColumn, StringComparison.OrdinalIgnoreCase))))
            {
                columns.Add(($"{alias}_{column.Column}", column));
            }
        }

        return columns;
    }

    /// <summary>One join as the view's SQL writes it.</summary>
    /// <param name="Table">The joined dimension's table, or the joined view.</param>
    /// <param name="SqlAlias">The alias the SQL gives it.</param>
    /// <param name="KeyColumn">The joined table's key column, or the column of the view it groups by.</param>
    /// <param name="OnSql">The SQL of the value joined on.</param>
    /// <param name="View">Whether <paramref name="Table"/> is a view of the flow, which holds no key's hash to find a row by.</param>
    public sealed record JoinSql(string Table, string SqlAlias, string KeyColumn, string OnSql, bool View = false);

    /// <summary>
    /// The view's <c>FROM</c> clause: <paramref name="fromTable"/>, every join left, compared on the partition, the key's
    /// hash (a dimension's table only, which keeps one) and the key's text exactly, then the values computed once each
    /// (<paramref name="applies"/>, which the columns converting them read).
    /// </summary>
    public static string FromSql(string fromTable, IReadOnlyList<JoinSql> joins, IReadOnlyList<string> applies)
    {
        ArgumentNullException.ThrowIfNull(fromTable);
        ArgumentNullException.ThrowIfNull(joins);
        ArgumentNullException.ThrowIfNull(applies);
        var sql = new StringBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"FROM {DimensionTables.Qualified(fromTable)} AS [{FromSqlAlias}]");
        foreach (var join in joins)
        {
            sql.Append(CultureInfo.InvariantCulture, $"\nLEFT JOIN {DimensionTables.Qualified(join.Table)} AS [{join.SqlAlias}]");
            sql.Append(CultureInfo.InvariantCulture, $"\n    ON [{join.SqlAlias}].[partition] = [{FromSqlAlias}].[partition]");
            if (!join.View)
            {
                sql.Append(CultureInfo.InvariantCulture, $"\n   AND [{join.SqlAlias}].[{DimensionTables.KeyHashColumn}] = {DimensionTables.KeyHash(join.OnSql)}");
            }

            sql.Append(CultureInfo.InvariantCulture,
                $"\n   AND [{join.SqlAlias}].{DimensionViewExpressions.Quoted(join.KeyColumn)} COLLATE {DimensionTables.Exact} = {join.OnSql} COLLATE {DimensionTables.Exact}");
        }

        foreach (var apply in applies)
        {
            sql.Append('\n').Append(apply);
        }

        return sql.ToString();
    }

    /// <summary>
    /// The <c>FROM</c> clause of a grouped view: a derived table, under the alias the <c>from</c> table has, that groups the
    /// rows <paramref name="rowsSql"/> reads and <paramref name="whereSql"/> keeps by their partition and
    /// <paramref name="groupBy"/>, with each column of <paramref name="columns"/> computed there once under its name
    /// (<see cref="DimensionViewColumnDefinition.GroupSql"/>), the partition, and the least number of the rows of each
    /// group, which is the view's key: a row's number is one row's alone, so no two groups share one.
    /// </summary>
    public static string GroupedFromSql(string rowsSql, string? whereSql, IReadOnlyList<string> groupBy, IReadOnlyList<DimensionViewColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(rowsSql);
        ArgumentNullException.ThrowIfNull(groupBy);
        ArgumentNullException.ThrowIfNull(columns);
        var select = new List<string>
        {
            $"[{FromSqlAlias}].[partition] AS [partition]",
            $"MIN([{FromSqlAlias}].[id]) AS [id]",
        };
        select.AddRange(columns.Select(c => $"{c.GroupSql ?? throw new ArgumentException($"Column {c.Name} of a grouped view is computed in no group.", nameof(columns))} AS {DimensionViewExpressions.Quoted(c.Name)}"));
        var where = whereSql is null ? string.Empty : $"\nWHERE {whereSql}";
        var by = string.Join(", ", new[] { $"[{FromSqlAlias}].[partition]" }.Concat(groupBy));
        return $"FROM (\nSELECT\n    {string.Join(",\n    ", select)}\n{rowsSql}{where}\nGROUP BY {by}\n) AS [{FromSqlAlias}]";
    }

    /// <summary>
    /// The statement that writes the view <paramref name="viewName"/> of flow <paramref name="flow"/>: its column list,
    /// <c>partition</c> and <c>id</c> first, and its <c>SELECT</c> over <paramref name="fromSql"/>, never <c>*</c>, keeping
    /// the rows <paramref name="whereSql"/> holds for when the document gives a condition.
    /// </summary>
    public static string CreateSql(string flow, string viewName, IReadOnlyList<DimensionViewColumnDefinition> columns, string fromSql, string? whereSql)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(columns);
        var names = string.Join(", ", columns.Select(c => DimensionViewExpressions.Quoted(c.Name)));
        var select = string.Join(",\n    ", columns.Select(c => c.Sql));
        var who = new string(flow.Where(c => !char.IsControl(c)).ToArray());
        var where = whereSql is null ? string.Empty : $"\nWHERE {whereSql}";
        return $"""
            CREATE OR ALTER VIEW {DimensionTables.Qualified(viewName)} ({names})
            AS
            -- Written by the builds of dimension flow '{who}' from its document; a change made here is undone by its next build.
            SELECT
                {select}
            {fromSql}{where};
            """;
    }

    /// <summary>The rows of a partition of the view, as its check reads them: the partition's, and those its condition keeps.</summary>
    private static string Rows(DimensionViewDefinition view)
        => view.WhereSql is null
            ? $"WHERE [{FromSqlAlias}].[partition] = @partition"
            : $"WHERE [{FromSqlAlias}].[partition] = @partition AND {view.WhereSql}";

    /// <summary>
    /// The rows the view's joins are made over, as the check counts them: for a grouped view the rows it groups, before they
    /// are grouped, which its joins meet; for any other its own rows.
    /// </summary>
    private static (string From, string Where) JoinRows(DimensionViewDefinition view)
        => view.RowsFromSql is { } rows
            ? (rows, view.RowsWhereSql is null
                ? $"WHERE [{FromSqlAlias}].[partition] = @partition"
                : $"WHERE [{FromSqlAlias}].[partition] = @partition AND {view.RowsWhereSql}")
            : (view.FromSql, Rows(view));

    /// <summary>The hash a view's statement is known by: SHA-256 of its UTF-8 bytes, hex.</summary>
    public static string HashOf(string createSql)
    {
        ArgumentNullException.ThrowIfNull(createSql);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(createSql)));
    }

    /// <summary>
    /// The hash a view is known by: its statement's (<see cref="HashOf(string)"/>), and for a view that joins views theirs
    /// too, so the view is written again whenever a view it reads is, and SQL Server reads that view's columns anew.
    /// </summary>
    public static string HashOf(string createSql, IEnumerable<string> joinedViewHashes)
    {
        ArgumentNullException.ThrowIfNull(createSql);
        ArgumentNullException.ThrowIfNull(joinedViewHashes);
        var joined = joinedViewHashes.ToList();
        return joined.Count == 0 ? HashOf(createSql) : HashOf(createSql + "\n-- reads " + string.Join(", ", joined));
    }

    /// <summary>
    /// The statement the check reads a partition of the view by (osdu/docs/reference/flow/dimension-views.md), its
    /// <c>@partition</c> the partition: the rows; for each join the rows that found theirs and those whose value found
    /// none; for each converted column the values that did not convert; and for every column the rows holding a value,
    /// which computes every column of every row, so a value no expression could be written around fails here.
    /// </summary>
    public static string CheckSql(DimensionViewDefinition view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var joins = new List<string>();
        foreach (var join in view.Joins)
        {
            joins.Add($"COUNT_BIG([{join.SqlAlias}].[id])");
            joins.Add($"COUNT_BIG(CASE WHEN {join.OnSql} IS NOT NULL AND [{join.SqlAlias}].[id] IS NULL THEN 1 END)");
        }

        var columns = new List<string>();
        foreach (var column in view.Columns.Where(c => c.InputSql is not null))
        {
            columns.Add($"COUNT_BIG(CASE WHEN {column.InputSql} IS NOT NULL AND {column.Sql} IS NULL THEN 1 END)");
        }

        foreach (var column in view.Columns)
        {
            columns.Add($"COUNT_BIG({column.Sql})");
        }

        if (!view.Grouped)
        {
            var counts = new List<string> { "COUNT_BIG(*)" };
            counts.AddRange(joins);
            counts.AddRange(columns);
            return $"SELECT\n    {string.Join(",\n    ", counts)}\n{view.FromSql}\n{Rows(view)}\nOPTION (MAX_GRANT_PERCENT = 10);";
        }

        // A grouped view's joins meet the rows it groups, so they are counted over those rows, its columns over its groups;
        // the two reads are one row each, read side by side in the order the check reads every view's counts.
        var (rowsFrom, rowsWhere) = JoinRows(view);
        string Named(string prefix, IReadOnlyList<string> sql) => string.Join(",\n        ", sql.Select((c, i) => string.Create(CultureInfo.InvariantCulture, $"{c} AS [{prefix}{i}]")));
        var grouped = $"(\n    SELECT\n        COUNT_BIG(*) AS [n0]{(columns.Count == 0 ? string.Empty : ",\n        " + Named("c", columns))}\n    {view.FromSql.Replace("\n", "\n    ", StringComparison.Ordinal)}\n    {Rows(view)}\n) AS [g]";
        var order = new List<string> { "[g].[n0]" };
        order.AddRange(joins.Select((_, i) => string.Create(CultureInfo.InvariantCulture, $"[r].[j{i}]")));
        order.AddRange(columns.Select((_, i) => string.Create(CultureInfo.InvariantCulture, $"[g].[c{i}]")));
        var from = joins.Count == 0
            ? $"FROM {grouped}"
            : $"FROM {grouped}\nCROSS JOIN (\n    SELECT\n        {Named("j", joins)}\n    {rowsFrom.Replace("\n", "\n    ", StringComparison.Ordinal)}\n    {rowsWhere}\n) AS [r]";
        return $"SELECT\n    {string.Join(",\n    ", order)}\n{from}\nOPTION (MAX_GRANT_PERCENT = 10);";
    }

    /// <summary>The statement that computes one column of a partition of the view, to find which column a failing check failed on.</summary>
    public static string ColumnSql(DimensionViewDefinition view, DimensionViewColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(column);
        return $"SELECT COUNT_BIG({column.Sql})\n{view.FromSql}\n{Rows(view)}\nOPTION (MAX_GRANT_PERCENT = 10);";
    }

    /// <summary>The statement that reads up to three values of a partition a join found no row for, distinct, in order.</summary>
    public static string UnmatchedSql(DimensionViewDefinition view, DimensionViewJoinDefinition join)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(join);
        var (from, where) = JoinRows(view);
        return $"SELECT TOP (3) {join.OnSql}\n{from}\n{where} AND {join.OnSql} IS NOT NULL AND [{join.SqlAlias}].[id] IS NULL\nGROUP BY {join.OnSql}\nORDER BY {join.OnSql};";
    }

    /// <summary>The statement that reads up to three rows of a partition whose value a column's conversion could not read: the row's number and the value.</summary>
    public static string UnconvertedSql(DimensionViewDefinition view, DimensionViewColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(column);
        return $"SELECT TOP (3) [{FromSqlAlias}].[id], LEFT(CONVERT(nvarchar(4000), {column.InputSql}), 256)\n{view.FromSql}\n{Rows(view)} AND {column.InputSql} IS NOT NULL AND {column.Sql} IS NULL\nORDER BY [{FromSqlAlias}].[id];";
    }

    /// <summary>
    /// The tables <paramref name="view"/> reads, as <paramref name="flow"/>'s dimensions declare them, so a build can make one
    /// no build has made yet.
    /// </summary>
    public static IReadOnlyList<DimensionTableSpec> TablesOf(DimensionFlowDefinition flow, DimensionViewSpec view)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(view);
        var names = new HashSet<string>(view.Definition.Tables, StringComparer.OrdinalIgnoreCase);
        return flow.Dimensions
            .Where(d => names.Contains(DimensionTables.NameOf(d.Name)))
            .Select(d => DimensionTables.Of(d.Name, d.KeyColumn, d.ValueColumn, d.Attributes, d.Elements))
            .ToList();
    }

    /// <summary>A path as a key is compared by: its segments' names, its filters left out.</summary>
    private static string PathOf(string written)
        => DimensionPath.Parse(written).Path is { } path ? string.Join('.', path.Segments.Select(s => s.Name)) : written.Trim();

    /// <summary>The path from the record's root a field of an element reads: the elements' path, up as many objects as it says, then its own.</summary>
    private static string FieldPath(DimensionElementsSpec elements, DimensionElementField field)
    {
        var names = elements.Parsed.Segments.Select(s => s.Name).ToList();
        var at = names.Take(names.Count - field.Up);
        return field.Path == DimensionElementsSpec.Self
            ? string.Join('.', at)
            : string.Join('.', at.Concat(DimensionPath.Parse(field.Path).Path?.Segments.Select(s => s.Name) ?? [field.Path]));
    }
}
