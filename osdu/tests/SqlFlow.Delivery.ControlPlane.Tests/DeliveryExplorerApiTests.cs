using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Compute;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.Engine.Operations;
using SqlFlow.Delivery.Engine.Search;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Tests;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The explorer as the API serves it (osdu/docs/reference/concepts/explorer.md): each partition read through its own
/// connection, the platform and client the control plane's configuration names for it, with no flow in the catalog; a
/// partition registered without those values, and one not registered, said to have no connection and why; each read run
/// by the control plane with what it reads, and nothing queued for a node; a record page's check of a record run the same
/// way through the record's own flow, refused for a route that keeps no record in storage; and every search the operation
/// would refuse answered as a 400 before it runs. Nothing here reaches an OSDU: the operations are stood in for
/// (<see cref="RecordedOperations"/>), and what each was given is read back.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryExplorerApiTests
{
    [Fact]
    public async Task A_partition_is_read_through_its_own_connection_with_no_flow_and_a_search_the_operation_would_refuse_is_refused_here()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        // What the connection is made of comes from the control plane's configuration here, never from this process's
        // environment, which tools/test.sh keeps free of a checkout's .sqlflow/env.
        var inherited = PartitionConnection.Names.Where(n => Environment.GetEnvironmentVariable(n) is not null).ToList();
        Assert.True(inherited.Count == 0, $"The suite runs without the OSDU connection's values in its environment, and it holds {string.Join(", ", inherited)}.");

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var (named, bare, unregistered) = ("p" + suffix, "b" + suffix, "z" + suffix);
        var values = TestPartitions.ConnectionValues(named);

        var operations = new RecordedOperations();
        try
        {
            await TestPartitions.RegisterAsync(cs, named);
            await TestPartitions.RegisterAsync(cs, bare, connected: false);

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithServices(services => services.AddSingleton(operations.Registry()));
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // The partition is the registry's, whatever the case it is asked in, and its connection reaches the platform its
            // values name.
            var connection = await ConnectionAsync(client, token, $"?partition={named.ToUpperInvariant()}", null);
            Assert.Equal(
                (named, true, $"https://osdu-{named}.example.com/", (string?)null),
                (Text(connection, "partition"), connection.GetProperty("available").GetBoolean(), Text(connection, "endpoint"), Text(connection, "reason")));
            Assert.False(connection.TryGetProperty("through", out _));

            // The workbench's partition, carried in its header, is the one read when the request names none.
            Assert.Equal(named, Text(await ConnectionAsync(client, token, string.Empty, named), "partition"));

            // A registered partition whose configuration leaves the connection's values unset says which and where to set them.
            var unset = await ConnectionAsync(client, token, $"?partition={bare}", null);
            Assert.False(unset.GetProperty("available").GetBoolean());
            Assert.Null(Text(unset, "endpoint"));
            Assert.Contains($"The explorer reads partition '{bare}' through the platform and client its configuration names", Text(unset, "reason"), StringComparison.Ordinal);
            Assert.Contains(PartitionConnection.ClientSecretName, Text(unset, "reason"), StringComparison.Ordinal);
            Assert.Contains($"under Osdu:Partitions:{bare}:Properties in the control plane's configuration, or in its environment.", Text(unset, "reason"), StringComparison.Ordinal);

            // A partition the registry does not hold is read nowhere, as nothing runs in it.
            var notRegistered = await ConnectionAsync(client, token, $"?partition={unregistered}", null);
            Assert.False(notRegistered.GetProperty("available").GetBoolean());
            Assert.Contains($"Partition '{unregistered}' is not registered with the catalog", Text(notRegistered, "reason"), StringComparison.Ordinal);

            // A search runs as asked, through the partition's own connection, given the partition's values to resolve it with.
            var searched = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/search?partition={named}", new
            {
                text = "Wellbore B",
                kind = "*:*:master-data--Wellbore:*",
                filters = new[] { new { path = "legal.legaltags", index = "keyword", value = "dev-private" } },
                sort = "Modified",
                offset = 100,
                limit = 100,
                facet = new { path = "data.FacilityTypeID" },
            });
            Assert.Equal((ExploreOperation.OperationName, named), (searched.Operation, searched.SourceRef));
            Assert.Equal(
                (ExploreOperation.SearchAction, ExploreOperation.PartitionConnectionValue, named),
                (searched.Argument(ExploreOperation.ActionArgument), searched.Argument(ExploreOperation.ConnectionArgument), searched.Argument(DeliveryOperation.PartitionArgument)));
            Assert.NotNull(searched.References);
            Assert.All(values, value => Assert.Equal(value.Value, searched.References!.GetValueOrDefault(value.Key)));
            Assert.Null(searched.Argument("repoRoot"));
            var search = ExplorerSearch.Parse(searched.Argument(ExploreOperation.SearchArgument));
            Assert.Equal(("Wellbore B", "*:*:master-data--Wellbore:*", ExplorerSort.Modified, 100, 100), (search.Text, search.Kind, search.Sort, search.Offset, search.Limit));
            Assert.Equal(new ExplorerFilter { Path = "legal.legaltags", Index = OsduFieldIndex.Keyword, Value = "dev-private" }, Assert.Single(search.Filters));
            Assert.Equal(new ExplorerField { Path = "data.FacilityTypeID", Index = OsduFieldIndex.Text }, search.Facet);

            // A condition names what the property must hold, the nested list it is reached through, and the columns shown.
            var conditioned = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/search?partition={named}", new
            {
                kind = "*:*:master-data--Wellbore:*",
                filters = new object[]
                {
                    new { path = "data.Source", condition = "AnyOf", values = new[] { "WellDB", "WELLDB" } },
                    new { path = "data.GeoContexts.FieldID", nested = " data.GeoContexts ", value = "dev:master-data--Field:FIELD-A:" },
                    new { path = "data.Augmented.FieldId", condition = "missing" },
                },
                facet = new { path = "data.GeoContexts.FieldID", nested = "data.GeoContexts" },
                columns = new[] { " data.Augmented.WellboreName " },
            });
            var conditions = ExplorerSearch.Parse(conditioned.Argument(ExploreOperation.SearchArgument));
            Assert.Equal(
                [(ExplorerCondition.AnyOf, (string?)null), (ExplorerCondition.Is, "data.GeoContexts"), (ExplorerCondition.Missing, null)],
                conditions.Filters.Select(f => (f.Condition, f.Nested)));
            Assert.Equal(["WellDB", "WELLDB"], conditions.Filters[0].Values!);
            Assert.Equal("nested(data.GeoContexts, FieldID.keyword)", conditions.Facet!.AggregateBy());
            Assert.Equal(["data.Augmented.WellboreName"], conditions.Columns);

            var types = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/types?partition={named}", new { text = "Wellbore B" });
            Assert.Equal(ExploreOperation.TypesAction, types.Argument(ExploreOperation.ActionArgument));
            var fields = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/fields?partition={named}", new { kind = "osdu:wks:master-data--Wellbore:1.1.0" });
            Assert.Equal((ExploreOperation.FieldsAction, "osdu:wks:master-data--Wellbore:1.1.0"), (fields.Argument(ExploreOperation.ActionArgument), fields.Argument(ExploreOperation.KindArgument)));

            // A record is checked against what the Schema service holds by default, or a repository template's version asked for.
            var validated = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/validate?partition={named}", new { targetId = "dev:master-data--Wellbore:Wellbore-B:", version = 7L });
            Assert.Equal(
                (ExploreOperation.ValidateAction, "dev:master-data--Wellbore:Wellbore-B", "7", (string?)null, (string?)null),
                (validated.Argument(ExploreOperation.ActionArgument), validated.Argument("targetId"), validated.Argument("version"),
                    validated.Argument(ExploreOperation.SchemaArgument), validated.Argument(ExploreOperation.TemplateVersionArgument)));
            var againstTemplate = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/validate?partition={named}", new { targetId = "dev:master-data--Wellbore:Wellbore-B", schema = " Template ", templateVersion = " 9f3c41d07a2b88e1 " });
            Assert.Equal(("template", "9f3c41d07a2b88e1"), (againstTemplate.Argument(ExploreOperation.SchemaArgument), againstTemplate.Argument(ExploreOperation.TemplateVersionArgument)));

            // The explorer judges a mapping's assertions only when the request names one, by its synced id.
            Assert.Null(validated.Argument(ExploreOperation.MappingArgument));
            var mappingId = Guid.NewGuid();
            var asserted = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/validate?partition={named}", new { targetId = "dev:master-data--Wellbore:Wellbore-B", mapping = mappingId });
            Assert.Equal(mappingId.ToString("D"), asserted.Argument(ExploreOperation.MappingArgument));

            // A check of a search reads from the first record whatever page the search was on, up to the most asked.
            var listed = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/validate-list?partition={named}", new
            {
                search = new { text = "Wellbore B", kind = "*:*:master-data--Wellbore:*", offset = 300, limit = 50, facet = new { path = "data.FacilityTypeID" } },
                max = 250,
            });
            Assert.Equal((ExploreOperation.ValidateListAction, "250"), (listed.Argument(ExploreOperation.ActionArgument), listed.Argument(ExploreOperation.MaxArgument)));
            var checkedSearch = ExplorerSearch.Parse(listed.Argument(ExploreOperation.SearchArgument));
            Assert.Equal(("Wellbore B", "*:*:master-data--Wellbore:*", 0, ExplorerSearch.DefaultLimit), (checkedSearch.Text, checkedSearch.Kind, checkedSearch.Offset, checkedSearch.Limit));
            Assert.Null(checkedSearch.Facet);
            var everything = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/validate-list?partition={named}", new { });
            Assert.Equal(ExplorerChecks.MaxRecords.ToString(CultureInfo.InvariantCulture), everything.Argument(ExploreOperation.MaxArgument));
            Assert.Null(everything.Argument(ExploreOperation.MappingArgument));
            var listedAsserted = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/validate-list?partition={named}", new { mapping = mappingId });
            Assert.Equal(mappingId.ToString("D"), listedAsserted.Argument(ExploreOperation.MappingArgument));

            var validate400 = $"/api/v1/delivery/explorer/validate?partition={named}";
            await RefusedAsync(client, token, validate400, new { targetId = "dev:master-data--Wellbore:Wellbore-B", schema = "remote" }, HttpStatusCode.BadRequest, "is not a schema a record is checked against");
            await RefusedAsync(client, token, validate400, new { targetId = "Wellbore B-2 B" }, HttpStatusCode.BadRequest, "is not an OSDU record id");
            await RefusedAsync(client, token, validate400, new { targetId = "dev:master-data--Wellbore:Wellbore-B", schema = "saved" }, HttpStatusCode.BadRequest, "osdu (what the Schema service holds) or template");
            await RefusedAsync(client, token, validate400, new { targetId = "dev:master-data--Wellbore:Wellbore-B", schema = "template", templateVersion = new string('v', 65) }, HttpStatusCode.BadRequest, "template version as the Templates page shows it");
            var list400 = $"/api/v1/delivery/explorer/validate-list?partition={named}";
            await RefusedAsync(client, token, list400, new { max = 1001 }, HttpStatusCode.BadRequest, "A check reads 1 to 1000 records");
            await RefusedAsync(client, token, list400, new { max = 0 }, HttpStatusCode.BadRequest, "A check reads 1 to 1000 records");
            await RefusedAsync(client, token, list400, new { search = new { kind = "osdu:wks" } }, HttpStatusCode.BadRequest, "is not a kind");
            await RefusedAsync(client, token, list400, new { schema = "remote" }, HttpStatusCode.BadRequest, "is not a schema a record is checked against");

            // The types referring to a type are asked of the type a kind names, whatever its version, and the partition's
            // schemas are read again only when asked.
            var referring = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/referenced-by?partition={named}", new { type = " osdu:wks:reference-data--UnitOfMeasure:1.0.0 " });
            Assert.Equal(
                (ExploreOperation.OperationName, named, named),
                (referring.Operation, referring.SourceRef, referring.Argument(DeliveryOperation.PartitionArgument)));
            Assert.Equal(
                (ExploreOperation.ReferencedByAction, "reference-data--UnitOfMeasure", (string?)null),
                (referring.Argument(ExploreOperation.ActionArgument), referring.Argument(ExploreOperation.TypeArgument), referring.Argument(ExploreOperation.RefreshArgument)));
            var refreshed = await RanAsync(operations, client, token, $"/api/v1/delivery/explorer/referenced-by?partition={named}", new { type = "master-data--Wellbore", refresh = true });
            Assert.Equal(("master-data--Wellbore", "true"), (refreshed.Argument(ExploreOperation.TypeArgument), refreshed.Argument(ExploreOperation.RefreshArgument)));
            var referenced400 = $"/api/v1/delivery/explorer/referenced-by?partition={named}";
            await RefusedAsync(client, token, referenced400, new { type = "UnitOfMeasure" }, HttpStatusCode.BadRequest, "is not a type");
            await RefusedAsync(client, token, referenced400, new { type = "*:*:reference-data--*:*" }, HttpStatusCode.BadRequest, "is not a type");
            await RefusedAsync(client, token, referenced400, new { }, HttpStatusCode.BadRequest, "Name the type");

            // A read takes a reference as a document holds it, and a version beside it, in the workbench's partition.
            var read = await RanAsync(operations, client, token, "/api/v1/delivery/explorer/read", new { targetId = $" {named}:master-data--Wellbore:Wellbore-B: ", version = 1712345678901234L }, named);
            Assert.Equal((ExploreOperation.ReadAction, named, named), (read.Argument(ExploreOperation.ActionArgument), read.SourceRef, read.Argument(DeliveryOperation.PartitionArgument)));
            Assert.Equal(($"{named}:master-data--Wellbore:Wellbore-B", "1712345678901234"), (read.Argument("targetId"), read.Argument("version")));

            var search400 = $"/api/v1/delivery/explorer/search?partition={named}";
            await RefusedAsync(client, token, search400, new { kind = "osdu:wks" }, HttpStatusCode.BadRequest, "is not a kind");
            await RefusedAsync(client, token, search400, new { sort = "newest" }, HttpStatusCode.BadRequest, "is not an order");
            await RefusedAsync(client, token, search400, new { sort = "1" }, HttpStatusCode.BadRequest, "is not an order");
            await RefusedAsync(client, token, search400, new { filters = new[] { new { path = "data.Name", index = "fuzzy", value = "x" } } }, HttpStatusCode.BadRequest, "is not how a property is indexed");
            await RefusedAsync(client, token, search400, new { filters = new[] { new { path = "data.Name", index = "text", value = new string('x', 300) } } }, HttpStatusCode.BadRequest, "cannot narrow to data.Name");
            await RefusedAsync(client, token, search400, new { filters = new[] { new { path = "data.Name", condition = "near", value = "x" } } }, HttpStatusCode.BadRequest, "is not a condition");
            await RefusedAsync(client, token, search400, new { filters = new[] { new { path = "kind", index = "keyword", condition = "contains", value = "Well" } } }, HttpStatusCode.BadRequest, "compared whole");
            await RefusedAsync(client, token, search400, new { offset = 9950, limit = 100 }, HttpStatusCode.BadRequest, "first 10,000 records");
            await RefusedAsync(client, token, search400, new { text = "a", mentions = "dev:master-data--Well:1" }, HttpStatusCode.BadRequest, "not both");
            await RefusedAsync(client, token, $"/api/v1/delivery/explorer/fields?partition={named}", new { kind = " " }, HttpStatusCode.BadRequest, "Name the kind");
            await RefusedAsync(client, token, "/api/v1/delivery/explorer/read?partition=" + named, new { targetId = "Wellbore B-2 B" }, HttpStatusCode.BadRequest, "is not an OSDU record id");

            // A partition with no connection is read through nothing: neither one whose values are unset, nor one not registered.
            await RefusedAsync(client, token, $"/api/v1/delivery/explorer/search?partition={bare}", new { text = "x" }, HttpStatusCode.Conflict, "through the platform and client its configuration names");
            await RefusedAsync(client, token, $"/api/v1/delivery/explorer/search?partition={unregistered}", new { text = "x" }, HttpStatusCode.Conflict, "is not registered with the catalog");
            Assert.Empty(operations.Given);

            // Every read was answered in the request: nothing was queued for a node.
            await using var catalog = CatalogDatabase.Create(cs);
            Assert.False(await catalog.ComputeTasks.AnyAsync(t => t.SourceRef == named || t.SourceRef == bare));
        }
        finally
        {
            await TestPartitions.ForgetAsync(cs, named, bare);
        }
    }

    [Fact]
    public async Task A_record_page_checks_a_record_through_its_own_flow_and_a_route_that_keeps_none_in_storage_has_none_to_check()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var named = "p" + suffix;
        var repoId = FlowIdentity.FromName("repo/cp-explorer-" + suffix);
        var ddms = Flow("explorer-a-" + suffix, $"partitions: [{named}]", "ddms", "protocolOptions: { ddmsRoot: /api/os-wellbore-ddms }");
        var dspdm = Flow("explorer-d-" + suffix, $"partitions: [{named}]", "dspdm");
        var now = DateTime.UtcNow;

        await using (var db = CatalogDatabase.Create(cs))
        {
            db.Repos.Add(new CatalogRepo
            {
                Id = repoId, Name = "cp-explorer-" + suffix, RemoteUrl = "https://example/cp-explorer.git",
                RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now,
            });
            foreach (var (name, yaml) in new[] { ddms, dspdm })
            {
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = CatalogIdentity.Pipeline(repoId, name),
                    RepoId = repoId,
                    Name = name,
                    Kind = "delivery",
                    RelativePath = "flows/" + name + ".yaml",
                    ContentHash = new string('0', 64),
                    Yaml = yaml,
                    DefinitionJson = string.Create(CultureInfo.InvariantCulture, $$"""{"name":"{{name}}","flowKind":"delivery"}"""),
                    Active = true,
                    Wave = 0,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
            }

            await db.SaveChangesAsync();
        }

        var operations = new RecordedOperations();
        try
        {
            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithModules(new DeliveryControlPlaneModule())
                .WithSetting("ControlPlane:Worker:Enabled", "false")
                .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
                .WithServices(services => services.AddSingleton(operations.Registry()));
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // A record page checks a record it shows as the explorer checks one, read through the record's own flow and the
            // partition the page names; a bad ask is refused as the explorer's is.
            var onPage = $"/api/v1/delivery/flows/{CatalogIdentity.Pipeline(repoId, ddms.Name):D}/osdu/validate?partition={named}";
            var pageChecked = await RanAsync(operations, client, token, onPage, new { targetId = " dev:master-data--Wellbore:Wellbore-B: ", version = 7L, schema = " Template ", templateVersion = " 9f3c41d07a2b88e1 " });
            Assert.Equal((ExploreOperation.OperationName, ddms.Name, named), (pageChecked.Operation, pageChecked.SourceRef, pageChecked.Argument(DeliveryOperation.PartitionArgument)));
            Assert.Null(pageChecked.Argument(ExploreOperation.ConnectionArgument));
            Assert.Equal(
                (ExploreOperation.ValidateAction, "dev:master-data--Wellbore:Wellbore-B", "7", "template", "9f3c41d07a2b88e1"),
                (pageChecked.Argument(ExploreOperation.ActionArgument), pageChecked.Argument("targetId"), pageChecked.Argument("version"),
                    pageChecked.Argument(ExploreOperation.SchemaArgument), pageChecked.Argument(ExploreOperation.TemplateVersionArgument)));

            // A record page judges the assertions of the record's own flow's mapping, unless the request names another.
            Assert.Equal(
                SqlFlow.Delivery.Catalog.DeliveryCatalogSync.MappingId(repoId, "Wellbore@1.0.0").ToString("D"),
                pageChecked.Argument(ExploreOperation.MappingArgument));
            var mappingId = Guid.NewGuid();
            var pageNamed = await RanAsync(operations, client, token, onPage, new { targetId = "dev:master-data--Wellbore:Wellbore-B", mapping = mappingId });
            Assert.Equal(mappingId.ToString("D"), pageNamed.Argument(ExploreOperation.MappingArgument));
            await RefusedAsync(client, token, onPage, new { targetId = "Wellbore B-2 B" }, HttpStatusCode.BadRequest, "is not an OSDU record id");
            await RefusedAsync(client, token, onPage, new { targetId = "dev:master-data--Wellbore:Wellbore-B", schema = "remote" }, HttpStatusCode.BadRequest, "is not a schema a record is checked against");

            // A flow whose route keeps no record in storage has none to check.
            await RefusedAsync(
                client, token, $"/api/v1/delivery/flows/{CatalogIdentity.Pipeline(repoId, dspdm.Name):D}/osdu/validate?partition={named}",
                new { targetId = "dev:master-data--Wellbore:Wellbore-B" }, HttpStatusCode.Conflict, "keeps no record in OSDU's storage service");
            Assert.Empty(operations.Given);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var names = new[] { ddms.Name, dspdm.Name };
            await db.ComputeTasks.Where(t => names.Contains(t.SourceRef)).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>The operation's own answer is the response, and the payload it was run with is what it was given.</summary>
    private static async Task<ComputeTaskPayload> RanAsync(RecordedOperations operations, HttpClient client, string token, string path, object? body, string? workbench = null)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, path, body, workbench);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"POST {path} answered {(int)response.StatusCode}: {text}");
        var payload = operations.Last();
        Assert.Equal(payload.Operation, JsonDocument.Parse(text).RootElement.GetProperty("ran").GetString());
        return payload;
    }

    /// <summary>A delivery flow of one interface reading a table no test opens, by the route and partition settings given.</summary>
    private static (string Name, string Yaml) Flow(string name, string? partitions, string protocol, string? targetExtra = null)
        => (name, $$"""
            flowType: delivery
            name: {{name}}
            {{partitions ?? string.Empty}}
            source:
              connection: ${env:OSDU_DATA_DB}
              record: { object: OsduData.arc.Wellbore, key: [facility_name] }
              work: ../.work/explorer
            render:
              mapping: Wellbore@1.0.0
            target:
              endpoint: ${env:OSDU_URL}
              protocol: {{protocol}}
              {{targetExtra ?? string.Empty}}
            """);

    private static async Task<JsonElement> ConnectionAsync(HttpClient client, string token, string query, string? workbench)
    {
        using var response = await SendAsync(client, token, HttpMethod.Get, "/api/v1/delivery/explorer/connection" + query, null, workbench);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET connection{query} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>The text <paramref name="name"/> holds in <paramref name="element"/>; null when it holds none or is left out.</summary>
    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [Fact]
    public async Task The_queries_of_an_element_are_read_from_a_repositorys_template_file_and_nothing_is_asked_of_OSDU()
    {
        var cs = OsduTestServer.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.HoldTemplateRepositoryAsync(cs, DateTime.UtcNow);
        var operations = new RecordedOperations();
        await using var factory = new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
            .WithServices(services => services.AddSingleton(operations.Registry()));
        using var client = factory.CreateClient();
        var token = await TokenAsync(client);

        // A text of a wellbore: its whole value by its keyword, its words, and whether a record holds one.
        using (var answered = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/explorer/element-queries", new
        {
            kind = SampleEstate.WellboreTemplateKind,
            path = "data.FacilityName",
            value = "WB B/2-9 S",
        }))
        {
            var text = await answered.Content.ReadAsStringAsync();
            Assert.True(answered.StatusCode == HttpStatusCode.OK, text);
            var answer = JsonDocument.Parse(text).RootElement;
            var queries = answer.GetProperty("queries").EnumerateArray().ToDictionary(q => q.GetProperty("purpose").GetString()!, q => q.GetProperty("query").GetString());
            Assert.Equal("data.FacilityName.keyword:\"WB B/2-9 S\"", queries["exact"]);
            Assert.Equal("_exists_:data.FacilityName", queries["exists"]);
            Assert.Equal(SampleEstate.WellboreTemplateKind, answer.GetProperty("template").GetProperty("kind").GetString());
            Assert.Equal("text", answer.GetProperty("field").GetProperty("index").GetString());
        }

        // A value of a nested list, by its place in the record, is asked inside nested(...).
        using (var nested = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/explorer/element-queries", new
        {
            kind = SampleEstate.WellboreTemplateKind,
            path = "data.NameAliases[1].AliasName",
            value = "WB B/2-9 S",
        }))
        {
            var answer = JsonDocument.Parse(await nested.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(
                "nested(data.NameAliases, (AliasName.keyword:\"WB B/2-9 S\"))",
                answer.GetProperty("queries").EnumerateArray().Single(q => q.GetProperty("purpose").GetString() == "exact").GetProperty("query").GetString());
        }

        // An item of a nested list, by the values it holds: all of them together inside one nested query.
        using (var item = await SendAsync(client, token, HttpMethod.Post, "/api/v1/delivery/explorer/element-queries", new
        {
            kind = SampleEstate.WellboreTemplateKind,
            path = "data.NameAliases[0]",
            section = true,
            values = new object[]
            {
                new { path = "data.NameAliases[0].AliasName", value = "WB B/2-9 S" },
                new { path = "data.NameAliases[0].AliasNameTypeID", value = "dev:reference-data--AliasNameType:Borehole:" },
            },
        }))
        {
            var answer = JsonDocument.Parse(await item.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(
                "nested(data.NameAliases, (AliasName.keyword:\"WB B/2-9 S\" AND AliasNameTypeID.keyword:\"dev:reference-data--AliasNameType:Borehole:\"))",
                answer.GetProperty("queries")[0].GetProperty("query").GetString());
        }

        await RefusedAsync(client, token, "/api/v1/delivery/explorer/element-queries", new { kind = "osdu:wks:WellLog", path = "data.X" }, HttpStatusCode.BadRequest, "osdu:wks:WellLog");
        await RefusedAsync(client, token, "/api/v1/delivery/explorer/element-queries", new { kind = SampleEstate.WellboreTemplateKind }, HttpStatusCode.BadRequest, "Name the element's path");
        await RefusedAsync(client, token, "/api/v1/delivery/explorer/element-queries", new { kind = SampleEstate.WellboreTemplateKind, path = "data.X", value = new { a = 1 } }, HttpStatusCode.BadRequest, "is a section");
        Assert.Empty(operations.Given);
    }

    private static async Task RefusedAsync(HttpClient client, string token, string path, object body, HttpStatusCode status, string why)
    {
        using var response = await SendAsync(client, token, HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"POST {path} answered {(int)response.StatusCode}: {text}");
        Assert.Contains(why, text, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string path, object? body = null, string? workbench = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (workbench is not null)
        {
            request.Headers.Add("X-Osdu-Partition", workbench);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
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
