using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The SQL Server the module's suites run on, and the one database they use there. Development and testing expect a
/// local server: without <c>SQLFLOW_TEST_DB</c> the suites use <see cref="LocalDefault"/>, the local default instance
/// under Windows authentication; with it, the server and database it names (CI starts a server of its own and names one
/// there). A database that is missing is created and allowed snapshot transactions, as the ledger's reads need; one that
/// exists is never reconfigured, and one that does not allow them fails the suite naming the statement that does. A server
/// that does not answer fails the suite with a message naming it, never skips it: the module runs on SQL Server in
/// production, and a suite that skipped its database would prove nothing about it.
/// </summary>
/// <remarks>
/// The database must be disposable: the suites migrate it, seed it, and empty the module's schema in it. It is the only
/// database they keep on the server. The tests that use it are the <see cref="SqlServerSuite"/>, which runs one test
/// at a time and holds the database against every other test process while it runs, and the few that need a second
/// database take <see cref="OsduScratchDatabase"/> for as long as they run.
/// </remarks>
public static class OsduTestServer
{
    /// <summary>The environment variable that points the suites at a database other than the local default.</summary>
    public const string Variable = "SQLFLOW_TEST_DB";

    /// <summary>The database the suites use when <see cref="Variable"/> names none: the local default instance.</summary>
    public const string LocalDefault = "Server=localhost;Database=OsduDeliveryTests;Integrated Security=True;TrustServerCertificate=True";

    /// <summary>The SQL Server error a <c>CREATE DATABASE</c> raises when another process created it first.</summary>
    private const int DatabaseExists = 1801;

    private static readonly Lazy<string> Prepared = new(Prepare, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The test database's connection string, the database created and ready the first time it is asked for. Only a test
    /// that holds the database may ask: one in the <see cref="SqlServerSuite"/>.
    /// </summary>
    public static string ConnectionString
    {
        get
        {
            OsduTestDatabaseLock.EnsureHeld($"{nameof(OsduTestServer)}.{nameof(ConnectionString)}");
            return Prepared.Value;
        }
    }

    /// <summary>The connection string without the check that the process holds the database, for the lock that takes it.</summary>
    internal static string Unchecked => Prepared.Value;

    /// <summary>
    /// The test database, for a suite that shares it (the control plane's API suites, the SQL Server ledger suites), each
    /// test under ids of its own. Fails, naming the server, when the server does not answer.
    /// </summary>
    public static string Require() => ConnectionString;

    /// <summary>
    /// Creates <paramref name="database"/> on the server <paramref name="server"/> names when it is missing, and lets a
    /// database this call created run snapshot transactions. Another process creating it at the same moment is not an
    /// error. Returns whether the database allows snapshot transactions now.
    /// </summary>
    internal static bool EnsureDatabase(SqlConnectionStringBuilder server, string database)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        using var connection = new SqlConnection(new SqlConnectionStringBuilder(server.ConnectionString) { InitialCatalog = "master" }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@name", database);
        // EXEC takes a string built in a variable; it does not evaluate a function call in its argument.
        command.CommandText = """
            DECLARE @create nvarchar(300) = N'CREATE DATABASE ' + QUOTENAME(@name);
            IF DB_ID(@name) IS NULL BEGIN EXEC (@create); SELECT CAST(1 AS bit); END ELSE SELECT CAST(0 AS bit);
            """;
        bool created;
        try
        {
            created = (bool)command.ExecuteScalar()!;
        }
        catch (SqlException ex) when (ex.Number == DatabaseExists)
        {
            // Created by another suite between the check and the statement: the database is there, and it is that suite's
            // to configure.
            created = false;
        }

        if (created)
        {
            command.CommandText = """
                DECLARE @allow nvarchar(300) = N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET ALLOW_SNAPSHOT_ISOLATION ON';
                IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @name AND snapshot_isolation_state = 0) EXEC (@allow);
                """;
            command.ExecuteNonQuery();
        }

        command.CommandText = "SELECT CAST(CASE WHEN snapshot_isolation_state = 1 THEN 1 ELSE 0 END AS bit) FROM sys.databases WHERE name = @name;";
        return (bool)command.ExecuteScalar()!;
    }

    private static string Prepare()
    {
        var configured = Environment.GetEnvironmentVariable(Variable);
        var server = new SqlConnectionStringBuilder(string.IsNullOrWhiteSpace(configured) ? LocalDefault : configured);
        if (string.IsNullOrWhiteSpace(server.InitialCatalog))
        {
            throw new InvalidOperationException($"{Variable} names server '{server.DataSource}' but no database; name a disposable one with Database=.");
        }

        bool snapshot;
        try
        {
            snapshot = EnsureDatabase(server, server.InitialCatalog);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                $"The module's suites run on SQL Server and expect a local one for development and testing, and server '{server.DataSource}' "
                + $"(database '{server.InitialCatalog}', from {(string.IsNullOrWhiteSpace(configured) ? "the local default" : Variable)}) could not be reached or "
                + $"prepared: {ex.Message} Start a local SQL Server the Windows login can create databases on, or set {Variable} to a disposable database on "
                + "another server. The unit tier (bash tools/test.sh, and CI's Build and test) runs with no SQL Server: a test that needs one, here or "
                + "through the sample template and cache stores, carries [Trait(\"Category\", \"Integration\")] so that tier leaves it out.",
                ex);
        }

        if (!snapshot)
        {
            throw new InvalidOperationException(
                $"The ledger reads under snapshot isolation, and database '{server.InitialCatalog}' on server '{server.DataSource}' does not allow it. "
                + $"The suites never reconfigure a database they were lent; enable it once with ALTER DATABASE [{server.InitialCatalog}] SET ALLOW_SNAPSHOT_ISOLATION ON.");
        }

        return server.ConnectionString;
    }
}

