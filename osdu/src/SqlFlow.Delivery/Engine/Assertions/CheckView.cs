using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlFlow.Delivery.Engine.Assertions;

/// <summary>
/// How one check reads in a report, each fact once: what it expects (the comparison alone, such as <c>equals 5191</c>), what
/// qualifies that (<c>warns within 10%</c>, <c>leaving out "MD"</c>, <c>stated as of 2026-10-27</c>), what it found (the
/// number a count or an aggregate measured, else what the check says it found, as an aggregate over each record's bulk data
/// does: <c>2 of 3 record(s)' bulk data meet it</c>), and why it came out as it did, when that adds to the two
/// (<c>1 below 5191 (0.02%), within the warning band of 10%, so it warns.</c>). The run's report, a set's report and the GUI
/// show a check through this one reading.
/// </summary>
public sealed partial record CheckView(string Expected, IReadOnlyList<string> Qualifiers, string Found, string? Why)
{
    /// <summary>The check's reading, from what its run recorded.</summary>
    public static CheckView Of(AssertionOutcome check)
    {
        ArgumentNullException.ThrowIfNull(check);

        // A check's expectation is written as the comparison, then the warning band, the values left out and the day its
        // values were stated, in that order (Model.WarningBand.Note, AggregateAssertion.Expected, AssertionActivity.Stated).
        var expected = check.Expected;
        var qualifiers = new List<string>();
        string? stated = null;
        if (StatedPattern().Match(expected) is { Success: true } statedAt)
        {
            stated = statedAt.Groups[1].Value;
            expected = expected[..statedAt.Index];
        }

        if (LeavingOutPattern().Match(expected) is { Success: true } leaving)
        {
            qualifiers.Add("leaving out " + leaving.Groups[1].Value);
            expected = expected[..leaving.Index];
        }

        if (BandPattern().Match(expected) is { Success: true } band)
        {
            qualifiers.Insert(0, "warns within " + band.Groups[1].Value);
            expected = expected.Remove(band.Index, band.Length);
        }

        if (stated is not null)
        {
            qualifiers.Add("stated as of " + stated);
        }

        // A check judged record by record (an aggregate of each record's bulk data) keeps the share that held as its
        // value, which is not what it found: its finding says how many records meet it.
        var found = check.Type is "count" or "aggregate" && check.Value is { } value && double.IsFinite(value)
            && !PerRecordPattern().IsMatch(check.Actual ?? string.Empty)
            ? value.ToString("0.####", CultureInfo.InvariantCulture)
            : check.Actual ?? "-";
        return new CheckView(expected.Trim(), qualifiers, found, WhyOf(check));
    }

    /// <summary>
    /// What a check's message adds to what it expected and found: its clauses that repeat neither, such as by how much it
    /// missed; null when it adds nothing, or the check holds.
    /// </summary>
    private static string? WhyOf(AssertionOutcome check)
    {
        if (check.Message is not { Length: > 0 } message || check.Outcome == Ledger.TestOutcomes.Passed)
        {
            return null;
        }

        var clauses = message.TrimEnd('.', ' ')
            .Split("; ", StringSplitOptions.RemoveEmptyEntries)
            .Where(clause => !clause.StartsWith("expected ", StringComparison.Ordinal)
                && !(check.Actual is { } actual && actual.StartsWith(clause, StringComparison.Ordinal)))
            .ToList();
        return clauses.Count == 0 ? null : string.Join("; ", clauses) + ".";
    }

    [GeneratedRegex(@" \(stated as of ([^)]+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex StatedPattern();

    [GeneratedRegex(@", leaving out (.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex LeavingOutPattern();

    [GeneratedRegex(@" \(warning within ([^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex BandPattern();

    [GeneratedRegex(@"^\d+ of \d+ record\(s\)", RegexOptions.CultureInvariant)]
    private static partial Regex PerRecordPattern();
}
