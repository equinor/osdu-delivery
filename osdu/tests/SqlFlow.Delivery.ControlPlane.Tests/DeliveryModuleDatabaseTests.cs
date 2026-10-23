using SqlFlow.Core.Secrets;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Catalog.Modules;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Where the module's rows are written when the repository sync reconciles them, against a real SQL Server database. The
/// <c>osdu</c> schema lives in the catalog's database, so the rows are written on the catalog's connection and are part of
/// the sync's own transaction: they commit and roll back with it. Proven here over the sample estate.
/// <para>These tests need a reachable SQL Server whose login may create a database: the estate is the suites' scratch
/// database (<see cref="OsduScratchDatabase"/>), created for each test and dropped when it ends. A server that refuses to
/// create it fails the tests.</para>
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryModuleDatabaseTests
{
    private static readonly Guid Repo = Guid.NewGuid();

    private static readonly DateTime Now = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The mapping documents a copy of the sample estate holds, which is what a reconciliation of it writes: every mapping of
    /// the sample, and the wellbore fixture mapping the copy puts beside them (<see cref="SampleEstate.CopyTo"/>).
    /// </summary>
    private static readonly string[] SampleMappings =
    [
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "samples", "welldb", "mappings"), "*.yaml")
            .Select(file => Path.GetFileName(file)[..^".yaml".Length])
            .Append(SampleEstate.WellboreMapping)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>The partition the sample cache flow declares its types for.</summary>
    private const string SampleScope = "dev";

    /// <summary>
    /// The sample estate names its partition as the reference a node holds, so this assembly supplies it before any test
    /// reads a sample document, and never over a value the process was started with.
    /// </summary>
    [ModuleInitializer]
    internal static void UseSampleEstateReferences()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OSDU_DATA_PARTITION")))
        {
            Environment.SetEnvironmentVariable("OSDU_DATA_PARTITION", SampleScope);
        }
    }

    [Fact]
    public async Task The_rows_commit_and_roll_back_with_the_sync()
    {
        await using var estate = await TestEstate.CreateAsync();
        using var repository = new SampleRepository();
        var warnings = new List<string>();

        var rolledBack = await SyncAsync(estate, repository.Root, warnings, commit: false);
        Assert.Empty(warnings);
        Assert.True(rolledBack.Added > 0, "the sample estate reconciles to rows");
        await using (var module = estate.ModuleContext())
        {
            // The sync's transaction is what the rows were written under, so its rollback took them with it.
            Assert.Empty(await module.DeliveryMappings.Where(m => m.RepoId == Repo).ToListAsync());
            Assert.Empty(await module.DeliveryInterfaces.Where(i => i.RepoId == Repo).ToListAsync());
            Assert.Empty(await module.DeliveryCacheDefinitions.Where(d => d.RepoId == Repo).ToListAsync());
        }

        var committed = await SyncAsync(estate, repository.Root, new List<string>(), commit: true);
        Assert.Equal(rolledBack.Added, committed.Added);
        await using (var module = estate.ModuleContext())
        {
            Assert.Equal(SampleMappings, await ReferencesAsync(module));
            Assert.NotEmpty(await module.DeliveryInterfaces.Where(i => i.RepoId == Repo).ToListAsync());
        }

        // And read back through the module's own store, which is how the GUI and a run reach them.
        var declaration = await new OsduCacheStore(estate.ModuleContext).DeclarationAsync(SampleScope, CancellationToken.None);
        Assert.NotEmpty(declaration.TypeNames);
    }

    [Fact]
    public async Task A_mapping_change_is_read_on_the_catalogs_connection()
    {
        // The lineage check asks the catalog about the graph it stores before it asks the module about its mappings, so
        // this estate's database holds SQLFlow's catalog beside the osdu schema, as the database a sync runs against does.
        await using var estate = await TestEstate.CreateAsync(withCatalog: true);
        using var repository = new SampleRepository();
        await SyncAsync(estate, repository.Root, new List<string>(), commit: true);

        // The lineage check reads the mapping rows the sync wrote, on the connection it was handed.
        Assert.False(await ChangedAsync(estate, repository.Root));

        var mapping = SampleEstate.MappingIn(repository.Root, "Wellbore@1.0.0");
        await File.AppendAllTextAsync(mapping, Environment.NewLine + "# a comment the last sync did not see" + Environment.NewLine);

        Assert.True(await ChangedAsync(estate, repository.Root));
    }

    /// <summary>
    /// One reconciliation, given exactly what the repository sync gives the extension: the catalog's context inside the
    /// catalog's transaction. <paramref name="commit"/> says what becomes of that transaction, and so of the module's rows.
    /// </summary>
    private static async Task<CatalogSyncExtensionResult> SyncAsync(
        TestEstate estate, string root, ICollection<string> warnings, bool commit)
    {
        await using var catalog = CatalogDatabase.Create(estate.ConnectionString);
        await using var transaction = await catalog.Database.BeginTransactionAsync();
        var result = await Sync().SyncAsync(catalog, Repo, root, Now, warnings, CancellationToken.None);
        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        return result;
    }

    private static async Task<bool> ChangedAsync(TestEstate estate, string root)
    {
        await using var catalog = CatalogDatabase.Create(estate.ConnectionString);
        return await Sync().LineageInputsChangedAsync(catalog, Repo, root, CancellationToken.None);
    }

    /// <summary>
    /// The sync as a host composes it: the delivery loader and the resolver that turns a document's ${env:...} into the
    /// partition a cache is keyed by, which is what a capture and a render both key it under.
    /// </summary>
    private static DeliveryCatalogSync Sync() => new(new DeliveryDocumentLoader(), new SecretResolver([new EnvSecretProvider()]));

    /// <summary>The mapping references a reconciliation left, ordered here rather than by the server's collation.</summary>
    private static async Task<string[]> ReferencesAsync(OsduDbContext module)
    {
        var references = await module.DeliveryMappings.Where(m => m.RepoId == Repo).Select(m => m.Reference).ToListAsync();
        references.Sort(StringComparer.Ordinal);
        return [.. references];
    }

    /// <summary>The sample estate copied to a temp directory, and removed with the test.</summary>
    private sealed class SampleRepository : IDisposable
    {
        public SampleRepository()
        {
            Root = SampleEstate.CopyTo(Path.Combine(Path.GetTempPath(), "osdu_module_db_" + Guid.NewGuid().ToString("N")[..8]));
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    /// <summary>
    /// The database a test runs against: the suites' scratch database, created empty for the test and dropped when it ends,
    /// holding the module's <c>osdu</c> schema, migrated exactly as a host migrates it into a database provisioned first,
    /// and, for a test that reads it, SQLFlow's catalog beside it.
    /// </summary>
    private sealed class TestEstate : IAsyncDisposable
    {
        private readonly OsduScratchDatabase _database;

        private TestEstate(OsduScratchDatabase database) => _database = database;

        /// <summary>The estate's one database: the catalog's, which holds the osdu schema.</summary>
        public string ConnectionString => _database.ConnectionString;

        /// <summary>
        /// The estate's database with the module's schema migrated, and with <paramref name="withCatalog"/> SQLFlow's catalog
        /// too, for a test that reads it; the others only need the catalog's connection and its transaction.
        /// </summary>
        public static async Task<TestEstate> CreateAsync(bool withCatalog = false)
        {
            OsduScratchDatabase database;
            try
            {
                database = await OsduScratchDatabase.CreateAsync();
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException(
                    "These tests sync into an estate of their own, the suites' scratch database, which they create beside the test "
                    + $"database and drop again. This server refused: {ex.Message}",
                    ex);
            }

            try
            {
                // The module's schema, migrated into the database that is to hold it, as 'sqlflow db migrate' does.
                await ModuleDatabases.MigrateAsync(
                    OsduModuleDatabase.Create(),
                    database.ConnectionString,
                    allowCreate: false,
                    appliedBy: "module database tests",
                    appliedUtc: Now,
                    catalogAppliedMigrations: CatalogDatabase.KnownMigrations);
                if (withCatalog)
                {
                    await CatalogDatabase.MigrateAsync(database.ConnectionString);
                }

                return new TestEstate(database);
            }
            catch
            {
                // Whatever was taken before the failure is still this test's to give back.
                await database.DisposeAsync();
                throw;
            }
        }

        /// <summary>A context on the estate's database, for reading what a sync left.</summary>
        public OsduDbContext ModuleContext() => new(OsduDbContext.SqlServerOptions(ConnectionString));

        /// <summary>The scratch database clears its own connection pool and evicts any session left on it before it drops.</summary>
        public ValueTask DisposeAsync() => _database.DisposeAsync();
    }
}
