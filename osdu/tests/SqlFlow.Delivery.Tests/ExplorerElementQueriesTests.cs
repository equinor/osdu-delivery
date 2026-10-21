using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The query the explorer shows for an element of a record (osdu/docs/reference/concepts/explorer.md, A record): the
/// one that finds the records holding exactly that value, list, object or nested item, written as the module's own
/// searches are, by how the template of the record's kind has the platform index each value (text by its keyword
/// sub-field, a keyword, a number; in a nested list inside nested(...), an item's values together; in a flattened
/// list); written from the value and said to be a guess where the template cannot say, so there is always a query to
/// see.
/// </summary>
public class ExplorerElementQueriesTests
{
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";
    private const string UnitedStates = "dev:master-data--GeoPoliticalEntity:UnitedStates:";
    private const string Country = "dev:reference-data--GeoPoliticalEntityType:Country:";
    private const string CountyA = "dev:master-data--GeoPoliticalEntity:CountyA:";
    private const string County = "dev:reference-data--GeoPoliticalEntityType:County:";

    private static SchemaSnapshot Schema(string kind)
    {
        var file = Path.Combine(Samples.TemplateFiles, kind.Replace(':', '_') + ".json");
        return SchemaSnapshot.Parse(kind, File.ReadAllText(file), DateTimeOffset.UnixEpoch);
    }

    private static readonly SchemaSnapshot Wellbore = Schema(WellboreKind);

    private static ExplorerElementRequest Value(string path, object? value, string kind = WellboreKind) => new()
    {
        Kind = kind,
        Path = path,
        Value = JsonSerializer.SerializeToElement(value),
    };

    private static ExplorerElementRequest Section(string path, string kind = WellboreKind, params (string Path, object? Value)[] values) => new()
    {
        Kind = kind,
        Path = path,
        Section = true,
        Values = values.Select(v => new ExplorerElementLeaf(v.Path, JsonSerializer.SerializeToElement(v.Value))).ToList(),
    };

    private static Dictionary<string, string> Queries(ExplorerElementAnswer answer) => answer.Queries.ToDictionary(q => q.Purpose, q => q.Query);

    [Fact]
    public void A_text_is_found_exactly_by_its_keyword_by_its_words_and_by_being_there()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.FacilityName", "WELLBORE A/2-9 S"), Wellbore);

        Assert.Null(answer.Problem);
        Assert.Null(answer.Guess);
        Assert.Equal(WellboreKind, answer.Template!.Kind);
        Assert.Equal(new ExplorerElementField("data.FacilityName", "text", null, null), answer.Field);
        Assert.Equal("exact", answer.Queries[0].Purpose);
        var queries = Queries(answer);
        Assert.Equal("data.FacilityName.keyword:\"WELLBORE A/2-9 S\"", queries["exact"]);
        Assert.Equal("data.FacilityName:\"WELLBORE A/2-9 S\"", queries["words"]);
        Assert.Equal("_exists_:data.FacilityName", queries["exists"]);
        Assert.Equal("The records whose FacilityName is exactly \"WELLBORE A/2-9 S\", the whole value as written.", answer.Queries[0].Says);
        Assert.Contains("data.FacilityName.keyword, which an exact match asks", answer.Reading, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_of_a_nested_list_is_found_inside_nested_and_whether_one_is_there_is_not_asked()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.NameAliases[2].AliasName", "WELLBORE A/2-9 S"), Wellbore);

        var queries = Queries(answer);
        Assert.Equal("nested(data.NameAliases, (AliasName.keyword:\"WELLBORE A/2-9 S\"))", queries["exact"]);
        Assert.Equal("nested(data.NameAliases, (AliasName:\"WELLBORE A/2-9 S\"))", queries["words"]);
        Assert.False(queries.ContainsKey("exists"));
        Assert.Equal(("data.NameAliases.AliasName", "data.NameAliases"), (answer.Field!.Path, answer.Field.NestedPath));
        Assert.StartsWith("The records holding \"WELLBORE A/2-9 S\" as one of their AliasName values", answer.Queries[0].Says, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_one_form_of_a_choice_declares_is_found_inside_its_nested_list_and_an_id_has_no_words()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.GeoContexts[3].GeoPoliticalEntityID", UnitedStates), Wellbore);

        Assert.Equal($"nested(data.GeoContexts, (GeoPoliticalEntityID.keyword:\"{UnitedStates}\"))", Queries(answer)["exact"]);
        Assert.Equal("data.GeoContexts", answer.Field!.NestedPath);
        Assert.False(Queries(answer).ContainsKey("words"));
    }

    [Fact]
    public void A_number_is_compared_as_one_and_has_no_words()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.VerticalMeasurements[0].VerticalMeasurement", 25.5), Wellbore);

        Assert.Equal("number", answer.Field!.Index);
        var queries = Queries(answer);
        Assert.Equal("nested(data.VerticalMeasurements, (VerticalMeasurement:\"25.5\"))", queries["exact"]);
        Assert.False(queries.ContainsKey("words"));
    }

