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
/// out gone. A value is kept once however many sets hold it (<c>osdu.CacheSetValue</c>): a value no set holds goes in the
/// same sweep, and a set written beside the sweep is always whole.
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

    /// <summary>The values the render context keeps, each as what it was read from and what it held, in that order.</summary>
    private async Task<IReadOnlyList<string>> ValuesAsync()
    {
        await using var db = _db.CreateDbContext();
        var values = await db.DeliveryCacheSetValues.AsNoTracking().ToListAsync();
        return values.Select(v => $"{v.Scope}|{v.TypeName}|{v.ItemId}|{v.Path}|{v.Kind}|{v.ValueText}").Order(StringComparer.Ordinal).ToList();
    }

    private static string Held(string path, string value) => $"{Scope}|UnitOfMeasure|dev:reference-data--UnitOfMeasure:m|{path}|value|{value}";

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
        // a render was just given all stay, with what they hold. The value only the removed set held (Code) goes with it;
        // the one the delivered record's set holds too (Name, metre) stays.
        Assert.Equal(new CacheSetSweep(1, 2, 1), swept);
        Assert.Equal(new[] { delivered, held, recent }.Order(), await SetIdsAsync());
        Assert.Equal(0, await EntriesOfAsync(left));
        Assert.Equal(1, await EntriesOfAsync(delivered));
        Assert.Single(await _ledger.ListCacheSetAsync(held));
        Assert.Equal([Held("Name", "meter"), Held("Name", "metre"), Held("Name", "metres")], await ValuesAsync());

        // A second pass finds nothing more to take.
        Assert.Equal(new CacheSetSweep(0, 0, 0), await _ledger.SweepCacheSetsAsync(UnusedSince));
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
        Assert.Equal(new CacheSetSweep(0, 0, 0), await _ledger.SweepCacheSetsAsync(UnusedSince));
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
        Assert.Equal(new CacheSetSweep(1, 2, 2), await _ledger.SweepCacheSetsAsync(UnusedSince));
        Assert.Empty(await ValuesAsync());

        // Past the hour the ledger checks what it remembers, finds the set gone, and mints it again with every value, the
        // values the sweep took with it kept again.
        var second = await _ledger.EnsureCacheSetAsync(Scope, usages);
        Assert.NotEqual(first, second);
        Assert.Equal(new[] { second }, await SetIdsAsync());
        Assert.Equal(new[] { "Code", "Name" }, (await _ledger.ListCacheSetAsync(second)).Select(e => e.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal([Held("Code", "m"), Held("Name", "metre")], await ValuesAsync());
    }

    [Fact]
    public async Task A_backlog_larger_than_a_round_is_taken_whole_a_few_values_at_a_time()
    {
        var sets = new List<long>();
        for (var i = 0; i < 25; i++)
        {
            sets.Add(await _ledger.EnsureCacheSetAsync(Scope, [Reads("Name", $"unit-{i}"), Reads("Code", $"u{i}"), Reads("Alias", $"a{i}"), Reads("Symbol", "m")]));
        }

        var kept = await _ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "kept"), Reads("Symbol", "m")]);
        await DeliveredAsync("WELL-1", kept);
        _clock.Advance(PastTheDay);

        // Every set holds three values of its own and one they all hold, which is kept once: 77 values for 102 entries.
        Assert.Equal(77, (await ValuesAsync()).Count);

        // Rounds of at most four sets and five entries: a set of four each round, twenty-five rounds. Then the values, ten a
        // round: those only the removed sets held go, and the two the kept set holds stay, the shared one among them.
        var swept = await _ledger.SweepCacheSetsAsync(UnusedSince, setsPerRound: 4, entriesPerRound: 5, valuesPerRound: 10, CancellationToken.None);

        Assert.Equal(new CacheSetSweep(25, 100, 75), swept);
        Assert.Equal(new[] { kept }, await SetIdsAsync());
        foreach (var set in sets)
        {
            Assert.Equal(0, await EntriesOfAsync(set));
        }

        Assert.Equal([Held("Name", "kept"), Held("Symbol", "m")], await ValuesAsync());
        Assert.Equal(2, (await _ledger.ListCacheSetAsync(kept)).Count);
    }

    [Fact]
    public async Task A_value_is_kept_once_and_a_set_reads_back_exactly_what_the_render_handed_over()
    {
        // Every kind of dependency a render records, with the texts a database could read otherwise: quotes, a backslash,
        // a character outside the basic plane, a line break, a NUL, a space at an end, and a key that starts with one.
        var composite = CacheUsage.CompositeMiss("CurveAlias", [("Well", "A-12 \"deep\""), ("Mnemonic", "GR\\X")], ignoreSeparators: true)!;
        CacheUsage[] first =
        [
            new("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", "id", "dev:reference-data--UnitOfMeasure:m:", CacheUsageKind.Match),
            new("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", "Name", "metre \"m\" \\ é 😀\r\n\tend ", CacheUsageKind.Value),
            new("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", "Symbol", string.Empty, CacheUsageKind.Empty),
            new("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", "Note", "a\0b", CacheUsageKind.Value),
            new("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", "Long", new string('x', 1000), CacheUsageKind.Value),
            new("Spellings", CacheUsage.ListingKey("gamma ray"), "Canonical", "gamma ray", CacheUsageKind.Unlisted),
            CacheUsage.Listing("AccessGroup", "Field", "troll", ["dev:b", "dev:a"]),
            new("Wellbore", "dev:master-data--Wellbore:7", "id", "dev:master-data--Wellbore:7:", CacheUsageKind.Unverified),
            new("Wellbore", "dev:master-data--Wellbore:8", "id", "dev:master-data--Wellbore:8:", CacheUsageKind.Missing),
            composite,
        ];
        var set = await _ledger.EnsureCacheSetAsync(Scope, first);

        static List<CacheUse> Ordered(IEnumerable<CacheUse> uses)
            => uses.OrderBy(u => u.TypeName, StringComparer.Ordinal).ThenBy(u => u.Path, StringComparer.Ordinal).ThenBy(u => u.ItemId, StringComparer.Ordinal).ToList();

        var read = await _ledger.ListCacheSetAsync(set);
        Assert.Equal(Ordered(first.Select(u => new CacheUse(Scope, u.TypeName, u.ItemId, u.Path, u.Kind, u.ValueHash, u.Display))), Ordered(read));
        Assert.Equal(400, read.Single(u => u.Path == "Long").ValueText.Length);
        Assert.Equal(Hashing.ContentHash.Of(new string('x', 1000)), read.Single(u => u.Path == "Long").ValueHash);
        Assert.Equal(first.Length, (await ValuesAsync()).Count);

        // A second set holding other values of three of those places keeps three values more, and names the seven it shares:
        // two values that read alike to their first 400 characters are two values, told apart by their hash.
        CacheUsage[] changed = [.. first.Select(u => u.Kind == CacheUsageKind.Value ? u with { Value = u.Value + "!" } : u)];
        var second = await _ledger.EnsureCacheSetAsync(Scope, changed);
        Assert.NotEqual(set, second);
        Assert.Equal(first.Length + 3, (await ValuesAsync()).Count);
        Assert.Equal(first.Length, await EntriesOfAsync(second));
        Assert.Equal(Ordered(changed.Select(u => new CacheUse(Scope, u.TypeName, u.ItemId, u.Path, u.Kind, u.ValueHash, u.Display))), Ordered(await _ledger.ListCacheSetAsync(second)));
        Assert.Equal(Ordered(first.Select(u => new CacheUse(Scope, u.TypeName, u.ItemId, u.Path, u.Kind, u.ValueHash, u.Display))), Ordered(await _ledger.ListCacheSetAsync(set)));

        // The sets holding a cached record are found by the record, and a type's composite misses by their prefix.
        var holding = await _ledger.FindCacheSetsAsync(Scope, "UnitOfMeasure", ["dev:reference-data--UnitOfMeasure:m"]);
        Assert.Equal(10, holding.Count);
        Assert.Equal(new[] { set, second }.Order(), holding.Select(h => h.SetId).Distinct().Order());
        var misses = await _ledger.FindCompositeMissesAsync(Scope, "CurveAlias");
        Assert.Equal(new[] { set, second }.Order(), misses.Select(m => m.SetId).Order());
        Assert.All(misses, miss => Assert.Equal((composite.ItemId, composite.Path, composite.Display, composite.ValueHash), (miss.ItemId, miss.Path, miss.ValueText, miss.ValueHash)));
        Assert.Empty(await _ledger.FindCacheSetsAsync("other", "UnitOfMeasure", ["dev:reference-data--UnitOfMeasure:m"]));
    }

    [Fact]
    public async Task A_value_left_by_a_set_that_was_never_written_goes_in_the_next_sweep()
    {
        var set = await _ledger.EnsureCacheSetAsync(Scope, [Reads("Name", "metre")]);
        await using (var db = _db.CreateDbContext())
        {
            // What a run stopped between a set's values and the set itself leaves: a value no set holds.
            db.DeliveryCacheSetValues.Add(new SqlFlow.Delivery.Data.DeliveryCacheSetValue
            {
                Scope = Scope,
                TypeName = "UnitOfMeasure",
                ItemId = "dev:reference-data--UnitOfMeasure:m",
                Path = "Name",
                Kind = "value",
                ValueHash = Convert.FromHexString(Hashing.ContentHash.Of("meter")),
                ValueText = "meter",
            });
            await db.SaveChangesAsync();
        }

        // The set is a minute old and stays; the value nothing holds goes whatever its age, and the held one stays.
        Assert.Equal(new CacheSetSweep(0, 0, 1), await _ledger.SweepCacheSetsAsync(UnusedSince));
        Assert.Equal([Held("Name", "metre")], await ValuesAsync());
        Assert.Single(await _ledger.ListCacheSetAsync(set));
    }

    [Fact]
    public async Task A_type_name_longer_than_a_value_keeps_is_refused_before_anything_is_written()
    {
        var failure = await Assert.ThrowsAsync<DeliveryException>(
            () => _ledger.EnsureCacheSetAsync(Scope, [new CacheUsage(new string('T', 201), "dev:x:1", "Name", "v", CacheUsageKind.Value)]));

        Assert.Contains("longer than the 200 characters", failure.Message, StringComparison.Ordinal);
        Assert.Empty(await SetIdsAsync());
        Assert.Empty(await ValuesAsync());
    }

    [Fact]
    public async Task Sets_written_beside_the_sweep_and_each_other_are_whole_whichever_values_they_share()
    {
        // Eight writers, each a node of its own, mint sets that share most of their values and bring a few new ones each,
        // while a sweep passes over the values again and again. Every set written must hold every value it was handed, each
        // entry naming a value that is there.
        const int Writers = 8;
        const int SetsEach = 40;
        using var stop = new CancellationTokenSource();
        var sweeper = Task.Run(async () =>
        {
            var sweeps = _db.Ledger(_clock);
            while (!stop.IsCancellationRequested)
            {
                await sweeps.SweepCacheSetsAsync(UnusedSince, setsPerRound: 50, entriesPerRound: 500, valuesPerRound: 50, CancellationToken.None);
                await Task.Delay(TimeSpan.FromMilliseconds(5));
            }
        });

        var minted = await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
        {
            var ledger = _db.Ledger(_clock);
            var sets = new List<(long SetId, int Values)>();
            for (var i = 0; i < SetsEach; i++)
            {
                CacheUsage[] usages =
                [
                    Reads("Name", "metre"),
                    Reads("Code", $"m-{i % 5}"),
                    new("Wellbore", $"dev:master-data--Wellbore:{i}", "id", $"dev:master-data--Wellbore:{i}:", CacheUsageKind.Match),
                    new("Wellbore", $"dev:master-data--Wellbore:{writer}-{i}", "FacilityName", $"well {writer}-{i}", CacheUsageKind.Value),
                ];
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        sets.Add((await ledger.EnsureCacheSetAsync(Scope, usages), usages.Length));
                        break;
                    }
                    catch (DeliveryException ex) when (attempt < 5 && ex.Message.Contains("removed values it holds", StringComparison.Ordinal))
                    {
                        // The sweep took the set's new values three times running, which a sweep every few milliseconds can
                        // do: nothing was kept, and the delivery is run again, as the message says.
                    }
                }
            }

            return sets;
        })));

        await stop.CancelAsync();
        await sweeper;

        await using var db = _db.CreateDbContext();
        var whole = await (from entry in db.DeliveryCacheSetEntries
                           join value in db.DeliveryCacheSetValues on entry.ValueId equals value.ValueId
                           group entry by entry.SetId into g
                           select new { SetId = g.Key, Values = g.Count() }).ToDictionaryAsync(g => g.SetId, g => g.Values);
        var counted = await db.DeliveryCacheSets.AsNoTracking().ToDictionaryAsync(s => s.SetId, s => s.EntryCount);
        Assert.Equal(Writers * SetsEach, minted.SelectMany(sets => sets).Select(s => s.SetId).Distinct().Count());
        foreach (var (setId, values) in minted.SelectMany(sets => sets))
        {
            Assert.Equal(values, whole.GetValueOrDefault(setId));
            Assert.Equal(values, counted[setId]);
        }

        Assert.Equal(await db.DeliveryCacheSetEntries.CountAsync(), whole.Values.Sum());

        // The values the writers shared are kept once: one name, five codes, forty wellbores found, and each set's own.
        Assert.Equal(1 + 5 + SetsEach + (Writers * SetsEach), await db.DeliveryCacheSetValues.CountAsync());
    }
}
