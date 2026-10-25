using System.Text.Json;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Api;

/// <summary>
/// The values a delivery flow's parameter may take, for a run form (<see cref="IRunParameterLookup"/>,
/// osdu/docs/reference/flow/parameters.md, Lookups): the distinct values the column it is looked up in holds in the record
/// table of the interface it is looked up through (<see cref="KindRunInputs.LookupInterface"/>), the most used first, each
/// with how many rows hold it. They are read in this process by the operation a flow's Preview tab lists a scope's values
/// with (<see cref="ScopeValuesOperation"/>), through the flow's own connection reference in the partition the form names,
/// so the run form, the Preview tab and the value check offer the same values.
/// </summary>
internal sealed class DeliveryRunParameterLookup(
    CatalogDbContext db, DeliveryDocumentLoader documents, IPartitionRegistry partitions, DeliveryConfigStore config, DirectOperations direct, ILoggerFactory loggers)
    : IRunParameterLookup
{
    /// <summary>How the operation's answer reads: camel-cased, as every delivery operation writes it.</summary>
    private static readonly JsonSerializerOptions AnswerOptions = new(JsonSerializerDefaults.Web);

    public bool Serves(string flowKind) => string.Equals(flowKind, FlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase);

    public async Task<RunParameterValues> LookUpAsync(RunParameterLookupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = documents.ParseSource(request.Yaml, request.RelativePath);
        var through = KindRunInputs.LookupInterface(source, request.Parameter)
            ?? throw new SqlFlowException($"Flow '{request.FlowName}' looks up no values for parameter '{request.Parameter}'.");

        request.Values.TryGetValue(PartitionNames.RunValue, out var partition);
        var (flow, problem) = await DeliveryEndpoints.ResolveAsync(db, documents, partitions, request.PipelineId, through.Interface, partition, ct).ConfigureAwait(false);
        if (flow is null)
        {
            throw new SqlFlowException(problem!.ProblemDetails.Detail ?? $"Flow '{request.FlowName}' cannot be read in partition '{partition}'.");
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal) { [ScopeValuesOperation.ParameterArgument] = request.Parameter };
        if (request.Search is { } search)
        {
            arguments[ScopeValuesOperation.SearchArgument] = search;
        }

        var json = await DirectOperationRunner.AnswerAsync(db, config, direct, flow, ScopeValuesOperation.OperationName, arguments, request.Actor, loggers, ct).ConfigureAwait(false);
        var answer = JsonSerializer.Deserialize<ScopeValuesAnswer>(json, AnswerOptions)
            ?? throw new SqlFlowException($"The listing of parameter '{request.Parameter}' of flow '{request.FlowName}' answered nothing.");
        var listed = answer.Parameters.Count == 1
            ? answer.Parameters[0]
            : throw new SqlFlowException($"The listing of parameter '{request.Parameter}' of flow '{request.FlowName}' answered {answer.Parameters.Count} parameters, not the one asked.");
        return new RunParameterValues(
            request.Parameter,
            $"column {listed.Column} of {answer.RecordObject}",
            listed.Values.Select(v => new RunParameterValue(v.Value, v.Rows)).ToList(),
            listed.More);
    }
}
