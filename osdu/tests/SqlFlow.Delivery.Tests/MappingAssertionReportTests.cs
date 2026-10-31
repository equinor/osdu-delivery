using SqlFlow.Delivery.Cli;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Engine.Preview;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Where what a mapping's assertions find is shown before anything is sent (osdu/docs/reference/flow/mapping-assertions.md):
/// a plan counts the deliveries that fail one and says which would be held, Check values counts the rows that fail each
/// assertion beside the template's rules, a preview lists what the record fails, and the CLI says each of them. Over the
/// sample well database logs, with the sample mapping given assertions; no ledger, nothing written.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MappingAssertionReportTests : IDisposable
{
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private static int LogCount => SampleEstate.Logs().Count;

    /// <summary>The sample mapping's <c>data.Name</c> node, which the tests give assertions by default.</summary>
    private const string NameNode = "    Name:\n      $from: log_source\n      $modifiers: [trim]\n";

    /// <summary>
    /// The sample flow over a copy of its mapping whose <paramref name="node"/>, as the sample writes it, is written as
    /// <paramref name="block"/> (itself when null) and states <paramref name="assertion"/>.
    /// </summary>
    private async Task<(EngineContext Engine, FlowDefinition Flow)> EstateAsync(string assertion, string node = NameNode, string? block = null)
    {
        var mappings = Path.Combine(_root, "asserting-mappings");
        Directory.CreateDirectory(mappings);
        var sample = (await File.ReadAllTextAsync(Path.Combine(Samples.Mappings, "WellLog@1.4.0.yaml"))).ReplaceLineEndings("\n");
        Assert.Contains(node, sample, StringComparison.Ordinal);
        block ??= node;
        var indent = new string(' ', block.TakeWhile(c => c == ' ').Count() + 2);
        var asserted = block + indent + "$assert:\n"
            + string.Concat(assertion.ReplaceLineEndings("\n").Split('\n').Where(l => l.Length > 0).Select(l => indent + "  " + l + "\n"));
        await File.WriteAllTextAsync(Path.Combine(mappings, "WellLog@1.4.0.yaml"), sample.Replace(node, asserted, StringComparison.Ordinal));

        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var engine = Samples.Engine(ledger: null, _clock, sources: tables, searches: FixedRecordSearchFactory.SampleWellbores());
        var flow = Samples.LocalFlow(_root);
        return (engine, flow with { Render = flow.Render with { MappingsDirectory = mappings } });
    }

    private const string Holding = """
        - equals: no log is named this
          name: log-name-known
        """;

    [Fact]
    public async Task A_plan_counts_the_deliveries_that_fail_an_assertion_and_those_it_would_hold()
    {
        var (engine, flow) = await EstateAsync(Holding);
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        var header = await runtime.Planner.OpenAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, SourceSelection.Full(), gate: false, stored: null);
        var summary = new PlanSummary();
        var lines = new List<string>();
        await foreach (var entry in runtime.Planner.EntriesAsync(header, null, 1, summary, CancellationToken.None))
        {
            lines.Add(PlanFormatting.Describe(entry));
        }

        Assert.Equal(LogCount, summary.Deliveries);
        Assert.Equal((LogCount, LogCount), (summary.FailingAssertions, summary.HeldByAssertions));
        Assert.EndsWith($"of the deliveries, {LogCount} fail an assertion of the mapping, {LogCount} of them to be held before they are sent", summary.ToString(), StringComparison.Ordinal);
        Assert.All(lines, line => Assert.Contains("to be held before it is sent, as 1 of 1 assertion judgement(s) of WellLog@1.4.0 failed (1 holding), first data.Name \"log-name-known\"", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_values_counts_the_rows_failing_each_assertion_beside_the_templates_rules()
    {
        var (engine, flow) = await EstateAsync(Holding + "\n" + """
            - stage: incoming
              notEquals: COMPOSITE
              onFail: report
            """);
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        var check = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { Targets = ["osdu.data.Name"] });

        Assert.Equal((LogCount, LogCount), (check.Rows.WithFailedAssertion, check.Rows.HeldByAssertion));
        Assert.Equal(0, check.Rows.Clean);
        var name = Assert.Single(check.Variables, v => v.Target == "osdu.data.Name");
        Assert.Equal(LogCount, name.Rows.Asserted);
        var held = Assert.Single(name.Findings, f => f.Outcome == "asserted" && f.Rule == "log-name-known");
        Assert.Equal("fails \"log-name-known\" (record); the record is held before it is sent", held.Message);
        Assert.Equal("hold", held.OnFail);
        Assert.Contains(name.Findings, f => f.Outcome == "asserted" && f.OnFail == "report");
        Assert.Equal(LogCount, held.Count);
        Assert.NotEmpty(held.Values);
        Assert.All(held.Samples, s => Assert.StartsWith("is ", s.Message, StringComparison.Ordinal));

        var text = new StringWriter();
        DeliveryValueCheckVerbs.WriteText(text, check);
        var written = text.ToString();
        Assert.StartsWith("!! ", written, StringComparison.Ordinal);
        Assert.Contains($"    asserted    {LogCount} failing an assertion of the mapping, {LogCount} of them to be held before they are sent", written, StringComparison.Ordinal);
        Assert.Contains($"; {LogCount} failing an assertion", written, StringComparison.Ordinal);
        Assert.Contains("asserted", written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_item_failing_an_assertion_names_the_child_row_it_was_written_from_however_many_rows_wrote_no_item(bool filtered)
    {
        var (engine, flow) = await EstateAsync(
            """
            - equals: no curve is named this
              onFail: report
            """,
            node: "        Mnemonic: { $from: curve_id }\n",
            block: "        Mnemonic:\n          $from: curve_id\n");
        if (filtered)
        {
            // The first row of each log's curves writes no item, so an item's place in the array is not its child row.
            var file = Path.Combine(flow.Render.MappingsDirectory!, "WellLog@1.4.0.yaml");
            var mapping = await File.ReadAllTextAsync(file);
            Assert.Contains("      $forEach: curves\n", mapping, StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, mapping.Replace("      $forEach: curves\n", "      $forEach: curves\n      $where: curve_id != \"MD\"\n", StringComparison.Ordinal));
        }

        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        var met = new List<ValueCheckOccurrence>();
        await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { Targets = ["osdu.data.Curves[].Mnemonic"] }, met.Add);

        var failed = met.Where(o => o.Outcome == "asserted").ToList();
        Assert.NotEmpty(failed);
        Assert.Equal(filtered, failed.All(o => o.Value != "MD"));
        Assert.All(failed, occurrence =>
        {
            // What the check says failed is what the row it names holds, which is the row the workbook reads the base data from.
            Assert.Equal("curves", occurrence.Child);
            var rows = occurrence.Source!.ScopeRows("curves");
            Assert.Equal(occurrence.Value, rows[occurrence.Record.Item!.Value - 1].GetString("curve_id"));
            Assert.Equal(["dataset.curves.curve_id"], occurrence.Record.Columns);
        });
    }

    [Fact]
    public async Task Check_values_counts_each_item_of_a_repeated_array_failing_an_assertion_once()
    {
        var (engine, flow) = await EstateAsync(
            """
            - equals: no curve is named this
              onFail: report
            - notEquals: also never
              name: never-fails
            """,
            node: "        Mnemonic: { $from: curve_id }\n",
            block: "        Mnemonic:\n          $from: curve_id\n");
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        var check = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { Targets = ["osdu.data.Curves[].Mnemonic"] });

        var mnemonic = Assert.Single(check.Variables, v => v.Target == "osdu.data.Curves[].Mnemonic");
        Assert.True(mnemonic.Items!.Total > mnemonic.Rows.Asserted, "a log holds several curves");
        Assert.Equal(mnemonic.Items.Total, mnemonic.Items.Asserted);
        Assert.Equal(LogCount, mnemonic.Rows.Asserted);
        var reported = Assert.Single(mnemonic.Findings, f => f.Outcome == "asserted");
        Assert.Equal(("report", mnemonic.Items.Total), (reported.OnFail, reported.Count));
        Assert.Equal((LogCount, 0L), (check.Rows.WithFailedAssertion, check.Rows.HeldByAssertion));
    }

    [Fact]
    public async Task A_check_of_one_variable_evaluates_the_fields_its_assertions_conditions_read()
    {
        var (engine, flow) = await EstateAsync("""
            - equals: no log is named this
              onFail: report
              where:
                - field: data.LogRun
                  exists: true
            """);
        using var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        var check = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { Targets = ["osdu.data.Name"] });

        // The condition reads data.LogRun, which the check evaluates with the variable named, so the assertion is judged.
        Assert.Contains(check.Variables, v => v.Target == "osdu.data.LogRun");
        Assert.Equal(LogCount, check.Rows.WithFailedAssertion);
        Assert.Equal(0, check.Rows.HeldByAssertion);
    }

    [Fact]
    public async Task A_preview_lists_what_the_record_fails_and_says_it_would_be_held()
    {
        var (engine, flow) = await EstateAsync(Holding);
        var previews = await DeliveryPreviewVerbs.PreviewsAsync(engine, [flow], SampleEstate.Values, null, whole: false, CancellationToken.None);

        var findings = Assert.Single(previews).Document!.Assertions!;
        Assert.True(findings.Holds);
        var text = new StringWriter();
        DeliveryPreviewVerbs.Show(text, previews, json: false, written: null, RecordPreviewLimits.Default.MaxDocumentChars);
        Assert.Contains("    asserted    1 of 1 assertion judgement(s) of WellLog@1.4.0 failed (1 holding), first data.Name \"log-name-known\"; the check before sending would hold the record, its document kept", text.ToString(), StringComparison.Ordinal);
        Assert.Contains("data.Name fails \"log-name-known\" (record, hold): is ", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_records_attempt_says_its_verdict_and_the_assertions_it_failed()
    {
        var log = new AssertionLog("WellLog@1.4.0");
        log.Judged();
        log.Fail(
            "data.Name", "data.Name",
            new NodeAssertion { Label = "log-name-known", Location = "record.data.Name.$assert[0]", Condition = new ValueCondition { Operator = ValueOperator.EqualTo } },
            AssertionAction.Hold, "COMPOSITE", "is COMPOSITE, not \"no log is named this\"");
        var verdict = new ValidationVerdict { Outcome = ValidationOutcome.Valid, Rules = 12, Assertions = log.Findings(), CheckedUtc = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc) };
        var attempt = new AttemptRecord
        {
            DeliveryKey = DeliveryKey.Derive("welldb", ["P1", "L1"]),
            Worker = "w",
            StartedUtc = verdict.CheckedUtc,
            CompletedUtc = verdict.CheckedUtc,
            Outcome = AttemptOutcome.Held,
            Phase = "none",
            ResultJson = AttemptResult.WithValidation(null, verdict),
        };

        Assert.Equal(
            [
                "checked valid: 12 rule(s) met; 1 of 1 assertion judgement(s) of WellLog@1.4.0 failed (1 holding), first data.Name \"log-name-known\"",
                "  data.Name fails \"log-name-known\" (record, hold): is COMPOSITE, not \"no log is named this\"",
            ],
            DeliveryRecordVerbs.VerdictLines(attempt));
        Assert.Empty(DeliveryRecordVerbs.VerdictLines(attempt with { ResultJson = null }));
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
