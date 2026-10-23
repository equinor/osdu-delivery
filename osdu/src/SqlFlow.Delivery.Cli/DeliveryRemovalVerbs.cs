using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// The <c>removal</c> verb, and the <c>records</c> subcommands about removed keys (osdu/docs/reference/cli/removal.md): a removal
/// flow's runs where an operator already is, without a control plane to reach. <c>runs</c> lists a removal flow's runs in a
/// partition, <c>run</c> shows one with its tallies; <c>records removed</c> pages through a delivery flow's deletions, and
/// <c>records lift-removal</c> lifts the deletion of records so the next run delivers them again. A removal itself is a run like
/// any other: <c>sqlflow run &lt;removal.yaml&gt; --set partition=&lt;id&gt;</c>, and so is an approval, with the payload the
/// removal flow's page sends.
/// </summary>
internal static class DeliveryRemovalVerbs
{
    /// <summary>Runs listed when the command line asks for no count.</summary>
    private const int DefaultRuns = 20;

    /// <summary>The most runs one listing shows.</summary>
    private const int MaxRuns = 200;

    /// <summary>Removed keys listed when the command line asks for no count.</summary>
    private const int DefaultKeys = 50;

    /// <summary>The most removed keys one page lists.</summary>
    private const int MaxKeys = 1000;

    public static async Task<int> RemovalAsync(CliVerbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.CancellationToken;
        var verb = context.Arguments.Positional(1)?.ToLowerInvariant() ?? string.Empty;
        if (verb is not ("runs" or "run"))
        {
            return context.UsageError("say what to show: runs or run.");
        }

        var engine = context.Services.GetRequiredService<EngineContext>();
        var ledger = engine.Ledger
            ?? throw new FlowValidationException(
                "Removal runs live in the module's database. Run 'sqlflow removal' with --db <conn-ref>, or set the catalog variable.");
        if (verb == "run")
        {
            if (context.Arguments.Positional(2) is not { } partition || context.Arguments.Positional(3) is not { } number
                || !long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var runNumber) || runNumber < 1)
            {
                return context.UsageError("name the partition and the run's number, as 'sqlflow removal runs' shows it: sqlflow removal run <partition> <number>.");
            }

            var run = await ledger.GetRemovalRunAsync(partition.Trim(), runNumber, ct).ConfigureAwait(false)
                ?? throw new FlowValidationException($"No removal run {runNumber} in partition '{partition.Trim()}'.");
            Write(context, [run], $"removal run {runNumber}");
            return 0;
        }

        if (context.Arguments.Positional(2) is not { } flowPath)
        {
            return context.UsageError("name the removal flow document: sqlflow removal runs <removal.yaml>.");
        }

