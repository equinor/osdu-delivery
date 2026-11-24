using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// An operator's release and adoption of a flow's records by what they reach (<see cref="RecordReach"/>): the one path the
/// API, the CLI and the MCP server take, so which issue an action names, whether an adoption is confirmed and why a record
/// cannot be adopted are decided here and nowhere else. A refusal is an <see cref="InterventionRefusedException"/>, raised
/// before anything changed.
/// </summary>
public sealed partial class FlowRuntime
{
    /// <summary>
    /// The issue as the ledger names it now, which is what the operator was shown and what a release or an adoption of its
    /// records records; refused when no record of the flow is blocked by it any more.
    /// </summary>
    public async Task<ProblemGroup> IssueAsync(long problem, CancellationToken ct = default)
        => await RequireLedger().GetProblemAsync(Flow.Id, problem, ct).ConfigureAwait(false)
            ?? throw new InterventionRefusedException(
                InterventionRefusal.UnknownIssue,
                $"No record of {Flow.Label} is blocked by issue {ProblemSignature.Format(problem)}: its records were released, their source changed, or they were planned again since.");

    /// <summary>
    /// Releases the records <paramref name="reach"/> names (osdu/docs/reference/concepts/record-lifecycle.md, Releasing
    /// blocked records): the named ones, every one an issue keeps blocked, or every blocked record. Returns how many.
    /// </summary>
    public Task<int> ReleaseAsync(RecordReach reach, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reach);
        return reach switch
        {
            RecordReach.NamedRecords named => ReleaseNamedAsync(named.Keys, ct),
            RecordReach.IssueRecords issue => ReleaseProblemAsync(issue.Issue.Problem, issue.Issue.Pattern, ct),
            _ => ReleaseNamedAsync(null, ct),
        };
    }

    /// <summary>The partition the flow's records are kept in, or null when no run has kept any yet.</summary>
    public async Task<string?> KeptPartitionAsync(CancellationToken ct = default)
        => (await RequireLedger().GetLedgerAsync(Flow.Id, ct).ConfigureAwait(false))?.Partition is { Length: > 0 } kept ? kept : null;

    /// <summary>
    /// Refuses an adoption of what <paramref name="reach"/> names unless <paramref name="confirm"/> types back the partition
    /// the flow's records are kept in (<see cref="PartitionConfirmation"/>), since it takes over records OSDU holds that no
    /// ledger owned; returns that partition. A record's own adoption takes no confirmation (<see cref="AdoptRecordAsync"/>).
    /// </summary>
    public async Task<string> ConfirmAdoptionAsync(RecordReach reach, string? confirm, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reach);
        var kept = await KeptPartitionAsync(ct).ConfigureAwait(false)
            ?? throw new InterventionRefusedException(
                InterventionRefusal.NothingKept, $"{Flow.Label} keeps no records in a partition yet, so there is nothing to adopt.");
        if (PartitionConfirmation.Typed(confirm) is not { } typed)
        {
            var what = reach switch
            {
                RecordReach.NamedRecords named => $"Adopting {named.Keys.Count} record(s) of {Flow.Label}",
                RecordReach.IssueRecords issue => $"Adopting the records of issue {ProblemSignature.Format(issue.Issue.Problem)}",
                _ => $"Adopting every record of {Flow.Label} held for an id OSDU holds",
            };
            throw new InterventionRefusedException(
                InterventionRefusal.Unconfirmed,
                $"{what} takes over every record OSDU holds at their ids in partition {kept}, which no ledger owned, and the next run writes them there.",
                kept);
        }

        return PartitionConfirmation.Names(typed, kept)
            ? kept
            : throw new InterventionRefusedException(
                InterventionRefusal.ConfirmationDiffers,
                $"The adoption confirms partition '{typed}', and the records of {Flow.Label} are kept in '{kept}'. Nothing was adopted.",
                kept);
    }

    /// <summary>
    /// Asks the records <paramref name="reach"/> names that are held because OSDU already holds a record at the id each one's
    /// key makes to adopt that record (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already
    /// holds), once <paramref name="confirm"/> types back the partition (<see cref="ConfirmAdoptionAsync"/>). The flow's next
    /// deliver run takes each over, at the version OSDU holds then. A record held for another reason, or claiming an id
    /// already, is left as it is. Returns the records asked.
    /// </summary>
    public async Task<IReadOnlyList<DeliveryKey>> AdoptAsync(RecordReach reach, string? confirm, CancellationToken ct = default)
    {
        await ConfirmAdoptionAsync(reach, confirm, ct).ConfigureAwait(false);
        return reach switch
        {
            RecordReach.NamedRecords named => await AdoptNamedAsync(named.Keys, ct).ConfigureAwait(false),
            RecordReach.IssueRecords issue => await AdoptProblemAsync(issue.Issue.Problem, issue.Issue.Pattern, ct).ConfigureAwait(false),
            _ => await AdoptEveryAsync(ct).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Asks <paramref name="record"/>, the one an operator is looking at, to adopt the record OSDU holds at the id its key
    /// makes: no partition is typed back for one record seen on its own page. Refused saying why when the record is not held
    /// for such an id (<see cref="OccupiedHolds.Adoptable"/>), and when it moved on since it was shown.
    /// </summary>
    public async Task AdoptRecordAsync(RecordState record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!OccupiedHolds.Adoptable(record))
        {
            var why = record switch
            {
                { ClaimedTargetId: { } claimed } => $"it claimed {claimed} already, and a record keeps the OSDU id it claimed",
                { AdoptRequestedUtc: not null, Blocked: false } => "its adoption is asked already, and the flow's next run takes it over",
                { AdoptRequestedUtc: not null } =>
                    "its adoption is asked already, and a run held it since for another reason (its error says which); release it, and the run after takes it over",
                { LeaseOwner: not null } => "a delivery holds it now",
                _ => "it is not held because OSDU already holds a record at the OSDU id its key makes",
            };
            throw new InterventionRefusedException(
                InterventionRefusal.NotAdoptable,
                $"Record {record.SourceKey} of {Flow.Label} has nothing to adopt: {why}. An adoption takes over the record OSDU holds at the id a held record's key makes.");
        }

        if ((await AdoptNamedAsync([record.DeliveryKey], ct).ConfigureAwait(false)).Count == 0)
        {
            throw new InterventionRefusedException(
                InterventionRefusal.MovedOn,
                $"Record {record.SourceKey} of {Flow.Label} moved on since it was shown (a run planned it, or a delivery holds it), so no adoption was asked. Look at the record again.");
        }
    }
}
