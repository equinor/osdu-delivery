using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Lookup tables in the partition's cache: types whose rows are not OSDU records, filled from an ingestion table or a
/// dictionary document, kept under their keys, and read by a mapping the way any cached type is read.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class LookupCacheTests : IDisposable
{
    private const string Scope = "dev";

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly CacheCapture Capture = new(null, "cli:tester", "dictionary dictionaries/UnitAlias.yaml");

    private readonly OsduTestDatabase _catalog = new();

    public void Dispose() => _catalog.Dispose();

    /// <summary>A lookup table of rows keyed by their first value, each carrying the key under <paramref name="key"/> and its other values.</summary>
    internal static ReferenceType Lookup(string name, string key, params (string Key, IReadOnlyDictionary<string, string?> Values)[] rows) => new(
        name,
        ReferenceType.LookupEntityType(name),
        rows.Select(row =>
        {
            var fields = new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase) { [key] = ReferenceValue.Of(row.Key) };
            foreach (var (field, value) in row.Values)
            {
                if (value is not null)
                {
                    fields[field] = ReferenceValue.Of(value);
                }
            }

            return new ReferenceItem(row.Key, fields);
        }),
        key);

    /// <summary>A dictionary of pairs, as a dictionary document of scalar entries is cached: a key and a value.</summary>
    internal static ReferenceType Pairs(string name, params (string From, string? To)[] pairs)
        => Lookup(name, "key", pairs.Select(p => (p.From, (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["value"] = p.To })).ToArray());

    private static ReferenceSnapshot WithLookups(params ReferenceType[] lookups)
        => new("refs-1", T0, TestSchema.References().Types.Concat(lookups));

    [Fact]
    public void A_lookup_table_is_matched_on_its_key_and_its_fields_and_is_kept_under_a_lookup_entity_type()
    {
        var units = Pairs("UnitAlias", ("M", "m"), ("METRES.", "m"), ("ft", "ft"), ("fT", "fT"), ("NONE", null));
        Assert.True(units.IsLookup);
        Assert.Equal("lookup--UnitAlias", units.EntityType);
        Assert.Equal("key", units.Key);

        Assert.Equal("m", units.Value(units.Match("key", "metres.")!, "value")!.Text);
        Assert.Equal("fT", units.Match("key", "fT")!.Id);
        Assert.Null(units.Match("key", "FT"));
        Assert.True(units.Find("key", "FT").IsCaseAmbiguous);
        Assert.Null(units.Value(units.Match("key", "NONE")!, "value"));

        // The key is written only for a lookup table, so a type of OSDU records hashes exactly as it did before lookups.
        var json = units.ToJson();
        Assert.Equal("key", json["key"]!.GetValue<string>());
        var back = ReferenceType.FromJson("UnitAlias", json);
        Assert.Equal("key", back.Key);
        Assert.Equal(units.Items.Select(i => i.Id), back.Items.Select(i => i.Id));
        Assert.Null(TestSchema.References().Type("UnitOfMeasure")!.ToJson()["key"]);
        Assert.False(TestSchema.References().Type("UnitOfMeasure")!.IsLookup);

        // A lookup table always names its key, and a type of OSDU records never does.
        Assert.Throws<DeliveryException>(() => new ReferenceType("UnitAlias", "lookup--UnitAlias", []));
        Assert.Throws<DeliveryException>(() => new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", [], "key"));
    }

    [Fact]
    public void Lookup_keys_are_trimmed_non_empty_short_and_never_called_id()
    {
        Assert.Null(LookupKeys.Problem("METRES."));
        Assert.Null(LookupKeys.Problem("IMP_AI"));
        Assert.Null(LookupKeys.Problem("fT"));
        Assert.Null(LookupKeys.Problem("dev:reference-data--UnitOfMeasure:m"));
        Assert.Equal("is empty", LookupKeys.Problem("   "));
        Assert.Equal("is empty", LookupKeys.Problem(null));
        Assert.Contains("has spaces around it", LookupKeys.Problem(" M"), StringComparison.Ordinal);
        Assert.Contains("has spaces around it", LookupKeys.Problem("M "), StringComparison.Ordinal);
        Assert.Contains("longer than the 256 characters", LookupKeys.Problem(new string('x', 257)), StringComparison.Ordinal);
        Assert.Null(LookupKeys.Problem(new string('x', 256)));
        Assert.Contains("control character", LookupKeys.Problem("a\tb"), StringComparison.Ordinal);

        Assert.True(LookupKeys.IsIdName("ID"));
        Assert.True(LookupKeys.IsIdName(" id "));
        Assert.True(LookupKeys.IsIdName("Id"));
        Assert.False(LookupKeys.IsIdName("ident"));
    }

    [Fact]
    public void A_type_spec_keeps_to_the_settings_of_its_origin()
    {
        var table = new ReferenceTypeSpec
        {
            Name = "CurveClasses",
            EntityType = ReferenceType.LookupEntityType("CurveClasses"),
            Origin = CacheOrigin.Table,
            Table = "OsduData.arc.CurveDictionary",
            Key = "mnemonic",
            Fields = [new ReferenceFieldSpec("curve_family", "family"), new ReferenceFieldSpec("unit", "unit")],
        };
        table.Validate();
        Assert.True(table.IsLookup);
        Assert.Equal("table OsduData.arc.CurveDictionary", table.Describe());

        string Refused(ReferenceTypeSpec spec) => Assert.Throws<FlowValidationException>(spec.Validate).Message;
        Assert.Contains("needs the ingestion table", Refused(table with { Table = null }), StringComparison.Ordinal);
        Assert.Contains("needs the key", Refused(table with { Key = " " }), StringComparison.Ordinal);
        Assert.Contains("takes no 'kind'", Refused(table with { Kind = "osdu:wks:x--Y:*" }), StringComparison.Ordinal);
        Assert.Contains("takes no query", Refused(table with { Query = "data.Code:m" }), StringComparison.Ordinal);
        Assert.Contains("so it is kept as lookup--CurveClasses", Refused(table with { EntityType = "reference-data--LogCurveFamily" }), StringComparison.Ordinal);
        Assert.Contains("reads no column", Refused(table with { Fields = [] }), StringComparison.Ordinal);
        Assert.Contains("is keyed by 'ID'", Refused(table with { Key = "ID" }), StringComparison.Ordinal);
        Assert.Contains("keeps 'row_id' as 'Id'", Refused(table with { Fields = [new ReferenceFieldSpec("row_id", "Id")] }), StringComparison.Ordinal);
        Assert.Contains("lists its key 'mnemonic' among its fields", Refused(table with { Fields = [new ReferenceFieldSpec("mnemonic", "m")] }), StringComparison.Ordinal);
        Assert.Contains("keeps two values under the name 'Family'", Refused(table with { Fields = [new ReferenceFieldSpec("a", "family"), new ReferenceFieldSpec("b", "Family")] }), StringComparison.Ordinal);

        // As a cache flow declares it, a dictionary type names only its dictionary; once read, it carries the document's key.
        var dictionary = new ReferenceTypeSpec
        {
            Name = "UnitAlias", EntityType = ReferenceType.LookupEntityType("UnitAlias"), Origin = CacheOrigin.Dictionary, Dictionary = "UnitAlias",
        };
        dictionary.Validate();
        (dictionary with { Key = "key", Fields = [new ReferenceFieldSpec("value", "value")] }).Validate();
        Assert.Contains("needs the name of the dictionary", Refused(dictionary with { Dictionary = null }), StringComparison.Ordinal);
        Assert.Contains("whose document names its fields", Refused(dictionary with { Fields = [new ReferenceFieldSpec("value", "value")] }), StringComparison.Ordinal);
        Assert.Contains("takes no 'table'", Refused(dictionary with { Table = "a.b.c" }), StringComparison.Ordinal);
        Assert.Contains("is keyed by 'id'", Refused(dictionary with { Key = "id" }), StringComparison.Ordinal);

        // An OSDU type takes none of a lookup's settings.
        var osdu = new ReferenceTypeSpec { Name = "UnitOfMeasure", EntityType = "reference-data--UnitOfMeasure", Kind = "osdu:wks:reference-data--UnitOfMeasure:*", Fields = [new ReferenceFieldSpec("data.Code")] };
        osdu.Validate();
        Assert.Contains("takes no 'key'", Refused(osdu with { Key = "Code" }), StringComparison.Ordinal);
        Assert.Contains("takes no 'table'", Refused(osdu with { Table = "a.b.c" }), StringComparison.Ordinal);
    }

    [Fact]
    public void A_lookup_table_is_declared_by_one_cache_flow_of_a_partition()
    {
        var table = new ReferenceTypeSpec
        {
            Name = "CurveClasses", EntityType = ReferenceType.LookupEntityType("CurveClasses"), Origin = CacheOrigin.Table, Table = "OsduData.arc.CurveDictionary",
            Key = "mnemonic", Fields = [new ReferenceFieldSpec("curve_family", "family")],
        };
        var partition = new CacheDeclaration(Scope,
        [
            new CacheTypeDeclaration("lookups", "CurveClasses", table.EntityType, null, "*", table.Fields, CacheChangeMode.Auto, CacheOrigin.Table),
            new CacheTypeDeclaration("reference", "UnitOfMeasure", "reference-data--UnitOfMeasure", "osdu:wks:reference-data--UnitOfMeasure:*", "*", [new ReferenceFieldSpec("data.Code")], CacheChangeMode.Auto),
        ]);

        Assert.Empty(partition.Conflicts("lookups", table));
        var second = Assert.Single(partition.Conflicts("other-lookups", table));
        Assert.Contains("a lookup table from a table or a dictionary is declared by one cache flow of a partition only", second, StringComparison.Ordinal);

        // A lookup table and an OSDU type under one name disagree as well, whichever of them came first.
        var units = new ReferenceTypeSpec
        {
            Name = "UnitOfMeasure", EntityType = ReferenceType.LookupEntityType("UnitOfMeasure"), Origin = CacheOrigin.Dictionary, Dictionary = "Units",
        };
        Assert.Contains("declared by one cache flow of a partition only", Assert.Single(partition.Conflicts("lookups", units)), StringComparison.Ordinal);
        var throws = Assert.Throws<DeliveryException>(() => partition.ThrowOnConflicts("lookups", [units]));
        Assert.Contains("disagrees with another cache flow of partition 'dev'", throws.Message, StringComparison.Ordinal);

        // A lookup table keeps what its one flow declares.
        Assert.Same(table, partition.Widen(table));
    }

    [Fact]
    public void A_captured_lookup_table_replaces_its_whole_type()
    {
        var current = WithLookups(Pairs("UnitAlias", ("M", "m"), ("FT", "ft")));

        // Another flow's membership keeps an OSDU record the capture no longer finds, but a lookup table has one flow, and
        // what it captured is the whole type.
        var members = new Dictionary<CacheMemberKey, IReadOnlySet<string>>(CacheMemberKey.Comparer)
        {
            [new CacheMemberKey("UnitAlias", "FT")] = new HashSet<string>(StringComparer.Ordinal) { "someone-else" },
        };
        var plan = CacheMerge.Apply(current, members, "lookups", [Pairs("UnitAlias", ("M", "metre"))], [], "v2", T0.AddDays(1));
        var merged = plan.Snapshot.Type("UnitAlias")!;
        Assert.Equal(["M"], merged.Items.Select(i => i.Id));
        Assert.Equal("metre", merged.Value(merged.Items[0], "value")!.Text);
        Assert.Equal("key", merged.Key);

        // The OSDU types the capture did not cover are untouched.
        Assert.Equal(current.Type("UnitOfMeasure")!.Items.Count, plan.Snapshot.Type("UnitOfMeasure")!.Items.Count);
    }

    [Fact]
    public async Task A_lookup_table_round_trips_through_the_store_with_keys_osdu_would_never_mint()
    {
        var store = _catalog.Caches();
        var units = Pairs("UnitAlias", ("METRES.", "m"), ("IMP_AI", "Local-AI"), ("ft", "ft"), ("fT", "fT"), ("NONE", null));
        var write = await store.MergeAsync(Scope, "lookups", [units, TestSchema.References().Type("UnitOfMeasure")!], Capture, T0);
        Assert.True(write.Written);

        var info = Assert.Single(await store.ListVersionsAsync(Scope));
        Assert.Equal("key", info.Types.Single(t => t.Name == "UnitAlias").Key);
        Assert.Null(info.Types.Single(t => t.Name == "UnitOfMeasure").Key);
        Assert.Equal("dictionary dictionaries/UnitAlias.yaml", info.Origin);

        // Read through a store that has never seen it, so the rows come from the database and the hash is checked there.
        var back = (await _catalog.Caches().LoadAsync(Scope, write.Snapshot.Version))!;
        var type = back.Type("UnitAlias")!;
        Assert.True(type.IsLookup);
        Assert.Equal("key", type.Key);
        Assert.Equal("fT", type.Match("key", "fT")!.Id);
        Assert.Equal("ft", type.Match("key", "ft")!.Id);
        Assert.Equal("Local-AI", type.Value(type.Match("key", "IMP_AI")!, "value")!.Text);
        Assert.Null(type.Value(type.Match("key", "NONE")!, "value"));

        var again = await store.MergeAsync(Scope, "lookups", [units], Capture, T0.AddHours(1));
        Assert.False(again.Written);
    }

    [Theory]
    [InlineData(" M")]
    [InlineData("")]
    [InlineData("M\n")]
    public async Task A_lookup_key_the_cache_could_not_hold_is_refused_before_anything_is_written(string key)
    {
        var store = _catalog.Caches();
        var bad = new ReferenceType("UnitAlias", "lookup--UnitAlias", [new ReferenceItem(key, new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase))], "key");
        var ex = await Assert.ThrowsAsync<DeliveryException>(() => store.MergeAsync(Scope, "lookups", [bad], Capture, T0));
        Assert.Contains("lookup table UnitAlias has keys the cache cannot hold", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await store.ListVersionsAsync(Scope));
    }

    [Fact]
    public void A_lookup_table_has_no_id_to_write_but_every_field_reads_like_a_cached_one()
    {
        var units = Pairs("UnitAlias", ("M", "m"), ("dev:reference-data--UnitOfMeasure:m", "m, written as an id"));
        var references = WithLookups(units);
        var byId = TestSchema.Mapping("""
              Symbol:
                $cache: UnitAlias.id
                $findBy: key = unit
            """);
        var held = new MappingRenderer(byId, TestSchema.Build(), references, TestSchema.Context()).Render(Record("M"));
        Assert.True(held.IsHeld);
        Assert.Contains("is a lookup table in version refs-1 of the cache of partition 'dev', whose rows are not OSDU records, so it has no id to write", Assert.Single(held.Holds), StringComparison.Ordinal);

        var issues = Preflight.Check(byId, TestSchema.Build(), references, TestSchema.Context(), sourceColumns: null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("reads the id of a UnitAlias row, and UnitAlias is a lookup table whose rows are not OSDU records", StringComparison.Ordinal));

        // A field reads like any cached field, and a value shaped like an OSDU id is only a key in a table of no OSDU records.
        var byField = new MappingRenderer(TestSchema.Mapping("""
              Symbol:
                $cache: UnitAlias.value
                $findBy: key = unit
            """), TestSchema.Build(), references, TestSchema.Context());
        Assert.Equal("m", byField.Render(Record("m")).Document["data"]!["Symbol"]!.GetValue<string>());
        Assert.Equal("m, written as an id", byField.Render(Record("dev:reference-data--UnitOfMeasure:m")).Document["data"]!["Symbol"]!.GetValue<string>());
    }

    private static SourceRecord Record(string unit) => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "well-1", ["depth"] = "1", ["unit"] = unit }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void A_cache_flow_reads_a_table_type_over_its_connection_and_refuses_what_a_delivery_flow_would()
    {
        var loader = new Documents.DeliveryDocumentLoader();
        const string Tables = """
            flowType: cache
            name: lookups
            source:
              connection: ${env:OSDU_DATA_DB}
              headers: { data-partition-id: dev }
            types:
              - table: OsduData.arc.CurveDictionary
                key: mnemonic
                fields: [curve_family, { column: curve_main_family, as: mainFamily }]
            """;
        var flow = loader.ParseCache(Tables, "cache/lookups.yaml");
        Assert.Equal("${env:OSDU_DATA_DB}", flow.Source.Connection);
        Assert.Null(flow.Source.Endpoint);
        var type = Assert.Single(flow.Types);
        Assert.Equal("CurveDictionary", type.Name);
        Assert.Equal(CacheOrigin.Table, type.Origin);
        Assert.Equal("lookup--CurveDictionary", type.EntityType);
        Assert.Equal("mnemonic", type.Key);
        Assert.Equal(["curve_family", "mainFamily"], type.Fields.Select(f => f.Name));
        Assert.Equal(["curve_family", "curve_main_family"], type.Fields.Select(f => f.Path));
        Assert.Contains(flow.CredentialReferences(), r => r.Key == "source.connection");
        var document = new Documents.CacheFlowDocument { Flow = flow };
        Assert.True(document.RequiresRepoTree);
        Assert.Equal("${env:OSDU_DATA_DB}", document.SourceConnectionReference);
        var read = Assert.Single(document.DeclaredObjects);
        Assert.Equal(("OsduData", "arc", "CurveDictionary"), (read.Database, read.Schema, read.Name));

        string Refused(string yaml) => Assert.Throws<FlowValidationException>(() => loader.ParseCache(yaml, "cache/lookups.yaml")).Message;
        Assert.Contains("source.connection is required", Refused(Tables.Replace("  connection: ${env:OSDU_DATA_DB}\n", string.Empty, StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("carries a literal password", Refused(Tables.Replace("${env:OSDU_DATA_DB}", "Server=db;Database=x;User Id=u;Password=secret", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("must be a three-part name", Refused(Tables.Replace("OsduData.arc.CurveDictionary", "ing.CurveDictionary", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("types[0].key is required", Refused(Tables.Replace("    key: mnemonic\n", string.Empty, StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("names a column '[mnemonic]'", Refused(Tables.Replace("key: mnemonic", "key: \"[mnemonic]\"", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("which takes no 'query'", Refused(Tables.Replace("    key: mnemonic\n", "    key: mnemonic\n    query: \"*\"\n", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("a table column takes 'column' and 'as'", Refused(Tables.Replace("{ column: curve_main_family, as: mainFamily }", "{ path: curve_main_family }", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("names more than one origin", Refused(Tables.Replace("  - table: OsduData.arc.CurveDictionary\n", "  - table: OsduData.arc.CurveDictionary\n    dictionary: UnitAlias\n", StringComparison.Ordinal)), StringComparison.Ordinal);

        // A connection with no table to read, and a key on a type of OSDU records, are settings that would do nothing.
        Assert.Contains("declares no type read from a table there", Refused("""
            flowType: cache
            name: units
            source:
              endpoint: https://osdu.example.com
              connection: ${env:OSDU_DATA_DB}
              headers: { data-partition-id: dev }
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields: [data.Code]
            """), StringComparison.Ordinal);
        Assert.Contains("names a key, which only a table type takes", Refused("""
            flowType: cache
            name: units
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: dev }
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                key: Code
                fields: [data.Code]
            """), StringComparison.Ordinal);
    }

    [Fact]
    public void One_cache_flow_combines_types_searched_on_osdu_with_tables_read_from_the_database_and_dictionaries()
    {
        // The platform is reached for the kind, the database for the table, and the repository for the dictionary: one
        // flow, one refresh, one version of the partition's cache holding all three, read by a mapping the same way.
        var flow = new Documents.DeliveryDocumentLoader().ParseCache("""
            flowType: cache
            name: welldb-combined-00-cache
            source:
              endpoint: ${env:OSDU_URL}
              connection: ${env:OSDU_DATA_DB}
              auth:
                type: oauth2ClientCredentials
                secondarySecretRef: ${env:OSDU_CLIENT_ID}
                secretRef: ${env:OSDU_CLIENT_SECRET}
                token: { url: "${env:OSDU_TOKEN_URL}" }
              headers: { data-partition-id: dev }
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields: [data.Code, data.Name, data.ID]
              - table: OsduData.arc.UnitAlias
                name: UnitAlias
                key: source_unit
                fields: [osdu_unit]
              - dictionary: CurveClasses
            """, "cache/combined.yaml");

        Assert.Equal("${env:OSDU_URL}", flow.Source.Endpoint);
        Assert.Equal("${env:OSDU_DATA_DB}", flow.Source.Connection);
        Assert.Equal(
            [(CacheOrigin.Osdu, "reference-data--UnitOfMeasure"), (CacheOrigin.Table, "lookup--UnitAlias"), (CacheOrigin.Dictionary, "lookup--CurveClasses")],
            flow.Types.Select(t => (t.Origin, t.EntityType)));
        var references = flow.CredentialReferences().Select(r => r.Key).ToList();
        Assert.Contains("source.endpoint", references);
        Assert.Contains("source.connection", references);
        Assert.Contains("source.auth.secretRef", references);
        var read = Assert.Single(new Documents.CacheFlowDocument { Flow = flow }.DeclaredObjects);
        Assert.Equal(("OsduData", "arc", "UnitAlias"), (read.Database, read.Schema, read.Name));
    }

    [Fact]
    public void A_lookup_table_json_carries_its_key_between_entity_type_and_items()
    {
        var json = Pairs("UnitAlias", ("M", "m")).ToJson();
        Assert.Equal(["entityType", "key", "items"], json.Select(kv => kv.Key));
        Assert.IsType<JsonArray>(json["items"]);
    }
}
