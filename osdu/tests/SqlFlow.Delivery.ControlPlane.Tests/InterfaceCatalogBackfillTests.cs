using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane.Background;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The control plane's start-up description of delivery interfaces (<see cref="InterfaceCatalogBackfillService"/>): the rows
/// of a repository described before the cache version each interface renders against was recorded are described again
/// from the catalog's copies, so the partition cache's retention stops waiting on them without a sync; and the rows of a
/// repository the catalog holds no delivery flow of any more become inactive, since no repository declares those flows.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class InterfaceCatalogBackfillTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    private readonly OsduTestDatabase _module = new();

    public void Dispose() => _module.Dispose();

    [Fact]
    public async Task Rows_described_before_pins_were_recorded_are_described_again_and_rows_of_a_vanished_repository_retire()
    {
        const string Pinned = "20260901T060000Z";
        var repoId = Guid.NewGuid();
        var vanished = Guid.NewGuid();
        var header = "wells-wellbore-03-header-delivery";
        var welllogs = "welldb-welllog-03-header-delivery";
        await SeedCatalogAsync(repoId, [
            (header, "flows/" + header + ".yaml", await File.ReadAllTextAsync(Samples.WellboreFlowFile)),
            (welllogs, "flows/" + welllogs + ".yaml", (await File.ReadAllTextAsync(Samples.Flow))
                .Replace("mapping: WellLog@1.4.0", $"mapping: WellLog@1.4.0\n  cacheVersion: {Pinned}", StringComparison.Ordinal)),
        ]);

        try
        {
            // As an upgrade leaves them: the repository's rows written before the column, and a row of a repository the
            // catalog no longer holds, all active and none with a recorded cache version.
            await using (var db = _module.CreateDbContext())
            {
                db.DeliveryInterfaces.AddRange(
                    Row(repoId, header, string.Empty),
                    Row(repoId, welllogs, "dev"),
                    Row(vanished, "retired-delivery", string.Empty));
                await db.SaveChangesAsync();
            }

            var cache = _module.Caches();
            await cache.MergeAsync(
                "dev", "welldb-osdu-00-reference-cache",
                [new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", [ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Name"] = "metre" })])],
                new CacheCapture(null, "tests", "seeded"), new DateTimeOffset(Now.AddDays(-30)));
            var waiting = await cache.ApplyRetentionAsync(CacheRetentionRequest.Purge("dev", 0, new DateTimeOffset(Now), "tests", dryRun: true));
            Assert.Contains("retired-delivery", waiting.Deferred, StringComparison.Ordinal);

            await BackfillAsync();

            await using (var db = _module.CreateDbContext())
            {
                var rows = await db.DeliveryInterfaces.AsNoTracking().ToListAsync();
                var described = rows.Where(r => r.RepoId == repoId && r.Active).ToList();
                Assert.Equal(FlowRender.CurrentCacheVersion, Assert.Single(described, r => r.FlowName == header).CacheVersion);
                Assert.Equal(Pinned, Assert.Single(described, r => r.FlowName == welllogs).CacheVersion);
                Assert.False(Assert.Single(rows, r => r.RepoId == vanished).Active);
                Assert.DoesNotContain(rows, r => r.Active && r.CacheVersion == null);
            }

            // Nothing is left for the retention to wait on.
            var applied = await cache.ApplyRetentionAsync(CacheRetentionRequest.Purge("dev", 0, new DateTimeOffset(Now), "tests", dryRun: true));
            Assert.Null(applied.Deferred);

            // A second pass finds every row described, and changes nothing.
            await BackfillAsync();
            await using (var db = _module.CreateDbContext())
            {
                Assert.Equal(2, await db.DeliveryInterfaces.CountAsync(r => r.RepoId == repoId && r.Active));
            }
        }
        finally
        {
            await using var catalog = _module.CreateCatalogContext();
            await catalog.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await catalog.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Rows_written_before_document_hashes_get_them_from_the_catalog_copies_of_removed_pipelines_too()
    {
        var repoId = Guid.NewGuid();
        const string OldName = "welldb-welllog-03-header-delivery";
        const string NewName = "welldb-welllog-03-v140-header-delivery";
        var yaml = (await File.ReadAllTextAsync(Samples.Flow)).ReplaceLineEndings("\n");
        await SeedCatalogAsync(repoId, [
            (OldName, "flows/" + OldName + ".yaml", yaml, false),
            (NewName, "flows/" + NewName + ".yaml", yaml.Replace($"name: {OldName}\n", $"name: {NewName}\n", StringComparison.Ordinal), true),
        ]);

        try
        {
            // A rename synced before the hash was recorded: the old name's row inactive, the new one's active, neither hashed.
            await using (var db = _module.CreateDbContext())
            {
                var old = Row(repoId, OldName, "dev");
                old.Active = false;
                db.DeliveryInterfaces.AddRange(old, Row(repoId, NewName, "dev"));
                await db.SaveChangesAsync();
            }

            await BackfillAsync();

            // Both rows carry the one document's hash, from the catalog's copies; the old one stays inactive, and the backfill
            // asks for no move, which is the sync's to ask.
            await using (var db = _module.CreateDbContext())
            {
                var rows = await db.DeliveryInterfaces.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync();
                var old = Assert.Single(rows, r => r.FlowName == OldName);
                var renamed = Assert.Single(rows, r => r.FlowName == NewName);
                Assert.False(old.Active);
                Assert.True(renamed.Active);
                Assert.NotNull(old.DocumentHash);
                Assert.Equal(old.DocumentHash, renamed.DocumentHash);
                Assert.False(await db.DeliveryActivities.AnyAsync(a => a.Kind == Delivery.Ledger.LedgerMoves.Kind));
            }
        }
        finally
        {
            await using var catalog = _module.CreateCatalogContext();
            await catalog.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await catalog.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>Runs one pass of the start-up description over the test database, as the control plane does when it starts.</summary>
    private async Task BackfillAsync()
    {
        await using var services = new ServiceCollection()
            .AddScoped(_ => _module.CreateCatalogContext())
            .AddScoped(_ => _module.CreateDbContext())
            .AddSingleton(new DeliveryDocumentLoader())
            .BuildServiceProvider();
        await new InterfaceCatalogBackfillService(services, TimeProvider.System, NullLogger<InterfaceCatalogBackfillService>.Instance)
            .BackfillAsync(CancellationToken.None);
    }

    /// <summary>A repository in the catalog with the delivery flows given, as a sync copies them, every one declared.</summary>
    private Task SeedCatalogAsync(Guid repoId, IReadOnlyList<(string Name, string Path, string Yaml)> flows)
        => SeedCatalogAsync(repoId, flows.Select(f => (f.Name, f.Path, f.Yaml, true)).ToList());

    /// <summary>A repository in the catalog with the delivery flows given, each declared or one the repository no longer declares.</summary>
    private async Task SeedCatalogAsync(Guid repoId, IReadOnlyList<(string Name, string Path, string Yaml, bool Active)> flows)
    {
        await using var catalog = _module.CreateCatalogContext();
        catalog.Repos.Add(new CatalogRepo { Id = repoId, Name = "backfill-" + repoId.ToString("N")[..10], FirstSeenUtc = Now, LastSyncUtc = Now });
        foreach (var (name, path, yaml, active) in flows)
        {
            catalog.Pipelines.Add(new CatalogPipeline
            {
                Id = Guid.NewGuid(),
                RepoId = repoId,
                Name = name,
                Kind = FlowDefinition.FlowTypeName,
                RelativePath = path,
                ContentHash = new string('0', 64),
                Yaml = yaml,
                DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"delivery"}"""),
                Active = active,
                Wave = 0,
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
        }

        await catalog.SaveChangesAsync();
    }

    /// <summary>
    /// An active interface row as the sync wrote it before it recorded the cache version the interface renders against: under
    /// the id the sync derives for the flow's single interface in the partition.
    /// </summary>
    private static DeliveryInterface Row(Guid repoId, string flowName, string partition) => new()
    {
        Id = FlowIdentity.FromName(
            $"delivery-interface/{repoId:N}/{flowName.ToLowerInvariant()}/" + (partition.Length == 0 ? string.Empty : $"@{partition.ToLowerInvariant()}")),
        RepoId = repoId,
        FlowName = flowName,
        Interface = string.Empty,
        Partition = partition,
        LedgerFlowId = Guid.NewGuid(),
        LedgerName = flowName,
        Route = "storage",
        MappingReference = "Wellbore@1.0.0",
        CacheVersion = null,
        RecordObject = "Wells.ing.Wellbore",
        RelativePath = "flows/" + flowName + ".yaml",
        Active = true,
        FirstSeenUtc = Now,
        LastSeenUtc = Now,
    };
}
