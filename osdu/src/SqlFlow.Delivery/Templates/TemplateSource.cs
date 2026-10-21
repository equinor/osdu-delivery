using SqlFlow.Core;
using SqlFlow.Delivery.Snapshots;

namespace SqlFlow.Delivery.Templates;

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
/// Where the templates a document fills or reads come from (osdu/docs/reference/concepts/templates.md). For the documents of
/// a repository it is the template folders on the document's path, read from disk (<see cref="FolderTemplates"/>) or as the
/// catalog recorded them at the last sync; for what no repository document asks (the explorer, the Templates page, the
/// mapping builder) it is the saved templates, the library, which only assists.
/// </summary>
public interface ITemplateSource
{
    /// <summary>
    /// The template <paramref name="kind"/> the document at <paramref name="document"/> names: version
    /// <paramref name="version"/> exactly when it names one, and otherwise the one the source takes for the kind. A source of
    /// template folders reads the folders on the document's path; the library ignores the path.
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
                templates.Add(new TemplateInfo(file.Kind!, file.Version!, DateTime.MinValue, FolderCapturedBy, file.Shown));
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

    /// <summary>Who a template read from a folder was saved by, as a listing shows it.</summary>
    public const string FolderCapturedBy = "template folder";

    private static TemplateInfo Info(TemplateFolderFile file, SchemaSnapshot schema)
        => new(schema.Kind, schema.Version, schema.CapturedUtc.UtcDateTime, FolderCapturedBy, file.Shown);

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

/// <summary>How the library of saved templates settles a name: a version exactly, and a kind alone by the newest saved.</summary>
public static class TemplateLibrary
{
    /// <summary>What a template read from the library by its exact version says it came from.</summary>
    public const string LibraryOrigin = "read from the saved templates";

    /// <summary>The saved template <paramref name="kind"/> version <paramref name="version"/>, or the newest saved of the kind.</summary>
    public static async Task<TemplateResolution> ResolveAsync(ITemplateStore store, string kind, string? version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (!string.IsNullOrWhiteSpace(version))
        {
            // A version never changes, so the store's copy of it answers without listing the library; the listing below
            // only words why an exact version is not there.
            var exact = await store.LoadAsync(new TemplateReference(kind.Trim(), version.Trim()), ct).ConfigureAwait(false);
            if (exact is not null)
            {
                return TemplateResolution.Of(
                    new TemplateInfo(exact.Kind, exact.Version, exact.CapturedUtc.UtcDateTime, LibraryOrigin, LibraryOrigin), exact);
            }
        }

        var versions = (await store.ListAsync(ct).ConfigureAwait(false))
            .Where(t => string.Equals(t.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.CapturedUtc)
            .ToList();
        var chosen = string.IsNullOrWhiteSpace(version)
            ? versions.FirstOrDefault()
            : versions.FirstOrDefault(t => string.Equals(t.Version, version.Trim(), StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            return TemplateResolution.Missing(string.IsNullOrWhiteSpace(version)
                ? $"no template of {kind} is saved. Save it on the Templates page, or with 'sqlflow template capture --kind {kind}'."
                : $"template {kind} version {version} is not saved{(versions.Count == 0 ? string.Empty : $"; the saved versions of the kind are {TemplateChain.Listed(versions.Select(v => v.Version))}")}. Save it on the Templates page, or with 'sqlflow template import'.");
        }

        var schema = await store.LoadAsync(chosen.Reference, ct).ConfigureAwait(false);
        return schema is null
            ? TemplateResolution.Missing($"template {chosen.Reference} was listed and is no longer saved.")
            : TemplateResolution.Of(chosen, schema);
    }
}
