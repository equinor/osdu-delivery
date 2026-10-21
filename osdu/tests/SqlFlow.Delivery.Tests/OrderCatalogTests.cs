using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Lineage;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Order;
using SqlFlow.Delivery.Ordering;
using SqlFlow.Delivery.Templates;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The OSDU order the module database holds (osdu/docs/reference/concepts/osdu-order.md): the catalog read of the synced
/// deliveries with the partition each one's records reach, a kind no template folder holds read from a built release of
/// OSDU's lineage, a sync that orders a repository by its own deliveries and computes it again once a release it reads is
/// built, and the schedules that run best before each schedule.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class OrderCatalogTests : IDisposable
{
    private static readonly DateTime Utc = OrderEstate.Utc;

    private readonly OsduTestDatabase _database = new();
    private readonly OrderEstate.MemoryTemplates _templates = new();
    private readonly string _rootA = Samples.NewTempDirectory();
    private readonly string _rootB = Samples.NewTempDirectory();
    private readonly OrderEstate _a;
    private readonly OrderEstate _b;
    private readonly Guid _repoA = Guid.NewGuid();
    private readonly Guid _repoB = Guid.NewGuid();

    public OrderCatalogTests()
    {
        _a = new OrderEstate(_rootA, _templates);
        _b = new OrderEstate(_rootB, _templates);
    }

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_rootA, recursive: true);
        Directory.Delete(_rootB, recursive: true);
    }

    [Fact]
    public async Task The_catalog_reads_the_synced_deliveries_with_the_partition_their_records_reach()
    {
        WellsAndWellbores();
        _b.Delivery("wells", "master-data--Well");
        _a.Delivery("wellbores", "master-data--Wellbore", columns: ["WellID"]);
        _a.Delivery("test-wellbores", "master-data--Wellbore", columns: ["WellID"], partition: "test");
        HoldTemplates();
        await ReconcileAsync(_repoB, _rootB);
        await ReconcileAsync(_repoA, _rootA);

        var read = await ReadAsync();
        Assert.Empty(read.Problems);
        Assert.Equal(
            ["test-wellbores@test", "wellbores@dev", "wells@dev"],
            read.Deliveries.Select(d => $"{d.Delivery.Flow}@{d.Delivery.Partition}").Order(StringComparer.Ordinal));
        var wellbores = read.Deliveries.Single(d => d.Delivery.Flow == "wellbores");
        Assert.Equal((_repoA, "flows/wellbores.yaml", _repoA.ToString("D")), (wellbores.RepoId, wellbores.RelativePath, wellbores.Delivery.Repository));
        Assert.Contains("osdu.data.WellID", wellbores.Delivery.Filled!);
        Assert.Equal("master-data--Wellbore", wellbores.Schema!.EntityType);

        // One repository's own deliveries, which its sync checks its waves against.
        Assert.Equal(["test-wellbores", "wellbores"], (await ReadAsync(_repoA)).Deliveries.Select(d => d.Delivery.Flow).Order(StringComparer.Ordinal));

        // The order of each partition with every repository's deliveries, named by their repository.
        var plans = RepositoryOrder.Plan(read, new Dictionary<Guid, string> { [_repoA] = "estate", [_repoB] = "masters" });
        var dev = plans.Single(p => p.Partition == "dev");
        Assert.Equal("master-data--Wellbore>master-data--Well", string.Join(" ", dev.Edges.Select(e => $"{e.From}>{e.To}")));
        Assert.Equal("masters: wells", dev.DeliveriesOf("master-data--Well").Single().FullLabel);
        Assert.Empty(plans.Single(p => p.Partition == "test").Edges);

        // A flow the repository no longer declares is not delivered any more, and leaves the order.
        _a.Remove("test-wellbores");
        await ReconcileAsync(_repoA, _rootA);
        Assert.DoesNotContain((await ReadAsync()).Deliveries, d => d.Delivery.Flow == "test-wellbores");
    }

    [Fact]
    public async Task A_kind_whose_template_file_is_gone_is_read_from_the_newest_built_release_holding_it()
    {
        WellsAndWellbores();
        _a.Delivery("wells", "master-data--Well");
        _a.Delivery("wellbores", "master-data--Wellbore", columns: ["WellID"]);
        HoldTemplates();
        await ReconcileAsync(_repoA, _rootA);

        // The order reads a template from the file its folder holds, where the repository was synced. With the file gone
        // since the sync, and no release knowing the wellbores' kind, what the mapping fills cannot be told, and the order
        // says why.
        File.Delete(Path.Combine(_rootA, "templates", TemplateSources.FileName("osdu:wks:master-data--Wellbore:1.0.0")));

        var unread = await ReadAsync();
        var unknown = unread.Deliveries.Single(d => d.Delivery.Flow == "wellbores");
        Assert.Null(unknown.Schema);
        Assert.Null(unknown.Delivery.Filled);
        Assert.Contains(unread.Problems, p => p.Contains("no longer reads: the file no longer exists; sync the repository again", StringComparison.Ordinal));

        var store = new SchemaLineageStore(_database.CreateDbContext);
        await store.SaveAsync("v0.28.0", "aaaa", null, [Entry("master-data--Wellbore")], [], "tests", Utc);
        await store.SaveAsync("v0.29.0", "bbbb", Utc, [Entry("master-data--Wellbore", ("osdu.data.WellID", "master-data--Well"))], [], "tests", Utc.AddHours(1));

        var released = (await ReadAsync()).Deliveries.Single(d => d.Delivery.Flow == "wellbores");
        Assert.Equal(["osdu.data.WellID"], released.Schema!.Relationships.Select(r => r.Property));
        Assert.Contains("osdu.data.WellID", released.Delivery.Filled!);
    }

    [Fact]
    public async Task A_template_file_that_no_longer_reads_as_one_is_said_and_the_release_is_asked_instead()
    {
        WellsAndWellbores();
        _a.Delivery("wellbores", "master-data--Wellbore", columns: ["WellID"]);
        HoldTemplates();
        await ReconcileAsync(_repoA, _rootA);
        await File.WriteAllTextAsync(Path.Combine(_rootA, "templates", TemplateSources.FileName("osdu:wks:master-data--Wellbore:1.0.0")), "{ not json");

        var read = await ReadAsync();
        var problem = Assert.Single(read.Problems);
        Assert.StartsWith("The template osdu:wks:master-data--Wellbore:1.0.0 version ", problem, StringComparison.Ordinal);
        Assert.Contains("that mappings/Wellbores@1.0.0.yaml fills cannot be read", problem, StringComparison.Ordinal);
        Assert.Contains("no longer reads: the schema is not valid JSON", problem, StringComparison.Ordinal);
        Assert.Null(Assert.Single(read.Deliveries).Schema);
    }

    [Fact]
    public async Task A_release_built_since_a_mapping_was_synced_is_a_lineage_input_change_when_no_template_folder_holds_its_template()
    {
        WellsAndWellbores();
        _a.Delivery("wells", "master-data--Well");
        _a.Delivery("wellbores", "master-data--Wellbore", columns: ["WellID"]);
        HoldTemplates("master-data--Well");
        var warning = Assert.Single(await ReconcileAsync(_repoA, _rootA, clean: false));
        Assert.Contains("no template folder on the path of mappings/Wellbores@1.0.0.yaml holds template osdu:wks:master-data--Wellbore:1.0.0", warning, StringComparison.Ordinal);
        Assert.False(await ReleaseBuiltSinceAsync());

        // A release holding a kind whose template a folder holds changes nothing.
        var store = new SchemaLineageStore(_database.CreateDbContext);
        await store.SaveAsync("v0.28.0", "aaaa", null, [Entry("master-data--Well")], [], "tests", Utc.AddHours(1));
        Assert.False(await ReleaseBuiltSinceAsync());

        // One holding the wellbores' kind, built after the sync that saw their mapping, does: once.
        await store.SaveAsync("v0.29.0", "bbbb", null, [Entry("master-data--Wellbore", ("osdu.data.WellID", "master-data--Well"))], [], "tests", Utc.AddHours(1));
        Assert.True(await ReleaseBuiltSinceAsync());
        await ReconcileAsync(_repoA, _rootA, Utc.AddHours(2), clean: false);
        Assert.False(await ReleaseBuiltSinceAsync());
    }

    [Fact]
    public async Task A_release_built_again_replaces_its_kinds_and_a_kind_is_read_from_the_newest_build_holding_it()
    {
        var store = new SchemaLineageStore(_database.CreateDbContext);
        Assert.Empty(await store.ListAsync());
        Assert.Null(await store.LoadAsync("v0.29.0"));
        Assert.Null(await store.FindKindAsync("osdu:wks:master-data--Wellbore:1.0.0"));

        await store.SaveAsync(
            "v0.29.0", "aaaa", Utc, [Entry("master-data--Well"), Entry("master-data--Wellbore", ("osdu.data.WellID", "master-data--Well"))],
            [new SchemaLineageFailure("osdu:wks:master-data--Broken:1.0.0", "its schema is not JSON")], "tests", Utc);
        var built = await store.LoadAsync("v0.29.0");
        Assert.Equal((2, 1, "aaaa"), (built!.Info.Kinds, built.Info.Relationships, built.Info.Commit));
        Assert.Equal("its schema is not JSON", Assert.Single(built.Info.Failures).Reason);
        Assert.Equal(["osdu:wks:master-data--Well:1.0.0", "osdu:wks:master-data--Wellbore:1.0.0"], built.Kinds.Select(k => k.Node.Kind));
        Assert.Equal(["master-data--Well"], built.Graph().EntityReferences()["master-data--Wellbore"]);

        // Built again: its kinds are replaced, not added to, under the same release.
        await store.SaveAsync("v0.29.0", "bbbb", Utc, [Entry("master-data--Wellbore")], [], "tests", Utc.AddHours(1));
        var rebuilt = await store.LoadAsync("v0.29.0");
        Assert.Equal((1, 0, "bbbb"), (rebuilt!.Info.Kinds, rebuilt.Info.Relationships, rebuilt.Info.Commit));
        Assert.Single(await store.ListAsync());

        // The newest build holding a kind answers for it.
        await store.SaveAsync("v0.30.0", "cccc", Utc, [Entry("master-data--Wellbore", ("osdu.data.ParentWellboreID", "master-data--Wellbore"))], [], "tests", Utc.AddHours(2));
        Assert.Equal(["osdu.data.ParentWellboreID"], (await store.FindKindAsync("osdu:wks:master-data--Wellbore:1.0.0"))!.Relationships.Select(r => r.Property));
        Assert.Equal(["v0.30.0", "v0.29.0"], (await store.ListAsync()).Select(l => l.Release));

        await Assert.ThrowsAsync<DeliveryException>(() => store.SaveAsync(new string('v', 101), "dddd", null, [Entry("master-data--Well")], [], "tests", Utc));
        await Assert.ThrowsAsync<DeliveryException>(() => store.SaveAsync("v0.31.0", "dddd", null, [Entry("master-data--Well"), Entry("master-data--Well")], [], "tests", Utc));
    }

    [Fact]
    public async Task A_sync_orders_a_repository_by_its_own_deliveries_and_names_the_schedules_that_run_best_first()
    {
        WellsAndWellbores();
        _a.Schema("work-product-component--WellLog", ("WellboreID", "master-data--Wellbore"));
        _b.Delivery("wells", "master-data--Well");
        _a.Delivery("wellbores", "master-data--Wellbore", columns: ["WellID"]);
        _a.Delivery("logs", "work-product-component--WellLog", columns: ["WellboreID"]);
        HoldTemplates();
        var documents = new DeliveryDocumentLoader();
        var loader = YamlDocumentLoader.CreateDefault(
            [new DeliveryFlowKind(documents, new MappingTemplateSource(), () => new OrderCatalog(_database.CreateDbContext)), new CacheFlowKind(documents)]);
        var sync = new CatalogSync(loader, [new DeliveryCatalogSync(documents)]);
        var nameA = "order-a-" + _repoA.ToString("N")[..10];
        var nameB = "order-b-" + _repoB.ToString("N")[..10];
        var idA = FlowIdentity.FromName(nameA);
        var idB = FlowIdentity.FromName(nameB);
        var touched = new List<string>();

        async Task<CatalogSyncResult> SyncAsync(string root, string name, DateTime nowUtc)
        {
            await using var db = _database.CreateCatalogContext();
            return await sync.SyncAsync(db, root, name, null, nowUtc);
        }

        try
        {
            Assert.DoesNotContain((await SyncAsync(_rootB, nameB, Utc)).Warnings, w => w.Contains("OSDU order", StringComparison.Ordinal));
            var synced = await SyncAsync(_rootA, nameA, Utc.AddMinutes(1));
            Assert.DoesNotContain(synced.Warnings, w => w.Contains("OSDU order", StringComparison.Ordinal));

            await using (var db = _database.CreateCatalogContext())
            {
                touched = await db.LineageEdges.Where(e => e.RepoId == idA || e.RepoId == idB).Select(e => e.ObjectKey).Distinct().ToListAsync();
                var waves = await db.Pipelines.Where(p => p.RepoId == idA).ToDictionaryAsync(p => p.Name, p => p.Wave);
                Assert.True(waves["wellbores"] < waves["logs"]);

                // The wells are another repository's, delivered by its own schedules: nothing here reads them for the order.
                var reads = await db.LineageEdges.Where(e => e.RepoId == idA && e.Relation == nameof(LineageRelation.Reads)).Select(e => e.ObjectKey).ToListAsync();
                Assert.Contains(reads, k => k.Contains("*:*:master-data--Wellbore:*", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(reads, k => k.Contains("*:*:master-data--Well:*", StringComparison.OrdinalIgnoreCase));

                // Schedules: the masters' nightly delivers the wells; the estate's nightly runs both of its flows, its hourly
                // the logs alone, chained behind the nightly.
                Schedule(db, idB, "masters-nightly", "wells");
                Schedule(db, idA, "estate-nightly", "wellbores", "logs");
                var hourly = Schedule(db, idA, "estate-hourly", "logs");
                db.ScheduleParents.Add(new CatalogScheduleParent { ScheduleId = hourly, RepoId = idA, ParentName = "estate-nightly", Ordinal = 0 });
                await db.SaveChangesAsync();
            }

            await using (var catalog = _database.CreateCatalogContext())
            await using (var module = _database.CreateDbContext())
            {
                var orders = (await ScheduleOrders.ReadAsync(catalog, module, Folders())).Schedules;
                var nightly = orders.Single(o => o.Schedule.Name == "estate-nightly");
                var first = Assert.Single(nightly.RunFirst);
                Assert.Equal(("masters-nightly", nameB, false, false), (first.Schedule.Name, first.Schedule.Repository, first.SameRepository, first.Chained));
                Assert.Equal(ScheduleTiming.Unknown, first.Timing);
                var reason = Assert.Single(first.Because);
                Assert.Equal(("wellbores", "master-data--Well", "wells"), (reason.Flow, reason.RefersTo, reason.ProviderFlow));

                // The hourly refers to the wellbores, which its repository's nightly runs, and it already fires after it.
                var hourly = orders.Single(o => o.Schedule.Name == "estate-hourly");
                var behind = Assert.Single(hourly.RunFirst);
                Assert.Equal(("estate-nightly", true, true), (behind.Schedule.Name, behind.SameRepository, behind.Chained));
                Assert.Empty(orders.Single(o => o.Schedule.Name == "masters-nightly").RunFirst);
            }
        }
        finally
        {
            await using var db = _database.CreateCatalogContext();
            await db.ScheduleParents.Where(p => p.RepoId == idA || p.RepoId == idB).ExecuteDeleteAsync();
            await RepoStore.DeleteAsync(db, idA, CancellationToken.None);
            await RepoStore.DeleteAsync(db, idB, CancellationToken.None);
            var kept = await db.LineageEdges.Where(e => touched.Contains(e.ObjectKey)).Select(e => e.ObjectKey).Distinct().ToListAsync();
            var mine = touched.Except(kept, StringComparer.Ordinal).ToList();
            await db.ObjectColumns.Where(c => mine.Contains(c.ObjectKey)).ExecuteDeleteAsync();
            await db.Objects.Where(o => mine.Contains(o.Key)).ExecuteDeleteAsync();
        }
    }

    private void WellsAndWellbores()
    {
        _a.Schema("master-data--Well");
        _a.Schema("master-data--Wellbore", ("WellID", "master-data--Well"));
    }

    /// <summary>A schedule of <paramref name="repoId"/> running <paramref name="flows"/>, as a sync leaves it.</summary>
    private static Guid Schedule(CatalogDbContext db, Guid repoId, string name, params string[] flows)
    {
        var id = Guid.NewGuid();
        db.Schedules.Add(new CatalogSchedule { Id = id, RepoId = repoId, Name = name, IntervalSeconds = 3600, CreatedUtc = Utc, UpdatedUtc = Utc });
        foreach (var flow in flows)
        {
            db.ScheduleMembers.Add(new CatalogScheduleMember { ScheduleId = id, PipelineId = CatalogIdentity.Pipeline(repoId, flow), RepoId = repoId, FlowName = flow });
        }

        return id;
    }

    /// <summary>
    /// Puts the schemas the estates were written with in the template folder of each, as a repository holds the templates
    /// its mappings fill; with entity types named, those alone.
    /// </summary>
    private void HoldTemplates(params string[] entityTypes)
    {
        _a.Hold(entityTypes);
        _b.Hold(entityTypes);
    }

    /// <summary>Reconciles a repository and returns what its sync warned of, which a <paramref name="clean"/> sync has none of.</summary>
    private async Task<IReadOnlyList<string>> ReconcileAsync(Guid repo, string root, DateTime? nowUtc = null, bool clean = true)
    {
        var warnings = new List<string>();
        await using var db = _database.CreateDbContext();
        await new DeliveryCatalogSync(new DeliveryDocumentLoader()).ReconcileAsync(db, repo, root, nowUtc ?? Utc, warnings, CancellationToken.None);
        if (clean)
        {
            Assert.Empty(warnings);
        }

        return warnings;
    }

    private async Task<CatalogDeliveries> ReadAsync(Guid? repo = null)
    {
        await using var db = _database.CreateDbContext();
        return await OrderCatalog.ReadAsync(db, Folders(), repo);
    }

    /// <summary>The two estates' repositories, read where their syncs read them, as the control plane finds them.</summary>
    private IRepositoryFolders Folders() => RepositoryFolders.Of(_repoA, _rootA, RepositoryFolders.Of(_repoB, _rootB));

    private async Task<bool> ReleaseBuiltSinceAsync()
    {
        await using var db = _database.CreateDbContext();
        return await DeliveryCatalogSync.ReleaseBuiltSinceAsync(db, _repoA, CancellationToken.None);
    }

    private static SchemaLineageEntry Entry(string entityType, params (string Property, string Target)[] references)
        => new(new SchemaNode($"osdu:wks:{entityType}:1.0.0", entityType, [.. references.Select(r => new SchemaRelationship(r.Property, [r.Target]))]), "1.0.0", "PUBLISHED");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
