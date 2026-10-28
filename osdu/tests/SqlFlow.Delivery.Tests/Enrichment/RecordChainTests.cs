using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using Xunit;
using static SqlFlow.Delivery.Tests.EnrichmentSuite;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A record of a flow with <c>enrich:</c> steps, rendered as a run renders it (osdu/docs/reference/flow/enrich.md, How a record
/// renders): the flow's mapping, then each step's fragment over the record as it stands, the whole record hashed once. What a
/// step reads is its own rows and the record before it; one row or many; a step with no row adds nothing; a step that holds or
/// conflicts holds the record and stops the chain; and the hash moves with the document alone.
/// </summary>
[Trait(EnrichmentSuite.Trait, EnrichmentSuite.Name)]
public sealed class RecordChainTests
{
    private static readonly MappingDefinition Grade = Fragment("""
        data:
          Symbol: { $from: grade }
        tags:
          Graded: { $from: graded_by, $required: false }
        """, name: "Grade");

    private static RecordChain Chain(params ResolvedStep[] steps) => new(Renderer(Owner()), steps);

    private static JsonObject Data(RenderResult result) => result.Document["data"]!.AsObject();

    [Fact]
    public void A_step_adds_its_rows_paths_to_the_record_the_mapping_rendered_and_the_record_is_hashed_whole()
    {
        var plain = Renderer(Owner()).Render(Record("a", 1));
        var result = Chain(Step("grade", Grade)).Render(Record("a", 1, ("grade", [Row(("grade", "A"), ("graded_by", "qc"))])));

        Assert.False(result.IsHeld);
        Assert.Equal("A", Data(result)["Symbol"]!.GetValue<string>());
        Assert.Equal("qc", result.Document["tags"]!["Graded"]!.GetValue<string>());
        Assert.Equal(plain.Document["id"]!.GetValue<string>(), result.Document["id"]!.GetValue<string>());
        Assert.NotEqual(plain.MetadataHash, result.MetadataHash);
        Assert.Equal(Hashing.ContentHash.Of(result.Canonical), result.MetadataHash);
        var contribution = Assert.Single(result.Enrichment);
        Assert.Equal(("grade", "Grade@1.0.0", (int?)1), (contribution.Step, contribution.Mapping, contribution.Rows));
        Assert.Equal(["data.Symbol", "tags"], contribution.Effects.Select(e => e.Path));
    }

    [Fact]
    public void A_record_without_a_row_of_the_step_is_the_mapping_alone_and_hashes_as_the_mapping_renders_it()
    {
        var plain = Renderer(Owner()).Render(Record("a", 1));
        var result = Chain(Step("grade", Grade)).Render(Record("a", 1, ("grade", [])));

        Assert.Equal(plain.MetadataHash, result.MetadataHash);
        var contribution = Assert.Single(result.Enrichment);
        Assert.Equal((0, 0), (contribution.Rows, contribution.Effects.Count));
    }

    [Fact]
    public void A_step_reading_one_row_holds_a_record_with_several_naming_the_count_and_the_way_on()
    {
        var result = Chain(Step("grade", Grade)).Render(Record("a", 1, ("grade", [Row(("grade", "A")), Row(("grade", "B"))])));

        Assert.True(result.IsHeld);
        var hold = Assert.Single(result.Holds);
        Assert.StartsWith("enrich.grade: the step reads one row per record (rows: one), and its dataset holds 2 for this record", hold, StringComparison.Ordinal);
        Assert.Contains("rows: many and read them with $forEach: grade", hold, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_taking_many_rows_adds_an_item_for_each()
    {
        var aliases = Fragment("""
            data:
              Curves:
                $forEach: aliases
                $item:
                  CurveID: { $from: alias }
            """, name: "Aliases");

        var result = Chain(Step("aliases", aliases, rows: EnrichRows.Many))
            .Render(Record("a", 1, ("aliases", [Row(("alias", "GR")), Row(("alias", "DT"))])));

        Assert.False(result.IsHeld);
        Assert.Equal("""[{"CurveID":"GR"},{"CurveID":"DT"}]""", Data(result)["Curves"]!.ToJsonString());
    }

    [Fact]
    public void A_later_step_reads_what_an_earlier_one_added_and_the_record_holds_both()
    {
        var name = Fragment("""
            data:
              Nested:
                Inner: { $from: inner }
            """, name: "Inner");
        var copy = Fragment("""
            data:
              Description: { $base: data.Nested.Inner, $modifiers: [upper] }
            """, name: "Copy");

        var result = Chain(Step("inner", name), Step("copy", copy, readsRows: false, after: ["inner"]))
            .Render(Record("a", 1, ("inner", [Row(("inner", "first"))])));

        Assert.False(result.IsHeld);
        Assert.Equal("first", Data(result)["Nested"]!["Inner"]!.GetValue<string>());
        Assert.Equal("FIRST", Data(result)["Description"]!.GetValue<string>());
        Assert.Null(Assert.Single(result.Enrichment, c => c.Step == "copy").Rows);
    }

    [Fact]
    public void A_change_to_an_earlier_step_reaches_every_step_that_reads_it_in_the_same_render()
    {
        var name = Fragment("data:\n  Nested:\n    Inner: { $from: inner }", name: "Inner");
        var copy = Fragment("data:\n  Description: { $base: data.Nested.Inner }", name: "Copy");
        var chain = Chain(Step("inner", name), Step("copy", copy, readsRows: false, after: ["inner"]));

        var before = chain.Render(Record("a", 1, ("inner", [Row(("inner", "first"))])));
        var after = chain.Render(Record("a", 1, ("inner", [Row(("inner", "second"))])));

        Assert.Equal("second", Data(after)["Description"]!.GetValue<string>());
        Assert.NotEqual(before.MetadataHash, after.MetadataHash);
    }

    [Fact]
    public void A_change_to_the_mappings_row_reaches_every_step_that_reads_the_record()
    {
        var copy = Fragment("data:\n  Description: { $base: data.Name }", name: "Copy");
        var chain = Chain(Step("copy", copy, readsRows: false));

        Assert.Equal("a", Data(chain.Render(Record("a", 1)))["Description"]!.GetValue<string>());
        Assert.Equal("b", Data(chain.Render(Record("b", 1)))["Description"]!.GetValue<string>());
    }

    [Fact]
    public void Rendering_the_same_rows_again_gives_the_same_document_and_hash()
    {
        var chain = Chain(Step("grade", Grade), Step("copy", Fragment("data:\n  Description: { $base: data.Symbol }", name: "Copy"), readsRows: false));
        var record = Record("a", 1, ("grade", [Row(("grade", "A"))]));

        var first = chain.Render(record);
        var second = chain.Render(record);

        Assert.Equal(first.Canonical, second.Canonical);
        Assert.Equal(first.MetadataHash, second.MetadataHash);
    }

    [Fact]
    public void A_step_that_holds_holds_the_record_with_its_reason_and_the_steps_after_it_do_not_render()
    {
        var required = Fragment("data:\n  Symbol: { $from: grade }", name: "Required");
        var later = Fragment("data:\n  Description: { $value: later }", name: "Later");

        var result = Chain(Step("required", required), Step("later", later, readsRows: false))
            .Render(Record("a", 1, ("required", [Row(("grade", null))])));

        Assert.True(result.IsHeld);
        Assert.StartsWith("enrich.required: ", Assert.Single(result.Holds), StringComparison.Ordinal);
        Assert.True(Assert.Single(result.Enrichment, c => c.Step == "later").NotRendered);
        Assert.Null(Data(result)["Description"]);
    }

    [Fact]
    public void A_step_that_would_change_what_the_mapping_wrote_holds_the_record_naming_the_path()
    {
        var clash = Fragment("data:\n  Name: { $value: other }", name: "Clash");

        var result = Chain(Step("clash", clash, readsRows: false)).Render(Record("a", 1));

        Assert.True(result.IsHeld);
        Assert.StartsWith("enrich.clash: data.Name: the step sets \"other\", and the record already holds \"a\" there", Assert.Single(result.Holds), StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_the_mapping_holds_is_returned_as_it_rendered_without_its_steps()
    {
        var result = Chain(Step("grade", Grade)).Render(Record("", 1, ("grade", [Row(("grade", "A"))])));

        Assert.True(result.IsHeld);
        Assert.Empty(result.Enrichment);
        Assert.DoesNotContain(result.Holds, h => h.StartsWith("enrich.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_base_read_of_a_path_the_record_lacks_holds_when_required_and_leaves_the_value_out_when_not()
    {
        var required = Fragment("data:\n  Description: { $base: data.Nested.Inner }", name: "Need");
        var optional = Fragment("data:\n  Description: { $base: data.Nested.Inner, $required: false }", name: "Maybe");

        var held = Chain(Step("need", required, readsRows: false)).Render(Record("a", 1));
        var sent = Chain(Step("maybe", optional, readsRows: false)).Render(Record("a", 1));

        Assert.Contains("the record holds no data.Nested.Inner when the fragment renders, and the entry is required", Assert.Single(held.Holds), StringComparison.Ordinal);
        Assert.False(sent.IsHeld);
        Assert.Null(Data(sent)["Description"]);
    }

    [Fact]
    public void A_base_read_of_a_list_takes_it_whole_and_modifiers_on_it_hold()
    {
        var owner = Owner("Days: [\"2026-01-01\", \"2026-01-02\"]");
        var whole = Fragment("data:\n  Aliases: { $base: data.Days }", name: "Whole");
        var modified = Fragment("data:\n  Symbol: { $base: data.Days, $modifiers: [upper] }", name: "Modified");

        var copied = new RecordChain(Renderer(owner), [Step("whole", whole, readsRows: false)]).Render(Record("a", 1));
        var held = new RecordChain(Renderer(owner), [Step("modified", modified, readsRows: false)]).Render(Record("a", 1));

        Assert.False(copied.IsHeld);
        Assert.Equal("""["2026-01-01","2026-01-02"]""", Data(copied)["Aliases"]!.ToJsonString());
        Assert.Contains("$modifiers change a single value, and the record's data.Days is a list", Assert.Single(held.Holds), StringComparison.Ordinal);
    }

    [Fact]
    public void The_cache_values_every_step_read_are_the_records_so_a_cache_change_reaches_it()
    {
        var unit = Fragment("""
            data:
              Unit:
                $cache: UnitOfMeasure.id
                $findBy: Code = unit
            """, name: "Unit");

        var result = Chain(Step("unit", unit)).Render(Record("a", 1, ("unit", [Row(("unit", "m"))])));

        Assert.False(result.IsHeld);
        Assert.Equal("dev:reference-data--UnitOfMeasure:m:", Data(result)["Unit"]!.GetValue<string>());
        Assert.Contains(result.CacheUsages, u => u.TypeName == "UnitOfMeasure");
    }

    [Fact]
    public void A_record_of_a_flow_without_steps_renders_exactly_as_the_mapping_renders_it()
    {
        var plain = Renderer(Owner()).Render(Record("a", 1));
        var chained = Chain().Render(Record("a", 1));

        Assert.Equal((plain.Canonical, plain.MetadataHash), (chained.Canonical, chained.MetadataHash));
        Assert.Empty(chained.Enrichment);
    }
}
