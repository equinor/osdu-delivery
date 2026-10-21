using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a property of a searched kind is indexed, read from its schema the way the indexer reads it
/// (<c>PropertiesProcessor</c>, <c>TypeMapper</c>): the shape decides the query, so a shape read wrongly is a lookup that
/// silently finds nothing.
/// </summary>
public class SearchFieldsTests
{
    private static (OsduField? Field, string? Problem) Classify(string path) => SearchFields.Classify(SearchSourceTests.WellboreSchema(), path);

    [Theory]
    [InlineData("data.FacilityName")]
    // Any format but a date one is indexed as text, as the indexer maps a storage type it does not know.
    [InlineData("data.Uri")]
    // An OSDU id reference is text: only a pattern starting ^srn makes a link.
    [InlineData("data.WellID")]
    // An object's properties are indexed under dotted paths.
    [InlineData("data.Location.Label")]
    // A list is typed by its items.
    [InlineData("data.Codes")]
    public void A_string_is_text_with_a_keyword_sub_field(string path)
    {
        var (field, problem) = Classify(path);

        Assert.Null(problem);
        Assert.Equal(OsduField.Text(path), field);
    }

    [Theory]
    [InlineData("data.LegacyRef")]
    [InlineData("data.LegacyRefs")]
    public void A_legacy_srn_link_is_a_bare_keyword(string path)
        => Assert.Equal(OsduField.Keyword(path), Classify(path).Field);

    [Fact]
    public void A_property_of_a_nested_array_is_asked_inside_that_array()
    {
        Assert.Equal(OsduField.Text("data.NameAliases.AliasName", "data.NameAliases"), Classify("data.NameAliases.AliasName").Field);

        // An object inside the nested items is still inside the array.
        Assert.Equal(
            OsduField.Text("data.VerticalMeasurements.Reference.Label", "data.VerticalMeasurements"),
            Classify("data.VerticalMeasurements.Reference.Label").Field);
    }

    [Theory]
    [InlineData("data.FacilitySpecifications.FacilitySpecificationText")]
    // Everything inside a flattened array is a keyword, a number or a deeper object's value alike.
    [InlineData("data.FacilitySpecifications.Size")]
    [InlineData("data.FacilitySpecifications.Inner.Deep")]
    public void A_value_inside_a_flattened_array_is_a_keyword(string path)
        => Assert.Equal(OsduField.Keyword(path), Classify(path).Field);

