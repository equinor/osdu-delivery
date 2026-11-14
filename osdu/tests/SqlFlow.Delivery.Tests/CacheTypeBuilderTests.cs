using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The explorer's cache builder as YAML (osdu/docs/reference/concepts/explorer-cache-builder.md): a draft written as the
/// item a cache flow lists under types, in the documented style, read back by the document loader into exactly the type it
/// says; what is missing said before anything is read; what the loader refuses said in its own words, pointed at the part
/// it is about; and the first records of a type made into what the cache would keep of them, read as a refresh reads a
/// page. Against a scripted platform, so nothing here reaches OSDU or a database.
/// </summary>
public class CacheTypeBuilderTests
{
    private const string Wellbores = "osdu:wks:master-data--Wellbore:*";
    private const string FieldId = "data.GeoContexts[GeoTypeID$=:Field:].FieldID";

    /// <summary>The wellbores a well log mapping links to, each with its well's name and its field, as the builder holds them.</summary>
    private static CacheTypeDraft Wellbore() => new()
    {
        Kind = Wellbores,
        Fields = [new CacheTypeDraftField("data.FacilityName", null), new CacheTypeDraftField("data.NameAliases.AliasName", "Alias")],
        Attributes =
        [
            new CacheTypeDraftAttributeSpec("WellName", ["data.WellID", "data.FacilityName"]),
            new CacheTypeDraftAttributeSpec("FieldID", [FieldId]),
        ],
    };

    private static ReferenceTypeSpec Read(CacheTypeDraft draft)
        => new DeliveryDocumentLoader().ParseCache(CacheTypeBuilder.Document(CacheTypeBuilder.ToYaml(draft)), CacheTypeBuilder.CheckSource).Types[0];

    private static DimensionDraftIssue Refused(CacheTypeDraft draft)
        => CacheTypeBuilder.FromLoader(Assert.Throws<FlowValidationException>(() => Read(draft)).Message);

    [Fact]
    public void A_type_is_written_as_a_cache_flow_lists_it_and_read_back_as_the_type_it_says()
    {
        var yaml = CacheTypeBuilder.ToYaml(Wellbore());

        // A kind with a wildcard and a path with a filter are quoted, since YAML reads neither back bare.
        Assert.Equal(
            """
              - kind: 'osdu:wks:master-data--Wellbore:*'
                fields:
                  - data.FacilityName
                  - { path: data.NameAliases.AliasName, as: Alias }
                attributes:
                  WellName: [data.WellID, data.FacilityName]
                  FieldID: 'data.GeoContexts[GeoTypeID$=:Field:].FieldID'

            """.ReplaceLineEndings("\n"),
            yaml);

        // The name is the kind's own while the draft gives none; each value is kept under the name the item says.
        var type = Read(Wellbore());
        Assert.Equal(("Wellbore", "master-data--Wellbore", Wellbores, "*", false), (type.Name, type.EntityType, type.Kind, type.Query, type.FetchRecords));
        Assert.Equal(["FacilityName", "Alias", "WellName", "FieldID"], type.Fields.Select(f => f.Name));
        Assert.Equal([false, false, true, true], type.Fields.Select(f => f.IsAttribute));
        Assert.Equal(["data.WellID", "data.FacilityName"], type.Fields[2].Attribute!.Steps);
        Assert.Equal(CacheChangeMode.Auto, type.OnChange);
    }

    [Fact]
    public void Every_setting_a_draft_holds_is_written_and_read_back()
    {
        var draft = Wellbore() with { Name = "AcmeWellbore", Query = "data.FacilityName:A*", FetchRecords = true, OnChange = "approve" };

        var yaml = CacheTypeBuilder.ToYaml(draft);
        Assert.Contains("    name: AcmeWellbore\n    query: 'data.FacilityName:A*'\n", yaml, StringComparison.Ordinal);
        Assert.Contains("    fetchRecords: true\n", yaml, StringComparison.Ordinal);
        Assert.EndsWith("    onChange: approve\n", yaml, StringComparison.Ordinal);

        var type = Read(draft);
        Assert.Equal(("AcmeWellbore", "data.FacilityName:A*", true, CacheChangeMode.Approve), (type.Name, type.Query, type.FetchRecords, type.OnChange));
    }

    [Fact]
    public void A_draft_missing_its_kind_or_anything_to_keep_says_so_before_it_is_read()
    {
        var nothing = CacheTypeBuilder.Incomplete(new CacheTypeDraft());
        Assert.Equal(["kind", "fields"], nothing.Select(i => i.Target));
        Assert.All(nothing, issue => Assert.Equal(DimensionDraftIssue.Error, issue.Severity));

        // A type of attributes alone keeps something, and so does one of paths alone.
        Assert.Empty(CacheTypeBuilder.Incomplete(new CacheTypeDraft { Kind = Wellbores, Attributes = [new CacheTypeDraftAttributeSpec("WellName", ["data.WellID", "data.FacilityName"])] }));
        Assert.Empty(CacheTypeBuilder.Incomplete(new CacheTypeDraft { Kind = Wellbores, Fields = [new CacheTypeDraftField("data.FacilityName", null)] }));
        Assert.Equal(["fields"], CacheTypeBuilder.Incomplete(new CacheTypeDraft { Kind = Wellbores, Fields = [new CacheTypeDraftField(" ", null)] }).Select(i => i.Target));
    }