/// <summary>
/// The test database belongs to one test process at a time: whichever holds an exclusive, session-owned application lock
/// on it. Inside a process the SQL Server tests run one at a time (they are one collection with parallelization
/// disabled), so the hold is counted per process: the collection's fixture takes it for as long as the collection runs,
/// and whatever uses the database inside it (a test's <see cref="OsduTestDatabase"/>, the scratch database) enters the
/// hold the process already has. A process that dies releases it with its session.
/// </summary>
internal static class OsduTestDatabaseLock
{
    /// <summary>How long a process waits for another to finish its SQL Server tests before it gives up.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(30);

    /// <summary>The name the holding session carries, so a waiting process can say who holds the database.</summary>
    private const string HolderName = "OSDU Delivery tests (holding the test database)";

    private const string Resource = "osdu-test-database";

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim Acquiring = new(1, 1);
    private static SqlConnection? _session;
    private static int _holds;

    /// <summary>Takes the test database for this process, waiting while another process holds it.</summary>
    public static IDisposable Acquire()
    {
        if (TryEnter() is { } entered)
        {
            return entered;
        }

        Acquiring.Wait();
        try
        {
            if (TryEnter() is { } raced)
            {
                return raced;
            }

            var session = Lock();
            lock (Gate)
            {
                _session = session;
                _holds = 1;
            }

            return new Hold();
        }
        finally
        {
            Acquiring.Release();
        }
    }

    /// <summary>Enters the hold this process already has; <paramref name="what"/> names the caller when it has none.</summary>
    public static IDisposable Enter(string what) => TryEnter() ?? throw NotHeld(what);

    /// <summary>Fails, naming <paramref name="what"/> and the fix, unless this process holds the test database.</summary>
    public static void EnsureHeld(string what)
    {
        lock (Gate)
        {
            if (_holds == 0)
            {
                throw NotHeld(what);
            }
        }
    }

    private static Hold? TryEnter()
    {
        lock (Gate)
        {
            if (_holds == 0)
            {
                return null;
            }

            _holds++;
            return new Hold();
        }
    }

