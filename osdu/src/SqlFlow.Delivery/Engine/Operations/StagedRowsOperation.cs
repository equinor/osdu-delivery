using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Source;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-staged-rows</c>: a page of the rows staged in a flow's scope, for a mapping's Staged records tab
/// (osdu/docs/reference/concepts/dry-run.md). The operation reads the record table with the flow's own connection reference, a
/// page at a time, each row named as the ledger would name it, with its file and row and how many rows each child dataset
/// holds of it. It renders nothing and needs nothing of the mapping but its key and label, so a mapping that does not pass
/// its preflight still shows the rows it was written for. Nothing is written.
/// </summary>
public sealed class StagedRowsOperation : DeliveryOperation
{
    public const string OperationName = "delivery-staged-rows";

    /// <summary>The most rows on a page.</summary>
    public const int MaxLimit = 500;

    /// <summary>The rows on a page unless it asks for another number.</summary>
    public const int DefaultLimit = 200;

    /// <summary>The longest text a search names.</summary>
    public const int MaxSearch = 200;

    public const string LimitArgument = "limit";

    public const string SearchArgument = "search";

    public const string AfterArgument = "after";

    public StagedRowsOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    public override string CorrelationOperation => "staged";

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var dataset = DatasetOf(context, flow, out var unread);
        var source = context.Sources.Open(flow, FlowParameters.Resolve(flow, Values(payload)), context.Loggers);
        var page = await source.ReadStagedAsync(
            new StagedRowsQuery
            {
                Limit = Limit(payload),
                After = payload.Argument(AfterArgument) is { } after ? KeyTuple.FromJson(after) : null,
                Search = payload.Argument(SearchArgument),
                Columns = dataset?.ReadColumns() ?? [],
            },
            ct).ConfigureAwait(false);

        return new StagedRows(
            flow.Label,
            flow.Interface,
            flow.Source.Record.Object,
            flow.Source.Record.Key,
            flow.Source.Datasets.ToDictionary(d => d.Key, d => d.Value.Object, StringComparer.Ordinal),
            page.Total,
            page.Rows.Select(row => View(row, dataset, flow)).ToList(),
            page.Next?.Json,
            unread,
            context.Time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// The key and label of the mapping the flow pins, read from its file alone: what a row's name and delivery key are made from.
    /// A mapping that cannot be read leaves the rows named by their key, and says why.
    /// </summary>
    private static MappingDataset? DatasetOf(EngineContext context, FlowDefinition flow, out string? unread)
    {
        unread = null;
        try
        {
            return new MappingCatalog(DeliveryLayout.Resolve(flow).MappingsDirectory, context.Documents).Load(flow.Render.Mapping).Dataset;
        }
        catch (SqlFlowException ex)
        {
            unread = ex.Message;
            return null;
        }
    }

    private static StagedRowView View(StagedRow row, MappingDataset? dataset, FlowDefinition flow)
    {
        var parts = row.Key.Values;
        var complete = !parts.Any(string.IsNullOrWhiteSpace);
        return new StagedRowView(
            dataset is null ? string.Join("/", parts) : SourceKey.Display(dataset.System, parts),
            row.Key.Json,
            complete && dataset is not null ? DeliveryKey.Derive(dataset.System, parts).Value : null,
            dataset?.LabelOf(column => row.Columns.GetValueOrDefault(column)),
            row.File,
            row.Row,
            row.UpdatedUtc,
            row.DeletedUtc,
            complete,
            row.Children);
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

/// <summary>One staged row as a page lists it: its name, where it came from, and what its child datasets hold of it.</summary>
/// <param name="SourceKey">The row's name, as the ledger would name its record.</param>
/// <param name="KeyJson">The key's parts as a JSON array, which names the row to a preview.</param>
/// <param name="DeliveryKey">The delivery key its record would have; null when a part of its key is empty, or the mapping could not be read.</param>
/// <param name="Label">The label the mapping gives it.</param>
/// <param name="File">The file it was landed from.</param>
/// <param name="Row">Its row in that file.</param>
/// <param name="UpdatedUtc">When the row last changed.</param>
/// <param name="DeletedUtc">When the ingestion table marked it deleted.</param>
/// <param name="Complete">False when a part of its key is empty, so it has no record.</param>
/// <param name="Children">How many rows each child dataset holds of it.</param>
public sealed record StagedRowView(
    string SourceKey, string KeyJson, Guid? DeliveryKey, string? Label, string? File, long? Row, DateTime? UpdatedUtc, DateTime? DeletedUtc, bool Complete,
    IReadOnlyDictionary<string, long> Children);

/// <summary>What <see cref="StagedRowsOperation"/> answers.</summary>
/// <param name="Flow">The flow's label.</param>
/// <param name="Interface">The interface, for a source with several.</param>
/// <param name="RecordObject">The record table.</param>
/// <param name="KeyColumns">Its key columns, in order.</param>
/// <param name="Datasets">The child tables the flow reads, by dataset name.</param>
/// <param name="Total">How many rows the scope and the search hold, on the first page; null on a later one.</param>
/// <param name="Rows">The page.</param>
/// <param name="Next">Where the next page starts; null on the last.</param>
/// <param name="MappingUnread">Why the rows are named by their key alone, when the mapping could not be read.</param>
/// <param name="ReadUtc">When it was read.</param>
public sealed record StagedRows(
    string Flow, string? Interface, string RecordObject, IReadOnlyList<string> KeyColumns, IReadOnlyDictionary<string, string> Datasets, long? Total,
    IReadOnlyList<StagedRowView> Rows, string? Next, string? MappingUnread, DateTime ReadUtc);
