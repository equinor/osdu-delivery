using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Templates;
using SqlFlow.Execution;
using SqlFlow.Orchestration;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The sample estate on a real SQL Server, for the chain suite: a copy of the repository's pre, ingestion and OSDU flows
/// generated with the test database's name and with schemas of this fixture's own, plus the host composition that runs
/// them. What executes is the shipped estate, rewritten only where it names a database,
/// a schema, a connection reference or a network target, so a chain run here is the chain an operator would run.
/// <para>Every fixture instance owns a unique pair of schemas (<c>pre_&lt;n&gt;</c> and <c>silver_&lt;n&gt;</c>), a unique flow
/// name prefix and therefore unique ledger rows, and a unique environment variable holding the connection. Suites and
/// classes running beside each other never meet, and everything the fixture created is dropped when it is disposed.</para>
/// <para>The tables themselves are created by SQLFlow's own flows rather than by the fixture: the pre flow creates
/// <c>pre_&lt;n&gt;.WellLog</c> and its typed view, the ingestion flow creates the keyed <c>silver_&lt;n&gt;.WellLog</c> with
/// the system columns the delivery reads. The fixture creates only what the engine does not, which is the schemas.</para>
/// </summary>
public sealed class SqlServerIngestionFixture : IAsyncDisposable
{
    /// <summary>The estate parts copied next to the generated flows; the data folders are written per test instead.</summary>
    /// <summary>
    /// What the fixture copies out of the source folder as it is: the mappings the flows pin, the template folder holding
    /// the templates they fill, and the cache flow with the sample records beside it.
    /// </summary>
    private static readonly string[] CopiedParts = ["mappings", "templates", "cache"];

    /// <summary>The flow documents of the well log chain, by the name they carry in the repository.</summary>
    private static readonly string[] ChainDocuments =
    [
        "welldb-welllog-01-header-pre", "welldb-welllog-01-curves-pre", "welldb-welllog-02-header-ing", "welldb-welllog-02-curves-ing", "welldb-welllog-03-header-delivery",
    ];

    /// <summary>The fixture flows that load the wellbore tables, generated for a fixture that asks for them.</summary>
    private static readonly string[] WellboreChainDocuments =
    [
        "wells-wellbore-01-header-pre", "wells-wellbore-01-aliases-pre", "wells-wellbore-02-header-ing", "wells-wellbore-02-aliases-ing",
    ];

    /// <summary>
    /// The target block of the shipped delivery flow, which a test replaces with a local placeholder. Its line endings are
    /// normalized because this source file's own are whatever the checkout wrote, and the document it is matched against
    /// is normalized the same way.
    /// </summary>
    private static readonly string ShippedTarget = """
          endpoint: ${env:OSDU_URL}
          auth:
            type: oauth2ClientCredentials
            secondarySecretRef: ${env:OSDU_CLIENT_ID}
            secretRef: ${env:OSDU_CLIENT_SECRET}
            token:
              url: ${env:OSDU_TOKEN_URL}
              body:
                scope: ${env:OSDU_SCOPE}
        """.ReplaceLineEndings("\n");

    /// <summary>What replaces it: tests never call OSDU, and the protocol is a fake one the host is composed with.</summary>
    private static readonly string LocalTarget = """
          endpoint: http://localhost:9/facade-api
          auth:
            type: none
        """.ReplaceLineEndings("\n");

    /// <summary>The database the shipped documents name their ingestion tables in, which every generated one replaces.</summary>
    private const string DataDatabase = "OsduData.";

    private readonly OsduTestDatabase _database;

    private readonly ServiceProvider _provider;

    private SqlServerIngestionFixture(OsduTestDatabase database, string databaseName, string suffix, string root, ServiceProvider provider, FakeProtocol protocol)
    {
        _database = database;
        ConnectionString = database.ConnectionString;
        DatabaseName = databaseName;
        Suffix = suffix;
        Root = root;
        _provider = provider;
        Protocol = protocol;
        Ledger = new OsduLedger(Context, TimeProvider.System);
    }

