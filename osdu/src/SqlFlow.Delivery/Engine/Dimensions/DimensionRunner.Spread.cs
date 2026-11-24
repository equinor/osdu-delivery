using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Dimensions;

/// <summary>
/// What a build run knows of spreading a build over nodes (osdu/docs/reference/flow/dimension-build.md, Spread over nodes):
/// the parameters and payload it was given, which each part it queues copies with its own dimensions and the rate share set;
/// or, for a part, the payload naming the runs it shares the flow's rate limit with.
/// </summary>
public sealed record DimensionBuildSpread
{
    /// <summary>A build that cannot be spread: run outside the fleet, or by a caller that hands it no parameters.</summary>
    public static DimensionBuildSpread None { get; } = new();

    /// <summary>The parameters the run was given, which each part it queues copies: the operation, values, full load and window.</summary>
    public RunParameters? Parameters { get; init; }

    /// <summary>The payload the run was given, the central configuration among it, which each part it queues carries.</summary>
    public DeliveryRunPayload Payload { get; init; } = DeliveryRunPayload.None;

    /// <summary>For a part of a split build, the runs sharing the flow's rate limit; null for every other build.</summary>
    public int? RateShare => Payload.RateShare;

    /// <summary>
    /// Whether this run is a part of a build another run split: it builds the dimensions it names, is never split again,
    /// and leaves the views to the run that split the build.
    /// </summary>
    public bool IsPart => RateShare is not null;

    /// <summary>The spread of a run given <paramref name="parameters"/>, its payload read from them.</summary>
    public static DimensionBuildSpread Of(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new DimensionBuildSpread { Parameters = parameters, Payload = DeliveryRunPayload.Parse(parameters) };
    }
}

/// <summary>
/// How a build's dimensions are dealt into parts (osdu/docs/reference/flow/dimension-build.md, Spread over nodes): each
/// weighed by what it is expected to cost, the heaviest first, each to the part carrying the least so far, so the parts end
/// near one another, and the first part (the one the splitting run builds) holds the heaviest.
/// </summary>
public static class DimensionShares
{
    /// <summary>
    /// What a dimension is expected to cost, in seconds where the ledger has seen the flow build: the records it reads now
    /// times the seconds a record took in its last full build. A record of a dimension that writes many rows of each (a
    /// well log's curves) costs more than one of a dimension that writes one, and its last full build, read and write
    /// together, says by how much. A dimension with no full build yet is weighed at the flow's average over the dimensions
    /// that have one; when none has, every dimension is weighed by its records alone.
    /// </summary>
    /// <param name="records">The records each dimension reads now, by name.</param>
    /// <param name="lastFull">Each dimension's last full build, by name: how long it took and how many records it read.</param>
    public static IReadOnlyDictionary<string, double> Costs(IReadOnlyDictionary<string, long> records, IReadOnlyDictionary<string, DimensionBuildCost> lastFull)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(lastFull);
        var known = lastFull.Where(b => records.ContainsKey(b.Key) && b.Value is { Records: > 0, Seconds: > 0 }).ToDictionary(b => b.Key, b => b.Value, StringComparer.Ordinal);
        if (known.Count == 0)
        {
            return records.ToDictionary(r => r.Key, r => (double)r.Value, StringComparer.Ordinal);
        }

        var average = known.Values.Sum(b => b.Seconds) / known.Values.Sum(b => (double)b.Records);
        return records.ToDictionary(
            r => r.Key,
            r => r.Value * (known.TryGetValue(r.Key, out var build) ? build.Seconds / build.Records : average),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// <paramref name="dimensions"/> dealt into <paramref name="parts"/> parts by what each is expected to cost
    /// (<paramref name="costs"/>, by name; see <see cref="Costs"/>). Ties go by the order the flow declares the dimensions,
    /// and each part lists its dimensions in that order. A part is never empty: there are never more parts than dimensions.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<DimensionSpec>> Deal(IReadOnlyList<DimensionSpec> dimensions, IReadOnlyDictionary<string, double> costs, int parts)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parts, Math.Max(1, dimensions.Count));
        var declared = dimensions.Select((d, i) => (Dimension: d, Order: i)).ToList();
        var dealt = Enumerable.Range(0, parts).Select(_ => new List<(DimensionSpec Dimension, int Order)>()).ToList();
        var load = new double[parts];
        foreach (var (dimension, order) in declared.OrderByDescending(d => costs.GetValueOrDefault(d.Dimension.Name)).ThenBy(d => d.Order))
        {
            // The emptiest part first, so no part is left with nothing while another holds two; then the one carrying the
            // least, then the first.
            var to = Enumerable.Range(0, parts).OrderBy(p => dealt[p].Count == 0 ? 0 : 1).ThenBy(p => load[p]).ThenBy(p => p).First();
            dealt[to].Add((dimension, order));
            load[to] += costs.GetValueOrDefault(dimension.Name);
        }

        return dealt.Select(part => (IReadOnlyList<DimensionSpec>)part.OrderBy(d => d.Order).Select(d => d.Dimension).ToList()).ToList();
    }
}

