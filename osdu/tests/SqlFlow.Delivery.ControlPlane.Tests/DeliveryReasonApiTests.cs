using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Why a record was delivered, as the API serves it (osdu/docs/reference/concepts/delivery-reasons.md): a delivered attempt
/// carries its reason lifted out of its result, one delivered before the ledger kept reasons says its reason was not
/// recorded and guesses nothing, and a submission counts its deliveries by what sent them. The attempts go through the
/// ledger as a worker's completions do, so the triggers the counts read are the ones the bulk append writes.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryReasonApiTests
{
    [Fact]
    public async Task A_delivered_attempt_says_why_it_was_sent_and_its_submission_counts_its_deliveries_by_what_sent_them()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowName = "reason-api-" + Guid.NewGuid().ToString("N")[..8];
        var flowId = FlowId.Of(flowName);
        var sent = new DeliveryKey(Guid.NewGuid());
        var older = new DeliveryKey(Guid.NewGuid());
        var at = new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId);
        var (submission, _) = await ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = Guid.CreateVersion7(),
            FlowId = flowId,
            FlowName = flowName,
            MappingReference = "WellLog@1.4.0",
            RenderContext = "{}",
            Status = SubmissionStatus.Completed,
            Kind = SubmissionKinds.Full,
            SourceConnection = "${env:OSDU_DATA_DB}",
            SourceObject = "OsduData.silver.WellLog",
            ReceivedUtc = at,
        });

        var reason = new DeliveryReason
        {
            Action = "update-metadata",
            Triggers =
            [
                new ReasonTrigger
                {
                    Kind = ReasonKinds.MappingChanged, From = "WellLog@1.4.0", To = "WellLog@1.4.0", FromFingerprint = "1f0c", ToFingerprint = "9a8b",
                    FromCommit = "268b2527d1c04e2fa3b9c8d7e6f5a4b3c2d1e0f9", ToCommit = "852db64a9c0e4f7b8d1e2a3b4c5d6e7f80912345",
                },
                new ReasonTrigger { Kind = ReasonKinds.CacheChanged, From = "20261117T020000Z", To = "20261121T020000Z" },
            ],
            Paths = new PathChanges { Changed = 1, Listed = [new PathChange("acl.viewers", PathChangeKind.Changed)] },
        };

        try
        {
            await ledger.UpsertPendingAsync(flowId, [Pending(flowId, sent, submission.SubmissionId), Pending(flowId, older, submission.SubmissionId)]);
            var lease = "reason-test/" + Guid.NewGuid().ToString("N");
            await ledger.AppendAsync(flowId, lease, new LeaseAppend([],
            [
                Delivered(sent, submission.SubmissionId, at, AttemptResult.WithReason("""{"steps":[]}""", reason), reason.Kinds),
                Delivered(older, submission.SubmissionId, at, """{"steps":[],"detail":"1 chunk(s)"}""", null),
            ]));
            await ledger.CheckpointLeaseAsync(lease, at.AddSeconds(2));

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The reason comes out of the result, whole and marked recorded; the result keeps everything else.
            var attempt = Assert.Single((await JsonAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{sent.Value:D}/attempts")).EnumerateArray().ToList());
            var why = attempt.GetProperty("reason");
            Assert.True(why.GetProperty("recorded").GetBoolean());
            Assert.Equal("mapping WellLog@1.4.0 changed (852db64), cache moved to 20261121T020000Z; acl.viewers changed", why.GetProperty("summary").GetString());
            Assert.Equal("mapping-changed", why.GetProperty("triggers")[0].GetProperty("kind").GetString());
            Assert.Equal("852db64a9c0e4f7b8d1e2a3b4c5d6e7f80912345", why.GetProperty("triggers")[0].GetProperty("toCommit").GetString());
            Assert.Equal("acl.viewers", why.GetProperty("paths").GetProperty("listed")[0].GetProperty("path").GetString());
            Assert.False(attempt.GetProperty("result").TryGetProperty("reason", out _));

            // A delivery made before the ledger kept reasons says so, and nothing more.
            var before = Assert.Single((await JsonAsync(client, token, $"/api/v1/delivery/records/{flowId:D}/{older.Value:D}/attempts")).EnumerateArray().ToList());
            Assert.False(before.GetProperty("reason").GetProperty("recorded").GetBoolean());
            Assert.Equal("reason not recorded", before.GetProperty("reason").GetProperty("summary").GetString());
            Assert.Equal("1 chunk(s)", before.GetProperty("result").GetProperty("detail").GetString());

            // The submission counts its two deliveries: one sent by the mapping and the cache, one with no reason recorded.
            var deliveredBy = (await JsonAsync(client, token, $"/api/v1/delivery/submissions/{submission.SubmissionId:D}")).GetProperty("deliveredBy");
            Assert.Equal(2, deliveredBy.GetProperty("delivered").GetInt64());
            Assert.Equal(1, deliveredBy.GetProperty("notRecorded").GetInt64());
            Assert.Equal(
                [("mapping-changed", 1L), ("cache-changed", 1L)],
                deliveredBy.GetProperty("triggers").EnumerateArray().Select(t => (t.GetProperty("kind").GetString()!, t.GetProperty("records").GetInt64())).ToList());
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            Guid[] keys = [sent.Value, older.Value];
            await osdu.DeliveryAttempts.Where(a => a.FlowId == flowId && keys.Contains(a.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == flowId && keys.Contains(r.DeliveryKey)).ExecuteDeleteAsync();
            await osdu.DeliverySubmissions.Where(s => s.SubmissionId == submission.SubmissionId).ExecuteDeleteAsync();
        }
    }

    private static RecordState Pending(Guid flowId, DeliveryKey key, Guid submissionId) => new()
    {
        DeliveryKey = key,
        FlowId = flowId,
        SourceKey = "LOG-" + key.Value.ToString("N")[..6],
        MappingName = "WellLog",
        Status = RecordStatus.Pending,
        LastSubmissionId = submissionId,
        PendingDocumentRef = "1:0:10",
        PendingMetadata = true,
    };

    private static RecordCompletion Delivered(DeliveryKey key, Guid submissionId, DateTime at, string? resultJson, TriggerKinds? triggers) => new()
    {
        DeliveryKey = key,
        Status = RecordStatus.Delivered,
        Promote = true,
        TargetId = "dev:work-product-component--WellLog:" + key.Value.ToString("N"),
        TargetVersion = 2,
        Attempt = new AttemptRecord
        {
            DeliveryKey = key,
            SubmissionId = submissionId,
            Worker = "reason-test",
            StartedUtc = at,
            CompletedUtc = at.AddSeconds(1),
            Outcome = AttemptOutcome.Delivered,
            Phase = "metadata",
            ResultJson = resultJson,
            Triggers = triggers,
        },
    };

    private static async Task<JsonElement> JsonAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
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
