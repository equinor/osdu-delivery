using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Query;
using SqlFlow.Core.Reports;
using SqlFlow.Core.Runs;
using SqlFlow.SqlServer.Query;
using SqlFlow.Yaml;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// A report as a list shows it. <see cref="Problem"/> is set when the synced document no longer reads as a report (it
/// was synced by a build that read it differently): the report is listed so it can be found and fixed, with the reason
/// in place of what it would show.
/// </summary>
public sealed record ReportSummaryDto(
    Guid Id, Guid RepoId, string RepoName, string Name, string Title, string? Description, string? Batch,
    string RelativePath, string? Connection, string? Database, int Queries, int Views, int Parameters,
    DateTime LastSeenUtc, string? Problem);

/// <summary>One parameter of a report, with the queries that bind it, so a client can say what setting it changes and
/// run again only what it changes.</summary>
public sealed record ReportParameterDto(
    string Name, string Label, string? Description, string Type, string? Default, bool Required,
    IReadOnlyList<ReportChoice> Choices, string? ChoicesFrom, IReadOnlyList<string> UsedBy);

/// <summary>
/// One query of a report: what it is called, where it runs, the statement, the parameters it binds, and how its answer
/// is shown. <see cref="Problem"/> is why the statement cannot run (it is not one read-only <c>SELECT</c>), or null.
/// </summary>
public sealed record ReportQueryDto(
    string Name, string Title, string? Description, string? Section, string Connection, string? Database,
    int TimeoutSeconds, int MaxRows, string Sql, IReadOnlyList<string> Parameters, IReadOnlyList<ReportView> Views,
    IReadOnlyList<ReportColumn> Columns, string? Problem);

/// <summary>A report as its page reads it: its heading, its parameters and its queries, in the document's order.</summary>
public sealed record ReportDto(
    Guid Id, Guid RepoId, string RepoName, string Name, string Title, string? Description, string? Batch,
    string RelativePath, string Connection, string? Database, DateTime LastSeenUtc,
    IReadOnlyList<ReportParameterDto> Parameters, IReadOnlyList<ReportQueryDto> Queries);

/// <summary>The values a person set for a run of one query, by parameter name. A parameter left out takes its default; one
/// given null or an empty value was cleared and binds NULL.</summary>
public sealed record ReportRunRequest(IReadOnlyDictionary<string, string?>? Values);

/// <summary>
/// The acknowledgement of a queued query: the task to read the answer from
/// (<c>GET /api/v1/datasources/tasks/{taskId}</c>, optionally long-polled) and the values the statement was bound with,
/// defaults included, so a client shows what the answer is an answer to.
/// </summary>
public sealed record ReportRunAccepted(Guid TaskId, string Status, string Query, IReadOnlyList<QueryParameter> Parameters);

/// <summary>
/// The report surface. A report (<c>flowType: rpt</c>) is a document of read-only queries a repository declares; these
/// endpoints list the reports, read one, and run one of its queries.
///
/// A run takes a report's id, a query's name and values for the report's parameters, and nothing else: the statement is
/// the one the synced document holds, so no SQL ever arrives from a client. The statement is proved to be one read-only
/// <c>SELECT</c> by parsing it here and again on the worker, every value is bound as a typed parameter, and the query runs
/// through the same compute queue and the same rolled-back transaction as every other read of live data, on a worker: the
/// control plane opens no connection to a datasource. The answer is read from the compute task, as a browse's is.
/// </summary>
public static class ReportEndpoints
{
    /// <summary>The most reports one listing returns. An estate's reports are documents, counted in tens.</summary>
    private const int MaxListed = 500;

    public static RouteGroupBuilder MapReportReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/reports", ListAsync)
            .WithTags("Reports").WithName("ListReports");
        group.MapGet("/reports/{reportId:guid}", GetAsync)
            .WithTags("Reports").WithName("GetReport");

