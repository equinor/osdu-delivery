using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The explorer's cache builder (osdu/docs/reference/concepts/explorer-cache-builder.md) and the writing of a whole cache
/// flow file (osdu/docs/reference/guides/cache-flow-file.md), as the API serves them: a draft written as the item a cache
/// flow lists under types and read back by the loader a flow is read by, told where a cache flow of the catalog declares a
/// type of its name otherwise, and previewed through the partition's own connection; the patterns a new cache flow follows;
/// and a whole document checked by the loader, the catalog's flows and types, and the registry's partitions. Nothing here
/// reaches an OSDU: the operations are stood in for (<see cref="RecordedOperations"/>), and nothing is written.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryExplorerCacheApiTests
{
    private const string Wellbores = "osdu:wks:master-data--Wellbore:*";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Estate(string Cs, string Suffix, string Partition, Guid RepoId, string Repository, string CacheFlow, string Type, string DataDb);

    /// <summary>
    /// A repository holding a cache flow, filling one registered partition with a type of wellbores and a lookup table read
    /// through a connection of this test's own, a dimension flow declaring one dimension, and an ingestion flow writing the
    /// estate's data through that connection.
    /// </summary>
    private static async Task<Estate> SeedAsync()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var repository = "cp-cache-builder-" + suffix;
        var estate = new Estate(
            cs, suffix, "c" + suffix, FlowIdentity.FromName("repo/" + repository), repository, "acme-c-" + suffix, "Wellbore" + suffix,
            "${env:ACME_DATA_DB_" + suffix.ToUpperInvariant() + "}");
        var cache = $$"""
            # The wellbores the well log mappings link to.
            flowType: cache
            name: {{estate.CacheFlow}}
            description: The wellbores the well log mappings link to.
            batch: acme
            partitions: [{{estate.Partition}}]

            source:
              endpoint: ${env:OSDU_URL}
              connection: {{estate.DataDb}}
              auth:
                type: oauth2ClientCredentials
                secondarySecretRef: ${env:OSDU_CLIENT_ID}
                secretRef: ${env:OSDU_CLIENT_SECRET}
                token:
                  url: ${env:OSDU_TOKEN_URL}

            # What the cache holds.
            types:
              - kind: "{{Wellbores}}"
                name: {{estate.Type}}
                fields: [data.FacilityName]
              - table: AcmeData.silver.UnitAlias
                name: UnitAlias{{suffix}}
                key: source_unit
                fields: [osdu_unit]

            retentionDays: 30
            """;
        var dimensions = $$"""
            flowType: dimension
            name: acme-d-{{suffix}}
            partitions: [{{estate.Partition}}]
            source:
              endpoint: ${env:OSDU_URL}
            dimensions:
              - name: Operator{{suffix}}
                kind: {{Wellbores}}
                path: data.CurrentOperatorID
            """;
        var ingestion = $$"""
            flowType: ing
            name: acme-ing-{{suffix}}
            connections:
              acmeData: {{estate.DataDb}}
            source: { server: acmeData, object: AcmeData.pre.v_UnitAlias }
            target: { server: acmeData, object: AcmeData.silver.UnitAlias }
            """;

        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo
        {
            Id = estate.RepoId, Name = repository, RemoteUrl = "https://example/" + repository + ".git",
            RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
        });
        foreach (var (name, kind, yaml, source, target) in new[]
        {
            (estate.CacheFlow, "cache", cache, (string?)estate.DataDb, (string?)null),
            ("acme-d-" + suffix, "dimension", dimensions, null, "file"),
            ("acme-ing-" + suffix, "ing", ingestion, estate.DataDb, estate.DataDb),
        })
        {
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = CatalogIdentity.Pipeline(estate.RepoId, name),
                RepoId = estate.RepoId,
                Name = name,
                Kind = kind,
                Batch = "acme",
                RelativePath = "flows/" + name + ".yaml",
                SourceServer = source,
                TargetServer = target,
                ContentHash = new string('0', 64),
                Yaml = yaml,
                DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"{{kind}}"}"""),
                Active = true,
                Wave = 0,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
        }

        await db.SaveChangesAsync();
        await TestPartitions.RegisterAsync(cs, estate.Partition);
        return estate;
    }

    private static async Task CleanAsync(Estate estate)
    {
        await using var db = CatalogDatabase.Create(estate.Cs);
        await db.ComputeTasks.Where(t => t.SourceRef == estate.Partition).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == estate.RepoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == estate.RepoId).ExecuteDeleteAsync();
        await TestPartitions.ForgetAsync(estate.Cs, estate.Partition);
    }

    private static ControlPlaneAppFactory Host(Estate estate, RecordedOperations? operations = null) => new ControlPlaneAppFactory()
        .WithCatalog(estate.Cs)
        .WithModules(new DeliveryControlPlaneModule())
        .WithSetting("ControlPlane:Worker:Enabled", "false")
        .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
        .WithServices(services => services.AddSingleton((operations ?? new RecordedOperations()).Registry()));

    private static object Draft(string? name, string kind = Wellbores, object[]? fields = null, object[]? attributes = null) => new
    {
        name,
        kind,
        query = (string?)null,
        fields = fields ?? [new { path = "data.FacilityName", @as = (string?)null }],
        attributes = attributes ?? [new { name = "WellName", steps = new[] { "data.WellID", "data.FacilityName" } }],
        fetchRecords = false,
        onChange = (string?)null,
    };

    [Fact]
    public async Task A_draft_is_written_as_a_cache_flows_item_and_read_back_by_the_loader()
    {
        var estate = await SeedAsync();
        try
        {
            await using var factory = Host(estate);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var composed = await PostAsync(client, token, "/api/v1/delivery/explorer/cache/compose", new { draft = Draft("Fresh" + estate.Suffix) }, estate.Partition);

            Assert.True(composed.GetProperty("valid").GetBoolean(), composed.ToString());
            Assert.Empty(composed.GetProperty("issues").EnumerateArray());
            Assert.Empty(composed.GetProperty("declaredBy").EnumerateArray());
            Assert.Equal(
                $"  - kind: '{Wellbores}'\n    name: Fresh{estate.Suffix}\n    fields:\n      - data.FacilityName\n    attributes:\n      WellName: [data.WellID, data.FacilityName]\n",
                composed.GetProperty("yaml").GetString());
            Assert.Equal(6, composed.GetProperty("lines").GetArrayLength());

            var type = composed.GetProperty("type");
            Assert.Equal(
                ("Fresh" + estate.Suffix, "master-data--Wellbore", Wellbores, "auto", false),
                (type.GetProperty("name").GetString(), type.GetProperty("entityType").GetString(), type.GetProperty("kind").GetString(),
                    type.GetProperty("onChange").GetString(), type.GetProperty("fetchRecords").GetBoolean()));
            Assert.Equal(JsonValueKind.Null, type.GetProperty("query").ValueKind);
            Assert.Equal(
                [("FacilityName", "data.FacilityName", false), ("WellName", "{ path: [data.WellID, data.FacilityName] }", true)],
                type.GetProperty("fields").EnumerateArray().Select(f => (f.GetProperty("name").GetString(), f.GetProperty("path").GetString(), f.GetProperty("attribute").GetBoolean())));

            // A draft with nothing picked says what is missing, and loads nothing.
            var empty = await PostAsync(
                client, token, "/api/v1/delivery/explorer/cache/compose", new { draft = Draft(null, kind: "", fields: [], attributes: []) }, estate.Partition);
            Assert.False(empty.GetProperty("valid").GetBoolean());
            Assert.Equal(JsonValueKind.Null, empty.GetProperty("type").ValueKind);
            Assert.Equal(["kind", "fields"], empty.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("target").GetString()));

            // What the loader refuses is an error at the part it is about.
            var refused = await PostAsync(client, token, "/api/v1/delivery/explorer/cache/compose", new { draft = Draft(null, kind: "wellbores") }, estate.Partition);
            var issue = Assert.Single(refused.GetProperty("issues").EnumerateArray());
            Assert.Equal(("error", "kind"), (issue.GetProperty("severity").GetString(), issue.GetProperty("target").GetString()));
            Assert.False(refused.GetProperty("valid").GetBoolean());
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    [Fact]
    public async Task A_type_a_cache_flow_declares_already_is_named_and_a_disagreement_with_it_warned_of()
    {
        var estate = await SeedAsync();
        try
        {
            await using var factory = Host(estate);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The same type, read alike: the flow declaring it is named, and nothing is wrong.
            var same = await PostAsync(
                client, token, "/api/v1/delivery/explorer/cache/compose", new { draft = Draft(estate.Type, attributes: []) }, estate.Partition);
            Assert.Equal([estate.CacheFlow], same.GetProperty("declaredBy").EnumerateArray().Select(f => f.GetString()));
            Assert.Empty(same.GetProperty("issues").EnumerateArray());
            Assert.True(same.GetProperty("valid").GetBoolean());

            // A value of the flow's own name read from another path: a warning at the fields, which does not stop the YAML.
            var other = await PostAsync(
                client, token, "/api/v1/delivery/explorer/cache/compose",
                new { draft = Draft(estate.Type, fields: [new { path = "data.WellboreName", @as = "FacilityName" }], attributes: []) }, estate.Partition);
            var path = Assert.Single(other.GetProperty("issues").EnumerateArray());
            Assert.Equal(("warning", "fields"), (path.GetProperty("severity").GetString(), path.GetProperty("target").GetString()));
            Assert.Contains($"{estate.Type}.FacilityName from data.FacilityName", path.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.True(other.GetProperty("valid").GetBoolean());

            // The name on records of another type: a warning at the name.
            var wells = await PostAsync(
                client, token, "/api/v1/delivery/explorer/cache/compose",
                new { draft = Draft(estate.Type, kind: "osdu:wks:master-data--Well:*", attributes: []) }, estate.Partition);
            var name = Assert.Single(wells.GetProperty("issues").EnumerateArray());
            Assert.Equal(("warning", "name"), (name.GetProperty("severity").GetString(), name.GetProperty("target").GetString()));
            Assert.Contains("give this type another name", name.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    [Fact]
    public async Task The_records_are_previewed_through_the_connection_from_the_item_exactly_as_written()
    {
        var estate = await SeedAsync();
        const string Preview = """
            {"connection":"c","partition":"p","correlationId":"x","answeredUtc":"2026-11-14T00:00:00Z","answer":{
              "name":"Wellbore","entityType":"master-data--Wellbore","total":4210,"notes":[],
              "columns":[{"name":"FacilityName","path":"data.FacilityName","attribute":false,"held":1}],
              "records":[{"id":"dev:master-data--Wellbore:W1","fields":{"FacilityName":"WB B/2-9 S"}}]}}
            """;
        var operations = new RecordedOperations { Answer = _ => Preview };
        try
        {
            await using var factory = Host(estate, operations);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var draft = Draft("Fresh" + estate.Suffix);
            var yaml = (await PostAsync(client, token, "/api/v1/delivery/explorer/cache/compose", new { draft }, estate.Partition)).GetProperty("yaml").GetString()!;

            var answer = (await PostAsync(client, token, "/api/v1/delivery/explorer/cache/preview", new { draft }, estate.Partition)).GetProperty("answer");
            Assert.Equal((4210, "WB B/2-9 S"), (answer.GetProperty("total").GetInt32(), answer.GetProperty("records")[0].GetProperty("fields").GetProperty("FacilityName").GetString()));

            // The operation was given the item the compose wrote, through the partition's own connection.
            var ran = operations.Last();
            Assert.Equal((ExploreOperation.CachePreviewAction, estate.Partition), (ran.Argument(ExploreOperation.ActionArgument), ran.SourceRef));
            Assert.Equal(ExploreOperation.PartitionConnectionValue, ran.Argument(ExploreOperation.ConnectionArgument));
            Assert.Equal(yaml, LongArgument.Read(ran, ExploreOperation.ItemArgument));

            // A draft a refresh would refuse is not read: the first thing to put right is said.
            using var refused = await SendAsync(
                client, token, HttpMethod.Post, "/api/v1/delivery/explorer/cache/preview", new { draft = Draft(null, kind: "wellbores") }, estate.Partition);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("The records are previewed once a refresh would cache them", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    [Fact]
    public async Task The_patterns_are_the_catalogs_cache_flows_as_written_but_their_types()
    {
        var estate = await SeedAsync();
        try
        {
            await using var factory = Host(estate);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var patterns = await GetAsync(client, token, "/api/v1/delivery/cache/flow-patterns");

            var flow = patterns.GetProperty("flows").EnumerateArray().Single(f => f.GetProperty("name").GetString() == estate.CacheFlow);
            Assert.Equal(
                (estate.Repository, $"flows/{estate.CacheFlow}.yaml", "acme", estate.DataDb),
                (flow.GetProperty("repository").GetString(), flow.GetProperty("relativePath").GetString(), flow.GetProperty("batch").GetString(),
                    flow.GetProperty("connection").GetString()));
            Assert.Equal([estate.Partition], flow.GetProperty("partitions").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal(JsonValueKind.Null, flow.GetProperty("problem").ValueKind);
            Assert.Equal(
                [(estate.Type, "osdu", Wellbores), ("UnitAlias" + estate.Suffix, "table", "AcmeData.silver.UnitAlias")],
                flow.GetProperty("types").EnumerateArray().Select(t => (t.GetProperty("name").GetString()!, t.GetProperty("origin").GetString()!, t.GetProperty("from").GetString()!)));

            // The document as written, comments and all, but the types, which a new flow writes its own of.
            var envelope = flow.GetProperty("envelope").GetString()!;
            Assert.StartsWith("# The wellbores the well log mappings link to.\nflowType: cache\n", envelope, StringComparison.Ordinal);
            Assert.Contains("    secretRef: ${env:OSDU_CLIENT_SECRET}\n", envelope, StringComparison.Ordinal);
            Assert.EndsWith("retentionDays: 30\n", envelope, StringComparison.Ordinal);
            Assert.DoesNotContain("types:", envelope, StringComparison.Ordinal);
            Assert.DoesNotContain(estate.Type, envelope, StringComparison.Ordinal);
            Assert.False(flow.GetProperty("envelopeCut").GetBoolean());

            // The estate's data is kept through the connection the ingestion flow writes and the cache flow's tables read.
            var data = patterns.GetProperty("connections").EnumerateArray().Single(c => c.GetProperty("reference").GetString() == estate.DataDb);
            Assert.Equal(1, data.GetProperty("writtenBy").GetProperty("ing").GetInt32());
            Assert.Equal(1, data.GetProperty("readBy").GetProperty("cache").GetInt32());
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    /// <summary>A new cache flow of the estate's own shape: its source and partition, the types given, and the connection its table types read, when it has any.</summary>
    private static string NewFlow(Estate estate, string name, string types, string? partition = null, string? connection = null)
    {
        var reads = connection is null ? string.Empty : "\n  connection: " + connection;
        return $$"""
            flowType: cache
            name: {{name}}
            batch: acme
            partitions: [{{partition ?? estate.Partition}}]

            source:
              endpoint: ${env:OSDU_URL}{{reads}}
              auth:
                type: oauth2ClientCredentials
                secondarySecretRef: ${env:OSDU_CLIENT_ID}
                secretRef: ${env:OSDU_CLIENT_SECRET}
                token:
                  url: ${env:OSDU_TOKEN_URL}

            types:
            {{types}}
            """;
    }

    [Fact]
    public async Task A_whole_cache_flow_is_checked_as_a_sync_and_a_refresh_would_take_it()
    {
        var estate = await SeedAsync();
        try
        {
            await using var factory = Host(estate);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            const string Route = "/api/v1/delivery/cache/flow-check";

            // A new flow with a type of its own and the estate's dimension: valid, its types named.
            var types = $"  - kind: \"osdu:wks:master-data--Well:*\"\n    name: Well{estate.Suffix}\n    fields: [data.FacilityName]\n"
                + $"  - dimension: Operator{estate.Suffix}\n    dimensionFlow: acme-d-{estate.Suffix}\n";
            var good = await PostAsync(client, token, Route, new { yaml = NewFlow(estate, "acme-new-" + estate.Suffix, types), path = "flows/acme-new.yaml" }, null);
            Assert.True(good.GetProperty("loads").GetBoolean() && good.GetProperty("valid").GetBoolean(), good.ToString());
            Assert.Empty(good.GetProperty("issues").EnumerateArray());
            Assert.Equal("acme-new-" + estate.Suffix, good.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, good.GetProperty("existing").ValueKind);
            Assert.Equal(
                [("Well" + estate.Suffix, "osdu"), ("Operator" + estate.Suffix, "dimension")],
                good.GetProperty("types").EnumerateArray().Select(t => (t.GetProperty("name").GetString()!, t.GetProperty("origin").GetString()!)));

            // The catalog's own flow under its name: replaced, a warning. Its type on records of another kind, in the partition
            // both name: an error, since a refresh there is refused.
            var clash = $"  - kind: \"osdu:wks:master-data--Well:*\"\n    name: {estate.Type}\n    fields: [data.FacilityName]\n";
            var replaced = await PostAsync(client, token, Route, new { yaml = NewFlow(estate, estate.CacheFlow, clash) }, null);
            var replace = Assert.Single(replaced.GetProperty("issues").EnumerateArray());
            Assert.Equal(("warning", "name"), (replace.GetProperty("severity").GetString(), replace.GetProperty("target").GetString()));
            Assert.Equal($"flows/{estate.CacheFlow}.yaml", replaced.GetProperty("existing").GetProperty("relativePath").GetString());
            Assert.True(replaced.GetProperty("valid").GetBoolean());

            var second = await PostAsync(client, token, Route, new { yaml = NewFlow(estate, "acme-second-" + estate.Suffix, clash) }, null);
            var disagrees = Assert.Single(second.GetProperty("issues").EnumerateArray());
            Assert.Equal(("error", estate.Type, "name"), (disagrees.GetProperty("severity").GetString(), disagrees.GetProperty("type").GetString(), disagrees.GetProperty("target").GetString()));
            Assert.False(second.GetProperty("valid").GetBoolean());

            // The same in a partition the other flow does not fill: a warning, and that partition is not registered.
            var elsewhere = await PostAsync(client, token, Route, new { yaml = NewFlow(estate, "acme-third-" + estate.Suffix, clash, partition: "z" + estate.Suffix) }, null);
            Assert.Equal(
                [("warning", "partitions"), ("warning", "name")],
                elsewhere.GetProperty("issues").EnumerateArray().Select(i => (i.GetProperty("severity").GetString()!, i.GetProperty("target").GetString()!)));
            Assert.True(elsewhere.GetProperty("valid").GetBoolean());

            // A name a flow of another kind has: an error. A dimension no flow declares, and a table read through a
            // connection no flow names: warnings.
            var loose = $"  - kind: \"osdu:wks:master-data--Well:*\"\n    name: Loose{estate.Suffix}\n    fields: [data.FacilityName]\n"
                + $"  - dimension: Missing{estate.Suffix}\n    dimensionFlow: acme-d-{estate.Suffix}\n"
                + "  - table: AcmeData.silver.CurveAlias\n    key: mnemonic\n    fields: [curve_type]\n";
            var taken = await PostAsync(
                client, token, Route, new { yaml = NewFlow(estate, "acme-ing-" + estate.Suffix, loose, connection: "${env:ACME_OTHER_DB_" + estate.Suffix.ToUpperInvariant() + "}") }, null);
            Assert.True(taken.GetProperty("loads").GetBoolean(), taken.ToString());
            Assert.Equal(
                [("error", "name"), ("warning", "dimension"), ("warning", "source")],
                taken.GetProperty("issues").EnumerateArray().Select(i => (i.GetProperty("severity").GetString()!, i.GetProperty("target").GetString()!)));
            Assert.Contains($"does not declare; it declares Operator{estate.Suffix}", taken.GetProperty("issues")[1].GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains(estate.DataDb, taken.GetProperty("issues")[2].GetProperty("message").GetString(), StringComparison.Ordinal);

            // What the loader refuses, in its own words without the file's name.
            var broken = await PostAsync(client, token, Route, new { yaml = NewFlow(estate, "acme-broken", "  - kind: wellbores\n    fields: [data.Name]\n"), path = "flows/acme-broken.yaml" }, null);
            Assert.False(broken.GetProperty("loads").GetBoolean() || broken.GetProperty("valid").GetBoolean());
            Assert.StartsWith("types[0].kind 'wellbores' is not", Assert.Single(broken.GetProperty("issues").EnumerateArray()).GetProperty("message").GetString(), StringComparison.Ordinal);

            using var nothing = await SendAsync(client, token, HttpMethod.Post, Route, new { yaml = " " }, null);
            Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string token, string route, object body, string? workbench)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, route, body, workbench);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{route} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string token, string route)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, route, null, null);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{route} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body, string? workbench)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (workbench is not null)
        {
            request.Headers.Add("X-Osdu-Partition", workbench);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Web);
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
