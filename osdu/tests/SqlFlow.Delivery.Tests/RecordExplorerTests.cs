using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Http;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Search;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The explorer's reads of what OSDU holds (osdu/docs/reference/concepts/explorer.md): what a reader typed read once
/// into a query, a page of the records it finds with their names and the groups of a property, the kinds they are of,
/// the properties a kind's records hold, a query the service refuses answered plainer or with its words, and a record
/// read from the storage service.
/// </summary>
[Trait("Category", "Integration")]
public class RecordExplorerTests
{
    private const string Wellbore = "dev:master-data--Wellbore:WB-C-3-C-28-B";

    private static ExplorerReading Read(string? text, string? kind = null, bool lucene = false)
        => RecordExplorer.Interpret(new ExplorerSearch { Text = text, Kind = kind, Lucene = lucene }, "dev");

    [Fact]
    public void Nothing_typed_is_every_record_and_a_lucene_query_goes_as_written()
    {
        Assert.Equal(new ExplorerReading("everything", null), Read("  "));
        Assert.Equal(new ExplorerReading("lucene", "data.FacilityName:\"WB C*\""), Read(" data.FacilityName:\"WB C*\" ", lucene: true));
    }

    [Fact]
    public void A_whole_id_finds_the_record_the_ids_that_go_on_from_it_and_its_unique_part_under_another_type()
    {
        var reading = Read(Wellbore + ":1712345678901234");

        Assert.Equal("id", reading.Reading);
        Assert.Equal(
            "id:\"dev:master-data--Wellbore:WB-C-3-C-28-B\" OR id:dev\\:master\\-data\\-\\-Wellbore\\:WB\\-C\\-3\\-C\\-28\\-B* OR id:dev\\:*\\:WB\\-C\\-3\\-C\\-28\\-B",
            reading.Query);
        Assert.Equal("id:\"dev:master-data--Wellbore:WB-C-3-C-28-B\" OR id:dev\\:master\\-data\\-\\-Wellbore\\:WB\\-C\\-3\\-C\\-28\\-B*", reading.Plainer);
        Assert.Equal("the same unique part under another type", reading.Dropped);
    }

    [Fact]
    public void The_start_of_an_id_finds_the_ids_that_start_with_it_in_the_partition_when_it_starts_at_its_type()
    {
        Assert.Equal(new ExplorerReading("idPrefix", "id:dev\\:master\\-data\\-\\-Wellbore\\:WB\\-C*"), Read("master-data--Wellbore:WB-C"));
        Assert.Equal(new ExplorerReading("idPrefix", "id:test\\:work\\-product\\-component\\-\\-WellLog\\:*"), Read("test:work-product-component--WellLog:"));
    }

    [Fact]
    public void Text_is_a_phrase_and_one_word_in_a_kind_of_one_type_may_also_be_the_end_of_an_id_of_it()
    {
        Assert.Equal(new ExplorerReading("text", "\"WB C/3-C-28 B\""), Read("WB C/3-C-28 B", "*:*:master-data--Wellbore:*"));

        var reading = Read("WB-C-3-C-28-B", "*:*:master-data--Wellbore:*");
        Assert.Equal(
            "\"WB-C-3-C-28-B\" OR id:\"dev:master-data--Wellbore:WB-C-3-C-28-B\" OR id:dev\\:master\\-data\\-\\-Wellbore\\:WB\\-C\\-3\\-C\\-28\\-B*",
            reading.Query);
        Assert.Equal("\"WB-C-3-C-28-B\"", reading.Plainer);

        // In every kind, a word is a phrase alone; a minted unique part is looked for at the end of any id of the partition.
        Assert.Equal(new ExplorerReading("text", "\"FieldA\""), Read("FieldA"));
        var minted = Read("26c5ab12-4f1e-4d2a-9b7e-0c1d2e3f4a5b");
        Assert.Equal("\"26c5ab12-4f1e-4d2a-9b7e-0c1d2e3f4a5b\" OR id:dev\\:*\\:26c5ab12\\-4f1e\\-4d2a\\-9b7e\\-0c1d2e3f4a5b*", minted.Query);
        Assert.Equal("\"26c5ab12-4f1e-4d2a-9b7e-0c1d2e3f4a5b\"", minted.Plainer);
    }

    [Fact]
    public void The_mentions_of_a_record_are_the_records_holding_its_id_the_record_itself_left_out()
    {
        var reading = RecordExplorer.Interpret(new ExplorerSearch { Mentions = Wellbore + ":" }, "dev");

        Assert.Equal(new ExplorerReading("mentions", $"\"{Wellbore}\" AND NOT id:\"{Wellbore}\""), reading);
    }

    [Fact]
    public void A_value_with_an_angle_bracket_is_asked_as_a_phrase_since_no_escape_carries_it()
    {
        Assert.Equal(new ExplorerReading("text", "\"dev:x--Y<1\""), Read("dev:x--Y<1"));
    }

    [Fact]
    public void Property_values_narrow_the_reading_each_asked_exactly_as_the_platform_indexes_it()
    {
        var filters = new[]
        {
            new ExplorerFilter { Path = "data.FacilityTypeID", Value = "dev:reference-data--FacilityType:Wellbore:" },
            new ExplorerFilter { Path = "legal.legaltags", Index = OsduFieldIndex.Keyword, Value = "dev-osdu-private-legal" },
        };

        Assert.Equal(
            "(\"WB C\") AND data.FacilityTypeID.keyword:\"dev:reference-data--FacilityType:Wellbore:\" AND legal.legaltags:\"dev-osdu-private-legal\"",
            RecordExplorer.Compose("\"WB C\"", filters));
        Assert.Equal("legal.legaltags:\"dev-osdu-private-legal\"", RecordExplorer.Compose(null, filters[1..]));
        Assert.Null(RecordExplorer.Compose(null, []));
    }

