using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a dimension's elements are found in a record and each field read from them: every shape a nested OSDU record takes
/// (arrays inside arrays, filters, plain values, nulls, mixed arrays, references with versions, numbers and booleans).
/// </summary>
public class DimensionElementReaderTests
{
    private static List<(JsonNode Element, IReadOnlyList<JsonObject> Ancestors)> Elements(JsonNode record, string path)
    {
        var found = new List<(JsonNode, IReadOnlyList<JsonObject>)>();
        DimensionElementReader.Walk(record, DimensionPath.Parse(path).Path!, 0, [], found);
        return found;
    }

    private static string? Read((JsonNode Element, IReadOnlyList<JsonObject> Ancestors) element, DimensionElementField field)
        => DimensionElementReader.FieldReader.Of(field).Read(element.Element, element.Ancestors).Value;

    [Fact]
    public void The_objects_of_an_array_are_elements_in_order_and_what_is_no_object_or_null_is_passed_over()
    {
        var record = JsonNode.Parse("""
            { "data": { "Curves": [ { "Mnemonic": "MD" }, null, { "Mnemonic": "GR" }, [ { "Mnemonic": "RS" } ] ] } }
            """)!;

        var found = Elements(record, "data.Curves");

        // An array inside the array is stepped into, as the index flattens it; a null is no element.
        Assert.Equal(["MD", "GR", "RS"], found.Select(e => Read(e, new DimensionElementField("M", "Mnemonic"))));
        Assert.Empty(Elements(JsonNode.Parse("""{ "data": { "Curves": null } }""")!, "data.Curves"));
        Assert.Empty(Elements(JsonNode.Parse("""{ "data": { } }""")!, "data.Curves"));
        Assert.Empty(Elements(JsonNode.Parse("""{ "data": { "Curves": [] } }""")!, "data.Curves"));
    }

    [Fact]
    public void A_single_object_where_an_array_is_expected_is_one_element()
    {
        var found = Elements(JsonNode.Parse("""{ "data": { "Curves": { "Mnemonic": "MD" } } }""")!, "data.Curves");
        Assert.Equal("MD", Read(Assert.Single(found), new DimensionElementField("M", "Mnemonic")));
    }

    [Fact]
    public void A_filter_keeps_the_objects_it_matches_and_a_field_reads_from_any_object_up_the_path()
    {
        var record = JsonNode.Parse("""
            {
              "id": "dev:work-product-component--WellLog:1",
              "data": {
                "Name": "STAT_COMP",
                "Curves": [
                  { "Mnemonic": "MD", "CurveType": "Scalar", "Columns": [ "a" ] },
                  { "Mnemonic": "IMG", "CurveType": "ArrayCurve", "Columns": [ "c1", "c2", null, "" , 3, true ] },
                  { "Mnemonic": "WF", "CurveType": "arraywave", "Columns": [ "w1" ] }
                ]
              }
            }
            """)!;

        var found = Elements(record, "data.Curves[CurveType*=array].Columns");

        // Plain values are elements read by @; a null is passed over, an empty text is an element holding nothing.
        Assert.Equal(["c1", "c2", null, "3", "true", "w1"], found.Select(e => Read(e, new DimensionElementField("C", "@"))));
        Assert.Equal(["IMG", "IMG", "IMG", "IMG", "IMG", "WF"], found.Select(e => Read(e, new DimensionElementField("M", "Mnemonic", Up: 1))));
        Assert.All(found, e => Assert.Equal("STAT_COMP", Read(e, new DimensionElementField("L", "data.Name", Up: 3))));
        Assert.All(found, e => Assert.Equal("1", Read(e, new DimensionElementField("I", "id", Up: 3))));
        Assert.All(found, e => Assert.Equal("dev:work-product-component--WellLog:1", Read(e, new DimensionElementField("I", "id", DimensionValueKeep.Key, Up: 3))));
    }

    [Fact]
    public void A_field_reaching_several_values_keeps_the_first_or_joins_them_each_once()
    {
        var element = Assert.Single(Elements(JsonNode.Parse("""
            { "data": { "Curves": [ { "Aliases": [
                { "Kind": "Short", "Text": "GR" }, { "Kind": "Long", "Text": "Gamma ray" }, { "Kind": "Short", "Text": "GRC" },
                { "Kind": "Short", "Text": "GR" }, { "Kind": "Short", "Text": "  " }, { "Kind": "Short" } ] } ] } }
            """)!, "data.Curves"));

        Assert.Equal("GR", Read(element, new DimensionElementField("A", "Aliases[Kind=Short].Text")));
        Assert.Equal("GR; GRC", Read(element, new DimensionElementField("A", "Aliases[Kind=Short].Text", Many: DimensionValueMany.Join)));
        Assert.Equal("GR; Gamma ray; GRC", Read(element, new DimensionElementField("A", "Aliases.Text", Many: DimensionValueMany.Join)));
        Assert.Null(Read(element, new DimensionElementField("A", "Aliases[Kind=None].Text")));
        Assert.Null(Read(element, new DimensionElementField("A", "Missing.Path")));

        // max keeps the greatest by code point: lower case after upper, as a binary collation orders them.
        Assert.Equal("Gamma ray", Read(element, new DimensionElementField("A", "Aliases.Text", Many: DimensionValueMany.Max)));
        Assert.Equal("GRC", Read(element, new DimensionElementField("A", "Aliases[Kind=Short].Text", Many: DimensionValueMany.Max)));
        Assert.Null(Read(element, new DimensionElementField("A", "Aliases[Kind=None].Text", Many: DimensionValueMany.Max)));
    }