    private static InvalidOperationException NotHeld(string what) => new(
        $"{what} was used by a test outside the SQL Server collection. The suites share one test database and use it one test at a time: "
        + $"put the test class in [Collection({nameof(SqlServerSuite)}.{nameof(SqlServerSuite.Name)})].");

    private static SqlConnection Lock()
    {
        // Pooling is off so that disposing the connection ends its session, and with it the lock, however the process ends.
        var builder = new SqlConnectionStringBuilder(OsduTestServer.Unchecked) { Pooling = false, ApplicationName = HolderName };
        var session = new SqlConnection(builder.ConnectionString);
        try
        {
            session.Open();
            using var command = session.CreateCommand();
            command.CommandTimeout = (int)Wait.TotalSeconds + 60;
            command.CommandText = """
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = @timeout;
                SELECT @result;
                """;
            command.Parameters.AddWithValue("@resource", Resource);
            command.Parameters.AddWithValue("@timeout", (int)Wait.TotalMilliseconds);
            var result = (int)command.ExecuteScalar()!;
            if (result >= 0)
            {
                return session;
            }

            throw new InvalidOperationException(result == -1
                ? $"Another test process has held the test database '{builder.InitialCatalog}' on '{builder.DataSource}' for {Wait.TotalMinutes:0} minutes. "
                  + $"Its session carries the application name '{HolderName}': find it with SELECT session_id, host_process_id, login_time FROM sys.dm_exec_sessions "
                  + $"WHERE program_name = N'{HolderName}', and wait for that test run or end it."
                : $"SQL Server refused the lock on the test database '{builder.InitialCatalog}' on '{builder.DataSource}' (sp_getapplock returned {result}).");
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static void Release()
    {
        lock (Gate)
        {
            _holds--;
            if (_holds == 0)
            {
                _session?.Dispose();
                _session = null;
            }
        }
    }

    private sealed class Hold : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Release();
            }
        }
    }
}

/// <summary>
/// The fixture of the <see cref="SqlServerSuite"/>: holds the test database against every other test process for as
/// long as the collection runs in this one, and imports the sample templates and cache before the collection's first test,
/// so that no test inside it finds its database written by that import.
/// </summary>
public sealed class OsduTestDatabaseFixture : IDisposable
{
    private readonly IDisposable _hold = OsduTestDatabaseLock.Acquire();

    public OsduTestDatabaseFixture()
    {
        try
        {
            Samples.WarmSampleStores();
        }
        catch
        {
            _hold.Dispose();
            throw;
        }
    }

    public void Dispose() => _hold.Dispose();
}

