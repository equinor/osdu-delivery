using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine;

/// <summary>Why a run reads what it reads of its scope (<see cref="ScopeReads"/>).</summary>
public enum ScopeReadReason
{
    /// <summary>No ledger keeps a watermark, so the whole scope is read.</summary>
    NoLedger,

    /// <summary>No whole-scope plan of the scope has completed yet, so the whole scope is read.</summary>
    NoWatermark,

    /// <summary>The mapping, template or parameters moved since the watermark was written, so the whole scope is read.</summary>
    RulesMoved,

    /// <summary>The rows changed since the watermark, less the flow's overlap.</summary>
    SinceWatermark,

    /// <summary>
    /// The first records of the scope, by key, which the flow's <c>source.record.maxRecords</c> or a payload's
    /// <c>maxRecords</c> limits a run to; the watermark stays where it is.
    /// </summary>
    FirstRecords,
}

/// <summary>What a run reads of its scope, and why: the scope, the selection, the watermark it went by, and the reason.</summary>
/// <param name="Scope">The scope's key (<see cref="Planner.ScopeKey"/>).</param>
/// <param name="Selection">The rows read: the whole scope, or the rows changed after a moment.</param>
/// <param name="Watermark">The scope's watermark, when the ledger holds one.</param>
/// <param name="Reason">Why the selection is what it is.</param>
public sealed record ScopeRead(string Scope, SourceSelection Selection, SourceWatermark? Watermark, ScopeReadReason Reason)
{
    /// <summary>The reason in words, for a check that reports what the next run would read.</summary>
    public string Why(FlowDefinition flow, RenderContext rendering)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(rendering);
        return Reason switch
        {
            ScopeReadReason.NoLedger => "no ledger keeps a watermark here, so a run reads every row in scope",
            ScopeReadReason.NoWatermark => $"no whole-scope plan of scope {Scope} has completed yet, so the next run reads every row in scope",
            ScopeReadReason.RulesMoved =>
                $"scope {Scope} was last planned with other rules than {rendering.MappingReference} renders with now (its mapping, template or parameters changed), so the next run reads every row in scope",
            ScopeReadReason.FirstRecords => string.Create(
                CultureInfo.InvariantCulture,
                $"{Selection.FirstOfSetting} limits every run of scope {Scope} to its first {Selection.FirstOf} record(s), read by key, so the scope's watermark stays where it is while it is set"),
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"scope {Scope} was planned through {Watermark!.UpdatedThroughUtc:o}; the next run reads the rows changed after {Selection.LowerUtc:o} (an overlap of {flow.Source.Incremental.OverlapSeconds}s)"),
        };
    }
}

/// <summary>
/// What a run of a delivery flow reads of its scope when nothing narrows it (no submission, no named records, no replan):
/// the rows changed since the scope's watermark, less the flow's overlap; or the whole scope when no ledger keeps a
/// watermark, when no whole-scope plan of the scope has completed yet, or when the rules it renders with (the mapping,
/// the template, the parameters) moved since the watermark was written, since rows that did not change still render
/// differently then and nothing else would reach them. A run limited to the first records of its scope (a payload's
/// <c>maxRecords</c>, the flow's <c>source.record.maxRecords</c>) reads those by key instead (<see cref="FirstRecords"/>).
/// One decision, for a run and for <c>sqlflow check --connect</c>, which reports what the next run would read.
/// </summary>
public static class ScopeReads
{
    /// <summary>What a run of <paramref name="flow"/> with <paramref name="values"/>, rendering with <paramref name="rendering"/>, reads of its scope.</summary>
    public static async Task<ScopeRead> DecideAsync(
        ILedger? ledger, FlowDefinition flow, IReadOnlyDictionary<string, string> values, RenderContext rendering, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(rendering);
        var scope = Planner.ScopeKey(values);
        if (ledger is null)
        {
            return new ScopeRead(scope, SourceSelection.Full(), null, ScopeReadReason.NoLedger);
        }

        var watermark = await ledger.GetWatermarkAsync(flow.Id, scope, ct).ConfigureAwait(false);
        if (watermark is null)
        {
            return new ScopeRead(scope, SourceSelection.Full(), null, ScopeReadReason.NoWatermark);
        }

        if (watermark.ContextHash is { } planned && !string.Equals(planned, rendering.RulesHash(), StringComparison.Ordinal))
        {
            return new ScopeRead(scope, SourceSelection.Full(), watermark, ScopeReadReason.RulesMoved);
        }

        // The overlap looks a little below the watermark again, for rows whose statement committed after the last read
        // fixed its upper bound: their update time is inside the window that has already been read.
        var lower = watermark.UpdatedThroughUtc.AddSeconds(-flow.Source.Incremental.OverlapSeconds);
        return new ScopeRead(scope, SourceSelection.Incremental(lower), watermark, ScopeReadReason.SinceWatermark);
    }

    /// <summary>
    /// How many of the first records of its scope a run reads, and the setting that says so: the payload's
    /// <c>maxRecords</c> when the run gives one, which stands in for the flow's on this run (more, fewer, or 0 for the whole
    /// scope), else the flow's <c>source.record.maxRecords</c>, a default every run of the scope takes. Null for a run of the
    /// whole scope, and for a run of named records or of a submission, which reads what it names.
    /// </summary>
    public static (int Count, string Setting)? FirstRecords(FlowDefinition flow, DeliveryRunPayload payload, SubmissionState? submission)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(payload);
        if (submission is not null || payload.SubmissionId is not null || payload.RecordKeys.Count > 0 || payload.Slices.Count > 0)
        {
            return null;
        }

        return (payload.MaxRecords, flow.Source.Record.MaxRecords) switch
        {
            (0, _) => null,
            ({ } asked, _) => (asked, DeliveryRunPayload.MaxRecordsProperty),
            (null, { } limit) => (limit, KeyPaths.Of(flow).Name("source.record.maxRecords")),
            _ => null,
        };
    }

    /// <summary>
    /// The first <paramref name="max"/> records of a run's scope, as <paramref name="setting"/> asks (<see cref="FirstRecords"/>),
    /// in the order a read of the scope pages in, as a read of those records by key: the run plans each as a run of every
    /// row would, and leaves the scope's watermark and the records asked to be planned again for a run of the whole scope.
    /// The keys come from the ingestion tables themselves, so the records need not be in the ledger yet, as a first run's
    /// are not.
    /// </summary>
    public static async Task<SourceSelection> FirstOfScopeAsync(IIngestionSource source, int max, string setting, ILogger log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        ArgumentNullException.ThrowIfNull(log);
        var keys = new List<KeyTuple>(max);
        await foreach (var key in source.ReadKeysAsync(max, ct).ConfigureAwait(false))
        {
            keys.Add(key.Key);
            if (keys.Count == max)
            {
                break;
            }
        }

        var selection = SourceSelection.FirstOfScope(keys, max, setting);
        log.LogInformation(
            "{Setting} {Max}: this run reads {Selection}, by key, and leaves the scope's watermark where it is, so a run of the whole scope still reads every row.",
            setting, max, selection.Describe());
        return selection;
    }
}
