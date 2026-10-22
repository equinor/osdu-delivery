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
/// A mapping that takes sections of its records' parent (osdu/docs/reference/flow/mapping-parent.md): the <c>$parent</c>
/// node as the loader reads it, and the preflight that refuses, before anything renders, a mapping whose parent cannot be
/// read as it says: a template linked to none, a parent of the record's own entity type, a parent template the folders do
/// not hold, a path the parent does not declare, a section of a shape its variable cannot take; and the mapping builder,
/// which opens such a mapping, writes it back the same, and says what a parent input lacks.
/// </summary>
public class ParentMappingTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static MappingEntry Entry(MappingDefinition mapping, string target)
        => mapping.Entries.Single(e => string.Equals(e.Target.Text, target, StringComparison.Ordinal));

    [Fact]
    public void A_parent_node_reads_a_section_by_its_path_from_the_records_root()
    {
        var mapping = LinkedTemplates.Mapping("""
            Nested: { $parent: data.Nested }
            Aliases:
              $parent: data.Aliases
              $required: false
              $description: The aliases the canonical record carries.
            """);

        var nested = Entry(mapping, "osdu.data.Nested");
        Assert.Equal((MappingSourceKind.Parent, "data.Nested", true), (nested.Source!.Kind, nested.Source.ParentPath, nested.Required));
        Assert.Equal("parent.data.Nested", nested.Source.ToString());
        Assert.False(nested.Source.ReadsRow);
        Assert.False(nested.Source.Resolves);
        Assert.False(Entry(mapping, "osdu.data.Aliases").Required);

        Assert.True(mapping.ReadsParent);
        Assert.Equal(["data.Aliases", "data.Nested"], mapping.ParentPaths());
        Assert.False(TestSchema.Mapping().ReadsParent);
        Assert.Empty(TestSchema.Mapping().ParentPaths());
    }

    [Fact]
    public void A_parent_node_takes_modifiers_a_condition_and_an_assertion_on_the_record()
    {
        var mapping = LinkedTemplates.Mapping("""
            Symbol:
              $parent: data.Symbol
              $modifiers: [trim, upper]
              $when: not empty(name)
              $assert:
                - matches: "^[A-Z]+$"
            """);

        var symbol = Entry(mapping, "osdu.data.Symbol");
        Assert.Equal(2, symbol.Modifiers.Count);
        Assert.NotNull(symbol.AppliesWhen);
        Assert.Equal(AssertionStage.Record, Assert.Single(symbol.Assertions).Stage);
    }

    [Fact]
    public void A_parent_node_is_an_alternative_of_coalesce_like_any_value_node()
    {
        var mapping = LinkedTemplates.Mapping("""
            Symbol:
              $coalesce:
                - $parent: data.Symbol
                - $from: name
            """);

        var symbol = Entry(mapping, "osdu.data.Symbol");
        Assert.Equal(MappingSourceKind.Parent, symbol.Source!.Kind);
        Assert.Equal(MappingSourceKind.DatasetColumn, Assert.Single(symbol.Alternatives).Source!.Kind);
        Assert.Equal(["data.Symbol"], mapping.ParentPaths());
    }

    public static TheoryData<string, string> RefusedNodes => new()
    {
        {
            """
            Curves:
              $forEach: curves
              $item:
                CurveID: { $parent: data.Symbol }
            """,
            "$parent reads the record's parent, which a row of curves has none of; read the whole array from the parent instead"
        },
        { "Aliases: { $parent: \"data.Aliases[]\" }", "$parent 'data.Aliases[]' steps into an array, and a section of the parent is read whole" },
        { "Aliases: { $parent: \"data.Curves[0]\" }", "$parent 'data.Curves[0]' steps into an array, and a section of the parent is read whole" },
        { "Symbol: { $parent: data..Symbol }", "$parent names the path of a section of the record's parent from the record's root" },
        { "Symbol: { $parent: \"data.Sym bol\" }", "$parent names the path of a section of the record's parent from the record's root" },
        { "Symbol: { $parent: \"$data.Symbol\" }", "$parent names the path of a section of the record's parent from the record's root" },
        { "Symbol: { $parent: \"\" }", "$parent is empty" },
        { "Symbol: { $parent: data.Symbol, $findBy: Code = name }", "$findBy selects the record a $cache or a $search node reads; this node reads its record's parent, which is found by the record's own id" },
        { "Symbol: { $parent: data.Symbol, $findAll: Code = name }", "$findAll reads every row of a cached type that matches, so it belongs to a $cache node; this node reads parent.data.Symbol" },
        { "Symbol: { $parent: data.Symbol, $ignoreSeparators: true }", "$ignoreSeparators loosens how a value is matched against the cache, so it only applies to a $cache node" },
        { "Symbol: { $parent: data.Symbol, $unverified: true }", "$unverified lets an id the node builds with id or ref go out" },
        { "Symbol: { $parent: data.Symbol, $from: name }", "reads its value with $from and $parent; a node reads one of" },
    };

    [Theory]
    [MemberData(nameof(RefusedNodes))]
    public void A_parent_node_written_wrong_is_refused_with_why(string data, string said)
    {
        var refused = Assert.Throws<FlowValidationException>(() => LinkedTemplates.Mapping(data));
        Assert.Contains(said, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The preflight of <paramref name="mapping"/> over <paramref name="schema"/>, its parent's template resolved as <paramref name="parent"/> says.</summary>
    private static IReadOnlyList<ValidationIssue> Check(MappingDefinition mapping, SchemaSnapshot schema, ResolvedParent? parent)
    {
        var searches = parent is null ? ResolvedSearches.None : ResolvedSearches.None.WithParent(parent);
        return Preflight.Check(mapping, schema, ReferenceSnapshot.Empty, LinkedTemplates.Context(schema), sourceColumns: null, searches);
    }

    private static ResolvedParent Resolved(SchemaSnapshot custom)
        => new(custom.Parent!, LinkedTemplates.Parent(), null);

    private static IReadOnlyList<string> Errors(IReadOnlyList<ValidationIssue> issues)
        => issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).ToList();

    [Fact]
    public void A_mapping_reading_its_parent_as_the_parents_template_declares_it_passes()
    {
        var custom = LinkedTemplates.Custom();
        var mapping = LinkedTemplates.Mapping("""
            Nested: { $parent: data.Nested }
            Aliases: { $parent: data.Aliases }
            Curves: { $parent: data.Curves }
            Symbol: { $parent: data.Symbol, $modifiers: [upper] }
            """, custom);

        Assert.Empty(Errors(Check(mapping, custom, Resolved(custom))));
    }

    [Fact]
    public void A_mapping_reading_a_parent_its_template_is_not_linked_to_is_refused()
    {
        // The test template itself, linked to nothing, filled by a mapping that reads a parent.
        var plain = LinkedTemplates.Parent();
        var mapping = LinkedTemplates.Mapping("Nested: { $parent: data.Nested }", plain);

        var error = Assert.Single(Errors(Check(mapping, plain, null)));
        Assert.Contains($"the mapping reads its records' parent with $parent, and template {mapping.Template} is linked to no parent", error, StringComparison.Ordinal);
        Assert.Contains("x-osdu-inheriting-from-kind", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_whose_link_cannot_be_read_says_why()
    {
        var custom = LinkedTemplates.Custom(
            LinkedTemplates.Link("Thing", LinkedTemplates.ParentKind),
            LinkedTemplates.Link("Log", "osdu:wks:work-product-component--WellLog:1.4.0"));
        var mapping = LinkedTemplates.Mapping("Nested: { $parent: data.Nested }", custom);

        var error = Assert.Single(Errors(Check(mapping, custom, null)));
        Assert.Contains("names no parent that can be read: x-osdu-inheriting-from-kind names 2 record kinds", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parent_of_the_records_own_entity_type_is_refused_since_the_record_would_be_written_over_it()
    {
        // Another authority, the same entity type: an OSDU id carries no authority, so the record and its parent share one.
        var sameType = LinkedTemplates.Custom("acme:wks:work-product-component--Thing:1.0.0", LinkedTemplates.Link("Thing", LinkedTemplates.ParentKind));
        var mapping = LinkedTemplates.Mapping("Nested: { $parent: data.Nested }", sameType);

        var error = Assert.Single(Errors(Check(mapping, sameType, Resolved(sameType))));
        Assert.Contains("so a record and its parent would have the same OSDU id and the record would be written over its parent", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parent_template_the_folders_do_not_hold_is_refused_with_why()
    {
        var custom = LinkedTemplates.Custom();
        var mapping = LinkedTemplates.Mapping("Nested: { $parent: data.Nested }", custom);

        var missing = Assert.Single(Errors(Check(mapping, custom, new ResolvedParent(custom.Parent!, null, "no template folder on the path of customthing.yaml holds the kind"))));
        Assert.Contains($"the mapping reads its records' parent, of {LinkedTemplates.ParentKind}, and that template cannot be read: no template folder", missing, StringComparison.Ordinal);

        // A check made without the parent's template says it could not judge the paths, rather than passing them.
        var unloaded = Assert.Single(Errors(Check(mapping, custom, null)));
        Assert.Contains("that template was not loaded for this check", unloaded, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_the_parents_template_does_not_declare_is_refused()
    {
        var custom = LinkedTemplates.Custom();
        var mapping = LinkedTemplates.Mapping("""
            Nested: { $parent: data.Nexted }
            Symbol:
              $coalesce:
                - $parent: data.Missing
                - $from: name
            """, custom);

        var errors = Errors(Check(mapping, custom, Resolved(custom)));
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("reads data.Nexted of the record's parent, and template test:wks:work-product-component--Thing:1.0.0", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("reads data.Missing of the record's parent", StringComparison.Ordinal));
    }

    [Fact]
    public void A_path_inside_an_object_the_parents_template_leaves_open_is_read_whatever_it_holds()
    {
        // OSDU's ExtensionProperties declares no properties, and the wellbore DDMS keeps its wdms references in it; an object
        // closed by additionalProperties: false, or one declaring its properties, holds nothing else.
        var root = (JsonObject)LinkedTemplates.Parent().Root.DeepClone();
        var data = (JsonObject)root["properties"]!["data"]!["allOf"]![1]!["properties"]!;
        data["ExtensionProperties"] = new JsonObject { ["type"] = "object" };
        data["Closed"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false };
        var parent = new SchemaSnapshot(LinkedTemplates.ParentKind, root, DateTimeOffset.UnixEpoch);
        var custom = LinkedTemplates.Custom();
        var mapping = LinkedTemplates.Mapping("""
            Nested: { $parent: data.ExtensionProperties.wdms }
            Description: { $parent: data.ExtensionProperties.wdms.bulkURI }
            Symbol:
              $coalesce:
                - $parent: data.Closed.Code
                - $parent: data.Nested.Other
                - $from: name
            """, custom);

        var errors = Errors(Check(mapping, custom, new ResolvedParent(custom.Parent!, parent, null)));
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("reads data.Closed.Code of the record's parent", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("reads data.Nested.Other of the record's parent", StringComparison.Ordinal));
    }

    [Fact]
    public void A_section_of_a_shape_its_variable_cannot_take_is_refused()
    {
        var custom = LinkedTemplates.Custom();
        var mapping = LinkedTemplates.Mapping("""
            Symbol: { $parent: data.Nested }
            Description: { $parent: data.Aliases }
            Nested: { $parent: data.Symbol }
            Curves: { $parent: data.Depth }
            """, custom);

        var errors = Errors(Check(mapping, custom, Resolved(custom)));
        Assert.Contains(errors, e => e.Contains("takes the parent's data.Nested, an object, where template test:wks:work-product-component--CustomThing:1.0.0 takes a single value at osdu.data.Symbol", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("takes the parent's data.Aliases, a list, where", StringComparison.Ordinal) && e.Contains("takes a single value at osdu.data.Description", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("takes the parent's data.Symbol, a single value, where", StringComparison.Ordinal) && e.Contains("takes an object at osdu.data.Nested", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("takes the parent's data.Depth, a single value, where", StringComparison.Ordinal) && e.Contains("takes a list of objects at osdu.data.Curves", StringComparison.Ordinal));
        Assert.Equal(4, errors.Count);
    }

    [Fact]
    public void A_single_value_fills_a_list_of_values_as_a_list_of_one()
    {
        var custom = LinkedTemplates.Custom();
        var mapping = LinkedTemplates.Mapping("Aliases: { $parent: data.Symbol }", custom);

        Assert.Empty(Errors(Check(mapping, custom, Resolved(custom))));
    }

    [Fact]
    public async Task A_search_resolution_reads_the_parents_template_from_the_folders_the_mapping_reads()
    {
        var custom = LinkedTemplates.Custom();
        var mapping = LinkedTemplates.Mapping("Nested: { $parent: data.Nested }", custom);

        var held = await RenderResolver.SearchesAsync(new MemoryTemplates(custom, LinkedTemplates.Parent()), mapping, custom);
        Assert.Equal((LinkedTemplates.ParentKind, LinkedTemplates.Parent().Version), (held.Parent!.Schema!.Kind, held.Parent.Schema.Version));
        Assert.Null(held.Parent.Problem);

        var unheld = await RenderResolver.SearchesAsync(new MemoryTemplates(custom), mapping, custom);
        Assert.Null(unheld.Parent!.Schema);
        Assert.NotNull(unheld.Parent.Problem);
        Assert.Contains("that template cannot be read", Assert.Single(Errors(Preflight.Check(mapping, custom, ReferenceSnapshot.Empty, LinkedTemplates.Context(custom), null, unheld))), StringComparison.Ordinal);

        // A mapping reading no parent resolves none, whatever its template is linked to.
        var reading = LinkedTemplates.Mapping(string.Empty, custom);
        Assert.Null((await RenderResolver.SearchesAsync(new MemoryTemplates(custom), reading, custom)).Parent);
    }

    [Fact]
    public void A_mapping_reading_its_parent_takes_one_more_round_of_questions()
    {
        var plain = TestSchema.Mapping();
        var reading = LinkedTemplates.Mapping("Nested: { $parent: data.Nested }");

        Assert.Equal(Planner.SearchRounds(plain) + 1, Planner.SearchRounds(reading));
    }

    [Fact]
    public void The_builder_opens_a_mapping_reading_its_parent_and_writes_it_back_as_the_same_mapping()
    {
        var loader = new DeliveryDocumentLoader();
        var original = LinkedTemplates.Mapping("""
            Nested: { $parent: data.Nested }
            Curves: { $parent: data.Curves, $required: false }
            Symbol: { $parent: data.Symbol, $modifiers: [upper], $when: not empty(name) }
            Description:
              $coalesce:
                - $from: name
                - $parent: data.Description
            """);

        var draft = MappingBuilder.FromDefinition(original);
        Assert.Empty(MappingBuilder.Incomplete(draft));
        var nested = Assert.Single(draft.Entries, e => e.Target == "osdu.data.Nested");
        Assert.Equal((MappingDraftInput.Parent, "data.Nested", (string?)null), (nested.Input, nested.ParentPath, nested.CacheType));
        var coalesce = Assert.Single(draft.Entries, e => e.Target == "osdu.data.Description");
        Assert.Equal((MappingDraftInput.Parent, "data.Description"), (coalesce.Alternatives[1].Input, coalesce.Alternatives[1].ParentPath));

        var yaml = MappingBuilder.ToYaml(draft).ReplaceLineEndings("\n");
        Assert.Contains("Nested: { $parent: data.Nested }", yaml, StringComparison.Ordinal);
        Assert.Contains("- { $parent: data.Description }", yaml, StringComparison.Ordinal);
        var again = MappingBuilder.FromDefinition(loader.ParseMapping(yaml, "customthing.yaml"));
        Assert.Equal(JsonSerializer.Serialize(draft, Json), JsonSerializer.Serialize(again, Json));
    }

    [Fact]
    public void The_builder_names_what_a_parent_input_lacks_where_it_is()
    {
        var draft = MappingBuilder.FromDefinition(LinkedTemplates.Mapping("Nested: { $parent: data.Nested }")) with
        {
            Entries =
            [
                new MappingDraftEntry { Target = "osdu.data.Nested", Input = MappingDraftInput.Parent },
                new MappingDraftEntry { Target = "osdu.data.Symbol", Input = MappingDraftInput.Parent, ParentPath = "data.Curves[].CurveID" },
                new MappingDraftEntry { Target = "osdu.data.Description", Input = MappingDraftInput.Parent, ParentPath = "data Description" },
                new MappingDraftEntry
                {
                    Target = "osdu.data.Aliases", Input = MappingDraftInput.Parent, ParentPath = "data.Aliases",
                    FindBy = [new MappingDraftFind("Code", "name", null)],
                },
                new MappingDraftEntry { Target = "osdu.data.Curves", Input = MappingDraftInput.Repeat, Child = "curves" },
                new MappingDraftEntry { Target = "osdu.data.Curves[].CurveID", Input = MappingDraftInput.Parent, ParentPath = "data.Name" },
            ],
        };

        var issues = MappingBuilder.Incomplete(draft);
        Assert.Contains(issues, i => i.Target == "osdu.data.Nested" && i.Message.Contains("name the section of the record's parent to take", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Symbol" && i.Message.Contains("'data.Curves[].CurveID' is not a path of the parent", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Description" && i.Message.Contains("'data Description' is not a path of the parent", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Aliases" && i.Message.Contains("found by the record's own id, so a parent input takes no findBy lines", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Target == "osdu.data.Curves[].CurveID" && i.Message.Contains("a row of curves has no parent", StringComparison.Ordinal));
    }
}
