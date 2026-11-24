using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// The records an operator's release or adoption reaches (osdu/docs/reference/concepts/record-lifecycle.md, Releasing
/// blocked records and Adopting a record OSDU already holds): records named by key, every record one issue keeps blocked,
/// or every record the action can reach on the flow. Every record is a reach of its own, never an empty or a missing list of
/// keys, so a client that dropped its keys acts on none rather than on the whole flow: the API, the CLI and the MCP server
/// each say which one they mean.
/// </summary>
public abstract class RecordReach
{
    private RecordReach()
    {
    }

    /// <summary>
    /// Every record the action can reach: for a release every blocked record, for an adoption every record held because
    /// OSDU holds a record at its id.
    /// </summary>
    public static RecordReach Every { get; } = new EveryRecord();

    /// <summary>The records <paramref name="keys"/> names: none when it names none, never every one.</summary>
    public static RecordReach Named(IReadOnlyList<DeliveryKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return new NamedRecords(keys);
    }

    /// <summary>Every record <paramref name="issue"/> keeps blocked, the issue as the ledger names it now (<see cref="FlowRuntime.IssueAsync"/>).</summary>
    public static RecordReach Of(ProblemGroup issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        return new IssueRecords(issue);
    }

    /// <summary>The records named by key.</summary>
    public sealed class NamedRecords : RecordReach
    {
        internal NamedRecords(IReadOnlyList<DeliveryKey> keys) => Keys = keys;

        /// <summary>The keys, as many as were named.</summary>
        public IReadOnlyList<DeliveryKey> Keys { get; }
    }

    /// <summary>Every record an issue keeps blocked.</summary>
    public sealed class IssueRecords : RecordReach
    {
        internal IssueRecords(ProblemGroup issue) => Issue = issue;

        /// <summary>The issue, with the pattern the operator was shown, which the action records.</summary>
        public ProblemGroup Issue { get; }
    }

    /// <summary>Every record the action can reach on the flow.</summary>
    public sealed class EveryRecord : RecordReach
    {
        internal EveryRecord()
        {
        }
    }
}

/// <summary>
/// The partition an operator types back before an action asking again cannot take back: an adoption of the records OSDU
/// holds at their ids, a removal, new OSDU ids, deleting a ledger. The one rule each of them keeps, so the API, the CLI
/// and the MCP server read a confirmation alike: what was typed, trimmed, names the partition the action works in, case
/// aside.
/// </summary>
public static class PartitionConfirmation
{
    /// <summary>What <paramref name="confirm"/> types back, trimmed, or null when it types nothing.</summary>
    public static string? Typed(string? confirm) => confirm?.Trim() is { Length: > 0 } typed ? typed : null;

    /// <summary>Whether <paramref name="confirm"/> names <paramref name="partition"/>, the partition the action works in.</summary>
    public static bool Names(string? confirm, string partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        return Typed(confirm) is { } typed && string.Equals(typed, partition, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Why an operator's release or adoption was refused before it changed anything; each client answers it its own way.</summary>
public enum InterventionRefusal
{
    /// <summary>No record of the flow is blocked by the issue named.</summary>
    UnknownIssue,

    /// <summary>The flow keeps no records in a partition yet, so there is nothing to adopt.</summary>
    NothingKept,

    /// <summary>No partition was typed back for an adoption that takes one.</summary>
    Unconfirmed,

    /// <summary>The partition typed back is not the one the flow's records are kept in.</summary>
    ConfirmationDiffers,

    /// <summary>The record named is not one an adoption can be asked of; the message says why.</summary>
    NotAdoptable,

    /// <summary>The record named moved on since it was shown, so nothing was asked of it.</summary>
    MovedOn,
}

/// <summary>An operator's release or adoption refused before it changed anything (<see cref="InterventionRefusal"/>).</summary>
public sealed class InterventionRefusedException : DeliveryException
{
    public InterventionRefusedException(InterventionRefusal refusal, string message, string? partition = null)
        : base(message)
    {
        Refusal = refusal;
        Partition = partition;
    }

    /// <summary>Why it was refused.</summary>
    public InterventionRefusal Refusal { get; }

    /// <summary>For a confirmation refused, the partition the flow's records are kept in, which the operator types back.</summary>
    public string? Partition { get; }
}
