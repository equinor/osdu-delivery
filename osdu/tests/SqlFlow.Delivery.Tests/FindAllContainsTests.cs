using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;
using static SqlFlow.Delivery.Tests.AccessListTests;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A <c>$findAll</c> line comparing two fields of the same row, <c>GroupName contains GeoPoliticalEntityName</c>: the
/// country groups of a country are the rows naming it, no field, and a name that says the country, compared by letters and
/// digits alone and ignoring case. Both fields a row was judged by are recorded, a line that cannot say which rows it
/// keeps is refused when the mapping is read, and one whose fields the cache does not capture is refused at the gate.
/// </summary>
public sealed class FindAllContainsTests
{
    private const string Contractors = "dev:reference-data--AccessGroup:us-contractors";
    private const string Seismic = "dev:reference-data--AccessGroup:us-seismic";
    private const string Gulf = "dev:reference-data--AccessGroup:us-gulf";
    private const string Unnamed = "dev:reference-data--AccessGroup:us-unnamed";

    /// <summary>The country groups of a United States wellbore with no field: rows naming the country, no field, and a name that says the country.</summary>
    internal const string CountryGroups = """
        acl:
          owners: [owners@x]
          viewers:
            - viewers@x
            - $cache: AccessGroup.EntitlementGroupEmail
              $findAll:
                - GeoPoliticalEntityID = $lookup.wellbore.GeoContexts.GeoPoliticalEntityID
                - FieldList is empty
                - FieldIDList is empty
                - GroupName contains GeoPoliticalEntityName
              $required: false
        legal:
          legaltags: [tag]
          otherRelevantDataCountries: [US]
        """;

    /// <summary>
    /// The United States' groups filed under the country with no field: two whose names say the country (one written
    /// without the space), one filed there for another purpose, and one with no name; and Field B's group, which names a
    /// field and says the country.
    /// </summary>
    internal static ReferenceType NamedGroups(Func<ReferenceItem, ReferenceItem>? change = null) => new("AccessGroup", "reference-data--AccessGroup",
        new[]
        {
            Group(Contractors, "dataoffice united states for consultants - contractors", "data.office.us.contractors.viewers@x"),
            Group(Seismic, "AZAPPL - dataoffice unitedstates internal seismic", "data.office.us.seismic.viewers@x"),
            Group(Gulf, "AZAPPL - dataoffice gulf group", "data.office.gulf.viewers@x"),
            Item(Unnamed,
                ("GeoPoliticalEntityID", $"\"{UnitedStates}\""), ("GeoPoliticalEntityName", "\"United States\""),
                ("EntitlementGroupEmail", "\"data.office.unnamed.viewers@x\"")),
            Item("dev:reference-data--AccessGroup:field-b",
                ("GeoPoliticalEntityID", $"\"{UnitedStates}\""), ("GeoPoliticalEntityName", "\"United States\""),
                ("GroupName", "\"dataoffice united states field b\""), ("FieldList", "[\"FIELD B\"]"), ("FieldIDList", $"[\"{FieldB}\"]"),
                ("EntitlementGroupEmail", "\"data.office.field.b.viewers@x\"")),
        }.Select(change ?? (item => item)));

    internal static ReferenceItem Group(string id, string name, string email) => Item(id,
        ("GeoPoliticalEntityID", $"\"{UnitedStates}\""), ("GeoPoliticalEntityName", "\"United States\""),
        ("GroupName", System.Text.Json.JsonSerializer.Serialize(name)), ("EntitlementGroupEmail", System.Text.Json.JsonSerializer.Serialize(email)));

    [Theory]
    [InlineData("dataoffice unitedkingdom internal seismic", "United Kingdom", true)]
    [InlineData("Data Office - United Kingdom", "united-kingdom", true)]
    [InlineData("DATA OFFICE CÔTE D'IVOIRE", "Côte d’Ivoire", true)]
    [InlineData("dataoffice gulf group", "United States", false)]
    [InlineData("dataoffice norway", " - ", false)]
    [InlineData("dataoffice norway", "", false)]
    [InlineData(null, "Norway", false)]
    [InlineData("Norway", null, false)]
    public void A_text_holds_a_part_by_its_letters_and_digits_whatever_their_case(string? text, string? part, bool holds)
        => Assert.Equal(holds, ReferenceKeyFold.Holds(text, part));

