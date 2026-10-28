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

            if (Read(() => Whole(new MappingCatalog(directory, documents).Load(reference, full => Shown(full, folder)))) is { } problem)
            {
                problems.Add($"{KeyPaths.Of(flow).Mapping}: {problem}");
            }

            // Each step of enrich: pins a fragment, and its files the fragment of their dataset, found the way the flow's mapping is.
            foreach (var step in flow.Enrich ?? [])
            {
                if (step.Mapping is { } fragment
                    && Read(() => Fragment(new MappingCatalog(directory, documents).Load(fragment, full => Shown(full, folder)))) is { } refused)
                {
                    problems.Add($"{KeyPaths.Of(flow).Enrich}.{step.Name}.mapping: {refused}");
                }

                if (step.Files?.Mapping is { } files
                    && Read(() => Fragment(new MappingCatalog(directory, documents).Load(files, full => Shown(full, folder)))) is { } filesRefused)
                {
                    problems.Add($"{KeyPaths.Of(flow).Enrich}.{step.Name}.files.mapping: {filesRefused}");
                }
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

    /// <summary>A flow pins the mapping of a whole record; a fragment renders only the paths a step adds.</summary>
    private static void Whole(MappingDefinition mapping)
    {
        if (mapping.Fragment)
        {
            throw new FlowValidationException(
                $"{mapping.Reference} is a fragment (fragment: true), the paths a step of enrich: adds to a record; pin the mapping of the whole record, and name the fragment under enrich.<step>.mapping.");
        }
    }

    /// <summary>A step pins a fragment; the mapping of a whole record would render a record of its own.</summary>
    private static void Fragment(MappingDefinition mapping)
    {
        if (!mapping.Fragment)
        {
            throw new FlowValidationException(
                $"{mapping.Reference} is a mapping of a whole record; a step renders a fragment, the paths it adds, declared with fragment: true and no dataset.");
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
