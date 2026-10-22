namespace SqlFlow.Delivery.Templates;

/// <summary>
/// A file of a template folder as it was read: the kind and version of the template it holds, or why it holds none.
/// </summary>
/// <param name="Path">Where the file is: a full path on disk, or the file's path in its repository when the catalog lists it.</param>
/// <param name="Shown">The path as messages name it: relative to the repository wherever the repository is known.</param>
/// <param name="Kind">The kind the file's schema declares (<c>x-osdu-schema-source</c>); null when it could not be read.</param>
/// <param name="Version">The template version of the file's schema, the hash of its canonical form; null when it could not be read.</param>
/// <param name="Problem">Why the file holds no template; null when it holds one.</param>
public sealed record TemplateFolderFile(string Path, string Shown, string? Kind, string? Version, string? Problem)
{
    /// <summary>
    /// The kind the template the file holds is linked to as its parent (<see cref="TemplateParent"/>); null for a template
    /// linked to none, and for a file that holds no template.
    /// </summary>
    public string? Parent { get; init; }

    /// <summary>Why the template's link to a parent cannot be read; null when it names one parent or none.</summary>
    public string? ParentProblem { get; init; }

    /// <summary>Whether the file holds a template.</summary>
    public bool IsTemplate => Problem is null && Kind is not null && Version is not null;

    /// <summary>The template the file holds.</summary>
    /// <exception cref="InvalidOperationException">The file holds none.</exception>
    public TemplateReference Reference => IsTemplate
        ? new TemplateReference(Kind!, Version!)
        : throw new InvalidOperationException($"{Shown} holds no template: {Problem}");

