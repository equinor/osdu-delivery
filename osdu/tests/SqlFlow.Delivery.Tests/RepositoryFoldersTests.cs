using LibGit2Sharp;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Where a synced repository's files are read on a host (osdu/docs/reference/concepts/templates.md, Where templates are
/// read): a local-path source where it lives, any other repository in the folder its last sync read when the host sees it,
/// and otherwise its synced commit checked out from its remote, as a node checks out a run's commit; and why none is to be
/// had when none is. The catalog is answered here, and the remote is a real local git repository, so nothing is reached over
/// the network.
/// </summary>
public sealed class RepositoryFoldersTests : IDisposable
{
    private static readonly Guid Repo = Guid.NewGuid();
    private readonly List<string> _folders = [];
    private readonly SecretResolver _secrets = new([new EnvSecretProvider()]);

    public void Dispose()
    {
        foreach (var folder in _folders)
        {
            Delete(folder);
        }
    }

    private string Folder()
    {
        var folder = Samples.NewTempDirectory();
        _folders.Add(folder);
        return folder;
    }

    private CatalogRepositoryFolders Folders(RepositorySync? sync, string? checkouts = null)
        => new((id, _) => Task.FromResult(id == Repo ? sync : null), _secrets, new GitMaterializer(checkouts ?? Folder()));

    [Fact]
    public async Task A_local_path_source_is_read_where_it_lives_and_only_where_the_host_sees_it()
    {
        var live = Folder();
        var found = await Folders(new RepositorySync("estate", RootPath: Folder(), LocalPath: live, RemoteUrl: null, SyncedSha: null, null, null)).FolderOfAsync(Repo);
        Assert.Equal((Path.GetFullPath(live), (string?)null), (found.Path, found.Problem));

        var elsewhere = Path.Combine(live, "not-here");
        var missing = await Folders(new RepositorySync("estate", RootPath: null, LocalPath: elsewhere, null, null, null, null)).FolderOfAsync(Repo);
        Assert.Null(missing.Path);
        Assert.Equal(
            $"repository 'estate' is read live from {elsewhere}, which this host does not see; a local-path repository is read only on the machine that holds it",
            missing.Problem);
    }

    [Fact]
    public async Task A_repository_is_read_in_the_folder_its_last_sync_read_when_the_host_sees_it()
    {
        var synced = Folder();
        var found = await Folders(new RepositorySync("estate", synced, null, "https://example.invalid/estate.git", new string('a', 40), null, null)).FolderOfAsync(Repo);
        Assert.Equal(Path.GetFullPath(synced), found.Path);
    }

    [Fact]
    public async Task A_folder_the_host_does_not_see_and_no_commit_to_check_out_says_to_sync_again()
    {
        var gone = Path.Combine(Folder(), "gone");
        var unseen = await Folders(new RepositorySync("estate", gone, null, RemoteUrl: null, SyncedSha: null, null, null)).FolderOfAsync(Repo);
        Assert.Equal(
            $"repository 'estate' was synced from {gone}, which this host does not see, and it records no commit to check out from a remote; sync it again on this host",
            unseen.Problem);

        var never = await Folders(new RepositorySync("estate", RootPath: null, null, null, null, null, null)).FolderOfAsync(Repo);
        Assert.Equal("repository 'estate' has no synced folder recorded; sync it again", never.Problem);

        var removed = await Folders(sync: null).FolderOfAsync(Repo);
        Assert.Equal($"the catalog holds no repository {Repo:D}; it may have been removed", removed.Problem);
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_read_is_said_without_what_it_quoted()
    {
        var failing = new CatalogRepositoryFolders(
            (_, _) => throw new InvalidOperationException("Server=sql.example.test;Password=hunter2;the login failed"),
            _secrets);
        var answer = await failing.FolderOfAsync(Repo);
        Assert.Null(answer.Path);
        Assert.StartsWith($"the catalog could not be read to find where repository {Repo:D} was synced: ", answer.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", answer.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_folder_the_host_does_not_see_is_checked_out_at_the_synced_commit_from_the_remote()
    {
        var remote = Folder();
        Repository.Init(remote);
        string first;
        using (var git = new Repository(remote))
        {
            first = Commit(git, remote, "templates/thing.json", "first");
            Commit(git, remote, "templates/thing.json", "second");
        }

        // The last sync pulled the first commit, on a host whose folder this one does not see: the commit is checked out
        // here, and holds the file as that commit holds it, whatever the remote's tip holds since.
        var checkouts = Folder();
        var gone = Path.Combine(Folder(), "on-another-host");
        var found = await Folders(new RepositorySync("estate", gone, null, remote, first, null, null), checkouts).FolderOfAsync(Repo);
        Assert.NotNull(found.Path);
        Assert.StartsWith(Path.GetFullPath(checkouts), found.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("first", File.ReadAllText(Path.Combine(found.Path!, "templates", "thing.json")));

        // A commit the remote does not hold is said, with the commit, and nothing is read.
        var unknown = new string('0', 40);
        var absent = await Folders(new RepositorySync("estate", gone, null, remote, unknown, null, null), checkouts).FolderOfAsync(Repo);
        Assert.Null(absent.Path);
        Assert.StartsWith($"repository 'estate' synced commit {unknown}, which could not be checked out here: ", absent.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sync_reads_its_own_repository_where_it_scans_it_and_a_host_with_no_catalog_finds_none()
    {
        var scanned = Folder();
        var other = Guid.NewGuid();
        var folders = RepositoryFolders.Of(Repo, scanned);
        Assert.Equal(Path.GetFullPath(scanned), (await folders.FolderOfAsync(Repo)).Path);
        Assert.Equal(
            $"this host has no catalog to find where repository {other:D} was synced, so its files cannot be read here",
            (await folders.FolderOfAsync(other)).Problem);

        var both = RepositoryFolders.Of(Repo, scanned, RepositoryFolders.Of(other, Folder()));
        Assert.NotNull((await both.FolderOfAsync(other)).Path);
    }

    private static string Commit(Repository git, string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        Commands.Stage(git, "*");
        var signature = new Signature("tests", "tests@example.com", DateTimeOffset.UnixEpoch.AddDays(1));
        return git.Commit(content, signature, signature).Sha;
    }

    /// <summary>Deletes a folder a git checkout was made in, whose object files git marks read-only.</summary>
    private static void Delete(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(folder, recursive: true);
    }
}
