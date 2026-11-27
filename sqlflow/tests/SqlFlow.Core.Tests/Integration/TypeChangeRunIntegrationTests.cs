using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// A change to the type of a column the target already has, end to end through real ingestion runs against the sink:
/// the source declares a new type, a run without the opt-in reports it pending and loads into the old type, a run with
/// <c>schema.convertTypes</c> converts the column after the load (rows, identity and system dates kept), stored values
/// that do not convert refuse the change until a full-load run re-renders them from the source, and a load whose values
/// would change on the way into a column that kept its type stops before writing anything.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TypeChangeRunIntegrationTests
{
    private static IngestionFlow Flow(int flowId, string src, string trg, bool convertTypes, IncrementalPolicy? incremental = null)
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
            SchemaSync = new SchemaSyncPolicy { ConvertTypes = convertTypes },
            Incremental = incremental ?? new IncrementalPolicy(),
        };

    private static async Task ResetAsync(string cs, string src, string trg, int flowId, string sourceColumns)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ({sourceColumns});");
    }

    private static async Task CleanupAsync(string cs, string src, string trg, int flowId)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
    }

    private static Task<string?> FingerprintAsync(string cs, string table)
        => IntegrationDb.ScalarAsync<string?>(cs,
            $"SELECT STRING_AGG(CONCAT([RecId], ':', [Id], ':', CONVERT(varchar(30), [InsertedDate_DW], 126)), '|') WITHIN GROUP (ORDER BY [RecId]) FROM [dbo].[{table}]");

    [SkippableFact]
    public async Task A_declared_date_is_pending_without_the_opt_in_and_converted_after_the_load_with_it()
    {
        const int flowId = 71;
        var cs = IntegrationDb.Require();
        const string src = "_SfTypeRun1_Src";
        const string trg = "_SfTypeRun1_Trg";
        await ResetAsync(cs, src, trg, flowId, "[Id] int NOT NULL, [Logged] varchar(30) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1, '2026-01-05'), (2, NULL), (3, '2026-03-01');");
        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            Assert.True((await runner.RunAsync(Flow(flowId, src, trg, convertTypes: false))).Success);
            Assert.Equal("varchar(30)", await IntegrationDb.ColumnTypeAsync(cs, trg, "Logged"));
            var before = await FingerprintAsync(cs, trg);

            // The source now declares the column a date.
            await IntegrationDb.ExecuteAsync(cs, $"ALTER TABLE [dbo].[{src}] ALTER COLUMN [Logged] date NULL;");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (4, '2026-04-04');");

            var pending = await runner.RunAsync(Flow(flowId, src, trg, convertTypes: false));
            Assert.True(pending.Success, pending.Error);
            var report = Assert.Single(pending.TypeChanges);
            Assert.Equal(TypeChangeStatus.Pending, report.Status);
            Assert.True(report.NeedsAttention);
            Assert.Equal("varchar(30)", await IntegrationDb.ColumnTypeAsync(cs, trg, "Logged"));
            Assert.Equal(4, await IntegrationDb.RowCountAsync(cs, trg));

            var converted = await runner.RunAsync(Flow(flowId, src, trg, convertTypes: true));
            Assert.True(converted.Success, converted.Error);
            Assert.Equal(TypeChangeStatus.Applied, Assert.Single(converted.TypeChanges).Status);
            Assert.Equal("date", await IntegrationDb.ColumnTypeAsync(cs, trg, "Logged"));
            Assert.StartsWith(before!, await FingerprintAsync(cs, trg), StringComparison.Ordinal);

            // The next run finds nothing to change, and the upsert sees no row as changed.
            var settled = await runner.RunAsync(Flow(flowId, src, trg, convertTypes: true));
            Assert.True(settled.Success, settled.Error);
            Assert.Empty(settled.TypeChanges);
            Assert.Equal(0, settled.RowsUpdated);
        }
        finally
        {
            await CleanupAsync(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task Stored_values_that_do_not_convert_refuse_the_change_until_a_full_load_re_renders_them()
    {
        const int flowId = 72;
        var cs = IntegrationDb.Require();
        const string src = "_SfTypeRun2_Src";
        const string trg = "_SfTypeRun2_Trg";
        await ResetAsync(cs, src, trg, flowId, "[Id] int NOT NULL, [Logged] varchar(30) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1, 'NA'), (2, 'NA'), (3, '2026-03-01');");
        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var incremental = new IncrementalPolicy { Columns = ["Id"] };
            Assert.True((await runner.RunAsync(Flow(flowId, src, trg, convertTypes: true, incremental))).Success);

            // The source maps 'NA' to no value and declares a date; the target still holds the 'NA' it was given.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [Logged] = NULL WHERE [Logged] = 'NA';");
            await IntegrationDb.ExecuteAsync(cs, $"ALTER TABLE [dbo].[{src}] ALTER COLUMN [Logged] date NULL;");

            var refused = await runner.RunAsync(Flow(flowId, src, trg, convertTypes: true, incremental));
            Assert.True(refused.Success, refused.Error);
            var report = Assert.Single(refused.TypeChanges);
            Assert.Equal(TypeChangeStatus.Refused, report.Status);
            Assert.Equal(2, report.FailingValues);
            Assert.Equal("varchar(30)", await IntegrationDb.ColumnTypeAsync(cs, trg, "Logged"));

            var full = await runner.RunAsync(Flow(flowId, src, trg, convertTypes: true, incremental), new IngestionRunOptions
            {
                Parameters = new RunParameters { FullLoad = true },
            });
            Assert.True(full.Success, full.Error);
            Assert.Equal(TypeChangeStatus.Applied, Assert.Single(full.TypeChanges).Status);
            Assert.Equal("date", await IntegrationDb.ColumnTypeAsync(cs, trg, "Logged"));
            Assert.Equal(2, await IntegrationDb.ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Logged] IS NULL"));
        }
        finally
        {
            await CleanupAsync(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task A_load_whose_values_would_change_in_a_column_that_kept_its_type_stops_before_writing()
    {
        const int flowId = 73;
        var cs = IntegrationDb.Require();
        const string src = "_SfTypeRun3_Src";
        const string trg = "_SfTypeRun3_Trg";
        await ResetAsync(cs, src, trg, flowId, "[Id] int NOT NULL, [Amount] int NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1, 3);");
        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            Assert.True((await runner.RunAsync(Flow(flowId, src, trg, convertTypes: true))).Success);

            // decimal(10,2) holds fewer integer digits than int: a manual change, so the column keeps int, and 2.50
            // would be written as 2.
            await IntegrationDb.ExecuteAsync(cs, $"ALTER TABLE [dbo].[{src}] ALTER COLUMN [Amount] decimal(10,2) NULL;");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (2, 2.50);");

            var stopped = await runner.RunAsync(Flow(flowId, src, trg, convertTypes: true));
            Assert.False(stopped.Success);
            Assert.Contains("[Amount]: 1 staged value(s)", stopped.Error, StringComparison.Ordinal);
            Assert.Contains(stopped.TypeChanges, c => c.Column == "Amount" && c.Status == TypeChangeStatus.Manual);
            Assert.Equal(1, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal("int", await IntegrationDb.ColumnTypeAsync(cs, trg, "Amount"));
        }
        finally
        {
            await CleanupAsync(cs, src, trg, flowId);
        }
    }
}
