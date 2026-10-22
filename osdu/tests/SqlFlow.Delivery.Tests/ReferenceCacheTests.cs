using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The cache the mappings resolve against: what a captured path yields (a scalar, a set, a nested object), how the
/// cache is matched and read, and how a cache definition is declared in YAML.
/// </summary>
[Trait("Category", "Integration")]
public class ReferenceCacheTests
{
    private static JsonObject Hit(string json) => JsonNode.Parse(json) as JsonObject ?? throw new DeliveryException("not an object");

    [Fact]
    public void Path_selects_through_arrays_of_objects()
    {
        var hit = Hit("""
            {
              "id": "dev:master-data--Wellbore:1",
              "data": {
                "FacilityName": "WB 1/1-A",
                "NameAlias": [
                  { "AliasName": "1/1-A", "AliasNameTypeID": "dev:reference-data--AliasNameType:Short:" },
                  { "AliasName": "WELL A" }
                ],
                "Tags": { "source": "wells" }
              }
            }
            """);

        Assert.Equal("WB 1/1-A", Assert.Single(JsonPathReader.SelectNodes(hit, "data.FacilityName")).GetValue<string>());
        Assert.Equal(["1/1-A", "WELL A"], JsonPathReader.SelectNodes(hit, "data.NameAlias.AliasName").Select(n => n.GetValue<string>()));
        Assert.Equal("1/1-A", Assert.Single(JsonPathReader.SelectNodes(hit, "data.NameAlias[0].AliasName")).GetValue<string>());
        Assert.Equal("wells", Assert.Single(JsonPathReader.SelectNodes(hit, "data.Tags.source")).GetValue<string>());
        Assert.Empty(JsonPathReader.SelectNodes(hit, "data.NotThere"));
    }

    [Fact]
    public void Value_keeps_any_json_and_flattens_its_terms()
    {
        var set = ReferenceValue.From(JsonNode.Parse("""["1/1-A", "WELL A"]""")!);
        Assert.True(set.IsSet);
        Assert.Equal(2, set.Count);
        Assert.Equal(["1/1-A", "WELL A"], set.Terms);

        var scalar = ReferenceValue.From(JsonValue.Create(42));
        Assert.False(scalar.IsSet);
        Assert.Equal("42", scalar.Text);
        Assert.Equal(["42"], scalar.Terms);

        var nested = ReferenceValue.From(JsonNode.Parse("""{ "AliasName": "1/1-A", "Deep": { "Code": "A" } }""")!);
        Assert.Equal(["1/1-A", "A"], nested.Terms);
        Assert.Equal("1/1-A", nested.Select("AliasName")!.Text);
        Assert.Equal("A", nested.Select("Deep.Code")!.Text);
        Assert.Null(nested.Select("Missing"));
    }

    [Fact]
    public void A_record_is_found_by_the_code_its_id_ends_with_as_a_reference_writes_it_or_decoded()
    {
        static ReferenceItem Item(string id, string code) => new(id, new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["Code"] = ReferenceValue.Of(code),
        });

        const string GammaRay = "dev:reference-data--LogCurveFamily:Gamma%20Ray";
        const string Impedance = "dev:reference-data--LogCurveFamily:Local-Acoustic%20Impedance%20Compressional";
        var families = new ReferenceType("LogCurveFamily", "reference-data--LogCurveFamily",
            [Item(GammaRay, "Gamma Ray"), Item(Impedance, "Local-Acoustic Impedance Compressional")]);

        // A curve dictionary names a family by the code its id ends with, encoded as ids encode it: the record id, that
        // code and the code decoded all find the one record.
        Assert.Equal(GammaRay, families.Match("id", GammaRay)?.Id);
        Assert.Equal(GammaRay, families.Match("id", "Gamma%20Ray")?.Id);
        Assert.Equal(GammaRay, families.Match("id", "Gamma Ray")?.Id);
        Assert.Equal(Impedance, families.Match("id", "Local-Acoustic%20Impedance%20Compressional")?.Id);

        // Case is ignored only when that finds exactly one record, as for any other field, and a part of a code is no code.
        Assert.Equal(GammaRay, families.Match("id", "gamma%20ray")?.Id);
        Assert.Null(families.Match("id", "Gamma"));
        Assert.Null(families.Match("id", "reference-data--LogCurveFamily:Gamma%20Ray"));

