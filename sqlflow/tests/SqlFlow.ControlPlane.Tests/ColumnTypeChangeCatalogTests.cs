using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A run's warnings and its changes to existing columns' types, recorded by the run queue's completion against the real
/// catalog: the run keeps its succeeded status and counts its warnings, each reported change is a row of the column's
/// type history keyed by the object the pipeline writes, a change the run made sets the column's type in the dictionary,
/// a run projected twice keeps one set of rows, and a run recorded late never moves a column back. Every row a test
/// seeds sits under keys unique to it, and is removed in a finally.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ColumnTypeChangeCatalogTests
{
    private const string Node = "type-change-node";
    private static readonly DateTime Ended = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task ACompletedRun_RecordsItsTypeChangesAndWarnings_AndTheColumnFollows()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var seed = NewSeed();

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                Seed(db, seed, depthType: "int");
                await db.SaveChangesAsync();
            }

            Guid runId;
            await using (var db = CatalogDatabase.Create(cs))
            {
                runId = (await RunQueueStore.EnqueueAsync(
                    db, new RunEnqueueRequest(seed.RepoId, seed.FlowName, "ing", RequestedBy: "alice"), Ended.AddMinutes(-5))).RunId;
                Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, Ended.AddMinutes(-4)));
                var recorded = await RunQueueStore.RecordOutcomeAsync(
                    db, runId, Node, 1, RunOutcomeKind.Completed, null, Artifact(runId, seed), Ended.AddSeconds(5));
                Assert.Equal(RunOutcomeStatus.Recorded, recorded.Status);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
                // It succeeded, and its one warning (the refusal) tells it apart from a clean run.
                Assert.Equal(RunStatuses.Succeeded, run.Status);
                Assert.Equal(1, run.WarningCount);

                var changes = await db.ColumnTypeChanges.AsNoTracking().Where(c => c.RunId == runId).OrderBy(c => c.Id).ToListAsync();
                Assert.Equal(["Depth", "Spud"], changes.Select(c => c.ColumnName));
                Assert.All(changes, c =>
                {
                    // The run named the table in another case; the row takes the key the catalog holds the object under.
                    Assert.Equal(seed.ObjectKey, c.ObjectKey);
                    Assert.Equal((seed.RepoId, seed.PipelineId, "alice", Ended), (c.RepoId, c.PipelineId, c.RequestedBy, c.OccurredUtc));
                });
                Assert.Equal(("Applied", "int", "bigint"), (changes[0].Status, changes[0].FromType, changes[0].ToType));
                Assert.Equal(("Refused", 2L), (changes[1].Status, changes[1].FailingValues));
                Assert.Equal("""[{"value":"NA","rows":3}]""", changes[1].SampleJson);

                // The change the run made shows in the column dictionary at once; the refused one leaves its column as it was.
                var columns = await Columns(db, seed.ObjectKey);
                Assert.Equal("bigint", columns["Depth"]);
                Assert.Equal("nvarchar(20)", columns["Spud"]);
            }

            // Projected again from the same artifact, the run keeps one set of rows.
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(
                    RunOutcomeStatus.Recorded,
                    (await RunQueueStore.CompleteFromArtifactAsync(db, runId, Artifact(runId, seed), Ended.AddMinutes(1))).Status);
                Assert.Equal(2, await db.ColumnTypeChanges.CountAsync(c => c.RunId == runId));
            }
        }
        finally
        {
            await Cleanup(cs, seed);
        }
    }

    [SkippableFact]
    public async Task ARunRecordedLate_NeverMovesAColumnBack()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var seed = NewSeed();

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                // A later run already widened Depth to decimal(28,0), and the dictionary says so.
                Seed(db, seed, depthType: "decimal(28,0)");
                db.ColumnTypeChanges.Add(new CatalogColumnTypeChange
                {
                    RepoId = seed.RepoId,
                    RunId = Guid.CreateVersion7(),
                    PipelineId = seed.PipelineId,
                    ObjectKey = seed.ObjectKey,
                    TableName = seed.Table,
                    Layer = "target",
                    ColumnName = "Depth",
                    FromType = "bigint",
                    ToType = "decimal(28,0)",
                    Tier = "Widening",
                    Status = "Applied",
                    Message = "widened",
                    OccurredUtc = Ended.AddHours(1),
                });
                await db.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(seed.RepoId, seed.FlowName, "ing"), Ended.AddMinutes(-5))).RunId;
                Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, Ended.AddMinutes(-4)));
                await RunQueueStore.RecordOutcomeAsync(
                    db, runId, Node, 1, RunOutcomeKind.Completed, null, Artifact(runId, seed), Ended.AddSeconds(5));
                Assert.Equal(2, await db.ColumnTypeChanges.CountAsync(c => c.RunId == runId));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                // The earlier change (int to bigint) is recorded in the history, and the column keeps the later type.
                Assert.Equal("decimal(28,0)", (await Columns(db, seed.ObjectKey))["Depth"]);
                Assert.Equal(3, await db.ColumnTypeChanges.CountAsync(c => c.ObjectKey == seed.ObjectKey));
            }
        }
        finally
        {
            await Cleanup(cs, seed);
        }
    }

    /// <summary>One test's keys. The object sits under a server reference of its own (lineage unified the pipeline's
    /// target with it), so its key is one only lineage gives, never the one composed from the pipeline's target server.</summary>
    private sealed record SeedIds(
        Guid RepoId, string FlowName, Guid PipelineId, string ServerRef, string ObjectServerRef, string Table, string TableName,
        string ObjectKey);

    private static SeedIds NewSeed()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("tc_" + suffix);
        var flowName = "tc_flow_" + suffix;
        var serverRef = "${env:tc_" + suffix + "}";
        var objectServerRef = "@tc_" + suffix;
        var tableName = "WellLog_" + suffix;
        return new SeedIds(
            repoId, flowName, CatalogIdentity.Pipeline(repoId, flowName), serverRef, objectServerRef,
            $"[warehouse].[ARC].[{tableName.ToUpperInvariant()}]", tableName,
            $"{objectServerRef}|warehouse|arc|{tableName.ToLowerInvariant()}");
    }

    /// <summary>The pipeline, the object it writes (with its lineage edge) and the object's columns.</summary>
    private static void Seed(CatalogDbContext db, SeedIds seed, string depthType)
    {
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = seed.PipelineId,
            RepoId = seed.RepoId,
            Name = seed.FlowName,
            Kind = "ing",
            RelativePath = $"flows/{seed.FlowName}.flow.yaml",
            SourceServer = seed.ServerRef,
            TargetServer = seed.ServerRef,
            // No content hash: the queue snapshots no flow version for it, so the test leaves none behind.
            ContentHash = string.Empty,
            Yaml = $"name: {seed.FlowName}\n",
            DefinitionJson = "{}",
            Active = true,
            FirstSeenUtc = Ended,
            LastSeenUtc = Ended,
        });
        db.Objects.Add(new CatalogObject
        {
            Key = seed.ObjectKey,
            ServerRef = seed.ObjectServerRef,
            Database = "Warehouse",
            Schema = "arc",
            Name = seed.TableName,
            Kind = "Table",
            FirstSeenUtc = Ended,
            LastSeenUtc = Ended,
        });
        db.LineageEdges.Add(new CatalogLineageEdge
        {
            RepoId = seed.RepoId,
            Flow = seed.FlowName,
            PipelineId = seed.PipelineId,
            Relation = "Writes",
            ObjectKey = seed.ObjectKey,
            ObjectName = $"Warehouse.arc.{seed.TableName}",
            Tier = "Declared",
        });
        db.ObjectColumns.AddRange(
            new CatalogObjectColumn { ObjectKey = seed.ObjectKey, Ordinal = 1, Name = "Depth", DataType = depthType, Tier = "Observed" },
            new CatalogObjectColumn { ObjectKey = seed.ObjectKey, Ordinal = 2, Name = "Spud", DataType = "nvarchar(20)", Nullable = true, Tier = "Observed" });
    }

    /// <summary>An ingestion run's artifact that applied one change and had one refused, the refusal raising its warning.</summary>
    private static string Artifact(Guid runId, SeedIds seed) => $$"""
        {
          "schemaVersion": 1, "flowKind": "ing", "flowName": "{{seed.FlowName}}", "runId": "{{runId}}", "success": true,
          "writtenUtc": "2026-11-27T10:00:01Z",
          "result": {
            "rowsStaged": 12, "startTimeUtc": "2026-11-27T09:59:00Z", "endTimeUtc": "2026-11-27T10:00:00Z",
            "typeChanges": [
              { "layer": "target", "table": "{{seed.Table}}", "column": "Depth", "fromType": "int", "toType": "bigint",
                "tier": "Widening", "status": "Applied", "rewritesTable": true, "isKey": false, "rows": 12,
                "message": "[Depth] int -> bigint: applied." },
              { "layer": "target", "table": "{{seed.Table}}", "column": "Spud", "fromType": "nvarchar(20)", "toType": "date",
                "tier": "CheckedConversion", "status": "Refused", "requiresOptIn": "schema.convertTypes", "rows": 12,
                "failingValues": 2, "sample": [ { "value": "NA", "rows": 3 } ],
                "message": "[Spud] nvarchar(20) -> date: 2 values do not convert.", "manualSteps": [ "Clean them." ] }
            ]
          },
          "events": [
            { "timestampUtc": "2026-11-27T09:59:30Z", "level": "info", "step": "target.typechange", "message": "applied" },
            { "timestampUtc": "2026-11-27T09:59:31Z", "level": "warning", "step": "target.typechange", "message": "refused" }
          ]
        }
        """;

    private static async Task<Dictionary<string, string?>> Columns(CatalogDbContext db, string objectKey)
        => await db.ObjectColumns.AsNoTracking()
            .Where(c => c.ObjectKey == objectKey)
            .ToDictionaryAsync(c => c.Name, c => c.DataType);

    private static async Task Cleanup(string cs, SeedIds seed)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.ColumnTypeChanges.Where(c => c.RepoId == seed.RepoId || c.ObjectKey == seed.ObjectKey).ExecuteDeleteAsync();
        await db.RunEvents.Where(e => e.RepoId == seed.RepoId).ExecuteDeleteAsync();
        await db.RunStatements.Where(s => s.RepoId == seed.RepoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == seed.RepoId).ExecuteDeleteAsync();
        await db.ObjectColumns.Where(c => c.ObjectKey == seed.ObjectKey).ExecuteDeleteAsync();
        await db.LineageEdges.Where(e => e.RepoId == seed.RepoId).ExecuteDeleteAsync();
        await db.Objects.Where(o => o.Key == seed.ObjectKey).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == seed.RepoId).ExecuteDeleteAsync();
    }
}
