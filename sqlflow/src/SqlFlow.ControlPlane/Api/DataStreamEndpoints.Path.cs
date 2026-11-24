using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Api;

/// <summary>What a node of a stream's path stands for without drawing it, counted where it was cut:
/// <c>producers</c> (flows writing what this flow reads, past the ones drawn), <c>inputs</c> (objects this flow reads
/// that are not on the path), <c>outputs</c> (objects it writes that are not), <c>readers</c> (flows reading this
/// object that are not drawn, such as checks and reports, which write nothing), <c>upstream</c> and <c>downstream</c>
/// (flows past the depth the path is walked to). <see cref="Count"/> is the whole number; <see cref="Names"/> lists at
/// most <see cref="StreamPathWalk.MaxNamesListed"/> of them, flows by name and objects by qualified name.</summary>
public sealed record StreamPathOmissionDto(string What, int Count, IReadOnlyList<string> Names);

/// <summary>
/// One node of a stream's path. A <c>flow</c> carries its pipeline id, kind and execution wave; an <c>object</c> its
/// lineage key, its kind as the lineage graph captions it (<c>table</c>, <c>view</c>, <c>file</c>, a dataset's system)
/// and its qualified name. <see cref="Role"/> says which side of the selected flow it was reached from (<c>selected</c>,
/// <c>upstream</c>, <c>downstream</c>); <see cref="External"/> marks an object nothing in the estate writes, which is
/// where data enters it.
/// </summary>
public sealed record StreamPathNodeDto(
    string Id, string Type, string Role, string Name, string Kind,
    Guid? PipelineId, int? Wave,
    string? ObjectKey, string? QualifiedName, bool External,
    IReadOnlyList<StreamPathOmissionDto> Omitted);

/// <summary>Data moving from one node of a path to the next: a flow writing an object, or an object read by a flow.</summary>
public sealed record StreamPathEdgeDto(string From, string To);

/// <summary>The files one run took in, totalled: how many, their rows and bytes, and the newest modification time
/// among them, which is when the source last changed what this run read.</summary>
public sealed record StreamPathFilesDto(int Files, long Rows, long Bytes, DateTimeOffset? NewestModified);

/// <summary>One flow's run in a fire: the run as the runs list shows it (with the counts its result reports), how many
/// parts it split its work into over the fleet (0 when it did not), and the files it took in (null when it took
/// none).</summary>
public sealed record StreamPathRunDto(RunSummaryDto Run, int Parts, StreamPathFilesDto? Files);

/// <summary>
/// One fire through the path: the runs of the path's flows that one start set going. A schedule's fire or a run of a
/// flow and its descendants is a run group, keyed by its <see cref="GroupId"/>; a run started on its own is a fire of
/// one, keyed by its run id. <see cref="StartedUtc"/> is when its first run started (or was queued); its runs are in
/// execution order.
/// </summary>
public sealed record StreamPathFireDto(string Key, Guid? GroupId, DateTime StartedUtc, IReadOnlyList<StreamPathRunDto> Runs);

/// <summary>
/// One data stream followed end to end: the path its data takes (where it enters the estate, every flow and object it
/// goes through, where it ends), each flow's verdict over the window as the board judges it, and the latest fires
/// through the path with what each flow's run did in them. <see cref="LineageKnown"/> is false for a flow with no
/// lineage at all, whose path is the flow alone. <see cref="Fires"/> is newest first and at most
/// <see cref="DataStreamEndpoints.MaxPathFires"/>; <see cref="TotalFires"/> counts every fire in the window, and
/// <see cref="OlderThanUtc"/>, when set, is the <c>before</c> to ask with for the fires older than these.
/// </summary>
public sealed record StreamPathDto(
    Guid PipelineId, string FlowName, int WindowDays, DateTime FromUtc, DateTime AsOfUtc, bool IncludeBackfills,
    bool LineageKnown,
    IReadOnlyList<StreamPathNodeDto> Nodes, IReadOnlyList<StreamPathEdgeDto> Edges,
    IReadOnlyList<DataStreamDto> Streams,
    IReadOnlyList<StreamPathFireDto> Fires, int TotalFires, DateTime? OlderThanUtc);

public static partial class DataStreamEndpoints
{
    /// <summary>Fires returned per read of a stream's path. Forty covers more than a month of a daily source; older
    /// fires are read page by page with <c>before</c>.</summary>
    public const int MaxPathFires = 40;

