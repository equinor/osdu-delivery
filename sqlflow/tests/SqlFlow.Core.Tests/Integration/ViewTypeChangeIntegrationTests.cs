using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// How the typed transformation view of a file flow changes a column's type, against the physical sink: the view is
/// rebuilt on every run, a run that finds no new file included, so a declaration reaches it at once; a declared type
/// the landed values do not hold is refused and the column keeps its type; and inference never moves a column out of
/// the type the view already gives it (only a declaration does).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ViewTypeChangeIntegrationTests
{
    private static FlowDefinition Flow(string cs, string table, string location, TypeInferencePolicy inference, LoadMode mode = LoadMode.Append) => new()
    {
        Name = "it-view-type-" + table,
        Source = new SourceSpec { Type = "csv", Location = location },
        Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
        Inference = inference,
        Load = new LoadPolicy { Mode = mode },
    };

    private static Task<string?> ViewColumnTypeAsync(string cs, string view, string column)
        => IntegrationDb.ScalarAsync<string>(cs,
            "SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS " +
            $"WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = '{view}' AND COLUMN_NAME = '{column}';");

    private static async Task<(string Table, string View, DirectoryInfo Dir, DirectoryInfo Empty)> ArrangeAsync(string cs, string prefix)
    {
        var table = prefix + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[v_{table}];");
        return (table, "v_" + table, Directory.CreateTempSubdirectory("sqlflow-vtype-"), Directory.CreateTempSubdirectory("sqlflow-vtype-empty-"));
    }

    private static async Task CleanupAsync(string cs, string table, string view, params DirectoryInfo[] dirs)
    {
        await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [dbo].[{view}];");
        await IntegrationDb.DropTableAsync(cs, table);
        foreach (var dir in dirs)
        {
            dir.Delete(recursive: true);
        }
    }

    [SkippableFact]
    public async Task A_run_that_finds_no_new_file_still_rebuilds_the_view_so_a_declaration_reaches_it()
    {
        var cs = IntegrationDb.Require();
        var (table, view, dir, empty) = await ArrangeAsync(cs, "IT_VNoFile_");
        try
        {
            var csv = Path.Combine(dir.FullName, "logs.csv");
            await File.WriteAllTextAsync(csv, "id,date_logged\n1,NA\n2,NA\n");
            var landed = await IntegrationDb.RealRunner().RunAsync(Flow(cs, table, csv, new TypeInferencePolicy { Enabled = true }));
            Assert.Equal(FlowStatus.Success, landed.Status);
            Assert.Equal("varchar", await ViewColumnTypeAsync(cs, view, "date_logged"));

            // Nothing new to land, and a column the next flow needs is declared: the view takes it on this run.
            var declared = new TypeInferencePolicy
            {
                Enabled = true,
                Columns =
                [
                    new ColumnTransform { Name = "date_logged", Expression = "CONVERT(date, NULLIF(@ColName, 'NA'), 23)", Type = "date" },
                    new ColumnTransform { Name = "date_logged_unreadable", Virtual = true, Expression = "CONVERT(bit, 0)", Type = "bit" },
                ],
            };
            var quiet = await IntegrationDb.RealRunner().RunAsync(Flow(cs, table, empty.FullName, declared));

            Assert.Equal(FlowStatus.Success, quiet.Status);
            Assert.Equal(0, quiet.RowsLoaded);
            Assert.NotNull(quiet.TransformView);
            Assert.Equal("date", await ViewColumnTypeAsync(cs, view, "date_logged"));
            Assert.Equal("bit", await ViewColumnTypeAsync(cs, view, "date_logged_unreadable"));
        }
        finally
        {
            await CleanupAsync(cs, table, view, dir, empty);
        }
    }

    [SkippableFact]
    public async Task A_declared_type_the_landed_values_do_not_hold_is_refused_and_the_view_keeps_its_type()
    {
        var cs = IntegrationDb.Require();
        var (table, view, dir, empty) = await ArrangeAsync(cs, "IT_VRefuse_");
        try
        {
            var csv = Path.Combine(dir.FullName, "logs.csv");
            await File.WriteAllTextAsync(csv, "id,date_logged,run_date\n1,NA,2026-01-01\n2,2026-02-03,2026-01-02\n3,NA,2026-01-03\n");
            var idOnly = new TypeInferencePolicy { Columns = [new ColumnTransform { Name = "id", Type = "int" }] };
            Assert.Equal(FlowStatus.Success, (await IntegrationDb.RealRunner().RunAsync(Flow(cs, table, csv, idOnly))).Status);
            Assert.Equal("varchar", await ViewColumnTypeAsync(cs, view, "date_logged"));

            var typed = new TypeInferencePolicy
            {
                Columns =
                [
                    new ColumnTransform { Name = "id", Type = "int" },
                    new ColumnTransform { Name = "date_logged", Type = "date" },
                    new ColumnTransform { Name = "run_date", Type = "date" },
                ],
            };
            var result = await IntegrationDb.RealRunner().RunAsync(Flow(cs, table, empty.FullName, typed));

            Assert.Equal(FlowStatus.Success, result.Status);
            var refused = Assert.Single(result.TypeChanges, c => c.Column == "date_logged");
            Assert.Equal(TypeChangeStatus.Refused, refused.Status);
            Assert.Equal(TypeChangeReport.ViewLayer, refused.Layer);
            Assert.Equal(2, refused.FailingValues);
            Assert.Equal("NA", Assert.Single(refused.Sample).Value);
            Assert.True(refused.NeedsAttention);
            var applied = Assert.Single(result.TypeChanges, c => c.Column == "run_date");
            Assert.Equal(TypeChangeStatus.Applied, applied.Status);

            Assert.Equal("varchar", await ViewColumnTypeAsync(cs, view, "date_logged"));
            Assert.Equal("date", await ViewColumnTypeAsync(cs, view, "run_date"));
        }
        finally
        {
            await CleanupAsync(cs, table, view, dir, empty);
        }
    }

    [SkippableFact]
    public async Task Inference_keeps_each_column_in_the_type_the_view_already_gives_it()
    {
        var cs = IntegrationDb.Require();
        var (table, view, dir, empty) = await ArrangeAsync(cs, "IT_VSticky_");
        try
        {
            var inference = new TypeInferencePolicy { Enabled = true };
            var first = Path.Combine(dir.FullName, "first.csv");
            await File.WriteAllTextAsync(first, "id,qty,code\n1,5,a\n2,7,b\n");
            Assert.Equal(FlowStatus.Success,
                (await IntegrationDb.RealRunner().RunAsync(Flow(cs, table, first, inference, LoadMode.TruncateLoad))).Status);
            var qtyType = await ViewColumnTypeAsync(cs, view, "qty");
            Assert.Contains(qtyType, new[] { "tinyint", "smallint", "int", "bigint" });
            Assert.Equal("varchar", await ViewColumnTypeAsync(cs, view, "code"));

            // The next export swaps the shapes: qty holds text and code holds numbers. Inference alone would re-type
            // both; the view keeps them as they were, so only a declaration can change them.
            var second = Path.Combine(dir.FullName, "second.csv");
            File.Delete(first);
            await File.WriteAllTextAsync(second, "id,qty,code\n1,x,1\n2,y,2\n");
            Assert.Equal(FlowStatus.Success,
                (await IntegrationDb.RealRunner().RunAsync(Flow(cs, table, second, inference, LoadMode.TruncateLoad))).Status);

            Assert.Equal(qtyType, await ViewColumnTypeAsync(cs, view, "qty"));
            Assert.Equal("varchar", await ViewColumnTypeAsync(cs, view, "code"));
        }
        finally
        {
            await CleanupAsync(cs, table, view, dir, empty);
        }
    }
}
