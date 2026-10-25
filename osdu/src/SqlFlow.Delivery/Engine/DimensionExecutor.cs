using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
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
/// still carrying its outcome, with every other dimension built.
/// </summary>
public sealed class DimensionExecutor : IFlowDocumentExecutor
{
    private readonly IServiceProvider _provider;

    public DimensionExecutor(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public bool CanExecute(RegisteredFlowDocument document) => document is DimensionFlowDocument;

    public async Task<DocumentExecutionResult> ExecuteAsync(RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        if (document is not DimensionFlowDocument dimensions)
        {
            throw new SqlFlowException($"The dimension executor cannot run a '{document.Kind}' document.");
        }

        var flow = dimensions.Flow with { SourcePath = Path.GetFullPath(flowFile) };
        var parameters = options.Parameters;
        parameters.Validate();
        var operation = DimensionFlowKind.Operation(parameters);
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
        log.LogInformation("dimension flow '{Flow}': {Operation} (parameters: {Parameters}) requested by {Actor}, run {RunId}", flow.Name, operation, parameters.Describe(), actor, runId);
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
        catch (DimensionBuildsFailedException failed)
        {
            // Every dimension was taken up and at least one failed: the run ends failed, and its outcome still says how each
            // dimension came out.
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
        var artifact = RunArtifacts.Artifact(DimensionFlowDefinition.FlowTypeName, flow.Name, runId, success, error, result, events.Records);
        var directory = RunArtifacts.Write(flowFile, flow.Name, runId, artifact, runLogger.Render(), null, options.Echo);
        return new DocumentExecutionResult
        {
            FlowName = flow.Name,
            FlowKind = DimensionFlowDefinition.FlowTypeName,
            Success = success,
            Error = error,
            RunId = runId,
            RunDirectory = directory,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Result = result,
        };
    }

    private static async Task<object> ExecuteOperationAsync(
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
            : await runner.BuildAsync(payload.Dimensions, load, runId, actor, ct).ConfigureAwait(false);
    }

    /// <summary>A bound of a backfill window as UTC: SQLFlow carries the window in UTC, and one with no kind is taken as such.</summary>
    private static DateTime? Utc(DateTime? value)
        => value is { } v ? v.Kind == DateTimeKind.Utc ? v : v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}
