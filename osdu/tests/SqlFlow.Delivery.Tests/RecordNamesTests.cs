using System.Text.Json;
using SqlFlow.Delivery.Engine.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The name a record is known by (<see cref="RecordNames"/>): the first of the properties records are named by that holds a
/// text, whichever way the service that answered projected it, and the list the explorer names records by too.
/// </summary>
public sealed class RecordNamesTests
{
    private static string? NameOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return RecordNames.Of(document.RootElement);
    }

    [Theory]
    [InlineData("""{"data":{"FacilityName":"Wellbore A-1"}}""", "Wellbore A-1")]
    [InlineData("""{"data.FacilityName":"Wellbore A-1"}""", "Wellbore A-1")]
    [InlineData("""{"data":{"DatasetProperties.FileSourceInfo.Name":"gr.las"}}""", "gr.las")]
    [InlineData("""{"data":{"DatasetProperties":{"FileSourceInfo":{"Name":"gr.las"}}}}""", "gr.las")]
    [InlineData("""{"data.DatasetProperties.FileSourceInfo.Name":"gr.las"}""", "gr.las")]
    public void A_property_is_found_nested_under_its_dotted_path_at_the_root_or_under_its_rest_inside_data(string record, string expected)
        => Assert.Equal(expected, NameOf(record));

    [Fact]
    public void The_first_property_holding_a_text_names_the_record_trimmed()
    {
        Assert.Equal("Wellbore A-1", NameOf("""{"data":{"Name":"second","FacilityName":"  Wellbore A-1  "}}"""));
        Assert.Equal("P-7", NameOf("""{"data":{"FacilityName":"   ","Name":7,"ProjectName":"P-7","Code":"C"}}"""));
        Assert.Equal("C-1", NameOf("""{"data":{"Code":"C-1","DatasetProperties":{"FileSourceInfo":{"Name":"f.las"}}}}"""));
    }

    [Theory]
    [InlineData("""{"data":{"Depth":1200}}""")]
    [InlineData("""{"data":{"Name":null,"FacilityName":["A"]}}""")]
    [InlineData("""{"id":"dev:master-data--Wellbore:1"}""")]
    [InlineData("""{"data":"text"}""")]
    [InlineData("""[]""")]
    public void A_record_holding_none_of_them_as_a_text_has_no_name(string record)
        => Assert.Null(NameOf(record));

    [Fact]
    public void The_explorer_names_a_record_by_the_same_properties()
        => Assert.Same(RecordNames.Fields, RecordExplorer.NameFields);
}