        return group;
    }

    public static RouteGroupBuilder MapReportRunEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        // Not opened to an assistant run: a report's statement is one its repository reviewed for people to read, and
        // is not held to the column allow-list that governs what an assistant may read.
        group.MapPost("/reports/{reportId:guid}/queries/{query}/run", RunAsync)
            .WithTags("Reports").WithName("RunReportQuery");

        return group;
    }

    /// <summary>The estate's reports: every active pipeline of kind <c>rpt</c>, by batch and title.</summary>
    private static async Task<Ok<IReadOnlyList<ReportSummaryDto>>> ListAsync(
        CatalogDbContext db, Guid? repoId, CancellationToken ct)
    {
        var rows = await ReportRows(db)
            .Where(p => repoId == null || p.RepoId == repoId)
            .OrderBy(p => p.Name)
            .Take(MaxListed)
            .Select(p => new ReportRow(p.Id, p.RepoId, p.Name, p.Batch, p.RelativePath, p.Yaml, p.LastSeenUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        var repos = await RepoNamesAsync(db, rows.Select(r => r.RepoId), ct).ConfigureAwait(false);

        var items = new List<ReportSummaryDto>(rows.Count);
        foreach (var row in rows)
        {
            var repo = repos.GetValueOrDefault(row.RepoId, string.Empty);
            if (TryRead(row, out var report, out var problem))
            {
                items.Add(new ReportSummaryDto(
                    row.Id, row.RepoId, repo, report.Name, report.Title ?? report.Name, report.Description, report.Batch,
                    row.RelativePath, report.Connection, report.Database, report.Queries.Count,
                    report.Queries.Sum(q => q.Views.Count), report.Parameters.Count, row.LastSeenUtc, null));
            }
            else
            {
                items.Add(new ReportSummaryDto(
                    row.Id, row.RepoId, repo, row.Name, row.Name, null, row.Batch, row.RelativePath, null, null, 0, 0, 0,
                    row.LastSeenUtc, problem));
            }
        }

        return TypedResults.Ok<IReadOnlyList<ReportSummaryDto>>(
            [.. items
                .OrderBy(r => r.Batch ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>One report as its page reads it.</summary>
    private static async Task<Results<Ok<ReportDto>, ProblemHttpResult>> GetAsync(
        Guid reportId, CatalogDbContext db, CancellationToken ct)
    {
        var row = await RowAsync(db, reportId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound(reportId);
        }

        if (!TryRead(row, out var report, out var problem))
        {
            return Problem(problem, StatusCodes.Status422UnprocessableEntity, "The report does not load");
        }

        var repos = await RepoNamesAsync(db, [row.RepoId], ct).ConfigureAwait(false);
        var parameters = report.Parameters
            .Select(p => new ReportParameterDto(
                p.Name, p.Label ?? p.Name, p.Description, p.Type, p.Default, p.Required, p.Choices, p.ChoicesFrom,
                [.. report.Queries
                    .Where(q => q.Parameters.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(q => q.Name)]))
            .ToList();
        var queries = report.Queries
            .Select(q => new ReportQueryDto(
                q.Name, q.Title ?? q.Name, q.Description, q.Section, report.ConnectionOf(q), report.DatabaseOf(q),
                report.TimeoutOf(q), report.MaxRowsOf(q), q.Sql, q.Parameters, q.Views, q.Columns,
                ReportStatements.Problem(q)))
            .ToList();

        return TypedResults.Ok(new ReportDto(
            row.Id, row.RepoId, repos.GetValueOrDefault(row.RepoId, string.Empty), report.Name, report.Title ?? report.Name,
            report.Description, report.Batch, row.RelativePath, report.Connection, report.Database, row.LastSeenUtc,
            parameters, queries));
    }

    /// <summary>
    /// Queues one query of a report. The statement is the synced document's; the request gives only values for the
    /// report's parameters, each checked as a value of its parameter's type and bound as a typed parameter.
    /// </summary>
    private static async Task<Results<Accepted<ReportRunAccepted>, ProblemHttpResult>> RunAsync(
        Guid reportId, string query, ReportRunRequest? request, CatalogDbContext db, IRunDispatcher dispatcher,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var row = await RowAsync(db, reportId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound(reportId);
        }

        if (!TryRead(row, out var report, out var problem))
        {
            return Problem(problem, StatusCodes.Status422UnprocessableEntity, "The report does not load");
        }

        var declared = report.Query(query ?? string.Empty);
        if (declared is null)
        {
            return Problem(
                $"The report '{report.Name}' has no query '{Clip(query)}'. Its queries are: {string.Join(", ", report.Queries.Select(q => q.Name))}.",
                StatusCodes.Status404NotFound, "Not found");
        }

        ComputeTaskPayload payload;
        IReadOnlyList<QueryParameter> bound;
        try
        {
            bound = report.Bind(declared, request?.Values ?? new Dictionary<string, string?>(StringComparer.Ordinal));
            payload = new ComputeTaskPayload
            {
                Operation = ComputeOperations.ReportQuery,
                SourceRef = report.ConnectionOf(declared),
                Database = report.DatabaseOf(declared),

                // Parsed here so a statement that is not one read-only SELECT is a 400 and never a queued task; the
                // worker parses it once more before it reaches the engine.
                Sql = ReadOnlyQueryGuard.Validate(declared.Sql, declared: true),
                MaxRows = report.MaxRowsOf(declared),
                TimeoutSeconds = report.TimeoutOf(declared),
                Parameters = bound.Count == 0 ? null : bound,

                // Named on the queue row, so the task history says which report and which of its queries was read.
                Report = report.Name,
                ReportQuery = declared.Name,
            };
            payload.Validate();
        }
        catch (SqlFlowException ex)
        {
            return Problem(ex.Message, StatusCodes.Status400BadRequest, "The query cannot run");
        }

        // The task belongs to the report's repository, so it resolves its connection with the values that repository's
        // runs are supplied with, and is recorded with who asked.
        var taskId = await dispatcher.EnqueueComputeTaskAsync(
            db,
            new ComputeTaskEnqueueRequest(
                payload.Operation, payload.SourceRef, null, payload.ToJson(), report.Pool, RequestActor.Of(user), row.RepoId),
            ct).ConfigureAwait(false);

        return TypedResults.Accepted(
            $"/api/v1/datasources/tasks/{taskId}",
            new ReportRunAccepted(taskId, RunStatuses.Queued, declared.Name, bound));
    }

    /// <summary>The pipelines that are reports and are in their repository now.</summary>
    private static IQueryable<CatalogPipeline> ReportRows(CatalogDbContext db)
        => db.Pipelines.AsNoTracking().Where(p => p.Kind == ReportFlow.FlowType && p.Active);

    private static Task<ReportRow?> RowAsync(CatalogDbContext db, Guid reportId, CancellationToken ct)
        => ReportRows(db)
            .Where(p => p.Id == reportId)
            .Select(p => new ReportRow(p.Id, p.RepoId, p.Name, p.Batch, p.RelativePath, p.Yaml, p.LastSeenUtc))
            .FirstOrDefaultAsync(ct);

    private static async Task<Dictionary<Guid, string>> RepoNamesAsync(
        CatalogDbContext db, IEnumerable<Guid> repoIds, CancellationToken ct)
    {
        var ids = repoIds.Distinct().ToList();
        return ids.Count == 0
            ? []
            : await db.Repos.AsNoTracking()
                .Where(r => ids.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a report from its synced text with the reader that accepted it at sync, so what a page shows and what a run
    /// executes are the document as it is stored, never a second reading of it. A text that no longer reads (it was
    /// synced by a build that read reports differently) answers false with the reason.
    /// </summary>
    private static bool TryRead(ReportRow row, [NotNullWhen(true)] out ReportFlow? report, out string problem)
    {
        try
        {
            report = YamlReportFlowLoader.Parse(row.Yaml, row.RelativePath);
            problem = string.Empty;
            return true;
        }
        catch (FlowValidationException ex)
        {
            report = null;
            problem = $"The report '{row.Name}' does not load as synced: {ex.Message} Sync its repository again once the document is fixed.";
            return false;
        }
    }

    private static ProblemHttpResult NotFound(Guid reportId)
        => Problem(
            $"No report has the id {reportId}. A report is a document with 'flowType: rpt' in a synced repository.",
            StatusCodes.Status404NotFound, "Not found");

    private static ProblemHttpResult Problem(string detail, int statusCode, string title)
        => TypedResults.Problem(detail: detail, statusCode: statusCode, title: title);

    private static string Clip(string? value)
        => value is null ? string.Empty : value.Length <= 80 ? value : value[..80] + "...";

    /// <summary>The columns of a pipeline row a report is read from.</summary>
    private sealed record ReportRow(
        Guid Id, Guid RepoId, string Name, string? Batch, string RelativePath, string Yaml, DateTime LastSeenUtc);
}
