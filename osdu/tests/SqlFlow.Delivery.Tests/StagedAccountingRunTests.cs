using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A staged delivered assertion end to end (osdu/docs/reference/flow/staged-accounting.md): the sample well logs delivered by
/// the sample delivery flow to a fake protocol, with the ledger in the module's database on SQL Server; then an assertion flow
/// beside it accounts for every row the flow stages against a fake OSDU holding what the test puts there. Each scenario covers
/// one way a row comes out: in OSDU, held back by the ledger with its reason, never planned, delivered and not found, marked
/// deleted and not expected; and the settings that weigh them: explained, exact, values.
/// </summary>
/// <remarks>
/// The assertion flow finds its delivery flow beside it in a copy of the estate the test owns, as a checkout the catalog has
/// not synced, and reads its rows from the in-memory ingestion tables the delivery read them from.
/// </remarks>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class StagedAccountingRunTests : IDisposable
{
    private const string DeliveryFlowName = "welldb-welllog-03-header-delivery";

    private const string HoldReason = "data.WellboreID: the wellbore W-17 the log names is not in the partition";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly FakeProtocol _protocol = new();
    private readonly FakeAssertionPlatform _platform = new();
    private readonly OsduLedger _ledger;
    private readonly List<FlowRuntime> _runtimes = [];
    private MemoryIngestionTables _tables = null!;
    private FlowRuntime _delivery = null!;

    public StagedAccountingRunTests()
    {
        _ledger = _db.Ledger(_clock);
        WriteEstate();
    }

    public void Dispose()
    {
        foreach (var runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _platform.Dispose();
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a runtime still held is left in the test's own temp folder, which nothing else reads.
        }
    }

    private string Estate => Path.Combine(_root, "estate");

    private Guid LedgerId => _delivery.Flow.Id;

    private static IReadOnlyList<SampleLog> Logs => SampleEstate.Logs();

    /// <summary>The estate's copy the assertion flow reads beside it: the sample delivery flow, its target a placeholder, and its mappings.</summary>
    private void WriteEstate()
    {
        Directory.CreateDirectory(Path.Combine(Estate, "flows"));
        var yaml = File.ReadAllText(Samples.Flow).ReplaceLineEndings("\n");
        var target = yaml.IndexOf("target:\n", StringComparison.Ordinal);
        var protocol = yaml.IndexOf("  protocol: ddms\n", target, StringComparison.Ordinal);
        Assert.True(target > 0 && protocol > target, "the sample flow's target block moved; the test's copy of it has to follow");
        yaml = yaml[..target] + "target:\n  endpoint: http://localhost:9/facade-api\n  auth:\n    type: none\n" + yaml[protocol..];
        File.WriteAllText(Path.Combine(Estate, "flows", DeliveryFlowName + ".yaml"), yaml);
        CopyDirectory(Samples.Mappings, Path.Combine(Estate, "mappings"));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (var folder in Directory.GetDirectories(from))
        {
            CopyDirectory(folder, Path.Combine(to, Path.GetFileName(folder)));
        }
    }

    private EngineContext Engine() => Samples.Engine(_ledger, _clock, sources: _tables) with { Protocols = new FakeProtocolFactory(_protocol) };

    /// <summary>Every sample log delivered once, and OSDU holding each record the ledger holds as delivered.</summary>
    private async Task DeliverEverythingAsync()
    {
        _tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        _delivery = await FlowRuntime.CreateAsync(Engine(), Samples.LocalFlow(_root), SampleEstate.Values);
        _runtimes.Add(_delivery);
        await _ledger.RegisterAsync(_delivery.Flow);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var selection = SourceSelection.Full();
        _delivery.Selection = selection;
        _delivery.RunId = Guid.CreateVersion7();
        var run = await _delivery.RunAsync(force: true);
        Assert.Equal(Logs.Count, run.Work.Delivered);
        foreach (var id in await DeliveredIdsAsync())
        {
            _platform.Add(id, Samples.WellLogKind, new JsonObject { ["Name"] = id });
        }

        _clock.Advance(TimeSpan.FromHours(1));
    }

    /// <summary>The OSDU id of each record the ledger holds as delivered, without its version.</summary>
    private async Task<List<string>> DeliveredIdsAsync()
        => (await _ledger.ListRecordsAsync(LedgerId, null, 1000))
            .Where(r => r.Status == RecordStatus.Delivered)
            .Select(r => TargetId.WithoutVersion(r.TargetId!).TrimEnd(':'))
            .Order(StringComparer.Ordinal)
            .ToList();

    private async Task<string> IdOfAsync(int index)
        => TargetId.WithoutVersion((await _ledger.GetRecordAsync(LedgerId, SampleEstate.Key(index)))!.TargetId!).TrimEnd(':');

    /// <summary>A row staged after the delivery ran: a copy of the first log under a log id of its own.</summary>
    private MemoryRecord StageNew(string logId)
    {
        var first = _tables.Records.First();
        var row = new Dictionary<string, object?>(first.Row, StringComparer.OrdinalIgnoreCase) { ["log_id"] = logId, ["RecId"] = 1000L + _tables.Records.Count };
        return _tables.Add(new MemoryRecord
        {
            Row = row,
            UpdatedUtc = _clock.GetUtcNow().UtcDateTime,
            InsertedUtc = _clock.GetUtcNow().UtcDateTime,
            FileName = SampleEstate.FileName,
            RowNumber = _tables.Records.Count + 1,
        });
    }

    /// <summary>A staged row the ledger holds held, as a render that found the wellbore missing holds it.</summary>
    private async Task HoldAsync(MemoryRecord staged)
    {
        var values = new[] { (string)staged.Row["source_project"]!, (string)staged.Row["log_id"]! };
        var key = DeliveryKey.Derive(SampleWellLogs.System, values);
        var held = await _ledger.MarkHeldAsync(LedgerId, [new RecordState
        {
            DeliveryKey = key,
            FlowId = LedgerId,
            SourceKey = SourceKey.Display(SampleWellLogs.System, values),
            SourceKeyJson = new KeyTuple(values).Json,
            MappingName = "WellLog",
            Status = RecordStatus.Held,
            LastError = HoldReason,
            PendingSourceFileName = SampleEstate.FileName,
            PendingSourceRowNumber = staged.RowNumber,
        }]);
        Assert.Equal(key, Assert.Single(held));
    }

    private static string Flow(string values = $"values: {{ logSource: {SampleWellLogs.LogSource} }}") => $"""
        flowType: assertion
        name: welldb-welllog-05-assertion
        partitions: [dev]
        source:
          endpoint: http://localhost
        defaults: {"{"} examples: 20, indexSettleSeconds: 0 {"}"}
        reliability: {"{"} retry: {"{"} attempts: 1, baseDelayMs: 1, maxDelayMs: 1 {"}"} {"}"}
        tests:
          - name: logs-staged
            kind: {Samples.WellLogKind}
            assert:
              - delivered: {DeliveryFlowName}
                staged: true
                {values}
        """;

    /// <summary>One run of the assertion flow <paramref name="yaml"/> declares, from its file beside the delivery flow, and the result of its test.</summary>
    private async Task<TestResult> RunAsync(string yaml)
    {
        var file = Path.Combine(Estate, "flows", "welldb-welllog-05-assertion.yaml");
        File.WriteAllText(file, yaml);
        var flow = new DeliveryDocumentLoader().ParseAssertion(File.ReadAllText(file), file).ForPartition(Samples.SamplePartition);
        var runner = new AssertionRunner(Engine(), flow, new Dictionary<string, string>(StringComparer.Ordinal), Samples.Logger<AssertionRunner>(), _platform, allowLoopback: true);
        var outcome = await runner.TestAsync([], [], Guid.CreateVersion7(), "tests", CancellationToken.None);
        return TestResults.Deserialize(Assert.Single(await _ledger.ListAssertionResultsAsync(outcome.AssertionRunId)).Detail)!;
    }

    private static AssertionOutcome Staged(TestResult result) => Assert.Single(result.Assertions, a => a.Type == "delivered");

    [Fact]
    public async Task Every_staged_row_is_in_osdu_once_it_is_delivered()
    {
        await DeliverEverythingAsync();

        var staged = Staged(await RunAsync(Flow()));

        Assert.Equal(TestOutcomes.Passed, staged.Outcome);
        Assert.Equal(Logs.Count, staged.Checked);
        Assert.Equal(100.0, staged.Value);
        Assert.StartsWith(string.Create(CultureInfo.InvariantCulture, $"{Logs.Count} row(s) staged: {Logs.Count} in OSDU, 0 held back by the ledger, 0 not in OSDU"), staged.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_never_planned_or_delivered_and_not_found_fails_and_a_row_marked_deleted_is_not_expected()
    {
        await DeliverEverythingAsync();
        var lost = await IdOfAsync(1);
        _platform.Records.RemoveAll(r => r["id"]!.GetValue<string>() == lost);
        var added = StageNew("NEW-LOG-1");
        _tables.Records.Last(r => (string)r.Row["log_id"]! != "NEW-LOG-1").DeletedUtc = _clock.GetUtcNow().UtcDateTime;

        var staged = Staged(await RunAsync(Flow()));

        Assert.Equal(TestOutcomes.Failed, staged.Outcome);
        Assert.Equal("error", staged.Severity);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"{Logs.Count + 1} row(s) staged: {Logs.Count - 2} in OSDU, 0 held back by the ledger, 2 not in OSDU with nothing to explain it (1 never planned, 1 delivered and not found, 0 with a blank key); 1 marked deleted"), staged.Actual, StringComparison.Ordinal);
        Assert.Contains("2 staged row(s) are not in OSDU, and the ledger of", staged.Message, StringComparison.Ordinal);
        var notFound = Assert.Single(staged.Examples, e => e.Id == lost);
        Assert.Contains($"is delivered by {DeliveryFlowName}", notFound.Reason, StringComparison.Ordinal);
        Assert.Contains("and the test's query does not find it", notFound.Reason, StringComparison.Ordinal);
        var neverPlanned = Assert.Single(staged.Examples, e => e.Reason.Contains("no run has planned it", StringComparison.Ordinal));
        Assert.Contains("NEW-LOG-1", neverPlanned.Value, StringComparison.Ordinal);

        // The result keeps where the rows were read and each listed row's key, which a query of them in the flow's tables
        // narrows to: the key in the flow's key order (source_project, log_id), the values the scope was read under.
        Assert.Equal([(string)added.Row["source_project"]!, "NEW-LOG-1"], neverPlanned.Key);
        Assert.Equal([Logs[1].SourceProject, Logs[1].LogId], notFound.Key);
        var scope = Assert.IsType<StagedScope>(staged.Staged);
        Assert.Equal(DeliveryFlowName, scope.Flow);
        Assert.Null(scope.Interface);
        Assert.Equal(SampleWellLogs.LogSource, scope.Values["logSource"]);
    }

    [Fact]
    public async Task A_row_the_ledger_holds_back_says_why_and_weighs_what_explained_gives_it()
    {
        await DeliverEverythingAsync();
        var held = StageNew("HELD-LOG-1");
        await HoldAsync(held);

        var failed = Staged(await RunAsync(Flow()));
        Assert.Equal(TestOutcomes.Failed, failed.Outcome);
        var example = Assert.Single(failed.Examples);
        Assert.Equal($"is held by {DeliveryFlowName}: {HoldReason} (row {held.RowNumber} of {SampleEstate.FileName})", example.Reason);
        Assert.Equal([(string)held.Row["source_project"]!, "HELD-LOG-1"], example.Key);
        Assert.Contains("1 held back by the ledger, 0 not in OSDU with nothing to explain it", failed.Actual, StringComparison.Ordinal);
        Assert.Contains($"held back most for is held by {DeliveryFlowName}: {HoldReason} (1)", failed.Actual, StringComparison.Ordinal);

        var warned = await RunAsync(Flow(values: $"values: {{ logSource: {SampleWellLogs.LogSource} }}\n        explained: warning"));
        Assert.Equal(TestOutcomes.Warned, warned.Outcome);
        Assert.Equal("warning", Staged(warned).Severity);
        Assert.Contains("1 staged row(s) the ledger holds back, saying why (warning)", Staged(warned).Message, StringComparison.Ordinal);
        Assert.EndsWith("; a row its ledger explains counts as warning", Staged(warned).Expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exact_fails_a_record_no_staged_row_accounts_for()
    {
        await DeliverEverythingAsync();
        _platform.Add("dev:work-product-component--WellLog:not-staged", Samples.WellLogKind, new JsonObject { ["Name"] = "stray" });

        var loose = Staged(await RunAsync(Flow()));
        Assert.Equal(TestOutcomes.Passed, loose.Outcome);

        var exact = Staged(await RunAsync(Flow(values: $"values: {{ logSource: {SampleWellLogs.LogSource} }}\n        exact: true")));
        Assert.Equal(TestOutcomes.Failed, exact.Outcome);
        var stray = Assert.Single(exact.Examples);
        Assert.Equal("dev:work-product-component--WellLog:not-staged", stray.Id);
        Assert.Equal($"is found by the test's query, and no row {DeliveryFlowName} stages accounts for it", stray.Reason);

        // A record no staged row accounts for has no row, so no key to query the source by.
        Assert.Null(stray.Key);
    }

    [Fact]
    public async Task Rows_are_read_under_the_scope_values_names_and_a_scope_left_unnamed_is_refused()
    {
        await DeliverEverythingAsync();

        var other = Staged(await RunAsync(Flow(values: "values: { logSource: WIRELINE }")));
        Assert.Equal(TestOutcomes.Passed, other.Outcome);
        Assert.StartsWith("0 row(s) staged", other.Actual, StringComparison.Ordinal);

        var unnamed = Staged(await RunAsync(Flow(values: string.Empty)));
        Assert.Equal(TestOutcomes.Errored, unnamed.Outcome);
        Assert.Contains("logSource", unnamed.Message, StringComparison.Ordinal);

        var wrong = Staged(await RunAsync(Flow(values: "values: { source: COMPOSITE }")));
        Assert.Equal(TestOutcomes.Errored, wrong.Outcome);
        Assert.Equal($"logs-staged: assert[0].values.source names no parameter of delivery flow '{DeliveryFlowName}'; it declares logSource.", wrong.Message);
    }
}
