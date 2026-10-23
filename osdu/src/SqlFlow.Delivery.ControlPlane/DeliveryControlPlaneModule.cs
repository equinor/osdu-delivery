using Microsoft.Extensions.Logging;
using SqlFlow.Delivery.Catalog;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog.Modules;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.ControlPlane.ManagedWorkers;
using SqlFlow.Dispatch.ManagedWorkers;
using SqlFlow.Delivery.Telemetry;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.ControlPlane;

/// <summary>
/// The OSDU module as a control plane composes it: the delivery, retrieval, cache, assertion, dimension and inventory
/// flow kinds (with the mapping and dictionary documents they read), their executors and compute operations, the ledger
/// over the module's own database (schema <c>osdu</c>), the mapping and cache documents the repository sync reconciles
/// beside the flows, the OSDU data definitions the Templates page browses, the background work that carries approved
/// cache changes out, reads each partition's current cache version ahead of its renders, and probes the targets on a
/// schedule, the delivery search category, and every delivery endpoint on the
/// control plane's own authenticated route groups.
/// </summary>
/// <remarks>
/// A host passes this to <c>ControlPlaneHost.RunAsync</c>; nothing else about the control plane changes. Every service
/// here is registered after SQLFlow's own, so the module extends the platform rather than replacing it.
/// </remarks>
public sealed class DeliveryControlPlaneModule : IControlPlaneModule
{
    /// <summary>The module's name: what the platform calls it in errors, and the name its database is registered under.</summary>
    public string Name => OsduSchema.Module;

    public void ConfigureServices(ControlPlaneModuleServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var rollout = services.AddOptions<CacheRolloutOptions>(CacheRolloutOptions.SectionName, options => options.Validate());
        var warmup = services.AddOptions<CacheWarmupOptions>(CacheWarmupOptions.SectionName, options => options.Validate());
        var repository = services.AddOptions<SchemaRepositoryOptions>(SchemaRepositoryOptions.SectionName, options => options.Validate());
        var probe = services.AddOptions<TargetProbeOptions>(TargetProbeOptions.SectionName, options => options.Validate());
        var telemetry = services.AddOptions<TelemetryOptions>(TelemetryOptions.SectionName, options => options.Validate());
        services.AddOptions<ConfiguredPartitionsOptions>(ConfiguredPartitionsOptions.SectionName, options => options.Validate());

        // The module's database: the osdu schema in the catalog's database, on the catalog's connection. Bootstrap migrates
        // it after the catalog, and readiness stays red until it verifies against this build.
        OsduModuleDatabase.RefuseRetiredSettings(name => services.Configuration.GetSection(name).Exists()
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));
        services.AddDatabase(OsduModuleDatabase.Create());

        // Where a synced repository's files are on this host, which a template is read from outside a run of its own
        // repository: this control plane's catalog says where each repository's last sync put them. Registered before the
        // kind, whose own registration reads the catalog a node or the command line names.
        services.Services.AddSingleton<IRepositoryFolders>(provider => new CatalogRepositoryFolders(
            async (repoId, ct) =>
            {
                await using var scope = provider.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
                var catalog = scope.ServiceProvider.GetRequiredService<SqlFlow.Catalog.CatalogDbContext>();
                return await RepositorySync.ReadAsync(catalog, repoId, ct).ConfigureAwait(false);
            },
            provider.GetRequiredService<SqlFlow.Core.Secrets.ISecretResolver>()));

        // The kinds, their executors and the compute operations a node runs for this control plane, plus the ledger
        // and the caches over the module database's context factory.
        services.Services.AddDeliveryKind();
        services.Services.AddDeliveryLedger();

        // The record search reads the partition the workbench works in from the request it answers.
        services.Services.AddHttpContextAccessor();

        // One module context per request, opened from the module database's factory, so an endpoint reads the osdu
        // tables exactly as the ledger does and the catalog's own context stays SQLFlow's.
        services.Services.AddScoped(provider => provider.GetRequiredService<IDbContextFactory<OsduDbContext>>().CreateDbContext());

        // The values set for no partition reach every run of every kind, and every compute task, through the control
        // plane's own reference source: a pre-ingestion or ingestion flow's connections as much as a delivery's platform.
        services.Services.AddSingleton<IRunReferenceSource, CentralConfigurationReferences>();