    [Theory]
    [InlineData("data.Plain.Code", "no x-osdu-indexing hint")]
    [InlineData("data.VerticalMeasurements.Readings.Value", "a nested array inside the nested array")]
    [InlineData("data.SpudDate", "indexes as a date")]
    [InlineData("data.TotalDepth", "a search compares text")]
    [InlineData("data.Count", "indexes as a number")]
    [InlineData("data.Depths", "list of number values")]
    [InlineData("data.Location", "is an object, not a value")]
    [InlineData("data.NameAliases", "list of objects, not a value")]
    [InlineData("data.FacilitySpecifications.Inner", "an object inside a flattened array")]
    [InlineData("data.Codes.Anything", "a list of values, which has no properties")]
    [InlineData("data.TotalDepth.Anything", "has no properties to compare")]
    [InlineData("data.Untyped", "declares no type")]
    [InlineData("data.NoSuchThing", "has no property data.NoSuchThing")]
    [InlineData("data.NoSuchThing.Deeper", "has no property data.NoSuchThing")]
    public void A_property_no_exact_query_can_reach_is_refused_with_the_reason(string path, string reason)
    {
        var (field, problem) = Classify(path);

        Assert.Null(field);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bundled_wellbore_schema_indexes_names_as_text_and_aliases_as_a_nested_array()
    {
        // The schema the sample mappings' searches pin, as the platform publishes it.
        var wellbore = Samples.SampleTemplate(Samples.WellboreKind);

        Assert.Equal(OsduField.Text("data.FacilityName"), SearchFields.Classify(wellbore, "data.FacilityName").Field);
        Assert.Equal(OsduField.Text("data.NameAliases.AliasName", "data.NameAliases"), SearchFields.Classify(wellbore, "data.NameAliases.AliasName").Field);
        Assert.Equal(
            OsduField.Keyword("data.FacilitySpecifications.FacilitySpecificationText"),
            SearchFields.Classify(wellbore, "data.FacilitySpecifications.FacilitySpecificationText").Field);
        Assert.Contains("a search compares text", SearchFields.Classify(wellbore, "data.VerticalMeasurements.VerticalMeasurement").Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data.FacilityName", OsduFieldIndex.Text, false)]
    [InlineData("data.LegacyRef", OsduFieldIndex.Keyword, false)]
    // A value, rather than a text comparison, may be a number, a date or a boolean, as the indexer stores it.
    [InlineData("data.TotalDepth", OsduFieldIndex.Number, false)]
    [InlineData("data.Count", OsduFieldIndex.Number, false)]
    [InlineData("data.SpudDate", OsduFieldIndex.Date, false)]
    // A list holds several values in one record.
    [InlineData("data.Codes", OsduFieldIndex.Text, true)]
    [InlineData("data.LegacyRefs", OsduFieldIndex.Keyword, true)]
    [InlineData("data.Depths", OsduFieldIndex.Number, true)]
    // So does an array of objects, nested or flattened; a flattened one holds keywords whatever their type.
    [InlineData("data.FacilitySpecifications.Size", OsduFieldIndex.Keyword, true)]
    [InlineData("data.Location.Label", OsduFieldIndex.Text, false)]
    public void A_value_is_read_as_the_index_stores_it_and_says_whether_a_record_holds_several(string path, OsduFieldIndex index, bool repeats)
    {
        var shape = SearchFields.ClassifyValue(SearchSourceTests.WellboreSchema(), path);

        Assert.Null(shape.Problem);
        Assert.Equal(OsduField.Of(path, index), shape.Field);
        Assert.Equal(repeats, shape.Repeats);
    }

    [Fact]
    public void A_value_inside_a_nested_array_is_read_through_it_and_repeats()
    {
        var shape = SearchFields.ClassifyValue(SearchSourceTests.WellboreSchema(), "data.VerticalMeasurements.VerticalMeasurementID");

        Assert.Equal(OsduField.Text("data.VerticalMeasurements.VerticalMeasurementID", "data.VerticalMeasurements"), shape.Field);
        Assert.True(shape.Repeats);
    }

    [Fact]
    public void A_boolean_and_a_number_inside_a_nested_array_are_values_too()
    {
        var schema = Snapshots.SchemaSnapshot.Parse("osdu:wks:work-product-component--WellLog:1.0.0", """
            {
              "type": "object",
              "properties": {
                "data": {
                  "type": "object",
                  "properties": {
                    "IsActive": { "type": "boolean" },
                    "Curves": {
                      "type": "array",
                      "x-osdu-indexing": { "type": "nested" },
                      "items": { "type": "object", "properties": { "Mnemonic": { "type": "string" }, "Depth": { "type": "integer" } } }
                    }
                  }
                }
              }
            }
            """, DateTimeOffset.UnixEpoch);

        Assert.Equal(OsduField.Boolean("data.IsActive"), SearchFields.ClassifyValue(schema, "data.IsActive").Field);
        Assert.Equal(OsduField.Number("data.Curves.Depth", "data.Curves"), SearchFields.ClassifyValue(schema, "data.Curves.Depth").Field);

        // A lookup compares text, so for it neither is a property it can ask for.
        Assert.Null(SearchFields.Classify(schema, "data.IsActive").Field);
    }

    [Theory]
    [InlineData("data.Plain.Code", "no x-osdu-indexing hint")]
    [InlineData("data.VerticalMeasurements.Readings.Value", "a nested array inside the nested array")]
    [InlineData("data.Location", "is an object, not a value")]
    [InlineData("data.NameAliases", "list of objects, not a value")]
    [InlineData("data.Untyped", "declares no type")]
    [InlineData("data.NoSuchThing", "has no property data.NoSuchThing")]
    public void A_value_the_index_holds_no_exact_form_of_is_refused_with_the_reason(string path, string reason)
    {
        var shape = SearchFields.ClassifyValue(SearchSourceTests.WellboreSchema(), path);

        Assert.Null(shape.Field);
        Assert.Contains(reason, shape.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kind", OsduFieldIndex.Keyword, false)]
    [InlineData("id", OsduFieldIndex.Keyword, false)]
    [InlineData("authority", OsduFieldIndex.Keyword, false)]
    [InlineData("version", OsduFieldIndex.Number, false)]
    [InlineData("createTime", OsduFieldIndex.Date, false)]
    [InlineData("createUser", OsduFieldIndex.Keyword, false)]
    [InlineData("acl.viewers", OsduFieldIndex.Keyword, true)]
    [InlineData("acl.owners", OsduFieldIndex.Keyword, true)]
    [InlineData("legal.legaltags", OsduFieldIndex.Keyword, true)]
    [InlineData("legal.otherRelevantDataCountries", OsduFieldIndex.Keyword, true)]
    [InlineData("legal.status", OsduFieldIndex.Keyword, false)]
    [InlineData("ancestry.parents", OsduFieldIndex.Keyword, true)]
    [InlineData("index.statusCode", OsduFieldIndex.Number, false)]
    [InlineData("tags.Source", OsduFieldIndex.Keyword, false)]
    public void A_property_of_the_record_itself_is_read_as_the_indexer_maps_it_for_every_kind(string path, OsduFieldIndex index, bool repeats)
    {
        var shape = SearchFields.RecordProperty(path);

        Assert.Null(shape.Problem);
        Assert.Equal(OsduField.Of(path, index), shape.Field);
        Assert.Equal(repeats, shape.Repeats);
    }

    [Theory]
    [InlineData("tags", "name one tag")]
    [InlineData("tags.Source.Inner", "reaches inside the tag")]
    [InlineData("acl", "under acl it holds acl.viewers, acl.owners")]
    [InlineData("legal.somethingElse", "under legal it holds")]
    [InlineData("meta", "not a property the index holds")]
    [InlineData("data.FacilityName", "the kind's schema describes")]
    [InlineData("x-acl", "not a property path")]
    public void A_property_of_the_record_the_index_holds_no_value_of_is_refused_with_the_reason(string path, string reason)
    {
        var shape = SearchFields.RecordProperty(path);

        Assert.Null(shape.Field);
        Assert.Contains(reason, shape.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mapping_is_resolved_against_the_schema_its_search_pins_and_every_problem_is_listed()
    {
        var mapping = SearchSourceTests.Mapping("""
              WellboreID:
                $search: Wellbore
                $findBy:
                  - data.FacilityName = wb
                  - data.SpudDate = wb
                  - data.Plain.Code = wb
            """);

        var resolved = SearchSourceTests.Resolve(mapping);

        Assert.True(resolved.TryField("Wellbore", "data.FacilityName", out _, out var name));
        Assert.Equal(OsduField.Text("data.FacilityName"), name);
        Assert.Equal(2, resolved.Problems.Count);
        Assert.Contains(resolved.Problems, p => p.Contains("search.Wellbore.data.SpudDate", StringComparison.Ordinal) && p.Contains("date", StringComparison.Ordinal));
        Assert.Contains(resolved.Problems, p => p.Contains("search.Wellbore.data.Plain.Code", StringComparison.Ordinal) && p.Contains("x-osdu-indexing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_search_whose_pinned_schema_is_not_read_is_named_with_why()
    {
        var mapping = SearchSourceTests.Mapping("""
              WellboreID:
                $search: Wellbore
                $findBy: data.FacilityName = wb
            """);
        var schema = mapping.Searches["Wellbore"].Schema;

        // Why it was not read is what the source of templates said: the folders on the mapping's path, or the library.
        var why = "no template folder on the path of mappings/Thing@1.0.0.yaml holds template it. Put the template's file in one of them.";
        var resolved = ResolvedSearches.Resolve(
            mapping, new Dictionary<Templates.TemplateReference, Snapshots.SchemaSnapshot>(), new Dictionary<Templates.TemplateReference, string> { [schema] = why });
        var problem = Assert.Single(resolved.Problems);
        Assert.EndsWith($"search 'Wellbore' cannot read the template it pins, {schema}: {why}", problem, StringComparison.Ordinal);
        Assert.Empty(resolved.Searches);

        // Given no reason, it still says which template it could not read.
        var unexplained = Assert.Single(ResolvedSearches.Resolve(mapping, new Dictionary<Templates.TemplateReference, Snapshots.SchemaSnapshot>()).Problems);
        Assert.EndsWith($"search 'Wellbore' cannot read the template it pins, {schema}.", unexplained, StringComparison.Ordinal);
    }

    /// <summary>The sample estate's saved wellbore template, as OSDU publishes it: GeoContexts a nested list of a choice of contexts.</summary>
    private static SchemaSnapshot PublishedWellbore()
    {
        const string kind = "osdu:wks:master-data--Wellbore:1.3.0";
        var file = Path.Combine(Samples.TemplateFiles, kind.Replace(':', '_') + ".json");
        return SchemaSnapshot.Parse(kind, File.ReadAllText(file), DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void A_property_one_form_of_a_choice_declares_is_classified_through_the_choice()
    {
        // A wellbore's GeoContexts holds one of several kinds of context (oneOf); only some declare a property, and the list
        // is nested whichever form an item takes.
        var wellbore = PublishedWellbore();
        Assert.Equal(OsduField.Text("data.GeoContexts.GeoPoliticalEntityID", "data.GeoContexts"), SearchFields.Classify(wellbore, "data.GeoContexts.GeoPoliticalEntityID").Field);
        Assert.Equal(OsduField.Text("data.GeoContexts.FieldID", "data.GeoContexts"), SearchFields.ClassifyValue(wellbore, "data.GeoContexts.FieldID").Field);
        Assert.Equal(new SectionShape("data.GeoContexts", null, null), SearchFields.Section(wellbore, "data.GeoContexts"));

        // A property no form declares is still not one.
        Assert.Contains("has no property data.GeoContexts.NoSuchProperty", SearchFields.Classify(wellbore, "data.GeoContexts.NoSuchProperty").Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_is_classified_by_the_arrays_it_is_or_sits_in()
    {
        var wellbore = PublishedWellbore();
        Assert.Equal(new SectionShape("data.NameAliases", null, null), SearchFields.Section(wellbore, "data.NameAliases"));
        Assert.Equal(new SectionShape(null, "data.FacilitySpecifications", null), SearchFields.Section(wellbore, "data.FacilitySpecifications"));
        Assert.Equal(new SectionShape(null, null, null), SearchFields.Section(wellbore, "data.ResourceHostRegionIDs"));
        Assert.Contains("a value rather than a section", SearchFields.Section(wellbore, "data.FacilityName").Problem, StringComparison.Ordinal);
        Assert.Contains("has no property data.Nothing", SearchFields.Section(wellbore, "data.Nothing").Problem, StringComparison.Ordinal);
    }
}
