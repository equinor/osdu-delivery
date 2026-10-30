using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Where a flow's mappings live. A flow repository keeps them next to its flows (osdu/docs/reference/flow/mapping.md,
/// Where a mapping lives and how a flow finds it) in <c>mappings/</c>. Templates live in the template folders on a
/// document's path, and caches in the module's database. A flow may name the directory explicitly under <c>render.mappings</c> (relative to the flow file);
/// otherwise the nearest directory of that name walking up from the flow file is used, so a flow three folders deep in
/// a repository still finds the repository's shared mappings.
/// </summary>
public sealed record DeliveryLayout(string MappingsDirectory)
{
    public const string MappingsDirectoryName = "mappings";

    private const int MaxAscent = 16;

    public static DeliveryLayout Resolve(FlowDefinition flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var baseDirectory = flow.SourcePath is { } path
            ? Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        return new DeliveryLayout(Locate(baseDirectory, flow.Render.MappingsDirectory, MappingsDirectoryName, within: null)!);
    }

    /// <summary>
    /// The layout of a flow whose file sits in <paramref name="flowFolder"/>, looked for only where
    /// <paramref name="within"/> allows: the search for the nearest mappings directory stops at the first folder outside,
    /// and a declared directory outside gives null. Lineage reads a flow's mappings this way, inside the checkout it scans.
    /// </summary>
    public static DeliveryLayout? ResolveWithin(FlowDefinition flow, string flowFolder, Func<string, bool> within)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFolder);
        ArgumentNullException.ThrowIfNull(within);
        return Locate(Path.GetFullPath(flowFolder), flow.Render.MappingsDirectory, MappingsDirectoryName, within) is { } directory
            ? new DeliveryLayout(directory)
            : null;
    }

    private static string? Locate(string baseDirectory, string? declared, string name, Func<string, bool>? within)
    {
        if (!string.IsNullOrWhiteSpace(declared))
        {
            var full = Path.GetFullPath(Path.Combine(baseDirectory, declared));
            return within is null || within(full) ? full : null;
        }

        var current = baseDirectory;
        for (var i = 0; i < MaxAscent && current is not null && (within is null || within(current)); i++)
        {
            var candidate = Path.Combine(current, name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = Path.GetDirectoryName(current);
        }

        // Nothing found: report the conventional place next to the flow, so the error names where to create it.
        return Path.Combine(baseDirectory, name);
    }
}
