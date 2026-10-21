using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ordering;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The repository sync over template folders (osdu/docs/reference/concepts/templates.md, Template folders): it records every
/// file of every template folder, which template it holds and nothing of it, settles each mapping from the folders on its
/// path, and what the pages read of a repository settles a document as a run settles it, each template read from its file in
/// the folder the repository was synced into.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class TemplateFolderSyncTests : IDisposable
{
    private const string Thing = TestSchema.Kind;
    private readonly OsduTestDatabase _db = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly Guid _repo = Guid.NewGuid();

    /// <summary>Unique to this test run, so each template version a test makes is one no other test made.</summary>
    private readonly string _run = Guid.NewGuid().ToString("N")[..12];

    public TemplateFolderSyncTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        _db.Dispose();
    }

    /// <summary>The test template as a folder keeps it, in a version of its own named <paramref name="name"/>.</summary>
    private string ThingFile(string name) => TemplateFolderFixtures.ThingFile($"{_run}-{name}");

    private static string VersionOf(string json) => TemplateFolderFixtures.VersionOf(Thing, json);

    private string Write(string relative, string text, string? root = null)
    {
        var path = Path.Combine(root ?? _root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private void Delete(string relative, string? root = null) => File.Delete(Path.Combine(root ?? _root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Named(string name, string? version = null)
        => TemplateFolderFixtures.KindAloneMapping(version).Replace("name: Thing\n", $"name: {name}\n", StringComparison.Ordinal);

    private async Task<(SqlFlow.Catalog.CatalogSyncExtensionResult Result, List<string> Warnings)> SyncAsync(Guid? repo = null, string? root = null)
    {
        var warnings = new List<string>();
        await using var db = _db.CreateDbContext();
        var result = await new DeliveryCatalogSync(new DeliveryDocumentLoader())
            .ReconcileAsync(db, repo ?? _repo, root ?? _root, DateTime.UtcNow, warnings, CancellationToken.None);
        return (result, warnings);
    }

    private async Task<List<DeliveryRepositoryTemplate>> FilesAsync(Guid? repo = null)
    {
        await using var db = _db.CreateDbContext();
        var wanted = repo ?? _repo;
        return await db.DeliveryRepositoryTemplates.AsNoTracking().Where(t => t.RepoId == wanted).OrderBy(t => t.Path).ToListAsync();
    }

    private async Task<DeliveryMapping> MappingAsync(string reference, Guid? repo = null)
    {
        await using var db = _db.CreateDbContext();
        var wanted = repo ?? _repo;
        return await db.DeliveryMappings.AsNoTracking().SingleAsync(m => m.RepoId == wanted && m.Reference == reference);
    }

    /// <summary>The repository synced from <see cref="_root"/> read where a sync left it, and any other from <paramref name="others"/>.</summary>
    private IRepositoryFolders Folders(IRepositoryFolders? others = null) => RepositoryFolders.Of(_repo, _root, others);

    [Fact]
    public async Task A_sync_records_which_template_every_file_of_the_template_folders_holds_and_none_of_its_content()
    {
        var a = ThingFile("a");
        var b = ThingFile("b");
        Write("templates/thing.json", a);
        Write("templates/copy/thing.json", a);
        Write("templates/broken.json", "{ not json");
        Write("p/templates/thing.json", b);
        Write("q/Templates/thing.json", ThingFile("unread"));

        var (_, warnings) = await SyncAsync();
        Assert.Contains("templates/broken.json: the schema is not valid JSON", warnings.Single(w => w.StartsWith("templates/broken.json", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains($"templates/copy/thing.json, templates/thing.json hold the same template, {Thing} version {VersionOf(a)}; one file of it in a folder is enough.", warnings);
        Assert.Contains("q/Templates is named 'Templates', and a template folder is named 'templates' exactly, so no document reads the templates in it.", warnings);

        var files = await FilesAsync();
        Assert.Equal(
            [
                ("p/templates/thing.json", "p/templates", Thing, VersionOf(b), false),
                ("templates/broken.json", "templates", string.Empty, string.Empty, true),
                ("templates/copy/thing.json", "templates", Thing, VersionOf(a), false),
                ("templates/thing.json", "templates", Thing, VersionOf(a), false),
            ],
            files.Select(f => (f.Path, f.Folder, f.Kind, f.Version, f.Problem is not null)));

        // The rows are the index the Templates page lists; the content is the files', and the database keeps no copy of it.
        await using (var db = _db.CreateDbContext())
        {
            var columns = db.Model.FindEntityType(typeof(DeliveryRepositoryTemplate))!.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain("SchemaJson", columns);
            Assert.Null(db.Model.FindEntityType("SqlFlow.Delivery.Data.DeliveryTemplate"));
        }

        // A second sync of the same folders changes no row.
        var firstSeen = files.ToDictionary(f => f.Path, f => (f.FirstSeenUtc, f.Kind, f.Version));
        await SyncAsync();
        Assert.All(await FilesAsync(), f => Assert.Equal(firstSeen[f.Path], (f.FirstSeenUtc, f.Kind, f.Version)));
        await using (var db = _db.CreateDbContext())
        {
            Assert.False(await DeliveryCatalogSync.TemplateFoldersChangedAsync(db, _repo, _root, CancellationToken.None));
        }

        // A file the repository no longer holds loses its row at the next sync; one it adds gains one.
        Delete("p/templates/thing.json");
        Write("r/templates/thing.json", b);
        await SyncAsync();
        Assert.DoesNotContain(await FilesAsync(), f => f.Path == "p/templates/thing.json");
        Assert.Contains(await FilesAsync(), f => f.Path == "r/templates/thing.json" && f.Version == VersionOf(b));
    }

    [Fact]
    public async Task A_page_reads_a_template_from_its_file_where_the_sync_left_it_and_says_to_sync_again_when_the_file_changed_since()
    {
        var a = ThingFile("a");
        Write("templates/thing.json", a);
        Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping());
        await SyncAsync();

        await using var db = _db.CreateDbContext();
        var read = await (await SyncedTemplates.ReadAsync(db, Folders(), _repo, CancellationToken.None)).ResolveAsync("mappings/Thing@1.0.0.yaml", Thing, null);
        Assert.Equal((VersionOf(a), "templates/thing.json"), (read.Schema!.Version, read.Template!.File));

        // The file changes and the repository is not synced yet: the page reads the file, sees it no longer holds what the
        // sync recorded, and says so rather than reading another version than the mapping settled to.
        Write("templates/thing.json", ThingFile("changed"));
        var changed = await (await SyncedTemplates.ReadAsync(db, Folders(), _repo, CancellationToken.None)).ResolveAsync("mappings/Thing@1.0.0.yaml", Thing, null);
        Assert.False(changed.Found);
        Assert.StartsWith($"templates/thing.json held template {Thing} version {VersionOf(a)} and now holds {Thing} version ", changed.Problem, StringComparison.Ordinal);
        Assert.EndsWith("; sync the repository again, as its last sync read the file before it changed.", changed.Problem, StringComparison.Ordinal);

        // Deleted since: the same, and the next sync records what the repository holds.
        Delete("templates/thing.json");
        var gone = await (await SyncedTemplates.ReadAsync(db, Folders(), _repo, CancellationToken.None)).ResolveAsync("mappings/Thing@1.0.0.yaml", Thing, null);
        Assert.Contains("no longer reads: the file no longer exists; sync the repository again", gone.Problem, StringComparison.Ordinal);
        await SyncAsync();
        Assert.Empty(await FilesAsync());

        // A host that cannot find the folder the repository was synced into says why, and settles the document's pins all
        // the same, since they need no file.
        Write("templates/thing.json", a);
        await SyncAsync();
        var nowhere = await SyncedTemplates.ReadAsync(db, RepositoryFolders.None, _repo, CancellationToken.None);
        Assert.Equal(VersionOf(a), nowhere.For("mappings/Thing@1.0.0.yaml").Pin(Thing, null, "where").Version);
        Assert.Contains(
            $"this host has no catalog to find where repository {_repo:D} was synced",
            (await nowhere.ResolveAsync("mappings/Thing@1.0.0.yaml", Thing, null)).Problem,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mapping_is_settled_from_the_folders_on_its_path_and_follows_a_file_that_changes_while_the_document_does_not()
    {
        var a = ThingFile("a");
        var b = ThingFile("b");
        Write("templates/thing.json", a);
        Write("p/templates/thing.json", b);
        Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping());
        Write("p/mappings/Other@1.0.0.yaml", Named("Other"));

        await SyncAsync();
        var thing = await MappingAsync("Thing@1.0.0");
        Assert.Equal(("valid", Thing, VersionOf(a), "templates/thing.json"), (thing.Status, thing.Kind, thing.TemplateVersion, thing.TemplatePath));
        Assert.Contains("\"templateVersionNamed\":false", thing.SummaryJson, StringComparison.Ordinal);
        var other = await MappingAsync("Other@1.0.0");
        Assert.Equal((VersionOf(b), "p/templates/thing.json"), (other.TemplateVersion, other.TemplatePath));

        // The file the mapping reads changes: the row follows it although the document is the same.
        var c = ThingFile("c");
        Write("templates/thing.json", c);
        var (changed, _) = await SyncAsync();
        Assert.Equal(1, changed.Updated);
        thing = await MappingAsync("Thing@1.0.0");
        Assert.Equal((VersionOf(c), "templates/thing.json"), (thing.TemplateVersion, thing.TemplatePath));
        Assert.Equal(VersionOf(c), (await FilesAsync()).Single(f => f.Path == "templates/thing.json").Version);

        // The nearer folder no longer holds the kind: the mapping reads the folder above.
        Delete("p/templates/thing.json");
        await SyncAsync();
        other = await MappingAsync("Other@1.0.0");
        Assert.Equal((VersionOf(c), "templates/thing.json"), (other.TemplateVersion, other.TemplatePath));

        // Two versions of the kind in the folder they read: neither mapping settles, and the sync says why.
        Write("templates/alternative.json", ThingFile("d"));
        var (_, warnings) = await SyncAsync();
        Assert.Contains(warnings, w => w.StartsWith($"templates holds 2 versions of {Thing}", StringComparison.Ordinal));
        thing = await MappingAsync("Thing@1.0.0");
        Assert.Equal(("invalid", string.Empty, string.Empty, Thing), (thing.Status, thing.TemplateVersion, thing.TemplatePath, thing.Kind));
        Assert.Contains("the nearest template folder on the path of mappings/Thing@1.0.0.yaml holding", thing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mapping_whose_version_no_folder_on_its_path_holds_does_not_load_and_keeps_its_pin()
    {
        var a = ThingFile("a");
        var b = ThingFile("b");
        Write("templates/thing.json", a);
        Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping(VersionOf(b)));

        await SyncAsync();
        var row = await MappingAsync("Thing@1.0.0");
        Assert.Equal(("invalid", VersionOf(b), string.Empty), (row.Status, row.TemplateVersion, row.TemplatePath));
        Assert.StartsWith(
            $"mappings/Thing@1.0.0.yaml: template: no template folder on the path of mappings/Thing@1.0.0.yaml holds template {Thing} version {VersionOf(b)}. "
            + $"On its path templates/thing.json holds version {VersionOf(a)}. It reads templates.",
            row.Message,
            StringComparison.Ordinal);

        // Held by a folder off the mapping's path: the message says where.
        Write("other/templates/thing.json", b);
        await SyncAsync();
        Assert.Contains(
            $"The repository holds version {VersionOf(b)} of {Thing} at other/templates/thing.json, which is not on its path.",
            (await MappingAsync("Thing@1.0.0")).Message,
            StringComparison.Ordinal);

        // Put where the mapping reads it, it loads.
        Write("mappings/templates/thing.json", b);
        await SyncAsync();
        row = await MappingAsync("Thing@1.0.0");
        Assert.Equal(("valid", VersionOf(b), "mappings/templates/thing.json"), (row.Status, row.TemplateVersion, row.TemplatePath));
    }

    [Fact]
    public async Task Two_repositories_holding_one_template_each_read_it_from_their_own_folder()
    {
        var other = Samples.NewTempDirectory();
        var otherRepo = Guid.NewGuid();
        try
        {
            var a = ThingFile("shared");
            Write("templates/thing.json", a);
            Write("templates/thing.json", a, other);
            Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping());
            await SyncAsync();
            await SyncAsync(otherRepo, other);
            Assert.Equal(VersionOf(a), Assert.Single(await FilesAsync()).Version);
            Assert.Equal(VersionOf(a), Assert.Single(await FilesAsync(otherRepo)).Version);

            await using (var db = _db.CreateDbContext())
            {
                var folders = Folders(RepositoryFolders.Of(otherRepo, other));
                var read = await (await SyncedTemplates.ReadAsync(db, folders, _repo, CancellationToken.None)).ResolveAsync("mappings/Thing@1.0.0.yaml", Thing, null);
                Assert.Equal((VersionOf(a), "templates/thing.json"), (read.Schema!.Version, read.Template!.File));

                // Every repository's templates together, as the Templates page and the explorer read them: one file each,
                // each read from its own repository's folder, so one repository losing its file leaves the other's readable.
                var every = await RepositoryTemplates.ReadAsync(db, folders, CancellationToken.None);
                Assert.Equal(2, every.Files.Count(f => f.Kind == Thing && f.Version == VersionOf(a)));
                Delete("templates/thing.json", other);
                Assert.True((await every.LoadAsync(_repo, "templates/thing.json")).Found);
                Assert.Contains("the file no longer exists", (await every.LoadAsync(otherRepo, "templates/thing.json")).Problem, StringComparison.Ordinal);
            }

            // One repository dropping the file at its sync leaves the other's row where it is.
            Delete("templates/thing.json");
            await SyncAsync();
            Assert.Empty(await FilesAsync());
            Assert.Single(await FilesAsync(otherRepo));
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task Every_repositorys_templates_settle_a_kind_by_the_repository_synced_last_and_a_version_exactly()
    {
        var other = Samples.NewTempDirectory();
        var otherRepo = Guid.NewGuid();
        try
        {
            var mine = ThingFile("mine");
            var theirs = ThingFile("theirs");
            Write("templates/thing.json", mine);
            Write("templates/thing.json", theirs, other);
            Write("templates/broken.json", "{ not json", other);
            var earlier = DateTime.UtcNow.AddMinutes(-5);
            var warnings = new List<string>();
            await using (var db = _db.CreateDbContext())
            {
                await new DeliveryCatalogSync(new DeliveryDocumentLoader()).ReconcileAsync(db, _repo, _root, earlier, warnings, CancellationToken.None);
            }

            await SyncAsync(otherRepo, other);
            await using var read = _db.CreateDbContext();
            var every = await RepositoryTemplates.ReadAsync(read, Folders(RepositoryFolders.Of(otherRepo, other)), CancellationToken.None);

            // A kind named alone reads the version the repository synced last holds; a version named reads that version.
            Assert.Equal(VersionOf(theirs), (await every.ResolveAsync(null, Thing, null)).Schema!.Version);
            Assert.Equal(VersionOf(mine), (await every.ResolveAsync(null, Thing, VersionOf(mine))).Schema!.Version);
            Assert.Equal(
                [VersionOf(theirs), VersionOf(mine)],
                (await every.ListForAsync(null)).Where(t => t.Kind == Thing).Select(t => t.Version));

            // A version no repository holds names those they do; a kind none holds says where to get it; a file that holds
            // no template is listed, and read as none.
            Assert.Contains(
                $"no synced repository holds template {Thing} version 0000000000000000 in a template folder; they hold version ",
                (await every.ResolveAsync(null, Thing, "0000000000000000")).Problem,
                StringComparison.Ordinal);
            Assert.Contains("the Templates page downloads it from the OSDU data definitions", (await every.ResolveAsync(null, "acme:wks:master-data--None:1.0.0", null)).Problem, StringComparison.Ordinal);
            Assert.StartsWith("templates/broken.json holds no template: ", (await every.LoadAsync(otherRepo, "templates/broken.json")).Problem, StringComparison.Ordinal);
            Assert.Contains("hold no file templates/missing.json", (await every.LoadAsync(otherRepo, "templates/missing.json")).Problem, StringComparison.Ordinal);

            // The repository synced last cannot be read on this host: the kind is read from the next that holds it, and
            // only when none can be read is the first one's reason the answer.
            var unseen = await RepositoryTemplates.ReadAsync(read, Folders(), CancellationToken.None);
            Assert.Equal(VersionOf(mine), (await unseen.ResolveAsync(null, Thing, null)).Schema!.Version);
            var blind = await RepositoryTemplates.ReadAsync(read, RepositoryFolders.None, CancellationToken.None);
            var none = await blind.ResolveAsync(null, Thing, null);
            Assert.False(none.Found);
            Assert.Contains($"templates/thing.json holds template {Thing} version {VersionOf(theirs)}, and this host has no catalog", none.Problem, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task The_pages_settle_a_repositorys_documents_from_what_its_sync_recorded_as_a_run_does_from_the_files()
    {
        var a = ThingFile("a");
        Write("templates/thing.json", a);
        Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping());
        await SyncAsync();

        var ledger = _db.Ledger();
        var folders = await SyncedTemplates.ReadAsync(ledger, Folders(), _repo, CancellationToken.None);
        Assert.Equal(["templates/thing.json"], (await ledger.RepositoryTemplatesAsync(_repo)).Select(f => f.Path));
        Assert.Contains(await ledger.RepositoryTemplatesAsync(), f => f.RepoId == _repo && f.Path == "templates/thing.json" && f.Version == VersionOf(a));
        var read = await folders.ResolveAsync("mappings/Thing@1.0.0.yaml", Thing, null);
        Assert.Equal((VersionOf(a), "templates/thing.json"), (read.Schema!.Version, read.Template!.File));

        await using (var db = _db.CreateDbContext())
        {
            var direct = await SyncedTemplates.ReadAsync(db, Folders(), _repo, CancellationToken.None);
            Assert.Equal(VersionOf(a), direct.For("mappings/Thing@1.0.0.yaml").Pin(Thing, null, "where").Version);
        }

        // A synced mapping that names its template's kind alone is read back settled, as a page reads it.
        var (mapping, problem) = await SyncedMappings.ByReferenceAsync(ledger, Folders(), new DeliveryDocumentLoader(), "Thing@1.0.0", CancellationToken.None);
        Assert.Null(problem);
        Assert.Equal(VersionOf(a), mapping!.Template.Version);

        // The same document in another repository whose folder holds another version is not one mapping to read.
        var other = Samples.NewTempDirectory();
        try
        {
            Write("templates/thing.json", ThingFile("elsewhere"), other);
            Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping(), other);
            await SyncAsync(Guid.NewGuid(), other);
            var (none, why) = await SyncedMappings.ByReferenceAsync(ledger, Folders(), new DeliveryDocumentLoader(), "Thing@1.0.0", CancellationToken.None);
            Assert.Null(none);
            Assert.Contains("repositories whose template folders give it different templates", why, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task The_sample_estate_with_its_mapping_naming_its_templates_kind_alone_syncs_settled_and_is_ordered()
    {
        foreach (var folder in new[] { "flows", "reference", "cache", "dictionaries", "mappings", "templates", "data" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Samples.Source, folder), "*", SearchOption.AllDirectories))
            {
                Write(Path.Combine("welldb", folder, Path.GetRelativePath(Path.Combine(Samples.Source, folder), file)).Replace('\\', '/'), File.ReadAllText(file));
            }
        }

        var mappingPath = Path.Combine(_root, "welldb", "mappings", "WellLog@1.4.0.yaml");
        var yaml = File.ReadAllText(mappingPath).ReplaceLineEndings("\n");
        Assert.Contains("  version: 26a3c3441882db4f\n", yaml, StringComparison.Ordinal);
        File.WriteAllText(mappingPath, yaml.Replace("  version: 26a3c3441882db4f\n", string.Empty, StringComparison.Ordinal));

        await SyncAsync();
        var row = await MappingAsync("WellLog@1.4.0");
        Assert.Equal(
            ("valid", "26a3c3441882db4f", "welldb/templates/osdu_wks_work-product-component--WellLog_1.4.0.json"),
            (row.Status, row.TemplateVersion, row.TemplatePath));

        // The OSDU order reads the synced mapping settled, and orders its delivery.
        await using var db = _db.CreateDbContext();
        var order = await OrderCatalog.ReadAsync(db, Folders(), _repo);
        Assert.DoesNotContain(order.Problems, p => p.Contains("WellLog", StringComparison.Ordinal));
        Assert.Contains(order.Deliveries, d => d.Delivery.Kind == "osdu:wks:work-product-component--WellLog:1.4.0");
    }
}
