namespace SqlFlow.Delivery.ComputeUsage;

/// <summary>
/// The unit compute usage is counted in (osdu/docs/reference/concepts/compute-usage.md, The compute unit): one compute
/// unit is one core held for one hour. A node's cores are shared evenly over its slots, so work is charged its wall-clock
/// hours times the cores its slots stand for, and a node never yields more units in an hour than it has cores.
/// </summary>
public static class ComputeUnits
{
    /// <summary>The milliseconds in the hour a unit is counted over.</summary>
    public const double MillisecondsPerHour = 3_600_000d;

    /// <summary>
    /// The compute units of work that held <paramref name="slots"/> of a node's <paramref name="nodeSlots"/> for
    /// <paramref name="wallClockMs"/>, on a node of <paramref name="cores"/> cores:
    /// <c>wallClockMs / 3,600,000 * cores * slots / nodeSlots</c>. Work holding more slots than the node has is charged
    /// the whole node, never more.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A time below zero, or a node with no core or no slot.</exception>
    public static double Of(long wallClockMs, int cores, int slots, int nodeSlots)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wallClockMs);
        ArgumentOutOfRangeException.ThrowIfLessThan(cores, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeSlots, 1);
        return wallClockMs / MillisecondsPerHour * cores * Math.Min(slots, nodeSlots) / nodeSlots;
    }
}

/// <summary>The kinds of work a node is handed.</summary>
public static class ComputeWorkKinds
{
    /// <summary>One attempt of a platform run.</summary>
    public const string Run = "run";

    /// <summary>One compute task.</summary>
    public const string Task = "task";
}

/// <summary>How a piece of work ended on its node.</summary>
public static class ComputeOutcomes
{
    /// <summary>The node reported the work done; a run's own result says whether the flow succeeded.</summary>
    public const string Completed = "completed";

    /// <summary>The node reported the work failed.</summary>
    public const string Failed = "failed";

    /// <summary>The node honored a cancel.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The node gave the work up without an outcome: its lease was revoked, or the node stopped before it finished.</summary>
    public const string Interrupted = "interrupted";
}

/// <summary>
/// One piece of work as its node measured it: what the node writes to <c>osdu.ComputeUsage</c> once the work has ended.
/// </summary>
/// <param name="MeasurementId">The measurement's own id, so writing it twice lands it once.</param>
/// <param name="Node">The node, by the name it polls under.</param>
/// <param name="Kind">One of <see cref="ComputeWorkKinds"/>.</param>
/// <param name="WorkId">The platform run or the compute task.</param>
/// <param name="Attempt">The run's attempt; 0 for a task.</param>
/// <param name="RepoId">The repository of the work's flow, when the work names one.</param>
/// <param name="Repo">The repository's name; empty when the work names none.</param>
/// <param name="FlowFolder">
/// The folder of the flow's file in its repository, with forward slashes; empty for a flow at the root and for a task. The
/// work's team is found by it and it is not kept.
/// </param>
/// <param name="Project">The first folder of the flow's path in its repository; empty for a flow at the root and for a task.</param>
/// <param name="Flow">The flow's name; a task's operation.</param>
/// <param name="RequestedBy">Who asked for the work, when it says.</param>
/// <param name="StartedUtc">When the node was handed the work.</param>
/// <param name="EndedUtc">When the work ended on the node.</param>
/// <param name="WallClockMs">How long the node held it, by its monotonic clock.</param>
/// <param name="Cores">The node's cores.</param>
/// <param name="Slots">The slots the work held.</param>
/// <param name="NodeSlots">The node's slots in all.</param>
/// <param name="Outcome">One of <see cref="ComputeOutcomes"/>.</param>
public sealed record ComputeWork(
    Guid MeasurementId,
    string Node,
    string Kind,
    Guid WorkId,
    int Attempt,
    Guid? RepoId,
    string Repo,
    string FlowFolder,
    string Project,
    string Flow,
    string? RequestedBy,
    DateTime StartedUtc,
    DateTime EndedUtc,
    long WallClockMs,
    int Cores,
    int Slots,
    int NodeSlots,
    string Outcome)
{
    /// <summary>The compute units the work used (<see cref="ComputeUnits.Of"/>).</summary>
    public double Units => ComputeUnits.Of(WallClockMs, Cores, Slots, NodeSlots);
}
