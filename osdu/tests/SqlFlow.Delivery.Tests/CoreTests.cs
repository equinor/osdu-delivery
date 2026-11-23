using SqlFlow.Delivery.Documents;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Planning;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Tests;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

public class CanonicalJsonTests
{
    [Fact]
    public void Sorts_keys_removes_nulls_and_normalises_numbers()
    {
        var canonical = CanonicalJson.Canonicalize("""{ "b": 1.0, "a": null, "c": { "z": 1e2, "y": [3, null, 0.5] }, "d": "x" }""");
        Assert.Equal("""{"b":1,"c":{"y":[3,null,0.5],"z":100},"d":"x"}""", canonical);
    }

    [Fact]
    public void Is_byte_stable_across_whitespace_and_key_order()
    {
        var a = CanonicalJson.Canonicalize("""{"x": {"k2": 2, "k1": 1}}""");
        var b = CanonicalJson.Canonicalize("""{ "x" : { "k1" : 1 , "k2" : 2 } }""");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Formats_doubles_shortest_round_trip()
    {
        Assert.Equal("0.1", CanonicalJson.FormatDouble(0.1));
        Assert.Equal("1E+21", CanonicalJson.FormatDouble(1e21));
        Assert.Throws<InvalidOperationException>(() => CanonicalJson.FormatDouble(double.NaN));
    }

    [Fact]
    public void Keeps_array_order()
    {
        var canonical = CanonicalJson.Canonicalize("""[{"b":1,"a":2},{"a":1}]""");
        Assert.Equal("""[{"a":2,"b":1},{"a":1}]""", canonical);
    }
}

public class DeterministicGuidTests
{
    [Fact]
    public void Matches_the_rfc_4122_version_5_test_vector()
    {
        var dns = new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        Assert.Equal(new Guid("2ed6657d-e927-568b-95e1-2665a8aea6a2"), DeterministicGuid.V5(dns, "www.example.com"));
    }

    [Fact]
    public void Delivery_key_is_deterministic_and_separator_safe()
    {
        var a = DeliveryKey.Derive("wells", ["AREA_A_1", "L-1001"]);
        var b = DeliveryKey.Derive("Wells ", ["AREA_A_1", "L-1001"]);
        Assert.Equal(a, b);
        Assert.NotEqual(DeliveryKey.Derive("wells", ["a|b", "c"]), DeliveryKey.Derive("wells", ["a", "b|c"]));
        Assert.Equal('5', a.ToString()[14]);
    }

    [Fact]
    public void Flow_id_ignores_case_and_whitespace()
    {
        Assert.Equal(FlowId.Of("welldb-welllog-03-header-delivery"), FlowId.Of(" WellDB-WellLog-03-Header-Delivery "));
        Assert.NotEqual(FlowId.Of("a"), FlowId.Of("b"));
    }

    [Fact]
    public void Target_id_uses_the_key_without_hyphens()
    {
        var key = DeliveryKey.Derive("wells", ["x"]);
        var id = TargetId.Compose("dev", "work-product-component--WellLog", key);
        Assert.StartsWith("dev:work-product-component--WellLog:", id, StringComparison.Ordinal);
        Assert.DoesNotContain("-", id[(id.LastIndexOf(':') + 1)..], StringComparison.Ordinal);
        Assert.Equal("work-product-component--WellLog", TargetId.EntityTypeFromKind("osdu:wks:work-product-component--WellLog:1.4.0"));
    }
}

public class ContentHashTests
{
    [Fact]
    public void Hashes_are_lower_case_sha256_hex_whether_given_text_or_its_bytes()
    {
        var hash = Hashing.ContentHash.Of("x");
        Assert.Equal(Hashing.ContentHash.HexLength, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.Equal(hash, Hashing.ContentHash.Of(System.Text.Encoding.UTF8.GetBytes("x")));
    }
}

public class SchemaSnapshotTests
{
    [Fact]
    public void Resolves_through_allOf_refs_arrays_and_additional_properties()
    {
        var schema = TestSchema.Build();
        Assert.Equal(SchemaType.String, schema.Resolve("data.Name")!.Type);
        Assert.Equal(SchemaType.Number, schema.Resolve("data.Depth")!.Type);
        Assert.Equal(SchemaType.Array, schema.Resolve("data.Curves")!.Type);
        Assert.Equal(SchemaType.Number, schema.Resolve("data.Curves.TopDepth")!.Type);
        Assert.Equal(SchemaType.String, schema.Resolve("tags.Anything")!.Type);
        Assert.Equal(SchemaType.Array, schema.Resolve("acl.owners")!.Type);
        Assert.True(schema.Resolve("data.WellboreID")!.IsRelationship);
        Assert.Null(schema.Resolve("data.Missing"));
    }

    [Fact]
    public void Reports_required_properties_per_level()
    {
        var schema = TestSchema.Build();
        Assert.Equal(["kind", "acl", "legal"], schema.RequiredAt(string.Empty));
        Assert.Equal(["Depth"], schema.RequiredAt("data"));
    }

    [Fact]
    public void Version_is_content_addressed()
    {
        var a = TestSchema.Build();
        var b = TestSchema.Build();
        Assert.Equal(a.Version, b.Version);
        Assert.Equal(16, a.Version.Length);
    }

    [Fact]
    public void Non_local_refs_are_rejected()
    {
        var schema = SchemaSnapshot.Parse("k:s:t:1.0.0", """{ "type": "object", "properties": { "data": { "$ref": "../other.json" } } }""", DateTimeOffset.UnixEpoch);
        Assert.Throws<DeliveryException>(() => schema.Resolve("data.X"));
    }
}

public class MappingRendererTests
{
    private static SourceRecord Record(string name = "well-1", string? depth = "12.5", string? unit = "m", string? flag = "REGULAR", string? wellbore = "WB 1/1-A", bool curves = true, string? when = "01.09.2026")
        => new()
        {
            Row = SourceRow.FromStrings(new Dictionary<string, string?>
            {
                ["name"] = name,
                ["depth"] = depth,
                ["unit"] = unit,
                ["wb"] = wellbore,
                ["flag"] = flag,
                ["when"] = when,
                ["pass"] = "MAIN,REPEAT",
            }),
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase)
            {
                ["curves"] = curves
                    ? [SourceRow.FromStrings(new Dictionary<string, string?> { ["curve_id"] = "GR", ["top"] = "1" }), SourceRow.FromStrings(new Dictionary<string, string?> { ["curve_id"] = "RHOB", ["top"] = "2" })]
                    : [],
            },
        };

    private static MappingRenderer Renderer(string data = "", ReferenceSnapshot? references = null, string record = "")
        => new(TestSchema.Mapping(data, record: record), TestSchema.Build(), references ?? TestSchema.References(), TestSchema.Context());