    /// <summary>
    /// The suites' test database (<see cref="OsduTestDatabase"/>), its module schema emptied when the fixture starts. The
    /// chain's records are the same every run, and so are the OSDU ids they render to: without the emptying, a run whose
    /// process died before it cleaned up would leave those ids claimed by its flow, and every later run's records would be
    /// held for them.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>Its Initial Catalog: what the generated three-part names are written with.</summary>
    public string DatabaseName { get; }

    /// <summary>What makes this fixture's schemas, flow names and environment variable its own.</summary>
    public string Suffix { get; }

    /// <summary>The generated estate: flows, mappings and the data folders the pre flows read.</summary>
    public string Root { get; }

    /// <summary>The schema the pre-ingestion flows land into.</summary>
    public string PreSchema => "pre_" + Suffix;

    /// <summary>The schema the ingestion flows keep their keyed tables in, which the OSDU flow reads.</summary>
    public string SilverSchema => "silver_" + Suffix;

    /// <summary>The environment variable the generated flows reference; a flow document never holds a connection string.</summary>
    public string ConnectionVariable => "SQLFLOW_CHAIN_DB_" + Suffix;

    /// <summary>The prefix every generated flow name carries, so this fixture's ledger rows are its own.</summary>
    public string FlowPrefix => "rw" + Suffix;

    /// <summary>The OSDU flow's name.</summary>
    public string DeliveryFlowName => FlowPrefix;

    /// <summary>The OSDU flow's id, which the ledger holds its records, submissions and watermark under.</summary>
    public Guid FlowId => Identity.FlowId.Of(DeliveryFlowName);

    /// <summary>The record table the OSDU flow reads, as its document names it.</summary>
    public string RecordObject => $"[{DatabaseName}].[{SilverSchema}].[WellLog]";

    /// <summary>The target the fake protocol stands in for; every delivery the chain makes is recorded on it.</summary>
    public FakeProtocol Protocol { get; }

    /// <summary>The ledger over the module's schema in the test database: what the chain's traceability is asserted from.</summary>
    public OsduLedger Ledger { get; }

    /// <summary>The engine as the host composed it, over the real ingestion tables and the fake target.</summary>
    public EngineContext Engine => _provider.GetRequiredService<EngineContext>();

    /// <summary>The document executor every flow of the chain runs through, whatever its kind.</summary>
    public DocumentExecutor Documents => _provider.GetRequiredService<DocumentExecutor>();

    /// <summary>The composed host, for the services a test needs beyond the engine (the document loaders, the kinds).</summary>
    public IServiceProvider Services => _provider;

    /// <summary>A context over the module's schema in the test database.</summary>
    public OsduDbContext Context() => new(OsduDbContext.SqlServerOptions(ConnectionString));

