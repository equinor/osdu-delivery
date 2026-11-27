using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The plan of an ingestion flow against the sink: after a run created the target and the source moved a column to a new
/// type, <see cref="IngestionFlowRunner.PlanAsync"/> reports what a run with the flow's settings would do (a checked
/// conversion pending without its opt-in, planned with it when every stored value converts and refused with a count and a
/// sample when some do not, a key change manual and blocking the load, a rewrite an earlier run left unfinished finished or
/// rolled back), and leaves the target exactly as it was: the same columns and types, the same rows, no shadow column, no
/// trigger, no staging table.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IngestionPlanIntegrationTests
{
    private static IngestionFlow Flow(
        int flowId, string src, string trg, bool convertTypes, bool allowTableRewrite = false, bool sync = true,
        IncrementalPolicy? incremental = null)
        => new()
        {
            FlowId = flowId,
            Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
            Target = new IngestionTarget
            {
                Server = "sink",
                Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg },
                IdentityColumn = "RecId",
            },
            Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
            SchemaSync = new SchemaSyncPolicy { Sync = sync, ConvertTypes = convertTypes, AllowTableRewrite = allowTableRewrite },
            Incremental = incremental ?? new IncrementalPolicy(),
        };

    private static async Task ResetAsync(string cs, string src, string trg, int flowId, string sourceColumns)
    {
        await CleanupAsync(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ({sourceColumns});");
    }

    private static async Task CleanupAsync(string cs, string src, string trg, int flowId)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
    }

    // Everything a plan could have changed about the target: each column's name, position, type and nullability, a hash of
    // every row as JSON, the triggers, the extended properties, the indexes, the table's last schema change, and the flow's
    // staging tables.
    private static Task<string?> SnapshotAsync(string cs, string table, int flowId)
        => IntegrationDb.ScalarAsync<string?>(cs, $"""
            DECLARE @id int = OBJECT_ID(N'[dbo].[{table}]');
            SELECT CONCAT(
                (SELECT STRING_AGG(CONCAT(c.column_id, ':', c.name, ':', t.name, ':', c.max_length, ':', c.precision, ':', c.scale, ':', c.is_nullable), '|')
                        WITHIN GROUP (ORDER BY c.column_id)
                 FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = @id),
                '#', (SELECT COUNT_BIG(*) FROM [dbo].[{table}]),
                '#', CONVERT(varchar(64), HASHBYTES('SHA2_256', (SELECT * FROM [dbo].[{table}] ORDER BY [RecId] FOR JSON PATH, INCLUDE_NULL_VALUES)), 2),
                '#', (SELECT COUNT(*) FROM sys.triggers WHERE parent_id = @id),
                '#', (SELECT COUNT(*) FROM sys.extended_properties WHERE class = 1 AND major_id = @id),
                '#', (SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY name) FROM sys.indexes WHERE object_id = @id AND name IS NOT NULL),
                '#', (SELECT CONVERT(varchar(30), modify_date, 126) FROM sys.objects WHERE object_id = @id),
                '#', (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
                      WHERE s.name = 'raw' AND t.name LIKE '%[_]{flowId}'));
            """);

    [SkippableFact]
    public async Task A_checked_conversion_is_pending_without_its_opt_in_and_planned_or_refused_by_its_data_with_it()
    {
        const int flowId = 76;
        var cs = IntegrationDb.Require();
        const string src = "_SfPlan1_Src";
        const string trg = "_SfPlan1_Trg";
        await ResetAsync(cs, src, trg, flowId, "[Id] int NOT NULL, [Good] varchar(30) NULL, [Bad] varchar(30) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1, '2026-01-05', 'NA'), (2, NULL, 'NA'), (3, '2026-03-01', '2026-03-02');");
        try
        {
            // An incremental flow: a later run reads only new rows, so the stored text stays what its check finds.
            var incremental = new IncrementalPolicy { Columns = ["Id"] };
            IngestionFlow FlowOf(bool convertTypes, bool sync = true)
                => Flow(flowId, src, trg, convertTypes, sync: sync, incremental: incremental);

            var runner = RelationalIngestionHarness.BuildRunner();
            Assert.True((await runner.RunAsync(FlowOf(convertTypes: false))).Success);

            // The source now declares both columns dates (its 'NA' is no value) and serves a new column; the target still
            // holds the text it was given.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [Bad] = NULL WHERE [Bad] = 'NA';");
            await IntegrationDb.ExecuteAsync(cs,
                $"ALTER TABLE [dbo].[{src}] ALTER COLUMN [Good] date NULL; ALTER TABLE [dbo].[{src}] ALTER COLUMN [Bad] date NULL; " +
                $"ALTER TABLE [dbo].[{src}] ADD [Note] nvarchar(10) NULL;");
            var before = await SnapshotAsync(cs, trg, flowId);

            var pending = await runner.PlanAsync(FlowOf(convertTypes: false));
            Assert.True(pending.TargetExists);
            Assert.True(pending.SchemaSync);
            Assert.False(pending.IsBlocked);
            Assert.True(pending.NeedsAttention);
            var added = Assert.Single(pending.ColumnsToAdd);
            Assert.Equal("Note", added.Name);
            Assert.Equal("nvarchar(10)", added.Type, ignoreCase: true);
            Assert.Empty(pending.ColumnsToAlter);
            Assert.Empty(pending.CriticalMismatches);
            Assert.Equal(new[] { "Bad", "Good" }, pending.TypeChanges.Select(c => c.Column).Order(StringComparer.Ordinal));
            Assert.All(pending.TypeChanges, c =>
            {
                Assert.Equal(TypeChangeStatus.Pending, c.Status);
                Assert.Equal(TypeChangeTier.CheckedConversion, c.Tier);
                Assert.Equal(ColumnTypeChange.ConvertTypes, c.RequiresOptIn);
                Assert.Equal(3, c.Rows);
                Assert.Equal(TypeChangeReport.TargetLayer, c.Layer);
            });

            var converting = await runner.PlanAsync(FlowOf(convertTypes: true));
            var good = Assert.Single(converting.TypeChanges, c => c.Column == "Good");
            Assert.Equal(TypeChangeStatus.Planned, good.Status);
            Assert.False(good.NeedsAttention);
            Assert.Contains("Every one of the 3 stored value(s) converts to date without loss", good.Message, StringComparison.Ordinal);
            var bad = Assert.Single(converting.TypeChanges, c => c.Column == "Bad");
            Assert.Equal(TypeChangeStatus.Refused, bad.Status);
            Assert.Equal(2, bad.FailingValues);
            var sample = Assert.Single(bad.Sample);
            Assert.Equal("NA", sample.Value);
            Assert.Equal(2, sample.Rows);
            Assert.Contains("A run checks them once its load has written the source's values", bad.Message, StringComparison.Ordinal);
            Assert.True(converting.NeedsAttention);

            // With schema sync off a run writes [Note] into a target that lacks it, so the plan refuses as the run does.
            var off = await Assert.ThrowsAsync<SqlFlowException>(() => runner.PlanAsync(FlowOf(convertTypes: false, sync: false)));
            Assert.Contains("schema.sync is off", off.Message, StringComparison.Ordinal);
            Assert.Contains("Note", off.Message, StringComparison.Ordinal);

            Assert.Equal(before, await SnapshotAsync(cs, trg, flowId));
            Assert.Equal("varchar(30)", await IntegrationDb.ColumnTypeAsync(cs, trg, "Good"));
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            // A run with the opt-in does what the plan said.
            var run = await runner.RunAsync(FlowOf(convertTypes: true));
            Assert.True(run.Success, run.Error);
            Assert.Equal(TypeChangeStatus.Applied, Assert.Single(run.TypeChanges, c => c.Column == "Good").Status);
            Assert.Equal(TypeChangeStatus.Refused, Assert.Single(run.TypeChanges, c => c.Column == "Bad").Status);
        }
        finally
        {
            await CleanupAsync(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task A_key_column_change_is_manual_and_blocks_the_load_naming_the_keys_that_would_merge()
    {
        const int flowId = 77;
        var cs = IntegrationDb.Require();
        const string src = "_SfPlan2_Src";
        const string trg = "_SfPlan2_Trg";
        await ResetAsync(cs, src, trg, flowId, "[Id] varchar(20) NOT NULL, [Name] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES ('00123', N'a'), ('123', N'b'), ('7', N'c');");
        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            Assert.True((await runner.RunAsync(Flow(flowId, src, trg, convertTypes: true))).Success);

            // The source now declares its key an int: '00123' and '123' would both become 123.
            await IntegrationDb.ExecuteAsync(cs, $"ALTER TABLE [dbo].[{src}] ALTER COLUMN [Id] int NOT NULL;");
            var before = await SnapshotAsync(cs, trg, flowId);

            var plan = await runner.PlanAsync(Flow(flowId, src, trg, convertTypes: true, allowTableRewrite: true));
            Assert.True(plan.IsBlocked);
            Assert.True(plan.NeedsAttention);
            Assert.Equal("Id", Assert.Single(plan.CriticalMismatches).Column);
            var key = Assert.Single(plan.TypeChanges);
            Assert.Equal("Id", key.Column);
            Assert.Equal(TypeChangeTier.Manual, key.Tier);
            Assert.Equal(TypeChangeStatus.Manual, key.Status);
            Assert.True(key.IsKey);
            Assert.Equal(3, key.Rows);
            Assert.Contains("1 converted key(s)", key.Message, StringComparison.Ordinal);
            Assert.Contains("'123' in 2 row(s)", key.Message, StringComparison.Ordinal);
            Assert.NotEmpty(key.ManualSteps);
            Assert.DoesNotContain("Manual steps:", TypeChangeApplier.WithoutManualSteps(key), StringComparison.Ordinal);

            Assert.Equal(before, await SnapshotAsync(cs, trg, flowId));

            // A run stops where the plan said it would, before any change.
            var run = await runner.RunAsync(Flow(flowId, src, trg, convertTypes: true, allowTableRewrite: true));
            Assert.False(run.Success);
            Assert.Contains("blocked", run.Error, StringComparison.Ordinal);
            Assert.Equal("varchar(20)", await IntegrationDb.ColumnTypeAsync(cs, trg, "Id"));
        }
        finally
        {
            await CleanupAsync(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task A_rewrite_an_earlier_run_left_unfinished_is_planned_to_finish_or_to_roll_back()
    {
        const int flowId = 78;
        var cs = IntegrationDb.Require();
        const string src = "_SfPlan3_Src";
        const string trg = "_SfPlan3_Trg";
        await ResetAsync(cs, src, trg, flowId, "[Id] int NOT NULL, [Logged] varchar(30) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1, '2026-01-05'), (2, '2026-02-01'), (3, NULL);");
        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            Assert.True((await runner.RunAsync(Flow(flowId, src, trg, convertTypes: false))).Success);
            await IntegrationDb.ExecuteAsync(cs, $"ALTER TABLE [dbo].[{src}] ALTER COLUMN [Logged] date NULL;");

            // A run with the opt-in stops inside the conversion, at its verify step: the shadow column and its trigger stay.
            using (var cancel = new CancellationTokenSource())
            {
                var stopAtVerify = new CancelAtStep("retype.verify", cancel);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
                    Flow(flowId, src, trg, convertTypes: true), new IngestionRunOptions { StatementSink = stopAtVerify }, cancel.Token));
            }

            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM sys.triggers WHERE parent_id = OBJECT_ID('[dbo].[{trg}]')"));
            var before = await SnapshotAsync(cs, trg, flowId);

            var finish = await runner.PlanAsync(Flow(flowId, src, trg, convertTypes: true));
            var finished = Assert.Single(finish.TypeChanges);
            Assert.Equal("Logged", finished.Column);
            Assert.Equal(TypeChangeStatus.Planned, finished.Status);
            Assert.Contains("was left unfinished; a run finishes it first", finished.Message, StringComparison.Ordinal);
            Assert.Empty(finish.ExtraColumns);

            var rollBack = await runner.PlanAsync(Flow(flowId, src, trg, convertTypes: false));
            Assert.Equal(new[] { TypeChangeStatus.RolledBack, TypeChangeStatus.Pending }, rollBack.TypeChanges.Select(c => c.Status));
            Assert.Contains("a run rolls it back first because the flow no longer sets schema.convertTypes", rollBack.TypeChanges[0].Message, StringComparison.Ordinal);
            Assert.True(rollBack.NeedsAttention);

            Assert.Equal(before, await SnapshotAsync(cs, trg, flowId));

            // A run with the opt-in does what the plan said: it finishes the conversion.
            var run = await runner.RunAsync(Flow(flowId, src, trg, convertTypes: true));
            Assert.True(run.Success, run.Error);
            Assert.Equal(TypeChangeStatus.Resumed, Assert.Single(run.TypeChanges).Status);
            Assert.Equal("date", await IntegrationDb.ColumnTypeAsync(cs, trg, "Logged"));
        }
        finally
        {
            await CleanupAsync(cs, src, trg, flowId);
        }
    }

    /// <summary>A statement sink that cancels the run as it reaches one step, the way a run dies part way.</summary>
    private sealed class CancelAtStep(string step, CancellationTokenSource cancel) : IRunStatementSink
    {
        public void Report(SqlTraceEntry entry)
        {
            if (entry.Step == step)
            {
                cancel.Cancel();
            }
        }

        public void ReportFailure(int sequence, string errorMessage)
        {
        }
    }
}
