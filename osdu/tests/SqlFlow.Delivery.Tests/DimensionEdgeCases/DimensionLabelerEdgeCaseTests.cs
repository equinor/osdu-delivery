using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Model;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// How a key's label and attributes are read through the records it names, against a stand-in for the search service, at
/// every edge of the settings an attribute takes: a filter through a reference told by the referenced record's own code
/// (an id and a code that differ, a referenced record the search does not hold, a reference with a version), the greatest
/// and every value over all the records a step reaches, bounded by <c>maxReferences</c> with a note, the kinds records are
/// looked up in, white space kept, and the records a key is read through, which an incremental load reads again by.
/// </summary>
[Trait(DimensionEdgeCaseSuite.Trait, DimensionEdgeCaseSuite.Name)]
public sealed class DimensionLabelerEdgeCaseTests : IDisposable
{
    private const string Wellbore = "osdu:wks:master-data--Wellbore:1.3.0";
    private const string Entity = "osdu:wks:master-data--GeoPoliticalEntity:1.0.0";
    private const string EntityType = "osdu:wks:reference-data--GeoPoliticalEntityType:1.0.0";
    private const string Field = "osdu:wks:master-data--Field:1.0.0";

    /// <summary>A type of country whose id says nothing of what its code says, as a partition's own records may.</summary>
    private const string CountryType = "dev:reference-data--GeoPoliticalEntityType:CTRY";

    /// <summary>A type of region whose id holds <c>:Country:</c>, which a filter on the id's text takes for a country.</summary>
    private const string RegionType = "dev:reference-data--GeoPoliticalEntityType:Country:Region";

    private readonly FakeDimensionPlatform _platform = new();

    public void Dispose() => _platform.Dispose();

