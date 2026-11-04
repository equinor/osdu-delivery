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
/// A cache type that reads its records whole from storage (osdu/docs/reference/flow/cache-records.md): the search index holds
/// only what a kind's schema describes, so a path the schema leaves open, such as a property of data.ExtensionProperties, is
/// kept only when each record the search finds is read back from the storage service. Against a scripted platform, so nothing
/// here reaches OSDU or a database.
/// </summary>
public class CacheWholeRecordTests
{
    private const string Scope = "dev";

    private const string SearchPath = "/query_with_cursor";

    private const string StoragePath = "/api/storage/v2/query/records";

    private static readonly ReferenceTypeSpec Wellbores = new()
    {
        Name = "Wellbore",
        EntityType = "master-data--Wellbore",
        Kind = "osdu:wks:master-data--Wellbore:*",
        FetchRecords = true,
        Fields =
        [
            new ReferenceFieldSpec("data.FacilityName"),
            new ReferenceFieldSpec("data.ExtensionProperties.WellDb.Region", "Region"),
            new ReferenceFieldSpec("data.ExtensionProperties.WellDb", "WellDb"),
            new ReferenceFieldSpec("namespace", "Namespace"),
        ],
    };

    private static string Id(int i) => $"dev:master-data--Wellbore:WB-{i:D4}";

    /// <summary>One search page of the wellbores numbered <paramref name="from"/> up, as the index returns them when asked for the id and the namespace.</summary>
    private static string Hits(int from, int count, string cursor = "c1")
        => new JsonObject
        {
            ["cursor"] = cursor,
            ["totalCount"] = count,
            ["results"] = new JsonArray(Enumerable.Range(from, count).Select(i => (JsonNode?)new JsonObject { ["id"] = Id(i), ["namespace"] = "dev:wks" }).ToArray()),
        }.ToJsonString();

    /// <summary>A wellbore as storage holds it: its data, and the extension properties the index does not hold.</summary>
    private static JsonObject Stored(string id) => new()
    {
        ["id"] = id,
        ["kind"] = "osdu:wks:master-data--Wellbore:1.0.0",
        ["version"] = 1730000000000001,
        ["acl"] = new JsonObject { ["viewers"] = new JsonArray("data.default.viewers@dev.example.com"), ["owners"] = new JsonArray("data.default.owners@dev.example.com") },
        ["legal"] = new JsonObject { ["legaltags"] = new JsonArray("dev-legal"), ["otherRelevantDataCountries"] = new JsonArray("NO") },
        ["data"] = new JsonObject
        {
            ["FacilityName"] = "Facility " + id[^4..],
            ["ExtensionProperties"] = new JsonObject { ["WellDb"] = new JsonObject { ["Region"] = "North", ["Codes"] = new JsonArray("A", "B") } },
        },
    };

    /// <summary>Storage's answer to a read of the ids <paramref name="body"/> names: every one, except <paramref name="gone"/>, and <paramref name="retry"/> asked to be retried.</summary>
    private static HttpResponseMessage Storage(string? body, ISet<string>? gone = null, ISet<string>? retry = null)
    {
        var asked = JsonNode.Parse(body!)!["records"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var answer = new JsonObject
        {
            ["records"] = new JsonArray(asked.Where(id => gone?.Contains(id) != true && retry?.Contains(id) != true).Select(id => (JsonNode?)Stored(id)).ToArray()),
            ["invalidRecords"] = new JsonArray(asked.Where(id => gone?.Contains(id) == true).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["retryRecords"] = new JsonArray(asked.Where(id => retry?.Contains(id) == true).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        };
        return FakeHttpHandler.Json(HttpStatusCode.OK, answer.ToJsonString());
    }

    private static async Task<(SnapshotBuilder Builder, OsduConnection Osdu)> BuildAsync(FakeHttpHandler handler)
    {
        // Capturing a type reads OSDU only; the store is not written until a version is.
        var store = new FixedCacheStore(Scope, "whole-records", ReferenceSnapshot.Empty);
        var builder = new SnapshotBuilder(store, Scope, "whole-records", new TestClock(), Samples.Logger<SnapshotBuilder>());
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

    /// <summary>The fields a search was asked to return.</summary>
    private static List<string> Returned(FakeHttpHandler.Request search)
        => JsonNode.Parse(search.Body!)!["returnedFields"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

    [Fact]
    public async Task A_type_read_whole_keeps_what_only_storage_holds_and_what_only_the_index_adds()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, SearchPath, hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0 ? Hits(1, 2) : """{"cursor":"c1","results":[]}"""))
            .On(HttpMethod.Post, StoragePath, (_, body) => Storage(body));
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, Wellbores);

            // The search only names the records: their ids, and the one path under a property the indexer adds.
            var search = handler.Calls.First(c => c.Uri.AbsolutePath.EndsWith(SearchPath, StringComparison.Ordinal));
            Assert.Equal(["id", "namespace"], Returned(search));

            Assert.Equal([Id(1), Id(2)], captured.Items.Select(i => i.Id));
            var first = captured.Items[0];
            Assert.Equal("Facility 0001", first.Fields["FacilityName"].Text);
            Assert.Equal("North", first.Fields["Region"].Text);

            // A path ending at an object keeps the object as storage holds it.
            var welldb = Assert.IsType<JsonObject>(first.Fields["WellDb"].Node);
            Assert.Equal("""{"Region":"North","Codes":["A","B"]}""", welldb.ToJsonString());

            // Storage's record has no namespace; the hit's is kept.
            Assert.Equal("dev:wks", first.Fields["Namespace"].Text);
        }
    }

