using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;

namespace SqlFlow.Delivery.Catalog;

/// <summary>The folder on this host that holds a synced repository's files, or why there is none.</summary>
/// <param name="Path">The folder, the repository's top; null when none is to be had.</param>
/// <param name="Problem">Why no folder is to be had: a clause that reads after the name of what was to be read from it.</param>
public sealed record RepositoryFolder(string? Path, string? Problem)
{
    /// <summary>The repository's files are in <paramref name="path"/>.</summary>
    public static RepositoryFolder At(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new RepositoryFolder(System.IO.Path.GetFullPath(path), null);
    }

    /// <summary>No folder holds the repository's files here, and why.</summary>
    public static RepositoryFolder Unavailable(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        return new RepositoryFolder(null, problem);
    }
}

/// <summary>
/// Where a synced repository's files are read on this host (osdu/docs/reference/concepts/templates.md, Where templates are
/// read): what reads a repository's template outside a run of its own (a page about a document, the Templates page, an
/// assertion run reading a mapping by its reference) reads the file itself, never a copy of it, from the folder the
/// repository's last sync read.
/// </summary>
public interface IRepositoryFolders
{
    /// <summary>The folder holding repository <paramref name="repoId"/> as its last sync read it.</summary>
    Task<RepositoryFolder> FolderOfAsync(Guid repoId, CancellationToken ct = default);
}

/// <summary>The folders of repositories known without a catalog: none, or one a sync is reading.</summary>
public static class RepositoryFolders
{
    /// <summary>A host that knows no repository's folder, as one with no catalog.</summary>
    public static IRepositoryFolders None { get; } = new NoFolders();

    /// <summary>
    /// Repository <paramref name="repoId"/> in <paramref name="root"/>, the folder a sync is reading, and any other through
    /// <paramref name="others"/> (none when not given): a sync settles its repository from the files it is scanning.
    /// </summary>
    public static IRepositoryFolders Of(Guid repoId, string root, IRepositoryFolders? others = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return new OneFolder(repoId, RepositoryFolder.At(root), others ?? None);
    }

    private sealed class NoFolders : IRepositoryFolders
    {
        public Task<RepositoryFolder> FolderOfAsync(Guid repoId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(RepositoryFolder.Unavailable(
                $"this host has no catalog to find where repository {repoId:D} was synced, so its files cannot be read here"));
        }
    }

    private sealed class OneFolder(Guid repoId, RepositoryFolder folder, IRepositoryFolders others) : IRepositoryFolders
    {
        public Task<RepositoryFolder> FolderOfAsync(Guid id, CancellationToken ct = default)
            => id == repoId ? Task.FromResult(folder) : others.FolderOfAsync(id, ct);
    }
}

/// <summary>What the catalog says of where a repository was synced from and into.</summary>
/// <param name="Name">The repository's name.</param>
/// <param name="RootPath">The folder its last sync read, on the host that synced it.</param>
/// <param name="LocalPath">The folder a local-path source is read from live; null for a git source.</param>
/// <param name="RemoteUrl">The git remote of its source; null when it has none.</param>
/// <param name="SyncedSha">The commit its last sync pulled; null when none is recorded.</param>
/// <param name="CredentialReference">The reference to the git credential of its source.</param>
/// <param name="CredentialUsername">The git username paired with that credential.</param>
public sealed record RepositorySync(
    string Name, string? RootPath, string? LocalPath, string? RemoteUrl, string? SyncedSha, string? CredentialReference, string? CredentialUsername)
{
    /// <summary>What <paramref name="catalog"/> says of repository <paramref name="repoId"/>; null when it holds none.</summary>
    public static async Task<RepositorySync?> ReadAsync(CatalogDbContext catalog, Guid repoId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var repo = await catalog.Repos.AsNoTracking()
            .Where(r => r.Id == repoId)
            .Select(r => new { r.Name, r.RootPath, r.RemoteUrl })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (repo is null)
        {
            return null;
        }

        // A repository synced by a registered source is that source's (matched by name, as the managed sync names it); one
        // synced by the command line has no source and is read where it was synced.
        var source = await catalog.RepoSources.AsNoTracking()
            .Where(s => s.Name == repo.Name)
            .Select(s => new { s.LocalPath, s.RemoteUrl, s.LastSyncedSha, s.CredentialReference, s.CredentialUsername })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return new RepositorySync(
            repo.Name,
            repo.RootPath,
            source?.LocalPath,
            source?.RemoteUrl ?? repo.RemoteUrl,
            source?.LastSyncedSha,
            source?.CredentialReference,
            source?.CredentialUsername);
    }
}

