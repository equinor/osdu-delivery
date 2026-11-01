using System.Globalization;
using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-source-sql</c>: the T-SQL a person runs in SSMS to see what an attribute of a mapping reads, as the flow's source
/// tables hold it now (<see cref="SourceQuery"/>). The operation reads the tables' shape with the flow's own connection
/// reference, to name their columns as they are, and reads no row. It runs nothing: the script is text for the reader to run.
/// </summary>
public sealed class SourceSqlOperation : DeliveryOperation
{
    public const string OperationName = "delivery-source-sql";

    public const string TargetArgument = "target";

    public const string MaxRowsArgument = "maxRows";

    public const string KeysArgument = "keys";

    public SourceSqlOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    public override string CorrelationOperation => "sourcesql";

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var mapping = new MappingCatalog(DeliveryLayout.Resolve(flow).MappingsDirectory, context.Documents).Load(flow.Render.Mapping);
        var source = context.Sources.Open(flow, FlowParameters.Resolve(flow, Values(payload)), context.Loggers);
        var described = await source.DescribeAsync(ct).ConfigureAwait(false);
        return SourceQuery.For(described, mapping, flow.Label, flow.Interface, Target(payload), MaxRows(payload), Keys(payload));
    }

    private static TemplatePath? Target(ComputeTaskPayload payload)
    {
        if (payload.Argument(TargetArgument) is not { } text)
        {
            return null;
        }

        return TemplatePath.TryParse(text, out var path, out var error)
            ? path
            : throw new SqlFlowException($"The task's '{TargetArgument}' argument is not a template path: {error}.");
    }

    private static long MaxRows(ComputeTaskPayload payload)
    {
        if (payload.Argument(MaxRowsArgument) is not { } text)
        {
            return 0;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var rows)
            ? rows
            : throw new SqlFlowException($"The task's '{MaxRowsArgument}' argument is not a number of rows: '{text}'.");
    }

    private static IReadOnlyList<KeyTuple> Keys(ComputeTaskPayload payload)
    {
        if (payload.Argument(KeysArgument) is not { } json)
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<string[]>>(json) ?? []).Select(parts => KeyTuple.Of(parts)).ToList();
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"The task's '{KeysArgument}' argument is not a JSON array of record keys: {ex.Message}", ex);
        }
    }
}
