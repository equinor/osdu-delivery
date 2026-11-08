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
/// How a build's dimensions are dealt into parts: the dimension reading the most records first, each to the part reading
/// the fewest so far, so the parts end near one another, and the first part (the one the splitting run builds) holds the
/// dimension reading the most.
/// </summary>
public static class DimensionShares
{
    /// <summary>
    /// <paramref name="dimensions"/> dealt into <paramref name="parts"/> parts by the records each reads
    /// (<paramref name="records"/>, by name). Ties go by the order the flow declares the dimensions, and each part lists its
    /// dimensions in that order. A part is never empty: there are never more parts than dimensions.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<DimensionSpec>> Deal(IReadOnlyList<DimensionSpec> dimensions, IReadOnlyDictionary<string, long> records, int parts)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentOutOfRangeException.ThrowIfLessThan(parts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parts, Math.Max(1, dimensions.Count));
        var declared = dimensions.Select((d, i) => (Dimension: d, Order: i)).ToList();
        var dealt = Enumerable.Range(0, parts).Select(_ => new List<(DimensionSpec Dimension, int Order)>()).ToList();
        var load = new long[parts];
        foreach (var (dimension, order) in declared.OrderByDescending(d => records.GetValueOrDefault(d.Dimension.Name)).ThenBy(d => d.Order))
        {
            // The emptiest part first, so no part is left with nothing while another holds two; then the one reading the
            // fewest records, then the first.
            var to = Enumerable.Range(0, parts).OrderBy(p => dealt[p].Count == 0 ? 0 : 1).ThenBy(p => load[p]).ThenBy(p => p).First();
            dealt[to].Add((dimension, order));
            load[to] += records.GetValueOrDefault(dimension.Name);
        }

        return dealt.Select(part => (IReadOnlyList<DimensionSpec>)part.OrderBy(d => d.Order).Select(d => d.Dimension).ToList()).ToList();
    }
}

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

        var shares = DimensionShares.Deal(building, records, parts);
        var members = shares.Skip(1).Select(share => PartParameters(spread, scope.Partition, share, parts)).ToList();

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
            "Spread the build of {Dimensions} dimension(s), {Records} record(s) to read, over {Parts} parts (group {GroupId}), each on a node of its own. This run builds {Own}; {Queued}.",
            building.Count, total, parts, handle.GroupId, Names(shares[0]),
            string.Join("; ", shares.Skip(1).Select((share, i) => string.Create(CultureInfo.InvariantCulture, $"part {i + 2} (run {handle.RunIds[i]}) builds {Names(share)}"))));

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
        var names = (await scope.Ledger.ListDimensionsAsync(scope.Partition, _flow.LedgerId, ct).ConfigureAwait(false))
            .ToDictionary(d => d.DimensionId, d => d.Name);
        var left = new List<DimensionSpec>();
        for (var i = 0; i < members.Count; i++)
        {
            var runId = handle.RunIds[i];
            var share = shares[i + 1];
            var member = wait.State.Members.FirstOrDefault(m => m.RunId == runId)
                ?? throw new DeliveryException(
                    string.Create(CultureInfo.InvariantCulture, $"Part {i + 2} (run {runId:D}) is missing from the state of its group ({handle.GroupId:D}), so whether it built {Names(share)} cannot be told. The dimensions this run built are kept; build the flow again."));
            var kept = (await scope.Ledger.DimensionRunsOfAsync(runId, ct).ConfigureAwait(false))
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
