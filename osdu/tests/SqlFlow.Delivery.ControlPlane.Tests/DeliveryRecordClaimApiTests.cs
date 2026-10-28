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
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Whether a record's flow wrote to OSDU under its id, as the API serves it (osdu/docs/reference/tables/record.md,
/// ClaimedTargetId). A record names the id its key makes from its first plan, but only one that queued a document claimed
/// it, and only a claimed id is read back. The record page reads, and offers a read of, only a record whose flow wrote there:
/// one that was only ever held says it was never sent rather than asking for a read the control plane refuses.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DeliveryRecordClaimApiTests
{
    private const string FlowName = "wells-wellbore-03-claim-delivery";

    [Fact]
    public async Task A_record_says_whether_its_flow_claimed_the_osdu_id_it_names()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var flowId = FlowId.Of(FlowName);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var queued = Record(flowId, $"WB-QUEUED-{suffix}");
        var held = Record(flowId, $"WB-HELD-{suffix}");
        var ledger = new OsduLedger(() => SampleEstate.Context(cs));
        await ledger.RegisterAsync(flowId);

        try
        {
            // One record queued a document under its id, which claims it for the flow; the other was held by its plan, and
            // names the id its key makes without its flow having written anything there.
            await ledger.UpsertPendingAsync(flowId, [queued with { Status = RecordStatus.Pending, PendingDocumentRef = "1:0:10", PendingMetadata = true }]);
            await ledger.MarkHeldAsync(flowId, [held with
            {
                LastError = "osdu.data.Curves[].CurveUnit, in 1 of 13 items: dataset.curves.curve_unit is empty, and the entry is required",
            }]);

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var claimed = await RecordAsync(client, token, flowId, queued.DeliveryKey);
            Assert.Equal((queued.TargetId, true), (claimed.GetProperty("targetId").GetString(), claimed.GetProperty("claimed").GetBoolean()));

            var unclaimed = await RecordAsync(client, token, flowId, held.DeliveryKey);
            Assert.Equal(
                (held.TargetId, "held", false),
                (unclaimed.GetProperty("targetId").GetString(), unclaimed.GetProperty("status").GetString(), unclaimed.GetProperty("claimed").GetBoolean()));
        }
        finally
        {
            await using var osdu = SampleEstate.Context(cs);
            await osdu.DeliveryAttempts.Where(a => a.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecordIdentities.Where(i => i.FlowId == flowId).ExecuteDeleteAsync();
            await osdu.DeliveryRecords.Where(r => r.FlowId == flowId).ExecuteDeleteAsync();
        }
    }

    private static RecordState Record(Guid flowId, string sourceKey) => new()
    {
        DeliveryKey = new DeliveryKey(Guid.NewGuid()),
        FlowId = flowId,
        SourceKey = sourceKey,
        Label = sourceKey,
        MappingName = SampleEstate.WellboreMapping,
        TargetId = $"dev:master-data--Wellbore:{sourceKey}",
        LastSubmissionId = Guid.NewGuid(),
    };

    private static async Task<JsonElement> RecordAsync(HttpClient client, string token, Guid flowId, DeliveryKey key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/delivery/records/{flowId:D}/{key.Value:D}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET record {key} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("record").Clone();
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
