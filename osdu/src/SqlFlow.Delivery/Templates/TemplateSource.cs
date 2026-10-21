using SqlFlow.Core;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Templates;

/// <summary>The identity a document pins a template by: the OSDU kind and the content version of its schema.</summary>
public readonly record struct TemplateReference(string Kind, string Version)
{
    public override string ToString() => $"{Kind} version {Version}";
}

/// <summary>A template as a listing shows it: its kind and version, and the file it is read from.</summary>
/// <param name="Kind">The OSDU kind the schema describes.</param>
/// <param name="Version">The content version, the hash of the canonical schema.</param>
/// <param name="File">The file holding it: its path in its repository, or on disk for a working copy.</param>
public sealed record TemplateInfo(string Kind, string Version, string File)
{
    public TemplateReference Reference => new(Kind, Version);
}

/// <summary>A template a document reads, found for a kind pattern, with its schema.</summary>
public sealed record TemplateMatch(TemplateInfo Template, SchemaSnapshot Schema);

/// <summary>What a document's template resolved to: the template and its schema, or why there is none.</summary>
public sealed record TemplateResolution(TemplateInfo? Template, SchemaSnapshot? Schema, string? Problem)
{
    /// <summary>Whether a template was found and read.</summary>
    public bool Found => Template is not null && Schema is not null;

    /// <summary>A template found.</summary>
    public static TemplateResolution Of(TemplateInfo template, SchemaSnapshot schema)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(schema);
        return new TemplateResolution(template, schema, null);
    }

    /// <summary>No template, and why: a clause that reads after the name of whoever asked (<c>where: problem</c>).</summary>
    public static TemplateResolution Missing(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        return new TemplateResolution(null, null, problem);
    }
}

/// <summary>
/// Where the templates a document fills or reads come from (osdu/docs/reference/concepts/templates.md): always the files of
/// the template folders of a repository. For the documents of a repository it is the template folders on the document's
/// path, read from disk (<see cref="FolderTemplates"/>) or as the catalog recorded them at the last sync; for what no
/// repository document asks (the explorer, the mapping builder) it is every template the synced repositories hold.
/// </summary>
public interface ITemplateSource
{
    /// <summary>
    /// The template <paramref name="kind"/> the document at <paramref name="document"/> names: version
    /// <paramref name="version"/> exactly when it names one, and otherwise the one the source takes for the kind. A source of
    /// template folders reads the folders on the document's path; the templates of every repository ignore the path.
    /// </summary>
    Task<TemplateResolution> ResolveAsync(string? document, string kind, string? version, CancellationToken ct = default);

    /// <summary>Every template the document at <paramref name="document"/> can read, nearest first for template folders.</summary>
    Task<IReadOnlyList<TemplateInfo>> ListForAsync(string? document, CancellationToken ct = default);
}

/// <summary>
/// Settles the template a document names while the document is read: the version it pins, checked where the reader is
/// asked to, or the version chosen for a kind it names alone. A document read where no template folder is known gets none,
/// and then has to name its version.
/// </summary>
public interface ITemplatePins
{
    /// <summary>The template <paramref name="kind"/> (and <paramref name="version"/>, when written) settles to.</summary>
    /// <param name="kind">The kind the document names.</param>
    /// <param name="version">The version it names, or null when it names the kind alone.</param>
    /// <param name="where">Where in the document the template is named, which a message starts with.</param>
    /// <exception cref="FlowValidationException">No template settles the name; the message says why and how to put one where it is read.</exception>
    TemplateReference Pin(string kind, string? version, string where);
}

/// <summary>Pins for reading a mapping document as something other than a render.</summary>
public static class TemplatePins
{
    /// <summary>
    /// The pins of a mapping document read on its own, as a check of the document or to tell which reference it declares:
    /// a version it names is taken as written, and a kind it names alone is left unsettled (the reference carries no
    /// version), since the template folders that settle it are its repository's. What is read so serves that alone and
    /// never renders.
    /// </summary>
    public static ITemplatePins DocumentAlone { get; } = new DocumentAlonePins();

    private sealed class DocumentAlonePins : ITemplatePins
    {
        public TemplateReference Pin(string kind, string? version, string where) => new(kind, version ?? string.Empty);
    }
}

/// <summary>
/// The templates the documents of a repository on disk read: the template folders on each document's path
/// (<see cref="TemplateFolders.ChainFor"/>). Every resolution reads the folders again, so a file changed between two runs
/// is read as it now is; a file's parse is kept for as long as it is unchanged.
/// </summary>
public sealed class FolderTemplates : ITemplateSource
{
    /// <summary>The source for documents whose repository's top is found by its <c>.git</c>, as runs and checks read them.</summary>
    public static FolderTemplates Default { get; } = new();

    private readonly string? _root;

    /// <param name="root">The repository's top, when the caller knows it (a sync, lineage): no folder above it is read.</param>
    public FolderTemplates(string? root = null)
    {
        _root = root is null ? null : Path.GetFullPath(root);
    }

    public Task<TemplateResolution> ResolveAsync(string? document, string kind, string? version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(document))
        {
            return Task.FromResult(TemplateResolution.Missing(
                $"template {kind}{(version is null ? string.Empty : $" version {version}")} is read from the template folders on a document's path, and this document was not read from a file."));
        }

        var pick = TemplateFolders.ChainFor(document, _root).Pick(kind, version);
        if (!pick.Found)
        {
            return Task.FromResult(TemplateResolution.Missing(pick.Problem!));
        }

        try
        {
            var schema = TemplateFolders.Load(pick.File!);
            return Task.FromResult(TemplateResolution.Of(Info(pick.File!, schema), schema));
        }
        catch (DeliveryException ex)
        {
            return Task.FromResult(TemplateResolution.Missing(ex.Message));
        }
    }

    public Task<IReadOnlyList<TemplateInfo>> ListForAsync(string? document, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(document))
        {
            return Task.FromResult<IReadOnlyList<TemplateInfo>>([]);
        }

        var seen = new HashSet<TemplateReference>();
        var templates = new List<TemplateInfo>();
        foreach (var file in TemplateFolders.ChainFor(document, _root).Templates)
        {
            if (seen.Add(file.Reference))
            {
                templates.Add(new TemplateInfo(file.Kind!, file.Version!, file.Shown));
            }
        }

        return Task.FromResult<IReadOnlyList<TemplateInfo>>(templates);
    }

    /// <summary>A pin settled from the template folders on the path of the document at <paramref name="document"/>, its version checked.</summary>
    public ITemplatePins For(string document) => new DocumentPins(this, document, checkVersions: true);

    /// <summary>
    /// A pin settled from the template folders on the path of the document at <paramref name="document"/> only where it names
    /// a kind alone: a pinned version is taken as written, and checked when its template is read for the render.
    /// </summary>
    public ITemplatePins ForKindsAlone(string document) => new DocumentPins(this, document, checkVersions: false);

    private static TemplateInfo Info(TemplateFolderFile file, SchemaSnapshot schema)
        => new(schema.Kind, schema.Version, file.Shown);

    private sealed class DocumentPins(FolderTemplates source, string document, bool checkVersions) : ITemplatePins
    {
        public TemplateReference Pin(string kind, string? version, string where)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            if (version is not null && !checkVersions)
            {
                return new TemplateReference(kind, version);
            }

            var pick = TemplateFolders.ChainFor(document, source._root).Pick(kind, version);
            return pick.Found
                ? pick.File!.Reference
                : throw new FlowValidationException($"{where}: {pick.Problem}");
        }
    }
}
