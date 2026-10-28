using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// The offline check <c>sqlflow validate</c> makes of the files a flow names beside it
/// (<see cref="SqlFlow.Yaml.RegisteredFlowDocument.CheckOffline"/>): the mapping each interface of a delivery flow pins,
/// and the dictionary each dictionary type of a cache flow holds. Each is found and read the way a run finds and reads it,
/// through the same lookup (<see cref="DeliveryLayout"/> and <see cref="MappingCatalog"/>, as the render resolver uses them;
/// <see cref="DictionaryCatalog"/>, as a refresh and the repository sync use it), so validate refuses exactly what a run
/// would fail on. Paths are named relative to the flow's file. Parsing a flow does not look for them, so the estate scan and
/// the catalog sync keep a flow whose file is missing and report it in their own terms.
/// </summary>
internal static class CompanionFiles
{
    /// <summary>What is wrong with the mappings the interfaces of <paramref name="source"/>, read from <paramref name="documentPath"/>, pin: one sentence each.</summary>
    public static IReadOnlyList<string> Mappings(SourceDefinition source, string documentPath, DeliveryDocumentLoader documents)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentNullException.ThrowIfNull(documents);
        var folder = Folder(documentPath);
        var problems = new List<string>();
        var read = new HashSet<(string Directory, string Reference)>();
        foreach (var flow in source.Interfaces)
        {
            // The run's lookup: render.mappings when the flow names it, else the nearest mappings folder walking up.
            var directory = DeliveryLayout.ResolveWithin(flow, folder, _ => true)!.MappingsDirectory;
            var reference = flow.Render.Mapping;
            if (!read.Add((directory, reference)))
            {
                continue;
            }

            MappingDefinition? pinned = null;
            if (Read(() => pinned = Whole(new MappingCatalog(directory, documents).Load(reference, full => Shown(full, folder)))) is { } problem)
            {
                problems.Add($"{KeyPaths.Of(flow).Mapping}: {problem}");
                continue;
            }

            // Each enrichment of the mapping names an enrichment mapping, found the way the flow's mapping is; the flow binds what
            // it reads.
            foreach (var enrichment in pinned!.Enrich)
            {
                if (enrichment.Mapping is { } named
                    && Read(() => Fragment(new MappingCatalog(directory, documents).Load(named, full => Shown(full, folder)))) is { } refused)
                {
                    problems.Add($"{KeyPaths.Of(flow).Mapping} {reference}: enrich.{enrichment.Name}.mapping: {refused}");
                }
            }

            if (Read(() => EnrichBinding.Bind(flow, pinned, named => new MappingCatalog(directory, documents).Load(named, full => Shown(full, folder)))) is { } unbound)
            {
                problems.Add(unbound);
            }
        }

        return problems;
    }

    /// <summary>What is wrong with the dictionaries the types of <paramref name="flow"/>, read from <paramref name="documentPath"/>, hold: one sentence each.</summary>
    public static IReadOnlyList<string> Dictionaries(CacheDefinition flow, string documentPath, DeliveryDocumentLoader documents)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentNullException.ThrowIfNull(documents);
        var folder = Folder(documentPath);
        var problems = new List<string>();
        foreach (var type in flow.Types.Where(t => t.Origin == CacheOrigin.Dictionary && t.Dictionary is not null))
        {
            if (Read(() => new DictionaryCatalog(documents).Load(type.Dictionary!, folder, full => Shown(full, folder))) is { } problem)
            {
                problems.Add($"type '{type.Name}': {problem}");
            }
        }

        return problems;
    }

    /// <summary>What is wrong with the dictionaries the map steps of <paramref name="flow"/>, read from <paramref name="documentPath"/>, clean through: one sentence each.</summary>
    public static IReadOnlyList<string> Dictionaries(DimensionFlowDefinition flow, string documentPath, DeliveryDocumentLoader documents)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentNullException.ThrowIfNull(documents);
        var folder = Folder(documentPath);
        var problems = new List<string>();
        var read = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dimension in flow.Dimensions)
        {
            foreach (var step in dimension.Clean.Where(s => s.Kind == CleanStepKind.Map && s.Dictionary is not null))
            {
                if (read.Add(step.Dictionary!)
                    && Read(() => new DictionaryCatalog(documents).Load(step.Dictionary!, folder, full => Shown(full, folder))) is { } problem)
                {
                    problems.Add($"dimension '{dimension.Name}': {problem}");
                }
            }
        }

        return problems;
    }

    /// <summary>A flow pins the mapping that keys its records; an enrichment mapping renders only what an enrichment adds.</summary>
    private static MappingDefinition Whole(MappingDefinition mapping)
        => mapping.Fragment
            ? throw new FlowValidationException(
                $"{mapping.Reference} is an enrichment mapping (enrichment: true), what an enrichment of another mapping renders for its records; pin the mapping that keys the records, which names this one under its enrich:.")
            : mapping;

    /// <summary>An enrichment names an enrichment mapping; a mapping keying records of its own would deliver records of its own.</summary>
    private static void Fragment(MappingDefinition mapping)
    {
        if (!mapping.Fragment)
        {
            throw new FlowValidationException(
                $"{mapping.Reference} keys records of its own; an enrichment renders an enrichment mapping, declared with enrichment: true and no dataset.");
        }
    }

    /// <summary>Why <paramref name="read"/> could not find or read its file, or null when it did.</summary>
    private static string? Read(Action read)
    {
        try
        {
            read();
            return null;
        }
        catch (FlowValidationException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the file could not be read ({ex.GetType().Name}: {SecretHygiene.RedactedMessage(ex.Message)}).";
        }
    }

    private static string Folder(string documentPath)
        => Path.GetDirectoryName(Path.GetFullPath(documentPath)) ?? Directory.GetCurrentDirectory();

    /// <summary>A path as a problem names it: relative to the flow's folder, with forward slashes.</summary>
    private static string Shown(string full, string folder) => Path.GetRelativePath(folder, full).Replace('\\', '/');
}
