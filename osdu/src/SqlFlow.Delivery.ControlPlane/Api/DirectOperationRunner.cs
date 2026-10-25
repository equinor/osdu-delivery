using System.Data.Common;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Http;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// Runs an operation a person asks of a flow and waits on (<see cref="DirectOperations"/>) in this process, and answers with
/// what it found: a read of OSDU, a probe, a read of the ingestion tables, a preview. The operation is given exactly what a
/// node would be given (<see cref="DeliveryEndpoints.OperationPayloadAsync"/>: the flow file, the interface, the partition,
/// who asked) and the central configuration of the flow's partition as a queued task carries it
/// (<see cref="ConfiguredRunDispatcher.WithReferences"/>), so it reaches the same target with the same credentials. The
/// explorer's reads of a partition are run the same way through the partition's own connection, with no flow
/// (<see cref="RunOnPartitionAsync"/>). Nothing is queued and nothing is polled; the request is the wait.
/// </summary>
/// <remarks>
/// A failure answers as a problem whose words say which side failed, every resolved secret redacted from them: the platform
/// or the database refusing or failing (502), not answering in time (504), or the request naming something the flow cannot
/// serve (422). A request the caller abandons stops the operation. The reads change nothing and leave only this runner's
/// log line; the probe, an operator action, is run through here by <see cref="TargetProbes"/>, which records it in the audit
/// trail and counts it.
/// </remarks>
internal static partial class DirectOperationRunner
{
    /// <summary>The logger category every direct operation reports under.</summary>
    public const string LogCategory = "SqlFlow.Delivery.ControlPlane.DirectOperations";