    [Fact]
    public void A_field_decodes_its_percent_escapes_before_it_is_kept()
    {
        var element = Assert.Single(Elements(JsonNode.Parse("""
            { "data": { "Curves": [ { "Unit": "g%2Fcm3", "Units": [ "m%2Fs", "ft%2Fs" ], "Plus": "a+b%20c" } ] } }
            """)!, "data.Curves"));

        Assert.Equal("g/cm3", Read(element, new DimensionElementField("U", "Unit", Decode: DimensionValueDecode.Url)));
        Assert.Equal("g%2Fcm3", Read(element, new DimensionElementField("U", "Unit")));
        Assert.Equal("a+b c", Read(element, new DimensionElementField("P", "Plus", Decode: DimensionValueDecode.Url)));

        // Decoded first, then chosen among: the greatest of the decoded values.
        Assert.Equal("m/s", Read(element, new DimensionElementField("U", "Units", Decode: DimensionValueDecode.Url, Many: DimensionValueMany.Max)));
        Assert.Equal("m/s; ft/s", Read(element, new DimensionElementField("U", "Units", Decode: DimensionValueDecode.Url, Many: DimensionValueMany.Join)));
    }

    [Fact]
    public void References_are_kept_as_a_value_a_key_or_the_id_of_the_record_they_name()
    {
        var element = Assert.Single(Elements(JsonNode.Parse("""
            { "data": { "Curves": [ {
                "Latest": "dev:reference-data--UnitOfMeasure:us%2Fft:",
                "Pinned": "dev:reference-data--UnitOfMeasure:m:1700000000",
                "Text": "  plain text  ",
                "Depth": 203.149,
                "Whole": 30,
                "Regular": false } ] } }
            """)!, "data.Curves"));

        Assert.Equal("us/ft", Read(element, new DimensionElementField("U", "Latest")));
        Assert.Equal("dev:reference-data--UnitOfMeasure:us%2Fft:", Read(element, new DimensionElementField("U", "Latest", DimensionValueKeep.Key)));
        Assert.Equal("dev:reference-data--UnitOfMeasure:us%2Fft", Read(element, new DimensionElementField("U", "Latest", DimensionValueKeep.Id)));
        Assert.Equal("dev:reference-data--UnitOfMeasure:m", Read(element, new DimensionElementField("U", "Pinned", DimensionValueKeep.Id)));
        Assert.Equal("plain text", Read(element, new DimensionElementField("T", "Text", DimensionValueKeep.Id)));
        Assert.Equal("203.149", Read(element, new DimensionElementField("D", "Depth")));
        Assert.Equal("30", Read(element, new DimensionElementField("W", "Whole")));
        Assert.Equal("false", Read(element, new DimensionElementField("R", "Regular")));
    }

    [Fact]
    public void A_value_longer_than_a_row_keeps_is_cut_but_a_key_is_left_out_rather_than_cut()
    {
        var longText = new string('x', DimensionSpec.MaxElementValueLength + 10);
        var element = Assert.Single(Elements(JsonNode.Parse($$"""{ "data": { "Curves": [ { "Long": "{{longText}}" } ] } }""")!, "data.Curves"));

        var asValue = DimensionElementReader.FieldReader.Of(new DimensionElementField("L", "Long")).Read(element.Element, element.Ancestors);
        Assert.Equal((DimensionSpec.MaxElementValueLength, true, false), (asValue.Value!.Length, asValue.Cut, asValue.TooLong));

        // A field keeps more than an attribute does: a curve's description of a thousand characters is kept whole.
        var description = new string('d', 1000);
        var described = Assert.Single(Elements(JsonNode.Parse($$"""{ "data": { "Curves": [ { "Text": "{{description}}" } ] } }""")!, "data.Curves"));
        Assert.Equal(description, DimensionElementReader.FieldReader.Of(new DimensionElementField("T", "Text")).Read(described.Element, described.Ancestors).Value);

        var asKey = DimensionElementReader.FieldReader.Of(new DimensionElementField("L", "Long", DimensionValueKeep.Key)).Read(element.Element, element.Ancestors);
        Assert.Equal(((string?)null, false, true), asKey);
    }

    [Fact]
    public void Elements_two_arrays_deep_each_keep_the_object_that_holds_them()
    {
        var record = JsonNode.Parse("""
            { "data": { "Runs": [
                { "Run": "A", "Passes": [ { "Pass": 1 }, { "Pass": 2 } ] },
                { "Run": "B", "Passes": [] },
                { "Run": "C", "Passes": [ { "Pass": 1 } ] } ] } }
            """)!;

        var found = Elements(record, "data.Runs.Passes");

        Assert.Equal(
            [("A", "1"), ("A", "2"), ("C", "1")],
            found.Select(e => (Read(e, new DimensionElementField("R", "Run", Up: 1)), Read(e, new DimensionElementField("P", "Pass")))));
    }
}