/// <summary>A dimension's last full build as the deal weighs it: how long it took, its read and its write, and the records it read.</summary>
public sealed record DimensionBuildCost(double Seconds, long Records);

// A build spread over the fleet (osdu/docs/reference/flow/dimension-build.md, Spread over nodes). The build run counts the
// records each dimension reads, as a plan does, and from them how many parts it is split into (parallelism.nodes at most, a
// part per parallelism.minRecordsPerNode records, never more than the dimensions). It deals the dimensions into the parts,
// queues a run of the flow for every part but the first, each asking for a node of its own, and builds the first part
// itself meanwhile. A part builds its dimensions as any build does, keeping each in the ledger under its own run, and
// writes no view. Once its own part is built the run waits for the others, builds the dimensions of any part that did not
// build them (one no node started, one that stopped or failed before it kept them), and writes the views.
public sealed partial class DimensionRunner
{
    /// <summary>How often a build spread over nodes asks the platform how the parts it queued are doing.</summary>
    internal TimeSpan MemberPoll { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a build spread over nodes waits, once its own part is built, for parts no node has started when none is
    /// running and none started or finished meanwhile, before it builds their dimensions itself. Long enough for an
    /// autoscaler to bring up a node for them; past it the pool has no place free.
    /// </summary>
    internal TimeSpan MemberStartGrace { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>What every dimension build of one run shares: the ledger, the search, the templates, and who asked for what.</summary>
    private sealed record BuildScope(
        ILedger Ledger, OsduSearch Search, TemplateCache Templates, string Partition, DimensionLoadRequest Load, Guid RunId, string Actor);

    /// <summary>
    /// The build spread over nodes, or null when this run builds every dimension itself: the flow keeps its builds on one
    /// node, the run cannot queue runs, it builds one dimension, the records could not be counted, or they are too few for
    /// two parts. Spread, it returns how every dimension came out, whoever built it.
    /// </summary>
    private async Task<List<DimensionBuildSummary>?> SpreadAsync(
        BuildScope scope, IReadOnlyList<DimensionSpec> building, DimensionBuildSpread spread, HttpRuntime http, CancellationToken ct)
    {
        var settings = _flow.Parallelism;
        var unavailable = settings.Nodes <= 1 ? "parallelism.nodes is 1"
            : building.Count < 2 ? "it builds one dimension, and a dimension is built by one run"
            : !_context.Dispatcher.Available || spread.Parameters is null ? "this run cannot queue runs of the flow: it runs outside the fleet"
            : null;
        if (unavailable is not null)
        {
            if (settings.Nodes > 1 && building.Count > 1)
            {
                _log.LogInformation("The build is not spread over nodes: {Reason}.", unavailable);
            }

            return null;
        }

        var records = await RecordsToReadAsync(scope, building, ct).ConfigureAwait(false);
        if (records is null)
        {
            return null;
        }

        var total = records.Values.Sum();
        var parts = settings.PartsFor(total, building.Count);
        if (parts < 2)
        {
            _log.LogInformation(
                "The build is not spread over nodes: its {Dimensions} dimension(s) read {Records} record(s), fewer than two parts of parallelism.minRecordsPerNode ({PerNode}), so one node builds them.",
                building.Count, total, settings.MinRecordsPerNode);
            return null;
        }

        var lastFull = await LastFullBuildsAsync(scope, building, ct).ConfigureAwait(false);
        var costs = DimensionShares.Costs(records, lastFull);
        var shares = DimensionShares.Deal(building, costs, parts);
        var members = shares.Skip(1).Select(share => PartParameters(spread, scope.Partition, share, parts)).ToList();
        string Share(IReadOnlyList<DimensionSpec> share)
            => lastFull.Count == 0
                ? Names(share)
                : string.Create(CultureInfo.InvariantCulture, $"{Names(share)} (about {Took(share.Sum(d => costs.GetValueOrDefault(d.Name)))})");

        // Every part shares the flow's rate limit, this run's among them, so the build as a whole keeps to the rate.
        http.RateLimit.Share(parts);
        var enqueued = _context.Time.GetUtcNow();
        var handle = await _context.Dispatcher.EnqueueAsync(members, wholeNode: true, ct).ConfigureAwait(false);
        if (handle.RunIds.Count != members.Count)
        {
            await FanOutWait.CancelAsync(_context, _log, handle).ConfigureAwait(false);
            throw new DeliveryException(
                string.Create(CultureInfo.InvariantCulture, $"The platform answered the spread of dimension flow '{_flow.Name}' over {parts} parts with {handle.RunIds.Count} run(s) for the {members.Count} it was asked to queue, so which run builds which dimensions cannot be told. Nothing was built; build the flow again."));
        }

        _log.LogInformation(
            "Spread the build of {Dimensions} dimension(s), {Records} record(s) to read, over {Parts} parts (group {GroupId}), each on a node of its own, dealt by {Weighed}. This run builds {Own}; {Queued}.",
            building.Count, total, parts, handle.GroupId,
            lastFull.Count == 0 ? "the records each reads, since none has a full build yet" : "the time each is expected to take, from its last full build",
            Share(shares[0]),
            string.Join("; ", shares.Skip(1).Select((share, i) => string.Create(CultureInfo.InvariantCulture, $"part {i + 2} (run {handle.RunIds[i]}) builds {Share(share)}"))));

        List<DimensionBuildSummary> summaries;
        FanOutWaitResult wait;
        try
        {
            summaries = await BuildHereAsync(scope, shares[0], ct).ConfigureAwait(false);
            wait = await FanOutWait.UntilEndedAsync(_context, _log, handle, enqueued, MemberStartGrace, MemberPoll, "builds their dimensions itself", ct).ConfigureAwait(false);
        }
        catch
        {
            // The run stops before its parts ended (it was cancelled, or failed outside a dimension): they stop with it.
            await FanOutWait.CancelAsync(_context, _log, handle).ConfigureAwait(false);
            throw;
        }

        // What each part kept is read from the ledger, which holds every build under the run that made it: a dimension a part
        // completed or failed is that part's, and one it never built, or stopped building, is built here.
        var names = (await scope.Ledger.Dimensions.ListAsync(scope.Partition, _flow.LedgerId, ct).ConfigureAwait(false))
            .ToDictionary(d => d.DimensionId, d => d.Name);
        var left = new List<DimensionSpec>();
        for (var i = 0; i < members.Count; i++)
        {
            var runId = handle.RunIds[i];
            var share = shares[i + 1];
            var member = wait.State.Members.FirstOrDefault(m => m.RunId == runId)
                ?? throw new DeliveryException(
                    string.Create(CultureInfo.InvariantCulture, $"Part {i + 2} (run {runId:D}) is missing from the state of its group ({handle.GroupId:D}), so whether it built {Names(share)} cannot be told. The dimensions this run built are kept; build the flow again."));
            var kept = (await scope.Ledger.Dimensions.RunsOfAsync(runId, ct).ConfigureAwait(false))
                .Where(b => b.Status is DimensionRunStatus.Completed or DimensionRunStatus.Failed && names.ContainsKey(b.DimensionId))
                .GroupBy(b => names[b.DimensionId], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.DimensionRunId).First(), StringComparer.Ordinal);
            var notBuilt = new List<DimensionSpec>();
            foreach (var dimension in share)
            {
                if (kept.TryGetValue(dimension.Name, out var build))
                {
                    summaries.Add(Kept(dimension, build));
                }
                else
                {
                    notBuilt.Add(dimension);
                }
            }

            if (notBuilt.Count > 0 && !wait.Stopped.Contains(runId))
            {
                _log.LogWarning(
                    "Part {Part} (run {RunId}) {Outcome} without building {Dimensions}; this run builds them.",
                    i + 2, runId, member.Succeeded ? "succeeded" : $"{member.Status} ({member.Error ?? "no error recorded"})", Names(notBuilt));
            }

            left.AddRange(notBuilt);
        }

        if (left.Count > 0)
        {
            _log.LogInformation("Building {Dimensions} here, the dimensions of parts that did not build them.", Names(left));
            summaries.AddRange(await BuildHereAsync(scope, left, ct).ConfigureAwait(false));
        }

        return summaries;
    }

    /// <summary>
    /// The records each dimension of <paramref name="building"/> reads, as a plan counts them (<see cref="RecordsReadAsync"/>);
    /// null, saying why, when a count fails, and the build is then not spread.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, long>?> RecordsToReadAsync(BuildScope scope, IReadOnlyList<DimensionSpec> building, CancellationToken ct)
    {
        var records = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        try
        {
            await Parallel.ForEachAsync(
                building,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, _flow.Parallelism.RecordsAtOnce), CancellationToken = ct },
                async (dimension, token) =>
                {
                    var query = Query(dimension, scope.Partition);
                    var loading = await PlanLoadAsync(scope.Ledger, dimension, query, scope.Load, Now, token).ConfigureAwait(false);
                    records[dimension.Name] = await RecordsReadAsync(scope.Search, dimension, query, loading, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (Exception ex) when (Expected(ex, ct))
        {
            _log.LogWarning("The build is not spread over nodes: counting the records its dimensions read failed, so one node builds them: {Error}", SecretHygiene.RedactedMessage(ex));
            return null;
        }

        return records;
    }

    /// <summary>
    /// The last full build of each dimension of <paramref name="building"/> the ledger holds in the run's partition, by name:
    /// how long it took and the records it read, which the deal weighs a dimension by (<see cref="DimensionShares.Costs"/>).
    /// A dimension never built in full, or whose last full build kept no count of its records, has none.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, DimensionBuildCost>> LastFullBuildsAsync(BuildScope scope, IReadOnlyList<DimensionSpec> building, CancellationToken ct)
    {
        var names = building.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var dimensions = (await scope.Ledger.Dimensions.ListAsync(scope.Partition, _flow.LedgerId, ct).ConfigureAwait(false))
            .Where(d => names.Contains(d.Name) && d.LastFullRunId is not null)
            .ToDictionary(d => d.LastFullRunId!.Value, d => d.Name);
        if (dimensions.Count == 0)
        {
            return new Dictionary<string, DimensionBuildCost>(StringComparer.Ordinal);
        }

        return (await scope.Ledger.Dimensions.GetRunsAsync(dimensions.Keys, ct).ConfigureAwait(false))
            .Where(b => b.Status == DimensionRunStatus.Completed && b.CompletedUtc is not null && b.Read.Records is > 0)
            .ToDictionary(b => dimensions[b.DimensionRunId], b => new DimensionBuildCost((b.CompletedUtc!.Value - b.StartedUtc).TotalSeconds, b.Read.Records!.Value), StringComparer.Ordinal);
    }

    /// <summary>A number of seconds as an operator reads a duration: <c>40 s</c>, <c>12 min</c>, <c>1 h 5 min</c>.</summary>
    private static string Took(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalMinutes < 1 ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0} s")
            : span.TotalHours < 1 ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalMinutes:0} min")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h {span.Minutes} min");
    }