    public static async Task<Results<ContentHttpResult, ProblemHttpResult>> RunAsync(
        CatalogDbContext db, DeliveryConfigStore config, DirectOperations operations, DeliveryEndpoints.FlowContext flow, string operation,
        IReadOnlyDictionary<string, string> arguments, ClaimsPrincipal user, ILoggerFactory loggers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        return await RunAsync(db, config, operations, flow, operation, arguments, RequestActor.Label(user), loggers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The same, for a caller that names the actor itself rather than having it read from a principal: the target probe
    /// (<see cref="TargetProbes"/>), which runs for a person and for the schedule alike and records itself in the audit trail.
    /// </summary>
    public static async Task<Results<ContentHttpResult, ProblemHttpResult>> RunAsync(
        CatalogDbContext db, DeliveryConfigStore config, DirectOperations operations, DeliveryEndpoints.FlowContext flow, string operation,
        IReadOnlyDictionary<string, string> arguments, string actor, ILoggerFactory loggers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(loggers);
        var (configured, problem) = await PrepareAsync(db, config, flow, operation, arguments, actor, ct).ConfigureAwait(false);
        return configured is null ? problem! : await ExecuteAsync(operations, configured, flow.Flow.Label, actor, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The same operation, for a caller that reads its answer itself (a run form's lookup of a parameter's values): the JSON it
    /// answers with, logged as <see cref="RunAsync(CatalogDbContext, DeliveryConfigStore, DirectOperations, DeliveryEndpoints.FlowContext, string, IReadOnlyDictionary{string, string}, string, ILoggerFactory, CancellationToken)"/>
    /// logs it. Its failure is thrown as it was raised, after the redacted reason is logged; a flow that cannot be given the
    /// operation (its repository not materialized) is a <see cref="SqlFlowException"/> saying why.
    /// </summary>
    public static async Task<string> AnswerAsync(
        CatalogDbContext db, DeliveryConfigStore config, DirectOperations operations, DeliveryEndpoints.FlowContext flow, string operation,
        IReadOnlyDictionary<string, string> arguments, string actor, ILoggerFactory loggers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(loggers);
        var (configured, problem) = await PrepareAsync(db, config, flow, operation, arguments, actor, ct).ConfigureAwait(false);
        if (configured is null)
        {
            throw new SqlFlowException(problem!.ProblemDetails.Detail ?? $"Flow '{flow.Flow.Label}' cannot run {operation} here.");
        }

        var log = loggers.CreateLogger(LogCategory);
        try
        {
            var answer = await operations.Get(operation).ExecuteAsync(configured, ct).ConfigureAwait(false);
            LogRan(log, operation, flow.Flow.Label, actor);
            return answer;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Classify(ex) is { } failure)
        {
            LogFailed(log, operation, flow.Flow.Label, actor, failure.Status, DeliveryEndpoints.Redacted(ex));
            throw;
        }
    }

    /// <summary>
    /// The payload a node would be given for <paramref name="operation"/> on <paramref name="flow"/>, with the central
    /// configuration of the flow's partition laid on it, or the problem that keeps the flow from being given it.
    /// </summary>
    private static async Task<(ComputeTaskPayload? Payload, ProblemHttpResult? Problem)> PrepareAsync(
        CatalogDbContext db, DeliveryConfigStore config, DeliveryEndpoints.FlowContext flow, string operation, IReadOnlyDictionary<string, string> arguments,
        string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(flow);
        var (payload, problem) = await DeliveryEndpoints.OperationPayloadAsync(db, flow, operation, arguments, actor, ct).ConfigureAwait(false);
        if (payload is null)
        {
            return (null, problem);
        }

        var configuration = await config.ConfigurationAsync(flow.Pipeline.RepoId, ct).ConfigureAwait(false);
        return (ConfiguredRunDispatcher.WithReferences(payload, configuration.For(flow.Flow.Partition)), null);
    }

    /// <summary>
    /// Runs one of the explorer's reads through <paramref name="partition"/>'s own connection
    /// (<see cref="PartitionConnection"/>), with no flow: the operation is given the partition and the control plane's
    /// configuration for it (<paramref name="configured"/>, its values for the partition over those for none), which its
    /// references resolve with before this process's environment.
    /// </summary>
    public static async Task<Results<ContentHttpResult, ProblemHttpResult>> RunOnPartitionAsync(
        DirectOperations operations, string partition, IReadOnlyDictionary<string, string> configured, IReadOnlyDictionary<string, string> arguments,
        ClaimsPrincipal user, ILoggerFactory loggers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(loggers);
        var actor = RequestActor.Label(user);
        var payload = new ComputeTaskPayload
        {
            Operation = ExploreOperation.OperationName,
            SourceRef = partition,
            Arguments = new Dictionary<string, string>(arguments, StringComparer.Ordinal)
            {
                [ExploreOperation.ConnectionArgument] = ExploreOperation.PartitionConnectionValue,
                [DeliveryOperation.PartitionArgument] = partition,
                ["actor"] = actor,
            },
        };

        // Checked as every payload of the module's own operations is, before the configuration is laid on it.
        payload.Validate([ExploreOperation.OperationName]);
        var withReferences = ConfiguredRunDispatcher.WithReferences(payload, configured);
        return await ExecuteAsync(operations, withReferences, PartitionConnection.LabelOf(partition), actor, loggers, ct).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="payload"/>'s operation and answers with what it found, or with the failure in words a reader acts on.</summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> ExecuteAsync(
        DirectOperations operations, ComputeTaskPayload payload, string through, string actor, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger(LogCategory);
        var operation = payload.Operation;
        try
        {
            var answer = await operations.Get(operation).ExecuteAsync(payload, ct).ConfigureAwait(false);
            LogRan(log, operation, through, actor);
            return TypedResults.Text(answer, "application/json", Encoding.UTF8, StatusCodes.Status200OK);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (Classify(ex) is { } failure)
        {
            var said = DeliveryEndpoints.Redacted(ex);
            LogFailed(log, operation, through, actor, failure.Status, said);
            return TypedResults.Problem(detail: said, statusCode: failure.Status, title: failure.Title);
        }
    }

    /// <summary>
    /// What a failure of an operation says about where it failed, as the status and title it answers with; null for one this
    /// runner does not know, which the host answers as any unexpected failure.
    /// </summary>
    private static (int Status, string Title)? Classify(Exception ex) => ex switch
    {
        OsduStatusException => (StatusCodes.Status502BadGateway, "OSDU refused the request"),
        TimeoutException or TaskCanceledException => (StatusCodes.Status504GatewayTimeout, "No answer in time"),
        HttpRequestException => (StatusCodes.Status502BadGateway, "OSDU could not be reached"),
        DbException => (StatusCodes.Status502BadGateway, "The ingestion tables could not be read"),
        DeliveryException { InnerException: HttpRequestException or IOException } => (StatusCodes.Status502BadGateway, "OSDU could not be reached"),
        DeliveryException { InnerException: TaskCanceledException or TimeoutException } => (StatusCodes.Status504GatewayTimeout, "No answer in time"),
        SqlFlowException => (StatusCodes.Status422UnprocessableEntity, "The request could not be completed"),
        _ => null,
    };

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{Actor} ran {Operation} on {Target} in process.")]
    private static partial void LogRan(ILogger logger, string operation, string target, string actor);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "{Actor}'s {Operation} on {Target} answered {Status}: {Reason}")]
    private static partial void LogFailed(ILogger logger, string operation, string target, string actor, int status, string reason);
}
