using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.Engine.FanOut;

/// <summary>What waiting for the parts of a split run came to: their state once every one ended, and the ones the waiting run stopped.</summary>
public sealed record FanOutWaitResult(FanOutState State, IReadOnlySet<Guid> Stopped);

/// <summary>
/// How a run that split its work over nodes waits for the parts it queued: a delivery's
/// (osdu/docs/reference/concepts/submissions.md, Many nodes) and a dimension build's
/// (osdu/docs/reference/flow/dimension-build.md, Spread over nodes). It reads their state on a pace, says how they are doing on
/// the run trace's pace, and stops the ones no node has started once none has moved for a while, so the waiting run does
/// their work itself rather than wait for a node the pool may never start.
/// </summary>
internal static class FanOutWait
{
    /// <summary>How often a run without a trace says how its parts are doing.</summary>
    private static readonly TimeSpan Progress = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Waits until every part of <paramref name="handle"/> has ended. One no node has started is not waited for for ever:
    /// once none is running and none has started or ended for <paramref name="stall"/> since <paramref name="since"/>, the
    /// ones still queued are stopped (the platform cancels a queued run outright, and asks one a node took meanwhile to
    /// stop) and named in <see cref="FanOutWaitResult.Stopped"/>, for the waiting run to do their work, which
    /// <paramref name="takeOver"/> says in the run log ("delivers their rows itself"). Every node of the pool can be holding
    /// work, the waiting run's own among them, and a pool at its most nodes starts no more, so waiting on would wait for ever.
    /// </summary>
    public static async Task<FanOutWaitResult> UntilEndedAsync(
        EngineContext context, ILogger log, FanOutHandle handle, DateTimeOffset since, TimeSpan stall, TimeSpan poll, string takeOver, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(takeOver);
        var dispatcher = context.Dispatcher;
        var lastProgress = context.Time.GetUtcNow();
        var lastChange = since;
        var seen = (Running: 0, Ended: 0);
        var stopping = false;
        IReadOnlySet<Guid> stopped = new HashSet<Guid>();
        while (true)
        {
            var state = await dispatcher.StateAsync(handle, ct).ConfigureAwait(false);
            if (state.AllTerminal)
            {
                log.LogInformation("The parts finished: {State}.", state);
                return new FanOutWaitResult(state, stopped);
            }

            var now = context.Time.GetUtcNow();
            var shape = (Running: state.Running, Ended: state.Members.Count(m => m.IsTerminal));
            if (shape != seen)
            {
                seen = shape;
                lastChange = now;
            }

            if (!stopping && state.Running == 0 && now - lastChange >= stall)
            {
                stopping = true;
                stopped = state.Members.Where(m => !m.IsTerminal).Select(m => m.RunId).ToHashSet();
                log.LogInformation(
                    "No node has started {Count} part(s) in {Seconds}s and none is running: every place in the pool holds work. This run stops them and {TakeOver}.",
                    stopped.Count, (int)(now - lastChange).TotalSeconds, takeOver);
                await dispatcher.CancelAsync(handle, ct).ConfigureAwait(false);

                // Asked again at once: the queued ones have ended by now, and one a node took meanwhile ends when it hears the stop.
                continue;
            }

            if (context.Trace is { } trace ? trace.ProgressDue("members", now) : now - lastProgress >= Progress)
            {
                lastProgress = now;
                log.LogInformation(RunTrace.Bounded, "Waiting for the parts: {State}.", state);
            }

            await Task.Delay(poll, context.Time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels the parts of <paramref name="handle"/> when the run that queued them ends without them: cancelled, or failed
    /// before it waited. A failure to cancel is logged, not thrown, since the run is already ending with its own outcome.
    /// </summary>
    public static async Task CancelAsync(EngineContext context, ILogger log, FanOutHandle handle)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(handle);
        try
        {
            await context.Dispatcher.CancelAsync(handle, CancellationToken.None).ConfigureAwait(false);
            log.LogInformation("Cancelled the parts of group {GroupId} with this run.", handle.GroupId);
        }
        catch (Exception ex) when (ex is DeliveryException or InvalidOperationException or System.Data.Common.DbException)
        {
            log.LogWarning("Could not cancel the parts of group {GroupId}: {Message}", handle.GroupId, HeaderRedaction.RedactMessage(ex.Message));
        }
    }
}
