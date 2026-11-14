using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core;
using SqlFlow.Delivery.ComputeUsage;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>A sum of compute usage: pieces of work, their wall-clock milliseconds and their compute units.</summary>
public sealed record DeliveryComputeTotalsDto(long Work, long WallClockMs, double ComputeUnits);

/// <summary>One UTC day's compute usage; <c>Day</c> is <c>yyyy-MM-dd</c>.</summary>
public sealed record DeliveryComputeDayDto(string Day, long Work, long WallClockMs, double ComputeUnits);

/// <summary>
/// One flow's compute usage in the period: the team it was charged to (empty when no repository document above the flow
/// named one), its repository (empty for work that named none), its project folder (empty at the repository's root and
/// for a compute task), the flow (a task's operation), and <c>run</c> or <c>task</c>.
/// </summary>
public sealed record DeliveryComputeShareDto(string Team, string Repo, string Project, string Flow, string Kind, long Work, long WallClockMs, double ComputeUnits);

/// <summary>
/// The compute usage of a period (osdu/docs/reference/concepts/compute-usage.md): the days asked for, the total, each
/// day that has usage, each flow's share with the largest first, and when the oldest piece of work still listed one by one
/// ended (null when none is), which is how far back <c>/compute-usage/work</c> reaches.
/// </summary>
public sealed record DeliveryComputeUsageDto(
    string From,
    string To,
    DeliveryComputeTotalsDto Totals,
    IReadOnlyList<DeliveryComputeDayDto> Days,
    IReadOnlyList<DeliveryComputeShareDto> Shares,
    DateTime? WorkKeptFromUtc);

/// <summary>
/// One piece of work a node did: one attempt of a run (<c>WorkId</c> is the run) or one compute task, with every value its
/// compute units are worked out from.
/// </summary>
public sealed record DeliveryComputeWorkDto(
    long UsageId,
    string Node,
    string Kind,
    Guid WorkId,
    int Attempt,
    string Team,
    string Repo,
    string Project,
    string Flow,
    string? RequestedBy,
    DateTime StartedUtc,
    DateTime EndedUtc,
    long WallClockMs,
    int Cores,
    int Slots,
    int NodeSlots,
    double ComputeUnits,
    string Outcome);

/// <summary>A page of work, newest first; <c>Next</c> is what to pass as <c>after</c> for the page that follows, null on the last.</summary>
public sealed record DeliveryComputeWorkPageDto(IReadOnlyList<DeliveryComputeWorkDto> Rows, string? Next);

/// <summary>
/// The team a folder of a repository belongs to, as the repository's last sync read it from the folder's repository
/// document: the repository (its name null once the catalog no longer holds it), the folder (empty for the root) and the
/// team, which the work done for the flows under the folder is charged to.
/// </summary>
public sealed record DeliveryRepositoryTeamDto(Guid RepoId, string? Repo, string Folder, string Team, DateTime UpdatedUtc);

/// <summary>
/// The Compute usage page's reads (osdu/docs/reference/concepts/compute-usage.md): how much compute the nodes spent in a
/// period, how it divides over teams, repositories, project folders and flows, and the work behind a share. An admin's,
/// as the page is: the figures are what a deployment's cost is divided by.
/// </summary>
public static class DeliveryComputeUsageEndpoints
{
    /// <summary>The days a summary covers when the request names none: today and the 29 days before it, in UTC.</summary>
    public const int DefaultPeriodDays = 30;

    /// <summary>The rows a page of work holds when the request names no number.</summary>
    public const int DefaultPageSize = 100;

    public static void Map(RouteGroupBuilder delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery.MapGet("/compute-usage", SummaryAsync).WithName("GetDeliveryComputeUsage").RequireAuthorization(ControlPlanePolicies.Admin);
        delivery.MapGet("/compute-usage/work", WorkAsync).WithName("ListDeliveryComputeWork").RequireAuthorization(ControlPlanePolicies.Admin);
        delivery.MapGet("/compute-usage/teams", TeamsAsync).WithName("ListDeliveryComputeTeams").RequireAuthorization(ControlPlanePolicies.Admin);
    }

