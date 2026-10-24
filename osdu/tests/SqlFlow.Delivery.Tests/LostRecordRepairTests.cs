using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A record OSDU lost, as the storage route writes it (osdu/docs/reference/flow/inventory-removals.md, Repairing missing ids): one
/// OSDU no longer held when it was last looked for is brought back from its soft delete under its id before it is written,
/// so the keys the write keeps of it are read from it; a purged one is written again under its id without being read; one
/// OSDU serves already is written as any other; credentials that are no owner of it fail it and write nothing. Against a
/// fake storage service, on the wire (osdu/specs/core/INTEGRATION.md section 5.4, D3; storage source,
/// RecordServiceImpl.patchRecord).
/// </summary>
[Trait("Category", "Integration")]
public sealed class StorageRestoreFirstTests
{
    private const string RecordId = "dev:work-product-component--WellLog:abc";

    private static (OsduHttpClient Client, HttpRuntime Runtime) Client(FakeHttpHandler handler)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost/osdu", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (client, runtime);
    }

    private static DeliveryWork Work(bool restoreFirst) => new()
    {
        Key = DeliveryKey.Derive("restore-first", ["abc"]),
        TargetId = RecordId,
        Document = new JsonObject
        {
            ["id"] = RecordId,
            ["kind"] = "osdu:wks:work-product-component--WellLog:1.4.0",
            ["data"] = new JsonObject { ["Name"] = "GR" },
        },
        DeliverMetadata = true,
        DeliverPayload = false,
        ExistingVersion = 1,
        RestoreFirst = restoreFirst,
    };

    private static readonly ProtocolOptions Options = new() { PreserveDataKeys = ["Datasets"] };

    private static FakeHttpHandler Storage(HttpStatusCode patch, string? patchBody = null) => new FakeHttpHandler()
        .On(HttpMethod.Patch, ":abc", patch, patchBody)
        .On(HttpMethod.Get, ":abc", HttpStatusCode.OK, $$$"""{"id":"{{{RecordId}}}","version":1,"data":{"Name":"GR","Datasets":["dev:dataset--File.Generic:f1:"]}}""")
        .On(HttpMethod.Put, "/records", HttpStatusCode.Created, $$"""{"recordCount":1,"recordIdVersions":["{{RecordId}}:2"]}""");

    [Fact]
    public async Task A_record_osdu_lost_is_brought_back_from_its_soft_delete_before_it_is_written_and_keeps_its_preserved_keys()
    {
        var handler = Storage(HttpStatusCode.OK, $$"""{"id":"{{RecordId}}"}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = Assert.Single(await new OsduRecordProtocol(client, Options).DeliverBatchAsync([Work(restoreFirst: true)]));

            Assert.Null(outcome.Failure);
            Assert.Equal(["PATCH", "GET", "PUT"], handler.Calls.Select(c => c.Method.Method));
            var patch = handler.Calls[0];
            Assert.Equal(("application/merge-patch+json", """{"deleted":false}"""), (patch.ContentType, patch.Body));
            Assert.EndsWith("/api/storage/v2/records/" + RecordId, patch.Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.Contains("dev:dataset--File.Generic:f1:", handler.Calls[2].Body, StringComparison.Ordinal);
            Assert.Equal(2L, outcome.TargetVersion);
            var restore = Assert.Single(outcome.Steps, s => s.Name == OsduRecordProtocol.RestoreStep);
            Assert.Equal(("restored", 200), (restore.Returned["outcome"], restore.Status));
            Assert.Contains("brought back under its id", outcome.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_purged_record_is_written_again_under_its_id_without_being_read()
    {
        var handler = Storage(HttpStatusCode.NotFound, """{"code":404,"reason":"Record not found for patching"}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = Assert.Single(await new OsduRecordProtocol(client, Options).DeliverBatchAsync([Work(restoreFirst: true)]));

            Assert.Null(outcome.Failure);
            Assert.Equal(["PATCH", "PUT"], handler.Calls.Select(c => c.Method.Method));
            Assert.DoesNotContain("Datasets", handler.Calls[1].Body, StringComparison.Ordinal);
            Assert.Equal("not held", Assert.Single(outcome.Steps, s => s.Name == OsduRecordProtocol.RestoreStep).Returned["outcome"]);
            Assert.Contains("(purged): it was written again under its id", outcome.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_record_osdu_serves_already_is_written_as_any_other()
    {
        var handler = Storage(HttpStatusCode.BadRequest, """{"code":400,"reason":"Record State already updated","message":"Record State already updated"}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = Assert.Single(await new OsduRecordProtocol(client, Options).DeliverBatchAsync([Work(restoreFirst: true)]));

            Assert.Null(outcome.Failure);
            Assert.Equal(["PATCH", "GET", "PUT"], handler.Calls.Select(c => c.Method.Method));
            Assert.Equal("active", Assert.Single(outcome.Steps, s => s.Name == OsduRecordProtocol.RestoreStep).Returned["outcome"]);
            Assert.Null(outcome.Detail);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "no owner of the record")]
    [InlineData(HttpStatusCode.BadRequest, "OSDU refused to bring")]
    public async Task A_refused_restore_fails_the_record_and_writes_nothing(HttpStatusCode status, string says)
    {
        var handler = Storage(status, """{"code":0,"reason":"refused","message":"The user is not authorized to perform this action"}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = Assert.Single(await new OsduRecordProtocol(client, Options).DeliverBatchAsync([Work(restoreFirst: true)]));

            Assert.Contains(says, Assert.IsType<OsduStatusException>(outcome.Failure).Message, StringComparison.Ordinal);
            Assert.Equal(["PATCH"], handler.Calls.Select(c => c.Method.Method));
        }
    }

    [Fact]
    public async Task A_record_osdu_held_when_last_looked_for_is_written_without_a_restore()
    {
        var handler = Storage(HttpStatusCode.OK);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = Assert.Single(await new OsduRecordProtocol(client, Options).DeliverBatchAsync([Work(restoreFirst: false)]));

            Assert.Null(outcome.Failure);
            Assert.Equal(["GET", "PUT"], handler.Calls.Select(c => c.Method.Method));
            Assert.DoesNotContain(outcome.Steps, s => s.Name == OsduRecordProtocol.RestoreStep);
        }
    }
}

/// <summary>
/// Records OSDU lost, delivered again through their flow's ordinary deliver run (osdu/docs/reference/flow/inventory-removals.md,
/// Repairing missing ids): marked as lost, a record is sent again under its id with the route told to bring it back first,
/// and the mark goes with the write that lands; given a new OSDU id, it is written under that id from then on, whatever its
/// mapping makes from its key, with both ids in its history and its search entries naming the new one. Only a record the
/// ledger holds as delivered is reached. Through the executor's deliver path, over the module's database on SQL Server and
/// a fake protocol.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class LostRecordRepairTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A file a run left open on a slow machine; the temp folder is the operating system's to clear.
        }
    }

    /// <summary>A runtime over the sample estate whose first deliver run has sent every record.</summary>
    private async Task<(FlowRuntime Runtime, OsduLedger Ledger, FakeProtocol Protocol)> DeliveredAsync()
    {
        var tables = await SampleEstate.BuildAsync(_root, Now.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, sources: tables) with { Protocols = new FakeProtocolFactory(protocol) };
        var runtime = await FlowRuntime.CreateAsync(engine, Samples.LocalFlow(_root), SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        runtime.Actor = "user:operator";
        await RunAsync(runtime);
        Assert.Equal(SampleEstate.Logs().Count, protocol.Deliveries.Count);
        protocol.Deliveries.Clear();
        return (runtime, ledger, protocol);
    }

    private Task RunAsync(FlowRuntime runtime)
    {
        _clock.Advance(TimeSpan.FromMinutes(1));
        runtime.RunId = Guid.CreateVersion7();
        return DeliveryExecutor.ExecuteInterfaceAsync(runtime, DeliveryOperations.Deliver, DeliveryRunPayload.None, null, Samples.Logger<DeliveryExecutor>(), CancellationToken.None);
    }

    [Fact]
    public async Task A_record_marked_lost_is_sent_again_under_its_id_with_its_route_told_to_bring_it_back_first()
    {
        var (runtime, ledger, protocol) = await DeliveredAsync();
        using (runtime)
        {
            var lost = SampleEstate.Key(0);
            var before = (await ledger.GetRecordAsync(runtime.Flow.Id, lost))!;

            var marked = await runtime.RedeliverAsync([lost, DeliveryKey.Derive("never", ["delivered"])], new RedeliverSelection(RedeliverScope.All, []) { Lost = true });

            // Only the record the ledger holds as delivered is marked, as a verify marks one it finds missing.
            Assert.Equal(1, marked);
            var asked = (await ledger.GetRecordAsync(runtime.Flow.Id, lost))!;
            Assert.Equal((RecordStatus.Delivered, VerifyOutcome.Missing, before.ClaimedTargetId), (asked.Status, asked.LastVerifyOutcome!.Value, asked.ClaimedTargetId));
            Assert.NotNull(asked.PlanRequestedUtc);
            Assert.Contains("OSDU no longer holds the record", asked.LastError, StringComparison.Ordinal);
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "redeliver" }));
            Assert.Contains("\"lost\":true", activity.ParametersJson, StringComparison.Ordinal);

            // The next ordinary deliver run sends it again, under its own id, telling the route to bring it back first.
            await RunAsync(runtime);
            var sent = Assert.Single(protocol.Deliveries);
            Assert.Equal((lost, before.ClaimedTargetId, true), (sent.Key, sent.TargetId, sent.RestoreFirst));
            var delivered = (await ledger.GetRecordAsync(runtime.Flow.Id, lost))!;
            Assert.Equal((RecordStatus.Delivered, (VerifyOutcome?)null), (delivered.Status, delivered.LastVerifyOutcome));

            // The mark went with the write that landed: a later redelivery is an ordinary one.
            protocol.Deliveries.Clear();
            await runtime.RedeliverAsync([lost], RedeliverScope.All);
            await RunAsync(runtime);
            Assert.False(Assert.Single(protocol.Deliveries).RestoreFirst);
        }
    }

    [Fact]
    public async Task A_record_given_a_new_id_is_written_under_it_from_then_on_with_both_ids_in_its_history()
    {
        var (runtime, ledger, protocol) = await DeliveredAsync();
        using (runtime)
        {
            var key = SampleEstate.Key(0);
            var lost = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.ClaimedTargetId!;

            var given = Assert.Single(await runtime.ReassignTargetIdsAsync([key, DeliveryKey.Derive("never", ["delivered"])], "inventory WellLogs found it missing in dev"));

            var assigned = TargetId.Reassigned(lost, key)!;
            Assert.Equal((key, lost, assigned), (given.Key, given.Lost, given.Assigned));
            Assert.NotEqual(lost, assigned);
            Assert.StartsWith(lost[..lost.LastIndexOf(':')] + ":", assigned, StringComparison.Ordinal);
            var record = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!;
            Assert.Equal((assigned, assigned, (long?)null), (record.ClaimedTargetId, record.TargetId, record.TargetVersion));
            Assert.NotNull(record.TargetIdAssignedUtc);
            Assert.NotNull(record.PlanRequestedUtc);
            var attempt = (await ledger.ListAttemptsAsync(runtime.Flow.Id, key, 10))[0];
            Assert.Equal((AttemptOutcome.Skipped, AttemptPhases.ReassignId, "user:operator"), (attempt.Outcome, attempt.Phase, attempt.Worker));
            Assert.Contains(lost, attempt.ResultJson, StringComparison.Ordinal);
            Assert.Contains(assigned, attempt.ResultJson, StringComparison.Ordinal);
            Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, DeliveryKey = key.Value, Kind = "reassign-id" }));

            // The record is found by its new id, and no longer by the one OSDU lost.
            await using (var db = _db.CreateDbContext())
            {
                var tokens = await db.DeliveryRecordIdentities.AsNoTracking().Where(i => i.FlowId == runtime.Flow.Id && i.DeliveryKey == key.Value).Select(i => i.Token).ToListAsync();
                Assert.Contains(assigned.ToUpperInvariant(), tokens);
                Assert.DoesNotContain(lost.ToUpperInvariant(), tokens);
            }

            // The next ordinary deliver run writes it under the new id, its document naming it, and holds nothing.
            await RunAsync(runtime);
            var sent = Assert.Single(protocol.Deliveries);
            Assert.Equal((assigned, assigned, false), (sent.TargetId, sent.Document["id"]!.GetValue<string>(), sent.RestoreFirst));
            var delivered = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!;
            Assert.Equal((RecordStatus.Delivered, assigned), (delivered.Status, delivered.ClaimedTargetId));
            Assert.NotNull(delivered.TargetVersion);

            // Its render under the new id is what OSDU holds now: the run after it sends nothing.
            protocol.Deliveries.Clear();
            await RunAsync(runtime);
            Assert.Empty(protocol.Deliveries);

            // Given a new id again, it gets another one: the id it is given is made from the one it replaces.
            var again = Assert.Single(await runtime.ReassignTargetIdsAsync([key], "inventory WellLogs found it missing again"));
            Assert.Equal(assigned, again.Lost);
            Assert.NotEqual(assigned, again.Assigned);
        }
    }

    [Fact]
    public async Task A_record_another_record_already_claims_the_new_id_of_is_left_as_it_is()
    {
        var (runtime, ledger, _) = await DeliveredAsync();
        using (runtime)
        {
            var key = SampleEstate.Key(0);
            var other = SampleEstate.Key(1);
            var lost = (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.ClaimedTargetId!;
            await using (var db = _db.CreateDbContext())
            {
                await db.DeliveryRecords.Where(r => r.FlowId == runtime.Flow.Id && r.DeliveryKey == other.Value)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.ClaimedTargetId, TargetId.Reassigned(lost, key)));
            }

            Assert.Empty(await runtime.ReassignTargetIdsAsync([key], "inventory WellLogs found it missing in dev"));
            Assert.Equal(lost, (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.ClaimedTargetId);
        }
    }
}