    [Fact]
    public async Task A_page_of_records_is_read_from_storage_a_hundred_ids_a_request()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, SearchPath, hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0 ? Hits(1, 250) : """{"cursor":"c1","results":[]}"""))
            .On(HttpMethod.Post, StoragePath, (_, body) => Storage(body));
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, Wellbores);

            Assert.Equal(250, captured.Items.Count);
            var reads = handler.Calls.Where(c => c.Uri.AbsolutePath.EndsWith(StoragePath, StringComparison.Ordinal)).ToList();
            var asked = reads.Select(r => JsonNode.Parse(r.Body!)!["records"]!.AsArray().Count).Order().ToList();
            Assert.Equal([50, 100, 100], asked);

            // Every id the search found is read exactly once, and every one of them is cached with its extension property.
            Assert.Equal(
                Enumerable.Range(1, 250).Select(Id).Order(StringComparer.Ordinal),
                reads.SelectMany(r => JsonNode.Parse(r.Body!)!["records"]!.AsArray().Select(n => n!.GetValue<string>())).Order(StringComparer.Ordinal));
            Assert.All(captured.Items, item => Assert.Equal("North", item.Fields["Region"].Text));
        }
    }

    [Fact]
    public async Task A_record_the_index_still_lists_and_storage_no_longer_holds_is_not_cached()
    {
        var gone = new HashSet<string>(StringComparer.Ordinal) { Id(2) };
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, SearchPath, hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0 ? Hits(1, 3) : """{"cursor":"c1","results":[]}"""))
            .On(HttpMethod.Post, StoragePath, (_, body) => Storage(body, gone: gone));
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, Wellbores);

            Assert.Equal([Id(1), Id(3)], captured.Items.Select(i => i.Id));
        }
    }

    [Fact]
    public async Task A_record_storage_leaves_unanswered_fails_the_type_rather_than_caching_it_short()
    {
        // Storage asks for the id to be retried, and again when it is: whether it holds the record is unknown, so the type
        // cannot be read whole and nothing of it is kept.
        var retry = new HashSet<string>(StringComparer.Ordinal) { Id(2) };
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, SearchPath, hit => FakeHttpHandler.Json(HttpStatusCode.OK, hit == 0 ? Hits(1, 3) : """{"cursor":"c1","results":[]}"""))
            .On(HttpMethod.Post, StoragePath, (_, body) => Storage(body, retry: retry));
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var error = await Assert.ThrowsAsync<DeliveryException>(() => builder.CaptureTypeAsync(osdu, Wellbores));

            Assert.Contains("storage did not say whether it holds 1 record(s) the search of kind osdu:wks:master-data--Wellbore:* found", error.Message, StringComparison.Ordinal);
            Assert.Contains(Id(2), error.Message, StringComparison.Ordinal);
            Assert.Equal(2, handler.Calls.Count(c => c.Uri.AbsolutePath.EndsWith(StoragePath, StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task A_type_read_from_the_index_asks_the_search_for_its_paths_and_reads_nothing_from_storage()
    {
        var handler = new FakeHttpHandler()
            .On(HttpMethod.Post, SearchPath, hit => FakeHttpHandler.Json(
                HttpStatusCode.OK,
                hit == 0 ? """{"cursor":"c1","totalCount":1,"results":[{"id":"dev:master-data--Wellbore:WB-0001","data":{"FacilityName":"Facility 0001"}}]}""" : """{"cursor":"c1","results":[]}"""));
        var (builder, osdu) = await BuildAsync(handler);
        using (osdu)
        {
            var captured = await builder.CaptureTypeAsync(osdu, Wellbores with { FetchRecords = false });

            var search = handler.Calls.First(c => c.Uri.AbsolutePath.EndsWith(SearchPath, StringComparison.Ordinal));
            Assert.Equal(["id", "data.FacilityName", "data.ExtensionProperties.WellDb.Region", "data.ExtensionProperties.WellDb", "namespace"], Returned(search));
            Assert.DoesNotContain(handler.Calls, c => c.Uri.AbsolutePath.EndsWith(StoragePath, StringComparison.Ordinal));

            // What the index does not hold is simply not there.
            var item = Assert.Single(captured.Items);
            Assert.Equal(["FacilityName"], item.Fields.Keys);
        }
    }

    /// <summary>A cache flow of the given types (YAML list items indented by two spaces), over <paramref name="source"/>.</summary>
    private static CacheDefinition Parse(string types, string source = "  endpoint: https://osdu.example.com\n  headers: { data-partition-id: dev }")
        => new DeliveryDocumentLoader().ParseCache(
            "flowType: cache\nname: whole-records-cache\nsource:\n" + source + "\ntypes:\n" + types + "\n",
            "cache/whole-records-cache.yaml");

    [Fact]
    public void A_type_searched_by_its_kind_reads_its_records_whole_only_when_it_says_so()
    {
        var cache = Parse("""
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                fetchRecords: true
                fields: [data.FacilityName, data.ExtensionProperties.WellDb.Region]
              - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
                fields: [data.Code]
            """);

        Assert.Equal([("Wellbore", true), ("UnitOfMeasure", false)], cache.Types.Select(t => (t.Name, t.FetchRecords)));
    }

    [Fact]
    public void A_lookup_table_takes_no_fetchRecords()
    {
        var table = Assert.Throws<FlowValidationException>(() => Parse(
            """
              - table: OsduData.silver.UnitAlias
                key: source_unit
                fields: [osdu_unit]
                fetchRecords: true
            """,
            "  connection: ${env:OSDU_DATA_DB}"));
        Assert.Contains("types[0] holds table OsduData.silver.UnitAlias, which takes no 'fetchRecords'", table.Message, StringComparison.Ordinal);

        var dictionary = Assert.Throws<FlowValidationException>(() => Parse(
            """
              - dictionary: sampling-domain
                fetchRecords: false
            """,
            "  {}"));
        Assert.Contains("types[0] holds dictionary sampling-domain, which takes no 'fetchRecords'", dictionary.Message, StringComparison.Ordinal);

        var dimension = Assert.Throws<FlowValidationException>(() => Parse(
            """
              - dimension: Field
                dimensionFlow: welldb-welllog-05-dimensions
                fetchRecords: true
            """,
            "  {}"));
        Assert.Contains("types[0] holds dimension Field, which takes no 'fetchRecords'", dimension.Message, StringComparison.Ordinal);

        // The spec refuses it too, whoever builds one.
        var spec = new ReferenceTypeSpec
        {
            Name = "UnitAlias", EntityType = ReferenceType.LookupEntityType("UnitAlias"), Origin = CacheOrigin.Table, Table = "OsduData.silver.UnitAlias", Key = "source_unit",
            Fields = [new ReferenceFieldSpec("osdu_unit")], FetchRecords = true,
        };
        Assert.Contains("which takes no 'fetchRecords'", Assert.Throws<FlowValidationException>(spec.Validate).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_any_flow_of_the_partition_reads_whole_is_read_whole_by_every_capture_of_it()
    {
        var facility = new ReferenceFieldSpec("data.FacilityName");
        var region = new ReferenceFieldSpec("data.ExtensionProperties.WellDb.Region", "Region");
        var declaration = new CacheDeclaration(Scope,
        [
            new CacheTypeDeclaration("project-a", "Wellbore", "master-data--Wellbore", "osdu:wks:master-data--Wellbore:*", "*", [facility], CacheChangeMode.Auto),
            new CacheTypeDeclaration("project-b", "Wellbore", "master-data--Wellbore", "osdu:wks:master-data--Wellbore:*", "*", [facility, region], CacheChangeMode.Auto, FetchRecords: true),
            new CacheTypeDeclaration("project-a", "UnitOfMeasure", "reference-data--UnitOfMeasure", "osdu:wks:reference-data--UnitOfMeasure:*", "*", [new("data.Code")], CacheChangeMode.Auto),
        ]);

        // Project A reads only the index itself; widened to the partition, its refresh reads the wellbores whole, or it would
        // drop the region project B keeps from every wellbore it captured.
        var widened = declaration.Widen(Wellbores with { FetchRecords = false, Fields = [facility] });
        Assert.True(widened.FetchRecords);
        Assert.Equal(["FacilityName", "Region"], widened.Fields.Select(f => f.Name));
        Assert.True(declaration.FetchesRecords("wellbore"));
        Assert.True(declaration.Step("Wellbore")!.FetchRecords);

        // A type no flow reads whole stays read from the index.
        Assert.False(declaration.FetchesRecords("UnitOfMeasure"));
        Assert.False(declaration.Step("UnitOfMeasure")!.FetchRecords);
        Assert.False(declaration.Widen(new ReferenceTypeSpec
        {
            Name = "UnitOfMeasure", EntityType = "reference-data--UnitOfMeasure", Kind = "osdu:wks:reference-data--UnitOfMeasure:*", Fields = [new("data.Code")],
        }).FetchRecords);
    }
}
