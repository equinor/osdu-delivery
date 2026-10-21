using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Catalog;

/// <summary>
/// The template folders of one repository as its last sync found them (<c>[osdu].[RepositoryTemplate]</c>): what the control
/// plane's pages settle a repository document's templates from, by the rule a run settles them by from the files
/// (<see cref="TemplateChain"/>, osdu/docs/reference/concepts/templates.md, Template folders), so a page and a run agree on
/// which version a document reads. Documents are named by their path in the repository. A template's schema is read from
/// the copy the sync keeps of the file on its row, never from the library of saved templates, which the repositories do
/// not touch; the sync itself settles the documents it reads from the scan it has just made, and reads a schema from the
/// file.
/// </summary>
public sealed class SyncedTemplates : ITemplateSource
{
    /// <summary>How many folders up a document's path is walked: past any repository's depth.</summary>
    private const int MaxAscent = 64;

    private readonly Dictionary<string, TemplateFolder> _folders;
    private readonly IReadOnlyList<TemplateFolderFile> _all;
    private readonly Func<TemplateFolderFile, CancellationToken, Task<SchemaSnapshotRead>> _load;

    /// <summary>The copies already read, by file, for folders the catalog recorded; null for a scan, which reads each file afresh.</summary>
    private readonly ConcurrentDictionary<string, SchemaSnapshotRead>? _read;

    /// <summary>A template's schema as read for a file, or why it could not be.</summary>
    private sealed record SchemaSnapshotRead(SchemaSnapshot? Schema, string? Problem);

    private SyncedTemplates(IEnumerable<TemplateFolder> folders, Func<TemplateFolderFile, CancellationToken, Task<SchemaSnapshotRead>> load, bool remember)
    {
        // Folders are keyed by their path in the repository, which is what a document's path is walked by.
        _folders = folders.ToDictionary(f => f.Shown, StringComparer.Ordinal);
        _all = [.. _folders.Values.OrderBy(f => f.Shown, StringComparer.Ordinal).SelectMany(f => f.Files)];
        _load = load;
        _read = remember ? new ConcurrentDictionary<string, SchemaSnapshotRead>(StringComparer.Ordinal) : null;
    }

