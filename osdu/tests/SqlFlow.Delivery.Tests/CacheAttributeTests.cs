using System.Net;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A cache type's attributes (osdu/docs/reference/flow/cache-attributes.md): a dimension's attributes, written and read exactly
/// as a dimension reads them, each cached record's id the key. An attribute reads the cached record, or through the records its
/// steps' references name, and is kept beside the type's paths under its name. Against a scripted platform, so nothing here
/// reaches OSDU or a database.
/// </summary>
public class CacheAttributeTests
{
    private const string Scope = "dev";

    private const string CursorPath = "/query_with_cursor";

    private const string QueryPath = "/api/search/v2/query";

    private const string StoragePath = "/api/storage/v2/query/records";

    private static string Wellbore(int i) => $"dev:master-data--Wellbore:WB-{i}";

    private static string Well(int i) => $"dev:master-data--Well:W-{i}";

    /// <summary>A cache flow of the given types (YAML list items indented by two spaces).</summary>
    private static CacheDefinition Parse(string types, string source = "  endpoint: https://osdu.example.com\n  headers: { data-partition-id: dev }")
        => new DeliveryDocumentLoader().ParseCache(
            "flowType: cache\nname: attributes-cache\nsource:\n" + source + "\ntypes:\n" + types + "\n",
            "cache/attributes-cache.yaml");

    [Fact]
    public void A_cache_type_takes_a_dimension_s_attributes_in_each_of_their_forms()
    {
        var cache = Parse("""
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                fields: [data.FacilityName]
                attributes:
                  WellName: [data.WellID, data.FacilityName]
                  Operator: data.CurrentOperatorID
                  FieldID: { path: 'data.GeoContexts[GeoTypeID$=:Field:].FieldID', keep: id }
                  Countries: { path: [data.GeoContexts.GeoPoliticalEntityID, data.GeoPoliticalEntityName], many: join, maxReferences: 50 }
                lookupKinds: [osdu:wks:master-data--Well:1.0.0]
            """);

        var type = Assert.Single(cache.Types);
        Assert.Equal(["FacilityName", "WellName", "Operator", "FieldID", "Countries"], type.Fields.Select(f => f.Name));
        Assert.False(type.Fields[0].IsAttribute);

        var wellName = type.Fields[1];
        Assert.True(wellName.IsAttribute);
        Assert.Equal(["data.WellID", "data.FacilityName"], wellName.Attribute!.Steps);
        Assert.Equal("osdu:wks:master-data--Well:1.0.0", wellName.LookupKinds["master-data--Well"]);
        Assert.Equal("{ path: [data.WellID, data.FacilityName], lookupKinds: [osdu:wks:master-data--Well:1.0.0] }", wellName.Path);

        var fieldId = type.Fields[3];
        Assert.Equal(DimensionValueKeep.Id, fieldId.Attribute!.Keep);
        Assert.Equal("{ path: data.GeoContexts[GeoTypeID$=:Field:].FieldID, keep: id, lookupKinds: [osdu:wks:master-data--Well:1.0.0] }", fieldId.Path);

        var countries = type.Fields[4].Attribute!;
        Assert.Equal((DimensionValueMany.Join, 50), (countries.Many, countries.MaxReferences));
    }

    [Fact]
    public void A_type_of_attributes_alone_lists_no_paths()
    {
        var type = Assert.Single(Parse("""
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                attributes:
                  WellName: [data.WellID, data.FacilityName]
            """).Types);

        Assert.Equal(["WellName"], type.Fields.Select(f => f.Name));
    }

