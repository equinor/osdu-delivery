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
public sealed class AssertionExecutor : IFlowDocumentExecutor
{
    private readonly IServiceProvider _provider;

    public AssertionExecutor(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public bool CanExecute(RegisteredFlowDocument document) => document is AssertionFlowDocument;

    public async Task<DocumentExecutionResult> ExecuteAsync(RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        if (document is not AssertionFlowDocument assertion)
        {
            throw new SqlFlowException($"The assertion executor cannot run a '{document.Kind}' document.");
        }

        var flow = assertion.Flow with { SourcePath = Path.GetFullPath(flowFile) };
        var parameters = options.Parameters;
        parameters.Validate();
        var operation = AssertionFlowKind.Operation(parameters);
        var runId = options.RunId ?? Guid.CreateVersion7();
        var actor = string.IsNullOrWhiteSpace(options.Actor) ? "unknown" : options.Actor.Trim();
        var (runLogger, events, _) = RunArtifacts.BuildEventPlumbing(options, flow.Name);
        var loggers = new RunLogLoggerFactory(runLogger, events, runId, flow.Name);
        var log = loggers.CreateLogger("run");
        var context = _provider.GetRequiredService<EngineContext>().ForRun(loggers);

        var stopwatch = Stopwatch.StartNew();
        object result;
        string? error = null;
        var success = false;
        log.LogInformation("assertion flow '{Flow}': {Operation} (parameters: {Parameters}) requested by {Actor}, run {RunId}", flow.Name, operation, parameters.Describe(), actor, runId);
        try
        {
            // Before anything else: every OSDU request the run sends carries the run's correlation id.
            using var correlation = RunCorrelation.Begin(context.Documents, flowFile, flow.TelemetryOperation, runId, log);
            result = await ExecuteOperationAsync(context, flow, operation, parameters, runId, actor, log, ct).ConfigureAwait(false);
            success = true;
            log.LogInformation("{Operation} completed in {Seconds:0.###}s", operation, stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AssertionTestsFailedException failed)
        {
            // The tests ran and came out as the flow says fails the run: the run ends failed, and its outcome still says how
            // every test came out.
            error = failed.Message;
            log.LogError("{Operation} failed: {Error}", operation, error);
            result = failed.Outcome;
        }
        catch (Exception ex)
        {
            // The run boundary: every failure ends the run as a recorded one. An unexpected kind is a defect, so its stack
            // goes to the run log as well.
            error = RunFailure.Describe(ex);
            log.LogError(RunFailure.IsExpected(ex) ? null : ex, "{Operation} failed: {Error}", operation, error);
            result = new OperationFailure(operation, error);
        }

        stopwatch.Stop();
        var artifact = RunArtifacts.Artifact(AssertionFlowDefinition.FlowTypeName, flow.Name, runId, success, error, result, events.Records);
        var directory = RunArtifacts.Write(flowFile, flow.Name, runId, artifact, runLogger.Render(), null, options.Echo);
        return new DocumentExecutionResult
        {
            FlowName = flow.Name,
            FlowKind = AssertionFlowDefinition.FlowTypeName,
            Success = success,
            Error = error,
            RunId = runId,
            RunDirectory = directory,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Result = result,
        };
    }

    private static async Task<object> ExecuteOperationAsync(
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
