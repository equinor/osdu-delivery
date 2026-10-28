using System.Text.Json.Nodes;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A record inspected entry by entry (<see cref="MappingRenderer.Inspect"/>): with every entry selected it is the render a
/// delivery makes, document and holds alike, and it says what each entry came to on the way: the value it wrote, that it
/// left the variable out and why, that its <c>$when</c> does not hold, the reasons it holds the record, or the search it
/// waits on. A selection evaluates only what reaches the variables it names.
/// </summary>
public sealed class RecordInspectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static MappingRenderer Renderer(string data)
        => new(TestSchema.Mapping(data), TestSchema.Build(), TestSchema.References(), TestSchema.Context());

    /// <summary>A renderer of a mapping declaring the Wellbore search, whose questions <paramref name="search"/> answers.</summary>
    private static MappingRenderer Searching(string data, IRecordSearch search)
    {
        var mapping = SearchSourceTests.Mapping(data);
        return new(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context(), SearchSourceTests.Resolve(mapping), search);
    }

    private static SourceRecord Record(IReadOnlyDictionary<string, string?> row, params (string Dataset, IReadOnlyDictionary<string, string?> Row)[] children)
    {
        var scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in children.GroupBy(c => c.Dataset, StringComparer.OrdinalIgnoreCase))
        {
            scopes[group.Key] = group.Select(c => SourceRow.FromStrings(c.Row)).ToList();
        }

        return new SourceRecord { Row = SourceRow.FromStrings(row), Scopes = scopes };
    }

    private static Dictionary<string, string?> Row(params (string Column, string? Value)[] columns)
    {
        var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "well-1", ["depth"] = "12.5" };
        foreach (var (column, value) in columns)
        {
            row[column] = value;
        }

        return row;
    }

    private static EntryOutcome Only(RecordInspection inspection, string target)
        => Assert.Single(inspection.Outcomes, o => o.Target == target && o.Item is null);

    /// <summary>A mapping touching every way an entry computes its value, for the tests that compare an inspection with a render.</summary>
    private const string EveryKind = """
        Symbol:
          $from: unit
          $required: false
          $modifiers:
            - replace: { NONE: ~, M: m }
        Unit:
          $cache: UnitOfMeasure.id
          $findBy: Code = unit
          $required: false
        When:
          $from: when
          $required: false
          $modifiers:
            - date: dd.MM.yyyy
        Weight:
          $expr: nullif(weight, -999) * 2
          $required: false
        IsRegular:
          $from: coding
          $when: coding != ""
          $modifiers:
            - equals: REGULAR
        Description:
          $coalesce:
            - $from: note
            - $from: remark
            - $value: none given
        Aliases: ["fixed", { $from: alias }]
        Curves:
          $forEach: curves
          $where: curve_id != "SKIP"
          $item:
            CurveID: { $from: curve_id }
            TopDepth:
              $from: top
              $required: false
        """;

    public static TheoryData<string> Rows => new()
    {
        "complete",
        "empty-optional",
        "required-empty",
        "cache-miss",
        "bad-date",
        "not-applicable",
        "replaced-to-nothing",
        "no-curves",
        "skipped-curve",
    };

    private static SourceRecord Case(string name) => name switch
    {
        "complete" => Record(
            Row(("unit", "m"), ("when", "01.09.2026"), ("weight", "3"), ("coding", "REGULAR"), ("note", "n"), ("alias", "a1")),
            ("curves", Row(("curve_id", "GR"), ("top", "10"))), ("curves", Row(("curve_id", "DT"), ("top", "11")))),
        "empty-optional" => Record(Row(("unit", " "), ("weight", "-999"))),
        "required-empty" => Record(Row(("depth", null)), ("curves", Row(("curve_id", null)))),
        "cache-miss" => Record(Row(("unit", "furlong"))),
        "bad-date" => Record(Row(("when", "2026/09/01"))),
        "not-applicable" => Record(Row(("coding", ""))),
        "replaced-to-nothing" => Record(Row(("unit", "NONE"))),
        "no-curves" => Record(Row()),
        _ => Record(Row(), ("curves", Row(("curve_id", "SKIP"))), ("curves", Row(("curve_id", "GR"), ("top", "x")))),
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Every_entry_selected_inspects_to_the_document_and_holds_a_render_writes(string name)
    {
        var renderer = Renderer(EveryKind);
        var record = Case(name);

        var render = renderer.Render(record);
        var inspection = renderer.Inspect(record, EntrySelection.All);

        Assert.Equal(render.Canonical, CanonicalJson.ToString(CanonicalJson.Normalize(inspection.Document)));
        Assert.Equal(render.Holds, inspection.Holds);
        Assert.Equal(render.Key, inspection.Key);
        Assert.Equal(render.SourceKey, inspection.SourceKey);

        // Every hold the render gives is one some outcome gives, apart from a key with an empty part, which is the record's own;
        // what the items of a repeated array hold for is said once with how many items said it, and each item's outcome says it.
        var reasons = inspection.Outcomes.SelectMany(o => o.Reasons).ToHashSet(StringComparer.Ordinal);
        Assert.All(render.Holds.Where(h => !h.StartsWith("dataset key incomplete", StringComparison.Ordinal)), hold => Assert.Contains(Uncounted(hold), reasons));
    }

    /// <summary>A reason the items of a repeated array give, as each item's outcome says it: without how many items said it.</summary>
    private static string Uncounted(string hold)
        => System.Text.RegularExpressions.Regex.Replace(hold, @"^(?<target>[^\s,:]+), in \d+ of \d+ items: ", "${target}: ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    [Fact]
    public void A_reason_several_items_of_a_repeated_array_share_is_said_once_with_how_many_items_say_it()
    {
        var renderer = Renderer("""
            Curves:
              $forEach: curves
              $item:
                CurveID: { $from: curve_id }
                TopDepth:
                  $coalesce:
                    - $from: top
                    - $from: top_md
            """);
        var curve = (string id, string? top) => ("curves", Row(("curve_id", id), ("top", top), ("top_md", null)));
        var three = Record(Row(), curve("GR", null), curve("DT", "10"), curve("RHOB", null), curve("NPHI", null));
        var one = Record(Row(), curve("GR", "10"), curve("DT", null));

        // Three curves of four with no top: one reason, naming how far it reaches, rather than the same reason three times.
        const string Why = "none of the 2 alternatives of $coalesce gives a value, and the entry is required: 1. dataset.curves.top is empty; 2. dataset.curves.top_md is empty";
        var held = renderer.Render(three);
        Assert.Equal([$"osdu.data.Curves[].TopDepth, in 3 of 4 items: {Why}"], held.Holds);
        Assert.Equal([$"osdu.data.Curves[].TopDepth, in 1 of 2 items: {Why}"], renderer.Render(one).Holds);

        // However many of a record's items it reaches, records held for it are one problem.
        Assert.Equal(
            ProblemSignature.Of(string.Join("; ", held.Holds)),
            ProblemSignature.Of(string.Join("; ", renderer.Render(one).Holds)));

        // An inspection says what each item came to, the reason in each item that gave it.
        var tops = renderer.Inspect(three, EntrySelection.All).Outcomes.Where(o => o.Target == "osdu.data.Curves[].TopDepth").ToList();
        Assert.Equal([0, 2, 3], tops.Where(o => o.Kind == EntryOutcomeKind.Held).Select(o => o.Item));
        Assert.All(tops.Where(o => o.Kind == EntryOutcomeKind.Held), o => Assert.Equal([$"osdu.data.Curves[].TopDepth: {Why}"], o.Reasons));

        // A reason naming each item's own value is each item's own.
        var values = renderer.Render(Record(Row(), curve("GR", "x"), curve("DT", "y")));
        Assert.Equal(2, values.Holds.Count);
        Assert.All(values.Holds, h => Assert.StartsWith("osdu.data.Curves[].TopDepth, in 1 of 2 items: ", h, StringComparison.Ordinal));
        Assert.Contains("'x'", values.Holds[0], StringComparison.Ordinal);
        Assert.Contains("'y'", values.Holds[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Each_entry_says_what_it_came_to_and_why()
    {
        var renderer = Renderer(EveryKind);

        var complete = renderer.Inspect(Case("complete"), EntrySelection.All);
        Assert.Equal(EntryOutcomeKind.Value, Only(complete, "osdu.data.Name").Kind);
        Assert.Equal("well-1", Only(complete, "osdu.data.Name").Value!.GetValue<string>());
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", Only(complete, "osdu.data.Unit").Value!.GetValue<string>());
        Assert.Empty(Only(complete, "osdu.data.Unit").Reasons);

        var empty = renderer.Inspect(Case("empty-optional"), EntrySelection.All);
        var weight = Only(empty, "osdu.data.Weight");
        Assert.Equal(EntryOutcomeKind.Empty, weight.Kind);
        Assert.Equal(["nullif(weight, -999) * 2 gives no value"], weight.Reasons);
        Assert.Equal(["dataset.unit is empty"], Only(empty, "osdu.data.Symbol").Reasons);
        Assert.Equal(["dataset.unit is empty, so there is nothing to find in the cache"], Only(empty, "osdu.data.Unit").Reasons);

        // A required entry with nothing to write holds the record, with the render's own reason.
        var required = renderer.Inspect(Case("required-empty"), EntrySelection.All);
        var depth = Only(required, "osdu.data.Depth");
        Assert.Equal(EntryOutcomeKind.Held, depth.Kind);
        Assert.Contains("osdu.data.Depth: dataset.depth is empty, and the entry is required", depth.Reasons);
        Assert.Contains("schema-required property data.Depth rendered empty", depth.Reasons);

        var miss = Only(renderer.Inspect(Case("cache-miss"), EntrySelection.All), "osdu.data.Unit");
        Assert.Equal(EntryOutcomeKind.Empty, miss.Kind);
        Assert.StartsWith("no UnitOfMeasure matches 'furlong' by Code", Assert.Single(miss.Reasons), StringComparison.Ordinal);

        // A value that is there and wrong holds the record whatever the entry's required flag says.
        var date = Only(renderer.Inspect(Case("bad-date"), EntrySelection.All), "osdu.data.When");
        Assert.Equal(EntryOutcomeKind.Held, date.Kind);
        Assert.Equal(["osdu.data.When: '2026/09/01' is not a date/time in the format dd.MM.yyyy"], date.Reasons);

        var regular = Only(renderer.Inspect(Case("not-applicable"), EntrySelection.All), "osdu.data.IsRegular");
        Assert.Equal(EntryOutcomeKind.NotApplicable, regular.Kind);
        Assert.Equal(["$when coding != \"\" does not hold"], regular.Reasons);

        // The column held a value; the replace turned it into none, and the reason says which value that was.
        var symbol = Only(renderer.Inspect(Case("replaced-to-nothing"), EntrySelection.All), "osdu.data.Symbol");
        Assert.Equal(EntryOutcomeKind.Empty, symbol.Kind);
        Assert.Equal(["dataset.unit is 'NONE', which its modifiers (replace(NONE: ~, M: m)) turn into no value"], symbol.Reasons);

        // The literal default of a $coalesce is a value like any other.
        Assert.Equal("none given", Only(renderer.Inspect(Case("no-curves"), EntrySelection.All), "osdu.data.Description").Value!.GetValue<string>());
    }

    [Fact]
    public void The_entries_of_a_repeated_item_speak_per_child_row_and_a_row_without_one_is_not_meant_for_them()
    {
        var renderer = Renderer(EveryKind);

        var complete = renderer.Inspect(Case("complete"), EntrySelection.All);
        var tops = complete.Outcomes.Where(o => o.Target == "osdu.data.Curves[].TopDepth").ToList();
        Assert.Equal([0, 1], tops.Select(o => o.Item));
        Assert.All(tops, o => Assert.Equal(EntryOutcomeKind.Value, o.Kind));
        var curves = Only(complete, "osdu.data.Curves");
        Assert.Equal(EntryOutcomeKind.Value, curves.Kind);
        Assert.Equal(2, ((JsonArray)curves.Value!).Count);

        var none = renderer.Inspect(Case("no-curves"), EntrySelection.All);
        var id = Assert.Single(none.Outcomes, o => o.Target == "osdu.data.Curves[].CurveID");
        Assert.Equal(EntryOutcomeKind.NotApplicable, id.Kind);
        Assert.Null(id.Item);
        Assert.Equal(["the row has no child row in dataset.curves where curve_id != \"SKIP\""], id.Reasons);
        Assert.Equal(EntryOutcomeKind.Held, Only(none, "osdu.data.Curves").Kind);

        // The row the $where turns away makes no item, and its row number is kept for the ones it lets through.
        var skipped = renderer.Inspect(Case("skipped-curve"), EntrySelection.All);
        var top = Assert.Single(skipped.Outcomes, o => o.Target == "osdu.data.Curves[].TopDepth");
        Assert.Equal(1, top.Item);
        Assert.Equal(EntryOutcomeKind.Held, top.Kind);
    }

    [Fact]
    public void A_selection_evaluates_only_what_reaches_the_variables_it_names()
    {
        var search = new FixedRecordSearch([("data.FacilityName", "Wellbore 1/1-A", "dev:master-data--Wellbore:abc")]);
        var renderer = Searching("""
            WellboreID:
              $search: Wellbore
              $findBy: data.FacilityName = wellbore
            Curves:
              $forEach: curves
              $item:
                CurveID: { $from: curve_id }
                TopDepth: { $from: top }
            """, search);
        var record = Record(Row(("wellbore", "Wellbore 1/1-A"), ("depth", null)), ("curves", Row(("curve_id", "GR"), ("top", null))));

        // A check of the name asks the platform nothing: the wellbore is another variable's.
        var name = renderer.Inspect(record, EntrySelection.Of([Path("osdu.data.Name")]));
        Assert.Equal(["osdu.data.Name"], name.Outcomes.Select(o => o.Target));
        Assert.Empty(name.Unanswered);
        Assert.Empty(name.Holds);

        // Depth is required of data and empty; a check of the curves is not held for it, nor for their required array.
        var curve = renderer.Inspect(record, EntrySelection.Of([Path("osdu.data.Curves[].CurveID")]));
        Assert.Equal(["osdu.data.Curves[].CurveID"], curve.Outcomes.Select(o => o.Target));
        Assert.Empty(curve.Holds);
        Assert.Equal("GR", curve.Document["data"]!["Curves"]![0]!["CurveID"]!.GetValue<string>());

        // A check of the array covers every entry of its items, so what the array is held for is its own.
        var array = renderer.Inspect(record, EntrySelection.Of([Path("osdu.data.Curves")]));
        Assert.Equal(["osdu.data.Curves[].CurveID", "osdu.data.Curves[].TopDepth", "osdu.data.Curves"], array.Outcomes.Select(o => o.Target));
        Assert.Equal(EntryOutcomeKind.Held, Assert.Single(array.Outcomes, o => o.Target == "osdu.data.Curves[].TopDepth").Kind);

        // A check of data covers its required Depth, whose absence it now reports.
        var data = renderer.Inspect(record, EntrySelection.Of([Path("osdu.data.Depth")]));
        Assert.Equal(EntryOutcomeKind.Held, Only(data, "osdu.data.Depth").Kind);
    }

    [Fact]
    public async Task An_entry_waiting_on_a_search_says_what_it_waits_for_and_settles_once_the_platform_answers()
    {
        var search = new FixedRecordSearch([("data.FacilityName", "Wellbore 1/1-A", "dev:master-data--Wellbore:abc")]);
        var renderer = Searching("""
            WellboreID:
              $search: Wellbore
              $findBy: data.FacilityName = wellbore
            """, search);
        var record = Record(Row(("wellbore", "Wellbore 1/1-A")));
        var selection = EntrySelection.Of([Path("osdu.data.WellboreID")]);

        var first = renderer.Inspect(record, selection);
        var waiting = Only(first, "osdu.data.WellboreID");
        Assert.Equal(EntryOutcomeKind.Waiting, waiting.Kind);
        Assert.True(first.IsIncomplete);
        Assert.Equal([MappingRenderer.Unanswerable(Assert.Single(first.Unanswered))], waiting.Reasons);

        await search.AnswerAsync(first.Unanswered);
        var second = renderer.Inspect(record, selection);
        Assert.False(second.IsIncomplete);
        Assert.Equal("dev:master-data--Wellbore:abc:", Only(second, "osdu.data.WellboreID").Value!.GetValue<string>());
    }

    [Fact]
    public void A_selection_says_which_variables_it_reaches_and_which_it_covers_whole()
    {
        var selection = EntrySelection.Of([Path("osdu.data.Curves[].CurveID")]);
        Assert.True(selection.Includes("osdu.data.Curves[].CurveID"));
        Assert.True(selection.Includes("osdu.data.Curves"));
        Assert.False(selection.Includes("osdu.data.CurvesExtra"));
        Assert.False(selection.Includes("osdu.data.Curves[].TopDepth"));
        Assert.False(selection.Covers("osdu.data.Curves"));

        var data = EntrySelection.Of([Path("osdu.data")]);
        Assert.True(data.Covers("osdu.data.Curves"));
        Assert.True(data.Covers("osdu.data.Curves[].TopDepth"));
        Assert.False(data.Includes("osdu.tags.Source"));
        Assert.True(EntrySelection.Of([]).IsAll);
    }

    private static TemplatePath Path(string text)
        => TemplatePath.TryParse(text, out var path, out var error) ? path! : throw new ArgumentException(error, nameof(text));
}
