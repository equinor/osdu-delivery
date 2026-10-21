using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.ControlPlane;
using SqlFlow.Delivery.ControlPlane.Api;
using SqlFlow.Delivery.Data;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Tests;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Templates and the mapping builder through the API (osdu/docs/reference/concepts/templates.md; the builder:
/// osdu/docs/reference/concepts/gui.md, Mapping builder): saving a template version, which takes a signed-in caller,
/// reading it as variables and as its schema, the delete a pinned version refuses, and the builder's repository
/// listing, draft, parse and check against a repository's cache as the catalog carries it, and browsing the OSDU data
/// definitions, served here by a stand-in for the repository's API. Gated on a reachable catalog database.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerSuite.Name)]
public sealed class DeliveryTemplateApiTests
{
    private const string WellLogKind = "osdu:wks:work-product-component--WellLog:1.4.0";
    private const string WellLogVersion = "26a3c3441882db4f";
    private const string WellLogFile = "osdu_wks_work-product-component--WellLog_1.4.0.json";
    private const string WellboreKind = "osdu:wks:master-data--Wellbore:1.3.0";
    private const string WellboreVersion = "58d6bdbd9d066a06";
    private const string WellboreFile = "osdu_wks_master-data--Wellbore_1.3.0.json";
    private const string ReferenceVersion = "20260908T212727Z";

    /// <summary>The source connection of the seeded flow, as a flow declares one: a reference, never a connection string.</summary>
    private const string SourceConnectionReference = "${env:OSDU_DATA_DB}";

    /// <summary>A reference nothing sets, so the builder's repository listing cannot resolve it.</summary>
    private const string UnsetReference = "${env:ODTEST_BUILDER_UNSET_LEGAL_TAG}";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The samples directory beside the binaries: the source folders, and the bundled schemas beside them.</summary>
    private static string SampleRoot => Path.Combine(AppContext.BaseDirectory, "samples");

    /// <summary>The sample source's own folder, which is what a repository sync would read.</summary>
    private static string SampleSource => Path.Combine(SampleRoot, SampleEstate.SourceFolder);

    [Fact]
    public async Task A_template_is_saved_once_by_a_signed_in_caller_and_read_as_variables_and_as_its_schema()
    {
        var cs = OsduTestServer.Require();
        await ProvisionAsync(cs);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var reader = await TokenAsync(client, "read");
        var author = await TokenAsync(client, "read", "author");

        // Saving is an author action, which every signed-in user may take and no anonymous caller may.
        using (var anonymous = await client.PostAsJsonAsync(new Uri("/api/v1/delivery/templates", UriKind.Relative), SaveBody(WellboreKind, WellboreFile), Web))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var first = await ReadAsync<DeliveryTemplateSavedDto>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/templates", SaveBody(WellboreKind, WellboreFile)));
        Assert.Equal(WellboreVersion, first.Template.Version);
        Assert.Contains(first.Outcome, new[] { "created", "unchanged" });
        var again = await ReadAsync<DeliveryTemplateSavedDto>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/templates", SaveBody(WellboreKind, WellboreFile)));
        Assert.Equal("unchanged", again.Outcome);

        var listed = await ReadAsync<List<DeliveryTemplateDto>>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/templates"));
        Assert.Contains(listed, t => t.Kind == WellboreKind && t.Version == WellboreVersion);

        var detail = await ReadAsync<DeliveryTemplateDetailDto>(await SendAsync(client, reader, HttpMethod.Get, DetailUrl(WellboreKind, WellboreVersion)));
        Assert.NotNull(detail.Saved);
        Assert.Contains(detail.Variables, v => v.Path == "osdu.data.FacilityName" && v.Role == "Mapping");
        Assert.Contains(detail.Variables, v => v.Path == "osdu.id" && v.Role == "Engine");
        Assert.Contains(detail.Variables, v => v.Path == "osdu.data.NameAliases" && v.Shape == "GroupList");

        using (var schema = await SendAsync(client, reader, HttpMethod.Get, $"/api/v1/delivery/templates/schema?kind={Uri.EscapeDataString(WellboreKind)}&version={WellboreVersion}"))
        {
            Assert.Equal(HttpStatusCode.OK, schema.StatusCode);
            Assert.Equal("application/json", schema.Content.Headers.ContentType?.MediaType);
            var text = await schema.Content.ReadAsStringAsync();
            Assert.NotNull(JsonNode.Parse(text)?["properties"]?["data"]);

            // The answer is a file to save, named as the samples name theirs, which imports again as this same version.
            Assert.Equal("attachment", schema.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal(WellboreFile, schema.Content.Headers.ContentDisposition?.FileNameStar ?? schema.Content.Headers.ContentDisposition?.FileName);
            Assert.Equal(WellboreVersion, TemplateSources.FromBundledJson(text, WellboreKind, DateTimeOffset.UnixEpoch, WellboreFile).Version);
        }

        using var notRecord = await SendAsync(client, reader, HttpMethod.Post, "/api/v1/delivery/templates/preview", new { kind = WellboreKind, schema = new { type = "object" } });
        Assert.Equal(HttpStatusCode.BadRequest, notRecord.StatusCode);
        Assert.Contains("declares no 'data' property", await notRecord.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_saved_version_is_deleted_whatever_a_synced_mapping_names_since_no_repository_reads_the_library()
    {
        var cs = OsduTestServer.Require();
        await ProvisionAsync(cs);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var author = await TokenAsync(client, "read", "author");

        // A kind of its own, so deleting it cannot pull a template from under any other test.
        var kind = $"test:wks:master-data--ApiThing{Guid.NewGuid():N}:1.0.0";
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"kind":{"type":"string"},"data":{"type":"object","properties":{"Name":{"type":"string"}}}}}""").RootElement.Clone();
        var saved = await ReadAsync<DeliveryTemplateSavedDto>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/templates", new { kind, schema, origin = "test" }));
        Assert.Equal("created", saved.Outcome);
        var version = saved.Template.Version;

        var mappingId = Guid.NewGuid();
        await using (var db = SampleEstate.Context(cs))
        {
            db.DeliveryMappings.Add(new DeliveryMapping
            {
                Id = mappingId,
                RepoId = Guid.NewGuid(),
                Reference = "ApiThing@1.0.0",
                Name = "ApiThing",
                Version = "1.0.0",
                Kind = kind,
                TemplateVersion = version,
                RelativePath = "mappings/ApiThing@1.0.0.yaml",
                ContentHash = new string('0', 64),
                Yaml = "documentType: mapping",
                SummaryJson = "{}",
                Status = "valid",
                FirstSeenUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // A synced mapping that names the version reads it from its repository's template folder, never from here, so the
        // saved version is deleted and the mapping's row is left as it is.
        var deleteUrl = $"/api/v1/delivery/templates?kind={Uri.EscapeDataString(kind)}&version={version}";
        try
        {
            using (var deleted = await SendAsync(client, author, HttpMethod.Delete, deleteUrl))
            {
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            }

            await using (var db = SampleEstate.Context(cs))
            {
                Assert.True(await db.DeliveryMappings.AnyAsync(m => m.Id == mappingId && m.TemplateVersion == version));
            }

            using (var gone = await SendAsync(client, author, HttpMethod.Get, DetailUrl(kind, version)))
            {
                Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
            }

            // Deleting it again finds nothing to delete.
            using var again = await SendAsync(client, author, HttpMethod.Delete, deleteUrl);
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }
        finally
        {
            await using var db = SampleEstate.Context(cs);
            await db.DeliveryMappings.Where(m => m.Id == mappingId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task A_template_a_repositorys_folder_holds_settles_its_documents_from_the_syncs_copy_and_the_library_is_left_alone()
    {
        var cs = OsduTestServer.Require();
        await ProvisionAsync(cs);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var author = await TokenAsync(client, "read", "author");

        // A repository whose template folder holds a version of the test kind no other test makes, and a mapping that names
        // the kind alone, synced as a control plane syncs it.
        var kind = TestSchema.Kind;
        var json = TemplateFolderFixtures.ThingFile($"api-{Guid.NewGuid():N}");
        var version = TemplateFolderFixtures.VersionOf(kind, json);
        var repoId = Guid.NewGuid();
        var root = Samples.NewTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        Directory.CreateDirectory(Path.Combine(root, "mappings"));
        await File.WriteAllTextAsync(Path.Combine(root, "templates", "thing.json"), json);
        await File.WriteAllTextAsync(Path.Combine(root, "mappings", "Thing@1.0.0.yaml"), TemplateFolderFixtures.KindAloneMapping());
        try
        {
            await using (var db = SampleEstate.Context(cs))
            {
                await new DeliveryCatalogSync(new DeliveryDocumentLoader()).ReconcileAsync(db, repoId, root, DateTime.UtcNow, new List<string>(), CancellationToken.None);
            }

            // The sync keeps a copy of the file with its row, and saves nothing in the library.
            var listed = await ReadAsync<List<DeliveryTemplateDto>>(await SendAsync(client, author, HttpMethod.Get, "/api/v1/delivery/templates"));
            Assert.DoesNotContain(listed, t => t.Kind == kind && t.Version == version);
            await using (var db = SampleEstate.Context(cs))
            {
                var row = await db.DeliveryRepositoryTemplates.AsNoTracking().SingleAsync(t => t.RepoId == repoId);
                Assert.Equal(version, TemplateFolderFixtures.VersionOf(kind, row.SchemaJson!));
            }

            // A document of the repository settles to it, as its run would; a version no folder on its path holds is said
            // with where it would go.
            var path = Uri.EscapeDataString("mappings/Thing@1.0.0.yaml");
            var resolveUrl = $"/api/v1/delivery/templates/resolve?repoId={repoId}&path={path}&kind={Uri.EscapeDataString(kind)}";
            var found = await ReadAsync<DeliveryTemplateResolutionDto>(await SendAsync(client, author, HttpMethod.Get, resolveUrl));
            Assert.Equal((true, (string?)version, (string?)"templates/thing.json"), (found.Found, found.Version, found.File));
            var missing = await ReadAsync<DeliveryTemplateResolutionDto>(await SendAsync(client, author, HttpMethod.Get, resolveUrl + "&version=0123456789abcdef"));
            Assert.False(missing.Found);
            Assert.StartsWith($"no template folder on the path of mappings/Thing@1.0.0.yaml holds template {kind} version 0123456789abcdef.", missing.Problem, StringComparison.Ordinal);
            Assert.Equal("templates/test_wks_work-product-component--Thing_1.0.0.json", missing.SuggestedPath);
            using (var unnamed = await SendAsync(client, author, HttpMethod.Get, "/api/v1/delivery/templates/resolve?repoId=" + repoId))
            {
                Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
            }

            // The mapping views read the mapping as a document of its repository; without the repository its version is unknown.
            var coverage = await ReadAsync<DeliveryMappingCoverageResult>(await SendAsync(
                client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/coverage",
                new { yaml = TemplateFolderFixtures.KindAloneMapping(), path = "mappings/Thing@1.0.0.yaml", repoId }));
            Assert.Equal((kind, version), (coverage.Kind, coverage.Version));
            Assert.NotEmpty(coverage.Variables);
            var alone = await ReadAsync<DeliveryMappingCoverageResult>(await SendAsync(
                client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/coverage",
                new { yaml = TemplateFolderFixtures.KindAloneMapping(), path = "mappings/Thing@1.0.0.yaml" }));
            Assert.Contains(alone.Issues, i => i.Message.Contains("without a version", StringComparison.Ordinal));

            // The same schema saved in the library and deleted again changes nothing for the repository, whose documents still
            // settle from its own copy.
            using (var schemaJson = JsonDocument.Parse(json))
            {
                var saved = await ReadAsync<DeliveryTemplateSavedDto>(await SendAsync(
                    client, author, HttpMethod.Post, "/api/v1/delivery/templates", new { kind, schema = schemaJson.RootElement.Clone(), origin = "test" }));
                Assert.Equal(version, saved.Template.Version);
            }

            using (var deleted = await SendAsync(client, author, HttpMethod.Delete, $"/api/v1/delivery/templates?kind={Uri.EscapeDataString(kind)}&version={version}"))
            {
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            }

            var still = await ReadAsync<DeliveryMappingCoverageResult>(await SendAsync(
                client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/coverage",
                new { yaml = TemplateFolderFixtures.KindAloneMapping(), path = "mappings/Thing@1.0.0.yaml", repoId }));
            Assert.Equal((kind, version), (still.Kind, still.Version));
            Assert.NotEmpty(still.Variables);

            // Without the copy (a row recorded before copies were kept), the page says to sync the repository again.
            await using (var db = SampleEstate.Context(cs))
            {
                await db.DeliveryRepositoryTemplates.Where(t => t.RepoId == repoId).ExecuteUpdateAsync(set => set.SetProperty(t => t.SchemaJson, (string?)null));
            }

            var uncopied = await ReadAsync<DeliveryMappingCoverageResult>(await SendAsync(
                client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/coverage",
                new { yaml = TemplateFolderFixtures.KindAloneMapping(), path = "mappings/Thing@1.0.0.yaml", repoId }));
            Assert.Contains(uncopied.Issues, i => i.Message.Contains("the copy the sync keeps of it is missing; sync the repository again", StringComparison.Ordinal));
        }
        finally
        {
            await using (var db = SampleEstate.Context(cs))
            {
                await db.DeliveryRepositoryTemplates.Where(t => t.RepoId == repoId).ExecuteDeleteAsync();
                await db.DeliveryMappings.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
                await db.DeliveryTemplates.Where(t => t.Kind == kind && t.Version == version).ExecuteDeleteAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_builder_drafts_from_a_cache_and_checks_what_it_writes()
    {
        var cs = OsduTestServer.Require();
        await ProvisionAsync(cs);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var author = await TokenAsync(client, "read", "operate", "author");
        foreach (var (kind, file) in new[] { (WellLogKind, WellLogFile), (WellboreKind, WellboreFile) })
        {
            using var response = await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/templates", SaveBody(kind, file));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_tpl_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var flowName = "welllog-tpl-" + suffix;
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var sourceId = Guid.NewGuid();
        var scope = "cp-tpl-" + suffix;
        var cacheFlowName = "cp-cache-" + suffix;
        await SeedRepositoryAsync(cs, repoName, repoId, sourceId, flowName, pipelineId, scope, cacheFlowName);
        var config = new DeliveryConfigStore(() => SampleEstate.Context(cs));
        try
        {
            await using (var catalog = CatalogDatabase.Create(cs))
            {
                await config.SetAsync(repoId, null, "OSDU_ACL_OWNER", "owners@" + suffix + ".example", null, "tests", DateTime.UtcNow, catalog);
            }

            var repos = await ReadAsync<List<DeliveryBuilderRepoDto>>(await SendAsync(client, author, HttpMethod.Get, "/api/v1/delivery/mapping-builder/repos"));
            var repo = Assert.Single(repos, r => r.RepoId == repoId);
            Assert.Equal(sourceId, repo.SourceId);
            var flow = Assert.Single(repo.Flows);
            Assert.Equal("WellLog@1.4.0", flow.Mapping);
            // The flow reads the cache of the partition it delivers to.
            Assert.Equal(scope, flow.CacheScope);

            // The check renders with what a run would: the values the flow writes out, and for the aclOwner it leaves out the
            // kind's own ${env:OSDU_ACL_OWNER}, resolved from the repository's central configuration as a run resolves it.
            Assert.Equal("dev", flow.Parameters["dataPartition"]);
            Assert.Equal("data.welldb.viewers@dev.dataservices.energy", flow.Parameters["aclViewer"]);
            Assert.Equal("owners@" + suffix + ".example", flow.Parameters["aclOwner"]);
            // A reference the control plane cannot resolve leaves the value to the author, and the listing says which it is.
            Assert.False(flow.Parameters.ContainsKey("legalTag"));
            Assert.Equal(
                new Dictionary<string, string> { ["aclOwner"] = "${env:OSDU_ACL_OWNER}", ["legalTag"] = UnsetReference },
                flow.ParameterReferences);

            var caches = await ReadAsync<List<DeliveryBuilderCacheDto>>(await SendAsync(client, author, HttpMethod.Get, "/api/v1/delivery/mapping-builder/caches"));
            var cache = Assert.Single(caches, c => c.Scope == scope);
            Assert.Equal([cacheFlowName, cacheFlowName + "-lookups"], cache.Flows);
            Assert.Equal(ReferenceVersion, cache.CurrentVersion);
            Assert.Equal(["Code", "Name"], Assert.Single(cache.Types, c => c.Name == "VerticalMeasurementType").Fields);
            Assert.Null(Assert.Single(cache.Types, c => c.Name == "VerticalMeasurementType").Key);

            // A lookup table names its key, so the builder can say what a replace reading it matches on by default.
            var units = Assert.Single(cache.Types, c => c.Name == "UnitAlias");
            Assert.Equal(("lookup--UnitAlias", "source_unit"), (units.EntityType, units.Key));
            Assert.Equal(["osdu_unit"], units.Fields);
            Assert.Equal("mnemonic", Assert.Single(cache.Types, c => c.Name == "CurveDictionary").Key);

            // Wellbores are searched for on the platform, so no cache holds them.
            Assert.DoesNotContain(cache.Types, c => c.Name == "Wellbore");

            // The cache page reads the same cache: the flow and file that fill it, its types with what the current version holds
            // of each, and the version with the flow that wrote it and who captured it.
            var listed = await ReadAsync<List<DeliveryCacheDto>>(await SendAsync(client, author, HttpMethod.Get, "/api/v1/delivery/caches?repoId=" + repoId));
            var described = Assert.Single(listed);
            Assert.Equal(scope, described.Scope);
            Assert.Equal([cacheFlowName, cacheFlowName + "-lookups"], described.Flows.Select(f => f.Name).Order(StringComparer.Ordinal));
            var filler = Assert.Single(described.Flows, f => f.Name == cacheFlowName);
            Assert.Equal("cache/" + cacheFlowName + ".yaml", filler.RelativePath);
            Assert.Equal(ReferenceVersion, described.Current!.Version);
            Assert.Equal("tests", described.Current.CapturedBy);
            Assert.Equal(cacheFlowName, described.Current.Flow);
            Assert.Equal(1, described.Versions);
            Assert.Equal(1, Assert.Single(described.Types, t => t.Name == "VerticalMeasurementType").Items);
            var measurementTypes = await ReadAsync<PagedResult<DeliveryCachedItemDto>>(await SendAsync(client, author, HttpMethod.Get, $"/api/v1/delivery/cache/items?scope={scope}&type=VerticalMeasurementType"));
            Assert.Equal(1, measurementTypes.Total);
            Assert.All(measurementTypes.Items, item => Assert.Equal(ReferenceVersion, item.Version));
            var history = await ReadAsync<List<DeliveryCacheHistoryEntryDto>>(await SendAsync(client, author, HttpMethod.Get, $"/api/v1/delivery/cache/history?scope={scope}"));
            var only = Assert.Single(history);
            Assert.Null(only.Before);
            using (var unnamed = await SendAsync(client, author, HttpMethod.Get, "/api/v1/delivery/cache/items"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, unnamed.StatusCode);
            }

            var detail = await ReadAsync<DeliveryTemplateDetailDto>(await SendAsync(client, author, HttpMethod.Get, DetailUrl(WellLogKind, WellLogVersion) + "&scope=" + scope));
            Assert.Equal(["UnitOfMeasure"], Assert.Single(detail.Variables, v => v.Path == "osdu.data.Curves[].CurveUnit").CacheTypes);

            var draft = await ReadAsync<MappingDraft>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/draft", new
            {
                scope,kind = WellLogKind, version = WellLogVersion, name = "WellLog", mappingVersion = "9.0.0", system = "welldb",
            }));
            var prefilled = Assert.Single(draft.Entries, e => e.Target == "osdu.data.VerticalMeasurement.VerticalMeasurementTypeID");
            Assert.True(prefilled.Prefilled);
            Assert.Equal(MappingDraftInput.Cache, prefilled.Input);
            Assert.Equal("Code", Assert.Single(prefilled.FindBy).Field);

            // Nothing in the cache answers the wellbore reference, which the sample mapping searches for instead.
            Assert.DoesNotContain(draft.Entries, e => e.Target == "osdu.data.WellboreID");

            // The sample mapping, opened in the builder and written back, passes the check against the seeded cache.
            var sample = await File.ReadAllTextAsync(Path.Combine(SampleSource, "mappings", "WellLog@1.4.0.yaml"));
            var parsed = await ReadAsync<DeliveryMappingParseResult>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/parse", new { yaml = sample, path = "mappings/WellLog@1.4.0.yaml" }));
            Assert.Empty(parsed.Issues);
            Assert.NotNull(parsed.Draft);

            // A curve's unit is a coalesce, opened as its alternatives in order: the unit table, the partition's own units, and
            // the table's translation unverified. A replace reading a cached table opens with the table, and nothing the table
            // settles: UnitAlias names its key and its one field, so the draft leaves both out, and a spelling it does not
            // list is kept as it is. The ref after it writes the translated unit as the reference to that unit.
            var curveUnit = Assert.Single(parsed.Draft.Entries, e => e.Target == "osdu.data.Curves[].CurveUnit");
            Assert.Equal(MappingDraftInput.Coalesce, curveUnit.Input);
            Assert.Equal([MappingDraftInput.Dataset, MappingDraftInput.Cache, MappingDraftInput.Dataset], curveUnit.Alternatives.Select(a => a.Input));
            Assert.Equal([false, false, true], curveUnit.Alternatives.Select(a => a.Unverified));
            var unitModifiers = curveUnit.Alternatives[0].Modifiers;
            Assert.Equal(["replace", "ref"], unitModifiers.Select(m => m.Kind));
            var unit = unitModifiers[0];
            Assert.Equal(("UnitAlias", (string?)null, (string?)null, "keep"), (unit.Table, unit.Match, unit.Field, unit.OtherwiseKind));
            Assert.Null(unit.Replacements);

            // An id built from a cached table opens as its template, whole: the curve's family is the code the curve
            // dictionary gives its mnemonic.
            var family = Assert.Single(Assert.Single(parsed.Draft.Entries, e => e.Target == "osdu.data.Curves[].LogCurveFamilyID").Modifiers);
            Assert.Equal(
                ("id", "{$param.dataPartition}:reference-data--LogCurveFamily:{$cache.CurveDictionary.log_curve_family_id}:"),
                (family.Kind, family.Text));
            // The check reads dev's cache as the delivery suites keep it: the sample reference data under dev's ids.
            await Samples.ImportSampleCacheAsync(new OsduCacheStore(() => SampleEstate.Context(cs)));
            var parameters = new Dictionary<string, string>
            {
                ["dataPartition"] = "dev",
                ["aclOwner"] = "data.welldb.owners@dev.dataservices.energy",
                ["aclViewer"] = "data.welldb.viewers@dev.dataservices.energy",
                ["legalTag"] = "dev-osdu-default-legal",
            };
            var checkedSample = await ReadAsync<DeliveryMappingComposeResult>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/compose", new { scope,draft = parsed.Draft, parameters }));
            Assert.True(checkedSample.Valid, string.Join(Environment.NewLine, checkedSample.Issues.Select(i => i.Message)));
            Assert.Contains("    WellboreID:\n      $search: Wellbore\n", checkedSample.Yaml.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Contains("          $coalesce:\n            - $from: curve_unit\n              $modifiers:\n                - replace: $cache.UnitAlias\n                - ref\n", checkedSample.Yaml.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Contains("            - id: \"{$param.dataPartition}:reference-data--LogCurveFamily:{$cache.CurveDictionary.log_curve_family_id}:\"\n", checkedSample.Yaml.ReplaceLineEndings("\n"), StringComparison.Ordinal);

            // A cached table the partition's cache does not hold is refused by the check, naming what it does hold.
            var missingTable = parsed.Draft with
            {
                Entries = parsed.Draft.Entries.Select(e => e.Target == "osdu.data.Curves[].CurveUnit"
                    ? e with { Alternatives = [e.Alternatives[0] with { Modifiers = [e.Alternatives[0].Modifiers[0] with { Table = "NoSuchUnits" }] }, .. e.Alternatives.Skip(1)] }
                    : e).ToList(),
            };
            var missing = await ReadAsync<DeliveryMappingComposeResult>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/compose", new { scope, draft = missingTable, parameters }));
            Assert.False(missing.Valid);
            Assert.Contains(missing.Issues, i => i.Severity == "error" && i.Message.Contains("replace reads $cache.NoSuchUnits, which cache version", StringComparison.Ordinal));

            // The same document drawn as the shape of the records it renders: the identity and the envelope as a render
            // writes them, and a placeholder wherever a value comes from a row or the cache.
            var shape = await ReadAsync<DeliveryMappingShapeResult>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/shape", new { yaml = sample, path = "mappings/WellLog@1.4.0.yaml", parameters }));
            Assert.Empty(shape.Issues);
            Assert.NotNull(shape.Record);
            Assert.Equal("dev:work-product-component--WellLog:<delivery key from welldb, dataset.source_project, dataset.log_id>", shape.Record["id"]!.GetValue<string>());
            Assert.Equal("<string from dataset.curves.curve_id>", shape.Record["data"]!["Curves"]![0]!["CurveID"]!.GetValue<string>());
            Assert.Equal("dev", Assert.Single(shape.Parameters, p => p.Name == "dataPartition").Value);

            // The same document measured against its template: every variable a mapping may fill, with how the document
            // reaches it, and nothing required left empty. Nothing is rendered and no cache is read.
            var coverage = await ReadAsync<DeliveryMappingCoverageResult>(
                await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/coverage", new { yaml = sample, path = "mappings/WellLog@1.4.0.yaml" }));
            Assert.Equal(WellLogKind, coverage.Kind);
            Assert.Equal(WellLogVersion, coverage.Version);
            Assert.DoesNotContain(coverage.Issues, i => i.Severity == "error");
            Assert.Equal(CoverageState.Always, Assert.Single(coverage.Variables, v => v.Target == "osdu.data.WellboreID").State);
            Assert.True(Assert.Single(coverage.Variables, v => v.Target == "osdu.acl").State == CoverageState.Always);
            Assert.False(Assert.Single(coverage.Variables, v => v.Target == "osdu.acl").Direct);
            Assert.Contains(coverage.Variables, v => v.State == CoverageState.Empty);
            Assert.DoesNotContain(coverage.Variables, v => v.Target == "osdu.id");

            // A document whose template is not saved says so, instead of answering with an empty template.
            var unsaved = await ReadAsync<DeliveryMappingCoverageResult>(await SendAsync(
                client,
                author,
                HttpMethod.Post,
                "/api/v1/delivery/mapping-builder/coverage",
                new { yaml = sample.Replace($"version: {WellLogVersion}", "version: 00000000deadbeef", StringComparison.Ordinal), path = "mappings/WellLog@1.4.0.yaml" }));
            Assert.Empty(unsaved.Variables);
            Assert.Contains(unsaved.Issues, i => i.Severity == "error"
                && i.Message.StartsWith("The mapping's template cannot be read: template osdu:wks:work-product-component--WellLog:1.4.0 version 00000000deadbeef is not saved", StringComparison.Ordinal));

            // A wellbore reference read from the unit cache is written, and refused by the check against the template. The
            // entry no longer searches for the wellbore, so the search it read goes with it.
            var wrong = parsed.Draft with
            {
                Searches = [],
                Entries = parsed.Draft.Entries.Select(e => e.Target == "osdu.data.WellboreID"
                    ? e with { Input = MappingDraftInput.Cache, CacheType = "UnitOfMeasure", CacheField = "id", FindBy = [new MappingDraftFind("Code", "wellbore_uwi", null)] }
                    : e).ToList(),
            };
            var refused = await ReadAsync<DeliveryMappingComposeResult>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/compose", new { scope,draft = wrong, parameters }));
            Assert.False(refused.Valid);
            Assert.Contains(refused.Issues, i => i.Severity == "error" && i.Message.Contains("writes the id of a cached UnitOfMeasure", StringComparison.Ordinal));

            // Without a partition named there is no cache to check the cache entries against, and the check says so.
            var noCache = await ReadAsync<DeliveryMappingComposeResult>(await SendAsync(client, author, HttpMethod.Post, "/api/v1/delivery/mapping-builder/compose", new { scope = (string?)null, draft = parsed.Draft, parameters }));
            Assert.False(noCache.Valid);
            Assert.Contains(noCache.Issues, i => i.Message.Contains("replace reads $cache.UnitAlias, which cache version 'none' does not hold", StringComparison.Ordinal));
        }
        finally
        {
            await config.RemoveAsync(repoId, null, "OSDU_ACL_OWNER");
            await CleanupAsync(cs, repoId, flowName, scope);
        }
    }

    [Fact]
    public async Task The_cache_page_lists_one_cache_per_partition_with_every_cache_flow_that_fills_it()
    {
        var cs = OsduTestServer.Require();
        await ProvisionAsync(cs);
        await using var factory = Factory(cs);
        using var client = factory.CreateClient();
        var reader = await TokenAsync(client, "read");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var shared = "cp-shared-" + suffix;
        var single = "cp-single-" + suffix;
        var (projectA, projectB, projectC) = ("cp-a-" + suffix, "cp-b-" + suffix, "cp-c-" + suffix);
        var repoName = "cp_caches_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var root = Path.Combine(Path.GetTempPath(), "sqlflow_cp_caches_" + suffix);
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        await File.WriteAllTextAsync(Path.Combine(root, "cache", "a.yaml"), CacheFlowYaml(projectA, shared, """
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                fields: [data.FacilityName]
            """));
        await File.WriteAllTextAsync(Path.Combine(root, "cache", "b.yaml"), CacheFlowYaml(projectB, shared, """
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                onChange: approve
                fields:
                  - data.FacilityName
                  - path: data.NameAlias.AliasName
                    as: Alias
              - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
                fields: [data.Code]
            """));
        await File.WriteAllTextAsync(Path.Combine(root, "cache", "c.yaml"), CacheFlowYaml(projectC, single, """
              - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
                fields: [data.Code]
            """));

        try
        {
            var warnings = new List<string>();
            var now = DateTime.UtcNow;
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = repoName, RemoteUrl = "https://example/" + repoName + ".git", RootPath = root, FirstSeenUtc = now, LastSyncUtc = now });
                await db.SaveChangesAsync();
            }

            await using (var osdu = SampleEstate.Context(cs))
            {
                // The one write the sync extension makes: the repository sync opens exactly this context on the
                // catalog's connection and transaction, and this reconcile is its body.
                var synced = await new DeliveryCatalogSync(new DeliveryDocumentLoader())
                    .ReconcileAsync(osdu, repoId, root, now, warnings, CancellationToken.None);
                Assert.Equal((4, 0), (synced.Added, synced.Invalid));
            }

            Assert.Empty(warnings);

            var listed = await ReadAsync<List<DeliveryCacheDto>>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/caches?repoId=" + repoId));
            Assert.Equal(2, listed.Count);

            // Two flows fill the shared partition's one cache: each with its file and the types it declares, and the types as
            // they declare them together.
            var partition = Assert.Single(listed, c => c.Scope == shared);
            Assert.Equal([projectA, projectB], partition.Flows.Select(f => f.Name));
            Assert.All(partition.Flows, f => Assert.Equal((repoId, repoName, "https://osdu.example.test"), (f.RepoId, f.RepoName, f.Endpoint)));
            Assert.Equal(["cache/a.yaml", "cache/b.yaml"], partition.Flows.Select(f => f.RelativePath));
            Assert.Equal(["UnitOfMeasure", "Wellbore"], partition.Flows[1].Types);
            Assert.Null(partition.Current);
            Assert.Equal(0, partition.Versions);
            var wellbore = Assert.Single(partition.Types, t => t.Name == "Wellbore");
            Assert.Equal([projectA, projectB], wellbore.Sources.Select(s => s.Flow));
            Assert.Equal("approve", wellbore.OnChange);
            Assert.Equal(["FacilityName", "Alias"], wellbore.Fields.Select(f => f.As));
            Assert.Equal([projectA, projectB], wellbore.Fields[0].Flows);
            Assert.Equal([projectB], wellbore.Fields[1].Flows);
            Assert.Equal([projectB], Assert.Single(partition.Types, t => t.Name == "UnitOfMeasure").Sources.Select(s => s.Flow));

            var alone = Assert.Single(listed, c => c.Scope == single);
            Assert.Equal([projectC], alone.Flows.Select(f => f.Name));
            Assert.Equal(["UnitOfMeasure"], alone.Types.Select(t => t.Name));

            // A capture by either flow lands in the partition's one cache, which the page shows with the flow that wrote it.
            var store = new OsduCacheStore(() => SampleEstate.Context(cs));
            var captured = new ReferenceType(
                "Wellbore", "master-data--Wellbore",
                [ReferenceItem.FromText(shared + ":master-data--Wellbore:1", new Dictionary<string, string> { ["FacilityName"] = "WB 1/1-A", ["Alias"] = "WELL A" })]);
            Assert.True((await store.MergeAsync(shared, projectB, [captured], new CacheCapture(null, "tests", "seeded"), DateTimeOffset.UtcNow)).Written);
            var refreshed = Assert.Single(
                await ReadAsync<List<DeliveryCacheDto>>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/caches?repoId=" + repoId)),
                c => c.Scope == shared);
            Assert.Equal(projectB, refreshed.Current!.Flow);
            Assert.Equal(1, refreshed.Versions);
            Assert.Equal(1, Assert.Single(refreshed.Types, t => t.Name == "Wellbore").Items);
            Assert.Null(Assert.Single(
                await ReadAsync<List<DeliveryCacheDto>>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/caches?repoId=" + repoId)),
                c => c.Scope == single).Current);

            // The mapping builder offers the same partition cache, filled by both flows.
            var builder = await ReadAsync<List<DeliveryBuilderCacheDto>>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/mapping-builder/caches"));
            Assert.Equal([projectA, projectB], Assert.Single(builder, c => c.Scope == shared).Flows);
            Assert.Equal([projectC], Assert.Single(builder, c => c.Scope == single).Flows);
        }
        finally
        {
            await CleanupCachesAsync(cs, shared, single);
            await using (var osdu = SampleEstate.Context(cs))
            {
                await osdu.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
                await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A cache flow of one partition searching the test endpoint, with its types (YAML list items indented by two spaces).</summary>
    private static string CacheFlowYaml(string name, string partition, string types) => $"""
        flowType: cache
        name: {name}
        source:
          endpoint: https://osdu.example.test
          headers:
            data-partition-id: {partition}
        types:
        {types}
        """;

    [Fact]
    public async Task A_reader_browses_the_OSDU_data_definitions_and_gets_a_kind_bundled_with_where_it_came_from()
    {
        var cs = OsduTestServer.Require();
        await ProvisionAsync(cs);
        using var handler = new DataDefinitionsHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        using var cache = new TemporaryDirectory();
        var definitions = new OsduDataDefinitions(
            () => http, OsduDataDefinitions.DefaultApiUrl, OsduDataDefinitions.DefaultWebUrl, cache.Path, Timeout.InfiniteTimeSpan, TimeSpan.FromMinutes(1), TimeProvider.System);
        await using var factory = Factory(cs).WithServices(services => services.AddSingleton(definitions));
        using var client = factory.CreateClient();
        var reader = await TokenAsync(client, "read");

        using (var anonymous = await client.GetAsync(new Uri("/api/v1/delivery/templates/osdu/releases", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var listed = await ReadAsync<DeliveryOsduReleasesDto>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/templates/osdu/releases"));
        Assert.NotNull(listed.SyncedUtc);
        var release = Assert.Single(listed.Releases);
        Assert.Equal("v0.30.0", release.Name);
        Assert.Equal(DataDefinitionsHandler.Commit, release.Commit);

        // Listing the releases downloads nothing: a release comes into the local copy when it is first read.
        Assert.False(release.Local);
        Assert.Equal("https://community.opengroup.org/osdu/data/data-definitions/-/tree/v0.30.0/Generated", release.WebUrl.AbsoluteUri);

        // The index lists the record kinds only: the abstract schema it also names is a building block.
        var index = await ReadAsync<DeliveryOsduSchemaIndexDto>(await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/templates/osdu/schemas"));
        Assert.Equal(2, index.Schemas.Count);
        var wellbore = Assert.Single(index.Schemas, s => s.Kind == WellboreKind);
        Assert.Equal(WellboreKind, wellbore.Kind);
        Assert.Equal("https://community.opengroup.org/osdu/data/data-definitions/-/blob/v0.30.0/Generated/master-data/Wellbore.1.3.0.json", wellbore.WebUrl.AbsoluteUri);

        var file = await ReadAsync<DeliveryOsduSchemaFileDto>(await SendAsync(
            client, reader, HttpMethod.Get, $"/api/v1/delivery/templates/osdu/schema?release=v0.30.0&kind={Uri.EscapeDataString(WellboreKind)}"));
        Assert.Equal("OSDU data definitions v0.30.0 (99f8fc88d8ad) Generated/master-data/Wellbore.1.3.0.json", file.Origin);
        Assert.NotNull(file.Schema["definitions"]?["AbstractAccessControlList.1.0.0"]);

        // What comes back is what the preview lays out, and what saving would store.
        var detail = await ReadAsync<DeliveryTemplateDetailDto>(await SendAsync(client, reader, HttpMethod.Post, "/api/v1/delivery/templates/preview", new { kind = file.Kind, schema = file.Schema }));
        Assert.Equal(file.Version, detail.Version);
        Assert.Contains(detail.Variables, v => v.Path == "osdu.data.FacilityName");
        Assert.Contains(detail.Variables, v => v.Path == "osdu.acl.owners");

        using (var unknownRelease = await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/templates/osdu/schemas?release=v9.9.9"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknownRelease.StatusCode);
        }

        using (var missingKind = await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/templates/osdu/schema?kind=" + Uri.EscapeDataString("osdu:wks:master-data--Missing:1.0.0")))
        {
            Assert.Equal(HttpStatusCode.NotFound, missingKind.StatusCode);
            Assert.Contains("Generated/master-data/Missing.1.0.0.json", await missingKind.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var notKind = await SendAsync(client, reader, HttpMethod.Get, "/api/v1/delivery/templates/osdu/schema?kind=Wellbore"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, notKind.StatusCode);
        }

        // Two versions of the kind compare variable by variable, and as their files are published.
        const string OlderKind = "osdu:wks:master-data--Wellbore:1.0.0";
        var comparison = await ReadAsync<DeliveryOsduComparisonDto>(await SendAsync(
            client, reader, HttpMethod.Get,
            $"/api/v1/delivery/templates/osdu/compare?fromRelease=v0.30.0&fromKind={Uri.EscapeDataString(OlderKind)}&toRelease=v0.30.0&toKind={Uri.EscapeDataString(WellboreKind)}"));
        Assert.False(comparison.SameFile);
        Assert.False(comparison.OnlyIdentifiersDiffer);
        Assert.False(comparison.SameTemplate);
        Assert.Equal(("DEVELOPMENT", "PUBLISHED"), (comparison.From.Status, comparison.To.Status));
        Assert.Contains("\"Name\"", comparison.From.FileText, StringComparison.Ordinal);
        Assert.Equal("https://community.opengroup.org/osdu/data/data-definitions/-/blob/v0.30.0/Generated/master-data/Wellbore.1.0.0.json", comparison.From.WebUrl.AbsoluteUri);
        var removed = Assert.Single(comparison.Changes, c => c.Path == "osdu.data.Name");
        Assert.Equal(("Removed", "Breaking"), (removed.Change, removed.Impact));
        var added = Assert.Single(comparison.Changes, c => c.Path == "osdu.data.FacilityName");
        Assert.Equal(("Added", "Additive"), (added.Change, added.Impact));
        Assert.Equal((1, 1, 0), (comparison.Breaking, comparison.Additive, comparison.Wording));

        // Both versions refer to the same access control list schema, which is the same in both, so no shared schema differs.
        Assert.Equal(1, comparison.SameReferencedFiles);
        Assert.Empty(comparison.ReferencedFiles);

        var same = await ReadAsync<DeliveryOsduComparisonDto>(await SendAsync(
            client, reader, HttpMethod.Get, $"/api/v1/delivery/templates/osdu/compare?fromKind={Uri.EscapeDataString(WellboreKind)}&toKind={Uri.EscapeDataString(WellboreKind)}"));
        Assert.True(same.SameFile && same.SameTemplate);
        Assert.Empty(same.Changes);

        using (var otherKind = await SendAsync(
            client, reader, HttpMethod.Get, $"/api/v1/delivery/templates/osdu/compare?fromKind={Uri.EscapeDataString("osdu:wks:master-data--Well:1.0.0")}&toKind={Uri.EscapeDataString(WellboreKind)}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, otherKind.StatusCode);
            Assert.Contains("different kinds", await otherKind.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // A sync reads the release list again and downloads the newest release when it is not on disk. It sits in the operate
        // group, which takes a signed-in caller: an anonymous one is refused.
        using (var anonymousSync = await client.PostAsJsonAsync(new Uri("/api/v1/delivery/templates/osdu/sync", UriKind.Relative), new { release = (string?)null }, Web))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousSync.StatusCode);
        }

        var operate = await TokenAsync(client, "read", "operate");
        var sync = await ReadAsync<DeliveryOsduSyncDto>(await SendAsync(client, operate, HttpMethod.Post, "/api/v1/delivery/templates/osdu/sync", new { release = (string?)null }));
        var synced = Assert.Single(sync.Releases);
        Assert.True(synced.Local);
        Assert.Empty(sync.Downloaded);
        Assert.Equal(1, handler.Archives);
    }

    /// <summary>
    /// A repository as the sync would leave it: a tracked source, one delivery flow that renders with the sample WellLog
    /// mapping and delivers to <paramref name="scope"/>, the cache definitions a cache flow of that partition declares, and
    /// the sample cache records as the current version of the partition's cache, merged the way a refresh merges a capture.
    /// </summary>
    private static async Task SeedRepositoryAsync(string cs, string repoName, Guid repoId, Guid sourceId, string flowName, Guid pipelineId, string scope, string cacheFlowName)
    {
        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(cs);
        await using var osdu = SampleEstate.Context(cs);
        db.Repos.Add(new CatalogRepo { Id = repoId, Name = repoName, RemoteUrl = "https://example/" + repoName + ".git", RootPath = Path.GetTempPath(), FirstSeenUtc = now, LastSyncUtc = now });
        db.RepoSources.Add(new CatalogRepoSource { Id = sourceId, Name = repoName, RemoteUrl = "https://example/" + repoName + ".git", Branch = "main" });
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = pipelineId,
            RepoId = repoId,
            Name = flowName,
            Kind = "delivery",
            RelativePath = "flows/" + flowName + ".yaml",
            ContentHash = new string('0', 64),
            Yaml = $"""
                flowType: delivery
                name: {flowName}
                source:
                  connection: {SourceConnectionReference}
                  record:
                    object: OsduData.silver.WellLog
                    key: [source_project, log_id]
                  lastModified: update_date
                  work: ../.work/{flowName}
                render:
                  mapping: WellLog@1.4.0
                  parameters:
                    dataPartition: dev
                    aclViewer: data.welldb.viewers@dev.dataservices.energy
                    legalTag: {UnsetReference}
                target:
                  endpoint: https://osdu.example.test
                  headers:
                    data-partition-id: {scope}
                  protocol: storage
                """,
            DefinitionJson = $$"""{"name":"{{flowName}}","flowKind":"delivery"}""",
            Active = true,
            Wave = 0,
            FirstSeenUtc = now,
            LastSeenUtc = now,
        });

        var fields = new Dictionary<string, string>
        {
            ["UnitOfMeasure"] = """[{"path":"data.Code","as":"Code"},{"path":"data.Name","as":"Name"},{"path":"data.ID","as":"ID"}]""",
            ["LogCurveBusinessValue"] = """[{"path":"data.Code","as":"Code"},{"path":"data.Name","as":"Name"}]""",
            ["LogCurveFamily"] = """[{"path":"data.Code","as":"Code"},{"path":"data.Name","as":"Name"}]""",
            ["LogCurveMainFamily"] = """[{"path":"data.Code","as":"Code"},{"path":"data.Name","as":"Name"}]""",
            ["LogCurveType"] = """[{"path":"data.Code","as":"Code"},{"path":"data.Name","as":"Name"}]""",
            ["VerticalMeasurementType"] = """[{"path":"data.Code","as":"Code"},{"path":"data.Name","as":"Name"}]""",
        };

        var types = new List<ReferenceType>();
        foreach (var (type, declared) in fields)
        {
            var file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(SampleRoot, "cache-records", type + ".json")))!.AsObject();
            var entityType = file["entityType"]!.GetValue<string>();
            osdu.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
            {
                Id = Guid.NewGuid(),
                RepoId = repoId,
                FlowName = cacheFlowName,
                Scope = scope,
                Endpoint = "https://osdu.example.test",
                RelativePath = "cache/" + cacheFlowName + ".yaml",
                Name = type,
                EntityType = entityType,
                Kind = "osdu:wks:" + entityType + ":*",
                FieldsJson = declared,
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            types.Add(ReferenceType.FromJson(type, file));
        }

        // The lookup tables the mapping translates source spellings through, declared by the partition's lookups flow as a
        // sync records them: the unit maps and the curve dictionary, each an ingestion table its own flows load.
        foreach (var lookup in Samples.SampleLookups())
        {
            osdu.DeliveryCacheDefinitions.Add(new DeliveryCacheDefinition
            {
                Id = Guid.NewGuid(),
                RepoId = repoId,
                FlowName = cacheFlowName + "-lookups",
                Scope = scope,
                Origin = "table",
                Connection = SourceConnectionReference,
                SourceObject = "OsduData.silver." + lookup.Name,
                KeyField = lookup.Key,
                DictionaryPath = null,
                RelativePath = "cache/" + cacheFlowName + "-lookups.yaml",
                Name = lookup.Name,
                EntityType = lookup.EntityType,
                FieldsJson = JsonSerializer.Serialize(lookup.FieldNames.Where(f => f != lookup.Key).Select(f => new { path = f, @as = f })),
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
            types.Add(lookup);
        }

        await db.SaveChangesAsync();
        await osdu.SaveChangesAsync();

        // Captured at the instant the reference version names, so the version the merge writes carries that label.
        var store = new OsduCacheStore(() => SampleEstate.Context(cs));
        var write = await store.MergeAsync(scope, cacheFlowName, types, new CacheCapture(null, "tests", "sample files"), new DateTimeOffset(2026, 9, 8, 21, 27, 27, TimeSpan.Zero));
        Assert.Equal(ReferenceVersion, write.Snapshot.Version);
    }

    /// <summary>Removes every version, record and flow membership of the partitions' caches.</summary>
    private static async Task CleanupCachesAsync(string cs, params string[] scopes)
    {
        await using var osdu = SampleEstate.Context(cs);
        await osdu.DeliveryCacheMembers.Where(m => scopes.Contains(m.Scope)).ExecuteDeleteAsync();
        await osdu.DeliveryCacheItems.Where(i => scopes.Contains(i.Scope)).ExecuteDeleteAsync();
        await osdu.DeliveryCacheVersions.Where(v => scopes.Contains(v.Scope)).ExecuteDeleteAsync();
    }

    private static async Task CleanupAsync(string cs, Guid repoId, string flowName, string scope)
    {
        await CleanupCachesAsync(cs, scope);
        await using (var osdu = SampleEstate.Context(cs))
        {
            await osdu.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
            await osdu.DeliveryInterfaces.Where(i => i.RepoId == repoId).ExecuteDeleteAsync();
        }

        await using var db = CatalogDatabase.Create(cs);
        await db.ComputeTasks.Where(t => t.SourceRef == flowName).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        var repoName = await db.Repos.Where(r => r.Id == repoId).Select(r => r.Name).FirstOrDefaultAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        if (repoName is not null)
        {
            await db.RepoSources.Where(s => s.Name == repoName).ExecuteDeleteAsync();
        }
    }

    private static object SaveBody(string kind, string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(SampleSource, "templates", file)));
        return new { kind, schema = document.RootElement.Clone(), origin = "file " + file };
    }

    private static string DetailUrl(string kind, string version) => $"/api/v1/delivery/templates/detail?kind={Uri.EscapeDataString(kind)}&version={version}";

    /// <summary>The control plane with the OSDU module, its own database migrated, and no network warm-up of the schema repository.</summary>
    private static ControlPlaneAppFactory Factory(string cs)
        => new ControlPlaneAppFactory()
            .WithCatalog(cs)
            .WithModules(new DeliveryControlPlaneModule())
            .WithSetting("ControlPlane:Worker:Enabled", "false")
            .WithSetting("Osdu:SchemaRepository:WarmOnStart", "false")
;

    /// <summary>Provisions the catalog and the module's schema: what a control plane's bootstrap does before it serves.</summary>
    private static async Task ProvisionAsync(string cs)
    {
        await CatalogDatabase.MigrateAsync(cs);
        await SampleEstate.MigrateModuleAsync(cs);
    }

    private static async Task<string> TokenAsync(HttpClient client, params string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/v1/auth/token", UriKind.Relative), new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Web);
        }

        return await client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.StatusCode}: {text}");
            var value = JsonSerializer.Deserialize<T>(text, Web);
            Assert.NotNull(value);
            return value;
        }
    }

    /// <summary>
    /// The OSDU data definitions' GitLab API, reduced to one release: its tag, and its Generated folder (a Wellbore schema
    /// in two versions and the abstract schema they refer to) as the tar.gz archive GitLab serves.
    /// </summary>
    private sealed class DataDefinitionsHandler : HttpMessageHandler
    {
        public const string Commit = "99f8fc88d8ad838b5738ac5ad92ac643538b5766";

        private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
        {
            ["SchemaStatus.json"] = """{ "osdu:wks:master-data--Wellbore:1.0.0": "DEVELOPMENT", "osdu:wks:master-data--Wellbore:1.3.0": "PUBLISHED", "osdu:wks:AbstractAccessControlList:1.0.0": "PUBLISHED" }""",
            ["master-data/Wellbore.1.0.0.json"] = """
                { "x-osdu-schema-source": "osdu:wks:master-data--Wellbore:1.0.0", "type": "object",
                  "properties": { "acl": { "$ref": "../abstract/AbstractAccessControlList.1.0.0.json" }, "data": { "type": "object", "properties": { "Name": { "type": "string" } } } } }
                """,
            ["master-data/Wellbore.1.3.0.json"] = """
                { "x-osdu-schema-source": "osdu:wks:master-data--Wellbore:1.3.0", "type": "object",
                  "properties": { "acl": { "$ref": "../abstract/AbstractAccessControlList.1.0.0.json" }, "data": { "type": "object", "properties": { "FacilityName": { "type": "string" } } } } }
                """,
            ["abstract/AbstractAccessControlList.1.0.0.json"] = """{ "type": "object", "properties": { "owners": { "type": "array", "items": { "type": "string" } } } }""",
        };

        private int _archives;

        /// <summary>How many times the release's archive was downloaded.</summary>
        public int Archives => _archives;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/repository/tags", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""[ { "name": "v0.30.0", "commit": { "id": "{{Commit}}", "committed_date": "2026-07-17T14:55:57.000+08:00" } } ]""", Encoding.UTF8, "application/json"),
                });
            }

            if (path.EndsWith("/repository/archive.tar.gz", StringComparison.Ordinal) && request.RequestUri.Query.Contains("sha=" + Commit, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _archives);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive()) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static byte[] Archive()
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                var top = $"data-definitions-v0.30.0-{Commit}-Generated/";
                foreach (var (file, json) in Files)
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "Generated/" + file) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(json)) });
                }
            }

            return buffer.ToArray();
        }
    }

    /// <summary>A folder under the temp folder for one test, removed afterwards.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlflow_dd_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
