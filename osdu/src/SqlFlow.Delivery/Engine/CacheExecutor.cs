using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one cache flow document as one platform run. The run's parameters select the operation: refresh (the default; a
/// run triggered without an operation, and a scheduled run, refreshes) or plan (count what each type's search matches,
/// write nothing). The engine's log becomes the run log and the live trace, and the outcome becomes <c>run.json</c>; the
/// version a refresh writes carries the run id, so every cached value can be traced to the run that captured it.
/// </summary>
public sealed class CacheExecutor : KindExecutor<CacheFlowDocument, CacheDefinition>
{
    /// <summary>
    /// What a cache run's payload takes besides the central configuration: the types it refreshes or plans
    /// (osdu/docs/reference/flow/cache.md, Operations and the run payload). The kind checks a run with it as it is queued.
    /// </summary>
    public static readonly IReadOnlyCollection<string> PayloadTakes = [DeliveryRunPayload.TypesProperty];

    public CacheExecutor(IServiceProvider provider)
        : base(provider)
    {
    }

    protected override string FlowType => CacheDefinition.FlowTypeName;

    protected override CacheDefinition FlowOf(CacheFlowDocument document, string sourcePath) => document.Flow with { SourcePath = sourcePath };

    protected override string FlowName(CacheDefinition flow) => flow.Name;

    protected override string TelemetryOperation(CacheDefinition flow) => flow.TelemetryOperation;

    protected override string OperationOf(RunParameters parameters) => Operation(parameters);

    /// <summary>The operation a cache flow runs: refresh (its default) or plan.</summary>
    public static string Operation(RunParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return (parameters.Operation ?? DeliveryOperations.Refresh) switch
        {
            DeliveryOperations.Refresh => DeliveryOperations.Refresh,
            DeliveryOperations.Plan => DeliveryOperations.Plan,
            var other => throw new SqlFlowException(
                $"A cache flow runs the {DeliveryOperations.Refresh} and {DeliveryOperations.Plan} operations; '{other}' is not one of them."),
        };
    }

    /// <summary>
    /// Runs the operation for the partition the run names, the default when it names none, or every partition the flow
    /// serves, one after another, when it names every one (osdu/docs/reference/flow/cache.md, Which partitions a flow
    /// fills). Each partition's cache is captured and merged on its own, with the central configuration set for that
    /// partition, so a partition whose capture fails leaves the others refreshed and the run ends failed naming it. A
    /// flow whose partition is its header's runs as it always did.
    /// </summary>
    protected override async Task<object> ExecuteOperationAsync(
        EngineContext context, CacheDefinition flow, string operation, RunParameters parameters, Guid runId, string actor, ILogger log, CancellationToken ct)
    {
        var payload = DeliveryRunPayload.Parse(parameters);
        payload.RefuseOtherThan(CacheDefinition.FlowTypeName, PayloadTakes);

        var (partition, supplied) = PartitionNames.SplitRunValues(
            parameters.Values, keptAsParameter: !flow.Partitioned && flow.Parameters.ContainsKey(PartitionNames.RunValue));
        var values = FlowParameters.Resolve(flow.Parameters, flow.SourcePath ?? flow.Name, supplied);

        // A hard-coded partition settles itself; a flow that leaves its partitions to the registry builds the one named, which
        // the registry has to hold, every registered one when the run names every partition, or else the registry's default.
        var registry = flow.NeedsRegistry(partition)
            ? await context.PartitionRegistry.ReadAsync(ct).ConfigureAwait(false)
            : RegisteredPartitions.None;
        var bound = flow.ForRun(partition, registry);
        if (bound.Count == 1)
        {
            // A cache and a retrieval flow name their platform and partition the same way a delivery flow does, so a run
            // resolves them from the central configuration the control plane supplied before the node's own.
            return await RunOneAsync(context.WithSuppliedReferences(payload.ReferencesFor(bound[0].Partition)), bound[0], operation, values, payload.Types, runId, actor, log, ct)
                .ConfigureAwait(false);
        }

        var outcomes = new List<CachePartitionOutcome>(bound.Count);
        foreach (var one in bound)
        {
            ct.ThrowIfCancellationRequested();
            log.LogInformation("partition '{Partition}': {Operation}", one.Partition, operation);
            try
            {
                var outcome = await RunOneAsync(context.WithSuppliedReferences(payload.ReferencesFor(one.Partition)), one, operation, values, payload.Types, runId, actor, log, ct)
                    .ConfigureAwait(false);
                outcomes.Add(new CachePartitionOutcome(one.Partition!, outcome, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One partition's failure is its own: the others are captured and merged regardless, and the run then ends
                // failed naming every partition that did not complete.
                var error = RunFailure.Describe(ex);
                log.LogError(RunFailure.IsExpected(ex) ? null : ex, "partition '{Partition}': {Operation} failed: {Error}", one.Partition, operation, error);
                outcomes.Add(new CachePartitionOutcome(one.Partition!, null, error));
            }
        }

        var all = new CachePartitionsOutcome(operation, flow.Name, outcomes);
        return all.Complete ? all : throw new CachePartitionsIncompleteException(all);
    }

    private static async Task<object> RunOneAsync(
        EngineContext context, CacheDefinition flow, string operation, IReadOnlyDictionary<string, string> values, IReadOnlyList<string> types, Guid runId, string actor,
        ILogger log, CancellationToken ct)
    {
        // A run naming types refreshes or plans those alone; the rest of the partition's cache is left as it is.
        var narrowed = CacheRefresher.Narrow(flow, types);
        var refresher = new CacheRefresher(context, log);
        return operation == DeliveryOperations.Plan
            ? await refresher.PlanAsync(narrowed, values, ct).ConfigureAwait(false)
            : await refresher.RefreshAsync(narrowed, values, runId, actor, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The <c>result</c> of a run of a cache flow over several of its partitions: each partition's outcome (a refresh's or a
/// plan's), or the error that stopped it.
/// </summary>
public sealed record CachePartitionsOutcome(string Operation, string Flow, IReadOnlyList<CachePartitionOutcome> Partitions)
{
    /// <summary>True when every partition's operation completed.</summary>
    public bool Complete => Partitions.All(p => p.Error is null);

    /// <summary>What the run did, as a run's error states it: the partitions that did not complete and why, then the ones that did.</summary>
    public string Describe()
    {
        var failed = Partitions.Where(p => p.Error is not null).Select(p => $"partition '{p.Partition}' ({p.Error})").ToList();
        var done = Partitions.Where(p => p.Error is null).Select(p => $"'{p.Partition}'").ToList();
        return failed.Count == 0
            ? $"cache flow '{Flow}': the {Operation} of every partition completed ({string.Join(", ", done)})."
            : $"cache flow '{Flow}': the {Operation} of {string.Join(", ", failed)} did not complete; "
                + (done.Count == 0 ? "no partition completed." : $"{string.Join(", ", done)} completed.");
    }
}

/// <summary>One partition of a run over several: the outcome of its operation, or why it did not complete.</summary>
public sealed record CachePartitionOutcome(string Partition, object? Outcome, string? Error);

/// <summary>A run over several partitions in which at least one did not complete; it carries what every partition did.</summary>
public sealed class CachePartitionsIncompleteException : DeliveryException, IFailureWithOutcome
{
    public CachePartitionsIncompleteException(CachePartitionsOutcome outcome)
        : base((outcome ?? throw new ArgumentNullException(nameof(outcome))).Describe())
    {
        Outcome = outcome;
    }

    public CachePartitionsOutcome Outcome { get; }

    object IFailureWithOutcome.Outcome => Outcome;
}