        var flow = engine.Documents.LoadRemoval(flowPath);
        var bound = await CliPartitions.RemovalAsync(context, flow, ct).ConfigureAwait(false);
        var entry = context.Arguments.GetOption("--removal") is { } named
            ? bound.Entry(named)?.Name ?? throw new FlowValidationException($"Removal flow '{flow.Name}' declares no removal named '{named}'; it declares {string.Join(", ", flow.Entries.Select(e => e.Name))}.")
            : null;
        var max = Count(context.Arguments.GetOption("--max"), DefaultRuns, MaxRuns, "--max");
        var runs = await ledger.ListRemovalRunsAsync(bound.Partition!, flow.Name, entry, max, ct).ConfigureAwait(false);
        Write(context, runs, $"{flow.Name} in {bound.Partition}");
        return 0;
    }

    /// <summary>A delivery flow's deletions: the keys its source removed, a page at a time in key order, of one state with --state.</summary>
    public static async Task<int> RemovedAsync(CliVerbContext context, ILedger ledger, Guid flowId, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);
        var state = context.Arguments.GetOption("--state");
        if (state is not null && !SourceRemovals.IsState(state))
        {
            return context.UsageError($"--state '{state}' is not a state a removed key is in; it is one of {string.Join(", ", SourceRemovals.States)}.");
        }

        DeliveryKey? after = context.Arguments.GetOption("--after") is { } text
            ? Guid.TryParse(text, CultureInfo.InvariantCulture, out var key) ? new DeliveryKey(key) : throw new FlowValidationException($"--after '{text}' is not a delivery key.")
            : null;
        var max = Count(context.Arguments.GetOption("--max"), DefaultKeys, MaxKeys, "--max");
        var keys = await ledger.ListRemovedKeysAsync(flowId, state, null, after, max + 1, ct).ConfigureAwait(false);
        var counts = await ledger.RemovedKeyCountsAsync(flowId, ct).ConfigureAwait(false);
        var page = keys.Take(max).ToList();
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flowId.ToString(),
                ["counts"] = Counts(counts),
                ["removed"] = new JsonArray(page.Select(k => (JsonNode)Described(k)).ToArray()),
                ["next"] = keys.Count > max ? page[^1].Key.ToString() : null,
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{label}: {counts.GetValueOrDefault(SourceRemovals.Pending)} pending, {counts.GetValueOrDefault(SourceRemovals.AwaitingApproval)} waiting for approval, {counts.GetValueOrDefault(SourceRemovals.Done)} removed"));
        foreach (var removed in page)
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {removed.Key.Value:N}  {removed.State,-17}  {removed.SourceKeyJson ?? "(no key tuple)"}"));
            var origin = removed.FileName is null ? string.Empty : $" ({removed.FileName}{(removed.RowNumber is { } row ? $", row {row}" : string.Empty)})";
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"      {removed.Reason} as of {Stamp(removed.RemovedUtc)}{origin}; found by {removed.RemovalFlow} ({removed.Entry}, run {removed.DetectedRunId}){(removed.Outcome is null ? string.Empty : $"; {removed.Outcome} after {removed.Tries} try(ies)")}"));
            if (removed.Detail is { Length: > 0 } detail)
            {
                context.Out.WriteLine("      " + detail);
            }
        }

        if (keys.Count > max)
        {
            context.Out.WriteLine($"  more: --after {page[^1].Key.Value:D}");
        }

        return 0;
    }

    /// <summary>Lifts the deletion of the records --key names, as the local account: the flow's next run delivers each again.</summary>
    public static async Task<int> LiftAsync(CliVerbContext context, EngineContext engine, FlowDefinition flow, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(flow);
        var asked = context.Arguments.GetOptions("--key").ToList();
        if (asked.Count == 0)
        {
            return context.UsageError("name the records whose removal to lift with --key <delivery key>; 'records removed' lists them.");
        }

        var keys = asked
            .Select(value => Guid.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
                ? new DeliveryKey(parsed)
                : throw new FlowValidationException($"--key '{value}' is not a delivery key; 'records removed' lists them."))
            .Distinct()
            .ToList();
        using var runtime = FlowRuntime.ForTarget(flow.Interface is null ? engine : engine.ForInterface(flow.Interface), flow);
        runtime.Actor = RunActors.LocalAccount();
        var done = await runtime.LiftRemovalsAsync(keys, ct).ConfigureAwait(false);
        var lifted = keys.Where(done.Contains).Select(k => k.ToString()).ToList();
        var not = keys.Where(k => !done.Contains(k)).Select(k => k.ToString()).ToList();

        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flow.Id.ToString(),
                ["lifted"] = new JsonArray(lifted.Select(k => (JsonNode)k).ToArray()),
                ["notRemoved"] = new JsonArray(not.Select(k => (JsonNode)k).ToArray()),
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{label}: lifted the removal of {lifted.Count} record(s); the flow's next run delivers them again."));
        foreach (var key in not)
        {
            context.Out.WriteLine($"  {key}: the ledger keeps its key out for no removal");
        }

        return not.Count == 0 ? 0 : 1;
    }

    private static void Write(CliVerbContext context, IReadOnlyList<RemovalRunState> runs, string title)
    {
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["title"] = title,
                ["runs"] = new JsonArray(runs.Select(r => (JsonNode)Described(r)).ToArray()),
            }));
            return;
        }

        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{title}: {runs.Count} run(s)"));
        if (runs.Count == 0)
        {
            context.Out.WriteLine("  none yet. A removal is a run: sqlflow run <removal.yaml> --set partition=<id>");
        }

        foreach (var run in runs)
        {
            var t = run.Tally;
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {run.RemovalRunId,6}  {run.Status,-10}  {run.Operation,-6}  {run.Entry} -> {run.DeliveryFlow}  {Stamp(run.StartedUtc)}  by {run.Actor}"));
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"          {t.SourceKeys} source key(s), {t.ListedKeys} listed, {t.IngestionKeys} in the table, {t.Compared} compared; {t.Detected} found removed ({t.AwaitingApproval} held), {t.CameBack} came back, {t.Lifted} lifted"));
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"          {t.Removed} removed from OSDU, {t.AlreadyGone} already gone, {t.Settled} settled, {t.KeptOut} kept out; {t.Referenced} referenced, {t.Shared} shared, {t.Busy} busy, {t.Failed} failed"));
            if (run.Error is { Length: > 0 } error)
            {
                context.Out.WriteLine("          " + error);
            }
        }
    }

    private static JsonObject Described(RemovalRunState run) => new()
    {
        ["partition"] = run.Partition,
        ["removalRunId"] = run.RemovalRunId,
        ["flow"] = run.FlowName,
        ["removal"] = run.Entry,
        ["deliveryFlow"] = run.DeliveryFlow,
        ["ledgerFlowId"] = run.LedgerFlowId.ToString(),
        ["runId"] = run.RunId?.ToString(),
        ["operation"] = run.Operation,
        ["status"] = run.Status,
        ["actor"] = run.Actor,
        ["startedUtc"] = Stamp(run.StartedUtc),
        ["completedUtc"] = run.CompletedUtc is { } done ? Stamp(done) : null,
        ["sourceReadUtc"] = run.SourceReadUtc is { } read ? Stamp(read) : null,
        ["rowsRead"] = run.Tally.RowsRead,
        ["sourceKeys"] = run.Tally.SourceKeys,
        ["listedKeys"] = run.Tally.ListedKeys,
        ["ingestionKeys"] = run.Tally.IngestionKeys,
        ["compared"] = run.Tally.Compared,
        ["detected"] = run.Tally.Detected,
        ["awaitingApproval"] = run.Tally.AwaitingApproval,
        ["approved"] = run.Tally.Approved,
        ["removed"] = run.Tally.Removed,
        ["alreadyGone"] = run.Tally.AlreadyGone,
        ["settled"] = run.Tally.Settled,
        ["keptOut"] = run.Tally.KeptOut,
        ["referenced"] = run.Tally.Referenced,
        ["shared"] = run.Tally.Shared,
        ["busy"] = run.Tally.Busy,
        ["failed"] = run.Tally.Failed,
        ["cameBack"] = run.Tally.CameBack,
        ["lifted"] = run.Tally.Lifted,
        ["guardPercent"] = run.GuardPercent,
        ["guardRecords"] = run.GuardRecords,
        ["approvedBy"] = run.ApprovedBy,
        ["error"] = run.Error,
    };

    private static JsonObject Described(RemovedKeyState key) => new()
    {
        ["deliveryKey"] = key.Key.ToString(),
        ["sourceKey"] = key.SourceKeyJson,
        ["record"] = key.RecordStatus,
        ["state"] = key.State,
        ["reason"] = key.Reason,
        ["removedUtc"] = Stamp(key.RemovedUtc),
        ["fileName"] = key.FileName,
        ["rowNumber"] = key.RowNumber,
        ["outcome"] = key.Outcome,
        ["detail"] = key.Detail,
        ["tries"] = key.Tries,
        ["removalFlow"] = key.RemovalFlow,
        ["removal"] = key.Entry,
        ["detectedRunId"] = key.DetectedRunId,
        ["doneRunId"] = key.DoneRunId,
        ["approvedBy"] = key.ApprovedBy,
        ["approvedUtc"] = key.ApprovedUtc is { } approved ? Stamp(approved) : null,
        ["createdUtc"] = Stamp(key.CreatedUtc),
        ["doneUtc"] = key.DoneUtc is { } done ? Stamp(done) : null,
    };

    private static JsonObject Counts(IReadOnlyDictionary<string, long> counts)
    {
        var json = new JsonObject();
        foreach (var state in SourceRemovals.States)
        {
            json[state] = counts.GetValueOrDefault(state);
        }

        return json;
    }

    private static int Count(string? value, int fallback, int max, string option)
    {
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 1 && count <= max
            ? count
            : throw new FlowValidationException(string.Create(CultureInfo.InvariantCulture, $"{option} '{value}' is not a count from 1 to {max}."));
    }

    private static string Stamp(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