    [Fact]
    public void What_the_loader_refuses_is_said_without_the_documents_name_at_the_part_it_is_about()
    {
        var kind = Refused(Wellbore() with { Kind = "wellbores" });
        Assert.Equal("kind", kind.Target);
        Assert.StartsWith("kind 'wellbores' is not authority:source:entityType:version", kind.Message, StringComparison.Ordinal);

        // Two values under one name: the attribute is named like the path's own name.
        var twice = Refused(Wellbore() with { Attributes = [new CacheTypeDraftAttributeSpec("FacilityName", ["data.WellID", "data.FacilityName"])] });
        Assert.DoesNotContain("builder", twice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("types[0]", twice.Message, StringComparison.Ordinal);
        Assert.Equal("fields", twice.Target);

        // An attribute reading through more records than one may.
        var far = Refused(Wellbore() with { Attributes = [new CacheTypeDraftAttributeSpec("Far", ["data.A", "data.B", "data.C", "data.D"])] });
        Assert.Equal("attributes.Far", far.Target);

        var mode = Refused(Wellbore() with { OnChange = "later" });
        Assert.Equal("onChange", mode.Target);
    }

    private static string Id(int i) => $"dev:master-data--Wellbore:WB-{i:D4}";

    private static async Task<OsduConnection> ConnectAsync(FakeHttpHandler handler)
        => await OsduConnection.CreateAsync(
            "http://localhost/osdu",
            new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = "dev" },
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]),
            handler,
            allowLoopback: true);

    [Fact]
    public async Task A_sample_is_the_first_records_as_the_cache_would_keep_them_with_what_no_record_gave_noted()
    {
        var page = new JsonObject
        {
            ["totalCount"] = 4210,
            ["results"] = new JsonArray(Enumerable.Range(1, 3).Select(i => (JsonNode?)new JsonObject
            {
                ["id"] = Id(i),
                ["data"] = new JsonObject
                {
                    ["FacilityName"] = "Facility " + i,
                    ["NameAliases"] = new JsonArray(new JsonObject { ["AliasName"] = "F" + i }, new JsonObject { ["AliasName"] = "Fac-" + i }),
                },
            }).ToArray()),
        }.ToJsonString();
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", _ => FakeHttpHandler.Json(HttpStatusCode.OK, page));
        using var osdu = await ConnectAsync(handler);
        var type = Read(new CacheTypeDraft
        {
            Kind = Wellbores,
            Query = "data.FacilityName:F*",
            Fields =
            [
                new CacheTypeDraftField("data.FacilityName", null), new CacheTypeDraftField("data.NameAliases.AliasName", "Alias"),
                new CacheTypeDraftField("data.ExtensionProperties.Region", "Region"),
            ],
        });

        var sample = await SnapshotBuilder.SampleTypeAsync(osdu.Client, type, 3, Samples.Logger<SnapshotBuilder>());

        // One search, for the first records alone, asking what the type keeps of each.
        var search = Assert.Single(handler.Calls);
        var asked = JsonNode.Parse(search.Body!)!;
        Assert.Equal((Wellbores, "data.FacilityName:F*", 3), (asked["kind"]!.GetValue<string>(), asked["query"]!.GetValue<string>(), asked["limit"]!.GetValue<int>()));
        Assert.Equal(
            ["id", "data.FacilityName", "data.NameAliases.AliasName", "data.ExtensionProperties.Region"],
            asked["returnedFields"]!.AsArray().Select(n => n!.GetValue<string>()));

        Assert.Equal(("Wellbore", "master-data--Wellbore", 4210L, (string?)null), (sample.Name, sample.EntityType, sample.Total, sample.Refusal));
        Assert.Equal([Id(1), Id(2), Id(3)], sample.Records.Select(r => r.Id));
        Assert.Equal("Facility 2", sample.Records[1].Fields["FacilityName"]!.GetValue<string>());
        // A path through a list keeps every value found along it.
        Assert.Equal(["F2", "Fac-2"], sample.Records[1].Fields["Alias"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(sample.Records[1].Fields.ContainsKey("Region"));

        Assert.Equal(
            [("FacilityName", "data.FacilityName", false, 3), ("Alias", "data.NameAliases.AliasName", false, 3), ("Region", "data.ExtensionProperties.Region", false, 0)],
            sample.Columns.Select(c => (c.Name, c.Path, c.Attribute, c.Held)));
        var note = Assert.Single(sample.Notes);
        Assert.StartsWith("None of these records holds data.ExtensionProperties.Region.", note, StringComparison.Ordinal);
        Assert.Contains("read the records whole from storage", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_query_the_search_service_refuses_is_answered_with_its_words()
    {
        var handler = new FakeHttpHandler().On(
            HttpMethod.Post, "/api/search/v2/query", _ => FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"code":400,"reason":"Bad Request","message":"Invalid parameters were given on search request"}"""));
        using var osdu = await ConnectAsync(handler);
        var type = Read(new CacheTypeDraft { Kind = Wellbores, Query = "data.FacilityName:(", Fields = [new CacheTypeDraftField("data.FacilityName", null)] });

        var sample = await SnapshotBuilder.SampleTypeAsync(osdu.Client, type, 25, Samples.Logger<SnapshotBuilder>());

        Assert.Contains("Invalid parameters", sample.Refusal, StringComparison.Ordinal);
        Assert.Empty(sample.Records);
        Assert.Equal(["FacilityName"], sample.Columns.Select(c => c.Name));
    }
}
