using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-export-value-issues</c>: the value check of <see cref="CheckValuesOperation"/> over the same rows, every row a
/// variable named fails written to an Excel workbook with the source data behind it (<see cref="ValueIssuesWorkbook"/>),
/// rather than the first few of each finding. It is asked for as a check is, and the task's result carries the file, which
/// is how a node hands one back: a task holds its result on the queue, so the file is bounded by what a result may hold, and
/// one with more issues than that says so. Nothing is written: not the ledger, not the work location, not OSDU.
/// </summary>
public sealed class ExportValueIssuesOperation : DeliveryOperation
{
    public const string OperationName = "delivery-export-value-issues";

    /// <summary>
    /// The bytes the Issues sheet may bring the file to. Written as Base64, 5,000,000 bytes are about 6,700,000 characters,
    /// which leaves the Summary and About sheets and the rest of the answer under the 8,000,000 a node task's result may hold.
    /// </summary>
    public const int MaxWorkbookBytes = 5_000_000;

    public ExportValueIssuesOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    public override string CorrelationOperation => "values";

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        // The workbook lists every issue, so the check keeps no examples of its own.
        var request = CheckValuesOperation.Request(payload) with { Samples = 0, SkipSamples = 0 };
        using var runtime = await FlowRuntime.CreateAsync(flow.Interface is null ? context : context.ForInterface(flow.Interface), flow, Values(payload), ct).ConfigureAwait(false);
        var resolved = runtime.Mapping;
        var baseData = ValueIssuesBaseData.Of(flow, resolved.Mapping, ValueChecker.Select(request.Targets, resolved.Mapping, resolved.Schema));
        using var file = new MemoryStream();
        using var workbook = new ValueIssuesWorkbook(file, MaxWorkbookBytes, baseData);
        var check = await new ValueChecker(runtime).CheckAsync(request, workbook.Add, ct).ConfigureAwait(false);
        workbook.Complete(check, flow, context.Time.GetUtcNow().UtcDateTime);
        return new ValueIssuesExport
        {
            Flow = check.Flow,
            Interface = check.Interface,
            RowsChecked = check.Rows.Checked,
            Issues = workbook.Found,
            Listed = workbook.Listed,
            CheckedUtc = check.CheckedUtc,
            Workbook = file.ToArray(),
        };
    }
}

/// <summary>What <see cref="ExportValueIssuesOperation"/> answers: what the check found, and the workbook listing it.</summary>
public sealed record ValueIssuesExport
{
    public required string Flow { get; init; }

    public string? Interface { get; init; }

    /// <summary>The rows inspected.</summary>
    public required long RowsChecked { get; init; }

    /// <summary>Every issue found.</summary>
    public required long Issues { get; init; }

    /// <summary>The issues the workbook lists; fewer than <see cref="Issues"/> when they do not fit, which its About sheet says.</summary>
    public required long Listed { get; init; }

    public required DateTime CheckedUtc { get; init; }

    /// <summary>The <c>.xlsx</c> file, which the answer's JSON carries as Base64.</summary>
    public required ReadOnlyMemory<byte> Workbook { get; init; }
}
