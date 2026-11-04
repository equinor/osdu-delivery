using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Delivery.Catalog;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// What a cache flow declares reaching the catalog through the repository sync, so the GUI can show what a cache holds and
/// which file defines it. The sync only reads the repository: a cache's versions are written by its runs, never by the
/// sync, and snapshot files left in a repository are nothing to it.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CacheCatalogSyncTests : IDisposable
{
    private const string CacheFlow = """
        flowType: cache
        name: welldb-osdu-00-reference-cache
        source:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }
        onChange: approve
        types:
          - kind: osdu:wks:master-data--Wellbore:1.0.0
            onChange: auto
            fields:
              - data.FacilityName
              - path: data.NameAlias.AliasName
                as: Alias
          - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
            fields: [data.Code, data.Name]
        """;

    private readonly OsduTestDatabase _catalog = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-cache-sync-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _catalog.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string content, string? root = null)
    {
        var path = Path.Combine(root ?? _root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private async Task<IReadOnlyList<string>> SyncAsync(Guid repoId, string? root = null) => (await SyncWithResultAsync(repoId, root)).Warnings;

    private async Task<(IReadOnlyList<string> Warnings, CatalogSyncExtensionResult Result)> SyncWithResultAsync(Guid repoId, string? root = null)
    {
        var warnings = new List<string>();
        var sync = new DeliveryCatalogSync(new DeliveryDocumentLoader());
        await using var db = _catalog.CreateDbContext();
        // The one write the extension makes, on the context it is given: the control plane hands it the module's
        // database enlisted in the catalog's own sync transaction, and a test hands it the module's database directly.
        var result = await sync.ReconcileAsync(db, repoId, root ?? _root, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), warnings, CancellationToken.None);
        return (warnings, result);
    }

    /// <summary>A cache flow document: its name, partition and endpoint, and its types (YAML list items indented by two spaces).</summary>
    private static string Flow(string name, string types, string partition = "dev", string endpoint = "https://osdu.example.com") => $"""
        flowType: cache
        name: {name}
        source:
          endpoint: {endpoint}
          headers: {"{"} data-partition-id: {partition} {"}"}
        types:
        {types}
        """;

    private const string FacilityWellbore = """
          - kind: osdu:wks:master-data--Wellbore:1.0.0
            fields: [data.FacilityName]
        """;

    private const string AliasedWellboreAndUnits = """
          - kind: osdu:wks:master-data--Wellbore:1.0.0
            fields:
              - data.FacilityName
              - path: data.NameAlias.AliasName
                as: Alias
          - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
            fields: [data.Code, data.Name]
        """;

    [Fact]
    public async Task Cache_flows_of_one_partition_declare_into_one_cache_and_a_declaration_that_disagrees_is_left_out()
    {
        Write("cache/a.yaml", Flow("project-a-cache", FacilityWellbore));
        Write("cache/b.yaml", Flow("project-b-cache", AliasedWellboreAndUnits));
        Write("cache/c.yaml", Flow("project-c-cache", """
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                fields:
                  - path: data.Name
                    as: FacilityName
              - kind: osdu:wks:reference-data--VerticalMeasurementType:1.0.0
                fields: [data.Code]
            """));
        Write("cache/d.yaml", Flow("other-partition-cache", """
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                fields:
                  - path: data.Name
                    as: FacilityName
            """, partition: "other"));
        var repoId = Guid.NewGuid();

        var (warnings, result) = await SyncWithResultAsync(repoId);

        var warning = Assert.Single(warnings);
        Assert.StartsWith("cache/c.yaml: Wellbore is left out of the cache of partition 'dev', because ", warning, StringComparison.Ordinal);
        Assert.Contains("Wellbore.FacilityName is cached from data.FacilityName by cache flow 'project-a-cache' and from data.Name by 'project-c-cache'", warning, StringComparison.Ordinal);
        Assert.Equal(1, result.Invalid);

        await using var db = _catalog.CreateDbContext();
        var rows = (await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ToListAsync())
            .OrderBy(c => c.Scope, StringComparer.Ordinal).ThenBy(c => c.FlowName, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => $"{c.Scope}/{c.FlowName}/{c.Name}")
            .ToList();
        Assert.Equal(
            [
                "dev/project-a-cache/Wellbore",
                "dev/project-b-cache/UnitOfMeasure",
                "dev/project-b-cache/Wellbore",
                "dev/project-c-cache/VerticalMeasurementType",
                "other/other-partition-cache/Wellbore",
            ],
            rows);
        var aliased = await db.DeliveryCacheDefinitions.SingleAsync(c => c.RepoId == repoId && c.FlowName == "project-b-cache" && c.Name == "Wellbore");
        Assert.Equal("https://osdu.example.com", aliased.Endpoint);
        Assert.Equal("cache/b.yaml", aliased.RelativePath);

        // What a refresh of the partition reads: one Wellbore type, filled from every path its flows declare.
        var declaration = await OsduCacheStore.DeclarationAsync(db, "dev");
        Assert.Equal(["project-a-cache", "project-b-cache"], declaration.Of("Wellbore").Select(d => d.FlowName));
        Assert.Equal(["FacilityName", "Alias"], declaration.FieldsOf("Wellbore").Select(f => f.Name));
        Assert.Equal(["data.Name"], (await OsduCacheStore.DeclarationAsync(db, "other")).FieldsOf("Wellbore").Select(f => f.Path));
    }

    [Fact]
    public async Task A_declaration_that_disagrees_with_another_repository_s_flow_of_the_partition_is_left_out()
    {
        Write("cache/welldb-osdu-00-reference-cache.yaml", CacheFlow);
        Assert.Empty(await SyncAsync(Guid.NewGuid()));

        var other = Path.Combine(_root, "other");
        Write("well-cache.yaml", Flow("well-cache", """
              - kind: osdu:wks:master-data--Well:1.0.0
                name: Wellbore
                fields: [data.FacilityName]
              - kind: osdu:wks:master-data--Well:1.0.0
                fields: [data.FacilityName]
            """), other);
        var repoId = Guid.NewGuid();
        var (warnings, result) = await SyncWithResultAsync(repoId, other);

        var warning = Assert.Single(warnings);
        Assert.Contains(
            "well-cache.yaml: Wellbore is left out of the cache of partition 'dev', because cache flow 'welldb-osdu-00-reference-cache' declares Wellbore as master-data--Wellbore, and 'well-cache' declares it as master-data--Well",
            warning,
            StringComparison.Ordinal);
        Assert.Equal(1, result.Invalid);
        await using var db = _catalog.CreateDbContext();
        Assert.Equal(["Well"], await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).Select(c => c.Name).ToListAsync());
    }

    [Fact]
    public async Task Cache_flows_of_one_partition_searching_different_endpoints_are_warned_about()
    {
        Write("cache/a.yaml", Flow("project-a-cache", FacilityWellbore));
        Write("cache/b.yaml", Flow("project-b-cache", AliasedWellboreAndUnits, endpoint: "https://other.example.com"));
        Write("cache/c.yaml", Flow("elsewhere-cache", FacilityWellbore, partition: "other", endpoint: "https://third.example.com"));
        var repoId = Guid.NewGuid();

        var (warnings, result) = await SyncWithResultAsync(repoId);

        var warning = Assert.Single(warnings);
        Assert.StartsWith("The cache flows of partition 'dev' search different endpoints (https://osdu.example.com, https://other.example.com)", warning, StringComparison.Ordinal);
        Assert.Equal(0, result.Invalid);
        await using var db = _catalog.CreateDbContext();
        Assert.Equal(4, await db.DeliveryCacheDefinitions.CountAsync(c => c.RepoId == repoId));
    }

    [Fact]
    public async Task A_flow_that_stops_declaring_a_type_lets_go_of_the_records_it_held()
    {
        Write("cache/a.yaml", Flow("project-a-cache", AliasedWellboreAndUnits));
        Write("cache/b.yaml", Flow("project-b-cache", """
              - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
                fields: [data.Code, data.Name]
            """));
        var repoId = Guid.NewGuid();
        Assert.Empty(await SyncAsync(repoId));

        static ReferenceType Units(params string[] codes) => new(
            "UnitOfMeasure", "reference-data--UnitOfMeasure",
            codes.Select(code => ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:" + code, new Dictionary<string, string> { ["Code"] = code, ["Name"] = code })));
        var wellbore = new ReferenceType(
            "Wellbore", "master-data--Wellbore",
            [ReferenceItem.FromText("dev:master-data--Wellbore:1", new Dictionary<string, string> { ["FacilityName"] = "WB-0001" })]);
        var store = _catalog.Caches();
        var capture = new CacheCapture(null, "tests", "seeded");
        var at = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        await store.MergeAsync("dev", "project-a-cache", [Units("m"), wellbore], capture, at);
        await store.MergeAsync("dev", "project-b-cache", [Units("ft")], capture, at.AddHours(1));

        Write("cache/a.yaml", Flow("project-a-cache", FacilityWellbore));
        Assert.Empty(await SyncAsync(repoId));

        await using (var db = _catalog.CreateDbContext())
        {
            Assert.False(await db.DeliveryCacheMembers.AnyAsync(m => m.FlowName == "project-a-cache" && m.TypeName == "UnitOfMeasure"));
            Assert.Equal(1, await db.DeliveryCacheMembers.CountAsync(m => m.FlowName == "project-a-cache" && m.TypeName == "Wellbore"));
            Assert.Equal(1, await db.DeliveryCacheMembers.CountAsync(m => m.FlowName == "project-b-cache" && m.TypeName == "UnitOfMeasure"));
        }

        // With A's hold gone, the metre leaves the partition's cache the next time B captures units without it.
        var next = await store.MergeAsync("dev", "project-b-cache", [Units("ft")], capture, at.AddHours(2));
        Assert.True(next.Written);
        Assert.Equal(["dev:reference-data--UnitOfMeasure:ft"], next.Snapshot.Type("UnitOfMeasure")!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task What_a_cache_flow_declares_reaches_the_catalog()
    {
        Write("cache/welldb-osdu-00-reference-cache.yaml", CacheFlow);
        var repoId = Guid.NewGuid();
        Assert.Empty(await SyncAsync(repoId));

        await using var db = _catalog.CreateDbContext();
        var definitions = await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).OrderBy(c => c.Name).ToListAsync();
        Assert.Equal(["UnitOfMeasure", "Wellbore"], definitions.Select(d => d.Name));

        var wellbore = definitions[1];
        Assert.Equal("welldb-osdu-00-reference-cache", wellbore.FlowName);
        Assert.Equal("dev", wellbore.Scope);
        Assert.Equal("osdu", wellbore.Origin);
        Assert.Null(wellbore.Connection);
        Assert.Null(wellbore.SourceObject);
        Assert.Null(wellbore.KeyField);
        Assert.Null(wellbore.DictionaryPath);
        Assert.Equal("https://osdu.example.com", wellbore.Endpoint);
        Assert.Equal("cache/welldb-osdu-00-reference-cache.yaml", wellbore.RelativePath);
        Assert.Equal("master-data--Wellbore", wellbore.EntityType);
        Assert.Equal("osdu:wks:master-data--Wellbore:1.0.0", wellbore.Kind);
        Assert.Equal("auto", wellbore.OnChange);
        Assert.Equal("approve", definitions[0].OnChange);
        using var fields = JsonDocument.Parse(wellbore.FieldsJson);
        Assert.Equal(["FacilityName", "Alias"], fields.RootElement.EnumerateArray().Select(f => f.GetProperty("as").GetString()));
        Assert.Equal(
            ["data.FacilityName", "data.NameAlias.AliasName"],
            fields.RootElement.EnumerateArray().Select(f => f.GetProperty("path").GetString()));

        // The sync writes no version: a cache is filled by the runs of its cache flow.
        Assert.Empty(await db.DeliveryCacheVersions.ToListAsync());
        Assert.Empty(await db.DeliveryCacheItems.ToListAsync());
    }

    [Fact]
    public async Task A_second_sync_leaves_one_row_per_declared_type_and_reads_a_changed_declaration_again()
    {
        Write("cache/welldb-osdu-00-reference-cache.yaml", CacheFlow);
        var repoId = Guid.NewGuid();
        await SyncAsync(repoId);
        await SyncAsync(repoId);

        await using (var db = _catalog.CreateDbContext())
        {
            Assert.Equal(2, await db.DeliveryCacheDefinitions.CountAsync(c => c.RepoId == repoId));
        }

        Write("cache/welldb-osdu-00-reference-cache.yaml", CacheFlow.Replace("fields: [data.Code, data.Name]", "fields: [data.Code]", StringComparison.Ordinal));
        await SyncAsync(repoId);

        await using (var db = _catalog.CreateDbContext())
        {
            var units = await db.DeliveryCacheDefinitions.SingleAsync(c => c.RepoId == repoId && c.Name == "UnitOfMeasure");
            using var fields = JsonDocument.Parse(units.FieldsJson);
            Assert.Equal(["Code"], fields.RootElement.EnumerateArray().Select(f => f.GetProperty("as").GetString()));
        }
    }

    [Fact]
    public async Task A_type_that_reads_its_records_whole_says_so_in_the_catalog_and_a_change_to_it_is_read_again()
    {
        var whole = CacheFlow.Replace("    onChange: auto", "    onChange: auto\n    fetchRecords: true", StringComparison.Ordinal);
        Assert.NotEqual(CacheFlow, whole);
        Write("cache/welldb-osdu-00-reference-cache.yaml", whole);
        var repoId = Guid.NewGuid();
        Assert.Empty(await SyncAsync(repoId));

        await using (var db = _catalog.CreateDbContext())
        {
            var rows = await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).OrderBy(c => c.Name).ToListAsync();
            Assert.Equal([("UnitOfMeasure", false), ("Wellbore", true)], rows.Select(r => (r.Name, r.FetchRecords)));

            // A refresh and a cache step read it from the partition's declaration.
            var declaration = await OsduCacheStore.DeclarationAsync(db, "dev");
            Assert.True(declaration.FetchesRecords("Wellbore"));
            Assert.False(declaration.FetchesRecords("UnitOfMeasure"));
        }

        // Taken out again, the next sync reads the type from the index once more.
        Write("cache/welldb-osdu-00-reference-cache.yaml", CacheFlow);
        var (_, result) = await SyncWithResultAsync(repoId);
        Assert.Equal(1, result.Updated);
        await using (var db = _catalog.CreateDbContext())
        {
            Assert.False((await db.DeliveryCacheDefinitions.SingleAsync(c => c.RepoId == repoId && c.Name == "Wellbore")).FetchRecords);
        }
    }

    [Fact]
    public async Task A_type_s_attributes_reach_the_catalog_with_every_setting_and_a_refresh_reads_them_back()
    {
        Write("cache/attributes-cache.yaml", Flow("attributes-cache", """
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                fields: [data.FacilityName]
                attributes:
                  WellName: [data.WellID, data.FacilityName]
                  FieldID: { path: 'data.GeoContexts[GeoTypeID$=:Field:].FieldID', keep: id }
                lookupKinds: [osdu:wks:master-data--Well:1.0.0]
            """));
        var repoId = Guid.NewGuid();
        Assert.Empty(await SyncAsync(repoId));

        await using var db = _catalog.CreateDbContext();
        var row = await db.DeliveryCacheDefinitions.SingleAsync(c => c.RepoId == repoId);
        using (var fields = JsonDocument.Parse(row.FieldsJson))
        {
            var listed = fields.RootElement.EnumerateArray().ToList();
            Assert.Equal(["FacilityName", "WellName", "FieldID"], listed.Select(f => f.GetProperty("as").GetString()));
            Assert.False(listed[0].TryGetProperty("attribute", out _));
            Assert.Equal(["data.WellID", "data.FacilityName"], listed[1].GetProperty("attribute").GetProperty("steps").EnumerateArray().Select(s => s.GetString()));
            Assert.Equal("id", listed[2].GetProperty("attribute").GetProperty("keep").GetString());
        }

        // What a refresh and a cache step read: the attributes as the flow declares them.
        var declared = (await OsduCacheStore.DeclarationAsync(db, "dev")).FieldsOf("Wellbore");
        Assert.Equal([false, true, true], declared.Select(f => f.IsAttribute));
        Assert.Equal(Model.DimensionValueKeep.Id, declared[2].Attribute!.Keep);
        Assert.Equal("osdu:wks:master-data--Well:1.0.0", declared[1].LookupKinds["master-data--Well"]);

        // A second sync of the same document changes nothing.
        var (_, again) = await SyncWithResultAsync(repoId);
        Assert.Equal(0, again.Updated);
    }

    [Fact]
    public async Task Snapshot_files_left_in_a_repository_are_nothing_to_the_sync()
    {
        Write("cache/welldb-osdu-00-reference-cache.yaml", CacheFlow);
        Write("snapshots/references/current", "20260101T000000Z");
        Write("snapshots/references/20260101T000000Z/manifest.json", """
            { "version": "20260101T000000Z", "capturedUtc": "2026-01-01T00:00:00Z", "types": ["Wellbore"] }
            """);
        Write("snapshots/references/20260101T000000Z/Wellbore.json", """
            { "entityType": "master-data--Wellbore", "items": [ { "id": "dev:master-data--Wellbore:1", "FacilityName": "WB-0001" } ] }
            """);

        Assert.Empty(await SyncAsync(Guid.NewGuid()));

        await using var db = _catalog.CreateDbContext();
        Assert.Empty(await db.DeliveryCacheVersions.ToListAsync());
        Assert.Empty(await db.DeliveryCacheItems.ToListAsync());
    }

    [Fact]
    public async Task A_cache_that_leaves_the_repository_leaves_the_catalog()
    {
        Write("cache/welldb-osdu-00-reference-cache.yaml", CacheFlow);
        var repoId = Guid.NewGuid();
        await SyncAsync(repoId);

        File.Delete(Path.Combine(_root, "cache", "welldb-osdu-00-reference-cache.yaml"));
        await SyncAsync(repoId);

        await using var db = _catalog.CreateDbContext();
        Assert.Empty(await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ToListAsync());
    }

    [Fact]
    public async Task A_retrieval_flow_is_not_a_cache()
    {
        Write("flows/osdu-metadata-sync.yaml", """
            flowType: retrieval
            name: osdu-metadata-sync
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: dev }
              kinds: [osdu:wks:master-data--Wellbore:1.0.0]
            target:
              location: lake/metadata
            """);

        var repoId = Guid.NewGuid();
        Assert.Empty(await SyncAsync(repoId));
        await using var db = _catalog.CreateDbContext();
        Assert.Empty(await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ToListAsync());
    }

    [Fact]
    public async Task An_invalid_cache_declaration_is_reported_and_skipped()
    {
        Write("cache/broken.yaml", """
            flowType: cache
            name: broken-cache
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: dev }
            types:
              - fields: [data.FacilityName]
            """);

        var repoId = Guid.NewGuid();
        var warnings = await SyncAsync(repoId);
        Assert.Contains(warnings, w => w.Contains("needs a kind (the OSDU kind whose records the type caches), a dictionary", StringComparison.Ordinal));
        await using var db = _catalog.CreateDbContext();
        Assert.Empty(await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).ToListAsync());
    }

    [Fact]
    public async Task A_cache_declared_by_two_files_keeps_one_and_says_so()
    {
        Write("cache/a.yaml", CacheFlow);
        Write("cache/b.yaml", CacheFlow);

        var repoId = Guid.NewGuid();
        var warnings = await SyncAsync(repoId);
        Assert.Contains(warnings, w => w.Contains("cache flow 'welldb-osdu-00-reference-cache' is already declared by cache/a.yaml", StringComparison.Ordinal));
        await using var db = _catalog.CreateDbContext();
        Assert.Equal(2, await db.DeliveryCacheDefinitions.CountAsync(c => c.RepoId == repoId));
    }

    private const string LookupsFlow = """
        flowType: cache
        name: lookups
        source:
          headers: { data-partition-id: dev }
        types:
          - dictionary: UnitAlias
          - name: Curves
            dictionary: CurveDictionary
        """;

    [Fact]
    public async Task A_dictionary_type_is_recorded_as_its_document_makes_it_and_one_that_cannot_be_read_is_left_out()
    {
        Write("estate/cache/lookups.yaml", LookupsFlow);
        // A dictionary mentioning the word mapping is not a mapping document, and is never stored as one.
        Write("estate/dictionaries/UnitAlias.yaml", "# Not a mapping.\ndocumentType: dictionary\nname: UnitAlias\nentries:\n  M: m\n  NONE: ~\n");
        var repoId = Guid.NewGuid();

        var (warnings, result) = await SyncWithResultAsync(repoId);

        var warning = Assert.Single(warnings);
        Assert.StartsWith("estate/cache/lookups.yaml: Curves is left out of the cache of partition 'dev', because its dictionary could not be read:", warning, StringComparison.Ordinal);
        Assert.Contains("Dictionary 'CurveDictionary' was not found under 'estate/dictionaries'", warning, StringComparison.Ordinal);
        Assert.Equal(1, result.Invalid);

        await using var db = _catalog.CreateDbContext();
        var units = await db.DeliveryCacheDefinitions.SingleAsync(c => c.RepoId == repoId);
        Assert.Equal("UnitAlias", units.Name);
        Assert.Equal("dictionary", units.Origin);
        Assert.Equal("lookup--UnitAlias", units.EntityType);
        Assert.Equal("key", units.KeyField);
        Assert.Equal("estate/dictionaries/UnitAlias.yaml", units.DictionaryPath);
        Assert.Null(units.Endpoint);
        Assert.Null(units.Kind);
        Assert.Null(units.Query);
        using (var fields = JsonDocument.Parse(units.FieldsJson))
        {
            Assert.Equal(["value"], fields.RootElement.EnumerateArray().Select(f => f.GetProperty("as").GetString()));
        }

        Assert.Empty(await db.DeliveryMappings.Where(m => m.RepoId == repoId).ToListAsync());
        var declaration = await OsduCacheStore.DeclarationAsync(db, "dev");
        Assert.Equal(CacheOrigin.Dictionary, Assert.Single(declaration.Of("UnitAlias")).Origin);

        // Once the dictionary is there, the next sync records it; a dictionary that changes its fields changes the row.
        Write("estate/dictionaries/CurveDictionary.yaml", "documentType: dictionary\nname: CurveDictionary\nkey: mnemonic\nfields: [family]\nentries:\n  GR: { family: Gamma Ray }\n");
        Assert.Empty(await SyncAsync(repoId));
        var curves = await db.DeliveryCacheDefinitions.AsNoTracking().SingleAsync(c => c.RepoId == repoId && c.Name == "Curves");
        Assert.Equal("mnemonic", curves.KeyField);
        Assert.Equal("estate/dictionaries/CurveDictionary.yaml", curves.DictionaryPath);
    }

    [Fact]
    public async Task A_lookup_table_declared_by_two_cache_flows_of_a_partition_is_left_out_of_the_second()
    {
        Write("cache/lookups.yaml", LookupsFlow);
        Write("cache/more-lookups.yaml", LookupsFlow.Replace("name: lookups", "name: more-lookups", StringComparison.Ordinal));
        Write("dictionaries/UnitAlias.yaml", "documentType: dictionary\nname: UnitAlias\nentries:\n  M: m\n");
        Write("dictionaries/CurveDictionary.yaml", "documentType: dictionary\nname: CurveDictionary\nentries:\n  GR: Gamma Ray\n");
        var repoId = Guid.NewGuid();

        var warnings = await SyncAsync(repoId);

        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("a lookup table from a table or a dictionary is declared by one cache flow of a partition only", w, StringComparison.Ordinal));
        await using var db = _catalog.CreateDbContext();
        Assert.Equal(["lookups", "lookups"], await db.DeliveryCacheDefinitions.Where(c => c.RepoId == repoId).Select(c => c.FlowName).ToListAsync());
    }

    [Fact]
    public async Task A_mapping_after_a_byte_order_mark_is_still_found_and_a_document_naming_mapping_elsewhere_is_not()
    {
        Write("mappings/Thing@1.0.0.yaml", "﻿" + TestSchema.MappingDocument());
        Write("notes.yaml", "documentType: notes\ndescription: how the mapping works\n");
        var repoId = Guid.NewGuid();

        await SyncAsync(repoId);

        await using var db = _catalog.CreateDbContext();
        Assert.Equal(["mappings/Thing@1.0.0.yaml"], await db.DeliveryMappings.Where(m => m.RepoId == repoId).Select(m => m.RelativePath).ToListAsync());
    }

    [Fact]
    public async Task A_cache_flow_s_retention_reaches_the_catalog_and_a_change_to_it_is_synced()
    {
        Write("cache/a.yaml", Flow("project-a-cache", AliasedWellboreAndUnits).Replace("types:", "retentionDays: 30\ntypes:", StringComparison.Ordinal));
        Write("cache/b.yaml", Flow("project-b-cache", FacilityWellbore));
        var repoId = Guid.NewGuid();
        Assert.Empty(await SyncAsync(repoId));

        await using (var db = _catalog.CreateDbContext())
        {
            // Every type a flow declares carries the flow's retention; a flow that declares none keeps seven days.
            var rows = await db.DeliveryCacheDefinitions.AsNoTracking().Where(c => c.RepoId == repoId).ToListAsync();
            Assert.All(rows.Where(r => r.FlowName == "project-a-cache"), r => Assert.Equal(30, r.RetentionDays));
            Assert.All(rows.Where(r => r.FlowName == "project-b-cache"), r => Assert.Equal(CacheRetention.DefaultDays, r.RetentionDays));

            // The partition keeps the longest of the two.
            Assert.Equal(30, (await OsduCacheStore.DeclarationAsync(db, "dev")).RetentionDays);
        }

        Write("cache/a.yaml", Flow("project-a-cache", AliasedWellboreAndUnits));
        var (_, result) = await SyncWithResultAsync(repoId);
        Assert.True(result.Updated > 0);
        await using (var db = _catalog.CreateDbContext())
        {
            Assert.All(await db.DeliveryCacheDefinitions.AsNoTracking().Where(c => c.RepoId == repoId).ToListAsync(), r => Assert.Equal(CacheRetention.DefaultDays, r.RetentionDays));
            Assert.Equal(CacheRetention.DefaultDays, (await OsduCacheStore.DeclarationAsync(db, "dev")).RetentionDays);
        }
    }

    [Fact]
    public async Task A_cache_another_repository_declares_under_the_same_name_is_reported()
    {
        Write("cache/welldb-osdu-00-reference-cache.yaml", CacheFlow);
        Assert.Empty(await SyncAsync(Guid.NewGuid()));

        var other = Path.Combine(_root, "other");
        Write("welldb-osdu-00-reference-cache.yaml", CacheFlow, other);
        var warnings = await SyncAsync(Guid.NewGuid(), other);
        Assert.Contains(warnings, w => w.Contains("also declared by another repository", StringComparison.Ordinal));
    }
}
