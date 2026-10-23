using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.Catalog.Modules;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core.Hosting;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.ControlPlane.Configuration;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Hosting;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Tests;
using SqlFlow.Execution;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The OSDU module as a control plane composes it: what it registers, what it maps, what it declares about its own
/// database, and the search category it contributes. None of this needs a database: the host binds the module lazily, so
/// it boots and answers the no-database surface (authorization, options, the registrations themselves) without SQL Server.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryModuleTests
{
    [Fact]
    public async Task TheModule_RegistersItsKinds_ExecutorsComputeOperationsAndDirectOperations()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();

        var kinds = factory.Services.GetServices<IFlowDocumentKind>().Select(k => k.FlowType).ToList();
        var executors = factory.Services.GetServices<IFlowDocumentExecutor>().ToList();
        var operations = factory.Services.GetServices<IComputeOperation>().Select(o => o.Name).ToList();

        Assert.Contains(FlowDefinition.FlowTypeName, kinds);
        Assert.Contains(RetrievalDefinition.FlowTypeName, kinds);
        Assert.Contains(CacheDefinition.FlowTypeName, kinds);
        Assert.Contains(AssertionFlowDefinition.FlowTypeName, kinds);
        Assert.Contains(DimensionFlowDefinition.FlowTypeName, kinds);
        Assert.Contains(InventoryFlowDefinition.FlowTypeName, kinds);
        Assert.Contains(RemovalFlowDefinition.FlowTypeName, kinds);
        Assert.Equal(7, executors.Count);
        Assert.Contains(executors, e => e is InventoryExecutor);
        Assert.Contains(executors, e => e is RemovalExecutor);

        // A node runs only what scans or writes (a value check, a removal); what a person asks for and waits on is run by the
        // control plane itself, and is never offered to a node.
        Assert.Equal(["delivery-check-values", "delivery-delete"], operations.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(
            ["delivery-explore", "delivery-preview", "delivery-probe", "delivery-read", "delivery-scope-values", "delivery-source"],
            factory.Services.GetRequiredService<DirectOperations>().Names.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task TheModule_RegistersItsCompanionDocuments_SoTheLoaderAndTheProposalPreflightReadMappingsAndDictionaries()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();

        // The delivery kind owns the mapping documents; the dictionary documents cache flows hold are a companion of their own.
        var companions = factory.Services.GetServices<ICompanionDocumentKind>().Select(k => k.DocumentType).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([DictionaryDefinition.DocumentTypeName, MappingDefinition.DocumentTypeName], companions);
        Assert.Same(
            factory.Services.GetServices<IFlowDocumentKind>().OfType<DeliveryFlowKind>().Single(),
            factory.Services.GetServices<ICompanionDocumentKind>().OfType<DeliveryFlowKind>().Single());

        // The loader every consumer shares reads both under their own type.
        var loader = factory.Services.GetRequiredService<YamlDocumentLoader>();
        Assert.Equal([DictionaryDefinition.DocumentTypeName, MappingDefinition.DocumentTypeName], loader.CompanionKinds.Select(k => k.DocumentType).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheModule_JoinsTheRepositorySync_SoItsMappingAndCacheDocumentsAreReconciledWithTheFlows()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();

        var extension = Assert.Single(factory.Services.GetServices<ICatalogSyncExtension>());

        Assert.IsType<SqlFlow.Delivery.Catalog.DeliveryCatalogSync>(extension);
    }

    [Fact]
    public async Task TheModule_RegistersItsDatabase_UnderItsOwnNameAndSchema()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();

        var database = Assert.Single(factory.Services.GetServices<ModuleDatabase>());

        Assert.Equal(OsduSchema.Module, database.Module);
        Assert.Equal(DeliveryModel.SchemaName, database.Schema);
        Assert.Equal(OsduDbContext.ModuleVersion, database.Version);
        Assert.Equal(OsduDbContext.MinimumCatalogMigration, database.MinimumCatalogMigration);
        // No connection of its own: the osdu schema lives in the catalog's database unless a deployment says otherwise.
        Assert.Null(database.ConnectionReference);
        Assert.Equal("module 'osdu' (schema 'osdu', catalog connection)", database.Describe());
    }

    [Fact]
    public async Task TheModulesEndpoints_AreMappedOnTheAuthenticatedGroups()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();

        // Mapped, and behind the control plane's own policies: an anonymous caller is refused rather than answered 404.
        foreach (var path in new[]
        {
            $"/api/v1/delivery/flows/{Guid.NewGuid():D}/stats",
            "/api/v1/delivery/templates",
            "/api/v1/delivery/mapping-builder/repos",
        })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheModulesBackgroundWork_RunsWithTheHost_AndIsSwitchedOffByItsOwnOptions()
    {
        await using (var on = Host())
        {
            using var client = on.CreateClient();
            var hosted = on.Services.GetServices<IHostedService>().ToList();
            Assert.Contains(hosted, service => service is CacheUpdateRolloutService);
            Assert.Contains(hosted, service => service is CacheWarmupService);
            // The data definitions warm-up is off in these tests: it would reach the public schema repository.
            Assert.DoesNotContain(hosted, service => service is DataDefinitionsWarmupService);
        }

        await using var off = Host()
            .WithSetting("Osdu:CacheRollout:Enabled", "false")
            .WithSetting("Osdu:CacheWarmup:Enabled", "false");
        using var offClient = off.CreateClient();
        var offHosted = off.Services.GetServices<IHostedService>().ToList();

        Assert.DoesNotContain(offHosted, service => service is CacheUpdateRolloutService);
        Assert.DoesNotContain(offHosted, service => service is CacheWarmupService);
    }

    [Fact]
    public async Task ACacheWarmupLookingTooOften_StopsTheHost_NamingTheSetting()
    {
        await using var factory = Host().WithSetting("Osdu:CacheWarmup:PollSeconds", "1");

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var module = Module(error);
        Assert.Equal(OsduSchema.Module, module.ModuleName);
        Assert.Contains("Osdu:CacheWarmup:PollSeconds must be between 5 and 3600", module.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModuleOptionsOutOfRange_StopTheHost_NamingTheModuleAndTheSetting()
    {
        await using var factory = Host().WithSetting("Osdu:CacheRollout:BatchSize", "0");

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var module = Module(error);
        Assert.Equal(OsduSchema.Module, module.ModuleName);
        Assert.Contains("Osdu:CacheRollout", module.Message, StringComparison.Ordinal);
        Assert.Contains("BatchSize must be positive", module.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHost_CarriesTheProductsBranding()
    {
        await using var factory = Host().WithBranding(OsduDeliveryBranding.Product);
        using var client = factory.CreateClient();

        var branding = factory.Services.GetRequiredService<ProductBranding>();

        Assert.Equal("OSDU Delivery", branding.ProductName);
        Assert.Equal("OSDU Delivery, powered by SQLFlow", branding.Lockup);
        Assert.False(branding.IsSqlFlow);
    }

    /// <summary>
    /// The module's database declaration: its own connection is a secret reference or nothing at all, and the version row
    /// its migrate records is the one status and startup verification read back.
    /// </summary>
    [Fact]
    public async Task TheModuleDatabase_TakesAReferenceOrTheCatalogConnection_AndRoundTripsItsVersionRow()
    {
        var own = OsduModuleDatabase.Create(OsduModuleDatabase.EnvironmentReference);
        Assert.Equal("${env:SQLFLOW_OSDU_DB}", own.ConnectionReference);
        Assert.Equal("module 'osdu' (schema 'osdu', own connection)", own.Describe());

        // A connection string where a reference belongs usually carries a password, so it is refused without echoing it.
        var refused = Assert.Throws<ModuleDatabaseException>(() => OsduModuleDatabase.Create("Server=db;User ID=sa;Password=hunter2"));
        Assert.Equal(OsduSchema.Module, refused.Module);
        Assert.DoesNotContain("hunter2", refused.Message, StringComparison.Ordinal);

        // The configured option wins over the environment, and neither means the catalog's own database.
        Assert.Equal("${keyvault:v/osdu}", OsduModuleDatabase.ResolveReference("${keyvault:v/osdu}", _ => "ignored"));
        Assert.Equal(OsduModuleDatabase.EnvironmentReference, OsduModuleDatabase.ResolveReference(null, _ => "Server=..."));
        Assert.Null(OsduModuleDatabase.ResolveReference(null, _ => null));

        // The after-migrate and read-version hooks are the module's own, and they keep exactly one row.
        var database = OsduModuleDatabase.Create();
        using var osdu = new OsduTestDatabase();
        await using var context = osdu.CreateDbContext();
        var applied = new ModuleMigrationApplied(
            OsduSchema.Module, "1.0.0", "20260915214130_InitialOsduSchema", "control plane module tests", new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc),
            OsduDbContext.MinimumCatalogMigration);
        await database.AfterMigrate!(context, applied, CancellationToken.None);
        await database.AfterMigrate!(context, applied with { AppliedBy = "again" }, CancellationToken.None);
        var recorded = await database.ReadRecordedVersion!(context, CancellationToken.None);

        Assert.Equal(new ModuleRecordedVersion("1.0.0", "20260915214130_InitialOsduSchema"), recorded);
        var row = Assert.Single(await context.SchemaVersions.AsNoTracking().ToListAsync());
        Assert.Equal(1, row.Id);
        Assert.Equal("again", row.AppliedBy);
        Assert.Equal(OsduDbContext.MinimumCatalogMigration, row.MinimumCatalogMigration);
    }

    /// <summary>
    /// The records category the module contributes to the control plane's search: the ledger's own indexed lookup, with
    /// the record's detail in the hit's data (which is where the GUI's renderer reads it) and the route its page opens at.
    /// </summary>
    [Fact]
    public async Task TheSearchCategory_AnswersFromTheLedger_WithTheRecordsDetailAndItsRoute()
    {
        using var database = new OsduTestDatabase();
        var ledger = database.Ledger();
        var flowName = "wells-wellbore-03-header-delivery";
        var flowId = FlowId.Of(flowName);
        await ledger.RegisterAsync(flowId);
        var pipelineId = Guid.NewGuid();
        var repoId = Guid.NewGuid();
        await using (var db = database.CreateCatalogContext())
        {
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = pipelineId,
                RepoId = repoId,
                Name = flowName,
                Kind = FlowDefinition.FlowTypeName,
                RelativePath = "flows/wells-wellbore-03-header-delivery.yaml",
                ContentHash = new string('0', 64),
                Yaml = "flowType: delivery",
                DefinitionJson = "{}",
                Active = true,
                Wave = 0,
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        try
        {
            await AnswersFromTheLedgerAsync(database, ledger, flowName, flowId, pipelineId, repoId);
        }
        finally
        {
            // Only the module's schema is emptied between tests; the catalog row this test wrote is its own to remove.
            await using var db = database.CreateCatalogContext();
            await db.Pipelines.Where(p => p.Id == pipelineId).ExecuteDeleteAsync();
        }
    }

    private static async Task AnswersFromTheLedgerAsync(OsduTestDatabase database, OsduLedger ledger, string flowName, Guid flowId, Guid pipelineId, Guid repoId)
    {
        // The read model of sources and interfaces is what leads a ledger identity to its pipeline.
        await using (var osdu = database.CreateDbContext())
        {
            osdu.DeliveryInterfaces.Add(new DeliveryInterface
            {
                Id = Guid.NewGuid(),
                RepoId = repoId,
                FlowName = flowName,
                LedgerFlowId = flowId,
                LedgerName = flowName,
                Route = "storage",
                MappingReference = SampleEstate.WellboreMapping + "@1.0.0",
                RecordObject = "[Wells].[ing].[Wellbore]",
                RelativePath = "flows/wells-wellbore-03-header-delivery.yaml",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
            });
            await osdu.SaveChangesAsync();
        }

        var key = new DeliveryKey(Guid.NewGuid());
        await ledger.UpsertPendingAsync(
            flowId,
        [
            new RecordState
            {
                DeliveryKey = key,
                FlowId = flowId,
                SourceKey = "OSDU-DEV-1-A",
                Label = "OSDU-DEV-1-A",
                MappingName = SampleEstate.WellboreMapping,
                Status = RecordStatus.Pending,
                PendingDocumentRef = "1:0:10",
                PendingMetadata = true,
            },
        ]);

        await using var context = database.CreateCatalogContext();
        await using var module = database.CreateDbContext();
        var contributor = new RecordSearchContributor(ledger, context, module, new Microsoft.AspNetCore.Http.HttpContextAccessor());
        var contribution = await contributor.SearchAsync(
            new SearchContributionRequest("OSDU-DEV-1", ["OSDU-DEV-1"], 1, 5, new ClaimsPrincipal()), CancellationToken.None);

        Assert.Equal(RecordSearchContributor.CategoryKey, contributor.Key);
        Assert.Null(contributor.RequiredPolicy);
        Assert.Equal(1, contribution.Total);
        Assert.False(contribution.TotalCapped);
        var hit = Assert.Single(contribution.Items);
        // A key names one record per flow that reads the row, so the hit and the page it opens name the flow as well.
        Assert.Equal($"{flowId:D}/{key.Value:D}", hit.Id);
        Assert.Equal("OSDU-DEV-1-A", hit.Title);
        Assert.Equal(flowName, hit.Subtitle);
        Assert.Equal($"/delivery/records/{flowId:D}/{key.Value:D}", hit.Route);
        var record = Assert.IsType<DeliveryRecordHitDto>(hit.Data);
        Assert.Equal(key.Value, record.DeliveryKey);
        Assert.Equal(flowId, record.FlowId);
        Assert.Equal(pipelineId, record.PipelineId);
        Assert.Equal("pending", record.Status);

        // A phrase no record carries is an empty category rather than a failure.
        var none = await contributor.SearchAsync(
            new SearchContributionRequest("nothing-matches-this", ["nothing-matches-this"], 1, 5, new ClaimsPrincipal()), CancellationToken.None);
        Assert.Empty(none.Items);
        Assert.Equal(0, none.Total);

        // The contract the search holds every contributor to, asserted through the platform's own rule rather than a
        // restatement of it here, so this test cannot drift from what the control plane actually enforces.
        foreach (var answer in new[] { contribution, none })
        {
            Assert.Null(SearchContributors.ContractViolation(answer, pageSize: 5));

            // What is this module's own claim, and not the platform's: every record hit opens on the record's page.
            Assert.All(answer.Items, item => Assert.StartsWith("/delivery/records/", item.Route!, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TheModule_LeavesTheChatsToolsToTheProductsMcpServer_AndKeepsAListADeploymentConfigured()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();

        // The product's MCP server decides what each mode lists, for the chat as for any other client of it, so the
        // module sets no list of its own: empty means every tool the session lists.
        var mcp = factory.Services.GetRequiredService<IOptions<SqlFlow.ControlPlane.Configuration.ControlPlaneOptions>>().Value.Assistant.Mcp;
        Assert.Empty(mcp.AllowedTools);

        // A deployment that narrowed the tools keeps exactly what it configured.
        await using var configured = Host().WithSetting("ControlPlane:Assistant:Mcp:AllowedTools:0", "delivery_record");
        using var other = configured.CreateClient();
        var kept = configured.Services.GetRequiredService<IOptions<SqlFlow.ControlPlane.Configuration.ControlPlaneOptions>>().Value.Assistant.Mcp.AllowedTools;
        Assert.Equal(["delivery_record"], kept);
    }

    /// <summary>The control plane with the module, and without the settings a test host must not act on (no network warm-up).</summary>
    private static ControlPlaneAppFactory Host()
        => new ControlPlaneAppFactory()
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    private static ControlPlaneModuleException Module(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is ControlPlaneModuleException module)
            {
                return module;
            }

            if (current is AggregateException { InnerExceptions.Count: 1 } aggregate)
            {
                return Module(aggregate.InnerExceptions[0]);
            }
        }

        throw new Xunit.Sdk.XunitException($"No ControlPlaneModuleException in: {error}");
    }
}
