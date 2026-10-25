using System.Globalization;
using System.Text;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;
using SqlFlow.SqlServer.Schema;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// An attribute column of a dimension's table: its name, whether it is collected, so a key holds several, and whether it is
/// a field of the dimension's elements, so a key holds a row per element.
/// </summary>
public sealed record DimensionTableColumn(string Name, bool Collected, bool Element = false);

/// <summary>
/// A dimension's table as a build keeps it: its name, the names of the columns that hold its key and its value, and its
/// attribute columns in the order the dimension declares them.
/// </summary>
/// <param name="Name">The table's name in the module's schema, without the schema.</param>
/// <param name="KeyColumn">The column that holds each key, named as the dimension names it (<see cref="DimensionColumnNames"/>).</param>
/// <param name="ValueColumn">The column that holds each key's value, named as the dimension names it.</param>
/// <param name="Columns">The attribute columns, in the order the dimension declares them, then the fields of its elements.</param>
public sealed record DimensionTableSpec(string Name, string KeyColumn, string ValueColumn, IReadOnlyList<DimensionTableColumn> Columns)
{
    /// <summary>Whether the dimension reads elements: a row per object of a nested array, numbered in the <c>element</c> column.</summary>
    public bool HasElements => Columns.Any(c => c.Element);
}

/// <summary>
/// A dimension's table as a reader names it: the table, and the two columns whose names are the dimension's own, as the
/// table has them now.
/// </summary>
/// <param name="Name">The table's name in the module's schema, without the schema.</param>
/// <param name="KeyColumn">The column that holds each key.</param>
/// <param name="ValueColumn">The column that holds each key's value.</param>
public sealed record DimensionTableRef(string Name, string KeyColumn, string ValueColumn);

/// <summary>
/// The table of a dimension (osdu/docs/reference/flow/dimension-table.md, The dimension's table): the dimension as one table
/// any SQL client reads and joins on, in the module's schema, named after the dimension. A row per key and value it
/// collects (one row for a key of a dimension collecting nothing, or collecting no value), with the row's number, the
/// partition, the key's number, the key exactly as the index holds it, its value, the records of the row, the search
/// filter finding the key's records, and a column per attribute named as the dimension declares it. The key's column
/// and the value's are named after what the dimension reads (<see cref="DimensionColumnNames"/>: <c>WellboreID</c> and
/// <c>FacilityName</c>), as the attributes' are. A key under no value, and a key no build finds any more, is no row.
/// </summary>
/// <remarks>
/// <para>
/// A build makes the table and keeps it: it writes the rows in the transaction that writes the rest of the dimension, so
/// the table is never behind it, and it writes only what differs from the rows the table held, so a row keeps its number
/// for as long as the dimension holds it.
/// </para>
/// <para>
/// The table's schema follows the declaration through SQLFlow's schema evolution, the same that widens an ingestion
/// table: a first build creates the table with its identity key, an attribute the flow starts to declare gets its
/// column on the next build, and nothing is ever dropped or narrowed. An attribute the flow stops declaring keeps its
/// column, emptied, so a query that names it still runs.
/// </para>
/// <para>
/// The key's column and the value's are renamed where they are when the dimension names them otherwise (its path or
/// its label changed, or the document gives another name), before the schema is brought to the declaration: the rows,
/// their numbers and the indexes stay, and only the name moves. A table made before dimensions named their columns
/// holds them as <c>key</c> and <c>value</c>, and its next build renames them the same way.
/// </para>
/// </remarks>
public static class DimensionTables
{
    /// <summary>What every dimension table's name begins with, so the dimensions of a schema are told from the ledger's tables at a glance.</summary>
    public const string Prefix = "dim_";

    /// <summary>The collation text is matched exactly by, whatever the database compares by.</summary>
    internal const string Exact = "Latin1_General_100_BIN2";

    /// <summary>The column every dimension table keeps its key's hash in: SHA-256 of the key's text as SQL Server's <c>HASHBYTES</c> gives it.</summary>
    public const string KeyHashColumn = "key_hash";

