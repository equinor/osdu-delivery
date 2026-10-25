using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a dimension's elements are found and their fields read at every edge of the settings they take: a null in the
/// array as an element (at the element's level, inside an array of arrays, where a filter compares, and where the array
/// itself is null or missing), the fields of such an element (inside it, up the path, the element itself), white space a
/// field keeps, and every value of a field, as often as it is held, or the greatest of them.
/// </summary>
[Trait(DimensionEdgeCaseSuite.Trait, DimensionEdgeCaseSuite.Name)]
public class DimensionElementEdgeCaseTests
{
    private static List<(JsonNode Element, IReadOnlyList<JsonObject> Ancestors)> Elements(JsonNode record, string path, bool keepNulls)
    {
        var found = new List<(JsonNode, IReadOnlyList<JsonObject>)>();
        DimensionElementReader.Walk(record, DimensionPath.Parse(path).Path!, 0, [], found, keepNulls);
        return found;
    }

    private static string? Read((JsonNode Element, IReadOnlyList<JsonObject> Ancestors) element, DimensionElementField field)
        => DimensionElementReader.FieldReader.Of(field).Read(element.Element, element.Ancestors).Value;

    [Fact]
    public void A_null_in_the_array_is_an_element_of_every_field_null_only_when_the_elements_keep_nulls()
    {
        var record = JsonNode.Parse("""
            { "id": "dev:work-product-component--WellLog:1", "data": { "LogVersion": "2", "Curves": [ { "Mnemonic": "MD" }, null, { "Mnemonic": "GR" } ] } }
            """)!;

        Assert.Equal(["MD", "GR"], Elements(record, "data.Curves", keepNulls: false).Select(e => Read(e, new DimensionElementField("M", "Mnemonic"))));
        var kept = Elements(record, "data.Curves", keepNulls: true);
        Assert.Equal(["MD", null, "GR"], kept.Select(e => Read(e, new DimensionElementField("M", "Mnemonic"))));

        // The null element's own fields are null; a field read up the path reads the object that holds the array, and the
        // element itself (@) is no value.
        Assert.Equal(["2", "2", "2"], kept.Select(e => Read(e, new DimensionElementField("V", "LogVersion", Up: 1))));
        Assert.Equal(["1", "1", "1"], kept.Select(e => Read(e, new DimensionElementField("I", "id", Up: 2))));
        Assert.Null(Read(kept[1], new DimensionElementField("S", "@")));
    }

    [Fact]
    public void A_null_array_a_missing_one_and_an_empty_one_hold_no_element_whatever_the_elements_keep()
    {
        foreach (var keepNulls in new[] { false, true })
        {
            Assert.Empty(Elements(JsonNode.Parse("""{ "data": { "Curves": null } }""")!, "data.Curves", keepNulls));
            Assert.Empty(Elements(JsonNode.Parse("""{ "data": { } }""")!, "data.Curves", keepNulls));
            Assert.Empty(Elements(JsonNode.Parse("""{ "data": { "Curves": [] } }""")!, "data.Curves", keepNulls));
        }

        // An array of nulls alone is that many elements of every field null.
        Assert.Equal(2, Elements(JsonNode.Parse("""{ "data": { "Curves": [ null, null ] } }""")!, "data.Curves", keepNulls: true).Count);
    }

    [Fact]
    public void A_null_inside_an_array_of_arrays_is_an_element_and_one_a_filter_compares_is_not()
    {
        var nested = JsonNode.Parse("""{ "data": { "Curves": [ [ { "Mnemonic": "MD" }, null ], [ null ] ] } }""")!;
        Assert.Equal(["MD", null, null], Elements(nested, "data.Curves", keepNulls: true).Select(e => Read(e, new DimensionElementField("M", "Mnemonic"))));
        Assert.Equal(["MD"], Elements(nested, "data.Curves", keepNulls: false).Select(e => Read(e, new DimensionElementField("M", "Mnemonic"))));

        // A filter keeps the objects that match, and a null matches nothing.
        var filtered = JsonNode.Parse("""{ "data": { "Curves": [ { "Type": "Scalar", "Mnemonic": "MD" }, null ] } }""")!;
        Assert.Equal(["MD"], Elements(filtered, "data.Curves[Type=Scalar]", keepNulls: true).Select(e => Read(e, new DimensionElementField("M", "Mnemonic"))));

        // A null on the way to the elements is no object to step through.
        var onTheWay = JsonNode.Parse("""{ "data": { "Runs": [ null, { "Passes": [ { "Pass": 1 }, null ] } ] } }""")!;
        Assert.Equal(["1", null], Elements(onTheWay, "data.Runs.Passes", keepNulls: true).Select(e => Read(e, new DimensionElementField("P", "Pass"))));
    }

    [Fact]
    public void A_field_keeps_its_white_space_when_it_says_so_and_white_space_alone_is_then_a_value()
    {
        var element = Assert.Single(Elements(JsonNode.Parse("""{ "data": { "Curves": [ { "Name": "  GR  ", "Blank": "   ", "Empty": "" } ] } }""")!, "data.Curves", false));
        Assert.Equal("GR", Read(element, new DimensionElementField("N", "Name")));
        Assert.Equal("  GR  ", Read(element, new DimensionElementField("N", "Name", Trim: false)));
        Assert.Null(Read(element, new DimensionElementField("B", "Blank")));
        Assert.Equal("   ", Read(element, new DimensionElementField("B", "Blank", Trim: false)));
        Assert.Null(Read(element, new DimensionElementField("E", "Empty", Trim: false)));
    }

    [Fact]
    public void A_field_reaching_several_values_keeps_every_one_as_often_as_held_or_the_greatest()
    {
        var element = Assert.Single(Elements(JsonNode.Parse("""
            { "data": { "Curves": [ { "Aliases": [ "GR", "gr", "GR", "Å", "Z" ], "Units": [ "dev:reference-data--UnitOfMeasure:m:", "dev:reference-data--UnitOfMeasure:ft:12" ] } ] } }
            """)!, "data.Curves", false));
        Assert.Equal("GR; gr; GR; Å; Z", Read(element, new DimensionElementField("A", "Aliases", Many: DimensionValueMany.All)));
        Assert.Equal("GR; gr; Å; Z", Read(element, new DimensionElementField("A", "Aliases", Many: DimensionValueMany.Join)));
        Assert.Equal("Å", Read(element, new DimensionElementField("A", "Aliases", Many: DimensionValueMany.Max)));

        // References kept as a reference keep a version they name, and lose only the trailing colon of the latest.
        Assert.Equal(
            "dev:reference-data--UnitOfMeasure:m; dev:reference-data--UnitOfMeasure:ft:12",
            Read(element, new DimensionElementField("U", "Units", DimensionValueKeep.Reference, Many: DimensionValueMany.Join)));
        Assert.Equal(
            "dev:reference-data--UnitOfMeasure:m; dev:reference-data--UnitOfMeasure:ft",
            Read(element, new DimensionElementField("U", "Units", DimensionValueKeep.Id, Many: DimensionValueMany.Join)));
    }
}
