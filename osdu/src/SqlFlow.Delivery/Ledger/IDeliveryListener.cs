namespace SqlFlow.Delivery.Ledger;

/// <summary>One thing that happened to a record or a submission, as seen by a listener.</summary>
public sealed record DeliveryEvent
{
    public required DateTime AtUtc { get; init; }

    public required Guid FlowId { get; init; }

    /// <summary>The flow as the ledger records it: its name, and <c>/interface</c> for an interface of a source.</summary>
    public required string FlowName { get; init; }

    /// <summary>The interface of its source the event belongs to, or null for a flow in the single form.</summary>
    public string? Interface { get; init; }

    /// <summary>record.delivered, record.unchanged, record.retry, record.held, record.failed, record.waiting, record.released, record.adoption-asked, record.adopted, batch.progress, batch.completed, submission.planned, submission.completed, verify.completed.</summary>
    public required string Kind { get; init; }

    public Guid? SubmissionId { get; init; }

    public Identity.DeliveryKey? DeliveryKey { get; init; }

    public string? SourceKey { get; init; }

    public string? Label { get; init; }

    public string? TargetId { get; init; }

    public long? TargetVersion { get; init; }

    public string? Worker { get; init; }

    public string? Phase { get; init; }

    public TimeSpan? Duration { get; init; }

    public string? Detail { get; init; }

    /// <summary>The attempt result JSON (the steps and what the target returned), for record-level events.</summary>
    public string? Returned { get; init; }
}

/// <summary>
/// The completion callback of the fan-out: invoked after every record completes (delivered, retry scheduled, held,
/// failed or released) and after every submission and verify pass closes. Implementations log, publish metrics,
/// feed a live view or call a webhook. They must not throw; the ledger row is already written when they run.
/// </summary>
public interface IDeliveryListener
{
    ValueTask OnEventAsync(DeliveryEvent evt, CancellationToken ct = default);
}

/// <summary>Fans one event out to several listeners, isolating each from the others' failures.</summary>
public sealed class CompositeDeliveryListener : IDeliveryListener
{
    private readonly IReadOnlyList<IDeliveryListener> _listeners;

    public CompositeDeliveryListener(IEnumerable<IDeliveryListener> listeners)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        _listeners = listeners.ToList();
    }

    public static CompositeDeliveryListener Empty { get; } = new([]);

    public async ValueTask OnEventAsync(DeliveryEvent evt, CancellationToken ct = default)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                await listener.OnEventAsync(evt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A listener is an observer. Its failure is its own problem, never the delivery's.
                _ = ex;
            }
        }
    }
}