    /// <summary>The key SQLFlow's schema evolution holds a table to: a change to it is refused, never applied.</summary>
    internal static readonly IReadOnlySet<string> KeyColumns = new HashSet<string>(["id"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The table's name: <c>dim_&lt;dimension&gt;</c>, the dimension's name with whatever is not a letter, a digit or an
    /// underscore made an underscore, so it needs no quoting (<c>dim_Wellbore</c>). The flow's name is no part of it, so
    /// a dimension's name is unique among the flows of a database: two that declare one of the same name would write one
    /// table, and the second to build is refused.
    /// </summary>
    public static string NameOf(string dimension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        return Prefix + Plain(dimension);
    }

    /// <summary>
    /// The table of dimension <paramref name="dimension"/>, its key in <paramref name="keyColumn"/> and its value in
    /// <paramref name="valueColumn"/>, reading <paramref name="attributes"/> and the fields of <paramref name="elements"/>.
    /// </summary>
    /// <exception cref="DeliveryException">The dimension declares more columns than a build lays out.</exception>
    public static DimensionTableSpec Of(
        string dimension, string keyColumn, string valueColumn, IReadOnlyList<DimensionAttributeSpec> attributes, DimensionElementsSpec? elements = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueColumn);
        ArgumentNullException.ThrowIfNull(attributes);
        if (attributes.Count > DimensionSpec.MaxAttributes)
        {
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture, $"Dimension {dimension} declares {attributes.Count} attributes, and a dimension's table holds {DimensionSpec.MaxAttributes}."));
        }

        var fields = elements?.Fields ?? [];
        if (attributes.Count + fields.Count > DimensionSpec.MaxAttributes)
        {
            throw new DeliveryException(string.Create(CultureInfo.InvariantCulture,
                $"Dimension {dimension} declares {attributes.Count} attributes and {fields.Count} element fields, and a dimension's table holds {DimensionSpec.MaxAttributes} beside its key and value."));
        }

