using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Dictionary documents: one lookup table per file in the repository's dictionaries folder, read as text exactly as it is
/// written, held by a cache flow in its partition's cache, and captured by a refresh into a version like any cached type.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class DictionaryTests : IDisposable
{
    private const string Pairs = """
        documentType: dictionary
        name: UnitAlias
        description: Unit spellings.
        entries:
          M: m
          METRES.: m
          NONE: ~
          NO: number
          true: yes
          1.10: one point ten
          "~": tilde
          empty:
        """;

    private const string Curves = """
        documentType: dictionary
        name: CurveDictionary
        key: mnemonic
        fields: [type, family, mainFamily, unit]
        entries:
          GR: { type: Local-GR, family: Gamma Ray, mainFamily: GammaRay, unit: gAPI }
          IMP_AI:
            type: Local-AI
            family: Local-Acoustic Impedance Compressional
            unit: ~
          DEPTH:
        """;

    private readonly string _root = Samples.NewTempDirectory();
    private readonly OsduTestDatabase _db = new();

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static DictionaryDefinition Parse(string yaml) => new DeliveryDocumentLoader().ParseDictionary(yaml, "dictionaries/x.yaml");

    private static string Refused(string yaml) => Assert.Throws<FlowValidationException>(() => Parse(yaml)).Message;

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_dictionary_of_pairs_keeps_every_key_and_value_as_the_text_it_is_written_as()
    {
        var units = Parse(Pairs);
        Assert.Equal("UnitAlias", units.Name);
        Assert.Equal("Unit spellings.", units.Description);
        Assert.True(units.IsPairs);
        Assert.Equal("key", units.Key);
        Assert.Equal(["value"], units.Fields);

        var entries = units.Entries.ToDictionary(e => e.Key, e => e.Values["value"], StringComparer.Ordinal);
        Assert.Equal("m", entries["METRES."]);
        Assert.Null(entries["NONE"]);
        Assert.Null(entries["empty"]);

        // A typed read would have made these a boolean and a number; a dictionary keeps the text.
        Assert.Equal("number", entries["NO"]);
        Assert.Equal("yes", entries["true"]);
        Assert.Equal("one point ten", entries["1.10"]);
        Assert.Equal("tilde", entries["~"]);

        var lookup = units.ToLookup("UnitAlias");
        Assert.True(lookup.IsLookup);
        Assert.Equal("key", lookup.Key);
        Assert.Equal("m", lookup.Value(lookup.Match("key", "metres.")!, "value")!.Text);
        Assert.Null(lookup.Value(lookup.Match("key", "NONE")!, "value"));
        Assert.Equal(["1.10", "M", "METRES.", "NO", "NONE", "empty", "true", "~"], lookup.Items.Select(i => i.Id));
    }

    [Fact]
    public void A_dictionary_with_fields_gives_each_key_its_named_values()
    {
        var curves = Parse(Curves);
        Assert.False(curves.IsPairs);
        Assert.Equal("mnemonic", curves.Key);
        Assert.Equal(["type", "family", "mainFamily", "unit"], curves.Fields);

        var lookup = curves.ToLookup("CurveClasses");
        Assert.Equal("lookup--CurveClasses", lookup.EntityType);
        var ai = lookup.Match("mnemonic", "IMP_AI")!;
        Assert.Equal("Local-Acoustic Impedance Compressional", lookup.Value(ai, "family")!.Text);
        Assert.Null(lookup.Value(ai, "unit"));
        Assert.Null(lookup.Value(ai, "mainFamily"));
        Assert.Null(lookup.Value(lookup.Match("mnemonic", "DEPTH")!, "type"));
        Assert.Equal("gAPI", lookup.Value(lookup.Match("mnemonic", "gr")!, "unit")!.Text);
        Assert.Equal(["mnemonic", "type", "family", "mainFamily", "unit"], curves.FieldSpecs().Select(f => f.Name).Prepend(curves.Key));
    }

    [Fact]
    public void A_dictionary_that_would_hold_something_ambiguous_or_unreadable_is_refused_where_it_is_wrong()
    {
        Assert.Contains("line 7: Duplicate key M", Refused(Pairs.Replace("  NONE: ~", "  M: metre", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("'values' is not a key of a dictionary document", Refused(Pairs + "\nvalues: []\n"), StringComparison.Ordinal);
        Assert.Contains("expected 'documentType: dictionary', found 'mapping'", Refused(Pairs.Replace("documentType: dictionary", "documentType: mapping", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("name is required", Refused(Pairs.Replace("name: UnitAlias", "name: 1units", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("key 'ID' names the entries' key 'id'", Refused(Curves.Replace("key: mnemonic", "key: ID", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("field 'Id' is called id", Refused(Curves.Replace("fields: [type,", "fields: [Id, type,", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("field 'Mnemonic' is the key's own name", Refused(Curves.Replace("fields: [type,", "fields: [Mnemonic, type,", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("field 'Type' is listed twice", Refused(Curves.Replace("fields: [type,", "fields: [type, Type,", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("field 'main family' is not a name a mapping can read", Refused(Curves.Replace("mainFamily, unit]", "\"main family\", unit]", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("'M' maps to something that is not text", Refused(Pairs.Replace("  M: m", "  M: [m]", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("'GR' gives 'colour', which fields does not list", Refused(Curves.Replace("unit: gAPI }", "unit: gAPI, colour: red }", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("'GR' gives text; a dictionary with fields gives each key a map of them", Refused(Curves.Replace("GR: { type: Local-GR, family: Gamma Ray, mainFamily: GammaRay, unit: gAPI }", "GR: gamma", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("gives unit as a list", Refused(Curves.Replace("unit: gAPI }", "unit: [gAPI] }", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("entries is required", Refused("documentType: dictionary\nname: Empty\nentries: {}\n"), StringComparison.Ordinal);
        Assert.Contains("an entry has no key", Refused("documentType: dictionary\nname: Nulls\nentries:\n  ~: m\n"), StringComparison.Ordinal);
        Assert.Contains("has spaces around it", Refused("documentType: dictionary\nname: Spaced\nentries:\n  \" M\": m\n"), StringComparison.Ordinal);
        Assert.Contains("holds exactly one document", Refused(Pairs + "\n---\n" + Pairs), StringComparison.Ordinal);

        var many = "documentType: dictionary\nname: Huge\nentries:\n" + string.Concat(Enumerable.Range(0, DictionaryDefinition.MaxEntries + 1).Select(i => $"  k{i}: v\n"));
        Assert.Contains($"and a dictionary holds at most {DictionaryDefinition.MaxEntries}", Refused(many), StringComparison.Ordinal);
    }

    [Fact]
    public void A_dictionary_is_found_in_the_nearest_dictionaries_folder_under_the_name_it_declares()
    {
        Write("dictionaries/UnitAlias.yaml", Pairs);
        Write("dictionaries/Misfiled.yml", Pairs);
        Directory.CreateDirectory(Path.Combine(_root, "estate", "cache", "deep"));
        var catalog = new DictionaryCatalog(new DeliveryDocumentLoader());
        string Shown(string full) => Path.GetRelativePath(_root, full).Replace('\\', '/');

        var found = catalog.Load("UnitAlias", Path.Combine(_root, "estate", "cache", "deep"), Shown);
        Assert.Equal("dictionaries/UnitAlias.yaml", found.ShownPath);
        Assert.Equal(12 - 4, found.Dictionary.Entries.Count);

        var misfiled = Assert.Throws<FlowValidationException>(() => catalog.Load("Misfiled", _root, Shown));
        Assert.Contains("dictionaries/Misfiled.yml: declares dictionary 'UnitAlias' but is filed as 'Misfiled'", misfiled.Message, StringComparison.Ordinal);
        Assert.Contains("Expected Absent.yaml or Absent.yml", Assert.Throws<FlowValidationException>(() => catalog.Load("Absent", _root, Shown)).Message, StringComparison.Ordinal);
        Assert.Contains("names a path", Assert.Throws<FlowValidationException>(() => catalog.Load("../UnitAlias", _root, Shown)).Message, StringComparison.Ordinal);

        // A search that may not leave the repository stops at its edge.
        var outside = Samples.NewTempDirectory();
        Assert.Contains("there is no dictionaries/ directory", Assert.Throws<FlowValidationException>(() => catalog.Load("UnitAlias", Path.Combine(_root, "estate"), Shown, within: path => path.StartsWith(Path.Combine(_root, "estate"), StringComparison.OrdinalIgnoreCase))).Message, StringComparison.Ordinal);
        Assert.Null(DictionaryCatalog.Locate(outside));
    }

    [Fact]
    public void A_cache_flow_holds_a_dictionary_without_an_endpoint_and_takes_none_of_an_osdu_types_settings()
    {
        var loader = new DeliveryDocumentLoader();
        const string Lookups = """
            flowType: cache
            name: lookups
            source:
              headers: { data-partition-id: dev }
            types:
              - dictionary: UnitAlias
              - name: Curves
                dictionary: CurveDictionary
                onChange: approve
            """;
        var flow = loader.ParseCache(Lookups, "cache/lookups.yaml");
        Assert.Null(flow.Source.Endpoint);
        var units = flow.Types[0];
        Assert.Equal("UnitAlias", units.Name);
        Assert.Equal(CacheOrigin.Dictionary, units.Origin);
        Assert.Equal("UnitAlias", units.Dictionary);
        Assert.Equal("lookup--UnitAlias", units.EntityType);
        Assert.Equal("Curves", flow.Types[1].Name);
        Assert.Equal(CacheChangeMode.Approve, flow.Types[1].OnChange);
        Assert.True(new CacheFlowDocument { Flow = flow }.RequiresRepoTree);

        string Refused(string yaml) => Assert.Throws<FlowValidationException>(() => loader.ParseCache(yaml, "cache/lookups.yaml")).Message;
        Assert.Contains("names more than one origin", Refused(Lookups.Replace("- dictionary: UnitAlias", "- { dictionary: UnitAlias, kind: \"osdu:wks:x--Y:*\" }", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("which takes no 'fields'", Refused(Lookups.Replace("- dictionary: UnitAlias", "- { dictionary: UnitAlias, fields: [value] }", StringComparison.Ordinal)), StringComparison.Ordinal);
        // The dictionary document names its own key, so a key written beside it would be one nothing reads.
        Assert.Contains("which takes no 'key'", Refused(Lookups.Replace("- dictionary: UnitAlias", "- { dictionary: UnitAlias, key: code }", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("every type it declares is a lookup table. Remove them", Refused(Lookups.Replace("  headers:", "  endpoint: https://osdu.example.com\n  headers:", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("source.endpoint is required", Refused(Lookups + "\n  - kind: \"osdu:wks:reference-data--UnitOfMeasure:*\"\n    fields: [data.Code]\n"), StringComparison.Ordinal);
        Assert.Contains("needs a kind", Refused(Lookups.Replace("- dictionary: UnitAlias", "- name: Nothing", StringComparison.Ordinal)), StringComparison.Ordinal);

        // A flow with no dictionary still reads the repository document above it, whose code its requests carry.
        var searched = loader.ParseCache("""
            flowType: cache
            name: searched
            source:
              endpoint: https://osdu.example.com
              headers: { data-partition-id: dev }
            types:
              - kind: "osdu:wks:reference-data--UnitOfMeasure:*"
                fields: [data.Code, data.Name]
            """, "cache/searched.yaml");
        Assert.True(new CacheFlowDocument { Flow = searched }.RequiresRepoTree);
    }

    [Fact]
    public async Task A_refresh_captures_a_dictionary_into_a_version_and_an_edited_entry_tags_only_the_records_built_from_it()
    {
        var flowPath = Write("cache/lookups.yaml", """
            flowType: cache
            name: lookups
            source:
              headers: { data-partition-id: dev }
            types:
              - dictionary: UnitAlias
            """);
        Write("dictionaries/UnitAlias.yaml", "documentType: dictionary\nname: UnitAlias\nentries:\n  M: m\n  FT: ft\n");
        var clock = new TestClock();
        var ledger = _db.Ledger(clock);
        var store = _db.Caches();
        var engine = Samples.Engine(ledger, clock, cache: store);
        var flow = new DeliveryDocumentLoader().LoadCache(flowPath);
        var refresher = new CacheRefresher(engine, Samples.Logger<CacheRefresher>());

        var first = await refresher.RefreshAsync(flow, new Dictionary<string, string>(), Guid.NewGuid(), "manual:tester", CancellationToken.None);
        Assert.True(first.Written);
        var type = Assert.Single(first.Types);
        Assert.Equal("dictionary", type.Origin);
        Assert.Equal("dictionary UnitAlias", type.Source);
        Assert.Null(type.Kind);
        Assert.Equal(2, type.Items);
        Assert.Equal(["key", "value"], type.Fields);
        var version = Assert.Single(await store.ListVersionsAsync("dev"));
        Assert.Equal("dictionary dictionaries/UnitAlias.yaml", version.Origin);
        Assert.Empty(version.SystemProperties);

        // Records delivered from the two rows: three read M, one reads FT.
        await DeliveredAsync(ledger, clock, "M", "m", 3);
        await DeliveredAsync(ledger, clock, "FT", "ft", 1);

        // An unchanged file writes no version; a plan counts the entries and writes nothing.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False((await refresher.RefreshAsync(flow, new Dictionary<string, string>(), Guid.NewGuid(), "manual:tester", CancellationToken.None)).Written);
        var plan = await refresher.PlanAsync(flow, new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal(2, Assert.Single(plan.Types).Records);

        // Editing one entry writes a version, and the change reaches only the records that read that entry.
        Write("dictionaries/UnitAlias.yaml", "documentType: dictionary\nname: UnitAlias\nentries:\n  M: metre\n  FT: ft\n");
        clock.Advance(TimeSpan.FromMinutes(1));
        var edited = await refresher.RefreshAsync(flow, new Dictionary<string, string>(), Guid.NewGuid(), "manual:tester", CancellationToken.None);
        Assert.True(edited.Written);
        Assert.Equal(1, edited.Types.Single().ChangedItems);
        Assert.Equal(3, edited.AffectedRecords);
        var tag = Assert.Single(await ledger.ListTagsAsync("approved", 10, 0));
        Assert.Equal("M", tag.ItemId);
        Assert.Equal("m", tag.OldValue);
        Assert.Equal("metre", tag.NewValue);

        // A dictionary the refresh cannot read captures nothing.
        Write("dictionaries/UnitAlias.yaml", "documentType: dictionary\nname: Renamed\nentries:\n  M: m\n");
        var broken = await Assert.ThrowsAsync<DeliveryException>(() => refresher.RefreshAsync(flow, new Dictionary<string, string>(), Guid.NewGuid(), "manual:tester", CancellationToken.None));
        Assert.Contains("could not read dictionary UnitAlias for type UnitAlias, so nothing was captured", broken.Message, StringComparison.Ordinal);
        Assert.Equal(2, (await store.ListVersionsAsync("dev")).Count);
    }

    [Fact]
    public async Task A_refresh_says_which_of_its_types_moved_and_a_type_that_did_not_keeps_its_hash_and_its_date()
    {
        var flowPath = Write("cache/lookups.yaml", """
            flowType: cache
            name: lookups
            source:
              headers: { data-partition-id: dev }
            types:
              - dictionary: UnitAlias
              - dictionary: DepthUnitAlias
            """);
        Write("dictionaries/UnitAlias.yaml", "documentType: dictionary\nname: UnitAlias\nentries:\n  M: m\n  FT: ft\n");
        Write("dictionaries/DepthUnitAlias.yaml", "documentType: dictionary\nname: DepthUnitAlias\nentries:\n  MD: m\n");
        var clock = new TestClock();
        var store = _db.Caches();
        var engine = Samples.Engine(_db.Ledger(clock), clock, cache: store);
        var flow = new DeliveryDocumentLoader().LoadCache(flowPath);
        var refresher = new CacheRefresher(engine, Samples.Logger<CacheRefresher>());

        var first = await refresher.RefreshAsync(flow, new Dictionary<string, string>(), Guid.NewGuid(), "manual:tester", CancellationToken.None);
        Assert.All(first.Types, t => Assert.Equal("added", t.Change));

        // One of the two dictionaries is edited: the refresh writes a version, and says the other rode along unchanged.
        Write("dictionaries/UnitAlias.yaml", "documentType: dictionary\nname: UnitAlias\nentries:\n  M: metre\n  FT: ft\n");
        clock.Advance(TimeSpan.FromMinutes(1));
        var edited = await refresher.RefreshAsync(flow, new Dictionary<string, string>(), Guid.NewGuid(), "manual:tester", CancellationToken.None);
        Assert.True(edited.Written);
        var units = Assert.Single(edited.Types, t => t.Name == "UnitAlias");
        var depths = Assert.Single(edited.Types, t => t.Name == "DepthUnitAlias");
        Assert.Equal("changed", units.Change);
        Assert.Equal(1, units.ChangedItems);
        Assert.Equal("unchanged", depths.Change);
        Assert.Equal(0, depths.ChangedItems);
        Assert.Equal(Assert.Single(first.Types, t => t.Name == "DepthUnitAlias").Hash, depths.Hash);
        Assert.NotEqual(Assert.Single(first.Types, t => t.Name == "UnitAlias").Hash, units.Hash);

        var current = Assert.Single(await store.ListVersionsAsync("dev"), v => v.Current);
        Assert.Equal(edited.Version, Assert.Single(current.Types, t => t.Name == "UnitAlias").Since);
        Assert.Equal(first.Version, Assert.Single(current.Types, t => t.Name == "DepthUnitAlias").Since);

        // A refresh that finds nothing new writes no version, and says every type is unchanged.
        clock.Advance(TimeSpan.FromMinutes(1));
        var again = await refresher.RefreshAsync(flow, new Dictionary<string, string>(), Guid.NewGuid(), "manual:tester", CancellationToken.None);
        Assert.False(again.Written);
        Assert.All(again.Types, t => Assert.Equal("unchanged", t.Change));
    }

    /// <summary>Delivers <paramref name="count"/> records that read one row of the UnitAlias table by its key.</summary>
    private static async Task DeliveredAsync(OsduLedger ledger, TestClock clock, string key, string value, int count)
    {
        var flowId = FlowId.Of("units-flow");
        await ledger.RegisterAsync(flowId);
        var now = clock.GetUtcNow().UtcDateTime;
        var submission = Guid.NewGuid();
        await ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission,
            FlowId = flowId,
            FlowName = "units-flow",
            MappingReference = "Thing@1.0.0",
            RenderContext = "{}",
            RecordCount = count,
            Status = SubmissionStatus.Planned,
            ReceivedUtc = now,
        });

        var setId = await ledger.EnsureCacheSetAsync("dev",
        [
            new CacheUsage("UnitAlias", key, "key", key, CacheUsageKind.Match),
            new CacheUsage("UnitAlias", key, "value", value, CacheUsageKind.Value),
        ]);
        var records = Enumerable.Range(0, count).Select(i => new RecordState
        {
            DeliveryKey = DeliveryKey.Derive("test", [$"{key}-{i}"]),
            FlowId = flowId,
            SourceKey = $"{key}-{i}",
            MappingName = "Thing",
            TargetId = $"dev:work-product-component--Thing:{key}-{i}",
            LastSubmissionId = submission,
            PendingDocumentRef = "0:0:10",
            PendingRenderContext = "{}",
            PendingMetadataHash = "mh",
            PendingMetadata = true,
            CacheSetId = setId,
        }).ToList();
        await ledger.UpsertPendingAsync(flowId, records);
    }
}
