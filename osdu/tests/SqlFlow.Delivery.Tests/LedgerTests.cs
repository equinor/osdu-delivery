using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.Delivery.Tests;

[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public class SqlLedgerTests : IAsyncLifetime, IDisposable
{
    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("test-flow");

    private OsduLedger Ledger => _db.Ledger(_clock);

    /// <summary>Registers the ledgers the tests write to directly, as a run registers its own before it writes a row of it.</summary>
    public Task InitializeAsync() => Ledger.RegisterAsync(_flow, FlowId.Of("other-flow"), FlowId.Of("test-flow-two"), FlowId.Of("test-flow-wellbores"), FlowId.Of("test-flow-copy"));

    public Task DisposeAsync() => Task.CompletedTask;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private RecordState Pending(string sourceKey, Guid submission, bool payload = true) => new()
    {
        DeliveryKey = DeliveryKey.Derive("test", [sourceKey]),
        FlowId = _flow,
        SourceKey = sourceKey,
        MappingName = "Thing",
        TargetId = "dev:x:" + sourceKey,
        LastSubmissionId = submission,
        PendingDocumentRef = "0:0:10",
        PendingRenderContext = "{}",
        PendingSourceFingerprint = "fp",
        PendingMetadataHash = "mh",
        PendingPayloadHash = payload ? "ph" : null,
        PendingPayloadLocation = payload ? "loc" : null,
        PendingMetadata = true,
        PendingPayload = payload,
    };

    private SubmissionState Submission(Guid id) => new()
    {
        SubmissionId = id,
        FlowId = _flow,
        FlowName = "test-flow",
        MappingReference = "Thing@1.0.0",
        RenderContext = "{}",
        SourceConnection = "${env:OSDU_DATA_DB}",
        SourceObject = "OsduData.arc.WellLog",
        RecordCount = 1,
    };

    [Fact]
    public async Task Submissions_are_idempotent_by_id()
    {
        var id = Guid.NewGuid();
        var (first, created) = await Ledger.RegisterSubmissionAsync(Submission(id));
        var (second, createdAgain) = await Ledger.RegisterSubmissionAsync(Submission(id) with { RecordCount = 99 });
        Assert.True(created);
        Assert.False(createdAgain);
        Assert.Equal(1, second.RecordCount);
        Assert.Equal(first.SubmissionId, second.SubmissionId);
        await Ledger.UpdateSubmissionAsync(second with { Status = SubmissionStatus.Completed, Delivered = 3 });
        var loaded = await Ledger.GetSubmissionAsync(id);
        Assert.Equal(SubmissionStatus.Completed, loaded!.Status);
        Assert.Equal(3, loaded.Delivered);
        Assert.Single(await Ledger.ListSubmissionsAsync(_flow, 10));
    }

    [Fact]
    public async Task Lookup_answers_by_delivery_key_or_by_prefix_across_flows()
    {
        var submission = Guid.NewGuid();
        var otherFlow = FlowId.Of("other-flow");
        await Ledger.UpsertPendingAsync(_flow, [
            Pending("WELL-1", submission),
            Pending("WELL-2", submission),
        ]);
        await Ledger.UpsertPendingAsync(otherFlow, [Pending("OTHER-1", submission) with { FlowId = otherFlow, Label = "Other one" }]);

        var byKey = await Ledger.LookupAsync(DeliveryKey.Derive("test", ["WELL-1"]).Value.ToString(), 10);
        Assert.Equal("WELL-1", Assert.Single(byKey).SourceKey);

        // The same row read by another flow is that flow's record too: the key finds both, and a prefix that only one of
        // them matches finds only that one, even though the two share a key.
        await Ledger.UpsertPendingAsync(otherFlow, [Pending("WELL-1", submission) with { FlowId = otherFlow, TargetId = "dev:y:WELL-1", Label = "Seen elsewhere" }]);
        var bothFlows = await Ledger.LookupAsync(DeliveryKey.Derive("test", ["WELL-1"]).Value.ToString(), 10);
        Assert.Equal(new[] { _flow, otherFlow }.Order(), bothFlows.Select(r => r.FlowId).Order());
        Assert.Equal(new BoundedCount(2, Exact: true), await Ledger.CountLookupAsync(DeliveryKey.Derive("test", ["WELL-1"]).Value.ToString(), 10));
        Assert.Equal(otherFlow, Assert.Single(await Ledger.LookupAsync("Seen", 10)).FlowId);
        Assert.Equal(_flow, Assert.Single(await Ledger.LookupAsync("dev:x:WELL-1", 10)).FlowId);

        var byTargetId = await Ledger.LookupAsync("dev:x:WELL", 10);
        Assert.Equal(2, byTargetId.Count);
        Assert.Equal(new BoundedCount(2, Exact: true), await Ledger.CountLookupAsync("dev:x:WELL", 10));
        Assert.Equal(new BoundedCount(1, Exact: false), await Ledger.CountLookupAsync("dev:x:WELL", 1));
        Assert.Single(await Ledger.LookupAsync("dev:x:WELL", 1));

        var byLabel = await Ledger.LookupAsync("Other", 10);
        Assert.Equal(otherFlow, Assert.Single(byLabel).FlowId);

        Assert.Empty(await Ledger.LookupAsync("nothing-like-this", 10));
        Assert.Empty(await Ledger.LookupAsync(Guid.NewGuid().ToString(), 10));
        await Assert.ThrowsAsync<ArgumentException>(() => Ledger.LookupAsync(" ", 10));
    }

    [Fact]
    public async Task Recent_lists_what_the_ledger_last_touched_across_flows_newest_first()
    {
        var submission = Guid.NewGuid();
        var otherFlow = FlowId.Of("other-flow");
        await Ledger.UpsertPendingAsync(_flow, [Pending("WELL-1", submission), Pending("WELL-2", submission)]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Ledger.UpsertPendingAsync(otherFlow, [Pending("OTHER-1", submission) with { FlowId = otherFlow }]);

        // Every flow's records, the last one touched first. The two staged together share an update time, so they
        // follow it in whichever order the key tie-break gives, and the listing is the three of them.
        var recent = await Ledger.ListRecentAsync(10);
        Assert.Equal(3, recent.Count);
        Assert.Equal("OTHER-1", recent[0].SourceKey);
        Assert.Equal(otherFlow, recent[0].FlowId);
        Assert.Equal(["WELL-1", "WELL-2"], recent.Skip(1).Select(r => r.SourceKey).Order().ToArray());
        Assert.Equal(new BoundedCount(3, Exact: true), await Ledger.CountRecentAsync(10));

        // How many it returns is what was asked for, and the count is a floor once it reaches its limit.
        Assert.Equal(2, (await Ledger.ListRecentAsync(2)).Count);
        Assert.Equal(new BoundedCount(2, Exact: false), await Ledger.CountRecentAsync(2));

        // A custody state narrows the same listing; a state nothing is in is empty rather than everything.
        var key = DeliveryKey.Derive("test", ["WELL-1"]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Held,
            Error = "held by the test",
            Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Held, Phase = "metadata" },
        });
        Assert.Equal("WELL-1", Assert.Single(await Ledger.ListRecentAsync(10, RecordStatus.Held)).SourceKey);
        Assert.Equal(new BoundedCount(1, Exact: true), await Ledger.CountRecentAsync(10, RecordStatus.Held));
        Assert.Empty(await Ledger.ListRecentAsync(10, RecordStatus.Deleted));
        Assert.Equal(new BoundedCount(0, Exact: true), await Ledger.CountRecentAsync(10, RecordStatus.Deleted));

        // The record just held is the one last touched, so it leads the whole listing too.
        Assert.Equal("WELL-1", (await Ledger.ListRecentAsync(10))[0].SourceKey);
    }

    [Fact]
    public async Task Flows_with_records_are_the_identities_asked_about_that_hold_one_in_any_state()
    {
        var submission = Guid.NewGuid();
        var otherFlow = FlowId.Of("other-flow");
        var emptyFlow = FlowId.Of("empty-flow");
        await Ledger.UpsertPendingAsync(_flow, [Pending("WELL-1", submission), Pending("WELL-2", submission)]);
        await Ledger.UpsertPendingAsync(otherFlow, [Pending("OTHER-1", submission) with { FlowId = otherFlow }]);

        // Only the identities asked about, and of those only the ones holding a record; asking twice is asking once.
        Assert.Equal(
            new HashSet<Guid> { _flow, otherFlow },
            await Ledger.FlowsWithRecordsAsync([_flow, emptyFlow, otherFlow, _flow]));
        Assert.Equal(new HashSet<Guid> { otherFlow }, await Ledger.FlowsWithRecordsAsync([otherFlow, emptyFlow]));
        Assert.Empty(await Ledger.FlowsWithRecordsAsync([emptyFlow]));
        Assert.Empty(await Ledger.FlowsWithRecordsAsync([]));

        // A record in any state counts, a held or deleted one as much as a pending one.
        var key = DeliveryKey.Derive("test", ["OTHER-1"]);
        await Ledger.CompleteAsync(otherFlow, new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Held,
            Error = "held by the test",
            Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Held, Phase = "metadata" },
        });
        Assert.Contains(otherFlow, await Ledger.FlowsWithRecordsAsync([otherFlow]));

        // Thousands of identities are asked about in one statement: a query per identity joined by UNION nested as deep as
        // the list was long, and the provider's translation overflowed the stack walking it.
        var many = Enumerable.Range(0, 3000).Select(i => FlowId.Of($"unused-{i}")).Append(otherFlow).ToList();
        Assert.Equal(new HashSet<Guid> { otherFlow }, await Ledger.FlowsWithRecordsAsync(many));
    }

    [Fact]
    public async Task Flows_with_activities_are_the_identities_asked_about_with_one_on_the_trail()
    {
        var otherFlow = FlowId.Of("other-flow");
        var quietFlow = FlowId.Of("test-flow-two");
        var unregistered = FlowId.Of("never-registered");
        await Ledger.StartActivityAsync(new ActivityRecord { FlowId = _flow, FlowName = "test-flow", Kind = "release", Actor = "user:test", StartedUtc = Now });

        // A ledger with records but no activity is not one: the trail is what is asked about, not the records.
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(quietFlow, [Pending("QUIET-1", submission) with { FlowId = quietFlow }]);
        Assert.Equal(new HashSet<Guid> { _flow }, await Ledger.FlowsWithActivitiesAsync([_flow, otherFlow, quietFlow, unregistered, _flow]));
        Assert.Empty(await Ledger.FlowsWithActivitiesAsync([otherFlow, quietFlow]));
        Assert.Empty(await Ledger.FlowsWithActivitiesAsync([]));

        // Any activity counts, whatever its outcome: a run that changed nothing, or one that failed.
        var idle = await Ledger.StartActivityAsync(new ActivityRecord { FlowId = otherFlow, FlowName = "other-flow", Kind = "deliver", Actor = "schedule:test", StartedUtc = Now });
        await Ledger.CompleteActivityAsync(idle.ActivityId, "completed", "nothing to deliver", null, Now, idle: true);
        var failed = await Ledger.StartActivityAsync(new ActivityRecord { FlowId = quietFlow, FlowName = "test-flow-two", Kind = "deliver", Actor = "schedule:test", StartedUtc = Now });
        await Ledger.CompleteActivityAsync(failed.ActivityId, "failed", "the token request timed out", null, Now);
        Assert.Equal(new HashSet<Guid> { _flow, otherFlow, quietFlow }, await Ledger.FlowsWithActivitiesAsync([_flow, otherFlow, quietFlow]));

        // Thousands of identities are asked about in one statement, as for the records.
        var many = Enumerable.Range(0, 3000).Select(i => FlowId.Of($"unused-{i}")).Append(otherFlow).ToList();
        Assert.Equal(new HashSet<Guid> { otherFlow }, await Ledger.FlowsWithActivitiesAsync(many));
    }

    [Fact]
    public async Task Recent_and_lookup_narrow_to_one_flow()
    {
        var submission = Guid.NewGuid();
        var otherFlow = FlowId.Of("other-flow");
        var ours = FlowId.Of("test-flow");
        await Ledger.UpsertPendingAsync(otherFlow, new[] { "WELL-A1", "WELL-A2", "WELL-A3" }.Select(k => Pending(k, submission) with { FlowId = otherFlow }).ToList());
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Ledger.UpsertPendingAsync(ours, [Pending("WELL-B1", submission)]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Ledger.UpsertPendingAsync(otherFlow, [Pending("WELL-A4", submission) with { FlowId = otherFlow }]);

        // The recency listing of one flow is that flow's records alone, newest first, however recently another flow wrote.
        Assert.Equal("WELL-B1", Assert.Single(await Ledger.ListRecentAsync(10, flowId: ours)).SourceKey);
        Assert.Equal(new BoundedCount(1, Exact: true), await Ledger.CountRecentAsync(10, flowId: ours));
        var theirs = await Ledger.ListRecentAsync(10, flowId: otherFlow);
        Assert.Equal(4, theirs.Count);
        Assert.Equal("WELL-A4", theirs[0].SourceKey);
        Assert.All(theirs, r => Assert.Equal(otherFlow, r.FlowId));
        Assert.Equal(new BoundedCount(2, Exact: false), await Ledger.CountRecentAsync(2, flowId: otherFlow));
        Assert.Empty(await Ledger.ListRecentAsync(10, flowId: FlowId.Of("no-such-flow")));

        // A prefix both flows share finds the chosen flow's record only.
        Assert.Equal(ours, Assert.Single(await Ledger.LookupAsync("WELL-", 10, flowId: ours)).FlowId);
        Assert.Equal(4, (await Ledger.LookupAsync("WELL-", 10, flowId: otherFlow)).Count);

        // The other flow's tokens sort first, so over every flow a bound of two is spent on them. Narrowed to one flow,
        // the candidates are that flow's own tokens: its record is reached, and the count is exact.
        Assert.Equal(new BoundedCount(2, Exact: false), await Ledger.CountLookupAsync("WELL-", 2));
        Assert.Equal(new BoundedCount(1, Exact: true), await Ledger.CountLookupAsync("WELL-", 2, flowId: ours));

        // The same row read by both flows is one record in each: its delivery key finds both, and the chosen flow's alone.
        await Ledger.UpsertPendingAsync(otherFlow, [Pending("WELL-B1", submission) with { FlowId = otherFlow, TargetId = "dev:y:WELL-B1" }]);
        var shared = DeliveryKey.Derive("test", ["WELL-B1"]);
        Assert.Equal(2, (await Ledger.LookupAsync(shared.Value.ToString(), 10)).Count);
        Assert.Equal(otherFlow, Assert.Single(await Ledger.LookupAsync(shared.Value.ToString(), 10, flowId: otherFlow)).FlowId);
        Assert.Equal(new BoundedCount(1, Exact: true), await Ledger.CountLookupAsync(shared.Value.ToString(), 10, flowId: ours));

        // A custody state and a flow narrow together.
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Ledger.CompleteAsync(ours, new RecordCompletion
        {
            DeliveryKey = shared,
            Status = RecordStatus.Held,
            Error = "held by the test",
            Attempt = new AttemptRecord { DeliveryKey = shared, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Held, Phase = "metadata" },
        });
        Assert.Equal(ours, Assert.Single(await Ledger.ListRecentAsync(10, RecordStatus.Held, ours)).FlowId);
        Assert.Empty(await Ledger.ListRecentAsync(10, RecordStatus.Held, otherFlow));
        Assert.Equal(new BoundedCount(0, Exact: true), await Ledger.CountRecentAsync(10, RecordStatus.Held, otherFlow));
        Assert.Single(await Ledger.LookupAsync("WELL-B", 10, RecordStatus.Held, ours));
        Assert.Empty(await Ledger.LookupAsync("WELL-B", 10, RecordStatus.Held, otherFlow));
        Assert.Equal(new BoundedCount(1, Exact: true), await Ledger.CountLookupAsync("WELL-B", 10, RecordStatus.Pending, otherFlow));
    }

    [Fact]
    public async Task Record_listings_count_to_a_limit_page_in_a_stable_order_and_bound_their_searches()
    {
        var s1 = Guid.NewGuid();

        // Twelve records under one update time, as a bulk write stamps a batch: the pages must still partition them.
        await Ledger.UpsertPendingAsync(_flow, Enumerable.Range(0, 12).Select(i => Pending($"WELL-{i:D2}", s1)).ToList());
        var all = new RecordQuery();
        Assert.Equal(new BoundedCount(12, Exact: true), await Ledger.CountAsync(_flow, all, 13));
        Assert.Equal(new BoundedCount(5, Exact: false), await Ledger.CountAsync(_flow, all, 5));
        var paged = new List<DeliveryKey>();
        for (var offset = 0; offset < 12; offset += 5)
        {
            paged.AddRange((await Ledger.ListAsync(_flow, all with { Offset = offset, Max = 5 })).Select(r => r.DeliveryKey));
        }

        Assert.Equal(12, paged.Distinct().Count());
        await Assert.ThrowsAsync<RecordQueryTooBroadException>(() => Ledger.ListAsync(_flow, all with { Offset = RecordListing.CountLimit }));

        // Two are held, and they sort last by source key. A prefix search whose candidates stop before reaching them
        // cannot say there are none: the count is a floor, and a bound that reaches them counts them exactly.
        foreach (var name in new[] { "WELL-10", "WELL-11" })
        {
            var key = DeliveryKey.Derive("test", [name]);
            await Ledger.CompleteAsync(_flow, new RecordCompletion
            {
                DeliveryKey = key,
                Status = RecordStatus.Held,
                Error = "held by the test",
                Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Held, Phase = "metadata" },
            });
        }

        var heldWells = new RecordQuery { Status = RecordStatus.Held, Search = "WELL-" };
        Assert.Equal(new BoundedCount(0, Exact: false), await Ledger.CountAsync(_flow, heldWells, 5));
        Assert.Equal(new BoundedCount(2, Exact: true), await Ledger.CountAsync(_flow, heldWells, 13));
        Assert.Equal(2, (await Ledger.ListAsync(_flow, heldWells)).Count);

        // A contains term has no index: it runs only over what the rest of the filter leaves, counted first.
        var bounded = new OsduLedger(_db.CreateDbContext, _clock) { ContainsScanLimit = 5 };
        var contains = new RecordQuery { Search = "LL-1", Mode = SearchMode.Contains };
        var refused = await Assert.ThrowsAsync<RecordQueryTooBroadException>(() => bounded.ListAsync(_flow, contains));
        Assert.Contains("prefix search", refused.Message, StringComparison.Ordinal);
        Assert.Equal(2, (await bounded.ListAsync(_flow, contains with { Status = RecordStatus.Held })).Count);
        Assert.Equal(2, (await bounded.ListKeysAsync(_flow, contains with { Status = RecordStatus.Held }, 100)).Count);
    }

    [Fact]
    public async Task A_record_is_found_by_every_value_an_operator_holds_and_a_restaging_leaves_nothing_stale()
    {
        var submission = Guid.NewGuid();
        var key = DeliveryKey.Derive("test", ["L-1001"]);
        var record = Pending("wells:WB_A_1/L-1001", submission) with
        {
            DeliveryKey = key,
            SourceKeyJson = """["WB_A_1","L-1001"]""",
            Label = "OSDU-DEV-1-A / COMPOSITE / run 1 (L-1001)",
            Identities = ["OSDU-DEV-1-A", "WB A/1-19 ST"],
            TargetId = "dev:work-product-component--WellLog:ea10870200ce",
            PendingSourceFileName = "welllog_20260901.csv",
        };
        await Ledger.UpsertPendingAsync(_flow, [record]);

        // Every way an operator arrives: the wellbore id and the well name the mapping declares, a key value, the OSDU
        // id and its trailing part, a word of the label, the file it came from, and the delivery key itself.
        foreach (var term in new[]
        {
            "OSDU-DEV-1-A", "osdu-dev-1", "WB A/1-19", "L-1001", "WB_A_1",
            "dev:work-product-component--WellLog:ea1087", "ea10870200ce", "COMPOSITE", "welllog_2026", key.Value.ToString("D"),
        })
        {
            var found = await Ledger.LookupAsync(term, 10);
            Assert.Equal(key, Assert.Single(found).DeliveryKey);
            Assert.Equal(new BoundedCount(1, Exact: true), await Ledger.CountLookupAsync(term, 10));
        }

        // A status narrows the same lookup without leaving the index.
        Assert.Empty(await Ledger.LookupAsync("OSDU-DEV-1-A", 10, RecordStatus.Delivered));
        Assert.Single(await Ledger.LookupAsync("OSDU-DEV-1-A", 10, RecordStatus.Pending));

        // A short term still seeks the index, because the floor is on the value stored, not on what is typed: an
        // operator who types the first characters of a wellbore id gets the records that start with them.
        Assert.Single(await Ledger.LookupAsync("OS", 10));
        Assert.Empty(await Ledger.LookupAsync("ZZ", 10));

        // The row moves on: a new file, a new wellbore id, a renamed label. The record is found by what it is now, and
        // no longer by what it was, because a staging rewrites the record's tokens as a set.
        await Ledger.UpsertPendingAsync(_flow, [record with
        {
            Label = "OSDU-DEV-1-B / CPI / run 2 (L-1001)",
            Identities = ["OSDU-DEV-1-B"],
            PendingSourceFileName = "welllog_20260902.csv",
        }]);
        Assert.Single(await Ledger.LookupAsync("OSDU-DEV-1-B", 10));
        Assert.Single(await Ledger.LookupAsync("welllog_20260902", 10));
        Assert.Empty(await Ledger.LookupAsync("OSDU-DEV-1-A", 10));
        Assert.Empty(await Ledger.LookupAsync("welllog_20260901", 10));
        // What did not change is still found, and only once however many stagings wrote it.
        Assert.Equal(key, Assert.Single(await Ledger.LookupAsync("L-1001", 10)).DeliveryKey);

        // The same row read by a second flow is that flow's record, and one term finds both.
        var otherFlow = FlowId.Of("test-flow-two");
        await Ledger.UpsertPendingAsync(otherFlow, [record with { FlowId = otherFlow, TargetId = "dev:master-data--Well:ea10870200ce" }]);
        Assert.Equal(2, (await Ledger.LookupAsync("L-1001", 10)).Count);
        Assert.Equal(new BoundedCount(2, Exact: true), await Ledger.CountLookupAsync("L-1001", 10));
    }

    [Fact]
    public void Identity_tokens_are_derived_once_and_bounded()
    {
        // What a record is findable by, and nothing else: the declared identities first, then the key values, the OSDU
        // id with its own part, the label's words and the file. Case is folded for comparison, kept for display.
        var tokens = RecordIdentities.Of(
            ["OSDU-DEV-1-A"],
            "wells:WB_A_1/L-1001",
            ["WB_A_1", "L-1001", null, "  "],
            "OSDU-DEV-1-A / COMPOSITE / run 1 (L-1001)",
            "dev:work-product-component--WellLog:ea1087",
            "welllog_20260901.csv");
        var by = tokens.ToLookup(t => t.Kind);

        Assert.Equal("OSDU-DEV-1-A", Assert.Single(by[RecordIdentityKind.Declared]).Display);
        // The key whole, then its parts: an operator pastes the key as the ledger prints it, or holds one column of it.
        Assert.Equal(["wells:WB_A_1/L-1001", "WB_A_1", "L-1001"], by[RecordIdentityKind.Key].Select(t => t.Display));
        Assert.Contains(by[RecordIdentityKind.Target], t => t.Display == "ea1087");
        Assert.Contains(by[RecordIdentityKind.Label], t => t.Display == "COMPOSITE");
        Assert.Equal("welllog_20260901.csv", Assert.Single(by[RecordIdentityKind.File]).Display);
        // A value the record already carries is one token, whichever way it arrived: the wellbore id is declared, so
        // the label's first word does not repeat it.
        Assert.Equal(tokens.Count, tokens.Select(t => t.Token).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(tokens, t => t.Token == "OSDU-DEV-1-A");
        Assert.All(tokens, t => Assert.Equal(t.Display.ToUpperInvariant(), t.Token));

        // A value too short to tell records apart is not worth an index row, and neither is an empty one.
        Assert.Empty(RecordIdentities.Of(["WB", "1", "", "   "], null, null, null, null, null));

        // A row with more identifying values than the index holds keeps the first, so one record costs a known number
        // of rows however wide its source row is, and a long value is stored by its start.
        var many = RecordIdentities.Of(
            Enumerable.Range(0, 100).Select(i => $"VALUE-{i:D3}").ToList(), null, null, null, null, null);
        Assert.Equal(RecordIdentityLimits.MaxPerRecord, many.Count);
        var long_ = RecordIdentities.Of([new string('W', RecordIdentityLimits.MaxTokenLength + 50)], null, null, null, null, null);
        Assert.Equal(RecordIdentityLimits.MaxTokenLength, Assert.Single(long_).Token.Length);

        // A key tuple the ledger stored as an object, and one it cannot read, are both handled: the record stays
        // findable by everything else rather than failing its staging.
        Assert.Equal(["A1", "B2"], RecordIdentities.KeyValues("""{"project":"A1","log":"B2"}"""));
        Assert.Empty(RecordIdentities.KeyValues("not json"));
        Assert.Empty(RecordIdentities.KeyValues(null));
    }

    [Fact]
    public async Task Records_already_in_the_ledger_are_indexed_a_page_at_a_time()
    {
        // A ledger that predates the identity index: rows written with the index dropped, as a database upgraded to
        // this version holds them.
        var submission = Guid.NewGuid();
        var records = Enumerable.Range(0, 5)
            .Select(i => Pending($"wells:WB_A_1/L-{i:D4}", submission) with { Label = $"WELL-{i:D4}" })
            .ToList();
        await Ledger.UpsertPendingAsync(_flow, records);
        await using (var db = _db.CreateDbContext())
        {
            await db.DeliveryRecordIdentities.ExecuteDeleteAsync();
        }

        Assert.Empty(await Ledger.LookupAsync("WELL-0003", 10));

        // The backfill walks the ledger in key order, a page at a time, and is resumable from the key it last wrote.
        var written = 0;
        RecordCursor? after = null;
        for (var pass = 0; pass < 10; pass++)
        {
            var (last, count, tokens) = await Ledger.BackfillIdentitiesAsync(after, 2);
            if (last is null)
            {
                break;
            }

            Assert.True(tokens >= count);
            written += count;
            after = last;
        }

        Assert.Equal(5, written);
        Assert.Single(await Ledger.LookupAsync("WELL-0003", 10));

        // A pass over records the index already holds writes nothing again, so restarting the control plane is free.
        var again = await Ledger.BackfillIdentitiesAsync(null, 100);
        Assert.Equal(0, again.Records);
    }

    [Fact]
    public async Task A_submission_s_delivered_records_stay_its_own_after_later_submissions_move_them_on()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        var a = DeliveryKey.Derive("test", ["a"]);
        var b = DeliveryKey.Derive("test", ["b"]);
        var c = DeliveryKey.Derive("test", ["c"]);
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", s1), Pending("b", s1), Pending("c", s1)]);

        // The first batch delivers a and b and fails on c.
        var claimed = (await Ledger.ClaimAsync(_flow, s1, "w1", 10, TimeSpan.FromMinutes(5), Now)).Records;
        Assert.Equal(3, claimed.Count);
        foreach (var record in claimed)
        {
            var delivered = record.DeliveryKey != c;
            await Ledger.CompleteAsync(_flow, new RecordCompletion
            {
                DeliveryKey = record.DeliveryKey,
                Status = delivered ? RecordStatus.Delivered : RecordStatus.Failed,
                Promote = delivered,
                TargetId = record.TargetId,
                TargetVersion = delivered ? 1 : null,
                Error = delivered ? null : "500 from the target",
                Attempt = new AttemptRecord
                {
                    DeliveryKey = record.DeliveryKey,
                    SubmissionId = s1,
                    Worker = "w1",
                    StartedUtc = Now,
                    CompletedUtc = Now,
                    Outcome = delivered ? AttemptOutcome.Delivered : AttemptOutcome.Failed,
                    Phase = "metadata+payload",
                    TargetVersion = delivered ? 1 : null,
                    Error = delivered ? null : "500 from the target",
                },
            });
        }

        // A later batch finds a unchanged and delivers b again: both now belong to it as their last submission.
        await Ledger.MarkSkippedAsync(_flow, [new SkippedRecord { DeliveryKey = a, Kind = SkipKind.Unchanged, Reason = "unchanged" }], s2);
        await Ledger.UpsertPendingAsync(_flow, [Pending("b", s2)]);
        var again = Assert.Single((await Ledger.ClaimAsync(_flow, s2, "w2", 10, TimeSpan.FromMinutes(5), Now)).Records);
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = again.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetId = again.TargetId,
            TargetVersion = 2,
            Attempt = new AttemptRecord
            {
                DeliveryKey = again.DeliveryKey, SubmissionId = s2, Worker = "w2", StartedUtc = Now, CompletedUtc = Now,
                Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload", TargetVersion = 2,
            },
        });
        Assert.Equal(s2, (await Ledger.GetRecordAsync(_flow, a))!.LastSubmissionId);
        Assert.Equal(s2, (await Ledger.GetRecordAsync(_flow, b))!.LastSubmissionId);

        // "The records of the first submission" has moved on to what it left behind: only the failure is still its own.
        var lastPlannedBy1 = new RecordQuery { SubmissionId = s1 };
        Assert.Equal([c], (await Ledger.ListAsync(_flow, lastPlannedBy1)).Select(r => r.DeliveryKey).ToList());

        // What the first batch delivered is still what it delivered, whatever happened to the records since; the
        // failure it never got into OSDU is not part of it, and the listing, the count and the removal's keys agree.
        var deliveredBy1 = new RecordQuery { DeliveredBySubmissionId = s1 };
        var sent = (await Ledger.ListAsync(_flow, deliveredBy1)).Select(r => r.DeliveryKey).OrderBy(k => k.Value).ToList();
        Assert.Equal(new[] { a, b }.OrderBy(k => k.Value).ToList(), sent);
        Assert.Equal(new BoundedCount(2, Exact: true), await Ledger.CountAsync(_flow, deliveredBy1, 10));
        Assert.Equal(sent, (await Ledger.ListKeysAsync(_flow, deliveredBy1, 10)).OrderBy(k => k.Value).ToList());
        Assert.Equal([b], (await Ledger.ListAsync(_flow, new RecordQuery { DeliveredBySubmissionId = s2 })).Select(r => r.DeliveryKey).ToList());
        Assert.Empty(await Ledger.ListAsync(_flow, new RecordQuery { DeliveredBySubmissionId = Guid.NewGuid() }));

        // The filter composes with the rest of the listing, the way a removal aimed at part of a batch needs it to.
        Assert.Equal([a], (await Ledger.ListAsync(_flow, deliveredBy1 with { Search = "a" })).Select(r => r.DeliveryKey).ToList());
        Assert.Empty(await Ledger.ListAsync(_flow, deliveredBy1 with { Status = RecordStatus.Failed }));

        // The lookup across every flow narrows to a custody state without leaving its identity indexes.
        Assert.Equal(3, (await Ledger.LookupAsync("dev:x:", 10)).Count);
        Assert.Equal([c], (await Ledger.LookupAsync("dev:x:", 10, RecordStatus.Failed)).Select(r => r.DeliveryKey).ToList());
        Assert.Equal(new BoundedCount(1, Exact: true), await Ledger.CountLookupAsync("dev:x:", 10, RecordStatus.Failed));
        Assert.Equal(new BoundedCount(2, Exact: true), await Ledger.CountLookupAsync("dev:x:", 10, RecordStatus.Delivered));
    }

    [Fact]
    public async Task Claim_leases_pending_records_once_and_complete_promotes_pending_state()
    {
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", submission), Pending("b", submission)]);

        var claimed = (await Ledger.ClaimAsync(_flow, submission, "w1", 10, TimeSpan.FromMinutes(5), Now)).Records;
        Assert.Equal(2, claimed.Count);
        Assert.All(claimed, r => Assert.Equal(RecordStatus.Delivering, r.Status));
        Assert.All(claimed, r => Assert.Equal(1, r.AttemptCount));
        Assert.All(claimed, r => Assert.StartsWith("w1/", r.LeaseOwner!, StringComparison.Ordinal));
        Assert.All(claimed, r => Assert.Equal(Now.AddMinutes(5), r.LeaseExpiresUtc));
        Assert.Empty((await Ledger.ClaimAsync(_flow, submission, "w2", 10, TimeSpan.FromMinutes(5), Now)).Records);
        // One lease holds both records, and renewing it is one row.
        Assert.Single(claimed.Select(r => r.LeaseOwner).Distinct());
        Assert.True(await Ledger.RenewLeaseAsync(claimed[0].LeaseOwner!, TimeSpan.FromMinutes(6), Now));
        foreach (var leased in claimed)
        {
            Assert.Equal(Now.AddMinutes(6), (await Ledger.GetRecordAsync(_flow, leased.DeliveryKey))!.LeaseExpiresUtc);
        }

        Assert.False(await Ledger.RenewLeaseAsync("someone-else/" + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), Now));

        var record = claimed[0];
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = record.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 42,
            TargetId = record.TargetId,
            Attempt = new AttemptRecord { DeliveryKey = record.DeliveryKey, SubmissionId = submission, Worker = "w1", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload", TargetVersion = 42 },
        });

        var delivered = await Ledger.GetRecordAsync(_flow, record.DeliveryKey);
        Assert.Equal(RecordStatus.Delivered, delivered!.Status);
        Assert.Equal("mh", delivered.MetadataHash);
        Assert.Equal("ph", delivered.PayloadHash);
        Assert.Equal("fp", delivered.SourceFingerprint);
        Assert.Equal(42, delivered.TargetVersion);
        Assert.Null(delivered.PendingDocumentRef);
        Assert.Null(delivered.LeaseOwner);
        Assert.Equal(0, delivered.AttemptCount);
        Assert.Single(await Ledger.ListAttemptsAsync(_flow, record.DeliveryKey, 10));
        Assert.Equal(1, await Ledger.CountAsync(_flow, submission, RecordStatus.Delivered));
        Assert.True(await Ledger.HasPendingAsync(_flow, submission, Now));
    }

    [Fact]
    public async Task Expired_leases_are_reclaimed_and_backoff_is_honoured()
    {
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", submission)]);
        var first = (await Ledger.ClaimAsync(_flow, null, "w1", 10, TimeSpan.FromMinutes(5), Now)).Records;
        Assert.Single(first);

        _clock.Advance(TimeSpan.FromMinutes(6));
        var reclaimed = await Ledger.RecoverExpiredLeasesAsync(_flow, Now);
        Assert.Equal(1, reclaimed);
        var state = await Ledger.GetRecordAsync(_flow, first[0].DeliveryKey);
        Assert.Equal(RecordStatus.Pending, state!.Status);
        Assert.Contains("lease expired", state.LastError, StringComparison.Ordinal);

        var second = (await Ledger.ClaimAsync(_flow, null, "w2", 10, TimeSpan.FromMinutes(5), Now)).Records;
        Assert.Single(second);
        Assert.Equal(2, second[0].AttemptCount);

        // Retry later: the record goes back to pending with a next-attempt time and is not claimable until then.
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = second[0].DeliveryKey,
            Status = RecordStatus.Pending,
            NextAttemptUtc = Now + TimeSpan.FromMinutes(10),
            Error = "503",
            Attempt = new AttemptRecord { DeliveryKey = second[0].DeliveryKey, Worker = "w2", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Failed, Phase = "none", Error = "503" },
        });
        Assert.Empty((await Ledger.ClaimAsync(_flow, null, "w3", 10, TimeSpan.FromMinutes(5), Now)).Records);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Single((await Ledger.ClaimAsync(_flow, null, "w3", 10, TimeSpan.FromMinutes(5), Now)).Records);

        // A claim that expired while still 'delivering' is picked up directly by the next claim as well.
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Single((await Ledger.ClaimAsync(_flow, null, "w4", 10, TimeSpan.FromMinutes(5), Now)).Records);
    }

    [Fact]
    public async Task Closing_a_lease_on_shutdown_does_not_charge_the_attempt_and_a_lease_that_runs_out_does()
    {
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", submission)]);
        var claim = await Ledger.ClaimAsync(_flow, submission, "w1", 10, TimeSpan.FromMinutes(5), Now);
        var lease = claim.Lease!;
        var record = claim.Records.Single();
        Assert.Equal((1, lease.Token), (record.AttemptCount, record.LeaseOwner));
        Assert.Equal((_flow, (Guid?)submission, (int?)null, "w1"), (lease.FlowId, lease.SubmissionId, lease.WorkBatch, lease.Owner));
        Assert.Equal(Now.AddMinutes(5), await Ledger.NextLeaseExpiryAsync(_flow, submission));

        // A lease nobody holds closes nothing; its holder's close hands the record back, once, and the lease is gone.
        var stopped = new LeaseClosing { End = LeaseEnd.Stopped };
        Assert.Equal(LeaseApplied.None, await Ledger.CloseLeaseAsync("someone-else/" + Guid.NewGuid().ToString("N"), stopped, Now));
        Assert.Equal(new LeaseApplied(0, 1), await Ledger.CloseLeaseAsync(lease.Token, stopped, Now));
        Assert.Equal(LeaseApplied.None, await Ledger.CloseLeaseAsync(lease.Token, stopped, Now));
        Assert.False(await Ledger.RenewLeaseAsync(lease.Token, TimeSpan.FromMinutes(5), Now));
        Assert.Null(await Ledger.NextLeaseExpiryAsync(_flow, submission));

        var released = await Ledger.GetRecordAsync(_flow, record.DeliveryKey);
        Assert.Equal(RecordStatus.Pending, released!.Status);
        Assert.Equal(0, released.AttemptCount);
        Assert.Null(released.LeaseOwner);
        Assert.Null(released.LeaseExpiresUtc);
        Assert.Contains("stopped mid-attempt", released.LastError, StringComparison.Ordinal);
        Assert.Empty(await Ledger.ListAttemptsAsync(_flow, record.DeliveryKey, 10));

        // Immediately claimable again, and the retry budget starts from the first attempt.
        var again = (await Ledger.ClaimAsync(_flow, submission, "w2", 10, TimeSpan.FromMinutes(5), Now)).Records;
        Assert.Equal(1, again.Single().AttemptCount);

        // A lease that runs out is recovered by the next claim of the flow, and the interrupted try counts. A worker that
        // was only slow cannot renew it once it is recovered.
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(0, await Ledger.RecoverExpiredLeasesAsync(Guid.NewGuid(), Now));
        Assert.Equal(1, await Ledger.RecoverExpiredLeasesAsync(_flow, Now));
        var recovered = await Ledger.GetRecordAsync(_flow, record.DeliveryKey);
        Assert.Equal((RecordStatus.Pending, 1, (string?)null), (recovered!.Status, recovered.AttemptCount, recovered.LeaseOwner));
        Assert.Contains("lease expired", recovered.LastError, StringComparison.Ordinal);
        Assert.False(await Ledger.RenewLeaseAsync(again[0].LeaseOwner!, TimeSpan.FromMinutes(5), Now));
        Assert.Null(await Ledger.NextLeaseExpiryAsync(_flow, null));
    }

    [Fact]
    public async Task Upsert_pending_preserves_current_state_and_held_records_can_be_released()
    {
        var s1 = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", s1)]);
        var claimed = (await Ledger.ClaimAsync(_flow, s1, "w", 10, TimeSpan.FromMinutes(1), Now)).Records;
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = claimed[0].DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            Attempt = new AttemptRecord { DeliveryKey = claimed[0].DeliveryKey, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata" },
        });

        var s2 = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", s2) with { PendingMetadataHash = "mh2", PendingPayload = false, PendingPayloadHash = null }]);
        var state = await Ledger.GetRecordAsync(_flow, claimed[0].DeliveryKey);
        Assert.Equal(RecordStatus.Pending, state!.Status);
        Assert.Equal("mh", state.MetadataHash);
        Assert.Equal("ph", state.PayloadHash);
        Assert.Equal(1, state.TargetVersion);
        Assert.Equal("mh2", state.PendingMetadataHash);
        Assert.Equal(s2, state.LastSubmissionId);

        await Ledger.MarkSkippedAsync(_flow, [new SkippedRecord { DeliveryKey = claimed[0].DeliveryKey, Kind = SkipKind.Unchanged, Reason = "unchanged" }], s2);
        var held = Pending("b", s2) with { LastError = "no wellbore" };
        await Ledger.MarkHeldAsync(_flow, [held]);
        var heldState = await Ledger.GetRecordAsync(_flow, held.DeliveryKey);
        Assert.Equal(RecordStatus.Held, heldState!.Status);
        Assert.Equal("no wellbore", heldState.LastError);
        Assert.Equal(1, await Ledger.CountAsync(_flow, s2, RecordStatus.Held));

        // A held record without a pending document cannot go to the worker; releasing it unblocks the next plan.
        Assert.Equal(1, await Ledger.ReleaseAsync(_flow, [held.DeliveryKey], Now));
        var unblocked = await Ledger.GetRecordAsync(_flow, held.DeliveryKey);
        Assert.Equal(RecordStatus.Held, unblocked!.Status);
        Assert.True(heldState.Blocked);
        Assert.False(unblocked.Blocked);
        Assert.Contains("released", unblocked.LastError, StringComparison.Ordinal);

        await Ledger.UpsertPendingAsync(_flow, [Pending("c", s2)]);
        var c = (await Ledger.ClaimAsync(_flow, s2, "w", 10, TimeSpan.FromMinutes(1), Now)).Records;
        var cRecord = c.Single(r => r.SourceKey == "c");
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = cRecord.DeliveryKey,
            Status = RecordStatus.Held,
            Error = "409",
            Attempt = new AttemptRecord { DeliveryKey = cRecord.DeliveryKey, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Held, Phase = "none", Error = "409" },
        });
        Assert.Equal(1, await Ledger.ReleaseAsync(_flow, null, Now));
        Assert.Equal(RecordStatus.Pending, (await Ledger.GetRecordAsync(_flow, cRecord.DeliveryKey))!.Status);
    }

    [Fact]
    public async Task Work_batches_lease_their_due_records_and_close_with_counts()
    {
        var submission = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(submission));
        await Ledger.UpsertPendingAsync(_flow, [
            Pending("a", submission) with { WorkBatch = 3, PendingDocumentRef = "3:0:10" },
            Pending("b", submission) with { WorkBatch = 3, PendingDocumentRef = "3:11:10" },
            Pending("c", submission) with { WorkBatch = 4, PendingDocumentRef = "4:0:10" },
        ]);
        await Ledger.AddWorkBatchAsync(new WorkBatchState { SubmissionId = submission, FlowId = _flow, Index = 3, Location = "batch-3", RecordCount = 2, CreatedUtc = Now });
        await Ledger.AddWorkBatchAsync(new WorkBatchState { SubmissionId = submission, FlowId = _flow, Index = 4, Location = "batch-4", RecordCount = 1, CreatedUtc = Now.AddSeconds(1) });
        Assert.Equal(2, await Ledger.CountWorkBatchesAsync(submission, WorkBatchStatus.Queued));

        var claimed = await Ledger.ClaimWorkBatchAsync(_flow, submission, "w1", TimeSpan.FromMinutes(5), Now, runId: Guid.NewGuid());
        Assert.NotNull(claimed);
        Assert.Equal(3, claimed!.Batch.Index);
        Assert.Equal(WorkBatchStatus.Running, claimed.Batch.Status);
        Assert.Equal(2, claimed.Records.Count);
        Assert.All(claimed.Records, r => Assert.Equal(RecordStatus.Delivering, r.Status));
        Assert.All(claimed.Records, r => Assert.Equal(claimed.Lease.Token, r.LeaseOwner));
        Assert.Equal((claimed.Lease.Token, claimed.Lease.ExpiresUtc), (claimed.Batch.LeaseOwner, claimed.Batch.LeaseExpiresUtc));
        Assert.Equal(((Guid?)submission, (int?)3), (claimed.Lease.SubmissionId, claimed.Lease.WorkBatch));

        // Records leased under the batch are invisible to the individual claim; a record of a queued batch is not.
        var loose = (await Ledger.ClaimAsync(_flow, submission, "w2", 10, TimeSpan.FromMinutes(5), Now)).Records;
        Assert.Equal("c", Assert.Single(loose).SourceKey);
        Assert.True(await Ledger.RenewLeaseAsync(claimed.Lease.Token, TimeSpan.FromMinutes(5), Now));
        Assert.Equal(Now.AddMinutes(5), (await Ledger.ListWorkBatchesAsync(submission, 10, 0)).Single(b => b.Index == 3).LeaseExpiresUtc);

        // The worker appends; nothing is applied to the records until the lease checkpoints or closes, and the attempts
        // are there at once.
        await Ledger.AppendAsync(_flow, claimed.Lease.Token, new LeaseAppend([], claimed.Records.Select(r => new RecordCompletion
        {
            DeliveryKey = r.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 5,
            TargetStateJson = "{\"recordId\":\"x\"}",
            Attempt = new AttemptRecord { DeliveryKey = r.DeliveryKey, SubmissionId = submission, Worker = "w1", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata", ResultJson = "{\"steps\":[]}", WorkBatch = 3 },
        }).ToList()));
        Assert.Equal(RecordStatus.Delivering, (await Ledger.GetRecordAsync(_flow, claimed.Records[0].DeliveryKey))!.Status);
        Assert.Equal(2, await Ledger.CountAttemptsAsync(submission, AttemptOutcome.Delivered));
        Assert.Equal(new LeaseApplied(2, 0), await Ledger.CloseLeaseAsync(claimed.Lease.Token, new LeaseClosing { End = LeaseEnd.Done, Delivered = 2 }, Now));

        var batches = await Ledger.ListWorkBatchesAsync(submission, 10, 0);
        var done = batches.Single(b => b.Index == 3);
        Assert.Equal(WorkBatchStatus.Done, done.Status);
        Assert.Equal(2, done.Delivered);
        Assert.Null(done.LeaseOwner);
        Assert.NotNull(done.CompletedUtc);
        var a = await Ledger.GetRecordAsync(_flow, claimed.Records[0].DeliveryKey);
        Assert.Equal(RecordStatus.Delivered, a!.Status);
        Assert.Null(a.PendingDocumentRef);
        Assert.Null(a.WorkBatch);
        Assert.Equal("{\"recordId\":\"x\"}", a.TargetStateJson);
        var attempts = await Ledger.ListAttemptsAsync(_flow, a.DeliveryKey, 5);
        Assert.Equal(3, attempts[0].WorkBatch);
        Assert.Equal("{\"steps\":[]}", attempts[0].ResultJson);

        // Batch 4: its only record is leased by w2, so the claim finds nothing due; a stop hands the batch back.
        var second = await Ledger.ClaimWorkBatchAsync(_flow, submission, "w1", TimeSpan.FromMinutes(5), Now);
        Assert.NotNull(second);
        Assert.Equal(4, second!.Batch.Index);
        Assert.Empty(second.Records);
        Assert.Equal(LeaseApplied.None, await Ledger.CloseLeaseAsync(second.Lease.Token, new LeaseClosing { End = LeaseEnd.Stopped }, Now));
        Assert.Equal(1, await Ledger.CountWorkBatchesAsync(submission, WorkBatchStatus.Queued));

        // An expired batch lease is recovered by the next claim of the flow.
        var third = await Ledger.ClaimWorkBatchAsync(_flow, submission, "w3", TimeSpan.FromMinutes(1), Now);
        Assert.NotNull(third);
        Assert.Equal(0, await Ledger.CountWorkBatchesAsync(submission, WorkBatchStatus.Queued));
        _clock.Advance(TimeSpan.FromMinutes(2));
        await Ledger.RecoverExpiredLeasesAsync(_flow, Now);
        Assert.Equal(1, await Ledger.CountWorkBatchesAsync(submission, WorkBatchStatus.Queued));
        Assert.Null((await Ledger.ListWorkBatchesAsync(submission, 10, 0)).Single(b => b.Index == 4).LeaseOwner);
        Assert.Null(await Ledger.ClaimWorkBatchAsync(Guid.NewGuid(), null, "w4", TimeSpan.FromMinutes(1), Now));
    }

    [Fact]
    public async Task Writes_that_reach_more_records_than_a_slice_reach_every_one_of_them()
    {
        // Five records, two to a statement: each write below runs in three slices and must still reach all five.
        var sliced = new OsduLedger(_db.CreateDbContext, _clock) { WriteSlice = 2 };
        var submission = Guid.NewGuid();
        var records = Enumerable.Range(0, 5).Select(i => Pending($"sliced-{i}", submission) with { WorkBatch = 0, PendingDocumentRef = $"0:{i * 10}:10" }).ToList();
        Assert.Equal(5, (await sliced.UpsertPendingAsync(_flow, records)).Staged);
        await sliced.AddWorkBatchAsync(new WorkBatchState { SubmissionId = submission, FlowId = _flow, Index = 0, Location = "batch-0", RecordCount = 5, CreatedUtc = Now });

        var claimed = await sliced.ClaimWorkBatchAsync(_flow, submission, "w1", TimeSpan.FromMinutes(5), Now);
        var token = claimed!.Lease.Token;
        Assert.Equal(5, claimed.Records.Count);
        Assert.All(claimed.Records, r => Assert.Equal((RecordStatus.Delivering, token, 1), (r.Status, r.LeaseOwner, r.AttemptCount)));

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await sliced.RenewLeaseAsync(token, TimeSpan.FromMinutes(5), Now));
        foreach (var record in records)
        {
            Assert.Equal(Now.AddMinutes(5), (await sliced.GetRecordAsync(_flow, record.DeliveryKey))!.LeaseExpiresUtc);
        }

        // Steps, two to a slice, applied in the order they were appended: each record keeps its last.
        await sliced.AppendAsync(_flow, token, new LeaseAppend(
            records.SelectMany(r => new[] { 1, 2 }.Select(n => new RecordStep(r.DeliveryKey, submission, r.PendingDocumentRef!, $"{{\"upload\":{{\"n\":\"{n}\"}}}}", Now))).ToList(),
            []));
        Assert.Equal(new LeaseApplied(0, 0), await sliced.CheckpointLeaseAsync(token, Now));
        foreach (var record in records)
        {
            Assert.Equal("{\"upload\":{\"n\":\"2\"}}", (await sliced.GetRecordAsync(_flow, record.DeliveryKey))!.PendingStepJson);
        }

        // Closing the batch hands back every record it never reached, without charging the try.
        Assert.Equal(new LeaseApplied(0, 5), await sliced.CloseLeaseAsync(token, new LeaseClosing { End = LeaseEnd.Failed, Failure = "the node stopped" }, Now));
        Assert.Equal("the node stopped", (await sliced.ListWorkBatchesAsync(submission, 10, 0)).Single().Error);
        foreach (var record in records)
        {
            var released = await sliced.GetRecordAsync(_flow, record.DeliveryKey);
            Assert.Equal((RecordStatus.Pending, (string?)null, 0), (released!.Status, released.LeaseOwner, released.AttemptCount));
        }

        // A retry lease that runs out hands back every record it holds.
        Assert.Equal(5, ((await sliced.ClaimAsync(_flow, submission, "w2", 10, TimeSpan.FromMinutes(1), Now)).Records).Count);
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(5, await sliced.RecoverExpiredLeasesAsync(_flow, Now));
        Assert.Equal(5, await sliced.CountAsync(_flow, submission, RecordStatus.Pending));

        // Failed records are released, and then redelivered, a slice at a time.
        await sliced.CompleteManyAsync(_flow, records.Select(r => new RecordCompletion
        {
            DeliveryKey = r.DeliveryKey,
            Status = RecordStatus.Failed,
            Error = "refused by the target",
            Attempt = new AttemptRecord { DeliveryKey = r.DeliveryKey, SubmissionId = submission, Worker = "w2", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Failed, Phase = "metadata" },
        }).ToList());
        Assert.Equal(5, await sliced.CountAsync(_flow, submission, RecordStatus.Failed));
        Assert.Equal(5, await sliced.ReleaseAsync(_flow, null, Now));
        Assert.Equal(5, await sliced.CountAsync(_flow, submission, RecordStatus.Pending));
        Assert.Equal(5, await sliced.ForceRedeliverAsync(_flow, records.Select(r => r.DeliveryKey), RedeliverScope.Metadata, Now));
        Assert.Equal(5, (await sliced.ListPlanRequestedAsync(_flow, null, 10)).Count);
    }

    [Fact]
    public async Task A_recovered_lease_applies_what_its_worker_appended_and_the_worker_s_late_writes_settle_only_records_no_one_holds()
    {
        var submission = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(submission));
        var records = new[] { "a", "b", "c", "d" }
            .Select((name, i) => Pending(name, submission) with { WorkBatch = 0, PendingDocumentRef = $"0:{i * 10}:10" })
            .ToList();
        await Ledger.UpsertPendingAsync(_flow, records);
        await Ledger.AddWorkBatchAsync(new WorkBatchState { SubmissionId = submission, FlowId = _flow, Index = 0, Location = "batch-0", RecordCount = 4, CreatedUtc = Now });
        var claimed = await Ledger.ClaimWorkBatchAsync(_flow, submission, "w1", TimeSpan.FromMinutes(1), Now);
        var token = claimed!.Lease.Token;
        var leased = claimed.Records.ToDictionary(r => r.SourceKey);
        const string Step = "{\"upload\":{\"done\":\"1\"}}";

        // The worker delivered a and finished a step of b, then stalled. What it appended waits in the log.
        await Ledger.AppendAsync(_flow, token, new LeaseAppend(
            [new RecordStep(leased["b"].DeliveryKey, submission, leased["b"].PendingDocumentRef!, Step, Now)],
            [Delivered(leased["a"], submission)]));
        Assert.Equal(RecordStatus.Delivering, (await Ledger.GetRecordAsync(_flow, leased["a"].DeliveryKey))!.Status);

        // Its lease runs out, and the recovery applies both, then hands b, c and d back with their try charged and an attempt
        // saying so on each.
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(4, await Ledger.RecoverExpiredLeasesAsync(_flow, Now));
        var a = await Ledger.GetRecordAsync(_flow, leased["a"].DeliveryKey);
        Assert.Equal((RecordStatus.Delivered, (string?)null, 0, (long?)7), (a!.Status, a.LeaseOwner, a.AttemptCount, a.TargetVersion));
        var b = await Ledger.GetRecordAsync(_flow, leased["b"].DeliveryKey);
        Assert.Equal((RecordStatus.Pending, (string?)null, 1, Step), (b!.Status, b.LeaseOwner, b.AttemptCount, b.PendingStepJson));
        Assert.Contains("lease expired", b.LastError, StringComparison.Ordinal);
        var c = await Ledger.GetRecordAsync(_flow, leased["c"].DeliveryKey);
        Assert.Equal((RecordStatus.Pending, 1), (c!.Status, c.AttemptCount));
        var batch = Assert.Single(await Ledger.ListWorkBatchesAsync(submission, 10, 0));
        Assert.Equal((WorkBatchStatus.Queued, (string?)null, (DateTime?)null), (batch.Status, batch.LeaseOwner, batch.LeaseExpiresUtc));
        Assert.False(await Ledger.RenewLeaseAsync(token, TimeSpan.FromMinutes(1), Now));
        Assert.Null(await Ledger.NextLeaseExpiryAsync(_flow, null));
        Assert.Equal(0, await Ledger.RecoverExpiredLeasesAsync(_flow, Now));

        // The worker wakes, records the try of c it had sent, and closes as if its batch were done. The try lands, since no
        // one holds c; the batch and the other records are the recovery's, and stay as it left them.
        await Ledger.AppendAsync(_flow, token, new LeaseAppend([], [Delivered(leased["c"], submission)]));
        Assert.Equal(new LeaseApplied(1, 0), await Ledger.CloseLeaseAsync(token, new LeaseClosing { End = LeaseEnd.Done, Delivered = 2 }, Now));
        Assert.Equal(RecordStatus.Delivered, (await Ledger.GetRecordAsync(_flow, leased["c"].DeliveryKey))!.Status);
        batch = Assert.Single(await Ledger.ListWorkBatchesAsync(submission, 10, 0));
        Assert.Equal((WorkBatchStatus.Queued, 0L), (batch.Status, batch.Delivered));

        // The next worker takes what is left, b resuming from its step. A try the stalled worker reports for d now is
        // history, and never touches the record the next worker holds.
        var next = await Ledger.ClaimWorkBatchAsync(_flow, submission, "w2", TimeSpan.FromMinutes(1), Now);
        Assert.Equal(new[] { "b", "d" }, next!.Records.Select(r => r.SourceKey).Order());
        Assert.Equal(Step, next.Records.Single(r => r.SourceKey == "b").PendingStepJson);
        await Ledger.AppendAsync(_flow, token, new LeaseAppend([], [Delivered(leased["d"], submission)]));
        Assert.Equal(LeaseApplied.None, await Ledger.CheckpointLeaseAsync(token, Now));
        var d = await Ledger.GetRecordAsync(_flow, leased["d"].DeliveryKey);
        Assert.Equal((RecordStatus.Delivering, next.Lease.Token, 2), (d!.Status, d.LeaseOwner, d.AttemptCount));
        var tries = await Ledger.ListAttemptsAsync(_flow, d.DeliveryKey, 10);
        Assert.Equal(2, tries.Count);
        Assert.Contains(tries, t => t.Outcome == AttemptOutcome.Failed && t.Error!.Contains("lease expired", StringComparison.Ordinal));
        Assert.Contains(tries, t => t.Outcome == AttemptOutcome.Delivered);
        Assert.Equal(new LeaseApplied(0, 2), await Ledger.CloseLeaseAsync(next.Lease.Token, new LeaseClosing { End = LeaseEnd.Stopped }, Now));
        Assert.Equal(2, await Ledger.CountAsync(_flow, submission, RecordStatus.Pending));

        // The stalled worker reports d once more, and stops before it closes. No lease will apply that, so a recovery of
        // the flow does once the recovery hold has passed; d is no one's now, so the try lands.
        await Ledger.AppendAsync(_flow, token, new LeaseAppend([], [Delivered(leased["d"], submission)]));
        Assert.Equal(0, await Ledger.RecoverExpiredLeasesAsync(_flow, Now + OsduLedger.RecoveryHold));
        Assert.Equal(0, await Ledger.RecoverExpiredLeasesAsync(Guid.NewGuid(), Now + OsduLedger.RecoveryHold + TimeSpan.FromSeconds(1)));
        Assert.Equal(1, await Ledger.RecoverExpiredLeasesAsync(_flow, Now + OsduLedger.RecoveryHold + TimeSpan.FromSeconds(1)));
        d = await Ledger.GetRecordAsync(_flow, leased["d"].DeliveryKey);
        Assert.Equal((RecordStatus.Delivered, (string?)null), (d!.Status, d.LeaseOwner));
        Assert.Equal(0, await Ledger.RecoverExpiredLeasesAsync(_flow, Now + OsduLedger.RecoveryHold + TimeSpan.FromSeconds(1)));
    }

    private RecordCompletion Delivered(RecordState claimed, Guid submission) => new()
    {
        DeliveryKey = claimed.DeliveryKey,
        Status = RecordStatus.Delivered,
        Promote = true,
        TargetId = claimed.TargetId,
        TargetVersion = 7,
        Claimed = ClaimedWork.Of(claimed),
        Attempt = new AttemptRecord
        {
            DeliveryKey = claimed.DeliveryKey, SubmissionId = submission, Worker = "w1", StartedUtc = Now, CompletedUtc = Now,
            Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload", TargetVersion = 7,
        },
    };

    [Fact]
    public async Task Step_progress_and_next_due_are_tracked_on_the_record()
    {
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("s", submission)]);
        var claimed = (await Ledger.ClaimAsync(_flow, submission, "w", 10, TimeSpan.FromMinutes(5), Now)).Records;
        var key = claimed[0].DeliveryKey;
        await Ledger.SaveStepAsync(_flow, key, submission, "0:0:10", "{\"metadata\":{\"version\":\"3\"}}", Now);
        Assert.Equal("{\"metadata\":{\"version\":\"3\"}}", (await Ledger.GetRecordAsync(_flow, key))!.PendingStepJson);

        var next = Now + TimeSpan.FromMinutes(10);
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Pending,
            NextAttemptUtc = next,
            Error = "503",
            PendingStepJson = "{\"metadata\":{\"version\":\"3\"}}",
            Attempt = new AttemptRecord { DeliveryKey = key, SubmissionId = submission, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Failed, Phase = "none", Error = "503" },
        });
        Assert.Equal(next, await Ledger.NextDueAsync(_flow, submission, Now));
        Assert.Null(await Ledger.NextDueAsync(_flow, submission, next));
        Assert.True(await Ledger.HasPendingAsync(_flow, submission, Now));
        Assert.Equal("{\"metadata\":{\"version\":\"3\"}}", (await Ledger.GetRecordAsync(_flow, key))!.PendingStepJson);

        _clock.Advance(TimeSpan.FromMinutes(11));
        var again = (await Ledger.ClaimAsync(_flow, submission, "w", 10, TimeSpan.FromMinutes(5), Now)).Records;
        Assert.Single(again);
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 4,
            TargetStateJson = JsonMerge.Merge(again[0].TargetStateJson, "{\"version\":\"4\"}"),
            Attempt = new AttemptRecord { DeliveryKey = key, SubmissionId = submission, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata" },
        });
        var delivered = await Ledger.GetRecordAsync(_flow, key);
        Assert.Null(delivered!.PendingStepJson);
        Assert.Equal("{\"version\":\"4\"}", delivered.TargetStateJson);
        Assert.Null(await Ledger.NextDueAsync(_flow, submission, Now));
        Assert.False(await Ledger.HasPendingAsync(_flow, submission, Now));
    }

    [Fact]
    public async Task Verify_reconcile_the_scope_watermark_the_records_waiting_to_be_planned_and_prune()
    {
        var s = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", s)]);
        var claimed = (await Ledger.ClaimAsync(_flow, s, "w", 10, TimeSpan.FromMinutes(1), Now)).Records;
        var key = claimed[0].DeliveryKey;
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 7,
            Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata" },
        });

        var due = await Ledger.ListForVerifyAsync(_flow, null, 10);
        Assert.Single(due);
        await Ledger.RecordVerifyAsync(_flow, key, VerifyOutcome.Drifted, 9, Now, requeue: true);
        var state = await Ledger.GetRecordAsync(_flow, key);
        Assert.Equal(VerifyOutcome.Drifted, state!.LastVerifyOutcome);
        Assert.Null(state.MetadataHash);
        Assert.Equal(RecordStatus.Delivered, state.Status);
        Assert.Empty(await Ledger.ListForVerifyAsync(_flow, Now - TimeSpan.FromHours(1), 10));

        // One watermark per scope, and it never moves back: a late run that read an earlier window leaves it where it is.
        var through = Now;
        await Ledger.SetWatermarkAsync(new SourceWatermark(_flow, "logSource=X", through, s, Now, "ctx-1"));
        await Ledger.SetWatermarkAsync(new SourceWatermark(_flow, "logSource=X", through.AddMinutes(-10), Guid.NewGuid(), Now, "ctx-0"));
        var mark = await Ledger.GetWatermarkAsync(_flow, "logSource=X");
        Assert.Equal(through, mark!.UpdatedThroughUtc);
        Assert.Equal("ctx-1", mark.ContextHash);
        await Ledger.SetWatermarkAsync(new SourceWatermark(_flow, "logSource=X", through.AddMinutes(10), s, Now, "ctx-2"));
        Assert.Equal(through.AddMinutes(10), (await Ledger.GetWatermarkAsync(_flow, "logSource=X"))!.UpdatedThroughUtc);
        Assert.Null(await Ledger.GetWatermarkAsync(_flow, "logSource=Y"));

        // A redelivery asks for the record to be planned again, and the plan that read the request takes it away. A request
        // made after the plan read the record is not the one it answered, and stays for the next run.
        Assert.Equal(1, await Ledger.ForceRedeliverAsync(_flow, [key], RedeliverScope.All, Now));
        var requested = Assert.Single(await Ledger.ListPlanRequestedAsync(_flow, null, 10));
        Assert.Equal(key, requested.DeliveryKey);
        var read = (await Ledger.GetRecordAsync(_flow, key))!;
        Assert.Equal(1, await Ledger.ForceRedeliverAsync(_flow, [key], RedeliverScope.All, Now.AddSeconds(1)));
        await Ledger.MarkSkippedAsync(_flow, [new SkippedRecord { DeliveryKey = key, Kind = SkipKind.Unchanged, Reason = "unchanged", Basis = PlanBasis.Of(read) }], s);
        Assert.Equal(Now.AddSeconds(1), Assert.Single(await Ledger.ListPlanRequestedAsync(_flow, null, 10)).RequestedUtc);
        read = (await Ledger.GetRecordAsync(_flow, key))!;
        await Ledger.MarkSkippedAsync(_flow, [new SkippedRecord { DeliveryKey = key, Kind = SkipKind.Unchanged, Reason = "unchanged", Basis = PlanBasis.Of(read) }], s);
        Assert.Empty(await Ledger.ListPlanRequestedAsync(_flow, null, 10));

        for (var i = 0; i < 3; i++)
        {
            await Ledger.CompleteAsync(_flow, new RecordCompletion
            {
                DeliveryKey = key,
                Status = RecordStatus.Delivered,
                Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = Now - TimeSpan.FromDays(100), CompletedUtc = Now, Outcome = AttemptOutcome.Skipped, Phase = "none" },
            });
        }

        var pruned = await Ledger.PruneAttemptsAsync(Now - TimeSpan.FromDays(30));
        Assert.Equal(2, pruned);
        Assert.Equal(2, (await Ledger.ListAttemptsAsync(_flow, key, 10)).Count);
    }

    [Fact]
    public async Task A_redelivery_of_payload_parts_names_them_on_the_delivered_payload_hash()
    {
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("parts", submission)]);
        var key = DeliveryKey.Derive("test", ["parts"]);
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Delivered,
            Promote = true,
            Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload" },
        });
        var delivered = (await Ledger.GetRecordAsync(_flow, key))!;
        Assert.Equal(("mh", "ph"), (delivered.MetadataHash, delivered.PayloadHash));

        // The parts are named on the payload hash the record was delivered with, which the next plan reads and the next
        // payload delivery replaces; the record half stays as delivered.
        Assert.Equal(1, await Ledger.ForceRedeliverAsync(_flow, [key], new RedeliverSelection(RedeliverScope.Payload, [PayloadParts.Files, PayloadParts.Bulk]), Now));
        var marked = (await Ledger.GetRecordAsync(_flow, key))!;
        Assert.Equal("redeliver:bulk,files", marked.PayloadHash);
        Assert.True(PayloadParts.RedeliverRoles(marked.PayloadHash)!.SetEquals([PayloadParts.Files, PayloadParts.Bulk]));
        Assert.Equal("mh", marked.MetadataHash);
        Assert.Null(marked.PayloadModifiedUtc);
        Assert.Equal("redelivery of files and bulk requested", marked.LastError);
        Assert.Equal(key, Assert.Single(await Ledger.ListPlanRequestedAsync(_flow, null, 10)).DeliveryKey);

        // A selection of no parts is the whole payload, which clears the hash as a payload redelivery always has; a part
        // no route sends is refused before anything is written.
        await Assert.ThrowsAsync<ArgumentException>(() => Ledger.ForceRedeliverAsync(_flow, [key], new RedeliverSelection(RedeliverScope.Payload, ["record"]), Now));
        Assert.Equal("redeliver:bulk,files", (await Ledger.GetRecordAsync(_flow, key))!.PayloadHash);
        Assert.Equal(1, await Ledger.ForceRedeliverAsync(_flow, [key], new RedeliverSelection(RedeliverScope.Payload, []), Now));
        Assert.Null((await Ledger.GetRecordAsync(_flow, key))!.PayloadHash);
        Assert.Equal("redelivery of payload requested", (await Ledger.GetRecordAsync(_flow, key))!.LastError);
    }

    [Fact]
    public async Task A_redelivery_without_keys_marks_every_record_the_flow_has_delivered_and_no_other()
    {
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("sent-1", submission), Pending("sent-2", submission), Pending("waiting", submission)]);
        var sent = new[] { "sent-1", "sent-2" }.Select(name => DeliveryKey.Derive("test", [name])).ToList();
        foreach (var key in sent)
        {
            await Ledger.CompleteAsync(_flow, new RecordCompletion
            {
                DeliveryKey = key,
                Status = RecordStatus.Delivered,
                Promote = true,
                TargetId = $"dev:master-data--Wellbore:{key.Value:N}",
                Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload" },
            });
        }

        Assert.Equal(2, await Ledger.ForceRedeliverAsync(_flow, null, RedeliverScope.Payload, Now));
        foreach (var key in sent)
        {
            var marked = (await Ledger.GetRecordAsync(_flow, key))!;
            Assert.Equal(("mh", (string?)null), (marked.MetadataHash, marked.PayloadHash));
        }

        Assert.Equal(sent.Select(k => k.Value).Order().ToList(), (await Ledger.ListPlanRequestedAsync(_flow, null, 10)).Select(r => r.DeliveryKey.Value).Order().ToList());
        Assert.Equal(RecordStatus.Pending, (await Ledger.GetRecordAsync(_flow, DeliveryKey.Derive("test", ["waiting"])))!.Status);
    }

    [Fact]
    public async Task Flow_statistics_count_statuses_drift_and_the_last_24_hours_to_the_tick()
    {
        // The counts come from the ledger's indexed statistics view, which its migration builds, checked here to the tick.
        _clock.Advance(TimeSpan.FromMinutes(30));
        var now = Now;
        var s1 = Guid.NewGuid();
        (string Name, TimeSpan Before)[] deliveries =
        [
            ("old", TimeSpan.FromHours(25)),
            ("part-hour-outside", TimeSpan.FromHours(24) + TimeSpan.FromMinutes(10)),
            ("part-hour-inside", TimeSpan.FromHours(24) - TimeSpan.FromMinutes(10)),
            ("recent", TimeSpan.FromHours(1)),
        ];
        await Ledger.UpsertPendingAsync(_flow, deliveries.Select(d => Pending(d.Name, s1)).ToList());
        foreach (var (name, before) in deliveries)
        {
            _clock.Advance(now - before - Now);
            var key = DeliveryKey.Derive("test", [name]);
            await Ledger.CompleteAsync(_flow, new RecordCompletion
            {
                DeliveryKey = key,
                Status = RecordStatus.Delivered,
                Promote = true,
                Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload" },
            });
        }

        _clock.Advance(now - Now);
        await Ledger.RecordVerifyAsync(_flow, DeliveryKey.Derive("test", ["recent"]), VerifyOutcome.Drifted, 2, Now, requeue: false);
        await Ledger.UpsertPendingAsync(_flow, [Pending("waiting", s1)]);

        var stats = await Ledger.StatsAsync(_flow, Now);
        Assert.Equal(5, stats.Total);
        Assert.Equal(4, stats.Delivered);
        Assert.Equal(1, stats.Pending);
        Assert.Equal(1, stats.Drifted);
        Assert.Equal(2, stats.DeliveredLast24h);
        Assert.Equal(now.AddHours(-1), stats.LastDeliveredUtc);
    }

    [Fact]
    public async Task Work_for_a_record_in_flight_queues_behind_the_delivery_and_its_completion_leaves_it_pending()
    {
        var s1 = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", s1) with { PendingSourceModifiedUtc = Now.AddDays(-2) }]);
        var claimed = ((await Ledger.ClaimAsync(_flow, s1, "w1", 10, TimeSpan.FromMinutes(5), Now)).Records).Single();

        var s2 = Guid.NewGuid();
        var staging = await Ledger.UpsertPendingAsync(_flow, [Pending("a", s2) with { PendingDocumentRef = "7:0:10", WorkBatch = 7, PendingMetadataHash = "mh2", PendingSourceModifiedUtc = Now.AddDays(-1) }]);
        Assert.Equal(1, staging.Staged);
        Assert.Empty(staging.Refused);
        var queued = await Ledger.GetRecordAsync(_flow, claimed.DeliveryKey);
        Assert.Equal(RecordStatus.Delivering, queued!.Status);
        Assert.Equal(claimed.LeaseOwner, queued.LeaseOwner);
        Assert.Equal(s2, queued.LastSubmissionId);
        Assert.Equal("7:0:10", queued.PendingDocumentRef);

        // The in-flight try's step progress belongs to its own document and never reaches the newer work.
        await Ledger.SaveStepAsync(_flow, claimed.DeliveryKey, s1, "0:0:10", "{\"metadata\":{\"version\":\"1\"}}", Now);
        Assert.Null((await Ledger.GetRecordAsync(_flow, claimed.DeliveryKey))!.PendingStepJson);

        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = claimed.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            Claimed = ClaimedWork.Of(claimed),
            Attempt = new AttemptRecord { DeliveryKey = claimed.DeliveryKey, SubmissionId = s1, Worker = "w1", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload" },
        });

        var settled = await Ledger.GetRecordAsync(_flow, claimed.DeliveryKey);
        Assert.Equal(RecordStatus.Pending, settled!.Status);
        Assert.Null(settled.LeaseOwner);
        Assert.Equal("mh", settled.MetadataHash);
        Assert.Equal(Now.AddDays(-2), settled.SourceModifiedUtc);
        Assert.NotNull(settled.LastDeliveredUtc);
        Assert.Equal("mh2", settled.PendingMetadataHash);
        Assert.Equal("7:0:10", settled.PendingDocumentRef);
        Assert.Equal(0, settled.AttemptCount);
        Assert.Equal(1, await Ledger.CountAttemptsAsync(s1, AttemptOutcome.Delivered));
        Assert.Equal(0, await Ledger.CountAttemptsAsync(s2, AttemptOutcome.Delivered));
        Assert.Single((await Ledger.ClaimAsync(_flow, s2, "w2", 10, TimeSpan.FromMinutes(5), Now)).Records);
    }

    [Fact]
    public async Task Work_older_than_what_the_record_holds_is_refused_and_a_stale_skip_is_recorded_without_touching_it()
    {
        var s1 = Guid.NewGuid();
        var delivered = Now.AddDays(-1);
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", s1) with { PendingSourceModifiedUtc = delivered, PendingPayloadModifiedUtc = delivered }]);
        var claimed = ((await Ledger.ClaimAsync(_flow, s1, "w", 10, TimeSpan.FromMinutes(5), Now)).Records).Single();
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = claimed.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            Claimed = ClaimedWork.Of(claimed),
            Attempt = new AttemptRecord { DeliveryKey = claimed.DeliveryKey, SubmissionId = s1, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload" },
        });
        var key = claimed.DeliveryKey;
        Assert.Equal(delivered, (await Ledger.GetRecordAsync(_flow, key))!.PayloadModifiedUtc);

        var s2 = Guid.NewGuid();
        var olderSource = await Ledger.UpsertPendingAsync(_flow, [Pending("a", s2) with { PendingSourceModifiedUtc = delivered.AddHours(-1) }]);
        Assert.Equal(0, olderSource.Staged);
        Assert.Equal(key, Assert.Single(olderSource.Refused));
        var olderPayload = await Ledger.UpsertPendingAsync(_flow, [Pending("a", s2) with { PendingSourceModifiedUtc = delivered.AddHours(1), PendingPayloadModifiedUtc = delivered.AddHours(-1) }]);
        Assert.Equal(key, Assert.Single(olderPayload.Refused));
        var untouched = await Ledger.GetRecordAsync(_flow, key);
        Assert.Equal(RecordStatus.Delivered, untouched!.Status);
        Assert.Equal(s1, untouched.LastSubmissionId);

        // Newer work is staged, and then stands against anything older than itself; work carrying no moment is never refused.
        Assert.Equal(1, (await Ledger.UpsertPendingAsync(_flow, [Pending("a", s2) with { PendingSourceModifiedUtc = delivered.AddHours(2) }])).Staged);
        Assert.Single((await Ledger.UpsertPendingAsync(_flow, [Pending("a", s2) with { PendingSourceModifiedUtc = delivered.AddHours(1) }])).Refused);
        Assert.Equal(1, (await Ledger.UpsertPendingAsync(_flow, [Pending("a", s2)])).Staged);

        await Ledger.MarkSkippedAsync(_flow, [new SkippedRecord { DeliveryKey = key, Kind = SkipKind.Stale, Reason = "the drop carries an older version", SourceModifiedUtc = delivered.AddHours(-1) }], s2);
        var stale = (await Ledger.ListAttemptsAsync(_flow, key, 10)).Single(a => a.Phase == AttemptPhases.Stale);
        Assert.Equal(AttemptOutcome.Skipped, stale.Outcome);
        Assert.Equal(s2, stale.SubmissionId);
        Assert.Null(stale.Error);
        Assert.Contains("older version", stale.ResultJson, StringComparison.Ordinal);
        Assert.Equal(1, await Ledger.CountAttemptsAsync(s2, AttemptOutcome.Skipped, AttemptPhases.Stale));
    }

    [Fact]
    public async Task A_rendered_skip_advances_a_delivered_record_and_leaves_queued_work_with_its_submission()
    {
        var s1 = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", s1) with { PendingSourceModifiedUtc = Now.AddDays(-2) }]);
        var claimed = ((await Ledger.ClaimAsync(_flow, s1, "w", 10, TimeSpan.FromMinutes(5), Now)).Records).Single();
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = claimed.DeliveryKey,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 1,
            Claimed = ClaimedWork.Of(claimed),
            Attempt = new AttemptRecord { DeliveryKey = claimed.DeliveryKey, SubmissionId = s1, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload" },
        });
        await Ledger.UpsertPendingAsync(_flow, [Pending("b", s1) with { PendingSourceModifiedUtc = Now.AddDays(-2) }]);
        var a = claimed.DeliveryKey;
        var b = DeliveryKey.Derive("test", ["b"]);

        var s2 = Guid.NewGuid();
        await Ledger.MarkSkippedAsync(_flow, [
            new SkippedRecord { DeliveryKey = a, Kind = SkipKind.Rendered, Reason = "hashes unchanged", SourceModifiedUtc = Now.AddDays(-1), RenderContext = "{\"cache\":\"2\"}" },
            new SkippedRecord { DeliveryKey = b, Kind = SkipKind.Unchanged, Reason = "unchanged" },
        ], s2);

        var advanced = await Ledger.GetRecordAsync(_flow, a);
        Assert.Equal(s2, advanced!.LastSubmissionId);
        Assert.Equal(Now.AddDays(-1), advanced.SourceModifiedUtc);
        Assert.Equal("{\"cache\":\"2\"}", advanced.RenderContext);
        Assert.Equal(s1, (await Ledger.GetRecordAsync(_flow, b))!.LastSubmissionId);

        // A render identical to the queued work lends that work the newer moment it was found at.
        await Ledger.MarkSkippedAsync(_flow, [new SkippedRecord { DeliveryKey = b, Kind = SkipKind.Rendered, Reason = "equal to the version already queued", SourceModifiedUtc = Now }], s2);
        var queued = await Ledger.GetRecordAsync(_flow, b);
        Assert.Equal(s1, queued!.LastSubmissionId);
        Assert.Equal(Now, queued.PendingSourceModifiedUtc);
        Assert.Equal(RecordStatus.Pending, queued.Status);
    }

    [Fact]
    public async Task A_changed_row_that_renders_what_OSDU_holds_is_recorded_once_and_the_origin_moves_with_it()
    {
        var arrived = Now.AddDays(-3);
        var loaded = Now.AddDays(-2);
        var changed = Now.AddDays(-1);
        var s1 = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", s1) with
        {
            PendingSourceFileName = "first.csv", PendingSourceRowNumber = 1, PendingSourceUpdatedUtc = loaded, SourceInsertedUtc = arrived,
        }]);
        var claimed = ((await Ledger.ClaimAsync(_flow, s1, "w", 10, TimeSpan.FromMinutes(5), Now)).Records).Single();
        await Ledger.CompleteAsync(_flow, Delivered(claimed, s1));
        await Ledger.UpsertPendingAsync(_flow, [Pending("b", s1) with
        {
            PendingSourceFileName = "first.csv", PendingSourceRowNumber = 2, PendingSourceUpdatedUtc = loaded, SourceInsertedUtc = arrived,
        }]);
        var a = claimed.DeliveryKey;
        var b = DeliveryKey.Derive("test", ["b"]);
        var delivered = await Ledger.GetRecordAsync(_flow, a);
        Assert.Equal(("first.csv", 1L, loaded, arrived), (delivered!.SourceFileName, delivered.SourceRowNumber, delivered.SourceUpdatedUtc, delivered.SourceInsertedUtc));

        // The ingestion table changed both rows from a second file, and each renders the document OSDU holds or the one
        // already queued: nothing is sent, and each change is recorded against its record with the row's new origin.
        SkippedRecord Identical(DeliveryKey key, long row, DateTime stamp) => new()
        {
            DeliveryKey = key,
            Kind = SkipKind.Rendered,
            Reason = "metadata and payload hashes unchanged",
            SourceFingerprint = "fp-2",
            Origin = new RecordOrigin("second.csv", row, stamp),
            SourceInsertedUtc = arrived,
        };

        var s2 = Guid.NewGuid();
        await Ledger.MarkSkippedAsync(_flow, [Identical(a, 7, changed), Identical(b, 8, changed)], s2);
        foreach (var (key, row) in new[] { (a, 7L), (b, 8L) })
        {
            var identical = Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10), x => x.Phase == AttemptPhases.Identical);
            Assert.Equal((AttemptOutcome.Skipped, "intake", s2), (identical.Outcome, identical.Worker, identical.SubmissionId));
            Assert.Equal(("second.csv", row, changed), (identical.SourceFileName, identical.SourceRowNumber, identical.SourceUpdatedUtc));
            Assert.Contains("hashes unchanged", identical.ResultJson, StringComparison.Ordinal);
        }

        // The delivered record's origin moves with its fingerprint; the queued work's lands with that work.
        var moved = await Ledger.GetRecordAsync(_flow, a);
        Assert.Equal(("second.csv", 7L, changed, "fp-2"), (moved!.SourceFileName, moved.SourceRowNumber, moved.SourceUpdatedUtc, moved.SourceFingerprint));
        var queued = await Ledger.GetRecordAsync(_flow, b);
        Assert.Equal(("second.csv", 8L, changed), (queued!.PendingSourceFileName, queued.PendingSourceRowNumber, queued.PendingSourceUpdatedUtc));
        Assert.Equal(RecordStatus.Pending, queued.Status);

        // The same rows rendered again (a cache or mapping rollout), or re-read from under the watermark: no change, so no
        // attempt; the origin stays where the change put it.
        await Ledger.MarkSkippedAsync(_flow, [Identical(a, 7, changed), Identical(b, 8, changed)], Guid.NewGuid());
        await Ledger.MarkSkippedAsync(_flow, [Identical(a, 1, loaded), Identical(b, 2, loaded)], Guid.NewGuid());
        Assert.Single(await Ledger.ListAttemptsAsync(_flow, a, 10), x => x.Phase == AttemptPhases.Identical);
        Assert.Single(await Ledger.ListAttemptsAsync(_flow, b, 10), x => x.Phase == AttemptPhases.Identical);
        var kept = await Ledger.GetRecordAsync(_flow, a);
        Assert.Equal(("second.csv", 7L, changed), (kept!.SourceFileName, kept.SourceRowNumber, kept.SourceUpdatedUtc));

        // The versions the record's history holds: each once, newest first, however many attempts recorded it.
        var versions = await Ledger.ListOriginsAsync(_flow, b, 10);
        var only = Assert.Single(versions);
        Assert.Equal((new RecordOrigin("second.csv", 8, changed), (DateTime?)null), (only.Origin, only.DeletedUtc));

        // A read of a table without the insert column leaves the arrival the ledger knows.
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", Guid.NewGuid()) with { PendingSourceUpdatedUtc = Now }]);
        Assert.Equal(arrived, (await Ledger.GetRecordAsync(_flow, a))!.SourceInsertedUtc);
    }

    [Fact]
    public async Task A_record_held_because_its_row_was_deleted_dates_the_deletion_on_its_hold()
    {
        var deleted = Now.AddHours(-1);
        await Ledger.MarkHeldAsync(_flow, [
            Pending("a", Guid.NewGuid()) with { PendingSourceUpdatedUtc = Now.AddDays(-1), PendingSourceDeletedUtc = deleted, SourceInsertedUtc = Now.AddDays(-2), LastError = "the ingestion table marked the record row deleted" },
            Pending("b", Guid.NewGuid()) with { LastError = "no Wellbore matches" },
        ]);

        var gone = Assert.Single(await Ledger.ListAttemptsAsync(_flow, DeliveryKey.Derive("test", ["a"]), 10));
        Assert.Equal((AttemptOutcome.Held, AttemptPhases.SourceDeleted, deleted), (gone.Outcome, gone.Phase, gone.SourceDeletedUtc));
        Assert.Equal(Now.AddDays(-2), (await Ledger.GetRecordAsync(_flow, DeliveryKey.Derive("test", ["a"])))!.SourceInsertedUtc);
        var other = Assert.Single(await Ledger.ListAttemptsAsync(_flow, DeliveryKey.Derive("test", ["b"]), 10));
        Assert.Equal(("render", (DateTime?)null), (other.Phase, other.SourceDeletedUtc));

        // The deletion is one of the versions the record's history holds, with the row's last stamp beside it.
        var version = Assert.Single(await Ledger.ListOriginsAsync(_flow, DeliveryKey.Derive("test", ["a"]), 10));
        Assert.Equal(((DateTime?)deleted, (DateTime?)Now.AddDays(-1)), (version.DeletedUtc, version.Origin.UpdatedUtc));
    }

    [Fact]
    public async Task Two_flows_reading_the_same_row_keep_separate_records_and_histories()
    {
        // One ingestion row, two flows: the well log flow writes a WellLog, the other flow a Wellbore. The delivery key is
        // the same for both, and each flow's record is its own.
        var other = FlowId.Of("test-flow-wellbores");
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", s1)]);
        await Ledger.UpsertPendingAsync(other, [Pending("a", s2) with { FlowId = other, MappingName = "Wellbore", TargetId = "dev:y:a", PendingMetadataHash = "mh-b" }]);
        var key = DeliveryKey.Derive("test", ["a"]);

        var mine = await Ledger.GetRecordAsync(_flow, key);
        var theirs = await Ledger.GetRecordAsync(other, key);
        Assert.Equal(("dev:x:a", "Thing", (Guid?)s1), (mine!.TargetId, mine.MappingName, mine.LastSubmissionId));
        Assert.Equal(("dev:y:a", "Wellbore", (Guid?)s2), (theirs!.TargetId, theirs.MappingName, theirs.LastSubmissionId));
        Assert.Equal("dev:x:a", mine.ClaimedTargetId);
        Assert.Equal("dev:y:a", theirs.ClaimedTargetId);

        // A claim, a lease and a completion in one flow leave the other flow's record exactly as it was.
        var claimed = Assert.Single((await Ledger.ClaimAsync(_flow, null, "w1", 10, TimeSpan.FromMinutes(5), Now)).Records);
        Assert.Equal(_flow, claimed.FlowId);
        Assert.True(await Ledger.RenewLeaseAsync(claimed.LeaseOwner!, TimeSpan.FromMinutes(5), Now));
        Assert.Null((await Ledger.GetRecordAsync(other, key))!.LeaseOwner);
        await Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = key,
            Status = RecordStatus.Delivered,
            Promote = true,
            TargetVersion = 3,
            TargetId = claimed.TargetId,
            Claimed = ClaimedWork.Of(claimed),
            Attempt = new AttemptRecord { DeliveryKey = key, SubmissionId = s1, Worker = "w1", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata+payload" },
        });

        Assert.Equal(RecordStatus.Delivered, (await Ledger.GetRecordAsync(_flow, key))!.Status);
        var untouched = await Ledger.GetRecordAsync(other, key);
        Assert.Equal(RecordStatus.Pending, untouched!.Status);
        Assert.Null(untouched.MetadataHash);
        Assert.Equal("mh-b", untouched.PendingMetadataHash);

        // Each record's history is its own flow's attempts.
        Assert.Equal(s1, Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10)).SubmissionId);
        Assert.Empty(await Ledger.ListAttemptsAsync(other, key, 10));
        await Ledger.MarkRemovedAsync(other, [key], RemovalScope.Record, "gui:tahir", Now);
        Assert.Equal("delete", Assert.Single(await Ledger.ListAttemptsAsync(other, key, 10)).Phase);
        Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10));
        Assert.Equal(RecordStatus.Delivered, (await Ledger.GetRecordAsync(_flow, key))!.Status);
        Assert.Equal(RecordStatus.Deleted, (await Ledger.GetRecordAsync(other, key))!.Status);

        // Interventions, verify outcomes and statistics stay inside their flow: the other flow's only request is its removal's.
        var removalRequest = Assert.Single(await Ledger.ListPlanRequestedAsync(other, null, 10));
        Assert.Equal(1, await Ledger.ForceRedeliverAsync(_flow, [key], RedeliverScope.All, Now));
        Assert.Equal(removalRequest, Assert.Single(await Ledger.ListPlanRequestedAsync(other, null, 10)));
        await Ledger.RecordVerifyAsync(_flow, key, VerifyOutcome.Missing, null, Now, requeue: false);
        Assert.Null((await Ledger.GetRecordAsync(other, key))!.LastVerifyOutcome);
        Assert.Equal(1, (await Ledger.StatsAsync(_flow, Now)).Delivered);
        Assert.Equal(1, (await Ledger.StatsAsync(other, Now)).Deleted);
        Assert.Equal(key, Assert.Single(await Ledger.ListAsync(other, new RecordQuery())).DeliveryKey);

        // Pruning keeps the latest attempt of each flow's record, not one attempt per key.
        _clock.Advance(TimeSpan.FromDays(60));
        Assert.Equal(0, await Ledger.PruneAttemptsAsync(Now));
        Assert.Single(await Ledger.ListAttemptsAsync(_flow, key, 10));
        Assert.Single(await Ledger.ListAttemptsAsync(other, key, 10));
    }

    [Fact]
    public async Task Pruning_works_through_the_history_in_bounded_statements_and_keeps_each_record_s_last_attempt()
    {
        var submission = Guid.NewGuid();
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", submission), Pending("b", submission)]);
        foreach (var name in new[] { "a", "b" })
        {
            var key = DeliveryKey.Derive("test", [name]);
            for (var i = 0; i < 3; i++)
            {
                await Ledger.CompleteAsync(_flow, new RecordCompletion
                {
                    DeliveryKey = key,
                    Status = RecordStatus.Pending,
                    Attempt = new AttemptRecord { DeliveryKey = key, Worker = "w", StartedUtc = Now.AddDays(-90 + i), CompletedUtc = Now, Outcome = AttemptOutcome.Failed, Phase = "none" },
                });
            }
        }

        // Two at a time, four old attempts go in three statements; each record keeps its latest, however old it is.
        var bounded = new OsduLedger(_db.CreateDbContext, _clock) { PruneBatch = 2 };
        // The Maintenance page's count is the prune's own test: it says exactly what the prune then takes.
        Assert.Equal(4, await bounded.CountPrunableAttemptsAsync(Now));
        Assert.Equal(4, await bounded.PruneAttemptsAsync(Now));
        Assert.Equal(0, await bounded.CountPrunableAttemptsAsync(Now));
        foreach (var name in new[] { "a", "b" })
        {
            var kept = Assert.Single(await Ledger.ListAttemptsAsync(_flow, DeliveryKey.Derive("test", [name]), 10));
            Assert.Equal(Now.AddDays(-88), kept.StartedUtc);
        }

        Assert.Equal(0, await bounded.PruneAttemptsAsync(Now));
    }

    [Fact]
    public async Task A_flow_writes_only_its_own_records()
    {
        var other = FlowId.Of("test-flow-wellbores");
        var submission = Guid.NewGuid();
        var foreign = Pending("a", submission) with { FlowId = other };
        var staged = await Assert.ThrowsAsync<ArgumentException>(() => Ledger.UpsertPendingAsync(_flow, [foreign]));
        Assert.Contains("a flow writes only its own records", staged.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => Ledger.MarkHeldAsync(_flow, [foreign]));

        // A completion names its record twice; the two must agree, or the attempt would land in another record's history.
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", submission), Pending("b", submission)]);
        var a = DeliveryKey.Derive("test", ["a"]);
        var b = DeliveryKey.Derive("test", ["b"]);
        await Assert.ThrowsAsync<ArgumentException>(() => Ledger.CompleteAsync(_flow, new RecordCompletion
        {
            DeliveryKey = a,
            Status = RecordStatus.Delivered,
            Attempt = new AttemptRecord { DeliveryKey = b, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata" },
        }));

        // A record another flow never staged is not in that flow's ledger.
        var missing = await Assert.ThrowsAsync<DeliveryException>(() => Ledger.CompleteAsync(other, new RecordCompletion
        {
            DeliveryKey = a,
            Status = RecordStatus.Delivered,
            Attempt = new AttemptRecord { DeliveryKey = a, Worker = "w", StartedUtc = Now, CompletedUtc = Now, Outcome = AttemptOutcome.Delivered, Phase = "metadata" },
        }));
        Assert.Contains(other.ToString("D"), missing.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DeliveryException>(() => Ledger.MarkRemovedAsync(other, [a], RemovalScope.Record, "gui:tahir", Now));
        Assert.Empty(await Ledger.ListAttemptsAsync(_flow, a, 10));
    }

    [Fact]
    public async Task An_osdu_id_belongs_to_the_flow_that_first_queued_a_document_for_it()
    {
        var other = FlowId.Of("test-flow-copy");
        var s1 = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(s1));
        await Ledger.UpsertPendingAsync(_flow, [Pending("a", s1)]);

        // A second flow rendering the same OSDU id is refused and told whose it is; nothing is written for it.
        var s2 = Guid.NewGuid();
        var refused = await Ledger.UpsertPendingAsync(other, [Pending("a", s2) with { FlowId = other }]);
        Assert.Equal(0, refused.Staged);
        var conflict = Assert.Single(refused.Conflicts);
        Assert.Equal((DeliveryKey.Derive("test", ["a"]), "dev:x:a", _flow, "test-flow"), (conflict.DeliveryKey, conflict.TargetId, conflict.OwnerFlowId, conflict.OwnerFlowName));
        Assert.Contains("already claimed by flow 'test-flow'", conflict.Describe(), StringComparison.Ordinal);
        Assert.Null(await Ledger.GetRecordAsync(other, conflict.DeliveryKey));

        // Held with the conflict as its reason, the other flow's record carries no id: nothing it does reaches the owner's record.
        await Ledger.MarkHeldAsync(other, [Pending("a", s2) with { FlowId = other, LastError = conflict.Describe() }]);
        var held = await Ledger.GetRecordAsync(other, conflict.DeliveryKey);
        Assert.Equal(RecordStatus.Held, held!.Status);
        Assert.Null(held.TargetId);
        Assert.Null(held.ClaimedTargetId);

        // The owner restages its own id freely, and an id differing only by case is another OSDU record.
        Assert.Equal(1, (await Ledger.UpsertPendingAsync(_flow, [Pending("a", s1)])).Staged);
        var cased = await Ledger.UpsertPendingAsync(other, [Pending("b", s2) with { FlowId = other, TargetId = "dev:x:A" }]);
        Assert.Equal((1, 0), (cased.Staged, cased.Conflicts.Count));

        // A record that was only ever held claims nothing, so the id it names can still be claimed by the flow that
        // queues a document for it first; the held record then meets the conflict when its own work is staged.
        await Ledger.MarkHeldAsync(other, [Pending("c", s2) with { FlowId = other, LastError = "no wellbore" }]);
        Assert.Null((await Ledger.GetRecordAsync(other, DeliveryKey.Derive("test", ["c"])))!.ClaimedTargetId);
        Assert.Equal(1, (await Ledger.UpsertPendingAsync(_flow, [Pending("c", s1)])).Staged);
        var late = await Ledger.UpsertPendingAsync(other, [Pending("c", s2) with { FlowId = other }]);
        Assert.Equal(_flow, Assert.Single(late.Conflicts).OwnerFlowId);
        Assert.Equal("dev:x:c", (await Ledger.GetRecordAsync(_flow, DeliveryKey.Derive("test", ["c"])))!.ClaimedTargetId);
    }

    [Fact]
    public async Task A_refused_claim_names_the_owner_s_ledger_and_whether_a_repository_still_declares_it()
    {
        // The owner keeps the ledger of a partition of its own: welldb-welllog in test, named welldb-welllog@test.
        var owner = FlowId.Of("welldb-welllog", "test");
        await Ledger.RegisterAsync(owner, "test", "welldb-welllog", "welldb-welllog@test");
        var s1 = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(Submission(s1) with { FlowId = owner, FlowName = "welldb-welllog" });
        await Ledger.UpsertPendingAsync(owner, [Pending("a", s1) with { FlowId = owner }]);
        var other = FlowId.Of("test-flow-copy");

        // No repository declares a pipeline that keeps it, as after a rename: the refusal names the ledger as a flow writes it
        // in ledger:, without the partition the binding adds again.
        var gone = Assert.Single((await Ledger.UpsertPendingAsync(other, [Pending("a", Guid.NewGuid()) with { FlowId = other }])).Conflicts);
        Assert.Equal((owner, "welldb-welllog", "welldb-welllog", false), (gone.OwnerFlowId, gone.OwnerFlowName, gone.OwnerLedger, gone.OwnerDeclared));
        Assert.Contains($"already claimed by flow 'welldb-welllog' ({owner:D}), which no repository declares any more", gone.Describe(), StringComparison.Ordinal);
        Assert.Contains("with ledger: welldb-welllog in its document", gone.Describe(), StringComparison.Ordinal);

        // A pipeline a repository declares keeps it: two flows want one id, and the refusal says only how to part them.
        await using (var context = _db.CreateDbContext())
        {
            context.DeliveryInterfaces.Add(new Data.DeliveryInterface
            {
                Id = Guid.NewGuid(),
                RepoId = Guid.NewGuid(),
                FlowName = "welldb-welllog",
                Partition = "test",
                LedgerFlowId = owner,
                LedgerName = "welldb-welllog@test",
                Route = "storage",
                MappingReference = "Thing@1.0.0",
                RecordObject = "OsduData.arc.WellLog",
                RelativePath = "flows/welldb-welllog.yaml",
                Active = true,
                FirstSeenUtc = Now,
                LastSeenUtc = Now,
            });
            await context.SaveChangesAsync();
        }

        var live = Assert.Single((await Ledger.UpsertPendingAsync(other, [Pending("a", Guid.NewGuid()) with { FlowId = other }])).Conflicts);
        Assert.True(live.OwnerDeclared);
        Assert.Equal(
            $"OSDU id dev:x:a is already claimed by flow 'welldb-welllog' ({owner:D}), and one OSDU record belongs to one flow. Deliver this flow to another data partition, or give its mapping a dataset.system or key that yields other OSDU ids.",
            live.Describe());
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}

