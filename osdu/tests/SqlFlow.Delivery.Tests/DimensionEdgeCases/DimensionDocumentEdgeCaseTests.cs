using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a dimension document says of the settings an attribute, an element's field, the elements, a dimension and the
/// incremental block take, at every edge: each value each takes, each one refused and why, the bounds of
/// <c>maxReferences</c> and of the attributes a dimension reads, the kinds references are looked up in, filters through a
/// reference where a label's and an attribute's steps take them and where nothing does, and the hash a declaration keeps
/// whether a setting is written at its default or left out.
/// </summary>
[Trait(DimensionEdgeCaseSuite.Trait, DimensionEdgeCaseSuite.Name)]
public class DimensionDocumentEdgeCaseTests
{
    private const string Head = """
        flowType: dimension
        name: wells-dimensions
        partitions: [dev]
        source:
          endpoint: ${env:OSDU_URL}

        """;

    private const string Wellbore = """
        dimensions:
          - name: WellboreContext
            kind: "osdu:wks:master-data--Wellbore:1.3.0"
            path: id
            columns: { key: WellboreID, value: WellboreCode }
        """;

    private static DimensionFlowDefinition Parse(string yaml) => new DeliveryDocumentLoader().ParseDimension(yaml, "flows/dims.yaml");

    private static DimensionSpec Dimension(string settings) => Parse(Head + Wellbore + "\n    " + settings).Dimensions[0];

    private static string Refused(string settings) => Assert.Throws<FlowValidationException>(() => Dimension(settings)).Message;

    private static string Hash(string settings) => Dimension(settings).DefinitionHash;

