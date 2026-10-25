using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A path's filter through a reference (<c>[Property-&gt;path=text]</c>) at every edge: written with each comparison and
/// with spaces, refused where its property or its path is not one, the lookups it asks for (one filter, several, nested),
/// and what it reads: a reference with or without its version, an array of them, text that names no record, a record the
/// lookup does not find, and no lookup at all.
/// </summary>
[Trait(DimensionEdgeCaseSuite.Trait, DimensionEdgeCaseSuite.Name)]
public class DimensionPathEdgeCaseTests
{
    private const string Country = "dev:reference-data--GeoPoliticalEntityType:CTRY";
    private const string Region = "dev:reference-data--GeoPoliticalEntityType:REG";

    private static DimensionPath Path(string text) => DimensionPath.Parse(text).Path ?? throw new InvalidOperationException(DimensionPath.Parse(text).Problem);

    [Theory]
    [InlineData("data.GeoContexts[GeoTypeID->data.Code=Country].GeoPoliticalEntityID", DimensionPathCompare.Equals, "Country")]
    [InlineData("data.GeoContexts[GeoTypeID->data.Code*=count].GeoPoliticalEntityID", DimensionPathCompare.Contains, "count")]
    [InlineData("data.GeoContexts[GeoTypeID->data.Code$=TRY].GeoPoliticalEntityID", DimensionPathCompare.EndsWith, "TRY")]
    [InlineData("data.GeoContexts[ GeoTypeID -> data.Code = Country ].GeoPoliticalEntityID", DimensionPathCompare.Equals, "Country")]
    [InlineData("data.GeoContexts[GeoTypeID->data.Codes.Text=Country].GeoPoliticalEntityID", DimensionPathCompare.Equals, "Country")]
    public void A_filter_through_a_reference_is_written_with_each_comparison(string text, DimensionPathCompare compare, string value)
    {
        var path = Path(text);
        var segment = path.Segments[1];
        Assert.Equal(("GeoTypeID", value, compare), (segment.FilterProperty, segment.FilterValue, segment.Compare));
        Assert.NotNull(segment.FilterThrough);
        Assert.StartsWith("data.Code", segment.FilterThrough!.Text, StringComparison.Ordinal);
        Assert.True(path.HasLookups);

        // The search returns the reference the filter follows, as it returns any filter's property.
        Assert.Equal(["data.GeoContexts.GeoPoliticalEntityID", "data.GeoContexts.GeoTypeID"], path.ReturnedFields);
    }