        return new DimensionTableSpec(
            NameOf(dimension), keyColumn, valueColumn,
            [.. attributes.Select(a => new DimensionTableColumn(a.Name, a.IsCollected)), .. fields.Select(f => new DimensionTableColumn(f.Name, false, Element: true))]);
    }

    /// <summary>A table's name with its schema, as a statement names it: <c>[osdu].[dim_...]</c>.</summary>
    public static string Qualified(string name) => $"[{DeliveryModel.SchemaName}].{Quoted(name)}";

    /// <summary>A table's name as a person types it: <c>osdu.dim_...</c>, which needs no quoting.</summary>
    public static string Shown(string name) => $"{DeliveryModel.SchemaName}.{name}";

    /// <summary>
    /// The columns <paramref name="table"/> is to have, which SQLFlow's schema evolution brings the table to: the fixed
    /// ones first, so an attribute the flow declares later takes its place after them as the others did.
    /// </summary>
    internal static IReadOnlyList<SqlColumn> Desired(DimensionTableSpec table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var columns = new List<SqlColumn>
        {
            new() { Name = "id", DataType = Type("bigint"), IsNullable = false, IsIdentity = true, IsPrimaryKey = true, Role = ColumnRole.Identity, Origin = ColumnOrigin.Computed },
            new() { Name = "partition", DataType = Text(256), IsNullable = false },
            new() { Name = "key_id", DataType = Type("bigint"), IsNullable = false },
            new() { Name = table.KeyColumn, DataType = Text(DeliveryDimensionValue.MaxOriginalLength), IsNullable = false },
            new() { Name = table.ValueColumn, DataType = Text(256), IsNullable = false },
            new() { Name = "records", DataType = Type("bigint"), IsNullable = false },
            new() { Name = "filter", DataType = Text(DeliveryDimensionValue.MaxFilterLength), IsNullable = true },

            // The key's hash, which a view's join finds a row by (osdu/docs/reference/flow/dimension-views.md): the
            // key itself is too long for an index key. A column the build writes, not a computed one, so the key's
            // column can still be renamed.
            new() { Name = KeyHashColumn, DataType = new SqlDataType { BaseType = "binary", Length = 32 }, IsNullable = true },
        };
        if (table.HasElements)
        {
            columns.Add(new SqlColumn { Name = DimensionElementsSpec.Column, DataType = Type("int"), IsNullable = true });
        }

        columns.AddRange(table.Columns.Select(c => new SqlColumn
        {
            Name = c.Name,
            DataType = Text(c.Element ? DeliveryDimensionElement.MaxValueLength : DeliveryDimensionAttributeValue.MaxValueLength),
            IsNullable = true,
        }));
        return columns;
    }

    /// <summary>
    /// The indexes a table is read through, made when it has none of the name: a key's rows, which a build matches the
    /// rows it writes by and a join on the key's number seeks; the rows of a partition in value order, which a page
    /// reads and a count counts; and a key's row by its hash, which a view's join seeks. An index follows a column that is
    /// renamed, so each is named after what its column is for, not after the column.
    /// </summary>
    internal static string IndexSql(DimensionTableSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var table = Qualified(spec.Name);
        var literal = table.Replace("'", "''", StringComparison.Ordinal);
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'{literal}', N'U') AND [name] = N'IX_key')
                CREATE INDEX [IX_key] ON {table} ([partition], [key_id]);
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'{literal}', N'U') AND [name] = N'IX_value')
                CREATE INDEX [IX_value] ON {table} ([partition], {Quoted(spec.ValueColumn)}, [id]);
            {KeyHashIndexSql(spec.Name)}
            """;
    }

    /// <summary>The index a view's join finds a key's row by, made when the table has none of the name.</summary>
    internal static string KeyHashIndexSql(string name)
    {
        var table = Qualified(name);
        var literal = table.Replace("'", "''", StringComparison.Ordinal);
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'{literal}', N'U') AND [name] = N'IX_key_hash')
                CREATE INDEX [IX_key_hash] ON {table} ([partition], [{KeyHashColumn}]);
            """;
    }

    /// <summary>
    /// The statement that gives every row of <paramref name="table"/> without a key hash its hash, in every partition or in
    /// <c>@partition</c>'s alone: rows a build wrote before tables kept one. <paramref name="keyColumn"/> is the key's column
    /// as the table names it now.
    /// </summary>
    internal static string FillKeyHashSql(string table, string keyColumn, bool onePartition)
        => $"UPDATE {Qualified(table)} SET [{KeyHashColumn}] = {KeyHash(Quoted(keyColumn))} WHERE [{KeyHashColumn}] IS NULL{(onePartition ? " AND [partition] = @partition" : string.Empty)};";

    /// <summary>The hash of a key's text, as a view's join computes the hash of the value it joins on.</summary>
    internal static string KeyHash(string keySql) => $"CAST(HASHBYTES('SHA2_256', {keySql}) AS binary(32))";

    /// <summary>
    /// The statements that bring the rows of one partition in <paramref name="table"/> to the rows a build laid out in
    /// <c>#DimRow</c> (a key, the value it collects, and each attribute in the column of its place): a row that left is
    /// deleted, one that changed rewritten, a new one added, so a build that found the same thing writes nothing and a row
    /// keeps its number. <paramref name="retired"/> are the columns of attributes the dimension no longer declares, which
    /// are emptied. The statement takes <c>@partition</c>, and <c>@rewrite</c>: 1 when the attribute that makes the rows is
    /// no longer the one the table's rows were written with, so no row of it can be matched and all are written again.
    /// </summary>
    internal static string ApplySql(DimensionTableSpec table, IReadOnlyCollection<string> retired)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(retired);
        var target = Qualified(table.Name);
        var key = Quoted(table.KeyColumn);
        var value = Quoted(table.ValueColumn);
        var attributes = table.Columns.Select((column, index) => (Column: Quoted(column.Name), Slot: $"[{Slot(index + 1)}]", column.Collected)).ToList();
        var collected = attributes.FirstOrDefault(a => a.Collected).Column;
        var element = Quoted(DimensionElementsSpec.Column);

        // A row is a key and the value it collects, or the element it holds, each compared exactly, whatever the database compares text by.
        var same = table.HasElements
            ? $"s.[ValueId] = t.[key_id] AND ISNULL(s.[Element], -1) = ISNULL(t.{element}, -1)"
            : collected is null
                ? "s.[ValueId] = t.[key_id]"
                : $"s.[ValueId] = t.[key_id] AND s.[Part] = ISNULL(t.{collected}, N'') COLLATE {Exact}";
        var sql = new StringBuilder();
        sql.Append(CultureInfo.InvariantCulture, $"IF @rewrite = 1\n    DELETE FROM {target} WHERE [partition] = @partition;\n\n");
        sql.Append(CultureInfo.InvariantCulture, $"DELETE t\nFROM {target} AS t\nWHERE t.[partition] = @partition\n  AND NOT EXISTS (SELECT 1 FROM #DimRow AS s WHERE {same});\n\n");

        sql.Append(CultureInfo.InvariantCulture,
            $"UPDATE t SET t.{key} = s.[Key], t.[{KeyHashColumn}] = {KeyHash("s.[Key]")}, t.{value} = s.[Value], t.[records] = s.[Records], t.[filter] = s.[Filter]");
        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", t.{attribute.Column} = s.{attribute.Slot}");
        }

        foreach (var column in retired)
        {
            sql.Append(CultureInfo.InvariantCulture, $", t.{Quoted(column)} = NULL");
        }

        sql.Append(CultureInfo.InvariantCulture, $"\nFROM {target} AS t\nINNER JOIN #DimRow AS s ON {same}\nWHERE t.[partition] = @partition\n  AND (EXISTS (\n");
        sql.Append("        SELECT s.[Key], s.[Value], s.[Records], s.[Filter]");
        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", s.{attribute.Slot}");
        }

        sql.Append(CultureInfo.InvariantCulture, $"\n        EXCEPT\n        SELECT t.{key} COLLATE {Exact}, t.{value} COLLATE {Exact}, t.[records], t.[filter] COLLATE {Exact}");
        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", t.{attribute.Column} COLLATE {Exact}");
        }

        sql.Append(')');
        foreach (var column in retired)
        {
            sql.Append(CultureInfo.InvariantCulture, $"\n       OR t.{Quoted(column)} IS NOT NULL");
        }

        sql.Append(");\n\n");

        // New rows take their numbers in the table's own order: by value, then key, then the value collected or the element.
        sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO {target} ([partition], [key_id], {key}, [{KeyHashColumn}], {value}, [records], [filter]");
        if (table.HasElements)
        {
            sql.Append(CultureInfo.InvariantCulture, $", {element}");
        }

        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", {attribute.Column}");
        }

        sql.Append(CultureInfo.InvariantCulture, $")\nSELECT @partition, s.[ValueId], s.[Key], {KeyHash("s.[Key]")}, s.[Value], s.[Records], s.[Filter]");
        if (table.HasElements)
        {
            sql.Append(", s.[Element]");
        }

        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", s.{attribute.Slot}");
        }

        sql.Append(CultureInfo.InvariantCulture,
            $"\nFROM #DimRow AS s\nWHERE NOT EXISTS (SELECT 1 FROM {target} AS t WHERE t.[partition] = @partition AND {same})\nORDER BY s.[Value], s.[Key], {(table.HasElements ? "s.[Element]" : "s.[Part]")};");
        sql.Append("\n\n").Append(FillKeyHashSql(table.Name, table.KeyColumn, onePartition: true));
        return sql.ToString();
    }

    /// <summary>The most attributes a build lays out beside a key, which is the most a dimension declares.</summary>
    internal const int Slots = DimensionSpec.MaxAttributes;

    /// <summary>The column of <c>#DimRow</c> the attribute of place <paramref name="ordinal"/> is laid out in, from 1: <c>A01</c> onwards.</summary>
    internal static string Slot(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ordinal, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ordinal, Slots);
        return string.Create(CultureInfo.InvariantCulture, $"A{ordinal:D2}");
    }

    /// <summary>An identifier in brackets, a closing bracket inside it doubled.</summary>
    internal static string Quoted(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static SqlDataType Type(string name) => RunTimeTables.Type(name);

    private static SqlDataType Text(int length) => RunTimeTables.Text(length);

    private static string Plain(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            plain.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        return plain.ToString();
    }
}