        // A type caching a field of its own called ID is matched on that field under the name, never on its ids' codes.
        var units = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            new ReferenceItem("dev:reference-data--UnitOfMeasure:g%2Fcm3", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = ReferenceValue.Of("gcc"),
            }),
        ]);
        Assert.Null(units.Match("id", "g%2Fcm3"));
        Assert.Equal("dev:reference-data--UnitOfMeasure:g%2Fcm3", units.Match("id", "gcc")?.Id);
    }

    [Fact]
    public void A_set_matches_by_any_of_its_values()
    {
        var type = new ReferenceType("Wellbore", "master-data--Wellbore",
        [
            new ReferenceItem("dev:master-data--Wellbore:1", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["FacilityName"] = ReferenceValue.Of("WB 1/1-A"),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse("""["1/1-A", "WELL A"]""")!),
            }),
            new ReferenceItem("dev:master-data--Wellbore:2", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["FacilityName"] = ReferenceValue.Of("WB 2/2-B"),
            }),
        ]);

        Assert.Equal("dev:master-data--Wellbore:1", type.Match("Alias", "1/1-A")!.Id);
        Assert.Equal("dev:master-data--Wellbore:1", type.Match("Alias", " well a ")!.Id);
        Assert.Equal("dev:master-data--Wellbore:1", type.Match("data.FacilityName", "WB 1/1-A")!.Id);
        Assert.Equal("dev:master-data--Wellbore:2", type.Match("id", "dev:master-data--Wellbore:2")!.Id);
        Assert.Null(type.Match("Alias", "nothing"));
        Assert.True(type.HasField("Alias"));
        Assert.False(type.HasField("Nope"));
        Assert.Equal(["Alias", "FacilityName"], type.FieldNames);
    }

    [Fact]
    public void Several_records_holding_a_value_exactly_match_none_of_them_and_are_all_named()
    {
        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("a", new Dictionary<string, string> { ["Code"] = "m" }),
            ReferenceItem.FromText("b", new Dictionary<string, string> { ["Code"] = "m" }),
            ReferenceItem.FromText("c", new Dictionary<string, string> { ["Code"] = "ft" }),
        ]);

        // Taking the first would write a reference nobody chose, so the value resolves to neither and both are listed.
        Assert.Null(type.Match("Code", "m"));
        var found = type.Find("Code", "m");
        Assert.True(found.IsCaseAmbiguous);
        Assert.Equal(ReferenceMatchKind.Exact, found.Kind);
        Assert.Equal("exactly", found.Loosening);
        Assert.Equal(["a", "b"], found.CaseVariants.Select(v => v.Id));
        Assert.True(type.IsAmbiguous("Code"));

        // A value only one record holds still resolves.
        Assert.Equal("c", type.Match("Code", "ft")!.Id);
    }

    [Fact]
    public void Codes_that_differ_only_by_case_are_different_records()
    {
        // As a live partition's search returned them: the femtotesla before the foot.
        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("test:reference-data--UnitOfMeasure:fT", new Dictionary<string, string> { ["Code"] = "fT", ["Name"] = "femtotesla" }),
            ReferenceItem.FromText("test:reference-data--UnitOfMeasure:ft", new Dictionary<string, string> { ["Code"] = "ft", ["Name"] = "foot" }),
            ReferenceItem.FromText("test:reference-data--UnitOfMeasure:gAPI", new Dictionary<string, string> { ["Code"] = "gAPI", ["Name"] = "API gamma ray" }),
        ]);

        Assert.Equal("test:reference-data--UnitOfMeasure:ft", type.Match("Code", "ft")!.Id);
        Assert.Equal("test:reference-data--UnitOfMeasure:fT", type.Match("Code", "fT")!.Id);
        Assert.Equal("test:reference-data--UnitOfMeasure:ft", type.Match("id", "test:reference-data--UnitOfMeasure:ft")!.Id);

        // One item under folded case is still found, so an upper-case drop value keeps resolving.
        Assert.Equal("test:reference-data--UnitOfMeasure:gAPI", type.Match("Code", "GAPI")!.Id);

        // Two items under folded case: neither is chosen, and both are named.
        var undecided = type.Find("Code", "FT");
        Assert.Null(undecided.Item);
        Assert.True(undecided.IsCaseAmbiguous);
        Assert.Equal(["test:reference-data--UnitOfMeasure:fT", "test:reference-data--UnitOfMeasure:ft"], undecided.CaseVariants.Select(i => i.Id));
        Assert.Null(type.Match("Code", "FT"));
        Assert.False(type.IsAmbiguous("Code"));
    }

    [Fact]
    public void Items_survive_the_snapshot_round_trip_whatever_their_shape()
    {
        var type = new ReferenceType("Wellbore", "master-data--Wellbore",
        [
            new ReferenceItem("dev:master-data--Wellbore:1", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["FacilityName"] = ReferenceValue.Of("WB 1/1-A"),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse("""["1/1-A", "WELL A"]""")!),
                ["Depth"] = ReferenceValue.From(JsonValue.Create(1234.5)),
                ["Tags"] = ReferenceValue.From(JsonNode.Parse("""{ "source": "wells" }""")!),
            }),
        ]);

        var round = ReferenceType.FromJson("Wellbore", type.ToJson());
        var item = Assert.Single(round.Items);
        Assert.Equal(["1/1-A", "WELL A"], item.Select("Alias")!.Terms);
        Assert.Equal("1234.5", item.Select("Depth")!.Text);
        Assert.Equal("wells", item.Select("Tags.source")!.Text);
        Assert.Equal("dev:master-data--Wellbore:1", round.Value(item, "id")!.Text);
    }

    [Fact]
    public void Text_only_items_hash_as_they_did_before_sets_were_supported()
    {
        // The sample estate's snapshots carry recorded hashes, so a scalar capture must serialise unchanged.
        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Code"] = "m", ["Name"] = "metre" }),
        ]);

        Assert.Equal(
            """{"entityType":"reference-data--UnitOfMeasure","items":[{"Code":"m","Name":"metre","id":"dev:reference-data--UnitOfMeasure:m"}]}""",
            CanonicalJson.ToString(type.ToJson()));
    }

    [Fact]
    public void Index_is_safe_under_the_parallel_renders_of_one_run()
    {
        var items = Enumerable.Range(0, 500)
            .Select(i => ReferenceItem.FromText($"dev:reference-data--UnitOfMeasure:{i}", new Dictionary<string, string>
            {
                ["Code"] = $"C{i}",
                ["Name"] = $"N{i}",
                ["ID"] = $"I{i}",
            }))
            .ToList();
        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", items);

        Parallel.For(0, 512, i =>
        {
            var field = (i % 3) switch { 0 => "Code", 1 => "Name", _ => "ID" };
            var prefix = field switch { "Code" => "C", "Name" => "N", _ => "I" };
            var hit = type.Match(field, prefix + (i % 500));
            Assert.NotNull(hit);
        });
    }

    [Fact]
    public void A_value_thousands_of_records_share_names_each_record_once_and_indexes_in_linear_time()
    {
        // As a partition's wellbores hold their aliases: 14,297 of the dev partition's 163,887 wellbores list "1" among
        // theirs, some of them twice. Every record here holds it twice, beside aliases of its own for each matching tier.
        const int Records = 40_000;
        var type = new ReferenceType("Wellbore", "master-data--Wellbore", Enumerable.Range(0, Records)
            .Select(i => new ReferenceItem($"dev:master-data--Wellbore:{i:D6}", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Alias"] = ReferenceValue.From(new JsonArray("1", $"own {i}", "1", $"OWN {i}", $"WB {i}/1-A")),
            }))
            .ToList());
        var inOrder = type.Items.Select(item => item.Id).ToList();

        // The three indexes over the field (exact, folded case, folded separators) are built here, each once. Built by a pass
        // over the records each time a record joins a shared value, as they once were, they take minutes at this size.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var shared = type.Find("Alias", "1");
        var everyHolder = type.FindAll("Alias", "1");
        var bySeparators = type.Find("Alias", "WB_17_1_A", ignoreSeparators: true);
        var sharedBySeparators = type.Find("Alias", "-1-", ignoreSeparators: true);
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Indexing {Records} records sharing one value took {clock.Elapsed}.");

        // A value every record holds names every record, once each, in snapshot order, and matches none of them.
        Assert.Null(shared.Item);
        Assert.Equal(ReferenceMatchKind.Exact, shared.Kind);
        Assert.Equal(inOrder, shared.CaseVariants.Select(item => item.Id));
        Assert.Equal(inOrder, everyHolder.Select(item => item.Id));
        Assert.True(type.IsAmbiguous("Alias"));

        // A value of one record still finds it at each tier, however many spellings of it the record holds.
        Assert.Equal("dev:master-data--Wellbore:000017", type.Match("Alias", "own 17")!.Id);
        Assert.Equal("dev:master-data--Wellbore:000017", type.Match("Alias", "Own 17")!.Id);
        Assert.Equal(ReferenceMatchKind.IgnoringSeparators, bySeparators.Kind);
        Assert.Equal("dev:master-data--Wellbore:000017", bySeparators.Item!.Id);
        Assert.Equal(ReferenceMatchKind.IgnoringSeparators, sharedBySeparators.Kind);
        Assert.Equal(inOrder, sharedBySeparators.CaseVariants.Select(item => item.Id));
    }

    [Fact]
    public void Field_names_normalise_the_data_prefix()
    {
        Assert.Equal("Code", ReferenceField.Normalize("data.Code"));
        Assert.Equal("NameAlias.AliasName", ReferenceField.Normalize("$.data.NameAlias.AliasName"));
        Assert.Equal("legal.legaltags", ReferenceField.Normalize("legal.legaltags"));
        Assert.True(ReferenceField.IsId("data.id"));
        Assert.Equal([("NameAlias", "AliasName")], ReferenceField.Prefixes("data.NameAlias.AliasName"));
    }

    /// <summary>A cache flow over the given <c>types:</c> block (list items indented by two spaces).</summary>
    private static CacheDefinition CacheFlow(string types, string extra = "") => new DeliveryDocumentLoader().ParseCache(
        """
        flowType: cache
        name: welldb-osdu-00-reference-cache
        source:
          endpoint: https://osdu.example.com
          headers: { data-partition-id: dev }

        """ + extra + "\ntypes:\n" + types + "\n",
        "cache/welldb-osdu-00-reference-cache.yaml");

    [Fact]
    public void A_cache_flow_reads_paths_written_either_way()
    {
        var cache = CacheFlow("""
              - kind: "osdu:wks:master-data--Wellbore:*"
                name: Wellbore
                fields:
                  - data.FacilityName
                  - path: data.NameAlias.AliasName
                    as: Alias
            """);

        var type = Assert.Single(cache.Types);
        Assert.Equal(["FacilityName", "Alias"], type.Fields.Select(f => f.Name));
        Assert.Equal(["data.FacilityName", "data.NameAlias.AliasName"], type.Fields.Select(f => f.Path));
    }

    [Fact]
    public void A_cache_flow_refuses_two_paths_under_one_name()
    {
        var ex = Assert.Throws<FlowValidationException>(() => CacheFlow("""
              - kind: "osdu:wks:master-data--Wellbore:*"
                name: Wellbore
                fields:
                  - data.FacilityName
                  - path: data.NameAlias.AliasName
                    as: FacilityName
            """));
        Assert.Contains("two paths under the name 'FacilityName'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cached_field_called_ID_is_allowed_and_shadows_the_record_id()
    {
        // OSDU reference data carries data.ID, and the sample estate caches it. Only the exact key 'id' collides.
        var cache = CacheFlow("""
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields: [data.Code, data.ID]
            """);
        Assert.Equal(["Code", "ID"], Assert.Single(cache.Types).Fields.Select(f => f.Name));

        var type = new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, string> { ["Code"] = "m", ["ID"] = "metre-id" }),
        ]);
        Assert.Equal("dev:reference-data--UnitOfMeasure:m", type.Match("ID", "metre-id")!.Id);
        Assert.Null(type.Match("id", "dev:reference-data--UnitOfMeasure:m"));
        Assert.False(type.MeansRecordId("id"));

        var ex = Assert.Throws<FlowValidationException>(() => CacheFlow("""
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields:
                  - path: data.Code
                    as: id
            """));
        Assert.Contains("the key the record id is written under", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cache_flow_declares_what_the_cache_holds()
    {
        var cache = CacheFlow(
            """
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                onChange: auto
                fields: [data.FacilityName]
              - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
                query: data.Code:m*
                fields: [data.Code]
            """,
            "onChange: approve\n");

        Assert.Equal("welldb-osdu-00-reference-cache", cache.Name);
        Assert.Equal("dev", cache.Scope);
        Assert.Equal("https://osdu.example.com", cache.Source.Endpoint);
        var wellbore = cache.Types[0];
        Assert.Equal("Wellbore", wellbore.Name);
        Assert.Equal("master-data--Wellbore", wellbore.EntityType);
        Assert.Equal("*", wellbore.Query);
        Assert.Equal(CacheChangeMode.Auto, wellbore.OnChange);
        var units = cache.Types[1];
        Assert.Equal("UnitOfMeasure", units.Name);
        Assert.Equal("data.Code:m*", units.Query);
        Assert.Equal(CacheChangeMode.Approve, units.OnChange);
    }

    [Fact]
    public void A_changed_cached_value_goes_out_on_the_next_run_unless_a_type_opts_into_approval()
    {
        var cache = CacheFlow(
            """
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                fields: [data.FacilityName]
              - kind: osdu:wks:reference-data--UnitOfMeasure:1.0.0
                onChange: approve
                fields: [data.Code]
            """);

        Assert.Equal(CacheChangeMode.Auto, cache.OnChange);
        Assert.Equal(CacheChangeMode.Auto, cache.Types[0].OnChange);
        Assert.Equal(CacheChangeMode.Approve, cache.Types[1].OnChange);
    }

    [Fact]
    public void A_cached_type_names_its_kind_and_a_query_uses_only_declared_parameters()
    {
        var noKind = Assert.Throws<FlowValidationException>(() => CacheFlow("""
              - name: Wellbore
                entityType: master-data--Wellbore
                fields: [data.FacilityName]
            """));
        Assert.Contains("types[0] needs a kind (the OSDU kind whose records the type caches), a dictionary", noKind.Message, StringComparison.Ordinal);

        var token = Assert.Throws<FlowValidationException>(() => CacheFlow("""
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                query: data.Country:{country}
                fields: [data.FacilityName]
            """));
        Assert.Contains("'{country}', which is not declared under parameters", token.Message, StringComparison.Ordinal);

        var none = Assert.Throws<FlowValidationException>(() => CacheFlow(string.Empty));
        Assert.Contains("types is required", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retrieval_flow_declares_no_cache()
    {
        // What is cached is defined by a cache flow alone; a cache section on a retrieval flow is an unknown key.
        Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseRetrieval("""
            flowType: retrieval
            name: osdu-metadata-sync
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: dev }
              kinds: [osdu:wks:master-data--Wellbore:1.0.0]
            target:
              location: lake/metadata
            cache:
              types:
                - fields: [data.FacilityName]
            """, "sync.yaml"));
    }

    [Fact]
    public void A_cache_flow_names_its_partition_and_no_longer_takes_makeCurrent()
    {
        var referenced = new DeliveryDocumentLoader().ParseCache(
            """
            flowType: cache
            name: env-cache
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: " ${env:OSDU_PARTITION} " }
            types:
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                fields: [data.FacilityName]
            """,
            "cache/env-cache.yaml");
        Assert.Equal("${env:OSDU_PARTITION}", referenced.Scope);

        var makeCurrent = Assert.Throws<FlowValidationException>(() => CacheFlow(
            """
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                fields: [data.FacilityName]
            """,
            "makeCurrent: true\n"));
        Assert.Contains("makeCurrent is not a setting any more", makeCurrent.Message, StringComparison.Ordinal);

        var slashed = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseCache(
            """
            flowType: cache
            name: bad-partition
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: open/des }
            types:
              - kind: osdu:wks:master-data--Wellbore:1.0.0
                fields: [data.FacilityName]
            """,
            "cache/bad-partition.yaml"));
        Assert.Contains("data-partition-id 'open/des' is neither a partition id", slashed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_delivery_flow_names_no_cache_and_pins_a_version_of_its_partition_s_cache()
    {
        var sample = File.ReadAllText(Samples.Flow);
        var loader = new DeliveryDocumentLoader();
        Assert.Equal(FlowRender.CurrentCacheVersion, loader.ParseFlow(sample, "flow.yaml").Render.CacheVersion);

        var pinned = loader.ParseFlow(sample.Replace("mapping: WellLog@1.4.0", "mapping: WellLog@1.4.0\n  cacheVersion: 20260908T212727Z", StringComparison.Ordinal), "flow.yaml");
        Assert.Equal("20260908T212727Z", pinned.Render.CacheVersion);

        // A flow still naming a cache is refused: the cache it reads is its partition's, whatever it names.
        var named = Assert.Throws<FlowValidationException>(() => loader.ParseFlow(
            sample.Replace("mapping: WellLog@1.4.0", "mapping: WellLog@1.4.0\n  cache: welldb-osdu-00-reference-cache", StringComparison.Ordinal), "flow.yaml"));
        Assert.Contains("render.cache is not a setting any more", named.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mapping_that_reads_the_cache_renders_against_the_cache_of_the_partition_the_flow_delivers_to()
    {
        var engine = Samples.Engine(ledger: null);
        var values = new Dictionary<string, string> { ["logSource"] = "COMPOSITE" };
        var flow = Samples.LocalFlow(Samples.NewTempDirectory());

        using (var runtime = await FlowRuntime.CreateAsync(engine, flow, values))
        {
            Assert.Equal(Samples.SampleCacheScope, runtime.Mapping.Context.CacheScope);
            Assert.Equal("20260908T212727Z", runtime.Mapping.Context.CacheVersion);
            Assert.Contains($"\"cache\":\"{Samples.SampleCacheScope}\"", runtime.Mapping.Context.Canonical(), StringComparison.Ordinal);
        }

        // render.cacheVersion pins a version of the partition's cache.
        using (var runtime = await FlowRuntime.CreateAsync(engine, flow with { Render = flow.Render with { CacheVersion = "20260908T212727Z" } }, values))
        {
            Assert.Equal(Samples.SampleCacheScope, runtime.Mapping.Context.CacheScope);
            Assert.Equal("20260908T212727Z", runtime.Mapping.Context.CacheVersion);
        }

        var elsewhere = flow with
        {
            Target = flow.Target with { Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = "no-such-partition" } },
        };
        var empty = await Assert.ThrowsAsync<FlowValidationException>(() => FlowRuntime.CreateAsync(engine, elsewhere, values));
        Assert.Contains("reads the cache of partition 'no-such-partition', which holds no version yet", empty.Message, StringComparison.Ordinal);

        var unpartitioned = flow with { Target = flow.Target with { Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) } };
        var none = await Assert.ThrowsAsync<FlowValidationException>(() => FlowRuntime.CreateAsync(engine, unpartitioned, values));
        Assert.Contains("declare no 'data-partition-id'", none.Message, StringComparison.Ordinal);

        var missingVersion = await Assert.ThrowsAsync<FlowValidationException>(() => FlowRuntime.CreateAsync(engine, flow with { Render = flow.Render with { CacheVersion = "19990101T000000Z" } }, values));
        Assert.Contains($"pins version 19990101T000000Z of the cache of partition '{Samples.SampleCacheScope}', which the catalog does not hold", missingVersion.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sample mapping once declared the source unit `V/V` as `%`, so a neutron porosity of 0.21 was published as 0.21
    /// percent, a hundredth of what the curve carries. `V/V` is a volume fraction, which the well database's unit spellings
    /// translate to OSDU's `m3/m3` (cubic metre per cubic metre). The sample mapping's own curve unit entry is rendered here
    /// against the sample template and cache, through the curve unit map it reads from the cache, so the map and the cached
    /// reference data have to agree for this to pass, and the rendered id is checked as well, so a map that resolved to
    /// another unit the cache holds would not pass either.
    /// </summary>
    [Fact]
    public async Task The_sample_mapping_renders_the_porosity_unit_as_a_volume_fraction()
    {
        var mapping = new MappingCatalog(Samples.Mappings, new DeliveryDocumentLoader()).Load("WellLog@1.4.0");
        var curveUnit = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Curves[].CurveUnit");
        Assert.Equal("UnitAlias", curveUnit.Modifiers.Single(m => m.Kind == ModifierKind.Replace).Table!.CacheType);
        var units = Samples.SampleLookups().Single(t => t.Name == "UnitAlias");
        Assert.Equal("m3/m3", units.Match("source_unit", "V/V")!.Select("osdu_unit")!.Text);

        var version = await Samples.SampleCache.CurrentVersionAsync(Samples.SampleCacheScope);
        Assert.NotNull(version);
        var references = await Samples.SampleCache.LoadAsync(Samples.SampleCacheScope, version);
        Assert.NotNull(references);
        var schema = await Samples.SampleTemplates.LoadAsync(mapping.Template);
        Assert.NotNull(schema);

        var context = new RenderContext
        {
            MappingReference = mapping.Reference,
            CacheScope = Samples.SampleCacheScope,
            CacheVersion = references.Version,
            SchemaSnapshotVersion = schema.Version,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RenderContext.DataPartitionParameter] = "dev",
                ["aclOwner"] = "data.welldb.owners@dev.dataservices.energy",
                ["aclViewer"] = "data.welldb.viewers@dev.dataservices.energy",
                ["legalTag"] = "dev-osdu-default-legal",
            },
        };
        // The wellbore the log belongs to is searched for, and the sample platform holds it.
        var searches = await RenderResolver.SearchesAsync(Samples.SampleTemplates, mapping, schema);
        var log = SampleWellLogs.Logs().Single(l => l.LogId == "LOG-0005/1");
        var search = new FixedRecordSearch([("data.FacilityName", log.WellboreUwi, "dev:master-data--Wellbore:" + FixedRecordSearchFactory.WellboreId(log.WellboreUwi))]);
        var renderer = new MappingRenderer(mapping, schema, references, context, searches, search);
        // The neutron porosity of Wellbore A-1, as the well database describes it, in place of the log's own curves.
        var nphi = SampleWellLogs.CurveRows(SampleWellLogs.Logs().Single(l => l.LogId == "LOG-0001/1")).Single(c => c["curve_id"] == "NPHI");
        var porosity = await Samples.RenderSettledAsync(renderer, new SourceRecord
        {
            Row = SourceRow.FromStrings(SampleWellLogs.LogRow(log)),
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase)
            {
                ["curves"] = [SourceRow.FromStrings(nphi.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase))],
            },
        });

        Assert.False(porosity.IsHeld, string.Join("; ", porosity.Holds));
        var curve = porosity.Document["data"]!["Curves"]!.AsArray().Single(c => c!["CurveID"]!.GetValue<string>() == "NPHI");
        Assert.Equal("dev:reference-data--UnitOfMeasure:m3%2Fm3:", curve!["CurveUnit"]!.GetValue<string>());
    }
}

