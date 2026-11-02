using System.Globalization;
using System.Text;

namespace SqlFlow.Delivery.Source;

/// <summary>One child dataset as the SQL builders see it: where it is, how it joins its record, and how its rows are ordered.</summary>
/// <param name="Name">The name a mapping reads the dataset under.</param>
/// <param name="Object">The dataset's table.</param>
/// <param name="Join">Child column to record key column, in record key order.</param>
/// <param name="OrderBy">The columns child rows are ordered by within a record.</param>
/// <param name="Updated">The dataset table's update column, when it carries one.</param>
/// <param name="Deleted">The dataset table's soft-delete column, when it carries one.</param>
/// <param name="MaxRowsPerRecord">The most rows one record may carry.</param>
/// <param name="Filter">The predicate narrowing the dataset's rows (<c>source.datasets.&lt;name&gt;.filter</c>), appended as written; null for none.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming", "CA1720:Identifier contains type name",
    Justification = "The flow document's key is 'object'; the layout mirrors the document so a message about it names what the author wrote.")]
public sealed record IngestionDataset(
    string Name,
    SourceObjectName Object,
    IReadOnlyList<KeyValuePair<string, string>> Join,
    IReadOnlyList<string> OrderBy,
    string? Updated,
    string? Deleted,
    int MaxRowsPerRecord,
    string? Filter = null);

/// <summary>One column of the scope predicate as the SQL builders see it.</summary>
/// <param name="Column">The record table's column.</param>
/// <param name="Parameter">The run parameter whose value fills it.</param>
/// <param name="Values">
/// How many values the run gives the parameter: one compares the column with it, several select the rows holding any of
/// them (<c>IN</c>). A read that fills no scope (the listing of the values a scope can be given) counts one.
/// </param>
public sealed record IngestionScope(string Column, string Parameter, int Values = 1);

/// <summary>What one statement of a person's query reads: columns of the record table and, joined to it, of one child dataset.</summary>
/// <param name="Columns">The record table's columns.</param>
/// <param name="Child">The child dataset joined to it; null for the record table alone.</param>
/// <param name="ChildColumns">The child dataset's columns.</param>
public sealed record InspectionRead(IReadOnlyList<string> Columns, IngestionDataset? Child, IReadOnlyList<string> ChildColumns);

/// <summary>
/// A flow's source as the SQL builders see it, once the flow's declarations have been checked against the tables: the record
/// table with its key columns and their SQL types, the system columns the tables actually carry, the scope predicate and the
/// child datasets.
/// </summary>
public sealed record IngestionLayout
{
    /// <summary>The record table.</summary>
    public required SourceObjectName Record { get; init; }

    /// <summary>The record key columns in key order, with the SQL type each holds.</summary>
    public required IReadOnlyList<SourceKeyColumn> Key { get; init; }

    /// <summary>The record table's identity primary key, when the flow names one (<c>source.record.primaryKey</c>).</summary>
    public SourceKeyColumn? PrimaryKey { get; init; }

    /// <summary>The columns a read pages in and bounds its ranges by: the identity primary key, or the record key without one.</summary>
    public IReadOnlyList<SourceKeyColumn> Order => PrimaryKey is { } primaryKey ? [primaryKey] : Key;

    /// <summary>The record table's update column, which an incremental read windows on.</summary>
    public required string Updated { get; init; }

    /// <summary>The record table's soft-delete column, when it carries one.</summary>
    public string? Deleted { get; init; }

    /// <summary>
    /// The predicate narrowing the record table's rows every read takes (<c>source.record.filter</c>), appended as written; null
    /// for none. A row it leaves out is read as a row of another scope is: never planned, and out of scope to a probe by key.
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>The scope predicate: each record column with the run parameter that fills it, in declaration order.</summary>
    public IReadOnlyList<IngestionScope> Scope { get; init; } = [];

    /// <summary>The child datasets, in name order.</summary>
    public IReadOnlyList<IngestionDataset> Datasets { get; init; } = [];
}

/// <summary>
/// The SQL an ingestion read sends (osdu/docs/reference/concepts/change-detection.md, Which rows a run reads;
/// osdu/docs/reference/flow/delivery.md, source). Every identifier a flow declares is bracket-quoted through
/// <see cref="SourceObjectName"/>, and every value a run supplies (the window, the scope, the keys, the page bounds)
/// goes as a parameter, so a column or table name can never carry a fragment of the statement.
/// </summary>
public static class IngestionSql
{
    /// <summary>The parameter holding the window's exclusive lower bound.</summary>
    public const string LowerParameter = "@lower";

    /// <summary>The parameter holding the window's inclusive upper bound.</summary>
    public const string UpperParameter = "@upper";

    /// <summary>The parameter holding the page size.</summary>
    public const string PageParameter = "@page";

    /// <summary>The parameter holding a JSON array of key tuples (each an array of the key parts, in key order).</summary>
    public const string KeysParameter = "@keys";

