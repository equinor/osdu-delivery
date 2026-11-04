using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// A delivery flow another flow names by its name (a removal's <c>flow</c>, an assertion's <c>delivered</c>), with where the
/// naming flow's document is and how its messages name the key that names it.
/// </summary>
public sealed record DeliveryFlowReference
{
    /// <summary>The delivery flow's name.</summary>
    public required string Flow { get; init; }

    /// <summary>The interface of that flow, for a source with interfaces; null for a flow in the single form.</summary>
    public string? Interface { get; init; }

    /// <summary>The naming flow's file, which the delivery flow is found beside; null for a flow read from no file.</summary>
    public required string? Origin { get; init; }

    /// <summary>The naming flow as a message opens with it: <c>Removal flow 'welldb-removal'</c>.</summary>
    public required string Owner { get; init; }

    /// <summary>The document path of the key that names the flow: <c>removals[0].flow</c>.</summary>
    public required string At { get; init; }

    /// <summary>The document path of the key that names the interface: <c>removals[0].interface</c>.</summary>
    public required string InterfaceAt { get; init; }

    /// <summary>What names the flow, as a rule's message says it: <c>a removal</c>.</summary>
    public required string Namer { get; init; }

    /// <summary>The kind of the naming flow: <c>removal flow</c>.</summary>
    public required string NamerFlow { get; init; }
}

/// <summary>
/// Finds the delivery flow another flow names, in the checkout the naming flow runs from, and binds it to the run's partition and
/// the interface it names (osdu/docs/reference/flow/removal.md, The delivery flow). Where the catalog's read model of the
/// repository's interfaces says which file declares the flow, that file is the one, found beside the naming flow's own; a checkout
/// the catalog has not synced is searched, from the naming flow's folder up, for the one delivery document of that name.
/// </summary>
public static class DeliveryFlowDocuments
{
    /// <summary>How many folders up from the naming flow's own a search for its delivery flow goes.</summary>
    public const int MaxLevelsUp = 6;

    /// <summary>How deep below a folder a search for a delivery flow reads.</summary>
    public const int MaxDepth = 5;

    /// <summary>The most documents a search for a delivery flow reads before it gives up, so a run never walks a whole disk.</summary>
    public const int MaxDocuments = 5000;

    private static readonly string[] Skipped = ["node_modules", "bin", "obj", ".git", ".work", ".sqlflow"];

    /// <summary>The delivery flow <paramref name="reference"/> names, bound to <paramref name="partition"/> and its interface, and the file it was read from.</summary>
    /// <exception cref="DeliveryException">The flow cannot be found, is found twice, or does not serve the partition or name the interface.</exception>
    public static async Task<(FlowDefinition Flow, string File)> ResolveAsync(
        EngineContext context, DeliveryFlowReference reference, string partition, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var origin = reference.Origin ?? throw new DeliveryException(
            $"{reference.Owner} has no file, so the delivery flow '{reference.Flow}' it names cannot be found beside it.");
        var file = await CatalogFileAsync(context, origin, reference, ct).ConfigureAwait(false) ?? SearchFile(context, origin, reference);
        var source = context.Documents.LoadSource(file);
        if (!string.Equals(source.Name, reference.Flow, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeliveryException($"{reference.At} names '{reference.Flow}', and {file} declares '{source.Name}'; re-sync the repository.");
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
            throw new DeliveryException($"{Parent(reference.At)}: delivery flow '{reference.Flow}' cannot be read in partition '{partition}': {ex.Message}", ex);
        }

        FlowDefinition flow;
        try
        {
            flow = bound.Interface(reference.Interface);
        }
        catch (Exception ex) when (ex is DeliveryException or SqlFlowException)
        {
            throw new DeliveryException($"{reference.InterfaceAt}: {ex.Message}", ex);
        }

        return (flow, file);
    }

    /// <summary>The document path that holds a key: <c>removals[0]</c> of <c>removals[0].flow</c>.</summary>
    private static string Parent(string at)
    {
        var dot = at.LastIndexOf('.');
        return dot > 0 ? at[..dot] : at;
    }

    /// <summary>
    /// The file the catalog says declares the flow, found under one of the naming flow's folders: the read model keeps each
    /// interface's path relative to its repository's root, and the naming flow sits somewhere below that root. Null when the
    /// catalog holds no interface of the flow, or none of its paths is beside the naming flow.
    /// </summary>
    private static async Task<string?> CatalogFileAsync(EngineContext context, string origin, DeliveryFlowReference reference, CancellationToken ct)
    {
        if (context.Ledger is not { } ledger)
        {
            return null;
        }

        var paths = await ledger.DeliveryFlowPathsAsync(reference.Flow, ct).ConfigureAwait(false);
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
                $"{reference.At} names '{reference.Flow}', which the catalog finds declared in {string.Join(" and ", found)}; {reference.Namer} names a flow one file declares."),
        };
    }

    /// <summary>
    /// The one delivery document declaring the flow, searched for from the naming flow's folder up, a folder at a time, each read
    /// <see cref="MaxDepth"/> folders deep: the first folder whose tree holds it settles it.
    /// </summary>
    private static string SearchFile(EngineContext context, string origin, DeliveryFlowReference reference)
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
                        $"{reference.At} names '{reference.Flow}', which the catalog does not know and {MaxDocuments} documents around the {reference.NamerFlow} do not declare; sync the repository, or keep the {reference.NamerFlow} beside its delivery flows.");
                }

                if (Declares(context, file, reference.Flow))
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
                    $"{reference.At} names '{reference.Flow}', which {string.Join(" and ", found)} both declare; {reference.Namer} names a flow one file declares.");
            }
        }

        throw new DeliveryException(
            $"{reference.At} names delivery flow '{reference.Flow}', which neither the catalog nor the folders around {origin} declare. Name a delivery flow of the same repository.");
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
