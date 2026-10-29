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
public sealed class DownloadExecutor : IFlowDocumentExecutor
{
    private readonly IServiceProvider _provider;

    public DownloadExecutor(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public bool CanExecute(RegisteredFlowDocument document) => document is DownloadFlowDocument;

    public async Task<DocumentExecutionResult> ExecuteAsync(RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        if (document is not DownloadFlowDocument downloads)
        {
            throw new SqlFlowException($"The download executor cannot run a '{document.Kind}' document.");
        }

        var flow = downloads.Flow with { SourcePath = Path.GetFullPath(flowFile) };
        var parameters = options.Parameters;
        parameters.Validate();
        var operation = DownloadFlowKind.Operation(parameters);
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
        log.LogInformation("download flow '{Flow}': {Operation} (parameters: {Parameters}) requested by {Actor}, run {RunId}", flow.Name, operation, parameters.Describe(), actor, runId);
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
        catch (DownloadRunsFailedException failed)
        {
            // Every download was taken up and at least one failed: the run ends failed, and its outcome still says how each
            // download came out.
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
        var artifact = RunArtifacts.Artifact(DownloadFlowDefinition.FlowTypeName, flow.Name, runId, success, error, result, events.Records);
        var directory = RunArtifacts.Write(flowFile, flow.Name, runId, artifact, runLogger.Render(), null, options.Echo);
        return new DocumentExecutionResult
        {
            FlowName = flow.Name,
            FlowKind = DownloadFlowDefinition.FlowTypeName,
            Success = success,
            Error = error,
            RunId = runId,
            RunDirectory = directory,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Result = result,
        };
    }

    private static async Task<object> ExecuteOperationAsync(
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
