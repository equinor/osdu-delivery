using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// Reading a dimension's table where it is kept (osdu/docs/reference/flow/dimension-table.md): its attribute columns, a
/// page of its rows narrowed and ordered as a query asks, and every row as a stream.
/// </summary>
internal static partial class SqlServerDimensionStore
{
    /// <summary>
    /// The attribute columns of table <paramref name="table"/> as dimension <paramref name="dimensionId"/> reads it, where
    /// <paramref name="home"/> keeps it: the attributes the dimension declares now (read from the ledger, on
    /// <paramref name="catalog"/>), in their order, each with a column there.
    /// </summary>
    /// <exception cref="DimensionTableMissingException">The database holds no such table.</exception>
    internal static async Task<IReadOnlyList<string>> TableAttributesAsync(
        SqlConnection catalog, DimensionTableDatabase home, short partitionId, int dimensionId, string table, CancellationToken ct)
        => (await TableLayoutAsync(catalog, home, partitionId, dimensionId, table, ct).ConfigureAwait(false)).Attributes;

    /// <summary>
    /// The columns a reader of a table names beside the ones every table has from its first build: its attribute columns,
    /// and whether it holds its rows' times yet (<see cref="DimensionColumnNames.InsertedColumn"/>,
    /// <see cref="DimensionColumnNames.UpdatedColumn"/>), which the next build gives a table made before tables kept them.
    /// </summary>
    private sealed record TableLayout(IReadOnlyList<string> Attributes, bool Stamped);