    [Fact]
    public void Writes_the_id_and_kind_and_converts_values_to_the_template_types()
    {
        var result = Renderer().Render(Record());
        Assert.False(result.IsHeld);
        Assert.Equal("dev:work-product-component--Thing:" + result.Key!.Value.Value.ToString("N"), result.TargetId);
        var data = result.Document["data"]!;
        Assert.Equal(12.5, data["Depth"]!.GetValue<double>());
        Assert.Equal("well-1", data["Name"]!.GetValue<string>());
        Assert.Equal("test:wks:work-product-component--Thing:1.0.0", result.Document["kind"]!.GetValue<string>());
        Assert.Equal("tag", result.Document["legal"]!["legaltags"]![0]!.GetValue<string>());
        Assert.Equal("owners@x", result.Document["acl"]!["owners"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Renders_cache_ids_repeaters_objects_and_modifiers()
    {
        var renderer = Renderer(
            """
            Unit:
              $cache: UnitOfMeasure.id
              $findBy: Code = unit
            WellboreID:
              $cache: Wellbore.id
              $findBy: FacilityName = wb
            IsRegular:
              $from: flag
              $modifiers:
                - equals: regular
            Nested:
              Inner:
                $from: name
                $modifiers: [upper]
            Description:
              $from: pass
              $modifiers:
                - split: { separator: ",", part: 2 }
            When:
              $from: when
              $modifiers:
                - date: dd.MM.yyyy
            Curves:
              $forEach: curves
              $item:
                CurveID: { $from: curve_id }
                TopDepth: { $from: top }
            """,
            record: """
            tags:
              Source: test
            """);
        var result = renderer.Render(Record());
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        var data = result.Document["data"]!;
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", data["Unit"]!.GetValue<string>());
        Assert.Equal("dev:master-data--Wellbore:abc:", data["WellboreID"]!.GetValue<string>());
        Assert.True(data["IsRegular"]!.GetValue<bool>());
        Assert.Equal("WELL-1", data["Nested"]!["Inner"]!.GetValue<string>());
        Assert.Equal("REPEAT", data["Description"]!.GetValue<string>());
        Assert.StartsWith("2026-09-01T00:00:00", data["When"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("test", result.Document["tags"]!["Source"]!.GetValue<string>());
        Assert.Equal(["GR", "RHOB"], data["Curves"]!.AsArray().Select(c => c!["CurveID"]!.GetValue<string>()));
        Assert.Equal(2, data["Curves"]![1]!["TopDepth"]!.GetValue<long>());
    }

    [Fact]
    public void An_empty_required_value_holds_the_record_and_an_empty_optional_value_is_left_out()
    {
        var required = Renderer("Description: { $from: missing }").Render(Record());
        Assert.True(required.IsHeld);
        Assert.Contains(required.Holds, h => h.Contains("osdu.data.Description: dataset.missing is empty", StringComparison.Ordinal));

        var optional = Renderer("Description: { $from: missing, $required: false }").Render(Record());
        Assert.False(optional.IsHeld);
        Assert.Null(optional.Document["data"]!["Description"]);
    }

    [Fact]
    public void A_cache_miss_holds_a_required_entry_and_leaves_an_optional_one_out()
    {
        const string Unit = """
            Unit:
              $cache: UnitOfMeasure.id
              $findBy: Code = unit
            """;
        var missing = Renderer(Unit).Render(Record(unit: "furlong"));
        Assert.True(missing.IsHeld);
        Assert.Contains(missing.Holds, h => h.Contains("no UnitOfMeasure matches 'furlong' by Code", StringComparison.Ordinal));

        var optional = Renderer(Unit + "\n  $required: false").Render(Record(unit: "furlong"));
        Assert.False(optional.IsHeld);
        Assert.Null(optional.Document["data"]!["Unit"]);

        var noKey = Renderer().Render(Record(name: " "));
        Assert.True(noKey.IsHeld);
        Assert.Null(noKey.Key);
    }

    [Fact]
    public void Several_records_holding_the_value_hold_the_record_even_when_the_entry_is_optional()
    {
        var references = new ReferenceSnapshot("refs-1", DateTimeOffset.UnixEpoch,
        [
            new ReferenceType("Wellbore", "master-data--Wellbore",
            [
                ReferenceItem.FromText("dev:master-data--Wellbore:one", new Dictionary<string, string> { ["FacilityName"] = "WB 1/1-A" }),
                ReferenceItem.FromText("dev:master-data--Wellbore:two", new Dictionary<string, string> { ["FacilityName"] = "WB 1/1-A" }),
            ]),
        ]);
        var result = Renderer("""
            WellboreID:
              $cache: Wellbore.id
              $findBy: FacilityName = wb
              $required: false
            """, references).Render(Record());
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h =>
            h.Contains("'WB 1/1-A' matches 2 Wellbore records by FacilityName exactly", StringComparison.Ordinal)
            && h.Contains("dev:master-data--Wellbore:one", StringComparison.Ordinal)
            && h.Contains("dev:master-data--Wellbore:two", StringComparison.Ordinal));
    }

    [Fact]
    public void Holds_a_unit_that_names_two_records_only_when_case_is_ignored()
    {
        var references = new ReferenceSnapshot("refs-1", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        [
            new ReferenceType("UnitOfMeasure", "reference-data--UnitOfMeasure",
            [
                ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:fT", new Dictionary<string, string> { ["Code"] = "fT", ["Name"] = "femtotesla" }),
                ReferenceItem.FromText("dev:reference-data--UnitOfMeasure:ft", new Dictionary<string, string> { ["Code"] = "ft", ["Name"] = "foot" }),
            ]),
        ]);
        var renderer = Renderer("""
            Unit:
              $cache: UnitOfMeasure.id
              $findBy:
                - Code = unit
                - Name = unit
            """, references);

        var feet = renderer.Render(Record(unit: "ft"));
        Assert.False(feet.IsHeld);
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft:", feet.Document["data"]!["Unit"]!.GetValue<string>());

        // The femtotesla comes first in the snapshot, and used to be what "FT" resolved to.
        var undecided = renderer.Render(Record(unit: "FT"));
        Assert.True(undecided.IsHeld);
        Assert.Contains(undecided.Holds, h =>
            h.Contains("'FT' matches 2 UnitOfMeasure records by Code once case is ignored", StringComparison.Ordinal)
            && h.Contains("dev:reference-data--UnitOfMeasure:fT", StringComparison.Ordinal)
            && h.Contains("dev:reference-data--UnitOfMeasure:ft", StringComparison.Ordinal));
    }

    [Fact]
    public void When_leaves_the_variable_out_for_rows_it_does_not_apply_to()
    {
        var renderer = Renderer("""
            Description:
              $from: name
              $when: flag = "REGULAR"
            Symbol:
              $value: flagged
              $when: not empty(flag)
            Count:
              $value: 1
              $when: flag != "regular"
            """);

        var regular = renderer.Render(Record(flag: "regular"));
        Assert.False(regular.IsHeld);
        Assert.Equal("well-1", regular.Document["data"]!["Description"]!.GetValue<string>());
        Assert.Equal("flagged", regular.Document["data"]!["Symbol"]!.GetValue<string>());
        Assert.Null(regular.Document["data"]!["Count"]);

        var discrete = renderer.Render(Record(flag: "DISCRETE"));
        Assert.False(discrete.IsHeld);
        Assert.Null(discrete.Document["data"]!["Description"]);
        Assert.Equal(1, discrete.Document["data"]!["Count"]!.GetValue<long>());

        var none = renderer.Render(Record(flag: null));
        Assert.Null(none.Document["data"]!["Symbol"]);
    }

    [Fact]
    public void A_repeater_without_rows_holds_when_required_and_is_written_empty_when_optional()
    {
        const string Curves = """
            Curves:
              $forEach: curves
              $item:
                CurveID: { $from: curve_id }
            """;
        var required = Renderer(Curves).Render(Record(curves: false));
        Assert.True(required.IsHeld);
        Assert.Contains(required.Holds, h => h.Contains("osdu.data.Curves: dataset.curves has no rows", StringComparison.Ordinal));

        var optional = Renderer(Curves + "\n  $required: false").Render(Record(curves: false));
        Assert.False(optional.IsHeld);
        Assert.Empty(Assert.IsType<JsonArray>(optional.Document["data"]!["Curves"]));
    }

    [Fact]
    public void Static_values_expand_parameters_and_take_the_template_type()
    {
        var result = Renderer("""
            Description: "{$param.dataPartition}-x"
            Count: "5"
            IsRegular: true
            Aliases: [one, two]
            """).Render(Record());
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        var data = result.Document["data"]!;
        Assert.Equal("dev-x", data["Description"]!.GetValue<string>());
        Assert.Equal(5, data["Count"]!.GetValue<long>());
        Assert.True(data["IsRegular"]!.GetValue<bool>());
        Assert.Equal(["one", "two"], data["Aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void Replace_takes_an_exact_key_first_and_otherwise_the_one_key_that_matches_ignoring_case()
    {
        var renderer = Renderer("""
            Unit:
              $cache: UnitOfMeasure.id
              $findBy: Code = unit
              $modifiers:
                - replace: { Metre: m, FEET: ft, feet: ft, Mtr: m, MTR: metre }
            """);
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", renderer.Render(Record(unit: "METRE")).Document["data"]!["Unit"]!.GetValue<string>());
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft:", renderer.Render(Record(unit: "feet")).Document["data"]!["Unit"]!.GetValue<string>());

        // Two keys match "Feet" once case is ignored, and both replace it with ft, so which one was meant makes no difference.
        Assert.Equal("dev:reference-data--UnitOfMeasure:ft:", renderer.Render(Record(unit: "Feet")).Document["data"]!["Unit"]!.GetValue<string>());

        // Two keys match "mTr" once case is ignored and they disagree, so the record is held, naming both keys.
        var ambiguous = renderer.Render(Record(unit: "mTr"));
        Assert.True(ambiguous.IsHeld);
        var reason = Assert.Single(ambiguous.Holds);
        Assert.Contains("'mTr' matches the replace keys", reason, StringComparison.Ordinal);
        Assert.Contains("'Mtr'", reason, StringComparison.Ordinal);
        Assert.Contains("'MTR'", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_gives_no_value_for_a_tilde_and_the_required_flag_decides()
    {
        var required = Renderer("""
            Symbol:
              $from: unit
              $modifiers:
                - replace: { NONE: ~, M: m }
                - upper
            """);
        Assert.Equal("M", required.Render(Record(unit: "m")).Document["data"]!["Symbol"]!.GetValue<string>());
        var held = required.Render(Record(unit: " none "));
        Assert.True(held.IsHeld);
        // The column held a value, and the replace is what turned it into none, which the reason says rather than calling the column empty.
        Assert.Contains("osdu.data.Symbol: dataset.unit is ' none ', which its modifiers (replace(NONE: ~, M: m) | upper) turn into no value, and the entry is required", held.Holds, StringComparer.Ordinal);

        var optional = Renderer("""
            Symbol:
              $from: unit
              $required: false
              $modifiers:
                - replace: { NONE: ~ }
            """);
        var left = optional.Render(Record(unit: "NONE"));
        Assert.False(left.IsHeld);
        Assert.Null(left.Document["data"]!["Symbol"]);
    }

    [Fact]
    public void Replace_otherwise_decides_what_an_unlisted_value_becomes()
    {
        string? Symbol(string otherwise, string unit)
        {
            var result = Renderer($$"""
                Symbol:
                  $from: unit
                  $required: false
                  $modifiers:
                    - replace: { M: m }
                      {{otherwise}}
                """).Render(Record(unit: unit));
            Assert.False(result.IsHeld);
            return result.Document["data"]!["Symbol"]?.GetValue<string>();
        }

        // Without otherwise an unlisted value passes on trimmed; with it the value is dropped, or becomes a fixed text.
        Assert.Equal("KB", Symbol(string.Empty, " KB "));
        Assert.Null(Symbol("otherwise: ~", "KB"));
        Assert.Equal("Unevaluated", Symbol("otherwise: Unevaluated", "KB"));
        Assert.Equal("~", Symbol("otherwise: \"~\"", "KB"));
        Assert.Equal("true", Symbol("otherwise: true", "KB"));

        // A listed value is never an unlisted one, and an empty value stays empty rather than becoming the fixed text.
        Assert.Equal("m", Symbol("otherwise: Unevaluated", "M"));
        Assert.Null(Symbol("otherwise: Unevaluated", "   "));
    }

    [Fact]
    public void A_date_that_is_not_a_date_holds_whatever_the_required_flag()
    {
        var result = Renderer("""
            When:
              $from: name
              $modifiers:
                - date: dd.MM.yyyy
              $required: false
            """).Render(Record());
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains("'well-1' is not a date/time in the format dd.MM.yyyy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2026-09-01", "2026-09-01T00:00:00Z")]
    [InlineData(" 2026-09-01 ", "2026-09-01T00:00:00Z")]
    [InlineData("2026-09-01T10:15:30Z", "2026-09-01T10:15:30Z")]
    [InlineData("2026-09-01t10:15:30z", "2026-09-01T10:15:30Z")]
    [InlineData("2026-09-01T12:15:30+02:00", "2026-09-01T10:15:30Z")]
    [InlineData("2026-09-01T05:15:30-0500", "2026-09-01T10:15:30Z")]
    [InlineData("2026-09-01 10:15", "2026-09-01T10:15:00Z")]
    [InlineData("2026-09-01T10:15:30.1250000Z", "2026-09-01T10:15:30.125Z")]
    [InlineData("2022-07-22T00:00:00.000000", "2022-07-22T00:00:00Z")]
    public void The_date_modifier_reads_ISO_8601_and_writes_an_RFC_3339_UTC_date_time(string incoming, string expected)
    {
        var result = Renderer("""
            When:
              $from: when
              $modifiers: [date]
            """).Render(Record(when: incoming));
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal(expected, result.Document["data"]!["When"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("01/09/2026")]
    [InlineData("09/01/2026")]
    [InlineData("12:30")]
    [InlineData("Sep 1 2026")]
    [InlineData("1 September 2026")]
    [InlineData("2026-02-30")]
    [InlineData("20260901")]
    [InlineData("26-09-01")]
    [InlineData("2026-09-01T25:00")]
    [InlineData("2026-09-01T10:15:30.123456789Z")]
    public void The_date_modifier_never_guesses_at_a_form_that_is_not_ISO_8601(string incoming)
    {
        var result = Renderer("""
            When:
              $from: when
              $modifiers: [date]
              $required: false
            """).Render(Record(when: incoming));
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains($"'{incoming}' is not an ISO 8601 date or date-time", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("dd.MM.yyyy", "01.09.2026", "2026-09-01T00:00:00Z")]
    [InlineData("yyyyMMdd", "20260901", "2026-09-01T00:00:00Z")]
    [InlineData("dd MMM yyyy HH:mm", "01 Sep 2026 10:15", "2026-09-01T10:15:00Z")]
    [InlineData("MM/dd/yyyy HH:mm:ss.fff", "04/01/2016 20:15:26.289", "2016-04-01T20:15:26.289Z")]
    [InlineData("yyyy-MM-dd'T'HH:mm:sszzz", "2026-09-01T12:15:30+02:00", "2026-09-01T10:15:30Z")]
    public void The_date_modifier_reads_exactly_the_format_given(string format, string incoming, string expected)
    {
        var result = Renderer($$"""
            When:
              $from: when
              $modifiers: [{ date: "{{format}}" }]
            """).Render(Record(when: incoming));
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal(expected, result.Document["data"]!["When"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("2026-09-01", "[date]")]
    [InlineData("2026-09-01T00:00:00Z", "[date]")]
    [InlineData("01.09.2026", "[{ date: dd.MM.yyyy }]")]
    [InlineData("2026-09-01T23:30:00Z", "[{ split: { separator: T, part: 1 } }, date]")]
    public void Where_the_template_takes_a_date_the_date_modifier_writes_a_full_date(string incoming, string modifiers)
    {
        var result = Renderer($$"""
            Day:
              $from: when
              $modifiers: {{modifiers}}
            """).Render(Record(when: incoming));
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("2026-09-01", result.Document["data"]!["Day"]!.GetValue<string>());
    }

    [Fact]
    public void A_time_of_day_where_the_template_takes_a_date_holds_rather_than_being_dropped()
    {
        var result = Renderer("""
            Day:
              $from: when
              $modifiers: [date]
            """).Render(Record(when: "2026-09-01T10:15:30Z"));
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains("osdu.data.Day: '2026-09-01T10:15:30+00:00' has a time of day, but the template takes a date", StringComparison.Ordinal));
    }

    [Fact]
    public void A_timestamp_from_the_drop_is_written_in_the_form_its_property_takes()
    {
        static SourceRecord Stamped(DateTimeOffset stamp) => new()
        {
            Row = new SourceRow(new Dictionary<string, object?> { ["name"] = "well-1", ["depth"] = 12.5, ["stamp"] = stamp }),
        };

        var renderer = Renderer("""
            When: { $from: stamp }
            Day: { $from: stamp }
            """);

        var midnight = renderer.Render(Stamped(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(midnight.IsHeld, string.Join("; ", midnight.Holds));
        Assert.Equal("2026-09-01T00:00:00Z", midnight.Document["data"]!["When"]!.GetValue<string>());
        Assert.Equal("2026-09-01", midnight.Document["data"]!["Day"]!.GetValue<string>());

        var afternoon = renderer.Render(Stamped(new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.Zero)));
        Assert.True(afternoon.IsHeld);
        Assert.Contains(afternoon.Holds, h => h.Contains("osdu.data.Day: '2026-09-01T14:00:00+00:00' has a time of day", StringComparison.Ordinal));
    }

    private static SourceRecord Valued(object? value) => new()
    {
        Row = new SourceRow(new Dictionary<string, object?> { ["name"] = "well-1", ["depth"] = 12.5, ["v"] = value }),
    };

    /// <summary>The canonical JSON a render wrote at one data property, after checking the record is not held.</summary>
    private static string Written(RenderResult result, string property)
    {
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        return CanonicalJson.ToString(result.Document["data"]![property]);
    }

    [Theory]
    [InlineData("12.5", "12.5")]
    [InlineData(" -0.25 ", "-0.25")]
    [InlineData("+7", "7")]
    [InlineData("12.0", "12")]
    [InlineData("1e3", "1000")]
    [InlineData(".5", "0.5")]
    [InlineData("-0", "0")]
    [InlineData("9007199254740993", "9007199254740993")]
    public void Text_is_written_as_the_number_it_states(string incoming, string expected)
        => Assert.Equal(expected, Written(Renderer("Weight: { $from: v }").Render(Valued(incoming)), "Weight"));

    [Theory]
    [InlineData("12,5", "separators this entry does not read")]
    [InlineData("1,234.5", "separators this entry does not read")]
    [InlineData("1 234", "separators this entry does not read")]
    [InlineData("NaN", "NaN and Infinity are not numbers")]
    [InlineData("-Infinity", "NaN and Infinity are not numbers")]
    [InlineData("1e400", "beyond the range of a double")]
    [InlineData("1e-400", "too close to zero")]
    [InlineData("abc", "not written as a number")]
    [InlineData("0x10", "not written as a number")]
    [InlineData("12.5 m", "not written as a number")]
    [InlineData("١٢", "not written as a number")]
    public void Text_that_does_not_state_a_number_holds_with_the_reason(string incoming, string reason)
    {
        var result = Renderer("Weight: { $from: v, $required: false }").Render(Valued(incoming));
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains($"osdu.data.Weight: value '{incoming}' is not a valid number", StringComparison.Ordinal) && h.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_number_from_the_drop_is_written_in_the_form_its_property_takes()
    {
        const string Weight = "Weight: { $from: v }";
        Assert.Equal("12.3", Written(Renderer(Weight).Render(Valued(12.3f)), "Weight"));
        Assert.Equal("12.5", Written(Renderer(Weight).Render(Valued(12.50m)), "Weight"));
        Assert.Equal(CanonicalJson.FormatDouble((double)1234567890.123456789m), Written(Renderer(Weight).Render(Valued(1234567890.123456789m)), "Weight"));
        Assert.Equal("9007199254740993", Written(Renderer(Weight).Render(Valued(9007199254740993L)), "Weight"));

        const string Symbol = "Symbol: { $from: v }";
        Assert.Equal("\"1234567890.123456789\"", Written(Renderer(Symbol).Render(Valued(1234567890.123456789m)), "Symbol"));
        Assert.Equal("\"12.5\"", Written(Renderer(Symbol).Render(Valued(12.50m)), "Symbol"));
        Assert.Equal("\"0.0001\"", Written(Renderer(Symbol).Render(Valued(0.0001m)), "Symbol"));
        Assert.Equal("\"12.3\"", Written(Renderer(Symbol).Render(Valued(12.3f)), "Symbol"));

        foreach (var entry in new[] { Weight, Symbol, "Count: { $from: v }" })
        {
            var infinite = Renderer(entry).Render(Valued(double.PositiveInfinity));
            Assert.True(infinite.IsHeld);
            Assert.Contains(infinite.Holds, h => h.Contains("NaN and Infinity are not numbers", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("12", "Count", "12")]
    [InlineData("12.0", "Count", "12")]
    [InlineData("1e3", "Count", "1000")]
    [InlineData("-2147483648", "Small", "-2147483648")]
    [InlineData("3000000000", "Big", "3000000000")]
    [InlineData("9223372036854775807", "Big", "9223372036854775807")]
    public void Text_fills_an_integer_when_it_is_a_whole_number_in_range(string incoming, string property, string expected)
        => Assert.Equal(expected, Written(Renderer($"{property}: {{ $from: v }}").Render(Valued(incoming)), property));

    [Theory]
    [InlineData("12.5", "Count", "it has a fraction")]
    [InlineData("3000000000", "Small", "the template declares int32 here")]
    [InlineData("9223372036854775808", "Big", "outside the 64-bit range")]
    [InlineData("1e30", "Big", "outside the 64-bit range")]
    [InlineData("true", "Count", "not written as a number")]
    public void Text_that_is_not_a_whole_number_in_range_holds_an_integer(string incoming, string property, string reason)
    {
        var result = Renderer($"{property}: {{ $from: v }}").Render(Valued(incoming));
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains($"osdu.data.{property}: value '{incoming}' is not a valid integer", StringComparison.Ordinal) && h.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_number_from_the_drop_fills_an_integer_only_when_it_is_exactly_a_whole_number_in_range()
    {
        static string Filled(string property, object value)
            => Written(Renderer($"{property}: {{ $from: v }}").Render(Valued(value)), property);

        static void Holds(string property, object value, string reason)
        {
            var result = Renderer($"{property}: {{ $from: v }}").Render(Valued(value));
            Assert.True(result.IsHeld);
            Assert.Contains(result.Holds, h => h.Contains($"osdu.data.{property}: value", StringComparison.Ordinal) && h.Contains(reason, StringComparison.Ordinal));
        }

        Assert.Equal("12", Filled("Count", 12.0));
        Assert.Equal("7", Filled("Count", 7.00m));
        Assert.Equal("9007199254740992", Filled("Count", 9007199254740992.0));
        Assert.Equal("3000000000", Filled("Big", 3000000000L));

        // An unchecked cast would have written long.MaxValue for each of the first two.
        Holds("Count", 1e19, "outside the 64-bit range");
        Holds("Count", 9007199254740994.0, "beyond 2^53");
        Holds("Count", 12.5, "it has a fraction");
        Holds("Small", 3000000000L, "the template declares int32 here");
        Holds("Count", true, "a boolean is not a number");
    }

    [Theory]
    [InlineData("12,5", "{ decimal: \",\" }", "12.5")]
    [InlineData("1 234 567,89", "{ decimal: \",\", group: \" \" }", "1234567.89")]
    [InlineData("1 234,5", "{ decimal: \",\", group: \" \" }", "1234.5")]
    [InlineData("1 234,5", "{ decimal: \",\", group: \" \" }", "1234.5")]
    [InlineData("1.234.567,89", "{ decimal: \",\", group: \".\" }", "1234567.89")]
    [InlineData("1234,5", "{ decimal: \",\", group: \".\" }", "1234.5")]
    [InlineData("1,234,567.89", "{ group: \",\" }", "1234567.89")]
    [InlineData("1'234.5", "{ group: \"'\" }", "1234.5")]
    [InlineData("−5,5", "{ decimal: \",\" }", "-5.5")]
    public void The_number_modifier_reads_text_written_with_the_separators_it_is_given(string incoming, string settings, string expected)
    {
        var result = Renderer($"Weight: {{ $from: v, $modifiers: [{{ number: {settings} }}] }}").Render(Valued(incoming));
        Assert.Equal(expected, Written(result, "Weight"));
    }

    [Theory]
    [InlineData("1.23,5", "{ decimal: \",\", group: \".\" }")]
    [InlineData("1.234,5", "{ decimal: \",\" }")]
    [InlineData("12.5", "{ decimal: \",\" }")]
    [InlineData("1,2,3", "{ group: \",\" }")]
    [InlineData("12,", "{ group: \",\" }")]
    public void The_number_modifier_holds_text_its_separators_do_not_read_rather_than_guess(string incoming, string settings)
    {
        var result = Renderer($"Weight: {{ $from: v, $required: false, $modifiers: [{{ number: {settings} }}] }}").Render(Valued(incoming));
        Assert.True(result.IsHeld);
        Assert.Contains(result.Holds, h => h.Contains($"osdu.data.Weight: value '{incoming}' is not a valid number", StringComparison.Ordinal));
    }

    [Fact]
    public void The_number_modifier_writes_text_as_its_shortest_exact_form()
    {
        var result = Renderer("Symbol: { $from: v, $modifiers: [{ number: { decimal: \",\", group: \" \" } }] }").Render(Valued("1 234,50"));
        Assert.Equal("\"1234.5\"", Written(result, "Symbol"));
    }

    [Fact]
    public void A_list_of_dates_writes_each_item_in_the_form_its_items_take()
    {
        var result = Renderer("Days: { $from: v, $modifiers: [date] }").Render(Valued("2026-09-01T00:00:00Z"));
        Assert.Equal("[\"2026-09-01\"]", Written(result, "Days"));
    }

    [Fact]
    public void A_static_list_of_objects_fills_an_array_of_objects_item_for_item()
    {
        var result = Renderer("Curves: [{ CurveID: MD, TopDepth: 0 }, { CurveID: GR }]").Render(Record());
        Assert.Empty(result.Holds);
        Assert.Equal("[{\"CurveID\":\"MD\",\"TopDepth\":0},{\"CurveID\":\"GR\"}]", Written(result, "Curves"));
    }

    [Fact]
    public void Holds_on_schema_required_property_missing_and_bad_number()
    {
        var result = Renderer().Render(Record(depth: null));
        Assert.Contains(result.Holds, h => h.Contains("osdu.data.Depth: dataset.depth is empty", StringComparison.Ordinal));
        Assert.Contains(result.Holds, h => h.Contains("schema-required property data.Depth rendered empty", StringComparison.Ordinal));
        var bad = Renderer().Render(Record(depth: "deep"));
        Assert.Contains(bad.Holds, h => h.Contains("not a valid number", StringComparison.Ordinal));
    }

    [Fact]
    public void Hash_is_of_the_document_so_a_new_render_context_that_renders_the_same_document_does_not_move_it()
    {
        // Seen live: every cache refresh writes a cache version, and a hash that took the render context in re-sent every
        // record rendered against the cache, identical documents included.
        var a = Renderer().Render(Record());
        var other = new MappingRenderer(TestSchema.Mapping(), TestSchema.Build(), TestSchema.References(), TestSchema.Context() with { CacheVersion = "refs-2" });
        var b = other.Render(Record());
        Assert.Equal(a.Canonical, b.Canonical);
        Assert.Equal(a.MetadataHash, b.MetadataHash);
        Assert.Equal(a.MetadataHash, Renderer().Render(Record()).MetadataHash);

        // A document that differs is what moves the hash.
        Assert.NotEqual(a.MetadataHash, Renderer().Render(Record(depth: "13.5")).MetadataHash);
    }

    [Fact]
    public void A_list_the_mapping_defines_is_written_empty_when_nothing_fills_it_and_one_it_does_not_define_is_left_out()
    {
        // The mapping decides which lists the record carries empty: a list it defines that gives nothing for the row, at any
        // depth; a list it does not define is left out, which Storage keeps as absent in a record's data. The record's own
        // meta is a field of Storage's record, which Storage holds as null when left out and the schema refuses, so it is
        // written empty whatever the mapping says.
        var root = TestSchema.Build().Root.DeepClone().AsObject();
        root["properties"]!["meta"] = JsonNode.Parse("""{ "type": "array", "items": { "type": "object" } }""");
        var data = root["properties"]!["data"]!["allOf"]![1]!["properties"]!.AsObject();
        data["Nested"]!["properties"]!["Codes"] = JsonNode.Parse("""{ "type": "array", "items": { "type": "string" } }""");
        data["Curves"]!["items"]!["properties"]!["Notes"] = JsonNode.Parse("""{ "type": "array", "items": { "type": "string" } }""");
        data["Mixed"] = JsonNode.Parse("""{ "type": "array", "items": { "type": ["string", "null"] } }""");
        var schema = new SchemaSnapshot(TestSchema.Kind, root, DateTimeOffset.UnixEpoch);
        MappingDefinition Mapping(string data = "", string record = "") => new DeliveryDocumentLoader().ParseMapping(
            TestSchema.MappingDocument(data, record: record).Replace(TestSchema.Build().Version, schema.Version, StringComparison.Ordinal), "thing.yaml");
        MappingRenderer Over(MappingDefinition mapping) => new(mapping, schema, TestSchema.References(), TestSchema.Context() with { SchemaSnapshotVersion = schema.Version });
        static SourceRow Row(params (string Column, string? Value)[] values) => SourceRow.FromStrings(values.ToDictionary(v => v.Column, v => v.Value));
        SourceRecord Rows(bool curves = true) => new()
        {
            Row = Row(("name", "well-1"), ("depth", "12.5"), ("alias", null), ("codes", null)),
            Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase)
            {
                ["curves"] = curves ? [Row(("curve_id", "GR"), ("note", null)), Row(("curve_id", "RHOB"), ("note", "checked"))] : [],
            },
        };
        const string Defines = """
            Aliases: { $from: alias, $required: false }
            Nested:
              Inner: { $from: name }
              Codes: { $from: codes, $required: false }
            Curves:
              $forEach: curves
              $required: false
              $item:
                CurveID: { $from: curve_id }
                Notes: { $from: note, $required: false }
            """;

        var result = Over(Mapping(Defines)).Render(Rows());
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        var document = result.Document;
        var body = document["data"]!.AsObject();
        Assert.Empty(Assert.IsType<JsonArray>(document["meta"]));
        Assert.Empty(Assert.IsType<JsonArray>(body["Aliases"]));
        Assert.Empty(Assert.IsType<JsonArray>(body["Nested"]!["Codes"]));
        var curves = body["Curves"]!.AsArray();
        Assert.Empty(Assert.IsType<JsonArray>(curves[0]!["Notes"]));
        Assert.Equal("checked", Assert.Single(curves[1]!["Notes"]!.AsArray())!.GetValue<string>());
        Assert.False(body.ContainsKey("Days"));
        Assert.False(body.ContainsKey("Mixed"));
        Assert.Empty(RecordValidator.Check(document, SchemaRules.Of(schema)).Problems);

        // A $forEach with no rows, or whose $when does not hold, writes its list empty; a required one holds the record.
        Assert.Empty(Assert.IsType<JsonArray>(Over(Mapping(Defines)).Render(Rows(curves: false)).Document["data"]!["Curves"]));
        var whenNot = Over(Mapping(Defines.Replace("$required: false\n  $item:", "$required: false\n  $when: name = \"other\"\n  $item:", StringComparison.Ordinal))).Render(Rows());
        Assert.Empty(Assert.IsType<JsonArray>(whenNot.Document["data"]!["Curves"]));

        // A list in an object the record does not hold stays out with it: no object is made for an empty list.
        var noObject = Over(Mapping("""
            Nested:
              Codes: { $from: codes, $required: false }
            """)).Render(Rows());
        Assert.False(noObject.Document["data"]!.AsObject().ContainsKey("Nested"));

        // Each item of a literal list of objects carries the lists it defines; one that does not define a list has none.
        var literal = Over(Mapping("""
            Curves:
              - CurveID: A
                Notes: { $from: alias, $required: false }
              - CurveID: B
            """)).Render(Rows());
        var items = literal.Document["data"]!["Curves"]!.AsArray();
        Assert.Empty(Assert.IsType<JsonArray>(items[0]!["Notes"]));
        Assert.False(items[1]!.AsObject().ContainsKey("Notes"));

        // A list the mapping fills is the one the record carries, and a template without a list of the record's own gets none.
        var written = Over(Mapping(record: """
            meta:
              - kind: Unit
                name: metre
            """)).Render(Rows());
        Assert.Equal("metre", Assert.Single(written.Document["meta"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.False(Renderer().Render(Record()).Document.ContainsKey("meta"));
        Assert.Equal(["meta"], new ListValues(schema).Names);
        Assert.Empty(new ListValues(schema, recordLists: false).Names);

        // A null item, as a list read whole from the cache may hold, is dropped where the template's items take no null and
        // kept where they take one; nothing else of the record is touched.
        var lists = new ListValues(schema);
        var nulls = TestSchema.Doc("""{"data":{"Name":null,"Aliases":["a",null,"b"],"Mixed":["a",null],"Curves":[{"CurveID":"GR","Notes":[null]},null]}}""");
        lists.RemoveNullItems(nulls);
        Assert.Equal(["a", "b"], nulls["data"]!["Aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(2, nulls["data"]!["Mixed"]!.AsArray().Count);
        Assert.Empty(Assert.Single(nulls["data"]!["Curves"]!.AsArray())!["Notes"]!.AsArray());
        Assert.True(nulls["data"]!.AsObject().ContainsKey("Name"));

        // An inspection of one variable drops null items only from the lists it covers.
        var covered = TestSchema.Doc("""{"data":{"Aliases":["a",null],"Days":[null]}}""");
        lists.RemoveNullItems(covered, path => path == "osdu.data.Aliases");
        Assert.Single(covered["data"]!["Aliases"]!.AsArray());
        Assert.Single(covered["data"]!["Days"]!.AsArray());
    }

    [Fact]
    public void A_renderer_refuses_a_template_version_the_mapping_does_not_pin()
    {
        var other = SchemaSnapshot.Parse(TestSchema.Kind, TestSchema.Build().Root.ToJsonString().Replace("\"Symbol\"", "\"Mark\"", StringComparison.Ordinal), DateTimeOffset.UnixEpoch);
        var ex = Assert.Throws<FlowValidationException>(() => new MappingRenderer(TestSchema.Mapping(), other, TestSchema.References(), TestSchema.Context()));
        Assert.Contains("the mapping fills template", ex.Message, StringComparison.Ordinal);
    }
}

public class PreflightTests
{
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> Columns(params string[] rootColumns)
        => new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["record"] = new HashSet<string>(rootColumns, StringComparer.OrdinalIgnoreCase),
            ["curves"] = new HashSet<string>(["curve_id", "top"], StringComparer.OrdinalIgnoreCase),
        };

    private static IReadOnlyList<ValidationIssue> Check(
        string data, IReadOnlyDictionary<string, IReadOnlySet<string>>? columns = null, string baseData = TestSchema.BaseData, RenderContext? context = null, string record = "")
        => Preflight.Check(TestSchema.Mapping(data, baseData, record), TestSchema.Build(), TestSchema.References(), context ?? TestSchema.Context(), columns);

    private static void HasError(IReadOnlyList<ValidationIssue> issues, string text)
        => Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void Passes_a_consistent_combination()
    {
        var issues = Preflight.Check(new DeliveryDocumentLoader().ParseMapping(TestSchema.MappingYaml, "m.yaml"), TestSchema.Build(), TestSchema.References(), TestSchema.Context(), Columns("name", "depth", "unit"));
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void Fails_on_missing_columns_an_unknown_variable_and_a_missing_cache_type()
    {
        var issues = Check("""
            Nope: { $from: depth }
            Unit:
              $cache: Country.id
              $findBy: Code = unit
            """, Columns("name"));
        HasError(issues, "reads dataset.depth, which the record table does not hold");
        HasError(issues, "fills a variable that template test:wks:work-product-component--Thing:1.0.0");
        HasError(issues, "reads Country from the cache, and cache version 'refs-1' does not hold it");
        Assert.Throws<FlowValidationException>(() => Preflight.ThrowIfFailed(issues, "test"));
    }

    [Fact]
    public void Fails_when_a_schema_required_property_has_no_entry_or_may_be_left_out()
    {
        HasError(Check(string.Empty, baseData: "Name: { $from: name }"), "requires osdu.data.Depth, which the mapping does not fill");

        var optionalDepth = TestSchema.BaseData.Replace("Depth: { $from: depth }", "Depth: { $from: depth, $required: false }", StringComparison.Ordinal);
        HasError(Check(string.Empty, baseData: optionalDepth), "is $required: false, but the template requires osdu.data.Depth");
    }

    [Fact]
    public void A_required_object_is_filled_by_the_entries_for_its_properties_when_one_of_them_always_renders()
    {
        // A template that requires an object whose properties the mapping fills, as WellboreTrajectory requires VerticalMeasurement.
        var schema = SchemaSnapshot.Parse(
            TestSchema.Kind,
            TestSchema.Build().Root.ToJsonString().Replace("\"required\":[\"Depth\"]", "\"required\":[\"Depth\",\"Nested\"]", StringComparison.Ordinal),
            DateTimeOffset.UnixEpoch);
        IReadOnlyList<ValidationIssue> Issues(string entries)
        {
            var yaml = TestSchema.MappingDocument(entries).Replace($"version: {TestSchema.Build().Version}", $"version: {schema.Version}", StringComparison.Ordinal);
            var mapping = new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml");
            return Preflight.Check(mapping, schema, TestSchema.References(), TestSchema.Context() with { SchemaSnapshotVersion = schema.Version }, null);
        }

        static bool Mentions(IReadOnlyList<ValidationIssue> issues) => issues.Any(i => i.Severity == IssueSeverity.Error && i.Message.Contains("osdu.data.Nested", StringComparison.Ordinal));

        HasError(Issues(string.Empty), "requires osdu.data.Nested, which the mapping does not fill");
        Assert.False(Mentions(Issues("Nested: { Inner: { $from: name } }")));
        Assert.False(Mentions(Issues("Nested: { Inner: fixed }")));

        var optional = Issues("Nested: { Inner: { $from: name, $required: false } }");
        HasError(optional, "requires osdu.data.Nested, and every entry filling its properties (");
        HasError(optional, " (record.data.Nested.Inner) may leave it out ($required: false or $when), so a record without it cannot be sent.");
        HasError(Issues("Nested: { Inner: { $from: name, $when: flag = \"yes\" } }"), "may leave it out ($required: false or $when)");
    }

    [Fact]
    public void Refuses_what_the_engine_and_OSDU_write()
    {
        HasError(Check(string.Empty, record: "id: x"), "osdu.id is written by OSDU Delivery");
        HasError(Check(string.Empty, record: "kind: x"), "osdu.kind is written by OSDU Delivery");
    }

    [Fact]
    public void Refuses_values_whose_shape_the_variable_does_not_take()
    {
        HasError(Check("""
            Nested:
              $forEach: curves
              $item:
                Inner: { $from: curve_id }
            """), "a $forEach fills a list of objects");
        HasError(Check("Nested: { $from: name }"), "is one value");
        HasError(Check("Symbol: { $value: { a: b } }"), "a literal object cannot be written");
        HasError(Check("Symbol: { $from: flag, $modifiers: [{ equals: yes }] }"), "the last modifier is equals");
        HasError(Check("Count: { $from: when, $modifiers: [date] }"), "the last modifier is date, which gives a date written as text");
        HasError(Check("Clock: { $from: when, $modifiers: [date] }"), "takes a time string, which a date is not written as");
        HasError(Check("IsRegular: { $from: v, $modifiers: [number] }"), "the last modifier is number, which gives a number, but the template takes a boolean");
        HasError(Check("When: { $from: v, $modifiers: [number] }"), "takes a date-time string, which a number is not written as");
        foreach (var target in new[] { "Weight", "Count", "Small", "Symbol" })
        {
            Assert.DoesNotContain(Check($"{target}: {{ $from: v, $modifiers: [number] }}"), i => i.Message.Contains("the last modifier is number", StringComparison.Ordinal));
        }

        // A list of values is judged by each item's type and format.
        HasError(Check("Days: { $from: v, $modifiers: [number] }"), "takes a list of date strings, which a number is not written as");
        Assert.DoesNotContain(Check("Days: { $from: v, $modifiers: [date] }"), i => i.Message.Contains("the last modifier is date", StringComparison.Ordinal));
    }

    [Fact]
    public void Warns_when_a_date_property_takes_dataset_text_without_the_date_modifier()
    {
        const string Warning = "add the date modifier so every record carries the RFC 3339 form";
        Assert.Contains(Check("When: { $from: when }"), i => i.Severity == IssueSeverity.Warning && i.Message.Contains(Warning, StringComparison.Ordinal));
        Assert.Contains(Check("Day: { $from: when }"), i => i.Severity == IssueSeverity.Warning && i.Message.Contains(Warning, StringComparison.Ordinal));
        Assert.Contains(Check("Days: { $from: when }"), i => i.Severity == IssueSeverity.Warning && i.Message.Contains("takes a list of date strings", StringComparison.Ordinal) && i.Message.Contains(Warning, StringComparison.Ordinal));
        Assert.DoesNotContain(Check("When: { $from: when, $modifiers: [date, trim] }"), i => i.Message.Contains(Warning, StringComparison.Ordinal));
        Assert.DoesNotContain(Check("Symbol: { $from: when }"), i => i.Message.Contains(Warning, StringComparison.Ordinal));
    }

    [Fact]
    public void Refuses_a_cache_id_of_another_entity_type_than_the_template_points_to()
    {
        HasError(Check("""
            WellboreID:
              $cache: UnitOfMeasure.id
              $findBy: Code = unit
            """), "writes the id of a cached UnitOfMeasure (reference-data--UnitOfMeasure), but the template points osdu.data.WellboreID to master-data--Wellbore");
    }

    [Fact]
    public void Checks_a_static_reference_against_the_cache_and_the_relationship()
    {
        var known = Check("Unit: \"{$param.dataPartition}:reference-data--UnitOfMeasure:m:\"");
        Assert.DoesNotContain(known, i => i.Severity == IssueSeverity.Error);

        HasError(Check("Unit: \"dev:reference-data--UnitOfMeasure:furlong:\""), "is not in cache version 'refs-1'");
        HasError(Check("Unit: \"dev:master-data--Wellbore:abc:\""), "is a master-data--Wellbore record, and osdu.data.Unit points to reference-data--UnitOfMeasure");
        HasError(Check("Unit: metre"), "'metre' is not an OSDU record id");
    }

    [Fact]
    public void Refuses_matching_by_fields_the_cache_does_not_hold()
    {
        HasError(Check("""
            Unit:
              $cache: UnitOfMeasure.id
              $findBy: NotCached = unit
            """), "caches none of those");
    }

    [Fact]
    public void Fails_on_an_undeclared_parameter()
    {
        var context = TestSchema.Context() with { Parameters = new Dictionary<string, string> { ["dataPartition"] = "dev", ["extra"] = "1" } };
        HasError(Check(string.Empty, context: context), "parameter 'extra'");
    }

    [Theory]
    [InlineData("fixtureDefaults:\n  parameters: { dataPartition: dev }\nfixtures:\n  - name: f\n    row: { name: w }\n    expected: \"{}\"\n", "delete its fixtureDefaults and fixtures blocks.")]
    [InlineData("fixtures:\n", "delete its fixtures block.")]
    public void A_mapping_that_still_holds_fixtures_is_refused_and_told_to_delete_them(string blocks, string expected)
    {
        // Mappings carried example rows and the records they had to render to; a document written then is told to delete the
        // blocks rather than given the parser's unknown key, and a key written with nothing under it is refused the same way.
        var ex = Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(TestSchema.MappingDocument() + blocks, "thing.yaml"));
        Assert.StartsWith("thing.yaml: a mapping holds no fixtures; ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }
}

public class ChangeDetectorTests
{
    private static RecordState Delivered(string fingerprint = "f1", string meta = "m1", string payload = "p1", string context = "c1") => new()
    {
        DeliveryKey = new DeliveryKey(Guid.NewGuid()),
        FlowId = Guid.NewGuid(),
        SourceKey = "k",
        MappingName = "M",
        Status = RecordStatus.Delivered,
        SourceFingerprint = fingerprint,
        MetadataHash = meta,
        PayloadHash = payload,
        RenderContext = context,
        TargetVersion = 5,
    };

    private static readonly DateTime At = new(2026, 9, 1, 10, 15, 0, DateTimeKind.Utc);

    [Fact]
    public void Tier1_skips_only_when_fingerprint_payload_and_context_all_match()
    {
        var change = new FlowChange();
        Assert.True(ChangeDetector.CanSkipWithoutRender(Delivered(), SourceVersion.Of("f1"), "p1", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered(), SourceVersion.Of("f2"), "p1", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered(), SourceVersion.Of("f1"), "p2", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered(), SourceVersion.Of("f1"), "p1", "c2", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(null, SourceVersion.Of("f1"), "p1", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered() with { Status = RecordStatus.Held }, SourceVersion.Of("f1"), "p1", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered(), SourceVersion.Of("f1"), "p1", "c1", new FlowChange { OnUnchanged = UnchangedAction.Deliver }));
        // A forgotten metadata hash means the ledger no longer knows what OSDU holds: only a render can answer.
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered() with { MetadataHash = null }, SourceVersion.Of("f1"), "p1", "c1", change));
        // A record asked to be planned again asks for a render, whatever version it was last planned under.
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered() with { PlanRequestedUtc = At }, SourceVersion.Of("f1"), "p1", "c1", change));
    }

    [Fact]
    public void Tier1_under_a_last_modified_column_compares_the_moment()
    {
        var change = new FlowChange();
        var delivered = Delivered() with { SourceFingerprint = null, SourceModifiedUtc = At };
        Assert.True(ChangeDetector.CanSkipWithoutRender(delivered, SourceVersion.At(At), "p1", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(delivered, SourceVersion.At(At.AddTicks(1)), "p1", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(Delivered(), SourceVersion.At(At), "p1", "c1", change));
        Assert.False(ChangeDetector.CanSkipWithoutRender(delivered with { PlanRequestedUtc = At }, SourceVersion.At(At), "p1", "c1", change));
    }

    [Fact]
    public void The_newest_version_counts_queued_work_but_not_where_a_blocked_record_was_left()
    {
        var queued = Delivered() with
        {
            Status = RecordStatus.Pending,
            SourceModifiedUtc = At,
            PayloadModifiedUtc = At,
            PendingDocumentRef = "0:0:1",
            PendingSourceModifiedUtc = At.AddHours(1),
            PendingPayload = true,
            PendingPayloadModifiedUtc = At.AddHours(2),
        };
        Assert.Equal(At.AddHours(1), ChangeDetector.NewestSourceModified(queued));
        Assert.Equal(At.AddHours(2), ChangeDetector.NewestPayloadModified(queued));
        Assert.Equal(At, ChangeDetector.NewestSourceModified(queued with { Status = RecordStatus.Held }));
        Assert.Equal(At, ChangeDetector.NewestPayloadModified(queued with { PendingPayload = false }));
        Assert.Null(ChangeDetector.NewestSourceModified(null));
    }

    [Fact]
    public void A_render_is_compared_with_the_queued_work_and_keeps_every_half_the_queue_would_have_changed()
    {
        var change = new FlowChange();
        var queued = Delivered() with { Status = RecordStatus.Pending, PendingDocumentRef = "0:0:1", PendingMetadata = true, PendingMetadataHash = "m2", PendingPayload = true, PendingPayloadHash = "p2" };

        // Identical to the queue: the queued work lands it, nothing new is staged.
        Assert.Equal(PlannedAction.Skip, ChangeDetector.DecideWithQueue(queued, "m2", "p2", true, change).Action);

        // Back to exactly what OSDU holds: the queue is replaced by work carrying both halves, so the worker's final
        // check settles it against whatever the target holds by then (a delivery of the queue may be in flight).
        var back = ChangeDetector.DecideWithQueue(queued, "m1", "p1", true, change);
        Assert.Equal(PlannedAction.UpdateBoth, back.Action);

        // The metadata moves again and the payload equals the queued one, which OSDU does not hold yet: both still go.
        var moved = ChangeDetector.DecideWithQueue(queued, "m3", "p2", true, change);
        Assert.True(moved.DeliverMetadata && moved.DeliverPayload);

        // Without queued work it is the ordinary tier 2.
        Assert.Equal(PlannedAction.Skip, ChangeDetector.DecideWithQueue(Delivered(), "m1", "p1", true, change).Action);
        Assert.Equal(PlannedAction.UpdatePayload, ChangeDetector.DecideWithQueue(Delivered(), "m1", "p2", true, change).Action);
    }

    [Fact]
    public void Queued_work_the_run_cannot_read_is_replaced_by_the_render_even_when_it_is_identical()
    {
        var change = new FlowChange();
        var queued = Delivered() with { Status = RecordStatus.Pending, PendingDocumentRef = "0:0:1", PendingMetadata = true, PendingMetadataHash = "m2", PendingPayload = true, PendingPayloadHash = "p2" };

        // Identical to the queue, which cannot land from here: the render replaces it and sends what OSDU does not hold yet.
        var same = ChangeDetector.DecideWithQueue(queued, "m2", "p2", true, change, queueReadable: false);
        Assert.Equal((PlannedAction.UpdateBoth, true, true), (same.Action, same.DeliverMetadata, same.DeliverPayload));
        Assert.Equal(QueueComparison.UnreadableQueued, same.Basis.Queue);
        Assert.EndsWith(ChangeBasis.UnreadableQueue, same.Reason, StringComparison.Ordinal);

        // Identical to the queue and to what OSDU holds: still replaced, so the record leaves work no run can send, and the
        // worker's final check settles it with nothing sent.
        var held = queued with { PendingMetadataHash = "m1", PendingPayloadHash = "p1" };
        var settles = ChangeDetector.DecideWithQueue(held, "m1", "p1", true, change, queueReadable: false);
        Assert.True(settles.IsDelivery);
        Assert.Equal((ChangeBasisKind.Unchanged, QueueComparison.UnreadableQueued), (settles.Basis.Kind, settles.Basis.Queue));

        // A record OSDU never held is created, whatever was queued for it.
        var never = queued with { TargetVersion = null, MetadataHash = null, PayloadHash = null };
        Assert.Equal(PlannedAction.Create, ChangeDetector.DecideWithQueue(never, "m2", "p2", true, change, queueReadable: false).Action);

        // Readable, the identical queue is still what lands it.
        Assert.Equal(PlannedAction.Skip, ChangeDetector.DecideWithQueue(queued, "m2", "p2", true, change, queueReadable: true).Action);
    }

    [Fact]
    public void The_final_check_sends_only_the_halves_the_target_does_not_already_hold()
    {
        var change = new FlowChange();
        var claimed = Delivered() with
        {
            Status = RecordStatus.Delivering,
            LastDeliveredUtc = At,
            PendingDocumentRef = "0:0:1",
            PendingMetadata = true,
            PendingMetadataHash = "m1",
            PendingPayload = true,
            PendingPayloadHash = "p1",
        };
        Assert.Equal((false, false), ChangeDetector.AtPush(claimed, change));
        Assert.Equal((true, false), ChangeDetector.AtPush(claimed with { PendingMetadataHash = "m2" }, change));
        Assert.Equal((false, true), ChangeDetector.AtPush(claimed with { PendingPayloadHash = "p2" }, change));
        Assert.Equal((false, false), ChangeDetector.AtPush(claimed with { PendingPayload = false, PendingPayloadHash = "p2" }, change));
        Assert.Equal((true, false), ChangeDetector.AtPush(claimed with { MetadataHash = null }, change));
        Assert.Equal((true, true), ChangeDetector.AtPush(claimed with { LastDeliveredUtc = null, TargetVersion = null }, change));
        Assert.Equal((true, true), ChangeDetector.AtPush(claimed, new FlowChange { OnUnchanged = UnchangedAction.Deliver }));
    }

    [Fact]
    public void A_blocked_record_moves_only_when_the_source_moves_past_where_it_was_left()
    {
        var blocked = Delivered() with { Status = RecordStatus.Held, Blocked = true, PendingSourceModifiedUtc = At, PendingSourceFingerprint = "f1" };
        Assert.True(ChangeDetector.StaysBlocked(blocked, SourceVersion.At(At), ordered: true));
        Assert.True(ChangeDetector.StaysBlocked(blocked, SourceVersion.At(At.AddDays(-1)), ordered: true));
        Assert.False(ChangeDetector.StaysBlocked(blocked, SourceVersion.At(At.AddTicks(1)), ordered: true));
        Assert.True(ChangeDetector.StaysBlocked(blocked, default, ordered: true));
        Assert.False(ChangeDetector.StaysBlocked(blocked with { PendingSourceModifiedUtc = null }, SourceVersion.At(At), ordered: true));
        Assert.True(ChangeDetector.StaysBlocked(blocked, SourceVersion.Of("f1"), ordered: false));
        Assert.False(ChangeDetector.StaysBlocked(blocked, SourceVersion.Of("f2"), ordered: false));
        Assert.True(ChangeDetector.StaysBlocked(blocked, default, ordered: false));
    }

    [Fact]
    public void A_blocked_record_of_an_ordered_flow_moves_when_the_ingestion_table_rewrites_its_row_at_the_same_moment()
    {
        var blocked = Delivered() with { Status = RecordStatus.Held, Blocked = true, PendingSourceModifiedUtc = At, PendingSourceFingerprint = "f1" };

        // The same row as it was left at stays blocked: a re-run never tries a known problem again.
        Assert.True(ChangeDetector.StaysBlocked(blocked, new SourceVersion("f1", At), ordered: true));

        // The row corrected in its file and loaded again: the business moment names the day the data describes and did not
        // move, the ingestion table stamped the row anew.
        Assert.False(ChangeDetector.StaysBlocked(blocked, new SourceVersion("f2", At), ordered: true));

        // A later moment moves it whatever the fingerprint; an older one is a late row and moves nothing, rewritten or not.
        Assert.False(ChangeDetector.StaysBlocked(blocked, new SourceVersion("f1", At.AddTicks(1)), ordered: true));
        Assert.True(ChangeDetector.StaysBlocked(blocked, new SourceVersion("f2", At.AddDays(-1)), ordered: true));

        // Left at no fingerprint, the ledger cannot see the row move at the same moment, under an order or none: the record waits
        // for an operator (a release, or a redelivery, whose request the planner honours whatever the record was left at).
        Assert.True(ChangeDetector.StaysBlocked(blocked with { PendingSourceFingerprint = null }, new SourceVersion("f1", At), ordered: true));
        Assert.True(ChangeDetector.StaysBlocked(blocked with { PendingSourceFingerprint = null }, SourceVersion.Of("f1"), ordered: false));
        Assert.False(ChangeDetector.StaysBlocked(blocked with { PendingSourceFingerprint = null }, new SourceVersion("f1", At.AddTicks(1)), ordered: true));
    }

    [Fact]
    public void A_record_held_at_its_render_is_rendered_again_under_other_rules_and_a_record_keeping_its_document_is_not()
    {
        var atRender = Delivered() with { Status = RecordStatus.Held, Blocked = true, PendingDocumentRef = null, LastError = "tags['lra.securityClassification']: dataset.Lra_securityClassification is empty, and the entry is required", HeldRulesHash = "v1" };

        // Held under the rules the plan renders with: it stays blocked. Under other rules, or rules no one recorded (a hold made
        // before the ledger kept them), it is rendered again.
        Assert.True(ChangeDetector.HeldAtRender(atRender));
        Assert.False(ChangeDetector.HeldUnderOtherRules(atRender, "v1"));
        Assert.True(ChangeDetector.HeldUnderOtherRules(atRender, "v2"));
        Assert.True(ChangeDetector.HeldUnderOtherRules(atRender with { HeldRulesHash = null }, "v1"));

        // Held keeping its document (an assertion of its mapping, a refused try): a release sends that document, and a change of
        // rules leaves it as it is.
        var kept = atRender with { PendingDocumentRef = "1:0:120", LastError = "assertion: the record fails what its mapping WellLog@1.4.0 asserts" };
        Assert.False(ChangeDetector.HeldAtRender(kept));
        Assert.False(ChangeDetector.HeldUnderOtherRules(kept, "v2"));

        // Held for an id OSDU holds: it waits for an adoption, by the id it kept or, held before the hold kept it, by its error.
        Assert.False(ChangeDetector.HeldUnderOtherRules(atRender with { OccupiedTargetId = "dev:work-product-component--WellLog:1" }, "v2"));
        Assert.False(ChangeDetector.HeldUnderOtherRules(atRender with { LastError = OccupiedHolds.Prefix + "dev:work-product-component--WellLog:1" }, "v2"));

        // Not held: a failed or reverted record keeps what put it there.
        Assert.False(ChangeDetector.HeldUnderOtherRules(atRender with { Status = RecordStatus.Failed }, "v2"));
        Assert.False(ChangeDetector.HeldUnderOtherRules(atRender with { Status = RecordStatus.Reverted }, "v2"));
    }

    [Fact]
    public void A_waiting_record_holds_queued_work_that_a_plan_compares_against_and_never_goes_back_from()
    {
        var change = new FlowChange();
        var waiting = Delivered() with
        {
            Status = RecordStatus.Waiting,
            PendingDocumentRef = "0:0:1",
            PendingMetadata = true,
            PendingMetadataHash = "m2",
            PendingSourceModifiedUtc = At.AddDays(2),
        };

        Assert.True(waiting.HasPendingWork);
        Assert.Equal(At.AddDays(2), ChangeDetector.NewestSourceModified(waiting));
        var identical = ChangeDetector.DecideWithQueue(waiting, "m2", "p1", true, change);
        Assert.Equal(PlannedAction.Skip, identical.Action);
        Assert.Contains("already queued", identical.Reason, StringComparison.Ordinal);
        Assert.False((waiting with { PendingDocumentRef = null }).HasPendingWork);
        Assert.False((waiting with { Status = RecordStatus.Held }).HasPendingWork);
    }

    [Fact]
    public void Tier2_decides_metadata_and_payload_independently()
    {
        var change = new FlowChange();
        Assert.Equal(PlannedAction.Create, ChangeDetector.Decide(null, "m1", "p1", true, change).Action);
        Assert.Equal(PlannedAction.Skip, ChangeDetector.Decide(Delivered(), "m1", "p1", true, change).Action);
        Assert.Equal(PlannedAction.UpdateMetadata, ChangeDetector.Decide(Delivered(), "m2", "p1", true, change).Action);
        Assert.Equal(PlannedAction.UpdatePayload, ChangeDetector.Decide(Delivered(), "m1", "p2", true, change).Action);
        Assert.Equal(PlannedAction.UpdateBoth, ChangeDetector.Decide(Delivered(), "m2", "p2", true, change).Action);
        Assert.Equal(PlannedAction.Skip, ChangeDetector.Decide(Delivered(), "m1", "p2", false, change).Action);
        Assert.Equal(PlannedAction.UpdateBoth, ChangeDetector.Decide(Delivered(), "m1", "p1", true, new FlowChange { OnUnchanged = UnchangedAction.Deliver }).Action);
    }
}

public class SourceVersionTests
{
    private static readonly DateTime At = new(2026, 9, 1, 10, 15, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("2026-09-01T10:15:00Z")]
    [InlineData("2026-09-01T12:15:00+02:00")]
    [InlineData("2026-09-01 10:15:00")]
    [InlineData(" 2026-09-01T10:15:00.0000000Z ")]
    public void Last_modified_text_is_read_as_a_utc_moment(string text)
    {
        var row = Rendering.SourceRow.FromStrings(new Dictionary<string, string?> { ["update_date"] = text });
        Assert.True(LastModifiedColumn.TryRead(row, "update_date", out var utc, out var problem));
        Assert.Null(problem);
        Assert.Equal(At, utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void Timestamps_are_taken_as_they_are_and_unreadable_values_say_why()
    {
        var zoned = new DateTimeOffset(2026, 9, 1, 12, 15, 0, TimeSpan.FromHours(2));
        var row = new Rendering.SourceRow(new Dictionary<string, object?>
        {
            ["zoned"] = zoned,
            ["unzoned"] = new DateTime(2026, 9, 1, 10, 15, 0, DateTimeKind.Unspecified),
            ["bad"] = "yesterday",
            ["blank"] = " ",
            ["number"] = 42L,
        });

        Assert.True(LastModifiedColumn.TryRead(row, "zoned", out var utc, out _));
        Assert.Equal(At, utc);
        Assert.True(LastModifiedColumn.TryRead(row, "unzoned", out utc, out _));
        Assert.Equal(At, utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);

        Assert.False(LastModifiedColumn.TryRead(row, "bad", out _, out var problem));
        Assert.Contains("'bad' holds 'yesterday'", problem, StringComparison.Ordinal);
        Assert.False(LastModifiedColumn.TryRead(row, "blank", out _, out problem));
        Assert.Contains("is empty", problem, StringComparison.Ordinal);
        Assert.False(LastModifiedColumn.TryRead(row, "absent", out _, out problem));
        Assert.Contains("is empty", problem, StringComparison.Ordinal);
        Assert.False(LastModifiedColumn.TryRead(row, "number", out _, out problem));
        Assert.Contains("Int64", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_files_take_the_newest_modified_time_and_sign_the_whole_set()
    {
        var early = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var late = early.AddHours(3);
        PayloadFile[] chunks =
        [
            new(0, "lake/drop1/curves/k/chunk_00000.parquet", 10, early),
            new(1, "lake/drop1/curves/k/chunk_00001.parquet", 20, late),
        ];

        var files = PayloadFiles.Of(chunks);
        Assert.Equal(2, files.Count);
        Assert.Equal(late.UtcDateTime, files.ModifiedUtc);

        // The same files read from another drop location are the same payload; a rewrite, a resize or a removal is not.
        Assert.Equal(files.Signature, PayloadFiles.Of([chunks[1] with { Path = "other/drop2/curves/k/chunk_00001.parquet" }, chunks[0] with { Path = @"other\drop2\curves\k\chunk_00000.parquet" }]).Signature);
        Assert.NotEqual(files.Signature, PayloadFiles.Of([chunks[0], chunks[1] with { Modified = late.AddSeconds(1) }]).Signature);
        Assert.NotEqual(files.Signature, PayloadFiles.Of([chunks[0], chunks[1] with { Size = 21 }]).Signature);
        Assert.NotEqual(files.Signature, PayloadFiles.Of([chunks[0]]).Signature);
        Assert.Null(PayloadFiles.Of([]).ModifiedUtc);
    }
}

public class JsonPathAndDiffTests
{
    [Fact]
    public void Reads_paths_indices_and_wildcards()
    {
        var root = JsonDocument.Parse("""{ "recordIdVersions": ["a:b:12", "c:d:13"], "data": { "token": "t" }, "items": [ { "id": 1 }, { "id": 2 } ] }""").RootElement;
        Assert.Equal("a:b:12", JsonPathReader.SelectValue(root, "recordIdVersions[0]"));
        Assert.Equal("t", JsonPathReader.SelectValue(root, "$.data.token"));
        Assert.Equal(["1", "2"], JsonPathReader.SelectValues(root, "items[*].id"));
        Assert.Null(JsonPathReader.SelectValue(root, "nope.x"));
        Assert.Equal(2, JsonPathReader.CountRecords(root, null));
    }

}

public class RenderContextTests
{
    [Fact]
    public void Canonical_form_round_trips_and_orders_parameters()
    {
        var context = new RenderContext
        {
            MappingReference = "M@1",
            CacheScope = "c",
            CacheVersion = "r",
            SchemaSnapshotVersion = "s",
            Parameters = new Dictionary<string, string> { ["z"] = "1", ["a"] = "2", ["dataPartition"] = "dev" },
        };
        var canonical = context.Canonical();
        Assert.StartsWith("{\"cache\":\"c\",\"cacheVersion\":\"r\",\"mapping\":\"M@1\",\"parameters\":{\"a\":\"2\",\"dataPartition\":\"dev\",\"z\":\"1\"}", canonical, StringComparison.Ordinal);
        Assert.Equal(canonical, RenderContext.Parse(canonical).Canonical());
        Assert.Equal("dev", RenderContext.Parse(canonical).DataPartition);
    }
}
