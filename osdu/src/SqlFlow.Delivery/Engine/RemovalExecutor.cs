using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Removals;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one removal flow document as one platform run (osdu/docs/reference/flow/removal.md, A run). The run's parameters select
/// the operation, remove (the default) or plan; the payload names the removals (none naming every one) and, for an approval, how
/// many held keys the operator was shown and the partition it confirms; the partition run value names the partition, settled as
/// every run of the module settles it. The engine's log becomes the run log and live trace, and the outcome becomes
/// <c>run.json</c>. A run in which a removal failed, or held what it found for approval, ends failed, still carrying its outcome,
/// with every other removal done.
/// </summary>
public sealed class RemovalExecutor : KindExecutor<RemovalFlowDocument, RemovalFlowDefinition>
{
    public RemovalExecutor(IServiceProvider provider)
        : base(provider)
    {
    }

    protected override string FlowType => RemovalFlowDefinition.FlowTypeName;

    protected override RemovalFlowDefinition FlowOf(RemovalFlowDocument document, string sourcePath) => document.Flow with { SourcePath = sourcePath };

    protected override string FlowName(RemovalFlowDefinition flow) => flow.Name;

    protected override string TelemetryOperation(RemovalFlowDefinition flow) => flow.TelemetryOperation;

    protected override string OperationOf(RunParameters parameters) => RemovalFlowKind.Operation(parameters);

    protected override async Task<object> ExecuteOperationAsync(
        EngineContext context, RemovalFlowDefinition flow, string operation, RunParameters parameters, Guid runId, string actor, ILogger log,
        CancellationToken ct)
    {
        var sources = Services.GetRequiredService<RemovalKeySources>();
        var payload = DeliveryRunPayload.Parse(parameters);
        var (partition, supplied) = PartitionNames.SplitRunValues(parameters.Values, keptAsParameter: false);
        var values = FlowParameters.Resolve(flow.Parameters, flow.SourcePath ?? flow.Name, supplied);
        var registry = flow.NeedsRegistry(partition)
            ? await context.PartitionRegistry.ReadAsync(ct).ConfigureAwait(false)
            : RegisteredPartitions.None;
        var bound = flow.ForRun(partition, registry);

        // The delivery flows' platform and credentials, and the key sources' connections, resolve from the central configuration
        // the control plane supplied, the partition's own values first, before the node's own.
        var runContext = context.WithSuppliedReferences(payload.ReferencesFor(bound.Partition));
        var runner = new RemovalRunner(runContext, sources, bound, values, runId, actor, log);
        return await runner.RunAsync(operation, payload.Entries, payload.Approve, payload.Confirm, ct).ConfigureAwait(false);
    }
}
