using System.Text.Json.Nodes;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Source;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A canonical record rendered with the links to its children (osdu/docs/reference/flow/mapping-child.md): the render asks
/// whether the platform holds the child by the id the record's own makes in the child's namespace, stops until it has been
/// answered, writes the reference once the child is there and leaves the property out while it is not, never holding the
/// record; and the link is part of the document, so a record rendered once its child landed hashes differently and is sent.
/// The platform is answered here by a <see cref="FixedRecordSearch"/> the way storage hands a record over.
/// </summary>
public class ChildRenderTests
{
    private const string ChildKind = LinkedTemplates.CustomKind;

    /// <summary>A child record as storage holds it, at <paramref name="version"/>.</summary>
    private static JsonObject ChildRecord(string name, long version = 4, string kind = ChildKind) => new()
    {
        ["id"] = TargetId.Compose(LinkedTemplates.Partition, TargetId.EntityTypeFromKind(kind), DeliveryKey.Derive("test", [name])),
        ["kind"] = kind,
        ["version"] = version,
    };

    /// <summary>The platform holding the children of the records named <paramref name="names"/>.</summary>
    private static FixedRecordSearch Platform(params string[] names)
        => new([], children: names.ToDictionary(LinkedTemplates.RecordId, name => ChildRecord(name), StringComparer.Ordinal));

    private static MappingRenderer Renderer(MappingDefinition mapping, IRecordSearch search)
    {
        var schema = ChildMappingTests.Canonical();
        return new MappingRenderer(mapping, schema, ReferenceSnapshot.Empty, TestSchema.Context() with { SchemaSnapshotVersion = schema.Version }, searches: null, search);
    }

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

    private static readonly string Linked = $$"""
        ExtensionProperties:
          CustomThingID: { $child: {{ChildKind}} }
        """;

    [Fact]
    public void A_render_that_meets_a_child_link_first_stops_and_names_the_child_rather_than_guessing()
    {
        var result = Renderer(ChildMappingTests.Mapping(Linked), Platform("x")).Render(Row("x"));

        Assert.True(result.IsIncomplete);
        var question = Assert.IsType<ChildQuestion>(Assert.Single(result.Unanswered));
        Assert.Equal((LinkedTemplates.RecordId("x"), ChildKind), (question.Id, question.Kind));
        Assert.Empty(result.Children);
        Assert.Null(result.ChildLinkKey);
    }

    [Fact]
    public async Task A_child_the_platform_holds_is_linked_by_its_id_with_no_version()
    {
        var search = Platform("x");
        var (result, renders) = await Settle(Renderer(ChildMappingTests.Mapping(Linked), search), search, Row("x"));

        Assert.Equal(2, renders);
        Assert.False(result.IsHeld, string.Join(" | ", result.Holds));
        Assert.Equal(LinkedTemplates.RecordId("x") + ":", result.Document["data"]!["ExtensionProperties"]!["CustomThingID"]!.GetValue<string>());

        var usage = Assert.Single(result.Children);
        Assert.Equal((LinkedTemplates.RecordId("x"), ChildKind, true, (long?)4), (usage.Id, usage.Kind, usage.Found, usage.Version));

        // The record and its children share the unique segment of their ids, which is what the ledger finds the record by.
        Assert.Equal(TargetId.Unique(result.TargetId!), result.ChildLinkKey);
        Assert.Equal(TargetId.Unique(LinkedTemplates.RecordId("x")), result.ChildLinkKey);
    }

    [Fact]
    public async Task A_child_the_platform_does_not_hold_leaves_the_property_out_and_never_holds_the_record()
    {
        var search = Platform();
        var (result, _) = await Settle(Renderer(ChildMappingTests.Mapping(Linked), search), search, Row("x"));

        Assert.False(result.IsHeld, string.Join(" | ", result.Holds));
        Assert.Null(result.Document["data"]!["ExtensionProperties"]);
        var usage = Assert.Single(result.Children);
        Assert.Equal((false, (long?)null), (usage.Found, usage.Version));

        // Looked for and missing still names the segment, so the child landing later finds the record.
        Assert.Equal(TargetId.Unique(LinkedTemplates.RecordId("x")), result.ChildLinkKey);
    }

