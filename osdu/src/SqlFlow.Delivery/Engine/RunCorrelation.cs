using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// The correlation id every OSDU request of a run carries (osdu/docs/reference/concepts/request-correlation.md):
/// <c>osdu-delivery</c>, the code of the repository the flow's file is in (its repository document,
/// osdu/docs/reference/flow/repository.md), the flow's operation code and the run's id. Every kind's executor opens it before
/// the run does anything else, and the run log says what it is, so a run's requests can be found in the platform's logs.
/// </summary>
internal static partial class RunCorrelation
{
    /// <summary>Opens the run's correlation id; disposing it ends the run's.</summary>
    /// <param name="documents">The loader the repository document is read with.</param>
    /// <param name="flowFile">The flow's file, which the repository document is looked for above.</param>
    /// <param name="operation">The flow's operation code (<c>telemetry.operation</c>, or its kind's).</param>
    /// <param name="runId">The run's id.</param>
    /// <param name="log">The run log.</param>
    /// <exception cref="SqlFlow.Core.FlowValidationException">The repository document is there and cannot be read.</exception>
    public static OsduCorrelationScope Begin(DeliveryDocumentLoader documents, string flowFile, string operation, Guid runId, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        ArgumentNullException.ThrowIfNull(log);
        var repository = RepositoryDocuments.Find(flowFile, documents);
        var scope = OsduCorrelation.ForRun(repository?.Repository.Code, operation, runId);
        if (repository is null)
        {
            LogWithoutRepository(log, OsduCorrelation.HeaderName, scope.Id, RepositoryDefinition.FileName);
        }
        else
        {
            LogCarried(log, OsduCorrelation.HeaderName, scope.Id, repository.FullPath);
        }

        return scope;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "OSDU requests of this run carry {Header} {Value}, the repository's code from {Document}")]
    private static partial void LogCarried(ILogger log, string header, string value, string document);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "OSDU requests of this run carry {Header} {Value}; no {FileName} in the flow's folder or above it names the repository's code")]
    private static partial void LogWithoutRepository(ILogger log, string header, string value, string fileName);
}
