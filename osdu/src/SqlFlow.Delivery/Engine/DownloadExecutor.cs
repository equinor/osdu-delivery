using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Downloads;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one download flow document as one platform run (osdu/docs/reference/flow/download.md, Operations). The run's
/// parameters select the operation: download (the default) or plan; the payload names the downloads, none naming every one,
/// and whether every record is written again (<c>force</c>); the partition run value names the partition, settled as every
/// run of the module settles it. The engine's log becomes the run log and live trace, the outcome becomes <c>run.json</c>,
/// and every run is kept with its download. A run in which a download failed ends failed, still carrying its outcome, with
/// every other download done.
/// </summary>
public sealed class DownloadExecutor : KindExecutor<DownloadFlowDocument, DownloadFlowDefinition>
{
    public DownloadExecutor(IServiceProvider provider)
        : base(provider)
    {
    }

    protected override string FlowType => DownloadFlowDefinition.FlowTypeName;

    protected override DownloadFlowDefinition FlowOf(DownloadFlowDocument document, string sourcePath) => document.Flow with { SourcePath = sourcePath };

    protected override string FlowName(DownloadFlowDefinition flow) => flow.Name;

    protected override string TelemetryOperation(DownloadFlowDefinition flow) => flow.TelemetryOperation;

    protected override string OperationOf(RunParameters parameters) => DownloadFlowKind.Operation(parameters);

    protected override async Task<object> ExecuteOperationAsync(
        EngineContext context, DownloadFlowDefinition flow, string operation, RunParameters parameters, Guid runId, string actor, ILogger log, CancellationToken ct)
    {
        var payload = DeliveryRunPayload.Parse(parameters);
        var (partition, supplied) = PartitionNames.SplitRunValues(parameters.Values, keptAsParameter: false);
        var values = FlowParameters.Resolve(flow.Parameters, flow.SourcePath ?? flow.Name, supplied);

        // A hard-coded partition settles itself; a flow that leaves its partitions to the registry reads the one named, which the
        // registry has to hold, or else the registry's default.
        var registry = flow.NeedsRegistry(partition)
            ? await context.PartitionRegistry.ReadAsync(ct).ConfigureAwait(false)
            : RegisteredPartitions.None;
        var bound = flow.ForRun(partition, registry);

        // The platform and credentials a flow names by reference resolve from the central configuration the control plane
        // supplied, the partition's own values first, before the node's own.
        var runContext = context.WithSuppliedReferences(payload.ReferencesFor(bound.Partition));
        var runner = new DownloadRunner(runContext, bound, values, log);
        return operation == DeliveryOperations.Plan
            ? await runner.PlanAsync(payload.Downloads, ct).ConfigureAwait(false)
            : await runner.DownloadAsync(payload.Downloads, payload.Force, runId, actor, ct).ConfigureAwait(false);
    }
}