    /// <summary>
    /// The folders as a scan of the repository on disk found them (<see cref="TemplateFolders.Scan"/>), each named by its
    /// path in the repository: what a sync settles the repository's documents with before it records anything, each
    /// template read from its file.
    /// </summary>
    public static SyncedTemplates Of(TemplateFolderScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        // A file is read again each time, so one that changed since the scan is not taken for what it held then.
        return new SyncedTemplates(scan.Folders, (file, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return Task.FromResult(new SchemaSnapshotRead(TemplateFolders.Load(file), null));
            }
            catch (DeliveryException ex)
            {
                return Task.FromResult(new SchemaSnapshotRead(null, ex.Message));
            }
        }, remember: false);
    }

    /// <summary>
    /// The folders the catalog recorded for a repository, a template's schema read through <paramref name="copies"/>, the
    /// copy the sync keeps of each file (by its path in the repository); with none to read, the folders settle a document's
    /// pins and read no schema.
    /// </summary>
    public static SyncedTemplates Of(IEnumerable<SyncedTemplateFile> files, Func<string, CancellationToken, Task<RecordedTemplateCopy?>>? copies)
    {
        ArgumentNullException.ThrowIfNull(files);
        async Task<SchemaSnapshotRead> LoadAsync(TemplateFolderFile file, CancellationToken ct)
        {
            if (copies is null)
            {
                return new SchemaSnapshotRead(null, $"{file.Shown} holds template {file.Reference}, and the copy the sync keeps of it is not read here.");
            }

            if (await copies(file.Path, ct).ConfigureAwait(false) is not { } copy)
            {
                return new SchemaSnapshotRead(null, $"{file.Shown} holds template {file.Reference}, and the copy the sync keeps of it is missing; sync the repository again.");
            }

            SchemaSnapshot schema;
            try
            {
                schema = SchemaSnapshot.Parse(file.Kind!, copy.SchemaJson, new DateTimeOffset(DateTime.SpecifyKind(copy.SeenUtc, DateTimeKind.Utc)));
            }
            catch (Exception ex) when (ex is DeliveryException or System.Text.Json.JsonException)
            {
                return new SchemaSnapshotRead(null, $"{file.Shown} holds template {file.Reference}, and the copy the sync keeps of it does not read as a schema; sync the repository again.");
            }

            return string.Equals(schema.Version, file.Version, StringComparison.OrdinalIgnoreCase)
                ? new SchemaSnapshotRead(schema, null)
                : new SchemaSnapshotRead(null, $"{file.Shown} holds template {file.Reference}, and the copy the sync keeps of it reads as version {schema.Version}; sync the repository again.");
        }

        return new SyncedTemplates(
            files.GroupBy(f => f.Folder, StringComparer.Ordinal)
                .Select(g => new TemplateFolder(g.Key, g.Key, [.. g
                    .OrderBy(f => f.Path, StringComparer.Ordinal)
                    .Select(f => new TemplateFolderFile(
                        f.Path, f.Path,
                        string.IsNullOrEmpty(f.Kind) ? null : f.Kind,
                        string.IsNullOrEmpty(f.Version) ? null : f.Version,
                        f.Problem ?? (string.IsNullOrEmpty(f.Kind) || string.IsNullOrEmpty(f.Version) ? "the sync recorded no template for it" : null)))])),
            LoadAsync,
            remember: true);
    }

    /// <summary>
    /// The folders the catalog recorded for repository <paramref name="repoId"/>, read from <paramref name="db"/>, which
    /// reads the copy of a template only once a document settles on it: <paramref name="db"/> has to outlive the reads.
    /// </summary>
    public static async Task<SyncedTemplates> ReadAsync(Data.OsduDbContext db, Guid repoId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var files = await db.DeliveryRepositoryTemplates.AsNoTracking()
            .Where(t => t.RepoId == repoId)
            .OrderBy(t => t.Path)
            .Take(OsduLedger.MaxRepositoryTemplates)
            .Select(t => new SyncedTemplateFile(t.Path, t.Folder, t.Kind, t.Version, t.Problem))
            .ToListAsync(ct).ConfigureAwait(false);
        return Of(files, (path, read) => CopyAsync(db, repoId, path, read));
    }

    /// <summary>The folders the catalog recorded for repository <paramref name="repoId"/>, read through <paramref name="ledger"/>.</summary>
    public static async Task<SyncedTemplates> ReadAsync(ILedger ledger, Guid repoId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return Of(
            await ledger.RepositoryTemplatesAsync(repoId, ct).ConfigureAwait(false),
            (path, read) => ledger.RepositoryTemplateCopyAsync(repoId, path, read));
    }

    /// <summary>The copy the last sync of repository <paramref name="repoId"/> kept of the template its file at <paramref name="path"/> holds.</summary>
    internal static async Task<RecordedTemplateCopy?> CopyAsync(Data.OsduDbContext db, Guid repoId, string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await db.DeliveryRepositoryTemplates.AsNoTracking()
            .Where(t => t.RepoId == repoId && t.Path == path && t.SchemaJson != null)
            .Select(t => new { t.SchemaJson, t.LastSeenUtc })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : new RecordedTemplateCopy(row.SchemaJson!, row.LastSeenUtc);
    }

    /// <summary>Every file of every folder, in folder order and then path order.</summary>
    public IReadOnlyList<TemplateFolderFile> Files => _all;

    /// <summary>
    /// The template folders the document at <paramref name="document"/>, its path in the repository, reads: the folder
    /// named <c>templates</c> beside it and in every folder above it, nearest first.
    /// </summary>
    public TemplateChain ChainFor(string document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        var path = Normalized(document);
        var directory = Parent(path);
        var looked = new List<string>();
        var folders = new List<TemplateFolder>();
        for (var i = 0; i < MaxAscent; i++)
        {
            var candidate = directory.Length == 0 ? TemplateFolders.FolderName : $"{directory}/{TemplateFolders.FolderName}";
            looked.Add(candidate);
            if (_folders.TryGetValue(candidate, out var folder))
            {
                folders.Add(folder);
            }

            if (directory.Length == 0)
            {
                break;
            }

            directory = Parent(directory);
        }

        return new TemplateChain(path, looked, folders, () => _all);
    }

    /// <summary>A pin settled from the template folders on the path of the document at <paramref name="document"/>, its version checked.</summary>
    public ITemplatePins For(string document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        return new DocumentPins(this, document);
    }

    public async Task<TemplateResolution> ResolveAsync(string? document, string kind, string? version, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (string.IsNullOrWhiteSpace(document))
        {
            return TemplateResolution.Missing(
                $"template {kind}{(version is null ? string.Empty : $" version {version}")} is read from the template folders on a document's path, and no document was named.");
        }

        var pick = ChainFor(document).Pick(kind, version);
        if (!pick.Found)
        {
            return TemplateResolution.Missing(pick.Problem!);
        }

        // A recorded file's copy is read once per instance, which lives as long as one page's work.
        SchemaSnapshotRead read;
        if (_read is null)
        {
            read = await _load(pick.File!, ct).ConfigureAwait(false);
        }
        else if (!_read.TryGetValue(pick.File!.Path, out read!))
        {
            read = await _load(pick.File!, ct).ConfigureAwait(false);
            _read[pick.File!.Path] = read;
        }

        if (read.Schema is not { } schema)
        {
            return TemplateResolution.Missing(read.Problem!);
        }

        return TemplateResolution.Of(new TemplateInfo(schema.Kind, schema.Version, schema.CapturedUtc.UtcDateTime, FolderTemplates.FolderCapturedBy, pick.File!.Shown), schema);
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
        foreach (var file in ChainFor(document).Templates)
        {
            if (seen.Add(file.Reference))
            {
                templates.Add(new TemplateInfo(file.Kind!, file.Version!, DateTime.MinValue, FolderTemplates.FolderCapturedBy, file.Shown));
            }
        }

        return Task.FromResult<IReadOnlyList<TemplateInfo>>(templates);
    }

    /// <summary>A path in the repository as the catalog names it: forward slashes, no leading <c>./</c> or slash.</summary>
    private static string Normalized(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private sealed class DocumentPins(SyncedTemplates templates, string document) : ITemplatePins
    {
        public TemplateReference Pin(string kind, string? version, string where)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(kind);
            var pick = templates.ChainFor(document).Pick(kind, version);
            return pick.Found
                ? pick.File!.Reference
                : throw new FlowValidationException($"{where}: {pick.Problem}");
        }
    }
}