    /// <summary>Whether the file holds a template of <paramref name="kind"/>, whichever version.</summary>
    public bool Holds(string kind) => IsTemplate && string.Equals(Kind, kind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the file holds template <paramref name="kind"/> version <paramref name="version"/>.</summary>
    public bool Holds(string kind, string version) => Holds(kind) && string.Equals(Version, version, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A template folder: a directory named <c>templates</c>, and the files under it, in path order.</summary>
/// <param name="Path">The folder: a full path on disk, or its path in its repository.</param>
/// <param name="Shown">The folder as messages name it.</param>
/// <param name="Files">The files under it, in ordinal path order, those that hold no template among them.</param>
public sealed record TemplateFolder(string Path, string Shown, IReadOnlyList<TemplateFolderFile> Files);

/// <summary>What a pin found among the template folders on a document's path: the file holding the template, or why none does.</summary>
public sealed record TemplatePick(TemplateFolder? Folder, TemplateFolderFile? File, string? Problem)
{
    /// <summary>Whether a template was found.</summary>
    public bool Found => File is not null;
}

/// <summary>
/// The template folders a document reads, nearest first, and the rule a document's pin is resolved by
/// (osdu/docs/reference/concepts/templates.md, Template folders). A document reads the folder named <c>templates</c> beside
/// it and the one in every folder above it, up to the top of its repository. A pin that names a version takes it from the
/// nearest of those folders that holds it; a pin that names only a kind takes the nearest folder that holds the kind, which
/// has to hold one version of it. The same rule serves the folders read from disk (runs, checks, lineage, the sync) and the
/// folders the catalog recorded at the last sync (the control plane's pages), so both settle a pin alike.
/// </summary>
public sealed class TemplateChain
{
    /// <summary>The most files or folders one message lists; the rest are counted.</summary>
    public const int MaxListed = 5;

    private readonly Func<IReadOnlyList<TemplateFolderFile>>? _elsewhere;
    private IReadOnlyList<TemplateFolderFile>? _elsewhereRead;

    /// <param name="document">The document whose path is walked, as messages name it.</param>
    /// <param name="looked">Every place a template folder was looked for, nearest first, whether one is there or not.</param>
    /// <param name="folders">The template folders found there, nearest first.</param>
    /// <param name="elsewhere">
    /// Every template file of the repository, asked for only when a pin is not found on the path, so the message can say
    /// where the repository does hold it; null where the repository's other folders are not known.
    /// </param>
    public TemplateChain(
        string document,
        IReadOnlyList<string> looked,
        IReadOnlyList<TemplateFolder> folders,
        Func<IReadOnlyList<TemplateFolderFile>>? elsewhere = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        ArgumentNullException.ThrowIfNull(looked);
        ArgumentNullException.ThrowIfNull(folders);
        Document = document;
        Looked = looked;
        Folders = folders;
        _elsewhere = elsewhere;
    }

    /// <summary>The document whose path is walked, as messages name it.</summary>
    public string Document { get; }

    /// <summary>Every place a template folder was looked for, nearest first.</summary>
    public IReadOnlyList<string> Looked { get; }

    /// <summary>The template folders on the path, nearest first.</summary>
    public IReadOnlyList<TemplateFolder> Folders { get; }

    /// <summary>Every file on the path that holds a template, nearest folder first.</summary>
    public IEnumerable<TemplateFolderFile> Templates => Folders.SelectMany(f => f.Files).Where(f => f.IsTemplate);

    /// <summary>
    /// The template <paramref name="kind"/> the document names: version <paramref name="version"/> exactly when it names
    /// one, and otherwise the one version the nearest folder holding the kind holds.
    /// </summary>
    public TemplatePick Pick(string kind, string? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return string.IsNullOrWhiteSpace(version) ? PickKind(kind) : PickVersion(kind, version.Trim());
    }

    private TemplatePick PickVersion(string kind, string version)
    {
        foreach (var folder in Folders)
        {
            if (folder.Files.FirstOrDefault(f => f.Holds(kind, version)) is { } file)
            {
                return new TemplatePick(folder, file, null);
            }
        }

        var heldOnPath = Templates.Where(f => f.Holds(kind)).ToList();
        var parts = new List<string>
        {
            $"no template folder on the path of {Document} holds template {kind} version {version}.",
        };
        if (heldOnPath.Count > 0)
        {
            parts.Add($"On its path {Listed(heldOnPath.Select(f => $"{f.Shown} holds version {f.Version}"))}.");
        }

        parts.AddRange(Context(f => f.Holds(kind, version), $"version {version} of {kind}"));
        parts.Add(Fix(kind, version));
        return new TemplatePick(null, null, string.Join(" ", parts));
    }

    private TemplatePick PickKind(string kind)
    {
        foreach (var folder in Folders)
        {
            var holding = folder.Files.Where(f => f.Holds(kind)).ToList();
            if (holding.Count == 0)
            {
                continue;
            }

            var versions = holding.GroupBy(f => f.Version!, StringComparer.OrdinalIgnoreCase).ToList();
            if (versions.Count == 1)
            {
                return new TemplatePick(folder, holding[0], null);
            }

            return new TemplatePick(folder, null,
                $"{folder.Shown}, the nearest template folder on the path of {Document} holding {kind}, holds {versions.Count} versions of it: "
                + $"{Listed(versions.Select(v => $"{v.Key} ({string.Join(", ", v.Select(f => f.Shown))})"))}. "
                + "Name the version to use, or keep one version of the kind in the folder.");
        }

        var parts = new List<string> { $"no template folder on the path of {Document} holds a template of {kind}." };
        parts.AddRange(Context(f => f.Holds(kind), $"a template of {kind}"));
        parts.Add(Fix(kind, null));
        return new TemplatePick(null, null, string.Join(" ", parts));
    }

    /// <summary>
    /// What a message adds when nothing on the path holds what was asked for: where the document looked, the files on its
    /// path that could not be read (one of them may be the one meant), and where else in the repository it is held.
    /// </summary>
    private IEnumerable<string> Context(Func<TemplateFolderFile, bool> wanted, string what)
    {
        if (Folders.Count == 0)
        {
            yield return Looked.Count == 0
                ? "A document reads the folder named templates beside it and those above it, up to the top of its repository, and there is none."
                : $"A document reads the folder named templates beside it and those above it, up to the top of its repository, and there is none: {Listed(Looked)}.";
        }
        else
        {
            yield return $"It reads {Listed(Folders.Select(f => f.Shown))}.";
        }

        var unreadable = Folders.SelectMany(f => f.Files).Where(f => !f.IsTemplate).ToList();
        if (unreadable.Count > 0)
        {
            yield return $"{(unreadable.Count == 1 ? "A file" : $"{unreadable.Count} files")} on its path could not be read as a template: "
                + $"{Listed(unreadable.Select(f => $"{f.Shown} ({f.Problem})"))}.";
        }

        if (_elsewhere is not null)
        {
            _elsewhereRead ??= _elsewhere();
            var onPath = Folders.SelectMany(f => f.Files).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var held = _elsewhereRead.Where(f => !onPath.Contains(f.Path) && wanted(f)).ToList();
            if (held.Count > 0)
            {
                yield return $"The repository holds {what} at {Listed(held.Select(f => f.Shown))}, which is not on its path.";
            }
        }
    }

    /// <summary>How to put the template where the document reads it.</summary>
    private string Fix(string kind, string? version)
    {
        var nearest = Folders.Count > 0 ? Folders[0].Shown : Looked.Count > 0 ? Looked[0] : "templates";
        var file = Documents.FlowMapper.IsRecordKind(kind) ? TemplateSources.FileName(kind) : "<kind>.json";
        return version is null
            ? $"Put the template's file in one of them: the Templates page downloads it from the OSDU data definitions, and 'sqlflow template capture --kind {kind} --out {nearest}/{file}' writes the newest release's."
            : $"Put the file holding version {version} in one of them: copy it from where the repository holds it, or, for a version a release of the OSDU data definitions publishes, download it on the Templates page or write it with 'sqlflow template capture --kind {kind} --release <tag> --out {nearest}/{file}'.";
    }

    /// <summary>Up to <see cref="MaxListed"/> items, and how many more there are.</summary>
    public static string Listed(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var all = items.ToList();
        var shown = string.Join(", ", all.Take(MaxListed));
        return all.Count > MaxListed ? $"{shown} and {all.Count - MaxListed} more" : shown;
    }
}