    [Fact]
    public void What_a_cache_type_s_attribute_cannot_be_is_refused_when_the_flow_is_read()
    {
        static string Refused(string types, string? source = null)
            => Assert.Throws<FlowValidationException>(() => source is null ? Parse(types) : Parse(types, source)).Message;

        Assert.Contains(
            "types[0]: attributes.Logs collects data.Name, and a cached type is keyed by each record's own id",
            Refused("""
                  - kind: osdu:wks:master-data--Wellbore:1.0.0
                    attributes:
                      Logs: { collect: data.Name }
                """),
            StringComparison.Ordinal);

        Assert.Contains(
            "types[0]: lookupKinds names the kind each entity type's records are looked up in when an attribute reads through a reference, and the type reads no attribute",
            Refused("""
                  - kind: osdu:wks:master-data--Wellbore:1.0.0
                    fields: [data.FacilityName]
                    lookupKinds: [osdu:wks:master-data--Well:1.0.0]
                """),
            StringComparison.Ordinal);

        Assert.Contains(
            "types[0]: attributes.ID takes a name a cached record holds already (id)",
            Refused("""
                  - kind: osdu:wks:master-data--Wellbore:1.0.0
                    attributes:
                      ID: [data.WellID, data.FacilityName]
                """),
            StringComparison.Ordinal);

        Assert.Contains(
            "caches two paths under the name 'FacilityName'",
            Refused("""
                  - kind: osdu:wks:master-data--Wellbore:1.0.0
                    fields: [data.FacilityName]
                    attributes:
                      FacilityName: [data.WellID, data.FacilityName]
                """),
            StringComparison.Ordinal);

        // As a dimension's: at most three steps, each a path its filters parse.
        Assert.Contains(
            "attributes.Basin reads through 4 records; an attribute reads through at most 3",
            Refused("""
                  - kind: osdu:wks:master-data--Wellbore:1.0.0
                    attributes:
                      Basin: [data.WellID, data.FieldID, data.BasinID, data.BasinName]
                """),
            StringComparison.Ordinal);

        Assert.Contains(
            "types[0] holds table OsduData.silver.UnitAlias, which takes no 'attributes'",
            Refused(
                """
                  - table: OsduData.silver.UnitAlias
                    key: source_unit
                    fields: [osdu_unit]
                    attributes:
                      Unit: data.Code
                """,
                "  connection: ${env:OSDU_DATA_DB}"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Two_flows_declaring_one_attribute_agree_only_when_every_setting_does()
    {
        var name = new ReferenceFieldSpec(new DimensionAttributeSpec("WellName", ["data.WellID", "data.FacilityName"]));
        var joined = new ReferenceFieldSpec(new DimensionAttributeSpec("WellName", ["data.WellID", "data.FacilityName"], Many: DimensionValueMany.Join));
        var declaration = new CacheDeclaration(Scope,
        [
            new CacheTypeDeclaration("project-a", "Wellbore", "master-data--Wellbore", "osdu:wks:master-data--Wellbore:*", "*", [name], CacheChangeMode.Auto),
        ]);

        ReferenceTypeSpec Declared(params ReferenceFieldSpec[] fields) => new()
        {
            Name = "Wellbore", EntityType = "master-data--Wellbore", Kind = "osdu:wks:master-data--Wellbore:*", Fields = fields,
        };

        Assert.Empty(declaration.Conflicts("project-b", Declared(new ReferenceFieldSpec(new DimensionAttributeSpec("WellName", ["data.WellID", "data.FacilityName"])))));
        Assert.Contains(
            "Wellbore.WellName is cached from { path: [data.WellID, data.FacilityName] } by cache flow 'project-a' and from { path: [data.WellID, data.FacilityName], many: join } by 'project-b'",
            declaration.Conflicts("project-b", Declared(joined)));
        Assert.Single(declaration.Conflicts("project-b", Declared(new ReferenceFieldSpec("data.WellName", "WellName"))));

        // Widened, a flow's capture reads every attribute the partition keeps for the type.
        var widened = declaration.Widen(Declared(new ReferenceFieldSpec("data.FacilityName")));
        Assert.Equal(["FacilityName", "WellName"], widened.Fields.Select(f => f.Name));
        Assert.True(widened.Fields[1].IsAttribute);
    }

    [Fact]
    public void The_catalog_keeps_an_attribute_with_every_setting_and_a_path_as_it_always_did()
    {
        var attribute = new ReferenceFieldSpec(
            new DimensionAttributeSpec("FieldID", ["data.GeoContexts[GeoTypeID$=:Field:].FieldID"], null, DimensionValueKeep.Reference, DimensionValueMany.Max, DimensionValueDecode.Url, 7, Trim: false),
            new Dictionary<string, string> { ["master-data--Field"] = "osdu:wks:master-data--Field:1.0.0" });

        var json = attribute.ToJson();
        var read = ReferenceFieldSpec.FromJson(JsonNode.Parse(json.ToJsonString())!.AsObject())!;
        Assert.Equal(attribute, read);
        Assert.Equal(attribute.Attribute!.Steps, read.Attribute!.Steps);
        Assert.Equal(
            (DimensionValueKeep.Reference, DimensionValueMany.Max, DimensionValueDecode.Url, 7, false),
            (read.Attribute.Keep, read.Attribute.Many, read.Attribute.Decode, read.Attribute.MaxReferences, read.Attribute.Trim));
        Assert.Equal("osdu:wks:master-data--Field:1.0.0", read.LookupKinds["master-data--Field"]);

        // A path is kept exactly as before, so no declaration the sync wrote earlier reads as changed.
        Assert.Equal("""{"path":"data.Code","as":"Code"}""", new ReferenceFieldSpec("data.Code").ToJson().ToJsonString());
        Assert.False(ReferenceFieldSpec.FromJson(JsonNode.Parse("""{"path":"data.Code","as":"Code"}""")!.AsObject())!.IsAttribute);
    }

    private static readonly ReferenceTypeSpec Wellbores = new()
    {
        Name = "Wellbore",
        EntityType = "master-data--Wellbore",
        Kind = "osdu:wks:master-data--Wellbore:*",
        Fields =
        [
            new ReferenceFieldSpec("data.FacilityName"),
            new ReferenceFieldSpec(new DimensionAttributeSpec("WellName", ["data.WellID", "data.FacilityName"])),
            new ReferenceFieldSpec(new DimensionAttributeSpec("FieldID", ["data.GeoContexts[GeoTypeID$=:Field:].FieldID"], null, DimensionValueKeep.Id)),
        ],
    };

    /// <summary>A wellbore as the index or storage holds it: its name, its well, and a field and a country among its contexts.</summary>
    private static JsonObject WellboreRecord(int i) => new()
    {
        ["id"] = Wellbore(i),
        ["kind"] = "osdu:wks:master-data--Wellbore:1.0.0",
        ["data"] = new JsonObject
        {
            ["FacilityName"] = $"WB {i}",
            ["WellID"] = Well(i % 2) + ":",
            ["GeoContexts"] = new JsonArray(
                new JsonObject { ["GeoTypeID"] = "dev:reference-data--GeoPoliticalEntityType:Country:", ["GeoPoliticalEntityID"] = "dev:master-data--GeoPoliticalEntity:NO:" },
                new JsonObject { ["GeoTypeID"] = "dev:reference-data--FieldType:Field:", ["FieldID"] = $"dev:master-data--Field:F-{i}:" }),
        },
    };

    /// <summary>A well as the index or storage holds it, in <paramref name="kind"/>, its extension properties beside its name.</summary>
    private static JsonObject WellRecord(string id, string kind = "osdu:wks:master-data--Well:1.0.0") => new()
    {
        ["id"] = id,
        ["kind"] = kind,
        ["data"] = new JsonObject
        {
            ["FacilityName"] = "Well " + id[^1..],
            ["ExtensionProperties"] = new JsonObject { ["WellDb"] = new JsonObject { ["Region"] = "North " + id[^1..] } },
        },
    };

    private static async Task<(SnapshotBuilder Builder, OsduConnection Osdu)> BuildAsync(FakeHttpHandler handler)
    {
        var store = new FixedCacheStore(Scope, "attributes", ReferenceSnapshot.Empty);
        var builder = new SnapshotBuilder(store, Scope, "attributes", new TestClock(), Samples.Logger<SnapshotBuilder>());
        var osdu = await OsduConnection.CreateAsync(
            "http://localhost/osdu",
            new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = Scope },
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]),
            handler,
            allowLoopback: true);
        return (builder, osdu);
    }

