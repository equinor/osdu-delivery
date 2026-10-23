using System.Data.SqlTypes;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Data;
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
/// The three removals against the storage service, verified as endpoint and method rather than as "a delete":
/// they are different calls with different promises and the protocol must not blur them.
/// </summary>
[Trait("Category", "Integration")]
public class RemovalProtocolTests
{
    private const string RecordId = "dev:work-product-component--WellLog:abc";

    private static (OsduHttpClient Client, HttpRuntime Runtime) Client(FakeHttpHandler handler)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 2, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost/osdu", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (client, runtime);
    }

    [Fact]
    public async Task Each_scope_calls_its_own_storage_endpoint()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, ":abc:delete", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, ":abc/versions", HttpStatusCode.NoContent, null)
            .On(HttpMethod.Delete, ":abc", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());

            var reversible = await protocol.DeleteAsync(RecordId, RemovalScope.Record);
            Assert.True(reversible.Deleted);
            Assert.False(reversible.AlreadyGone);
            Assert.Contains("reversible", reversible.Detail, StringComparison.Ordinal);
            Assert.Equal(HttpMethod.Post, handler.Calls[0].Method);
            Assert.EndsWith(":delete", handler.Calls[0].Uri.AbsolutePath, StringComparison.Ordinal);

            var history = await protocol.DeleteAsync(RecordId, RemovalScope.History);
            Assert.True(history.Deleted);
            Assert.Contains("latest version is still live", history.Detail, StringComparison.Ordinal);
            Assert.Equal(HttpMethod.Delete, handler.Calls[1].Method);
            Assert.EndsWith("/versions", handler.Calls[1].Uri.AbsolutePath, StringComparison.Ordinal);

            var everything = await protocol.DeleteAsync(RecordId, RemovalScope.Everything);
            Assert.True(everything.Deleted);
            Assert.Contains("every version", everything.Detail, StringComparison.Ordinal);
            Assert.Equal(HttpMethod.Delete, handler.Calls[2].Method);
            Assert.EndsWith(":abc", handler.Calls[2].Uri.AbsolutePath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_record_OSDU_no_longer_holds_is_already_gone_not_a_failure()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, ":delete", HttpStatusCode.NotFound, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var outcome = await new OsduRecordProtocol(client, new ProtocolOptions()).DeleteAsync(RecordId, RemovalScope.Record);
            Assert.False(outcome.Deleted);
            Assert.True(outcome.AlreadyGone);
        }
    }

    [Fact]
    public async Task The_reversible_scope_removes_many_records_in_one_bulk_request()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/records/delete", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = await protocol.DeleteBatchAsync(Removals("a", "b", "c"), RemovalScope.Record);

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.True(r.Succeeded));
            Assert.Single(handler.Calls);
            var sent = JsonNode.Parse(handler.Calls[0].Body!)!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Assert.Equal(["dev:x:a", "dev:x:b", "dev:x:c"], sent);
        }
    }

    /// <summary>
    /// The storage service's own 207 (GlobalExceptionMapper.handleDeleteRecordsException, osdu/specs/core/INTEGRATION.md, D2): a
    /// JSON array naming each id it kept and why. The contract's shape, the exception's {key, value} pairs, and either one sent
    /// as a JSON string are read the same way.
    /// </summary>
    public static TheoryData<string> PartialAnswers => new()
    {
        """[{"notDeletedRecordId":"dev:x:b","message":"Record with id 'dev:x:b' not found"},{"notDeletedRecordId":"dev:x:d","message":"The user is not authorized to perform delete record with id dev:x:d"}]""",
        """{"notDeletedRecords":[{"key":"dev:x:b","value":"Record with id 'dev:x:b' not found"},{"key":"dev:x:d","value":"The user is not authorized to perform delete record with id dev:x:d"}]}""",
        """ "[{\"notDeletedRecordId\":\"dev:x:b\",\"message\":\"not found\"},{\"notDeletedRecordId\":\"dev:x:d\",\"message\":\"not authorized\"}]" """,
    };

    [Theory]
    [MemberData(nameof(PartialAnswers))]
    public async Task A_partial_bulk_removal_asks_again_only_for_the_ids_it_names_and_the_rest_are_removed(string answer)
    {
        // The service deleted every id the 207 does not name. Asked again, those would answer 404 like an id that was gone
        // before, so only the named ids are asked about: one it could not find is already gone, one it refused fails.
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/records/delete", HttpStatusCode.MultiStatus, answer)
            .On(HttpMethod.Post, ":b:delete", HttpStatusCode.NotFound, """{"code":404,"reason":"Record not found"}""")
            .On(HttpMethod.Post, ":d:delete", HttpStatusCode.Forbidden, """{"code":403,"reason":"Access denied","message":"The user is not authorized to perform this action"}""")
            .On(HttpMethod.Post, ":delete", HttpStatusCode.NotFound, """{"code":404,"reason":"Record not found"}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = (await protocol.DeleteBatchAsync(Removals("a", "b", "c", "d"), RemovalScope.Record)).ToDictionary(r => r.Removal.TargetId);

            Assert.Equal(4, results.Count);
            Assert.Equal((true, false), (results["dev:x:a"].Outcome!.Deleted, results["dev:x:a"].Outcome!.AlreadyGone));
            Assert.Equal((true, false), (results["dev:x:c"].Outcome!.Deleted, results["dev:x:c"].Outcome!.AlreadyGone));
            Assert.Equal((false, true), (results["dev:x:b"].Outcome!.Deleted, results["dev:x:b"].Outcome!.AlreadyGone));
            Assert.Equal(403, Assert.IsType<OsduStatusException>(results["dev:x:d"].Failure).StatusCode);
            Assert.Equal(3, handler.Calls.Count);
            Assert.EndsWith("/records/delete", handler.Calls[0].Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.EndsWith(":b:delete", handler.Calls[1].Uri.AbsolutePath, StringComparison.Ordinal);
            Assert.EndsWith(":d:delete", handler.Calls[2].Uri.AbsolutePath, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("""{"code":207,"reason":"Multi-Status"}""")]
    [InlineData("""[]""")]
    [InlineData("""[{"notDeletedRecordId":"dev:x:z","message":"Record with id 'dev:x:z' not found"}]""")]
    [InlineData("""[{"id":"dev:x:b"}]""")]
    [InlineData("not json")]
    public async Task A_partial_bulk_removal_whose_answer_names_no_id_sent_leaves_every_id_failed_with_that_answer(string answer)
    {
        // Asking every id again would find the ones the bulk call deleted gone and report them so, so nothing is guessed: each id
        // fails with what the service answered, and the next removal settles it.
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/records/delete", HttpStatusCode.MultiStatus, answer)
            .On(HttpMethod.Post, ":delete", HttpStatusCode.NotFound, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = await protocol.DeleteBatchAsync(Removals("a", "b", "c"), RemovalScope.Record);

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.False(r.Succeeded));
            Assert.All(results, r => Assert.Contains("answered 207", r.Failure!.Message, StringComparison.Ordinal));
            Assert.All(results, r => Assert.Contains("whether this one was deleted is unknown", r.Failure!.Message, StringComparison.Ordinal));
            Assert.Single(handler.Calls);
        }
    }

    [Fact]
    public async Task A_bulk_route_the_service_does_not_serve_is_replaced_by_one_request_per_record()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/records/delete", HttpStatusCode.NotFound, null)
            .On(HttpMethod.Post, ":delete", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = await protocol.DeleteBatchAsync(Removals("a", "b"), RemovalScope.Record);

            Assert.All(results, r => Assert.True(r.Outcome!.Deleted));
            Assert.Equal(3, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task A_bulk_removal_the_service_refused_outright_does_not_repeat_itself_once_per_record()
    {
        // 401 is not a verdict on any record in the chunk. Asking again one id at a time would produce the same
        // failure three times over; every record carries the one failure that actually happened.
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/records/delete", HttpStatusCode.Unauthorized, """{"code":401,"reason":"Unauthorized"}""");
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = await protocol.DeleteBatchAsync(Removals("a", "b", "c"), RemovalScope.Record);

            Assert.Equal(3, results.Count);
            Assert.All(results, r => Assert.False(r.Succeeded));
            Assert.All(results, r => Assert.Contains("401", r.Failure!.Message, StringComparison.Ordinal));

            // Twice, not once per record: the client retries a 401 once under a freshly resolved token, and stops.
            Assert.Equal(2, handler.Calls.Count);
            Assert.All(handler.Calls, c => Assert.EndsWith("/records/delete", c.Uri.AbsolutePath, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_bulk_removal_the_service_rejected_as_malformed_is_asked_again_record_by_record()
    {
        // 400 is about the ids in the list, so the chunk is split to find which of them the service objects to.
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, "/records/delete", HttpStatusCode.BadRequest, """{"code":400,"reason":"Invalid id format"}""")
            .On(HttpMethod.Post, ":b:delete", HttpStatusCode.BadRequest, """{"code":400,"reason":"Invalid id format"}""")
            .On(HttpMethod.Post, ":delete", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = await protocol.DeleteBatchAsync(Removals("a", "b", "c"), RemovalScope.Record);

            Assert.Equal(3, results.Count);
            Assert.True(results.Single(r => r.Removal.TargetId == "dev:x:a").Succeeded);
            Assert.False(results.Single(r => r.Removal.TargetId == "dev:x:b").Succeeded);
            Assert.True(results.Single(r => r.Removal.TargetId == "dev:x:c").Succeeded);
            Assert.Equal(4, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task The_purges_have_no_bulk_endpoint_and_go_one_at_a_time()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Delete, "/versions", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduRecordProtocol(client, new ProtocolOptions());
            var results = await protocol.DeleteBatchAsync(Removals("a", "b"), RemovalScope.History);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.True(r.Succeeded));
            Assert.Equal(2, handler.Calls.Count);
            Assert.All(handler.Calls, c => Assert.EndsWith("/versions", c.Uri.AbsolutePath, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_endpoints_a_flow_would_call_follow_its_protocol()
    {
        var storage = RemovalEndpoints.Of(Samples.Targeting(new FlowTarget { Endpoint = "http://x", Protocol = DeliveryProtocol.Storage }), null);
        Assert.Equal("/api/storage/v2/records/{id}:delete", storage.Record);
        Assert.Equal("/api/storage/v2/records/{id}/versions", storage.History);
        Assert.Equal("/api/storage/v2/records/{id}", storage.Everything);

        // The wellbore DDMS owns the record but not its versions, so only the history scope leaves the DDMS. Its
        // paths carry no /api/<service>/ prefix, so a storage path under a DDMS endpoint would not resolve: with
        // nowhere to send it the scope reports itself unconfigured rather than naming a URL that would 404.
        var ddmsFlow = Samples.Targeting(new FlowTarget { Endpoint = "http://x", Protocol = DeliveryProtocol.Ddms });
        var ddms = RemovalEndpoints.Of(ddmsFlow, "osdu:wks:work-product-component--WellLog:1.4.0");
        Assert.Equal("/ddms/v3/welllogs/{id}", ddms.Record);
        Assert.Equal(RemovalEndpoints.HistoryNotConfigured, ddms.History);
        Assert.Equal("/ddms/v3/welllogs/{id}?purge=true", ddms.Everything);

        // The collection follows the kind: a trajectory's records are in wellboretrajectories.
        Assert.Equal("/ddms/v3/wellboretrajectories/{id}", RemovalEndpoints.Of(ddmsFlow, "osdu:wks:work-product-component--WellboreTrajectory:1.3.0").Record);

        // A record-only collection's DELETE is logical only, so its purge is the storage service's, which a DDMS
        // endpoint does not reach either.
        var wellbores = RemovalEndpoints.Of(ddmsFlow, "osdu:wks:master-data--Wellbore:1.3.0");
        Assert.Equal("/ddms/v3/wellbores/{id}", wellbores.Record);
        Assert.Equal(RemovalEndpoints.PurgeNotConfigured, wellbores.Everything);

        // Until the kind is known, the collection is not.
        Assert.Equal(RemovalEndpoints.CollectionNotKnown, RemovalEndpoints.Of(ddmsFlow, null).Record);

        // A kind no DDMS the flow reaches serves says so instead of naming a path.
        Assert.StartsWith("(not routable: ", RemovalEndpoints.Of(ddmsFlow, "osdu:wks:work-product-component--SeismicTraceData:1.0.0").Record, StringComparison.Ordinal);

        // A ddms flow that says where storage lives is taken at its word.
        var configured = RemovalEndpoints.Of(
            Samples.Targeting(new FlowTarget
            {
                Endpoint = "http://x",
                Protocol = DeliveryProtocol.Ddms,
                ProtocolOptions = new ProtocolOptions
                {
                    PurgeVersionsPath = "https://osdu.example.com/api/storage/v2/records/{id}/versions",
                    PurgePath = "https://osdu.example.com/api/storage/v2/records/{id}",
                },
            }),
            "osdu:wks:master-data--Wellbore:1.3.0");
        Assert.Equal("https://osdu.example.com/api/storage/v2/records/{id}/versions", configured.History);
        Assert.Equal("https://osdu.example.com/api/storage/v2/records/{id}", configured.Everything);
    }

    [Fact]
    public async Task A_well_log_history_purge_refuses_rather_than_deleting_somewhere_nobody_chose()
    {
        var handler = new FakeHttpHandler();
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var protocol = new OsduDdmsProtocol(client, new ProtocolOptions(), Samples.Logger<OsduDdmsProtocol>());
            var ex = await Assert.ThrowsAsync<RecordHeldException>(() => protocol.DeleteAsync(RecordId, RemovalScope.History));

            Assert.Contains("purgeVersionsPath", ex.Message, StringComparison.Ordinal);
            Assert.Empty(handler.Calls);
        }
    }

    [Fact]
    public async Task A_well_log_history_purge_goes_to_the_storage_service_the_flow_names()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Delete, "/versions", HttpStatusCode.NoContent, null);
        var (client, runtime) = Client(handler);
        using (runtime)
        {
            var options = new ProtocolOptions { PurgeVersionsPath = "http://localhost/storage/api/storage/v2/records/{id}/versions" };
            var protocol = new OsduDdmsProtocol(client, options, Samples.Logger<OsduDdmsProtocol>());
            var outcome = await protocol.DeleteAsync(RecordId, RemovalScope.History);

            Assert.True(outcome.Deleted);

            // The absolute path is honoured as written, not joined under the flow's DDMS endpoint.
            var call = Assert.Single(handler.Calls);
            Assert.Equal("http://localhost/storage/api/storage/v2/records/" + RecordId + "/versions", call.Uri.AbsoluteUri);
        }
    }

    private static List<RecordRemoval> Removals(params string[] keys)
        => keys.Select(k => new RecordRemoval(DeliveryKey.Derive("test", [k]), "dev:x:" + k, null)).ToList();
}

/// <summary>What each scope does to the ledger, which is not the same question as what it does to OSDU.</summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public class RemovalLedgerTests : IAsyncLifetime, IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("test-flow");

    private OsduLedger Ledger => _db.Ledger(_clock);

    /// <summary>Registers the ledgers the tests write to directly, as a run registers its own before it writes a row of it.</summary>
    public Task InitializeAsync() => Ledger.RegisterAsync(_flow);

    public Task DisposeAsync() => Task.CompletedTask;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task Deleting_a_record_from_the_ledger_deletes_only_the_record_and_moves_no_watermark()
    {
        var removed = await DeliveredAsync("a", Guid.NewGuid());
        var delivered = await DeliveredAsync("b", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [removed], RemovalScope.Record, "gui:tahir", Now);
        foreach (var scope in new[] { "", "scope=COMPOSITE" })
        {
            await Ledger.SetWatermarkAsync(new SourceWatermark(_flow, scope, Now, Guid.NewGuid(), Now, "rules"));
        }

        // The record goes and nothing else of the ledger moves: no scope is read whole for it, and nothing holds or blocks
        // it. Its row comes back by key, through the line kept of it (RemovedRecordLedgerTests).
        Assert.Single(await Ledger.PurgeRecordsAsync(_flow, [removed], "gui:tahir", null, Now));
        Assert.Null(await Ledger.GetRecordAsync(_flow, removed));
        Assert.Equal(RecordStatus.Delivered, (await Ledger.GetRecordAsync(_flow, delivered))!.Status);
        Assert.NotNull(await Ledger.GetWatermarkAsync(_flow, ""));
        Assert.NotNull(await Ledger.GetWatermarkAsync(_flow, "scope=COMPOSITE"));
    }

    [Fact]
    public async Task Deleting_the_ledger_takes_every_record_whatever_its_state_and_everything_its_runs_kept()
    {
        var submission = await SubmissionAsync(_flow);
        var delivered = await DeliveredAsync("a", submission);
        var removed = await DeliveredAsync("b", submission);
        await Ledger.MarkRemovedAsync(_flow, [removed], RemovalScope.Record, "gui:tahir", Now);
        var pending = Pending(_flow, "c", submission).DeliveryKey;
        Assert.Equal(1, (await Ledger.UpsertPendingAsync(_flow, [Pending(_flow, "c", submission)])).Staged);
        await Ledger.SetWatermarkAsync(new SourceWatermark(_flow, "", Now, submission, Now, "rules"));
        await using (var db = _db.CreateDbContext())
        {
            var partition = await db.DeliveryLedgers.Where(l => l.FlowId == _flow).Select(l => l.PartitionId).SingleAsync();
            db.DeliveryWorkBatches.Add(new DeliveryWorkBatch
            {
                PartitionId = partition, SubmissionId = submission, Index = 0, FlowId = _flow, Location = "batch-0.jsonl", RecordCount = 1, CreatedUtc = Now,
            });
            await db.SaveChangesAsync();
        }

        // Another ledger of the partition, which the deletion leaves as it is.
        var other = FlowId.Of("another-flow");
        await Ledger.RegisterAsync(other);
        var kept = Pending(other, "z", Guid.NewGuid()).DeliveryKey;
        Assert.Equal(1, (await Ledger.UpsertPendingAsync(other, [Pending(other, "z", Guid.NewGuid())])).Staged);
        await Ledger.SetWatermarkAsync(new SourceWatermark(other, "", Now, Guid.NewGuid(), Now, "rules"));
        var activity = await Ledger.StartActivityAsync(new ActivityRecord
        {
            FlowId = _flow, FlowName = "test-flow", Kind = DeliveryOperations.DeleteLedger, Actor = "gui:tahir", StartedUtc = Now,
        });

        // The leases the deliveries took ran out long ago, and are settled with the rest.
        _clock.Advance(TimeSpan.FromMinutes(10));
        var deleted = await Ledger.DeleteLedgerAsync(_flow, "gui:tahir", activity.ActivityId, Now);

        Assert.Equal((3, 1, 1, 1), (deleted.Records, deleted.Submissions, deleted.WorkBatches, deleted.Watermarks));
        foreach (var key in new[] { delivered, removed, pending })
        {
            Assert.Null(await Ledger.GetRecordAsync(_flow, key));
            Assert.Equal(activity.ActivityId, (await Ledger.FindPurgedAsync(_flow, key))!.ActivityId);
        }

        Assert.Null(await Ledger.GetWatermarkAsync(_flow, ""));
        Assert.Empty(await Ledger.ListSubmissionsAsync(_flow, 10));
        await using (var db = _db.CreateDbContext())
        {
            Assert.False(await db.DeliveryWorkBatches.AnyAsync(b => b.FlowId == _flow));
            Assert.False(await db.DeliveryLeases.AnyAsync(l => l.FlowId == _flow));
            Assert.False(await db.DeliveryAttempts.AnyAsync(a => a.FlowId == _flow));
            Assert.False(await db.DeliveryRecordEvents.AnyAsync(e => e.FlowId == _flow));
        }

        // The audit trail and the ledger's entry in the directory stay, and so does every row of the other ledger.
        Assert.NotNull(await Ledger.GetActivityAsync(activity.ActivityId));
        Assert.NotNull(await Ledger.GetLedgerAsync(_flow));
        Assert.NotNull(await Ledger.GetRecordAsync(other, kept));
        Assert.NotNull(await Ledger.GetWatermarkAsync(other, ""));

        // Asked again, an empty ledger has nothing left to delete.
        Assert.Equal(new LedgerDeletion(0, 0, 0, 0, 0, 0, 0), await Ledger.DeleteLedgerAsync(_flow, "gui:tahir", null, Now));
    }

    [Fact]
    public async Task What_a_ledger_changed_in_OSDU_lately_is_counted_by_change_and_named_by_one_of_its_ids()
    {
        await DeliveredAsync("old", Guid.NewGuid());
        var removed = await DeliveredAsync("removed", Guid.NewGuid());
        var purged = await DeliveredAsync("purged", Guid.NewGuid());
        var quiet = FlowId.Of("quiet-flow");
        await Ledger.RegisterAsync(quiet);
        Assert.Equal(1, (await Ledger.UpsertPendingAsync(quiet, [Pending(quiet, "q", Guid.NewGuid())])).Staged);
        _clock.Advance(TimeSpan.FromMinutes(30));
        var since = Now;

        // After the moment: one record written, one taken out of OSDU and kept, one taken out and deleted from the ledger.
        _clock.Advance(TimeSpan.FromSeconds(10));
        await Ledger.MarkRemovedAsync(_flow, [removed, purged], RemovalScope.Record, "gui:tahir", Now);
        Assert.Single(await Ledger.PurgeRecordsAsync(_flow, [purged], "gui:tahir", null, Now));
        _clock.Advance(TimeSpan.FromSeconds(10));
        await DeliveredAsync("written", Guid.NewGuid());
        var latest = Now;

        var change = Assert.Single(await Ledger.RecentOsduChangesAsync(TestLedgers.Partition, since));

        Assert.Equal((_flow, 1L, 1L, 1L), (change.FlowId, change.Written, change.Removed, change.Deleted));
        Assert.Equal(latest, change.LatestUtc);
        Assert.StartsWith("dev:x:", change.SampleTargetId, StringComparison.Ordinal);

        // Nothing after the latest change, and nothing in a partition the directory does not hold.
        Assert.Empty(await Ledger.RecentOsduChangesAsync(TestLedgers.Partition, Now));
        Assert.Empty(await Ledger.RecentOsduChangesAsync("nowhere", since));
    }

    [Fact]
    public async Task A_worker_holding_a_lease_keeps_the_record_it_holds_and_everything_the_runs_kept()
    {
        var delivered = await DeliveredAsync("a", Guid.NewGuid());
        _clock.Advance(TimeSpan.FromMinutes(10));
        var submission = Guid.NewGuid();
        var busy = Pending(_flow, "busy", submission).DeliveryKey;
        Assert.Equal(1, (await Ledger.UpsertPendingAsync(_flow, [Pending(_flow, "busy", submission)])).Staged);
        Assert.Single((await Ledger.ClaimAsync(_flow, submission, "elsewhere", 10, TimeSpan.FromMinutes(5), Now)).Records);
        await Ledger.SetWatermarkAsync(new SourceWatermark(_flow, "", Now, submission, Now, "rules"));

        var refused = await Assert.ThrowsAsync<DeliveryException>(() => Ledger.DeleteLedgerAsync(_flow, "gui:tahir", null, Now));

        Assert.Contains("1 record(s) were deleted, but a worker still holds a lease on its records", refused.Message, StringComparison.Ordinal);
        Assert.Null(await Ledger.GetRecordAsync(_flow, delivered));
        Assert.NotNull(await Ledger.GetRecordAsync(_flow, busy));
        Assert.NotNull(await Ledger.GetWatermarkAsync(_flow, ""));
    }

    [Fact]
    public async Task A_removed_record_a_lease_holds_is_not_deleted_from_the_ledger()
    {
        var key = await DeliveredAsync("a", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.Record, "gui:tahir", Now);
        await using (var db = _db.CreateDbContext())
        {
            var record = await db.DeliveryRecords.SingleAsync(r => r.FlowId == _flow && r.DeliveryKey == key.Value);
            record.LeaseOwner = "worker-elsewhere";
            await db.SaveChangesAsync();
        }

        Assert.Empty(await Ledger.PurgeRecordsAsync(_flow, [key], "gui:tahir", null, Now));
        Assert.NotNull(await Ledger.GetRecordAsync(_flow, key));
        Assert.Null(await Ledger.FindPurgedAsync(_flow, key));
    }

    [Fact]
    public async Task Deleting_many_removed_records_from_the_ledger_takes_every_one_a_slice_at_a_time()
    {
        var sliced = new OsduLedger(_db.CreateDbContext, _clock) { WriteSlice = 2 };
        var keys = new List<DeliveryKey>();
        for (var i = 0; i < 5; i++)
        {
            keys.Add(await DeliveredAsync("s" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Guid.NewGuid()));
        }

        await sliced.MarkRemovedAsync(_flow, keys, RemovalScope.Everything, "gui:tahir", Now);

        var purged = await sliced.PurgeRecordsAsync(_flow, keys, "gui:tahir", null, Now);

        Assert.Equal(keys.OrderBy(k => k.Value), purged.OrderBy(k => k.Value));
        foreach (var key in keys)
        {
            Assert.Null(await sliced.GetRecordAsync(_flow, key));
            Assert.NotNull(await sliced.FindPurgedAsync(_flow, key));
        }
    }

    [Fact]
    public async Task A_record_a_removal_blocked_before_removals_stopped_blocking_is_still_released()
    {
        var key = await DeliveredAsync("a", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.Record, "gui:tahir", Now);
        await using (var db = _db.CreateDbContext())
        {
            // As a removal left a record before: deleted, blocked at the source version it was removed at.
            var record = await db.DeliveryRecords.SingleAsync(r => r.FlowId == _flow && r.DeliveryKey == key.Value);
            record.Blocked = true;
            record.PendingSourceFingerprint = "fp";
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await Ledger.ReleaseAsync(_flow, [key], Now));
        var released = await Ledger.GetRecordAsync(_flow, key);
        Assert.False(released!.Blocked);
        Assert.NotNull(released.PlanRequestedUtc);
    }

    [Fact]
    public async Task Removing_the_record_marks_it_deleted_and_forgets_the_hashes_without_blocking_it()
    {
        var key = await DeliveredAsync("a", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.Record, "gui:tahir", Now);

        var record = await Ledger.GetRecordAsync(_flow, key);
        Assert.Equal(RecordStatus.Deleted, record!.Status);
        Assert.False(record.Blocked);
        Assert.Equal(Now, record.PlanRequestedUtc);
        Assert.Null(record.PendingSourceFingerprint);
        Assert.Null(record.MetadataHash);
        Assert.Null(record.TargetVersion);

        var attempt = Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10), a => a.Phase == "delete");
        Assert.Equal(AttemptOutcome.Deleted, attempt.Outcome);
        Assert.Null(attempt.Error);
        Assert.Contains("gui:tahir", attempt.ResultJson!, StringComparison.Ordinal);
        Assert.Contains("reversible", attempt.ResultJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_removal_attempt_reads_like_any_other_attempt_and_names_its_correlation_id()
    {
        // A removed record's page went blank in the GUI: this attempt's result was the record's target state, not the
        // steps-shaped result every other attempt carries.
        var key = await DeliveredAsync("a", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.Record, "gui:tahir", Now, "corr-1");

        var attempt = Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10), a => a.Phase == "delete");
        using var result = System.Text.Json.JsonDocument.Parse(attempt.ResultJson!);
        Assert.Equal("corr-1", result.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, result.RootElement.GetProperty("steps").ValueKind);
        Assert.Contains("reversible", result.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Purging_everything_is_recorded_as_the_permanent_removal_it_is()
    {
        var key = await DeliveredAsync("a", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.Everything, "gui:tahir", Now);

        Assert.Equal(RecordStatus.Deleted, (await Ledger.GetRecordAsync(_flow, key))!.Status);
        var attempt = Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10), a => a.Phase == "delete");
        Assert.Null(attempt.Error);
        Assert.Contains("every version", attempt.ResultJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deleting_from_the_ledger_takes_only_removed_records_and_keeps_one_line_of_each()
    {
        var removed = await DeliveredAsync("a", Guid.NewGuid());
        var delivered = await DeliveredAsync("b", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [removed], RemovalScope.Record, "gui:tahir", Now);
        var activity = await Ledger.StartActivityAsync(new ActivityRecord { FlowId = _flow, FlowName = "test-flow", Kind = "delete", Actor = "gui:tahir", StartedUtc = Now });
        await using (var before = _db.CreateDbContext())
        {
            Assert.True(await before.DeliveryRecordIdentities.AnyAsync(i => i.DeliveryKey == removed.Value));
        }

        // A record OSDU still holds is never deleted from the ledger, whatever is asked.
        var purged = await Ledger.PurgeRecordsAsync(_flow, [removed, delivered, removed], "gui:tahir", activity.ActivityId, Now);

        Assert.Equal(removed, Assert.Single(purged));
        Assert.Null(await Ledger.GetRecordAsync(_flow, removed));
        Assert.Empty(await Ledger.ListAttemptsAsync(_flow, removed, 10));
        Assert.Equal(RecordStatus.Delivered, (await Ledger.GetRecordAsync(_flow, delivered))!.Status);
        Assert.NotEmpty(await Ledger.ListAttemptsAsync(_flow, delivered, 10));
        await using (var after = _db.CreateDbContext())
        {
            Assert.False(await after.DeliveryRecordIdentities.AnyAsync(i => i.DeliveryKey == removed.Value));
            Assert.True(await after.DeliveryActivityRecords.AnyAsync(l => l.DeliveryKey == removed.Value && l.ActivityId == activity.ActivityId));
            Assert.Single(await after.DeliveryActivities.Where(a => a.ActivityId == activity.ActivityId).ToListAsync());
        }

        // The line the ledger keeps says what it was, which OSDU id and version it last had, and who deleted it when.
        var line = await Ledger.FindPurgedAsync(_flow, removed);
        Assert.Equal(("a", "dev:x:a", (long?)42, 2), (line!.SourceKey, line.TargetId, line.LastVersion, line.Attempts));
        Assert.Equal(("gui:tahir", Now, (long?)activity.ActivityId), (line.PurgedBy, line.PurgedUtc, line.ActivityId));
        Assert.Null(await Ledger.FindPurgedAsync(_flow, delivered));

        // Asked again, there is nothing left to delete.
        Assert.Empty(await Ledger.PurgeRecordsAsync(_flow, [removed], "gui:tahir", null, Now));
    }

    [Fact]
    public async Task Purging_the_history_leaves_the_record_delivered_because_OSDU_still_holds_it()
    {
        var key = await DeliveredAsync("a", Guid.NewGuid());
        await Ledger.MarkRemovedAsync(_flow, [key], RemovalScope.History, "gui:tahir", Now);

        var record = await Ledger.GetRecordAsync(_flow, key);
        Assert.Equal(RecordStatus.Delivered, record!.Status);
        Assert.False(record.Blocked);
        Assert.Equal(42, record.TargetVersion);
        Assert.Equal("mh", record.MetadataHash);

        var attempt = Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10), a => a.Phase == "purge-history");
        Assert.Equal(AttemptOutcome.HistoryPurged, attempt.Outcome);
        Assert.Equal(42, attempt.TargetVersion);
    }

    [Fact]
    public async Task A_listing_can_be_narrowed_to_the_records_one_run_touched()
    {
        var submission = Guid.NewGuid();
        var run = Guid.NewGuid();
        var touched = await DeliveredAsync("a", submission, run);
        var untouched = await DeliveredAsync("b", submission, Guid.NewGuid());

        var keys = await Ledger.ListKeysAsync(_flow, new RecordQuery { RunId = run }, 100);
        Assert.Equal([touched], keys);
        Assert.Equal(new BoundedCount(1, Exact: true), await Ledger.CountAsync(_flow, new RecordQuery { RunId = run }, 100));

        // The same filter with a status the run's record is not in matches nothing, so the two compose.
        Assert.Empty(await Ledger.ListKeysAsync(_flow, new RecordQuery { RunId = run, Status = RecordStatus.Failed }, 100));
        Assert.Equal(new BoundedCount(2, Exact: true), await Ledger.CountAsync(_flow, new RecordQuery(), 100));
        Assert.DoesNotContain(untouched, keys);
    }

    [Fact]
    public async Task Selected_keys_come_back_in_key_order_so_a_removal_never_reshuffles_its_own_pages()
    {
        var submission = Guid.NewGuid();
        var keys = new List<DeliveryKey>();
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            keys.Add(await DeliveredAsync(name, submission));
        }

        // Key order is the order the database keeps the keys in, which is the order the removal's pages walk: SQL Server
        // orders a uniqueidentifier by its last group of bytes first, exactly as SqlGuid compares, not as Guid does.
        var listed = await Ledger.ListKeysAsync(_flow, new RecordQuery(), 100);
        Assert.Equal(keys.OrderBy(k => new SqlGuid(k.Value)).ToList(), listed);
        Assert.Equal(2, (await Ledger.ListKeysAsync(_flow, new RecordQuery(), 2)).Count);
    }

    [Fact]
    public async Task A_record_with_no_OSDU_id_at_all_is_skipped_because_there_is_nothing_to_ask_for()
    {
        await Ledger.UpsertPendingAsync(_flow, [new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", ["no-id"]),
            FlowId = _flow,
            SourceKey = "no-id",
            MappingName = "Thing",
            PendingRenderContext = "{}",
            PendingSourceFingerprint = "fp",
            PendingMetadataHash = "mh",
            PendingMetadata = true,
        }]);

        var record = await Ledger.GetRecordAsync(_flow, DeliveryKey.Derive("test", ["no-id"]));
        Assert.Null(record!.TargetId);
        Assert.Equal(new BoundedCount(1, Exact: true), await Ledger.CountAsync(_flow, new RecordQuery { EverDelivered = false }, 100));
    }

    /// <summary>A record staged for <paramref name="flow"/> and never claimed: what a plan leaves for its drain.</summary>
    private static RecordState Pending(Guid flow, string sourceKey, Guid submission) => new()
    {
        DeliveryKey = DeliveryKey.Derive("test", [sourceKey]),
        FlowId = flow,
        SourceKey = sourceKey,
        MappingName = "Thing",
        TargetId = "dev:x:" + sourceKey,
        LastSubmissionId = submission,
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingSourceFingerprint = "fp",
        PendingMetadataHash = "mh",
        PendingMetadata = true,
    };

    /// <summary>A submission of <paramref name="flow"/> in the ledger, as a plan registers one.</summary>
    private async Task<Guid> SubmissionAsync(Guid flow)
    {
        var (submission, _) = await Ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = Guid.CreateVersion7(),
            FlowId = flow,
            FlowName = "test-flow",
            MappingReference = "Thing@1.0.0",
            RenderContext = "{}",
            Status = SubmissionStatus.Completed,
            Kind = SubmissionKinds.Incremental,
            SourceConnection = "${env:OSDU_DATA_DB}",
            SourceObject = "OsduData.arc.Thing",
            ReceivedUtc = Now,
        });
        return submission.SubmissionId;
    }

    /// <summary>A record staged, claimed and delivered, so a removal has something real to act on.</summary>
    private async Task<DeliveryKey> DeliveredAsync(string sourceKey, Guid submission, Guid? runId = null)
    {
        var key = DeliveryKey.Derive("test", [sourceKey]);
        await Ledger.UpsertPendingAsync(_flow, [new RecordState
        {
            DeliveryKey = key,
            FlowId = _flow,
            SourceKey = sourceKey,
            MappingName = "Thing",
            TargetId = "dev:x:" + sourceKey,
            LastSubmissionId = submission,
            PendingDocumentRef = "0:0:10",
            PendingRenderContext = "{}",
            PendingSourceFingerprint = "fp",
            PendingMetadataHash = "mh",
            PendingMetadata = true,
        }]);
        var claimed = (await Ledger.ClaimAsync(_flow, submission, "w", 10, TimeSpan.FromMinutes(5), Now)).Records;
        var record = claimed.Single(r => r.DeliveryKey == key);
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 42,
            TargetId = record.TargetId,
            Attempt = new AttemptRecord
            {
                DeliveryKey = key,
                SubmissionId = submission,
                RunId = runId,
                Worker = "w",
                StartedUtc = Now,
                CompletedUtc = Now,
                Outcome = AttemptOutcome.Delivered,
                Phase = "metadata",
                TargetVersion = 42,
            },
        });
        return key;
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>The runtime's removal: the ledger settled per record, and a selection that is a filter resolved here.</summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public class RemovalRuntimeTests : IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly string _root = Samples.NewTempDirectory();

    private Guid _submission;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task Removing_a_selection_settles_every_record_and_reports_what_each_one_did()
    {
        var (runtime, protocol, ledger) = await DeliveredEstateAsync();
        using (runtime)
        {
            var keys = await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100);
            Assert.Equal(5, keys.Count);
            protocol.Gone.Add((await ledger.GetRecordAsync(runtime.Flow.Id, keys[0]))!.TargetId!);
            runtime.Actor = "gui:tahir";

            var summary = await runtime.RemoveAsync(RemovalSelection.Of(keys), RemovalChoice.Everything);

            Assert.Equal(5, summary.Selected);
            Assert.Equal(4, summary.Removed);
            Assert.Equal(1, summary.AlreadyGone);
            Assert.Equal(0, summary.Failed);
            Assert.Equal(0, summary.Skipped);
            Assert.All(protocol.Deletes, d => Assert.Equal(RemovalScope.Everything, d.Scope));
            foreach (var key in keys)
            {
                Assert.Equal(RecordStatus.Deleted, (await ledger.GetRecordAsync(runtime.Flow.Id, key))!.Status);
            }

            var activity = (await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "delete" })).Single();
            Assert.Equal("completed", activity.Outcome);
            Assert.Equal("gui:tahir", activity.Actor);
            Assert.Contains("5 record(s) selected", activity.Summary!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Records_already_removed_are_deleted_from_the_ledger_without_asking_OSDU_and_the_rest_are_left()
    {
        var (runtime, protocol, ledger) = await DeliveredEstateAsync();
        using (runtime)
        {
            var keys = await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100);
            runtime.Actor = "gui:tahir";
            Assert.Equal(2, (await runtime.RemoveAsync(RemovalSelection.Of([keys[0], keys[1]]), RemovalChoice.Record)).Removed);
            protocol.Deletes.Clear();

            // Named: the removed one goes, the one OSDU still holds is left and counted as such.
            var named = await runtime.PurgeFromLedgerAsync([keys[0], keys[2], keys[0]]);

            Assert.Equal((2, 1, 1), (named.Selected, named.Purged, named.Left));
            Assert.Contains("1 of 2 record(s) deleted from the ledger; 1 left as they were", named.Describe(), StringComparison.Ordinal);
            Assert.Empty(protocol.Deletes);
            Assert.Null(await ledger.GetRecordAsync(runtime.Flow.Id, keys[0]));
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, keys[2]))!.Status);
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "purge" }));
            Assert.Equal(("completed", "gui:tahir"), (activity.Outcome, activity.Actor));
            Assert.Equal(activity.ActivityId, (await ledger.FindPurgedAsync(runtime.Flow.Id, keys[0]))!.ActivityId);

            // Every record removed: the rest of them go, and nothing OSDU holds.
            var every = await runtime.PurgeFromLedgerAsync(null);

            Assert.Equal((1, 1), (every.Selected, every.Purged));
            Assert.Null(await ledger.GetRecordAsync(runtime.Flow.Id, keys[1]));
            Assert.Equal(3, (await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100)).Count);
            Assert.Empty(protocol.Deletes);

            // Nothing left to delete: asked again, it says so and changes nothing.
            Assert.Equal((0, 0), ((await runtime.PurgeFromLedgerAsync(null)).Selected, (await runtime.PurgeFromLedgerAsync(null)).Purged));
        }
    }

    [Fact]
    public async Task A_record_OSDU_had_already_lost_is_deleted_from_the_ledger_with_the_removal()
    {
        var (runtime, protocol, ledger) = await DeliveredEstateAsync();
        using (runtime)
        {
            var keys = await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100);
            protocol.Gone.Add((await ledger.GetRecordAsync(runtime.Flow.Id, keys[0]))!.TargetId!);
            runtime.Actor = "gui:tahir";

            var summary = await runtime.RemoveAsync(RemovalSelection.Of([keys[0]]), RemovalChoice.Everything, purgeLedger: true);

            Assert.Equal((1, 1), (summary.AlreadyGone, summary.Purged));
            Assert.Null(await ledger.GetRecordAsync(runtime.Flow.Id, keys[0]));
            Assert.NotNull(await ledger.FindPurgedAsync(runtime.Flow.Id, keys[0]));
        }
    }

    [Fact]
    public async Task A_route_that_cannot_write_an_earlier_version_back_passes_every_record_over_before_asking_OSDU()
    {
        var ledger = _db.Ledger(_clock);
        var flow = Samples.LocalFlow(_root);
        flow = flow with { Target = flow.Target with { Protocol = DeliveryProtocol.Dspdm } };
        await ledger.RegisterAsync(flow);
        var protocol = new FakeProtocol();
        var record = new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("dspdm", ["row-1"]),
            FlowId = flow.Id,
            SourceKey = "row-1",
            MappingName = "Row",
            Status = RecordStatus.Delivered,
            TargetId = "dev:x:row-1",
            ClaimedTargetId = "dev:x:row-1",
            TargetVersion = 7,
        };

        var steps = await new Engine.Reversals.PreviousVersionRestore(flow, ledger, protocol, _clock, "user:test", null)
            .RestoreAsync([record], "corr-1", CancellationToken.None);

        var step = Assert.Single(steps);
        Assert.Equal("skipped", step.Result.Outcome);
        Assert.Contains("DSPDM keeps no earlier versions of a row", step.Result.Detail, StringComparison.Ordinal);
        Assert.Null(step.Restored);
        Assert.Empty(protocol.Deletes);
    }

    [Fact]
    public async Task A_removal_can_also_delete_what_OSDU_confirmed_removed_from_the_ledger()
    {
        var (runtime, protocol, ledger) = await DeliveredEstateAsync();
        using (runtime)
        {
            var keys = await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100);
            var failing = (await ledger.GetRecordAsync(runtime.Flow.Id, keys[1]))!.TargetId!;
            protocol.Refuse.Add(failing);
            runtime.Actor = "gui:tahir";

            var summary = await runtime.RemoveAsync(RemovalSelection.Of([keys[0], keys[1]]), RemovalChoice.Record, purgeLedger: true);

            // What OSDU confirmed removed is gone from the ledger, with one line kept; what it refused stays as it was.
            Assert.Equal((1, 1, 1), (summary.Removed, summary.Failed, summary.Purged));
            Assert.Contains("1 deleted from the ledger", summary.Describe(), StringComparison.Ordinal);
            Assert.EndsWith("; deleted from the ledger", Assert.Single(summary.Records, r => r.Outcome == "removed").Detail, StringComparison.Ordinal);
            Assert.Null(await ledger.GetRecordAsync(runtime.Flow.Id, keys[0]));
            Assert.Equal("gui:tahir", (await ledger.FindPurgedAsync(runtime.Flow.Id, keys[0]))!.PurgedBy);
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, keys[1]))!.Status);
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = "delete" }));
            Assert.Contains("\"purgeLedger\":true", activity.ParametersJson!, StringComparison.Ordinal);
            Assert.Equal(activity.ActivityId, (await ledger.FindPurgedAsync(runtime.Flow.Id, keys[0]))!.ActivityId);

            // Only a removal that takes the record out of OSDU deletes it from the ledger.
            await Assert.ThrowsAsync<DeliveryException>(() => runtime.RemoveAsync(RemovalSelection.Of([keys[2]]), RemovalChoice.History, purgeLedger: true));
            await Assert.ThrowsAsync<DeliveryException>(() => runtime.RemoveAsync(RemovalSelection.Of([keys[2]]), RemovalChoice.Previous, purgeLedger: true));
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, keys[2]))!.Status);
        }
    }

    [Fact]
    public async Task Deleting_the_ledger_removes_every_record_OSDU_holds_then_deletes_the_whole_ledger()
    {
        var (runtime, protocol, ledger) = await DeliveredEstateAsync();
        using (runtime)
        {
            var keys = await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100);
            var removedBefore = (await ledger.GetRecordAsync(runtime.Flow.Id, keys[0]))!.TargetId!;
            runtime.Actor = "gui:tahir";
            Assert.Equal(1, (await runtime.RemoveAsync(RemovalSelection.Of([keys[0]]), RemovalChoice.Record)).Removed);
            protocol.Deletes.Clear();
            await ledger.SetWatermarkAsync(new SourceWatermark(runtime.Flow.Id, "", Now, _submission, Now, "rules"));
            var partition = (await ledger.GetLedgerAsync(runtime.Flow.Id))!.Partition!;
            _clock.Advance(TimeSpan.FromMinutes(10));

            var summary = await runtime.DeleteLedgerAsync(partition.ToUpperInvariant());

            // The four records OSDU still held are removed from it, reversibly; the one removed before is not asked about again.
            Assert.Equal((4, 0, 1, 0), (summary.Removed, summary.AlreadyGone, summary.AlreadyRemoved, summary.NeverInOsdu));
            Assert.Equal(4, protocol.Deletes.Count);
            Assert.All(protocol.Deletes, d => Assert.Equal(RemovalScope.Record, d.Scope));
            Assert.DoesNotContain(protocol.Deletes, d => d.TargetId == removedBefore);

            // Then the ledger goes whole: its records, its submission and its watermark, so the next run reads every row.
            // Two watermarks: the one its run wrote for the scope it planned, and the one set here.
            Assert.Equal((5, 2), (summary.Deleted.Records, summary.Deleted.Watermarks));
            Assert.True(summary.Deleted.Submissions >= 1);
            Assert.Empty(await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100));
            Assert.Null(await ledger.GetWatermarkAsync(runtime.Flow.Id, ""));
            Assert.Empty(await ledger.ListSubmissionsAsync(runtime.Flow.Id, 10));
            var activity = Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = DeliveryOperations.DeleteLedger }));
            Assert.Equal(("completed", "gui:tahir"), (activity.Outcome, activity.Actor));
            Assert.Contains("4 record(s) removed from OSDU (reversible)", activity.Summary!, StringComparison.Ordinal);
            foreach (var key in keys)
            {
                Assert.Equal(activity.ActivityId, (await ledger.FindPurgedAsync(runtime.Flow.Id, key))!.ActivityId);
            }
        }
    }

    [Fact]
    public async Task A_record_OSDU_will_not_remove_keeps_the_whole_ledger_until_it_does()
    {
        var (runtime, protocol, ledger) = await DeliveredEstateAsync();
        using (runtime)
        {
            var keys = await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100);
            var refused = (await ledger.GetRecordAsync(runtime.Flow.Id, keys[2]))!.TargetId!;
            protocol.Refuse.Add(refused);
            runtime.Actor = "gui:tahir";
            var partition = (await ledger.GetLedgerAsync(runtime.Flow.Id))!.Partition!;
            _clock.Advance(TimeSpan.FromMinutes(10));

            var failure = await Assert.ThrowsAsync<DeliveryException>(() => runtime.DeleteLedgerAsync(partition));

            // OSDU never holds a record the ledger forgot: every record stays, the four OSDU removed marked so.
            Assert.Contains("1 record(s) of", failure.Message, StringComparison.Ordinal);
            Assert.Contains("could not be removed from OSDU, so the ledger was kept as it was", failure.Message, StringComparison.Ordinal);
            Assert.Contains(refused, failure.Message, StringComparison.Ordinal);
            Assert.Equal(5, (await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100)).Count);
            Assert.Equal(4, (await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Deleted }, 100)).Count);
            Assert.Equal(RecordStatus.Delivered, (await ledger.GetRecordAsync(runtime.Flow.Id, keys[2]))!.Status);
            Assert.Equal("failed", Assert.Single(await ledger.ListActivitiesAsync(new ActivityQuery { FlowId = runtime.Flow.Id, Kind = DeliveryOperations.DeleteLedger })).Outcome);

            // Once OSDU removes it, the ledger goes, and what was removed before is not asked about again.
            protocol.Refuse.Clear();
            protocol.Deletes.Clear();
            var summary = await runtime.DeleteLedgerAsync(partition);

            Assert.Equal((1, 4, 5), (summary.Removed, summary.AlreadyRemoved, summary.Deleted.Records));
            Assert.Equal(refused, Assert.Single(protocol.Deletes).TargetId);
            Assert.Empty(await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100));
        }
    }

    [Fact]
    public async Task Deleting_the_ledger_names_the_partition_it_is_kept_in_or_touches_nothing()
    {
        var (runtime, protocol, ledger) = await DeliveredEstateAsync();
        using (runtime)
        {
            var partition = (await ledger.GetLedgerAsync(runtime.Flow.Id))!.Partition!;
            runtime.Actor = "gui:tahir";

            var wrong = await Assert.ThrowsAsync<DeliveryException>(() => runtime.DeleteLedgerAsync("elsewhere"));

            Assert.Contains($"which is kept in partition '{partition}', so nothing was removed or deleted", wrong.Message, StringComparison.Ordinal);
            Assert.Empty(protocol.Deletes);
            Assert.Equal(5, (await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Delivered }, 100)).Count);
        }
    }

    [Fact]
    public async Task A_filter_selection_is_resolved_when_the_removal_runs_not_when_it_was_asked_for()
    {
        var (runtime, protocol, ledger) = await DeliveredEstateAsync();
        using (runtime)
        {
            var summary = await runtime.RemoveAsync(
                RemovalSelection.Of(new RecordQuery { Status = RecordStatus.Delivered }), RemovalChoice.Record);

            Assert.Equal(5, summary.Selected);
            Assert.Equal(5, summary.Removed);
            Assert.Equal(5, protocol.Deletes.Count);
            Assert.Empty(await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery { Status = RecordStatus.Delivered }, 100));
        }
    }

    [Fact]
    public async Task A_record_that_was_planned_but_never_delivered_comes_back_already_gone()
    {
        // The plan gives a record its OSDU id, so an undelivered record has an id OSDU has never seen. The removal
        // is still made (the ledger cannot know what the target holds) and OSDU answers 404, which is not a failure.
        var (runtime, protocol, ledger) = await DeliveredEstateAsync(deliver: false);
        using (runtime)
        {
            var keys = await ledger.ListKeysAsync(runtime.Flow.Id, new RecordQuery(), 100);
            foreach (var key in keys)
            {
                protocol.Gone.Add((await ledger.GetRecordAsync(runtime.Flow.Id, key))!.TargetId!);
            }

            var summary = await runtime.RemoveAsync(RemovalSelection.Of(keys), RemovalChoice.Record);

            Assert.Equal(keys.Count, summary.Selected);
            Assert.Equal(keys.Count, summary.AlreadyGone);
            Assert.Equal(0, summary.Removed);
            Assert.Equal(0, summary.Failed);
            Assert.All(summary.Records, r => Assert.Equal("already-gone", r.Outcome));
            Assert.Equal(keys.Count, (await ledger.CountAsync(runtime.Flow.Id, new RecordQuery { EverDelivered = false }, 100)).Count);
        }
    }

    /// <summary>The sample estate delivered through the fake protocol, with the runtime wired to that same fake.</summary>
    private async Task<(FlowRuntime Runtime, FakeProtocol Protocol, OsduLedger Ledger)> DeliveredEstateAsync(bool deliver = true)
    {
        var tables = await SampleEstate.BuildAsync(_root, _clock.GetUtcNow().UtcDateTime.AddMinutes(-5), time: _clock);
        var ledger = _db.Ledger(_clock);
        var protocol = new FakeProtocol();
        var engine = Samples.Engine(ledger, _clock, new FixedProtocolFactory(protocol), sources: tables);
        var flow = Samples.LocalFlow(_root);
        var runtime = await FlowRuntime.CreateAsync(engine, flow, SampleEstate.Values);
        await ledger.RegisterAsync(runtime.Flow);
        var intake = await runtime.Intake.IntakeAsync(runtime.Flow, runtime.Mapping, runtime.Parameters, runtime.Request, force: false);
        Assert.False(intake.NothingToDo);
        _submission = intake.Submission.SubmissionId;
        if (deliver)
        {
            var worker = new Engine.Worker.DeliveryWorker(
                ledger, runtime.Context.Payloads, runtime.Context.Stores, protocol, runtime.Flow, _clock,
                CompositeDeliveryListener.Empty, Samples.Logger<Engine.Worker.DeliveryWorker>(), "test-worker") { MaxWait = null };
            await worker.DrainAsync(_submission);
            protocol.Deletes.Clear();
        }

        await runtime.Intake.CompleteAsync(_submission, runtime.Flow.Id);
        return (runtime, protocol, ledger);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Hands the runtime the same fake protocol the test holds, so a removal can be observed.</summary>
internal sealed class FixedProtocolFactory : IProtocolFactory
{
    private readonly IDeliveryProtocol _protocol;

    public FixedProtocolFactory(IDeliveryProtocol protocol) => _protocol = protocol;

    public Task<IDeliveryProtocol> CreateAsync(FlowDefinition flow, HttpRuntime http, ILoggerFactory loggers, CancellationToken ct = default)
        => Task.FromResult(_protocol);
}

/// <summary>
/// The contract the API and the remove run share: the scope on the wire, the flag that deletes from the ledger too, and the
/// listing filter a removal carries when the operator asked for "every record this matches". A drift here would aim a
/// removal at the wrong records or at the wrong depth, so it is pinned rather than assumed.
/// </summary>
[Trait("Category", "Integration")]
public class RemovalContractTests
{
    [Theory]
    [InlineData("record", RemovalChoice.Record)]
    [InlineData("previous", RemovalChoice.Previous)]
    [InlineData("history", RemovalChoice.History)]
    [InlineData("everything", RemovalChoice.Everything)]
    public void Scopes_round_trip_through_their_wire_names(string wire, RemovalChoice scope)
    {
        Assert.Equal(wire, Engine.Operations.RemovalScopes.Wire(scope));
        Assert.True(Engine.Operations.RemovalScopes.TryParse(wire, out var parsed));
        Assert.Equal(scope, parsed);

        // The remove run reads the scope its payload carries by the same names, and writes it back the same way.
        var payload = DeliveryRunPayload.Parse($$"""{"scope":"{{wire}}","recordKeys":["11111111-1111-1111-1111-111111111111"]}""");
        Assert.Equal(scope, payload.Scope);
        Assert.Contains($"\"scope\":\"{wire}\"", payload.ToJson(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RemovalChoice.Record, RemovalScope.Record)]
    [InlineData(RemovalChoice.History, RemovalScope.History)]
    [InlineData(RemovalChoice.Everything, RemovalScope.Everything)]
    public void Each_delete_choice_reaches_the_protocol_as_its_own_scope(RemovalChoice choice, RemovalScope scope)
        => Assert.Equal(scope, choice.Scope());

    [Fact]
    public void Restoring_the_previous_version_never_reaches_a_protocol_as_a_delete()
    {
        // A protocol reads any scope but the reversible one as a purge in places, so the one choice that deletes nothing must
        // never be turned into a scope by accident.
        Assert.Throws<ArgumentOutOfRangeException>(() => RemovalChoice.Previous.Scope());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void The_extra_step_s_flag_reads_only_true_or_false(string? wire, bool purge)
    {
        var json = wire is null ? """{"scope":"record"}""" : $$"""{"scope":"record","purgeLedger":{{wire}}}""";
        Assert.Equal(purge, DeliveryRunPayload.Parse(json).PurgeLedger);
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    public void A_mistyped_extra_step_is_refused_rather_than_read_as_either(string wire)
    {
        // A flag read as true would delete history nobody asked to delete; read as false, it would quietly keep what was asked gone.
        Assert.Contains(
            "payload purgeLedger must be true or false",
            Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse($$"""{"scope":"record","purgeLedger":{{wire}}}""")).Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("purge")]
    [InlineData("Record")]
    public void An_unknown_scope_is_refused_rather_than_defaulted(string? wire)
    {
        // Defaulting would pick a removal depth the operator never asked for, which is the one mistake this
        // vocabulary exists to prevent.
        Assert.False(Engine.Operations.RemovalScopes.TryParse(wire, out _));

        // A remove run refuses it as well: written, as a scope it does not know; left out, as one it has to have.
        var json = wire is null
            ? """{"recordKeys":["11111111-1111-1111-1111-111111111111"]}"""
            : $$"""{"scope":"{{wire}}","recordKeys":["11111111-1111-1111-1111-111111111111"]}""";
        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse(json).Validate(DeliveryOperations.Remove));
    }

    [Fact]
    public void A_listing_filter_survives_the_trip_to_the_node()
    {
        var submission = Guid.NewGuid();
        var deliveredBy = Guid.NewGuid();
        var run = Guid.NewGuid();
        var query = new RecordQuery
        {
            Status = RecordStatus.Delivered,
            Search = "STAT",
            Mode = SearchMode.Contains,
            SubmissionId = submission,
            DeliveredBySubmissionId = deliveredBy,
            RunId = run,
            Drifted = true,
            Problem = ProblemSignature.Of("Unit 'ft' is not in the reference cache"),
        };

        var back = Engine.Operations.RemovalFilter.FromJson(Engine.Operations.RemovalFilter.ToJson(query));

        Assert.Equal(RecordStatus.Delivered, back.Status);
        Assert.Equal("STAT", back.Search);
        Assert.Equal(SearchMode.Contains, back.Mode);
        Assert.Equal(submission, back.SubmissionId);
        Assert.Equal(deliveredBy, back.DeliveredBySubmissionId);
        Assert.Equal(run, back.RunId);
        Assert.True(back.Drifted);

        // The issue the listing was narrowed to travels too: without it, the run would remove every record the rest of the
        // filter matches, more than the operator was shown and counted.
        Assert.Equal(query.Problem, back.Problem);
        Assert.Equal(query, back);
    }

    [Fact]
    public void An_empty_filter_stays_empty_and_matches_everything_the_flow_holds()
    {
        var back = Engine.Operations.RemovalFilter.FromJson(Engine.Operations.RemovalFilter.ToJson(new RecordQuery()));
        Assert.Null(back.Status);
        Assert.Null(back.Search);
        Assert.Null(back.SubmissionId);
        Assert.Null(back.DeliveredBySubmissionId);
        Assert.Null(back.RunId);
        Assert.Null(back.Problem);
        Assert.False(back.Drifted);
        Assert.Equal(SearchMode.Prefix, back.Mode);
    }

    [Theory]
    [InlineData("""{"status":"delivered","owner":"alice"}""", "could not be mapped")]
    [InlineData("""{"status":"lost"}""", "payload filter status 'lost' is not a record status")]
    [InlineData("""{"issue":"not-an-issue"}""", "payload filter issue 'not-an-issue' is not an issue")]
    public void A_filter_naming_what_the_listing_does_not_know_is_refused_rather_than_widened(string filter, string why)
    {
        // A part the run did not read would leave the filter wider than the listing the operator counted.
        var refused = Assert.Throws<SqlFlowException>(() => Engine.Operations.RemovalFilter.FromJson(filter));
        Assert.Contains(why, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_deleting_the_ledger_names_its_partition_and_nothing_it_does_not_take()
    {
        var payload = DeliveryRunPayload.Parse("""{"confirm":" dev ","interfaces":["units","quantities"]}""");
        payload.Validate(DeliveryOperations.DeleteLedger);
        Assert.Equal("dev", payload.Confirm);
        Assert.Equal("""{"interfaces":["units","quantities"],"confirm":"dev"}""", payload.ToJson());
        Assert.False(payload.CarriesOnlyConfiguration);

        Assert.Contains(
            "payload confirm is required by the delete-ledger operation",
            Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.None.Validate(DeliveryOperations.DeleteLedger)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "payload confirm does not apply to the deliver operation",
            Assert.Throws<SqlFlowException>(() => new DeliveryRunPayload { Confirm = "dev" }.Validate(DeliveryOperations.Deliver)).Message,
            StringComparison.Ordinal);
        foreach (var (json, why) in new[]
        {
            ("""{"confirm":"dev","force":true}""", "passes no gate"),
            ("""{"confirm":"dev","recordKeys":["11111111-1111-1111-1111-111111111111"]}""", "remove records with a removal"),
            ("""{"confirm":"dev","redeliver":"all"}""", "sends nothing"),
        })
        {
            Assert.Contains(why, Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse(json).Validate(DeliveryOperations.DeleteLedger)).Message, StringComparison.Ordinal);
        }

        Assert.Contains("payload confirm must be a non-empty string", Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{"confirm":"  "}""")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_selection_must_name_records_and_cannot_exceed_the_ceiling()
    {
        Assert.Throws<DeliveryException>(() => RemovalSelection.Of(Array.Empty<DeliveryKey>()));
        var tooMany = Enumerable.Range(0, RemovalLimits.MaxSelection + 1)
            .Select(i => DeliveryKey.Derive("test", [i.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
            .ToList();
        Assert.Throws<DeliveryException>(() => RemovalSelection.Of(tooMany));
    }
}
