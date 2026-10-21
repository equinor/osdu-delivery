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
/// file of every template folder, saves every template they hold in the library once, settles each mapping from the folders
/// on its path, and what the pages read of a repository settles a document as a run settles it from the files.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class TemplateFolderSyncTests : IDisposable
{
    private const string Thing = TestSchema.Kind;
    private readonly OsduTestDatabase _db = new();
    private readonly string _root = Samples.NewTempDirectory();
    private readonly Guid _repo = Guid.NewGuid();

    /// <summary>Unique to this test run, so each template version a test makes is one no other test saved.</summary>
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

    private async Task<List<DeliveryTemplate>> SavedAsync(string version)
    {
        await using var db = _db.CreateDbContext();
        return await db.DeliveryTemplates.AsNoTracking().Where(t => t.Kind == Thing && t.Version == version).ToListAsync();
    }

    [Fact]
    public async Task A_sync_records_every_file_of_the_template_folders_with_a_copy_of_its_template_and_leaves_the_library_alone()
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

        // Each file that holds a template keeps a copy of it, which reads back as the version it holds; one that holds none
        // keeps none. The library is not the repository's: nothing is saved there.
        Assert.All(files.Where(f => f.Problem is null), f => Assert.Equal(f.Version, VersionOf(f.SchemaJson!)));
        Assert.Null(files.Single(f => f.Path == "templates/broken.json").SchemaJson);
        Assert.Empty(await SavedAsync(VersionOf(a)));
        Assert.Empty(await SavedAsync(VersionOf(b)));

        // A second sync of the same folders changes no row.
        var firstSeen = files.ToDictionary(f => f.Path, f => (f.FirstSeenUtc, f.SchemaJson));
        await SyncAsync();
        Assert.All(await FilesAsync(), f => Assert.Equal(firstSeen[f.Path], (f.FirstSeenUtc, f.SchemaJson)));
        await using (var db = _db.CreateDbContext())
        {
            Assert.False(await DeliveryCatalogSync.TemplateFoldersChangedAsync(db, _repo, _root, CancellationToken.None));
        }

        // A row without its copy (recorded before copies were kept) gets it at the next sync.
        await using (var db = _db.CreateDbContext())
        {
            await db.DeliveryRepositoryTemplates.Where(t => t.RepoId == _repo).ExecuteUpdateAsync(set => set.SetProperty(t => t.SchemaJson, (string?)null));
        }

        await SyncAsync();
        Assert.All((await FilesAsync()).Where(f => f.Problem is null), f => Assert.Equal(f.Version, VersionOf(f.SchemaJson!)));

        // A file the repository no longer holds loses its row, and its copy with it.
        Delete("p/templates/thing.json");
        await SyncAsync();
        Assert.DoesNotContain(await FilesAsync(), f => f.Path == "p/templates/thing.json");
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
        Assert.Equal(VersionOf(c), VersionOf((await FilesAsync()).Single(f => f.Path == "templates/thing.json").SchemaJson!));

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
    public async Task A_mapping_whose_version_no_folder_on_its_path_holds_does_not_load_keeps_its_pin_and_the_library_does_not_settle_it()
    {
        var a = ThingFile("a");
        var b = ThingFile("b");
        Write("templates/thing.json", a);
        Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping(VersionOf(b)));
        await _db.Templates().SaveAsync(SqlFlow.Delivery.Snapshots.SchemaSnapshot.Parse(Thing, b, DateTimeOffset.UnixEpoch), "tests", "tests");

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
    public async Task Two_repositories_holding_one_template_each_keep_their_own_copy_and_a_saved_version_of_it_is_deleted_freely()
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
            Assert.Equal(VersionOf(a), VersionOf(Assert.Single(await FilesAsync()).SchemaJson!));
            Assert.Equal(VersionOf(a), VersionOf(Assert.Single(await FilesAsync(otherRepo)).SchemaJson!));
            Assert.Empty(await SavedAsync(VersionOf(a)));

            // Someone saves the same schema in the library and deletes it again: the repositories never read it, so nothing
            // refuses the delete and the mapping still settles from its repository's copy.
            var store = _db.Templates();
            var reference = new TemplateReference(Thing, VersionOf(a));
            await store.SaveAsync(SqlFlow.Delivery.Snapshots.SchemaSnapshot.Parse(Thing, a, DateTimeOffset.UnixEpoch), "tests", "tests");
            await store.DeleteAsync(reference);
            Assert.Empty(await SavedAsync(VersionOf(a)));
            await using (var db = _db.CreateDbContext())
            {
                var folders = await SyncedTemplates.ReadAsync(db, _repo, CancellationToken.None);
                var read = await folders.ResolveAsync("mappings/Thing@1.0.0.yaml", Thing, null);
                Assert.Equal((VersionOf(a), "templates/thing.json"), (read.Schema!.Version, read.Template!.Origin));
            }

            // One repository dropping the file leaves the other's copy where it is.
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
    public async Task The_pages_settle_a_repositorys_documents_from_what_its_sync_recorded_as_a_run_does_from_the_files()
    {
        var a = ThingFile("a");
        Write("templates/thing.json", a);
        Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping());
        await SyncAsync();

        var ledger = _db.Ledger();
        var folders = await SyncedTemplates.ReadAsync(ledger, _repo, CancellationToken.None);
        Assert.Equal(["templates/thing.json"], (await ledger.RepositoryTemplatesAsync(_repo)).Select(f => f.Path));
        var read = await folders.ResolveAsync("mappings/Thing@1.0.0.yaml", Thing, null);
        Assert.Equal((VersionOf(a), "templates/thing.json"), (read.Schema!.Version, read.Template!.Origin));

        await using (var db = _db.CreateDbContext())
        {
            var direct = await SyncedTemplates.ReadAsync(db, _repo, CancellationToken.None);
            Assert.Equal(VersionOf(a), direct.For("mappings/Thing@1.0.0.yaml").Pin(Thing, null, "where").Version);
        }

        // A synced mapping that names its template's kind alone is read back settled, as a page reads it.
        var (mapping, problem) = await SyncedMappings.ByReferenceAsync(ledger, new DeliveryDocumentLoader(), "Thing@1.0.0", CancellationToken.None);
        Assert.Null(problem);
        Assert.Equal(VersionOf(a), mapping!.Template.Version);

        // The same document in another repository whose folder holds another version is not one mapping to read.
        var other = Samples.NewTempDirectory();
        try
        {
            Write("templates/thing.json", ThingFile("elsewhere"), other);
            Write("mappings/Thing@1.0.0.yaml", TemplateFolderFixtures.KindAloneMapping(), other);
            await SyncAsync(Guid.NewGuid(), other);
            var (none, why) = await SyncedMappings.ByReferenceAsync(ledger, new DeliveryDocumentLoader(), "Thing@1.0.0", CancellationToken.None);
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
        var order = await OrderCatalog.ReadAsync(db, _repo);
        Assert.DoesNotContain(order.Problems, p => p.Contains("WellLog", StringComparison.Ordinal));
        Assert.Contains(order.Deliveries, d => d.Delivery.Kind == "osdu:wks:work-product-component--WellLog:1.4.0");
    }
}