    private DimensionLabeler Labeler()
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), _platform, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, FakeDimensionPlatform.Endpoint, new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return new DimensionLabeler(new OsduSearch(client, "/api/search/v2/query", "/api/search/v2/query_with_cursor", NullLogger.Instance), NullLogger.Instance, concurrency: 2);
    }

    private static JsonObject Context(string entity, string type) => new() { ["GeoPoliticalEntityID"] = entity + ":", ["GeoTypeID"] = type + ":" };

    /// <summary>Two types whose codes are Country and Region, their ids neither; entities of each; and wellbores in them.</summary>
    private void Countries()
    {
        _platform.Add(CountryType, EntityType, new JsonObject { ["Code"] = "Country", ["Name"] = "Country" });
        _platform.Add(RegionType, EntityType, new JsonObject { ["Code"] = "Region", ["Name"] = "Region" });
        _platform.Add("dev:master-data--GeoPoliticalEntity:NO", Entity, new JsonObject { ["GeoPoliticalEntityName"] = "Norway" });
        _platform.Add("dev:master-data--GeoPoliticalEntity:SE", Entity, new JsonObject { ["GeoPoliticalEntityName"] = "Sweden" });
        _platform.Add("dev:master-data--GeoPoliticalEntity:NORDLAND", Entity, new JsonObject { ["GeoPoliticalEntityName"] = "Nordland" });
        _platform.Add("dev:master-data--Wellbore:W1", Wellbore, new JsonObject
        {
            ["GeoContexts"] = new JsonArray(
                Context("dev:master-data--GeoPoliticalEntity:NORDLAND", RegionType),
                Context("dev:master-data--GeoPoliticalEntity:NO", CountryType)),
        });
        _platform.Add("dev:master-data--Wellbore:W2", Wellbore, new JsonObject
        {
            ["GeoContexts"] = new JsonArray(
                new JsonObject { ["GeoPoliticalEntityID"] = "dev:master-data--GeoPoliticalEntity:SE:", ["GeoTypeID"] = CountryType + ":1700000000" },
                Context("dev:master-data--GeoPoliticalEntity:NO", "dev:reference-data--GeoPoliticalEntityType:GONE")),
        });
    }

    [Fact]
    public async Task A_filter_through_a_reference_tells_a_country_by_its_type_s_code_where_the_type_s_id_says_otherwise()
    {
        Countries();
        DimensionAttributeSpec[] attributes =
        [
            new("ByCode", ["data.GeoContexts[GeoTypeID->data.Code=Country].GeoPoliticalEntityID", "data.GeoPoliticalEntityName"], Many: DimensionValueMany.Max),
            new("ByIdText", ["data.GeoContexts[GeoTypeID*=:Country:].GeoPoliticalEntityID", "data.GeoPoliticalEntityName"], Many: DimensionValueMany.Max),
        ];

        var read = await Labeler().ReadAsync(["dev:master-data--Wellbore:W1", "dev:master-data--Wellbore:W2"], [], attributes, null, CancellationToken.None);

        // The id of the country type holds no Country, and the region's does, so a filter on the id's text takes the region
        // for a country: only the type's code tells them apart. A reference with a version names the record its id names;
        // a type the search does not hold keeps its context out.
        Assert.Equal([("ByCode", "Norway"), ("ByIdText", "Nordland")], read.Attributes["dev:master-data--Wellbore:W1"].Select(a => (a.Name, a.Value)));
        Assert.Equal([("ByCode", "Sweden")], read.Attributes["dev:master-data--Wellbore:W2"].Select(a => (a.Name, a.Value)));

        // The types looked up are records each key was read through, so a type changed is a key read again.
        Assert.Contains(CountryType, read.Records["dev:master-data--Wellbore:W1"]);
        Assert.Contains(RegionType, read.Records["dev:master-data--Wellbore:W1"]);
        Assert.Contains("dev:reference-data--GeoPoliticalEntityType:GONE", read.Records["dev:master-data--Wellbore:W2"]);

        // The types were looked up in their own kind, asked only for what the filter reads.
        var typeSearch = Assert.Single(_platform.Calls, c => c.Body is { } body && body.Contains("GeoPoliticalEntityType:*", StringComparison.Ordinal));
        Assert.Contains("data.Code", typeSearch.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("data.Name", typeSearch.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_greatest_and_every_value_are_read_over_every_record_a_step_reaches_up_to_as_many_as_it_follows()
    {
        // A wellbore of 25 fields, named F01 to F25, the last ones greatest; field F07 is named twice.
        var contexts = new JsonArray();
        for (var i = 1; i <= 25; i++)
        {
            var id = $"dev:master-data--Field:F{i:D2}";
            _platform.Add(id, Field, new JsonObject { ["FieldName"] = i == 9 ? "Field F07" : $"Field F{i:D2}" });
            contexts.Add(new JsonObject { ["FieldID"] = id + ":" });
        }

        _platform.Add("dev:master-data--Wellbore:W1", Wellbore, new JsonObject { ["GeoContexts"] = contexts });
        DimensionAttributeSpec[] attributes =
        [
            new("Bounded", ["data.GeoContexts.FieldID", "data.FieldName"], Many: DimensionValueMany.Max),
            new("Whole", ["data.GeoContexts.FieldID", "data.FieldName"], Many: DimensionValueMany.Max, MaxReferences: 25),
            new("Every", ["data.GeoContexts.FieldID", "data.FieldName"], Many: DimensionValueMany.All, MaxReferences: 25),
            new("Once", ["data.GeoContexts.FieldID", "data.FieldName"], Many: DimensionValueMany.Join, MaxReferences: 25),
            new("First", ["data.GeoContexts.FieldID", "data.FieldName"]),
        ];

        var read = await Labeler().ReadAsync(["dev:master-data--Wellbore:W1"], [], attributes, null, CancellationToken.None);

        var values = read.Attributes["dev:master-data--Wellbore:W1"].ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);
        Assert.Equal("Field F20", values["Bounded"]);
        Assert.Equal("Field F25", values["Whole"]);
        Assert.Equal("Field F01", values["First"]);

        // Every value, a name held twice twice, in the order the fields were reached, cut at the length an attribute keeps.
        Assert.StartsWith("Field F01; Field F02; Field F03; Field F04; Field F05; Field F06; Field F07; Field F08; Field F07; Field F10", values["Every"], StringComparison.Ordinal);
        Assert.Equal(DimensionSpec.MaxAttributeValueLength, values["Every"].Length);
        Assert.DoesNotContain("Field F08; Field F07", values["Once"], StringComparison.Ordinal);

        // The key the default bounded is said so, with the setting that follows more.
        Assert.Contains(read.Notes, n => n.Contains("1 key(s) name more than 20 record(s) at a step of Bounded, and only the first 20 were followed; raise maxReferences", StringComparison.Ordinal));
        Assert.Contains(read.Notes, n => n.Contains("1 key(s) name more than 20 record(s) at a step of First", StringComparison.Ordinal));
        Assert.DoesNotContain(read.Notes, n => n.Contains("at a step of Whole", StringComparison.Ordinal));
        Assert.Contains(read.Notes, n => n.Contains("were longer than a dimension keeps", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_dimension_looks_records_up_in_the_kind_it_names_and_finds_none_of_another()
    {
        _platform.Add("dev:master-data--Field:OLD", "osdu:wks:master-data--Field:0.9.0", new JsonObject { ["FieldName"] = "Old field" });
        _platform.Add("dev:master-data--Field:NEW", Field, new JsonObject { ["FieldName"] = "New field" });
        _platform.Add("dev:master-data--Wellbore:W1", Wellbore, new JsonObject
        {
            ["GeoContexts"] = new JsonArray(new JsonObject { ["FieldID"] = "dev:master-data--Field:OLD:" }, new JsonObject { ["FieldID"] = "dev:master-data--Field:NEW:" }),
        });
        DimensionAttributeSpec[] attributes = [new("Fields", ["data.GeoContexts.FieldID", "data.FieldName"], Many: DimensionValueMany.Join)];

        var any = await Labeler().ReadAsync(["dev:master-data--Wellbore:W1"], [], attributes, null, CancellationToken.None);
        var pinned = await Labeler().ReadAsync(
            ["dev:master-data--Wellbore:W1"], [], attributes, new Dictionary<string, string>(StringComparer.Ordinal) { ["master-data--Field"] = Field }, CancellationToken.None);

        Assert.Equal("Old field; New field", any.Attributes["dev:master-data--Wellbore:W1"].Single().Value);
        Assert.Equal("New field", pinned.Attributes["dev:master-data--Wellbore:W1"].Single().Value);
        Assert.Contains(_platform.Calls, c => c.Body is { } body && body.Contains($"\"kind\":\"{Field}\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_attribute_keeps_white_space_decodes_and_keeps_a_reference_as_it_says()
    {
        _platform.Add("dev:master-data--Wellbore:W1", Wellbore, new JsonObject
        {
            ["FacilityName"] = "  15/9-F-11  ",
            ["Code"] = "g%2Fcm3",
            ["WellID"] = "dev:master-data--Well:W:1700000000",
            ["Blank"] = "   ",
        });
        DimensionAttributeSpec[] attributes =
        [
            new("Trimmed", ["data.FacilityName"]),
            new("AsHeld", ["data.FacilityName"], Trim: false),
            new("Decoded", ["data.Code"], Decode: DimensionValueDecode.Url),
            new("Encoded", ["data.Code"]),
            new("Reference", ["data.WellID"], Keep: DimensionValueKeep.Reference),
            new("Id", ["data.WellID"], Keep: DimensionValueKeep.Id),
            new("BlankTrimmed", ["data.Blank"]),
            new("BlankAsHeld", ["data.Blank"], Trim: false),
        ];

        var read = await Labeler().ReadAsync(["dev:master-data--Wellbore:W1"], [], attributes, null, CancellationToken.None);

        Assert.Equal(
            [("Trimmed", "15/9-F-11"), ("AsHeld", "  15/9-F-11  "), ("Decoded", "g/cm3"), ("Encoded", "g%2Fcm3"),
             ("Reference", "dev:master-data--Well:W:1700000000"), ("Id", "dev:master-data--Well:W"), ("BlankAsHeld", "   ")],
            read.Attributes["dev:master-data--Wellbore:W1"].Select(a => (a.Name, a.Value)));
    }
}