/// <summary>
/// What several cache flows of one partition declare, taken together: the partition a flow's headers name, the paths the
/// cache keeps for a type, what a change to it does, and the declarations that cannot be merged.
/// </summary>
public class CacheDeclarationTests
{
    private static readonly ReferenceFieldSpec Facility = new("data.FacilityName");

    private static readonly ReferenceFieldSpec Alias = new("data.NameAlias.AliasName", "Alias");

    private static CacheTypeDeclaration Wellbore(string flow, CacheChangeMode onChange, params ReferenceFieldSpec[] fields)
        => new(flow, "Wellbore", "master-data--Wellbore", "osdu:wks:master-data--Wellbore:*", "*", fields, onChange);

    private static ReferenceTypeSpec WellboreSpec(string entityType = "master-data--Wellbore", params ReferenceFieldSpec[] fields) => new()
    {
        Name = "Wellbore",
        EntityType = entityType,
        Kind = "osdu:wks:" + entityType + ":*",
        Fields = fields,
    };

    private static CacheDeclaration Partition() => new("dev",
    [
        Wellbore("project-b", CacheChangeMode.Approve, Facility, Alias),
        Wellbore("project-a", CacheChangeMode.Auto, Facility),
        new CacheTypeDeclaration("project-a", "UnitOfMeasure", "reference-data--UnitOfMeasure", "osdu:wks:reference-data--UnitOfMeasure:*", "*", [new("data.Code")], CacheChangeMode.Auto),
    ]);

