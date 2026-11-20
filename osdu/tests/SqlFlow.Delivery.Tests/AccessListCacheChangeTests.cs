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
/// What a refresh of the wellbores or of the access groups does to well logs already delivered with the viewers they
/// gave: a group the data office lists for a field later, a wellbore loaded after its logs, a wellbore given its field, and a
/// country group that comes to name a field each reach the records built without them, and a change nothing read reaches
/// none. Every record is judged from the dependency set its render left behind, never by rendering the estate again.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class AccessListCacheChangeTests : IAsyncLifetime, IDisposable
{
    private const string Scope = "dev";

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();
    private readonly Guid _flow = FlowId.Of("access-list-flow");
    private readonly OsduLedger _ledger;

    public AccessListCacheChangeTests() => _ledger = _db.Ledger(_clock);

    public Task InitializeAsync() => _ledger.RegisterAsync(_flow);

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _db.Dispose();

    /// <summary>Renders a log of the wellbore named <paramref name="uwi"/> and delivers <paramref name="count"/> records built from what it read.</summary>
    private async Task<IReadOnlyList<CacheUsage>> DeliveredAsync(string uwi, int count, bool optionalWellbore = false, ReferenceSnapshot? cache = null, MappingDefinition? mapping = null)
    {
        mapping ??= optionalWellbore ? Mapping(data: "WellboreID: { $lookup: wellbore.id, $required: false }") : Mapping();
        var usages = Renderer(mapping, cache).Render(Record(uwi)).CacheUsages;
        var submission = Guid.NewGuid();
        var now = _clock.GetUtcNow().UtcDateTime;
        await _ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission,
            FlowId = _flow,
            FlowName = "access-list-flow",
            MappingReference = "Thing@1.0.0",
            RenderContext = "{}",
            RecordCount = count,
            Status = SubmissionStatus.Planned,
            ReceivedUtc = now,
        });

        var setId = await _ledger.EnsureCacheSetAsync(Scope, usages);
        var records = Enumerable.Range(0, count).Select(i => new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", [$"{uwi}-{i}"]),
            FlowId = _flow,
            SourceKey = $"{uwi}-{i}",
            MappingName = "Thing",
            TargetId = $"dev:work-product-component--WellLog:{uwi.Replace(' ', '-').Replace('/', '-')}-{i}",
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

    private async Task<UpdateTag> OnlyTagAsync() => Assert.Single(await _ledger.ListTagsAsync("approved", 10, 0));

    [Fact]
    public async Task A_group_the_data_office_lists_for_a_field_later_reaches_every_log_built_without_it()
    {
        await DeliveredAsync("WELLBORE B-1", 4);

        var impact = await AnalyzeAsync(Groups(), Groups(Item("dev:reference-data--AccessGroup:field-b-extra",
            ("FieldIDList", $"[\"{FieldB}\"]"), ("FieldList", "[\"FIELD B\"]"),
            ("EntitlementGroupEmail", "\"data.office.field.b.partners.viewers@x\""))));

        Assert.Equal(4, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal("relisted", tag.Change);
        Assert.Equal("FieldIDList", tag.Path);
        Assert.Equal(CacheUsage.ListingKey(FieldB), tag.ItemId);
        Assert.Equal(string.Empty, tag.OldValue ?? string.Empty);
        Assert.Equal("dev:reference-data--AccessGroup:field-b-extra", tag.NewValue);
        Assert.Contains("AccessGroup rows whose FieldIDList holds", tag.Describe(), StringComparison.Ordinal);
        Assert.Equal(4, tag.AffectedRecords);
    }

    [Fact]
    public async Task A_group_listed_for_a_field_no_log_lies_in_reaches_none_of_them()
    {
        await DeliveredAsync("WELLBORE B-1", 2);

        var impact = await AnalyzeAsync(Groups(), Groups(Item("dev:reference-data--AccessGroup:elsewhere",
            ("FieldIDList", "[\"dev:master-data--Field:ELSEWHERE:\"]"), ("FieldList", "[\"ELSEWHERE\"]"),
            ("EntitlementGroupEmail", "\"data.office.elsewhere.viewers@x\""))));

        Assert.Equal(0, impact.AffectedRecords);
        Assert.Empty(await _ledger.ListTagsAsync(null, 10, 0));
    }

    [Fact]
    public async Task A_country_group_that_comes_to_name_a_field_stops_being_a_country_group_of_the_logs_it_reached()
    {
        await DeliveredAsync("WELLBORE X-1", 3);

        var narrowed = new ReferenceType("AccessGroup", "reference-data--AccessGroup", Groups().Items.Select(item => item.Id.EndsWith(":us-contractors", StringComparison.Ordinal)
            ? Item(item.Id, ("GeoPoliticalEntityID", $"\"{UnitedStates}\""), ("FieldList", "[\"FIELD D\"]"), ("EntitlementGroupEmail", "\"data.office.us.contractors.viewers@x\""))
            : item));
        var impact = await AnalyzeAsync(Groups(), narrowed);

        Assert.Equal(3, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal("dev:reference-data--AccessGroup:us-contractors", tag.ItemId);
        Assert.Equal("FieldList", tag.Path);
        Assert.Equal("FIELD D", tag.NewValue);
    }

    [Fact]
    public async Task A_group_renamed_to_say_its_country_reaches_the_logs_its_old_name_kept_it_from()
    {
        var mapping = Mapping(record: FindAllContainsTests.CountryGroups);
        await DeliveredAsync("WELLBORE X-1", 2, cache: Cache(groups: FindAllContainsTests.NamedGroups()), mapping: mapping);

        var renamed = FindAllContainsTests.NamedGroups(item => item.Id.EndsWith(":us-gulf", StringComparison.Ordinal)
            ? FindAllContainsTests.Group(item.Id, "dataoffice united states gulf", "data.office.gulf.viewers@x")
            : item);
        var impact = await AnalyzeAsync(FindAllContainsTests.NamedGroups(), renamed);

        Assert.Equal(2, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal(("changed", "dev:reference-data--AccessGroup:us-gulf", "GroupName"), (tag.Change, tag.ItemId, tag.Path));
        Assert.Equal("dataoffice united states gulf", tag.NewValue);
    }

    [Fact]
    public async Task A_group_s_changed_address_reaches_the_logs_that_carry_it()
    {
        await DeliveredAsync("WELLBORE B-1", 2);

        var renamed = new ReferenceType("AccessGroup", "reference-data--AccessGroup", Groups().Items.Select(item => item.Id.EndsWith(":us-permanent", StringComparison.Ordinal)
            ? Item(item.Id, ("GeoPoliticalEntityID", $"\"{UnitedStates}\""), ("EntitlementGroupEmail", "\"data.office.us.employees.viewers@x\""))
            : item));
        await AnalyzeAsync(Groups(), renamed);

        var tag = await OnlyTagAsync();
        Assert.Equal("changed", tag.Change);
        Assert.Equal("EntitlementGroupEmail", tag.Path);
        Assert.Equal("data.office.us.viewers@x", tag.OldValue);
        Assert.Equal("data.office.us.employees.viewers@x", tag.NewValue);
    }

    [Fact]
    public async Task A_wellbore_loaded_after_its_logs_reaches_them_by_the_name_they_found_no_wellbore_by()
    {
        await DeliveredAsync("WELLBORE Z-9", 2, optionalWellbore: true);

        var impact = await AnalyzeAsync(Wellbores(), Wellbores(Item("dev:master-data--Wellbore:late",
            ("FacilityName", "\"WELLBORE Z-9\""),
            ("GeoContexts.FieldID", $"[\"{FieldB}\"]"))));

        Assert.Equal(2, impact.AffectedRecords);
        var tags = await _ledger.ListTagsAsync("approved", 10, 0);
        var tag = Assert.Single(tags, t => t.Path == "FacilityName");
        Assert.Equal("listed", tag.Change);
        Assert.Equal("dev:master-data--Wellbore:late", tag.ItemId);
        Assert.Equal("WELLBORE Z-9", tag.NewValue);
        Assert.Contains("Wellbore now lists 'WELLBORE Z-9' as 'dev:master-data--Wellbore:late'", tag.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wellbore_given_its_field_reaches_the_logs_built_without_the_field_s_groups()
    {
        await DeliveredAsync("WELLBORE X-1", 2);

        var placed = new ReferenceType("Wellbore", "master-data--Wellbore", Wellbores().Items.Select(item => item.Id.EndsWith(":nofield", StringComparison.Ordinal)
            ? Item(item.Id, ("FacilityName", "\"WELLBORE X-1\""), ("GeoContexts.FieldID", $"[\"{FieldB}\"]"), ("GeoContexts.GeoPoliticalEntityID", $"[\"{UnitedStates}\"]"))
            : item));
        var impact = await AnalyzeAsync(Wellbores(), placed);

        Assert.Equal(2, impact.AffectedRecords);
        var tag = await OnlyTagAsync();
        Assert.Equal("changed", tag.Change);
        Assert.Equal("GeoContexts.FieldID", tag.Path);
        Assert.Null(tag.OldValue);
        Assert.Contains(FieldB, tag.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_gaps_name_what_logs_were_built_without_and_how_many_most_records_first()
    {
        // Three logs of a field no group lists yet, two of a wellbore the cache does not hold, one complete, and one of a
        // wellbore whose GeoContexts name its country and an area no access group names.
        await DeliveredAsync("WELLBORE N-1", 3, cache: Cache(Wellbores(Item("dev:master-data--Wellbore:new1",
            ("FacilityName", "\"WELLBORE N-1\""),
            ("GeoContexts.FieldID", "[\"dev:master-data--Field:NEW:\"]")))));
        await DeliveredAsync("WELLBORE Z-9", 2, optionalWellbore: true);
        await DeliveredAsync("WELLBORE B-1", 1);
        await DeliveredAsync("WELLBORE E-1", 1, cache: Cache(Wellbores(Item("dev:master-data--Wellbore:area1",
            ("FacilityName", "\"WELLBORE E-1\""),
            ("GeoContexts.FieldID", $"[\"{FieldB}\"]"),
            ("GeoContexts.GeoPoliticalEntityID", $"[\"{UnitedStates}\", \"dev:master-data--GeoPoliticalEntity:AREA-A:\"]")))));

        var listing = await _ledger.ListCacheGapsAsync(Scope, typeName: null, empty: false, take: 50, skip: 0);
        var gaps = listing.Items;
        Assert.Equal(gaps.Count, listing.Total);

        var field = Assert.Single(gaps, g => g.TypeName == "AccessGroup" && g.Path == "FieldIDList" && g.Key == CacheUsage.ListingKey("dev:master-data--Field:NEW:"));
        Assert.Equal(CacheUsageKind.Listed, field.Kind);
        Assert.Equal(3, field.Records);
        var wellbore = Assert.Single(gaps, g => g.TypeName == "Wellbore" && g.Path == "FacilityName");
        Assert.Equal(CacheUsageKind.Unlisted, wellbore.Kind);
        Assert.Equal("WELLBORE Z-9", wellbore.Value);
        Assert.Equal(2, wellbore.Records);
        Assert.Equal(gaps.Max(g => g.Records), gaps[0].Records);

        // A complete log leaves no gap, and a key that found its rows is none either, in whichever form it found them: the
        // field's groups name it without the version separator and the country's with it.
        Assert.DoesNotContain(gaps, g => g.Key == CacheUsage.ListingKey(FieldB.TrimEnd(':')) || g.Key == CacheUsage.ListingKey(FieldB));
        Assert.DoesNotContain(gaps, g => g.Key == CacheUsage.ListingKey(UnitedStates.TrimEnd(':')) || g.Key == CacheUsage.ListingKey(UnitedStates));

        // A key no form of which found a row is one gap, not one per form.
        Assert.DoesNotContain(gaps, g => g.Key == CacheUsage.ListingKey("dev:master-data--Field:NEW"));

        // The country's groups were found, so the area beside it, which no group names, left the log wanting nothing.
        Assert.DoesNotContain(gaps, g => g.Key.Contains("AREA-A", StringComparison.Ordinal));

        // A page is a slice of the same order, and every page counts them all.
        var second = await _ledger.ListCacheGapsAsync(Scope, typeName: null, empty: false, take: 1, skip: 1);
        Assert.Equal(listing.Total, second.Total);
        Assert.Equal(gaps[1], Assert.Single(second.Items));
        Assert.Empty((await _ledger.ListCacheGapsAsync(Scope, typeName: null, empty: false, take: 50, skip: listing.Total)).Items);

        // One type's gaps, and the paths read empty beside them: the wellbore the new field's log lies in has no country.
        Assert.All((await _ledger.ListCacheGapsAsync(Scope, "Wellbore", empty: false, take: 50, skip: 0)).Items, g => Assert.Equal("Wellbore", g.TypeName));
        var withEmpty = (await _ledger.ListCacheGapsAsync(Scope, "Wellbore", empty: true, take: 50, skip: 0)).Items;
        Assert.Contains(withEmpty, g => g.Kind == CacheUsageKind.Empty && g.Path == "GeoContexts.GeoPoliticalEntityID" && g.Key == "dev:master-data--Wellbore:new1");

        // The data office lists a group for the new field: the refresh raises a change for the three logs, and what was
        // missing is listed with the changes from then on, not as missing.
        await AnalyzeAsync(Groups(), Groups(Item("dev:reference-data--AccessGroup:new",
            ("FieldIDList", "[\"dev:master-data--Field:NEW:\"]"), ("FieldList", "[\"NEW\"]"),
            ("EntitlementGroupEmail", "\"data.office.new.viewers@x\""))));
        Assert.Equal(3, Assert.Single(await _ledger.ListTagsAsync("approved", 10, 0), t => t.Path == "FieldIDList").AffectedRecords);
        Assert.DoesNotContain((await _ledger.ListCacheGapsAsync(Scope, typeName: null, empty: false, take: 50, skip: 0)).Items, g => g.Path == "FieldIDList");
    }

    [Fact]
    public async Task A_listing_survives_the_ledger_as_the_render_wrote_it()
    {
        var usages = await DeliveredAsync("WELLBORE B-1", 1);
        var set = await _ledger.EnsureCacheSetAsync(Scope, usages);
        var entries = await _ledger.ListCacheSetAsync(set);

        // The country is asked for as the wellbore writes it and without its version separator: two keys, one listing each.
        var listings = entries.Where(e => e.Kind == CacheUsageKind.Listed && e.Path == "GeoPoliticalEntityID").ToList();
        Assert.Equal([CacheUsage.ListingKey(UnitedStates.TrimEnd(':')), CacheUsage.ListingKey(UnitedStates)], listings.Select(e => e.ItemId).Order(StringComparer.Ordinal));
        var listing = Assert.Single(listings, e => e.ItemId == CacheUsage.ListingKey(UnitedStates));
        Assert.Equal(
            "dev:reference-data--AccessGroup:field-b, dev:reference-data--AccessGroup:us-contractors, dev:reference-data--AccessGroup:us-permanent",
            listing.ValueText);
    }
}
