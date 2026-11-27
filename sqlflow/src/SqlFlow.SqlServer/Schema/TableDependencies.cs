using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer.Schema;

/// <summary>What references a column and so decides whether, and how, its type can change.</summary>
public enum ColumnReferenceKind
{
    ComputedColumn,
    CheckConstraint,
    DefaultConstraint,
    ForeignKey,
    SchemaBoundModule,
    FullTextIndex,
    PartitionColumn,
}

/// <summary>An object that references one column of a table.</summary>
public sealed record ColumnReference
{
    public required string Column { get; init; }
    public required ColumnReferenceKind Kind { get; init; }
    public required string Name { get; init; }

    public string Describe() => Kind switch
    {
        ColumnReferenceKind.ComputedColumn => $"computed column [{Name}]",
        ColumnReferenceKind.CheckConstraint => $"check constraint [{Name}]",
        ColumnReferenceKind.DefaultConstraint => $"default constraint [{Name}]",
        ColumnReferenceKind.ForeignKey => $"foreign key [{Name}]",
        ColumnReferenceKind.SchemaBoundModule => $"schema-bound {Name}",
        ColumnReferenceKind.FullTextIndex => "the full-text index",
        _ => $"partition scheme [{Name}]",
    };
}

/// <summary>One key or included column of an index.</summary>
public sealed record IndexColumn
{
    public required string Name { get; init; }
    public bool Descending { get; init; }
    public bool Included { get; init; }
}

/// <summary>A rowstore or columnstore index on a table, with what is needed to build it again on another column.</summary>
public sealed record IndexDefinition
{
    public required string Name { get; init; }
    public bool IsClustered { get; init; }
    public bool IsUnique { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsUniqueConstraint { get; init; }
    public bool IsColumnStore { get; init; }
    public bool IgnoreDupKey { get; init; }
    public int FillFactor { get; init; }
    public bool IsPadded { get; init; }
    public bool AllowRowLocks { get; init; } = true;
    public bool AllowPageLocks { get; init; } = true;

    /// <summary>NONE, ROW or PAGE (of the first partition; a partitioned index is refused before this matters).</summary>
    public string Compression { get; init; } = "NONE";

    public string? Filter { get; init; }
    public required IReadOnlyList<IndexColumn> Columns { get; init; }

    public bool Covers(string column) => Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the filter predicate names the column (compared as a bracketed or bare identifier).</summary>
    public bool FilterNames(string column)
        => Filter is { } filter
           && (filter.Contains($"[{column}]", StringComparison.OrdinalIgnoreCase)
               || filter.Contains(column, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A user-created statistics object (not one an index backs, not an auto-created one).</summary>
public sealed record StatisticsDefinition
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> Columns { get; init; }
    public string? Filter { get; init; }
}

/// <summary>
/// The live facts about a target table that decide what a type change of one of its columns may do: its indexes and
/// statistics (rebuilt by the safe rewrite), the objects that reference each column (each stops the engine), the
/// table-level states that rule the safe rewrite out (system versioning, a columnstore index, change data capture,
/// partitioning), the columns that identify a row (the rewrite's batches and its trigger join on them), the plain views
/// to refresh afterwards, and the row count. Read by <see cref="TableDependencyReader"/>.
/// </summary>
public sealed record TableDependencies
{
    /// <summary>The facts of a table nobody read: a classification on the types alone (a pure planner call).</summary>
    public static readonly TableDependencies None = new();

    /// <summary>True when the facts were read from the live table; false for <see cref="None"/>.</summary>
    public bool Known { get; init; }

    public long RowCount { get; init; }
    public bool IsTemporal { get; init; }
    public bool HasColumnStore { get; init; }
    public bool IsCdcTracked { get; init; }
    public bool IsPartitioned { get; init; }

    /// <summary>The columns that identify a row: the identity column, else the primary key's columns, else the columns of
    /// the narrowest unique, unfiltered index whose columns are all NOT NULL (an ingestion target's key index); empty when
    /// the table has none of them.</summary>
    public IReadOnlyList<string> RowIdentity { get; init; } = [];

    /// <summary>The SQL type of the single row-identity column, when there is exactly one.</summary>
    public SqlDataType? RowIdentityType { get; init; }

    public IReadOnlyList<IndexDefinition> Indexes { get; init; } = [];
    public IReadOnlyList<StatisticsDefinition> Statistics { get; init; } = [];
    public IReadOnlyList<ColumnReference> References { get; init; } = [];

    /// <summary>Plain (not schema-bound) views over the table, refreshed with <c>sp_refreshview</c> after a change.</summary>
    public IReadOnlyList<string> DependentViews { get; init; } = [];

    public IReadOnlyList<IndexDefinition> IndexesOn(string column) => Indexes.Where(i => i.Covers(column)).ToList();

    public IReadOnlyList<ColumnReference> ReferencesTo(string column)
        => References.Where(r => string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase)).ToList();

    public IReadOnlyList<StatisticsDefinition> StatisticsOn(string column)
        => Statistics.Where(s => s.Columns.Any(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase))).ToList();
}

/// <summary>Reads <see cref="TableDependencies"/> for one table from the SQL Server catalog views.</summary>
public static class TableDependencyReader
{
    public static async Task<TableDependencies> ReadAsync(SqlConnection connection, RelationalObject table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(table);

        var objectName = $"[{Escape(table.Schema)}].[{Escape(table.Name)}]";
        await using var command = new SqlCommand(Sql, connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 600) { Value = objectName });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        // 1. table facts
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return TableDependencies.None;
        }

