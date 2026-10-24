using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The missing documents and their takeover, as the API serves them (osdu/docs/reference/concepts/api.md, Missing
/// documents): a flow no repository declares any more is listed with what it left behind and the declared flow that pins its
/// mapping suggested first; a takeover without its names is refused, one names the flow that takes the ledger over, moves
/// it as the caller and answers how each ledger ended, and a document taken over is missing no more.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryOrphanedLedgerApiTests
{
    [Fact]
    public async Task A_missing_document_is_listed_and_taken_over_by_the_flow_named_as_the_caller()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var oldName = "api-orphan-" + suffix;
        var newName = "api-orphan-v2-" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-orphans-" + suffix);
        var (old, renamed) = (FlowId.Of(oldName), FlowId.Of(newName));

        // The old flow left its repository with a delivered record; the renamed one, which pins the same mapping, is declared,
        // in the catalog and in the read model as a sync describes it now, so the control plane's start-up backfill leaves it.
        await SeedCatalogAsync(cs, repoId, newName);
        await using (var osdu = SampleEstate.Context(cs))
        {
            osdu.DeliveryInterfaces.AddRange(Interface(repoId, oldName, active: false), Interface(repoId, newName, active: true));
            await osdu.SaveChangesAsync();
        }

        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(old, TestLedgers.Partition, oldName);
        await ledger.UpsertPendingAsync(old, [Claiming(old, suffix)]);

        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            using (var listed = await SendAsync(client, token, HttpMethod.Get, $"/api/v1/delivery/ledgers/orphaned?partition={TestLedgers.Partition}", null))
            {
                Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
                var documents = JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement;
                var missing = Assert.Single(documents.EnumerateArray(), d => d.GetProperty("flowName").GetString() == oldName);
                Assert.Equal(("delivery", 1L, 1L), (missing.GetProperty("kind").GetString(), missing.GetProperty("rows").GetInt64(), missing.GetProperty("claimed").GetInt64()));
                var first = missing.GetProperty("candidates")[0];
                Assert.Equal((newName, true), (first.GetProperty("flowName").GetString(), first.GetProperty("suggested").GetBoolean()));
            }

            using (var incomplete = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/ledgers/orphaned/takeover", new { kind = "delivery", from = oldName }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
            }

            var takeover = new { kind = "delivery", from = oldName, to = newName, partition = TestLedgers.Partition };
            using (var taken = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/ledgers/orphaned/takeover", takeover))
            {
                var text = await taken.Content.ReadAsStringAsync();
                Assert.True(taken.StatusCode == HttpStatusCode.OK, text);
                var answer = JsonDocument.Parse(text).RootElement;
                Assert.Equal((1, 0, 0, 0), (answer.GetProperty("completed").GetInt32(), answer.GetProperty("waiting").GetInt32(), answer.GetProperty("failed").GetInt32(), answer.GetProperty("skipped").GetInt32()));
                var move = Assert.Single(answer.GetProperty("ledgers").EnumerateArray());
                Assert.Equal((LedgerMoves.Completed, renamed), (move.GetProperty("outcome").GetString(), move.GetProperty("to").GetGuid()));
            }

            // The record is the renamed flow's, moved as the caller, and the document is missing no more.
            Assert.Equal($"dev:work-product-component--WellLog:{suffix}", Assert.Single(await ledger.ListAsync(renamed, new RecordQuery())).ClaimedTargetId);
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = renamed, Kind = LedgerMoves.Kind }));
            Assert.StartsWith("user:", activity.Actor, StringComparison.Ordinal);
            using var again = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/ledgers/orphaned/takeover", takeover);
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            Guid[] ids = [old, renamed];
            await osdu.DeliveryActivityRecords.Where(a => ids.Contains(a.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryActivities.Where(a => ids.Contains(a.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryAttempts.Where(a => ids.Contains(a.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => ids.Contains(i.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => ids.Contains(r.FlowId)).ExecuteDeleteAsync();
            await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
            await osdu.DeliveryLedgers.Where(l => ids.Contains(l.FlowId)).ExecuteDeleteAsync();
            await using var catalog = CatalogDatabase.Create(cs);
            await catalog.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await catalog.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>The repository in the catalog, declaring <paramref name="flowName"/>.</summary>
    private static async Task SeedCatalogAsync(string cs, Guid repoId, string flowName)
    {
        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo { Id = repoId, Name = "cp-" + flowName, RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now });
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = CatalogIdentity.Pipeline(repoId, flowName),
            RepoId = repoId,
            Name = flowName,
            Kind = "delivery",
            RelativePath = $"flows/{flowName}.yaml",
            ContentHash = new string('0', 64),
            Yaml = $"flowType: delivery\nname: {flowName}\n",
            DefinitionJson = $$"""{"name":"{{flowName}}","flowKind":"delivery"}""",
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The single interface of <paramref name="flowName"/>, as the sync describes a flow that names no partitions.</summary>
    private static DeliveryInterface Interface(Guid repoId, string flowName, bool active) => new()
    {
        Id = FlowIdentity.FromName($"delivery-interface/{repoId:N}/{flowName}/"),
        RepoId = repoId,
        FlowName = flowName,
        LedgerFlowId = FlowId.Of(flowName),
        LedgerName = flowName,
        Route = "storage",
        MappingReference = "WellLog@1.4.0",
        RecordObject = "Wells.ing.WellLog",
        AfterJson = "[]",
        RelativePath = $"flows/{flowName}.yaml",
        CacheVersion = "current",
        DocumentHash = new string('d', 64),
        Active = active,
        FirstSeenUtc = DateTime.UtcNow,
        LastSeenUtc = DateTime.UtcNow,
    };

    /// <summary>Work for a record of <paramref name="flow"/> that queues a document, which claims the record's OSDU id.</summary>
    private static RecordState Claiming(Guid flow, string key) => new()
    {
        DeliveryKey = DeliveryKey.Derive("test", [key]),
        FlowId = flow,
        SourceKey = key,
        MappingName = "WellLog",
        TargetId = "dev:work-product-component--WellLog:" + key,
        LastSubmissionId = Guid.NewGuid(),
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingSourceFingerprint = "fp",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
    };

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
