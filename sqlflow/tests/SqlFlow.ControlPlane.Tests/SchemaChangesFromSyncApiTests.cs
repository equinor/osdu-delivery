using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Dispatch;
using SqlFlow.Lineage.Collection;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The catalog's own schema history, through a schema registration flow (flowType: sch) whose runs read a database live:
/// the first registration is the baseline and records nothing; a later one that reads a column retyped, one added, and a
/// table the flow had not registered before records each as a change of that run, with the columns that moved; a re-read
/// that finds the same columns records nothing and rewrites nothing. The Schema changes feed returns the moved columns and
/// finds a change by one of them, and the comparison of a change reads the object's columns before and now.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchemaChangesFromSyncApiTests
{
    [SkippableFact]
    public async Task Registrations_RecordWhatMoved_AndTheFeedAndComparisonShowIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("schhist_" + suffix);
        var flowName = "schhist_" + suffix + "_00_sch";
        var serverRef = "${env:SQLFLOW_SCHHIST_" + suffix.ToUpperInvariant() + "}";
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var orders = NodeKey.For(serverRef, "Registry", "dbo", "Orders");
        var customers = NodeKey.For(serverRef, "Registry", "dbo", "Customers");

        var baseline = new Table("Orders", [("Id", "int", false), ("Name", "nvarchar(50)", true)]);
        var widened = new Table("Orders", [("Id", "int", false), ("Name", "nvarchar(100)", true), ("Amount", "decimal(18,2)", true)]);
        var added = new Table("Customers", [("Id", "int", false)]);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var now = DateTime.UtcNow;
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = "repo_" + flowName, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = pipelineId, RepoId = repoId, Name = flowName, Kind = "sch", RelativePath = flowName + ".yaml",
                    Active = true, SourceServer = serverRef, TargetServer = ServerIdentity.FileSystem,
                    DefinitionJson = "{}", FirstSeenUtc = now, LastSeenUtc = now, Yaml = "flowType: sch\n",
                });
                await db.SaveChangesAsync();
            }

            // The first registration reads what was already there: no change.
            await RegisterAsync(cs, repoId, flowName, baseline);
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.False(await db.SchemaChanges.AnyAsync(c => c.RepoId == repoId));
                Assert.Equal(2, await db.ObjectColumns.CountAsync(c => c.ObjectKey == orders));
            }

            var second = await RegisterAsync(cs, repoId, flowName, widened, added);
            List<long> columnIds;
            await using (var db = CatalogDatabase.Create(cs))
            {
                var changes = await db.SchemaChanges.AsNoTracking().Include(c => c.Columns)
                    .Where(c => c.RepoId == repoId).OrderBy(c => c.Name).ToListAsync();
                Assert.Equal(["Customers", "Orders"], changes.Select(c => c.Name));
                Assert.All(changes, c =>
                {
                    Assert.Equal(SchemaChangeOrigins.Sync, c.Origin);
                    Assert.Equal(second, c.RunId);
                    Assert.Equal(pipelineId, c.PipelineId);
                    Assert.Equal(("Registry", "Table", "dbo"), (c.Database, c.Category, c.Schema));
                    Assert.Null(c.CommitSha);
                });

                var customer = changes[0];
                Assert.Equal((SchemaChangeKinds.Added, customers), (customer.ChangeType, customer.ObjectKey));
                var id = Assert.Single(customer.Columns);
                Assert.Equal(("Id", SchemaChangeKinds.Added, "int"), (id.Name, id.ChangeType, id.ToType));

                var order = changes[1];
                Assert.Equal((SchemaChangeKinds.Changed, orders), (order.ChangeType, order.ObjectKey));
                Assert.Collection(order.Columns.OrderBy(c => c.ToOrdinal),
                    name => Assert.Equal(("Name", SchemaChangeKinds.Changed, "nvarchar(50)", "nvarchar(100)"), (name.Name, name.ChangeType, name.FromType, name.ToType)),
                    amount => Assert.Equal(("Amount", SchemaChangeKinds.Added, "decimal(18,2)"), (amount.Name, amount.ChangeType, amount.ToType)));

                columnIds = await db.ObjectColumns.Where(c => c.ObjectKey == orders).OrderBy(c => c.Ordinal).Select(c => c.Id).ToListAsync();
            }

            // The same columns again: nothing recorded, nothing rewritten.
            await RegisterAsync(cs, repoId, flowName, widened, added);
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(2, await db.SchemaChanges.CountAsync(c => c.RepoId == repoId));
                Assert.Equal(columnIds, await db.ObjectColumns.Where(c => c.ObjectKey == orders).OrderBy(c => c.Ordinal).Select(c => c.Id).ToListAsync());
            }

            using var client = factory.CreateClient();
            var token = await AiContextTestKit.IssueTokenAsync(client, ["read"]);

            // A change is found by a column it moved, and carries the columns.
            var found = await GetAsync<PagedResult<SchemaChangeDto>>(client, token, $"/api/v1/schema-changes?repoId={repoId}&search=Amount");
            var row = Assert.Single(found.Items);
            Assert.Equal(("Orders", SchemaChangeOrigins.Sync, orders), (row.Name, row.Origin, row.ObjectKey));
            Assert.Equal(["Name", "Amount"], row.Columns.Select(c => c.Name));

            var byObject = await GetAsync<PagedResult<SchemaChangeDto>>(
                client, token, $"/api/v1/schema-changes?repoId={repoId}&objectKey={Uri.EscapeDataString(customers)}");
            Assert.Equal(["Customers"], byObject.Items.Select(c => c.Name));

            // Before and now: the first read kept, against the columns the catalog holds today.
            var compared = await GetAsync<SchemaObjectCompareDto>(client, token, $"/api/v1/schema-changes/{row.Id}/compare");
            Assert.Equal((SchemaChangeOrigins.Sync, "Registry.dbo.Orders"), (compared.Origin, compared.Path));
            Assert.Null(compared.Before);
            Assert.Null(compared.After);
            Assert.Equal("[Id] int NOT NULL\n[Name] nvarchar(50) NULL", compared.BeforeText);
            Assert.Equal("[Id] int NOT NULL\n[Name] nvarchar(100) NULL\n[Amount] decimal(18,2) NULL", compared.AfterText);
            Assert.Equal((2, 1), (compared.LinesAdded, compared.LinesDeleted));

            // An object added inside the window did not exist before it.
            var newcomer = byObject.Items[0];
            var fresh = await GetAsync<SchemaObjectCompareDto>(client, token, $"/api/v1/schema-changes/{newcomer.Id}/compare");
            Assert.Null(fresh.BeforeText);
            Assert.Equal("[Id] int NOT NULL", fresh.AfterText);

            // A window opening after the change holds no change: before is now.
            var later = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(5).ToString("O"));
            var unchanged = await GetAsync<SchemaObjectCompareDto>(client, token, $"/api/v1/schema-changes/{row.Id}/compare?since={later}");
            Assert.Equal(unchanged.AfterText, unchanged.BeforeText);
            Assert.Equal((0, 0), (unchanged.LinesAdded, unchanged.LinesDeleted));
        }
        finally
        {
            var prefix = serverRef.ToLowerInvariant() + "|";
            await using var db = CatalogDatabase.Create(cs);
            await db.SchemaChanges.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.ObjectColumns.Where(c => c.ObjectKey.StartsWith(prefix)).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key.StartsWith(prefix)).ExecuteDeleteAsync();
            await db.RunStatements.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>Enqueues and completes one registration run that read <paramref name="tables"/>; returns its run id.</summary>
    private static async Task<Guid> RegisterAsync(string cs, Guid repoId, string flowName, params Table[] tables)
    {
        await using var db = CatalogDatabase.Create(cs);
        var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "sch"), DateTime.UtcNow)).RunId;
        var outcome = await RunQueueStore.CompleteFromArtifactAsync(db, runId, Artifact(runId, flowName, tables), DateTime.UtcNow);
        Assert.Equal(RunOutcomeStatus.Recorded, outcome.Status);
        return runId;
    }

    private static string Artifact(Guid runId, string flowName, IReadOnlyList<Table> tables)
    {
        var objects = string.Join(",", tables.Select(table =>
        {
            var columns = string.Join(",", table.Columns.Select((c, i) =>
                $$"""{ "ordinal": {{i + 1}}, "name": "{{c.Name}}", "dataType": "{{c.Type}}", "nullable": {{(c.Nullable ? "true" : "false")}} }"""));
            return $$"""
                { "schema": "dbo", "name": "{{table.Name}}", "kind": "Table", "keyColumns": ["Id"],
                  "script": "CREATE TABLE [dbo].[{{table.Name}}] ([Id] int NOT NULL)", "columns": [ {{columns}} ] }
                """;
        }));
        return $$"""
            {
              "schemaVersion": 1,
              "flowKind": "sch",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": true,
              "writtenUtc": "2026-11-27T10:00:00Z",
              "result": {
                "runId": "{{runId}}",
                "flowName": "{{flowName}}",
                "success": true,
                "database": "Registry",
                "objects": [ {{objects}} ],
                "tables": {{tables.Count}},
                "views": 0,
                "columns": {{tables.Sum(t => t.Columns.Count)}},
                "warnings": [],
                "startedUtc": "2026-11-27T09:59:59Z",
                "endedUtc": "2026-11-27T10:00:00Z",
                "durationSeconds": 1.0
              }
            }
            """;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{relativeUri}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }

    /// <summary>One table a registration run read: its name and its columns, in order.</summary>
    private sealed record Table(string Name, IReadOnlyList<(string Name, string Type, bool Nullable)> Columns);
}
