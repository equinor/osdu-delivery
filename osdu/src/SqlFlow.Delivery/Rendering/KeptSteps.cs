using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;

namespace SqlFlow.Delivery.Rendering;

/// <summary>
/// What an enrichment set aside on its hold (<c>onHold: step</c>) added to the version of the record the last delivery wrote
/// (osdu/docs/reference/flow/enrich-holds.md): the paths its mapping composed into the record, in the order it composed them,
/// and the records it added. A record whose enrichment holds is rendered with these in the enrichment's place, so OSDU keeps
/// what the enrichment gave until it renders again.
/// </summary>
/// <param name="Fragments">What the enrichment composed into the record, each as the composition took it.</param>
/// <param name="Records">The ids of the records the enrichment added to the record, which it keeps linking.</param>
public sealed record KeptStep(IReadOnlyList<JsonObject> Fragments, IReadOnlyList<string> Records)
{
    /// <summary>The target state value an enrichment's last delivered contribution is kept under.</summary>
    public static string StateKey(string step) => "enrich.step." + step;

    /// <summary>The contribution as the target state keeps it: canonical JSON, its fragments in order and its records in id order.</summary>
    public string ToText()
    {
        var node = new JsonObject
        {
            ["fragments"] = new JsonArray(Fragments.Select(f => (JsonNode?)f.DeepClone()).ToArray()),
            ["records"] = new JsonArray(Records.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()),
        };
        return CanonicalJson.ToString(node);
    }

    /// <summary>A contribution as the target state keeps it, or null for a value that does not read as one.</summary>
    public static KeptStep? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonObject node || node["fragments"] is not JsonArray fragments || node["records"] is not JsonArray records)
            {
                return null;
            }

            var composed = fragments.Select(f => f as JsonObject).ToList();
            var ids = records.Select(d => d is JsonValue value && value.TryGetValue<string>(out var id) ? id : null).ToList();
            return composed.Contains(null) || ids.Contains(null)
                ? null
                : new KeptStep(composed.Select(f => (JsonObject)f!.DeepClone()).ToList(), ids.Select(i => i!).ToList());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// What the steps set aside on their holds left in a record's last delivery, read from its target state: each step's kept
/// contribution, and whether OSDU holds a version of the record at all, which says whether a step with nothing kept added
/// nothing (a record never delivered) or was delivered before its contribution was kept.
/// </summary>
public sealed record KeptSteps
{
    /// <summary>A record OSDU holds no version of: a step set aside on it adds nothing.</summary>
    public static KeptSteps None { get; } = new();

    /// <summary>True when the ledger holds a version OSDU wrote of the record.</summary>
    public bool Delivered { get; init; }

    /// <summary>Each step's last delivered contribution, by the step's name.</summary>
    public IReadOnlyDictionary<string, KeptStep> Steps { get; init; } = new Dictionary<string, KeptStep>(StringComparer.Ordinal);

    /// <summary>What <paramref name="state"/>'s last delivery kept of the steps set aside on their holds; none for a record the ledger does not hold.</summary>
    public static KeptSteps From(RecordState? state)
    {
        if (state is null)
        {
            return None;
        }

        var steps = new Dictionary<string, KeptStep>(StringComparer.Ordinal);
        foreach (var (key, value) in JsonMerge.ToValues(state.TargetStateJson))
        {
            if (key.StartsWith("enrich.step.", StringComparison.Ordinal) && KeptStep.Parse(value) is { } kept)
            {
                steps[key["enrich.step.".Length..]] = kept;
            }
        }

        return new KeptSteps { Delivered = state.TargetVersion is not null, Steps = steps };
    }
}

/// <summary>
/// What a record's chain renders with beyond its rows (osdu/docs/reference/flow/enrich-holds.md): what its last delivery kept of
/// the steps set aside on their holds, and the steps the plan holds whatever they render, with why (a step whose files cannot
/// be found where its row says).
/// </summary>
public sealed record ChainInputs
{
    /// <summary>A record rendered from its rows alone.</summary>
    public static ChainInputs None { get; } = new();

    public KeptSteps Kept { get; init; } = KeptSteps.None;

    /// <summary>The steps held whatever they render, by name, each with its reason.</summary>
    public IReadOnlyDictionary<string, string> Holding { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>These inputs, holding <paramref name="step"/> too for <paramref name="reason"/>.</summary>
    public ChainInputs Hold(string step, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return this with { Holding = new Dictionary<string, string>(Holding, StringComparer.Ordinal) { [step] = reason } };
    }
}
