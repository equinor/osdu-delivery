using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Catalog;
using SqlFlow.Catalog.Modules;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Tests;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The sample delivery estate (<c>osdu/samples/welldb</c>) copied to a temp repository, for the API tests that need a
/// real flow: the well log delivery flow with the pre and ing chains that feed it, the mapping it renders with, the
/// schedule library, and the drop-off folder the pre flows read. The wellbore fixture flow and its mapping are copied in
/// beside them, for the suites that need a second flow with no payload files. The copy keeps the shape a repository has,
/// one folder per source, so a path the catalog records here is the path it would record for a customer's repo. The
/// templates the mappings fill are in the source's template folder, which a sync of the copy records; the
/// partition cache lives in the module's database, so a test saves it rather than inventing a second estate that would
/// drift from the real one.
/// </summary>
internal static class SampleEstate
{
    /// <summary>The log source the sample well log flow's schedule fires for; the flow's one required parameter.</summary>
    public const string LogSource = "COMPOSITE";

    /// <summary>The well log flow of the sample estate, which streams payload files beside its documents.</summary>
    public const string FlowName = "welldb-welllog-03-header-delivery";

    /// <summary>The wellbore master data fixture flow, which streams no payload files.</summary>
    public const string WellboreFlowName = "wells-wellbore-03-header-delivery";

    /// <summary>The mapping the wellbore flow pins, and the template version it fills.</summary>
    public const string WellboreMapping = "Wellbore@1.0.0";

    public const string WellboreTemplateKind = "osdu:wks:master-data--Wellbore:1.3.0";

    public const string WellboreTemplateVersion = "58d6bdbd9d066a06";

    /// <summary>The templates the sample and fixture mappings pin, by kind, and the file of the sample's template folder holding each.</summary>
    private static readonly (string Kind, string File)[] Templates =
    [
        ("osdu:wks:work-product-component--WellLog:1.4.0", "osdu_wks_work-product-component--WellLog_1.4.0.json"),
        (WellboreTemplateKind, "osdu_wks_master-data--Wellbore_1.3.0.json"),
    ];

    /// <summary>
    /// The folder the source occupies in a repository. A repository is laid out per source: one top-level folder, which
    /// the catalog and the GUI read as a project, holding everything that source needs.
    /// </summary>
    public const string SourceFolder = "welldb";

    /// <summary>
    /// What the source folder holds, and so what a copy of the estate is made of: the documents, what they render with
    /// (mappings, the template folder holding the templates they fill, and the dictionaries a dimension flow cleans its
    /// values through), and the drop-off folder. The cache records are not among them: a cache lives in the module's
    /// database, and the repository holds only the flows that declare it.
    /// </summary>
    private static readonly string[] Parts = ["flows", "reference", "cache", "dictionaries", "mappings", "templates", "data"];

    /// <summary>The schedule library every flow of the estate joins a schedule of.</summary>
    private const string ScheduleLibrary = "schedules.yaml";

    /// <summary>
    /// Copies the estate into <paramref name="destination"/> and returns it, with the wellbore fixture flow and its mapping
    /// beside the estate's own. Nothing about the flows is rewritten: what executes is the sample flow as shipped, reading
    /// the ingestion tables its document declares, and the copy is a disposable place for its run logs and work batches.
    /// </summary>
    public static string CopyTo(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var source = SourceRoot();
        foreach (var part in Parts)
        {
            CopyDirectory(Path.Combine(source, part), Path.Combine(destination, SourceFolder, part));
        }

        File.Copy(Path.Combine(source, ScheduleLibrary), Path.Combine(destination, SourceFolder, ScheduleLibrary), overwrite: true);
        File.Copy(Samples.WellboreFlowFile, Path.Combine(destination, SourceFolder, "flows", WellboreFlowName + ".yaml"), overwrite: true);
        File.Copy(Path.Combine(Samples.FixtureMappings, WellboreMapping + ".yaml"), Path.Combine(destination, SourceFolder, "mappings", WellboreMapping + ".yaml"), overwrite: true);
        return destination;
    }

