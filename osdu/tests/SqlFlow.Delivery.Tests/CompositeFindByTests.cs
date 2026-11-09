using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.SearchTerms;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A findBy line that compares several fields at once (<c>well = well and mnemonic = mnemonic</c>): a lookup table keyed by
/// one unique column, read by the values of several of its columns together, and a type of OSDU records read the same way.
/// The line is read, matched, rendered, checked, outlined and written back by the builder as one line whose every comparison
/// a record must meet; the lines themselves are still tried in order.
/// </summary>
public sealed class CompositeFindByTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The curve types a well database's mnemonics stand for, per well: keyed by an identity column, since no one column tells
    /// the rows apart, and read by the well and the mnemonic together.
    /// </summary>
    internal static ReferenceType CurveMap(params (string Id, string Well, string Mnemonic, string CurveType)[] extra)
    {
        (string Id, string Well, string Mnemonic, string CurveType)[] rows =
        [
            ("1", "A-12", "GR", "GammaRay"),
            ("2", "A-12", "DT", "Sonic"),
            ("3", "B-1", "GR", "GammaRay B"),
            ("4", "C-3", "gr", "gamma lower"),
            ("5", "C-3", "Gr", "gamma mixed"),
            ("6", "D/4", "RHOB", "Density"),
            .. extra,
        ];
        return LookupCacheTests.Lookup("CurveMap", "row_id", rows.Select(row => (row.Id, (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
        {
            ["well"] = row.Well,
            ["mnemonic"] = row.Mnemonic,
            ["curve_type"] = row.CurveType,
        })).ToArray());
    }

    internal static ReferenceSnapshot References(ReferenceType? curveMap = null)
        => new("refs-1", T0, TestSchema.References().Types.Append(curveMap ?? CurveMap()));

    private static MappingRenderer Renderer(string data, ReferenceSnapshot? references = null)
        => new(TestSchema.Mapping(data), TestSchema.Build(), references ?? References(), TestSchema.Context());

    internal static SourceRecord Record(string? well, string? mnemonic, string? unit = null) => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "log-1", ["depth"] = "1", ["well"] = well, ["mnemonic"] = mnemonic, ["unit"] = unit,
        }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>The curve type of the row whose well and mnemonic are the row's, else of the row of the mnemonic alone.</summary>
    internal const string CurveType = """
        Symbol:
          $cache: CurveMap.curve_type
          $findBy:
            - well = well and mnemonic = mnemonic
            - mnemonic = mnemonic
          $required: false
        """;

    private static MappingEntry Symbol(MappingDefinition mapping) => Assert.Single(mapping.Entries, e => e.Target.Text == "osdu.data.Symbol");

    private static string Refusal(string data)
        => Assert.Throws<FlowValidationException>(() => TestSchema.Mapping(data)).Message;

    private static string? Written(RenderResult result) => result.Document["data"]?["Symbol"]?.GetValue<string>();

    [Fact]
    public void A_line_joined_by_and_compares_each_field_and_the_lines_are_still_tried_in_order()
    {
        var entry = Symbol(TestSchema.Mapping(CurveType.Replace("well = well and", "well = well AND", StringComparison.Ordinal)));

        Assert.Equal(2, entry.FindBy.Count);
        var line = entry.FindBy[0];
        Assert.True(line.IsComposite);
        Assert.Equal(["well", "mnemonic"], line.Comparisons.Select(c => c.Field));
        Assert.Equal(["dataset.well", "dataset.mnemonic"], line.Comparisons.Select(c => c.Column!.ToString()));
        Assert.All(line.Comparisons, c => Assert.Equal("CurveMap", c.Type));
        Assert.Equal("cache.CurveMap.well = dataset.well and cache.CurveMap.mnemonic = dataset.mnemonic", line.ToString());
        Assert.False(entry.FindBy[1].IsComposite);
        Assert.Equal([line], line.Comparisons.Take(1));
    }

    [Fact]
    public void An_and_inside_quotes_is_text_and_a_literal_may_stand_beside_a_column()
    {
        var entry = Symbol(TestSchema.Mapping("""
            Symbol:
              $cache: CurveMap.curve_type
              $findBy: "well = 'A and B' and mnemonic = mnemonic"
            """));

        var line = Assert.Single(entry.FindBy);
        Assert.Equal([("well", (string?)"A and B", (string?)null), ("mnemonic", null, "dataset.mnemonic")],
            line.Comparisons.Select(c => (c.Field, c.Literal, c.Column?.ToString())));
        Assert.Equal(["Rock and Roll"], MappingMapper.AndParts("name = 'Rock and Roll'").Select(p => p.Split('\'')[1]));
        Assert.Equal(["a = b", "c = d"], MappingMapper.AndParts("a = b\tand\tc = d"));
        Assert.Equal(["brand = x"], MappingMapper.AndParts("brand = x"));
    }

    [Theory]
    [InlineData("well = well and", "has nothing on one side of an and")]
    [InlineData("and well = well", "has nothing on one side of an and")]
    [InlineData("well = well and  and mnemonic = mnemonic", "has nothing on one side of an and")]
    [InlineData("well = well and well = mnemonic", "compares well twice")]
    [InlineData("data.well = well and well = mnemonic", "compares well twice")]
    [InlineData("well = well and mnemonic mnemonic", "'mnemonic mnemonic' must read <field> = <column>")]
    [InlineData("well = well and mnemonic = ''", "'mnemonic = ''' compares with empty text")]
    public void A_line_joined_by_and_is_refused_when_a_side_is_missing_twice_or_malformed(string line, string message)
        => Assert.Contains(message, Refusal($"""
            Symbol:
              $cache: CurveMap.curve_type
              $findBy: "{line}"
            """), StringComparison.Ordinal);

    [Fact]
    public void A_search_and_a_find_all_take_no_line_joined_by_and()
    {
        var search = Assert.Throws<FlowValidationException>(() => SearchSourceTests.Mapping("""
            WellboreID:
              $search: Wellbore
              $findBy: data.FacilityName = name and data.WellboreName = name
            """)).Message;
        Assert.Contains("a search asks the platform for one property at a time; write each as a line of its own", search, StringComparison.Ordinal);

        Assert.Contains("joins conditions with and; a $findAll takes each condition as a line of its own", Refusal("""
            Aliases:
              $cache: CurveMap.curve_type
              $findAll: well = well and mnemonic = mnemonic
            """), StringComparison.Ordinal);
    }

    [Fact]
    public void Modifiers_change_every_column_a_line_compares_and_are_refused_where_every_comparison_is_a_fixed_text()
    {
        var renderer = Renderer("""
            Symbol:
              $cache: CurveMap.curve_type
              $findBy: well = well and mnemonic = mnemonic
              $modifiers: [upper]
            """);
        Assert.Equal("GammaRay", Written(renderer.Render(Record("a-12", "gr"))));

        Assert.Contains("this lookup's $findBy reads none", Assert.Throws<FlowValidationException>(() => new DeliveryDocumentLoader().ParseMapping(
            TestSchema.MappingDocument("Symbol: { $lookup: curve.curve_type }").Replace("parameters:", """
                lookups:
                  curve:
                    $cache: CurveMap
                    $findBy: well = 'A-12' and mnemonic = 'GR'
                    $modifiers: [upper]
                parameters:
                """, StringComparison.Ordinal), "thing.yaml")).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The curve map with a row whose well is a set of more values than a combination is indexed for, so the well and the
    /// mnemonic are matched by reading candidates instead: what it finds must be what the index finds.
    /// </summary>
    private static ReferenceType Wide()
    {
        var map = CurveMap();
        var wells = new System.Text.Json.Nodes.JsonArray(Enumerable.Range(0, ReferenceType.MaxCombinationsPerItem + 1).Select(i => (System.Text.Json.Nodes.JsonNode?)$"W-{i}").ToArray());
        var wide = new ReferenceItem("99", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["row_id"] = ReferenceValue.Of("99"),
            ["well"] = ReferenceValue.From(wells),
            ["mnemonic"] = ReferenceValue.Of("ZZZ"),
            ["curve_type"] = ReferenceValue.Of("Wide"),
        });
        return new ReferenceType(map.Name, map.EntityType, map.Items.Append(wide), map.Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_row_meeting_every_value_is_found_exactly_first_then_without_case_and_never_by_one_value_alone(bool wide)
    {
        var map = wide ? Wide() : CurveMap();
        if (wide)
        {
            Assert.Equal("99", map.Find([("well", "w-64"), ("mnemonic", "ZZZ")]).Item!.Id);
        }

        ReferenceMatch Find(string well, string mnemonic, bool fold = false) => map.Find([("well", well), ("mnemonic", mnemonic)], fold);

        Assert.Equal(("1", ReferenceMatchKind.Exact), (Find("A-12", "GR").Item!.Id, Find("A-12", "GR").Kind));
        Assert.Equal("3", Find("B-1", "GR").Item!.Id);
        Assert.Equal(("1", ReferenceMatchKind.IgnoringCase), (Find("a-12", "gr").Item!.Id, Find("a-12", "gr").Kind));

        // Each value is held by some row, but no row holds both: nothing, whatever the rows of either value alone say.
        Nothing(Find("B-1", "DT"));

        // Two rows meet the values once case is ignored, and neither exactly: neither is taken.
        var both = Find("C-3", "GR");
        Assert.True(both.IsCaseAmbiguous);
        Assert.Equal(["4", "5"], both.CaseVariants.Select(r => r.Id));
        Assert.Equal("4", Find("C-3", "gr").Item!.Id);

        // Punctuation and spacing fold away only when asked, as for one value.
        Nothing(Find("D-4", "RHOB"));
        Assert.Equal(("6", ReferenceMatchKind.IgnoringSeparators), (Find("D-4", "RHOB", fold: true).Item!.Id, Find("D-4", "RHOB", fold: true).Kind));

        // An empty value meets nothing, and one comparison is the single-field match.
        Nothing(map.Find([("well", "A-12"), ("mnemonic", " ")]));
        Assert.Equal("2", map.Find([("mnemonic", "DT")]).Item!.Id);
        Assert.Throws<ArgumentException>(() => map.Find([]));
    }

    [Fact]
    public void A_field_holding_a_set_meets_a_comparison_by_any_of_its_values_and_a_record_id_by_its_id()
    {
        var wellbores = new ReferenceType("Wellbore", "master-data--Wellbore",
        [
            new ReferenceItem("dev:master-data--Wellbore:w1", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["FieldName"] = ReferenceValue.Of("TROLL"),
                ["Aliases"] = ReferenceValue.From(System.Text.Json.Nodes.JsonNode.Parse("""["A-1", "TR-A-1"]""")!),
            }),
            new ReferenceItem("dev:master-data--Wellbore:w2", new Dictionary<string, ReferenceValue>(StringComparer.OrdinalIgnoreCase)
            {
                ["FieldName"] = ReferenceValue.Of("OSEBERG"),
                ["Aliases"] = ReferenceValue.From(System.Text.Json.Nodes.JsonNode.Parse("""["A-1"]""")!),
            }),
        ]);

        Assert.Equal("dev:master-data--Wellbore:w2", wellbores.Find([("FieldName", "OSEBERG"), ("Aliases", "A-1")]).Item!.Id);
        Assert.Equal("dev:master-data--Wellbore:w1", wellbores.Find([("FieldName", "TROLL"), ("Aliases", "tr-a-1")]).Item!.Id);
        Assert.Equal("dev:master-data--Wellbore:w1", wellbores.Find([("id", "dev:master-data--Wellbore:w1"), ("Aliases", "A-1")]).Item!.Id);
        Assert.Equal("dev:master-data--Wellbore:w1", wellbores.Find([("id", "w1"), ("FieldName", "TROLL")]).Item!.Id);
        Assert.True(wellbores.Find([("Aliases", "A-1"), ("id", "dev:master-data--Wellbore:w1")]).Item is not null);
    }

    [Fact]
    public void A_render_writes_what_the_row_meeting_every_value_gives_and_depends_on_each_value_it_was_found_by()
    {
        var renderer = Renderer(CurveType);

        var found = renderer.Render(Record("A-12", "GR"));
        Assert.False(found.IsHeld);
        Assert.Equal("GammaRay", Written(found));
        Assert.Equal(
            [("1", "well", "A-12", CacheUsageKind.Match), ("1", "mnemonic", "GR", CacheUsageKind.Match), ("1", "curve_type", "GammaRay", CacheUsageKind.Value)],
            found.CacheUsages.Where(u => u.TypeName == "CurveMap").Select(u => (u.ItemId, u.Path, u.Value, u.Kind)));

        // The same mnemonic on another well finds that well's row, not the first row of the mnemonic.
        Assert.Equal("GammaRay B", Written(renderer.Render(Record("B-1", "GR"))));

        // A well the table does not list for the mnemonic falls through to the next line, the mnemonic alone.
        Assert.Equal("Sonic", Written(renderer.Render(Record("Z-9", "DT"))));

        // A line whose value is empty is skipped, as a line of one is.
        Assert.Equal("Sonic", Written(renderer.Render(Record(null, "DT"))));
    }

    [Fact]
    public void Several_rows_meeting_the_values_hold_the_record_whatever_required_says_and_name_the_rows()
    {
        var held = Renderer(CurveType).Render(Record("C-3", "GR"));

        Assert.True(held.IsHeld);
        Assert.Equal(
            "osdu.data.Symbol: well 'C-3' and mnemonic 'GR' match 2 CurveMap records once case is ignored (4, 5) in version refs-1 of the cache of partition 'dev'; make the incoming values exact with a replace modifier, or compare another field that tells them apart",
            Assert.Single(held.Holds));
    }

    [Fact]
    public void A_miss_names_every_value_of_every_line_and_notes_the_values_tried_together()
    {
        var renderer = Renderer("""
            Symbol:
              $cache: CurveMap.curve_type
              $findBy: [well = well and mnemonic = mnemonic, mnemonic = unit]
            """);

        var held = renderer.Render(Record("Z-9", "NPHI", "furlong"));
        Assert.Equal(
            "osdu.data.Symbol: no CurveMap matches well 'Z-9' and mnemonic 'NPHI' or mnemonic 'furlong' in version refs-1 of the cache of partition 'dev'",
            Assert.Single(held.Holds));

        // The values the line compared together are noted together, of a lookup table as of any type; the line of one value on
        // a lookup table is noted as it always was, not at all.
        var noted = Assert.Single(held.CacheUsages, u => u.TypeName == "CurveMap");
        Assert.Equal(CacheUsageKind.Unlisted, noted.Kind);
        Assert.Equal(CacheUsage.CompositeKey(["Z-9", "NPHI"], ignoreSeparators: false), noted.ItemId);
        Assert.Equal(" [\"Z-9\",\"NPHI\"]", noted.ItemId);
        Assert.Equal("well and mnemonic", noted.Path);
        Assert.Equal("[\"Z-9\",\"NPHI\"]", noted.Value);

        var inspected = Assert.Single(renderer.Inspect(Record("Z-9", "NPHI", "furlong"), EntrySelection.All).Outcomes, o => o.Target == "osdu.data.Symbol");
        var miss = Assert.Single(inspected.Misses);
        Assert.Equal(RenderMissKind.NoMatch, miss.Kind);
        Assert.Equal([("well", "Z-9"), ("mnemonic", "NPHI"), ("mnemonic", "furlong")], miss.Tries.Select(t => (t.Field, t.Value)));
        Assert.Equal(["dataset.well", "dataset.mnemonic", "dataset.unit"], miss.Columns.Select(c => c.ToString()));
    }

    [Fact]
    public void A_value_check_says_the_values_were_looked_up_together_and_never_one_of_them_alone()
    {
        var renderer = Renderer("""
            Symbol:
              $cache: CurveMap.curve_type
              $findBy: well = well and mnemonic = mnemonic
            """);
        var miss = Assert.Single(Assert.Single(renderer.Inspect(Record("Z-9", "NPHI"), EntrySelection.All).Outcomes, o => o.Target == "osdu.data.Symbol").Misses);
        Assert.Equal([false, true], miss.Tries.Select(t => t.And));

        var cause = Assert.Single(ValueTally.Causes("held", null, [miss], null));
        Assert.True(cause.Together);
        Assert.Equal(["well", "mnemonic"], cause.Fields);

        var finding = new ValueCheckFinding
        {
            Outcome = "held", At = "osdu.data.Symbol", Message = miss.Reason, Count = 2, Rows = 2,
            Values = [new ValueCheckValue("Z-9" + ValueTally.TogetherSeparator + "NPHI", 2)], Causes = [cause],
        };
        var said = ValueCheckExplainer.Explain(
            finding, new ValueCheckVariable { Target = "osdu.data.Symbol", Rows = new ValueCheckCounts(), Required = true }, new ValueCheckInputs("Thing@1.0.0", TestSchema.Kind, "abc", "dev", "refs-1"));
        Assert.Equal("No `CurveMap` in the cache holds `well` and `mnemonic` together as `Z-9 / NPHI`", said.Headline);
    }

    [Fact]
    public void Nothing_to_compare_on_any_line_names_the_columns_that_were_empty()
    {
        var held = Renderer("""
            Symbol:
              $cache: CurveMap.curve_type
              $findBy: well = well and mnemonic = mnemonic
            """).Render(Record("A-12", " "));

        Assert.Equal("osdu.data.Symbol: dataset.mnemonic is empty, so there is nothing to find in the cache, and the entry is required", Assert.Single(held.Holds));
    }

    [Fact]
    public void A_type_of_osdu_records_is_read_by_several_fields_and_a_value_naming_a_record_compares_as_its_id()
    {
        var renderer = Renderer("""
            Unit:
              $cache: UnitOfMeasure.id
              $findBy: Code = unit and Name = well
            """);

        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", renderer.Render(Record("metre", null, "m")).Document["data"]!["Unit"]!.GetValue<string>());
        var missed = renderer.Render(Record("foot", null, "m"));
        Assert.True(missed.IsHeld);
        var noted = Assert.Single(missed.CacheUsages, u => u.Kind == CacheUsageKind.Unlisted);
        Assert.Equal(("Code and Name", "[\"m\",\"foot\"]"), (noted.Path, noted.Value));

        var byId = Renderer("""
            Symbol:
              $cache: UnitOfMeasure.Name
              $findBy: id = unit and Code = well
            """);
        Assert.Equal("foot", Written(byId.Render(Record("ft", null, "dev:reference-data--UnitOfMeasure:ft:"))));
    }

    [Fact]
    public void A_named_lookup_finds_its_record_by_a_line_joined_by_and()
    {
        var mapping = new DeliveryDocumentLoader().ParseMapping(
            TestSchema.MappingDocument("Symbol: { $lookup: curve.curve_type, $required: false }").Replace("parameters:", """
                lookups:
                  curve:
                    $cache: CurveMap
                    $findBy:
                      - well = well and mnemonic = mnemonic
                parameters:
                """, StringComparison.Ordinal),
            "thing.yaml");
        var renderer = new MappingRenderer(mapping, TestSchema.Build(), References(), TestSchema.Context());

        Assert.Equal(["dataset.well", "dataset.mnemonic"], Assert.Single(mapping.Lookups.Values).Columns.Select(c => c.ToString()));
        Assert.Equal("Sonic", Written(renderer.Render(Record("A-12", "DT"))));
        Assert.Null(Written(renderer.Render(Record("B-1", "DT"))));
    }

    [Fact]
    public void The_preflight_needs_one_line_whose_every_field_the_cache_holds()
    {
        var references = References();
        var some = Preflight.Check(TestSchema.Mapping("""
            Symbol:
              $cache: CurveMap.curve_type
              $findBy: [well = well and depth = depth, mnemonic = mnemonic]
            """), TestSchema.Build(), references, TestSchema.Context(), sourceColumns: null);
        Assert.DoesNotContain(some, i => i.Severity == IssueSeverity.Error);
        Assert.Contains(some, i => i.Severity == IssueSeverity.Warning
            && i.Message.Contains("finds CurveMap by well and depth, and cache version 'refs-1' does not cache 'depth', so that line finds no record", StringComparison.Ordinal));

        var none = Preflight.Check(TestSchema.Mapping("""
            Symbol:
              $cache: CurveMap.curve_type
              $findBy: well = well and depth = depth
            """), TestSchema.Build(), references, TestSchema.Context(), sourceColumns: null);
        Assert.Contains(none, i => i.Severity == IssueSeverity.Error
            && i.Message.Contains("finds CurveMap by well and depth, and cache version 'refs-1' caches every field of none of those lines", StringComparison.Ordinal));
    }

    [Fact]
    public void The_outline_and_the_render_text_say_a_line_s_comparisons_go_together()
    {
        var outline = MappingOutline.Of(TestSchema.Mapping(CurveType));
        var symbol = Walk(outline.Record).Single(node => node.Path == "data.Symbol");
        var find = Assert.Single(symbol.Flow!.Steps, step => step.Kind == "find");
        Assert.Contains("A line of several comparisons finds the record that meets them all", find.Detail, StringComparison.Ordinal);
        var lookup = Assert.Single(find.Lookups);
        Assert.Equal([("well", "dataset.well", false), ("mnemonic", "dataset.mnemonic", true), ("mnemonic", "dataset.mnemonic", false)],
            lookup.Matches.Select(m => (m.Field, m.Value, m.And)));

        var origin = EntryValues.Origin(Symbol(TestSchema.Mapping(CurveType)));
        Assert.Equal("cache.CurveMap.curve_type by (well = dataset.well and mnemonic = dataset.mnemonic) or mnemonic = dataset.mnemonic", origin);
    }

    private static IEnumerable<MappingOutlineNode> Walk(IEnumerable<MappingOutlineNode> nodes)
        => nodes.SelectMany(node => Walk(node.Children).Prepend(node));

    private static void Nothing(ReferenceMatch match)
    {
        Assert.Null(match.Item);
        Assert.Empty(match.CaseVariants);
        Assert.Equal(ReferenceMatchKind.None, match.Kind);
    }

    [Fact]
    public void The_builder_reads_a_line_joined_by_and_and_writes_it_back_as_it_was()
    {
        var mapping = TestSchema.Mapping(CurveType);
        var draft = MappingBuilder.FromDefinition(mapping);
        var entry = Assert.Single(draft.Entries, e => e.FindBy.Count > 0);
        Assert.Equal(new MappingDraftFind("mnemonic", "mnemonic", null), Assert.Single(entry.FindBy[0].And!) with { And = null });
        Assert.Null(entry.FindBy[1].And);

        var yaml = MappingBuilder.ToYaml(draft);
        Assert.Contains("- well = well and mnemonic = mnemonic", yaml, StringComparison.Ordinal);
        var again = Symbol(new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml"));
        Assert.Equal(Symbol(mapping).FindBy.Select(f => f.ToString()), again.FindBy.Select(f => f.ToString()));
    }

    [Fact]
    public void The_builder_refuses_a_field_compared_twice_a_nested_comparison_and_and_on_a_search()
    {
        var draft = MappingBuilder.FromDefinition(TestSchema.Mapping(CurveType));
        MappingDraft With(MappingDraftFind line) => draft with
        {
            Entries = draft.Entries.Select(e => e.FindBy.Count > 0 ? e with { FindBy = [line] } : e).ToList(),
        };

        var twice = MappingBuilder.Incomplete(With(new MappingDraftFind("well", "well", null, [new MappingDraftFind("Well", "mnemonic", null)])));
        Assert.Contains(twice, i => i.Message.Contains("findBy compares well twice on one line", StringComparison.Ordinal));

        var nested = MappingBuilder.Incomplete(With(new MappingDraftFind("well", "well", null,
            [new MappingDraftFind("mnemonic", "mnemonic", null, [new MappingDraftFind("curve_type", "unit", null)])])));
        Assert.Contains(nested, i => i.Message.Contains("a comparison holds none of its own", StringComparison.Ordinal));

        var missing = MappingBuilder.Incomplete(With(new MappingDraftFind("well", "well", null, [new MappingDraftFind("mnemonic", null, null)])));
        Assert.Contains(missing, i => i.Message.Contains("findBy mnemonic needs the dataset column, or a fixed text, it must equal", StringComparison.Ordinal));

        var searched = MappingBuilder.FromDefinition(SearchSourceTests.Mapping("""
            WellboreID:
              $search: Wellbore
              $findBy: data.FacilityName = name
            """));
        var andOnSearch = searched with
        {
            Entries = searched.Entries.Select(e => e.FindBy.Count > 0
                ? e with { FindBy = [e.FindBy[0] with { And = [new MappingDraftFind("data.WellboreName", "name", null)] }] }
                : e).ToList(),
        };
        Assert.Contains(MappingBuilder.Incomplete(andOnSearch), i => i.Message.Contains("a search asks the platform for one property at a time", StringComparison.Ordinal));
    }

    [Fact]
    public void A_search_term_does_not_follow_a_column_whose_record_is_found_by_several_values()
    {
        static SearchCacheType? CacheType(string name) => name == "UnitOfMeasure"
            ? new SearchCacheType("UnitOfMeasure", SearchCacheType.OsduOrigin, "osdu:wks:reference-data--UnitOfMeasure:*",
                [new ReferenceFieldSpec("data.Code"), new ReferenceFieldSpec("data.Name")])
            : null;
        var mapping = TestSchema.Mapping("""
            Unit:
              $cache: UnitOfMeasure.id
              $findBy: Code = unit and Name = well
            """);

        var terms = SearchTermCompiler.Compile([new SearchTermSource(mapping, "OsduData.silver.Thing", new Dictionary<string, string>(StringComparer.Ordinal))], CacheType, []);
        foreach (var column in new[] { "unit", "well" })
        {
            var route = Assert.Single(Assert.Single(terms, t => t.Key.Column == column).Routes, r => r.Kind == SearchRouteKind.Lookup);
            Assert.Equal("its UnitOfMeasure record is found by dataset.unit and dataset.well together, which one value cannot stand for", route.Problem);
        }
    }

    [Fact]
    public void A_composite_miss_keeps_its_values_whole_where_the_ledger_can_and_reads_them_back_from_the_key_where_it_cut_them()
    {
        var miss = CacheUsage.CompositeMiss("CurveMap", [("data.well", " A-12 "), ("mnemonic", "gr")], ignoreSeparators: true)!;
        Assert.Equal((" ~[\"A-12\",\"GR\"]", "well and mnemonic", "[\"A-12\",\"gr\"]"), (miss.ItemId, miss.Path, miss.Value));
        Assert.True(CacheUsage.IsComposite(miss.ItemId));
        Assert.False(CacheUsage.IsComposite(CacheUsage.ListingKey(" A-12")));

        var read = CacheUsage.Composite(miss.ItemId, miss.Path, miss.Value)!;
        Assert.Equal([("well", "A-12"), ("mnemonic", "gr")], read.Comparisons);
        Assert.True(read.IgnoreSeparators);

        // A value the ledger cut short is read from the key, folded as the key keeps it.
        var cut = CacheUsage.Composite(miss.ItemId, miss.Path, "[\"A-12\",\"g")!;
        Assert.Equal([("well", "A-12"), ("mnemonic", "GR")], cut.Comparisons);
        Assert.Null(CacheUsage.Composite(miss.ItemId, "well and mnemonic and run", "[]"));
        Assert.Null(CacheUsage.Composite("dev:reference-data--UnitOfMeasure:m", "Code", "m"));

        // Values that would not fit the ledger's key whole are not noted, as a key no table could hold is not.
        Assert.Null(CacheUsage.CompositeMiss("CurveMap", [("well", new string('x', 300)), ("mnemonic", new string('y', 300))], ignoreSeparators: false));
        Assert.Throws<ArgumentException>(() => CacheUsage.CompositeMiss("CurveMap", [("well", "A-12")], ignoreSeparators: false));

        Assert.Equal("well = 'A-12' and mnemonic = 'gr'", CacheUsage.CompositeText(miss.Path, miss.Value));
        Assert.Null(CacheUsage.CompositeText("mnemonic", "GR"));
        Assert.Equal([" [\"A-12\",\"GR\"]", " ~[\"A-12\",\"GR\"]"], CacheUsage.CompositeKeys(miss.Path, miss.Value));
        Assert.Empty(CacheUsage.CompositeKeys("mnemonic", "GR"));
    }

    [Fact]
    public void A_change_listing_values_looked_up_together_says_which_values_the_row_now_meets()
    {
        var tag = new UpdateTag
        {
            Scope = "dev", TypeName = "CurveMap", ItemId = "7", Path = "well and mnemonic", Change = "listed",
            OldValue = "[\"Z-9\",\"NPHI\"]", NewValue = null, ToVersion = "v2", Mode = "auto", SetIds = [1], AffectedRecords = 1,
        };

        Assert.Equal("CurveMap now holds '7', where well = 'Z-9' and mnemonic = 'NPHI', which delivered records found no row meeting together", tag.Describe());
    }
}
