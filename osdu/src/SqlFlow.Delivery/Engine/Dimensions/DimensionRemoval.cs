using System.Text.Json;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// Removes a dimension its flow no longer declares, for good (osdu/docs/reference/flow/dimension.md, Removing a
/// dimension): everything the ledger keeps of it in its partition, and nothing else. The API (an admin's) and the CLI
/// both remove through here, so the rules and the audit trail are the same wherever a removal comes from: a dimension a
/// cache flow of its partition still captures is refused, naming the cache flow, and every removal is an activity of
/// its flow, with who asked and what went, recorded before anything is deleted and completed as it ends.
/// </summary>
public static class DimensionRemoval
{
    /// <summary>The activity a removal is recorded as.</summary>
    public const string ActivityKind = "remove-dimension";

    /// <summary>
    /// Removes <paramref name="dimension"/>, which its flow no longer declares (the caller has settled that, from the flow
    /// as its pipeline or its file declares it now), on behalf of <paramref name="actor"/>.
    /// </summary>
    /// <exception cref="DeliveryException">
    /// A cache flow of the dimension's partition captures it, it is no longer in the ledger, or a build held its write lock
    /// past the timeout.
    /// </exception>
    public static async Task<DimensionRemoved> RemoveAsync(ILedgerActivities ledger, DimensionStore dimensions, DimensionState dimension, string actor, TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(dimension);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentNullException.ThrowIfNull(clock);

        // A view reading the dimension's table would read a table that is gone: its flow's next build writes it without the
        // dimension, once the flow no longer joins it.
        if (dimension.TableName is { } table && await dimensions.ViewsReadingAsync(table, ct).ConfigureAwait(false) is { Count: > 0 } views)
        {
            throw new DeliveryException(
                $"Dimension {dimension.Name} of {dimension.FlowName} is read by view{(views.Count == 1 ? string.Empty : "s")} {string.Join(", ", views)}, so it is not removed: the view would read a table that is gone. Take the dimension out of the view's joins and build the flow, which writes the view without it, then remove the dimension.");
        }

        var captures = await dimensions.CapturesAsync(dimension.DimensionId, ct).ConfigureAwait(false);
        if (captures.Count > 0)
        {
            throw new DeliveryException(
                $"Dimension {dimension.Name} of {dimension.FlowName} is captured by {string.Join(", ", captures.Select(c => $"type {c.Type} of cache flow {c.CacheFlow}"))} in partition {captures[0].Partition}, so it is not removed: a refresh of that cache would read a dimension that is gone. Remove the type from the cache flow and sync the repository first.");
        }

        var activity = await ledger.StartActivityAsync(
            new ActivityRecord
            {
                FlowId = dimension.FlowId,
                FlowName = dimension.FlowName,
                Kind = ActivityKind,
                Actor = actor,
                StartedUtc = clock.GetUtcNow().UtcDateTime,
                ParametersJson = JsonSerializer.Serialize(new { dimensionId = dimension.DimensionId, dimension = dimension.Name, partition = dimension.Partition }),
            },
            ct).ConfigureAwait(false);
        try
        {
            var removed = await dimensions.RemoveAsync(dimension.DimensionId, ct).ConfigureAwait(false)
                ?? throw new DeliveryException($"Dimension {dimension.Name} of {dimension.FlowName} is no longer in the ledger: another removal took it first.");
            await ledger.CompleteActivityAsync(activity.ActivityId, "completed", removed.Describe(), null, clock.GetUtcNow().UtcDateTime, ct: CancellationToken.None).ConfigureAwait(false);
            return removed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ledger.CompleteActivityAsync(
                activity.ActivityId, "cancelled", "Cancelled part way; removing the dimension again finishes the work.", null, clock.GetUtcNow().UtcDateTime,
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
