using System.Globalization;
using System.Text.Json.Nodes;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Reversals;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Cli;

/// <summary>
/// <c>sqlflow records reverse</c> and <c>sqlflow records reversals</c>
/// (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission): what reversing a run or a
/// submission would reach, the reversal itself, run in this process as a reverse run of the flow runs on a node (the same
/// runtime, the same ledger writes, recorded as <c>cli:&lt;user&gt;@&lt;machine&gt;</c>'s), and the reversals the ledger
/// keeps with their counts.
/// </summary>
internal static class DeliveryReversalVerbs
{
    /// <summary>Reversals listed when the command line asks for no count.</summary>
    private const int DefaultReversals = 20;

    /// <summary>Records of one outcome named when the command line asks for no count.</summary>
    private const int DefaultRecords = 20;

    /// <summary>
    /// Reverses what <c>--run</c> or <c>--submission</c> delivered to the interface's OSDU, or with <c>--preview</c> says what
    /// it would reach and do, writing nothing.
    /// </summary>
    public static async Task<int> ReverseAsync(CliVerbContext context, ILedger ledger, EngineContext engine, FlowDefinition flow, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (SourceOf(context) is not { } source)
        {
            return context.UsageError("name what to reverse: --run <run id> or --submission <submission id>, one of them.");
        }

        if (context.Arguments.HasFlag("--preview"))
        {
            var preview = await ReversalPreview.ReadAsync(ledger, flow, source, null, ReversalLimits.PreviewSample, ct).ConfigureAwait(false);
            return WritePreview(context, label, preview);
        }

        using var runtime = CliInterventions.Runtime(engine, flow);
        var summary = await runtime.ReverseAsync(source, ct).ConfigureAwait(false);
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["flowId"] = flow.Id.ToString(),
                ["reversalId"] = summary.ReversalId,
                ["source"] = source.Kind,
                ["sourceId"] = source.Id.ToString("D"),
                ["records"] = summary.Records,
                ["taken"] = summary.Taken,
                ["restored"] = summary.Restored,
                ["removed"] = summary.Removed,
                ["skipped"] = summary.Skipped,
                ["failed"] = summary.Failed,
                ["outcomes"] = Counts(summary.Counts.Outcomes),
            }));
            return summary.Failed > 0 ? 1 : 0;
        }

        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: reversal {summary.ReversalId} of {source}: this run took {summary.Taken:N0} of {summary.Records:N0} record(s): {summary.Restored:N0} restored, {summary.Removed:N0} removed, {summary.Skipped:N0} passed over, {summary.Failed:N0} failed."));
        context.Out.WriteLine($"  across every run: {summary.Counts}");
        if (summary.Failed > 0)
        {
            context.Out.WriteLine($"  The failed records are taken again when the reversal is asked again; 'records reversals --{source.Kind} {source.Id:D} --outcome failed' names them.");
        }

        return summary.Failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// The interface's reversals, newest first; or the reversal of <c>--run</c> or <c>--submission</c> with its counts, and with
    /// <c>--outcome</c> the records it settled that way.
    /// </summary>
    public static async Task<int> ListAsync(CliVerbContext context, ILedger ledger, FlowDefinition flow, string label, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (SourceOf(context) is not { } source)
        {
            if (context.Arguments.GetOption("--run") is not null || context.Arguments.GetOption("--submission") is not null)
            {
                return context.UsageError("name one reversal by --run or by --submission, not both.");
            }

            var max = Max(context, DefaultReversals);
            var reversals = await ledger.ListReversalsAsync(flow.Id, max, ct).ConfigureAwait(false);
            if (context.Json)
            {
                context.Out.WriteLine(CanonicalJson.Pretty(new JsonArray(reversals.Select(r => (JsonNode)Describe(r, null)).ToArray())));
                return 0;
            }

            if (reversals.Count == 0)
            {
                context.Out.WriteLine($"{label}: no reversal was asked for.");
                return 0;
            }

            foreach (var reversal in reversals)
            {
                context.Out.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{reversal.ReversalId,6}  {reversal.Status,-10}  {reversal.Source}  asked by {reversal.RequestedBy} at {reversal.RequestedUtc:u}"));
            }

            return 0;
        }

        var found = await ledger.FindReversalAsync(flow.Id, source, ct).ConfigureAwait(false);
        if (found is null)
        {
            context.Out.WriteLine($"{label}: no reversal of {source} was asked for.");
            return 1;
        }

        var counts = await ledger.CountReversalAsync(found.ReversalId, ct).ConfigureAwait(false);
        var outcome = context.Arguments.GetOption("--outcome");
        var items = outcome is null ? [] : await ledger.ListReversalItemsAsync(found.ReversalId, outcome, null, Max(context, DefaultRecords), ct).ConfigureAwait(false);
        if (context.Json)
        {
            var described = Describe(found, counts);
            if (outcome is not null)
            {
                described["records"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
                {
                    ["deliveryKey"] = i.DeliveryKey.ToString(),
                    ["targetId"] = i.TargetId,
                    ["outcome"] = i.Outcome,
                    ["detail"] = i.Detail,
                    ["restoredVersion"] = i.RestoredVersion,
                    ["newVersion"] = i.NewVersion,
                }).ToArray());
            }

            context.Out.WriteLine(CanonicalJson.Pretty(described));
            return 0;
        }

        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: reversal {found.ReversalId} of {found.Source}, {found.Status}; asked by {found.RequestedBy} at {found.RequestedUtc:u}{(found.CompletedUtc is { } done ? $", last run ended {done:u}" : string.Empty)}"));
        context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {counts.Records:N0} record(s): {counts}"));
        if (found.Error is { } error)
        {
            context.Out.WriteLine($"  stopped: {error}");
        }

        foreach (var item in items)
        {
            context.Out.WriteLine($"  {item.DeliveryKey}  {item.TargetId}  {item.Outcome}: {item.Detail}");
        }

        return 0;
    }

    private static int WritePreview(CliVerbContext context, string label, ReversalPreview preview)
    {
        if (context.Json)
        {
            context.Out.WriteLine(CanonicalJson.Pretty(new JsonObject
            {
                ["flow"] = label,
                ["source"] = preview.Source.Kind,
                ["sourceId"] = preview.Source.Id.ToString("D"),
                ["submissions"] = preview.Submissions.Count,
                ["records"] = preview.Records,
                ["sampled"] = preview.Sampled,
                ["restore"] = preview.Restore,
                ["remove"] = preview.Remove,
                ["resolvedFromOsdu"] = preview.ResolvedFromOsdu,
                ["passedOver"] = Counts(preview.PassedOver.ToDictionary(p => p.Key, p => (long)p.Value, StringComparer.Ordinal)),
                ["restores"] = preview.Route.Restore,
                ["removes"] = preview.Route.Remove,
                ["reversalId"] = preview.Existing?.ReversalId,
            }));
            return 0;
        }

        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: {preview.Source} delivered {preview.Records:N0} record(s) under {preview.Submissions.Count} submission(s)."));
        var of = preview.SampleIsAll ? "Of them" : string.Create(CultureInfo.InvariantCulture, $"Of the first {preview.Sampled:N0} in key order");
        context.Out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {of}: {preview.Restore:N0} would be restored to the version OSDU held before, {preview.Remove:N0} removed (it created them), {preview.ResolvedFromOsdu:N0} decided by OSDU's version list."));
        foreach (var (outcome, count) in preview.PassedOver.OrderByDescending(p => p.Value))
        {
            context.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {count:N0} would be passed over: {outcome}"));
        }

        context.Out.WriteLine($"  restore: {preview.Route.Restore}");
        context.Out.WriteLine($"  remove:  {preview.Route.Remove}");
        if (preview.Existing is { } existing)
        {
            context.Out.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  Reversal {existing.ReversalId} of this source is {existing.Status}: {preview.ExistingCounts}. Asking again resumes it."));
        }

        context.Out.WriteLine("  Nothing was written. Run without --preview to reverse.");
        return 0;
    }

    /// <summary>The source the command line names: <c>--run</c> or <c>--submission</c>, exactly one; null otherwise.</summary>
    private static ReversalSource? SourceOf(CliVerbContext context)
    {
        var run = context.Arguments.GetOption("--run");
        var submission = context.Arguments.GetOption("--submission");
        return (run, submission) switch
        {
            ({ } r, null) => ReversalSource.Run(Id(r, "--run")),
            (null, { } s) => ReversalSource.Submission(Id(s, "--submission")),
            _ => null,
        };
    }

    private static Guid Id(string value, string option)
        => Guid.TryParse(value, CultureInfo.InvariantCulture, out var id) && id != Guid.Empty
            ? id
            : throw new FlowValidationException($"{option} '{value}' is not an id: the run's or the submission's UUID.");

    private static int Max(CliVerbContext context, int fallback)
    {
        var text = context.Arguments.GetOption("--max");
        if (text is null)
        {
            return fallback;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var max) && max > 0
            ? max
            : throw new FlowValidationException($"--max '{text}' is not a positive whole number.");
    }

    private static JsonObject Describe(ReversalState reversal, ReversalCounts? counts)
    {
        var described = new JsonObject
        {
            ["reversalId"] = reversal.ReversalId,
            ["source"] = reversal.Source.Kind,
            ["sourceId"] = reversal.Source.Id.ToString("D"),
            ["status"] = reversal.Status,
            ["requestedBy"] = reversal.RequestedBy,
            ["requestedUtc"] = reversal.RequestedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["completedUtc"] = reversal.CompletedUtc?.ToString("O", CultureInfo.InvariantCulture),
            ["error"] = reversal.Error,
        };
        if (counts is not null)
        {
            described["recordCount"] = counts.Records;
            described["outcomes"] = Counts(counts.Outcomes);
        }

        return described;
    }

    private static JsonObject Counts(IReadOnlyDictionary<string, long> counts)
    {
        var json = new JsonObject();
        foreach (var (name, count) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            json[name] = count;
        }

        return json;
    }
}
