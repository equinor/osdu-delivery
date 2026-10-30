using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Listeners;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using SqlFlow.Execution;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// The delivery module's composition root. Every host (the control plane, a worker node, the CLI) calls
/// <see cref="AddDeliveryKind"/> after the platform's <c>AddSqlFlowEngine</c>, so the kinds, their executors and their
/// compute operations are wired the same way everywhere; a host that has the module's database connection also calls
/// <see cref="AddDeliveryLedger(IServiceCollection, Func{IServiceProvider, Func{OsduDbContext}?})"/>, which is what
/// turns plan-only into deliver and gives the ledger and the caches.
/// </summary>
public static class DeliveryServices
{
    /// <summary>What a host is told when an operation needs the module's database and it has none.</summary>
    public const string NoLedgerMessage =
        "This host has no osdu database connection; the delivery ledger and caches are unavailable. "
        + "Without them only validation and planning against the flow document are available, since a render reads any cache it reads from that database. "
        + OsduModuleDatabase.StartWithCatalogConnection;

    public static IServiceCollection AddDeliveryKind(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Storage: the platform's file stores list and read forward; these writers and readers add the streamed
        // writes, the seekable reads and the range reads the engine needs.
        services.AddSingleton<IFileWriter, LocalFileWriter>();
        services.AddSingleton<IFileWriter, AzureBlobFileWriter>();
        services.AddSingleton<IFileReader, LocalFileReader>();
        services.AddSingleton<IFileReader, AzureBlobFileReader>();
        services.AddSingleton(sp => new FileStoreRegistry(
            sp.GetServices<IFileStore>(), sp.GetServices<IFileWriter>(), sp.GetServices<IFileReader>()));
        services.TryAddSingleton<IPayloadFiles>(sp => new StoragePayloadFiles(sp.GetRequiredService<FileStoreRegistry>()));

        // The ingestion tables a flow reads, opened on the node with the flow's own connection reference and logging to
        // whoever opens them: a run's own log and live trace.
        services.TryAddSingleton<IIngestionSourceFactory>(sp => new SqlServerIngestionSourceFactory(sp.GetRequiredService<ISecretResolver>()));

        // Documents: the delivery loader behind the platform's envelope probe.
        services.AddSingleton<DeliveryDocumentLoader>();
        // The delivery kind also owns the mapping documents its flows pin, so the one instance is registered both as a flow
        // kind and as the companion kind the loader, the CLI and the proposal preflight read mappings through; the dictionary
        // documents cache flows hold are a companion kind of their own. Its lineage reads the templates those mappings fill
        // from the template folders on each mapping's path in the checkout it scans, as a run reads them.
        services.AddSingleton(sp => new DeliveryFlowKind(
            sp.GetRequiredService<DeliveryDocumentLoader>(),
            new MappingTemplateSource(),
            () => sp.GetService<DeliveryLedgerSource>()?.OrderCatalog(sp)));
        services.AddSingleton<IFlowDocumentKind>(sp => sp.GetRequiredService<DeliveryFlowKind>());
        services.AddSingleton<ICompanionDocumentKind>(sp => sp.GetRequiredService<DeliveryFlowKind>());
        services.AddSingleton<ICompanionDocumentKind, DictionaryDocumentKind>();
        services.AddSingleton<ICompanionDocumentKind, RepositoryDocumentKind>();
        services.AddSingleton<IFlowDocumentKind, RetrievalFlowKind>();
        services.AddSingleton<IFlowDocumentKind, CacheFlowKind>();
        services.AddSingleton<IFlowDocumentKind, AssertionFlowKind>();
        services.AddSingleton<IFlowDocumentKind, DimensionFlowKind>();
        services.AddSingleton<IFlowDocumentKind, InventoryFlowKind>();
        services.AddSingleton<IFlowDocumentKind, RemovalFlowKind>();
        services.AddSingleton<IFlowDocumentKind, DownloadFlowKind>();
        // The repository sync's delivery half, whose rows ride the catalog's transaction: the osdu schema is in its database.
        // Once the sync committed, it makes the ledger moves it asked with this host's ledger, when the host has one.
        services.AddSingleton<ICatalogSyncExtension>(sp => new DeliveryCatalogSync(
            sp.GetRequiredService<DeliveryDocumentLoader>(),
            sp.GetService<ISecretResolver>(),
            () => sp.GetService<DeliveryLedgerSource>()?.Open(sp)));
        // Where a synced repository's files are on this host, which a template read outside a run of its own repository is
        // read from: the catalog this host names says where each repository's last sync put them. The control plane
        // registers its own catalog's before this.
        services.TryAddSingleton<IRepositoryFolders>(sp => CatalogRepositoryFolders.FromEnvironment(sp.GetRequiredService<ISecretResolver>()));

        // Protocols and the completion callback. The logging listener is always on; hosts add their own (a live
        // feed, metrics, a webhook) by registering more IDeliveryListener instances.
        services.TryAddSingleton<IProtocolFactory, DefaultProtocolFactory>();
        services.TryAddSingleton<IRecordSearchFactory>(PlatformRecordSearchFactory.Instance);
        services.AddSingleton<IDeliveryListener, LoggingDeliveryListener>();

        // The connections to OSDU a person's reads keep between calls (a record read back, a probe, the explorer).
        services.TryAddSingleton(sp => new TargetClients(sp.GetRequiredService<TimeProvider>()));

        // What each partition's Schema service holds, read once and kept, which the explorer's Referenced by answers from.
        services.TryAddSingleton(sp => new PartitionSchemaIndexes(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILoggerFactory>()));

        services.AddSingleton(sp => new EngineContext(
            sp.GetRequiredService<DeliveryDocumentLoader>(),
            sp.GetRequiredService<IIngestionSourceFactory>(),
            sp.GetRequiredService<IPayloadFiles>(),
            sp.GetRequiredService<FileStoreRegistry>(),
            sp.GetRequiredService<ISecretResolver>(),
            sp.GetService<DeliveryLedgerSource>()?.Open(sp),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IProtocolFactory>(),
            new CompositeDeliveryListener(sp.GetServices<IDeliveryListener>()),
            // The fan-out belongs to one run: the executor attaches the one the platform handed that run.
            FanOut: null,
            sp.GetService<DeliveryLedgerSource>()?.Cache(sp),
            sp.GetRequiredService<IRecordSearchFactory>(),
            Partitions: sp.GetService<DeliveryLedgerSource>()?.Partitions(sp),
            Clients: sp.GetRequiredService<TargetClients>(),
            // The OSDU data definitions' example records, which a check's guidance quotes; a host that keeps no copy of
            // the data definitions (a node) registers none, and guidance there quotes the schema alone.
            Examples: sp.GetService<IOfficialExamples>(),
            SchemaIndexes: sp.GetRequiredService<PartitionSchemaIndexes>())
        {
            RepositoryFolders = sp.GetRequiredService<IRepositoryFolders>(),
        });

        // Execution: the run executors behind the platform's document executor, the compute operations a node runs for the
        // control plane (a value check and a removal), and the operations the control plane runs itself.
        services.TryAddSingleton<PartitionLedgers>();
        services.AddSingleton<IFlowDocumentExecutor, DeliveryExecutor>();
        services.AddSingleton<IFlowDocumentExecutor, RetrievalExecutor>();
        services.AddSingleton<IFlowDocumentExecutor, CacheExecutor>();
        services.AddSingleton<IFlowDocumentExecutor, AssertionExecutor>();
        services.AddSingleton<IFlowDocumentExecutor, DimensionExecutor>();
        services.AddSingleton<IFlowDocumentExecutor, InventoryExecutor>();
        services.AddSingleton<IFlowDocumentExecutor, RemovalExecutor>();
        services.AddSingleton<IFlowDocumentExecutor, DownloadExecutor>();

        // What a removal flow reads its keys through: SQLFlow's own source readers, connection resolution, connection factory and
        // SQL dialects, as the platform registered them, so a removal reads every format and source system SQLFlow reads.
        services.TryAddSingleton(sp => new Removals.RemovalKeySources(
            sp.GetServices<SqlFlow.Core.Abstractions.ISourceReader>(),
            sp.GetRequiredService<SqlFlow.Core.Connections.IConnectionResolver>(),
            sp.GetRequiredService<SqlFlow.Core.Connections.IConnectionFactory>(),
            sp.GetRequiredService<SqlFlow.Core.Connections.SourceProviderRegistry>(),
            sp.GetRequiredService<ISecretResolver>()));
        // What scans long is a node task: a value check across a scope. A removal writes to OSDU and the ledger, so it is a
        // run of the flow (DeliveryOperations.Remove), listed with its runs.
        services.AddSingleton<IComputeOperation, CheckValuesOperation>();
        services.AddSingleton<IComputeOperation, ExportValueIssuesOperation>();

        // What a person asks and waits on runs in the process asked (DirectOperations): a probe, a record read back, a
        // record's source rows, a preview, a scope's values and the explorer's reads.
        services.AddSingleton<DeliveryOperation, ProbeTargetOperation>();
        services.AddSingleton<DeliveryOperation, ReadRecordOperation>();
        services.AddSingleton<DeliveryOperation, ReadSourceRowOperation>();
        services.AddSingleton<DeliveryOperation, PreviewRecordOperation>();
        services.AddSingleton<DeliveryOperation, ScopeValuesOperation>();
        services.AddSingleton<DeliveryOperation, ExploreOperation>();
        services.AddSingleton<DirectOperations>();

        return services;
    }

