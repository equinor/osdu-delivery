using System.Globalization;
using System.Text.Json;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.ControlPlane.Background;

/// <summary>
/// What a delivery run did, read from its result (osdu/docs/reference/cli/run.md, The result), for the trace of a trigger
/// request and the summary a GitHub job writes: the records it planned, then those it delivered, left unchanged, is
/// retrying, held, failed or left waiting, only the counts that are not zero. A source's run adds its interfaces up and says
/// how many completed. Any other operation (a plan, a verify, a removal) and any other kind is left to the run's row counts.
/// </summary>
public sealed class DeliveryRunOutcomeDescriber : IRunOutcomeDescriber
{
    private static readonly (string Property, string Word)[] Counts =
    [
        ("delivered", "delivered"),
        ("skippedUnchanged", "unchanged"),
        ("unchanged", "unchanged"),
        ("retried", "retrying"),
        ("held", "held"),
        ("failed", "failed"),
        ("waiting", "waiting"),
    ];

    public string? Describe(CatalogRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!string.Equals(run.FlowKind, FlowDefinition.FlowTypeName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(run.ResultJson))
        {
            return null;
        }

        // A result that is not JSON throws here, and the caller logs it and falls back to the row counts.
        using var document = JsonDocument.Parse(run.ResultJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("interfaces", out var interfaces) && interfaces.ValueKind == JsonValueKind.Array)
        {
            var results = interfaces.EnumerateArray()
                .Where(i => i.ValueKind == JsonValueKind.Object)
                .ToList();
            var completed = results.Count(i => i.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String
                && string.Equals(state.GetString(), "completed", StringComparison.Ordinal));
            var tally = Tally(results.Select(i => i.TryGetProperty("result", out var result) ? result : default));
            var interfacesText = $"{completed} of {results.Count} {(results.Count == 1 ? "interface" : "interfaces")} completed";
            return tally is null ? interfacesText : $"{tally}; {interfacesText}";
        }

        if (!root.TryGetProperty("delivered", out _))
        {
            return null;
        }

        if (root.TryGetProperty("nothingToDo", out var nothing) && nothing.ValueKind == JsonValueKind.True)
        {
            return "nothing to deliver";
        }

        return Tally([root]) ?? "nothing to deliver";
    }

    /// <summary>The counts added up over <paramref name="results"/>: "1,840 planned: 1,828 delivered, 12 unchanged", or null
    /// when every count is zero.</summary>
    private static string? Tally(IEnumerable<JsonElement> results)
    {
        long planned = 0;
        var sums = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var result in results.Where(r => r.ValueKind == JsonValueKind.Object))
        {
            planned += Number(result, "planned");
            foreach (var (property, word) in Counts)
            {
                sums[word] = sums.GetValueOrDefault(word) + Number(result, property);
            }
        }

        var parts = Counts.Select(c => c.Word).Distinct(StringComparer.Ordinal)
            .Where(word => sums.GetValueOrDefault(word) > 0)
            .Select(word => $"{N(sums[word])} {word}")
            .ToList();
        if (parts.Count == 0)
        {
            return planned > 0 ? $"{N(planned)} planned" : null;
        }

        var list = string.Join(", ", parts);
        return planned > 0 ? $"{N(planned)} planned: {list}" : list;
    }

    private static long Number(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
