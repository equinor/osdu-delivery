namespace SqlFlow.Delivery.Model;

/// <summary>
/// A repository document (<c>documentType: repository</c>, osdu/docs/reference/flow/repository.md): the file
/// <see cref="FileName"/> at the root of a flow repository, naming the code the runs of its flows carry in the correlation
/// id of every OSDU request (osdu/docs/reference/concepts/request-correlation.md). A flow takes the nearest one in its
/// folder or a folder above it, up to the repository's root.
/// </summary>
public sealed record RepositoryDefinition
{
    public const string DocumentTypeName = "repository";

    /// <summary>The name the document is found by.</summary>
    public const string FileName = "osdu-delivery.yaml";

    /// <summary>The same name with the other extension YAML files take.</summary>
    public const string AlternateFileName = "osdu-delivery.yml";

    /// <summary>The repository's code: the segment after <c>osdu-delivery</c> in its runs' correlation id.</summary>
    public required string Code { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// True when <paramref name="fileName"/> is a name the document is found by, exactly: a node on Linux tells names apart by
    /// case where Windows does not, so a file is found the same way on every host.
    /// </summary>
    public static bool IsFileName(string? fileName)
        => string.Equals(fileName, FileName, StringComparison.Ordinal) || string.Equals(fileName, AlternateFileName, StringComparison.Ordinal);
}
