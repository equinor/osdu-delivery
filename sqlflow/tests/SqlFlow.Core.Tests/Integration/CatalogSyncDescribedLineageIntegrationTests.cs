using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// A catalog sync of an estate that holds lineage descriptions (<c>flowType: lin</c>) against the real catalog. The
/// estate is one landing flow and two descriptions of the steps another system runs before it. The test syncs the
/// estate without the descriptions, then with them, then without them again, and asserts the rule the feature rests
/// on: the lineage SQLFlow computed (its edges, its flow dependencies, each pipeline's wave) is row for row the same
/// in all three, and what the descriptions add is stored beside it under relations of its own, with the optional
/// column level in its own table, and leaves with the documents.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogSyncDescribedLineageIntegrationTests : IDisposable
{
    private const string DescribedReads = nameof(LineageRelation.DescribedReads);
    private const string DescribedWrites = nameof(LineageRelation.DescribedWrites);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_catlin_" + Guid.NewGuid().ToString("N"));
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

    public CatalogSyncDescribedLineageIntegrationTests() => Directory.CreateDirectory(_dir);

    private string Landing => "lin_land_" + _suffix;

    private string Curate => "lin_u1_curate_" + _suffix;

    private string Publish => "lin_u2_publish_" + _suffix;

    private string Curated => "curated.orders_" + _suffix;

    private string CuratedKey => NodeKey.For(LineageDescribedObject.ServerRef("lakehouse"), null, null, Curated);

    private string OrdersKey => NodeKey.For(LineageDescribedObject.ServerRef("erp"), null, null, "sales.orders_" + _suffix);

    private void Write(string relativePath, string yaml)
    {
        var path = Path.Combine(_dir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml.Replace("__S__", _suffix, StringComparison.Ordinal));
    }

    private void WriteLanding()
        => Write("flows/10-land.flow.yaml", """
            name: lin_land___S__
            source:
              type: csv
              location: data/orders___S__
            target:
              connection: ${env:SQLFlowSinkConStr}
              schema: dbo
              table: LinOrders___S__
            """);

    private void WriteDescriptions()
    {
        Write("lineage/u1-curate.yaml", """
            flowType: lin
            name: lin_u1_curate___S__
            description: Builds the curated orders table.
            system: lakehouse
            unit: job orders-daily, task curate
            reads:
              - { system: erp, name: sales.orders___S__, as: src, origin: true }
              - { system: erp, name: sales.rates___S__, as: fx, origin: true }
            writes:
              - name: curated.orders___S__
                columns:
                  order_id: src.order_no
                  amount_eur:
                    from: [src.amount, fx.rate]
                    note: amount * rate
            context: |
              Test orders are left out.
            """);
        Write("lineage/u2-publish.yaml", """
            flowType: lin
            name: lin_u2_publish___S__
            system: lakehouse
            reads:
              - { name: curated.orders___S__, as: orders }
            writes:
              - feeds: lin_land___S__
                columns:
                  order_id: orders.order_id
                  amount: { from: orders.amount_eur, note: Rounded to two decimals. }
                  exported_at:
            """);
    }

    /// <summary>The lineage SQLFlow computed for the repository, as comparable lines: every edge that is not a
    /// described one, every flow dependency, and the wave of every pipeline that is not a description.</summary>
    private static async Task<List<string>> ComputedAsync(CatalogDbContext db, Guid repoId)
    {
        var edges = await db.LineageEdges.AsNoTracking()
            .Where(e => e.RepoId == repoId && e.Relation != DescribedReads && e.Relation != DescribedWrites)
            .Select(e => "edge|" + e.Flow + "|" + e.ViaModule + "|" + e.Relation + "|" + e.ObjectKey + "|" + e.ObjectName + "|" + e.Tier)
            .ToListAsync();
        var dependencies = await db.FlowDependencies.AsNoTracking()
            .Where(d => d.RepoId == repoId)
            .Select(d => "dependency|" + d.FromFlow + "|" + d.ToFlow + "|" + d.ViaObjects)
            .ToListAsync();
        var waves = await db.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && p.Kind != LineageDescriptionFlow.FlowType)
            .Select(p => "pipeline|" + p.Name + "|" + p.Kind + "|" + p.Wave + "|" + p.ExecutionMode + "|" + p.Active)
            .ToListAsync();
        return [.. edges.Concat(dependencies).Concat(waves).Order(StringComparer.Ordinal)];
    }

    [SkippableFact]
    public async Task Sync_StoresWhatDescriptionsSayBesideTheComputedLineage_WhichStaysTheSame()
    {
        var cs = IntegrationDb.Require();
        var repo = "cat_lin_" + _suffix;
        var repoId = FlowIdentity.FromName(repo);
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            // ---- The estate on its own.
            WriteLanding();
            List<string> alone;
            await using (var db = CatalogDatabase.Create(cs))
            {
                var result = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(1, result.PipelinesAdded);
                alone = await ComputedAsync(db, repoId);
                Assert.Contains(alone, line => line.StartsWith($"pipeline|{Landing}|file|1|", StringComparison.Ordinal));
                Assert.False(await db.LineageEdges.AnyAsync(
                    e => e.RepoId == repoId && (e.Relation == DescribedReads || e.Relation == DescribedWrites)));
            }

            // ---- With two descriptions of what happens before the landing flow.
            WriteDescriptions();
            await using (var db = CatalogDatabase.Create(cs))
            {
                var result = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(2, result.PipelinesAdded);
                Assert.DoesNotContain(result.Warnings, w => w.StartsWith("described lineage:", StringComparison.Ordinal));
            }

            string landedKey;
            await using (var db = CatalogDatabase.Create(cs))
            {
                // The computed lineage is line for line what it was.
                Assert.Equal(alone, await ComputedAsync(db, repoId));

                // A description is a pipeline of its own kind that is in no wave and never runs.
                var descriptions = await db.Pipelines.AsNoTracking()
                    .Where(p => p.RepoId == repoId && p.Kind == LineageDescriptionFlow.FlowType)
                    .OrderBy(p => p.Name)
                    .ToListAsync();
                Assert.Equal([Curate, Publish], descriptions.Select(p => p.Name));
                Assert.All(descriptions, p =>
                {
                    Assert.True(p.Active);
                    Assert.Equal(-1, p.Wave);
                    Assert.Equal(PipelineExecutionModes.Disabled, p.ExecutionMode);
                    Assert.Contains("flowType: lin", p.Yaml, StringComparison.Ordinal);
                });

                // What the descriptions say, under relations of their own.
                var described = await db.LineageEdges.AsNoTracking()
                    .Where(e => e.RepoId == repoId && (e.Relation == DescribedReads || e.Relation == DescribedWrites))
                    .ToListAsync();
                Assert.Equal(5, described.Count);
                Assert.All(described, e => Assert.Equal(nameof(LineageTier.Declared), e.Tier));

                var curate = described.Where(e => e.Flow == Curate).ToList();
                Assert.Equal(2, curate.Count(e => e.Relation == DescribedReads));
                Assert.Contains(curate, e => e.Relation == DescribedReads && e.ObjectKey == OrdersKey
                    && e.ObjectName == "sales.orders_" + _suffix);
                var curateWrite = Assert.Single(curate, e => e.Relation == DescribedWrites);
                Assert.Equal(CuratedKey, curateWrite.ObjectKey);
                Assert.Equal(Curated, curateWrite.ObjectName);
                Assert.Equal(CatalogIdentity.Pipeline(repoId, Curate), curateWrite.PipelineId);

                // The second description chains onto the first by the object's name, and onto the estate by naming the
                // landing flow: its write is the very object that flow reads.
                var publish = described.Where(e => e.Flow == Publish).ToList();
                Assert.Equal(CuratedKey, Assert.Single(publish, e => e.Relation == DescribedReads).ObjectKey);
                landedKey = (await db.LineageEdges.AsNoTracking()
                    .SingleAsync(e => e.RepoId == repoId && e.Flow == Landing && e.Relation == nameof(LineageRelation.Reads))).ObjectKey;
                Assert.Equal(landedKey, Assert.Single(publish, e => e.Relation == DescribedWrites).ObjectKey);

                // An object only descriptions name is not in the object registry.
                Assert.False(await db.Objects.AsNoTracking().AnyAsync(o => o.Key == CuratedKey || o.Key == OrdersKey));

                // The optional column level: one row per source of a mapped column, one row for a column with none.
                var columns = await db.LineageColumnMappings.AsNoTracking()
                    .Where(m => m.RepoId == repoId)
                    .OrderBy(m => m.Flow).ThenBy(m => m.Ordinal).ThenBy(m => m.SourceOrdinal)
                    .ToListAsync();
                Assert.Equal(6, columns.Count);

                var amountEur = columns.Where(m => m.Flow == Curate && m.TargetColumn == "amount_eur").ToList();
                Assert.Equal(2, amountEur.Count);
                Assert.All(amountEur, m =>
                {
                    Assert.Equal(CuratedKey, m.TargetObjectKey);
                    Assert.Equal("amount * rate", m.Note);
                });
                Assert.Equal(["amount", "rate"], amountEur.Select(m => m.SourceColumn));
                Assert.Equal(OrdersKey, amountEur[0].SourceObjectKey);

                var amount = Assert.Single(columns, m => m.Flow == Publish && m.TargetColumn == "amount");
                Assert.Equal(landedKey, amount.TargetObjectKey);
                Assert.Equal(CuratedKey, amount.SourceObjectKey);
                Assert.Equal("amount_eur", amount.SourceColumn);

                var exportedAt = Assert.Single(columns, m => m.Flow == Publish && m.TargetColumn == "exported_at");
                Assert.Null(exportedAt.SourceObjectKey);
                Assert.Null(exportedAt.SourceColumn);
            }

            // ---- The same estate again, lineage recomputed: nothing is doubled.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var again = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow, forceLineage: true);
                Assert.Equal(0, again.PipelinesAdded);
                Assert.Equal(alone, await ComputedAsync(db, repoId));
                Assert.Equal(5, await db.LineageEdges.CountAsync(
                    e => e.RepoId == repoId && (e.Relation == DescribedReads || e.Relation == DescribedWrites)));
                Assert.Equal(6, await db.LineageColumnMappings.CountAsync(m => m.RepoId == repoId));
            }

            // ---- A description that names a flow the estate does not hold: a finding, and its other connections stay.
            Write("lineage/u2-publish.yaml", """
                flowType: lin
                name: lin_u2_publish___S__
                system: lakehouse
                reads:
                  - { name: curated.orders___S__ }
                writes:
                  - feeds: lin_no_such_flow___S__
                """);
            await using (var db = CatalogDatabase.Create(cs))
            {
                var result = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Contains(result.Warnings, w => w.StartsWith("described lineage:", StringComparison.Ordinal)
                    && w.Contains("lin_no_such_flow_" + _suffix, StringComparison.Ordinal));
                Assert.Equal(alone, await ComputedAsync(db, repoId));
                var publish = await db.LineageEdges.AsNoTracking()
                    .Where(e => e.RepoId == repoId && e.Flow == Publish)
                    .ToListAsync();
                Assert.Equal(DescribedReads, Assert.Single(publish).Relation);
            }

            // ---- The descriptions leave the estate: so does everything they said, and nothing else.
            Directory.Delete(Path.Combine(_dir, "lineage"), recursive: true);
            await using (var db = CatalogDatabase.Create(cs))
            {
                var removed = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Equal(2, removed.PipelinesDeleted);
                Assert.Equal(alone, await ComputedAsync(db, repoId));
                Assert.False(await db.LineageEdges.AnyAsync(
                    e => e.RepoId == repoId && (e.Relation == DescribedReads || e.Relation == DescribedWrites)));
                Assert.False(await db.LineageColumnMappings.AnyAsync(m => m.RepoId == repoId));
                Assert.False(await db.Pipelines.AnyAsync(p => p.RepoId == repoId && p.Kind == LineageDescriptionFlow.FlowType));
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var objectKeys = await db.LineageEdges.Where(e => e.RepoId == repoId).Select(e => e.ObjectKey).Distinct().ToListAsync();
            await db.LineageColumnMappings.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => objectKeys.Contains(o.Key)).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task ADescription_NeverTakesAFlowsPipeline_AndAFlowRewrittenAsOne_LeavesThePlan()
    {
        var cs = IntegrationDb.Require();
        var repo = "cat_lin_name_" + _suffix;
        var repoId = FlowIdentity.FromName(repo);
        var extra = "lin_extra_" + _suffix;
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            // Two landing flows, both in wave 1.
            WriteLanding();
            Write("flows/20-extra.flow.yaml", """
                name: lin_extra___S__
                source:
                  type: csv
                  location: data/extra___S__
                target:
                  connection: ${env:SQLFlowSinkConStr}
                  schema: dbo
                  table: LinExtra___S__
                """);
            List<string> alone;
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                alone = await ComputedAsync(db, repoId);
            }

            // ---- A description with the landing flow's name, in a folder read before the flow's own. The first
            // document in path order becomes a name's pipeline; a description is never that document.
            Write("_first/clash.yaml", """
                flowType: lin
                name: LIN_LAND___S__
                system: lakehouse
                writes:
                  - { name: somewhere___S__ }
                """);
            await using (var db = CatalogDatabase.Create(cs))
            {
                var result = await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                Assert.Contains(result.Warnings, w => w.StartsWith("described lineage:", StringComparison.Ordinal)
                    && w.Contains("has the name of the file flow", StringComparison.Ordinal));
                Assert.DoesNotContain(result.Warnings, w => w.Contains("their facts merge", StringComparison.Ordinal));
                Assert.Equal(alone, await ComputedAsync(db, repoId));

                var landing = await db.Pipelines.AsNoTracking().SingleAsync(p => p.Id == CatalogIdentity.Pipeline(repoId, Landing));
                Assert.Equal("file", landing.Kind);
                Assert.Equal(1, landing.Wave);
                Assert.Equal(PipelineExecutionModes.Auto, landing.ExecutionMode);
                Assert.Equal("flows/10-land.flow.yaml", landing.RelativePath);
                Assert.False(await db.Pipelines.AnyAsync(p => p.RepoId == repoId && p.Kind == LineageDescriptionFlow.FlowType));
                Assert.False(await db.LineageEdges.AnyAsync(
                    e => e.RepoId == repoId && (e.Relation == DescribedReads || e.Relation == DescribedWrites)));
            }

            // ---- The second flow's document is rewritten as a description under the same name: the row keeps its
            // identity and its text changes kind, and it keeps nothing of the wave the flow ran in.
            File.Delete(Path.Combine(_dir, "_first", "clash.yaml"));
            Write("flows/20-extra.flow.yaml", """
                flowType: lin
                name: lin_extra___S__
                system: lakehouse
                writes:
                  - { feeds: lin_land___S__ }
                """);
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
                var rewritten = await db.Pipelines.AsNoTracking().SingleAsync(p => p.Id == CatalogIdentity.Pipeline(repoId, extra));
                Assert.Equal(LineageDescriptionFlow.FlowType, rewritten.Kind);
                Assert.Equal(-1, rewritten.Wave);
                Assert.Equal(PipelineExecutionModes.Disabled, rewritten.ExecutionMode);

                var landing = await db.Pipelines.AsNoTracking().SingleAsync(p => p.Id == CatalogIdentity.Pipeline(repoId, Landing));
                Assert.Equal(1, landing.Wave);
                Assert.Equal(DescribedWrites, (await db.LineageEdges.AsNoTracking().SingleAsync(e => e.RepoId == repoId && e.Flow == extra)).Relation);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var objectKeys = await db.LineageEdges.Where(e => e.RepoId == repoId).Select(e => e.ObjectKey).Distinct().ToListAsync();
            await db.LineageColumnMappings.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Objects.Where(o => objectKeys.Contains(o.Key) || o.Name == "LinExtra_" + _suffix).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
