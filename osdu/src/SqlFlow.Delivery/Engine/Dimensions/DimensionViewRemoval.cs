using System.Text.Json;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// Removes a view no dimension flow declares any more, for good (osdu/docs/reference/cli/dimensions.md, remove-view):
/// the view from the database and its record with its checks. A flow that still declares a view writes it again on its
/// next build, so only a view whose flow is gone, or no longer declares it, is removed here; the API (an admin's) and
/// the CLI both remove through here, and every removal is an activity of the flow's ledger, with who asked and what
/// went, recorded before anything is dropped and completed as it ends.
/// </summary>
public static class DimensionViewRemoval
{
    /// <summary>The activity a removal is recorded as.</summary>
    public const string ActivityKind = "remove-view";

    /// <summary>
    /// Removes <paramref name="view"/>, which no flow declares (the caller has settled that, from the flow as its pipeline
    /// or its file declares it now), on behalf of <paramref name="actor"/>.
    /// </summary>
    /// <exception cref="DeliveryException">The view is no longer recorded, its flow's ledger is gone, or a build held its lock past the timeout.</exception>
    public static async Task<DimensionViewRemoved> RemoveAsync(ILedger ledger, DimensionViewState view, string actor, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentNullException.ThrowIfNull(clock);

        var activity = await ledger.StartActivityAsync(
            new ActivityRecord
            {
                FlowId = view.LedgerId,
                FlowName = view.FlowName,
                Kind = ActivityKind,
                Actor = actor,
                StartedUtc = clock.GetUtcNow().UtcDateTime,
                ParametersJson = JsonSerializer.Serialize(new { view = view.Name, viewName = view.ViewName }),
            },
            ct).ConfigureAwait(false);
        try
        {
            var removed = await ledger.Dimensions.RemoveViewAsync(view.Name, ct).ConfigureAwait(false)
                ?? throw new DeliveryException($"View {view.Name} of {view.FlowName} is no longer recorded: another removal, or its flow's build, took it first.");
            await ledger.CompleteActivityAsync(activity.ActivityId, "completed", removed.Describe(), null, clock.GetUtcNow().UtcDateTime, ct: CancellationToken.None).ConfigureAwait(false);
            return removed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ledger.CompleteActivityAsync(
                activity.ActivityId, "cancelled", "Cancelled part way; removing the view again finishes the work.", null, clock.GetUtcNow().UtcDateTime,
                ct: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await ledger.CompleteActivityAsync(activity.ActivityId, "failed", SecretHygiene.RedactedMessage(ex), null, clock.GetUtcNow().UtcDateTime, ct: CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