    /// <summary>The path of one generated flow document, by the name the repository gives it.</summary>
    public string FlowFile(string shippedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shippedName);
        return Path.Combine(Root, "flows", Rename(shippedName) + ".yaml");
    }

    /// <summary>The folder the generated flows live in, which the lineage collector reads as an estate.</summary>
    public string FlowsDirectory => Path.Combine(Root, "flows");

    /// <summary>The name a shipped flow carries in this fixture's estate.</summary>
    public string Rename(string shippedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shippedName);
        return Rename(shippedName, Suffix);
    }

    /// <summary>The name a shipped flow of either chain carries in an estate generated with <paramref name="suffix"/>.</summary>
    private static string Rename(string shippedName, string suffix)
        => shippedName
            .Replace("welldb-welllog-03-header-delivery", "rw" + suffix, StringComparison.Ordinal)
            .Replace("wells-wellbore-03-header-delivery", "wb" + suffix, StringComparison.Ordinal);

    /// <summary>
    /// Brings up one fixture: its schemas, its generated estate and the host that runs it.
    /// </summary>
    /// <param name="fanOut">How many member runs the OSDU flow's document declares beside a coordinating run; 0 declares none.</param>
    /// <param name="batchRecords">How many records one work batch holds, which is also what decides the slice count.</param>
    /// <param name="wellboreChain">Whether the estate also holds the flows that load the wellbore tables, for a source
    /// whose interfaces read them beside the well logs.</param>
    public static async Task<SqlServerIngestionFixture> StartAsync(int fanOut = 0, int batchRecords = 0, bool wellboreChain = false, CancellationToken ct = default)
    {
        // The chain runs against a real server or not at all: there is no in-memory stand-in for what it proves. Its
        // database is the suites' test database, migrated and with the module's schema emptied, allowing the snapshot
        // isolation a record and its child rows are read under; its catalog name is what the chain's flows write
        // three-part names with.
        var database = new OsduTestDatabase();
        var connectionString = database.ConnectionString;
        var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var root = Samples.NewTempDirectory();
        var variable = "SQLFLOW_CHAIN_DB_" + suffix;
        Environment.SetEnvironmentVariable(variable, connectionString);

        var protocol = new FakeProtocol();
        ServiceProvider? provider = null;
        try
        {
            await CreateSchemasAsync(connectionString, "pre_" + suffix, "silver_" + suffix, ct).ConfigureAwait(false);
            GenerateEstate(root, databaseName, suffix, variable, fanOut, batchRecords, wellboreChain);
            provider = Compose(connectionString, protocol);
            await ImportRenderInputsAsync(connectionString, ct).ConfigureAwait(false);
            return new SqlServerIngestionFixture(database, databaseName, suffix, root, provider, protocol);
        }
        catch
        {
            if (provider is not null)
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }

            Environment.SetEnvironmentVariable(variable, null);
            await DropSchemasAsync(connectionString, "pre_" + suffix, "silver_" + suffix, CancellationToken.None).ConfigureAwait(false);
            Delete(root);
            database.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs one flow of the chain through the platform's document executor, the way a node runs a queued run, and fails
    /// the test with the run's own error when it did not succeed.
    /// </summary>
    public async Task<DocumentRunOutcome> RunFlowAsync(string shippedName, RunParameters? parameters = null, Guid? runId = null, CancellationToken ct = default)
    {
        var file = FlowFile(shippedName);
        var outcome = await Documents.RunAsync(
            file,
            new DocumentExecutionOptions
            {
                Parameters = parameters ?? RunParameters.None,
                RunId = runId,
                Actor = "chain tests",
            },
            ct).ConfigureAwait(false);

        Assert.True(outcome.Success, $"{outcome.FlowName} ({outcome.FlowKind}) failed: {outcome.Error}");
        return outcome;
    }

    /// <summary>
    /// Runs the four SQLFlow flows that feed the OSDU flow, in the wave order lineage puts them in: the two pre flows
    /// land whatever files are in their folders, then the two ingestion flows upsert the keyed tables the delivery reads.
    /// </summary>
    public async Task RunIngestionChainAsync(CancellationToken ct = default)
    {
        await RunFlowAsync("welldb-welllog-01-header-pre", ct: ct).ConfigureAwait(false);
        await RunFlowAsync("welldb-welllog-01-curves-pre", ct: ct).ConfigureAwait(false);
        await RunFlowAsync("welldb-welllog-02-header-ing", ct: ct).ConfigureAwait(false);
        await RunFlowAsync("welldb-welllog-02-curves-ing", ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the flows that load the wellbore tables (a fixture started with the wellbore chain): the two pre flows land
    /// the wellbore and alias files, then the two ingestion flows upsert the keyed tables a source's wellbores read.
    /// </summary>
    public async Task RunWellboreChainAsync(CancellationToken ct = default)
    {
        foreach (var name in WellboreChainDocuments)
        {
            await RunFlowAsync(name, ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the wellbore and alias files the wellbore chain's pre flows read.</summary>
    public async Task WriteWellboreFilesAsync(
        IReadOnlyList<IReadOnlyDictionary<string, string?>> wellbores, IReadOnlyList<IReadOnlyDictionary<string, string?>> aliases, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wellbores);
        ArgumentNullException.ThrowIfNull(aliases);
        await SampleWellLogs.WriteCsvAsync(Path.Combine(Root, "data", "wellbore", "wellbore_20260901.csv"), FixtureWellbores.WellboreColumns, wellbores, ct).ConfigureAwait(false);
        await SampleWellLogs.WriteCsvAsync(Path.Combine(Root, "data", "wellbore-aliases", "wellbore_aliases_20260901.csv"), FixtureWellbores.AliasColumns, aliases, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the OSDU flow's <c>deliver</c> operation through the document executor, with this run's values.</summary>
    public Task<DocumentRunOutcome> DeliverAsync(Guid? runId = null, CancellationToken ct = default)
        => RunFlowAsync(
            "welldb-welllog-03-header-delivery",
            new RunParameters { Operation = DeliveryOperations.Deliver, Values = SampleEstate.Values },
            runId ?? Guid.NewGuid(),
            ct);

    /// <summary>
    /// The OSDU flow as its generated document declares it, loaded through the module's own loader and bound to the partition
    /// it names, as a run of it binds it.
    /// </summary>
    public FlowDefinition DeliveryFlow()
    {
        var flow = _provider.GetRequiredService<DeliveryDocumentLoader>().LoadFlow(FlowFile("welldb-welllog-03-header-delivery"));
        return flow.DeclaresPartitions ? flow.ForPartition(Samples.SamplePartition) : flow;
    }

    /// <summary>Writes the well log metadata file the first pre flow reads; naming a new file lands a new batch of rows.</summary>
    public Task WriteLogFileAsync(string fileName, IReadOnlyList<SampleLog> logs, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(logs);
        return SampleWellLogs.WriteCsvAsync(
            Path.Combine(Root, "data", "welllog", fileName), SampleWellLogs.LogColumns, logs.Select(SampleWellLogs.LogRow).ToList(), ct);
    }

    /// <summary>Writes the curve metadata file the second pre flow reads.</summary>
    public Task WriteCurveFileAsync(string fileName, IReadOnlyList<SampleLog> logs, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(logs);
        return SampleWellLogs.WriteCsvAsync(
            Path.Combine(Root, "data", "curves-meta", fileName), SampleWellLogs.CurveColumns, logs.SelectMany(SampleWellLogs.CurveRows).ToList(), ct);
    }

    /// <summary>Writes one file of rows a test built itself, for the cases a sample log cannot express.</summary>
    public Task WriteRowsAsync(string folder, string fileName, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> rows, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return SampleWellLogs.WriteCsvAsync(Path.Combine(Root, "data", folder, fileName), columns, rows, ct);
    }

    /// <summary>Writes each log's payload files where its row points, so a delivery has chunks to stream.</summary>
    public async Task WritePayloadsAsync(IReadOnlyList<SampleLog> logs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(logs);
        foreach (var log in logs)
        {
            await SampleWellLogs.WriteChunkAsync(SampleEstate.PayloadFolder(Path.Combine(Root, "data"), log), log, ct).ConfigureAwait(false);
        }
    }

    /// <summary>How many rows one of this fixture's tables holds, for the assertions about what the chain loaded.</summary>
    public async Task<int> CountAsync(string schema, string table, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET QUOTED_IDENTIFIER ON; SELECT COUNT_BIG(*) FROM [{schema}].[{table}];";
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is long count ? (int)count : 0;
    }

    /// <summary>One column of one ingestion row, by its record key, for the assertions about what the upsert left.</summary>
    public async Task<object?> ValueAsync(string table, string column, string sourceProject, string logId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SET QUOTED_IDENTIFIER ON; SELECT [{column}] FROM [{SilverSchema}].[{table}] WHERE [source_project] = @project AND [log_id] = @log;";
        command.Parameters.AddWithValue("@project", sourceProject);
        command.Parameters.AddWithValue("@log", logId);
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is DBNull ? null : value;
    }

    /// <summary>
    /// Drops everything this fixture created: its two schemas with the tables and views the flows put in them, the ledger
    /// rows of its flow, its environment variable and its estate on disk, and gives its database back to the pool, which
    /// empties the module's schema before the next test takes it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync().ConfigureAwait(false);
        await ClearLedgerAsync().ConfigureAwait(false);
        await DropSchemasAsync(ConnectionString, PreSchema, SilverSchema, CancellationToken.None).ConfigureAwait(false);
        Environment.SetEnvironmentVariable(ConnectionVariable, null);
        Delete(Root);
        _database.Dispose();
    }

    private Task ClearLedgerAsync() => ForgetFlowAsync(FlowId);

    /// <summary>The ledger tables a flow's rows are forgotten from, the rows that refer to others first.</summary>
    private static readonly Type[] LedgerTables =
    [
        typeof(DeliveryRecordEvent), typeof(DeliveryLease), typeof(DeliveryAttempt), typeof(DeliveryRecord),
        typeof(DeliveryWorkBatch), typeof(DeliverySourceWatermark), typeof(DeliveryActivity), typeof(DeliverySubmission),
    ];

    /// <summary>
    /// The rows one statement of <see cref="ForgetFlowAsync"/> deletes: the ledger's own limit. SQL Server locks a whole
    /// table once one statement holds 5,000 row locks on one of its indexes, and the ledger suite's lock escalation test
    /// counts such locks across the database while the other suites run, so a cleanup of a large case must not take them.
    /// </summary>
    private const int ForgetChunk = 1000;

    /// <summary>
    /// Deletes every ledger row of <paramref name="flowId"/>: its record events, leases, attempts, records, work batches,
    /// watermarks, activities and submissions, <see cref="ForgetChunk"/> rows to a statement. The sample rows give every
    /// fixture the same delivery keys; the flow is what makes the rows a flow's. A test that delivers through a flow of its
    /// own beside the fixture's forgets that flow when it ends.
    /// </summary>
    public async Task ForgetFlowAsync(Guid flowId)
    {
        List<string> tables;
        await using (var db = Context())
        {
            tables = LedgerTables
                .Select(type => db.Model.FindEntityType(type) ?? throw new InvalidOperationException($"{type.Name} is not an entity of the module's model."))
                .Select(entity => $"[{entity.GetSchema()}].[{entity.GetTableName()}]")
                .ToList();
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET QUOTED_IDENTIFIER ON; DELETE TOP ({ForgetChunk.ToString(CultureInfo.InvariantCulture)}) FROM {table} WHERE [FlowId] = @flow;";
            command.Parameters.Add(new SqlParameter("@flow", System.Data.SqlDbType.UniqueIdentifier) { Value = flowId });
            int deleted;
            do
            {
                deleted = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            while (deleted > 0);
        }
    }

    /// <summary>
    /// Deletes the cache of a partition a test created for itself, with the dependency sets its renders recorded against
    /// it. The sample partition's cache is shared by every fixture and is never forgotten.
    /// </summary>
    public async Task ForgetCacheAsync(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (scope == Samples.SampleCacheScope)
        {
            throw new InvalidOperationException($"The cache of partition '{scope}' is shared by every fixture; a test forgets only a partition of its own.");
        }

        await using var db = Context();
        var sets = await db.DeliveryCacheSetEntries.Where(e => e.Scope == scope).Select(e => e.SetId).Distinct().ToListAsync().ConfigureAwait(false);
        await db.DeliveryCacheSetEntries.Where(e => sets.Contains(e.SetId)).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.DeliveryCacheSets.Where(s => sets.Contains(s.SetId)).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.DeliveryUpdateTags.Where(t => t.Scope == scope).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.DeliveryCacheMembers.Where(m => m.Scope == scope).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.DeliveryCacheItems.Where(i => i.Scope == scope).ExecuteDeleteAsync().ConfigureAwait(false);
        await db.DeliveryCacheVersions.Where(v => v.Scope == scope).ExecuteDeleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The host as the CLI and the node compose it: the platform engine, then the module's kinds, then the ledger over the
    /// module's schema in the test database. The fake target is registered before the module, because the module registers
    /// the real protocol factory only when nothing else claims it.
    /// </summary>
    private static ServiceProvider Compose(string connectionString, FakeProtocol protocol)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IProtocolFactory>(new FakeProtocolFactory(protocol));
        services.AddSingleton<IRecordSearchFactory>(FixedRecordSearchFactory.SampleWellbores());
        services.AddSqlFlowEngine();
        services.AddDeliveryKind();
        services.AddDeliveryLedger(_ => () => new OsduDbContext(OsduDbContext.SqlServerOptions(connectionString)));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Saves the templates the sample mappings pin and the sample partition's cache into the fixture's database, which
    /// starts empty. A render reads the cache from that database and its template from the estate's template folder; the
    /// templates are saved as a repository sync saves what its folders hold, which the validation gate reads a version from
    /// when a folder no longer holds it.
    /// </summary>
    private static async Task ImportRenderInputsAsync(string connectionString, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        OsduDbContext Contexts() => new(OsduDbContext.SqlServerOptions(connectionString));
        await Samples.ImportSampleTemplatesAsync(new OsduTemplateStore(Contexts, TimeProvider.System)).ConfigureAwait(false);
        await Samples.ImportSampleCacheAsync(new OsduCacheStore(Contexts)).ConfigureAwait(false);
    }

    private static async Task CreateSchemasAsync(string connectionString, string preSchema, string silverSchema, CancellationToken ct)
    {
        // SQLFlow's flows create a schema they find missing, but two first runs can both find it missing, and the second
        // CREATE then fails. [raw], where ingestion stages its rows, is shared by every fixture on the database, so the
        // schemas are made here before any flow runs, under an application lock that queues the fixtures starting together
        // on a new database.
        await ExecuteAsync(
            connectionString,
            $"""
            SET QUOTED_IDENTIFIER ON;
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            DECLARE @granted int;
            EXEC @granted = sys.sp_getapplock @Resource = N'SqlFlow.Delivery.Tests.schemas', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 20000;
            IF @granted < 0 THROW 50000, N'The chain fixture waited 20 seconds for another fixture to finish creating its schemas.', 1;
            IF SCHEMA_ID('{preSchema}') IS NULL EXEC(N'CREATE SCHEMA [{preSchema}]');
            IF SCHEMA_ID('{silverSchema}') IS NULL EXEC(N'CREATE SCHEMA [{silverSchema}]');
            IF SCHEMA_ID('raw') IS NULL EXEC(N'CREATE SCHEMA [raw]');
            COMMIT TRANSACTION;
            """,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops the views, then the tables, then the schemas themselves. The order matters: a typed view is bound to the table
    /// it projects, and a schema cannot be dropped while it holds anything.
    /// </summary>
    private static async Task DropSchemasAsync(string connectionString, string preSchema, string silverSchema, CancellationToken ct)
    {
        try
        {
            await ExecuteAsync(
                connectionString,
                $"""
                SET QUOTED_IDENTIFIER ON;
                DECLARE @sql nvarchar(max) = N'';
                SELECT @sql = @sql + N'DROP VIEW [' + s.[name] + N'].[' + v.[name] + N'];'
                FROM sys.views v INNER JOIN sys.schemas s ON s.[schema_id] = v.[schema_id]
                WHERE s.[name] IN (N'{preSchema}', N'{silverSchema}');
                SELECT @sql = @sql + N'DROP TABLE [' + s.[name] + N'].[' + t.[name] + N'];'
                FROM sys.tables t INNER JOIN sys.schemas s ON s.[schema_id] = t.[schema_id]
                WHERE s.[name] IN (N'{preSchema}', N'{silverSchema}');
                IF @sql <> N'' EXEC sp_executesql @sql;
                IF SCHEMA_ID('{preSchema}') IS NOT NULL EXEC(N'DROP SCHEMA [{preSchema}]');
                IF SCHEMA_ID('{silverSchema}') IS NOT NULL EXEC(N'DROP SCHEMA [{silverSchema}]');
                """,
                ct).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            // The database is a disposable one, and a schema left behind must not turn a passing chain into a failing
            // suite; it is reported so the leftover is visible rather than silent.
            throw new InvalidOperationException(
                $"The chain fixture could not drop its schemas [{preSchema}] and [{silverSchema}] from the test database; drop them by hand. {ex.Message}", ex);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes this fixture's estate: the shipped mappings and the cache with its sample records as they are, the five
    /// chain documents rewritten for this database (and, when asked for, the four flows that load the wellbore tables), and
    /// the empty drop-off folders the pre flows read.
    /// </summary>
    internal static void GenerateEstate(string root, string databaseName, string suffix, string variable, int fanOut, int batchRecords, bool wellboreChain = false)
    {
        foreach (var part in CopiedParts)
        {
            CopyDirectory(Path.Combine(Samples.Source, part), Path.Combine(root, part));
        }

        // The fixture mappings sit beside the sample's, for the sources whose interfaces deliver wellbores, and the
        // templates the sample does not carry beside its own in the one template folder.
        CopyDirectory(Samples.FixtureMappings, Path.Combine(root, "mappings"));
        CopyDirectory(Samples.FixtureTemplateFiles, Path.Combine(root, "templates"));

        foreach (var folder in new[] { "welllog", "curves-meta", "curves", "wellbore", "wellbore-aliases" })
        {
            Directory.CreateDirectory(Path.Combine(root, "data", folder));
        }

        Directory.CreateDirectory(Path.Combine(root, "flows"));
        foreach (var name in wellboreChain ? ChainDocuments.Concat(WellboreChainDocuments) : ChainDocuments)
        {
            // The repository is checked out with whatever line endings the platform writes, so every document is read as
            // one normalized text; the rewrites below are line based and would otherwise match nothing.
            var folder = WellboreChainDocuments.Contains(name) ? Path.Combine(Samples.FixtureDocuments, "flows") : Path.Combine(Samples.Source, "flows");
            var shipped = File.ReadAllText(Path.Combine(folder, name + ".yaml")).ReplaceLineEndings("\n");
            var generated = Generate(shipped, name, databaseName, suffix, variable, fanOut, batchRecords);
            File.WriteAllText(Path.Combine(root, "flows", Rename(name, suffix) + ".yaml"), generated);
        }
    }

    /// <summary>
    /// One shipped document as this fixture runs it: the same flow, naming this database, these schemas, this connection
    /// reference and, for the OSDU flow, a target no test ever calls.
    /// </summary>
    private static string Generate(string shipped, string name, string databaseName, string suffix, string variable, int fanOut, int batchRecords)
    {
        var text = Rename(Replace(shipped.ReplaceLineEndings("\n"), "${env:OSDU_DATA_DB}", "${env:" + variable + "}", name), suffix);
        text = QualifyObjectNames(text, name, databaseName, suffix);

        if (name.EndsWith("-pre", StringComparison.Ordinal))
        {
            text = Replace(text, "\n  schema: pre\n", $"\n  schema: pre_{suffix}\n", name);
        }

        if (name == "welldb-welllog-03-header-delivery")
        {
            text = Replace(text, ShippedTarget, LocalTarget, name);

            // The chain renders against the cache the fixture imports, with no cache flow synced to bring up to date, so
            // the shipped cache step stays out, as the platform it would reach does.
            text = Replace(text, "\ncache:\n  before: [UnitAlias, DepthUnitAlias, CurveDictionary]\n", "\n", name);
            if (fanOut > 0)
            {
                text = Replace(
                    text,
                    "\n  concurrency: 8\n",
                    $"\n  concurrency: 8\n  fanOut: {fanOut.ToString(CultureInfo.InvariantCulture)}\n  fanOutMinRecords: 1\n",
                    name);
            }

            if (batchRecords > 0)
            {
                text = Replace(
                    text,
                    "\n  batchSize: 50\n",
                    $"\n  batchSize: 50\n  batchRecords: {batchRecords.ToString(CultureInfo.InvariantCulture)}\n",
                    name);
            }
        }

        return text;
    }

    /// <summary>
    /// Points every ingestion table a document names at this fixture's database and schemas, writing the three-part name
    /// as a quoted scalar.
    /// <para>The quotes are the point: a rewritten name begins with '[', and a YAML plain scalar may not, so
    /// <c>object: [Db].[ing_x].WellLog</c> parses as a flow sequence and the document is refused. The whole value is
    /// rewritten rather than its prefix, because a prefix substitution cannot put the closing quote on.</para>
    /// <para>Nothing may still name the sample's own database afterwards: a value this does not understand would
    /// otherwise leave the generated estate reading tables that belong to the repository's sample, not to this test.</para>
    /// </summary>
    private static string QualifyObjectNames(string text, string document, string databaseName, string suffix)
    {
        const string Key = "object:";
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var at = line.IndexOf(Key, StringComparison.Ordinal);
            if (at < 0 || line.AsSpan(0, at).TrimStart().Length > 0)
            {
                // Not a key of its own: a word inside a comment or a longer key, which names no table.
                continue;
            }

            var value = line[(at + Key.Length)..].Trim();
            if (!value.StartsWith(DataDatabase, StringComparison.Ordinal))
            {
                continue;
            }

            var qualified = value[DataDatabase.Length..];
            var dot = qualified.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0 || dot == qualified.Length - 1)
            {
                throw new InvalidOperationException(
                    $"The sample flow '{document}.yaml' names the table '{value}', which is not [database].[schema].[table]; the chain fixture cannot point it at the test database.");
            }

            var layer = qualified[..dot];
            var schema = layer switch
            {
                "pre" => "pre_" + suffix,
                "silver" => "silver_" + suffix,
                _ => throw new InvalidOperationException(
                    $"The sample flow '{document}.yaml' names the schema '{layer}', which the chain fixture has no schema of its own for; it creates only a pre and a silver schema."),
            };

            lines[i] = $"{line[..at]}{Key} \"[{databaseName}].[{schema}].[{qualified[(dot + 1)..]}]\"";
        }

        var rewritten = string.Join('\n', lines);
        if (rewritten.Contains(DataDatabase, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The sample flow '{document}.yaml' still names {DataDatabase.TrimEnd('.')} somewhere the chain fixture does not rewrite, so the generated estate would read the repository's own tables. Rewrite that value too.");
        }

        return rewritten;
    }

    /// <summary>
    /// Substitutes one piece of a shipped document, refusing loudly when the document no longer holds it. A silent miss
    /// would leave the estate pointing at the sample's own database or at a real endpoint, which is the one failure this
    /// fixture must never have.
    /// </summary>
    private static string Replace(string text, string old, string replacement, string document)
    {
        if (!text.Contains(old, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The sample flow '{document}.yaml' no longer holds the text the chain fixture rewrites, so the generated estate would not name the test database. Expected to find: {old.Trim()}");
        }

        return text.Replace(old, replacement, StringComparison.Ordinal);
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(
                $"The sample estate was not copied next to the test binaries ({source}). It is a Content item of this test project; rebuild the suite.");
        }

        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void Delete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A run artifact a reader still holds open is left for the operating system's own cleanup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
