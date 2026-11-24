using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Inventories;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one inventory flow document as one platform run (osdu/docs/reference/flow/inventory.md, Operations). The run's
/// parameters select the operation: build (the default), reconcile, plan, remove, restore, match or duplicates; the payload names
/// the inventories, none naming every one (a removal names one, with what it removes and the partition it confirms; a restore
/// names one, with what it brings back and the partition it confirms; a match names one, with the selection it fills; a
/// duplicates run names one, with the check it fills); the partition run
/// value names the partition, settled as every run of the module settles it. The engine's log becomes the run log and
/// live trace, the outcome becomes <c>run.json</c>, and every build and reconcile is kept with its inventory. A run in
/// which an inventory failed ends failed, still carrying its outcome, with every other inventory done.
/// </summary>
public sealed class InventoryExecutor : KindExecutor<InventoryFlowDocument, InventoryFlowDefinition>
{
    public InventoryExecutor(IServiceProvider provider)
        : base(provider)
    {
    }

    protected override string FlowType => InventoryFlowDefinition.FlowTypeName;

    protected override InventoryFlowDefinition FlowOf(InventoryFlowDocument document, string sourcePath) => document.Flow with { SourcePath = sourcePath };

    protected override string FlowName(InventoryFlowDefinition flow) => flow.Name;

    protected override string TelemetryOperation(InventoryFlowDefinition flow) => flow.TelemetryOperation;

    protected override string OperationOf(RunParameters parameters) => InventoryFlowKind.Operation(parameters);

    protected override async Task<object> ExecuteOperationAsync(
        EngineContext context, InventoryFlowDefinition flow, string operation, RunParameters parameters, Guid runId, string actor, ILogger log, CancellationToken ct)
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
        var runner = new InventoryRunner(runContext, bound, values, log);
        return operation switch
        {
            DeliveryOperations.Plan => await runner.PlanAsync(payload.Inventories, ct).ConfigureAwait(false),
            DeliveryOperations.Remove => await runner.RemoveAsync(
                payload.Inventories.Count == 1 ? payload.Inventories[0] : throw new DeliveryException("A remove run names the one inventory it removes from."),
                payload.Removal ?? throw new DeliveryException("A remove run names what it removes (removal)."),
                payload.Confirm ?? throw new DeliveryException("A remove run names the partition it acts in (confirm)."),
                runId, actor, ct).ConfigureAwait(false),
            DeliveryOperations.Restore => await runner.RestoreAsync(
                payload.Inventories.Count == 1 ? payload.Inventories[0] : throw new DeliveryException("A restore run names the one inventory it restores in."),
                payload.Restore ?? throw new DeliveryException("A restore run names what it brings back (restore)."),
                payload.Confirm ?? throw new DeliveryException("A restore run names the partition it acts in (confirm)."),
                runId, actor, ct).ConfigureAwait(false),
            DeliveryOperations.Match => await runner.MatchAsync(
                payload.Inventories.Count == 1 ? payload.Inventories[0] : throw new DeliveryException("A match run names the one inventory it matches."),
                payload.SelectionId ?? throw new DeliveryException("A match run names the selection it fills (selection)."),
                runId, actor, ct).ConfigureAwait(false),
            DeliveryOperations.Duplicates => await runner.FindDuplicatesAsync(
                payload.Inventories.Count == 1 ? payload.Inventories[0] : throw new DeliveryException("A duplicates run names the one inventory it checks."),
                payload.DuplicateCheckId ?? throw new DeliveryException("A duplicates run names the check it fills (duplicateCheck)."),
                runId, actor, ct).ConfigureAwait(false),
            DeliveryOperations.Reconcile => await runner.ReconcileAsync(payload.Inventories, runId, actor, ct).ConfigureAwait(false),
            _ => await runner.BuildAsync(payload.Inventories, runId, actor, ct).ConfigureAwait(false),
        };
    }
}
