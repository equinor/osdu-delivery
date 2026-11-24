using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// A failure that still carries what its run did up to it: the run ends failed and records this as its outcome, rather than
/// the failure alone (a download flow whose downloads did not all complete, an inventory's removal that stopped part way).
/// </summary>
public interface IFailureWithOutcome
{
    /// <summary>What the run did, recorded as its outcome.</summary>
    object Outcome { get; }
}

/// <summary>
/// The run boundary every flow kind but delivery shares (assertion, cache, dimension, download, inventory, removal and
/// retrieval flows): one platform run of one document, its log becoming the run log and live trace, every OSDU request it
/// sends carrying the run's correlation id, and every way it ends recorded as <c>run.json</c> in the run's directory. A
/// failure that carries an outcome (<see cref="IFailureWithOutcome"/>) ends the run failed with that outcome; any other ends
/// it failed with the failure, and an unexpected kind of failure, a defect, puts its stack in the run log too. A kind says
/// only which document it runs, which operation the run asks for, and how it carries that operation out.
/// </summary>
/// <typeparam name="TDocument">The kind's registered document.</typeparam>
/// <typeparam name="TFlow">The flow the document holds.</typeparam>
public abstract class KindExecutor<TDocument, TFlow> : IFlowDocumentExecutor
    where TDocument : RegisteredFlowDocument
{
    protected KindExecutor(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    /// <summary>The host's services, for a kind that resolves what its operation needs when it runs.</summary>
    protected IServiceProvider Services { get; }

    /// <summary>The kind's <c>flowType</c>, as the run's artifact and result name it and its log line opens with.</summary>
    protected abstract string FlowType { get; }

    /// <summary>The flow <paramref name="document"/> holds, read from <paramref name="sourcePath"/>.</summary>
    protected abstract TFlow FlowOf(TDocument document, string sourcePath);

    /// <summary>The flow's name.</summary>
    protected abstract string FlowName(TFlow flow);

    /// <summary>The operation code the flow's runs are correlated under (<c>telemetry.operation</c>).</summary>
    protected abstract string TelemetryOperation(TFlow flow);

    /// <summary>The operation the run's parameters ask for.</summary>
    protected abstract string OperationOf(RunParameters parameters);

    /// <summary>The engine a run works with, given the one made for the run's log; as it is unless the kind adds to it.</summary>
    protected virtual EngineContext ContextFor(EngineContext context, DocumentExecutionOptions options) => context;

    /// <summary>Carries out <paramref name="operation"/> and returns its outcome, or throws.</summary>
    protected abstract Task<object> ExecuteOperationAsync(
        EngineContext context, TFlow flow, string operation, RunParameters parameters, Guid runId, string actor, ILogger log, CancellationToken ct);

    public bool CanExecute(RegisteredFlowDocument document) => document is TDocument;

    public async Task<DocumentExecutionResult> ExecuteAsync(RegisteredFlowDocument document, string flowFile, DocumentExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(options);
        if (document is not TDocument kindDocument)
        {
            throw new SqlFlowException($"The {FlowType} executor cannot run a '{document.Kind}' document.");
        }

        var flow = FlowOf(kindDocument, Path.GetFullPath(flowFile));
        var name = FlowName(flow);
        var parameters = options.Parameters;
        parameters.Validate();
        var operation = OperationOf(parameters);
        var runId = options.RunId ?? Guid.CreateVersion7();
        var actor = string.IsNullOrWhiteSpace(options.Actor) ? "unknown" : options.Actor.Trim();
        var (runLogger, events, _) = RunArtifacts.BuildEventPlumbing(options, name);
        var loggers = new RunLogLoggerFactory(runLogger, events, runId, name);
        var log = loggers.CreateLogger("run");
        var context = ContextFor(Services.GetRequiredService<EngineContext>().ForRun(loggers), options);

        var stopwatch = Stopwatch.StartNew();
        object result;
        string? error = null;
        var success = false;
        log.LogInformation("{Kind} flow '{Flow}': {Operation} (parameters: {Parameters}) requested by {Actor}, run {RunId}", FlowType, name, operation, parameters.Describe(), actor, runId);
        try
        {
            // Before anything else: every OSDU request the run sends carries the run's correlation id.
            using var correlation = RunCorrelation.Begin(context.Documents, flowFile, TelemetryOperation(flow), runId, log);
            result = await ExecuteOperationAsync(context, flow, operation, parameters, runId, actor, log, ct).ConfigureAwait(false);
            success = true;
            log.LogInformation("{Operation} completed in {Seconds:0.###}s", operation, stopwatch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IFailureWithOutcome)
        {
            // The run did its work and some of it failed or stopped part way: it ends failed, and its outcome still says what
            // it did.
            error = RunFailure.Describe(ex);
            log.LogError("{Operation} failed: {Error}", operation, error);
            result = ((IFailureWithOutcome)ex).Outcome;
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
        var artifact = RunArtifacts.Artifact(FlowType, name, runId, success, error, result, events.Records);
        var directory = RunArtifacts.Write(flowFile, name, runId, artifact, runLogger.Render(), null, options.Echo);
        return new DocumentExecutionResult
        {
            FlowName = name,
            FlowKind = FlowType,
            Success = success,
            Error = error,
            RunId = runId,
            RunDirectory = directory,
            DurationSeconds = stopwatch.Elapsed.TotalSeconds,
            Result = result,
        };
    }
}
