using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// Edges of reading the partition's cache: a blank cached value is no value, an id whose code holds colons is found by its
/// id, a record held for a reference the cache does not hold yet waits for it, a findBy line tried before the one that won
/// is recorded, a value the modifiers turn into nothing is named for what it held, and a quoted text reads the mapping's
/// parameters.
/// </summary>
public sealed class CachedLookupEdgeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ReferenceItem Item(string id, params (string Field, string Value)[] fields)
        => ReferenceItem.FromText(id, fields.ToDictionary(f => f.Field, f => f.Value, StringComparer.OrdinalIgnoreCase));

    private static RenderResult Render(string data, ReferenceSnapshot cache, params (string Column, string? Value)[] row)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "log-1", ["depth"] = "1" };
        foreach (var (column, value) in row)
        {
            values[column] = value;
        }

        return new MappingRenderer(TestSchema.Mapping(data), TestSchema.Build(), cache, TestSchema.Context())
            .Render(new SourceRecord { Row = SourceRow.FromStrings(values), Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase) });
    }

    /// <summary>A curve dictionary keyed by mnemonic, whose GR row holds a blank description and an empty list of aliases.</summary>
    private static ReferenceSnapshot Dictionary() => new("refs-1", T0,
    [
        new ReferenceType("Curves", "lookup--Curves", key: "mnemonic", items:
        [
            ReferenceItem.FromText("GR", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mnemonic"] = "GR", ["description"] = "  " }),
            new ReferenceItem("DT", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["mnemonic"] = ReferenceValue.From(System.Text.Json.Nodes.JsonValue.Create("DT")!),
                ["aliases"] = ReferenceValue.From(new System.Text.Json.Nodes.JsonArray()),
            }),
        ]),
    ]);

    [Fact]
    public void A_blank_cached_value_or_an_empty_list_is_no_value()
    {
        var required = Render("Symbol: { $cache: Curves.description, $findBy: mnemonic = mn }", Dictionary(), ("mn", "GR"));
        Assert.True(required.IsHeld);
        Assert.Contains("osdu.data.Symbol: Curves 'GR' caches nothing at 'description'", Assert.Single(required.Holds), StringComparison.Ordinal);
        Assert.Contains(new CacheUsage("Curves", "GR", "description", string.Empty, CacheUsageKind.Empty), required.CacheUsages);

        var optional = Render("Symbol: { $cache: Curves.description, $findBy: mnemonic = mn, $required: false }", Dictionary(), ("mn", "GR"));
        Assert.False(optional.IsHeld, string.Join("; ", optional.Holds));
        Assert.Null(optional.Document["data"]!["Symbol"]);

        var list = Render("Aliases: { $cache: Curves.aliases, $findBy: mnemonic = mn }", Dictionary(), ("mn", "DT"));
        Assert.True(list.IsHeld);
        Assert.Contains("caches nothing at 'aliases'", Assert.Single(list.Holds), StringComparison.Ordinal);

        // A node marked to restrict the record while its value is missing restricts it, the property left out.
        var yaml = TestSchema.MappingDocument("Symbol: { $cache: Curves.description, $findBy: mnemonic = mn, $restrictWhenMissing: true }")
            .Replace("\nrecord:\n", "\nrestrictedAccess: unclassified\nrecord:\n", StringComparison.Ordinal);
        var restriction = new RenderRestriction(3, "unclassified", ["data.restricted.viewers@x"], ["data.restricted.owners@x"]);
        var restricted = new MappingRenderer(new Documents.DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml"), TestSchema.Build(), Dictionary(), TestSchema.Context() with { RestrictedAccess = restriction })
            .Render(new SourceRecord
            {
                Row = SourceRow.FromStrings(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "log-1", ["depth"] = "1", ["mn"] = "GR" }),
                Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
            });
        Assert.False(restricted.IsHeld, string.Join("; ", restricted.Holds));
        Assert.Equal(restriction, restricted.Restriction);
        Assert.Null(restricted.Document["data"]!["Symbol"]);
    }

    /// <summary>The partition's coordinate reference systems, whose codes hold colons of their own.</summary>
    private static ReferenceSnapshot Systems() => new("refs-1", T0,
    [
        new ReferenceType("Crs", "reference-data--CoordinateReferenceSystem",
        [
            Item("dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::23031", ("Name", "ED50 / UTM zone 31N")),
        ]),
    ]);

    [Theory]
    [InlineData("dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::23031:")]
    [InlineData("dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::23031")]
    public void An_id_whose_code_holds_colons_is_found_by_its_id(string id)
    {
        var result = Render("Symbol: { $cache: Crs.Name, $findBy: Name = crs }", Systems(), ("crs", id));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("ED50 / UTM zone 31N", result.Document["data"]!["Symbol"]!.GetValue<string>());
        Assert.Contains(result.CacheUsages, u => u.Kind == CacheUsageKind.Match && u.Path == "id" && u.ItemId == "dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::23031");
    }

    [Fact]
    public void A_record_held_for_an_id_the_cache_does_not_hold_waits_for_that_record()
    {
        var held = Render("Symbol: { $cache: Crs.Name, $findBy: Name = crs }", Systems(), ("crs", "dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::4230:"));

        Assert.True(held.IsHeld);
        Assert.Contains(
            new CacheUsage("Crs", "dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::4230", "id", "dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::4230:", CacheUsageKind.Missing),
            held.CacheUsages);

        // An optional node leaves the property out and notes the same id, so a version holding the record reaches the record
        // delivered without it.
        var optional = Render("Symbol: { $cache: Crs.Name, $findBy: Name = crs, $required: false }", Systems(), ("crs", "dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::4230:"));
        Assert.False(optional.IsHeld, string.Join("; ", optional.Holds));
        Assert.Null(optional.Document["data"]!["Symbol"]);
        Assert.Contains(optional.CacheUsages, u => u.Kind == CacheUsageKind.Missing && u.ItemId == "dev:reference-data--CoordinateReferenceSystem:Projected:EPSG::4230");
    }

    [Fact]
    public void A_replace_reading_a_type_of_OSDU_records_notes_a_value_no_record_holds()
    {
        var result = Render(
            "Symbol: { $from: unit, $modifiers: [ { replace: $cache.UnitOfMeasure, match: Code, field: Name, otherwise: ~ } ], $required: false }",
            TestSchema.References(),
            ("unit", "furlong"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Contains(new CacheUsage("UnitOfMeasure", "FURLONG", "Code", "furlong", CacheUsageKind.Unlisted), result.CacheUsages);
    }

    [Fact]
    public void A_legal_list_written_with_value_nodes_is_refused_naming_them()
    {
        var ex = Assert.Throws<SqlFlow.Core.FlowValidationException>(() => AccessListTests.Mapping(record: """
            acl:
              owners: [owners@x]
              viewers: [viewers@x]
            legal:
              legaltags: [tag, { $value: other }]
              otherRelevantDataCountries: [US]
            """));
        Assert.Contains("record.legal.legaltags writes some of its items as $value nodes, and the legal tags are a literal list", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Write each item as the text it is.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_keys_of_a_literal_that_name_one_property_are_refused()
    {
        var spaced = Assert.Throws<SqlFlow.Core.FlowValidationException>(() => TestSchema.Mapping("Aliases: [ { \"a \": x, a: y } ]"));
        Assert.Contains("names the property 'a' twice", spaced.Message, StringComparison.Ordinal);

        var numbered = Assert.Throws<SqlFlow.Core.FlowValidationException>(() => TestSchema.Mapping("Nested: { $value: { 1.0: x, \"1\": y } }"));
        Assert.Contains("names the property '1' twice", numbered.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_held_for_a_cached_reference_to_a_record_the_cache_does_not_hold_waits_for_that_record()
    {
        var cache = new ReferenceSnapshot("refs-1", T0,
        [
            new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure", [Item("dev:reference-data--UnitOfMeasure:m", ("Code", "m"))]),
            new ReferenceType("SourceUnits", "reference-data--ExternalUnitOfMeasure",
            [
                Item("dev:reference-data--ExternalUnitOfMeasure:WellDB.GAPI", ("Code", "GAPI"), ("UnitOfMeasureID", "dev:reference-data--UnitOfMeasure:gAPI:")),
            ]),
        ]);

        var result = Render("Unit: { $cache: SourceUnits.UnitOfMeasureID, $findBy: Code = unit }", cache, ("unit", "GAPI"));

        Assert.True(result.IsHeld);
        Assert.Contains(new CacheUsage("UnitOfMeasure", "dev:reference-data--UnitOfMeasure:gAPI", "id", "dev:reference-data--UnitOfMeasure:gAPI:", CacheUsageKind.Missing), result.CacheUsages);
    }

    /// <summary>Wellbores two of which answer to one name only once case is ignored, and a third known by that name as an alias.</summary>
    private static ReferenceSnapshot Wellbores() => new("refs-1", T0,
    [
        new ReferenceType("Wellbore", "master-data--Wellbore",
        [
            Item("dev:master-data--Wellbore:upper", ("FacilityName", "WB-1")),
            Item("dev:master-data--Wellbore:lower", ("FacilityName", "wb-1")),
            Item("dev:master-data--Wellbore:alias", ("FacilityName", "WB ALIAS"), ("NameAliases.AliasName", "Wb-1")),
            Item("dev:master-data--Wellbore:dev", ("FacilityName", "dev-WB")),
            Item("dev:master-data--Wellbore:1234", ("FacilityName", "WB 1234")),
        ]),
    ]);

    [Theory]
    [InlineData("dev:master-data--Wellbore:9999:1700000000000", "dev:master-data--Wellbore:9999:1700000000000")]
    [InlineData("dev:master-data--Wellbore:9999", "dev:master-data--Wellbore:9999:")]
    [InlineData("dev:master-data--Wellbore:9999:", "dev:master-data--Wellbore:9999:")]
    public void An_id_the_cache_does_not_hold_is_written_as_a_reference_and_one_naming_a_version_keeps_it(string given, string written)
    {
        var result = Render("WellboreID: { $cache: Wellbore.id, $findBy: FacilityName = wb }", Wellbores(), ("wb", given));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal(written, result.Document["data"]!["WellboreID"]!.GetValue<string>());
    }

    [Fact]
    public void An_id_a_modifier_is_given_that_names_a_version_of_a_record_the_cache_holds_keeps_the_version()
    {
        // The separator added after the version would name a record whose code is '1234:1700000000000', which no cache holds.
        var result = Render("WellboreID: { $from: wb, $modifiers: [ ref ] }", Wellbores(), ("wb", "dev:master-data--Wellbore:1234:1700000000000"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("dev:master-data--Wellbore:1234:1700000000000", result.Document["data"]!["WellboreID"]!.GetValue<string>());
    }

    private const string ByNameThenAlias = """
        WellboreID:
          $cache: Wellbore.id
          $findBy:
            - FacilityName = wb
            - NameAliases.AliasName = wb
        """;

    [Fact]
    public void A_line_tried_before_the_one_that_found_the_record_is_recorded()
    {
        // The name finds no wellbore and the alias does: the name is recorded as found by none, so a version holding a
        // wellbore of that name, which the first line would find, reaches the record.
        var aliased = Render(ByNameThenAlias, new("refs-1", T0,
        [
            new ReferenceType("Wellbore", "master-data--Wellbore", [Item("dev:master-data--Wellbore:a", ("FacilityName", "A"), ("NameAliases.AliasName", "B-7"))]),
        ]), ("wb", "B-7"));
        Assert.False(aliased.IsHeld, string.Join("; ", aliased.Holds));
        Assert.Equal("dev:master-data--Wellbore:a:", aliased.Document["data"]!["WellboreID"]!.GetValue<string>());
        Assert.Contains(new CacheUsage("Wellbore", "B-7", "FacilityName", "B-7", CacheUsageKind.Unlisted), aliased.CacheUsages);

        // The name matches two wellbores once case is ignored and the alias one: each of the two is recorded as matched, so
        // a version that leaves the name to one of them reaches the record the alias found.
        var ambiguous = Render(ByNameThenAlias, Wellbores(), ("wb", "Wb-1"));
        Assert.False(ambiguous.IsHeld, string.Join("; ", ambiguous.Holds));
        Assert.Equal("dev:master-data--Wellbore:alias:", ambiguous.Document["data"]!["WellboreID"]!.GetValue<string>());
        Assert.Contains(new CacheUsage("Wellbore", "dev:master-data--Wellbore:upper", "FacilityName", "Wb-1", CacheUsageKind.Match), ambiguous.CacheUsages);
        Assert.Contains(new CacheUsage("Wellbore", "dev:master-data--Wellbore:lower", "FacilityName", "Wb-1", CacheUsageKind.Match), ambiguous.CacheUsages);
    }

    [Fact]
    public void A_value_the_modifiers_turn_into_nothing_is_named_for_what_it_held()
    {
        var result = Render(
            "Unit: { $cache: UnitOfMeasure.id, $findBy: Code = unit, $modifiers: [ { replace: { NONE: ~ } } ] }",
            TestSchema.References(),
            ("unit", "NONE"));

        Assert.True(result.IsHeld);
        var hold = Assert.Single(result.Holds);
        Assert.Contains("osdu.data.Unit: dataset.unit is 'NONE', which its modifiers (", hold, StringComparison.Ordinal);
        Assert.Contains(") turn into no value, so there is nothing to find in the cache, and the entry is required", hold, StringComparison.Ordinal);

        var blank = Render("Unit: { $cache: UnitOfMeasure.id, $findBy: Code = unit }", TestSchema.References(), ("unit", " "));
        Assert.Contains("osdu.data.Unit: dataset.unit is empty, so there is nothing to find in the cache, and the entry is required", Assert.Single(blank.Holds), StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_text_reads_the_mapping_s_parameters()
    {
        var result = Render("WellboreID: { $cache: Wellbore.id, $findBy: \"FacilityName = '{$param.dataPartition}-WB'\" }", Wellbores());

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("dev:master-data--Wellbore:dev:", result.Document["data"]!["WellboreID"]!.GetValue<string>());
    }
}
