using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer.Schema;

/// <summary>Tuning for the safe rewrite.</summary>
public sealed record RetypeOptions
{
    /// <summary>Rows converted per committed batch of the backfill.</summary>
    public int BatchSize { get; init; } = 20_000;

    /// <summary>Distinct failing values a refusal reports.</summary>
    public int SampleSize { get; init; } = 5;

    /// <summary>The lock waits: the table's schema app lock and the DDL's own schema-lock timeout.</summary>
    public SchemaApplyOptions Apply { get; init; } = new();
}

/// <summary>What a conversion check found: the rows, the values that do not convert without loss, and a sample.</summary>
public sealed record StoredValueCheck(long Rows, long Failing, IReadOnlyList<TypeChangeValueSample> Sample);

/// <summary>Key values that would become one under a key column's new type.</summary>
public sealed record KeyMergeResult(long MergedKeys, IReadOnlyList<TypeChangeValueSample> Sample);

/// <summary>A safe rewrite an earlier run started and did not finish: its shadow column, the column it replaces, the
/// type it was converting to, and whether the trigger that keeps it in step is still there and enabled.</summary>
public sealed record PendingRetype(string Shadow, string Column, string ToType, bool TriggerIntact);

/// <summary>The connection, table and tracing a rewrite works with. The connection stays open for the whole rewrite:
/// the table's schema app lock is owned by its session.</summary>
public sealed record RetypeContext(SqlConnection Connection, RelationalObject Table, RetypeOptions Options, Action<string, string> Trace);

/// <summary>
/// Changes the type of one column of a populated table without a long lock and without losing a row or a value: the
/// safe rewrite behind <see cref="TypeChangeMechanism.ShadowRewrite"/>.
/// <list type="number">
/// <item><b>Prepare</b> (one short transaction): add a NULL shadow column of the new type, mark it with extended
/// properties naming the column it replaces and the type, and create a trigger that keeps it in step with every
/// INSERT and UPDATE of the column from then on (and refuses a value that does not convert without loss).</item>
/// <item><b>Backfill</b>: convert the column into the shadow in committed batches of <see cref="RetypeOptions.BatchSize"/>
/// rows, keyed by the table's integer identity (or primary key) when it has one. Readers and writers carry on.</item>
/// <item><b>Verify</b>: no row holds a value whose shadow is empty.</item>
/// <item><b>Rebuild</b>: make the shadow NOT NULL if the column is, and build a copy of every index and statistics
/// object on the column over the shadow (online where the edition allows). A unique index whose values collide under
/// the new type fails here, before anything is swapped.</item>
/// <item><b>Swap</b> (one transaction, the only moment readers wait): drop the trigger and the old indexes, rename the
/// column away and the shadow into its place, drop the old column, give the copies the original names, drop the
/// markers.</item>
/// </list>
/// The state of a rewrite lives in the table itself (the marked shadow column and its trigger), so a run that dies part
/// way leaves a consistent table: the column is untouched until the swap, which is atomic. <see cref="FindPendingAsync"/>
/// finds what an earlier run left, and the caller finishes it (<see cref="ApplyAsync"/> with <c>resume</c>) or removes it
/// (<see cref="RollBackAsync"/>). Every step is idempotent.
/// <para>
/// The column keeps its name, nullability, indexes and statistics, and every row keeps its identity, its system dates
/// and its other columns; the column moves to the end of the table's column order, which only <c>SELECT *</c> by
/// position would notice. The schema app lock taken for the whole rewrite keeps every other schema change of the table
/// out, and the trigger keeps a writer that is not the flow correct.
/// </para>
/// </summary>
public static class ColumnRetyper
{
    /// <summary>The suffix that marks the engine's working columns, index copies and statistics copies.</summary>
    public const string Marker = "__sqlflow_retype";

    private const string OldSuffix = "__sqlflow_old";
    private const string PropertyOf = "SqlFlow.RetypeOf";
    private const string PropertyTo = "SqlFlow.RetypeTo";

    /// <summary>True for a column name the safe rewrite uses for its work.</summary>
    public static bool IsShadowColumnName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.EndsWith(Marker, StringComparison.OrdinalIgnoreCase) || name.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public static string ShadowName(string column) => Bounded(column, Marker);

