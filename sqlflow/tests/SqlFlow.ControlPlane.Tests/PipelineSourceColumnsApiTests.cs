using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A file flow's source, scanned file by file through <c>POST /pipelines/{id}/source-columns</c> as a run of the flow reads
/// it: the control plane queues a compute task carrying the flow's document as the catalog stores it and the table it loads
/// into, under the pipeline's repository, so the task carries the values that repository's runs are supplied with; the
/// host's worker reads the newest files' headers and answers. The flow's location names a reference only those supplied
/// values resolve, never the process's environment, as a deployed flow's landing location does. A flow that reads no
/// files, an unbounded count and a scan queued through the generic task endpoint are refused. The repository is a folder
/// of the test's own; every catalog row is removed in a finally.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PipelineSourceColumnsApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow_srccols_" + Guid.NewGuid().ToString("N"));

    [SkippableFact]
    public async Task AFileFlowsSource_IsReadOnANode_WithTheValuesItsRunsAreSupplied_AgainstTheTableItLoadsInto()
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

        // The landing location, as a deployment's central configuration supplies it with every run of the repository.
        var landing = "SRCCOLS_LANDING_" + suffix.ToUpperInvariant();
        Assert.Null(Environment.GetEnvironmentVariable(landing));
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal) { [landing] = Path.Combine(_root, "landing") };

        Directory.CreateDirectory(Path.Combine(_root, "flows"));
        Directory.CreateDirectory(Path.Combine(_root, "landing", "wells"));
        WriteFile("landing/wells/wells_1.csv", "well_id,well name,elevation\nW1,One,10\n", DateTime.UtcNow.AddDays(-2));
        WriteFile("landing/wells/wells_2.csv", "well_id,well name,elevation,spud_date\nW2,Two,11,2026-01-01\n", DateTime.UtcNow.AddDays(-1));
        var yaml = $$"""
            name: {{flowName}}
            source:
              type: csv
              location: ${env:{{landing}}}/wells
              options:
                srcFile: "*.csv"
            target:
              connection: {{connection}}
              schema: pre
              table: {{table}}
            """;

        await using var factory = new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithServices(services => services.AddSingleton<IRunReferenceSource>(new RepositorySource(repoId, supplied)));
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

            SourceColumnsScanAccepted accepted;
            using (var response = await SendAsync(
                       client, token, HttpMethod.Post, $"/api/v1/pipelines/{pipelineId}/source-columns",
                       new { expect = new[] { "spud_date", " Well Name ", "depth", "" } }))
            {
                var text = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"the scan answered {(int)response.StatusCode}: {text}");
                accepted = JsonSerializer.Deserialize<SourceColumnsScanAccepted>(text, Web)!;
                Assert.Equal((pipelineId, flowName), (accepted.PipelineId, accepted.Flow));
                Assert.Equal($"/api/v1/datasources/tasks/{accepted.TaskId}", response.Headers.Location?.ToString());
            }

            // The queued task carries the pipeline's own document and the values its repository's runs are supplied with.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var task = await db.ComputeTasks.AsNoTracking().SingleAsync(t => t.TaskId == accepted.TaskId);
                Assert.Equal((ComputeOperations.ScanSourceColumns, flowName), (task.Operation, task.SourceRef));
                var payload = ComputeTaskPayload.FromJson(task.ArgumentsJson);
                Assert.Equal(yaml, payload.Flow);
                Assert.Equal(Path.GetFullPath(Path.Combine(_root, "flows/wells.yaml")), payload.FlowFile);
                Assert.Equal(supplied, payload.References);
                Assert.Equal(objectKey, payload.ScanTarget?.ObjectKey);
            }

            var scan = await WaitAsync(client, token, accepted.TaskId);
            Assert.Equal((flowName, "csv", $"${{env:{landing}}}/wells"), (scan.Flow, scan.SourceType, scan.Location));
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

            // A flow that reads no files has no source to scan, the count is bounded, an unknown pipeline is not found,
            // and no client queues a scan of a document or a location of its own.
            await RefusedAsync(client, token, $"/api/v1/pipelines/{procId}/source-columns", new { }, HttpStatusCode.BadRequest);
            await RefusedAsync(client, token, $"/api/v1/pipelines/{pipelineId}/source-columns", new { files = 500 }, HttpStatusCode.BadRequest);
            await RefusedAsync(client, token, $"/api/v1/pipelines/{Guid.NewGuid()}/source-columns", new { }, HttpStatusCode.NotFound);
            await RefusedAsync(
                client, token, "/api/v1/datasources/tasks",
                new { reference = flowName, operation = ComputeOperations.ScanSourceColumns }, HttpStatusCode.BadRequest);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.ComputeTasks.Where(t => t.SourceRef == flowName || t.SourceRef == procName).ExecuteDeleteAsync();
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

    /// <summary>Long-polls the scan's task to its end (bounded, so a stuck worker fails rather than hangs) and reads its
    /// answer.</summary>
    private static async Task<SourceColumnsReport> WaitAsync(HttpClient client, string token, Guid taskId)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var detail = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/datasources/tasks/{taskId}?waitMs=2000", null);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var body = await detail.Content.ReadAsStringAsync();
            using var task = JsonDocument.Parse(body);
            var status = task.RootElement.GetProperty("status").GetString();
            if (status is "queued" or "running")
            {
                continue;
            }

            Assert.True(status == "succeeded", $"the scan ended '{status}': {body}");
            return task.RootElement.GetProperty("result").Deserialize<SourceColumnsReport>(Web)!;
        }

        throw new Xunit.Sdk.XunitException($"the scan's task {taskId} did not finish.");
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

    private static async Task RefusedAsync(HttpClient client, string token, string relativeUri, object body, HttpStatusCode expected)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, relativeUri, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"POST {relativeUri} answered {(int)response.StatusCode}, not {(int)expected}: {text}");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string relativeUri, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(relativeUri, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    /// <summary>The values a deployment's central configuration supplies with the runs of one repository, and nothing for
    /// any other: the host's scheduler and managed sync share the test database with other tests.</summary>
    private sealed class RepositorySource(Guid repoId, IReadOnlyDictionary<string, string> values) : IRunReferenceSource
    {
        public Task<IReadOnlyDictionary<string, string>> ReferencesAsync(Guid? repo, CancellationToken ct)
            => Task.FromResult(repo == repoId ? values : new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