    [Theory]
    [InlineData("data.GeoContexts[->data.Code=Country].X", "compares '', which is not a property name")]
    [InlineData("data.GeoContexts[GeoTypeID->=Country].X", "reads '' through GeoTypeID, which is not a path")]
    [InlineData("data.GeoContexts[GeoTypeID->data..Code=Country].X", "reads 'data..Code' through GeoTypeID, which is not a path")]
    [InlineData("data.GeoContexts[GeoTypeID->data.Co de=Country].X", "which is not a path")]
    [InlineData("data.GeoContexts[Geo Type->data.Code=Country].X", "compares 'Geo Type', which is not a property name")]
    [InlineData("data.GeoContexts[GeoTypeID->data.Code=].X", "compares with nothing")]
    public void A_filter_through_a_reference_that_is_not_one_is_refused_saying_why(string text, string problem)
    {
        var (path, why) = DimensionPath.Parse(text);
        Assert.Null(path);
        Assert.Contains(problem, why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_without_a_filter_through_a_reference_looks_nothing_up()
    {
        Assert.False(Path("data.GeoContexts[GeoTypeID*=Country].GeoPoliticalEntityID").HasLookups);
        Assert.Empty(Path("data.GeoContexts[GeoTypeID*=Country].GeoPoliticalEntityID").Lookups);
        Assert.Null(Path("data.GeoContexts[GeoTypeID*=Country].GeoPoliticalEntityID").Segments[1].FilterThrough);
    }

    [Fact]
    public void Each_filter_through_a_reference_asks_for_the_references_it_follows_and_the_path_it_reads()
    {
        var path = Path("data.A[R1->data.X=1].B[R2->data.Y.Z=2].C");
        Assert.Equal(
            [("data.A.R1", "data.X"), ("data.A.B.R2", "data.Y.Z")],
            path.Lookups.Select(l => (l.References.Text, l.Read.Text)));

        // The references are read without the filters on the way, so every reference a filter may follow is looked up.
        Assert.All(path.Lookups, l => Assert.All(l.References.Segments, s => Assert.Null(s.FilterProperty)));
    }

    [Fact]
    public void A_filter_through_a_reference_keeps_the_objects_whose_referenced_record_holds_the_text()
    {
        var record = JsonNode.Parse($$"""
            { "data": { "GeoContexts": [
                { "GeoTypeID": "{{Country}}:", "GeoPoliticalEntityID": "E1" },
                { "GeoTypeID": "{{Region}}:", "GeoPoliticalEntityID": "E2" },
                { "GeoTypeID": "not a reference", "GeoPoliticalEntityID": "E3" },
                { "GeoTypeID": ["dev:reference-data--GeoPoliticalEntityType:GONE:", "{{Country}}:1700000000"], "GeoPoliticalEntityID": "E4" },
                { "GeoPoliticalEntityID": "E5" },
                { "GeoTypeID": null, "GeoPoliticalEntityID": "E6" },
                { "GeoTypeID": 7, "GeoPoliticalEntityID": "E7" }
            ] } }
            """)!;
        var types = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            [Country] = JsonNode.Parse("""{ "data": { "Code": "Country" } }""")!.AsObject(),
            [Region] = JsonNode.Parse("""{ "data": { "Code": "Region" } }""")!.AsObject(),
        };
        JsonObject? Lookup(string id) => types.TryGetValue(id, out var found) ? found : null;

        // The type is told by its record's code, not its id; a versioned reference names the record its id names; text that
        // names no record, a reference to a record not found, no property and a property of no text keep nothing.
        Assert.Equal(["E1", "E4"], Path("data.GeoContexts[GeoTypeID->data.Code=Country].GeoPoliticalEntityID").Read(record, Lookup));
        Assert.Equal(["E2"], Path("data.GeoContexts[GeoTypeID->data.Code=Region].GeoPoliticalEntityID").Read(record, Lookup));
        Assert.Equal(["E1", "E4"], Path("data.GeoContexts[GeoTypeID->data.Code*=COUNT].GeoPoliticalEntityID").Read(record, Lookup));
        Assert.Equal(["E2"], Path("data.GeoContexts[GeoTypeID->data.Code$=ION].GeoPoliticalEntityID").Read(record, Lookup));
        Assert.Empty(Path("data.GeoContexts[GeoTypeID->data.Code=country].GeoPoliticalEntityID").Read(record, Lookup));
        Assert.Empty(Path("data.GeoContexts[GeoTypeID->data.Missing=Country].GeoPoliticalEntityID").Read(record, Lookup));

        // Without a lookup a filter through a reference keeps nothing, never the objects unfiltered.
        Assert.Empty(Path("data.GeoContexts[GeoTypeID->data.Code=Country].GeoPoliticalEntityID").Read(record));
        Assert.Empty(Path("data.GeoContexts[GeoTypeID->data.Code=Country].GeoPoliticalEntityID").Read(record, _ => null));
    }

    [Fact]
    public void A_referenced_record_holding_several_values_at_the_path_matches_on_any()
    {
        var record = JsonNode.Parse($$"""{ "data": { "Items": [ { "TypeID": "{{Country}}:", "Name": "N1" } ] } }""")!;
        var type = JsonNode.Parse("""{ "data": { "Aliases": [ { "Text": "Land" }, { "Text": "Country" } ] } }""")!.AsObject();
        Assert.Equal(["N1"], Path("data.Items[TypeID->data.Aliases.Text=Country].Name").Read(record, id => id == Country ? type : null));
        Assert.Empty(Path("data.Items[TypeID->data.Aliases.Text=Region].Name").Read(record, id => id == Country ? type : null));
    }
}