    [Theory]
    [InlineData("1", 1)]
    [InlineData("20", 20)]
    [InlineData("1000", 1000)]
    public void An_attribute_follows_as_many_references_a_step_as_it_says(string declared, int expected)
        => Assert.Equal(expected, Dimension($"attributes: {{ Field: {{ path: [data.GeoContexts.FieldID, data.FieldName], maxReferences: {declared} }} }}").Attributes[0].MaxReferences);

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("-5")]
    [InlineData("2.5")]
    [InlineData("many")]
    [InlineData("'1 000'")]
    public void An_attribute_s_maxReferences_out_of_bounds_is_refused(string declared)
        => Assert.Contains("maxReferences is", Refused($"attributes: {{ Field: {{ path: [data.GeoContexts.FieldID, data.FieldName], maxReferences: {declared} }} }}"), StringComparison.Ordinal);

    [Theory]
    [InlineData("trim: false", false)]
    [InlineData("trim: true", true)]
    [InlineData("trim: 'False'", false)]
    [InlineData("trim: TRUE", true)]
    public void An_attribute_keeps_white_space_when_it_says_so(string declared, bool trim)
        => Assert.Equal(trim, Dimension($"attributes: {{ Name: {{ path: data.FacilityName, {declared} }} }}").Attributes[0].Trim);

    [Theory]
    [InlineData("attributes: { Name: { path: data.FacilityName, trim: yes } }", "trim is 'yes'")]
    [InlineData("attributes: { Name: { path: data.FacilityName, trim: 0 } }", "trim is '0'")]
    [InlineData("attributes: { Name: { path: data.WellID, keep: id, trim: false } }", "keeps each value as id, which is always trimmed")]
    [InlineData("attributes: { Name: { path: data.WellID, keep: key, trim: false } }", "keeps each value as key, which is always trimmed")]
    [InlineData("attributes: { Name: { path: data.WellID, keep: reference, trim: false } }", "keeps each value as reference, which is always trimmed")]
    [InlineData("attributes: { Name: { path: data.WellID, keep: reference, decode: url } }", "keeps each value as reference, the text the dimension it joins is keyed by")]
    [InlineData("attributes: { Name: { collect: data.Source, trim: false } }", "collects its values, and trim reads the value")]
    [InlineData("attributes: { Name: { collect: data.Source, maxReferences: 5 } }", "collects its values, and maxReferences reads the value")]
    [InlineData("attributes: { Name: { collect: data.Source, many: all } }", "collects its values, and many reads the value")]
    [InlineData("attributes: { Name: { path: data.FacilityName, many: every } }", "many is 'every'")]
    [InlineData("attributes: { Name: { path: data.FacilityName, keep: ref } }", "keep is 'ref'")]
    [InlineData("attributes: { Name: { path: data.FacilityName, decode: html } }", "decode is 'html'")]
    [InlineData("attributes: { Name: { path: data.FacilityName, tidy: true } }", "it names tidy")]
    public void An_attribute_setting_that_breaks_a_rule_is_refused_naming_it(string settings, string reason)
        => Assert.Contains(reason, Refused(settings), StringComparison.Ordinal);

    [Fact]
    public void An_attribute_takes_every_many_and_keep_the_engine_reads_by()
    {
        var dimension = Dimension("""
            attributes:
                  First: { path: data.A, many: first }
                  Joined: { path: data.A, many: join }
                  Every: { path: data.A, many: all }
                  Greatest: { path: data.A, many: max }
                  Reference: { path: data.WellID, keep: reference }
                  WellKey: { path: data.WellID, keep: key }
            """);
        Assert.Equal(
            [DimensionValueMany.First, DimensionValueMany.Join, DimensionValueMany.All, DimensionValueMany.Max, DimensionValueMany.First, DimensionValueMany.First],
            dimension.Attributes.Select(a => a.Many));
        Assert.Equal(DimensionValueKeep.Reference, dimension.Attribute("Reference")!.Keep);
    }

    [Fact]
    public void A_dimension_reads_as_many_attributes_and_fields_as_its_table_lays_out_and_no_more()
    {
        string Attributes(int n) => string.Join(", ", Enumerable.Range(0, n).Select(i => $"A{i}: data.A{i}"));
        string Fields(int n) => string.Join(", ", Enumerable.Range(0, n).Select(i => $"F{i}: F{i}"));
        Assert.Equal(DimensionSpec.MaxAttributes, Dimension($"attributes: {{ {Attributes(DimensionSpec.MaxAttributes)} }}").Attributes.Count);
        Assert.Contains("attributes names 65; a dimension reads at most 64", Refused($"attributes: {{ {Attributes(DimensionSpec.MaxAttributes + 1)} }}"), StringComparison.Ordinal);

        var both = Dimension($"attributes: {{ {Attributes(40)} }}\n    elements: {{ path: data.GeoContexts, fields: {{ {Fields(24)} }} }}");
        Assert.Equal(64, both.Attributes.Count + both.Elements!.Fields.Count);
        Assert.Contains("name 65 columns", Refused($"attributes: {{ {Attributes(40)} }}\n    elements: {{ path: data.GeoContexts, fields: {{ {Fields(25)} }} }}"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_dimension_looks_the_records_of_each_entity_type_up_in_the_kind_it_names()
    {
        var dimension = Dimension("lookupKinds: [osdu:wks:master-data--Basin:1.2.0, ' osdu:wks:master-data--Field:* ', '*:*:reference-data--BasinType:1.0.0']");
        Assert.Equal("osdu:wks:master-data--Basin:1.2.0", dimension.LookupKind("master-data--Basin"));
        Assert.Equal("osdu:wks:master-data--Field:*", dimension.LookupKind("master-data--Field"));
        Assert.Equal("*:*:reference-data--BasinType:1.0.0", dimension.LookupKind("reference-data--BasinType"));
        Assert.Equal("*:*:master-data--Well:*", dimension.LookupKind("master-data--Well"));
        Assert.Empty(Dimension("lookupKinds: []").LookupKinds);
    }

    [Theory]
    [InlineData("lookupKinds: [osdu:wks:master-data--Basin]", "is not authority:source:entityType:version")]
    [InlineData("lookupKinds: ['osdu:wks:*:1.0.0']", "names no one entity type")]
    [InlineData("lookupKinds: ['osdu:wks:master-data--*:1.0.0']", "names no one entity type")]
    [InlineData("lookupKinds: [osdu:wks:Basin:1.0.0]", "names no one entity type")]
    [InlineData("lookupKinds: [osdu:wks:master-data--Basin:1.2.0, osdu:wks:master-data--Basin:1.0.0]", "is a second kind of master-data--Basin")]
    [InlineData("lookupKinds: ['']", "is not authority:source:entityType:version")]
    public void A_lookup_kind_that_names_no_one_entity_type_is_refused(string declared, string reason)
        => Assert.Contains(reason, Refused(declared), StringComparison.Ordinal);

    [Theory]
    [InlineData("nulls: keep", true)]
    [InlineData("nulls: KEEP", true)]
    [InlineData("nulls: skip", false)]
    [InlineData("", false)]
    public void The_elements_keep_a_null_of_the_array_as_an_element_when_they_say_so(string nulls, bool keep)
        => Assert.Equal(keep, Dimension($"elements: {{ path: data.GeoContexts, {nulls}{(nulls.Length == 0 ? string.Empty : ", ")}fields: {{ FieldID: FieldID }} }}").Elements!.KeepNulls);

    [Theory]
    [InlineData("elements: { path: data.GeoContexts, nulls: drop, fields: { FieldID: FieldID } }", "elements.nulls is 'drop'")]
    [InlineData("elements: { path: 'data.GeoContexts[GeoTypeID->data.Code=Field]', fields: { FieldID: FieldID } }", "filters through a reference (->), which only a label's and an attribute's steps do")]
    [InlineData("elements: { path: data.GeoContexts, fields: { Name: 'Items[TypeID->data.Code=A].Name' } }", "filters through a reference (->), which only a label's and an attribute's steps do")]
    [InlineData("elements: { path: data.GeoContexts, fields: { FieldID: { path: FieldID, keep: id, trim: false } } }", "keeps each value as id, which is always trimmed")]
    [InlineData("elements: { path: data.GeoContexts, fields: { FieldID: { path: FieldID, trim: maybe } } }", "trim is 'maybe'")]
    public void An_elements_setting_that_breaks_a_rule_is_refused_naming_it(string settings, string reason)
        => Assert.Contains(reason, Refused(settings), StringComparison.Ordinal);

    [Fact]
    public void A_field_takes_every_many_keep_decode_and_trim_an_attribute_does()
    {
        var fields = Dimension("""
            elements:
                  path: data.Curves
                  fields:
                    All: { path: Aliases, many: all }
                    Max: { path: Aliases, many: max, decode: url }
                    Raw: { path: Mnemonic, trim: false }
                    Reference: { path: CurveUnit, keep: reference }
            """).Elements!.Fields;
        Assert.Equal(
            [(DimensionValueMany.All, DimensionValueDecode.None, true, DimensionValueKeep.Value), (DimensionValueMany.Max, DimensionValueDecode.Url, true, DimensionValueKeep.Value),
             (DimensionValueMany.First, DimensionValueDecode.None, false, DimensionValueKeep.Value), (DimensionValueMany.First, DimensionValueDecode.None, true, DimensionValueKeep.Reference)],
            fields.Select(f => (f.Many, f.Decode, f.Trim, f.Keep)));
    }

    [Theory]
    [InlineData("label: ['data.GeoContexts[GeoTypeID->data.Code=Country].GeoPoliticalEntityID', data.GeoPoliticalEntityName]")]
    [InlineData("attributes: { Country: ['data.GeoContexts[GeoTypeID->data.Code=Country].GeoPoliticalEntityID', data.GeoPoliticalEntityName] }")]
    [InlineData("attributes: { Country: { path: ['data.GeoContexts[GeoTypeID->data.Code$=TRY].GeoPoliticalEntityID', 'data[GeoPoliticalEntityTypeID->data.Name*=land].GeoPoliticalEntityName'], many: max } }")]
    public void A_label_s_and_an_attribute_s_steps_filter_through_a_reference(string declared)
        => Assert.NotNull(Dimension(declared));

    [Theory]
    [InlineData("path: 'data.GeoContexts[GeoTypeID->data.Code=Country].FieldID'")]
    [InlineData("attributes: { Source: { collect: 'data.Items[TypeID->data.Code=A].Name' } }")]
    public void A_key_s_path_and_a_collected_path_take_no_filter_at_all(string declared)
    {
        var yaml = declared.StartsWith("path:", StringComparison.Ordinal)
            ? Head + Wellbore.Replace("path: id", declared, StringComparison.Ordinal)
            : Head + Wellbore + "\n    " + declared;
        Assert.Contains("is not a property path", Assert.Throws<FlowValidationException>(() => Parse(yaml)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("incremental: { keyColumns: [id], fullLoadWhenRecordsLeave: true }", true)]
    [InlineData("incremental: { keyColumns: [id], fullLoadWhenRecordsLeave: false }", false)]
    [InlineData("incremental: { keyColumns: [id] }", false)]
    public void An_incremental_block_loads_in_full_when_records_leave_when_it_says_so(string block, bool full)
        => Assert.Equal(full, Parse(Head + block + "\n" + Wellbore).Incremental!.FullLoadWhenRecordsLeave);

    [Fact]
    public void An_incremental_setting_misspelled_is_refused_by_name()
        => Assert.Contains(
            "fullLoadWhenRecordLeave",
            Assert.ThrowsAny<Exception>(() => Parse(Head + "incremental: { keyColumns: [id], fullLoadWhenRecordLeave: true }\n" + Wellbore)).Message,
            StringComparison.Ordinal);

    [Fact]
    public void A_setting_written_at_its_default_hashes_as_one_left_out_and_any_other_value_does_not()
    {
        var bare = Hash("attributes: { Field: [data.GeoContexts.FieldID, data.FieldName] }");
        foreach (var same in new[]
        {
            "attributes: { Field: { path: [data.GeoContexts.FieldID, data.FieldName], maxReferences: 20 } }",
            "attributes: { Field: { path: [data.GeoContexts.FieldID, data.FieldName], trim: true } }",
            "attributes: { Field: { path: [data.GeoContexts.FieldID, data.FieldName], many: first, decode: none, keep: value } }",
            "attributes: { Field: [data.GeoContexts.FieldID, data.FieldName] }\n    lookupKinds: []",
        })
        {
            Assert.Equal(bare, Hash(same));
        }

        var others = new[]
        {
            "attributes: { Field: { path: [data.GeoContexts.FieldID, data.FieldName], maxReferences: 21 } }",
            "attributes: { Field: { path: [data.GeoContexts.FieldID, data.FieldName], trim: false } }",
            "attributes: { Field: { path: [data.GeoContexts.FieldID, data.FieldName], many: all } }",
            "attributes: { Field: { path: [data.GeoContexts.FieldID, data.FieldName], keep: reference } }",
            "attributes: { Field: [data.GeoContexts.FieldID, data.FieldName] }\n    lookupKinds: [osdu:wks:master-data--Field:1.0.0]",
            "attributes: { Field: ['data.GeoContexts[GeoTypeID->data.Code=Field].FieldID', data.FieldName] }",
        }.Select(Hash).ToList();
        Assert.DoesNotContain(bare, others);
        Assert.Equal(others.Count, others.Distinct(StringComparer.Ordinal).Count());

        var elements = Hash("elements: { path: data.Curves, fields: { M: Mnemonic } }");
        Assert.Equal(elements, Hash("elements: { path: data.Curves, nulls: skip, fields: { M: { path: Mnemonic, trim: true, decode: none } } }"));
        Assert.NotEqual(elements, Hash("elements: { path: data.Curves, nulls: keep, fields: { M: Mnemonic } }"));
        Assert.NotEqual(elements, Hash("elements: { path: data.Curves, fields: { M: { path: Mnemonic, trim: false } } }"));
    }
}