    [Theory]
    [InlineData("{\"kind\":\"osdu:wks\"}", "is not a kind")]
    [InlineData("{\"offset\":9950,\"limit\":100}", "first 10,000 records")]
    [InlineData("{\"limit\":500}", "1 to 200 records")]
    [InlineData("{\"text\":\"a\",\"mentions\":\"dev:master-data--Well:1\"}", "not both")]
    [InlineData("{\"mentions\":\"not an id\"}", "is not an OSDU record id")]
    [InlineData("{\"filters\":[{\"path\":\"data.Name\",\"value\":\"null\"}]}", "cannot narrow to data.Name")]
    [InlineData("{\"facet\":{\"path\":\"data..Name\"}}", "cannot be grouped by")]
    [InlineData("{\"sort\":\"newest\"}", "is not one the explorer reads")]
    [InlineData("{\"unknown\":true}", "is not one the explorer reads")]
    public void A_search_that_cannot_be_asked_is_refused_with_why(string json, string said)
    {
        var refused = Assert.Throws<DeliveryException>(() => ExplorerSearch.Parse(json));
        Assert.Contains(said, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_search_crosses_to_the_node_as_it_was_asked()
    {
        var search = new ExplorerSearch
        {
            Text = "WB C",
            Kind = "*:*:master-data--Wellbore:*",
            Filters = [new ExplorerFilter { Path = "acl.viewers", Index = OsduFieldIndex.Keyword, Value = "data.default.viewers@dev.dataservices.energy" }],
            Sort = ExplorerSort.Modified,
            Offset = 200,
            Limit = 100,
            Facet = new ExplorerField { Path = "data.FacilityTypeID" },
        };

        var json = search.ToJson();
        var back = ExplorerSearch.Parse(json);

        Assert.Contains("\"sort\":\"modified\"", json, StringComparison.Ordinal);
        Assert.Contains("\"index\":\"keyword\"", json, StringComparison.Ordinal);
        Assert.Equal(json, back.ToJson());
    }

    [Fact]
    public async Task A_page_asks_the_kind_query_order_and_window_and_lists_each_record_by_its_name()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, new JsonObject
        {
            ["results"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = Wellbore,
                    ["kind"] = "osdu:wks:master-data--Wellbore:1.1.0",
                    ["version"] = 1712345678901234,
                    ["modifyTime"] = "2026-09-01T10:00:00.000Z",
                    ["modifyUser"] = "loader@dev",
                    ["data"] = new JsonObject { ["FacilityName"] = "WB C/3-C-28 B", ["Name"] = "not this one" },
                },
                new JsonObject { ["id"] = "dev:reference-data--UnitOfMeasure:m", ["kind"] = "osdu:wks:reference-data--UnitOfMeasure:1.0.0", ["data.Code"] = "m" },
                new JsonObject { ["kind"] = "osdu:wks:master-data--Wellbore:1.1.0" }),
            ["aggregations"] = new JsonArray(new JsonObject { ["key"] = "dev:reference-data--FacilityType:Wellbore:", ["count"] = 2 }),
            ["totalCount"] = 1234567,
        }.ToJsonString());
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var page = await explorer.SearchAsync(new ExplorerSearch
            {
                Text = "WB C",
                Kind = "*:*:master-data--Wellbore:*",
                Sort = ExplorerSort.Modified,
                Offset = 100,
                Limit = 100,
                Facet = new ExplorerField { Path = "data.FacilityTypeID" },
            }, CancellationToken.None);

            Assert.Equal(("text", "\"WB C\"", 1234567L, 100), (page.Reading, page.Query, page.Total, page.Offset));
            Assert.Null(page.Refusal);
            Assert.Empty(page.Notes);
            Assert.Equal(2, page.Hits.Count);
            Assert.Equal(new ExplorerHit(Wellbore, "osdu:wks:master-data--Wellbore:1.1.0", 1712345678901234, "WB C/3-C-28 B", "data.FacilityName", null, null, "2026-09-01T10:00:00.000Z", "loader@dev"), page.Hits[0]);
            Assert.Equal(("m", "data.Code"), (page.Hits[1].Name, page.Hits[1].NameField));
            Assert.Equal(new ExplorerBucket("dev:reference-data--FacilityType:Wellbore:", 2), Assert.Single(page.Facet!));

