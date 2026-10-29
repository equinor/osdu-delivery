using SqlFlow.Core;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// Finds the repository document a flow's runs take their repository code from (osdu/docs/reference/flow/repository.md): the
/// file <see cref="RepositoryDefinition.FileName"/> (or <see cref="RepositoryDefinition.AlternateFileName"/>) nearest the flow's
/// file, in its folder or a folder above it, the way a flow finds its <c>mappings/</c>. The walk stops at the repository's root
/// (the folder holding <c>.git</c>), so a repository never takes the code of a folder it sits in, and after 16 folders.
/// </summary>
public static class RepositoryDocuments
{
    private const int MaxAscent = 16;

    /// <summary>The folder or file git keeps a checkout's state in, which marks the repository's root.</summary>
    private const string GitMarker = ".git";

    /// <summary>
    /// The full path of the repository document for the flow whose file is <paramref name="flowFile"/>, or null when there
    /// is none, looked for only where <paramref name="within"/> allows (null allows anywhere).
    /// </summary>
    /// <exception cref="FlowValidationException">A folder holds the document under both of its names.</exception>
    public static string? Locate(string flowFile, Func<string, bool>? within = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFile);
        var current = Path.GetDirectoryName(Path.GetFullPath(flowFile));
        for (var i = 0; i < MaxAscent && current is not null && (within is null || within(current)); i++)
        {
            // A folder of the path that is not there holds nothing; the walk goes on above it.
            if (!Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
                continue;
            }

            var found = Directory.EnumerateFiles(current, "osdu-delivery.y*ml")
                .Where(f => RepositoryDefinition.IsFileName(Path.GetFileName(f)))
                .ToList();
            if (found.Count > 1)
            {
                throw new FlowValidationException(
                    $"{current} holds both {RepositoryDefinition.FileName} and {RepositoryDefinition.AlternateFileName}; a folder holds one repository document. Remove one.");
            }

            if (found.Count == 1)
            {
                return found[0];
            }

            if (Path.Exists(Path.Combine(current, GitMarker)))
            {
                return null;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>
    /// The repository document for the flow whose file is <paramref name="flowFile"/>, read, its messages naming the file as
    /// <paramref name="shown"/> gives it (its full path when null); null when there is none.
    /// </summary>
    /// <exception cref="FlowValidationException">The document is there and cannot be read.</exception>
    public static RepositoryFile? Find(string flowFile, DeliveryDocumentLoader documents, Func<string, string>? shown = null, Func<string, bool>? within = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        return Locate(flowFile, within) is { } path
            ? new RepositoryFile(documents.LoadRepository(path, shown?.Invoke(path) ?? path), path)
            : null;
    }
}

/// <summary>A repository document found for a flow: the document and its full path.</summary>
public sealed record RepositoryFile(RepositoryDefinition Repository, string FullPath);
