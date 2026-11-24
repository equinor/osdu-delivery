using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.FanOut;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one dimension flow document as one platform run (osdu/docs/reference/flow/dimension-build.md, Operations). The run's
/// parameters select the operation: build (the default) or plan; the payload names the dimensions, none naming every
/// one; the partition run value names the partition, settled as every run of the module settles it. SQLFlow's
/// <c>fullLoad</c> makes a build load in full whatever the flow declares, and a backfill window names the window a flow
/// loading incrementally reads. The engine's log becomes the run log and live trace, the outcome becomes
/// <c>run.json</c>, and every build is kept in the flow's ledger. A build run in which a dimension failed ends failed,
/// still carrying its outcome, with every other dimension built. A run the platform handed a fan-out spreads a large build
/// over nodes (osdu/docs/reference/flow/dimension-build.md, Spread over nodes).
/// </summary>
public sealed class DimensionExecutor : KindExecutor<DimensionFlowDocument, DimensionFlowDefinition>
{
    public DimensionExecutor(IServiceProvider provider)
        : base(provider)
    {
    }

    protected override string FlowType => DimensionFlowDefinition.FlowTypeName;

    protected override DimensionFlowDefinition FlowOf(DimensionFlowDocument document, string sourcePath) => document.Flow with { SourcePath = sourcePath };

    protected override string FlowName(DimensionFlowDefinition flow) => flow.Name;

    protected override string TelemetryOperation(DimensionFlowDefinition flow) => flow.TelemetryOperation;

    protected override string OperationOf(RunParameters parameters) => DimensionFlowKind.Operation(parameters);

    // The fan-out the platform handed the run, through which a large build is spread over nodes (osdu/docs/reference/flow/
    // dimension-build.md, Spread over nodes); a run without one builds every dimension itself.
    protected override EngineContext ContextFor(EngineContext context, DocumentExecutionOptions options)
        => context.WithFanOut(options.FanOut is { } fanOut ? new RunFanOutDispatcher(fanOut) : null);

    protected override async Task<object> ExecuteOperationAsync(
        EngineContext context, DimensionFlowDefinition flow, string operation, RunParameters parameters, Guid runId, string actor, ILogger log, CancellationToken ct)
    {
        var payload = DeliveryRunPayload.Parse(parameters);
        var (partition, supplied) = PartitionNames.SplitRunValues(parameters.Values, keptAsParameter: false);
        var values = FlowParameters.Resolve(flow.Parameters, flow.SourcePath ?? flow.Name, supplied);

        // A hard-coded partition settles itself; a flow that leaves its partitions to the registry builds in the one named,
        // which the registry has to hold, or else the registry's default.
        var registry = flow.NeedsRegistry(partition)
            ? await context.PartitionRegistry.ReadAsync(ct).ConfigureAwait(false)
            : RegisteredPartitions.None;
        var bound = flow.ForRun(partition, registry);

        // The platform and credentials a flow names by reference resolve from the central configuration the control plane
        // supplied, the partition's own values first, before the node's own.
        var runContext = context.WithSuppliedReferences(payload.ReferencesFor(bound.Partition));
        var runner = new DimensionRunner(runContext, bound, values, log);
        var load = new DimensionLoadRequest(parameters.FullLoad, Utc(parameters.BackfillFrom), Utc(parameters.BackfillTo));
        return operation == DeliveryOperations.Plan
            ? await runner.PlanAsync(payload.Dimensions, load, ct).ConfigureAwait(false)
            : await runner.BuildAsync(payload.Dimensions, load, DimensionBuildSpread.Of(parameters), runId, actor, ct).ConfigureAwait(false);
    }

    /// <summary>A bound of a backfill window as UTC: SQLFlow carries the window in UTC, and one with no kind is taken as such.</summary>
    private static DateTime? Utc(DateTime? value)
        => value is { } v ? v.Kind == DateTimeKind.Utc ? v : v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}
