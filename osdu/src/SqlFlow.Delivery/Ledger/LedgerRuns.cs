namespace SqlFlow.Delivery.Ledger;

/// <summary>What the run tables of the ledger and its stores keep alike: a run's error, redacted and cut to its column.</summary>
internal static class LedgerRuns
{
    /// <summary>The longest error a run's row keeps, its column's length.</summary>
    private const int MaxErrorLength = 4000;

    /// <summary>A run's error as the ledger keeps it: redacted, and cut to its column. Null stays null.</summary>
    public static string? Error(string? error)
    {
        if (error is null)
        {
            return null;
        }

        var redacted = Http.HeaderRedaction.RedactMessage(error);
        return redacted.Length <= MaxErrorLength ? redacted : redacted[..MaxErrorLength];
    }
}
