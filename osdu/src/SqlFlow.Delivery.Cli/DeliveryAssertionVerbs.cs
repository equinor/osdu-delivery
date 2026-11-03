using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The <c>assertions</c> verb: an assertion flow's report where an operator already is, at a terminal or in a CI job,
/// without a control plane to reach. <c>list</c> shows the flow's runs, <c>status</c> where each test stands, and
/// <c>report</c> writes a run's full report as JSON, Markdown, HTML or JUnit XML, rendered by the same code as the API's.
/// Running tests is a run like any other: <c>sqlflow run &lt;flow.yaml&gt; --payload '{"tests":["name"]}'</c>.
/// </summary>
internal static class DeliveryAssertionVerbs
{
    /// <summary>Runs listed when the command line asks for no count.</summary>
    private const int DefaultMax = 20;

    public static async Task<int> AssertionsAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var engine = context.Services.GetRequiredService<EngineContext>();
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;
        if (context.Arguments.Positional(2) is not { } flowPath)
        {
            return context.UsageError("name the assertion flow document whose report this is.");
        }

        var ledger = engine.Ledger
            ?? throw new FlowValidationException(
                "Assertion reports live in the module's database. Run 'sqlflow assertions' with --db <conn-ref>, or set the catalog variable.");
        var flow = await CliPartitions.AssertionAsync(context, engine.Documents.LoadAssertion(flowPath), ct).ConfigureAwait(false);
        return verb switch
        {
            "list" => await ListAsync(context, ledger, flow, ct).ConfigureAwait(false),
            "status" => await StatusAsync(context, ledger, flow, ct).ConfigureAwait(false),
            "report" => await ReportAsync(context, ledger, engine.Time, flow, ct).ConfigureAwait(false),
            _ => context.UsageError("say what to show: list, status or report."),
        };
    }

    /// <summary>The flow's runs in its partition, newest first, with how their tests came out.</summary>
    private static async Task<int> ListAsync(CliVerbContext context, ILedger ledger, AssertionFlowDefinition flow, CancellationToken ct)
    {
        var max = Count(context.Arguments.GetOption("--max"), DefaultMax, "--max");
        var runs = await ledger.ListAssertionRunsAsync(flow.LedgerId, max, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = flow.Name,
                ["ledger"] = flow.LedgerName,
                ["runs"] = new JsonArray(runs.Select(r => (JsonNode)Described(r)).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{flow.LedgerName}: {runs.Count} run(s)"));
        if (runs.Count == 0)
        {
            context.Out.WriteLine("  none yet. Run the tests with: sqlflow run <flow.yaml> (a plan checks them without running: --operation plan)");
        }

        foreach (var run in runs)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {run.AssertionRunId,8}  {run.Status,-9}  {Stamp(run.StartedUtc)}  {run.Counts.Passed} passed, {run.Counts.Failed} failed, {run.Counts.Warned} warned, {run.Counts.Errored} errored, {run.Counts.Skipped} skipped  by {run.Actor}"));
            if (run.Error is { Length: > 0 } error)
            {
                context.Out.WriteLine("            " + error);
            }
        }

        return 0;
    }

    /// <summary>
    /// Where each test of the flow stands now, as the board and a set's report say it (<see cref="AssertionSetReport.TestOf"/>):
    /// disabled, by its latest result of the test as it is now, or not run.
    /// </summary>
    private static async Task<int> StatusAsync(CliVerbContext context, ILedger ledger, AssertionFlowDefinition flow, CancellationToken ct)
    {
        var latest = (await ledger.LatestAssertionResultsAsync([flow.LedgerId], ct: ct).ConfigureAwait(false))
            .ToDictionary(r => r.TestName, StringComparer.OrdinalIgnoreCase);
        var tests = flow.Tests
            .Select(t => (Test: t, Result: latest.TryGetValue(t.Name, out var hit) ? hit : null))
            .Select(x => (x.Test, x.Result, Standing: AssertionSetReport.TestOf(x.Test, x.Result)))
            .ToList();
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = flow.Name,
                ["ledger"] = flow.LedgerName,
                ["set"] = flow.Set,
                ["tests"] = new JsonArray(tests.Select(x =>
                {
                    var judged = x.Standing.AssertionRunId is null ? null : x.Result;
                    return (JsonNode)new JsonObject
                    {
                        ["test"] = x.Test.Name,
                        ["kind"] = x.Test.Kind,
                        ["standing"] = x.Standing.Standing,
                        ["why"] = x.Standing.Why,
                        ["assertionRunId"] = judged?.AssertionRunId,
                        ["matched"] = judged?.Matched,
                        ["failedAssertions"] = judged?.FailedAssertions,
                        ["completedUtc"] = judged?.CompletedUtc,
                        ["changed"] = x.Standing.Changed,
                    };
                }).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{flow.LedgerName}: {flow.Tests.Count} test(s){(flow.Set is { } set ? $", set {set}" : string.Empty)}"));
        foreach (var (test, result, standing) in tests)
        {
            var word = standing.Standing == SetStandings.NotRun ? "not run" : standing.Standing;
            if (standing.AssertionRunId is null || result is null)
            {
                context.Out.WriteLine($"  {word,-9}  {test.Name}  ({test.Kind})");
                if (standing.Why is { } why)
                {
                    context.Out.WriteLine("             " + why);
                }

                continue;
            }

            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {word,-9}  {test.Name}  ({test.Kind})  matched {result.Matched?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}, {result.FailedAssertions} of {result.Assertions} assertion(s) failed, run {result.AssertionRunId} at {Stamp(result.CompletedUtc)}"));
            if (result.Error is { Length: > 0 } error)
            {
                context.Out.WriteLine("             " + error);
            }
        }

        return 0;
    }

    /// <summary>A run's full report (the latest run, or the one --run names) in the format --format names, to --out or the console.</summary>
    private static async Task<int> ReportAsync(CliVerbContext context, ILedger ledger, TimeProvider time, AssertionFlowDefinition flow, CancellationToken ct)
    {
        var format = AssertionReport.FormatOf(context.Arguments.GetOption("--format"))
            ?? throw new FlowValidationException($"--format '{context.Arguments.GetOption("--format")}' is not one of json, md, html, junit.");
        AssertionRunState? run;
        if (context.Arguments.GetOption("--run") is { } asked)
        {
            if (!long.TryParse(asked, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return context.UsageError($"--run '{asked}' is not a report number.");
            }

            run = await ledger.GetAssertionRunAsync(id, ct).ConfigureAwait(false)
                ?? throw new FlowValidationException($"No assertion run {id}.");
            if (run.FlowId != flow.LedgerId)
            {
                throw new FlowValidationException($"Assertion run {id} is a run of {run.FlowName} in {run.Partition}, not of {flow.LedgerName}.");
            }
        }
        else
        {
            var runs = await ledger.ListAssertionRunsAsync(flow.LedgerId, 1, ct).ConfigureAwait(false);
            run = runs.Count > 0 ? runs[0] : throw new FlowValidationException($"{flow.LedgerName} has no run to report yet. Run its tests with: sqlflow run <flow.yaml>");
        }

        var results = (await ledger.ListAssertionResultsAsync(run.AssertionRunId, ct).ConfigureAwait(false))
            .Select(r => TestResults.Deserialize(r.Detail))
            .Where(r => r is not null)
            .Cast<TestResult>()
            .ToList();
        var text = new AssertionReport(run, results, time.GetUtcNow().UtcDateTime).Render(format);
        if (context.Arguments.GetOption("--out") is { } outPath)
        {
            var full = Path.GetFullPath(outPath);
            if (Path.GetDirectoryName(full) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(full, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
            context.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote the report of run {run.AssertionRunId} ({run.Status}) to {full}"));
        }
        else
        {
            context.Out.Write(text);
        }

        return 0;
    }

    private static JsonObject Described(AssertionRunState run) => new()
    {
        ["assertionRunId"] = run.AssertionRunId,
        ["runId"] = run.RunId?.ToString("D"),
        ["partition"] = run.Partition,
        ["status"] = run.Status,
        ["tests"] = run.Counts.Tests,
        ["passed"] = run.Counts.Passed,
        ["failed"] = run.Counts.Failed,
        ["warned"] = run.Counts.Warned,
        ["errored"] = run.Counts.Errored,
        ["skipped"] = run.Counts.Skipped,
        ["actor"] = run.Actor,
        ["startedUtc"] = run.StartedUtc,
        ["completedUtc"] = run.CompletedUtc,
        ["error"] = run.Error,
    };

    private static int Count(string? value, int fallback, string option)
    {
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is > 0 and <= 1000
            ? count
            : throw new FlowValidationException($"{option} '{value}' is not a count between 1 and 1000.");
    }

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
