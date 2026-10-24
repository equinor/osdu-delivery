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
/// parameters select the operation: build (the default), reconcile, plan, remove, match or duplicates; the payload names the
/// inventories, none naming every one (a removal names one, with what it removes and the partition it confirms; a match names
/// one, with the selection it fills; a duplicates run names one, with the check it fills); the partition run
/// value names the partition, settled as every run of the module settles it. The engine's log becomes the run log and
/// live trace, the outcome becomes <c>run.json</c>, and every build and reconcile is kept with its inventory. A run in
/// which an inventory failed ends failed, still carrying its outcome, with every other inventory done.
/// </summary>
public sealed class InventoryExecutor : IFlowDocumentExecutor
{
    private readonly IServiceProvider _provider;

    public InventoryExecutor(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public bool CanExecute(RegisteredFlowDocument document) => document is InventoryFlowDocument;

    public async Task<DocumentExecutionResult> ExecuteAsync(RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        if (document is not InventoryFlowDocument inventories)
        {
            throw new SqlFlowException($"The inventory executor cannot run a '{document.Kind}' document.");
        }

        var flow = inventories.Flow with { SourcePath = Path.GetFullPath(flowFile) };
        var parameters = options.Parameters;
        parameters.Validate();
        var operation = InventoryFlowKind.Operation(parameters);
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
        log.LogInformation("inventory flow '{Flow}': {Operation} (parameters: {Parameters}) requested by {Actor}, run {RunId}", flow.Name, operation, parameters.Describe(), actor, runId);
        try
        {
            result = await ExecuteOperationAsync(context, flow, operation, parameters, runId, actor, log, ct).ConfigureAwait(false);
            success = true;
            log.LogInformation("{Operation} completed in {Seconds:0.###}s", operation, stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (InventoryRunsFailedException failed)
        {
            // Every inventory was taken up and at least one failed: the run ends failed, and its outcome still says how each
            // inventory came out.
            error = failed.Message;
            log.LogError("{Operation} failed: {Error}", operation, error);
            result = failed.Outcome;
        }
        catch (InventoryRemovalFailedException stopped)
        {
            // The removal stopped part way: the run ends failed, and its outcome says what it removed up to there.
            error = stopped.Message;
            log.LogError("{Operation} failed: {Error}", operation, error);
            result = stopped.Outcome;
        }
        catch (InventoryMatchFailedException stopped)
        {
            // The match stopped: the run ends failed, its outcome saying why, and its selection holds no id.
            error = stopped.Message;
            log.LogError("{Operation} failed: {Error}", operation, error);
            result = stopped.Outcome;
        }
        catch (InventoryDuplicatesFailedException stopped)
        {
            // The check stopped: the run ends failed, its outcome saying why, and the check holds no group.
            error = stopped.Message;
            log.LogError("{Operation} failed: {Error}", operation, error);
            result = stopped.Outcome;
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
        var artifact = RunArtifacts.Artifact(InventoryFlowDefinition.FlowTypeName, flow.Name, runId, success, error, result, events.Records);
        var directory = RunArtifacts.Write(flowFile, flow.Name, runId, artifact, runLogger.Render(), null, options.Echo);
        return new DocumentExecutionResult
        {
            FlowName = flow.Name,
            FlowKind = InventoryFlowDefinition.FlowTypeName,
            Success = success,
            Error = error,
            RunId = runId,
            RunDirectory = directory,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Result = result,
        };
    }

    private static async Task<object> ExecuteOperationAsync(
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
