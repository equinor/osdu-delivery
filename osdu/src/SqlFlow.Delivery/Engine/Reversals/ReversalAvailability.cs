using System.Globalization;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Engine.Reversals;

/// <summary>
/// Whether a source has anything to reverse now (osdu/docs/reference/concepts/removal-and-reversal.md, Reversing a run
/// or a submission): one answer for every place that offers a reversal or starts one, so the GUI shows the action
/// exactly when the request would be taken and the run would act. A source with no reversal yet has something to
/// reverse when it delivered a record of the ledger; one whose reversal exists has something while that reversal has
/// records to take (not settled yet, failed, or passed over as busy) or has not finished listing what the source
/// delivered; and neither has while a reverse run of the source is queued or running.
/// </summary>
/// <param name="Reversible">Whether asking for the reversal now would act on something.</param>
/// <param name="Resumes">Whether that request resumes the reversal that exists rather than opening one.</param>
/// <param name="Reason">Why there is nothing to reverse now; null when there is something.</param>
/// <param name="Reversal">The reversal of the source, when one was asked for.</param>
/// <param name="Counts">That reversal's records by state and outcome.</param>
public sealed record ReversalAvailability(bool Reversible, bool Resumes, string? Reason, ReversalState? Reversal, ReversalCounts? Counts)
{
    /// <summary>
    /// Reads whether <paramref name="source"/> has anything to reverse in the flow's ledger. <paramref name="activeRun"/> is a
    /// reverse run of the source the platform has queued or is running, which the caller knows and the ledger does not; the
    /// reverse run asking about its own source passes none.
    /// </summary>
    public static async Task<ReversalAvailability> ReadAsync(ILedgerReversals ledger, Guid flowId, ReversalSource source, Guid? activeRun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(source);
        var reversal = await ledger.FindReversalAsync(flowId, source, ct).ConfigureAwait(false);
        var counts = reversal is null ? null : await ledger.CountReversalAsync(reversal.ReversalId, ct).ConfigureAwait(false);
        if (activeRun is { } run)
        {
            return new ReversalAvailability(false, false, string.Create(CultureInfo.InvariantCulture, $"Run {run:D} is reversing {source} now."), reversal, counts);
        }

        if (reversal is null)
        {
            return await ledger.SourceDeliveredAsync(flowId, source, ct).ConfigureAwait(false)
                ? new ReversalAvailability(true, false, null, null, null)
                : new ReversalAvailability(false, false, $"{Capitalized(source)} delivered nothing to OSDU in this ledger, so there is nothing to reverse.", null, null);
        }

        if (reversal.CapturedUtc is null || counts!.ToTake > 0)
        {
            return new ReversalAvailability(true, true, null, reversal, counts);
        }

        return new ReversalAvailability(
            false,
            false,
            string.Create(CultureInfo.InvariantCulture, $"Reversal {reversal.ReversalId} of {source} settled every record it delivered ({counts}); nothing is left to reverse."),
            reversal,
            counts);
    }

    private static string Capitalized(ReversalSource source)
    {
        var text = source.ToString();
        return string.Concat(char.ToUpperInvariant(text[0]).ToString(CultureInfo.InvariantCulture), text[1..]);
    }
}
