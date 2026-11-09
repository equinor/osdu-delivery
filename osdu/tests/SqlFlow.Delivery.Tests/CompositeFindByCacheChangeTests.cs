using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using Xunit;
using static SqlFlow.Delivery.Tests.CompositeFindByTests;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a refresh does to records built by a findBy line that compares several fields at once: a row that comes to meet
/// every value they looked up reaches them, a row that shares only some of the values reaches none, and a row they were
/// found by that stops holding one of its values reaches them as a match that no longer holds. Each record is judged from
/// the set its render left behind, by asking both versions for the values together, of a lookup table as of OSDU records.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CompositeFindByCacheChangeTests : IAsyncLifetime, IDisposable
{
    private const string Scope = "dev";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("composite-findby-flow");
    private readonly OsduLedger _ledger;

    public CompositeFindByCacheChangeTests() => _ledger = _db.Ledger(_clock);

    public Task InitializeAsync() => _ledger.RegisterAsync(_flow);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Renders a log of <paramref name="well"/>'s <paramref name="mnemonic"/> by <paramref name="data"/> against
    /// <paramref name="cache"/>, and delivers <paramref name="count"/> records built from what it read.
    /// </summary>
    private async Task<IReadOnlyList<CacheUsage>> DeliveredAsync(string data, string well, string mnemonic, int count, ReferenceSnapshot cache, string? unit = null)
    {
        var mapping = TestSchema.Mapping(data);
        var usages = new MappingRenderer(mapping, TestSchema.Build(), cache, TestSchema.Context()).Render(Record(well, mnemonic, unit)).CacheUsages;
        var submission = Guid.NewGuid();
        var now = _clock.GetUtcNow().UtcDateTime;
        await _ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission,
            FlowId = _flow,
            FlowName = "composite-findby-flow",
            MappingReference = "Thing@1.0.0",
            RenderContext = "{}",
            RecordCount = count,
            Status = SubmissionStatus.Planned,
            ReceivedUtc = now,
        });

        var setId = await _ledger.EnsureCacheSetAsync(Scope, usages);
        var source = $"{well}-{mnemonic}-{unit}";
        var records = Enumerable.Range(0, count).Select(i => new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", [$"{source}-{i}"]),
            FlowId = _flow,
            SourceKey = $"{source}-{i}",
            MappingName = "Thing",
            TargetId = $"dev:work-product-component--Thing:{source.Replace('/', '-')}-{i}",
            LastSubmissionId = submission,
            PendingDocumentRef = "0:0:10",
            PendingRenderContext = "{}",
            PendingMetadataHash = "mh",
            PendingMetadata = true,
            CacheSetId = setId,
        }).ToList();
        await _ledger.UpsertPendingAsync(_flow, records);
        return usages;
    }

    private Task<CacheImpactResult> AnalyzeAsync(ReferenceType previous, ReferenceType current)
        => new CacheImpactAnalyzer(_ledger, _clock, NullLogger.Instance).AnalyzeAsync(Scope, previous, current, CacheChangeMode.Auto, "v1", "v2");

    private Task<IReadOnlyList<UpdateTag>> TagsAsync() => _ledger.ListTagsAsync("approved", 50, 0);

    /// <summary>The curve type of the row whose well and mnemonic are the row's, and nothing when no row is.</summary>
    private const string Together = """
        Symbol:
          $cache: CurveMap.curve_type
          $findBy: well = well and mnemonic = mnemonic
          $required: false
        """;

    [Fact]
    public async Task A_row_that_comes_to_meet_the_values_looked_up_together_reaches_the_records_built_without_it()
    {
        await DeliveredAsync(Together, "Z-9", "NPHI", 3, References());

        var impact = await AnalyzeAsync(CurveMap(), CurveMap(("7", "Z-9", "NPHI", "Neutron")));

        Assert.Equal(3, impact.AffectedRecords);
        var tag = Assert.Single(await TagsAsync());
        Assert.Equal(("listed", "7", "well and mnemonic", "[\"Z-9\",\"NPHI\"]"), (tag.Change, tag.ItemId, tag.Path, tag.OldValue));
        Assert.Null(tag.NewValue);
        Assert.Equal("CurveMap now holds '7', where well = 'Z-9' and mnemonic = 'NPHI', which delivered records found no row meeting together", tag.Describe());
    }

    [Fact]
    public async Task A_row_that_shares_only_some_of_the_values_reaches_none_of_them()
    {
        await DeliveredAsync(Together, "Z-9", "NPHI", 2, References());

        // The well's other curves and the mnemonic on another well each share a value, and meet the two together nowhere.
        var impact = await AnalyzeAsync(CurveMap(), CurveMap(("7", "Z-9", "GR", "GammaRay Z"), ("8", "Y-1", "NPHI", "Neutron Y")));

        Assert.Equal(0, impact.AffectedRecords);
        Assert.Empty(await TagsAsync());
    }

    [Fact]
    public async Task Values_looked_up_in_another_case_reach_the_records_and_a_second_row_meeting_them_does_too()
    {
        await DeliveredAsync(Together, "z-9", "nphi", 2, References());

        await AnalyzeAsync(CurveMap(), CurveMap(("7", "Z-9", "NPHI", "Neutron")));
        Assert.Equal("7", Assert.Single(await TagsAsync()).ItemId);

        // Once two rows meet the values, a render would hold the record on them: that changes it as surely.
        var second = await AnalyzeAsync(CurveMap(("7", "Z-9", "NPHI", "Neutron")), CurveMap(("7", "Z-9", "NPHI", "Neutron"), ("8", "z-9", "Nphi", "Neutron 2")));
        Assert.Equal(2, second.AffectedRecords);
    }

    [Fact]
    public async Task A_row_the_records_were_found_by_that_stops_holding_one_of_the_values_is_a_match_that_no_longer_holds()
    {
        await DeliveredAsync(Together, "A-12", "GR", 2, References());

        var moved = CurveMap().Items.Select(item => item.Id == "1"
            ? new ReferenceItem("1", new Dictionary<string, ReferenceValue>(item.Fields, StringComparer.OrdinalIgnoreCase) { ["mnemonic"] = ReferenceValue.Of("GR2") })
            : item);
        var impact = await AnalyzeAsync(CurveMap(), new ReferenceType("CurveMap", CurveMap().EntityType, moved, "row_id"));

        Assert.Equal(2, impact.AffectedRecords);
        var tag = Assert.Single(await TagsAsync());
        Assert.Equal(("unmatched", "1", "mnemonic", "GR"), (tag.Change, tag.ItemId, tag.Path, tag.OldValue));
    }

    [Fact]
    public async Task A_record_of_osdu_records_found_by_several_fields_is_reached_when_one_meets_them_all()
    {
        await DeliveredAsync("""
            Unit:
              $cache: UnitOfMeasure.id
              $findBy: Code = unit and Name = well
              $required: false
            """, "furlong", "x", 2, References(), unit: "fur");

        var units = TestSchema.References().Type("UnitOfMeasure")!;
        var later = new ReferenceType(units.Name, units.EntityType, units.Items.Append(ReferenceItem.FromText(
            "dev:reference-data--UnitOfMeasure:fur",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Code"] = "fur", ["Name"] = "furlong" })));
        var impact = await AnalyzeAsync(units, later);

        Assert.Equal(2, impact.AffectedRecords);
        var tag = Assert.Single(await TagsAsync(), t => t.Change == "listed");
        Assert.Equal(("dev:reference-data--UnitOfMeasure:fur", "Code and Name"), (tag.ItemId, tag.Path));
    }

    [Fact]
    public async Task A_node_folding_separators_is_reached_by_a_row_meeting_its_values_once_they_fold()
    {
        await DeliveredAsync("""
            Symbol:
              $cache: CurveMap.curve_type
              $findBy: well = well and mnemonic = mnemonic
              $ignoreSeparators: true
              $required: false
            """, "E 5", "NPHI", 1, References());

        var impact = await AnalyzeAsync(CurveMap(), CurveMap(("7", "E/5", "NPHI", "Neutron E")));
        Assert.Equal(1, impact.AffectedRecords);

        // A node that does not fold is not reached by that row: it would still find none.
        await DeliveredAsync(Together, "F 6", "NPHI", 1, References());
        Assert.Equal(0, (await AnalyzeAsync(CurveMap(), CurveMap(("8", "F/6", "NPHI", "Neutron F")))).AffectedRecords);
    }

    [Fact]
    public async Task Values_looked_up_together_are_one_gap_until_a_change_brings_the_row_meeting_them()
    {
        await DeliveredAsync(Together, "Z-9", "NPHI", 3, References());
        await DeliveredAsync(Together, "Z-9", "DT", 1, References());

        var gaps = (await _ledger.ListCacheGapsAsync(Scope, "CurveMap", empty: false, take: 50, skip: 0)).Items;
        Assert.Equal(2, gaps.Count);
        var nphi = Assert.Single(gaps, g => g.Value == "[\"Z-9\",\"NPHI\"]");
        Assert.Equal((CacheUsageKind.Unlisted, "well and mnemonic", 3L), (nphi.Kind, nphi.Path, nphi.Records));
        Assert.Equal(CacheUsage.CompositeKey(["Z-9", "NPHI"], ignoreSeparators: false), nphi.Key);

        // The type's composite misses are read by their prefix alone.
        var misses = await _ledger.FindCompositeMissesAsync(Scope, "CurveMap");
        Assert.Equal(2, misses.Select(m => m.ItemId).Distinct().Count());
        Assert.All(misses, m => Assert.True(CacheUsage.IsComposite(m.ItemId)));
        Assert.Empty(await _ledger.FindCompositeMissesAsync(Scope, "UnitOfMeasure"));

        await AnalyzeAsync(CurveMap(), CurveMap(("7", "Z-9", "NPHI", "Neutron")));
        var left = Assert.Single((await _ledger.ListCacheGapsAsync(Scope, "CurveMap", empty: false, take: 50, skip: 0)).Items);
        Assert.Equal("[\"Z-9\",\"DT\"]", left.Value);
    }
}
