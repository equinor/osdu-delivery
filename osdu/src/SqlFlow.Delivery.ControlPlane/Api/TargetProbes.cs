using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Diagnostics;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>The outcomes a settled probe is recorded and counted under; the vocabulary the metric's <c>outcome</c> tag uses.</summary>
public static class ProbeOutcomes
{
    /// <summary>The service answered under the flow's credentials.</summary>
    public const string Reachable = "reachable";

    /// <summary>The service refused the call or did not answer at all. The target is there to be alerted on.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The probe itself could not run: the flow file could not be read, a credential would not resolve, the host stopped while it ran.</summary>
    public const string Error = "error";

    /// <summary>Whoever asked stopped waiting before the target answered: a request abandoned, a host stopping.</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// One interface's target probe, run in this process and recorded: the one path an operator's "Probe target" (the GUI, the
/// API and the MCP tool all ask it through <c>POST /flows/{pipelineId}/probe</c>) and the scheduled probe
/// (<see cref="Background.ScheduledTargetProbeService"/>) both take, so what a schedule reports is what a button reports,
/// and either is an entry of the audit trail under who asked. The probe is <c>delivery-probe</c> under the flow's own
/// credentials, resolved with the central configuration of the flow's partition (<see cref="DirectOperationRunner"/>).
/// </summary>
/// <remarks>
/// <para>
/// Each probe registers the interface's ledger in the partition it delivers to, as a run does
/// (<see cref="LedgerRegistration"/>), opens a <c>probe</c> activity there under the actor (<c>user:alice</c>,
/// <c>service:schedule</c>), runs the probe and closes the activity with what it found: completed when the target answered,
/// failed when it refused or did not answer, or when the probe could not run, and cancelled when whoever asked stopped
/// waiting. The outcome is counted on <c>osdu_delivery.probes</c> and logged. A flow whose ledger cannot be placed (its
/// partition does not resolve here and its ledger was never registered, or its header now names another partition than
/// its ledger's) is not probed, since nothing could record it; the answer says why.
/// </para>
/// <para>
/// A probe answers within its own bounded wait (<see cref="TargetClients.Interactive"/>), so an activity still open
/// <see cref="UnfinishedAfter"/> after it started belongs to a host that stopped while it ran: the next probe of the same
/// interface closes it as unfinished before it starts, and a scheduled pass closes every ledger's
/// (<see cref="CloseUnfinishedAsync"/>).
/// </para>
/// </remarks>
internal static partial class TargetProbes
{
    /// <summary>The activity kind every probe is recorded under, scheduled or asked for.</summary>
    public const string ActivityKind = "probe";

    /// <summary>The activity outcome of a probe still running.</summary>
    public const string Running = "running";

    /// <summary>The logger category every probe reports its outcome under.</summary>
    public const string LogCategory = "SqlFlow.Delivery.ControlPlane.TargetProbes";

    /// <summary>
    /// How long after it started a probe's activity is known to be unfinished: well past the longest a probe can wait, which
    /// is two tries of at most <see cref="TargetClients.InteractiveTimeoutSeconds"/> for the token and two for the call.
    /// </summary>
    public static readonly TimeSpan UnfinishedAfter = TimeSpan.FromMinutes(15);

    /// <summary>Open probes one close reads, which is at most one per interface per stopped host.</summary>
    private const int MaxUnfinished = 1000;

    /// <summary>
    /// Probes <paramref name="flow"/>'s target for <paramref name="actor"/> and records what it found; answers with the probe's
    /// own result, or with the problem that stopped it.
    /// </summary>
    public static async Task<Results<ContentHttpResult, ProblemHttpResult>> RunAsync(
        CatalogDbContext db,
        ILedger ledger,
        DeliveryConfigStore config,
        DirectOperations direct,
        EngineContext engine,
        TimeProvider clock,
        ILoggerFactory loggers,
        DeliveryEndpoints.FlowContext flow,
        string actor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggers);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var log = loggers.CreateLogger(LogCategory);

