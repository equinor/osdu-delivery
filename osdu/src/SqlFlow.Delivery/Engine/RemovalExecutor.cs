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
public sealed class RemovalExecutor : IFlowDocumentExecutor
{
    private readonly IServiceProvider _provider;

    public RemovalExecutor(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public bool CanExecute(RegisteredFlowDocument document) => document is RemovalFlowDocument;

    public async Task<DocumentExecutionResult> ExecuteAsync(RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        if (document is not RemovalFlowDocument removals)
        {
            throw new SqlFlowException($"The removal executor cannot run a '{document.Kind}' document.");
        }

        var flow = removals.Flow with { SourcePath = Path.GetFullPath(flowFile) };
        var parameters = options.Parameters;
        parameters.Validate();
        var operation = RemovalFlowKind.Operation(parameters);
        var runId = options.RunId ?? Guid.CreateVersion7();
        var actor = string.IsNullOrWhiteSpace(options.Actor) ? "unknown" : options.Actor.Trim();
        var (runLogger, events, _) = RunArtifacts.BuildEventPlumbing(options, flow.Name);
        var loggers = new RunLogLoggerFactory(runLogger, events, runId, flow.Name);
        var log = loggers.CreateLogger("run");
        var context = _provider.GetRequiredService<EngineContext>().ForRun(loggers);
        var sources = _provider.GetRequiredService<RemovalKeySources>();

        var stopwatch = Stopwatch.StartNew();
        object result;
        string? error = null;
        var success = false;
        log.LogInformation("removal flow '{Flow}': {Operation} (parameters: {Parameters}) requested by {Actor}, run {RunId}", flow.Name, operation, parameters.Describe(), actor, runId);
        try
        {
            result = await ExecuteOperationAsync(context, sources, flow, operation, parameters, runId, actor, log, ct).ConfigureAwait(false);
            success = true;
            log.LogInformation("{Operation} completed in {Seconds:0.###}s", operation, stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RemovalRunFailedException failed)
        {
            // Every removal was taken up, and one failed or held what it found for approval: the run ends failed, so a
            // run-failed notification goes out, and its outcome still says how each removal came out.
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
        var artifact = RunArtifacts.Artifact(RemovalFlowDefinition.FlowTypeName, flow.Name, runId, success, error, result, events.Records);
        var directory = RunArtifacts.Write(flowFile, flow.Name, runId, artifact, runLogger.Render(), null, options.Echo);
        return new DocumentExecutionResult
        {
            FlowName = flow.Name,
            FlowKind = RemovalFlowDefinition.FlowTypeName,
            Success = success,
            Error = error,
            RunId = runId,
            RunDirectory = directory,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Result = result,
        };
    }

    private static async Task<object> ExecuteOperationAsync(
        EngineContext context, RemovalKeySources sources, RemovalFlowDefinition flow, string operation, RunParameters parameters, Guid runId, string actor, ILogger log,
        CancellationToken ct)
    {
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
