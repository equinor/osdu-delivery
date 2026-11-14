using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Delivery.ComputeUsage;

/// <summary>
/// Measures the work this process's node does (osdu/docs/reference/concepts/compute-usage.md, How work is measured): the
/// wall-clock time from the poll that handed a run or a compute task to the node until the node reports its outcome, on
/// the node's own monotonic clock, with the node's cores and slots at the time. <see cref="MeteredNodeTransport"/> tells
/// it what the node is handed and what it reports, and <see cref="ComputeUsageRecorder"/> writes what it measured.
/// </summary>
/// <remarks>
/// <para>
/// Work the node gives up without an outcome is measured too, as <see cref="ComputeOutcomes.Interrupted"/>: a run whose
/// lease the control plane revokes ends with the poll that says so, and anything else the node no longer says it holds
/// ends with its next poll. What a node that dies outright was doing is not measured: nothing is left to say how long
/// it ran.
/// </para>
/// <para>
/// Measuring never fails the work and never waits for the database: an ended piece of work is put on a bounded queue
/// the recorder drains. When the queue is full, because the database has not taken a write for that long, the measurement
/// is dropped and <see cref="Dropped"/> counts it, which the recorder reports.
/// </para>
/// </remarks>
public sealed class ComputeUsageMeter
{
    /// <summary>How many ended pieces of work wait for the database at most.</summary>
    public const int Capacity = 10_000;

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<HeldKey, Held> _held = new();
    private readonly Channel<ComputeWork> _measured;
    private long _dropped;

    /// <summary>The meter of a node with this machine's cores.</summary>
    public ComputeUsageMeter(TimeProvider clock)
        : this(clock, Environment.ProcessorCount, Capacity)
    {
    }

    /// <summary>The meter of a node of <paramref name="cores"/> cores, queueing at most <paramref name="capacity"/> measurements.</summary>
    internal ComputeUsageMeter(TimeProvider clock, int cores, int capacity)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(cores, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _clock = clock;
        Cores = cores;
        _measured = Channel.CreateBounded<ComputeWork>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    /// <summary>The cores work on this node is charged by.</summary>
    public int Cores { get; }

    /// <summary>What has ended and waits to be written.</summary>
    public ChannelReader<ComputeWork> Measured => _measured.Reader;

    /// <summary>How many measurements were dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>How many pieces of work are being measured now.</summary>
    public int InFlight => _held.Count;

    /// <summary>
    /// A poll and its answer: what the node no longer says it holds has ended without an outcome, and what the answer
    /// hands out starts now.
    /// </summary>
    public void Polled(NodePollRequest request, NodePollResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        var holdingRuns = request.HoldingRuns.Select(r => r.RunId).ToHashSet();
        var holdingTasks = request.HoldingTasks.ToHashSet();
        foreach (var (key, _) in _held)
        {
            if (!string.Equals(key.Node, request.Node, StringComparison.Ordinal))
            {
                continue;
            }

            var stillHeld = key.Kind == ComputeWorkKinds.Run ? holdingRuns.Contains(key.WorkId) : holdingTasks.Contains(key.WorkId);
            if (!stillHeld)
            {
                End(key, ComputeOutcomes.Interrupted);
            }
        }

        // A lease the control plane revokes is aborted by the node at once, with no outcome to report: the work ends here.
        foreach (var revoked in response.RevokedRuns)
        {
            End(new HeldKey(request.Node, ComputeWorkKinds.Run, revoked), ComputeOutcomes.Interrupted);
        }

        foreach (var revoked in response.RevokedTasks)
        {
            End(new HeldKey(request.Node, ComputeWorkKinds.Task, revoked), ComputeOutcomes.Interrupted);
        }

        var nodeSlots = Math.Max(1, request.RunSlots + request.TaskSlots);
        foreach (var run in response.Runs)
        {
            var spec = run.Spec;
            Start(
                new HeldKey(request.Node, ComputeWorkKinds.Run, run.RunId),
                new Held(
                    run.Attempt, spec.RepoId, spec.RepoName ?? string.Empty, FolderOf(spec.PipelineRelativePath), ProjectOf(spec.PipelineRelativePath), spec.FlowName,
                    spec.RequestedBy, _clock.GetUtcNow().UtcDateTime, _clock.GetTimestamp(),
                    spec.WholeNode ? Math.Max(1, request.RunSlots) : 1, nodeSlots));
        }

        foreach (var task in response.Tasks)
        {
            var (repoId, actor) = TaskArguments(task.Spec.ArgumentsJson);
            Start(
                new HeldKey(request.Node, ComputeWorkKinds.Task, task.TaskId),
                new Held(
                    0, repoId, string.Empty, string.Empty, string.Empty, task.Spec.Operation, actor,
                    _clock.GetUtcNow().UtcDateTime, _clock.GetTimestamp(), 1, nodeSlots));
        }
    }

    /// <summary>The node reports a run's outcome: the run's work ended, whatever the control plane makes of the report.</summary>
    public void RunEnded(Guid runId, RunOutcomeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        End(new HeldKey(request.Node, ComputeWorkKinds.Run, runId), request.Outcome switch
        {
            RunOutcomeKind.Completed => ComputeOutcomes.Completed,
            RunOutcomeKind.Cancelled => ComputeOutcomes.Cancelled,
            _ => ComputeOutcomes.Failed,
        });
    }

    /// <summary>The node reports a compute task's outcome: the task's work ended.</summary>
    public void TaskEnded(Guid taskId, TaskOutcomeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        End(new HeldKey(request.Node, ComputeWorkKinds.Task, taskId), request.Outcome switch
        {
            TaskOutcomeKind.Succeeded => ComputeOutcomes.Completed,
            TaskOutcomeKind.Cancelled => ComputeOutcomes.Cancelled,
            _ => ComputeOutcomes.Failed,
        });
    }

    /// <summary>
    /// The node is stopping: whatever it still holds ends here as <see cref="ComputeOutcomes.Interrupted"/>, and nothing
    /// more is measured, so the recorder can write what is left and finish.
    /// </summary>
    public void Stop()
    {
        foreach (var (key, _) in _held)
        {
            End(key, ComputeOutcomes.Interrupted);
        }

        _measured.Writer.TryComplete();
    }

    /// <summary>The folder of a flow's file in its repository, with forward slashes; empty for a flow at the root.</summary>
    internal static string FolderOf(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return string.Empty;
        }

        var path = relativePath.Replace('\\', '/');
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? string.Empty : path[..slash];
    }

