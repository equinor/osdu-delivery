using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// Every template the synced repositories hold in their template folders (osdu/docs/reference/concepts/templates.md, Where
/// templates are read): what reads a template where no document asks for one (the Templates page, the explorer, the mapping
/// builder, the assistant's tools). The catalog's rows (<c>[osdu].[RepositoryTemplate]</c>) say which file holds which
/// template; a template is read from its file, in the folder its repository was synced into (<see cref="IRepositoryFolders"/>),
/// never from the database. A kind named without a version settles to the version the repository synced last holds, then by
/// path; a version named is read from the first file holding it. A file that cannot be read (its repository's folder is not
/// to be had here, or the file changed since the sync) gives way to the next that holds the kind, and only when none can be
/// read is the first one's problem the answer.
/// </summary>
public sealed class RepositoryTemplates : ITemplateSource
{
    /// <summary>The most template files read at once, across every repository.</summary>
    public const int MaxFiles = 50_000;

    private readonly IReadOnlyList<RepositoryTemplateFile> _files;
    private readonly IRepositoryFolders _folders;
    private readonly ConcurrentDictionary<Guid, Task<RepositoryFolder>> _located = new();

    private RepositoryTemplates(IReadOnlyList<RepositoryTemplateFile> files, IRepositoryFolders folders)
    {
        _files = files;
        _folders = folders;
    }

