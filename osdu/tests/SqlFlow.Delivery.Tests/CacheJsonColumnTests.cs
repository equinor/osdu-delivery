using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A table type's column read with <c>decode: json</c> (osdu/docs/reference/flow/cache-origins.md, A column holding a JSON
/// object): its text kept as the JSON object it holds, so a mapping writes the object whole where the template takes an
/// object, every property name and JSON type as the column holds them. Opt in only: a column read without it stays text.
/// Nothing here reaches a database; the capture from SQL Server is <see cref="SqlServerTableCaptureTests"/>.
/// </summary>
public class CacheJsonColumnTests
{
    private const string Tags = """{ "lra.dataID": "LRA-17", "Source": "welldb", "Region": "North" }""";

    [Fact]
    public void A_json_column_value_is_kept_as_the_object_its_text_holds_with_every_name_and_type()
    {
        var decoded = JsonColumnValue.Decode(
            """{ "lra.dataID": "LRA-17", "depth": 1.50, "count": 3, "regular": true, "aliases": ["a", null, 2], "nested": { "b": 1, "a": "x" }, "Source": "welldb", "gone": null }""",
            out var problem);

        Assert.Null(problem);
        Assert.NotNull(decoded);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{ "Source": "welldb", "aliases": ["a", null, 2], "count": 3, "depth": 1.5, "lra.dataID": "LRA-17", "nested": { "a": "x", "b": 1 }, "regular": true }"""),
            decoded));

        // Names are kept exactly, a dotted one whole, in the ordinal order every object of the cache keeps; null is left out.
        Assert.Equal(["Source", "aliases", "count", "depth", "lra.dataID", "nested", "regular"], decoded.Select(p => p.Key));
        Assert.Equal("LRA-17", decoded["lra.dataID"]!.GetValue<string>());
        Assert.False(decoded.ContainsKey("gone"));

        // Each value keeps its JSON type; an array keeps its null element, which holds its place.
        Assert.Equal(System.Text.Json.JsonValueKind.Number, decoded["count"]!.GetValueKind());
        Assert.Equal(System.Text.Json.JsonValueKind.True, decoded["regular"]!.GetValueKind());
        Assert.Equal(System.Text.Json.JsonValueKind.Object, decoded["nested"]!.GetValueKind());
        var aliases = decoded["aliases"]!.AsArray();
        Assert.Equal(3, aliases.Count);
        Assert.Null(aliases[1]);
        Assert.Equal(System.Text.Json.JsonValueKind.Number, aliases[2]!.GetValueKind());

        // What a capture keeps is what the store writes, so a version read back from the database is the same object.
        Assert.Equal(CanonicalJson.ToString(decoded), CanonicalJson.ToString(ReferenceValue.From(decoded).Node));
        Assert.Equal("{}", CanonicalJson.ToString(JsonColumnValue.Decode("{}", out _)));
    }

    [Theory]
    [InlineData("""[{ "Source": "welldb" }]""", "is a JSON array, not an object")]
    [InlineData("\"welldb\"", "is a JSON string, not an object")]
    [InlineData("42", "is a JSON number, not an object")]
    [InlineData("true", "is a JSON boolean, not an object")]
    [InlineData("null", "is a JSON null, not an object")]
    [InlineData("""{ "Source": "welldb" """, "is not valid JSON (")]
    [InlineData("""{ "Source": "welldb", }""", "is not valid JSON (")]
    [InlineData("""{ /* note */ "Source": "welldb" }""", "is not valid JSON (")]
    [InlineData("""{ "Source": "welldb" } { }""", "is not valid JSON (")]
    [InlineData("""{ Source: "welldb" }""", "is not valid JSON (")]
    [InlineData("", "is empty text, which is no JSON object; a row that has no object holds NULL in the column")]
    [InlineData("   ", "is empty text, which is no JSON object")]
    [InlineData("""{ "Source": "welldb", "Source": "other" }""", "holds the property $.Source twice")]
    [InlineData("""{ "lra": { "x.y": 1, "x.y": 2 } }""", "holds the property $.lra['x.y'] twice")]
    [InlineData("""{ "depths": [1, { "top": 1e400 }] }""", "holds a number at $.depths[1].top beyond the range of a JSON number the cache keeps")]
    public void Text_that_is_not_one_json_object_is_refused_with_the_reason(string text, string reason)
    {
        Assert.Null(JsonColumnValue.Decode(text, out var problem));
        Assert.NotNull(problem);
        Assert.StartsWith(reason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_table_column_declares_its_decoding_and_only_a_table_column_takes_one()
    {
        var loader = new DeliveryDocumentLoader();
        var flow = loader.ParseCache(Flow("[well_name, { column: tags, as: Tags, decode: json }, { column: notes, decode: none }]"), "cache/well-tags.yaml");
        var type = Assert.Single(flow.Types);
        Assert.Equal(["well_name", "Tags", "notes"], type.Fields.Select(f => f.Name));
        Assert.Equal([CacheColumnDecode.None, CacheColumnDecode.Json, CacheColumnDecode.None], type.Fields.Select(f => f.Decode));

        string Refused(string fields) => Assert.Throws<FlowValidationException>(() => loader.ParseCache(Flow(fields), "cache/well-tags.yaml")).Message;
        Assert.Equal(
            "cache/well-tags.yaml: 'types[0].fields[0].decode' value 'xml' is not one of none, json.",
            Refused("[{ column: tags, decode: xml }]"));
        Assert.Contains("types[0].fields[0].decode takes one word, json,", Refused("[{ column: tags, decode: '' }]"), StringComparison.Ordinal);
        Assert.Contains("types[0].fields[0].decode takes one word, json,", Refused("[{ column: tags, decode: [json] }]"), StringComparison.Ordinal);
        Assert.Contains("has no 'parse' setting; a table column takes 'column', 'as' and 'decode'.", Refused("[{ column: tags, parse: json }]"), StringComparison.Ordinal);

        // A path of an OSDU record yields its JSON as it is, so an OSDU type's field takes no decode.
        var osdu = Assert.Throws<FlowValidationException>(() => loader.ParseCache("""
            flowType: cache
            name: reference
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: dev }
            types:
              - kind: "osdu:wks:master-data--Wellbore:*"
                fields: [{ path: data.ExtensionProperties, as: Extension, decode: json }]
            """, "cache/reference.yaml"));
        Assert.Contains("has no 'decode' setting; a cached field takes 'path' and 'as'.", osdu.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_decoded_column_is_kept_by_the_catalog_and_refused_on_a_type_holding_no_table()
    {
        var decoded = new ReferenceFieldSpec("tags", "Tags", CacheColumnDecode.Json);
        var json = decoded.ToJson();
        Assert.Equal("json", json["decode"]!.GetValue<string>());
        Assert.Equal(decoded, ReferenceFieldSpec.FromJson(json));
        Assert.NotEqual(new ReferenceFieldSpec("tags", "Tags"), decoded);

        // A field decoded from nothing is stored as it was before columns were decoded, so no stored declaration moves.
        Assert.False(new ReferenceFieldSpec("tags", "Tags").ToJson().ContainsKey("decode"));

        // A pipeline's cache step reads a table type from the catalog's declaration, so it decodes the column as the flow does.
        var stored = OsduCacheStore.ParseFields("""[{ "path": "well_name", "as": "well_name" }, { "path": "tags", "as": "Tags", "decode": "json" }]""", "well-tags", "WellTags");
        Assert.Equal([CacheColumnDecode.None, CacheColumnDecode.Json], stored.Select(f => f.Decode));
        var unknown = Assert.Throws<DeliveryException>(() => OsduCacheStore.ParseFields("""[{ "path": "tags", "as": "Tags", "decode": "xml" }]""", "well-tags", "WellTags"));
        Assert.Contains("holds a field this host cannot read (Field Tags holds 'xml', which is no CacheColumnDecode.)", unknown.Message, StringComparison.Ordinal);

        var osdu = new ReferenceTypeSpec
        {
            Name = "Wellbore", EntityType = "master-data--Wellbore", Kind = "osdu:wks:master-data--Wellbore:*", Fields = [new ReferenceFieldSpec("data.ExtensionProperties", "Extension", CacheColumnDecode.Json)],
        };
        Assert.Equal(
            "Cached type 'Wellbore' comes from kind osdu:wks:master-data--Wellbore:*, which takes no 'decode' (Extension): only a column of an ingestion table holds text to decode.",
            Assert.Throws<FlowValidationException>(osdu.Validate).Message);

        var dictionary = new ReferenceTypeSpec
        {
            Name = "UnitAlias", EntityType = ReferenceType.LookupEntityType("UnitAlias"), Origin = CacheOrigin.Dictionary, Dictionary = "UnitAlias",
            Key = "key", Fields = [new ReferenceFieldSpec("value", "value", CacheColumnDecode.Json)],
        };
        Assert.Contains("which takes no 'decode' (value)", Assert.Throws<FlowValidationException>(dictionary.Validate).Message, StringComparison.Ordinal);

        var table = new ReferenceTypeSpec
        {
            Name = "WellTags", EntityType = ReferenceType.LookupEntityType("WellTags"), Origin = CacheOrigin.Table, Table = "OsduData.silver.WellTags",
            Key = "well_id", Fields = [decoded],
        };
        table.Validate();
    }

    [Fact]
    public void A_mapping_writes_a_decoded_object_whole_where_the_template_takes_an_object_and_text_only_where_it_takes_text()
    {
        var decoded = References(ReferenceValue.From(JsonColumnValue.Decode(Tags, out _)!));
        var mapping = TestSchema.Mapping(record: """
            tags:
              $cache: WellTags.Tags
              $findBy: well_id = name
            """);

        var rendered = new MappingRenderer(mapping, TestSchema.Build(), decoded, TestSchema.Context()).Render(Record("W-1"));
        Assert.False(rendered.IsHeld, string.Join("; ", rendered.Holds));
        var tags = rendered.Document["tags"]!.AsObject();
        Assert.Equal("LRA-17", tags["lra.dataID"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Tags), tags));

        // An object written where the template takes text holds the record, naming the target.
        var asText = new MappingRenderer(TestSchema.Mapping("""
            Symbol:
              $cache: WellTags.Tags
              $findBy: well_id = name
            """), TestSchema.Build(), decoded, TestSchema.Context()).Render(Record("W-1"));
        Assert.True(asText.IsHeld);
        Assert.Contains("an object cannot be written where the template takes a string", Assert.Single(asText.Holds), StringComparison.Ordinal);

        // The same text read without decode stays text, which a template's object does not take.
        var text = new MappingRenderer(mapping, TestSchema.Build(), References(ReferenceValue.Of(Tags)), TestSchema.Context()).Render(Record("W-1"));
        Assert.True(text.IsHeld);
        Assert.Contains("a single value cannot be written where the template takes an object", Assert.Single(text.Holds), StringComparison.Ordinal);
    }

    /// <summary>The test template's reference data with a lookup table of well tags, keyed by well id, holding <paramref name="tags"/> for W-1.</summary>
    private static ReferenceSnapshot References(ReferenceValue tags)
        => new("refs-1", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TestSchema.References().Types.Append(new ReferenceType(
            "WellTags",
            ReferenceType.LookupEntityType("WellTags"),
            [new ReferenceItem("W-1", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { ["well_id"] = ReferenceValue.Of("W-1"), ["Tags"] = tags })],
            "well_id")));

    private static SourceRecord Record(string name) => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = name, ["depth"] = "1" }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
    };

    private static string Flow(string fields) => $$"""
        flowType: cache
        name: well-tags
        source:
          connection: ${env:OSDU_DATA_DB}
          headers: { data-partition-id: dev }
        types:
          - name: WellTags
            table: OsduData.silver.WellTags
            key: well_id
            fields: {{fields}}
        """;
}
