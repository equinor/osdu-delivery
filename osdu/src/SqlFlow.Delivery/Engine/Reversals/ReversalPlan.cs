using System.Globalization;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Reversals;

/// <summary>What a reversal does with one record, as the ledger decides it before OSDU is asked anything.</summary>
public enum ReversalAction
{
    /// <summary>Write back the version OSDU held before the source (<see cref="ReversalItemState.PriorVersion"/>).</summary>
    Restore,

    /// <summary>Remove the record reversibly: the source created it.</summary>
    Remove,

    /// <summary>The ledger no longer says what OSDU held before: OSDU's version list decides between a restore and a removal.</summary>
    ResolvePrior,

    /// <summary>Pass the record over, saying why.</summary>
    Skip,
}

/// <summary>What the ledger decided for one record: the action, and for a record passed over the outcome and why.</summary>
public sealed record ReversalStep(ReversalAction Action, string? Outcome = null, string? Detail = null)
{
    public static ReversalStep Skip(string outcome, string detail) => new(ReversalAction.Skip, outcome, detail);
}

/// <summary>
/// How a reversal decides, from the ledger alone, what to do with one record its source delivered
/// (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run or a submission): the one decision the run
/// and the preview both take, so what the preview counts is what the run does. A record is put back only while it is
/// still the record the source left; anything that came after the source is left as it is, saying what it was.
/// </summary>
public static class ReversalPlan
{
    public static ReversalStep Decide(ReversalItemState item, RecordState? record, ReversalRoute route)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(route);
        if (record is null)
        {
            return ReversalStep.Skip(ReversalOutcomes.NotInLedger, "the ledger holds no record of the flow under this key");
        }

        if (item.TargetId is null || !string.Equals(record.ClaimedTargetId, item.TargetId, StringComparison.Ordinal))
        {
            return ReversalStep.Skip(ReversalOutcomes.NotClaimed, "the record never claimed an OSDU id, so this flow wrote nothing to OSDU under it");
        }

        if (record.Restricted)
        {
            return ReversalStep.Skip(
                ReversalOutcomes.Restricted,
                "the record's access in OSDU is restricted, and nothing is written to it while it is; it is taken again when the reversal is asked again, once the restriction is lifted");
        }

        if (record.LeaseOwner is not null || record.Status is RecordStatus.Pending or RecordStatus.Delivering or RecordStatus.Waiting)
        {
            return ReversalStep.Skip(
                ReversalOutcomes.Busy,
                $"work is queued for the record ({record.Status.ToString().ToLowerInvariant()}{(record.LastSubmissionId is { } s ? $", submission {s:D}" : string.Empty)}); it is taken again when the reversal is asked again, once that work has settled");
        }

        if (record.Status == RecordStatus.Deleted)
        {
            return ReversalStep.Skip(ReversalOutcomes.Superseded, "the record was removed from OSDU after the source delivered it; it is left as the removal left it");
        }

        if (record.Status == RecordStatus.Reverted)
        {
            return ReversalStep.Skip(
                ReversalOutcomes.Superseded,
                string.Create(CultureInfo.InvariantCulture, $"a reversal already put the record back after the source delivered it (the ledger holds version {Version(record.TargetVersion)})"));
        }

        if (record.TargetVersion != item.RunVersion)
        {
            return ReversalStep.Skip(
                ReversalOutcomes.Superseded,
                string.Create(CultureInfo.InvariantCulture, $"the ledger holds version {Version(record.TargetVersion)}, delivered after the source left version {Version(item.RunVersion)}; reverse the later run first"));
        }

        if (item.RunVersion is null)
        {
            return ReversalStep.Skip(ReversalOutcomes.NotReversible, "the source's delivery recorded no OSDU version, so whether OSDU still holds what it wrote cannot be told");
        }

        switch (item.Prior)
        {
            case ReversalPriors.Version when item.PriorVersion == item.RunVersion:
                return ReversalStep.Skip(
                    ReversalOutcomes.Unchanged,
                    string.Create(CultureInfo.InvariantCulture, $"the source left OSDU at version {Version(item.RunVersion)}, the one it held before; there is nothing to put back"));

            case ReversalPriors.Version when item.PriorVersion is not null:
                return route.Restores ? new ReversalStep(ReversalAction.Restore) : ReversalStep.Skip(ReversalOutcomes.NotReversible, route.RestoreRefusal!);

            case ReversalPriors.None:
                return route.Removes ? new ReversalStep(ReversalAction.Remove) : ReversalStep.Skip(ReversalOutcomes.NotReversible, route.RemoveRefusal!);

            default:
                return route.Restores
                    ? new ReversalStep(ReversalAction.ResolvePrior)
                    : ReversalStep.Skip(
                        ReversalOutcomes.NotReversible,
                        $"the ledger no longer says what OSDU held before the source (the record's earlier attempts were pruned), and OSDU's version list cannot be read: {route.RestoreRefusal}");
        }
    }

    /// <summary>A version as a message names it.</summary>
    public static string Version(long? version) => version is { } v ? v.ToString(CultureInfo.InvariantCulture) : "none";
}