    [Fact]
    public void A_value_of_a_flattened_list_is_a_keyword_under_its_dotted_path()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.FacilitySpecifications[0].FacilitySpecificationText", "Rated"), Wellbore);

        Assert.Equal(new ExplorerElementField("data.FacilitySpecifications.FacilitySpecificationText", "keyword", null, "data.FacilitySpecifications"), answer.Field);
        Assert.Equal("data.FacilitySpecifications.FacilitySpecificationText:\"Rated\"", Queries(answer)["exact"]);
        Assert.Contains("flattened list data.FacilitySpecifications", answer.Reading, StringComparison.Ordinal);
    }

    [Fact]
    public void An_item_of_a_nested_list_is_found_by_all_its_values_together_inside_one_nested_query()
    {
        var answer = ExplorerElementQueries.Describe(
            Section("data.GeoContexts[3]", WellboreKind, ("data.GeoContexts[3].GeoPoliticalEntityID", UnitedStates), ("data.GeoContexts[3].GeoTypeID", Country)), Wellbore);

        Assert.Equal($"nested(data.GeoContexts, (GeoPoliticalEntityID.keyword:\"{UnitedStates}\" AND GeoTypeID.keyword:\"{Country}\"))", Queries(answer)["exact"]);
        Assert.False(Queries(answer).ContainsKey("exists"));
        Assert.Null(answer.Guess);
        Assert.Contains("the values of each item of a nested list together in one item", answer.Queries[0].Says, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_nested_list_is_found_by_each_of_its_items()
    {
        var answer = ExplorerElementQueries.Describe(
            Section(
                "data.GeoContexts",
                WellboreKind,
                ("data.GeoContexts[0].GeoPoliticalEntityID", CountyA),
                ("data.GeoContexts[0].GeoTypeID", County),
                ("data.GeoContexts[1].GeoPoliticalEntityID", UnitedStates),
                ("data.GeoContexts[1].GeoTypeID", Country)),
            Wellbore);

        Assert.Equal(
            $"(nested(data.GeoContexts, (GeoPoliticalEntityID.keyword:\"{CountyA}\" AND GeoTypeID.keyword:\"{County}\")))"
            + $" AND (nested(data.GeoContexts, (GeoPoliticalEntityID.keyword:\"{UnitedStates}\" AND GeoTypeID.keyword:\"{Country}\")))",
            Queries(answer)["exact"]);
        Assert.Contains(answer.Notes, n => n.Contains("data.GeoContexts is a nested list", StringComparison.Ordinal));
    }

    [Fact]
    public void A_list_of_values_is_found_by_every_value_it_holds()
    {
        var answer = ExplorerElementQueries.Describe(
            Section("data.ResourceHostRegionIDs", WellboreKind, ("data.ResourceHostRegionIDs[0]", "dev:reference-data--OSDURegion:A:"), ("data.ResourceHostRegionIDs[1]", "dev:reference-data--OSDURegion:B:")),
            Wellbore);

        Assert.Equal(
            "(data.ResourceHostRegionIDs.keyword:\"dev:reference-data--OSDURegion:A:\") AND (data.ResourceHostRegionIDs.keyword:\"dev:reference-data--OSDURegion:B:\")",
            Queries(answer)["exact"]);
        Assert.Equal("_exists_:data.ResourceHostRegionIDs", Queries(answer)["exists"]);
    }

    [Fact]
    public void A_flattened_list_is_found_by_its_values_and_by_being_there()
    {
        var answer = ExplorerElementQueries.Describe(
            Section("data.FacilitySpecifications[3]", WellboreKind, ("data.FacilitySpecifications[3].FacilitySpecificationText", "Rated")), Wellbore);

        Assert.Equal("data.FacilitySpecifications.FacilitySpecificationText:\"Rated\"", Queries(answer)["exact"]);
        Assert.Equal("_exists_:data.FacilitySpecifications", Queries(answer)["exists"]);
    }

    [Fact]
    public void A_section_takes_only_the_values_inside_it_and_says_what_it_left_out()
    {
        var answer = ExplorerElementQueries.Describe(
            Section("data.GeoContexts[3]", WellboreKind, ("data.GeoContexts[3].GeoTypeID", Country), ("data.GeoContexts[2].GeoTypeID", County), ("data.GeoContexts[3].GeoPoliticalEntityID", null)),
            Wellbore);

        Assert.Equal($"nested(data.GeoContexts, (GeoTypeID.keyword:\"{Country}\"))", Queries(answer)["exact"]);
        Assert.Contains(answer.Notes, n => n.StartsWith("2 values held null or could not be compared", StringComparison.Ordinal));

        // Every record holds data, so it is no section to search by.
        Assert.Contains("Every record holds data", ExplorerElementQueries.Describe(Section("data"), Wellbore).Problem, StringComparison.Ordinal);
    }

    /// <summary>A schema with an object and a list of objects the template gives no indexing hint.</summary>
    private static SchemaSnapshot Unhinted() => new("osdu:wks:master-data--Thing:1.0.0", JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "data": {
              "type": "object",
              "properties": {
                "Place": { "type": "object", "properties": { "Name": { "type": "string" }, "Code": { "type": "string" } } },
                "Parts": { "type": "array", "items": { "type": "object", "properties": { "Code": { "type": "string" } } } }
              }
            }
          }
        }
        """)!.AsObject(), DateTimeOffset.UnixEpoch);

    [Fact]
    public void Where_the_template_cannot_say_how_a_value_is_indexed_a_query_is_still_written_and_said_to_be_a_guess()
    {
        const string kind = "osdu:wks:master-data--Thing:1.0.0";
        var schema = Unhinted();

        var value = ExplorerElementQueries.Describe(Value("data.Parts[0].Code", "A1", kind), schema);
        Assert.Equal("data.Parts.Code.keyword:\"A1\"", Queries(value)["exact"]);
        Assert.Contains("no query reaches inside it", value.Guess, StringComparison.Ordinal);
        Assert.Contains("may not find what it looks for", value.Guess, StringComparison.Ordinal);
        Assert.Null(value.Problem);

        var section = ExplorerElementQueries.Describe(Section("data.Parts[0]", kind, ("data.Parts[0].Code", "A1")), schema);
        Assert.Equal("data.Parts.Code.keyword:\"A1\"", Queries(section)["exact"]);
        Assert.False(Queries(section).ContainsKey("exists"));
        Assert.Contains("gives no x-osdu-indexing hint", section.Guess, StringComparison.Ordinal);

        // An object is found by all its values, and by being there.
        var place = ExplorerElementQueries.Describe(Section("data.Place", kind, ("data.Place.Name", "Oslo"), ("data.Place.Code", "OSL")), schema);
        Assert.Equal("(data.Place.Name.keyword:\"Oslo\") AND (data.Place.Code.keyword:\"OSL\")", Queries(place)["exact"]);
        Assert.Equal("_exists_:data.Place", Queries(place)["exists"]);
        Assert.Null(place.Guess);

        // A property the template does not declare is written from its value too.
        var undeclared = ExplorerElementQueries.Describe(Value("data.Extra", 7, kind), schema);
        Assert.Equal("data.Extra:\"7\"", Queries(undeclared)["exact"]);
        Assert.Contains("has no property data.Extra", undeclared.Guess, StringComparison.Ordinal);
    }

    [Fact]
    public void The_records_own_values_are_asked_as_the_indexer_maps_them_for_every_kind()
    {
        var tag = ExplorerElementQueries.Describe(Value("legal.legaltags[0]", "dev-public"), Wellbore);
        Assert.Equal("legal.legaltags:\"dev-public\"", Queries(tag)["exact"]);
        Assert.Equal("keyword", tag.Field!.Index);

        var id = ExplorerElementQueries.Describe(Value("id", "dev:master-data--Wellbore:W1"), Wellbore);
        Assert.Equal("id:\"dev:master-data--Wellbore:W1\"", Queries(id)["exact"]);

        var acl = ExplorerElementQueries.Describe(Section("acl", WellboreKind, ("acl.viewers[0]", "data.default.viewers@dev"), ("acl.owners[0]", "data.default.owners@dev")), Wellbore);
        Assert.Equal("(acl.viewers:\"data.default.viewers@dev\") AND (acl.owners:\"data.default.owners@dev\")", Queries(acl)["exact"]);
        Assert.Equal("_exists_:acl", Queries(acl)["exists"]);
    }

    [Fact]
    public void Without_a_template_the_value_is_read_as_most_are_and_said_to_be_a_guess()
    {
        var text = ExplorerElementQueries.Describe(Value("data.Remark", "deep"), null);
        Assert.Null(text.Template);
        Assert.Equal("data.Remark.keyword:\"deep\"", Queries(text)["exact"]);
        Assert.StartsWith("No template a synced repository holds of", text.Guess, StringComparison.Ordinal);

        Assert.Equal("number", ExplorerElementQueries.Describe(Value("data.Depth", 1200), null).Field!.Index);
        Assert.Equal("date", ExplorerElementQueries.Describe(Value("data.Spudded", "2024-03-01T12:00:00Z"), null).Field!.Index);

        var inList = ExplorerElementQueries.Describe(Value("data.GeoContexts[1].GeoTypeID", "x"), null);
        Assert.Equal("data.GeoContexts.GeoTypeID.keyword:\"x\"", Queries(inList)["exact"]);
        Assert.Contains("data.GeoContexts is a list of objects", inList.Guess, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_is_found_only_by_whether_a_record_holds_a_value()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.FacilityName", null), Wellbore);

        Assert.Equal(["exists"], answer.Queries.Select(q => q.Purpose));
        Assert.Contains(answer.Notes, n => n.Contains("holds null here", StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_no_whole_match_can_find_still_has_its_words_and_says_why()
    {
        var answer = ExplorerElementQueries.Describe(Value("data.FacilityName", new string('x', 300)), Wellbore);

        var queries = Queries(answer);
        Assert.False(queries.ContainsKey("exact"));
        Assert.True(queries.ContainsKey("words"));
        Assert.Contains(answer.Notes, n => n.StartsWith("No query finds this whole value", StringComparison.Ordinal));

        var tooLong = ExplorerElementQueries.Describe(Value("data.FacilityName", new string('x', ExplorerElementRequest.MaxValueLength + 1)), Wellbore);
        Assert.Empty(tooLong.Queries);
        Assert.Contains("a query is written for a value of at most", tooLong.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data..FacilityName")]
    [InlineData("[0].FacilityName")]
    [InlineData("data.Facility Name")]
    [InlineData("data.FacilityName[")]
    [InlineData("data.Names[x]")]
    [InlineData(".data")]
    public void A_path_that_is_not_one_is_said_to_be_not_one(string path)
    {
        var answer = ExplorerElementQueries.Describe(Value(path, "x"), Wellbore);

        Assert.Empty(answer.Queries);
        Assert.Contains("is not the path of an element of a record", answer.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_that_cannot_be_read_is_refused()
    {
        Assert.Contains("Name the kind", new ExplorerElementRequest { Kind = "", Path = "data.X" }.Problem(), StringComparison.Ordinal);
        Assert.Contains("osdu:wks:WellLog", new ExplorerElementRequest { Kind = "osdu:wks:WellLog", Path = "data.X" }.Problem(), StringComparison.Ordinal);
        Assert.Contains("Name the element's path", new ExplorerElementRequest { Kind = WellboreKind, Path = " " }.Problem(), StringComparison.Ordinal);
        Assert.Contains("is a section", Value("data.X", new { a = 1 }).Problem(), StringComparison.Ordinal);
        Assert.Contains("is a section", Section("data.X", WellboreKind, ("data.X.Y", new[] { 1 })).Problem(), StringComparison.Ordinal);
        Assert.Contains("Only a section holds values", (Section("data.X", WellboreKind, ("data.X.Y", "a")) with { Section = false }).Problem(), StringComparison.Ordinal);
        var many = Enumerable.Range(0, ExplorerElementRequest.MaxLeaves + 1).Select(i => ($"data.X.Y[{i}]", (object?)"a")).ToArray();
        Assert.Contains("at most 48", Section("data.X", WellboreKind, many).Problem(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_kind_no_repository_holds_a_template_of_is_read_by_the_newest_of_its_type_and_says_so()
    {
        var answer = await ExplorerElementQueries.DescribeAsync(
            Value("data.FacilityName", "WELLBORE A/2-9 S", "osdu:wks:master-data--Wellbore:1.1.0"), new MemoryTemplates(Wellbore), CancellationToken.None);

        Assert.Equal(WellboreKind, answer.Template!.Kind);
        Assert.Contains(answer.Notes, n => n.Contains("No synced repository holds a template of osdu:wks:master-data--Wellbore:1.1.0, so it is read by osdu:wks:master-data--Wellbore:1.3.0", StringComparison.Ordinal));
        Assert.Equal("data.FacilityName.keyword:\"WELLBORE A/2-9 S\"", Queries(answer)["exact"]);

        var none = await ExplorerElementQueries.DescribeAsync(Value("data.FacilityName", "x", "osdu:wks:master-data--Field:1.0.0"), new MemoryTemplates(Wellbore), CancellationToken.None);
        Assert.Null(none.Template);
        Assert.StartsWith("No template a synced repository holds of osdu:wks:master-data--Field:1.0.0", none.Guess, StringComparison.Ordinal);

        await Assert.ThrowsAsync<DeliveryException>(() => ExplorerElementQueries.DescribeAsync(Value("data.X", "x", "bad"), new MemoryTemplates(), CancellationToken.None));
    }
}
