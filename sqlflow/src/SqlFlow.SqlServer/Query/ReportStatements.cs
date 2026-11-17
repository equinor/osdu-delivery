using SqlFlow.Core.Reports;

namespace SqlFlow.SqlServer.Query;

/// <summary>
/// Checks the statements a report declares. A report's reader cannot tell whether a statement is one read-only
/// <c>SELECT</c>: only the T-SQL parser can, and it lives here. Everything that judges a report's statements asks this
/// one place: <c>validate</c>, which refuses the document; the estate scan, which reports it and keeps the report; the
/// report's page, which shows the reason on the query's tile instead of running it; and a run, which refuses the query.
/// The worker parses the statement once more before it reaches the engine (<see cref="SqlServerQueryRunner.RunAsync"/>).
/// </summary>
public static class ReportStatements
{
    /// <summary>Why <paramref name="query"/>'s statement cannot run, or null when it is one read-only <c>SELECT</c>. A
    /// report's statement is one its repository declares, so it may read the server's own catalog.</summary>
    public static string? Problem(ReportQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadOnlyQueryGuard.IsReadOnly(query.Sql, declared: true, out var refusal) ? null : refusal;
    }

    /// <summary>Every query of <paramref name="report"/> whose statement cannot run, with the reason, in the report's
    /// order; empty when all of them can.</summary>
    public static IReadOnlyList<ReportStatementProblem> Problems(ReportFlow report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var problems = new List<ReportStatementProblem>();
        foreach (var query in report.Queries)
        {
            if (Problem(query) is { } problem)
            {
                problems.Add(new ReportStatementProblem(query.Name, problem));
            }
        }

        return problems;
    }
}

/// <summary>A query of a report whose statement cannot run, and why.</summary>
/// <param name="Query">The query's name.</param>
/// <param name="Problem">What the parser refused, in a sentence.</param>
public sealed record ReportStatementProblem(string Query, string Problem);
