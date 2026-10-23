using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Engine.Planning;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A mapping that links its records to their children (osdu/docs/reference/flow/mapping-child.md): the <c>$child</c> node as
/// the loader reads it, and the preflight that refuses, before anything renders, a link that cannot be what it says: a kind
/// whose template is linked to no parent, or to another entity type than the mapping's own, one of the mapping's own entity
/// type, a template the folders do not hold, a property that cannot take a reference of the child's entity type; and the
/// mapping builder, which opens such a mapping, writes it back the same, and says what a child input lacks. The parent is the
/// canonical test template with the open <c>ExtensionProperties</c> OSDU's schemas carry, which the estate cannot change, so
/// the link goes there or in a tag.
/// </summary>
public class ChildMappingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string ChildKind = LinkedTemplates.CustomKind;

    /// <summary>The canonical template, as OSDU's schemas are: with an <c>ExtensionProperties</c> object that declares nothing.</summary>
    internal static SchemaSnapshot Canonical()
    {
        var root = (JsonObject)LinkedTemplates.Parent().Root.DeepClone();
        var data = (JsonObject)root["properties"]!["data"]!["allOf"]![1]!["properties"]!;
        data["ExtensionProperties"] = new JsonObject { ["type"] = "object" };
        return new SchemaSnapshot(LinkedTemplates.ParentKind, root, DateTimeOffset.UnixEpoch);
    }

    /// <summary>A mapping over <see cref="Canonical"/>, its data properties <paramref name="data"/> after the base ones and its record-level ones <paramref name="record"/>.</summary>
    internal static MappingDefinition Mapping(string data, string record = "", string idFrom = "")
    {
        var document = TestSchema.MappingDocument(data, record: record)
            .Replace($"version: {TestSchema.Build().Version}", $"version: {Canonical().Version}", StringComparison.Ordinal);
        if (idFrom.Length > 0)
        {
            document = document.Replace("  key: [name]\n", $"  key: [name]\n  idFrom: {idFrom}\n", StringComparison.Ordinal);
        }

        return new DeliveryDocumentLoader().ParseMapping(document, "thing.yaml");
    }

    private static MappingEntry Entry(MappingDefinition mapping, string target)
        => mapping.Entries.Single(e => string.Equals(e.Target.Text, target, StringComparison.Ordinal));

    [Fact]
    public void A_child_node_names_the_kind_it_links_and_never_holds_its_record()
    {
        var mapping = Mapping(
            $$"""
            ExtensionProperties:
              CustomThingID: { $child: {{ChildKind}} }
            """,
            record: $$"""
            tags:
              CustomThing: { $child: {{ChildKind}}, $description: The custom record beside this one. }
            """);

        var link = Entry(mapping, "osdu.data.ExtensionProperties.CustomThingID");
        Assert.Equal((MappingSourceKind.Child, ChildKind, false), (link.Source!.Kind, link.Source.ChildKind, link.Required));
        Assert.Equal($"child.{ChildKind}", link.Source.ToString());
        Assert.False(link.Source.ReadsRow);
        Assert.False(link.Source.Resolves);
        Assert.False(Entry(mapping, "osdu.tags.CustomThing").Required);

        Assert.True(mapping.LinksChildren);
        Assert.Equal([ChildKind], mapping.ChildKinds());
        Assert.False(mapping.ReadsParent);
        Assert.False(TestSchema.Mapping().LinksChildren);
        Assert.Empty(TestSchema.Mapping().ChildKinds());
    }

    [Fact]
    public void A_child_node_takes_a_condition_a_description_and_an_explicit_required_false()
    {
        var mapping = Mapping($$"""
            Symbol:
              $child: {{ChildKind}}
              $when: not empty(name)
              $required: false
              $description: The custom record of the same log.
            """);

        var link = Entry(mapping, "osdu.data.Symbol");
        Assert.NotNull(link.AppliesWhen);
        Assert.False(link.Required);
        Assert.Equal("The custom record of the same log.", link.Description);
    }

    [Fact]
    public void Several_children_are_linked_as_the_items_of_a_list()
    {
        const string Other = "test:wks:work-product-component--OtherThing:1.0.0";
        var mapping = Mapping($$"""
            Aliases:
              - { $child: {{ChildKind}} }
              - { $child: {{Other}} }
            """);

        var list = Entry(mapping, "osdu.data.Aliases");
        Assert.Equal(2, list.Parts.Count);
        Assert.All(list.Parts, part => Assert.Equal((MappingSourceKind.Child, false), (part.Source!.Kind, part.Required)));
        Assert.Equal([ChildKind, Other], mapping.ChildKinds());
    }

    public static TheoryData<string, string> RefusedNodes => new()
    {
        {
            $$"""
            Curves:
              $forEach: curves
              $item:
                CurveID: { $child: {{ChildKind}} }
            """,
            "$child links one of the record's children, which are found by the record's own id, and a row of curves has none of its own"
        },
        { "Symbol: { $child: CustomThing }", "$child names the kind of the child it links, 'authority:source:group--Entity:major.minor.patch'" },
        { "Symbol: { $child: \"test:wks:work-product-component--CustomThing:1.0\" }", "$child names the kind of the child it links" },
        { "Symbol: { $child: \"osdu:wks:AbstractWPCGroupType:1.2.0\" }", "$child 'osdu:wks:AbstractWPCGroupType:1.2.0' names an abstract schema, which no record is of" },
        { "Symbol: { $child: \"\" }", "$child is empty" },
        { $$"""Symbol: { $child: {{ChildKind}}, $modifiers: [upper] }""", "$modifiers change a value the row gives, and a $child node writes the reference to the record's child as OSDU names it" },
        { $$"""Symbol: { $child: {{ChildKind}}, $required: true }""", "a $child node never holds its record: the link is written once OSDU holds the child, and left out until then" },
        { $$"""Symbol: { $child: {{ChildKind}}, $findBy: Code = name }""", "this node links one of its record's children, which is found by the record's own id" },
        { $$"""Symbol: { $child: {{ChildKind}}, $findAll: Code = name }""", "$findAll reads every row of a cached type that matches, so it belongs to a $cache node" },
        { $$"""Symbol: { $child: {{ChildKind}}, $ignoreSeparators: true }""", "$ignoreSeparators loosens how a value is matched against the cache" },
        { $$"""Symbol: { $child: {{ChildKind}}, $unverified: true }""", "$unverified lets an id the node builds with id or ref go out" },
        { $$"""Symbol: { $child: {{ChildKind}}, $from: name }""", "reads its value with $from and $child; a node reads one of" },
        {
            $$"""
            Symbol:
              $coalesce:
                - $child: {{ChildKind}}
                - $from: name
            """,
            "a $child node links one of the record's children, and gives nothing while OSDU does not hold it, which no other value stands in for"
        },
    };

    [Theory]
    [MemberData(nameof(RefusedNodes))]
    public void A_child_node_written_wrong_is_refused_with_why(string data, string said)
    {
        var refused = Assert.Throws<FlowValidationException>(() => Mapping(data));
        Assert.Contains(said, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The preflight of <paramref name="mapping"/> over <see cref="Canonical"/>, the templates of the kinds it links resolved as <paramref name="children"/> give them.</summary>
    private static IReadOnlyList<ValidationIssue> Check(MappingDefinition mapping, params ResolvedChild[] children)
    {
        var schema = Canonical();
        var searches = children.Length == 0 ? ResolvedSearches.None : ResolvedSearches.None.WithChildren(children);
        var context = TestSchema.Context() with { SchemaSnapshotVersion = schema.Version };
        return Preflight.Check(mapping, schema, ReferenceSnapshot.Empty, context, sourceColumns: null, searches);
    }

    /// <summary>The custom template, linked to the canonical one, resolved as the folders hold it.</summary>
    private static ResolvedChild Linked(string kind = ChildKind) => new(kind, LinkedTemplates.Custom(kind), null);

    private static IReadOnlyList<string> Errors(IReadOnlyList<ValidationIssue> issues)
        => issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).ToList();

    [Fact]
    public void A_link_in_extension_properties_a_tag_a_text_property_or_a_list_of_text_passes()
    {
        var mapping = Mapping(
            $$"""
            ExtensionProperties:
              Links:
                CustomThingID: { $child: {{ChildKind}} }
              All:
                - { $child: {{ChildKind}} }
            Symbol: { $child: {{ChildKind}} }
            Aliases:
              - { $child: {{ChildKind}} }
            """,
            record: $$"""
            tags:
              CustomThing: { $child: {{ChildKind}} }
            """);

        Assert.Empty(Errors(Check(mapping, Linked())));

        // OSDU's own open object takes the reference without a warning: it is where a canonical template carries the link.
        Assert.DoesNotContain(Check(mapping, Linked()), issue => issue.Target == "osdu.data.ExtensionProperties.Links.CustomThingID");
    }

    [Fact]
    public void A_kind_whose_template_is_linked_to_no_parent_is_refused()
    {
        var unlinked = (JsonObject)LinkedTemplates.Custom().Root.DeepClone();
        unlinked.Remove(TemplateParent.Key);
        var mapping = Mapping($$"""Symbol: { $child: {{ChildKind}} }""");

        var error = Assert.Single(Errors(Check(mapping, new ResolvedChild(ChildKind, new SchemaSnapshot(ChildKind, unlinked, DateTimeOffset.UnixEpoch), null))));
        Assert.Contains($"links the record's child of {ChildKind}, and that template is linked to no parent, so its records are no record's children", error, StringComparison.Ordinal);
        Assert.Contains("x-osdu-inheriting-from-kind", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kind_whose_template_is_linked_to_another_entity_type_is_refused()
    {
        var elsewhere = LinkedTemplates.Custom(ChildKind, LinkedTemplates.Link("Log", "osdu:wks:work-product-component--WellLog:1.4.0"));
        var mapping = Mapping($$"""Symbol: { $child: {{ChildKind}} }""");

        var error = Assert.Single(Errors(Check(mapping, new ResolvedChild(ChildKind, elsewhere, null))));
        Assert.Contains(
            "and that template is linked to osdu:wks:work-product-component--WellLog:1.4.0, a work-product-component--WellLog record, where this mapping renders work-product-component--Thing records",
            error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_child_of_the_records_own_entity_type_is_refused_since_the_two_would_share_an_id()
    {
        const string SameType = "acme:wks:work-product-component--Thing:1.0.0";
        var mapping = Mapping($$"""Symbol: { $child: {{SameType}} }""");

        var error = Assert.Single(Errors(Check(mapping, Linked(SameType))));
        Assert.Contains("a work-product-component--Thing record as the mapping's own records are, so a record and its child would have the same OSDU id", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_the_folders_do_not_hold_is_refused_with_why()
    {
        var mapping = Mapping($$"""Symbol: { $child: {{ChildKind}} }""");

        var missing = Assert.Single(Errors(Check(mapping, new ResolvedChild(ChildKind, null, "no template folder on the path of thing.yaml holds the kind"))));
        Assert.Contains($"links the record's child of {ChildKind}, and that template cannot be read: no template folder", missing, StringComparison.Ordinal);

        // A check made without the child's template says it could not judge the link, rather than passing it.
        var unloaded = Assert.Single(Errors(Check(mapping)));
        Assert.Contains("that template was not loaded for this check", unloaded, StringComparison.Ordinal);
    }

    [Fact]
    public void A_property_that_cannot_take_a_reference_to_the_child_is_refused()
    {
        var mapping = Mapping($$"""
            WellboreID: { $child: {{ChildKind}} }
            Weight: { $child: {{ChildKind}} }
            When: { $child: {{ChildKind}} }
            Nested: { $child: {{ChildKind}} }
            """);

        var errors = Errors(Check(mapping, Linked()));
        Assert.Contains(errors, e => e.Contains("the link to the record's child of", StringComparison.Ordinal)
            && e.Contains("which osdu.data.WellboreID does not take", StringComparison.Ordinal)
            && e.Contains("work-product-component--CustomThing record, and the template points the variable to master-data--Wellbore", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("a $child node writes the reference to the record's child as text, and the template takes a number at osdu.data.Weight", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("and the template takes a date-time string at osdu.data.When", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains($"child.{ChildKind} is the reference to one of the record's children, one value, and osdu.data.Nested is", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_search_resolution_reads_each_linked_kinds_template_from_the_folders_the_mapping_reads()
    {
        var canonical = Canonical();
        var mapping = Mapping($$"""Symbol: { $child: {{ChildKind}} }""");

        var held = await RenderResolver.SearchesAsync(new MemoryTemplates(canonical, LinkedTemplates.Custom()), mapping, canonical);
        var child = Assert.Single(held.Children).Value;
        Assert.Equal((ChildKind, ChildKind), (child.Kind, child.Schema!.Kind));
        Assert.Null(child.Problem);
        Assert.Null(held.Parent);

        var unheld = await RenderResolver.SearchesAsync(new MemoryTemplates(canonical), mapping, canonical);
        Assert.Null(unheld.Children[ChildKind].Schema);
        Assert.NotNull(unheld.Children[ChildKind].Problem);

        // A mapping linking no child resolves none.
        Assert.Empty((await RenderResolver.SearchesAsync(new MemoryTemplates(canonical), Mapping(string.Empty), canonical)).Children);
    }

    [Fact]
    public void A_mapping_linking_its_children_takes_one_more_round_of_questions()
    {
        Assert.Equal(Planner.SearchRounds(TestSchema.Mapping()) + 1, Planner.SearchRounds(Mapping($$"""Symbol: { $child: {{ChildKind}} }""")));
    }

    [Fact]
    public void The_builder_opens_a_mapping_linking_its_children_and_writes_it_back_as_the_same_mapping()
    {
        var original = Mapping(
            $$"""
            ExtensionProperties:
              CustomThingID: { $child: {{ChildKind}} }
            Symbol: { $child: {{ChildKind}}, $when: not empty(name), $description: The custom record. }
            Aliases:
              - { $from: name }
              - { $child: {{ChildKind}} }
            """,
            record: $$"""
            tags:
              CustomThing: { $child: {{ChildKind}} }
            """);

        var draft = MappingBuilder.FromDefinition(original);
        Assert.Empty(MappingBuilder.Incomplete(draft));
        var link = Assert.Single(draft.Entries, e => e.Target == "osdu.data.ExtensionProperties.CustomThingID");
        Assert.Equal((MappingDraftInput.Child, ChildKind, false, (string?)null), (link.Input, link.ChildKind, link.Required, link.CacheType));
        var list = Assert.Single(draft.Entries, e => e.Target == "osdu.data.Aliases");
        Assert.Equal((MappingDraftInput.Child, ChildKind), (list.Items[1].Input, list.Items[1].ChildKind));

        // A child link is never required, so it is written without $required; a kind holds colons, so it is quoted.
        var yaml = MappingBuilder.ToYaml(draft).ReplaceLineEndings("\n");
        Assert.Contains($"CustomThingID: {{ $child: \"{ChildKind}\" }}", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("$required", yaml, StringComparison.Ordinal);
        var again = MappingBuilder.FromDefinition(new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml"));
        Assert.Equal(JsonSerializer.Serialize(draft, Json), JsonSerializer.Serialize(again, Json));
    }

    [Fact]
    public void The_builder_names_what_a_child_input_lacks_where_it_is()
    {
        var draft = MappingBuilder.FromDefinition(Mapping($$"""Symbol: { $child: {{ChildKind}} }""")) with
        {
            Entries =
            [
                new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Child, Required = false },
                new MappingDraftEntry { Target = "osdu.data.Description", Input = MappingDraftInput.Child, ChildKind = "CustomThing", Required = false },
                new MappingDraftEntry { Target = "osdu.data.Name", Input = MappingDraftInput.Child, ChildKind = ChildKind, Required = true },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Aliases", Input = MappingDraftInput.Child, ChildKind = ChildKind, Required = false,
                    Modifiers = [new MappingDraftModifier("upper")],
                },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Unit", Input = MappingDraftInput.Coalesce,
                    Alternatives =
                    [
                        new MappingDraftEntry { Target = "osdu.data.Unit", Input = MappingDraftInput.Child, ChildKind = ChildKind, Required = false },
                        new MappingDraftEntry { Target = "osdu.data.Unit", Input = MappingDraftInput.Dataset, Column = "name" },
                    ],
                },
                new MappingDraftEntry { Target = "osdu.data.Curves", Input = MappingDraftInput.Repeat, Child = "curves" },
                new MappingDraftEntry { Target = "osdu.data.Curves[].CurveID", Input = MappingDraftInput.Child, ChildKind = ChildKind, Required = false },
            ],
        };

        var issues = MappingBuilder.Incomplete(draft);
        Assert.Contains(issues, i => i.Target == "osdu.data.Symbol" && i.Message.Contains("name the kind of the child to link", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Description" && i.Message.Contains("'CustomThing' is not the kind of a record", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Name" && i.Message.Contains("a child link never holds its record", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Aliases" && i.Message.Contains("a child input takes no findBy lines, find all, modifiers", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Unit" && i.Message.Contains("a child link gives nothing while OSDU does not hold the child", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Curves[].CurveID" && i.Message.Contains("a row of curves has no children of its own", StringComparison.Ordinal));
    }
}