        // The ledger the probe is recorded in is the one the interface's runs deliver into, placed as a run places it.
        LedgerEntry placed;
        try
        {
            var configured = await DeliveryEndpoints.ConfiguredAsync(engine, config, flow, ct).ConfigureAwait(false);
            placed = await LedgerRegistration.RegisterAsync(ledger, flow.Flow, configured.Secrets, keptWhenUnresolved: true, ct).ConfigureAwait(false);
        }
        catch (SqlFlowException ex)
        {
            var reason = Redacted(ex);
            LogNotRecorded(log, flow.Flow.Label, actor, reason);
            return TypedResults.Problem(
                detail: $"The probe of {flow.Flow.Label} was not run, because it could not be recorded in the flow's ledger: {reason}",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The probe could not be recorded");
        }

        // A probe of this interface a stopped host left open is closed first, so a probe asked for in a deployment that runs no
        // schedule leaves nothing open for good either.
        await CloseUnfinishedAsync(ledger, flow.FlowId, clock, log, ct).ConfigureAwait(false);
        var target = flow.Flow.Target;

        // What the activity records about the target is what the target view already shows: the endpoint as the document
        // declares it (a reference, never a resolved secret), the partition its ledger is kept under and the protocol.
        // Redacted regardless, since nothing the ledger stores is allowed to carry a credential.
        var parameters = Redacted(JsonSerializer.Serialize(new
        {
            pipelineId = flow.Pipeline.Id,
            @interface = flow.Flow.Interface,
            endpoint = target.Endpoint,
            partition = placed.Partition,
            protocol = DeliveryProtocols.Name(target.Protocol),
        }));

        var activity = await ledger.StartActivityAsync(
            new ActivityRecord
            {
                FlowId = flow.FlowId,
                FlowName = flow.Flow.Label,
                Kind = ActivityKind,
                Actor = actor,
                StartedUtc = clock.GetUtcNow().UtcDateTime,
                ParametersJson = parameters,
            },
            ct).ConfigureAwait(false);
        var probe = new OpenProbe(activity.ActivityId, flow.Flow.Label, placed.Partition, flow.Flow.Interface);

        Results<ContentHttpResult, ProblemHttpResult> answer;
        try
        {
            answer = await DirectOperationRunner.RunAsync(
                db, config, direct, flow, ProbeTargetOperation.OperationName, new Dictionary<string, string>(StringComparer.Ordinal), actor, loggers, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Whoever asked stopped waiting (a request abandoned, a host stopping): the probe is closed as cancelled, which
            // says nothing of the target either way.
            await SettleAsync(ledger, probe, actor, ProbeOutcomes.Cancelled, "the probe was stopped before the target answered", clock, log).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // A failure the probe does not describe itself is still this probe's outcome, recorded before it goes on up.
            await SettleAsync(ledger, probe, actor, ProbeOutcomes.Error, CouldNotRun(Redacted(ex)), clock, log).ConfigureAwait(false);
            throw;
        }

        var (outcome, summary) = answer.Result switch
        {
            ContentHttpResult { ResponseContent: { } json } => FromResult(json),
            ProblemHttpResult problem => (ProbeOutcomes.Error, CouldNotRun(Redacted(problem.ProblemDetails.Detail ?? problem.ProblemDetails.Title ?? "no reason was given"))),
            _ => (ProbeOutcomes.Error, "the probe answered with nothing to record"),
        };
        await SettleAsync(ledger, probe, actor, outcome, summary, clock, log).ConfigureAwait(false);
        return answer;
    }

    /// <summary>
    /// Closes a probe's activity with what it found, counts it on <c>osdu_delivery.probes</c>, and says so in the log. The
    /// audit trail's outcome is what an operator acts on, so a target that would not answer reads failed there (a probe
    /// stopped before it answered, cancelled); the metric's outcome tag keeps the cases apart.
    /// </summary>
    public static async Task SettleAsync(ILedgerActivities ledger, OpenProbe probe, string actor, string outcome, string summary, TimeProvider clock, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(log);
        var recorded = outcome switch
        {
            ProbeOutcomes.Reachable => "completed",
            ProbeOutcomes.Cancelled => "cancelled",
            _ => "failed",
        };
        await CloseAsync(ledger, probe, recorded, summary, clock, log).ConfigureAwait(false);
        DeliveryMetrics.ProbeSettled(probe.FlowLabel, probe.Partition, probe.Interface, outcome);
        if (outcome == ProbeOutcomes.Reachable)
        {
            LogReachable(log, probe.Where, actor, summary);
        }
        else
        {
            LogNotReachable(log, probe.Where, actor, outcome, summary);
        }
    }

    /// <summary>
    /// Closes the probes left open past <see cref="UnfinishedAfter"/>, scheduled or asked for, of one ledger
    /// (<paramref name="flowId"/>) or of every one (null): the host stopped while they ran, or they were queued for a node
    /// before probes ran in the control plane. Read from the ledger, so a restart loses none; none of them can report any
    /// more. A younger one may still be running, here or on another replica, and is left to finish. Returns how many it
    /// closed.
    /// </summary>
    public static async Task<int> CloseUnfinishedAsync(ILedgerActivities ledger, Guid? flowId, TimeProvider clock, ILogger log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(clock);
        var running = await ledger.ListActivitiesAsync(
            new ActivityQuery
            {
                FlowId = flowId,
                Kind = ActivityKind,
                Outcome = Running,
                UntilUtc = clock.GetUtcNow().UtcDateTime - UnfinishedAfter,
                Max = MaxUnfinished,
            },
            ct).ConfigureAwait(false);
        foreach (var activity in running)
        {
            ct.ThrowIfCancellationRequested();
            await SettleAsync(
                ledger,
                new OpenProbe(activity.ActivityId, activity.FlowName, activity.Partition, InterfaceOf(activity.ParametersJson)),
                activity.Actor,
                ProbeOutcomes.Error,
                "the probe did not finish: the control plane stopped while it ran, or it was queued for a node before probes ran in the control plane",
                clock,
                log).ConfigureAwait(false);
        }

        return running.Count;
    }

    /// <summary>The interface an open activity's parameters name; null for a flow in the single form, or parameters that cannot be read.</summary>
    private static string? InterfaceOf(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(parametersJson);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("interface", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Closes a probe's activity whatever stopped the caller: the write is the probe's last, so it is not cancelled with the
    /// request. An activity that went in the meantime (a prune, a restored database) is reported, not thrown.
    /// </summary>
    private static async Task CloseAsync(ILedgerActivities ledger, OpenProbe probe, string recorded, string summary, TimeProvider clock, ILogger log)
    {
        try
        {
            await ledger.CompleteActivityAsync(probe.ActivityId, recorded, summary, null, clock.GetUtcNow().UtcDateTime, ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (DeliveryException ex)
        {
            LogActivityGone(log, probe.ActivityId, probe.Where, Redacted(ex));
        }
    }

    /// <summary>A probe that could not run, in the words the audit trail records it with; the reason is redacted already.</summary>
    public static string CouldNotRun(string reason) => $"the probe could not run: {reason}";

    /// <summary>The probe's own result, as <c>delivery-probe</c> wrote it: reachable or not, with the status and the path it asked on.</summary>
    private static (string Outcome, string Summary) FromResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return (ProbeOutcomes.Error, "the probe finished without recording a result");
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            var reachable = root.TryGetProperty("reachable", out var answered) && answered.ValueKind == JsonValueKind.True;
            var status = root.TryGetProperty("status", out var code) && code.TryGetInt32(out var value) ? value : 0;
            var path = root.TryGetProperty("path", out var asked) ? asked.GetString() ?? string.Empty : string.Empty;
            var detail = root.TryGetProperty("detail", out var said) ? said.GetString() ?? string.Empty : string.Empty;
            var summary =
                $"{(reachable ? ProbeOutcomes.Reachable : ProbeOutcomes.Unreachable)}: {(status > 0 ? $"HTTP {status}" : "no answer")} " +
                $"at {(path.Length == 0 ? "the target" : path)}{(detail.Length == 0 ? string.Empty : $" ({detail})")}";
            return (reachable ? ProbeOutcomes.Reachable : ProbeOutcomes.Unreachable, Redacted(summary));
        }
        catch (JsonException ex)
        {
            return (ProbeOutcomes.Error, $"the probe's result could not be read: {Redacted(ex.Message)}");
        }
    }

    /// <summary>A message as it may be stored and shown: every resolved secret, token and key redacted out of it.</summary>
    public static string Redacted(string text) => HeaderRedaction.RedactMessage(SecretHygiene.RedactedMessage(text));

    /// <summary>An exception's messages as they may be stored and shown.</summary>
    public static string Redacted(Exception ex) => HeaderRedaction.RedactMessage(SecretHygiene.RedactedMessage(ex));

    /// <summary>A probe whose activity waits for its outcome.</summary>
    public sealed record OpenProbe(long ActivityId, string FlowLabel, string? Partition, string? Interface)
    {
        /// <summary>The flow and the partition whose target was probed, as a log line names them.</summary>
        public string Where => Partition is null ? FlowLabel : $"{FlowLabel}@{Partition}";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Probe of {Flow} by {Actor}: {Summary}")]
    private static partial void LogReachable(ILogger logger, string flow, string actor, string summary);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Probe of {Flow} by {Actor} settled {Outcome}: {Summary}")]
    private static partial void LogNotReachable(ILogger logger, string flow, string actor, string outcome, string summary);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Probe of {Flow} by {Actor} was not run, because it could not be recorded: {Reason}")]
    private static partial void LogNotRecorded(ILogger logger, string flow, string actor, string reason);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Probe activity {ActivityId} of {Flow} could not be closed: {Error}")]
    private static partial void LogActivityGone(ILogger logger, long activityId, string flow, string error);
}