    /// <summary>The flow document of the copied estate, as the catalog stores it.</summary>
    public static string FlowYaml(string root, string flowName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowName);
        return File.ReadAllText(Path.Combine(root, SourceFolder, "flows", flowName + ".yaml"));
    }

    /// <summary>The repo-relative path of a flow document, as the catalog records it: under the folder of its source.</summary>
    public static string FlowPath(string flowName) => SourceFolder + "/flows/" + flowName + ".yaml";

    /// <summary>A mapping document of a copied estate (<c>Name@version</c>), at the path the copy put it.</summary>
    public static string MappingIn(string root, string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        return Path.Combine(root, SourceFolder, "mappings", reference + ".yaml");
    }

    /// <summary>A sample mapping document (<c>Name@version</c>) as the repository holds it, or the fixture mapping of that name.</summary>
    public static string MappingYaml(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var sample = Path.Combine(SourceRoot(), "mappings", reference + ".yaml");
        return File.ReadAllText(File.Exists(sample) ? sample : Path.Combine(Samples.FixtureMappings, reference + ".yaml"));
    }

    /// <summary>
    /// Brings the module's own schema up to date in the catalog database, exactly as the control plane's bootstrap does
    /// before it serves a request. The database itself is never created here: the suite is given a disposable one.
    /// </summary>
    public static async Task MigrateModuleAsync(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        // One migrate at a time in this process: the suites run in parallel and every one of them brings the schema up to
        // date first, and two migrates of the same schema at once fail rather than wait for each other.
        await ModuleMigrate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ModuleDatabases.MigrateAsync(
                OsduModuleDatabase.Create(),
                connectionString,
                allowCreate: false,
                appliedBy: "control plane module tests",
                appliedUtc: DateTime.UtcNow,
                catalogAppliedMigrations: CatalogDatabase.KnownMigrations).ConfigureAwait(false);
        }
        finally
        {
            ModuleMigrate.Release();
        }
    }

    /// <summary>Serializes the module migrate across the suites of this process.</summary>
    private static readonly SemaphoreSlim ModuleMigrate = new(1, 1);

    /// <summary>
    /// The folder a repository holding the sample templates is synced into, as the catalog records its root: the sample
    /// source, whose template folder holds the files <see cref="HoldTemplatesAsync"/> records, at the paths it records them.
    /// </summary>
    public static string TemplateRoot => SourceRoot();

    /// <summary>The repository every suite of this process reads the sample templates through, where no repository of its own is at hand.</summary>
    public const string TemplateRepositoryName = "cp-sample-templates";

    /// <summary>
    /// Records the templates the sample mappings fill as the files of a template folder at the top of repository
    /// <paramref name="repoId"/>, as a sync of the sample estate records its <c>templates</c> folder: which file holds which
    /// template, and nothing of its content, which is read from the file. The repository's catalog row names
    /// <see cref="TemplateRoot"/> as its root, so a page reads the files there. A test that writes a repository's mapping
    /// rows itself, as a sync would, records its template folder with them.
    /// </summary>
    public static async Task HoldTemplatesAsync(string connectionString, Guid repoId, DateTime nowUtc)
    {
        await MigrateModuleAsync(connectionString);
        await using var db = Context(connectionString);
        foreach (var (kind, file) in Templates)
        {
            var path = "templates/" + file;
            var schema = TemplateSources.FromBundledJson(await File.ReadAllTextAsync(Path.Combine(TemplateRoot, "templates", file)), kind, DateTimeOffset.UtcNow, file);
            var row = db.DeliveryRepositoryTemplates.FirstOrDefault(t => t.RepoId == repoId && t.Path == path);
            if (row is null)
            {
                row = new DeliveryRepositoryTemplate
                {
                    Id = SqlFlow.Core.Identity.FlowIdentity.FromName($"delivery-template-file/{repoId:N}/{path}"),
                    RepoId = repoId,
                    Path = path,
                    FirstSeenUtc = nowUtc,
                };
                db.DeliveryRepositoryTemplates.Add(row);
            }

            row.Folder = "templates";
            row.Kind = schema.Kind;
            row.Version = schema.Version;
            row.Problem = null;
            row.LastSeenUtc = nowUtc;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The repository <see cref="TemplateRepositoryName"/>, synced into <see cref="TemplateRoot"/> and holding the sample
    /// templates in its template folder, as a sync of it records them: the catalog's row of it and the rows of its template
    /// files, seen <paramref name="nowUtc"/>. What the Templates page, the explorer and the mapping builder read the sample
    /// templates through. Recorded again whenever asked, so it is the repository synced last.
    /// </summary>
    public static async Task<Guid> HoldTemplateRepositoryAsync(string connectionString, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var repoId = SqlFlow.Core.Identity.FlowIdentity.FromName("repo/" + TemplateRepositoryName);
        await using (var catalog = CatalogDatabase.Create(connectionString))
        {
            var repo = await catalog.Repos.FindAsync(repoId);
            if (repo is null)
            {
                catalog.Repos.Add(new CatalogRepo
                {
                    Id = repoId, Name = TemplateRepositoryName, RootPath = TemplateRoot, FirstSeenUtc = nowUtc, LastSyncUtc = nowUtc,
                });
            }
            else
            {
                repo.RootPath = TemplateRoot;
                repo.LastSyncUtc = nowUtc;
            }

            await catalog.SaveChangesAsync();
        }

        await HoldTemplatesAsync(connectionString, repoId, nowUtc);
        return repoId;
    }

    /// <summary>
    /// Merges the sample lookup tables and the sample reference data into the cache of the sample partition in the
    /// module's database, exactly as the lookups flow's refresh writes the tables and the reference flow's refresh would
    /// capture the records. The well log mapping builds every reference id it writes from these tables and a template, and
    /// each id names one of those records. A database that already holds the same content keeps its current version.
    /// </summary>
    public static async Task SaveCacheAsync(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await MigrateModuleAsync(connectionString);
        var loader = new DeliveryDocumentLoader();
        var flow = loader.LoadCache(Path.Combine(SourceRoot(), "cache", "welldb-lookups-00-cache.yaml"));
        var store = new OsduCacheStore(() => Context(connectionString));
        var lookups = new SnapshotBuilder(store, flow.Scope, flow.Name, TimeProvider.System, NullLogger<SnapshotBuilder>.Instance);
        await lookups.WriteAsync(Samples.SampleLookups(), new CacheCapture(null, "tests", "sample unit maps and curve dictionary"), []);
        var reference = loader.LoadCache(Path.Combine(SourceRoot(), "cache", "osdu-reference-00-cache.yaml"));
        var references = new SnapshotBuilder(store, reference.Scope, reference.Name, TimeProvider.System, NullLogger<SnapshotBuilder>.Instance);
        await references.ImportDirectoryAsync(Path.Combine(Locate(), "cache-records"), reference.Types, new CacheCapture(null, "tests", "sample reference files"));
    }

    /// <summary>A context over the module's schema in the catalog database the suite was given.</summary>
    public static OsduDbContext Context(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new OsduDbContext(OsduDbContext.SqlServerOptions(connectionString));
    }

    /// <summary>The source's own folder inside the samples directory: everything a repository sync would read.</summary>
    private static string SourceRoot() => Path.Combine(Locate(), SourceFolder);

    /// <summary>
    /// The samples directory as the build copied it next to the test binaries: the source folders, and the bundled
    /// schemas beside them. It is copied rather than read out of the repository so the suite works wherever the build
    /// output lands, which walking up from the binaries does not.
    /// </summary>
    private static string Locate()
    {
        var copied = Path.Combine(AppContext.BaseDirectory, "samples");
        if (!Directory.Exists(copied))
        {
            throw new DirectoryNotFoundException(
                $"The sample estate was not copied next to the test binaries ({copied}). It is a Content item of this test project; rebuild the suite.");
        }

        return copied;
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var child in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
        }
    }
}
