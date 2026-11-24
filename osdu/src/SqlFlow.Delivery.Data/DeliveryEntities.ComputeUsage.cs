namespace SqlFlow.Delivery.Data;

// The work nodes did (osdu/docs/reference/concepts/compute-usage.md): each piece as a node measured it, and each flow's
// usage of a day folded from them.

/// <summary>
/// One piece of work a node did, as the node measured it (osdu/docs/reference/concepts/compute-usage.md): one attempt of a
/// platform run, or one compute task, with the wall-clock time the node held it, the node's cores and slots at the time,
/// the compute units that comes to, and who the work is charged to: the team its flow's repository document names, the
/// repository and project folder of its flow, the flow, and who asked for it. The node writes the row once the work has
/// ended, whatever way it ended.
/// </summary>
/// <remarks>
/// Every value the compute units are worked out from is kept on the row (<see cref="WallClockMs"/>, <see cref="Cores"/>,
/// <see cref="Slots"/>, <see cref="NodeSlots"/>), so a figure can be worked out again from the row alone, and another
/// policy can be applied to the same rows later. A cleanup an admin sets folds the rows older than its days into
/// <see cref="DeliveryComputeUsageDay"/> before it removes them, so no usage is lost with them.
/// </remarks>
public sealed class DeliveryComputeUsage
{
    /// <summary>The longest node name a row keeps: the width SQLFlow gives a node's name.</summary>
    public const int MaxNodeLength = 128;

    /// <summary>The longest team a repository document names.</summary>
    public const int MaxTeamLength = 64;

    /// <summary>The longest repository name a row keeps: the width SQLFlow gives a repository's name.</summary>
    public const int MaxRepoLength = 256;

    /// <summary>The longest project folder a row keeps; a longer folder name is cut to it.</summary>
    public const int MaxProjectLength = 100;

    /// <summary>The longest flow name (or compute task operation) a row keeps: the width SQLFlow gives a pipeline's name.</summary>
    public const int MaxFlowLength = 400;

    /// <summary>The longest kind of work.</summary>
    public const int MaxKindLength = 8;

    /// <summary>The longest outcome.</summary>
    public const int MaxOutcomeLength = 16;

    /// <summary>The longest requester a row keeps: the width SQLFlow gives a run's requester.</summary>
    public const int MaxRequestedByLength = 256;

    /// <summary>When the work ended on its node: the first column of the key, so a period's rows sit together.</summary>
    public DateTime EndedUtc { get; set; }

    public long UsageId { get; set; }

    /// <summary>The id the node gave the measurement when it took it, so a batch a node writes twice lands once.</summary>
    public Guid MeasurementId { get; set; }

    /// <summary>The node that did the work, by the name it polls the control plane under.</summary>
    public string Node { get; set; } = string.Empty;

    /// <summary><c>run</c> for an attempt of a platform run, <c>task</c> for a compute task.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The platform run, or the compute task.</summary>
    public Guid WorkId { get; set; }

    /// <summary>Which attempt of the run this was; 0 for a compute task.</summary>
    public int Attempt { get; set; }

    /// <summary>
    /// The team the work is charged to: the <c>team</c> of the repository document nearest the flow's file, as the
    /// repository's last sync before the work ended recorded it (<see cref="DeliveryRepositoryTeam"/>); empty when no
    /// document above the flow names one.
    /// </summary>
    public string Team { get; set; } = string.Empty;

    /// <summary>The repository of the work's flow, when the work names one.</summary>
    public Guid? RepoId { get; set; }

    /// <summary>The repository's name as it was when the work ran; empty for work that names no repository.</summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>The first folder of the flow's path in its repository; empty for a flow at the repository's root and for a task.</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>The flow's name; for a compute task, its operation.</summary>
    public string Flow { get; set; } = string.Empty;

    /// <summary>Who asked for the run, as the platform recorded it (a user, <c>schedule:name</c>, <c>trigger:name</c>); null when the work names nobody.</summary>
    public string? RequestedBy { get; set; }

    /// <summary>When the node was handed the work.</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>How long the node held the work, in milliseconds, by the node's own monotonic clock.</summary>
    public long WallClockMs { get; set; }

    /// <summary>The cores the node had.</summary>
    public int Cores { get; set; }

    /// <summary>The slots of the node the work held: one, or every run slot for a run that takes a whole node.</summary>
    public int Slots { get; set; }

    /// <summary>The slots the node had in all: its run slots and its compute task slots.</summary>
    public int NodeSlots { get; set; }

    /// <summary>The compute units the work used: its hours times the cores its slots stand for.</summary>
    public double ComputeUnits { get; set; }

    /// <summary>How the work ended: <c>completed</c>, <c>failed</c>, <c>cancelled</c>, or <c>interrupted</c> for work the node gave up without an outcome.</summary>
    public string Outcome { get; set; } = string.Empty;
}

/// <summary>
/// The compute usage of one UTC day by one flow, folded from the rows of <see cref="DeliveryComputeUsage"/> a cleanup
/// removed (osdu/docs/reference/concepts/compute-usage.md, Keeping the totals): how many pieces of work, their wall-clock
/// time and their compute units. A day's usage is this row plus whatever rows of that day the work table still holds; a
/// row of work is in exactly one of the two.
/// </summary>
public sealed class DeliveryComputeUsageDay
{
    public int UsageDayId { get; set; }

    /// <summary>The UTC day the work ended on.</summary>
    public DateOnly Day { get; set; }

    /// <summary>The team the work was charged to; empty for work no team was named for.</summary>
    public string Team { get; set; } = string.Empty;

    /// <summary>The repository's name; empty for work that named no repository.</summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>The project folder; empty for a flow at the repository's root and for a task.</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>The flow's name; for compute tasks, the operation.</summary>
    public string Flow { get; set; } = string.Empty;

    /// <summary><c>run</c> or <c>task</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>How many pieces of work the row sums.</summary>
    public long WorkCount { get; set; }

    /// <summary>Their wall-clock time, in milliseconds.</summary>
    public long WallClockMs { get; set; }

    /// <summary>Their compute units.</summary>
    public double ComputeUnits { get; set; }
}