        // Every run reaches the queue through the dispatcher, whether an endpoint, a schedule or a fan-out group put it
        // there, so the partitions' values are attached by decorating it rather than at any one caller.
        RunDispatcherDecoration.Decorate(services.Services, (inner, provider) => new ConfiguredRunDispatcher(
            inner, provider.GetRequiredService<DeliveryConfigStore>()));

        // The delivery records category of the control plane's search: resolved per request, beside the built-in ones.
        services.Services.AddScoped<ISearchContributor, RecordSearchContributor>();

        // The OSDU data definitions (the Open Group's public repository of OSDU schemas) the Templates page browses and
        // downloads template files from, kept as a local copy: each release downloaded once as one archive and read from disk
        // after that, the release list read again when it is older than RefreshMinutes or when someone syncs.
        services.Services.AddHttpClient(OsduDataDefinitions.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.Services.AddSingleton(provider => new OsduDataDefinitions(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient(OsduDataDefinitions.HttpClientName),
            repository.ApiUrl,
            repository.WebUrl,
            string.IsNullOrWhiteSpace(repository.CacheDirectory) ? OsduDataDefinitions.DefaultCacheDirectory : repository.CacheDirectory,
            TimeSpan.FromMinutes(repository.RefreshMinutes),
            TimeSpan.FromMinutes(repository.DownloadTimeoutMinutes),
            provider.GetRequiredService<TimeProvider>()));

        // Their example records are what a check's guidance quotes as OSDU's own way of writing a value.
        services.Services.AddSingleton<IOfficialExamples>(provider => provider.GetRequiredService<OsduDataDefinitions>());

        // OSDU's lineage by release, built from the same local copy and kept in the module database, which the OSDU order
        // reads a kind from when no template folder holds its template, and the order pages draw.
        services.Services.AddSingleton(provider => new Ordering.SchemaLineageStore(
            provider.GetRequiredService<IDbContextFactory<OsduDbContext>>().CreateDbContext));
        services.Services.AddSingleton(provider => new Ordering.SchemaLineageBuilder(
            provider.GetRequiredService<OsduDataDefinitions>(),
            provider.GetRequiredService<Ordering.SchemaLineageStore>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<Ordering.SchemaLineageBuilder>()));

        if (repository.WarmOnStart)
        {
            services.AddHostedService<DataDefinitionsWarmupService>();
        }

        // The central configuration the deployment's configuration sets (its partitions with their values, and the values set
        // for no partition) is written by the module's step of the start-up pass, after the module's database is verified, so
        // the control plane comes up with it; a configuration that names none leaves the registry as it is.
        // OSDU Delivery works in partitions, so its start-up is done (/health/startup) only once they are written and at least
        // one is registered. It is not part of readiness: a control plane without a partition still serves the API, where a
        // person reads why and registers one.
        services.Services.AddSingleton<PartitionReadiness>();
        services.Services.AddHealthChecks().AddCheck<PartitionReadiness>("osdu-partitions", tags: [BootstrapReadiness.Tag]);
        services.Services.AddSingleton<IBootstrapStep, CentralConfigurationStep>();

        // The pipelines synced before the read model of sources and interfaces existed are described once at start, so a
        // record's page finds its flow without waiting for its repository's next sync.
        services.AddHostedService<InterfaceCatalogBackfillService>();

        // The search terms are written again from the module's own rows when the control plane starts, so terms extracted by
        // an older version, or never extracted, are this version's without waiting for a repository's next sync.
        services.AddHostedService<SearchTermCatalogRefreshService>();

        // The records a ledger held before the identity index existed are indexed in the background, so looking a
        // record up by a wellbore id or a file name answers for the whole ledger, not only for what has been planned
        // since. Records staged from now on write their own rows as they are staged.
        services.AddHostedService<RecordIdentityBackfillService>();

        // The records blocked before the ledger kept problems are sorted into theirs in the background, so the Problems
        // view groups every blocked record, not only those blocked since. Records blocked from now on are sorted as they
        // are blocked.
        services.AddHostedService<RecordProblemBackfillService>();

        if (rollout.Enabled)
        {
            services.AddHostedService<CacheUpdateRolloutService>();
        }

        // The current version of every partition's cache is read into memory before anyone asks for it, so a record's Render,
        // a flow's Preview and the runs this control plane runs itself find it there rather than waiting while it is read.
        services.Services.AddDeliveryCacheWarmup(warmup);

        // Asking every flow's OSDU whether it still answers costs a token exchange and a live request per interface, so
        // the schedule runs only where a deployment has asked for it; the operator's probe button is always there.
        if (probe.Enabled)
        {
            services.AddHostedService<ScheduledTargetProbeService>();
        }

        // Where the metrics go, when a deployment says anywhere. The meters publish either way, so this changes only
        // whether the measurements leave the process (osdu/docs/reference/concepts/run-trace-and-metrics.md,
        // "Metrics").
        services.Services.AddDeliveryMetricsExport(telemetry);

        AddWorkerLaunchers(services);
    }

