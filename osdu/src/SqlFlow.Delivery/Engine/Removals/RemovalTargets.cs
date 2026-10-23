using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Removals;

/// <summary>
/// Finds the delivery flow a removal names (osdu/docs/reference/flow/removal.md, The delivery flow), in the checkout the removal
/// flow runs from, and binds it to the run's partition and the removal's interface: the delivery flow whose ledger the removal
/// compares and writes to, and whose route, credentials and removal path take the records out. Where the catalog's read model
/// of the repository's interfaces says which file declares the flow, that file is the one, found beside the removal flow's own;
/// a checkout the catalog has not synced is searched, from the removal flow's folder up, for the one delivery document of that name.
/// </summary>
public static class RemovalTargets
{
    /// <summary>How many folders up from the removal flow's own a search for its delivery flow goes.</summary>
    public const int MaxLevelsUp = 6;

    /// <summary>How deep below a folder a search for a delivery flow reads.</summary>
    public const int MaxDepth = 5;

    /// <summary>The most documents a search for a delivery flow reads before it gives up, so a removal flow never walks a whole disk.</summary>
    public const int MaxDocuments = 5000;

    private static readonly string[] Skipped = ["node_modules", "bin", "obj", ".git", ".work", ".sqlflow"];

    /// <summary>The delivery flow a removal names, bound to <paramref name="partition"/> and the removal's interface, and the file it was read from.</summary>
    /// <exception cref="DeliveryException">The flow cannot be found, is found twice, or does not serve the partition or name the interface.</exception>
    public static async Task<(FlowDefinition Flow, string File)> ResolveAsync(
        EngineContext context, RemovalFlowDefinition removal, RemovalEntry entry, string partition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(removal);
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var origin = removal.SourcePath ?? throw new DeliveryException(
            $"Removal flow '{removal.Name}' has no file, so the delivery flow '{entry.Flow}' it names cannot be found beside it.");
        var file = await CatalogFileAsync(context, origin, entry, ct).ConfigureAwait(false) ?? SearchFile(context, origin, entry);
        var source = context.Documents.LoadSource(file);
        if (!string.Equals(source.Name, entry.Flow, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeliveryException($"{entry.Path}.flow names '{entry.Flow}', and {file} declares '{source.Name}'; re-sync the repository.");
        }

        var registry = source.NeedsRegistry(partition)
            ? await context.PartitionRegistry.ReadAsync(ct).ConfigureAwait(false)
            : RegisteredPartitions.None;
        SourceDefinition bound;
        try
        {
            bound = source.Resolve(partition, registry);
        }
        catch (DeliveryException ex)
        {
            throw new DeliveryException($"{entry.Path}: delivery flow '{entry.Flow}' cannot be read in partition '{partition}': {ex.Message}", ex);
        }

        FlowDefinition flow;
        try
        {
            flow = bound.Interface(entry.Interface);
        }
        catch (Exception ex) when (ex is DeliveryException or SqlFlowException)
        {
            throw new DeliveryException($"{entry.Path}.interface: {ex.Message}", ex);
        }

        return (flow, file);
    }

    /// <summary>
    /// The file the catalog says declares the flow, found under one of the removal flow's folders: the read model keeps each
    /// interface's path relative to its repository's root, and the removal flow sits somewhere below that root. Null when the
    /// catalog holds no interface of the flow, or none of its paths is beside the removal flow.
    /// </summary>
    private static async Task<string?> CatalogFileAsync(EngineContext context, string origin, RemovalEntry entry, CancellationToken ct)
    {
        if (context.Ledger is not { } ledger)
        {
            return null;
        }

        var paths = await ledger.DeliveryFlowPathsAsync(entry.Flow, ct).ConfigureAwait(false);
        if (paths.Count == 0)
        {
            return null;
        }

        var found = new List<string>();
        for (var folder = Path.GetDirectoryName(Path.GetFullPath(origin)); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            foreach (var path in paths)
            {
                var candidate = Path.GetFullPath(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(candidate) && !found.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    found.Add(candidate);
                }
            }

            if (found.Count > 0)
            {
                break;
            }
        }

        return found.Count switch
        {
            0 => null,
            1 => found[0],
            _ => throw new DeliveryException(
                $"{entry.Path}.flow names '{entry.Flow}', which the catalog finds declared in {string.Join(" and ", found)}; a removal names a flow one file declares."),
        };
    }

    /// <summary>
    /// The one delivery document declaring the flow, searched for from the removal flow's folder up, a folder at a time, each
    /// read <see cref="MaxDepth"/> folders deep: the first folder whose tree holds it settles it.
    /// </summary>
    private static string SearchFile(EngineContext context, string origin, RemovalEntry entry)
    {
        var read = 0;
        var folder = Path.GetDirectoryName(Path.GetFullPath(origin));
        for (var level = 0; folder is not null && level <= MaxLevelsUp; level++, folder = Path.GetDirectoryName(folder))
        {
            var found = new List<string>();
            foreach (var file in Documents(folder, 0))
            {
                if (++read > MaxDocuments)
                {
                    throw new DeliveryException(
                        $"{entry.Path}.flow names '{entry.Flow}', which the catalog does not know and {MaxDocuments} documents around the removal flow do not declare; sync the repository, or keep the removal flow beside its delivery flows.");
                }

                if (Declares(context, file, entry.Flow))
                {
                    found.Add(file);
                }
            }

            if (found.Count == 1)
            {
                return found[0];
            }

            if (found.Count > 1)
            {
                throw new DeliveryException(
                    $"{entry.Path}.flow names '{entry.Flow}', which {string.Join(" and ", found)} both declare; a removal names a flow one file declares.");
            }
        }

        throw new DeliveryException(
            $"{entry.Path}.flow names delivery flow '{entry.Flow}', which neither the catalog nor the folders around {origin} declare. Name a delivery flow of the same repository.");
    }

    private static IEnumerable<string> Documents(string folder, int depth)
    {
        IEnumerable<string> files;
        IEnumerable<string> folders;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.y*ml").Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)).ToList();
            folders = depth < MaxDepth ? Directory.EnumerateDirectories(folder).ToList() : [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A folder the node cannot read holds nothing it could run either.
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }

        foreach (var child in folders)
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || Skipped.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in Documents(child, depth + 1))
            {
                yield return file;
            }
        }
    }

    /// <summary>Whether <paramref name="file"/> is a delivery document declaring <paramref name="flow"/>: its kind and name only, read cheaply.</summary>
    private static bool Declares(EngineContext context, string file, string flow)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }

        try
        {
            if (!string.Equals(context.Documents.Probe(text, file), FlowDefinition.FlowTypeName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.Equals(context.Documents.ParseSource(text, file).Name, flow, StringComparison.OrdinalIgnoreCase);
        }
        catch (FlowValidationException)
        {
            // Not a document of the module, or one that does not load: it declares nothing a run could use.
            return false;
        }
    }
}