    [Fact]
    public void A_contains_line_is_read_as_two_fields_of_the_row_beside_the_key_and_the_empty_fields()
    {
        var mapping = Mapping(record: CountryGroups);

        var query = mapping.Entries.Single(e => e.Target.Text == "osdu.acl.viewers").Parts[1].FindAll!;
        Assert.Equal("GeoPoliticalEntityID", query.Field);
        Assert.Equal(["FieldList", "FieldIDList"], query.Empty);
        Assert.Equal(new FindAllContains("GroupName", "GeoPoliticalEntityName"), Assert.Single(query.Contains));
        Assert.Equal(
            "GeoPoliticalEntityID = $lookup.wellbore.GeoContexts.GeoPoliticalEntityID and FieldList is empty and FieldIDList is empty and GroupName contains GeoPoliticalEntityName",
            query.ToString());
    }

    [Fact]
    public void The_country_groups_are_the_rows_whose_name_says_the_country_however_it_is_spaced()
    {
        var result = Renderer(Mapping(record: CountryGroups), Cache(groups: NamedGroups())).Render(Record("WELLBORE X-1"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal(["viewers@x", "data.office.us.contractors.viewers@x", "data.office.us.seismic.viewers@x"], Viewers(result));
    }

    [Fact]
    public void Both_fields_a_row_was_judged_by_are_recorded_as_it_held_them()
    {
        var result = Renderer(Mapping(record: CountryGroups), Cache(groups: NamedGroups())).Render(Record("WELLBORE X-1"));

        // A row the line passed over, and one it kept: each field as the row held it, so a renamed group reaches the record.
        Assert.Contains(new CacheUsage("AccessGroup", Gulf, "GroupName", "AZAPPL - dataoffice gulf group", CacheUsageKind.Value), result.CacheUsages);
        Assert.Contains(new CacheUsage("AccessGroup", Gulf, "GeoPoliticalEntityName", "United States", CacheUsageKind.Value), result.CacheUsages);
        Assert.Contains(new CacheUsage("AccessGroup", Seismic, "GroupName", "AZAPPL - dataoffice unitedstates internal seismic", CacheUsageKind.Value), result.CacheUsages);

        // A row with no name is passed over and recorded as read empty, so a name given to it later reaches the record.
        Assert.Contains(new CacheUsage("AccessGroup", Unnamed, "GroupName", string.Empty, CacheUsageKind.Empty), result.CacheUsages);

        // A row the empty fields turned away is never judged by the contains line.
        Assert.DoesNotContain(result.CacheUsages, u => u.ItemId == "dev:reference-data--AccessGroup:field-b" && u.Path == "GroupName");
    }

    [Fact]
    public void A_required_findAll_no_row_passes_holds_naming_what_narrowed_the_rows()
    {
        var mapping = Mapping(record: CountryGroups.Replace("$required: false", "$required: true", StringComparison.Ordinal));
        var renamed = NamedGroups(item => item.Id is Contractors or Seismic ? Group(item.Id, "dataoffice gulf group", "x@x") : item);

        var result = Renderer(mapping, Cache(groups: renamed)).Render(Record("WELLBORE X-1"));

        Assert.True(result.IsHeld);
        Assert.Contains(
            "no AccessGroup row with FieldList empty and FieldIDList empty and GroupName containing its GeoPoliticalEntityName holds",
            Assert.Single(result.Holds),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$findAll: [GeoPoliticalEntityID = unit, GroupName contains 'Norway']", "a contains line compares two fields of the same row")]
    [InlineData("$findAll: [GeoPoliticalEntityID = unit, GroupName contains $lookup.wellbore.FacilityName]", "a contains line compares two fields of the same row")]
    [InlineData("$findAll: [GeoPoliticalEntityID = unit, GroupName contains groupname]", "compares GroupName with itself")]
    [InlineData("$findAll: [GeoPoliticalEntityID = unit, GroupName contains GeoPoliticalEntityName, groupname contains GeoPoliticalEntityName]", "says it a second time")]
    [InlineData("$findAll: [GeoPoliticalEntityID = unit, GeoPoliticalEntityName is empty, GroupName contains GeoPoliticalEntityName]", "no row holds both")]
    [InlineData("$findAll: GroupName contains GeoPoliticalEntityName", "needs the line the rows are found by")]
    [InlineData("$findAll: [GeoPoliticalEntityID = unit, '']", "has an empty line")]
    [InlineData("$findAll: [GeoPoliticalEntityID = unit, GroupName contains]", "or <field> contains <field>")]
    public void A_contains_line_that_cannot_say_which_rows_it_keeps_is_refused(string settings, string expected)
    {
        var data = "WellboreID: { $lookup: wellbore.id }\nAliases:\n  $cache: AccessGroup.EntitlementGroupEmail\n" + TestSchema.Indented(settings, 2);
        var ex = Assert.Throws<FlowValidationException>(() => Mapping(data: data));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gate_refuses_a_contains_line_whose_field_no_row_holds()
    {
        // The groups as captured without their names: no row could pass, so the mapping would give no country group.
        var unnamed = Preflight.Check(Mapping(record: CountryGroups), TestSchema.Build(), Cache(), TestSchema.Context(), sourceColumns: null);
        Assert.Contains(unnamed, i => i.Severity == IssueSeverity.Error
            && i.Message.Contains("where 'GroupName contains GeoPoliticalEntityName', and no row of cache version 'refs-1' holds a GroupName, so no row would pass", StringComparison.Ordinal));

        // A column written where the line names a field of the row reads as a field no row holds.
        var column = Mapping(record: CountryGroups.Replace("contains GeoPoliticalEntityName", "contains wellbore_uwi", StringComparison.Ordinal));
        var issues = Preflight.Check(column, TestSchema.Build(), Cache(groups: NamedGroups()), TestSchema.Context(), sourceColumns: null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error
            && i.Message.Contains("holds a wellbore_uwi, so no row would pass. Both sides of a contains line are fields of the AccessGroup rows", StringComparison.Ordinal));

        // Captured, it passes.
        Assert.DoesNotContain(
            Preflight.Check(Mapping(record: CountryGroups), TestSchema.Build(), Cache(groups: NamedGroups()), TestSchema.Context(), sourceColumns: null),
            i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void The_outline_and_the_shape_say_what_the_line_keeps()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping("""
            Aliases:
              - viewers@x
              - $cache: AccessGroup.GroupEmail
                $findAll:
                  - Country = country_id
                  - GroupName contains CountryName
            """));
        var found = Assert.Single(Walk(outline.Record), node => node.Path == "data.Aliases").Children[1];
        Assert.Contains("and whose GroupName holds its CountryName (letters and digits, ignoring case)", Assert.Single(found.Flow!.Steps).Detail, StringComparison.Ordinal);

        var shape = MappingRenderer.Shape(Mapping(record: CountryGroups), TestSchema.Build(), new Dictionary<string, string>(StringComparer.Ordinal) { ["dataPartition"] = "dev" });
        Assert.Contains("FieldIDList is empty and GroupName contains GeoPoliticalEntityName", shape.Document["acl"]!["viewers"]![1]!.GetValue<string>(), StringComparison.Ordinal);

        static IEnumerable<MappingOutlineNode> Walk(IEnumerable<MappingOutlineNode> nodes) => nodes.SelectMany(node => Walk(node.Children).Prepend(node));
    }

    [Fact]
    public void The_builder_opens_a_contains_line_and_writes_it_back()
    {
        var original = Mapping(record: CountryGroups);

        var draft = MappingBuilder.FromDefinition(original);
        Assert.Empty(MappingBuilder.Incomplete(draft));
        var viewers = draft.Entries.Single(e => e.Target == "osdu.acl.viewers");
        var line = Assert.Single(viewers.Items[1].FindAll!.Contains);
        Assert.Equal(("GroupName", "GeoPoliticalEntityName"), (line.Field, line.Part));

        var yaml = MappingBuilder.ToYaml(draft);
        Assert.Contains("- GroupName contains GeoPoliticalEntityName", yaml, StringComparison.Ordinal);
        var reread = new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml");
        Assert.Equal(
            Viewers(Renderer(original, Cache(groups: NamedGroups())).Render(Record("WELLBORE X-1"))),
            Viewers(Renderer(reread, Cache(groups: NamedGroups())).Render(Record("WELLBORE X-1"))));

        // A draft comparing a field with itself, or with a field it asks to be empty, says so before it is written.
        MappingDraftEntry With(MappingDraftFindAll all) => viewers with { Items = [viewers.Items[0], viewers.Items[1] with { FindAll = all }] };
        var all = viewers.Items[1].FindAll!;
        var itself = draft with { Entries = [.. draft.Entries.Select(e => e == viewers ? With(all with { Contains = [new() { Field = "GroupName", Part = "groupname" }] }) : e)] };
        Assert.Contains(MappingBuilder.Incomplete(itself), i => i.Message.Contains("compares GroupName with itself", StringComparison.Ordinal));
        var emptied = draft with { Entries = [.. draft.Entries.Select(e => e == viewers ? With(all with { Empty = [.. all.Empty, "GroupName"] }) : e)] };
        Assert.Contains(MappingBuilder.Incomplete(emptied), i => i.Message.Contains("asks for GroupName to be empty and compares it with another field", StringComparison.Ordinal));
    }
}
