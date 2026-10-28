using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Validation;

/// <summary>
/// Where a flow's payload files may sit (osdu/docs/reference/flow/delivery.md, source). A record names its payload
/// folder in a column rather than carrying bytes, and the node opens that folder with its own identity when the run
/// delivers. That identity can read whatever it has been granted, so an unguarded location would let a row have any
/// readable file shipped to OSDU. The roots a flow allows are each payload's declared <c>root</c>, with the run's
/// parameter values substituted.
/// </summary>
public static class PayloadRoots
{
    /// <summary>The prefixes the flow's payload files may sit under, in the order they are declared, without repeats.</summary>
    public static IReadOnlyList<string> Of(FlowDefinition flow, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        var roots = new List<string>();
        foreach (var (name, payload) in flow.Source.Payloads)
        {
            Add(roots, FlowParameters.ResolvePath(flow, payload.Root, values, $"source.payloads.{name}.root"));
        }

        // The files a step of enrich: registers sit under the step's own root (osdu/docs/reference/flow/enrich.md).
        foreach (var step in flow.Enrich ?? [])
        {
            if (step.Files is { } files)
            {
                Add(roots, FlowParameters.ResolvePath(flow, files.Payload.Root, values, $"enrich.{step.Name}.files.root"));
            }
        }

        return roots;
    }

    /// <summary>
    /// Why <paramref name="location"/> is not somewhere this flow's payload files may be read from, or null when it is. The
    /// location has to sit under one of the flow's roots and may not climb out of it.
    /// </summary>
    public static string? Refusal(FlowDefinition flow, IReadOnlyDictionary<string, string> values, string location)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(values);
        if (string.IsNullOrWhiteSpace(location))
        {
            return "a payload location is required; it is the folder the record's files already sit in.";
        }

        if (location.Contains("..", StringComparison.Ordinal))
        {
            return $"payload location '{location}' must not contain '..'.";
        }

        var roots = Of(flow, values);
        if (roots.Count == 0)
        {
            return $"flow '{flow.Name}' declares no payload root, so there is nowhere its payload files may sit.";
        }

        var candidate = Normalize(location);
        return roots.Any(root => IsUnder(candidate, root))
            ? null
            : $"payload location '{location}' is outside what flow '{flow.Name}' allows; its payload files must sit inside: {string.Join(", ", roots)}.";
    }

    /// <summary>True when <paramref name="location"/> is the root itself or sits under it.</summary>
    public static bool IsUnder(string location, string root)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(root);
        var candidate = Normalize(location);
        var prefix = Normalize(root);
        return candidate.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A location in the one form roots compare in: '/' separators, no trailing separator.</summary>
    internal static string Normalize(string location) => location.Trim().Replace('\\', '/').TrimEnd('/');

    private static void Add(List<string> roots, string root)
    {
        var normalized = Normalize(root);
        if (!roots.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(normalized);
        }
    }
}
