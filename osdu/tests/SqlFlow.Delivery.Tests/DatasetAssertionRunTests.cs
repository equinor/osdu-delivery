using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A run of a mapping with rules on the rows it renders from (osdu/docs/reference/flow/mapping-dataset-assertions.md): a
/// plan over the sample well database logs holds the logs whose rows break a rule, judged per record as each renders or
/// once a run over every row of the scope, and counts them as it counts the property assertions'. No ledger, nothing written.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DatasetAssertionRunTests : IDisposable
{
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private static int LogCount => SampleEstate.Logs().Count;

    /// <summary>The sample flow over a copy of its mapping stating <paramref name="rules"/> under <c>dataset.assert</c>.</summary>
    private async Task<(EngineContext Engine, FlowDefinition Flow, MemoryIngestionTables Tables)> EstateAsync(string rules)
    {
        var mappings = Path.Combine(_root, "asserting-mappings");
        Directory.CreateDirectory(mappings);
        var sample = (await File.ReadAllTextAsync(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"))).ReplaceLineEndings("\n");
        const string Identity = "  identity: [wellbore_uwi, log_id]\n";
        Assert.Contains(Identity, sample, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            Path.Combine(mappings, "WellLog@1.4.0.yaml"),
            sample.Replace(Identity, Identity + "  assert:\n" + TestSchema.Indented(rules, 4), StringComparison.Ordinal));

        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var engine = Samples.Engine(ledger: null, _clock, sources: tables, searches: FixedRecordSearchFactory.SampleWellbores());
        var flow = Samples.LocalFlow(_root);
        return (engine, flow with { Render = flow.Render with { MappingsDirectory = mappings } }, tables);
    }

    private static async Task<(PlanSummary Summary, List<PlanEntry> Entries)> PlanAsync(EngineContext engine, FlowDefinition flow)
    {
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        var header = await runtime.Planner.OpenAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, SourceSelection.Full(), gate: false, stored: null);
        var summary = new PlanSummary();
        var entries = new List<PlanEntry>();
        await foreach (var entry in runtime.Planner.EntriesAsync(header, null, 1, summary, CancellationToken.None))
        {
            entries.Add(entry);
        }

        return (summary, entries);
    }

    [Fact]
    public async Task A_rule_on_each_logs_curve_rows_holds_the_logs_that_break_it_and_reads_no_run_wide_rows()
    {
        var (engine, flow, tables) = await EstateAsync("""
            - name: many-curves
              rows: curves
              count: { atLeast: 1000 }
            - name: curve-has-unit
              rows: curves
              column: curve_unit
              exists: true
              onFail: report
            """);

        var (summary, entries) = await PlanAsync(engine, flow);

        Assert.Equal((LogCount, LogCount, LogCount), (summary.Deliveries, summary.FailingAssertions, summary.HeldByAssertions));
        Assert.All(entries, entry =>
        {
            var findings = entry.Render!.Assertions!;
            Assert.True(findings.Holds);
            var counted = Assert.Single(findings.Failures, f => f.Assertion == "many-curves");
            Assert.Equal(("dataset.curves", "incoming"), (counted.At, counted.Stage));
        });
        Assert.Equal(0, tables.RunJudgements);
    }

    [Fact]
    public async Task A_rule_over_the_runs_rows_is_judged_once_and_holds_the_logs_whose_rows_share_values()
    {
        var (engine, flow, tables) = await EstateAsync("""
            - name: one-log-per-wellbore
              per: run
              unique: [wellbore_uwi]
            - name: scope-not-empty
              per: run
              count: { atLeast: 1 }
            """);
        // Each sample log has a wellbore of its own; the second log's row is given the first's.
        Assert.Equal(LogCount, tables.Records.Select(r => r.Row["wellbore_uwi"]).Distinct().Count());
        var uwi = (string)tables.Records[0].Row["wellbore_uwi"]!;
        tables.Records[1].Row["wellbore_uwi"] = uwi;

        var (summary, entries) = await PlanAsync(engine, flow);

        Assert.Equal(1, tables.RunJudgements);
        Assert.Equal((LogCount, 2, 2), (summary.Deliveries, summary.FailingAssertions, summary.HeldByAssertions));
        Assert.All(entries, entry => Assert.Equal(2, entry.Render!.Assertions!.Checked));
        var held = entries.Where(e => e.Render!.Assertions!.Holds).ToList();
        Assert.Equal(
            new[] { SampleEstate.Key(0).Value, SampleEstate.Key(1).Value }.Order(),
            held.Select(e => e.Key!.Value.Value).Order());
        Assert.All(held, entry =>
        {
            var failure = Assert.Single(entry.Render!.Assertions!.Failures);
            Assert.Equal(("dataset", "one-log-per-wellbore", $"wellbore_uwi '{uwi}'"), (failure.At, failure.Assertion, failure.Value));
        });
    }

    [Fact]
    public async Task Check_values_of_every_attribute_counts_the_rows_a_rule_on_the_rows_fails()
    {
        var (engine, flow, tables) = await EstateAsync("""
            - name: one-log-per-wellbore
              per: run
              unique: [wellbore_uwi]
            - name: curve-unit-known
              rows: curves
              column: curve_unit
              notEquals: UNKNOWN
            """);
        tables.Records[1].Row["wellbore_uwi"] = tables.Records[0].Row["wellbore_uwi"];
        tables.Records[2].Datasets["curves"][0]["curve_unit"] = "UNKNOWN";
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);

        var whole = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest());
        Assert.Equal((3L, 3L), (whole.Rows.WithFailedAssertion, whole.Rows.HeldByAssertion));

        // A check of some attributes judges their assertions alone: a rule on the rows is about none of them.
        var one = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { Targets = ["osdu.data.Name"] });
        Assert.Equal((0L, 0L), (one.Rows.WithFailedAssertion, one.Rows.HeldByAssertion));
    }

    [Fact]
    public async Task A_count_over_the_runs_rows_that_fails_holds_every_log_of_the_run()
    {
        var (engine, flow, _) = await EstateAsync("""
            - per: run
              count: { atMost: 1 }
            """);

        var (summary, entries) = await PlanAsync(engine, flow);

        Assert.Equal((LogCount, LogCount), (summary.FailingAssertions, summary.HeldByAssertions));
        Assert.All(entries, entry => Assert.Equal(
            $"the run's scope holds {LogCount} rows of the record table, and the rule asks for count atMost 1",
            Assert.Single(entry.Render!.Assertions!.Failures).Message));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory a reader still holds open is left for the operating system to reclaim.
        }

        GC.SuppressFinalize(this);
    }
}
