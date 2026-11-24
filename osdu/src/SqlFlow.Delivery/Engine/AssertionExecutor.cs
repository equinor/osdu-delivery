using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Model;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Runs one assertion flow document as one platform run (osdu/docs/reference/flow/assertion.md, Operations and the run
/// payload). The run's parameters select the operation: test (the default) or plan; the payload names the tests (by
/// name or tag), none naming every test; the partition run value names the partition, settled as every run of the
/// module settles it. The engine's log becomes the run log and live trace, the outcome becomes <c>run.json</c>, and the
/// report is kept in the flow's ledger. A test run whose tests come out as the flow's <c>failRunOn</c> says ends
/// failed, still carrying its outcome.
/// </summary>
public sealed class AssertionExecutor : KindExecutor<AssertionFlowDocument, AssertionFlowDefinition>
{
    public AssertionExecutor(IServiceProvider provider)
        : base(provider)
    {
    }

    protected override string FlowType => AssertionFlowDefinition.FlowTypeName;

    protected override AssertionFlowDefinition FlowOf(AssertionFlowDocument document, string sourcePath) => document.Flow with { SourcePath = sourcePath };

    protected override string FlowName(AssertionFlowDefinition flow) => flow.Name;

    protected override string TelemetryOperation(AssertionFlowDefinition flow) => flow.TelemetryOperation;

    protected override string OperationOf(RunParameters parameters) => AssertionFlowKind.Operation(parameters);

    protected override async Task<object> ExecuteOperationAsync(
        EngineContext context, AssertionFlowDefinition flow, string operation, RunParameters parameters, Guid runId, string actor, ILogger log, CancellationToken ct)
    {
        var payload = DeliveryRunPayload.Parse(parameters);
        var (partition, supplied) = PartitionNames.SplitRunValues(parameters.Values, keptAsParameter: false);
        var values = FlowParameters.Resolve(flow.Parameters, flow.SourcePath ?? flow.Name, supplied);

        // A hard-coded partition settles itself; a flow that leaves its partitions to the registry tests the one named, which
        // the registry has to hold, or else the registry's default.
        var registry = flow.NeedsRegistry(partition)
            ? await context.PartitionRegistry.ReadAsync(ct).ConfigureAwait(false)
            : RegisteredPartitions.None;
        var bound = flow.ForRun(partition, registry);

        // The platform and credentials a flow names by reference resolve from the central configuration the control plane
        // supplied, the partition's own values first, before the node's own.
        var runContext = context.WithSuppliedReferences(payload.ReferencesFor(bound.Partition));
        var runner = new AssertionRunner(runContext, bound, values, log);
        if (operation == DeliveryOperations.Plan)
        {
            return await runner.PlanAsync(payload.Tests, payload.Tags, ct).ConfigureAwait(false);
        }

        var outcome = await runner.TestAsync(payload.Tests, payload.Tags, runId, actor, ct).ConfigureAwait(false);
        return FailsRun(flow, outcome) ? throw new AssertionTestsFailedException(outcome) : outcome;
    }

    /// <summary>
    /// Whether a test run's outcome fails the platform run, as the flow's <c>failRunOn</c> says: a failed or errored test
    /// (error, the default), a warned one as well (warning), or never.
    /// </summary>
    public static bool FailsRun(AssertionFlowDefinition flow, AssertionRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(outcome);
        return flow.FailRunOn switch
        {
            FailRunOn.Error => outcome.Failed > 0 || outcome.Errored > 0,
            FailRunOn.Warning => outcome.Failed > 0 || outcome.Errored > 0 || outcome.Warned > 0,
            _ => false,
        };
    }
}
