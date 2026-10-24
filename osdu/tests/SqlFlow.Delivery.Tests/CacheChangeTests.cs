using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What happens to delivered records when the cache moves under them: the dependency set a render leaves behind,
/// the changes a new cache version raises against it, the approval gate, and the batched rollout that carries an
/// approved change out without taking the estate with it.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CacheChangeTests : IAsyncLifetime, IDisposable
{
    /// <summary>The partition whose cache the records under test were built from.</summary>
    private const string Scope = "dev";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("test-flow");
    private readonly OsduLedger _ledger;

    public CacheChangeTests() => _ledger = _db.Ledger(_clock);

    private OsduLedger Ledger => _ledger;

    /// <summary>Registers the ledgers the tests write to directly, as a run registers its own before it writes a row of it.</summary>
    public Task InitializeAsync() => Ledger.RegisterAsync(_flow, FlowId.Of("test-flow-wellbores"));

    public Task DisposeAsync() => Task.CompletedTask;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public void Dispose() => _db.Dispose();

    private static ReferenceType Units(string metreName, params string[] extraAliases) => new(
        "UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            new ReferenceItem("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = ReferenceValue.Of("m"),
                ["Name"] = ReferenceValue.Of(metreName),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse($"""["metre"{(extraAliases.Length > 0 ? ", " + string.Join(", ", extraAliases.Select(a => $"\"{a}\"")) : string.Empty)}]""")!),
            }),
        ]);

    private static CacheUsage Reads(string path, string value, CacheUsageKind kind = CacheUsageKind.Value)
        => new("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", path, value, kind);

    /// <summary>Delivers <paramref name="count"/> records that all read the same cached values.</summary>
    private Task<IReadOnlyList<DeliveryKey>> DeliveredAsync(int count, params CacheUsage[] usages) => DeliveredAsync("WELL", count, usages);

    /// <summary>Delivers <paramref name="count"/> records keyed under <paramref name="prefix"/> that all read the same cached values.</summary>
    private Task<IReadOnlyList<DeliveryKey>> DeliveredAsync(string prefix, int count, params CacheUsage[] usages) => DeliveredAsync(Scope, prefix, count, usages);

    /// <summary>Delivers <paramref name="count"/> records keyed under <paramref name="prefix"/> that all read the same values of the cache of partition <paramref name="scope"/>.</summary>
    private Task<IReadOnlyList<DeliveryKey>> DeliveredAsync(string scope, string prefix, int count, params CacheUsage[] usages)
        => DeliveredAsync(_flow, "test-flow", "x", scope, prefix, count, usages);

    /// <summary>
    /// Delivers <paramref name="count"/> records of the flow <paramref name="flowName"/>, keyed under <paramref name="prefix"/>
    /// and written to OSDU ids of the entity type <paramref name="entityType"/>, that all read the same cached values.
    /// </summary>
    private async Task<IReadOnlyList<DeliveryKey>> DeliveredAsync(
        Guid flow, string flowName, string entityType, string scope, string prefix, int count, params CacheUsage[] usages)
    {
        var submission = Guid.NewGuid();
        await Ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission,
            FlowId = flow,
            FlowName = flowName,
            MappingReference = "Thing@1.0.0",
            RenderContext = "{}",
            RecordCount = count,
            Status = SubmissionStatus.Planned,
            ReceivedUtc = Now,
        });

        var setId = usages.Length == 0 ? (long?)null : await Ledger.EnsureCacheSetAsync(scope, usages);
        var keys = new List<DeliveryKey>();
        var records = new List<RecordState>();
        for (var i = 0; i < count; i++)
        {
            var key = DeliveryKey.Derive("test", [$"{prefix}-{i}"]);
            keys.Add(key);
            records.Add(new RecordState
            {
                DeliveryKey = key,
                FlowId = flow,
                SourceKey = $"{prefix}-{i}",
                MappingName = "Thing",
                TargetId = $"dev:{entityType}:{prefix}-{i}",
                LastSubmissionId = submission,
                PendingDocumentRef = "0:0:10",
                PendingRenderContext = "{}",
                PendingMetadataHash = "mh",
                PendingMetadata = true,
                CacheSetId = setId,
            });
        }

        await Ledger.UpsertPendingAsync(flow, records);
        return keys;
    }

    private Task<CacheImpactResult> AnalyzeAsync(ReferenceType? previous, ReferenceType current, CacheChangeMode mode, string scope = Scope)
        => new CacheImpactAnalyzer(Ledger, _clock, NullLogger.Instance).AnalyzeAsync(scope, previous, current, mode, "v1", "v2");

    [Fact]
    public async Task A_change_to_one_partition_s_cache_never_reaches_records_built_from_another_partition_s_cache()
    {
        // Every partition's cache can hold a UnitOfMeasure type; a set is a set of one partition's cached values, and a
        // refresh of another partition's cache finds nothing built from it.
        await DeliveredAsync("other-partition", "OTHER", 2, Reads("Name", "metre"));
        var elsewhere = await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        Assert.Equal(0, elsewhere.AffectedRecords);
        Assert.Empty(await Ledger.GatedCacheSetsAsync());

        var there = await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve, scope: "other-partition");
        Assert.Equal(2, there.AffectedRecords);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("other-partition", tag.Scope);

        // The cache page lists one partition's changes: the other partition has none to show or count.
        Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0, "other-partition"));
        Assert.Equal(1, await Ledger.CountTagsAsync("pending", "other-partition"));
        Assert.Empty(await Ledger.ListTagsAsync("pending", 10, 0, Scope));
        Assert.Equal(0, await Ledger.CountTagsAsync(null, Scope));
    }

    [Fact]
    public void A_render_records_what_it_read_out_of_the_cache()
    {
        var mapping = TestSchema.Mapping("""
              Unit:
                $cache: UnitOfMeasure.id
                $findBy: Code = unit
              Symbol:
                $cache: UnitOfMeasure.Name
                $findBy: Code = unit
            """);
        var renderer = new MappingRenderer(mapping, TestSchema.Build(), new ReferenceSnapshot("refs-1", DateTimeOffset.UnixEpoch, [Units("metre")]), TestSchema.Context());

        var result = renderer.Render(new SourceRecord
        {
            Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "well-1", ["depth"] = "1", ["unit"] = "m" }),
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
        });

        Assert.False(result.IsHeld);

        // What it matched by, and what it took: both are dependencies, for different reasons.
        Assert.Contains(result.CacheUsages, u => u.Kind == CacheUsageKind.Match && u.Path == "Code" && u.Value == "m");
        Assert.Contains(result.CacheUsages, u => u.Kind == CacheUsageKind.Value && u.Path == "id");
        Assert.Contains(result.CacheUsages, u => u.Kind == CacheUsageKind.Value && u.Path == "Name" && u.Value == "metre");
    }

    [Fact]
    public async Task Records_reading_the_same_values_share_one_dependency_set()
    {
        var keys = await DeliveredAsync(3, Reads("Name", "metre"));
        var first = await Ledger.GetRecordAsync(_flow, keys[0]);
        var last = await Ledger.GetRecordAsync(_flow, keys[^1]);

        Assert.NotNull(first!.CacheSetId);
        Assert.Equal(first.CacheSetId, last!.CacheSetId);

        // The same values from a second run resolve to the same set, so the trail never grows with the estate.
        var again = await Ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "metre")]);
        Assert.Equal(first.CacheSetId, again);

        var entries = await Ledger.ListCacheSetAsync(again);
        var entry = Assert.Single(entries);
        Assert.Equal("Name", entry.Path);
        Assert.Equal("metre", entry.ValueText);
    }

    [Fact]
    public async Task Runs_that_mint_the_same_set_at_once_each_get_that_set_with_every_value()
    {
        // Seen in the parts of a split delivery, which plan at the same moment and render the same cached values: two
        // inserted the same set, and the one the set's unique hash refused failed its run.
        for (var round = 0; round < 5; round++)
        {
            CacheUsage[] usages = [Reads("Name", $"metre-{round}"), Reads("Code", "m")];
            var ledgers = Enumerable.Range(0, 8).Select(_ => _db.Ledger(_clock)).ToList();
            using var start = new SemaphoreSlim(0);
            var minting = ledgers
                .Select(ledger => Task.Run(async () =>
                {
                    await start.WaitAsync();
                    return await ledger.EnsureCacheSetAsync(Scope, usages);
                }))
                .ToList();
            start.Release(ledgers.Count);
            var ids = await Task.WhenAll(minting);

            var id = Assert.Single(ids.Distinct());
            var entries = await Ledger.ListCacheSetAsync(id);
            Assert.Equal(new[] { "Code", "Name" }, entries.Select(e => e.Path).Order(StringComparer.Ordinal).ToArray());
        }
    }

    [Fact]
    public async Task A_changed_value_raises_one_change_covering_every_record_built_from_it()
    {
        await DeliveredAsync(5, Reads("Name", "metre"));

        var impact = await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        Assert.Equal(1, impact.ChangedItems);
        Assert.Equal(1, impact.Changes);
        Assert.Equal(5, impact.AffectedRecords);

        // One decision, not five: the tag names the value that moved and how far it reaches.
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("changed", tag.Change);
        Assert.Equal("metre", tag.OldValue);
        Assert.Equal("meter", tag.NewValue);
        Assert.Equal(5, tag.AffectedRecords);
        Assert.Contains("Name changed from 'metre' to 'meter'", tag.Describe(), StringComparison.Ordinal);
        Assert.True(tag.WaitsForApproval);

        // The gate is on the set, so holding five million records back would cost the same as holding five.
        Assert.Equal(tag.SetIds, await Ledger.GatedCacheSetsAsync());
    }

    [Fact]
    public async Task A_change_names_the_value_the_replaced_version_held_not_one_a_set_left_from_an_earlier_render_holds()
    {
        // Seen live: a set built before the last change still holds the value before last, and the tag named that one.
        await Ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "metre")]);
        await DeliveredAsync(2, Reads("Name", "meter"));

        var impact = await AnalyzeAsync(Units("meter"), Units("metres"), CacheChangeMode.Approve);
        Assert.Equal(1, impact.Changes);
        Assert.Equal(2, impact.AffectedRecords);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("meter", tag.OldValue);
        Assert.Contains("Name changed from 'meter' to 'metres'", tag.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_set_is_judged_by_the_value_it_holds_so_one_already_holding_the_new_value_hides_no_other()
    {
        // Records rendered against two versions of the cache: one set already holds the new value, the other still holds
        // the old one. Only the second is touched, and it is not missed because the lookup lists the first ahead of it.
        await DeliveredAsync("NEWER", 3, Reads("Name", "meter"));
        var older = await DeliveredAsync("OLDER", 2, Reads("Name", "metre"));
        var olderSet = (await Ledger.GetRecordAsync(_flow, older[0]))!.CacheSetId!.Value;

        var impact = await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        Assert.Equal(1, impact.Changes);
        Assert.Equal(2, impact.AffectedRecords);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("metre", tag.OldValue);
        Assert.Equal(2, tag.AffectedRecords);
        Assert.Equal(olderSet, Assert.Single(tag.SetIds));
        Assert.Equal(olderSet, Assert.Single(await Ledger.GatedCacheSetsAsync()));
    }

    [Fact]
    public async Task A_value_no_record_read_changes_nothing()
    {
        await DeliveredAsync(2, Reads("id", "dev:reference-data--UnitOfMeasure:m"));

        var impact = await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        Assert.Equal(1, impact.ChangedItems);
        Assert.Equal(0, impact.Changes);
        Assert.Empty(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Empty(await Ledger.GatedCacheSetsAsync());
    }

    [Fact]
    public async Task A_matched_value_that_survives_under_another_alias_is_not_tagged()
    {
        await DeliveredAsync(1, Reads("Alias", "metre", CacheUsageKind.Match));

        var impact = await AnalyzeAsync(Units("metre"), Units("metre", "meter"), CacheChangeMode.Approve);
        Assert.Equal(0, impact.Changes);
    }

    [Fact]
    public async Task A_match_that_no_longer_resolves_is_tagged_unmatched()
    {
        await DeliveredAsync(1, Reads("Alias", "metre", CacheUsageKind.Match));

        var current = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            new ReferenceItem("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = ReferenceValue.Of("m"),
                ["Name"] = ReferenceValue.Of("metre"),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse("""["meter"]""")!),
            }),
        ]);

        await AnalyzeAsync(Units("metre"), current, CacheChangeMode.Approve);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("unmatched", tag.Change);
        Assert.Contains("no longer matches", tag.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_record_a_delivered_record_references_unverified_is_tagged_found_once_the_cache_holds_it()
    {
        // The records wrote the reference to psia without the cache holding it ($unverified), and read the metre as well.
        const string Psia = "dev:reference-data--UnitOfMeasure:psia";
        await DeliveredAsync(2,
            new CacheUsage("UnitOfMeasure", Psia, "id", Psia + ":", CacheUsageKind.Unverified),
            Reads("Name", "metre"));

        // A capture that brings in another unit, and leaves the metre as it was, reaches none of them.
        await AnalyzeAsync(Units("metre"), With(Units("metre"), "dev:reference-data--UnitOfMeasure:bar"), CacheChangeMode.Approve);
        Assert.Empty(await Ledger.ListTagsAsync("pending", 10, 0));

        // One that brings in psia verifies the reference they carry, and the records are built again against it.
        var result = await AnalyzeAsync(Units("metre"), With(Units("metre"), Psia), CacheChangeMode.Approve);
        Assert.Equal(1, result.Changes);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("found", tag.Change);
        Assert.Equal(Psia, tag.ItemId);
        Assert.Equal(Psia + ":", tag.OldValue);
        Assert.Equal(2, tag.AffectedRecords);
        Assert.Contains("which delivered records reference as an unverified id", tag.Describe(), StringComparison.Ordinal);

        static ReferenceType With(ReferenceType units, string id) => new(units.Name, units.EntityType, units.Items.Append(
            new ReferenceItem(id, new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { ["Code"] = ReferenceValue.Of(id.Split(':')[2]) })));
    }

    [Fact]
    public async Task A_cached_record_that_disappears_is_tagged_removed()
    {
        await DeliveredAsync(1, Reads("Name", "metre"));

        await AnalyzeAsync(Units("metre"), new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", []), CacheChangeMode.Approve);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("removed", tag.Change);
        Assert.Null(tag.NewValue);
    }

    [Fact]
    public async Task Auto_approves_as_it_is_written_and_gates_nothing()
    {
        await DeliveredAsync(2, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Auto);

        var tag = Assert.Single(await Ledger.ListTagsAsync("approved", 10, 0));
        Assert.Equal("auto", tag.Mode);
        Assert.Equal("system:auto", tag.DecidedBy);
        Assert.Empty(await Ledger.GatedCacheSetsAsync());
    }

    [Fact]
    public async Task Approving_releases_the_gate_and_rejecting_leaves_osdu_alone()
    {
        await DeliveredAsync(2, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));

        Assert.Equal(1, await Ledger.DecideTagsAsync([tag.TagId], approve: true, "tahir", Now));
        Assert.Equal("tahir", Assert.Single(await Ledger.ListTagsAsync("approved", 10, 0)).DecidedBy);
        Assert.Empty(await Ledger.GatedCacheSetsAsync());

        // A second change, rejected this time: the gate opens again and OSDU keeps what it has.
        await AnalyzeAsync(Units("meter"), Units("metres"), CacheChangeMode.Approve);
        var second = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal(1, await Ledger.DecideTagsAsync([second.TagId], approve: false, "tahir", Now));
        Assert.Single(await Ledger.ListTagsAsync("rejected", 10, 0));
        Assert.Empty(await Ledger.GatedCacheSetsAsync());
    }

    [Fact]
    public async Task A_value_that_moves_again_after_approval_reopens_the_question()
    {
        await DeliveredAsync(1, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        await Ledger.DecideTagsAsync([tag.TagId], approve: true, "tahir", Now);

        await AnalyzeAsync(Units("metre"), Units("metres"), CacheChangeMode.Approve);
        var reopened = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal(tag.TagId, reopened.TagId);
        Assert.Equal("metres", reopened.NewValue);
        Assert.Null(reopened.DecidedBy);
        Assert.NotEmpty(await Ledger.GatedCacheSetsAsync());
    }

    [Fact]
    public async Task An_approved_change_rolls_out_in_batches_and_resumes_where_it_stopped()
    {
        var keys = await DeliveredAsync(5, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Auto);
        var tag = Assert.Single(await Ledger.ListRolloutQueueAsync(10));
        Assert.Equal(5, tag.AffectedRecords);

        // Two at a time: the pass is bounded, and what it marked is metadata only.
        var first = await Ledger.RollOutTagAsync(tag.TagId, batchSize: 2, Now);
        Assert.Equal(2, first.Marked);
        Assert.False(first.Completed);
        Assert.Equal("rolling", Assert.Single(await Ledger.ListTagsAsync("rolling", 10, 0)).Status);

        var second = await Ledger.RollOutTagAsync(tag.TagId, batchSize: 2, Now);
        Assert.Equal(2, second.Marked);
        Assert.Equal(4, second.Processed);

        var third = await Ledger.RollOutTagAsync(tag.TagId, batchSize: 2, Now);
        Assert.Equal(1, third.Marked);
        Assert.False(third.Completed);
        Assert.Equal(5, third.Processed);

        // Every record is now due for a metadata redelivery, and no payload was touched.
        foreach (var key in keys)
        {
            var state = await Ledger.GetRecordAsync(_flow, key);
            Assert.Null(state!.MetadataHash);
            Assert.Contains("cache change", state.LastError!, StringComparison.Ordinal);
        }

        // Marked is not delivered: the change waits, still rolling out, for the flow to render its records again.
        var waiting = Assert.Single(await Ledger.ListTagsAsync("rolling", 10, 0));
        Assert.Equal("delivering", waiting.Status);
        Assert.Equal(5, waiting.Waiting);
        Assert.Equal(5, waiting.WaitingByFlow[_flow]);

        // Nothing is left to mark, and a further pass is a no-op rather than a rescan.
        Assert.Empty(await Ledger.ListRolloutQueueAsync(10));
        Assert.Equal(0, (await Ledger.RollOutTagAsync(tag.TagId, batchSize: 2, Now)).Marked);
        Assert.Equal(0, await Ledger.SettleRolloutsAsync(Now));

        // The flow runs: its records are built from the new value, and the change is rolled out.
        await DeliveredAsync(5, Reads("Name", "meter"));
        Assert.Equal(1, await Ledger.SettleRolloutsAsync(Now));
        var applied = Assert.Single(await Ledger.ListTagsAsync("applied", 10, 0));
        Assert.Equal(0, applied.Waiting);
        Assert.NotNull(applied.CompletedUtc);
    }

    [Fact]
    public async Task A_rollout_leaves_a_reverted_or_removed_record_as_an_operator_left_it_and_does_not_wait_for_it()
    {
        var keys = await DeliveredAsync(3, Reads("Name", "metre"));

        // One record a reversal put back, one removed from OSDU: what OSDU holds of them is an operator's decision.
        await using (var db = _db.CreateDbContext())
        {
            await db.DeliveryRecords.Where(r => r.FlowId == _flow && r.DeliveryKey == keys[1].Value)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, "reverted").SetProperty(r => r.Blocked, true).SetProperty(r => r.MetadataHash, "restored"));
            await db.DeliveryRecords.Where(r => r.FlowId == _flow && r.DeliveryKey == keys[2].Value)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, "deleted"));
        }

        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Auto);
        var tag = Assert.Single(await Ledger.ListRolloutQueueAsync(10));
        Assert.Equal(1, (await Ledger.RollOutTagAsync(tag.TagId, batchSize: 10, Now)).Marked);

        // Neither is asked to be planned again: a cache change sends back neither a reversed version nor a removed record.
        var reverted = (await Ledger.GetRecordAsync(_flow, keys[1]))!;
        Assert.Equal(("restored", (DateTime?)null), (reverted.MetadataHash, reverted.PlanRequestedUtc));
        Assert.Null((await Ledger.GetRecordAsync(_flow, keys[2]))!.PlanRequestedUtc);
        Assert.NotNull((await Ledger.GetRecordAsync(_flow, keys[0]))!.PlanRequestedUtc);

        // The change waits only for the record it marked: once that one is built from the new value, it is rolled out.
        var waiting = Assert.Single(await Ledger.ListTagsAsync("rolling", 10, 0));
        Assert.Equal(1, waiting.Waiting);
        await DeliveredAsync(1, Reads("Name", "meter"));
        Assert.Equal(1, await Ledger.SettleRolloutsAsync(Now));
    }

    [Fact]
    public async Task A_rollout_marks_every_flow_s_record_of_a_shared_row_and_resumes_between_them()
    {
        // Two flows read the same rows into different OSDU kinds, and both read the metre's name: each flow's records are
        // its own, and the rollout reaches all of them, even when a batch ends between two flows' records of one key.
        var other = FlowId.Of("test-flow-wellbores");
        var keys = await DeliveredAsync(_flow, "test-flow", "x", Scope, "WELL", 2, Reads("Name", "metre"));
        var same = await DeliveredAsync(other, "test-flow-wellbores", "y", Scope, "WELL", 2, Reads("Name", "metre"));
        Assert.Equal(keys, same);
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Auto);
        var tag = Assert.Single(await Ledger.ListRolloutQueueAsync(10));
        Assert.Equal(4, tag.AffectedRecords);

        var marked = 0L;
        for (var pass = 0; pass < 4; pass++)
        {
            var batch = await Ledger.RollOutTagAsync(tag.TagId, batchSize: 1, Now);
            Assert.Equal(1, batch.Marked);
            marked += batch.Marked;
        }

        Assert.Equal(4, marked);
        Assert.False((await Ledger.RollOutTagAsync(tag.TagId, batchSize: 1, Now)).Completed);
        foreach (var flow in new[] { _flow, other })
        {
            foreach (var key in keys)
            {
                var state = await Ledger.GetRecordAsync(flow, key);
                Assert.Contains("cache change", state!.LastError!, StringComparison.Ordinal);
                Assert.NotNull(state.PlanRequestedUtc);
            }
        }

        // Both flows read the cache, so the change is rolled out only when the second of them has run too.
        var both = Assert.Single(await Ledger.ListTagsAsync("rolling", 10, 0));
        Assert.Equal(2, both.WaitingByFlow[_flow]);
        Assert.Equal(2, both.WaitingByFlow[other]);

        await DeliveredAsync(_flow, "test-flow", "x", Scope, "WELL", 2, Reads("Name", "meter"));
        Assert.Equal(0, await Ledger.SettleRolloutsAsync(Now));
        var one = Assert.Single(await Ledger.ListTagsAsync("rolling", 10, 0));
        Assert.Equal(other, Assert.Single(one.WaitingByFlow).Key);
        Assert.Equal(2, one.Waiting);

        await DeliveredAsync(other, "test-flow-wellbores", "y", Scope, "WELL", 2, Reads("Name", "meter"));
        Assert.Equal(1, await Ledger.SettleRolloutsAsync(Now));
        Assert.Empty(await Ledger.ListTagsAsync("rolling", 10, 0));
        Assert.Single(await Ledger.ListTagsAsync("applied", 10, 0));
    }

    [Fact]
    public async Task A_value_that_moves_again_while_its_flows_catch_up_marks_its_records_again()
    {
        await DeliveredAsync(2, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Auto);
        var tag = Assert.Single(await Ledger.ListRolloutQueueAsync(10));
        Assert.False((await Ledger.RollOutTagAsync(tag.TagId, batchSize: 10, Now)).Completed);

        // The flow has not run, and the name moves again: the records still read the first value and are marked afresh.
        await AnalyzeAsync(Units("metre"), Units("metres"), CacheChangeMode.Auto);
        var again = Assert.Single(await Ledger.ListRolloutQueueAsync(10));
        Assert.Equal(tag.TagId, again.TagId);
        Assert.Equal("approved", again.Status);
        Assert.Equal(0, again.Processed);
        Assert.Equal(2, (await Ledger.RollOutTagAsync(tag.TagId, batchSize: 10, Now)).Marked);
    }

    [Fact]
    public async Task A_rejected_change_is_not_raised_again_when_another_value_of_the_same_cached_record_moves()
    {
        await DeliveredAsync(2, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        await Ledger.DecideTagsAsync([tag.TagId], approve: false, "tahir", Now);

        // The metre gains an alias. The record is asked about again because one of its values moved, and its name reads
        // exactly as it did in the version before: the refresh changed nothing the records read, so it raises nothing.
        var impact = await AnalyzeAsync(Units("meter"), Units("meter", "m."), CacheChangeMode.Approve);
        Assert.Equal(1, impact.ChangedItems);
        Assert.Equal(0, impact.Changes);
        Assert.Empty(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Single(await Ledger.ListTagsAsync("rejected", 10, 0));
        Assert.Empty(await Ledger.GatedCacheSetsAsync());
    }

    [Fact]
    public async Task A_change_waiting_for_a_decision_keeps_the_version_it_was_found_in_when_another_value_of_the_record_moves()
    {
        await DeliveredAsync(2, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        var found = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));

        _clock.Advance(TimeSpan.FromHours(1));
        var impact = await new CacheImpactAnalyzer(Ledger, _clock, NullLogger.Instance)
            .AnalyzeAsync(Scope, Units("meter"), Units("meter", "m."), CacheChangeMode.Approve, "v2", "v3");
        Assert.Equal(0, impact.Changes);

        // The change still reads as found in v2, when it was, and not as a change a later refresh made.
        var standing = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal(found.TagId, standing.TagId);
        Assert.Equal("v2", standing.ToVersion);
        Assert.Equal(found.DetectedUtc, standing.DetectedUtc);
        Assert.Equal(found.SetIds, standing.SetIds);
    }

    [Fact]
    public async Task A_path_that_held_nothing_and_still_holds_nothing_is_not_told_as_a_change()
    {
        // Built when the metre's aliases were an empty set: the documents carry none.
        await DeliveredAsync(2, Reads("Aliases", string.Empty, CacheUsageKind.Empty));

        // The metre is renamed and its aliases are still an empty set: nothing the records read moved.
        var impact = await AnalyzeAsync(Aliased("metre", "[]"), Aliased("meter", "[]"), CacheChangeMode.Approve);
        Assert.Equal(1, impact.ChangedItems);
        Assert.Equal(0, impact.Changes);
        Assert.Empty(await Ledger.ListTagsAsync("pending", 10, 0));

        // Aliases that arrive are a change of the documents.
        var arrived = await AnalyzeAsync(Aliased("meter", "[]"), Aliased("meter", """["m."]"""), CacheChangeMode.Approve);
        Assert.Equal(1, arrived.Changes);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal("Aliases", tag.Path);
        Assert.Equal("m.", tag.NewValue);

        static ReferenceType Aliased(string name, string aliases) => new(
            "UnitOfMeasure", "reference-data--UnitOfMeasure",
            [
                new ReferenceItem("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Code"] = ReferenceValue.Of("m"),
                    ["Name"] = ReferenceValue.Of(name),
                    ["Aliases"] = ReferenceValue.From(JsonNode.Parse(aliases)!),
                }),
            ]);
    }

    [Fact]
    public async Task A_change_to_a_path_longer_than_the_ledger_keeps_is_found_again_rather_than_raised_twice()
    {
        var set = await Ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "metre")]);
        var tag = new UpdateTag
        {
            Scope = Scope,
            TypeName = "UnitOfMeasure",
            ItemId = "dev:reference-data--UnitOfMeasure:m",
            Path = "Name." + new string('p', 450),
            Change = "changed",
            OldValue = "metre",
            NewValue = "meter",
            ToVersion = "v2",
            Mode = "approve",
            SetIds = [set],
            AffectedRecords = 1,
        };

        Assert.Equal(1, await Ledger.TagUpdatesAsync([tag], Now));
        Assert.Equal(0, await Ledger.TagUpdatesAsync([tag with { ToVersion = "v3" }], Now));
        var standing = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        Assert.Equal(UpdateTag.MaxTextLength, standing.Path.Length);
        Assert.Equal("v3", standing.ToVersion);
    }

    [Fact]
    public async Task A_rejected_change_is_never_rolled_out()
    {
        var keys = await DeliveredAsync(2, Reads("Name", "metre"));
        await AnalyzeAsync(Units("metre"), Units("meter"), CacheChangeMode.Approve);
        var tag = Assert.Single(await Ledger.ListTagsAsync("pending", 10, 0));
        await Ledger.DecideTagsAsync([tag.TagId], approve: false, "tahir", Now);

        Assert.Empty(await Ledger.ListRolloutQueueAsync(10));
        var batch = await Ledger.RollOutTagAsync(tag.TagId, batchSize: 10, Now);
        Assert.Equal(0, batch.Marked);

        // Nothing was marked for redelivery, so no record carries a cache-change note.
        foreach (var key in keys)
        {
            var state = await Ledger.GetRecordAsync(_flow, key);
            Assert.DoesNotContain("cache change", state!.LastError ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
