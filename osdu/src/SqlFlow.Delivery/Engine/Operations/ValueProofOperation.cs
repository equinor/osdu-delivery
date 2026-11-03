using System.Globalization;
using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Operations;

/// <summary>
/// <c>delivery-value-proof</c>: the proof of one finding of a value check (<see cref="ValueProver"/>, osdu/docs/reference/concepts/value-proof.md):
/// where the flow's source holds the values behind it, the records to fix by their keys, and what the partition's cache or the
/// platform's search holds of what was looked for, each as the statement a person copies, with the step <c>run</c> names run.
/// The task carries the finding as the check answered it: the attribute in <c>target</c>, what it comes from in <c>cause</c>,
/// the values behind it in <c>sourceValues</c> and <c>looked</c>, the cache version the check read in <c>cacheVersion</c>, and
/// the mapping it was asked of in <c>mapping</c>. Every statement is written here, never taken from the task, and reads only
/// the columns the attribute reads; nothing is written anywhere.
/// </summary>
public sealed class ValueProofOperation : DeliveryOperation
{
    public const string OperationName = "delivery-value-proof";

    public const string TargetArgument = "target";

    public const string CauseArgument = "cause";

    public const string SourceValuesArgument = "sourceValues";

    public const string LookedArgument = "looked";

    public const string CacheVersionArgument = "cacheVersion";

    public const string RunArgument = "run";

    public const string LimitArgument = "limit";

    public const string MappingArgument = "mapping";

    /// <summary>The steps a proof can run.</summary>
    public static IReadOnlyList<string> Steps { get; } = ["values", "records", "cache"];

    private static readonly JsonSerializerOptions Read = new(JsonSerializerDefaults.Web);

    public ValueProofOperation(EngineContext context)
        : base(context)
    {
    }

    public override string Name => OperationName;

    public override string CorrelationOperation => "valueproof";

    protected override async Task<object> RunAsync(EngineContext context, FlowDefinition flow, ComputeTaskPayload payload, CancellationToken ct)
    {
        var request = Request(payload);
        using var runtime = await FlowRuntime.CreateAsync(flow.Interface is null ? context : context.ForInterface(flow.Interface), flow, Values(payload), ct).ConfigureAwait(false);
        if (payload.Argument(MappingArgument) is { } expected && !string.Equals(expected.Trim(), runtime.Mapping.Mapping.Reference, StringComparison.Ordinal))
        {
            throw new DeliveryException(
                $"Flow '{flow.Label}' renders with mapping {runtime.Mapping.Mapping.Reference}, not {expected.Trim()}: the catalog and the repository have drifted. Sync the repository and check again.");
        }

        return await new ValueProver(runtime).ProveAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>The proof the task asks for, read as strictly as the control plane wrote it.</summary>
    internal static ValueProofRequest Request(ComputeTaskPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var target = payload.Argument(TargetArgument) ?? throw new SqlFlowException($"The task names no '{TargetArgument}': a proof is of one attribute.");
        var cause = Json<ValueCheckCause>(payload, CauseArgument) ?? throw new SqlFlowException($"The task names no '{CauseArgument}': a proof is of what a finding comes from.");
        var run = payload.Argument(RunArgument);
        if (run is not null && !Steps.Contains(run, StringComparer.Ordinal))
        {
            throw new SqlFlowException($"The task's '{RunArgument}' argument is '{run}'; a proof runs one of {string.Join(", ", Steps)}.");
        }

        var limit = ValueProver.DefaultLimit;
        if (payload.Argument(LimitArgument) is { } text && !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out limit))
        {
            throw new SqlFlowException($"The task's '{LimitArgument}' argument is not a number of rows: '{text}'.");
        }

        return new ValueProofRequest
        {
            Target = target,
            Cause = cause,
            SourceValues = Json<List<ValueCheckSourceValue>>(payload, SourceValuesArgument) ?? [],
            Looked = Json<List<string>>(payload, LookedArgument) ?? [],
            CacheVersion = payload.Argument(CacheVersionArgument),
            Run = run,
            Limit = limit,
        };
    }

    private static T? Json<T>(ComputeTaskPayload payload, string argument)
        where T : class
    {
        if (payload.Argument(argument) is not { } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Read);
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"The task's '{argument}' argument is not what a proof reads there: {ex.Message}", ex);
        }
    }
}
