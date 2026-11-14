using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Node;

namespace SqlFlow.Delivery.ComputeUsage;

/// <summary>
/// The node's line to the control plane with the meter listening on it: every run and compute task a node is handed
/// arrives in a poll's answer and leaves in an outcome report, on a standalone node and on the control plane's own node
/// alike, so this is the one place all node work passes. It changes nothing a node sends or receives.
/// </summary>
public sealed class MeteredNodeTransport : INodeTransport
{
    private readonly INodeTransport _inner;
    private readonly ComputeUsageMeter _meter;

    public MeteredNodeTransport(INodeTransport inner, ComputeUsageMeter meter)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(meter);
        _inner = inner;
        _meter = meter;
    }

    public async Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct)
    {
        var response = await _inner.PollAsync(request, ct).ConfigureAwait(false);
        _meter.Polled(request, response);
        return response;
    }

    public Task<RunOutcomeStatus> ReportRunOutcomeAsync(Guid runId, RunOutcomeRequest request, CancellationToken ct)
    {
        // The work ended when the node set out to report it: a report that is retried, or that the control plane drops
        // because the lease moved on, reports work this node did all the same.
        _meter.RunEnded(runId, request);
        return _inner.ReportRunOutcomeAsync(runId, request, ct);
    }

    public Task<bool> ReportTaskOutcomeAsync(Guid taskId, TaskOutcomeRequest request, CancellationToken ct)
    {
        _meter.TaskEnded(taskId, request);
        return _inner.ReportTaskOutcomeAsync(taskId, request, ct);
    }

    public Task<string?> GetFlowVersionAsync(string contentHash, CancellationToken ct)
        => _inner.GetFlowVersionAsync(contentHash, ct);

    public Task<RunContextResponse> ResolveRunContextAsync(Guid runId, RunContextRequest request, CancellationToken ct)
        => _inner.ResolveRunContextAsync(runId, request, ct);

    public Task<bool> ReportTraceAsync(Guid runId, RunTraceBatch batch, CancellationToken ct)
        => _inner.ReportTraceAsync(runId, batch, ct);

    public Task<FanOutResponse> EnqueueFanOutAsync(Guid rootRunId, FanOutRequest request, CancellationToken ct)
        => _inner.EnqueueFanOutAsync(rootRunId, request, ct);

    public Task<FanOutStateResponse> GetFanOutStateAsync(Guid rootRunId, Guid groupId, FanOutFence fence, CancellationToken ct)
        => _inner.GetFanOutStateAsync(rootRunId, groupId, fence, ct);

    public Task<FanOutCancelResponse> CancelFanOutAsync(Guid rootRunId, Guid groupId, FanOutFence fence, CancellationToken ct)
        => _inner.CancelFanOutAsync(rootRunId, groupId, fence, ct);
}
