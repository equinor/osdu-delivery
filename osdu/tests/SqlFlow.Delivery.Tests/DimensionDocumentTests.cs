using SqlFlow.Core;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The dimension flow document (<c>flowType: dimension</c>): what it declares, and every rule the loader holds it to, each
/// refusal naming the place in the document it is about.
/// </summary>
public class DimensionDocumentTests
{
    private const string Head = """
        flowType: dimension
        name: wells-dimensions
        batch: reference
        partitions: [dev, test]
        source:
          endpoint: ${env:OSDU_URL}
          auth:
            type: bearer
            secretRef: ${env:OSDU_TOKEN}

        """;

    private const string Curves = """
        dimensions:
          - name: CurveMnemonic
            description: Every curve mnemonic the well logs hold.
            kind: "osdu:wks:work-product-component--WellLog:*"
            path: data.Curves.Mnemonic
        """;

    private static DimensionFlowDefinition Parse(string yaml) => new DeliveryDocumentLoader().ParseDimension(yaml, "flows/dims.yaml");

    private static FlowValidationException Refused(string yaml) => Assert.Throws<FlowValidationException>(() => Parse(yaml));

    /// <summary>
    /// The dimensions built at once are written <c>parallelism.recordsAtOnce</c>, as on a delivery flow; the name the block
    /// had before is refused naming the one it is now.
    /// </summary>
    [Fact]
    public void The_dimensions_at_once_are_written_recordsAtOnce_as_on_a_delivery_flow()
    {
        Assert.Equal(4, Parse(Head + "parallelism: { recordsAtOnce: 4 }\n" + Curves).Parallelism.RecordsAtOnce);
        Assert.Equal(8, Parse(Head + Curves).Parallelism.RecordsAtOnce);

        Assert.Contains(
            "flows/dims.yaml: parallelism.concurrency is now parallelism.recordsAtOnce, as on a delivery flow: the dimensions built at once, and the searches each asks at once. Rename it.",
            Refused(Head + "parallelism: { concurrency: 4 }\n" + Curves).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "flows/dims.yaml: reliability.concurrency is now parallelism.recordsAtOnce",
            Refused(Head + "reliability: { concurrency: 4 }\n" + Curves).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_declares_its_dimensions_with_their_paths_and_clean_steps()
    {
        var flow = Parse(Head + """
            parameters:
              country: { default: US }
            dimensions:
              - name: CurveMnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                query: 'data.Country:"{country}" AND data.Partition:"{partition}"'
                path: data.Curves.Mnemonic
                countRecords: true
                maxValues: 200000
                clean:
                  - trim
                  - collapseSpaces
                  - upper
                  - nfkc
                  - replace: { pattern: '^(\w+)_\d+$', with: '$1' }
                  - map: CurveAliases
                  - map: { dictionary: CurveFamilies, field: family, otherwise: ~ }
                  - map: { dictionary: CurveFamilies, otherwise: Other }
              - name: LegalTag
                kind: "*:*:*:*"
                path: legal.legaltags
                partitions: [dev]
            """);

        Assert.Equal("wells-dimensions", flow.Name);
        Assert.Equal(["dev", "test"], flow.Partitions);
        Assert.Equal(DimensionSource.DefaultAggregationSize, flow.Source.AggregationSize);
        Assert.Equal(DimensionSource.DefaultQueryPath, flow.Source.QueryPath);
        Assert.True(flow.ReadsDictionaries);

        var curves = flow.Dimensions[0];
        Assert.Equal("data.Curves.Mnemonic", curves.Path);
        Assert.True(curves.CountRecords);
        Assert.Equal(200_000, curves.MaxValues);
        Assert.Equal(
            [CleanStepKind.Trim, CleanStepKind.CollapseSpaces, CleanStepKind.Upper, CleanStepKind.Nfkc, CleanStepKind.Replace, CleanStepKind.Map, CleanStepKind.Map, CleanStepKind.Map],
            curves.Clean.Select(s => s.Kind));
        Assert.Equal((@"^(\w+)_\d+$", "$1"), (curves.Clean[4].Pattern, curves.Clean[4].With));
        Assert.Equal(("CurveAliases", null, MapOtherwise.Keep), (curves.Clean[5].Dictionary, curves.Clean[5].Field, curves.Clean[5].Otherwise));
        Assert.Equal(("CurveFamilies", "family", MapOtherwise.LeaveOut), (curves.Clean[6].Dictionary, curves.Clean[6].Field, curves.Clean[6].Otherwise));
        Assert.Equal((MapOtherwise.Text, "Other"), (curves.Clean[7].Otherwise, curves.Clean[7].OtherwiseText));
        Assert.Equal(16, curves.DefinitionHash.Length);

        var legal = flow.Dimensions[1];
        Assert.Equal(DimensionSpec.DefaultMaxValues, legal.MaxValues);
        Assert.Empty(legal.Clean);
        Assert.Equal(["dev"], legal.Partitions);
        Assert.True(legal.BuildsIn("dev"));
        Assert.False(legal.BuildsIn("test"));
    }

    [Fact]
    public void A_dimensions_hash_says_whether_what_it_declares_changed_and_nothing_else()
    {
        var first = Parse(Head + Curves).Dimensions[0].DefinitionHash;
        var relaidOut = Parse(Head + """
            dimensions:
              - path: data.Curves.Mnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                name: CurveMnemonic
                description: Every curve mnemonic the well logs hold.
            """).Dimensions[0].DefinitionHash;
        var cleaned = Parse(Head + Curves + "\n    clean: [upper]").Dimensions[0].DefinitionHash;

        Assert.Equal(first, relaidOut);
        Assert.NotEqual(first, cleaned);
    }

    [Fact]
    public void A_label_is_read_through_one_path_or_a_list_of_paths_and_changes_what_the_dimension_declares()
    {
        var flow = Parse(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
              - name: Country
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: [data.GeoContexts.GeoPoliticalEntityID, ' data.GeoPoliticalEntityName ']
            """);

        Assert.Equal(["data.FacilityName"], flow.Dimensions[0].Label);
        Assert.Equal(["data.GeoContexts.GeoPoliticalEntityID", "data.GeoPoliticalEntityName"], flow.Dimensions[1].Label);
        Assert.Empty(Parse(Head + Curves).Dimensions[0].Label);

        // Keys labelled another way are another dimension's values, so the label is part of what the hash says changed.
        Assert.NotEqual(Parse(Head + Curves).Dimensions[0].DefinitionHash, Parse(Head + Curves + "\n    label: data.Name").Dimensions[0].DefinitionHash);
    }

    [Fact]
    public void A_labelled_dimension_names_the_value_of_keys_without_a_label()
    {
        var flow = Parse(Head + Curves + "\n    label: data.Name\n    unlabelled: ' Not specified '");
        Assert.Equal("Not specified", flow.Dimensions[0].Unlabelled);
        Assert.Null(Parse(Head + Curves + "\n    label: data.Name").Dimensions[0].Unlabelled);
        Assert.Contains("reads neither", Refused(Head + Curves + "\n    unlabelled: Not specified").Message, StringComparison.Ordinal);
        Assert.Equal("Not specified", Parse(Head + Curves + "\n    attributes: { Family: data.Name }\n    unlabelled: Not specified").Dimensions[0].Unlabelled);
        Assert.Contains("unlabelled is empty", Refused(Head + Curves + "\n    label: data.Name\n    unlabelled: ' '").Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("label: 'data.Facility Name'", "label 'data.Facility Name' is not a property path")]
    [InlineData("label: [data.A, 'x y']", "label[1] 'x y' is not a property path")]
    [InlineData("label: [data.A, [data.B]]", "label[1] is not a path")]
    [InlineData("label: [data.A, data.B, data.C, data.D]", "at most 3")]
    [InlineData("label: { path: data.A }", "it is neither")]
    public void A_label_that_breaks_a_rule_is_refused_naming_the_rule(string label, string reason)
    {
        var refused = Refused(Head + Curves + "\n    " + label);
        Assert.Contains("dimensions[0] 'CurveMnemonic'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attributes_are_read_like_a_label_each_under_its_name_and_a_step_can_filter_the_objects_it_holds()
    {
        var flow = Parse(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
                attributes:
                  Country: ['data.GeoContexts[GeoTypeID*=Country].GeoPoliticalEntityID', data.GeoPoliticalEntityName]
                  SpudDate: data.SpudDate
            """);

        var wellbore = flow.Dimensions[0];
        Assert.Equal(["Country", "SpudDate"], wellbore.Attributes.Select(a => a.Name));
        Assert.Equal(["data.GeoContexts[GeoTypeID*=Country].GeoPoliticalEntityID", "data.GeoPoliticalEntityName"], wellbore.Attribute("country")!.Steps);
        Assert.Equal(["data.SpudDate"], wellbore.Attribute("SpudDate")!.Steps);

        // Another attribute is another declaration; a dimension that reads none keeps the hash it had before attributes existed.
        var plain = Parse(Head + Curves).Dimensions[0].DefinitionHash;
        Assert.NotEqual(plain, Parse(Head + Curves + "\n    attributes: { Family: data.Name }").Dimensions[0].DefinitionHash);
        Assert.Equal(plain, Parse(Head + Curves + "\n    attributes: {}").Dimensions[0].DefinitionHash);
    }

    [Theory]
    [InlineData("attributes: { 'not a name': data.A }", "is not an attribute name")]
    [InlineData("attributes: { Keys: data.A }", "takes a name a dimension's rows hold already")]
    [InlineData("attributes: { Partition: data.A }", "takes a name a dimension's rows hold already")]
    [InlineData("attributes: { Key_Id: data.A }", "takes a name a dimension's rows hold already")]
    [InlineData("attributes: { Area: data.A, area: data.B }", "is named twice")]
    [InlineData("attributes: { Area: [] }", "reads nothing")]
    [InlineData("attributes: { Area: 'data.A[B]' }", "is not [Property=text]")]
    [InlineData("attributes: { Area: 'data.A[B=' }", "has no closing ]")]
    [InlineData("attributes: { Area: 'data.A[=x]' }", "is not [Property=text]")]
    [InlineData("attributes: { Area: 'data..A' }", "is not a property name")]
    [InlineData("attributes: { Area: [data.A, data.B, data.C, data.D] }", "at most 3")]
    public void An_attribute_that_breaks_a_rule_is_refused_naming_the_rule(string attributes, string reason)
    {
        var refused = Refused(Head + Curves + "\n    " + attributes);
        Assert.Contains("dimensions[0] 'CurveMnemonic'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_attribute_is_collected_from_the_dimensions_own_records_and_a_dimension_collects_one()
    {
        var flow = Parse(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                attributes:
                  UUID: data.FacilityID
                  Source: { collect: ' data.Source ' }
            """);

        var source = flow.Dimensions[0].Attribute("source")!;
        Assert.Equal(("Source", "data.Source", true), (source.Name, source.Collect, source.IsCollected));
        Assert.Empty(source.Steps);
        Assert.False(flow.Dimensions[0].Attribute("UUID")!.IsCollected);

        // Collecting is another declaration than reading the same path from the record a key names.
        Assert.NotEqual(
            Parse(Head + Curves + "\n    attributes: { Source: data.Source }").Dimensions[0].DefinitionHash,
            Parse(Head + Curves + "\n    attributes: { Source: { collect: data.Source } }").Dimensions[0].DefinitionHash);
    }

    [Theory]
    [InlineData("attributes: { Source: { collect: data.Source }, Type: { collect: data.LogType } }", "is a second collected attribute beside Source")]
    [InlineData("attributes: { Source: { collected: data.Source } }", "it names collected")]
    [InlineData("attributes: { Source: { collect: data.Source, from: logs } }", "it names from")]
    [InlineData("attributes: { Source: { collect: data.Source, path: data.Source } }", "it names both")]
    [InlineData("attributes: { Source: { keep: key } }", "it names neither")]
    [InlineData("attributes: { Source: { collect: '' } }", "collect is the path of the dimension's own records")]
    [InlineData("attributes: { Source: { collect: [data.Source] } }", "collect is the path of the dimension's own records")]
    [InlineData("attributes: { Source: { collect: 'data.Log Source' } }", "is not a property path")]
    [InlineData("attributes: { Source: { path: 'data.Log Source' } }", "is not a property path")]
    [InlineData("attributes: { Source: { path: [] } }", "path reads nothing")]
    [InlineData("attributes: { Source: { path: data.Source, keep: ref } }", "keep is 'ref'")]
    public void A_collected_attribute_that_breaks_a_rule_is_refused_naming_the_rule(string attributes, string reason)
    {
        var refused = Refused(Head + Curves + "\n    " + attributes);
        Assert.Contains("dimensions[0] 'CurveMnemonic'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_attribute_keeps_the_key_so_its_column_joins_to_the_dimension_keyed_by_it()
    {
        var flow = Parse(Head + """
            dimensions:
              - name: WellLog
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: id
                columns: { key: WellLogID, value: WellLogName }
                attributes:
                  WellboreID: { path: ' data.WellboreID ', keep: key }
                  Wellbore: { path: [data.WellboreID, data.FacilityName] }
                  CompanyID: { path: data.ServiceCompanyID, keep: id }
                  CurveType: { collect: data.Curves.LogCurveTypeID, keep: key }
            """);

        var join = flow.Dimensions[0].Attribute("wellboreid")!;
        Assert.Equal(DimensionValueKeep.Key, join.Keep);
        Assert.Equal(["data.WellboreID"], join.Steps);
        Assert.Equal(DimensionValueKeep.Id, flow.Dimensions[0].Attribute("CompanyID")!.Keep);

        // path with no keep reads exactly as the bare form does, and keeps the value as a value shows it.
        var read = flow.Dimensions[0].Attribute("Wellbore")!;
        Assert.Equal(DimensionValueKeep.Value, read.Keep);
        Assert.Equal(["data.WellboreID", "data.FacilityName"], read.Steps);
        Assert.Equal(
            Parse(Head + Curves + "\n    attributes: { W: [data.WellboreID, data.FacilityName] }").Dimensions[0].Attribute("W")!.Steps,
            read.Steps);

        // A collected attribute keeps the key too.
        Assert.True(flow.Dimensions[0].Attribute("CurveType")!.IsCollected);
        Assert.Equal(DimensionValueKeep.Key, flow.Dimensions[0].Attribute("CurveType")!.Keep);

        // Keeping the key is another declaration than showing the value, so a build knows the dimension changed.
        Assert.NotEqual(
            Parse(Head + Curves + "\n    attributes: { Source: data.Source }").Dimensions[0].DefinitionHash,
            Parse(Head + Curves + "\n    attributes: { Source: { path: data.Source, keep: key } }").Dimensions[0].DefinitionHash);
        Assert.Equal(
            Parse(Head + Curves + "\n    attributes: { Source: data.Source }").Dimensions[0].DefinitionHash,
            Parse(Head + Curves + "\n    attributes: { Source: { path: data.Source, keep: value } }").Dimensions[0].DefinitionHash);
    }

    [Fact]
    public void A_dimension_reads_the_objects_of_a_nested_array_as_rows_with_their_fields_together()
    {
        var flow = Parse(Head + """
            dimensions:
              - name: LogCurve
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: id
                columns: { key: WellLogID, value: WellLogName }
                label: data.Name
                attributes:
                  WellboreID: { path: data.WellboreID, keep: id }
                elements:
                  path: data.Curves
                  fields:
                    Mnemonic: Mnemonic
                    CurveUnitID: { path: CurveUnit, keep: id }
                    TopDepth: ' TopDepth '
            """);

        var elements = flow.Dimensions[0].Elements!;
        Assert.Equal("data.Curves", elements.Path);
        Assert.Equal(
            [("Mnemonic", "Mnemonic", DimensionValueKeep.Value), ("CurveUnitID", "CurveUnit", DimensionValueKeep.Id), ("TopDepth", "TopDepth", DimensionValueKeep.Value)],
            elements.Fields.Select(f => (f.Name, f.Path, f.Keep)));
        Assert.Equal(["data.Curves.Mnemonic", "data.Curves.CurveUnit", "data.Curves.TopDepth"], elements.ReturnedFields());

        // The table has the attributes, then the fields, and numbers each key's objects.
        var table = DimensionTables.Of("LogCurve", "WellLogID", "WellLogName", flow.Dimensions[0].Attributes, elements);
        Assert.True(table.HasElements);
        Assert.Equal([("WellboreID", false), ("Mnemonic", true), ("CurveUnitID", true), ("TopDepth", true)], table.Columns.Select(c => (c.Name, c.Element)));

        // Reading elements is part of the declaration, so a build knows the dimension changed.
        Assert.NotEqual(
            Parse(Head + Curves).Dimensions[0].DefinitionHash,
            Parse(Head + Curves + "\n    elements: { path: data.Curves, fields: { Unit: CurveUnit } }").Dimensions[0].DefinitionHash);
    }

    [Theory]
    [InlineData("elements: { fields: { Unit: CurveUnit } }", "elements.path")]
    [InlineData("elements: { path: 'data.Cur ves', fields: { Unit: CurveUnit } }", "is not a path the elements can be read through")]
    [InlineData("elements: { path: 'data.Curves[Type]', fields: { Unit: CurveUnit } }", "is not a path the elements can be read through")]
    [InlineData("elements: { path: data.Curves }", "elements.fields names no field")]
    [InlineData("elements: { path: data.Curves, fields: { 1Unit: CurveUnit } }", "is not a field name")]
    [InlineData("elements: { path: data.Curves, fields: { element: CurveUnit } }", "takes a name a dimension's rows hold already")]
    [InlineData("elements: { path: data.Curves, fields: { Unit: CurveUnit, unit: DepthUnit } }", "is named as another field or an attribute is")]
    [InlineData("attributes: { Unit: data.A }\n    elements: { path: data.Curves, fields: { Unit: CurveUnit } }", "is named as another field or an attribute is")]
    [InlineData("elements: { path: data.Curves, fields: { Unit: 'Curve Unit' } }", "is not a path inside the element")]
    [InlineData("elements: { path: data.Curves, fields: { Unit: { path: CurveUnit, up: 3 } } }", "up is 3, and elements.path passes 2 object(s)")]
    [InlineData("elements: { path: data.Curves, fields: { Unit: { path: CurveUnit, up: -1 } } }", "up is '-1'")]
    [InlineData("elements: { path: data.Curves, fields: { Unit: { path: '@', up: 1 } } }", "reads @, the element itself")]
    [InlineData("elements: { path: data.Curves, fields: { Unit: { path: CurveUnit, many: all } } }", "many is 'all'")]
    [InlineData("elements: { path: data.Curves, fields: { Unit: { keep: id } } }", "it names no path")]
    [InlineData("elements: { path: data.Curves, fields: { Unit: { path: CurveUnit, keep: ref } } }", "keep is 'ref'")]
    [InlineData("elements: { path: data.Curves, fields: { Unit: [CurveUnit] } }", "is the path inside each element")]
    [InlineData("attributes: { Source: { collect: data.Source } }\n    elements: { path: data.Curves, fields: { Unit: CurveUnit } }", "would each make a row of the table per value")]
    [InlineData("columns: { key: element }\n    elements: { path: data.Curves, fields: { Unit: CurveUnit } }", "would be the element column")]
    public void Elements_that_break_a_rule_are_refused_naming_the_rule(string declared, string reason)
    {
        var refused = Refused(Head + Curves + "\n    " + declared);
        Assert.Contains("dimensions[0] 'CurveMnemonic'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Attributes_and_element_fields_together_hold_no_more_columns_than_a_table_lays_out()
    {
        var attributes = string.Join(", ", Enumerable.Range(0, 15).Select(i => $"A{i}: data.A{i}"));
        var fields = string.Join(", ", Enumerable.Range(0, 6).Select(i => $"F{i}: F{i}"));
        var refused = Refused(Head + Curves + "\n    attributes: { " + attributes + " }\n    elements: { path: data.Curves, fields: { " + fields + " } }");
        Assert.Contains("attributes and elements.fields name 21 columns", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Elements_read_through_filters_and_nested_arrays_ask_the_search_for_exactly_what_they_read()
    {
        var flow = Parse(Head + """
            dimensions:
              - name: LogCurveColumn
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: id
                columns: { key: WellLogID, value: WellLogName }
                elements:
                  path: 'data.Curves[CurveType*=Array].Columns'
                  fields:
                    Column: '@'
                    Mnemonic: { path: Mnemonic, up: 1 }
                    LogName: { path: data.Name, up: 3 }
                    Aliases: { path: 'Aliases[Kind=Short].Text', up: 1, many: join }
            """);

        var elements = flow.Dimensions[0].Elements!;
        Assert.Equal(
            [("Column", "@", 0, DimensionElementMany.First), ("Mnemonic", "Mnemonic", 1, DimensionElementMany.First),
             ("LogName", "data.Name", 3, DimensionElementMany.First), ("Aliases", "Aliases[Kind=Short].Text", 1, DimensionElementMany.Join)],
            elements.Fields.Select(f => (f.Name, f.Path, f.Up, f.Many)));

        // The filters' properties come back with what each field reads; the arrays themselves are never asked for whole
        // except where the element itself is the value.
        Assert.Equal(
            ["data.Curves.CurveType", "data.Curves.Columns", "data.Curves.Mnemonic", "data.Name", "data.Curves.Aliases.Text", "data.Curves.Aliases.Kind"],
            elements.ReturnedFields());
    }

    [Fact]
    public void More_attributes_than_a_dimension_reads_are_refused()
    {
        var many = string.Join(", ", Enumerable.Range(0, DimensionSpec.MaxAttributes + 1).Select(i => $"A{i}: data.A{i}"));
        Assert.Contains("at most 20", Refused(Head + Curves + "\n    attributes: { " + many + " }").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tables_key_and_value_columns_are_named_after_what_the_dimension_reads_unless_the_document_names_them()
    {
        var flow = Parse(Head + """
            dimensions:
              - name: Wellbore
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: data.FacilityName
              - name: Country
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.WellboreID
                label: [data.GeoContexts.GeoPoliticalEntityID, 'data[GeoPoliticalEntityTypeID*=Country].GeoPoliticalEntityName']
              - name: Curve.Mnemonic
                kind: "osdu:wks:work-product-component--WellLog:*"
                path: data.Curves.Mnemonic
              - name: LegalTag
                kind: "*:*:*:*"
                path: legal.legaltags
                columns: { key: Tag, value: ' TagName ' }
              - name: Record
                kind: "*:*:*:*"
                path: id
                columns: { key: RecordId }
              - name: Source
                kind: "*:*:*:*"
                path: data.Source
              - name: Version
                kind: "*:*:*:*"
                path: version
              - name: Name
                kind: "*:*:*:*"
                path: data.Parent.Name
                label: data.Name
              - name: Unit
                kind: "*:*:*:*"
                path: data.Symbol
                columns: { value: symbol }
            """);

        // The key's column is the property the path ends with; the value's the property the label ends with, its filter
        // aside, or with no label the dimension's own name, made a column name.
        Assert.Equal(("WellboreID", "FacilityName"), (flow.Dimensions[0].KeyColumn, flow.Dimensions[0].ValueColumn));
        Assert.Equal(("WellboreID", "GeoPoliticalEntityName"), (flow.Dimensions[1].KeyColumn, flow.Dimensions[1].ValueColumn));
        Assert.Equal(("Mnemonic", "Curve_Mnemonic"), (flow.Dimensions[2].KeyColumn, flow.Dimensions[2].ValueColumn));
        Assert.Equal(("Tag", "TagName"), (flow.Dimensions[3].KeyColumn, flow.Dimensions[3].ValueColumn));
        Assert.Equal(("RecordId", "Record"), (flow.Dimensions[4].KeyColumn, flow.Dimensions[4].ValueColumn));

        // Where the value's column has the name the path gives the key's, the value keeps it, being the one a person
        // reads, and the key's takes Key at its end: a dimension named after the property it reads needs no name given.
        Assert.Equal(("SourceKey", "Source"), (flow.Dimensions[5].KeyColumn, flow.Dimensions[5].ValueColumn));
        Assert.Equal(("versionKey", "Version"), (flow.Dimensions[6].KeyColumn, flow.Dimensions[6].ValueColumn));
        Assert.Equal(("NameKey", "Name"), (flow.Dimensions[7].KeyColumn, flow.Dimensions[7].ValueColumn));
        Assert.Equal(("SymbolKey", "symbol"), (flow.Dimensions[8].KeyColumn, flow.Dimensions[8].ValueColumn));

        // A name the document does not give, or gives as the path and the label would, is no part of what it declares,
        // so a dimension declared before columns had names keeps its hash; another name is another declaration.
        var plain = Parse(Head + Curves).Dimensions[0].DefinitionHash;
        Assert.Equal(plain, Parse(Head + Curves + "\n    columns: { key: Mnemonic, value: CurveMnemonic }").Dimensions[0].DefinitionHash);
        Assert.Equal(plain, Parse(Head + Curves + "\n    columns: {}").Dimensions[0].DefinitionHash);
        Assert.NotEqual(plain, Parse(Head + Curves + "\n    columns: { value: Curve }").Dimensions[0].DefinitionHash);
        Assert.NotEqual(plain, Parse(Head + Curves + "\n    columns: { key: Spelling }").Dimensions[0].DefinitionHash);
        const string Source = "dimensions:\n  - name: Source\n    kind: '*:*:*:*'\n    path: data.Source";
        Assert.Equal(Parse(Head + Source).Dimensions[0].DefinitionHash, Parse(Head + Source + "\n    columns: { key: SourceKey, value: Source }").Dimensions[0].DefinitionHash);
    }

    [Theory]
    [InlineData("name: Source\n    kind: '*:*:*:*'\n    path: kind\n    columns: { key: source }", "columns.key 'source' is the name of the value's column (named after the dimension (it reads no label)), and a table has one column of a name. Give the key's column another name, or leave it out for the name the path gives.")]
    [InlineData("name: D\n    kind: '*:*:*:*'\n    path: kind\n    columns: { key: Same, value: same }", "columns.key 'Same' is the name of the value's column (columns.value)")]
    [InlineData("name: Source\n    kind: '*:*:*:*'\n    path: data.Source\n    attributes: { SourceKey: data.Other }", "the key's column would be named 'SourceKey', after the property its path ends with and the Key it takes beside a value's column of that name, which is the name of its attribute SourceKey")]
    [InlineData("name: D\n    kind: '*:*:*:*'\n    path: id", "the key's column would be named 'id', after the property its path ends with, which is a column every dimension's table has already (id, partition, key_id, records, filter). Name it in the document: columns: { key: <name> }.")]
    [InlineData("name: D\n    kind: '*:*:*:*'\n    path: kind\n    columns: { key: Records }", "columns.key 'Records' is a column every dimension's table has already")]
    [InlineData("name: D\n    kind: '*:*:*:*'\n    path: kind\n    columns: { value: 'not a name' }", "columns.value 'not a name' is not a column name (a letter, then letters, digits and underscores, at most 64). Give it another name.")]
    [InlineData("name: 3D\n    kind: '*:*:*:*'\n    path: kind", "the value's column would be named '3D', after the dimension (it reads no label), which is not a column name")]
    [InlineData("name: D\n    kind: '*:*:*:*'\n    path: kind\n    columns: { key: Value }", "columns.key 'Value' is the word the value's column is asked for by, whatever it is named")]
    [InlineData("name: D\n    kind: '*:*:*:*'\n    path: kind\n    columns: { value: key }", "columns.value 'key' is the word the key's column is asked for by, whatever it is named")]
    [InlineData("name: D\n    kind: '*:*:*:*'\n    path: kind\n    columns: { key: '  ' }", "columns.key is empty")]
    [InlineData("name: D\n    kind: '*:*:*:*'\n    path: data.WellboreID\n    label: data.Name\n    attributes: { name: data.Other }", "the value's column would be named 'Name', after the property its label ends with, which is the name of its attribute name, and a table has one column of a name. Name it in the document: columns: { value: <name> }, or rename the attribute.")]
    [InlineData("name: D\n    kind: '*:*:*:*'\n    path: kind\n    columns: { key: Family }\n    attributes: { family: data.Other }", "columns.key 'Family' is the name of its attribute family, and a table has one column of a name. Give it another name, or rename the attribute.")]
    [InlineData("name: D\n    kind: '*:*:*:*'\n    path: kind\n    columns: { key: Kind, colour: red }", "colour")]
    public void A_column_name_that_cannot_name_a_column_is_refused_saying_how_to_name_it(string dimension, string reason)
    {
        var refused = Refused(Head + "dimensions:\n  - " + dimension);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_key_and_value_columns_may_keep_the_names_tables_had_before_dimensions_named_them()
    {
        var kept = Parse(Head + Curves + "\n    columns: { key: key, value: value }").Dimensions[0];
        Assert.Equal((DimensionColumnNames.KeyRole, DimensionColumnNames.ValueRole), (kept.KeyColumn, kept.ValueColumn));
    }

    [Theory]
    [InlineData("data.WellboreID", "data.FacilityName", "Wellbore", "Country", "WellboreID", "FacilityName")]
    [InlineData("data.WellboreID", null, "Wellbore", "Country", "WellboreID", "Wellbore")]
    [InlineData("data.Source", null, "Source", "Country", "SourceKey", "Source")]
    [InlineData("data.Source", null, "Source", "sourcekey", "key", "value")]
    [InlineData("data.WellboreID", "data.Country", "Wellbore", "Country", "key", "value")]
    [InlineData("id", null, "Record", "Country", "key", "value")]
    public void A_dimension_no_document_named_the_columns_of_takes_the_names_it_reads_by_when_they_can_name_columns(
        string path, string? label, string dimension, string attribute, string key, string value)
        => Assert.Equal((key, value), DimensionColumnNames.Settled(path, label is null ? [] : [label], dimension, [attribute]));

    [Theory]
    [InlineData("kind", "osdu:wks:master-data--Wellbore:*")]
    [InlineData("acl.viewers", "*:*:*:*")]
    [InlineData("legal.status", "*:*:*:*")]
    [InlineData("tags.Source", "*:*:*:*")]
    [InlineData("createTime", "*:*:*:*")]
    [InlineData("data.Anything.Here", "*:*:*:*")]
    public void Any_property_search_can_match_exactly_can_be_a_dimension(string path, string kind)
        => Assert.Equal(path, Parse(Head + $"""
            dimensions:
              - name: D
                kind: "{kind}"
                path: {path}
            """).Dimensions[0].Path);

    [Theory]
    [InlineData("meta", "not a property the index holds")]
    [InlineData("acl", "under acl it holds")]
    [InlineData("tags", "name one tag")]
    [InlineData("data.Curves[*].Mnemonic", "not a property path")]
    [InlineData("x-acl", "not a property path")]
    public void A_path_search_holds_no_exact_value_at_is_refused_where_it_is_written(string path, string reason)
    {
        var refused = Refused(Head + $"""
            dimensions:
              - name: D
                kind: "*:*:*:*"
                path: {path}
            """);
        Assert.Contains("dimensions[0] 'D'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dimensions: []", "at least one dimension")]
    [InlineData("dimensions:\n  - name: 'not a name'\n    kind: 'a:b:c:1.0.0'\n    path: kind", "is not a dimension name")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'not-a-kind'\n    path: kind", "is not authority:source:entityType:version")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'", "path")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n    query: 'data.X:\"{undeclared}\"'", "'{undeclared}'")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n  - name: d\n    kind: 'a:b:c:1.0.0'\n    path: type", "as an earlier dimension is")]
    [InlineData("dimensions:\n  - name: Well.Type\n    kind: 'a:b:c:1.0.0'\n    path: kind\n  - name: Well-Type\n    kind: 'a:b:c:1.0.0'\n    path: type", "would share its table with 'Well.Type'")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n    maxValues: 0", "maxValues is 0")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n    maxValues: 5000001", "maxValues is 5000001")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n    partitions: [prod]", "'prod' is not a partition of the flow")]
    [InlineData("dimensions:\n  - name: D\n    kind: 'a:b:c:1.0.0'\n    path: kind\n    colour: red", "colour")]
    public void A_dimension_that_breaks_a_rule_is_refused_naming_the_rule(string dimensions, string reason)
        => Assert.Contains(reason, Refused(Head + dimensions).Message, StringComparison.Ordinal);

    [Fact]
    public void More_dimensions_than_one_flow_holds_are_refused()
    {
        var many = string.Join("\n", Enumerable.Range(0, DimensionFlowDefinition.MaxDimensions + 1).Select(i => $"  - {{ name: D{i}, kind: 'a:b:c:1.0.0', path: kind }}"));
        Assert.Contains("at most 100", Refused(Head + "dimensions:\n" + many).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("- sparkle", "'sparkle' is not a clean step")]
    [InlineData("- replace", "replace takes settings")]
    [InlineData("- map", "map takes settings")]
    [InlineData("- { trim: ~ }", "trim takes no settings")]
    [InlineData("- { upper: ~, lower: ~ }", "holds 2 keys")]
    [InlineData("- replace: { with: x }", "replace needs a pattern")]
    [InlineData("- replace: { pattern: a }", "replace needs with")]
    [InlineData("- replace: { pattern: a, with: b, flags: i }", "no 'flags' setting")]
    [InlineData("- replace: { pattern: '(a)\\1', with: b }", "non-backtracking")]
    [InlineData("- replace: { pattern: '(unclosed', with: b }", "cannot be used")]
    [InlineData("- map: { field: family }", "map needs a dictionary")]
    [InlineData("- map: '../Other'", "map needs a dictionary")]
    [InlineData("- map: { dictionary: Aliases, field: 'not a field' }", "is not a dictionary field name")]
    [InlineData("- map: { dictionary: Aliases, otherwise: '' }", "otherwise is empty text")]
    [InlineData("- map: { dictionary: Aliases, colour: red }", "no 'colour' setting")]
    [InlineData("- map: [Aliases]", "map takes a dictionary's name")]
    [InlineData("- ~", "is empty")]
    public void A_clean_step_that_breaks_a_rule_is_refused_naming_the_step(string step, string reason)
    {
        var refused = Refused(Head + Curves + "\n    clean:\n      " + step);
        Assert.Contains("clean[0]", refused.Message, StringComparison.Ordinal);
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_clean_steps_than_a_dimension_applies_are_refused()
        => Assert.Contains(
            "at most 20",
            Refused(Head + Curves + "\n    clean: [" + string.Join(", ", Enumerable.Repeat("trim", DimensionSpec.MaxCleanSteps + 1)) + "]").Message,
            StringComparison.Ordinal);

    [Theory]
    [InlineData(9)]
    [InlineData(10_001)]
    public void An_aggregation_size_no_platform_uses_is_refused(int size)
        => Assert.Contains("AGGREGATION_SIZE", Refused(Head.Replace("source:", $"source:\n  aggregationSize: {size}", StringComparison.Ordinal) + Curves).Message, StringComparison.Ordinal);

    [Fact]
    public void An_aggregation_size_the_platform_was_set_to_is_taken()
        => Assert.Equal(500, Parse(Head.Replace("source:", "source:\n  aggregationSize: 500", StringComparison.Ordinal) + Curves).Source.AggregationSize);

    [Fact]
    public void A_flow_naming_its_partitions_and_a_partition_header_is_refused()
        => Assert.Contains(
            "Remove the header",
            Refused(Head.Replace("secretRef: ${env:OSDU_TOKEN}", "secretRef: ${env:OSDU_TOKEN}\n  headers:\n    data-partition-id: dev", StringComparison.Ordinal) + Curves).Message,
            StringComparison.Ordinal);

    [Fact]
    public void A_parameter_named_partition_is_refused_because_the_run_names_the_partition()
        => Assert.Contains("parameters declares 'partition'", Refused(Head + "parameters:\n  partition: { default: x }\n" + Curves).Message, StringComparison.Ordinal);

    [Fact]
    public void An_unknown_key_is_refused_rather_than_ignored()
        => Assert.Contains("sourcee", Refused(Head + Curves + "\nsourcee: {}").Message, StringComparison.Ordinal);

    [Fact]
    public void A_run_builds_in_the_partition_it_names_and_one_partition_only()
    {
        var flow = Parse(Head + Curves);
        var registry = RegisteredPartitions.None;

        Assert.Equal("test", flow.ForRun("test", registry).Partition);
        Assert.Equal("dev", flow.ForRun("test", registry).ForPartition("dev").Partition);
        Assert.Contains("one partition per run", Assert.Throws<DeliveryException>(() => flow.ForRun(PartitionNames.Every, registry)).Message, StringComparison.Ordinal);
        Assert.Contains("does not build in partition 'prod'", Assert.Throws<DeliveryException>(() => flow.ForRun("prod", registry)).Message, StringComparison.Ordinal);
        Assert.NotEqual(flow.ForPartition("dev").LedgerId, flow.ForPartition("test").LedgerId);
        Assert.Equal("wells-dimensions@dev", flow.ForPartition("dev").LedgerName);
        Assert.Equal("dev", flow.ForPartition("dev").Source.Headers["data-partition-id"]);
    }

    [Fact]
    public void A_run_selects_dimensions_by_name_and_refuses_one_the_flow_does_not_have()
    {
        var flow = Parse(Head + Curves);
        Assert.Equal(["CurveMnemonic"], flow.Select(["curvemnemonic"]).Select(d => d.Name));
        Assert.Single(flow.Select([]));
        Assert.Contains("'Nope'", Assert.Throws<DeliveryException>(() => flow.Select(["Nope"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_payload_carries_the_dimensions_a_run_builds_and_no_other_kind_takes_them()
    {
        var payload = DeliveryRunPayload.Parse("""{ "dimensions": ["CurveMnemonic", "LegalTag"] }""");
        Assert.Equal(["CurveMnemonic", "LegalTag"], payload.Dimensions);
        Assert.Equal("""{"dimensions":["CurveMnemonic","LegalTag"]}""", payload.ToJson());
        Assert.False(payload.CarriesOnlyConfiguration);

        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "dimensions": ["not a name"] }"""));
        Assert.Throws<SqlFlowException>(() => DeliveryRunPayload.Parse("""{ "dimensions": ["A", "a"] }"""));
        Assert.Contains("only a dimension flow", Assert.Throws<SqlFlowException>(() => payload.Validate(DeliveryOperations.Deliver)).Message, StringComparison.Ordinal);

        var kind = new DimensionFlowKind(new DeliveryDocumentLoader());
        kind.ValidateParameters(new RunParameters { Payload = payload.ToJson() });
        Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { Payload = """{ "tests": ["t"] }""" }));
        Assert.Throws<SqlFlowException>(() => new AssertionFlowKind(new DeliveryDocumentLoader()).ValidateParameters(new RunParameters { Payload = payload.ToJson() }));
    }

    [Theory]
    [InlineData(null, "build")]
    [InlineData("build", "build")]
    [InlineData("plan", "plan")]
    public void A_dimension_flow_builds_by_default_and_plans_when_asked(string? operation, string expected)
        => Assert.Equal(expected, DimensionFlowKind.Operation(new RunParameters { Operation = operation }));

    [Fact]
    public void A_dimension_flow_runs_no_other_operation()
        => Assert.Throws<SqlFlowException>(() => DimensionFlowKind.Operation(new RunParameters { Operation = "deliver" }));

    [Fact]
    public void Lineage_reads_each_dimensions_kind_and_writes_each_dimension_in_each_partition()
    {
        var flow = Parse(Head + Curves);

        var lineage = DimensionLineage.Describe(flow);

        var reads = lineage.Datasets.Where(d => d.Relation == LineageRelation.Reads).Select(d => $"{d.System}/{d.Namespace}/{d.Name}").ToList();
        var writes = lineage.Datasets.Where(d => d.Relation == LineageRelation.Writes).Select(d => $"{d.System}/{d.Namespace}/{d.Group}/{d.Name}").ToList();
        Assert.Equal(
            ["osdu-type/dev/osdu:wks:work-product-component--WellLog:*", "osdu-type/test/osdu:wks:work-product-component--WellLog:*"],
            reads);
        Assert.Equal(["osdu-dimension/dev/wells-dimensions/CurveMnemonic", "osdu-dimension/test/wells-dimensions/CurveMnemonic"], writes);
        Assert.Empty(lineage.Warnings);
    }

    [Fact]
    public void The_flow_needs_the_repositorys_tree_only_when_it_cleans_through_a_dictionary()
    {
        Assert.False(new DimensionFlowDocument { Flow = Parse(Head + Curves) }.RequiresRepoTree);
        Assert.True(new DimensionFlowDocument { Flow = Parse(Head + Curves + "\n    clean: [{ map: Aliases }]") }.RequiresRepoTree);
    }

    [Fact]
    public void A_flow_naming_no_target_connection_writes_on_no_server()
    {
        var document = new DimensionFlowDocument { Flow = Parse(Head + Curves) };

        Assert.Null(document.TargetConnectionReference);
        Assert.Equal(SqlFlow.Lineage.Collection.ServerIdentity.FileSystem, Assert.Single(SqlFlow.Lineage.Collection.FlowDocumentHeaders.Project(document)).TargetServerRef);
    }

    [Fact]
    public void Its_secrets_are_references_the_hygiene_check_inspects()
    {
        var references = new DimensionFlowDocument { Flow = Parse(Head + Curves) }.CredentialReferences.ToList();
        Assert.Contains(new KeyValuePair<string, string>("source.endpoint", "${env:OSDU_URL}"), references);
        Assert.Contains(new KeyValuePair<string, string>("source.auth.secretRef", "${env:OSDU_TOKEN}"), references);
    }

    [Fact]
    public void An_incremental_block_names_the_unique_key_and_takes_modifyTime_then_createTime_as_when_a_record_changed()
    {
        Assert.Null(Parse(Head + Curves).Incremental);

        var flow = Parse(Head + """
            incremental:
              keyColumns: [id]
              fullLoadAfterHours: 168

            """ + Curves);

        var incremental = flow.Incremental!;
        Assert.Equal(["id"], incremental.KeyColumns);
        Assert.Equal(["modifyTime", "createTime"], incremental.DateColumns);
        Assert.Equal((5, (int?)168, false, "id"), (incremental.LagMinutes, incremental.FullLoadAfterHours, incremental.FullLoad, incremental.RecordKey));

        var named = Parse(Head + """
            incremental:
              keyColumns: [id, data.Version]
              dateColumns: [data.UpdatedDate]
              lagMinutes: 0
              fullLoad: true

            """ + Curves).Incremental!;
        Assert.Equal(["data.UpdatedDate"], named.DateColumns);
        Assert.Equal((0, true, "id,data.Version"), (named.LagMinutes, named.FullLoad, named.RecordKey));

        // The declaration a dimension is built with is its own: a flow's load is not part of it.
        Assert.Equal(Parse(Head + Curves).Dimensions[0].DefinitionHash, flow.Dimensions[0].DefinitionHash);
    }

    [Theory]
    [InlineData("incremental: { lagMinutes: 5 }", "incremental names no keyColumns")]
    [InlineData("incremental: { keyColumns: [] }", "incremental.keyColumns names 0 properties")]
    [InlineData("incremental: { keyColumns: [id, id] }", "incremental.keyColumns names id twice")]
    [InlineData("incremental: { keyColumns: [a, b, c, d] }", "incremental.keyColumns names 4 properties")]
    [InlineData("incremental: { keyColumns: ['data.Not a path'] }", "incremental.keyColumns[0]: path 'data.Not a path' is not a property path")]
    [InlineData("incremental: { keyColumns: [id], dateColumns: [modifyTime, modifyTime] }", "incremental.dateColumns names modifyTime twice")]
    [InlineData("incremental: { keyColumns: [id], lagMinutes: -1 }", "incremental.lagMinutes is -1")]
    [InlineData("incremental: { keyColumns: [id], fullLoadAfterHours: 0 }", "incremental.fullLoadAfterHours is 0")]
    [InlineData("incremental: { keyColumns: [id], since: 2026-01-01 }", "since")]
    public void An_incremental_block_is_refused_where_it_cannot_be_read(string block, string expected)
        => Assert.Contains(expected, Refused(Head + block + "\n" + Curves).Message, StringComparison.Ordinal);

    [Fact]
    public void A_build_takes_SQLFlow_s_full_load_and_backfill_window_and_still_refuses_what_a_file_flow_takes()
    {
        var kind = new DimensionFlowKind(new DeliveryDocumentLoader());
        kind.ValidateParameters(new RunParameters { FullLoad = true });
        kind.ValidateParameters(new RunParameters { BackfillFrom = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), BackfillTo = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc) });
        var refused = Assert.Throws<SqlFlowException>(() => kind.ValidateParameters(new RunParameters { FilePattern = "*.csv" }));
        Assert.StartsWith("filePattern do(es) not apply to 'dimension' flows; a build loads what the flow's incremental block says", refused.Message, StringComparison.Ordinal);
    }
}