    /// <summary>The parameter holding how many ranges of identity values the candidates are counted in.</summary>
    public const string BucketsParameter = "@buckets";

    /// <summary>The parameter holding the column a primary key probe is about.</summary>
    public const string ColumnParameter = "@column";

    /// <summary>The parameter holding the record key's column names, as a JSON array.</summary>
    public const string KeyNamesParameter = "@key_names";

    /// <summary>The prefix of the parameters holding the key the page resumes after.</summary>
    public const string AfterPrefix = "@a";

    /// <summary>The prefix of the parameters holding a key range's exclusive lower bound.</summary>
    public const string FromPrefix = "@f";

    /// <summary>The prefix of the parameters holding a key range's inclusive upper bound.</summary>
    public const string ToPrefix = "@t";

    /// <summary>
    /// The prefix of the parameters holding the scope values: <c>@scope0</c> holds the first column's one value, and
    /// <c>@scope0_0</c>, <c>@scope0_1</c> and on each of its values when the run gives it several.
    /// </summary>
    public const string ScopePrefix = "@scope";

    /// <summary>The parameter naming the table whose columns are read.</summary>
    public const string ObjectParameter = "@object";

    /// <summary>The parameter holding how many values a listing of a scope column's values returns.</summary>
    public const string TopParameter = "@top";

    /// <summary>The parameter holding the pattern a listing of a column's values is narrowed to (<see cref="ContainsPattern"/>).</summary>
    public const string SearchParameter = "@search";

    /// <summary>
    /// The <c>LIKE</c> pattern matching a value that contains <paramref name="text"/>, its wildcards taken as written and in
    /// upper case, which the listing compares the column's text in upper case with, so a search ignores case whatever the
    /// column's collation.
    /// </summary>
    public static string ContainsPattern(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var escaped = new System.Text.StringBuilder(text.Length + 8);
        foreach (var c in text.ToUpperInvariant())
        {
            if (c is '\\' or '%' or '_' or '[')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return "%" + escaped + "%";
    }

    /// <summary>Which bounds a candidate query carries beyond its selection.</summary>
    /// <param name="After">A keyset bound: only values above the page's last one, in the order the read pages in.</param>
    /// <param name="From">A slice's exclusive lower bound.</param>
    /// <param name="To">A slice's inclusive upper bound.</param>
    public readonly record struct KeyBounds(bool After, bool From, bool To)
    {
        /// <summary>No bounds: the whole candidate set.</summary>
        public static KeyBounds None => default;
    }

    /// <summary>The columns and types of one table, from the database that holds it.</summary>
    public static string Columns(SourceObjectName table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var database = SourceObjectName.Quote(table.Database);
        return $"""
            SELECT c.[name] AS column_name, t.[name] AS type_name, c.max_length, c.[precision], c.scale, c.is_nullable
            FROM {database}.sys.columns c
            INNER JOIN {database}.sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.[object_id] = OBJECT_ID({ObjectParameter})
            ORDER BY c.column_id;
            """;
    }

    /// <summary>
    /// What the record table says about a declared primary key: whether the column is an identity column, whether it
    /// alone is the table's primary key, and whether the record key has a unique index without a filter, so no record is
    /// held by two rows that two ranges could deal to two runs.
    /// </summary>
    public static string PrimaryKeyProbe(SourceObjectName table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var database = SourceObjectName.Quote(table.Database);
        return $"""
            DECLARE @id int = OBJECT_ID({ObjectParameter});
            SELECT
                CAST(CASE WHEN EXISTS (
                    SELECT 1 FROM {database}.sys.identity_columns AS c WHERE c.[object_id] = @id AND c.[name] = {ColumnParameter})
                    THEN 1 ELSE 0 END AS int) AS is_identity,
                CAST(CASE WHEN EXISTS (
                    SELECT 1
                    FROM {database}.sys.indexes AS i
                    WHERE i.[object_id] = @id AND i.is_primary_key = 1
                      AND (SELECT COUNT(*) FROM {database}.sys.index_columns AS ic
                           WHERE ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id AND ic.key_ordinal > 0) = 1
                      AND EXISTS (
                          SELECT 1
                          FROM {database}.sys.index_columns AS ic
                          INNER JOIN {database}.sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.column_id = ic.column_id
                          WHERE ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id AND ic.key_ordinal = 1 AND c.[name] = {ColumnParameter}))
                    THEN 1 ELSE 0 END AS int) AS is_primary_key,
                CAST(CASE WHEN EXISTS (
                    SELECT 1
                    FROM {database}.sys.indexes AS i
                    WHERE i.[object_id] = @id AND i.is_unique = 1 AND i.has_filter = 0
                      AND (SELECT COUNT(*) FROM {database}.sys.index_columns AS ic
                           WHERE ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id AND ic.key_ordinal > 0)
                          = (SELECT COUNT(*) FROM OPENJSON({KeyNamesParameter}))
                      AND NOT EXISTS (
                          SELECT 1
                          FROM {database}.sys.index_columns AS ic
                          INNER JOIN {database}.sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.column_id = ic.column_id
                          WHERE ic.[object_id] = i.[object_id] AND ic.index_id = i.index_id AND ic.key_ordinal > 0
                            AND c.[name] NOT IN (SELECT [value] FROM OPENJSON({KeyNamesParameter}))))
                    THEN 1 ELSE 0 END AS int) AS key_is_unique;
            """;
    }

    /// <summary>The moment a read fixes its window at, as the source database keeps time.</summary>
    public static string UpperBound() => "SELECT SYSUTCDATETIME();";

    /// <summary>
    /// Which of the named keys the record table holds, and whether each is in this run's scope. A key the query does not
    /// return is missing; one it returns with <c>in_scope</c> 0 belongs to another scope's run, or is a row the flow's filter
    /// leaves out.
    /// </summary>
    public static string KeyProbe(IngestionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var scope = ScopePredicate(layout, "r");
        if (layout.Filter is not null)
        {
            // The filter's columns resolve against the record table alone, in a read of its own, never against the keys joined here.
            var kept = $"EXISTS (SELECT 1 FROM {RecordRows(layout)} f WHERE {JoinOn(layout, "f", "r")})";
            scope = scope.Length == 0 ? kept : $"{scope} AND {kept}";
        }

        return $"""
            SELECT {KeyList(layout, "r")}, CAST(CASE WHEN {(scope.Length == 0 ? "1 = 1" : scope)} THEN 1 ELSE 0 END AS int) AS in_scope
            FROM {layout.Record.Quoted} r
            INNER JOIN OPENJSON({KeysParameter}) WITH ({OpenJsonColumns(layout)}) j ON {JoinOn(layout, "r", "j")};
            """;
    }

    /// <summary>
    /// One record of this run's scope whose key is unknown, or nothing when every key is known. SQLFlow's ingestion
    /// creates a target's data columns nullable and never tightens them, so a delivery cannot ask its key columns to be
    /// declared NOT NULL; what it needs is that no row it would deliver actually carries a NULL there. Only the columns
    /// that can hold one are tested, and only within the run's scope, so another scope's rows never refuse this run.
    /// </summary>
    public static string NullKeyProbe(IngestionLayout layout, IReadOnlyList<string> nullableKeys)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(nullableKeys);
        if (nullableKeys.Count == 0)
        {
            throw new ArgumentException("A null-key probe needs at least one column that can hold a null.", nameof(nullableKeys));
        }

        var scope = ScopePredicate(layout, "r");
        var unknown = string.Join(" OR ", nullableKeys.Select(k => $"r.{SourceObjectName.Quote(k)} IS NULL"));
        return $"""
            SELECT TOP (1) {string.Join(", ", nullableKeys.Select(k => $"CAST(CASE WHEN r.{SourceObjectName.Quote(k)} IS NULL THEN 1 ELSE 0 END AS int) AS {SourceObjectName.Quote(k)}"))}
            FROM {RecordRows(layout)} r
            WHERE {(scope.Length == 0 ? "1 = 1" : scope)} AND ({unknown});
            """;
    }