        var rowCount = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
        var temporal = reader.GetBoolean(1);
        var cdc = reader.GetBoolean(2);

        // 2. row identity: identity column first, else primary key columns in key order
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var identity = new List<(string Name, string Type)>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            identity.Add((reader.GetString(0), reader.GetString(1)));
        }

        // 3. index columns, one row per (index, column), ordered
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var indexes = new List<IndexDefinition>();
        var partitioned = false;
        var hasColumnStore = false;
        IndexDefinition? current = null;
        var columns = new List<IndexColumn>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (current is null || !string.Equals(current.Name, name, StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    indexes.Add(current with { Columns = columns });
                }

                columns = [];
                var type = reader.GetByte(1);
                hasColumnStore |= type is 5 or 6;
                partitioned |= reader.GetBoolean(12);
                current = new IndexDefinition
                {
                    Name = name,
                    IsClustered = type is 1 or 5,
                    IsColumnStore = type is 5 or 6,
                    IsUnique = reader.GetBoolean(2),
                    IsPrimaryKey = reader.GetBoolean(3),
                    IsUniqueConstraint = reader.GetBoolean(4),
                    IgnoreDupKey = reader.GetBoolean(5),
                    FillFactor = reader.GetByte(6),
                    IsPadded = reader.GetBoolean(7),
                    AllowRowLocks = reader.GetBoolean(8),
                    AllowPageLocks = reader.GetBoolean(9),
                    Filter = reader.IsDBNull(10) ? null : reader.GetString(10),
                    Compression = reader.IsDBNull(11) ? "NONE" : reader.GetString(11),
                    Columns = [],
                };
            }

            if (!reader.IsDBNull(13))
            {
                columns.Add(new IndexColumn { Name = reader.GetString(13), Descending = reader.GetBoolean(14), Included = reader.GetBoolean(15) });
            }
        }

        if (current is not null)
        {
            indexes.Add(current with { Columns = columns });
        }

        // 4. user statistics
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var statistics = new List<StatisticsDefinition>();
        StatisticsDefinition? stat = null;
        var statColumns = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (stat is null || !string.Equals(stat.Name, name, StringComparison.Ordinal))
            {
                if (stat is not null)
                {
                    statistics.Add(stat with { Columns = statColumns });
                }

                statColumns = [];
                stat = new StatisticsDefinition { Name = name, Filter = reader.IsDBNull(1) ? null : reader.GetString(1), Columns = [] };
            }

            statColumns.Add(reader.GetString(2));
        }

        if (stat is not null)
        {
            statistics.Add(stat with { Columns = statColumns });
        }

        // 5. column references
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var references = new List<ColumnReference>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            references.Add(new ColumnReference
            {
                Column = reader.GetString(0),
                Kind = (ColumnReferenceKind)reader.GetInt32(1),
                Name = reader.GetString(2),
            });
        }

        // 6. plain views to refresh
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var views = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            views.Add(reader.GetString(0));
        }

        return new TableDependencies
        {
            Known = true,
            RowCount = rowCount,
            IsTemporal = temporal,
            IsCdcTracked = cdc,
            HasColumnStore = hasColumnStore,
            IsPartitioned = partitioned,
            RowIdentity = identity.Select(i => i.Name).ToList(),
            RowIdentityType = identity.Count == 1 ? SqlDataType.Parse(identity[0].Type) : null,
            Indexes = indexes,
            Statistics = statistics,
            References = references.Distinct().ToList(),
            DependentViews = views,
        };
    }

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    // The ColumnReferenceKind ordinals are written into the SQL below; keep them in step.
    private static readonly string Sql = string.Create(CultureInfo.InvariantCulture, $"""
        SET NOCOUNT ON;
        DECLARE @id int = OBJECT_ID(@name, N'U');

        SELECT (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = @id AND p.index_id IN (0, 1)) AS row_count,
               CONVERT(bit, CASE WHEN t.temporal_type = 2 THEN 1 ELSE 0 END) AS is_temporal,
               t.is_tracked_by_cdc
        FROM sys.tables t WHERE t.object_id = @id;

        DECLARE @key int = (SELECT index_id FROM sys.indexes WHERE object_id = @id AND is_primary_key = 1);
        IF @key IS NULL
            SET @key = (SELECT TOP (1) i.index_id FROM sys.indexes i
                        WHERE i.object_id = @id AND i.is_unique = 1 AND i.has_filter = 0 AND i.type IN (1, 2) AND i.is_disabled = 0
                          AND NOT EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0 AND c.is_nullable = 1)
                        ORDER BY (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0),
                                 i.index_id);

        IF EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = @id)
            SELECT c.name, TYPE_NAME(c.user_type_id) FROM sys.identity_columns c WHERE c.object_id = @id;
        ELSE
            SELECT c.name, TYPE_NAME(c.user_type_id) COLLATE DATABASE_DEFAULT
              + CASE WHEN TYPE_NAME(c.user_type_id) IN (N'varchar', N'char', N'varbinary', N'binary')
                       THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CONVERT(nvarchar(10), c.max_length) END + N')'
                     WHEN TYPE_NAME(c.user_type_id) IN (N'nvarchar', N'nchar')
                       THEN N'(' + CASE WHEN c.max_length = -1 THEN N'max' ELSE CONVERT(nvarchar(10), c.max_length / 2) END + N')'
                     WHEN TYPE_NAME(c.user_type_id) IN (N'decimal', N'numeric')
                       THEN N'(' + CONVERT(nvarchar(10), c.precision) + N', ' + CONVERT(nvarchar(10), c.scale) + N')'
                     ELSE N'' END
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = @id AND i.index_id = @key
            ORDER BY ic.key_ordinal;

        SELECT i.name, i.type, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.ignore_dup_key, i.fill_factor,
               i.is_padded, i.allow_row_locks, i.allow_page_locks, i.filter_definition,
               (SELECT TOP (1) p.data_compression_desc FROM sys.partitions p
                 WHERE p.object_id = i.object_id AND p.index_id = i.index_id ORDER BY p.partition_number) AS compression,
               CONVERT(bit, CASE WHEN EXISTS (SELECT 1 FROM sys.partition_schemes ps WHERE ps.data_space_id = i.data_space_id)
                                 THEN 1 ELSE 0 END) AS is_partitioned,
               c.name AS column_name, ic.is_descending_key, ic.is_included_column
        FROM sys.indexes i
        LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                       AND (ic.key_ordinal > 0 OR ic.is_included_column = 1)
        LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = @id AND i.type > 0 AND i.is_hypothetical = 0
        ORDER BY i.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id;

        SELECT s.name, s.filter_definition, c.name
        FROM sys.stats s
        JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id
        JOIN sys.columns c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
        WHERE s.object_id = @id AND s.user_created = 1
          AND NOT EXISTS (SELECT 1 FROM sys.indexes i WHERE i.object_id = s.object_id AND i.index_id = s.stats_id)
        ORDER BY s.name, sc.stats_column_id;

        SELECT COL_NAME(@id, d.referenced_minor_id) COLLATE DATABASE_DEFAULT AS column_name,
               CASE WHEN d.referencing_id = @id THEN {(int)ColumnReferenceKind.ComputedColumn}
                    WHEN o.type = 'C' THEN {(int)ColumnReferenceKind.CheckConstraint}
                    ELSE {(int)ColumnReferenceKind.SchemaBoundModule} END AS kind,
               CASE WHEN d.referencing_id = @id THEN COL_NAME(@id, d.referencing_minor_id) COLLATE DATABASE_DEFAULT
                    WHEN o.type = 'C' THEN o.name COLLATE DATABASE_DEFAULT
                    ELSE LOWER(REPLACE(o.type_desc COLLATE DATABASE_DEFAULT, N'SQL_', N'')) + N' ['
                         + SCHEMA_NAME(o.schema_id) COLLATE DATABASE_DEFAULT + N'].[' + o.name COLLATE DATABASE_DEFAULT + N']' END
        FROM sys.sql_expression_dependencies d
        JOIN sys.objects o ON o.object_id = d.referencing_id
        WHERE d.referenced_id = @id AND d.referenced_minor_id > 0
          AND (d.referencing_id = @id OR o.type = 'C' OR d.is_schema_bound_reference = 1)
        UNION ALL
        SELECT COL_NAME(@id, dc.parent_column_id) COLLATE DATABASE_DEFAULT, {(int)ColumnReferenceKind.DefaultConstraint}, dc.name COLLATE DATABASE_DEFAULT
        FROM sys.default_constraints dc WHERE dc.parent_object_id = @id
        UNION ALL
        SELECT COL_NAME(@id, cc.parent_column_id) COLLATE DATABASE_DEFAULT, {(int)ColumnReferenceKind.CheckConstraint}, cc.name COLLATE DATABASE_DEFAULT
        FROM sys.check_constraints cc WHERE cc.parent_object_id = @id AND cc.parent_column_id > 0
        UNION ALL
        SELECT COL_NAME(fkc.parent_object_id, fkc.parent_column_id) COLLATE DATABASE_DEFAULT, {(int)ColumnReferenceKind.ForeignKey}, fk.name COLLATE DATABASE_DEFAULT
        FROM sys.foreign_key_columns fkc JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
        WHERE fkc.parent_object_id = @id
        UNION ALL
        SELECT COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) COLLATE DATABASE_DEFAULT, {(int)ColumnReferenceKind.ForeignKey}, fk.name COLLATE DATABASE_DEFAULT
        FROM sys.foreign_key_columns fkc JOIN sys.foreign_keys fk ON fk.object_id = fkc.constraint_object_id
        WHERE fkc.referenced_object_id = @id
        UNION ALL
        SELECT COL_NAME(@id, fic.column_id) COLLATE DATABASE_DEFAULT, {(int)ColumnReferenceKind.FullTextIndex}, N'full-text'
        FROM sys.fulltext_index_columns fic WHERE fic.object_id = @id
        UNION ALL
        SELECT COL_NAME(@id, ic.column_id) COLLATE DATABASE_DEFAULT, {(int)ColumnReferenceKind.PartitionColumn}, ps.name COLLATE DATABASE_DEFAULT
        FROM sys.indexes i
        JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.partition_ordinal > 0
        WHERE i.object_id = @id;

        SELECT DISTINCT QUOTENAME(SCHEMA_NAME(o.schema_id)) + N'.' + QUOTENAME(o.name)
        FROM sys.sql_expression_dependencies d
        JOIN sys.objects o ON o.object_id = d.referencing_id
        WHERE d.referenced_id = @id AND o.type = 'V' AND d.is_schema_bound_reference = 0;
        """);
}