    /// <summary>
    /// The attribute columns of table <paramref name="table"/> as <see cref="TableAttributesAsync"/> finds them, and whether
    /// the table holds its rows' times.
    /// </summary>
    /// <exception cref="DimensionTableMissingException">The database holds no such table.</exception>
    private static async Task<TableLayout> TableLayoutAsync(
        SqlConnection catalog, DimensionTableDatabase home, short partitionId, int dimensionId, string table, CancellationToken ct)
    {
        var declared = new List<string>();
        await using (var names = new SqlCommand(
            "SELECT n.[Name] FROM [osdu].[DimensionAttributeName] AS n WITH (FORCESEEK) WHERE n.[PartitionId] = @p AND n.[DimensionId] = @d AND n.[Ordinal] IS NOT NULL ORDER BY n.[Ordinal];",
            catalog) { CommandTimeout = CommandTimeoutSeconds })
        {
            names.Parameters.Add(new SqlParameter("@p", SqlDbType.SmallInt) { Value = partitionId });
            names.Parameters.Add(new SqlParameter("@d", SqlDbType.Int) { Value = dimensionId });
            await using var reader = await names.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                declared.Add(reader.GetString(0));
            }
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var read = new SqlCommand(
            """
            SELECT CASE WHEN OBJECT_ID(@table, N'U') IS NULL THEN 0 ELSE 1 END;
            SELECT c.[name] FROM sys.columns AS c WHERE c.[object_id] = OBJECT_ID(@table, N'U');
            """,
            home.Connection) { CommandTimeout = CommandTimeoutSeconds })
        {
            read.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 300) { Value = home.Target.Qualified(table) });
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt32(0) == 0)
            {
                throw new DimensionTableMissingException(
                    $"The table {home.Target.Shown(table)} is not in the database: it was dropped, or never made. Run the flow's pipeline to make it again.");
            }

            await reader.NextResultAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(0));
            }
        }

        return new TableLayout(
            declared.Where(columns.Contains).ToList(),
            columns.Contains(DimensionColumnNames.InsertedColumn) && columns.Contains(DimensionColumnNames.UpdatedColumn));
    }

    /// <summary>The columns of every table a page can be ordered by, beside the key's, the value's and the attributes'.</summary>
    private const string RecordsColumn = "records";

    private const string IdColumn = "id";

    /// <summary>
    /// The columns a page of the table can be ordered by: the value's, the key's, the records, the row's number, the rows'
    /// times where the table holds them, and the attributes'.
    /// </summary>
    private static List<string> OrderColumns(DimensionTableRef table, TableLayout layout)
        => [table.ValueColumn, table.KeyColumn, RecordsColumn, IdColumn,
            .. layout.Stamped ? new[] { DimensionColumnNames.InsertedColumn, DimensionColumnNames.UpdatedColumn } : [], .. layout.Attributes];

    /// <summary>
    /// A column of the table by the name a caller gave it, ignoring case: a column named so in the table first, then the
    /// two words the value's and the key's columns are asked for by whatever the dimension names them; null when the
    /// table has no such column.
    /// </summary>
    private static string? TableColumn(DimensionTableRef table, TableLayout layout, string name)
    {
        var wanted = name.Trim();
        return OrderColumns(table, layout).FirstOrDefault(c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase))
            ?? (string.Equals(wanted, DimensionColumnNames.ValueRole, StringComparison.OrdinalIgnoreCase) ? table.ValueColumn
                : string.Equals(wanted, DimensionColumnNames.KeyRole, StringComparison.OrdinalIgnoreCase) ? table.KeyColumn
                : null);
    }

    /// <summary>The columns a row ends with.</summary>
    private static readonly string[] EndColumns = ["[records]", "[filter]"];

    /// <summary>
    /// The rows a statement over a table reads: the row's number, the key's, the key, the value, each attribute, the
    /// records, the filter, and the row's two times, read as none from a table its next build has yet to give them.
    /// </summary>
    private static string TableColumns(DimensionTableRef table, TableLayout layout, string alias = "")
    {
        IEnumerable<string> stamps = layout.Stamped
            ? new[] { DimensionColumnNames.InsertedColumn, DimensionColumnNames.UpdatedColumn }.Select(c => alias + Bracketed(c))
            : ["NULL", "NULL"];
        return string.Join(", ", new[] { "[id]", "[key_id]", Bracketed(table.KeyColumn), Bracketed(table.ValueColumn) }
            .Concat(layout.Attributes.Select(Bracketed)).Concat(EndColumns).Select(c => alias + c).Concat(stamps));
    }

    /// <summary>What SQL Server answers a statement naming a column the table does not have.</summary>
    private const int InvalidColumnName = 207;

    /// <summary>A read that named a column the table no longer has: a build renamed it between the read's two steps.</summary>
    private static DimensionTableRenamedException Renamed(DimensionTableRef table, DimensionTableTarget target, SqlException ex)
        => new(
            $"The table {target.Shown(table.Name)} was changed while it was read (a build renamed a column of it, or one was dropped by hand): {ex.Message.Trim()} Read it again.",
            ex);

    /// <summary>A row of a table, its columns read in the order the statement names them, as a reader streaming them needs.</summary>
    private static DimensionTableRow TableRow(SqlDataReader reader, int attributes)
    {
        var id = reader.GetInt64(0);
        var keyId = reader.GetInt64(1);
        var key = reader.GetString(2);
        var value = reader.GetString(3);
        var values = new string?[attributes];
        for (var i = 0; i < attributes; i++)
        {
            values[i] = reader.IsDBNull(4 + i) ? null : reader.GetString(4 + i);
        }

        var records = reader.GetInt64(4 + attributes);
        var filter = reader.IsDBNull(5 + attributes) ? null : reader.GetString(5 + attributes);
        return new DimensionTableRow(id, keyId, key, value, values, records, filter, Stamp(reader, 6 + attributes), Stamp(reader, 7 + attributes));
    }

    /// <summary>A row's time as the table holds it, in UTC; null where the table holds none yet.</summary>
    private static DateTime? Stamp(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);

    /// <summary>
    /// A page of the rows of the table <paramref name="named"/> in partition <paramref name="partition"/>, narrowed and ordered as
    /// <paramref name="query"/> asks, with how many rows it matches in all on a first page.
    /// </summary>
    /// <remarks>
    /// A page is found before it is read: the first statement orders the numbers of the rows the query matches and keeps
    /// the page's, which is all it sorts (a number and the column ordered by, never a key or a filter), and the second
    /// reads those rows by their numbers. Rows in value order come from the index that keeps them so. Text is searched
    /// case-folded and compared exactly, which is several times quicker than a comparison by a language's rules, and an
    /// attribute's value is matched exactly, as the lists that offer it hold it; either reads the partition's rows once,
    /// for the count and the page together.
    /// </remarks>
    /// <exception cref="DimensionTableMissingException">The database holds no such table.</exception>
    /// <exception cref="DimensionTableRenamedException">A build renamed a column of the table while it was read.</exception>
    /// <exception cref="DeliveryException">The query orders or narrows by a column the table does not have.</exception>
    internal static async Task<DimensionTablePage> ReadTableAsync(
        SqlConnection catalog, DimensionTableDatabase home, short partitionId, int dimensionId, DimensionTableRef named, string partition,
        DimensionTableQuery query, int maxPage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(named);
        ArgumentNullException.ThrowIfNull(query);
        var table = named.Name;
        var target = home.Target;
        var connection = home.Connection;
        try
        {
            var layout = await TableLayoutAsync(catalog, home, partitionId, dimensionId, table, ct).ConfigureAwait(false);
            var attributes = layout.Attributes;
            var where = new List<string> { "[partition] = @partition" };
            var parameters = new List<SqlParameter> { new("@partition", SqlDbType.NVarChar, 256) { Value = partition } };
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                // What is typed is found anywhere in the key, the value or an attribute, ignoring case; its own wildcards are text.
                var like = "%" + query.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
                    .Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "%";
                parameters.Add(new SqlParameter("@like", SqlDbType.NVarChar, 600) { Value = like });
                where.Add("(" + string.Join(" OR ", new[] { named.ValueColumn, named.KeyColumn }.Concat(attributes).Select(Bracketed)
                    .Select(c => $"UPPER({c}) COLLATE {DimensionTables.Exact} LIKE UPPER(@like) ESCAPE N'\\'")) + ")");
            }

            var matched = 0;
            foreach (var match in query.Attributes ?? [])
            {
                var column = attributes.FirstOrDefault(a => string.Equals(a, match.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new DeliveryException(
                        $"The table {target.Shown(table)} has no attribute column '{match.Name}'{(attributes.Count == 0 ? "; it has none" : $"; it has {string.Join(", ", attributes)}")}.");
                var names = new List<string>();
                foreach (var value in match.Values.Distinct(StringComparer.Ordinal))
                {
                    var name = string.Create(CultureInfo.InvariantCulture, $"@m{matched++}");
                    parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, DeliveryDimensionElement.MaxValueLength) { Value = value });
                    names.Add(name);
                }

                if (names.Count > 0)
                {
                    where.Add($"{Bracketed(column)} COLLATE {DimensionTables.Exact} IN ({string.Join(", ", names)})");
                }
            }

            var ordered = string.IsNullOrWhiteSpace(query.OrderBy) ? named.ValueColumn : TableColumn(named, layout, query.OrderBy)
                ?? throw new DeliveryException(
                    $"The table {target.Shown(table)} has no column '{query.OrderBy}' to order by; it has {string.Join(", ", OrderColumns(named, layout))}.");

            // A row's number tells it from every other, so the order ends with it: every page then holds its own rows.
            var direction = query.Descending ? "DESC" : "ASC";
            var byId = string.Equals(ordered, IdColumn, StringComparison.Ordinal);
            var order = byId ? $"[id] {direction}" : $"{Bracketed(ordered)} {direction}, [id]";
            var take = Math.Clamp(query.Limit, 1, maxPage);
            var offset = Math.Max(0, query.Offset);
            var filter = string.Join(" AND ", where);
            var from = target.Qualified(table);

            long? total = null;
            var rows = new List<DimensionTableRow>(take);
            if (where.Count > 1)
            {
                // A search or an attribute's value is found by reading the partition's rows, so they are read once: the
                // numbers of the rows that match are kept with the column they are ordered by, counted, and the page
                // taken from them.
                var by = byId ? string.Empty : $", {Bracketed(ordered)} AS [o]";
                var matchOrder = byId ? $"[id] {direction}" : $"[o] {direction}, [id]";
                await using var command = new SqlCommand(
                    $"""
                    SET NOCOUNT ON;
                    DECLARE @page TABLE ([n] int IDENTITY(1, 1) NOT NULL PRIMARY KEY, [id] bigint NOT NULL);
                    SELECT [id]{by} INTO #match FROM {from} WHERE {filter};
                    DECLARE @total bigint = ROWCOUNT_BIG();
                    INSERT INTO @page ([id])
                    SELECT [id] FROM #match ORDER BY {matchOrder} OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
                    DROP TABLE #match;
                    SELECT @total;
                    SELECT {TableColumns(named, layout, "t.")}
                    FROM @page AS g INNER JOIN {from} AS t ON t.[id] = g.[id]
                    ORDER BY g.[n];
                    """,
                    connection)
                {
                    CommandTimeout = CommandTimeoutSeconds,
                };
                command.Parameters.AddRange(parameters.ToArray());
                command.Parameters.Add(new SqlParameter("@offset", SqlDbType.Int) { Value = offset });
                command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = take });
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    total = reader.GetInt64(0);
                }

                await reader.NextResultAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    rows.Add(TableRow(reader, attributes.Count));
                }
            }
            else
            {
                // Every row of the partition: counted from the narrow index that keeps them in value order, on a first
                // page, and the page's numbers taken in order, from that index when the order is the value's.
                if (offset == 0)
                {
                    await using var count = new SqlCommand($"SELECT COUNT_BIG(*) FROM {from} WHERE {filter};", connection) { CommandTimeout = CommandTimeoutSeconds };
                    count.Parameters.AddRange(parameters.Select(p => (SqlParameter)((ICloneable)p).Clone()).ToArray());
                    total = Convert.ToInt64(await count.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }

                if (total != 0)
                {
                    await using var command = new SqlCommand(
                        $"""
                        SET NOCOUNT ON;
                        DECLARE @page TABLE ([n] int IDENTITY(1, 1) NOT NULL PRIMARY KEY, [id] bigint NOT NULL);
                        INSERT INTO @page ([id])
                        SELECT [id] FROM {from} WHERE {filter} ORDER BY {order} OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;
                        SELECT {TableColumns(named, layout, "t.")}
                        FROM @page AS g INNER JOIN {from} AS t ON t.[id] = g.[id]
                        ORDER BY g.[n];
                        """,
                        connection)
                    {
                        CommandTimeout = CommandTimeoutSeconds,
                    };
                    command.Parameters.AddRange(parameters.ToArray());
                    command.Parameters.Add(new SqlParameter("@offset", SqlDbType.Int) { Value = offset });
                    command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = take });
                    await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        rows.Add(TableRow(reader, attributes.Count));
                    }
                }
            }

            // A first page knows how many rows there are; a later one says more may follow while it comes back full.
            var more = total is { } all ? offset + rows.Count < all : rows.Count == take;
            return new DimensionTablePage(target.Shown(table), named.KeyColumn, named.ValueColumn, attributes, rows, more, total);
        }
        catch (SqlException ex) when (ex.Number == InvalidColumnName)
        {
            throw Renamed(named, target, ex);
        }
    }

    /// <summary>Every row of the table <paramref name="named"/> in partition <paramref name="partition"/>, by value then row number, one at a time.</summary>
    /// <exception cref="DimensionTableMissingException">The database holds no such table.</exception>
    /// <exception cref="DimensionTableRenamedException">A build renamed a column of the table as the read began.</exception>
    internal static async IAsyncEnumerable<DimensionTableRow> StreamTableAsync(
        SqlConnection catalog, DimensionTableDatabase home, short partitionId, int dimensionId, DimensionTableRef named, string partition,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(named);
        var table = named.Name;
        var connection = home.Connection;
        {
            var layout = await TableLayoutAsync(catalog, home, partitionId, dimensionId, table, ct).ConfigureAwait(false);

            // The whole table in order is a sort of every row, and a row is declared wide (a key, a filter), so the server
            // would reserve memory for the widest table it can imagine: a tenth of what it may give one query sorts any
            // dimension, past it the sort uses tempdb, and other queries keep their memory.
            await using var command = new SqlCommand(
                $"SELECT {TableColumns(named, layout)} FROM {home.Target.Qualified(table)} WHERE [partition] = @partition ORDER BY {Bracketed(named.ValueColumn)}, [id] OPTION (MAX_GRANT_PERCENT = 10);",
                connection)
            {
                CommandTimeout = CommandTimeoutSeconds,
            };
            command.Parameters.Add(new SqlParameter("@partition", SqlDbType.NVarChar, 256) { Value = partition });
            SqlDataReader opened;
            try
            {
                opened = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (ex.Number == InvalidColumnName)
            {
                throw Renamed(named, home.Target, ex);
            }

            await using var reader = opened;
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                yield return TableRow(reader, layout.Attributes.Count);
            }
        }
    }
}
