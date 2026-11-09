using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Assertions;
using SqlFlow.Delivery.Engine.Checks;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Search;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// A mapping key holding a dot, a bracket or a space (osdu/docs/reference/flow/mapping-property-names.md): one property name,
/// written where the template takes names it does not declare (a tag, an open object), and refused where it declares its
/// properties by name, with the way to write what was meant. Every reader of the target names it as one step in brackets.
/// </summary>
public class DottedPropertyNameTests
{
    private const string Tags = """
        tags:
          DeliveredBy: osdu-delivery
          lra.securityClassification: { $from: classification }
          "lra.exportControl": { $value: "No" }
          Data Owner:
            $from: owner
            $required: false
          $$lra.marker: kept
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static SourceRecord Row(string classification = "Open", string? owner = "Ops") => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = "W-1", ["depth"] = "1", ["classification"] = classification, ["owner"] = owner,
        }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase),
    };

    private static IReadOnlyList<string> Errors(MappingDefinition mapping, SchemaSnapshot? schema = null)
        => Preflight.Check(mapping, schema ?? TestSchema.Build(), TestSchema.References(), TestSchema.Context(), sourceColumns: null)
            .Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).ToList();

    private static string Refused(string record = "", string data = "")
        => Assert.Throws<FlowValidationException>(() => TestSchema.Mapping(data, record: record)).Message;

    /// <summary>The test template with an open <c>data.ExtensionProperties</c>, and a mapping pinning it.</summary>
    private static SchemaSnapshot Extended() => SchemaSnapshot.Parse(
        TestSchema.Kind,
        TestSchema.Build().Root.ToJsonString().Replace("\"Symbol\":{\"type\":\"string\"}", "\"Symbol\":{\"type\":\"string\"},\"ExtensionProperties\":{\"type\":\"object\"}", StringComparison.Ordinal),
        DateTimeOffset.UnixEpoch);

    private static MappingDefinition ExtendedMapping(string data)
        => new DeliveryDocumentLoader().ParseMapping(
            TestSchema.MappingDocument(data).Replace($"version: {TestSchema.Build().Version}", $"version: {Extended().Version}", StringComparison.Ordinal),
            "thing.yaml");

    [Fact]
    public void A_key_holding_a_dot_is_one_property_named_in_brackets()
    {
        var mapping = TestSchema.Mapping(record: Tags);
        var targets = mapping.Entries.Select(e => e.Target.Text).ToList();
        Assert.Contains("osdu.tags.DeliveredBy", targets);
        Assert.Contains("osdu.tags['lra.securityClassification']", targets);
        Assert.Contains("osdu.tags['lra.exportControl']", targets);
        Assert.Contains("osdu.tags['Data Owner']", targets);
        Assert.Contains("osdu.tags['$lra.marker']", targets);

        var classification = Assert.Single(mapping.Entries, e => e.Target.Leaf == "lra.securityClassification");
        Assert.Equal("record.tags['lra.securityClassification']", classification.Location);
        Assert.Equal(["tags", "lra.securityClassification"], classification.Target.Segments.Select(s => s.Name));
        Assert.Empty(Errors(mapping));
    }

    [Fact]
    public void A_tag_whose_name_holds_a_dot_renders_as_one_key_of_the_record()
    {
        var rendered = new MappingRenderer(TestSchema.Mapping(record: Tags), TestSchema.Build(), TestSchema.References(), TestSchema.Context()).Render(Row());
        Assert.False(rendered.IsHeld, string.Join("; ", rendered.Holds));
        var tags = rendered.Document["tags"]!.AsObject();
        Assert.Equal("Open", tags["lra.securityClassification"]!.GetValue<string>());
        Assert.Equal("No", tags["lra.exportControl"]!.GetValue<string>());
        Assert.Equal("Ops", tags["Data Owner"]!.GetValue<string>());
        Assert.Equal("kept", tags["$lra.marker"]!.GetValue<string>());
        Assert.Equal("osdu-delivery", tags["DeliveredBy"]!.GetValue<string>());
        Assert.False(tags.ContainsKey("lra"));

        // A tag left out on its own leaves the others written.
        var withoutOwner = new MappingRenderer(TestSchema.Mapping(record: Tags), TestSchema.Build(), TestSchema.References(), TestSchema.Context()).Render(Row(owner: null));
        Assert.False(withoutOwner.Document["tags"]!.AsObject().ContainsKey("Data Owner"));
        Assert.Equal("Open", withoutOwner.Document["tags"]!["lra.securityClassification"]!.GetValue<string>());
    }

    [Fact]
    public void The_schema_describes_a_dotted_tag_by_its_whole_name()
    {
        var schema = TestSchema.Build();
        Assert.Equal(SchemaType.String, schema.Resolve("tags['lra.dataID']")!.Type);
        Assert.Null(schema.Resolve("tags.lra.dataID"));
        Assert.NotNull(SchemaRules.Of(schema).At("tags['lra.dataID']"));
        Assert.Null(SchemaRules.Of(schema).At("tags['lra"));
    }

    [Fact]
    public void A_check_of_a_record_names_a_dotted_tag_as_one_step()
    {
        var record = TestSchema.Doc("""
            { "kind": "k", "acl": { "owners": ["o"], "viewers": ["v"] }, "legal": { "legaltags": ["t"], "otherRelevantDataCountries": ["US"] },
              "tags": { "lra.dataID": 6632, "Source": "welldb" }, "data": { "Depth": 1 } }
            """);
        var problem = Assert.Single(RecordValidator.Check(record, SchemaRules.Of(TestSchema.Build())).Problems);
        Assert.Equal(("tags['lra.dataID']", "tags['lra.dataID']", "type"), (problem.At, problem.Path, problem.Rule));

        var value = Assert.Single(TemplateValueRules.Check(JsonValue.Create(6632), Path("osdu.tags['lra.dataID']"), TestSchema.Build()));
        Assert.Equal("osdu.tags['lra.dataID']", value.At);
    }

    [Fact]
    public void A_dotted_name_where_the_template_declares_its_properties_is_refused_with_the_path_it_meant()
    {
        var nested = Errors(TestSchema.Mapping("Nested.Inner: { $from: name }"));
        Assert.Contains(nested, e => e.Contains("osdu.data declares its properties by name, and none is called 'Nested.Inner'", StringComparison.Ordinal)
            && e.Contains("to write osdu.data.Nested.Inner, nest the properties a path names rather than writing the path", StringComparison.Ordinal));

        var items = Errors(TestSchema.Mapping("\"Curves[].CurveID\": { $from: name }"));
        Assert.Contains(items, e => e.Contains("none is called 'Curves[].CurveID'", StringComparison.Ordinal)
            && e.Contains("to write osdu.data.Curves[].CurveID", StringComparison.Ordinal));

        // A name no path of the template matches is refused without a suggestion.
        Assert.Contains(Errors(TestSchema.Mapping("No.Such: { $from: name }")), e => e.Contains("none is called 'No.Such'", StringComparison.Ordinal) && !e.Contains("to write", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tag_name_written_as_nested_objects_is_refused_with_the_one_key_it_meant()
    {
        var errors = Errors(TestSchema.Mapping(record: """
            tags:
              lra:
                securityClassification: { $from: classification }
            """));
        Assert.Contains(errors, e => e.Contains("osdu.tags takes a text under each name it holds, so osdu.tags.lra holds no properties of its own", StringComparison.Ordinal)
            && e.Contains("write 'lra.securityClassification' as one key under tags, which fills osdu.tags['lra.securityClassification']", StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_of_the_record_itself_and_a_key_that_names_nothing_are_refused_as_they_are_read()
    {
        Assert.Contains("'data.Name' is not a property name", Refused(record: "data.Name: { $from: name }"), StringComparison.Ordinal);
        Assert.Contains("'acl owners' is not a property name", Refused(record: "\"acl owners\": [x]"), StringComparison.Ordinal);
        Assert.Contains("a key is empty", Refused(record: "tags: { \"\": x }"), StringComparison.Ordinal);
        Assert.Contains("a key is empty", Refused(record: "tags: { \"  \": x }"), StringComparison.Ordinal);

        // The spaces around a key are not part of its name; those inside it are.
        var spaced = TestSchema.Mapping(record: "tags: { \" Data Owner \": x }");
        Assert.Contains("osdu.tags['Data Owner']", spaced.Entries.Select(e => e.Target.Text));
    }

    [Fact]
    public void An_open_object_takes_a_dotted_name_at_any_depth()
    {
        var mapping = ExtendedMapping("""
            ExtensionProperties:
              WellDB:
                lra.dataID: { $from: name }
                "Data Owner": { $from: owner }
            """);
        Assert.Contains("osdu.data.ExtensionProperties.WellDB['lra.dataID']", mapping.Entries.Select(e => e.Target.Text));
        Assert.Empty(Errors(mapping, Extended()));

        var rendered = new MappingRenderer(mapping, Extended(), TestSchema.References(), TestSchema.Context()).Render(Row());
        Assert.False(rendered.IsHeld, string.Join("; ", rendered.Holds));
        var wellDb = rendered.Document["data"]!["ExtensionProperties"]!["WellDB"]!.AsObject();
        Assert.Equal("W-1", wellDb["lra.dataID"]!.GetValue<string>());
        Assert.Equal("Ops", wellDb["Data Owner"]!.GetValue<string>());
    }

    [Fact]
    public void A_dotted_name_inside_the_items_of_a_list_is_judged_by_what_the_items_declare()
    {
        var mapping = TestSchema.Mapping("""
            Curves:
              $forEach: curves
              $item:
                CurveID: { $from: curve_id }
                lra.dataID: { $from: curve_id }
            """);
        Assert.Contains("osdu.data.Curves[]['lra.dataID']", mapping.Entries.Select(e => e.Target.Text));
        Assert.Contains(Errors(mapping), e => e.Contains("osdu.data.Curves declares its properties by name, and none is called 'lra.dataID'", StringComparison.Ordinal));
    }

    [Fact]
    public void What_a_mapping_covers_and_what_a_check_selects_name_a_dotted_tag_as_one_variable()
    {
        var mapping = TestSchema.Mapping(record: Tags);
        var report = MappingCoverage.Of(mapping, OsduTemplate.From(TestSchema.Build()));
        Assert.Equal(CoverageState.Always, Assert.Single(report.Variables, v => v.Target == "osdu.tags['lra.securityClassification']").State);
        Assert.Equal(CoverageState.Sometimes, Assert.Single(report.Variables, v => v.Target == "osdu.tags['Data Owner']").State);

        var one = ValueChecker.Select(["osdu.tags['lra.securityClassification']"], mapping, TestSchema.Build());
        Assert.True(one.Includes("osdu.tags['lra.securityClassification']"));
        Assert.False(one.Includes("osdu.tags['lra.exportControl']"));
        Assert.False(one.Includes("osdu.tags.DeliveredBy"));

        var all = ValueChecker.Select(["osdu.tags"], mapping, TestSchema.Build());
        Assert.True(all.Includes("osdu.tags['lra.securityClassification']"));
        Assert.True(EntrySelection.Within("osdu.tags['lra.dataID']", "osdu.tags"));
        Assert.False(EntrySelection.Within("osdu.tagsX", "osdu.tags"));

        Assert.Contains("is not a variable a check can name", Assert.Throws<DeliveryException>(() => ValueChecker.Select(["osdu.tags['lra"], mapping, TestSchema.Build())).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_outline_places_a_dotted_tag_at_its_own_line()
    {
        var yaml = TestSchema.MappingDocument(record: Tags);
        var mapping = new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml");
        var outline = MappingOutline.Of(mapping, TestSchema.Build(), yaml);
        var tags = Assert.Single(outline.Record, n => n.Path == "tags");
        var classification = Assert.Single(tags.Children, n => n.Path == "tags['lra.securityClassification']");
        var line = yaml.ReplaceLineEndings("\n").Split('\n').ToList().FindIndex(l => l.TrimStart().StartsWith("lra.securityClassification:", StringComparison.Ordinal)) + 1;
        Assert.Equal(line, classification.Line);
        Assert.Equal("lra.securityClassification", classification.Name);
        Assert.NotNull(Assert.Single(tags.Children, n => n.Path == "tags['Data Owner']").Line);
    }

    [Fact]
    public void The_builder_writes_a_dotted_tag_back_as_one_key()
    {
        var draft = MappingBuilder.FromDefinition(TestSchema.Mapping(record: Tags));
        Assert.Empty(MappingBuilder.Incomplete(draft));
        Assert.Contains(draft.Entries, e => e.Target == "osdu.tags['lra.securityClassification']");

        var yaml = MappingBuilder.ToYaml(draft).ReplaceLineEndings("\n");
        Assert.Contains("    lra.securityClassification:", yaml, StringComparison.Ordinal);
        Assert.Contains("    Data Owner:", yaml, StringComparison.Ordinal);
        var again = MappingBuilder.FromDefinition(new DeliveryDocumentLoader().ParseMapping(yaml, "thing.yaml"));
        Assert.Equal(JsonSerializer.Serialize(draft, Json), JsonSerializer.Serialize(again, Json));

        // One variable however its target is written: the same tag twice is refused.
        var twice = draft with { Entries = [.. draft.Entries, draft.Entries.Single(e => e.Target == "osdu.tags['lra.securityClassification']") with { Target = "osdu.tags[\"lra.securityClassification\"]" }] };
        Assert.Contains(MappingBuilder.Incomplete(twice), i => i.Message.Contains("has more than one entry", StringComparison.Ordinal));
    }

    [Fact]
    public void An_assertion_on_a_dotted_tag_names_it_as_one_step()
    {
        var mapping = TestSchema.Mapping(record: """
            tags:
              lra.securityClassification:
                $from: classification
                $assert:
                  - equals: Open
            """);
        var rendered = new MappingRenderer(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context()).Render(Row(classification: "Secret"));
        var failure = Assert.Single(rendered.Assertions!.Failures);
        Assert.Equal(("tags['lra.securityClassification']", "tags['lra.securityClassification']", "hold"), (failure.At, failure.Path, failure.OnFail));

        Assert.Empty(new MappingRenderer(mapping, TestSchema.Build(), TestSchema.References(), TestSchema.Context()).Render(Row()).Assertions!.Failures);
    }

    [Fact]
    public void An_assertion_flow_reads_a_dotted_tag_in_brackets_and_searches_it_by_its_dotted_name()
    {
        Assert.Equal("tags['lra.securityClassification']", ConditionReader.RecordPath("tags['lra.securityClassification']", "field", "a.yaml"));
        Assert.Equal("tags[\"Data Owner\"]", ConditionReader.RecordPath(" tags[\"Data Owner\"] ", "field", "a.yaml"));
        Assert.Equal("data.Curves[*].CurveID", ConditionReader.RecordPath("data.Curves[*].CurveID", "field", "a.yaml"));
        Assert.Contains("is not a path into a record", Assert.Throws<FlowValidationException>(() => ConditionReader.RecordPath("tags[lra.x]", "field", "a.yaml")).Message, StringComparison.Ordinal);
        Assert.Contains("is not a path into a record", Assert.Throws<FlowValidationException>(() => ConditionReader.RecordPath("tags['lra", "field", "a.yaml")).Message, StringComparison.Ordinal);
        Assert.Contains("empty property name", Assert.Throws<FlowValidationException>(() => ConditionReader.RecordPath("tags['']", "field", "a.yaml")).Message, StringComparison.Ordinal);
        Assert.Contains("starts at 'other'", Assert.Throws<FlowValidationException>(() => ConditionReader.RecordPath("['other'].x", "field", "a.yaml")).Message, StringComparison.Ordinal);

        var template = OsduTemplate.From(TestSchema.Build());
        var problems = new List<string>();
        var tag = AssertionTemplates.Resolve(template, "tags['lra.securityClassification']", "field", "the test", problems);
        Assert.Empty(problems);
        Assert.Equal(("string", TemplateVariableShape.Value), (tag!.Type, tag.Shape));
        Assert.NotNull(AssertionTemplates.Resolve(template, "tags['Source']", "field", "the test", problems));
        Assert.Empty(problems);
        Assert.Null(AssertionTemplates.Resolve(template, "data['No.Such']", "field", "the test", problems));
        Assert.Contains(problems, p => p.Contains("is not a property of the test", StringComparison.Ordinal));

        var keyword = SearchFields.RecordProperty("tags['lra.securityClassification']");
        Assert.Equal(("tags.lra.securityClassification", OsduFieldIndex.Keyword, (string?)null), (keyword.Field!.Path, keyword.Field.Index, keyword.Problem));
        Assert.Equal("tags.lra.securityClassification", SearchFields.RecordProperty("tags.['lra.securityClassification']").Field!.Path);
        Assert.Equal("tags.Source", SearchFields.RecordProperty("tags.Source").Field!.Path);
        Assert.Contains("no query can name this one", SearchFields.RecordProperty("tags['Data Owner']").Problem, StringComparison.Ordinal);
        Assert.Equal("tags.lra.securityClassification", GroupEvaluator.SearchField("tags['lra.securityClassification']"));
        Assert.Equal("data.Curves.CurveID", GroupEvaluator.SearchField("data.Curves[*].CurveID"));
    }

    private static TemplatePath Path(string text)
    {
        Assert.True(TemplatePath.TryParse(text, out var path, out var error), error);
        return path!;
    }
}
