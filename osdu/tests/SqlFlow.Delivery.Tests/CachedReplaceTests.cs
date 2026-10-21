using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Snapshots;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A replace that reads its table from the partition's cache (<c>replace: $cache.&lt;Type&gt;</c>): a dictionary, a table
/// read from an ingestion table, or OSDU reference data, matched and replaced by the cache's own rules, every row it used
/// recorded, and what it cannot decide held rather than guessed.
/// </summary>
[Collection(SqlServerSuite.Name)]
[Trait("Category", "Integration")]
public sealed class CachedReplaceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly OsduTestDatabase _db = new();
    private readonly TestClock _clock = new();

    public void Dispose() => _db.Dispose();

    /// <summary>The unit spellings of a source, as a dictionary of pairs is cached.</summary>
    private static ReferenceType UnitAlias(params (string From, string? To)[] pairs)
        => LookupCacheTests.Pairs("UnitAlias", pairs.Length > 0 ? pairs : [("M", "m"), ("METRE", "m"), ("FT", "ft"), ("NONE", null)]);

    /// <summary>A curve dictionary as a table type caches it: keyed by mnemonic, with a family and a unit.</summary>
    private static ReferenceType CurveClasses() => LookupCacheTests.Lookup(
        "CurveClasses",
        "mnemonic",
        ("GR", Values(("curve_family", "Gamma Ray"), ("unit", "gAPI"))),
        ("RHOB", Values(("curve_family", "Bulk Density"), ("unit", "g/cm3"))),
        ("XX", Values(("unit", "m"))));

    private static IReadOnlyDictionary<string, string?> Values(params (string Name, string? Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static ReferenceSnapshot Cache(params ReferenceType[] types) => new("refs-1", T0, TestSchema.References().Types.Concat(types));

    private static MappingRenderer Renderer(string entries, ReferenceSnapshot cache)
        => new(TestSchema.Mapping(entries), TestSchema.Build(), cache, TestSchema.Context());

    private static SourceRecord Record(string unit) => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = "well-1", ["depth"] = "1", ["unit"] = unit }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
    };

    private static string? Data(RenderResult result, string property) => result.Document["data"]![property]?.GetValue<string>();

    [Fact]
    public void A_replace_names_a_cached_table_and_the_fields_it_matches_on_and_replaces_by()
    {
        var mapping = TestSchema.Mapping("""
              Symbol:
                $from: unit
                $modifiers:
                  - replace: $cache.UnitAlias
              Description:
                $from: unit
                $modifiers:
                  - replace: $cache.CurveClasses
                    field: curve_family
                    match: mnemonic
                    otherwise: ~
            """);

        var plain = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Symbol").Modifiers.Single();
        Assert.Equal(new CachedReplaceTable("UnitAlias", null, null), plain.Table);
        Assert.Empty(plain.Replacements);
        Assert.Equal(ReplaceFallback.Keep, plain.Otherwise);
        Assert.Equal("replace from $cache.UnitAlias", plain.ToString());

        var named = mapping.Entries.Single(e => e.Target.Text == "osdu.data.Description").Modifiers.Single();
        Assert.Equal(new CachedReplaceTable("CurveClasses", "mnemonic", "curve_family"), named.Table);
        Assert.Equal("replace from $cache.CurveClasses (mnemonic to curve_family), otherwise ~", named.ToString());

        // A table read by a modifier alone is still a type the mapping reads, for the render, the intake and lineage alike.
        Assert.Equal(["CurveClasses", "UnitAlias"], mapping.CacheTypesRead());
        Assert.Equal(["CurveClasses", "UnitAlias"], DeliveryLineage.CacheTypes(mapping));

        string Refused(string modifier) => Assert.Throws<FlowValidationException>(() => TestSchema.Mapping($"""
              Symbol:
                $from: unit
                $modifiers:
            {modifier}
            """)).Message;
        Assert.Contains("a table read from the cache is named $cache.<Type>, such as replace: $cache.UnitAlias", Refused("      - replace: UnitAlias"), StringComparison.Ordinal);
        Assert.Contains("a table read from the cache is named $cache.<Type>", Refused("      - replace: $cache.Unit.Alias"), StringComparison.Ordinal);
        Assert.Contains("a table read from the cache is named $cache.<Type>", Refused("      - replace: search.Wellbore"), StringComparison.Ordinal);
        Assert.Contains("replace's match names a field of the cached table", Refused("      - replace: $cache.UnitAlias\n        match: [a, b]"), StringComparison.Ordinal);
        Assert.Contains("replace's field names a field of the cached table", Refused("      - replace: $cache.UnitAlias\n        field: \"a b\""), StringComparison.Ordinal);
        Assert.Contains("replace's field names a field of the cached table", Refused("      - replace: $cache.UnitAlias\n        field: ~"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_cached_dictionary_replaces_a_value_on_its_key_by_its_value_and_every_row_it_used_is_recorded()
    {
        var renderer = Renderer("""
              Unit:
                $cache: UnitOfMeasure.id
                $findBy: Code = unit
                $modifiers:
                  - replace: $cache.UnitAlias
            """, Cache(UnitAlias()));

        var metre = renderer.Render(Record(" METRE "));
        Assert.False(metre.IsHeld, string.Join("; ", metre.Holds));
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", Data(metre, "Unit"));
        Assert.Contains(new CacheUsage("UnitAlias", "METRE", "key", "METRE", CacheUsageKind.Match), metre.CacheUsages);
        Assert.Contains(new CacheUsage("UnitAlias", "METRE", "value", "m", CacheUsageKind.Value), metre.CacheUsages);
        Assert.Contains(metre.CacheUsages, u => u.TypeName == "UnitOfMeasure" && u.Kind == CacheUsageKind.Match && u.Value == "m");

        // A key is matched ignoring case when that finds one row, and the row is recorded under its own key.
        var feet = renderer.Render(Record("ft"));
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft:", Data(feet, "Unit"));
        Assert.Contains(new CacheUsage("UnitAlias", "FT", "key", "ft", CacheUsageKind.Match), feet.CacheUsages);

        // A row that gives no value gives none, and what it gave (nothing) is recorded, so an edit giving it one reaches
        // this record; the entry is required, so the record is held.
        var none = renderer.Render(Record("NONE"));
        Assert.True(none.IsHeld);
        Assert.Contains("dataset.unit is empty, so there is nothing to find in the cache, and the entry is required", Assert.Single(none.Holds), StringComparison.Ordinal);
        Assert.Contains(new CacheUsage("UnitAlias", "NONE", "value", string.Empty, CacheUsageKind.Empty), none.CacheUsages);

        // The partition's own spelling is listed too, ignoring case, so it is looked up like any other.
        var own = renderer.Render(Record("m"));
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", Data(own, "Unit"));
        Assert.Contains(new CacheUsage("UnitAlias", "M", "key", "m", CacheUsageKind.Match), own.CacheUsages);

        // A spelling the dictionary does not list passes on unchanged, and the key it was looked up under is recorded, so a
        // dictionary that comes to list it reaches this record.
        var kb = renderer.Render(Record("kb"));
        Assert.True(kb.IsHeld);
        Assert.Contains(new CacheUsage("UnitAlias", "KB", "value", "kb", CacheUsageKind.Unlisted), kb.CacheUsages);
    }

    [Fact]
    public void A_cached_table_replaces_by_the_field_it_names_and_otherwise_decides_what_no_row_holds()
    {
        string? Symbol(string settings, string curve)
        {
            var result = Renderer($$"""
                  Symbol:
                    $from: unit
                    $required: false
                    $modifiers:
                      - replace: $cache.CurveClasses
                        {{settings}}
                """, Cache(CurveClasses())).Render(Record(curve));
            Assert.False(result.IsHeld, string.Join("; ", result.Holds));
            return Data(result, "Symbol");
        }

        const string Family = "field: curve_family";
        Assert.Equal("Gamma Ray", Symbol(Family, "GR"));
        Assert.Equal("Bulk Density", Symbol(Family, "rhob"));

        // A row with nothing at the field gives no value; otherwise decides only for a value no row holds.
        Assert.Null(Symbol(Family + "\n        otherwise: Unknown", "XX"));
        Assert.Equal("ZZ", Symbol(Family, "ZZ"));
        Assert.Null(Symbol(Family + "\n        otherwise: ~", "ZZ"));
        Assert.Equal("Unknown", Symbol(Family + "\n        otherwise: Unknown", "ZZ"));

        // Matched on another field than the key, the row is found by what it holds there.
        Assert.Equal("RHOB", Symbol("match: unit\n        field: mnemonic", "g/cm3"));
    }

    [Fact]
    public void A_table_settles_the_fields_a_replace_leaves_out_only_where_they_are_unambiguous()
    {
        // A lookup table with several fields beside its key does not say which of them replaces.
        var several = Renderer("""
              Symbol:
                $from: unit
                $modifiers:
                  - replace: $cache.CurveClasses
            """, Cache(CurveClasses())).Render(Record("GR"));
        Assert.True(several.IsHeld);
        Assert.Contains(
            "lookup table CurveClasses holds 2 fields beside its key mnemonic (curve_family, unit); name the one that replaces the value, such as field: curve_family",
            Assert.Single(several.Holds),
            StringComparison.Ordinal);

        // A dictionary whose every entry gives no value is read as the dictionary of pairs it is: every row gives none.
        var empty = Renderer("""
              Symbol:
                $from: unit
                $required: false
                $modifiers:
                  - replace: $cache.UnitAlias
            """, Cache(UnitAlias(("NONE", null)))).Render(Record("none"));
        Assert.False(empty.IsHeld, string.Join("; ", empty.Holds));
        Assert.Null(Data(empty, "Symbol"));

        // A type of OSDU records has no key and many fields, so a replace reading one names both.
        string Held(string settings)
        {
            var result = Renderer($$"""
                  Symbol:
                    $from: unit
                    $modifiers:
                      - replace: $cache.UnitOfMeasure
                        {{settings}}
                """, Cache()).Render(Record("metre"));
            Assert.True(result.IsHeld);
            return Assert.Single(result.Holds);
        }

        Assert.Contains("$cache.UnitOfMeasure holds OSDU records (reference-data--UnitOfMeasure), which have no key for a replace to match on; name the field the incoming value is compared with, such as match: Code", Held("field: Code"), StringComparison.Ordinal);
        Assert.Contains("$cache.UnitOfMeasure holds OSDU records (reference-data--UnitOfMeasure); name the field that replaces the value, such as field: id", Held("match: Name"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_osdu_type_is_a_table_too_once_the_replace_names_both_fields()
    {
        string? Symbol(string field, string unit, out IReadOnlyList<CacheUsage> usages)
        {
            var result = Renderer($$"""
                  Symbol:
                    $from: unit
                    $modifiers:
                      - replace: $cache.UnitOfMeasure
                        match: Name
                        field: {{field}}
                """, Cache()).Render(Record(unit));
            Assert.False(result.IsHeld, string.Join("; ", result.Holds));
            usages = result.CacheUsages;
            return Data(result, "Symbol");
        }

        Assert.Equal("m", Symbol("Code", "Metre", out var byName));
        Assert.Contains(new CacheUsage("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", "Name", "Metre", CacheUsageKind.Match), byName);
        Assert.Contains(new CacheUsage("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:m", "Code", "m", CacheUsageKind.Value), byName);
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft", Symbol("id", "foot", out _));

        // A value no OSDU record holds on a field other than a key depends on no row, so nothing is recorded for it.
        Assert.Equal("yard", Symbol("Code", "yard", out var missing));
        Assert.Empty(missing);
    }

    [Fact]
    public void A_replace_holds_what_it_cannot_decide()
    {
        string Held(string table, ReferenceSnapshot cache, string unit, string settings = "")
        {
            var result = Renderer($$"""
                  Symbol:
                    $from: unit
                    $required: false
                    $modifiers:
                      - replace: $cache.{{table}}
                        {{settings}}
                """, cache).Render(Record(unit));
            Assert.True(result.IsHeld, $"'{unit}' was not held");
            return Assert.Single(result.Holds);
        }

        Assert.Contains(
            "osdu.data.Symbol: replace reads $cache.Nope, and version refs-1 of the cache of partition 'dev' holds no type 'Nope'",
            Held("Nope", Cache(UnitAlias()), "M"),
            StringComparison.Ordinal);

        // Two keys answer to "ft" only once case is ignored, and they disagree: taking either writes a value nobody chose.
        var clash = Held("UnitAlias", Cache(UnitAlias(("Ft", "ft"), ("FT", "foot"))), "ft");
        Assert.Contains("'ft' matches the UnitAlias rows ", clash, StringComparison.Ordinal);
        Assert.Contains("'Ft'", clash, StringComparison.Ordinal);
        Assert.Contains("'FT'", clash, StringComparison.Ordinal);
        Assert.Contains("by key only once case is ignored in version refs-1 of the cache of partition 'dev', and they replace it with different values", clash, StringComparison.Ordinal);

        // Agreeing rows make no difference which was meant.
        var agreeing = Renderer("""
              Symbol:
                $from: unit
                $modifiers:
                  - replace: $cache.UnitAlias
            """, Cache(UnitAlias(("Ft", "ft"), ("FT", "ft")))).Render(Record("ft"));
        Assert.Equal("ft", Data(agreeing, "Symbol"));
        Assert.Equal(2, agreeing.CacheUsages.Count(u => u.Kind == CacheUsageKind.Match));

        // A field holding several values cannot become one value; a set of one can.
        var aliases = new ReferenceType("Aliases", "reference-data--UnitOfMeasure",
        [
            new ReferenceItem("dev:reference-data--UnitOfMeasure:m", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = ReferenceValue.Of("m"),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse("""["metre", "meter"]""")!),
            }),
            new ReferenceItem("dev:reference-data--UnitOfMeasure:ft", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["Code"] = ReferenceValue.Of("ft"),
                ["Alias"] = ReferenceValue.From(JsonNode.Parse("""["foot"]""")!),
            }),
        ]);
        Assert.Contains(
            "Aliases 'dev:reference-data--UnitOfMeasure:m' holds 2 values at 'Alias' in version refs-1 of the cache of partition 'dev' ([\"metre\",\"meter\"]), and a replace turns 'm' into one value",
            Held("Aliases", Cache(aliases), "m", "match: Code\n        field: Alias"),
            StringComparison.Ordinal);
        var single = Renderer("""
              Symbol:
                $from: unit
                $modifiers:
                  - replace: $cache.Aliases
                    match: Code
                    field: Alias
            """, Cache(aliases)).Render(Record("ft"));
        Assert.Equal("foot", Data(single, "Symbol"));
    }

    [Fact]
    public void The_gate_checks_a_cached_table_and_lists_the_values_that_find_nothing_where_they_are_looked_up()
    {
        var schema = TestSchema.Build();
        IReadOnlyList<ValidationIssue> Check(string entries, ReferenceSnapshot cache)
            => Preflight.Check(TestSchema.Mapping(entries), schema, cache, TestSchema.Context(), sourceColumns: null);

        const string ThroughUnits = """
              Unit:
                $cache: UnitOfMeasure.id
                $findBy:
                  - Code = unit
                  - Name = unit
                $modifiers:
                  - replace: $cache.UnitAlias
            """;
        Assert.DoesNotContain(Check(ThroughUnits, Cache(UnitAlias())), i => i.Message.Contains("replace", StringComparison.Ordinal));

        // Values the dictionary gives that no unit holds, by code or by name, are listed before any row arrives.
        var unknown = Check(ThroughUnits, Cache(UnitAlias(("M", "m"), ("G/CM3", "g/cm3"), ("DEG", "dega"))));
        var warning = Assert.Single(unknown, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("its replace can give", StringComparison.Ordinal));
        Assert.Contains("2 of the 3 value(s) its replace can give find no UnitOfMeasure by Code/Name in cache version 'refs-1': 'dega', 'g/cm3'. A record whose value becomes one of them is held.", warning.Message, StringComparison.Ordinal);
        Assert.Equal("osdu.data.Unit", warning.Target);

        // A written table's values, and a text otherwise, are listed the same way; a case modifier after the replace is applied.
        var written = Check("""
              Unit:
                $cache: UnitOfMeasure.id
                $findBy: Code = unit
                $required: false
                $modifiers:
                  - replace: { METRE: M, FEET: FT }
                    otherwise: Unknown
                  - lower
            """, Cache());
        Assert.Contains(written, i => i.Severity == IssueSeverity.Warning
            && i.Message.Contains("1 of the 3 value(s) its replace can give find no UnitOfMeasure by Code in cache version 'refs-1': 'unknown'. A record whose value becomes one of them is left without it.", StringComparison.Ordinal));

        string Error(string modifier, ReferenceSnapshot cache)
        {
            var issues = Check($$"""
                  Symbol:
                    $from: unit
                    $modifiers:
                {{modifier}}
                """, cache);
            return Assert.Single(issues, i => i.Severity == IssueSeverity.Error).Message;
        }

        Assert.Contains("replace reads $cache.Nope, which cache version 'refs-1' does not hold. Cached: UnitAlias, UnitOfMeasure, Wellbore.", Error("      - replace: $cache.Nope", Cache(UnitAlias())), StringComparison.Ordinal);
        Assert.Contains("replace matches the value on 'colour' of UnitAlias, which cache version 'refs-1' does not cache", Error("      - replace: $cache.UnitAlias\n        match: colour", Cache(UnitAlias())), StringComparison.Ordinal);
        Assert.Contains("replace replaces the value by 'colour' of CurveClasses, which cache version 'refs-1' does not cache. Cached: id, curve_family, mnemonic, unit.", Error("      - replace: $cache.CurveClasses\n        field: colour", Cache(CurveClasses())), StringComparison.Ordinal);
        Assert.Contains("lookup table CurveClasses holds 2 fields beside its key mnemonic", Error("      - replace: $cache.CurveClasses", Cache(CurveClasses())), StringComparison.Ordinal);
        Assert.Contains("$cache.UnitOfMeasure holds OSDU records (reference-data--UnitOfMeasure), which have no key", Error("      - replace: $cache.UnitOfMeasure", Cache()), StringComparison.Ordinal);

        // An empty table replaces nothing, which is worth saying; it is not an error, since a refresh may fill it.
        var emptyTable = Check("""
              Symbol:
                $from: unit
                $modifiers:
                  - replace: $cache.Empty
            """, Cache(new ReferenceType("Empty", ReferenceType.LookupEntityType("Empty"), [], "key")));
        Assert.Contains(emptyTable, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("replace reads $cache.Empty, which holds no rows in cache version 'refs-1', so every value becomes what its otherwise says", StringComparison.Ordinal));
        Assert.DoesNotContain(emptyTable, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public async Task A_mapping_whose_only_cache_read_is_a_replace_renders_against_the_partitions_cache()
    {
        var templates = new MemoryTemplates(TestSchema.Build());
        var caches = _db.Caches();
        var directory = Samples.NewTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory, "Thing@1.0.0.yaml"), TestSchema.MappingDocument("""
              Symbol:
                $from: unit
                $modifiers:
                  - replace: $cache.UnitAlias
            """));
        var resolver = new RenderResolver(new MappingCatalog(directory, new DeliveryDocumentLoader()), caches, templates, new SecretResolver([new EnvSecretProvider()]));
        var flow = Samples.Targeting(new FlowTarget
        {
            Endpoint = "https://osdu.example.test",
            Protocol = DeliveryProtocol.Storage,
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [CacheScope.PartitionHeader] = "dev" },
        }) with
        {
            Render = new FlowRender
            {
                Mapping = "Thing@1.0.0",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" },
            },
        };

        // No version yet: the mapping reads the cache, so it cannot render without one.
        var none = await Assert.ThrowsAsync<FlowValidationException>(() => resolver.ResolveAsync(flow));
        Assert.Contains("reads the cache of partition 'dev', which holds no version yet", none.Message, StringComparison.Ordinal);

        var write = await caches.MergeAsync("dev", "lookups", [UnitAlias()], new CacheCapture(null, "tests", "dictionary"), T0);
        var resolved = await resolver.ResolveAsync(flow);
        Assert.Equal(("dev", write.Snapshot.Version), (resolved.Context.CacheScope, resolved.Context.CacheVersion));
        var result = resolved.Renderer.Render(Record("METRE"));
        Assert.Equal("m", Data(result, "Symbol"));
        Assert.Contains(new CacheUsage("UnitAlias", "METRE", "value", "m", CacheUsageKind.Value), result.CacheUsages);
    }

    [Fact]
    public async Task An_edited_dictionary_tags_exactly_the_records_that_used_the_entries_it_changed()
    {
        var ledger = _db.Ledger(_clock);
        var renderer = Renderer("""
              Symbol:
                $from: unit
                $required: false
                $modifiers:
                  - replace: $cache.UnitAlias
            """, Cache(UnitAlias()));
        var before = UnitAlias();

        // Five records built from METRE, three from FT, two from NONE (which gave no value), two from KB (unlisted).
        var metre = await DeliveredAsync(ledger, "METRE", 5, renderer.Render(Record("METRE")).CacheUsages);
        var feet = await DeliveredAsync(ledger, "FT", 3, renderer.Render(Record("FT")).CacheUsages);
        var none = await DeliveredAsync(ledger, "NONE", 2, renderer.Render(Record("NONE")).CacheUsages);
        var kb = await DeliveredAsync(ledger, "KB", 2, renderer.Render(Record("kb")).CacheUsages);
        Assert.Equal(12, metre.Count + feet.Count + none.Count + kb.Count);

        // METRE now gives metre, NONE gives m, and the dictionary comes to list kb; FT is untouched.
        var after = UnitAlias(("M", "m"), ("METRE", "metre"), ("FT", "ft"), ("NONE", "m"), ("kb", "m"));
        var impact = await new CacheImpactAnalyzer(ledger, _clock, NullLogger.Instance)
            .AnalyzeAsync("dev", before, after, CacheChangeMode.Approve, "v1", "v2");
        Assert.Equal(9, impact.AffectedRecords);

        var tags = await ledger.ListTagsAsync("pending", 20, 0);
        var changed = Assert.Single(tags, t => t.ItemId == "METRE");
        Assert.Equal(("value", "changed", "m", "metre", 5L), (changed.Path, changed.Change, changed.OldValue, changed.NewValue, changed.AffectedRecords));

        var gained = Assert.Single(tags, t => t.ItemId == "NONE");
        Assert.Equal(("value", "changed", (string?)null, "m", 2L), (gained.Path, gained.Change, gained.OldValue, gained.NewValue, gained.AffectedRecords));
        Assert.Contains("gave no value and now gives 'm'", gained.Describe(), StringComparison.Ordinal);

        var listed = Assert.Single(tags, t => t.Change == "listed");
        Assert.Equal(("kb", "value", "kb", "m", 2L), (listed.ItemId, listed.Path, listed.OldValue, listed.NewValue, listed.AffectedRecords));
        Assert.Contains("UnitAlias now lists 'kb' as 'kb', giving value = 'm'", listed.Describe(), StringComparison.Ordinal);

        Assert.DoesNotContain(tags, t => t.ItemId == "FT");
    }

    /// <summary>Delivers <paramref name="count"/> records keyed under <paramref name="prefix"/> that were built from <paramref name="usages"/>.</summary>
    private async Task<IReadOnlyList<DeliveryKey>> DeliveredAsync(OsduLedger ledger, string prefix, int count, IReadOnlyList<CacheUsage> usages)
    {
        var flow = FlowId.Of("replace-flow");
        await ledger.RegisterAsync(flow);
        var submission = Guid.NewGuid();
        await ledger.RegisterSubmissionAsync(new SubmissionState
        {
            SubmissionId = submission,
            FlowId = flow,
            FlowName = "replace-flow",
            MappingReference = "Thing@1.0.0",
            RenderContext = "{}",
            RecordCount = count,
            Status = SubmissionStatus.Planned,
            ReceivedUtc = _clock.GetUtcNow().UtcDateTime,
        });

        var setId = usages.Count == 0 ? (long?)null : await ledger.EnsureCacheSetAsync("dev", usages);
        var keys = new List<DeliveryKey>();
        var records = new List<RecordState>();
        for (var i = 0; i < count; i++)
        {
            var key = DeliveryKey.Derive("test", [$"{prefix}-{i}"]);
            keys.Add(key);
            records.Add(new RecordState
            {
                DeliveryKey = key,
                FlowId = flow,
                SourceKey = $"{prefix}-{i}",
                MappingName = "Thing",
                TargetId = $"dev:x:{prefix}-{i}",
                LastSubmissionId = submission,
                PendingDocumentRef = "0:0:10",
                PendingRenderContext = "{}",
                PendingMetadataHash = "mh",
                PendingMetadata = true,
                CacheSetId = setId,
            });
        }

        await ledger.UpsertPendingAsync(flow, records);
        return keys;
    }
}