    /// <summary>
    /// One stream followed end to end: what the vendor delivered, and what every flow after it did with the data. The
    /// path is the selected flow's lineage cut down to the data's journey (<see cref="StreamPathWalk"/>), each flow on it
    /// carries the board's own verdict (the same analysis, so the two can never disagree), and the fires say what each
    /// flow's run did on each start, with the files the vendor's delivery brought in.
    /// </summary>
    /// <param name="pipelineId">The flow to follow.</param>
    /// <param name="db">The shadow catalog.</param>
    /// <param name="clock">The clock the window is anchored at.</param>
    /// <param name="options">Deployment configuration, for how the board classifies streams.</param>
    /// <param name="days">The window (1 to <see cref="MaxWindowDays"/>), for the verdicts and the fires.</param>
    /// <param name="includeBackfills">Count backfills as normal traffic in the verdicts, as on the board. The fires list
    /// every run whatever this says: a backfill is part of what happened, and its runs say they were one.</param>
    /// <param name="before">Only fires that started before this instant: the <c>olderThanUtc</c> of the previous read,
    /// to page back through the window.</param>
    /// <param name="ct">Cancellation.</param>
    private static async Task<Results<Ok<StreamPathDto>, NotFound, ProblemHttpResult>> GetStreamPathAsync(
        Guid pipelineId, CatalogDbContext db, TimeProvider clock, IOptions<ControlPlaneOptions> options,
        int? days, bool? includeBackfills, DateTime? before, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Validate(days, limit: null) is { } problem)
        {
            return problem;
        }

