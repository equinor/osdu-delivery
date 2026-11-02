using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-table-rows</c>: the first rows of one table a flow reads, every column of it, for a mapping overview's look at a
/// table (osdu/docs/reference/concepts/mapping-overview.md, Looking at a table's rows). The operation reads the record table or
/// a child dataset with the flow's own connection reference, over the rows a delivery reads in the scope it is given, cut to a
/// bounded page and a bounded count. It renders nothing and needs nothing of the mapping, so a mapping that does not pass its
/// preflight still shows the rows it was written for. Nothing is written.
/// </summary>
public sealed class TableRowsOperation : DeliveryOperation
{
    public const string OperationName = "delivery-table-rows";

    /// <summary>The most rows read.</summary>
    public const int MaxLimit = 200;

    /// <summary>The rows read unless another number is asked for.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The most characters a value keeps.</summary>
    public const int MaxCharacters = 1000;

    /// <summary>How far the rows are counted.</summary>
    public const long CountUpTo = 100_000;

    public const string DatasetArgument = "dataset";

    public const string LimitArgument = "limit";

    public TableRowsOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    public override string CorrelationOperation => "table";

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var dataset = payload.Argument(DatasetArgument);
        if (dataset is not null && !flow.Source.Datasets.ContainsKey(dataset))
        {
            throw new SqlFlowException(
                $"Flow '{flow.Label}' reads no child dataset '{dataset}'; it reads {(flow.Source.Datasets.Count == 0 ? "none" : string.Join(", ", flow.Source.Datasets.Keys))}.");
        }

        var source = context.Sources.Open(flow, FlowParameters.Resolve(flow, Values(payload)), context.Loggers);
        var read = await source.ReadTableAsync(
            new TableRowsQuery { Dataset = dataset, Limit = Limit(payload), MaxCharacters = MaxCharacters, CountUpTo = CountUpTo },
            ct).ConfigureAwait(false);
        return new TableRowsView(
            flow.Label,
            flow.Interface,
            dataset,
            read.Table,
            read.Columns,
            read.Rows,
            read.Counted,
            read.CountCapped,
            MaxCharacters,
            context.Time.GetUtcNow().UtcDateTime);
    }

    private static int Limit(ComputeTaskPayload payload)
    {
        if (payload.Argument(LimitArgument) is not { } text)
        {
            return DefaultLimit;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit is >= 1 and <= MaxLimit
            ? limit
            : throw new SqlFlowException($"The task's '{LimitArgument}' argument is '{text}', not a whole number from 1 to {MaxLimit}.");
    }
}

/// <summary>What <see cref="TableRowsOperation"/> answers.</summary>
/// <param name="Flow">The flow's label.</param>
/// <param name="Interface">The interface, for a source with several.</param>
/// <param name="Dataset">The child dataset read; null for the record table.</param>
/// <param name="Table">The table, as the flow names it.</param>
/// <param name="Columns">Its columns, in the table's order, without SQLFlow's system columns.</param>
/// <param name="Rows">Each row's values, in the order of <paramref name="Columns"/>.</param>
/// <param name="Counted">How many rows a delivery reads there, counted up to <see cref="TableRowsOperation.CountUpTo"/>.</param>
/// <param name="CountCapped">True when there are more than that.</param>
/// <param name="MaxCharacters">The most characters a value keeps; a column whose values were cut says so.</param>
/// <param name="ReadUtc">When it was read.</param>
public sealed record TableRowsView(
    string Flow, string? Interface, string? Dataset, string Table, IReadOnlyList<TableColumn> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows,
    long Counted, bool CountCapped, int MaxCharacters, DateTime ReadUtc);