    /// <summary>
    /// The distinct values one column of the scope predicate holds, with how many rows hold each, the most rows first, at
    /// most <c>@top</c> of them: over the record table's rows not marked deleted, and never a null, which no parameter
    /// value selects.
    /// </summary>
    public static string ScopeValues(IngestionLayout layout, string column, bool search = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        var value = "r." + SourceObjectName.Quote(column);
        var live = layout.Deleted is { } deleted ? $" AND r.{SourceObjectName.Quote(deleted)} IS NULL" : string.Empty;
        var matching = search ? $" AND UPPER(CAST({value} AS nvarchar(4000))) LIKE {SearchParameter} ESCAPE N'\\'" : string.Empty;
        return $"""
            SELECT TOP ({TopParameter}) {value} AS scope_value, COUNT_BIG(*) AS scope_rows
            FROM {RecordRows(layout)} r
            WHERE {value} IS NOT NULL{live}{matching}
            GROUP BY {value}
            ORDER BY COUNT_BIG(*) DESC, {value};
            """;
    }

    /// <summary>
    /// One page of the record table's keys in the scope, with each row's change moment, its value in the order the read pages
    /// in, and, when the table carries the soft-delete column, when it was marked deleted (the last columns), resuming after the
    /// previous page's last value: every row, whatever its moment. The rows the flow's filter keeps (<see cref="SourceKeyRead.Read"/>),
    /// or every row of the scope (<see cref="SourceKeyRead.Whole"/>), which is how a removal flow reads the table it tags.
    /// </summary>
    public static string KeyPage(IngestionLayout layout, bool after, SourceKeyRead read = SourceKeyRead.Read)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var predicates = new List<string>();
        if (layout.Scope.Count > 0)
        {
            predicates.Add(ScopePredicate(layout, "r"));
        }

        if (after)
        {
            predicates.Add(Above(layout, "r", AfterPrefix));
        }