            var body = JsonNode.Parse(Assert.Single(handler.Calls).Body!)!;
            Assert.Equal("*:*:master-data--Wellbore:*", body["kind"]!.GetValue<string>());
            Assert.Equal("\"WB C\"", body["query"]!.GetValue<string>());
            Assert.Equal((100, 100, true), (body["offset"]!.GetValue<int>(), body["limit"]!.GetValue<int>(), body["trackTotalCount"]!.GetValue<bool>()));
            Assert.Equal("data.FacilityTypeID.keyword", body["aggregateBy"]!.GetValue<string>());
            Assert.Equal(["modifyTime"], body["sort"]!["field"]!.AsArray().Select(f => f!.GetValue<string>()));
            Assert.Equal(["DESC"], body["sort"]!["order"]!.AsArray().Select(f => f!.GetValue<string>()));
            Assert.Contains("data.FacilityName", body["returnedFields"]!.AsArray().Select(f => f!.GetValue<string>()));
            Assert.Equal("dev", Assert.Single(handler.Calls).Headers["data-partition-id"]);
        }

        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task An_order_the_service_will_not_sort_by_is_dropped_with_a_note()
    {
        var sorted = new FakeHttpHandler()
            .OnMatch(r => Body(r).Contains("\"sort\"", StringComparison.Ordinal), _ => FakeHttpHandler.Json(HttpStatusCode.BadRequest, "{\"code\":400,\"message\":\"No mapping found for [modifyTime] in order to sort on\"}"))
            .OnMatch(_ => true, _ => FakeHttpHandler.Json(HttpStatusCode.OK, Result(3, Wellbore)));
        var (explorer, runtime) = Explorer(sorted);
        using (runtime)
        {
            var page = await explorer.SearchAsync(new ExplorerSearch { Kind = "*:*:master-data--Wellbore:*", Sort = ExplorerSort.Modified }, CancellationToken.None);

            Assert.Equal(3, page.Total);
            Assert.Null(page.Refusal);
            Assert.Contains(page.Notes, n => n.Contains("would not order these records by modifyTime", StringComparison.Ordinal));
            Assert.Contains(page.Notes, n => n.Contains("No mapping found for [modifyTime]", StringComparison.Ordinal));
            Assert.Equal(2, sorted.Calls.Count);
        }
    }

    [Fact]
    public async Task A_clause_the_service_refuses_is_dropped_for_the_core_of_the_reading_with_a_note()
    {
        var handler = new FakeHttpHandler()
            .OnMatch(r => Body(r).Contains("id:dev\\\\:*", StringComparison.Ordinal), _ => FakeHttpHandler.Json(HttpStatusCode.BadRequest, "{\"message\":\"wildcard queries are not allowed\"}"))
            .OnMatch(_ => true, _ => FakeHttpHandler.Json(HttpStatusCode.OK, Result(1, Wellbore)));
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var page = await explorer.SearchAsync(new ExplorerSearch { Text = "26c5ab12-4f1e-4d2a" }, CancellationToken.None);

            Assert.Equal(("\"26c5ab12-4f1e-4d2a\"", 1L), (page.Query, page.Total));
            Assert.Contains(page.Notes, n => n.Contains("would not look for the records whose id ends with the word", StringComparison.Ordinal));
            Assert.Equal(2, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task A_query_still_refused_is_the_page_with_the_services_words_not_a_failure()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.BadRequest, "{\"message\":\"Failed to parse query [data.Name:(]\"}");
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var page = await explorer.SearchAsync(new ExplorerSearch { Text = "data.Name:(", Lucene = true }, CancellationToken.None);

            Assert.Equal(0, page.Total);
            Assert.Empty(page.Hits);
            Assert.Contains("Failed to parse query", page.Refusal, StringComparison.Ordinal);
            Assert.Single(handler.Calls);
        }
    }

    [Fact]
    public async Task A_failure_other_than_a_refusal_fails_the_page_rather_than_passing_for_nothing_found()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.Forbidden, "{\"message\":\"no access\"}");
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => explorer.SearchAsync(new ExplorerSearch(), CancellationToken.None));
        }
    }

    [Fact]
    public async Task The_kinds_of_what_a_text_finds_are_counted_across_every_kind_in_order()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject { ["id"] = Wellbore }),
            ["aggregations"] = new JsonArray(
                new JsonObject { ["key"] = "osdu:wks:work-product-component--WellLog:1.4.0", ["count"] = 12 },
                new JsonObject { ["key"] = "osdu:wks:master-data--Wellbore:1.1.0", ["count"] = 3 }),
            ["totalCount"] = 20,
        }.ToJsonString());
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var types = await explorer.TypesAsync(new ExplorerSearch { Text = "WB C", Kind = "*:*:master-data--Wellbore:*", Offset = 300 }, CancellationToken.None);

            Assert.Equal(("text", 20L, 15L), (types.Reading, types.Total, types.Listed));
            Assert.Equal(["osdu:wks:master-data--Wellbore:1.1.0", "osdu:wks:work-product-component--WellLog:1.4.0"], types.Kinds.Select(k => k.Kind));

            // The list is what a kind is picked from, so the kind in view, its order and its page do not narrow it.
            var body = JsonNode.Parse(Assert.Single(handler.Calls).Body!)!;
            Assert.Equal(("*:*:*:*", "kind", "\"WB C\""), (body["kind"]!.GetValue<string>(), body["aggregateBy"]!.GetValue<string>(), body["query"]!.GetValue<string>()));
            Assert.Null(body["offset"]);
            Assert.Null(body["sort"]);
        }

        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task Every_kind_the_partitions_schemas_list_is_a_type_with_no_records_where_the_search_finds_none_while_nothing_narrows_the_list()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject { ["id"] = Wellbore }),
            ["aggregations"] = new JsonArray(
                new JsonObject { ["key"] = "osdu:wks:master-data--Wellbore:1.1.0", ["count"] = 3 },
                new JsonObject { ["key"] = "acme:wells:Completion:1.0.0", ["count"] = 2 }),
            ["totalCount"] = 5,
        }.ToJsonString());
        var schemas = Listed(
            ("osdu:wks:master-data--Wellbore:1.1.0", "PUBLISHED", "SHARED"),
            ("acme:wells:Completion:1.0.0", "PUBLISHED", "INTERNAL"),
            ("acme:wells:StationReading:1.0.0", "DEVELOPMENT", "INTERNAL"));
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var types = await explorer.TypesAsync(new ExplorerSearch(), schemas, CancellationToken.None);

            // A partition's own kind naming no group is a type like any other; one registered with no record has none.
            Assert.Equal(
                [
                    new ExplorerKindCount("acme:wells:Completion:1.0.0", 2, "PUBLISHED", "INTERNAL"),
                    new ExplorerKindCount("acme:wells:StationReading:1.0.0", 0, "DEVELOPMENT", "INTERNAL"),
                    new ExplorerKindCount("osdu:wks:master-data--Wellbore:1.1.0", 3, "PUBLISHED", "SHARED"),
                ],
                types.Kinds);
            Assert.Equal((5L, 5L), (types.Total, types.Listed));
            Assert.Equal(("ready", 3), (types.Schemas!.State, types.Schemas.Kinds));

            // A search lists only where it finds records, and the search groups named every kind, so nothing more was asked.
            var found = await explorer.TypesAsync(new ExplorerSearch { Text = "C-28" }, schemas, CancellationToken.None);
            Assert.Equal(["acme:wells:Completion:1.0.0", "osdu:wks:master-data--Wellbore:1.1.0"], found.Kinds.Select(k => k.Kind));
            Assert.Equal(2, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task The_kinds_the_search_groups_leave_out_are_counted_apart_by_the_kinds_the_partitions_schemas_list()
    {
        // The first answer names the largest groups only (at most 1,000 in the platform); the partition's own kinds are left out.
        var handler = new FakeHttpHandler()
            .OnMatch(r => Body(r).Contains("acme", StringComparison.Ordinal), _ => FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject
            {
                ["results"] = new JsonArray(new JsonObject { ["id"] = Wellbore }),
                ["aggregations"] = new JsonArray(
                    new JsonObject { ["key"] = "acme:wells:Completion:1.0.0", ["count"] = 7 },
                    new JsonObject { ["key"] = "acme:governance:data-governance--AccessGroupMap:1.0.0", ["count"] = 2 }),
                ["totalCount"] = 9,
            }.ToJsonString()))
            .On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, new JsonObject
            {
                ["results"] = new JsonArray(new JsonObject { ["id"] = Wellbore }),
                ["aggregations"] = new JsonArray(new JsonObject { ["key"] = "osdu:wks:master-data--Wellbore:1.1.0", ["count"] = 90 }),
                ["totalCount"] = 100,
            }.ToJsonString());
        var schemas = Listed(
            ("osdu:wks:master-data--Wellbore:1.1.0", "PUBLISHED", "SHARED"),
            ("acme:wells:Completion:1.0.0", "PUBLISHED", "INTERNAL"),
            ("acme:governance:data-governance--AccessGroupMap:1.0.0", "PUBLISHED", "INTERNAL"),
            ("acme:wells:SurveyRun:1.0.0", "PUBLISHED", "INTERNAL"));
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var types = await explorer.TypesAsync(new ExplorerSearch { Text = "Riverbend" }, schemas, CancellationToken.None);

            Assert.Equal(
                [("acme:governance:data-governance--AccessGroupMap:1.0.0", 2L), ("acme:wells:Completion:1.0.0", 7L), ("osdu:wks:master-data--Wellbore:1.1.0", 90L)],
                types.Kinds.Select(k => (k.Kind, k.Count)));
            Assert.Equal((100L, 99L), (types.Total, types.Listed));

            // The kinds left out are asked as the first was, with a condition naming them, the kind it already named not among them.
            var fill = JsonNode.Parse(handler.Calls[1].Body!)!;
            Assert.Equal(("*:*:*:*", "kind"), (fill["kind"]!.GetValue<string>(), fill["aggregateBy"]!.GetValue<string>()));
            Assert.Equal(
                "(\"Riverbend\") AND (kind:(\"acme:governance:data-governance--AccessGroupMap:1.0.0\" OR \"acme:wells:Completion:1.0.0\" OR \"acme:wells:SurveyRun:1.0.0\"))",
                fill["query"]!.GetValue<string>());
            Assert.Equal(2, handler.Calls.Count);
        }

        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search);
    }

    /// <summary>A listing of the partition's schemas holding <paramref name="kinds"/>, each in its status and scope.</summary>
    private static SchemaKindsView Listed(params (string Kind, string Status, string Scope)[] kinds)
        => new(
            new SchemaKindsListing(
                DateTimeOffset.UnixEpoch,
                kinds.ToDictionary(
                    k => k.Kind,
                    k =>
                    {
                        var parts = k.Kind.Split(':');
                        return new SchemaListing(k.Kind, parts[0], parts[1], parts[2], parts[3], k.Status, k.Scope);
                    },
                    StringComparer.Ordinal),
                kinds.Length,
                []),
            Listing: false,
            Problem: null,
            ProblemUtc: null);

    [Fact]
    public async Task Without_a_schema_the_properties_of_a_kind_are_those_its_records_hold_each_typed_by_its_value()
    {
        var handler = new FakeHttpHandler()
            .OnMatch(r => Path(r).StartsWith("/api/schema-service/v1/schema/", StringComparison.Ordinal), _ => FakeHttpHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"not found\"}"))
            .On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, new JsonObject
            {
                ["results"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = Wellbore,
                        ["data"] = new JsonObject
                        {
                            ["FacilityName"] = "WB C/3-C-28 B",
                            ["SpudDate"] = "2019-03-01T00:00:00Z",
                            ["TotalDepth"] = 3120.5,
                            ["GeoContexts"] = new JsonArray(new JsonObject { ["GeoPoliticalEntityID"] = "dev:x--Y:z:" }),
                            ["SourceKeys"] = new JsonArray("a", "b"),
                            ["VerticalMeasurement"] = new JsonObject { ["Depth"] = 12 },
                            ["odd key"] = "left out",
                        },
                    },
                    // A second record adds what the first does not hold; a path both hold is typed by the first.
                    new JsonObject { ["id"] = Wellbore + "-2", ["data"] = new JsonObject { ["IsActive"] = true, ["TotalDepth"] = "deep" } }),
                ["totalCount"] = 2,
            }.ToJsonString());
        var (explorer, runtime, client) = Connect(handler);
        using (runtime)
        {
            var fields = await explorer.FieldsAsync("osdu:wks:master-data--Wellbore:1.1.0", new SchemaServiceReader(client, new TestClock()), CancellationToken.None);

            Assert.Equal((Wellbore, 2, (string?)null), (fields.SampleId, fields.Sampled, fields.SchemaKind));
            Assert.Equal(RecordExplorer.EnvelopeFields, fields.Fields.Take(RecordExplorer.EnvelopeFields.Count));
            Assert.Equal(
                [
                    new ExplorerFieldInfo("data.FacilityName", "text", Origin: "records"),
                    new ExplorerFieldInfo("data.IsActive", "boolean", Origin: "records"),
                    new ExplorerFieldInfo("data.SourceKeys", "text", Origin: "records"),
                    new ExplorerFieldInfo("data.SpudDate", "date", Origin: "records"),
                    new ExplorerFieldInfo("data.TotalDepth", "number", Origin: "records"),
                    new ExplorerFieldInfo("data.VerticalMeasurement.Depth", "number", Origin: "records"),
                ],
                fields.Fields.Skip(RecordExplorer.EnvelopeFields.Count));

            // A value inside a list of objects is left out without a schema: the list may be nested, which a plain path misses.
            Assert.DoesNotContain(fields.Fields, f => f.Path.StartsWith("data.GeoContexts", StringComparison.Ordinal));
            Assert.Contains(fields.Notes!, n => n.Contains("The Schema service holds no schema of osdu:wks:master-data--Wellbore:1.1.0", StringComparison.Ordinal));

            // Whole records, nothing projected away, enough of them to meet what an index augmentation adds.
            var body = JsonNode.Parse(handler.Calls.Single(c => c.Method == HttpMethod.Post).Body!)!;
            Assert.Null(body["returnedFields"]);
            Assert.Null(body["aggregateBy"]);
            Assert.Equal(RecordExplorer.SampleSize, body["limit"]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task The_properties_of_a_type_are_its_schemas_declared_values_and_those_its_records_hold_beyond_them()
    {
        const string Kind = "osdu:wks:master-data--Wellbore:1.1.0";
        var handler = new FakeHttpHandler()
            .OnMatch(r => Path(r) == "/api/schema-service/v1/schema/" + Kind, _ => FakeHttpHandler.Json(HttpStatusCode.OK, WellboreSchema))
            .On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, new JsonObject
            {
                ["results"] = new JsonArray(new JsonObject
                {
                    ["id"] = Wellbore,
                    ["data"] = new JsonObject
                    {
                        ["FacilityName"] = "WB C/3-C-28 B",
                        ["GeoContexts"] = new JsonArray(new JsonObject { ["FieldID"] = "dev:master-data--Field:FIELD-A:", ["GeoTypeID"] = "dev:reference-data--GeoPoliticalEntityType:Field:" }),
                        // What an index augmentation adds: no schema declares it, and the index names it with a dot.
                        ["Augmented.WellboreName"] = "WB C/3-C-28 B",
                        ["Remarks"] = new JsonArray(new JsonObject { ["Remark"] = "left out: the list is not indexed inside" }),
                    },
                }),
                ["aggregations"] = new JsonArray(
                    new JsonObject { ["key"] = "osdu:wks:master-data--Wellbore:1.0.0", ["count"] = 3 },
                    new JsonObject { ["key"] = Kind, ["count"] = 40 }),
                ["totalCount"] = 43,
            }.ToJsonString());
        var (explorer, runtime, client) = Connect(handler);
        using (runtime)
        {
            var fields = await explorer.FieldsAsync("*:*:master-data--Wellbore:*", new SchemaServiceReader(client, new TestClock()), CancellationToken.None);

            // The kind of the type holding the most records is the one whose schema is read.
            Assert.Equal(Kind, fields.SchemaKind);
            var content = fields.Fields.Skip(RecordExplorer.EnvelopeFields.Count).ToList();
            Assert.Equal(
                [
                    "data.FacilityName", "data.GeoContexts.FieldID", "data.GeoContexts.GeoPoliticalEntityID", "data.GeoContexts.GeoTypeID",
                    "data.NameAliases.AliasName", "data.SpudDate", "data.TotalDepth", "data.Augmented.WellboreName",
                ],
                content.Select(f => f.Path));
            Assert.Equal(new ExplorerFieldInfo("data.FacilityName", "text", null, "schema", "Facility Name", "The name of the facility."), content[0]);
            Assert.Equal(new ExplorerFieldInfo("data.GeoContexts.FieldID", "text", "data.GeoContexts", "schema", "Field ID"), content[1]);
            Assert.Equal(("data.NameAliases", "schema"), (content[4].Nested, content[4].Origin));
            Assert.Equal(("date", "number"), (content[5].Index, content[6].Index));
            Assert.Equal(new ExplorerFieldInfo("data.Augmented.WellboreName", "text", Origin: "records"), content[7]);
            Assert.Contains(fields.Notes!, n => n.StartsWith("1 value the schema of " + Kind + " declares is left out", StringComparison.Ordinal));

            var body = JsonNode.Parse(handler.Calls.Single(c => c.Method == HttpMethod.Post).Body!)!;
            Assert.Equal("kind", body["aggregateBy"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Records_of_several_types_are_described_by_no_one_schema_so_none_is_read()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, new JsonObject
        {
            ["results"] = new JsonArray(new JsonObject { ["id"] = Wellbore, ["data"] = new JsonObject { ["FacilityName"] = "A" } }),
            ["aggregations"] = new JsonArray(
                new JsonObject { ["key"] = "osdu:wks:master-data--Wellbore:1.1.0", ["count"] = 3 },
                new JsonObject { ["key"] = "osdu:wks:master-data--Well:1.1.0", ["count"] = 4 }),
            ["totalCount"] = 7,
        }.ToJsonString());
        var (explorer, runtime, client) = Connect(handler);
        using (runtime)
        {
            var fields = await explorer.FieldsAsync("*:*:master-data--*:*", new SchemaServiceReader(client, new TestClock()), CancellationToken.None);

            Assert.Null(fields.SchemaKind);
            Assert.Equal(new ExplorerFieldInfo("data.FacilityName", "text", Origin: "records"), fields.Fields[^1]);
            Assert.Contains(fields.Notes!, n => n.Contains("several types", StringComparison.Ordinal));
            Assert.Single(handler.Calls);
        }
    }

    [Fact]
    public void Each_condition_is_asked_as_the_platform_indexes_the_property_and_an_exclusion_alone_starts_from_every_record()
    {
        ExplorerFilter Filter(ExplorerCondition condition, string path = "data.Source", OsduFieldIndex index = OsduFieldIndex.Text, string? value = null, string? to = null, IReadOnlyList<string>? values = null, string? nested = null)
            => new() { Path = path, Index = index, Condition = condition, Value = value, To = to, Values = values, Nested = nested };

        Assert.Equal("data.Source.keyword:\"WellDB\"", Filter(ExplorerCondition.Is, value: "WellDB").Clause());
        Assert.Equal("NOT (data.Source.keyword:\"WellDB\")", Filter(ExplorerCondition.IsNot, value: "WellDB").Clause());
        Assert.Equal("(data.Source.keyword:(\"WellDB\" OR \"WELLDB\"))", Filter(ExplorerCondition.AnyOf, values: ["WellDB", "WELLDB"]).Clause());
        Assert.Equal("data.Source:\"welldb database\"", Filter(ExplorerCondition.Contains, value: "welldb database").Clause());
        Assert.Equal("data.Source.keyword:WELLDB\\ D*", Filter(ExplorerCondition.StartsWith, value: "WELLDB D").Clause());
        Assert.Equal("data.TotalDepth:[\"1000\" TO \"2000\"}", Filter(ExplorerCondition.Range, "data.TotalDepth", OsduFieldIndex.Number, "1000", "2000").Clause());
        Assert.Equal("createTime:[\"2026-01-01\" TO *]", Filter(ExplorerCondition.Range, "createTime", OsduFieldIndex.Date, "2026-01-01", "").Clause());
        Assert.Equal("_exists_:data.Source", Filter(ExplorerCondition.Exists).Clause());
        Assert.Equal("NOT (_exists_:data.Augmented.FieldId)", Filter(ExplorerCondition.Missing, "data.Augmented.FieldId").Clause());
        Assert.Equal(
            "nested(data.GeoContexts, (FieldID.keyword:\"dev:master-data--Field:FIELD-A:\"))",
            Filter(ExplorerCondition.Is, "data.GeoContexts.FieldID", value: "dev:master-data--Field:FIELD-A:", nested: "data.GeoContexts").Clause());

        // The service refuses a query that only excludes; one that also reads text or includes needs no start.
        var missing = Filter(ExplorerCondition.Missing, "data.Augmented.FieldId");
        Assert.Equal("_exists_:id AND NOT (_exists_:data.Augmented.FieldId)", RecordExplorer.Compose(null, [missing]));
        Assert.Equal("(\"WB D\") AND NOT (_exists_:data.Augmented.FieldId)", RecordExplorer.Compose("\"WB D\"", [missing]));
        Assert.Equal(
            "(data.Source.keyword:(\"WellDB\" OR \"WELLDB\")) AND NOT (_exists_:data.Augmented.FieldId)",
            RecordExplorer.Compose(null, [Filter(ExplorerCondition.AnyOf, values: ["WellDB", "WELLDB"]), missing]));
    }

    [Theory]
    [InlineData("{\"filters\":[{\"path\":\"kind\",\"index\":\"keyword\",\"condition\":\"contains\",\"value\":\"Well\"}]}", "compared whole")]
    [InlineData("{\"filters\":[{\"path\":\"data.GeoContexts.FieldID\",\"nested\":\"data.GeoContexts\",\"condition\":\"exists\"}]}", "does not see inside")]
    [InlineData("{\"filters\":[{\"path\":\"data.GeoContexts.FieldID\",\"nested\":\"data.GeoContexts\",\"condition\":\"startsWith\",\"value\":\"a\"}]}", "rewrites the query")]
    [InlineData("{\"filters\":[{\"path\":\"data.IsActive\",\"index\":\"boolean\",\"condition\":\"range\",\"value\":\"false\"}]}", "true or false")]
    [InlineData("{\"filters\":[{\"path\":\"data.TotalDepth\",\"index\":\"number\",\"condition\":\"range\"}]}", "open at both ends")]
    [InlineData("{\"filters\":[{\"path\":\"data.Source\",\"condition\":\"is\"}]}", "compared with no value")]
    [InlineData("{\"filters\":[{\"path\":\"data.Source\",\"condition\":\"near\",\"value\":\"x\"}]}", "is not one the explorer reads")]
    [InlineData("{\"columns\":[\"data.Name\",\"data..Name\"]}", "is not a property path a page can show")]
    [InlineData("{\"columns\":[\"a\",\"b\",\"c\",\"d\",\"e\",\"f\",\"g\",\"h\",\"i\"]}", "at most 8 properties")]
    public void A_condition_or_column_that_cannot_be_asked_is_refused_with_why(string json, string said)
    {
        var refused = Assert.Throws<DeliveryException>(() => ExplorerSearch.Parse(json));
        Assert.Contains(said, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_condition_compares_at_most_fifty_values()
    {
        var search = new ExplorerSearch
        {
            Filters = [new ExplorerFilter { Path = "data.Source", Condition = ExplorerCondition.AnyOf, Values = Enumerable.Range(0, ExplorerFilter.MaxValues + 1).Select(i => $"v{i}").ToList() }],
        };

        Assert.Contains("at most 50 at once", search.Problem(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_condition_crosses_to_the_node_as_it_was_asked()
    {
        var search = new ExplorerSearch
        {
            Kind = "*:*:work-product-component--WellLog:*",
            Filters =
            [
                new ExplorerFilter { Path = "data.Source", Condition = ExplorerCondition.AnyOf, Values = ["WellDB", "WELLDB"] },
                new ExplorerFilter { Path = "data.GeoContexts.FieldID", Nested = "data.GeoContexts", Value = "dev:master-data--Field:FIELD-A:" },
                new ExplorerFilter { Path = "data.TotalDepth", Index = OsduFieldIndex.Number, Condition = ExplorerCondition.Range, Value = "10", To = "20" },
            ],
            Facet = new ExplorerField { Path = "data.GeoContexts.FieldID", Nested = "data.GeoContexts" },
            Columns = ["data.Augmented.WellboreName"],
        };

        var json = search.ToJson();
        var back = ExplorerSearch.Parse(json);

        Assert.Contains("\"condition\":\"anyOf\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("excludes", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(json, back.ToJson());
        Assert.Equal("nested(data.GeoContexts, FieldID.keyword)", back.Facet!.AggregateBy());
    }

    [Fact]
    public async Task A_page_carries_what_each_record_holds_at_its_columns_through_lists_and_dotted_names()
    {
        var handler = new FakeHttpHandler().On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, new JsonObject
        {
            ["results"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = Wellbore,
                    ["data"] = new JsonObject
                    {
                        ["Augmented.WellboreName"] = "WB D/4-A-30",
                        ["GeoContexts"] = new JsonArray(new JsonObject { ["FieldID"] = "dev:master-data--Field:FIELD-A:" }, new JsonObject { ["GeoTypeID"] = "x" }, new JsonObject { ["FieldID"] = "dev:master-data--Field:FIELD-B:" }),
                        ["TotalDepth"] = 3120.5,
                        ["Remark"] = new string('r', RecordExplorer.MaxColumnValueLength + 10),
                    },
                },
                new JsonObject { ["id"] = Wellbore + "-2", ["data"] = new JsonObject { ["Augmented"] = new JsonObject { ["WellboreName"] = "WB D/4-B-1" } } }),
            ["totalCount"] = 2,
        }.ToJsonString());
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var columns = new[] { "data.Augmented.WellboreName", "data.GeoContexts.FieldID", "data.TotalDepth", "data.Remark" };
            var page = await explorer.SearchAsync(new ExplorerSearch { Kind = "*:*:master-data--Wellbore:*", Columns = columns }, CancellationToken.None);

            var first = page.Hits[0].Values!;
            Assert.Equal(["WB D/4-A-30"], first["data.Augmented.WellboreName"]);
            Assert.Equal(["dev:master-data--Field:FIELD-A:", "dev:master-data--Field:FIELD-B:"], first["data.GeoContexts.FieldID"]);
            Assert.Equal(["3120.5"], first["data.TotalDepth"]);
            Assert.Equal(RecordExplorer.MaxColumnValueLength, first["data.Remark"][0].Length);
            Assert.EndsWith("...", first["data.Remark"][0], StringComparison.Ordinal);

            // A column a record holds nothing at is left out of its values.
            Assert.Equal(["data.Augmented.WellboreName"], page.Hits[1].Values!.Keys);
            Assert.Equal(["WB D/4-B-1"], page.Hits[1].Values!["data.Augmented.WellboreName"]);

            var returned = JsonNode.Parse(Assert.Single(handler.Calls).Body!)!["returnedFields"]!.AsArray().Select(f => f!.GetValue<string>()).ToList();
            Assert.All(columns, c => Assert.Contains(c, returned));
            Assert.Contains("data.FacilityName", returned);
        }

        // A page with no columns carries no values.
        var plain = RecordExplorer.Hit(new JsonObject { ["id"] = Wellbore }, []);
        Assert.Null(plain!.Values);
    }

    /// <summary>A condition on wellbores by name, read through the wellbores the name finds, as a search term's lookup reads it.</summary>
    private static ExplorerFilter ThroughWellbores(ExplorerCondition condition, string value) => new()
    {
        Path = "data.WellboreID",
        Condition = condition,
        Value = value,
        Via = new ExplorerVia
        {
            Term = "Wellbore name",
            Kind = "osdu:wks:master-data--Wellbore:*",
            Match = [new ExplorerField { Path = "data.FacilityName" }],
            Read = new ExplorerField { Path = "id", Index = OsduFieldIndex.Keyword },
        },
    };

    /// <summary>Whether a search request asks for wellbores: its kind names them, whatever its query names.</summary>
    private static bool SearchesWellbores(string body) => JsonNode.Parse(body)?["kind"]?.GetValue<string>() == "osdu:wks:master-data--Wellbore:*";

    [Fact]
    public async Task A_condition_through_other_records_finds_them_first_and_compares_the_ids_they_are_named_by()
    {
        var handler = new FakeHttpHandler()
            .OnMatch(r => SearchesWellbores(Body(r)), _ => FakeHttpHandler.Json(HttpStatusCode.OK, Result(2, "dev:master-data--Wellbore:W1", "dev:master-data--Wellbore:W2")))
            .OnMatch(_ => true, _ => FakeHttpHandler.Json(HttpStatusCode.OK, Result(4, "dev:work-product-component--WellLog:L1")));
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var search = new ExplorerSearch { Kind = "*:*:work-product-component--WellLog:*", Filters = [ThroughWellbores(ExplorerCondition.Contains, "WB D")] };
            var page = await explorer.SearchAsync(search, CancellationToken.None);

            // The wellbores first, by their name's words; then the logs naming one of them, in the reference form a log writes.
            Assert.Equal("(data.WellboreID.keyword:(\"dev:master-data--Wellbore:W1:\" OR \"dev:master-data--Wellbore:W2:\"))", page.Query);
            Assert.Equal(4, page.Total);
            var note = Assert.Single(page.Notes);
            Assert.StartsWith("Wellbore name contains WB D: 2 Wellbore records found by data.FacilityName:\"WB D\"", note, StringComparison.Ordinal);

            var found = JsonNode.Parse(handler.Calls[0].Body!)!;
            Assert.Equal(("osdu:wks:master-data--Wellbore:*", "data.FacilityName:\"WB D\"", ExplorerVia.MaxFound), (found["kind"]!.GetValue<string>(), found["query"]!.GetValue<string>(), found["limit"]!.GetValue<int>()));
            Assert.Equal(["id"], found["returnedFields"]!.AsArray().Select(f => f!.GetValue<string>()));

            // The next page of the same search reads the wellbores no more.
            await explorer.SearchAsync(search with { Offset = 100 }, CancellationToken.None);
            Assert.Equal(1, handler.Calls.Count(c => SearchesWellbores(c.Body!)));
        }

        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search);
    }

    [Fact]
    public async Task A_value_that_finds_no_record_matches_no_record_or_excluding_leaves_every_record()
    {
        var handler = new FakeHttpHandler()
            .OnMatch(r => SearchesWellbores(Body(r)), _ => FakeHttpHandler.Json(HttpStatusCode.OK, Result(0)))
            .OnMatch(_ => true, _ => FakeHttpHandler.Json(HttpStatusCode.OK, Result(0)));
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var none = await explorer.SearchAsync(new ExplorerSearch { Filters = [ThroughWellbores(ExplorerCondition.Is, "WB Z")] }, CancellationToken.None);
            Assert.Equal("_exists_:id AND NOT (_exists_:id)", none.Query);
            Assert.Contains("no record is found by it", Assert.Single(none.Notes), StringComparison.Ordinal);

            var every = await explorer.SearchAsync(new ExplorerSearch { Filters = [ThroughWellbores(ExplorerCondition.IsNot, "WB Z")] }, CancellationToken.None);
            Assert.Null(every.Query);
            Assert.Contains("it leaves every record", Assert.Single(every.Notes), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_condition_on_a_key_column_makes_the_ids_with_every_value_the_platform_holds_of_the_other_columns()
    {
        var handler = new FakeHttpHandler()
            .OnMatch(r => Body(r).Contains("aggregateBy", StringComparison.Ordinal), _ => FakeHttpHandler.Json(HttpStatusCode.OK, new JsonObject
            {
                ["results"] = new JsonArray(),
                ["aggregations"] = new JsonArray(new JsonObject { ["key"] = "PROJECT_A", ["count"] = 3 }, new JsonObject { ["key"] = "PROJECT_B", ["count"] = 1 }),
                ["totalCount"] = 4,
            }.ToJsonString()))
            .OnMatch(_ => true, _ => FakeHttpHandler.Json(HttpStatusCode.OK, Result(1, "dev:work-product-component--WellLog:L1")));
        var (explorer, runtime) = Explorer(handler);
        using (runtime)
        {
            var filter = new ExplorerFilter
            {
                Path = "id",
                Index = OsduFieldIndex.Keyword,
                Value = "LOG-0001/1",
                Via = new ExplorerVia
                {
                    Term = "WellDB log id",
                    Key = new ExplorerViaKey
                    {
                        Systems = ["welldb"],
                        EntityType = "work-product-component--WellLog",
                        Columns = ["source_project", "log_id"],
                        Given = "log_id",
                        Others = [new ExplorerViaColumn { Column = "source_project", Path = "tags.SourceProject", Index = OsduFieldIndex.Keyword }],
                    },
                },
            };

            var page = await explorer.SearchAsync(new ExplorerSearch { Filters = [filter] }, CancellationToken.None);

            // Each id is the delivery's own: the key's values in its order, under the source system, in the partition read.
            var ids = new[] { "PROJECT_A", "PROJECT_B" }.Select(p => TargetId.Compose("dev", "work-product-component--WellLog", DeliveryKey.Derive("welldb", [p, "LOG-0001/1"])));
            Assert.Equal($"(id:({string.Join(" OR ", ids.Select(i => $"\"{i}\""))}))", page.Query);
            Assert.Contains("2 record ids made from source_project and log_id with 2 values of source_project (tags.SourceProject)", Assert.Single(page.Notes), StringComparison.Ordinal);

            var grouped = JsonNode.Parse(handler.Calls[0].Body!)!;
            Assert.Equal(("*:*:work-product-component--WellLog:*", "tags.SourceProject"), (grouped["kind"]!.GetValue<string>(), grouped["aggregateBy"]!.GetValue<string>()));
        }
    }

    [Theory]
    [InlineData(ExplorerCondition.Range, "is is, isNot, anyOf, noneOf, contains or startsWith")]
    [InlineData(ExplorerCondition.Exists, "compares at least one value")]
    public void A_condition_through_other_records_that_cannot_be_read_as_asked_is_refused(ExplorerCondition condition, string said)
    {
        var filter = ThroughWellbores(condition, "WB D") with { Value = condition == ExplorerCondition.Exists ? null : "WB D", To = "WB E" };

        Assert.Contains(said, new ExplorerSearch { Filters = [filter] }.Problem(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_condition_through_other_records_crosses_to_the_node_as_it_was_asked()
    {
        var search = new ExplorerSearch { Filters = [ThroughWellbores(ExplorerCondition.AnyOf, "x") with { Value = null, Values = ["A", "B"] }] };

        var json = search.ToJson();

        Assert.Contains("\"via\":{\"term\":\"Wellbore name\"", json, StringComparison.Ordinal);
        Assert.Equal(json, ExplorerSearch.Parse(json).ToJson());
    }

    [Fact]
    public async Task The_task_reads_a_record_from_the_storage_service_whatever_route_the_flow_delivers_by()
    {
        var root = Samples.NewTempDirectory();
        var flowFile = System.IO.Path.Combine(root, "explorer-route.yaml");
        await File.WriteAllTextAsync(flowFile, """
            flowType: delivery
            name: explorer-route
            partitions:
              - dev
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.ing.Record, key: [record_id] }
              work: work
            render:
              mapping: Targeting@1.0.0
            target:
              endpoint: http://localhost
              protocol: ddms
            """);
        var record = new JsonObject { ["id"] = Wellbore, ["kind"] = "osdu:wks:master-data--Wellbore:1.1.0", ["version"] = 2, ["data"] = new JsonObject { ["FacilityName"] = "WB C/3-C-28 B" } };
        var handler = new FakeHttpHandler()
            .OnMatch(r => Path(r) == "/api/storage/v2/records/versions/" + Wellbore, _ => FakeHttpHandler.Json(HttpStatusCode.OK, "{\"recordId\":\"" + Wellbore + "\",\"versions\":[1,2]}"))
            .OnMatch(r => Path(r) == "/api/storage/v2/records/" + Wellbore, _ => FakeHttpHandler.Json(HttpStatusCode.OK, record.ToJsonString()))
            .On(HttpMethod.Post, "/api/search/v2/query", HttpStatusCode.OK, Result(7, Wellbore));
        var operation = new ExploreOperation(Samples.Engine(ledger: null), handler, allowLoopback: true);

        var read = JsonNode.Parse(await operation.ExecuteAsync(Task(flowFile, ExploreOperation.ReadAction, new() { ["targetId"] = Wellbore + ":" }), CancellationToken.None))!;
        Assert.True(read["found"]!.GetValue<bool>());
        Assert.Equal((Wellbore, "explorer-route"), (read["targetId"]!.GetValue<string>(), read["flow"]!.GetValue<string>()));
        Assert.Equal([2L, 1L], read["versions"]!.AsArray().Select(v => v!.GetValue<long>()));
        Assert.Equal("WB C/3-C-28 B", read["record"]!["data"]!["FacilityName"]!.GetValue<string>());

        var searched = JsonNode.Parse(await operation.ExecuteAsync(
            Task(flowFile, ExploreOperation.SearchAction, new() { [ExploreOperation.SearchArgument] = new ExplorerSearch { Text = "master-data--Wellbore:WB" }.ToJson() }),
            CancellationToken.None))!;
        Assert.Equal(("dev", "explorer-route"), (searched["partition"]!.GetValue<string>(), searched["connection"]!.GetValue<string>()));
        Assert.Equal(7, searched["answer"]!["total"]!.GetValue<long>());
        Assert.Equal("id:dev\\:master\\-data\\-\\-Wellbore\\:WB*", searched["answer"]!["query"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(searched["correlationId"]!.GetValue<string>()));

        // Every request carried the partition the flow is bound to, and each read is the storage or search service's own.
        Assert.All(handler.Calls, call => Assert.Equal("dev", call.Headers["data-partition-id"]));
        OsduContracts.AssertConform(handler.Calls, null, OsduContracts.Search, OsduContracts.Storage);

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => operation.ExecuteAsync(Task(flowFile, "purge", new()), CancellationToken.None));
        Assert.Contains("is not a read the explorer makes", refused.Message, StringComparison.Ordinal);
    }

    private static ComputeTaskPayload Task(string flowFile, string action, Dictionary<string, string> arguments)
    {
        arguments["flowFile"] = flowFile;
        arguments[ExploreOperation.ActionArgument] = action;
        arguments[DeliveryOperation.PartitionArgument] = "dev";
        return new ComputeTaskPayload { Operation = ExploreOperation.OperationName, SourceRef = "explorer-route", Arguments = arguments };
    }

    private static (RecordExplorer Explorer, HttpRuntime Runtime) Explorer(FakeHttpHandler handler)
    {
        var (explorer, runtime, _) = Connect(handler);
        return (explorer, runtime);
    }

    /// <summary>The explorer over <paramref name="handler"/>, with its connection, which the Schema service is read through as well.</summary>
    private static (RecordExplorer Explorer, HttpRuntime Runtime, OsduHttpClient Client) Connect(FakeHttpHandler handler)
    {
        var runtime = new HttpRuntime(
            new FlowReliability { Retry = new FlowRetry { Attempts = 1, BaseDelayMs = 1, MaxDelayMs = 1 } },
            new SecretResolver([new EnvSecretProvider()]), new TestClock(), handler, allowLoopback: true);
        var client = new OsduHttpClient(
            runtime, "http://localhost", new TargetAuth { Type = TargetAuthType.None },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["data-partition-id"] = "dev" });
        return (new RecordExplorer(client, "dev", NullLogger.Instance), runtime, client);
    }

    /// <summary>
    /// A wellbore schema as the Schema service answers it: a text with a title, a date, a number, a nested list, a list of
    /// objects with no indexing hint (which no query reaches inside), and a nested list whose items are a choice of forms.
    /// </summary>
    private const string WellboreSchema = """
        {
          "$schema": "http://json-schema.org/draft-07/schema#",
          "x-osdu-schema-source": "osdu:wks:master-data--Wellbore:1.1.0",
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "kind": { "type": "string" },
            "data": {
              "type": "object",
              "properties": {
                "FacilityName": { "type": "string", "title": "Facility Name", "description": "The name of the facility." },
                "SpudDate": { "type": "string", "format": "date-time" },
                "TotalDepth": { "type": "number" },
                "NameAliases": {
                  "type": "array",
                  "x-osdu-indexing": { "type": "nested" },
                  "items": { "type": "object", "properties": { "AliasName": { "type": "string" } } }
                },
                "Remarks": {
                  "type": "array",
                  "items": { "type": "object", "properties": { "Remark": { "type": "string" } } }
                },
                "GeoContexts": {
                  "type": "array",
                  "x-osdu-indexing": { "type": "nested" },
                  "items": {
                    "oneOf": [
                      { "type": "object", "title": "Field", "properties": { "FieldID": { "type": "string", "title": "Field ID" }, "GeoTypeID": { "type": "string" } } },
                      { "type": "object", "title": "Country", "properties": { "GeoPoliticalEntityID": { "type": "string" }, "GeoTypeID": { "type": "string" } } }
                    ]
                  }
                }
              }
            }
          }
        }
        """;

    private static string Result(long total, params string[] ids)
        => new JsonObject
        {
            ["results"] = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray()),
            ["totalCount"] = total,
        }.ToJsonString();

    /// <summary>A request's body; the handler has read it already, and buffered content reads again from the start.</summary>
    private static string Body(HttpRequestMessage request)
    {
        if (request.Content is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(request.Content.ReadAsStream());
        return reader.ReadToEnd();
    }

    /// <summary>A request's path with its escapes undone, so a rule names an id as it is written.</summary>
    private static string Path(HttpRequestMessage request) => Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
}
