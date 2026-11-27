using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The type each column a delivery flow's mapping reads should have, as the API serves it: the wellbore fixture flow and its
/// mapping, widened with a whole-number and a number property, its template held from the sample's template folder, and
/// the lineage a pre-ingestion flow and an ingestion flow leave in the catalog. A column of bit for a whole-number property
/// is refused with the type to declare, the type the pre flow declares and the one its last run detected, and where its
/// values can be profiled; a floating point column for a number is approximate; text for text fits. Nothing is read of a
/// row, and nothing reaches an OSDU.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryColumnTypesApiTests
{
    [Fact]
    public async Task Each_column_a_mapping_reads_is_advised_for_the_property_it_reaches_with_the_types_upstream()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var repoId = FlowIdentity.FromName("repo/cp-coltypes-" + suffix);
        var flowName = "api-coltypes-" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var ingId = CatalogIdentity.Pipeline(repoId, flowName + "-ing");
        var preId = CatalogIdentity.Pipeline(repoId, flowName + "-pre");
        const string server = "${env:OSDU_DATA_DB}";
        var record = Key("silver", "wellbore" + suffix);
        var aliases = Key("silver", "wellbore" + suffix + "alias");
        var view = Key("pre", "v_wellbore" + suffix);
        var raw = Key("pre", "wellbore" + suffix);
        var now = DateTime.UtcNow;

        var flowYaml = File.ReadAllText(Samples.WellboreFlowFile)
            .Replace("name: wells-wellbore-03-header-delivery", "name: " + flowName, StringComparison.Ordinal)
            .Replace("OsduData.silver.Wellbore", "OsduData.silver.Wellbore" + suffix, StringComparison.Ordinal);
        var mappingYaml = SampleEstate.MappingYaml("Wellbore@1.0.0").Replace(
            "  data:\n",
            "  data:\n    SequenceNumber:\n      $from: sequence_number\n      $required: false\n"
            + "    WellboreCosts:\n      $forEach: aliases\n      $required: false\n      $item:\n        Cost: { $from: cost }\n",
            StringComparison.Ordinal);
        Assert.Contains("SequenceNumber", mappingYaml, StringComparison.Ordinal);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = "cp-coltypes-" + suffix, RootPath = SampleEstate.TemplateRoot, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.AddRange(
                    Pipeline(pipelineId, repoId, flowName, "delivery", flowYaml, now),
                    Pipeline(ingId, repoId, flowName + "-ing", "ing", "flowType: ing\n", now),
                    Pipeline(preId, repoId, flowName + "-pre", "file", "name: pre\n", now));
                db.Objects.AddRange(
                    Table(record, server, "silver", "Wellbore" + suffix, now),
                    Table(aliases, server, "silver", "Wellbore" + suffix + "Alias", now),
                    Table(view, server, "pre", "v_Wellbore" + suffix, now, kind: "View"),
                    Table(raw, server, "pre", "Wellbore" + suffix, now));
                db.ObjectColumns.AddRange(
                    Column(record, 1, "facility_name", "nvarchar(200)"),
                    Column(record, 2, "sequence_number", "bit"),
                    Column(aliases, 1, "alias_name", "nvarchar(100)"),
                    Column(aliases, 2, "cost", "float"));
                db.LineageEdges.AddRange(
                    Edge(repoId, ingId, flowName + "-ing", "Writes", record),
                    Edge(repoId, ingId, flowName + "-ing", "Reads", view),
                    Edge(repoId, preId, flowName + "-pre", "Creates", view),
                    Edge(repoId, preId, flowName + "-pre", "Writes", raw));
                db.PipelineColumns.AddRange(
                    Transform(repoId, preId, "declared", 1, "sequence_number", "int"),
                    Transform(repoId, preId, "detected", 1, "facility_name", "nvarchar(200)"),
                    Transform(repoId, preId, "detected", 2, "sequence_number", "bit"));
                await db.SaveChangesAsync();
            }

            await using (var osdu = SampleEstate.Context(cs))
            {
                osdu.DeliveryMappings.Add(new DeliveryMapping
                {
                    Id = FlowIdentity.FromName($"delivery-mapping/{repoId:N}/Wellbore@1.0.0"),
                    RepoId = repoId,
                    Reference = "Wellbore@1.0.0",
                    Name = "Wellbore",
                    Version = "1.0.0",
                    Kind = SampleEstate.WellboreTemplateKind,
                    TemplateVersion = SampleEstate.WellboreTemplateVersion,
                    RelativePath = "mappings/Wellbore@1.0.0.yaml",
                    ContentHash = new string('2', 64),
                    Yaml = mappingYaml,
                    SummaryJson = "{}",
                    Status = "valid",
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
                await osdu.SaveChangesAsync();
            }

            await SampleEstate.HoldTemplatesAsync(cs, repoId, now);

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false");
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            using var response = await GetAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/column-types?partition=dev");
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, text);
            var answer = JsonDocument.Parse(text).RootElement;
            Assert.Equal("Wellbore@1.0.0", answer.GetProperty("mapping").GetString());
            Assert.Equal(JsonValueKind.Null, answer.GetProperty("problem").ValueKind);
            var columns = answer.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("column").GetString()!);

            // A bit for a whole number: refused, with the type to declare, the pre flow's own types and where to profile it.
            var sequence = columns["sequence_number"];
            Assert.Equal(("bit", "refused", "bigint"), (Text(sequence, "type"), Text(sequence, "verdict"), Text(sequence, "recommended")));
            Assert.Equal(("int", "bit"), (Text(sequence, "declaredType"), Text(sequence, "detectedType")));
            Assert.Equal(flowName + "-pre", Text(sequence, "declaredInFlow"));
            Assert.Equal("asInteger", Text(sequence, "profileCount"));
            var property = Assert.Single(sequence.GetProperty("properties").EnumerateArray().ToList());
            Assert.Equal(("data.SequenceNumber", "integer"), (Text(property, "path"), Text(property, "type")));
            var profile = sequence.GetProperty("profile");
            Assert.Equal((server, "OsduData", "pre", "Wellbore" + suffix, "sequence_number"),
                (Text(profile, "reference"), Text(profile, "database"), Text(profile, "schema"), Text(profile, "table"), Text(profile, "column")));

            // A floating point column of the child dataset for a number: approximate, and nothing upstream says more.
            var cost = columns["cost"];
            Assert.Equal(("aliases", "float", "approximate", "decimal(38,18)"),
                (Text(cost, "dataset"), Text(cost, "type"), Text(cost, "verdict"), Text(cost, "recommended")));
            Assert.Equal(JsonValueKind.Null, cost.GetProperty("profile").ValueKind);

            // Text for text fits, the trim it goes through included.
            Assert.Equal("fits", Text(columns["facility_name"], "verdict"));
            Assert.Equal("fits", Text(columns["alias_name"], "verdict"));

            // One column, by name.
            using var one = await GetAsync(client, token, $"/api/v1/delivery/flows/{pipelineId:D}/column-types?partition=dev&column=COST");
            var only = JsonDocument.Parse(await one.Content.ReadAsStringAsync()).RootElement.GetProperty("columns").EnumerateArray().ToList();
            Assert.Equal("cost", Text(Assert.Single(only), "column"));
        }
        finally
        {
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryMappings.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryRepositoryTemplates.Where(t => t.RepoId == repoId).ExecuteDeleteAsync();
            }

            await using var db = CatalogDatabase.Create(cs);
            var keys = new[] { record, aliases, view, raw };
            await db.PipelineColumns.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
            await db.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.ObjectColumns.Where(c => keys.Contains(c.ObjectKey)).ExecuteDeleteAsync();
            await db.Objects.Where(o => keys.Contains(o.Key)).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static string Key(string schema, string name) => $"${{env:osdu_data_db}}|osdudata|{schema}|{name}";

    private static string? Text(JsonElement element, string property) => element.GetProperty(property).GetString();

    private static CatalogPipeline Pipeline(Guid id, Guid repoId, string name, string kind, string yaml, DateTime now) => new()
    {
        Id = id, RepoId = repoId, Name = name, Kind = kind, RelativePath = $"flows/{name}.yaml", ContentHash = new string('0', 64),
        Yaml = yaml, DefinitionJson = "{}", Active = true, FirstSeenUtc = now, LastSeenUtc = now,
    };

    private static CatalogObject Table(string key, string server, string schema, string name, DateTime now, string kind = "Table") => new()
    {
        Key = key, ServerRef = server, Database = "OsduData", Schema = schema, Name = name, Kind = kind, FirstSeenUtc = now, LastSeenUtc = now,
    };

    private static CatalogObjectColumn Column(string key, int ordinal, string name, string type) => new()
    {
        ObjectKey = key, Ordinal = ordinal, Name = name, DataType = type, Nullable = true, Tier = "Derived",
    };

    private static CatalogLineageEdge Edge(Guid repoId, Guid pipelineId, string flow, string relation, string key) => new()
    {
        RepoId = repoId, Flow = flow, PipelineId = pipelineId, Relation = relation, ObjectKey = key, ObjectName = key, Tier = "Declared",
    };

    private static CatalogPipelineColumn Transform(Guid repoId, Guid pipelineId, string kind, int ordinal, string column, string type) => new()
    {
        RepoId = repoId, PipelineId = pipelineId, Kind = kind, Ordinal = ordinal, ColumnName = column, DataType = type,
    };

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }
}
