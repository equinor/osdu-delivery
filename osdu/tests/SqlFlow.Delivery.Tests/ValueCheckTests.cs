using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The value check (<see cref="ValueChecker"/>): the rows of a flow's scope that will not give the variables of its mapping
/// the values its template expects, found by inspecting every row as a delivery renders it, over the sample well database logs.
/// Each failing row is named with the reason, the value behind it and where the row came from; counts are exact and what
/// is listed is bounded; and nothing is written anywhere.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ValueCheckTests : IDisposable
{
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a failed assertion left open is removed with the temporary folder by the operating system.
        }
    }

    private async Task<(MemoryIngestionTables Tables, FlowRuntime Runtime, FixedRecordSearchFactory Searches)> RuntimeAsync(Action<MemoryIngestionTables>? change = null)
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        change?.Invoke(tables);
        var searches = FixedRecordSearchFactory.SampleWellbores();
        var engine = Samples.Engine(ledger: null, _clock, sources: tables, searches: searches);
        var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        return (tables, runtime, searches);
    }

    private static string SourceKeyOf(SampleLog log) => SourceKey.Display(SampleWellLogs.System, [log.SourceProject, log.LogId]);

    private static ValueCheckVariable Variable(ValueCheck check, string target) => Assert.Single(check.Variables, v => v.Target == target);

    [Fact]
    public async Task Every_variable_of_every_row_is_counted_and_what_a_plan_holds_is_what_the_check_finds_held()
    {
        var (tables, runtime, _) = await RuntimeAsync();
        using (runtime)
        {
            var logs = SampleEstate.Logs().Count;
            var check = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest());

            Assert.Equal(logs, check.Rows.Read);
            Assert.Equal(logs, check.Rows.Checked);
            Assert.True(check.Rows.Complete);
            Assert.Equal(0, check.Rows.PassedOver);
            Assert.Equal(0, check.Rows.Keyless);
            Assert.Equal("WellLog@1.4.0", check.Inputs.Mapping);
            Assert.Empty(check.Asked.Targets);

            // Every variable is listed, and every row counts once for each of them.
            Assert.Equal(runtime.Mapping.Mapping.Entries.Select(e => e.Target.Text).Distinct(), check.Variables.Where(v => v.Entry is not null).Select(v => v.Target));
            Assert.All(check.Variables, v => Assert.Equal(logs, v.Rows.Total));

            // An optional property a curve row leaves empty leaves the item without it, and says which column.
            var version = Variable(check, "osdu.data.Curves[].CurveVersion");
            Assert.Equal("osdu.data.Curves", version.Repeater);
            Assert.False(version.Required);
            Assert.True(version.Items!.Empty > 0);
            var empty = Assert.Single(version.Findings, f => f.Outcome == "empty");
            Assert.Equal("dataset.curves.curve_version is empty", empty.Message);
            Assert.Equal(version.Items.Empty, empty.Count);
            Assert.All(empty.Samples, s => Assert.NotNull(s.Item));

            // The check read the whole scope, once, and wrote nothing into a work location.
            Assert.Equal(SourceSelectionKind.Full, Assert.Single(tables.Selections).Kind);
            Assert.False(Directory.Exists(Path.Combine(_root, "work")) && Directory.EnumerateFileSystemEntries(Path.Combine(_root, "work")).Any());

            // What a plan of the same scope holds is what the check finds held: the one render decides both.
            Assert.Equal(await HeldByPlanAsync(runtime), check.Rows.WithHeld);
        }
    }

    [Fact]
    public async Task A_row_that_will_not_give_a_variable_its_value_is_named_with_the_reason_the_value_and_where_it_came_from()
    {
        var logs = SampleEstate.Logs();
        var (_, runtime, _) = await RuntimeAsync(tables =>
        {
            tables.Records[0].Row["log_run"] = "   ";
            tables.Records[1].Row["wellbore_uwi"] = "NOWHERE 1";
        });
        using (runtime)
        {
            var occurrences = new List<ValueCheckOccurrence>();
            var check = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest(), occurrences.Add);

            var name = Variable(check, "osdu.data.LogRun");
            Assert.Equal(1, name.Rows.Held);
            Assert.Equal(logs.Count - 1, name.Rows.Valid);
            var held = Assert.Single(name.Findings);
            Assert.Equal("held", held.Outcome);
            Assert.Equal("dataset.log_run is empty, and the entry is required", held.Message);
            var record = Assert.Single(held.Samples);
            Assert.Equal(SourceKeyOf(logs[0]), record.SourceKey);
            Assert.Equal(logs[0].Key.Value, record.DeliveryKey);
            Assert.Equal(SampleEstate.FileName, record.File);
            Assert.Equal(1, record.Row);

            var wellbore = Variable(check, "osdu.data.WellboreID");
            Assert.Equal(1, wellbore.Rows.Held);
            var missing = Assert.Single(wellbore.Findings);
            Assert.Contains("NOWHERE 1", missing.Message, StringComparison.Ordinal);
            Assert.Equal("NOWHERE 1", Assert.Single(missing.Values).Value);
            Assert.Equal(SourceKeyOf(logs[1]), Assert.Single(missing.Samples).SourceKey);

            Assert.Equal(2, check.Rows.WithHeld);
            Assert.Equal(await HeldByPlanAsync(runtime), check.Rows.WithHeld);

            // Every failing occurrence is met once, the ones the mapping means no value for aside.
            Assert.Contains(occurrences, o => o.Target == "osdu.data.LogRun" && o.Outcome == "held" && o.Record.SourceKey == SourceKeyOf(logs[0]));
            Assert.Contains(occurrences, o => o.Target == "osdu.data.WellboreID" && o.Record.SourceKey == SourceKeyOf(logs[1]));
            Assert.DoesNotContain(occurrences, o => o.Outcome == "notApplicable");
            Assert.Equal(check.Variables.Sum(v => v.Findings.Where(f => f.Outcome != "notApplicable").Sum(f => f.Count)), occurrences.Count);
        }
    }

    [Fact]
    public async Task A_check_of_one_variable_evaluates_that_variable_alone_and_asks_the_platform_nothing_for_another()
    {
        var (_, runtime, searches) = await RuntimeAsync();
        using (runtime)
        {
            var check = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { Targets = ["osdu.data.Name"] });

            Assert.Equal(["osdu.data.Name"], check.Variables.Select(v => v.Target));
            Assert.Equal(["osdu.data.Name"], check.Asked.Targets);
            Assert.Equal(SampleEstate.Logs().Count, Variable(check, "osdu.data.Name").Rows.Valid);
            Assert.All(searches.Created, s => Assert.Empty(s.Asked));

            // The wellbore is searched for, and only it.
            var wellbore = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { Targets = ["osdu.data.WellboreID"] });
            Assert.Equal(SampleEstate.Logs().Count, Variable(wellbore, "osdu.data.WellboreID").Rows.Valid);
            Assert.Contains(searches.Created, s => !s.Asked.IsEmpty);
        }
    }

    [Fact]
    public async Task A_check_reads_no_more_rows_than_asked_and_says_the_scope_holds_more()
    {
        var (_, runtime, _) = await RuntimeAsync();
        using (runtime)
        {
            var logs = SampleEstate.Logs().Count;
            Assert.True(logs > 2, "the sample estate holds more than two logs");

            var capped = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { MaxRows = 2 });
            Assert.Equal(2, capped.Rows.Read);
            Assert.Equal(2, capped.Rows.Checked);
            Assert.False(capped.Rows.Complete);
            Assert.Equal(logs, capped.Rows.ScopeRecords);

            var exact = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { MaxRows = logs });
            Assert.True(exact.Rows.Complete);

            var whole = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { MaxRows = 0 });
            Assert.True(whole.Rows.Complete);
            Assert.Equal(logs, whole.Rows.Checked);
        }
    }

    [Fact]
    public async Task A_row_a_delivery_never_renders_is_passed_over_and_counted_by_why()
    {
        var (_, runtime, _) = await RuntimeAsync(tables => tables.Records[0].DeletedUtc = Now.AddMinutes(-1));
        using (runtime)
        {
            var check = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest());

            Assert.Equal(SampleEstate.Logs().Count, check.Rows.Read);
            Assert.Equal(1, check.Rows.PassedOver);
            Assert.Equal(check.Rows.Read - 1, check.Rows.Checked);
            var why = Assert.Single(check.Rows.PassedOverWhy);
            Assert.Contains("deleted", why.Reason, StringComparison.Ordinal);
            Assert.Equal(SourceKeyOf(SampleEstate.Logs()[0]), Assert.Single(why.Samples).SourceKey);
        }
    }

    [Fact]
    public async Task The_examples_of_a_finding_are_paged_in_the_order_the_scope_is_read()
    {
        var logs = SampleEstate.Logs();
        var (_, runtime, _) = await RuntimeAsync(tables =>
        {
            tables.Records[0].Row["log_run"] = null;
            tables.Records[1].Row["log_run"] = "";
            tables.Records[2].Row["log_run"] = " ";
        });
        using (runtime)
        {
            var first = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { Targets = ["osdu.data.LogRun"], Samples = 2 });
            var finding = Assert.Single(Variable(first, "osdu.data.LogRun").Findings);
            Assert.Equal(3, finding.Count);
            Assert.Equal(3, finding.Rows);
            Assert.Equal(0, finding.SamplesFrom);
            Assert.Equal([SourceKeyOf(logs[0]), SourceKeyOf(logs[1])], finding.Samples.Select(s => s.SourceKey));

            var next = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { Targets = ["osdu.data.LogRun"], Samples = 2, SkipSamples = 2 });
            var rest = Assert.Single(Variable(next, "osdu.data.LogRun").Findings);
            Assert.Equal(2, rest.SamplesFrom);
            Assert.Equal([SourceKeyOf(logs[2])], rest.Samples.Select(s => s.SourceKey));
        }
    }

    [Fact]
    public async Task A_check_that_cannot_say_anything_is_refused_naming_why()
    {
        var (_, runtime, _) = await RuntimeAsync();
        using (runtime)
        {
            var checker = new ValueChecker(runtime);

            var unknown = await Assert.ThrowsAsync<DeliveryException>(() => checker.CheckAsync(new ValueCheckRequest { Targets = ["osdu.data.NoSuchThing"] }));
            Assert.StartsWith("osdu.data.NoSuchThing: no entry of mapping WellLog@1.4.0 fills it", unknown.Message, StringComparison.Ordinal);

            var malformed = await Assert.ThrowsAsync<DeliveryException>(() => checker.CheckAsync(new ValueCheckRequest { Targets = ["data.Name"] }));
            Assert.Contains("must start with 'osdu.'", malformed.Message, StringComparison.Ordinal);

            var drifted = await Assert.ThrowsAsync<DeliveryException>(() => checker.CheckAsync(new ValueCheckRequest { Mapping = "WellLog@1.3.0" }));
            Assert.Contains("renders with mapping WellLog@1.4.0, not WellLog@1.3.0", drifted.Message, StringComparison.Ordinal);

            await Assert.ThrowsAsync<DeliveryException>(() => checker.CheckAsync(new ValueCheckRequest { MaxRows = -1 }));
            await Assert.ThrowsAsync<DeliveryException>(() => checker.CheckAsync(new ValueCheckRequest { Samples = ValueCheckLimits.Default.MaxSamples + 1 }));
        }
    }

    [Fact]
    public async Task An_answer_too_large_loses_what_it_lists_and_keeps_every_count()
    {
        var (_, runtime, _) = await RuntimeAsync(tables =>
        {
            foreach (var record in tables.Records)
            {
                record.Row["log_run"] = null;
            }
        });
        using (runtime)
        {
            var check = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { Samples = 50 });
            var full = System.Text.Json.JsonSerializer.Serialize(check).Length;
            var fitted = ValueChecker.Fit(check, full - 1, c => System.Text.Json.JsonSerializer.Serialize(c).Length);

            Assert.True(System.Text.Json.JsonSerializer.Serialize(fitted).Length < full);
            Assert.NotEmpty(fitted.Notes);
            Assert.Equal(check.Variables.Select(v => v.Rows), fitted.Variables.Select(v => v.Rows));
            Assert.Equal(
                check.Variables.SelectMany(v => v.Findings).Select(f => f.Count),
                fitted.Variables.SelectMany(v => v.Findings).Select(f => f.Count));
            Assert.Same(check, ValueChecker.Fit(check, full, c => System.Text.Json.JsonSerializer.Serialize(c).Length));
        }
    }

    [Fact]
    public void A_value_the_template_does_not_accept_is_invalid_and_the_rows_failing_one_rule_are_one_finding_with_the_values_behind_it()
    {
        var mapping = TestSchema.Mapping("""
            When:
              $from: when
              $required: false
            """);
        var renderer = new MappingRenderer(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context());
        var request = new ValueCheckRequest();
        var tally = new ValueTally(mapping, TestSchema.Build(), EntrySelection.All, request, ValueCheckLimits.Default, each: null);
        foreach (var (name, written) in new[] { ("w-1", "01.09.2026"), ("w-2", "2026-09-01T10:15:30Z"), ("w-3", "02.09.2026"), ("w-4", "01.09.2026") })
        {
            var record = new SourceRecord
            {
                Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = name, ["depth"] = "1", ["when"] = written }),
                Origin = new SourceOrigin("wells.csv", long.Parse(name[2..], System.Globalization.CultureInfo.InvariantCulture), null),
            };
            tally.Add(record, renderer.Inspect(record, EntrySelection.All), renderer);
        }

        var variable = Assert.Single(tally.Variables(), v => v.Target == "osdu.data.When");
        Assert.Equal(3, variable.Rows.Invalid);
        Assert.Equal(1, variable.Rows.Valid);
        Assert.Equal("2026-09-01T10:15:30Z", Assert.Single(variable.Values).Value);

        var finding = Assert.Single(variable.Findings);
        Assert.Equal("invalid", finding.Outcome);
        Assert.Equal("format", finding.Rule);
        Assert.Equal(3, finding.Count);
        Assert.Equal([new ValueCheckValue("01.09.2026", 2), new ValueCheckValue("02.09.2026", 1)], finding.Values);
        Assert.Equal([1L, 3L, 4L], finding.Samples.Select(s => s.Row!.Value));
        Assert.All(finding.Samples, s => Assert.Equal("wells.csv", s.File));
        Assert.Equal(3, tally.Rows(4, 4, complete: true).WithInvalid);
    }

    /// <summary>How many records a plan of the scope holds, rendering them as a first delivery would.</summary>
    [Fact]
    public async Task An_export_of_an_attribute_hands_back_the_rows_failing_it_with_the_base_data_behind_them_and_nothing_written()
    {
        var logs = SampleEstate.Logs();
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        tables.Records[0].Row["log_run"] = "   ";
        tables.Records[1].Row["wellbore_uwi"] = "NOWHERE 1";
        var engine = Samples.Engine(ledger: null, _clock, sources: tables, searches: FixedRecordSearchFactory.SampleWellbores());
        var flow = Samples.LocalFlow(_root);

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["flowFile"] = Samples.Flow,
            ["values"] = SqlFlow.Core.Runs.RunParameters.ValuesToJson(SampleEstate.Values)!,
            ["targets"] = """["osdu.data.WellboreID"]""",
            ["samples"] = "20",
        };
        if (flow.Interface is { } name)
        {
            arguments["interface"] = name;
        }

        if (flow.Partition is { } partition)
        {
            arguments[DeliveryOperation.PartitionArgument] = partition;
        }

        var payload = new SqlFlow.Core.Compute.ComputeTaskPayload { Operation = ExportValueIssuesOperation.OperationName, SourceRef = flow.Name, Arguments = arguments };
        payload.Validate([ExportValueIssuesOperation.OperationName]);

        var answer = await new ExportValueIssuesOperation(engine).ExecuteAsync(payload, CancellationToken.None);
        using var json = System.Text.Json.JsonDocument.Parse(answer);
        var root = json.RootElement;
        Assert.Equal(logs.Count, root.GetProperty("rowsChecked").GetInt64());
        Assert.Equal(1, root.GetProperty("issues").GetInt64());
        Assert.Equal(1, root.GetProperty("listed").GetInt64());

        // Only the attribute's own failing row, not the row another attribute fails, with what the row holds in the source.
        using var read = new WorkbookReading(Convert.FromBase64String(root.GetProperty("workbook").GetString()!));
        var row = Assert.Single(read.Rows(ValueIssuesWorkbook.IssuesSheet));
        Assert.Equal("osdu.data.WellboreID", row["Attribute"].GetText());
        Assert.Equal("Held", row["Outcome"].GetText());
        Assert.Equal(SourceKeyOf(logs[1]), row["Source key"].GetText());
        Assert.Equal(SampleEstate.FileName, row["Source file"].GetText());
        Assert.Equal("NOWHERE 1", row["dataset.wellbore_uwi"].GetText());
        Assert.Equal("dataset.wellbore_uwi", row["Source column"].GetText());
        Assert.Equal("NOWHERE 1", row["Source value"].GetText());
        Assert.DoesNotContain("dataset.log_run", read.Headers(ValueIssuesWorkbook.IssuesSheet));
        Assert.Equal("osdu.data.WellboreID", read.About()["Attribute"].GetText());

        Assert.False(Directory.Exists(Path.Combine(_root, "work")) && Directory.EnumerateFileSystemEntries(Path.Combine(_root, "work")).Any());
    }

    [Fact]
    public async Task A_failing_value_names_the_source_column_to_fix()
    {
        var (_, runtime, _) = await RuntimeAsync(tables =>
        {
            tables.Records[0].Row["log_run"] = "   ";
            tables.Records[1].Row["wellbore_uwi"] = "NOWHERE 1";
        });
        using (runtime)
        {
            var check = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest());

            // The reason names the column.
            Assert.Equal(["dataset.log_run"], Assert.Single(Assert.Single(Variable(check, "osdu.data.LogRun").Findings).Samples).Columns);

            // The reason quotes the value, which is what the column holds.
            Assert.Equal(["dataset.wellbore_uwi"], Assert.Single(Assert.Single(Variable(check, "osdu.data.WellboreID").Findings).Samples).Columns);
        }
    }

    [Fact]
    public async Task A_failing_item_names_the_file_and_row_of_its_own_child_row()
    {
        var (_, runtime, _) = await RuntimeAsync(tables =>
        {
            foreach (var record in tables.Records)
            {
                var curves = record.Datasets["curves"];
                for (var i = 0; i < curves.Count; i++)
                {
                    curves[i][FlowSystemColumns.DefaultFileName] = "curves.csv";
                    curves[i][FlowSystemColumns.DefaultRowNumber] = 5000L + i;
                }
            }
        });
        using (runtime)
        {
            var check = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest { Targets = ["osdu.data.Curves[].CurveVersion"] });

            var empty = Assert.Single(Variable(check, "osdu.data.Curves[].CurveVersion").Findings, f => f.Outcome == "empty");
            Assert.NotEmpty(empty.Samples);
            Assert.All(empty.Samples, sample =>
            {
                Assert.Equal("curves.csv", sample.File);
                Assert.Equal(5000L + sample.Item!.Value - 1, sample.Row);
                Assert.Equal(["dataset.curves.curve_version"], sample.Columns);
            });
        }
    }

    [Fact]
    public async Task A_variable_says_which_source_columns_it_reads()
    {
        var (_, runtime, _) = await RuntimeAsync();
        using (runtime)
        {
            var check = await new ValueChecker(runtime).CheckAsync(new ValueCheckRequest());

            Assert.Contains("dataset.log_run", Variable(check, "osdu.data.LogRun").Columns);
            Assert.Contains("dataset.curves.curve_version", Variable(check, "osdu.data.Curves[].CurveVersion").Columns);
        }
    }

    private static async Task<long> HeldByPlanAsync(FlowRuntime runtime) => (await runtime.PlanAsync(force: true)).Holds;
}
