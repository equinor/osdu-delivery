using System.Text.Json.Nodes;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A record of a linked template rendered from its parent (osdu/docs/reference/flow/mapping-parent.md): the render asks for
/// the parent by the id the record's own makes in the parent's namespace, stops until it has been read, takes each section
/// whole as the parent holds it, and holds the record when the parent is not there to read. The parent is the platform's,
/// answered here by a <see cref="FixedRecordSearch"/> the way storage hands it over.
/// </summary>
public class ParentRenderTests
{
    private static readonly JsonObject ParentData = new()
    {
        ["Nested"] = new JsonObject { ["Inner"] = "from the parent" },
        ["Aliases"] = new JsonArray("A-1", "A-2"),
        ["Curves"] = new JsonArray(new JsonObject { ["CurveID"] = "GR", ["TopDepth"] = 10.5 }, new JsonObject { ["CurveID"] = "DT", ["TopDepth"] = 11 }),
        ["Symbol"] = "  wb ",
        ["Weight"] = "12.5",
        ["Description"] = "",
    };

    /// <summary>The platform holding the parents of the records named <paramref name="names"/>, each at version 7.</summary>
    private static FixedRecordSearch Platform(params string[] names)
        => new([], names.ToDictionary(LinkedTemplates.ParentId, name => LinkedTemplates.ParentRecord(name, 7, (JsonObject)ParentData.DeepClone()), StringComparer.Ordinal));

    private static MappingRenderer Renderer(MappingDefinition mapping, IRecordSearch search)
        => new(mapping, LinkedTemplates.Custom(), ReferenceSnapshot.Empty, LinkedTemplates.Context(), searches: null, search);

    private static SourceRecord Row(string? name, string depth = "1")
        => new() { Row = SourceRow.FromStrings(new Dictionary<string, string?> { ["name"] = name, ["depth"] = depth }) };

    /// <summary>Renders the row, answering what it asks between renders, until it finishes; with how many renders it took.</summary>
    private static async Task<(RenderResult Result, int Renders)> Settle(MappingRenderer renderer, IRecordSearch search, SourceRecord row)
    {
        for (var renders = 1; renders <= 10; renders++)
        {
            var result = renderer.Render(row);
            if (!result.IsIncomplete)
            {
                return (result, renders);
            }

            await search.AnswerAsync(result.Unanswered);
        }

        throw new InvalidOperationException("the render never finished");
    }

    [Fact]
    public void A_render_that_meets_the_parent_first_stops_and_names_it_rather_than_guessing()
    {
        var mapping = LinkedTemplates.Mapping("""
            Nested: { $parent: data.Nested }
            Aliases: { $parent: data.Aliases }
            """);

        var result = Renderer(mapping, Platform("x")).Render(Row("x"));

        Assert.True(result.IsIncomplete);
        var question = Assert.IsType<ParentQuestion>(Assert.Single(result.Unanswered));
        Assert.Equal((LinkedTemplates.ParentId("x"), LinkedTemplates.ParentKind, "data.Aliases|data.Nested"), (question.Id, question.Kind, question.Sections));
        Assert.Null(result.Parent);
    }

    [Fact]
    public void The_parents_id_is_the_records_own_in_the_parents_namespace()
    {
        // The minted value is the delivery key, the same whatever the kind: only the entity type differs.
        var mapping = LinkedTemplates.Mapping("Nested: { $parent: data.Nested }");
        var result = Renderer(mapping, Platform()).Render(Row("x"));
        var question = Assert.IsType<ParentQuestion>(Assert.Single(result.Unanswered));

        Assert.Equal(LinkedTemplates.RecordId("x"), result.TargetId);
        Assert.Equal(result.TargetId!.Replace("--CustomThing:", "--Thing:", StringComparison.Ordinal), question.Id);

        // Under idFrom: key the id is made from the key's values, and the parent's is made the same way.
        var byKey = LinkedTemplates.Mapping("Nested: { $parent: data.Nested }", idFrom: "key");
        var keyed = Renderer(byKey, Platform()).Render(Row("A/1 B"));
        var keyedQuestion = Assert.IsType<ParentQuestion>(Assert.Single(keyed.Unanswered));
        Assert.Equal("dev:work-product-component--CustomThing:A%2F1%20B", keyed.TargetId);
        Assert.Equal("dev:work-product-component--Thing:A%2F1%20B", keyedQuestion.Id);
    }

