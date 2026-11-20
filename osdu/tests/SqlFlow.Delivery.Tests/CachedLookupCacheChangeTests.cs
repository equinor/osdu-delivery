using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using Xunit;
using static SqlFlow.Delivery.Tests.AccessListTests;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a refresh does to records built from what a lookup read where it found nothing or several: the access groups a
/// <c>$findAll</c> read from a lookup table, which a row listed under another key or a row added reaches, and a wellbore
/// found by its alias, which a version holding a wellbore of that name reaches, since the line trying the name came first.
/// Every record is judged from the dependency set its render left behind, never by rendering the estate again.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CachedLookupCacheChangeTests : IAsyncLifetime, IDisposable
{
    private const string Scope = "dev";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("cached-lookup-flow");
    private readonly OsduLedger _ledger;

    public CachedLookupCacheChangeTests() => _ledger = _db.Ledger(_clock);

    public Task InitializeAsync() => _ledger.RegisterAsync(_flow);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _db.Dispose();

    /// <summary>The access groups as a lookup table keyed by address, each row naming the field it covers.</summary>
    private static ReferenceType GroupTable(params (string Email, string Field)[] rows) => new("GroupTable", "lookup--GroupTable", key: "group_email", items:
        rows.Select(r => ReferenceItem.FromText(r.Email, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["group_email"] = r.Email, ["field_id"] = r.Field })));

    private const string TableViewers = """
        acl:
          owners: [owners@x]
          viewers:
            - viewers@x
            - $cache: GroupTable.group_email
              $findAll: field_id = $lookup.wellbore.GeoContexts.FieldID
              $required: false
        legal:
          legaltags: [tag]
          otherRelevantDataCountries: [US]
        """;

    /// <summary>Renders a log of the wellbore named <paramref name="uwi"/> and delivers <paramref name="count"/> records built from what it read.</summary>
    private async Task<IReadOnlyList<string>> DeliveredAsync(MappingDefinition mapping, ReferenceSnapshot cache, string uwi, int count)
    {
        var rendered = Renderer(mapping, cache).Render(Record(uwi));
        Assert.False(rendered.IsHeld, string.Join("; ", rendered.Holds));
        var submission = Guid.NewGuid();
        await _ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission,
            FlowId = _flow,
            FlowName = "cached-lookup-flow",
            MappingReference = "Thing@1.0.0",
            RenderContext = "{}",
            RecordCount = count,
            Status = SubmissionStatus.Planned,
            ReceivedUtc = _clock.GetUtcNow().UtcDateTime,
        });

        var setId = await _ledger.EnsureCacheSetAsync(Scope, rendered.CacheUsages);
        await _ledger.UpsertPendingAsync(_flow, Enumerable.Range(0, count).Select(i => new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", [$"{uwi}-{i}"]),
            FlowId = _flow,
            SourceKey = $"{uwi}-{i}",
            MappingName = "Thing",
            TargetId = $"dev:work-product-component--WellLog:{uwi.Replace(' ', '-').Replace(':', '-').Trim('-')}-{i}",
            LastSubmissionId = submission,
            PendingDocumentRef = "0:0:10",
            PendingRenderContext = "{}",
            PendingMetadataHash = "mh",
            PendingMetadata = true,
            CacheSetId = setId,
        }).ToList());
        return Viewers(rendered);
    }

    private Task<CacheImpactResult> AnalyzeAsync(ReferenceType previous, ReferenceType current)
        => new CacheImpactAnalyzer(_ledger, _clock, NullLogger.Instance).AnalyzeAsync(Scope, previous, current, CacheChangeMode.Auto, "v1", "v2");

    private async Task<UpdateTag> OnlyTagAsync() => Assert.Single(await _ledger.ListTagsAsync("approved", 10, 0));

    [Fact]
    public async Task A_group_a_lookup_table_moves_to_another_field_leaves_the_logs_of_the_field_it_left()
    {
        var before = GroupTable(("g1@x", FieldB), ("g2@x", FieldA));
        var viewers = await DeliveredAsync(Mapping(record: TableViewers), Cache(groups: before), "WELLBORE B-1", 3);
        Assert.Equal(["viewers@x", "g1@x"], viewers);

        var impact = await AnalyzeAsync(before, GroupTable(("g1@x", FieldA), ("g2@x", FieldA)));

        Assert.Equal(3, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal(("relisted", "field_id"), (tag.Change, tag.Path));
    }

    [Fact]
    public async Task A_group_a_lookup_table_adds_for_a_field_reaches_the_logs_built_without_it()
    {
        var before = GroupTable(("g1@x", FieldB));
        await DeliveredAsync(Mapping(record: TableViewers), Cache(groups: before), "WELLBORE B-1", 2);

        var impact = await AnalyzeAsync(before, GroupTable(("g1@x", FieldB), ("g3@x", FieldB)));

        Assert.Equal(2, impact.AffectedRecords);
        Assert.Equal("relisted", (await OnlyTagAsync()).Change);
    }

    [Fact]
    public async Task A_wellbore_a_log_named_by_its_id_before_the_cache_held_it_reaches_the_log_built_without_it()
    {
        var mapping = Mapping(record: TestSchema.Envelope, lookups: string.Empty, data: "Symbol: { $cache: Wellbore.FacilityName, $findBy: FacilityName = wellbore_uwi, $required: false }");
        await DeliveredAsync(mapping, Cache(), "dev:master-data--Wellbore:later:", 2);

        var impact = await AnalyzeAsync(Wellbores(), Wellbores(Item("dev:master-data--Wellbore:later", ("FacilityName", "\"WELLBORE L-1\""))));

        Assert.Equal(2, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal(("found", "dev:master-data--Wellbore:later"), (tag.Change, tag.ItemId));
    }

    [Fact]
    public async Task A_wellbore_whose_code_a_log_looked_for_by_id_reaches_the_log()
    {
        var mapping = Mapping(record: TestSchema.Envelope, lookups: string.Empty, data: "Symbol: { $cache: Wellbore.FacilityName, $findBy: id = wellbore_uwi, $required: false }");
        await DeliveredAsync(mapping, Cache(), "later", 2);

        var impact = await AnalyzeAsync(Wellbores(), Wellbores(Item("dev:master-data--Wellbore:later", ("FacilityName", "\"WELLBORE L-1\""))));

        Assert.Equal(2, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal(("listed", "dev:master-data--Wellbore:later", "id"), (tag.Change, tag.ItemId, tag.Path));
    }

    [Fact]
    public async Task A_wellbore_written_another_way_reaches_a_log_whose_lookup_folds_separators_away()
    {
        // The lookup ignores punctuation and spacing, and no wellbore is named WB Z-12 however it is written.
        var folding = Mapping(
            record: TestSchema.Envelope,
            lookups: Lookups.Replace("    $description:", "    $ignoreSeparators: true\n    $description:", StringComparison.Ordinal),
            data: "WellboreID: { $lookup: wellbore.id, $required: false }");
        var rendered = Renderer(folding).Render(Record("WB Z-12"));
        Assert.Contains(rendered.CacheUsages, u => u.Kind == CacheUsageKind.Unlisted && CacheUsage.IsComposite(u.ItemId) && u.Path == "FacilityName");
        await DeliveredAsync(folding, Cache(), "WB Z-12", 2);

        // A wellbore named WB_Z_12, which the lookup finds once separators are folded, reaches the records built without it.
        var impact = await AnalyzeAsync(Wellbores(), Wellbores(Item("dev:master-data--Wellbore:z12", ("FacilityName", "\"WB_Z_12\""))));

        Assert.Equal(2, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal(("listed", "dev:master-data--Wellbore:z12"), (tag.Change, tag.ItemId));
    }

    [Fact]
    public async Task A_wellbore_of_the_name_a_log_was_found_by_alias_reaches_the_log()
    {
        // WELLBORE A-1 is known by the alias FIELD A A1, which the log names; the line trying the name came first and found none.
        await DeliveredAsync(Mapping(), Cache(), "FIELD A A1", 2);

        var named = Wellbores(Item("dev:master-data--Wellbore:named", ("FacilityName", "\"FIELD A A1\"")));
        var impact = await AnalyzeAsync(Wellbores(), named);

        Assert.Equal(2, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal(("listed", "dev:master-data--Wellbore:named", "FacilityName"), (tag.Change, tag.ItemId, tag.Path));
    }
}
