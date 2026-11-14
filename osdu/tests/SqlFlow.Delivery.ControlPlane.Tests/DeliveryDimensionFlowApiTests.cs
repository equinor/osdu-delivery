using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Writing a whole dimension flow file (osdu/docs/reference/guides/dimension-flow-file.md), as the API serves the assistant:
/// the patterns a new flow follows, the catalog's dimension flows each with its document but its dimensions and views, and
/// the connections the estate's flows keep their data through; and a whole document checked by the loader, its templates,
/// the tables the catalog's other flows write, the registry's partitions and where the estate keeps its data. Nothing is
/// written, and nothing reaches an OSDU.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryDimensionFlowApiTests
{
    private const string WellLog = "osdu:wks:work-product-component--WellLog:1.4.0";

    private sealed record Estate(string Cs, string Suffix, string Partition, Guid RepoId, string Repository, string DimensionFlow, string Taken, string DataDb);

    /// <summary>
    /// A repository holding a dimension flow, keeping its tables in the module's database and declaring a view, and an
    /// ingestion flow reading and writing the estate's data through a connection of this test's own; the sample templates,
    /// held by a synced repository; and a registered partition.
    /// </summary>
    private static async Task<Estate> SeedAsync()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
        await SampleEstate.HoldTemplateRepositoryAsync(cs, DateTime.UtcNow);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var repository = "cp-dimension-flow-" + suffix;
        var estate = new Estate(
            cs, suffix, "f" + suffix, FlowIdentity.FromName("repo/" + repository), repository, "acme-d-" + suffix, "Taken" + suffix,
            "${env:ACME_DATA_DB_" + suffix.ToUpperInvariant() + "}");
        var dimensions = $$"""
            # The well logs' dimensions.
            flowType: dimension
            name: {{estate.DimensionFlow}}
            description: The well logs' dimensions.
            batch: acme
            partitions: [{{estate.Partition}}]

            parameters:
              deliveredBy: { default: acme }

            source:
              endpoint: ${env:OSDU_URL}
              auth:
                type: oauth2ClientCredentials
                secondarySecretRef: ${env:OSDU_CLIENT_ID}
                secretRef: ${env:OSDU_CLIENT_SECRET}
                token:
                  url: ${env:OSDU_TOKEN_URL}

            target:
              connection: ${env:SQLFLOW_CATALOG_DB}

            dimensions:
              - name: {{estate.Taken}}
                kind: {{WellLog}}
                query: 'tags.DeliveredBy:"{deliveredBy}"'
                path: data.Source
            # The view over them.
            views:
              - name: v{{estate.Taken}}
                from: {{estate.Taken}}

            parallelism: { recordsAtOnce: 4 }
            """;
        var ingestion = $$"""
            flowType: ing
            name: acme-ing-{{suffix}}
            connections:
              acmeData: {{estate.DataDb}}
            source: { server: acmeData, object: AcmeData.pre.v_WellLog }
            target: { server: acmeData, object: AcmeData.silver.WellLog }
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
            (estate.DimensionFlow, "dimension", dimensions, (string?)null, "file"),
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
        await db.Pipelines.Where(p => p.RepoId == estate.RepoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == estate.RepoId).ExecuteDeleteAsync();
        await TestPartitions.ForgetAsync(estate.Cs, estate.Partition);
    }

    private static ControlPlaneAppFactory Host(Estate estate) => new ControlPlaneAppFactory()
        .WithCatalog(estate.Cs)
        .WithModules(new DeliveryControlPlaneModule())
        .WithSetting("ControlPlane:Worker:Enabled", "false")
        .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");

    /// <summary>A new flow of the estate's own shape: its source and partition, its tables kept where its data is, one dimension.</summary>
    private static string NewFlow(Estate estate, string name, string dimension, string? target = null, string partition = "") => $$"""
        flowType: dimension
        name: {{name}}
        description: The wellbores the well logs name.
        batch: acme
        partitions: [{{(partition == "" ? estate.Partition : partition)}}]

        source:
          endpoint: ${env:OSDU_URL}
          auth:
            type: oauth2ClientCredentials
            secondarySecretRef: ${env:OSDU_CLIENT_ID}
            secretRef: ${env:OSDU_CLIENT_SECRET}
            token:
              url: ${env:OSDU_TOKEN_URL}
        {{target ?? $"\ntarget:\n  connection: {estate.DataDb}\n  schema: dim\n"}}
        dimensions:
          - name: {{dimension}}
            kind: {{WellLog}}
            path: data.WellboreID
            label: data.FacilityName
            unlabelled: Not specified
        """;

    [Fact]
    public async Task The_patterns_are_the_catalogs_dimension_flows_as_written_but_their_dimensions_and_the_connections_its_data_is_kept_through()
    {
        var estate = await SeedAsync();
        try
        {
            await using var factory = Host(estate);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            var patterns = await SendAsync(client, token, HttpMethod.Get, "/api/v1/delivery/dimensions/flow-patterns", null);
            Assert.Equal("${env:SQLFLOW_CATALOG_DB}", patterns.GetProperty("moduleConnection").GetString());

            // The flow, where its file is and what it declares, and its document as written, comments and all, but the
            // dimensions and the views, which a new flow writes its own of.
            var flow = patterns.GetProperty("flows").EnumerateArray().Single(f => f.GetProperty("name").GetString() == estate.DimensionFlow);
            Assert.Equal(
                (estate.Repository, $"flows/{estate.DimensionFlow}.yaml", "acme"),
                (flow.GetProperty("repository").GetString(), flow.GetProperty("relativePath").GetString(), flow.GetProperty("batch").GetString()));
            Assert.Equal([estate.Partition], flow.GetProperty("partitions").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal(["deliveredBy"], flow.GetProperty("parameters").EnumerateArray().Select(p => p.GetString()));
            Assert.Equal([estate.Taken], flow.GetProperty("dimensions").EnumerateArray().Select(d => d.GetString()));
            Assert.Equal(["v" + estate.Taken], flow.GetProperty("views").EnumerateArray().Select(d => d.GetString()));
            Assert.Equal([WellLog], flow.GetProperty("kinds").EnumerateArray().Select(d => d.GetString()));
            Assert.Equal(JsonValueKind.Null, flow.GetProperty("problem").ValueKind);

            // Its target names the module's connection and no database: the module's database, as spelled.
            var target = flow.GetProperty("target");
            Assert.Equal(("${env:SQLFLOW_CATALOG_DB}", "osdu", true), (target.GetProperty("connection").GetString(), target.GetProperty("schema").GetString(), target.GetProperty("inModuleDatabase").GetBoolean()));

            var envelope = flow.GetProperty("envelope").GetString()!;
            Assert.StartsWith("# The well logs' dimensions.\nflowType: dimension\n", envelope, StringComparison.Ordinal);
            Assert.Contains("    secretRef: ${env:OSDU_CLIENT_SECRET}\n", envelope, StringComparison.Ordinal);
            Assert.Contains("target:\n  connection: ${env:SQLFLOW_CATALOG_DB}\n", envelope, StringComparison.Ordinal);
            Assert.EndsWith("parallelism: { recordsAtOnce: 4 }\n", envelope, StringComparison.Ordinal);
            Assert.DoesNotContain("dimensions:", envelope, StringComparison.Ordinal);
            Assert.DoesNotContain("views:", envelope, StringComparison.Ordinal);
            Assert.DoesNotContain(estate.Taken, envelope, StringComparison.Ordinal);
            Assert.False(flow.GetProperty("envelopeCut").GetBoolean());

            // The estate's data is kept through the ingestion flow's connection, which it reads and writes; the module's own
            // is marked as such wherever a flow names it.
            var data = patterns.GetProperty("connections").EnumerateArray().Single(c => c.GetProperty("reference").GetString() == estate.DataDb);
            Assert.False(data.GetProperty("module").GetBoolean());
            Assert.Equal(1, data.GetProperty("writtenBy").GetProperty("ing").GetInt32());
            Assert.Equal(1, data.GetProperty("readBy").GetProperty("ing").GetInt32());
            Assert.DoesNotContain(patterns.GetProperty("connections").EnumerateArray(), c => c.GetProperty("reference").GetString() == "file");
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    [Fact]
    public async Task A_flow_of_the_estates_shape_keeping_its_tables_where_its_data_is_is_valid_and_says_where_each_table_goes()
    {
        var estate = await SeedAsync();
        try
        {
            await using var factory = Host(estate);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var name = "acme-new-" + estate.Suffix;
            var dimension = "Wellbore" + estate.Suffix;

            var check = await CheckAsync(client, token, NewFlow(estate, name, dimension), $"flows/{name}.yaml");

            Assert.True(check.GetProperty("loads").GetBoolean(), check.ToString());
            Assert.True(check.GetProperty("valid").GetBoolean(), check.ToString());
            Assert.Equal(name, check.GetProperty("name").GetString());
            Assert.Empty(check.GetProperty("issues").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, check.GetProperty("existing").ValueKind);

            var target = check.GetProperty("target");
            Assert.Equal((estate.DataDb, "dim", false), (target.GetProperty("connection").GetString(), target.GetProperty("schema").GetString(), target.GetProperty("inModuleDatabase").GetBoolean()));
            var table = check.GetProperty("dimensions").EnumerateArray().Single();
            Assert.Equal(
                (dimension, WellLog, "data.WellboreID", $"dim.dim_{dimension}"),
                (table.GetProperty("name").GetString(), table.GetProperty("kind").GetString(), table.GetProperty("path").GetString(), table.GetProperty("table").GetString()));
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    [Fact]
    public async Task What_would_stop_a_build_is_an_error_and_what_would_not_is_a_warning_each_pointed_at_its_part()
    {
        var estate = await SeedAsync();
        try
        {
            await using var factory = Host(estate);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            var name = "acme-new-" + estate.Suffix;

            // A dimension whose table another flow's dimension writes: a build is refused, so it is an error naming that flow.
            var taken = await CheckAsync(client, token, NewFlow(estate, name, estate.Taken), null);
            Assert.True(taken.GetProperty("loads").GetBoolean());
            Assert.False(taken.GetProperty("valid").GetBoolean());
            var collision = Single(taken, "error");
            Assert.Equal((estate.Taken, "name"), (collision.GetProperty("dimension").GetString(), collision.GetProperty("target").GetString()));
            Assert.Contains($"flow {estate.DimensionFlow}", collision.GetProperty("message").GetString(), StringComparison.Ordinal);

            // The same dimension in a document of the flow's own name is the flow replacing itself: no collision, and the
            // flow it replaces, with a warning saying so.
            var replacing = await CheckAsync(client, token, NewFlow(estate, estate.DimensionFlow, estate.Taken), null);
            Assert.True(replacing.GetProperty("valid").GetBoolean(), replacing.ToString());
            var existing = replacing.GetProperty("existing");
            Assert.Equal(
                (estate.Repository, $"flows/{estate.DimensionFlow}.yaml", "dimension"),
                (existing.GetProperty("repository").GetString(), existing.GetProperty("relativePath").GetString(), existing.GetProperty("kind").GetString()));
            Assert.Contains("replaces it", Single(replacing, "warning").GetProperty("message").GetString(), StringComparison.Ordinal);

            // A flow keeping its tables in the module's database is told where the estate keeps its data.
            var inModule = await CheckAsync(client, token, NewFlow(estate, name, "Wellbore" + estate.Suffix, target: ""), null);
            Assert.True(inModule.GetProperty("valid").GetBoolean());
            Assert.Equal(JsonValueKind.Null, inModule.GetProperty("target").ValueKind);
            var module = Single(inModule, "warning");
            Assert.Equal("target", module.GetProperty("target").GetString());
            Assert.Contains($"names no target, so it keeps its dimension tables and views in the module's database", module.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains(estate.DataDb, module.GetProperty("message").GetString(), StringComparison.Ordinal);

            // A connection no flow of the catalog names must be set where a build resolves it; a partition the registry does
            // not hold refuses a run.
            var unknown = await CheckAsync(
                client, token, NewFlow(estate, name, "Wellbore" + estate.Suffix, target: "\ntarget:\n  connection: ${env:ACME_ELSEWHERE_" + estate.Suffix + "}\n", partition: "nowhere" + estate.Suffix), null);
            Assert.True(unknown.GetProperty("valid").GetBoolean());
            var warnings = unknown.GetProperty("issues").EnumerateArray().ToList();
            Assert.All(warnings, w => Assert.Equal("warning", w.GetProperty("severity").GetString()));
            Assert.Contains(warnings, w => w.GetProperty("target").GetString() == "target" && w.GetProperty("message").GetString()!.Contains("is named by no flow of the catalog", StringComparison.Ordinal));
            Assert.Contains(warnings, w => w.GetProperty("target").GetString() == "partitions" && w.GetProperty("message").GetString()!.Contains($"'nowhere{estate.Suffix}' is not registered", StringComparison.Ordinal));

            // A document the loader refuses says why in its words, with no name of the file it was sent as.
            var broken = await CheckAsync(client, token, NewFlow(estate, name, "Wellbore" + estate.Suffix).Replace("batch: acme", "bogus: acme", StringComparison.Ordinal), "flows/x.yaml");
            Assert.False(broken.GetProperty("loads").GetBoolean());
            Assert.False(broken.GetProperty("valid").GetBoolean());
            var refusal = Single(broken, "error").GetProperty("message").GetString()!;
            Assert.Contains("bogus", refusal, StringComparison.Ordinal);
            Assert.DoesNotContain("flows/x.yaml:", refusal, StringComparison.Ordinal);
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    [Fact]
    public async Task A_check_with_no_document_is_refused()
    {
        var estate = await SeedAsync();
        try
        {
            await using var factory = Host(estate);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/delivery/dimensions/flow-check", UriKind.Relative))
            {
                Content = JsonContent.Create(new { yaml = "  " }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("whole dimension flow document", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            await CleanAsync(estate);
        }
    }

    private static JsonElement Single(JsonElement check, string severity)
        => check.GetProperty("issues").EnumerateArray().Single(i => i.GetProperty("severity").GetString() == severity);

    private static Task<JsonElement> CheckAsync(HttpClient client, string token, string yaml, string? path)
        => SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/dimensions/flow-check", new { yaml, path });

    private static async Task<JsonElement> SendAsync(HttpClient client, string token, HttpMethod method, string route, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(route, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{route} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
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
