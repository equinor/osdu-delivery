using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The render-context sweep (osdu/docs/reference/concepts/storage-and-retention.md, The render context): a set of cached
/// values no record points at, unused for a day, goes with its values; a set a record points at or is held on, or one a
/// render used within the day, stays; and a set the sweep took while a ledger remembered it is minted again, never handed
/// out gone.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CacheSetSweepTests : IAsyncLifetime, IDisposable
{
    private const string Scope = "dev";

    private static readonly TimeSpan PastTheDay = CacheSetKeeping.UnusedFor + TimeSpan.FromHours(1);

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("sweep-flow");
    private readonly OsduLedger _ledger;

    public CacheSetSweepTests() => _ledger = _db.Ledger(_clock);

    public Task InitializeAsync() => _ledger.RegisterAsync(_flow);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _db.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private DateTime UnusedSince => Now - CacheSetKeeping.UnusedFor;

    private static CacheUsage Reads(string path, string value)
        => new("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", path, value, CacheUsageKind.Value);

    /// <summary>Delivers one record built from <paramref name="setId"/>, keyed <paramref name="key"/>.</summary>
    private async Task<DeliveryKey> DeliveredAsync(string key, long setId)
    {
        var submission = Guid.NewGuid();
        await _ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission,
            FlowId = _flow,
            FlowName = "sweep-flow",
            MappingReference = "Thing@1.0.0",
            RenderContext = "{}",
            RecordCount = 1,
            Status = SubmissionStatus.Planned,
            ReceivedUtc = Now,
        });

        var deliveryKey = DeliveryKey.Derive("test", [key]);
        await _ledger.UpsertPendingAsync(_flow,
        [
            new RecordState
            {
                DeliveryKey = deliveryKey,
                FlowId = _flow,
                SourceKey = key,
                MappingName = "Thing",
                TargetId = $"dev:x:{key}",
                LastSubmissionId = submission,
                PendingDocumentRef = "0:0:10",
                PendingRenderContext = "{}",
                PendingMetadataHash = "mh",
                PendingMetadata = true,
                CacheSetId = setId,
            },
        ]);
        return deliveryKey;
    }

    private async Task<IReadOnlyList<long>> SetIdsAsync()
    {
        await using var db = _db.CreateDbContext();
        return await db.DeliveryCacheSets.AsNoTracking().OrderBy(s => s.SetId).Select(s => s.SetId).ToListAsync();
    }

    private async Task<int> EntriesOfAsync(long setId)
    {
        await using var db = _db.CreateDbContext();
        return await db.DeliveryCacheSetEntries.AsNoTracking().CountAsync(e => e.SetId == setId);
    }

    [Fact]
    public async Task A_set_no_record_points_at_goes_with_its_values_once_unused_for_a_day_and_every_other_stays()
    {
        var delivered = await _ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "metre")]);
        await DeliveredAsync("WELL-1", delivered);
        var left = await _ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "metre"), Reads("Code", "m")]);
        var held = await _ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "meter")]);
        var heldOn = await DeliveredAsync("WELL-2", delivered);
        await using (var db = _db.CreateDbContext())
        {
            await db.DeliveryRecords.Where(r => r.FlowId == _flow && r.DeliveryKey == heldOn.Value)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.HeldCacheSetId, held));
        }

        _clock.Advance(PastTheDay);
        var recent = await _ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "metres")]);

        var swept = await _ledger.SweepCacheSetsAsync(UnusedSince);

        // Only the set nothing points at and nothing used for a day: the delivered record's, the held record's and the one
        // a render was just given all stay, values and all.
        Assert.Equal(new CacheSetSweep(1, 2), swept);
        Assert.Equal(new[] { delivered, held, recent }.Order(), await SetIdsAsync());
        Assert.Equal(0, await EntriesOfAsync(left));
        Assert.Equal(1, await EntriesOfAsync(delivered));
        Assert.Single(await _ledger.ListCacheSetAsync(held));

        // A second pass finds nothing more to take.
        Assert.Equal(new CacheSetSweep(0, 0), await _ledger.SweepCacheSetsAsync(UnusedSince));
    }

    [Fact]
    public async Task A_render_that_uses_an_old_set_again_marks_it_so_the_sweep_leaves_it()
    {
        CacheUsage[] usages = [Reads("Name", "metre")];
        var set = await _ledger.EnsureCacheSetAsync(Scope, usages);
        _clock.Advance(PastTheDay);

        // Another ledger, a node that never saw the set, reads it again: that is a use, and the set is a day younger for it.
        var again = await _db.Ledger(_clock).EnsureCacheSetAsync(Scope, usages);

        Assert.Equal(set, again);
        Assert.Equal(new CacheSetSweep(0, 0), await _ledger.SweepCacheSetsAsync(UnusedSince));
        Assert.Single(await _ledger.ListCacheSetAsync(set));
    }

    [Fact]
    public async Task A_set_the_sweep_took_while_a_ledger_remembered_it_is_minted_again_never_handed_out_gone()
    {
        CacheUsage[] usages = [Reads("Name", "metre"), Reads("Code", "m")];
        var first = await _ledger.EnsureCacheSetAsync(Scope, usages);

        // Within the hour the ledger answers from memory, as a run's records that read the same values do.
        _clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(first, await _ledger.EnsureCacheSetAsync(Scope, usages));

        _clock.Advance(PastTheDay);
        Assert.Equal(new CacheSetSweep(1, 2), await _ledger.SweepCacheSetsAsync(UnusedSince));

        // Past the hour the ledger checks what it remembers, finds the set gone, and mints it again with every value.
        var second = await _ledger.EnsureCacheSetAsync(Scope, usages);
        Assert.NotEqual(first, second);
        Assert.Equal(new[] { second }, await SetIdsAsync());
        Assert.Equal(new[] { "Code", "Name" }, (await _ledger.ListCacheSetAsync(second)).Select(e => e.Path).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_backlog_larger_than_a_round_is_taken_whole_a_few_values_at_a_time()
    {
        var sets = new List<long>();
        for (var i = 0; i < 25; i++)
        {
            sets.Add(await _ledger.EnsureCacheSetAsync(Scope, [Reads("Name", $"unit-{i}"), Reads("Code", $"u{i}"), Reads("Alias", $"a{i}")]));
        }

        var kept = await _ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "kept")]);
        await DeliveredAsync("WELL-1", kept);
        _clock.Advance(PastTheDay);

        // Rounds of at most four sets and five values: a set of three values each round but the last, twenty-five rounds.
        var swept = await _ledger.SweepCacheSetsAsync(UnusedSince, setsPerRound: 4, entriesPerRound: 5, CancellationToken.None);

        Assert.Equal(new CacheSetSweep(25, 75), swept);
        Assert.Equal(new[] { kept }, await SetIdsAsync());
        foreach (var set in sets)
        {
            Assert.Equal(0, await EntriesOfAsync(set));
        }
    }
}