/// <summary>
/// The folders of synced repositories as the catalog records them. A local-path source is read where it lives; any other
/// repository is read in the folder its last sync read, when this host sees it (the control plane that synced it, or a
/// node on the same machine), and otherwise its synced commit is checked out from its remote into the cache a run's
/// commit is checked out into, with its source's credential, as a node does for a run pinned to a commit.
/// </summary>
public sealed class CatalogRepositoryFolders : IRepositoryFolders
{
    private readonly Func<Guid, CancellationToken, Task<RepositorySync?>> _read;
    private readonly ISecretResolver _secrets;
    private readonly GitMaterializer _checkouts;

    /// <param name="read">What the catalog says of a repository; it throws when the catalog cannot be read.</param>
    /// <param name="secrets">Resolves a source's git credential reference.</param>
    /// <param name="checkouts">Where a synced commit is checked out; the cache runs use by default.</param>
    public CatalogRepositoryFolders(Func<Guid, CancellationToken, Task<RepositorySync?>> read, ISecretResolver secrets, GitMaterializer? checkouts = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(secrets);
        _read = read;
        _secrets = secrets;
        _checkouts = checkouts ?? new GitMaterializer();
    }

    /// <summary>The reference a node and the command line name their catalog by, as SQLFlow's own commands do.</summary>
    public const string CatalogReference = "${env:SQLFLOW_CATALOG_DB}";

    /// <summary>
    /// The folders as the catalog <see cref="CatalogReference"/> names records them, opened for each lookup: what a node and
    /// the command line read, where no catalog context is registered. A host that names no catalog finds no folder, and says so.
    /// </summary>
    public static CatalogRepositoryFolders FromEnvironment(ISecretResolver secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        return new CatalogRepositoryFolders(
            async (repoId, ct) =>
            {
                var connection = await secrets.ResolveAsync(CatalogReference, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(connection))
                {
                    throw new DeliveryException($"this host names no catalog ({CatalogReference} is empty)");
                }

                await using var catalog = CatalogDatabase.Create(connection);
                return await RepositorySync.ReadAsync(catalog, repoId, ct).ConfigureAwait(false);
            },
            secrets);
    }

    public async Task<RepositoryFolder> FolderOfAsync(Guid repoId, CancellationToken ct = default)
    {
        RepositorySync? sync;
        try
        {
            sync = await _read(repoId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RepositoryFolder.Unavailable(
                $"the catalog could not be read to find where repository {repoId:D} was synced: {SecretHygiene.RedactedMessage(ex)}");
        }

        if (sync is null)
        {
            return RepositoryFolder.Unavailable($"the catalog holds no repository {repoId:D}; it may have been removed");
        }

        if (!string.IsNullOrWhiteSpace(sync.LocalPath))
        {
            return Directory.Exists(sync.LocalPath)
                ? RepositoryFolder.At(sync.LocalPath)
                : RepositoryFolder.Unavailable(
                    $"repository '{sync.Name}' is read live from {sync.LocalPath}, which this host does not see; a local-path repository is read only on the machine that holds it");
        }

        if (!string.IsNullOrWhiteSpace(sync.RootPath) && Directory.Exists(sync.RootPath))
        {
            return RepositoryFolder.At(sync.RootPath);
        }

        if (string.IsNullOrWhiteSpace(sync.RemoteUrl) || string.IsNullOrWhiteSpace(sync.SyncedSha))
        {
            return RepositoryFolder.Unavailable(string.IsNullOrWhiteSpace(sync.RootPath)
                ? $"repository '{sync.Name}' has no synced folder recorded; sync it again"
                : $"repository '{sync.Name}' was synced from {sync.RootPath}, which this host does not see, and it records no commit to check out from a remote; sync it again on this host");
        }

        try
        {
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(_secrets, sync.CredentialReference, sync.CredentialUsername, sync.RemoteUrl, GitAccess.Read, ct)
                .ConfigureAwait(false);

            // A clone is slow and blocking, so it is kept off the caller's thread; a commit already checked out is reused.
            var folder = await Task.Run(() => _checkouts.Materialize(sync.RemoteUrl, sync.SyncedSha, credentials, ct), ct).ConfigureAwait(false);
            return RepositoryFolder.At(folder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RepositoryFolder.Unavailable(
                $"repository '{sync.Name}' synced commit {sync.SyncedSha}, which could not be checked out here: {SecretHygiene.RedactedMessage(ex)}");
        }
    }
}