    /// <summary>
    /// Wires the ledger and the caches over the module's own database (schema <c>osdu</c>).
    /// <paramref name="contexts"/> decides, once the host is built, whether that database is configured and, when it is,
    /// opens a fresh context per operation; the ledger disposes what it opens.
    /// </summary>
    public static IServiceCollection AddDeliveryLedger(this IServiceCollection services, Func<IServiceProvider, Func<OsduDbContext>?> contexts)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contexts);
        services.AddSingleton(new DeliveryLedgerSource(contexts));
        services.AddSingleton<ILedger>(sp => sp.GetRequiredService<DeliveryLedgerSource>().Open(sp)
            ?? throw new DeliveryException(NoLedgerMessage));
        services.AddSingleton<ICacheStore>(sp => sp.GetRequiredService<DeliveryLedgerSource>().Cache(sp)
            ?? throw new DeliveryException(NoLedgerMessage));

        // The central configuration is registered for every host, not the control plane alone: the control plane reads it
        // to supply the runs it queues, and the CLI reads and writes it so a deployment can configure an estate.
        services.AddSingleton(sp => new Catalog.DeliveryConfigStore(sp.GetRequiredService<DeliveryLedgerSource>().Contexts(sp)));

        // The partition registry: which partitions a flow that names none serves, and the default a run that names none runs in.
        // One registry per host, the one the engine reads: a host without the module's database gets one that says so when used.
        services.AddSingleton(sp => sp.GetRequiredService<DeliveryLedgerSource>().Partitions(sp) ?? new DeliveryPartitionRegistry(null));
        services.AddSingleton<IPartitionRegistry>(sp => sp.GetRequiredService<DeliveryPartitionRegistry>());
        return services;
    }

    /// <summary>
    /// Reads the current version of every partition's cache ahead of the renders that need it (<see cref="CacheWarmupService"/>),
    /// unless <paramref name="options"/> turn it off: what the control plane registers, whose renders are a person's and, in a
    /// single-node estate, every run's, and what a node registers, which runs runs. A host that never renders (a CLI command)
    /// registers nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting is outside its range; the message names it.</exception>
    public static IServiceCollection AddDeliveryCacheWarmup(this IServiceCollection services, CacheWarmupOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (options.Enabled)
        {
            services.AddSingleton(options);
            services.AddHostedService<CacheWarmupService>();
        }

        return services;
    }

    /// <summary>
    /// Wires the ledger over the module database the host registered with the platform's module-database support, which
    /// migrates it, records its version and hands out contexts through an <see cref="IDbContextFactory{TContext}"/>. A
    /// host without that registration gets a module that plans but does not deliver.
    /// </summary>
    public static IServiceCollection AddDeliveryLedger(this IServiceCollection services)
        => services.AddDeliveryLedger(sp =>
        {
            var factory = sp.GetService<IDbContextFactory<OsduDbContext>>();
            return factory is null ? null : factory.CreateDbContext;
        });
}

