using System.Text.Json.Nodes;
using SqlFlow.Core;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Json;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;
using SqlFlow.Delivery.Validation;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// An object the schema leaves open (osdu/docs/reference/flow/mapping-values.md, Open objects):
/// <c>data.ExtensionProperties</c> declares no properties, names no type for its keys and refuses none, so a mapping
/// lays out what it writes inside it as it lays out the record: values, literals, objects and a repeater's items, at
/// any depth, written as they arrive. A flow that carries the same key over from the stored record into every update
/// would undo it, and is refused.
/// </summary>
public sealed class OpenObjectTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Kind = "test:wks:work-product-component--Logged:1.0.0";

    private static SchemaSnapshot Schema() => SchemaSnapshot.Parse(Kind, """
        {
          "$id": "https://example.org/Logged.1.0.0.json",
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "kind": { "type": "string" },
            "acl": { "type": "object", "properties": { "owners": { "type": "array", "items": { "type": "string" } }, "viewers": { "type": "array", "items": { "type": "string" } } }, "required": ["owners", "viewers"] },
            "legal": { "type": "object", "properties": { "legaltags": { "type": "array", "items": { "type": "string" } }, "otherRelevantDataCountries": { "type": "array", "items": { "type": "string" } } }, "required": ["legaltags", "otherRelevantDataCountries"] },
            "data": {
              "allOf": [
                {
                  "type": "object",
                  "properties": {
                    "Name": { "type": "string" },
                    "Curves": { "type": "array", "items": { "type": "object", "properties": { "CurveID": { "type": "string" }, "Detail": { "type": "object" } } } },
                    "Sealed": { "type": "object", "additionalProperties": false },
                    "Choice": { "type": "object", "oneOf": [ { "properties": { "A": { "type": "string" } } }, { "properties": { "B": { "type": "string" } } } ] }
                  }
                },
                { "type": "object", "properties": { "ExtensionProperties": { "type": "object" } } }
              ]
            }
          },
          "required": ["kind", "acl", "legal"]
        }
        """, T0);

    private static ReferenceSnapshot Cache() => new("refs-1", T0, []);

    private static RenderContext Context() => new()
    {
        MappingReference = "Logged@1.0.0",
        CacheScope = "dev",
        CacheVersion = "refs-1",
        SchemaSnapshotVersion = Schema().Version,
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { [RenderContext.DataPartitionParameter] = "dev" },
    };

    /// <summary>
    /// The well database's own unit spellings beside the curves, as the WellDB well log mappings keep them: additional
    /// information, so a curve without a unit, or a log without curves, holds nothing.
    /// </summary>
    private const string Originals = """
        Curves:
          $forEach: curves
          $required: false
          $item:
            CurveID: { $from: curve_id }
        ExtensionProperties:
          WellDB:
            Source: WELLDB
            Project: { $from: project }
            Curves:
              $forEach: curves
              $required: false
              $item:
                CurveID: { $from: curve_id }
                OriginalUnit:
                  $from: curve_unit
                  $required: false
        """;

    private static MappingDefinition Mapping(string data) => new DeliveryDocumentLoader().ParseMapping($"""
        documentType: mapping
        name: Logged
        version: 1.0.0
        template:
          kind: {Kind}
          version: {Schema().Version}
        dataset:
          system: test
          key: [name]
        parameters:
          dataPartition: {"{"} required: true {"}"}

        record:
        {TestSchema.Indented(TestSchema.Envelope, 2)}  data:
        {TestSchema.Indented("Name: { $from: name }", 4)}{TestSchema.Indented(data, 4)}

        """, "logged.yaml");

    private static SourceRecord Log(params (string Id, string? Unit)[] curves) => new()
    {
        Row = SourceRow.FromStrings(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["name"] = "log-1", ["project"] = "PROJECT_A" }),
        Scopes = new Dictionary<string, IReadOnlyList<SourceRow>>(StringComparer.OrdinalIgnoreCase)
        {
            ["curves"] = curves.Select(c => SourceRow.FromStrings(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["curve_id"] = c.Id, ["curve_unit"] = c.Unit })).ToList(),
        },
    };

    private static TemplatePath Path(string text)
    {
        Assert.True(TemplatePath.TryParse(text, out var path, out var error), error);
        return path!;
    }

    private static FlowDefinition Flow(IReadOnlyList<string> preserved, string? interfaceName = null)
        => Samples.Targeting(
            new FlowTarget
            {
                Endpoint = "https://osdu.example.com",
                Protocol = DeliveryProtocol.Ddms,
                ProtocolOptions = new ProtocolOptions { DdmsRoot = "/api/os-wellbore-ddms", PreserveDataKeys = preserved },
            },
            interfaceName);

    private static IReadOnlyList<ValidationIssue> Errors(MappingDefinition mapping)
        => Preflight.Check(mapping, Schema(), Cache(), Context(), sourceColumns: null).Where(i => i.Severity == IssueSeverity.Error).ToList();

    [Fact]
    public void An_object_the_schema_leaves_open_takes_any_path_inside_it()
    {
        var template = OsduTemplate.From(Schema());
        var extension = template.Find(Path("osdu.data.ExtensionProperties"))!;
        Assert.True(extension.Open);
        Assert.Equal(TemplateVariableShape.Whole, extension.Shape);
        Assert.Null(extension.Inside);

        foreach (var text in new[] { "osdu.data.ExtensionProperties.WellDB", "osdu.data.ExtensionProperties.WellDB.Curves", "osdu.data.ExtensionProperties.WellDB.Curves[].OriginalUnit" })
        {
            var inside = template.Find(Path(text))!;
            Assert.Equal(text, inside.Path.Text);
            Assert.Equal("osdu.data.ExtensionProperties", inside.Inside!.Text);
            Assert.Equal("any", inside.Type);
            Assert.Equal(TemplateVariableRole.Mapping, inside.Role);
            Assert.False(inside.Open);
        }

        // An open object inside the items of a list is reached with the list's own step into its items.
        var detail = template.Find(Path("osdu.data.Curves[].Detail.Note"))!;
        Assert.Equal("osdu.data.Curves[].Detail", detail.Inside!.Text);
    }

    [Fact]
    public void An_object_that_refuses_keys_or_offers_forms_is_not_open_and_a_property_the_schema_does_not_declare_stays_unknown()
    {
        var template = OsduTemplate.From(Schema());
        Assert.False(template.Find(Path("osdu.data.Sealed"))!.Open);
        Assert.Null(template.Find(Path("osdu.data.Sealed.Note")));
        Assert.False(template.Find(Path("osdu.data.Choice"))!.Open);
        Assert.Null(template.Find(Path("osdu.data.Choice.C")));

        // A property of a declared object, or of a list's items, is no free content; nor is the open object stepped into as a list.
        Assert.Null(template.Find(Path("osdu.data.Name.Note")));
        Assert.Null(template.Find(Path("osdu.data.Curves[].Note")));
        Assert.Null(template.Find(Path("osdu.data.ExtensionProperties[].Note")));
    }

    [Fact]
    public void What_a_mapping_lays_out_inside_an_open_object_renders_as_written_with_each_value_as_it_arrives()
    {
        var mapping = Mapping(Originals);
        Assert.Empty(Errors(mapping));

        var result = new MappingRenderer(mapping, Schema(), Cache(), Context()).Render(Log(("MD", "M"), ("GR", "GAPI"), ("DT", "0100"), ("CALI", null)));
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));

        Assert.Equal(
            CanonicalJson.Canonicalize("""
                {
                  "WellDB": {
                    "Source": "WELLDB",
                    "Project": "PROJECT_A",
                    "Curves": [
                      { "CurveID": "MD", "OriginalUnit": "M" },
                      { "CurveID": "GR", "OriginalUnit": "GAPI" },
                      { "CurveID": "DT", "OriginalUnit": "0100" },
                      { "CurveID": "CALI" }
                    ]
                  }
                }
                """),
            CanonicalJson.ToString(result.Document["data"]!["ExtensionProperties"]!));

        // The declared curves beside it are rendered as before.
        Assert.Equal(4, result.Document["data"]!["Curves"]!.AsArray().Count);
    }

    [Fact]
    public void A_log_without_curves_writes_their_list_empty_and_keeps_the_rest_of_the_open_object()
    {
        var result = new MappingRenderer(Mapping(Originals), Schema(), Cache(), Context()).Render(Log());
        Assert.False(result.IsHeld, string.Join("; ", result.Holds));

        // A list the mapping defines and no row fills is written empty where the object holding it is, as anywhere.
        Assert.Equal(
            CanonicalJson.Canonicalize("""{ "WellDB": { "Source": "WELLDB", "Project": "PROJECT_A", "Curves": [] } }"""),
            CanonicalJson.ToString(result.Document["data"]!["ExtensionProperties"]!));
    }

    [Fact]
    public void A_value_inside_an_open_object_is_required_unless_it_says_otherwise_as_anywhere_in_the_record()
    {
        var mapping = Mapping("""
            ExtensionProperties:
              WellDB:
                Curves:
                  $forEach: curves
                  $item:
                    CurveID: { $from: curve_id }
                    OriginalUnit: { $from: curve_unit }
            """);

        var result = new MappingRenderer(mapping, Schema(), Cache(), Context()).Render(Log(("GR", "GAPI"), ("CALI", null)));
        Assert.Equal(["osdu.data.ExtensionProperties.WellDB.Curves[].OriginalUnit, in 1 of 2 items: dataset.curves.curve_unit is empty, and the entry is required"], result.Holds);
    }

    [Fact]
    public void The_preflight_refuses_a_value_written_over_an_open_object_and_anything_inside_an_object_that_is_not_open()
    {
        var whole = Assert.Single(Errors(Mapping("ExtensionProperties: { $from: project }")));
        Assert.Contains("is one value, and osdu.data.ExtensionProperties is an object the schema does not break into properties; fill the properties inside it instead", whole.Message, StringComparison.Ordinal);

        var literal = Mapping("ExtensionProperties: { $value: { WellDB: { Source: WELLDB } } }");
        Assert.Empty(Errors(literal));

        var sealedObject = Assert.Single(Errors(Mapping("Sealed:\n  Note: { $from: project }")));
        Assert.Contains("record.data.Sealed.Note fills a variable that template " + Kind, sealedObject.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void What_a_mapping_writes_inside_an_open_object_is_covered_under_it_each_holder_before_what_it_holds()
    {
        // WellDB itself has no entry: it is listed for what it holds, ahead of it, as every holder is.
        var report = MappingCoverage.Of(Mapping("""
            ExtensionProperties:
              WellDB:
                Curves:
                  $forEach: curves
                  $item:
                    CurveID: { $from: curve_id }
                    OriginalUnit:
                      $from: curve_unit
                      $required: false
                Source: WELLDB
            """), OsduTemplate.From(Schema()));

        var inside = report.Variables.SkipWhile(v => v.Target != "osdu.data.ExtensionProperties").ToList();
        Assert.Equal(
            [
                "osdu.data.ExtensionProperties",
                "osdu.data.ExtensionProperties.WellDB",
                "osdu.data.ExtensionProperties.WellDB.Curves",
                "osdu.data.ExtensionProperties.WellDB.Curves[].CurveID",
                "osdu.data.ExtensionProperties.WellDB.Curves[].OriginalUnit",
                "osdu.data.ExtensionProperties.WellDB.Source",
            ],
            inside.Select(v => v.Target).Where(t => t.StartsWith("osdu.data.ExtensionProperties", StringComparison.Ordinal)));

        var byTarget = inside.ToDictionary(v => v.Target);
        Assert.Equal(CoverageState.Always, byTarget["osdu.data.ExtensionProperties.WellDB.Source"].State);
        Assert.True(byTarget["osdu.data.ExtensionProperties.WellDB.Source"].Direct);
        Assert.Equal(CoverageState.Sometimes, byTarget["osdu.data.ExtensionProperties.WellDB.Curves[].OriginalUnit"].State);
        Assert.False(byTarget["osdu.data.ExtensionProperties.WellDB"].Direct);
        Assert.NotEqual(CoverageState.Empty, byTarget["osdu.data.ExtensionProperties"].State);
        Assert.DoesNotContain(report.Issues, issue => issue.Target?.StartsWith("osdu.data.ExtensionProperties", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void A_flow_that_carries_a_key_over_from_the_stored_record_refuses_a_mapping_that_writes_under_it()
    {
        var mapping = Mapping(Originals);

        // Keys the mapping leaves alone are carried over as before.
        RouteChecks.CheckPreserved(Flow(["Datasets", "DDMSDatasets"]), mapping, "logs.yaml");
        RouteChecks.CheckPreserved(Flow([]), mapping, "logs.yaml");

        var refused = Assert.Throws<FlowValidationException>(() => RouteChecks.CheckPreserved(Flow(["Datasets", "ExtensionProperties"]), mapping, "logs.yaml")).Message;
        Assert.StartsWith(
            "logs.yaml: target.protocolOptions.preserveDataKeys carries data.ExtensionProperties over from the record OSDU holds into every update, in place of what the mapping renders, "
            + "and Logged@1.0.0 writes osdu.data.ExtensionProperties.WellDB.Source, osdu.data.ExtensionProperties.WellDB.Project, osdu.data.ExtensionProperties.WellDB.Curves, ",
            refused,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "what it writes there would reach a record only when it is created. Take ExtensionProperties out of preserveDataKeys, or leave it out of the mapping.",
            refused,
            StringComparison.Ordinal);

        var ofInterface = Assert.Throws<FlowValidationException>(() => RouteChecks.CheckPreserved(Flow(["ExtensionProperties"], "logs"), mapping, "estate.yaml")).Message;
        Assert.Contains("target.protocolOptions.preserveDataKeys of interface 'logs' carries data.ExtensionProperties", ofInterface, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mapping_that_writes_a_preserved_key_whole_is_refused_too()
    {
        var mapping = Mapping("ExtensionProperties: { $value: { WellDB: { Source: WELLDB } } }");
        var refused = Assert.Throws<FlowValidationException>(() => RouteChecks.CheckPreserved(Flow(["ExtensionProperties"]), mapping, "logs.yaml")).Message;
        Assert.Contains("Logged@1.0.0 writes osdu.data.ExtensionProperties:", refused, StringComparison.Ordinal);
    }
}
