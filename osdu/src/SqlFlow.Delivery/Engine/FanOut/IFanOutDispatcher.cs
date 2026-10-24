using SqlFlow.Core.Runs;
using SqlFlow.Orchestration;

namespace SqlFlow.Delivery.Engine.FanOut;

/// <summary>The member runs a fan-out produced, as one run group.</summary>
public sealed record FanOutHandle(Guid GroupId, IReadOnlyList<Guid> RunIds);

/// <summary>One member run of a fan-out as the platform last journaled it.</summary>
public sealed record FanOutMemberState(Guid RunId, int Slot, string Status, string? Error, string? ResultJson)
{
    public bool IsTerminal => Status is not ("queued" or "running");

    public bool Succeeded => Status == "succeeded";
}

/// <summary>The state of a fan-out: every member and the counts a coordinator logs while it waits.</summary>
public sealed record FanOutState(IReadOnlyList<FanOutMemberState> Members)
{
    public bool AllTerminal => Members.All(m => m.IsTerminal);

    public int Queued => Members.Count(m => m.Status == "queued");

    public int Running => Members.Count(m => m.Status == "running");

    public int Succeeded => Members.Count(m => m.Succeeded);

    public int Failed => Members.Count(m => m.IsTerminal && !m.Succeeded);

    public override string ToString() => $"{Members.Count} member(s): {Succeeded} succeeded, {Failed} failed or cancelled, {Running} running, {Queued} queued";
}

/// <summary>
/// How a delivery run spreads its work across the fleet (osdu/docs/reference/concepts/submissions.md, Many nodes): it
/// asks the platform to queue runs of its own flow, one per part of its rows, reads their state while it waits, and takes
/// them with it when it is cancelled. One seam, one implementation: the platform hands the run an <see cref="IRunFanOut"/>
/// when the host it executes on can queue runs (the control plane and a node), and a run without one does all its work
/// itself.
/// </summary>
public interface IFanOutDispatcher
{
    /// <summary>False when this run cannot enqueue members; the run then works alone.</summary>
    bool Available { get; }

    /// <summary>
    /// Queues one run per parameter set; a re-executed run gets back the runs it already has. With
    /// <paramref name="wholeNode"/> each takes a node to itself: the platform hands it only to a node executing nothing else,
    /// and the autoscaler starts a node for each one no idle node takes.
    /// </summary>
    Task<FanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, bool wholeNode, CancellationToken ct = default);

    Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default);

    Task CancelAsync(FanOutHandle handle, CancellationToken ct = default);
}

/// <summary>The dispatcher of a run the platform handed a fan-out: the control plane and a node alike.</summary>
public sealed class RunFanOutDispatcher : IFanOutDispatcher
{
    private readonly IRunFanOut _fanOut;

    public RunFanOutDispatcher(IRunFanOut fanOut)
    {
        ArgumentNullException.ThrowIfNull(fanOut);
        _fanOut = fanOut;
    }

    public bool Available => true;

    public async Task<FanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, bool wholeNode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        var handle = await _fanOut.EnqueueAsync(members, wholeNode, ct).ConfigureAwait(false);
        return new FanOutHandle(handle.GroupId, handle.RunIds);
    }

    public async Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var members = await _fanOut.MembersAsync(new RunFanOutHandle(handle.GroupId, handle.RunIds), ct).ConfigureAwait(false);
        return new FanOutState(members.Select(m => new FanOutMemberState(m.RunId, m.Slot, m.Status, m.Error, m.ResultJson)).ToList());
    }

    public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return _fanOut.CancelAsync(new RunFanOutHandle(handle.GroupId, handle.RunIds), ct);
    }
}

/// <summary>The dispatcher of a run without a fan-out: never available, so the run does all its work itself.</summary>
public sealed class NoFanOutDispatcher : IFanOutDispatcher
{
    public static NoFanOutDispatcher Instance { get; } = new();

    public bool Available => false;

    public Task<FanOutHandle> EnqueueAsync(IReadOnlyList<RunParameters> members, bool wholeNode, CancellationToken ct = default)
        => throw new DeliveryException("This run was not given a fan-out, so it cannot queue runs of its flow; it does its work itself.");

    public Task<FanOutState> StateAsync(FanOutHandle handle, CancellationToken ct = default)
        => throw new DeliveryException("This run was not given a fan-out, so it has no member runs to read.");

    public Task CancelAsync(FanOutHandle handle, CancellationToken ct = default)
        => throw new DeliveryException("This run was not given a fan-out, so it has no member runs to cancel.");
}