    /// <summary>The ids a search by id asks for: the quoted values of its query.</summary>
    private static List<string> AskedIds(string? body)
        => [.. System.Text.RegularExpressions.Regex.Matches(JsonNode.Parse(body!)!["query"]!.GetValue<string>(), "\"([^\"]+)\"").Select(m => m.Groups[1].Value)];

    [Fact]
    public async Task An_attribute_reads_the_record_its_reference_names_through_the_search_as_a_dimension_does()
    {
        var wells = new Dictionary<string, JsonObject>(StringComparer.Ordinal) { [Well(0)] = WellRecord(Well(0)), [Well(1)] = WellRecord(Well(1)) };
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, CursorPath, hit => FakeHttpHandler.Json(
                HttpStatusCode.OK,
                hit == 0
                    ? new JsonObject { ["cursor"] = "c1", ["totalCount"] = 3, ["results"] = new JsonArray(WellboreRecord(1), WellboreRecord(2), WellboreRecord(3)) }.ToJsonString()
                    : """{"cursor":"c1","results":[]}"""))
            .On(HttpMethod.Post, QueryPath, (_, body) =>
            {
                var asked = AskedIds(body);
                var found = asked.Where(wells.ContainsKey).Select(id => (JsonNode?)JsonNode.Parse(wells[id].ToJsonString())).ToArray();
                return FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject { ["results"] = new JsonArray(found), ["totalCount"] = found.Length }.ToJsonString());
            });
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, Wellbores);

            // The type's search returns what the first step of each attribute reads of a wellbore, filters' properties included.
            var search = handler.Calls.First(c => c.Uri.AbsolutePath.EndsWith(CursorPath, StringComparison.Ordinal));
            var returned = JsonNode.Parse(search.Body!)!["returnedFields"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Assert.Equal(["id", "data.FacilityName", "data.WellID", "data.GeoContexts.FieldID", "data.GeoContexts.GeoTypeID"], returned);

            // The wellbores in hand answer the first step: only the wells are read by id, in the well's kind, once each.
            var byId = handler.Calls.Where(c => c.Uri.AbsolutePath.EndsWith(QueryPath, StringComparison.Ordinal)).ToList();
            var asked = Assert.Single(byId);
            Assert.Equal("*:*:master-data--Well:*", JsonNode.Parse(asked.Body!)!["kind"]!.GetValue<string>());
            Assert.Equal([Well(0), Well(1)], AskedIds(asked.Body).Order(StringComparer.Ordinal));

            var first = captured.ById(Wellbore(1))!;
            Assert.Equal("WB 1", first.Fields["FacilityName"].Text);
            Assert.Equal("Well 1", first.Fields["WellName"].Text);
            Assert.Equal("Well 0", captured.ById(Wellbore(2))!.Fields["WellName"].Text);

            // keep: id keeps the reference of the context the filter picked, as the id of the record it names.
            Assert.Equal("dev:master-data--Field:F-1", first.Fields["FieldID"].Text);
        }
    }

    [Fact]
    public async Task A_type_read_whole_reads_the_records_its_attributes_reach_whole_from_storage_in_the_lookup_kind()
    {
        // The second well is of another kind than the type looks wells up in, so it is not read, as a dimension would not find it.
        var stored = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            [Wellbore(1)] = WellboreRecord(1),
            [Wellbore(2)] = WellboreRecord(2),
            [Well(1)] = WellRecord(Well(1)),
            [Well(0)] = WellRecord(Well(0), "osdu:wks:master-data--Well:2.0.0"),
        };
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal) { ["master-data--Well"] = "osdu:wks:master-data--Well:1.0.0" };
        var spec = Wellbores with
        {
            FetchRecords = true,
            Fields =
            [
                new ReferenceFieldSpec(new DimensionAttributeSpec("Region", ["data.WellID", "data.ExtensionProperties.WellDb.Region"]), kinds),
            ],
        };
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, CursorPath, hit => FakeHttpHandler.Json(
                HttpStatusCode.OK,
                hit == 0 ? $$"""{"cursor":"c1","totalCount":2,"results":[{"id":"{{Wellbore(1)}}"},{"id":"{{Wellbore(2)}}"}]}""" : """{"cursor":"c1","results":[]}"""))
            .On(HttpMethod.Post, StoragePath, (_, body) =>
            {
                var asked = JsonNode.Parse(body!)!["records"]!.AsArray().Select(n => n!.GetValue<string>());
                var found = asked.Where(stored.ContainsKey).Select(id => (JsonNode?)JsonNode.Parse(stored[id].ToJsonString())).ToArray();
                return FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject { ["records"] = new JsonArray(found) }.ToJsonString());
            });
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, spec);

            Assert.Equal("North 1", captured.ById(Wellbore(1))!.Fields["Region"].Text);
            Assert.False(captured.ById(Wellbore(2))!.Fields.ContainsKey("Region"));

            // Nothing is read by id through the search: the wellbores whole, then their wells, from storage.
            Assert.DoesNotContain(handler.Calls, c => c.Uri.AbsolutePath.EndsWith(QueryPath, StringComparison.Ordinal));
            var reads = handler.Calls.Where(c => c.Uri.AbsolutePath.EndsWith(StoragePath, StringComparison.Ordinal))
                .Select(c => JsonNode.Parse(c.Body!)!["records"]!.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal).ToList())
                .ToList();
            Assert.Equal([[Wellbore(1), Wellbore(2)], [Well(0), Well(1)]], reads);
        }
    }

    [Fact]
    public async Task Several_records_reached_give_every_value_joined_when_the_attribute_says_so()
    {
        var wells = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            [Well(1)] = WellRecord(Well(1)),
            [Well(2)] = WellRecord(Well(2)),
        };
        var wellbore = WellboreRecord(7);
        wellbore["data"]!["WellID"] = new JsonArray(Well(2) + ":", Well(1) + ":");
        var spec = Wellbores with
        {
            Fields = [new ReferenceFieldSpec(new DimensionAttributeSpec("Wells", ["data.WellID", "data.FacilityName"], Many: DimensionValueMany.Join))],
        };
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, CursorPath, hit => FakeHttpHandler.Json(
                HttpStatusCode.OK,
                hit == 0 ? new JsonObject { ["cursor"] = "c1", ["totalCount"] = 1, ["results"] = new JsonArray(wellbore) }.ToJsonString() : """{"cursor":"c1","results":[]}"""))
            .On(HttpMethod.Post, QueryPath, (_, body) =>
            {
                var found = AskedIds(body).Where(wells.ContainsKey).Select(id => (JsonNode?)JsonNode.Parse(wells[id].ToJsonString())).ToArray();
                return FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject { ["results"] = new JsonArray(found), ["totalCount"] = found.Length }.ToJsonString());
            });
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, spec);

            // In the order the references were reached, each once, joined as a dimension joins them.
            Assert.Equal("Well 2; Well 1", Assert.Single(captured.Items).Fields["Wells"].Text);
        }
    }
}
