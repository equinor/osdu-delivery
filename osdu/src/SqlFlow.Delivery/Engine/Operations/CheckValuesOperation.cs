using System.Globalization;
using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-check-values</c>: the rows of a flow's scope that will not give the variables of its mapping the values the
/// template expects (<see cref="ValueChecker"/>), for the mapping page's value check. The node reads the ingestion tables
/// with the flow's own connection reference, renders with the pinned template and the partition's cache, and asks the
/// platform's search only what the mapping's searches ask a run. The task names the variables to check in <c>targets</c>
/// (a JSON array of template paths, none for every entry), how many rows to read in <c>maxRows</c> (0 for the whole scope),
/// how many example records each finding names in <c>samples</c> and how many it passes over first in <c>skipSamples</c>,
/// and the mapping it is asked of in <c>mapping</c>; <c>values</c> are the flow parameter values the scope is read with.
/// Nothing is written: not the ledger, not the work location, not OSDU.
/// </summary>
public sealed class CheckValuesOperation : DeliveryOperation
{
    public const string OperationName = "delivery-check-values";

    /// <summary>The most characters the answer carries, under the 8,000,000 a node task's result may hold.</summary>
    public const int MaxResultChars = 7_000_000;

    public CheckValuesOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    public override string CorrelationOperation => "values";

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var request = Request(payload);
        using var runtime = await FlowRuntime.CreateAsync(flow.Interface is null ? context : context.ForInterface(flow.Interface), flow, Values(payload), ct).ConfigureAwait(false);
        var check = await new ValueChecker(runtime).CheckAsync(request, each: null, ct).ConfigureAwait(false);

        // Measured as the answer is written, escapes and all, so what fits here fits the task result.
        return ValueChecker.Fit(check, MaxResultChars, c => JsonSerializer.Serialize(c, JsonOptions).Length);
    }

    /// <summary>The check the task asks for, read as strictly as the control plane wrote it.</summary>
    internal static ValueCheckRequest Request(ComputeTaskPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        IReadOnlyList<string> targets = [];
        if (payload.Argument("targets") is { } json)
        {
            try
            {
                targets = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
            catch (JsonException ex)
            {
                throw new SqlFlowException($"The task's 'targets' argument is not a JSON array of template paths: {ex.Message}", ex);
            }

            if (targets.Any(string.IsNullOrWhiteSpace))
            {
                throw new SqlFlowException("The task's 'targets' argument names an empty variable; name each as a template path, such as osdu.data.Name.");
            }
        }

        return new ValueCheckRequest
        {
            Targets = targets,
            MaxRows = Whole(payload, "maxRows") ?? ValueCheckLimits.DefaultRows,
            Samples = (int)Math.Min(Whole(payload, "samples") ?? ValueCheckLimits.DefaultSamples, int.MaxValue),
            SkipSamples = Whole(payload, "skipSamples") ?? 0,
            Mapping = payload.Argument("mapping"),
        };
    }

    private static long? Whole(ComputeTaskPayload payload, string name)
    {
        if (payload.Argument(name) is not { } text)
        {
            return null;
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new SqlFlowException($"The task's '{name}' argument is '{text}', not a whole number from 0.");
    }
}
