using System.Text.Json.Nodes;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The paths a label or an attribute is read through (osdu/docs/reference/flow/dimension-attributes.md, The fields a label search
/// returns): segments with an optional filter on the objects they hold, the fields a search returns to read them, and
/// the text they read from a record; and a key naming an OSDU record shown as the code its id ends with, its escapes
/// decoded, ready for a drop-down.
/// </summary>
public class DimensionPathTests
{
    [Fact]
    public void A_filter_through_a_reference_compares_what_the_record_it_names_holds()
    {
        var path = DimensionPath.Parse("data.GeoContexts[GeoTypeID->data.Code=Country].GeoPoliticalEntityID").Path!;
        var record = System.Text.Json.Nodes.JsonNode.Parse("""
            { "data": { "GeoContexts": [
                { "GeoTypeID": "dev:reference-data--GeoPoliticalEntityType:CTRY:", "GeoPoliticalEntityID": "NO" },
                { "GeoTypeID": "dev:reference-data--GeoPoliticalEntityType:REG:", "GeoPoliticalEntityID": "NORDLAND" } ] } }
            """)!;
        var country = System.Text.Json.Nodes.JsonNode.Parse("""{ "data": { "Code": "Country" } }""")!.AsObject();

        Assert.True(path.HasLookups);
        Assert.Equal(["NO"], path.Read(record, id => id == "dev:reference-data--GeoPoliticalEntityType:CTRY" ? country : null));
        Assert.Empty(path.Read(record));
    }

    private static readonly JsonObject Wellbore = JsonNode.Parse("""
        {
          "id": "dev:master-data--Wellbore:1",
          "data": {
            "FacilityName": "Wellbore A/1-F-1",
            "GeoContexts": [
              { "GeoPoliticalEntityID": "dev:master-data--GeoPoliticalEntity:RegionA:", "GeoTypeID": "dev:reference-data--GeoPoliticalEntityType:Region:" },
              { "GeoPoliticalEntityID": "dev:master-data--GeoPoliticalEntity:UnitedStates:", "GeoTypeID": "dev:reference-data--GeoPoliticalEntityType:Country:" },
              { "FieldID": "dev:master-data--Field:FIELD-A:" }
            ]
          }
        }
        """)!.AsObject();

    private static DimensionPath Path(string text) => DimensionPath.Parse(text).Path ?? throw new InvalidOperationException(DimensionPath.Parse(text).Problem);

    [Fact]
    public void A_path_reads_every_value_it_reaches_stepping_into_arrays()
    {
        Assert.Equal(["Wellbore A/1-F-1"], Path("data.FacilityName").Read(Wellbore));
        Assert.Equal(
            ["dev:master-data--GeoPoliticalEntity:RegionA:", "dev:master-data--GeoPoliticalEntity:UnitedStates:"],
            Path("data.GeoContexts.GeoPoliticalEntityID").Read(Wellbore));
        Assert.Empty(Path("data.Nothing.Here").Read(Wellbore));
    }

    [Fact]
    public void A_filter_keeps_the_objects_whose_property_equals_or_contains_its_text()
    {
        var country = Path("data.GeoContexts[GeoTypeID*=country].GeoPoliticalEntityID");
        Assert.Equal(["dev:master-data--GeoPoliticalEntity:UnitedStates:"], country.Read(Wellbore));
        Assert.Equal(["data.GeoContexts.GeoPoliticalEntityID", "data.GeoContexts.GeoTypeID"], country.ReturnedFields);

        // An ends-with filter compares the end of the text, ignoring case.
        Assert.Equal(["dev:master-data--GeoPoliticalEntity:UnitedStates:"], Path("data.GeoContexts[GeoTypeID$=type:COUNTRY:].GeoPoliticalEntityID").Read(Wellbore));

        // An exact filter compares the whole text, case included.
        Assert.Empty(Path("data.GeoContexts[GeoTypeID=Country].GeoPoliticalEntityID").Read(Wellbore));
        Assert.Equal(
            ["dev:master-data--GeoPoliticalEntity:RegionA:"],
            Path("data.GeoContexts[GeoTypeID=dev:reference-data--GeoPoliticalEntityType:Region:].GeoPoliticalEntityID").Read(Wellbore));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("data.", "separated by one dot")]
    [InlineData("data.A b", "separated by one dot")]
    [InlineData("data.A[B]", "is not [Property=text]")]
    [InlineData("data.A[B=x", "no closing ]")]
    [InlineData("data.A[B-C=x]", "is not a property name")]
    [InlineData("data.A[B=]", "compares with nothing")]
    public void A_path_that_is_not_one_says_why(string text, string reason)
    {
        var (path, problem) = DimensionPath.Parse(text);
        Assert.Null(path);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dev:reference-data--UnitOfMeasure:us%2Fft:", "us/ft")]
    [InlineData("dev:reference-data--UnitOfMeasure:gAPI:", "gAPI")]
    [InlineData("dev:reference-data--LogType:Interpreted", "Interpreted")]
    [InlineData("dev:master-data--Wellbore:Wellbore-A-1-F-1:1234567", "Wellbore-A-1-F-1")]
    [InlineData("dev:reference-data--UnitOfMeasure:100%25:", "100%")]
    [InlineData("dev:reference-data--UnitOfMeasure:bad%zz:", "bad%zz")]
    [InlineData("GR", "GR")]
    [InlineData("us%2Fft", "us%2Fft")]
    public void A_key_naming_a_record_shows_as_its_code_with_its_escapes_decoded_and_other_text_as_it_is(string key, string shown)
        => Assert.Equal(shown, DimensionLabeler.DisplayOf(key));
}