    [Fact]
    public async Task A_parent_found_gives_its_sections_whole_and_the_record_says_which_parent_and_version()
    {
        var mapping = LinkedTemplates.Mapping("""
            Nested: { $parent: data.Nested }
            Aliases: { $parent: data.Aliases }
            Curves: { $parent: data.Curves }
            """);
        var search = Platform("x");

        var (result, renders) = await Settle(Renderer(mapping, search), search, Row("x"));

        Assert.Equal(2, renders);
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        var data = result.Document["data"]!;
        Assert.Equal("from the parent", data["Nested"]!["Inner"]!.GetValue<string>());
        Assert.Equal(["A-1", "A-2"], data["Aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(["GR", "DT"], data["Curves"]!.AsArray().Select(c => c!["CurveID"]!.GetValue<string>()));
        Assert.Equal(new ParentUsage(LinkedTemplates.ParentId("x"), LinkedTemplates.ParentKind, true, 7), result.Parent);

        // The parent is read once, however many sections the record takes of it.
        Assert.Single(search.Read);
    }

    [Fact]
    public async Task A_parent_the_platform_does_not_hold_holds_the_record_whatever_required_says()
    {
        var mapping = LinkedTemplates.Mapping("""
            Nested:
              $parent: data.Nested
              $required: false
            """);
        var search = Platform();

        var (result, _) = await Settle(Renderer(mapping, search), search, Row("x"));

        Assert.True(result.IsHeld);
        Assert.Equal(
            $"osdu.data.Nested: the record's parent {LinkedTemplates.ParentId("x")} ({LinkedTemplates.ParentKind}) is not in OSDU, or the flow's identity may not read it, and the record takes data.Nested from it",
            Assert.Single(result.Holds));
        Assert.Equal(new ParentUsage(LinkedTemplates.ParentId("x"), LinkedTemplates.ParentKind, false, null), result.Parent);
    }

    [Fact]
    public async Task A_section_the_parent_lacks_is_an_empty_value_which_required_decides()
    {
        var required = LinkedTemplates.Mapping("Symbol: { $parent: data.Unit }");
        var optional = LinkedTemplates.Mapping("Symbol: { $parent: data.Unit, $required: false }");
        var blank = LinkedTemplates.Mapping("Description: { $parent: data.Description }");
        var search = Platform("x");

        var (held, _) = await Settle(Renderer(required, search), search, Row("x"));
        Assert.Equal($"osdu.data.Symbol: the record's parent {LinkedTemplates.ParentId("x")} holds no data.Unit, and the entry is required", Assert.Single(held.Holds));

        var (left, _) = await Settle(Renderer(optional, search), search, Row("x"));
        Assert.False(left.IsHeld);
        Assert.Null(left.Document["data"]!["Symbol"]);

        // A section holding empty text is as empty as one not there.
        var (empty, _) = await Settle(Renderer(blank, search), search, Row("x"));
        Assert.Contains("holds no data.Description", Assert.Single(empty.Holds), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_single_value_from_the_parent_passes_through_the_nodes_modifiers_and_takes_its_variables_type()
    {
        var mapping = LinkedTemplates.Mapping("""
            Symbol: { $parent: data.Symbol, $modifiers: [trim, upper] }
            Weight: { $parent: data.Weight }
            Aliases: { $parent: data.Symbol, $modifiers: [trim] }
            """);
        var search = Platform("x");

        var (result, _) = await Settle(Renderer(mapping, search), search, Row("x"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("WB", result.Document["data"]!["Symbol"]!.GetValue<string>());
        // Text the parent holds becomes the number the custom template takes; a single value fills a list as a list of one.
        Assert.Equal(12.5, result.Document["data"]!["Weight"]!.GetValue<double>());
        Assert.Equal(["wb"], result.Document["data"]!["Aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public async Task Modifiers_on_an_object_or_a_list_from_the_parent_hold_the_record()
    {
        var mapping = LinkedTemplates.Mapping("Nested: { $parent: data.Nested, $modifiers: [upper] }");
        var search = Platform("x");

        var (result, _) = await Settle(Renderer(mapping, search), search, Row("x"));

        Assert.Equal("osdu.data.Nested: $modifiers change a single value, and the parent's data.Nested is an object", Assert.Single(result.Holds));
    }

    [Fact]
    public async Task A_coalesce_takes_the_parents_value_and_falls_through_when_the_parent_holds_none()
    {
        var mapping = LinkedTemplates.Mapping("""
            Symbol:
              $coalesce:
                - $parent: data.Unit
                - $parent: data.Symbol
                - $from: name
            """);
        var search = Platform("x");

        var (result, _) = await Settle(Renderer(mapping, search), search, Row("x"));

        Assert.False(result.IsHeld, string.Join("; ", result.Holds));
        Assert.Equal("  wb ", result.Document["data"]!["Symbol"]!.GetValue<string>());
        Assert.Equal((2, "parent.data.Symbol"), (Assert.Single(result.Choices).Alternative, result.Choices[0].Origin));
    }

    [Fact]
    public async Task Records_reading_one_parent_each_get_their_own_copy_of_it()
    {
        var mapping = LinkedTemplates.Mapping("Nested: { $parent: data.Nested }");
        var search = Platform("x");
        var renderer = Renderer(mapping, search);

        var (first, _) = await Settle(renderer, search, Row("x"));
        var (second, _) = await Settle(renderer, search, Row("x"));
        first.Document["data"]!["Nested"]!["Inner"] = "changed after the render";

        Assert.Equal("from the parent", second.Document["data"]!["Nested"]!["Inner"]!.GetValue<string>());
        Assert.True(search.TryRead(Assert.IsType<ParentQuestion>(Assert.Single(search.Read)), out var answer));
        Assert.Equal("from the parent", answer.At("data.Nested.Inner")!.GetValue<string>());
    }

    [Fact]
    public void A_row_without_its_key_asks_for_no_parent_and_is_held_for_its_key()
    {
        var mapping = LinkedTemplates.Mapping("Nested: { $parent: data.Nested }");

        var result = Renderer(mapping, Platform()).Render(Row(null));

        Assert.Empty(result.Unanswered);
        Assert.True(result.IsHeld);
        Assert.StartsWith("dataset key incomplete", result.Holds[0], StringComparison.Ordinal);
        Assert.Null(result.Parent);
    }

    [Fact]
    public async Task A_parent_read_that_never_got_an_answer_says_so_when_the_record_is_held_for_it()
    {
        Assert.Equal(
            $"reading the parent record {LinkedTemplates.ParentId("x")} ({LinkedTemplates.ParentKind}) got no answer from the platform",
            MappingRenderer.Unanswerable(new ParentQuestion(LinkedTemplates.ParentId("x"), LinkedTemplates.ParentKind, "data.Nested")));

        // A render of a mapping's shape reaches no platform: it reads no parent and draws where each value comes from.
        var mapping = LinkedTemplates.Mapping("""
            Nested: { $parent: data.Nested }
            Symbol: { $parent: data.Symbol, $required: false }
            """);
        var shape = MappingRenderer.Shape(mapping, LinkedTemplates.Custom(), new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = LinkedTemplates.Partition });
        Assert.Equal("<object from parent.data.Nested>", shape.Document["data"]!["Nested"]!.GetValue<string>());
        Assert.Equal("<string from parent.data.Symbol, optional>", shape.Document["data"]!["Symbol"]!.GetValue<string>());

        // A render given no platform finds no parent there, and holds the record for it.
        var (held, _) = await Settle(Renderer(mapping, NoRecordSearch.Instance), NoRecordSearch.Instance, Row("x"));
        Assert.Contains(held.Holds, h => h.Contains("is not in OSDU, or the flow's identity may not read it", StringComparison.Ordinal));
    }
}
