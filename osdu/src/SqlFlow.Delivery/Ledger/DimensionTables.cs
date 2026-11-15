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
/// any SQL client reads and joins on, in the module's schema unless its flow's target names another database or schema
/// (<see cref="DimensionTableTarget"/>), named after the dimension. A row per key and value it
/// collects (one row for a key of a dimension collecting nothing, or collecting no value), with the row's number, the
/// partition, the key's number, the key exactly as the index holds it, its value, the records of the row, the search
/// filter finding the key's records, and a column per attribute named as the dimension declares it. The key's column
/// and the value's are named after what the dimension reads (<see cref="DimensionColumnNames"/>: <c>WellboreID</c> and
/// <c>FacilityName</c>), as the attributes' are. A key under no value, and a key no build finds any more, is no row.
/// </summary>
/// <remarks>
/// <para>
/// A build makes the table and keeps it: once the rest of the dimension is written, it brings the table to the rows that
/// write laid out, in a transaction of the database the table is kept in, and writes only what differs from the rows the
/// table held, so a row keeps its number for as long as the dimension holds it. The ledger records the build whose rows
/// the table holds, so a build after one whose table write did not complete lays out every key and the table catches up.
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

    /// <summary>
    /// The column tables made before keys were found by their text kept the key's hash in, which a build drops with its
    /// index (<see cref="KeyIndexSql"/>).
    /// </summary>
    internal const string FormerKeyHashColumn = "key_hash";

    /// <summary>The index a view's join finds a key's rows by: the partition, then the key's text.</summary>
    internal const string KeyIndex = "IX_key_text";

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

    /// <summary>A table's name in the module's schema, as a statement names it: <c>[osdu].[dim_...]</c>.</summary>
    public static string Qualified(string name) => DimensionTableTarget.Catalog.Qualified(name);

    /// <summary>A table's name in the module's schema as a person types it: <c>osdu.dim_...</c>, which needs no quoting.</summary>
    public static string Shown(string name) => DimensionTableTarget.Catalog.Shown(name);

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
    /// The order a table keeps its rows in and the indexes it is read through, each made when the table has none of the
    /// name: a partition's rows together and a key's rows together within it (<c>IX_key</c>, the clustered index), which a
    /// build matches the rows it writes by and seeks by its partition, so builds of the dimension in two partitions at
    /// once read and lock their own partition's rows alone, and a join on the key's number seeks; the rows of a partition
    /// in value order, which a page reads and a count counts; and a key's row by its hash, which a view's join seeks. A
    /// table made before its rows were kept by partition (its key on <c>id</c> clustered, <c>IX_key</c> a secondary index)
    /// is laid out once: its secondary indexes go first and come back last, so each is built once, and its key on
    /// <c>id</c> stays, nonclustered. An index follows a column that is renamed, so each is named after what its column is
    /// for, not after the column. The statement runs under the table's lock, so two builds never lay a table out at once.
    /// </summary>
    internal static string IndexSql(DimensionTableSpec spec, DimensionTableTarget target)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(target);
        var table = target.Qualified(spec.Name);
        var literal = table.Replace("'", "''", StringComparison.Ordinal);
        var key = ("PK_" + spec.Name).Replace("'", "''", StringComparison.Ordinal);
        return $"""
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'{literal}', N'U') AND [name] = N'IX_key' AND [type] = 1)
            BEGIN
                DROP INDEX IF EXISTS [IX_value] ON {table};
                DROP INDEX IF EXISTS [{KeyIndex}] ON {table};
                DROP INDEX IF EXISTS [IX_key] ON {table};
                DECLARE @key sysname = (SELECT [name] FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'{literal}', N'U') AND [type] = 'PK');
                IF @key IS NOT NULL
                BEGIN
                    DECLARE @dropKey nvarchar(1000) = N'ALTER TABLE {literal} DROP CONSTRAINT ' + QUOTENAME(@key) + N';';
                    EXEC sys.sp_executesql @dropKey;
                END;
                CREATE UNIQUE CLUSTERED INDEX [IX_key] ON {table} ([partition], [key_id], [id]);
                DECLARE @addKey nvarchar(1000) = N'ALTER TABLE {literal} ADD CONSTRAINT ' + QUOTENAME(ISNULL(@key, N'{key}')) + N' PRIMARY KEY NONCLUSTERED ([id]);';
                EXEC sys.sp_executesql @addKey;
            END;
            IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'{literal}', N'U') AND [name] = N'IX_value')
                CREATE INDEX [IX_value] ON {table} ([partition], {Quoted(spec.ValueColumn)}, [id]);
            {KeyIndexSql(spec.Name, spec.KeyColumn, target)}
            """;
    }

    /// <summary>
    /// The index a view's join finds a key's rows by, the partition then the key's text (<see cref="KeyIndex"/>), made when
    /// the table has none of the name and holds the key's column <paramref name="keyColumn"/>; and, in a table made before
    /// keys were found by their text, the key's hash and its index dropped. A key is at most
    /// <see cref="DeliveryDimensionValue.MaxOriginalLength"/> characters, so the index holds every key a build writes.
    /// </summary>
    internal static string KeyIndexSql(string name, string keyColumn, DimensionTableTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var table = target.Qualified(name);
        var literal = table.Replace("'", "''", StringComparison.Ordinal);
        var column = keyColumn.Replace("'", "''", StringComparison.Ordinal);
        return $"""
            DROP INDEX IF EXISTS [IX_key_hash] ON {table};
            IF COL_LENGTH(N'{literal}', N'{FormerKeyHashColumn}') IS NOT NULL
                ALTER TABLE {table} DROP COLUMN [{FormerKeyHashColumn}];
            IF COL_LENGTH(N'{literal}', N'{column}') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'{literal}', N'U') AND [name] = N'{KeyIndex}')
                CREATE INDEX [{KeyIndex}] ON {table} ([partition], {Quoted(keyColumn)});
            """;
    }

    /// <summary>
    /// The statement that lays out the rows a dimension's table holds into <c>#DimRow</c>, made for a table of
    /// <paramref name="slots"/> attribute columns: a row per key the dimension holds now and value it collects, or element it
    /// holds, each attribute in the column of its place (<c>A01</c> on), read from what the write has just kept and joined by
    /// the attributes' numbers. An attribute read from the record a key names is one value of the key, so every such attribute
    /// of a key is read in one pass and laid out by its place; the collected attribute is the one that makes rows, so it is
    /// joined. Only the table's own columns are laid out, so a dimension of a few attributes computes those alone of every
    /// attribute it keeps. It takes <c>@p</c>, <c>@d</c>, <c>@elements</c>, and the collected attribute's number and place
    /// (<c>@collectedId</c>, <c>@collectedOrdinal</c>), null when it collects none; and <c>@scoped</c>: 1 to lay out the rows
    /// of the keys <c>#DimScope</c> holds alone (an incremental load's: the keys it read again and those it removed), 0 to lay
    /// out every key's.
    /// </summary>
    internal static string RowsSql(int slots)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slots);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slots, Slots);
        var places = Enumerable.Range(1, slots).Select(i => (Place: i, Slot: Slot(i))).ToList();
        string Each(Func<(int Place, string Slot), string> each, string between) => string.Concat(places.Select(p => between + each(p)));

        // Scoped, every read of the dimension's rows starts at the keys in scope, so it seeks their rows alone.
        string Insert(bool scoped)
        {
            var keys = scoped ? "\n    INNER JOIN #DimScope AS z ON z.[ValueId] = k.[ValueId]" : string.Empty;
            var attributes = scoped ? "\n        INNER JOIN #DimScope AS za ON za.[ValueId] = a.[ValueId]" : string.Empty;
            var elements = scoped ? "\n        INNER JOIN #DimScope AS ze ON ze.[ValueId] = e.[ValueId]" : string.Empty;
            return $"""
                INSERT INTO #DimRow ([ValueId], [Part], [Element], [Key], [Value]{Each(p => $"[{p.Slot}]", ", ")}, [Records], [Filter])
                SELECT k.[ValueId], COALESCE(c.[Value], CONVERT(nvarchar(256), el.[Seq]), N''), el.[Seq], k.[Original], m.[Value]{Each(p => $"CASE WHEN @collectedOrdinal = {p.Place} THEN c.[Value] ELSE COALESCE(x.[{p.Slot}], el.[{p.Slot}]) END", ",\n    ")},
                    COALESCE(c.[Records], CASE WHEN el.[Seq] IS NULL THEN k.[Count] ELSE 1 END), k.[Filter]
                FROM [osdu].[DimensionValue] AS k WITH (FORCESEEK){keys}
                INNER JOIN [osdu].[DimensionMember] AS m WITH (FORCESEEK) ON m.[PartitionId] = @p AND m.[DimensionId] = @d AND m.[MemberId] = k.[MemberId]
                LEFT JOIN (
                    SELECT a.[ValueId]{Each(p => $"MAX(CASE WHEN n.[Ordinal] = {p.Place} THEN a.[Value] END) AS [{p.Slot}]", ",\n        ")}
                    FROM [osdu].[DimensionAttribute] AS a WITH (FORCESEEK){attributes}
                    INNER JOIN [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK) ON n.[PartitionId] = @p AND n.[DimensionId] = @d AND n.[AttributeId] = a.[AttributeId]
                    WHERE a.[PartitionId] = @p AND a.[DimensionId] = @d AND n.[Ordinal] IS NOT NULL AND n.[Collected] = 0
                    GROUP BY a.[ValueId]) AS x ON x.[ValueId] = k.[ValueId]
                LEFT JOIN (
                    SELECT e.[ValueId], e.[Seq]{Each(p => $"MAX(CASE WHEN n.[Ordinal] = {p.Place} THEN e.[Value] END) AS [{p.Slot}]", ",\n        ")}
                    FROM [osdu].[DimensionElement] AS e WITH (FORCESEEK){elements}
                    INNER JOIN [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK) ON n.[PartitionId] = @p AND n.[DimensionId] = @d AND n.[AttributeId] = e.[AttributeId]
                    WHERE e.[PartitionId] = @p AND e.[DimensionId] = @d AND n.[Ordinal] IS NOT NULL AND @elements = 1
                    GROUP BY e.[ValueId], e.[Seq]) AS el ON el.[ValueId] = k.[ValueId]
                LEFT JOIN [osdu].[DimensionAttribute] AS c WITH (FORCESEEK)
                    ON c.[PartitionId] = @p AND c.[DimensionId] = @d AND c.[AttributeId] = @collectedId AND c.[ValueId] = k.[ValueId]
                WHERE k.[PartitionId] = @p AND k.[DimensionId] = @d AND k.[RemovedRunId] IS NULL;
                """;
        }

        return $"""
            -- An element is a row of its own, the key's other attributes beside each; a key holding none is one row.
            IF @scoped = 1
            BEGIN
            {Insert(scoped: true)}
            END
            ELSE
            BEGIN
            {Insert(scoped: false)}
            END;
            """;
    }

    /// <summary>
    /// The statements that bring the rows of one partition in <paramref name="table"/> to the rows a build laid out in
    /// <c>#DimRow</c> (a key, the value it collects, and each attribute in the column of its place): a row that left is
    /// deleted, one that changed rewritten, a new one added, so a build that found the same thing writes nothing and a row
    /// keeps its number. <paramref name="retired"/> are the columns of attributes the dimension no longer declares, which
    /// are emptied. The statement takes <c>@partition</c>, and <c>@rewrite</c>: 1 when the attribute that makes the rows is
    /// no longer the one the table's rows were written with, so no row of it can be matched and all are written again; and
    /// <c>@scoped</c>: 1 when <c>#DimRow</c> holds the rows of the keys in <c>#DimScope</c> alone (an incremental load's
    /// write), so only those keys' rows are compared and every other row is left as it is. It adds the rows it deleted,
    /// rewrote and added to <c>@changed</c> (an output), which the write raises the table's version by
    /// (<c>DeliveryDimension.TableVersion</c>).
    /// </summary>
    internal static string ApplySql(DimensionTableSpec table, IReadOnlyCollection<string> retired, DimensionTableTarget kept)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(retired);
        ArgumentNullException.ThrowIfNull(kept);
        var target = kept.Qualified(table.Name);
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
        sql.Append(CultureInfo.InvariantCulture, $"IF @rewrite = 1\nBEGIN\n    DELETE t FROM {target} AS t WITH (FORCESEEK) WHERE t.[partition] = @partition;\n    SET @changed += @@ROWCOUNT;\nEND;\n\n");

        // Scoped (an incremental load's write), only the rows of the keys in scope are laid out, so only theirs can have left:
        // every other row stays as it is.
        sql.Append(CultureInfo.InvariantCulture,
            $"IF @scoped = 1\nBEGIN\n    DELETE t\n    FROM #DimScope AS z\n    INNER JOIN {target} AS t WITH (FORCESEEK) ON t.[partition] = @partition AND t.[key_id] = z.[ValueId]\n    WHERE NOT EXISTS (SELECT 1 FROM #DimRow AS s WHERE {same});\n    SET @changed += @@ROWCOUNT;\nEND\n");
        sql.Append(CultureInfo.InvariantCulture,
            $"ELSE\nBEGIN\n    DELETE t\n    FROM {target} AS t WITH (FORCESEEK)\n    WHERE t.[partition] = @partition\n      AND NOT EXISTS (SELECT 1 FROM #DimRow AS s WHERE {same});\n    SET @changed += @@ROWCOUNT;\nEND;\n\n");

        sql.Append(CultureInfo.InvariantCulture,
            $"UPDATE t SET t.{key} = s.[Key], t.{value} = s.[Value], t.[records] = s.[Records], t.[filter] = s.[Filter]");
        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", t.{attribute.Column} = s.{attribute.Slot}");
        }

        foreach (var column in retired)
        {
            sql.Append(CultureInfo.InvariantCulture, $", t.{Quoted(column)} = NULL");
        }

        sql.Append(CultureInfo.InvariantCulture, $"\nFROM {target} AS t WITH (FORCESEEK)\nINNER JOIN #DimRow AS s ON {same}\nWHERE t.[partition] = @partition\n  AND (EXISTS (\n");
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

        sql.Append(");\nSET @changed += @@ROWCOUNT;\n\n");

        // New rows take their numbers in the table's own order: by value, then key, then the value collected or the element.
        sql.Append(CultureInfo.InvariantCulture, $"INSERT INTO {target} ([partition], [key_id], {key}, {value}, [records], [filter]");
        if (table.HasElements)
        {
            sql.Append(CultureInfo.InvariantCulture, $", {element}");
        }

        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", {attribute.Column}");
        }

        sql.Append(CultureInfo.InvariantCulture, $")\nSELECT @partition, s.[ValueId], s.[Key], s.[Value], s.[Records], s.[Filter]");
        if (table.HasElements)
        {
            sql.Append(", s.[Element]");
        }

        foreach (var attribute in attributes)
        {
            sql.Append(CultureInfo.InvariantCulture, $", s.{attribute.Slot}");
        }

        sql.Append(CultureInfo.InvariantCulture,
            $"\nFROM #DimRow AS s\nWHERE NOT EXISTS (SELECT 1 FROM {target} AS t WITH (FORCESEEK) WHERE t.[partition] = @partition AND {same})\nORDER BY s.[Value], s.[Key], {(table.HasElements ? "s.[Element]" : "s.[Part]")};\nSET @changed += @@ROWCOUNT;");
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
