using System.Text.Json;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A mapping that searches, as the preflight gate and the mapping builder see it: every lookup checked against the schema
/// its search pins before anything renders, and a document that survives being opened and written back by the builder.
/// </summary>
public class SearchMappingTests
{
    private const string ByNameThenAlias = """
          WellboreID:
            $search: Wellbore
            $findBy:
              - data.FacilityName = wb
              - data.NameAliases.AliasName = wb
        """;

    private static IReadOnlyList<ValidationIssue> Check(MappingDefinition mapping, ResolvedSearches? searches)
        => Preflight.Check(mapping, TestSchema.Build(), ReferenceSnapshot.Empty, TestSchema.Context(), sourceColumns: null, searches);

    private static IEnumerable<string> Errors(IReadOnlyList<ValidationIssue> issues)
        => issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message);

    [Fact]
    public void A_mapping_that_searches_is_not_passed_without_the_schemas_its_searches_pin()
    {
        var mapping = SearchSourceTests.Mapping(ByNameThenAlias);

        var error = Assert.Single(Errors(Check(mapping, searches: null)));

        Assert.Contains("the schemas those searches pin were not loaded", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_property_a_search_cannot_ask_for_fails_the_preflight_naming_the_entry_and_the_reason()
    {
        var mapping = SearchSourceTests.Mapping("""
              WellboreID:
                $search: Wellbore
                $findBy: data.SpudDate = wb
            """);

        var error = Assert.Single(Errors(Check(mapping, SearchSourceTests.Resolve(mapping))));

        Assert.Contains("record.data.WellboreID", error, StringComparison.Ordinal);
        Assert.Contains("search.Wellbore.data.SpudDate", error, StringComparison.Ordinal);
        Assert.Contains("indexes as a date", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_that_finds_another_kind_of_record_than_the_property_points_to_fails_the_preflight()
    {
        var mapping = SearchSourceTests.Mapping("""
              Unit:
                $search: Wellbore
                $findBy: data.FacilityName = wb
            """);

        var errors = Errors(Check(mapping, SearchSourceTests.Resolve(mapping))).ToList();

        Assert.Contains(
            errors,
            e => e.Contains("writes the id of a master-data--Wellbore record search 'Wellbore' finds, but the template points osdu.data.Unit to reference-data--UnitOfMeasure", StringComparison.Ordinal));
    }

    [Fact]
    public void A_search_cannot_fill_an_object_since_it_finds_one_id()
    {
        var mapping = SearchSourceTests.Mapping("""
              Nested:
                $search: Wellbore
                $findBy: data.FacilityName = wb
            """);

        var errors = Errors(Check(mapping, SearchSourceTests.Resolve(mapping))).ToList();

        Assert.Contains(errors, e => e.Contains("search.Wellbore.id is the id of the record a search finds, and osdu.data.Nested is an object", StringComparison.Ordinal));
    }

    [Fact]
    public void A_searching_mapping_opened_in_the_builder_is_written_back_as_the_same_mapping()
    {
        var loader = new DeliveryDocumentLoader();
        var original = loader.ParseMapping(SearchSourceTests.Document(ByNameThenAlias + "\n    $required: false"), "thing.yaml");
        var draft = MappingBuilder.FromDefinition(original);

        var yaml = MappingBuilder.ToYaml(draft);
        var reread = loader.ParseMapping(yaml, "thing.yaml");
        var again = MappingBuilder.FromDefinition(reread);

        var json = new JsonSerializerOptions { WriteIndented = false };
        Assert.Equal(JsonSerializer.Serialize(draft, json), JsonSerializer.Serialize(again, json));
        Assert.Equal(yaml, MappingBuilder.ToYaml(again));

        // The search, its pinned schema and the search entry all came back as they were.
        var search = Assert.Single(reread.Searches.Values);
        Assert.Equal(original.Searches["Wellbore"], search);
        var entry = Assert.Single(reread.Entries, e => e.Source?.Kind == MappingSourceKind.Search);
        Assert.Equal("search.Wellbore.id", entry.Source!.ToString());
        Assert.False(entry.Required);
        Assert.Equal(["data.FacilityName", "data.NameAliases.AliasName"], entry.FindBy.Select(f => f.Field));
        Assert.Empty(Errors(Check(reread, SearchSourceTests.Resolve(reread))));
    }

    [Fact]
    public void A_mapping_that_names_its_records_by_identity_columns_keeps_them_through_the_builder()
    {
        var loader = new DeliveryDocumentLoader();
        var original = loader.ParseMapping(
            TestSchema.MappingDocument().Replace("  key: [name]", "  key: [name]\n  identity: [name, depth]", StringComparison.Ordinal),
            "thing.yaml");
        Assert.Equal(["name", "depth"], original.Dataset.Identity);

        var reread = loader.ParseMapping(MappingBuilder.ToYaml(MappingBuilder.FromDefinition(original)), "thing.yaml");

        Assert.Equal(original.Dataset.Identity, reread.Dataset.Identity);
    }

    [Fact]
    public void The_builder_says_what_a_search_entry_still_lacks()
    {
        var draft = new MappingDraft
        {
            Name = "Thing",
            Version = "1.0.0",
            TemplateKind = TestSchema.Kind,
            TemplateVersion = TestSchema.Build().Version,
            System = "test",
            Key = ["name"],
            Searches = [new MappingDraftSearch("Wellbore", "osdu:wks:master-data--Wellbore:*", string.Empty, string.Empty, null)],
            Entries =
            [
                new MappingDraftEntry { Target = "osdu.data.WellboreID", Input = MappingDraftInput.Search, CacheType = "Nowhere", CacheField = "data.FacilityName", FindBy = [new MappingDraftFind("FacilityName", "wb", null)] },
                new MappingDraftEntry { Target = "osdu.data.Unit", Input = MappingDraftInput.Search, CacheType = "Wellbore", CacheField = "id" },
            ],
        };

        var issues = MappingBuilder.Incomplete(draft);

        Assert.Contains(issues, i => i.Message.Contains("pick the template whose schema says how the kind it searches is indexed", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.WellboreID" && i.Message.Contains("choose one of the mapping's searches", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.WellboreID" && i.Message.Contains("reads the id of the record it finds", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.WellboreID" && i.Message.Contains("compares a property under data", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Unit" && i.Message.Contains("at least one findBy line", StringComparison.Ordinal));
    }
}