    /// <summary>Every folder that names a team, by repository then folder.</summary>
    private static async Task<Ok<IReadOnlyList<DeliveryRepositoryTeamDto>>> TeamsAsync(OsduDbContext osdu, CatalogDbContext catalog, CancellationToken ct)
    {
        var rows = await osdu.DeliveryRepositoryTeams.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var repoIds = rows.Select(r => r.RepoId).Distinct().ToList();
        var names = repoIds.Count == 0
            ? []
            : await catalog.Repos.AsNoTracking().Where(r => repoIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryRepositoryTeamDto>>(
        [
            .. rows
                .Select(r => new DeliveryRepositoryTeamDto(
                    r.RepoId, names.GetValueOrDefault(r.RepoId), r.Folder, r.Team, DateTime.SpecifyKind(r.UpdatedUtc, DateTimeKind.Utc)))
                .OrderBy(t => t.Repo ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.RepoId)
                .ThenBy(t => t.Folder, StringComparer.Ordinal),
        ]);
    }

    private static async Task<Results<Ok<DeliveryComputeUsageDto>, ProblemHttpResult>> SummaryAsync(
        [FromQuery] string? from, [FromQuery] string? to, ComputeUsageStore store, TimeProvider clock, CancellationToken ct)
    {
        try
        {
            var (first, last) = Period(from, to, clock);
            var summary = await store.SummaryAsync(first, last, ct).ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryComputeUsageDto(
                Day(summary.From),
                Day(summary.To),
                new DeliveryComputeTotalsDto(summary.Totals.Work, summary.Totals.WallClockMs, summary.Totals.ComputeUnits),
                [.. summary.Days.Select(d => new DeliveryComputeDayDto(Day(d.Day), d.Work, d.WallClockMs, d.ComputeUnits))],
                [.. summary.Shares.Select(s => new DeliveryComputeShareDto(s.Team, s.Repo, s.Project, s.Flow, s.Kind, s.Work, s.WallClockMs, s.ComputeUnits))],
                summary.WorkKeptFromUtc));
        }
        catch (FlowValidationException invalid)
        {
            return Refused(invalid);
        }
    }

    private static async Task<Results<Ok<DeliveryComputeWorkPageDto>, ProblemHttpResult>> WorkAsync(
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? team,
        [FromQuery] string? repo,
        [FromQuery] string? project,
        [FromQuery] string? flow,
        [FromQuery] string? after,
        [FromQuery] int? take,
        ComputeUsageStore store,
        TimeProvider clock,
        CancellationToken ct)
    {
        try
        {
            var (first, last) = Period(from, to, clock);
            var page = await store.WorkAsync(
                new ComputeWorkQuery(first, last, team, repo, project, flow), string.IsNullOrWhiteSpace(after) ? null : after.Trim(), take ?? DefaultPageSize, ct)
                .ConfigureAwait(false);
            return TypedResults.Ok(new DeliveryComputeWorkPageDto(
                [.. page.Rows.Select(r => new DeliveryComputeWorkDto(
                    r.UsageId, r.Node, r.Kind, r.WorkId, r.Attempt, r.Team, r.Repo, r.Project, r.Flow, r.RequestedBy,
                    r.StartedUtc, r.EndedUtc, r.WallClockMs, r.Cores, r.Slots, r.NodeSlots, r.ComputeUnits, r.Outcome))],
                page.Next));
        }
        catch (FlowValidationException invalid)
        {
            return Refused(invalid);
        }
    }

    /// <summary>The period a request names, each end a <c>yyyy-MM-dd</c> UTC day; the last 30 days when it names neither.</summary>
    private static (DateOnly From, DateOnly To) Period(string? from, string? to, TimeProvider clock)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var last = string.IsNullOrWhiteSpace(to) ? today : ParseDay(to, "to");
        var first = string.IsNullOrWhiteSpace(from) ? last.AddDays(1 - DefaultPeriodDays) : ParseDay(from, "from");
        return (first, last);
    }

    private static DateOnly ParseDay(string value, string name)
        => DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : throw new FlowValidationException($"'{name}' is '{value}'; it is a day written yyyy-MM-dd.");

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ProblemHttpResult Refused(FlowValidationException invalid)
        => TypedResults.Problem(detail: invalid.Message, statusCode: StatusCodes.Status400BadRequest, title: "Compute usage request refused");
}