    [Fact]
    public void The_scope_of_a_flow_is_the_partition_its_headers_carry()
    {
        Assert.Equal("dev", CacheScope.Of(new Dictionary<string, string>(StringComparer.Ordinal) { ["Data-Partition-Id"] = " dev " }, "flow.yaml"));
        Assert.Equal("${keyvault:partition}", CacheScope.Of(new Dictionary<string, string>(StringComparer.Ordinal) { ["data-partition-id"] = "${keyvault:partition}" }, "flow.yaml"));

        var none = Assert.Throws<FlowValidationException>(() => CacheScope.Of(new Dictionary<string, string>(StringComparer.Ordinal), "flow.yaml"));
        Assert.Contains("the headers declare no 'data-partition-id'", none.Message, StringComparison.Ordinal);
        var spaced = Assert.Throws<FlowValidationException>(() => CacheScope.Normalize("open des", "flow.yaml"));
        Assert.Contains("is neither a partition id", spaced.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_type_s_changes_wait_for_approval_when_any_flow_declaring_it_asks_for_that()
    {
        var declaration = Partition();
        Assert.Equal(CacheChangeMode.Approve, declaration.ModeOf("Wellbore", CacheChangeMode.Auto));
        Assert.Equal(CacheChangeMode.Approve, declaration.ModeOf("wellbore", CacheChangeMode.Auto));
        Assert.Equal(CacheChangeMode.Auto, declaration.ModeOf("UnitOfMeasure", CacheChangeMode.Approve));

        // A type no flow declares keeps the mode the capturing flow gives it.
        Assert.Equal(CacheChangeMode.Approve, declaration.ModeOf("VerticalMeasurementType", CacheChangeMode.Approve));
        Assert.Equal(CacheChangeMode.Auto, CacheDeclaration.None("dev").ModeOf("Wellbore", CacheChangeMode.Auto));
    }

    [Fact]
    public void The_cache_keeps_every_path_any_flow_declares_and_a_capture_is_widened_to_them()
    {
        var declaration = Partition();
        Assert.Equal(["project-a", "project-b"], declaration.Of("Wellbore").Select(d => d.FlowName));
        Assert.Equal(["FacilityName", "Alias"], declaration.FieldsOf("Wellbore").Select(f => f.Name));
        Assert.Equal(["UnitOfMeasure", "Wellbore"], declaration.TypeNames.Order(StringComparer.Ordinal));
        Assert.Empty(declaration.FieldsOf("VerticalMeasurementType"));

        // A flow's own paths come first; what the other flows keep follows.
        var widened = declaration.Widen(WellboreSpec(fields: [new ReferenceFieldSpec("data.WellID", "Well"), Facility]));
        Assert.Equal(["Well", "FacilityName", "Alias"], widened.Fields.Select(f => f.Name));
        Assert.Equal(["data.WellID", "data.FacilityName", "data.NameAlias.AliasName"], widened.Fields.Select(f => f.Path));

        // Widening keeps everything else the flow declares about the type, and a partition declaring nothing adds nothing.
        Assert.Equal(("master-data--Wellbore", "osdu:wks:master-data--Wellbore:*"), (widened.EntityType, widened.Kind));
        Assert.Equal(["FacilityName"], CacheDeclaration.None("dev").Widen(WellboreSpec(fields: Facility)).Fields.Select(f => f.Name));
    }

    [Fact]
    public void Declarations_that_would_hold_two_meanings_under_one_name_conflict()
    {
        var declaration = Partition();

        // Agreeing with the others, and adding a path of its own, is no conflict; nor is a flow's own earlier declaration.
        Assert.Empty(declaration.Conflicts("project-c", WellboreSpec(fields: [Facility, new ReferenceFieldSpec("data.WellID", "Well")])));
        Assert.Empty(new CacheDeclaration("dev", [Wellbore("project-b", CacheChangeMode.Auto, Facility)]).Conflicts("project-b", WellboreSpec("master-data--Well", Facility)));
        Assert.Empty(declaration.Conflicts("project-c", new ReferenceTypeSpec { Name = "VerticalMeasurementType", EntityType = "reference-data--VerticalMeasurementType", Kind = "osdu:wks:reference-data--VerticalMeasurementType:*", Fields = [new ReferenceFieldSpec("data.Code")] }));

        var entityType = declaration.Conflicts("project-c", WellboreSpec("master-data--Well", Facility));
        Assert.Contains("cache flow 'project-a' declares Wellbore as master-data--Wellbore, and 'project-c' declares it as master-data--Well", entityType);
        Assert.Contains("cache flow 'project-b' declares Wellbore as master-data--Wellbore, and 'project-c' declares it as master-data--Well", entityType);

        var path = declaration.Conflicts("project-c", WellboreSpec(fields: new ReferenceFieldSpec("data.Name", "FacilityName")));
        Assert.Equal(
            [
                "Wellbore.FacilityName is cached from data.FacilityName by cache flow 'project-a' and from data.Name by 'project-c'",
                "Wellbore.FacilityName is cached from data.FacilityName by cache flow 'project-b' and from data.Name by 'project-c'",
            ],
            path);
    }
}

/// <summary>Cache sources that read a field out of the cached record rather than its id: building a document out of what the cache holds.</summary>
public class CachedLookupTests
{
    private static ReferenceSnapshot References() => new("refs-1", DateTimeOffset.UnixEpoch,
    [
        new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
        [
            new ReferenceItem("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = ReferenceValue.Of("m"),
                ["Name"] = ReferenceValue.Of("metre"),
                ["Symbol"] = ReferenceValue.Of("m"),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse("""["meter", "metre"]""")!),
                ["Persistable"] = ReferenceValue.From(JsonNode.Parse("""{ "Scale": { "Code": "SI" } }""")!),
            }),
        ]),
        new ReferenceType("Wellbore", "master-data--Wellbore",
        [
            ReferenceItem.FromText("dev:master-data--Wellbore:abc", new Dictionary<string, string> { ["FacilityName"] = "WB 1/1-A" }),
        ]),
    ]);

    private static MappingRenderer Renderer(string entries)
        => new(TestSchema.Mapping(entries), TestSchema.Build(), References(), TestSchema.Context());

    private static SourceRecord Record(string unit = "m") => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "well-1", ["depth"] = "12.5", ["unit"] = unit }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
    };

    private static string Read(string property, string field, string findBy = "Code") => $$"""
          {{property}}:
            $cache: UnitOfMeasure.{{field}}
            $findBy: {{findBy}} = unit
        """;

    [Fact]
    public void Reads_a_cached_scalar_into_the_document()
    {
        var result = Renderer(Read("Symbol", "Symbol")).Render(Record());
        Assert.False(result.IsHeld);
        Assert.Equal("m", result.Document["data"]!["Symbol"]!.GetValue<string>());
    }

    [Fact]
    public void Reads_a_cached_set_into_an_array()
    {
        var result = Renderer(Read("Aliases", "Alias")).Render(Record());
        Assert.False(result.IsHeld);
        Assert.Equal(["meter", "metre"], result.Document["data"]!["Aliases"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Reads_a_path_inside_a_cached_object()
    {
        var result = Renderer(Read("Symbol", "Persistable.Scale.Code")).Render(Record());
        Assert.False(result.IsHeld);
        Assert.Equal("SI", result.Document["data"]!["Symbol"]!.GetValue<string>());
    }

    [Fact]
    public void A_scalar_target_wraps_into_an_array_and_a_set_holds()
    {
        var single = Renderer(Read("Aliases", "Name")).Render(Record());
        Assert.Equal(["metre"], single.Document["data"]!["Aliases"]!.AsArray().Select(n => n!.GetValue<string>()));

        var set = Renderer(Read("Symbol", "Alias")).Render(Record());
        Assert.True(set.IsHeld);
        Assert.Contains(set.Holds, h => h.Contains("2 values were given but the template takes one string", StringComparison.Ordinal));
    }

    [Fact]
    public void An_uncached_field_holds_the_record_and_names_what_is_cached()
    {
        var result = Renderer(Read("Symbol", "NotCached")).Render(Record());
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h =>
            h.Contains("caches nothing at 'NotCached'", StringComparison.Ordinal) && h.Contains("Cached: id, Alias, Code", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missed_match_holds_exactly_as_reading_the_id_does()
    {
        var result = Renderer(Read("Symbol", "Symbol")).Render(Record(unit: "furlong"));
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains("no UnitOfMeasure matches 'furlong'", StringComparison.Ordinal));
    }

    [Fact]
    public void Preflight_rejects_a_field_the_cache_cannot_answer()
    {
        var issues = Preflight.Check(TestSchema.Mapping(Read("Symbol", "NotCached")), TestSchema.Build(), References(), TestSchema.Context(), sourceColumns: null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("reads 'NotCached' out of UnitOfMeasure", StringComparison.Ordinal));
    }

    [Fact]
    public void Preflight_rejects_matching_by_fields_the_cache_does_not_hold()
    {
        var issues = Preflight.Check(TestSchema.Mapping(Read("Unit", "id", findBy: "NotCached")), TestSchema.Build(), References(), TestSchema.Context(), sourceColumns: null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("caches none of those", StringComparison.Ordinal));
    }
}
