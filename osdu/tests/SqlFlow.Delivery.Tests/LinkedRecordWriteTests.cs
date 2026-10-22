using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// One OSDU record two writers deliver: two instances of the delivery, each with a ledger of its own, linked to the same
/// record (osdu/docs/reference/concepts/record-lifecycle.md, Adopting a record OSDU already holds). Through the storage route,
/// a version the other writer moved without changing what this one wrote is not drift, a write OSDU skips as unchanged lands
/// at the version OSDU holds rather than the one the ledger knew, and a skipped record OSDU holds soft-deleted is brought
/// back rather than recorded as delivered while OSDU serves nothing.
/// </summary>
public sealed class LinkedRecordWriteTests : IDisposable
{
    private const string Partition = "dev";
    private const string Id = "dev:reference-data--ExternalCatalogNamespace:WELLDB";
    private const string Kind = "osdu:wks:reference-data--ExternalCatalogNamespace:1.0.0";

    private static readonly SecretResolver Secrets = new([new EnvSecretProvider()]);

    private readonly FakeOsduPlatform _platform = new();
    private readonly HttpRuntime _runtime;
    private readonly OsduHttpClient _client;

    public LinkedRecordWriteTests()
    {
        _runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            Secrets, TimeProvider.System, _platform, allowLoopback: true);
        _client = new OsduHttpClient(
            _runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = Partition });
    }

    private static JsonObject Namespace(string name = "Well database") => FakeOsduPlatform.Record(Id, Kind, new JsonObject { ["Name"] = name, ["Code"] = "WELLDB" });

    private IDeliveryProtocol Route(bool skipDuplicates = false)
        => new OsduRecordProtocol(_client, new ProtocolOptions { BatchSize = 10, SkipDuplicates = skipDuplicates });

    private static DeliveryWork Work(JsonObject document, long? existing = null, IReadOnlyDictionary<string, string>? state = null) => new()
    {
        Key = Identity.DeliveryKey.Derive("welldb", [document["id"]!.GetValue<string>()]),
        TargetId = document["id"]!.GetValue<string>(),
        Document = document,
        DeliverMetadata = true,
        DeliverPayload = false,
        ExistingVersion = existing,
        TargetState = state ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static VerifyRequest Verify(DeliveryOutcome delivered)
        => new(Id, delivered.TargetVersion, OwnedContent.StateOf(JsonMerge.FromValues(delivered.Returned)));

    [Fact]
    public async Task A_version_another_writer_moved_with_the_same_content_is_not_drift_and_one_that_changed_it_is()
    {
        // This instance writes the record; the other instance, linked to it, writes it again as it renders it, the same.
        var ours = await Route().DeliverAsync(Work(Namespace()));
        Assert.True(ours.Succeeded, ours.Failure?.Message);
        Assert.Equal(OwnedContent.Hash(Namespace(), []), ours.Returned[OwnedContent.HashValue]);
        var theirs = _platform.Put(Namespace());
        Assert.NotEqual(ours.TargetVersion, theirs);

        var same = Assert.Single(await Route().VerifyBatchAsync([Verify(ours)]));
        Assert.Equal((VerifyOutcome.Match, (long?)theirs), (same.Outcome, same.ObservedVersion));
        Assert.Contains("holds the content the flow wrote, written again by another writer", same.Detail, StringComparison.Ordinal);

        // The other instance writes another name: what this one delivered changed, which is drift.
        var renamed = _platform.Put(Namespace("Well database, renamed"));
        var changed = Assert.Single(await Route().VerifyBatchAsync([Verify(ours)]));
        Assert.Equal((VerifyOutcome.Drifted, (long?)renamed), (changed.Outcome, changed.ObservedVersion));
    }

    [Fact]
    public async Task A_write_osdu_skips_as_unchanged_lands_at_the_version_osdu_holds()
    {
        // The other instance wrote the record twice since this ledger knew it: OSDU holds the same content at a newer version.
        var known = _platform.Put(Namespace());
        _platform.Put(Namespace("Well database, interim"));
        var current = _platform.Put(Namespace());

        var skipped = await Route(skipDuplicates: true).DeliverAsync(Work(Namespace(), existing: known));
        Assert.True(skipped.Succeeded, skipped.Failure?.Message);
        Assert.Equal(current, skipped.TargetVersion);
        Assert.Equal("true", skipped.Returned["skipped"]);
        Assert.Equal("unchanged at the target", skipped.Detail);
        Assert.Equal(current, _platform.Records[Id]["version"]!.GetValue<long>());
        OsduContracts.AssertConform(_platform.Calls, null, OsduContracts.Storage);
    }

    [Fact]
    public async Task A_skipped_record_osdu_holds_soft_deleted_is_brought_back_and_one_it_cannot_bring_back_is_not_delivered()
    {
        // The other instance removed the shared record (a soft delete); this one writes it unchanged with skipdupes, which
        // storage skips, leaving it deleted. The route brings it back at the version it had.
        var version = _platform.Put(Namespace());
        _platform.Removed.Add(Id);
        var restored = await Route(skipDuplicates: true).DeliverAsync(Work(Namespace(), existing: version));
        Assert.True(restored.Succeeded, restored.Failure?.Message);
        Assert.Equal(version, restored.TargetVersion);
        Assert.DoesNotContain(Id, _platform.Removed);
        Assert.Equal([Id], _platform.Restores);
        Assert.Contains("soft-deleted and skipped the unchanged write, which leaves a deleted record deleted: it was brought back under its id", restored.Detail, StringComparison.Ordinal);
        Assert.Contains(restored.Steps, s => s.Name == OsduRecordProtocol.RestoreStep);

        // Storage refusing to bring it back (the credentials are no owner of the record) leaves it undelivered, saying why.
        _platform.Removed.Add(Id);
        var refusing = new FakeHttpHandler()
            .On(HttpMethod.Put, "/api/storage/v2/records", _ => FakeHttpHandler.Json(HttpStatusCode.Created, $$"""{"recordCount":1,"skippedRecordIds":["{{Id}}"]}"""))
            .On(HttpMethod.Post, "/api/storage/v2/query/records", _ => FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"records":[],"invalidRecords":["{{Id}}"],"retryRecords":[]}"""))
            .OnMatch(r => r.Method == HttpMethod.Patch, _ => FakeHttpHandler.Json(HttpStatusCode.Forbidden, """{"code":403,"reason":"Forbidden","message":"User is not authorized to update records."}"""));
        using var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } }, Secrets, TimeProvider.System, refusing, allowLoopback: true);
        var client = new OsduHttpClient(runtime, FakeOsduPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None }, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = Partition });
        IDeliveryProtocol route = new OsduRecordProtocol(client, new ProtocolOptions { BatchSize = 10, SkipDuplicates = true });
        var refused = Assert.Single(await route.DeliverBatchAsync([Work(Namespace(), existing: version)]));
        Assert.False(refused.Succeeded);
        var status = Assert.IsType<OsduStatusException>(refused.Failure);
        Assert.Equal(403, status.StatusCode);
        Assert.Contains("no owner of the record", status.Message, StringComparison.Ordinal);
        Assert.Contains(refused.Steps, s => s.Name == OsduRecordProtocol.RecordsStep && s.Error is not null);
    }

    public void Dispose() => _runtime.Dispose();
}