    private static string OldName(string column) => Bounded(column, OldSuffix);

    private static string CopyName(string name) => Bounded(name, Marker);

    private static string TriggerName(RelationalObject table, string column) => Bounded($"SqlFlowRetype_{table.Name}_{column}", string.Empty);

    /// <summary>Counts the stored values of the column that do not convert to <see cref="ColumnTypeChange.ToType"/>
    /// without loss, and samples them. Read only.</summary>
    public static async Task<StoredValueCheck> CheckAsync(RetypeContext context, ColumnTypeChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(change);

        var table = Qualify(context.Table);
        var column = Bracket(change.Column);
        var flag = LosslessConversion.LosslessFlag(change.FromType, change.ToType, column);
        var countSql = $"SELECT COUNT_BIG(*), COUNT_BIG(CASE WHEN {flag} = 0 THEN 1 END) FROM {table};";
        context.Trace("retype.check", countSql);
        long rows;
        long failing;
        await using (var command = new SqlCommand(countSql, context.Connection) { CommandTimeout = 0 })
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            await reader.ReadAsync(ct).ConfigureAwait(false);
            rows = reader.GetInt64(0);
            failing = reader.GetInt64(1);
        }

        if (failing == 0)
        {
            return new StoredValueCheck(rows, 0, []);
        }

