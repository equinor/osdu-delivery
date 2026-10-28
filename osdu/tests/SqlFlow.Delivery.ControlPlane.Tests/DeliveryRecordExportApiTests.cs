using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The records exports as the API serves them (osdu/docs/reference/concepts/api.md, Flows and records;
/// osdu/docs/reference/cli/records.md, records export): a flow's records a listing filter matches, and the records the
/// lookup across every flow reaches, each as a workbook of the records the page lists, read from the ledger in SQL Server.
/// A filter the listing refuses is refused here too, as a problem rather than a file.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryRecordExportApiTests
{
    private static string MissingWellbore(string wellbore) => $"osdu.data.WellboreID: '{wellbore}' matches no cached Wellbore, and the entry is required";

    [Fact]
    public async Task A_flow_s_records_and_the_lookup_are_exported_as_workbooks_of_the_records_they_list()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var (repoId, pipelineId, flowId, flowName) = await SeedFlowAsync(cs, suffix);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId, TestLedgers.Partition, flowName);
        var held = new[] { "Wellbore C", "wellbore a", "Wellbore B" }
            .Select((label, i) => Held(flowId, $"W-{suffix}-{i + 1}", label, MissingWellbore($"WB-{i + 1}"), i + 1))
            .ToList();
        await ledger.MarkHeldAsync(flowId, held);
        var issue = ProblemSignature.Format(ProblemSignature.Of(held[0].LastError));

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // Every held record of the flow, by record, each with the key column the flow declares, its issue and its problem.
            var (bytes, fileName) = await WorkbookAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/records/export?status=held");
            Assert.Equal($"{flowName}-records-held.xlsx", fileName);
            using (var read = new WorkbookReading(bytes))
            {
                Assert.Equal(["Record", "Source key", "facility_name", "Status", "Blocked", "Issue", "Problem"], read.Headers().Take(7));
                var rows = read.Rows();
                Assert.Equal(["wellbore a", "Wellbore B", "Wellbore C"], rows.Select(r => r["Record"].GetText()));
                Assert.All(rows, row =>
                {
                    var record = held.Single(h => h.DeliveryKey.Value.ToString("D") == row["Delivery key"].GetText());
                    Assert.Equal(record.SourceKey, row["Source key"].GetText());
                    Assert.Equal(record.SourceKey, row["facility_name"].GetText());
                    Assert.Equal(("held", "yes", issue), (row["Status"].GetText(), row["Blocked"].GetText(), row["Issue"].GetText()));
                    Assert.Equal(record.LastError, row["Problem"].GetText());
                    Assert.Equal(("wells.csv", (double)record.PendingSourceRowNumber!), (row["Source file"].GetText(), row["Source row"].GetNumber()));
                });

                var about = read.About();
                Assert.Equal((flowName, TestLedgers.Partition), (about["Flow"].GetText(), about["Partition"].GetText()));
                Assert.Equal(("status held", 3d), (about["Filter"].GetText(), about["Records"].GetNumber()));
            }

            // A filter the listing refuses is refused here as a problem, and one matching nothing is the header row alone.
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/records/export?status=nonsense", HttpStatusCode.BadRequest);
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/records/export?issue=xyz", HttpStatusCode.BadRequest);
            await ProblemAsync(client, token, $"/api/v1/delivery/flows/{Guid.NewGuid():D}/records/export", HttpStatusCode.NotFound);
            using (var none = new WorkbookReading((await WorkbookAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/records/export?status=delivered")).Bytes))
            {
                Assert.Empty(none.Rows());
                Assert.Equal(0d, none.About()["Records"].GetNumber());
            }

            // The lookup narrowed to the flow: the same records, each naming the flow that delivers it and its partition.
            var (recent, recentName) = await WorkbookAsync(client, token, $"/api/v1/delivery/records/export?flowId={flowId:D}&status=held");
            Assert.StartsWith("records-", recentName, StringComparison.Ordinal);
            Assert.EndsWith("-held.xlsx", recentName, StringComparison.Ordinal);
            using (var read = new WorkbookReading(recent))
            {
                Assert.Equal(["Record", "Source key", "Flow", "Interface", "Partition", "Status"], read.Headers().Take(6));
                var rows = read.Rows();
                Assert.Equal(["wellbore a", "Wellbore B", "Wellbore C"], rows.Select(r => r["Record"].GetText()));
                Assert.All(rows, row => Assert.Equal((flowName, TestLedgers.Partition), (row["Flow"].GetText(), row["Partition"].GetText())));
                var about = read.About();
                Assert.StartsWith("none:", about["Lookup"].GetText(), StringComparison.Ordinal);
                Assert.False(about.ContainsKey("Reach"));
            }

            // With a term, each record says which of its values the term matched.
            using (var read = new WorkbookReading((await WorkbookAsync(client, token, $"/api/v1/delivery/records/export?search=W-{suffix}&flowId={flowId:D}")).Bytes))
            {
                Assert.Contains("Matched", read.Headers());
                Assert.Equal(3, read.Rows().Count);
                Assert.Equal($"'W-{suffix}': the start of an identity, the source key, a word of the label, the OSDU id, the delivery key or the file", read.About()["Lookup"].GetText());
            }

            // A term matched anywhere finds what no value starts with, and the About sheet says how it matched.
            using (var read = new WorkbookReading((await WorkbookAsync(client, token, $"/api/v1/delivery/records/export?search=ellbore&mode=contains&flowId={flowId:D}")).Bytes))
            {
                Assert.Equal(["wellbore a", "Wellbore B", "Wellbore C"], read.Rows().Select(r => r["Record"].GetText()));
                Assert.Equal("'ellbore' anywhere in the source key, the label, the OSDU id or the file, or the delivery key", read.About()["Lookup"].GetText());
                Assert.False(read.About().ContainsKey("Narrowed to"));
            }

            await ProblemAsync(client, token, "/api/v1/delivery/records/export?status=nonsense", HttpStatusCode.BadRequest);
        }
        finally
        {
            await CleanAsync(cs, repoId, flowId);
        }
    }

    /// <summary>A delivery flow keyed by one column, in a repository of its own in the catalog, named with <paramref name="suffix"/>.</summary>
    private static async Task<(Guid RepoId, Guid PipelineId, Guid FlowId, string FlowName)> SeedFlowAsync(string cs, string suffix)
    {
        var flowName = "api-export-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-export-" + suffix);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var now = DateTime.UtcNow;
        var yaml = $$"""
            flowType: delivery
            name: {{flowName}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.Wellbore, key: [facility_name] }
              work: ../.work/export
            render:
              mapping: Wellbore@1.0.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: storage
              headers: { data-partition-id: dev }
            """;

        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId, Name = "cp-export-" + suffix, RemoteUrl = "https://example/cp-export.git",
            RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
        });
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = pipelineId,
            RepoId = repoId,
            Name = flowName,
            Kind = "delivery",
            RelativePath = "flows/" + flowName + ".yaml",
            ContentHash = new string('0', 64),
            Yaml = yaml,
            DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{flowName}}","flowKind":"delivery"}"""),
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });
        await db.SaveChangesAsync();
        return (repoId, pipelineId, FlowId.Of(flowName), flowName);
    }

    /// <summary>Removes what a test of this class left: the flow's ledger rows, and the repository's catalog rows.</summary>
    private static async Task CleanAsync(string cs, Guid repoId, Guid flowId)
    {
        await using (var osdu = SampleEstate.Context(cs))
        {
            await osdu.DeliveryAttempts.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => i.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
        }

        await using (var db = CatalogDatabase.Create(cs))
        {
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static RecordState Held(Guid flowId, string sourceKey, string label, string error, int row) => new()
    {
        DeliveryKey = DeliveryKey.Derive("export", [sourceKey]),
        FlowId = flowId,
        SourceKey = sourceKey,
        SourceKeyJson = JsonSerializer.Serialize(new[] { sourceKey }),
        Label = label,
        MappingName = "Wellbore",
        LastSubmissionId = Guid.NewGuid(),
        PendingSourceFileName = "wells.csv",
        PendingSourceRowNumber = row,
        LastError = error,
    };

    /// <summary>A workbook the route answers with, and the name it is sent as.</summary>
    private static async Task<(byte[] Bytes, string FileName)> WorkbookAsync(HttpClient client, string token, string path)
    {
        using var response = await SendAsync(client, token, path);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path} answered {(int)response.StatusCode}: {System.Text.Encoding.UTF8.GetString(bytes)}");
        Assert.Equal(RecordExport.MediaType, response.Content.Headers.ContentType?.MediaType);
        return (bytes, response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName ?? string.Empty);
    }

    /// <summary>A refusal: the status asked, as a problem document rather than a file.</summary>
    private static async Task ProblemAsync(HttpClient client, string token, string path, HttpStatusCode expected)
    {
        using var response = await SendAsync(client, token, path);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"GET {path} answered {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
