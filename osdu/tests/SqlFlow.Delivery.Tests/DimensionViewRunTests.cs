using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A dimension flow's views built end to end on SQL Server (osdu/docs/reference/flow/dimension-views.md): written
/// after the dimensions, joined exactly, converted without failing a read, checked with their counts and examples, left
/// alone when nothing changed, written again after a build renames a column they read, dropped when no longer declared,
/// refused where another flow or a person holds the name, failing the build on a value no expression could be written
/// around, and refused when <c>target.connection</c> reaches another database than the module's.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DimensionViewRunTests : IDisposable
{
    private const string WellLog = Samples.WellLogKind;

    private const string Unit = "osdu:wks:reference-data--UnitOfMeasure:1.0.0";

    /// <summary>The variable <c>target.connection</c> names, set to the test database's connection string for the suite's runs.</summary>
    private const string TargetVariable = "OSDU_VIEW_TESTS_DB";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly FakeDimensionPlatform _platform = new() { AggregationSize = 50 };

    public DimensionViewRunTests()
    {
        Environment.SetEnvironmentVariable(TargetVariable, _db.ConnectionString);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TargetVariable, null);
        _db.Dispose();
        _platform.Dispose();
    }

    private static string Flow(string views, string name = "wells-dimensions", string logValue = "LogName", string dimensions = "") => $$"""
        flowType: dimension
        name: {{name}}
        partitions: [dev]
        source:
          endpoint: http://localhost
          aggregationSize: 50
        target:
          connection: ${env:{{TargetVariable}}}
        reliability: { retry: { attempts: 1, baseDelayMs: 1, maxDelayMs: 1 } }
        parallelism: { recordsAtOnce: 2 }
        dimensions:
          - name: TestCurve
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            label: data.Name
            columns: { key: WellLogID, value: WellLogName }
            elements:
              path: data.Curves
              fields:
                Mnemonic: Mnemonic
                CurveUnitID: { path: CurveUnit, keep: id }
                TopDepth: TopDepth
                BaseDepth: BaseDepth
          - name: TestLog
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: id
            label: data.Name
            columns: { key: WellLogID, value: {{logValue}} }
            attributes:
              Created: data.CreationDateTime
          - name: TestUnit
            kind: "osdu:wks:reference-data--UnitOfMeasure:*"
            path: id
            label: data.Code
            columns: { key: UnitID, value: UnitCode }
        {{dimensions}}
        {{views}}
        """;

    private const string CurveView = """
        views:
          - name: TestCurveView
            from: TestCurve
            join:
              - { on: WellLogID, to: TestLog, as: Log }
              - { on: CurveUnitID, to: TestUnit, as: Unit }
            columns:
              Log: Log.LogName
              Curve: Mnemonic
              Unit: Unit.UnitCode
              TopDepth: { expression: TopDepth, dataType: float }
              Interval: { expression: "TRY_CAST(BaseDepth AS float) - TRY_CAST(TopDepth AS float)", dataType: "decimal(18,3)" }
              Created: { expression: Log.Created, dataType: datetime2(0) }
        """;

    /// <summary>
    /// Units whose ids differ only in case (m, the metre; M, as a partition may hold one), and well logs whose curves name
    /// them: one curve's depth is written with a comma, one names a unit the partition does not hold, one log holds none.
    /// </summary>
    private void Logs()
    {
        _platform.Add("dev:reference-data--UnitOfMeasure:m", Unit, new JsonObject { ["Code"] = "m" });
        _platform.Add("dev:reference-data--UnitOfMeasure:M", Unit, new JsonObject { ["Code"] = "M (not the metre)" });
        _platform.Add("dev:reference-data--UnitOfMeasure:gAPI", Unit, new JsonObject { ["Code"] = "gAPI" });
        JsonObject Curve(string mnemonic, string unit, JsonNode? top, JsonNode? bottom)
            => new() { ["Mnemonic"] = mnemonic, ["CurveUnit"] = $"dev:reference-data--UnitOfMeasure:{unit}:", ["TopDepth"] = top, ["BaseDepth"] = bottom };
        _platform.Add("dev:work-product-component--WellLog:1", WellLog, new JsonObject
        {
            ["Name"] = "LOG-1",
            ["CreationDateTime"] = "2013-03-22T11:16:03+02:00",
            ["Curves"] = new JsonArray(Curve("MD", "m", 203.1, 210.5), Curve("GR", "gAPI", "12,5", 300)),
        });
        _platform.Add("dev:work-product-component--WellLog:2", WellLog, new JsonObject
        {
            ["Name"] = "LOG-2", ["Curves"] = new JsonArray(Curve("MD", "xx", 1, 2)),
        });
        _platform.Add("dev:work-product-component--WellLog:3", WellLog, new JsonObject { ["Name"] = "LOG-3" });
    }

    private async Task<(DimensionRunner Runner, OsduLedger Ledger, DimensionFlowDefinition Flow)> RunnerAsync(string yaml)
    {
        var ledger = _db.Ledger(_clock);
        var engine = Samples.Engine(ledger, _clock);
        var flow = new DeliveryDocumentLoader().ParseDimension(yaml, "flows/dims.yaml").ForPartition("dev");
        return (new DimensionRunner(engine, flow, new Dictionary<string, string>(), Samples.Logger<DimensionRunner>(), _platform, allowLoopback: true), ledger, flow);
    }

    [Fact]
    public async Task A_grouped_view_is_a_row_per_key_and_a_view_joining_it_meets_one_row_of_each()
    {
        Logs();
        var (runner, _, _) = await RunnerAsync(Flow("""
            views:
              - name: TestCurveCounts
                from: TestCurve
                where: element IS NOT NULL
                groupBy: WellLogID
                columns:
                  WellLogID: WellLogID
                  Curves: COUNT(*)
                  Mnemonics: STRING_AGG(Mnemonic, ',') WITHIN GROUP (ORDER BY element)
                  Greatest: MAX(Mnemonic)
              - name: TestLogCounts
                from: TestLog
                join:
                  - { on: WellLogID, view: TestCurveCounts, as: Counts }
                columns:
                  Log: LogName
                  Curves: Counts.Curves
                  Mnemonics: Counts.Mnemonics
                  Greatest: Counts.Greatest
            """));

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal(
            [("TestCurveCounts", DimensionViewWriteStatus.Written, DimensionViewCheckStatus.Passed, (long?)2L), ("TestLogCounts", DimensionViewWriteStatus.Written, DimensionViewCheckStatus.Passed, (long?)3L)],
            outcome.Views.Select(v => (v.View, v.Status, v.Check, v.Rows)));
        Assert.Equal(
            ["LOG-1|2|MD,GR|MD", "LOG-2|1|MD|MD", "LOG-3|(null)|(null)|(null)"],
            await SqlAsync("SELECT [Log], [Curves], [Mnemonics], [Greatest] FROM [osdu].[v_dim_TestLogCounts] ORDER BY [Log];"));
    }

    [Fact]
    public async Task A_view_joins_its_dimensions_exactly_converts_without_failing_and_is_checked()
    {
        Logs();
        var (runner, ledger, _) = await RunnerAsync(Flow(CurveView));

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((3, 0), (outcome.Built, outcome.Failed));
        var view = Assert.Single(outcome.Views);
        Assert.Equal((DimensionViewWriteStatus.Written, DimensionViewCheckStatus.Passed, (long?)4L), (view.Status, view.Check, view.Rows));

        // The curve of unit m meets the metre alone, though the database's collation would equate m and M; a unit the
        // partition does not hold, and a log of no curve, leave their joined columns null; every row of the from table
        // is one row of the view.
        Assert.Equal(
            [
                "LOG-1|GR|gAPI|(null)|(null)|03/22/2013 09:16:03",
                "LOG-1|MD|m|203.1|7.400|03/22/2013 09:16:03",
                "LOG-2|MD|(null)|1|1.000|(null)",
                "LOG-3|(null)|(null)|(null)|(null)|(null)",
            ],
            await SqlAsync("SELECT [Log], [Curve], [Unit], [TopDepth], [Interval], [Created] FROM [osdu].[v_dim_TestCurveView] ORDER BY [Log], [Curve];"));
        Assert.Equal(
            ["partition", "id", "Log", "Curve", "Unit", "TopDepth", "Interval", "Created"],
            await SqlAsync("SELECT c.[name] FROM sys.columns AS c WHERE c.[object_id] = OBJECT_ID(N'[osdu].[v_dim_TestCurveView]') ORDER BY c.[column_id];"));
        Assert.Equal(["float|decimal|datetime2"], await SqlAsync("""
            SELECT STRING_AGG(t.[name], '|') WITHIN GROUP (ORDER BY c.[column_id])
            FROM sys.columns AS c JOIN sys.types AS t ON t.[user_type_id] = c.[user_type_id]
            WHERE c.[object_id] = OBJECT_ID(N'[osdu].[v_dim_TestCurveView]') AND c.[name] IN (N'TopDepth', N'Interval', N'Created');
            """));

        // The check counts what each join found and each conversion could not read, with examples, in the run's notes.
        Assert.Contains("CurveUnitID: 1 of 4 rows name no row of TestUnit, for example 'dev:reference-data--UnitOfMeasure:xx'.", view.Notes);
        Assert.Contains("TopDepth: 1 value(s) did not convert, for example '12,5'", string.Join(" ", view.Notes), StringComparison.Ordinal);
        var detail = (await ledger.GetDimensionViewAsync("testcurveview"))!;
        Assert.True(detail.View.Written);
        Assert.Equal("wells-dimensions", detail.View.FlowName);
        Assert.Equal(["dim_TestCurve", "dim_TestLog", "dim_TestUnit"], detail.View.Tables);
        var check = Assert.Single(detail.Checks);
        Assert.Equal(("dev", 4L), (check.Partition, check.Rows));
        Assert.Equal([("Log", 4L, 0L), ("Unit", 2L, 1L)], check.Joins.Select(j => (j.Alias, j.Matched, j.Unmatched)));
        Assert.Equal((long?)1L, check.Columns.Single(c => c.Name == "TopDepth").Unconverted);
        Assert.Equal((long?)0L, check.Columns.Single(c => c.Name == "Interval").Unconverted);

        // The joined table is read through the index on its key's hash.
        Assert.Equal(["1"], await SqlAsync("SELECT COUNT(*) FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[osdu].[dim_TestUnit]') AND [name] = N'IX_key_hash';"));
    }

    [Fact]
    public async Task A_view_whose_tables_hold_what_they_held_at_its_last_check_keeps_that_check_and_one_whose_table_changed_is_read_again()
    {
        Logs();
        var (runner, ledger, _) = await RunnerAsync(Flow(CurveView));
        var first = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var read = (await ledger.GetDimensionViewAsync("TestCurveView"))!.Checks[0];
        Assert.Null(read.ReusedFrom);

        // The same records built again: every table the view reads holds what it held, so its check is kept, findings and all.
        var again = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal((DimensionViewWriteStatus.Unchanged, DimensionViewCheckStatus.Passed, (long?)4L), (again.Views[0].Status, again.Views[0].Check, again.Views[0].Rows));
        Assert.Equal(first.Views[0].Notes, again.Views[0].Notes);
        var kept = (await ledger.GetDimensionViewAsync("TestCurveView"))!.Checks;
        Assert.Equal((read.CheckId, read.Rows), (kept[0].ReusedFrom, kept[0].Rows));
        Assert.Equal(read.Joins.Select(j => (j.Alias, j.Matched, j.Unmatched)), kept[0].Joins.Select(j => (j.Alias, j.Matched, j.Unmatched)));

        // Built a third time, the check names the one that read the view, not the one that kept it.
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(read.CheckId, (await ledger.GetDimensionViewAsync("TestCurveView"))!.Checks[0].ReusedFrom);

        // A unit's code changed: its table's rows change, so the view is read again and finds the new code.
        _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:reference-data--UnitOfMeasure:gAPI")["data"]!["Code"] = "API";
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Null((await ledger.GetDimensionViewAsync("TestCurveView"))!.Checks[0].ReusedFrom);
        Assert.Equal(["API", "m"], await SqlAsync("SELECT DISTINCT [Unit] FROM [osdu].[v_dim_TestCurveView] WHERE [Unit] IS NOT NULL ORDER BY [Unit];"));

        // A view written anew reads its rows, though its tables hold what they did.
        var (renaming, renamedLedger, _) = await RunnerAsync(Flow(CurveView.Replace("Log.LogName", "Log.LogTitle", StringComparison.Ordinal), logValue: "LogTitle"));
        await renaming.BuildAsync(["TestLog"], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Null((await renamedLedger.GetDimensionViewAsync("TestCurveView"))!.Checks[0].ReusedFrom);
    }

    [Fact]
    public async Task An_incremental_load_that_reads_a_key_again_and_finds_it_the_same_keeps_its_table_and_the_view_s_check()
    {
        Logs();
        foreach (var record in _platform.Records)
        {
            record["createTime"] = OsduSearch.LuceneTime(_clock.GetUtcNow().UtcDateTime.AddHours(-2));
        }

        var (runner, ledger, _) = await RunnerAsync(Flow(CurveView).Replace("dimensions:\n", "incremental:\n  keyColumns: [id]\ndimensions:\n", StringComparison.Ordinal));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var read = (await ledger.GetDimensionViewAsync("TestCurveView"))!.Checks[0];
        var rows = await SqlAsync("SELECT CONCAT([id], '|', [UnitID], '|', [UnitCode]) FROM [osdu].[dim_TestUnit] ORDER BY [id];");

        // A unit saved again as it was: the next build reads its key again and finds what its table holds.
        var unit = _platform.Records.Single(r => r["id"]!.GetValue<string>() == "dev:reference-data--UnitOfMeasure:gAPI");
        unit["modifyTime"] = OsduSearch.LuceneTime(_clock.GetUtcNow().UtcDateTime.AddMinutes(30));
        _clock.Advance(TimeSpan.FromHours(1));
        var again = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        var units = again.Dimensions.Single(d => d.Dimension == "TestUnit");
        Assert.Equal((DimensionRunModes.Incremental, (long?)1L), (units.Load, units.TouchedKeys));
        Assert.Equal(rows, await SqlAsync("SELECT CONCAT([id], '|', [UnitID], '|', [UnitCode]) FROM [osdu].[dim_TestUnit] ORDER BY [id];"));
        Assert.Equal(read.CheckId, (await ledger.GetDimensionViewAsync("TestCurveView"))!.Checks[0].ReusedFrom);

        // The unit's code changed in the next window: its row alone is rewritten, and the view is read again.
        unit["data"]!["Code"] = "API";
        unit["modifyTime"] = OsduSearch.LuceneTime(_clock.GetUtcNow().UtcDateTime.AddMinutes(30));
        _clock.Advance(TimeSpan.FromHours(1));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(
            rows.Select(r => r.EndsWith("|gAPI", StringComparison.Ordinal) ? r[..^4] + "API" : r),
            await SqlAsync("SELECT CONCAT([id], '|', [UnitID], '|', [UnitCode]) FROM [osdu].[dim_TestUnit] ORDER BY [id];"));
        Assert.Null((await ledger.GetDimensionViewAsync("TestCurveView"))!.Checks[0].ReusedFrom);
    }

    [Fact]
    public async Task A_view_is_written_again_only_when_its_document_changed_and_after_a_build_renames_a_column_it_reads()
    {
        Logs();
        var (runner, _, _) = await RunnerAsync(Flow(CurveView));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        var first = await SqlAsync("SELECT OBJECT_ID(N'[osdu].[v_dim_TestCurveView]');");

        // The same document: the view is not written again, and still checked.
        var again = await runner.BuildAsync(["TestLog"], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal((DimensionViewWriteStatus.Unchanged, DimensionViewCheckStatus.Passed), (again.Views[0].Status, again.Views[0].Check));
        Assert.Equal(first, await SqlAsync("SELECT OBJECT_ID(N'[osdu].[v_dim_TestCurveView]');"));

        // The log's value column renamed by its document: the build that renames it drops the view in the same
        // transaction, so the view never reads the column under its old name, and the run's view step writes it again.
        var (renaming, ledger, _) = await RunnerAsync(Flow(CurveView.Replace("Log.LogName", "Log.LogTitle", StringComparison.Ordinal), logValue: "LogTitle"));
        var renamed = await renaming.BuildAsync(["TestLog"], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal((DimensionViewWriteStatus.Written, DimensionViewCheckStatus.Passed), (renamed.Views[0].Status, renamed.Views[0].Check));
        Assert.NotEqual(first, await SqlAsync("SELECT OBJECT_ID(N'[osdu].[v_dim_TestCurveView]');"));
        Assert.Equal(["LOG-1", "LOG-1", "LOG-2", "LOG-3"], await SqlAsync("SELECT [Log] FROM [osdu].[v_dim_TestCurveView] ORDER BY [Log];"));
        Assert.Null((await ledger.GetDimensionViewAsync("TestCurveView"))!.View.Note);
    }

    [Fact]
    public async Task A_view_its_flow_no_longer_declares_is_dropped_and_a_dimension_a_view_reads_is_not_removed()
    {
        Logs();
        var (runner, ledger, flow) = await RunnerAsync(Flow(CurveView));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        // A dimension a view reads is not removed: the view would read a table that is gone.
        var unit = (await ledger.FindDimensionAsync(flow.LedgerId, "TestUnit"))!;
        var refused = await Assert.ThrowsAsync<DeliveryException>(() => DimensionRemoval.RemoveAsync(ledger, unit, "user:admin", _clock, CancellationToken.None));
        Assert.Contains("is read by view TestCurveView, so it is not removed", refused.Message, StringComparison.Ordinal);

        var (without, _, _) = await RunnerAsync(Flow(string.Empty));
        var outcome = await without.BuildAsync(["TestUnit"], Guid.NewGuid(), "tests", CancellationToken.None);
        Assert.Equal(["TestCurveView"], outcome.ViewsDropped);
        Assert.Equal(["(null)"], await SqlAsync("SELECT OBJECT_ID(N'[osdu].[v_dim_TestCurveView]');"));
        Assert.Empty(await ledger.ListDimensionViewsAsync(null));
        Assert.Empty(await SqlAsync("SELECT [CheckId] FROM [osdu].[DimensionViewCheck];"));
    }

    [Fact]
    public async Task A_view_name_another_flow_holds_or_an_object_no_build_made_is_refused_and_written_over_by_none()
    {
        Logs();
        var (runner, _, _) = await RunnerAsync(Flow(CurveView));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        // Another flow declaring a view of the same name: its build fails, and the first flow's view is as it was.
        var other = Flow(
            "views:\n  - name: testcurveview\n    from: OtherUnit",
            name: "other-dimensions",
            dimensions: "  - name: OtherUnit\n    kind: \"osdu:wks:reference-data--UnitOfMeasure:*\"\n    path: id\n    columns: { key: OtherUnitID, value: OtherUnitCode }\n    label: data.Code")
            .Replace("- name: TestCurve\n", "- name: OtherCurve\n", StringComparison.Ordinal)
            .Replace("- name: TestLog\n", "- name: OtherLog\n", StringComparison.Ordinal)
            .Replace("- name: TestUnit\n", "- name: OtherUnit2\n", StringComparison.Ordinal);
        var (second, _, _) = await RunnerAsync(other);
        var failed = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => second.BuildAsync(["OtherUnit"], Guid.NewGuid(), "tests", CancellationToken.None));
        var view = Assert.Single(failed.Outcome.Views);
        Assert.Equal(DimensionViewWriteStatus.Failed, view.Status);
        Assert.Contains("is declared by dimension flow 'wells-dimensions' as well", view.Error, StringComparison.Ordinal);
        Assert.Equal(4, (await SqlAsync("SELECT [Log] FROM [osdu].[v_dim_TestCurveView];")).Count);

        // A view a person made in the schema is not written over.
        await SqlAsync("CREATE VIEW [osdu].[v_dim_Hand] AS SELECT 1 AS [x];");
        var (hand, _, _) = await RunnerAsync(Flow("views:\n  - name: Hand\n    from: TestUnit"));
        var handFailed = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => hand.BuildAsync(["TestUnit"], Guid.NewGuid(), "tests", CancellationToken.None));
        Assert.Contains("osdu.v_dim_Hand is in the database, and no build of a dimension flow made it", handFailed.Outcome.Views.Single(v => v.View == "Hand").Error, StringComparison.Ordinal);
        Assert.Equal(["1"], await SqlAsync("SELECT [x] FROM [osdu].[v_dim_Hand];"));
    }

    [Fact]
    public async Task A_value_no_expression_could_be_written_around_fails_the_build_naming_the_column()
    {
        Logs();
        var (runner, _, _) = await RunnerAsync(Flow(CurveView.Replace(
            "      Created: { expression: Log.Created, dataType: datetime2(0) }",
            "      Created: { expression: Log.Created, dataType: datetime2(0) }\n      Huge: TRY_CAST(BaseDepth AS int) * 2147483647",
            StringComparison.Ordinal)));

        var failed = await Assert.ThrowsAsync<DimensionBuildsFailedException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));

        // The dimensions are built; the view is written and keeps its definition; its check names the column and the run fails.
        Assert.Equal((3, 0), (failed.Outcome.Built, failed.Outcome.Failed));
        var view = Assert.Single(failed.Outcome.Views);
        Assert.Equal((DimensionViewWriteStatus.Written, DimensionViewCheckStatus.Failed), (view.Status, view.Check));
        Assert.Contains("Column Huge of view TestCurveView could not be computed for every row of partition 'dev'", view.Error, StringComparison.Ordinal);
        Assert.Contains("overflow", view.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0 view(s) written and checked, 1 failed", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rows_written_before_tables_kept_their_key_s_hash_are_given_it_and_joined()
    {
        Logs();
        var (runner, _, _) = await RunnerAsync(Flow(CurveView));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);
        await SqlAsync("UPDATE [osdu].[dim_TestUnit] SET [key_hash] = NULL;");
        Assert.Equal(["(null)", "(null)", "(null)", "(null)"], await SqlAsync("SELECT [Unit] FROM [osdu].[v_dim_TestCurveView] ORDER BY [Unit];"));

        // A build that does not rebuild the unit's dimension still gives its rows their hash before the view is checked.
        await runner.BuildAsync(["TestLog"], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal(["(null)", "(null)", "gAPI", "m"], await SqlAsync("SELECT [Unit] FROM [osdu].[v_dim_TestCurveView] ORDER BY [Unit];"));
    }

    [Fact]
    public async Task A_view_a_build_wrote_under_the_former_prefix_moves_to_its_name_now()
    {
        Logs();
        var (runner, ledger, _) = await RunnerAsync(Flow(CurveView));
        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        // As a build before views were named v_dim_ left it: the view and its record under dimv_.
        await SqlAsync("""
            EXEC sp_rename N'[osdu].[v_dim_TestCurveView]', N'dimv_TestCurveView';
            UPDATE [osdu].[DimensionView] SET [ViewName] = N'dimv_TestCurveView', [SqlHash] = N'former';
            """);

        var outcome = await runner.BuildAsync(["TestLog"], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal((DimensionViewWriteStatus.Written, DimensionViewCheckStatus.Passed), (outcome.Views[0].Status, outcome.Views[0].Check));
        Assert.Equal(["(null)"], await SqlAsync("SELECT OBJECT_ID(N'[osdu].[dimv_TestCurveView]');"));
        Assert.Equal(4, (await SqlAsync("SELECT [Log] FROM [osdu].[v_dim_TestCurveView];")).Count);
        Assert.Equal("v_dim_TestCurveView", (await ledger.GetDimensionViewAsync("TestCurveView"))!.View.ViewName);
    }

    [Fact]
    public async Task A_view_keeps_the_rows_its_where_holds_for_and_a_field_keeps_a_long_value_whole()
    {
        Logs();
        var view = CurveView.ReplaceLineEndings("\n").Replace("  - name: TestCurveView\n", "  - name: TestCurveView\n    where: element IS NOT NULL\n", StringComparison.Ordinal);
        var (runner, ledger, _) = await RunnerAsync(Flow(view));

        var outcome = await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        // LOG-3 holds no curve: its table keeps the key's one row with no element, and the view leaves it out.
        Assert.Equal(["LOG-1", "LOG-1", "LOG-2", "LOG-3"], await SqlAsync("SELECT [WellLogName] FROM [osdu].[dim_TestCurve] ORDER BY [WellLogName];"));
        Assert.Equal(["LOG-1", "LOG-1", "LOG-2"], await SqlAsync("SELECT [Log] FROM [osdu].[v_dim_TestCurveView] ORDER BY [Log];"));
        var written = Assert.Single(outcome.Views);
        Assert.Equal((DimensionViewWriteStatus.Written, DimensionViewCheckStatus.Passed, (long?)3L), (written.Status, written.Check, written.Rows));
        Assert.Equal("element IS NOT NULL", (await ledger.GetDimensionViewAsync("TestCurveView"))!.View.Where);

        // A table made when a field kept 256 characters is widened by the next build, and a field keeps a thousand
        // characters whole, in the ledger, the table and the view.
        await SqlAsync("ALTER TABLE [osdu].[dim_TestCurve] ALTER COLUMN [Mnemonic] nvarchar(256) NULL;");
        var description = new string('d', 1000);
        _platform.Add("dev:work-product-component--WellLog:4", WellLog, new JsonObject
        {
            ["Name"] = "LOG-4",
            ["Curves"] = new JsonArray(new JsonObject { ["Mnemonic"] = description, ["CurveUnit"] = "dev:reference-data--UnitOfMeasure:m:" }),
        });

        await runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None);

        Assert.Equal(["8000"], await SqlAsync("SELECT [max_length] FROM sys.columns WHERE [object_id] = OBJECT_ID(N'[osdu].[dim_TestCurve]') AND [name] = N'Mnemonic';"));
        Assert.Equal(["1000"], await SqlAsync("SELECT MAX(LEN([Value])) FROM [osdu].[DimensionElement];"));
        Assert.Equal(["1000"], await SqlAsync("SELECT LEN([Mnemonic]) FROM [osdu].[dim_TestCurve] WHERE [WellLogName] = N'LOG-4';"));
        Assert.Equal(["1000"], await SqlAsync("SELECT LEN([Curve]) FROM [osdu].[v_dim_TestCurveView] WHERE [Log] = N'LOG-4';"));
    }

    [Fact]
    public async Task A_target_connection_that_reaches_another_database_than_the_module_s_is_refused_before_anything_is_built()
    {
        Logs();
        var other = new SqlConnectionStringBuilder(_db.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        Environment.SetEnvironmentVariable(TargetVariable, other);
        var (runner, ledger, flow) = await RunnerAsync(Flow(CurveView));

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => runner.BuildAsync([], Guid.NewGuid(), "tests", CancellationToken.None));

        Assert.Contains($"target.connection ${{env:{TargetVariable}}} reaches another database than this host's module database", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("master", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await ledger.FindDimensionAsync(flow.LedgerId, "TestCurve"));

        // The plan says so too, and gives the statement a build would write.
        var plan = await runner.PlanAsync([], CancellationToken.None);
        var view = Assert.Single(plan.Views);
        Assert.StartsWith("CREATE OR ALTER VIEW [osdu].[v_dim_TestCurveView]", view.Sql, StringComparison.Ordinal);
        Assert.Contains(view.Problems, p => p.Contains("reaches another database", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plan_says_what_a_build_will_do_with_each_view()
    {
        Logs();
        var (runner, _, _) = await RunnerAsync(Flow(CurveView));

        var plan = await runner.PlanAsync([], CancellationToken.None);

        var view = Assert.Single(plan.Views);
        Assert.Empty(view.Problems);
        Assert.Equal(["dim_TestCurve", "dim_TestLog", "dim_TestUnit"], view.Tables);
        Assert.Contains("A build writes osdu.v_dim_TestCurveView.", view.Notes);
        Assert.Contains(view.Notes, n => n.StartsWith("osdu.dim_TestUnit is not in the database yet", StringComparison.Ordinal));
        Assert.Equal(["(null)"], await SqlAsync("SELECT OBJECT_ID(N'[osdu].[v_dim_TestCurveView]');"));
    }

    private async Task<List<string>> SqlAsync(string sql)
    {
        await using var connection = new SqlConnection(_db.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "(null)" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        }

        return rows;
    }
}
