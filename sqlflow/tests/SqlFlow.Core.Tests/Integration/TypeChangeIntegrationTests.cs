using Microsoft.Data.SqlClient;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.SqlServer.Catalog;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The three tiers of a change to the type of a column that already exists, against the real sink: a widening in place
/// and by the safe rewrite (only with its opt-in), a checked conversion applied, refused by its data, and pending without
/// its opt-in, a tier 3 key change and a lossy change refused with their steps, and an interrupted rewrite finished or
/// rolled back. Every table carries an identity primary key, system dates and an index on the changing column, as an
/// ingestion target does, and every test checks that each row, its identity and its dates survive.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TypeChangeIntegrationTests
{
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly RetypeOptions Options = new() { BatchSize = 3 };
    private static readonly TypeChangePolicy Nothing = new(AllowTableRewrite: false, ConvertTypes: false);
    private static readonly TypeChangePolicy Everything = new(AllowTableRewrite: true, ConvertTypes: true);

    private static SchemaSyncService Service() => new(new SqlServerCatalogReader());

    private static RelationalObject Target(string table) => new() { Database = "db", Schema = "dbo", Name = table };

    private static SqlColumn Col(string name, string type, bool nullable = true)
        => new() { Name = name, DataType = SqlDataType.Parse(type), IsNullable = nullable };

    private static void NoTrace(string step, string sql)
    {
    }

    private static async Task CreateAsync(string cs, string table, string columnType, params string?[] values)
    {
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{table}] ([RecId] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_{table}] PRIMARY KEY CLUSTERED, " +
            $"[Code] varchar(20) NOT NULL, [V] {columnType} NULL, [InsertedDate_DW] datetime NULL, [UpdatedDate_DW] datetime NULL);");
        for (var i = 0; i < values.Length; i++)
        {
            var literal = values[i] is null ? "NULL" : $"N'{values[i]!.Replace("'", "''", StringComparison.Ordinal)}'";
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{table}] ([Code], [V], [InsertedDate_DW], [UpdatedDate_DW]) " +
                $"VALUES ('c{i}', {literal}, DATEADD(day, -{i + 1}, '2026-01-31'), DATEADD(day, -{i}, '2026-01-31'));");
        }

        await IntegrationDb.ExecuteAsync(cs, $"CREATE NONCLUSTERED INDEX [IX_{table}_V] ON [dbo].[{table}] ([V]) INCLUDE ([Code]);");
    }

    // Every row's identity, code and system dates, the fingerprint a rewrite must leave as it was.
    private static Task<string?> FingerprintAsync(string cs, string table)
        => IntegrationDb.ScalarAsync<string?>(cs,
            $"SELECT STRING_AGG(CONCAT([RecId], ':', [Code], ':', CONVERT(varchar(30), [InsertedDate_DW], 126), ':', " +
            $"CONVERT(varchar(30), [UpdatedDate_DW], 126)), '|') WITHIN GROUP (ORDER BY [RecId]) FROM [dbo].[{table}]");

    private static Task<string?> ValuesAsync(string cs, string table)
        => IntegrationDb.ScalarAsync<string?>(cs,
            $"SELECT STRING_AGG(CONCAT([RecId], '=', ISNULL(CONVERT(nvarchar(100), [V], 121), N'NULL')), '|') WITHIN GROUP (ORDER BY [RecId]) FROM [dbo].[{table}]");

    private static Task<int?> ColumnCountAsync(string cs, string table)
        => IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('[dbo].[{table}]')");

    private static Task<string?> IndexKeyColumnAsync(string cs, string table, string index)
        => IntegrationDb.ScalarAsync<string?>(cs,
            $"SELECT c.name FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 " +
            $"JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE i.object_id = OBJECT_ID('[dbo].[{table}]') AND i.name = '{index}'");

    private static Task<IReadOnlyList<TypeChangeReport>> ApplyAsync(
        string cs, string table, string toType, TypeChangeTiming timing, TypeChangePolicy policy, Action<string, string>? trace = null,
        CancellationToken ct = default)
        => TypeChangeApplier.ApplyAsync(Service(), cs, Target(table), Desired(toType), NoKeys, timing, policy, Options, trace ?? NoTrace, ct);

    private static IReadOnlyList<SqlColumn> Desired(string type)
        => [Col("RecId", "int", nullable: false), Col("Code", "varchar(20)", nullable: false), Col("V", type),
            Col("InsertedDate_DW", "datetime"), Col("UpdatedDate_DW", "datetime")];

    [SkippableFact]
    public async Task A_variable_length_growth_is_applied_in_place_with_its_index_kept()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfType_Grow";
        await CreateAsync(cs, table, "varchar(20)", "a", "bb", null);
        try
        {
            var before = await FingerprintAsync(cs, table);
            var outcome = await Service().EvolveAsync(cs, Target(table), Desired("varchar(60)"), NoKeys, allowTableRewrite: false, new SchemaApplyOptions());
            var reports = await TypeChangeApplier.DescribeAsync(cs, Target(table), outcome.Plan, outcome.AlteredColumns, Options, NoTrace, default);

            var report = Assert.Single(reports);
            Assert.Equal(TypeChangeTier.Widening, report.Tier);
            Assert.Equal(TypeChangeStatus.Applied, report.Status);
            Assert.False(report.RewritesTable);
            Assert.Equal("varchar(60)", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));
            Assert.True(await IntegrationDb.IndexExistsAsync(cs, table, $"IX_{table}_V"));
            Assert.Equal(before, await FingerprintAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task A_widening_that_rewrites_waits_for_its_opt_in_then_keeps_every_row_and_rebuilds_the_index()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfType_Widen";
        await CreateAsync(cs, table, "int", "1", "2", null, "2147483647", "-5");
        try
        {
            var before = await FingerprintAsync(cs, table);
            var values = await ValuesAsync(cs, table);

            var pending = Assert.Single(await ApplyAsync(cs, table, "bigint", TypeChangeTiming.BeforeLoad, Nothing));
            Assert.Equal(TypeChangeStatus.Pending, pending.Status);
            Assert.True(pending.NeedsAttention);
            Assert.Equal(ColumnTypeChange.AllowTableRewrite, pending.RequiresOptIn);
            Assert.Equal("int", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));

            var applied = Assert.Single(await ApplyAsync(cs, table, "bigint", TypeChangeTiming.BeforeLoad, Everything));
            Assert.Equal(TypeChangeStatus.Applied, applied.Status);
            Assert.Equal(TypeChangeTier.Widening, applied.Tier);
            Assert.Equal("bigint", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));
            Assert.Equal("V", await IndexKeyColumnAsync(cs, table, $"IX_{table}_V"));
            Assert.Equal(before, await FingerprintAsync(cs, table));
            Assert.Equal(values, await ValuesAsync(cs, table));
            Assert.Equal(5, await ColumnCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Text_whose_every_value_converts_becomes_a_date_with_rows_identity_dates_index_and_view_kept()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfType_ToDate";
        const string view = "_SfType_ToDate_v";
        await CreateAsync(cs, table, "varchar(255)", "2026-11-27", " 2025-01-02 ", null, "2024-02-29 00:00:00", "1999-12-31");
        await IntegrationDb.ExecuteAsync(cs, $"CREATE STATISTICS [ST_{table}_V] ON [dbo].[{table}] ([V], [Code]);");
        await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[{view}];");
        await IntegrationDb.ExecuteAsync(cs, $"CREATE VIEW [dbo].[{view}] AS SELECT * FROM [dbo].[{table}];");
        try
        {
            var before = await FingerprintAsync(cs, table);

            // A text column becoming typed waits for the load: nothing happens before it.
            Assert.Empty(await ApplyAsync(cs, table, "date", TypeChangeTiming.BeforeLoad, Everything));

            var report = Assert.Single(await ApplyAsync(cs, table, "date", TypeChangeTiming.AfterLoad, Everything));
            Assert.Equal(TypeChangeStatus.Applied, report.Status);
            Assert.Equal(TypeChangeTier.CheckedConversion, report.Tier);
            Assert.Equal("date", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));
            Assert.Equal("1=2026-11-27|2=2025-01-02|3=NULL|4=2024-02-29|5=1999-12-31", await ValuesAsync(cs, table));
            Assert.Equal(before, await FingerprintAsync(cs, table));
            Assert.Equal("V", await IndexKeyColumnAsync(cs, table, $"IX_{table}_V"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM sys.stats WHERE object_id = OBJECT_ID('[dbo].[{table}]') AND name = 'ST_{table}_V'"));
            Assert.Equal("date", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT TYPE_NAME(user_type_id) FROM sys.columns WHERE object_id = OBJECT_ID('[dbo].[{view}]') AND name = 'V'"));
            Assert.Equal(5, await ColumnCountAsync(cs, table));
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM sys.triggers WHERE parent_id = OBJECT_ID('[dbo].[{table}]')"));
        }
        finally
        {
            await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[{view}];");
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Text_holding_values_that_do_not_convert_is_refused_with_a_sample_and_the_table_untouched()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfType_Refuse";
        await CreateAsync(cs, table, "varchar(255)", "NA", "2026-11-27", "NA", "2026-11-27 10:15", "NA", "27.11.2026");
        try
        {
            var before = await FingerprintAsync(cs, table);
            var values = await ValuesAsync(cs, table);

            var report = Assert.Single(await ApplyAsync(cs, table, "date", TypeChangeTiming.AfterLoad, Everything));

            Assert.Equal(TypeChangeStatus.Refused, report.Status);
            Assert.True(report.NeedsAttention);
            Assert.Equal(6, report.Rows);
            Assert.Equal(5, report.FailingValues);
            Assert.Equal(new TypeChangeValueSample { Value = "NA", Rows = 3 }, report.Sample[0]);
            Assert.Contains(report.Sample, s => s.Value == "2026-11-27 10:15");
            Assert.Contains("full load", report.Message, StringComparison.Ordinal);
            Assert.Equal("varchar(255)", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));
            Assert.Equal(values, await ValuesAsync(cs, table));
            Assert.Equal(before, await FingerprintAsync(cs, table));
            Assert.Equal(5, await ColumnCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task A_checked_conversion_without_its_opt_in_is_a_pending_warning_and_the_table_untouched()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfType_Pending";
        await CreateAsync(cs, table, "varchar(255)", "2026-11-27");
        try
        {
            var report = Assert.Single(await ApplyAsync(cs, table, "date", TypeChangeTiming.AfterLoad, Nothing));

            Assert.Equal(TypeChangeStatus.Pending, report.Status);
            Assert.True(report.NeedsAttention);
            Assert.Equal(ColumnTypeChange.ConvertTypes, report.RequiresOptIn);
            Assert.Contains("schema.convertTypes: true", report.Message, StringComparison.Ordinal);
            Assert.Equal("varchar(255)", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task A_key_column_change_is_manual_blocks_the_load_and_names_the_keys_that_would_merge()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfType_Key";
        await CreateAsync(cs, table, "varchar(20)", "00123", "123", "7");
        try
        {
            var keys = new HashSet<string>(["V"], StringComparer.OrdinalIgnoreCase);
            await using var connection = new SqlConnection(cs);
            await connection.OpenAsync();
            var plan = await Service().PlanAsync(connection, Target(table), Desired("int"), keys);

            Assert.True(plan.IsBlocked);
            var reports = await TypeChangeApplier.DescribeAsync(cs, Target(table), plan, new HashSet<string>(), Options, NoTrace, default);
            var report = Assert.Single(reports);
            Assert.Equal(TypeChangeTier.Manual, report.Tier);
            Assert.Equal(TypeChangeStatus.Manual, report.Status);
            Assert.True(report.IsKey);
            Assert.Contains("1 converted key(s)", report.Message, StringComparison.Ordinal);
            Assert.Contains("'123'", report.Message, StringComparison.Ordinal);
            Assert.NotEmpty(report.ManualSteps);
            await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() =>
                Service().EvolveAsync(cs, Target(table), Desired("int"), keys, allowTableRewrite: true, new SchemaApplyOptions()));
            Assert.Equal("varchar(20)", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task A_lossy_change_is_manual_with_its_steps_and_never_applied()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfType_Lossy";
        await CreateAsync(cs, table, "float", "1.25", "0.1");
        try
        {
            await using var connection = new SqlConnection(cs);
            await connection.OpenAsync();
            var plan = await Service().PlanAsync(connection, Target(table), Desired("decimal(18,2)"), NoKeys);
            Assert.False(plan.IsBlocked);
            Assert.Empty(await ApplyAsync(cs, table, "decimal(18,2)", TypeChangeTiming.BeforeLoad, Everything));

            var report = Assert.Single(await TypeChangeApplier.DescribeAsync(cs, Target(table), plan, new HashSet<string>(), Options, NoTrace, default));
            Assert.Equal(TypeChangeTier.Manual, report.Tier);
            Assert.Equal(TypeChangeStatus.Manual, report.Status);
            Assert.True(report.NeedsAttention);
            Assert.NotEmpty(report.ManualSteps);
            Assert.Equal("float", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task An_interrupted_rewrite_is_finished_by_the_next_run_with_writes_made_meanwhile_kept()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfType_Resume";
        await CreateAsync(cs, table, "varchar(255)", "2026-01-01", "2026-01-02", "2026-01-03", "2026-01-04");
        try
        {
            var before = await FingerprintAsync(cs, table);
            await InterruptBeforeVerifyAsync(cs, table, "date");
            Assert.Equal(6, await ColumnCountAsync(cs, table));

            // While the conversion is half done, a writer updates a row: the trigger carries it into the shadow column,
            // and refuses a value the new type cannot hold.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{table}] SET [V] = '2030-05-06' WHERE [RecId] = 2;");
            var refused = await Assert.ThrowsAsync<SqlException>(() =>
                IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{table}] SET [V] = 'NA' WHERE [RecId] = 3;"));
            Assert.Equal(51000, refused.Number);

            var resumed = Assert.Single(await TypeChangeApplier.ResumeAsync(
                Service(), cs, Target(table), Desired("date"), NoKeys, Everything, Options, NoTrace, default));

            Assert.Equal(TypeChangeStatus.Resumed, resumed.Status);
            Assert.Equal("date", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));
            Assert.Equal("1=2026-01-01|2=2030-05-06|3=2026-01-03|4=2026-01-04", await ValuesAsync(cs, table));
            Assert.Equal(before, await FingerprintAsync(cs, table));
            Assert.Equal(5, await ColumnCountAsync(cs, table));
            Assert.Equal("V", await IndexKeyColumnAsync(cs, table, $"IX_{table}_V"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task An_interrupted_rewrite_the_source_no_longer_wants_is_rolled_back_leaving_the_table_as_it_was()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfType_Rollback";
        await CreateAsync(cs, table, "varchar(255)", "2026-01-01", "2026-01-02");
        try
        {
            var values = await ValuesAsync(cs, table);
            await InterruptBeforeVerifyAsync(cs, table, "date");

            var rolledBack = Assert.Single(await TypeChangeApplier.ResumeAsync(
                Service(), cs, Target(table), Desired("varchar(255)"), NoKeys, Everything, Options, NoTrace, default));

            Assert.Equal(TypeChangeStatus.RolledBack, rolledBack.Status);
            Assert.Equal("varchar(255)", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));
            Assert.Equal(values, await ValuesAsync(cs, table));
            Assert.Equal(5, await ColumnCountAsync(cs, table));
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM sys.triggers WHERE parent_id = OBJECT_ID('[dbo].[{table}]')"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('[dbo].[{table}]') AND type = 2"));
            Assert.Empty(await TypeChangeApplier.ResumeAsync(
                Service(), cs, Target(table), Desired("varchar(255)"), NoKeys, Everything, Options, NoTrace, default));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task A_typed_column_becomes_text_before_the_load_when_every_rendering_fits()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfType_ToText";
        await CreateAsync(cs, table, "int", "12", "-7", null);
        try
        {
            var report = Assert.Single(await ApplyAsync(cs, table, "varchar(20)", TypeChangeTiming.BeforeLoad, Everything));
            Assert.Equal(TypeChangeStatus.Applied, report.Status);
            Assert.Equal("varchar(20)", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));
            Assert.Equal("1=12|2=-7|3=NULL", await ValuesAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task The_write_guard_stops_a_load_whose_values_would_change_on_the_way_into_the_target()
    {
        var cs = IntegrationDb.Require();
        const string staging = "_SfType_Guard";
        await IntegrationDb.DropTableAsync(cs, staging);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{staging}] ([Amount] decimal(10,2) NULL, [When] datetime2(7) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{staging}] VALUES (3.00, '2026-01-01'), (2.50, '2026-01-01 10:00'), (2.50, NULL);");
        try
        {
            var written = new[]
            {
                new StagingWriteGuard.WrittenColumn("Amount", SqlDataType.Parse("decimal(10,2)"), SqlDataType.Parse("int")),
                new StagingWriteGuard.WrittenColumn("When", SqlDataType.Parse("datetime2(7)"), SqlDataType.Parse("datetime2(7)")),
            };
            var refused = await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() =>
                StagingWriteGuard.EnsureLosslessAsync(cs, Target(staging), Target("_Trg"), written, 5, NoTrace, default));
            Assert.Contains("[Amount]: 2 staged value(s)", refused.Message, StringComparison.Ordinal);
            Assert.Contains("'2.50' in 2 row(s)", refused.Message, StringComparison.Ordinal);

            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{staging}] SET [Amount] = 4;");
            var checkedColumns = await StagingWriteGuard.EnsureLosslessAsync(cs, Target(staging), Target("_Trg"), written, 5, NoTrace, default);
            Assert.Single(checkedColumns);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, staging);
        }
    }

    [SkippableTheory]
    [InlineData("varchar(40)", "date", "2026-11-27", 1)]
    [InlineData("varchar(40)", "date", "2026-11-27 00:00:00", 1)]
    [InlineData("varchar(40)", "date", "2026-11-27 10:15", 0)]
    [InlineData("varchar(40)", "date", "2026-11-27T00:00:00+05:00", 0)]
    [InlineData("varchar(40)", "date", "03/04/2026", 0)]
    [InlineData("varchar(40)", "date", "NA", 0)]
    [InlineData("varchar(40)", "datetime2(3)", "2026-11-27 10:15:00.123", 1)]
    [InlineData("varchar(40)", "datetime2(3)", "2026-11-27 10:15:00.1236", 0)]
    [InlineData("varchar(40)", "datetime2(7)", "2026-11-27 10:15:00.12345678", 0)]
    [InlineData("varchar(40)", "decimal(10,2)", "1.239", 0)]
    [InlineData("varchar(40)", "decimal(10,2)", "1.230", 1)]
    [InlineData("varchar(40)", "decimal(10,2)", " -12.5 ", 1)]
    [InlineData("varchar(40)", "decimal(4,2)", "123.4", 0)]
    [InlineData("varchar(40)", "decimal(10,2)", "1E2", 0)]
    [InlineData("varchar(40)", "int", "7", 1)]
    [InlineData("varchar(40)", "int", "007", 0)]
    [InlineData("varchar(40)", "int", "+7", 0)]
    [InlineData("varchar(40)", "int", "1.0", 0)]
    [InlineData("varchar(40)", "bit", "true", 1)]
    [InlineData("varchar(40)", "bit", "5", 0)]
    [InlineData("varchar(60)", "uniqueidentifier", "6F9619FF-8B86-D011-B42D-00C04FC964FF", 1)]
    [InlineData("varchar(60)", "uniqueidentifier", "6F9619FF-8B86-D011-B42D-00C04FC964FFabc", 0)]
    [InlineData("varchar(10)", "varchar(3)", "abc", 1)]
    [InlineData("varchar(10)", "varchar(3)", "abcd", 0)]
    [InlineData("varchar(10)", "varchar(3)", "abc  ", 0)]
    [InlineData("nvarchar(10)", "varchar(10)", "Ωmega", 0)]
    [InlineData("nvarchar(10)", "varchar(10)", "Omega", 1)]
    public async Task The_lossless_test_accepts_only_values_that_convert_unchanged(string from, string to, string value, int expected)
    {
        var cs = IntegrationDb.Require();
        var flag = LosslessConversion.LosslessFlag(SqlDataType.Parse(from), SqlDataType.Parse(to), "CONVERT(" + from + ", @v)");
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT {flag};", connection);
        command.Parameters.AddWithValue("@v", value);
        Assert.Equal(expected, Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [SkippableTheory]
    [InlineData("int", "varchar(20)", 1)]
    [InlineData("datetime2(7)", "varchar(10)", 0)]
    [InlineData("float", "varchar(50)", 0)]
    [InlineData("decimal(10,4)", "decimal(10,2)", 0)]
    [InlineData("datetime2(7)", "date", 0)]
    public async Task The_lossless_test_round_trips_typed_values(string from, string to, int expected)
    {
        var cs = IntegrationDb.Require();
        var value = from switch
        {
            "int" => "CONVERT(int, -12345)",
            "float" => "CONVERT(float, 1234567.891)",
            "decimal(10,4)" => "CONVERT(decimal(10,4), 1.2345)",
            _ => "CONVERT(datetime2(7), '2026-11-27 10:15:00')",
        };
        var flag = LosslessConversion.LosslessFlag(SqlDataType.Parse(from), SqlDataType.Parse(to), value);
        Assert.Equal(expected, await IntegrationDb.ScalarAsync<int>(cs, $"SELECT {flag};"));
    }

    // Leaves the conversion of [V] half done, as a run that died between the backfill and the swap would: the shadow
    // column is filled and the trigger is in place, the column itself untouched.
    private static async Task InterruptBeforeVerifyAsync(string cs, string table, string toType)
    {
        using var cancel = new CancellationTokenSource();
        void StopAtVerify(string step, string sql)
        {
            if (step == "retype.verify")
            {
                cancel.Cancel();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ApplyAsync(cs, table, toType, TypeChangeTiming.AfterLoad, Everything, StopAtVerify, cancel.Token));
        Assert.Equal("varchar(255)", await IntegrationDb.ColumnTypeAsync(cs, table, "V"));
        Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM sys.triggers WHERE parent_id = OBJECT_ID('[dbo].[{table}]')"));
    }
}