    /// <summary>
    /// The launchers this module offers SQLFlow's managed workers (osdu/docs/reference/guides/managed-workers.md): Radix
    /// jobs for an estate on Radix, and local processes for <c>dev.bat</c> and <c>./dev.sh</c>. Both are registered while managed
    /// workers are on, so a misnamed launcher is refused naming them; only the selected one's settings are checked.
    /// </summary>
    private static void AddWorkerLaunchers(ControlPlaneModuleServices services)
    {
        var managed = services.Configuration.GetSection(ManagedWorkersOptions.Section).Get<ManagedWorkersOptions>()
            ?? new ManagedWorkersOptions();
        if (!managed.Enabled)
        {
            return;
        }

        var selected = managed.Launcher?.Trim() ?? string.Empty;
        var radix = services.AddOptions<RadixLauncherOptions>(RadixLauncherOptions.SectionName, options =>
        {
            if (string.Equals(selected, RadixWorkerLauncher.LauncherName, StringComparison.OrdinalIgnoreCase))
            {
                options.Validate(managed);
            }
        });
        var process = services.AddOptions<ProcessLauncherOptions>(ProcessLauncherOptions.SectionName, options =>
        {
            if (string.Equals(selected, ProcessWorkerLauncher.LauncherName, StringComparison.OrdinalIgnoreCase))
            {
                options.Validate();
            }
        });

        services.Services.AddHttpClient(RadixWorkerLauncher.HttpClientName, client =>
        {
            if (radix.JobSchedulerUrl is { } url)
            {
                // A trailing slash, so the scheduler's routes resolve beneath the configured address.
                client.BaseAddress = url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");
            }

            // Each call carries its own timeout (RequestTimeoutSeconds), so one default never cuts a call short.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.Services.AddSingleton<IWorkerLauncher>(provider => new RadixWorkerLauncher(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient(RadixWorkerLauncher.HttpClientName), radix,
            provider.GetRequiredService<TimeProvider>(), Environment.GetEnvironmentVariable(RadixWorkerLauncher.CommitVariable)));
        // A local worker opens the module database this control plane opens: the catalog's, on the connection the control
        // plane resolved for it, whichever way the control plane was given it.
        services.Services.AddSingleton<IWorkerLauncher>(provider => new ProcessWorkerLauncher(
            process, managed.MaxLifetimeHours, provider.GetRequiredService<TokenIssuer>(), provider.GetRequiredService<IServer>(),
            () => provider.GetRequiredService<IModuleDatabaseConnections>()
                .ConnectionString(provider.GetRequiredService<ModuleDatabase<OsduDbContext>>()),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<ProcessWorkerLauncher>>()));
    }

    public void MapEndpoints(ControlPlaneModuleEndpoints endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Reading the ledger, the mappings, the caches, the templates and the search terms is any signed-in user's; running
        // something (a redelivery, a removal, a data definitions sync) is an operate action; refining a search term changes
        // what every reader searches by, so it is an author action, as SQLFlow's own proposals are. A template is added or
        // removed only by its repository and that repository's sync. Of the reads, the ones the chat assistant's delivery tools call are
        // opened to an assistant run's delegated token, which SQLFlow refuses everywhere else (DeliveryAssistantAccess).
        endpoints.Read
            .OpenToAssistantRuns()
            .MapDeliveryReadEndpoints()
            .MapDeliveryTemplateReadEndpoints()
            .MapDeliverySearchTermReadEndpoints()
            .MapDeliveryOrderReadEndpoints();
        endpoints.Operate
            .MapDeliveryWriteEndpoints()
            .MapDeliveryTemplateOperateEndpoints()
            .MapDeliveryOrderOperateEndpoints();
        endpoints.Author
            .MapDeliverySearchTermAuthorEndpoints();
    }
}