        var sampleSql = SampleSql(table, column, $"{flag} = 0", context.Options.SampleSize);
        context.Trace("retype.check.sample", sampleSql);
        return new StoredValueCheck(rows, failing, await ReadSampleAsync(context.Connection, sampleSql, ct).ConfigureAwait(false));
    }

    /// <summary>For a key column, counts the converted values that more than one stored value maps to (keys that would
    /// merge under the new type), and samples them. Read only.</summary>
    public static async Task<KeyMergeResult> KeyMergesAsync(RetypeContext context, ColumnTypeChange change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(change);

        var table = Qualify(context.Table);
        var column = Bracket(change.Column);
        var converted = $"TRY_CONVERT({change.SourceType.Render()}, {column})";
        var groups = $"SELECT {converted} AS k, COUNT_BIG(*) AS n FROM {table} WHERE {converted} IS NOT NULL " +
                     $"GROUP BY {converted} HAVING COUNT(DISTINCT {column}) > 1";
        var sql = $"SELECT COUNT_BIG(*) FROM ({groups}) m; " +
                  $"SELECT TOP ({context.Options.SampleSize.ToString(CultureInfo.InvariantCulture)}) LEFT(CONVERT(nvarchar(max), m.k), {TypeChangeValueSample.MaxLength.ToString(CultureInfo.InvariantCulture)}), m.n " +
                  $"FROM ({groups}) m ORDER BY m.n DESC;";
        context.Trace("retype.key-merge", sql);
        await using var command = new SqlCommand(sql, context.Connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        var merged = reader.GetInt64(0);
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var sample = new List<TypeChangeValueSample>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            sample.Add(new TypeChangeValueSample { Value = reader.IsDBNull(0) ? "NULL" : reader.GetString(0), Rows = reader.GetInt64(1) });
        }

        return new KeyMergeResult(merged, sample);
    }

    /// <summary>The rewrites earlier runs left unfinished on the table.</summary>
    public static async Task<IReadOnlyList<PendingRetype>> FindPendingAsync(SqlConnection connection, RelationalObject table, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(table);

        const string sql = """
            SELECT c.name, CONVERT(nvarchar(256), po.value), CONVERT(nvarchar(256), pt.value)
            FROM sys.columns c
            JOIN sys.extended_properties po ON po.class = 1 AND po.major_id = c.object_id AND po.minor_id = c.column_id AND po.name = N'SqlFlow.RetypeOf'
            LEFT JOIN sys.extended_properties pt ON pt.class = 1 AND pt.major_id = c.object_id AND pt.minor_id = c.column_id AND pt.name = N'SqlFlow.RetypeTo'
            WHERE c.object_id = OBJECT_ID(@table, N'U');
            """;
        var found = new List<(string Shadow, string Column, string To)>();
        await using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
        {
            command.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 600) { Value = Qualify(table) });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                found.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
            }
        }

        var pending = new List<PendingRetype>();
        foreach (var (shadow, column, to) in found)
        {
            pending.Add(new PendingRetype(shadow, column, to, await TriggerIntactAsync(connection, table, column, ct).ConfigureAwait(false)));
        }

        return pending;
    }

    /// <summary>
    /// Carries out the change: prepare (unless <paramref name="resume"/> and the earlier run's shadow and trigger are
    /// intact), backfill, verify, rebuild, swap, then refresh the plain views over the table. Holds the table's schema app
    /// lock throughout. A failure before the swap leaves the column untouched (the shadow and trigger stay for the next
    /// run to finish or remove), and the caller decides whether to roll back.
    /// </summary>
    public static async Task ApplyAsync(
        RetypeContext context, ColumnTypeChange change, TableDependencies dependencies, bool resume, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (change.Mechanism != TypeChangeMechanism.ShadowRewrite)
        {
            throw new ArgumentException($"[{change.Column}] is not a shadow rewrite ({change.Mechanism}).", nameof(change));
        }

        if (dependencies.RowIdentity.Count == 0)
        {
            throw new SqlFlowException($"{Qualify(context.Table)} has no identity column or primary key, so [{change.Column}] cannot be rewritten safely.");
        }

        var resource = SqlServerSchemaProvider.SchemaLockResource(context.Table.Schema, context.Table.Name);
        await SetLockTimeoutAsync(context.Connection, context.Options.Apply.DdlLockTimeoutMs, ct).ConfigureAwait(false);
        var code = await SqlAppLock.TryAcquireAsync(context.Connection, resource, context.Options.Apply.AppLockTimeoutMs, ct).ConfigureAwait(false);
        if (code < 0)
        {
            throw new SchemaLockTimeoutException(resource, code);
        }

        try
        {
            if (!resume)
            {
                await PrepareAsync(context, change, dependencies, ct).ConfigureAwait(false);
            }

            await BackfillAsync(context, change, dependencies, ct).ConfigureAwait(false);
            await VerifyAsync(context, change, dependencies, ct).ConfigureAwait(false);
            var copies = await RebuildAsync(context, change, dependencies, ct).ConfigureAwait(false);
            await SwapAsync(context, change, copies, ct).ConfigureAwait(false);
        }
        finally
        {
            await SqlAppLock.ReleaseAsync(context.Connection, resource).ConfigureAwait(false);
            await SetLockTimeoutAsync(context.Connection, -1, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The plain views over the table whose metadata a type change leaves stale, refreshed one by one. Returns
    /// the ones SQL Server could not refresh with its reason, so the caller reports them.</summary>
    public static async Task<IReadOnlyList<string>> RefreshViewsAsync(RetypeContext context, TableDependencies dependencies, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(dependencies);

        var failed = new List<string>();
        foreach (var view in dependencies.DependentViews)
        {
            var sql = $"EXEC sys.sp_refreshview N'{Literal(view)}';";
            context.Trace("retype.refresh-view", sql);
            try
            {
                await ExecuteAsync(context.Connection, null, sql, ct).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                failed.Add($"{view}: {ex.Message}");
            }
        }

        return failed;
    }

    /// <summary>Removes what an unfinished rewrite left (its trigger, index and statistics copies, and shadow column),
    /// leaving the column as it was. Idempotent; holds the table's schema app lock.</summary>
    public static async Task RollBackAsync(RetypeContext context, PendingRetype pending, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pending);

        var resource = SqlServerSchemaProvider.SchemaLockResource(context.Table.Schema, context.Table.Name);
        await SetLockTimeoutAsync(context.Connection, context.Options.Apply.DdlLockTimeoutMs, ct).ConfigureAwait(false);
        var code = await SqlAppLock.TryAcquireAsync(context.Connection, resource, context.Options.Apply.AppLockTimeoutMs, ct).ConfigureAwait(false);
        if (code < 0)
        {
            throw new SchemaLockTimeoutException(resource, code);
        }

        try
        {
            var table = Qualify(context.Table);
            var literal = Literal(table);
            var trigger = $"[{Escape(context.Table.Schema)}].{Bracket(TriggerName(context.Table, pending.Column))}";
            var sql = new StringBuilder();
            sql.Append("SET XACT_ABORT ON;\nBEGIN TRANSACTION;\n");
            sql.Append(CultureInfo.InvariantCulture, $"DROP TRIGGER IF EXISTS {trigger};\n");
            // Index and statistics copies over the shadow are found by what they cover, not by name.
            sql.Append(CultureInfo.InvariantCulture, $"""
                DECLARE @drop nvarchar(max) = N'';
                SELECT @drop += N'DROP INDEX ' + QUOTENAME(i.name) + N' ON {Literal(table)};'
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID(N'{literal}', N'U')
                  AND EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND c.name = N'{Literal(pending.Shadow)}');
                SELECT @drop += N'DROP STATISTICS {Literal(table)}.' + QUOTENAME(s.name) + N';'
                FROM sys.stats s
                WHERE s.object_id = OBJECT_ID(N'{literal}', N'U') AND s.user_created = 1
                  AND EXISTS (SELECT 1 FROM sys.stats_columns sc JOIN sys.columns c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
                              WHERE sc.object_id = s.object_id AND sc.stats_id = s.stats_id AND c.name = N'{Literal(pending.Shadow)}');
                EXEC sys.sp_executesql @drop;
                IF COL_LENGTH(N'{literal}', N'{Literal(pending.Shadow)}') IS NOT NULL
                    ALTER TABLE {table} DROP COLUMN {Bracket(pending.Shadow)};
                COMMIT TRANSACTION;
                """);
            context.Trace("retype.rollback", sql.ToString());
            await ExecuteAsync(context.Connection, null, sql.ToString(), ct).ConfigureAwait(false);
        }
        finally
        {
            await SqlAppLock.ReleaseAsync(context.Connection, resource).ConfigureAwait(false);
            await SetLockTimeoutAsync(context.Connection, -1, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task PrepareAsync(RetypeContext context, ColumnTypeChange change, TableDependencies dependencies, CancellationToken ct)
    {
        var table = Qualify(context.Table);
        var literal = Literal(table);
        var shadow = ShadowName(change.Column);
        var schema = Literal(context.Table.Schema);
        var name = Literal(context.Table.Name);
        var sql = new StringBuilder();
        sql.Append("SET XACT_ABORT ON;\nBEGIN TRANSACTION;\n");
        sql.Append(CultureInfo.InvariantCulture,
            $"IF COL_LENGTH(N'{literal}', N'{Literal(shadow)}') IS NULL ALTER TABLE {table} ADD {Bracket(shadow)} {change.ToType.Render()} NULL;\n");
        foreach (var (property, value) in new[] { (PropertyOf, change.Column), (PropertyTo, change.ToType.Render()) })
        {
            sql.Append(CultureInfo.InvariantCulture, $"""
                IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND major_id = OBJECT_ID(N'{literal}', N'U')
                               AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'{literal}', N'U'), N'{Literal(shadow)}', 'ColumnId') AND name = N'{property}')
                    EXEC sys.sp_addextendedproperty @name = N'{property}', @value = N'{Literal(value)}',
                         @level0type = N'SCHEMA', @level0name = N'{schema}', @level1type = N'TABLE', @level1name = N'{name}',
                         @level2type = N'COLUMN', @level2name = N'{Literal(shadow)}';

                """);
        }

        // CREATE TRIGGER must start its own batch, so it runs through sp_executesql inside the same transaction.
        sql.Append(CultureInfo.InvariantCulture, $"EXEC sys.sp_executesql N'{Literal(TriggerSql(context.Table, change, dependencies))}';\n");
        sql.Append("COMMIT TRANSACTION;");
        context.Trace("retype.prepare", sql.ToString());
        await ExecuteAsync(context.Connection, null, sql.ToString(), ct).ConfigureAwait(false);
    }

    private static string TriggerSql(RelationalObject target, ColumnTypeChange change, TableDependencies dependencies)
    {
        var table = Qualify(target);
        var column = Bracket(change.Column);
        var shadow = Bracket(ShadowName(change.Column));
        var join = string.Join(" AND ", dependencies.RowIdentity.Select(k => $"t.{Bracket(k)} = i.{Bracket(k)}"));
        var flag = LosslessConversion.LosslessFlag(change.FromType, change.ToType, $"i.{column}");
        var refusal = $"{table}.{column} is being converted to {change.ToType.Render()} by SQLFlow, and the value written does not " +
                      "convert to it without loss; the write is refused until the conversion finishes or is rolled back.";
        return $"""
            CREATE OR ALTER TRIGGER [{Escape(target.Schema)}].{Bracket(TriggerName(target, change.Column))} ON {table}
            AFTER INSERT, UPDATE
            AS
            BEGIN
                SET NOCOUNT ON;
                IF NOT UPDATE({column}) RETURN;
                IF EXISTS (SELECT 1 FROM inserted AS i WHERE {flag} = 0)
                    THROW 51000, N'{Literal(refusal)}', 1;
                UPDATE t SET t.{shadow} = {LosslessConversion.Convert(change.FromType, change.ToType, $"i.{column}")}
                FROM {table} AS t INNER JOIN inserted AS i ON {join};
            END
            """;
    }

    private static async Task BackfillAsync(RetypeContext context, ColumnTypeChange change, TableDependencies dependencies, CancellationToken ct)
    {
        var table = Qualify(context.Table);
        var column = Bracket(change.Column);
        var shadow = Bracket(ShadowName(change.Column));
        var set = $"{shadow} = {LosslessConversion.Convert(change.FromType, change.ToType, column)}";
        var pending = $"{shadow} IS NULL AND {column} IS NOT NULL";
        var batch = context.Options.BatchSize.ToString(CultureInfo.InvariantCulture);

        var identity = dependencies.RowIdentity.Count == 1 && dependencies.RowIdentityType is { Family: SqlTypeFamily.Integer } t ? t : null;
        if (identity is null)
        {
            // No single integer row identity to range over: convert the next rows still pending until none are left.
            // The trigger keeps every row written since the prepare in step, so "pending" is exactly what is left.
            var sql = $"UPDATE TOP ({batch}) {table} SET {set} WHERE {pending};";
            context.Trace("retype.backfill", sql);
            int converted;
            do
            {
                converted = await ExecuteAsync(context.Connection, null, sql, ct).ConfigureAwait(false);
            }
            while (converted > 0);

            return;
        }

        // Keyset batches over the integer identity: each finds the next BatchSize keys with one seek and converts that
        // range, so every statement touches a bounded number of rows whatever the table's size or its identity's gaps.
        var key = Bracket(dependencies.RowIdentity[0]);
        var keyType = identity.Render();
        var first = $"""
            DECLARE @hi bigint = (SELECT MAX({key}) FROM (SELECT TOP ({batch}) {key} FROM {table} ORDER BY {key}) b);
            IF @hi IS NOT NULL UPDATE {table} SET {set} WHERE {key} <= CONVERT({keyType}, @hi) AND {pending};
            SELECT @hi;
            """;
        var next = $"""
            DECLARE @hi bigint = (SELECT MAX({key}) FROM (SELECT TOP ({batch}) {key} FROM {table} WHERE {key} > CONVERT({keyType}, @lo) ORDER BY {key}) b);
            IF @hi IS NOT NULL UPDATE {table} SET {set} WHERE {key} > CONVERT({keyType}, @lo) AND {key} <= CONVERT({keyType}, @hi) AND {pending};
            SELECT @hi;
            """;
        context.Trace("retype.backfill", first);
        context.Trace("retype.backfill", next);

        long? lo = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var command = new SqlCommand(lo is null ? first : next, context.Connection) { CommandTimeout = 0 };
            if (lo is { } low)
            {
                command.Parameters.Add(new SqlParameter("@lo", SqlDbType.BigInt) { Value = low });
            }

            var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (result is null or DBNull)
            {
                return;
            }

            lo = System.Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
    }

    private static async Task VerifyAsync(RetypeContext context, ColumnTypeChange change, TableDependencies dependencies, CancellationToken ct)
    {
        var table = Qualify(context.Table);
        var sql = $"SELECT COUNT_BIG(*) FROM {table} WHERE {Bracket(ShadowName(change.Column))} IS NULL AND {Bracket(change.Column)} IS NOT NULL;";
        context.Trace("retype.verify", sql);
        for (var pass = 0; pass < 2; pass++)
        {
            ct.ThrowIfCancellationRequested();
            await using var command = new SqlCommand(sql, context.Connection) { CommandTimeout = 0 };
            var left = System.Convert.ToInt64(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (left == 0)
            {
                return;
            }

            // Rows a writer reached between the backfill and here without the trigger (it was disabled, say): convert
            // them once more, then insist.
            await BackfillAsync(context, change, dependencies, ct).ConfigureAwait(false);
        }

        throw new SqlFlowException(
            $"{table}.[{change.Column}] still holds values its shadow column has not taken after two passes; the conversion stops before " +
            "swapping anything. Check that nothing disables the trigger SQLFlow adds for the conversion, then run the flow again.");
    }

    private sealed record Copies(IReadOnlyList<(string Original, string Copy)> Indexes, IReadOnlyList<(string Original, string Copy)> Statistics);

    private static async Task<Copies> RebuildAsync(RetypeContext context, ColumnTypeChange change, TableDependencies dependencies, CancellationToken ct)
    {
        var table = Qualify(context.Table);
        var shadowName = ShadowName(change.Column);
        var shadow = Bracket(shadowName);

        var nullable = await IsNullableAsync(context.Connection, context.Table, change.Column, ct).ConfigureAwait(false);
        var shadowNullable = await IsNullableAsync(context.Connection, context.Table, shadowName, ct).ConfigureAwait(false);
        if (!nullable && shadowNullable)
        {
            // Validated by one scan under the schema lock, before any index is built over the shadow (an indexed column's
            // nullability cannot change).
            var notNull = $"ALTER TABLE {table} ALTER COLUMN {shadow} {change.ToType.Render()} NOT NULL;";
            context.Trace("retype.not-null", notNull);
            await ExecuteAsync(context.Connection, null, notNull, ct).ConfigureAwait(false);
        }

        var online = await SupportsOnlineIndexBuildsAsync(context.Connection, ct).ConfigureAwait(false);
        var indexes = new List<(string, string)>();
        foreach (var index in dependencies.IndexesOn(change.Column))
        {
            var copy = CopyName(index.Name);
            var sql = $"IF INDEXPROPERTY(OBJECT_ID(N'{Literal(table)}', N'U'), N'{Literal(copy)}', 'IndexID') IS NULL\n    " +
                      IndexSql(table, index, copy, change.Column, shadowName, online);
            context.Trace("retype.index", sql);
            await ExecuteAsync(context.Connection, null, sql, ct).ConfigureAwait(false);
            indexes.Add((index.Name, copy));
        }

        var statistics = new List<(string, string)>();
        foreach (var stat in dependencies.StatisticsOn(change.Column))
        {
            var copy = CopyName(stat.Name);
            var columns = string.Join(", ", stat.Columns.Select(c => Bracket(Swap(c, change.Column, shadowName))));
            var filter = stat.Filter is null ? string.Empty : $" WHERE {SwapInFilter(stat.Filter, change.Column, shadowName)}";
            var sql = $"IF NOT EXISTS (SELECT 1 FROM sys.stats WHERE object_id = OBJECT_ID(N'{Literal(table)}', N'U') AND name = N'{Literal(copy)}')\n    " +
                      $"CREATE STATISTICS {Bracket(copy)} ON {table} ({columns}){filter};";
            context.Trace("retype.statistics", sql);
            await ExecuteAsync(context.Connection, null, sql, ct).ConfigureAwait(false);
            statistics.Add((stat.Name, copy));
        }

        return new Copies(indexes, statistics);
    }

    private static string IndexSql(string table, IndexDefinition index, string copy, string column, string shadow, bool online)
    {
        var keys = index.Columns.Where(c => !c.Included)
            .Select(c => $"{Bracket(Swap(c.Name, column, shadow))}{(c.Descending ? " DESC" : string.Empty)}");
        var included = index.Columns.Where(c => c.Included).Select(c => Bracket(Swap(c.Name, column, shadow))).ToList();
        var options = new List<string>
        {
            $"PAD_INDEX = {(index.IsPadded ? "ON" : "OFF")}",
            $"IGNORE_DUP_KEY = {(index.IgnoreDupKey ? "ON" : "OFF")}",
            $"ALLOW_ROW_LOCKS = {(index.AllowRowLocks ? "ON" : "OFF")}",
            $"ALLOW_PAGE_LOCKS = {(index.AllowPageLocks ? "ON" : "OFF")}",
            $"DATA_COMPRESSION = {index.Compression}",
        };
        if (index.FillFactor > 0)
        {
            options.Add($"FILLFACTOR = {index.FillFactor.ToString(CultureInfo.InvariantCulture)}");
        }

        if (online)
        {
            options.Add("ONLINE = ON");
        }

        return $"CREATE {(index.IsUnique ? "UNIQUE " : string.Empty)}NONCLUSTERED INDEX {Bracket(copy)} ON {table} ({string.Join(", ", keys)})" +
               (included.Count > 0 ? $" INCLUDE ({string.Join(", ", included)})" : string.Empty) +
               (index.Filter is null ? string.Empty : $" WHERE {SwapInFilter(index.Filter, column, shadow)}") +
               $" WITH ({string.Join(", ", options)});";
    }

    private static async Task SwapAsync(RetypeContext context, ColumnTypeChange change, Copies copies, CancellationToken ct)
    {
        var table = Qualify(context.Table);
        var literal = Literal(table);
        var shadow = ShadowName(change.Column);
        var old = OldName(change.Column);
        var sql = new StringBuilder();
        sql.Append("SET XACT_ABORT ON;\nBEGIN TRANSACTION;\n");
        sql.Append(CultureInfo.InvariantCulture, $"DROP TRIGGER IF EXISTS [{Escape(context.Table.Schema)}].{Bracket(TriggerName(context.Table, change.Column))};\n");
        foreach (var (original, _) in copies.Indexes)
        {
            sql.Append(CultureInfo.InvariantCulture, $"DROP INDEX {Bracket(original)} ON {table};\n");
        }

        foreach (var (original, _) in copies.Statistics)
        {
            sql.Append(CultureInfo.InvariantCulture, $"DROP STATISTICS {table}.{Bracket(original)};\n");
        }

        sql.Append(CultureInfo.InvariantCulture, $"EXEC sys.sp_rename N'{Literal($"{table}.{Bracket(change.Column)}")}', N'{Literal(old)}', N'COLUMN';\n");
        sql.Append(CultureInfo.InvariantCulture, $"EXEC sys.sp_rename N'{Literal($"{table}.{Bracket(shadow)}")}', N'{Literal(change.Column)}', N'COLUMN';\n");
        sql.Append(CultureInfo.InvariantCulture, $"ALTER TABLE {table} DROP COLUMN {Bracket(old)};\n");
        foreach (var (original, copy) in copies.Indexes)
        {
            sql.Append(CultureInfo.InvariantCulture, $"EXEC sys.sp_rename N'{Literal($"{table}.{Bracket(copy)}")}', N'{Literal(original)}', N'INDEX';\n");
        }

        foreach (var (original, copy) in copies.Statistics)
        {
            sql.Append(CultureInfo.InvariantCulture, $"EXEC sys.sp_rename N'{Literal($"{table}.{Bracket(copy)}")}', N'{Literal(original)}', N'STATISTICS';\n");
        }

        foreach (var property in new[] { PropertyOf, PropertyTo })
        {
            sql.Append(CultureInfo.InvariantCulture, $"""
                IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 1 AND major_id = OBJECT_ID(N'{literal}', N'U')
                           AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'{literal}', N'U'), N'{Literal(change.Column)}', 'ColumnId') AND name = N'{property}')
                    EXEC sys.sp_dropextendedproperty @name = N'{property}',
                         @level0type = N'SCHEMA', @level0name = N'{Literal(context.Table.Schema)}', @level1type = N'TABLE',
                         @level1name = N'{Literal(context.Table.Name)}', @level2type = N'COLUMN', @level2name = N'{Literal(change.Column)}';

                """);
        }

        sql.Append("COMMIT TRANSACTION;");
        context.Trace("retype.swap", sql.ToString());
        await ExecuteAsync(context.Connection, null, sql.ToString(), ct).ConfigureAwait(false);
    }

    private static async Task<bool> TriggerIntactAsync(SqlConnection connection, RelationalObject table, string column, CancellationToken ct)
    {
        const string sql = "SELECT COUNT(*) FROM sys.triggers WHERE parent_id = OBJECT_ID(@table, N'U') AND name = @name AND is_disabled = 0;";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 600) { Value = Qualify(table) });
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = TriggerName(table, column) });
        return System.Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> IsNullableAsync(SqlConnection connection, RelationalObject table, string column, CancellationToken ct)
    {
        const string sql = "SELECT CONVERT(int, COLUMNPROPERTY(OBJECT_ID(@table, N'U'), @column, 'AllowsNull'));";
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@table", SqlDbType.NVarChar, 600) { Value = Qualify(table) });
        command.Parameters.Add(new SqlParameter("@column", SqlDbType.NVarChar, 128) { Value = column });
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is not (null or DBNull) && System.Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    // Enterprise (3), Azure SQL Database (5) and Managed Instance (8) build indexes online; Standard and Express do not.
    private static async Task<bool> SupportsOnlineIndexBuildsAsync(SqlConnection connection, CancellationToken ct)
    {
        await using var command = new SqlCommand("SELECT CONVERT(int, SERVERPROPERTY('EngineEdition'));", connection) { CommandTimeout = 0 };
        var edition = System.Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return edition is 3 or 5 or 8;
    }

    private static async Task<List<TypeChangeValueSample>> ReadSampleAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        var sample = new List<TypeChangeValueSample>();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            sample.Add(new TypeChangeValueSample { Value = reader.GetString(0), Rows = reader.GetInt64(1) });
        }

        return sample;
    }

    internal static string SampleSql(string from, string column, string failing, int size)
    {
        var value = $"LEFT(CONVERT(nvarchar(max), {column}), {TypeChangeValueSample.MaxLength.ToString(CultureInfo.InvariantCulture)})";
        return $"SELECT TOP ({size.ToString(CultureInfo.InvariantCulture)}) {value} AS value, COUNT_BIG(*) AS rows FROM {from} " +
               $"WHERE {failing} GROUP BY {value} ORDER BY COUNT_BIG(*) DESC, {value};";
    }

    private static async Task<int> ExecuteAsync(SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken ct)
    {
        // A cancelled run stops between statements as a cancellation, never by aborting one half way.
        ct.ThrowIfCancellationRequested();
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 0 };
        try
        {
            return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 1222 or 1204 or 1205)
        {
            throw new SchemaLockTimeoutException(connection.Database, null, ex);
        }
    }

    private static async Task SetLockTimeoutAsync(SqlConnection connection, int timeoutMs, CancellationToken ct)
    {
        if (connection.State != ConnectionState.Open)
        {
            return;
        }

        await using var command = new SqlCommand($"SET LOCK_TIMEOUT {timeoutMs.ToString(CultureInfo.InvariantCulture)};", connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Swap(string name, string column, string shadow)
        => string.Equals(name, column, StringComparison.OrdinalIgnoreCase) ? shadow : name;

    // SQL Server stores a filter with every column bracketed, so the column is replaced by its bracketed name.
    private static string SwapInFilter(string filter, string column, string shadow)
        => filter.Replace(Bracket(column), Bracket(shadow), StringComparison.OrdinalIgnoreCase);

    // A name within SQL Server's 128 characters that always ends with its suffix: a long base is cut and given a short
    // hash of itself, so two long names never collide.
    private static string Bounded(string name, string suffix)
    {
        if (name.Length + suffix.Length <= 128)
        {
            return name + suffix;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
        return name[..(128 - suffix.Length - 9)] + "_" + hash + suffix;
    }

    private static string Qualify(RelationalObject table) => $"[{Escape(table.Schema)}].[{Escape(table.Name)}]";

    private static string Bracket(string identifier) => $"[{Escape(identifier)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    private static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