        // The flows a path may go through: every active one, and the selected flow even when it left the estate, so a
        // stream on the board can always be followed from its row.
        var flows = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active || p.Id == pipelineId)
            .Select(p => new { p.Id, p.Name, p.Kind, p.Wave })
            .ToDictionaryAsync(p => p.Id, p => new StreamPathFlow(p.Id, p.Name, p.Kind, p.Wave), ct)
            .ConfigureAwait(false);
        if (!flows.TryGetValue(pipelineId, out var selected))
        {
            return TypedResults.NotFound();
        }

        var graph = await LoadScopeGraphAsync(db, repoId: null, ct).ConfigureAwait(false);
        var walk = StreamPathWalk.Walk(
            new StreamPathGraph
            {
                Flows = flows,
                Reads = graph.Reads,
                Writes = graph.Writes,
                // The board's target where it names one, so a flow's row and its node agree; otherwise the first
                // object the flow writes, declared first, so a flow writing only what the registry does not hold
                // (an export's file location) still shows where its data goes.
                Targets = graph.Writes
                    .Where(w => w.Value.Count > 0)
                    .ToDictionary(
                        w => w.Key,
                        w => graph.Targets.GetValueOrDefault(w.Key).Key ?? w.Value[0]),
            },
            pipelineId);

        var objectNames = await ResolveObjectsAsync(db, walk, ct).ConfigureAwait(false);
        var flowIds = walk.Nodes.Where(n => n.FlowId is not null).Select(n => n.FlowId!.Value).ToList();

        // The verdicts exactly as the board computes them: every scope and schedule state, because a path names its
        // flows and a flow on it is not less on it for being internal or unscheduled.
        var report = await ComputeAsync(
            db, clock, options.Value.DataStreams, days ?? DefaultWindowDays, repoId: null, batch: null,
            includeBackfills == true, includeUnscheduled: true, AllScopes, flowIds, includeSeries: false, ct, graph)
            .ConfigureAwait(false);

        var (fires, total, olderThan) = await ReadFiresAsync(
            db, flowIds, report.FromUtc, before is { } b ? RunEndpoints.ToUtc(b) : null, ct).ConfigureAwait(false);

        var nodes = walk.Nodes.Select(node =>
        {
            var omitted = node.Omitted
                .Select(o => new StreamPathOmissionDto(
                    o.Key,
                    o.Value.Count,
                    o.Value
                        .Select(name => o.Key is StreamPathOmissions.Inputs or StreamPathOmissions.Outputs
                            ? objectNames.GetValueOrDefault(name)?.Qualified ?? name
                            : name)
                        .Take(StreamPathWalk.MaxNamesListed)
                        .ToList()))
                .ToList();

            if (node.FlowId is { } id)
            {
                var flow = flows[id];
                return new StreamPathNodeDto(
                    node.Id, "flow", node.Role, flow.Name, flow.Kind, id, flow.Wave, null, null, false, omitted);
            }

            var key = node.ObjectKey!;
            var named = objectNames.GetValueOrDefault(key);
            return new StreamPathNodeDto(
                node.Id, "object", node.Role, named?.Name ?? key, named?.Kind ?? "table", null, null, key,
                named?.Qualified, node.External, omitted);
        }).ToList();

        return TypedResults.Ok(new StreamPathDto(
            pipelineId, selected.Name, report.WindowDays, report.FromUtc, report.AsOfUtc, report.IncludeBackfills,
            walk.LineageKnown,
            nodes,
            walk.Edges.Select(e => new StreamPathEdgeDto(e.From, e.To)).ToList(),
            report.Streams,
            fires, total, olderThan));
    }

    /// <summary>How one object is named on a path: its bare name, its qualified name, and its kind as drawings caption
    /// it.</summary>
    private sealed record PathObjectName(string Name, string Qualified, string Kind);

    /// <summary>
    /// The names of every object the path draws or counts, from the object registry, or from the name a lineage edge
    /// recorded for an object the registry does not hold (a file location a flow reads, which only its flows name).
    /// </summary>
    private static async Task<Dictionary<string, PathObjectName>> ResolveObjectsAsync(
        CatalogDbContext db, StreamPathWalkResult walk, CancellationToken ct)
    {
        var keys = walk.Nodes.Where(n => n.ObjectKey is not null).Select(n => n.ObjectKey!)
            .Concat(walk.Nodes.SelectMany(n => n.Omitted
                .Where(o => o.Key is StreamPathOmissions.Inputs or StreamPathOmissions.Outputs)
                .SelectMany(o => o.Value.Take(StreamPathWalk.MaxNamesListed))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var names = new Dictionary<string, PathObjectName>(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0)
        {
            return names;
        }

        var registered = await db.Objects.AsNoTracking()
            .Where(o => keys.Contains(o.Key))
            .Select(o => new { o.Key, o.Database, o.Schema, o.Name, o.Kind })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var o in registered)
        {
            names.TryAdd(o.Key, new PathObjectName(
                o.Name,
                Qualify(o.Database, o.Schema, o.Name),
                LineageObjectKinds.OfKey(o.Key) ?? LineageObjectKinds.OfRegistry(o.Kind) ?? "table"));
        }

        var unregistered = keys.Where(key => !names.ContainsKey(key)).ToList();
        if (unregistered.Count > 0)
        {
            var recorded = await db.LineageEdges.AsNoTracking()
                .Where(e => unregistered.Contains(e.ObjectKey))
                .Select(e => new { e.ObjectKey, e.ObjectName })
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var e in recorded.OrderBy(e => e.ObjectName, StringComparer.Ordinal))
            {
                var name = string.IsNullOrWhiteSpace(e.ObjectName) ? e.ObjectKey : e.ObjectName;
                names.TryAdd(e.ObjectKey, new PathObjectName(name, name, LineageObjectKinds.OfKey(e.ObjectKey) ?? "table"));
            }
        }

        return names;
    }

    /// <summary>
    /// The fires through a path inside the window, newest first: the runs of its flows grouped by what started them
    /// (a run group, or a run on its own). A fan-out's members are parts of their root's run rather than runs of their
    /// own, so only roots are read, and each carries how many parts it split into. Every run in the window is listed,
    /// backfills included: they are part of what happened, and each run's summary says how it was scoped.
    /// </summary>
    private static async Task<(List<StreamPathFireDto> Fires, int Total, DateTime? OlderThan)> ReadFiresAsync(
        CatalogDbContext db, IReadOnlyList<Guid> flowIds, DateTime fromUtc, DateTime? beforeUtc, CancellationToken ct)
    {
        if (flowIds.Count == 0)
        {
            return ([], 0, null);
        }

        var roots = await db.Runs.AsNoTracking()
            .Where(r => flowIds.Contains(r.PipelineId) && r.FanOutRoot == null && r.WrittenUtc >= fromUtc)
            .Select(r => new { r.RunId, r.GroupId, Started = r.StartUtc ?? r.EnqueuedUtc ?? r.WrittenUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        var all = roots
            .GroupBy(r => r.GroupId is { } group ? group.ToString("D") : r.RunId.ToString("D"), StringComparer.Ordinal)
            .Select(g => new
            {
                Key = g.Key,
                g.First().GroupId,
                Started = g.Min(r => r.Started),
                RunIds = g.Select(r => r.RunId).ToList(),
            })
            .OrderByDescending(f => f.Started)
            .ThenBy(f => f.Key, StringComparer.Ordinal)
            .ToList();
        var eligible = beforeUtc is { } cutoff ? all.Where(f => f.Started < cutoff).ToList() : all;
        var page = eligible.Take(MaxPathFires).ToList();
        DateTime? olderThan = eligible.Count > page.Count ? page[^1].Started : null;

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
                .ToDictionaryAsync(x => x.RunId, x => new StreamPathFilesDto(x.Files, x.Rows, x.Bytes, x.Newest), ct)
                .ConfigureAwait(false);

        var fires = page.Select(f => new StreamPathFireDto(
                f.Key,
                f.GroupId,
                f.Started,
                f.RunIds
                    .Where(summaries.ContainsKey)
                    .Select(id => summaries[id])
                    .OrderBy(run => run.Wave)
                    .ThenBy(run => run.FlowName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(run => run.WrittenUtc)
                    .Select(run => new StreamPathRunDto(run, parts.GetValueOrDefault(run.RunId), files.GetValueOrDefault(run.RunId)))
                    .ToList()))
            .ToList();
        return (fires, all.Count, olderThan);
    }
}