    /// <summary>
    /// The records a build of <paramref name="dimension"/> loaded as <paramref name="loading"/> says reads: those the query
    /// matches for a full load, those that changed in the window for an incremental one.
    /// </summary>
    private static Task<long> RecordsReadAsync(OsduSearch search, DimensionSpec dimension, string? query, LoadPlan loading, CancellationToken ct)
        => search.CountAsync(
            new OsduSearchQuery
            {
                Kind = dimension.Kind,
                Query = loading.Mode == DimensionRunModes.Incremental
                    ? OsduSearch.Within(query, RecordChanges.Within(RecordChanges.ModifyTime, loading.From, loading.To))
                    : query,
                ReturnedFields = ["id"],
            },
            ct);

    /// <summary>
    /// The run a part is queued as: this run's parameters, its payload naming the part's dimensions and the runs sharing the
    /// rate, and the partition this run builds in, so the part binds to the same partition and keeps its builds in the same ledger.
    /// </summary>
    private RunParameters PartParameters(DimensionBuildSpread spread, string partition, IReadOnlyList<DimensionSpec> share, int parts)
    {
        var parameters = spread.Parameters ?? throw new InvalidOperationException("A build is spread only by a run that was handed its parameters.");
        var values = _flow.Partitioned
            ? new Dictionary<string, string>(parameters.Values, StringComparer.Ordinal) { [PartitionNames.RunValue] = partition }
            : parameters.Values;
        return parameters with
        {
            Operation = DeliveryOperations.Build,
            Values = values,
            Payload = (spread.Payload with { Dimensions = share.Select(d => d.Name).ToList(), RateShare = parts }).ToJson(),
        };
    }

    /// <summary>How a part's build of <paramref name="dimension"/> came out, read from the ledger as the part kept it.</summary>
    private static DimensionBuildSummary Kept(DimensionSpec dimension, DimensionRunState build)
    {
        if (build.Status != DimensionRunStatus.Failed)
        {
            return Summary(dimension, build, build.Read, build.AggregateBy);
        }

        var read = build.Read;
        return new DimensionBuildSummary(dimension.Name, DimensionRunStatus.Failed, build.DimensionRunId, 0, 0, 0, 0, 0, DimensionBuildChanges.None,
            read.Aggregations + read.ScanPages, null, build.Error, read.Notes.Take(DimensionBuildOutcome.MaxNotes).ToList())
        {
            Load = read.Mode,
            WindowFrom = read.WindowFrom,
            WindowTo = read.WindowTo,
            ChangedRecords = read.ChangedRecords,
            TouchedKeys = read.TouchedKeys,
        };
    }

    private static string Names(IEnumerable<DimensionSpec> dimensions) => string.Join(", ", dimensions.Select(d => d.Name));
}