/// <summary>The module database the host registered: resolved once, the first time the ledger is needed.</summary>
public sealed class DeliveryLedgerSource
{
    private readonly Func<IServiceProvider, Func<OsduDbContext>?> _contexts;
    private readonly Lock _gate = new();
    private bool _resolved;
    private Func<OsduDbContext>? _factory;
    private ILedger? _ledger;
    private OsduCacheStore? _cache;
    private DeliveryPartitionRegistry? _partitions;
    private Ordering.SchemaLineageStore? _lineage;
    private Ordering.OrderCatalog? _order;

    public DeliveryLedgerSource(Func<IServiceProvider, Func<OsduDbContext>?> contexts)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        _contexts = contexts;
    }

    /// <summary>The ledger, or null when the host has no osdu database connection.</summary>
    public ILedger? Open(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Resolve(provider);
        return _ledger;
    }

    /// <summary>The cache store, or null when the host has no osdu database connection.</summary>
    public ICacheStore? Cache(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Resolve(provider);
        return _cache;
    }

    /// <summary>The partition registry, or null when the host has no osdu database connection.</summary>
    public DeliveryPartitionRegistry? Partitions(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Resolve(provider);
        return _partitions;
    }

    /// <summary>The store of OSDU's lineage by release, or null when the host has no osdu database connection.</summary>
    public Ordering.SchemaLineageStore? Lineage(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Resolve(provider);
        return _lineage;
    }

    /// <summary>What the catalog knows for the OSDU order (the built releases of OSDU's lineage), or null without an osdu database.</summary>
    public Ordering.IOrderCatalog? OrderCatalog(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Resolve(provider);
        return _order;
    }

    /// <summary>The context factory, or null when the host has no osdu database connection.</summary>
    public Func<OsduDbContext>? Contexts(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        Resolve(provider);
        return _factory;
    }

    private void Resolve(IServiceProvider provider)
    {
        lock (_gate)
        {
            if (!_resolved)
            {
                _factory = _contexts(provider);
                _ledger = _factory is null ? null : new OsduLedger(_factory, provider.GetRequiredService<TimeProvider>());
                _cache = _factory is null ? null : new OsduCacheStore(_factory);
                _partitions = _factory is null ? null : new DeliveryPartitionRegistry(_factory);
                _lineage = _factory is null ? null : new Ordering.SchemaLineageStore(_factory);
                _order = _factory is null ? null : new Ordering.OrderCatalog(_factory);
                _resolved = true;
            }
        }
    }
}
