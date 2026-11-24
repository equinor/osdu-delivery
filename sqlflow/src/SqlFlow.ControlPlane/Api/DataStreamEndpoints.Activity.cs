using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// One day of one flow's runs, totalled: what the lineage graph draws on a flow's node for that day.
/// <see cref="Day"/> counts days from the answer's <c>fromUtc</c>, the day a run's result was written, as the data
/// streams analysis counts it. <see cref="Runs"/> are the runs that finished (succeeded or failed) and
/// <see cref="Failed"/> the failed among them; a run still queued or running is in neither, and is found among the
/// day's fires. <see cref="RowsRead"/> is what the runs took in; <see cref="RowsInserted"/>,
/// <see cref="RowsUpdated"/> and <see cref="RowsDeleted"/> are what they did to their target, and
/// <see cref="Breakdown"/> says whether any run reported those at all (a run that reports only a row count wrote
/// what it read). <see cref="Files"/>, <see cref="FileRows"/> and <see cref="FileBytes"/> total the files the runs
/// took in, and <see cref="Seconds"/> how long they ran. <see cref="Counts"/> sums the counts the runs' own results
/// report (<see cref="RunResultCounts"/>), for a kind that counts something other than rows; null when no run
/// reported any.
/// </summary>
public sealed record FlowActivityDayDto(
    int Day, int Runs, int Failed,
    long RowsRead, bool Breakdown, long RowsInserted, long RowsUpdated, long RowsDeleted,
    int Files, long FileRows, long FileBytes, double Seconds,
    IReadOnlyDictionary<string, long>? Counts);

/// <summary>One flow's days with at least one run in the window, oldest first. A day with no run has no entry.</summary>
public sealed record FlowActivityFlowDto(Guid PipelineId, IReadOnlyList<FlowActivityDayDto> Days);

/// <summary>
/// What the named flows did, day by day, over the window: <see cref="WindowDays"/> days from <see cref="FromUtc"/>
/// (a UTC midnight) to <see cref="AsOfUtc"/>. A flow that had no run in the window is left out.
/// </summary>
public sealed record FlowActivityDto(
    int WindowDays, DateTime FromUtc, DateTime AsOfUtc, IReadOnlyList<FlowActivityFlowDto> Flows);

/// <summary>The files one run took in, totalled: how many, their rows and bytes, and the newest modification time
/// among them, which is when the source last changed what this run read.</summary>
public sealed record RunFilesDto(int Files, long Rows, long Bytes, DateTimeOffset? NewestModified);

/// <summary>One flow's run in a fire: the run as the runs list shows it (with the counts its result reports), how many
/// parts it split its work into over the fleet (0 when it did not), and the files it took in (null when it took
/// none).</summary>
public sealed record FlowFireRunDto(RunSummaryDto Run, int Parts, RunFilesDto? Files);

/// <summary>
/// One fire: the runs of the named flows that one start set going. A schedule's fire or a run of a flow and its
/// descendants is a run group, keyed by its <see cref="GroupId"/>; a run started on its own is a fire of one, keyed by
/// its run id. <see cref="StartedUtc"/> is when its first run started (or was queued); its runs are in execution order.
/// </summary>
public sealed record FlowFireDto(string Key, Guid? GroupId, DateTime StartedUtc, IReadOnlyList<FlowFireRunDto> Runs);

/// <summary>
/// The fires of one day through the named flows, newest first and at most
/// <see cref="DataStreamEndpoints.MaxDayFires"/>; <see cref="TotalFires"/> counts every fire of the day.
/// </summary>
public sealed record FlowFiresDto(DateTime DayUtc, IReadOnlyList<FlowFireDto> Fires, int TotalFires);

public static partial class DataStreamEndpoints
{
    /// <summary>Fires returned for one day. A day holds a handful for a daily source and twenty-four for an hourly one;
    /// past this the newest are kept and the count says how many the day had.</summary>
    public const int MaxDayFires = 50;

