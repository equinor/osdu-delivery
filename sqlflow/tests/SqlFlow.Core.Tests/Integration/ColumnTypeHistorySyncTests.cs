using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// A run's warnings and its changes to existing columns' types, recorded by the two sync paths against the real
/// catalog: the full sync, which keys a run's changes by the lineage it computes in the same pass, and the per-run
/// write-back after <c>sqlflow run</c>, which keys them by the lineage the catalog holds. Both count the run's warnings,
/// neither records a run twice, and a column dictionary re-read from the CREATE TABLE a run once executed keeps the
/// type a later run changed the column to. Every test's table and repository are unique to it and removed afterwards.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ColumnTypeHistorySyncTests : IDisposable
{
    private const string ServerRef = "${env:SQLFlowSinkConStr}";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_typehist_" + Guid.NewGuid().ToString("N"));

    public ColumnTypeHistorySyncTests() => Directory.CreateDirectory(_dir);

    [SkippableFact]
    public async Task Sync_KeysARunsTypeChanges_ByThePassLineage_AndAReReadColumnKeepsTheChangedType()
    {
        var cs = IntegrationDb.Require();
        var ids = NewIds("typehist_sync");
        WriteFlow(ids);
        var olderRunId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var runJson = WriteRun(ids, runId);
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            // The run that created the table: its CREATE TABLE is in the catalog's statement history, so the lineage pass
            // reads the table's columns from it, with the types the table was created with.
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Runs.Add(new CatalogRun
                {
                    RunId = olderRunId,
                    PipelineId = ids.PipelineId,
                    RepoId = ids.RepoId,
                    FlowName = ids.FlowName,
                    FlowKind = "file",
                    Success = true,
                    Status = RunStatuses.Succeeded,
                    WrittenUtc = DateTime.UtcNow.AddDays(-1),
                });
                db.RunStatements.Add(new CatalogRunStatement
                {
                    RunId = olderRunId,
                    RepoId = ids.RepoId,
                    Ordinal = 1,
                    Step = "schema.apply-ddl",
                    Sql = $"CREATE TABLE [dbo].[{ids.Table}] ([Name] nvarchar(50) NULL, [Code] int NULL);",
                });
                await db.SaveChangesAsync();
            }

            // The pipeline's first sync: no lineage is stored for it yet, so its run's changes are keyed by this pass's.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var synced = await new CatalogSync().SyncAsync(db, _dir, ids.Repo, null, DateTime.UtcNow);
                Assert.Equal(2, synced.TypeChangesAdded);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var objectKey = await ObjectKeyAsync(db, ids);
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                Assert.Equal((RunStatuses.Succeeded, 1), (run.Status, run.WarningCount));

                var changes = await db.ColumnTypeChanges.AsNoTracking().Where(c => c.RunId == runId).OrderBy(c => c.Id).ToListAsync();
                Assert.Equal(["Name", "Code"], changes.Select(c => c.ColumnName));
                Assert.All(changes, c => Assert.Equal(objectKey, c.ObjectKey));

                // Read from the CREATE TABLE, Name was nvarchar(50); the run widened it, so the dictionary says nvarchar(100).
                // The refused change leaves Code as it was.
                Assert.Equal(("nvarchar(100)", "int"), await ColumnTypesAsync(db, objectKey));
            }

            // A later pass reads the columns from the same CREATE TABLE again, and the column keeps the type the run gave it.
            File.Delete(runJson);
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, ids.Repo, null, DateTime.UtcNow, forceLineage: true);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var objectKey = await ObjectKeyAsync(db, ids);
                Assert.Equal(("nvarchar(100)", "int"), await ColumnTypesAsync(db, objectKey));
                Assert.Equal(2, await db.ColumnTypeChanges.CountAsync(c => c.RunId == runId));
            }
        }
        finally
        {
            await CleanupAsync(cs, ids);
        }
    }

    [SkippableFact]
    public async Task RecordRun_KeysARunsTypeChanges_ByTheStoredLineage_AndRecordsThemOnce()
    {
        var cs = IntegrationDb.Require();
        var ids = NewIds("typehist_rec");
        var flowFile = WriteFlow(ids);
        var runId = Guid.NewGuid();
        await CatalogDatabase.MigrateAsync(cs);

        try
        {
            // A full sync first, as an estate's is: it stores the lineage the write-back keys the run's changes by.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, ids.Repo, null, DateTime.UtcNow);
            }

            var runJson = WriteRun(ids, runId);
            await using (var db = CatalogDatabase.Create(cs))
            {
                var recorded = await new CatalogSync().RecordRunAsync(db, flowFile, runJson, ids.Repo, null, DateTime.UtcNow);
                Assert.True(recorded.RunRecorded);
                Assert.Equal(2, recorded.TypeChangesAdded);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var objectKey = await ObjectKeyAsync(db, ids);
                Assert.Equal(1, (await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId)).WarningCount);
                var changes = await db.ColumnTypeChanges.AsNoTracking().Where(c => c.RunId == runId).ToListAsync();
                Assert.Equal(2, changes.Count);
                Assert.All(changes, c =>
                {
                    Assert.Equal(objectKey, c.ObjectKey);
                    Assert.Equal(ids.PipelineId, c.PipelineId);
                    // A run recorded from its artifact alone names nobody who asked for it.
                    Assert.Null(c.RequestedBy);
                });
            }

            // Recording the same run again changes nothing: a run is immutable.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var again = await new CatalogSync().RecordRunAsync(db, flowFile, runJson, ids.Repo, null, DateTime.UtcNow);
                Assert.False(again.RunRecorded);
                Assert.Equal(2, await db.ColumnTypeChanges.CountAsync(c => c.RunId == runId));
            }
        }
        finally
        {
            await CleanupAsync(cs, ids);
        }
    }

    private sealed record Ids(string Repo, Guid RepoId, string FlowName, Guid PipelineId, string Table);

    private static Ids NewIds(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = $"{prefix}_{suffix}";
        var repoId = FlowIdentity.FromName(repo);
        var flowName = $"{prefix}_flow_{suffix}";
        return new Ids(repo, repoId, flowName, CatalogIdentity.Pipeline(repoId, flowName), "TypeHist_" + suffix);
    }

    private string WriteFlow(Ids ids)
    {
        var path = Path.Combine(_dir, "flows", "typehist.flow.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            name: {{ids.FlowName}}
            source:
              type: csv
              location: ./data.csv
            target:
              connection: {{ServerRef}}
              schema: dbo
              table: {{ids.Table}}
            """);
        return path;
    }

    /// <summary>A run that widened one column and had a change to another refused, the refusal raising its warning,
    /// written where the engine writes a run.</summary>
    private string WriteRun(Ids ids, Guid runId)
    {
        var dir = Path.Combine(_dir, ".sqlflow", "runs", RunHistoryWriter.SafeName(ids.FlowName), $"20261127-100000_{runId.ToString("N")[..8]}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "run.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": 1, "flowKind": "file", "flowName": "{{ids.FlowName}}", "runId": "{{runId}}", "success": true,
              "writtenUtc": "2026-11-27T10:00:00Z",
              "result": {
                "rowsLoaded": 5, "totalMs": 100.0,
                "typeChanges": [
                  { "layer": "target", "table": "[dbo].[{{ids.Table}}]", "column": "Name", "fromType": "nvarchar(50)",
                    "toType": "nvarchar(100)", "tier": "Widening", "status": "Applied", "message": "[Name] widened." },
                  { "layer": "target", "table": "[dbo].[{{ids.Table}}]", "column": "Code", "fromType": "int",
                    "toType": "smallint", "tier": "Manual", "status": "Manual", "message": "[Code] narrowing left alone.",
                    "manualSteps": [ "Convert it by hand." ] }
                ]
              },
              "events": [
                { "timestampUtc": "2026-11-27T09:59:59Z", "level": "warning", "step": "target.typechange", "message": "[Code] is manual." }
              ]
            }
            """);
        return path;
    }

    /// <summary>The key the catalog holds the flow's target under: the object its pipeline writes.</summary>
    private static async Task<string> ObjectKeyAsync(CatalogDbContext db, Ids ids)
        => (await db.Objects.AsNoTracking().SingleAsync(o => o.ServerRef == ServerRef && o.Schema == "dbo" && o.Name == ids.Table)).Key;

    private static async Task<(string? Name, string? Code)> ColumnTypesAsync(CatalogDbContext db, string objectKey)
    {
        var columns = await db.ObjectColumns.AsNoTracking()
            .Where(c => c.ObjectKey == objectKey)
            .ToDictionaryAsync(c => c.Name, c => c.DataType);
        return (columns.GetValueOrDefault("Name"), columns.GetValueOrDefault("Code"));
    }

    private static async Task CleanupAsync(string cs, Ids ids)
    {
        await using var db = CatalogDatabase.Create(cs);
        var keys = await db.Objects.Where(o => o.ServerRef == ServerRef && o.Name == ids.Table).Select(o => o.Key).ToListAsync();
        await db.ColumnTypeChanges.Where(c => c.RepoId == ids.RepoId).ExecuteDeleteAsync();
        await db.ObjectColumns.Where(c => keys.Contains(c.ObjectKey)).ExecuteDeleteAsync();
        await db.Objects.Where(o => keys.Contains(o.Key)).ExecuteDeleteAsync();
        await db.FlowDependencies.Where(d => d.RepoId == ids.RepoId).ExecuteDeleteAsync();
        await db.LineageEdges.Where(e => e.RepoId == ids.RepoId).ExecuteDeleteAsync();
        await db.RunEvents.Where(e => e.RepoId == ids.RepoId).ExecuteDeleteAsync();
        await db.RunStatements.Where(s => s.RepoId == ids.RepoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == ids.RepoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == ids.RepoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == ids.RepoId).ExecuteDeleteAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
