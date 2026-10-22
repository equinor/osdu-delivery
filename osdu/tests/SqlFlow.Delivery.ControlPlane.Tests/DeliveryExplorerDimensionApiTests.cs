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
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Engine.Dimensions;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The explorer's dimension builder as the API serves it (osdu/docs/reference/concepts/explorer.md, Building a
/// dimension): a draft written as the item a dimension flow lists and read back by the loader a flow is read by;
/// checked against the templates the synced repositories hold as a flow's own dimension is against its folders, a missing
/// template said with where to put one; every mistake pointed at the part it is about; a name whose table another flow's
/// dimension writes warned of; the keys a kind's template suggests; and an example key made into its row, and the
/// commonest keys of a path, through the partition's own connection, as the explorer reads it, given exactly what the
/// operation reads back. Nothing here reaches an OSDU: the operations are stood in for (<see cref="RecordedOperations"/>),
/// and what each was given is read back.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryExplorerDimensionApiTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string Country = "data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The well database estate's Wellbore dimension, as the builder sends it, under a name of this test's own.</summary>
    private static object Draft(string name, string kind = WellLog, string? path = "data.WellboreID", object[]? attributes = null, object[]? clean = null) => new
    {
        name,
        description = (string?)null,
        kind,
        query = (string?)null,
        path,
        label = new[] { "data.FacilityName" },
        unlabelled = "Not specified",
        attributes = attributes ?? new object[]
        {
            new { name = "UUID", steps = new[] { "data.FacilityID" }, collect = (string?)null },
            new { name = "Country", steps = new[] { "data.GeoContexts.GeoPoliticalEntityID", Country }, collect = (string?)null },
            new { name = "Source", steps = (string[]?)null, collect = "data.Source" },
        },
        clean = clean ?? Array.Empty<object>(),
        keyColumn = (string?)null,
        valueColumn = (string?)null,
        countRecords = false,
        maxValues = (long?)null,
    };

    /// <summary>An example as the operation answers it: the key's row as a build would make it.</summary>
    private const string Example = """
        {"connection":"c","partition":"p","correlationId":"x","answeredUtc":"2026-10-03T00:00:00Z","answer":{
          "key":"dev:master-data--Wellbore:W1:","label":"WB B/2-9 S","labelFrom":"dev:master-data--Wellbore:W1","problem":null,
          "value":"WB B/2-9 S","leftOut":null,"note":null,
          "attributes":[{"name":"Country","value":"United States","from":"dev:master-data--GeoPoliticalEntity:US","records":null},
                        {"name":"Source","value":"WellDB","from":"WellDB","records":2}],
          "records":3,"filter":"data.WellboreID.keyword:\"dev:master-data--Wellbore:W1:\"","notes":["one note"]}}
        """;

    private sealed record Estate(string Cs, string Suffix, string Partition, Guid RepoId, string DimensionFlow, string Taken);

    private static async Task<Estate> SeedAsync()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        // The sample templates, held by a synced repository whose folder this host reads, as the builder reads every
        // repository's.
        await SampleEstate.HoldTemplateRepositoryAsync(cs, DateTime.UtcNow);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var estate = new Estate(cs, suffix, "h" + suffix, FlowIdentity.FromName("repo/cp-dimension-builder-" + suffix), "builder-d-" + suffix, "Taken" + suffix);
        var dimensions = $$"""
            flowType: dimension
            name: {{estate.DimensionFlow}}
            source:
              endpoint: ${env:OSDU_URL}
            dimensions:
              - name: {{estate.Taken}}
                kind: {{WellLog}}
                path: data.Source
            """;
        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(cs);
        db.Repos.Add(new CatalogRepo
        {
            Id = estate.RepoId, Name = "cp-dimension-builder-" + suffix, RemoteUrl = "https://example/cp-dimension-builder.git",
            RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
        });
        foreach (var (name, kind, yaml) in new[] { (estate.DimensionFlow, "dimension", dimensions) })
        {
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = CatalogIdentity.Pipeline(estate.RepoId, name),
                RepoId = estate.RepoId,
                Name = name,
                Kind = kind,
                RelativePath = "flows/" + name + ".yaml",
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

        // The partition the builder reads, with its own connection and no flow to lend one.
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

    private static ControlPlaneAppFactory Host(Estate estate, RecordedOperations operations) => new ControlPlaneAppFactory()
        .WithCatalog(estate.Cs)
        .WithModules(new DeliveryControlPlaneModule())
        .WithSetting("ControlPlane:Worker:Enabled", "false")
        .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
        .WithServices(services => services.AddSingleton(operations.Registry()));

    [Fact]
    public async Task A_draft_is_written_checked_against_the_repositories_templates_and_its_example_made_through_the_connection()
    {
        var estate = await SeedAsync();
        var operations = new RecordedOperations { Answer = _ => Example };
        try
        {
            await using var factory = Host(estate, operations);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var name = "Wellbore" + estate.Suffix;

            var composed = await ComposeAsync(client, token, estate.Partition, new { draft = Draft(name), example = "dev:master-data--Wellbore:W1:" });

            // The YAML is the builder's writer's, and it loads: a repository holds the logs' and the wellbores' templates, so the key and
            // the wellbore's reads are checked; the countries' are not, which reads them as written and says so.
            var yaml = composed.GetProperty("yaml").GetString()!;
            Assert.StartsWith($"  - name: {name}\n    kind: {WellLog}\n    path: data.WellboreID\n    label: data.FacilityName\n", yaml, StringComparison.Ordinal);
            Assert.Contains("      - 'data[GeoPoliticalEntityTypeID*=GeoPoliticalEntityType:Country:].GeoPoliticalEntityName'\n", yaml, StringComparison.Ordinal);
            Assert.True(composed.GetProperty("valid").GetBoolean(), composed.ToString());
            var issues = composed.GetProperty("issues").EnumerateArray().ToList();
            Assert.DoesNotContain(issues, i => i.GetProperty("severity").GetString() == "error");
            Assert.Contains(issues, i => i.GetProperty("code").GetString() == "template" && i.GetProperty("message").GetString()!.Contains("GeoPoliticalEntity", StringComparison.Ordinal));

            // The item's lines are numbered from its first, each thing it declares where it is written.
            var item = composed.GetProperty("item");
            Assert.Equal(1, item.GetProperty("firstLine").GetInt32());
            Assert.Equal(yaml.TrimEnd('\n').Split('\n'), item.GetProperty("lines").EnumerateArray().Select(l => l.GetString()));
            var spans = item.GetProperty("spans").EnumerateArray().ToDictionary(s => s.GetProperty("target").GetString()!, s => s.GetProperty("line").GetInt32());
            Assert.Equal((3, 4), (spans["path"], spans["label.0"]));
            Assert.True(spans.ContainsKey("attributes.Country.1"));

            // The dimension and its blueprint, as the dimension pages describe them, and the table it would write.
            var dimension = composed.GetProperty("dimension");
            Assert.Equal(("WellboreID", "FacilityName"), (dimension.GetProperty("keyColumn").GetString(), dimension.GetProperty("valueColumn").GetString()));
            Assert.Equal(JsonValueKind.Null, dimension.GetProperty("dimensionId").ValueKind);
            Assert.Equal($"osdu.dim_{name}", composed.GetProperty("table").GetString());
            Assert.Equal(WellLog, composed.GetProperty("blueprint").GetProperty("source").GetProperty("template").GetProperty("kind").GetString());

            // The example row is the operation's answer, as the builder shows it.
            var example = composed.GetProperty("example");
            Assert.Equal(("WB B/2-9 S", 3), (example.GetProperty("value").GetString(), example.GetProperty("records").GetInt32()));
            Assert.Equal(["United States", "WellDB"], example.GetProperty("attributes").EnumerateArray().Select(a => a.GetProperty("value").GetString()));
            Assert.Equal(JsonValueKind.Null, composed.GetProperty("exampleProblem").ValueKind);

            // The operation was given the item exactly as written, the key, and how the key and the collected path are indexed,
            // as the repository's template says, through the partition's own connection.
            var ran = operations.Last();
            Assert.Equal((ExploreOperation.OperationName, estate.Partition), (ran.Operation, ran.SourceRef));
            Assert.Equal(
                (ExploreOperation.PartitionConnectionValue, estate.Partition),
                (ran.Argument(ExploreOperation.ConnectionArgument), ran.Argument(DeliveryOperation.PartitionArgument)));
            Assert.Equal(ExploreOperation.DimensionExampleAction, ran.Argument(ExploreOperation.ActionArgument));
            Assert.Equal("dev:master-data--Wellbore:W1:", ran.Argument(ExploreOperation.KeyArgument));
            Assert.Equal(yaml, LongArgument.Read(ran, ExploreOperation.ItemArgument));
            var fields = JsonSerializer.Deserialize<DimensionExampleFields>(LongArgument.Read(ran, ExploreOperation.FieldsArgument)!, ExploreOperation.BuilderJson)!;
            Assert.Equal(new DimensionFieldWire("data.WellboreID", "text", null), fields.Key);
            Assert.Equal(new DimensionFieldWire("data.Source", "text", null), fields.Collected!["Source"]);
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    [Fact]
    public async Task A_kind_no_repository_holds_a_template_of_is_refused_with_where_to_put_one()
    {
        var estate = await SeedAsync();
        var operations = new RecordedOperations();
        try
        {
            await using var factory = Host(estate, operations);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var composed = await ComposeAsync(client, token, estate.Partition, new { draft = Draft("Logs" + estate.Suffix, "osdu:wks:work-product-component--WellLog:9.9.9") });

            Assert.False(composed.GetProperty("valid").GetBoolean());
            var issue = composed.GetProperty("issues").EnumerateArray().Single(i => i.GetProperty("severity").GetString() == "error");
            Assert.Equal(("kind", "template"), (issue.GetProperty("target").GetString(), issue.GetProperty("code").GetString()));
            Assert.Contains("No template a synced repository holds matches", issue.GetProperty("message").GetString(), StringComparison.Ordinal);

            // It still loads, so it is still described, and no example was asked for.
            Assert.NotEqual(JsonValueKind.Null, composed.GetProperty("blueprint").ValueKind);
            Assert.Empty(operations.Given);
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    /// <summary>A draft the loader refuses, and the part the builder points at.</summary>
    public static TheoryData<string, string> Mistakes => new()
    {
        { """{"name":"Well bore","kind":"osdu:wks:work-product-component--WellLog:1.4.0","path":"data.WellboreID","label":[],"attributes":[],"clean":[]}""", "name" },
        { """{"name":"W","kind":"osdu:wks:WellLog","path":"data.WellboreID","label":[],"attributes":[],"clean":[]}""", "kind" },
        { """{"name":"W","kind":"osdu:wks:work-product-component--WellLog:1.4.0","path":"data..x","label":[],"attributes":[],"clean":[]}""", "path" },
        { """{"name":"W","kind":"osdu:wks:work-product-component--WellLog:1.4.0","path":"data.WellboreID","label":["a","b","c","d"],"attributes":[],"clean":[]}""", "label" },
        { """{"name":"W","kind":"osdu:wks:work-product-component--WellLog:1.4.0","path":"data.WellboreID","label":[],"attributes":[{"name":"key","steps":["data.X"],"collect":null}],"clean":[]}""", "attributes.key" },
        { """{"name":"W","kind":"osdu:wks:work-product-component--WellLog:1.4.0","path":"data.WellboreID","label":[],"attributes":[],"clean":[{"step":"shout"}]}""", "clean" },
        { """{"name":"","kind":"","path":null,"label":[],"attributes":[],"clean":[]}""", "name" },
    };

    [Theory]
    [MemberData(nameof(Mistakes))]
    public async Task Each_mistake_is_said_in_the_loaders_words_and_pointed_at(string draft, string target)
    {
        var estate = await SeedAsync();
        var operations = new RecordedOperations();
        try
        {
            await using var factory = Host(estate, operations);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var composed = await ComposeAsync(client, token, estate.Partition, JsonDocument.Parse($$"""{"draft":{{draft}},"example":"x"}""").RootElement);

            Assert.False(composed.GetProperty("valid").GetBoolean());
            var error = composed.GetProperty("issues").EnumerateArray().First(i => i.GetProperty("severity").GetString() == "error");
            Assert.Equal(target, error.GetProperty("target").GetString());
            Assert.DoesNotContain("dimensions[0]", error.GetProperty("message").GetString(), StringComparison.Ordinal);

            // What does not load is shown as written, with nothing linked, and nothing is asked of OSDU for it.
            Assert.Equal(JsonValueKind.Null, composed.GetProperty("blueprint").ValueKind);
            Assert.Equal(JsonValueKind.Null, composed.GetProperty("dimension").ValueKind);
            Assert.Empty(composed.GetProperty("item").GetProperty("spans").EnumerateArray());
            Assert.NotEmpty(composed.GetProperty("item").GetProperty("lines").EnumerateArray());
            Assert.Empty(operations.Given);
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    [Fact]
    public async Task A_name_whose_table_another_flows_dimension_writes_is_warned_of()
    {
        var estate = await SeedAsync();
        try
        {
            await using var factory = Host(estate, new RecordedOperations());
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The same name, and one apart only in a character a table's name leaves out, would write the same table.
            foreach (var name in new[] { estate.Taken, estate.Taken.ToUpperInvariant() })
            {
                var composed = await ComposeAsync(client, token, estate.Partition, new { draft = Draft(name) });
                var warning = composed.GetProperty("issues").EnumerateArray().Single(i => i.GetProperty("code").GetString() == "name");
                Assert.Equal(("warning", "name"), (warning.GetProperty("severity").GetString(), warning.GetProperty("target").GetString()));
                Assert.Contains(estate.DimensionFlow, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
                Assert.Contains($"osdu.dim_{estate.Taken}", warning.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            }

            var free = await ComposeAsync(client, token, estate.Partition, new { draft = Draft("Free" + estate.Suffix) });
            Assert.DoesNotContain(free.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "name");
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    [Fact]
    public async Task An_example_the_operation_cannot_make_says_why_and_the_yaml_stands()
    {
        var estate = await SeedAsync();
        var operations = new RecordedOperations { Answer = _ => throw new SqlFlowException("the search service would not read the key's records") };
        try
        {
            await using var factory = Host(estate, operations);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var composed = await ComposeAsync(client, token, estate.Partition, new { draft = Draft("Wellbore" + estate.Suffix), example = "dev:master-data--Wellbore:W1:" });
            Assert.True(composed.GetProperty("valid").GetBoolean());
            Assert.Equal(JsonValueKind.Null, composed.GetProperty("example").ValueKind);
            Assert.Contains("would not read the key's records", composed.GetProperty("exampleProblem").GetString(), StringComparison.Ordinal);

            // A partition the registry does not hold has no connection to read an example through; the YAML is written all the same.
            var unreached = await ComposeAsync(client, token, "z" + estate.Suffix, new { draft = Draft("Wellbore" + estate.Suffix), example = "dev:master-data--Wellbore:W1:" });
            Assert.Contains("is not registered with the catalog", unreached.GetProperty("exampleProblem").GetString(), StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(unreached.GetProperty("yaml").GetString()));
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    [Fact]
    public async Task The_keys_are_read_through_the_connection_the_keys_index_settled_by_the_template_or_said_to_be_a_guess()
    {
        var estate = await SeedAsync();
        var operations = new RecordedOperations();
        try
        {
            await using var factory = Host(estate, operations);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            using (var read = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/explorer/dimension/keys", new
            {
                kind = WellLog,
                query = " data.Source:\"WellDB\" ",
                path = " data.WellboreID ",
            }, estate.Partition))
            {
                Assert.True(read.StatusCode == HttpStatusCode.OK, await read.Content.ReadAsStringAsync());
            }

            var ran = operations.Last();
            Assert.Equal((ExploreOperation.DimensionKeysAction, estate.Partition), (ran.Argument(ExploreOperation.ActionArgument), ran.SourceRef));
            var request = JsonSerializer.Deserialize<DimensionKeysRequest>(LongArgument.Read(ran, ExploreOperation.KeysArgument)!, ExploreOperation.BuilderJson)!;
            Assert.Equal((WellLog, "data.Source:\"WellDB\"", "data.WellboreID"), (request.Kind, request.Query, request.Path));
            Assert.Equal(new DimensionFieldWire("data.WellboreID", "text", null), request.KeyField);
            Assert.False(request.KeyFieldGuessed);

            // A kind no repository holds a template of is read with the key read as text, said to be a guess.
            using (var guessed = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/explorer/dimension/keys", new
            {
                kind = "osdu:wks:work-product-component--WellLog:9.9.9",
                path = "data.WellboreID",
            }, estate.Partition))
            {
                Assert.True(guessed.StatusCode == HttpStatusCode.OK, await guessed.Content.ReadAsStringAsync());
            }

            var guess = JsonSerializer.Deserialize<DimensionKeysRequest>(LongArgument.Read(operations.Last(), ExploreOperation.KeysArgument)!, ExploreOperation.BuilderJson)!;
            Assert.True(guess.KeyFieldGuessed);
            Assert.Equal("text", guess.KeyField!.Index);

            // A property of the record itself is indexed as the indexer maps it, template or not.
            using (var own = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/explorer/dimension/keys", new
            {
                kind = "osdu:wks:work-product-component--WellLog:9.9.9",
                path = "legal.legaltags",
            }, estate.Partition))
            {
                Assert.True(own.StatusCode == HttpStatusCode.OK, await own.Content.ReadAsStringAsync());
            }

            var tags = JsonSerializer.Deserialize<DimensionKeysRequest>(LongArgument.Read(operations.Last(), ExploreOperation.KeysArgument)!, ExploreOperation.BuilderJson)!;
            Assert.Equal(("keyword", false), (tags.KeyField!.Index, tags.KeyFieldGuessed));

            // A partition the registry does not hold has no connection to read through.
            using var unreached = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/explorer/dimension/keys", new { kind = WellLog, path = "data.WellboreID" }, "z" + estate.Suffix);
            Assert.Equal(HttpStatusCode.Conflict, unreached.StatusCode);
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    [Fact]
    public async Task The_keys_a_kinds_template_suggests_are_answered_from_the_repositories_templates_alone()
    {
        var estate = await SeedAsync();
        var operations = new RecordedOperations();
        try
        {
            await using var factory = Host(estate, operations);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            using (var suggested = await SendAsync(client, token, HttpMethod.Get, "/api/v1/delivery/explorer/dimension/candidates?kind=" + Uri.EscapeDataString(" *:*:work-product-component--WellLog:* "), null, estate.Partition))
            {
                var text = await suggested.Content.ReadAsStringAsync();
                Assert.True(suggested.StatusCode == HttpStatusCode.OK, text);
                var answer = JsonDocument.Parse(text).RootElement;
                Assert.Equal("*:*:work-product-component--WellLog:*", answer.GetProperty("kind").GetString());

                // A kind of the pattern a synced repository holds answers, read from its file. The shared test database holds
                // other suites' repositories too, some of whose folders are gone, so which kind is read from what they hold
                // rather than assumed; which of several is newest is the blueprint's own suite's to say.
                await using (var osdu = SampleEstate.Context(estate.Cs))
                {
                    var held = await osdu.DeliveryRepositoryTemplates.AsNoTracking()
                        .Where(t => t.Kind.StartsWith("osdu:wks:work-product-component--WellLog:"))
                        .Select(t => t.Kind)
                        .Distinct()
                        .ToListAsync();
                    Assert.Contains(WellLog, held);
                    Assert.Contains(answer.GetProperty("template").GetProperty("kind").GetString(), held);
                }

                var first = answer.GetProperty("keys")[0];
                Assert.Equal(("data.WellboreID", false), (first.GetProperty("path").GetString(), first.GetProperty("repeated").GetBoolean()));
                Assert.Contains("master-data--Wellbore", first.GetProperty("names").EnumerateArray().Select(n => n.GetString()));
                Assert.Equal(JsonValueKind.Null, answer.GetProperty("missing").ValueKind);
            }

            using (var unsaved = await SendAsync(client, token, HttpMethod.Get, "/api/v1/delivery/explorer/dimension/candidates?kind=" + Uri.EscapeDataString("osdu:wks:master-data--Nothing:1.0.0"), null, estate.Partition))
            {
                var answer = JsonDocument.Parse(await unsaved.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal(HttpStatusCode.OK, unsaved.StatusCode);
                Assert.Empty(answer.GetProperty("keys").EnumerateArray());
                Assert.Contains("No template a synced repository holds matches", answer.GetProperty("missing").GetString(), StringComparison.Ordinal);
            }

            foreach (var (kind, why) in new[] { ("", "Name the kind"), ("osdu:wks:WellLog", "osdu:wks:WellLog") })
            {
                using var refused = await SendAsync(client, token, HttpMethod.Get, "/api/v1/delivery/explorer/dimension/candidates?kind=" + Uri.EscapeDataString(kind), null, estate.Partition);
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
                Assert.Contains(why, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // Nothing is asked of OSDU: the suggestions are the templates'.
            Assert.Empty(operations.Given);
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    /// <summary>A request the builder refuses before anything is read, and why.</summary>
    public static TheoryData<string, string, string> Refusals => new()
    {
        { "compose", "{}", "Send the draft" },
        { "compose", "{\"draft\":{\"name\":\"" + new string('x', DimensionBuilder.MaxTextLength + 1) + "\",\"kind\":\"k\"}}", "longer than" },
        {
            "compose",
            "{\"draft\":{\"name\":\"W\",\"kind\":\"k\",\"attributes\":[" + string.Join(',', Enumerable.Repeat("{\"name\":\"A\",\"steps\":[\"data.A\"]}", DimensionBuilder.MaxListLength + 1)) + "]}}",
            "more than the 64 entries"
        },
        { "keys", "{}", "Name the kind" },
        { "keys", "{\"kind\":\"osdu:wks:WellLog\",\"path\":\"data.WellboreID\"}", "osdu:wks:WellLog" },
        { "keys", "{\"kind\":\"" + WellLog + "\"}", "Name the key's path" },
        { "keys", "{\"kind\":\"" + WellLog + "\",\"path\":\" \"}", "Name the key's path" },
        { "keys", "{\"kind\":\"" + WellLog + "\",\"path\":\"data..x\"}", "is not a property path" },
        { "keys", "{\"kind\":\"" + WellLog + "\",\"path\":\"data[Type=x].WellboreID\"}", "is not a property path" },
        { "keys", "{\"kind\":\"" + WellLog + "\",\"path\":\"acl.nothing\"}", "The key " },
        { "keys", "{\"kind\":\"" + WellLog + "\",\"path\":\"data.WellboreID\",\"query\":\"" + new string('q', DeliveryExplorerEndpoints.MaxQueryLength + 1) + "\"}", "longer than the" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_request_the_builder_cannot_read_is_refused_before_anything_is_read(string route, string body, string why)
    {
        var estate = await SeedAsync();
        var operations = new RecordedOperations();
        try
        {
            await using var factory = Host(estate, operations);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/v1/delivery/explorer/dimension/{route}", UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Osdu-Partition", estate.Partition);
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{route} answered {(int)response.StatusCode}: {text}");
            Assert.Contains(why, text, StringComparison.Ordinal);
            Assert.Empty(operations.Given);
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    private static async Task<JsonElement> ComposeAsync(HttpClient client, string token, string partition, object body)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/explorer/dimension/compose", body, partition);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"compose answered {(int)response.StatusCode}: {text}");
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