    [Fact]
    public async Task The_link_is_part_of_the_document_so_a_record_rendered_once_its_child_landed_hashes_differently()
    {
        var mapping = ChildMappingTests.Mapping(Linked);
        var before = Platform();
        var (first, _) = await Settle(Renderer(mapping, before), before, Row("x"));
        var (again, _) = await Settle(Renderer(mapping, before), before, Row("x"));
        var after = Platform("x");
        var (linked, _) = await Settle(Renderer(mapping, after), after, Row("x"));

        // Without the child the record renders the same each time, so change detection sends it once; with it, the document
        // changes, and the next run sends the record with the link.
        Assert.Equal(first.MetadataHash, again.MetadataHash);
        Assert.NotEqual(first.MetadataHash, linked.MetadataHash);
        Assert.Contains(LinkedTemplates.RecordId("x") + ":", linked.Canonical, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_in_a_tag_is_written_as_the_tag_value()
    {
        var mapping = ChildMappingTests.Mapping(string.Empty, record: $$"""
            tags:
              CustomThing: { $child: {{ChildKind}} }
            """);
        var search = Platform("x");
        var (result, _) = await Settle(Renderer(mapping, search), search, Row("x"));

        Assert.Equal(LinkedTemplates.RecordId("x") + ":", result.Document["tags"]!["CustomThing"]!.GetValue<string>());
    }

    [Fact]
    public async Task Several_children_are_linked_as_a_list_of_those_the_platform_holds()
    {
        const string Other = "test:wks:work-product-component--OtherThing:1.0.0";
        var mapping = ChildMappingTests.Mapping($$"""
            Aliases:
              - { $child: {{ChildKind}} }
              - { $child: {{Other}} }
            """);
        var search = Platform("x");
        var (result, renders) = await Settle(Renderer(mapping, search), search, Row("x"));

        // Both children are asked in the same round, and only the one the platform holds is linked.
        Assert.Equal(2, renders);
        Assert.Equal([LinkedTemplates.RecordId("x") + ":"], result.Document["data"]!["Aliases"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(2, result.Children.Count);
        Assert.Single(result.Children, c => c.Found);
        Assert.Equal(2, search.Looked.Count);
    }

    [Fact]
    public async Task Under_id_from_key_the_childs_id_is_made_from_the_keys_values_as_its_own_render_makes_its_parents()
    {
        var mapping = ChildMappingTests.Mapping(Linked, idFrom: "key");
        var search = new FixedRecordSearch([]);
        var result = Renderer(mapping, search).Render(Row("x"));

        var question = Assert.IsType<ChildQuestion>(Assert.Single(result.Unanswered));
        Assert.Equal($"{LinkedTemplates.Partition}:work-product-component--CustomThing:x", question.Id);
        Assert.Equal(result.TargetId!.Replace("--Thing:", "--CustomThing:", StringComparison.Ordinal), question.Id);
        await search.AnswerAsync(result.Unanswered);
        Assert.Equal("x", Renderer(mapping, search).Render(Row("x")).ChildLinkKey);
    }

    [Fact]
    public void A_row_without_a_complete_key_asks_for_no_child()
    {
        var result = Renderer(ChildMappingTests.Mapping(Linked), Platform("x")).Render(Row(null));

        Assert.Empty(result.Unanswered);
        Assert.True(result.IsHeld);
        Assert.Empty(result.Children);
    }

    [Fact]
    public void A_link_whose_condition_does_not_hold_asks_nothing()
    {
        var mapping = ChildMappingTests.Mapping($$"""
            Symbol:
              $child: {{ChildKind}}
              $when: depth > 5
            """);

        var result = Renderer(mapping, Platform("x")).Render(Row("x", depth: "1"));

        Assert.False(result.IsIncomplete);
        Assert.Empty(result.Children);
        Assert.Null(result.ChildLinkKey);
    }

    [Fact]
    public async Task Why_a_link_was_left_out_says_the_child_is_not_in_osdu()
    {
        var mapping = ChildMappingTests.Mapping(Linked);
        var search = Platform();
        var renderer = Renderer(mapping, search);
        await Settle(renderer, search, Row("x"));
        var entry = mapping.Entries.Single(e => e.Target.Text == "osdu.data.ExtensionProperties.CustomThingID");

        var why = EntryValues.WhyEmpty(entry, Row("x").Row, null, renderer);
        Assert.Equal(
            $"the record's child {LinkedTemplates.RecordId("x")} ({ChildKind}) is not in OSDU, or the flow's identity may not read it, so the record goes out without the link until it lands",
            why);
    }

    [Fact]
    public void A_render_whose_child_got_no_answer_says_so()
    {
        Assert.Equal(
            $"looking for the child record {LinkedTemplates.RecordId("x")} ({ChildKind}) got no answer from the platform",
            MappingRenderer.Unanswerable(new ChildQuestion(LinkedTemplates.RecordId("x"), ChildKind)));
    }

    [Fact]
    public void A_shape_names_where_the_link_comes_from()
    {
        var shape = MappingRenderer.Shape(
            ChildMappingTests.Mapping(Linked), ChildMappingTests.Canonical(), new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" });

        Assert.Equal($"<value from child.{ChildKind}, optional>", shape.Document["data"]!["ExtensionProperties"]!["CustomThingID"]!.GetValue<string>());
    }
}