    /// <summary>The first folder of a flow's path in its repository, cut to the column; empty for a flow at the root.</summary>
    internal static string ProjectOf(string? relativePath)
    {
        var folder = FolderOf(relativePath);
        var slash = folder.IndexOf('/', StringComparison.Ordinal);
        var first = slash < 0 ? folder : folder[..slash];
        return first.Length <= DeliveryComputeUsage.MaxProjectLength ? first : first[..DeliveryComputeUsage.MaxProjectLength];
    }

    /// <summary>The repository and the requester a compute task's arguments name, when they name any.</summary>
    private static (Guid? RepoId, string? Actor) TaskArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            Guid? repoId = document.RootElement.TryGetProperty(DeliveryOperation.RepositoryArgument, out var repo)
                && repo.ValueKind == JsonValueKind.String && Guid.TryParse(repo.GetString(), out var parsed)
                    ? parsed
                    : null;
            var actor = document.RootElement.TryGetProperty("actor", out var asked) && asked.ValueKind == JsonValueKind.String
                ? asked.GetString()
                : null;
            return (repoId, string.IsNullOrWhiteSpace(actor) ? null : actor);
        }
        catch (JsonException)
        {
            // The arguments are another operation's own format: the task is measured all the same, charged to no repository.
            return (null, null);
        }
    }

    private void Start(HeldKey key, Held held)
    {
        // The same work handed to this node again while the earlier hand-out is still measured (its lease was revoked and
        // it came back before a poll showed it gone): the earlier one ended without an outcome.
        End(key, ComputeOutcomes.Interrupted);
        _held[key] = held;
    }

    private void End(HeldKey key, string outcome)
    {
        if (!_held.TryRemove(key, out var held))
        {
            return;
        }

        var elapsed = _clock.GetElapsedTime(held.StartTimestamp);
        var wallClockMs = Math.Max(0L, (long)elapsed.TotalMilliseconds);
        var work = new ComputeWork(
            Guid.CreateVersion7(), Cut(key.Node, DeliveryComputeUsage.MaxNodeLength), key.Kind, key.WorkId, held.Attempt, held.RepoId,
            Cut(held.Repo, DeliveryComputeUsage.MaxRepoLength), held.FlowFolder, held.Project, Cut(held.Flow, DeliveryComputeUsage.MaxFlowLength),
            held.RequestedBy is { } by ? Cut(by, DeliveryComputeUsage.MaxRequestedByLength) : null,
            held.StartedUtc, held.StartedUtc.AddMilliseconds(wallClockMs), wallClockMs, Cores, held.Slots, held.NodeSlots, outcome);
        if (!_measured.Writer.TryWrite(work))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private static string Cut(string value, int length) => value.Length <= length ? value : value[..length];

    private readonly record struct HeldKey(string Node, string Kind, Guid WorkId);

    private sealed record Held(
        int Attempt, Guid? RepoId, string Repo, string FlowFolder, string Project, string Flow, string? RequestedBy,
        DateTime StartedUtc, long StartTimestamp, int Slots, int NodeSlots);
}