        var where = predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates);
        var deleted = layout.Deleted is { } column ? $", r.{SourceObjectName.Quote(column)}" : string.Empty;
        return $"""
            SELECT TOP ({PageParameter}) {KeyList(layout, "r")}, r.{SourceObjectName.Quote(layout.Updated)}, {OrderList(layout, "r")}{deleted}
            FROM {(read == SourceKeyRead.Whole ? layout.Record.Quoted : RecordRows(layout))} r
            {where}
            ORDER BY {OrderList(layout, "r")};
            """;
    }

    /// <summary>
    /// Marks the record table's rows of the keys <see cref="KeysParameter"/> names deleted, as SQLFlow's key match does (the
    /// soft-delete column stamped with the database's UTC clock), or takes the mark off them, stamping the update column so an
    /// incremental read meets the row again (osdu/docs/reference/flow/removal-marks.md). Only rows of
    /// the scope, and only rows not already so, are written; the statement answers how many it wrote.
    /// </summary>
    public static string MarkDeleted(IngestionLayout layout, bool deleted)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var column = layout.Deleted
            ?? throw new InvalidOperationException("A row is marked deleted in the record table's soft-delete column, and the layout names none.");
        var mark = SourceObjectName.Quote(column);
        var set = deleted
            ? $"r.{mark} = SYSUTCDATETIME()"
            : $"r.{mark} = NULL, r.{SourceObjectName.Quote(layout.Updated)} = SYSUTCDATETIME()";
        var scope = ScopePredicate(layout, "r");
        return $"""
            SET NOCOUNT ON;
            UPDATE r SET {set}
            FROM {layout.Record.Quoted} r
            INNER JOIN OPENJSON({KeysParameter}) WITH ({OpenJsonColumns(layout)}) j ON {JoinOn(layout, "r", "j")}
            WHERE r.{mark} IS {(deleted ? string.Empty : "NOT ")}NULL{(scope.Length == 0 ? string.Empty : " AND " + scope)};
            SELECT @@ROWCOUNT;
            """;
    }

    /// <summary>
    /// One page of the rows staged in the scope, for a person to go through: each row's key, its change moment, the columns
    /// named, and its value in the order the read pages in (the last columns), narrowed to the rows whose
    /// <paramref name="searched"/> columns hold <see cref="SearchParameter"/> when there is one. The record table alone: what
    /// a row holds in its child datasets is counted apart (<see cref="StagedChildren"/>).
    /// </summary>
    public static string StagedPage(IngestionLayout layout, IReadOnlyList<string> columns, IReadOnlyList<string> searched, bool search, bool after)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(searched);
        var named = string.Concat(columns.Select(c => $", r.{SourceObjectName.Quote(c)}"));
        return $"""
            SELECT TOP ({PageParameter}) {KeyList(layout, "r")}, r.{SourceObjectName.Quote(layout.Updated)}{named}, {OrderList(layout, "r")}
            FROM {RecordRows(layout)} r
            {StagedWhere(layout, searched, search, after)}
            ORDER BY {OrderList(layout, "r")};
            """;
    }

    /// <summary>How many rows are staged in the scope, narrowed as <see cref="StagedPage"/> narrows them.</summary>
    public static string StagedCount(IngestionLayout layout, IReadOnlyList<string> searched, bool search)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(searched);
        return $"""
            SELECT COUNT_BIG(*)
            FROM {RecordRows(layout)} r
            {StagedWhere(layout, searched, search, after: false)};
            """;
    }

    /// <summary>
    /// How many rows each child dataset holds of the records <see cref="KeysParameter"/> names: one result set per dataset, in
    /// the layout's order, each a record's key and its count, a record the dataset holds nothing of left out.
    /// </summary>
    public static string StagedChildren(IngestionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var columns = string.Join(", ", layout.Key.Select(k => $"{SourceObjectName.Quote(k.Name)} {k.SqlType} NOT NULL"));
        var names = string.Join(", ", layout.Key.Select(k => SourceObjectName.Quote(k.Name)));
        var keys = string.Join(", ", layout.Key.Select(k => $"k.{SourceObjectName.Quote(k.Name)}"));
        var sql = new StringBuilder();
        sql.Append("DECLARE @page_keys TABLE (").Append(columns).AppendLine(");");
        sql.Append("INSERT INTO @page_keys (").Append(names).Append(") SELECT ").Append(names)
            .Append(" FROM OPENJSON(").Append(KeysParameter).Append(") WITH (").Append(OpenJsonColumns(layout.Key)).AppendLine(");");
        foreach (var dataset in layout.Datasets)
        {
            var on = string.Join(" AND ", dataset.Join.Select(j => $"c.{SourceObjectName.Quote(j.Key)} = k.{SourceObjectName.Quote(j.Value)}"));
            sql.Append("SELECT ").Append(keys).Append(", COUNT_BIG(*) FROM ").Append(DatasetRows(dataset)).Append(" c INNER JOIN @page_keys k ON ").Append(on);
            if (dataset.Deleted is { } deleted)
            {
                sql.Append(" WHERE c.").Append(SourceObjectName.Quote(deleted)).Append(" IS NULL");
            }

            sql.Append(" GROUP BY ").Append(keys).AppendLine(";");
        }

        return sql.ToString();
    }

    /// <summary>
    /// One statement of a query a person runs (<see cref="SourceQuery"/>): each row's key, the columns <paramref name="read"/>
    /// names and the columns saying where it came from, over the rows a delivery reads, in the order it reads them: the scope's,
    /// without the ones marked deleted, every child dataset's deleted rows left out as a read leaves them out. A child dataset
    /// is left joined, so a record with no child rows shows, with nulls where they would be. The scope's values stay parameters
    /// the script declares (<see cref="ScopeParameter(int)"/>), and <paramref name="keys"/> narrows to the records
    /// <see cref="KeysParameter"/> names.
    /// </summary>
    public static string Inspection(IngestionLayout layout, InspectionRead read, IReadOnlyList<string> origin, long top, bool keys)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(origin);
        var key = layout.Key.Select(k => k.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columns = layout.Key.Select(k => $"r.{SourceObjectName.Quote(k.Name)}")
            .Concat(read.Columns.Where(c => !key.Contains(c)).Select(c => $"r.{SourceObjectName.Quote(c)}"));
        var sql = new StringBuilder();
        if (read.Child is { } child)
        {
            // Under the dataset's name: a record table and its child tables commonly share column names.
            columns = columns.Concat(read.ChildColumns.Select(c => $"c.{SourceObjectName.Quote(c)} AS {SourceObjectName.Quote($"{child.Name}.{c}")}"));
        }

        columns = columns.Concat(origin.Select(c => $"r.{SourceObjectName.Quote(c)}"));
        sql.Append("SELECT ").Append(top > 0 ? $"TOP ({top.ToString(CultureInfo.InvariantCulture)}) " : string.Empty).AppendLine(string.Join(", ", columns));
        sql.Append("FROM ").Append(RecordRows(layout)).AppendLine(" r");
        if (keys)
        {
            sql.Append("INNER JOIN OPENJSON(").Append(KeysParameter).Append(") WITH (").Append(OpenJsonColumns(layout)).Append(") j ON ").AppendLine(JoinOn(layout, "r", "j"));
        }

        var order = new List<string> { OrderList(layout, "r") };
        if (read.Child is { } joined)
        {
            var on = joined.Join.Select(j => $"c.{SourceObjectName.Quote(j.Key)} = r.{SourceObjectName.Quote(j.Value)}").ToList();
            if (joined.Deleted is { } deletedChild)
            {
                on.Add($"c.{SourceObjectName.Quote(deletedChild)} IS NULL");
            }

            sql.Append("LEFT JOIN ").Append(DatasetRows(joined)).Append(" c ON ").AppendLine(string.Join(" AND ", on));
            order.AddRange(joined.OrderBy.Select(o => $"c.{SourceObjectName.Quote(o)}"));
        }

        var predicates = new List<string>();
        if (layout.Scope.Count > 0)
        {
            predicates.Add(ScopePredicate(layout, "r"));
        }

        if (layout.Deleted is { } deleted)
        {
            predicates.Add($"r.{SourceObjectName.Quote(deleted)} IS NULL");
        }

        if (predicates.Count > 0)
        {
            sql.Append("WHERE ").AppendLine(string.Join(" AND ", predicates));
        }

        sql.Append("ORDER BY ").Append(string.Join(", ", order)).Append(';');
        return sql.ToString();
    }

    /// <summary>
    /// The first rows of the record table, or of the child dataset <paramref name="dataset"/>, that a delivery reads
    /// (<see cref="IIngestionSource.ReadTableAsync"/>): <paramref name="select"/>, written over the table read as <c>t</c>, of at
    /// most <see cref="PageParameter"/> rows of the scope's records not marked deleted, in the order a read pages in; then how
    /// many such rows there are, counted up to <see cref="TopParameter"/>. A child row is read when its own row is not marked
    /// deleted either, as a delivery reads it.
    /// </summary>
    internal static string TableRows(IngestionLayout layout, IngestionDataset? dataset, IReadOnlyList<string> select)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(select);
        var record = dataset is null ? "t" : "r";
        var predicates = new List<string>();
        if (layout.Scope.Count > 0)
        {
            predicates.Add(ScopePredicate(layout, record));
        }

        if (layout.Deleted is { } deleted)
        {
            predicates.Add($"{record}.{SourceObjectName.Quote(deleted)} IS NULL");
        }

        var from = $"{layout.Record.Quoted} t";
        var order = OrderList(layout, record);
        if (dataset is not null)
        {
            from = $"{dataset.Object.Quoted} t INNER JOIN {layout.Record.Quoted} r ON "
                + string.Join(" AND ", dataset.Join.Select(j => $"t.{SourceObjectName.Quote(j.Key)} = r.{SourceObjectName.Quote(j.Value)}"));
            if (dataset.Deleted is { } deletedChild)
            {
                predicates.Add($"t.{SourceObjectName.Quote(deletedChild)} IS NULL");
            }

            order = string.Join(", ", new[] { order }.Concat(dataset.OrderBy.Select(o => $"t.{SourceObjectName.Quote(o)}")));
        }

        var where = predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates);
        return $"""
            SELECT TOP ({PageParameter}) {string.Join(", ", select)}
            FROM {from}
            {where}
            ORDER BY {order};
            SELECT COUNT_BIG(*) FROM (SELECT TOP ({TopParameter}) 1 AS one FROM {from} {where}) q;
            """;
    }

    private static string StagedWhere(IngestionLayout layout, IReadOnlyList<string> searched, bool search, bool after)
    {
        var predicates = new List<string>();
        if (layout.Scope.Count > 0)
        {
            predicates.Add(ScopePredicate(layout, "r"));
        }

        if (after)
        {
            predicates.Add(Above(layout, "r", AfterPrefix));
        }

        if (search)
        {
            predicates.Add("(" + string.Join(
                " OR ",
                searched.Select(c => $"UPPER(CAST(r.{SourceObjectName.Quote(c)} AS nvarchar(4000))) LIKE {SearchParameter} ESCAPE N'\\'")) + ")");
        }

        return predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates);
    }

    /// <summary>How many records this selection is expected to meet.</summary>
    public static string CandidateCount(IngestionLayout layout, SourceSelectionKind kind, bool hasLower)
        => $"SELECT COUNT_BIG(*) FROM ({CandidateSet(layout, kind, hasLower, KeyBounds.None)}) q;";

    /// <summary>
    /// One page of candidates, in the order the read pages in (the identity primary key, or the record key without one),
    /// resuming after the previous page's last value.
    /// </summary>
    public static string CandidatePage(IngestionLayout layout, SourceSelectionKind kind, bool hasLower, KeyBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return $"""
            SELECT TOP ({PageParameter}) {OrderList(layout, "q")}
            FROM ({CandidateSet(layout, kind, hasLower, bounds)}) q
            ORDER BY {OrderList(layout, "q")};
            """;
    }

    /// <summary>
    /// How the candidates spread over the identity primary key, for cutting them into slices without ranking them: the
    /// lowest value and the width of a range of values (first result), then how many candidates each non-empty range holds
    /// (second result), about <c>@buckets</c> ranges in all. Both reads aggregate; neither sorts the candidates. A read with
    /// no candidates answers a null lowest value and no ranges.
    /// </summary>
    public static string SliceHistogram(IngestionLayout layout, SourceSelectionKind kind, bool hasLower)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var primaryKey = layout.PrimaryKey
            ?? throw new InvalidOperationException("A read is cut into slices on the record table's identity primary key, and the layout names none.");
        var column = "q." + SourceObjectName.Quote(primaryKey.Name);
        var candidates = CandidateSet(layout, kind, hasLower, KeyBounds.None);
        return $"""
            DECLARE @lowest bigint, @highest bigint;
            SELECT @lowest = CAST(MIN({column}) AS bigint), @highest = CAST(MAX({column}) AS bigint) FROM ({candidates}) q;
            DECLARE @width bigint = CASE WHEN @lowest IS NULL THEN NULL ELSE ((@highest - @lowest) / {BucketsParameter}) + 1 END;
            SELECT @lowest AS lowest, @width AS width;
            SELECT (CAST({column} AS bigint) - @lowest) / @width AS bucket, COUNT_BIG(*) AS candidates
            FROM ({candidates}) q
            WHERE @width IS NOT NULL
            GROUP BY (CAST({column} AS bigint) - @lowest) / @width
            ORDER BY bucket;
            """;
    }

    /// <summary>
    /// The rows of one page: the page's record keys into a table variable, then the record rows, then one result set per
    /// child dataset, each joined on the record keys and ordered by them. A page of identity values finds its record rows
    /// by the primary key and takes their record keys from them.
    /// </summary>
    public static string PageRows(IngestionLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var columns = string.Join(", ", layout.Key.Select(k => $"{SourceObjectName.Quote(k.Name)} {k.SqlType} NOT NULL"));
        var names = string.Join(", ", layout.Key.Select(k => SourceObjectName.Quote(k.Name)));
        var sql = new StringBuilder();
        sql.Append("DECLARE @page_keys TABLE (").Append(columns).AppendLine(");");
        if (layout.PrimaryKey is { } primaryKey)
        {
            var id = SourceObjectName.Quote(primaryKey.Name);
            sql.Append("DECLARE @page_ids TABLE (").Append(id).Append(' ').Append(primaryKey.SqlType).AppendLine(" NOT NULL PRIMARY KEY);");
            sql.Append("INSERT INTO @page_ids (").Append(id).Append(") SELECT ").Append(id)
                .Append(" FROM OPENJSON(").Append(KeysParameter).Append(") WITH (").Append(OpenJsonColumns(layout.Order)).AppendLine(");");
            sql.Append("INSERT INTO @page_keys (").Append(names).Append(") SELECT ").Append(KeyList(layout, "r")).Append(" FROM ")
                .Append(RecordRows(layout)).Append(" r INNER JOIN @page_ids p ON r.").Append(id).Append(" = p.").Append(id).AppendLine(";");
            sql.Append("SELECT r.* FROM ").Append(RecordRows(layout)).Append(" r INNER JOIN @page_ids p ON r.").Append(id).Append(" = p.").Append(id)
                .Append(" ORDER BY r.").Append(id).AppendLine(";");
        }
        else
        {
            sql.Append("INSERT INTO @page_keys (").Append(names).Append(") SELECT ").Append(names)
                .Append(" FROM OPENJSON(").Append(KeysParameter).Append(") WITH (").Append(OpenJsonColumns(layout.Key)).AppendLine(");");
            sql.Append("SELECT r.* FROM ").Append(RecordRows(layout)).Append(" r INNER JOIN @page_keys k ON ").Append(JoinOn(layout, "r", "k"))
                .Append(" ORDER BY ").Append(KeyList(layout, "r")).AppendLine(";");
        }

        foreach (var dataset in layout.Datasets)
        {
            var on = string.Join(" AND ", dataset.Join.Select(j => $"c.{SourceObjectName.Quote(j.Key)} = k.{SourceObjectName.Quote(j.Value)}"));
            var order = dataset.Join.Select(j => $"c.{SourceObjectName.Quote(j.Key)}")
                .Concat(dataset.OrderBy.Select(o => $"c.{SourceObjectName.Quote(o)}"));
            sql.Append("SELECT c.* FROM ").Append(DatasetRows(dataset)).Append(" c INNER JOIN @page_keys k ON ").Append(on);
            if (dataset.Deleted is { } deleted)
            {
                sql.Append(" WHERE c.").Append(SourceObjectName.Quote(deleted)).Append(" IS NULL");
            }

            sql.Append(" ORDER BY ").Append(string.Join(", ", order)).AppendLine(";");
        }

        return sql.ToString();
    }

    /// <summary>
    /// The distinct records a selection covers, as a derived table whose columns are the columns the read pages in: the
    /// identity primary key, or the record key columns without one.
    /// </summary>
    internal static string CandidateSet(IngestionLayout layout, SourceSelectionKind kind, bool hasLower, KeyBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (kind == SourceSelectionKind.Keys)
        {
            return $"""
                SELECT DISTINCT {OrderList(layout, "r")}
                FROM {RecordRows(layout)} r
                INNER JOIN OPENJSON({KeysParameter}) WITH ({OpenJsonColumns(layout)}) j ON {JoinOn(layout, "r", "j")}
                {Where(layout, "r", null, bounds)}
                """;
        }

        var window = kind == SourceSelectionKind.Incremental;
        var records = $"""
            SELECT DISTINCT {OrderList(layout, "r")}
            FROM {RecordRows(layout)} r
            {Where(layout, "r", window ? WindowPredicate("r", layout.Updated, hasLower) : null, bounds)}
            """;
        if (!window)
        {
            return records;
        }

        // A record whose row was soft-deleted in the window is a candidate although its update column did not move: the
        // ingestion flow tags a row deleted without touching it, and a deleted row is one the plan must hold. Its own
        // part, rather than an OR beside the update window, so each column is sought through its own index.
        var parts = new List<string> { records };
        if (layout.Deleted is { } deletedRecord)
        {
            parts.Add($"""
                SELECT DISTINCT {OrderList(layout, "r")}
                FROM {RecordRows(layout)} r
                {Where(layout, "r", WindowPredicate("r", deletedRecord, hasLower), bounds)}
                """);
        }

        // A record whose own row did not change in the window is still a candidate when one of its child rows did, or when
        // a child row was soft-deleted in it: the record's document is built from those rows as well.
        foreach (var dataset in layout.Datasets.Where(d => d.Updated is not null || d.Deleted is not null))
        {
            var changed = new List<string>();
            if (dataset.Updated is { } updated)
            {
                changed.Add(WindowPredicate("c", updated, hasLower));
            }

            if (dataset.Deleted is { } deleted)
            {
                changed.Add(WindowPredicate("c", deleted, hasLower));
            }

            var on = string.Join(" AND ", dataset.Join.Select(j => $"c.{SourceObjectName.Quote(j.Key)} = r.{SourceObjectName.Quote(j.Value)}"));
            parts.Add($"""
                SELECT DISTINCT {OrderList(layout, "r")}
                FROM {RecordRows(layout)} r
                INNER JOIN {DatasetRows(dataset)} c ON {on}
                {Where(layout, "r", "(" + string.Join(" OR ", changed.Select(c => "(" + c + ")")) + ")", bounds)}
                """);
        }

        return string.Join(Environment.NewLine + "UNION" + Environment.NewLine, parts);
    }

    private static string Where(IngestionLayout layout, string alias, string? changed, KeyBounds bounds)
    {
        var predicates = new List<string>();
        if (changed is not null)
        {
            predicates.Add(changed);
        }

        var scope = ScopePredicate(layout, alias);
        if (scope.Length > 0)
        {
            predicates.Add(scope);
        }

        if (bounds.After)
        {
            predicates.Add(Above(layout, alias, AfterPrefix));
        }

        if (bounds.From)
        {
            predicates.Add(Above(layout, alias, FromPrefix));
        }

        if (bounds.To)
        {
            predicates.Add(AtMost(layout, alias, ToPrefix));
        }

        return predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates);
    }

    private static string WindowPredicate(string alias, string column, bool hasLower)
    {
        var quoted = $"{alias}.{SourceObjectName.Quote(column)}";
        return hasLower
            ? $"{quoted} > {LowerParameter} AND {quoted} <= {UpperParameter}"
            : $"{quoted} <= {UpperParameter}";
    }

    private static string ScopePredicate(IngestionLayout layout, string alias)
        => string.Join(" AND ", layout.Scope.Select((s, i) =>
        {
            var column = $"{alias}.{SourceObjectName.Quote(s.Column)}";
            return s.Values == 1
                ? $"{column} = {ScopeParameter(i)}"
                : $"{column} IN ({string.Join(", ", Enumerable.Range(0, s.Values).Select(j => ScopeParameter(i, j)))})";
        }));

    /// <summary>The parameter holding the one value of the scope's <paramref name="column"/>th column.</summary>
    public static string ScopeParameter(int column) => ScopePrefix + column.ToString(CultureInfo.InvariantCulture);

    /// <summary>The parameter holding the <paramref name="value"/>th of the values the run gives the scope's <paramref name="column"/>th column.</summary>
    public static string ScopeParameter(int column, int value)
        => ScopePrefix + column.ToString(CultureInfo.InvariantCulture) + "_" + value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A row is above the value the parameters hold, comparing part by part in the order the read pages in.</summary>
    private static string Above(IngestionLayout layout, string alias, string prefix) => Compare(layout.Order, alias, prefix, ">");

    /// <summary>A row is at most the value the parameters hold.</summary>
    private static string AtMost(IngestionLayout layout, string alias, string prefix) => Compare(layout.Order, alias, prefix, "<", orEqual: true);

    private static string Compare(IReadOnlyList<SourceKeyColumn> order, string alias, string prefix, string op, bool orEqual = false)
    {
        var text = new StringBuilder();
        for (var i = 0; i < order.Count; i++)
        {
            var column = $"{alias}.{SourceObjectName.Quote(order[i].Name)}";
            var parameter = prefix + i.ToString(CultureInfo.InvariantCulture);
            var last = i == order.Count - 1;
            text.Append('(').Append(column).Append(' ').Append(op).Append(last && orEqual ? "= " : " ").Append(parameter);
            if (!last)
            {
                text.Append(" OR (").Append(column).Append(" = ").Append(parameter).Append(" AND ");
            }
        }

        for (var i = 0; i < order.Count - 1; i++)
        {
            text.Append("))");
        }

        text.Append(')');
        return text.ToString();
    }

    /// <summary>
    /// The record table as a read's FROM names it: the table itself, or under <c>source.record.filter</c> the rows the filter
    /// keeps, as a derived table, so the filter's columns resolve against the record table alone whatever the read joins to
    /// it. The optimizer folds it into the read, which still seeks the table's indexes.
    /// </summary>
    private static string RecordRows(IngestionLayout layout) => Rows(layout.Record, layout.Filter);

    /// <summary>A child dataset as a read's FROM names it, under its own filter as <see cref="RecordRows"/> does.</summary>
    private static string DatasetRows(IngestionDataset dataset) => Rows(dataset.Object, dataset.Filter);

    private static string Rows(SourceObjectName table, string? filter)
        => filter is null ? table.Quoted : $"(SELECT * FROM {table.Quoted} WHERE 1 = 1 {filter})";

    private static string KeyList(IngestionLayout layout, string alias)
        => string.Join(", ", layout.Key.Select(k => $"{alias}.{SourceObjectName.Quote(k.Name)}"));

    private static string OrderList(IngestionLayout layout, string alias)
        => string.Join(", ", layout.Order.Select(k => $"{alias}.{SourceObjectName.Quote(k.Name)}"));

    private static string JoinOn(IngestionLayout layout, string left, string right)
        => string.Join(" AND ", layout.Key.Select(k => $"{left}.{SourceObjectName.Quote(k.Name)} = {right}.{SourceObjectName.Quote(k.Name)}"));

    private static string OpenJsonColumns(IngestionLayout layout) => OpenJsonColumns(layout.Key);

    private static string OpenJsonColumns(IReadOnlyList<SourceKeyColumn> columns)
        => string.Join(", ", columns.Select((k, i) => $"{SourceObjectName.Quote(k.Name)} {k.SqlType} '$[{i.ToString(CultureInfo.InvariantCulture)}]'"));
}