    /// <summary>
    /// What the named flows moved, day by day: the totals a lineage graph draws on its nodes to show where data went
    /// and where it stopped. Every run in the window counts, backfills included, because this is what happened rather
    /// than a judgement of it (the verdicts are the board's, and leave reprocessing out). A fan-out's member runs are
    /// parts of their root's work, which the root's own result reports whole, so only roots are read.
    /// </summary>
    /// <param name="db">The shadow catalog.</param>
    /// <param name="clock">The clock the window is anchored at.</param>
    /// <param name="pipelineId">The flows to read, named explicitly (repeat the parameter), at most
    /// <see cref="MaxNamedStreams"/>.</param>
    /// <param name="days">The window (1 to <see cref="MaxWindowDays"/>), counted as the board counts it.</param>
    /// <param name="ct">Cancellation.</param>
    private static async Task<Results<Ok<FlowActivityDto>, ProblemHttpResult>> GetFlowActivityAsync(
        CatalogDbContext db, TimeProvider clock, Guid[]? pipelineId, int? days, CancellationToken ct)
    {
        if (Validate(days, limit: null) is { } problem)
        {
            return problem;
        }

        if (NamedFlows(pipelineId) is not { } flowIds)
        {
            return NamedFlowsProblem();
        }

        var windowDays = days ?? DefaultWindowDays;
        var now = clock.GetUtcNow().UtcDateTime;
        var fromUtc = now.Date.AddDays(-(windowDays - 1));
        var roots = db.Runs.AsNoTracking()
            .Where(r => flowIds.Contains(r.PipelineId) && r.FanOutRoot == null && r.WrittenUtc >= fromUtc);

        var totals = await roots
            .GroupBy(r => new { r.PipelineId, Day = r.WrittenUtc.Date })
            .Select(g => new
            {
                g.Key.PipelineId,
                g.Key.Day,
                Runs = g.Count(r => r.Status == RunStatuses.Succeeded || r.Status == RunStatuses.Failed),
                Failed = g.Count(r => r.Status == RunStatuses.Failed),
                RowsRead = g.Sum(r => r.RowsLoaded ?? 0),
                Breakdown = g.Count(r => r.RowsInserted != null || r.RowsUpdated != null || r.RowsDeleted != null),
                Inserted = g.Sum(r => r.RowsInserted ?? 0),
                Updated = g.Sum(r => r.RowsUpdated ?? 0),
                Deleted = g.Sum(r => r.RowsDeleted ?? 0),
                Seconds = g.Sum(r => r.DurationSeconds ?? 0),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var files = await db.RunFiles.AsNoTracking()
            .Join(roots, f => f.RunId, r => r.RunId, (f, r) => new { r.PipelineId, Day = r.WrittenUtc.Date, f.Rows, f.SizeBytes })
            .GroupBy(f => new { f.PipelineId, f.Day })
            .Select(g => new
            {
                g.Key.PipelineId,
                g.Key.Day,
                Files = g.Count(),
                Rows = g.Sum(f => f.Rows),
                Bytes = g.Sum(f => f.SizeBytes),
            })
            .ToDictionaryAsync(f => (f.PipelineId, f.Day), ct).ConfigureAwait(false);

        // The counts a run's own result reports, summed per flow and day. Only the kinds that count something other
        // than rows record a result, and each is read as it streams past rather than held, so the cost is the results
        // of the flows named and nothing is kept but the sums.
        var counts = new Dictionary<(Guid PipelineId, DateTime Day), Dictionary<string, long>>();
        var results = roots
            .Where(r => r.ResultJson != null)
            .Select(r => new { r.PipelineId, Day = r.WrittenUtc.Date, r.ResultJson })
            .AsAsyncEnumerable();
        await foreach (var result in results.WithCancellation(ct).ConfigureAwait(false))
        {
            if (RunResultCounts.Of(result.ResultJson) is not { Count: > 0 } reported)
            {
                continue;
            }

            var key = (result.PipelineId, result.Day);
            if (!counts.TryGetValue(key, out var sums))
            {
                sums = new Dictionary<string, long>(StringComparer.Ordinal);
                counts[key] = sums;
            }

            foreach (var (name, value) in reported)
            {
                sums[name] = sums.GetValueOrDefault(name) + value;
            }
        }

        var flows = totals
            .GroupBy(t => t.PipelineId)
            .OrderBy(g => g.Key)
            .Select(g => new FlowActivityFlowDto(
                g.Key,
                g.OrderBy(t => t.Day)
                    .Select(t =>
                    {
                        var took = files.GetValueOrDefault((t.PipelineId, t.Day));
                        return new FlowActivityDayDto(
                            (t.Day - fromUtc).Days, t.Runs, t.Failed,
                            t.RowsRead, t.Breakdown > 0, t.Inserted, t.Updated, t.Deleted,
                            took?.Files ?? 0, took?.Rows ?? 0, took?.Bytes ?? 0, Math.Round(t.Seconds, 1),
                            counts.GetValueOrDefault((t.PipelineId, t.Day)));
                    })
                    .ToList()))
            .ToList();

        return TypedResults.Ok(new FlowActivityDto(windowDays, fromUtc, now, flows));
    }

    /// <summary>
    /// The fires of one day through the named flows, newest first: their runs grouped by what started them (a run
    /// group, or a run on its own), each as the runs list serves it, with the parts a fan-out split it into and the
    /// files it took in. A run belongs to the day its result was written, as <see cref="GetFlowActivityAsync"/> counts
    /// it, so a day's fires account for exactly the runs behind that day's totals.
    /// </summary>
    /// <param name="db">The shadow catalog.</param>
    /// <param name="pipelineId">The flows to read, named explicitly (repeat the parameter), at most
    /// <see cref="MaxNamedStreams"/>.</param>
    /// <param name="day">The UTC day to read.</param>
    /// <param name="ct">Cancellation.</param>
    private static async Task<Results<Ok<FlowFiresDto>, ProblemHttpResult>> GetFlowFiresAsync(
        CatalogDbContext db, Guid[]? pipelineId, DateOnly? day, CancellationToken ct)
    {
        if (NamedFlows(pipelineId) is not { } flowIds)
        {
            return NamedFlowsProblem();
        }

        if (day is not { } named)
        {
            return TypedResults.Problem(
                detail: "day is required, as a date (2026-01-31).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var fromUtc = named.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(1);
        var roots = await db.Runs.AsNoTracking()
            .Where(r => flowIds.Contains(r.PipelineId) && r.FanOutRoot == null
                && r.WrittenUtc >= fromUtc && r.WrittenUtc < toUtc)
            .Select(r => new { r.RunId, r.GroupId, Started = r.StartUtc ?? r.EnqueuedUtc ?? r.WrittenUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        var all = roots
            .GroupBy(r => r.GroupId is { } group ? group.ToString("D") : r.RunId.ToString("D"), StringComparer.Ordinal)
            .Select(g => new
            {
                g.Key,
                g.First().GroupId,
                Started = g.Min(r => r.Started),
                RunIds = g.Select(r => r.RunId).ToList(),
            })
            .OrderByDescending(f => f.Started)
            .ThenBy(f => f.Key, StringComparer.Ordinal)
            .ToList();
        var page = all.Take(MaxDayFires).ToList();

        var runIds = page.SelectMany(f => f.RunIds).ToList();
        var summaries = (await RunEndpoints.SummariesOfAsync(db, runIds, resultCounts: true, ct).ConfigureAwait(false))
            .ToDictionary(s => s.RunId);
        var parts = runIds.Count == 0
            ? []
            : await db.Runs.AsNoTracking()
                .Where(r => r.FanOutRoot != null && runIds.Contains(r.FanOutRoot.Value))
                .GroupBy(r => r.FanOutRoot!.Value)
                .Select(g => new { Root = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Root, x => x.Count, ct).ConfigureAwait(false);
        var files = runIds.Count == 0
            ? []
            : await db.RunFiles.AsNoTracking()
                .Where(f => runIds.Contains(f.RunId))
                .GroupBy(f => f.RunId)
                .Select(g => new
                {
                    RunId = g.Key,
                    Files = g.Count(),
                    Rows = g.Sum(f => f.Rows),
                    Bytes = g.Sum(f => f.SizeBytes),
                    Newest = g.Max(f => f.Modified),
                })
                .ToDictionaryAsync(x => x.RunId, x => new RunFilesDto(x.Files, x.Rows, x.Bytes, x.Newest), ct)
                .ConfigureAwait(false);

        var fires = page.Select(f => new FlowFireDto(
                f.Key,
                f.GroupId,
                f.Started,
                f.RunIds
                    .Where(summaries.ContainsKey)
                    .Select(id => summaries[id])
                    .OrderBy(run => run.Wave)
                    .ThenBy(run => run.FlowName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(run => run.WrittenUtc)
                    .Select(run => new FlowFireRunDto(run, parts.GetValueOrDefault(run.RunId), files.GetValueOrDefault(run.RunId)))
                    .ToList()))
            .ToList();
        return TypedResults.Ok(new FlowFiresDto(fromUtc, fires, all.Count));
    }

    /// <summary>The flows a request names, without repeats; null when it names none or more than one read takes.</summary>
    private static List<Guid>? NamedFlows(Guid[]? pipelineId) =>
        pipelineId is { Length: > 0 and <= MaxNamedStreams } ? pipelineId.Distinct().ToList() : null;

    private static ProblemHttpResult NamedFlowsProblem() => TypedResults.Problem(
        detail: $"Name between 1 and {MaxNamedStreams} pipelineId values.",
        statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
}
