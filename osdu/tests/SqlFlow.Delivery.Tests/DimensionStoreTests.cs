using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Ledger;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The dimensions the ledger keeps on SQL Server (osdu/docs/reference/concepts/dimension-tables.md):
/// a build written whole or not at all, members and originals that keep their ids across builds, what each build changed
/// told in the change log, and values compared exactly, as a filter compares them.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DimensionStoreTests : IDisposable
{
    private static readonly Guid FlowId = Guid.Parse("7f3d1a52-5b8e-4e24-9f44-0b1c2d3e4f50");

    private static readonly DimensionFieldState Field = new("text", "data.Curves", "nested(data.Curves, Mnemonic.keyword)", Repeats: true);

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();

    public void Dispose() => _db.Dispose();

    private async Task<OsduLedger> LedgerAsync()
    {
        var ledger = _db.Ledger(_clock);
        await ledger.RegisterLedgerAsync(new LedgerEntry
        {
            FlowId = FlowId,
            Partition = "dev",
            Kind = LedgerKinds.Dimension,
            FlowName = "wells-dimensions",
            LedgerName = "wells-dimensions@dev",
        });
        return ledger;
    }

    private static DimensionDeclaration Declaration(string hash = "0123456789abcdef") => new()
    {
        FlowId = FlowId,
        FlowName = "wells-dimensions",
        Name = "CurveMnemonic",
        Kind = "osdu:wks:work-product-component--WellLog:*",
        Path = "data.Curves.Mnemonic",
        CleanJson = """[{"kind":"upper"}]""",
        DefinitionHash = hash,
    };

    private static DimensionOriginalWrite Original(string original, string? member, long count = 1, bool filterable = true, string? leftOut = null, string? note = null)
        => new(original, member, member is null ? leftOut ?? DimensionLeftOut.Empty : null, note, count, filterable);

    private static DimensionMemberWrite Member(string value, long records, int originals, string? filter = null)
        => new(value, records, RecordsExact: false, originals, Unfilterable: 0, filter ?? $"filter of {value}", FilterParts: 1);

    /// <summary>Starts a build and writes it with <paramref name="originals"/> and the members they add up to.</summary>
    private async Task<(DimensionState Dimension, DimensionRunState Run)> BuildAsync(OsduLedger ledger, params DimensionOriginalWrite[] originals)
    {
        var (dimension, run) = await ledger.StartDimensionRunAsync(Declaration(), Guid.NewGuid(), "tests", _clock.GetUtcNow().UtcDateTime);
        var members = originals.Where(o => o.CleanValue is not null)
            .GroupBy(o => o.CleanValue!, StringComparer.Ordinal)
            .Select(g => Member(g.Key, g.Sum(o => o.Count), g.Count()))
            .ToList();
        _clock.Advance(TimeSpan.FromMinutes(1));
        var written = await ledger.WriteDimensionAsync(new DimensionWrite
        {
            DimensionRunId = run.DimensionRunId,
            DimensionId = dimension.DimensionId,
            FlowId = FlowId,
            Field = Field,
            Originals = originals,
            Members = members,
            Read = new DimensionReadCounts { Records = 10, Aggregations = 1, Slices = 1, Notes = ["a note"] },
            CompletedUtc = _clock.GetUtcNow().UtcDateTime,
        });
        return ((await ledger.GetDimensionAsync(dimension.DimensionId))!, written);
    }

    private static async Task<IReadOnlyList<DimensionMemberState>> MembersAsync(OsduLedger ledger, int dimensionId, bool includeRemoved = false)
        => await ledger.ListDimensionMembersAsync(dimensionId, new DimensionMemberQuery(null, includeRemoved, null, 1000));

    [Fact]
    public async Task A_first_build_keeps_its_members_and_originals_and_logs_no_arrivals()
    {
        var ledger = await LedgerAsync();

        var (dimension, run) = await BuildAsync(ledger,
            Original("GR", "GR", 5), Original("gr", "GR", 2), Original("DT", "DT", 3), Original("---", null));

        Assert.Equal(DimensionRunStatus.Completed, run.Status);
        Assert.Equal((2L, 4L, 1L), (run.Members, run.Originals, run.LeftOut));
        Assert.Equal(new DimensionChangeCounts(2, 0, 0, 4, 0, 0, 0), run.Changes);
        Assert.Equal(["a note"], run.Read.Notes);
        Assert.Equal((2L, 4L, run.DimensionRunId), (dimension.Members, dimension.Originals, dimension.LastRunId));
        Assert.Equal(Field, dimension.Field);
        Assert.Equal("dev", dimension.Partition);

        var members = await MembersAsync(ledger, dimension.DimensionId);
        Assert.Equal(["DT", "GR"], members.Select(m => m.Value));
        Assert.Equal(7, members.Single(m => m.Value == "GR").Records);
        Assert.Empty(await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(null, null, null, null, null, 100)));

        var left = await ledger.ListDimensionValuesAsync(dimension.DimensionId, new DimensionValueQuery(null, null, LeftOutOnly: true, false, null, 100));
        Assert.Equal(("---", DimensionLeftOut.Empty), (Assert.Single(left).Original, left[0].LeftOut));
    }

    [Fact]
    public async Task A_rebuild_over_keys_that_share_an_attribute_value_matches_each_key_to_itself_not_to_every_other()
    {
        // Twenty thousand wellbores of one country: matched by the value alone, a rebuild pairs each with every other
        // (four hundred million pairs, minutes); matched by the key's and the attribute's numbers, each with itself.
        var ledger = await LedgerAsync();
        var originals = Enumerable.Range(0, 20_000)
            .Select(i => new DimensionOriginalWrite(
                $"dev:master-data--Wellbore:{i:D6}:", $"US {i:D6}", null, null, 3, true,
                Attributes: [new DimensionAttributeState("Country", "United States", "dev:master-data--GeoPoliticalEntity:US"), new DimensionAttributeState("Field", "Not specified", null)]))
            .ToArray();
        var (dimension, first) = await BuildAsync(ledger, originals);
        Assert.Equal(20_000L, first.Originals);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (_, second) = await BuildAsync(ledger, originals);
        watch.Stop();

        Assert.Equal(new DimensionChangeCounts(0, 0, 0, 0, 0, 0, 0), second.Changes);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"A rebuild of 20,000 keys that changed nothing took {watch.Elapsed.TotalSeconds:N1} s.");
        var keys = await ledger.ListDimensionValuesAsync(dimension.DimensionId, new DimensionValueQuery(null, null, false, false, null, 2));
        Assert.All(keys, k => Assert.Equal([("Country", "United States"), ("Field", "Not specified")], k.Attributes.Select(a => (a.Name, a.Value))));
    }

    [Fact]
    public async Task A_later_build_adds_removes_and_moves_and_every_member_keeps_its_id()
    {
        var ledger = await LedgerAsync();
        var (dimension, _) = await BuildAsync(ledger, Original("GR", "GR"), Original("gr", "GR"), Original("DT", "DT"));
        var before = await MembersAsync(ledger, dimension.DimensionId);

        var (after, run) = await BuildAsync(ledger, Original("GR", "GR"), Original("gr", "Gr"), Original("RHOB", "RHOB"));

        Assert.Equal(new DimensionChangeCounts(MembersAdded: 2, MembersRemoved: 1, MembersRestored: 0, OriginalsAdded: 1, OriginalsRemoved: 1, OriginalsMoved: 1, OriginalsRestored: 0), run.Changes);
        Assert.Equal((3L, 3L), (after.Members, after.Originals));

        var members = await MembersAsync(ledger, dimension.DimensionId, includeRemoved: true);
        Assert.Equal(before.Single(m => m.Value == "GR").MemberId, members.Single(m => m.Value == "GR").MemberId);
        Assert.Equal(run.DimensionRunId, members.Single(m => m.Value == "DT").RemovedRunId);
        Assert.Equal(["GR", "Gr", "RHOB"], (await MembersAsync(ledger, dimension.DimensionId)).Select(m => m.Value));

        var changes = await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(run.DimensionRunId, null, null, null, null, 100));
        Assert.Equal(
            [("DT", DimensionChangeKinds.Removed, "DT", null), ("RHOB", DimensionChangeKinds.Added, null, "RHOB"), ("gr", DimensionChangeKinds.Moved, "GR", "Gr")],
            changes.Select(c => (c.Original, c.Change, c.FromValue, c.ToValue)).OrderBy(c => c.Original, StringComparer.Ordinal));

        // A member's history is every change that took an original from it or brought one to it; a kind narrows the log.
        var gr = members.Single(m => m.Value == "GR").MemberId;
        Assert.Equal(["gr"], (await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(null, null, gr, null, null, 100))).Select(c => c.Original));
        Assert.Equal(["RHOB"], (await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(null, null, null, DimensionChangeKinds.Added, null, 100))).Select(c => c.Original));
        var newest = await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(null, null, null, null, null, 2));
        var older = await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(null, null, null, null, newest[^1].ChangeId, 2));
        Assert.Equal(3, newest.Concat(older).Select(c => c.ChangeId).Distinct().Count());
    }

    [Fact]
    public async Task The_latest_build_of_each_dimension_is_read_whatever_it_came_to_and_builds_are_read_by_id()
    {
        var ledger = await LedgerAsync();
        var (dimension, completed) = await BuildAsync(ledger, Original("GR", "GR"));
        var (_, failing) = await ledger.StartDimensionRunAsync(Declaration("fedcba9876543210"), null, "tests", _clock.GetUtcNow().UtcDateTime);
        await ledger.CloseDimensionRunAsync(failing.DimensionRunId, DimensionRunStatus.Failed, DimensionReadCounts.None, "search answered 503", _clock.GetUtcNow().UtcDateTime);

        var latest = Assert.Single(await ledger.LatestDimensionRunsAsync([dimension.DimensionId, 999_999]));
        Assert.Equal((failing.DimensionRunId, DimensionRunStatus.Failed), (latest.DimensionRunId, latest.Status));
        var named = await ledger.GetDimensionRunsAsync([completed.DimensionRunId, 999_999_999L]);
        Assert.Equal(DimensionRunStatus.Completed, Assert.Single(named).Status);
    }

    [Fact]
    public async Task A_member_and_an_original_that_come_back_after_their_removal_was_deleted_arrive_again_under_new_ids()
    {
        // A dimension keeps only its latest state: the build after the one that removed them deleted them, so coming back
        // is an arrival, and the change that removed them still names them.
        var ledger = await LedgerAsync();
        var (dimension, _) = await BuildAsync(ledger, Original("DT", "DT"), Original("GR", "GR"));
        var dt = (await MembersAsync(ledger, dimension.DimensionId)).Single(m => m.Value == "DT").MemberId;
        var (_, removal) = await BuildAsync(ledger, Original("GR", "GR"));

        var (_, run) = await BuildAsync(ledger, Original("DT", "DT"), Original("GR", "GR"));

        Assert.Equal(new DimensionChangeCounts(MembersAdded: 1, MembersRemoved: 0, MembersRestored: 0, OriginalsAdded: 1, OriginalsRemoved: 0, OriginalsMoved: 0, OriginalsRestored: 0), run.Changes);
        var back = (await MembersAsync(ledger, dimension.DimensionId, includeRemoved: true)).Single(m => m.Value == "DT");
        Assert.NotEqual(dt, back.MemberId);
        Assert.Null(back.RemovedRunId);
        var removed = Assert.Single(await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(removal.DimensionRunId, null, null, null, null, 100)));
        Assert.Equal(("DT", DimensionChangeKinds.Removed, "DT", (string?)null), (removed.Original, removed.Change, removed.FromValue, removed.ToValue));
        Assert.Contains("1 key(s) and 1 value(s) an earlier build removed were deleted", Assert.Single(run.Read.Notes, n => n.Contains("earlier build", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_build_after_a_removal_deletes_the_removed_key_with_its_attributes_and_keeps_its_change()
    {
        var ledger = await LedgerAsync();
        var attributes = new[] { new DimensionAttributeState("Country", "Norway", "dev:master-data--GeoPoliticalEntity:NO") };
        var (dimension, _) = await BuildAsync(ledger,
            Original("DT", "DT") with { Attributes = attributes }, Original("GR", "GR") with { Attributes = attributes });
        var (_, removal) = await BuildAsync(ledger, Original("GR", "GR") with { Attributes = attributes });

        // Until the next build the removal stays as it was made: the key and its value marked removed, attributes and all.
        await using (var db = _db.CreateDbContext())
        {
            var dt = await db.DeliveryDimensionValues.AsNoTracking().SingleAsync(v => v.DimensionId == dimension.DimensionId && v.Original == "DT");
            Assert.Equal(removal.DimensionRunId, dt.RemovedRunId);
            Assert.Equal(1, await db.DeliveryDimensionAttributeValues.AsNoTracking().CountAsync(a => a.DimensionId == dimension.DimensionId && a.ValueId == dt.ValueId));
        }

        await BuildAsync(ledger, Original("GR", "GR") with { Attributes = attributes });

        await using (var db = _db.CreateDbContext())
        {
            Assert.Equal(["GR"], await db.DeliveryDimensionValues.AsNoTracking().Where(v => v.DimensionId == dimension.DimensionId).Select(v => v.Original).ToListAsync());
            Assert.Equal(["GR"], await db.DeliveryDimensionMembers.AsNoTracking().Where(m => m.DimensionId == dimension.DimensionId).Select(m => m.Value).ToListAsync());
            Assert.Equal(1, await db.DeliveryDimensionAttributeValues.AsNoTracking().CountAsync(a => a.DimensionId == dimension.DimensionId));
        }

        var change = Assert.Single(await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(null, null, null, DimensionChangeKinds.Removed, null, 100)));
        Assert.Equal(("DT", "DT", removal.DimensionRunId), (change.Original, change.FromValue, change.DimensionRunId));
    }

    [Fact]
    public async Task A_build_that_found_the_same_values_changes_nothing()
    {
        var ledger = await LedgerAsync();
        var (dimension, _) = await BuildAsync(ledger, Original("GR", "GR", 5), Original("DT", "DT", 3));

        var (_, run) = await BuildAsync(ledger, Original("GR", "GR", 5), Original("DT", "DT", 3));

        Assert.True(run.Changes.IsNone);
        Assert.Empty(await ledger.ListDimensionChangesAsync(dimension.DimensionId, new DimensionChangeQuery(run.DimensionRunId, null, null, null, null, 100)));
    }

    [Fact]
    public async Task A_new_count_or_filter_is_written_without_being_a_change_of_an_original()
    {
        var ledger = await LedgerAsync();
        var (dimension, _) = await BuildAsync(ledger, Original("GR", "GR", 5));

        var (_, run) = await BuildAsync(ledger, Original("GR", "GR", 9));

        Assert.True(run.Changes.IsNone);
        Assert.Equal(9, (await MembersAsync(ledger, dimension.DimensionId)).Single().Records);
    }

    [Fact]
    public async Task Values_are_compared_exactly_as_a_filter_compares_them()
    {
        var ledger = await LedgerAsync();

        // Case makes two members; a trailing space makes two originals, which SQL Server would compare as one text.
        var (dimension, run) = await BuildAsync(ledger, Original("GR", "GR"), Original("gr", "gr"), Original("GR ", "GR"), Original(new string('x', 1024), "long"));

        Assert.Equal((3L, 4L), (run.Members, run.Originals));
        var originals = await ledger.MemberOriginalsAsync(dimension.DimensionId, (await MembersAsync(ledger, dimension.DimensionId)).Select(m => m.MemberId).ToList());
        Assert.Contains(originals, o => o.Original == "GR " && o.MemberValue == "GR");
        Assert.Contains(originals, o => o.Original == "GR" && o.MemberValue == "GR");
    }

    [Fact]
    public async Task An_original_longer_than_a_dimension_keeps_is_refused_before_anything_is_written()
    {
        var ledger = await LedgerAsync();
        var (dimension, run) = await ledger.StartDimensionRunAsync(Declaration(), null, "tests", _clock.GetUtcNow().UtcDateTime);

        await Assert.ThrowsAsync<DeliveryException>(() => ledger.WriteDimensionAsync(new DimensionWrite
        {
            DimensionRunId = run.DimensionRunId,
            DimensionId = dimension.DimensionId,
            FlowId = FlowId,
            Field = Field,
            Originals = [Original(new string('x', 1025), "x")],
            Members = [Member("x", 1, 1)],
            Read = DimensionReadCounts.None,
            CompletedUtc = _clock.GetUtcNow().UtcDateTime,
        }));
        Assert.Null((await ledger.GetDimensionAsync(dimension.DimensionId))!.LastRunId);
    }

    [Fact]
    public async Task A_failed_build_is_closed_as_failed_and_leaves_the_dimension_as_the_build_before_left_it()
    {
        var ledger = await LedgerAsync();
        var (dimension, first) = await BuildAsync(ledger, Original("GR", "GR"));
        var (_, run) = await ledger.StartDimensionRunAsync(Declaration("fedcba9876543210"), null, "tests", _clock.GetUtcNow().UtcDateTime);

        await ledger.CloseDimensionRunAsync(run.DimensionRunId, DimensionRunStatus.Failed, new DimensionReadCounts { Aggregations = 3 }, "search answered 503", _clock.GetUtcNow().UtcDateTime);

        var runs = await ledger.ListDimensionRunsAsync(dimension.DimensionId, 10);
        var failed = runs.Single(r => r.DimensionRunId == run.DimensionRunId);
        Assert.Equal((DimensionRunStatus.Failed, "search answered 503", 3), (failed.Status, failed.Error, failed.Read.Aggregations));
        var now = (await ledger.GetDimensionAsync(dimension.DimensionId))!;
        Assert.Equal(first.DimensionRunId, now.LastRunId);
        Assert.Equal(["GR"], (await MembersAsync(ledger, dimension.DimensionId)).Select(m => m.Value));
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.CloseDimensionRunAsync(run.DimensionRunId, DimensionRunStatus.Completed, DimensionReadCounts.None, null, DateTime.UtcNow));
    }

    [Fact]
    public async Task Members_are_found_by_their_clean_value_or_an_original_whatever_its_case_and_paged_in_order()
    {
        var ledger = await LedgerAsync();
        var (dimension, _) = await BuildAsync(ledger,
            Original("Gamma Ray", "GR"), Original("DT", "DT"), Original("RHOB", "RHOB"), Original("NPHI", "NPHI"));

        var found = await ledger.ListDimensionMembersAsync(dimension.DimensionId, new DimensionMemberQuery("gamma", false, null, 10));
        Assert.Equal(["GR"], found.Select(m => m.Value));
        Assert.Equal(["RHOB"], (await ledger.ListDimensionMembersAsync(dimension.DimensionId, new DimensionMemberQuery("rho", false, null, 10))).Select(m => m.Value));

        var first = await ledger.ListDimensionMembersAsync(dimension.DimensionId, new DimensionMemberQuery(null, false, null, 2));
        var second = await ledger.ListDimensionMembersAsync(dimension.DimensionId, new DimensionMemberQuery(null, false, new DimensionMemberCursor(first[^1].Value, first[^1].Records), 2));
        Assert.Equal(["DT", "GR", "NPHI", "RHOB"], first.Concat(second).Select(m => m.Value));

        var named = await ledger.GetDimensionMembersAsync(dimension.DimensionId, [first[0].MemberId], ["RHOB", "absent"]);
        Assert.Equal(["DT", "RHOB"], named.Select(m => m.Value));
    }

    [Fact]
    public async Task Members_and_originals_page_with_the_most_records_first_ties_in_order_of_value_and_arrival()
    {
        var ledger = await LedgerAsync();
        var (dimension, _) = await BuildAsync(ledger,
            Original("GR", "GR", 5), Original("gr", "GR", 4), Original("DT", "DT", 3), Original("NPHI", "NPHI", 9), Original("RHOB", "RHOB", 9),
            Original("CALI", "CALI", 1));

        var members = new List<DimensionMemberState>();
        DimensionMemberCursor? after = null;
        for (var page = 0; page < 10; page++)
        {
            var read = await ledger.ListDimensionMembersAsync(dimension.DimensionId, new DimensionMemberQuery(null, false, after, 2, DimensionMemberOrder.Records));
            members.AddRange(read);
            if (read.Count < 2)
            {
                break;
            }

            after = new DimensionMemberCursor(read[^1].Value, read[^1].Records);
        }

        Assert.Equal([("GR", 9L), ("NPHI", 9L), ("RHOB", 9L), ("DT", 3L), ("CALI", 1L)], members.Select(m => (m.Value, m.Records)));

        var originals = new List<DimensionValueState>();
        DimensionValueCursor? next = null;
        for (var page = 0; page < 10; page++)
        {
            var read = await ledger.ListDimensionValuesAsync(dimension.DimensionId, new DimensionValueQuery(null, null, false, false, next, 4, DimensionValueOrder.Count));
            originals.AddRange(read);
            if (read.Count < 4)
            {
                break;
            }

            next = new DimensionValueCursor(read[^1].ValueId, read[^1].Count);
        }

        Assert.Equal(["NPHI", "RHOB", "GR", "gr", "DT", "CALI"], originals.Select(v => v.Original));

        var gr = members.Single(m => m.Value == "GR").MemberId;
        var dt = members.Single(m => m.Value == "DT").MemberId;
        var top = await ledger.TopMemberOriginalsAsync(dimension.DimensionId, [gr, dt], perMember: 1);
        Assert.Equal([("DT", "DT"), ("GR", "GR")], top.Select(v => (v.MemberValue!, v.Original)).Order());
        Assert.Equal(["GR", "gr"], (await ledger.ListDimensionValuesAsync(dimension.DimensionId, new DimensionValueQuery("g", null, false, false, null, 10, DimensionValueOrder.Count))).Select(v => v.Original));
    }

    [Fact]
    public async Task A_dimension_is_found_by_its_flow_and_name_and_listed_by_partition()
    {
        var ledger = await LedgerAsync();
        var (dimension, _) = await BuildAsync(ledger, Original("GR", "GR"));

        Assert.Equal(dimension.DimensionId, (await ledger.FindDimensionAsync(FlowId, "CurveMnemonic"))!.DimensionId);
        Assert.Null(await ledger.FindDimensionAsync(FlowId, "Nope"));
        Assert.Single(await ledger.ListDimensionsAsync("dev", null));
        Assert.Empty(await ledger.ListDimensionsAsync("test", null));
        Assert.Single(await ledger.ListDimensionsAsync(null, FlowId));
    }

    [Fact]
    public async Task Tens_of_thousands_of_originals_are_written_in_one_build_and_changed_by_the_next()
    {
        var ledger = await LedgerAsync();
        var originals = Enumerable.Range(0, 50_000).Select(i => Original($"V{i:D6}", $"M{i % 5_000:D4}", count: i)).ToArray();

        var (dimension, run) = await BuildAsync(ledger, originals);

        Assert.Equal((5_000L, 50_000L), (run.Members, run.Originals));
        var next = originals.Skip(1_000).Select(o => o with { Count = o.Count + 1 }).ToArray();
        var (_, second) = await BuildAsync(ledger, next);
        Assert.Equal(1_000, second.Changes.OriginalsRemoved);
        await using var context = _db.CreateDbContext();
        Assert.Equal(1_000, await context.DeliveryDimensionChanges.CountAsync(c => c.DimensionId == dimension.DimensionId));
    }

    [Fact]
    public async Task Two_builds_of_one_dimension_writing_at_once_are_written_one_after_the_other()
    {
        var ledger = await LedgerAsync();
        var (dimension, first) = await ledger.StartDimensionRunAsync(Declaration(), null, "tests", _clock.GetUtcNow().UtcDateTime);
        var (_, second) = await ledger.StartDimensionRunAsync(Declaration(), null, "tests", _clock.GetUtcNow().UtcDateTime);
        DimensionWrite Write(DimensionRunState run, string value) => new()
        {
            DimensionRunId = run.DimensionRunId,
            DimensionId = dimension.DimensionId,
            FlowId = FlowId,
            Field = Field,
            Originals = Enumerable.Range(0, 5_000).Select(i => Original($"{value}{i}", value)).ToList(),
            Members = [Member(value, 5_000, 5_000)],
            Read = DimensionReadCounts.None,
            CompletedUtc = _clock.GetUtcNow().UtcDateTime,
        };

        var written = await Task.WhenAll(ledger.WriteDimensionAsync(Write(first, "A")), ledger.WriteDimensionAsync(Write(second, "B")));

        Assert.All(written, w => Assert.Equal(DimensionRunStatus.Completed, w.Status));
        var members = await MembersAsync(ledger, dimension.DimensionId);
        Assert.Single(members);
        Assert.Equal(5_000, (await ledger.GetDimensionAsync(dimension.DimensionId))!.Originals);
    }

    [Fact]
    public async Task A_write_runs_as_long_as_it_takes_and_stops_when_its_run_is_cancelled_writing_nothing()
    {
        // A write has no time limit, since one of millions of curves takes as long as its rows do: the run's cancellation
        // is what stops it, here while another session holds the dimension's row, which the write has to change.
        var ledger = await LedgerAsync();
        var (dimension, run) = await ledger.StartDimensionRunAsync(Declaration(), null, "tests", _clock.GetUtcNow().UtcDateTime);
        await using var holder = new Microsoft.Data.SqlClient.SqlConnection(OsduTestServer.ConnectionString);
        await holder.OpenAsync();
        await using var held = (Microsoft.Data.SqlClient.SqlTransaction)await holder.BeginTransactionAsync();
        await using (var hold = new Microsoft.Data.SqlClient.SqlCommand("UPDATE [osdu].[Dimension] SET [Members] = [Members] WHERE [DimensionId] = @d;", holder, held))
        {
            hold.Parameters.AddWithValue("@d", dimension.DimensionId);
            Assert.Equal(1, await hold.ExecuteNonQueryAsync());
        }

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ledger.WriteDimensionAsync(new DimensionWrite
        {
            DimensionRunId = run.DimensionRunId,
            DimensionId = dimension.DimensionId,
            FlowId = FlowId,
            Field = Field,
            Originals = [Original("GR", "GR")],
            Members = [Member("GR", 1, 1)],
            Read = DimensionReadCounts.None,
            CompletedUtc = _clock.GetUtcNow().UtcDateTime,
        }, cancel.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"the cancelled write ended after {watch.Elapsed}");
        Assert.Equal(cancel.Token, cancelled.CancellationToken);
        await held.RollbackAsync();
        Assert.Equal(0, (await ledger.GetDimensionAsync(dimension.DimensionId))!.Originals);
        Assert.Empty(await MembersAsync(ledger, dimension.DimensionId, includeRemoved: true));
    }
}
