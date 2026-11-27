using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A file flow's source, scanned file by file through <c>GET /pipelines/{id}/source-columns</c>: the flow's document as the
/// catalog stores it, its relative location resolved against its folder in the repository's checkout, the newest files'
/// headers, and the table it loads into as the catalog holds it. The answer names the columns new to the table, the
/// table's columns no file carries (leaving out the ones a load adds itself), and the columns asked about. A flow that
/// reads no files is refused. The repository is a folder of the test's own; every catalog row is removed in a finally.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PipelineSourceColumnsApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow_srccols_" + Guid.NewGuid().ToString("N"));

    [SkippableFact]
    public async Task AFileFlowsSource_IsReadFileByFile_AgainstTheTableItLoadsInto()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("srccols_" + suffix);
        var flowName = "srccols_wells_" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var procName = "srccols_proc_" + suffix;
        var procId = CatalogIdentity.Pipeline(repoId, procName);
        var table = "Wells_" + suffix;
        var connection = "${env:srccols_" + suffix + "}";
        var objectKey = $"{connection.ToLowerInvariant()}|landing|pre|{table.ToLowerInvariant()}";

        Directory.CreateDirectory(Path.Combine(_root, "flows"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "wells"));
        WriteFile("data/wells/wells_1.csv", "well_id,well name,elevation\nW1,One,10\n", DateTime.UtcNow.AddDays(-2));
        WriteFile("data/wells/wells_2.csv", "well_id,well name,elevation,spud_date\nW2,Two,11,2026-01-01\n", DateTime.UtcNow.AddDays(-1));
        var yaml = $"""
            name: {flowName}
            source:
              type: csv
              location: ../data/wells
              options:
                srcFile: "*.csv"
            target:
              connection: {connection}
              schema: pre
              table: {table}
            """;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                var now = DateTime.UtcNow;
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = "srccols_" + suffix, RootPath = _root, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(Pipeline(pipelineId, repoId, flowName, "file", "flows/wells.yaml", yaml, now));
                db.Pipelines.Add(Pipeline(procId, repoId, procName, "sp", "flows/proc.yaml", "flowType: sp\n", now));
                db.Objects.Add(new CatalogObject
                {
                    Key = objectKey, ServerRef = connection, Database = "Landing", Schema = "pre", Name = table,
                    Kind = "Table", FirstSeenUtc = now, LastSeenUtc = now,
                });
                var ordinal = 0;
                foreach (var name in new[] { "well_id", "well_name", "elevation", "legacy_code", "FileName_DW", "FileDate_DW" })
                {
                    db.ObjectColumns.Add(new CatalogObjectColumn
                    {
                        ObjectKey = objectKey, Ordinal = ++ordinal, Name = name, DataType = "nvarchar(max)", Nullable = true, Tier = "Derived",
                    });
                }

                db.LineageEdges.Add(new CatalogLineageEdge
                {
                    RepoId = repoId, Flow = flowName, PipelineId = pipelineId, Relation = "Writes", ObjectKey = objectKey,
                    ObjectName = table, Tier = "Declared",
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate"]);

            using var response = await GetAsync(client, token, $"/api/v1/pipelines/{pipelineId}/source-columns?expect=spud_date,Well Name,depth");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var scan = await response.Content.ReadFromJsonAsync<SourceColumnsDto>();
            Assert.NotNull(scan);

            Assert.Equal((flowName, "csv", "../data/wells"), (scan.Flow, scan.SourceType, scan.Location));
            Assert.Equal((2, 2), (scan.FilesMatched, scan.FilesRead));
            Assert.Equal(["wells_2.csv", "wells_1.csv"], scan.Files.Select(f => f.Name));
            Assert.Equal(["well_id", "well_name", "elevation", "spud_date"], scan.Columns.Select(c => c.Name));
            Assert.Equal([2, 2, 2, 1], scan.Columns.Select(c => c.Files));
            Assert.Equal(objectKey, scan.Target?.ObjectKey);

            // A column the newest file brought, and a column of the table no file carries any more.
            Assert.Equal(["spud_date"], scan.NewColumns);
            Assert.Equal(["legacy_code"], scan.NotInFiles);

            Assert.Collection(scan.Expected,
                spud =>
                {
                    Assert.Equal(("spud_date", 1, false, false), (spud.Name, spud.Files, spud.InEveryFile, spud.InTarget));
                    Assert.Equal(["wells_1.csv"], spud.MissingFrom);
                },
                // Asked as the file writes it, found as the load names it.
                name => Assert.Equal(("Well Name", 2, true), (name.Name, name.Files, name.InEveryFile)),
                depth => Assert.Equal(("depth", 0, false), (depth.Name, depth.Files, depth.InEveryFile)));

            // A flow that reads no files has no source to scan, and the count is bounded.
            using var proc = await GetAsync(client, token, $"/api/v1/pipelines/{procId}/source-columns");
            Assert.Equal(HttpStatusCode.BadRequest, proc.StatusCode);
            using var tooMany = await GetAsync(client, token, $"/api/v1/pipelines/{pipelineId}/source-columns?files=500");
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.ObjectColumns.Where(c => c.ObjectKey == objectKey).ExecuteDeleteAsync();
            await db.Objects.Where(o => o.Key == objectKey).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, string kind, string path, string yaml, DateTime now) => new()
    {
        Id = id, RepoId = repoId, Name = name, Kind = kind, RelativePath = path, Active = true, Yaml = yaml,
        DefinitionJson = "{}", SourceServer = SqlFlow.Lineage.Collection.ServerIdentity.FileSystem, TargetServer = SqlFlow.Lineage.Collection.ServerIdentity.FileSystem,
        FirstSeenUtc = now, LastSeenUtc = now,
    };

    private void WriteFile(string relative, string content, DateTime modifiedUtc)
    {
        var path = Path.Combine(_root, relative);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