    /// <summary>The templates <paramref name="files"/> list, each read from its file in the folder <paramref name="folders"/> finds.</summary>
    public static RepositoryTemplates Of(IEnumerable<RepositoryTemplateFile> files, IRepositoryFolders folders)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(folders);
        return new RepositoryTemplates(
            [.. files.OrderBy(f => f.RepoId).ThenBy(f => f.Path, StringComparer.Ordinal)],
            folders);
    }

    /// <summary>The templates the catalog in <paramref name="db"/> records.</summary>
    public static async Task<RepositoryTemplates> ReadAsync(Data.OsduDbContext db, IRepositoryFolders folders, CancellationToken ct)
        => Of(await FilesAsync(db, null, ct).ConfigureAwait(false), folders);

    /// <summary>The templates <paramref name="ledger"/> records.</summary>
    public static async Task<RepositoryTemplates> ReadAsync(ILedger ledger, IRepositoryFolders folders, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return Of(await ledger.RepositoryTemplatesAsync(ct).ConfigureAwait(false), folders);
    }

    /// <summary>The templates of repository <paramref name="repoId"/> alone, as the catalog in <paramref name="db"/> records them.</summary>
    public static async Task<RepositoryTemplates> ReadAsync(Data.OsduDbContext db, IRepositoryFolders folders, Guid repoId, CancellationToken ct)
        => Of(await FilesAsync(db, repoId, ct).ConfigureAwait(false), folders);

    /// <summary>
    /// The rows of the template folder files the repository sync recorded, by repository and path: those of repository
    /// <paramref name="repoId"/>, or of every repository when none is named.
    /// </summary>
    public static async Task<IReadOnlyList<RepositoryTemplateFile>> FilesAsync(Data.OsduDbContext db, Guid? repoId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = db.DeliveryRepositoryTemplates.AsNoTracking();
        if (repoId is { } only)
        {
            rows = rows.Where(t => t.RepoId == only);
        }

        return await rows
            .OrderBy(t => t.RepoId).ThenBy(t => t.Path)
            .Take(MaxFiles)
            .Select(t => new RepositoryTemplateFile(t.RepoId, t.Path, t.Folder, t.Kind, t.Version, t.Problem, t.LastSeenUtc, t.Parent))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Every file of every repository's template folders, by repository and path, those holding no template included.</summary>
    public IReadOnlyList<RepositoryTemplateFile> Files => _files;

    /// <summary>The template repository <paramref name="repoId"/>'s file at <paramref name="path"/> holds, read from the file.</summary>
    public async Task<TemplateResolution> LoadAsync(Guid repoId, string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = _files.FirstOrDefault(f => f.RepoId == repoId && string.Equals(f.Path, path, StringComparison.Ordinal));
        if (file is null)
        {
            return TemplateResolution.Missing(
                $"the template folders of repository {repoId:D} hold no file {path} as its last sync found them; sync the repository if the file is new");
        }

        if (file.Problem is not null || file.Kind.Length == 0 || file.Version.Length == 0)
        {
            return TemplateResolution.Missing($"{file.Path} holds no template: {file.Problem ?? "the sync recorded none in it"}");
        }

        var reference = new TemplateReference(file.Kind, file.Version);
        RepositoryFolder top;
        try
        {
            top = await _located.GetOrAdd(repoId, id => _folders.FolderOfAsync(id, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The asker who started the lookup gave up on it; the next asker looks again.
            _located.TryRemove(repoId, out _);
            throw;
        }

        if (top.Path is not { } root)
        {
            // A lookup that failed is asked again next time, so a folder that turns up later is read then.
            _located.TryRemove(repoId, out _);
            return TemplateResolution.Missing($"{file.Path} holds template {reference}, and {top.Problem}.");
        }

        var full = SyncedTemplates.RepositoryFile(root, file.Path);
        if (full is null)
        {
            return TemplateResolution.Missing($"{file.Path} holds template {reference}, and its path leads out of the repository.");
        }

        try
        {
            var schema = TemplateFolders.Load(new TemplateFolderFile(full, file.Path, file.Kind, file.Version, null));
            return TemplateResolution.Of(new TemplateInfo(schema.Kind, schema.Version, file.Path), schema);
        }
        catch (DeliveryException ex)
        {
            return TemplateResolution.Missing(SyncedTemplates.SyncAgain(ex.Message));
        }
    }

    public async Task<TemplateResolution> ResolveAsync(string? document, string kind, string? version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var ofKind = Templates().Where(f => string.Equals(f.Kind, kind.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = string.IsNullOrWhiteSpace(version)
            ? ofKind
            : ofKind.Where(f => string.Equals(f.Version, version.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            return TemplateResolution.Missing(string.IsNullOrWhiteSpace(version) || ofKind.Count == 0
                ? $"no synced repository holds a template of {kind} in a template folder. Put its file in one (the Templates page downloads it from the OSDU data definitions), and sync the repository"
                : $"no synced repository holds template {kind} version {version} in a template folder; they hold {TemplateChain.Listed(ofKind.Select(f => $"version {f.Version} in {f.Path}").Distinct(StringComparer.Ordinal))}");
        }

        TemplateResolution? first = null;
        foreach (var candidate in candidates.OrderByDescending(f => f.SeenUtc).ThenBy(f => f.Path, StringComparer.Ordinal))
        {
            var loaded = await LoadAsync(candidate.RepoId, candidate.Path, ct).ConfigureAwait(false);
            if (loaded.Found)
            {
                return loaded;
            }

            first ??= loaded;
        }

        return first!;
    }

    public Task<IReadOnlyList<TemplateInfo>> ListForAsync(string? document, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var seen = new HashSet<TemplateReference>();
        var templates = new List<TemplateInfo>();
        foreach (var file in Templates()
                     .OrderBy(f => f.Kind, StringComparer.OrdinalIgnoreCase)
                     .ThenByDescending(f => f.SeenUtc)
                     .ThenBy(f => f.Path, StringComparer.Ordinal))
        {
            if (seen.Add(new TemplateReference(file.Kind, file.Version)))
            {
                templates.Add(new TemplateInfo(file.Kind, file.Version, file.Path));
            }
        }

        return Task.FromResult<IReadOnlyList<TemplateInfo>>(templates);
    }

    /// <summary>The files that hold a template.</summary>
    private IEnumerable<RepositoryTemplateFile> Templates()
        => _files.Where(f => f.Problem is null && f.Kind.Length > 0 && f.Version.Length > 0);
}
