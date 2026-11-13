using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The partition cache's retention as a rule (<see cref="CacheRetention"/>): which versions keep their records at an instant,
/// which are pruned, and which have their changes recorded first; what a cache flow may declare as its retention, and which
/// retention a partition filled by several flows keeps. The store that applies the rule is covered by
/// <see cref="CacheRetentionStoreTests"/>.
/// </summary>
public sealed class CacheRetentionTests
{
    private static readonly DateTime Start = new(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// <paramref name="count"/> versions, the first captured at <see cref="Start"/> and each next one a day later, the last
    /// current; the versions numbered in <paramref name="pruned"/> are pruned already.
    /// </summary>
    private static List<CacheVersionSpan> Daily(int count, params int[] pruned)
        => Enumerable.Range(1, count)
            .Select(sequence => new CacheVersionSpan(sequence, Label(sequence), Start.AddDays(sequence - 1), sequence == count, pruned.Contains(sequence)))
            .ToList();

    private static string Label(int sequence) => CacheVersionLabel.Mint(new DateTimeOffset(Start.AddDays(sequence - 1)));

    private static readonly IReadOnlySet<string> NoPins = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>As many versions as a partition may keep, so a plan weighs the days alone.</summary>
    private const int AnyNumber = CacheRetention.MaxVersions;

    [Fact]
    public void The_current_version_and_the_one_it_replaced_keep_their_records_however_long_ago_they_were_captured()
    {
        var plan = CacheRetention.Plan(Daily(3), retentionDays: 1, Start.AddYears(1), NoPins, AnyNumber);

        Assert.Equal(3, plan.Current);
        Assert.Equal([2, 3], plan.Kept);
        Assert.Equal([1], plan.Pruned.Select(v => v.Sequence));
        // The pruned version's changes, and those of the kept version after it, whose removed rows the pruned one held.
        Assert.Equal([1, 2], plan.Counted);
        Assert.Empty(plan.Pinned);
    }

    [Fact]
    public void A_version_a_newer_one_replaced_within_the_retention_keeps_its_records()
    {
        // Version k was replaced when version k + 1 was captured, k days after the start. Twelve days in, a retention of seven
        // keeps every version replaced on day five or later: the one replaced exactly seven days ago included.
        var plan = CacheRetention.Plan(Daily(10), retentionDays: 7, Start.AddDays(12), NoPins, AnyNumber);

        Assert.Equal([5, 6, 7, 8, 9, 10], plan.Kept);
        Assert.Equal([1, 2, 3, 4], plan.Pruned.Select(v => v.Sequence));
        Assert.Equal([Label(1), Label(2), Label(3), Label(4)], plan.Pruned.Select(v => v.Version));
        Assert.Equal([1, 2, 3, 4, 5], plan.Counted);
        Assert.Equal([(5, 10)], plan.KeptRuns());
    }

    [Fact]
    public void Nothing_is_pruned_while_every_replaced_version_is_within_the_retention()
    {
        var plan = CacheRetention.Plan(Daily(5), retentionDays: 30, Start.AddDays(6), NoPins, AnyNumber);

        Assert.Equal([1, 2, 3, 4, 5], plan.Kept);
        Assert.Empty(plan.Pruned);
        Assert.Empty(plan.Counted);
    }

    [Fact]
    public void A_version_a_delivery_flow_pins_keeps_its_records_past_the_retention()
    {
        var pins = new HashSet<string>(StringComparer.Ordinal) { Label(2), "20200101T000000Z" };
        var plan = CacheRetention.Plan(Daily(10), retentionDays: 7, Start.AddDays(12), pins, AnyNumber);

        Assert.Equal([2, 5, 6, 7, 8, 9, 10], plan.Kept);
        Assert.Equal([1, 3, 4], plan.Pruned.Select(v => v.Sequence));
        Assert.Equal([Label(2)], plan.Pinned);
        // The pinned version follows a pruned one, and so does the oldest version kept for the retention.
        Assert.Equal([1, 2, 3, 4, 5], plan.Counted);
        Assert.Equal([(2, 2), (5, 10)], plan.KeptRuns());
    }

    [Fact]
    public void A_pruned_version_stays_pruned_whatever_pins_it_now()
    {
        var pins = new HashSet<string>(StringComparer.Ordinal) { Label(2) };
        var plan = CacheRetention.Plan(Daily(10, pruned: [1, 2, 3, 4]), retentionDays: 7, Start.AddDays(12), pins, AnyNumber);

        Assert.Equal([5, 6, 7, 8, 9, 10], plan.Kept);
        Assert.Empty(plan.Pruned);
        Assert.Empty(plan.Pinned);
        // The kept version after the pruned ones is still named: a store records its changes only when it holds none yet.
        Assert.Equal([5], plan.Counted);
    }

    [Fact]
    public void Only_the_newest_versions_keep_their_records_however_recently_the_others_were_replaced()
    {
        // Every version was replaced within the thirty days; three are kept, the current one counted.
        var plan = CacheRetention.Plan(Daily(10), retentionDays: 30, Start.AddDays(10), NoPins, versionsKept: 3);

        Assert.Equal([8, 9, 10], plan.Kept);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], plan.Pruned.Select(v => v.Sequence));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], plan.Counted);
        Assert.Equal([(8, 10)], plan.KeptRuns());
    }

    [Theory]
    [InlineData(3, new[] { 8, 9, 10 })]
    [InlineData(8, new[] { 5, 6, 7, 8, 9, 10 })]
    public void The_number_of_versions_and_the_days_both_limit_what_is_kept(int versionsKept, int[] kept)
    {
        // Twelve days in, seven days keep the versions replaced on day five or later; the number of versions keeps the newest.
        var plan = CacheRetention.Plan(Daily(10), retentionDays: 7, Start.AddDays(12), NoPins, versionsKept);

        Assert.Equal(kept, plan.Kept);
    }

    [Fact]
    public void A_pinned_version_older_than_the_newest_keeps_its_records_and_takes_none_of_their_places()
    {
        var pins = new HashSet<string>(StringComparer.Ordinal) { Label(2) };
        var plan = CacheRetention.Plan(Daily(10), retentionDays: 30, Start.AddDays(10), pins, versionsKept: 3);

        Assert.Equal([2, 8, 9, 10], plan.Kept);
        Assert.Equal([Label(2)], plan.Pinned);
        Assert.Equal([1, 3, 4, 5, 6, 7], plan.Pruned.Select(v => v.Sequence));
    }

    [Fact]
    public void Two_versions_keep_the_current_one_and_the_one_it_replaced()
    {
        var plan = CacheRetention.Plan(Daily(5), retentionDays: 30, Start.AddDays(5), NoPins, versionsKept: CacheRetention.MinVersions);

        Assert.Equal([4, 5], plan.Kept);
        Assert.Equal([1, 2, 3], plan.Pruned.Select(v => v.Sequence));
    }

    [Theory]
    [InlineData(CacheRetention.MinVersions - 1)]
    [InlineData(CacheRetention.MaxVersions + 1)]
    public void A_plan_keeps_from_two_to_a_thousand_versions(int versionsKept)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CacheRetention.Plan(Daily(2), 7, Start, NoPins, versionsKept));
        Assert.False(CacheRetention.KeepsVersions(versionsKept));
        Assert.Equal(
            $"A partition's cache keeps from 2 to 1000 versions, the current one counted, not {versionsKept}: the current version and the one it replaced always keep their records.",
            CacheRetention.VersionsOutOfRange(versionsKept.ToString(CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData(0, CacheRetention.MinVersions)]
    [InlineData(3, 3)]
    [InlineData(50_000, CacheRetention.MaxVersions)]
    public void A_partition_row_edited_by_hand_keeps_a_number_a_plan_takes(int stored, int bounded)
        => Assert.Equal(bounded, CacheRetention.BoundedVersions(stored));

    [Fact]
    public void A_cache_with_no_current_version_prunes_nothing()
    {
        var versions = Daily(3).Select(v => v with { Current = false }).ToList();
        Assert.Same(CacheRetentionPlan.Nothing, CacheRetention.Plan(versions, 7, Start.AddYears(1), NoPins, AnyNumber));
        Assert.Same(CacheRetentionPlan.Nothing, CacheRetention.Plan([], 7, Start, NoPins, AnyNumber));
    }

    [Fact]
    public void A_plan_reads_the_versions_in_any_order()
    {
        var newestFirst = Enumerable.Reverse(Daily(6)).ToList();
        // Six days in, a retention of two keeps the versions replaced on day four or later.
        var plan = CacheRetention.Plan(newestFirst, retentionDays: 2, Start.AddDays(6), NoPins, AnyNumber);

        Assert.Equal([4, 5, 6], plan.Kept);
        Assert.Equal([1, 2, 3], plan.Pruned.Select(v => v.Sequence));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(CacheRetention.MaxDays + 1)]
    public void A_plan_is_given_days_from_none_to_a_hundred_years(int days)
        => Assert.Throws<ArgumentOutOfRangeException>(() => CacheRetention.Plan(Daily(2), days, Start, NoPins, AnyNumber));

    [Fact]
    public void A_purge_keeping_no_days_keeps_only_the_versions_that_are_always_kept()
    {
        var pins = new HashSet<string>(StringComparer.Ordinal) { Label(3) };

        // An hour after the last capture, every version but the current one, the one it replaced and the pinned one goes,
        // however recently it was replaced.
        var plan = CacheRetention.Plan(Daily(6), retentionDays: 0, Start.AddDays(5).AddHours(1), pins, AnyNumber);

        Assert.Equal([3, 5, 6], plan.Kept);
        Assert.Equal([1, 2, 4], plan.Pruned.Select(v => v.Sequence));
        Assert.Equal([Label(3)], plan.Pinned);
    }

    [Fact]
    public void A_refresh_weighs_what_its_flow_declares_and_a_purge_keeps_the_days_it_names()
    {
        var now = new DateTimeOffset(Start);
        var refresh = CacheRetentionRequest.ForFlow("dev", "units", 30, now, "schedule:nightly");
        Assert.Equal(("dev", "units", 30, "schedule:nightly", false), (refresh.Scope, refresh.FlowName, refresh.Days, refresh.Actor, refresh.DryRun));
        // A refresh keeps the number of versions the partition keeps.
        Assert.Null(refresh.VersionsKept);

        var purge = CacheRetentionRequest.Purge("dev", 0, now, "tahir", dryRun: true);
        Assert.Equal(("dev", (string?)null, 0, "tahir", true), (purge.Scope, purge.FlowName, purge.Days, purge.Actor, purge.DryRun));
        Assert.Null(purge.VersionsKept);
        Assert.Equal(5, CacheRetentionRequest.Purge("dev", 7, now, "tahir", dryRun: true, versionsKept: 5).VersionsKept);
        Assert.Throws<ArgumentOutOfRangeException>(() => CacheRetentionRequest.Purge("dev", 7, now, "tahir", dryRun: true, versionsKept: 1));

        // A flow declares at least a day; a purge keeps from none to a hundred years; both name who applies them.
        Assert.Throws<ArgumentOutOfRangeException>(() => CacheRetentionRequest.ForFlow("dev", "units", 0, now, "tahir"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CacheRetentionRequest.Purge("dev", -1, now, "tahir", dryRun: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => CacheRetentionRequest.Purge("dev", CacheRetention.MaxDays + 1, now, "tahir", dryRun: false));
        Assert.Throws<ArgumentException>(() => CacheRetentionRequest.Purge("dev", 7, now, " ", dryRun: false));
        Assert.Throws<ArgumentException>(() => CacheRetentionRequest.Purge(" ", 7, now, "tahir", dryRun: false));
    }

    [Fact]
    public void A_purge_says_in_one_line_what_it_did_would_do_or_waits_for()
    {
        Assert.Equal(
            "Pruned the records of 2 version(s), keeping the newest 3 version(s), none replaced over 7 day(s) ago, and removed 40 stored row(s); 3 version(s) keep theirs.",
            new CacheRetentionOutcome(7, 3, ["a", "b"], 40).DescribePurge());
        Assert.Equal(
            "Pruning, keeping only the current version, the one it replaced and every pinned version, removes the records of 1 version(s), 5 stored row(s); 2 version(s) keep theirs.",
            new CacheRetentionOutcome(0, 2, ["a"], 5, DryRun: true).DescribePurge());
        Assert.Equal(
            "Nothing to prune keeping the newest 5 version(s), none replaced over 30 day(s) ago; 4 version(s) keep their records.",
            new CacheRetentionOutcome(30, 4, [], 0, VersionsKept: 5).DescribePurge());
        Assert.Equal("5 version(s), 30 day(s)", new CacheRetentionOutcome(30, 4, [], 0, VersionsKept: 5).Rule);
        Assert.Equal("Nothing can be pruned yet: the sync has to run.", new CacheRetentionOutcome(7, 4, [], 0, Deferred: "the sync has to run").DescribePurge());
    }

    [Fact]
    public void A_cache_flow_keeps_seven_days_unless_it_declares_a_retention_from_one_day_to_a_hundred_years()
    {
        Assert.Equal(CacheRetention.DefaultDays, CacheRetention.Check(null, "cache.yaml"));
        Assert.Equal(7, CacheRetention.DefaultDays);
        Assert.Equal(1, CacheRetention.Check(1, "cache.yaml"));
        Assert.Equal(CacheRetention.MaxDays, CacheRetention.Check(CacheRetention.MaxDays, "cache.yaml"));

        foreach (var days in new[] { 0, -3, CacheRetention.MaxDays + 1 })
        {
            var refused = Assert.Throws<FlowValidationException>(() => CacheRetention.Check(days, "cache.yaml"));
            Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"cache.yaml: retentionDays is {days}"), refused.Message, StringComparison.Ordinal);
            Assert.Contains("a whole number from 1 to 36500", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Leave it out to keep them 7 days", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_partition_keeps_the_longest_retention_any_of_its_cache_flows_declares()
    {
        Assert.Equal(30, CacheRetention.For(7, [30, 14]));
        Assert.Equal(30, CacheRetention.For(30, [7]));
        Assert.Equal(7, CacheRetention.For(7, []));
        Assert.Equal(CacheRetention.DefaultDays, CacheRetention.OfPartition([]));
        Assert.Equal(3, CacheRetention.OfPartition([1, 3]));

        // A row edited by hand outside what a document may declare is brought within it.
        Assert.Equal(7, CacheRetention.For(7, [0, -5]));
        Assert.Equal(CacheRetention.MaxDays, CacheRetention.OfPartition([int.MaxValue]));
    }

    [Fact]
    public void A_declaration_says_the_partition_s_retention_and_the_one_a_flow_s_refresh_applies()
    {
        IReadOnlyList<ReferenceFieldSpec> fields = [new ReferenceFieldSpec("data.Code")];
        var declaration = new CacheDeclaration("dev",
        [
            new CacheTypeDeclaration("units", "UnitOfMeasure", "reference-data--UnitOfMeasure", "osdu:wks:reference-data--UnitOfMeasure:1.0.0", "*", fields, CacheChangeMode.Auto, RetentionDays: 30),
            new CacheTypeDeclaration("units", "LogCurveType", "reference-data--LogCurveType", "osdu:wks:reference-data--LogCurveType:1.0.0", "*", fields, CacheChangeMode.Auto, RetentionDays: 30),
            new CacheTypeDeclaration("wells", "Wellbore", "master-data--Wellbore", "osdu:wks:master-data--Wellbore:1.0.0", "*", fields, CacheChangeMode.Auto, RetentionDays: 7),
        ]);

        Assert.Equal(30, declaration.RetentionDays);
        // A refresh of wells applies the 30 days units asks for; a refresh of units applies what its document says now,
        // or the 7 days wells asks for, whichever is longer.
        Assert.Equal(30, declaration.RetentionFor("wells", 7));
        Assert.Equal(7, declaration.RetentionFor("units", 3));
        Assert.Equal(90, declaration.RetentionFor("units", 90));
        Assert.Equal(CacheRetention.DefaultDays, CacheDeclaration.None("dev").RetentionDays);
    }

    /// <summary>A cache flow of one OSDU type, with <paramref name="retention"/> as a top-level line (or none).</summary>
    private static Model.CacheDefinition Parse(string retention) => new DeliveryDocumentLoader().ParseCache(
        $"""
        flowType: cache
        name: welldb-osdu-00-reference-cache
        source:
          endpoint: https://osdu.example.com
          headers: {"{"} data-partition-id: dev {"}"}
        {retention}
        types:
          - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
            fields: [data.Code]
        """,
        "cache/welldb-osdu-00-reference-cache.yaml");

    [Fact]
    public void A_cache_flow_declares_its_retention_with_retentionDays()
    {
        Assert.Equal(CacheRetention.DefaultDays, Parse(string.Empty).RetentionDays);
        Assert.Equal(30, Parse("retentionDays: 30").RetentionDays);

        var zero = Assert.Throws<FlowValidationException>(() => Parse("retentionDays: 0"));
        Assert.Contains("cache/welldb-osdu-00-reference-cache.yaml: retentionDays is 0", zero.Message, StringComparison.Ordinal);

        // A retention is a number of days, written as a number.
        var unit = Assert.Throws<FlowValidationException>(() => Parse("retentionDays: 7d"));
        Assert.Contains("invalid YAML", unit.Message, StringComparison.Ordinal);
    }
}