/// <summary>
/// The module's own database (schema <c>osdu</c>) on SQL Server, emptied when a test takes it, for the suites that run the
/// ledger, the template and cache stores and the engine: the real <see cref="OsduLedger"/> over the real model and the
/// schema its migrations build, on the provider production runs on. It is the test database itself, brought to the
/// module's latest migration the first time a process takes it, and every row of the <c>osdu</c> schema is removed each
/// time a test takes it, so every test starts from a module that holds nothing, exactly as a new deployment does. Only a
/// test in the <see cref="SqlServerSuite"/> may take it.
/// </summary>
public sealed class OsduTestDatabase : IDisposable
{
    private static readonly Lazy<bool> Migrated = new(Migrate, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<bool> CatalogMigrated = new(MigrateCatalog, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IDisposable _hold;
    private readonly DbContextOptions<OsduDbContext> _options;

    public OsduTestDatabase()
    {
        _hold = OsduTestDatabaseLock.Enter(nameof(OsduTestDatabase));
        try
        {
            ConnectionString = OsduTestServer.ConnectionString;
            _ = Migrated.Value;
            Empty(ConnectionString);
            _options = OsduDbContext.SqlServerOptions(ConnectionString);
        }
        catch
        {
            _hold.Dispose();
            throw;
        }
    }

    /// <summary>The database's connection string, for a suite that reaches it as a host would.</summary>
    public string ConnectionString { get; }

    public OsduDbContext CreateDbContext() => new(_options);

    public OsduLedger Ledger(TimeProvider? time = null) => new(CreateDbContext, time);

    public OsduCacheStore Caches() => new(CreateDbContext);

    /// <summary>
    /// A context over SQLFlow's catalog in the same database, for the seams the module shares with the platform (the
    /// catalog sync extension, the search contributor). The catalog is migrated there the first time a process asks. Only
    /// the module's schema is emptied between tests, so a test writing catalog rows writes them under ids of its own and
    /// removes them when it is done.
    /// </summary>
    public CatalogDbContext CreateCatalogContext()
    {
        _ = CatalogMigrated.Value;
        return new CatalogDbContext(CatalogDatabase.BuildOptions(ConnectionString));
    }

    /// <summary>
    /// Declares what <paramref name="flowName"/> caches for <paramref name="scope"/> exactly as the repository sync leaves it:
    /// one <c>osdu.CacheDefinition</c> row per type, replacing every row the flow had. No types declares nothing for the flow.
    /// </summary>
    public async Task DeclareCacheAsync(string scope, string flowName, params ReferenceTypeSpec[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        await using var db = CreateDbContext();
        await db.DeliveryCacheDefinitions.Where(d => d.FlowName == flowName).ExecuteDeleteAsync();
        var repoId = Guid.NewGuid();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var type in types)
        {
            db.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
            {
                Id = Guid.NewGuid(),
                RepoId = repoId,
                FlowName = flowName,
                Scope = scope,
                Origin = CacheOrigins.Text(type.Origin),
                Endpoint = type.Origin == CacheOrigin.Osdu ? "https://osdu.example.test" : null,
                Connection = type.Origin == CacheOrigin.Table ? "${env:INGESTION_DB}" : null,
                SourceObject = type.Table,
                KeyField = type.Key,
                DictionaryPath = type.DictionaryPath,
                RelativePath = "cache/" + flowName + ".yaml",
                Name = type.Name,
                EntityType = type.EntityType,
                Kind = type.Kind,
                Query = type.Origin == CacheOrigin.Osdu ? type.Query : null,
                FieldsJson = new JsonArray(type.Fields.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["as"] = f.Name }).ToArray()).ToJsonString(),
                OnChange = type.OnChange == CacheChangeMode.Approve ? "approve" : "auto",
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
        }

        await db.SaveChangesAsync();
    }

    public void Dispose() => _hold.Dispose();

    private static bool Migrate()
    {
        using var db = new OsduDbContext(OsduDbContext.SqlServerOptions(OsduTestServer.ConnectionString));
        db.Database.Migrate();
        return true;
    }

    private static bool MigrateCatalog()
    {
        using var db = new CatalogDbContext(CatalogDatabase.BuildOptions(OsduTestServer.ConnectionString));
        db.Database.Migrate();
        return true;
    }

    /// <summary>
    /// Empties every table of the module's schema but its migration history, and drops the tables and the views dimension
    /// builds made there (<c>dim_...</c>, <c>v_dim_...</c>, a build's staging <c>dimstage_...</c>), which are no objects of
    /// the model; a view a test made by hand goes with them. The schema has no foreign keys, so each table of the model is truncated, which also starts its
    /// identity column from its seed again.
    /// </summary>
    private static void Empty(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@schema", DeliveryModel.SchemaName);
        command.Parameters.AddWithValue("@history", OsduDbContext.MigrationsHistoryTable);
        command.Parameters.AddWithValue("@made", DimensionTables.Prefix.Replace("_", "[_]", StringComparison.Ordinal) + "%");
        command.Parameters.AddWithValue("@staged", DimensionStage.TablePrefix.Replace("_", "[_]", StringComparison.Ordinal) + "%");
        command.CommandText = """
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql = @sql + N'DROP VIEW ' + QUOTENAME(s.name) + N'.' + QUOTENAME(v.name) + N';'
            FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id
            WHERE s.name = @schema;
            SELECT @sql = @sql + CASE WHEN t.name LIKE @made OR t.name LIKE @staged THEN N'DROP TABLE ' ELSE N'TRUNCATE TABLE ' END + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name <> @history;
            EXEC sys.sp_executesql @sql;
            """;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// The one database a test may have beside the test database, for the few tests that need a second one: a migration that
/// has to start from an empty schema, a database that does not allow snapshot isolation, an estate that keeps SQLFlow's
/// catalog and the module apart, a counter that must see no other writer. It is always the same database,
/// <c>{test database}_scratch</c>: created empty when a test takes it, after dropping whatever a test that died left
/// there, and dropped when the test disposes it, so the server holds it only while such a test runs. Only a test in the
/// <see cref="SqlServerSuite"/> may take it, and those run one at a time, so it never has two users.
/// </summary>
public sealed class OsduScratchDatabase : IAsyncDisposable
{
    private readonly IDisposable _hold;
    private readonly string _master;
    private bool _disposed;

    private OsduScratchDatabase(IDisposable hold, string master, string name, string connectionString)
    {
        _hold = hold;
        _master = master;
        Name = name;
        ConnectionString = connectionString;
    }

    /// <summary>The scratch database's name.</summary>
    public string Name { get; }

    /// <summary>Its connection string, under the test database's server and credentials.</summary>
    public string ConnectionString { get; }

    /// <summary>Creates the scratch database empty, as a database with the server's defaults.</summary>
    public static async Task<OsduScratchDatabase> CreateAsync(CancellationToken ct = default)
    {
        var hold = OsduTestDatabaseLock.Enter(nameof(OsduScratchDatabase));
        try
        {
            var test = new SqlConnectionStringBuilder(OsduTestServer.ConnectionString);
            var name = test.InitialCatalog + "_scratch";
            var master = new SqlConnectionStringBuilder(test.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
            var connectionString = new SqlConnectionStringBuilder(test.ConnectionString) { InitialCatalog = name }.ConnectionString;
            await DropAsync(master, name, connectionString, ct).ConfigureAwait(false);
            await OnMasterAsync(master, name, "N'CREATE DATABASE ' + QUOTENAME(@name)", ct).ConfigureAwait(false);
            return new OsduScratchDatabase(hold, master, name, connectionString);
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    /// <summary>Lets the scratch database run snapshot transactions, as the ledger's reads need.</summary>
    public Task AllowSnapshotIsolationAsync(CancellationToken ct = default)
        => OnMasterAsync(_master, Name, "N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET ALLOW_SNAPSHOT_ISOLATION ON'", ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await DropAsync(_master, Name, ConnectionString, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _hold.Dispose();
        }
    }

    private static async Task DropAsync(string master, string name, string connectionString, CancellationToken ct)
    {
        // A pooled connection to it would keep it in use; only its own pool is cleared, so no other test's connections go.
        using (var pooled = new SqlConnection(connectionString))
        {
            SqlConnection.ClearPool(pooled);
        }

        // Single-user first, so a connection the test left open cannot keep the database alive.
        await OnMasterAsync(
            master,
            name,
            "CASE WHEN DB_ID(@name) IS NULL THEN N'' ELSE N'ALTER DATABASE ' + QUOTENAME(@name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@name) + N';' END",
            ct).ConfigureAwait(false);
    }

    /// <summary>Runs, on master, the statement <paramref name="statement"/> builds from the database name <c>@name</c>.</summary>
    private static async Task OnMasterAsync(string master, string name, string statement, CancellationToken ct)
    {
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("@name", name);
        // EXEC takes a string built in a variable; it does not evaluate an expression in its argument.
        command.CommandText = $"DECLARE @sql nvarchar(800) = {statement}; IF @sql <> N'' EXEC (@sql);";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
